using Convai.Infrastructure.Networking.Connection;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class LiveKitRoomBackendAttributionTests
    {
        [Test]
        public void ResolveDataPacketParticipantId_MissingSender_ReturnsEmpty()
        {
            Assert.AreEqual(
                string.Empty,
                LiveKitRoomBackend.ResolveDataPacketParticipantId(null, null));
        }

        [TestCase("participant-sid", "character:membership-1", "participant-sid")]
        [TestCase("", "character:membership-1", "character:membership-1")]
        public void ResolveDataPacketParticipantId_KnownSender_PrefersSid(
            string sid,
            string identity,
            string expected)
        {
            Assert.AreEqual(expected, LiveKitRoomBackend.ResolveDataPacketParticipantId(sid, identity));
        }
    }
}
