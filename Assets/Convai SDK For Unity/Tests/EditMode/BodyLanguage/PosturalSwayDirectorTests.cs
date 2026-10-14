using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Tests.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="PosturalSwayDirector" />: the two-octave band-limited sway —
    ///     determinism, amplitude scaling with the per-state AmbientDrift value, the
    ///     disable/re-enable envelope (silent output, phases keep advancing), the band-limit
    ///     (no implausible per-frame slope), and zero allocation.
    /// </summary>
    public sealed class PosturalSwayDirectorTests
    {
        private const float Dt = 1f / 60f;

        [Test]
        public void Determinism_SameSeed_ProducesIdenticalTrace()
        {
            var directorA = new PosturalSwayDirector();
            directorA.Seed(555);
            var directorB = new PosturalSwayDirector();
            directorB.Seed(555);

            for (int i = 0; i < 60 * 30; i++)
            {
                directorA.Tick(true, 1f, Dt);
                directorB.Tick(true, 1f, Dt);
                Assert.That(directorA.SwaySagittal01, Is.EqualTo(directorB.SwaySagittal01),
                    $"Identical seed + tick sequence must produce an identical sway trace at tick {i}.");
                Assert.That(directorA.SwayLateral01, Is.EqualTo(directorB.SwayLateral01));
            }
        }

        [Test]
        public void AmplitudeScalesWithStateAmbientDrift01()
        {
            var directorFull = new PosturalSwayDirector();
            directorFull.Seed(3);
            var directorLow = new PosturalSwayDirector();
            directorLow.Seed(3);

            float peakFull = 0f;
            float peakLow = 0f;

            for (int i = 0; i < 60 * 30; i++)
            {
                directorFull.Tick(true, 1f, Dt);
                directorLow.Tick(true, 0.2f, Dt);
                peakFull = System.Math.Max(peakFull, System.Math.Abs(directorFull.SwaySagittal01));
                peakLow = System.Math.Max(peakLow, System.Math.Abs(directorLow.SwaySagittal01));
            }

            Assert.That(peakLow, Is.LessThan(peakFull),
                "A lower stateAmbientDrift01 must produce a smaller-amplitude sway than a full one.");
        }

        [Test]
        public void ZeroAmbientDrift_ProducesSilentOutput()
        {
            var director = new PosturalSwayDirector();
            director.Seed(4);

            for (int i = 0; i < 60 * 20; i++)
            {
                director.Tick(true, 0f, Dt);
                Assert.That(director.SwaySagittal01, Is.EqualTo(0f), "AmbientDrift == 0 must silence the sway entirely.");
                Assert.That(director.SwayLateral01, Is.EqualTo(0f));
            }
        }

        [Test]
        public void Disabled_ProducesSilentOutput()
        {
            var director = new PosturalSwayDirector();
            director.Seed(5);

            for (int i = 0; i < 60 * 10; i++)
            {
                director.Tick(false, 1f, Dt);
                Assert.That(director.SwaySagittal01, Is.EqualTo(0f).Within(1e-4f),
                    "Disabled must decay/hold the sway output at (near) zero.");
                Assert.That(director.SwayLateral01, Is.EqualTo(0f).Within(1e-4f));
            }
        }

        [Test]
        public void ReEnabling_RampsBackIn_WithoutPopping()
        {
            var director = new PosturalSwayDirector();
            director.Seed(6);

            // Long disabled window so the underlying octaves have clearly moved on.
            for (int i = 0; i < 60 * 20; i++)
                director.Tick(false, 1f, Dt);

            float firstTickAfterEnable;
            director.Tick(true, 1f, Dt);
            firstTickAfterEnable = director.SwaySagittal01;

            Assert.That(System.Math.Abs(firstTickAfterEnable), Is.LessThan(0.05f),
                "Re-enabling must ramp the envelope in from zero, not pop straight to full amplitude in one tick.");

            bool sawNonTrivialOutput = false;
            for (int i = 0; i < 60 * 3; i++)
            {
                director.Tick(true, 1f, Dt);
                if (System.Math.Abs(director.SwaySagittal01) > 0.05f || System.Math.Abs(director.SwayLateral01) > 0.05f)
                    sawNonTrivialOutput = true;
            }

            Assert.IsTrue(sawNonTrivialOutput, "After the envelope ramps in (~1s), the sway must resume producing visible output.");
        }

        [Test]
        public void BandLimited_NoImplausibleSingleFrameSlope()
        {
            var director = new PosturalSwayDirector();
            director.Seed(7);

            // Warm up so the enable envelope has fully settled to 1.
            for (int i = 0; i < 60 * 5; i++)
                director.Tick(true, 1f, Dt);

            float previousSagittal = director.SwaySagittal01;
            float previousLateral = director.SwayLateral01;

            for (int i = 0; i < 60 * 60; i++)
            {
                director.Tick(true, 1f, Dt);
                float sagittal = director.SwaySagittal01;
                float lateral = director.SwayLateral01;

                // Analytic worst-case per-tick delta: slow octave alpha*2 + 0.35 * fast octave
                // alpha*2 (both bounded well under 0.03 at 60fps) — generous margin below.
                Assert.That(System.Math.Abs(sagittal - previousSagittal), Is.LessThan(0.05f),
                    $"Sagittal sway must never jump by an implausible amount in a single tick ({i}).");
                Assert.That(System.Math.Abs(lateral - previousLateral), Is.LessThan(0.05f),
                    $"Lateral sway must never jump by an implausible amount in a single tick ({i}).");

                previousSagittal = sagittal;
                previousLateral = lateral;
            }
        }

        [Test]
        public void Reset_ReturnsToZero()
        {
            var director = new PosturalSwayDirector();
            director.Seed(8);

            for (int i = 0; i < 60 * 20; i++)
                director.Tick(true, 1f, Dt);

            director.Reset();

            Assert.That(director.SwaySagittal01, Is.EqualTo(0f));
            Assert.That(director.SwayLateral01, Is.EqualTo(0f));
        }

        [Test]
        public void ZeroAllocation_SteadyStateTick()
        {
            var director = new PosturalSwayDirector();
            director.Seed(9);

            for (int i = 0; i < 500; i++) director.Tick(true, 0.7f, Dt);

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 500; i++) director.Tick(true, 0.7f, Dt);
            });

            Assert.That(allocationCount, Is.EqualTo(0L), "PosturalSwayDirector.Tick must produce zero managed allocation events in steady state.");
        }
    }
}
