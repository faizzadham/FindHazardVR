using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Abstractions;
using Convai.Domain.DomainEvents.Participant;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Protocol;
using Convai.RestAPI;
using Convai.RestAPI.Internal;
using Convai.RestAPI.Services;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime;
using Convai.Runtime.Room;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using ILogger = Convai.Domain.Logging.ILogger;
using LogCategory = Convai.Domain.Logging.LogCategory;

namespace Convai.Infrastructure.Networking.WebGL
{
    /// <summary>
    ///     WebGL implementation of IConvaiRoomController using IRealtimeTransport.
    ///     Provides room connection and management for WebGL platforms.
    /// </summary>
    internal sealed class WebGLRoomController : IConvaiRoomController, IRoomDetailsStateTarget,
        IRealtimeTransportAccessor
    {
        private const float AudioTrackResolutionTimeoutSeconds = 5f;
        private readonly IAgentRegistry _agentRegistry;
        private readonly MonoBehaviour _coroutineRunner;
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly IEventHub _eventHub;
        private readonly ILogger _logger;
        private readonly IPlayerSession _playerSession;
        private readonly ProtocolGateway _protocolGateway;
        private readonly INarrativeSectionNameResolver _sectionNameResolver;
        private readonly ISessionPersistence _sessionPersistence;

        private readonly object _stateLock = new();
        private readonly IRealtimeTransport _transport;
        private readonly ITransportConfiguration _transportConfiguration;
        private string _characterSessionId;
        private bool _disposed;

        private bool _hasRoomDetails;
        private bool _isConnectedToRoom;
        private bool _isMicMuted;
        private string _requestTraceId;
        private string _resolvedEndUserId;
        private IReadOnlyDictionary<string, object> _resolvedEndUserMetadata;
        private string _resolvedSpeakerId;
        private string _roomName;
        private string _roomUrl;

        private string _sessionId;
        private string _targetCharacterId;
        private string _token;

        /// <summary>
        ///     Creates a new WebGLRoomController.
        /// </summary>
        /// <param name="agentRegistry">Agent registry for looking up characters.</param>
        /// <param name="playerSession">Player session information.</param>
        /// <param name="transportConfiguration">Read-only transport/session configuration.</param>
        /// <param name="sessionPersistence">Character-session persistence adapter.</param>
        /// <param name="dispatcher">Main thread dispatcher.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="eventHub">Event hub for domain events.</param>
        /// <param name="transport">The realtime transport implementation.</param>
        /// <param name="coroutineRunner">MonoBehaviour for running coroutines (required for WebGL HTTP calls).</param>
        /// <param name="sectionNameResolver">Optional narrative section resolver.</param>
        public WebGLRoomController(
            IAgentRegistry agentRegistry,
            IPlayerSession playerSession,
            ITransportConfiguration transportConfiguration,
            ISessionPersistence sessionPersistence,
            IMainThreadDispatcher dispatcher,
            ILogger logger,
            IEventHub eventHub,
            IRealtimeTransport transport,
            MonoBehaviour coroutineRunner,
            INarrativeSectionNameResolver sectionNameResolver = null)
        {
            _agentRegistry = agentRegistry ?? throw new ArgumentNullException(nameof(agentRegistry));
            _playerSession = playerSession ?? throw new ArgumentNullException(nameof(playerSession));
            _transportConfiguration =
                transportConfiguration ?? throw new ArgumentNullException(nameof(transportConfiguration));
            _sessionPersistence = sessionPersistence ?? throw new ArgumentNullException(nameof(sessionPersistence));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).WithTag(nameof(WebGLRoomController), LogCategory.Transport);
            _eventHub = eventHub;
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _coroutineRunner = coroutineRunner ?? throw new ArgumentNullException(nameof(coroutineRunner));
            _sectionNameResolver = sectionNameResolver;

            _protocolGateway = new ProtocolGateway(
                null,
                msg => _logger.Debug(msg, LogCategory.Transport),
                msg => _logger.Error(msg, LogCategory.Transport),
                () => _logger.IsEnabled(LogLevel.Debug, LogCategory.Transport));

            // Subscribe to transport events
            SubscribeToTransportEvents();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;

            UnsubscribeFromTransportEvents();
            _disposed = true;
        }

        private void SubscribeToTransportEvents()
        {
            _transport.Connected += OnTransportConnected;
            _transport.Disconnected += OnTransportDisconnected;
            _transport.Reconnecting += OnTransportReconnecting;
            _transport.Reconnected += OnTransportReconnected;
            _transport.ParticipantConnected += OnParticipantConnected;
            _transport.ParticipantDisconnected += OnParticipantDisconnected;
            _transport.TrackSubscribed += OnTrackSubscribed;
            _transport.TrackUnsubscribed += OnTrackUnsubscribed;
            _transport.DataReceived += OnDataReceived;
        }

        private void UnsubscribeFromTransportEvents()
        {
            _transport.Connected -= OnTransportConnected;
            _transport.Disconnected -= OnTransportDisconnected;
            _transport.Reconnecting -= OnTransportReconnecting;
            _transport.Reconnected -= OnTransportReconnected;
            _transport.ParticipantConnected -= OnParticipantConnected;
            _transport.ParticipantDisconnected -= OnParticipantDisconnected;
            _transport.TrackSubscribed -= OnTrackSubscribed;
            _transport.TrackUnsubscribed -= OnTrackUnsubscribed;
            _transport.DataReceived -= OnDataReceived;
        }

        #region IConvaiRoomController State Properties

        /// <inheritdoc />
        public bool HasRoomDetails
        {
            get
            {
                lock (_stateLock) return _hasRoomDetails;
            }
            private set
            {
                lock (_stateLock) _hasRoomDetails = value;
            }
        }


        /// <inheritdoc />
        public bool IsConnectedToRoom
        {
            get
            {
                lock (_stateLock) return _isConnectedToRoom;
            }
            private set
            {
                lock (_stateLock) _isConnectedToRoom = value;
            }
        }

        /// <inheritdoc />
        public bool IsMicMuted
        {
            get
            {
                lock (_stateLock) return _isMicMuted;
            }
            private set
            {
                lock (_stateLock) _isMicMuted = value;
            }
        }

        /// <inheritdoc />
        public string SessionID
        {
            get
            {
                lock (_stateLock) return _sessionId;
            }
            private set
            {
                lock (_stateLock) _sessionId = value;
            }
        }

        /// <inheritdoc />
        public string CharacterSessionID
        {
            get
            {
                lock (_stateLock) return _characterSessionId;
            }
            private set
            {
                lock (_stateLock) _characterSessionId = value;
            }
        }

        /// <inheritdoc />
        public string RoomName
        {
            get
            {
                lock (_stateLock) return _roomName;
            }
            private set
            {
                lock (_stateLock) _roomName = value;
            }
        }

        /// <inheritdoc />
        public string RoomURL
        {
            get
            {
                lock (_stateLock) return _roomUrl;
            }
            private set
            {
                lock (_stateLock) _roomUrl = value;
            }
        }

        /// <inheritdoc />
        public string Token
        {
            get
            {
                lock (_stateLock) return _token;
            }
            private set
            {
                lock (_stateLock) _token = value;
            }
        }

        /// <inheritdoc />
        public string ResolvedSpeakerId
        {
            get
            {
                lock (_stateLock) return _resolvedSpeakerId;
            }
            private set
            {
                lock (_stateLock) _resolvedSpeakerId = value;
            }
        }

        /// <inheritdoc />
        public string RequestTraceId
        {
            get
            {
                lock (_stateLock) return _requestTraceId;
            }
            private set
            {
                lock (_stateLock) _requestTraceId = value;
            }
        }

        /// <inheritdoc />
        public string ResolvedEndUserId
        {
            get
            {
                lock (_stateLock) return _resolvedEndUserId;
            }
            private set
            {
                lock (_stateLock) _resolvedEndUserId = value;
            }
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata
        {
            get
            {
                lock (_stateLock) return _resolvedEndUserMetadata;
            }
            private set
            {
                lock (_stateLock) _resolvedEndUserMetadata = value;
            }
        }

        string IRoomDetailsStateTarget.Token
        {
            get => Token;
            set => Token = value;
        }

        string IRoomDetailsStateTarget.RoomName
        {
            get => RoomName;
            set => RoomName = value;
        }

        string IRoomDetailsStateTarget.RoomURL
        {
            get => RoomURL;
            set => RoomURL = value;
        }

        string IRoomDetailsStateTarget.SessionID
        {
            get => SessionID;
            set => SessionID = value;
        }

        string IRoomDetailsStateTarget.CharacterSessionID
        {
            get => CharacterSessionID;
            set => CharacterSessionID = value;
        }

        string IRoomDetailsStateTarget.ResolvedSpeakerId
        {
            get => ResolvedSpeakerId;
            set => ResolvedSpeakerId = value;
        }

        string IRoomDetailsStateTarget.RequestTraceId
        {
            get => RequestTraceId;
            set => RequestTraceId = value;
        }

        string IRoomDetailsStateTarget.ResolvedEndUserId
        {
            get => ResolvedEndUserId;
            set => ResolvedEndUserId = value;
        }

        IReadOnlyDictionary<string, object> IRoomDetailsStateTarget.ResolvedEndUserMetadata
        {
            get => ResolvedEndUserMetadata;
            set => ResolvedEndUserMetadata = value;
        }

        bool IRoomDetailsStateTarget.HasRoomDetails
        {
            get => HasRoomDetails;
            set => HasRoomDetails = value;
        }

        /// <inheritdoc />
        public RTVIHandler RTVIHandler { get; private set; }

        /// <inheritdoc />
        public IRoomFacade CurrentRoom { get; private set; }

        #endregion

        #region Events

        /// <inheritdoc />
        public event Action OnRoomConnectionSuccessful;

        /// <inheritdoc />
        public event Action OnRoomConnectionFailed;

        /// <inheritdoc />
        public event Action<bool> OnMicMuteChanged;

        /// <inheritdoc />
        public event Action OnRoomReconnecting;

        /// <inheritdoc />
        public event Action OnRoomReconnected;

        /// <inheritdoc />
        public event Action OnUnexpectedRoomDisconnected;

        /// <inheritdoc />
        public event Action<IRemoteAudioTrack, string, string> OnRemoteAudioTrackSubscribed;

        /// <inheritdoc />
        public event Action<string, string> OnRemoteAudioTrackUnsubscribed;

        #endregion

        #region Transport Event Handlers

        private void OnTransportConnected(TransportSessionInfo sessionInfo)
        {
            CurrentRoom = _transport.Room;
            _roomName = sessionInfo.RoomName;
            _sessionId = sessionInfo.SessionId;
            _characterSessionId = sessionInfo.CharacterSessionId;
            IsConnectedToRoom = true;
            _logger.Info("Connected to room", LogCategory.Transport);

            _dispatcher.TryDispatch(() => OnRoomConnectionSuccessful?.Invoke());
        }

        private void OnTransportDisconnected(DisconnectReason reason)
        {
            IsConnectedToRoom = false;
            CurrentRoom = null;
            _logger.Info("Disconnected from room.", LogCategory.Transport);

            if (reason != DisconnectReason.ClientInitiated)
                _dispatcher.TryDispatch(() => OnUnexpectedRoomDisconnected?.Invoke());
        }

        private void OnTransportReconnecting()
        {
            _logger.Info("Reconnecting to room...", LogCategory.Transport);
            _dispatcher.TryDispatch(() => OnRoomReconnecting?.Invoke());
        }

        private void OnTransportReconnected()
        {
            _logger.Info("Reconnected to room", LogCategory.Transport);
            _dispatcher.TryDispatch(() => OnRoomReconnected?.Invoke());
        }

        private void OnParticipantConnected(TransportParticipantInfo info)
        {
            if (string.IsNullOrEmpty(info.ParticipantId) || _agentRegistry == null) return;

            MultiCharacterRoomSession roomSession =
                (_agentRegistry as IMultiCharacterSessionRegistry)?.CurrentMultiCharacterSession;
            if (roomSession != null)
            {
                CharacterRoomMembership membership = roomSession.Resolve(null, info.Identity, info.ParticipantId);
                if (membership?.Character != null)
                {
                    roomSession.BindParticipant(membership, info.ParticipantId);
                    ParticipantEventPublicationSupport.PublishConnected(
                        _eventHub,
                        _logger,
                        ParticipantInfo.ForCharacter(
                            info.ParticipantId,
                            info.Identity,
                            membership.Character.CharacterName),
                        nameof(WebGLRoomController));
                }
                else
                    ParticipantEventPublicationSupport.PublishConnected(
                        _eventHub,
                        _logger,
                        ParticipantInfo.ForRemotePlayer(info.ParticipantId, info.Identity, info.Identity),
                        nameof(WebGLRoomController));
                return;
            }

            string characterId = TryResolveCharacterId(info);
            if (string.IsNullOrEmpty(characterId)) return;

            if (_agentRegistry.TryGetCharacter(characterId, out IConvaiCharacterAgent agent))
            {
                _agentRegistry.TryGetParticipantId(characterId, out string existingParticipantId);
                if (!string.Equals(existingParticipantId, info.ParticipantId, StringComparison.OrdinalIgnoreCase))
                {
                    _agentRegistry.SetParticipantId(characterId, info.ParticipantId);

                    ParticipantEventPublicationSupport.PublishConnected(
                        _eventHub,
                        _logger,
                        ParticipantInfo.ForCharacter(info.ParticipantId, agent.CharacterId,
                            agent.CharacterName),
                        nameof(WebGLRoomController));
                }
            }
        }

        private void OnParticipantDisconnected(TransportParticipantInfo info)
        {
            if (string.IsNullOrEmpty(info.ParticipantId) || _agentRegistry == null) return;

            MultiCharacterRoomSession roomSession =
                (_agentRegistry as IMultiCharacterSessionRegistry)?.CurrentMultiCharacterSession;
            if (roomSession != null)
            {
                CharacterRoomMembership membership = roomSession.Resolve(null, info.Identity, info.ParticipantId);
                ParticipantInfo participant = membership?.Character != null
                    ? ParticipantInfo.ForCharacter(
                        info.ParticipantId,
                        info.Identity,
                        membership.Character.CharacterName)
                    : ParticipantInfo.ForRemotePlayer(info.ParticipantId, info.Identity, info.Identity);
                ParticipantEventPublicationSupport.PublishDisconnected(
                    _eventHub,
                    _logger,
                    participant,
                    nameof(WebGLRoomController));
                return;
            }

            if (_agentRegistry.TryGetCharacterByParticipantId(info.ParticipantId, out IConvaiCharacterAgent agent))
            {
                ParticipantEventPublicationSupport.PublishDisconnected(
                    _eventHub,
                    _logger,
                    ParticipantInfo.ForCharacter(info.ParticipantId, agent.CharacterId, agent.CharacterName),
                    nameof(WebGLRoomController));

                _agentRegistry.SetParticipantId(agent.CharacterId, null);
            }
        }

        private void OnTrackSubscribed(TrackInfo trackInfo)
        {
            if (trackInfo.Kind != TrackKind.Audio) return;

            if (TryNotifyAudioTrackSubscribed(trackInfo))
                return;

            _coroutineRunner.StartCoroutine(NotifyAudioTrackSubscribedWhenAvailable(trackInfo));
        }

        private void OnTrackUnsubscribed(TrackInfo trackInfo)
        {
            if (trackInfo.Kind != TrackKind.Audio) return;

            string participantSid = trackInfo.ParticipantId;
            string characterId = ResolveCharacterIdFromParticipant(participantSid);

            _logger.Debug("Remote audio track unsubscribed.",
                LogCategory.Transport);

            RemoteTrackSessionNotificationSupport.NotifyAudioTrackUnsubscribed(
                OnRemoteAudioTrackUnsubscribed,
                participantSid,
                characterId,
                _logger,
                nameof(WebGLRoomController),
                _dispatcher.TryDispatch);
        }

        private IEnumerator NotifyAudioTrackSubscribedWhenAvailable(TrackInfo trackInfo)
        {
            // A track can be subscribed while the connect is still finishing, before the room facade exists.
            // Wait on the clock rather than a frame count: that window spans a variable number of frames.
            float deadline = Time.realtimeSinceStartup + AudioTrackResolutionTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (_disposed)
                    yield break;

                yield return null;

                if (TryNotifyAudioTrackSubscribed(trackInfo))
                    yield break;
            }

            if (_logger.IsEnabled(LogLevel.Warning, LogCategory.Audio))
                _logger.Warning(
                $"Timed out waiting for wrapped remote audio track. The audio still plays, but lip sync and body language will not follow this character's speech: participant='{trackInfo.ParticipantId}', trackSid='{trackInfo.TrackSid}'.",
                LogCategory.Audio);
        }

        private bool TryNotifyAudioTrackSubscribed(TrackInfo trackInfo)
        {
            string participantSid = trackInfo.ParticipantId;
            IRemoteAudioTrack audioTrack = TryResolveRemoteAudioTrack(trackInfo);
            if (audioTrack == null)
                return false;

            string participantIdentity = trackInfo.ParticipantIdentity;
            if (string.IsNullOrWhiteSpace(participantIdentity))
                participantIdentity = CurrentRoom?.GetParticipantBySid(participantSid)?.Identity;
            if (string.IsNullOrWhiteSpace(participantIdentity))
                participantIdentity = ResolveCharacterIdFromParticipant(participantSid);

            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                _logger.Debug(
                $"Remote audio track subscribed: participant={participantSid}, identity={participantIdentity}",
                LogCategory.Transport);

            RemoteTrackSessionNotificationSupport.NotifyAudioTrackSubscribed(
                OnRemoteAudioTrackSubscribed,
                audioTrack,
                participantSid,
                participantIdentity,
                _logger,
                nameof(WebGLRoomController),
                _dispatcher.TryDispatch);

            return true;
        }

        private IRemoteAudioTrack TryResolveRemoteAudioTrack(TrackInfo trackInfo)
        {
            IRemoteParticipant participant = CurrentRoom?.GetParticipantBySid(trackInfo.ParticipantId);
            if (participant == null)
                return null;

            foreach (IRemoteAudioTrack track in participant.AudioTracks)
            {
                if (track.Sid == trackInfo.TrackSid)
                    return track;
            }

            return null;
        }

        private void OnDataReceived(DataPacket packet)
        {
            if (packet.Payload.Length == 0) return;

            try
            {
                ProtocolPacketDispatchSupport.DispatchIncoming(packet, _protocolGateway, RTVIHandler);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error, LogCategory.Transport))
                    _logger.Error($"Error processing data ({ex.GetType().Name}).", LogCategory.Transport);
            }
        }

        #endregion

        #region Connection Methods

        /// <inheritdoc />
        public Task<RoomConnectionAttemptResult> InitializeAsync(
            string connectionType,
            string coreServerUrl,
            string characterId,
            string storedSessionId,
            bool enableSessionResume,
            string dynamicInfoText,
            bool keepDynamicInfoInContext) =>
            InitializeAsync(connectionType, coreServerUrl, characterId, storedSessionId,
                enableSessionResume, dynamicInfoText, keepDynamicInfoInContext, null, CancellationToken.None);

        /// <inheritdoc />
        public async Task<RoomConnectionAttemptResult> InitializeAsync(
            string connectionType,
            string coreServerUrl,
            string characterId,
            string storedSessionId,
            bool enableSessionResume,
            string dynamicInfoText,
            bool keepDynamicInfoInContext,
            RoomJoinOptions joinOptions,
            CancellationToken cancellationToken = default)
        {
            HasRoomDetails = false;
            (_agentRegistry as IMultiCharacterSessionRegistry)?.ClearMultiCharacterSession();
            RequestTraceId = null;
            ResolvedEndUserId = null;
            ResolvedEndUserMetadata = null;
            ResolvedSpeakerId = null;
            _targetCharacterId = characterId;

            _logger.Info("Initializing room connection.",
                LogCategory.Transport);

            try
            {
                string credential = _transportConfiguration.ApiKey;
                bool usesAuthToken = TransportAuthenticationSupport.UsesAuthToken(_transportConfiguration);
                string authenticationHeader = TransportAuthenticationSupport.GetHeaderName(_transportConfiguration);
                _logger.Debug("Resolving room details via core-service.",
                    LogCategory.Transport);
                RoomEmotionConfig emotionConfig = joinOptions?.ResolvedEmotionConfig;
                string requestEndUserId = joinOptions?.ResolvedEndUserId ?? _transportConfiguration.EndUserId;
                IReadOnlyDictionary<string, object> requestEndUserMetadata =
                    joinOptions?.ResolvedEndUserMetadata ?? _transportConfiguration.EndUserMetadata;
                string playerNameForRequest = joinOptions?.ResolvedEndUserMetadata != null
                    ? null
                    : _playerSession?.PlayerName;

                RoomSessionStartupPlan startupPlan = RoomSessionStartupKernel.Prepare(
                    characterId,
                    connectionType,
                    coreServerUrl,
                    storedSessionId,
                    enableSessionResume,
                    joinOptions,
                    requestEndUserId,
                    requestEndUserMetadata,
                    _transportConfiguration.VideoTrackName,
                    emotionConfig,
                    joinOptions?.ResolvedTurnTakingOptions ?? ResolvedTurnTakingOptions.DefaultHandsFree,
                    _transportConfiguration.LipSyncTransportOptions,
                    StoredSessionFallbackPolicy.WebGLCompatibility,
                    InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed,
                    dynamicInfoText,
                    keepDynamicInfoInContext,
                    _transportConfiguration.Debug,
                    playerNameForRequest,
                    joinOptions?.ResolvedUserVadSettings,
                    joinOptions?.ResolvedVisionInputConfig,
                    joinOptions?.ResolvedRespondModes);
                RoomConnectionRequest roomRequest = startupPlan.Request;
                string jsonBody = RoomConnectionRequestTransportSerializer.SerializeForTransport(
                    roomRequest,
                    ConvaiRestOptionsFactory.CreateForRuntimeCredential(credential, usesAuthToken));

                _logger.Debug(startupPlan.FormatModeLogMessage(), LogCategory.Transport);

                _logger.Debug("Requesting room details using coroutine-backed HTTP.",
                    LogCategory.Transport);

                RoomDetails roomDetails;
                try
                {
                    roomDetails = await RunCoroutineRequestAsync<RoomDetails>(
                        tcs => _coroutineRunner.StartCoroutine(FetchRoomDetailsCoroutine(
                            coreServerUrl,
                            authenticationHeader,
                            credential,
                            jsonBody,
                            tcs)),
                        cancellationToken);
                }
                catch (Exception restEx)
                {
                    RoomSessionStartupDecision failureDecision = RoomSessionStartupKernel.FromRequestException(
                        startupPlan,
                        restEx);
                    if (_logger.IsEnabled(LogLevel.Error, LogCategory.Transport))
                        _logger.Error(
                        $"REST API call failed with exception: {restEx.GetType().Name}: {restEx.Message}",
                        LogCategory.Transport);
                    _logger.Debug(failureDecision.FormatDiagnosticsLogMessage(),
                        LogCategory.Transport);
                    if (failureDecision.InitializationOutcome.ShouldClearStoredSession)
                    {
                        RoomInitializationRecoverySupport.TryClearStoredSessionForRecovery(
                            _sessionPersistence,
                            _logger,
                            characterId,
                            roomRequest,
                            failureDecision.InitializationOutcome,
                            "[WebGLRoomController]");
                    }

                    OnRoomConnectionFailed?.Invoke();
                    return RoomConnectionAttemptResult.Fail(failureDecision.FailureOutcome.Failure);
                }

                if (roomDetails == null || string.IsNullOrEmpty(roomDetails.Token))
                {
                    RoomSessionStartupDecision failureDecision = RoomSessionStartupKernel.FromInvalidRoomDetails(
                        startupPlan,
                        "Failed to get room details");
                    _logger.Debug(failureDecision.FormatDiagnosticsLogMessage(),
                        LogCategory.Transport);
                    _logger.Error("Failed to get room details", LogCategory.Transport);
                    OnRoomConnectionFailed?.Invoke();
                    return RoomConnectionAttemptResult.Fail(failureDecision.FailureOutcome.Failure);
                }

                ConfigureMultiCharacterSession(roomDetails);
                RoomSessionStartupDecision acceptedDecision = RoomSessionStartupKernel.AcceptRoomDetails(
                    startupPlan,
                    roomDetails);
                _logger.Debug(acceptedDecision.FormatDiagnosticsLogMessage(),
                    LogCategory.Transport);
                RoomDetailsStateApplier.Apply(
                    this,
                    acceptedDecision.AppliedRoomDetailsState,
                    _logger,
                    true,
                    "[WebGLRoomController]");
                RoomInitializationRecoverySupport.TryPersistCharacterSession(
                    _sessionPersistence,
                    _logger,
                    characterId,
                    acceptedDecision.AppliedRoomDetailsState.CharacterSessionID,
                    acceptedDecision.InitializationOutcome,
                    "[WebGLRoomController]");

                PreparedRtviHandlerDependencies rtviHandlerDependencies =
                    RoomTransportConnectSupport.PrepareRtviHandlerDependencies(
                        _protocolGateway,
                        _transport,
                        _agentRegistry,
                        _playerSession,
                        _dispatcher,
                        _logger,
                        _eventHub,
                        _sectionNameResolver,
                        _transportConfiguration.LipSyncTransportOptions);
                RTVIHandler = rtviHandlerDependencies.CreateHandler();

                _logger.Debug("Connecting realtime transport.",
                    LogCategory.Transport);
                bool connected = await _transport.ConnectAsync(RoomURL, Token, null, cancellationToken);
                TransportConnectOutcome connectOutcome = RoomTransportConnectSupport.FromConnectResult(
                    connected,
                    "Transport connection failed");

                if (!connectOutcome.Connected)
                {
                    _logger.Error(connectOutcome.FormatFailureLogMessage(),
                        LogCategory.Transport);
                    OnRoomConnectionFailed?.Invoke();
                    return RoomConnectionAttemptResult.Fail(ConnectionFailure.Create(
                        SessionErrorCodes.TransportLivekitError,
                        connectOutcome.FailureMessage,
                        SessionErrorStage.Transport,
                        true));
                }

                _logger.Info("Connection successful. Microphone will start after user gesture.",
                    LogCategory.Transport);
                return RoomConnectionAttemptResult.Success();
            }
            catch (Exception ex)
            {
                TransportConnectOutcome connectOutcome = RoomTransportConnectSupport.FromException(
                    ex,
                    "Connection error");
                _logger.Error(connectOutcome.FormatFailureLogMessage(),
                    LogCategory.Transport);
                OnRoomConnectionFailed?.Invoke();
                return RoomConnectionAttemptResult.Fail(ConnectionFailure.Create(
                    SessionErrorCodes.TransportLivekitError,
                    connectOutcome.FailureMessage,
                    SessionErrorStage.Transport,
                    true,
                    exception: ex));
            }
        }

        /// <summary>
        ///     Coroutine-based HTTP call for WebGL compatibility.
        ///     Uses UnityWebRequest which properly yields on WebGL, unlike async/await with Task.Yield().
        /// </summary>
        private IEnumerator FetchRoomDetailsCoroutine(string url, string authenticationHeader, string credential,
            string jsonBody,
            TaskCompletionSource<RoomDetails> tcs)
        {
            using (UnityWebRequest webRequest = CreateJsonPostRequest(
                       url,
                       authenticationHeader,
                       credential,
                       jsonBody))
            {
                _logger.Debug("Sending room-details request.", LogCategory.Transport);
                yield return webRequest.SendWebRequest();

                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    _logger.Error("Room-details request failed.", LogCategory.Transport);
                    tcs.TrySetException(CreateRoomDetailsRequestException(
                        webRequest.error,
                        webRequest.responseCode,
                        webRequest.downloadHandler?.text));
                    yield break;
                }

                string responseText = webRequest.downloadHandler.text;

                try
                {
                    var roomDetails = JsonConvert.DeserializeObject<RoomDetails>(responseText);
                    if (roomDetails == null)
                    {
                        tcs.TrySetException(new RoomInitializationFetchException(
                            "Failed to deserialize room details: result was null"));
                        yield break;
                    }

                    _logger.Debug("Parsed room details.",
                        LogCategory.Transport);
                    tcs.TrySetResult(roomDetails);
                }
                catch (JsonException ex)
                {
                    if (_logger.IsEnabled(LogLevel.Error, LogCategory.Transport))
                        _logger.Error($"Failed to parse room details ({ex.GetType().Name}).",
                        LogCategory.Transport);
                    tcs.TrySetException(new RoomInitializationFetchException(
                        $"Failed to parse room details: {ex.Message}",
                        innerException: ex));
                }
            }
        }

        internal static RoomInitializationFetchException CreateRoomDetailsRequestException(
            string requestError,
            long responseCode,
            string responseBody)
        {
            string error = $"HTTP request failed: {requestError} (Code: {responseCode})";
            return new RoomInitializationFetchException(
                error,
                error,
                responseCode,
                responseBody);
        }

        private async Task<T> RunCoroutineRequestAsync<T>(
            Action<TaskCompletionSource<T>> startRequest,
            CancellationToken cancellationToken)
        {
            var tcs =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            startRequest(tcs);

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))) return await tcs.Task;
        }

        private static UnityWebRequest CreateJsonPostRequest(string url, string authenticationHeader,
            string credential,
            string jsonBody)
        {
            var webRequest = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
            webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            webRequest.SetRequestHeader("Content-Type", "application/json");
            webRequest.SetRequestHeader(authenticationHeader, credential);
            webRequest.timeout = 30;
            return webRequest;
        }

        /// <inheritdoc />
        public void DisconnectFromRoom() => _ = DisconnectFromRoomAsync();

        /// <inheritdoc />
        public async Task DisconnectFromRoomAsync(CancellationToken cancellationToken = default)
        {
            _logger.Debug("Disconnecting from room...", LogCategory.Transport);

            try
            {
                await _transport.DisconnectAsync(DisconnectReason.ClientInitiated, cancellationToken);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error, LogCategory.Transport))
                    _logger.Error($"Disconnect error ({ex.GetType().Name}).", LogCategory.Transport);
            }

            IsConnectedToRoom = false;
            HasRoomDetails = false;
            (_agentRegistry as IMultiCharacterSessionRegistry)?.ClearMultiCharacterSession();
            CurrentRoom = null;
            RequestTraceId = null;
            ResolvedEndUserId = null;
            ResolvedEndUserMetadata = null;
            ResolvedSpeakerId = null;
        }

        private void ConfigureMultiCharacterSession(RoomDetails roomDetails)
        {
            if (_agentRegistry is not IMultiCharacterSessionRegistry registry) return;
            if (roomDetails?.Characters is { Count: > 0 } && !string.IsNullOrWhiteSpace(roomDetails.RoomSessionId))
                registry.Configure(roomDetails, _agentRegistry.Characters);
            else
                registry.ClearMultiCharacterSession();
        }

        #endregion

        #region Audio Control

        /// <inheritdoc />
        /// <remarks>
        ///     The room manager reads browser audio playback state and unlocks playback through this transport.
        /// </remarks>
        public IRealtimeTransport Transport => _transport;

        /// <inheritdoc />
        public TransportCapabilities Capabilities => _transport.Capabilities;

        /// <inheritdoc />
        public void SetMicMuted(bool mute)
        {
            IsMicMuted = mute;
            _transport.SetMicrophoneMuted(mute);
            _dispatcher.TryDispatch(() => OnMicMuteChanged?.Invoke(mute));
        }

        /// <inheritdoc />
        public void ToggleMicMute() => SetMicMuted(!IsMicMuted);

        /// <inheritdoc />
        public bool SetCharacterAudioMuted(string characterId, bool mute)
        {
            _logger.Warning("SetCharacterAudioMuted not fully implemented for WebGL",
                LogCategory.Transport);
            return false;
        }

        /// <inheritdoc />
        public bool MuteCharacter(string characterId) => SetCharacterAudioMuted(characterId, true);

        /// <inheritdoc />
        public bool UnmuteCharacter(string characterId) => SetCharacterAudioMuted(characterId, false);

        /// <inheritdoc />
        public bool IsCharacterAudioMuted(string characterId) => false;

        /// <inheritdoc />
        public void SetAudioSubscriptionPolicy(Func<string, bool> policy) =>
            _logger.Debug("Audio subscription policy set", LogCategory.Transport);

        /// <inheritdoc />
        public void ApplyRemoteAudioPreference(string characterId, bool enabled) => _logger.Debug(
            $"Remote audio preference updated: enabled={enabled}",
            LogCategory.Transport);

        #endregion

        #region Helper Methods

        private string ResolveCharacterIdFromParticipant(string participantSid)
        {
            if (string.IsNullOrEmpty(participantSid)) return null;

            if (_agentRegistry.TryGetCharacterByParticipantId(participantSid, out IConvaiCharacterAgent agent))
                return agent.CharacterId;

            if (!string.IsNullOrEmpty(_targetCharacterId))
            {
                IReadOnlyList<IConvaiCharacterAgent> all = _agentRegistry.Characters;
                if (all != null && all.Count == 1) return _targetCharacterId;
            }

            return null;
        }

        private string TryResolveCharacterId(TransportParticipantInfo info)
        {
            if (!string.IsNullOrEmpty(info.Identity) && _agentRegistry.TryGetCharacter(info.Identity, out _))
                return info.Identity;

            string fromMetadata = TryExtractCharacterIdFromMetadata(info.Metadata);
            if (!string.IsNullOrEmpty(fromMetadata) && _agentRegistry.TryGetCharacter(fromMetadata, out _))
                return fromMetadata;

            if (!string.IsNullOrEmpty(_targetCharacterId))
            {
                IReadOnlyList<IConvaiCharacterAgent> all = _agentRegistry.Characters;
                if (all != null && all.Count == 1) return _targetCharacterId;
            }

            return null;
        }

        private static string TryExtractCharacterIdFromMetadata(string metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata)) return null;

            try
            {
                JObject obj = JObject.Parse(metadata);
                JToken token = obj["characterId"] ??
                               obj["character_id"] ?? obj["convai_character_id"] ?? obj["convaiCharacterId"];
                return token?.Type == JTokenType.String ? token.Value<string>() : token?.ToString();
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
