using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyLanguage.Core.Gestures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="HeadGestureDirector" />: program shapes (bounded, C¹ endpoints),
    ///     refractory/queueing semantics, determinism, intensity scaling, and reset.
    /// </summary>
    public sealed class HeadGestureDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float NodMaxPitch = 8f;
        private const float ShakeMaxYaw = 9f;
        private const float TiltMaxRoll = 6f;
        private const float Refractory = 0.6f;
        private const float RefractoryVariance = 0f; // deterministic by default; variance covered separately

        // The lobed-decay normalization constant (shared with Gaze's shipped BackchannelDirector
        // recipe) crests at ~1.0018× rather than exactly 1.0 — a deliberate inherited constant,
        // not a bug — so amplitude-bound assertions allow a small proportional slack instead of
        // a tight absolute epsilon.
        private const float AmplitudeOvershootTolerance = 1.01f;

        private static void TickN(HeadGestureDirector director, int n)
        {
            for (int i = 0; i < n; i++)
                director.Tick(Dt, NodMaxPitch, ShakeMaxYaw, TiltMaxRoll, Refractory, RefractoryVariance);
        }

        [Test]
        public void TryRequest_WhenIdle_StartsImmediately()
        {
            var director = new HeadGestureDirector();

            bool accepted = director.TryRequest(HeadGestureKind.Nod, 1f);

            Assert.IsTrue(accepted);
            Assert.IsTrue(director.IsPlaying);
            Assert.AreEqual(HeadGestureKind.Nod, director.ActiveKind);
        }

        [Test]
        public void Nod_StartsAndEndsAtZeroOffset_WithSmallBoundaryDeltas()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);

            // First step: the boundary delta (change from the exact-zero start) must be small —
            // a C¹-continuous envelope has zero derivative at p=0, so one small timestep must
            // not produce a large jump.
            HeadGestureOffset beforeFirstStep = director.Current;
            Assert.That(beforeFirstStep.Weight, Is.EqualTo(0f), "Before any Tick, the offset must be at rest.");

            TickN(director, 1);
            HeadGestureOffset afterFirstStep = director.Current;
            Assert.That(System.Math.Abs(afterFirstStep.PitchDegrees), Is.LessThan(0.5f),
                "Zero-velocity start: the first tick's pitch must stay near zero, not jump to peak.");

            // Run to completion.
            int maxSteps = 300;
            int steps = 0;
            while (director.IsPlaying && steps < maxSteps)
            {
                TickN(director, 1);
                steps++;
            }

            Assert.IsFalse(director.IsPlaying, "The Nod program must complete within a bounded number of ticks.");
            HeadGestureOffset atEnd = director.Current;
            Assert.AreEqual(0f, atEnd.PitchDegrees, "Offset must return to exactly zero pitch on completion.");
            Assert.AreEqual(0f, atEnd.Weight, "Weight must return to exactly zero on completion.");
        }

        [Test]
        public void Nod_NeverExceedsAmplitudeMaximum()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);

            float maxAbsPitch = 0f;
            for (int i = 0; i < 200 && director.IsPlaying; i++)
            {
                TickN(director, 1);
                maxAbsPitch = System.Math.Max(maxAbsPitch, System.Math.Abs(director.Current.PitchDegrees));
            }

            Assert.That(maxAbsPitch, Is.LessThanOrEqualTo(NodMaxPitch * AmplitudeOvershootTolerance),
                "Nod pitch must never exceed the configured amplitude maximum (within the shared normalization tolerance).");
        }

        [Test]
        public void Shake_NeverExceedsAmplitudeMaximum_AndAlternatesSign()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Shake, 1f);

            float maxAbsYaw = 0f;
            bool sawPositive = false, sawNegative = false;
            for (int i = 0; i < 200 && director.IsPlaying; i++)
            {
                TickN(director, 1);
                float yaw = director.Current.YawDegrees;
                maxAbsYaw = System.Math.Max(maxAbsYaw, System.Math.Abs(yaw));
                if (yaw > 0.5f) sawPositive = true;
                if (yaw < -0.5f) sawNegative = true;
            }

            Assert.That(maxAbsYaw, Is.LessThanOrEqualTo(ShakeMaxYaw * AmplitudeOvershootTolerance),
                "Shake yaw must never exceed the configured amplitude maximum (within the shared normalization tolerance).");
            Assert.IsTrue(sawPositive && sawNegative, "Shake must alternate sign (left-right), not stay one-signed.");
        }

        [Test]
        public void Tilt_NeverExceedsAmplitudeMaximum_AndHoldsMidCourse()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Tilt, 1f);

            float maxAbsRoll = 0f;
            for (int i = 0; i < 300 && director.IsPlaying; i++)
            {
                TickN(director, 1);
                maxAbsRoll = System.Math.Max(maxAbsRoll, System.Math.Abs(director.Current.RollDegrees));
            }

            Assert.That(maxAbsRoll, Is.LessThanOrEqualTo(TiltMaxRoll + 1e-3f),
                "Tilt roll must never exceed the configured amplitude maximum.");
            Assert.That(maxAbsRoll, Is.GreaterThan(TiltMaxRoll * 0.9f),
                "Tilt must reach (and hold at) close to its full amplitude, not just graze it.");
        }

        [Test]
        public void IntensityScaling_HalvesPeakAmplitude()
        {
            var fullIntensity = new HeadGestureDirector();
            fullIntensity.TryRequest(HeadGestureKind.Nod, 1f);
            var halfIntensity = new HeadGestureDirector();
            halfIntensity.TryRequest(HeadGestureKind.Nod, 0.5f);

            float maxFull = 0f, maxHalf = 0f;
            for (int i = 0; i < 200; i++)
            {
                TickN(fullIntensity, 1);
                TickN(halfIntensity, 1);
                maxFull = System.Math.Max(maxFull, System.Math.Abs(fullIntensity.Current.PitchDegrees));
                maxHalf = System.Math.Max(maxHalf, System.Math.Abs(halfIntensity.Current.PitchDegrees));
            }

            Assert.That(maxHalf, Is.EqualTo(maxFull * 0.5f).Within(0.05f),
                "Halved intensity must halve the peak amplitude.");
        }

        [Test]
        public void TryRequest_DuringIdleRefractory_QueuesInsteadOfStartingImmediately()
        {
            // Adversarial-review regression: a request arriving AFTER a program completed but
            // while its post-completion refractory is still draining must not bypass the
            // refractory — it parks in the pending slot and starts only once the window elapses.
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);

            // Run ONLY until the program completes — a fixed large tick count would drain the
            // 0.6s refractory too, and the request below would then be legitimately allowed to
            // start immediately (the original version of this test made exactly that mistake).
            int steps = 0;
            while (director.IsPlaying && steps++ < 300)
                TickN(director, 1);
            Assert.IsFalse(director.IsPlaying, "Precondition: the program must have completed.");
            TickN(director, 2); // two ticks into the draining refractory window

            bool accepted = director.TryRequest(HeadGestureKind.Shake, 1f);

            Assert.IsTrue(accepted, "A request during the idle refractory is accepted (queued).");
            Assert.IsFalse(director.IsPlaying,
                "It must NOT start immediately — the refractory window is still draining.");

            bool refused = director.TryRequest(HeadGestureKind.Tilt, 1f);
            Assert.IsFalse(refused, "The single pending slot is occupied — further requests refuse.");

            TickN(director, Mathf.CeilToInt(Refractory / Dt) + 2);
            Assert.IsTrue(director.IsPlaying, "The queued request starts once the refractory elapses.");
            Assert.AreEqual(HeadGestureKind.Shake, director.ActiveKind);
        }

        [Test]
        public void TryRequest_WhileActive_QueuesOnePending()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);

            bool queued = director.TryRequest(HeadGestureKind.Shake, 1f);

            Assert.IsTrue(queued, "A request while one program is active (and none pending) must be queued.");
            Assert.IsTrue(director.HasPending);
        }

        [Test]
        public void TryRequest_WhileActiveAndPendingSlotFull_IsRefused()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);
            director.TryRequest(HeadGestureKind.Shake, 1f);

            bool refused = director.TryRequest(HeadGestureKind.Tilt, 1f);

            Assert.IsFalse(refused, "A third request while one is active and one is pending must be refused outright.");
        }

        // ── Co-speech beat semantics: fire-now-or-drop, never queued ──

        [Test]
        public void TryRequestBeat_WhenIdle_StartsImmediatelyWithBeatDuration()
        {
            var director = new HeadGestureDirector();

            bool accepted = director.TryRequestBeat(HeadGestureKind.Nod, 1f);

            Assert.IsTrue(accepted);
            Assert.IsTrue(director.IsPlaying);
            Assert.IsTrue(director.ActiveIsBeat, "A beat request must be observable as a beat program.");
            // A beat's duration is a per-beat random draw in [BeatDurationMinSeconds,
            // BeatDurationMaxSeconds] — was a fixed BeatDurationSeconds
            // (0.35s); tick past the MAX of that range (+ margin) so this assertion
            // holds regardless of the draw.
            TickN(director, Mathf.CeilToInt(HeadGestureDirector.BeatDurationMaxSeconds / Dt) + 2);
            Assert.IsFalse(director.IsPlaying, "A beat must finish within BeatDurationMaxSeconds.");
        }

        [Test]
        public void TryRequestBeat_WhileActive_IsDroppedNotQueued()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);

            bool accepted = director.TryRequestBeat(HeadGestureKind.Nod, 1f);

            Assert.IsFalse(accepted, "A beat that cannot start now is off-rhythm by definition — drop, never queue.");
            Assert.IsFalse(director.HasPending, "A dropped beat must not occupy the pending slot.");
        }

        [Test]
        public void TryRequestBeat_DuringRefractory_IsDroppedNotQueued()
        {
            var director = new HeadGestureDirector();
            director.TryRequestBeat(HeadGestureKind.Nod, 1f);
            // Run the beat to completion so only the post-completion refractory remains.
            TickN(director, Mathf.CeilToInt(HeadGestureDirector.BeatDurationMaxSeconds / Dt) + 2);
            Assert.IsFalse(director.IsPlaying);

            bool accepted = director.TryRequestBeat(HeadGestureKind.Nod, 1f);

            Assert.IsFalse(accepted, "A beat during the refractory window must be dropped, not queued.");
            Assert.IsFalse(director.HasPending);
        }

        [Test]
        public void ScriptedRequest_StillQueuesAfterBeatWasDropped()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);
            director.TryRequestBeat(HeadGestureKind.Nod, 1f); // dropped — must not consume the slot

            bool queued = director.TryRequest(HeadGestureKind.Shake, 1f);

            Assert.IsTrue(queued, "Scripted requests win the pending slot — a dropped beat must not block them.");
            Assert.IsTrue(director.HasPending);
        }

        [Test]
        public void PendingRequest_StartsAfterActiveCompletesAndRefractoryElapses()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);
            director.TryRequest(HeadGestureKind.Shake, 1f);

            // Run the Nod to completion.
            int steps = 0;
            while (director.IsPlaying && director.ActiveKind == HeadGestureKind.Nod && steps < 300)
            {
                TickN(director, 1);
                steps++;
            }

            Assert.IsFalse(director.IsPlaying, "Sanity: Nod must have completed before the refractory check.");

            // During the refractory window, the pending Shake must not start yet.
            TickN(director, 1);
            Assert.IsFalse(director.IsPlaying && director.ActiveKind == HeadGestureKind.Shake,
                "The pending program must not start before the refractory window elapses.");

            // After waiting out the refractory window (with zero variance, exactly Refractory seconds).
            int refractoryTicks = (int)(Refractory / Dt) + 2;
            TickN(director, refractoryTicks);

            Assert.IsTrue(director.IsPlaying, "The pending Shake must start once the refractory window elapses.");
            Assert.AreEqual(HeadGestureKind.Shake, director.ActiveKind);
        }

        [Test]
        public void Determinism_SameSeedAndSequence_ProducesIdenticalOffsets()
        {
            // Nonzero refractory variance so this test actually exercises the seeded
            // DeterministicEmbodimentRandom draw (TickN's shared constants use zero variance,
            // which would make this pass trivially even with a broken RNG).
            const float refractoryVariance = 0.4f;

            var directorA = new HeadGestureDirector();
            directorA.Seed(12345);
            var directorB = new HeadGestureDirector();
            directorB.Seed(12345);

            directorA.TryRequest(HeadGestureKind.Nod, 1f);
            directorB.TryRequest(HeadGestureKind.Nod, 1f);
            directorA.TryRequest(HeadGestureKind.Shake, 1f);
            directorB.TryRequest(HeadGestureKind.Shake, 1f);

            for (int i = 0; i < 400; i++)
            {
                directorA.Tick(Dt, NodMaxPitch, ShakeMaxYaw, TiltMaxRoll, Refractory, refractoryVariance);
                directorB.Tick(Dt, NodMaxPitch, ShakeMaxYaw, TiltMaxRoll, Refractory, refractoryVariance);

                HeadGestureOffset a = directorA.Current;
                HeadGestureOffset b = directorB.Current;
                Assert.AreEqual(a.PitchDegrees, b.PitchDegrees, 1e-6f, $"Pitch mismatch at tick {i}");
                Assert.AreEqual(a.YawDegrees, b.YawDegrees, 1e-6f, $"Yaw mismatch at tick {i}");
                Assert.AreEqual(a.RollDegrees, b.RollDegrees, 1e-6f, $"Roll mismatch at tick {i}");
                Assert.AreEqual(a.Weight, b.Weight, 1e-6f, $"Weight mismatch at tick {i}");
            }
        }

        [Test]
        public void Determinism_DifferentSeeds_ProduceDifferentRefractoryTiming()
        {
            // Confirms the seed actually influences output (guards against a no-op RNG):
            // two different seeds with nonzero variance must diverge in WHEN the pending Shake
            // starts after the Nod completes.
            const float refractoryVariance = 0.4f;

            var directorA = new HeadGestureDirector();
            directorA.Seed(111);
            var directorB = new HeadGestureDirector();
            directorB.Seed(222);

            directorA.TryRequest(HeadGestureKind.Nod, 1f);
            directorB.TryRequest(HeadGestureKind.Nod, 1f);
            directorA.TryRequest(HeadGestureKind.Shake, 1f);
            directorB.TryRequest(HeadGestureKind.Shake, 1f);

            int shakeStartTickA = -1, shakeStartTickB = -1;
            for (int i = 0; i < 400 && (shakeStartTickA < 0 || shakeStartTickB < 0); i++)
            {
                directorA.Tick(Dt, NodMaxPitch, ShakeMaxYaw, TiltMaxRoll, Refractory, refractoryVariance);
                directorB.Tick(Dt, NodMaxPitch, ShakeMaxYaw, TiltMaxRoll, Refractory, refractoryVariance);

                if (shakeStartTickA < 0 && directorA.IsPlaying && directorA.ActiveKind == HeadGestureKind.Shake)
                    shakeStartTickA = i;
                if (shakeStartTickB < 0 && directorB.IsPlaying && directorB.ActiveKind == HeadGestureKind.Shake)
                    shakeStartTickB = i;
            }

            Assert.That(shakeStartTickA, Is.GreaterThanOrEqualTo(0), "Sanity: Shake must have started for seed A.");
            Assert.That(shakeStartTickB, Is.GreaterThanOrEqualTo(0), "Sanity: Shake must have started for seed B.");
            Assert.AreNotEqual(shakeStartTickA, shakeStartTickB,
                "Different seeds must produce different refractory variance draws (anti-metronome).");
        }

        // ── Per-beat duration/amplitude variance and neck-lead sequencing ──

        [Test]
        public void TryRequestBeat_DurationIsDrawnInRange_AndDeterministicUnderFixedSeed()
        {
            var directorA = new HeadGestureDirector();
            directorA.Seed(555);
            var directorB = new HeadGestureDirector();
            directorB.Seed(555);

            directorA.TryRequestBeat(HeadGestureKind.Nod, 1f);
            directorB.TryRequestBeat(HeadGestureKind.Nod, 1f);

            int stepsA = 0, stepsB = 0;
            while (directorA.IsPlaying && stepsA < 100) { TickN(directorA, 1); stepsA++; }
            while (directorB.IsPlaying && stepsB < 100) { TickN(directorB, 1); stepsB++; }

            float secondsA = stepsA * Dt;
            float secondsB = stepsB * Dt;

            Assert.That(secondsA,
                Is.InRange(HeadGestureDirector.BeatDurationMinSeconds - Dt, HeadGestureDirector.BeatDurationMaxSeconds + Dt),
                "The drawn beat duration must fall within [BeatDurationMinSeconds, BeatDurationMaxSeconds].");
            Assert.That(secondsA, Is.EqualTo(secondsB).Within(Dt),
                "Identical seed must draw an identical beat duration (determinism).");
        }

        [Test]
        public void TryRequestBeat_TimeToPeak_IsWithin200Milliseconds()
        {
            var director = new HeadGestureDirector();
            director.Seed(555);
            director.TryRequestBeat(HeadGestureKind.Nod, 1f);

            float peakAbsPitch = 0f;
            float peakTimeSeconds = 0f;
            float elapsed = 0f;
            for (int i = 0; i < 100 && director.IsPlaying; i++)
            {
                TickN(director, 1);
                elapsed += Dt;
                float absPitch = Mathf.Abs(director.Current.PitchDegrees);
                if (absPitch > peakAbsPitch)
                {
                    peakAbsPitch = absPitch;
                    peakTimeSeconds = elapsed;
                }
            }

            Assert.That(peakTimeSeconds, Is.LessThanOrEqualTo(0.2f),
                "BeatNod's early (30%-of-duration) peak must land within the fast channel's latency budget regardless of the drawn duration.");
        }

        [Test]
        public void TryRequestBeat_AmplitudeScaleVariesBetweenSuccessiveBeats()
        {
            var director = new HeadGestureDirector();
            director.Seed(777);

            director.TryRequestBeat(HeadGestureKind.Nod, 0.8f);
            float peak1 = 0f;
            for (int i = 0; i < 100 && director.IsPlaying; i++)
            {
                TickN(director, 1);
                peak1 = Mathf.Max(peak1, Mathf.Abs(director.Current.PitchDegrees));
            }
            Assert.IsFalse(director.IsPlaying, "Sanity: the first beat must have completed.");

            TickN(director, Mathf.CeilToInt(Refractory / Dt) + 5); // drain the post-completion refractory

            director.TryRequestBeat(HeadGestureKind.Nod, 0.8f);
            float peak2 = 0f;
            for (int i = 0; i < 100 && director.IsPlaying; i++)
            {
                TickN(director, 1);
                peak2 = Mathf.Max(peak2, Mathf.Abs(director.Current.PitchDegrees));
            }

            Assert.That(peak1, Is.Not.EqualTo(peak2).Within(1e-4f),
                "Two beats at the same intensity must draw different per-beat amplitude scales — peak pitch must differ.");
        }

        [Test]
        public void CurrentNeckLead_PeaksEarlierThanHeadOffset_DuringABeat_AndIsNoneWhenIdle()
        {
            var director = new HeadGestureDirector();
            director.Seed(999);

            Assert.AreEqual(0f, director.CurrentNeckLead.Weight, "Idle (never requested): neck lead must be None.");

            director.TryRequestBeat(HeadGestureKind.Nod, 1f);

            float headPeak = 0f;
            int headPeakTick = -1;
            float neckPeak = 0f;
            int neckPeakTick = -1;
            int tick = 0;
            while (director.IsPlaying && tick < 100)
            {
                TickN(director, 1);
                float headAbs = Mathf.Abs(director.Current.PitchDegrees);
                float neckAbs = Mathf.Abs(director.CurrentNeckLead.PitchDegrees);
                if (headAbs > headPeak) { headPeak = headAbs; headPeakTick = tick; }
                if (neckAbs > neckPeak) { neckPeak = neckAbs; neckPeakTick = tick; }
                tick++;
            }

            Assert.IsFalse(director.IsPlaying, "Sanity: the beat must have completed.");
            Assert.That(headPeakTick, Is.GreaterThanOrEqualTo(0), "Sanity: the head offset must have moved during the beat.");
            Assert.That(neckPeakTick, Is.GreaterThanOrEqualTo(0), "Sanity: the neck-lead offset must have moved during the beat.");
            Assert.That(neckPeakTick, Is.LessThan(headPeakTick),
                "Proximal-to-distal sequencing: the neck-lead offset must peak at an earlier tick than the head offset.");

            Assert.AreEqual(0f, director.CurrentNeckLead.Weight, "After completion, the neck lead must return to None.");
        }

        [Test]
        public void Reset_StopsActiveAndPending_ClearsOffset()
        {
            var director = new HeadGestureDirector();
            director.TryRequest(HeadGestureKind.Nod, 1f);
            director.TryRequest(HeadGestureKind.Shake, 1f);
            TickN(director, 5);

            director.Reset();

            Assert.IsFalse(director.IsPlaying);
            Assert.IsFalse(director.HasPending);
            Assert.AreEqual(0f, director.Current.Weight);

            // A fresh request after Reset must be accepted immediately (idle state restored).
            Assert.IsTrue(director.TryRequest(HeadGestureKind.Tilt, 1f));
        }
    }
}
