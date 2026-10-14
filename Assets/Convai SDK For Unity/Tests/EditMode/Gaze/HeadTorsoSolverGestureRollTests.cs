using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Coverage for the <c>GestureRollDegrees</c> seam added to
    ///     <see cref="HeadTorsoSolver" />: a zero roll must reproduce
    ///     today's exact yaw/pitch-only rotation (the bit-identity guarantee the rest of the
    ///     phase depends on), a non-zero roll must land on the Head bone only, soft-clamped at
    ///     the internal limit, and compose cleanly with an active yaw/pitch gesture. Kept as a
    ///     separate file (rather than appended to <c>HeadTorsoSolverTests.cs</c>) so that file —
    ///     covering the pre-existing, unmodified behavior — stays pristine.
    /// </summary>
    public sealed class HeadTorsoSolverGestureRollTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private HeadTorsoSolver _solver;
        private GazeChainCalibration _chain;

        private GameObject _root;
        private Transform _neck;
        private Transform _head;
        private Quaternion _neckRest;
        private Quaternion _headRest;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _solver = new HeadTorsoSolver();
            _chain = new GazeChainCalibration();

            _root = new GameObject("Root");
            _neck = NewChild(_root.transform, "Neck", new Vector3(0f, 1.5f, 0f));
            _head = NewChild(_neck, "Head", new Vector3(0f, 1.65f, 0f));

            _chain.BindManual(_root.transform, null, null, _neck, _head, null, null);

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

        private void ResetAnimatedPose()
        {
            _neck.localRotation = _neckRest;
            _head.localRotation = _headRest;
        }

        private HeadTorsoSolveInput ReleaseInput() => new()
        {
            Chain = _chain,
            Profile = _profile,
            DeltaTime = Dt,
            HasTarget = false,
            AmbientActive = false
        };

        [Test]
        public void ZeroGestureRoll_ProducesBitIdenticalHeadRotation_ToUntouchedInput()
        {
            // Two solvers, two chains: one solved with a HeadTorsoSolveInput whose
            // GestureRollDegrees field was never assigned (defaults to 0f, exactly today's
            // struct shape), the other with it explicitly assigned to 0f. Both must land on
            // exactly the same head rotation, frame for frame, over a run long enough to
            // exercise the spring, the gesture yaw/pitch channel, and the release path.
            var chainA = new GazeChainCalibration();
            var rootA = new GameObject("RootA");
            Transform neckA = NewChild(rootA.transform, "Neck", new Vector3(0f, 1.5f, 0f));
            Transform headA = NewChild(neckA, "Head", new Vector3(0f, 1.65f, 0f));
            chainA.BindManual(rootA.transform, null, null, neckA, headA, null, null);
            var solverA = new HeadTorsoSolver();

            var chainB = new GazeChainCalibration();
            var rootB = new GameObject("RootB");
            Transform neckB = NewChild(rootB.transform, "Neck", new Vector3(0f, 1.5f, 0f));
            Transform headB = NewChild(neckB, "Head", new Vector3(0f, 1.65f, 0f));
            chainB.BindManual(rootB.transform, null, null, neckB, headB, null, null);
            var solverB = new HeadTorsoSolver();

            Quaternion neckRestA = neckA.localRotation, headRestA = headA.localRotation;
            Quaternion neckRestB = neckB.localRotation, headRestB = headB.localRotation;

            Vector3 target = headA.position + new Vector3(2f, 0f, 2f);
            var director = new GazeShiftDirector();

            try
            {
                int steps = Mathf.CeilToInt(2f / Dt);
                for (int i = 0; i < steps; i++)
                {
                    neckA.localRotation = neckRestA;
                    headA.localRotation = headRestA;
                    neckB.localRotation = neckRestB;
                    headB.localRotation = headRestB;

                    // Both rigs are identical, so one measurement and one plan drive both —
                    // which also keeps the two solvers on exactly the same allocated share,
                    // the only way a bit-identity claim about the roll channel means anything.
                    chainA.TryMeasureShift(target, out GazeShiftMeasurement measurement);
                    GazeShiftPlan plan = director.Plan(
                        in measurement, _profile, 1f, 1f,
                        torsoAvailable: false, feetAvailable: false, 5, Dt);

                    // Struct field left at its default (0f) — the exact shape of every
                    // pre-existing test and call site before this phase.
                    var inputA = new HeadTorsoSolveInput
                    {
                        Chain = chainA,
                        Profile = _profile,
                        DeltaTime = Dt,
                        TargetPoint = target,
                        HasTarget = true,
                        Measurement = measurement,
                        Plan = plan,
                        Engagement = 1f,
                        GestureOffset = new Vector2(0f, -3f)
                    };
                    // Same input, GestureRollDegrees explicitly set to 0f.
                    HeadTorsoSolveInput inputB = inputA;
                    inputB.Chain = chainB;
                    inputB.GestureRollDegrees = 0f;

                    solverA.Solve(in inputA);
                    solverB.Solve(in inputB);

                    Assert.That(headA.localRotation.x, Is.EqualTo(headB.localRotation.x));
                    Assert.That(headA.localRotation.y, Is.EqualTo(headB.localRotation.y));
                    Assert.That(headA.localRotation.z, Is.EqualTo(headB.localRotation.z));
                    Assert.That(headA.localRotation.w, Is.EqualTo(headB.localRotation.w));
                }

                Assert.That(solverA.HeadRollDegrees, Is.EqualTo(0f));
                Assert.That(solverB.HeadRollDegrees, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(rootA);
                Object.DestroyImmediate(rootB);
            }
        }

        [Test]
        public void GestureRoll_AppliesToHeadBoneOnly()
        {
            var input = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                HasTarget = false,
                AmbientActive = false,
                GestureRollDegrees = 6f
            };

            ResetAnimatedPose();
            _solver.Solve(in input);

            // The Neck bone's local rotation must be untouched by roll (no yaw/pitch goal is
            // active either, at rest with no target/ambient), while the Head bone rotates.
            Assert.That(Quaternion.Angle(_neck.localRotation, _neckRest), Is.LessThan(0.05f),
                "Gesture roll must never be distributed onto the Neck bone.");
            Assert.That(Quaternion.Angle(_head.localRotation, _headRest), Is.GreaterThan(1f),
                "Gesture roll must visibly rotate the Head bone.");
        }

        [Test]
        public void GestureRoll_SoftClampsAtTheInternalLimit()
        {
            // 10f is the solver's hardcoded internal gesture-roll limit; far beyond it the
            // applied roll must still be bounded near that limit (soft-clamp knee), never
            // anywhere close to the raw input.
            var input = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                HasTarget = false,
                AmbientActive = false,
                GestureRollDegrees = 60f
            };

            ResetAnimatedPose();
            _solver.Solve(in input);

            Assert.That(_solver.HeadRollDegrees, Is.LessThanOrEqualTo(10.5f),
                "Gesture roll must never exceed the internal soft-clamp limit (~10°).");
            Assert.That(_solver.HeadRollDegrees, Is.GreaterThan(5f),
                "A large roll request should still land well past the soft-clamp knee (0.85 * limit).");
        }

        [Test]
        public void GestureRoll_ComposesWithYawPitchGesture()
        {
            // Target dead ahead so the aim goal is ~0; only the gesture channel moves the head.
            Vector3 target = _head.position + _root.transform.forward * 2f;
            var input = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                TargetPoint = target,
                HasTarget = true,
                Engagement = 1f,
                GestureOffset = new Vector2(0f, -4f),
                GestureRollDegrees = 5f
            };

            int steps = Mathf.CeilToInt(1.5f / Dt);
            for (int i = 0; i < steps; i++)
            {
                ResetAnimatedPose();
                _solver.Solve(in input);
            }

            Assert.That(_solver.HeadAngles.y, Is.EqualTo(-4f).Within(0.75f),
                "The yaw/pitch gesture channel must be unaffected by an active roll gesture.");
            Assert.That(_solver.HeadRollDegrees, Is.EqualTo(5f).Within(0.5f),
                "The roll gesture must land at its requested amplitude alongside the pitch nod.");
        }

        [Test]
        public void GestureRoll_ReleasesCleanly_WhenInputReturnsToZero()
        {
            var withRoll = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = _profile,
                DeltaTime = Dt,
                HasTarget = false,
                AmbientActive = false,
                GestureRollDegrees = 8f
            };
            for (int i = 0; i < Mathf.CeilToInt(0.5f / Dt); i++)
            {
                ResetAnimatedPose();
                _solver.Solve(in withRoll);
            }
            Assert.That(_solver.HeadRollDegrees, Is.GreaterThan(3f), "Sanity: roll is active.");

            HeadTorsoSolveInput release = ReleaseInput();
            for (int i = 0; i < Mathf.CeilToInt(0.5f / Dt); i++)
            {
                ResetAnimatedPose();
                _solver.Solve(in release);
            }

            Assert.That(_solver.HeadRollDegrees, Is.EqualTo(0f),
                "Roll must drop back to exactly zero the instant GestureRollDegrees returns to 0.");
            Assert.That(Quaternion.Angle(_head.localRotation, _headRest), Is.LessThan(0.5f),
                "With no target, no ambient, and no gesture, the head must settle back near rest.");
        }
    }
}
