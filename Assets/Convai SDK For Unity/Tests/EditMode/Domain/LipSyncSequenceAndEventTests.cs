using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.Models.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    public class LipSyncPackedEventTests
    {
        [Test]
        public void LipSyncPackedChunk_ComputesDurationAndValidity()
        {
            LipSyncPackedChunk chunk = new(
                LipSyncProfileId.ARKit,
                60f,
                new[] { "EyeBlinkLeft", "EyeBlinkRight" },
                new[] { new[] { 0.2f, 0.1f }, new[] { 0.3f, 0.2f }, new[] { 0.4f, 0.3f } });

            Assert.IsTrue(chunk.IsValid);
            Assert.AreEqual(3, chunk.FrameCount);
            Assert.AreEqual(3f / 60f, chunk.Duration, 0.0001f);
        }

        [Test]
        public void LipSyncPackedDataReceived_UsesChunkProfileId()
        {
            LipSyncPackedChunk chunk = new(
                LipSyncProfileId.MetaHuman,
                60f,
                new[] { "jawOpen" },
                new[] { new[] { 0.5f }, new[] { 0.25f } });

            var evt = LipSyncPackedDataReceived.Create("char-2", "participant-2", chunk);

            Assert.IsTrue(evt.IsValid);
            Assert.AreEqual(LipSyncProfileId.MetaHuman, evt.ProfileId);
            Assert.AreEqual(2, evt.FrameCount);
            Assert.AreEqual(2f / 60f, evt.Duration, 0.0001f);
        }
    }
}
