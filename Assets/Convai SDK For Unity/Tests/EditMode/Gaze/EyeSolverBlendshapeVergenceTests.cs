using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     E5 blendshape-only vergence: a rig with NO eye bones still converges on near targets
    ///     using eye positions synthesized from the head frame, and stays parallel at
    ///     conversation distance. (Bone-mode vergence is regression-guarded by
    ///     <see cref="EyeSolverTests" />, whose math is unchanged by the position refactor.)
    /// </summary>
    public sealed class EyeSolverBlendshapeVergenceTests
    {
        private const float Dt = 1f / 120f;

        private ConvaiGazeProfile _profile;
        private EyeSolver _solver;
        private GazeChainCalibration _chain;
        private GameObject _root;
        private Transform _head;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _solver = new EyeSolver();
            _chain = new GazeChainCalibration();

            _root = new GameObject("Root");
            _head = new GameObject("Head").transform;
            _head.SetParent(_root.transform, false);
            _head.position = new Vector3(0f, 1.65f, 0f);

            // No eye bones → blendshape backend; EyeCenterPosition falls back to the head pivot.
            _chain.BindManual(_root.transform, null, null, null, _head, null, null);
            Assert.IsFalse(_chain.HasEyeBones, "This fixture models a rig with no eye bones.");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_profile);
        }

        private EyeSolveInput BlendshapeInput(Vector3 targetPoint, bool lookShapesActive = true) => new()
        {
            Chain = _chain,
            Profile = _profile,
            DeltaTime = Dt,
            TargetPoint = targetPoint,
            HasTarget = true,
            Engagement = 1f,
            FixationLiveliness = 1f,
            GenerationId = 1,
            ApplyToBones = false,               // no eye bones to write
            LookShapesActive = lookShapesActive // blendshape backend on
        };

        private void SolveFor(float seconds, EyeSolveInput input)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
                _solver.Solve(in input);
        }

        [Test]
        public void NearTarget_ConvergesEyes_OnBlendshapeBackend()
        {
            Vector3 target = _chain.EyeCenterPosition + Vector3.forward * 0.3f;

            SolveFor(1f, BlendshapeInput(target));

            float leftYaw = _solver.LeftEyeAngles.x;
            float rightYaw = _solver.RightEyeAngles.x;
            Assert.That(leftYaw, Is.GreaterThan(rightYaw + 1f),
                "A 30 cm target must converge the eyes even without eye bones (each eye rotates inward).");
            Assert.That(Mathf.Abs(leftYaw - rightYaw) * 0.5f,
                Is.LessThanOrEqualTo(_profile.MaxConvergenceDegrees + 0.5f),
                "Synthesized vergence still honors the per-eye cross-eye clamp.");
        }

        [Test]
        public void FarTarget_HasNegligibleVergence_OnBlendshapeBackend()
        {
            Vector3 target = _chain.EyeCenterPosition + Vector3.forward * 6f;

            SolveFor(1f, BlendshapeInput(target));

            Assert.That(Mathf.Abs(_solver.LeftEyeAngles.x - _solver.RightEyeAngles.x), Is.LessThan(1f),
                "At conversation distance the eyes are effectively parallel.");
        }

        [Test]
        public void NoBonesAndNoLookShapes_KeepsEyesParallel()
        {
            // With neither eye bones NOR look shapes there is no way to express vergence, so it
            // must stay off — both eyes share the conjugate yaw.
            Vector3 target = _chain.EyeCenterPosition + Vector3.forward * 0.3f;

            SolveFor(1f, BlendshapeInput(target, lookShapesActive: false));

            Assert.That(Mathf.Abs(_solver.LeftEyeAngles.x - _solver.RightEyeAngles.x), Is.LessThan(0.001f),
                "Vergence stays off when no backend can express it.");
        }
    }
}
