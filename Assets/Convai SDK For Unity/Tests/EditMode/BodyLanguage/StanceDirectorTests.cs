using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Tests.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="StanceDirector" />: the weight-shift/stance schedule —
    ///     alternating sides, the Thinking asymmetric hold, suppression freeze/decay, the
    ///     post-suppression settle step, output slewing, and determinism.
    /// </summary>
    public sealed class StanceDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float IntervalSeconds = 6f;
        private const float IntervalVariance = 1f;
        private const float TransferSeconds = 1f;

        private static void TickActive(
            StanceDirector director,
            DialogueState state = DialogueState.Idle,
            bool enabled = true,
            GestureSuppression suppression = GestureSuppression.None) =>
            director.Tick(state, enabled, suppression, IntervalSeconds, IntervalVariance, TransferSeconds, Dt);

        [Test]
        public void Determinism_SameSeed_ProducesIdenticalSchedule()
        {
            var directorA = new StanceDirector();
            directorA.Seed(4242);
            var directorB = new StanceDirector();
            directorB.Seed(4242);

            for (int i = 0; i < 60 * 40; i++)
            {
                TickActive(directorA);
                TickActive(directorB);
                Assert.That(directorA.PelvisLateral01, Is.EqualTo(directorB.PelvisLateral01),
                    $"Identical seed + tick sequence must produce an identical stance schedule at tick {i}.");
                Assert.That(directorA.PelvisYaw01, Is.EqualTo(directorB.PelvisYaw01));
            }
        }

        [Test]
        public void OverTime_AlternatesSides()
        {
            var director = new StanceDirector();
            director.Seed(7);

            bool sawPositive = false;
            bool sawNegative = false;

            for (int i = 0; i < 60 * 60; i++) // 60 simulated seconds — several shift cycles.
            {
                TickActive(director);
                if (director.PelvisLateral01 > 0.4f) sawPositive = true;
                if (director.PelvisLateral01 < -0.4f) sawNegative = true;
            }

            Assert.IsTrue(sawPositive, "The stance schedule must shift weight onto the positive side at some point.");
            Assert.IsTrue(sawNegative, "The stance schedule must shift weight onto the negative side at some point.");
        }

        [Test]
        public void Thinking_HoldsAnAsymmetricStance_WithoutRescheduling()
        {
            var director = new StanceDirector();
            director.Seed(3);

            // Warm up in Idle so a shift schedule is already running.
            for (int i = 0; i < 60 * 3; i++)
                TickActive(director);

            for (int i = 0; i < 60 * 2; i++)
                TickActive(director, state: DialogueState.Thinking);

            float thinkingLateral = director.PelvisLateral01;
            Assert.That(System.Math.Abs(thinkingLateral), Is.GreaterThan(0.3f),
                "Thinking must hold a visibly asymmetric stance.");

            // Continue for a long time while still Thinking — the target must never re-schedule
            // (the held value stays put once slewed in, no further alternation).
            for (int i = 0; i < 60 * 30; i++)
                TickActive(director, state: DialogueState.Thinking);

            Assert.That(director.PelvisLateral01, Is.EqualTo(thinkingLateral).Within(0.02f),
                "Thinking must hold the SAME asymmetric target for the whole state — no rescheduling.");
        }

        [Test]
        public void SuppressionNotNone_FreezesSchedulingAndDecaysToZero()
        {
            var director = new StanceDirector();
            director.Seed(5);

            // Get a shift running first.
            float engaged = 0f;
            for (int i = 0; i < 60 * 30 && engaged < 0.4f; i++)
            {
                TickActive(director);
                engaged = System.Math.Abs(director.PelvisLateral01);
            }
            Assert.That(engaged, Is.GreaterThan(0.4f), "Sanity: a shift engaged before suppressing.");

            for (int i = 0; i < 60 * 5; i++)
                TickActive(director, suppression: GestureSuppression.UpperBody);

            Assert.That(director.PelvisLateral01, Is.EqualTo(0f).Within(0.01f),
                "Suppression must decay the pelvis lateral output to (near) zero.");
            Assert.That(director.PelvisYaw01, Is.EqualTo(0f).Within(0.01f),
                "Suppression must decay the pelvis yaw output to (near) zero.");
        }

        [Test]
        public void SettleStep_FiresSoonAfterALongSuppressionLifts()
        {
            var director = new StanceDirector();
            director.Seed(9);

            for (int i = 0; i < 60 * 3; i++)
                TickActive(director);

            // Suppress for well over the 2s settle threshold.
            for (int i = 0; i < 60 * 3; i++)
                TickActive(director, suppression: GestureSuppression.FullBody);

            // After release, a settle step must fire within ~1.5s (well under the normal interval).
            //  (stance pre-load): FireShift now stashes the lateral/yaw retarget as
            // PENDING for an additional ~0.25s pre-load window (obliquity leads instead) before it
            // actually reaches PelvisLateral01 — widened from 2s to 3s so the worst-case schedule
            // draw (up to 1.5s) plus that pre-load delay still leaves comfortable slew room, rather
            // than the test's timing margin colliding with the new deliberate delay.
            bool sawShiftWithinSettleWindow = false;
            for (int i = 0; i < 60 * 3; i++) // 3 simulated seconds
            {
                TickActive(director);
                if (System.Math.Abs(director.PelvisLateral01) > 0.15f) sawShiftWithinSettleWindow = true;
            }

            Assert.IsTrue(sawShiftWithinSettleWindow,
                "A settle step must schedule soon (0.5-1.5s) after a suppression of >= 2s lifts, well before the normal interval would fire.");
        }

        [Test]
        public void SettleStep_RichnessGainZero_SuppressesTheSettleStepMagnitude()
        {
            // richness gates the settle-step magnitude — at richnessGain == 0
            // (Subtle) the post-suppression settle step must stay effectively silent, unlike the
            // Natural (richnessGain == 1) case covered by SettleStep_FiresSoonAfterALongSuppressionLifts.
            var director = new StanceDirector();
            director.Seed(9);

            for (int i = 0; i < 60 * 3; i++)
                director.Tick(DialogueState.Idle, true, GestureSuppression.None, IntervalSeconds, IntervalVariance, TransferSeconds, Dt, 0f);

            for (int i = 0; i < 60 * 3; i++)
                director.Tick(DialogueState.Idle, true, GestureSuppression.FullBody, IntervalSeconds, IntervalVariance, TransferSeconds, Dt, 0f);

            bool sawMeaningfulShift = false;
            for (int i = 0; i < 60 * 2; i++)
            {
                director.Tick(DialogueState.Idle, true, GestureSuppression.None, IntervalSeconds, IntervalVariance, TransferSeconds, Dt, 0f);
                if (System.Math.Abs(director.PelvisLateral01) > 0.15f) sawMeaningfulShift = true;
            }

            Assert.IsFalse(sawMeaningfulShift,
                "richnessGain == 0 must scale the settle-step magnitude to (near) zero.");
        }

        [Test]
        public void OutputsSlew_NoImplausibleSingleFrameJump()
        {
            var director = new StanceDirector();
            director.Seed(11);

            float previous = director.PelvisLateral01;
            for (int i = 0; i < 60 * 60; i++)
            {
                TickActive(director);
                float current = director.PelvisLateral01;
                Assert.That(System.Math.Abs(current - previous), Is.LessThan(0.2f),
                    $"A single tick must never jump the pelvis lateral output by more than a small slew step (tick {i}).");
                previous = current;
            }
        }

        [Test]
        public void Reset_ReturnsToZeroAndClearsSchedule()
        {
            var director = new StanceDirector();
            director.Seed(1);

            for (int i = 0; i < 60 * 10; i++)
                TickActive(director);

            director.Reset();

            Assert.That(director.PelvisLateral01, Is.EqualTo(0f));
            Assert.That(director.PelvisYaw01, Is.EqualTo(0f));
            Assert.That(director.SpineCounterLateral01, Is.EqualTo(0f));
        }

        [Test]
        public void SpineCounterLateral_IsOppositeAndScaledFromPelvisLateral()
        {
            var director = new StanceDirector();
            director.Seed(13);

            for (int i = 0; i < 60 * 20; i++)
            {
                TickActive(director);
                Assert.That(director.SpineCounterLateral01, Is.EqualTo(-director.PelvisLateral01 * 0.65f).Within(1e-4f));
            }
        }

        [Test]
        public void ZeroAllocation_SteadyStateTick()
        {
            var director = new StanceDirector();
            director.Seed(21);

            for (int i = 0; i < 500; i++) TickActive(director);

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 500; i++) TickActive(director);
            });

            Assert.That(allocationCount, Is.EqualTo(0L), "StanceDirector.Tick must produce zero managed allocation events in steady state.");
        }

        // ── Stance pre-load / anticipation ─────────────────

        [Test]
        public void StancePreLoad_ObliquityStartsMoving_StrictlyBeforeLateral_OnAFiredShift()
        {
            var director = new StanceDirector();
            director.Seed(17);

            float prevObliquity = director.PelvisObliquity01;
            float prevLateral = director.PelvisLateral01;
            int obliquityFirstMovedTick = -1;
            int lateralFirstMovedTick = -1;

            for (int i = 0; i < 60 * 30 && (obliquityFirstMovedTick < 0 || lateralFirstMovedTick < 0); i++)
            {
                TickActive(director);

                if (obliquityFirstMovedTick < 0 && System.Math.Abs(director.PelvisObliquity01 - prevObliquity) > 1e-5f)
                    obliquityFirstMovedTick = i;
                if (lateralFirstMovedTick < 0 && System.Math.Abs(director.PelvisLateral01 - prevLateral) > 1e-5f)
                    lateralFirstMovedTick = i;

                prevObliquity = director.PelvisObliquity01;
                prevLateral = director.PelvisLateral01;
            }

            Assert.That(obliquityFirstMovedTick, Is.GreaterThanOrEqualTo(0), "Sanity: a shift must have fired within 30 simulated seconds.");
            Assert.That(lateralFirstMovedTick, Is.GreaterThanOrEqualTo(0), "Sanity: lateral must eventually start moving too.");
            Assert.That(obliquityFirstMovedTick, Is.LessThan(lateralFirstMovedTick),
                "Stance pre-load: obliquity must start moving strictly before lateral on a fired shift — the body loads the hip before it shifts weight onto it.");
        }

        [Test]
        public void SteadyState_ObliquityEqualsLateral_AfterSettling()
        {
            var director = new StanceDirector();
            director.Seed(23);

            const float intervalSeconds = 10f;
            const float intervalVariance = 0f; // Deterministic timing — isolates the settle window from schedule jitter.
            const float transferSeconds = 1f;

            // Drive well past the one deterministic shift (fires at exactly t=10s) — its pre-load
            // window (0.25s) plus several slew time constants (tau = transferSeconds/3) — while
            // staying safely inside the ~10s gap before the next scheduled shift.
            for (int i = 0; i < 60 * 15; i++)
                director.Tick(DialogueState.Idle, true, GestureSuppression.None, intervalSeconds, intervalVariance, transferSeconds, Dt);

            Assert.That(director.PelvisObliquity01, Is.EqualTo(director.PelvisLateral01).Within(1e-3f),
                "At steady state (no shift in flight) PelvisObliquity01 must equal PelvisLateral01 — both settle on the same target with the same slew.");
        }

        [Test]
        public void Frozen_ZeroesObliquity_AndClearsAnyPendingPreLoadRetarget()
        {
            var director = new StanceDirector();
            director.Seed(29);

            bool firedShift = false;
            for (int i = 0; i < 60 * 30 && !firedShift; i++)
            {
                TickActive(director);
                if (System.Math.Abs(director.PelvisObliquity01) > 0.01f) firedShift = true;
            }
            Assert.IsTrue(firedShift, "Sanity: a shift must have fired (obliquity engaged) before freezing.");

            // Suppress for long enough to fully decay (>= ~5 slew time constants at
            // transferSeconds/3 = 0.33s each) but DELIBERATELY under the 2s
            // SettleSuppressedThresholdSeconds — a suppression >= 2s arms the (pre-existing
            // "settle step" mechanism, which then legitimately reschedules a new
            // (non-stale) shift within 0.5-1.5s of unfreezing (see
            // SettleStep_FiresSoonAfterALongSuppressionLifts) — well inside this test's 1s
            // post-unfreeze window. Staying under the threshold isolates the "stale pre-load
            // retarget must not survive a freeze" behavior this test actually targets from that
            // unrelated (and itself correctly-tested elsewhere) settle-step behavior.
            for (int i = 0; i < 108; i++) // 1.8 simulated seconds
                TickActive(director, suppression: GestureSuppression.FullBody);

            Assert.That(director.PelvisObliquity01, Is.EqualTo(0f).Within(0.01f),
                "Suppression must decay the pelvis obliquity output to (near) zero too.");
            Assert.That(director.PelvisLateral01, Is.EqualTo(0f).Within(0.01f));

            // Unfreeze and tick past the old pending retarget's window (<=0.6s) without enough
            // time for a brand-new shift to fire (next schedule is >= ~5s, and no settle step is
            // armed since the suppression above stayed under the 2s threshold) — lateral must
            // stay at zero, never jumping to a stale pre-freeze pending target.
            for (int i = 0; i < 60; i++) // 1 simulated second
                TickActive(director);

            Assert.That(director.PelvisLateral01, Is.EqualTo(0f).Within(0.05f),
                "A stale pre-load retarget from before the freeze must never apply after unfreezing.");
        }
    }
}
