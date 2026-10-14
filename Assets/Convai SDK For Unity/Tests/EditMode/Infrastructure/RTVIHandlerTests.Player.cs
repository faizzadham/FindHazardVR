using System.Linq;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Infrastructure.Protocol.Messages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [TestCase(LogLevel.Info, false, "<orphan-placeholder>")]
        [TestCase(LogLevel.Debug, false, "<orphan-placeholder>")]
        [TestCase(LogLevel.Debug, true, "<orphan-placeholder>")]
        [TestCase(LogLevel.Debug, false, null)]
        [TestCase(LogLevel.Debug, false, "")]
        public void OrphanedProcessedFinal_PreservesDeliveryAndLogsOnlyEnabledMetadata(
            LogLevel level, bool disablePlayer, string text)
        {
            RtviTestContext context = CreateContext();
            context.Logger.Level = level;
            if (disablePlayer) context.Logger.DisabledCategory = LogCategory.Player;

            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription", new JObject { ["text"] = text }));

            Assert.That(context.PlayerSession.StartedSessionIds, Is.Empty);
            Assert.That(context.PlayerSession.StoppedSessions, Is.Empty);
            Assert.That(context.PlayerSession.TypedTranscriptions, Is.Empty);
            Assert.That(context.PlayerSession.Transcriptions, Has.Count.EqualTo(1));
            Assert.That(context.PlayerSession.Transcriptions[0].Phase, Is.EqualTo(TranscriptionPhase.ProcessedFinal));
            Assert.That(context.PlayerSession.Transcriptions[0].Text == (text ?? string.Empty), Is.True,
                "The orphaned final must still be delivered without starting a speaking session.");
            TestLogRecord[] logs = context.Logger.Entries.Where(entry => entry.Category == LogCategory.Player).ToArray();
            bool enabled = level == LogLevel.Debug && !disablePlayer;
            Assert.That(logs, Has.Length.EqualTo(enabled ? 2 : 0),
                "The logger retains disabled calls, so this checks caller-side guards too.");
            foreach (TestLogRecord log in logs)
            {
                Assert.That(log.Level, Is.EqualTo(LogLevel.Debug));
                StringAssert.Contains($"length={text?.Length ?? 0}", log.Message);
                if (!string.IsNullOrEmpty(text))
                    Assert.That(log.Message.Contains(text), Is.False, "Transcript content must not reach diagnostics.");
            }
        }

        [TestCase(LogLevel.Info)]
        [TestCase(LogLevel.Debug)]
        public void PlayerTranscription_LogsOnlyEnabledMetadataAndPreservesDelivery(LogLevel level)
        {
            const string interim = "<interim-placeholder>";
            const string asrFinal = "<asr-final-placeholder>";
            const string processed = "<processed-placeholder>";
            RtviTestContext context = CreateContext();
            context.Logger.Level = level;

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket(interim, isFinal: false));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket(asrFinal, isFinal: true));
            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject { ["text"] = processed }));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            CollectionAssert.AreEqual(
                new[]
                {
                    TranscriptionPhase.Listening,
                    TranscriptionPhase.Interim,
                    TranscriptionPhase.AsrFinal,
                    TranscriptionPhase.ProcessedFinal,
                    TranscriptionPhase.Completed
                },
                context.PlayerSession.Transcriptions.Select(entry => entry.Phase).ToArray(),
                "Player transcript delivery must be unchanged.");
            TestLogRecord[] playerLogs = context.Logger.Entries
                .Where(entry => entry.Category == LogCategory.Player).ToArray();
            foreach (TestLogRecord log in playerLogs)
            {
                Assert.That(log.Message.Contains(interim), Is.False, "Player transcript text must never reach diagnostics.");
                Assert.That(log.Message.Contains(asrFinal), Is.False, "Player transcript text must never reach diagnostics.");
                Assert.That(log.Message.Contains(processed), Is.False, "Player transcript text must never reach diagnostics.");
            }

            TestLogRecord[] transcriptionLogs = playerLogs
                .Where(entry => entry.Message.Contains("player transcription")).ToArray();
            Assert.That(transcriptionLogs.Length, Is.EqualTo(level == LogLevel.Debug ? 3 : 0),
                "The recording logger retains disabled calls too; Info must never call a transcript log sink.");
            foreach (TestLogRecord log in transcriptionLogs)
            {
                Assert.That(log.Level, Is.EqualTo(LogLevel.Debug));
                StringAssert.Contains("length=", log.Message);
            }
        }

        [Test]
        public void PlayerSpeech_OrdinaryCycle_UsesNormalTranscriptionPath()
        {
            RtviTestContext context = CreateContext();

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("working", isFinal: false));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("worked", isFinal: true));
            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject { ["text"] = "Worked" }));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.AreEqual(1, context.PlayerSession.StartedSessionIds.Count);
            Assert.AreEqual(1, context.PlayerSession.StoppedSessions.Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    TranscriptionPhase.Listening,
                    TranscriptionPhase.Interim,
                    TranscriptionPhase.AsrFinal,
                    TranscriptionPhase.ProcessedFinal,
                    TranscriptionPhase.Completed
                },
                context.PlayerSession.Transcriptions.Select(entry => entry.Phase).ToArray());
        }

        [Test]
        public void TypedText_MultiplePendingMessages_UsesNewestMessage()
        {
            RtviTestContext context = CreateContext();

            context.Handler.SendData(new RTVIUserTextMessage("first", "typed-1"));
            context.Handler.SendData(new RTVIUserTextMessage("second", "typed-2"));
            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject { ["text"] = "second" }));

            Assert.AreEqual(1, context.PlayerSession.TypedTranscriptions.Count);
            Assert.AreEqual("typed-2", context.PlayerSession.TypedTranscriptions[0].MessageId);
            Assert.AreEqual("second", context.PlayerSession.TypedTranscriptions[0].Text);
        }

        [Test]
        public void PlayerSpeech_DuplicateStart_DoesNotStartSecondSession()
        {
            RtviTestContext context = CreateContext();

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));

            Assert.AreEqual(1, context.PlayerSession.StartedSessionIds.Count);
            Assert.IsTrue(context.Logger.Contains("duplicate user-started-speaking"));
        }

        [Test]
        public void PlayerSpeech_OrphanStopWithoutSession_IsIgnored()
        {
            RtviTestContext context = CreateContext();
            int stoppedEvents = 0;
            context.EventHub.Subscribe<PlayerSpeakingStateChanged>(evt =>
            {
                if (!evt.IsSpeaking) stoppedEvents++;
            });

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.AreEqual(0, stoppedEvents);
            Assert.IsEmpty(context.PlayerSession.StoppedSessions);
            Assert.IsTrue(context.Logger.Contains("without an active speech session"));
        }

        [Test]
        public void PlayerSpeech_InterimAfterPause_SynthesizesMatchingStartBeforeNextStop()
        {
            RtviTestContext context = CreateContext();
            int startedEvents = 0;
            int stoppedEvents = 0;
            context.EventHub.Subscribe<PlayerSpeakingStateChanged>(evt =>
            {
                if (evt.IsSpeaking)
                    startedEvents++;
                else
                    stoppedEvents++;
            });

            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("first phrase", isFinal: false));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));
            context.Gateway.ProcessIncoming(CreateUserTranscriptionPacket("second phrase", isFinal: false));
            context.Gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.AreEqual(2, startedEvents);
            Assert.AreEqual(2, stoppedEvents);
            Assert.IsFalse(context.Logger.Contains("without a preceding user-started-speaking"));
        }
    }
}
