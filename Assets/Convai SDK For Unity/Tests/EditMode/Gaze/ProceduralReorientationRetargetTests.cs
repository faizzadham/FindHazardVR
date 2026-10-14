using System;
using Convai.Modules.Gaze.Core.Reorientation;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     What the procedural body turn owes a target that moves while the turn is in flight:
    ///     every frame of it is a shaped step, and it still lands.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The defect these were written against was measured in Play Mode: a 90° turn was
    ///         re-aimed to the other side mid-flight, the root reversed, and then between two
    ///         frames it went 151.4° → 179.9° — 28.5° in one frame, about 1880°/s — and the turn
    ///         ended. The turn's <i>shape</i> was never the problem; the last frame of its clock
    ///         was. <c>BallisticMotor.Step</c> lands by assignment when the movement's time runs
    ///         out, and a re-aim can leave far more angle to cover than the clock had left, so
    ///         that landing was the whole residual applied at once.
    ///     </para>
    ///     <para>
    ///         The assertions are therefore about the per-frame step rather than the pose: a
    ///         test that only checks where the character ended up cannot see a whip at all,
    ///         because the whip lands on target — that is what makes it a whip.
    ///     </para>
    /// </remarks>
    public sealed class ProceduralReorientationRetargetTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>
        ///     A minimum-jerk movement peaks at 1.875× its average rate, and the profile's turn
        ///     speed is authored as that peak — so this is the fastest a single frame of a legal
        ///     turn may rotate the body by. Mirrors <c>ProceduralReorientationDriver</c>'s own
        ///     constant deliberately: a test that imported the number could not catch it changing.
        /// </summary>
        private const float MinJerkPeakFactor = 1.875f;

        /// <summary>Slack for float drift and the frame the clamp itself lands on.</summary>
        private const float StepTolerance = 0.5f;

        private ConvaiGazeProfile _profile;
        private GameObject _root;
        private ProceduralReorientationDriver _driver;

        private float StepBound => _profile.ProceduralTurnSpeed * Dt + StepTolerance;

        /// <summary>The driver's own clock for a turn of <paramref name="degrees" />.</summary>
        private float PlannedSeconds(float degrees) =>
            Mathf.Max(0.25f, MinJerkPeakFactor * degrees / _profile.ProceduralTurnSpeed);

        /// <summary>
        ///     That clock plus the margin a deadline needs: the turn ends on the completion
        ///     tolerance rather than on the last degree, and a frame is quantised.
        /// </summary>
        private float ClockBound(float degrees) => PlannedSeconds(degrees) * 1.3f + Dt;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _root = new GameObject("ProceduralTurnRoot");
            _driver = new ProceduralReorientationDriver();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_profile);
        }

        [Test]
        public void RetargetToTheOppositeSide_NeverWhipsTheRootAndStillLands()
        {
            _driver.Begin(_root.transform, _root.transform, DirectionAt(90f), _profile);
            Assert.IsTrue(_driver.IsActive, "A 90° turn must start.");

            // Far enough into the turn that the body is moving at speed toward +90° when the
            // target jumps to the other side — the reversal is what leaves the original clock
            // unable to close the angle it is now being asked to close.
            TurnRun run = RunToCompletion(t => DirectionAt(t < 0.35f ? 90f : -49f), 3f);

            AssertShapedAndLanded(run, -49f);
        }

        [Test]
        public void RetargetToANearerTargetOnTheSameSide_NeverWhipsTheRootAndStillLands()
        {
            _driver.Begin(_root.transform, _root.transform, DirectionAt(90f), _profile);
            Assert.IsTrue(_driver.IsActive, "A 90° turn must start.");

            // The other way a clock outlives its angle: the goal moves toward the character, so
            // the turn arrives early and the movement's remaining time has nothing left to spend.
            TurnRun run = RunToCompletion(t => DirectionAt(t < 0.35f ? 90f : 30f), 3f);

            AssertShapedAndLanded(run, 30f);
        }

        [Test]
        public void TargetJitteringEveryFrame_NeitherStallsNorWhips()
        {
            _driver.Begin(_root.transform, _root.transform, DirectionAt(90f), _profile);

            // A target that never holds still by more than a degree — a hand-held camera, a
            // breathing player rig. Two things have to survive it: the re-plan must not treat
            // every frame's wobble as a residual worth planning a fresh movement for, and the
            // clock must not be reset by an aim that has not actually moved, which is the crawl
            // ProceduralReorientationDriver.Retarget's aim-shift gate exists to prevent.
            int frame = 0;
            TurnRun run = RunToCompletion(_ => DirectionAt(90f + (frame++ % 2 == 0 ? 1f : -1f)), 3f);

            AssertShapedAndLanded(run, 90f);

            // The clock assertion: a turn whose plan is reset by a wobble still moves smoothly
            // and still arrives, it just takes visibly longer, and only a deadline catches that.
            // Derived from the profile, not written down — the bound used to be a flat 1.5 s
            // against the 1.2 s a 90° turn took at the old 140 °/s default, and the shipped
            // default is now 90 °/s.
            float clockBound = ClockBound(90f);
            Assert.That(run.Seconds, Is.LessThan(clockBound),
                $"A jittering target must not extend a turn planned for {PlannedSeconds(90f):0.00} s — " +
                $"landed in {run.Seconds:0.00} s. " +
                "Only a goal that actually moved may reset the movement's clock.");
        }

        private void AssertShapedAndLanded(in TurnRun run, float finalTargetYaw)
        {
            Assert.That(run.MaxStep, Is.LessThanOrEqualTo(StepBound),
                $"{run.MaxStep:0.00}° in one frame at t={run.MaxStepAt:0.00}s — " +
                $"{run.MaxStep / Dt:0}°/s against a {_profile.ProceduralTurnSpeed:0}°/s turn. " +
                "A re-aimed turn whose clock runs out must re-plan the residual as a movement, " +
                "never land on it in a single frame.");

            Assert.IsTrue(run.Completed,
                $"The turn was still running after {run.Seconds:0.00} s. Re-planning a residual " +
                "that never converges is the other way bounding the step can go wrong.");

            Vector3 forward = _root.transform.forward;
            forward.y = 0f;
            float residual = Vector3.Angle(forward.normalized, DirectionAt(finalTargetYaw));
            Assert.That(residual, Is.LessThanOrEqualTo(_profile.BodyTurnCompletionToleranceDegrees),
                $"The turn stopped {residual:0.0}° off the live target — bounding the per-frame " +
                "step must not be paid for by abandoning the angle it was going to cover.");
        }

        /// <summary>
        ///     Drives the turn at 60 Hz, re-aiming every frame the way <c>ReorientationDirector</c>
        ///     does, and records the largest single-frame yaw change the root took.
        /// </summary>
        private TurnRun RunToCompletion(Func<float, Vector3> aimAt, float timeoutSeconds)
        {
            var run = new TurnRun();
            float elapsed = 0f;

            while (_driver.IsActive && elapsed < timeoutSeconds)
            {
                _driver.Retarget(aimAt(elapsed), _profile);

                float before = _root.transform.eulerAngles.y;
                _driver.Tick(_profile, Dt);
                float step = Mathf.Abs(Mathf.DeltaAngle(before, _root.transform.eulerAngles.y));

                if (step > run.MaxStep)
                {
                    run.MaxStep = step;
                    run.MaxStepAt = elapsed;
                }

                elapsed += Dt;
            }

            run.Seconds = elapsed;
            run.Completed = !_driver.IsActive;
            return run;
        }

        private static Vector3 DirectionAt(float yawDegrees) =>
            Quaternion.Euler(0f, yawDegrees, 0f) * Vector3.forward;

        private struct TurnRun
        {
            public float MaxStep;
            public float MaxStepAt;
            public float Seconds;
            public bool Completed;
        }
    }
}
