using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="CoSpeechCoordinator" />: the glance/brow
    ///     cross-module dispatch and the sequence latch that turns "the planner still reports a
    ///     gesture" into "dispatch exactly once per newly-resolved gesture".
    /// </summary>
    public sealed class CoSpeechCoordinatorTests
    {
        private sealed class FakeGlanceHandler : IGazeGlanceHandler
        {
            public int CallCount;
            public Vector3 LastPosition;
            public float LastDuration;
            public void RequestGlance(Vector3 worldPosition, float durationSeconds)
            {
                CallCount++;
                LastPosition = worldPosition;
                LastDuration = durationSeconds;
            }
        }

        private sealed class FakeBrowCueSink : IBrowCueSink
        {
            public int CallCount;
            public BrowCueKind LastKind;
            public float LastIntensity;
            public void RaiseBrowCue(BrowCueKind kind, float intensity01)
            {
                CallCount++;
                LastKind = kind;
                LastIntensity = intensity01;
            }
        }

        private static CoSpeechPerformanceReading Reading(
            int sequence, GestureCueKind kind, bool hasWorldTarget = false, Vector3 worldTarget = default,
            float intensity = 0.8f) =>
            new(
                generationId: 1, isSpeaking: true, speechEnergy: 0.5f, phraseProgress: 0.5f,
                phrasePhase: CoSpeechPhrasePhase.Speaking, qualityTier: CoSpeechQualityTier.EnergyOnly,
                gesture: new CoSpeechGestureRequest(
                    sequence, kind, intensity, confidence: 1f,
                    preparationSeconds: 0.1f, strokeSeconds: 0.2f, holdSeconds: 0.1f, retractionSeconds: 0.1f,
                    hasWorldTarget: hasWorldTarget, worldTarget: worldTarget));

        [Test]
        public void NoGesture_DispatchesNothing()
        {
            var coordinator = new CoSpeechCoordinator();
            var glance = new FakeGlanceHandler();
            var brow = new FakeBrowCueSink();

            // A static property cannot be passed by `in` reference (CS8156) — bind it to a local.
            CoSpeechPerformanceReading none = CoSpeechPerformanceReading.None;
            coordinator.Dispatch(in none, glance, brow);

            Assert.AreEqual(0, glance.CallCount);
            Assert.AreEqual(0, brow.CallCount);
        }

        [Test]
        public void WorldTargetGesture_RequestsGlance_WithSummedTiming()
        {
            var coordinator = new CoSpeechCoordinator();
            var glance = new FakeGlanceHandler();
            var target = new Vector3(1f, 0f, 2f);
            CoSpeechPerformanceReading reading = Reading(1, GestureCueKind.Affirmative, true, target);

            coordinator.Dispatch(in reading, glance, null);

            Assert.AreEqual(1, glance.CallCount);
            Assert.AreEqual(target, glance.LastPosition);
            Assert.AreEqual(0.1f + 0.2f + 0.1f, glance.LastDuration, 1e-4f);
        }

        [Test]
        public void NoWorldTarget_NeverRequestsGlance()
        {
            var coordinator = new CoSpeechCoordinator();
            var glance = new FakeGlanceHandler();
            CoSpeechPerformanceReading reading = Reading(1, GestureCueKind.Affirmative, false);

            coordinator.Dispatch(in reading, glance, null);

            Assert.AreEqual(0, glance.CallCount);
        }

        [TestCase(GestureCueKind.Emphatic, BrowCueKind.Flash)]
        [TestCase(GestureCueKind.Greeting, BrowCueKind.SubtleRaise)]
        [TestCase(GestureCueKind.Enumerate, BrowCueKind.SubtleRaise)]
        public void QualifyingCueKind_RaisesMatchingBrowCue(GestureCueKind kind, BrowCueKind expectedBrow)
        {
            var coordinator = new CoSpeechCoordinator();
            var brow = new FakeBrowCueSink();
            CoSpeechPerformanceReading reading = Reading(1, kind, intensity: 0.6f);

            coordinator.Dispatch(in reading, null, brow);

            Assert.AreEqual(1, brow.CallCount);
            Assert.AreEqual(expectedBrow, brow.LastKind);
            Assert.AreEqual(0.6f, brow.LastIntensity, 1e-4f);
        }

        [TestCase(GestureCueKind.Affirmative)]
        [TestCase(GestureCueKind.Negative)]
        [TestCase(GestureCueKind.Uncertain)]
        public void NonQualifyingCueKind_NeverRaisesBrowCue(GestureCueKind kind)
        {
            var coordinator = new CoSpeechCoordinator();
            var brow = new FakeBrowCueSink();
            CoSpeechPerformanceReading reading = Reading(1, kind);

            coordinator.Dispatch(in reading, null, brow);

            Assert.AreEqual(0, brow.CallCount);
        }

        [Test]
        public void SameSequence_DispatchedOnlyOnce()
        {
            var coordinator = new CoSpeechCoordinator();
            var glance = new FakeGlanceHandler();
            CoSpeechPerformanceReading reading = Reading(7, GestureCueKind.Affirmative, true, Vector3.one);

            coordinator.Dispatch(in reading, glance, null);
            coordinator.Dispatch(in reading, glance, null);
            coordinator.Dispatch(in reading, glance, null);

            Assert.AreEqual(1, glance.CallCount, "the same gesture sequence must only ever dispatch once.");
        }

        [Test]
        public void NewSequence_DispatchesAgain()
        {
            var coordinator = new CoSpeechCoordinator();
            var glance = new FakeGlanceHandler();
            CoSpeechPerformanceReading first = Reading(1, GestureCueKind.Affirmative, true, Vector3.one);
            CoSpeechPerformanceReading second = Reading(2, GestureCueKind.Affirmative, true, Vector3.one);

            coordinator.Dispatch(in first, glance, null);
            coordinator.Dispatch(in second, glance, null);

            Assert.AreEqual(2, glance.CallCount);
        }

        [Test]
        public void Reset_AllowsTheSameSequenceToDispatchAgain()
        {
            var coordinator = new CoSpeechCoordinator();
            var glance = new FakeGlanceHandler();
            CoSpeechPerformanceReading reading = Reading(5, GestureCueKind.Affirmative, true, Vector3.one);

            coordinator.Dispatch(in reading, glance, null);
            coordinator.Reset();
            coordinator.Dispatch(in reading, glance, null);

            Assert.AreEqual(2, glance.CallCount, "Reset() must re-arm the latch for the next build's first gesture.");
        }

        [Test]
        public void NullHandlers_DoNotThrow()
        {
            var coordinator = new CoSpeechCoordinator();
            CoSpeechPerformanceReading reading = Reading(1, GestureCueKind.Emphatic, true, Vector3.one);

            Assert.DoesNotThrow(() => coordinator.Dispatch(in reading, null, null));
        }
    }
}
