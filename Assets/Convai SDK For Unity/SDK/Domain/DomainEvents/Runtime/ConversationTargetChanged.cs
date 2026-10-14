using System;

namespace Convai.Domain.DomainEvents.Runtime
{
    /// <summary>
    ///     How far a change of conversation target has got.
    /// </summary>
    /// <remarks>
    ///     A manager-level target request and its eventual confirmation are a round trip apart, and
    ///     everything the service does because of the move
    ///     — including ending the previous character's answer — happens between them. Direct room
    ///     APIs and roster handovers can publish <see cref="Confirmed" /> without a Requested phase.
    /// </remarks>
    public enum ConversationTargetChangePhase
    {
        /// <summary>
        ///     ConvaiManager has claimed the routing window and is about to send the move. The send
        ///     or service response can still fail. Direct room APIs do not emit this phase.
        /// </summary>
        Requested = 0,

        /// <summary>The service response has reconciled the authoritative conversation route.</summary>
        Confirmed,

        /// <summary>
        ///     The requested move was refused. The response can still reconcile a newer
        ///     authoritative route, which is published as <see cref="Confirmed" /> first.
        /// </summary>
        Failed
    }

    /// <summary>
    ///     Domain event raised as the conversation moves from one character in a room to another.
    /// </summary>
    /// <remarks>
    ///     Published for multi-character rooms only, because a room with one character has nothing
    ///     to move the conversation between.
    /// </remarks>
    public readonly struct ConversationTargetChanged
    {
        /// <summary>
        ///     Creates the event with an explicit timestamp. Prefer <see cref="Create" />, which
        ///     stamps the current time.
        /// </summary>
        public ConversationTargetChanged(
            ConversationTargetChangePhase phase,
            string characterId,
            string characterName,
            string membershipId,
            string reason,
            DateTime timestamp)
        {
            Phase = phase;
            CharacterId = characterId ?? string.Empty;
            CharacterName = characterName ?? string.Empty;
            MembershipId = membershipId ?? string.Empty;
            Reason = reason ?? string.Empty;
            Timestamp = timestamp;
        }

        /// <summary>How far the change has got.</summary>
        public ConversationTargetChangePhase Phase { get; }

        /// <summary>Convai Character ID of the character the conversation is moving to.</summary>
        public string CharacterId { get; }

        /// <summary>Display name of that character, for logs and UI.</summary>
        public string CharacterName { get; }

        /// <summary>The room membership the move addresses.</summary>
        public string MembershipId { get; }

        /// <summary>Why the requested move was refused. Empty unless <see cref="Phase" /> is Failed.</summary>
        public string Reason { get; }

        /// <summary>When the phase was reached, in UTC.</summary>
        public DateTime Timestamp { get; }

        /// <summary>Stamps the current time onto a new event.</summary>
        public static ConversationTargetChanged Create(
            ConversationTargetChangePhase phase,
            string characterId,
            string characterName,
            string membershipId,
            string reason = null) =>
            new(phase, characterId, characterName, membershipId, reason, DateTime.UtcNow);
    }
}
