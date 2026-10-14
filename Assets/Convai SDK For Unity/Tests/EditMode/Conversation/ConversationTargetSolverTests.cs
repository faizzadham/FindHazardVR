using System.Collections.Generic;
using Convai.Runtime.Conversation;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Conversation
{
    public sealed class ConversationTargetSolverTests
    {
        private const long Left = 1;
        private const long Right = 2;
        private const long Far = 3;

        private static ConversationTargetingOptions Options(
            float maxDistance = 10f,
            float maxAngle = 35f,
            float switchMargin = 10f,
            ConversationTargetingMode mode = ConversationTargetingMode.LookAt) =>
            new()
            {
                Mode = mode,
                MaxDistance = maxDistance,
                MaxAngle = maxAngle,
                SwitchMargin = switchMargin
            };

        private static long Solve(
            ConversationTargetingOptions options,
            Vector3 forward,
            params ConversationTargetCandidate[] candidates) =>
            ConversationTargetSolver.Solve(
                new ConversationTargetQuery(Vector3.zero, forward, options),
                new List<ConversationTargetCandidate>(candidates));

        /// <summary>A candidate at (x, 0, z) sits at the given angle off +Z, at the given distance.</summary>
        private static ConversationTargetCandidate At(long id, float angleDegrees, float distance, bool current = false)
        {
            float radians = angleDegrees * Mathf.Deg2Rad;
            return new ConversationTargetCandidate(
                id,
                new Vector3(Mathf.Sin(radians) * distance, 0f, Mathf.Cos(radians) * distance),
                current);
        }

        [Test]
        public void Solve_ChoosesTheCharacterNearestTheCentreOfView()
        {
            long chosen = Solve(Options(), Vector3.forward, At(Left, -20f, 5f), At(Right, 5f, 5f));

            Assert.That(chosen, Is.EqualTo(Right));
        }

        [Test]
        public void Solve_IgnoresCharactersOutsideTheAngle()
        {
            long chosen = Solve(Options(maxAngle: 20f), Vector3.forward, At(Left, 60f, 5f), At(Right, 10f, 5f));

            Assert.That(chosen, Is.EqualTo(Right));
        }

        [Test]
        public void Solve_IgnoresCharactersOutsideTheDistance()
        {
            long chosen = Solve(Options(maxDistance: 6f), Vector3.forward, At(Far, 0f, 20f), At(Right, 15f, 3f));

            Assert.That(chosen, Is.EqualTo(Right), "A character dead ahead but out of range must not win.");
        }

        [Test]
        public void Solve_KeepsTheCurrentTargetWhenNobodyQualifies()
        {
            // The player has turned away from everyone. Clearing the target here would leave them
            // speaking to nobody, which reads as a broken game.
            long chosen = Solve(Options(), Vector3.forward, At(Left, 170f, 5f, current: true), At(Right, 150f, 5f));

            Assert.That(chosen, Is.EqualTo(Left));
        }

        [Test]
        public void Solve_ReturnsNoTargetWhenNobodyQualifiesAndNobodyHoldsTheConversation()
        {
            long chosen = Solve(Options(), Vector3.forward, At(Left, 170f, 5f), At(Right, 150f, 5f));

            Assert.That(chosen, Is.EqualTo(ConversationTargetSolver.NoTarget));
        }

        [Test]
        public void Solve_HoldsTheCurrentTargetAgainstAMarginallyBetterChallenger()
        {
            // Five degrees better is inside the ten-degree margin: two characters standing close
            // together must not trade the conversation on small camera movement.
            long chosen = Solve(Options(switchMargin: 10f), Vector3.forward,
                At(Left, 12f, 5f, current: true), At(Right, 7f, 5f));

            Assert.That(chosen, Is.EqualTo(Left));
        }

        [Test]
        public void Solve_YieldsWhenAChallengerBeatsTheMargin()
        {
            long chosen = Solve(Options(switchMargin: 10f), Vector3.forward,
                At(Left, 30f, 5f, current: true), At(Right, 2f, 5f));

            Assert.That(chosen, Is.EqualTo(Right));
        }

        [Test]
        public void Solve_MarginIsWhatHoldsTheTarget_NotSomethingElse()
        {
            // The same geometry that holds above must switch when the margin is removed. Without this
            // the hold test would pass for a solver that never switches at all.
            long chosen = Solve(Options(switchMargin: 0f), Vector3.forward,
                At(Left, 12f, 5f, current: true), At(Right, 7f, 5f));

            Assert.That(chosen, Is.EqualTo(Right));
        }

        [Test]
        public void Solve_ProximityModeIgnoresWhereThePlayerIsLooking()
        {
            long chosen = Solve(Options(mode: ConversationTargetingMode.Proximity), Vector3.forward,
                At(Left, 175f, 2f), At(Right, 0f, 9f));

            Assert.That(chosen, Is.EqualTo(Left), "The nearest character wins even when behind the player.");
        }

        [Test]
        public void Solve_ReturnsNoTargetForAnEmptyRoom()
        {
            Assert.That(
                ConversationTargetSolver.Solve(
                    new ConversationTargetQuery(Vector3.zero, Vector3.forward, Options()),
                    new List<ConversationTargetCandidate>()),
                Is.EqualTo(ConversationTargetSolver.NoTarget));
        }
    }
}
