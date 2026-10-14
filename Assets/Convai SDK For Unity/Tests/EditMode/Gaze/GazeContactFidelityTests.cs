using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Contact-fidelity diagnostics: <see cref="EyeSolver.ContactErrorDegrees" /> and its
    ///     surfacing through <see cref="ConvaiGazeController.CaptureSnapshot()" />.
    /// </summary>
    public sealed class GazeContactFidelityTests
    {
        private const float Dt = 1f / 60f;

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

        private EyeSolveInput TargetInput(Vector3 targetPoint, bool hasTarget = true, float engagement = 1f, int generation = 1) =>
            new()
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                TargetPoint = targetPoint,
                HasTarget = hasTarget,
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

        private Vector3 EyeCenter => (_leftEye.position + _rightEye.position) * 0.5f;

        [Test]
        public void ReachableTarget_SettlesToLowContactError()
        {
            Vector3 target = EyeCenter + new Vector3(Mathf.Sin(20f * Mathf.Deg2Rad), 0f, Mathf.Cos(20f * Mathf.Deg2Rad)) * 2f;

            // Reaction latency + main-sequence flight + pursuit settle.
            SolveFor(2f, TargetInput(target));

            Assert.That(_solver.ContactErrorDegrees, Is.LessThanOrEqualTo(1.5f),
                "A reachable static target must be landed within tight contact fidelity after settling.");
        }

        [Test]
        public void UnreachableTarget_KeepsResidualContactError()
        {
            // 70° yaw, far outside the default 35° oculomotor range, with the head not helping.
            Vector3 target = EyeCenter + new Vector3(Mathf.Sin(70f * Mathf.Deg2Rad), 0f, Mathf.Cos(70f * Mathf.Deg2Rad)) * 2f;

            SolveFor(1.5f, TargetInput(target));

            Assert.That(_solver.ContactErrorDegrees, Is.GreaterThan(5f),
                "A genuinely unreachable target must keep a residual contact error even after settling.");
        }

        [Test]
        public void NoTarget_ReportsNaN()
        {
            SolveFor(0.5f, TargetInput(Vector3.zero, hasTarget: false));

            Assert.IsTrue(float.IsNaN(_solver.ContactErrorDegrees),
                "Contact error must be NaN while disengaged (no target).");
        }

        [Test]
        public void CaptureSnapshot_BeforeRuntime_ReportsNaN()
        {
            var root = new GameObject("GazeContactFidelityTestCharacter");
            try
            {
                root.AddComponent<ConvaiCharacter>();
                ConvaiGazeController controller = root.AddComponent<ConvaiGazeController>();

                var snapshot = controller.CaptureSnapshot();

                Assert.IsTrue(float.IsNaN(snapshot.ContactErrorDegrees),
                    "A fresh controller with no engaged target must report NaN contact error.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
