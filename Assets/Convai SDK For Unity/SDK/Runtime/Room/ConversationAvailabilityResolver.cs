using Convai.Domain.DomainEvents.Session;

namespace Convai.Runtime.Room
{
    /// <summary>
    ///     Works out whether the player can talk to a character right now.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         There is exactly one of these, and everything that gates input asks it: the text
    ///         field, the microphone, and the send path itself. Before it existed, five places each
    ///         decided for themselves — <c>!_isInjected</c> here, <c>!HasPlayer</c> there,
    ///         <c>!IsConnected</c> somewhere else — and none could see the others, so a message could
    ///         pass every check the SDK knew about and still reach nobody.
    ///     </para>
    ///     <para>
    ///         Kept as a plain decision over plain inputs, like <see cref="LiveRosterPlanner" /> and
    ///         <see cref="LateCharacterPlanner" />: the caller answers every Unity-side and
    ///         room-side question, and this only decides.
    ///     </para>
    /// </remarks>
    internal static class ConversationAvailabilityResolver
    {
        /// <param name="hasCharacter">
        ///     A character is being addressed at all, and it is still present and enabled in the
        ///     scene. A character whose GameObject has been turned off is not one the player can
        ///     talk to, whatever the room still believes about it.
        /// </param>
        /// <param name="isInjected">That character has been set up by the manager.</param>
        /// <param name="roomState">The room's own connection state.</param>
        /// <param name="roomHasRoster">
        ///     Whether the room keeps a roster at all. A single-character room does not, and the
        ///     difference is not something <paramref name="membershipStatus" /> can carry on its
        ///     own: <c>null</c> means "there is no roster to be in" in one room and "there is a
        ///     roster and this character is not in it" in the other, and those are opposite answers.
        /// </param>
        /// <param name="membershipStatus">
        ///     What the room says about this character, when the room keeps a roster and this
        ///     character is in it. <c>null</c> otherwise.
        /// </param>
        /// <param name="isCharacterReady">The service has announced this character.</param>
        /// <param name="isSpeaking">The character is answering right now.</param>
        internal static ConvaiConversationAvailability Resolve(
            bool hasCharacter,
            bool isInjected,
            SessionState roomState,
            bool roomHasRoster,
            CharacterRoomStatus? membershipStatus,
            bool isCharacterReady,
            bool isSpeaking)
        {
            // Present, set up, and only then worth asking the room about. The scene half is first
            // because it is the one the room can be wrong about: a roster edit is a round trip, so
            // between a character being disabled and the service acknowledging it the membership
            // still reads Ready — and answering Ready there tells a game to send something to a
            // character that is not in the scene any more.
            if (!hasCharacter || !isInjected)
                return ConvaiConversationAvailability.NoCharacter;

            // A room that keeps a roster routes by that roster and by nothing else, so a character
            // with no seat in it cannot be reached however healthy it looks. Its own readiness flag
            // is the trap: the recovery path sets it, a previous connection leaves it set, and
            // neither is undone by the character failing to make it into this room. Falling through
            // to that flag here reported a character that is not in the conversation as Ready —
            // which is precisely the promise this type exists to stop the SDK making.
            if (roomHasRoster && !membershipStatus.HasValue)
                return ConvaiConversationAvailability.Unavailable;

            // A character the room has given up on stays unavailable whatever the room is doing;
            // reporting it as merely "connecting" would promise a recovery that is not coming.
            if (membershipStatus == CharacterRoomStatus.Failed)
                return ConvaiConversationAvailability.Unavailable;

            switch (roomState)
            {
                case SessionState.Disconnected:
                case SessionState.Disconnecting:
                    return ConvaiConversationAvailability.Offline;

                case SessionState.Error:
                    return ConvaiConversationAvailability.Unavailable;

                case SessionState.Connecting:
                case SessionState.Reconnecting:
                    return ConvaiConversationAvailability.Connecting;
            }

            // Connected. The room being up says nothing about this character: in a roster room the
            // membership is authoritative, and in a single-character room — the only place the
            // membership can still be null by the time execution reaches here — the service's own
            // readiness signal is all there is.
            bool characterConfirmed = membershipStatus.HasValue
                ? membershipStatus.Value == CharacterRoomStatus.Ready
                : isCharacterReady;

            if (!characterConfirmed)
                return ConvaiConversationAvailability.Preparing;

            return isSpeaking
                ? ConvaiConversationAvailability.Answering
                : ConvaiConversationAvailability.Ready;
        }

        /// <summary>
        ///     Closes the input window synchronously while the room is changing who receives it.
        /// </summary>
        /// <remarks>
        ///     The manager publishes availability changes from its Unity tick, but a game can call
        ///     <c>TalkTo</c> and send text or press push-to-talk again in the same frame. This
        ///     decision is therefore applied when the property is read, not only on the next tick.
        /// </remarks>
        internal static ConvaiConversationAvailability ApplyRoutingTransition(
            ConvaiConversationAvailability addressedAvailability,
            bool targetChangeInFlight) =>
            targetChangeInFlight && addressedAvailability.CanAcceptPlayerInput()
                ? ConvaiConversationAvailability.Preparing
                : addressedAvailability;
    }
}
