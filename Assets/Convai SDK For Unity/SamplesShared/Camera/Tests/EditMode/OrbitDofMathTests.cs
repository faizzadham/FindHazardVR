// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using Convai.Sample.Camera;
using NUnit.Framework;

namespace Convai.Tests.SamplesShared.Camera.EditMode
{
    public class OrbitDofMathTests
    {
        [Test]
        public void Evaluate_WithMidRangeDistance_ReturnsBlendedAperture()
        {
            // Arrange
            float cameraDistance = 1.64f; // Midpoint between 0.78 and 2.5
            float closeDistance = 0.78f;
            float farDistance = 2.5f;
            float closeAperture = 1.4f;
            float farAperture = 8.0f;

            // Act
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                closeDistance,
                farDistance,
                closeAperture,
                farAperture);

            // Assert
            // Should be between close and far aperture (smoothstep affects exact value)
            Assert.Greater(result.Aperture, closeAperture);
            Assert.Less(result.Aperture, farAperture);
        }

        [Test]
        public void Evaluate_WithCloseDistance_ReturnsCloseAperture()
        {
            // Arrange
            float cameraDistance = 0.5f; // Below close threshold
            float closeDistance = 0.78f;
            float farDistance = 2.5f;
            float closeAperture = 1.4f;
            float farAperture = 8.0f;

            // Act
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                closeDistance,
                farDistance,
                closeAperture,
                farAperture);

            // Assert
            Assert.AreEqual(closeAperture, result.Aperture, 0.01f);
        }

        [Test]
        public void Evaluate_WithFarDistance_ReturnsFarAperture()
        {
            // Arrange
            float cameraDistance = 3.0f; // Above far threshold
            float closeDistance = 0.78f;
            float farDistance = 2.5f;
            float closeAperture = 1.4f;
            float farAperture = 8.0f;

            // Act
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                closeDistance,
                farDistance,
                closeAperture,
                farAperture);

            // Assert
            Assert.AreEqual(farAperture, result.Aperture, 0.01f);
        }

        [Test]
        public void Evaluate_FocusDistanceMatchesCameraDistance()
        {
            // Arrange
            float cameraDistance = 1.5f;
            float closeDistance = 0.78f;
            float farDistance = 2.5f;
            float closeAperture = 1.4f;
            float farAperture = 8.0f;

            // Act
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                closeDistance,
                farDistance,
                closeAperture,
                farAperture);

            // Assert
            Assert.AreEqual(cameraDistance, result.FocusDistance, 0.01f);
        }

        [Test]
        public void Evaluate_WithZeroDistance_ReturnsMinimumFocusDistance()
        {
            // Arrange - edge case
            float cameraDistance = 0f;
            float closeDistance = 0.78f;
            float farDistance = 2.5f;
            float closeAperture = 1.4f;
            float farAperture = 8.0f;

            // Act
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                closeDistance,
                farDistance,
                closeAperture,
                farAperture);

            // Assert
            Assert.Greater(result.FocusDistance, 0f, "Focus distance sanitized to positive");
            Assert.AreEqual(0.1f, result.FocusDistance, 0.01f);
        }

        [Test]
        public void Evaluate_WithNegativeDistance_ClampsToPositive()
        {
            // Arrange - edge case
            float cameraDistance = -1f;
            float closeDistance = 0.78f;
            float farDistance = 2.5f;
            float closeAperture = 1.4f;
            float farAperture = 8.0f;

            // Act
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                closeDistance,
                farDistance,
                closeAperture,
                farAperture);

            // Assert
            Assert.Greater(result.FocusDistance, 0f);
            Assert.Greater(result.Aperture, 0f);
        }

        [Test]
        public void Evaluate_AtCloseThreshold_ReturnsCloseApertureExactly()
        {
            // The endpoint is a contract, not an approximation: a camera sitting exactly on the close
            // threshold must get exactly the authored close aperture.
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(0.78f, 0.78f, 2.5f, 1.4f, 8.0f);

            Assert.AreEqual(1.4f, result.Aperture, 0.0001f);
        }

        [Test]
        public void Evaluate_AtFarThreshold_ReturnsFarApertureExactly()
        {
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(2.5f, 0.78f, 2.5f, 1.4f, 8.0f);

            Assert.AreEqual(8.0f, result.Aperture, 0.0001f);
        }

        [Test]
        public void Evaluate_WithNarrowCloseAperture_HonorsAuthoredDirection()
        {
            // The shipped default authors the close end as the NARROWER f-stop (face sharp up close).
            // Evaluate must follow whichever direction the caller authored - it owns no look opinion
            // of its own. This is the regression guard for the endpoints ever being swapped internally.
            const float closeAperture = 8.0f;
            const float farAperture = 2.8f;

            OrbitDofMath.AdaptiveDofResult atClose = OrbitDofMath.Evaluate(0.5f, 0.5f, 2.5f, closeAperture, farAperture);
            OrbitDofMath.AdaptiveDofResult atFar = OrbitDofMath.Evaluate(2.5f, 0.5f, 2.5f, closeAperture, farAperture);

            Assert.AreEqual(closeAperture, atClose.Aperture, 0.0001f, "Close end must use the close aperture");
            Assert.AreEqual(farAperture, atFar.Aperture, 0.0001f, "Far end must use the far aperture");
        }

        [Test]
        public void Evaluate_AcrossRange_MovesMonotonicallyBetweenEndpoints()
        {
            // Zooming must never reverse direction mid-travel, or the DOF visibly pumps while the user drags.
            const float closeDistance = 0.78f;
            const float farDistance = 2.5f;
            float previous = float.NegativeInfinity;

            for (int step = 0; step <= 20; step++)
            {
                float distance = closeDistance + (farDistance - closeDistance) * (step / 20f);
                float aperture = OrbitDofMath.Evaluate(distance, closeDistance, farDistance, 1.4f, 8.0f).Aperture;

                Assert.GreaterOrEqual(aperture, previous, $"Aperture reversed at distance {distance}");
                previous = aperture;
            }
        }

        [Test]
        public void Evaluate_WithFarThresholdBelowClose_StaysFinite()
        {
            // Mis-authored thresholds must degrade to a usable value rather than divide by zero.
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(1.0f, 2.5f, 0.5f, 1.4f, 8.0f);

            Assert.That(result.Aperture, Is.GreaterThan(0f).And.LessThan(100f));
            Assert.Greater(result.FocusDistance, 0f);
        }

        [Test]
        public void Evaluate_ApertureNeverBelowMinimum()
        {
            // Arrange - test with very low aperture values
            float cameraDistance = 1.0f;
            float closeDistance = 0.78f;
            float farDistance = 2.5f;
            float closeAperture = 0.1f; // Below physical minimum
            float farAperture = 0.5f;

            // Act
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                closeDistance,
                farDistance,
                closeAperture,
                farAperture);

            // Assert
            Assert.GreaterOrEqual(result.Aperture, 0.7f, "Aperture clamped to minimum f/0.7");
        }
    }
}
