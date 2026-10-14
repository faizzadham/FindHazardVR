using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Room;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public class PttReleaseTests
    {
        private static JObject Result(string id, string status, string reason = null, string outerStatus = "success") =>
            new JObject { ["status"] = outerStatus, ["extras"] = new JObject
            { ["stop_id"] = id, ["status"] = status, ["reason"] = reason } };

        [Test]
        public async Task DelayedFinalizationKeepsReleasePendingAndDoesNotOpenAnotherCapture()
        {
            var sent = new List<string>();
            var drain = new TaskCompletionSource<bool>();
            var commit = new TaskCompletionSource<PttCommitResult>();
            using var session = new PttReleaseCoordinator(
                (participant, ct) => { sent.Add("open"); return Task.CompletedTask; },
                ct => { sent.Add("drain"); return drain.Task; },
                (participant, ct) => { sent.Add("mute"); return Task.CompletedTask; },
                (id, participant, ct) => { sent.Add("force:" + id); return commit.Task; });
            Assert.That(session.TryPress("bot", true, () => Assert.Fail("unexpected interruption"), 0), Is.True);
            Task<PttCommitResult> releasing = session.ReleaseAsync();
            Assert.That(session.ReleaseAsync(), Is.SameAs(releasing));
            Assert.That(session.State, Is.EqualTo(PttState.Draining));
            Assert.That(sent, Is.EqualTo(new[] { "open", "drain" }));
            Assert.That(session.TryPress("bot", true, () => Assert.Fail("must not interrupt during release"), 0), Is.False);
            drain.SetResult(true);
            await Until(() => session.State == PttState.AwaitingCommit);
            Assert.That(sent, Is.EqualTo(new[] { "open", "drain", "mute", "force:" + session.ReleaseId }));
            Assert.That(releasing.IsCompleted, Is.False, "A late provider-final result must remain pending.");
            commit.SetResult(new PttCommitResult(session.ReleaseId, PttCommitStatus.Committed, "response-2"));
            await releasing;
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingBotTurn));
            session.ObserveCompletion("bot", "response-1");
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingBotTurn));
            session.ObserveCompletion("bot", "response-2");
            Assert.That(session.State, Is.EqualTo(PttState.Idle));
        }

        [TestCase(PttCommitStatus.Empty, PttState.Idle)]
        [TestCase(PttCommitStatus.Failed, PttState.Error)]
        public async Task EmptyAndFailureDoNotWaitForBotAudio(PttCommitStatus status, PttState state)
        {
            using var session = Session((id, participant, ct) => Task.FromResult(new PttCommitResult(id, status)));
            session.TryPress("bot", false, null, 0);
            await session.ReleaseAsync();
            Assert.That(session.State, Is.EqualTo(state));
        }

        [Test]
        public async Task OptionalBotTurnGuardOnlyAppliesAfterCommit()
        {
            var commit = new TaskCompletionSource<PttCommitResult>();
            using var session = Session((id, participant, ct) => commit.Task);
            session.TryPress("bot", false, null, 0, requireTurnCompletion: false);
            Task<PttCommitResult> releasing = session.ReleaseAsync();
            Assert.That(session.TryPress("bot", false, null, 0, requireTurnCompletion: false), Is.False);
            commit.SetResult(new PttCommitResult(session.ReleaseId, PttCommitStatus.Committed, "response"));
            await releasing;
            Assert.That(session.TryPress("bot", false, null, 0, requireTurnCompletion: false), Is.True);
        }

        [Test]
        public async Task EarlyLifecycleCompletionIsReconciledAfterCommitAndOldStopsCannotUnlockIt()
        {
            var commit = new TaskCompletionSource<PttCommitResult>();
            using var session = Session((id, participant, ct) => commit.Task);
            session.ObserveSpeech("bot", "old", true);
            session.TryPress("bot", false, null, 0);
            Task<PttCommitResult> release = session.ReleaseAsync();
            session.ObserveSpeech("bot", "old", false);
            session.ObserveSpeech("bot", "new", true);
            session.ObserveCompletion("bot", "old");
            session.ObserveCompletion("bot", "new");
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingCommit));
            commit.SetResult(new PttCommitResult(session.ReleaseId, PttCommitStatus.Committed));
            await release;
            Assert.That(session.ResponseId, Is.EqualTo("new"));
            Assert.That(session.State, Is.EqualTo(PttState.Idle));
        }

        [Test]
        public async Task CompletionWithoutSpeechStartFinishesSilentResponseAfterCommit()
        {
            using var session = Session((id, participant, ct) =>
                Task.FromResult(new PttCommitResult(id, PttCommitStatus.Committed)));
            session.ObserveSpeech("bot", "old", true);
            session.TryPress("bot", false, null, 0);
            await session.ReleaseAsync();
            session.ObserveCompletion("other-bot", "other");
            session.ObserveCompletion("bot", "old");
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingBotTurn));
            Assert.That(session.ResponseId, Is.Null);
            session.ObserveCompletion("bot", "silent");
            Assert.That(session.ResponseId, Is.EqualTo("silent"));
            Assert.That(session.State, Is.EqualTo(PttState.Idle));
            Assert.That(session.TryPress("bot", false, null, 0), Is.True);
            await session.ReleaseAsync();
            session.ObserveCompletion("bot", "silent");
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingBotTurn), "Previous turn must stay retired.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EarlySilentCompletionWaitsForCommitAndExplicitResponseIdTakesPrecedence(bool explicitResponse)
        {
            var drain = new TaskCompletionSource<bool>();
            var commit = new TaskCompletionSource<PttCommitResult>();
            using var session = new PttReleaseCoordinator((participant, ct) => Task.CompletedTask,
                ct => drain.Task, (participant, ct) => Task.CompletedTask, (id, participant, ct) => commit.Task);
            session.TryPress("bot", false, null, 0);
            Task<PttCommitResult> release = session.ReleaseAsync();
            session.ObserveCompletion("bot", "silent");
            Assert.That(session.State, Is.EqualTo(PttState.Draining));
            drain.SetResult(true);
            await Until(() => session.State == PttState.AwaitingCommit);
            Assert.That(session.TryPress("bot", false, null, 0), Is.False);
            commit.SetResult(new PttCommitResult(session.ReleaseId, PttCommitStatus.Committed,
                explicitResponse ? "authoritative" : null));
            await release;
            if (explicitResponse)
            {
                Assert.That(session.ResponseId, Is.EqualTo("authoritative"));
                session.ObserveCompletion("bot", "silent");
                Assert.That(session.State, Is.EqualTo(PttState.AwaitingBotTurn));
                session.ObserveCompletion("bot", "authoritative");
            }
            Assert.That(session.State, Is.EqualTo(PttState.Idle));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task SpeechStopFallbackHonorsPolicyAndNeverUnlocksPendingCommit(bool enabled, bool early)
        {
            var commit = new TaskCompletionSource<PttCommitResult>();
            using var session = Session((id, participant, ct) => commit.Task);
            session.TryPress("bot", false, null, 0, allowSpeechStoppedFallbackAfterSpeechStart: enabled);
            Task<PttCommitResult> release = session.ReleaseAsync();
            session.ObserveSpeech("bot", "response", true);
            if (early) session.ObserveSpeech("bot", "response", false);
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingCommit));
            Assert.That(session.TryPress("bot", false, null, 0), Is.False);
            commit.SetResult(new PttCommitResult(session.ReleaseId, PttCommitStatus.Committed));
            await release;
            if (!early) session.ObserveSpeech("bot", "response", false);
            Assert.That(session.State, Is.EqualTo(enabled ? PttState.Idle : PttState.AwaitingBotTurn));
            session.ObserveCompletion("bot", "response");
            Assert.That(session.State, Is.EqualTo(PttState.Idle));
        }

        [Test]
        public async Task SpeechStopFallbackRequiresStartForSameParticipantAndAuthoritativeResponse()
        {
            var commit = new TaskCompletionSource<PttCommitResult>();
            using var session = Session((id, participant, ct) => commit.Task);
            session.TryPress("bot", false, null, 0, allowSpeechStoppedFallbackAfterSpeechStart: true);
            Task<PttCommitResult> release = session.ReleaseAsync();
            session.ObserveSpeech("bot", "provisional", true);
            session.ObserveSpeech("bot", "provisional", false);
            commit.SetResult(new PttCommitResult(session.ReleaseId, PttCommitStatus.Committed, "authoritative"));
            await release;
            session.ObserveSpeech("bot", "provisional", false);
            session.ObserveSpeech("other-bot", "authoritative", true);
            session.ObserveSpeech("bot", "authoritative", false);
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingBotTurn));
            session.ObserveSpeech("bot", "authoritative", true);
            session.ObserveSpeech("other-bot", "authoritative", false);
            session.ObserveSpeech("bot", "mismatched", false);
            Assert.That(session.State, Is.EqualTo(PttState.AwaitingBotTurn));
            session.ObserveSpeech("bot", "authoritative", false);
            Assert.That(session.State, Is.EqualTo(PttState.Idle));
        }

        [Test]
        public async Task DrainFailureNeverSendsMuteOrForceAndRequiresExplicitRecovery()
        {
            using var session = new PttReleaseCoordinator((participant, ct) => Task.CompletedTask,
                ct => Task.FromException(new InvalidOperationException("failed sender")),
                (participant, ct) => throw new AssertionException("Mute must follow successful drain"),
                (id, participant, ct) => throw new AssertionException("Force must follow successful drain"));
            session.TryPress("bot", true, () => {}, 0);
            PttCommitResult result = await session.ReleaseAsync();
            Assert.That(result.Status, Is.EqualTo(PttCommitStatus.Failed));
            Assert.That(result.ReconnectRequired, Is.True);
            Assert.That(session.TryPress("bot", true, () => {}, 0), Is.False);
        }

        [Test]
        public async Task ReleaseFromStateCallbackJoinsOneReleaseAndWaitsForCaptureOpening()
        {
            int commits = 0;
            var opening = new TaskCompletionSource<bool>();
            using var session = new PttReleaseCoordinator((participant, ct) => opening.Task,
                ct => Task.CompletedTask, (participant, ct) => Task.CompletedTask,
                (id, participant, ct) => { commits++; return Task.FromResult(new PttCommitResult(id, PttCommitStatus.Empty)); });
            Task<PttCommitResult> release = null;
            session.StateChanged += state =>
            {
                if (state == PttState.Preparing) release = session.ReleaseAsync();
                if (state == PttState.Draining) Assert.That(session.ReleaseAsync(), Is.SameAs(release));
            };
            Assert.That(session.TryPress("bot", false, null, 0), Is.True);
            Assert.That(release.IsCompleted, Is.False);
            Assert.That(commits, Is.Zero);
            opening.SetResult(true);
            Assert.That((await release).Status, Is.EqualTo(PttCommitStatus.Empty));
            Assert.That(commits, Is.EqualTo(1));
        }

        [Test]
        public async Task TrackerRegistersBeforeSendAndPreservesCommittedOnListeningFailure()
        {
            PttCommitTracker tracker = null;
            tracker = new PttCommitTracker((id, participant, ct) =>
            {
                tracker.Receive(participant, Result(id, "committed", "stt_reconnect_failed", "error"));
                return Task.CompletedTask;
            });
            using (tracker)
            {
                PttCommitResult result = await tracker.CommitAsync("release", "bot", default);
                Assert.That(result.Status, Is.EqualTo(PttCommitStatus.Committed));
                Assert.That(result.ReconnectRequired, Is.True);
            }
        }

        [Test]
        public async Task RetryUsesSameIdAndCallerCancellationDoesNotEraseSentRelease()
        {
            var sends = new List<string>();
            var ticks = new Queue<TaskCompletionSource<bool>>();
            using var tracker = new PttCommitTracker((id, participant, ct) =>
            { sends.Add(id); return Task.CompletedTask; }, (ms, ct) =>
            { var tick = new TaskCompletionSource<bool>(); ticks.Enqueue(tick); return tick.Task; });
            using var caller = new CancellationTokenSource();
            Task<PttCommitResult> abandoned = tracker.CommitAsync("same-release", "bot", caller.Token);
            caller.Cancel();
            try { await abandoned; Assert.Fail("The caller wait should be cancelled."); }
            catch (OperationCanceledException) { }
            Task<PttCommitResult> joined = tracker.CommitAsync("same-release", "bot", default);
            tracker.Receive("wrong-bot", Result("same-release", "committed"));
            tracker.Receive("bot", Result("other-release", "committed"));
            tracker.Receive("bot", Result("same-release", null, "stt_finalization_pending", "error"));
            Assert.That(joined.IsCompleted, Is.False);
            ticks.Dequeue().SetResult(true);
            await Until(() => sends.Count == 2);
            Assert.That(sends, Is.EqualTo(new[] { "same-release", "same-release" }));
            tracker.Receive("bot", Result("same-release", "empty"));
            Assert.That((await joined).Status, Is.EqualTo(PttCommitStatus.Empty));
        }

        [Test]
        public async Task DisconnectInvalidatesPendingRelease()
        {
            var tick = new TaskCompletionSource<bool>();
            var tracker = new PttCommitTracker((id, participant, ct) => Task.CompletedTask, (ms, ct) => tick.Task);
            Task<PttCommitResult> pending = tracker.CommitAsync("release", "bot", default);
            tracker.Dispose();
            Assert.That((await pending).Reason, Is.EqualTo("session_closed"));
        }

        private static PttReleaseCoordinator Session(Func<string, string, CancellationToken, Task<PttCommitResult>> commit) =>
            new((participant, ct) => Task.CompletedTask, ct => Task.CompletedTask,
                (participant, ct) => Task.CompletedTask, commit);

        private static async Task Until(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Yield();
            Assert.That(condition(), Is.True);
        }
    }
}
