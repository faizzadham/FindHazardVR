using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Core.Async;
using Newtonsoft.Json.Linq;

namespace Convai.Runtime.Room
{
    internal sealed class PttCommitTracker : IDisposable
    {
        private sealed class Pending
        {
            public string ParticipantId;
            public TaskCompletionSource<PttCommitResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly Dictionary<string, Pending> _requests = new(StringComparer.Ordinal);
        private readonly Queue<string> _completed = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Func<string, string, CancellationToken, Task> _send;
        private readonly Func<int, CancellationToken, Task> _delay;
        private string _activeRelease;
        private bool _disposed;

        public PttCommitTracker(Func<string, string, CancellationToken, Task> send,
            Func<int, CancellationToken, Task> delay = null)
        {
            _send = send;
            _delay = delay ?? ConvaiTimers.Delay;
        }

        public Task<PttCommitResult> CommitAsync(string releaseId, string participantId, CancellationToken waitToken)
        {
            if (_disposed) return Task.FromException<PttCommitResult>(new ObjectDisposedException(nameof(PttCommitTracker)));
            if (string.IsNullOrWhiteSpace(releaseId) || string.IsNullOrWhiteSpace(participantId))
                return Task.FromException<PttCommitResult>(new ArgumentException("Release ID and target participant are required."));
            if (!_requests.TryGetValue(releaseId, out Pending request))
            {
                if (_activeRelease != null && !_requests[_activeRelease].Completion.Task.IsCompleted)
                    return Task.FromException<PttCommitResult>(new InvalidOperationException("A PTT release is already pending."));
                request = new Pending { ParticipantId = participantId };
                _requests.Add(releaseId, request); // Before publication: in-process replies are legal.
                _activeRelease = releaseId;
                _ = SendAndAwaitAsync(releaseId, request);
            }
            else if (request.ParticipantId != participantId)
                return Task.FromException<PttCommitResult>(new InvalidOperationException("A release ID cannot change target."));
            return AwaitCallerAsync(request.Completion.Task, waitToken);
        }

        private async Task SendAndAwaitAsync(string releaseId, Pending request)
        {
            using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            ConvaiTimers.CancelAfter(requestLifetime, 10000);
            CancellationToken token = requestLifetime.Token;
            try
            {
                for (int elapsed = 0; elapsed < 10000 && !request.Completion.Task.IsCompleted; elapsed += 2000)
                {
                    if (elapsed <= 4000)
                        await PttAsync.WithTimeout(_send(releaseId, request.ParticipantId, token), 5000, token);
                    await ConvaiTimers.WhenAny(request.Completion.Task, _delay(2000, token));
                    token.ThrowIfCancellationRequested();
                }
                request.Completion.TrySetResult(new PttCommitResult(releaseId, PttCommitStatus.Failed,
                    reason: "ptt_commit_timeout", reconnectRequired: true));
            }
            catch (Exception error)
            {
                request.Completion.TrySetResult(new PttCommitResult(releaseId, PttCommitStatus.Failed,
                    reason: error is OperationCanceledException ? (_disposed ? "session_closed" : "ptt_commit_timeout") : "ptt_send_failed", reconnectRequired: true));
            }
            finally
            {
                if (_activeRelease == releaseId) _activeRelease = null;
                _completed.Enqueue(releaseId);
                while (_completed.Count > 256) _requests.Remove(_completed.Dequeue());
            }
        }

        public void Receive(string participantId, JObject payload)
        {
            var extras = payload?["extras"] as JObject;
            string id = extras?.Value<string>("stop_id");
            if (id == null || !_requests.TryGetValue(id, out Pending request) ||
                request.ParticipantId != participantId) return;
            string reason = extras.Value<string>("reason");
            if (reason == "stt_finalization_pending") return;
            string status = extras.Value<string>("status");
            if (status != "committed" && status != "empty" && status != "failed")
            {
                if (payload.Value<string>("status") != "error") return;
                status = "failed";
            }
            bool reconnect = extras.Value<bool?>("reconnect_required") == true ||
                reason is "stt_reset_failed" or "stt_reconnect_failed" or "user_input_commit_timeout";
            request.Completion.TrySetResult(new PttCommitResult(id,
                status == "committed" ? PttCommitStatus.Committed : status == "empty" ? PttCommitStatus.Empty : PttCommitStatus.Failed,
                extras.Value<string>("response_id"), reason, reconnect));
        }

        internal static async Task<T> AwaitCallerAsync<T>(Task<T> task, CancellationToken token)
        {
            if (!token.CanBeCanceled) return await task;
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (await ConvaiTimers.WhenAny(task, canceled.Task) != task) token.ThrowIfCancellationRequested();
                return await task;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            foreach (var entry in _requests)
                entry.Value.Completion.TrySetResult(new PttCommitResult(entry.Key, PttCommitStatus.Failed,
                    reason: "session_closed", reconnectRequired: true));
            _requests.Clear();
        }
    }

    internal static class PttAsync
    {
        public static async Task WithTimeout(Task task, int milliseconds, CancellationToken token)
        {
            if (await ConvaiTimers.WhenAny(task, ConvaiTimers.Delay(milliseconds, token)) != task)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("PTT operation timed out.");
            }
            await task;
        }
    }
}
