using Convai.Domain.Embodiment.Semantics;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>
    ///     Which gaze candidates the arbiter is allowed to see while the conversation has already
    ///     decided who this character is looking at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The conversation director produces a policy overlay, not a target: it says "attend
    ///         to C" and the arbiter then picks somebody on its own relevance. That division is
    ///         deliberate — every ramp, filter and interest rule lives in the arbiter — but it
    ///         leaves one gap. Every other character in the room is still on the table, and the
    ///         arbiter scores them without knowing the conversation happened, so a silent
    ///         bystander who happens to be nearer, or whose interest budget has just recovered,
    ///         can win the frame. The look then lands on somebody nobody decided on, which reads
    ///         as the character glancing away from the speaker for no reason.
    ///     </para>
    ///     <para>
    ///         So while a decision is live the room is narrowed to the one person it names: the
    ///         attended character, or nobody at all when the look is on the player, where the
    ///         player anchor is the whole of the answer. Candidates that are not people — props,
    ///         world objects, the path ahead, scripted looks — are untouched; they lose to the
    ///         conversation on priority already, and taking them away would put the character in
    ///         an empty room the moment anybody spoke.
    ///     </para>
    ///     <para>
    ///         <b>A missing candidate is not a substitution.</b> When the attended character
    ///         cannot be built this tick — the registry has not caught up, the head anchor is
    ///         mid-rebind — no character candidate is offered at all, and the arbiter's own
    ///         target-loss hold carries the look for a moment longer. Offering the room instead
    ///         would replace one absent decision with a completely different one.
    ///     </para>
    /// </remarks>
    internal static class ConversationCandidateFilter
    {
        /// <summary>
        ///     Whether one candidate may be offered to the arbiter this tick.
        /// </summary>
        /// <param name="kind">What kind of candidate it is. Only characters are ever withheld.</param>
        /// <param name="key">
        ///     Room key of the character the candidate is for (its
        ///     <c>ConvaiCharacterGazeRegistry.Entry.Key</c>), or 0 for anything else.
        /// </param>
        /// <param name="attention">This tick's conversation decision.</param>
        internal static bool ShouldOffer(GazeTargetKind kind, int key, in ConversationGazeState attention)
        {
            // Everything that is not another character is offered exactly as before.
            if (kind != GazeTargetKind.Character) return true;

            // No decision: the room is open and the arbiter arbitrates, which is the whole of how
            // a character behaves when nobody is talking to it.
            if (!attention.Active) return true;

            switch (attention.Focus)
            {
                // The player is the look. A bystander offered alongside them is either redundant
                // (the anchor outranks it) or a substitution waiting for the anchor to blink.
                case ConversationGazeFocus.Player:
                    return false;

                // Exactly the one the conversation named — including the person an idle glance, a
                // speaker's audience check or an interruption reflex named, because those are
                // decisions too, and each of them is about one person.
                case ConversationGazeFocus.Character:
                    return key != 0 && key == attention.CharacterKey;

                default:
                    return true;
            }
        }
    }
}
