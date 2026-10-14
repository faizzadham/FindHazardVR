using Convai.Modules.Gaze.Core.Policy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Proxemic intimacy regulation: the smoothed closeness factor and the three output
    ///     scales it drives (aversion floor, face-scan radius, blink rate) — equilibrium-theory
    ///     softening as the player leans in close, with no flicker at the distance boundary.
    /// </summary>
    public sealed class ProxemicRegulatorTests
    {
        private const float CloseDistance = 0.6f;
        private const float Dt60Hz = 1f / 60f;

        private ProxemicRegulator _regulator;

        [SetUp]
        public void SetUp() => _regulator = new ProxemicRegulator();

        /// <summary>Ticks the regulator for a fixed duration at the given step, returning the final state.</summary>
        private void TickFor(
            float seconds, float distance, bool hasDistance = true, bool enabled = true,
            float intensity = 1f, float dt = Dt60Hz)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / dt));
            for (int i = 0; i < steps; i++)
                _regulator.Tick(enabled, hasDistance, distance, CloseDistance, intensity, dt);
        }

        [Test]
        public void Far_AllOutputsNeutral()
        {
            TickFor(5f, distance: 5f);

            Assert.That(_regulator.AversionFloor, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(_regulator.FaceScanRadiusScale, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(_regulator.BlinkRateScale, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void VeryClose_OutputsConvergeToExpectedValues()
        {
            // Well inside the close distance (falloff is a small internal band) — closeness
            // saturates to 1 at full intensity.
            TickFor(5f, distance: 0.05f);

            Assert.That(_regulator.Closeness, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(_regulator.AversionFloor, Is.EqualTo(0.2f).Within(1e-3f),
                "Aversion floor must reach ~0.2 at full closeness/intensity.");
            Assert.That(_regulator.FaceScanRadiusScale, Is.EqualTo(1.5f).Within(1e-3f),
                "Face-scan radius scale must reach ~1.5 (x1.5) at full closeness/intensity.");
            Assert.That(_regulator.BlinkRateScale, Is.EqualTo(1.2f).Within(1e-3f),
                "Blink rate scale must reach ~1.2 (x1.2) at full closeness/intensity.");
        }

        [Test]
        public void Ramp_IsSmoothed_NoStepChangeOnFirstTick()
        {
            // Starts far (neutral), then the player is suddenly very close: the very first tick
            // must not jump straight to the converged values.
            _regulator.Tick(true, true, 5f, CloseDistance, 1f, Dt60Hz);
            Assert.That(_regulator.Closeness, Is.EqualTo(0f).Within(1e-4f));

            _regulator.Tick(true, true, 0.05f, CloseDistance, 1f, Dt60Hz);
            Assert.That(_regulator.Closeness, Is.LessThan(0.1f),
                "A single 1/60s tick must only move the smoothed factor a small bounded step, not snap to the target.");
            Assert.That(_regulator.AversionFloor, Is.LessThan(0.05f),
                "Aversion floor must not jump to its converged value on the first tick.");
        }

        [Test]
        public void Oscillation_AroundThreshold_DoesNotFlicker()
        {
            // Warm up near the threshold so the smoothed factor is mid-range, then oscillate the
            // raw distance reading by +/-2 cm around the close-distance threshold every tick.
            TickFor(1f, distance: CloseDistance);

            float previous = _regulator.AversionFloor;
            for (int i = 0; i < 30; i++)
            {
                float distance = (i % 2 == 0) ? CloseDistance - 0.02f : CloseDistance + 0.02f;
                _regulator.Tick(true, true, distance, CloseDistance, 1f, Dt60Hz);

                float current = _regulator.AversionFloor;
                Assert.That(Mathf.Abs(current - previous), Is.LessThan(0.01f),
                    "A +/-2cm oscillation around the threshold must not flicker the composed output tick-to-tick.");
                previous = current;
            }
        }

        [Test]
        public void DisabledFlag_DecaysToNeutral()
        {
            TickFor(5f, distance: 0.05f); // build up full closeness first
            Assert.That(_regulator.Closeness, Is.GreaterThan(0.9f));

            TickFor(5f, distance: 0.05f, enabled: false); // flip the profile toggle off

            Assert.That(_regulator.AversionFloor, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(_regulator.FaceScanRadiusScale, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(_regulator.BlinkRateScale, Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void IntensityScalesTheOutputs()
        {
            TickFor(5f, distance: 0.05f, intensity: 0.5f);

            Assert.That(_regulator.AversionFloor, Is.EqualTo(0.1f).Within(1e-3f));
            Assert.That(_regulator.FaceScanRadiusScale, Is.EqualTo(1.25f).Within(1e-3f));
            Assert.That(_regulator.BlinkRateScale, Is.EqualTo(1.1f).Within(1e-3f));
        }

        [Test]
        public void PlayerAbsent_DecaysToNeutral()
        {
            TickFor(5f, distance: 0.05f); // build up full closeness first
            Assert.That(_regulator.Closeness, Is.GreaterThan(0.9f));

            TickFor(5f, distance: 0f, hasDistance: false); // player anchor unresolved (no camera)

            Assert.That(_regulator.AversionFloor, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(_regulator.FaceScanRadiusScale, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(_regulator.BlinkRateScale, Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void Reset_ReturnsToNeutral()
        {
            TickFor(5f, distance: 0.05f);
            Assert.That(_regulator.Closeness, Is.GreaterThan(0.9f));

            _regulator.Reset();

            Assert.That(_regulator.Closeness, Is.EqualTo(0f));
            Assert.That(_regulator.AversionFloor, Is.EqualTo(0f));
            Assert.That(_regulator.FaceScanRadiusScale, Is.EqualTo(1f));
            Assert.That(_regulator.BlinkRateScale, Is.EqualTo(1f));
        }
    }
}
