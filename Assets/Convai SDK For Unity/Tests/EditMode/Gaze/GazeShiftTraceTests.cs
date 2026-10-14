using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The acceptance table for the head/eye coordination work, expressed as assertions
    ///     over a recorded gaze shift rather than as prose.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each test names the invariant it enforces. They run against
    ///         <see cref="GazeShiftTraceHarness" />, which drives the real solvers over a
    ///         synthetic rig, so a regression in either solver — or in how they divide the work
    ///         between them — fails here rather than being noticed in a play session weeks later.
    ///     </para>
    ///     <para>
    ///         Numbers in the assertions are the intended targets, not the values that happened to
    ///         come out: a test written to whatever the code currently does measures nothing.
    ///     </para>
    /// </remarks>
    public sealed class GazeShiftTraceTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private GazeShiftTraceHarness NewHarness() => new(_profile);

        /// <summary>A point at eye height, <paramref name="yawDegrees" /> off the character's axis.</summary>
        private static Vector3 TargetAt(GazeShiftTraceHarness harness, float yawDegrees, float distance = 2f)
        {
            Vector3 origin = harness.EyeCenter;
            Quaternion rotation = Quaternion.AngleAxis(yawDegrees, harness.Root.up);
            return origin + rotation * (harness.Root.forward * distance);
        }

        /// <summary>Invariant: no aim solve may produce roll. A tilted head is a composition fault.</summary>
        [Test]
        public void HeadNeverRolls_WhileAimingAnOffAxisTargetBelowEyeLine()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            // Both axes loaded: yaw off-axis and the target below the eye line, which is the
            // combination the neck/head share split used to turn into a visible sideways tilt.
            Vector3 target = TargetAt(harness, 40f) + Vector3.down * 0.6f;
            harness.Run(3f, target);

            Assert.That(harness.PeakHeadRoll(), Is.LessThan(0.1f),
                "The aim solve produces no roll of its own, so any roll here is the share split " +
                "failing to recompose.");
        }

        /// <summary>
        ///     Invariant: a settled shift leaves the eyes near orbit centre. Eyes pinned at their
        ///     clamp after the head has arrived is the "staring from under the brows" read.
        /// </summary>
        [Test]
        public void SettledShift_LeavesTheEyesNearOrbitCentre()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            harness.Run(4f, TargetAt(harness, 25f));

            Assert.That(harness.Final().EyeEccentricity, Is.LessThan(6f),
                "Once the head has carried the shift the eyes must return toward centre.");
        }

        /// <summary>
        ///     Invariant: the head cancels the animation's own head deviation while engaged, so
        ///     the eyes never have to counter-stare against a clip's head bow.
        /// </summary>
        [Test]
        public void AnimatedHeadBow_IsCancelledRatherThanLeftToTheEyes()
        {
            using GazeShiftTraceHarness harness = NewHarness();
            harness.AnimatedHeadPitchDegrees = -12f;

            harness.Run(3f, TargetAt(harness, 0f));

            Assert.That(harness.Final().Eye.y, Is.EqualTo(0f).Within(3f),
                "With the target dead ahead the eyes must sit level: a bowed head plus rolled-up " +
                "eyes is the defect this reflex exists to prevent.");
        }

        /// <summary>
        ///     The same, during a body turn — the case where the reflex used to be scaled down to
        ///     0.245 and three-quarters of the clip's bow reached the screen.
        /// </summary>
        [Test]
        public void AnimatedHeadBow_IsStillCancelled_WhileTheBodyIsTurning()
        {
            using GazeShiftTraceHarness harness = NewHarness();
            harness.AnimatedHeadPitchDegrees = -12f;

            harness.Run(3f, TargetAt(harness, 0f), headContribution: 0.68f, bodyTurnActive: true);

            Assert.That(harness.Final().Eye.y, Is.EqualTo(0f).Within(4f),
                "Body-turn relief governs the voluntary shift, never the stabilization reflex " +
                ".");
        }

        /// <summary>
        ///     The same, inside the head-onset window — the leak found while implementing the
        ///     reflex/aim split, where the solver returned before reaching the reflex at all.
        /// </summary>
        [Test]
        public void AnimatedHeadBow_IsCancelledFromTheFirstFrameOfANewShift()
        {
            using GazeShiftTraceHarness harness = NewHarness();
            harness.AnimatedHeadPitchDegrees = -12f;

            // Settle on a target, then re-target: the onset window restarts here.
            harness.Run(2f, TargetAt(harness, 0f));
            harness.Retarget();
            harness.Run(0.12f, TargetAt(harness, 0f));

            Assert.That(harness.Final().Head.y, Is.GreaterThan(6f),
                "The onset window holds the voluntary aim, not the reflex: the head must still be " +
                "cancelling the bow during it.");
        }

        /// <summary>
        ///     Aiming the head bone rather than the eye line leaves the eyes holding a permanent
        ///     counter-offset. With a target dead ahead there is no shift to speak of, so any
        ///     residual eccentricity is pure parallax.
        /// </summary>
        [Test]
        public void TargetDeadAhead_LeavesNoStandingEyeOffset()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            harness.Run(3f, TargetAt(harness, 0f, distance: 1.5f));

            Assert.That(harness.Final().EyeEccentricity, Is.LessThan(1f),
                "A target straight ahead needs no eye deviation; anything here is the head aiming " +
                "from the wrong origin.");
        }

        /// <summary>
        ///     Invariant I1: what the shift required and what the actuators delivered must agree,
        ///     to within the geometry that summing three angles cannot express.
        /// </summary>
        /// <remarks>
        ///     The budget is derived from the rig rather than chosen. Summing the contributions
        ///     assumes the eyes sit on the head's rotation pivot; they sit 7.5 cm in front of it,
        ///     so a 30° head turn carries the eye centre along an arc and delivers 28.94° of
        ///     bearing change to a target 2 m away. The eyes correctly counter-rotate by the
        ///     difference, and the sum falls permanently short by the same amount with nothing
        ///     wrong. That is a property of having a head, not of this solver, and no amount of
        ///     correct coordination removes it — so the bound has to carry it or the test is
        ///     asserting that the character's eyes are in the middle of its skull.
        ///     <para>
        ///         The slack ON TOP of the geometric term is what actually polices coordination,
        ///         and it stays tight: an actuator backing off while another clamps leaves several
        ///         degrees, not a fraction of one.
        ///     </para>
        /// </remarks>
        [Test]
        public void ActuatorContributions_SumToTheRequiredShift()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            const float turnDegrees = 30f;
            const float distance = 2f;
            harness.Run(4f, TargetAt(harness, turnDegrees, distance));

            float parallax = harness.PivotParallaxAllowanceDegrees(turnDegrees, distance);
            float bound = parallax + 0.5f;

            Assert.That(harness.PeakConservationErrorAfter(1f), Is.LessThan(bound),
                $"Eyes plus head plus torso must account for the whole required shift to within " +
                $"the {parallax:0.00}° the eye-to-pivot offset accounts for on its own; an " +
                "unaccounted residual beyond that means one actuator backed off and another " +
                "silently clamped.");
        }

        /// <summary>
        ///     Invariant I3: one onset cascade, not three stacked hold windows. A long stretch
        ///     where the head does not move while the shift is still unsatisfied is the
        ///     freeze-then-whip signature.
        /// </summary>
        [Test]
        public void HeadTrajectory_HasNoLongPlateauWhileTheShiftIsUnsatisfied()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            harness.Run(4f, TargetAt(harness, 45f));

            // The bound is the head's own onset plus a frame of slack. It is deliberately not
            // tighter: the onset is a real, wanted pause — the eyes-then-head cascade — and the
            // defect being guarded against is a plateau that OUTLASTS it, which is what stacked
            // hold windows produced.
            Assert.That(harness.LongestHeadPlateauBeforeSettle(), Is.LessThan(0.2f),
                "The head may lag the eyes by its onset, but it must not sit still through " +
                "stacked hold windows on its way there.");
        }

        /// <summary>
        ///     The harness itself has to be able to see the defect it guards against, or the
        ///     suite above proves nothing: with the reflex turned off, the animated bow must
        ///     reach the eyes.
        /// </summary>
        [Test]
        public void HarnessDetectsTheDefect_WhenStabilizationIsDisabled()
        {
            using GazeShiftTraceHarness harness = NewHarness();
            SetHeadStabilization(_profile, 0f);
            harness.AnimatedHeadPitchDegrees = -12f;

            harness.Run(3f, TargetAt(harness, 0f));

            Assert.That(harness.Final().Eye.y, Is.GreaterThan(4f),
                "With the reflex disabled the eyes must visibly roll up to hold the target — if " +
                "they do not, this suite cannot detect the defect it exists for.");
        }

        private static void SetHeadStabilization(ConvaiGazeProfile profile, float value)
        {
            var serialized = new SerializedObject(profile);
            GazeProfileSerializedPaths.Find(serialized, "headStabilization").floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        ///     The head must reach its allocated share promptly and stop there. A spring never
        ///     arrives — it trails its goal and then unwinds the accumulated error when the goal
        ///     stops, which is the soft-then-whip read this replaced.
        /// </summary>
        [Test]
        public void HeadStepResponse_ArrivesQuicklyAndDoesNotOvershoot()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            harness.Run(1.5f, TargetAt(harness, 40f));

            float settled = harness.Final().Head.x;
            Assert.That(settled, Is.GreaterThan(1f), "Sanity: the head took a share of the shift.");

            float worstOvershoot = 0f;
            float ninetyPercentAt = float.PositiveInfinity;
            foreach (GazeShiftSample sample in harness.Samples)
            {
                // Overshoot is measured against the share the head was ASKED for that frame.
                // Measuring against the settled value instead would count the requirement's own
                // convergence — the head turning carries the eye line with it, so the angle to
                // the target shrinks as the head arrives — as if the motor had rung.
                worstOvershoot = Mathf.Max(worstOvershoot, sample.Head.x - sample.PlannedHead.x);
                if (float.IsPositiveInfinity(ninetyPercentAt) && sample.Head.x >= settled * 0.9f)
                    ninetyPercentAt = sample.Time;
            }

            // The head's timing is a duration law now, not a deadline: a look takes
            // headTurnBaseSeconds + headTurnSecondsPerDegree per degree, plus its onset. At the
            // shipped conversational pace a 45° target's head share lands 90% inside ~0.9 s. The
            // bound is a band rather than a ceiling — motion that is too FAST is the defect this
            // whole suite exists for, and a one-sided "fast enough" assertion is exactly the gate
            // that let the harshness ship in the first place.
            Assert.That(ninetyPercentAt, Is.InRange(0.35f, 1.4f),
                "The head reaches 90% of its share on the duration law's schedule — neither " +
                "snapping there nor crawling.");
            Assert.That(worstOvershoot, Is.LessThan(0.5f),
                "A minimum-jerk movement arrives at rest on its goal; it must never carry the " +
                "head past the share it was given.");
        }

        /// <summary>
        ///     Tracking a slowly moving target must not lag. A spring's steady-state error is
        ///     proportional to target speed by construction; a rate limiter is transparent to
        ///     motion inside its caps.
        /// </summary>
        [Test]
        public void SlowlyMovingTarget_IsTrackedWithoutStandingLag()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            // Settle first, then creep the target sideways at ~5°/s for two seconds.
            harness.Run(1.5f, TargetAt(harness, 20f));

            const float degreesPerSecond = 5f;
            int steps = Mathf.CeilToInt(2f / GazeShiftTraceHarness.FrameSeconds);
            for (int i = 0; i < steps; i++)
            {
                float yaw = 20f + degreesPerSecond * (i * GazeShiftTraceHarness.FrameSeconds);
                harness.Step(TargetAt(harness, yaw));
            }

            // The target ends at 30° after two seconds of creep, so the same eye-to-pivot
            // geometry applies as in ActuatorContributions_SumToTheRequiredShift — see its
            // remarks. Lag would show up on top of it, and lag against a 5°/s target is worth
            // whole degrees, not tenths.
            float parallax = harness.PivotParallaxAllowanceDegrees(
                20f + degreesPerSecond * 2f, 2f);

            Assert.That(harness.Final().ConservationError, Is.LessThan(parallax + 0.5f),
                "A tracked target must stay accounted for: lag shows up as an unexplained " +
                "residual beyond what the eye-to-pivot offset already accounts for.");
        }
    }
}
