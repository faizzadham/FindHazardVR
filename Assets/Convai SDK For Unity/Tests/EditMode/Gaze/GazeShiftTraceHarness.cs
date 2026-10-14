using System;
using System.Collections.Generic;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     One frame of a recorded gaze shift. Angles are degrees in the character-root frame:
    ///     yaw positive to the character's right, pitch positive upward.
    /// </summary>
    internal struct GazeShiftSample
    {
        public float Time;

        /// <summary>The shift still required this frame: eye line to target.</summary>
        public Vector2 Required;

        public Vector2 Head;
        public Vector2 Torso;

        /// <summary>
        ///     Head angular velocity this frame (deg/s), the first difference of <see cref="Head" />
        ///     against the previous sample over the frame's <c>dt</c>. Zero on the first sample.
        /// </summary>
        public Vector2 HeadVelocity;

        /// <summary>Torso angular velocity this frame (deg/s), the first difference of <see cref="Torso" />.</summary>
        public Vector2 TorsoVelocity;

        /// <summary>
        ///     Head angular acceleration this frame (deg/s^2), the first difference of
        ///     <see cref="HeadVelocity" />. Zero on the first two samples, since it needs a prior
        ///     velocity to difference against.
        /// </summary>
        public Vector2 HeadAcceleration;

        /// <summary>Torso angular acceleration this frame (deg/s^2), the first difference of <see cref="TorsoVelocity" />.</summary>
        public Vector2 TorsoAcceleration;

        /// <summary>Cyclopean eye angles relative to the head-carried rest forward.</summary>
        public Vector2 Eye;

        /// <summary>Roll written to the head bone, measured from the bone itself, not reported by the solver.</summary>
        public float HeadRoll;

        /// <summary>Root world yaw, so a body turn is visible in the trace.</summary>
        public float RootYaw;

        /// <summary>Head-carried "straight ahead" deviation, i.e. what the animation is doing to the head.</summary>
        public Vector2 AnimatedDeviation;

        /// <summary>Deepest ladder rung recruited this frame.</summary>
        public GazeLadderDepth Depth;

        /// <summary>Whether the ladder asked for a body turn this frame.</summary>
        public bool WantsFeet;

        /// <summary>
        ///     Whether a ballistic head movement was in flight this frame — the observable side of
        ///     the solver's movement detector.
        /// </summary>
        /// <remarks>
        ///     <c>DetectMovement</c> is private and has to stay that way, but what it decides is
        ///     visible: a detected movement arms the shift lane. Recorded so a trace can assert
        ///     that a target which never really moved never re-planned one, which is the whole
        ///     claim of target conditioning and is not readable from the head angles alone (a
        ///     small re-plan and a small pursuit correction look the same in the pose).
        /// </remarks>
        public bool HeadShiftActive;

        /// <summary>
        ///     The head share the ladder allocated this frame, before the actuator's own
        ///     stabilization reflex and body-turn relief. Recorded so overshoot can be measured
        ///     against what the head was ASKED for, which is the only thing the motor stage
        ///     controls — the requirement itself legitimately drifts as the head turns and
        ///     carries the eye line with it.
        /// </summary>
        public Vector2 PlannedHead;

        /// <summary>How far the eyes sit from orbit centre.</summary>
        public float EyeEccentricity => Eye.magnitude;

        /// <summary>Head angular speed this frame (deg/s) — the magnitude a viewer actually reads.</summary>
        public float HeadSpeed => HeadVelocity.magnitude;

        /// <summary>Torso angular speed this frame (deg/s).</summary>
        public float TorsoSpeed => TorsoVelocity.magnitude;

        /// <summary>Head angular acceleration magnitude this frame (deg/s^2).</summary>
        public float HeadAccelerationMagnitude => HeadAcceleration.magnitude;

        /// <summary>Torso angular acceleration magnitude this frame (deg/s^2).</summary>
        public float TorsoAccelerationMagnitude => TorsoAcceleration.magnitude;

        /// <summary>Required minus delivered — the conservation residual (see the coordination invariant).</summary>
        /// <remarks>
        ///     <para>
        ///         <b>Carries a geometric term, so it is not zero on a correct solve.</b> Summing
        ///         the three contributions treats the eyes as if they sat on the head's rotation
        ///         pivot. They do not, on this rig or on any other: the harness places the eye
        ///         centre 7.5 cm in front of the head bone, so rotating the head carries the eyes
        ///         along an arc and the bearing change delivered to a nearby target is not equal
        ///         to the angle the head turned through. A 30° head rotation toward a target 2 m
        ///         away moves the eye centre to x=+3.75 cm, from which the target sits at 28.94°
        ///         — so the eyes correctly counter-rotate by about −1.06° and the sum falls short
        ///         of the requirement by the same amount, permanently, with nothing wrong.
        ///     </para>
        ///     <para>
        ///         The term grows with turn angle and shrinks with target distance. Callers
        ///         asserting on this must budget for it — see
        ///         <see cref="PivotParallaxAllowanceDegrees" /> — rather than assuming a bound
        ///         that happened to hold for the angles first tried.
        ///     </para>
        /// </remarks>
        public float ConservationError =>
            (Required - new Vector2(Head.x + Torso.x + Eye.x, Head.y + Torso.y + Eye.y)).magnitude;
    }

    /// <summary>
    ///     The largest single-frame change in the APPLIED head pose over a trace: how far the
    ///     head jumped, and where — so a failure says WHERE and not merely THAT.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Measured on <see cref="GazeShiftSample.Head" />, which is what the solver actually
    ///         wrote to the bones: the two-lane actuator's output plus body-turn relief, the
    ///         aversion beat, the head-gesture envelope and the animated-deviation stabilization
    ///         reflex, after the soft clamp.
    ///     </para>
    ///     <para>
    ///         Deliberately NOT <see cref="GazeShiftSample.PlannedHead" />. The ladder's allocated
    ///         share is ALLOWED to step — a decision to look elsewhere is a step, and turning it
    ///         into a movement is the lane's job. Every "the head snapped" defect so far came from
    ///         a producer composed on top of the lanes, downstream of everything that shapes
    ///         motion, so the lane input is the one signal that proves nothing.
    ///     </para>
    /// </remarks>
    internal readonly struct GazeAppliedHeadStep
    {
        /// <summary>Magnitude of the one-frame change in applied (yaw, pitch), in degrees.</summary>
        public readonly float Degrees;

        /// <summary>Signed one-frame change in applied head yaw (degrees).</summary>
        public readonly float DeltaYaw;

        /// <summary>Signed one-frame change in applied head pitch (degrees).</summary>
        public readonly float DeltaPitch;

        /// <summary>Index into <see cref="GazeShiftTraceHarness.Samples" /> of the frame it landed on.</summary>
        public readonly int FrameIndex;

        /// <summary>Trace time (seconds) of that frame.</summary>
        public readonly float TimeSeconds;

        /// <summary>Applied head angles the frame before.</summary>
        public readonly Vector2 From;

        /// <summary>Applied head angles on that frame.</summary>
        public readonly Vector2 To;

        public GazeAppliedHeadStep(int frameIndex, float timeSeconds, Vector2 from, Vector2 to)
        {
            FrameIndex = frameIndex;
            TimeSeconds = timeSeconds;
            From = from;
            To = to;
            DeltaYaw = to.x - from.x;
            DeltaPitch = to.y - from.y;
            Degrees = (to - from).magnitude;
        }

        public override string ToString() =>
            $"largest applied head step {Degrees:0.000} deg " +
            $"(yaw {DeltaYaw:+0.000;-0.000}, pitch {DeltaPitch:+0.000;-0.000}) " +
            $"on frame {FrameIndex} at t={TimeSeconds:0.000}s, " +
            $"head ({From.x:0.00}, {From.y:0.00}) -> ({To.x:0.00}, {To.y:0.00}) deg " +
            $"= {Degrees * 60f:0} deg/s at the harness's 60 Hz";
    }

    /// <summary>
    ///     Drives the gaze solver chain over a synthetic rig with no scene, no Animator and no
    ///     controller, recording one <see cref="GazeShiftSample" /> per frame.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the measurement apparatus the head/eye coordination work is judged on.
    ///         Before it existed, "the turn feels wrong" and "the turn feels better" were both
    ///         unfalsifiable; every claim in the acceptance criteria is a query over a trace
    ///         this harness produced.
    ///     </para>
    ///     <para>
    ///         <b>The animated-pose reset is load-bearing.</b> The solvers layer swing deltas on
    ///         top of whatever pose the Animator left, and recompute them from scratch each
    ///         frame. With no Animator, last frame's delta is still on the bones, so the harness
    ///         restores the rest pose (optionally plus an authored deviation) at the top of every
    ///         frame exactly as a live Animator would. Without it the deltas integrate and the
    ///         measured heading spins.
    ///     </para>
    ///     <para>
    ///         Disposable rather than <c>[TearDown]</c>-driven so a test can run several traces
    ///         and compare them.
    ///     </para>
    /// </remarks>
    internal sealed class GazeShiftTraceHarness : IDisposable
    {
        public const float FrameSeconds = 1f / 60f;

        /// <summary>
        ///     Seconds each <see cref="Step" /> advances by. Defaults to the harness's 60 Hz;
        ///     a test that claims frame-rate independence sets this and re-runs the same
        ///     scenario, because "independent by construction" is a design intent and only a
        ///     trace at another rate makes it a measurement.
        /// </summary>
        public float FrameDeltaSeconds { get; set; } = FrameSeconds;

        /// <summary>The speed floor a movement-bounds query treats as "the head has started".</summary>
        private const float MovementStartSpeedThresholdDegPerSec = 1f;

        /// <summary>
        ///     The settle tolerance used by the queries that do not take their own epsilon
        ///     (<see cref="PeakSpeedPositionFraction" />, <see cref="IsVelocityUnimodal" />).
        /// </summary>
        private const float UnimodalSettleEpsilonDegrees = 0.5f;

        private readonly GameObject _root;
        private readonly Transform _chest;
        private readonly Transform _upperChest;
        private readonly Transform _neck;
        private readonly Transform _head;
        private readonly Transform _leftEye;
        private readonly Transform _rightEye;

        private readonly Quaternion _chestRest;
        private readonly Quaternion _upperChestRest;
        private readonly Quaternion _neckRest;
        private readonly Quaternion _headRest;
        private readonly Quaternion _leftEyeRest;
        private readonly Quaternion _rightEyeRest;

        private readonly GazeShiftDirector _shiftDirector = new();
        private readonly HeadTorsoSolver _headTorso = new();
        private readonly EyeSolver _eyes = new();
        private readonly GazeChainCalibration _chain = new();
        private readonly List<GazeShiftSample> _samples = new(2048);

        private float _time;
        private int _generation = 1;
        private GazeShiftPlan _lastPlan = GazeShiftPlan.Idle;
        private GazeShiftMeasurement _lastMeasurement;

        public GazeShiftTraceHarness(ConvaiGazeProfile profile)
        {
            Profile = profile;

            _root = new GameObject("TraceRoot");
            _chest = NewChild(_root.transform, "Chest", new Vector3(0f, 1.00f, 0f));
            _upperChest = NewChild(_chest, "UpperChest", new Vector3(0f, 1.20f, 0f));
            _neck = NewChild(_upperChest, "Neck", new Vector3(0f, 1.45f, 0f));
            _head = NewChild(_neck, "Head", new Vector3(0f, 1.55f, 0f));
            // Offset forward and up from the head pivot by roughly what a real rig uses, so the
            // eye-line aim (and its absence) is measurable rather than degenerate.
            _leftEye = NewChild(_head, "LeftEye", new Vector3(-0.032f, 1.62f, 0.075f));
            _rightEye = NewChild(_head, "RightEye", new Vector3(0.032f, 1.62f, 0.075f));

            _chain.BindManual(_root.transform, _chest, _upperChest, _neck, _head, _leftEye, _rightEye);

            _chestRest = _chest.localRotation;
            _upperChestRest = _upperChest.localRotation;
            _neckRest = _neck.localRotation;
            _headRest = _head.localRotation;
            _leftEyeRest = _leftEye.localRotation;
            _rightEyeRest = _rightEye.localRotation;
        }

        public ConvaiGazeProfile Profile { get; }

        public Transform Root => _root.transform;
        public Transform Head => _head;
        public Vector3 EyeCenter => (_leftEye.position + _rightEye.position) * 0.5f;

        /// <summary>Every recorded frame, oldest first.</summary>
        public IReadOnlyList<GazeShiftSample> Samples => _samples;

        /// <summary>
        ///     Pitch (degrees, about the root's right axis) the "animation" bows the head by at
        ///     the top of every frame — the walk and talk clips' own head motion, which the
        ///     solver's stabilization reflex exists to cancel. Zero means a still rig.
        /// </summary>
        public float AnimatedHeadPitchDegrees { get; set; }

        /// <summary>Yaw (degrees) the "animation" turns the head by at the top of every frame.</summary>
        public float AnimatedHeadYawDegrees { get; set; }

        /// <summary>
        ///     Whether idle life is running. Mirrors the controller's
        ///     <c>ambientActive = !HasEngagedTarget &amp;&amp; EnableAmbientExploration</c>: setting
        ///     this is a request, and it only reaches the solvers on frames that have no engaged
        ///     target, exactly as in the live chain.
        /// </summary>
        public bool AmbientActive { get; set; }

        /// <summary>
        ///     The ambient fixation (yaw/pitch degrees from rest forward) the exploration director
        ///     is currently holding. Settable rather than driven by the real director because the
        ///     transition worth tracing is the director CHANGING it — it hands over a discrete
        ///     fixation, it does not slide to one — and a test needs to choose when that happens.
        /// </summary>
        public Vector2 AmbientAngles { get; set; }

        /// <summary>
        ///     What kind of movement the traced shift is, which is what decides how long it takes.
        ///     Defaults to <see cref="GazeMovementUrgency.Relaxed" /> — the solver input's own zero
        ///     value — so every trace written before this knob existed keeps the tempo it was
        ///     recorded at.
        /// </summary>
        public GazeMovementUrgency Urgency { get; set; }

        /// <summary>Bumps the generation id, which is how the solvers learn a re-target happened.</summary>
        public void Retarget() => _generation++;

        /// <summary>
        ///     Rotates the root, standing in for a body turn the reorientation director would
        ///     have driven. Returns the achieved world yaw.
        /// </summary>
        public float TurnRoot(float degrees)
        {
            _root.transform.Rotate(0f, degrees, 0f, Space.World);
            return _root.transform.eulerAngles.y;
        }

        /// <summary>Runs <paramref name="seconds" /> of frames toward a fixed world point.</summary>
        public void Run(
            float seconds,
            Vector3 targetPoint,
            float engagement = 1f,
            float headContribution = 1f,
            bool bodyTurnActive = false,
            bool hasTarget = true,
            float commitment = 1f)
        {
            int steps = Mathf.CeilToInt(seconds / FrameDeltaSeconds);
            for (int i = 0; i < steps; i++)
                Step(targetPoint, engagement, headContribution, bodyTurnActive, hasTarget, commitment);
        }

        /// <summary>
        ///     Runs a single step-response experiment: a fresh synthetic rig, a target placed
        ///     <paramref name="amplitudeDegrees" /> off the character's forward axis at eye height,
        ///     held fixed for <paramref name="frameCount" /> frames of <see cref="FrameSeconds" />
        ///     each. This is the experiment the main-sequence table and the recorded baseline are
        ///     built from — a step, not a moving target, so the recorded shape is the actuator's
        ///     alone.
        /// </summary>
        /// <remarks>
        ///     Returns the harness rather than just the samples so a caller can also inspect
        ///     <see cref="Final" />, <see cref="Root" />, etc. Ownership — and disposal — passes to
        ///     the caller, matching every other use of this harness.
        /// </remarks>
        /// <param name="urgency">
        ///     What kind of movement this step is, which scales its duration. Defaults to
        ///     <see cref="GazeMovementUrgency.Relaxed" /> so the recorded baseline keeps the tempo
        ///     it was recorded at; a caller measuring the cost of a classification passes its own.
        /// </param>
        public static GazeShiftTraceHarness RunStepResponse(
            ConvaiGazeProfile profile,
            float amplitudeDegrees,
            int frameCount,
            GazeMovementUrgency urgency = GazeMovementUrgency.Relaxed)
        {
            var harness = new GazeShiftTraceHarness(profile) { Urgency = urgency };

            Vector3 origin = harness.EyeCenter;
            Quaternion rotation = Quaternion.AngleAxis(amplitudeDegrees, harness.Root.up);
            Vector3 targetPoint = origin + rotation * (harness.Root.forward * 2f);

            for (int i = 0; i < frameCount; i++)
                harness.Step(targetPoint);

            return harness;
        }

        /// <summary>One frame: re-pose as an Animator would, solve head then eyes, record.</summary>
        /// <param name="hasTarget">
        ///     Whether a target is engaged this frame. False models a released target, and
        ///     reproduces the controller's own handling of it: <c>ConvaiGazeController</c> measures
        ///     the shift only while <c>_directive.HasEngagedTarget</c>, and CLEARS the measurement
        ///     to <c>default</c> otherwise — which zeroes not only the requirement but the
        ///     animated-deviation fields the stabilization reflex reads. Modelled faithfully
        ///     rather than approximated, because the release edge is precisely what is under test.
        /// </param>
        /// <param name="commitment">
        ///     The arbiter's acquire/release ramp for this frame. Only the idle-life hand-over
        ///     reads it: below 1 with idle life running, the fixation is still there to be handed
        ///     over, exactly as <c>ConvaiGazeController</c> computes <c>AmbientHandover</c>.
        ///     Defaults to 1 (fully committed), which is no hand-over and therefore the engaged
        ///     behaviour every other trace expects.
        ///     <para>
        ///         Note it does NOT scale <paramref name="engagement" />: the ladder is handed the
        ///         settled strength precisely so the acquisition ramp cannot shape the movement.
        ///     </para>
        /// </param>
        public void Step(
            Vector3 targetPoint,
            float engagement = 1f,
            float headContribution = 1f,
            bool bodyTurnActive = false,
            bool hasTarget = true,
            float commitment = 1f)
        {
            float dt = FrameDeltaSeconds;
            ApplyAnimatedPose();

            // The real pipeline order: measure the shift once from the rig, divide it once
            // across the ladder, then let each actuator execute the share it was handed.
            GazeShiftMeasurement measurement = default;
            bool measured = hasTarget && _chain.TryMeasureShift(targetPoint, out measurement);
            if (!measured) measurement = default;

            // Idle life only runs when nothing is engaged — the controller's own gate — and hands
            // the head over to (and back from) an engaged look at the moment the head joins it,
            // rather than by being switched off underneath it.
            bool ambientActive = AmbientActive && !measured;
            bool ambientHandover = AmbientActive && measured && commitment < 0.9999f;

            GazeShiftPlan plan = measured
                ? _shiftDirector.Plan(
                    in measurement, Profile, engagement, headContribution,
                    torsoAvailable: true, feetAvailable: true, _generation, dt,
                    (_eyes.LeftEyeAngles + _eyes.RightEyeAngles).magnitude * 0.5f,
                    _headTorso.HeadAngles.x)
                : GazeShiftPlan.Idle;
            _lastPlan = plan;
            _lastMeasurement = measurement;

            var headInput = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = Profile,
                DeltaTime = dt,
                TargetPoint = targetPoint,
                HasTarget = measured,
                Measurement = measurement,
                Plan = plan,
                Engagement = engagement,
                AmbientAngles = AmbientAngles,
                AmbientActive = ambientActive,
                AmbientHandover = ambientHandover,
                BodyTurnActive = bodyTurnActive,
                Urgency = Urgency
            };
            _headTorso.Solve(in headInput);

            var eyeInput = new EyeSolveInput
            {
                Chain = _chain,
                Profile = Profile,
                DeltaTime = dt,
                TargetPoint = targetPoint,
                HasTarget = measured,
                Engagement = engagement,
                AmbientAngles = AmbientAngles,
                AmbientActive = ambientActive,
                // The head stage sampled it this frame, before it wrote anything — the real
                // chain's own order, and the reason the harness solves head then eyes.
                AnimatedHeadAngles = _headTorso.AnimatedHeadAngles,
                GenerationId = _generation,
                ApplyToBones = true,
                SaccadeTempoScale = 1f,
                FixationLiveliness = 1f
            };
            _eyes.Solve(in eyeInput);

            _time += dt;
            _samples.Add(Capture());
        }

        private GazeShiftSample Capture()
        {
            Vector2 eye = (_eyes.LeftEyeAngles + _eyes.RightEyeAngles) * 0.5f;
            Vector2 head = _headTorso.HeadAngles;
            Vector2 torso = _headTorso.TorsoAngles;

            // Differenced against the previous sample, which is still the last entry in
            // _samples: this frame's own sample has not been appended yet. The first sample has
            // no predecessor, so velocity (and therefore acceleration) is zero there by
            // construction rather than by a special case.
            Vector2 headVelocity = Vector2.zero;
            Vector2 torsoVelocity = Vector2.zero;
            Vector2 headAcceleration = Vector2.zero;
            Vector2 torsoAcceleration = Vector2.zero;
            if (_samples.Count > 0)
            {
                GazeShiftSample previous = _samples[^1];
                headVelocity = (head - previous.Head) / FrameDeltaSeconds;
                torsoVelocity = (torso - previous.Torso) / FrameDeltaSeconds;
                headAcceleration = (headVelocity - previous.HeadVelocity) / FrameDeltaSeconds;
                torsoAcceleration = (torsoVelocity - previous.TorsoVelocity) / FrameDeltaSeconds;
            }

            return new GazeShiftSample
            {
                Time = _time,
                Required = new Vector2(_lastMeasurement.RequiredYaw, _lastMeasurement.RequiredPitch),
                Head = head,
                Torso = torso,
                HeadVelocity = headVelocity,
                TorsoVelocity = torsoVelocity,
                HeadAcceleration = headAcceleration,
                TorsoAcceleration = torsoAcceleration,
                Eye = eye,
                HeadRoll = MeasuredHeadRoll(),
                RootYaw = _root.transform.eulerAngles.y,
                AnimatedDeviation = new Vector2(AnimatedHeadYawDegrees, AnimatedHeadPitchDegrees),
                Depth = _lastPlan.Depth,
                WantsFeet = _lastPlan.WantsFeet,
                HeadShiftActive = _headTorso.IsShifting,
                PlannedHead = _lastPlan.Head
            };
        }

        /// <summary>
        ///     Roll measured from the head bone itself rather than read back from the solver:
        ///     a composition fault produces roll the solver does not know it wrote, so asking
        ///     the solver would be asking the suspect.
        /// </summary>
        private float MeasuredHeadRoll()
        {
            Vector3 forward = _head.forward;
            Vector3 referenceUp = Vector3.ProjectOnPlane(_root.transform.up, forward);
            Vector3 headUp = Vector3.ProjectOnPlane(_head.up, forward);
            if (referenceUp.sqrMagnitude < 1e-8f || headUp.sqrMagnitude < 1e-8f) return 0f;

            return Vector3.SignedAngle(referenceUp, headUp, forward);
        }

        /// <summary>
        ///     Stands in for the Animator: restores the rest pose, then applies this trace's
        ///     authored head deviation. Runs before the solvers every frame, which is the real
        ///     execution order (Animator → embodiment actuation).
        /// </summary>
        private void ApplyAnimatedPose()
        {
            _chest.localRotation = _chestRest;
            _upperChest.localRotation = _upperChestRest;
            _neck.localRotation = _neckRest;
            _head.localRotation = _headRest;
            _leftEye.localRotation = _leftEyeRest;
            _rightEye.localRotation = _rightEyeRest;

            if (AnimatedHeadPitchDegrees == 0f && AnimatedHeadYawDegrees == 0f) return;

            Transform reference = _root.transform;
            Quaternion delta = Quaternion.AngleAxis(AnimatedHeadYawDegrees, reference.up) *
                               Quaternion.AngleAxis(-AnimatedHeadPitchDegrees, reference.right);
            _head.rotation = delta * _head.rotation;
        }

        // ------------------------------------------------------------------ trace queries

        /// <summary>Largest absolute head roll over the trace — the sideways head tilt a viewer reads.</summary>
        public float PeakHeadRoll() => Peak(s => Mathf.Abs(s.HeadRoll));

        /// <summary>Largest eye eccentricity over the trace.</summary>
        public float PeakEyeEccentricity() => Peak(s => s.EyeEccentricity);

        /// <summary>
        ///     How much of a conservation residual is pure geometry rather than a coordination
        ///     fault, for a shift of <paramref name="turnDegrees" /> toward a target
        ///     <paramref name="targetDistanceMeters" /> away.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Derived from the rig, not fitted to an observation. The eye centre sits
        ///         <c>forwardOffset</c> in front of the head's pivot, so a turn of θ moves it to
        ///         <c>(sin θ, cos θ) × forwardOffset</c>; the bearing to the target from there is
        ///         no longer θ, and the eyes take up the difference. Everything here is the
        ///         planar law of that arc — no constant is chosen to make a test pass.
        ///     </para>
        ///     <para>
        ///         Checked against the measured value before being trusted: at θ=30°, d=2 m this
        ///         predicts 1.06° and the solver produces 1.079°.
        ///     </para>
        /// </remarks>
        public float PivotParallaxAllowanceDegrees(float turnDegrees, float targetDistanceMeters)
        {
            // Eye centre relative to the head bone, in the rest pose — the arc's radius.
            float forwardOffset = Mathf.Abs(
                _head.InverseTransformPoint((_leftEye.position + _rightEye.position) * 0.5f).z);
            if (forwardOffset <= 0f || targetDistanceMeters <= 0f) return 0f;

            float theta = turnDegrees * Mathf.Deg2Rad;

            // Target relative to the REST eye centre, and the eye centre after the turn.
            var target = new Vector2(
                targetDistanceMeters * Mathf.Sin(theta), targetDistanceMeters * Mathf.Cos(theta));
            var movedEye = new Vector2(
                forwardOffset * Mathf.Sin(theta), forwardOffset * (Mathf.Cos(theta) - 1f));

            Vector2 fromMoved = target - movedEye;
            float bearing = Mathf.Atan2(fromMoved.x, fromMoved.y) * Mathf.Rad2Deg;
            return Mathf.Abs(turnDegrees - bearing);
        }

        /// <summary>Largest conservation residual, ignoring the first <paramref name="settleSeconds" />.</summary>
        public float PeakConservationErrorAfter(float settleSeconds)
        {
            float worst = 0f;
            foreach (GazeShiftSample s in _samples)
                if (s.Time >= settleSeconds)
                    worst = Mathf.Max(worst, s.ConservationError);

            return worst;
        }

        /// <summary>The final recorded frame — what the pose settled to.</summary>
        public GazeShiftSample Final() => _samples[^1];

        /// <summary>Time (seconds) of the first frame on which the ladder asked for the feet.</summary>
        public float TimeToFeetRequest()
        {
            foreach (GazeShiftSample sample in _samples)
                if (sample.WantsFeet)
                    return sample.Time;

            return float.PositiveInfinity;
        }

        /// <summary>
        ///     Longest stretch (seconds) the head yaw sat still — moving less than
        ///     <paramref name="epsilonDegrees" /> per frame — while it was still on its way to
        ///     the value it eventually settled on. This is the freeze-then-whip signature: the
        ///     eyes have gone, the head has not, and nothing is moving.
        /// </summary>
        /// <remarks>
        ///     Deliberately bounded to the approach. A head that has finished its share of a
        ///     shift and is holding it is correct, not frozen, so measuring stillness against
        ///     "the target is still off-axis" would fire on every settled pose the ladder never
        ///     intended the head to close alone. The cutoff is the first frame the head reaches
        ///     90% of its final yaw.
        /// </remarks>
        public float LongestHeadPlateauBeforeSettle(float epsilonDegrees = 0.05f)
        {
            if (_samples.Count < 2) return 0f;

            float settled = _samples[^1].Head.x;
            int settleIndex = _samples.Count - 1;
            for (int i = 0; i < _samples.Count; i++)
            {
                if (Mathf.Abs(_samples[i].Head.x) < Mathf.Abs(settled) * 0.9f) continue;
                settleIndex = i;
                break;
            }

            float longest = 0f, current = 0f;
            for (int i = 1; i <= settleIndex; i++)
            {
                bool still = Mathf.Abs(_samples[i].Head.x - _samples[i - 1].Head.x) < epsilonDegrees;
                if (still)
                {
                    current += _samples[i].Time - _samples[i - 1].Time;
                    longest = Mathf.Max(longest, current);
                }
                else
                {
                    current = 0f;
                }
            }

            return longest;
        }

        /// <summary>
        ///     The worst one-frame jump in the APPLIED head pose over the trace — the module's
        ///     output-continuity measure. See <see cref="GazeAppliedHeadStep" /> for why it is the
        ///     applied pose and not the ladder's allocated share.
        /// </summary>
        /// <param name="afterSeconds">
        ///     Ignore frames before this trace time. Defaults to the whole trace: the first sample
        ///     has no predecessor to be differenced against, so the harness's own bind frame — where
        ///     the tracking filters initialise onto their goal — is excluded by construction rather
        ///     than by a window a test has to remember to pass.
        /// </param>
        public GazeAppliedHeadStep LargestAppliedHeadStep(float afterSeconds = 0f)
        {
            var worst = default(GazeAppliedHeadStep);
            for (int i = 1; i < _samples.Count; i++)
            {
                if (_samples[i].Time < afterSeconds) continue;

                float degrees = (_samples[i].Head - _samples[i - 1].Head).magnitude;
                if (degrees <= worst.Degrees) continue;

                worst = new GazeAppliedHeadStep(
                    i, _samples[i].Time, _samples[i - 1].Head, _samples[i].Head);
            }

            return worst;
        }

        /// <summary>Largest head angular speed over the trace (deg/s) — the main-sequence peak.</summary>
        public float PeakAngularSpeed() => Peak(s => s.HeadSpeed);

        /// <summary>Largest torso angular speed over the trace (deg/s).</summary>
        public float PeakTorsoAngularSpeed() => Peak(s => s.TorsoSpeed);

        /// <summary>Largest head angular acceleration magnitude over the trace (deg/s^2).</summary>
        public float MaxAbsAcceleration() => Peak(s => s.HeadAccelerationMagnitude);

        /// <summary>
        ///     Duration (seconds) of the head's movement: from the first frame its speed rises
        ///     above a small "it has started" floor to the last frame it has not yet settled —
        ///     the last frame whose position still sits more than <paramref name="settleEpsilonDegrees" />
        ///     from where it ends up.
        /// </summary>
        /// <remarks>
        ///     The "has it started" floor is a fixed, low threshold
        ///     (<see cref="MovementStartSpeedThresholdDegPerSec" />), not
        ///     <paramref name="settleEpsilonDegrees" /> — that parameter answers a different
        ///     question (how close counts as arrived), and conflating the two would make a looser
        ///     settle tolerance also delay the measured start of the movement.
        /// </remarks>
        public float MovementDurationSeconds(float settleEpsilonDegrees)
        {
            (int startIndex, int endIndex) = MovementBounds(settleEpsilonDegrees);
            if (startIndex < 0) return 0f;

            return _samples[endIndex].Time - _samples[startIndex].Time;
        }

        /// <summary>
        ///     Where in the head's movement the peak speed occurred, as a fraction of the
        ///     movement's own duration (0 = at the start, 1 = at the end). The main-sequence band
        ///     in §4.5 of the motion-quality plan expects this in the 0.35–0.5 window.
        /// </summary>
        public float PeakSpeedPositionFraction()
        {
            (int startIndex, int endIndex) = MovementBounds(UnimodalSettleEpsilonDegrees);
            if (startIndex < 0 || endIndex <= startIndex) return 0f;

            int peakIndex = startIndex;
            float peakSpeed = _samples[startIndex].HeadSpeed;
            for (int i = startIndex + 1; i <= endIndex; i++)
            {
                if (_samples[i].HeadSpeed <= peakSpeed) continue;
                peakSpeed = _samples[i].HeadSpeed;
                peakIndex = i;
            }

            float duration = _samples[endIndex].Time - _samples[startIndex].Time;
            if (duration <= 0f) return 0f;

            return Mathf.Clamp01((_samples[peakIndex].Time - _samples[startIndex].Time) / duration);
        }

        /// <summary>
        ///     True when the head's speed profile is a single hump: non-decreasing up to its peak
        ///     and non-increasing after it, both within <paramref name="tolerance" /> deg/s of
        ///     frame-to-frame noise. A profile with a second hump — a dip below the tolerance
        ///     followed by a re-rise — is the double-pump signature a stacked hold window leaves.
        /// </summary>
        /// <remarks>
        ///     A rig that never moves (no samples cross the start-of-movement floor) is vacuously
        ///     unimodal: there is no profile to be bimodal.
        /// </remarks>
        public bool IsVelocityUnimodal(float tolerance)
        {
            (int startIndex, int endIndex) = MovementBounds(UnimodalSettleEpsilonDegrees);
            if (startIndex < 0 || endIndex <= startIndex) return true;

            int peakIndex = startIndex;
            float peakSpeed = _samples[startIndex].HeadSpeed;
            for (int i = startIndex + 1; i <= endIndex; i++)
            {
                if (_samples[i].HeadSpeed <= peakSpeed) continue;
                peakSpeed = _samples[i].HeadSpeed;
                peakIndex = i;
            }

            for (int i = startIndex + 1; i <= peakIndex; i++)
                if (_samples[i].HeadSpeed < _samples[i - 1].HeadSpeed - tolerance)
                    return false;

            for (int i = peakIndex + 1; i <= endIndex; i++)
                if (_samples[i].HeadSpeed > _samples[i - 1].HeadSpeed + tolerance)
                    return false;

            return true;
        }

        /// <summary>
        ///     The head movement's [start, end] sample indices: start is the first frame its speed
        ///     crosses <see cref="MovementStartSpeedThresholdDegPerSec" />, end is the last frame
        ///     its position still sits more than <paramref name="settleEpsilonDegrees" /> from the
        ///     final sample. Returns (-1, -1) when the trace never starts moving.
        /// </summary>
        private (int startIndex, int endIndex) MovementBounds(float settleEpsilonDegrees)
        {
            if (_samples.Count < 2) return (-1, -1);

            int startIndex = -1;
            for (int i = 0; i < _samples.Count; i++)
            {
                if (_samples[i].HeadSpeed < MovementStartSpeedThresholdDegPerSec) continue;
                startIndex = i;
                break;
            }
            if (startIndex < 0) return (-1, -1);

            float finalMagnitude = _samples[^1].Head.magnitude;
            int endIndex = startIndex;
            for (int i = startIndex; i < _samples.Count; i++)
            {
                if (Mathf.Abs(_samples[i].Head.magnitude - finalMagnitude) <= settleEpsilonDegrees) continue;
                endIndex = i;
            }

            return (startIndex, endIndex);
        }

        private float Peak(Func<GazeShiftSample, float> selector)
        {
            float worst = 0f;
            foreach (GazeShiftSample sample in _samples)
                worst = Mathf.Max(worst, selector(sample));

            return worst;
        }

        private static Transform NewChild(Transform parent, string name, Vector3 worldPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            return go.transform;
        }

        public void Dispose()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }
    }
}
