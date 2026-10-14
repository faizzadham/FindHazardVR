using Convai.Modules.BodyLanguage.Core.Policy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     POCO coverage for <see cref="CameraDistanceLod" /> ( Feature D): anchor
    ///     exactness at the authored breakpoints, monotonicity across the whole domain, and the
    ///     slew-rate constant the controller applies on top of this pure mapping.
    /// </summary>
    public sealed class CameraDistanceLodTests
    {
        [TestCase(0f, 0.7f)]
        [TestCase(0.5f, 0.7f)]
        [TestCase(1f, 0.7f)]
        [TestCase(2.5f, 1.0f)]
        [TestCase(4f, 1.0f)]
        [TestCase(6f, 1.0f)]
        [TestCase(12f, 1.3f)]
        [TestCase(50f, 1.3f)]
        public void ScaleForDistance_AnchorsExactly(float distanceMeters, float expected) =>
            Assert.That(CameraDistanceLod.ScaleForDistance(distanceMeters), Is.EqualTo(expected).Within(1e-5f));

        [Test]
        public void ScaleForDistance_MidpointBetweenNearAndNeutral_IsHalfway()
        {
            float mid = (CameraDistanceLod.NearDistanceMeters + CameraDistanceLod.NeutralNearDistanceMeters) * 0.5f;
            float expected = (CameraDistanceLod.NearScale + CameraDistanceLod.NeutralScale) * 0.5f;

            Assert.That(CameraDistanceLod.ScaleForDistance(mid), Is.EqualTo(expected).Within(1e-4f));
        }

        [Test]
        public void ScaleForDistance_MidpointBetweenNeutralAndFar_IsHalfway()
        {
            float mid = (CameraDistanceLod.NeutralFarDistanceMeters + CameraDistanceLod.FarDistanceMeters) * 0.5f;
            float expected = (CameraDistanceLod.NeutralScale + CameraDistanceLod.FarScale) * 0.5f;

            Assert.That(CameraDistanceLod.ScaleForDistance(mid), Is.EqualTo(expected).Within(1e-4f));
        }

        [Test]
        public void ScaleForDistance_IsMonotoneNonDecreasing_AcrossTheDomain()
        {
            float previous = CameraDistanceLod.ScaleForDistance(0f);
            for (float d = 0f; d <= 20f; d += 0.1f)
            {
                float current = CameraDistanceLod.ScaleForDistance(d);
                Assert.That(current, Is.GreaterThanOrEqualTo(previous - 1e-5f),
                    $"Scale must never decrease as distance increases (d={d:F1}).");
                previous = current;
            }
        }

        [Test]
        public void ScaleForDistance_NegativeDistance_ClampsToNearScale() =>
            Assert.That(CameraDistanceLod.ScaleForDistance(-5f), Is.EqualTo(CameraDistanceLod.NearScale));

        [Test]
        public void MaxScaleChangePerSecond_MatchesTheAuthoredSlewRate() =>
            Assert.That(CameraDistanceLod.MaxScaleChangePerSecond, Is.EqualTo(0.5f),
                "The  spec pins the slew rate at 0.5 scale-units/second — a change here must be deliberate.");

        [Test]
        public void Slew_FromNearToFar_TakesAtLeastOneSecond_AtTheAuthoredRate()
        {
            // Mathf.MoveTowards over the full 0.7 -> 1.3 range at 0.5/s takes 1.2s — the
            // controller's own application of this constant (verified statically; the
            // MoveTowards call itself lives in the controller, not this POCO).
            float current = CameraDistanceLod.NearScale;
            const float target = CameraDistanceLod.FarScale;
            const float dt = 1f / 60f;
            int ticks = 0;
            while (!Mathf.Approximately(current, target) && ticks < 600)
            {
                current = Mathf.MoveTowards(current, target, CameraDistanceLod.MaxScaleChangePerSecond * dt);
                ticks++;
            }

            float elapsedSeconds = ticks * dt;
            Assert.That(elapsedSeconds, Is.GreaterThan(1.0f),
                "A camera cut from near to far must not pop the amplitude scale within a single second.");
        }
    }
}
