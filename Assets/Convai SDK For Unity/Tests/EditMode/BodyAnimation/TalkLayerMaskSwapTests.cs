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
    ///     Regression tests for the talk-layer mask/mode swap deferral fix: a body-coverage or
    ///     additive-mode change must never cut in while the layer is visible — it has to be
    ///     queued and applied at the envelope trough (weight ≈ 0), never instantly mid-speech.
    /// </summary>
    public sealed class TalkLayerMaskSwapTests
    {
        [Test]
        public void FullBodyEntry_StartsMoving_DefersMaskSwapToTrough()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerMaskSwapDeferTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);

                ConvaiBodyAnimationSet set = CreateSetWithTalkEntry(
                    cleanup, BodyCoverage.FullBody, additive: false);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerMaskSwapDeferTests"),
                    RandomSeed = 3
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                // Speaking, stationary: full-body coverage applies immediately (layer starts
                // invisible), and the envelope rises to full weight.
                for (int i = 0; i < 3; i++)
                    Tick(layer, 0.1f, DialogueState.Speaking, isMoving: false);

                Assert.That(layer.AppliedCoverageForTests, Is.EqualTo(BodyCoverage.FullBody));
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                // Movement starts mid-speech: the mask swap to upper-body must be deferred,
                // not applied instantly (the fix under test).
                Tick(layer, 0.02f, DialogueState.Speaking, isMoving: true);

                Assert.That(layer.HasPendingApplyForTests, Is.True);
                Assert.That(layer.AppliedCoverageForTests, Is.EqualTo(BodyCoverage.FullBody),
                    "the swap must be deferred to the envelope trough, not applied instantly");

                bool swapped = false;
                float previousWeight = layer.Weight;

                for (int i = 0; i < 24 && !swapped; i++)
                {
                    Tick(layer, 0.02f, DialogueState.Speaking, isMoving: true);

                    if (layer.AppliedCoverageForTests == BodyCoverage.UpperBody)
                    {
                        swapped = true;
                        Assert.That(previousWeight, Is.LessThan(0.15f),
                            "the mask swap must only happen once the envelope has dipped to the trough");
                    }

                    previousWeight = layer.Weight;
                }

                Assert.That(swapped, Is.True, "coverage never swapped to UpperBody within the tick budget");
                Assert.That(layer.HasPendingApplyForTests, Is.False);

                // Envelope rises again after the swap, still speaking + moving. The entry has
                // no additive clip, so while moving the override is softened to the config's
                // Moving Talk Override Weight (default 0.45) — the gait bleeds through
                // instead of a full-strength override freezing the arms.
                for (int i = 0; i < 20; i++)
                    Tick(layer, 0.02f, DialogueState.Speaking, isMoving: true);

                Assert.That(layer.Weight, Is.GreaterThan(0.4f));
                Assert.That(layer.Weight, Is.LessThan(0.55f),
                    "while moving without an additive clip the override must stay softened");
                Assert.That(layer.StateLabel, Is.EqualTo("Talking"));
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
        public void AdditiveEntry_FreshStart_AppliesImmediately()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerAdditiveFreshStartTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);

                ConvaiBodyAnimationSet set = CreateSetWithTalkEntry(
                    cleanup, BodyCoverage.UpperBody, additive: true);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerAdditiveFreshStartTests"),
                    RandomSeed = 9
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                Tick(layer, 0.1f, DialogueState.Speaking, isMoving: false);

                Assert.That(layer.AppliedAdditiveForTests, Is.True);
                Assert.That(layer.HasPendingApplyForTests, Is.False,
                    "an invisible layer's first mode application must be instant, not queued");
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

        private static ConvaiBodyAnimationConfig CreateConfig()
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);

            // This test asserts a settled talk layer as "> 0.9" while it watches the mask swap, so
            // it needs the overlay cap pinned at 1 rather than at whatever Convai last tuned the
            // shipped cap to.
            serialized.FindProperty("_talkOverlayWeight").floatValue = 1f;
            serialized.FindProperty("_talkFadeInSeconds").floatValue = 0.1f;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = 0.2f;
            serialized.FindProperty("_useSpeechEnergy").boolValue = false;
            serialized.FindProperty("_talkReleaseDelaySeconds").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static ConvaiBodyAnimationSet CreateSetWithTalkEntry(
            List<Object> cleanup, BodyCoverage coverage, bool additive)
        {
            var clip = new AnimationClip { name = "talk" };
            clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 1f));
            cleanup.Add(clip);

            var talk = new TalkEntry();
            talk.Initialize(clip, 1f, coverage, additive);

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
