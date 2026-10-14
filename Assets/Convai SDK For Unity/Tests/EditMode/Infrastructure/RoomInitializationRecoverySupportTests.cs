using System;
using System.Collections.Generic;
using Convai.Domain.Abstractions;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.RestAPI.Services;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RoomInitializationRecoverySupportTests
    {
        [Test]
        public void FromAccepted_WithResumeEnabledAndResolvedCharacterSession_PersistsStoredSession()
        {
            RoomInitializationOutcome outcome = RoomInitializationRecoverySupport.FromAccepted(
                "stored-session",
                enableSessionResume: true,
                resolvedCharacterSessionId: "fresh-session",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionAllowed);

            Assert.That(outcome.Status, Is.EqualTo(RoomInitializationOutcomeStatus.RoomDetailsAccepted));
            Assert.That(outcome.AttemptedWithStoredSession, Is.True);
            Assert.That(outcome.ShouldPersistStoredSession, Is.True);
            Assert.That(outcome.ShouldClearStoredSession, Is.False);
            Assert.That(outcome.ShouldRetryWithoutStoredSession, Is.False);
            Assert.That(outcome.DiagnosticsMetadata, Does.Contain("status=RoomDetailsAccepted"));
            Assert.That(outcome.DiagnosticsMetadata, Does.Contain("shouldPersistStoredSession=True"));
        }

        [Test]
        public void FromFailure_WithInvalidStoredSessionAndNativePolicy_ClearsAndAllowsRetry()
        {
            RoomInitializationOutcome outcome = RoomInitializationRecoverySupport.FromFailure(
                "stored-session",
                enableSessionResume: true,
                failureMessage: "Invalid character_session_id for request",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionAllowed);

            Assert.That(outcome.Status, Is.EqualTo(RoomInitializationOutcomeStatus.InvalidStoredSession));
            Assert.That(outcome.ShouldClearStoredSession, Is.True);
            Assert.That(outcome.ShouldRetryWithoutStoredSession, Is.True);
            Assert.That(outcome.DiagnosticsMetadata, Does.Contain("retryWithoutStoredSessionAllowed=True"));
        }

        [Test]
        public void FromFailure_WithInvalidStoredSessionAndWebGLPolicy_DoesNotClearAndDisallowsRetry()
        {
            RoomInitializationOutcome outcome = RoomInitializationRecoverySupport.FromFailure(
                "stored-session",
                enableSessionResume: true,
                failureMessage: "Invalid character_session_id for request",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed);

            Assert.That(outcome.Status, Is.EqualTo(RoomInitializationOutcomeStatus.InvalidStoredSession));
            Assert.That(outcome.ShouldClearStoredSession, Is.False);
            Assert.That(outcome.ShouldRetryWithoutStoredSession, Is.False);
            Assert.That(outcome.DiagnosticsMetadata, Does.Contain("retryWithoutStoredSessionAllowed=False"));
            Assert.That(outcome.DiagnosticsMetadata, Does.Contain("shouldClearStoredSession=False"));
        }

        [Test]
        public void TryPersistCharacterSession_WhenOutcomeAllowsPersistence_StoresConfiguredSession()
        {
            var sessionPersistence = new TestConfigurationProvider();
            var logger = new TestLogger();
            RoomInitializationOutcome outcome = RoomInitializationRecoverySupport.FromAccepted(
                "stored-session",
                enableSessionResume: true,
                resolvedCharacterSessionId: "fresh-session",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionAllowed);

            bool persisted = RoomInitializationRecoverySupport.TryPersistCharacterSession(
                sessionPersistence,
                logger,
                "char-123",
                "fresh-session",
                outcome,
                "[NativeRoomController]");

            Assert.That(persisted, Is.True);
            Assert.That(sessionPersistence.StoredCharacterId, Is.EqualTo("char-123"));
            Assert.That(sessionPersistence.StoredSessionId, Is.EqualTo("fresh-session"));
            Assert.That(logger.DebugMessages,
                Has.Some.Contains("[NativeRoomController] Stored character session ID for character char-123: fresh-session"));
        }

        [Test]
        public void TryClearStoredSessionForRecovery_WhenInvalidSessionDisallowsRetry_DoesNotClearStoredSession()
        {
            var sessionPersistence = new TestConfigurationProvider();
            var logger = new TestLogger();
            RoomConnectionRequest roomRequest = new() { CharacterSessionId = "stored-session" };
            RoomInitializationOutcome outcome = RoomInitializationRecoverySupport.FromFailure(
                roomRequest.CharacterSessionId,
                enableSessionResume: true,
                failureMessage: "Invalid character_session_id for request",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed);

            bool cleared = RoomInitializationRecoverySupport.TryClearStoredSessionForRecovery(
                sessionPersistence,
                logger,
                "char-123",
                roomRequest,
                outcome,
                "[WebGLRoomController]");

            Assert.That(cleared, Is.False);
            Assert.That(sessionPersistence.ClearedCharacterId, Is.Null);
            Assert.That(roomRequest.CharacterSessionId, Is.EqualTo("stored-session"));
            Assert.That(logger.WarningMessages, Is.Empty);
        }

        private sealed class TestConfigurationProvider : IConfigurationProvider, ISessionPersistence
        {
            public string ApiKey => "api-key";
            public string CoreServerUrl => "https://core.convai.com";
            public ConvaiConnectionType ConnectionType => ConvaiConnectionType.Audio;
            public string VideoTrackName => "camera-main";
            public ConvaiServerEndpoint ServerEndpoint => ConvaiServerEndpoint.Connect;
            public LipSyncTransportOptions LipSyncTransportOptions => LipSyncTransportOptions.Disabled;
            public string EndUserId => "user-1";
            public IReadOnlyDictionary<string, object> EndUserMetadata => null;
            public bool Debug => false;
            public string StoredCharacterId { get; private set; }
            public string StoredSessionId { get; private set; }
            public string ClearedCharacterId { get; private set; }

            public void StoreCharacterSessionId(string characterId, string sessionId)
            {
                StoredCharacterId = characterId;
                StoredSessionId = sessionId;
            }

            public string GetCharacterSessionId(string characterId) => null;

            public void ClearCharacterSessionId(string characterId)
            {
                ClearedCharacterId = characterId;
            }

            public void ClearAllCharacterSessionIds()
            {
            }

            public string LoadSession(string characterId) => GetCharacterSessionId(characterId);
            public void SaveSession(string characterId, string sessionId) => StoreCharacterSessionId(characterId, sessionId);
            public void ClearSession(string characterId) => ClearCharacterSessionId(characterId);
            public void ClearAllSessions() => ClearAllCharacterSessionIds();
            public bool HasSession(string characterId) => !string.IsNullOrEmpty(GetCharacterSessionId(characterId));
        }

        private sealed class TestLogger : ILogger
        {
            public void Log(in Convai.Domain.Logging.LogEntry entry)
            {
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
            public List<string> WarningMessages { get; } = new();

            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK)
            {
            }

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
                DebugMessages.Add(message);
            }

            public void Info(string message, LogCategory category = LogCategory.SDK)
            {
            }

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Warning(string message, LogCategory category = LogCategory.SDK)
            {
                WarningMessages.Add(message);
            }

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
                WarningMessages.Add(message);
            }

            public void Error(string message, LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, LogCategory category = LogCategory.SDK)
            {
                DebugMessages.Add(message);
            }

            public bool IsEnabled(LogLevel level, LogCategory category) => true;
        }
    }
}
