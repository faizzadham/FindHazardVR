using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;

namespace Convai.Runtime.Core.Async
{
    /// <summary>
    ///     Delays, timeouts and gate waits that also work in a WebGL player. Code that can run in a player
    ///     calls these instead of <c>Task.Delay</c>, <c>CancelAfter</c>, a timeout
    ///     <see cref="CancellationTokenSource" />, <c>System.Threading.Timer</c> or
    ///     <see cref="SemaphoreSlim.WaitAsync()" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Measured in a Unity 6000.6 WebGL player over 20 seconds: <c>Task.Delay(200)</c> never completed, a
    ///         timeout <see cref="CancellationTokenSource" /> and <c>CancelAfter</c> never cancelled, a
    ///         <c>System.Threading.Timer</c> never fired, and a <see cref="SemaphoreSlim.WaitAsync()" /> waiter never
    ///         resumed after <see cref="SemaphoreSlim.Release()" /> on the main thread, because the player has no
    ///         timer or thread-pool threads. A <c>Task.Delay(Timeout.Infinite, token)</c> did complete when the token
    ///         was cancelled, and <c>WaitAsync(0)</c> never queues, so both stay framework calls.
    ///     </para>
    ///     <para>
    ///         In a WebGL player these helpers run on a <see cref="PlayerLoopTimerQueue" /> that
    ///         <c>UnityScheduler</c> ticks every frame. Everywhere else they pass straight through to the framework
    ///         call, so native behavior is unchanged. While a browser tab is hidden the player loop stops, so a wait
    ///         that elapsed meanwhile completes on the first frame after the tab is shown again.
    ///     </para>
    /// </remarks>
    internal static class ConvaiTimers
    {
        /// <summary>Whether the player-loop queue is used. Tests switch it on to run the WebGL behavior.</summary>
#if UNITY_WEBGL && !UNITY_EDITOR
        internal static bool UsePlayerLoop = true;
#else
        internal static bool UsePlayerLoop = false;
#endif

        /// <summary>The queue used while <see cref="UsePlayerLoop" /> is true. Tests replace it with a fake clock.</summary>
        internal static PlayerLoopTimerQueue Queue = CreateDefaultQueue();

        /// <inheritdoc cref="Task.Delay(int, CancellationToken)" />
        internal static Task Delay(int millisecondsDelay, CancellationToken cancellationToken = default) =>
            UsePlayerLoop && millisecondsDelay != Timeout.Infinite
                ? Queue.Delay(millisecondsDelay, cancellationToken)
                : Task.Delay(millisecondsDelay, cancellationToken);

        /// <inheritdoc cref="Task.Delay(TimeSpan, CancellationToken)" />
        internal static Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) =>
            UsePlayerLoop
                ? Delay(ToMilliseconds(delay), cancellationToken)
                : Task.Delay(delay, cancellationToken);

        /// <inheritdoc cref="CancellationTokenSource.CancelAfter(int)" />
        internal static void CancelAfter(CancellationTokenSource source, int millisecondsDelay)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!UsePlayerLoop)
            {
                source.CancelAfter(millisecondsDelay);
                return;
            }

            if (millisecondsDelay == Timeout.Infinite) return;
            Queue.Schedule(millisecondsDelay, () =>
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The owner finished before the timeout and disposed the source.
                }
            });
        }

        /// <inheritdoc cref="CancellationTokenSource.CancelAfter(TimeSpan)" />
        internal static void CancelAfter(CancellationTokenSource source, TimeSpan delay) =>
            CancelAfter(source, ToMilliseconds(delay));

        /// <summary>Creates a source that cancels itself after <paramref name="millisecondsDelay" />.</summary>
        internal static CancellationTokenSource CreateTimeoutSource(int millisecondsDelay)
        {
            if (!UsePlayerLoop) return new CancellationTokenSource(millisecondsDelay);

            var source = new CancellationTokenSource();
            CancelAfter(source, millisecondsDelay);
            return source;
        }

        /// <inheritdoc cref="CreateTimeoutSource(int)" />
        internal static CancellationTokenSource CreateTimeoutSource(TimeSpan delay) =>
            CreateTimeoutSource(ToMilliseconds(delay));

        /// <inheritdoc cref="SemaphoreSlim.WaitAsync(CancellationToken)" />
        internal static Task WaitAsync(SemaphoreSlim gate, CancellationToken cancellationToken = default) =>
            UsePlayerLoop ? Queue.WaitAsync(gate, cancellationToken) : gate.WaitAsync(cancellationToken);

        /// <summary><c>Task.WhenAny</c> that also completes in a WebGL player.</summary>
        /// <remarks>
        ///     In a WebGL player <c>Task.WhenAny</c> never completed when one of its tasks came from a
        ///     <see cref="TaskCompletionSource{T}" /> created with <see cref="TaskCreationOptions.RunContinuationsAsynchronously" />
        ///     (measured): the runtime queues the <c>WhenAny</c> completion to the thread pool. There, each task is
        ///     awaited on the captured context instead, which the player loop pumps.
        /// </remarks>
        internal static Task<Task> WhenAny(Task first, Task second)
        {
            if (!UsePlayerLoop) return Task.WhenAny(first, second);

            var winner = new TaskCompletionSource<Task>();
            ReportWhenDone(first, winner);
            ReportWhenDone(second, winner);
            return winner.Task;
        }

        /// <inheritdoc cref="WhenAny(Task, Task)" />
        internal static Task<Task<T>> WhenAny<T>(Task<T> first, Task<T> second)
        {
            if (!UsePlayerLoop) return Task.WhenAny(first, second);

            var winner = new TaskCompletionSource<Task<T>>();
            ReportWhenDone(first, winner);
            ReportWhenDone(second, winner);
            return winner.Task;
        }

        /// <summary>Called by the player loop every frame while <see cref="UsePlayerLoop" /> is true.</summary>
        internal static void Tick() => Queue.Tick();

        private static async void ReportWhenDone<TTask>(TTask task, TaskCompletionSource<TTask> winner)
            where TTask : Task
        {
            try
            {
                await task.ConfigureAwait(true);
            }
            catch
            {
                // The caller inspects the task that won, as it would after Task.WhenAny.
            }

            winner.TrySetResult(task);
        }

        private static int ToMilliseconds(TimeSpan delay)
        {
            double milliseconds = delay.TotalMilliseconds;
            if (milliseconds == Timeout.Infinite) return Timeout.Infinite;
            if (milliseconds < 0 || milliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(delay));
            return (int)Math.Ceiling(milliseconds);
        }

        private static PlayerLoopTimerQueue CreateDefaultQueue() =>
            new(() => UnityEngine.Time.realtimeSinceStartupAsDouble)
            {
                ReportException = ex => ConvaiLogger.Error($"Exception in scheduled action: {ex}", LogCategory.Events)
            };
    }
}
