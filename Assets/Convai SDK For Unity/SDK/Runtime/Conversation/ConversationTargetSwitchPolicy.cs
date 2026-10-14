namespace Convai.Runtime.Conversation
{
    /// <summary>Why a proposed change of conversation target was allowed or held back.</summary>
    public enum ConversationTargetSwitchVerdict
    {
        /// <summary>The proposal matches the character already holding the conversation.</summary>
        AlreadyActive = 0,

        /// <summary>The change is allowed and should be sent.</summary>
        Commit,

        /// <summary>Held: the proposal has not been the best choice for long enough yet.</summary>
        HeldForDelay,

        /// <summary>Held: the player is part-way through saying something.</summary>
        HeldForPlayerSpeech,

        /// <summary>Held: a previous change is still in flight.</summary>
        HeldForPendingCommand
    }

    /// <summary>
    ///     Decides whether a proposed conversation target may take over yet. Separate from
    ///     <see cref="ConversationTargetSolver" /> because the two answer different questions — the
    ///     solver answers <i>who</i>, this answers <i>whether now</i> — and both are easier to trust
    ///     when they can be tested apart.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The player's own sentence is the only thing that pins the target.</b> A character
    ///         answering does not: the service permits one speaker at a time and ends the previous
    ///         character's turn on every target change, so waiting for an answer to finish would delay
    ///         a switch without preserving anything.
    ///     </para>
    ///     <para>
    ///         <b>Nothing is committed on the first frame it looks best.</b> Sweeping the view across
    ///         a room passes over every character in it. Without a delay each one would be addressed
    ///         in turn, and each would cost a round trip to the service.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationTargetSwitchPolicy
    {
        private long _pendingId = ConversationTargetSolver.NoTarget;
        private float _pendingSinceSeconds;

        /// <summary>The candidate currently accumulating time toward a switch.</summary>
        public long PendingId => _pendingId;

        /// <summary>Forgets any proposal in progress.</summary>
        public void Reset()
        {
            _pendingId = ConversationTargetSolver.NoTarget;
            _pendingSinceSeconds = 0f;
        }

        /// <summary>Judges a proposed target against the current state of the conversation.</summary>
        /// <param name="proposedId">Who the solver chose.</param>
        /// <param name="activeId">Who currently holds the conversation.</param>
        /// <param name="nowSeconds">A monotonically rising clock, in seconds.</param>
        /// <param name="switchDelaySeconds">How long a proposal must persist before it commits.</param>
        /// <param name="playerMidUtterance">Whether the player is part-way through saying something.</param>
        /// <param name="commandInFlight">Whether a previous change has not been acknowledged yet.</param>
        public ConversationTargetSwitchVerdict Evaluate(
            long proposedId,
            long activeId,
            float nowSeconds,
            float switchDelaySeconds,
            bool playerMidUtterance,
            bool commandInFlight)
        {
            if (proposedId == ConversationTargetSolver.NoTarget || proposedId == activeId)
            {
                Reset();
                return ConversationTargetSwitchVerdict.AlreadyActive;
            }

            // Recorded before the holds, not after them. A proposal that is held is still a
            // proposal: a player who looks at somebody while finishing their own sentence has
            // spent that time addressing them, and restarting the delay at the full stop makes
            // them hold the look a second time for no reason they can see. The proposal still has
            // to survive the next evaluation to commit — looking back at whoever holds the
            // conversation resets this on the following frame — so nothing is committed on the
            // strength of a glance the player has already abandoned.
            if (proposedId != _pendingId)
            {
                _pendingId = proposedId;
                _pendingSinceSeconds = nowSeconds;
            }

            if (playerMidUtterance)
                return ConversationTargetSwitchVerdict.HeldForPlayerSpeech;

            if (commandInFlight)
                return ConversationTargetSwitchVerdict.HeldForPendingCommand;

            if (nowSeconds - _pendingSinceSeconds < switchDelaySeconds)
                return ConversationTargetSwitchVerdict.HeldForDelay;

            Reset();
            return ConversationTargetSwitchVerdict.Commit;
        }
    }
}
