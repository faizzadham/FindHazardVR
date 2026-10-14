using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyLanguage.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    public sealed class MotionRegionArbitratorTests
    {
        [Test]
        public void FullBody_RemovesEveryProceduralRegion()
        {
            MotionRegionWeights weights = MotionRegionArbitrator.Resolve(
                GestureSuppression.FullBody, true, 1f, 0.75f);

            Assert.That(weights.Master, Is.Zero);
            Assert.That(weights.Posture, Is.Zero);
            Assert.That(weights.Breath, Is.Zero);
            Assert.That(weights.Arms, Is.Zero);
            Assert.That(weights.HandMicro, Is.Zero);
        }

        [Test]
        public void UpperBody_RetainsBreathAndAuthoredPostureFloor_ButRemovesArms()
        {
            MotionRegionWeights weights = MotionRegionArbitrator.Resolve(
                GestureSuppression.UpperBody, false, 0f, 0.7f);

            Assert.That(weights.Master, Is.EqualTo(1f));
            Assert.That(weights.Posture, Is.EqualTo(0.7f).Within(1e-5f));
            Assert.That(weights.Breath, Is.EqualTo(1f));
            Assert.That(weights.Arms, Is.Zero);
        }

        [Test]
        public void ContinuousOccupancy_DucksPostureAndHands_AndProtectsBusyArms()
        {
            MotionRegionWeights weights = MotionRegionArbitrator.Resolve(
                GestureSuppression.None, true, 0.6f, 0.75f);

            Assert.That(weights.Posture, Is.EqualTo(0.85f).Within(1e-5f));
            Assert.That(weights.HandMicro, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(weights.Arms, Is.Zero);
            Assert.That(weights.Breath, Is.EqualTo(1f));
        }
    }
}
