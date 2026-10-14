using Convai.Infrastructure.Networking.Native;
using NUnit.Framework;
using NativeReason = LiveKit.Proto.DisconnectReason;
using TransportReason = Convai.Infrastructure.Networking.Transport.DisconnectReason;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class NativeDisconnectReasonMapperTests
    {
        [TestCase(NativeReason.ClientInitiated, TransportReason.ClientInitiated)]
        [TestCase(NativeReason.DuplicateIdentity, TransportReason.DuplicateIdentity)]
        [TestCase(NativeReason.ServerShutdown, TransportReason.ServerShutdown)]
        [TestCase(NativeReason.ParticipantRemoved, TransportReason.ParticipantRemoved)]
        [TestCase(NativeReason.RoomDeleted, TransportReason.RoomDeleted)]
        [TestCase(NativeReason.StateMismatch, TransportReason.TransportError)]
        [TestCase(NativeReason.JoinFailure, TransportReason.JoinFailure)]
        [TestCase(NativeReason.SignalClose, TransportReason.TransportError)]
        [TestCase(NativeReason.Migration, TransportReason.Unknown)]
        [TestCase(NativeReason.UnknownReason, TransportReason.Unknown)]
        public void Map_KnownValues(NativeReason native, TransportReason expected)
        {
            Assert.That(NativeDisconnectReasonMapper.Map(native), Is.EqualTo(expected));
        }

        [Test]
        public void Map_UnknownNumericValue_IsUnknown()
        {
            Assert.That(NativeDisconnectReasonMapper.Map((NativeReason)999), Is.EqualTo(TransportReason.Unknown));
        }
    }
}
