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
    ///     Coverage for the "Moving Talk" (walk-and-talk) dual-slot system on the talk layer:
    ///     the stationary override port and the additive moving port (<see cref="LayerPorts.TalkMoving" />)
    ///     crossfade their share of the envelope based on <see cref="MovingTalkMode" /> and
    ///     whether the active entry authored an additive twin.
    /// </summary>
    public sealed class MovingTalkTests
    {
        [Test]
        public void TierA_AdditiveEntry_MovingShiftsWeightToMovingPort()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("MovingTalkTierATests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    fadeInSeconds: 0.1f,
                    fadeOutSeconds: 0.2f,
                    useSpeechEnergy: false,
                    movingTalkMode: 0,
                    movingTalkBlendSeconds: 0.2f);
                cleanup.Add(config);

                TalkEntry talk = BuildTalkEntry(cleanup, withAdditiveClip: true);
                ConvaiBodyAnimationSet set = CreateSetWithTalkEntry(talk, cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("MovingTalkTierATests"),
                    RandomSeed = 11
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                // Stationary speech settles fully on the override port.
                for (int i = 0; i < 3; i++)
                    Tick(layer, 0.1f, DialogueState.Speaking, isMoving: false);

                Assert.That(layer.Weight, Is.GreaterThan(0.9f));
                Assert.That(layer.MovingWeight, Is.EqualTo(0f).Within(0.01f));

                // Start moving mid-speech: weight crossfades onto the additive moving port.
                const int steps = 20; // 20 * 0.02s = 0.4s, matches movingTalkBlendSeconds=0.2s twice over
                float midWeight = -1f;
                float midMovingWeight = -1f;

                for (int i = 0; i < steps; i++)
                {
                    Tick(layer, 0.02f, DialogueState.Speaking, isMoving: true);
                    if (i == 4)
                    {
                        midWeight = layer.Weight;
                        midMovingWeight = layer.MovingWeight;
                    }
                }

                Assert.That(midWeight, Is.GreaterThan(0f), "mid-crossfade stationary weight should not have already reached 0");
                Assert.That(midWeight, Is.LessThan(1f), "mid-crossfade stationary weight should not still be at full weight");
                Assert.That(midMovingWeight, Is.GreaterThan(0f), "mid-crossfade moving weight should have started rising");
                Assert.That(midMovingWeight, Is.LessThan(1f), "mid-crossfade moving weight should not have snapped to full");

                Assert.That(layer.MovingWeight, Is.EqualTo(0.7f).Within(0.05f));
                Assert.That(layer.Weight, Is.EqualTo(0f).Within(0.05f));
                Assert.That(layer.MovingClipNameForTests, Is.EqualTo("talk_additive"));

                // Stop moving: weight crossfades back onto the stationary override port.
                for (int i = 0; i < steps; i++)
                    Tick(layer, 0.02f, DialogueState.Speaking, isMoving: false);

                Assert.That(layer.Weight, Is.GreaterThan(0.9f));
                Assert.That(layer.MovingWeight, Is.EqualTo(0f).Within(0.01f));
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void TierB_NoAdditive_SoftensOverrideWhileMoving()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("MovingTalkTierBTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    fadeInSeconds: 0.1f,
                    fadeOutSeconds: 0.2f,
                    useSpeechEnergy: false,
                    movingTalkMode: 0,
                    movingTalkBlendSeconds: 0.2f);
                cleanup.Add(config);

                TalkEntry talk = BuildTalkEntry(cleanup, withAdditiveClip: false);
                ConvaiBodyAnimationSet set = CreateSetWithTalkEntry(talk, cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("MovingTalkTierBTests"),
                    RandomSeed = 12
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                for (int i = 0; i < 3; i++)
                    Tick(layer, 0.1f, DialogueState.Speaking, isMoving: false);

                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                const int steps = 20; // 0.4s
                for (int i = 0; i < steps; i++)
                {
                    Tick(layer, 0.02f, DialogueState.Speaking, isMoving: true);
                    Assert.That(layer.MovingWeight, Is.EqualTo(0f),
                        "no additive twin exists, the moving port must stay silent");
                }

                Assert.That(layer.Weight, Is.EqualTo(0.45f).Within(0.05f));
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void SpeechStartsWhileMoving_TierA_OpensOnMovingPort()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("MovingTalkOpenOnMovingTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    fadeInSeconds: 0.1f,
                    fadeOutSeconds: 0.2f,
                    useSpeechEnergy: false,
                    movingTalkMode: 0,
                    movingTalkBlendSeconds: 0.2f);
                cleanup.Add(config);

                TalkEntry talk = BuildTalkEntry(cleanup, withAdditiveClip: true);
                ConvaiBodyAnimationSet set = CreateSetWithTalkEntry(talk, cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("MovingTalkOpenOnMovingTests"),
                    RandomSeed = 13
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                // First tick ever, already Speaking + moving, with a tiny dt so the envelope
                // is still at the trough when the slot split is resolved — the split snaps
                // straight to its target instead of ramping through the blend.
                Tick(layer, 0.001f, DialogueState.Speaking, isMoving: true);

                Assert.That(layer.MovingFactorForTests, Is.EqualTo(0.7f).Within(0.05f),
                    "the moving slot must snap to its target share while the envelope is still at the trough");

                // Let the envelope rise while the slot split (already at target) holds.
                for (int i = 0; i < 6; i++)
                    Tick(layer, 0.05f, DialogueState.Speaking, isMoving: true);

                Assert.That(layer.MovingWeight, Is.GreaterThan(0.5f));
                Assert.That(layer.Weight, Is.LessThan(0.1f));
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void MovingTalkMode_Suppress_FadesOutEvenWithAdditiveTwin()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("MovingTalkSuppressModeTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    fadeInSeconds: 0.1f,
                    fadeOutSeconds: 0.2f,
                    useSpeechEnergy: false,
                    movingTalkMode: 2,
                    movingTalkBlendSeconds: 0.2f);
                cleanup.Add(config);

                TalkEntry talk = BuildTalkEntry(cleanup, withAdditiveClip: true);
                ConvaiBodyAnimationSet set = CreateSetWithTalkEntry(talk, cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("MovingTalkSuppressModeTests"),
                    RandomSeed = 14
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                for (int i = 0; i < 3; i++)
                    Tick(layer, 0.1f, DialogueState.Speaking, isMoving: false);

                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                // Suppress fades talk out entirely even though the entry has an additive twin —
                // there is no compat override left to win over it.
                Tick(layer, 0.1f, DialogueState.Speaking, isMoving: true);
                Assert.That(layer.StateLabel, Is.EqualTo("FadingOut"));

                for (int i = 0; i < 6; i++)
                {
                    Tick(layer, 0.1f, DialogueState.Speaking, isMoving: true);
                    Assert.That(layer.MovingWeight, Is.EqualTo(0f), "suppress must never open the moving port");
                }

                Assert.That(layer.Weight, Is.LessThan(0.05f));
                Assert.That(layer.MovingWeight, Is.EqualTo(0f));
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void MovingTalkMode_SoftenedOverride_IgnoresAdditiveClip()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("MovingTalkSoftenedOverrideTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    fadeInSeconds: 0.1f,
                    fadeOutSeconds: 0.2f,
                    useSpeechEnergy: false,
                    movingTalkMode: 1,
                    movingTalkBlendSeconds: 0.2f);
                cleanup.Add(config);

                // Additive twin is present, but SoftenedOverride must ignore it.
                TalkEntry talk = BuildTalkEntry(cleanup, withAdditiveClip: true);
                ConvaiBodyAnimationSet set = CreateSetWithTalkEntry(talk, cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("MovingTalkSoftenedOverrideTests"),
                    RandomSeed = 15
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                for (int i = 0; i < 3; i++)
                    Tick(layer, 0.1f, DialogueState.Speaking, isMoving: false);

                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                const int steps = 20; // 0.4s
                for (int i = 0; i < steps; i++)
                {
                    Tick(layer, 0.02f, DialogueState.Speaking, isMoving: true);
                    Assert.That(layer.MovingWeight, Is.EqualTo(0f),
                        "SoftenedOverride must never open the additive moving port, even with an additive twin authored");
                }

                Assert.That(layer.Weight, Is.EqualTo(0.45f).Within(0.05f));
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        private static ConvaiBodyAnimationConfig CreateConfig(
            float fadeInSeconds,
            float fadeOutSeconds,
            bool useSpeechEnergy,
            int movingTalkMode,
            float movingTalkBlendSeconds)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);

            // These tests are about which PORT the talk weight lands on, so they assert a settled
            // layer as "> 0.9". That reads as an absolute only while the overlay cap is 1; pinning
            // it here keeps the assertions meaning what they say instead of quietly re-testing
            // whatever Convai last tuned the shipped cap to.
            serialized.FindProperty("_talkOverlayWeight").floatValue = 1f;
            serialized.FindProperty("_talkFadeInSeconds").floatValue = fadeInSeconds;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = fadeOutSeconds;
            serialized.FindProperty("_useSpeechEnergy").boolValue = useSpeechEnergy;
            serialized.FindProperty("_movingTalkMode").intValue = movingTalkMode;
            serialized.FindProperty("_movingTalkBlendSeconds").floatValue = movingTalkBlendSeconds;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static TalkEntry BuildTalkEntry(List<Object> cleanup, bool withAdditiveClip)
        {
            var clip = new AnimationClip { name = "talk" };
            cleanup.Add(clip);

            var talk = new TalkEntry();
            talk.Initialize(clip);

            if (withAdditiveClip)
            {
                var additiveClip = new AnimationClip { name = "talk_additive" };
                cleanup.Add(additiveClip);
                talk.SetAdditiveClip(additiveClip);
            }

            return talk;
        }

        private static ConvaiBodyAnimationSet CreateSetWithTalkEntry(TalkEntry talk, List<Object> cleanup)
        {
            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, null, mask);
            return set;
        }

        private static void Tick(TalkLayer layer, float deltaTime, DialogueState state, bool isMoving)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(deltaTime, state, in emotion, 0f, false, isMoving);
            layer.Tick(in context);
        }
    }
}
