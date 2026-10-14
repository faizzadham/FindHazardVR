using Convai.Modules.Gaze.Data;
using Convai.Runtime.Embodiment;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Behaviors
{
    /// <summary>
    ///     Deliberate contact-break beats that keep engaged gaze from reading as a stare:
    ///     cognitive aversion (Thinking looks up/aside while "recalling") and natural
    ///     conversational aversion (brief, subtle breaks during long mutual gaze). Produces
    ///     an angular offset applied on top of the target aim plus an
    ///     <see cref="IsAverting" /> flag surfaced in the gaze reading.
    /// </summary>
    /// <remarks>
    ///     The offset eases in/out so beats read as intentional glances, not twitches.
    ///     Strength scales both the frequency and the amplitude of the beats; strength 0
    ///     (or mode None) produces unbroken contact — the shipped Speaking/Reacting default.
    /// </remarks>
    internal sealed class AversionDirector
    {
        /// <summary>
        ///     Time constants an exponential needs to be within about 5% of its target. Used to
        ///     turn a movement duration into an ease rate.
        /// </summary>
        private const float SettleTimeConstants = 3f;

        /// <summary>Shortest a beat may take, so a one-degree flick is still a movement.</summary>
        private const float MinBeatSeconds = 0.25f;

        // The head's own movement law, handed over by the controller so a beat travels at the
        // speed a deliberate look of the same size would. Defaults mirror the shipped profile, so
        // a director nobody configures behaves as the profile says rather than as a magic number.
        private float _headMoveBaseSeconds = 0.45f;
        private float _headMoveSecondsPerDegree = 0.0125f;

        // The ease in flight: where it is going and how long it decided that should take.
        private Vector2 _easeTarget;
        private Vector2 _easeStart;
        private float _easeElapsed;
        private float _easeSeconds = MinBeatSeconds;
        private bool _hasEase;

        /// <summary>The mode the current beat was drawn under; a change of mode ends the beat.</summary>
        private GazeAversionMode _beatMode = GazeAversionMode.None;

        private enum Phase
        {
            Contact,
            Averting
        }

        private Phase _phase = Phase.Contact;
        private Vector2 _beatOffset;
        private Vector2 _currentOffset;
        private float _phaseRemaining;
        private bool _initialized;

        /// <summary>Emotion-modulation hook: scales strength (1 = authored strength).</summary>
        public float StrengthScale { get; set; } = 1f;

        /// <summary>
        ///     Tells the director how long the head takes to move a given distance, so a beat can
        ///     obey the same law as everything else that moves this head.
        /// </summary>
        /// <remarks>
        ///     Without it the ease ran at one rate for every beat, so the largest cognitive
        ///     look-away and the smallest conversational one both finished in about a third of a
        ///     second — the larger travelling nearly four times faster than a deliberate look of
        ///     that size, which is what made it read as a flinch after the character stopped
        ///     speaking rather than as glancing away.
        /// </remarks>
        public void SetHeadMovementLaw(float baseSeconds, float secondsPerDegree)
        {
            _headMoveBaseSeconds = Mathf.Max(0f, baseSeconds);
            _headMoveSecondsPerDegree = Mathf.Max(0f, secondsPerDegree);
        }

        /// <summary>Current eased aversion offset (yaw/pitch degrees) — the head's share.</summary>
        public Vector2 Offset => _currentOffset;

        /// <summary>
        ///     Instantaneous beat target (yaw/pitch degrees) for the eye stage. The eyes
        ///     glance onto and off a beat with single ballistic saccades, so they receive
        ///     the raw step; feeding them the eased ramp would smear one glance into a
        ///     stutter of catch-up saccades. The head keeps the eased <see cref="Offset" />.
        /// </summary>
        public Vector2 EyeOffset => _beatOffset;

        /// <summary>
        ///     Whether a contact-break beat is currently active. Keyed to the sampled beat
        ///     target, not the eased head ramp: the eyes glance onto the beat with a single
        ///     ballistic saccade as soon as it is sampled (including a <see cref="ForceBeat" />
        ///     with no intervening <see cref="Tick" />), so contact is deliberately broken from
        ///     the decision tick, while <see cref="Offset" /> is still catching up.
        /// </summary>
        public bool IsAverting => _phase == Phase.Averting && _beatOffset.sqrMagnitude > 1f;

        public void Reset()
        {
            _phase = Phase.Contact;
            _beatOffset = Vector2.zero;
            _currentOffset = Vector2.zero;
            _easeTarget = Vector2.zero;
            _easeStart = Vector2.zero;
            _easeElapsed = 0f;
            _easeSeconds = MinBeatSeconds;
            _hasEase = false;
            _beatMode = GazeAversionMode.None;
            _phaseRemaining = 0f;
            _initialized = false;
            StrengthScale = 1f;
        }

        /// <summary>
        ///     Forces the director directly into an <see cref="Phase.Averting" /> beat with a
        ///     freshly sampled cognitive (up/side) offset, bypassing the normal Contact-phase
        ///     wait — used by the turn-taking planning break (<c>TurnTakingDirector</c>),
        ///     which needs the same "thinking" beat shape as natural cognitive aversion but on
        ///     its own short schedule right at the start of an utterance rather than on the
        ///     state's authored cadence. The beat then runs its normal Averting → Contact life
        ///     through <see cref="Tick" /> as long as the caller keeps feeding
        ///     <see cref="GazeAversionMode.Cognitive" /> with a non-zero strength for the beat's
        ///     duration — otherwise <see cref="Tick" />'s own disengage branch would reset it
        ///     immediately on the very next call.
        /// </summary>
        public void ForceCognitiveBeat(float durationSeconds, float strength, ref DeterministicEmbodimentRandom random)
            => ForceBeat(GazeAversionMode.Cognitive, durationSeconds, strength, ref random);

        /// <summary>Forces one bounded turn-taking beat using the existing aversion sampler.</summary>
        public void ForceBeat(
            GazeAversionMode mode,
            float durationSeconds,
            float strength,
            ref DeterministicEmbodimentRandom random)
        {
            _initialized = true;
            _phase = Phase.Averting;
            _phaseRemaining = Mathf.Max(0.01f, durationSeconds);
            _beatOffset = SampleBeatOffset(mode, Mathf.Clamp01(strength), ref random);
            _beatMode = mode;
        }

        /// <summary>
        ///     Ticks the director for one frame.
        /// </summary>
        /// <param name="bias">
        ///     Emotion-driven override for the beat's direction, sampled instead of the
        ///     mode's own shape whenever it is not <see cref="GazeAversionBias.CognitiveDefault" />.
        ///     Callers forcing a turn-taking planning break through <see cref="ForceCognitiveBeat" />
        ///     must pass <see cref="GazeAversionBias.CognitiveDefault" /> here for the duration of
        ///     that beat — a planning break is cognitive, not emotional, and must keep its
        ///     up/side shape regardless of the active emotion.
        /// </param>
        public void Tick(
            GazeAversionMode mode,
            float strength,
            GazeAversionBias bias,
            bool engaged,
            float deltaTime,
            ref DeterministicEmbodimentRandom random)
        {
            strength = Mathf.Clamp01(strength * StrengthScale);

            if (!engaged || mode == GazeAversionMode.None || strength <= 0.001f)
            {
                // Ease any residual offset back to contact.
                _phase = Phase.Contact;
                _beatOffset = Vector2.zero;
                _currentOffset = EaseOffset(_currentOffset, Vector2.zero, deltaTime);
                _initialized = false;
                return;
            }

            if (!_initialized)
            {
                _initialized = true;
                _phase = Phase.Contact;
                _phaseRemaining = SampleContactSeconds(mode, strength, ref random);
            }

            // A beat belongs to the state that drew it. Thinking's look-away is up and to the
            // side and twice the size of a conversational one; carried into Listening — where the state row asks for a faint
            // natural break — it read as the character rolling its eyes at the person who had
            // just started talking to it. The moment the mode changes, the beat ends and the
            // ease brings the head home.
            if (_phase == Phase.Averting && mode != _beatMode)
            {
                _phase = Phase.Contact;
                _phaseRemaining = SampleContactSeconds(mode, strength, ref random);
                _beatOffset = Vector2.zero;
            }

            _phaseRemaining -= deltaTime;
            if (_phaseRemaining <= 0f)
            {
                if (_phase == Phase.Contact)
                {
                    _phase = Phase.Averting;
                    _phaseRemaining = SampleAvertSeconds(mode, ref random);
                    _beatOffset = SampleBeatOffset(mode, strength, bias, ref random);
                    _beatMode = mode;
                }
                else
                {
                    _phase = Phase.Contact;
                    _phaseRemaining = SampleContactSeconds(mode, strength, ref random);
                    _beatOffset = Vector2.zero;
                }
            }

            // Ease toward the beat offset — glance speed, not saccade math (the eye stage
            // still saccades onto the offset point naturally).
            _currentOffset = EaseOffset(_currentOffset, _beatOffset, deltaTime);
        }

        /// <summary>
        ///     Eases the head's share of a beat toward <paramref name="target" />.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         An exponential ease rather than the fixed 140 °/s ramp this replaced. A
        ///         constant rate is the same defect the head chain had: it makes every beat, of
        ///         any size, travel at one speed, so a small look-away is over in under a tenth of
        ///         a second and starts and stops with a velocity step at both ends. A beat that
        ///         quick does not read as a glance — it reads as a flinch.
        ///     </para>
        ///     <para>
        ///         Exponential rather than the ballistic profile the aim uses, deliberately: an
        ///         aversion beat is a soft drift off contact and back, not a decision to look at
        ///         something else, and it is composed on top of a movement that may itself be in
        ///         flight. Giving it its own ballistic clock would have two movement profiles
        ///         fighting on one channel.
        ///     </para>
        /// </remarks>
        private Vector2 EaseOffset(Vector2 current, Vector2 target, float deltaTime)
        {
            // How long this ease should take is decided once, from the distance it set out to
            // cover — not from the distance still left. Recomputing it every frame shortens the
            // law as the gap closes, so the movement accelerates into its own target and arrives
            // early: a beat measured at 0.52 s against the 0.68 s its size called for.
            if (!_hasEase || (target - _easeTarget).sqrMagnitude > 1e-6f)
            {
                _easeTarget = target;
                _easeStart = current;
                _easeElapsed = 0f;
                _easeSeconds = Mathf.Max(
                    MinBeatSeconds,
                    _headMoveBaseSeconds + _headMoveSecondsPerDegree * Vector2.Distance(current, target));
                _hasEase = true;
            }

            // A movement, not a filter. The exponential this replaces left at peak slew — the
            // head's share of a beat jumped 1.7° in its first frame, sixty degrees a second from
            // rest, which is exactly the "sudden little head reposition" a listener produced every
            // few seconds. Minimum jerk starts and ends at rest and covers the distance in the
            // time the head's own law gave it, so a look-away is shaped like the look it is.
            _easeElapsed += Mathf.Max(0f, deltaTime);
            float t = Mathf.Clamp01(_easeElapsed / _easeSeconds);
            float ease = t * t * t * (10f + t * (-15f + 6f * t));
            return Vector2.LerpUnclamped(_easeStart, _easeTarget, ease);
        }

        private static float SampleContactSeconds(
            GazeAversionMode mode, float strength, ref DeterministicEmbodimentRandom random)
        {
            // Stronger aversion → shorter contact stretches.
            return mode == GazeAversionMode.Cognitive
                ? Mathf.Lerp(4.5f, 1.2f, strength) + random.Range(0f, 1.5f)
                : Mathf.Lerp(14f, 5f, strength) + random.Range(0f, 4f);
        }

        private static float SampleAvertSeconds(GazeAversionMode mode, ref DeterministicEmbodimentRandom random)
        {
            return mode == GazeAversionMode.Cognitive
                ? random.Range(0.9f, 2.2f)
                : random.Range(0.4f, 1.1f);
        }

        private static Vector2 SampleBeatOffset(
            GazeAversionMode mode, float strength, ref DeterministicEmbodimentRandom random)
        {
            float side = random.Value < 0.5f ? -1f : 1f;

            // Sizes are what a person's eyes do while they think, not what their head does while
            // they look at something else. The ranges above these covered a fifth of the visual
            // field — at 22° of yaw the character was no longer averting its gaze from the person
            // it was talking to, it was inspecting the wall behind them, and coming back read as
            // a second decision rather than as the end of a thought.
            if (mode == GazeAversionMode.Cognitive)
            {
                // The classic "thinking" look: up and to the side.
                return new Vector2(
                    side * random.Range(8f, 14f),
                    random.Range(5f, 10f)) * Mathf.Lerp(0.6f, 1f, strength);
            }

            // Natural break: smaller, often downward (soft disengage). Scaled off the cognitive
            // beat by the same factors its own ends moved (×0.83 at the near end, ×0.7 at the
            // far one), so the two shapes keep the proportion they were authored in.
            float pitch = random.Value < 0.6f ? random.Range(-6f, -2.5f) : random.Range(1.5f, 4f);
            return new Vector2(side * random.Range(5f, 9f), pitch) * Mathf.Lerp(0.5f, 1f, strength);
        }

        /// <summary>
        ///     Emotion-biased beat direction: overrides the mode's own up/side or
        ///     mostly-down shape while an emotion with a non-default <see cref="GazeAversionBias" />
        ///     is dominant. <see cref="GazeAversionBias.CognitiveDefault" /> falls back to the
        ///     mode-based <see cref="SampleBeatOffset(GazeAversionMode, float, ref DeterministicEmbodimentRandom)" />
        ///     unchanged, so existing callers and rows without an authored bias sample
        ///     byte-identically to before.
        /// </summary>
        private static Vector2 SampleBeatOffset(
            GazeAversionMode mode, float strength, GazeAversionBias bias, ref DeterministicEmbodimentRandom random)
        {
            if (bias == GazeAversionBias.CognitiveDefault)
                return SampleBeatOffset(mode, strength, ref random);

            float side = random.Value < 0.5f ? -1f : 1f;

            switch (bias)
            {
                case GazeAversionBias.Up:
                    // Straight up, little side drift — a distancing "can't look at you" beat.
                    return new Vector2(side * random.Range(2f, 6f), random.Range(8f, 16f)) *
                           Mathf.Lerp(0.6f, 1f, strength);

                case GazeAversionBias.Side:
                    // Level sideways glance — anger's sharp, confrontational-avoidant beat.
                    return new Vector2(side * random.Range(12f, 22f), random.Range(-2f, 2f)) *
                           Mathf.Lerp(0.6f, 1f, strength);

                case GazeAversionBias.Down:
                    // Straight down — sadness's withdrawn, low-energy beat.
                    return new Vector2(side * random.Range(2f, 6f), -random.Range(6f, 13f)) *
                           Mathf.Lerp(0.5f, 1f, strength);

                case GazeAversionBias.DownSide:
                    // Down and to the side — shame/embarrassment's averted beat.
                    return new Vector2(side * random.Range(10f, 18f), -random.Range(5f, 11f)) *
                           Mathf.Lerp(0.5f, 1f, strength);

                default:
                    return SampleBeatOffset(mode, strength, ref random);
            }
        }
    }
}
