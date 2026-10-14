using System;
using UnityEngine;

namespace Convai.Runtime.Networking.Media
{
    /// <summary>
    ///     Decides, from the microphone's own loudness, whether somebody has just started talking.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why not the neural detector.</b> The SDK already ships a Silero VAD, but it runs
    ///         only while the character is speaking, because its job is barge-in — which is exactly
    ///         the window where this question does not need asking. It is also optional: it needs a
    ///         package most projects do not install. Noticing the player is not worth a neural
    ///         network or a dependency, so this is arithmetic on the samples that are already there.
    ///     </para>
    ///     <para>
    ///         <b>Why it learns the noise floor.</b> A fixed loudness threshold is a guess about the
    ///         player's microphone gain, and it is wrong on most of them — too deaf on a quiet
    ///         headset, permanently triggered on a hot desk mic. This tracks the quietest the room
    ///         has recently been and fires on sound that stands out from it, so a noisy room raises
    ///         the bar instead of jamming the gate open. The floor follows quiet quickly and loud
    ///         slowly, so settling into a new room is fast and a long sentence cannot drag the floor
    ///         up underneath itself.
    ///     </para>
    ///     <para>
    ///         <b>It is allowed to be wrong.</b> A false positive costs a character glancing up at a
    ///         door slamming, which is what a person does. Nothing here can commit a turn or send
    ///         anything, so the gate is tuned to notice rather than to be certain — the opposite of
    ///         how the service's own detector is tuned.
    ///     </para>
    ///     <para>
    ///         Pure POCO with no Unity object dependencies, so it is edit-mode testable.
    ///         <see cref="Observe" /> is called from the audio thread and allocates nothing;
    ///         the caller marshals <see cref="StateChanged" /> to the main thread.
    ///     </para>
    /// </remarks>
    internal sealed class MicrophoneLevelGate
    {
        /// <summary>How far above the settled noise floor a sound has to be to count as speech.</summary>
        internal const float ActivationRatio = 3.5f;

        /// <summary>Hysteresis: it stays active until it falls back to nearly the floor.</summary>
        internal const float ReleaseRatio = 1.8f;

        /// <summary>
        ///     A floor of its own, so a perfectly silent input (a muted or disconnected device
        ///     reading zeroes) cannot make any faint sound look enormous relative to nothing.
        /// </summary>
        internal const float SilenceFloor = 0.0008f;

        /// <summary>Consecutive loud windows before it says yes. Two is ~40 ms — a syllable.</summary>
        internal const int AttackWindows = 2;

        /// <summary>
        ///     Consecutive quiet windows before it says no. Long enough to ride out the pauses
        ///     inside a sentence, so the character does not flicker between beats mid-utterance.
        /// </summary>
        internal const int ReleaseWindows = 25;

        /// <summary>
        ///     Length of one analysis window. <see cref="AttackWindows" /> and
        ///     <see cref="ReleaseWindows" /> are counted in these, so this is what makes them
        ///     durations rather than sample counts.
        /// </summary>
        /// <remarks>
        ///     The window was a fixed 960 samples, which is 20 ms on a desktop capturing at 48 kHz
        ///     and 60 ms on a phone capturing at 16 kHz — so the same authored release hold was
        ///     half a second on one platform and a second and a half on another. The rate is
        ///     reported with every frame; the window is measured from it.
        /// </remarks>
        internal const float WindowSeconds = 0.02f;

        /// <summary>Rate assumed when a source reports none.</summary>
        internal const int DefaultSampleRate = 48000;

        /// <summary>Samples per analysis window at <see cref="DefaultSampleRate" />.</summary>
        internal const int WindowSamples = 960;

        // The floor drops toward a quiet window fast and rises toward a loud one slowly.
        private const float FloorFallRate = 0.25f;
        private const float FloorRiseRate = 0.0015f;

        private readonly Action<bool, float> _stateChanged;

        // Written by the main thread, read by the audio thread. The audio thread still owns every
        // other field here, so there is exactly one writer per field and no lock on the audio path.
        private volatile bool _suppressed;

        private double _sumOfSquares;
        private int _samplesInWindow;
        private int _windowSamples = WindowSamples;
        private float _noiseFloor = -1f;
        private int _loudWindows;
        private int _quietWindows;
        private bool _isActive;

        /// <param name="stateChanged">
        ///     Raised only on a change, with the loudness as a ratio above the noise floor (zero on
        ///     the falling edge). Called on whichever thread fed <see cref="Observe" />.
        /// </param>
        internal MicrophoneLevelGate(Action<bool, float> stateChanged)
        {
            _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        }

        /// <summary>Whether the gate currently believes somebody is talking.</summary>
        internal bool IsActive => _isActive;

        /// <summary>
        ///     Stops the gate listening while the character's own voice is coming out of the
        ///     player's speakers. Called from the main thread.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         An open microphone hears the character, and a character that mistakes its own
        ///         answer for the player turns to them while it is the one talking. Whether anybody
        ///         is audible is a room-wide question the SDK already answers
        ///         (<c>AudioTrackManager.HasActiveCharacterAudioPlayback</c>) — asked on the main
        ///         thread, because that answer walks a collection the main thread also mutates as
        ///         characters join and leave.
        ///     </para>
        ///     <para>
        ///         Setting it does not touch the gate's measurements: the audio thread notices the
        ///         flag and clears its own state, so the two threads never write the same field.
        ///     </para>
        /// </remarks>
        internal void SetSuppressed(bool suppressed) => _suppressed = suppressed;

        /// <summary>
        ///     Feeds one buffer of interleaved microphone samples. Safe to call with any buffer
        ///     size; windows are accumulated across calls.
        /// </summary>
        internal void Observe(float[] samples, int channels, int sampleRate = DefaultSampleRate)
        {
            if (samples == null || samples.Length == 0) return;
            if (channels < 1) channels = 1;

            if (_suppressed)
            {
                Suppress();
                return;
            }

            int window = WindowSamplesFor(sampleRate);
            if (window != _windowSamples)
            {
                // A different rate is a different device, and a part-filled window measured at the
                // old one would be averaged over the wrong span. Drop it rather than carry it.
                _windowSamples = window;
                _sumOfSquares = 0d;
                _samplesInWindow = 0;
            }

            for (int i = 0; i < samples.Length; i += channels)
            {
                float sample = samples[i];
                _sumOfSquares += sample * sample;
                _samplesInWindow++;

                if (_samplesInWindow < _windowSamples) continue;

                ObserveWindow((float)Math.Sqrt(_sumOfSquares / _samplesInWindow));
                _sumOfSquares = 0d;
                _samplesInWindow = 0;
            }
        }

        /// <summary>
        ///     Drops the gate and forgets what it learned. Used when the microphone closes, mutes,
        ///     or is replaced — the next device's quiet is not this one's.
        /// </summary>
        internal void Reset()
        {
            bool wasActive = _isActive;
            ClearMeasurements();

            if (wasActive) _stateChanged(false, 0f);
        }

        /// <summary>Samples in one analysis window at the rate this source is reporting.</summary>
        private static int WindowSamplesFor(int sampleRate) =>
            sampleRate > 0
                ? Mathf.Max(1, Mathf.RoundToInt(sampleRate * WindowSeconds))
                : WindowSamples;

        /// <summary>
        ///     Drops the gate because the character's own voice is in the microphone, and says so.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         It used to close silently, on the reasoning that the closing word belonged to
        ///         whoever suppressed the gate. Nothing ever said it: <c>SetSuppressed</c> only
        ///         raises a flag and its caller publishes nothing, so a gate that was open when the
        ///         character began answering left every listener holding "the player is talking"
        ///         until the player's <i>next</i> utterance ended. There is no second speaker to
        ///         defer to here, so the gate owns the word.
        ///     </para>
        ///     <para>
        ///         Teardown is the case that reasoning was really about, and it still holds there:
        ///         <see cref="Reset" /> is followed by the adapter's own closing word.
        ///     </para>
        /// </remarks>
        private void Suppress()
        {
            bool wasActive = _isActive;
            ClearMeasurements();

            if (wasActive) _stateChanged(false, 0f);
        }

        /// <summary>Forgets everything measured so far, without announcing anything.</summary>
        private void ClearMeasurements()
        {
            _sumOfSquares = 0d;
            _samplesInWindow = 0;
            _noiseFloor = -1f;
            _loudWindows = 0;
            _quietWindows = 0;
            _isActive = false;
        }

        /// <summary>One window's loudness. Internal for the tests, which drive windows directly.</summary>
        internal void ObserveWindow(float rms)
        {
            if (float.IsNaN(rms) || rms < 0f) rms = 0f;

            if (_noiseFloor < 0f)
            {
                // First window is the only estimate available, so start there rather than at zero:
                // starting at zero makes the very first breath look infinitely loud.
                _noiseFloor = Mathf.Max(rms, SilenceFloor);
                return;
            }

            float floor = Mathf.Max(_noiseFloor, SilenceFloor);
            float ratio = rms / floor;

            if (!_isActive)
            {
                if (ratio >= ActivationRatio)
                {
                    _loudWindows++;
                    _quietWindows = 0;
                    if (_loudWindows >= AttackWindows)
                    {
                        _isActive = true;
                        _stateChanged(true, ratio);
                    }
                }
                else
                {
                    _loudWindows = 0;
                }
            }
            else
            {
                if (ratio <= ReleaseRatio)
                {
                    _quietWindows++;
                    if (_quietWindows >= ReleaseWindows)
                    {
                        _isActive = false;
                        _loudWindows = 0;
                        _quietWindows = 0;
                        _stateChanged(false, 0f);
                    }
                }
                else
                {
                    _quietWindows = 0;
                }
            }

            // The floor only learns from windows the gate is not calling speech. Letting it learn
            // from the player's own voice is what makes a long sentence quietly raise the bar until
            // the speaker falls under it and the character stops attending mid-word.
            if (_isActive) return;

            float rate = rms < _noiseFloor ? FloorFallRate : FloorRiseRate;
            _noiseFloor += (rms - _noiseFloor) * rate;
            if (_noiseFloor < 0f) _noiseFloor = 0f;
        }
    }
}
