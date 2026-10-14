using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Locomotion;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public class LocomotionSelectorsTests
    {
        private readonly List<Object> _cleanup = new();
        private LocomotionSection _section;

        [SetUp]
        public void SetUp() => _section = new LocomotionSection();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
        }

        private void Assign(LocomotionClip slot, string name)
        {
            var clip = new AnimationClip { name = name };
            _cleanup.Add(clip);
            slot.Initialize(clip);
        }

        [Test]
        public void SelectStart_PicksByAngleBucket()
        {
            Assign(_section.WalkStartForward, "fwd");
            Assign(_section.WalkStart90Left, "90l");
            Assign(_section.WalkStart90Right, "90r");
            Assign(_section.WalkStart180Left, "180l");
            Assign(_section.WalkStart180Right, "180r");

            Assert.AreEqual("fwd", LocomotionSelectors.SelectStart(_section, false, 10f, 135f).Clip.ClipName);
            Assert.AreEqual("90r", LocomotionSelectors.SelectStart(_section, false, 70f, 135f).Clip.ClipName);
            Assert.AreEqual("90l", LocomotionSelectors.SelectStart(_section, false, -70f, 135f).Clip.ClipName);
            Assert.AreEqual("180r", LocomotionSelectors.SelectStart(_section, false, 170f, 135f).Clip.ClipName);
            Assert.AreEqual("180l", LocomotionSelectors.SelectStart(_section, false, -170f, 135f).Clip.ClipName);
        }

        [Test]
        public void SelectStart_MissingSide_FallsBackGracefully()
        {
            Assign(_section.WalkStartForward, "fwd");
            // No 90/180 clips assigned.

            MotionChoice choice = LocomotionSelectors.SelectStart(_section, false, 120f, 135f);
            Assert.IsTrue(choice.IsValid);
            Assert.AreEqual("fwd", choice.Clip.ClipName);

            // Nothing assigned at all → invalid, caller degrades to plain blend.
            var empty = new LocomotionSection();
            Assert.IsFalse(LocomotionSelectors.SelectStart(empty, false, 120f, 135f).IsValid);
        }

        [Test]
        public void SelectTurn_Uses180AboveThreshold()
        {
            Assign(_section.Turn90Left, "t90l");
            Assign(_section.Turn90Right, "t90r");
            Assign(_section.Turn180Left, "t180l");
            Assign(_section.Turn180Right, "t180r");

            Assert.AreEqual("t90r", LocomotionSelectors.SelectTurn(_section, 90f, 135f).Clip.ClipName);
            Assert.AreEqual("t180r", LocomotionSelectors.SelectTurn(_section, 160f, 135f).Clip.ClipName);
            Assert.AreEqual("t90l", LocomotionSelectors.SelectTurn(_section, -80f, 135f).Clip.ClipName);
            Assert.AreEqual("t180l", LocomotionSelectors.SelectTurn(_section, -179f, 135f).Clip.ClipName);
        }

        [Test]
        public void SelectStop_PrefersPlantMatch_ThenFallsBack()
        {
            Assign(_section.WalkStopLeftPlant, "lf");
            Assign(_section.WalkStopRightPlant, "rf");
            Assign(_section.WalkStopLowSpeed, "low");
            Assign(_section.WalkStopAbrupt, "abrupt");

            Assert.AreEqual("rf", LocomotionSelectors
                .SelectStop(_section, false, false, false, FootSide.Right).Clip.ClipName);
            Assert.AreEqual("lf", LocomotionSelectors
                .SelectStop(_section, false, false, false, FootSide.Left).Clip.ClipName);
            Assert.AreEqual("low", LocomotionSelectors
                .SelectStop(_section, false, false, true, FootSide.Left).Clip.ClipName);
            Assert.AreEqual("abrupt", LocomotionSelectors
                .SelectStop(_section, false, true, false, FootSide.Left).Clip.ClipName);

            // Jogging without jog stops falls into the walk family.
            Assert.AreEqual("rf", LocomotionSelectors
                .SelectStop(_section, true, false, false, FootSide.Right).Clip.ClipName);
        }

        [Test]
        public void SelectSpeedChange_MatchesPlantFoot()
        {
            Assign(_section.WalkToJogLeft, "wj_lf");
            Assign(_section.WalkToJogRight, "wj_rf");
            Assign(_section.JogToWalkLeft, "jw_lf");

            Assert.AreEqual("wj_rf", LocomotionSelectors
                .SelectSpeedChange(_section, true, FootSide.Right).Clip.ClipName);
            Assert.AreEqual("wj_lf", LocomotionSelectors
                .SelectSpeedChange(_section, true, FootSide.Left).Clip.ClipName);
            // Missing right jog→walk falls back to the left one.
            Assert.AreEqual("jw_lf", LocomotionSelectors
                .SelectSpeedChange(_section, false, FootSide.Right).Clip.ClipName);
        }
    }

    public class FootPhaseUtilTests
    {
        private static ClipMotionMetadata Meta(float[] left, float[] right)
        {
            var meta = new ClipMotionMetadata();
            meta.SetAnalyzed(1f, 1f, 0f, null, null, left, right);
            return meta;
        }

        [Test]
        public void NextPlantFoot_FindsUpcomingPlant_WithWrap()
        {
            ClipMotionMetadata meta = Meta(new[] { 0.1f }, new[] { 0.6f });

            Assert.AreEqual(FootSide.Left, FootPhaseUtil.NextPlantFoot(meta, 0.0f));
            Assert.AreEqual(FootSide.Right, FootPhaseUtil.NextPlantFoot(meta, 0.2f));
            Assert.AreEqual(FootSide.Left, FootPhaseUtil.NextPlantFoot(meta, 0.7f), "wraps past 1.0");
        }

        [Test]
        public void LastPlantFoot_ReturnsLatestMarker()
        {
            Assert.AreEqual(FootSide.Right, FootPhaseUtil.LastPlantFoot(Meta(new[] { 0.2f }, new[] { 0.8f })));
            Assert.AreEqual(FootSide.Left, FootPhaseUtil.LastPlantFoot(Meta(new[] { 0.9f }, new[] { 0.5f })));
            Assert.AreEqual(FootSide.Unknown, FootPhaseUtil.LastPlantFoot(new ClipMotionMetadata()));
        }

        [Test]
        public void HandoffPhase_ContinuesFromMatchingPlant()
        {
            // One-shot ends after a RIGHT plant; walk cycle plants right at 0.55.
            ClipMotionMetadata oneShot = Meta(new[] { 0.3f }, new[] { 0.85f });
            ClipMotionMetadata walk = Meta(new[] { 0.05f }, new[] { 0.55f });

            float phase = FootPhaseUtil.HandoffPhase(oneShot, walk, 0.123f, stanceLead: 0.05f);
            Assert.AreEqual(0.6f, phase, 1e-3f);

            // Without plant data the fallback phase is returned.
            Assert.AreEqual(0.123f, FootPhaseUtil.HandoffPhase(new ClipMotionMetadata(), walk, 0.123f), 1e-4f);
        }
    }

    public class MotionDriveTests
    {
        [Test]
        public void SpeedAt_DerivesVelocityFromDistanceCurve()
        {
            // Linear 0→2m over a 2s clip = 1 m/s. Use linear tangents via evenly spaced keys.
            var curve = AnimationCurve.Linear(0f, 0f, 1f, 2f);
            var meta = new ClipMotionMetadata();
            meta.SetAnalyzed(0f, 2f, 0f, curve, null, null, null);

            float speed = MotionDrive.SpeedAt(meta, 0.5f, clipLength: 2f);
            Assert.AreEqual(1f, speed, 0.05f);
        }

        [Test]
        public void SpeedAt_NoData_ReturnsZero()
        {
            Assert.AreEqual(0f, MotionDrive.SpeedAt(new ClipMotionMetadata(), 0.5f, 1f));
            Assert.AreEqual(0f, MotionDrive.SpeedAt(null, 0.5f, 1f));
        }

        [Test]
        public void YawScale_ClampsAndRejectsFlatClips()
        {
            Assert.AreEqual(1f, MotionDrive.YawScale(90f, 90f), 1e-4f);
            Assert.AreEqual(0.78f, MotionDrive.YawScale(70f, 90f), 0.01f);
            Assert.AreEqual(0.6f, MotionDrive.YawScale(10f, 90f), 1e-4f, "clamped low");
            Assert.AreEqual(1.4f, MotionDrive.YawScale(179f, 90f), 1e-4f, "clamped high");
            Assert.AreEqual(0f, MotionDrive.YawScale(90f, 0.2f), "flat clip yields no scale");
        }

        [Test]
        public void YawDelta_ScalesAuthoredCurve()
        {
            var yawCurve = AnimationCurve.Linear(0f, 0f, 1f, 90f);
            var meta = new ClipMotionMetadata();
            meta.SetAnalyzed(0f, 0f, 90f, null, yawCurve, null, null);

            float delta = MotionDrive.YawDelta(meta, 0.2f, 0.4f, yawScale: 0.5f);
            Assert.AreEqual(9f, delta, 0.01f); // (36−18) × 0.5
        }
    }
}
