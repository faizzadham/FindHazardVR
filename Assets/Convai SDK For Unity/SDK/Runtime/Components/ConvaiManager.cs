using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Composition;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Modules;
using Convai.Runtime.Facades;
using Convai.Runtime.Logging;
using Convai.Runtime.Presentation.Services;
using Convai.Runtime.Room;
using Convai.Shared.Abstractions;
using Convai.Shared.Interfaces;
using UnityEngine;
using UnityEngine.Serialization;

namespace Convai.Runtime.Components
{
    public enum ConvaiManagerConversationMode
    {
        UseRoomDefaults = 0,
        HandsFree = 1,
        PushToTalk = 2
    }

    /// <summary>
    ///     Main Unity entrypoint for the active Convai runtime.
    /// </summary>
    [AddComponentMenu("Convai/Convai Manager")]
    [DefaultExecutionOrder(-1100)]
    [DisallowMultipleComponent]
    public partial class ConvaiManager : MonoBehaviour, IRoomOwnershipProvider
    {

        // Serialized configuration
        [Header("Manager Settings")]
        [SerializeField]
        [HideInInspector]
        [Tooltip("Use scene-specific manager instances instead of persisting across scene loads.")]
        private bool _sceneSpecificManager = true;

        [SerializeField] [HideInInspector] [Tooltip("Log manager setup and lifecycle events.")]
        private bool _debugLogging;

        [SerializeField]
        [HideInInspector]
        [Tooltip("On WebGL, use the next non-UI scene click after connection to start audio.")]
        private bool _enableVoiceOnFirstSceneClickAfterConnectInWebGL = true;

        [Header("Bootstrap Settings")]
        [FormerlySerializedAs("eagerInitialization")]
        [SerializeField]
        [HideInInspector]
        [Tooltip("Eagerly initialize runtime services at startup.")]
        private bool _eagerInitialization = true;

        [SerializeField] [HideInInspector] [Tooltip("Register the default notification service.")]
        private bool _registerDefaultNotificationService = true;

        [Header("Injection Settings")]
        [SerializeField]
        [HideInInspector]
        [Tooltip("Throw if required runtime dependencies are missing during setup.")]
        private bool _strictMode;

        [SerializeField] [HideInInspector] [Tooltip("Automatically inject supported scene components after bootstrap.")]
        private bool _autoInject = true;

        [Header("Conversation Setup")]
        [SerializeField]
        [HideInInspector]
        [Tooltip(
            "Legacy hidden conversation setup retained only so older scenes can migrate their settings into ConvaiRoomManager.")]
        private ConvaiManagerConversationMode _conversationMode = ConvaiManagerConversationMode.UseRoomDefaults;

        [SerializeField] [HideInInspector] [FormerlySerializedAs("_pushToTalkKey")]
        private KeyCode _legacyPushToTalkKey = KeyCode.T;

        [SerializeField]
        [HideInInspector]
        [Tooltip("If true, pressing push-to-talk while the bot is speaking interrupts the bot before opening the mic.")]
        private bool _interruptBotOnPress = true;

        [SerializeField]
        [HideInInspector]
        [Tooltip("If true and interrupt is disabled, push-to-talk input is rejected until the current turn completes.")]
        private bool _requireTurnCompletionBeforeNextPress = true;

        [SerializeField]
        [HideInInspector]
        [Min(0)]
        [Tooltip("Fallback timeout used by the built-in push-to-talk flow to clear awaiting-completion state.")]
        private int _pushToTalkTurnCompletionTimeoutMs = PushToTalkPolicy.DefaultTurnCompletionTimeoutMs;

        // Managed components
        [Header("Managed Components")] [SerializeField] [HideInInspector]
        private ConvaiRoomManager _roomManager;

        [SerializeField] [HideInInspector] private ConvaiPushToTalkController _managedPushToTalkController;

        [SerializeField] [HideInInspector] private bool _managedPushToTalkControllerAutoCreated;

        [SerializeField]
        [HideInInspector]
        [Tooltip("Optional scene installer for additional setup.")]
        private ConvaiSceneInstaller _sceneInstaller;

        [SerializeField]
        [HideInInspector]
        [Tooltip("Optional explicit player reference.")]
        private ConvaiPlayer _explicitPlayer;

        [SerializeField] [HideInInspector] [Tooltip("Optional explicit character references.")]
        private List<ConvaiCharacter> _explicitCharacters = new();

        [SerializeField]
        [HideInInspector]
        [Tooltip("Use an explicit subset of owned characters for the next room connection.")]
        private bool _useCharacterConnectionSelection;

        [SerializeField]
        [HideInInspector]
        [Tooltip("Characters included when explicit room connection selection is enabled.")]
        private List<ConvaiCharacter> _includedCharacters = new();

        [SerializeField] [HideInInspector] [Tooltip("Optional explicit active conversation target.")]
        private ConvaiCharacter _explicitConversationTarget;

        // Host (composition root)
        private ConvaiRuntimeHost _host;
        private bool _lastManagedPushToTalkHeld;
        private ConvaiPushToTalkInputReader _pushToTalkInputReader;
        private readonly List<ConvaiCharacter> _runtimeOwnedCharacters = new();
        private bool _lateCharacterRefreshPending;

        // Public Properties

        /// <summary>Gets whether the Convai SDK services have been bootstrapped.</summary>
        public static bool IsBootstrapped => ActiveManager?._host?.IsBootstrapped ?? false;

        /// <summary>True when bootstrap and runtime EventHub are initialized.</summary>
        public bool IsInitialized => _host?.IsBootstrapped == true && TryGetEventHub(out _);

        /// <summary>True when currently connected to a Convai room.</summary>
        public bool IsConnected => _roomManager != null && _roomManager.IsConnected;

        /// <summary>Owned characters.</summary>
        public IReadOnlyList<ConvaiCharacter> Characters => _host?.Characters ?? Array.Empty<ConvaiCharacter>();

        /// <summary>Whether this manager uses an explicit subset for the next room connection.</summary>
        public bool UsesCharacterConnectionSelection => _useCharacterConnectionSelection;

        /// <summary>Owned player.</summary>
        public ConvaiPlayer Player => _host?.Player;

        /// <summary>Active conversation target.</summary>
        public ConvaiCharacter ActiveConversationCharacter => _host?.ActiveConversationCharacter;

        /// <summary>Currently active manager singleton.</summary>
        public static ConvaiManager ActiveManager { get; private set; }
        internal bool ShouldPersistAcrossScenes => !_sceneSpecificManager;

        public ConvaiManagerConversationMode ConversationMode => _conversationMode;
        public KeyCode PushToTalkKey => _roomManager != null ? _roomManager.PushToTalkKey : _legacyPushToTalkKey;
        public ConversationInputMode ActiveConversationInputMode =>
            _roomManager?.ActiveConversationInputMode ?? ConversationInputMode.HandsFree;

        /// <summary>Canonical typed reactive event facade for code-driven integrations.</summary>
        public ConvaiEvents Events
        {
            get
            {
                _host?.EnsureFacades(_roomManager);
                ConvaiEvents events = _host?.Events;
                if (events == null)
                {
                    throw new InvalidOperationException(
                        "[ConvaiManager] Events not available. Ensure initialization is complete.");
                }

                return events;
            }
        }

        /// <summary>
        ///     The event facade if it already exists, or <c>null</c>. Unlike <see cref="Events" />
        ///     this neither builds the facades nor throws when initialization has not finished, so
        ///     it is safe to consult from diagnostic paths that must never affect delivery of the
        ///     very events they are reporting on (see
        ///     <c>ConvaiCharacter.ReportUnrunActionsOnce</c>).
        /// </summary>
        internal ConvaiEvents EventsOrNull => _host?.Events;

        /// <summary>High-level audio facade.</summary>
        public ConvaiAudio Audio
        {
            get
            {
                _host?.EnsureFacades(_roomManager);
                ConvaiAudio audio = _host?.Audio;
                if (audio == null)
                {
                    throw new InvalidOperationException(
                        "[ConvaiManager] Audio not available. Ensure initialization is complete.");
                }

                return audio;
            }
        }

        /// <summary>Canonical transcript state and history facade.</summary>
        public ConvaiTranscripts Transcripts
        {
            get
            {
                if (!TryGetTranscripts(out ConvaiTranscripts transcripts))
                {
                    throw new InvalidOperationException(
                        "[ConvaiManager] Transcripts not available. Ensure initialization is complete.");
                }

                return transcripts;
            }
        }

        /// <summary>Tries to resolve canonical transcript state without throwing during initialization or teardown.</summary>
        public bool TryGetTranscripts(out ConvaiTranscripts transcripts)
        {
            _host?.EnsureFacades(_roomManager);
            transcripts = _host?.Transcripts;
            return transcripts != null;
        }

        // IRoomOwnershipProvider

        RoomOwnershipSnapshot IRoomOwnershipProvider.CaptureOwnership() =>
            PreferLookedAtInitialCharacter(
                _host?.CaptureOwnership() ?? new RoomOwnershipSnapshot(null, null));

        // Public Events

        /// <summary>Raised when room connection succeeds.</summary>
        public event Action OnConnected;

        /// <summary>Raised when room disconnects.</summary>
        public event Action OnDisconnected;

        /// <summary>Raised on operational error.</summary>
        public event Action<SessionError> OnError;

        // Public API — Room Operations

        public IConvaiOperation<RoomSession> ConnectAsync(CancellationToken cancellationToken = default) =>
            ConvaiOperation<RoomSession>.FromTask(ConnectAsyncCore(cancellationToken));

        public IConvaiOperation<RoomSession> ConnectAsync(
            RoomSessionConnectOptions options,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<RoomSession>.FromTask(ConnectAsyncCore(options, cancellationToken));

        /// <summary>
        ///     Connects using a caller-supplied Convai auth token and caller-supplied end-user identity.
        /// </summary>
        /// <remarks>
        ///     Select Auth Token mode in Convai Project Settings before using this path. The token is used only for
        ///     this connection attempt and is not persisted. <paramref name="endUserName" /> is sent as the
        ///     <c>name</c> value in <c>end_user_metadata</c>.
        /// </remarks>
        /// <param name="authToken">A short-lived Convai <c>apiAuthToken</c>.</param>
        /// <param name="endUserId">A stable, non-secret ID for the signed-in end user.</param>
        /// <param name="endUserName">The end user's display name.</param>
        /// <param name="cancellationToken">Token used to cancel the connection attempt.</param>
        /// <returns>Operation resolving with the established room session.</returns>
        public IConvaiOperation<RoomSession> ConnectWithAuthTokenAsync(
            string authToken,
            string endUserId,
            string endUserName,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<RoomSession>.FromTask(ConnectWithAuthTokenAsyncCore(
                authToken,
                endUserId,
                endUserName,
                cancellationToken));

        private async Task<RoomSession> ConnectAsyncCore(CancellationToken cancellationToken)
        {
            if (_roomManager == null)
            {
                var exception = new ConvaiOperationException(
                    SessionErrorCodes.ConnectionFailed,
                    "ConvaiRoomManager not available.");
                InvokeError(exception.Message, exception);
                throw exception;
            }

            return await _roomManager.ConnectAsync(cancellationToken).AsTask();
        }

        private async Task<RoomSession> ConnectAsyncCore(
            RoomSessionConnectOptions options,
            CancellationToken cancellationToken)
        {
            if (_roomManager == null)
            {
                var exception = new ConvaiOperationException(
                    SessionErrorCodes.ConnectionFailed,
                    "ConvaiRoomManager not available.");
                InvokeError(exception.Message, exception);
                throw exception;
            }

            return await _roomManager.ConnectAsync(options, cancellationToken).AsTask();
        }

        private async Task<RoomSession> ConnectWithAuthTokenAsyncCore(
            string authToken,
            string endUserId,
            string endUserName,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(authToken))
                throw new ConvaiOperationException(
                    SessionErrorCodes.ConnectionInvalidToken,
                    "A non-empty Convai auth token is required.");

            if (string.IsNullOrWhiteSpace(endUserId))
                throw new ConvaiOperationException(
                    SessionErrorCodes.ConnectionBadRequest,
                    "A non-empty end-user ID is required.");

            if (string.IsNullOrWhiteSpace(endUserName))
                throw new ConvaiOperationException(
                    SessionErrorCodes.ConnectionBadRequest,
                    "A non-empty end-user name is required.");

            if (_roomManager == null)
            {
                var exception = new ConvaiOperationException(
                    SessionErrorCodes.ConnectionFailed,
                    "ConvaiRoomManager not available.");
                InvokeError(exception.Message, exception);
                throw exception;
            }

            var options = new RoomSessionConnectOptions
            {
                TurnTaking = _roomManager.EffectiveTurnTakingOptions,
                EndUserId = endUserId.Trim(),
                EndUserMetadata = new Dictionary<string, object>
                {
                    ["name"] = endUserName.Trim()
                }
            };
            options.SetExplicitAuthToken(authToken);
            authToken = null;

            IConvaiOperation<RoomSession> operation;
            try
            {
                // ConvaiRoomManager clones connect options synchronously. Clear the caller-side copy immediately
                // instead of retaining the token while the network operation is in flight.
                operation = _roomManager.ConnectAsync(options, cancellationToken);
            }
            finally
            {
                options.ClearExplicitAuthToken();
            }

            return await operation.AsTask();
        }

        public IConvaiOperation<Unit> DisconnectAsync(CancellationToken cancellationToken = default) =>
            ConvaiOperation<Unit>.FromTask(DisconnectAsyncCore(cancellationToken));

        public IConvaiOperation<Unit> SetConversationInputModeAsync(
            ConversationInputMode mode,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<Unit>.FromTask(SetConversationInputModeAsyncCore(mode, cancellationToken));

        private async Task<Unit> DisconnectAsyncCore(CancellationToken cancellationToken)
        {
            if (_roomManager == null) return Unit.Value;
            try { await _roomManager.DisconnectAsync(cancellationToken: cancellationToken); }
            catch (Exception ex) { InvokeError($"Disconnect failed: {ex.Message}", ex); }

            return Unit.Value;
        }

        private async Task<Unit> SetConversationInputModeAsyncCore(
            ConversationInputMode mode,
            CancellationToken cancellationToken)
        {
            if (_roomManager == null)
            {
                var exception = new ConvaiOperationException(
                    SessionErrorCodes.ConnectionFailed,
                    "ConvaiRoomManager not available.");
                InvokeError(exception.Message, exception);
                throw exception;
            }

            return await _roomManager.SetConversationInputModeAsync(mode, cancellationToken).AsTask();
        }

        public void StartListening() => _roomManager?.StartListening();

        public bool ToggleMicMute() => _roomManager?.ToggleMicMute() ?? false;

        public void EnableAudioAndStartListening() => _roomManager?.EnableAudioAndStartListening();

        // Public API — Ownership

        public void RefreshReferences() =>
            RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);

        public void SetExplicitPlayer(ConvaiPlayer player)
        {
            _explicitPlayer = player;
            RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);
        }

        public void SetExplicitCharacters(IEnumerable<ConvaiCharacter> characters)
        {
            _runtimeOwnedCharacters.Clear();
            _explicitCharacters.Clear();
            if (characters != null)
            {
                foreach (ConvaiCharacter c in characters)
                {
                    if (c != null && !_explicitCharacters.Contains(c))
                        _explicitCharacters.Add(c);
                }
            }

            RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);
        }

        /// <summary>
        ///     Selects the owned characters included in the next room connection. Passing an empty
        ///     collection intentionally leaves the next connection without a valid character roster.
        /// </summary>
        /// <remarks>
        ///     Existing scenes remain in include-all mode until this method or the Inspector changes
        ///     the selection. Changes made while connected take effect on the next reconnect.
        /// </remarks>
        public void SetCharactersToConnect(IEnumerable<ConvaiCharacter> characters)
        {
            _useCharacterConnectionSelection = true;
            _includedCharacters.Clear();
            if (characters != null)
            {
                foreach (ConvaiCharacter character in characters)
                {
                    if (character != null && !_includedCharacters.Contains(character))
                        _includedCharacters.Add(character);
                }
            }

            RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);
        }

        /// <summary>Restores the default behavior where every owned scene character is included.</summary>
        public void UseAllCharactersForConnection()
        {
            _useCharacterConnectionSelection = false;
            _includedCharacters.Clear();
            RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);
        }

        /// <summary>
        ///     The explicit room selection, or empty while every active character is included.
        /// </summary>
        /// <remarks>
        ///     Read this before calling <see cref="SetCharactersToConnect" />: the selection is an
        ///     exact set rather than a list to append to, so a caller that means "and also this one"
        ///     has to start from what is already there. Empty is not the same as "nobody" — check
        ///     <see cref="UsesCharacterConnectionSelection" /> first.
        /// </remarks>
        public IReadOnlyList<ConvaiCharacter> CharactersToConnect => _includedCharacters;

        /// <summary>
        ///     The character the room opens on, or <c>null</c> when nothing has been chosen.
        /// </summary>
        /// <remarks>
        ///     The read half of <see cref="SetInitialCharacter" />. <c>null</c> is the ordinary case
        ///     and not a fault: the room then opens on the first connectable character in scene
        ///     order. Either way this chooses only who takes the <i>first</i> turn — under
        ///     <see cref="Convai.Runtime.Conversation.ConversationTargetingMode.LookAt" /> and
        ///     <see cref="Convai.Runtime.Conversation.ConversationTargetingMode.Proximity" /> the conversation is
        ///     re-evaluated from the moment the room is ready and moves off this character as soon
        ///     as another one scores better.
        /// </remarks>
        public ConvaiCharacter InitialCharacter => _explicitConversationTarget;

        /// <summary>Returns whether a character is selected for the next room connection.</summary>
        public bool IsCharacterSelectedForConnection(ConvaiCharacter character) =>
            ConvaiRuntimeHost.IsCharacterSelectedForConnection(
                character,
                _useCharacterConnectionSelection,
                _includedCharacters);

        /// <summary>
        ///     Chooses which character speaks first when the room connects.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This is setup, not steering. It decides where a conversation <i>starts</i>; while a
        ///         room is connected, changing it queues an ownership reconnect rather than moving the
        ///         conversation. To move the conversation in a live room, use
        ///         <see cref="TalkTo" />.
        ///     </para>
        ///     <para>
        ///         Leaving it unset is normal: the first character in scene order takes the first turn.
        ///     </para>
        /// </remarks>
        public void SetInitialCharacter(ConvaiCharacter character)
        {
            _explicitConversationTarget = character;
            RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);
        }

        // Component Registration (called by modules)

        public void RegisterModule(IConvaiModule module)
        {
            _host?.RegisterModule(module);

            ConvaiRuntime runtime = ConvaiRuntime;
            if (module != null && runtime?.State == RuntimeState.Running)
                _ = StartRuntimeModuleAsync(runtime, module);
        }

        public void UnregisterModule(IConvaiModule module)
        {
            _host?.UnregisterModule(module);

            ConvaiRuntime runtime = ConvaiRuntime;
            if (module != null && runtime != null &&
                runtime.State is RuntimeState.Running or RuntimeState.Paused)
                _ = StopRuntimeModuleAsync(runtime, module);
        }

        private async Task StartRuntimeModuleAsync(ConvaiRuntime runtime, IConvaiModule module)
        {
            try
            {
                // Registration commonly happens from a component's Awake callback.
                // Defer lifecycle startup so Awake can finish initializing that component.
                await Task.Yield();
                if (runtime.State != RuntimeState.Running) return;

                // A module can arrive as part of a character prefab instantiated after room startup.
                // Retain its owning character in addition to installer/explicit ownership, then
                // inject before composing the room or starting the module.
                TrackRuntimeModuleOwner(module);
                RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);

                await runtime.AddAndStartModuleAsync(module);
            }
            catch (Exception exception)
            {
                ConvaiLogger.Exception(
                    new InvalidOperationException(
                        $"Failed to start runtime module '{module.ModuleId}'.",
                        exception),
                    LogCategory.Bootstrap);
            }
        }

        private static async Task StopRuntimeModuleAsync(ConvaiRuntime runtime, IConvaiModule module)
        {
            try
            {
                await runtime.StopAndRemoveModuleAsync(module);
            }
            catch (Exception exception)
            {
                ConvaiLogger.Warning(
                    $"Failed to stop runtime module '{module.ModuleId}': {exception.Message}",
                    LogCategory.Bootstrap);
            }
        }

        // Late character registration (called by ConvaiCharacter.OnEnable/OnDisable/OnDestroy)

        /// <summary>
        ///     Called by a character whose GameObject became enabled. A character spawned or enabled
        ///     while the runtime is live was never part of the startup composition, so ownership is
        ///     refreshed the way the late-module path does it; during scene startup this is a no-op
        ///     because the manager's own Start-time refresh observes the whole scene anyway
        ///     (see <see cref="LateCharacterPlanner" />).
        /// </summary>
        internal void NotifyCharacterEnabled(ConvaiCharacter character) =>
            HandleLateCharacterChange(character, characterBecameEnabled: true);

        /// <summary>
        ///     Called by a character whose GameObject became disabled, so a connected room can refresh
        ///     activity and targeting without waiting for a reconnect. Ownership and the room seat are
        ///     kept because the same character can be enabled again.
        /// </summary>
        internal void NotifyCharacterDisabled(ConvaiCharacter character) =>
            HandleLateCharacterChange(character, characterBecameEnabled: false);

        /// <summary>
        ///     Called after Unity's disable callback when a character is actually being destroyed.
        ///     Destruction removes ownership rather than retaining a temporary disabled seat.
        /// </summary>
        internal void NotifyCharacterDestroyed(ConvaiCharacter character) =>
            HandleLateCharacterChange(
                character,
                characterBecameEnabled: false,
                characterWasDestroyed: true);

        /// <summary>
        ///     True while a character that just appeared or disappeared is still waiting to be folded
        ///     into ownership.
        /// </summary>
        /// <remarks>
        ///     A character enabled at runtime starts its own auto-connect, and connecting composes the
        ///     room from ownership as it stands. Both are deferred by a frame, and the connect used to
        ///     win: the room was composed while the character was still absent from the connectable
        ///     roster, so it resolved no conversation target and the connection was refused with
        ///     "connection preparation failed" — on the very character that had just appeared.
        ///     <see cref="ConvaiCharacter" /> waits on this rather than guessing a frame count.
        /// </remarks>
        internal bool HasPendingLateCharacterRefresh => _lateCharacterRefreshPending;

        private void HandleLateCharacterChange(
            ConvaiCharacter character,
            bool characterBecameEnabled,
            bool characterWasDestroyed = false)
        {
            ConvaiRuntime runtime = ConvaiRuntime;
            LateCharacterAction action = LateCharacterPlanner.Plan(
                character != null,
                runtime != null,
                runtime?.State ?? RuntimeState.Created,
                character != null && character.IsInjected,
                characterBecameEnabled,
                characterWasDestroyed);

            if (action == LateCharacterAction.None) return;

            if (action == LateCharacterAction.AdoptAndRefresh &&
                !_runtimeOwnedCharacters.Contains(character))
                _runtimeOwnedCharacters.Add(character);

            // Every character enabled or disabled in the same frame is absorbed by one refresh.
            if (_lateCharacterRefreshPending) return;
            _lateCharacterRefreshPending = true;
            _ = RunLateCharacterRefreshAsync();
        }

        private async Task RunLateCharacterRefreshAsync()
        {
            try
            {
                // Deferred one frame for the same reason the late-module path defers: the
                // character's own Awake/OnEnable chain — and any sibling spawns this frame —
                // finish initializing before ownership is recomposed.
                await Task.Yield();
                _lateCharacterRefreshPending = false;

                if (this == null) return;

                ConvaiRuntime runtime = ConvaiRuntime;
                if (runtime == null || runtime.State is RuntimeState.Created or RuntimeState.Stopping
                    or RuntimeState.Stopped or RuntimeState.Disposed)
                    return;

                RefreshOwnedAgentState(injectOwnedAgentsBeforeNotification: _autoInject);
            }
            catch (Exception exception)
            {
                _lateCharacterRefreshPending = false;
                ConvaiLogger.Exception(
                    new InvalidOperationException(
                        "Failed to refresh ownership for a character enabled or disabled at runtime.",
                        exception),
                    LogCategory.SDK);
            }
        }

        // Typed Service Accessors (delegate to host)

        public bool TryGetEventHub(out IEventHub eventHub) =>
            _host?.TryGetEventHub(out eventHub) ?? Out(out eventHub);

        public bool TryGetRoomConnectionService(out IConvaiRoomConnectionService service) =>
            _host?.TryGetRoomConnectionService(out service) ?? Out(out service);

        public bool TryGetRoomAudioService(out IConvaiRoomAudioService service) =>
            _host?.TryGetRoomAudioService(out service) ?? Out(out service);

        public bool TryGetAgentRegistry(out IAgentRegistry registry) =>
            _host?.TryGetAgentRegistry(out registry) ?? Out(out registry);

        public bool TryGetSettingsPanelController(out IConvaiSettingsPanelController controller) =>
            _host?.TryGetSettingsPanelController(out controller) ?? Out(out controller);

        public bool TryGetRuntimeSettingsService(out IConvaiRuntimeSettingsService service) =>
            _host?.TryGetRuntimeSettingsService(out service) ?? Out(out service);

        public bool TryGetMicrophoneDeviceService(out IMicrophoneDeviceService service) =>
            _host?.TryGetMicrophoneDeviceService(out service) ?? Out(out service);

        public bool TryGetPermissionService(out IConvaiPermissionService service) =>
            _host?.TryGetPermissionService(out service) ?? Out(out service);

        public bool TryGetNotificationService(out IConvaiNotificationService service) =>
            _host?.TryGetNotificationService(out service) ?? Out(out service);

        public bool TryGetPlayerInputService(out IPlayerInputService service) =>
            _host?.TryGetPlayerInputService(out service) ?? Out(out service);

        public bool TryGetVisibleCharacterService(out IVisibleCharacterService service) =>
            _host?.TryGetVisibleCharacterService(out service) ?? Out(out service);

        public bool TryGetTransportProvider(out ITransportProvider provider) =>
            _host?.TryGetTransportProvider(out provider) ?? Out(out provider);

        internal bool TryGetDiagnosticsService(out IConvaiRuntimeDiagnosticsService service) =>
            _host?.TryGetDiagnosticsService(out service) ?? Out(out service);

#if UNITY_INCLUDE_TESTS
        /// <summary>
        ///     Test-only accessor: gets the ConvaiRuntimeHost for registering test overrides.
        /// </summary>
        internal ConvaiRuntimeHost GetOrCreateHost()
        {
            if (_host == null)
            {
                if (ConvaiRuntime == null) BuildRuntime();
                if (ConvaiRuntime != null)
                {
                    _host = new ConvaiRuntimeHost(ConvaiRuntime, _debugLogging, _strictMode,
                        _registerDefaultNotificationService, _eagerInitialization);
                }
            }

            return _host;
        }
#endif

        // Private Helpers

        private void RefreshOwnedAgentState(bool injectOwnedAgentsBeforeNotification = false)
        {
            RemoveDestroyedRuntimeOwners();
            _host?.RefreshOwnedAgentState(
                _sceneInstaller, _explicitPlayer, _explicitCharacters,
                _explicitConversationTarget, this,
                _useCharacterConnectionSelection,
                _includedCharacters,
                _runtimeOwnedCharacters,
                injectOwnedAgentsBeforeNotification);
            _host?.UpdateDiagnosticsRegistration(this, _roomManager);
        }

        private void TrackRuntimeModuleOwner(IConvaiModule module)
        {
            if (module is not Component component) return;

            ConvaiCharacter owner = component.GetComponentInParent<ConvaiCharacter>(true);
            if (owner != null && !_runtimeOwnedCharacters.Contains(owner))
                _runtimeOwnedCharacters.Add(owner);
        }

        private void RemoveDestroyedRuntimeOwners()
        {
            for (int i = _runtimeOwnedCharacters.Count - 1; i >= 0; i--)
            {
                if (_runtimeOwnedCharacters[i] == null)
                    _runtimeOwnedCharacters.RemoveAt(i);
            }
        }

        internal static TurnTakingOptions CreateConversationModeTurnTakingOverride(
            ConvaiManagerConversationMode conversationMode,
            bool interruptBotOnPress,
            bool requireTurnCompletionBeforeNextPress,
            int pushToTalkTurnCompletionTimeoutMs)
        {
            switch (conversationMode)
            {
                case ConvaiManagerConversationMode.HandsFree:
                    return TurnTakingOptions.CreateHandsFreeDefault();

                case ConvaiManagerConversationMode.PushToTalk:
                    return new TurnTakingOptions
                    {
                        Mode = ConversationInputMode.PushToTalk,
                        TurnDetection = TurnDetectionMode.UseDefault,
                        InitialServerStt = ServerSttInitialState.UseDefault,
                        LocalAudioPolicy =
                            new LocalAudioPolicy
                            {
                                StartMutedInPushToTalk = true,
                                PushToTalkStartupMode = PushToTalkMicStartupMode.PrewarmMuted
                            },
                        PushToTalkPolicy = new PushToTalkPolicy
                        {
                            EnableServerSttToggle = true,
                            InterruptBotOnPress = interruptBotOnPress,
                            RequireTurnCompletionBeforeNextPress = requireTurnCompletionBeforeNextPress,
                            TurnCompletionTimeoutMs = Math.Max(0, pushToTalkTurnCompletionTimeoutMs),
                            AllowSpeechStoppedFallbackAfterSpeechStart = true
                        }
                    };

                case ConvaiManagerConversationMode.UseRoomDefaults:
                default:
                    return null;
            }
        }

        internal void EnsureConversationModeComponents()
        {
            if (ResolveEffectiveConversationInputMode() != ConversationInputMode.PushToTalk)
            {
                ReleaseManagedPushToTalkIfNeeded();
                return;
            }

            EnsureManagedPushToTalkController();
        }

        internal void HandleConversationModeInput()
        {
            if (_roomManager != null && _roomManager.IsConversationInputModeTransitionInProgress)
                return;

            if (ResolveEffectiveConversationInputMode() != ConversationInputMode.PushToTalk)
            {
                if (_lastManagedPushToTalkHeld)
                    SetManagedPushToTalkHeld(false);

                ReleaseManagedPushToTalkIfNeeded();
                return;
            }

            EnsureManagedPushToTalkController();
            if (_managedPushToTalkController == null || !_managedPushToTalkController.enabled)
                return;

            bool isHeld = IsPushToTalkHeld();
            if (isHeld == _lastManagedPushToTalkHeld)
                return;

            SetManagedPushToTalkHeld(isHeld);
        }

        private ConversationInputMode ResolveEffectiveConversationInputMode()
        {
            if (_roomManager == null)
                return ConversationInputMode.HandsFree;

            return _roomManager.ActiveConversationInputMode;
        }

        private void EnsureManagedPushToTalkController()
        {
            if (_managedPushToTalkController == null)
            {
                _managedPushToTalkController = gameObject.GetComponent<ConvaiPushToTalkController>();
                if (_managedPushToTalkController == null)
                {
                    _managedPushToTalkController = gameObject.AddComponent<ConvaiPushToTalkController>();
                    _managedPushToTalkControllerAutoCreated = true;
                }
            }

            if (_managedPushToTalkController != null)
                _managedPushToTalkController.enabled = true;
        }

        private void ReleaseManagedPushToTalkIfNeeded()
        {
            if (_managedPushToTalkController == null)
                return;

            if (_lastManagedPushToTalkHeld)
                SetManagedPushToTalkHeld(false);

            if (_managedPushToTalkControllerAutoCreated)
                _managedPushToTalkController.enabled = false;
        }

        private void SetManagedPushToTalkHeld(bool isHeld)
        {
            _lastManagedPushToTalkHeld = isHeld;
            if (_managedPushToTalkController == null || !_managedPushToTalkController.enabled)
                return;

            if (_managedPushToTalkController.SetPressed(isHeld))
                return;

            if (isHeld && _debugLogging)
            {
                string blockedReason = string.IsNullOrWhiteSpace(_managedPushToTalkController.BlockedReason)
                    ? "unknown"
                    : _managedPushToTalkController.BlockedReason;
                ConvaiLogger.Debug(
                    $"Push-to-talk press rejected: {blockedReason}",
                    LogCategory.SDK);
            }
        }

        private bool IsPushToTalkHeld()
        {
            _pushToTalkInputReader ??= new ConvaiPushToTalkInputReader(IsFallbackPushToTalkInputHeld);
            return _pushToTalkInputReader.IsHeld(PushToTalkKey);
        }

        /// <summary>
        ///     Keyboard fallback for push-to-talk when no XR controller is driving it. The shared
        ///     helper covers both input backends and caches its Input System lookups, so holding
        ///     the key costs no allocation per frame.
        /// </summary>
        private static bool IsFallbackPushToTalkInputHeld(KeyCode keyCode) =>
            ConvaiInputSystem.IsKeyHeld(keyCode);

        private void InvokeError(string message, Exception ex = null)
        {
            OnError?.Invoke(SessionError.Create(
                SessionErrorCodes.ConnectionFailed,
                message,
                exception: ex,
                stage: SessionErrorStage.Runtime));
        }

        /// <summary>Helper to set out param to null and return false.</summary>
        private static bool Out<T>(out T value) where T : class
        {
            value = null;
            return false;
        }
    }
}
