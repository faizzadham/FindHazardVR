using System;
using System.Text;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.Models.LipSync;
using Convai.Infrastructure.Protocol;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [Test]
        public void LipSyncFastPath_ValidPacket_PublishesPackedData()
        {
            RtviTestContext context = CreateContext(CreateLipSyncOptions());
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            LipSyncPackedDataReceived captured = default;
            context.EventHub.Subscribe<LipSyncPackedDataReceived>(evt => captured = evt);

            bool handled = context.Handler.TryHandleLipSyncServerMessage(CreateRawPacket(
                "{\"type\":\"server-message\",\"payload\":{\"type\":\"chunked-neurosync-blendshapes\",\"format\":\"arkit\",\"blendshapes\":[[0.1,0.2]],\"response_id\":\"response-1\",\"neurosync_turn_id\":1,\"epoch\":2}}",
                "participant-1"));

            Assert.IsTrue(handled);
            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual(1, captured.FrameCount);
            Assert.AreEqual("response-1", captured.Chunk.ResponseId);
        }

        [Test]
        public void LipSyncFastPath_NonCandidate_ReturnsFalse()
        {
            RtviTestContext context = CreateContext(CreateLipSyncOptions());

            bool handled = context.Handler.TryHandleLipSyncServerMessage(CreateRawPacket(
                "{\"type\":\"bot-ready\"}",
                "participant-1"));

            Assert.IsFalse(handled);
        }

        [Test]
        public void LipSyncFastPath_MalformedCandidate_IsConsumedWithoutPublication()
        {
            RtviTestContext context = CreateContext(CreateLipSyncOptions());
            int published = 0;
            context.EventHub.Subscribe<LipSyncPackedDataReceived>(_ => published++);

            bool handled = context.Handler.TryHandleLipSyncServerMessage(CreateRawPacket(
                "{\"type\":\"server-message\",\"payload\":{\"type\":\"chunked-neurosync-blendshapes\",\"blendshapes\":[[0.1]}",
                "participant-1"));

            Assert.IsTrue(handled);
            Assert.AreEqual(0, published);
        }

        [Test]
        public void LipSyncFastPath_UnresolvedParticipant_IsConsumedWithoutPublication()
        {
            RtviTestContext context = CreateContext(CreateLipSyncOptions());
            int published = 0;
            context.EventHub.Subscribe<LipSyncPackedDataReceived>(_ => published++);

            bool handled = context.Handler.TryHandleLipSyncServerMessage(CreateRawPacket(
                "{\"type\":\"server-message\",\"payload\":{\"type\":\"chunked-neurosync-blendshapes\",\"format\":\"arkit\",\"blendshapes\":[[0.1,0.2]]}}",
                "missing-participant"));

            Assert.IsTrue(handled);
            Assert.AreEqual(0, published);
        }

        private static LipSyncTransportOptions CreateLipSyncOptions() => new(
            true,
            "neurosync",
            LipSyncProfileId.ARKit,
            "arkit",
            new[] { "A", "B" },
            true,
            10,
            60,
            LipSyncTransportOptions.DefaultFramesBufferDuration);

        private static ProtocolPacket CreateRawPacket(string json, string participantId) => new(
            Encoding.UTF8.GetBytes(json),
            participantId,
            "rtvi-ai",
            true);
    }
}
