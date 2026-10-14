using System.Collections.Generic;
using System.Linq;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Infrastructure.Protocol.Messages;
using Convai.RestAPI.Internal;
using Convai.Runtime.Room;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [TestCase("bot-llm-text", TranscriptSegmentSourceKind.BotLlmPreview, TranscriptLifecycle.Streaming, LogLevel.Info)]
        [TestCase("bot-output", TranscriptSegmentSourceKind.BotOutput, TranscriptLifecycle.Stable, LogLevel.Info)]
        [TestCase("bot-transcription", TranscriptSegmentSourceKind.LegacyBotTranscript, TranscriptLifecycle.Stable, LogLevel.Info)]
        [TestCase("bot-llm-text", TranscriptSegmentSourceKind.BotLlmPreview, TranscriptLifecycle.Streaming, LogLevel.Debug)]
        [TestCase("bot-output", TranscriptSegmentSourceKind.BotOutput, TranscriptLifecycle.Stable, LogLevel.Debug)]
        [TestCase("bot-transcription", TranscriptSegmentSourceKind.LegacyBotTranscript, TranscriptLifecycle.Stable, LogLevel.Debug)]
        public void CharacterTranscription_LogsOnlyEnabledMetadataAndPreservesDelivery(
            string route, TranscriptSegmentSourceKind source, TranscriptLifecycle lifecycle, LogLevel level)
        {
            const string text = "<transcript-placeholder>";
            RtviTestContext context = CreateContext();
            context.Logger.Level = level;
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "<character-placeholder>"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            var received = new List<CharacterTranscriptReceived>();
            context.EventHub.Subscribe<CharacterTranscriptReceived>(received.Add);

            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(route, text, "participant-1"));

            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0].Text == text, Is.True, "Transcript delivery must be unchanged.");
            Assert.That(received[0].SourceKind, Is.EqualTo(source));
            Assert.That(received[0].Lifecycle, Is.EqualTo(lifecycle));
            TestLogRecord[] logs = context.Logger.Entries.Where(entry =>
                entry.Source == "RTVIHandler" &&
                (entry.Category == LogCategory.Character || entry.Category == LogCategory.Events)).ToArray();
            Assert.That(logs.Length, Is.EqualTo(level == LogLevel.Debug ? 2 : 0),
                "The recording logger retains disabled calls too; Info must never call a transcript log sink.");
            foreach (TestLogRecord log in logs)
            {
                Assert.That(log.Level, Is.EqualTo(LogLevel.Debug));
                Assert.That(log.Message.Contains(text), Is.False, "Transcript content must never reach diagnostics.");
                StringAssert.Contains("participant=participant-1", log.Message);
                StringAssert.Contains($"length={text.Length}", log.Message);
                StringAssert.Contains($"source={source}", log.Message);
                StringAssert.Contains($"lifecycle={lifecycle}", log.Message);
            }
        }

        [TestCase("bot-llm-text", LogLevel.Info)]
        [TestCase("bot-output", LogLevel.Info)]
        [TestCase("bot-transcription", LogLevel.Info)]
        [TestCase("bot-llm-text", LogLevel.Debug)]
        [TestCase("bot-output", LogLevel.Debug)]
        [TestCase("bot-transcription", LogLevel.Debug)]
        public void CharacterTranscription_TypedEchoRemainsSuppressedWithoutLoggingText(string route, LogLevel level)
        {
            const string text = "<typed-placeholder>";
            RtviTestContext context = CreateContext();
            context.Logger.Level = level;
            context.Handler.SendData(new RTVIUserTextMessage(text, "typed-placeholder-id"));
            context.Logger.Entries.Clear();
            int delivered = 0;
            context.EventHub.Subscribe<CharacterTranscriptReceived>(_ => delivered++);

            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(route, text, "participant-1"));

            Assert.That(delivered, Is.Zero);
            TestLogRecord[] logs = context.Logger.Entries.Where(entry =>
                entry.Source == "RTVIHandler").ToArray();
            Assert.That(logs.Length, Is.EqualTo(level == LogLevel.Debug ? 1 : 0));
            foreach (TestLogRecord log in logs)
            {
                Assert.That(log.Level, Is.EqualTo(LogLevel.Debug));
                Assert.That(log.Message.Contains(text), Is.False, "Suppressed content must not reach diagnostics.");
                StringAssert.Contains("Suppressed typed text echo", log.Message);
                StringAssert.Contains($"length={text.Length}", log.Message);
            }
        }

        [TestCase(LogCategory.Character, LogCategory.Events)]
        [TestCase(LogCategory.Events, LogCategory.Character)]
        public void CharacterTranscription_RespectsEachDiagnosticCategory(LogCategory disabled, LogCategory retained)
        {
            RtviTestContext context = CreateContext();
            context.Logger.DisabledCategory = disabled;
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "<character-placeholder>"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-output", "<placeholder>", "participant-1"));
            TestLogRecord[] logs = context.Logger.Entries.Where(entry =>
                entry.Source == "RTVIHandler").ToArray();
            Assert.That(logs, Has.Length.EqualTo(1));
            Assert.That(logs[0].Category, Is.EqualTo(retained));
        }

        [TestCase("bot-output", LogCategory.Character)]
        [TestCase("bot-output", LogCategory.Events)]
        [TestCase("bot-tts-text", LogCategory.Character)]
        [TestCase("bot-tts-text", LogCategory.Events)]
        public void CharacterDiagnostics_CategoryOverrideWorksWhenDefaultDebugIsDisabled(
            string route, LogCategory enabledCategory)
        {
            const string text = "<transcript-placeholder>";
            RtviTestContext context = CreateContext();
            context.Logger.Level = LogLevel.Info;
            context.Logger.CategoryLevels[enabledCategory] = LogLevel.Debug;
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "<character-placeholder>"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            int delivered = 0;
            context.EventHub.Subscribe<CharacterTranscriptReceived>(_ => delivered++);
            context.EventHub.Subscribe<CharacterTtsTextChunk>(_ => delivered++);

            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(route, text, "participant-1"));

            Assert.That(delivered, Is.EqualTo(1));
            TestLogRecord[] logs = context.Logger.Entries.Where(entry =>
                entry.Source == "RTVIHandler" && entry.Level == LogLevel.Debug).ToArray();
            Assert.That(logs, Has.Length.EqualTo(1));
            Assert.That(logs[0].Category, Is.EqualTo(enabledCategory));
            Assert.That(logs[0].Message.Contains(text), Is.False, "Transcript content must never reach diagnostics.");
            StringAssert.Contains($"length={text.Length}", logs[0].Message);
        }

        [TestCase(LogLevel.Info)]
        [TestCase(LogLevel.Debug)]
        public void CharacterTtsText_LogsOnlyEnabledMetadataAndPreservesDelivery(LogLevel level)
        {
            const string text = "<tts-placeholder>";
            RtviTestContext context = CreateContext();
            context.Logger.Level = level;
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "<character-placeholder>"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            var received = new List<CharacterTtsTextChunk>();
            context.EventHub.Subscribe<CharacterTtsTextChunk>(received.Add);

            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-tts-text", text, "participant-1"));

            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0].Text == text, Is.True, "TTS text delivery must be unchanged.");
            TestLogRecord[] logs = context.Logger.Entries.Where(entry =>
                entry.Source == "RTVIHandler" &&
                (entry.Category == LogCategory.Character || entry.Category == LogCategory.Events)).ToArray();
            Assert.That(logs.Length, Is.EqualTo(level == LogLevel.Debug ? 2 : 0),
                "Info must never call a TTS text log sink.");
            foreach (TestLogRecord log in logs)
            {
                Assert.That(log.Level, Is.EqualTo(LogLevel.Debug));
                Assert.That(log.Message.Contains(text), Is.False, "TTS text must never reach diagnostics.");
                StringAssert.Contains("participant=participant-1", log.Message);
                StringAssert.Contains($"length={text.Length}", log.Message);
            }
        }

        [TestCase(LogLevel.Info)]
        [TestCase(LogLevel.Debug)]
        public void CharacterTtsText_TypedEchoRemainsSuppressedWithoutLoggingText(LogLevel level)
        {
            const string text = "<typed-placeholder>";
            RtviTestContext context = CreateContext();
            context.Logger.Level = level;
            context.Handler.SendData(new RTVIUserTextMessage(text, "typed-placeholder-id"));
            context.Logger.Entries.Clear();
            int delivered = 0;
            context.EventHub.Subscribe<CharacterTtsTextChunk>(_ => delivered++);

            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-tts-text", text, "participant-1"));

            Assert.That(delivered, Is.Zero);
            TestLogRecord[] logs = context.Logger.Entries.Where(entry =>
                entry.Source == "RTVIHandler").ToArray();
            Assert.That(logs.Length, Is.EqualTo(level == LogLevel.Debug ? 1 : 0));
            foreach (TestLogRecord log in logs)
            {
                Assert.That(log.Level, Is.EqualTo(LogLevel.Debug));
                Assert.That(log.Message.Contains(text), Is.False, "Suppressed content must not reach diagnostics.");
                StringAssert.Contains("Suppressed typed text echo", log.Message);
                StringAssert.Contains($"length={text.Length}", log.Message);
            }
        }

        [Test]
        public void BotReady_PublishesResolvedCharacter()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            CharacterReady captured = default;
            context.EventHub.Subscribe<CharacterReady>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-ready", "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
        }

        [Test]
        public void CharacterStatusReady_PublishesReadyEventWithMembershipAndParticipantIdentity()
        {
            RtviTestContext context = CreateContext();
            var character = new TestCharacterAgent("char-1", "Camila");
            context.Registry.RegisterCharacter(character);
            var membershipDetails = new JObject
            {
                ["membership_id"] = "membership-1",
                ["character_id"] = "char-1",
                ["character_session_id"] = "character-session-1",
                ["participant_identity"] = "character:membership-1",
                ["is_initial"] = true,
                ["provisioning_status"] = "dispatch_accepted"
            }.ToObject<RoomCharacterDetails>();
            MultiCharacterRoomSession session = context.Registry.Configure(
                new RoomDetails(
                    "token",
                    "room-name",
                    "session-1",
                    "wss://room",
                    roomSessionId: "room-1",
                    characters: new List<RoomCharacterDetails> { membershipDetails }),
                context.Registry.Characters);
            CharacterReady captured = default;
            int readyEventCount = 0;
            context.EventHub.Subscribe<CharacterReady>(evt =>
            {
                captured = evt;
                readyEventCount++;
            });

            context.Gateway.ProcessIncoming(CreateInboundPacket(
                "character-status",
                "participant-1",
                new JObject
                {
                    ["status"] = "ready",
                    ["about"] = new JObject
                    {
                        ["membership_id"] = "membership-1",
                        ["character_id"] = "char-1",
                        ["character_session_id"] = "character-session-1",
                        ["participant_identity"] = "character:membership-1",
                        ["is_initial"] = true,
                        ["roster_epoch"] = 1
                    }
                }));

            Assert.AreEqual(1, readyEventCount);
            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual("membership-1", captured.MembershipId);
            Assert.AreEqual("character-session-1", captured.CharacterSessionId);
            Assert.AreEqual("character:membership-1", captured.ParticipantIdentity);
            Assert.IsTrue(session.IsReady);
            Assert.AreEqual("participant-1", session.InitialCharacter.ParticipantId);
        }

        [Test]
        public void CharacterStatusReady_RelayedByAnotherCharacter_PreservesBothParticipantBindings()
        {
            RtviTestContext context = CreateContext();
            var characterA = new TestCharacterAgent("char-a", "Camila");
            var characterB = new TestCharacterAgent("char-b", "Marcus");
            context.Registry.RegisterCharacter(characterA);
            context.Registry.RegisterCharacter(characterB);
            var membershipA = new JObject
            {
                ["membership_id"] = "membership-a",
                ["character_id"] = "char-a",
                ["character_session_id"] = "character-session-a",
                ["participant_identity"] = "character:membership-a",
                ["is_initial"] = true,
                ["provisioning_status"] = "dispatch_accepted"
            }.ToObject<RoomCharacterDetails>();
            var membershipB = new JObject
            {
                ["membership_id"] = "membership-b",
                ["character_id"] = "char-b",
                ["character_session_id"] = "character-session-b",
                ["participant_identity"] = "character:membership-b",
                ["is_initial"] = false,
                ["provisioning_status"] = "dispatch_accepted"
            }.ToObject<RoomCharacterDetails>();
            MultiCharacterRoomSession session = context.Registry.Configure(
                new RoomDetails(
                    "token",
                    "room-name",
                    "session-1",
                    "wss://room",
                    roomSessionId: "room-1",
                    characters: new List<RoomCharacterDetails> { membershipA, membershipB }),
                context.Registry.Characters);
            CharacterRoomMembership roomCharacterA = session.FindByMembershipId("membership-a");
            CharacterRoomMembership roomCharacterB = session.FindByMembershipId("membership-b");
            session.MarkReady(roomCharacterA, "participant-a");
            session.MarkReady(roomCharacterB, "participant-b");
            context.Registry.SetParticipantId("char-a", "participant-a");
            context.Registry.SetParticipantId("char-b", "participant-b");
            CharacterReady captured = default;
            context.EventHub.Subscribe<CharacterReady>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateInboundPacket(
                "character-status",
                "participant-a",
                new JObject
                {
                    ["status"] = "ready",
                    ["about"] = new JObject
                    {
                        ["membership_id"] = "membership-b",
                        ["character_id"] = "char-b",
                        ["character_session_id"] = "character-session-b",
                        ["participant_identity"] = "character:membership-b",
                        ["is_initial"] = false,
                        ["roster_epoch"] = 2
                    }
                }));

            Assert.AreEqual("participant-a", roomCharacterA.ParticipantId);
            Assert.AreEqual("participant-b", roomCharacterB.ParticipantId);
            Assert.IsTrue(context.Registry.TryGetCharacterByParticipantId("participant-a", out var resolvedA));
            Assert.AreSame(characterA, resolvedA);
            Assert.AreEqual("char-b", captured.CharacterId);
            Assert.AreEqual("participant-b", captured.ParticipantId);
        }

        [Test]
        public void BotLifecycle_LogOnlyRoutes_AreRegistered()
        {
            RtviTestContext context = CreateContext();

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-stopped", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-tts-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-tts-stopped", "participant-1"));

            Assert.IsTrue(context.Logger.Contains("Character LLM stopped"));
            Assert.IsTrue(context.Logger.Contains("Character TTS started"));
            Assert.IsTrue(context.Logger.Contains("Character TTS stopped"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BotTurnCompleted_PreservesResponseIdAndDoesNotClearAnotherResponse(bool serverWrapped)
        {
            Convai.Infrastructure.Protocol.ProtocolPacket LifecyclePacket(string type, string participant, JObject data = null) =>
                serverWrapped ? CreateServerMessagePacket(type, data, participant) : CreateInboundPacket(type, participant, data);
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            CharacterTurnCompleted completed = default;
            context.EventHub.Subscribe<CharacterTurnCompleted>(e => completed = e);
            var speech = new List<CharacterSpeechStateChanged>();
            context.EventHub.Subscribe<CharacterSpeechStateChanged>(speech.Add);
            context.Gateway.ProcessIncoming(LifecyclePacket("bot-started-speaking", "participant-1",
                new JObject { ["response_id"] = "current" }));
            context.Gateway.ProcessIncoming(LifecyclePacket("bot-stopped-speaking", "participant-1",
                new JObject { ["response_id"] = "old" }));
            context.Gateway.ProcessIncoming(LifecyclePacket("bot-turn-completed", "participant-1",
                new JObject { ["response_id"] = "old" }));
            context.Gateway.ProcessIncoming(LifecyclePacket("bot-stopped-speaking", "participant-1"));
            Assert.That(completed.ResponseId, Is.EqualTo("old"));
            Assert.That(speech.Select(e => e.ResponseId), Is.EqualTo(new[] { "current", "old", "current" }));
        }

        [Test]
        public void BotTurnCompleted_ClearsGeneratedResponseIdentity()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            var transcripts = new List<CharacterTranscriptReceived>();
            context.EventHub.Subscribe<CharacterTranscriptReceived>(transcripts.Add);

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription", "First", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-turn-completed", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription", "Second", "participant-1"));

            Assert.AreEqual(2, transcripts.Count);
            Assert.AreNotEqual(transcripts[0].ResponseId, transcripts[1].ResponseId);
        }

        [Test]
        public void BotLlmStarted_BeginsNewResponseIdentityWithoutWaitingForPriorCompletion()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            var transcripts = new List<CharacterTranscriptReceived>();
            context.EventHub.Subscribe<CharacterTranscriptReceived>(transcripts.Add);

            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-output", "Interrupted answer", "participant-1"));
            context.Gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            context.Gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-output", "Next answer", "participant-1"));

            Assert.AreEqual(2, transcripts.Count);
            Assert.AreNotEqual(transcripts[0].ResponseId, transcripts[1].ResponseId);
            Assert.AreNotEqual(transcripts[0].TurnId, transcripts[1].TurnId);
        }

        [Test]
        public void ServerMessage_BotEmotion_PublishesResolvedEmotion()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            CharacterEmotionChanged captured = default;
            context.EventHub.Subscribe<CharacterEmotionChanged>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "bot-emotion",
                new Newtonsoft.Json.Linq.JObject { ["emotion"] = "happy", ["scale"] = 3 },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("happy", captured.Emotion);
            Assert.AreEqual(3, captured.Intensity);
        }
    }
}
