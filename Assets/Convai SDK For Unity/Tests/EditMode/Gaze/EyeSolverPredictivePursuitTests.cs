using System.Reflection;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     E9 predictive pursuit: during smooth pursuit the eyes lead a moving target along its
    ///     measured velocity so the constant trailing error disappears, while static targets and
    ///     teleports are left untouched (no lead spike).
    /// </summary>
    public sealed class EyeSolverPredictivePursuitTests
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
            // Isolate the yaw channel: vergence would add a per-eye offset that muddies the
            // trailing-error measurement, and this test only cares about conjugate tracking.
            SetProfileSetting(_profile, "enableVergence", false);

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

        // Target on a fixed-radius arc about the eye center → exactly constant angular velocity.
        private Vector3 PointAtYaw(float yawDegrees, float radius = 3f)
        {
            float r = yawDegrees * Mathf.Deg2Rad;
            return EyeCenter + new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)) * radius;
        }

        private float TrueYaw(Vector3 target)
        {
            Vector3 dir = target - EyeCenter;
            return Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        }

        private EyeSolveInput Input(Vector3 target, int generation = 1, bool teleported = false) => new()
        {
            Chain = _chain,
            Profile = _profile,
            DeltaTime = Dt,
            TargetPoint = target,
            HasTarget = true,
            Engagement = 1f,
            FixationLiveliness = 1f,
            GenerationId = generation,
            Teleported = teleported,
            ApplyToBones = true
        };

        [Test]
        public void ConstantVelocityTarget_LeadReducesTrailingError()
        {
            float errorWithLead = SweepAndMeasureTrailingError(leadSeconds: 0.04f);
            float errorNoLead = SweepAndMeasureTrailingError(leadSeconds: 0f);

            Assert.That(errorNoLead, Is.GreaterThan(0.4f),
                "Sanity: an un-led constant-velocity pursuit must carry a measurable trailing error.");
            Assert.That(errorWithLead, Is.LessThan(errorNoLead * 0.75f),
                $"Predictive lead must cut the steady-state trailing error " +
                $"(with lead {errorWithLead:0.000}°, without {errorNoLead:0.000}°).");
        }

        private float SweepAndMeasureTrailingError(float leadSeconds)
        {
            _solver.Reset();
            SetProfileSetting(_profile, "pursuitLeadSeconds", leadSeconds);

            // Acquire a fixed off-center start so the sweep begins from a settled fixation.
            const float startYaw = 6f;
            for (int i = 0; i < 90; i++)
                _solver.Solve(Input(PointAtYaw(startYaw)));

            // Sweep outward at a constant 40°/s (lag well under the 5° catch-up threshold, so
            // this stays pure smooth pursuit) and average the trailing error over the tail.
            const float angularSpeed = 40f;
            float yaw = startYaw;
            float errorSum = 0f;
            int samples = 0;
            int saccades = 0;
            for (int i = 0; i < 60; i++)
            {
                yaw += angularSpeed * Dt;
                Vector3 target = PointAtYaw(yaw);
                _solver.Solve(Input(target));
                if (_solver.SaccadeStartedAmplitude > 0f) saccades++;

                if (i >= 40) // measure only once steady state is reached
                {
                    errorSum += Mathf.Abs(TrueYaw(target) - _solver.LeftEyeAngles.x);
                    samples++;
                }
            }

            Assert.That(saccades, Is.EqualTo(0),
                "The sweep must stay in smooth pursuit — a catch-up saccade would corrupt the measurement.");
            return errorSum / samples;
        }

        [Test]
        public void StaticTarget_LeadHasNoEffect()
        {
            Vector3 target = PointAtYaw(18f);

            var led = new EyeSolver();
            SetProfileSetting(_profile, "pursuitLeadSeconds", 0.04f);
            for (int i = 0; i < 150; i++) led.Solve(Input(target));
            Vector2 ledAngles = led.LeftEyeAngles;

            var unled = new EyeSolver();
            SetProfileSetting(_profile, "pursuitLeadSeconds", 0f);
            for (int i = 0; i < 150; i++) unled.Solve(Input(target));

            Assert.That(ledAngles.x, Is.EqualTo(unled.LeftEyeAngles.x).Within(1e-3f),
                "A static target moves at zero velocity, so the lead must be inert.");
            Assert.That(ledAngles.y, Is.EqualTo(unled.LeftEyeAngles.y).Within(1e-3f));
        }

        [Test]
        public void Teleport_ProducesNoLeadSpike()
        {
            SetProfileSetting(_profile, "pursuitLeadSeconds", 0.04f);

            // Acquire, then build a strong +yaw velocity so the lead is actively engaged.
            for (int i = 0; i < 90; i++) _solver.Solve(Input(PointAtYaw(6f)));
            float yaw = 6f;
            for (int i = 0; i < 40; i++)
            {
                yaw += 60f * Dt;
                _solver.Solve(Input(PointAtYaw(yaw)));
            }

            // Teleport (camera-cut semantics) to a static point. The velocity must reset, so the
            // eye settles exactly on it — a leaked velocity would bias the settle off-target.
            Vector3 settled = PointAtYaw(10f);
            float trueYaw = TrueYaw(settled);
            for (int i = 0; i < 150; i++)
                _solver.Solve(Input(settled, generation: 2, teleported: i == 0));

            Assert.That(_solver.LeftEyeAngles.x, Is.EqualTo(trueYaw).Within(0.4f),
                "After a teleport the tracked velocity must reset — no stale lead may bias the settle.");
        }

        /// <summary>
        ///     Writes one profile setting by name. The profile groups its settings into nested
        ///     blocks, so the field lives on the block rather than on the profile itself — which
        ///     block is the profile's business, not a test's.
        /// </summary>
        private static void SetProfileSetting(ConvaiGazeProfile profile, string settingName, object value)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

            foreach (FieldInfo block in typeof(ConvaiGazeProfile).GetFields(Flags))
            {
                FieldInfo setting = block.FieldType.GetField(settingName, Flags);
                if (setting == null) continue;

                setting.SetValue(block.GetValue(profile), value);
                return;
            }

            Assert.Fail($"ConvaiGazeProfile has no setting named {settingName}.");
        }
    }
}
