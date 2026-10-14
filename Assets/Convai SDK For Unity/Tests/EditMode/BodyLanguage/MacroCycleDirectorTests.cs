using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Tests.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="MacroCycleDirector" />: the multi-minute seeded idle drift —
    ///     determinism, bounded output, period respect (no early target redraw),
    ///     the disable envelope, reset, and zero allocation.
    /// </summary>
    public sealed class MacroCycleDirectorTests
    {
        private const float Dt = 1f / 60f;

        [Test]
        public void Determinism_SameSeed_ProducesIdenticalOutputs()
        {
            var directorA = new MacroCycleDirector();
            directorA.Seed(4040);
            var directorB = new MacroCycleDirector();
            directorB.Seed(4040);

            for (int i = 0; i < 60 * 60; i++)
            {
                directorA.Tick(true, Dt);
                directorB.Tick(true, Dt);
                Assert.That(directorA.Energy01, Is.EqualTo(directorB.Energy01),
                    $"Identical seed + tick sequence must produce identical outputs at tick {i}.");
            }
        }

        [Test]
        public void Output_StaysBoundedWithinUnitRange()
        {
            var director = new MacroCycleDirector();
            director.Seed(11);

            for (int i = 0; i < 60 * 180; i++)
            {
                director.Tick(true, Dt);
                Assert.That(director.Energy01, Is.InRange(-1f, 1f), $"Energy01 must stay within [-1, 1] (tick {i}).");
            }
        }

        [Test]
        public void PeriodRespected_NoTargetRedraw_BeforeTheEarliestPossibleRedrawTime()
        {
            var director = new MacroCycleDirector();
            director.Seed(101);

            float previous = 0f;
            int sign = 0;
            bool sawMovement = false;

            // The period is drawn once from random(120, 300)s, and the second target redraw
            // cannot fire before period * 0.8 >= 120 * 0.8 = 96s — regardless of the actual
            // seeded period. 90 simulated seconds is safely inside that window: a smooth
            // exponential slew toward a SINGLE fixed target never reverses direction, so any sign
            // flip in this window would indicate an (impossible, if the gate is respected)
            // early redraw.
            for (int i = 0; i < 60 * 90; i++)
            {
                director.Tick(true, Dt);
                float current = director.Energy01;
                float delta = current - previous;

                if (System.Math.Abs(delta) > 1e-6f)
                {
                    int deltaSign = delta > 0f ? 1 : -1;
                    if (sign == 0) sign = deltaSign;
                    else
                        Assert.AreEqual(sign, deltaSign,
                            $"A direction reversal at tick {i} (well before the earliest possible redraw at ~96s) " +
                            "would indicate an unexpected early target redraw.");
                    sawMovement = true;
                }

                previous = current;
            }

            Assert.IsTrue(sawMovement, "Sanity: the macro-cycle must produce some movement within 90 simulated seconds.");
        }

        [Test]
        public void Disabled_SlewsPublishedEnergyToZero()
        {
            var director = new MacroCycleDirector();
            director.Seed(202);

            // Warm up enabled long enough for the enable envelope to fully settle and the
            // underlying drift to move meaningfully away from zero.
            for (int i = 0; i < 60 * 60; i++)
                director.Tick(true, Dt);

            Assert.That(System.Math.Abs(director.Energy01), Is.GreaterThan(0.005f),
                "Sanity: after 60s enabled, Energy01 must be meaningfully away from zero.");

            // Disable — the enable envelope slews to zero over ~2s (mirrors
            // PosturalSwayDirector's own envelope pattern); the underlying drift itself keeps
            // advancing, but the PUBLISHED Energy01 must settle at (near) zero regardless.
            for (int i = 0; i < 60 * 5; i++)
                director.Tick(false, Dt);

            Assert.That(director.Energy01, Is.EqualTo(0f).Within(1e-3f),
                "Disabled must slew the published Energy01 to (near) zero within a few seconds.");
        }

        [Test]
        public void Reset_ReturnsToZero()
        {
            var director = new MacroCycleDirector();
            director.Seed(8);

            for (int i = 0; i < 60 * 30; i++)
                director.Tick(true, Dt);

            director.Reset();

            Assert.That(director.Energy01, Is.EqualTo(0f));
        }

        [Test]
        public void ZeroAllocation_SteadyStateTick()
        {
            var director = new MacroCycleDirector();
            director.Seed(9);

            for (int i = 0; i < 500; i++) director.Tick(true, Dt);

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 500; i++) director.Tick(true, Dt);
            });

            Assert.That(allocationCount, Is.EqualTo(0L), "MacroCycleDirector.Tick must produce zero managed allocation events in steady state.");
        }
    }
}
