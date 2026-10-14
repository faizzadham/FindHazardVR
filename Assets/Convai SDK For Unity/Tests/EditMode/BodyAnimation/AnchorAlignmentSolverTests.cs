using Convai.Modules.BodyAnimation.Core.Locomotion;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Pure math coverage for <see cref="AnchorAlignmentSolver" /> — approach point
    ///     computation, target facing per <see cref="ActionFacingMode" />, the alignment
    ///     envelope check, and the smoothstep lerp. No scene, no locomotion.
    /// </summary>
    public class AnchorAlignmentSolverTests
    {
        // ------------------------------------------------------------------ approach point

        [Test]
        public void ComputeApproachPoint_OffsetsInAnchorLocalSpace()
        {
            var anchor = new AnchorPose(new Vector3(10f, 0f, 5f), 90f); // facing +X
            Vector3 approach = AnchorAlignmentSolver.ComputeApproachPoint(anchor, new Vector3(0f, 0f, 0.5f));

            // Local +Z (0.5m "in front") rotated by +90° yaw lands on world +X.
            Assert.AreEqual(10.5f, approach.x, 1e-4f);
            Assert.AreEqual(5f, approach.z, 1e-4f);
        }

        [Test]
        public void ComputeApproachPoint_ZeroYaw_KeepsOffsetAxisAligned()
        {
            var anchor = new AnchorPose(Vector3.zero, 0f);
            Vector3 approach = AnchorAlignmentSolver.ComputeApproachPoint(anchor, new Vector3(0f, 0f, 0.5f));

            Assert.AreEqual(new Vector3(0f, 0f, 0.5f), approach);
        }

        // ------------------------------------------------------------------ target yaw

        [Test]
        public void ComputeTargetYaw_AnchorForward_ReturnsAnchorYaw()
        {
            var anchor = new AnchorPose(Vector3.zero, 123f);
            float yaw = AnchorAlignmentSolver.ComputeTargetYaw(
                anchor, new Vector3(0f, 0f, 1f), ActionFacingMode.AnchorForward, 0f);

            Assert.AreEqual(123f, yaw, 1e-4f);
        }

        [Test]
        public void ComputeTargetYaw_FaceAnchor_PointsBackAtAnchor()
        {
            // Character standing 1m in front of the anchor (+Z) should face -Z (180°) to look
            // back at it.
            var anchor = new AnchorPose(new Vector3(0f, 0f, 0f), 0f);
            float yaw = AnchorAlignmentSolver.ComputeTargetYaw(
                anchor, new Vector3(0f, 0f, 1f), ActionFacingMode.FaceAnchor, 0f);

            Assert.AreEqual(180f, Mathf.Abs(Mathf.DeltaAngle(0f, yaw)), 1e-3f);
        }

        [Test]
        public void ComputeTargetYaw_None_ReturnsCurrentYawUnchanged()
        {
            var anchor = new AnchorPose(Vector3.zero, 90f);
            float yaw = AnchorAlignmentSolver.ComputeTargetYaw(
                anchor, new Vector3(1f, 0f, 0f), ActionFacingMode.None, 37f);

            Assert.AreEqual(37f, yaw, 1e-4f);
        }

        // ------------------------------------------------------------------ envelope

        [Test]
        public void IsWithinEnvelope_AcceptsWithinDistanceAndYaw()
        {
            bool within = AnchorAlignmentSolver.IsWithinEnvelope(
                currentPosition: new Vector3(0.1f, 0f, 0.1f),
                currentYaw: 10f,
                targetPosition: Vector3.zero,
                targetYaw: 0f,
                facingMode: ActionFacingMode.AnchorForward,
                maxDistance: 0.4f,
                maxYawDegrees: 45f);

            Assert.IsTrue(within);
        }

        [Test]
        public void IsWithinEnvelope_RejectsBeyondDistance()
        {
            bool within = AnchorAlignmentSolver.IsWithinEnvelope(
                currentPosition: new Vector3(1f, 0f, 0f),
                currentYaw: 0f,
                targetPosition: Vector3.zero,
                targetYaw: 0f,
                facingMode: ActionFacingMode.AnchorForward,
                maxDistance: 0.4f,
                maxYawDegrees: 45f);

            Assert.IsFalse(within);
        }

        [Test]
        public void IsWithinEnvelope_RejectsBeyondYaw()
        {
            bool within = AnchorAlignmentSolver.IsWithinEnvelope(
                currentPosition: Vector3.zero,
                currentYaw: 100f,
                targetPosition: Vector3.zero,
                targetYaw: 0f,
                facingMode: ActionFacingMode.AnchorForward,
                maxDistance: 0.4f,
                maxYawDegrees: 45f);

            Assert.IsFalse(within);
        }

        [Test]
        public void IsWithinEnvelope_IsPlanar_IgnoresYDifference()
        {
            // A large Y gap (e.g. a tabletop-height anchor) must never fail the envelope on
            // its own — only XZ distance and yaw matter.
            bool within = AnchorAlignmentSolver.IsWithinEnvelope(
                currentPosition: new Vector3(0f, 0f, 0f),
                currentYaw: 0f,
                targetPosition: new Vector3(0.05f, 5f, 0.05f),
                targetYaw: 0f,
                facingMode: ActionFacingMode.AnchorForward,
                maxDistance: 0.4f,
                maxYawDegrees: 45f);

            Assert.IsTrue(within);
        }

        [Test]
        public void IsWithinEnvelope_FacingNone_SkipsYawCheck()
        {
            bool within = AnchorAlignmentSolver.IsWithinEnvelope(
                currentPosition: Vector3.zero,
                currentYaw: 179f,
                targetPosition: Vector3.zero,
                targetYaw: 0f,
                facingMode: ActionFacingMode.None,
                maxDistance: 0.4f,
                maxYawDegrees: 45f);

            Assert.IsTrue(within);
        }

        // ------------------------------------------------------------------ lerp

        [Test]
        public void Smoothstep01_IsMonotonicAndBounded()
        {
            float previous = -1f;
            for (int i = 0; i <= 20; i++)
            {
                float t = i / 20f;
                float s = AnchorAlignmentSolver.Smoothstep01(t);

                Assert.GreaterOrEqual(s, 0f);
                Assert.LessOrEqual(s, 1f);
                Assert.GreaterOrEqual(s, previous);
                previous = s;
            }
        }

        [Test]
        public void Smoothstep01_ClampsOutOfRangeInput()
        {
            Assert.AreEqual(0f, AnchorAlignmentSolver.Smoothstep01(-0.5f));
            Assert.AreEqual(1f, AnchorAlignmentSolver.Smoothstep01(1.5f));
        }

        [Test]
        public void LerpPosition_XZEndpointsMatch_AtTAndOne()
        {
            // Y matches on both ends here — isolates the XZ endpoint behavior from the Y policy.
            Vector3 from = new(1f, 2f, 3f);
            Vector3 to = new(4f, 2f, 6f);

            Assert.AreEqual(from, AnchorAlignmentSolver.LerpPosition(from, to, 0f));
            Assert.AreEqual(to, AnchorAlignmentSolver.LerpPosition(from, to, 1f));
        }

        [Test]
        public void LerpPosition_KeepsFromY_IgnoresToY_AtEveryT()
        {
            // An anchor authored at seat/prop height must never sink or lift the root — only
            // XZ is lerped, Y always passes through from "from" untouched.
            Vector3 from = new(0f, 1.5f, 0f);
            Vector3 to = new(2f, 9f, 2f); // wildly different Y (e.g. a tabletop anchor)

            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f;
                Vector3 result = AnchorAlignmentSolver.LerpPosition(from, to, t);
                Assert.AreEqual(1.5f, result.y, 1e-5f, $"t={t}");
            }
        }

        [Test]
        public void LerpYaw_EndpointsMatchExactly()
        {
            Assert.AreEqual(10f, AnchorAlignmentSolver.LerpYaw(10f, 170f, 0f), 1e-4f);
            Assert.AreEqual(170f, AnchorAlignmentSolver.LerpYaw(10f, 170f, 1f), 1e-4f);
        }

        [Test]
        public void LerpYaw_TakesShortestPathAcrossWrap()
        {
            // From 350° to 10° is a 20° step across the wrap, not 340° the long way.
            float mid = AnchorAlignmentSolver.LerpYaw(350f, 10f, 0.5f);
            float delta = Mathf.DeltaAngle(350f, mid);

            Assert.Less(Mathf.Abs(delta), 15f);
        }
    }
}
