using System.Collections.Generic;
using Convai.Runtime.Behaviors;

namespace Convai.Runtime.Room
{
    /// <summary>
    ///     Why a character appearing or disappearing was, or was not, applied to the live room.
    /// </summary>
    /// <remarks>
    ///     A live roster edit that quietly does nothing and a reconnect that quietly does nothing look
    ///     identical from the outside — the character is simply not in the conversation. Naming the
    ///     reason separates "declined, and the reconnect path owns this" from "nothing changed".
    /// </remarks>
    internal enum LiveRosterVerdict
    {
        /// <summary>The roster differs and the difference can be applied without reconnecting.</summary>
        Apply,

        /// <summary>There is no live room to edit.</summary>
        NotConnected,

        /// <summary>The room exists but has not finished starting, so its roster is not settled yet.</summary>
        SessionNotReady,

        /// <summary>Ownership could not be captured, so there is nothing to compare against.</summary>
        OwnershipUnavailable,

        /// <summary>The player changed, which decides how the room was created rather than who is in it.</summary>
        PlayerChanged,

        /// <summary>
        ///     The project assigned a different Initial Character, which is also a creation-time
        ///     decision.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Only an <i>assigned</i> Initial Character counts. A room with none derives its
        ///         starting character from scene order, and that answer moves whenever the roster
        ///         moves — so a character leaving changes it, and a character rejoining changes it
        ///         back. Treating the derived answer as a decision made the first roster edit
        ///         permanently poison every edit after it: once the room's original starting
        ///         character had left, the room's own record and the derived answer could never
        ///         agree again, so a character disabled could not leave and a character re-enabled
        ///         could not rejoin.
        ///     </para>
        ///     <para>
        ///         An assigned Initial Character that is disabled does not resolve either, so it
        ///         cannot trigger this by leaving: the composition falls through to the derived
        ///         answer, which does not count.
        ///     </para>
        /// </remarks>
        StartingCharacterChanged,

        /// <summary>The roster is already what it should be.</summary>
        NoRosterChange,

        /// <summary>Applying the change would leave the room with nobody in it.</summary>
        WouldEmptyTheRoom
    }

    /// <summary>
    ///     Decides whether a change in the scene's characters can be applied to a room that is already
    ///     connected, and which characters join and leave if so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Kept as a plain decision over two lists so it can be tested without a room, a session or
    ///         a scene. The caller owns every Unity-side fact — whether the transport is connected,
    ///         whether the player moved — and passes it in as a plain answer.
    ///     </para>
    ///     <para>
    ///         The scope is deliberately narrow. Only a roster difference is applied live; a changed
    ///         player or a changed starting character still means a reconnect, because those decide how
    ///         the room was created rather than who is in it.
    ///     </para>
    /// </remarks>
    internal static class LiveRosterPlanner
    {
        /// <summary>
        ///     Plans a live roster change. <paramref name="added" /> receives the characters to add, and
        ///     <paramref name="removedIndices" /> the positions in <paramref name="current" /> to remove,
        ///     so the caller can map them back to whatever it holds alongside each member.
        /// </summary>
        /// <remarks>
        ///     Both buffers are cleared first and are only written when the verdict is
        ///     <see cref="LiveRosterVerdict.Apply" />, so a declined plan cannot leave a caller holding a
        ///     half-built list it then acts on.
        /// </remarks>
        /// <param name="roomStartingCharacter">The character the room was opened for.</param>
        /// <param name="desiredStartingCharacter">
        ///     The starting character the current composition resolves to.
        /// </param>
        /// <param name="desiredStartingCharacterIsExplicit">
        ///     Whether that is the project's assigned Initial Character rather than the derived
        ///     first-in-scene-order fallback. Only the assigned one is a decision worth reconnecting
        ///     for.
        /// </param>
        /// <param name="desired">Characters that could join the room now: owned, selected, active.</param>
        /// <param name="retained">
        ///     Characters the room should keep a seat for: owned and selected, active or not. A
        ///     member is removed only when it is in neither list, so a character that was merely
        ///     disabled keeps its seat and needs no round trip to get it back.
        /// </param>
        internal static LiveRosterVerdict Plan(
            bool connected,
            bool sessionReady,
            bool ownershipAvailable,
            bool playerUnchanged,
            IConvaiCharacterAgent roomStartingCharacter,
            IConvaiCharacterAgent desiredStartingCharacter,
            bool desiredStartingCharacterIsExplicit,
            IReadOnlyList<IConvaiCharacterAgent> desired,
            IReadOnlyList<IConvaiCharacterAgent> retained,
            IReadOnlyList<IConvaiCharacterAgent> current,
            List<IConvaiCharacterAgent> added,
            List<int> removedIndices)
        {
            added?.Clear();
            removedIndices?.Clear();

            if (!connected) return LiveRosterVerdict.NotConnected;
            if (!sessionReady) return LiveRosterVerdict.SessionNotReady;
            if (!ownershipAvailable || desired == null || current == null)
                return LiveRosterVerdict.OwnershipUnavailable;
            if (!playerUnchanged) return LiveRosterVerdict.PlayerChanged;

            // Only an assigned Initial Character is a creation-time decision. The derived fallback
            // moves with the roster itself, so guarding on it refuses the very edit that moved it.
            if (desiredStartingCharacterIsExplicit &&
                !ReferenceEquals(roomStartingCharacter, desiredStartingCharacter))
                return LiveRosterVerdict.StartingCharacterChanged;

            int addedCount = 0;
            for (int i = 0; i < desired.Count; i++)
            {
                IConvaiCharacterAgent character = desired[i];
                if (character == null || Contains(current, character)) continue;
                added?.Add(character);
                addedCount++;
            }

            int removedCount = 0;
            int presentMembers = 0;
            for (int i = 0; i < current.Count; i++)
            {
                IConvaiCharacterAgent member = current[i];
                if (member == null) continue;

                presentMembers++;

                // Kept whenever the project still wants this character in the room, even if it is
                // not active this instant. Removing on inactivity turned hiding a character into
                // leaving the conversation, and coming back into a fresh membership.
                if (Contains(retained ?? desired, member)) continue;

                removedIndices?.Add(i);
                removedCount++;
            }

            if (addedCount == 0 && removedCount == 0)
            {
                added?.Clear();
                removedIndices?.Clear();
                return LiveRosterVerdict.NoRosterChange;
            }

            // Never empty the room from here. A roster with nobody left is a composition change, not a
            // roster edit, and the reconnect path is the one that knows how to refuse it.
            if (addedCount == 0 && removedCount >= presentMembers)
            {
                added?.Clear();
                removedIndices?.Clear();
                return LiveRosterVerdict.WouldEmptyTheRoom;
            }

            return LiveRosterVerdict.Apply;
        }

        private static bool Contains(
            IReadOnlyList<IConvaiCharacterAgent> characters,
            IConvaiCharacterAgent candidate)
        {
            for (int i = 0; i < characters.Count; i++)
                if (ReferenceEquals(characters[i], candidate))
                    return true;

            return false;
        }
    }
}
