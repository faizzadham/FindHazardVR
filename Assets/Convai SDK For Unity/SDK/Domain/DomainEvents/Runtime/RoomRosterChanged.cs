using System;

namespace Convai.Domain.DomainEvents.Runtime
{
    /// <summary>What happened to one character's place in a connected room.</summary>
    public enum RoomRosterChange
    {
        /// <summary>The character joined the room without a reconnect.</summary>
        Joined = 0,

        /// <summary>The character gave up its place without a reconnect.</summary>
        Left,

        /// <summary>The edit was refused. The room carries on with the roster it has.</summary>
        Refused
    }

    /// <summary>
    ///     Domain event raised when a connected room's roster is edited during play.
    /// </summary>
    /// <remarks>
    ///     Until this existed, a roster edit reported itself only to the Console, so a game could not
    ///     react to a character joining or leaving — announce it, open a nameplate, retarget a
    ///     camera — without polling the room. A refused edit is reported too, because a character
    ///     that silently never joins is indistinguishable from a scene fault.
    /// </remarks>
    public readonly struct RoomRosterChanged
    {
        /// <summary>
        ///     Creates the event with an explicit timestamp. Prefer <see cref="Create" />, which
        ///     stamps the current time.
        /// </summary>
        public RoomRosterChanged(
            RoomRosterChange change,
            string membershipId,
            string characterId,
            string characterName,
            int rosterSize,
            string reason,
            DateTime timestamp)
        {
            Change = change;
            MembershipId = membershipId ?? string.Empty;
            CharacterId = characterId ?? string.Empty;
            CharacterName = characterName ?? string.Empty;
            RosterSize = rosterSize;
            Reason = reason ?? string.Empty;
            Timestamp = timestamp;
        }

        /// <summary>What happened.</summary>
        public RoomRosterChange Change { get; }

        /// <summary>The room membership affected. Empty for a join that never got one.</summary>
        public string MembershipId { get; }

        /// <summary>Convai Character ID of the character this is about.</summary>
        public string CharacterId { get; }

        /// <summary>Display name of that character, for logs and UI.</summary>
        public string CharacterName { get; }

        /// <summary>How many characters the room holds after this change.</summary>
        public int RosterSize { get; }

        /// <summary>Why the edit was refused. Empty unless <see cref="Change" /> is Refused.</summary>
        public string Reason { get; }

        /// <summary>When the roster moved, in UTC.</summary>
        public DateTime Timestamp { get; }

        /// <summary>Stamps the current time onto a new event.</summary>
        public static RoomRosterChanged Create(
            RoomRosterChange change,
            string membershipId,
            string characterId,
            string characterName,
            int rosterSize,
            string reason = null) =>
            new(change, membershipId, characterId, characterName, rosterSize, reason, DateTime.UtcNow);
    }
}
