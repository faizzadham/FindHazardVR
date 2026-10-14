using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Convai.Runtime.Core.Async
{
    /// <summary>
    ///     Delays, scheduled actions and <see cref="SemaphoreSlim" /> waits completed by an explicit
    ///     <see cref="Tick" /> instead of by timer or thread-pool threads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ConvaiTimers" /> uses one instance in a WebGL player, where the player loop calls
    ///         <see cref="Tick" /> every frame. Tasks it returns complete inline, at the end of the tick, after the
    ///         queue's own lists are settled. They must not use
    ///         <see cref="TaskCreationOptions.RunContinuationsAsynchronously" />: with it, the continuation behind
    ///         <c>Task.WhenAny</c> is queued to the thread pool, which a WebGL player never runs, so a timeout raced
    ///         with <c>WhenAny</c> would never fire.
    ///     </para>
    ///     <para>
    ///         A gate waiter never calls <see cref="SemaphoreSlim.WaitAsync()" />: it takes the gate with
    ///         <c>Wait(0)</c> on a later tick, in the order the waits were requested. Code that holds the gate
    ///         releases it with <see cref="SemaphoreSlim.Release()" /> as usual.
    ///     </para>
    /// </remarks>
    internal sealed class PlayerLoopTimerQueue
    {
        private readonly Func<double> _nowSeconds;
        private readonly List<Entry> _entries = new();
        private readonly List<GateWaiter> _gateWaiters = new();

        /// <param name="nowSeconds">A monotonic clock in seconds.</param>
        internal PlayerLoopTimerQueue(Func<double> nowSeconds)
        {
            _nowSeconds = nowSeconds ?? throw new ArgumentNullException(nameof(nowSeconds));
        }

        /// <summary>Receives an exception thrown by a scheduled action. The queue keeps running.</summary>
        internal Action<Exception> ReportException { get; set; }

        /// <summary>Scheduled actions and gate waiters that have not completed yet.</summary>
        internal int PendingCount => _entries.Count + _gateWaiters.Count;

        /// <summary>Returns a task that completes on the first tick at least <paramref name="milliseconds" /> later.</summary>
        internal Task Delay(int milliseconds, CancellationToken cancellationToken)
        {
            if (milliseconds < Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (milliseconds == 0) return Task.CompletedTask;

            var completion = new TaskCompletionSource<bool>();
            Entry entry = milliseconds == Timeout.Infinite
                ? new Entry { Due = double.PositiveInfinity }
                : Add(milliseconds, null);
            entry.Fire = () => completion.TrySetResult(true);
            if (milliseconds == Timeout.Infinite) _entries.Add(entry);

            if (cancellationToken.CanBeCanceled)
            {
                entry.Registration = cancellationToken.Register(() =>
                {
                    entry.Removed = true;
                    completion.TrySetCanceled(cancellationToken);
                });
            }

            return completion.Task;
        }

        /// <summary>Runs <paramref name="action" /> on the first tick at least <paramref name="milliseconds" /> later.</summary>
        internal void Schedule(int milliseconds, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            Add(milliseconds, action);
        }

        /// <summary>Returns a task that completes once this caller holds <paramref name="gate" />.</summary>
        internal Task WaitAsync(SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (!HasWaiter(gate) && gate.Wait(0)) return Task.CompletedTask;

            var waiter = new GateWaiter
            {
                Gate = gate,
                Completion = new TaskCompletionSource<bool>()
            };
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() =>
                {
                    if (!_gateWaiters.Remove(waiter)) return;
                    waiter.Completion.TrySetCanceled(cancellationToken);
                });
            }

            _gateWaiters.Add(waiter);
            return waiter.Completion.Task;
        }

        /// <summary>
        ///     Completes every delay and scheduled action that is due, in due order, then hands free gates to
        ///     their waiters in request order. Work added during a tick waits for the next one.
        /// </summary>
        internal void Tick()
        {
            double now = _nowSeconds();

            List<Entry> due = null;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Removed)
                {
                    _entries.RemoveAt(i);
                    entry.Registration.Dispose();
                }
                else if (entry.Due <= now)
                {
                    _entries.RemoveAt(i);
                    (due ??= new List<Entry>()).Add(entry);
                }
            }

            // Hand gates over before running anything: completions and scheduled actions run caller code inline,
            // which may add or cancel waiters.
            List<GateWaiter> granted = null;
            for (int i = 0; i < _gateWaiters.Count; i++)
            {
                GateWaiter waiter = _gateWaiters[i];
                if (!waiter.Gate.Wait(0)) continue;

                _gateWaiters.RemoveAt(i--);
                waiter.Registration.Dispose();
                (granted ??= new List<GateWaiter>()).Add(waiter);
            }

            if (due != null)
            {
                due.Sort((a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Order.CompareTo(b.Order));
                foreach (Entry entry in due)
                {
                    entry.Registration.Dispose();
                    try
                    {
                        entry.Fire();
                    }
                    catch (Exception ex)
                    {
                        ReportException?.Invoke(ex);
                    }
                }
            }

            if (granted == null) return;
            foreach (GateWaiter waiter in granted)
            {
                if (!waiter.Completion.TrySetResult(true)) waiter.Gate.Release();
            }
        }

        private Entry Add(int milliseconds, Action action)
        {
            var entry = new Entry
            {
                Due = _nowSeconds() + milliseconds / 1000.0,
                Order = _nextOrder++,
                Fire = action
            };
            _entries.Add(entry);
            return entry;
        }

        private bool HasWaiter(SemaphoreSlim gate)
        {
            foreach (GateWaiter waiter in _gateWaiters)
            {
                if (ReferenceEquals(waiter.Gate, gate)) return true;
            }

            return false;
        }

        private long _nextOrder;

        private sealed class Entry
        {
            public double Due;
            public long Order;
            public Action Fire;
            public bool Removed;
            public CancellationTokenRegistration Registration;
        }

        private sealed class GateWaiter
        {
            public SemaphoreSlim Gate;
            public TaskCompletionSource<bool> Completion;
            public CancellationTokenRegistration Registration;
        }
    }
}
