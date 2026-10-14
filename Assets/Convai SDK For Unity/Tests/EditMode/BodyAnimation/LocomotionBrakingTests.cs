using Convai.Modules.BodyAnimation.Core.Locomotion;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Pure coverage for <see cref="LocomotionBraking" /> — the arithmetic a graceful stop
    ///     follows: how much room the run-out needs, and where along the current path that lands.
    ///     No agent, no NavMesh, no scene.
    /// </summary>
    /// <remarks>
    ///     These guard the fix for a stop that read as a slide: a cancelled move ended the
    ///     animation instantly while the agent coasted on under its own acceleration, so the
    ///     character glided across the floor with its feet standing still. The whole remedy is
    ///     stopping by arriving at a braking point instead, which makes "where is that point"
    ///     load-bearing.
    /// </remarks>
    public class LocomotionBrakingTests
    {
        // ------------------------------------------------------------------ braking distance

        [Test]
        public void PhysicalDistance_IsTheKinematicStoppingDistance()
        {
            // v²/2a: 3 m/s shed at 4.5 m/s² needs 1 m.
            Assert.That(
                LocomotionBraking.PhysicalDistance(3f, 4.5f, 0f),
                Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void PhysicalDistance_GrowsWithTheSquareOfSpeed()
        {
            float slow = LocomotionBraking.PhysicalDistance(1.2f, 4f, 0f);
            float fast = LocomotionBraking.PhysicalDistance(2.4f, 4f, 0f);

            Assert.That(fast, Is.EqualTo(slow * 4f).Within(1e-4f),
                "Doubling the speed must quadruple the run-out, or a jog brakes like a walk.");
        }

        [Test]
        public void PhysicalDistance_NeverFallsBelowTheMinimum()
        {
            // A character barely moving still needs a step to land on rather than stopping
            // between footfalls.
            Assert.That(
                LocomotionBraking.PhysicalDistance(0.05f, 4f, 0.35f),
                Is.EqualTo(0.35f).Within(1e-4f));
        }

        [Test]
        public void PhysicalDistance_SurvivesAZeroAcceleration()
        {
            // Divide-by-zero would hand back Infinity, and a braking point at infinity is a
            // destination the agent can never reach — the stop would never end.
            float distance = LocomotionBraking.PhysicalDistance(2.6f, 0f, 0.35f);

            Assert.That(float.IsFinite(distance), Is.True);
            Assert.That(distance, Is.GreaterThan(0f));
        }

        // ------------------------------------------------------------------ point along path

        [Test]
        public void TryPointAlongPath_LandsPartWayAlongAStraightLeg()
        {
            var corners = new[] { Vector3.zero, new Vector3(0f, 0f, 10f) };

            bool inside = LocomotionBraking.TryPointAlongPath(corners, 2, 4f, out Vector3 point);

            Assert.That(inside, Is.True);
            AssertPoint(point, new Vector3(0f, 0f, 4f));
        }

        [Test]
        public void TryPointAlongPath_FollowsTheCornerRatherThanTheHeading()
        {
            // 3 m north then east. Braking 5 m out has to turn the corner: a run-out measured
            // along the character's forward would brake 5 m north — through the wall the path
            // just went round.
            var corners = new[]
            {
                Vector3.zero,
                new Vector3(0f, 0f, 3f),
                new Vector3(6f, 0f, 3f)
            };

            bool inside = LocomotionBraking.TryPointAlongPath(corners, 3, 5f, out Vector3 point);

            Assert.That(inside, Is.True);
            AssertPoint(point, new Vector3(2f, 0f, 3f));
        }

        [Test]
        public void TryPointAlongPath_ReportsTheEndWhenThePathIsShorterThanTheRunOut()
        {
            // Already inside the stopping envelope: the destination the character has IS the
            // braking point, and moving it would pull the destination backwards — which the stop
            // machinery reads as an aborted stop and answers with a lurch.
            var corners = new[] { Vector3.zero, new Vector3(0f, 0f, 1.5f) };

            bool inside = LocomotionBraking.TryPointAlongPath(corners, 2, 4f, out Vector3 point);

            Assert.That(inside, Is.False);
            AssertPoint(point, new Vector3(0f, 0f, 1.5f));
        }

        [Test]
        public void TryPointAlongPath_RefusesAPathWithNothingToWalk()
        {
            Assert.That(
                LocomotionBraking.TryPointAlongPath(new[] { Vector3.zero }, 1, 2f, out _),
                Is.False);
            Assert.That(
                LocomotionBraking.TryPointAlongPath(null, 0, 2f, out _),
                Is.False);
        }

        [Test]
        public void TryPointAlongPath_ReadsOnlyTheCornersInUse()
        {
            // The component reuses a fixed buffer across stops, so stale corners sit past the
            // live count. Reading them would brake to wherever the character last walked.
            var buffer = new Vector3[8];
            buffer[0] = Vector3.zero;
            buffer[1] = new Vector3(0f, 0f, 2f);
            buffer[2] = new Vector3(500f, 0f, 500f); // stale

            bool inside = LocomotionBraking.TryPointAlongPath(buffer, 2, 5f, out Vector3 point);

            Assert.That(inside, Is.False);
            AssertPoint(point, new Vector3(0f, 0f, 2f));
        }

        [Test]
        public void TryPointAlongPath_HandlesDuplicateCorners()
        {
            // Degenerate legs appear in real paths (a corner sampled twice); a zero-length leg
            // must not divide by zero or stall the walk.
            var corners = new[]
            {
                Vector3.zero,
                Vector3.zero,
                new Vector3(0f, 0f, 6f)
            };

            bool inside = LocomotionBraking.TryPointAlongPath(corners, 3, 2f, out Vector3 point);

            Assert.That(inside, Is.True);
            AssertPoint(point, new Vector3(0f, 0f, 2f));
        }

        /// <summary>Asserts two points match, without depending on the test framework's comparer utilities.</summary>
        private static void AssertPoint(Vector3 actual, Vector3 expected) =>
            Assert.That((actual - expected).magnitude, Is.LessThan(1e-4f),
                $"expected {expected}, got {actual}");
    }
}
