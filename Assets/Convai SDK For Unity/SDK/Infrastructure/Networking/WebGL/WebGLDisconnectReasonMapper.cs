using LiveKit;
using TransportDisconnectReason = Convai.Infrastructure.Networking.Transport.DisconnectReason;

namespace Convai.Infrastructure.Networking.WebGL
{
    /// <summary>
    ///     Utility class for mapping LiveKit disconnect reasons to transport disconnect reasons.
    /// </summary>
    internal static class WebGLDisconnectReasonMapper
    {
        /// <summary>
        ///     Maps a LiveKit disconnect reason to a transport disconnect reason.
        /// </summary>
        /// <param name="reason">The LiveKit disconnect reason.</param>
        /// <returns>The mapped transport disconnect reason.</returns>
        public static TransportDisconnectReason Map(DisconnectReason? reason)
        {
            if (!reason.HasValue)
                return TransportDisconnectReason.Unknown;

            return reason.Value switch
            {
                DisconnectReason.CLIENT_INITIATED => TransportDisconnectReason.ClientInitiated,
                DisconnectReason.DUPLICATE_IDENTITY => TransportDisconnectReason.DuplicateIdentity,
                DisconnectReason.SERVER_SHUTDOWN => TransportDisconnectReason.ServerShutdown,
                DisconnectReason.PARTICIPANT_REMOVED => TransportDisconnectReason.ParticipantRemoved,
                DisconnectReason.ROOM_DELETED => TransportDisconnectReason.RoomDeleted,
                DisconnectReason.STATE_MISMATCH => TransportDisconnectReason.TransportError,
                DisconnectReason.JOIN_FAILURE => TransportDisconnectReason.JoinFailure,
                DisconnectReason.SIGNAL_CLOSE => TransportDisconnectReason.TransportError,
                _ => TransportDisconnectReason.Unknown
            };
        }
    }
}
