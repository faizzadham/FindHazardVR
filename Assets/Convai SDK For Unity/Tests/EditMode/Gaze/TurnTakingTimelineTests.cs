using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using Convai.Runtime.Embodiment;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class TurnTakingTimelineTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "planningBreakProbability").floatValue = 1f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(90)]
        [TestCase(120)]
        public void UtteranceTimelines_AreBoundedAcrossFrameRatesAndSeeds(int frameRate)
        {
            float[] shortDurations = { 0.35f, 0.8f, 1.5f };
            for (uint seed = 1; seed <= 64; seed++)
            {
                for (int i = 0; i < shortDurations.Length; i++)
                {
                    Metrics shortMetrics = Simulate(seed, frameRate, shortDurations[i], words: 5, hitch: seed % 2 == 0);
                    Assert.That(shortMetrics.BreakStarts, Is.EqualTo(0), $"short seed={seed} duration={shortDurations[i]}");
                    Assert.That(shortMetrics.Yields, Is.EqualTo(1));
                }

                Metrics medium3 = Simulate(seed, frameRate, 3f, words: 12, hitch: seed % 2 != 0);
                Metrics medium6 = Simulate(seed, frameRate, 6f, words: 18, hitch: seed % 2 == 0);
                Assert.That(medium3.BreakStarts, Is.LessThanOrEqualTo(1));
                Assert.That(medium6.BreakStarts, Is.LessThanOrEqualTo(1));
                Assert.That(medium3.Yields, Is.EqualTo(1));
                Assert.That(medium6.Yields, Is.EqualTo(1));

                Metrics long15 = Simulate(seed, frameRate, 15f, words: 35, hitch: seed % 2 != 0);
                Metrics long30 = Simulate(seed, frameRate, 30f, words: 70, hitch: seed % 2 == 0);
                AssertLongBounds(in long15, frameRate, seed);
                AssertLongBounds(in long30, frameRate, seed);
            }
        }

        [Test]
        public void SameSeedSchedule_IsFrameRateEquivalent()
        {
            for (uint seed = 1; seed <= 32; seed++)
            {
                Metrics hz30 = Simulate(seed, 30, 15f, 35, hitch: false);
                Metrics hz120 = Simulate(seed, 120, 15f, 35, hitch: false);
                Assert.That(hz30.BreakStarts, Is.EqualTo(hz120.BreakStarts), $"seed={seed}");
                Assert.That(Mathf.Abs(hz30.FirstStart - hz120.FirstStart), Is.LessThanOrEqualTo(1f / 30f + 0.01f));
                if (hz30.BreakStarts > 1)
                    Assert.That(Mathf.Abs(hz30.SecondStart - hz120.SecondStart), Is.LessThanOrEqualTo(1f / 30f + 0.01f));
            }
        }

        [Test]
        public void ReactingToShortSpeaking_HasNoPlanningBreak()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(7u);
            const float dt = 1f / 60f;
            for (int i = 0; i < 30; i++)
                director.Tick(DialogueState.Reacting, _profile, false, false, 0, true, false, 0f, dt, ref random);

            for (int i = 0; i < 90; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, true, 1f, dt, ref random);
                Assert.IsFalse(director.PlanningBreakStarted);
                Assert.IsFalse(director.PlanningBreakActive);
            }
        }

        [Test]
        public void ThinkingToShortSpeaking_StillHasNoManufacturedBreak()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(8u);
            const float dt = 1f / 60f;
            for (int i = 0; i < 30; i++)
                director.Tick(DialogueState.Thinking, _profile, false, false, 0, true, false, 0f, dt, ref random);

            for (int i = 0; i < 90; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, i == 0, i == 0 ? 5 : 0,
                    true, true, 1f, dt, ref random);
                Assert.IsFalse(director.PlanningBreakStarted);
                Assert.IsFalse(director.PlanningBreakActive);
            }
        }

        [Test]
        public void FinalAtStart_ActiveSpeechValleyDoesNotYield_ThenDebouncedStopYieldsOnce()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(11u);
            const float dt = 1f / 60f;
            director.Tick(DialogueState.Idle, _profile, false, false, 0, true, false, 0f, dt, ref random);
            director.Tick(DialogueState.Speaking, _profile, false, true, 24, true, true, 1f, dt, ref random);
            Assert.IsFalse(director.WantsYieldBlink);

            bool yielded = false;
            for (int i = 0; i < 120; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, true, 0f, dt, ref random);
                yielded |= director.WantsYieldBlink;
            }

            Assert.IsFalse(yielded, "Authoritative active speech wins over an energy valley.");

            int stopYields = 0;
            for (int i = 0; i < 12; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, false, 0f, dt, ref random);
                if (director.WantsYieldBlink) stopYields++;
            }
            Assert.That(stopYields, Is.EqualTo(1));

            director.Reset();
            for (int i = 0; i < 180; i++)
                director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, true,
                    i < 30 ? 1f : 0f, dt, ref random);
            Assert.IsFalse(director.YieldEngagementPinActive, "An ordinary energy valley is not a speech-stop cue.");
        }

        [Test]
        public void OneFrameSpeechDropout_DoesNotIrreversiblyYield()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(12u);
            const float dt = 1f / 60f;
            director.Tick(DialogueState.Speaking, _profile, false, true, 20, true, true, 1f, dt, ref random);
            director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, false, 0f, dt, ref random);
            Assert.IsFalse(director.WantsYieldBlink);
            director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, true, 1f, dt, ref random);
            Assert.IsFalse(director.WantsYieldBlink);
        }

        [Test]
        public void ResetMidExtendedTurn_ClearsAllCadenceAndYieldState()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(13u);
            const float dt = 1f / 60f;
            for (int i = 0; i < 360; i++)
                director.Tick(DialogueState.Speaking, _profile, false, i == 0, i == 0 ? 30 : 0,
                    true, true, 0.8f, dt, ref random);

            director.Reset();
            for (int i = 0; i < 90; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, i == 0, i == 0 ? 5 : 0,
                    true, true, 0.8f, dt, ref random);
                Assert.IsFalse(director.PlanningBreakStarted);
                Assert.IsFalse(director.PlanningBreakActive);
                Assert.IsFalse(director.WantsYieldBlink);
            }
        }

        [Test]
        public void FirstObservedSpeaking_InitializesFiveSecondMidBreakDeadline()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(131u);
            const float dt = 1f / 60f;
            int framesBeforeFive = Mathf.FloorToInt(4.9f / dt);
            for (int i = 0; i < framesBeforeFive; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, i == 0, i == 0 ? 30 : 0,
                    true, true, 0.8f, dt, ref random);
                Assert.IsFalse(director.PlanningBreakStarted, $"frame={i}");
            }

            bool started = false;
            for (int i = 0; i < 90; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, false, 0,
                    true, true, 0.8f, dt, ref random);
                started |= director.PlanningBreakStarted;
            }
            Assert.IsTrue(started);
        }

        [Test]
        public void TranscriptAndSpeakingEventOrder_ProduceEquivalentOpeningEligibility()
        {
            var a = new TurnTakingDirector();
            var b = new TurnTakingDirector();
            var randomA = new DeterministicEmbodimentRandom(14u);
            var randomB = new DeterministicEmbodimentRandom(14u);
            const float dt = 1f / 60f;
            for (int i = 0; i < 30; i++)
            {
                a.Tick(DialogueState.Thinking, _profile, false, false, 0, true, false, 0f, dt, ref randomA);
                b.Tick(DialogueState.Thinking, _profile, false, false, 0, true, false, 0f, dt, ref randomB);
            }

            a.Tick(DialogueState.Speaking, _profile, false, true, 24, true, true, 1f, dt, ref randomA);
            b.Tick(DialogueState.Speaking, _profile, false, false, 0, true, true, 1f, dt, ref randomB);
            b.Tick(DialogueState.Speaking, _profile, false, true, 24, true, true, 1f, dt, ref randomB);

            int startsA = CountStarts(a, ref randomA, 60);
            int startsB = CountStarts(b, ref randomB, 60);
            Assert.That(startsA, Is.EqualTo(1));
            Assert.That(startsB, Is.EqualTo(1));
        }

        [Test]
        public void ThinkingOriginExtendedTurn_HasAtMostTwoTotalBreaks()
        {
            const float dt = 1f / 60f;
            for (uint seed = 1; seed <= 64; seed++)
            {
                var director = new TurnTakingDirector();
                var random = new DeterministicEmbodimentRandom(seed);
                for (int i = 0; i < 30; i++)
                    director.Tick(DialogueState.Thinking, _profile, false, false, 0,
                        true, false, 0f, dt, ref random);

                int starts = 0;
                for (int i = 0; i < 1800; i++)
                {
                    director.Tick(DialogueState.Speaking, _profile, false, i == 0, i == 0 ? 70 : 0,
                        true, true, 0.7f, dt, ref random);
                    if (director.PlanningBreakStarted) starts++;
                }
                Assert.That(starts, Is.LessThanOrEqualTo(2), $"seed={seed}");
            }
        }

        [Test]
        public void MidTurnBreak_IsNaturalAndEyeLed()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(15u);
            const float dt = 1f / 60f;
            director.Tick(DialogueState.Idle, _profile, false, false, 0, true, false, 0f, dt, ref random);
            for (int i = 0; i < 400; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, i == 0, i == 0 ? 30 : 0,
                    true, true, 0.7f, dt, ref random);
                if (!director.PlanningBreakStarted) continue;
                Assert.That(director.StartedBreakKind, Is.EqualTo(TurnTakingBreakKind.MidTurn));
                Assert.That(director.StartedAversionMode, Is.EqualTo(GazeAversionMode.Natural));
                Assert.That(director.HeadParticipationScale, Is.InRange(0.2f, 0.35f));
                return;
            }
            Assert.Fail("Expected the probability-1 extended turn to schedule a mid-turn break.");
        }

        [Test]
        public void HotLoop_DoesNotAllocateManagedMemory()
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(99u);
            const float dt = 1f / 60f;
            for (int i = 0; i < 256; i++)
                director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, true, 0.6f, dt, ref random);

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 10000; i++)
                    director.Tick(DialogueState.Speaking, _profile, false, false, 0, true, true, 0.6f, dt, ref random);
            });

            Assert.That(allocationCount, Is.EqualTo(0));
        }

        [Test]
        public void TurnTakingSpeakingOwner_BlocksProxemicDoubleCadence()
        {
            Assert.That(ConvaiGazeController.ComposeNaturalSpeakingAversion(
                0.4f, 0f, 0.7f, true, turnTakingOwnsSpeaking: true), Is.EqualTo(0f));
            Assert.That(ConvaiGazeController.ComposeNaturalSpeakingAversion(
                0.4f, 0f, 0.7f, true, turnTakingOwnsSpeaking: false), Is.EqualTo(0.7f));
        }

        [Test]
        public void TranscriptWordCount_IsAllocationFreeAndHandlesPunctuation()
        {
            string transcript = "Hello, world! XR-ready characters don't freeze: 42 reasons.";
            Assert.That(ConvaiGazeController.CountTranscriptWords(transcript), Is.EqualTo(9));
            Assert.That(ConvaiGazeController.CountTranscriptWords(null), Is.EqualTo(0));

            for (int i = 0; i < 64; i++) ConvaiGazeController.CountTranscriptWords(transcript);
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 10000; i++) ConvaiGazeController.CountTranscriptWords(transcript);
            });
            Assert.That(allocationCount, Is.EqualTo(0));
        }

        private Metrics Simulate(uint seed, int frameRate, float duration, int words, bool hitch)
        {
            var director = new TurnTakingDirector();
            var random = new DeterministicEmbodimentRandom(seed);
            float baseDt = 1f / frameRate;
            director.Tick(DialogueState.Idle, _profile, false, false, 0, true, false, 0f, baseDt, ref random);

            var result = new Metrics { MinStartGap = float.PositiveInfinity };
            float time = 0f;
            bool hitchUsed = false;
            float previousStart = float.NegativeInfinity;
            while (time < duration)
            {
                float dt = hitch && !hitchUsed && time >= duration * 0.4f ? 0.1f : baseDt;
                hitchUsed |= dt > baseDt * 1.5f;
                bool final = time <= 0f;
                director.Tick(DialogueState.Speaking, _profile, false, final, final ? words : 0,
                    true, true, 0.7f, dt, ref random);
                if (director.PlanningBreakStarted)
                {
                    if (result.BreakStarts == 0) result.FirstStart = time;
                    else if (result.BreakStarts == 1) result.SecondStart = time;
                    result.BreakStarts++;
                    float gap = time - previousStart;
                    if (!float.IsNegativeInfinity(previousStart)) result.MinStartGap = Mathf.Min(result.MinStartGap, gap);
                    previousStart = time;
                }
                if (director.PlanningBreakActive)
                {
                    result.BreakDutySeconds += dt;
                    result.CurrentActiveRun += dt;
                    result.MaxActiveRun = Mathf.Max(result.MaxActiveRun, result.CurrentActiveRun);
                }
                else
                {
                    result.CurrentActiveRun = 0f;
                }
                if (director.WantsYieldBlink) result.Yields++;
                time += dt;
            }

            int stopFrames = Mathf.CeilToInt(0.25f / baseDt);
            for (int i = 0; i < stopFrames; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, false, 0,
                    true, false, 0f, baseDt, ref random);
                if (director.WantsYieldBlink) result.Yields++;
            }
            result.Duration = duration;
            return result;
        }

        private int CountStarts(
            TurnTakingDirector director,
            ref DeterministicEmbodimentRandom random,
            int frames)
        {
            int starts = 0;
            const float dt = 1f / 60f;
            for (int i = 0; i < frames; i++)
            {
                director.Tick(DialogueState.Speaking, _profile, false, false, 0,
                    true, true, 1f, dt, ref random);
                if (director.PlanningBreakStarted) starts++;
            }
            return starts;
        }

        private static void AssertLongBounds(in Metrics metrics, int frameRate, uint seed)
        {
            Assert.That(metrics.BreakStarts, Is.LessThanOrEqualTo(2), $"seed={seed} hz={frameRate}");
            Assert.That(metrics.BreakDutySeconds / metrics.Duration, Is.LessThanOrEqualTo(0.1f));
            Assert.That(metrics.MaxActiveRun, Is.LessThanOrEqualTo(0.7f));
            if (metrics.BreakStarts > 1)
                Assert.That(metrics.MinStartGap, Is.GreaterThanOrEqualTo(4.5f - 1f / frameRate));
            Assert.That(metrics.Yields, Is.EqualTo(1));
        }

        private struct Metrics
        {
            public int BreakStarts;
            public int Yields;
            public float BreakDutySeconds;
            public float Duration;
            public float MinStartGap;
            public float FirstStart;
            public float SecondStart;
            public float CurrentActiveRun;
            public float MaxActiveRun;
        }
    }
}
