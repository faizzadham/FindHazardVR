using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.Emotion.Core;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Unit tests for <see cref="BrowCueEnvelope" />: idle-zero baseline, per-kind
    ///     attack/peak/full-decay, intensity scaling, retrigger-mid-decay re-attack, and a
    ///     non-positive intensity no-op.
    /// </summary>
    [TestFixture]
    public sealed class BrowCueEnvelopeTests
    {
        private const float Dt = 1f / 60f;

        private BrowCueEnvelope _envelope;

        [SetUp]
        public void SetUp() => _envelope = new BrowCueEnvelope();

        [Test]
        public void NeverTriggered_StaysExactlyZero()
        {
            for (int i = 0; i < 120; i++)
            {
                _envelope.Tick(Dt);
                Assert.AreEqual(0f, _envelope.OuterWeight, $"Tick {i}: never triggered must stay exactly 0.");
                Assert.AreEqual(0f, _envelope.InnerWeight, $"Tick {i}: never triggered must stay exactly 0.");
            }
        }

        [TestCase(BrowCueKind.SubtleRaise)]
        [TestCase(BrowCueKind.Flash)]
        [TestCase(BrowCueKind.SurpriseFlash)]
        public void Trigger_RisesThenFullyDecaysToZero(BrowCueKind kind)
        {
            _envelope.Trigger(kind, 1f);

            // Run long enough for any kind's attack to complete.
            for (int i = 0; i < 60; i++)
                _envelope.Tick(Dt);

            Assert.That(_envelope.OuterWeight, Is.GreaterThan(0.01f),
                $"{kind}: envelope must rise above zero after triggering.");

            // Run long enough for any kind's decay to fully settle.
            for (int i = 0; i < 300; i++)
                _envelope.Tick(Dt);

            Assert.AreEqual(0f, _envelope.OuterWeight, $"{kind}: envelope must fully decay back to exactly 0.");
            Assert.AreEqual(0f, _envelope.InnerWeight, $"{kind}: envelope must fully decay back to exactly 0.");
        }

        [Test]
        public void InnerWeight_IsAFainterCoactivation_OfOuterWeight()
        {
            _envelope.Trigger(BrowCueKind.Flash, 1f);
            for (int i = 0; i < 20; i++) _envelope.Tick(Dt);

            Assert.That(_envelope.OuterWeight, Is.GreaterThan(0f));
            Assert.That(_envelope.InnerWeight, Is.LessThan(_envelope.OuterWeight),
                "The inner-brow co-activation must be fainter than the outer-brow weight.");
            Assert.That(_envelope.InnerWeight, Is.GreaterThan(0f),
                "The inner-brow co-activation must still be non-zero while outer is active.");
        }

        [Test]
        public void Intensity_ScalesThePeak()
        {
            var full = new BrowCueEnvelope();
            var half = new BrowCueEnvelope();

            full.Trigger(BrowCueKind.SubtleRaise, 1f);
            half.Trigger(BrowCueKind.SubtleRaise, 0.5f);

            for (int i = 0; i < 60; i++)
            {
                full.Tick(Dt);
                half.Tick(Dt);
            }

            Assert.That(half.OuterWeight, Is.EqualTo(full.OuterWeight * 0.5f).Within(0.001f),
                "Peak amplitude must scale linearly with the triggered intensity.");
        }

        [Test]
        public void SurpriseFlash_PeaksHigherThan_Flash_WhichPeaksHigherThan_SubtleRaise()
        {
            var subtle = new BrowCueEnvelope();
            var flash = new BrowCueEnvelope();
            var surprise = new BrowCueEnvelope();

            subtle.Trigger(BrowCueKind.SubtleRaise, 1f);
            flash.Trigger(BrowCueKind.Flash, 1f);
            surprise.Trigger(BrowCueKind.SurpriseFlash, 1f);

            float subtlePeak = 0f, flashPeak = 0f, surprisePeak = 0f;
            for (int i = 0; i < 60; i++)
            {
                subtle.Tick(Dt);
                flash.Tick(Dt);
                surprise.Tick(Dt);
                subtlePeak = System.Math.Max(subtlePeak, subtle.OuterWeight);
                flashPeak = System.Math.Max(flashPeak, flash.OuterWeight);
                surprisePeak = System.Math.Max(surprisePeak, surprise.OuterWeight);
            }

            Assert.That(flashPeak, Is.GreaterThan(subtlePeak), "Flash must peak higher than SubtleRaise.");
            Assert.That(surprisePeak, Is.GreaterThan(flashPeak), "SurpriseFlash must peak higher than Flash.");
        }

        [Test]
        public void RetriggerMidDecay_ReAttacksFromCurrentValue()
        {
            _envelope.Trigger(BrowCueKind.Flash, 1f);
            for (int i = 0; i < 20; i++) _envelope.Tick(Dt); // through attack

            for (int i = 0; i < 10; i++) _envelope.Tick(Dt); // into decay
            float midDecay = _envelope.OuterWeight;

            for (int i = 0; i < 5; i++) _envelope.Tick(Dt);
            float furtherDecayed = _envelope.OuterWeight;
            Assert.That(furtherDecayed, Is.LessThan(midDecay), "Sanity: still decaying before the retrigger.");

            _envelope.Trigger(BrowCueKind.Flash, 1f);
            for (int i = 0; i < 5; i++) _envelope.Tick(Dt);

            Assert.That(_envelope.OuterWeight, Is.GreaterThan(furtherDecayed),
                "Retriggering mid-decay must re-attack from the current value, not keep decaying.");
        }

        [Test]
        public void NonPositiveIntensity_IsANoOp()
        {
            _envelope.Trigger(BrowCueKind.SurpriseFlash, 0f);
            _envelope.Trigger(BrowCueKind.Flash, -1f);

            for (int i = 0; i < 30; i++)
            {
                _envelope.Tick(Dt);
                Assert.AreEqual(0f, _envelope.OuterWeight, $"Tick {i}: a non-positive intensity trigger must be a total no-op.");
            }
        }
    }
}
