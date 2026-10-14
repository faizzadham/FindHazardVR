using Convai.Infrastructure.Networking.WebGL;
using NUnit.Framework;
using WebGLReason = LiveKit.DisconnectReason;
using TransportReason = Convai.Infrastructure.Networking.Transport.DisconnectReason;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class WebGLDisconnectReasonMapperTests
    {
        [TestCase(0, TransportReason.Unknown)]
        [TestCase(1, TransportReason.ClientInitiated)]
        [TestCase(2, TransportReason.DuplicateIdentity)]
        [TestCase(3, TransportReason.ServerShutdown)]
        [TestCase(4, TransportReason.ParticipantRemoved)]
        [TestCase(5, TransportReason.RoomDeleted)]
        [TestCase(6, TransportReason.TransportError)]
        [TestCase(7, TransportReason.JoinFailure)]
        [TestCase(8, TransportReason.Unknown)]
        [TestCase(9, TransportReason.TransportError)]
        [TestCase(999, TransportReason.Unknown)]
        public void Map_ProtocolNumericValue_PreservesTransportReason(int protocolValue, TransportReason expected)
        {
            Assert.That(WebGLDisconnectReasonMapper.Map((WebGLReason)protocolValue), Is.EqualTo(expected));
        }

        [Test]
        public void Map_MissingReason_IsUnknown()
        {
            Assert.That(WebGLDisconnectReasonMapper.Map(null), Is.EqualTo(TransportReason.Unknown));
        }
    }
}
