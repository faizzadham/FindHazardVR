using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     One invariant, applied to the module's OUTPUT rather than to any of its producers:
    ///     the head pose the solver writes to the bones is continuous across every transition
    ///     the character can be driven through.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This suite exists because "the head snapped" kept recurring from a different
    ///         producer each time, and each fix was verified at its own producer. The applied
    ///         head pose is composed from several sources and only some of them are shaped:
    ///         the actuator ladder's allocated share goes through the two-lane actuator, but
    ///         body-turn relief, the aversion beat, the head-gesture envelope, the
    ///         animated-deviation stabilization reflex and the gesture roll are all layered on
    ///         top of it, downstream of everything that shapes motion. A producer that steps
    ///         puts that step straight on the screen, and until this file existed nothing
    ///         checked the sum.
    ///     </para>
    ///     <para>
    ///         Every test drives ONE transition and asserts the same bound, so a new producer
    ///         is covered the moment it participates in any of them — which is the point.
    ///         Adding a producer that steps should fail here without anyone writing a test for
    ///         it specifically.
    ///     </para>
    /// </remarks>
    public sealed class GazeOutputContinuityTests
    {
        /// <summary>
        ///     The most the applied head pose may move in one frame, in degrees.
        /// </summary>
        /// <remarks>
        ///     At the harness's fixed 60 Hz this is 180 deg/s. The shipped duration law
        ///     (<c>headTurnBaseSeconds</c> + <c>headTurnSecondsPerDegree</c> per degree, on a
        ///     minimum-jerk profile) peaks around 96 deg/s for even a 55-degree look — the largest
        ///     the head's own capacity allows — so legitimate motion clears this bound by nearly
        ///     a factor of two while nothing that reads as a step can pass under it. It is a
        ///     continuity bound, not a feel setting: motion quality is policed by
        ///     <see cref="GazeShiftTraceTests" />, which owns the main-sequence band.
        /// </remarks>
        private const float MaxStepDegrees = 3f;

        /// <summary>
        ///     Everything composed onto the applied head pose, named so a failure points at the
        ///     shortlist instead of at "the gaze module".
        /// </summary>
        private const string Suspects =
            "Every quantity composed onto the head pose AFTER the two-lane actuator must be " +
            "continuous. Suspects, in the order HeadTorsoSolver.Solve composes them:\n" +
            "  1. the ladder's allocated share (GazeActuatorLadder -> the ballistic/tracking " +
            "lanes). This one is SHAPED, so a step here means the lane was bypassed or " +
            "re-seeded, not that the share moved — the share is allowed to step.\n" +
            "  2. body-turn relief (_reliefBlend, eased at ReliefBlendSharpness). It scales both " +
            "the share and the voluntary dressings, so the larger the share the larger its " +
            "onset rate.\n" +
            "  3. the aversion beat (AversionDirector) and the head-gesture / backchannel " +
            "envelope (HeadGestureArbiter, turn-taking yield dip). Both are DELIBERATELY " +
            "unfiltered, so their producers carry the continuity obligation themselves.\n" +
            "  4. the animated-deviation stabilization reflex. Unfiltered by design; its GAIN is " +
            "eased (StabilizationGainSharpness). Note it multiplies the gain by " +
            "Measurement.AnimatedYaw/AnimatedPitch, and the controller CLEARS the whole " +
            "measurement to default the frame a target stops being engaged — a cleared " +
            "measurement is a stepped input that no gain easing can smooth.\n" +
            "  5. the gesture roll branch (GestureRollDegrees != 0f).\n" +
            "Anything new composed after the lanes belongs on this list.";

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

        /// <summary>
        ///     The assertion every test in this file makes. Reports the step size, where in the
        ///     trace it happened, and the shortlist of producers that could have caused it.
        /// </summary>
        private static void AssertAppliedHeadPoseNeverSteps(
            GazeShiftTraceHarness harness, string transition)
        {
            GazeAppliedHeadStep worst = harness.LargestAppliedHeadStep();

            Assert.That(worst.Degrees, Is.LessThan(MaxStepDegrees),
                $"The applied head pose STEPPED while driving: {transition}.\n" +
                $"{worst}\n" +
                $"Bound is {MaxStepDegrees} deg per frame at 60 Hz ({MaxStepDegrees * 60f:0} deg/s), " +
                "which the shipped duration law never approaches — so this is a discontinuity, " +
                "not fast motion.\n" +
                Suspects);
        }

        /// <summary>
        ///     Engagement stepping mid-run while the animation is holding the head off neutral.
        ///     Engagement is a policy value and policy values step — it is pinned by the
        ///     floor-yield beat, floored by the target-loss search, and moves whenever the
        ///     dialogue state does. It is also the stabilization reflex's gain, and the reflex is
        ///     composed downstream of every lane, so an un-eased gain put
        ///     <c>animatedDeviation x deltaEngagement</c> straight onto the bones.
        /// </summary>
        [Test]
        public void EngagementSteppingMidRun_DoesNotStepTheAppliedHeadPose()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            // A bowed head: without something for the reflex to cancel, its gain has nothing to
            // multiply and the defect is invisible.
            harness.AnimatedHeadPitchDegrees = -12f;

            Vector3 target = TargetAt(harness, 0f);

            // Settle fully engaged, long enough for the gain to reach its target.
            harness.Run(2f, target);

            // One frame later, engagement is a third of what it was. Both edges are driven: the
            // drop and the recovery, because an easing that is asymmetric would pass one of them.
            harness.Run(1.5f, target, engagement: 0.35f);
            harness.Run(1.5f, target);

            AssertAppliedHeadPoseNeverSteps(
                harness,
                "engagement stepping 1.0 -> 0.35 -> 1.0 in single frames, with a 12 deg animated " +
                "head bow present for the stabilization reflex to cancel");
        }

        /// <summary>
        ///     Acquiring a target from a disengaged idle. The measurement, the plan and the
        ///     reflex all come alive on the same frame.
        /// </summary>
        [Test]
        public void TargetAcquired_DoesNotStepTheAppliedHeadPose()
        {
            using GazeShiftTraceHarness harness = NewHarness();
            harness.AnimatedHeadPitchDegrees = -12f;

            Vector3 target = TargetAt(harness, 30f);

            // Disengaged: no target, nothing engaged, the head rides the animated pose.
            harness.Run(1.5f, target, engagement: 0f, hasTarget: false);

            // Acquire. A new target is a new shift, so the generation bumps as it does live.
            harness.Retarget();
            harness.Run(2.5f, target);

            AssertAppliedHeadPoseNeverSteps(
                harness, "a target being acquired out of a disengaged idle");
        }

        /// <summary>
        ///     Releasing a target. The mirror of acquisition, and not symmetric with it: on
        ///     acquisition the reflex's gain starts at zero and ramps, whereas on release the gain
        ///     is already at its settled value when the measurement it multiplies is cleared.
        /// </summary>
        [Test]
        public void TargetReleased_DoesNotStepTheAppliedHeadPose()
        {
            using GazeShiftTraceHarness harness = NewHarness();
            harness.AnimatedHeadPitchDegrees = -12f;

            Vector3 target = TargetAt(harness, 30f);

            // Settle engaged: the reflex is holding ~12 degrees of cancellation on the pose and
            // the gain has reached 1.
            harness.Run(2.5f, target);

            // Release.
            harness.Run(1.5f, target, engagement: 0f, hasTarget: false);

            AssertAppliedHeadPoseNeverSteps(
                harness,
                "a target being released while the stabilization reflex was cancelling a 12 deg " +
                "animated head bow");
        }

        /// <summary>
        ///     A body turn starting and ending — the relief blend easing down to
        ///     <c>BodyTurnHeadRelief</c> and back to 1, while the root actually sweeps underneath
        ///     the solver the way a real reorientation makes it.
        /// </summary>
        [Test]
        public void BodyTurnStartingAndEnding_DoesNotStepTheAppliedHeadPose()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            Vector3 target = TargetAt(harness, 45f);

            // Settle with relief at 1 and a large head share, which is the condition that makes
            // the relief blend's own rate largest: it scales the share.
            harness.Run(2f, target);

            // The turn: 30 degrees over half a second (60 deg/s, a realistic reorientation), with
            // the relief flag held for its duration.
            int turnFrames = Mathf.CeilToInt(0.5f / GazeShiftTraceHarness.FrameSeconds);
            float degreesPerFrame = 30f / turnFrames;
            for (int i = 0; i < turnFrames; i++)
            {
                harness.TurnRoot(degreesPerFrame);
                harness.Step(target, bodyTurnActive: true);
            }

            // Turn over: relief eases back out. A binary switch here used to read as a whip.
            harness.Run(2f, target);

            AssertAppliedHeadPoseNeverSteps(
                harness,
                "a body turn starting and ending — the relief blend easing on and off around a " +
                "30 deg root sweep");
        }

        /// <summary>
        ///     Head contribution stepping. The conversation-state policy hands the ladder a head
        ///     willingness, and it moves on state edges; the ladder's capacity, and therefore the
        ///     head's whole allocated share, moves with it.
        /// </summary>
        [Test]
        public void HeadContributionChanging_DoesNotStepTheAppliedHeadPose()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            Vector3 target = TargetAt(harness, 45f);

            // A large target so the share the willingness scales is a large one.
            harness.Run(2f, target);

            // The policy pulls the head most of the way back, then releases it again.
            harness.Run(2f, target, headContribution: 0.2f);
            harness.Run(2f, target);

            AssertAppliedHeadPoseNeverSteps(
                harness,
                "the ladder's head willingness stepping 1.0 -> 0.2 -> 1.0 on a 45 deg target");
        }

        /// <summary>
        ///     Ambient exploration re-targeting. The exploration director hands over a discrete
        ///     fixation — it decides to look somewhere, it does not slide there — so its output
        ///     steps BY DESIGN, and turning that decision into a movement is the head lane's job.
        ///     This is the one transition where a stepping producer is correct, which makes it the
        ///     sharpest test of whether the lane is actually in the path.
        /// </summary>
        [Test]
        public void AmbientExplorationRetargeting_DoesNotStepTheAppliedHeadPose()
        {
            using GazeShiftTraceHarness harness = NewHarness();

            // Ambient runs only when nothing is engaged; the point is never looked at.
            Vector3 unusedPoint = TargetAt(harness, 0f);
            harness.AmbientActive = true;

            harness.AmbientAngles = Vector2.zero;
            harness.Run(1f, unusedPoint, engagement: 0f, hasTarget: false);

            // Three fixations, each arriving as a one-frame jump in the director's output. The
            // third crosses the midline, which is the largest re-target idle life can produce.
            harness.AmbientAngles = new Vector2(24f, -8f);
            harness.Run(1.5f, unusedPoint, engagement: 0f, hasTarget: false);

            harness.AmbientAngles = new Vector2(-22f, 6f);
            harness.Run(1.5f, unusedPoint, engagement: 0f, hasTarget: false);

            harness.AmbientAngles = Vector2.zero;
            harness.Run(1.5f, unusedPoint, engagement: 0f, hasTarget: false);

            AssertAppliedHeadPoseNeverSteps(
                harness,
                "ambient exploration re-targeting its fixation three times (the director steps its " +
                "output by design — the head lane is what must absorb it)");
        }
    }
}
