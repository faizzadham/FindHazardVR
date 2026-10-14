using System.Reflection;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Animation;
using Convai.Runtime.Animation.ProceduralPose;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class EyeSolverTests
    {
        private const float Dt = 1f / 120f;

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

        private EyeSolveInput TargetInput(Vector3 targetPoint, float engagement = 1f, int generation = 1) =>
            new()
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                TargetPoint = targetPoint,
                HasTarget = true,
                Engagement = engagement,
                FixationLiveliness = 1f,
                GenerationId = generation,
                ApplyToBones = true
            };

        private void SolveFor(float seconds, EyeSolveInput input)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
                _solver.Solve(in input);
        }

        [Test]
        public void GazeReferenceFrame_UsesCalibratedAxesForYawAndPitch()
        {
            var frame = new GazeReferenceFrame(Vector3.right, Vector3.up);

            Assert.IsTrue(GazeSolverMath.TryGetDirectionYawPitch(frame, Vector3.forward,
                out float yaw, out float pitch));
            Assert.That(yaw, Is.EqualTo(-90f).Within(0.001f));
            Assert.That(pitch, Is.EqualTo(0f).Within(0.001f));

            Vector3 direction = GazeSolverMath.DirectionFromYawPitch(frame, 0f, 0f);
            Assert.That(Vector3.Angle(Vector3.right, direction), Is.LessThan(0.001f));
        }

        [Test]
        public void CalibratedStandardRigBinding_NonIdenticalEyeAxes_ConvergeOnSharedTarget()
        {
            GameObject host = new GameObject("CalibratedRig");
            try
            {
                EmbodimentContext context = host.AddComponent<EmbodimentContext>();
                StandardRigBinding binding = host.AddComponent<StandardRigBinding>();
                Transform head = NewChild(host.transform, "Head", new Vector3(0f, 1.6f, 0f));
                Transform left = NewChild(head, "LeftEye", new Vector3(-0.03f, 1.7f, 0.08f));
                Transform right = NewChild(head, "RightEye", new Vector3(0.03f, 1.7f, 0.08f));

                SetPrivate(binding, "headOverride", head);
                SetPrivate(binding, "leftEyeOverride", left);
                SetPrivate(binding, "rightEyeOverride", right);
                SetPrivate(binding, "gazeAxisCalibrationEnabled", true);
                SetPrivate(binding, "gazeRootForwardLocal", Vector3.right);
                SetPrivate(binding, "gazeRootUpLocal", Vector3.up);
                SetPrivate(binding, "leftEyeForwardLocal", Vector3.forward);
                SetPrivate(binding, "rightEyeForwardLocal", Vector3.right);
                binding.Rebuild();

                var chain = new GazeChainCalibration();
                chain.Bind(context, host.transform);
                Assert.IsTrue(chain.HasAxisCalibration);

                var solver = new EyeSolver();
                EyeSolveInput input = new()
                {
                    Chain = chain, Profile = _profile, DeltaTime = Dt, HasTarget = true,
                    TargetPoint = new Vector3(5f, 1.7f, 0f), Engagement = 1f,
                    FixationLiveliness = 0f, GenerationId = 42, ApplyToBones = true
                };
                for (int i = 0; i < 240; i++) solver.Solve(in input);

                Vector3 targetLeft = input.TargetPoint - left.position;
                Vector3 targetRight = input.TargetPoint - right.position;
                Assert.That(Vector3.Angle(left.TransformDirection(Vector3.forward), targetLeft), Is.LessThan(4f));
                Assert.That(Vector3.Angle(right.TransformDirection(Vector3.right), targetRight), Is.LessThan(4f));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CalibratedBlendshapeVergence_UsesCalibratedLateralAxis()
        {
            GameObject host = new GameObject("CalibratedBlendshapeRig");
            try
            {
                EmbodimentContext context = host.AddComponent<EmbodimentContext>();
                StandardRigBinding binding = host.AddComponent<StandardRigBinding>();
                Transform head = NewChild(host.transform, "Head", new Vector3(0f, 1.6f, 0f));
                SetPrivate(binding, "headOverride", head);
                SetPrivate(binding, "gazeAxisCalibrationEnabled", true);
                SetPrivate(binding, "gazeRootForwardLocal", Vector3.right);
                SetPrivate(binding, "gazeRootUpLocal", Vector3.up);
                binding.Rebuild();

                var chain = new GazeChainCalibration();
                chain.Bind(context, host.transform);
                Assert.IsTrue(chain.HasAxisCalibration);
                Assert.IsFalse(chain.HasEyeBones);

                var solver = new EyeSolver();
                EyeSolveInput input = new()
                {
                    Chain = chain, Profile = _profile, DeltaTime = Dt, HasTarget = true,
                    TargetPoint = new Vector3(0.35f, 1.68f, 0f), Engagement = 1f,
                    FixationLiveliness = 0f, GenerationId = 43, LookShapesActive = true
                };
                for (int i = 0; i < 240; i++) solver.Solve(in input);

                Assert.That(solver.LeftEyeAngles.x, Is.GreaterThan(solver.RightEyeAngles.x),
                    "With root forward +X and calibrated lateral axis -Z, the synthetic left eye must converge rightward and the right eye leftward.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        ///     A rig whose gaze frame is calibrated to something other than the root's +Z must
        ///     still be aimed in ITS frame when the write goes through the shared compositor.
        /// </summary>
        /// <remarks>
        ///     This used to be impossible, and the test used to assert the workaround: the
        ///     compositor's aim entry took an angle pair plus a Transform, so a calibrated frame
        ///     could not be expressed to it and gaze fell back to a second, private write guard
        ///     over bones the compositor already owned. The entry now takes composed deltas —
        ///     the frame is resolved on the gaze side, where it is known — so the fallback is
        ///     gone and there is one guard again. What is asserted is the property that mattered
        ///     all along: the bone ends up aimed around the calibrated axes.
        /// </remarks>
        [Test]
        public void CalibratedTorso_WritesItsOwnFrame_ThroughTheSharedCompositor()
        {
            GameObject host = new GameObject("CalibratedTorsoRig");
            try
            {
                EmbodimentContext context = host.AddComponent<EmbodimentContext>();
                StandardRigBinding binding = host.AddComponent<StandardRigBinding>();
                Transform chest = NewChild(host.transform, "Chest", new Vector3(0f, 1.2f, 0f));
                Transform head = NewChild(chest, "Head", new Vector3(0f, 1.6f, 0f));
                SetPrivate(binding, "chestOverride", chest);
                SetPrivate(binding, "headOverride", head);
                SetPrivate(binding, "gazeAxisCalibrationEnabled", true);
                SetPrivate(binding, "gazeRootForwardLocal", Vector3.right);
                SetPrivate(binding, "gazeRootUpLocal", Vector3.up);
                binding.Rebuild();

                var chain = new GazeChainCalibration();
                chain.Bind(context, host.transform);
                var sink = new ProceduralPoseCompositor();
                sink.BindManual(chest, chest, null, null, null);
                Assert.IsTrue(sink.IsBound);

                var solver = new HeadTorsoSolver();
                var director = new GazeShiftDirector();
                var target = new Vector3(0f, 1.6f, 5f);
                for (int i = 0; i < 360; i++)
                {
                    // Stands in for the compositor's frame owner. Now that gaze routes its
                    // writes through the shared guard, its per-tick idempotence comes from that
                    // guard's once-per-frame restore rather than from a gaze-private one — and
                    // the compositor keys "once per frame" off Time.frameCount, which does not
                    // advance inside an EditMode loop. Without this the deltas integrate 360
                    // times: the guard doing exactly its job on a caller pretending that 360
                    // ticks are one frame.
                    sink.RestoreStaleWrites();

                    chain.TryMeasureShift(target, out GazeShiftMeasurement measurement);
                    HeadTorsoSolveInput input = new()
                    {
                        Chain = chain, Profile = _profile, DeltaTime = Dt, HasTarget = true,
                        TargetPoint = target, Engagement = 1f,
                        Measurement = measurement,
                        Plan = director.Plan(in measurement, _profile, 1f, 1f,
                            torsoAvailable: true, feetAvailable: false, 44, Dt),
                        PoseSink = sink
                    };
                    solver.Solve(in input);
                }

                Assert.That(solver.TorsoAngles.sqrMagnitude, Is.GreaterThan(0.01f));
                Assert.That(Quaternion.Angle(Quaternion.identity, chest.localRotation), Is.GreaterThan(0.01f),
                    "The chest must actually be written.");

                Vector3 calibratedForward = host.transform.right;
                Vector3 calibratedUp = host.transform.up;
                Vector3 calibratedRight = Vector3.Cross(calibratedUp, calibratedForward).normalized;
                Quaternion expectedWrite = ProceduralPoseMath.AimSwing(
                    calibratedRight, calibratedUp, solver.TorsoAngles.x, solver.TorsoAngles.y);
                Assert.That(Quaternion.Angle(expectedWrite, chest.rotation), Is.LessThan(0.1f),
                    "The aim must be built around the calibrated +X-root frame, not the Transform's +Z.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static void SetPrivate(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
        }

        private Vector3 EyeCenter => (_leftEye.position + _rightEye.position) * 0.5f;

        [Test]
        public void FreshTarget_TriggersBallisticSaccade()
        {
            Vector3 target = EyeCenter + new Vector3(Mathf.Sin(20f * Mathf.Deg2Rad), 0f, Mathf.Cos(20f * Mathf.Deg2Rad)) * 2f;

            // A saccade launches only after the saccadic reaction latency — real eyes take
            // ~0.1–0.25 s to respond to a fresh target instead of jumping the same frame.
            EyeSolveInput input = TargetInput(target);
            float maxAmplitude = 0f;
            bool everSaccading = false;
            int steps = Mathf.CeilToInt((_profile.SaccadeReactionSeconds + 0.05f) / Dt);
            for (int i = 0; i < steps; i++)
            {
                _solver.Solve(in input);
                maxAmplitude = Mathf.Max(maxAmplitude, _solver.SaccadeStartedAmplitude);
                everSaccading |= _solver.PhaseName == "Saccade";
            }

            Assert.That(maxAmplitude, Is.GreaterThan(15f),
                "A fresh 20° target must fire a ballistic saccade of comparable amplitude.");

            // Observed across the window rather than sampled at the end of it. A 20° saccade's
            // main-sequence duration is short enough to start and finish inside this window, so
            // the final frame is legitimately back in Pursuit — the phase reported once the eyes
            // are on target and holding. Sampling the last frame made this test assert that the
            // saccade was still running at an arbitrary moment, which is not the contract; how
            // long it runs belongs to Saccade_FollowsMainSequenceDuration.
            Assert.IsTrue(everSaccading,
                "A fresh target must be acquired with a ballistic saccade at some point in the " +
                "window, not glided onto in Pursuit.");
        }

        [Test]
        public void Saccade_FollowsMainSequenceDuration()
        {
            Vector3 target = EyeCenter + new Vector3(Mathf.Sin(20f * Mathf.Deg2Rad), 0f, Mathf.Cos(20f * Mathf.Deg2Rad)) * 2f;
            float expectedDuration = _profile.SaccadeMinDurationSeconds + _profile.SaccadeDurationPerDegree * 20f;

            EyeSolveInput input = TargetInput(target);
            SolveFor(_profile.SaccadeReactionSeconds, input); // saccadic latency: the jump arms first
            SolveFor(expectedDuration * 0.5f, input);
            float midwayYaw = _solver.LeftEyeAngles.x;
            Assert.That(midwayYaw, Is.GreaterThan(1f).And.LessThan(19.5f),
                "Halfway through the main-sequence duration the eye is in flight.");

            SolveFor(expectedDuration, input);
            Assert.That(_solver.LeftEyeAngles.x, Is.EqualTo(20f).Within(1.5f),
                "The saccade (with its natural undershoot closed by pursuit) lands on target " +
                "within the main-sequence duration.");
        }

        [Test]
        public void OcularMotorRange_ClampsExtremeTargets()
        {
            Vector3 target = EyeCenter + new Vector3(Mathf.Sin(70f * Mathf.Deg2Rad), 0f, Mathf.Cos(70f * Mathf.Deg2Rad)) * 2f;

            SolveFor(1f, TargetInput(target));

            Assert.That(Mathf.Abs(_solver.LeftEyeAngles.x), Is.LessThanOrEqualTo(_profile.EyeMaxYawDegrees + 0.1f),
                "Eyes must never rotate past the oculomotor range.");
        }

        [Test]
        public void NearTarget_ConvergesEyes()
        {
            Vector3 target = EyeCenter + Vector3.forward * 0.3f;

            SolveFor(1f, TargetInput(target));

            float leftYaw = _solver.LeftEyeAngles.x;
            float rightYaw = _solver.RightEyeAngles.x;
            Assert.That(leftYaw, Is.GreaterThan(rightYaw + 2f),
                "A 30 cm target must visibly converge the eyes (left rotates right, right rotates left).");
        }

        [Test]
        public void FarTarget_HasNegligibleVergence()
        {
            Vector3 target = EyeCenter + Vector3.forward * 6f;

            SolveFor(1f, TargetInput(target));

            Assert.That(Mathf.Abs(_solver.LeftEyeAngles.x - _solver.RightEyeAngles.x), Is.LessThan(1f),
                "At conversation distance the eyes are effectively parallel.");
        }

        [Test]
        public void Vergence_RespectsConvergenceClamp()
        {
            // 2 cm in front of the eyes — far closer than vergenceMinDistance.
            Vector3 target = EyeCenter + Vector3.forward * 0.02f;

            SolveFor(1.5f, TargetInput(target));

            float convergence = Mathf.Abs(_solver.LeftEyeAngles.x - _solver.RightEyeAngles.x) * 0.5f;
            Assert.That(convergence, Is.LessThanOrEqualTo(_profile.MaxConvergenceDegrees + 0.5f),
                "The cross-eye clamp must cap convergence for absurdly close targets.");
        }

        [Test]
        public void SlowTargetMotion_UsesPursuitNotSaccades()
        {
            Vector3 target = EyeCenter + Vector3.forward * 2f;
            SolveFor(0.5f, TargetInput(target)); // acquire

            int saccades = 0;
            for (int i = 0; i < 240; i++)
            {
                // ~8°/s of angular motion at 2 m — well below the pursuit threshold.
                target += new Vector3(0.28f * 2f * Mathf.Deg2Rad * 8f * Dt, 0f, 0f);
                EyeSolveInput input = TargetInput(target);
                _solver.Solve(in input);
                if (_solver.SaccadeStartedAmplitude > 0f) saccades++;
            }

            Assert.That(saccades, Is.EqualTo(0),
                "Slow target drift must be tracked with smooth pursuit, not saccade spam.");
            Assert.That(_solver.PhaseName, Is.EqualTo("Pursuit").Or.EqualTo("Fixating"));
        }

        [Test]
        public void ZeroEngagement_KeepsEyesAtRest()
        {
            Vector3 target = EyeCenter + new Vector3(1.5f, 0f, 1.5f);

            SolveFor(1f, TargetInput(target, engagement: 0f));

            Assert.That(Mathf.Abs(_solver.LeftEyeAngles.x), Is.LessThan(1.2f),
                "Without engagement the eyes stay near rest (micro-motion excluded here).");
        }

        [Test]
        public void EyesRecenterInOrbit_WhenHeadTurnsTowardTarget()
        {
            Vector3 target = EyeCenter + new Vector3(Mathf.Sin(25f * Mathf.Deg2Rad), 0f, Mathf.Cos(25f * Mathf.Deg2Rad)) * 2f;
            SolveFor(0.5f, TargetInput(target));
            float offAxisYaw = Mathf.Abs(_solver.LeftEyeAngles.x);
            Assert.That(offAxisYaw, Is.GreaterThan(15f));

            // Simulate the head catching up: rotate the head 25° toward the target.
            _head.rotation = Quaternion.AngleAxis(25f, Vector3.up) * _head.rotation;
            SolveFor(0.5f, TargetInput(target));

            Assert.That(Mathf.Abs(_solver.LeftEyeAngles.x), Is.LessThan(4f),
                "As the head catches up the eyes must re-center in the orbit (VOR behavior).");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PartialEyeRig_DoesNotActuateAnUnpairedBone(bool useLeftEye)
        {
            Transform availableEye = useLeftEye ? _leftEye : _rightEye;
            _chain.BindManual(
                _root.transform, null, null, null, _head,
                useLeftEye ? _leftEye : null,
                useLeftEye ? null : _rightEye);
            Quaternion rest = availableEye.localRotation;
            Vector3 target = availableEye.position + new Vector3(0.7f, 0f, 2f);

            SolveFor(1f, TargetInput(target));

            Assert.IsFalse(_chain.HasEyeBones);
            Assert.That(Quaternion.Angle(rest, availableEye.localRotation), Is.LessThan(0.01f),
                "A partial binocular mapping must degrade to head-only instead of creating uncanny asymmetry.");
        }

        [Test]
        public void RestoreEyeRest_ReleasesPreviouslyDrivenBonesBeforeHotRebind()
        {
            Quaternion leftRest = _leftEye.localRotation;
            Quaternion rightRest = _rightEye.localRotation;
            Vector3 target = EyeCenter + new Vector3(0.7f, 0f, 2f);
            SolveFor(1f, TargetInput(target));
            Assert.That(Quaternion.Angle(leftRest, _leftEye.localRotation), Is.GreaterThan(2f));

            _chain.RestoreEyeRest();

            Assert.That(Quaternion.Angle(leftRest, _leftEye.localRotation), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(rightRest, _rightEye.localRotation), Is.LessThan(0.01f));
        }
    }
}
