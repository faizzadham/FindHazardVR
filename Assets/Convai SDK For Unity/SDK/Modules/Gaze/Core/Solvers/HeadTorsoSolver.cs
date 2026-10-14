using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Animation.ProceduralPose;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Solvers
{
    /// <summary>Per-frame input for <see cref="HeadTorsoSolver.Solve" />.</summary>
    internal struct HeadTorsoSolveInput
    {
        public GazeChainCalibration Chain;
        public ConvaiGazeProfile Profile;
        public float DeltaTime;

        /// <summary>
        ///     The character's shared pose compositor, when it has one. Every bone this solver
        ///     writes — chest, upper chest, neck, head — routes through it, so one guard owns
        ///     the restore protocol for the whole set. Null on a character with no Body
        ///     Language, where the solver falls back to its own private guard.
        /// </summary>
        public ProceduralPoseCompositor PoseSink;

        /// <summary>World point being gazed at (valid when <see cref="HasTarget" />).</summary>
        public Vector3 TargetPoint;
        public bool HasTarget;

        /// <summary>
        ///     This frame's shift requirement, measured once by
        ///     <c>GazeChainCalibration.TryMeasureShift</c>. Required whenever
        ///     <see cref="HasTarget" /> is set.
        /// </summary>
        public GazeShiftMeasurement Measurement;

        /// <summary>
        ///     This actuator's allocated share of the shift, from the actuator ladder. The
        ///     solver executes it; it no longer decides it.
        /// </summary>
        public GazeShiftPlan Plan;

        /// <summary>Effective engagement 0–1 (already includes commitment).</summary>
        public float Engagement;

        /// <summary>Ambient exploration angles (yaw/pitch degrees) when no target is engaged.</summary>
        public Vector2 AmbientAngles;
        public bool AmbientActive;

        /// <summary>
        ///     Whether idle life still has a fixation to hand over: the look is not yet fully
        ///     taken up (or is being released) and ambient exploration is enabled.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         While set, the head keeps holding its ambient fixation until it actually joins
        ///         the look (<c>Plan.HeadOnsetPending</c>), and takes the fixation back when it
        ///         stops taking part. False — the default, and what a fully committed look
        ///         supplies — leaves the allocated share untouched. The caller is responsible for
        ///         only setting it while idle life genuinely still holds a fixation to hand back
        ///         (<c>AmbientExplorationDirector.HasResumableFixation</c>): once the resume
        ///         window has cleared the angles, "hand the head back" would mean "face front".
        ///     </para>
        ///     <para>
        ///         It exists because the two sources of the head's share are chosen by a boolean
        ///         that flips a whole frame before the ladder has any share to hand over: on the
        ///         first frame of a look the head's onset has not elapsed, so the allocated share
        ///         is zero while the idle fixation the head was holding is dropped. The head
        ///         therefore returned to centre for the length of the onset gap and then turned
        ///         out to the target — one look executed as two movements in opposite directions,
        ///         which is what an idle curiosity glance did every time it fired.
        ///     </para>
        ///     <para>
        ///         Deliberately a gate and not a crossfade. Fading the two shares over the
        ///         commitment ramp removes the reversal but replaces it with something worse: the
        ///         goal becomes continuous, so the movement detector never fires, and a
        ///         continuous goal is <i>tracked</i> rather than shaped — the head then covers the
        ///         whole turn at the ramp's speed with no duration law and no velocity profile,
        ///         which reads far faster and harsher than the movement it replaced. The goal must
        ///         step exactly once, from the fixation to the share, and let the lane make a
        ///         movement out of it.
        ///     </para>
        /// </remarks>
        public bool AmbientHandover;

        /// <summary>Aversion beat offset (yaw/pitch degrees); the head carries half of it.</summary>
        public Vector2 AversionOffset;

        /// <summary>
        ///     Head gesture offset (yaw/pitch degrees, pitch-dominant) — e.g. listening
        ///     backchannel nods. Layered onto the bones AFTER the smoothing springs: the
        ///     envelope is authored motion with its own attack/decay, and chasing it through
        ///     the spring (or the stability band) would low-pass a sub-second nod to mush.
        ///     Producers must emit a continuous signal (the springs no longer smooth it).
        /// </summary>
        public Vector2 GestureOffset;

        /// <summary>
        ///     Additive head-gesture roll (tilt axis) in degrees — e.g. an external head-tilt
        ///     program. Gesture-only: there is no aim roll to compose against (the target-aim
        ///     path never produces roll), so this lands on the Head bone alone, soft-clamped
        ///     against a conservative internal limit (see <see cref="HeadTorsoSolver" />).
        ///     Zero by default; a zero value must be a complete no-op (see the solver's
        ///     bit-identity remarks).
        /// </summary>
        public float GestureRollDegrees;

        /// <summary>
        ///     True while a body reorientation (animated or procedural) is in flight: the
        ///     head/torso offsets are relieved so the neck rides the turn instead of staying
        ///     pinned at its limit.
        /// </summary>
        public bool BodyTurnActive;

        /// <summary>
        ///     What kind of movement this is, which decides how long it takes. Defaults to
        ///     <see cref="GazeMovementUrgency.Relaxed" /> because that is the zero value and idle
        ///     life is the case with no target to classify — a caller that never sets this gets
        ///     unhurried movement rather than an alert character, which is the safer default to
        ///     be wrong in.
        /// </summary>
        public GazeMovementUrgency Urgency;
    }

    /// <summary>
    ///     Anatomical head/neck/torso stage of the gaze chain: executes the share of a gaze
    ///     shift the actuator ladder allocated to it, cancels the animation's own head
    ///     deviation, smooths under an angular speed clamp, and layers the result on top of
    ///     the Animator's pose as world-axis swing deltas.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An actuator, not a decision-maker. How much of the shift the head takes, and
    ///         when it starts taking it, belong to <see cref="Shift.GazeShiftDirector" /> and
    ///         <see cref="Shift.GazeActuatorLadder" />. This type used to decide both for
    ///         itself, from its own recruitment threshold and its own latency timer, while the
    ///         eye solver and the body-turn director decided theirs — and nothing checked the
    ///         three answers added up to one shift.
    ///     </para>
    ///     <para>
    ///         Because the deltas are recomputed from the animated pose every frame, idle
    ///         animation personality (head bobs, weight shifts) survives under the gaze and no
    ///         bone-ownership tracking is needed: when the applied angles reach zero the solver
    ///         simply stops writing.
    ///     </para>
    /// </remarks>
    internal sealed class HeadTorsoSolver
    {
        /// <summary>
        ///     Settle time (seconds) of the body-turn relief blend. The relief factor eases in
        ///     when a turn starts and back out when it completes — a binary switch here stepped
        ///     the head goal the instant a turn ended and read as a whip.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Second order, and slower than it was.</b> Easing the factor exponentially
        ///         removed the step in the goal's POSITION and left one in its VELOCITY: an
        ///         exponential leaves at its peak rate, so the instant a turn began, the relief
        ///         factor acquired its full slew in one frame. That factor multiplies the head's
        ///         whole allocated share, so on a 45° look the goal picked up about 160 °/s of
        ///         velocity from a standing start. The tracking lane is transparent to in-budget
        ///         motion and therefore reproduces whatever the goal does, so it chased that with
        ///         everything it had: measured at a clean ±1500 °/s² — the acceleration envelope,
        ///         exactly — ramping to a 212 °/s peak and braking again. Bang-bang, which is the
        ///         harshest motion the caps allow and the precise shape this module's two-lane
        ///         actuator exists to never produce.
        ///     </para>
        ///     <para>
        ///         So the blend is critically damped (leaves and arrives at rest, no velocity step
        ///         at either end) and its timescale is comparable to the duration law's own base
        ///         rather than a third of it. Relief moves the head by <c>share × (1 - relief)</c>
        ///         — 27° on that same 45° look — and a movement that size is not something a neck
        ///         does in a fifth of a second. Doing it faster than <c>HeadTurnBaseSeconds</c>
        ///         meant the relief blend was quietly bypassing the duration law for one of the
        ///         largest head movements the module makes.
        ///     </para>
        /// </remarks>
        private const float ReliefSettleSeconds = 0.45f;

        /// <summary>
        ///     Conservative internal roll limit (degrees) for the gesture-only tilt axis. Roll
        ///     never comes from the aim solve (there is no "roll toward a target"), only from a
        ///     head-gesture producer, so this is a hardcoded internal cap rather than a profile
        ///     field — a tilt program is expected to stay well inside it.
        /// </summary>
        private const float MaxGestureRollDegrees = 10f;

        /// <summary>
        ///     Duration multiplier for a movement the character did not choose to make — a startle
        ///     beat, or re-acquiring a target after a cut. Not a profile field: a reflex is a
        ///     reflex, and a character whose startle response is authored to be leisurely is
        ///     describing a different thing than this scale is for.
        /// </summary>
        private const float UrgentTempoScale = 0.75f;

        /// <summary>
        ///     How much further the allocated share must jump to interrupt a movement that is
        ///     already running, as a multiple of the profile's trigger. See DetectMovement.
        /// </summary>
        private const float RetriggerHysteresis = 3f;

        // ---- Following ------------------------------------------------------------------
        //
        // The lanes know two verbs: a shift (step goal → shaped movement) and following (the
        // goal is already moving and the head goes with it). The following lane used to be a
        // dead-band latch on the goal in front of a transparent rate limiter, with a pursuit
        // classifier deciding when the latch stood aside. Measured on the real head bone under a
        // moving camera, that was the whole "stepped" feel: at 4 °/s the head parked for a
        // dozen frames at a time and caught up in 10 °/s bursts (a dead band is stick–slip by
        // definition, and every release was a velocity step the limiter executed at its
        // acceleration cap); at 55 °/s it stood still for the classifier's 0.12 s and then
        // overshot the target's rate by a quarter closing the band gap; at 1 Hz it reproduced a
        // hand-shaken camera with unit gain, because a transparent filter has no bandwidth and
        // the only thing left shaping the motion was the cap, which is bang-bang.
        //
        // So the following lane is now a PursuitTracker: critically damped, with a bounded
        // velocity lead, and a response time that belongs to the body (the profile's
        // HeadFollowSeconds) rather than to the signal. There is no dead band, no engage
        // latency and no catch-up, because each of those is a discontinuity in the goal by
        // another name. What the tracker needs from this stage is the goal's own velocity, which
        // the movement detector already estimates.

        /// <summary>Response rate (per second) of the goal-velocity estimate (~0.05 s).</summary>
        private const float GoalVelocitySharpness = 20f;

        /// <summary>
        ///     Response rate (per second) of the stabilization reflex's gain — about a fifth of a
        ///     second. Fast enough that engaging and disengaging still read as decisions, slow
        ///     enough that a policy value stepping can never arrive as a pose step.
        /// </summary>
        private const float StabilizationGainSharpness = 5f;

        /// <summary>
        ///     Overlapping action, as a redistribution of the neck/head split rather than an
        ///     offset on either bone.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A real neck does not rotate as one rigid piece: a turn travels up it, so the
        ///         neck leads, the head trails, and when the movement stops the head carries on a
        ///         degree or two and settles back. Its absence is the most recognisable tell of
        ///         procedural head motion — smooth, correctly timed, and unmistakably a machine.
        ///     </para>
        ///     <para>
        ///         The trick that makes this safe is where it is applied. Adding a lag offset to
        ///         the head bone would change where the character is looking, and the whole point
        ///         of the actuator ladder is that the contributions reconstruct the allocated
        ///         look exactly. Moving the SPLIT instead leaves the composed aim swing
        ///         bit-identical and only changes how it is divided between the two bones, so
        ///         conservation is untouched by construction and there is nothing to keep in sync.
        ///     </para>
        ///     <para>
        ///         Driven by the aim's own speed through an under-damped second order system, so
        ///         the same mechanism produces both halves of the effect: it builds while the head
        ///         is moving (neck leads) and rings down once it stops (head overshoots, then
        ///         settles). Two features, one signal, no phase bookkeeping.
        ///     </para>
        /// </remarks>
        private static class ChainLag
        {
            /// <summary>
            ///     Largest share redistribution at full follow-through, in share units. Was 0.15:
            ///     a contributor trace of a three-character conversation showed the chain-lag term
            ///     as the dominant source of fast frames in Attending and Thinking — the neck
            ///     leading and the head ringing down after every ordinary look — so the effect is
            ///     kept, smaller.
            /// </summary>
            public const float MaxShare = 0.1f;

            /// <summary>
            ///     Aim speed (deg/s) that drives the redistribution to its maximum. Calibrated
            ///     against the speeds conversational movement actually reaches — roughly 55 °/s
            ///     for a 20° look and 85 °/s for a 40° one — so an ordinary turn produces a
            ///     visible amount of flex rather than a fraction of one. A reference set for
            ///     maximal-effort movement would leave the whole effect dormant in normal use.
            /// </summary>
            public const float ReferenceSpeed = 90f;

            /// <summary>Undamped natural frequency (rad/s) — sets the settle's period.</summary>
            public const float Frequency = 18f;

            /// <summary>
            ///     Damping ratio. Below 1 so the settle overshoots once rather than creeping; 0.6
            ///     rather than the 0.35 it shipped with, which rang twice at a visible amplitude
            ///     and read as a wobble after every look rather than as a settle.
            /// </summary>
            public const float DampingRatio = 0.6f;

            public const float Stiffness = Frequency * Frequency;
            public const float Damping = 2f * DampingRatio * Frequency;

            /// <summary>Bounds on the redistribution, so the split can never invert or saturate.</summary>
            public const float MinClamp = -0.2f;
            public const float MaxClamp = 0.35f;
        }

        /// <summary>
        ///     Acceleration ceilings for the head and chest chains — a safety envelope, not a feel
        ///     setting.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         These are deliberately NOT profile fields, and that is a change of role rather
        ///         than an oversight. They used to be the thing that shaped a movement, because a
        ///         rate limiter running against its caps IS the movement — which made them
        ///         feel-critical constants that nobody could author. Now the shape comes from the
        ///         duration law on the profile and these only stop a movement that was planned
        ///         impossibly (a goal that jumped a long way with almost no time left) from
        ///         producing a rate no body could make.
        ///     </para>
        ///     <para>
        ///         At the shipped durations they are not reached. They CAN be, by authoring a turn
        ///         time far shorter than the movement's size warrants — the profile's floor of
        ///         0.05 s asks for accelerations two orders of magnitude past these — in which case
        ///         the movement degrades back toward the time-optimal shape this rework exists to
        ///         remove. <see cref="BallisticMotor.EnvelopeEngaged" /> reports the frame it
        ///         happens; nothing acts on it at runtime, and it is asserted in the primitive's
        ///         own tests rather than surfaced as a diagnostic.
        ///     </para>
        /// </remarks>
        private static class SafetyEnvelope
        {
            public const float HeadMaxAccel = 1500f;
            public const float TorsoMaxAccel = 600f;
        }

        private readonly AnimatedPoseWriteGuard _writeGuard = new();
        private ProceduralPoseCompositor _poseSink;

        // The two lanes. A channel is driven by exactly one of them at a time: the ballistic lane
        // while a movement is in flight, the pursuit tracker the rest of the time. See ExecuteLane.
        private BallisticMotor _headYawShift;
        private BallisticMotor _headPitchShift;
        private BallisticMotor _torsoYawShift;
        private BallisticMotor _torsoPitchShift;
        private PursuitTracker _headYawTracker;
        private PursuitTracker _headPitchTracker;
        private PursuitTracker _torsoYawTracker;
        private PursuitTracker _torsoPitchTracker;

        // Where each chain's movement in flight began, so a goal that keeps moving away can
        // lengthen the movement to what the law gives its total length. See ExecuteHeadLane.
        private Vector2 _headShiftStart;
        private Vector2 _torsoShiftStart;

        // The safety envelope on the COMPOSED head output. The lanes bound their own motion, but
        // the stabilization reflex, the dressings and the chain-lag split are composed after
        // them and were measured driving the head bone to two and four times the acceleration
        // cap. A cap that only some contributors respect is not a cap.
        //
        // A MotorFilter, because an envelope needs to BRAKE: a plain rate/acceleration clamp on
        // the delta has no notion of the distance left and, once engaged, overshoots a still
        // target and hunts around it at the acceleration cap (measured as a 0 → 99° → 0 cycle
        // on a 54° goal under the irregular frame times of a batch-mode test). The filter's
        // trapezoidal braking is exactly the missing term, and it is transparent to in-budget
        // motion, which is all the lanes ever hand it.
        private MotorFilter _composedYaw;
        private MotorFilter _composedPitch;
        private bool _hasComposed;

        // Last frame's allocated share, per chain — the signal a new movement is detected on. It
        // is deliberately the RAW share, before relief, the stability band and the stabilization
        // reflex: those are all continuous corrections to a movement, and letting them reach the
        // detector would have the animation's own head-bob restart the movement every frame.
        private Vector2 _lastHeadShare;
        private Vector2 _lastTorsoShare;
        private bool _hasLastShare;
        private bool _headMovedLastFrame;
        private bool _torsoMovedLastFrame;

        // The goal's own measured velocity per chain (deg/s, lightly smoothed). It feeds the
        // pursuit trackers' lead and the ballistic lane's landing, and it makes movement
        // detection frame-rate independent: a goal already in motion moves every frame, and
        // only the part of a frame's change the recent velocity does not explain is evidence of
        // a decision.
        private Vector2 _headGoalVelocity;
        private Vector2 _torsoGoalVelocity;

        // Overlapping action: how far the neck/head split is currently displaced from the
        // authored one, and the second-order state that carries it. See ChainLag.
        private float _chainLag;
        private float _chainLagVelocity;
        private Vector2 _previousAim;
        private bool _hasPreviousAim;

        // The animated-deviation reflex's two factors, both of which must be continuous: an eased
        // gain, and the last deviation actually measured, held so it can be faded out rather than
        // dropped when the measurement stops being available.
        private float _stabilizationGain;
        private Vector2 _lastAnimatedDeviation;

        // The animation's own head deviation, sampled once per frame at the only moment it is
        // unambiguous. See AnimatedHeadAngles.
        private Vector2 _animatedHeadAngles;

        private float _headYaw;
        private float _headPitch;
        private float _torsoYaw;
        private float _torsoPitch;
        private float _reliefBlend = 1f;
        private float _reliefVelocity;
        private float _appliedHeadYaw;
        private float _appliedHeadPitch;
        private float _appliedHeadRoll;

        // Contributor-trace cache: the last stabilization reflex and chain-lag terms this solver
        // computed, exposed read-only for diagnostics.
        private Vector2 _lastStabilizationOffset;
        private Vector2 _lastChainLagOffset;

        /// <summary>Head-chain contribution (degrees) actually written this frame: spring-smoothed aim + gesture channel.</summary>
        public Vector2 HeadAngles => new(_appliedHeadYaw, _appliedHeadPitch);

        /// <summary>
        ///     How far the ANIMATION has the head off its neutral this frame (yaw/pitch degrees,
        ///     in the same frame the shift is measured in) — the idle clip's own look-around,
        ///     before gaze has touched the bones.
        /// </summary>
        /// <remarks>
        ///     Published because the eye stage cannot work it out for itself: by the time it runs,
        ///     where the head points is the animation's deviation and gaze's own aim added
        ///     together, and telling an idle look apart from the clip that carried the head into
        ///     it is exactly what the eyes need to do (see <c>EyeSolveInput.AnimatedHeadAngles</c>).
        ///     Zero on a rig no animation source is posing.
        /// </remarks>
        public Vector2 AnimatedHeadAngles => _animatedHeadAngles;

        /// <summary>
        ///     True while the head is executing a shaped movement to a new goal — the ballistic
        ///     lane is in flight on either axis. False while holding, tracking, or at rest.
        /// </summary>
        public bool IsShifting => _headYawShift.IsActive || _headPitchShift.IsActive;

        /// <summary>Applied gesture roll (degrees) written to the Head bone this frame; zero outside an active roll gesture.</summary>
        public float HeadRollDegrees => _appliedHeadRoll;

        /// <summary>
        ///     The stabilization reflex's last applied offset (degrees) — the eased-gain
        ///     cancellation of the animation's own head deviation. Diagnostics only; already
        ///     folded into <see cref="HeadAngles" />.
        /// </summary>
        public Vector2 StabilizationOffset => _lastStabilizationOffset;

        /// <summary>
        ///     Approximate degrees the neck/head chain-lag redistribution (see
        ///     <see cref="ChainLag" />) shifted onto the head bone last frame. Diagnostics only —
        ///     the redistribution itself moves the SPLIT of an unchanged swing, not the composed
        ///     aim, so this is an estimate for measurement rather than a value fed back into the
        ///     solve.
        /// </summary>
        public Vector2 ChainLagOffset => _lastChainLagOffset;

        /// <summary>Solved torso contribution (degrees) after smoothing.</summary>
        public Vector2 TorsoAngles => new(_torsoYaw, _torsoPitch);

        /// <summary>
        ///     Total yaw error (degrees, signed) between the root forward and the target
        ///     this frame — the reorientation director's input.
        /// </summary>
        public float TargetYawError { get; private set; }

        /// <summary>
        ///     Total pitch error (degrees, signed) from the eye line to the target this frame.
        ///     The reorientation director has no use for it (feet only turn in yaw); it exists
        ///     so the coordination invariant — eyes + head + torso + feet sum to the required
        ///     shift — is measurable on both axes rather than only the one the body turns on.
        /// </summary>
        public float TargetPitchError { get; private set; }

        public void Reset()
        {
            // Unwind a still-applied write so a disabled gaze leaves the pose as found.
            _writeGuard.RestoreStaleWrites();
            _headYaw = 0f;
            _headPitch = 0f;
            _torsoYaw = 0f;
            _torsoPitch = 0f;
            _headYawShift.Reset();
            _headPitchShift.Reset();
            _torsoYawShift.Reset();
            _torsoPitchShift.Reset();
            _headYawTracker.Reset();
            _headPitchTracker.Reset();
            _torsoYawTracker.Reset();
            _torsoPitchTracker.Reset();
            _composedYaw.Reset();
            _composedPitch.Reset();
            _hasComposed = false;
            _lastHeadShare = Vector2.zero;
            _lastTorsoShare = Vector2.zero;
            _hasLastShare = false;
            _headGoalVelocity = Vector2.zero;
            _torsoGoalVelocity = Vector2.zero;
            _chainLag = 0f;
            _chainLagVelocity = 0f;
            _previousAim = Vector2.zero;
            _hasPreviousAim = false;
            _stabilizationGain = 0f;
            _lastAnimatedDeviation = Vector2.zero;
            _animatedHeadAngles = Vector2.zero;
            _reliefBlend = 1f;
            _reliefVelocity = 0f;
            _headShiftStart = Vector2.zero;
            _torsoShiftStart = Vector2.zero;
            _headMovedLastFrame = false;
            _torsoMovedLastFrame = false;
            _appliedHeadYaw = 0f;
            _appliedHeadPitch = 0f;
            _appliedHeadRoll = 0f;
            _lastStabilizationOffset = Vector2.zero;
            _lastChainLagOffset = Vector2.zero;
            TargetYawError = 0f;
            TargetPitchError = 0f;
        }

        public void Solve(in HeadTorsoSolveInput input)
        {
            // Stashed for Apply (below), which has no access to this frame's input struct.
            _poseSink = input.PoseSink;

            // With no animation source re-posing the skeleton this frame (Body Animation
            // disabled, Animator without a controller), last frame's swing deltas are
            // still on the bones — unwind them first so goals are computed against the
            // true underlying pose and the deltas never integrate into a runaway spin.
            _writeGuard.RestoreStaleWrites();

            GazeChainCalibration chain = input.Chain;
            ConvaiGazeProfile profile = input.Profile;
            if (chain == null || profile == null || !chain.HasHeadChain || chain.Root == null) return;

            // What the ANIMATION alone is doing to the head this frame. Measured here and
            // nowhere else: this is the one point in the frame where the bones carry the
            // animated pose and nothing of gaze's own — the unwind above has just happened and
            // this frame's swing has not been written yet. Read a line later, or from the eye
            // stage, and the answer includes whatever gaze itself put on the head.
            MeasureAnimatedHeadAngles(chain);

            float dt = input.DeltaTime > 0f ? input.DeltaTime : 1f / 60f;

            float reliefGoal = input.BodyTurnActive ? Mathf.Clamp01(profile.BodyTurnHeadRelief) : 1f;
            _reliefBlend = Mathf.SmoothDamp(
                _reliefBlend, reliefGoal, ref _reliefVelocity, ReliefSettleSeconds, Mathf.Infinity, dt);

            // ---- Decide the movement, then execute it. ----
            //
            // These are two different questions and they used to be one. The share this actuator
            // owns is decided upstream by the ladder; what is decided HERE is whether that share
            // changing constitutes a new movement, and if so what shape and duration that movement
            // has. Conflating them is what let a step in the goal become a step in the output.
            ComputeShares(in input, profile, out Vector2 headShare, out Vector2 torsoShare);

            // Body-turn relief scales what the lanes aim at, but never what the movement detector
            // reads: relief eases continuously, and a movement must not be restarted by the neck
            // relaxing into a turn it is already riding.
            Vector2 headTracked = headShare * _reliefBlend;
            Vector2 torsoTracked = torsoShare * _reliefBlend;

            DetectMovement(headShare, torsoShare, profile, dt, out bool headMoved, out bool torsoMoved);
            float tempo = TempoScale(input.Urgency, profile);

            ExecuteHeadLane(headTracked, headMoved, tempo, profile, dt);
            ExecuteTorsoLane(torsoTracked, torsoMoved, tempo, profile, dt);

            // Overlapping action, driven by the movement the lanes just produced — not by the
            // dressings below it. A nod is authored motion that already has its own shape; using
            // it to drive the chain lag would ring the neck against a signal that is not a turn.
            UpdateChainLag(new Vector2(_headYaw, _headPitch), profile, dt);

            // ---- Everything that is not the movement, composed on top of it. ----
            //
            // Three channels layer onto the executed aim rather than being routed through it,
            // each for its own reason:
            //
            //  · the stabilization reflex, because a reflex that lags the animation it cancels
            //    leaves exactly the residual bow it exists to remove — and because feeding the
            //    animated head-bob into the lanes would restart the movement every frame;
            //  · the aversion beat, because a look-away is a dressing on the shift with its own
            //    envelope, not a bigger shift (routing it through the lanes would also let a
            //    glance recruit a full movement);
            //  · the gesture channel (backchannel nods), because a 0.7 s double-bob is authored
            //    motion whose frequency content sits right where a filter attenuates hardest.
            //
            // Relief scales the voluntary dressings but never the reflex — see ComputeShares.
            // The reflex is immediate; how much of it applies is not.
            //
            // The reflex itself must never lag — it cancels the animation's own head movement, and
            // a lagging canceller leaves exactly the residual bow it exists to remove. That is why
            // it is composed here, outside both lanes. But its GAIN is a policy value, and policy
            // values step: engagement is pinned to 1 by the floor-yield beat and floored at 0.6 by
            // the target-loss search, both on boolean edges, and it moves whenever the dialogue
            // state does. A step in the gain is indistinguishable from a step in the pose — the
            // head jumps by the animated deviation times the change, with nothing in the way,
            // because this term is downstream of everything that shapes motion.
            //
            // So the gain is eased and the reflex is not. Tracking the animation stays frame-exact
            // while the amount of it that reaches the pose can only ever ramp.
            float stabilizationTarget =
                Mathf.Clamp01(input.Engagement) * Mathf.Clamp01(profile.HeadStabilization);
            _stabilizationGain += (stabilizationTarget - _stabilizationGain) *
                                  (1f - Mathf.Exp(-StabilizationGainSharpness * dt));

            // The reflex has two factors and BOTH have to be continuous — easing one and letting
            // the other step just moves the step.
            //
            // The deviation is only measurable while there is a target: the controller hands over
            // a cleared measurement the frame gaze disengages. Read straight, that zeroes the
            // reflex in one frame while the gain is still near 1, and the head — which was being
            // held level against the animation's bow — drops onto that bow instantly. On a talking
            // clip that is a dozen degrees, on every single release.
            //
            // So the last measured deviation is held and faded out by the gain instead. While
            // engaged it is refreshed every frame, which keeps the reflex frame-exact, the whole
            // reason it sits outside the lanes. While disengaging it is stale — and stale is
            // exactly right for a value whose only remaining job is to reach zero smoothly.
            if (input.Measurement.IsValid)
                _lastAnimatedDeviation =
                    new Vector2(input.Measurement.AnimatedYaw, input.Measurement.AnimatedPitch);

            float reflexYaw = -_lastAnimatedDeviation.x * _stabilizationGain;
            float reflexPitch = -_lastAnimatedDeviation.y * _stabilizationGain;
            _lastStabilizationOffset = new Vector2(reflexYaw, reflexPitch);
            // The chain-lag redistribution moves the neck/head SPLIT of the (pre-dressing) head
            // aim by its share; approximating the degrees that shifts onto the head bone as that
            // share times the pre-dressing aim is exact for the small-angle regime this reaches.
            _lastChainLagOffset = new Vector2(_headYaw, _headPitch) * _chainLag;

            float dressingYaw = (input.AversionOffset.x * 0.5f + input.GestureOffset.x) * _reliefBlend;
            float dressingPitch = (input.AversionOffset.y * 0.5f + input.GestureOffset.y) * _reliefBlend;

            // The range limit is a limit on where the HEAD ends up, not on how much gaze adds to
            // the animation. Clamping gaze's own delta let the animation's head turn through
            // whenever gaze was near the limit: with the head held at 55° on somebody to the
            // side, the idle clip's 25° turn the other way needs an 80° delta to cancel, the
            // clamp allowed 55, and the face swung 25° off the target and back with the clip —
            // measured as the head "bouncing off its limit" a beat after every big look. So the
            // clamp is applied to the total the bone will carry (the animated deviation plus
            // everything gaze composes on it) and the animated part is taken back out. Only
            // while the reflex is live: with the reflex off, gaze composes nothing worth
            // clamping and the animation is left exactly as authored.
            float animatedYaw = _stabilizationGain > 0.01f ? _lastAnimatedDeviation.x : 0f;
            float animatedPitch = _stabilizationGain > 0.01f ? _lastAnimatedDeviation.y : 0f;
            float composedYaw = GazeSolverMath.SoftClamp(
                _headYaw + dressingYaw + reflexYaw + animatedYaw, profile.MaxHeadYawDegrees, 0.85f) - animatedYaw;
            float composedPitch = GazeSolverMath.SoftClamp(
                _headPitch + dressingPitch + reflexPitch + animatedPitch, profile.MaxHeadPitchDegrees, 0.85f) - animatedPitch;

            // The envelope, on what actually reaches the bone. Transparent to everything the
            // lanes and the eased dressings produce — the shipped durations peak well inside it
            // — and engaged only by a contributor that stepped: a reflex re-armed on a clip cut,
            // a landing that had to snap. Those are precisely the frames that used to read as a
            // flick, and a cap that lets them through is decoration.
            if (!_hasComposed)
            {
                _hasComposed = true;
                _composedYaw.Seed(composedYaw);
                _composedPitch.Seed(composedPitch);
                _appliedHeadYaw = composedYaw;
                _appliedHeadPitch = composedPitch;
            }
            else
            {
                float headSpeedLimit = Mathf.Max(1f, profile.MaxHeadAngularSpeed);
                _appliedHeadYaw = _composedYaw.Step(composedYaw, headSpeedLimit, SafetyEnvelope.HeadMaxAccel, dt);
                _appliedHeadPitch = _composedPitch.Step(composedPitch, headSpeedLimit, SafetyEnvelope.HeadMaxAccel, dt);
            }

            // Braking under an acceleration cap can carry the filtered output beyond its
            // bounded target. The anatomical limit applies to the final composed pose too.
            _appliedHeadYaw = ClampComposedAngle(
                ref _composedYaw, _appliedHeadYaw, animatedYaw, profile.MaxHeadYawDegrees);
            _appliedHeadPitch = ClampComposedAngle(
                ref _composedPitch, _appliedHeadPitch, animatedPitch, profile.MaxHeadPitchDegrees);

            // Gesture roll (tilt axis): gesture-only, so there is nothing to add it to besides
            // the relief blend — no aim-roll goal exists to spring toward. Branched on non-zero
            // so the zero-gesture path (today's only path) never executes this line at all,
            // which is the bit-identity guarantee: a literal `0f` input can never observably
            // differ from the assignment below evaluating to exactly 0f, but keeping the whole
            // computation out of the executed path removes any doubt and matches the yaw/pitch
            // gesture channel's own convention of only doing work when there is a signal.
            if (input.GestureRollDegrees != 0f)
                _appliedHeadRoll = GazeSolverMath.SoftClamp(
                    input.GestureRollDegrees * _reliefBlend, MaxGestureRollDegrees, 0.85f);
            else
                _appliedHeadRoll = 0f;

            Apply(chain, profile);
        }

        private static float ClampComposedAngle(ref MotorFilter motor, float angle, float animatedAngle, float limit)
        {
            float total = angle + animatedAngle;
            if (total >= -limit && total <= limit)
                return angle;
            float bounded = Mathf.Clamp(total, -limit, limit) - animatedAngle;
            motor.Seed(bounded);
            return bounded;
        }

        /// <summary>
        ///     This actuator's raw allocated share of the current look — what the body is being
        ///     asked to hold, before anything is done about how it gets there.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The share is decided by the actuator ladder (<c>GazeShiftDirector</c>), not
        ///         here. This solver used to compute its own recruitment ease, its own latency
        ///         window and its own torso overflow, while the body-turn director computed its
        ///         own threshold and hold from the same error — four opinions about one shift,
        ///         with nothing checking they added up.
        ///     </para>
        ///     <para>
        ///         Deliberately raw: no relief, no stability band, no stabilization reflex, no
        ///         aversion. Every one of those is a continuous correction applied to a movement,
        ///         and this value is what the movement DETECTOR reads — so folding any of them in
        ///         would have a body turn, a camera nudge or the animation's own head-bob read as
        ///         a decision to look somewhere else.
        ///     </para>
        /// </remarks>
        private void ComputeShares(
            in HeadTorsoSolveInput input,
            ConvaiGazeProfile profile,
            out Vector2 headShare,
            out Vector2 torsoShare)
        {
            headShare = Vector2.zero;
            torsoShare = Vector2.zero;
            TargetYawError = 0f;
            TargetPitchError = 0f;

            if (input.HasTarget && input.Engagement > 0.0001f)
            {
                if (!input.Measurement.IsValid) return;

                // Echoed for diagnostics only. The measurement itself is taken once, by the
                // chain calibration, and handed to every stage — see
                // GazeChainCalibration.TryMeasureShift.
                TargetYawError = input.Measurement.RequiredYaw;
                TargetPitchError = input.Measurement.RequiredPitch;

                headShare = new Vector2(input.Plan.HeadYaw, input.Plan.HeadPitch);
                torsoShare = new Vector2(input.Plan.TorsoYaw, input.Plan.TorsoPitch);

                // Hand-over from idle life: until the head joins the look, it goes on holding the
                // fixation it was already on rather than being dropped to a share the ladder has
                // not allocated yet. One step, at the moment the head takes part — see
                // AmbientHandover for why this is a gate and not a fade. The torso is not in it:
                // idle life never recruits the chest, so it has nothing to hand over.
                //
                // Keyed on the ONSET being pending, not on the allocated share being zero. The
                // two are not the same question: a shift below the head's entry angle allocates
                // nothing to the head forever, because the eyes own small looks — and reading
                // that as "the head has not joined yet" handed the head an idle fixation for the
                // length of every commitment dip in a settled conversation, then snapped it back.
                // See GazeShiftPlan.HeadOnsetPending.
                if (input.AmbientHandover && input.Plan.HeadOnsetPending)
                    headShare = AmbientHeadShare(profile, input.AmbientAngles);
                return;
            }

            // Idle life. The ambient director hands over a discrete fixation — it decides to
            // look somewhere, it does not slide there — and that is correct modelling: an
            // intention is not a ramp. Turning that decision into a movement is this stage's
            // job, which is exactly why the step arrives here rather than being smoothed away
            // at the source.
            if (input.AmbientActive)
                headShare = AmbientHeadShare(profile, input.AmbientAngles);

            // No target and no ambient → the share is zero, and returning to neutral is itself
            // a movement rather than a decay.
        }

        /// <summary>
        ///     How far off centre the eyes will REST, as a fraction of the range they can reach.
        ///     People saccade to the edge of the socket and then bring the head round; they do
        ///     not hold a look there. The head's share of an idle fixation rises to keep the
        ///     eyes inside this.
        /// </summary>
        private const float EyeRestingYawFraction = 0.55f;

        /// <summary>
        ///     The head's part of an ambient fixation: the profile's follow fraction of the
        ///     angles the exploration director is holding, raised where needed to keep the eyes
        ///     off their limit, and soft-clamped to the head's range.
        /// </summary>
        internal static Vector2 AmbientHeadShare(ConvaiGazeProfile profile, Vector2 ambientAngles)
        {
            float follow = Mathf.Clamp01(profile.AmbientHeadFollow);
            float yaw = ambientAngles.x * follow;

            // Whatever the follow fraction says, the head takes enough of a wide idle look to
            // keep the eyes inside the range a person actually holds. Eyes can reach the corner
            // of the socket, but they do not REST there: a gaze held near the mechanical limit
            // is the sideways stare, and no amount of tuning elsewhere hides it. The band is the
            // profile's own eye comfort — the same one an engaged look is held to by the ladder
            // — so an idle look and a look at somebody rest the eyes in the same place. It used
            // to be a private fraction of the eye range (19° at the shipped 35°), which is
            // exactly where a bystander's eyes sat between turns in a room of three.
            float comfortable = profile.EyeComfortDegrees > 0f
                ? profile.EyeComfortDegrees
                : Mathf.Max(0f, profile.EyeMaxYawDegrees) * EyeRestingYawFraction;
            float relief = Mathf.Abs(ambientAngles.x) - comfortable;
            if (relief > 0f)
                yaw = Mathf.Sign(ambientAngles.x) * Mathf.Max(Mathf.Abs(yaw), relief);

            return new Vector2(
                GazeSolverMath.SoftClamp(yaw, profile.MaxHeadYawDegrees, 0.85f),
                GazeSolverMath.SoftClamp(
                    ambientAngles.y * follow, profile.MaxHeadPitchDegrees, 0.85f));
        }

        /// <summary>
        ///     Whether this frame's share represents a decision to look somewhere else, as
        ///     opposed to the current look being adjusted.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         One decision, reported per rung. Both flags are read off the same ladder
        ///         allocation, so head and chest can never disagree about WHICH look is being
        ///         made — but they must be free to disagree about when their own part of it
        ///         starts, because the onset cascade is exactly that disagreement, deliberately
        ///         staged. A single shared flag would have the chest joining at its onset restart
        ///         the head's movement, which was already a third of the way through.
        ///     </para>
        ///     <para>
        ///         The first frame after a bind has no previous share to have moved away from, so
        ///         it is compared against the POSE instead: a share the head is already holding
        ///         initialises the channel, and a share it is not is a movement like any other.
        ///         The chest is exempt — nothing recruits it on a bind frame, because its own
        ///         onset has not elapsed and idle life never asks for it.
        ///     </para>
        /// </remarks>
        private void DetectMovement(
            Vector2 headShare,
            Vector2 torsoShare,
            ConvaiGazeProfile profile,
            float dt,
            out bool headMoved,
            out bool torsoMoved)
        {
            if (!_hasLastShare)
            {
                _lastHeadShare = headShare;
                _lastTorsoShare = torsoShare;
                _hasLastShare = true;
                _headGoalVelocity = Vector2.zero;
                _torsoGoalVelocity = Vector2.zero;
                torsoMoved = false;

                // The bind frame has no previous share, so the usual question — did the share
                // CHANGE — cannot be asked. The question that can be asked is the one that
                // actually matters: is this share where the head already is? A rig that binds
                // holding the look it is at the end of answers yes, and is initialised. A
                // character whose idle life has just dealt it a fixation two dozen degrees away
                // answers no, and arriving there is not the same thing as looking there: with
                // nothing to move away from, the tracking filter's un-seeded first Step put the
                // whole share on in one frame (measured: 10.2° of a 24° fixation, 613 deg/s).
                // A decision is a decision on the first frame as much as on the thousandth, so
                // it goes down the ballistic lane from the pose the animation left, like any
                // other.
                float bindTrigger = Mathf.Max(0.01f, profile.ShiftTriggerDegrees);
                Vector2 bindOffset = headShare - new Vector2(_headYaw, _headPitch);
                headMoved = bindOffset.sqrMagnitude > bindTrigger * bindTrigger;

                // Either way the trackers start where the head IS, at rest. A movement seeds
                // them again where it lands; a head that binds already on its share is simply
                // initialised there and the tracker's first step moves nothing. The fall-through
                // case — a movement whose duration law rounds to nothing — cannot snap either.
                _headYawTracker.Seed(_headYaw);
                _headPitchTracker.Seed(_headPitch);
                // The chest too: an un-seeded tracker's first step is a snap onto its goal, and
                // a target beyond the head's reach can hand the chest a share on the bind frame.
                _torsoYawTracker.Seed(_torsoYaw);
                _torsoPitchTracker.Seed(_torsoPitch);
                return;
            }

            float trigger = Mathf.Max(0.01f, profile.ShiftTriggerDegrees);

            // Hysteresis while a movement is already running. The share is measured in the
            // character-root frame, so anything that rotates the root — a body turn, most of all —
            // sweeps the share past the trigger every single frame. Re-arming on each of those
            // would restart the movement continuously: it never pops (the restart carries position
            // and velocity) but the movement's shape is thrown away and the lane degrades into a
            // plain lag tracker for the length of the turn, which is the one place the shape is
            // most visible. A genuinely new decision clears the wider bar easily; a root sweeping
            // underneath an existing one does not.
            float headTrigger = _headYawShift.IsActive ? trigger * RetriggerHysteresis : trigger;
            float torsoTrigger = _torsoYawShift.IsActive ? trigger * RetriggerHysteresis : trigger;

            // Measured against what the goal's own recent velocity predicts, not against zero.
            // A raw per-frame delta compared to a fixed trigger makes the classification frame-
            // rate dependent — the same deg/s target crosses a 2° trigger three times as easily
            // at 20 fps as at 60 — and reclassifies steady pursuit as a chain of movements the
            // moment the editor slows down. Subtracting the predicted motion leaves exactly the
            // part of the change that is a decision.
            Vector2 headDelta = headShare - _lastHeadShare;
            Vector2 torsoDelta = torsoShare - _lastTorsoShare;
            Vector2 headExcess = headDelta - _headGoalVelocity * dt;
            Vector2 torsoExcess = torsoDelta - _torsoGoalVelocity * dt;

            headMoved = headExcess.sqrMagnitude > headTrigger * headTrigger;
            torsoMoved = torsoExcess.sqrMagnitude > torsoTrigger * torsoTrigger;

            // A step is a decision, not goal velocity — folding it into the estimate would have
            // the frame after a re-target predict a sprinting goal and miss the follow-up.
            //
            // Speeding up is smoothed; slowing down is believed at once. The estimate feeds the
            // trackers' lead and the ballistic landing, and a goal that stops dead — a camera
            // flick arriving at the head's limit, where the share saturates and stops changing —
            // must not go on being followed at its old rate for the smoother's settling time:
            // that carried 125 °/s of lead into a goal that was standing still, and the head
            // and chest overshot the limit and rang against it.
            //
            // Except when the trigger fires on consecutive frames. One step is a decision; a
            // second one immediately behind it is a goal in sustained motion that the estimate
            // has not caught up with — a low trigger at a modest frame rate lets a fast steady
            // sweep clear even the in-flight hysteresis every frame, and an estimate that only
            // learns while nothing is triggering would then never learn it, restarting the
            // movement each frame and pinning it in its opening phase. So the second consecutive
            // trigger is folded in after all; the estimate converges within a few frames and the
            // trigger stops firing. A genuine second decision on the very next frame costs one
            // frame of inflated estimate, which the slowing rule discards on the frame after.
            bool headSustained = headMoved && _headMovedLastFrame;
            bool torsoSustained = torsoMoved && _torsoMovedLastFrame;
            if (!headMoved || headSustained)
                _headGoalVelocity = AdvanceGoalVelocity(_headGoalVelocity, headDelta / dt, dt);
            if (!torsoMoved || torsoSustained)
                _torsoGoalVelocity = AdvanceGoalVelocity(_torsoGoalVelocity, torsoDelta / dt, dt);
            _headMovedLastFrame = headMoved;
            _torsoMovedLastFrame = torsoMoved;

            _lastHeadShare = headShare;
            _lastTorsoShare = torsoShare;
        }

        /// <summary>
        ///     One frame of the goal-velocity estimate: smoothed while the goal speeds up, taken
        ///     straight while it slows down or reverses.
        /// </summary>
        private static Vector2 AdvanceGoalVelocity(Vector2 estimate, Vector2 raw, float dt)
        {
            float alpha = 1f - Mathf.Exp(-GoalVelocitySharpness * dt);
            return new Vector2(
                AdvanceGoalVelocityAxis(estimate.x, raw.x, alpha),
                AdvanceGoalVelocityAxis(estimate.y, raw.y, alpha));
        }

        private static float AdvanceGoalVelocityAxis(float estimate, float raw, float alpha)
        {
            bool slowing = Mathf.Abs(raw) < Mathf.Abs(estimate) || raw * estimate < 0f;
            return slowing ? raw : estimate + (raw - estimate) * alpha;
        }

        /// <summary>
        ///     Executes the head chain's share: ballistically while a movement is in flight,
        ///     under the pursuit tracker the rest of the time.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The two lanes exist because two different signals arrive on this channel. A
        ///         decision to look elsewhere arrives as a step and wants a movement — a duration
        ///         chosen from its size, and a bell-shaped velocity profile. A target drifting,
        ///         or the relief blend easing, arrives continuously and wants to be FOLLOWED: a
        ///         response with the body's own bandwidth, a small lag the eyes cover, and no
        ///         reproduction of the input's own jitter. One filter cannot serve both: a
        ///         tracker turns a step into an exponential with no duration law, and a rate
        ///         limiter turns it into the time-optimal trajectory, the harshest motion its
        ///         caps allow.
        ///     </para>
        ///     <para>
        ///         The hand-off is continuous by construction. A movement ends on its goal
        ///         matching the goal's own velocity, and the tracker is seeded with both; a
        ///         movement that begins mid-track takes the tracker's current velocity with it,
        ///         so a new decision taken while the head is already moving does not stall it
        ///         first. Both directions matter — the second is what a re-target during a turn
        ///         hits every time, and the first is what a re-target during a sweep used to
        ///         stall on.
        ///     </para>
        /// </remarks>
        private void ExecuteHeadLane(
            Vector2 tracked, bool startMovement, float tempo, ConvaiGazeProfile profile, float dt)
        {
            float speed = Mathf.Max(1f, profile.MaxHeadAngularSpeed);
            float skew = Mathf.Clamp01(profile.MovementSkew);

            if (startMovement)
            {
                float distance = Vector2.Distance(new Vector2(_headYaw, _headPitch), tracked);
                float duration = MovementSeconds(
                    distance, profile.HeadTurnBaseSeconds, profile.HeadTurnSecondsPerDegree, tempo);
                if (duration > 0f)
                {
                    // Read before Begin: this is the velocity the channel is carrying INTO the
                    // new movement, and Begin overwrites it.
                    float yawVelocity = LaneVelocity(in _headYawShift, in _headYawTracker);
                    float pitchVelocity = LaneVelocity(in _headPitchShift, in _headPitchTracker);
                    _headYawShift.Begin(_headYaw, yawVelocity, duration);
                    _headPitchShift.Begin(_headPitch, pitchVelocity, duration);
                    _headShiftStart = new Vector2(_headYaw, _headPitch);
                }
            }

            if (_headYawShift.IsActive)
            {
                // A goal that keeps moving away lengthens the movement to what the law gives its
                // total length, so the head never sprints to a short appointment with a far goal.
                float total = MovementSeconds(
                    Vector2.Distance(_headShiftStart, tracked),
                    profile.HeadTurnBaseSeconds, profile.HeadTurnSecondsPerDegree, tempo);
                _headYawShift.Extend(total);
                _headPitchShift.Extend(total);

                _headYaw = _headYawShift.Step(
                    tracked.x, speed, SafetyEnvelope.HeadMaxAccel, skew, dt, _headGoalVelocity.x);
                _headPitch = _headPitchShift.Step(
                    tracked.y, speed, SafetyEnvelope.HeadMaxAccel, skew, dt, _headGoalVelocity.y);

                // Both channels share one duration, so they land on the same frame; the yaw
                // channel speaks for the pair.
                if (!_headYawShift.IsActive) HandOffHeadToTracking();
                return;
            }

            float response = profile.HeadFollowSeconds;
            _headYaw = _headYawTracker.Step(
                tracked.x, _headGoalVelocity.x, response, speed, SafetyEnvelope.HeadMaxAccel, dt);
            _headPitch = _headPitchTracker.Step(
                tracked.y, _headGoalVelocity.y, response, speed, SafetyEnvelope.HeadMaxAccel, dt);
        }

        /// <summary>
        ///     Executes the chest's share. Same two lanes as the head, with its own duration law
        ///     and its own response time: a chest is heavier than a head and a chest that keeps
        ///     up with one reads wrong.
        /// </summary>
        private void ExecuteTorsoLane(
            Vector2 tracked, bool startMovement, float tempo, ConvaiGazeProfile profile, float dt)
        {
            float speed = Mathf.Max(1f, profile.MaxTorsoAngularSpeed);
            float skew = Mathf.Clamp01(profile.MovementSkew);

            if (startMovement)
            {
                float distance = Vector2.Distance(new Vector2(_torsoYaw, _torsoPitch), tracked);
                float duration = MovementSeconds(
                    distance, profile.TorsoTurnBaseSeconds, profile.TorsoTurnSecondsPerDegree, tempo);
                if (duration > 0f)
                {
                    float yawVelocity = LaneVelocity(in _torsoYawShift, in _torsoYawTracker);
                    float pitchVelocity = LaneVelocity(in _torsoPitchShift, in _torsoPitchTracker);
                    _torsoYawShift.Begin(_torsoYaw, yawVelocity, duration);
                    _torsoPitchShift.Begin(_torsoPitch, pitchVelocity, duration);
                    _torsoShiftStart = new Vector2(_torsoYaw, _torsoPitch);
                }
            }

            if (_torsoYawShift.IsActive)
            {
                float total = MovementSeconds(
                    Vector2.Distance(_torsoShiftStart, tracked),
                    profile.TorsoTurnBaseSeconds, profile.TorsoTurnSecondsPerDegree, tempo);
                _torsoYawShift.Extend(total);
                _torsoPitchShift.Extend(total);

                _torsoYaw = _torsoYawShift.Step(
                    tracked.x, speed, SafetyEnvelope.TorsoMaxAccel, skew, dt, _torsoGoalVelocity.x);
                _torsoPitch = _torsoPitchShift.Step(
                    tracked.y, speed, SafetyEnvelope.TorsoMaxAccel, skew, dt, _torsoGoalVelocity.y);

                if (!_torsoYawShift.IsActive)
                {
                    _torsoYawTracker.Seed(_torsoYaw, _torsoYawShift.Velocity);
                    _torsoPitchTracker.Seed(_torsoPitch, _torsoPitchShift.Velocity);
                }

                return;
            }

            float response = profile.TorsoFollowSeconds;
            _torsoYaw = _torsoYawTracker.Step(
                tracked.x, _torsoGoalVelocity.x, response, speed, SafetyEnvelope.TorsoMaxAccel, dt);
            _torsoPitch = _torsoPitchTracker.Step(
                tracked.y, _torsoGoalVelocity.y, response, speed, SafetyEnvelope.TorsoMaxAccel, dt);
        }

        /// <summary>
        ///     Samples <see cref="AnimatedHeadAngles" /> from the pose the bones are currently in.
        ///     Decomposed in the calibrated gaze frame where the rig has one, which is the frame
        ///     every other angle in this module is expressed in — the eye stage subtracts this
        ///     from its own rest measurement, so the two must be measured the same way or the
        ///     difference carries a standing bias.
        /// </summary>
        private void MeasureAnimatedHeadAngles(GazeChainCalibration chain)
        {
            Vector3 restForward = chain.CurrentEyeRestForward;
            bool calibrated = chain.TryGetGazeReferenceFrame(out GazeReferenceFrame referenceFrame);
            bool measured = calibrated
                ? GazeSolverMath.TryGetDirectionYawPitch(
                    referenceFrame, restForward, out float yaw, out float pitch)
                : GazeSolverMath.TryGetDirectionYawPitch(chain.Root, restForward, out yaw, out pitch);

            _animatedHeadAngles = measured ? new Vector2(yaw, pitch) : Vector2.zero;
        }

        /// <summary>
        ///     Seeds the head's pursuit trackers where the movement just finished, carrying the
        ///     velocity it landed with, so the lane change is invisible.
        /// </summary>
        private void HandOffHeadToTracking()
        {
            _headYawTracker.Seed(_headYaw, _headYawShift.Velocity);
            _headPitchTracker.Seed(_headPitch, _headPitchShift.Velocity);
        }

        /// <summary>
        ///     Advances the neck/head split displacement for this frame. See <see cref="ChainLag" />.
        /// </summary>
        /// <remarks>
        ///     Semi-implicit Euler, which is unconditionally stable for this system at any frame
        ///     rate the engine actually runs at, and — unlike an analytic solution — stays correct
        ///     when the drive changes every frame, which it does.
        /// </remarks>
        private void UpdateChainLag(Vector2 aim, ConvaiGazeProfile profile, float dt)
        {
            float followThrough = Mathf.Clamp01(profile.ChainFollowThrough);

            // A zero amount must be a complete no-op, not a system that happens to settle at
            // zero: a character authored to turn rigidly should be bit-identical to one built
            // before this existed.
            if (followThrough <= 0f)
            {
                _chainLag = 0f;
                _chainLagVelocity = 0f;
                _previousAim = aim;
                _hasPreviousAim = true;
                return;
            }

            // Semi-implicit Euler on this system is stable to about 13 fps and divergent below it.
            // The clamp costs nothing at any playable frame rate and turns a pathological hitch
            // into a slightly slow response instead of a split that flaps against its bounds.
            float step = Mathf.Min(dt, 1f / 20f);

            float speed = _hasPreviousAim ? Vector2.Distance(aim, _previousAim) / dt : 0f;
            _previousAim = aim;
            _hasPreviousAim = true;

            float drive = followThrough * ChainLag.MaxShare *
                          Mathf.Clamp01(speed / ChainLag.ReferenceSpeed);

            float acceleration =
                ChainLag.Stiffness * (drive - _chainLag) - ChainLag.Damping * _chainLagVelocity;
            _chainLagVelocity += acceleration * step;
            _chainLag = Mathf.Clamp(
                _chainLag + _chainLagVelocity * step, ChainLag.MinClamp, ChainLag.MaxClamp);
        }

        /// <summary>Whichever lane currently owns the channel is the one whose velocity is real.</summary>
        private static float LaneVelocity(in BallisticMotor shift, in PursuitTracker tracking) =>
            shift.IsActive ? shift.Velocity : tracking.Velocity;

        /// <summary>
        ///     How long a movement of this size should take — the main sequence.
        /// </summary>
        /// <remarks>
        ///     Duration grows linearly with amplitude from a non-zero floor, which is the whole
        ///     point: the floor is what stops small movements from being the sharpest thing on
        ///     screen. A time-optimal trajectory under an acceleration cap scales as the square
        ///     root of distance, so a 5° correction finishes in a tenth of the time of a 40° turn
        ///     rather than half of it, and reads as a twitch.
        /// </remarks>
        private static float MovementSeconds(
            float amplitudeDegrees, float baseSeconds, float secondsPerDegree, float tempoScale) =>
            (Mathf.Max(0f, baseSeconds) + Mathf.Max(0f, secondsPerDegree) * Mathf.Abs(amplitudeDegrees)) *
            Mathf.Max(0.1f, tempoScale);

        /// <summary>
        ///     Duration multiplier for what kind of movement this is. Anything the character
        ///     chose to do is <see cref="GazeMovementUrgency.Neutral" /> — including looking at
        ///     the player, which is an ordinary act of attention and not an alarm.
        /// </summary>
        private static float TempoScale(GazeMovementUrgency urgency, ConvaiGazeProfile profile) =>
            urgency switch
            {
                GazeMovementUrgency.Relaxed => Mathf.Max(0.1f, profile.IdleDriftTempoScale),
                GazeMovementUrgency.Urgent => UrgentTempoScale,
                _ => 1f
            };

        private void Apply(GazeChainCalibration chain, ConvaiGazeProfile profile)
        {
            const float epsilon = 0.005f;
            bool headActive = Mathf.Abs(_appliedHeadYaw) > epsilon || Mathf.Abs(_appliedHeadPitch) > epsilon;
            bool torsoActive = Mathf.Abs(_torsoYaw) > epsilon || Mathf.Abs(_torsoPitch) > epsilon;
            bool rollActive = Mathf.Abs(_appliedHeadRoll) > epsilon;
            if (!headActive && !torsoActive && !rollActive) return;

            Transform reference = chain.Root;
            bool calibrated = chain.TryGetGazeReferenceFrame(out GazeReferenceFrame referenceFrame);

            // ---- Compose every delta first, write once. ----
            Quaternion chestDelta = Quaternion.identity;
            Quaternion upperChestDelta = Quaternion.identity;

            if (torsoActive)
            {
                Quaternion torsoSwing = BuildAimSwing(reference, calibrated, referenceFrame, _torsoYaw, _torsoPitch);
                if (chain.Chest != null && chain.UpperChest != null)
                {
                    GazeSolverMath.SplitAimSwing(torsoSwing, ProceduralPoseCompositor.ChestAimShare,
                        out chestDelta, out upperChestDelta);
                }
                else if (chain.UpperChest != null)
                {
                    upperChestDelta = torsoSwing;
                }
                else if (chain.Chest != null)
                {
                    chestDelta = torsoSwing;
                }
            }

            Quaternion neckDelta = Quaternion.identity;
            Quaternion headDelta = Quaternion.identity;

            if (headActive || rollActive)
            {
                Quaternion headSwing = BuildAimSwing(reference, calibrated, referenceFrame, _appliedHeadYaw, _appliedHeadPitch);

                // Gesture roll: Head only, never distributed to the Neck the way the aim is. A
                // tilt reads as a head gesture specifically when it stays local to the head —
                // the NeckShare split exists to make aim turns look anatomically continuous,
                // which does not apply to a tilt with no torso/neck counterpart to share with.
                // The roll axis is the AIMED forward, not the frame's neutral forward: once the
                // head is yawed, the neutral forward is no longer the head's long axis and a
                // roll about it reads as a tilt-plus-yaw rather than a tilt.
                Vector3 neutralForward = calibrated ? referenceFrame.Forward : reference.forward;
                Quaternion roll = GazeSolverMath.RollSwing(headSwing * neutralForward, _appliedHeadRoll);

                if (chain.Neck != null && chain.Head != null)
                {
                    // The split, displaced by however much overlapping action is currently in
                    // the chain. The swing being divided is untouched — only where the division
                    // falls moves, which is what keeps the aim exact while the chain flexes.
                    float neckShare = Mathf.Clamp01(profile.NeckShare + _chainLag);
                    GazeSolverMath.SplitAimSwing(headSwing, neckShare, out neckDelta, out headDelta);
                    // Roll is composed into the SAME write as the head's aim rather than a
                    // second write+record, so each bone is written exactly once per frame.
                    headDelta = roll * headDelta;
                }
                else if (chain.Head != null)
                {
                    headDelta = roll * headSwing;
                }
                else if (chain.Neck != null)
                {
                    neckDelta = roll * headSwing;
                }
            }

            // Route each bone to whichever guard actually owns it.
            //
            // The shared compositor is the single writer for every bone it has bound — Body
            // Language owns it, and it is built for several writers composing onto the same
            // bone in one frame (it keeps the FIRST pre-write value, so a restore unwinds to
            // the animated pose, never to an intermediate composite). Running gaze's own guard
            // over those same bones would be two restore protocols on one bone: a double-unwind
            // waiting to happen.
            //
            // But a compositor can be bound to a SUBSET of this chain — it binds from its own
            // rig resolution, and a character can have a spine bound and no head chain. Handing
            // it a delta for a bone it does not hold would silently drop that write, so bones it
            // does not own fall to the gaze-private guard. The two guards then cover disjoint
            // sets, which is the only arrangement that is safe.
            bool sinkBound = _poseSink != null && _poseSink.IsBound;
            bool sinkHasChest = sinkBound && _poseSink.Chest == chain.Chest;
            bool sinkHasUpperChest = sinkBound && _poseSink.UpperChest == chain.UpperChest;
            bool sinkHasNeck = sinkBound && _poseSink.Neck == chain.Neck;
            bool sinkHasHead = sinkBound && _poseSink.Head == chain.Head;

            if (sinkBound)
            {
                _poseSink.ComposeGazeAim(
                    sinkHasChest ? chestDelta : Quaternion.identity,
                    sinkHasUpperChest ? upperChestDelta : Quaternion.identity,
                    sinkHasNeck ? neckDelta : Quaternion.identity,
                    sinkHasHead ? headDelta : Quaternion.identity);
            }

            if (!sinkHasChest) ApplyGuarded(chain.Chest, chestDelta);
            if (!sinkHasUpperChest) ApplyGuarded(chain.UpperChest, upperChestDelta);
            if (!sinkHasNeck) ApplyGuarded(chain.Neck, neckDelta);
            if (!sinkHasHead) ApplyGuarded(chain.Head, headDelta);
        }

        /// <summary>Aim swing in whichever reference the rig calibrated to.</summary>
        private static Quaternion BuildAimSwing(
            Transform reference, bool calibrated, in GazeReferenceFrame referenceFrame, float yaw, float pitch) =>
            calibrated
                ? GazeSolverMath.AimSwing(referenceFrame, yaw, pitch)
                : GazeSolverMath.AimSwing(reference, yaw, pitch);

        /// <summary>
        ///     Writes one composed world-space delta through the pose-write guard so the write
        ///     can be unwound next frame if no animation source re-poses the bone. Exactly one
        ///     write and one record per bone, which is what keeps the guard's fixed 4-slot
        ///     capacity — chest, upper chest, neck, head — sufficient.
        /// </summary>
        private void ApplyGuarded(Transform bone, Quaternion worldDelta)
        {
            if (bone == null || worldDelta == Quaternion.identity) return;

            Quaternion preWrite = bone.localRotation;
            GazeSolverMath.ApplyDelta(bone, worldDelta);
            _writeGuard.Record(bone, preWrite);
        }
    }
}
