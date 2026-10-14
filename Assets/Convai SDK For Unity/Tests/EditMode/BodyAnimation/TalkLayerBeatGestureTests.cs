using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
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
    ///     Integration coverage for speech-rhythm beat gestures on <see cref="TalkLayer" />:
    ///     the onset-driven beat overlay only fires while Speaking with Beat/Emphatic-tagged
    ///     content, is suppressed by a peer layer owning the arms, and is fully inert (zero
    ///     behavior change) with no such content or with the feature disabled.
    /// </summary>
    public sealed class TalkLayerBeatGestureTests
    {
        [Test]
        public void NoBeatTaggedContent_NeverProducesBeatWeight()
        {
            RunHarness(taggedContent: false, enableBeatGestures: true, suppressed: false, out TalkLayer layer, out _);
            Assert.That(layer.BeatWeight, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void FeatureDisabled_NeverProducesBeatWeightEvenWithContent()
        {
            RunHarness(taggedContent: true, enableBeatGestures: false, suppressed: false, out TalkLayer layer, out _);
            Assert.That(layer.BeatWeight, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void TaggedContentAndOnset_ProducesBeatWeight()
        {
            RunHarness(taggedContent: true, enableBeatGestures: true, suppressed: false, out TalkLayer layer, out bool anyBeatWeightSeen);
            Assert.IsTrue(anyBeatWeightSeen, "A rising-edge onset with tagged content must produce non-zero beat weight.");
            Assert.That(layer.BeatWeight, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void SuppressedByPeerLayer_NeverProducesBeatWeight()
        {
            RunHarness(taggedContent: true, enableBeatGestures: true, suppressed: true, out TalkLayer layer, out bool anyBeatWeightSeen);
            Assert.IsFalse(anyBeatWeightSeen, "A peer layer owning the arms must suppress every beat onset.");
            Assert.That(layer.BeatWeight, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void NotSpeaking_NeverProducesBeatWeight()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerBeatNotSpeakingTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(enableBeatGestures: true);
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup, taggedContent: true);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerBeatNotSpeakingTests"),
                    RandomSeed = 41
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                bool anyBeatWeightSeen = false;
                for (int i = 0; i < 20; i++)
                {
                    float energy = i < 6 ? 0f : 0.9f;
                    Tick(layer, 0.05f, DialogueState.Listening, energy, beatSuppressedByPeers: false);
                    if (layer.BeatWeight > 0.001f) anyBeatWeightSeen = true;
                }

                Assert.IsFalse(anyBeatWeightSeen, "Beats must only fire during the Talk pool (Speaking).");
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void ProximityExpressivenessDisabled_MultiplierStaysExactlyNeutral()
        {
            // Done criterion 5: with the feature off, behavior must be byte-identical to before
            // it existed — the multiplier must never leave 1, regardless of any resolvable
            // camera in the test environment.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerProximityDisabledTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(enableBeatGestures: false);
                cleanup.Add(config); // _proximityExpressiveness defaults false in CreateConfig
                ConvaiBodyAnimationSet set = CreateSet(cleanup, taggedContent: false);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerProximityDisabledTests"),
                    RandomSeed = 41
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                for (int i = 0; i < 5; i++)
                    Tick(layer, 0.05f, DialogueState.Speaking, 0f, beatSuppressedByPeers: false);

                Assert.That(layer.Weight, Is.GreaterThan(0.9f));
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        private static void RunHarness(
            bool taggedContent, bool enableBeatGestures, bool suppressed,
            out TalkLayer layer, out bool anyBeatWeightSeen)
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerBeatHarness");
            layer = new TalkLayer();
            bool initialized = false;
            anyBeatWeightSeen = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(enableBeatGestures);
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup, taggedContent);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerBeatHarness"),
                    RandomSeed = 41
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                // Settle into Speaking with a quiet baseline first, then a sharp rise so the
                // adaptive baseline has something to detect an onset against.
                for (int i = 0; i < 10; i++)
                    Tick(layer, 0.05f, DialogueState.Speaking, 0.05f, suppressed);

                for (int i = 0; i < 20; i++)
                {
                    Tick(layer, 0.05f, DialogueState.Speaking, 0.9f, suppressed);
                    if (layer.BeatWeight > 0.001f) anyBeatWeightSeen = true;
                }
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        private static void Tick(
            TalkLayer layer, float deltaTime, DialogueState state, float speechEnergy, bool beatSuppressedByPeers)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(
                deltaTime, state, in emotion, speechEnergy, hasSpeechEnergy: true, isMoving: false,
                conversationalIntensityScale: 1f, beatSuppressedByPeers: beatSuppressedByPeers);
            layer.Tick(in context);
        }

        private static ConvaiBodyAnimationConfig CreateConfig(bool enableBeatGestures)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_talkFadeInSeconds").floatValue = 0.05f;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = 0.1f;
            serialized.FindProperty("_useSpeechEnergy").boolValue = false;
            serialized.FindProperty("_talkOverlayWeight").floatValue = 1f;
            serialized.FindProperty("_enableBeatGestures").boolValue = enableBeatGestures;
            serialized.FindProperty("_beatRefractorySeconds").floatValue = 0.2f;
            serialized.FindProperty("_beatWeightScale").floatValue = 1f;
            serialized.FindProperty("_proximityExpressiveness").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static ConvaiBodyAnimationSet CreateSet(List<Object> cleanup, bool taggedContent)
        {
            var talkClip = new AnimationClip { name = "talk" };
            cleanup.Add(talkClip);
            var talk = new TalkEntry();
            talk.Initialize(talkClip);

            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);

            List<ActionEntry> actions = null;
            if (taggedContent)
            {
                var beatClip = new AnimationClip { name = "beat" };
                cleanup.Add(beatClip);
                var beatEntry = new ActionEntry();
                beatEntry.Initialize("beat_nod", beatClip, ActionMaskMode.UpperBody);
                beatEntry.SetCue(GestureCueKind.Beat);
                actions = new List<ActionEntry> { beatEntry };
            }

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, actions, mask);
            return set;
        }
    }
}
