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
    ///     Conversational state acting (Talk/Listen/Think pools) and interruption behaviour
    ///     (interruption body beat: freeze then fast settle) coverage for
    ///     <see cref="TalkLayer" />.
    /// </summary>
    public sealed class TalkLayerConversationalStateTests
    {
        private const float FadeIn = 0.1f;
        private const float FadeOut = 0.5f;
        private const float ListenFadeIn = 0.3f;
        private const float ThinkingEnterDelay = 0.2f;
        private const float InterruptedFreeze = 0.25f;
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

        private TalkLayer CreateLayer(ConvaiBodyAnimationSet set, ConvaiBodyAnimationConfig config, int seed = 11)
        {
            _graph = PlayableGraph.Create("TalkLayerConversationalStateTests");
            _layer = new TalkLayer();
            var runtime = new LayerRuntime
            {
                Graph = _graph,
                Mixer = new LayerMixerHost(_graph, LayerPorts.Count),
                Set = set,
                Config = config,
                Trace = new AnimTrace("TalkLayerConversationalStateTests"),
                RandomSeed = seed
            };
            _layer.Initialize(runtime, LayerPorts.Talk);
            _initialized = true;
            return _layer;
        }

        private ConvaiBodyAnimationConfig CreateConfig()
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_talkFadeInSeconds").floatValue = FadeIn;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = FadeOut;
            serialized.FindProperty("_talkReleaseDelaySeconds").floatValue = 0f;
            serialized.FindProperty("_useSpeechEnergy").boolValue = false;
            serialized.FindProperty("_talkWeightAtLowEnergy").floatValue = 1f;
            serialized.FindProperty("_talkOverlayWeight").floatValue = 1f;
            serialized.FindProperty("_listenFadeInSeconds").floatValue = ListenFadeIn;
            serialized.FindProperty("_thinkingEnterDelaySeconds").floatValue = ThinkingEnterDelay;
            serialized.FindProperty("_interruptedFreezeSeconds").floatValue = InterruptedFreeze;
            serialized.FindProperty("_interruptedReleaseScale").floatValue = InterruptedReleaseScale;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _cleanup.Add(config);
            return config;
        }

        private ConvaiBodyAnimationSet CreateSet(bool withListen = true, bool withThink = true)
        {
            var talkClip = new AnimationClip { name = "talk" };
            var listenClip = new AnimationClip { name = "listen" };
            var thinkClip = new AnimationClip { name = "think" };
            var mask = new AvatarMask { name = "upper-body" };
            _cleanup.Add(talkClip);
            _cleanup.Add(listenClip);
            _cleanup.Add(thinkClip);
            _cleanup.Add(mask);

            var talk = new TalkEntry();
            talk.Initialize(talkClip);
            var talks = new List<TalkEntry> { talk };

            List<TalkEntry> listens = null;
            if (withListen)
            {
                var listen = new TalkEntry();
                listen.Initialize(listenClip);
                listens = new List<TalkEntry> { listen };
            }

            List<TalkEntry> thinks = null;
            if (withThink)
            {
                var think = new TalkEntry();
                think.Initialize(thinkClip);
                thinks = new List<TalkEntry> { think };
            }

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            _cleanup.Add(set);
            set.InitializeContent("Test", null, talks, null, mask, listens, thinks);
            return set;
        }

        private static void Tick(TalkLayer layer, float deltaTime, DialogueState state, bool isMoving = false)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(deltaTime, state, in emotion, 1f, false, isMoving);
            layer.Tick(in context);
        }

        [Test]
        public void Speaking_PlaysTalkPool()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 5; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);

            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.AreEqual("talk", layer.ActiveClipName);
            Assert.AreEqual("Talk", layer.ActivePoolKindForTests);
        }

        [Test]
        public void Listening_PlaysListenPool()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Listening);

            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.AreEqual("listen", layer.ActiveClipName);
            Assert.AreEqual("Listen", layer.ActivePoolKindForTests);
        }

        [Test]
        public void Attending_AlsoPlaysListenPool()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Attending);

            Assert.AreEqual("listen", layer.ActiveClipName);
            Assert.AreEqual("Listen", layer.ActivePoolKindForTests);
        }

        [Test]
        public void PoolSwitch_ListeningToSpeaking_CrossfadesWithoutFullRelease()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Listening);
            Assert.AreEqual("listen", layer.ActiveClipName);
            float weightBeforeSwitch = layer.Weight;

            // Speaking must take over immediately (same tick), not after a release delay
            // + full fade-out — a pool switch reuses the crossfade-continuation path.
            Tick(layer, 0.05f, DialogueState.Speaking);

            Assert.AreEqual("talk", layer.ActiveClipName);
            Assert.AreEqual("Talk", layer.ActivePoolKindForTests);
            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.Greater(layer.Weight, weightBeforeSwitch * 0.5f, "no drop through a full release");
        }

        [Test]
        public void EmptyListenPool_DegradesToReleasedLayer()
        {
            TalkLayer layer = CreateLayer(CreateSet(withListen: false), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Listening);

            Assert.AreEqual("Off", layer.StateLabel);
            Assert.AreEqual(0f, layer.Weight, 1e-4f);
        }

        [Test]
        public void Thinking_BelowEnterDelay_NeverCommitsThinkClip()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            // Two short Thinking ticks stay below the 0.2s gate.
            Tick(layer, 0.05f, DialogueState.Thinking);
            Tick(layer, 0.05f, DialogueState.Thinking);

            Assert.AreEqual("Off", layer.StateLabel);
            Assert.AreEqual("(none)", layer.ActiveClipName);
        }

        [Test]
        public void Thinking_AboveEnterDelay_CommitsThinkClip()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Thinking); // 0.5s total, past the 0.2s gate

            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.AreEqual("think", layer.ActiveClipName);
            Assert.AreEqual("Think", layer.ActivePoolKindForTests);
        }

        [Test]
        public void ListenFadeIn_UsesConfiguredListenFadeInSeconds()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            Tick(layer, 0.01f, DialogueState.Listening);

            Assert.AreEqual(ListenFadeIn, layer.FadeInSecondsForTests, 1e-4f);
        }

        [Test]
        public void Speaking_UsesTalkFadeInSeconds_NotListenFadeIn()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            Tick(layer, 0.01f, DialogueState.Speaking);

            Assert.AreEqual(FadeIn, layer.FadeInSecondsForTests, 1e-4f);
        }

        [Test]
        public void Interrupted_FreezesThenFastReleases_ThenRecoversCleanlyForNextSpeaking()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            // Reach full weight while Speaking.
            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            float weightAtInterrupt = layer.Weight;
            Assert.Greater(weightAtInterrupt, 0.9f, "sanity: fully converged before interrupting");

            // Interrupt: the very next tick must freeze, not fade.
            Tick(layer, 0.05f, DialogueState.Interrupted);
            Assert.AreEqual("InterruptedHold", layer.StateLabel);
            Assert.AreEqual(weightAtInterrupt, layer.Weight, 0.01f, "hold must not fade the weight");

            // Hold for InterruptedFreeze seconds (0.25s @ 0.05s steps = 5 ticks); weight
            // must stay essentially unchanged throughout the hold.
            for (int i = 0; i < 4; i++)
            {
                Tick(layer, 0.05f, DialogueState.Interrupted);
                Assert.AreEqual("InterruptedHold", layer.StateLabel, $"still holding at tick {i}");
                Assert.AreEqual(weightAtInterrupt, layer.Weight, 0.01f, $"weight moved during hold at tick {i}");
            }

            // One more tick crosses the 0.25s hold: the fast release begins.
            Tick(layer, 0.05f, DialogueState.Interrupted);
            Assert.AreEqual("FadingOut", layer.StateLabel);
            Assert.AreEqual(FadeOut * InterruptedReleaseScale, layer.FadeOutSecondsForTests, 1e-4f);
            float weightJustAfterHold = layer.Weight;
            Assert.Less(weightJustAfterHold, weightAtInterrupt, "the fast release must start decaying the weight");

            // The scaled release (0.25s) must fully settle out well before the un-scaled
            // Talk Fade Out Seconds (0.5s) would have.
            for (int i = 0; i < 5; i++)
                Tick(layer, 0.05f, DialogueState.Interrupted);

            Assert.AreEqual("Off", layer.StateLabel);
            Assert.AreEqual(0f, layer.Weight, 1e-3f);
            Assert.IsFalse(layer.IsInterruptedActiveForTests, "must be clean for the next Speaking entry");

            // Clean recovery: a fresh Speaking entry starts normally, no stuck frozen state.
            Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.AreEqual("talk", layer.ActiveClipName);
            Assert.AreEqual(FadeOut, layer.FadeOutSecondsForTests, 1e-4f, "fade-out scale must be restored to normal");
        }

        [Test]
        public void Interrupted_WhileFadingIn_DoesNotFreeze_FallsThroughToNormalRelease()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            // Barely started (envelope near zero) — an interruption here is not "actively
            // playing" and must not enter the freeze/hold sequence.
            Tick(layer, 0.001f, DialogueState.Speaking);
            Tick(layer, 0.001f, DialogueState.Interrupted);

            Assert.AreNotEqual("InterruptedHold", layer.StateLabel);
            Assert.IsFalse(layer.IsInterruptedActiveForTests);
        }

        [Test]
        public void Interrupted_ResumingSpeakingMidHold_CancelsInterruptionImmediately()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);

            Tick(layer, 0.05f, DialogueState.Interrupted);
            Assert.AreEqual("InterruptedHold", layer.StateLabel);

            // Speaking resumes mid-hold: must cancel the interruption and resume talking
            // immediately, not wait out the freeze hold.
            Tick(layer, 0.05f, DialogueState.Speaking);

            Assert.AreEqual("Talking", layer.StateLabel);
            Assert.IsFalse(layer.IsInterruptedActiveForTests);
            Assert.AreEqual("talk", layer.ActiveClipName);
        }
    }
}
