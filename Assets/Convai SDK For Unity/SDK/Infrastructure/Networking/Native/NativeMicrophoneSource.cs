using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Room;
using Convai.Runtime.Networking.Media;
using LiveKit;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Infrastructure.Networking.Native
{
    internal sealed class NativeMicrophoneSource : IMicrophoneSource, IMicrophonePcmSource, IMicrophoneCaptureDrain
    {
        private const float MonitoringIntervalSeconds = 0.5f;
        private readonly bool _ownsSourceObject;
        private readonly NativeMicrophoneSourceMonitor _monitor;
        private readonly GameObject _sourceObject;
        private readonly NativeVoiceProcessingSession _voiceProcessingSession;
        private string _lastDeviceListSignature;
        private int _lastRouteSignature;
        private bool _routeBaselineReady;
        private int _stopVersion;
        private bool _sessionInvalidated;
        private bool _disposed;
        private MicrophoneSource _underlyingSource;
        private bool _enableAcousticEchoCancellation;
        private Action<float[], int, int> _pcmFrame;
        private bool _processedAudioSubscribed;

        public NativeMicrophoneSource(string deviceName, int deviceIndex = 0, GameObject hostObject = null)
        {
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
            DeviceIndex = deviceIndex;
            string displayName = string.IsNullOrWhiteSpace(DeviceName) ? "System Default" : DeviceName;

            string hostSuffix = hostObject != null ? $" @ {hostObject.name}" : string.Empty;
            _sourceObject = new GameObject($"[Convai] MicrophoneSource ({displayName}){hostSuffix}");
            _sourceObject.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(_sourceObject);
            _ownsSourceObject = true;
            _monitor = _sourceObject.AddComponent<NativeMicrophoneSourceMonitor>();
            _voiceProcessingSession = new NativeVoiceProcessingSession();
        }

        public MicrophoneSource UnderlyingSource => _underlyingSource ??= new MicrophoneSource(DeviceName, _sourceObject,
            _enableAcousticEchoCancellation);

        public string Name => DeviceName;

        public bool IsCapturing { get; private set; }

        public string DeviceName { get; }

        public int DeviceIndex { get; }

        public event Action<IMicrophoneSource, MicrophoneSessionInvalidationReason> SessionInvalidated;
        public event Action<float[], int, int> PcmFrame
        {
            add
            {
                bool subscribeToSource = _pcmFrame == null;
                _pcmFrame += value;
                if (subscribeToSource)
                    SubscribeToProcessedAudio();
            }
            remove
            {
                _pcmFrame -= value;
                if (_pcmFrame == null)
                    UnsubscribeFromProcessedAudio();
            }
        }

        public bool IsAcousticEchoCancellationActive =>
            _enableAcousticEchoCancellation &&
            _underlyingSource?.IsAcousticEchoCancellationActive == true;

        public bool IsMuted
        {
            get => _underlyingSource?.Muted ?? false;
            set => _underlyingSource?.SetMute(value);
        }

        public bool EnableAcousticEchoCancellation
        {
            get => _enableAcousticEchoCancellation;
            set
            {
                ThrowIfDisposed();
                if (_underlyingSource != null || IsCapturing)
                    return;

                _enableAcousticEchoCancellation = value;
            }
        }

        /// <summary>
        ///     Enters the platform voice-processing session ahead of <see cref="StartCapture" /> and completes once it
        ///     has settled. Android applies communication mode asynchronously, and opening the microphone while that
        ///     switch was in progress blocked the main thread for 290-627 ms (Xiaomi Redmi Note 12, Android 15), so
        ///     the publish path awaits this first; <see cref="StartCapture" /> then reuses the running session.
        ///     Completes synchronously where the session applies at once (iOS, Editor, desktop) and when echo
        ///     cancellation is off.
        /// </summary>
        /// <exception cref="OperationCanceledException">
        ///     The token was cancelled, or the capture was stopped or disposed while the session settled. The session
        ///     is released in both cases.
        /// </exception>
        internal async Task PrepareCaptureAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            if (IsCapturing || !_enableAcousticEchoCancellation)
                return;

            _voiceProcessingSession.Start();
            Task settled = _voiceProcessingSession.WhenSettled;
            if (settled.IsCompleted)
                return;

            int stopVersion = _stopVersion;
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(static state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelled))
                await Task.WhenAny(settled, cancelled.Task);

            if (ct.IsCancellationRequested)
            {
                // Nobody else releases a session that was only prepared.
                if (stopVersion == _stopVersion)
                    StopCapture();
                ct.ThrowIfCancellationRequested();
            }

            // StopCapture or Dispose ran meanwhile and already released the session.
            if (stopVersion != _stopVersion)
                throw new OperationCanceledException("Microphone capture was stopped while its voice-processing session settled.");
        }

        public void StartCapture()
        {
            ThrowIfDisposed();

            if (IsCapturing) return;

            try
            {
                if (_enableAcousticEchoCancellation)
                    _voiceProcessingSession.Start();

                MicrophoneSource source = UnderlyingSource;
                SubscribeToProcessedAudio();
                source.Start();
                IsCapturing = true;
                BeginSessionMonitoring();
            }
            catch
            {
                UnsubscribeFromProcessedAudio();
                StopSessionMonitoring();
                _voiceProcessingSession.Stop();
                throw;
            }
        }

        public async Task StopAndDrainCaptureAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            // Like StopCapture: a PrepareCaptureAsync still awaiting the session must not start capture after this.
            _stopVersion++;
            if (!IsCapturing)
            {
                // Nothing to drain; release a session started by PrepareCaptureAsync.
                _voiceProcessingSession.Stop();
                return;
            }
            // Do not mute or unsubscribe first: that would discard the final
            // partial APM frame before the source can submit it.
            Task drained = UnderlyingSource.StopAndDrainCaptureAsync();
            IsCapturing = false;
            StopSessionMonitoring();
            try
            {
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken)))
                {
                    Task completed = await Task.WhenAny(drained, canceled.Task);
                    await completed;
                }
            }
            finally
            {
                UnsubscribeFromProcessedAudio();
                _voiceProcessingSession.Stop();
                EnsureAllRecordingDevicesReleased();
            }
        }

        public void StopCapture()
        {
            // A PrepareCaptureAsync still awaiting the session sees this and does not start capture.
            _stopVersion++;
            if (!IsCapturing)
            {
                // Releases a session started by PrepareCaptureAsync; no-op when none is running.
                _voiceProcessingSession.Stop();
                return;
            }

            try
            {
                UnsubscribeFromProcessedAudio();
                _underlyingSource?.Stop();
            }
            finally
            {
                IsCapturing = false;
                StopSessionMonitoring();
                _voiceProcessingSession.Stop();

                // LiveKit's MicrophoneSource.Stop() ends capture in a coroutine, so Microphone.End may not have run yet.
                // A fast reconnect or second Play session can call Microphone.Start while the device is still recording,
                // which fails silently on some platforms until the editor process restarts.
                EnsureAllRecordingDevicesReleased();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            StopCapture();
            _underlyingSource?.Dispose();
            _voiceProcessingSession.Dispose();
            SessionInvalidated = null;
            _pcmFrame = null;

            if (_ownsSourceObject) Object.Destroy(_sourceObject);

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void HandleProcessedAudioRead(float[] samples, int channels, int sampleRate) =>
            _pcmFrame?.Invoke(samples, channels, sampleRate);

        private void SubscribeToProcessedAudio()
        {
            if (_processedAudioSubscribed || _pcmFrame == null || _underlyingSource == null)
                return;

            _underlyingSource.ProcessedAudioRead += HandleProcessedAudioRead;
            _processedAudioSubscribed = true;
        }

        private void UnsubscribeFromProcessedAudio()
        {
            if (!_processedAudioSubscribed)
                return;

            if (_underlyingSource != null)
                _underlyingSource.ProcessedAudioRead -= HandleProcessedAudioRead;
            _processedAudioSubscribed = false;
        }

        /// <summary>
        ///     Best-effort synchronous release of Unity's microphone capture. Complements LiveKit's async stop coroutine.
        ///     Safe to call from any native teardown path that uses <see cref="LiveKit.MicrophoneSource" />.
        /// </summary>
        internal static void EnsureAllRecordingDevicesReleased()
        {
            try
            {
                Microphone.End(null);
                string[] devices = Microphone.devices;
                if (devices == null || devices.Length == 0) return;

                foreach (string device in devices)
                {
                    if (string.IsNullOrEmpty(device)) continue;
                    if (Microphone.IsRecording(device)) Microphone.End(device);
                }
            }
            catch
            {
                // Ignore — teardown must never throw; LiveKit will also attempt End in its coroutine.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeMicrophoneSource));
        }

        private void BeginSessionMonitoring()
        {
            _sessionInvalidated = false;
            _lastDeviceListSignature = BuildDeviceListSignature();
            // On Android the route is read off the main thread and only once the session has settled, so a capture
            // started before that takes its baseline from a later poll.
            _lastRouteSignature = 0;
            _routeBaselineReady = ShouldMonitorRoutes &&
                                  _voiceProcessingSession.TryGetRouteSignature(out _lastRouteSignature);
            _monitor.StartPolling(PollCaptureState, MonitoringIntervalSeconds);
        }

        private void StopSessionMonitoring()
        {
            _monitor.StopPolling();
            _sessionInvalidated = false;
        }

        private bool PollCaptureState()
        {
            if (_disposed || !IsCapturing || _sessionInvalidated)
                return false;

            if (HasCaptureDeviceChanged())
            {
                RaiseSessionInvalidated(MicrophoneSessionInvalidationReason.DeviceChanged);
                return false;
            }

            if (ShouldMonitorRoutes && _voiceProcessingSession.TryGetRouteSignature(out int currentRouteSignature))
            {
                if (!_routeBaselineReady)
                {
                    _lastRouteSignature = currentRouteSignature;
                    _routeBaselineReady = true;
                }
                else if (currentRouteSignature != _lastRouteSignature)
                {
                    RaiseSessionInvalidated(MicrophoneSessionInvalidationReason.RouteChanged);
                    return false;
                }
            }

            return true;
        }

        private bool HasCaptureDeviceChanged()
        {
            string currentSignature = BuildDeviceListSignature();
            if (string.Equals(currentSignature, _lastDeviceListSignature, StringComparison.Ordinal))
                return false;

            _lastDeviceListSignature = currentSignature;

            if (string.IsNullOrWhiteSpace(DeviceName))
                return true;

            string[] devices = Microphone.devices ?? Array.Empty<string>();
            for (int i = 0; i < devices.Length; i++)
            {
                if (string.Equals(devices[i], DeviceName, StringComparison.Ordinal) ||
                    string.Equals(devices[i], DeviceName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private bool ShouldMonitorRoutes => _enableAcousticEchoCancellation && _voiceProcessingSession.SupportsRouteMonitoring;

        private static string BuildDeviceListSignature()
        {
            string[] devices = Microphone.devices ?? Array.Empty<string>();
            if (devices.Length == 0)
                return string.Empty;

            return string.Join("\u001f", devices);
        }

        private void RaiseSessionInvalidated(MicrophoneSessionInvalidationReason reason)
        {
            if (_sessionInvalidated)
                return;

            _sessionInvalidated = true;
            SessionInvalidated?.Invoke(this, reason);
        }
    }
}
