using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Emotion;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.DynamicContext;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class RoomCompositionServiceTests
    {
        [Test]
        public void ValidateAndComposeStartup_WhenNotInjected_DisablesManager()
        {
            var service = new RoomCompositionService();

            RoomCompositionStartupResult result = service.ValidateAndComposeStartup(
                new RoomCompositionContext(),
                new RoomCompositionState { IsInjected = false });

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(result.DisableManager);
            Assert.That(result.ErrorMessage, Does.Contain("dependencies were not injected"));
        }

        [Test]
        public void ValidateAndComposeStartup_WhenCredentialsMissing_ReturnsCredentialFailure()
        {
            var service = new RoomCompositionService();

            RoomCompositionStartupResult result = service.ValidateAndComposeStartup(
                new RoomCompositionContext
                {
                    CredentialProvider = new TestCredentialProvider(null, "https://core.convai.com")
                },
                new RoomCompositionState { IsInjected = true });

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(RoomStartupFailureReason.MissingRuntimeCredentials, result.FailureReason);
            Assert.AreEqual(SessionErrorCodes.ConfigApiKeyMissing, result.FailureErrorCode);
            Assert.AreEqual("Runtime credentials are not configured", result.FailureRecordMessage);
        }

        [Test]
        public void ValidateAndComposeStartup_WhenCharactersAreCreatedAtRuntime_DefersConnection()
        {
            var service = new RoomCompositionService();
            var player = new TestPlayerAgent();

            RoomCompositionStartupResult result = service.ValidateAndComposeStartup(
                new RoomCompositionContext
                {
                    AgentRegistry = new MockAgentRegistry(),
                    OwnershipProvider = new TestOwnershipProvider(
                        new RoomOwnershipSnapshot(
                            player,
                            Array.Empty<IConvaiCharacterAgent>(),
                            null)),
                    CredentialProvider = new TestCredentialProvider("api-key", "https://core.convai.com"),
                    PostToMainThread = _ => true,
                    ConnectOnStart = true
                },
                new RoomCompositionState { IsInjected = true });

            Assert.IsTrue(result.IsValid);
            Assert.IsFalse(result.ShouldAutoConnect);
            Assert.AreEqual(RoomStartupFailureReason.MissingCharacters, result.FailureReason);
            Assert.IsNull(result.Artifacts);
            Assert.IsNull(result.FailureErrorCode);
        }

        [Test]
        public void ValidateAndComposeStartup_WhenAuthTokenConfiguredButUnresolved_PassesCredentialGates()
        {
            var service = new RoomCompositionService();
            var player = new TestPlayerAgent();

            RoomCompositionStartupResult result = service.ValidateAndComposeStartup(
                new RoomCompositionContext
                {
                    AgentRegistry = new MockAgentRegistry(),
                    OwnershipProvider = new TestOwnershipProvider(
                        new RoomOwnershipSnapshot(
                            player,
                            Array.Empty<IConvaiCharacterAgent>(),
                            null)),
                    CredentialProvider = new TestCredentialProvider(
                        null,
                        "https://core.convai.com",
                        hasValidCredentials: true),
                    PostToMainThread = _ => true,
                    ConnectOnStart = true
                },
                new RoomCompositionState { IsInjected = true });

            Assert.IsTrue(result.IsValid,
                "An obtainable async credential must pass startup before its token has been resolved.");
            Assert.IsFalse(result.ShouldAutoConnect);
            Assert.AreEqual(RoomStartupFailureReason.MissingCharacters, result.FailureReason);
            Assert.IsNull(result.FailureErrorCode);
        }

        [Test]
        public void ValidateAndComposeStartup_WhenExplicitTokenPathIsAvailable_ComposesWithoutAutoConnect()
        {
            var service = new RoomCompositionService();
            var player = new TestPlayerAgent();
            var character = new TestCharacterAgent("char-explicit");

            RoomCompositionStartupResult result = service.ValidateAndComposeStartup(
                new RoomCompositionContext
                {
                    AgentRegistry = new MockAgentRegistry(),
                    OwnershipProvider = new TestOwnershipProvider(
                        new RoomOwnershipSnapshot(
                            player,
                            new[] { character },
                            new ActiveConversationTarget(character))),
                    CredentialProvider = new ExplicitTokenCredentialProvider(),
                    ControllerFactory = new MockRoomControllerFactory(),
                    CurrentRoomProvider = () => null,
                    EventHub = new EventHub(new ImmediateScheduler()),
                    PostToMainThread = _ => true,
                    ConnectOnStart = true
                },
                new RoomCompositionState { IsInjected = true });

            Assert.IsTrue(result.IsValid,
                "The room must initialize so a developer can call ConnectWithAuthTokenAsync later.");
            Assert.IsFalse(result.ShouldAutoConnect,
                "Auto-connect cannot run until the developer supplies the one-shot token.");
            Assert.AreEqual(RoomStartupFailureReason.None, result.FailureReason);
            Assert.IsNotNull(result.Artifacts);
        }

        [Test]
        public void ValidateAndComposeStartup_WhenAuthTokenProviderMissing_ReturnsProviderMissingCode()
        {
            var service = new RoomCompositionService();

            RoomCompositionStartupResult result = service.ValidateAndComposeStartup(
                new RoomCompositionContext
                {
                    CredentialProvider = new TestCredentialProvider(
                        null,
                        "https://core.convai.com",
                        hasValidCredentials: false,
                        configurationErrorCode: SessionErrorCodes.ConfigAuthTokenProviderMissing,
                        configurationErrorMessage: "No auth token provider or endpoint is configured.")
                },
                new RoomCompositionState { IsInjected = true });

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(RoomStartupFailureReason.MissingRuntimeCredentials, result.FailureReason);
            Assert.AreEqual(SessionErrorCodes.ConfigAuthTokenProviderMissing, result.FailureErrorCode);
            Assert.AreEqual("No auth token provider or endpoint is configured.", result.FailureRecordMessage);
        }

        [Test]
        public void ValidateAndComposeStartup_WhenAuthTokenEndpointInvalid_ReturnsEndpointInvalidCode()
        {
            var service = new RoomCompositionService();

            RoomCompositionStartupResult result = service.ValidateAndComposeStartup(
                new RoomCompositionContext
                {
                    CredentialProvider = new TestCredentialProvider(
                        null,
                        "https://core.convai.com",
                        hasValidCredentials: false,
                        configurationErrorCode: SessionErrorCodes.ConfigAuthTokenEndpointInvalid,
                        configurationErrorMessage: "The auth token endpoint must be an absolute HTTP(S) URL.")
                },
                new RoomCompositionState { IsInjected = true });

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(RoomStartupFailureReason.MissingRuntimeCredentials, result.FailureReason);
            Assert.AreEqual(SessionErrorCodes.ConfigAuthTokenEndpointInvalid, result.FailureErrorCode);
            Assert.AreEqual(
                "The auth token endpoint must be an absolute HTTP(S) URL.",
                result.FailureRecordMessage);
        }

        [Test]
        public void HandleOwnedAgentStateChanged_WhenStartupNotCompleted_DefersUntilStartup()
        {
            var service = new RoomCompositionService();

            RoomOwnershipChangeResult result = service.HandleOwnedAgentStateChanged(
                new RoomCompositionContext
                {
                    OwnershipProvider = new TestOwnershipProvider(
                        new RoomOwnershipSnapshot(
                            null,
                            new[] { new TestCharacterAgent("char-1") },
                            new ActiveConversationTarget(new TestCharacterAgent("char-1"))))
                },
                new RoomCompositionState { IsInjected = true, HasStarted = false });

            Assert.AreEqual(RoomOwnershipRebindOutcome.DeferredUntilStartup, result.Outcome);
            Assert.AreEqual("char-1", result.RequestedCharacterId);
        }

        [Test]
        public void HandleOwnedAgentStateChanged_WhenConnected_ReturnsPendingReconnect()
        {
            var service = new RoomCompositionService();

            RoomOwnershipChangeResult result = service.HandleOwnedAgentStateChanged(
                new RoomCompositionContext
                {
                    OwnershipProvider = new TestOwnershipProvider(
                        new RoomOwnershipSnapshot(
                            null,
                            new[] { new TestCharacterAgent("char-2") },
                            new ActiveConversationTarget(new TestCharacterAgent("char-2"))))
                },
                new RoomCompositionState
                {
                    IsInjected = true,
                    HasStarted = true,
                    CurrentState = SessionState.Connected
                });

            Assert.AreEqual(RoomOwnershipRebindOutcome.PendingReconnect, result.Outcome);
            Assert.IsTrue(result.SetPendingReconnect);
            Assert.AreEqual("char-2", result.PendingReconnectCharacterId);
        }

        [Test]
        public void HandleOwnedAgentStateChanged_WhenConnectionIsTransitioning_RejectsRebind()
        {
            var service = new RoomCompositionService();

            RoomOwnershipChangeResult result = service.HandleOwnedAgentStateChanged(
                new RoomCompositionContext
                {
                    OwnershipProvider = new TestOwnershipProvider(
                        new RoomOwnershipSnapshot(
                            null,
                            new[] { new TestCharacterAgent("char-3") },
                            new ActiveConversationTarget(new TestCharacterAgent("char-3"))))
                },
                new RoomCompositionState
                {
                    IsInjected = true,
                    HasStarted = true,
                    CurrentState = SessionState.Connecting
                });

            Assert.AreEqual(RoomOwnershipRebindOutcome.RejectedTransitionState, result.Outcome);
            Assert.AreEqual("char-3", result.RequestedCharacterId);
        }

        private sealed class TestCredentialProvider : ICredentialProvider, ICredentialConfigurationStatus
        {
            private readonly string _apiKey;
            private readonly bool _hasValidCredentials;
            private readonly string _serverUrl;

            public TestCredentialProvider(
                string apiKey,
                string serverUrl,
                bool? hasValidCredentials = null,
                string configurationErrorCode = null,
                string configurationErrorMessage = null)
            {
                _apiKey = apiKey;
                _serverUrl = serverUrl;
                _hasValidCredentials = hasValidCredentials ?? !string.IsNullOrEmpty(apiKey);
                ConfigurationErrorCode = configurationErrorCode;
                ConfigurationErrorMessage = configurationErrorMessage;
            }

            public bool HasValidCredentials => _hasValidCredentials;
            public string ConfigurationErrorCode { get; }
            public string ConfigurationErrorMessage { get; }
            public string GetApiKey() => _apiKey;
            public string GetServerUrl() => _serverUrl;
            public void Refresh() { }
        }

        private sealed class ExplicitTokenCredentialProvider :
            ICredentialProvider,
            IExplicitAuthTokenCredentialProvider
        {
            public bool HasValidCredentials => false;
            public string GetApiKey() => string.Empty;
            public string GetServerUrl() => "https://core.convai.com";
            public void SetAuthTokenForNextConnection(string authToken) { }
            public void Refresh() { }
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestOwnershipProvider : IRoomOwnershipProvider
        {
            private readonly RoomOwnershipSnapshot _snapshot;

            public TestOwnershipProvider(RoomOwnershipSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public RoomOwnershipSnapshot CaptureOwnership() => _snapshot;
        }

        private sealed class TestCharacterAgent : IConvaiCharacterAgent
        {
            public TestCharacterAgent(string characterId, string characterName = "Test Character")
            {
                CharacterId = characterId;
                CharacterName = characterName;
            }

            public string CharacterId { get; }
            public string CharacterName { get; }
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext { get; } = new MockDynamicContext();
            public EmotionDetectionMode EmotionDetectionMode => Convai.Domain.Emotion.EmotionDetectionMode.Off;
            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }
        }

        private sealed class TestPlayerAgent : IConvaiPlayerAgent
        {
            public string PlayerName => "Test Player";
            public string PlayerId => "test-player";
            public Color NameTagColor => Color.white;
            public event Action<string> OnTextMessageSent;

            public void SendTextMessage(string message) => OnTextMessageSent?.Invoke(message);
        }
    }
}
