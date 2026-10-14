using NativeReason = LiveKit.Proto.DisconnectReason;
using TransportReason = Convai.Infrastructure.Networking.Transport.DisconnectReason;

namespace Convai.Infrastructure.Networking.Native
{
    /// <summary>Maps the native LiveKit disconnect reason to the transport vocabulary.</summary>
    internal static class NativeDisconnectReasonMapper
    {
        public static TransportReason Map(NativeReason reason) => reason switch
        {
            NativeReason.ClientInitiated => TransportReason.ClientInitiated,
            NativeReason.DuplicateIdentity => TransportReason.DuplicateIdentity,
            NativeReason.ServerShutdown => TransportReason.ServerShutdown,
            NativeReason.ParticipantRemoved => TransportReason.ParticipantRemoved,
            NativeReason.RoomDeleted => TransportReason.RoomDeleted,
            NativeReason.StateMismatch => TransportReason.TransportError,
            NativeReason.JoinFailure => TransportReason.JoinFailure,
            NativeReason.SignalClose => TransportReason.TransportError,
            _ => TransportReason.Unknown
        };
    }
}
