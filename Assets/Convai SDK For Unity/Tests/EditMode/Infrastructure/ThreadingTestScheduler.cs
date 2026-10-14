using System;
using System.Collections.Concurrent;
using System.Threading;
using Convai.Domain.EventSystem;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    ///     Test scheduler that simulates multi-threaded behavior for EventHub testing.
    /// </summary>
    /// <remarks>
    ///     This scheduler provides controlled execution of scheduled actions for testing:
    ///     - MainThread actions are queued and can be processed manually
    ///     - Background actions are executed on ThreadPool
    ///     - Provides synchronization primitives for test coordination
    ///     Usage:
    ///     <code>
    /// var scheduler = new ThreadingTestScheduler();
    /// var eventHub = new EventHub(scheduler);
    /// 
    /// 
    /// eventHub.Subscribe&lt;MyEvent&gt;(handler, EventDeliveryPolicy.MainThread);
    /// 
    /// 
    /// Task.Run(() => eventHub.Publish(new MyEvent()));
    /// 
    /// 
    /// scheduler.ProcessMainThreadQueue();
    /// </code>
    /// </remarks>
    public sealed class ThreadingTestScheduler : IUnityScheduler
    {
        private readonly ManualResetEventSlim _backgroundComplete = new(false);
        private readonly int _mainThreadId;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private int _backgroundActionsCompleted;

        /// <summary>
        ///     Creates a new ThreadingTestScheduler.
        ///     The current thread is considered the "main thread".
        /// </summary>
        public ThreadingTestScheduler()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        ///     Gets the number of actions waiting in the main thread queue.
        /// </summary>
        public int MainThreadQueueCount => _mainThreadQueue.Count;

        /// <summary>
        ///     Gets the number of background actions that have completed.
        /// </summary>
        public int BackgroundActionsCompleted => _backgroundActionsCompleted;

        /// <summary>
        ///     Schedules an action to run on the main thread.
        ///     Actions are queued and must be processed by calling ProcessMainThreadQueue().
        /// </summary>
        public void ScheduleOnMainThread(Action action)
        {
            if (action == null) return;
            _mainThreadQueue.Enqueue(action);
        }

        /// <summary>
        ///     Schedules an action to run on a background thread.
        ///     Actions are executed immediately on the ThreadPool.
        /// </summary>
        public void ScheduleOnBackground(Action action)
        {
            if (action == null) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    action();
                }
                finally
                {
                    Interlocked.Increment(ref _backgroundActionsCompleted);
                    _backgroundComplete.Set();
                }
            });
        }

        /// <summary>
        ///     Returns true if called from the main thread.
        /// </summary>
        public bool IsMainThread() => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        ///     Processes all actions currently in the main thread queue.
        /// </summary>
        /// <returns>Number of actions processed</returns>
        public int ProcessMainThreadQueue()
        {
            int processed = 0;
            while (_mainThreadQueue.TryDequeue(out Action action))
            {
                action?.Invoke();
                processed++;
            }

            return processed;
        }

        /// <summary>
        ///     Waits for at least one background action to complete.
        /// </summary>
        /// <param name="timeoutMs">Maximum time to wait in milliseconds</param>
        /// <returns>True if a background action completed; false if timeout</returns>
        public bool WaitForBackgroundAction(int timeoutMs = 5000) => _backgroundComplete.Wait(timeoutMs);

        /// <summary>
        ///     Resets the scheduler state for a new test.
        /// </summary>
        public void Reset()
        {
            while (_mainThreadQueue.TryDequeue(out _)) { }

            _backgroundActionsCompleted = 0;
            _backgroundComplete.Reset();
        }
    }
}
