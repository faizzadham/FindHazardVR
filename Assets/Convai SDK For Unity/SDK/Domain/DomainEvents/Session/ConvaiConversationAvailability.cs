namespace Convai.Domain.DomainEvents.Session
{
    /// <summary>
    ///     Whether the player can talk to a character right now, and if not, what is in the way.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         "Connected" is not one thing, and treating it as one is what made a lost message
    ///         invisible. A room can be connected while the character being addressed has not been
    ///         announced by the service yet; anything said in that window reaches nobody, and the
    ///         player is given no reason to think so. This names which situation they are in.
    ///     </para>
    ///     <para>
    ///         Values are listed in lifecycle order. Do not compare them with <c>&lt;</c> or
    ///         <c>&gt;</c> — ask <see cref="ConvaiConversationAvailabilityExtensions.CanAcceptPlayerInput" />
    ///         instead, because the two states that accept input are not adjacent and never will be.
    ///     </para>
    /// </remarks>
    public enum ConvaiConversationAvailability
    {
        /// <summary>No character is being addressed, or it is not set up yet.</summary>
        NoCharacter = 0,

        /// <summary>There is no room. Nothing is listening.</summary>
        Offline = 1,

        /// <summary>The room is being established.</summary>
        Connecting = 2,

        /// <summary>
        ///     The room is connected, but this character has not been confirmed by the service yet.
        ///     Messages sent now are lost, which is why this is its own state rather than part of
        ///     <see cref="Connecting" />: from the room's point of view everything is fine.
        /// </summary>
        Preparing = 3,

        /// <summary>The character can hear the player.</summary>
        Ready = 4,

        /// <summary>The character is answering. Input is still accepted — that is barge-in.</summary>
        Answering = 5,

        /// <summary>
        ///     The character failed to start, or left the room. Unlike <see cref="Connecting" /> and
        ///     <see cref="Preparing" />, this does not resolve on its own.
        /// </summary>
        Unavailable = 6
    }

    /// <summary>Questions worth asking about a <see cref="ConvaiConversationAvailability" />.</summary>
    public static class ConvaiConversationAvailabilityExtensions
    {
        /// <summary>
        ///     Whether a message sent now — typed or spoken — will reach the character.
        /// </summary>
        /// <remarks>
        ///     <see cref="ConvaiConversationAvailability.Answering" /> counts: interrupting a
        ///     character that is speaking is ordinary conversation, and refusing it would make the
        ///     player wait out every answer.
        /// </remarks>
        public static bool CanAcceptPlayerInput(this ConvaiConversationAvailability availability) =>
            availability is ConvaiConversationAvailability.Ready
                or ConvaiConversationAvailability.Answering;

        /// <summary>
        ///     Whether this resolves on its own, given a moment. A UI can wait through these without
        ///     telling the player to do anything.
        /// </summary>
        public static bool IsSettling(this ConvaiConversationAvailability availability) =>
            availability is ConvaiConversationAvailability.Connecting
                or ConvaiConversationAvailability.Preparing;
    }
}
