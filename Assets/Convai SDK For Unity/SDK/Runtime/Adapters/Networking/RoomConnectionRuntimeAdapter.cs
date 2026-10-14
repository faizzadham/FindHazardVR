using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Abstractions;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Transport;
using Convai.RestAPI;
using Convai.RestAPI.Services;
using Convai.Runtime.Actions;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Policies;
using Convai.Runtime.Emotion;
using Convai.Runtime.Networking.Media;
using Convai.Runtime.Room;
using Convai.Shared.Actions;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using Newtonsoft.Json;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Runtime.Adapters.Networking
{
    internal sealed class RoomConnectionRuntimeAdapter : IRoomConnectionRuntimeAdapter
    {
        private readonly Func<IConvaiCharacterAgent> _activeCharacterProvider;
        private readonly Func<IReadOnlyList<IConvaiCharacterAgent>> _characterListProvider;
        private readonly IAgentRegistry _agentRegistry;
        private readonly Func<bool> _canConnectProvider;
        private readonly Func<TurnTakingOptions> _configuredTurnTakingOptionsProvider;
        private readonly Func<ConnectionContext> _connectionContextProvider;
        private readonly object _connectionStateLock = new();
        private readonly Func<ConvaiConnectionType> _connectionTypeProvider;
        private readonly Func<ICredentialProvider> _credentialProvider;
        private readonly Func<RoomSessionConnectOptions> _consumePendingConnectOptions;
        private readonly Func<IConvaiRoomController> _controllerProvider;
        private readonly Func<string> _coreServerUrlProvider;
        private readonly Func<string> _endUserIdProvider;
        private readonly Func<IReadOnlyDictionary<string, object>> _endUserMetadataProvider;
        private readonly Func<UserVadSettings> _configuredUserVadSettingsProvider;
        private readonly Func<RoomRespondModesConfig> _respondModesProvider;
        private readonly Func<RoomVisionInputConfig> _visionInputConfigProvider;
        private readonly Func<SessionState> _currentStateProvider;
        private readonly RoomDisconnectRuntimeAdapter _disconnectRuntimeAdapter;
        private readonly Func<bool> _isConnectedProvider;
        private readonly ILogger _logger;
        private readonly Action<bool> _onConnectAttemptStarted;
        private readonly Func<bool> _prepareConnectionFunc;
        private readonly Action<ConnectionFailure> _recordConnectionFailure;
        private readonly Action<string, string, string, string> _recordConnectionSuccess;
        private readonly Func<ReconnectPolicy> _resolveReconnectPolicy;
        private readonly Func<ISessionPersistence> _sessionPersistenceProvider;
        private readonly Action<ConnectionContext> _setConnectionContext;
        private readonly Action<TurnTakingOptions, ResolvedTurnTakingOptions> _prepareSessionTurnTakingState;
        private readonly Action<ResolvedTurnTakingOptions> _setCurrentResolvedTurnTakingOptions;
        private readonly Action<ReconnectPolicy> _setReconnectPolicy;
        private readonly TaskCompletionSource<bool> _startCompletedTcs = new();
        private readonly Func<int> _startWaitTimeoutProvider;
        private readonly Action<SessionState, SessionError?> _updateSessionState;
        private TaskCompletionSource<bool> _connectionStateTcs;
        private string _lastSuccessfulIdentityScope;
        private readonly Dictionary<string, string> _characterIdentityScopes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _characterSessionReferences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sessionCharacterReferences = new(StringComparer.Ordinal);

        public RoomConnectionRuntimeAdapter(
            Func<SessionState> currentStateProvider,
            Func<bool> isConnectedProvider,
            Func<bool> canConnectProvider,
            Func<int> startWaitTimeoutProvider,
            Func<bool> prepareConnectionFunc,
            Func<IConvaiCharacterAgent> activeCharacterProvider,
            Func<IReadOnlyList<IConvaiCharacterAgent>> characterListProvider,
            IAgentRegistry agentRegistry,
            Func<ConnectionContext> connectionContextProvider,
            Action<ConnectionContext> setConnectionContext,
            Func<ReconnectPolicy> resolveReconnectPolicy,
            Action<ReconnectPolicy> setReconnectPolicy,
            Action<bool> onConnectAttemptStarted,
            Func<IConvaiRoomController> controllerProvider,
            Func<ConvaiConnectionType> connectionTypeProvider,
            Func<string> coreServerUrlProvider,
            Func<string> endUserIdProvider,
            Func<IReadOnlyDictionary<string, object>> endUserMetadataProvider,
            Func<TurnTakingOptions> configuredTurnTakingOptionsProvider,
            Func<UserVadSettings> configuredUserVadSettingsProvider,
            Func<RoomVisionInputConfig> visionInputConfigProvider,
            Func<RoomRespondModesConfig> respondModesProvider,
            Func<RoomSessionConnectOptions> consumePendingConnectOptions,
            Action<TurnTakingOptions, ResolvedTurnTakingOptions> prepareSessionTurnTakingState,
            Action<ResolvedTurnTakingOptions> setCurrentResolvedTurnTakingOptions,
            Func<ISessionPersistence> sessionPersistenceProvider,
            RoomDisconnectRuntimeAdapter disconnectRuntimeAdapter,
            Action<SessionState, SessionError?> updateSessionState,
            Action<string, string, string, string> recordConnectionSuccess,
            Action<ConnectionFailure> recordConnectionFailure,
            ILogger logger = null,
            Func<ICredentialProvider> credentialProvider = null)
        {
            _currentStateProvider =
                currentStateProvider ?? throw new ArgumentNullException(nameof(currentStateProvider));
            _isConnectedProvider = isConnectedProvider ?? throw new ArgumentNullException(nameof(isConnectedProvider));
            _canConnectProvider = canConnectProvider ?? throw new ArgumentNullException(nameof(canConnectProvider));
            _startWaitTimeoutProvider = startWaitTimeoutProvider ??
                                        throw new ArgumentNullException(nameof(startWaitTimeoutProvider));
            _prepareConnectionFunc =
                prepareConnectionFunc ?? throw new ArgumentNullException(nameof(prepareConnectionFunc));
            _activeCharacterProvider = activeCharacterProvider ??
                                       throw new ArgumentNullException(nameof(activeCharacterProvider));
            _characterListProvider = characterListProvider ??
                                     throw new ArgumentNullException(nameof(characterListProvider));
            _agentRegistry = agentRegistry ?? throw new ArgumentNullException(nameof(agentRegistry));
            _connectionContextProvider = connectionContextProvider ??
                                         throw new ArgumentNullException(nameof(connectionContextProvider));
            _setConnectionContext =
                setConnectionContext ?? throw new ArgumentNullException(nameof(setConnectionContext));
            _resolveReconnectPolicy =
                resolveReconnectPolicy ?? throw new ArgumentNullException(nameof(resolveReconnectPolicy));
            _setReconnectPolicy = setReconnectPolicy ?? throw new ArgumentNullException(nameof(setReconnectPolicy));
            _onConnectAttemptStarted = onConnectAttemptStarted ??
                                       throw new ArgumentNullException(nameof(onConnectAttemptStarted));
            _controllerProvider = controllerProvider ?? throw new ArgumentNullException(nameof(controllerProvider));
            _connectionTypeProvider =
                connectionTypeProvider ?? throw new ArgumentNullException(nameof(connectionTypeProvider));
            _coreServerUrlProvider =
                coreServerUrlProvider ?? throw new ArgumentNullException(nameof(coreServerUrlProvider));
            _endUserIdProvider = endUserIdProvider ?? (() => null);
            _endUserMetadataProvider = endUserMetadataProvider ?? (() => null);
            _configuredTurnTakingOptionsProvider = configuredTurnTakingOptionsProvider ??
                                                   throw new ArgumentNullException(
                                                       nameof(configuredTurnTakingOptionsProvider));
            _configuredUserVadSettingsProvider = configuredUserVadSettingsProvider ??
                                                 throw new ArgumentNullException(
                                                     nameof(configuredUserVadSettingsProvider));
            _visionInputConfigProvider = visionInputConfigProvider ?? (() => null);
            _respondModesProvider = respondModesProvider ?? (() => null);
            _consumePendingConnectOptions = consumePendingConnectOptions ??
                                            throw new ArgumentNullException(nameof(consumePendingConnectOptions));
            _prepareSessionTurnTakingState = prepareSessionTurnTakingState ??
                                             throw new ArgumentNullException(nameof(prepareSessionTurnTakingState));
            _setCurrentResolvedTurnTakingOptions = setCurrentResolvedTurnTakingOptions ??
                                                   throw new ArgumentNullException(
                                                       nameof(setCurrentResolvedTurnTakingOptions));
            _sessionPersistenceProvider = sessionPersistenceProvider ??
                                          throw new ArgumentNullException(nameof(sessionPersistenceProvider));
            _disconnectRuntimeAdapter = disconnectRuntimeAdapter ??
                                        throw new ArgumentNullException(nameof(disconnectRuntimeAdapter));
            _updateSessionState = updateSessionState ?? throw new ArgumentNullException(nameof(updateSessionState));
            _recordConnectionSuccess = recordConnectionSuccess ??
                                       throw new ArgumentNullException(nameof(recordConnectionSuccess));
            _recordConnectionFailure = recordConnectionFailure ??
                                       throw new ArgumentNullException(nameof(recordConnectionFailure));
            _credentialProvider = credentialProvider;
            _logger = logger.WithTag(nameof(RoomConnectionRuntimeAdapter), LogCategory.Transport);
        }

        public event Action<SessionStateChanged> StateChanged;

        public SessionState CurrentState => _currentStateProvider();
        public bool IsConnected => _isConnectedProvider();
        public string CurrentRoomName => _controllerProvider()?.RoomName ?? string.Empty;
        public string CurrentSessionId => _controllerProvider()?.SessionID ?? string.Empty;
        public bool CanConnect => _canConnectProvider();
        public bool HasStarted { get; private set; }

        public async Task<bool> WaitForStartCompletionAsync(CancellationToken ct)
        {
            _logger?.Info("Waiting for room manager startup to complete...");

            int timeoutMs = _startWaitTimeoutProvider();
            using var timeoutCts = ConvaiTimers.CreateTimeoutSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await ConvaiTimers.WhenAny(_startCompletedTcs.Task, Task.Delay(Timeout.Infinite, linkedCts.Token));
            ct.ThrowIfCancellationRequested();

            // Re-check after timeout in case SignalStartCompleted was called in the same moment
            if (!HasStarted)
                _logger?.Error("ConnectAsync timed out waiting for startup.");

            return HasStarted;
        }

        public bool PrepareConnection() => _prepareConnectionFunc();

        public async Task<bool> WaitForConnectionResolutionAsync(CancellationToken ct)
        {
            TaskCompletionSource<bool> tcs = GetOrCreateConnectionStateTcs();

            await ConvaiTimers.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct));
            ct.ThrowIfCancellationRequested();

            return tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
        }

        public async Task<RoomConnectionAttemptResult> ConnectAsync(CancellationToken ct)
        {
            ReconnectPolicy reconnectPolicy = _resolveReconnectPolicy();
            _setReconnectPolicy(reconnectPolicy);

            ConnectionContext context = _connectionContextProvider();
            _onConnectAttemptStarted(context.HasValidRoom);
            _updateSessionState(SessionState.Connecting, null);

            RoomSessionConnectOptions invocationOptions = _consumePendingConnectOptions();
            bool topologyFreeJoin = invocationOptions?.JoinExistingMultiCharacterRoom == true;
            IConvaiCharacterAgent activeCharacter = _activeCharacterProvider();
            if (activeCharacter == null && !topologyFreeJoin)
            {
                _logger?.Error("Cannot connect: no active character.");
                var failure = ConnectionFailure.Create(
                    SessionErrorCodes.ConnectionFailed,
                    "Cannot connect because no active character is available.",
                    SessionErrorStage.Configuration);
                ClearResolvedCredentials();
                _recordConnectionFailure(failure);
                return RoomConnectionAttemptResult.Fail(failure);
            }

            ConnectionFailure? credentialFailure;
            try
            {
                credentialFailure = await EnsureCredentialsAsync(invocationOptions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ClearResolvedCredentials();
                _updateSessionState(SessionState.Disconnected, null);
                throw;
            }

            if (credentialFailure.HasValue)
            {
                _logger?.Error(credentialFailure.Value.Message);
                _recordConnectionFailure(credentialFailure.Value);
                return RoomConnectionAttemptResult.Fail(credentialFailure.Value);
            }

            try
            {
                string characterReference = topologyFreeJoin ? null : ResolveCharacterReference(activeCharacter);
                string frozenEndUserId = Normalize(invocationOptions?.EndUserId) ??
                                         Normalize(_endUserIdProvider());
                IReadOnlyDictionary<string, object> frozenEndUserMetadata = FreezeMetadata(
                    invocationOptions?.EndUserMetadata ?? _endUserMetadataProvider());
                string identityScope = SessionResumeScope.CreateIdentityScope(frozenEndUserId);
                bool identityChanged = !string.IsNullOrEmpty(_lastSuccessfulIdentityScope) &&
                                       !string.Equals(
                                           _lastSuccessfulIdentityScope,
                                           identityScope,
                                           StringComparison.Ordinal);
                bool referenceChanged = !topologyFreeJoin &&
                                        HasCharacterReferenceChanged(activeCharacter, characterReference, context);
                bool characterChanged = !topologyFreeJoin &&
                                        IsRememberedForAnotherCharacter(context, activeCharacter);
                ConnectionContext scopedContext = identityChanged || referenceChanged || characterChanged
                    ? ConnectionContext.Empty
                    : context;
                string explicitCharacterSessionId = topologyFreeJoin ? null : ResolveCharacterSessionId(
                    activeCharacter,
                    invocationOptions,
                    identityScope,
                    frozenEndUserId,
                    referenceChanged,
                    context.CharacterSessionId,
                    characterReference);
                RoomJoinOptions joinOptions = ApplyInvocationJoinOverrides(
                    CreateJoinOptions(
                        scopedContext,
                        reconnectPolicy,
                        activeCharacter?.EnableSessionResume == true,
                        explicitCharacterSessionId),
                    invocationOptions);
                joinOptions.ResolvedCharacterReference = characterReference;
                ApplyMultiCharacterTopology(
                    joinOptions,
                    invocationOptions,
                    activeCharacter,
                    identityScope,
                    frozenEndUserId);
                TurnTakingOptions turnTakingSourceOptions =
                    TurnTakingOptionsResolver.ResolveSourceOptions(
                        _configuredTurnTakingOptionsProvider(),
                        invocationOptions);
                ResolvedTurnTakingOptions resolvedTurnTakingOptions =
                    TurnTakingOptionsResolver.ResolveFromSource(turnTakingSourceOptions);
                UserVadSettings resolvedUserVadSettings =
                    _configuredUserVadSettingsProvider()?.Clone() ?? UserVadSettings.CreateDefault();
                RoomVisionInputConfig resolvedVisionInputConfig = _visionInputConfigProvider();
                RoomRespondModesConfig resolvedRespondModes = _respondModesProvider();
                LogUserVadSettings(resolvedUserVadSettings);
                _prepareSessionTurnTakingState(turnTakingSourceOptions, resolvedTurnTakingOptions);
                _setCurrentResolvedTurnTakingOptions(resolvedTurnTakingOptions);
                LogActionConfigDiagnostics(activeCharacter, invocationOptions);
                ConvaiActionConfig resolvedActionConfig = ResolveActionConfig(activeCharacter, invocationOptions);
                IReadOnlyList<ConvaiActionDefinition> actionDefinitionCatalog =
                    ResolveActionDefinitionCatalog(activeCharacter, invocationOptions);
                IReadOnlyList<ConvaiActionDefinition> resolvedActionDefinitions =
                    ResolveActionDefinitions(actionDefinitionCatalog, resolvedActionConfig);
                ApplyResolvedActionContext(
                    activeCharacter,
                    resolvedActionConfig,
                    ResolveSessionActionConfig(activeCharacter, invocationOptions, resolvedActionConfig),
                    resolvedActionDefinitions,
                    actionDefinitionCatalog);
                RoomEmotionConfig emotionConfig = ResolveEmotionConfig(activeCharacter);
                if (joinOptions != null)
                {
                    joinOptions.ResolvedTurnTakingOptions = resolvedTurnTakingOptions;
                    joinOptions.ResolvedUserVadSettings = resolvedUserVadSettings;
                    joinOptions.ResolvedEmotionConfig = emotionConfig;
                    joinOptions.ResolvedEndUserId = frozenEndUserId;
                    joinOptions.ResolvedEndUserMetadata = frozenEndUserMetadata;
                    if (invocationOptions?.MaxNumParticipants > 0)
                        joinOptions.ResolvedMaxNumParticipants = invocationOptions.MaxNumParticipants;
                    if (!string.IsNullOrWhiteSpace(invocationOptions?.SharedSessionKey))
                        joinOptions.ResolvedSharedSessionKey = invocationOptions.SharedSessionKey.Trim();
                    joinOptions.ResolvedActionConfig = resolvedActionConfig?.Clone();
                    joinOptions.ResolvedVisionInputConfig = resolvedVisionInputConfig;
                    joinOptions.ResolvedRespondModes = resolvedRespondModes;
                }

                string reconnectMode = joinOptions.IsJoinRequest ? "rejoin" : "create";
                string coreServerUrl = _coreServerUrlProvider();
                if (_logger?.IsEnabled(LogLevel.Info) == true)
                    _logger?.Info(
                    activeCharacter == null
                    ? $"Attempting topology-free {reconnectMode}."
                    : $"Attempting room {reconnectMode}.");

                RoomConnectionAttemptResult result = await ConnectWithRetryAsync(
                    _connectionTypeProvider().ToApiString(),
                    coreServerUrl,
                    activeCharacter?.CharacterId ?? string.Empty,
                    activeCharacter?.EnableSessionResume == true,
                    explicitCharacterSessionId,
                    activeCharacter?.InitialDynamicInfoText ?? string.Empty,
                    activeCharacter?.InitialDynamicInfoKeepInContext == true,
                    joinOptions,
                    identityScope,
                    activeCharacter,
                    ct);
                if (!result.Succeeded)
                    ClearResolvedCredentials();

                return result;
            }
            catch (Exception ex)
            {
                if (activeCharacter != null)
                    ClearResolvedActionContext(activeCharacter);
                ClearResolvedCredentials();
                if (_logger?.IsEnabled(LogLevel.Error) == true)
                    _logger?.Error($"ConnectAsync failed ({ex.GetType().Name}).");
                ConnectionFailure failure = ConnectionFailure.FromException(
                    ex,
                    SessionErrorCodes.ConnectionFailed,
                    ex.Message,
                    SessionErrorStage.Runtime,
                    true);
                _recordConnectionFailure(failure);
                return RoomConnectionAttemptResult.Fail(failure);
            }
        }

        public async Task DisconnectAsync(CancellationToken ct)
        {
            try
            {
                await _disconnectRuntimeAdapter.DisconnectAsync(ct);
            }
            finally
            {
                ClearResolvedCredentials();
            }
        }

        internal void ClearResolvedCredentials()
        {
            try
            {
                ICredentialProvider credentialProvider = _credentialProvider?.Invoke();
                if (credentialProvider is IAsyncCredentialProvider)
                    credentialProvider.Refresh();
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Warning) == true)
                    _logger?.Warning($"Failed to clear resolved credentials ({ex.GetType().Name}).");
            }
        }

        private async Task<ConnectionFailure?> EnsureCredentialsAsync(
            RoomSessionConnectOptions invocationOptions,
            CancellationToken ct)
        {
            try
            {
                ICredentialProvider credentialProvider = _credentialProvider?.Invoke();

                string explicitAuthToken = invocationOptions?.ConsumeExplicitAuthToken();
                if (!string.IsNullOrWhiteSpace(explicitAuthToken))
                {
                    if (credentialProvider is not IExplicitAuthTokenCredentialProvider explicitCredentialProvider)
                    {
                        return ConnectionFailure.Create(
                            SessionErrorCodes.ConfigAuthTokenModeRequired,
                            "Explicit auth-token connections require Auth Token mode in Convai Project Settings.",
                            SessionErrorStage.Configuration);
                    }

                    explicitCredentialProvider.SetAuthTokenForNextConnection(explicitAuthToken);
                }

                if (credentialProvider is not IAsyncCredentialProvider asyncCredentialProvider)
                    return null;

                await asyncCredentialProvider.EnsureCredentialsAsync(ct);
                ct.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(credentialProvider.GetApiKey()))
                    return null;

                string errorMessage =
                    (credentialProvider as IAsyncCredentialResolutionStatus)?.CredentialResolutionErrorMessage;
                return RoomInitializationFailureSupport.FromAuthTokenFetchFailure(errorMessage);
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                throw ex as OperationCanceledException ??
                      new OperationCanceledException(
                          "Auth token resolution was cancelled.",
                          ex,
                          ct);
            }
            catch (Exception ex)
            {
                return RoomInitializationFailureSupport.FromAuthTokenFetchFailure(
                    $"Auth token provider failed ({ex.GetType().Name}).",
                    ex);
            }
        }

        private void ApplyMultiCharacterTopology(
            RoomJoinOptions joinOptions,
            RoomSessionConnectOptions invocationOptions,
            IConvaiCharacterAgent activeCharacter,
            string identityScope,
            string endUserId)
        {
            if (joinOptions == null) return;

            if (invocationOptions?.JoinExistingMultiCharacterRoom == true)
            {
                joinOptions.JoinExistingMultiCharacterRoom = true;
                joinOptions.ResolvedRoomSessionId = Normalize(invocationOptions.RoomSessionId);
                joinOptions.ResolvedSharedSessionKey = Normalize(invocationOptions.SharedSessionKey);
                return;
            }

            IReadOnlyList<IConvaiCharacterAgent> characters = _characterListProvider();
            if (characters == null || characters.Count <= 1) return;
            if (characters.Count > MultiCharacterRoomLimits.MaxCharacters)
                throw new ConvaiOperationException(
                    SessionErrorCodes.ConnectionBadRequest,
                    MultiCharacterRoomLimits.DescribeOverflow(characters.Count));

            var ordered = new List<IConvaiCharacterAgent>(characters.Count) { activeCharacter };
            for (int i = 0; i < characters.Count; i++)
            {
                IConvaiCharacterAgent character = characters[i];
                if (character != null && !ContainsReference(ordered, character))
                    ordered.Add(character);
            }

            if (ordered.Count != characters.Count)
                throw new ConvaiOperationException(
                    SessionErrorCodes.ConnectionBadRequest,
                    "Multi-character roster contains null or duplicate character references.");

            // Refused rather than sent. This layer used to pass a duplicated Character ID straight
            // through to the service, and what came back was a room that answered two characters
            // through one of them.
            if (MultiCharacterRosterIdentity.TryFindDuplicate(
                    ordered,
                    out IConvaiCharacterAgent existingCharacter,
                    out IConvaiCharacterAgent duplicateCharacter))
                throw new ConvaiOperationException(
                    SessionErrorCodes.ConnectionBadRequest,
                    MultiCharacterRosterIdentity.DescribeDuplicate(existingCharacter, duplicateCharacter));

            var roster = new List<RoomConnectionCharacterRequest>(ordered.Count);
            var sessionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (IConvaiCharacterAgent character in ordered)
            {
                if (string.IsNullOrWhiteSpace(character.CharacterId))
                    throw new ConvaiOperationException(
                        SessionErrorCodes.ConfigCharacterIdMissing,
                        "Every character in a multi-character room requires a Character ID.");

                EnsureUnversionedRosterCharacter(character);

                string characterSessionId = ResolveCharacterSessionId(
                    character,
                    ReferenceEquals(character, activeCharacter) ? invocationOptions : null,
                    identityScope,
                    endUserId,
                    characterReference: ResolveCharacterReference(character));
                if (!string.IsNullOrWhiteSpace(characterSessionId) && !sessionIds.Add(characterSessionId))
                    throw new ConvaiOperationException(
                        SessionErrorCodes.ConnectionBadRequest,
                        "Character session IDs must be unique within a multi-character roster.");

                roster.Add(new RoomConnectionCharacterRequest
                {
                    CharacterId = character.CharacterId,
                    CharacterSessionId = characterSessionId
                });
            }

            joinOptions.ResolvedCharacterRoster = roster;
        }

        private static string ResolveCharacterReference(IConvaiCharacterAgent character) =>
            character is ICharacterVersionSource versionSource ? versionSource.CharacterReference : null;

        internal static void EnsureUnversionedRosterCharacter(IConvaiCharacterAgent character)
        {
            string reference = ResolveCharacterReference(character);
            if (reference != null && !SessionResumeScope.CharacterReferencesMatch(reference, character.CharacterId))
                throw new ConvaiOperationException(
                    SessionErrorCodes.ConnectionBadRequest,
                    "Pinned character versions require a single-character connection. " +
                    "Use Live for multi-character rosters, which accept only bare Character IDs.");
        }

        private bool HasCharacterReferenceChanged(
            IConvaiCharacterAgent character,
            string characterReference,
            ConnectionContext context)
        {
            if (character == null) return false;

            if (_characterSessionReferences.TryGetValue(character.CharacterId, out string previousReference) &&
                !SessionResumeScope.CharacterReferencesMatch(previousReference, characterReference))
                return true;

            // Retain provenance when the adapter is recreated with an existing connection context.
            return (context.HasValidRoom || context.CanResumeSession) &&
                   (characterReference != null || context.CharacterReference != null) &&
                   !SessionResumeScope.CharacterReferencesMatch(context.CharacterReference, characterReference);
        }

        /// <summary>
        ///     Whether the remembered room and character session were made for a different character
        ///     than the one connecting now.
        /// </summary>
        /// <remarks>
        ///     The version reference check above catches this for agents that report a selection. An
        ///     agent that reports none, and whose Character ID is changed between sessions, would
        ///     otherwise rejoin the previous character's room -- where that character's agent may
        ///     still be answering -- and resume its conversation.
        /// </remarks>
        private static bool IsRememberedForAnotherCharacter(
            ConnectionContext context,
            IConvaiCharacterAgent character) =>
            character != null &&
            !string.IsNullOrEmpty(context.CharacterId) &&
            (context.HasValidRoom || context.CanResumeSession) &&
            !SessionResumeScope.CharacterReferencesMatch(context.CharacterId, character.CharacterId);

        private static bool ContainsReference(
            IReadOnlyList<IConvaiCharacterAgent> characters,
            IConvaiCharacterAgent candidate)
        {
            for (int i = 0; i < characters.Count; i++)
                if (ReferenceEquals(characters[i], candidate))
                    return true;
            return false;
        }

        private static string Normalize(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        public void NotifyStateChanged(SessionStateChanged stateChanged) =>
            StateChanged?.Invoke(stateChanged);

        public void SignalStartCompleted()
        {
            HasStarted = true;
            _startCompletedTcs.TrySetResult(true);
        }

        public void SignalConnectionStateWaiters(SessionState newState)
        {
            lock (_connectionStateLock)
            {
                if (_connectionStateTcs == null || _connectionStateTcs.Task.IsCompleted)
                    return;

                switch (newState)
                {
                    case SessionState.Connected:
                        _connectionStateTcs.TrySetResult(true);
                        break;
                    case SessionState.Error:
                    case SessionState.Disconnected:
                        _connectionStateTcs.TrySetResult(false);
                        break;
                }
            }
        }

        private TaskCompletionSource<bool> GetOrCreateConnectionStateTcs()
        {
            lock (_connectionStateLock)
            {
                if (_connectionStateTcs == null || _connectionStateTcs.Task.IsCompleted)
                {
                    // Scheduled, not inline: this signal is completed inside _connectionStateLock,
                    // and a plain source would run the rest of connecting — everything awaiting
                    // WaitForConnectionResolutionAsync — inside that lock, on the thread that
                    // reported the state, with every other caller of the wait queued behind it.
                    _connectionStateTcs =
                        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return _connectionStateTcs;
            }
        }

        private RoomJoinOptions CreateJoinOptions(
            ConnectionContext connectionContext,
            ReconnectPolicy reconnectPolicy,
            bool enableSessionResume,
            string explicitCharacterSessionId)
        {
            reconnectPolicy ??= ReconnectPolicy.Default;

            ResumePolicy effectiveResumePolicy = enableSessionResume
                ? reconnectPolicy.ResumePolicy
                : ResumePolicy.AlwaysFresh;

            RoomJoinOptions joinOptions = RoomJoinOptions.FromContext(connectionContext, reconnectPolicy);
            if (effectiveResumePolicy == ResumePolicy.AlwaysFresh)
                return joinOptions.IsJoinRequest
                    ? new RoomJoinOptions(joinOptions.RoomName, null, joinOptions.SpawnAgent,
                        joinOptions.MaxNumParticipants)
                    : RoomJoinOptions.CreateNew(null, joinOptions.MaxNumParticipants);

            if (enableSessionResume && !string.IsNullOrEmpty(explicitCharacterSessionId))
                return WithCharacterSessionId(joinOptions, explicitCharacterSessionId);

            return joinOptions;
        }

        private async Task<RoomConnectionAttemptResult> ConnectWithRetryAsync(
            string connectionType,
            string coreServerUrl,
            string characterId,
            bool enableSessionResume,
            string explicitCharacterSessionId,
            string dynamicInfoText,
            bool keepDynamicInfoInContext,
            RoomJoinOptions joinOptions,
            string identityScope,
            IConvaiCharacterAgent activeCharacter,
            CancellationToken cancellationToken)
        {
            ResolvedTurnTakingOptions resolvedTurnTakingOptions =
                joinOptions?.ResolvedTurnTakingOptions ?? ResolvedTurnTakingOptions.DefaultHandsFree;
            UserVadSettings resolvedUserVadSettings =
                joinOptions?.ResolvedUserVadSettings ?? UserVadSettings.CreateDefault();
            string sessionId = joinOptions?.CharacterSessionId;
            string persistenceKey = SessionResumeScope.CreatePersistenceKey(
                characterId,
                joinOptions?.ResolvedEndUserId,
                joinOptions);
            bool allowStoredSessionFallback = enableSessionResume && explicitCharacterSessionId == null;
            if (string.IsNullOrEmpty(sessionId) && allowStoredSessionFallback)
                sessionId = _sessionPersistenceProvider()?.LoadSession(persistenceKey);

            RoomJoinOptions currentJoinOptions = joinOptions;
            string currentSessionId = sessionId;

            var executor = new RetryExecutor(new ExponentialBackoffPolicy());
            ConnectionFailure lastFailure = default;

            try
            {
                return await executor.ExecuteAsync(async (attempt, ct) =>
                {
                    string mode = currentJoinOptions?.IsJoinRequest == true ? "join" : "create";
                    if (_logger?.IsEnabled(LogLevel.Info) == true)
                        _logger?.Info(
                        $"Room connection attempt {attempt + 1} (mode={mode}).");

                    IConvaiRoomController controller = _controllerProvider();
                    if (controller == null)
                    {
                        lastFailure = ConnectionFailure.Create(
                            SessionErrorCodes.ConnectionFailed,
                            "Room controller is not initialized.",
                            SessionErrorStage.Configuration);
                        throw lastFailure.ToException();
                    }

                    RoomConnectionAttemptResult attemptResult = await controller.InitializeAsync(
                        connectionType,
                        coreServerUrl,
                        characterId,
                        currentSessionId,
                        enableSessionResume,
                        dynamicInfoText,
                        keepDynamicInfoInContext,
                        currentJoinOptions,
                        ct);

                    if (attemptResult.Succeeded)
                    {
                        if (_agentRegistry is IMultiCharacterSessionRegistry multiCharacterRegistry &&
                            multiCharacterRegistry.CurrentMultiCharacterSession is { } multiCharacterSession)
                        {
                            if (currentJoinOptions?.IsMultiCharacterJoin == true)
                                _updateSessionState(SessionState.Connected, null);
                            else
                            {
                                try
                                {
                                    await WaitForInitialCharacterAsync(
                                        multiCharacterSession,
                                        activeCharacter,
                                        ct);
                                }
                                catch (Exception readinessException)
                                {
                                    lastFailure = ConnectionFailure.Create(
                                        SessionErrorCodes.ConnectionFailed,
                                        readinessException.Message,
                                        SessionErrorStage.Runtime);
                                    _recordConnectionFailure(lastFailure);
                                    return RoomConnectionAttemptResult.Fail(lastFailure);
                                }
                            }
                        }

                        string successfulPersistenceKey = SessionResumeScope.CreatePersistenceKey(
                            characterId,
                            currentJoinOptions?.ResolvedEndUserId,
                            currentJoinOptions);
                        PersistSession(
                            characterId,
                            successfulPersistenceKey,
                            identityScope,
                            currentJoinOptions?.ResolvedCharacterReference,
                            enableSessionResume);
                        ApplyCurrentCharacterSessionId(activeCharacter, controller.CharacterSessionID);
                        if (_logger?.IsEnabled(LogLevel.Info) == true)
                            _logger?.Info(
                            $"Room connection succeeded (mode={mode}).");
                        _recordConnectionSuccess(
                            controller.RoomName,
                            controller.CharacterSessionID,
                            controller.SessionID,
                            characterId);
                        ConnectionContext successfulContext = _connectionContextProvider();
                        if (successfulContext.HasValidRoom)
                            _setConnectionContext(successfulContext.WithCharacterReference(
                                currentJoinOptions?.ResolvedCharacterReference));
                        return RoomConnectionAttemptResult.Success();
                    }

                    lastFailure = attemptResult.Failure;

                    if (currentJoinOptions?.IsJoinRequest == true &&
                        currentJoinOptions.IsMultiCharacterJoin == false &&
                        attempt == 0)
                    {
                        _logger?.Info(
                            "Room join failed; falling back to create mode.");
                        currentJoinOptions =
                            RoomJoinOptions.CreateNew(currentSessionId, currentJoinOptions.MaxNumParticipants);
                        currentJoinOptions.ResolvedTurnTakingOptions = resolvedTurnTakingOptions;
                        currentJoinOptions.ResolvedUserVadSettings = resolvedUserVadSettings;
                        currentJoinOptions.ResolvedEmotionConfig = joinOptions?.ResolvedEmotionConfig;
                        currentJoinOptions.ResolvedCharacterReference = joinOptions?.ResolvedCharacterReference;
                        currentJoinOptions.ResolvedEndUserId = joinOptions?.ResolvedEndUserId;
                        currentJoinOptions.ResolvedEndUserMetadata =
                            CloneMetadata(joinOptions?.ResolvedEndUserMetadata);
                        currentJoinOptions.ResolvedSharedSessionKey = joinOptions?.ResolvedSharedSessionKey;
                        currentJoinOptions.ResolvedMaxNumParticipants = joinOptions?.ResolvedMaxNumParticipants;
                        currentJoinOptions.ResolvedActionConfig = joinOptions?.ResolvedActionConfig?.Clone();
                        currentJoinOptions.ResolvedVisionInputConfig = joinOptions?.ResolvedVisionInputConfig;
                        currentJoinOptions.ResolvedRespondModes = joinOptions?.ResolvedRespondModes;
                    }

                    currentSessionId = null;
                    throw lastFailure.ToException();
                }, cancellationToken);
            }
            catch (ConvaiOperationException)
            {
                _logger?.Error(
                    "Room connection failed after retry exhaustion.");
                if (string.IsNullOrWhiteSpace(lastFailure.Code))
                {
                    lastFailure = ConnectionFailure.Create(
                        SessionErrorCodes.ConnectionFailed,
                        "Failed to connect to Convai room.",
                        SessionErrorStage.Runtime,
                        true);
                }

                _recordConnectionFailure(lastFailure);
                return RoomConnectionAttemptResult.Fail(lastFailure);
            }
        }

        private static async Task WaitForInitialCharacterAsync(
            MultiCharacterRoomSession session,
            IConvaiCharacterAgent activeCharacter,
            CancellationToken cancellationToken)
        {
            float timeoutSeconds = activeCharacter is ConvaiCharacter character
                ? character.CharacterReadyTimeoutSeconds
                : 30f;
            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(1f, timeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);
            try
            {
                await session.WaitUntilReadyAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for initial character '{session.InitialCharacter?.CharacterId}' to become ready.");
            }
        }

        private string ResolveCharacterSessionId(
            IConvaiCharacterAgent activeCharacter,
            RoomSessionConnectOptions invocationOptions,
            string identityScope,
            string endUserId,
            bool referenceChanged = false,
            string previousContextSessionId = null,
            string characterReference = null)
        {
            string explicitInvocationSessionId = Normalize(invocationOptions?.CharacterSessionId);
            if (explicitInvocationSessionId != null &&
                _sessionCharacterReferences.TryGetValue(explicitInvocationSessionId, out string explicitReference) &&
                !SessionResumeScope.CharacterReferencesMatch(explicitReference, characterReference))
                throw DifferentCharacterVersionSession();

            if (referenceChanged)
            {
                string previousComponentSessionId = (activeCharacter as ConvaiCharacter)?.CharacterSessionId;
                ApplyCurrentCharacterSessionId(activeCharacter, null);
                if (explicitInvocationSessionId != null &&
                    (string.Equals(explicitInvocationSessionId, previousContextSessionId, StringComparison.Ordinal) ||
                     string.Equals(explicitInvocationSessionId, previousComponentSessionId, StringComparison.Ordinal)))
                    throw DifferentCharacterVersionSession();

                // An explicitly supplied session with unknown provenance remains backend-validated.
                return explicitInvocationSessionId;
            }
            if (explicitInvocationSessionId != null)
                return explicitInvocationSessionId;

            if (activeCharacter is not ConvaiCharacter character)
                return null;

            if (!character.EnableSessionResume)
                return null;

            if (_sessionCharacterReferences.TryGetValue(character.CharacterSessionId, out string componentReference) &&
                !SessionResumeScope.CharacterReferencesMatch(componentReference, characterReference))
            {
                character.ClearCharacterSessionId();
                return null;
            }

            if (string.IsNullOrWhiteSpace(endUserId))
                return character.CharacterSessionId;

            return _characterIdentityScopes.TryGetValue(character.CharacterId, out string knownScope) &&
                   string.Equals(knownScope, identityScope, StringComparison.Ordinal)
                ? character.CharacterSessionId
                : null;
        }

        private static ConvaiOperationException DifferentCharacterVersionSession() =>
            new(SessionErrorCodes.ConnectionBadRequest,
                "This character session belongs to a different character version selection. " +
                "Omit CharacterSessionId to start or resume a session for the selected version.");

        private static RoomJoinOptions WithCharacterSessionId(
            RoomJoinOptions joinOptions,
            string characterSessionId)
        {
            if (joinOptions == null || !joinOptions.IsJoinRequest)
                return new RoomJoinOptions(null, characterSessionId, maxNumParticipants: joinOptions?.MaxNumParticipants)
                {
                    ResolvedCharacterReference = joinOptions?.ResolvedCharacterReference
                };

            return new RoomJoinOptions(
                joinOptions.RoomName,
                characterSessionId,
                joinOptions.SpawnAgent,
                joinOptions.MaxNumParticipants,
                joinOptions.CharacterId)
            {
                ResolvedCharacterReference = joinOptions.ResolvedCharacterReference
            };
        }

        private static RoomJoinOptions ApplyInvocationJoinOverrides(
            RoomJoinOptions joinOptions,
            RoomSessionConnectOptions invocationOptions)
        {
            if (invocationOptions == null)
                return joinOptions;

            int? maxNumParticipants = invocationOptions.MaxNumParticipants > 0
                ? invocationOptions.MaxNumParticipants
                : null;
            string sharedSessionKey = string.IsNullOrWhiteSpace(invocationOptions.SharedSessionKey)
                ? null
                : invocationOptions.SharedSessionKey.Trim();

            if (!maxNumParticipants.HasValue && string.IsNullOrEmpty(sharedSessionKey))
                return joinOptions;

            RoomJoinOptions resolvedJoinOptions = WithMaxNumParticipants(joinOptions, maxNumParticipants);
            if (maxNumParticipants.HasValue)
                resolvedJoinOptions.ResolvedMaxNumParticipants = maxNumParticipants;
            if (!string.IsNullOrEmpty(sharedSessionKey))
                resolvedJoinOptions.ResolvedSharedSessionKey = sharedSessionKey;

            return resolvedJoinOptions;
        }

        private static RoomJoinOptions WithMaxNumParticipants(
            RoomJoinOptions joinOptions,
            int? maxNumParticipants)
        {
            if (!maxNumParticipants.HasValue)
                return joinOptions;

            if (joinOptions == null || !joinOptions.IsJoinRequest)
                return new RoomJoinOptions(null, joinOptions?.CharacterSessionId, maxNumParticipants: maxNumParticipants)
                {
                    ResolvedCharacterReference = joinOptions?.ResolvedCharacterReference
                };

            return new RoomJoinOptions(
                joinOptions.RoomName,
                joinOptions.CharacterSessionId,
                joinOptions.SpawnAgent,
                maxNumParticipants,
                joinOptions.CharacterId)
            {
                ResolvedCharacterReference = joinOptions.ResolvedCharacterReference
            };
        }

        private RoomEmotionConfig ResolveEmotionConfig(IConvaiCharacterAgent activeCharacter)
        {
            if (activeCharacter == null) return null;
            RoomEmotionConfig emotionConfig = EmotionConfigResolver.Resolve(activeCharacter.EmotionDetectionMode);
            if (emotionConfig != null)
                {
                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                        _logger?.Debug(
                        $"Emotion detection '{emotionConfig.Provider}' requested (client-controlled).");
                }
            else
                _logger?.Debug(
                    "Emotion detection disabled (no emotion controller, or mode Off).");

            return emotionConfig;
        }

        private static void ApplyCurrentCharacterSessionId(
            IConvaiCharacterAgent activeCharacter,
            string characterSessionId)
        {
            if (activeCharacter is ConvaiCharacter character)
                character.SetCurrentCharacterSessionId(characterSessionId);
        }

        private static IReadOnlyDictionary<string, object> CloneMetadata(
            IReadOnlyDictionary<string, object> metadata)
        {
            if (metadata == null || metadata.Count == 0)
                return null;

            return new Dictionary<string, object>(metadata);
        }

        private static IReadOnlyDictionary<string, object> FreezeMetadata(
            IReadOnlyDictionary<string, object> metadata)
        {
            if (metadata == null || metadata.Count == 0)
                return null;

            string snapshot = JsonConvert.SerializeObject(metadata);
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(snapshot);
        }

        /// <summary>
        ///     Resolves the wire-shaped <c>action_config</c> sent at connect. Disabled actions
        ///     (authored <see cref="ConvaiActionDefinition.Enabled" /> off, or a
        ///     <see cref="ConvaiCharacterActions.SetActionAvailable" /> override) are excluded here,
        ///     so the backend never offers them. An explicit per-call override bypasses filtering —
        ///     the caller authored exactly what it wants sent.
        /// </summary>
        private static ConvaiActionConfig ResolveActionConfig(
            IConvaiCharacterAgent activeCharacter,
            RoomSessionConnectOptions invocationOptions)
        {
            if (invocationOptions?.ActionConfigOverride != null)
                return invocationOptions.ActionConfigOverride.Clone();

            if (activeCharacter is not Component component)
                return null;

            ConvaiActionConfigSource source = component.GetComponent<ConvaiActionConfigSource>();
            return source?.BuildActionConfig((activeCharacter as ConvaiCharacter)?.Actions);
        }

        /// <summary>
        ///     Resolves the session config stored on the character for local resolution and
        ///     mid-session wire snapshots. Unlike the connect payload
        ///     (<see cref="ResolveActionConfig" />) it keeps disabled actions: the dispatcher must
        ///     still identify a stale command for a disabled action (reported as unhandled, not
        ///     unmatched), and a mid-session <c>SetActionAvailable(name, true)</c> must be able to
        ///     restore the action's rendered string in the next re-sync snapshot.
        /// </summary>
        private static ConvaiActionConfig ResolveSessionActionConfig(
            IConvaiCharacterAgent activeCharacter,
            RoomSessionConnectOptions invocationOptions,
            ConvaiActionConfig wireActionConfig)
        {
            if (invocationOptions?.ActionConfigOverride != null)
                return wireActionConfig;

            if (wireActionConfig == null || activeCharacter is not Component component)
                return wireActionConfig;

            ConvaiActionConfigSource source = component.GetComponent<ConvaiActionConfigSource>();
            return source != null ? source.BuildRuntimeResolutionConfig() : wireActionConfig;
        }

        private static IReadOnlyList<ConvaiActionDefinition> ResolveActionDefinitionCatalog(
            IConvaiCharacterAgent activeCharacter,
            RoomSessionConnectOptions invocationOptions)
        {
            if (invocationOptions?.ActionDefinitionsOverride != null &&
                invocationOptions.ActionDefinitionsOverride.Count > 0)
                return ConvaiActionDefinition.FilterAndClone(
                    invocationOptions.ActionDefinitionsOverride,
                    requireExecutable: true);

            if (activeCharacter is not Component component)
                return null;

            ConvaiActionConfigSource source = component.GetComponent<ConvaiActionConfigSource>();

            // Deliberately unnarrowed: the catalog keeps disabled definitions so a stale backend
            // command for a disabled action is still classified (reported as unhandled rather than
            // vanishing as unmatched) and a mid-session re-enable can restore it. The
            // availability-filtered narrowing happens in ResolveActionDefinitions below.
            return source?.GetEffectiveDefinitions(requireExecutable: true);
        }

        private static IReadOnlyList<ConvaiActionDefinition> ResolveActionDefinitions(
            IReadOnlyList<ConvaiActionDefinition> definitionCatalog,
            ConvaiActionConfig resolvedActionConfig)
        {
            if (resolvedActionConfig?.Actions == null || resolvedActionConfig.Actions.Count == 0)
                return Array.Empty<ConvaiActionDefinition>();

            return ConvaiActionDefinition.FilterAndClone(
                definitionCatalog,
                resolvedActionConfig?.Actions,
                requireExecutable: true);
        }

        private void LogUserVadSettings(UserVadSettings resolvedUserVadSettings)
        {
            if (resolvedUserVadSettings == null || resolvedUserVadSettings.UseServerDefault)
            {
                _logger?.Debug(
                    "Using server default vad_params (field omitted from connect payload).");
                return;
            }

            if (_logger?.IsEnabled(LogLevel.Info) == true)
                _logger?.Info(
                "Sending custom vad_params: " +
                $"confidence={resolvedUserVadSettings.Confidence}, " +
                $"start_secs={resolvedUserVadSettings.StartSecs}, " +
                $"stop_secs={resolvedUserVadSettings.StopSecs}, " +
                $"min_volume={resolvedUserVadSettings.MinVolume}");
        }

        private void LogActionConfigDiagnostics(
            IConvaiCharacterAgent activeCharacter,
            RoomSessionConnectOptions invocationOptions)
        {
            if (invocationOptions?.ActionConfigOverride != null)
                return;

            if (activeCharacter is not Component component)
                return;

            ConvaiActionConfigSource source = component.GetComponent<ConvaiActionConfigSource>();
            if (source == null)
                return;

            IReadOnlyList<ConvaiActionConfigDiagnostic> diagnostics = ConvaiActionConfigValidator.Validate(source);
            if (diagnostics == null || diagnostics.Count == 0)
                return;

            for (int i = 0; i < diagnostics.Count; i++)
            {
                ConvaiActionConfigDiagnostic diagnostic = diagnostics[i];
                if (diagnostic == null)
                    continue;

                string message = $"[RoomConnectionRuntimeAdapter] Action config diagnostic: {diagnostic}";
                if (diagnostic.Severity == ConvaiActionConfigDiagnosticSeverity.Error)
                    _logger?.Error(message);
                else
                    _logger?.Warning(message);
            }
        }

        /// <summary>
        ///     Stores both action views this session needs on the character: the
        ///     <paramref name="resolvedActionConfig" /> actually sent to the backend (the
        ///     server-shared truth that runtime patch reconciliation and ACK commit read), and the
        ///     wider <paramref name="localResolutionConfig" /> used only for local name-to-scene
        ///     resolution. The two are deliberately not the same object — see
        ///     <see cref="ResolveSessionActionConfig" />.
        /// </summary>
        private static void ApplyResolvedActionContext(
            IConvaiCharacterAgent activeCharacter,
            ConvaiActionConfig resolvedActionConfig,
            ConvaiActionConfig localResolutionConfig,
            IReadOnlyList<ConvaiActionDefinition> resolvedActionDefinitions,
            IReadOnlyList<ConvaiActionDefinition> actionDefinitionCatalog)
        {
            if (activeCharacter is ConvaiCharacter character)
            {
                ApplyTo(character);
                return;
            }

            if (activeCharacter is Component component &&
                component.TryGetComponent(out ConvaiCharacter targetCharacter))
                ApplyTo(targetCharacter);

            void ApplyTo(ConvaiCharacter target)
            {
                target.SetResolvedSessionActionConfig(resolvedActionConfig);
                target.SetSessionLocalResolutionConfig(localResolutionConfig);
                target.SetResolvedSessionActionDefinitions(resolvedActionDefinitions);
                target.SetResolvedSessionActionDefinitionCatalog(
                    actionDefinitionCatalog ?? Array.Empty<ConvaiActionDefinition>());
            }
        }

        private static void ClearResolvedActionContext(IConvaiCharacterAgent activeCharacter)
        {
            if (activeCharacter is ConvaiCharacter character)
            {
                character.ClearResolvedSessionActionConfig();
                character.ClearSessionLocalResolutionConfig();
                character.ClearResolvedSessionActionDefinitions();
                character.ClearResolvedSessionActionDefinitionCatalog();
                return;
            }

            if (activeCharacter is Component component &&
                component.GetComponent<ConvaiCharacter>() is { } targetCharacter)
            {
                targetCharacter.ClearResolvedSessionActionConfig();
                targetCharacter.ClearSessionLocalResolutionConfig();
                targetCharacter.ClearResolvedSessionActionDefinitions();
                targetCharacter.ClearResolvedSessionActionDefinitionCatalog();
            }
        }

        private void PersistSession(
            string characterId,
            string persistenceKey,
            string identityScope,
            string characterReference,
            bool enableSessionResume)
        {
            _lastSuccessfulIdentityScope = identityScope;
            if (!string.IsNullOrWhiteSpace(characterId))
            {
                _characterSessionReferences[characterId] = characterReference;
                string successfulSessionId = _controllerProvider()?.CharacterSessionID;
                if (!string.IsNullOrEmpty(successfulSessionId))
                    _sessionCharacterReferences[successfulSessionId] = characterReference;
            }
            if (!enableSessionResume) return;

            string sessionId = _controllerProvider()?.CharacterSessionID;
            if (string.IsNullOrEmpty(sessionId)) return;

            _sessionPersistenceProvider()?.SaveSession(persistenceKey, sessionId);
            if (!string.IsNullOrWhiteSpace(characterId))
                _characterIdentityScopes[characterId] = identityScope;

            ConnectionContext currentContext = _connectionContextProvider();
            if (!currentContext.HasValidRoom)
                return;

            _setConnectionContext(currentContext);
        }
    }

    internal sealed class RoomDisconnectRuntimeAdapter
    {
        private readonly Func<AudioTrackManager> _audioTrackManagerProvider;
        private readonly Action<DisconnectReason, bool, string> _completeDisconnectionTracking;
        private readonly Func<IConvaiRoomController> _controllerProvider;
        private readonly ILogger _logger;
        private readonly Action _resetBargeInCoordinatorState;
        private readonly Action<SessionState, SessionError?> _updateSessionState;

        public RoomDisconnectRuntimeAdapter(
            Func<AudioTrackManager> audioTrackManagerProvider,
            Func<IConvaiRoomController> controllerProvider,
            Action<SessionState, SessionError?> updateSessionState,
            Action<DisconnectReason, bool, string> completeDisconnectionTracking,
            Action resetBargeInCoordinatorState,
            ILogger logger = null)
        {
            _audioTrackManagerProvider = audioTrackManagerProvider ??
                                         throw new ArgumentNullException(nameof(audioTrackManagerProvider));
            _controllerProvider = controllerProvider ?? throw new ArgumentNullException(nameof(controllerProvider));
            _updateSessionState = updateSessionState ?? throw new ArgumentNullException(nameof(updateSessionState));
            _completeDisconnectionTracking =
                completeDisconnectionTracking ?? throw new ArgumentNullException(nameof(completeDisconnectionTracking));
            _resetBargeInCoordinatorState = resetBargeInCoordinatorState ??
                                            throw new ArgumentNullException(
                                                nameof(resetBargeInCoordinatorState));
            _logger = logger.WithTag(nameof(RoomDisconnectRuntimeAdapter), LogCategory.Transport);
        }

        /// <summary>
        ///     Every client-requested disconnect ends here. The session is over, so the room is not
        ///     kept for a rejoin.
        /// </summary>
        public async Task DisconnectAsync(CancellationToken ct)
        {
            _resetBargeInCoordinatorState();
            try
            {
                AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
                if (audioTrackManager != null)
                {
                    try
                    {
                        await audioTrackManager.UnpublishMicrophoneAsync();
                    }
                    catch (Exception ex)
                    {
                        if (_logger?.IsEnabled(LogLevel.Warning) == true)
                            _logger?.Warning(
                            $"DisconnectAsync failed to unpublish microphone cleanly: {ex.Message}");
                    }

                    audioTrackManager.SetMicMuted(true);
                    audioTrackManager.ClearState();
                }

                _logger?.Info("DisconnectAsync called.");

                _updateSessionState(SessionState.Disconnecting, null);

                IConvaiRoomController controller = _controllerProvider();
                if (controller != null)
                    await controller.DisconnectFromRoomAsync(ct);

                _completeDisconnectionTracking(DisconnectReason.ClientInitiated, true, "Disconnected from room");
            }
            finally
            {
                _resetBargeInCoordinatorState();
            }
        }

        /// <summary>
        ///     The room dropped without the client asking. The event carries no reason, so every such
        ///     drop is recorded as a transport error and the room stays eligible for a rejoin.
        /// </summary>
        public void HandleUnexpectedDisconnect(bool updateSessionState, string completionMessage)
        {
            _resetBargeInCoordinatorState();
            try
            {
                AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
                audioTrackManager?.SetMicMuted(true);
                audioTrackManager?.ClearState();
                _completeDisconnectionTracking(DisconnectReason.TransportError, updateSessionState, completionMessage);
            }
            finally
            {
                _resetBargeInCoordinatorState();
            }
        }
    }
}
