using UnityEngine;

namespace Convai.Runtime.Animation.ProceduralPose
{
    /// <summary>
    ///     Per-channel pursuit filter: a critically damped second-order tracker with a bounded
    ///     velocity lead, for following a goal that is already moving.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The third verb.</b> <see cref="BallisticMotor" /> executes a decision to move
    ///         somewhere (a step in the goal becomes a shaped movement); <see cref="MotorFilter" />
    ///         holds a channel against a safety envelope and is otherwise transparent. Neither is
    ///         a way to <i>follow</i> something: transparency reproduces every acceleration of the
    ///         input, so a head tracking a hand-driven camera reproduces the hand — and the only
    ///         thing left shaping it is the acceleration cap, which is bang-bang by construction.
    ///         Following needs bandwidth: a filter whose response time is a property of the body,
    ///         not of the signal.
    ///     </para>
    ///     <para>
    ///         Critically damped, so a step never overshoots, and driven by the goal's own
    ///         velocity with a lead of exactly <c>1/ω</c>. That lead is the largest one whose
    ///         frequency response never peaks above unity (a lead of <c>2/ω</c> would cancel the
    ///         ramp lag completely and ring at the natural frequency instead); it halves the lag
    ///         behind a constant-velocity goal, to <c>v/ω</c>. The remaining lag is deliberate —
    ///         a head in pursuit trails what it follows, and the eyes cover the difference.
    ///     </para>
    ///     <para>
    ///         Integrated semi-implicitly in substeps no longer than
    ///         <see cref="MaxSubstepSeconds" />, so a frame hitch or a level-of-detail lump
    ///         advances the same trajectory rather than a stiffer one. Struct, zero-alloc, no
    ///         UnityEngine.Object references — the same contract as its two siblings.
    ///     </para>
    /// </remarks>
    internal struct PursuitTracker
    {
        /// <summary>
        ///     Fraction of the goal velocity fed forward. 0.5 is <c>1/ω</c> of lead — see the
        ///     type remarks for why it is the largest value that cannot overshoot.
        /// </summary>
        private const float LeadFraction = 0.5f;

        /// <summary>Longest integration substep (seconds); the tracker is frame-rate stable well past this.</summary>
        private const float MaxSubstepSeconds = 1f / 90f;

        /// <summary>Ceiling on substeps per call, so an outsized frame time costs bounded work.</summary>
        private const int MaxSubsteps = 16;

        /// <summary>Shortest response time accepted, so a profile cannot ask for an infinitely stiff neck.</summary>
        private const float MinResponseSeconds = 0.02f;

        private float _current;
        private float _velocity;
        private bool _initialized;

        /// <summary>The last value returned by <see cref="Step" />.</summary>
        public float Current => _current;

        /// <summary>Channel velocity (units/second) as of the last <see cref="Step" />.</summary>
        public float Velocity => _velocity;

        /// <summary>
        ///     Advances the tracker toward <paramref name="goal" />.
        /// </summary>
        /// <param name="goal">Where the channel should be.</param>
        /// <param name="goalVelocity">
        ///     The goal's own velocity (units/second), estimated by the caller. Zero is always
        ///     safe: the tracker then behaves as a plain critically damped spring.
        /// </param>
        /// <param name="responseSeconds">
        ///     Response time. The natural frequency is <c>2 / responseSeconds</c>, so a step
        ///     settles in roughly two response times and a constant-velocity goal is trailed by
        ///     <c>velocity × responseSeconds / 2</c>.
        /// </param>
        /// <param name="maxSpeed">Safety envelope, units/second.</param>
        /// <param name="maxAccel">Safety envelope, units/second².</param>
        /// <param name="deltaTime">Tick delta.</param>
        public float Step(
            float goal, float goalVelocity, float responseSeconds, float maxSpeed, float maxAccel, float deltaTime)
        {
            if (deltaTime <= 0f) return _current;

            if (!_initialized)
            {
                Seed(goal);
                return _current;
            }

            float omega = 2f / Mathf.Max(MinResponseSeconds, responseSeconds);
            float stiffness = omega * omega;
            float damping = 2f * omega;
            float lead = LeadFraction * goalVelocity;
            float speedLimit = Mathf.Max(0f, maxSpeed);
            float accelLimit = Mathf.Max(0f, maxAccel);

            int substeps = Mathf.Clamp(Mathf.CeilToInt(deltaTime / MaxSubstepSeconds), 1, MaxSubsteps);
            float h = deltaTime / substeps;
            for (int i = 0; i < substeps; i++)
            {
                float acceleration = stiffness * (goal - _current) + damping * (lead - _velocity);
                acceleration = Mathf.Clamp(acceleration, -accelLimit, accelLimit);
                _velocity = Mathf.Clamp(_velocity + acceleration * h, -speedLimit, speedLimit);
                _current += _velocity * h;
            }

            return _current;
        }

        /// <summary>
        ///     Starts the tracker at <paramref name="value" /> moving at <paramref name="velocity" />,
        ///     so a hand-off from another lane carries its momentum instead of restarting from rest.
        /// </summary>
        public void Seed(float value, float velocity = 0f)
        {
            _current = value;
            _velocity = velocity;
            _initialized = true;
        }

        /// <summary>Zeroes all state; the next <see cref="Step" /> starts at its goal, at rest.</summary>
        public void Reset()
        {
            _current = 0f;
            _velocity = 0f;
            _initialized = false;
        }
    }
}
