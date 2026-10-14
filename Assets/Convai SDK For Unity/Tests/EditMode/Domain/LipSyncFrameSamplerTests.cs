using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    [TestFixture]
    public class LipSyncFrameSamplerTests
    {
        [TestCase(0f, 0.4f)]
        [TestCase(1f, 0.7f)]
        public void EvaluateCatmullRom_WithBoundaryAlpha_ReturnsSegmentEndpoints(float alpha, float expected)
        {
            // Arrange
            float[] p0 = { 0.1f };
            float[] p1 = { 0.4f };
            float[] p2 = { 0.7f };
            float[] p3 = { 0.9f };
            float[] output = new float[1];

            // Act
            FrameSampler.EvaluateCatmullRom(p0, p1, p2, p3, alpha, output, 1);

            // Assert
            Assert.AreEqual(expected, output[0], 0.0001f);
        }

        [Test]
        public void EvaluateCatmullRom_WithLinearRampMidpoint_ReturnsExpectedInterpolatedValue()
        {
            // Arrange
            float[] p0 = { 0f };
            float[] p1 = { 0.25f };
            float[] p2 = { 0.75f };
            float[] p3 = { 1f };
            float[] output = new float[1];

            // Act
            FrameSampler.EvaluateCatmullRom(p0, p1, p2, p3, 0.5f, output, 1);

            // Assert
            Assert.AreEqual(0.5f, output[0], 0.01f);
        }

        [Test]
        public void EvaluateCatmullRom_WithPotentialOvershoot_ClampsOutputToNormalizedRange()
        {
            // Arrange
            float[] p0 = { 0.9f };
            float[] p1 = { 1f };
            float[] p2 = { 0f };
            float[] p3 = { 0.1f };
            float[] output = new float[1];

            // Act
            FrameSampler.EvaluateCatmullRom(p0, p1, p2, p3, 0.5f, output, 1);

            // Assert
            Assert.That(output[0], Is.InRange(0f, 1f));
        }

        [Test]
        public void EvaluateCatmullRom_WithChannelCountLargerThanInputs_UsesSafeMinimumLength()
        {
            // Arrange
            float[] p0 = { 0f, 1f };
            float[] p1 = { 0.25f, 0.5f };
            float[] p2 = { 0.75f };
            float[] p3 = { 1f, 0.2f };
            float[] output = { -1f, -1f };

            // Act
            FrameSampler.EvaluateCatmullRom(p0, p1, p2, p3, 0.5f, output, 2);

            // Assert
            Assert.AreEqual(-1f, output[1], 0.0001f);
        }

        [Test]
        public void ApplyTemporalSmoothing_WithNonPositiveFactor_LeavesCurrentValuesUnchanged()
        {
            // Arrange
            float[] target = { 1f };
            float[] current = { 0.2f };

            // Act
            FrameSampler.ApplyTemporalSmoothing(target, current, -0.1f, 1f / 60f, 1);

            // Assert
            Assert.AreEqual(0.2f, current[0], 0.0001f);
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        public void ApplyTemporalSmoothing_AfterOneSecondConvergesToStableValueAcrossFrameRates(int fps)
        {
            // Arrange
            float value = SimulateForOneSecond(fps);

            // Act
            float baseline = SimulateForOneSecond(60);

            // Assert
            Assert.AreEqual(baseline, value, 0.002f);
        }

        [Test]
        public void ApplyTemporalSmoothing_WithLargeDeltaTime_UsesClampAndAvoidsOvershoot()
        {
            // Arrange
            float[] target = { 1f };
            float[] current = { 0f };

            // Act
            FrameSampler.ApplyTemporalSmoothing(target, current, 0.3f, 10f, 1);

            // Assert
            Assert.That(current[0], Is.InRange(0f, 1f));
        }

        [TestCase(0f, 0d)]
        [TestCase(0.5f, 1d / 60d)]
        [TestCase(0.95f, 0.033d)]
        public void GetTemporalSmoothingCompensationSeconds_ReturnsBoundedGroupDelay(
            float smoothingFactor, double expected)
        {
            double actual = FrameSampler.GetTemporalSmoothingCompensationSeconds(smoothingFactor);

            Assert.AreEqual(expected, actual, 0.0001d);
        }

        private static float SimulateForOneSecond(int fps)
        {
            float[] current = { 0f };
            float[] target = { 1f };
            float dt = 1f / fps;
            for (int i = 0; i < fps; i++) FrameSampler.ApplyTemporalSmoothing(target, current, 0.3f, dt, 1);

            return current[0];
        }
    }
}
