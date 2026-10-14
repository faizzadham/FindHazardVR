using System;
using Convai.Domain.Logging;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Runtime.Networking.Media
{
    internal enum ClientVoiceActivityStage
    {
        Candidate = 0,
        Confirmed = 1,
        Cancelled = 2,
        Ended = 3
    }

    internal readonly struct ClientVoiceActivityStateChanged
    {
        internal ClientVoiceActivityStateChanged(
            ClientVoiceActivityStage stage,
            float probability,
            bool isAcousticEchoCancellationActive = false)
        {
            Stage = stage;
            Probability = probability;
            IsAcousticEchoCancellationActive = isAcousticEchoCancellationActive;
        }

        internal ClientVoiceActivityStage Stage { get; }
        internal float Probability { get; }
        internal bool IsAcousticEchoCancellationActive { get; }
    }

    /// <summary>
    ///     Exposes microphone PCM without coupling the runtime assembly to a transport implementation.
    ///     Implementations invoke the callback on the audio thread.
    /// </summary>
    internal interface IMicrophonePcmSource
    {
        event Action<float[], int, int> PcmFrame;

        bool IsAcousticEchoCancellationActive { get; }
    }

    internal interface IClientVoiceActivityDetector : IDisposable
    {
    }

    internal interface IClientVoiceActivityDetectorFactory
    {
        IClientVoiceActivityDetector Create(
            IMicrophonePcmSource source,
            GameObject host,
            Func<bool> shouldProcess,
            Action<ClientVoiceActivityStateChanged> stateChanged,
            ILogger logger);
    }

    /// <summary>
    ///     Optional inference modules register here when their package dependency is available.
    ///     The core runtime remains usable without a local VAD implementation.
    /// </summary>
    internal static class ClientVoiceActivityDetectorFactoryRegistry
    {
        internal static IClientVoiceActivityDetectorFactory Factory { get; set; }
    }

    /// <summary>
    ///     Delays local speech detection past the first moments of each character turn. Echo
    ///     cancellation re-adapts at every far-end onset, so the residual it lets through is
    ///     loudest exactly when the character starts talking. Main thread only.
    /// </summary>
    internal sealed class CharacterAudibleHoldoff
    {
        internal const double HoldoffSeconds = 0.4;

        private double _audibleSince = -1;

        internal bool ShouldProcess(bool characterAudible, double now)
        {
            if (!characterAudible)
            {
                _audibleSince = -1;
                return false;
            }

            if (_audibleSince < 0)
                _audibleSince = now;

            return now - _audibleSince >= HoldoffSeconds;
        }
    }

    /// <summary>
    ///     Converts per-window speech probabilities into a quick candidate signal followed by
    ///     a more conservative confirmed signal. The early candidate can duck playback while
    ///     false positives recover without sending a transport interruption.
    /// </summary>
    internal sealed class ClientVoiceActivityDebouncer
    {
        internal const float ActivationThreshold = 0.65f;
        internal const float ReleaseThreshold = 0.35f;

        /// <summary>
        ///     Six 32 ms windows (~192 ms). Two was 64 ms, and residual echo of the character's own
        ///     voice at an utterance onset is speech-shaped for longer than that, so it committed
        ///     interruptions against the character itself over speakers.
        /// </summary>
        internal const int ConfirmationWindows = 6;
        internal const int CandidateReleaseWindows = 2;
        internal const int ConfirmedReleaseWindows = 5;

        private readonly Action<ClientVoiceActivityStateChanged> _stateChanged;
        private State _state;
        private int _positiveWindows;
        private int _releaseWindows;

        internal ClientVoiceActivityDebouncer(Action<ClientVoiceActivityStateChanged> stateChanged)
        {
            _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        }

        internal void Observe(float probability)
        {
            probability = Mathf.Clamp01(probability);

            switch (_state)
            {
                case State.Idle:
                    if (probability < ActivationThreshold)
                        return;

                    _state = State.Candidate;
                    _positiveWindows = 1;
                    _releaseWindows = 0;
                    Publish(ClientVoiceActivityStage.Candidate, probability);
                    return;

                case State.Candidate:
                    if (probability >= ActivationThreshold)
                    {
                        _positiveWindows++;
                        _releaseWindows = 0;
                        if (_positiveWindows >= ConfirmationWindows)
                        {
                            _state = State.Confirmed;
                            Publish(ClientVoiceActivityStage.Confirmed, probability);
                        }

                        return;
                    }

                    if (probability <= ReleaseThreshold)
                    {
                        _releaseWindows++;
                        if (_releaseWindows >= CandidateReleaseWindows)
                        {
                            Publish(ClientVoiceActivityStage.Cancelled, probability);
                            Reset();
                        }
                    }
                    else
                    {
                        _releaseWindows = 0;
                    }

                    return;

                case State.Confirmed:
                    if (probability <= ReleaseThreshold)
                    {
                        _releaseWindows++;
                        if (_releaseWindows >= ConfirmedReleaseWindows)
                        {
                            Publish(ClientVoiceActivityStage.Ended, probability);
                            Reset();
                        }
                    }
                    else
                    {
                        _releaseWindows = 0;
                    }

                    return;
                default:
                    return;
            }
        }

        internal void Reset()
        {
            _state = State.Idle;
            _positiveWindows = 0;
            _releaseWindows = 0;
        }

        internal void Stop()
        {
            if (_state == State.Candidate)
                Publish(ClientVoiceActivityStage.Cancelled, 0f);
            else if (_state == State.Confirmed)
                Publish(ClientVoiceActivityStage.Ended, 0f);

            Reset();
        }

        private void Publish(ClientVoiceActivityStage stage, float probability) =>
            _stateChanged(new ClientVoiceActivityStateChanged(stage, probability));

        private enum State
        {
            Idle = 0,
            Candidate = 1,
            Confirmed = 2
        }
    }
}
