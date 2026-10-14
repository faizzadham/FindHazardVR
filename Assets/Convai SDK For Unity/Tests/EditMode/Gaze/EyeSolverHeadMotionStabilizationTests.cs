using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The eyes must hold what they are looking at while the head turns underneath them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="EyeSolver" /> stores an eye-IN-HEAD angle, and two of its branches — the
    ///         saccadic latency wait and the ballistic flight between a saccade's captured start
    ///         and end — deliberately hold that angle still. While the head is turning, holding an
    ///         eye-in-head angle is not holding a fixation: it sweeps the eyes across the world at
    ///         head speed. Those two branches run during a gaze shift, which is precisely when the
    ///         head is turning.
    ///     </para>
    ///     <para>
    ///         The defect that motivated these tests was a staircase, measured on a shipped sample
    ///         character: the first saccade landed on the target, the still-turning head dragged
    ///         the eyes back off it, a catch-up saccade fired, and so on — three saccades and about
    ///         three quarters of a second to acquire one stationary target, with the eyes visibly
    ///         off it in between. The invariant below is the one that was violated: once the gaze
    ///         has reached a stationary target, continued head motion must not take it back off.
    ///     </para>
    /// </remarks>
    public sealed class EyeSolverHeadMotionStabilizationTests
    {
        private const float Dt = 1f / 120f;

        /// <summary>Once acquired, this is the most the gaze may stray while the head turns.</summary>
        private const float SettledToleranceDegrees = 4f;

        private ConvaiGazeProfile _profile;
        private EyeSolver _solver;
        private GazeChainCalibration _chain;

        private GameObject _root;
        private Transform _head;
        private Transform _leftEye;
        private Transform _rightEye;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _solver = new EyeSolver();
            _chain = new GazeChainCalibration();

            _root = new GameObject("Root");
            _head = NewChild(_root.transform, "Head", new Vector3(0f, 1.65f, 0f));
            _leftEye = NewChild(_head, "LeftEye", new Vector3(-0.032f, 1.7f, 0.08f));
            _rightEye = NewChild(_head, "RightEye", new Vector3(0.032f, 1.7f, 0.08f));

            _chain.BindManual(_root.transform, null, null, null, _head, _leftEye, _rightEye);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_profile);
        }

        private static Transform NewChild(Transform parent, string name, Vector3 worldPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            return go.transform;
        }

        private Vector3 EyeCenter => (_leftEye.position + _rightEye.position) * 0.5f;

        private EyeSolveInput TargetInput(Vector3 targetPoint, int generation = 1) =>
            new()
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                TargetPoint = targetPoint,
                HasTarget = true,
                Engagement = 1f,
                FixationLiveliness = 1f,
                GenerationId = generation,
                ApplyToBones = true
            };

        /// <summary>A point at <paramref name="yawDegrees" /> from the root forward, 2 m out.</summary>
        private Vector3 TargetAtYaw(float yawDegrees)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            return EyeCenter + new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians)) * 2f;
        }

        /// <summary>
        ///     The measured defect, reduced to a test: look away at something, then be given a new
        ///     target while the head is turning back toward it. This is what a Point At release
        ///     does, and it is the case the staircase appeared in.
        /// </summary>
        /// <remarks>
        ///     The head must be turning <em>through</em> the acquisition for this to bite. A
        ///     settled fixation does not expose it, because the fixation branch recomputes its
        ///     goal every frame and quietly tracks the head out. It is the two branches that hold
        ///     the angle still — the latency wait and the ballistic flight — that lose the target,
        ///     and reaching them needs a real re-target mid-turn.
        /// </remarks>
        [Test]
        public void RetargetWhileTheHeadIsTurning_AcquiresInOneSaccade()
        {
            const float awayYaw = 40f;
            const float headRateDegreesPerSecond = 75f;

            // Settle on something off to the side, head turned to it — the pointing pose.
            Vector3 away = TargetAtYaw(awayYaw);
            _head.localRotation = Quaternion.AngleAxis(awayYaw, Vector3.up);
            EyeSolveInput awayInput = TargetInput(away, generation: 1);
            for (int i = 0; i < Mathf.CeilToInt(0.8f / Dt); i++) _solver.Solve(in awayInput);

            // Now the point is released: a new target straight ahead, and the head starts back.
            Vector3 ahead = TargetAtYaw(0f);
            EyeSolveInput input = TargetInput(ahead, generation: 2);

            int saccades = 0;
            bool acquired = false;
            float worstErrorAfterAcquisition = 0f;
            float settleSeconds = 0f;
            float headYaw = awayYaw;

            int steps = Mathf.CeilToInt(1.2f / Dt);
            for (int i = 0; i < steps; i++)
            {
                // The head travels a little each frame, the way the head lane drives it — not in
                // one jump between solves.
                headYaw = Mathf.Max(0f, headYaw - headRateDegreesPerSecond * Dt);
                _head.localRotation = Quaternion.AngleAxis(headYaw, Vector3.up);

                _solver.Solve(in input);

                if (_solver.SaccadeStartedAmplitude > 0f) saccades++;

                float error = _solver.ContactErrorDegrees;
                if (float.IsNaN(error)) continue;

                if (!acquired)
                {
                    settleSeconds += Dt;
                    if (error < 2f) acquired = true;
                    continue;
                }

                worstErrorAfterAcquisition = Mathf.Max(worstErrorAfterAcquisition, error);
            }

            Assert.IsTrue(acquired, "The eyes must reach the target during the shift.");

            Assert.That(worstErrorAfterAcquisition, Is.LessThan(SettledToleranceDegrees),
                "Once the gaze has reached a stationary target, the head continuing to turn must " +
                "not carry the eyes back off it. A large value here is the staircase: the eye " +
                "state is held in head space while the head moves, so the head undoes each " +
                "saccade and another has to be issued.");

            Assert.That(saccades, Is.LessThanOrEqualTo(1),
                "One stationary target is one decision to look, so it costs one saccade. Extra " +
                "saccades here are corrections for a target the eyes had already reached and " +
                "were then dragged off — the measured defect cost three.");

            Assert.That(settleSeconds, Is.LessThan(0.45f),
                "A gaze shift of this size settles in roughly a reaction time plus a saccade, " +
                "not in the three quarters of a second a staircase of corrections takes.");
        }

        /// <summary>
        ///     The same invariant with the head turning <em>away</em> from a target the eyes can
        ///     still reach: the eyes must give up orbit eccentricity one-for-one to stay on it.
        /// </summary>
        [Test]
        public void HeadTurningUnderASettledFixation_HoldsTheWorldPoint()
        {
            Vector3 target = TargetAtYaw(0f);
            EyeSolveInput input = TargetInput(target);

            for (int i = 0; i < Mathf.CeilToInt(0.6f / Dt); i++) _solver.Solve(in input);
            Assert.That(_solver.ContactErrorDegrees, Is.LessThan(2f),
                "Precondition: the eyes start this test settled on the target.");

            float eyeYawBefore = _solver.LeftEyeAngles.x;

            const float headTurnDegrees = 15f;
            float headYaw = 0f;
            int steps = Mathf.CeilToInt(0.4f / Dt);
            for (int i = 0; i < steps; i++)
            {
                headYaw = Mathf.Min(headTurnDegrees, headYaw + 60f * Dt);
                _head.localRotation = Quaternion.AngleAxis(headYaw, Vector3.up);
                _solver.Solve(in input);

                Assert.That(_solver.ContactErrorDegrees, Is.LessThan(SettledToleranceDegrees),
                    "The gaze must stay on the target for every frame of the head's turn, not " +
                    "drift off and be recovered afterwards.");
            }

            // The head turned 15° away, so the eyes must have taken on ~15° of orbit
            // eccentricity in the other direction to keep aiming at the same point.
            Assert.That(_solver.LeftEyeAngles.x, Is.EqualTo(eyeYawBefore - headTurnDegrees).Within(3f),
                "Holding a world point while the head turns 15° off it means the eye-in-head " +
                "angle must move by about 15° the other way.");
        }

        /// <summary>
        ///     An ambient fixation is authored in the character's frame and the eyes take the part
        ///     of it the head has not: as the head turns onto the fixation, the eyes give the angle
        ///     up and come back to centre. That is what stops an idle look parking the eyes at the
        ///     corner of the socket (the head's follow share and the eyes' whole angle used to
        ///     ADD, and the character looked past its own fixation). This test used to assert the
        ///     opposite — eyes riding with the head — and was left red when the semantic changed.
        /// </summary>
        /// <remarks>
        ///     This replaced an assertion that ambient angles were head-relative and should ride
        ///     with the head. That premise put the head's share and the eyes' whole angle on top
        ///     of each other, which is the sideways stare fixed in
        ///     <c>IdleLookEyeComfortTests</c>.
        /// </remarks>
        [Test]
        public void AmbientExploration_IsTheResidualTheHeadHasNotTaken()
        {
            var input = new EyeSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                HasTarget = false,
                AmbientActive = true,
                AmbientAngles = new Vector2(18f, 0f),
                Engagement = 0f,
                FixationLiveliness = 1f,
                GenerationId = 1,
                ApplyToBones = true
            };

            for (int i = 0; i < Mathf.CeilToInt(0.8f / Dt); i++) _solver.Solve(in input);
            float settled = _solver.LeftEyeAngles.x;
            Assert.That(settled, Is.EqualTo(18f).Within(3f),
                "Precondition: the eyes settle on the ambient angle.");

            float headYaw = 0f;
            for (int i = 0; i < Mathf.CeilToInt(0.3f / Dt); i++)
            {
                headYaw = Mathf.Min(20f, headYaw + 70f * Dt);
                _head.localRotation = Quaternion.AngleAxis(headYaw, Vector3.up);
                _solver.Solve(in input);
            }

            // The head has turned 20° onto an 18° fixation: the eyes are left holding −2°.
            Assert.That(_solver.LeftEyeAngles.x, Is.EqualTo(18f - 20f).Within(3f),
                "The eyes take the part of the fixation the head has not; once the head has " +
                "turned onto it they are back near centre, not still 18° out in the socket.");
        }

        /// <summary>
        ///     A target the head has turned away from further than the eyes can reach must still
        ///     saturate the oculomotor clamp. The reflex holds the gaze where it physically can;
        ///     it does not grant the eyes range they do not have.
        /// </summary>
        [Test]
        public void HeadTurningPastTheOculomotorRange_StillSaturatesTheClamp()
        {
            Vector3 target = TargetAtYaw(0f);
            EyeSolveInput input = TargetInput(target);

            for (int i = 0; i < Mathf.CeilToInt(0.6f / Dt); i++) _solver.Solve(in input);

            // Turn the head 70° AWAY — far past the 35° oculomotor range.
            float headYaw = 0f;
            for (int i = 0; i < Mathf.CeilToInt(1f / Dt); i++)
            {
                headYaw = Mathf.Min(70f, headYaw + 90f * Dt);
                _head.localRotation = Quaternion.AngleAxis(headYaw, Vector3.up);
                _solver.Solve(in input);
            }

            Assert.That(Mathf.Abs(_solver.LeftEyeAngles.x),
                Is.LessThanOrEqualTo(_profile.EyeMaxYawDegrees + 0.5f),
                "The eyes must never exceed their oculomotor range, however far the head turns.");
            Assert.That(_solver.ContactErrorDegrees, Is.GreaterThan(10f),
                "Past the eyes' reach the gaze genuinely is dragged off the target. That is real " +
                "and must be preserved — the reflex is unity gain, not unlimited range.");
        }
    }
}
