using Convai.Modules.Emotion.Components;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Pure tests for <see cref="ConvaiEmotionController.ComputeProsodyGainTarget" />:
    ///     the not-speaking/coupling-off invariant (always exactly 1), the full-coupling
    ///     [0.85, 1.15] range, a half-coupling midpoint, and NaN/negative energy sanitization.
    /// </summary>
    [TestFixture]
    public sealed class ProsodyGainTargetTests
    {
        [Test]
        public void NotSpeaking_AlwaysReturnsOne()
        {
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: false, energy: 1f, coupling: 1f);

            Assert.That(gain, Is.EqualTo(1f));
        }

        [Test]
        public void CouplingZero_AlwaysReturnsOne()
        {
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: true, energy: 1f, coupling: 0f);

            Assert.That(gain, Is.EqualTo(1f));
        }

        [Test]
        public void Speaking_FullCoupling_ZeroEnergy_ReturnsLowerBound()
        {
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: true, energy: 0f, coupling: 1f);

            Assert.That(gain, Is.EqualTo(0.85f).Within(0.0001f));
        }

        [Test]
        public void Speaking_FullCoupling_FullEnergy_ReturnsUpperBound()
        {
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: true, energy: 1f, coupling: 1f);

            Assert.That(gain, Is.EqualTo(1.15f).Within(0.0001f));
        }

        [Test]
        public void Speaking_HalfCoupling_FullEnergy_ReturnsMidpoint()
        {
            // Lerp(1, 1.15, 0.5) = 1.075.
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: true, energy: 1f, coupling: 0.5f);

            Assert.That(gain, Is.EqualTo(1.075f).Within(0.0001f));
        }

        [Test]
        public void NegativeEnergy_ClampedToZero_ReturnsLowerBound()
        {
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: true, energy: -5f, coupling: 1f);

            Assert.That(gain, Is.EqualTo(0.85f).Within(0.0001f));
        }

        [Test]
        public void EnergyAboveOne_ClampedToOne_ReturnsUpperBound()
        {
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: true, energy: 5f, coupling: 1f);

            Assert.That(gain, Is.EqualTo(1.15f).Within(0.0001f));
        }

        [Test]
        public void CouplingAboveOne_ClampedToOne()
        {
            float gain = ConvaiEmotionController.ComputeProsodyGainTarget(
                speaking: true, energy: 0f, coupling: 5f);

            Assert.That(gain, Is.EqualTo(0.85f).Within(0.0001f));
        }
    }
}
