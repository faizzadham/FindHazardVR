using System;
using Convai.Runtime.Core.Coordinators;
using System.Collections;
using System.Threading;
using Convai.Runtime.Core.Async;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Logging;
using Convai.Runtime.Room;
using UnityEngine;

namespace Convai.Runtime.Components
{
    [AddComponentMenu("Convai/Convai Push To Talk Controller")]
    public sealed class ConvaiPushToTalkController : MonoBehaviour
    {
        [SerializeField] [Tooltip("Optional explicit manager reference. If omitted, the active ConvaiManager is used.")]
        private ConvaiManager _manager;

        [SerializeField]
        [Tooltip(
            "Optional explicit character target. If omitted, the character currently addressed by the room is used.")]
        private ConvaiCharacter _targetCharacter;

        [SerializeField]
        [Tooltip(
            "When true and no explicit target is assigned, the controller follows ConvaiManager.AddressedCharacter.")]
        private bool _useActiveConversationCharacter = true;

        private ConvaiRoomManager _pttRoom;
        private bool _usingCorrelatedPtt;
        public PttState State => _usingCorrelatedPtt && _pttRoom != null ? _pttRoom.PttState :
            IsPressed ? PttState.Capturing : _pendingRelease ? PttState.Draining :
            IsAwaitingTurnCompletion ? PttState.AwaitingBotTurn : PttState.Idle;
        public string ReleaseId => _usingCorrelatedPtt ? _pttRoom?.PttReleaseId ?? string.Empty : string.Empty;
        public string CompatibilityReason => _pttRoom?.PttCompatibilityReason ?? "The room service uses legacy PTT.";
        public event Action<PttState> StateChanged;
        public event Action<PttCommitResult> CommitCompleted;
        public IConvaiOperation<RoomSession> RetryAsync(CancellationToken cancellationToken = default) =>
            _pttRoom != null ? _pttRoom.RetryPttAsync(cancellationToken) :
                ConvaiOperation<RoomSession>.Failed(new InvalidOperationException("PTT room is unavailable."));

        private string _activeTargetCharacterId;
        private string _activeTargetParticipantId;
        private int _activeTurnGeneration;
        private IConvaiRoomAudioService _audioService;
        private SubscriptionToken _characterSpeechToken;
        private SubscriptionToken _characterTurnCompletedToken;
        private IConvaiRoomConnectionService _connectionService;

        private IEventHub _eventHub;
        private bool _hasOpenedMicrophoneThisSession;
        private SubscriptionToken _llmNoResponseToken;
        private SubscriptionToken _moderationResponseToken;
        private bool _forceStopSentForPendingRelease;
        private bool _isPostStopFinalizationWindow;
        private bool _pendingRelease;
        private int _pendingReleaseGeneration;
        private ResolvedTurnTakingOptions _pendingReleasePolicy;
        private Coroutine _releaseTailCoroutine;
        private Func<ResolvedTurnTakingOptions> _resolvedPolicyProviderOverride;
        private string _resolvedTargetCharacterIdOverride;
        private SubscriptionToken _sessionErrorToken;
        private SubscriptionToken _sessionStateToken;
        private bool _speechStartedForAwaitingTurn;
        private bool _targetSpeaking;
        private Coroutine _turnCompletionTimeoutCoroutine;
        private SubscriptionToken _usageLimitReachedToken;
        private SubscriptionToken _playerTranscriptToken;

        public bool IsPressed { get; private set; }
        public bool IsAwaitingTurnCompletion { get; private set; }
        public string ActiveTargetCharacterId => _activeTargetCharacterId ?? string.Empty;
        public string ActiveTargetParticipantId => _activeTargetParticipantId ?? string.Empty;
        public string BlockedReason { get; private set; } = string.Empty;

        private void OnEnable()
        {
            TryResolveDependencies();
            SubscribeToEvents();
        }

        private void OnDisable()
        {
            if (_usingCorrelatedPtt)
            {
                if (IsPressed) Release();
                UnsubscribeFromEvents();
                return;
            }
            bool captureClosed = AbortActiveCaptureIfNeeded();
            ClearAwaitingTurn(captureClosed ? string.Empty : BlockedReason);
            UnsubscribeFromEvents();
            if (IsPressed) PublishLocalActivity(false);
            IsPressed = false;
            _targetSpeaking = false;
            // A disabled controller cannot observe a disconnect/reconnect. Conservatively forget
            // publication state so the next first press re-ensures the local track; startup is
            // idempotent, while reusing a torn-down track leaves the new room silent.
            _hasOpenedMicrophoneThisSession = false;
        }

        private void OnDestroy()
        {
            if (_pttRoom != null)
            {
                _pttRoom.PttStateChanged -= HandlePttStateChanged;
                _pttRoom.PttCommitCompleted -= HandlePttCommitCompleted;
            }
        }

        private void OnApplicationFocus(bool focused) { if (!focused && IsPressed) Release(); }
        private void OnApplicationPause(bool paused) { if (paused && IsPressed) Release(); }

        private void BindPttRoom()
        {
            if (_connectionService is not ConvaiRoomManager room || room == _pttRoom) return;
            if (_pttRoom != null)
            {
                _pttRoom.PttStateChanged -= HandlePttStateChanged;
                _pttRoom.PttCommitCompleted -= HandlePttCommitCompleted;
            }
            _pttRoom = room;
            _pttRoom.PttStateChanged += HandlePttStateChanged;
            _pttRoom.PttCommitCompleted += HandlePttCommitCompleted;
        }

        private void HandlePttStateChanged(PttState state)
        {
            if (!_usingCorrelatedPtt) return;
            _pendingRelease = state is PttState.Draining or PttState.AwaitingCommit;
            IsAwaitingTurnCompletion = _pendingRelease || state == PttState.AwaitingBotTurn;
            if (state == PttState.Error)
            {
                BlockedReason = _pttRoom.PttError;
                if (IsPressed) PublishLocalActivity(false);
                IsPressed = false;
            }
            StateChanged?.Invoke(state);
        }

        private void HandlePttCommitCompleted(PttCommitResult result) => CommitCompleted?.Invoke(result);

        public bool SetPressed(bool pressed) => pressed ? Press() : Release();

        /// <summary>
        ///     Tells the rest of the SDK, without waiting for the service, that the player is or is
        ///     no longer holding the talk control.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Deliberately not <c>PlayerSpeakingStateChanged</c>: holding the control is not
        ///         speech, and anything that commits a turn, bills, or measures latency must keep
        ///         reading the service's own verdict. This is the hint that lets a character react
        ///         to being addressed at the moment it happens — see
        ///         <see cref="LocalPlayerActivityChanged" />.
        ///     </para>
        /// </remarks>
        private void PublishLocalActivity(bool isActive) =>
            _eventHub?.Publish(LocalPlayerActivityChanged.Create(
                isActive, LocalPlayerActivitySource.PushToTalk));

        internal bool BelongsToManager(ConvaiManager manager)
        {
            if (manager == null)
                return false;

            return ReferenceEquals(ResolveManager(), manager);
        }

        internal bool PrepareForConversationInputModeTransition(
            ResolvedTurnTakingOptions nextPolicy,
            string reason)
        {
            if (_usingCorrelatedPtt && _pttRoom?.HasPendingPttRelease == true)
            {
                if (IsPressed) Release();
                return Reject("Push-to-talk is finishing its capture; retry the change after commit.");
            }
            bool prepared = AbortActiveCaptureIfNeeded();
            _activeTurnGeneration++;
            if (IsPressed) PublishLocalActivity(false);
            IsPressed = false;
            _targetSpeaking = false;
            ClearAwaitingTurn(prepared ? reason : BlockedReason);
            _hasOpenedMicrophoneThisSession = nextPolicy?.ShouldAutoStartMicrophoneAfterConnect ?? false;
            if (prepared && _usingCorrelatedPtt) _pttRoom?.ClearCompletedPttTurn();
            return prepared;
        }

        /// <summary>
        ///     Commits any capture that was active when routing began without resetting whether the
        ///     microphone has already been published in this connection. A route change is not an
        ///     input-mode or connection boundary.
        /// </summary>
        internal bool PrepareForConversationTargetRouting(
            string reason,
            bool preservesCurrentTurnBoundary = false)
        {
            if (_usingCorrelatedPtt && _pttRoom?.HasPendingPttRelease == true)
            {
                if (IsPressed) Release();
                return Reject("Push-to-talk is finishing its capture; retry the change after commit.");
            }
            bool prepared = AbortActiveCaptureIfNeeded();
            _activeTurnGeneration++;
            if (IsPressed) PublishLocalActivity(false);
            IsPressed = false;
            if (!preservesCurrentTurnBoundary)
            {
                _targetSpeaking = false;
                ClearAwaitingTurn(prepared ? reason : BlockedReason);
            }
            return prepared;
        }

        /// <summary>
        ///     Clears the previous character's turn guard only after the authoritative room state
        ///     actually moves. A pre-wire failure therefore leaves the old target's busy state intact.
        /// </summary>
        internal void CommitConversationTargetChange(string reason)
        {
            if (_usingCorrelatedPtt) _pttRoom?.ClearCompletedPttTurn();
            _targetSpeaking = false;
            ClearAwaitingTurn(reason);
        }

        public bool Press()
        {
            if (!TryResolveDependencies())
                return Reject("Convai runtime services are not available.");

            ResolvedTurnTakingOptions policy = ResolveCurrentPolicy();
            if (policy.Mode != ConversationInputMode.PushToTalk)
                return Reject("Push-to-talk is not enabled for the current session.");

            if (_connectionService == null || !_connectionService.IsConnected)
                return Reject("Push-to-talk requires an active room connection.");

            if (_pendingRelease || IsPressed || (_usingCorrelatedPtt && _pttRoom?.HasPendingPttRelease == true))
                return Reject("Push-to-talk is finishing the previous capture.");

            string targetCharacterId = ResolveTargetCharacterId();
            if (string.IsNullOrWhiteSpace(targetCharacterId))
                return Reject("Push-to-talk could not resolve a target character.");

            // A resolvable character id is not readiness. The room can be connected while the
            // character it routes to has not been announced by the service, and speech captured in
            // that window is heard by nobody — the spoken twin of the message the chat field used to
            // swallow.
            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager != null && !manager.ConversationAvailability.CanAcceptPlayerInput())
            {
                return Reject(
                    $"'{manager.AddressedCharacter?.CharacterName ?? targetCharacterId}' cannot hear " +
                    $"the player yet ({manager.ConversationAvailability}).");
            }

            BindPttRoom();
            if (_pttRoom != null && _pttRoom.UsesCorrelatedPtt)
            {
                _usingCorrelatedPtt = true;
                _activeTargetCharacterId = targetCharacterId;
                if (!_pttRoom.BeginPttCapture(targetCharacterId))
                    return Reject(_pttRoom.PttError.Length > 0 ? _pttRoom.PttError : "Push-to-talk is waiting for its current turn.");
                IsPressed = true;
                BlockedReason = string.Empty;
                PublishLocalActivity(true);
                return true;
            }
            _usingCorrelatedPtt = false;

            bool targetBusy = _targetSpeaking || IsAwaitingTurnCompletion;
            if (targetBusy)
            {
                if (policy.InterruptBotOnPress)
                {
                    _connectionService.InterruptBot();
                    ClearAwaitingTurn("interrupt-on-press");
                    _targetSpeaking = false;
                }
                else if (policy.RequireTurnCompletionBeforeNextPress)
                    return Reject("Push-to-talk is waiting for the current character turn to finish.");
            }

            if (policy.EnableServerSttToggle && !_connectionService.SetSttMuted(false))
                return Reject("Push-to-talk could not enable backend speech recognition.");

            BlockedReason = string.Empty;
            _activeTargetCharacterId = targetCharacterId;
            _activeTargetParticipantId = string.Empty;

            OpenLocalMicrophone(policy);

            IsPressed = true;

            // Earlier and surer than any detector: the player reaching for the talk control is a
            // decision, not a sound to be inferred, and it lands before they have said anything.
            // The character turns to them as they raise the microphone rather than a round trip
            // after they finish the first word.
            PublishLocalActivity(true);
            return true;
        }

        public bool Release()
        {
            if (!TryResolveDependencies())
                return false;

            if (_usingCorrelatedPtt && _pttRoom != null)
            {
                if (!IsPressed) return _pttRoom.HasPendingPttRelease;
                IsPressed = false;
                PublishLocalActivity(false);
                _ = _pttRoom.ReleasePttCaptureAsync();
                return true;
            }
            ResolvedTurnTakingOptions policy = ResolveCurrentPolicy();
            if (policy.Mode != ConversationInputMode.PushToTalk || !IsPressed)
                return false;

            IsPressed = false;
            PublishLocalActivity(false);
            _activeTurnGeneration++;
            _speechStartedForAwaitingTurn = false;

            IsAwaitingTurnCompletion = true;
            BeginPendingRelease(policy, _activeTurnGeneration);
            StartTurnCompletionTimeout(policy, _activeTurnGeneration);
            return true;
        }

        private bool TryResolveDependencies()
        {
            TryResolveCaptureServices();
            ConvaiManager manager = ResolveManager();
            if (manager == null) return false;

            if (_eventHub == null)
                manager.TryGetEventHub(out _eventHub);

            SubscribeToEvents();
            BindPttRoom();

            return _connectionService != null && _audioService != null && _eventHub != null;
        }

        private void TryResolveCaptureServices()
        {
            if (_connectionService != null && _audioService != null)
                return;

            ConvaiManager manager = ResolveManager();
            if (manager == null)
                return;

            if (_connectionService == null)
                manager.TryGetRoomConnectionService(out _connectionService);
            if (_audioService == null)
                manager.TryGetRoomAudioService(out _audioService);
        }

        private ConvaiManager ResolveManager()
        {
            if (_manager != null)
                return _manager;

            _manager = ConvaiManager.ActiveManager ?? FindAnyObjectByType<ConvaiManager>();
            return _manager;
        }

        private ResolvedTurnTakingOptions ResolveCurrentPolicy()
        {
            if (_resolvedPolicyProviderOverride != null)
                return _resolvedPolicyProviderOverride();

            ConvaiManager manager = ResolveManager();
            if (manager != null && manager.TryGetRoomManager(out ConvaiRoomManager roomManager))
                return roomManager.CurrentResolvedTurnTakingOptions;

            return ResolvedTurnTakingOptions.DefaultHandsFree;
        }

        private ConvaiCharacter ResolveTargetCharacter()
        {
            if (_targetCharacter != null)
                return _targetCharacter;

            ConvaiManager manager = ResolveManager();
            if (manager == null)
                return null;

            if (_useActiveConversationCharacter && manager.AddressedCharacter != null)
                return manager.AddressedCharacter;

            return manager.Characters.Count == 1 ? manager.Characters[0] : null;
        }

        private string ResolveTargetCharacterId()
        {
            if (!string.IsNullOrWhiteSpace(_resolvedTargetCharacterIdOverride))
                return _resolvedTargetCharacterIdOverride;

            return ResolveTargetCharacter()?.CharacterId ?? string.Empty;
        }

        private void OpenLocalMicrophone(ResolvedTurnTakingOptions policy)
        {
            if (policy.PushToTalkStartupMode == PushToTalkMicStartupMode.OpenOnFirstPress &&
                !_hasOpenedMicrophoneThisSession)
            {
                _audioService.SetMicMuted(false);
                _audioService.StartListeningAsync();
                _hasOpenedMicrophoneThisSession = true;
                return;
            }

            _audioService.SetMicMuted(false);
        }

        private bool AbortActiveCaptureIfNeeded()
        {
            if (_usingCorrelatedPtt && _pttRoom != null)
            {
                if (IsPressed) Release();
                return !_pttRoom.HasPendingPttRelease;
            }
            if (!IsPressed && !_pendingRelease)
                return true;

            CloseLocalCapture();
            TryResolveCaptureServices();

            ResolvedTurnTakingOptions policy = _pendingReleasePolicy ?? ResolveCurrentPolicy();
            _activeTurnGeneration++;
            CancelReleaseTailCoroutine();
            bool forceStopAlreadySent = _pendingRelease && _forceStopSentForPendingRelease;
            _pendingRelease = false;
            _pendingReleasePolicy = null;
            _forceStopSentForPendingRelease = false;
            _isPostStopFinalizationWindow = false;

            bool forceStopSent = forceStopAlreadySent || TrySendAuthoritativeStop();
            bool serverSttClosed = CloseServerStt(policy);
            if (!forceStopSent)
                ReportAuthoritativeStopFailure();
            else if (!serverSttClosed)
                ReportServerSttControlFailure();

            return forceStopSent && serverSttClosed;
        }

        private void BeginPendingRelease(ResolvedTurnTakingOptions policy, int generation)
        {
            _forceStopSentForPendingRelease = false;
            _isPostStopFinalizationWindow = false;
            _pendingRelease = true;
            _pendingReleaseGeneration = generation;
            _pendingReleasePolicy = policy;

            if (policy.ReleaseTailMs <= 0 || !isActiveAndEnabled)
            {
                CompletePendingRelease(generation);
                return;
            }

            _releaseTailCoroutine =
                StartCoroutine(ReleaseTailCoroutine(policy.ReleaseTailMs, generation, postStopWindow: false));
        }

        private IEnumerator ReleaseTailCoroutine(int releaseTailMs, int generation, bool postStopWindow)
        {
            yield return new WaitForSecondsRealtime(releaseTailMs / 1000f);
            _releaseTailCoroutine = null;

            if (postStopWindow)
                CompletePendingRelease(generation);
            else
                BeginPostStopFinalization(generation);
        }

        private void BeginPostStopFinalization(int generation)
        {
            if (!_pendingRelease || generation != _pendingReleaseGeneration)
                return;

            CloseLocalCapture();
            _isPostStopFinalizationWindow = true;
            TrySendForceStopForPendingRelease();

            int releaseTailMs = _pendingReleasePolicy.ReleaseTailMs;
            if (releaseTailMs <= 0 || !isActiveAndEnabled)
            {
                CompletePendingRelease(generation);
                return;
            }

            _releaseTailCoroutine =
                StartCoroutine(ReleaseTailCoroutine(releaseTailMs, generation, postStopWindow: true));
        }

        private void CompletePendingRelease(int generation)
        {
            if (!_pendingRelease || generation != _pendingReleaseGeneration)
                return;

            ResolvedTurnTakingOptions policy = _pendingReleasePolicy;
            CancelReleaseTailCoroutine();
            CloseLocalCapture();
            bool forceStopSent = TrySendForceStopForPendingRelease();
            _pendingRelease = false;
            _pendingReleasePolicy = null;
            _forceStopSentForPendingRelease = false;
            _isPostStopFinalizationWindow = false;
            bool serverSttClosed = CloseServerStt(policy);
            if (!forceStopSent)
            {
                ClearAwaitingTurn("Push-to-talk could not signal the end of speech.");
                ReportAuthoritativeStopFailure();
            }
            else if (!serverSttClosed)
                ReportServerSttControlFailure();
        }

        private void CancelReleaseTailCoroutine()
        {
            if (_releaseTailCoroutine == null)
                return;

            StopCoroutine(_releaseTailCoroutine);
            _releaseTailCoroutine = null;
        }

        private bool TrySendForceStopForPendingRelease()
        {
            if (_forceStopSentForPendingRelease)
                return true;

            if (!TrySendAuthoritativeStop())
                return false;

            _forceStopSentForPendingRelease = true;
            return true;
        }

        private bool TrySendAuthoritativeStop() =>
            _connectionService != null && _connectionService.ForceUserStoppedSpeaking();

        private void ReportAuthoritativeStopFailure()
        {
            const string message =
                "Push-to-talk could not signal the end of speech because the room control channel is unavailable.";
            BlockedReason = message;
            ConvaiLogger.Warning(message, LogCategory.SDK);
        }

        private void ReportServerSttControlFailure()
        {
            const string message =
                "Push-to-talk could not close backend speech recognition because the room control channel is unavailable.";
            BlockedReason = message;
            ConvaiLogger.Warning(message, LogCategory.SDK);
        }

        private void CloseLocalCapture()
        {
            if (_audioService != null && !_audioService.IsMicMuted)
                _audioService.SetMicMuted(true);
        }

        private bool CloseServerStt(ResolvedTurnTakingOptions policy)
        {
            if (policy == null || !policy.EnableServerSttToggle)
                return true;

            return _connectionService != null && _connectionService.SetSttMuted(true);
        }

        private void SubscribeToEvents()
        {
            if (_eventHub == null)
                return;

            if (_characterSpeechToken == default)
                _characterSpeechToken =
                    _eventHub.Subscribe<CharacterSpeechStateChanged>(HandleCharacterSpeechStateChanged);
            if (_characterTurnCompletedToken == default)
                _characterTurnCompletedToken =
                    _eventHub.Subscribe<CharacterTurnCompleted>(HandleCharacterTurnCompleted);
            if (_llmNoResponseToken == default)
                _llmNoResponseToken = _eventHub.Subscribe<LlmNoResponseReceived>(HandleLlmNoResponse);
            if (_moderationResponseToken == default)
                _moderationResponseToken = _eventHub.Subscribe<ModerationResponseReceived>(HandleModerationResponse);
            if (_playerTranscriptToken == default)
                _playerTranscriptToken = _eventHub.Subscribe<PlayerTranscriptReceived>(HandlePlayerTranscriptReceived);
            if (_usageLimitReachedToken == default)
                _usageLimitReachedToken = _eventHub.Subscribe<UsageLimitReached>(HandleUsageLimitReached);
            if (_sessionErrorToken == default)
                _sessionErrorToken = _eventHub.Subscribe<SessionError>(HandleSessionError);
            if (_sessionStateToken == default)
                _sessionStateToken = _eventHub.Subscribe<SessionStateChanged>(HandleSessionStateChanged);
        }

        private void UnsubscribeFromEvents()
        {
            if (_eventHub == null)
                return;

            if (_characterSpeechToken != default) _eventHub.Unsubscribe(_characterSpeechToken);
            if (_characterTurnCompletedToken != default) _eventHub.Unsubscribe(_characterTurnCompletedToken);
            if (_llmNoResponseToken != default) _eventHub.Unsubscribe(_llmNoResponseToken);
            if (_moderationResponseToken != default) _eventHub.Unsubscribe(_moderationResponseToken);
            if (_playerTranscriptToken != default) _eventHub.Unsubscribe(_playerTranscriptToken);
            if (_usageLimitReachedToken != default) _eventHub.Unsubscribe(_usageLimitReachedToken);
            if (_sessionErrorToken != default) _eventHub.Unsubscribe(_sessionErrorToken);
            if (_sessionStateToken != default) _eventHub.Unsubscribe(_sessionStateToken);

            _characterSpeechToken = default;
            _characterTurnCompletedToken = default;
            _llmNoResponseToken = default;
            _moderationResponseToken = default;
            _playerTranscriptToken = default;
            _usageLimitReachedToken = default;
            _sessionErrorToken = default;
            _sessionStateToken = default;
        }

        private void HandleCharacterSpeechStateChanged(CharacterSpeechStateChanged e)
        {
            if (_usingCorrelatedPtt) return;
            if (!MatchesCurrentTarget(e.CharacterId, null))
                return;

            _targetSpeaking = e.IsSpeaking;
            if (IsAwaitingTurnCompletion && !_speechStartedForAwaitingTurn && e.IsSpeaking)
                _speechStartedForAwaitingTurn = true;

            if (IsAwaitingTurnCompletion &&
                !e.IsSpeaking &&
                _speechStartedForAwaitingTurn &&
                ResolveCurrentPolicy().AllowSpeechStoppedFallbackAfterSpeechStart)
                ClearAwaitingTurn("speech-stopped-fallback");
        }

        private void HandleCharacterTurnCompleted(CharacterTurnCompleted e)
        {
            if (_usingCorrelatedPtt) return;
            if (!MatchesCurrentTarget(e.CharacterId, e.ParticipantId))
                return;

            ClearAwaitingTurn("character-turn-completed", e.ParticipantId);
            _targetSpeaking = false;
        }

        private void HandleLlmNoResponse(LlmNoResponseReceived e)
        {
            if (_usingCorrelatedPtt) return;
            if (!MatchesCurrentTarget(e.CharacterId, e.ParticipantId))
                return;

            ClearAwaitingTurn("llm-no-response", e.ParticipantId);
            _targetSpeaking = false;
        }

        private void HandleModerationResponse(ModerationResponseReceived e)
        {
            if (_usingCorrelatedPtt) return;
            if (IsAwaitingTurnCompletion && e.WasFlagged)
                ClearAwaitingTurn("moderation-blocked");
        }

        private void HandlePlayerTranscriptReceived(PlayerTranscriptReceived e)
        {
            if (_usingCorrelatedPtt) return;
            if (!_pendingRelease || e.Phase != TranscriptionPhase.AsrFinal)
                return;

            string localParticipantId = _connectionService?.CurrentRoom?.LocalParticipant?.Sid;
            if (!string.IsNullOrWhiteSpace(e.ParticipantId) &&
                !string.IsNullOrWhiteSpace(localParticipantId) &&
                !string.Equals(e.ParticipantId, localParticipantId, StringComparison.OrdinalIgnoreCase))
                return;

            if (_isPostStopFinalizationWindow && !_forceStopSentForPendingRelease)
                return;

            CompletePendingRelease(_pendingReleaseGeneration);
        }

        private void HandleUsageLimitReached(UsageLimitReached e)
        {
            if (_usingCorrelatedPtt) return;
            if (IsAwaitingTurnCompletion)
                ClearAwaitingTurn($"usage-limit:{e.QuotaType}");
        }

        private void HandleSessionError(SessionError e)
        {
            if (_usingCorrelatedPtt) return;
            if (IsAwaitingTurnCompletion)
                ClearAwaitingTurn($"session-error:{e.ErrorCode}");
        }

        private void HandleSessionStateChanged(SessionStateChanged e)
        {
            if (e.NewState == SessionState.Connected)
            {
                _hasOpenedMicrophoneThisSession = ResolveCurrentPolicy().ShouldAutoStartMicrophoneAfterConnect;
                return;
            }

            if (e.NewState is SessionState.Disconnected or SessionState.Error)
            {
                CancelReleaseTailCoroutine();
                CloseLocalCapture();
                _pendingRelease = false;
                _pendingReleasePolicy = null;
                _forceStopSentForPendingRelease = false;
                _isPostStopFinalizationWindow = false;
                _hasOpenedMicrophoneThisSession = false;
                _targetSpeaking = false;
                // Every path that drops the press has to say so. Release() refuses to run once
                // IsPressed is false, so a press cleared quietly here is never followed by a
                // falling edge: the player's physical release is rejected and the hold stands.
                if (IsPressed) PublishLocalActivity(false);
                IsPressed = false;
                BlockedReason = string.Empty;
                ClearAwaitingTurn($"session-state:{e.NewState}");
            }
        }

        private bool MatchesCurrentTarget(string characterId, string participantId)
        {
            if (!IsAwaitingTurnCompletion && string.IsNullOrWhiteSpace(_activeTargetCharacterId))
                return MatchesResolvedTarget(characterId);

            if (!string.IsNullOrWhiteSpace(_activeTargetParticipantId) &&
                string.Equals(_activeTargetParticipantId, participantId, StringComparison.OrdinalIgnoreCase))
                return true;

            return !string.IsNullOrWhiteSpace(_activeTargetCharacterId) &&
                   string.Equals(_activeTargetCharacterId, characterId, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesResolvedTarget(string characterId)
        {
            if (!string.IsNullOrWhiteSpace(_resolvedTargetCharacterIdOverride))
            {
                return string.Equals(
                    _resolvedTargetCharacterIdOverride,
                    characterId,
                    StringComparison.OrdinalIgnoreCase);
            }

            ConvaiCharacter targetCharacter = ResolveTargetCharacter();
            return targetCharacter != null &&
                   string.Equals(targetCharacter.CharacterId, characterId, StringComparison.OrdinalIgnoreCase);
        }

        private void StartTurnCompletionTimeout(ResolvedTurnTakingOptions policy, int generation)
        {
            StopTurnCompletionTimeout();
            if (policy.TurnCompletionTimeoutMs <= 0)
                return;

            _turnCompletionTimeoutCoroutine =
                StartCoroutine(TurnCompletionTimeoutCoroutine(policy.TurnCompletionTimeoutMs, generation));
        }

        private IEnumerator TurnCompletionTimeoutCoroutine(int timeoutMs, int generation)
        {
            yield return new WaitForSecondsRealtime(timeoutMs / 1000f);

            if (!IsAwaitingTurnCompletion || generation != _activeTurnGeneration)
                yield break;

            ClearAwaitingTurn("timeout");
        }

        private void StopTurnCompletionTimeout()
        {
            if (_turnCompletionTimeoutCoroutine == null)
                return;

            StopCoroutine(_turnCompletionTimeoutCoroutine);
            _turnCompletionTimeoutCoroutine = null;
        }

        private void ClearAwaitingTurn(string reason, string participantId = null)
        {
            StopTurnCompletionTimeout();
            IsAwaitingTurnCompletion = false;
            _speechStartedForAwaitingTurn = false;
            _activeTargetCharacterId = string.Empty;
            _activeTargetParticipantId = string.Empty;
            BlockedReason = reason ?? string.Empty;
        }

        private bool Reject(string reason)
        {
            BlockedReason = reason ?? string.Empty;
            return false;
        }

        internal void InjectForTests(
            ConvaiManager manager,
            IEventHub eventHub,
            IConvaiRoomConnectionService connectionService,
            IConvaiRoomAudioService audioService,
            Func<ResolvedTurnTakingOptions> resolvedPolicyProvider)
        {
            _manager = manager;
            _eventHub = eventHub;
            _connectionService = connectionService;
            _audioService = audioService;
            _resolvedPolicyProviderOverride = resolvedPolicyProvider;
            SubscribeToEvents();
        }

        internal void SetTargetCharacterIdForTests(string characterId) =>
            _resolvedTargetCharacterIdOverride = characterId;

        internal bool AbortActiveCaptureForTests() => AbortActiveCaptureIfNeeded();

        internal bool HasPendingReleaseForTests => _pendingRelease;

        internal bool IsCapturingOrFinalizing => IsPressed || _pendingRelease;

        internal bool HasReleaseTailCoroutineForTests => _releaseTailCoroutine != null;

        internal void ExpireReleaseTailForTests()
        {
            if (!_pendingRelease)
                return;

            CancelReleaseTailCoroutine();
            if (_isPostStopFinalizationWindow)
                CompletePendingRelease(_pendingReleaseGeneration);
            else
                BeginPostStopFinalization(_pendingReleaseGeneration);
        }
    }
}
