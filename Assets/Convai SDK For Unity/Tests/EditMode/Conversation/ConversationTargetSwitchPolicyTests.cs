using Convai.Runtime.Conversation;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Conversation
{
    public sealed class ConversationTargetSwitchPolicyTests
    {
        private const long Active = 1;
        private const long Challenger = 2;
        private const long Other = 3;
        private const float Delay = 0.2f;

        /// <summary>
        ///     Times either side of the delay, kept clear of it on purpose.
        /// </summary>
        /// <remarks>
        ///     An earlier version asserted at the boundary itself and failed: <c>0.35f - 0.15f</c> is
        ///     <c>0.199999988</c>, which is under a delay of <c>0.2f</c>. That measured float
        ///     representation rather than the rule, and no caller depends on the exact instant — the
        ///     clock arrives in frame-sized steps, so the boundary is never landed on in practice.
        /// </remarks>
        private const float WithinDelay = Delay * 0.5f;
        private const float PastDelay = Delay * 2f;

        private ConversationTargetSwitchPolicy _policy;

        [SetUp]
        public void SetUp() => _policy = new ConversationTargetSwitchPolicy();

        private ConversationTargetSwitchVerdict Evaluate(
            long proposed,
            float now,
            bool playerMidUtterance = false,
            bool commandInFlight = false) =>
            _policy.Evaluate(proposed, Active, now, Delay, playerMidUtterance, commandInFlight);

        [Test]
        public void Evaluate_DoesNotCommitOnTheFirstFrameAProposalAppears()
        {
            Assert.That(Evaluate(Challenger, 0f), Is.EqualTo(ConversationTargetSwitchVerdict.HeldForDelay));
        }

        [Test]
        public void Evaluate_CommitsOnceTheProposalHasHeldForTheDelay()
        {
            Evaluate(Challenger, 0f);

            Assert.That(Evaluate(Challenger, PastDelay), Is.EqualTo(ConversationTargetSwitchVerdict.Commit));
        }

        [Test]
        public void Evaluate_RestartsTheDelayWhenTheProposalChanges()
        {
            // Sweeping the view across a room proposes each character in turn. None of them may
            // inherit the time the previous one accumulated.
            const float switchedAt = 0.15f;
            Evaluate(Challenger, 0f);
            Evaluate(Other, switchedAt);

            Assert.That(
                Evaluate(Other, switchedAt + WithinDelay),
                Is.EqualTo(ConversationTargetSwitchVerdict.HeldForDelay),
                "The second proposal must start its own delay, not inherit the first one's.");
            Assert.That(
                Evaluate(Other, switchedAt + PastDelay),
                Is.EqualTo(ConversationTargetSwitchVerdict.Commit));
        }

        [Test]
        public void Evaluate_NeverMovesTheTargetWhileThePlayerIsSpeaking()
        {
            Assert.That(
                Evaluate(Challenger, 0f, playerMidUtterance: true),
                Is.EqualTo(ConversationTargetSwitchVerdict.HeldForPlayerSpeech));
            Assert.That(
                Evaluate(Challenger, 10f, playerMidUtterance: true),
                Is.EqualTo(ConversationTargetSwitchVerdict.HeldForPlayerSpeech),
                "No amount of waiting may release a switch while the player is still talking.");
        }

        [Test]
        public void Evaluate_CountsTheLookMadeWhileThePlayerWasStillSpeaking()
        {
            // The documented rule: a glance made mid-sentence is remembered rather than discarded,
            // and applies once the sentence ends. Restarting the delay at the full stop made the
            // player hold the same look a second time, for a reason nothing in the game shows them.
            Evaluate(Challenger, 0f, playerMidUtterance: true);
            Evaluate(Challenger, PastDelay, playerMidUtterance: true);

            Assert.That(
                Evaluate(Challenger, PastDelay),
                Is.EqualTo(ConversationTargetSwitchVerdict.Commit),
                "A look held right through the sentence has already served its delay.");
        }

        [Test]
        public void Evaluate_StillWaitsWhenTheSentenceOutlastsTheLook()
        {
            // The other half of the same rule. Time accumulated by a proposal is not a licence for
            // the next one: a player who glanced away and back must still hold the new look.
            const float spokeUntil = 0.1f;
            Evaluate(Challenger, 0f, playerMidUtterance: true);

            Assert.That(
                Evaluate(Challenger, spokeUntil),
                Is.EqualTo(ConversationTargetSwitchVerdict.HeldForDelay),
                "A look shorter than the delay has not earned the switch yet.");
            Assert.That(
                Evaluate(Challenger, spokeUntil + PastDelay),
                Is.EqualTo(ConversationTargetSwitchVerdict.Commit));
        }

        [Test]
        public void Evaluate_ForgetsAGlanceThePlayerAbandonedDuringTheirSentence()
        {
            // Remembering the look must not become committing to a look the player has left. The
            // moment the view settles back on whoever holds the conversation, the proposal is gone.
            Evaluate(Challenger, 0f, playerMidUtterance: true);
            Evaluate(Active, PastDelay, playerMidUtterance: true);

            Assert.That(_policy.PendingId, Is.EqualTo(ConversationTargetSolver.NoTarget));
            Assert.That(
                Evaluate(Challenger, PastDelay),
                Is.EqualTo(ConversationTargetSwitchVerdict.HeldForDelay),
                "An abandoned glance may not commit on the strength of time it no longer holds.");
        }

        [Test]
        public void Evaluate_WaitsForAnInFlightCommand()
        {
            Assert.That(
                Evaluate(Challenger, 0f, commandInFlight: true),
                Is.EqualTo(ConversationTargetSwitchVerdict.HeldForPendingCommand));
        }

        [Test]
        public void Evaluate_CountsTheLookMadeWhileACommandWasInFlight()
        {
            // A round trip to the service is not the player's fault either, and it lands in the
            // same tenths of a second the delay is measured in.
            Evaluate(Challenger, 0f, commandInFlight: true);
            Evaluate(Challenger, PastDelay, commandInFlight: true);

            Assert.That(
                Evaluate(Challenger, PastDelay),
                Is.EqualTo(ConversationTargetSwitchVerdict.Commit));
        }

        [Test]
        public void Evaluate_ReportsTheActiveCharacterAsNothingToDo()
        {
            Assert.That(Evaluate(Active, 0f), Is.EqualTo(ConversationTargetSwitchVerdict.AlreadyActive));
        }

        [Test]
        public void Evaluate_ForgetsAProposalOnceTheTargetSettlesBack()
        {
            Evaluate(Challenger, 0f);
            Evaluate(Active, 0.1f);

            Assert.That(_policy.PendingId, Is.EqualTo(ConversationTargetSolver.NoTarget));
            Assert.That(
                Evaluate(Challenger, 0.15f),
                Is.EqualTo(ConversationTargetSwitchVerdict.HeldForDelay),
                "The abandoned proposal must start its delay again, not resume it.");
        }
    }
}
