using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using NUnit.Framework;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;
using LogCategory = Convai.Domain.Logging.LogCategory;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RemoteTrackSessionNotificationSupportTests
    {
        [Test]
        public void NotifyAudioTrackSubscribed_InvokesListenerAndLogsSuccess()
        {
            TestLogger logger = new();
            TestRemoteAudioTrack audioTrack = new();
            IRemoteAudioTrack notifiedTrack = null;
            string notifiedParticipantSid = null;
            string notifiedCharacterId = null;

            RemoteTrackSessionNotificationSupport.NotifyAudioTrackSubscribed(
                (track, participantSid, characterId) =>
                {
                    notifiedTrack = track;
                    notifiedParticipantSid = participantSid;
                    notifiedCharacterId = characterId;
                },
                audioTrack,
                "participant-1",
                "char-1",
                logger,
                "TestPublisher");

            Assert.That(notifiedTrack, Is.SameAs(audioTrack));
            Assert.That(notifiedParticipantSid, Is.EqualTo("participant-1"));
            Assert.That(notifiedCharacterId, Is.EqualTo("char-1"));
            Assert.That(logger.DebugMessages,
                Has.Some.Contains("Notified OnRemoteAudioTrackSubscribed: participant-1 (char-1)"));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Audio));
        }

        [Test]
        public void NotifyAudioTrackUnsubscribed_InvokesListenerAndLogsSuccess()
        {
            TestLogger logger = new();
            string notifiedParticipantSid = null;
            string notifiedCharacterId = null;

            RemoteTrackSessionNotificationSupport.NotifyAudioTrackUnsubscribed(
                (participantSid, characterId) =>
                {
                    notifiedParticipantSid = participantSid;
                    notifiedCharacterId = characterId;
                },
                "participant-2",
                "char-2",
                logger,
                "TestPublisher");

            Assert.That(notifiedParticipantSid, Is.EqualTo("participant-2"));
            Assert.That(notifiedCharacterId, Is.EqualTo("char-2"));
            Assert.That(logger.DebugMessages,
                Has.Some.Contains("Notified OnRemoteAudioTrackUnsubscribed: participant-2 (char-2)"));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Audio));
        }

        [Test]
        public void NotifyAudioTrackSubscribed_WhenTrackMissing_LogsNoOp()
        {
            TestLogger logger = new();

            RemoteTrackSessionNotificationSupport.NotifyAudioTrackSubscribed(
                (_, _, _) => Assert.Fail("Listener should not be invoked when track is unavailable."),
                null,
                "participant-3",
                "char-3",
                logger,
                "TestPublisher");

            Assert.That(logger.DebugMessages,
                Has.Some.Contains(
                    "Remote audio track unavailable; skipping OnRemoteAudioTrackSubscribed notification for participant: participant-3."));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Audio));
        }

        [Test]
        public void NotifyAudioTrackUnsubscribed_WhenNoListenersRegistered_LogsNoOp()
        {
            TestLogger logger = new();

            RemoteTrackSessionNotificationSupport.NotifyAudioTrackUnsubscribed(
                null,
                "participant-4",
                "char-4",
                logger,
                "TestPublisher");

            Assert.That(logger.DebugMessages,
                Has.Some.Contains(
                    "No listeners registered; skipping OnRemoteAudioTrackUnsubscribed notification."));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Audio));
        }

        [Test]
        public void NotifyAudioTrackSubscribed_WhenDispatchReturnsFalse_LogsFailure()
        {
            TestLogger logger = new();

            RemoteTrackSessionNotificationSupport.NotifyAudioTrackSubscribed(
                (_, _, _) => Assert.Fail("Listener should not run when dispatch fails."),
                new TestRemoteAudioTrack(),
                "participant-5",
                "char-5",
                logger,
                "TestPublisher",
                _ => false);

            Assert.That(logger.DebugMessages,
                Has.Some.Contains("Failed to dispatch OnRemoteAudioTrackSubscribed notification."));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Audio));
        }

        private sealed class TestRemoteAudioTrack : IRemoteAudioTrack
        {
            public string Sid => "track-1";
            public string Name => "Remote Audio";
            public TrackKind Kind => TrackKind.Audio;
            public bool IsMuted => false;
            public IRemoteParticipant Participant => null;
            public bool IsSubscribed => true;
            public bool IsAttached => false;
#pragma warning disable CS0067
            public event Action<bool> MuteChanged;
#pragma warning restore CS0067
            public IAudioStream CreateAudioStream() => null;
            public void AttachToAudioSource(AudioSource audioSource) { }
            public void Detach() { }
        }

        private sealed class TestLogger : ILogger
        {
            public List<LogEntry> Entries { get; } = new();
            public void Log(in Convai.Domain.Logging.LogEntry entry)
            {
                Entries.Add(entry);
                if (entry.HasException) { Error(entry.Exception, entry.Message, entry.Context, entry.Category); return; }
                switch (entry.Level)
                {
                    case Convai.Domain.Logging.LogLevel.Debug:
                    case Convai.Domain.Logging.LogLevel.Trace: Debug(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Info: Info(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Warning: Warning(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Error: Error(entry.Message, entry.Context, entry.Category); break;
                }
            }
            public List<string> DebugMessages { get; } = new();
            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);

            public void Info(string message, LogCategory category = LogCategory.SDK) { }

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Warning(string message, LogCategory category = LogCategory.SDK) { }

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(string message, LogCategory category = LogCategory.SDK) { }

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) { }

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public bool IsEnabled(LogLevel level, LogCategory category) => true;
        }
    }
}
