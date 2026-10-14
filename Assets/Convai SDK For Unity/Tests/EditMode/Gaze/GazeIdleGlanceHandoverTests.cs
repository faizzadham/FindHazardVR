using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The hand-over between idle life and a short glance, driven end to end: the character is
    ///     drifting on an ambient fixation, glances at something further round on the same side,
    ///     and is released back to idle.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the beat an idle character performs every few seconds — the curiosity
    ///         glance — and it used to be the harshest movement the module made. Idle life was
    ///         switched off by a boolean the frame a target became engaged, a whole head-onset gap
    ///         before the ladder had any share to replace it with, so the head's goal collapsed to
    ///         centre, the actuator dutifully executed a movement in that direction, and the real
    ///         turn to the target then reversed it. Two movements in opposite directions, and the
    ///         first one was executed at reflex speed because an ordinary re-target was being
    ///         classified as a camera cut.
    ///     </para>
    ///     <para>
    ///         So the assertion is not about smoothness bounds — <see cref="GazeOutputContinuityTests" />
    ///         owns those, and the old behaviour passed them: every one of those movements was
    ///         individually well shaped. It is about direction. The head must never travel away
    ///         from the thing it is about to look at.
    ///     </para>
    /// </remarks>
    public sealed class GazeIdleGlanceHandoverTests
    {
        /// <summary>
        ///     Engagement a curiosity glance carries — the controller's own override. A glance is
        ///     committed; its brevity is what makes it a glance.
        /// </summary>
        private const float GlanceEngagement = 1f;

        /// <summary>
        ///     Head participation a glance asks for, stated by the glance rather than inherited
        ///     from the Idle policy — <c>ConvaiGazeController.GlanceHeadContribution</c>.
        /// </summary>
        private const float GlanceHeadContribution = 0.75f;

        /// <summary>Shipped curiosity-glance hold, in seconds.</summary>
        private const float GlanceSeconds = 1.2f;

        /// <summary>Yaw of the ambient fixation the character is drifting on when the glance fires.</summary>
        private const float AmbientYawDegrees = 20f;

        /// <summary>
        ///     Yaw of the glance target. Deliberately on the SAME side as the ambient fixation and
        ///     further out, so "moving toward the target" and "moving away from the fixation" are
        ///     opposite directions and the trace can tell them apart.
        /// </summary>
        private const float GlanceYawDegrees = 45f;

        /// <summary>
        ///     How far the head may dip back before it counts as travelling the wrong way. Well
        ///     under the profile's 2 deg movement trigger, so anything that could recruit a
        ///     backwards movement fails this.
        /// </summary>
        private const float WrongWayToleranceDegrees = 0.5f;

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        [Test]
        public void GlanceOutOfIdleLife_HeadNeverTravelsAwayFromTheGlance()
        {
            using GazeShiftTraceHarness harness = DriveGlanceCycle(
                out int glanceFirstFrame, out _, out float idleHeadYaw);

            // The approach: from the frame the glance is pushed to the frame the head is furthest
            // round. Anything after that is the release, which is allowed to come back.
            int peakFrame = glanceFirstFrame;
            for (int i = glanceFirstFrame; i < harness.Samples.Count; i++)
                if (harness.Samples[i].Head.x > harness.Samples[peakFrame].Head.x)
                    peakFrame = i;

            float worstDip = 0f;
            int worstFrame = glanceFirstFrame;
            for (int i = glanceFirstFrame; i <= peakFrame; i++)
            {
                float dip = idleHeadYaw - harness.Samples[i].Head.x;
                if (dip <= worstDip) continue;
                worstDip = dip;
                worstFrame = i;
            }

            Assert.That(worstDip, Is.LessThan(WrongWayToleranceDegrees),
                $"The head started the glance by turning AWAY from it: yaw fell {worstDip:0.00} deg " +
                $"below the {idleHeadYaw:0.00} deg it was holding (frame {worstFrame}, " +
                $"t={harness.Samples[worstFrame].Time:0.000}s) before turning toward a target at " +
                $"{GlanceYawDegrees} deg.\n" +
                "The head hands its ambient fixation over as it joins the look " +
                "(HeadTorsoSolveInput.AmbientHandover x GazeShiftPlan.HeadOnsetPending). A dip " +
                "here means the fixation was dropped before the ladder had a share to replace it " +
                "with — either the hand-over gate is not reaching the solver, or idle life is " +
                "being cleared underneath it (AmbientExplorationDirector's resume window, which " +
                "HasResumableFixation reports).");
        }

        /// <summary>
        ///     The turn's speed must come from the profile's duration law, not from whatever ramp
        ///     happened to be feeding the goal.
        /// </summary>
        /// <remarks>
        ///     The failure this catches is not a step and passes every continuity bound: if the
        ///     head's goal arrives continuously — because the acquisition ramp is scaling the
        ///     allocated share, or because a hand-over crossfades instead of gating — the movement
        ///     detector never fires and the tracking lane executes it. That lane is transparent to
        ///     in-budget motion by design, so the head covers the whole turn in the ramp's own
        ///     time (0.35 s) rather than the ~0.6–0.9 s the turn-time settings ask for, at constant
        ///     velocity with no profile. On screen it reads as the character snapping round.
        /// </remarks>
        [Test]
        public void GlanceOutOfIdleLife_TurnSpeedObeysTheDurationLaw()
        {
            using GazeShiftTraceHarness harness = DriveGlanceCycle(
                out int glanceFirstFrame, out _, out float idleHeadYaw);

            int peakFrame = glanceFirstFrame;
            float peakSpeed = 0f;
            for (int i = glanceFirstFrame; i < harness.Samples.Count; i++)
                if (harness.Samples[i].Head.x > harness.Samples[peakFrame].Head.x)
                    peakFrame = i;

            for (int i = glanceFirstFrame; i <= peakFrame; i++)
                peakSpeed = Mathf.Max(peakSpeed, harness.Samples[i].HeadSpeed);

            float amplitude = Mathf.Abs(harness.Samples[peakFrame].Head.x - idleHeadYaw);
            float lawSeconds = _profile.HeadTurnBaseSeconds + _profile.HeadTurnSecondsPerDegree * amplitude;

            // A minimum-jerk movement peaks at 1.875x its mean speed. The allowance on top covers
            // the goal legitimately moving under the movement (the required shift shrinks as the
            // head turns) — it is not room for a different speed law.
            float lawPeak = 1.875f * amplitude / lawSeconds;

            Assert.That(peakSpeed, Is.LessThan(lawPeak * 1.6f),
                $"The glance turned at {peakSpeed:0} deg/s. A {amplitude:0.0} deg head movement " +
                $"under the profile's own law ({_profile.HeadTurnBaseSeconds}s + " +
                $"{_profile.HeadTurnSecondsPerDegree}s/deg = {lawSeconds:0.00}s) peaks at " +
                $"{lawPeak:0} deg/s.\n" +
                "A speed above the law means the movement was never planned: the goal reached the " +
                "head lane as a continuous ramp, so DetectMovement did not fire and the tracking " +
                "filter — which is transparent to in-budget motion — executed the turn at the " +
                "ramp's speed. Check that the ladder is handed SettledEngagement (not the " +
                "commitment-scaled value) and that the idle hand-over gates rather than fades.");
        }

        /// <summary>
        ///     By the end of the hold the character is looking at the player with its head, not
        ///     out of the corner of its eyes.
        /// </summary>
        /// <remarks>
        ///     This is the "she turned, but she is not looking at me" report. The glance's own
        ///     strength used to multiply the dialogue state's head contribution — 0.5 × Idle's 0.4
        ///     — so the head took a fifth of the shift and the eyes were left holding the rest,
        ///     far outside the profile's own <c>Eye Comfort Degrees</c>, and past a 44° shift they
        ///     simply ran out of travel and the aim landed short. Head participation is now stated
        ///     by the glance.
        /// </remarks>
        [Test]
        public void GlanceOutOfIdleLife_LandsOnTargetWithoutParkingTheEyesAtTheirLimit()
        {
            using GazeShiftTraceHarness harness = DriveGlanceCycle(out _, out int lastHoldFrame, out _);

            GazeShiftSample settled = harness.Samples[lastHoldFrame];
            float eccentricity = settled.EyeEccentricity;

            Assert.That(eccentricity, Is.LessThan(_profile.EyeMaxYawDegrees * 0.9f),
                $"The eyes ended the glance {eccentricity:0.0} deg off centre, at the edge of " +
                $"their {_profile.EyeMaxYawDegrees} deg range — so the aim was landing short of " +
                "the target, not on it.");

            Assert.That(eccentricity, Is.LessThan(_profile.EyeComfortDegrees + 6f),
                $"The glance settled with the eyes {eccentricity:0.0} deg off centre, well past " +
                $"the profile's own Eye Comfort Degrees ({_profile.EyeComfortDegrees}). The head " +
                "is meant to carry a glance at a person and the eyes to finish it; a residual " +
                "this large means the head was allocated too little of the shift. Check that the " +
                "glance states its own head contribution instead of inheriting the Idle policy's.");

            Assert.That(Mathf.Abs(settled.ConservationError), Is.LessThan(2f),
                "Head + torso + eyes must still add up to the shift being executed.");
        }

        [Test]
        public void GlanceOutOfIdleLife_HeadEndsBackOnTheFixationItLeft()
        {
            using GazeShiftTraceHarness harness = DriveGlanceCycle(out _, out _, out float idleHeadYaw);

            Assert.That(harness.Final().Head.x, Is.EqualTo(idleHeadYaw).Within(1f),
                "After the glance releases, the head must settle back onto the idle fixation it " +
                "was holding — not onto centre. Returning to centre first is the release half of " +
                "the same wrong-way movement.");
        }

        /// <summary>
        ///     Idle drift -> glance -> release, with the arbiter's commitment ramps reproduced so
        ///     the hand-over weight the controller derives from them is exercised rather than
        ///     assumed. Engagement is modelled as <c>0.5 x commitment</c>: the live policy engine
        ///     also exponentially smooths the 0.5 override, which only makes the ramp gentler, so
        ///     this is the harsher of the two.
        /// </summary>
        private GazeShiftTraceHarness DriveGlanceCycle(
            out int glanceFirstFrame, out int glanceLastHoldFrame, out float idleHeadYaw)
        {
            var harness = new GazeShiftTraceHarness(_profile)
            {
                AmbientActive = true,
                AmbientAngles = new Vector2(AmbientYawDegrees, 0f)
            };

            Vector3 target = harness.EyeCenter +
                             Quaternion.AngleAxis(GlanceYawDegrees, harness.Root.up) *
                             (harness.Root.forward * 2f);

            // Settle on the ambient fixation the way an idle character is when a glance fires.
            harness.Run(2.5f, target, engagement: 0f, hasTarget: false);
            idleHeadYaw = harness.Final().Head.x;
            Assert.That(idleHeadYaw, Is.GreaterThan(2f),
                "Idle life must have actually turned the head before the glance, or there is " +
                "nothing to hand over and the test proves nothing.");

            glanceFirstFrame = harness.Samples.Count;

            // The glance: a new shift, acquired over the commitment ramp and held. Engagement is
            // the SETTLED strength throughout — that is what the controller hands the ladder, and
            // it is the whole point: the commitment ramp says the look is being taken up, not how
            // fast the neck moves.
            harness.Retarget();
            float commitment = 0f;
            for (float t = 0f; t < GlanceSeconds; t += GazeShiftTraceHarness.FrameSeconds)
            {
                commitment = Mathf.MoveTowards(
                    commitment, 1f, GazeShiftTraceHarness.FrameSeconds / _profile.CommitmentAcquireSeconds);
                harness.Step(
                    target, GlanceEngagement, GlanceHeadContribution, commitment: commitment);
            }

            glanceLastHoldFrame = harness.Samples.Count - 1;

            // Released: the stack entry's deadline elapses and commitment decays.
            for (float t = 0f; t < 2.5f; t += GazeShiftTraceHarness.FrameSeconds)
            {
                commitment = Mathf.MoveTowards(
                    commitment, 0f, GazeShiftTraceHarness.FrameSeconds / _profile.CommitmentReleaseSeconds);
                harness.Step(
                    target, GlanceEngagement, GlanceHeadContribution,
                    hasTarget: commitment > 0.0001f, commitment: commitment);
            }

            return harness;
        }
    }
}
