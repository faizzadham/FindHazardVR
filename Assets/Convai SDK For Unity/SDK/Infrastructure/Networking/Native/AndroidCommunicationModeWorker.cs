using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;

namespace Convai.Infrastructure.Networking.Native
{
    /// <summary>
    ///     The android.media.AudioManager calls the voice-processing session makes. Called on the worker thread of
    ///     <see cref="AndroidCommunicationModeWorker" /> only.
    /// </summary>
    internal interface IAndroidAudioManager
    {
        int GetMode();
        void SetMode(int mode);
        bool IsSpeakerphoneOn();
        void SetSpeakerphoneOn(bool on);
        bool IsBluetoothScoOn();
        bool IsBluetoothA2dpOn();
        bool IsWiredHeadsetOn();
    }

    /// <summary>
    ///     Holds Android's communication audio mode (MODE_IN_COMMUNICATION, on the loudspeaker unless a headset is
    ///     connected) for echo-cancelled microphone captures, and reads the audio route, on one worker thread.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         setMode returns at once, but the audio service applies the switch afterwards and AudioManager calls
    ///         made meanwhile wait for it: on a Xiaomi Redmi Note 12 (Android 15) the first route read after
    ///         setMode(MODE_IN_COMMUNICATION) held the calling thread for ~650 ms, and Unity's Microphone.Start
    ///         blocked for 290-627 ms when it ran during the switch. The main thread therefore only queues jobs and
    ///         reads the latest published route. <see cref="Hold.Settled" /> completes once the mode reads back as
    ///         applied, which is when the microphone can open without waiting on the audio service.
    ///     </para>
    ///     <para>
    ///         The mode is device-wide, so holds are counted: the first saves the previous mode and speakerphone
    ///         setting, the last release restores them. The restore runs a short delay after that release, so a
    ///         capture that is stopped and started again (microphone recovery, a new session) keeps the mode instead
    ///         of switching out and back in, and it waits until the restored mode reads back so the next hold never
    ///         saves this worker's own communication mode as the one to restore.
    ///     </para>
    ///     <para>
    ///         Jobs run in order on a single thread, so a hold's route reads always follow its settle and a hold
    ///         acquired after a release always follows that release. The thread attaches to the JVM before its
    ///         first job; if attaching fails, no mode was entered and every hold settles as not applied, so capture
    ///         proceeds without communication mode instead of blocking the main thread.
    ///     </para>
    /// </remarks>
    internal sealed class AndroidCommunicationModeWorker : IDisposable
    {
        public const int RouteBluetoothBit = 1 << 0;
        public const int RouteWiredBit = 1 << 1;
        public const int RouteSpeakerBit = 1 << 2;
        public const int RouteModeShift = 8;
        public const int ModeInCommunication = 3;

        private const int HeadsetRouteBits = RouteBluetoothBit | RouteWiredBit;
        private const int DefaultSettleTimeoutMs = 2000;
        private const int SpeakerphoneSettleTimeoutMs = 250;
        private const int DefaultSettlePollMs = 20;
        private const int DefaultRestoreDelayMs = 1500;

        private enum JobKind
        {
            Acquire,
            Release,
            ReadRoute
        }

        private readonly struct Job
        {
            public Job(JobKind kind, Hold hold)
            {
                Kind = kind;
                Hold = hold;
            }

            public JobKind Kind { get; }
            public Hold Hold { get; }
        }

        private readonly IAndroidAudioManager _audioManager;
        private readonly Func<bool> _attachThread;
        private readonly Action _detachThread;
        private readonly int _settleTimeoutMs;
        private readonly int _settlePollMs;
        private readonly int _restoreDelayMs;

        private readonly object _gate = new();
        private readonly Queue<Job> _jobs = new();

        // Guarded by _gate.
        private Thread _thread;
        private bool _failed;
        private bool _disposed;

        // Worker thread only.
        private int _holdCount;
        private bool _previousStateSaved;
        private int _previousMode;
        private bool _previousSpeakerphoneOn;
        private bool _speakerphoneForced;
        private bool _modeRestoreUnconfirmed; // The last restore's setMode was issued but never read back.
        private long _restoreDueTimestamp; // Stopwatch timestamp; 0 when no restore is pending.

        /// <param name="audioManager">Called on the worker thread only.</param>
        /// <param name="attachThread">Attaches the worker thread to the JVM; false fails every hold.</param>
        /// <param name="detachThread">Detaches the worker thread when it ends.</param>
        public AndroidCommunicationModeWorker(
            IAndroidAudioManager audioManager,
            Func<bool> attachThread,
            Action detachThread,
            int settleTimeoutMs = DefaultSettleTimeoutMs,
            int settlePollMs = DefaultSettlePollMs,
            int restoreDelayMs = DefaultRestoreDelayMs)
        {
            _audioManager = audioManager ?? throw new ArgumentNullException(nameof(audioManager));
            _attachThread = attachThread ?? throw new ArgumentNullException(nameof(attachThread));
            _detachThread = detachThread ?? throw new ArgumentNullException(nameof(detachThread));
            _settleTimeoutMs = settleTimeoutMs;
            _settlePollMs = settlePollMs;
            _restoreDelayMs = restoreDelayMs;
        }

        /// <summary>
        ///     Requests communication mode without blocking. <see cref="Hold.Settled" /> completes with true once the
        ///     mode reads back as applied, or with false when that timed out, the worker is unavailable, or the hold
        ///     was released first.
        /// </summary>
        public Hold Acquire()
        {
            var hold = new Hold(this);
            if (!Enqueue(JobKind.Acquire, hold))
                hold.CompleteSettled(false);
            return hold;
        }

        /// <summary>
        ///     Ends the worker thread after its current job and leaves the device mode as it is. Holds still queued,
        ///     or acquired afterwards, settle as not applied.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                Monitor.PulseAll(_gate);
            }
        }

        private bool Enqueue(JobKind kind, Hold hold)
        {
            lock (_gate)
            {
                if (_failed || _disposed)
                    return false;

                if (_thread == null)
                {
                    // Started before the job is queued so a failed start leaves no orphaned job; the new thread waits
                    // on the lock until the job is in the queue.
                    try
                    {
                        var thread = new Thread(Run)
                        {
                            IsBackground = true,
                            Name = "Convai Android Audio"
                        };
                        thread.Start();
                        _thread = thread;
                    }
                    catch (Exception exception)
                    {
                        _failed = true;
                        ConvaiLogger.Warning(
                            $"Android audio worker could not start; capturing without communication mode: {exception.Message}",
                            LogCategory.Audio);
                        return false;
                    }
                }

                _jobs.Enqueue(new Job(kind, hold));
                Monitor.Pulse(_gate);
                return true;
            }
        }

        private void Run()
        {
            bool attached;
            try
            {
                attached = _attachThread();
            }
            catch (Exception exception)
            {
                ConvaiLogger.Warning($"Android audio worker could not attach to the JVM: {exception.Message}",
                    LogCategory.Audio);
                attached = false;
            }

            if (!attached)
            {
                ConvaiLogger.Warning(
                    "Android audio worker is not attached to the JVM; capturing without communication mode.",
                    LogCategory.Audio);
                Fail();
                return;
            }

            try
            {
                while (true)
                {
                    Job job = default;
                    bool hasJob;
                    lock (_gate)
                    {
                        int waitMs;
                        while (!_disposed && _jobs.Count == 0 && (waitMs = GetRestoreWaitMs()) != 0)
                            Monitor.Wait(_gate, waitMs);

                        if (_disposed)
                            break;

                        // Queued jobs go first: an acquire queued before a pending restore is due cancels it.
                        hasJob = _jobs.Count > 0;
                        if (hasJob)
                            job = _jobs.Dequeue();
                    }

                    try
                    {
                        if (hasJob)
                            RunJob(job);
                        else
                            RestoreIfIdle();
                    }
                    catch (Exception exception)
                    {
                        ConvaiLogger.Warning(
                            $"Android audio worker job {(hasJob ? job.Kind.ToString() : "Restore")} failed: {exception.Message}",
                            LogCategory.Audio);
                    }
                }
            }
            finally
            {
                Fail();
                try
                {
                    _detachThread();
                }
                catch (Exception exception)
                {
                    ConvaiLogger.Warning($"Android audio worker could not detach from the JVM: {exception.Message}",
                        LogCategory.Audio);
                }
            }
        }

        // Stops accepting jobs and releases everyone waiting on a dropped one.
        private void Fail()
        {
            lock (_gate)
            {
                _failed = true;
                while (_jobs.Count > 0)
                {
                    Job job = _jobs.Dequeue();
                    job.Hold.CompleteSettled(false);
                    if (job.Kind == JobKind.ReadRoute)
                        job.Hold.RouteReadFinished();
                }
            }
        }

        private void RunJob(Job job)
        {
            Hold hold = job.Hold;
            switch (job.Kind)
            {
                case JobKind.Acquire:
                    RunAcquire(hold);
                    break;
                case JobKind.Release:
                    RunRelease(hold);
                    break;
                case JobKind.ReadRoute:
                    try
                    {
                        if (hold.IsAcquired && !hold.IsReleased)
                            hold.PublishRoute(ReadRouteSignature());
                    }
                    finally
                    {
                        hold.RouteReadFinished();
                    }

                    break;
            }
        }

        private void RunAcquire(Hold hold)
        {
            // Released before it ran: entering the mode only to schedule its restore would churn the audio service.
            if (hold.IsReleased)
            {
                hold.CompleteSettled(false);
                return;
            }

            hold.IsAcquired = true;
            _holdCount++;
            _restoreDueTimestamp = 0;

            bool settled = false;
            try
            {
                EnterCommunicationMode();
                settled = WaitUntil(IsCommunicationModeApplied, "Android communication mode", _settleTimeoutMs);
                // A forced speakerphone's route change may still hold Microphone.Start, but some devices never read it
                // back as on, and every hold would then wait out the whole mode timeout. So it gets a short limit.
                if (settled && _speakerphoneForced)
                    WaitUntil(_audioManager.IsSpeakerphoneOn, "Android speakerphone", SpeakerphoneSettleTimeoutMs);
                // Published before Settled completes, so a capture's route baseline is always the settled route.
                hold.PublishRoute(ReadRouteSignature());
            }
            finally
            {
                hold.CompleteSettled(settled);
            }
        }

        private void RunRelease(Hold hold)
        {
            if (!hold.IsAcquired)
                return;

            hold.IsAcquired = false;
            if (_holdCount > 0)
                _holdCount--;

            if (_holdCount == 0 && _previousStateSaved)
                _restoreDueTimestamp = Stopwatch.GetTimestamp() + _restoreDelayMs * Stopwatch.Frequency / 1000;
        }

        private void EnterCommunicationMode()
        {
            // Still saved while a restore is pending or after a failed one: keep the state from before the first hold.
            if (!_previousStateSaved)
            {
                _previousMode = _audioManager.GetMode();
                _previousSpeakerphoneOn = _audioManager.IsSpeakerphoneOn();
                _previousStateSaved = true;
            }

            int route = ReadRouteSignature();
            // After a restore that never read back, the mode read here can be stale and that restore can still land.
            if (_modeRestoreUnconfirmed || route >> RouteModeShift != ModeInCommunication)
            {
                _audioManager.SetMode(ModeInCommunication);
                _modeRestoreUnconfirmed = false;
            }

            // Re-evaluated on every hold: a headset may have come or gone, or the loudspeaker may have been dropped,
            // while the mode was kept across a restart.
            bool wantSpeaker = (route & HeadsetRouteBits) == 0;
            if (wantSpeaker && (!_speakerphoneForced || (route & RouteSpeakerBit) == 0))
            {
                // Marked before the call: restoring the previous value is harmless if the call failed.
                _speakerphoneForced = true;
                _audioManager.SetSpeakerphoneOn(true);
            }
            else if (!wantSpeaker && _speakerphoneForced)
            {
                _audioManager.SetSpeakerphoneOn(_previousSpeakerphoneOn);
                _speakerphoneForced = false;
            }
        }

        private bool IsCommunicationModeApplied() => _audioManager.GetMode() == ModeInCommunication;

        private void RestoreIfIdle()
        {
            _restoreDueTimestamp = 0;
            if (_holdCount > 0 || !_previousStateSaved)
                return;

            // Each call is tried on its own, and the saved state is forgotten only once both succeeded and the mode
            // reads back, so a failed restore is retried when the next hold is released instead of being forgotten.
            if (_speakerphoneForced)
            {
                try
                {
                    _audioManager.SetSpeakerphoneOn(_previousSpeakerphoneOn);
                    _speakerphoneForced = false;
                }
                catch (Exception exception)
                {
                    ConvaiLogger.Warning($"Android speakerphone restore failed: {exception.Message}",
                        LogCategory.Audio);
                }
            }

            int previousMode = _previousMode;
            _audioManager.SetMode(previousMode);

            // Until the restore reads back, getMode can still report communication mode: a hold acquired next would
            // save it as the mode to restore, and would skip entering the mode although the restore may still land.
            _modeRestoreUnconfirmed = !WaitUntil(() => _audioManager.GetMode() == previousMode,
                "Android audio mode restore", _settleTimeoutMs);
            if (!_modeRestoreUnconfirmed && !_speakerphoneForced)
                _previousStateSaved = false;
        }

        // Each getter returns once the audio service has applied the pending change; polling also covers devices where
        // a getter reports the old value for a while. Bounded so a refused change cannot hold the queue.
        private bool WaitUntil(Func<bool> isApplied, string what, int timeoutMs)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!isApplied())
            {
                if (elapsed.ElapsedMilliseconds >= timeoutMs)
                {
                    ConvaiLogger.Warning($"{what} did not settle within {timeoutMs} ms; continuing.",
                        LogCategory.Audio);
                    return false;
                }

                Thread.Sleep(_settlePollMs);
            }

            return true;
        }

        // Timeout.Infinite with no pending restore, 0 once it is due.
        private int GetRestoreWaitMs()
        {
            if (_restoreDueTimestamp == 0)
                return Timeout.Infinite;

            long remaining = _restoreDueTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return 0;

            return Math.Max(1, (int)(remaining * 1000 / Stopwatch.Frequency));
        }

        private int ReadRouteSignature()
        {
            int signature = 0;
            if (_audioManager.IsBluetoothScoOn() || _audioManager.IsBluetoothA2dpOn())
                signature |= RouteBluetoothBit;
            if (_audioManager.IsWiredHeadsetOn())
                signature |= RouteWiredBit;
            if (_audioManager.IsSpeakerphoneOn())
                signature |= RouteSpeakerBit;

            signature |= _audioManager.GetMode() << RouteModeShift;
            return signature;
        }

        /// <summary>One capture's claim on communication mode. Acquired and released on the main thread.</summary>
        internal sealed class Hold
        {
            private const long RoutePublishedBit = 1L << 32;

            private readonly AndroidCommunicationModeWorker _worker;
            private readonly TaskCompletionSource<bool> _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // Written by the worker, read on the main thread: RoutePublishedBit plus the signature in the low 32 bits.
            private long _routeSnapshot;
            private int _routeReadPending;
            private int _released;

            internal Hold(AndroidCommunicationModeWorker worker) => _worker = worker;

            /// <summary>See <see cref="AndroidCommunicationModeWorker.Acquire" />. Continuations run asynchronously.</summary>
            public Task<bool> Settled => _settled.Task;

            public bool IsReleased => Volatile.Read(ref _released) != 0;

            // Worker thread only: the acquire job ran and counted this hold.
            internal bool IsAcquired { get; set; }

            /// <summary>
            ///     Returns the latest route the worker read for this hold and queues a fresh read for a later call.
            ///     False until the hold's acquire has run, and after the hold was released.
            /// </summary>
            public bool TryGetRouteSignature(out int signature)
            {
                signature = 0;
                if (IsReleased)
                    return false;

                if (Interlocked.CompareExchange(ref _routeReadPending, 1, 0) == 0 &&
                    !_worker.Enqueue(JobKind.ReadRoute, this))
                    Interlocked.Exchange(ref _routeReadPending, 0);

                long snapshot = Interlocked.Read(ref _routeSnapshot);
                if ((snapshot & RoutePublishedBit) == 0)
                    return false;

                signature = unchecked((int)snapshot);
                return true;
            }

            /// <summary>Gives the hold up. Idempotent; anyone still awaiting <see cref="Settled" /> resumes now.</summary>
            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0)
                    return;

                _settled.TrySetResult(false);
                _worker.Enqueue(JobKind.Release, this);
            }

            internal void CompleteSettled(bool applied) => _settled.TrySetResult(applied);

            internal void PublishRoute(int signature) =>
                Interlocked.Exchange(ref _routeSnapshot, RoutePublishedBit | (uint)signature);

            internal void RouteReadFinished() => Interlocked.Exchange(ref _routeReadPending, 0);
        }
    }
}
