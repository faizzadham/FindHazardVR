using System.Collections.Generic;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     One invariant, and it is not continuity: <b>a look that did not change must not be
    ///     re-aimed</b>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="GazeOutputContinuityTests" /> asks whether the applied pose ever steps,
    ///         and explicitly exempts the ladder's allocated share — "the share is allowed to
    ///         step", because a decision to look elsewhere IS a step and turning it into a
    ///         movement is the lane's job. That exemption is correct and it is also a blind spot:
    ///         every defect this file covers produced perfectly smooth, correctly shaped,
    ///         well-behaved motion <i>to somewhere the character never decided to look</i>. They
    ///         passed the continuity bound by a wide margin the entire time.
    ///     </para>
    ///     <para>
    ///         So these tests hold the target still, hold the generation still, and disturb
    ///         everything else — the commitment ramp, the state policy. Nothing the character is
    ///         looking at has changed, so its head has nothing to travel toward.
    ///     </para>
    /// </remarks>
    public sealed class GazeSteadyLookTests
    {
        /// <summary>
        ///     A shift small enough that the head is below its entry angle and never joins.
        ///     <c>headEntryDegrees</c> is 12 with a 10 degree blend, so recruitment is exactly
        ///     zero below 7 — and a character talking to someone it is already facing lives here.
        /// </summary>
        private const float SmallShiftDegrees = 3f;

        /// <summary>A shift large enough that the head owns most of it.</summary>
        private const float LargeShiftDegrees = 30f;

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private static Vector3 TargetAt(GazeShiftTraceHarness harness, float yawDegrees)
        {
            Vector3 origin = harness.EyeCenter;
            Quaternion rotation = Quaternion.AngleAxis(yawDegrees, harness.Root.up);
            return origin + rotation * (harness.Root.forward * 2f);
        }

        /// <summary>Largest |head yaw| over the samples recorded from <paramref name="from" /> on.</summary>
        private static float PeakHeadYawAfter(IReadOnlyList<GazeShiftSample> samples, int from)
        {
            float peak = 0f;
            for (int i = from; i < samples.Count; i++)
                peak = Mathf.Max(peak, Mathf.Abs(samples[i].Head.x));
            return peak;
        }

        /// <summary>Fastest the head moved over the samples recorded from <paramref name="from" /> on.</summary>
        private static float PeakHeadSpeedAfter(IReadOnlyList<GazeShiftSample> samples, int from)
        {
            float peak = 0f;
            for (int i = from; i < samples.Count; i++)
                peak = Mathf.Max(peak, samples[i].HeadSpeed);
            return peak;
        }

        /// <summary>
        ///     The defect this file was written for. A settled conversation — the character facing
        ///     the person it is talking to, so the required shift is a few degrees — plus any dip
        ///     in the arbiter's commitment ramp, and the head was handed idle life's fixation.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The hand-over exists to bridge the head's ONSET: for the 0.12 s before the head
        ///         joins a new look it goes on holding the ambient fixation rather than being
        ///         dropped to a share the ladder has not allocated yet. It was gated on
        ///         <c>HeadRecruitment</c>, which is <c>entry × onset</c> — and entry is
        ///         permanently zero below 7 degrees, because a small look is one the head is never
        ///         meant to take. So in a settled conversation the gate stood open indefinitely,
        ///         and the head's goal jumped to a fixation chosen before the conversation began.
        ///     </para>
        ///     <para>
        ///         It is a jump in the GOAL, so the actuator did what it is supposed to do with
        ///         one: planned a properly shaped movement, executed it, and planned another one
        ///         back when the commitment recovered. Smooth, unhurried, and completely wrong —
        ///         which is why no continuity bound ever saw it.
        ///     </para>
        /// </remarks>
        [Test]
        public void ACommitmentDipInASettledLook_DoesNotHandTheHeadBackToIdleLife()
        {
            using var harness = new GazeShiftTraceHarness(_profile);

            // Idle life holding a fixation well off to the side — the state it is in for the
            // first five seconds of every conversation, before the resume window clears it.
            harness.AmbientActive = true;
            harness.AmbientAngles = new Vector2(25f, 0f);

            Vector3 target = TargetAt(harness, SmallShiftDegrees);

            // Settled and fully committed: no hand-over, and the head is not a rung of a shift
            // this small, so it sits at rest.
            harness.Run(2f, target);

            float settledYaw = harness.Final().Head.x;
            int dipStart = harness.Samples.Count;

            // The commitment ramp dips — a re-decision, a state edge, a moment of lost line of
            // sight. The target has not moved and the generation has not changed.
            harness.Run(1.5f, target, commitment: 0.5f);

            float peak = PeakHeadYawAfter(harness.Samples, dipStart);

            Assert.That(peak, Is.LessThan(2f),
                $"The head travelled to {peak:0.00} deg during a commitment dip, from the " +
                $"{settledYaw:0.00} deg it was holding, with the target unchanged at " +
                $"{SmallShiftDegrees} deg.\n" +
                "That is the idle-life hand-over firing on a look the head was never going to " +
                "join. The hand-over must key on GazeShiftPlan.HeadOnsetPending — the head has " +
                "not joined YET — and not on HeadRecruitment being zero, which is also true, " +
                "permanently, whenever the shift is below the head's entry angle.\n" +
                $"Idle life was parked at 25 deg, so a hand-over shows up here as roughly " +
                $"25 x AmbientHeadFollow ({_profile.AmbientHeadFollow:0.00}) = " +
                $"{25f * _profile.AmbientHeadFollow:0.0} deg.");
        }

        /// <summary>
        ///     The same dip on a LARGE shift, where the head genuinely is a rung: here the
        ///     hand-over is correct and must still work, so this is the test that stops the fix
        ///     above from being "disable the hand-over".
        /// </summary>
        [Test]
        public void ACommitmentDipDuringAnOnsetGap_StillHandsTheHeadItsFixation()
        {
            using var harness = new GazeShiftTraceHarness(_profile);

            harness.AmbientActive = true;
            harness.AmbientAngles = new Vector2(25f, 0f);

            // Idle: the head is holding its share of the ambient fixation.
            harness.Run(1.5f, Vector3.zero, hasTarget: false);
            float idleYaw = harness.Final().Head.x;

            Assert.That(idleYaw, Is.GreaterThan(4f),
                "Precondition: idle life must actually have the head off centre, or this test " +
                "cannot tell a hand-over from its absence.");

            // A large look is acquired, and the head's onset has not elapsed. It must keep the
            // fixation rather than collapse toward a share that does not exist yet.
            Vector3 target = TargetAt(harness, LargeShiftDegrees);
            harness.Retarget();
            int acquireStart = harness.Samples.Count;
            harness.Run(_profile.HeadOnsetSeconds * 0.5f, target, commitment: 0.5f);

            float dip = idleYaw - PeakHeadYawAfter(harness.Samples, acquireStart);

            Assert.That(harness.Final().Head.x, Is.GreaterThan(idleYaw - 2f),
                $"During the head's onset gap the fixation was dropped: yaw fell from " +
                $"{idleYaw:0.00} to {harness.Final().Head.x:0.00} deg (dip {dip:0.00}). " +
                "This is the case GazeShiftPlan.HeadOnsetPending must still answer true for — " +
                "the head IS a rung of this shift, its turn has simply not come.");
        }

        /// <summary>
        ///     A conversation-state edge under an unchanged look. The state policy re-weights how
        ///     strongly the look is held; it does not decide to look somewhere else.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         On the shipped table this is the Speaking (engagement 1.0, head 0.85) to
        ///         Settling (0.6, 0.6) edge that ends every utterance: head participation falls
        ///         from 0.85 to 0.36 in a single frame. Fed to the ladder as a step, the actuator
        ///         reads it as a decision and plans a full movement — the head deliberately turns
        ///         away from a target the eyes never leave, and does it again when the floor-yield
        ///         engagement pin expires 0.8 s later.
        ///     </para>
        ///     <para>
        ///         The head is allowed to move here; it is holding less of the shift, and that is
        ///         the behaviour the state table is asking for. What it may not do is <i>travel</i>
        ///         — a re-weighting is a relaxation, and a relaxation is slower than the movement
        ///         it is not. The bound is set between the two: a planned movement of this size
        ///         peaks near 37 deg/s, the settle near 14.
        ///     </para>
        /// </remarks>
        [Test]
        public void ADialogueStatePolicyEdge_RelaxesTheLookRatherThanReAimingIt()
        {
            using var harness = new GazeShiftTraceHarness(_profile);

            Vector3 target = TargetAt(harness, LargeShiftDegrees);

            // Speaking, settled: the acquisition movement is long over.
            harness.Run(3f, target, engagement: 1f, headContribution: 0.85f);
            int edge = harness.Samples.Count;

            // Settling. Same target, same generation — only the policy moved.
            harness.Run(3f, target, engagement: 0.6f, headContribution: 0.6f);

            float peakSpeed = PeakHeadSpeedAfter(harness.Samples, edge);

            Assert.That(peakSpeed, Is.LessThan(25f),
                $"The head reached {peakSpeed:0.0} deg/s on a conversation-state edge with the " +
                "target unchanged and the generation unchanged.\n" +
                "That speed is a planned movement, not a settle: the ladder's amplitude inputs " +
                "(SettledEngagement, HeadContribution) stepped, the movement detector fired, and " +
                "the actuator re-aimed a look that never moved. GazeShiftDirector.SettleAmplitude " +
                "steps those inputs on a new look — where the step IS the decision — and eases " +
                "them within one, where it is only a re-weighting.");
        }

        /// <summary>
        ///     The other half of the same rule: a genuine re-target must still arrive as a step,
        ///     or the easing above has quietly removed the shaped lane it was added to protect.
        /// </summary>
        /// <remarks>
        ///     This is the failure mode the duration-law work was built to catch and the reason
        ///     the easing is keyed on the generation rather than applied unconditionally: a goal
        ///     that arrives over a ramp never trips the movement detector, so the tracking lane
        ///     executes it — and that lane is transparent to in-budget motion, so the head covers
        ///     the turn at the ramp's speed with no duration law and no velocity profile.
        /// </remarks>
        [Test]
        public void ARetarget_StillReachesTheLadderAsAStep()
        {
            using var harness = new GazeShiftTraceHarness(_profile);

            harness.Run(2f, TargetAt(harness, -LargeShiftDegrees));

            harness.Retarget();
            int retarget = harness.Samples.Count;
            harness.Run(2.5f, TargetAt(harness, LargeShiftDegrees));

            float peakSpeed = PeakHeadSpeedAfter(harness.Samples, retarget);

            Assert.That(peakSpeed, Is.GreaterThan(30f),
                $"A 60 deg re-target only reached {peakSpeed:0.0} deg/s. The ladder must see the " +
                "full destination on the first frame of a new look, or the movement is planned " +
                "against the wrong amplitude — or not planned at all, and merely tracked. " +
                "SettleAmplitude snaps on a generation change for exactly this reason.");
        }
    }
}
