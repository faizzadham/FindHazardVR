using Convai.Modules.BodyAnimation.Core.Locomotion;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Tests for <see cref="ExertionModel" />: rises under sustained jog/run pace,
    ///     barely registers at walk pace, decays at rest, stays clamped, and is deterministic.
    /// </summary>
    public sealed class ExertionModelTests
    {
        private const float Dt = 1f / 60f;
        private const float WalkSpeed = 1.2f;
        private const float JogSpeed = 2.6f;
        private const float RiseSeconds = 8f;
        private const float RecoverySeconds = 6f;

        [Test]
        public void SustainedFullRunEffort_RisesTowardOne()
        {
            var model = new ExertionModel();

            int ticks = (int)(RiseSeconds / Dt);
            for (int i = 0; i < ticks; i++)
                model.Tick(Dt, JogSpeed, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);

            Assert.That(model.Value01, Is.GreaterThan(0.9f),
                "Sustained full-run-pace effort for RiseSeconds must bring exertion close to 1.");
        }

        [Test]
        public void SlowWalk_BarelyRegisters()
        {
            var model = new ExertionModel();

            // At or below walk speed the effort input is exactly 0 by construction.
            for (int i = 0; i < 600; i++) // 10s
                model.Tick(Dt, WalkSpeed, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);

            Assert.That(model.Value01, Is.EqualTo(0f),
                "Walking at exactly walk pace must never register any exertion.");
        }

        [Test]
        public void SlightlyAboveWalkSpeed_StaysNearZeroOverAFewSeconds()
        {
            var model = new ExertionModel();
            float speed = WalkSpeed + 0.1f; // small effort input, well below jog pace

            for (int i = 0; i < 300; i++) // 5s
                model.Tick(Dt, speed, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);

            Assert.That(model.Value01, Is.LessThan(0.05f),
                "A small speed excess just above walk pace must barely register over a few seconds.");
        }

        [Test]
        public void RestAfterExertion_DecaysToZero()
        {
            var model = new ExertionModel();

            int riseTicks = (int)(RiseSeconds / Dt);
            for (int i = 0; i < riseTicks; i++)
                model.Tick(Dt, JogSpeed, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);
            float peak = model.Value01;
            Assert.That(peak, Is.GreaterThan(0.9f), "Precondition: exertion built up under sustained run pace.");

            int recoveryTicks = (int)(RecoverySeconds / Dt);
            for (int i = 0; i < recoveryTicks; i++)
                model.Tick(Dt, 0f, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);

            Assert.That(model.Value01, Is.LessThan(0.05f),
                "Stopping for RecoverySeconds must decay exertion back near 0.");
        }

        [Test]
        public void ExtremeInputs_StayClamped01()
        {
            var model = new ExertionModel();

            for (int i = 0; i < 600; i++)
                model.Tick(Dt, 50f, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);
            Assert.That(model.Value01, Is.LessThanOrEqualTo(1f));
            Assert.That(model.Value01, Is.GreaterThanOrEqualTo(0f));

            for (int i = 0; i < 600; i++)
                model.Tick(Dt, -10f, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);
            Assert.That(model.Value01, Is.EqualTo(0f));
        }

        [Test]
        public void Reset_ReturnsToZero()
        {
            var model = new ExertionModel();
            for (int i = 0; i < 300; i++)
                model.Tick(Dt, JogSpeed, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);
            Assert.That(model.Value01, Is.GreaterThan(0f));

            model.Reset();

            Assert.That(model.Value01, Is.EqualTo(0f));
        }

        [Test]
        public void SameInputSequence_ProducesIdenticalResults_Deterministic()
        {
            var a = new ExertionModel();
            var b = new ExertionModel();

            for (int i = 0; i < 400; i++)
            {
                float speed = (i % 3 == 0) ? JogSpeed : WalkSpeed + 0.3f;
                a.Tick(Dt, speed, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);
                b.Tick(Dt, speed, WalkSpeed, JogSpeed, RiseSeconds, RecoverySeconds);
            }

            Assert.That(a.Value01, Is.EqualTo(b.Value01),
                "Identical input sequences must produce bit-identical results.");
        }
    }
}
