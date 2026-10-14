using Convai.Modules.Gaze.Data;
using Convai.Runtime.Embodiment;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Behaviors
{
    /// <summary>
    ///     Active-listening head gesture: while the character listens it produces small
    ///     acknowledgment nods — one likely nod when <c>Listening</c> begins ("I'm with you")
    ///     and sparse nods on a randomized cadence afterwards. The result is a pitch-dominant
    ///     angular offset fed to the head/torso solver's gesture channel; nothing plays outside
    ///     <c>Listening</c> or while the gesture is suppressed (the character is producing
    ///     speech, or has no one to nod at).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The nod envelope is a procedural damped double-bob (two downward lobes, the
    ///         second ~55% of the first) that begins and ends exactly at rest, so it never
    ///         needs authored clips or curves and never pops the head goal.
    ///     </para>
    ///     <para>
    ///         The offset is applied to the bones post-spring, so this director guarantees a
    ///         continuous output: an active envelope is C¹-smooth by construction, and every
    ///         cancellation path (state exit, suppression) fades the residual offset to rest
    ///         at a bounded rate instead of stepping.
    ///     </para>
    ///     <para>
    ///         Suppression pauses without re-arming: only leaving <c>Listening</c> re-rolls
    ///         the acknowledgment nod, so a brief speech-energy flicker mid-listening cannot
    ///         fire spurious entry nods. <see cref="TriggerNod" /> is a public seam so a
    ///         future driver (e.g. player microphone energy) can request a nod on a point
    ///         without touching the scheduling here.
    ///     </para>
    /// </remarks>
    internal sealed class BackchannelDirector
    {
        /// <summary>The lobe already crests at exactly 1, so the configured pitch is the peak.</summary>
        private const float NodShapeNormalization = 1f;

        /// <summary>
        ///     Fade rate (degrees/second) for a nod cancelled mid-flight. The offset feeds the
        ///     bones after the smoothing springs, so cancellations must ease out — a step here
        ///     would pop the head. ~45°/s clears a full-amplitude residual in under 0.1 s.
        /// </summary>
        private const float CancelFadeDegreesPerSecond = 45f;

        /// <summary>Earliest a nod may land after listening begins — a listener settles in first.</summary>
        internal const float FirstNodMinSeconds = 2f;

        /// <summary>How close to due a scheduled nod must be for a pause in the speech to bring it forward.</summary>
        internal const float PauseCueWindowSeconds = 3f;

        private float _listeningFor;
        private bool _acknowledgeOnPause;
        private bool _active;
        private float _elapsed;
        private float _countdown;
        private bool _wasListening;
        private float _intensity = 1f;
        private Vector2 _offset;
        private bool _startedThisTick;

        /// <summary>Current head gesture offset (yaw/pitch degrees, pitch-dominant); zero at rest.</summary>
        public Vector2 GestureOffset => _offset;

        /// <summary>Whether a nod envelope is currently playing (diagnostics/tests).</summary>
        public bool IsNodding => _active;

        /// <summary>
        ///     True only on the tick a nod begins (entry or cadence) — a one-shot pulse for
        ///     consumers that want to react to a nod starting (e.g. the brow-cue
        ///     coordinator), mirroring <c>InterruptionReactionDirector.WantsReacquisition</c>'s
        ///     single-tick pulse contract.
        /// </summary>
        public bool NodStartedThisTick => _startedThisTick;

        public void Reset()
        {
            _active = false;
            _elapsed = 0f;
            _countdown = 0f;
            _wasListening = false;
            _listeningFor = 0f;
            _acknowledgeOnPause = false;
            _intensity = 1f;
            _offset = Vector2.zero;
            _startedThisTick = false;
        }

        /// <param name="profile">Tuning source; a null profile disables the director.</param>
        /// <param name="isListening">Whether the dialogue state is <c>Listening</c>.</param>
        /// <param name="suppressed">
        ///     Hard-pause without re-arming: the character is producing speech, or has no
        ///     engaged target to nod at. Any active nod fades out; the schedule freezes.
        /// </param>
        /// <param name="pauseCue">
        ///     True on the tick the speaker's stream paused (their voice flag fell). Nods are
        ///     drawn to these: an acknowledgement waits for the first one, and a scheduled nod
        ///     that is nearly due is brought forward onto it.
        /// </param>
        public void Tick(
            ConvaiGazeProfile profile,
            bool isListening,
            bool suppressed,
            float deltaTime,
            ref DeterministicEmbodimentRandom random,
            bool pauseCue = false)
        {
            _startedThisTick = false;

            bool canRun = profile != null && profile.EnableListeningNods && isListening;

            // Only leaving Listening re-arms the acknowledgment nod for the next entry;
            // suppression mid-listening must not re-roll it.
            if (!canRun)
                _wasListening = false;

            if (!canRun || suppressed)
            {
                if (_active)
                {
                    // The cancelled nod counts as delivered — resample the cadence so the
                    // schedule does not fire a make-up nod the instant suppression lifts.
                    _active = false;
                    _countdown = profile != null ? SampleInterval(profile, ref random) : 0f;
                }

                FadeOut(deltaTime);
                return;
            }

            if (!_wasListening)
            {
                _wasListening = true;
                _listeningFor = 0f;
                // Never on the entry edge. That edge is "the player has started talking", and a
                // head that bobs the instant somebody opens their mouth reads as a tic, not as
                // listening — with the eyes counter-rotating to stay on the face, it reads as
                // the eyes rolling up and back down. Real acknowledgement nods land on the
                // speaker's phrase boundaries, a couple of seconds in at the earliest. The
                // acknowledge probability now decides whether the FIRST nod waits for such a
                // pause (likely) or simply for the interval (the rest).
                _acknowledgeOnPause = random.Value < profile.AcknowledgeNodProbability;
                _countdown = SampleInterval(profile, ref random);
            }

            _listeningFor += deltaTime;

            if (_active)
            {
                _elapsed += deltaTime;
                float p = _elapsed / Mathf.Max(0.05f, profile.NodDurationSeconds);
                if (p >= 1f)
                {
                    // Shape(1) is exactly 0, so ending here is continuous.
                    _active = false;
                    _offset = Vector2.zero;
                    _countdown = SampleInterval(profile, ref random);
                }
                else
                {
                    _offset = new Vector2(0f, -profile.NodPitchDegrees * _intensity * Shape(p));
                }
                return;
            }

            // Idle between nods: finish any residual fade from a cancellation, run the cadence.
            FadeOut(deltaTime);
            _countdown -= deltaTime;

            // A pause in the speaker's stream is where a nod belongs. One that arrives while a
            // nod is already close on the schedule brings it forward; the first pause after
            // settling in takes the acknowledgement nod, when the draw said there would be one.
            bool settledIn = _listeningFor >= FirstNodMinSeconds;
            if (pauseCue && settledIn && (_acknowledgeOnPause || _countdown <= PauseCueWindowSeconds))
            {
                _acknowledgeOnPause = false;
                StartNod(1f);
                return;
            }

            if (_countdown <= 0f && settledIn)
                StartNod(1f);
        }

        /// <summary>
        ///     Requests a nod at the given intensity (0..1) on the next tick. Public seam for
        ///     future drivers; still suppressed unless the character is actively listening.
        /// </summary>
        public void TriggerNod(float intensity01) => StartNod(Mathf.Clamp01(intensity01));

        private void StartNod(float intensity)
        {
            _active = true;
            _elapsed = 0f;
            _intensity = Mathf.Clamp01(intensity);
            _startedThisTick = true;
        }

        private void FadeOut(float deltaTime) =>
            _offset = Vector2.MoveTowards(_offset, Vector2.zero, CancelFadeDegreesPerSecond * deltaTime);

        private static float SampleInterval(ConvaiGazeProfile profile, ref DeterministicEmbodimentRandom random) =>
            random.Range(profile.ListeningNodIntervalMin, profile.ListeningNodIntervalMax);

        /// <summary>
        ///     Damped double-bob over normalized phase <paramref name="p" /> ∈ [0,1]: two
        ///     downward lobes eased to rest at both ends (value and first derivative zero), the
        ///     second ~55% of the first. Returns a non-negative 0..~1 magnitude. Internal so
        ///     the C¹ endpoints can be asserted directly.
        /// </summary>
        /// <summary>
        ///     One soft lobe: the head dips and comes back once, cresting a little before the
        ///     middle so the return is slower than the dip, the way a nod actually goes. The
        ///     two-lobed bob it replaces (a second, weaker dip riding a decay) read as a tic at
        ///     the amplitude and speed a listener produces it — and at 4° with the eyes holding
        ///     the face, as the eyes rolling up and down.
        /// </summary>
        internal static float Shape(float p)
        {
            p = Mathf.Clamp01(p);
            // Skew the phase so the crest sits at p ≈ 0.4: quick down, gentle up.
            float skewed = p < 0.4f ? p / 0.4f * 0.5f : 0.5f + (p - 0.4f) / 0.6f * 0.5f;
            float lobe = Mathf.Sin(skewed * Mathf.PI);
            return lobe * lobe * (3f - 2f * lobe) * NodShapeNormalization;
        }
    }
}
