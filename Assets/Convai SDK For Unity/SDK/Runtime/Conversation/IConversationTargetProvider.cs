using System.Collections.Generic;
using Convai.Runtime.Components;

namespace Convai.Runtime.Conversation
{
    /// <summary>
    ///     Replaces the SDK's rule for deciding which character the player is addressing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shipped modes cover the common shapes — the character being looked at, the nearest
    ///         character, or nothing but explicit calls. A game whose rule is none of those (the
    ///         character a UI list has selected, the one a quest is about, the one standing in a
    ///         trigger volume) implements this instead of switching to
    ///         <see cref="ConversationTargetingMode.Manual" /> and rebuilding the parts that were
    ///         already right.
    ///     </para>
    ///     <para>
    ///         A provider only chooses. Everything that keeps the choice pleasant — never handing over
    ///         a turn in progress, never committing on the first frame, never leaving the player with
    ///         nobody listening — still applies on top of what it returns.
    ///     </para>
    /// </remarks>
    public interface IConversationTargetProvider
    {
        /// <summary>
        ///     Returns the character the player should be addressing.
        /// </summary>
        /// <param name="candidates">
        ///     Every character in the current room, in stable order. Never null, never empty when this
        ///     is called.
        /// </param>
        /// <param name="current">
        ///     The character currently holding the conversation, or <c>null</c> when none does.
        /// </param>
        /// <returns>
        ///     The character to address, or <c>null</c> to leave the conversation where it is. Returning
        ///     a character not in <paramref name="candidates" /> also leaves it where it is.
        /// </returns>
        ConvaiCharacter ResolveTarget(IReadOnlyList<ConvaiCharacter> candidates, ConvaiCharacter current);
    }
}
