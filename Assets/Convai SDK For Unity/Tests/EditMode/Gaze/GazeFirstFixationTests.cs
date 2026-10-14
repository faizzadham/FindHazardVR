using System.Text;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The very first thing an idle character does after it binds: idle life decides to look
    ///     somewhere, and the head has to take its part of that look the same way it takes every
    ///     later one — as a shaped movement.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The second is the control for the first: every fixation after the first was always
    ///         executed as a movement, and only the bind frame was not — which is what identified
    ///         the mechanism.
    ///     </para>
    ///     <para>
    ///         The bind frame is the one frame the solver has no previous share for, so
    ///         <c>HeadTorsoSolver.DetectMovement</c> could not ask its usual question. It used to
    ///         answer "no movement" and initialise instead — the stability band centred on the
    ///         share, and the tracking filter, un-seeded, snapping onto it on its first
    ///         <c>Step</c>. Measured: the head covered the whole 10.2° relief share of a 24°
    ///         fixation in a single frame, 613 deg/s at 60 Hz.
    ///     </para>
    ///     <para>
    ///         The question it asks now is the one it can answer: is this share where the head
    ///         already IS? A rig that binds holding its look is still initialised — a rebind
    ///         mid-conversation must not re-run the turn it is at the end of, and SC-7 in
    ///         <c>docs/DEMO-FINDINGS.md</c> seeded the band there deliberately, because a band
    ///         left at zero parked the head a band-width short of every idle fixation for good.
    ///         A share two dozen degrees from the pose is a decision, and goes down the ballistic
    ///         lane like any other; it lands exactly on its goal and re-centres the band there on
    ///         the way out, so SC-7's requirement is met by either route.
    ///     </para>
    /// </remarks>
    public sealed class GazeFirstFixationTests
    {
        /// <summary>
        ///     The fixation idle life hands over. Wide enough that the eyes alone cannot hold it
        ///     inside their comfort band, which is what obliges the head to take a share at all —
        ///     and the angle actually observed in the sample scene.
        /// </summary>
        private const float FixationYawDegrees = -24f;

        private const float FixationPitchDegrees = 6f;

        /// <summary>How long the trace runs, and where the head is asked to have arrived.</summary>
        private const float SettleSeconds = 0.8f;

        /// <summary>
        ///     Fraction of the relief share the head must have covered by <see cref="SettleSeconds" />.
        ///     Not 1: the movement's own duration law (idle tempo on a ~10° turn) puts the landing
        ///     within a few frames of the deadline, and this fixture is about whether the movement
        ///     happens at all, not about trimming its duration.
        /// </summary>
        private const float ArrivalFraction = 0.8f;

        /// <summary>
        ///     Largest single-frame change allowed in the applied head pose, measured from the
        ///     rest pose the character binds in. At 60 Hz this is 180 deg/s — far above anything
        ///     the shipped duration law produces for a turn this size, and far below a snap.
        /// </summary>
        private const float MaxStepDegrees = 3f;

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        /// <summary>
        ///     The head's part of the fixation: what <c>HeadTorsoSolver.AmbientHeadShare</c> owes
        ///     the eyes, i.e. however far past their comfort band the fixation sits.
        /// </summary>
        private float ReliefDegrees =>
            Mathf.Max(
                Mathf.Abs(FixationYawDegrees) * _profile.AmbientHeadFollow,
                Mathf.Abs(FixationYawDegrees) - _profile.EyeComfortDegrees);

        [Test]
        public void FirstFixationAfterBind_IsExecutedAsAMovement()
        {
            using var harness = new GazeShiftTraceHarness(_profile)
            {
                // Idle life is running from the first solved frame and has already chosen a
                // fixation on it — which is exactly what AmbientExplorationDirector does: its
                // first interval is zero, so its first Tick samples a target immediately.
                AmbientActive = true,
                AmbientAngles = new Vector2(FixationYawDegrees, FixationPitchDegrees)
            };

            harness.Run(SettleSeconds, Vector3.zero, hasTarget: false);

            AssertFirstFixationRecruitedTheHead(harness);
        }

        [Test]
        public void FirstOffCentreFixation_AfterARecentredOne_IsExecutedAsAMovement()
        {
            using var harness = new GazeShiftTraceHarness(_profile)
            {
                // The other opening idle life deals itself: the recentre bias makes the first
                // fixation the zero one, so the bind frame carries no share and the first
                // off-centre fixation arrives a moment later. Same requirement, and it is the
                // control for the test above — only one of the two is red, so the bind frame is
                // the whole mechanism.
                AmbientActive = true,
                AmbientAngles = Vector2.zero
            };

            harness.Run(0.5f, Vector3.zero, hasTarget: false);
            int fixationFrame = harness.Samples.Count;
            harness.AmbientAngles = new Vector2(FixationYawDegrees, FixationPitchDegrees);
            harness.Run(SettleSeconds, Vector3.zero, hasTarget: false);

            AssertFirstFixationRecruitedTheHead(harness, fixationFrame);
        }

        /// <summary>
        ///     Both halves of the claim: the head covered its share of the fixation within the
        ///     movement's own time, and it got there by moving rather than by arriving.
        /// </summary>
        /// <param name="fixationFrame">
        ///     Index of the frame idle life handed the fixation over on. Continuity is measured
        ///     from the frame before it — or from the rest pose when the fixation is on the bind
        ///     frame, since a head that binds straight onto its share has stepped there from the
        ///     pose the animation left, which no later query can see.
        /// </param>
        private void AssertFirstFixationRecruitedTheHead(
            GazeShiftTraceHarness harness, int fixationFrame = 0)
        {
            int arrivalFrame = fixationFrame + Mathf.RoundToInt(
                SettleSeconds / GazeShiftTraceHarness.FrameSeconds) - 1;
            arrivalFrame = Mathf.Min(arrivalFrame, harness.Samples.Count - 1);

            float arrived = Mathf.Abs(harness.Samples[arrivalFrame].Head.x);
            float required = ReliefDegrees * ArrivalFraction;

            Assert.That(arrived, Is.GreaterThanOrEqualTo(required),
                $"The head never took its part of the first idle fixation: {arrived:0.000} deg of " +
                $"the {ReliefDegrees:0.000} deg relief share by t={SettleSeconds:0.00}s. The eyes " +
                "are left holding the whole fixation at the corner of the socket until idle life " +
                $"decides on its next one, a whole ambient interval later.{Describe(harness, fixationFrame)}");

            (float step, int stepFrame) = LargestStep(harness, fixationFrame);
            Assert.That(step, Is.LessThanOrEqualTo(MaxStepDegrees),
                $"The head arrived on the first idle fixation rather than moving to it: " +
                $"{step:0.000} deg in one frame on frame {stepFrame} " +
                $"({step * 60f:0} deg/s at the harness's 60 Hz). A fixation is a decision, and the " +
                $"actuator's job is to turn it into a movement — the first one included." +
                Describe(harness, fixationFrame));
        }

        /// <summary>
        ///     Worst one-frame change in the applied head pose from the fixation onward, with the
        ///     bind frame differenced against the rest pose the solver started from.
        /// </summary>
        private static (float Degrees, int Frame) LargestStep(
            GazeShiftTraceHarness harness, int fixationFrame)
        {
            float worst = 0f;
            int worstFrame = -1;
            for (int i = fixationFrame; i < harness.Samples.Count; i++)
            {
                Vector2 previous = i == 0 ? Vector2.zero : harness.Samples[i - 1].Head;
                float degrees = (harness.Samples[i].Head - previous).magnitude;
                if (degrees <= worst) continue;

                worst = degrees;
                worstFrame = i;
            }

            return (worst, worstFrame);
        }

        /// <summary>
        ///     The first frames of applied head yaw, so a failure reports the shape of what
        ///     happened rather than only the number that broke.
        /// </summary>
        private static string Describe(GazeShiftTraceHarness harness, int fixationFrame)
        {
            var text = new StringBuilder("\nApplied head yaw (deg) from the fixation frame:");
            for (int i = fixationFrame; i < harness.Samples.Count; i += 6)
                text.Append($"\n  t={harness.Samples[i].Time:0.000}s  head={harness.Samples[i].Head.x:0.000}" +
                            $"  eye={harness.Samples[i].Eye.x:0.000}  shifting={harness.Samples[i].HeadShiftActive}");

            return text.ToString();
        }
    }
}
