using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using LiveKit.Internal;

using LiveKit.Internal.Threading;
namespace LiveKit
{
    /// <summary>
    /// An audio source which captures from the device's microphone.
    /// </summary>
    /// <remarks>
    /// Ensure microphone permissions are granted before calling <see cref="Start"/>.
    /// </remarks>
    public sealed class MicrophoneSource : RtcAudioSource
    {
        private const float DefaultCaptureStartTimeoutSeconds = 2f;

        /// <remarks>
        ///     Voice processing re-routes the audio input, and on Android the first samples
        ///     regularly arrive more than two seconds after <see cref="Microphone.Start" /> on
        ///     hardware where capture then works perfectly well. A source that asked for echo
        ///     cancellation therefore waits longer before it says anything.
        /// </remarks>
        private const float VoiceProcessingCaptureStartTimeoutSeconds = 6f;

        private const float AndroidAecStartupHealthCheckDelaySeconds = 0.5f;
        private const float AndroidSilentSignalThreshold = 0.000001f;
        private const float CaptureLagLogIntervalSeconds = 1f;

        private readonly bool _enableAcousticEchoCancellation;
        private readonly GameObject _sourceObject;
        private readonly string _deviceName;
        private string _activeDeviceName;

        public override event Action<float[], int, int> AudioRead;

        private bool _disposed = false;
        private bool _started = false;
        private int _captureAttemptId;
        private int _audioReadCountThisAttempt;
        private volatile bool _isMonitoringAndroidAecStartup;
        private bool _performedAndroidAecStartupRecovery;
        private volatile float _latestAudioPeak;
        private float[] _monoBuffer;

        /// <summary>
        /// Creates a new microphone source for the given device.
        /// </summary>
        /// <param name="deviceName">The name of the device to capture from. Use <see cref="Microphone.devices"/> to
        /// get the list of available devices.</param>
        /// <param name="sourceObject">The GameObject to attach the AudioSource to. The object must be kept in the scene
        /// for the duration of the source's lifetime.</param>
        /// <param name="enableAcousticEchoCancellation">Whether to enable explicit APM-based acoustic echo cancellation.</param>
        public MicrophoneSource(
            string deviceName,
            GameObject sourceObject,
            bool enableAcousticEchoCancellation = false)
            : base(
                RtcAudioSourceType.AudioSourceMicrophone,
                (uint)ResolveUnityCaptureSampleRate(),
                1,
                enableAcousticEchoCancellation)
        {
            _enableAcousticEchoCancellation = enableAcousticEchoCancellation;
            _deviceName = deviceName;
            _sourceObject = sourceObject;
        }

        /// <summary>
        /// Begins capturing audio from the microphone.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the microphone is not available or unauthorized.
        /// </exception>
        /// <remarks>
        /// Ensure microphone permissions are granted before calling this method
        /// by calling <see cref="Application.RequestUserAuthorization"/>.
        /// </remarks>
        public override void Start()
        {
            base.Start();
            if (_started) return;


            if (!Application.HasUserAuthorization(mode: UserAuthorization.Microphone))
                throw new InvalidOperationException("Microphone access not authorized");

            _isMonitoringAndroidAecStartup = false;
            _performedAndroidAecStartupRecovery = false;
            MonoBehaviourContext.OnApplicationPauseEvent += OnApplicationPause;
            _started = true;
            MonoBehaviourContext.RunCoroutine(StartMicrophone(++_captureAttemptId));
        }

        private IEnumerator StartMicrophone(int attemptId)
        {
            if (!IsCaptureAttemptCurrent(attemptId)) yield break;
            // Every capture (re)start changes the delay between rendered audio and its echo,
            // whether it is the first start, a resume from background, or the Android
            // silent-start recovery. None of those recreate the RtcAudioSource, so say so here.
            ResetEchoCancellationWarmup();
            _audioReadCountThisAttempt = 0;
            _latestAudioPeak = 0f;

            // Validate that the GameObject is still valid before starting
            if (_sourceObject == null)
            {
                Utils.Error("MicrophoneSource: GameObject is null, cannot start microphone");
                yield break;
            }

            // Verify microphone is still authorized (could change during background)
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                Utils.Error("MicrophoneSource: Microphone authorization lost");
                yield break;
            }

            string resolvedDeviceName = ResolveCaptureDeviceName(_deviceName);
            int sampleRate = ResolveUnityCaptureSampleRate();
            AudioClip clip = null;
            try
            {
                clip = Microphone.Start(
                    resolvedDeviceName,
                    loop: true,
                    lengthSec: 1,
                    frequency: sampleRate
                );
            }
            catch (ArgumentException) when (!string.IsNullOrWhiteSpace(resolvedDeviceName))
            {
                resolvedDeviceName = null;
                clip = Microphone.Start(
                    resolvedDeviceName,
                    loop: true,
                    lengthSec: 1,
                    frequency: sampleRate
                );
            }
            catch (Exception e)
            {
                Utils.Error($"MicrophoneSource: Exception starting microphone: {e.Message}");
                yield break;
            }

            if (clip == null)
            {
                Utils.Error("MicrophoneSource: Microphone.Start returned null, audio session may not be ready");
                yield break;
            }

            _activeDeviceName = resolvedDeviceName;

            // Ensure no duplicate components exist before adding new ones.
            // This is important during app resume on iOS where components might not be
            // fully destroyed yet due to Unity's deferred Destroy().
            var existingSource = _sourceObject.GetComponent<AudioSource>();
            if (existingSource != null)
                UnityEngine.Object.DestroyImmediate(existingSource);

            var existingProbe = _sourceObject.GetComponent<AudioProbe>();
            if (existingProbe != null)
            {
                existingProbe.AudioRead -= OnAudioRead;
                UnityEngine.Object.DestroyImmediate(existingProbe);
            }

            var source = _sourceObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;

            var probe = _sourceObject.AddComponent<AudioProbe>();
            // Clear the audio data after it is read as to not play it through the speaker locally.
            probe.ClearAfterInvocation();
            probe.AudioRead += OnAudioRead;

            // Hardware readiness is independent of game time. Stop/restart invalidates this waiter.
            float timeout = ResolveCaptureStartTimeoutSeconds();
            float deadline = Time.realtimeSinceStartup + timeout;
            while (IsCaptureAttemptCurrent(attemptId) &&
                   Microphone.GetPosition(_activeDeviceName) <= 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.05f);
            }

            if (!IsCaptureAttemptCurrent(attemptId) || source == null) yield break;

            bool startedLate = Microphone.GetPosition(_activeDeviceName) <= 0;
            if (startedLate)
            {
                Utils.Warning(
                    $"MicrophoneSource: microphone had not produced data after {timeout}s. Starting " +
                    "capture anyway; Android AEC startup recovery remains available.");
            }

            bool shouldMonitorAndroidAecStartup = CanRecoverFromSilentStart(attemptId);
            _isMonitoringAndroidAecStartup = shouldMonitorAndroidAecStartup;
            source.Play();
            if (shouldMonitorAndroidAecStartup)
                MonoBehaviourContext.RunCoroutine(MonitorAndroidAecStartupRecovery(attemptId));
            if (_enableAcousticEchoCancellation)
                MonoBehaviourContext.RunCoroutine(LogCaptureLag(attemptId, source, clip));

            Utils.Debug($"MicrophoneSource device='{_activeDeviceName ?? "default"}' started successfully");
        }

        // The AudioSource reads the looping microphone clip behind the device's write head. That
        // lag is part of the echo delay the canceller has to learn; if the two clocks drift it
        // creeps, and when it wraps the delay jumps by the whole clip. Logged so devices can be
        // measured before deciding whether a correction is worth its own discontinuity.
        private IEnumerator LogCaptureLag(int attemptId, AudioSource source, AudioClip clip)
        {
            int previousLag = -1;
            int wraps = 0;
            while (true)
            {
                yield return new WaitForSecondsRealtime(CaptureLagLogIntervalSeconds);
                if (!_started || attemptId != _captureAttemptId || source == null || clip == null)
                    yield break;

                int lag = (Microphone.GetPosition(_activeDeviceName) - source.timeSamples + clip.samples) % clip.samples;
                if (previousLag >= 0 && Mathf.Abs(lag - previousLag) > clip.samples / 2)
                    wraps++;
                previousLag = lag;
                Utils.Debug($"MicrophoneSource capture lag={lag * 1000L / clip.frequency}ms ({lag} samples) wraps={wraps}");
            }
        }

        /// <summary>
        /// Stops capturing audio from the microphone.
        /// </summary>
        public override void Stop()
        {
            _started = false;
            ++_captureAttemptId;
            base.Stop();
            _isMonitoringAndroidAecStartup = false;
            MonoBehaviourContext.RunCoroutine(StopMicrophone());
            MonoBehaviourContext.OnApplicationPauseEvent -= OnApplicationPause;
        }

        private IEnumerator StopMicrophone()
        {
            if (string.IsNullOrWhiteSpace(_activeDeviceName))
            {
                Microphone.End(null);
            }
            else if (Microphone.IsRecording(_activeDeviceName))
            {
                Microphone.End(_activeDeviceName);
            }

            _activeDeviceName = null;

            // Check if GameObject is still valid before trying to access components
            if (_sourceObject != null)
            {
                var probe = _sourceObject.GetComponent<AudioProbe>();
                if (probe != null)
                {
                    probe.AudioRead -= OnAudioRead;
                    UnityEngine.Object.Destroy(probe);
                }

                var source = _sourceObject.GetComponent<AudioSource>();
                if (source != null)
                    UnityEngine.Object.Destroy(source);
            }

            Utils.Debug($"MicrophoneSource device='{_deviceName ?? "default"}' stopped");
            yield return null;
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            float[] captureData = data;
            int captureChannels = channels;
            if (channels > 1 && data != null && data.Length % channels == 0)
            {
                int frameCount = data.Length / channels;
                if (_monoBuffer == null || _monoBuffer.Length != frameCount)
                    _monoBuffer = new float[frameCount];

                float channelScale = 1f / channels;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float mixedSample = 0f;
                    int frameStart = frame * channels;
                    for (int channel = 0; channel < channels; channel++)
                        mixedSample += data[frameStart + channel];

                    _monoBuffer[frame] = mixedSample * channelScale;
                }

                captureData = _monoBuffer;
                captureChannels = 1;
            }

            if (_isMonitoringAndroidAecStartup)
            {
                float peak = 0f;
                for (int i = 0; i < captureData.Length; i++)
                {
                    float abs = Mathf.Abs(captureData[i]);
                    if (abs > peak)
                        peak = abs;
                }

                _latestAudioPeak = peak;
                Interlocked.Increment(ref _audioReadCountThisAttempt);
            }

            AudioRead?.Invoke(captureData, captureChannels, sampleRate);
        }

        private void OnApplicationPause(bool pause)
        {
            if (!_started)
                return;

            if (pause)
            {
                // On iOS, when app goes to background, we should stop using audio resources
                // to avoid AVAudioSession interruption errors (FigCaptureSourceRemote -17281)
                ++_captureAttemptId;
                _isMonitoringAndroidAecStartup = false;
                MonoBehaviourContext.RunCoroutine(StopMicrophone());
            }
            else
            {
                // When resuming, restart the microphone
                MonoBehaviourContext.RunCoroutine(RestartMicrophone(++_captureAttemptId));
            }
        }

        private IEnumerator RestartMicrophone(int attemptId)
        {
            if (!IsCaptureAttemptCurrent(attemptId)) yield break;
            yield return StopMicrophone();
            if (!IsCaptureAttemptCurrent(attemptId)) yield break;

            // Wait for iOS audio session to be ready before attempting to restart.
            // On iOS, after app resumes from background, the audio session needs time to
            // recover from interruption. Poll for readiness instead of using arbitrary delay.
            yield return WaitForMicrophoneReady(attemptId);
            if (!IsCaptureAttemptCurrent(attemptId)) yield break;

            yield return StartMicrophone(attemptId);
        }

        private IEnumerator WaitForMicrophoneReady(int attemptId)
        {
            // Wait for microphone devices to become available again after iOS audio session interruption.
            // This is more reliable than a fixed delay because we wait for actual system readiness.
            const float timeout = 2f;
            float deadline = Time.realtimeSinceStartup + timeout;

            // On iOS, Microphone.devices may be empty immediately after resume while
            // AVAudioSession is recovering from interruption. Wait until devices are available.
            while (IsCaptureAttemptCurrent(attemptId) && Microphone.devices.Length == 0 &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(0.05f);
            }

            if (!IsCaptureAttemptCurrent(attemptId)) yield break;
            if (Microphone.devices.Length == 0)
            {
                Utils.Error($"MicrophoneSource: Microphone devices not available after {timeout}s timeout");
                yield break;
            }

            // Extra frame to ensure audio session is fully ready
            yield return null;
        }

        // Some Android devices start the first AEC-backed capture session with silent PCM until the mic is restarted.
        private IEnumerator MonitorAndroidAecStartupRecovery(int attemptId)
        {
            yield return new WaitForSecondsRealtime(AndroidAecStartupHealthCheckDelaySeconds);

            if (!CanRecoverFromSilentStart(attemptId))
            {
                // A stale monitor must not disable monitoring for a replacement attempt.
                if (attemptId == _captureAttemptId) _isMonitoringAndroidAecStartup = false;
                yield break;
            }

            bool shouldRestart =
                Volatile.Read(ref _audioReadCountThisAttempt) == 0 ||
                _latestAudioPeak <= AndroidSilentSignalThreshold;
            _isMonitoringAndroidAecStartup = false;
            if (!shouldRestart)
                yield break;

            _performedAndroidAecStartupRecovery = true;
            Debug.LogWarning(
                "[LiveKit] Android microphone started silently with AEC enabled. Restarting capture once.");
            yield return RestartMicrophone(++_captureAttemptId);
        }

        private static string ResolveCaptureDeviceName(string requestedDeviceName)
        {
            if (string.IsNullOrWhiteSpace(requestedDeviceName))
                return null;

            string[] devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
                return null;

            for (int i = 0; i < devices.Length; i++)
                if (string.Equals(devices[i], requestedDeviceName, StringComparison.Ordinal))
                    return requestedDeviceName;

            for (int i = 0; i < devices.Length; i++)
                if (string.Equals(devices[i], requestedDeviceName, StringComparison.OrdinalIgnoreCase))
                    return devices[i];

            return null;
        }

        private bool IsCaptureAttemptCurrent(int attemptId) =>
            _started && !_disposed && attemptId == _captureAttemptId;

        // At most one recovery per Start/Stop lifecycle, including resumed/route-changed attempts.
        private bool CanRecoverFromSilentStart(int attemptId) =>
            IsCaptureAttemptCurrent(attemptId) &&
            !_performedAndroidAecStartupRecovery &&
            _enableAcousticEchoCancellation &&
            Application.platform == RuntimePlatform.Android;

        private float ResolveCaptureStartTimeoutSeconds() =>
            _enableAcousticEchoCancellation && Application.platform == RuntimePlatform.Android
                ? VoiceProcessingCaptureStartTimeoutSeconds
                : DefaultCaptureStartTimeoutSeconds;

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing) Stop();
            _disposed = true;
            base.Dispose(disposing);
        }

        ~MicrophoneSource()
        {
            Dispose(false);
        }
    }
}
