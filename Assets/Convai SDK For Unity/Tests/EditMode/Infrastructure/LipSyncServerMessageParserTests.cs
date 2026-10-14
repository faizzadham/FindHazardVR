using System;
using System.Text;
using Convai.Domain.Models.LipSync;
using Convai.Infrastructure.Networking;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class LipSyncServerMessageParserTests
    {
        [Test]
        public void TryParse_ChunkedNumericPayload_ParsesFrames()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""blendshapes"": [
                    [0.1, 0.2],
                    [0.3, 0.4]
                ]
            }");
            LipSyncTransportOptions options = CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                options,
                out bool handled,
                out string payloadType,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(LipSyncServerMessageParser.ChunkedPayloadType, payloadType);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.IsNotNull(chunk);
            Assert.AreEqual(2, chunk.FrameCount);
            Assert.AreEqual(0.1f, chunk.Frames[0][0], 0.0001f);
            Assert.AreEqual(0.4f, chunk.Frames[1][1], 0.0001f);
        }

        [Test]
        public void TryParse_SingleNumericPayload_ParsesFrame()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""blendshapes"": [0.7, 0.2]
            }");
            LipSyncTransportOptions options = CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                options,
                out bool handled,
                out string payloadType,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(LipSyncServerMessageParser.SinglePayloadType, payloadType);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.AreEqual(1, chunk.FrameCount);
            Assert.AreEqual(0.7f, chunk.Frames[0][0], 0.0001f);
            Assert.AreEqual(0.2f, chunk.Frames[0][1], 0.0001f);
        }

        [Test]
        public void TryParse_NamedPayload_ParsesMappedChannels()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""mha"",
                ""blendshapes"": [
                    { ""jawOpen"": 0.75 },
                    { ""jawOpen"": ""0.25"" }
                ]
            }");
            LipSyncTransportOptions options = CreateOptions(LipSyncProfileId.MetaHuman, "mha", "jawOpen");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                options,
                out bool handled,
                out string payloadType,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(LipSyncServerMessageParser.ChunkedPayloadType, payloadType);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.AreEqual(2, chunk.FrameCount);
            Assert.AreEqual(0.75f, chunk.Frames[0][0], 0.0001f);
            Assert.AreEqual(0.25f, chunk.Frames[1][0], 0.0001f);
        }

        [Test]
        public void TryParse_FormatMismatch_ReturnsDropReason()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""mha"",
                ""blendshapes"": [[0.1]]
            }");
            LipSyncTransportOptions options = CreateOptions(LipSyncProfileId.ARKit, "arkit", "A");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                options,
                out bool handled,
                out _,
                out _,
                out string dropReason);

            Assert.IsFalse(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual("lipsync.parse.format_mismatch", dropReason);
        }

        [Test]
        public void TryParse_InvalidJsonLipSyncCandidate_IsHandledAndDropped()
        {
            string malformed =
                "{\"type\":\"server-message\",\"payload\":{\"type\":\"chunked-neurosync-blendshapes\",\"blendshapes\":[[0.1]}";
            ReadOnlyMemory<byte> payload = Encoding.UTF8.GetBytes(malformed);

            Assert.IsTrue(LipSyncServerMessageParser.MayContainLipSyncServerMessage(payload.Span));

            bool parsed = LipSyncServerMessageParser.TryParse(
                payload,
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A"),
                out bool handled,
                out _,
                out _,
                out string dropReason);

            Assert.IsFalse(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual("lipsync.parse.payload_invalid_json", dropReason);
        }

        [Test]
        public void TryParse_EmptyBlendshapeArray_ReturnsInvalidChunkDropReason()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""blendshapes"": []
            }");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A"),
                out bool handled,
                out _,
                out _,
                out string dropReason);

            Assert.IsFalse(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual("lipsync.parse.invalid_chunk_payload", dropReason);
        }

        [Test]
        public void TryParse_MixedChunkFrames_SkipsInvalidFrames()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""blendshapes"": [
                    [0.1, 0.2],
                    [],
                    { ""A"": 0.5 },
                    [{ ""complex"": 1 }],
                    4
                ]
            }");
            LipSyncTransportOptions options = CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                options,
                out bool handled,
                out _,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.AreEqual(2, chunk.FrameCount);
            Assert.AreEqual(0.1f, chunk.Frames[0][0], 0.0001f);
            Assert.AreEqual(0.2f, chunk.Frames[0][1], 0.0001f);
            Assert.AreEqual(0.5f, chunk.Frames[1][0], 0.0001f);
            Assert.AreEqual(0f, chunk.Frames[1][1], 0.0001f);
        }

        [Test]
        public void TryParse_SingleNumericPayload_TrimsExtrasAndPadsMissing()
        {
            LipSyncTransportOptions options = CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B");

            ReadOnlyMemory<byte> trimPacket = WrapServerMessage(@"
            {
                ""type"": ""neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""blendshapes"": [0.1, 0.2, 0.9]
            }");
            bool trimParsed = LipSyncServerMessageParser.TryParse(
                trimPacket,
                60f,
                options,
                out bool trimHandled,
                out _,
                out LipSyncPackedChunk trimChunk,
                out string trimDropReason);

            Assert.IsTrue(trimParsed);
            Assert.IsTrue(trimHandled);
            Assert.AreEqual(string.Empty, trimDropReason);
            Assert.AreEqual(0.1f, trimChunk.Frames[0][0], 0.0001f);
            Assert.AreEqual(0.2f, trimChunk.Frames[0][1], 0.0001f);

            ReadOnlyMemory<byte> padPacket = WrapServerMessage(@"
            {
                ""type"": ""neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""blendshapes"": [0.3]
            }");
            bool padParsed = LipSyncServerMessageParser.TryParse(
                padPacket,
                60f,
                options,
                out bool padHandled,
                out _,
                out LipSyncPackedChunk padChunk,
                out string padDropReason);

            Assert.IsTrue(padParsed);
            Assert.IsTrue(padHandled);
            Assert.AreEqual(string.Empty, padDropReason);
            Assert.AreEqual(0.3f, padChunk.Frames[0][0], 0.0001f);
            Assert.AreEqual(0f, padChunk.Frames[0][1], 0.0001f);
        }

        [Test]
        public void TryParse_NonLipSyncServerMessage_ReturnsUnhandled()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""bot-emotion"",
                ""emotion"": ""happy"",
                ""scale"": 50
            }");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A"),
                out bool handled,
                out string payloadType,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsFalse(parsed);
            Assert.IsFalse(handled);
            Assert.AreEqual(string.Empty, payloadType);
            Assert.IsNull(chunk);
            Assert.AreEqual(string.Empty, dropReason);
        }

        [Test]
        public void TryParse_ServerMessageWithDataEnvelope_ParsesChunkedPayload()
        {
            string json = @"
            {
                ""type"": ""server-message"",
                ""data"": {
                    ""type"": ""chunked-neurosync-blendshapes"",
                    ""format"": ""arkit"",
                    ""blendshapes"": [
                        [0.1, 0.2],
                        [0.3, 0.4]
                    ]
                }
            }";

            bool parsed = LipSyncServerMessageParser.TryParse(
                Encoding.UTF8.GetBytes(json),
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B"),
                out bool handled,
                out string payloadType,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(LipSyncServerMessageParser.ChunkedPayloadType, payloadType);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.IsNotNull(chunk);
            Assert.AreEqual(2, chunk.FrameCount);
            Assert.AreEqual(0.1f, chunk.Frames[0][0], 0.0001f);
            Assert.AreEqual(0.4f, chunk.Frames[1][1], 0.0001f);
        }

        [Test]
        public void TryParse_IncludesSequenceAndSourceTimestampMetadata_WhenProvided()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""sequence"": 17,
                ""timestamp"": 123.456,
                ""blendshapes"": [
                    [0.1, 0.2]
                ]
            }");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B"),
                out bool handled,
                out _,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.IsNotNull(chunk);
            Assert.AreEqual(17, chunk.Sequence);
        }

        [Test]
        public void TryParse_AheadChunkMetadata_PopulatesTimelineFieldsAndFrameRate()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""response_id"": ""session:r4"",
                ""neurosync_turn_id"": 4,
                ""epoch"": 2,
                ""sequence"": 17,
                ""fps"": 72,
                ""start_frame_index"": 120,
                ""blendshapes"": [
                    [0.1, 0.2],
                    [0.3, 0.4]
                ]
            }");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B"),
                out bool handled,
                out _,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.AreEqual("session:r4", chunk.ResponseId);
            Assert.AreEqual(4, chunk.NeuroSyncTurnId);
            Assert.AreEqual(2, chunk.Epoch);
            Assert.AreEqual(17, chunk.Sequence);
            Assert.AreEqual(72f, chunk.FrameRate);
            Assert.AreEqual(120, chunk.StartFrameIndex);
            Assert.IsTrue(chunk.HasOwnerMetadata);
            Assert.IsTrue(chunk.HasTimelineMetadata);
        }

        [Test]
        public void TryParse_AheadChunkTurnIdAlias_PopulatesNeuroSyncTurnId()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""response_id"": ""session:r9"",
                ""turn_id"": 9,
                ""start_frame_index"": 0,
                ""blendshapes"": [
                    [0.1, 0.2]
                ]
            }");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B"),
                out bool handled,
                out _,
                out LipSyncPackedChunk chunk,
                out string dropReason);

            Assert.IsTrue(parsed);
            Assert.IsTrue(handled);
            Assert.AreEqual(string.Empty, dropReason);
            Assert.AreEqual(9, chunk.NeuroSyncTurnId);
            Assert.IsTrue(chunk.HasOwnerMetadata);
            Assert.IsTrue(chunk.HasTimelineMetadata);
        }

        [Test]
        public void TryParse_NegativeStartFrameIndex_DoesNotMarkTimelineMetadata()
        {
            ReadOnlyMemory<byte> packet = WrapServerMessage(@"
            {
                ""type"": ""chunked-neurosync-blendshapes"",
                ""format"": ""arkit"",
                ""response_id"": ""session:r4"",
                ""start_frame_index"": -1,
                ""blendshapes"": [
                    [0.1, 0.2]
                ]
            }");

            bool parsed = LipSyncServerMessageParser.TryParse(
                packet,
                60f,
                CreateOptions(LipSyncProfileId.ARKit, "arkit", "A", "B"),
                out _,
                out _,
                out LipSyncPackedChunk chunk,
                out _);

            Assert.IsTrue(parsed);
            Assert.IsTrue(chunk.HasOwnerMetadata);
            Assert.IsFalse(chunk.HasTimelineMetadata);
        }

        private static ReadOnlyMemory<byte> WrapServerMessage(string payloadObjectJson)
        {
            string envelope = "{\"type\":\"server-message\",\"payload\":" + payloadObjectJson + "}";
            return Encoding.UTF8.GetBytes(envelope);
        }

        private static LipSyncTransportOptions CreateOptions(
            LipSyncProfileId profileId,
            string format,
            params string[] sourceNames)
        {
            return new LipSyncTransportOptions(
                true,
                "neurosync",
                profileId,
                format,
                sourceNames,
                true,
                10,
                60,
                LipSyncTransportOptions.DefaultFramesBufferDuration);
        }
    }
}
