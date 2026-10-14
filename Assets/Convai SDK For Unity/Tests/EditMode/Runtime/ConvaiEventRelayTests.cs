using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Runtime.Components;
using Convai.Runtime.Core;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Runtime.Facades;
using Convai.Runtime.Presentation.Events;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using ILogger = Convai.Domain.Logging.ILogger;
using System.Reflection;
using Convai.Application.Services.Transcript;

namespace Convai.Tests.EditMode
{
    public class ConvaiEventRelayTests
    {
        private readonly List<GameObject> _createdObjects = new();

        private static void InvokePrivateLifecycle(Component component, string methodName)
        {
            component.GetType()
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(component, null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _createdObjects.Clear();
        }

        [Test]
        public void SessionRelay_Publishes_State_Error_And_Lifecycle_Events_Once()
        {
            EventHub eventHub = CreateEventHub();
            using var eventsFacade = new ConvaiEvents(eventHub);
            ConvaiSessionEventRelay relay = CreateRelay<ConvaiSessionEventRelay>("SessionRelay");
            relay.BindForTesting(eventsFacade);

            int connectedCount = 0;
            int reconnectingCount = 0;
            int reconnectedCount = 0;
            int disconnectedCount = 0;
            int errorCount = 0;
            int usageLimitCount = 0;
            SessionStateChangedRelayData lastState = null;

            relay.OnConnected.AddListener(() => connectedCount++);
            relay.OnReconnecting.AddListener(() => reconnectingCount++);
            relay.OnReconnected.AddListener(() => reconnectedCount++);
            relay.OnDisconnected.AddListener(() => disconnectedCount++);
            relay.OnUsageLimitReached.AddListener(() => usageLimitCount++);
            relay.OnSessionError.AddListener(_ => errorCount++);
            relay.OnSessionStateChanged.AddListener(data => lastState = data);

            eventHub.Publish(SessionStateChanged.Create(SessionState.Connecting, SessionState.Connected, "session-1"));
            eventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Reconnecting, "session-1"));
            eventHub.Publish(SessionStateChanged.Create(SessionState.Reconnecting, SessionState.Connected, "session-1"));
            eventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));
            eventHub.Publish(SessionError.Create("connection.timeout", "Timed out", "session-1"));
            eventHub.Publish(UsageLimitReached.Create("daily", "Daily usage exhausted"));

            Assert.AreEqual(1, connectedCount);
            Assert.AreEqual(1, reconnectingCount);
            Assert.AreEqual(1, reconnectedCount);
            Assert.AreEqual(1, disconnectedCount);
            Assert.AreEqual(1, errorCount);
            Assert.AreEqual(1, usageLimitCount);
            Assert.NotNull(lastState);
            Assert.AreEqual(SessionState.Disconnected, lastState.NewState);

            relay.enabled = false;
            InvokePrivateLifecycle(relay, "OnDisable");
            eventHub.Publish(SessionStateChanged.Create(SessionState.Connecting, SessionState.Connected, "session-2"));

            Assert.AreEqual(1, connectedCount, "Disabled relay should unsubscribe from the facade.");
        }

        [Test]
        public void SessionRelay_Publishes_Idle_And_Background_Payloads()
        {
            EventHub eventHub = CreateEventHub();
            using var eventsFacade = new ConvaiEvents(eventHub);
            ConvaiSessionEventRelay relay = CreateRelay<ConvaiSessionEventRelay>("SessionLifecycleRelay");
            relay.BindForTesting(eventsFacade);

            UserIdleWarningRelayData warning = null;
            UserIdleTimeoutRelayData timeout = null;
            RuntimeBackgroundStateRelayData background = null;
            relay.OnUserIdleWarning.AddListener(data => warning = data);
            relay.OnUserIdleTimeout.AddListener(data => timeout = data);
            relay.OnRuntimeBackgroundStateChanged.AddListener(data => background = data);

            DateTime warningAt = new(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
            eventHub.Publish(new UserIdleWarningReceived(120, "Idle", warningAt));
            eventHub.Publish(new UserIdleTimeoutElapsed(
                warningAt,
                warningAt.AddSeconds(120),
                warningAt.AddSeconds(121)));
            eventHub.Publish(RuntimeBackgroundStateChanged.Create(
                true,
                RuntimeBackgroundPolicy.PauseTimeline,
                RuntimeBackgroundPolicy.MuteButCatchUp,
                RuntimePauseReason.ApplicationBackground));

            Assert.NotNull(warning);
            Assert.AreEqual(120, warning.RemainingSeconds);
            Assert.NotNull(timeout);
            Assert.AreEqual(warningAt.AddSeconds(120).ToString("O"), timeout.DeadlineUtc);
            Assert.NotNull(background);
            Assert.IsTrue(background.UsedPlatformFallback);
            Assert.AreEqual(RuntimeBackgroundPolicy.MuteButCatchUp, background.EffectivePolicy);
        }

        [Test]
        public void TranscriptRelay_Applies_FinalOnly_And_Character_Filter()
        {
            EventHub eventHub = CreateEventHub();
            using var transcriptEngine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(transcriptEngine);
            ConvaiTranscriptEventRelay relay = CreateRelay<ConvaiTranscriptEventRelay>("TranscriptRelay");
            relay.ConfigureForTesting(finalOnly: true, ignoreInterimUpdates: true, characterIdFilter: "npc-1");
            relay.BindForTesting(transcripts);

            int characterCount = 0;
            int transcriptUpdateCount = 0;
            int finalCharacterCount = 0;
            int playerCount = 0;
            int finalPlayerCount = 0;
            CharacterTranscriptRelayData lastCharacter = null;
            PlayerTranscriptRelayData lastPlayer = null;
            TranscriptUpdateRelayData lastUpdate = null;

            relay.OnTranscriptReceived.AddListener(data =>
            {
                transcriptUpdateCount++;
                lastUpdate = data;
            });
            relay.OnCharacterTranscriptReceived.AddListener(data =>
            {
                characterCount++;
                lastCharacter = data;
            });
            relay.OnFinalCharacterTranscriptReceived.AddListener(_ => finalCharacterCount++);
            relay.OnPlayerTranscriptReceived.AddListener(data =>
            {
                playerCount++;
                lastPlayer = data;
            });
            relay.OnFinalPlayerTranscriptReceived.AddListener(_ => finalPlayerCount++);

            // The interim and its final carry one response id, because that is the only thing the
            // transport publishes: RTVIHandler resolves every character transcript through
            // CharacterResponseState.ResolveTranscriptIdentity, which keeps one sticky id per
            // participant for the whole response. An interim published without one is a different
            // response as far as the engine is concerned, and it is right to commit the turn before
            // it — see RoomTranscriptEngineTests.Character_New_ResponseId_Starts_Fresh_Turn_For_
            // Same_Character. This fixture wants the ordinary case: one utterance, streamed then
            // finalised.
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "npc-1", "Camila", "Interim", false, responseId: "response-1"));
            eventHub.Publish(CharacterTranscriptReceived.Create("npc-2", "Other", "Filtered", true));
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "npc-1",
                "Camila",
                "Final line",
                true,
                responseId: "response-1"));
            eventHub.Publish(CharacterTurnCompleted.Create("npc-1", string.Empty, false));

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1",
                "You",
                "Streaming",
                false,
                turnId: "turn-1",
                messageId: "turn-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1",
                "You",
                "Stable",
                false,
                TranscriptionPhase.AsrFinal,
                turnId: "turn-1",
                messageId: "turn-1"));
            eventHub.Publish(PlayerSpeakingStateChanged.StoppedSpeaking("turn-1"));

            // The VAD stop alone no longer ends the player's turn — it delimits an acoustic burst,
            // and the backend can continue one utterance after a short pause, so the engine waits for
            // an authoritative boundary (see RoomTranscriptEngine.OnEvent(PlayerSpeakingStateChanged)).
            // The processed final is that boundary, and it is what commits the turn the relay reports.
            eventHub.Publish(FinalUserTranscriptionReceived.Create(
                "Stable",
                new SpeakerInfo("player-1", "You", string.Empty),
                "turn-1"));

            Assert.AreEqual(1, characterCount);
            Assert.AreEqual(1, finalCharacterCount);
            Assert.NotNull(lastCharacter);
            Assert.AreEqual("npc-1", lastCharacter.CharacterId);
            Assert.AreEqual("Final line", lastCharacter.Text);
            Assert.AreEqual("response-1", lastCharacter.MessageId);

            Assert.AreEqual(1, playerCount);
            Assert.AreEqual(1, finalPlayerCount);
            Assert.NotNull(lastPlayer);
            Assert.AreEqual("turn-1", lastPlayer.TurnId);
            Assert.AreEqual("Stable", lastPlayer.Text);
            Assert.AreEqual(2, transcriptUpdateCount);
            Assert.NotNull(lastUpdate);
            Assert.AreEqual("turn-1", lastUpdate.MessageId);
            Assert.AreEqual(Convai.Domain.Models.SpeakerType.Player, lastUpdate.SpeakerType);
        }

        [Test]
        public void TranscriptRelay_RebindsWithoutReceivingOldFacadeEvents()
        {
            EventHub firstHub = CreateEventHub();
            EventHub secondHub = CreateEventHub();
            using var firstEngine = new RoomTranscriptEngine(firstHub);
            using var secondEngine = new RoomTranscriptEngine(secondHub);
            using var firstTranscripts = new ConvaiTranscripts(firstEngine);
            using var secondTranscripts = new ConvaiTranscripts(secondEngine);
            ConvaiTranscriptEventRelay relay = CreateRelay<ConvaiTranscriptEventRelay>("TranscriptRelayRebind");
            relay.ConfigureForTesting(finalOnly: false, ignoreInterimUpdates: false);

            int count = 0;
            relay.OnTranscriptReceived.AddListener(_ => count++);
            relay.BindForTesting(firstTranscripts);
            firstHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "first", false,
                turnId: "turn-1", messageId: "turn-1"));

            relay.BindForTesting(secondTranscripts);
            firstHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "old", false,
                turnId: "turn-1", messageId: "turn-1"));
            secondHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "second", false,
                turnId: "turn-2", messageId: "turn-2"));

            Assert.AreEqual(2, count);
        }

        [Test]
        public void TranscriptRelay_RespectsPresentationSettingWithoutStoppingHistory()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            ConvaiTranscriptEventRelay relay = CreateRelay<ConvaiTranscriptEventRelay>("TranscriptRelaySetting");
            relay.ConfigureForTesting(finalOnly: false, ignoreInterimUpdates: false);

            int count = 0;
            relay.OnTranscriptReceived.AddListener(_ => count++);
            transcripts.SetPresentationEnabled(false);
            relay.BindForTesting(transcripts);

            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText(
                "player-1", "You", "hidden", "typed-hidden"));

            Assert.AreEqual(0, count);
            Assert.AreEqual(1, transcripts.CurrentTimeline.CommittedTurns.Count);

            transcripts.SetPresentationEnabled(true);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText(
                "player-1", "You", "visible", "typed-visible"));

            Assert.AreEqual(1, count);
            Assert.AreEqual(2, transcripts.CurrentTimeline.CommittedTurns.Count);
        }

        [Test]
        public void CharacterRelay_Forwards_Local_Character_Callbacks()
        {
            EventHub eventHub = CreateEventHub();
            var connectionService = new MockRoomConnectionService();
            var audioService = new MockRoomAudioService();
            var agentRegistry = new MockAgentRegistry();
            var logger = new TestLogger();

            var go = new GameObject("RelayCharacter");
            _createdObjects.Add(go);

            ConvaiCharacter character = go.AddComponent<ConvaiCharacter>();
            character.Configure("npc-1", "Camila");
            character.InjectDependencies(new ConvaiCharacterDependencies(
                eventHub,
                connectionService,
                audioService,
                agentRegistry,
                logger));

            agentRegistry.SetParticipantId("npc-1", "participant-1");

            ConvaiCharacterEventRelay relay = go.AddComponent<ConvaiCharacterEventRelay>();
            relay.BindForTesting(character);

            int speechStartedCount = 0;
            int speechStoppedCount = 0;
            int readyCount = 0;
            CharacterTranscriptRelayData transcript = null;
            CharacterEmotionRelayData emotion = null;
            CharacterTurnCompletedRelayData turn = null;

            relay.OnSpeechStarted.AddListener(() => speechStartedCount++);
            relay.OnSpeechStopped.AddListener(() => speechStoppedCount++);
            relay.OnCharacterReady.AddListener(() => readyCount++);
            relay.OnTranscriptReceived.AddListener(data => transcript = data);
            relay.OnEmotionChanged.AddListener(data => emotion = data);
            relay.OnTurnCompleted.AddListener(data => turn = data);

            eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("npc-1"));
            // The character raises OnTranscriptReceived from the transcript feed, not from
            // bot-tts-text — a wire the service never sends, so the old event proved nothing.
            eventHub.Publish(CharacterTranscriptReceived.Create("npc-1", "Camila", "Hello there", true));
            eventHub.Publish(CharacterEmotionChanged.Create("npc-1", "joy", 3));
            eventHub.Publish(CharacterTurnCompleted.Create("npc-1", "participant-1", true));
            eventHub.Publish(CharacterReady.Create("npc-1", "participant-1"));
            eventHub.Publish(CharacterSpeechStateChanged.StoppedSpeaking("npc-1"));

            Assert.AreEqual(1, speechStartedCount);
            Assert.AreEqual(1, speechStoppedCount);
            Assert.AreEqual(1, readyCount);
            Assert.NotNull(transcript);
            Assert.AreEqual("Camila", transcript.CharacterName);
            Assert.AreEqual("Hello there", transcript.Text);
            Assert.NotNull(emotion);
            Assert.AreEqual("joy", emotion.Emotion);
            Assert.NotNull(turn);
            Assert.IsTrue(turn.WasInterrupted);

            relay.enabled = false;
            InvokePrivateLifecycle(relay, "OnDisable");
            eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("npc-1"));

            Assert.AreEqual(1, speechStartedCount, "Disabled relay should unsubscribe from ConvaiCharacter callbacks.");
        }

        [Test]
        public void RelayEditors_Have_Actionable_Configuration_Warnings()
        {
            ConvaiSessionEventRelay sessionRelay = CreateRelay<ConvaiSessionEventRelay>("SessionRelayWarning");
            ConvaiTranscriptEventRelay transcriptRelay = CreateRelay<ConvaiTranscriptEventRelay>("TranscriptRelayWarning");
            ConvaiCharacterEventRelay characterRelay = CreateRelay<ConvaiCharacterEventRelay>("CharacterRelayWarning");

            Assert.IsNotEmpty(sessionRelay.GetConfigurationWarning());
            Assert.IsNotEmpty(transcriptRelay.GetConfigurationWarning());
            Assert.IsNotEmpty(characterRelay.GetConfigurationWarning());
        }

        private T CreateRelay<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _createdObjects.Add(go);
            return go.AddComponent<T>();
        }

        private static EventHub CreateEventHub() => new(new ImmediateScheduler());

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
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
            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }

            public void Log(
                LogLevel level,
                string message,
                IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, LogCategory category = LogCategory.SDK) { }

            public void Debug(
                string message,
                IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Info(string message, LogCategory category = LogCategory.SDK) { }

            public void Info(
                string message,
                IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Warning(string message, LogCategory category = LogCategory.SDK) { }

            public void Warning(
                string message,
                IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(string message, LogCategory category = LogCategory.SDK) { }

            public void Error(
                string message,
                IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) { }

            public void Error(
                Exception exception,
                string message,
                IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public bool IsEnabled(LogLevel level, LogCategory category) => true;
        }
    }
}
