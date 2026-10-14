using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Policies;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Core
{
    /// <summary>
    ///     The player-loop queue behind <see cref="ConvaiTimers" /> in a WebGL player, driven here by a fake clock.
    /// </summary>
    [TestFixture]
    public sealed class PlayerLoopTimerQueueTests
    {
        private double _now;
        private PlayerLoopTimerQueue _queue;

        [SetUp]
        public void SetUp()
        {
            _now = 0;
            _queue = new PlayerLoopTimerQueue(() => _now);
        }

        [Test]
        public void Delay_CompletesOnTheFirstTickAfterItIsDue()
        {
            Task delay = _queue.Delay(200, CancellationToken.None);

            _queue.Tick();
            _now = 0.199;
            _queue.Tick();
            Assert.That(delay.IsCompleted, Is.False);

            _now = 0.2;
            Assert.That(delay.IsCompleted, Is.False, "A due delay must wait for the tick.");
            _queue.Tick();
            Assert.That(delay.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(_queue.PendingCount, Is.Zero);
        }

        [Test]
        public void Delay_CompletesAWhenAnyRaceWithinTheTick()
        {
            Task never = new TaskCompletionSource<bool>().Task;
            Task race = Task.WhenAny(never, _queue.Delay(200, CancellationToken.None));

            _now = 0.2;
            _queue.Tick();

            Assert.That(race.IsCompleted, Is.True,
                "WhenAny's completion was deferred to the thread pool, which a WebGL player never runs.");
        }

        [Test]
        public void GateHandover_CompletesAWhenAnyRaceWithinTheTick()
        {
            var gate = new SemaphoreSlim(0, 1);
            Task race = Task.WhenAny(new TaskCompletionSource<bool>().Task,
                _queue.WaitAsync(gate, CancellationToken.None));

            gate.Release();
            _queue.Tick();

            Assert.That(race.IsCompleted, Is.True,
                "WhenAny's completion was deferred to the thread pool, which a WebGL player never runs.");
        }

        [Test]
        public void Delay_CancelledByItsToken_IsCanceledAndDropped()
        {
            using var cts = new CancellationTokenSource();
            Task delay = _queue.Delay(60000, cts.Token);

            cts.Cancel();

            Assert.That(delay.IsCanceled, Is.True);
            _queue.Tick();
            Assert.That(_queue.PendingCount, Is.Zero);
        }

        [Test]
        public void Schedule_RunsDueActionsInDueOrder_AndKeepsGoingAfterAFailure()
        {
            var ran = new List<string>();
            var reported = new List<Exception>();
            _queue.ReportException = reported.Add;
            _queue.Schedule(300, () => ran.Add("late"));
            _queue.Schedule(100, () => throw new InvalidOperationException("boom"));
            _queue.Schedule(100, () => ran.Add("early"));

            _now = 1;
            _queue.Tick();

            CollectionAssert.AreEqual(new[] { "early", "late" }, ran);
            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(reported[0].Message, Is.EqualTo("boom"));
        }

        [Test]
        public void WaitAsync_HandsAContendedGateOverInRequestOrder()
        {
            var gate = new SemaphoreSlim(1, 1);
            Task first = _queue.WaitAsync(gate, CancellationToken.None);
            Task second = _queue.WaitAsync(gate, CancellationToken.None);
            Task third = _queue.WaitAsync(gate, CancellationToken.None);
            Assert.That(first.IsCompleted, Is.True, "A free gate is taken at once.");

            _queue.Tick();
            Assert.That(second.IsCompleted, Is.False);

            gate.Release();
            Task latecomer = _queue.WaitAsync(gate, CancellationToken.None);
            Assert.That(latecomer.IsCompleted, Is.False, "A new caller must not overtake queued waiters.");

            _queue.Tick();
            Assert.That(second.IsCompleted, Is.True);
            Assert.That(third.IsCompleted, Is.False);

            gate.Release();
            _queue.Tick();
            Assert.That(third.IsCompleted, Is.True);
            Assert.That(latecomer.IsCompleted, Is.False);
        }

        [Test]
        public void WaitAsync_CancelledWaiter_NeverTakesTheGate()
        {
            var gate = new SemaphoreSlim(0, 1);
            using var cts = new CancellationTokenSource();
            Task waiter = _queue.WaitAsync(gate, cts.Token);

            cts.Cancel();
            gate.Release();
            _queue.Tick();

            Assert.That(waiter.IsCanceled, Is.True);
            Assert.That(gate.CurrentCount, Is.EqualTo(1), "The cancelled waiter must leave the gate free.");
        }
    }

    /// <summary>
    ///     Sites that used a framework timer now wait on the player loop when <see cref="ConvaiTimers.UsePlayerLoop" />
    ///     is on. Each would finish on its own after a real wait if it still used the framework timer.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiTimersPlayerLoopTests
    {
        private PlayerLoopScope _loop;

        [SetUp]
        public void SetUp() => _loop = new PlayerLoopScope();

        [TearDown]
        public void TearDown() => _loop.Dispose();

        [Test]
        public void RetryExecutor_WaitsForThePlayerLoopBeforeRetrying()
        {
            int attempts = 0;
            var executor = new RetryExecutor(new FixedRetryPolicy(TimeSpan.FromMilliseconds(50)));
            Task<int> run = executor.ExecuteAsync((attempt, _) =>
            {
                Interlocked.Increment(ref attempts);
                return attempt == 0
                    ? Task.FromException<int>(new InvalidOperationException("first attempt fails"))
                    : Task.FromResult(attempt);
            });

            Thread.Sleep(300);
            Assert.That(Volatile.Read(ref attempts), Is.EqualTo(1),
                "The backoff elapsed on a framework timer instead of waiting for the player loop.");

            _loop.Advance(0.05);

            Assert.That(run.Wait(TimeSpan.FromSeconds(2)), Is.True, "The retry never ran after the backoff.");
            Assert.That(run.Result, Is.EqualTo(1));
        }

        [Test]
        public void TimeoutSource_CancelsOnThePlayerLoop()
        {
            using CancellationTokenSource timeout = ConvaiTimers.CreateTimeoutSource(50);

            Thread.Sleep(200);
            Assert.That(timeout.IsCancellationRequested, Is.False);

            _loop.Advance(0.05);
            Assert.That(timeout.IsCancellationRequested, Is.True);
        }

        [Test]
        public void CancelAfter_OnADisposedSource_DoesNotThrowOnTheTick()
        {
            var source = new CancellationTokenSource();
            ConvaiTimers.CancelAfter(source, 10);
            source.Dispose();

            Assert.DoesNotThrow(() => _loop.Advance(1));
        }

        private sealed class FixedRetryPolicy : IRetryPolicy
        {
            private readonly TimeSpan _delay;

            public FixedRetryPolicy(TimeSpan delay) => _delay = delay;

            public int MaxAttempts => 2;

            public TimeSpan GetDelay(int attempt) => attempt == 0 ? TimeSpan.Zero : _delay;

            public bool ShouldRetry(Exception exception, int attempt) => true;
        }
    }

    /// <summary>Switches <see cref="ConvaiTimers" /> to a fake-clock player loop until disposed.</summary>
    internal sealed class PlayerLoopScope : IDisposable
    {
        private readonly bool _previousUsePlayerLoop;
        private readonly PlayerLoopTimerQueue _previousQueue;
        private double _now;

        public PlayerLoopScope()
        {
            _previousUsePlayerLoop = ConvaiTimers.UsePlayerLoop;
            _previousQueue = ConvaiTimers.Queue;
            ConvaiTimers.Queue = new PlayerLoopTimerQueue(() => _now);
            ConvaiTimers.UsePlayerLoop = true;
        }

        /// <summary>Moves the fake clock forward and runs one player-loop tick.</summary>
        public void Advance(double seconds)
        {
            _now += seconds;
            ConvaiTimers.Tick();
        }

        public void Dispose()
        {
            ConvaiTimers.UsePlayerLoop = _previousUsePlayerLoop;
            ConvaiTimers.Queue = _previousQueue;
        }
    }
}
