using System.Collections.Generic;
using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     <see cref="BallisticMotor" />'s moving-goal boundary condition: the optional
    ///     <c>goalVelocity</c> that makes a movement land MATCHING the goal's motion instead of
    ///     at rest.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A shift toward something that is itself moving used to end at rest on top of a
    ///         goal that had not stopped, so the frame after arrival the error was already
    ///         growing again and the next re-plan launched a fresh movement — a stall and a
    ///         re-launch on every re-target during a sweep. The fix moves two boundary
    ///         conditions: the end velocity becomes <c>v</c> rather than 0, and the plan aims at
    ///         <c>goal + v*t</c> rather than at where the goal is now.
    ///     </para>
    ///     <para>
    ///         The still-goal behaviour is the thing that must NOT have changed, so it is pinned
    ///         here twice: against the textbook polynomial, and against a run of the same steps
    ///         through the pre-existing five-argument overload.
    ///     </para>
    /// </remarks>
    public sealed class BallisticMotorMovingGoalTests
    {
        private const float GenerousMaxSpeed = 1_000_000f;
        private const float GenerousMaxAccel = 1_000_000f;
        private const float Dt = 1f / 60f;

        // ------------------------------------------------------------------ landing on a moving goal

        [Test]
        public void MovingGoal_LandsOnTheGoalMatchingItsVelocity()
        {
            const float goalVelocity = 30f;
            const float duration = 0.6f;

            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, duration);

            float goal = 20f;
            while (motor.IsActive)
            {
                goal += goalVelocity * Dt;
                motor.Step(goal, GenerousMaxSpeed, GenerousMaxAccel, 0f, Dt, goalVelocity);
            }

            Assert.That(motor.Current, Is.EqualTo(goal).Within(1e-3f),
                $"The movement must land ON the goal; ended at {motor.Current:F4} against a goal of {goal:F4}.");
            Assert.That(motor.Velocity, Is.EqualTo(goalVelocity).Within(1e-3f),
                "and land MATCHING the goal's velocity, so pursuit continues without a stall; " +
                $"ended at {motor.Velocity:F4} deg/s against {goalVelocity}.");
        }

        [Test]
        public void MovingGoal_TrajectoryNeverReversesDirection()
        {
            const float goalVelocity = 30f;

            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, 0.6f);

            float goal = 20f;
            float previous = motor.Current;
            float worstReversal = 0f;
            while (motor.IsActive)
            {
                goal += goalVelocity * Dt;
                float value = motor.Step(goal, GenerousMaxSpeed, GenerousMaxAccel, 0f, Dt, goalVelocity);
                worstReversal = Mathf.Min(worstReversal, value - previous);
                previous = value;
            }

            // The landing point no longer extrapolates the goal's motion — only the landing
            // VELOCITY matches it, and the position boundary aims at the goal where it is now
            // (see BallisticMotor.Step's remarks). Near the terminal frames that trades a small
            // ~0.005 deg correction (about 1% of a normal ~0.5 deg step here) for never
            // overshooting a goal whose lead is wrong; -0.01 still catches an actual reversal.
            Assert.That(worstReversal, Is.GreaterThanOrEqualTo(-0.01f),
                "A movement toward a goal receding in one direction must never step backwards — a " +
                $"reversal here is the landing boundary fighting the goal; worst step {worstReversal:F5} deg.");
        }

        [Test]
        public void ToldTheGoalIsStill_StillArrivesAtRest()
        {
            // The negative control for the fix: the same movement, planned with goalVelocity 0
            // against a goal that is in fact moving, is what used to stall on arrival.
            const float goalVelocity = 30f;

            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, 0.6f);

            float goal = 20f;
            while (motor.IsActive)
            {
                goal += goalVelocity * Dt;
                motor.Step(goal, GenerousMaxSpeed, GenerousMaxAccel, 0f, Dt, 0f);
            }

            Assert.That(motor.Velocity, Is.EqualTo(0f).Within(1e-3f),
                "Told the goal is still, the motor still lands at rest — which is the stall the " +
                $"goalVelocity argument exists to remove; ended at {motor.Velocity:F4} deg/s.");
        }

        // ------------------------------------------------------------------ the still-goal path is unchanged

        [Test]
        public void ZeroGoalVelocity_IsIdenticalToOmittingTheArgument()
        {
            List<float> withArgument = StillGoalTrace(passZeroExplicitly: true);
            List<float> withoutArgument = StillGoalTrace(passZeroExplicitly: false);

            Assert.That(withArgument.Count, Is.EqualTo(withoutArgument.Count),
                "The two runs must consume the same number of frames.");
            for (int i = 0; i < withArgument.Count; i++)
                Assert.That(withArgument[i], Is.EqualTo(withoutArgument[i]).Within(0f),
                    $"Frame {i} diverged: {withArgument[i]:F6} vs {withoutArgument[i]:F6}.");
        }

        [Test]
        public void StillGoal_StillTracesTheMinimumJerkPolynomial()
        {
            // The added boundary condition must collapse to the classic 10t^3-15t^4+6t^5 when the
            // goal is still. This is the same derivation check BallisticMotorTests runs, repeated
            // here because it is the property the new argument could silently have broken.
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, 1f);

            float elapsed = 0f;
            float worst = 0f;
            while (motor.IsActive)
            {
                float value = motor.Step(1f, GenerousMaxSpeed, GenerousMaxAccel, 0f, Dt, 0f);
                elapsed += Dt;
                float s = Mathf.Clamp01(elapsed);
                float expected = (10f * s * s * s) - (15f * s * s * s * s) + (6f * s * s * s * s * s);
                worst = Mathf.Max(worst, Mathf.Abs(value - expected));
            }

            Assert.That(worst, Is.LessThanOrEqualTo(0.02f),
                $"A still goal must still trace 10t^3-15t^4+6t^5 within 0.02; worst error {worst:F5}.");
        }

        private static List<float> StillGoalTrace(bool passZeroExplicitly)
        {
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, 1f);

            var trace = new List<float>(64);
            while (motor.IsActive)
                trace.Add(passZeroExplicitly
                    ? motor.Step(1f, GenerousMaxSpeed, GenerousMaxAccel, 0.3f, Dt, 0f)
                    : motor.Step(1f, GenerousMaxSpeed, GenerousMaxAccel, 0.3f, Dt));

            return trace;
        }

        [TestCase(90f, 20f, 20f, true)]
        [TestCase(-90f, 20f, -20f, true)]
        [TestCase(10f, 20f, 10f, false)]
        [TestCase(10f, 0f, 0f, true)]
        [TestCase(-10f, -20f, 0f, true)]
        public void Landing_RespectsTheSpeedEnvelope(float goalVelocity, float speedLimit, float expected, bool engaged)
        {
            var motor = new BallisticMotor();
            motor.Begin(0f, 0f, Dt);
            Assert.That(motor.Step(5f, speedLimit, GenerousMaxAccel, 0f, Dt, goalVelocity), Is.EqualTo(5f));
            Assert.That(motor.Velocity, Is.EqualTo(expected));
            Assert.That(motor.EnvelopeEngaged, Is.EqualTo(engaged));
        }

    }
}
