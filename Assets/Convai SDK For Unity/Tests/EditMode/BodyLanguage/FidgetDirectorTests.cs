using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="FidgetDirector" />: weight-shift program shape (engage/hold/
    ///     return), the suppression matrix, determinism, and rate scaling.
    /// </summary>
    public sealed class FidgetDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float GapSeconds = 3.5f;
        private const float EaseSeconds = 0.9f;
        private const float HoldSeconds = 2.2f;

        private static void TickActive(
            FidgetDirector director,
            DialogueState state = DialogueState.Idle,
            bool fidgetsEnabled = true,
            float fidgetRate = 1f,
            GestureSuppression suppression = GestureSuppression.None,
            float stillnessScale = 0f) =>
            director.Tick(state, fidgetsEnabled, fidgetRate, suppression, stillnessScale, Dt, GapSeconds, EaseSeconds, HoldSeconds);

        [Test]
        public void WeightShiftProgram_EngagesHoldsAndReturnsToZero()
        {
            var director = new FidgetDirector();
            director.Seed(1);

            bool sawEngaged = false;
            bool sawHold = false;
            bool sawReturn = false;
            float peak = 0f;

            // Run long enough to observe at least one full cycle (gap + ease-in + hold + ease-out).
            const int ticks = 60 * 20; // 20 seconds
            for (int i = 0; i < ticks; i++)
            {
                TickActive(director);
                float v = director.WeightShiftValue;
                if (System.Math.Abs(v) > 0.05f) sawEngaged = true;
                if (System.Math.Abs(v) > peak) peak = System.Math.Abs(v);
                if (System.Math.Abs(v) > 0.9f) sawHold = true;
                if (sawHold && System.Math.Abs(v) < 0.05f) sawReturn = true;
            }

            Assert.IsTrue(sawEngaged, "The fidget program must actually engage (nonzero weight-shift) within 20s.");
            Assert.IsTrue(sawHold, "The program must reach a near-full-amplitude hold.");
            Assert.IsTrue(sawReturn, "The program must ease back to (near) zero after a hold.");
            Assert.That(peak, Is.LessThanOrEqualTo(1.001f), "Weight-shift value must never exceed the -1..1 domain.");
        }

        [Test]
        public void FirstTicks_NeverOpenWithAnImmediateShift()
        {
            var director = new FidgetDirector();
            director.Seed(1);

            // The very first active tick must start on a gap, not mid-shift.
            TickActive(director);
            Assert.That(director.WeightShiftValue, Is.EqualTo(0f),
                "A fresh director must never snap into an immediate weight-shift on its first tick.");
        }

        [TestCase(DialogueState.Reacting)]
        [TestCase(DialogueState.Interrupted)]
        public void HardSuppressedStates_DecayToZero_AndNeverStartANewCycle(DialogueState state)
        {
            var director = new FidgetDirector();
            director.Seed(2);

            // First get a cycle running in Idle so there is nonzero amplitude to decay from.
            for (int i = 0; i < 60 * 6; i++)
                TickActive(director);

            for (int i = 0; i < 60 * 3; i++)
                TickActive(director, state: state);

            Assert.That(director.WeightShiftValue, Is.EqualTo(0f).Within(0.01f),
                $"{state} must decay the weight-shift to (near) zero.");

            // Continue for a long time — must never re-engage while suppressed.
            bool reEngaged = false;
            for (int i = 0; i < 60 * 20; i++)
            {
                TickActive(director, state: state);
                if (System.Math.Abs(director.WeightShiftValue) > 0.01f) reEngaged = true;
            }

            Assert.IsFalse(reEngaged, $"{state} must never allow a new weight-shift cycle to start.");
        }

        [Test]
        public void SuppressionNotNone_DecaysToZero_AndNeverStartsANewCycle()
        {
            var director = new FidgetDirector();
            director.Seed(3);

            for (int i = 0; i < 60 * 6; i++)
                TickActive(director);

            // Let the suppression decay settle first — the 6s warmup can leave the weight-shift
            // mid-hold (~1.0), which then legitimately eases down over ~2s. Checking for
            // "re-engagement" before it settles would false-trip on that decay, not on a new
            // cycle (mirrors HardSuppressedStates_DecayToZero_AndNeverStartANewCycle).
            for (int i = 0; i < 60 * 3; i++)
                TickActive(director, suppression: GestureSuppression.UpperBody);
            Assert.That(director.WeightShiftValue, Is.EqualTo(0f).Within(0.01f),
                "UpperBody suppression must decay the weight-shift to (near) zero.");

            bool reEngaged = false;
            for (int i = 0; i < 60 * 20; i++)
            {
                TickActive(director, suppression: GestureSuppression.UpperBody);
                if (System.Math.Abs(director.WeightShiftValue) > 0.01f) reEngaged = true;
            }

            Assert.IsFalse(reEngaged, "Any suppression != None must prevent a new fidget cycle from starting.");
        }

        [Test]
        public void SuppressionLiftedMidShift_EasesResidualOut_NeverSnapsToZero()
        {
            var director = new FidgetDirector();
            director.Seed(2);

            // Drive a cycle until the weight-shift is substantially engaged (near a hold).
            float engaged = 0f;
            for (int i = 0; i < 60 * 20 && engaged < 0.8f; i++)
            {
                TickActive(director);
                engaged = System.Math.Abs(director.WeightShiftValue);
            }
            Assert.That(engaged, Is.GreaterThan(0.8f), "Sanity: the fidget reached a near-full engagement to decay from.");

            // Suppress only briefly — far shorter than the decay — so a real residual survives.
            // Uses Reacting (still hard-suppressing) rather than Speaking, since Speaking no
            // longer hard-suppresses (see SpeakingSway tests below).
            for (int i = 0; i < 6; i++) // ~0.1s
                TickActive(director, state: DialogueState.Reacting);

            float residual = System.Math.Abs(director.WeightShiftValue);
            Assert.That(residual, Is.GreaterThan(0.3f),
                "Sanity: a brief suppression must leave a real residual (not fully decayed yet).");

            // The first unsuppressed tick must EASE the residual out, not re-enter Phase.Gap and
            // hard-zero it — that snap was a real defect — one tick's slew removes only a few
            // percent, never the whole residual.
            TickActive(director);
            float afterLift = System.Math.Abs(director.WeightShiftValue);
            Assert.That(afterLift, Is.GreaterThan(residual * 0.5f),
                "A lifted suppression must ease the residual weight-shift out, never snap it to zero in one tick.");
        }

        [Test]
        public void FidgetsDisabled_ProducesZeroThroughout()
        {
            var director = new FidgetDirector();
            director.Seed(4);

            for (int i = 0; i < 60 * 20; i++)
            {
                TickActive(director, fidgetsEnabled: false);
                Assert.That(director.WeightShiftValue, Is.EqualTo(0f), "FidgetsEnabled=false must produce zero output.");
            }
        }

        [Test]
        public void ZeroFidgetRate_ProducesZeroOutput()
        {
            var director = new FidgetDirector();
            director.Seed(5);

            for (int i = 0; i < 60 * 10; i++)
            {
                TickActive(director, fidgetRate: 0f);
                Assert.That(director.WeightShiftValue, Is.EqualTo(0f), "FidgetRate=0 must produce zero output.");
            }
        }

        [Test]
        public void StillnessScale_DampensAmplitude()
        {
            var directorFull = new FidgetDirector();
            directorFull.Seed(9);
            var directorStill = new FidgetDirector();
            directorStill.Seed(9);

            float peakFull = 0f;
            float peakStill = 0f;

            for (int i = 0; i < 60 * 20; i++)
            {
                TickActive(directorFull, stillnessScale: 0f);
                TickActive(directorStill, stillnessScale: 0.8f);

                peakFull = System.Math.Max(peakFull, System.Math.Abs(directorFull.WeightShiftValue));
                peakStill = System.Math.Max(peakStill, System.Math.Abs(directorStill.WeightShiftValue));
            }

            Assert.That(peakStill, Is.LessThan(peakFull),
                "A higher stillness scale must dampen the fidget amplitude relative to no damping.");
        }

        [Test]
        public void Determinism_SameSeed_ProducesIdenticalSchedule()
        {
            var directorA = new FidgetDirector();
            directorA.Seed(4242);
            var directorB = new FidgetDirector();
            directorB.Seed(4242);

            for (int i = 0; i < 60 * 30; i++)
            {
                TickActive(directorA);
                TickActive(directorB);
                Assert.That(directorA.WeightShiftValue, Is.EqualTo(directorB.WeightShiftValue),
                    $"Identical seed + tick sequence must produce an identical weight-shift schedule at tick {i}.");
            }
        }

        [Test]
        public void HigherFidgetRate_ProducesMoreCyclesOverTheSameWindow()
        {
            var directorSlow = new FidgetDirector();
            directorSlow.Seed(11);
            var directorFast = new FidgetDirector();
            directorFast.Seed(11);

            int crossingsSlow = CountZeroCrossingsIntoEngagement(directorSlow, fidgetRate: 0.2f);
            int crossingsFast = CountZeroCrossingsIntoEngagement(directorFast, fidgetRate: 1f);

            Assert.That(crossingsFast, Is.GreaterThan(crossingsSlow),
                "A higher FidgetRate must produce more weight-shift cycles over the same time window.");
        }

        private static int CountZeroCrossingsIntoEngagement(FidgetDirector director, float fidgetRate)
        {
            int count = 0;
            bool wasEngaged = false;
            for (int i = 0; i < 60 * 60; i++) // 60 seconds
            {
                director.Tick(DialogueState.Idle, true, fidgetRate, GestureSuppression.None, 0f, Dt, GapSeconds, EaseSeconds, HoldSeconds);
                bool engaged = System.Math.Abs(director.WeightShiftValue) > 0.5f;
                if (engaged && !wasEngaged) count++;
                wasEngaged = engaged;
            }
            return count;
        }

        [Test]
        public void Reset_ReturnsToZeroAndClearsSchedule()
        {
            var director = new FidgetDirector();
            director.Seed(1);

            for (int i = 0; i < 60 * 6; i++)
                TickActive(director);

            director.Reset();

            Assert.That(director.WeightShiftValue, Is.EqualTo(0f));
        }

        [Test]
        public void WantsClipFidget_AlwaysFalse()
        {
            // No GestureCueKind is reserved for an idle fidget clip today — this director must
            // never claim to want one (see class remarks).
            var director = new FidgetDirector();
            director.Seed(1);

            for (int i = 0; i < 60 * 5; i++)
            {
                TickActive(director);
                Assert.IsFalse(director.WantsClipFidget);
            }
        }

        // ── Speaking sway ───────────────────────────────────

        [Test]
        public void Speaking_NoLongerHardSuppresses_ProducesAReducedOngoingSway()
        {
            var director = new FidgetDirector();
            director.Seed(1);

            // Speaking's own policy row keeps FidgetsEnabled=false/FidgetRate=0 (unchanged) —
            // pass those through exactly as the controller would, and rely on the director's
            // internal Speaking override to still produce motion.
            float peak = 0f;
            const int ticks = 60 * 30; // 30 seconds — several full cycles at the reduced rate.
            for (int i = 0; i < ticks; i++)
            {
                TickActive(director, state: DialogueState.Speaking, fidgetsEnabled: false, fidgetRate: 0f);
                peak = System.Math.Max(peak, System.Math.Abs(director.WeightShiftValue));
            }

            Assert.That(peak, Is.GreaterThan(0.05f),
                "Speaking must still engage a visible sway even though the profile's own Speaking " +
                "FidgetsEnabled/FidgetRate stay false/0.");
            Assert.That(peak, Is.LessThan(0.5f),
                "Speaking's sway must read as a subtle reduction, well below the idle program's full amplitude.");
        }

        [Test]
        public void Speaking_UnderUpperBodySuppression_SwayStillEngages()
        {
            // The LipSync talk clip reports GestureSuppression.UpperBody for the ENTIRE duration
            // of speech — the sway must survive it (only FullBody hard-stops while Speaking); it
            // is reduced downstream at the solver via _postureSuppressionWeight instead, which is
            // not this director's concern.
            var director = new FidgetDirector();
            director.Seed(1);

            float peak = 0f;
            const int ticks = 60 * 30; // 30 seconds — several full cycles at the reduced rate.
            for (int i = 0; i < ticks; i++)
            {
                TickActive(director, state: DialogueState.Speaking, fidgetsEnabled: false, fidgetRate: 0f,
                    suppression: GestureSuppression.UpperBody);
                peak = System.Math.Max(peak, System.Math.Abs(director.WeightShiftValue));
            }

            Assert.That(peak, Is.GreaterThan(0.05f),
                "Speaking sway must still engage under UpperBody suppression (the talk-clip/locomotion " +
                "case) — only FullBody suppression may hard-stop it while Speaking.");
        }

        [Test]
        public void Speaking_UnderFullBodySuppression_DecaysToZero()
        {
            var director = new FidgetDirector();
            director.Seed(1);

            // First get the Speaking sway running so there is nonzero amplitude to decay from.
            for (int i = 0; i < 60 * 20; i++)
                TickActive(director, state: DialogueState.Speaking, fidgetsEnabled: false, fidgetRate: 0f);

            for (int i = 0; i < 60 * 3; i++)
                TickActive(director, state: DialogueState.Speaking, fidgetsEnabled: false, fidgetRate: 0f,
                    suppression: GestureSuppression.FullBody);

            Assert.That(director.WeightShiftValue, Is.EqualTo(0f).Within(0.01f),
                "FullBody suppression must still hard-stop the Speaking sway, decaying it to (near) zero.");

            // Continue for a long time — must never re-engage while FullBody-suppressed.
            bool reEngaged = false;
            for (int i = 0; i < 60 * 20; i++)
            {
                TickActive(director, state: DialogueState.Speaking, fidgetsEnabled: false, fidgetRate: 0f,
                    suppression: GestureSuppression.FullBody);
                if (System.Math.Abs(director.WeightShiftValue) > 0.01f) reEngaged = true;
            }

            Assert.IsFalse(reEngaged, "FullBody suppression must never allow the Speaking sway to re-engage.");
        }

        [Test]
        public void Speaking_PeaksLowerThanIdleAtFullRate()
        {
            var idle = new FidgetDirector();
            idle.Seed(7);
            var speaking = new FidgetDirector();
            speaking.Seed(7);

            float peakIdle = 0f;
            float peakSpeaking = 0f;
            const int ticks = 60 * 30;
            for (int i = 0; i < ticks; i++)
            {
                TickActive(idle, state: DialogueState.Idle, fidgetRate: 1f);
                TickActive(speaking, state: DialogueState.Speaking, fidgetsEnabled: false, fidgetRate: 0f);
                peakIdle = System.Math.Max(peakIdle, System.Math.Abs(idle.WeightShiftValue));
                peakSpeaking = System.Math.Max(peakSpeaking, System.Math.Abs(speaking.WeightShiftValue));
            }

            Assert.That(peakSpeaking, Is.LessThan(peakIdle),
                "Speaking's sway must peak lower than an Idle program running at full FidgetRate.");
            Assert.That(peakSpeaking / peakIdle, Is.LessThan(0.5f).And.GreaterThan(0.15f),
                "Speaking's sway should read roughly in the 0.3-0.4x reduced range relative to full-rate Idle.");
        }

        [Test]
        public void ReactingAndInterrupted_StillHardSuppress()
        {
            // Speaking is deliberately excluded from the hard-suppressed set;
            // Reacting/Interrupted must remain exactly as before — a genuine regression guard so
            // a future change can't accidentally also exempt them.
            var reacting = new FidgetDirector();
            reacting.Seed(3);
            var interrupted = new FidgetDirector();
            interrupted.Seed(3);

            for (int i = 0; i < 60 * 6; i++)
            {
                TickActive(reacting);
                TickActive(interrupted);
            }

            for (int i = 0; i < 60 * 3; i++)
            {
                TickActive(reacting, state: DialogueState.Reacting);
                TickActive(interrupted, state: DialogueState.Interrupted);
            }

            Assert.That(reacting.WeightShiftValue, Is.EqualTo(0f).Within(0.01f),
                "Reacting must still decay the weight-shift to (near) zero.");
            Assert.That(interrupted.WeightShiftValue, Is.EqualTo(0f).Within(0.01f),
                "Interrupted must still decay the weight-shift to (near) zero.");
        }

        [Test]
        public void TransitionIntoSpeakingMidHold_DoesNotPopTheOutputValue()
        {
            var director = new FidgetDirector();
            director.Seed(1);

            // Drive Idle to a near-full-amplitude hold first.
            float engaged = 0f;
            for (int i = 0; i < 60 * 20 && engaged < 0.9f; i++)
            {
                TickActive(director, fidgetRate: 1f);
                engaged = System.Math.Abs(director.WeightShiftValue);
            }
            Assert.That(engaged, Is.GreaterThan(0.9f), "Sanity: reached a near-full-amplitude hold to transition from.");
            float beforeSpeaking = director.WeightShiftValue;

            // Flip directly into Speaking for a single tick — Speaking does not hard-suppress, so
            // without the internal rate slew this would pop from the Idle-rate amplitude straight
            // to the Speaking-rate amplitude in one frame.
            TickActive(director, state: DialogueState.Speaking, fidgetsEnabled: false, fidgetRate: 0f);
            float afterOneTick = director.WeightShiftValue;

            float relativeDelta = System.Math.Abs(afterOneTick - beforeSpeaking) / System.Math.Abs(beforeSpeaking);
            Assert.That(relativeDelta, Is.LessThan(0.15f),
                "A single tick crossing into Speaking must not pop the weight-shift value — the " +
                $"effective rate must slew, not snap (relative delta was {relativeDelta:0.000}).");
        }
    }
}
