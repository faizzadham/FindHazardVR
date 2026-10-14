using System;
using Convai.Runtime.Core.Coordinators;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Room;

namespace Convai.Runtime.Adapters.Networking
{
    public partial class ConvaiRoomManager
    {
        private PttReleaseCoordinator _pttRelease;
        private RTVIHandler _pttHandler;
        private string _pttCharacterId;
        private SubscriptionToken _pttSpeechToken, _pttCompletionToken;

        public PttState PttState => _pttRelease?.State ?? PttState.Idle;
        public string PttReleaseId => _pttRelease?.ReleaseId ?? string.Empty;
        public string PttError => _pttRelease?.Error ?? string.Empty;
        public PttCommitResult LastPttCommitResult => _pttRelease?.LastResult;
        public event Action<PttState> PttStateChanged;
        public event Action<PttCommitResult> PttCommitCompleted;
        internal bool HasPendingPttRelease => _pttRelease?.IsBusy == true;
        internal bool UsesCorrelatedPtt => CurrentResolvedTurnTakingOptions.ReleaseMode != PttReleaseMode.Legacy &&
            CurrentResolvedTurnTakingOptions.EnableServerSttToggle &&
            _microphoneSourceFactory is IMicrophoneCaptureDrainFactory { SupportsCaptureDrain: true };
        public string PttCompatibilityReason => UsesCorrelatedPtt ? string.Empty :
            CurrentResolvedTurnTakingOptions.ReleaseMode == PttReleaseMode.Legacy
                ? "Legacy release was selected in the room policy."
                : !CurrentResolvedTurnTakingOptions.EnableServerSttToggle
                ? "Server STT control is disabled in the room policy; legacy release is active."
                : "This platform does not expose a microphone sender drain boundary; legacy release is active.";

        internal bool BeginPttCapture(string characterId)
        {
            RTVIHandler handler = RtvHandler;
            if (handler == null || !UsesCorrelatedPtt) return false;
            if (_pttHandler != handler)
            {
                ResetPttSession();
                _pttHandler = handler;
                handler.PttSessionInvalidated += ResetPttSession;
                _pttRelease = new PttReleaseCoordinator(
                    async (participant, ct) =>
                    {
                        await handler.SendPttControlAsync(new RTVISttToggle(false), participant, ct);
                        SetMicMuted(false);
                        await StartListeningAsync(cancellationToken: ct).AsTask();
                    },
                    ct => _roomAudioRuntimeAdapter.StopAndDrainCaptureAsync(ct),
                    (participant, ct) => handler.SendPttControlAsync(new RTVISttToggle(true), participant, ct),
                    (id, participant, ct) => handler.PttCommits.CommitAsync(id, participant, ct));
                _pttRelease.StateChanged += state =>
                {
                    if (state == PttState.Error)
                    {
                        SetMicMuted(true);
                        _ = StopPttCaptureAfterFailureAsync();
                    }
                    PttStateChanged?.Invoke(state);
                };
                _pttRelease.CommitCompleted += result => PttCommitCompleted?.Invoke(result);
                if (_eventHub != null)
                {
                    _pttSpeechToken = _eventHub.Subscribe<CharacterSpeechStateChanged>(speech =>
                    {
                        if (speech.CharacterId == _pttCharacterId)
                            _pttRelease?.ObserveSpeech(handler.ResolvePttParticipant(speech.CharacterId), speech.ResponseId, speech.IsSpeaking);
                    });
                    _pttCompletionToken = _eventHub.Subscribe<CharacterTurnCompleted>(completion =>
                        _pttRelease?.ObserveCompletion(completion.ParticipantId, completion.ResponseId));
                }
            }
            _pttCharacterId = characterId;
            return _pttRelease.TryPress(handler.ResolvePttParticipant(characterId),
                CurrentResolvedTurnTakingOptions.InterruptBotOnPress, () => InterruptBot(),
                CurrentResolvedTurnTakingOptions.TurnCompletionTimeoutMs,
                CurrentResolvedTurnTakingOptions.RequireTurnCompletionBeforeNextPress,
                CurrentResolvedTurnTakingOptions.AllowSpeechStoppedFallbackAfterSpeechStart);
        }

        private async Task StopPttCaptureAfterFailureAsync()
        {
            try { await StopListeningAsync().AsTask(); }
            catch (Exception) { _logger?.Warning("PTT microphone cleanup failed; reconnect the room."); }
        }

        internal Task<PttCommitResult> ReleasePttCaptureAsync() => _pttRelease?.ReleaseAsync()
            ?? Task.FromException<PttCommitResult>(new InvalidOperationException("No active PTT capture."));

        /// <summary>
        /// Send a stable release ID and await Core's matching terminal result.
        /// The caller must first drain capture and send STT mute. Cancelling this
        /// wait does not discard a release already sent to Core.
        /// </summary>
        public IConvaiOperation<PttCommitResult> ForceUserStoppedSpeakingAsync(string releaseId,
            CancellationToken cancellationToken = default)
        {
            RTVIHandler handler = RtvHandler;
            if (handler == null) return ConvaiOperation<PttCommitResult>.Failed(new InvalidOperationException("Room is disconnected."));
            string participant = _pttRelease?.ParticipantId ?? handler.ResolvePttParticipant();
            return ConvaiOperation<PttCommitResult>.FromTask(handler.PttCommits.CommitAsync(releaseId, participant, cancellationToken));
        }

        /// <summary>
        ///     Explicit user retry ends the session and connects to a fresh room, leaving the old agent
        ///     behind. It never replays the failed capture.
        /// </summary>
        public IConvaiOperation<RoomSession> RetryPttAsync(CancellationToken cancellationToken = default) =>
            ConvaiOperation<RoomSession>.FromTask(RetryPttCoreAsync(cancellationToken));

        private async Task<RoomSession> RetryPttCoreAsync(CancellationToken ct)
        {
            if (PttState != PttState.Error) throw new InvalidOperationException("PTT does not require recovery.");
            await DisconnectAsync(cancellationToken: ct).AsTask();
            return await ConnectAsync(ct).AsTask();
        }

        internal void ClearCompletedPttTurn()
        {
            if (!HasPendingPttRelease && PttState != PttState.Error) ResetPttSession();
        }

        private void ResetPttSession()
        {
            _pttRelease?.Dispose();
            _pttRelease = null;
            if (_pttHandler != null)
            {
                _pttHandler.PttSessionInvalidated -= ResetPttSession;
                _pttHandler.ResetPttRequests();
            }
            _pttHandler = null;
            if (_eventHub != null)
            {
                if (_pttSpeechToken != default) _eventHub.Unsubscribe(_pttSpeechToken);
                if (_pttCompletionToken != default) _eventHub.Unsubscribe(_pttCompletionToken);
            }
            _pttSpeechToken = _pttCompletionToken = default;
            PttStateChanged?.Invoke(PttState.Idle);
        }
    }
}
