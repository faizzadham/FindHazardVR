using System.Threading;
using LiveKit.Internal;
using LiveKit.Internal.FFI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LiveKit
{
    /// <summary>
    /// Feeds rendered playback audio into the APM reverse stream.
    /// </summary>
    internal sealed class ApmReverseStream : System.IDisposable
    {
        /// <summary>
        /// Audible far-end audio the canceller must have seen before it is reported active.
        /// The adaptive filter only learns while something is playing, so a processed frame of
        /// silence proves the plumbing and nothing about convergence.
        /// </summary>
        internal const int WarmupMs = 1500;

        /// <summary>Peak below which a rendered buffer is treated as silence (≈ -40 dBFS).</summary>
        internal const float AudibleThreshold = 0.01f;

        private readonly AudioBuffer _captureBuffer = new();
        private readonly AudioProcessingModule _apm;
        private AudioProbe _probe;
        private volatile bool _isActive;
        private volatile bool _hasProcessedReverseFrame;
        // Written on the audio thread, read on the main thread.
        private int _warmMs;
        // Stopwatch timestamp of the last audible rendered buffer, 0 when none since the last
        // reset. Written on the audio thread, read on the capture thread.
        private long _lastAudibleTimestamp;

        internal bool IsActive => _isActive && _probe != null && _hasProcessedReverseFrame && IsWarm;

        /// <summary>Whether the canceller has heard <see cref="WarmupMs"/> of audible far-end audio.</summary>
        internal bool IsWarm => Volatile.Read(ref _warmMs) >= WarmupMs;

        /// <summary>Whether rendered audio was audible within the last <paramref name="ms"/> milliseconds.</summary>
        internal bool WasAudibleWithin(int ms)
        {
            long last = Interlocked.Read(ref _lastAudibleTimestamp);
            return last != 0 &&
                System.Diagnostics.Stopwatch.GetTimestamp() - last <= ms * System.Diagnostics.Stopwatch.Frequency / 1000;
        }

        internal ApmReverseStream(AudioProcessingModule apm)
        {
            _apm = apm;
        }

        /// <summary>
        /// Forgets the warm-up. Call when the capture path restarts: the echo delay the canceller
        /// learned no longer holds, so it must adapt again before being trusted.
        /// </summary>
        internal void ResetWarmup()
        {
            Interlocked.Exchange(ref _warmMs, 0);
            Interlocked.Exchange(ref _lastAudibleTimestamp, 0);
        }

        internal void Start()
        {
            // A newly started output session must prove that rendered audio is flowing through
            // the APM. Merely retaining or attaching a probe is not sufficient for safe AEC.
            _hasProcessedReverseFrame = false;
            ResetWarmup();
            if (_probe != null)
            {
                _isActive = true;
                return;
            }

            AudioListener audioListener = Object.FindAnyObjectByType<AudioListener>();
            if (audioListener == null)
            {
                Utils.Error("AudioListener not found in scene, reverse AEC stream is unavailable.");
                return;
            }

            _probe = audioListener.gameObject.GetComponent<AudioProbe>();
            if (_probe == null)
                _probe = audioListener.gameObject.AddComponent<AudioProbe>();

            _probe.AudioRead += OnAudioRead;
            _isActive = true;
        }

        internal void Stop()
        {
            _isActive = false;
            _hasProcessedReverseFrame = false;
            ResetWarmup();
            if (_probe == null)
                return;

            _probe.AudioRead -= OnAudioRead;
            _probe = null;
        }

        public void Dispose()
        {
            Stop();
            _captureBuffer.Dispose();
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            if (!_isActive)
            {
                _captureBuffer.Clear();
                return;
            }

            if (!FfiClient.IsOperational)
            {
                HandleFfiShutdownDuringAudioCallback();
                return;
            }

            float peak = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float abs = data[i] < 0f ? -data[i] : data[i];
                if (abs > peak)
                    peak = abs;
            }

            _captureBuffer.Write(data, (uint)channels, (uint)sampleRate);
            while (true)
            {
                using AudioFrame frame = _captureBuffer.ReadDuration(AudioProcessingModule.FrameDurationMs);
                if (frame == null)
                    break;

                try
                {
                    _apm.ProcessReverseStream(frame);
                    _hasProcessedReverseFrame = true;
                }
                catch (System.Exception ex) when (FfiClient.ShouldIgnoreShutdownException(ex))
                {
                    HandleFfiShutdownDuringAudioCallback();
                    return;
                }
                catch (System.Exception ex)
                {
                    Utils.Error($"Reverse audio processing failed: {ex.Message}");
                    _isActive = false;
                    _hasProcessedReverseFrame = false;
                    ResetWarmup();
                    _captureBuffer.Clear();
                    return;
                }
            }

            if (_hasProcessedReverseFrame && peak >= AudibleThreshold && channels > 0 && sampleRate > 0)
            {
                Interlocked.Add(ref _warmMs, data.Length / channels * 1000 / sampleRate);
                Interlocked.Exchange(ref _lastAudibleTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

        private void HandleFfiShutdownDuringAudioCallback()
        {
            _isActive = false;
            _hasProcessedReverseFrame = false;
            ResetWarmup();
            _captureBuffer.Clear();
        }
    }
}
