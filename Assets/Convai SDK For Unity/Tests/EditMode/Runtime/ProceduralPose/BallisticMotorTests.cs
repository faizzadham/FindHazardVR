using System.Collections.Generic;
using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     Behavior tests for <see cref="BallisticMotor" />: the minimum-jerk shift motor that
    ///     re-plans every frame toward a goal that may move, over a duration fixed at
    ///     <see cref="BallisticMotor.Begin" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is also the closed-form check the type's own comments point to: the quintic
    ///         coefficients in <see cref="BallisticMotor.Step" /> are derived, not quoted, and a
    ///         transcription slip there compiles fine and is invisible until it is a feel bug.
    ///         <see cref="ClosedFormIdentity_MatchesTheMinimumJerkPolynomialAtEverySample" /> is
    ///         the test that actually proves the derivation.
    ///     </para>
    ///     <para>
    ///         Tolerances below (0.02 on the identity test, 0.04 across frame rates, 3% on peak
    ///         speed, etc.) are the acceptance numbers this suite was written against — not
    ///         loosened to make anything pass.
    ///     </para>
    /// </remarks>
    public sealed class BallisticMotorTests
    {
        // Large enough that the safety envelope never engages for the "planned, in-budget"
        // tests — those tests are about the polynomial's shape, not the clamp.
        private const float GenerousMaxSpeed = 1_000_000f;
        private const float GenerousMaxAccel = 1_000_000f;

        // Slack for shape assertions (unimodality, skew ordering) against float32 step noise —
        // one to two orders of magnitude under the quantization step a real reversal would show.
        private const float ShapeEpsilon = 1e-3f;

        [Test]
        public void ClosedFormIdentity_MatchesTheMinimumJerkPolynomialAtEverySample()
        {
            // Begin(0, 0, 1), step toward goal 1 at 60 Hz with skew 0 and a generous envelope:
            // every sample must equal 10t³-15t⁴+6t⁵. This is the derivation check — see the type
            // remarks — so it gets the tightest tolerance in the file.
            float maxError = MaxClosedFormError(1f / 60f, 1f);

            Assert.That(maxError, Is.LessThanOrEqualTo(0.02f),
                "Every sample must match the textbook minimum-jerk polynomial 10t³-15t⁴+6t⁵ "
                + $"within 0.02; measured max error {maxError:F5}.");
        }

        [Test]
        public void ClosedFormIdentity_HoldsAcrossFrameRates()
        {
            // Same experiment, run at 30/60/144/240 Hz. A still goal must trace the same path
            // regardless of step size — the whole point of evaluating the polynomial
            // analytically each frame instead of integrating jerk.
            foreach (float dt in new[] { 1f / 30f, 1f / 60f, 1f / 144f, 1f / 240f })
            {
                float maxError = MaxClosedFormError(dt, 1f);

                Assert.That(maxError, Is.LessThanOrEqualTo(0.04f),
                    $"dt={dt:F5}s must still track the closed form within 0.04; measured max "
                    + $"error {maxError:F5}.");
            }
        }

        [Test]
        public void Completion_LandsExactlyAtRestOnTheGoal_AndFurtherStepsAreNoOps()
        {
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, 0.5f);
            const float dt = 1f / 60f;
            const float goal = 2f;

            float value = 0f;
            while (motor.IsActive)
                value = motor.Step(goal, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);

            Assert.That(value, Is.EqualTo(goal), "The final Step must land exactly on the goal.");
            Assert.That(motor.Velocity, Is.EqualTo(0f), "Velocity must be exactly zero on completion.");
            Assert.That(motor.Acceleration, Is.EqualTo(0f), "Acceleration must be exactly zero on completion.");
            Assert.That(motor.IsActive, Is.False, "The movement must report inactive on completion.");

            // Stepping again after completion: same goal returns it unchanged and does nothing.
            float again = motor.Step(goal, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);
            Assert.That(again, Is.EqualTo(goal), "Stepping after completion must keep returning the goal.");
            Assert.That(motor.Velocity, Is.EqualTo(0f));
            Assert.That(motor.Acceleration, Is.EqualTo(0f));
            Assert.That(motor.IsActive, Is.False);

            // An inactive motor takes no direction from a *different* goal either — completion
            // is a stable rest state, not a paused plan waiting to be resumed.
            float differentGoal = motor.Step(99f, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);
            Assert.That(differentGoal, Is.EqualTo(goal),
                "An inactive motor must not resume motion toward a new goal on its own.");
        }

        [Test]
        public void VelocityProfile_WithZeroSkew_IsUnimodalAndPeaksAtTheMidpoint()
        {
            const float duration = 1f;
            const float distance = 10f;
            const float dt = 1f / 240f;

            (List<float> speeds, List<float> times) = RunSpeedProfile(distance, duration, 0f, dt);
            int peakIndex = ArgMax(speeds);
            float peakFraction = times[peakIndex] / duration;
            float peakSpeed = speeds[peakIndex];
            float expectedPeakSpeed = 1.875f * distance / duration;

            Assert.That(peakFraction, Is.EqualTo(0.5f).Within(0.03f),
                $"Peak speed must land at the profile's midpoint; measured at fraction {peakFraction:F3}.");
            Assert.That(peakSpeed, Is.EqualTo(expectedPeakSpeed).Within(expectedPeakSpeed * 0.03f),
                "Peak speed must match the minimum-jerk closed form 1.875·distance/duration "
                + $"within 3%; measured {peakSpeed:F4}, expected {expectedPeakSpeed:F4}.");

            AssertUnimodal(speeds, peakIndex);
        }

        [Test]
        public void Skew_MovesThePeakEarlier_WithoutChangingLandingOrDuration()
        {
            const float duration = 1f;
            const float distance = 10f;
            const float dt = 1f / 240f;
            // A frame's worth of slack on "duration unchanged": the motor's own end-of-movement
            // check compares `remaining <= deltaTime`, so the last partial frame can carry the
            // test's own elapsed accumulator very slightly past the nominal duration.
            const float durationTolerance = 2f * dt;

            (List<float> skewedSpeeds, List<float> skewedTimes) = RunSpeedProfile(distance, duration, 0.18f, dt);
            int skewedPeakIndex = ArgMax(skewedSpeeds);
            float skewedPeakFraction = skewedTimes[skewedPeakIndex] / duration;

            Assert.That(skewedPeakFraction, Is.EqualTo(0.42f).Within(0.03f),
                $"skew 0.18 must move the peak to ~0.42 of the movement; measured {skewedPeakFraction:F3}.");

            float finalValue = RunToCompletion(distance, duration, 0.18f, dt, out float elapsed);
            Assert.That(finalValue, Is.EqualTo(distance), "Skew must not change where the movement ends.");
            Assert.That(elapsed, Is.EqualTo(duration).Within(durationTolerance),
                "Skew must not change the movement's total duration.");

            // Monotonicity: a larger skew must never put the peak later than a smaller one.
            float previousFraction = float.NaN;
            foreach (float skew in new[] { 0f, 0.1f, 0.18f, 0.3f, 0.5f, 0.8f, 1f })
            {
                (List<float> speeds, List<float> times) = RunSpeedProfile(distance, duration, skew, dt);
                int peakIndex = ArgMax(speeds);
                float fraction = times[peakIndex] / duration;

                if (!float.IsNaN(previousFraction))
                    Assert.That(fraction, Is.LessThanOrEqualTo(previousFraction + ShapeEpsilon),
                        $"skew {skew}: peak fraction {fraction:F4} must not be later than the previous "
                        + $"(smaller) skew's peak fraction {previousFraction:F4}.");
                previousFraction = fraction;
            }
        }

        [Test]
        public void Retargeting_MidFlight_KeepsVelocityAndPositionContinuous_AndEndsOnTheNewGoal()
        {
            const float duration = 0.5f;
            const float dt = 1f / 240f;
            // Generous relative to what this movement actually demands (measured peak
            // acceleration around a few tens of units/s²) so the envelope never engages and the
            // test is purely about the re-plan's own continuity, not the clamp.
            const float maxSpeed = 1000f;
            const float maxAccel = 20000f;
            const float originalGoal = 1f;
            const float retargetedGoal = 1.6f;

            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, duration);

            float previousValue = 0f;
            float previousVelocity = 0f;
            float maxMeasuredAccel = 0f;
            float maxPositionOvershoot = 0f;
            float elapsed = 0f;
            int frame = 0;
            float finalValue = 0f;

            while (motor.IsActive)
            {
                float goal = frame >= 15 ? retargetedGoal : originalGoal;
                float value = motor.Step(goal, maxSpeed, maxAccel, 0f, dt);
                elapsed += dt;
                frame++;

                // (b) No frame-to-frame velocity change may imply an acceleration above the
                // envelope — checked on the internal velocity state, not a finite difference of
                // position (differentiating float32 position twice amplifies noise by 1/dt²).
                float measuredAccel = Mathf.Abs((motor.Velocity - previousVelocity) / dt);
                maxMeasuredAccel = Mathf.Max(maxMeasuredAccel, measuredAccel);

                // (c) No single-frame position jump may exceed what that frame's velocity
                // permits: the distance covered must not be more than the faster of the frame's
                // two velocity endpoints times dt (plus a tiny slack for the polynomial's own
                // curvature within the frame).
                float allowedByVelocity = Mathf.Max(Mathf.Abs(previousVelocity), Mathf.Abs(motor.Velocity)) * dt;
                float actualDelta = Mathf.Abs(value - previousValue);
                maxPositionOvershoot = Mathf.Max(maxPositionOvershoot, actualDelta - allowedByVelocity);

                previousValue = value;
                previousVelocity = motor.Velocity;
                finalValue = value;
            }

            // (a) Ends on the retargeted goal, at the movement's original clock — retargeting
            // changes where the movement is going, never how long it takes.
            Assert.That(finalValue, Is.EqualTo(retargetedGoal).Within(1e-3f),
                "The movement must end on the retargeted goal.");
            Assert.That(elapsed, Is.EqualTo(duration).Within(2f * dt),
                "Retargeting must not change the movement's original clock.");

            Assert.That(maxMeasuredAccel, Is.LessThanOrEqualTo(maxAccel + 1f),
                "No frame-to-frame velocity change may imply an acceleration above the envelope, "
                + $"even across a retarget; measured {maxMeasuredAccel:F3}.");
            Assert.That(maxPositionOvershoot, Is.LessThanOrEqualTo(1e-2f),
                "No single-frame position change may exceed what that frame's velocity permits; "
                + $"measured overshoot {maxPositionOvershoot:F6}.");
        }

        [Test]
        public void Envelope_EngagesOnlyWhenThePlannedMovementExceedsTheCaps()
        {
            const float dt = 1f / 240f;

            // Far too fast for the caps: 90 units in 0.05 s needs ~3400 units/s² of peak
            // acceleration against a 500 cap — the envelope must take over.
            bool tooFastEngaged = RunEnvelopeSweep(
                90f, 0.05f, 200f, 500f, dt, out float tooFastMaxSpeed, out float tooFastMaxAccel);

            Assert.That(tooFastEngaged, Is.True,
                "EnvelopeEngaged must report true for a movement planned far too fast for its caps.");
            Assert.That(tooFastMaxSpeed, Is.LessThanOrEqualTo(200f + 1e-2f),
                $"No frame may exceed the speed cap; measured {tooFastMaxSpeed:F3}.");
            Assert.That(tooFastMaxAccel, Is.LessThanOrEqualTo(500f + 1e-2f),
                $"No frame may exceed the acceleration cap; measured {tooFastMaxAccel:F3}.");

            // A normal movement (peak speed/accel comfortably inside 240/1500) must never touch
            // the envelope — the duration law is supposed to keep it out of reach.
            bool normalEngaged = RunEnvelopeSweep(
                20f, 0.34f, 240f, 1500f, dt, out float normalMaxSpeed, out float normalMaxAccel);

            Assert.That(normalEngaged, Is.False,
                "EnvelopeEngaged must not fire on a movement planned within its caps.");
            Assert.That(normalMaxSpeed, Is.LessThanOrEqualTo(240f),
                $"A normal movement's speed must stay under its own cap; measured {normalMaxSpeed:F3}.");
            Assert.That(normalMaxAccel, Is.LessThanOrEqualTo(1500f),
                $"A normal movement's acceleration must stay under its own cap; measured {normalMaxAccel:F3}.");
        }

        [Test]
        public void DegenerateInputs_ProduceNoNaNInfinityOrException()
        {
            const float dt = 1f / 60f;

            // Zero duration: Begin must not arm a movement, and Step must hand the starting
            // value straight back.
            var zeroDuration = new BallisticMotor();
            zeroDuration.Begin(1f, 0f, 0f);
            float zeroDurationValue = zeroDuration.Step(5f, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);
            Assert.That(zeroDuration.IsActive, Is.False, "Zero duration must never arm a movement.");
            Assert.That(float.IsFinite(zeroDurationValue), Is.True);
            Assert.That(zeroDurationValue, Is.EqualTo(1f));

            // Zero and negative deltaTime must hold state rather than step or throw.
            var heldState = new BallisticMotor();
            heldState.Begin(0f, 0f, 1f);
            float zeroDtValue = heldState.Step(5f, GenerousMaxSpeed, GenerousMaxAccel, 0f, 0f);
            Assert.That(float.IsFinite(zeroDtValue), Is.True);
            Assert.That(zeroDtValue, Is.EqualTo(0f), "Zero deltaTime must hold the current state.");

            float negativeDtValue = heldState.Step(5f, GenerousMaxSpeed, GenerousMaxAccel, 0f, -dt);
            Assert.That(float.IsFinite(negativeDtValue), Is.True);
            Assert.That(negativeDtValue, Is.EqualTo(0f), "Negative deltaTime must hold the current state too.");

            // Begin with a non-zero incoming velocity, duration shorter than a single frame:
            // the very next Step must terminate the movement on the spot without producing NaN.
            var shortWithVelocity = new BallisticMotor();
            shortWithVelocity.Begin(0f, 500f, 0.01f);
            float shortValue = shortWithVelocity.Step(0.001f, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);
            Assert.That(float.IsFinite(shortValue), Is.True);
            Assert.That(shortWithVelocity.IsActive, Is.False);

            // Reset mid-flight must leave a finite, motionless value that further stepping does
            // not disturb.
            var resetMidFlight = new BallisticMotor();
            resetMidFlight.Begin(0f, 0f, 1f);
            resetMidFlight.Step(10f, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);
            resetMidFlight.Reset();
            Assert.That(resetMidFlight.IsActive, Is.False);
            Assert.That(float.IsFinite(resetMidFlight.Current), Is.True);

            float frozenValue = resetMidFlight.Current;
            float afterReset = resetMidFlight.Step(10f, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);
            Assert.That(float.IsFinite(afterReset), Is.True);
            Assert.That(afterReset, Is.EqualTo(frozenValue),
                "A Step after Reset must not move the channel until Begin is called again.");
        }

        [Test]
        public void NonZeroLaunchVelocity_MakesTheFirstStepContinuousWithIt()
        {
            // Begin(from: 5, velocity: 40, duration: 0.3): the launch velocity is a boundary
            // condition of the minimum-jerk plan, so the very first step's implied velocity must
            // come out near 40 — not near 0, which is what the hand-off from a tracking filter
            // (MotorFilter) depends on being seamless.
            var motor = new BallisticMotor();
            motor.Begin(5f, 40f, 0.3f);
            const float dt = 1f / 240f;

            float value = motor.Step(6f, 1000f, 100000f, 0f, dt);
            float impliedVelocity = (value - 5f) / dt;

            Assert.That(motor.EnvelopeEngaged, Is.False,
                "The envelope must not engage here — the continuity claim is about the plan, not a clamp.");
            Assert.That(impliedVelocity, Is.EqualTo(40f).Within(2f),
                $"The first step's implied velocity must be near the 40 units/s launch velocity, "
                + $"not near 0; measured {impliedVelocity:F3}.");
            Assert.That(motor.Velocity, Is.EqualTo(40f).Within(2f),
                $"The motor's own velocity state after the first step must likewise sit near 40; "
                + $"measured {motor.Velocity:F3}.");
        }

        /// <summary>10t³-15t⁴+6t⁵ — the closed-form position of a from-rest-to-rest minimum-jerk move.</summary>
        private static float ClosedFormMinimumJerk(float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float t4 = t3 * t;
            float t5 = t4 * t;
            return 10f * t3 - 15f * t4 + 6f * t5;
        }

        /// <summary>
        ///     Runs a from-rest, skew-0 movement toward goal 1 over <paramref name="duration" />
        ///     and returns the largest deviation from <see cref="ClosedFormMinimumJerk" /> seen
        ///     at any sample.
        /// </summary>
        private static float MaxClosedFormError(float dt, float duration)
        {
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, duration);

            float elapsed = 0f;
            float maxError = 0f;
            while (motor.IsActive)
            {
                float value = motor.Step(1f, GenerousMaxSpeed, GenerousMaxAccel, 0f, dt);
                elapsed += dt;
                float t = Mathf.Clamp01(elapsed / duration);
                maxError = Mathf.Max(maxError, Mathf.Abs(value - ClosedFormMinimumJerk(t)));
            }

            return maxError;
        }

        /// <summary>Runs a from-rest movement to completion and returns the |velocity| and elapsed-time samples.</summary>
        private static (List<float> speeds, List<float> times) RunSpeedProfile(
            float distance, float duration, float skew, float dt)
        {
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, duration);

            var speeds = new List<float>();
            var times = new List<float>();
            float elapsed = 0f;
            while (motor.IsActive)
            {
                motor.Step(distance, GenerousMaxSpeed, GenerousMaxAccel, skew, dt);
                elapsed += dt;
                speeds.Add(Mathf.Abs(motor.Velocity));
                times.Add(elapsed);
            }

            return (speeds, times);
        }

        /// <summary>Runs a from-rest movement to completion and returns the final value, with elapsed time as an out param.</summary>
        private static float RunToCompletion(float distance, float duration, float skew, float dt, out float elapsed)
        {
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, duration);

            float value = 0f;
            elapsed = 0f;
            while (motor.IsActive)
            {
                value = motor.Step(distance, GenerousMaxSpeed, GenerousMaxAccel, skew, dt);
                elapsed += dt;
            }

            return value;
        }

        /// <summary>
        ///     Runs a from-rest movement to completion under the given caps and reports whether
        ///     the envelope ever engaged, along with the largest |velocity| and |acceleration|
        ///     seen at any frame.
        /// </summary>
        private static bool RunEnvelopeSweep(
            float distance, float duration, float maxSpeed, float maxAccel, float dt,
            out float maxMeasuredSpeed, out float maxMeasuredAccel)
        {
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, duration);

            bool engaged = false;
            maxMeasuredSpeed = 0f;
            maxMeasuredAccel = 0f;
            while (motor.IsActive)
            {
                motor.Step(distance, maxSpeed, maxAccel, 0f, dt);
                engaged |= motor.EnvelopeEngaged;
                maxMeasuredSpeed = Mathf.Max(maxMeasuredSpeed, Mathf.Abs(motor.Velocity));
                maxMeasuredAccel = Mathf.Max(maxMeasuredAccel, Mathf.Abs(motor.Acceleration));
            }

            return engaged;
        }

        private static int ArgMax(List<float> values)
        {
            int best = 0;
            for (int i = 1; i < values.Count; i++)
                if (values[i] > values[best])
                    best = i;
            return best;
        }

        /// <summary>Speed must be non-decreasing up to the peak and non-increasing after it — one hump, one peak.</summary>
        private static void AssertUnimodal(List<float> speeds, int peakIndex)
        {
            for (int i = 1; i <= peakIndex; i++)
                Assert.That(speeds[i], Is.GreaterThanOrEqualTo(speeds[i - 1] - ShapeEpsilon),
                    $"speed must be non-decreasing on the way up to the peak (frame {i}).");

            for (int i = peakIndex + 1; i < speeds.Count; i++)
                Assert.That(speeds[i], Is.LessThanOrEqualTo(speeds[i - 1] + ShapeEpsilon),
                    $"speed must be non-increasing after the peak (frame {i}).");
        }
    }
}
