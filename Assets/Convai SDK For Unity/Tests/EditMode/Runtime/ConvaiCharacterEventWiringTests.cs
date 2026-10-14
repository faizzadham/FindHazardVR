using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.Models;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ILogger = Convai.Domain.Logging.ILogger;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode
{
    /// <summary>
    ///     Tests for ConvaiCharacter event wiring and CharacterReady functionality.
    /// </summary>
    public class ConvaiCharacterEventWiringTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private MockRoomAudioService _audioService;
        private MockRoomConnectionService _connectionService;
        private EventHub _eventHub;
        private MockAgentRegistry _agentRegistry;
        private TestLogger _logger;

        [SetUp]
        public void SetUp()
        {
            _eventHub = new EventHub(new ImmediateScheduler());
            _connectionService = new MockRoomConnectionService();
            _audioService = new MockRoomAudioService();
            _agentRegistry = new MockAgentRegistry();
            _logger = new TestLogger();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _createdObjects.Clear();
            _eventHub = null;
        }

        private ConvaiCharacter CreateAndInjectCharacter(string characterId = "test-char-id",
            string characterName = "TestCharacter")
        {
            var go = new GameObject(characterName);
            _createdObjects.Add(go);

            var character = go.AddComponent<ConvaiCharacter>();
            character.Configure(characterId, characterName);
            character.InjectDependencies(new ConvaiCharacterDependencies(
                _eventHub,
                _connectionService,
                _audioService,
                _agentRegistry,
                _logger));

            return character;
        }

        [Test]
        public void ConvaiCharacter_ImplementsIConvaiCharacterAgent()
        {
            var go = new GameObject("TestCharacter");
            _createdObjects.Add(go);

            var character = go.AddComponent<ConvaiCharacter>();

            Assert.IsTrue(character is IConvaiCharacterAgent,
                "ConvaiCharacter should implement IConvaiCharacterAgent interface");
        }

        [Test]
        public void IsCharacterReady_DefaultsFalse()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();

            Assert.IsFalse(character.IsCharacterReady, "IsCharacterReady should default to false");
        }

        [Test]
        public void OnCharacterReady_IsRaisedWhenCharacterReadyEventPublished()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            bool eventRaised = false;
            character.OnCharacterReady += () => eventRaised = true;

            _eventHub.Publish(CharacterReady.Create("test-char-id", "participant-123"));

            Assert.IsTrue(eventRaised, "OnCharacterReady event should be raised");
            Assert.IsTrue(character.IsCharacterReady, "IsCharacterReady should be true after event");
        }

        [Test]
        public void OnCharacterReady_IsRaisedWhenParticipantBindingMatches()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            _agentRegistry.SetParticipantId("test-char-id", "participant-123");
            bool eventRaised = false;
            character.OnCharacterReady += () => eventRaised = true;

            _eventHub.Publish(CharacterReady.Create(string.Empty, "participant-123"));

            Assert.IsTrue(eventRaised, "OnCharacterReady should be raised when participant binding matches");
            Assert.IsTrue(character.IsCharacterReady, "IsCharacterReady should be true after participant-bound event");
        }

        [Test]
        public void OnCharacterReady_NotRaisedForDifferentCharacterId()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            bool eventRaised = false;
            character.OnCharacterReady += () => eventRaised = true;

            _eventHub.Publish(CharacterReady.Create("different-char-id", "participant-456"));

            Assert.IsFalse(eventRaised, "OnCharacterReady should not be raised for different character");
            Assert.IsFalse(character.IsCharacterReady, "IsCharacterReady should remain false");
        }

        [Test]
        public void IsCharacterReady_ResetOnDisconnect()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();

            _eventHub.Publish(CharacterReady.Create("test-char-id", "participant-123"));
            Assert.IsTrue(character.IsCharacterReady, "IsCharacterReady should be true after event");

            _connectionService.RaiseConnectionFailed();

            Assert.IsFalse(character.IsCharacterReady, "IsCharacterReady should be reset on disconnect");
        }

        [Test]
        public void OnTurnCompleted_IsRaisedWhenParticipantBindingMatches()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            _agentRegistry.SetParticipantId("test-char-id", "participant-123");
            bool eventRaised = false;
            character.OnTurnCompleted += _ => eventRaised = true;

            _eventHub.Publish(CharacterTurnCompleted.Create(string.Empty, "participant-123", false));

            Assert.IsTrue(eventRaised, "OnTurnCompleted should be raised when participant binding matches");
        }

        [Test]
        public void OnSpeechStarted_IsRaisedWhenSpeechStateUsesParticipantBinding()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            _agentRegistry.SetParticipantId("test-char-id", "participant-123");
            bool speechStarted = false;
            character.OnSpeechStarted += () => speechStarted = true;

            _eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("participant-123"));

            Assert.IsTrue(speechStarted, "OnSpeechStarted should be raised when speech state uses participant binding");
        }

        /// <summary>
        ///     The transcript event used to read a message the service never sends, so it never fired
        ///     at all. These hold it to the feed that does arrive, and to a truthful final flag — the
        ///     half of its contract the old wire could not answer even when it did carry text.
        /// </summary>
        [Test]
        public void OnTranscriptReceived_IsRaisedForAFinalTranscript()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            string received = null;
            bool? finalFlag = null;
            character.OnTranscriptReceived += (text, isFinal) =>
            {
                received = text;
                finalFlag = isFinal;
            };

            _eventHub.Publish(CharacterTranscriptReceived.Create(
                "test-char-id", "Test", "Good evening.", true));

            Assert.That(received, Is.EqualTo("Good evening."));
            Assert.That(finalFlag, Is.True,
                "A stable transcript has to arrive as final, or every final-gated consumer stays idle.");
        }

        [Test]
        public void OnTranscriptReceived_ReportsAStreamingTranscriptAsNotFinal()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            bool? finalFlag = null;
            character.OnTranscriptReceived += (_, isFinal) => finalFlag = isFinal;

            _eventHub.Publish(CharacterTranscriptReceived.Create(
                "test-char-id", "Test", "Good eve", false));

            Assert.That(finalFlag, Is.False);
        }

        [Test]
        public void OnTranscriptReceived_DoesNotRouteUnspokenBotOutputAsSpeech()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            bool raised = false;
            character.OnTranscriptReceived += (_, _) => raised = true;
            CharacterTranscriptReceived spokenShape = CharacterTranscriptReceived.Create(
                "test-char-id", "Test", "A quiet internal response.", true);

            _eventHub.Publish(new CharacterTranscriptReceived(
                spokenShape.Message,
                isSpoken: false));

            Assert.That(raised, Is.False,
                "The room transcript may retain quiet output, but a spoken-event consumer must not " +
                "animate or react as though the character said it aloud.");
        }

        [Test]
        public void OnTranscriptReceived_IsRaisedWhenParticipantBindingMatches()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            _agentRegistry.SetParticipantId("test-char-id", "participant-123");
            bool raised = false;
            character.OnTranscriptReceived += (_, _) => raised = true;

            // The transport stamps the participant on the packet; the character id can be absent.
            _eventHub.Publish(new CharacterTranscriptReceived(
                TranscriptMessage.Create(
                    string.Empty, "Test", "Good evening.", true,
                    participantId: "participant-123",
                    speakerType: SpeakerType.Character)));

            Assert.That(raised, Is.True);
        }

        [Test]
        public void OnTranscriptReceived_NotRaisedForAnotherCharacter()
        {
            ConvaiCharacter character = CreateAndInjectCharacter();
            bool raised = false;
            character.OnTranscriptReceived += (_, _) => raised = true;

            _eventHub.Publish(CharacterTranscriptReceived.Create(
                "different-char-id", "Other", "Not mine.", true));

            Assert.That(raised, Is.False,
                "A room with two characters must not deliver one character's line to the other.");
        }

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
            public List<string> DebugMessages { get; } = new();
            public List<string> WarningMessages { get; } = new();
            public List<string> ErrorMessages { get; } = new();

            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Debug(message, category);

            public void Info(string message, LogCategory category = LogCategory.SDK) { }

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Warning(string message, LogCategory category = LogCategory.SDK) => WarningMessages.Add(message);

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Warning(message, category);

            public void Error(string message, LogCategory category = LogCategory.SDK) => ErrorMessages.Add(message);

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Error(message, category);

            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) =>
                ErrorMessages.Add(message ?? exception.Message);

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Error(exception, message, category);

            public bool IsEnabled(LogLevel level, LogCategory category) => true;
        }

    }
}
