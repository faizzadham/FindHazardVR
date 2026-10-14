using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Services;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Context;
using Convai.Runtime.Presentation.Services;
using Convai.Sample.UI.Settings;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using DomainSessionState = Convai.Domain.DomainEvents.Session.SessionState;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public class ConfigurationOwnershipTests
    {
        private readonly List<Object> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdObjects.Count; i++)
                if (_createdObjects[i] != null)
                    Object.DestroyImmediate(_createdObjects[i]);
            _createdObjects.Clear();
        }

        [Test]
        public void RoomManager_UsesInlineDefaults_WhenNoRoomConfigAssetIsAssigned()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Audio);
            SetAutoProperty(roomManager, "VideoTrackName", "inline-track");
            SetAutoProperty(roomManager, "ServerEndpoint", ConvaiServerEndpoint.Connect);
            SetAutoProperty(roomManager, "ConnectOnStart", true);
            SetPrivateField(roomManager, "_roomRejoinTtlSeconds", 90f);
            SetPrivateField(roomManager, "_maxReconnectAttempts", 4);

            Assert.AreEqual(ConvaiConfigSourceMode.Inline, roomManager.ConfigurationSource);
            Assert.IsNull(roomManager.RoomConfigAsset);
            Assert.AreEqual(ConvaiConnectionType.Audio, roomManager.EffectiveConnectionType);
            Assert.AreEqual("inline-track", roomManager.EffectiveVideoTrackName);
            Assert.AreEqual(ConvaiServerEndpoint.Connect, roomManager.EffectiveServerEndpoint);
            Assert.IsTrue(roomManager.EffectiveConnectOnStart);
            Assert.That(roomManager.EffectiveReconnectPolicyDescription, Does.Contain("TTL=90"));
            Assert.That(roomManager.EffectiveReconnectPolicyDescription, Does.Contain("MaxAttempts=4"));
        }

        [Test]
        public void RoomManager_ProjectSettingsServerUrl_IgnoresLegacySceneOverrideAndUpdatesImmediately()
        {
            var settings = ScriptableObject.CreateInstance<ConvaiSettings>();
            _createdObjects.Add(settings);
            SetPrivateField(settings, "_apiEnvironment", ConvaiApiEnvironment.Custom);
            settings.SetApiKey("test-api-key");
            settings.SetServerUrl("http://127.0.0.1:8000");

            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();
            SetPrivateField(roomManager, "_credentialProvider", new ProjectSettingsCredentialProvider(settings));
            SetAutoProperty(roomManager, "CoreServerBaseURL", "https://realtime-api-stg.convai.com");
            SetAutoProperty(roomManager, "ServerEndpoint", ConvaiServerEndpoint.Connect);

            Assert.That(roomManager.CoreServerURL, Is.EqualTo("http://127.0.0.1:8000/connect"));

            settings.SetServerUrl("http://127.0.0.1:9000");

            Assert.That(roomManager.CoreServerURL, Is.EqualTo("http://127.0.0.1:9000/connect"));
        }

        [Test]
        public void RoomManager_AutoDynamicVisionContext_StaysAudio_EvenWhenChildVisionPublisherExists()
        {
            // Auto must never upgrade an Audio room on its own: enabling vision changes what the
            // backend bills per turn, so a mere publisher component in the hierarchy is not consent.
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();
            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Audio);
            SetPrivateField(roomManager, "_visionContextMode", ConvaiVisionContextMode.Auto);

            var visionGo = new GameObject("VisionPublisher");
            _createdObjects.Add(visionGo);
            visionGo.transform.SetParent(go.transform);
            visionGo.AddComponent<TestVisionPublisher>();

            Assert.That(roomManager.EffectiveVisionContextMode, Is.EqualTo(ConvaiVisionContextMode.Auto));
            Assert.That(roomManager.EffectiveVisionContextEnabled, Is.False);
            Assert.That(roomManager.EffectiveConnectionType, Is.EqualTo(ConvaiConnectionType.Audio));
        }

        [Test]
        public void RoomManager_AutoDynamicVisionContext_EnablesVision_WhenConnectionTypeIsVideo()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();
            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Video);
            SetPrivateField(roomManager, "_visionContextMode", ConvaiVisionContextMode.Auto);

            Assert.That(roomManager.EffectiveVisionContextEnabled, Is.True);
            Assert.That(roomManager.EffectiveConnectionType, Is.EqualTo(ConvaiConnectionType.Video));
        }

        [Test]
        public void RoomManager_EnabledDynamicVisionContext_UpgradesAudioToVideo()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();
            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Audio);
            SetPrivateField(roomManager, "_visionContextMode", ConvaiVisionContextMode.Enabled);

            Assert.That(roomManager.EffectiveVisionContextEnabled, Is.True);
            Assert.That(roomManager.EffectiveConnectionType, Is.EqualTo(ConvaiConnectionType.Video));
        }

        [Test]
        public void RoomManager_DisabledDynamicVisionContext_KeepsConfiguredVideo_ForLegacyVideoPaths()
        {
            // Disabled only suppresses the dynamic-vision connect config; it must not tear down a
            // deliberately configured Video connection (legacy native-video paths, e.g. Gemini Live).
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();
            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Video);
            SetPrivateField(roomManager, "_visionContextMode", ConvaiVisionContextMode.Disabled);

            Assert.That(roomManager.EffectiveVisionContextEnabled, Is.False);
            Assert.That(roomManager.EffectiveConnectionType, Is.EqualTo(ConvaiConnectionType.Video));
        }

        [Test]
        public void RoomManager_RespondModes_AreSentEvenWhenDynamicVisionIsDisabled()
        {
            // The context_update / trigger / scene_metadata lanes govern plain dynamic-context
            // features, so the lane policy must reach the backend regardless of the vision mode.
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();
            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Audio);
            SetPrivateField(roomManager, "_visionContextMode", ConvaiVisionContextMode.Disabled);

            Assert.IsNull(roomManager.CreateEffectiveVisionInputConfig(),
                "vision_input_config must be omitted while dynamic vision is disabled.");
            var respondModes = roomManager.CreateEffectiveVisionRespondModes();
            Assert.IsNotNull(respondModes, "respond_modes must be sent even for non-vision rooms.");
            Assert.AreEqual("auto", respondModes.ContextUpdate);
            Assert.AreEqual("silent", respondModes.SceneMetadata);
        }

        [Test]
        public void RoomManager_AssignedLegacyRoomConfigDefaultsToAssetMode()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Audio);
            SetAutoProperty(roomManager, "ServerEndpoint", ConvaiServerEndpoint.Connect);
            SetAutoProperty(roomManager, "ConnectOnStart", true);

            var roomConfig = ScriptableObject.CreateInstance<ConvaiRoomManagerProfile>();
            _createdObjects.Add(roomConfig);

            SerializedObject serializedRoomConfig = new(roomConfig);
            serializedRoomConfig.FindProperty("_connectionType").enumValueIndex = (int)ConvaiConnectionType.Video;
            serializedRoomConfig.FindProperty("_videoTrackName").stringValue = "profile-track";
            serializedRoomConfig.FindProperty("_serverEndpoint").enumValueIndex = (int)ConvaiServerEndpoint.RoomSession;
            serializedRoomConfig.FindProperty("_connectOnStart").boolValue = false;
            serializedRoomConfig.FindProperty("_maxReconnectAttempts").intValue = 7;
            serializedRoomConfig.ApplyModifiedPropertiesWithoutUndo();

            SetPrivateField(roomManager, "_roomConfigAsset", roomConfig);
            SetPrivateField(roomManager, "_configurationSourceInitialized", false);

            Assert.AreEqual(ConvaiConfigSourceMode.Asset, roomManager.ConfigurationSource);
            Assert.AreSame(roomConfig, roomManager.RoomConfigAsset);
            Assert.AreEqual(ConvaiConnectionType.Video, roomManager.EffectiveConnectionType);
            Assert.AreEqual("profile-track", roomManager.EffectiveVideoTrackName);
            Assert.AreEqual(ConvaiServerEndpoint.RoomSession, roomManager.EffectiveServerEndpoint);
            Assert.IsFalse(roomManager.EffectiveConnectOnStart);
            Assert.That(roomManager.EffectiveReconnectPolicyDescription, Does.Contain("MaxAttempts=7"));
        }

        [Test]
        public void RoomManager_ExplicitInlineSelection_WinsOverAssignedRoomConfigAsset()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            SetPrivateField(roomManager, "_connectionType", ConvaiConnectionType.Audio);
            SetAutoProperty(roomManager, "ServerEndpoint", ConvaiServerEndpoint.Connect);
            SetAutoProperty(roomManager, "ConnectOnStart", true);

            var roomConfig = ScriptableObject.CreateInstance<ConvaiRoomManagerProfile>();
            _createdObjects.Add(roomConfig);

            SerializedObject serializedRoomConfig = new(roomConfig);
            serializedRoomConfig.FindProperty("_connectionType").enumValueIndex = (int)ConvaiConnectionType.Video;
            serializedRoomConfig.FindProperty("_connectOnStart").boolValue = false;
            serializedRoomConfig.ApplyModifiedPropertiesWithoutUndo();

            SetPrivateField(roomManager, "_roomConfigAsset", roomConfig);
            SetPrivateField(roomManager, "_configurationSource", ConvaiConfigSourceMode.Inline);
            SetPrivateField(roomManager, "_configurationSourceInitialized", true);

            Assert.AreEqual(ConvaiConfigSourceMode.Inline, roomManager.ConfigurationSource);
            Assert.AreSame(roomConfig, roomManager.RoomConfigAsset);
            Assert.AreEqual(ConvaiConnectionType.Audio, roomManager.EffectiveConnectionType);
            Assert.IsTrue(roomManager.EffectiveConnectOnStart);
        }

        [Test]
        public void RoomManager_InlineAecOverride_PreventsLegacyAssetAutoSelection()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            TurnTakingOptions inlineTurnTaking = TurnTakingOptions.CreateHandsFreeDefault();
            inlineTurnTaking.LocalAudioPolicy.EnableAcousticEchoCancellation = true;
            SetPrivateField(roomManager, "_turnTakingOptions", inlineTurnTaking);

            var roomConfig = ScriptableObject.CreateInstance<ConvaiRoomManagerProfile>();
            _createdObjects.Add(roomConfig);
            SetPrivateField(roomManager, "_roomConfigAsset", roomConfig);

            Assert.AreEqual(ConvaiConfigSourceMode.Inline, roomManager.ConfigurationSource);
            Assert.That(roomManager.EffectiveTurnTakingOptions.LocalAudioPolicy.EnableAcousticEchoCancellation, Is.True);
        }

        [Test]
        public void RoomManager_UsesRoomConfigAssetUserVadSettings_WhenAssetModeSelected()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            var roomConfig = ScriptableObject.CreateInstance<ConvaiRoomManagerProfile>();
            _createdObjects.Add(roomConfig);

            UserVadSettings profileVadSettings = UserVadSettings.CreateDefault();
            profileVadSettings.UseServerDefault = false;
            profileVadSettings.Confidence = 0.81f;
            profileVadSettings.StartSecs = 0.33f;
            profileVadSettings.StopSecs = 0.44f;
            profileVadSettings.MinVolume = 0.55f;
            SetPrivateField(roomConfig, "_userVadSettings", profileVadSettings);

            SetPrivateField(roomManager, "_roomConfigAsset", roomConfig);
            SetPrivateField(roomManager, "_configurationSource", ConvaiConfigSourceMode.Asset);
            SetPrivateField(roomManager, "_configurationSourceInitialized", true);

            UserVadSettings effective = roomManager.EffectiveUserVadSettings;
            Assert.That(effective.UseServerDefault, Is.False);
            Assert.That(effective.Confidence, Is.EqualTo(0.81f).Within(0.0001f));
            Assert.That(effective.StartSecs, Is.EqualTo(0.33f).Within(0.0001f));
            Assert.That(effective.StopSecs, Is.EqualTo(0.44f).Within(0.0001f));
            Assert.That(effective.MinVolume, Is.EqualTo(0.55f).Within(0.0001f));
        }

        [Test]
        public void RoomManager_UsesInlineUserVadSettings_WhenInlineModeSelected()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            UserVadSettings inlineVadSettings = UserVadSettings.CreateDefault();
            inlineVadSettings.UseServerDefault = false;
            inlineVadSettings.Confidence = 0.75f;
            SetPrivateField(roomManager, "_userVadSettings", inlineVadSettings);

            var roomConfig = ScriptableObject.CreateInstance<ConvaiRoomManagerProfile>();
            _createdObjects.Add(roomConfig);
            SetPrivateField(roomConfig, "_userVadSettings", UserVadSettings.CreateDefault());
            SetPrivateField(roomManager, "_roomConfigAsset", roomConfig);
            SetPrivateField(roomManager, "_configurationSource", ConvaiConfigSourceMode.Inline);
            SetPrivateField(roomManager, "_configurationSourceInitialized", true);

            UserVadSettings effective = roomManager.EffectiveUserVadSettings;
            Assert.That(effective.UseServerDefault, Is.False);
            Assert.That(effective.Confidence, Is.EqualTo(0.75f).Within(0.0001f));
        }

        [Test]
        public void Character_UsesInlineFields_WhenNoCharacterConfigAssetIsAssigned()
        {
            var go = new GameObject("Character");
            _createdObjects.Add(go);
            var character = go.AddComponent<ConvaiCharacter>();
            character.Configure("local-char", "Local Character");
            SetPrivateField(character, "_nameTagColor", Color.cyan);
            SetPrivateField(character, "_enableRemoteAudio", true);
            SetPrivateField(character, "_enableSessionResume", true);

            Assert.AreEqual(ConvaiConfigSourceMode.Inline, character.ConfigurationSource);
            Assert.IsNull(character.CharacterConfigAsset);
            Assert.AreEqual("local-char", character.CharacterId);
            Assert.AreEqual("Local Character", character.CharacterName);
            Assert.AreEqual(Color.cyan, character.NameTagColor);
            Assert.IsTrue(character.EnableRemoteAudioOnStart);
            Assert.IsTrue(character.EnableSessionResume);
        }

        [Test]
        public void Character_SessionIdApi_NormalizesRuntimeValue()
        {
            var go = new GameObject("Character");
            _createdObjects.Add(go);
            var character = go.AddComponent<ConvaiCharacter>();

            character.SetCharacterSessionId("  character-session-123  ");

            Assert.AreEqual("character-session-123", character.CharacterSessionId);

            character.ClearCharacterSessionId();

            Assert.AreEqual(string.Empty, character.CharacterSessionId);
        }

        [Test]
        public void Character_AssignedLegacyCharacterConfigDefaultsToAssetMode()
        {
            var go = new GameObject("Character");
            _createdObjects.Add(go);
            var character = go.AddComponent<ConvaiCharacter>();
            character.Configure("local-char", "Local Character");
            SetPrivateField(character, "_nameTagColor", Color.green);
            SetPrivateField(character, "_enableRemoteAudio", false);
            SetPrivateField(character, "_enableSessionResume", false);

            var definition = ScriptableObject.CreateInstance<ConvaiCharacterProfile>();
            definition.name = "GuideAgent";
            _createdObjects.Add(definition);

            SerializedObject serializedDefinition = new(definition);
            serializedDefinition.FindProperty("_characterId").stringValue = "definition-char";
            serializedDefinition.FindProperty("_characterName").stringValue = "Guide";
            serializedDefinition.FindProperty("_nameTagColor").colorValue = Color.magenta;
            serializedDefinition.FindProperty("_enableRemoteAudioOnStart").boolValue = true;
            serializedDefinition.FindProperty("_enableSessionResume").boolValue = true;
            serializedDefinition.ApplyModifiedPropertiesWithoutUndo();

            SetPrivateField(character, "_characterConfigAsset", definition);
            SetPrivateField(character, "_configurationSourceInitialized", false);

            Assert.AreEqual(ConvaiConfigSourceMode.Asset, character.ConfigurationSource);
            Assert.AreSame(definition, character.CharacterConfigAsset);
            Assert.AreEqual("definition-char", character.CharacterId);
            Assert.AreEqual("Guide", character.CharacterName);
            Assert.AreEqual(Color.magenta, character.NameTagColor);
            Assert.IsTrue(character.EnableRemoteAudioOnStart);
            Assert.IsTrue(character.EnableSessionResume);
        }

        [Test]
        public void Character_ExplicitInlineSelection_WinsOverAssignedCharacterConfigAsset()
        {
            var go = new GameObject("Character");
            _createdObjects.Add(go);
            var character = go.AddComponent<ConvaiCharacter>();
            character.Configure("local-char", "Local Character");

            var definition = ScriptableObject.CreateInstance<ConvaiCharacterProfile>();
            definition.name = "GuideAgent";
            _createdObjects.Add(definition);

            SerializedObject serializedDefinition = new(definition);
            serializedDefinition.FindProperty("_characterId").stringValue = "definition-char";
            serializedDefinition.FindProperty("_characterName").stringValue = "Guide";
            serializedDefinition.ApplyModifiedPropertiesWithoutUndo();

            SetPrivateField(character, "_characterConfigAsset", definition);
            SetPrivateField(character, "_configurationSource", ConvaiConfigSourceMode.Inline);
            SetPrivateField(character, "_configurationSourceInitialized", true);

            Assert.AreEqual(ConvaiConfigSourceMode.Inline, character.ConfigurationSource);
            Assert.AreSame(definition, character.CharacterConfigAsset);
            Assert.AreEqual("local-char", character.CharacterId);
            Assert.AreEqual("Local Character", character.CharacterName);
        }

        [Test]
        public async Task RoomManager_ConnectAsyncWithOptions_ClearsPendingOverrides_WhenConnectReturnsEarly()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            var operation = roomManager.ConnectAsync(new RoomSessionConnectOptions
            {
                TurnTaking = TurnTakingOptions.CreatePushToTalkDefault()
            });

            try
            {
                await operation.AsTask();
                Assert.Fail("Expected ConnectAsync to fail before runtime injection is complete.");
            }
            catch (Exception)
            {
            }

            Assert.IsNull(GetPrivateField<ConvaiRoomManager, RoomSessionConnectOptions>(roomManager, "_pendingConnectOptions"));
        }

        [Test]
        public void RoomSessionConnectOptions_Clone_PreservesOneShotTokenAndEndUserMetadata()
        {
            var options = new RoomSessionConnectOptions
            {
                EndUserId = "player-42",
                EndUserMetadata = new Dictionary<string, object>
                {
                    ["name"] = "Player 42"
                }
            };
            options.SetExplicitAuthToken("  explicit-token  ");

            RoomSessionConnectOptions clone = options.Clone();

            Assert.AreEqual("player-42", clone.EndUserId);
            Assert.AreEqual("Player 42", clone.EndUserMetadata?["name"]);
            Assert.AreEqual("explicit-token", clone.ConsumeExplicitAuthToken());
            Assert.IsNull(clone.ConsumeExplicitAuthToken(),
                "The cloned credential must remain one-shot.");
        }

        [Test]
        public async Task RoomManager_ConnectAsyncWithOptions_DoesNotOverwriteExistingPendingOverrides()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            var firstOptions = new RoomSessionConnectOptions
            {
                TurnTaking = TurnTakingOptions.CreatePushToTalkDefault()
            };

            SetPrivateField(roomManager, "_pendingConnectOptions", firstOptions);

            var operation = roomManager.ConnectAsync(new RoomSessionConnectOptions
            {
                TurnTaking = TurnTakingOptions.CreateHandsFreeDefault()
            });

            try
            {
                await operation.AsTask();
                Assert.Fail("Expected ConnectAsync to fail before runtime injection is complete.");
            }
            catch (Exception)
            {
            }

            RoomSessionConnectOptions pending =
                GetPrivateField<ConvaiRoomManager, RoomSessionConnectOptions>(roomManager, "_pendingConnectOptions");
            Assert.IsNotNull(pending);
            Assert.AreEqual(ConversationInputMode.PushToTalk, pending.TurnTaking.Mode);
        }

        [Test]
        public void RoomManager_AdoptsLegacyManagerConversationSettings_WhenEditorInitializedButConversationStillDefault()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            SetPrivateField(roomManager, "_configurationSource", ConvaiConfigSourceMode.Inline);
            SetPrivateField(roomManager, "_configurationSourceInitialized", true);

            roomManager.AdoptLegacyManagerConversationSettings(
                ConvaiManagerConversationMode.PushToTalk,
                KeyCode.B,
                interruptBotOnPress: false,
                requireTurnCompletionBeforeNextPress: true,
                pushToTalkTurnCompletionTimeoutMs: 4200);

            Assert.AreEqual(ConvaiConfigSourceMode.Inline, roomManager.ConfigurationSource);
            Assert.AreEqual(KeyCode.B, roomManager.PushToTalkKey);
            Assert.AreEqual(ConversationInputMode.PushToTalk, roomManager.EffectiveTurnTakingOptions.Mode);
            Assert.AreEqual(4200, roomManager.EffectiveTurnTakingOptions.PushToTalkPolicy.TurnCompletionTimeoutMs);
            Assert.IsFalse(roomManager.EffectiveTurnTakingOptions.PushToTalkPolicy.InterruptBotOnPress);
        }

        [Test]
        public void RoomManager_SetConversationInputModeAsync_RejectsWhenNotConnected()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            ConvaiOperationException exception = Assert.ThrowsAsync<ConvaiOperationException>(
                async () => await roomManager.SetConversationInputModeAsync(ConversationInputMode.PushToTalk).AsTask());

            Assert.That(exception.Code, Is.EqualTo(SessionErrorCodes.SessionInvalidState));
        }

        [TestCase("event-hub")]
        [TestCase("session-state-changed")]
        [TestCase("connected")]
        public async Task RoomManager_ListenerSwitchingInputModeOnConnected_IsAccepted(string notification)
        {
            // A game that restores the player's input mode when the session connects does so from
            // whichever Connected notification it listens to, and every one of them has to find a
            // session that already accepts the switch. The event hub hears the transition first.
            ConnectingRoomManager harness = CreateConnectingRoomManager(ConversationInputMode.HandsFree);
            ConvaiRoomManager roomManager = harness.RoomManager;

            IConvaiOperation<Unit> switchOperation = null;

            void SwitchToPushToTalk() =>
                switchOperation ??= roomManager.SetConversationInputModeAsync(ConversationInputMode.PushToTalk);

            switch (notification)
            {
                case "event-hub":
                    harness.EventHub.Subscribe<SessionStateChanged>(stateChanged =>
                    {
                        if (stateChanged.NewState == DomainSessionState.Connected) SwitchToPushToTalk();
                    });
                    break;
                case "session-state-changed":
                    roomManager.OnSessionStateChanged += stateChanged =>
                    {
                        if (stateChanged.NewState == DomainSessionState.Connected) SwitchToPushToTalk();
                    };
                    break;
                default:
                    roomManager.Connected += SwitchToPushToTalk;
                    break;
            }

            // Subscribed after the listener, so the hub case really is the earliest subscriber.
            harness.RelayTransitionsToRoomManager();

            InvokePrivateMethod(roomManager, "UpdateSessionState", DomainSessionState.Connected, null);

            Assert.That(roomManager.CurrentState, Is.EqualTo(DomainSessionState.Connected));
            Assert.IsNotNull(switchOperation, "The listener never heard Connected.");

            ConvaiOperationException refusal = null;
            try
            {
                await switchOperation.AsTask();
            }
            catch (ConvaiOperationException exception)
            {
                refusal = exception;
            }

            Assert.IsNull(refusal, $"The switch was refused with {refusal?.Code}: {refusal?.Message}");
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.PushToTalk));
        }

        [Test]
        public void RoomManager_SessionErrorAfterConnected_RevertsPromotedTurnTakingState()
        {
            // The turn-taking state is promoted when Connected is requested; a session that then
            // fails must fall back to the configured defaults and refuse switches again.
            ConnectingRoomManager harness = CreateConnectingRoomManager(ConversationInputMode.PushToTalk);
            harness.RelayTransitionsToRoomManager();
            ConvaiRoomManager roomManager = harness.RoomManager;
            SetPrivateField(roomManager, "_turnTakingOptions", TurnTakingOptions.CreateHandsFreeDefault());

            InvokePrivateMethod(roomManager, "UpdateSessionState", DomainSessionState.Connected, null);
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.PushToTalk));

            InvokePrivateMethod(
                roomManager,
                "UpdateSessionState",
                DomainSessionState.Error,
                SessionError.Create(SessionErrorCodes.ConnectionFailed, "Transport lost."));

            Assert.That(roomManager.CurrentState, Is.EqualTo(DomainSessionState.Error));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.HandsFree));
            Assert.That(GetPrivateField<ConvaiRoomManager, bool>(roomManager, "_hasConnectedSessionTurnTakingState"),
                Is.False);
            ConvaiOperationException exception = Assert.ThrowsAsync<ConvaiOperationException>(
                async () => await roomManager.SetConversationInputModeAsync(ConversationInputMode.PushToTalk).AsTask());
            Assert.That(exception.Code, Is.EqualTo(SessionErrorCodes.SessionInvalidState));
        }

        [Test]
        public async Task RoomManager_ConversationInputModeChangedOnConnect_HandlerCanSwitchMode()
        {
            // The session's mode is announced once the session is Connected, so a handler that
            // answers the announcement by switching the mode is accepted.
            ConnectingRoomManager harness = CreateConnectingRoomManager(ConversationInputMode.PushToTalk);
            harness.RelayTransitionsToRoomManager();
            ConvaiRoomManager roomManager = harness.RoomManager;
            SetPrivateField(roomManager, "_turnTakingOptions", TurnTakingOptions.CreateHandsFreeDefault());

            var announcements = new List<string>();
            IConvaiOperation<Unit> switchOperation = null;
            roomManager.ConversationInputModeChanged += mode =>
            {
                announcements.Add($"{mode}:{roomManager.CurrentState}");
                if (mode == ConversationInputMode.PushToTalk)
                    switchOperation ??= roomManager.SetConversationInputModeAsync(ConversationInputMode.HandsFree);
            };

            InvokePrivateMethod(roomManager, "UpdateSessionState", DomainSessionState.Connected, null);

            Assert.IsNotNull(switchOperation, "The session's mode was never announced.");
            await switchOperation.AsTask();
            Assert.That(announcements, Is.EqualTo(new[] { "PushToTalk:Connected", "HandsFree:Connected" }));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.HandsFree));
        }

        [Test]
        public void RoomManager_ActiveConversationInputMode_UsesConnectedSessionState()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            SetPrivateField(roomManager, "_turnTakingOptions", TurnTakingOptions.CreatePushToTalkDefault());
            SetPrivateField(roomManager, "_sessionTurnTakingSourceOptions", TurnTakingOptions.CreateHandsFreeDefault());
            SetPrivateField(roomManager, "_hasConnectedSessionTurnTakingState", true);

            ResolvedTurnTakingOptions resolvedHandsFree =
                TurnTakingOptionsResolver.ResolveFromSource(TurnTakingOptions.CreateHandsFreeDefault());
            SetPrivateField(roomManager, "_currentResolvedTurnTakingOptions", resolvedHandsFree);

            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.HandsFree));
        }

        [Test]
        public async Task RoomManager_HandsFreeToPushToTalk_OrdersControlsBeforePublishingMode()
        {
            ConvaiRoomManager roomManager = CreateRoomManagerWithConnectedMode(ConversationInputMode.HandsFree);
            var operations = new List<string>();
            roomManager.ConversationInputModeSetMicMutedOverrideForTests =
                muted => operations.Add($"mic:{muted}");
            roomManager.ConversationInputModeForceStopOverrideForTests = () =>
            {
                operations.Add("force-user-stopped-speaking");
                return true;
            };
            roomManager.ConversationInputModeSetSttMutedOverrideForTests = muted =>
            {
                operations.Add($"stt:{muted}");
                return true;
            };
            roomManager.ConversationInputModePrepareControllersOverrideForTests = (_, _) =>
            {
                operations.Add("controllers-prepared");
                return true;
            };
            roomManager.ConversationInputModeChanged += mode => operations.Add($"mode:{mode}");

            TurnTakingOptions nextSource = TurnTakingOptions.CreatePushToTalkDefault();
            await InvokeConversationInputModeTransitionAsync(
                roomManager,
                "TransitionHandsFreeToPushToTalkAsync",
                TurnTakingOptions.CreateHandsFreeDefault(),
                nextSource);

            Assert.That(
                operations,
                Is.EqualTo(new[]
                {
                    "mic:True",
                    "force-user-stopped-speaking",
                    "stt:True",
                    "controllers-prepared",
                    "mic:True",
                    "mode:PushToTalk"
                }));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.PushToTalk));
        }

        [Test]
        public void RoomManager_HandsFreeToPushToTalk_WhenStopIsRejected_RestoresHandsFree()
        {
            ConvaiRoomManager roomManager = CreateRoomManagerWithConnectedMode(ConversationInputMode.HandsFree);
            var operations = new List<string>();
            roomManager.ConversationInputModeSetMicMutedOverrideForTests =
                muted => operations.Add($"mic:{muted}");
            roomManager.ConversationInputModeForceStopOverrideForTests = () =>
            {
                operations.Add("force-user-stopped-speaking");
                return false;
            };

            TurnTakingOptions nextSource = TurnTakingOptions.CreatePushToTalkDefault();
            ConvaiOperationException exception = Assert.ThrowsAsync<ConvaiOperationException>(async () =>
                await InvokeConversationInputModeTransitionAsync(
                    roomManager,
                    "TransitionHandsFreeToPushToTalkAsync",
                    TurnTakingOptions.CreateHandsFreeDefault(),
                    nextSource));

            Assert.That(exception.Code, Is.EqualTo(SessionErrorCodes.ConnectionServiceUnavailable));
            Assert.That(
                operations,
                Is.EqualTo(new[] { "mic:True", "force-user-stopped-speaking", "mic:False" }));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.HandsFree));
        }

        [Test]
        public async Task RoomManager_PushToTalkToHandsFree_CleansControllerBeforeReopeningSttAndPublishingMode()
        {
            ConvaiRoomManager roomManager = CreateRoomManagerWithConnectedMode(ConversationInputMode.PushToTalk);
            var operations = new List<string>();
            roomManager.ConversationInputModePrepareControllersOverrideForTests = (_, _) =>
            {
                operations.Add("controllers-prepared");
                return true;
            };
            roomManager.ConversationInputModeSetSttMutedOverrideForTests = muted =>
            {
                operations.Add($"stt:{muted}");
                return true;
            };
            roomManager.ConversationInputModeSetMicMutedOverrideForTests =
                muted => operations.Add($"mic:{muted}");
            roomManager.ConversationInputModeChanged += mode => operations.Add($"mode:{mode}");

            TurnTakingOptions nextSource = TurnTakingOptions.CreateHandsFreeDefault();
            await InvokeConversationInputModeTransitionAsync(
                roomManager,
                "TransitionPushToTalkToHandsFreeAsync",
                TurnTakingOptions.CreatePushToTalkDefault(),
                nextSource);

            Assert.That(
                operations,
                Is.EqualTo(new[] { "controllers-prepared", "stt:False", "mic:False", "mode:HandsFree" }));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.HandsFree));
        }

        [Test]
        public async Task RoomManager_PushToTalkToHandsFree_DoesNotToggleSttWhenCurrentPolicyDoesNotOwnIt()
        {
            ConvaiRoomManager roomManager = CreateRoomManagerWithConnectedMode(ConversationInputMode.PushToTalk);
            var operations = new List<string>();
            roomManager.ConversationInputModePrepareControllersOverrideForTests = (_, _) =>
            {
                operations.Add("controllers-prepared");
                return true;
            };
            roomManager.ConversationInputModeSetSttMutedOverrideForTests = muted =>
            {
                operations.Add($"stt:{muted}");
                return true;
            };
            roomManager.ConversationInputModeSetMicMutedOverrideForTests =
                muted => operations.Add($"mic:{muted}");
            roomManager.ConversationInputModeChanged += mode => operations.Add($"mode:{mode}");

            TurnTakingOptions currentSource = TurnTakingOptions.CreatePushToTalkDefault();
            currentSource.PushToTalkPolicy.EnableServerSttToggle = false;
            TurnTakingOptions nextSource = TurnTakingOptions.CreateHandsFreeDefault();
            await InvokeConversationInputModeTransitionAsync(
                roomManager,
                "TransitionPushToTalkToHandsFreeAsync",
                currentSource,
                nextSource);

            Assert.That(
                operations,
                Is.EqualTo(new[] { "controllers-prepared", "mic:False", "mode:HandsFree" }));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.HandsFree));
        }

        [Test]
        public void RoomManager_PushToTalkToHandsFree_WhenSttUnmuteIsRejected_RemainsPushToTalkMuted()
        {
            ConvaiRoomManager roomManager = CreateRoomManagerWithConnectedMode(ConversationInputMode.PushToTalk);
            var operations = new List<string>();
            roomManager.ConversationInputModePrepareControllersOverrideForTests = (_, reason) =>
            {
                operations.Add(reason.EndsWith("rollback", StringComparison.Ordinal)
                    ? "controllers-rolled-back"
                    : "controllers-prepared");
                return true;
            };
            roomManager.ConversationInputModeSetSttMutedOverrideForTests = muted =>
            {
                operations.Add($"stt:{muted}");
                return false;
            };
            roomManager.ConversationInputModeSetMicMutedOverrideForTests =
                muted => operations.Add($"mic:{muted}");

            TurnTakingOptions nextSource = TurnTakingOptions.CreateHandsFreeDefault();
            ConvaiOperationException exception = Assert.ThrowsAsync<ConvaiOperationException>(async () =>
                await InvokeConversationInputModeTransitionAsync(
                    roomManager,
                    "TransitionPushToTalkToHandsFreeAsync",
                    TurnTakingOptions.CreatePushToTalkDefault(),
                    nextSource));

            Assert.That(exception.Code, Is.EqualTo(SessionErrorCodes.ConnectionServiceUnavailable));
            Assert.That(
                operations,
                Is.EqualTo(new[] { "controllers-prepared", "stt:False", "mic:True", "controllers-rolled-back" }));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.PushToTalk));
        }

        [Test]
        public void RoomManager_PushToTalkToHandsFree_WhenControllerCleanupFails_RemainsPushToTalkMuted()
        {
            ConvaiRoomManager roomManager = CreateRoomManagerWithConnectedMode(ConversationInputMode.PushToTalk);
            var operations = new List<string>();
            roomManager.ConversationInputModePrepareControllersOverrideForTests = (_, reason) =>
            {
                bool rollback = reason.EndsWith("rollback", StringComparison.Ordinal);
                operations.Add(rollback ? "controllers-rolled-back" : "controllers-rejected");
                return rollback;
            };
            roomManager.ConversationInputModeSetMicMutedOverrideForTests =
                muted => operations.Add($"mic:{muted}");

            TurnTakingOptions nextSource = TurnTakingOptions.CreateHandsFreeDefault();
            ConvaiOperationException exception = Assert.ThrowsAsync<ConvaiOperationException>(async () =>
                await InvokeConversationInputModeTransitionAsync(
                    roomManager,
                    "TransitionPushToTalkToHandsFreeAsync",
                    TurnTakingOptions.CreatePushToTalkDefault(),
                    nextSource));

            Assert.That(exception.Code, Is.EqualTo(SessionErrorCodes.ConnectionServiceUnavailable));
            Assert.That(
                operations,
                Is.EqualTo(new[] { "controllers-rejected", "mic:True", "controllers-rolled-back" }));
            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.PushToTalk));
        }

        [Test]
        public async Task RoomManager_Disconnect_PreparesPushToTalkControllersBeforeTransportTeardown()
        {
            ConvaiRoomManager roomManager = CreateRoomManagerWithConnectedMode(ConversationInputMode.PushToTalk);
            var operations = new List<string>();
            roomManager.ConversationInputModePrepareControllersOverrideForTests = (_, _) =>
            {
                operations.Add("controllers-prepared");
                return true;
            };
            SetAutoProperty(
                roomManager,
                "ConnectionCoordinator",
                new RecordingConnectionCoordinator(() => operations.Add("disconnect")));

            await roomManager.DisconnectAsync().AsTask();

            Assert.That(operations, Is.EqualTo(new[] { "controllers-prepared", "disconnect" }));
        }

        [Test]
        public void RoomManager_ClearActiveSessionTurnTakingState_RevertsToConfiguredDefaults()
        {
            var go = new GameObject("RoomManager");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            SetPrivateField(roomManager, "_turnTakingOptions", TurnTakingOptions.CreatePushToTalkDefault());
            SetPrivateField(roomManager, "_sessionTurnTakingSourceOptions", TurnTakingOptions.CreateHandsFreeDefault());
            SetPrivateField(roomManager, "_hasConnectedSessionTurnTakingState", true);

            ResolvedTurnTakingOptions resolvedHandsFree =
                TurnTakingOptionsResolver.ResolveFromSource(TurnTakingOptions.CreateHandsFreeDefault());
            SetPrivateField(roomManager, "_currentResolvedTurnTakingOptions", resolvedHandsFree);

            InvokePrivateMethod(roomManager, "ClearActiveSessionTurnTakingState");

            Assert.That(roomManager.ActiveConversationInputMode, Is.EqualTo(ConversationInputMode.PushToTalk));
            Assert.That(GetPrivateField<ConvaiRoomManager, bool>(roomManager, "_hasConnectedSessionTurnTakingState"),
                Is.False);
        }

        [Test]
        public void SettingsHandler_Disable_UnsubscribesAndRestoresCursorState()
        {
            var go = new GameObject("SettingsHandler");
            _createdObjects.Add(go);
            go.SetActive(false);
            SettingsHandler handler = go.AddComponent<SettingsHandler>();
            var panelController = new ConvaiSettingsPanelController();
            SetPrivateField(handler, "_panelController", panelController);
            SetPrivateField(handler, "_panelInjected", true);

            CursorLockMode originalLockMode = Cursor.lockState;
            bool originalVisibility = Cursor.visible;
            try
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                // EditMode does not run MonoBehaviour lifecycle callbacks, so activating the object
                // would never reach OnEnable and the handler would never subscribe — the same reason
                // OnDisable_UnregistersFromRegistry drives the pair by hand.
                InvokeLifecycle(handler, "OnEnable");

                panelController.Open();
                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
                Assert.That(Cursor.visible, Is.True);

                InvokeLifecycle(handler, "OnDisable");
                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
                Assert.That(Cursor.visible, Is.False);

                Cursor.lockState = CursorLockMode.Confined;
                Cursor.visible = false;
                panelController.Close();
                panelController.Open();

                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Confined));
                Assert.That(Cursor.visible, Is.False);
            }
            finally
            {
                Cursor.lockState = originalLockMode;
                Cursor.visible = originalVisibility;
            }
        }

        /// <summary>
        ///     Calls a MonoBehaviour lifecycle callback directly.
        /// </summary>
        /// <remarks>
        ///     EditMode never raises Awake, OnEnable or OnDisable, so a test that needs one has to
        ///     call it — activating or deactivating the object proves nothing here.
        /// </remarks>
        private static void InvokeLifecycle(MonoBehaviour behaviour, string methodName)
        {
            MethodInfo method = behaviour.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, $"Missing lifecycle callback {methodName}.");
            method.Invoke(behaviour, null);
        }

        private static void SetAutoProperty<TTarget, TValue>(TTarget target, string propertyName, TValue value)
        {
            FieldInfo field = typeof(TTarget).GetField($"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing auto-property backing field for {propertyName}.");
            field.SetValue(target, value);
        }

        private static void SetPrivateField<TTarget>(TTarget target, string fieldName, object value)
        {
            FieldInfo field = typeof(TTarget).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing private field {fieldName}.");
            field.SetValue(target, value);
        }

        private static TValue GetPrivateField<TTarget, TValue>(TTarget target, string fieldName)
        {
            FieldInfo field = typeof(TTarget).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing private field {fieldName}.");
            return (TValue)field.GetValue(target);
        }

        private static object InvokePrivateMethod<TTarget>(TTarget target, string methodName, params object[] args)
        {
            MethodInfo method = typeof(TTarget).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Missing private method {methodName}.");
            return method.Invoke(target, args);
        }

        /// <summary>
        ///     A room manager in the Connecting state with a real session state machine behind it, so
        ///     a Connected transition reaches its listeners the way it does at runtime.
        /// </summary>
        /// <remarks>
        ///     The runtime adapter and the connection coordinator only relay the state machine's
        ///     event to the room manager's coordinator handler; the relay is subscribed directly
        ///     here because the adapter cannot be built without a transport.
        /// </remarks>
        private sealed class ConnectingRoomManager
        {
            public ConvaiRoomManager RoomManager;
            public EventHub EventHub;

            public void RelayTransitionsToRoomManager() =>
                EventHub.Subscribe(CreatePrivateHandler<SessionStateChanged>(RoomManager, "HandleCoordinatorStateChanged"));
        }

        private ConnectingRoomManager CreateConnectingRoomManager(ConversationInputMode sessionMode)
        {
            var go = new GameObject($"RoomManager-{sessionMode}-connecting");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();

            var eventHub = new EventHub(new ImmediateScheduler());
            var stateMachine = new SessionStateMachine(eventHub);
            Assert.IsTrue(stateMachine.TryTransition(DomainSessionState.Connecting));
            SetPrivateField(roomManager, "_eventHub", eventHub);
            SetPrivateField(roomManager, "_sessionStateMachine", stateMachine);
            SetPrivateField(roomManager, "_sessionDiagnostics",
                new RoomSessionDiagnostics(stateMachine, null, eventHub, null, () => "session-1"));
            SetPrivateField(roomManager, "_convaiRoomController", new ConnectedRoomController());
            // Connected would otherwise schedule the microphone auto-start coroutine.
            SetPrivateField(roomManager, "_autoStartMicrophoneCompleted", true);

            TurnTakingOptions sourceOptions = sessionMode == ConversationInputMode.HandsFree
                ? TurnTakingOptions.CreateHandsFreeDefault()
                : TurnTakingOptions.CreatePushToTalkDefault();
            roomManager.PrepareSessionTurnTakingState(
                sourceOptions,
                TurnTakingOptionsResolver.ResolveFromSource(sourceOptions));

            roomManager.ConversationInputModeSetMicMutedOverrideForTests = _ => { };
            roomManager.ConversationInputModeForceStopOverrideForTests = () => true;
            roomManager.ConversationInputModeSetSttMutedOverrideForTests = _ => true;
            roomManager.ConversationInputModePrepareControllersOverrideForTests = (_, _) => true;

            return new ConnectingRoomManager { RoomManager = roomManager, EventHub = eventHub };
        }

        private static Action<TEvent> CreatePrivateHandler<TEvent>(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Missing private method {methodName}.");
            return (Action<TEvent>)Delegate.CreateDelegate(typeof(Action<TEvent>), target, method);
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class ConnectedRoomController : IConvaiRoomController
        {
            public bool HasRoomDetails => true;
            public bool IsConnectedToRoom => true;
            public bool IsMicMuted => false;
            public string SessionID => "session-1";
            public string CharacterSessionID => "character-session-1";
            public string RoomName => "room-1";
            public string RoomURL => "wss://room-1";
            public string Token => "token";
            public string ResolvedSpeakerId => string.Empty;
            public string RequestTraceId => string.Empty;
            public string ResolvedEndUserId => string.Empty;
            public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata => null;
            public RTVIHandler RTVIHandler => null;
            public IRoomFacade CurrentRoom => null;
            public event Action OnRoomConnectionSuccessful { add { } remove { } }
            public event Action OnRoomConnectionFailed { add { } remove { } }
            public event Action<bool> OnMicMuteChanged { add { } remove { } }
            public event Action OnRoomReconnecting { add { } remove { } }
            public event Action OnRoomReconnected { add { } remove { } }
            public event Action OnUnexpectedRoomDisconnected { add { } remove { } }
            public event Action<IRemoteAudioTrack, string, string> OnRemoteAudioTrackSubscribed { add { } remove { } }
            public event Action<string, string> OnRemoteAudioTrackUnsubscribed { add { } remove { } }

            public Task<RoomConnectionAttemptResult> InitializeAsync(string connectionType, string coreServerUrl,
                string characterId, string storedSessionId, bool enableSessionResume, string dynamicInfoText,
                bool keepDynamicInfoInContext) => Task.FromResult(RoomConnectionAttemptResult.Success());

            public Task<RoomConnectionAttemptResult> InitializeAsync(string connectionType, string coreServerUrl,
                string characterId, string storedSessionId, bool enableSessionResume, string dynamicInfoText,
                bool keepDynamicInfoInContext, RoomJoinOptions joinOptions,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(RoomConnectionAttemptResult.Success());

            public void DisconnectFromRoom() { }
            public Task DisconnectFromRoomAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void SetMicMuted(bool mute) { }
            public void ToggleMicMute() { }
            public bool SetCharacterAudioMuted(string characterId, bool mute) => true;
            public bool MuteCharacter(string characterId) => true;
            public bool UnmuteCharacter(string characterId) => true;
            public bool IsCharacterAudioMuted(string characterId) => false;
            public void SetAudioSubscriptionPolicy(Func<string, bool> policy) { }
            public void ApplyRemoteAudioPreference(string characterId, bool enabled) { }
            public void Dispose() { }
        }

        private ConvaiRoomManager CreateRoomManagerWithConnectedMode(ConversationInputMode mode)
        {
            var go = new GameObject($"RoomManager-{mode}");
            _createdObjects.Add(go);
            var roomManager = go.AddComponent<ConvaiRoomManager>();
            TurnTakingOptions sourceOptions = mode == ConversationInputMode.HandsFree
                ? TurnTakingOptions.CreateHandsFreeDefault()
                : TurnTakingOptions.CreatePushToTalkDefault();
            roomManager.UpdateConnectedSessionTurnTakingState(
                sourceOptions,
                TurnTakingOptionsResolver.ResolveFromSource(sourceOptions));
            return roomManager;
        }

        private static async Task InvokeConversationInputModeTransitionAsync(
            ConvaiRoomManager roomManager,
            string methodName,
            TurnTakingOptions currentSource,
            TurnTakingOptions nextSource)
        {
            var task = (Task)InvokePrivateMethod(
                roomManager,
                methodName,
                TurnTakingOptionsResolver.ResolveFromSource(currentSource),
                nextSource,
                TurnTakingOptionsResolver.ResolveFromSource(nextSource),
                CancellationToken.None);
            await task;
        }

        private sealed class TestVisionPublisher : MonoBehaviour, IVisionPublisher
        {
            public string VideoTrackName => "test-vision";
        }

        private sealed class RecordingConnectionCoordinator : IRoomConnectionCoordinator
        {
            private readonly Action _onDisconnect;

            public RecordingConnectionCoordinator(Action onDisconnect) => _onDisconnect = onDisconnect;

            public event Action<SessionStateChanged> StateChanged
            {
                add { }
                remove { }
            }
            public DomainSessionState CurrentState => DomainSessionState.Connected;
            public bool IsConnected => true;
            public RoomSession CurrentSession => RoomSession.Empty;
            public string RoomCode => string.Empty;
            public bool IsRoomOwner => true;

            public IConvaiOperation<RoomSession> ConnectAsync(CancellationToken ct = default) =>
                ConvaiOperation<RoomSession>.Succeeded(RoomSession.Empty);

            public IConvaiOperation<Unit> DisconnectAsync(CancellationToken ct = default)
            {
                _onDisconnect();
                return ConvaiOperation<Unit>.Succeeded(Unit.Value);
            }

            public IConvaiOperation<RoomSession> CreateRoomAsync(
                RoomCreateOptions options,
                CancellationToken ct = default) =>
                ConvaiOperation<RoomSession>.Succeeded(RoomSession.Empty);

            public IConvaiOperation<JoinResult> JoinRoomAsync(
                string roomCode,
                CancellationToken ct = default) =>
                ConvaiOperation<JoinResult>.Succeeded(default);
        }
    }
}
