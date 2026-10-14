using Convai.Modules.Gaze.Core.Shift;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The WILLING branch of the feet verdict: even when the head's own willingness would
    ///     have left a residual under the feet's entry tolerance, a target genuinely beyond the
    ///     head's and chest's reach must still recruit the feet — see
    ///     <see cref="GazeActuatorLadder.IsBeyondReach" /> and its remarks on the eye budget.
    /// </summary>
    public sealed class GazeBeyondReachBodyTurnTests
    {
        /// <summary>Head 55/32, torso 22/6, willingness 0.85 — a real, not fully committed rig.</summary>
        private static GazeLadderCapacity Capacity() =>
            new(55f, 32f, 22f, 6f, headWillingness: 0.85f, headComfortYaw: 35f,
                torsoAvailable: true, feetAvailable: true, eyeRestDegrees: 14f);

        private static GazeLadderTuning Tuning() =>
            new(headEntryDegrees: 12f, torsoEntryDegrees: 35f, feetEntryDegrees: 25f,
                headOnsetSeconds: 0.12f, torsoOnsetSeconds: 0.15f, feetOnsetSeconds: 0.25f);

        private static GazeShiftPlan Solve(float yaw, float shiftAge, float comfortPressure = 0f)
        {
            var measurement = new GazeShiftMeasurement(yaw, 0f, 0f, 0f);
            GazeLadderCapacity capacity = Capacity();
            GazeLadderTuning tuning = Tuning();
            return GazeActuatorLadder.Solve(
                in measurement, in capacity, in tuning, shiftAge,
                engagement: 0.95f, orbitPressure: 0f, comfortPressure: comfortPressure,
                headEntryEase: 1f, torsoEntryEase: 1f);
        }

        [Test]
        public void UnreachableTarget_PastFeetOnset_WantsFeetEvenUnderTheWillingResidual()
        {
            // 95° is beyond what the head and chest can commit between them, so the eyes would
            // be left past their 14° rest band — the ceiling test the willing residual alone
            // cannot ask.
            GazeShiftPlan plan = Solve(yaw: 95f, shiftAge: 0.3f);

            Assert.IsTrue(plan.WantsFeet,
                "A target out of reach of the head and eyes together must recruit the feet, " +
                "whatever the willing residual says.");
        }

        [Test]
        public void UnreachableTarget_BeforeFeetOnset_DoesNotWantFeetYet()
        {
            // Same look, but the feet's own onset (0.25s) has not elapsed — the cascade still
            // gates on time even when the target is out of reach.
            GazeShiftPlan plan = Solve(yaw: 95f, shiftAge: 0.1f);

            Assert.IsFalse(plan.WantsFeet,
                "The feet are still the last rung in the cascade: out of reach is a reason to " +
                "join, not a reason to skip the onset.");
        }

        [Test]
        public void ReachableTarget_HeadAndTorsoCoverIt_DoesNotWantFeet()
        {
            // 60° sits inside what the head and chest can commit between them, so the eyes end
            // up within their rest band and nothing recruits the feet.
            GazeShiftPlan plan = Solve(yaw: 60f, shiftAge: 0.3f, comfortPressure: 0f);

            Assert.IsFalse(plan.WantsFeet,
                "The head and chest cover this look between them, so the eyes rest inside the " +
                "band and the feet must not join.");
        }
    }
}
