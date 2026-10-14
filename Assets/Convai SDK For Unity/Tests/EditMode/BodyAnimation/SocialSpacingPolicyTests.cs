using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="SocialSpacingPolicy" />: sustained-proximity
    ///     triggering gated by dialogue state/busy, the hysteresis latch, the rolling per-minute
    ///     budget, and the target-point math (including the degenerate direction fallback).
    /// </summary>
    public sealed class SocialSpacingPolicyTests
    {
        private const float ComfortRadius = 0.7f;
        private const float ComfortHoldSeconds = 0.6f;
        private const float Dt = 1f / 30f;

        [Test]
        public void SustainedProximity_InEligibleState_Triggers()
        {
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var characterPos = Vector3.zero;
            var conversantPos = new Vector3(0.3f, 0f, 0f); // well inside the bubble

            bool fired = TickUntilFired(policy, characterPos, conversantPos, DialogueState.Idle, false, out _);

            Assert.IsTrue(fired, "sustained proximity in an eligible state must eventually trigger");
        }

        [Test]
        public void BriefBrushPast_NeverTriggers()
        {
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var characterPos = Vector3.zero;
            var conversantPos = new Vector3(0.3f, 0f, 0f);

            // Under the hold time — a brush-past.
            float elapsed = 0f;
            bool fired = false;
            while (elapsed < ComfortHoldSeconds - Dt)
            {
                fired |= policy.Tick(
                    Vector3.Distance(characterPos, conversantPos), characterPos, conversantPos,
                    DialogueState.Idle, false, Dt, out _);
                elapsed += Dt;
            }

            Assert.IsFalse(fired, "a hold under ComfortHoldSeconds must never trigger");
        }

        [TestCase(DialogueState.Speaking)]
        [TestCase(DialogueState.Thinking)]
        [TestCase(DialogueState.Reacting)]
        [TestCase(DialogueState.Interrupted)]
        [TestCase(DialogueState.Settling)]
        public void IneligibleDialogueState_NeverTriggers(DialogueState state)
        {
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var characterPos = Vector3.zero;
            var conversantPos = new Vector3(0.3f, 0f, 0f);

            bool fired = TickUntilFired(policy, characterPos, conversantPos, state, false, out _, maxTicks: 300);

            Assert.IsFalse(fired, $"{state} must never trigger a social step");
        }

        [Test]
        public void Busy_NeverTriggers()
        {
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var characterPos = Vector3.zero;
            var conversantPos = new Vector3(0.3f, 0f, 0f);

            bool fired = TickUntilFired(
                policy, characterPos, conversantPos, DialogueState.Idle, isBusy: true, out _, maxTicks: 300);

            Assert.IsFalse(fired, "a busy character (action/locomotion/PlayActionAt) must never step");
        }

        [Test]
        public void OutsideComfortRadius_NeverTriggers()
        {
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var characterPos = Vector3.zero;
            var conversantPos = new Vector3(5f, 0f, 0f); // well outside the bubble

            bool fired = TickUntilFired(policy, characterPos, conversantPos, DialogueState.Idle, false, out _, maxTicks: 300);

            Assert.IsFalse(fired);
        }

        [Test]
        public void Hysteresis_ConversantChasing_DoesNotRetriggerUntilMarginCleared()
        {
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, maxRepositionsPerMinute: 10);
            var characterPos = Vector3.zero;
            var conversantPos = new Vector3(0.3f, 0f, 0f);

            Assert.IsTrue(
                TickUntilFired(policy, characterPos, conversantPos, DialogueState.Idle, false, out _),
                "precondition: first trigger must fire");

            // Conversant "chases" — stays just inside the (still-crowding) radius without ever
            // exceeding ComfortRadius + margin. Sustained again for well beyond the hold time.
            bool retriggered = false;
            for (int i = 0; i < 300; i++)
            {
                retriggered |= policy.Tick(
                    Vector3.Distance(characterPos, conversantPos), characterPos, conversantPos,
                    DialogueState.Idle, false, Dt, out _);
            }

            Assert.IsFalse(retriggered, "a conversant that never backs off past the margin must not retrigger");

            // Now the conversant genuinely backs off past ComfortRadius + margin, then closes in
            // again and holds — hysteresis is cleared, so this must be allowed to fire.
            var farPos = new Vector3(5f, 0f, 0f);
            for (int i = 0; i < 5; i++)
                policy.Tick(Vector3.Distance(characterPos, farPos), characterPos, farPos, DialogueState.Idle, false, Dt, out _);

            bool refired = TickUntilFired(policy, characterPos, conversantPos, DialogueState.Idle, false, out _);
            Assert.IsTrue(refired, "once the conversant has backed off past the margin, a new sustained approach must trigger");
        }

        [Test]
        public void RollingBudget_CapsRepositionsPerMinute()
        {
            const int budget = 2;
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, budget);
            var characterPos = Vector3.zero;
            var conversantPos = new Vector3(0.3f, 0f, 0f);
            var farPos = new Vector3(5f, 0f, 0f);

            int fireCount = 0;
            for (int round = 0; round < budget + 2; round++)
            {
                if (TickUntilFired(policy, characterPos, conversantPos, DialogueState.Idle, false, out _, maxTicks: 60))
                    fireCount++;

                // Back off past the margin (clears hysteresis) well within the same budget minute.
                for (int i = 0; i < 5; i++)
                    policy.Tick(Vector3.Distance(characterPos, farPos), characterPos, farPos, DialogueState.Idle, false, Dt, out _);
            }

            Assert.That(fireCount, Is.EqualTo(budget),
                "the rolling per-minute budget must cap the number of repositions regardless of hysteresis clearing");
        }

        [Test]
        public void TargetPoint_IsAwayFromConversantAtComfortRadiusPlusMargin()
        {
            var policy = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var characterPos = new Vector3(2f, 0f, 0f);
            var conversantPos = new Vector3(2.3f, 0f, 0f); // 0.3m away, inside the bubble, along +X

            Assert.IsTrue(TickUntilFired(policy, characterPos, conversantPos, DialogueState.Idle, false, out Vector3 target));

            Vector3 expectedDirection = (characterPos - conversantPos).normalized;
            Vector3 toTarget = target - conversantPos;

            Assert.That(toTarget.magnitude, Is.EqualTo(ComfortRadius + 0.3f).Within(0.001f),
                "the target must sit exactly ComfortRadius + margin away from the conversant");
            Assert.That(Vector3.Dot(toTarget.normalized, expectedDirection), Is.EqualTo(1f).Within(0.001f),
                "the target must lie directly away from the conversant, through the character");
            Assert.That(target.y, Is.EqualTo(characterPos.y).Within(0.0001f),
                "the target must keep the character's own height");
        }

        [Test]
        public void TargetPoint_DegenerateSamePosition_PicksDeterministicDirection()
        {
            var policyA = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var policyB = new SocialSpacingPolicy(ComfortRadius, ComfortHoldSeconds, 3);
            var samePos = new Vector3(1f, 0.5f, -2f);

            Assert.IsTrue(TickUntilFired(policyA, samePos, samePos, DialogueState.Idle, false, out Vector3 targetA));
            Assert.IsTrue(TickUntilFired(policyB, samePos, samePos, DialogueState.Idle, false, out Vector3 targetB));

            Assert.That(targetA, Is.EqualTo(targetB),
                "the degenerate (coincident-position) case must pick the same direction deterministically");
            Assert.That(Vector3.Distance(targetA, samePos), Is.EqualTo(ComfortRadius + 0.3f).Within(0.001f));
        }

        // ------------------------------------------------------------------ helpers

        private static bool TickUntilFired(
            SocialSpacingPolicy policy,
            Vector3 characterPos,
            Vector3 conversantPos,
            DialogueState state,
            bool isBusy,
            out Vector3 target,
            int maxTicks = 200)
        {
            target = default;
            for (int i = 0; i < maxTicks; i++)
            {
                float distance = Vector3.Distance(characterPos, conversantPos);
                if (policy.Tick(distance, characterPos, conversantPos, state, isBusy, Dt, out target))
                    return true;
            }
            return false;
        }
    }
}
