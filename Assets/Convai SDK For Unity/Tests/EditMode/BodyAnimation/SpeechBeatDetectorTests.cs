using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="SpeechBeatDetector" />: rising-edge onset
    ///     detection over a live 0..1 speech-energy signal, adaptive baseline, refractory
    ///     enforcement, and monotonic strength scaling. Pure POCO — no graph/animator needed.
    /// </summary>
    public sealed class SpeechBeatDetectorTests
    {
        private const float Dt = 0.05f; // 50ms ticks

        [Test]
        public void SilenceThenSharpRise_FiresRisingEdgeOnset()
        {
            var detector = new SpeechBeatDetector();

            // Settle the baseline near zero on silence first.
            for (int i = 0; i < 10; i++)
                detector.Tick(0f, Dt, refractorySeconds: 1f, out _);

            bool fired = detector.Tick(0.8f, Dt, refractorySeconds: 1f, out float strength);

            Assert.IsTrue(fired, "A sharp rise above the adaptive baseline must fire an onset.");
            Assert.That(strength, Is.GreaterThan(0f));
            Assert.That(strength, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void LoudSteadySpeech_DoesNotMachineGunBeats()
        {
            var detector = new SpeechBeatDetector();
            int fireCount = 0;

            // 4 seconds of constant loud energy: after the baseline catches up, the signal
            // never exceeds (baseline + margin) again, so onsets must stop firing.
            for (int i = 0; i < 80; i++)
            {
                if (detector.Tick(0.8f, Dt, refractorySeconds: 0.2f, out _))
                    fireCount++;
            }

            Assert.That(fireCount, Is.LessThanOrEqualTo(2),
                "Sustained constant-level speech must not repeatedly re-trigger onsets once the baseline adapts.");
        }

        [Test]
        public void Refractory_SuppressesASecondOnsetWithinTheWindow()
        {
            var detector = new SpeechBeatDetector();
            for (int i = 0; i < 10; i++)
                detector.Tick(0f, Dt, refractorySeconds: 1f, out _);

            bool firstFired = detector.Tick(0.8f, Dt, refractorySeconds: 1f, out _);
            Assert.IsTrue(firstFired);

            // Dip and rise again well inside the 1s refractory window.
            detector.Tick(0.1f, Dt, refractorySeconds: 1f, out _);
            bool secondFired = detector.Tick(0.9f, Dt, refractorySeconds: 1f, out _);

            Assert.IsFalse(secondFired, "A second onset inside the refractory window must be suppressed.");
        }

        [Test]
        public void AfterRefractoryElapses_ANewOnsetCanFireAgain()
        {
            var detector = new SpeechBeatDetector();
            for (int i = 0; i < 10; i++)
                detector.Tick(0f, Dt, refractorySeconds: 0.2f, out _);

            Assert.IsTrue(detector.Tick(0.8f, Dt, refractorySeconds: 0.2f, out _));

            // Dip below threshold and wait out the refractory window (0.2s = 4 ticks @ 0.05s).
            for (int i = 0; i < 6; i++)
                detector.Tick(0.05f, Dt, refractorySeconds: 0.2f, out _);

            bool refired = detector.Tick(0.8f, Dt, refractorySeconds: 0.2f, out float strength);
            Assert.IsTrue(refired, "A fresh onset after the refractory window and a dip must fire again.");
            Assert.That(strength, Is.GreaterThan(0f));
        }

        [Test]
        public void Strength_ScalesMonotonicallyWithOnsetSize()
        {
            // A wide margin keeps all three strengths below the 0..1 clamp ceiling so the
            // comparison actually exercises the scaling math rather than three clamped 1s.
            float StrengthFor(float peakEnergy)
            {
                var detector = new SpeechBeatDetector(margin: 0.6f);
                for (int i = 0; i < 10; i++)
                    detector.Tick(0f, Dt, refractorySeconds: 1f, out _);
                detector.Tick(peakEnergy, Dt, refractorySeconds: 1f, out float strength);
                return strength;
            }

            float small = StrengthFor(0.65f);
            float medium = StrengthFor(0.8f);
            float large = StrengthFor(0.95f);

            Assert.That(medium, Is.GreaterThan(small));
            Assert.That(large, Is.GreaterThan(medium));
            Assert.That(large, Is.LessThan(1f), "Sanity: the chosen peaks must stay under the clamp ceiling.");
        }

        [Test]
        public void Reset_ClearsBaselineAndRefractoryState()
        {
            var detector = new SpeechBeatDetector();
            for (int i = 0; i < 10; i++)
                detector.Tick(0.8f, Dt, refractorySeconds: 1f, out _);

            detector.Reset();

            Assert.That(detector.BaselineForTests, Is.EqualTo(0f));

            // A fresh rise right after Reset should be free to fire (no refractory carry-over).
            bool fired = detector.Tick(0.8f, Dt, refractorySeconds: 1f, out _);
            Assert.IsTrue(fired);
        }

        [Test]
        public void NoEnergy_NeverFires()
        {
            var detector = new SpeechBeatDetector();
            bool anyFired = false;
            for (int i = 0; i < 40; i++)
            {
                if (detector.Tick(0f, Dt, refractorySeconds: 1f, out _))
                    anyFired = true;
            }

            Assert.IsFalse(anyFired);
        }
    }
}
