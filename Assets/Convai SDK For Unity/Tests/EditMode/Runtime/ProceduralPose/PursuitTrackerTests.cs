using System.Collections.Generic;
using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     Behaviour tests for <see cref="PursuitTracker" />: the critically damped second-order
    ///     follower with a bounded velocity lead that the head/torso tracking lane runs on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The three properties that make this type the right tool are all frequency-domain
    ///         claims stated in its own remarks, and none of them are readable from the code by
    ///         inspection: critical damping (a step never overshoots), a lead of exactly
    ///         <c>1/omega</c> (a constant-velocity goal is trailed by <c>v/omega</c>, and the
    ///         response never peaks above unity), and substepped integration (the same trajectory
    ///         at any frame rate). Each one is measured here rather than asserted.
    ///     </para>
    ///     <para>
    ///         Numbers below are the design intent — the lag law is closed-form, the gains are
    ///         evaluated from <c>H(s) = (w^2 + w*s)/(s + w)^2</c> — not the values that happened
    ///         to come out of a run.
    ///     </para>
    /// </remarks>
    public sealed class PursuitTrackerTests
    {
        /// <summary>The profile default for the head, and the response every test here uses unless it says otherwise.</summary>
        private const float ResponseSeconds = 0.2f;

        /// <summary>Large enough that the safety envelope never engages except where a test asks it to.</summary>
        private const float GenerousMaxSpeed = 1_000_000f;

        private const float GenerousMaxAccel = 1_000_000f;

        // ------------------------------------------------------------------ step response

        [Test]
        public void CriticallyDampedStep_NeverOvershoots()
        {
            // Seeded at rest at 0, then handed a still goal of 1: critical damping is exactly the
            // claim that this approaches from below and stops, so any sample above the goal is
            // the type being under-damped and the head ringing on every re-target.
            var tracker = new PursuitTracker();
            tracker.Seed(0f);

            float peak = 0f;
            const float dt = 1f / 60f;
            for (int i = 0; i < 300; i++)
                peak = Mathf.Max(
                    peak, tracker.Step(1f, 0f, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, dt));

            Assert.That(peak, Is.LessThanOrEqualTo(1f + 1e-3f),
                $"A critically damped step must not overshoot its goal; peaked at {peak:F5} against a goal of 1.");
            Assert.That(tracker.Current, Is.EqualTo(1f).Within(1e-3f),
                $"and it must still arrive: settled at {tracker.Current:F5}.");
        }

        [Test]
        public void SteppedResponse_MatchesTheCriticallyDampedClosedForm()
        {
            // With a still goal the lead term vanishes and the tracker is exactly
            // x'' = w^2 (g - x) - 2w x', whose critically damped step response is
            // 1 - (1 + w t) e^(-w t). This is the derivation check: the substepped semi-implicit
            // integration has to reproduce the analytic curve, not merely something shaped like it.
            var tracker = new PursuitTracker();
            tracker.Seed(0f);

            const float dt = 1f / 240f;
            float omega = 2f / ResponseSeconds;
            float worst = 0f;
            float arrival = float.PositiveInfinity;

            for (int i = 1; i <= 480; i++)
            {
                float value = tracker.Step(
                    1f, 0f, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, dt);
                float t = i * dt;
                float expected = 1f - ((1f + (omega * t)) * Mathf.Exp(-omega * t));
                worst = Mathf.Max(worst, Mathf.Abs(value - expected));
                if (value >= 0.95f && float.IsPositiveInfinity(arrival)) arrival = t;
            }

            // 0.02 is the semi-implicit integrator's own first-order error at h = 1/240 with
            // w = 10 (measured 0.013), not a fitted number: the shape law is exact, the residual
            // is the discretisation, and it shrinks with the step.
            Assert.That(worst, Is.LessThanOrEqualTo(0.02f),
                $"The step response must match 1-(1+wt)e^-wt within 0.02; worst error {worst:F5}.");

            // The closed form crosses 95% at w*t = 4.744, i.e. 0.474 s at a 0.2 s response — so
            // "settles in roughly two response times" is a description of the knee, not a bound
            // at exactly 2T. Pinned against the analytic crossing, with 10% of slack.
            Assert.That(arrival, Is.EqualTo(4.744f / omega).Within(4.744f / omega * 0.1f),
                $"95% arrival must land on the analytic crossing {4.744f / omega:F3}s; measured {arrival:F3}s.");
        }

        // ------------------------------------------------------------------ the lag law

        [Test]
        public void ConstantVelocityGoal_IsTrailedByVelocityOverOmega()
        {
            // The lead of 1/w halves the ramp lag, to v/w = v * responseSeconds / 2. This is the
            // number the head's trailing distance is budgeted against downstream, so it is
            // measured to 10% rather than "roughly".
            const float velocity = 40f;
            float omega = 2f / ResponseSeconds;
            float expectedLag = velocity / omega;

            var tracker = new PursuitTracker();
            tracker.Seed(0f);

            const float dt = 1f / 120f;
            float goal = 0f;
            float lag = 0f;

            // Five response times of ramp is long past the transient; the end is steady state.
            for (int i = 0; i < 600; i++)
            {
                goal += velocity * dt;
                float value = tracker.Step(
                    goal, velocity, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, dt);
                lag = goal - value;
            }

            Assert.That(lag, Is.EqualTo(expectedLag).Within(expectedLag * 0.1f),
                $"A goal moving at {velocity} u/s must be trailed by v/omega = {expectedLag:F3}; measured {lag:F3}.");
        }

        // ------------------------------------------------------------------ frequency response

        [Test]
        public void FrequencyResponse_PassesSlowMotionAndAttenuatesShake()
        {
            // The whole point of a bandwidth: a 0.2 Hz drift is followed essentially intact, a
            // 1 Hz shake is not. Closed form for H(s) = (w^2 + w*s)/(s + w)^2 at w = 10 rad/s
            // gives 0.992 at 0.2 Hz and 0.847 at 1 Hz.
            float slowGain = MeasureGain(0.2f, 16f);
            float fastGain = MeasureGain(1f, 16f);

            Assert.That(slowGain, Is.GreaterThanOrEqualTo(0.98f),
                $"A 0.2 Hz drift must be followed essentially intact; gain was {slowGain:F3}.");
            Assert.That(fastGain, Is.LessThanOrEqualTo(0.9f),
                $"A 1 Hz shake must be attenuated by the lane's own bandwidth; gain was {fastGain:F3}.");
            Assert.That(slowGain, Is.LessThanOrEqualTo(1.001f),
                $"The lead is chosen so the response never peaks above unity; gain was {slowGain:F3}.");
        }

        /// <summary>
        ///     Steady-state amplitude ratio for a sinusoidal goal, measured peak-to-peak over the
        ///     last two whole cycles so the start transient is not part of the answer.
        /// </summary>
        private static float MeasureGain(float hertz, float amplitudeDegrees)
        {
            var tracker = new PursuitTracker();
            tracker.Seed(0f);

            const float dt = 1f / 240f;
            const int cycles = 8;
            float signalOmega = 2f * Mathf.PI * hertz;
            int steps = Mathf.CeilToInt(cycles / hertz / dt);
            float measureFrom = (cycles - 2) / hertz;

            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = 1; i <= steps; i++)
            {
                float t = i * dt;
                float goal = amplitudeDegrees * Mathf.Sin(signalOmega * t);
                float goalVelocity = amplitudeDegrees * signalOmega * Mathf.Cos(signalOmega * t);
                float value = tracker.Step(
                    goal, goalVelocity, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, dt);

                if (t < measureFrom) continue;
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }

            return (max - min) / (2f * amplitudeDegrees);
        }

        // ------------------------------------------------------------------ frame-rate stability

        [Test]
        public void StepTrace_IsTheSameAtThirtyAndOneHundredTwentyHertz()
        {
            // Substepped integration is a design intent until a trace at two rates makes it a
            // measurement: an unsubstepped spring at 30 Hz is a visibly stiffer trajectory.
            List<float> slow = StepTrace(1f / 30f, 20f, 3f);
            List<float> fast = StepTrace(1f / 120f, 20f, 3f);

            float worst = 0f;
            for (int i = 0; i < slow.Count; i++)
            {
                // Every 30 Hz sample lands on a 120 Hz sample: compare like for like.
                int mirror = ((i + 1) * 4) - 1;
                if (mirror >= fast.Count) break;
                worst = Mathf.Max(worst, Mathf.Abs(slow[i] - fast[mirror]));
            }

            Assert.That(worst, Is.LessThanOrEqualTo(0.2f),
                $"The same 20 deg step must trace the same path at 30 Hz and 120 Hz; worst divergence {worst:F4} deg.");
        }

        private static List<float> StepTrace(float dt, float goal, float seconds)
        {
            var tracker = new PursuitTracker();
            tracker.Seed(0f);

            int steps = Mathf.CeilToInt(seconds / dt);
            var trace = new List<float>(steps);
            for (int i = 0; i < steps; i++)
                trace.Add(tracker.Step(goal, 0f, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, dt));

            return trace;
        }

        // ------------------------------------------------------------------ seeding

        [Test]
        public void Seed_CarriesVelocityIntoTheFirstStep()
        {
            // A hand-off from the ballistic lane passes its velocity in. If Seed dropped it, the
            // channel would restart from rest and the hand-off would be a visible stop.
            const float dt = 1f / 1000f;
            const float velocity = 60f;

            var atRest = new PursuitTracker();
            atRest.Seed(0f);
            atRest.Step(0f, 0f, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, dt);

            var moving = new PursuitTracker();
            moving.Seed(0f, velocity);
            moving.Step(0f, 0f, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, dt);

            Assert.That(atRest.Current, Is.EqualTo(0f).Within(1e-4f),
                "A tracker seeded at rest on its goal must not move.");
            Assert.That(moving.Velocity, Is.GreaterThan(velocity * 0.9f),
                $"Seeded momentum must survive the first step; velocity was {moving.Velocity:F2} of {velocity}.");
            Assert.That(moving.Current, Is.EqualTo(velocity * dt).Within(velocity * dt * 0.05f),
                $"The first step after Seed(x, v) must move about v*dt = {velocity * dt:F5}; moved {moving.Current:F5}.");
        }

        [Test]
        public void Reset_RestartsOnTheNextGoal()
        {
            var tracker = new PursuitTracker();
            tracker.Seed(0f, 100f);
            tracker.Reset();

            float value = tracker.Step(
                45f, 0f, ResponseSeconds, GenerousMaxSpeed, GenerousMaxAccel, 1f / 60f);

            Assert.That(value, Is.EqualTo(45f).Within(1e-4f),
                "After Reset the next Step re-initialises onto its goal rather than travelling to it.");
            Assert.That(tracker.Velocity, Is.EqualTo(0f).Within(1e-4f),
                "and it does so at rest, not carrying the velocity Reset was supposed to discard.");
        }

        // ------------------------------------------------------------------ safety envelope

        [Test]
        public void SafetyEnvelope_BoundsSpeedAndAcceleration()
        {
            // A goal far enough away that the unclamped spring would ask for thousands of deg/s.
            const float maxSpeed = 100f;
            const float maxAccel = 800f;
            const float dt = 1f / 60f;

            var tracker = new PursuitTracker();
            tracker.Seed(0f);

            float previousVelocity = 0f;
            float peakSpeed = 0f;
            float peakAccel = 0f;
            for (int i = 0; i < 240; i++)
            {
                tracker.Step(2000f, 0f, ResponseSeconds, maxSpeed, maxAccel, dt);
                peakSpeed = Mathf.Max(peakSpeed, Mathf.Abs(tracker.Velocity));
                peakAccel = Mathf.Max(peakAccel, Mathf.Abs(tracker.Velocity - previousVelocity) / dt);
                previousVelocity = tracker.Velocity;
            }

            Assert.That(peakSpeed, Is.LessThanOrEqualTo(maxSpeed * 1.001f),
                $"Speed must stay inside the envelope; peaked at {peakSpeed:F2} against a cap of {maxSpeed}.");
            Assert.That(peakAccel, Is.LessThanOrEqualTo(maxAccel * 1.001f),
                $"Acceleration must stay inside the envelope; peaked at {peakAccel:F2} against a cap of {maxAccel}.");
        }
    }
}
