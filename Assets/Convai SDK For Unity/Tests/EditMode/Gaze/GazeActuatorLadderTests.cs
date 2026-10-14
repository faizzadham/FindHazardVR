using Convai.Modules.Gaze.Core.Shift;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The distribution rule: how one gaze shift is divided across eyes → head → torso →
    ///     feet, and in what order the rungs join.
    /// </summary>
    /// <remarks>
    ///     Engine-free, because the ladder is: no rig, no profile, no frame loop. That is the
    ///     point of having pulled the rule out of three solvers that each computed their own
    ///     share from their own threshold and their own timer.
    /// </remarks>
    public sealed class GazeActuatorLadderTests
    {
        /// <summary>Head 55/32, torso 22/6 — the shipped ranges, so the numbers here are real.</summary>
        private static GazeLadderCapacity Capacity(
            float headWillingness = 1f, bool torso = true, bool feet = true,
            float headComfortYaw = 35f, bool facingOwned = false) =>
            new(55f, 32f, 22f, 6f, headWillingness, headComfortYaw, torso, feet,
                facingOwnedElsewhere: facingOwned);

        private static GazeLadderTuning Tuning() =>
            new(headEntryDegrees: 12f, torsoEntryDegrees: 35f, feetEntryDegrees: 25f,
                headOnsetSeconds: 0.06f, torsoOnsetSeconds: 0.15f, feetOnsetSeconds: 0.25f);

        /// <summary>Long enough that every rung's onset has elapsed.</summary>
        private const float Settled = 2f;

        private static GazeShiftMeasurement Shift(float yaw, float pitch = 0f) =>
            new(yaw, pitch, 0f, 0f);

        private static GazeShiftPlan Solve(float yaw, float pitch = 0f, float age = Settled,
            float engagement = 1f, float headWillingness = 1f, bool torso = true, bool feet = true,
            float orbitPressure = 0f, float comfortPressure = 0f, float headComfortYaw = 35f,
            bool facingOwned = false)
        {
            GazeShiftMeasurement measurement = Shift(yaw, pitch);
            GazeLadderCapacity capacity = Capacity(headWillingness, torso, feet, headComfortYaw, facingOwned);
            GazeLadderTuning tuning = Tuning();
            return GazeActuatorLadder.Solve(
                in measurement, in capacity, in tuning, age, engagement, orbitPressure, comfortPressure);
        }

        // ------------------------------------------------------------------ recruitment order

        [Test]
        public void SmallShift_IsLeftEntirelyToTheEyes()
        {
            GazeShiftPlan plan = Solve(yaw: 5f);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.LessThan(0.5f),
                "Below the head's entry angle the eyes handle the whole shift alone.");
            Assert.That(plan.Depth, Is.EqualTo(GazeLadderDepth.Eyes));
        }

        [Test]
        public void MediumShift_RecruitsTheHeadButNotTheTorso()
        {
            GazeShiftPlan plan = Solve(yaw: 25f);

            Assert.That(plan.HeadYaw, Is.GreaterThan(15f));
            Assert.That(Mathf.Abs(plan.TorsoYaw), Is.LessThan(0.5f),
                "25° is below the torso's entry angle.");
            Assert.That(plan.Depth, Is.EqualTo(GazeLadderDepth.Head));
        }

        [Test]
        public void LargeShift_RecruitsTheTorsoForWhatTheHeadCouldNotTake()
        {
            GazeShiftPlan plan = Solve(yaw: 70f);

            Assert.That(plan.TorsoYaw, Is.GreaterThan(1f),
                "Beyond the head's range the torso takes the overflow.");
            Assert.That(plan.HeadYaw, Is.LessThanOrEqualTo(55f + 0.5f),
                "The head never exceeds its anatomical range.");
        }

        // ------------------------------------------------------------------ conservation (I1)

        [Test]
        public void HeadTorsoAndResidual_AlwaysSumToTheRequiredShift()
        {
            for (float yaw = -170f; yaw <= 170f; yaw += 10f)
            {
                GazeShiftPlan plan = Solve(yaw);
                float delivered = plan.HeadYaw + plan.TorsoYaw + plan.FeetResidualYaw;

                Assert.That(delivered, Is.EqualTo(yaw).Within(0.001f),
                    $"Conservation broken at {yaw}°: every rung is handed a remainder, so the " +
                    "parts must reconstruct the whole exactly.");
            }
        }

        [Test]
        public void WithTheTorsoUnavailable_TheResidualGrowsRatherThanVanishing()
        {
            GazeShiftPlan withTorso = Solve(yaw: 90f);
            GazeShiftPlan withoutTorso = Solve(yaw: 90f, torso: false);

            Assert.That(Mathf.Abs(withoutTorso.FeetResidualYaw),
                Is.GreaterThan(Mathf.Abs(withTorso.FeetResidualYaw)),
                "A rung that cannot contribute must hand its share on, not swallow it.");
            Assert.That(withoutTorso.HeadYaw + withoutTorso.FeetResidualYaw,
                Is.EqualTo(90f).Within(0.001f));
        }

        // ------------------------------------------------------------------ onset cascade (I3)

        [Test]
        public void AtTheInstantAShiftBegins_OnlyTheEyesAreMoving()
        {
            GazeShiftPlan plan = Solve(yaw: 60f, age: 0f);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.LessThan(0.5f));
            Assert.That(Mathf.Abs(plan.TorsoYaw), Is.LessThan(0.5f));
            Assert.IsFalse(plan.WantsFeet);
        }

        [Test]
        public void RungsJoinInOrder_HeadThenTorsoThenFeet()
        {
            const float yaw = 120f;

            // Each rung's onset is a gate, not a ramp: the share switches on at the onset and the
            // actuator turns that step into a movement. So the assertion is about WHICH rungs are
            // in at a given age, not about how far a ramp has progressed.
            Assert.That(Mathf.Abs(Solve(yaw, age: 0.1f).HeadYaw), Is.GreaterThan(1f),
                "The head is in once its own onset has passed.");
            Assert.That(Mathf.Abs(Solve(yaw, age: 0.1f).TorsoYaw), Is.LessThan(0.5f),
                "The torso joins after the head, not with it.");

            Assert.That(Mathf.Abs(Solve(yaw, age: 0.2f).TorsoYaw), Is.GreaterThan(1f),
                "The torso is in once its own onset has passed.");

            Assert.IsFalse(Solve(yaw, age: 0.2f).WantsFeet, "The feet are the last rung to join.");
            Assert.IsTrue(Solve(yaw, age: 0.3f).WantsFeet);
        }

        [Test]
        public void OnsetsAreOffsetsOnOneClock_NotWaitsThatStack()
        {
            // Every rung is up by the LONGEST onset, not by their sum. Three independent hold
            // timers used to add up to a visible freeze on arrival; this is the assertion that
            // stops that regressing.
            GazeShiftPlan plan = Solve(yaw: 120f, age: 0.25f + 0.08f + 0.001f);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.GreaterThan(1f));
            Assert.That(Mathf.Abs(plan.TorsoYaw), Is.GreaterThan(1f));
            Assert.IsTrue(plan.WantsFeet);
        }

        // ------------------------------------------------------------------ the feet verdict

        [Test]
        public void TheFeetAnswerToWhatIsLeftOver_NotToTheRawAngle()
        {
            // Same 60° look, two characters. The one whose head barely participates still has
            // most of it unmet and must turn; the one whose head covers it must not. A fixed
            // angle threshold cannot tell these apart — it fires for both, or for neither.
            GazeShiftPlan reluctantHead = Solve(yaw: 60f, headWillingness: 0.1f);
            GazeShiftPlan mobileHead = Solve(yaw: 60f, headWillingness: 1f);

            Assert.IsTrue(reluctantHead.WantsFeet,
                "A head that will not turn leaves the look unmet, so the feet must close it.");
            Assert.IsFalse(mobileHead.WantsFeet,
                "A head and chest that between them cover the look need no body turn.");
        }

        [Test]
        public void WithTheFeetUnavailable_TheLadderNeverAsksForThem()
        {
            GazeShiftPlan plan = Solve(yaw: 170f, feet: false);

            Assert.IsFalse(plan.WantsFeet,
                "While something else owns the character's facing the rung stands down rather " +
                "than competing for the same yaw.");
            Assert.That(Mathf.Abs(plan.FeetResidualYaw), Is.GreaterThan(0f),
                "The residual is still reported — it is what the eyes are left holding.");
        }

        // ------------------------------------------------------------------ gating

        [Test]
        public void NoEngagement_ProducesNoPlanAtAll()
        {
            GazeShiftPlan plan = Solve(yaw: 90f, engagement: 0f);

            Assert.That(plan.HeadYaw, Is.EqualTo(0f));
            Assert.That(plan.TorsoYaw, Is.EqualTo(0f));
            Assert.IsFalse(plan.WantsFeet);
            Assert.That(plan.Depth, Is.EqualTo(GazeLadderDepth.Idle));
        }

        [Test]
        public void PartialEngagement_ScalesTheHeadShareWithoutBreakingConservation()
        {
            GazeShiftPlan plan = Solve(yaw: 40f, engagement: 0.5f);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.LessThan(Mathf.Abs(Solve(yaw: 40f).HeadYaw)));
            Assert.That(plan.HeadYaw + plan.TorsoYaw + plan.FeetResidualYaw,
                Is.EqualTo(40f).Within(0.001f));
        }

        [Test]
        public void PitchIsAllocatedToo_NotJustYaw()
        {
            GazeShiftPlan plan = Solve(yaw: 0f, pitch: -25f);

            Assert.That(plan.HeadPitch, Is.LessThan(-5f),
                "A target well below the eye line recruits the head in pitch, not only in yaw.");
        }

        [Test]
        public void AmplitudeCombinesBothAxes_SoADiagonalLookRecruitsSooner()
        {
            // The head entry fades in across a band around 12°, so the contrast has to be
            // drawn against the band, not against the nominal angle: 6° of yaw alone is below
            // it entirely, while 6° of each is 8.5° of amplitude and is inside it.
            Assert.That(Mathf.Abs(Solve(yaw: 6f).HeadYaw), Is.EqualTo(0f).Within(0.001f));
            Assert.That(Mathf.Abs(Solve(yaw: 6f, pitch: 6f).HeadYaw), Is.GreaterThan(0.1f));
        }

        // ------------------------------------------------------------------ comfort (I4/I5)

        [Test]
        public void OrbitPressure_RecruitsMoreHeadFromAReluctantCharacter()
        {
            GazeShiftPlan relaxed = Solve(yaw: 40f, headWillingness: 0.4f);
            GazeShiftPlan strained = Solve(yaw: 40f, headWillingness: 0.4f, orbitPressure: 1f);

            Assert.That(strained.HeadYaw, Is.GreaterThan(relaxed.HeadYaw),
                "Eyes held off-centre must recruit more head, which is how they get back to " +
                "centre.");
        }

        [Test]
        public void OrbitPressure_NeverDrivesTheHeadPastItsRange()
        {
            GazeShiftPlan plan = Solve(yaw: 170f, headWillingness: 1f, orbitPressure: 1f);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.LessThanOrEqualTo(55f + 0.5f),
                "Pressure interpolates willingness toward 1; it is not an extra term that can " +
                "push past the rig's anatomical range.");
        }

        [Test]
        public void ComfortPressure_TurnsTheFeetEvenWhenNothingIsUnmet()
        {
            // A look the head covers on its own: no residual, so the ladder would not ask for
            // the feet. Once the neck has been held turned long enough, it asks anyway — which
            // is why people turn to face someone they can already see perfectly well.
            Assert.IsFalse(Solve(yaw: 45f).WantsFeet);
            Assert.IsTrue(Solve(yaw: 45f, comfortPressure: 1f).WantsFeet);
        }

        [Test]
        public void ComfortPressure_StillRespectsFeetAvailability()
        {
            Assert.IsFalse(Solve(yaw: 45f, comfortPressure: 1f, feet: false).WantsFeet,
                "A tired neck cannot override something else owning the character's facing.");
        }

        [Test]
        public void ComfortPressure_DoesNotFireBeforeTheFeetOnset()
        {
            Assert.IsFalse(Solve(yaw: 45f, age: 0.1f, comfortPressure: 1f).WantsFeet,
                "The feet are still the last rung in the cascade, however tired the neck is.");
        }

        // ------------------------------------------------------------------ blocked lower rung

        /// <summary>
        ///     While something else owns the character's facing — most often because it is still
        ///     walking — the head must not stretch to its anatomical limit at a target it cannot
        ///     yet turn toward. Doing so reads as trying to look at someone while waiting to
        ///     finish stopping.
        /// </summary>
        [Test]
        public void WithTheFeetBlocked_TheHeadStopsAtWhatTheNeckCanHold()
        {
            GazeShiftPlan free = Solve(yaw: 120f, feet: true);
            GazeShiftPlan blocked = Solve(yaw: 120f, feet: false, facingOwned: true);

            Assert.That(Mathf.Abs(free.HeadYaw), Is.GreaterThan(40f),
                "Sanity: with the feet available the head uses its full range.");
            Assert.That(Mathf.Abs(blocked.HeadYaw), Is.LessThanOrEqualTo(35f + 0.5f),
                "While walking owns the facing the head holds at its comfortable angle instead.");
        }

        /// <summary>
        ///     A policy that disallows the body turn is not the same as walking: a person who
        ///     will not turn round still turns their head as far as it goes.
        /// </summary>
        [Test]
        public void WithTheFeetMerelyDisallowed_TheHeadUsesItsFullRange()
        {
            GazeShiftPlan disallowed = Solve(yaw: 120f, feet: false);

            Assert.That(Mathf.Abs(disallowed.HeadYaw), Is.GreaterThan(40f));
        }

        /// <summary>Conservation still holds — the capped share moves to the residual, not nowhere.</summary>
        [Test]
        public void CappingTheHead_MovesTheShareToTheResidual()
        {
            GazeShiftPlan blocked = Solve(yaw: 120f, feet: false, facingOwned: true);

            Assert.That(blocked.HeadYaw + blocked.TorsoYaw + blocked.FeetResidualYaw,
                Is.EqualTo(120f).Within(0.001f));
        }

        /// <summary>
        ///     A look the neck covers comfortably is unaffected, so a character that simply
        ///     cannot turn its body still looks around normally.
        /// </summary>
        [Test]
        public void WithTheFeetBlocked_AComfortableLookIsUnchanged()
        {
            Assert.That(Solve(yaw: 25f, feet: false, facingOwned: true).HeadYaw,
                Is.EqualTo(Solve(yaw: 25f, feet: true).HeadYaw).Within(0.001f));
        }

        /// <summary>Zero comfort means "no comfort angle authored", not "cap the head at nothing".</summary>
        [Test]
        public void AZeroComfortAngle_DoesNotCapTheHead()
        {
            Assert.That(Mathf.Abs(Solve(yaw: 120f, feet: false, facingOwned: true, headComfortYaw: 0f).HeadYaw),
                Is.GreaterThan(40f));
        }
    }
}
