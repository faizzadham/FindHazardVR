using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Gesture brackets (talk intro/outro) coverage for <see cref="TalkLayer" />:
    ///     a fresh Speaking-enter plays an authored Intro Clip before handing off to the main
    ///     loop, release plays an authored Outro Clip capped at
    ///     <see cref="ConvaiBodyAnimationConfig.TalkOutroMaxSeconds" />, re-entering Speaking
    ///     mid-outro cuts back to the loop without a wedge, and an interruption mid-bracket
    ///     freezes/fast-settles exactly like an interruption mid-loop.
    /// </summary>
    public sealed class TalkLayerGestureBracketTests
    {
        private const float FadeIn = 0.05f;
        private const float FadeOut = 1f; // deliberately long so the outro cap is observable
        private const float ChainFade = 0.05f;
        private const float VariantCrossfade = 0.1f;
        private const float OutroMax = 0.3f;
        private const float InterruptedFreeze = 0.15f;
        private const float InterruptedReleaseScale = 0.5f;

        private readonly List<Object> _cleanup = new();
        private PlayableGraph _graph;
        private TalkLayer _layer;
        private bool _initialized;

        [TearDown]
        public void TearDown()
        {
            if (_initialized) _layer.Teardown();
            if (_graph.IsValid()) _graph.Destroy();
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
            _initialized = false;
        }

        private AnimationClip Clip(string name, float length)
        {
            var clip = new AnimationClip { name = name };
            clip.SetCurve("", typeof(Transform), "localPosition.x",
                AnimationCurve.Constant(0f, length, 0f));
            _cleanup.Add(clip);
            return clip;
        }

        /// <summary>A clip whose <c>isLooping</c> is true, for the intro-safety-net regression test.</summary>
        private AnimationClip LoopingClip(string name, float length)
        {
            AnimationClip clip = Clip(name, length);
            clip.wrapMode = WrapMode.Loop;

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            return clip;
        }

        private TalkLayer CreateLayer(ConvaiBodyAnimationSet set, ConvaiBodyAnimationConfig config)
        {
            _graph = PlayableGraph.Create("TalkLayerGestureBracketTests");
            _layer = new TalkLayer();
            var runtime = new LayerRuntime
            {
                Graph = _graph,
                Mixer = new LayerMixerHost(_graph, LayerPorts.Count),
                Set = set,
                Config = config,
                Trace = new AnimTrace("TalkLayerGestureBracketTests"),
                RandomSeed = 7
            };
            _layer.Initialize(runtime, LayerPorts.Talk);
            _initialized = true;
            return _layer;
        }

        private ConvaiBodyAnimationConfig CreateConfig(float calmness = 1f, float listenFadeIn = 0.4f)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_talkFadeInSeconds").floatValue = FadeIn;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = FadeOut;
            serialized.FindProperty("_listenFadeInSeconds").floatValue = listenFadeIn;
            serialized.FindProperty("_talkReleaseDelaySeconds").floatValue = 0f;
            serialized.FindProperty("_useSpeechEnergy").boolValue = false;
            serialized.FindProperty("_talkOverlayWeight").floatValue = 1f;
            serialized.FindProperty("_actionChainCrossfadeSeconds").floatValue = ChainFade;
            serialized.FindProperty("_talkVariantCrossfadeSeconds").floatValue = VariantCrossfade;
            serialized.FindProperty("_talkOutroMaxSeconds").floatValue = OutroMax;
            serialized.FindProperty("_interruptedFreezeSeconds").floatValue = InterruptedFreeze;
            serialized.FindProperty("_interruptedReleaseScale").floatValue = InterruptedReleaseScale;
            serialized.FindProperty("_calmness").floatValue = calmness;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _cleanup.Add(config);
            return config;
        }

        private ConvaiBodyAnimationSet CreateSet(
            AnimationClip loopClip, AnimationClip introClip, AnimationClip outroClip, AnimationClip listenClip = null)
        {
            var mask = new AvatarMask { name = "upper-body" };
            _cleanup.Add(mask);

            var talk = new TalkEntry();
            talk.Initialize(loopClip, introClip: introClip, outroClip: outroClip);
            var talks = new List<TalkEntry> { talk };

            List<TalkEntry> listens = null;
            if (listenClip != null)
            {
                var listen = new TalkEntry();
                listen.Initialize(listenClip);
                listens = new List<TalkEntry> { listen };
            }

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            _cleanup.Add(set);
            set.InitializeContent("Test", null, talks, null, mask, listens);
            return set;
        }

        private static void Tick(TalkLayer layer, float deltaTime, DialogueState state)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(deltaTime, state, in emotion, 1f, false, false);
            layer.Tick(in context);
        }

        [Test]
        public void SpeakingEnter_WithIntroClip_PlaysIntroThenHandsOffToLoop()
        {
            AnimationClip loop = Clip("loop", 5f);
            AnimationClip intro = Clip("intro", 0.3f);
            TalkLayer layer = CreateLayer(CreateSet(loop, intro, null), CreateConfig());

            // First tick: the intro must be current immediately (not the loop, not a weight
            // fade masquerading as the loop).
            Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.AreEqual("intro", layer.ActiveClipName);
            Assert.AreEqual("Intro", layer.TalkBracketPhaseForTests);

            // Advance well past the intro's 0.3s length: the handoff to the loop must happen
            // automatically, with no gap/pop (no separate "None" phase in between).
            for (int i = 0; i < 6; i++)
                Tick(layer, 0.05f, DialogueState.Speaking); // +0.3s -> total 0.35s

            Assert.AreEqual("loop", layer.ActiveClipName);
            Assert.AreEqual("Loop", layer.TalkBracketPhaseForTests);
        }

        [Test]
        public void SpeakingEnter_WithoutIntroClip_PlaysLoopImmediately_UnchangedDefaultPath()
        {
            AnimationClip loop = Clip("loop", 5f);
            TalkLayer layer = CreateLayer(CreateSet(loop, null, null), CreateConfig());

            Tick(layer, 0.05f, DialogueState.Speaking);

            Assert.AreEqual("loop", layer.ActiveClipName);
            Assert.AreEqual("Loop", layer.TalkBracketPhaseForTests);
        }

        [Test]
        public void Release_WithOutroClip_PlaysOutroAndCapsFadeOutAtOutroMaxSeconds()
        {
            AnimationClip loop = Clip("loop", 5f);
            AnimationClip outro = Clip("outro", 5f); // much longer than OutroMax
            TalkLayer layer = CreateLayer(CreateSet(loop, null, outro), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.Greater(layer.Weight, 0.9f, "sanity: fully converged before release");

            // Stop speaking: release must trigger immediately (Talk Release Delay = 0).
            Tick(layer, 0.05f, DialogueState.Idle);

            Assert.AreEqual("outro", layer.ActiveClipName, "release must play the outro, not just fade the loop");
            Assert.AreEqual("Outro", layer.TalkBracketPhaseForTests);
            Assert.AreEqual(OutroMax, layer.FadeOutSecondsForTests, 1e-4f,
                "the outro's added latency must be capped at TalkOutroMaxSeconds, even though the clip itself is 5s");

            // The capped fade-out (0.3s) must fully settle out well before the outro clip's
            // own 5s length, and well before the uncapped Talk Fade Out Seconds (1s) would.
            for (int i = 0; i < 8; i++)
                Tick(layer, 0.05f, DialogueState.Idle); // +0.4s

            Assert.AreEqual("Off", layer.StateLabel);
            Assert.AreEqual(0f, layer.Weight, 1e-3f);
        }

        [Test]
        public void ReenteringSpeaking_MidOutro_CutsBackToLoop_NoWedge()
        {
            AnimationClip loop = Clip("loop", 5f);
            AnimationClip outro = Clip("outro", 5f);
            TalkLayer layer = CreateLayer(CreateSet(loop, null, outro), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);

            Tick(layer, 0.05f, DialogueState.Idle);
            Assert.AreEqual("outro", layer.ActiveClipName, "sanity: outro is playing");
            float weightMidOutro = layer.Weight;
            Assert.Greater(weightMidOutro, 0.5f, "sanity: still visible, not yet faded out");

            // Speaking resumes mid-outro: must cut back to the loop in the same tick, not
            // wait out the outro or a full release-then-reattack (no wedge).
            Tick(layer, 0.05f, DialogueState.Speaking);

            Assert.AreEqual("loop", layer.ActiveClipName);
            Assert.AreEqual("Loop", layer.TalkBracketPhaseForTests);
            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.Greater(layer.Weight, weightMidOutro * 0.5f, "no drop through a full release");
        }

        [Test]
        public void Interrupted_DuringIntroBracket_FreezesThenFastReleases_LikeInterruptedDuringLoop()
        {
            AnimationClip loop = Clip("loop", 5f);
            AnimationClip intro = Clip("intro", 5f); // long enough to still be mid-intro when interrupted
            TalkLayer layer = CreateLayer(CreateSet(loop, intro, null), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking); // fade01 fully converged, still mid-intro
            Assert.AreEqual("Intro", layer.TalkBracketPhaseForTests, "sanity: still mid-intro when interrupted");
            float weightAtInterrupt = layer.Weight;
            Assert.Greater(weightAtInterrupt, 0.9f);

            // The very next tick must freeze the intro pose, not fade or hand off to the loop.
            Tick(layer, 0.05f, DialogueState.Interrupted);
            Assert.AreEqual("InterruptedHold", layer.StateLabel);
            Assert.AreEqual("intro", layer.ActiveClipName, "frozen pose must stay the intro clip");
            Assert.AreEqual(weightAtInterrupt, layer.Weight, 0.01f);

            // Hold elapses -> fast release begins, exactly like an interruption mid-loop.
            int holdTicks = Mathf.CeilToInt(InterruptedFreeze / 0.05f) + 1;
            for (int i = 0; i < holdTicks; i++)
                Tick(layer, 0.05f, DialogueState.Interrupted);
            Assert.AreEqual("FadingOut", layer.StateLabel);
            Assert.AreEqual(FadeOut * InterruptedReleaseScale, layer.FadeOutSecondsForTests, 1e-4f);

            for (int i = 0; i < 20; i++)
                Tick(layer, 0.05f, DialogueState.Interrupted);
            Assert.AreEqual("Off", layer.StateLabel);
            Assert.AreEqual(0f, layer.Weight, 1e-3f);
            Assert.IsFalse(layer.IsInterruptedActiveForTests);

            // Clean recovery: a fresh Speaking entry starts a brand-new intro, no stuck state.
            Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.AreEqual("intro", layer.ActiveClipName);
            Assert.AreEqual("Intro", layer.TalkBracketPhaseForTests);
        }

        [Test]
        public void SpeakingEnter_WithLoopingIntroClip_StillHandsOffAfterIntroLengthElapses()
        {
            // Regression: CrossfadeMixer.IsCurrentClipFinished is unconditionally false for a
            // looping clip, so a user-assigned LOOPING intro must not wedge the bracket forever
            // — the elapsed-time safety net in TickTalkBracket must still force the handoff.
            AnimationClip loop = Clip("loop", 5f);
            AnimationClip loopingIntro = LoopingClip("intro", 0.3f);
            TalkLayer layer = CreateLayer(CreateSet(loop, loopingIntro, null), CreateConfig());

            Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.AreEqual("intro", layer.ActiveClipName);
            Assert.AreEqual("Intro", layer.TalkBracketPhaseForTests);
            Assert.IsTrue(loopingIntro.isLooping, "sanity: the intro clip really is looping");

            // Well past the 0.3s authored length: without the safety net this would stay
            // wedged in "Intro" forever (IsCurrentClipFinished never true for a looping clip).
            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking); // +0.5s -> total 0.55s

            Assert.AreEqual("loop", layer.ActiveClipName);
            Assert.AreEqual("Loop", layer.TalkBracketPhaseForTests);
        }

        [Test]
        public void Calmness_Neutral_TalkFadeOutSecondsIsIdentity()
        {
            TalkLayer layer = CreateLayer(CreateSet(Clip("loop", 5f), null, null), CreateConfig(calmness: 1f));
            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Tick(layer, 0.05f, DialogueState.Idle);

            Assert.AreEqual(FadeOut, layer.FadeOutSecondsForTests, 1e-4f, "identity at Calmness = 1");
        }

        [Test]
        public void Calmness_AboveOne_LengthensTalkFadeOutSeconds()
        {
            TalkLayer layer = CreateLayer(CreateSet(Clip("loop", 5f), null, null), CreateConfig(calmness: 2f));
            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Tick(layer, 0.05f, DialogueState.Idle);

            // 1 + 0.25 * (2 - 1) = 1.25 -> FadeOut * 1.25
            Assert.AreEqual(FadeOut * 1.25f, layer.FadeOutSecondsForTests, 1e-4f, "Calmness must lengthen the release fade-out");
        }

        [Test]
        public void Calmness_Neutral_ListenFadeInSecondsIsIdentity()
        {
            const float listenFadeIn = 0.4f;
            TalkLayer layer = CreateLayer(
                CreateSet(Clip("loop", 5f), null, null, Clip("listen", 5f)),
                CreateConfig(calmness: 1f, listenFadeIn: listenFadeIn));

            Tick(layer, 0.01f, DialogueState.Listening);

            Assert.AreEqual(listenFadeIn, layer.FadeInSecondsForTests, 1e-4f, "identity at Calmness = 1");
        }

        [Test]
        public void Calmness_BelowOne_ShortensListenFadeInSeconds()
        {
            const float listenFadeIn = 0.4f;
            TalkLayer layer = CreateLayer(
                CreateSet(Clip("loop", 5f), null, null, Clip("listen", 5f)),
                CreateConfig(calmness: 0.5f, listenFadeIn: listenFadeIn));

            Tick(layer, 0.01f, DialogueState.Listening);

            // 1 + 0.25 * (0.5 - 1) = 0.875 -> listenFadeIn * 0.875
            Assert.AreEqual(listenFadeIn * 0.875f, layer.FadeInSecondsForTests, 1e-4f, "Calmness must scale the listen fade-in too");
        }
    }
}
