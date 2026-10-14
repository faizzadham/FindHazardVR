using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core;
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
    ///     Coverage for <see cref="ConversationalGesturePerformer" />'s
    ///     <see cref="Convai.Domain.Embodiment.Interfaces.IConversationalMotionBudget" />
    ///     implementation: the hard-suppression truth table, occupancy tracking off the live
    ///     talk-layer weight, the speech-gap cue window, and the intensity-report clamp.
    /// </summary>
    public sealed class ConversationalMotionBudgetTests
    {
        // ── ComputeHardSuppression truth table (8 rows, mirrors ComputeSuppression's own test shape) ──

        private sealed class Inputs
        {
            public bool FullBodyAction;
            public bool TurningInPlace;
            public bool TalkFullBodyCoverage;
            public bool Moving;
        }

        private static GestureSuppression ComputeHard(Inputs i) =>
            ConversationalGesturePerformer.ComputeHardSuppression(
                i.FullBodyAction, i.TurningInPlace, i.TalkFullBodyCoverage, i.Moving);

        [Test]
        public void HardSuppression_Idle_IsNone() =>
            Assert.AreEqual(GestureSuppression.None, ComputeHard(new Inputs()));

        [Test]
        public void HardSuppression_FullBodyAction_IsFullBody() =>
            Assert.AreEqual(GestureSuppression.FullBody, ComputeHard(new Inputs { FullBodyAction = true }));

        [Test]
        public void HardSuppression_TurningInPlace_IsFullBody() =>
            Assert.AreEqual(GestureSuppression.FullBody, ComputeHard(new Inputs { TurningInPlace = true }));

        [Test]
        public void HardSuppression_TalkFullBodyCoverage_IsFullBody() =>
            Assert.AreEqual(GestureSuppression.FullBody, ComputeHard(new Inputs { TalkFullBodyCoverage = true }));

        [Test]
        public void HardSuppression_Moving_IsUpperBody() =>
            Assert.AreEqual(GestureSuppression.UpperBody, ComputeHard(new Inputs { Moving = true }));

        [Test]
        public void HardSuppression_MovingAndFullBodyAction_IsFullBody() =>
            Assert.AreEqual(GestureSuppression.FullBody, ComputeHard(new Inputs { Moving = true, FullBodyAction = true }));

        [Test]
        public void HardSuppression_MovingAndTurningInPlace_IsFullBody() =>
            Assert.AreEqual(GestureSuppression.FullBody, ComputeHard(new Inputs { Moving = true, TurningInPlace = true }));

        [Test]
        public void HardSuppression_MovingAndTalkFullBodyCoverage_IsFullBody() =>
            Assert.AreEqual(GestureSuppression.FullBody, ComputeHard(new Inputs { Moving = true, TalkFullBodyCoverage = true }));

        // ── UpperBodyOccupancy01 ─────────────────────────────────────────────

        [Test]
        public void UpperBodyOccupancy01_NoTalkLayer_IsZero()
        {
            var performer = new ConversationalGesturePerformer(null, null, null, null);
            Assert.That(performer.UpperBodyOccupancy01, Is.EqualTo(0f));
        }

        [Test]
        public void UpperBodyOccupancy01_TracksLiveTalkLayerWeight()
        {
            RunWithTalkLayer(overlayWeight: 1f, fadeInSeconds: 0.05f, ticks: 200, (talkLayer, performer) =>
            {
                float expected = Mathf.Clamp01(Mathf.Max(talkLayer.Weight, talkLayer.MovingWeight));
                Assert.That(performer.UpperBodyOccupancy01, Is.EqualTo(expected).Within(1e-5f));
                Assert.That(performer.UpperBodyOccupancy01, Is.GreaterThan(0.9f),
                    "A converged full-overlay stationary talk should read as near-full occupancy.");
            });
        }

        // ── TryPerform speech-gap window ─────────────────────────────────────

        [Test]
        public void TryPerform_HighOccupancy_Refused()
        {
            RunWithTaggedActionAndTalkLayer(overlayWeight: 1f, fadeInSeconds: 0.05f, ticks: 200,
                (performer, occupancy) =>
                {
                    Assert.That(occupancy, Is.GreaterThan(ConversationalGesturePerformer.CueOccupancyWindowThreshold));
                    Assert.IsFalse(performer.TryPerform(new GestureCue(GestureCueKind.Affirmative, 1f)),
                        "A cue during high overlay occupancy must be refused (speech-gap window).");
                });
        }

        [Test]
        public void TryPerform_LowOccupancy_AllowedWhenHardSuppressionNone()
        {
            RunWithTaggedActionAndTalkLayer(overlayWeight: 0.1f, fadeInSeconds: 0.05f, ticks: 200,
                (performer, occupancy) =>
                {
                    Assert.That(occupancy, Is.LessThan(ConversationalGesturePerformer.CueOccupancyWindowThreshold));
                    Assert.IsTrue(performer.TryPerform(new GestureCue(GestureCueKind.Affirmative, 1f)),
                        "A cue during a low-occupancy speech-pause window must be accepted (hard suppression None).");
                });
        }

        // ── ReportConversationalIntensity clamp ──────────────────────────────

        [Test]
        public void ReportConversationalIntensity_ClampsToMinMaxWindow()
        {
            var performer = new ConversationalGesturePerformer(null, null, null, null);

            performer.ReportConversationalIntensity(0.2f);
            Assert.That(performer.ReportedIntensityScale,
                Is.EqualTo(ConversationalGesturePerformer.MinIntensityScale).Within(1e-5f));

            performer.ReportConversationalIntensity(5f);
            Assert.That(performer.ReportedIntensityScale,
                Is.EqualTo(ConversationalGesturePerformer.MaxIntensityScale).Within(1e-5f));

            performer.ReportConversationalIntensity(1f);
            Assert.That(performer.ReportedIntensityScale, Is.EqualTo(1f).Within(1e-5f));
        }

        // ── harness ──────────────────────────────────────────────────────────

        private static void RunWithTalkLayer(
            float overlayWeight, float fadeInSeconds, int ticks,
            System.Action<TalkLayer, ConversationalGesturePerformer> assertion)
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ConversationalMotionBudgetTests");
            var talkLayer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(overlayWeight, fadeInSeconds);
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSetWithTalkAndTaggedAction(cleanup, out _);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ConversationalMotionBudgetTests"),
                    RandomSeed = 11
                };

                talkLayer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                var performer = new ConversationalGesturePerformer(set, null, talkLayer, null);

                for (int i = 0; i < ticks; i++)
                    TickTalk(talkLayer, 0.05f, DialogueState.Speaking);

                assertion(talkLayer, performer);
            }
            finally
            {
                if (initialized) talkLayer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        private static void RunWithTaggedActionAndTalkLayer(
            float overlayWeight, float fadeInSeconds, int ticks,
            System.Action<ConversationalGesturePerformer, float> assertion)
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ConversationalMotionBudgetTests_TryPerform");
            var talkLayer = new TalkLayer();
            var actionLayer = new ActionLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(overlayWeight, fadeInSeconds);
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSetWithTalkAndTaggedAction(cleanup, out _);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ConversationalMotionBudgetTests_TryPerform"),
                    RandomSeed = 13
                };

                talkLayer.Initialize(runtime, LayerPorts.Talk);
                actionLayer.Initialize(runtime, LayerPorts.Action);
                initialized = true;

                var performer = new ConversationalGesturePerformer(set, actionLayer, talkLayer, null);

                for (int i = 0; i < ticks; i++)
                    TickTalk(talkLayer, 0.05f, DialogueState.Speaking);

                assertion(performer, performer.UpperBodyOccupancy01);
            }
            finally
            {
                if (initialized)
                {
                    talkLayer.Teardown();
                    actionLayer.Teardown();
                }
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        private static ConvaiBodyAnimationConfig CreateConfig(float overlayWeight, float fadeInSeconds)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_talkFadeInSeconds").floatValue = fadeInSeconds;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = 0.5f;
            serialized.FindProperty("_useSpeechEnergy").boolValue = false;
            serialized.FindProperty("_talkWeightAtLowEnergy").floatValue = overlayWeight;
            serialized.FindProperty("_talkOverlayWeight").floatValue = overlayWeight;
            serialized.FindProperty("_actionFadeInSeconds").floatValue = 0.05f;
            serialized.FindProperty("_actionFadeOutSeconds").floatValue = 0.05f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static ConvaiBodyAnimationSet CreateSetWithTalkAndTaggedAction(
            List<Object> cleanup, out ActionEntry taggedEntry)
        {
            var talkClip = new AnimationClip { name = "talk" };
            cleanup.Add(talkClip);
            var talk = new TalkEntry();
            talk.Initialize(talkClip);

            var actionClip = new AnimationClip { name = "yes" };
            actionClip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0f, 0.5f, 0f));
            cleanup.Add(actionClip);
            var entry = new ActionEntry();
            entry.Initialize("yes", actionClip, ActionMaskMode.UpperBody, ActionLoopMode.PlayOnce);
            entry.SetCue(GestureCueKind.Affirmative);
            taggedEntry = entry;

            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, new List<ActionEntry> { entry }, mask);
            return set;
        }

        private static void TickTalk(TalkLayer layer, float deltaTime, DialogueState state, bool isMoving = false)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(deltaTime, state, in emotion, 0f, false, isMoving);
            layer.Tick(in context);
        }
    }
}
