using Convai.Modules.Gaze.Data;
using Convai.Runtime.Animation.ProceduralPose;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Reorientation
{
    /// <summary>
    ///     Fallback body turn used when no <c>ICharacterReorientationHandler</c> (i.e. no
    ///     Body Animation module) is available: a critically-damped root-yaw swivel toward
    ///     the target direction. Not as pretty as an animated turn-in-place, but never
    ///     leaves the character talking to a wall.
    /// </summary>
    internal sealed class ProceduralReorientationDriver
    {
        /// <summary>
        ///     Angular acceleration ceiling (deg/s²) for the swivel — a safety envelope, not the
        ///     thing that shapes the turn. The shape comes from the minimum-jerk profile and the
        ///     duration below; this only catches a turn that was re-aimed so far, so late, that
        ///     finishing on its original clock would need a rate no body could produce.
        /// </summary>
        private const float TurnMaxAccel = 500f;

        /// <summary>Residual angular velocity (deg/s) below which the turn counts as settled.</summary>
        private const float SettleVelocityThreshold = 20f;

        /// <summary>
        ///     A minimum-jerk movement peaks at 1.875× its average rate. The profile's turn speed
        ///     is authored as how fast the character turns — i.e. its peak — so the duration is
        ///     stretched by this factor to make that true, rather than letting the peak overshoot
        ///     the authored number by 87 %.
        /// </summary>
        private const float MinJerkPeakFactor = 1.875f;

        /// <summary>
        ///     Floor (seconds) on a turn's duration. Without it a small correction would be
        ///     planned into almost no time at all, which is the same square-root-of-distance
        ///     problem that made small head movements the sharpest thing on screen.
        /// </summary>
        private const float MinTurnSeconds = 0.25f;

        private Transform _rotationRoot;
        private Transform _facingReference;
        private Vector3 _targetDirection;

        /// <summary>
        ///     The direction the current movement's clock was planned against, so a re-aim can be
        ///     measured from where the goal WAS when the plan was made rather than from where it
        ///     was last frame. Accumulated drift — a player walking steadily — then trips the
        ///     re-plan once it adds up to something, while a stationary goal never trips it at all.
        /// </summary>
        private Vector3 _plannedDirection;

        private BallisticMotor _turnMotor;
        private float _skew;

        public bool IsActive { get; private set; }

        /// <summary>
        ///     Starts a turn that rotates <paramref name="rotationRoot" /> until the
        ///     <paramref name="facingReference" /> (the rig the player actually sees, which
        ///     the root carries rigidly) faces <paramref name="worldDirection" />. Rotating
        ///     and measuring the same character-root transform the animated path uses keeps
        ///     the two turn paths from ever de-synchronizing parent and child.
        /// </summary>
        public void Begin(
            Transform rotationRoot,
            Transform facingReference,
            Vector3 worldDirection,
            ConvaiGazeProfile profile)
        {
            worldDirection.y = 0f;
            if (rotationRoot == null || worldDirection.sqrMagnitude < 1e-6f || profile == null) return;

            _rotationRoot = rotationRoot;
            _facingReference = facingReference != null ? facingReference : rotationRoot;
            _targetDirection = worldDirection.normalized;
            _plannedDirection = _targetDirection;
            _skew = Mathf.Clamp01(profile.MovementSkew);

            // The turn is planned as one movement: how far there is to go decides how long it
            // takes, and the minimum-jerk profile decides its shape. The channel is the angle
            // STILL TO COVER, so the movement runs from that angle down to zero.
            float angle = RemainingAngle();
            _turnMotor.Begin(angle, 0f, TurnSeconds(angle, profile));
            IsActive = Mathf.Abs(angle) > profile.BodyTurnCompletionToleranceDegrees;
        }

        /// <summary>
        ///     Re-aims an in-flight swivel at a new world direction so a moving target is
        ///     tracked live — the procedural counterpart of the animated turn's re-aim.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Re-aiming changes where the turn is going, not the fact that it is already
        ///         moving, so the movement is re-planned from the character's current angular
        ///         velocity rather than restarted from a standstill. The clock is re-planned too:
        ///         a turn re-aimed from 20° left to 70° right is a bigger movement than the one
        ///         that was started, and asking it to finish on the original deadline is how a
        ///         turn ends up whipping.
        ///     </para>
        ///     <para>
        ///         <b>But only when the goal actually moved.</b> The caller invokes this every
        ///         frame of a turn, whether or not the target went anywhere, so the re-plan needs
        ///         a reason and "does the size of what is left still warrant more time than the
        ///         clock has?" is not one. That comparison is true <i>by construction</i> for most
        ///         of a healthy movement: a minimum-jerk profile leaves at rest, so early on the
        ///         angle shrinks more slowly than the clock runs down, and <c>TurnSeconds</c> of
        ///         the remaining angle therefore exceeds <c>Remaining</c>. Left ungated it
        ///         re-planned on every single frame, resetting the clock to a full fresh duration
        ///         each time — so the turn was pinned in its opening phase forever, never reached
        ///         the fast middle, and never landed. Velocity carries across a re-plan, so it did
        ///         not stutter or pop; it simply crawled. Measured on the 180° fallback pivot: 65°
        ///         covered in 3.3 s, against the 2.4 s the movement was planned for.
        ///     </para>
        ///     <para>
        ///         Measuring the shift from the direction the clock was PLANNED against, rather
        ///         than from last frame's, is what keeps a steadily walking target working: no
        ///         single frame of drift is material, but the accumulation is, and it trips the
        ///         re-plan once it grows past the tolerance the turn is trying to land inside.
        ///         Sub-threshold drift still tracks perfectly — <see cref="Tick" /> re-derives the
        ///         whole polynomial toward the live goal every frame regardless. This gates the
        ///         CLOCK, not the aim.
        ///     </para>
        /// </remarks>
        public void Retarget(Vector3 worldDirection, ConvaiGazeProfile profile)
        {
            worldDirection.y = 0f;
            if (!IsActive || worldDirection.sqrMagnitude < 1e-6f || profile == null) return;

            _targetDirection = worldDirection.normalized;

            // Re-aimed by less than the turn is trying to land inside? Then it is the same
            // movement to the same place, and its clock stands.
            float aimShift = Vector3.Angle(_plannedDirection, _targetDirection);
            if (aimShift <= Mathf.Max(0f, profile.BodyTurnCompletionToleranceDegrees)) return;

            float angle = RemainingAngle();
            float replanned = TurnSeconds(angle, profile);
            if (replanned <= _turnMotor.Remaining) return;

            _plannedDirection = _targetDirection;
            _turnMotor.Begin(angle, _turnMotor.Velocity, replanned);
        }

        public void Cancel()
        {
            IsActive = false;
            _turnMotor.Reset();
            _rotationRoot = null;
            _facingReference = null;
            _plannedDirection = Vector3.zero;
        }

        /// <summary>Advances the turn. Returns <c>true</c> while still turning.</summary>
        public bool Tick(ConvaiGazeProfile profile, float deltaTime)
        {
            if (!IsActive || _rotationRoot == null || profile == null)
            {
                IsActive = false;
                return false;
            }

            // The same movement machinery the head chain uses: a duration chosen from the size
            // of the turn, and a minimum-jerk profile over it. The rate limiter this replaced
            // produced the time-optimal turn instead — full acceleration, cruise, brake — which
            // is the harshest pivot its caps allow and reads mechanical rather than muscular.
            //
            // The angle still to cover is re-measured from live geometry every frame — the
            // target moves, and on the animated path something else may have rotated the root —
            // so the motor is told where the value actually is before it steps, while keeping
            // the rate it had. Stepping from its own stale value would fight whatever moved it.
            float peakSpeed = Mathf.Max(45f, profile.ProceduralTurnSpeed);
            float tolerance = Mathf.Max(0f, profile.BodyTurnCompletionToleranceDegrees);

            float remaining = RemainingAngle();
            _turnMotor.SeedPreservingVelocity(remaining);

            // The movement lands by ASSIGNMENT on the last frame of its clock: the motor puts the
            // channel on the goal and stops. That is exactly right when the goal is where the
            // movement was planned to end, and it is a whip when it is not — a re-aim mid-flight
            // can leave more angle to cover than the clock has time for (and the reversal it asks
            // for is precisely where the motor's own acceleration envelope holds it back), so the
            // landing then applies the WHOLE residual in a single frame. Measured at 28.5° in one
            // frame — about 1880°/s — on a 90° turn re-aimed to the other side while in flight.
            //
            // So a residual still worth moving is re-planned as its own movement, from the rate
            // the body is already turning at and over the duration that size of turn deserves,
            // BEFORE the motor is allowed to land on it. A residual the turn is already close
            // enough to stop inside is left to the tolerance rule below, which is what keeps a
            // jittering target from re-planning forever instead of settling.
            if (deltaTime > 0f && _turnMotor.Remaining <= deltaTime && Mathf.Abs(remaining) > tolerance)
                _turnMotor.Begin(remaining, _turnMotor.Velocity, TurnSeconds(remaining, profile));

            float next = _turnMotor.Step(0f, peakSpeed, TurnMaxAccel, _skew, deltaTime);

            // Hard envelope on how far one frame may rotate the body: the authored turn speed,
            // which IS the peak — TurnSeconds already stretches the clock by MinJerkPeakFactor so
            // that the minimum-jerk profile peaks at exactly this rate, and the motor's own speed
            // cap holds a shaped step under it. The one frame that bypasses that cap is a landing
            // by assignment, and the re-plan above is what keeps such landings small; this is the
            // net underneath, so no future path through the motor can put a rate no body could
            // produce on screen. It is silent by design — a clamp that logged would log per
            // frame, and the thing worth knowing is measured by the tests, not the console.
            float maxStep = peakSpeed * Mathf.Max(0f, deltaTime);
            _rotationRoot.Rotate(0f, Mathf.Clamp(remaining - next, -maxStep, maxStep), 0f, Space.World);

            // A finished motor is the primary completion signal — with the re-plan above it can
            // only finish on a residual small enough to settle. The tolerance check stays as the
            // secondary one: geometry can move underneath the turn and put the character on
            // target early.
            bool settled = !_turnMotor.IsActive ||
                           (Mathf.Abs(next) <= tolerance &&
                            Mathf.Abs(_turnMotor.Velocity) < SettleVelocityThreshold);
            if (settled)
            {
                IsActive = false;
                _turnMotor.Reset();
                return false;
            }

            return true;
        }

        /// <summary>
        ///     How long a turn of this size takes. Linear in the angle from a floor, with the
        ///     profile's turn speed read as the movement's PEAK rate rather than its average —
        ///     see <see cref="MinJerkPeakFactor" />.
        /// </summary>
        private static float TurnSeconds(float angleDegrees, ConvaiGazeProfile profile)
        {
            float peakSpeed = Mathf.Max(45f, profile.ProceduralTurnSpeed);
            return Mathf.Max(MinTurnSeconds, MinJerkPeakFactor * Mathf.Abs(angleDegrees) / peakSpeed);
        }

        private float RemainingAngle()
        {
            Transform facing = _facingReference != null ? _facingReference : _rotationRoot;
            Vector3 forward = facing.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) return 0f;
            return Vector3.SignedAngle(forward.normalized, _targetDirection, Vector3.up);
        }
    }
}
