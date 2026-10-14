using Convai.Modules.BodyAnimation.Core.Locomotion;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Pure math coverage for <see cref="MotionDrive.NominalTurnYaw" /> and
    ///     <see cref="MotionDrive.NominalTurnYawDelta" /> — the fallback root-rotation drive
    ///     used when a turn-in-place clip carries no trustworthy analyzed yaw curve.
    /// </summary>
    /// <remarks>
    ///     Mirrors the private constants in <c>MotionDrive</c>: anticipation window 0.08
    ///     normalized time, minimum drive window 0.1 (so a driveEnd below anticipation+0.1 is
    ///     internally clamped to anticipation+0.1).
    /// </remarks>
    public class MotionDriveNominalTurnTests
    {
        private const float Anticipation = 0.08f;
        private const float MinDriveWindow = 0.1f;

        [Test]
        public void NominalTurnYaw_AnticipationAndDriveEndEndpoints()
        {
            const float authoredYaw = 90f;
            const float driveEnd = 0.85f;

            Assert.AreEqual(0f, MotionDrive.NominalTurnYaw(authoredYaw, 0f, driveEnd), 0.01f);
            Assert.AreEqual(0f, MotionDrive.NominalTurnYaw(authoredYaw, Anticipation, driveEnd), 0.01f);
            Assert.AreEqual(authoredYaw, MotionDrive.NominalTurnYaw(authoredYaw, driveEnd, driveEnd), 0.01f);
            Assert.AreEqual(authoredYaw, MotionDrive.NominalTurnYaw(authoredYaw, 1f, driveEnd), 0.01f);
        }

        [TestCase(90f)]
        [TestCase(-180f)]
        public void NominalTurnYaw_MonotonicMagnitude_AcrossFullRange(float authoredYaw)
        {
            const float driveEnd = 0.85f;
            const int samples = 50;

            float previousMagnitude = 0f;
            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                float magnitude = Mathf.Abs(MotionDrive.NominalTurnYaw(authoredYaw, t, driveEnd));

                Assert.GreaterOrEqual(magnitude, previousMagnitude - 0.001f,
                    $"yaw magnitude regressed at normalizedTime={t:F2}");
                previousMagnitude = magnitude;
            }
        }

        [Test]
        public void NominalTurnYawDelta_IntegratesToScaledAuthoredYaw()
        {
            const float authoredYaw = 90f;
            const float yawScale = 1.33f;
            const float driveEnd = 0.85f;
            const int steps = 100;

            float total = 0f;
            for (int i = 0; i < steps; i++)
            {
                float t0 = (float)i / steps;
                float t1 = (float)(i + 1) / steps;
                total += MotionDrive.NominalTurnYawDelta(authoredYaw, t0, t1, yawScale, driveEnd);
            }

            Assert.AreEqual(authoredYaw * yawScale, total, 0.5f);
        }

        [Test]
        public void NominalTurnYaw_DriveEndBelowMinimumWindow_ClampsWithoutNaN_AndReachesFullYaw()
        {
            const float authoredYaw = 90f;
            // Below Anticipation + MinDriveWindow (0.18) — internally clamped to that floor.
            const float tinyDriveEnd = 0.05f;

            for (float t = 0f; t <= 1f; t += 0.05f)
            {
                float yaw = MotionDrive.NominalTurnYaw(authoredYaw, t, tinyDriveEnd);
                Assert.IsFalse(float.IsNaN(yaw), $"NaN at normalizedTime={t:F2}");
            }

            Assert.AreEqual(authoredYaw, MotionDrive.NominalTurnYaw(authoredYaw, 1f, tinyDriveEnd), 0.01f);
            Assert.AreEqual(
                authoredYaw,
                MotionDrive.NominalTurnYaw(authoredYaw, Anticipation + MinDriveWindow, tinyDriveEnd),
                0.01f,
                "clamped drive end should complete the yaw by the effective window");
        }
    }
}
