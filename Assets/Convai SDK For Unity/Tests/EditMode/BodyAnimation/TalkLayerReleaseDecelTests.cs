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
    ///     Decelerating (non-frozen) release on speech end, and anticipatory release driven by
    ///     a speech-playback witness reading, for <see cref="TalkLayer" />.
    /// </summary>
    public sealed class TalkLayerReleaseDecelTests
    {
        private const float FadeIn = 0.1f;
        private const float FadeOut = 0.5f;
        private const float ReleaseLead = 0.6f;

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

        private TalkLayer CreateLayer(ConvaiBodyAnimationSet set, ConvaiBodyAnimationConfig config, int seed = 41)
        {
            _graph = PlayableGraph.Create("TalkLayerReleaseDecelTests");
            _layer = new TalkLayer();
            var runtime = new LayerRuntime
            {
                Graph = _graph,
                Mixer = new LayerMixerHost(_graph, LayerPorts.Count),
                Set = set,
                Config = config,
                Trace = new AnimTrace("TalkLayerReleaseDecelTests"),
                RandomSeed = seed
            };
            _layer.Initialize(runtime, LayerPorts.Talk);
            _initialized = true;
            return _layer;
        }

        private ConvaiBodyAnimationConfig CreateConfig(float releaseLeadSeconds = ReleaseLead)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_talkFadeInSeconds").floatValue = FadeIn;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = FadeOut;
            serialized.FindProperty("_talkReleaseDelaySeconds").floatValue = 0f;
            serialized.FindProperty("_useSpeechEnergy").boolValue = false;
            serialized.FindProperty("_talkWeightAtLowEnergy").floatValue = 1f;
            serialized.FindProperty("_talkOverlayWeight").floatValue = 1f;
            serialized.FindProperty("_talkReleaseLeadSeconds").floatValue = releaseLeadSeconds;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            _cleanup.Add(config);
            return config;
        }

        private ConvaiBodyAnimationSet CreateSet()
        {
            var talkClip = new AnimationClip { name = "talk" };
            var mask = new AvatarMask { name = "upper-body" };
            _cleanup.Add(talkClip);
            _cleanup.Add(mask);

            var talk = new TalkEntry();
            talk.Initialize(talkClip);

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            _cleanup.Add(set);
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, null, mask);
            return set;
        }

        private static void Tick(
            TalkLayer layer, float deltaTime, DialogueState state,
            float speechRemainingSeconds = -1f, bool speechEndKnown = false)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(
                deltaTime, state, in emotion, 1f, false, false,
                speechRemainingSeconds: speechRemainingSeconds, speechEndKnown: speechEndKnown);
            layer.Tick(in context);
        }

        // -------------------------------------------------------------- defect A: decelerating release

        [Test]
        public void SpeechEnds_ReleaseSpeedDecreasesMonotonicallyToZero_RestingAtBothEnds()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            // Get fully on.
            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.That(layer.StateLabel, Is.EqualTo("Talking"));

            // Speech ends: no release delay configured, so EndTalking fires immediately and the
            // decelerating release begins on this same tick.
            Tick(layer, 0.02f, DialogueState.Idle);
            Assert.That(layer.StateLabel, Is.EqualTo("FadingOut"));
            Assert.That(layer.IsReleaseDeceleratingForTests, Is.True);

            float firstStep = layer.ActivePlaybackSpeedForTests;
            // A tiny first tick (t/fade small): still close to the start speed, not yet the
            // steep middle of the smoothstep -- the derivative starts at 0 (rest at the start).
            Assert.That(firstStep, Is.GreaterThan(0.9f), "The first step must barely have eased off 1x (rest at the start).");

            float previous = firstStep;
            float maxStepDelta = 0f;
            float lastStepDelta = 0f;
            const float dt = 0.05f;
            for (int i = 0; i < 20 && layer.IsReleaseDeceleratingForTests; i++)
            {
                Tick(layer, dt, DialogueState.Idle);
                float current = layer.ActivePlaybackSpeedForTests;
                Assert.That(current, Is.LessThanOrEqualTo(previous + 1e-4f),
                    "Release speed must decrease monotonically toward 0.");
                lastStepDelta = previous - current;
                maxStepDelta = Mathf.Max(maxStepDelta, lastStepDelta);
                previous = current;
            }

            Assert.That(layer.IsReleaseDeceleratingForTests, Is.False, "The decel must finish within the fade-out.");
            Assert.That(layer.ActivePlaybackSpeedForTests, Is.EqualTo(0f).Within(0.0001f),
                "Speed must reach exactly 0 -- rest at the end.");
            Assert.That(lastStepDelta, Is.LessThan(maxStepDelta),
                "The last step must be smaller than a mid-fade step (rest at the end, no velocity jump).");
        }

        [Test]
        public void SpeechEnds_LayerWeightReachesZero_AtOrAfterReleaseSpeedSettles()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());
            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);

            Tick(layer, 0.02f, DialogueState.Idle);
            for (int i = 0; i < 40; i++)
                Tick(layer, 0.05f, DialogueState.Idle);

            Assert.That(layer.Weight, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(layer.IsReleaseDeceleratingForTests, Is.False);
            Assert.That(layer.ActivePlaybackSpeedForTests, Is.EqualTo(0f).Within(0.0001f));
        }

        // -------------------------------------------------------------- defect B: anticipatory release

        [Test]
        public void SpeechEndKnownAndNear_WhileStillSpeaking_BeginsReleaseAndLatches()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.That(layer.StateLabel, Is.EqualTo("Talking"));

            // Still Speaking, but the witness reports the end within the lead: release must
            // begin (no release delay configured, so it goes straight to FadingOut) even
            // though DialogueState never left Speaking.
            Tick(layer, 0.02f, DialogueState.Speaking, speechRemainingSeconds: 0.5f, speechEndKnown: true);
            Assert.That(layer.AnticipatoryReleaseLatchedForTests, Is.True);
            Assert.That(layer.StateLabel, Is.EqualTo("FadingOut"));

            // Subsequent Speaking ticks with the same reading must not restart Talking.
            for (int i = 0; i < 5; i++)
                Tick(layer, 0.05f, DialogueState.Speaking, speechRemainingSeconds: 0.4f, speechEndKnown: true);
            Assert.That(layer.StateLabel, Is.Not.EqualTo("Talking"));
            Assert.That(layer.AnticipatoryReleaseLatchedForTests, Is.True);

            // The state leaves Speaking: the latch clears.
            Tick(layer, 0.05f, DialogueState.Idle);
            Assert.That(layer.AnticipatoryReleaseLatchedForTests, Is.False);

            // A new Speaking turn with no witness evidence (speechEndKnown = false) plays again.
            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.That(layer.StateLabel, Is.EqualTo("Talking"));
        }

        [Test]
        public void NoWitness_DefaultContext_BehavesAsBeforeAnticipatoryRelease()
        {
            TalkLayer layer = CreateLayer(CreateSet(), CreateConfig());

            for (int i = 0; i < 10; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.That(layer.StateLabel, Is.EqualTo("Talking"));

            // Default LayerTickContext fields (speechEndKnown = false) must never trigger an
            // anticipatory release while still Speaking.
            for (int i = 0; i < 20; i++)
                Tick(layer, 0.05f, DialogueState.Speaking);
            Assert.That(layer.StateLabel, Is.EqualTo("Talking"));
            Assert.That(layer.AnticipatoryReleaseLatchedForTests, Is.False);
        }
    }
}
