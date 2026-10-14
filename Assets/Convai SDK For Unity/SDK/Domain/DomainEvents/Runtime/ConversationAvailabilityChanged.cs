using System;
using Convai.Domain.DomainEvents.Session;

namespace Convai.Domain.DomainEvents.Runtime
{
    /// <summary>
    ///     Domain event raised when the answer to "can the player talk right now" changes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The verdict is about the character being addressed, not about the room, and it moves
    ///         for two different reasons: that character's own readiness changed, or the player
    ///         started addressing a different character whose readiness differs. Both arrive here.
    ///     </para>
    ///     <para>
    ///         This is the event a chat field, a microphone button or a prompt should react to.
    ///         Reacting to session state instead is the mistake it exists to remove: a connected room
    ///         says nothing about whether the addressed character has been announced yet.
    ///     </para>
    /// </remarks>
    public readonly struct ConversationAvailabilityChanged
    {
        /// <summary>
        ///     Creates the event with an explicit timestamp. Prefer <see cref="Create" />, which
        ///     stamps the current time.
        /// </summary>
        public ConversationAvailabilityChanged(
            ConvaiConversationAvailability availability,
            ConvaiConversationAvailability previousAvailability,
            string characterId,
            string characterName,
            DateTime timestamp)
        {
            Availability = availability;
            PreviousAvailability = previousAvailability;
            CharacterId = characterId ?? string.Empty;
            CharacterName = characterName ?? string.Empty;
            Timestamp = timestamp;
        }

        /// <summary>The verdict now.</summary>
        public ConvaiConversationAvailability Availability { get; }

        /// <summary>The verdict this replaced.</summary>
        public ConvaiConversationAvailability PreviousAvailability { get; }

        /// <summary>Convai Character ID of the character being addressed, when there is one.</summary>
        public string CharacterId { get; }

        /// <summary>Display name of that character, for logs and UI.</summary>
        public string CharacterName { get; }

        /// <summary>When the verdict moved, in UTC.</summary>
        public DateTime Timestamp { get; }

        /// <summary>Whether the player may send speech or text right now.</summary>
        public bool CanAcceptPlayerInput => Availability.CanAcceptPlayerInput();

        /// <summary>Stamps the current time onto a new event.</summary>
        public static ConversationAvailabilityChanged Create(
            ConvaiConversationAvailability availability,
            ConvaiConversationAvailability previousAvailability,
            string characterId,
            string characterName) =>
            new(availability, previousAvailability, characterId, characterName, DateTime.UtcNow);
    }
}
