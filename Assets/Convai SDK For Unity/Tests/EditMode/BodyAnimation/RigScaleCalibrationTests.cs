using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Locomotion;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary><see cref="MotionScaleResolver.Resolve" /> — factor resolution.</summary>
    internal class MotionScaleResolverTests
    {
        [Test]
        public void Resolve_KnownAuthoredScale_ScalesByRatio()
        {
            // Target rig measures 1.2x; authored (walk clip) measured 1.0x → scale 1.2.
            float scale = MotionScaleResolver.Resolve(
                humanScale: 1f, lossyScale: new Vector3(1.2f, 1.2f, 1.2f), authoredWalkMotionScale: 1f);

            Assert.That(scale, Is.EqualTo(1.2f).Within(1e-4f));
        }

        [Test]
        public void Resolve_UnknownAuthoredScale_AssumesReferenceRig()
        {
            // authoredWalkMotionScale 0 = unknown (earlier/unanalyzed metadata) — falls back to
            // DefaultAuthoredMotionScale (1), so the result equals the target scale directly.
            float scale = MotionScaleResolver.Resolve(
                humanScale: 1f, lossyScale: new Vector3(1.2f, 1.2f, 1.2f), authoredWalkMotionScale: 0f);

            Assert.That(scale, Is.EqualTo(1.2f).Within(1e-4f));
        }

        [Test]
        public void Resolve_ClampsHigh()
        {
            float scale = MotionScaleResolver.Resolve(
                humanScale: 3f, lossyScale: Vector3.one, authoredWalkMotionScale: 1f);

            Assert.That(scale, Is.EqualTo(MotionScaleResolver.MaxScale).Within(1e-4f));
        }

        [Test]
        public void Resolve_ClampsLow()
        {
            float scale = MotionScaleResolver.Resolve(
                humanScale: 0.1f, lossyScale: Vector3.one, authoredWalkMotionScale: 1f);

            Assert.That(scale, Is.EqualTo(MotionScaleResolver.MinScale).Within(1e-4f));
        }

        [Test]
        public void Resolve_WithinDeadband_ReturnsExactlyOne()
        {
            // 1.5% off — inside the 2% deadband.
            float scale = MotionScaleResolver.Resolve(
                humanScale: 1.015f, lossyScale: Vector3.one, authoredWalkMotionScale: 1f);

            Assert.That(scale, Is.EqualTo(1f));
        }

        [Test]
        public void Resolve_OutsideDeadband_ReturnsScaledValue()
        {
            // 3% off — outside the 2% deadband, must NOT snap to 1.
            float scale = MotionScaleResolver.Resolve(
                humanScale: 1.03f, lossyScale: Vector3.one, authoredWalkMotionScale: 1f);

            Assert.That(scale, Is.Not.EqualTo(1f));
            Assert.That(scale, Is.EqualTo(1.03f).Within(1e-4f));
        }
    }

    /// <summary><see cref="MotionScaleResolver.FindMismatchedClips" /> — the set consistency check.</summary>
    internal class MotionScaleConsistencyTests
    {
        // No AnimationClip is assigned — FindMismatchedClips only reads Metadata, never Clip/IsValid.
        private static LocomotionClip ClipWithScale(float scale)
        {
            var clip = new LocomotionClip();
            clip.Metadata.SetAnalyzed(1f, 1f, 0f, null, null, null, null, authoredMotionScale: scale);
            return clip;
        }

        [Test]
        public void FindMismatchedClips_NamesDisagreeingClip()
        {
            var clips = new List<(string slot, LocomotionClip clip)>
            {
                ("Walk", ClipWithScale(1f)),
                ("Jog", ClipWithScale(1.2f)) // 20% off — well past the 5% threshold
            };

            string outliers = MotionScaleResolver.FindMismatchedClips(clips, walkAuthoredScale: 1f);

            Assert.That(outliers, Is.EqualTo("Jog"));
        }

        [Test]
        public void FindMismatchedClips_IgnoresUnknownScaleClips()
        {
            var clips = new List<(string slot, LocomotionClip clip)>
            {
                ("Walk", ClipWithScale(1f)),
                ("Jog", ClipWithScale(0f)) // unknown — never counts as disagreeing
            };

            string outliers = MotionScaleResolver.FindMismatchedClips(clips, walkAuthoredScale: 1f);

            Assert.That(outliers, Is.Null);
        }

        [Test]
        public void FindMismatchedClips_WithinThreshold_ReportsNothing()
        {
            var clips = new List<(string slot, LocomotionClip clip)>
            {
                ("Walk", ClipWithScale(1f)),
                ("Jog", ClipWithScale(1.02f)) // 2% — within the 5% threshold
            };

            string outliers = MotionScaleResolver.FindMismatchedClips(clips, walkAuthoredScale: 1f);

            Assert.That(outliers, Is.Null);
        }
    }

    /// <summary><see cref="MotionDrive.SpeedAt" /> and <see cref="MotionDrive.NormalizedTimeAtDistance" /> scaling.</summary>
    internal class MotionDriveScalingTests
    {
        [Test]
        public void SpeedAt_ScaleOne_IsIdentical()
        {
            var curve = AnimationCurve.Linear(0f, 0f, 1f, 2f);
            var meta = new ClipMotionMetadata();
            meta.SetAnalyzed(0f, 2f, 0f, curve, null, null, null);

            float unscaled = MotionDrive.SpeedAt(meta, 0.5f, clipLength: 2f);
            float scaled = MotionDrive.SpeedAt(meta, 0.5f, clipLength: 2f, motionScale: 1f);

            Assert.That(scaled, Is.EqualTo(unscaled));
        }

        [Test]
        public void SpeedAt_ScalesResultByMotionScale()
        {
            var curve = AnimationCurve.Linear(0f, 0f, 1f, 2f);
            var meta = new ClipMotionMetadata();
            meta.SetAnalyzed(0f, 2f, 0f, curve, null, null, null);

            float baseline = MotionDrive.SpeedAt(meta, 0.5f, clipLength: 2f);
            float scaled = MotionDrive.SpeedAt(meta, 0.5f, clipLength: 2f, motionScale: 1.5f);

            Assert.That(scaled, Is.EqualTo(baseline * 1.5f).Within(0.05f));
        }

        [Test]
        public void NormalizedTimeAtDistance_ScaleOne_IsIdentical()
        {
            var curve = AnimationCurve.Linear(0f, 0f, 1f, 4f);
            var meta = new ClipMotionMetadata();
            meta.SetAnalyzed(0f, 4f, 0f, curve, null, null, null);

            float unscaled = MotionDrive.NormalizedTimeAtDistance(meta, 2f);
            float scaled = MotionDrive.NormalizedTimeAtDistance(meta, 2f, motionScale: 1f);

            Assert.That(scaled, Is.EqualTo(unscaled).Within(1e-5f));
        }

        [Test]
        public void NormalizedTimeAtDistance_DividesWorldMetersByScale()
        {
            // Authored curve travels 0→4m over the clip. Asking for 2 WORLD metres on a rig
            // measuring 2x scale must land at the point the curve covers 1 authored metre —
            // half as far into the (unscaled) curve as the unscaled case.
            var curve = AnimationCurve.Linear(0f, 0f, 1f, 4f);
            var meta = new ClipMotionMetadata();
            meta.SetAnalyzed(0f, 4f, 0f, curve, null, null, null);

            float unscaledHalfway = MotionDrive.NormalizedTimeAtDistance(meta, 2f); // authored 2m → t=0.5
            float scaledQuarter = MotionDrive.NormalizedTimeAtDistance(meta, 2f, motionScale: 2f); // world 2m → authored 1m → t=0.25

            Assert.That(unscaledHalfway, Is.EqualTo(0.5f).Within(0.02f));
            Assert.That(scaledQuarter, Is.EqualTo(0.25f).Within(0.02f));
        }
    }

    /// <summary><see cref="NavMeshAgentDimensionResolver.Resolve" /> — agent capsule derivation.</summary>
    internal class NavMeshAgentDimensionResolverTests
    {
        [Test]
        public void Resolve_HeadBonePresent_DerivesHeightFromHeadHeight()
        {
            // 1.7m to the head bone × the crown factor → within the plausible band, no clamping.
            (float height, float radius) = NavMeshAgentDimensionResolver.Resolve(
                headHeightAboveRoot: 1.7f, humanScale: 1f);

            float expectedHeight = 1.7f * NavMeshAgentDimensionResolver.HeadCrownFactor;
            Assert.That(height, Is.EqualTo(expectedHeight).Within(1e-4f));
            Assert.That(radius, Is.EqualTo(expectedHeight * NavMeshAgentDimensionResolver.RadiusRatio).Within(1e-4f));
        }

        [Test]
        public void Resolve_HeadBoneUnmapped_FallsBackToHumanScale()
        {
            (float height, float radius) = NavMeshAgentDimensionResolver.Resolve(
                headHeightAboveRoot: null, humanScale: 1f);

            float expectedHeight = 1f * NavMeshAgentDimensionResolver.FallbackHumanScaleFactor;
            Assert.That(height, Is.EqualTo(expectedHeight).Within(1e-4f));
            Assert.That(radius, Is.EqualTo(expectedHeight * NavMeshAgentDimensionResolver.RadiusRatio).Within(1e-4f));
        }

        [Test]
        public void Resolve_ClampsToMinHeight()
        {
            (float height, float _) = NavMeshAgentDimensionResolver.Resolve(
                headHeightAboveRoot: 0.1f, humanScale: 1f);

            Assert.That(height, Is.EqualTo(NavMeshAgentDimensionResolver.MinHeight));
        }

        [Test]
        public void Resolve_ClampsToMaxHeight()
        {
            (float height, float _) = NavMeshAgentDimensionResolver.Resolve(
                headHeightAboveRoot: 5f, humanScale: 1f);

            Assert.That(height, Is.EqualTo(NavMeshAgentDimensionResolver.MaxHeight));
        }
    }
}
