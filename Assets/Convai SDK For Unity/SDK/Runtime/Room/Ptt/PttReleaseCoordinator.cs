using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Core.Async;

namespace Convai.Runtime.Room
{
    /// <summary>One room owns capture closure and commit lifetime, including when a UI controller is disabled.</summary>
    internal sealed class PttReleaseCoordinator : IDisposable
    {
        private readonly Func<string, CancellationToken, Task> _open;
        private readonly Func<CancellationToken, Task> _drain;
        private readonly Func<string, CancellationToken, Task> _mute;
        private readonly Func<string, string, CancellationToken, Task<PttCommitResult>> _commit;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<string> _retiredResponses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _completedResponses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _startedResponses = new(StringComparer.Ordinal);
        private Task _opening;
        private Task<PttCommitResult> _release;
        private bool _releaseRequested, _disposed;
        private bool _allowSpeechStoppedFallback;
        private int _generation;
        private int _botTimeoutMs;
        public PttState State { get; private set; } = PttState.Idle;
        public string ReleaseId { get; private set; }
        public string ParticipantId { get; private set; }
        public string ResponseId { get; private set; }
        public PttCommitResult LastResult { get; private set; }
        public string Error { get; private set; }
        public bool IsBusy => State is PttState.Preparing or PttState.Capturing or PttState.Draining or PttState.AwaitingCommit;
        public event Action<PttState> StateChanged;
        public event Action<PttCommitResult> CommitCompleted;

        public PttReleaseCoordinator(Func<string, CancellationToken, Task> open, Func<CancellationToken, Task> drain,
            Func<string, CancellationToken, Task> mute,
            Func<string, string, CancellationToken, Task<PttCommitResult>> commit)
        { _open = open; _drain = drain; _mute = mute; _commit = commit; }

        public bool TryPress(string participantId, bool interrupt, Action interruptBot, int botTimeoutMs,
            bool requireTurnCompletion = true, bool allowSpeechStoppedFallbackAfterSpeechStart = false)
        {
            if (_disposed || IsBusy || string.IsNullOrWhiteSpace(participantId) ||
                (State == PttState.Error && LastResult?.ReconnectRequired != false)) return false;
            if (State == PttState.AwaitingBotTurn)
            {
                if (interrupt) interruptBot();
                else if (requireTurnCompletion) return false;
            }
            if (!string.IsNullOrEmpty(ResponseId)) RememberRetired(ResponseId);
            ReleaseId = Guid.NewGuid().ToString();
            ParticipantId = participantId;
            ResponseId = null;
            LastResult = null;
            Error = null;
            _completedResponses.Clear();
            _startedResponses.Clear();
            _allowSpeechStoppedFallback = allowSpeechStoppedFallbackAfterSpeechStart;
            _release = null;
            _releaseRequested = false;
            _botTimeoutMs = botTimeoutMs;
            _generation++;
            var opening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _opening = opening.Task;
            SetState(PttState.Preparing);
            _ = OpenAsync(opening);
            return State != PttState.Error;
        }

        private async Task OpenAsync(TaskCompletionSource<bool> opening)
        {
            try
            {
                await PttAsync.WithTimeout(_open(ParticipantId, _lifetime.Token), 8000, _lifetime.Token);
                if (!_releaseRequested && !_disposed) SetState(PttState.Capturing);
            }
            catch (Exception) { Fail("ptt_capture_start_failed"); }
            finally { opening.TrySetResult(true); }
        }

        public Task<PttCommitResult> ReleaseAsync()
        {
            if (_release != null) return _release;
            if (!IsBusy) return Task.FromException<PttCommitResult>(new InvalidOperationException("No active PTT capture."));
            _releaseRequested = true;
            var completion = new TaskCompletionSource<PttCommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = completion.Task;
            _ = CompleteReleaseAsync(completion);
            return _release;
        }

        private async Task CompleteReleaseAsync(TaskCompletionSource<PttCommitResult> completion) =>
            completion.TrySetResult(await ReleaseCoreAsync());

        private async Task<PttCommitResult> ReleaseCoreAsync()
        {
            try
            {
                await _opening;
                if (State == PttState.Error) return LastResult;
                SetState(PttState.Draining);
                await PttAsync.WithTimeout(_drain(_lifetime.Token), 5000, _lifetime.Token);
                await PttAsync.WithTimeout(_mute(ParticipantId, _lifetime.Token), 5000, _lifetime.Token);
                SetState(PttState.AwaitingCommit);
                PttCommitResult result = await _commit(ReleaseId, ParticipantId, _lifetime.Token);
                if (_disposed) return result;
                LastResult = result;
                Error = result.Reason;
                if (!string.IsNullOrEmpty(result.ResponseId)) ResponseId = result.ResponseId;
                if (result.ReconnectRequired || result.Status == PttCommitStatus.Failed) SetState(PttState.Error);
                else if (result.Status == PttCommitStatus.Empty ||
                         (ResponseId != null && _completedResponses.Contains(ResponseId))) SetState(PttState.Idle);
                else
                {
                    SetState(PttState.AwaitingBotTurn);
                    _ = WatchBotTurnAsync(_generation);
                }
                CommitCompleted?.Invoke(result);
                return result;
            }
            catch (Exception) { Fail(_disposed ? "session_closed" : "ptt_release_failed"); return LastResult; }
        }

        // Current Core does not include a response_id on every commit result.
        // Bind the first new owner observed for this target after release; never
        // substitute a different owner after one has been bound.
        public void ObserveSpeech(string participantId, string responseId, bool speaking)
        {
            if (string.IsNullOrWhiteSpace(responseId)) return;
            if (State is PttState.Idle or PttState.Capturing or PttState.Preparing)
            { RememberRetired(responseId); return; }
            if (participantId != ParticipantId || _retiredResponses.Contains(responseId)) return;
            if (State is not (PttState.Draining or PttState.AwaitingCommit or PttState.AwaitingBotTurn)) return;
            if (speaking)
            {
                ResponseId ??= responseId;
                if (_startedResponses.Count < 32) _startedResponses.Add(responseId);
            }
            else if (_allowSpeechStoppedFallback && _startedResponses.Contains(responseId))
                ObserveCompletion(participantId, responseId);
        }
        public void ObserveCompletion(string participantId, string responseId)
        {
            if (string.IsNullOrWhiteSpace(responseId) || participantId != ParticipantId ||
                _retiredResponses.Contains(responseId)) return;
            if (State is not (PttState.Draining or PttState.AwaitingCommit or PttState.AwaitingBotTurn)) return;
            // Silent or aborted responses can complete without ever emitting speech-start.
            ResponseId ??= responseId;
            if (State is PttState.Draining or PttState.AwaitingCommit)
            {
                if (_completedResponses.Count < 32) _completedResponses.Add(responseId);
                return;
            }
            if (State == PttState.AwaitingBotTurn && ResponseId == responseId) SetState(PttState.Idle);
        }
        private void RememberRetired(string id)
        {
            if (_retiredResponses.Count >= 256) _retiredResponses.Clear();
            _retiredResponses.Add(id);
        }
        private async Task WatchBotTurnAsync(int generation)
        {
            try
            {
                if (_botTimeoutMs <= 0) return;
                await ConvaiTimers.Delay(_botTimeoutMs, _lifetime.Token);
                if (!_disposed && generation == _generation && State == PttState.AwaitingBotTurn)
                { Error = "bot_turn_timeout"; SetState(PttState.Error); }
            }
            catch (OperationCanceledException) { }
        }
        private void Fail(string reason)
        {
            if (State == PttState.Error) return;
            Error = reason;
            _lifetime.Cancel();
            LastResult = new PttCommitResult(ReleaseId, PttCommitStatus.Failed, reason: reason, reconnectRequired: true);
            SetState(PttState.Error);
            CommitCompleted?.Invoke(LastResult);
        }
        private void SetState(PttState state) { State = state; StateChanged?.Invoke(state); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            if (IsBusy) Fail("session_closed");
        }
    }
}
