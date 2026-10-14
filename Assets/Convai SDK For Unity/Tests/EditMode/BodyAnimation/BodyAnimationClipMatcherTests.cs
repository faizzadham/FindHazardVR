using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="BodyAnimationClipMatcher" />: every real shipped filename
    ///     (<c>SamplesShared/Art/Animations/Female</c>, 59 FBX — see the shipped animation library), the
    ///     separator/case/gender-token normalisation rules, that ambiguous names never resolve a
    ///     locomotion slot, and that no clip is ever silently dropped from a match pass.
    /// </summary>
    // Internal, not public: the test cases take BodyAnimationLocomotionSlot, which is internal to the
    // module's editor assembly. A public fixture with an internal parameter type is CS0051.
    internal sealed class BodyAnimationClipMatcherTests
    {
        // ---------------------------------------------------------- shipped vocabulary: locomotion

        [TestCase("Anim_F_Walk", BodyAnimationLocomotionSlot.Walk)]
        [TestCase("Anim_F_Jog", BodyAnimationLocomotionSlot.Jog)]
        [TestCase("Anim_F_WalkStart_RF", BodyAnimationLocomotionSlot.WalkStartForward)]
        [TestCase("Anim_F_WalkStart_90L", BodyAnimationLocomotionSlot.WalkStart90Left)]
        [TestCase("Anim_F_WalkStart_90R", BodyAnimationLocomotionSlot.WalkStart90Right)]
        [TestCase("Anim_F_WalkStart_180L", BodyAnimationLocomotionSlot.WalkStart180Left)]
        [TestCase("Anim_F_WalkStart_180R", BodyAnimationLocomotionSlot.WalkStart180Right)]
        [TestCase("Anim_F_JogStart_RF", BodyAnimationLocomotionSlot.JogStartForward)]
        [TestCase("Anim_F_JogStart_90L", BodyAnimationLocomotionSlot.JogStart90Left)]
        [TestCase("Anim_F_JogStart_90R", BodyAnimationLocomotionSlot.JogStart90Right)]
        [TestCase("Anim_F_JogStart_180L", BodyAnimationLocomotionSlot.JogStart180Left)]
        [TestCase("Anim_F_JogStart_180R", BodyAnimationLocomotionSlot.JogStart180Right)]
        [TestCase("Anim_F_WalkStop_LF", BodyAnimationLocomotionSlot.WalkStopLeftPlant)]
        [TestCase("Anim_F_WalkStop_RF", BodyAnimationLocomotionSlot.WalkStopRightPlant)]
        [TestCase("Anim_F_WalkStop_LowSpeed", BodyAnimationLocomotionSlot.WalkStopLowSpeed)]
        [TestCase("Anim_F_WalkStopAbrupt_RF", BodyAnimationLocomotionSlot.WalkStopAbrupt)]
        [TestCase("Anim_F_JogStop_LF", BodyAnimationLocomotionSlot.JogStopLeftPlant)]
        [TestCase("Anim_F_JogStopAbrupt_RF", BodyAnimationLocomotionSlot.JogStopAbrupt)]
        [TestCase("Anim_F_WalkToJog_LF", BodyAnimationLocomotionSlot.WalkToJogLeft)]
        [TestCase("Anim_F_WalkToJog_RF", BodyAnimationLocomotionSlot.WalkToJogRight)]
        [TestCase("Anim_F_JogToWalk_LF", BodyAnimationLocomotionSlot.JogToWalkLeft)]
        [TestCase("Anim_F_JogToWalk_RF", BodyAnimationLocomotionSlot.JogToWalkRight)]
        [TestCase("Anim_F_Turn90_L", BodyAnimationLocomotionSlot.Turn90Left)]
        [TestCase("Anim_F_Turn90_R", BodyAnimationLocomotionSlot.Turn90Right)]
        [TestCase("Anim_F_Turn180_L", BodyAnimationLocomotionSlot.Turn180Left)]
        [TestCase("Anim_F_Turn180_R", BodyAnimationLocomotionSlot.Turn180Right)]
        public void Match_ShippedLocomotionFilename_ResolvesExpectedSlot(string clipName, BodyAnimationLocomotionSlot expected)
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);

            Assert.AreEqual(BodyAnimationSlotCategory.Locomotion, match.Category);
            Assert.AreEqual(expected, match.LocomotionSlot);
            Assert.AreEqual(BodyAnimationMatchConfidence.High, match.Confidence);
        }

        // ---------------------------------------------------------- shipped vocabulary: idle/talk

        [Test]
        public void Match_Idle_ResolvesIdlePool()
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match("Anim_F_Idle");
            Assert.AreEqual(BodyAnimationSlotCategory.Idle, match.Category);
            Assert.AreEqual(BodyAnimationMatchConfidence.High, match.Confidence);
        }

        [Test]
        public void Match_Talk_ResolvesTalkPool()
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match("Anim_F_Talk");
            Assert.AreEqual(BodyAnimationSlotCategory.Talk, match.Category);
            Assert.AreEqual(BodyAnimationMatchConfidence.High, match.Confidence);
        }

        // ---------------------------------------------------------- shipped vocabulary: pointing

        [TestCase("Anim_F_Point_CF", "CF", 0f, 0f)]
        [TestCase("Anim_F_Point_CR", "CR", 90f, 0f)]
        [TestCase("Anim_F_Point_CL", "CL", -90f, 0f)]
        [TestCase("Anim_F_Point_CB", "CB", 180f, 0f)]
        [TestCase("Anim_F_Point_CBL", "CBL", -135f, 0f)]
        [TestCase("Anim_F_Point_DF", "DF", 0f, -45f)]
        [TestCase("Anim_F_Point_DR", "DR", 90f, -45f)]
        [TestCase("Anim_F_Point_DL", "DL", -90f, -45f)]
        [TestCase("Anim_F_Point_DB", "DB", 180f, -45f)]
        [TestCase("Anim_F_Point_DBL", "DBL", -135f, -45f)]
        [TestCase("Anim_F_Point_UF", "UF", 0f, 45f)]
        [TestCase("Anim_F_Point_UR", "UR", 90f, 45f)]
        [TestCase("Anim_F_Point_UL", "UL", -90f, 45f)]
        [TestCase("Anim_F_Point_UB", "UB", 180f, 45f)]
        [TestCase("Anim_F_Point_UBL", "UBL", -135f, 45f)]
        public void Match_ShippedPointingFilename_ResolvesDirectionAndAngles(
            string clipName, string expectedDirection, float expectedYaw, float expectedPitch)
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);

            Assert.AreEqual(BodyAnimationSlotCategory.Pointing, match.Category);
            Assert.AreEqual(expectedDirection, match.PointingDirection);
            Assert.AreEqual(expectedYaw, match.PointingYaw);
            Assert.AreEqual(expectedPitch, match.PointingPitch);
        }

        /// <summary>
        ///     "Piont" is not shipped (the shipped clips were renamed off that typo) but is still
        ///     accepted as a lenient alias — an easy letter transposition to make in a folder of
        ///     third-party clips, and rejecting an otherwise-valid pointing clip over it would be
        ///     needlessly strict for an authoring tool.
        /// </summary>
        [Test]
        public void Match_LegacyPiontTypo_StillMatchesSameAsCorrectlySpelledPoint()
        {
            BodyAnimationSlotMatch typo = BodyAnimationClipMatcher.Match("Anim_F_Piont_CF");
            BodyAnimationSlotMatch correct = BodyAnimationClipMatcher.Match("Anim_F_Point_CF");

            Assert.AreEqual(BodyAnimationSlotCategory.Pointing, correct.Category);
            Assert.AreEqual(typo.PointingDirection, correct.PointingDirection);
            Assert.AreEqual(typo.PointingYaw, correct.PointingYaw);
            Assert.AreEqual(typo.PointingPitch, correct.PointingPitch);
        }

        // ---------------------------------------------------------- shipped vocabulary: actions/gestures

        [TestCase("Anim_F_Clap", ActionMaskMode.FullBody)]
        [TestCase("Anim_F_Disco", ActionMaskMode.FullBody)]
        [TestCase("Anim_F_GStyle", ActionMaskMode.FullBody)]
        [TestCase("Anim_F_Groove", ActionMaskMode.FullBody)]
        [TestCase("Anim_F_Jump360", ActionMaskMode.FullBody)]
        [TestCase("Anim_F_Bye", ActionMaskMode.UpperBody)]
        [TestCase("Anim_F_Hi", ActionMaskMode.UpperBody)]
        [TestCase("Anim_F_Like", ActionMaskMode.UpperBody)]
        [TestCase("Anim_F_No", ActionMaskMode.UpperBody)]
        [TestCase("Anim_F_Yes", ActionMaskMode.UpperBody)]
        [TestCase("Anim_F_Wink_Body", ActionMaskMode.UpperBody)]
        public void Match_ShippedActionFilename_ProposesExpectedMasking(string clipName, ActionMaskMode expectedMask)
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);

            Assert.AreEqual(BodyAnimationSlotCategory.Action, match.Category);
            Assert.AreEqual(expectedMask, match.ProposedMaskMode);
        }

        [TestCase("Anim_F_Think_Loop")]
        [TestCase("Anim_F_Think_In")]
        [TestCase("Anim_F_Think_Out")]
        public void Match_ThinkFamily_ProposesUncertainCueAndHoldUntilStoppedLoop(string clipName)
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);

            Assert.AreEqual(BodyAnimationSlotCategory.Action, match.Category);
            Assert.AreEqual(GestureCueKind.Uncertain, match.ProposedCue);
            Assert.AreEqual(ActionLoopMode.HoldUntilStopped, match.ProposedLoopMode);
        }

        [Test]
        public void Match_WalkInPlace_NotInSlotVocabulary_FallsBackToLowConfidenceAction()
        {
            // Not in the shipped library (removed as unused, never wired into any set) --
            // exercises the true generic fallback (no locomotion slot, no recognised gesture token).
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match("Anim_F_WalkInPlace");

            Assert.AreEqual(BodyAnimationSlotCategory.Action, match.Category);
            Assert.AreEqual(BodyAnimationMatchConfidence.Low, match.Confidence);
        }

        // ---------------------------------------------------------- separator / case / gender tokens

        [TestCase("Anim_F_Walk")]
        [TestCase("Anim-F-Walk")]
        [TestCase("Anim F Walk")]
        [TestCase("ANIM_f_WALK")]
        [TestCase("anim_F_walk")]
        [TestCase("Anim_M_Walk")]
        [TestCase("Walk")]
        public void Match_SeparatorCaseAndGenderVariants_AllResolveToWalk(string clipName)
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);

            Assert.AreEqual(BodyAnimationSlotCategory.Locomotion, match.Category);
            Assert.AreEqual(BodyAnimationLocomotionSlot.Walk, match.LocomotionSlot);
        }

        // ---------------------------------------------------------- ambiguous names

        [TestCase("Anim_F_Walking")]
        [TestCase("Anim_F_WalkCycle")]
        [TestCase("Anim_F_WalkAbout")]
        public void Match_AmbiguousWalkLikeNames_DoNotResolveALocomotionSlot(string clipName)
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);

            Assert.AreNotEqual(BodyAnimationSlotCategory.Locomotion, match.Category);
        }

        // ---------------------------------------------------------- unmatched / never dropped

        [Test]
        public void Match_UnknownGarbageName_BecomesReviewableActionNotDropped()
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match("Xyzzy_Plugh_123");

            // No recognised vocabulary at all still becomes a reviewable Action proposal
            // ("everything else is a candidate action") — Unmatched is reserved for a
            // recognised-but-broken pattern, exercised below.
            Assert.AreEqual(BodyAnimationSlotCategory.Action, match.Category);
            Assert.AreEqual(BodyAnimationMatchConfidence.Low, match.Confidence);
        }

        [Test]
        public void Match_PointingPrefixWithUnrecognisedDirection_IsUnmatched()
        {
            BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match("Anim_F_Point_ZZ");

            Assert.AreEqual(BodyAnimationSlotCategory.Unmatched, match.Category);
            Assert.IsFalse(match.IsMatch);
        }

        [Test]
        public void MatchAll_NeverDropsAClip_EveryClipProducesAProposal()
        {
            var clips = new List<AnimationClip>
            {
                NewClip("Anim_F_Walk"),
                NewClip("Anim_F_Idle"),
                NewClip("Anim_F_Point_ZZ"),
                NewClip("Xyzzy_Plugh_123")
            };
            var proposals = new List<BodyAnimationClipProposal>();

            BodyAnimationClipMatcher.MatchAll(clips, proposals);

            Assert.AreEqual(clips.Count, proposals.Count);
            for (int i = 0; i < clips.Count; i++)
                Assert.AreSame(clips[i], proposals[i].Clip);
        }

        [Test]
        public void MatchAll_UnmatchedProposal_DefaultsExcludedButStillListed()
        {
            var clips = new List<AnimationClip> { NewClip("Anim_F_Point_ZZ") };
            var proposals = new List<BodyAnimationClipProposal>();

            BodyAnimationClipMatcher.MatchAll(clips, proposals);

            Assert.AreEqual(1, proposals.Count);
            Assert.AreEqual(BodyAnimationSlotCategory.Unmatched, proposals[0].Category);
            Assert.IsFalse(proposals[0].Included);
        }

        [Test]
        public void MatchAll_MatchedProposal_DefaultsIncluded()
        {
            var clips = new List<AnimationClip> { NewClip("Anim_F_Walk") };
            var proposals = new List<BodyAnimationClipProposal>();

            BodyAnimationClipMatcher.MatchAll(clips, proposals);

            Assert.IsTrue(proposals[0].Included);
        }

        // ---------------------------------------------------------- exhaustive: every shipped file

        [Test]
        public void Match_EveryShippedFilename_NeverUnmatched()
        {
            foreach (string clipName in AllShippedClipNames())
            {
                BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);
                Assert.AreNotEqual(BodyAnimationSlotCategory.Unmatched, match.Category,
                    $"'{clipName}' should never be Unmatched — every shipped file must resolve to a " +
                    "slot or fall back to a reviewable action.");
            }
        }

        [Test]
        public void MatchAll_EveryShippedFilename_ProducesExactlyOneProposalEach()
        {
            var clips = new List<AnimationClip>();
            foreach (string clipName in AllShippedClipNames()) clips.Add(NewClip(clipName));
            var proposals = new List<BodyAnimationClipProposal>();

            BodyAnimationClipMatcher.MatchAll(clips, proposals);

            Assert.AreEqual(clips.Count, proposals.Count);
        }

        [Test]
        public void Match_ShippedFilenames_CoverAllTwentySixLocomotionSlots()
        {
            var matchedSlots = new HashSet<BodyAnimationLocomotionSlot>();
            foreach (string clipName in AllShippedClipNames())
            {
                BodyAnimationSlotMatch match = BodyAnimationClipMatcher.Match(clipName);
                if (match.Category == BodyAnimationSlotCategory.Locomotion)
                    matchedSlots.Add(match.LocomotionSlot);
            }

            Assert.AreEqual(26, matchedSlots.Count,
                "Every one of the 26 locomotion slots should be reachable from the shipped filenames.");
        }

        /// <summary>
        ///     Every FBX under <c>SamplesShared/Art/Animations/Female</c> (58 files) as of the master
        ///     plan's audit. Mirrors the real library so the exhaustive tests above reflect the actual
        ///     shipped vocabulary rather than a hand-picked subset.
        /// </summary>
        private static IEnumerable<string> AllShippedClipNames()
        {
            // Actions
            yield return "Anim_F_Clap";
            yield return "Anim_F_Disco";
            yield return "Anim_F_GStyle";
            yield return "Anim_F_Groove";
            yield return "Anim_F_Jump360";

            // Gestures
            yield return "Anim_F_Bye";
            yield return "Anim_F_Hi";
            yield return "Anim_F_Like";
            yield return "Anim_F_No";
            yield return "Anim_F_Think";
            yield return "Anim_F_Think_In";
            yield return "Anim_F_Think_Loop";
            yield return "Anim_F_Think_Out";
            yield return "Anim_F_Wink_Body";
            yield return "Anim_F_Yes";

            // Idle / Talk
            yield return "Anim_F_Idle";
            yield return "Anim_F_Talk";

            // Jog
            yield return "Anim_F_Jog";
            yield return "Anim_F_JogStart_180L";
            yield return "Anim_F_JogStart_180R";
            yield return "Anim_F_JogStart_90L";
            yield return "Anim_F_JogStart_90R";
            yield return "Anim_F_JogStart_RF";
            yield return "Anim_F_JogStopAbrupt_RF";
            yield return "Anim_F_JogStop_LF";
            yield return "Anim_F_JogToWalk_LF";
            yield return "Anim_F_JogToWalk_RF";

            // Pointing (15 directions)
            yield return "Anim_F_Point_CB";
            yield return "Anim_F_Point_CBL";
            yield return "Anim_F_Point_CF";
            yield return "Anim_F_Point_CL";
            yield return "Anim_F_Point_CR";
            yield return "Anim_F_Point_DB";
            yield return "Anim_F_Point_DBL";
            yield return "Anim_F_Point_DF";
            yield return "Anim_F_Point_DL";
            yield return "Anim_F_Point_DR";
            yield return "Anim_F_Point_UB";
            yield return "Anim_F_Point_UBL";
            yield return "Anim_F_Point_UF";
            yield return "Anim_F_Point_UL";
            yield return "Anim_F_Point_UR";

            // Turn
            yield return "Anim_F_Turn180_L";
            yield return "Anim_F_Turn180_R";
            yield return "Anim_F_Turn90_L";
            yield return "Anim_F_Turn90_R";

            // Walk
            yield return "Anim_F_Walk";
            yield return "Anim_F_WalkStart_180L";
            yield return "Anim_F_WalkStart_180R";
            yield return "Anim_F_WalkStart_90L";
            yield return "Anim_F_WalkStart_90R";
            yield return "Anim_F_WalkStart_RF";
            yield return "Anim_F_WalkStopAbrupt_RF";
            yield return "Anim_F_WalkStop_LF";
            yield return "Anim_F_WalkStop_LowSpeed";
            yield return "Anim_F_WalkStop_RF";
            yield return "Anim_F_WalkToJog_LF";
            yield return "Anim_F_WalkToJog_RF";
        }

        private static AnimationClip NewClip(string name) => new() { name = name };
    }
}
