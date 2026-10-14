using Convai.Modules.BodyLanguage.Core.Pose;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Behavior tests for <see cref="AnimatedBreathMotionEstimator" /> (adaptive
    ///     layering): a constant sample settles at zero baked amplitude and no duck; a
    ///     slow synthetic oscillation is detected and ducks the procedural breath; fast jitter is
    ///     rejected by the low-pass; a zero state-duck-weight freezes the estimate entirely; the
    ///     duck factor never drops below its floor; the steady-state Tick path is allocation-free.
    /// </summary>
    public sealed class AnimatedBreathMotionEstimatorTests
    {
        private const float Dt = 1f / 60f;

        private static Quaternion OscillatingSample(float elapsedSeconds, float amplitudeDegrees, float frequencyHz) =>
            Quaternion.AngleAxis(amplitudeDegrees * Mathf.Sin(2f * Mathf.PI * frequencyHz * elapsedSeconds), Vector3.right);

        [Test]
        public void ConstantSample_SettlesAtZeroAmplitude_AndNoDuck()
        {
            var estimator = new AnimatedBreathMotionEstimator();

            for (int i = 0; i < 600; i++)
                estimator.Tick(Quaternion.identity, sampleValid: true, stateDuckWeight: 1f, Dt);

            Assert.That(estimator.BakedAmplitudeDegrees, Is.EqualTo(0f).Within(0.01f),
                "A constant sample must never read as baked breathing.");
            Assert.That(estimator.DuckFactor, Is.EqualTo(1f).Within(1e-3f),
                "No baked amplitude must mean no duck.");
        }

        [Test]
        public void SlowOscillation_IsDetected_AndDucksProceduralBreath()
        {
            var estimator = new AnimatedBreathMotionEstimator();

            const float amplitudeDegrees = 1.5f;
            const float frequencyHz = 0.25f;
            const int steps = 20 * 60; // 20 simulated seconds at Dt

            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                estimator.Tick(OscillatingSample(t, amplitudeDegrees, frequencyHz), sampleValid: true, stateDuckWeight: 1f, Dt);
            }

            Assert.That(estimator.BakedAmplitudeDegrees, Is.InRange(0.8f, 2.2f),
                $"A {amplitudeDegrees}° / {frequencyHz}Hz baked oscillation must read as a comparable baked amplitude; measured {estimator.BakedAmplitudeDegrees}.");
            Assert.That(estimator.DuckFactor, Is.LessThan(0.7f),
                "A clearly-detected baked oscillation must visibly duck the procedural breath.");
        }

        [Test]
        public void FastJitter_IsRejectedByLowPass_AndDoesNotDuck()
        {
            var estimator = new AnimatedBreathMotionEstimator();

            const float amplitudeDegrees = 1.5f;
            const float frequencyHz = 4f;
            const int steps = 20 * 60;

            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                estimator.Tick(OscillatingSample(t, amplitudeDegrees, frequencyHz), sampleValid: true, stateDuckWeight: 1f, Dt);
            }

            Assert.That(estimator.BakedAmplitudeDegrees, Is.LessThan(0.4f),
                $"Fast ({frequencyHz}Hz) jitter must be rejected by the low-pass filter, not read as breathing; measured {estimator.BakedAmplitudeDegrees}.");
            Assert.That(estimator.DuckFactor, Is.GreaterThan(0.9f),
                "Rejected jitter must not meaningfully duck the procedural breath.");
        }

        [Test]
        public void ZeroStateDuckWeight_FreezesTheEstimate()
        {
            var estimator = new AnimatedBreathMotionEstimator();

            // Initialize with a first valid sample (any weight — init always happens on the
            // first valid sample regardless of weight).
            estimator.Tick(Quaternion.identity, sampleValid: true, stateDuckWeight: 0f, Dt);

            const float amplitudeDegrees = 1.5f;
            const float frequencyHz = 0.25f;
            const int steps = 20 * 60;

            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                estimator.Tick(OscillatingSample(t, amplitudeDegrees, frequencyHz), sampleValid: true, stateDuckWeight: 0f, Dt);
            }

            Assert.That(estimator.BakedAmplitudeDegrees, Is.EqualTo(0f).Within(0.01f),
                "A zero state-duck-weight must freeze the filters entirely — the envelope never advances.");
            Assert.That(estimator.DuckFactor, Is.EqualTo(1f).Within(1e-4f),
                "A zero state-duck-weight must always yield exactly DuckFactor=1, regardless of the frozen amplitude.");
        }

        [Test]
        public void DuckFactor_NeverDropsBelowDuckFloor()
        {
            var estimator = new AnimatedBreathMotionEstimator();

            const float amplitudeDegrees = 5f; // well above the duck curve's saturation point
            const float frequencyHz = 0.25f;
            const int steps = 20 * 60;

            for (int i = 0; i < steps; i++)
            {
                float t = i * Dt;
                estimator.Tick(OscillatingSample(t, amplitudeDegrees, frequencyHz), sampleValid: true, stateDuckWeight: 1f, Dt);

                Assert.That(estimator.DuckFactor, Is.GreaterThanOrEqualTo(0.25f - 1e-4f),
                    $"DuckFactor must never drop below the floor (0.25); measured {estimator.DuckFactor} at step {i}.");
            }

            Assert.That(estimator.DuckFactor, Is.EqualTo(0.25f).Within(0.02f),
                "A large, clearly-saturating oscillation must settle the duck factor at (or very near) its floor.");
        }

        [Test]
        public void Reset_ReturnsToUnestimatedState()
        {
            var estimator = new AnimatedBreathMotionEstimator();

            for (int i = 0; i < 600; i++)
                estimator.Tick(OscillatingSample(i * Dt, 1.5f, 0.25f), sampleValid: true, stateDuckWeight: 1f, Dt);

            estimator.Reset();

            Assert.That(estimator.BakedAmplitudeDegrees, Is.EqualTo(0f));
            Assert.That(estimator.DuckFactor, Is.EqualTo(1f));
        }

        [Test]
        public void SteadyStateTick_AllocatesNothing()
        {
            var estimator = new AnimatedBreathMotionEstimator();

            const int warmupIterations = 1000;
            const int measuredIterations = 1000;

            for (int i = 0; i < warmupIterations; i++)
                estimator.Tick(OscillatingSample(i * Dt, 1.5f, 0.25f), sampleValid: true, stateDuckWeight: i % 3 == 0 ? 0f : 1f, Dt);

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < measuredIterations; i++)
                    estimator.Tick(OscillatingSample(i * Dt, 1.5f, 0.25f), sampleValid: true, stateDuckWeight: i % 3 == 0 ? 0f : 1f, Dt);
            });

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"Tick must produce zero managed allocation events in steady state; measured {allocationCount} allocation events over {measuredIterations} ticks.");
        }
    }
}
