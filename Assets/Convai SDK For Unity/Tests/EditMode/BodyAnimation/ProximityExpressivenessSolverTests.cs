using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="ProximityExpressivenessSolver" />: the pure
    ///     two-point distance-to-multiplier mapping used by proximity-scaled expressiveness.
    /// </summary>
    public sealed class ProximityExpressivenessSolverTests
    {
        private const float Near = 1.5f;
        private const float NearScale = 0.85f;
        private const float Far = 6f;
        private const float FarScale = 1.15f;
        private const float ClampMin = 0.8f;
        private const float ClampMax = 1.15f;

        [Test]
        public void AtOrBelowNearDistance_ReturnsNearScale()
        {
            Assert.That(
                ProximityExpressivenessSolver.ComputeTargetMultiplier(Near, Near, NearScale, Far, FarScale, ClampMin, ClampMax),
                Is.EqualTo(NearScale).Within(0.0001f));

            Assert.That(
                ProximityExpressivenessSolver.ComputeTargetMultiplier(0f, Near, NearScale, Far, FarScale, ClampMin, ClampMax),
                Is.EqualTo(NearScale).Within(0.0001f));
        }

        [Test]
        public void AtOrBeyondFarDistance_ReturnsFarScale()
        {
            Assert.That(
                ProximityExpressivenessSolver.ComputeTargetMultiplier(Far, Near, NearScale, Far, FarScale, ClampMin, ClampMax),
                Is.EqualTo(FarScale).Within(0.0001f));

            Assert.That(
                ProximityExpressivenessSolver.ComputeTargetMultiplier(50f, Near, NearScale, Far, FarScale, ClampMin, ClampMax),
                Is.EqualTo(FarScale).Within(0.0001f));
        }

        [Test]
        public void MidDistance_LinearlyInterpolates()
        {
            float mid = (Near + Far) / 2f;
            float result = ProximityExpressivenessSolver.ComputeTargetMultiplier(
                mid, Near, NearScale, Far, FarScale, ClampMin, ClampMax);

            float expected = (NearScale + FarScale) / 2f;
            Assert.That(result, Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void ConfiguredScalesOutsideClampRange_AreClamped()
        {
            // Near/far scales configured outside the safe range must still be clamped by the
            // solver itself — a defensive net independent of the config's own OnValidate clamp.
            float atNear = ProximityExpressivenessSolver.ComputeTargetMultiplier(
                Near, Near, 0.2f, Far, 3f, ClampMin, ClampMax);
            float atFar = ProximityExpressivenessSolver.ComputeTargetMultiplier(
                Far, Near, 0.2f, Far, 3f, ClampMin, ClampMax);

            Assert.That(atNear, Is.EqualTo(ClampMin).Within(0.0001f));
            Assert.That(atFar, Is.EqualTo(ClampMax).Within(0.0001f));
        }

        [Test]
        public void DegenerateFarAtOrInsideNear_FallsBackToFarScaleClamped()
        {
            float result = ProximityExpressivenessSolver.ComputeTargetMultiplier(
                3f, 5f, NearScale, 4f, FarScale, ClampMin, ClampMax);

            Assert.That(result, Is.EqualTo(FarScale).Within(0.0001f));
        }
    }
}
