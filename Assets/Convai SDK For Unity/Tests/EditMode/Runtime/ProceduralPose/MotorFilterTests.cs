using Convai.Runtime.Animation.ProceduralPose;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     Behavior tests for <see cref="MotorFilter" />: a rate limiter with
    ///     trapezoidal braking that must (a) hold every trajectory inside its velocity and
    ///     acceleration caps without clamp-induced ringing, (b) be numerically TRANSPARENT to
    ///     in-budget signals — it is a limiter, not a spring — (c) never exceed the acceleration
    ///     cap even under a super-human square wave, (d) allocate nothing, and (e) hold state on
    ///     zero dt and re-snap after <see cref="MotorFilter.Reset" />.
    /// </summary>
    public sealed class MotorFilterTests
    {
        // The compositor's tonic spine caps (MotorLimits) — representative human-postural budget.
        private const float TonicMaxSpeed = 45f;
        private const float TonicMaxAccel = 100f;

        [Test]
        public void StepInput_RespectsVelocityAndAccelerationCaps_AndConvergesWithoutRinging()
        {
            var filter = new MotorFilter();
            const float dt = 1f / 120f;

            // First Step initializes (snaps) the filter at rest on 0 — the 0→30° step happens
            // on the next Step, which is the trajectory under test.
            filter.Step(0f, TonicMaxSpeed, TonicMaxAccel, dt);

            float previousValue = 0f;
            float previousMeasuredVelocity = 0f;
            float previousInternalVelocity = 0f;

            float maxMeasuredSpeed = 0f;
            float maxInternalAcceleration = 0f;
            float maxMeasuredAcceleration = 0f;
            float maxVelocityInconsistency = 0f;
            float maxOvershoot = 0f;
            float value = 0f;

            // 5 s at 120 Hz — the accelerate/cruise/brake profile completes in ~1.1 s.
            for (int i = 0; i < 600; i++)
            {
                value = filter.Step(30f, TonicMaxSpeed, TonicMaxAccel, dt);

                float measuredVelocity = (value - previousValue) / dt;
                float measuredAcceleration = (measuredVelocity - previousMeasuredVelocity) / dt;
                float internalAcceleration = (filter.Velocity - previousInternalVelocity) / dt;

                maxMeasuredSpeed = Mathf.Max(maxMeasuredSpeed, Mathf.Abs(measuredVelocity));
                maxMeasuredAcceleration = Mathf.Max(maxMeasuredAcceleration, Mathf.Abs(measuredAcceleration));
                maxInternalAcceleration = Mathf.Max(maxInternalAcceleration, Mathf.Abs(internalAcceleration));
                maxVelocityInconsistency = Mathf.Max(
                    maxVelocityInconsistency, Mathf.Abs(measuredVelocity - filter.Velocity));
                maxOvershoot = Mathf.Max(maxOvershoot, value - 30f);

                previousValue = value;
                previousMeasuredVelocity = measuredVelocity;
                previousInternalVelocity = filter.Velocity;
            }

            Assert.That(maxMeasuredSpeed, Is.LessThanOrEqualTo(TonicMaxSpeed + 1e-3f),
                "Output-derived per-frame velocity must never exceed the speed cap.");
            Assert.That(maxInternalAcceleration, Is.LessThanOrEqualTo(TonicMaxAccel + 1e-3f),
                "Per-frame velocity-state change must never exceed the acceleration cap.");
            // Differentiating float32 POSITION samples twice amplifies quantization noise by
            // 1/dt² (~0.06°/s² here — 60× the 1e-3 tolerance), so the output-derived
            // acceleration gets a quantization-aware bound; the spec-tight 1e-3 bound is
            // carried by the velocity-state assertion above, and the consistency assertion
            // below proves the output is exactly that velocity state integrated.
            Assert.That(maxMeasuredAcceleration, Is.LessThanOrEqualTo(TonicMaxAccel + 0.1f),
                "Output-derived per-frame acceleration must never exceed the acceleration cap "
                + "beyond float32 position-quantization noise.");
            Assert.That(maxVelocityInconsistency, Is.LessThan(1e-2f),
                "The output trajectory must be the internal velocity state integrated — no hidden motion.");

            Assert.That(value, Is.EqualTo(30f).Within(0.01f),
                "The step response must converge onto the target.");
            Assert.That(maxOvershoot, Is.LessThanOrEqualTo(3f),
                "Trapezoidal braking must keep overshoot within 10% of the step (it measures ~0.2°).");
        }

        [Test]
        public void InBudgetSine_PassesThroughNumericallyUnchanged()
        {
            var filter = new MotorFilter();
            const float dt = 1f / 60f;

            // 0.3 Hz, ±3°: peak velocity ~5.7°/s, peak acceleration ~10.7°/s² — comfortably
            // inside the tonic budget, so the limiter must be an identity. The sinusoid is
            // phased to start at ZERO velocity (cosine): no causal acceleration-limited filter
            // can track a signal that starts at peak velocity from rest, so transparency is
            // asserted from a rate-matched start — the physically meaningful statement.
            float maxError = 0f;
            for (int i = 0; i < 600; i++) // 10 s
            {
                float target = 3f * Mathf.Cos(2f * Mathf.PI * 0.3f * (i * dt));
                float output = filter.Step(target, TonicMaxSpeed, TonicMaxAccel, dt);
                maxError = Mathf.Max(maxError, Mathf.Abs(output - target));
            }

            Assert.That(maxError, Is.LessThan(1e-3f),
                "An in-budget signal must pass through numerically unchanged — the filter is a "
                + $"limiter, not a spring; measured max |out - in| = {maxError}.");
        }

        [Test]
        public void SuperHumanSquareWave_OutputAccelerationNeverExceedsCap()
        {
            var filter = new MotorFilter();
            const float dt = 1f / 60f;

            filter.Step(0f, TonicMaxSpeed, TonicMaxAccel, dt);

            float previousValue = 0f;
            float previousMeasuredVelocity = 0f;
            float maxMeasuredSpeed = 0f;
            float maxMeasuredAcceleration = 0f;

            // 3 Hz ±1° square wave — instantaneous edges, far beyond any human accel budget.
            for (int i = 0; i < 600; i++)
            {
                float t = i * dt;
                float target = (t * 3f) % 1f < 0.5f ? 1f : -1f;
                float value = filter.Step(target, TonicMaxSpeed, TonicMaxAccel, dt);

                float measuredVelocity = (value - previousValue) / dt;
                float measuredAcceleration = (measuredVelocity - previousMeasuredVelocity) / dt;
                maxMeasuredSpeed = Mathf.Max(maxMeasuredSpeed, Mathf.Abs(measuredVelocity));
                maxMeasuredAcceleration = Mathf.Max(maxMeasuredAcceleration, Mathf.Abs(measuredAcceleration));

                previousValue = value;
                previousMeasuredVelocity = measuredVelocity;
            }

            Assert.That(maxMeasuredAcceleration, Is.LessThanOrEqualTo(TonicMaxAccel + 1e-3f),
                "A super-human square wave must come out acceleration-capped on every frame.");
            Assert.That(maxMeasuredSpeed, Is.LessThanOrEqualTo(TonicMaxSpeed + 1e-3f),
                "A super-human square wave must come out velocity-capped on every frame.");
        }

        [Test]
        public void Step_SteadyState_AllocatesNothing()
        {
            var filter = new MotorFilter();

            const int warmupIterations = 1000;
            const int measuredIterations = 1000;

            RunSteps(ref filter, 0, warmupIterations);

            // Both measured batches must remain allocation-free after the explicit warm-up.
            long allocationCount = MeasureAllocationCount(ref filter, warmupIterations, measuredIterations);

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"MotorFilter.Step must produce zero managed allocation events in steady state; measured "
                + $"{allocationCount} allocation events over {measuredIterations} steps.");
        }

        private static void RunSteps(ref MotorFilter filter, int startIndex, int iterations)
        {
            const float dt = 1f / 60f;
            for (int i = 0; i < iterations; i++)
            {
                // Mixed in-budget and super-human targets so both code paths stay measured.
                float target = (startIndex + i) % 7 == 0 ? 25f : 2f * Mathf.Sin((startIndex + i) * 0.05f);
                filter.Step(target, TonicMaxSpeed, TonicMaxAccel, dt);
            }
        }

        private static long MeasureAllocationCount(ref MotorFilter filter, int startIndex, int iterations)
        {
            MotorFilter measuredFilter = filter;
            long allocationCount = AllocationCounter.Measure(() =>
            {
                RunSteps(ref measuredFilter, startIndex, iterations);
            });
            filter = measuredFilter;
            return allocationCount;
        }

        [Test]
        public void ZeroDeltaTime_HoldsState_AndResetResnapsOnNextStep()
        {
            var filter = new MotorFilter();
            const float dt = 1f / 60f;

            float snapped = filter.Step(5f, TonicMaxSpeed, TonicMaxAccel, dt);
            Assert.That(snapped, Is.EqualTo(5f),
                "The first Step must snap to its target (initialization, no transient from 0).");

            Assert.That(filter.Step(90f, TonicMaxSpeed, TonicMaxAccel, 0f), Is.EqualTo(5f),
                "A zero deltaTime must hold the current state, ignoring the new target.");
            Assert.That(filter.Step(90f, TonicMaxSpeed, TonicMaxAccel, -dt), Is.EqualTo(5f),
                "A negative deltaTime must hold the current state too.");

            filter.Reset();
            float resnapped = filter.Step(12f, TonicMaxSpeed, TonicMaxAccel, dt);
            Assert.That(resnapped, Is.EqualTo(12f),
                "Reset must re-arm the initialization snap: the next Step lands on its target at rest.");
        }
    }
}
