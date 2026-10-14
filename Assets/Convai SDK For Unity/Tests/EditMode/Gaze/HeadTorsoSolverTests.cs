using System.Reflection;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class HeadTorsoSolverTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private HeadTorsoSolver _solver;
        private GazeChainCalibration _chain;

        // The solver is an actuator: it executes the share the ladder allocates. These tests
        // therefore drive the same two steps the controller does — measure once, plan once —
        // rather than hand-assembling a share, which would test a pipeline that does not exist.
        private GazeShiftDirector _shiftDirector;
        private float _headContribution = 1f;
        private int _generation = 1;

        private GameObject _root;
        private Transform _chest;
        private Transform _upperChest;
        private Transform _neck;
        private Transform _head;
        private Quaternion _chestRest;
        private Quaternion _upperChestRest;
        private Quaternion _neckRest;
        private Quaternion _headRest;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _solver = new HeadTorsoSolver();
            _chain = new GazeChainCalibration();
            _shiftDirector = new GazeShiftDirector();
            _headContribution = 1f;
            _generation = 1;

            _root = new GameObject("Root");
            _chest = NewChild(_root.transform, "Chest", new Vector3(0f, 1.0f, 0f));
            _upperChest = NewChild(_chest, "UpperChest", new Vector3(0f, 1.2f, 0f));
            _neck = NewChild(_upperChest, "Neck", new Vector3(0f, 1.5f, 0f));
            _head = NewChild(_neck, "Head", new Vector3(0f, 1.65f, 0f));

            _chain.BindManual(_root.transform, _chest, _upperChest, _neck, _head, null, null);

            _chestRest = _chest.localRotation;
            _upperChestRest = _upperChest.localRotation;
            _neckRest = _neck.localRotation;
            _headRest = _head.localRotation;
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

        private HeadTorsoSolveInput TargetInput(
            Vector3 targetPoint, float engagement = 1f, float headContribution = 1f, int generation = 1)
        {
            _headContribution = headContribution;
            _generation = generation;
            return new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                TargetPoint = targetPoint,
                HasTarget = true,
                Engagement = engagement
            };
        }

        /// <summary>
        ///     One frame of the real pipeline: measure the shift from the rig, divide it across
        ///     the ladder, then actuate. The plan is rebuilt every frame because the ladder's
        ///     onset cascade advances with the shift clock — reusing a plan from frame zero
        ///     would hold every rung at its pre-onset participation forever.
        /// </summary>
        private void Solve(ref HeadTorsoSolveInput input)
        {
            if (input.HasTarget && _chain.TryMeasureShift(input.TargetPoint, out GazeShiftMeasurement measurement))
            {
                input.Measurement = measurement;
                input.Plan = _shiftDirector.Plan(
                    in measurement, _profile, input.Engagement, _headContribution,
                    torsoAvailable: true, feetAvailable: true, _generation, Dt);
            }

            _solver.Solve(in input);
        }

        /// <summary>
        ///     Emulates the Animator re-posing the skeleton at the start of every frame.
        ///     The solver layers swing deltas ON TOP of the animated pose; without this
        ///     reset the per-tick deltas accumulate on the bones and the measured heading
        ///     wraps arbitrarily — something that never happens under a live Animator.
        /// </summary>
        private void ResetAnimatedPose()
        {
            _chest.localRotation = _chestRest;
            _upperChest.localRotation = _upperChestRest;
            _neck.localRotation = _neckRest;
            _head.localRotation = _headRest;
        }

        private void SolveFor(float seconds, HeadTorsoSolveInput input)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                ResetAnimatedPose();
                Solve(ref input);
            }
        }

        private static float MeasuredHeadYaw(Transform head, Transform root)
        {
            Vector3 local = root.InverseTransformDirection(head.forward);
            return Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        }

        [Test]
        public void Solve_TurnsHeadTowardOffAxisTarget()
        {
            Vector3 target = _head.position + new Vector3(2f, 0f, 2f); // 45° to the right

            SolveFor(2f, TargetInput(target));

            float headYaw = MeasuredHeadYaw(_head, _root.transform);
            Assert.That(headYaw, Is.GreaterThan(15f),
                "The head chain must visibly turn toward a 45° target.");
            Assert.That(_solver.TargetYawError, Is.EqualTo(45f).Within(1.5f));
        }

        [Test]
        public void Solve_RespectsHeadYawLimit()
        {
            Vector3 target = _head.position + new Vector3(2f, 0f, -0.5f); // ~104° right — beyond limits

            SolveFor(3f, TargetInput(target));

            Vector2 head = _solver.HeadAngles;
            Assert.That(Mathf.Abs(head.x), Is.LessThanOrEqualTo(_profile.MaxHeadYawDegrees + 0.5f),
                "Head yaw must never exceed the profile limit.");
        }

        [Test]
        public void Solve_BelowRecruitmentThreshold_KeepsHeadNearlyStill()
        {
            // ~3.4° off-axis: well below the 10° recruitment threshold → eyes-only territory.
            Vector3 target = _head.position + new Vector3(0.12f, 0f, 2f);

            SolveFor(2f, TargetInput(target));

            Assert.That(Mathf.Abs(_solver.HeadAngles.x), Is.LessThan(1f),
                "Small gaze amplitudes are handled by the eyes alone; the head barely recruits.");
        }

        [Test]
        public void Solve_ZeroEngagement_WritesNothing()
        {
            Quaternion before = _head.localRotation;
            Vector3 target = _head.position + new Vector3(2f, 0f, 2f);

            SolveFor(1f, TargetInput(target, engagement: 0f));

            Assert.That(_head.localRotation, Is.EqualTo(before),
                "At zero engagement the solver must not touch the animated pose.");
            Assert.That(_solver.HeadAngles.x, Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void Solve_TorsoRecruitsForLargeAmplitudes()
        {
            Vector3 target = _head.position + new Vector3(2f, 0f, 0.4f); // ~79° right

            SolveFor(3f, TargetInput(target));

            Assert.That(Mathf.Abs(_solver.TorsoAngles.x), Is.GreaterThan(2f),
                "Large amplitudes must recruit the torso.");
            Assert.That(Mathf.Abs(_solver.TorsoAngles.x), Is.LessThanOrEqualTo(_profile.MaxTorsoYawDegrees + 0.5f));
        }

        [Test]
        public void Solve_SmallAmplitude_DoesNotRecruitTorso()
        {
            Vector3 target = _head.position + new Vector3(0.7f, 0f, 2f); // ~19° right

            SolveFor(2f, TargetInput(target));

            Assert.That(Mathf.Abs(_solver.TorsoAngles.x), Is.LessThan(0.5f),
                "Conversational amplitudes stay in the neck/head.");
        }

        [Test]
        public void Solve_HeadLagsBehindNewGeneration()
        {
            Vector3 target = _head.position + new Vector3(2f, 0f, 2f);

            // Two ticks after a fresh generation we are still inside the latency window
            // (default 0.25 s): the head must not have started chasing yet.
            HeadTorsoSolveInput input = TargetInput(target, generation: 7);
            ResetAnimatedPose();
            Solve(ref input);
            ResetAnimatedPose();
            Solve(ref input);

            Assert.That(Mathf.Abs(_solver.HeadAngles.x), Is.LessThan(0.5f),
                "Eye-lead/head-lag: the head waits out the latency window on re-targets.");
        }

        [Test]
        public void Solve_BodyTurnActive_RelievesHeadOffset()
        {
            // bodyTurnHeadRelief's default changed 0.4 -> 1: at 1 the head KEEPS its aim during
            // a body turn instead of relaxing off it (see the field's tooltip). The relief
            // mechanic itself — a head letting go of some of its turn when configured below 1 —
            // still needs coverage, so this test drives it explicitly rather than through the
            // (now no-op) default.
            SetBodyTurnHeadRelief(_profile, 0.4f);

            Vector3 target = _head.position + new Vector3(2f, 0f, 0.4f); // ~79° right

            SolveFor(2f, TargetInput(target));
            float pinned = Mathf.Abs(_solver.HeadAngles.x);
            Assert.That(pinned, Is.GreaterThan(20f), "Sanity: the head sits near its limit.");

            _solver.Reset();
            HeadTorsoSolveInput turning = TargetInput(target);
            turning.BodyTurnActive = true;
            SolveFor(2f, turning);

            Assert.That(Mathf.Abs(_solver.HeadAngles.x), Is.LessThan(pinned * 0.7f),
                "During a body turn the head must relax off its limit and ride the turn " +
                "instead of staying pinned sideways.");
        }

        /// <summary>
        ///     The new default (1): a body turn must NOT pull the head off its aim — this is the
        ///     behaviour the 0.4 -> 1 default change was made for.
        /// </summary>
        [Test]
        public void Solve_BodyTurnActive_DefaultProfileKeepsHeadAim()
        {
            Vector3 target = _head.position + new Vector3(2f, 0f, 0.4f); // ~79° right

            SolveFor(2f, TargetInput(target));
            float pinned = Mathf.Abs(_solver.HeadAngles.x);
            Assert.That(pinned, Is.GreaterThan(20f), "Sanity: the head sits near its limit.");

            _solver.Reset();
            HeadTorsoSolveInput turning = TargetInput(target);
            turning.BodyTurnActive = true;
            SolveFor(2f, turning);

            Assert.That(Mathf.Abs(_solver.HeadAngles.x), Is.GreaterThan(pinned * 0.9f),
                "With the default relief of 1 the head must keep its aim during a body turn " +
                "rather than retreating from the target.");
        }

        private static void SetBodyTurnHeadRelief(ConvaiGazeProfile profile, float value)
        {
            FieldInfo bodyTurnField = typeof(ConvaiGazeProfile).GetField(
                "bodyTurn", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(bodyTurnField, "ConvaiGazeProfile.bodyTurn was renamed; update this helper.");
            object bodyTurnSettings = bodyTurnField.GetValue(profile);
            FieldInfo reliefField = bodyTurnSettings.GetType().GetField(
                "bodyTurnHeadRelief", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(reliefField, "bodyTurnHeadRelief was renamed; update this helper.");
            reliefField.SetValue(bodyTurnSettings, value);
        }

        [Test]
        public void Solve_StaticPoseWithoutAnimator_DoesNotAccumulateRotation()
        {
            // No ResetAnimatedPose between ticks — emulates Body Animation (or any
            // Animator) being disabled, so nothing re-poses the skeleton. The pose-write
            // guard must unwind the previous frame's write before each solve; without it
            // the swing deltas integrate and the head spins away.
            Vector3 target = _head.position + new Vector3(2f, 0f, 2f); // 45° right
            HeadTorsoSolveInput input = TargetInput(target);

            int steps = Mathf.CeilToInt(3f / Dt);
            for (int i = 0; i < steps; i++)
                Solve(ref input);

            float measured = MeasuredHeadYaw(_head, _root.transform);
            float solved = _solver.HeadAngles.x + _solver.TorsoAngles.x;
            Assert.That(measured, Is.EqualTo(solved).Within(1f),
                "The physically applied rotation must equal the solver's state — additive " +
                "writes over a static pose would integrate far beyond it.");
            Assert.That(Mathf.Abs(measured), Is.LessThanOrEqualTo(46f),
                "A static pose must never wind up past the 45° target amplitude.");
        }

        [Test]
        public void Solve_StaticPose_ReleaseRestoresOriginalPose()
        {
            Quaternion originalHead = _head.localRotation;
            Vector3 target = _head.position + new Vector3(2f, 0f, 2f);
            HeadTorsoSolveInput engaged = TargetInput(target);
            for (int i = 0; i < Mathf.CeilToInt(2f / Dt); i++)
                Solve(ref engaged);
            Assert.That(Mathf.Abs(_solver.HeadAngles.x), Is.GreaterThan(5f), "Sanity: gaze engaged.");

            var release = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                HasTarget = false,
                AmbientActive = false
                
            };
            for (int i = 0; i < Mathf.CeilToInt(4f / Dt); i++)
                Solve(ref release);

            Assert.That(Quaternion.Angle(originalHead, _head.localRotation), Is.LessThan(0.5f),
                "Releasing the target on a static pose must hand the head back to the " +
                "exact underlying pose — no residual gaze delta may stick.");
        }

        [Test]
        public void Solve_AnimatedHeadDip_IsCounteredAtFullEngagement()
        {
            // Target dead ahead at head height: amplitude ≈ 0, so head recruitment for the
            // gaze SHIFT is zero — but the stabilization reflex must still counter an
            // animator-driven head dip (talk clips bowing the head), otherwise the eyes
            // counter-stare upward against the dipped head: the classic horror look.
            Vector3 target = _head.position + _root.transform.forward * 2f;
            HeadTorsoSolveInput input = TargetInput(target);

            int steps = Mathf.CeilToInt(2f / Dt);
            for (int i = 0; i < steps; i++)
            {
                ResetAnimatedPose();
                _head.localRotation = _headRest * Quaternion.Euler(12f, 0f, 0f); // 12° dip
                Solve(ref input);
            }

            Assert.That(_solver.HeadAngles.y, Is.GreaterThan(6f),
                "The solver must counter most of the animated dip at full engagement.");

            Vector3 local = _root.transform.InverseTransformDirection(_head.forward);
            float measuredPitch = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            Assert.That(Mathf.Abs(measuredPitch), Is.LessThan(4f),
                "With the counter applied the head must sit nearly level on the target again.");
        }

        [Test]
        public void Solve_GestureOffset_MovesHead_EvenWhenAimSitsInsideStabilityBand()
        {
            // Target dead ahead → the raw aim sits inside the 2.5° stability band, so without
            // the gesture channel the head holds level. A 4° nod offset must still move it.
            Vector3 target = _head.position + _root.transform.forward * 2f;

            HeadTorsoSolveInput input = TargetInput(target);
            input.GestureOffset = new Vector2(0f, -4f); // downward nod, larger than the band

            SolveFor(1.5f, input);

            Assert.That(_solver.HeadAngles.y, Is.LessThan(-2.5f),
                "A gesture offset rides on top of the held aim, past the stability band.");
            Assert.That(_solver.HeadAngles.y, Is.EqualTo(-4f).Within(0.75f),
                "The head carries the full gesture amplitude.");
        }

        [Test]
        public void Solve_GestureOffset_IsNotFilteredByTheHeadSpring()
        {
            // The gesture channel layers on AFTER the smoothing springs. A sub-second nod
            // routed through the head goal would be low-passed to an invisible ripple (the
            // spring's smooth time sits right on the nod's frequency band) — this guards
            // against that regression: one solve tick must already carry the offset.
            Vector3 target = _head.position + _root.transform.forward * 2f;
            HeadTorsoSolveInput input = TargetInput(target);
            input.GestureOffset = new Vector2(0f, -4f);

            ResetAnimatedPose();
            Solve(ref input);

            Assert.That(_solver.HeadAngles.y, Is.EqualTo(-4f).Within(0.5f),
                "The gesture applies at full amplitude immediately — the springs must not " +
                "smooth (and thereby erase) the authored nod envelope.");
        }

        [Test]
        public void Solve_HeadOnlyRig_StillAims()
        {
            var chain = new GazeChainCalibration();
            chain.BindManual(_root.transform, null, null, null, _head, null, null);
            Vector3 target = _head.position + new Vector3(2f, 0f, 2f);

            var input = new HeadTorsoSolveInput
            {
                Chain = chain,
                Profile = _profile,
                DeltaTime = Dt,
                TargetPoint = target,
                HasTarget = true,
                Engagement = 1f
                
            };
            for (int i = 0; i < 120; i++)
            {
                ResetAnimatedPose();
                Solve(ref input);
            }

            Assert.That(MeasuredHeadYaw(_head, _root.transform), Is.GreaterThan(10f),
                "Rigs without a neck must fold the full head-chain rotation into the head bone.");
        }

        [Test]
        public void Solve_AmbientAngles_MoveHeadPartially()
        {
            var input = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                HasTarget = false,
                AmbientActive = true,
                AmbientAngles = new Vector2(20f, 0f)
                
            };
            for (int i = 0; i < 180; i++)
            {
                ResetAnimatedPose();
                Solve(ref input);
            }

            float expected = 20f * _profile.AmbientHeadFollow;
            Assert.That(_solver.HeadAngles.x, Is.EqualTo(expected).Within(1.5f),
                "Ambient exploration moves the head by the configured follow fraction.");
        }

        [Test]
        public void Solve_ReleaseReturnsHeadToAnimatedPose()
        {
            Vector3 target = _head.position + new Vector3(2f, 0f, 2f);
            SolveFor(2f, TargetInput(target));
            Assert.That(Mathf.Abs(_solver.HeadAngles.x), Is.GreaterThan(5f));

            var release = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                HasTarget = false,
                AmbientActive = false
                
            };
            for (int i = 0; i < 240; i++)
            {
                ResetAnimatedPose();
                Solve(ref release);
            }

            Assert.That(Mathf.Abs(_solver.HeadAngles.x), Is.LessThan(0.2f),
                "Without target or ambient life the head eases back to the animation.");
        }
    }
}
