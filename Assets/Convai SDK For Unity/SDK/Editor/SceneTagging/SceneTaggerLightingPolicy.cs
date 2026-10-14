using UnityEngine;

namespace Convai.Editor.SceneTagging
{
    // Presentation acceptance thresholds ported from SceneAutoTaggerCapture.cpp,
    // Convai Unreal source e72d938. Rendering implementations remain pipeline-specific.
    internal static class SceneTaggerLightingPolicy
    {
        internal static bool ShouldAttemptHighlightRecovery(SceneTaggerImageQuality Metrics)
        {
            bool bBroadShoulderCompression = Metrics.ShoulderFraction >= 0.38f
                && Metrics.MedianLuminance >= 205.0f
                && Metrics.P90Luminance >= 238.0f;
            bool bLiteralClipping = Metrics.ClippedFraction >= 0.10f
                && Metrics.P90Luminance >= 248.0f;
            return Metrics.ForegroundSamples >= 64
                && Metrics.ForegroundFraction >= 0.02f
                && Metrics.LowChromaFraction >= 0.60f
                && (bBroadShoulderCompression || bLiteralClipping);
        }

        internal static float ComputeHighlightRecoveryExposureBias(SceneTaggerImageQuality Metrics)
        {
            float ShoulderSeverity = Mathf.Clamp(
                (Metrics.ShoulderFraction - 0.35f) / 0.55f, 0.0f, 1.0f);
            float MedianSeverity = Mathf.Clamp(
                (Metrics.MedianLuminance - 205.0f) / 45.0f, 0.0f, 1.0f);
            float P90Severity = Mathf.Clamp(
                (Metrics.P90Luminance - 238.0f) / 17.0f, 0.0f, 1.0f);
            float ClipSeverity = Mathf.Clamp(Metrics.ClippedFraction / 0.60f, 0.0f, 1.0f);
            float Severity = Mathf.Clamp(
                ShoulderSeverity * 0.35f
                    + MedianSeverity * 0.25f
                    + P90Severity * 0.20f
                    + ClipSeverity * 0.20f,
                0.0f,
                1.0f);
            return Mathf.Lerp(-0.75f, -2.0f, Severity);
        }

        internal static bool PreferHighlightRecovery(
            SceneTaggerImageQuality Original,
            SceneTaggerImageQuality Recovery)
        {
            if (Recovery.ForegroundSamples < Mathf.RoundToInt(Original.ForegroundSamples * 0.80f)
                || Recovery.MeanLuminance < 56.0f
                || Recovery.MedianLuminance < 72.0f
                || Recovery.P90Luminance < 150.0f)
            {
                return false;
            }
            float RequiredShoulderReduction = Mathf.Max(
                0.08f,
                Original.ShoulderFraction * 0.18f);
            bool bRecoveredHeadroom = Original.P90Luminance - Recovery.P90Luminance >= 6.0f
                && Original.ShoulderFraction - Recovery.ShoulderFraction >= RequiredShoulderReduction;
            bool bRecoveredTone = Recovery.ToneSpan >= Original.ToneSpan + 4.0f;
            bool bRecoveredDetail = Recovery.InteriorDetail >= Original.InteriorDetail + 0.50f
                && Recovery.InteriorDetail >= Original.InteriorDetail * 1.08f;
            return bRecoveredHeadroom && (bRecoveredTone || bRecoveredDetail);
        }

        internal static bool ShouldAttemptDarkRecovery(SceneTaggerImageQuality Metrics)
        {
            return Metrics.ForegroundSamples >= 64
                && Metrics.ForegroundFraction >= 0.02f
                && Metrics.MedianLuminance < 48.0f
                && Metrics.P90Luminance < 112.0f;
        }

        internal static bool PreferDarkRecovery(
            SceneTaggerImageQuality Original,
            SceneTaggerImageQuality Recovery)
        {
            if (Recovery.ForegroundSamples < Mathf.RoundToInt(Original.ForegroundSamples * 0.80f)
                || Recovery.ShoulderFraction > 0.24f
                || Recovery.ClippedFraction > 0.04f)
            {
                return false;
            }
            bool bMeaningfullyBrighter = Recovery.MedianLuminance >= Original.MedianLuminance + 14.0f
                && Recovery.P90Luminance >= Original.P90Luminance + 18.0f;
            bool bRetainsStructure = Recovery.ToneSpan >= Mathf.Max(18.0f, Original.ToneSpan * 0.85f)
                && Recovery.InteriorDetail >= Original.InteriorDetail * 0.80f;
            return bMeaningfullyBrighter && bRetainsStructure;
        }

        internal static bool ShouldAttemptOpposingDiffuseRecovery(
            SceneTaggerImageQuality Metrics,
            bool bSingleDiffuseFillRetained,
            bool bSuppressDirectSpecularHighlights)
        {
            return bSingleDiffuseFillRetained
                && !bSuppressDirectSpecularHighlights
                && Metrics.ForegroundSamples >= 64
                && Metrics.ForegroundFraction >= 0.02f
                && Metrics.MedianLuminance >= 80.0f
                && Metrics.MedianLuminance <= 190.0f
                && Metrics.P90Luminance >= 130.0f
                && Metrics.P90Luminance <= 220.0f
                && Metrics.P25Luminance <= 90.0f
                && Metrics.MedianLuminance - Metrics.P25Luminance >= 40.0f
                && Metrics.ShoulderFraction <= 0.18f
                && Metrics.ClippedFraction <= 0.015f;
        }

        internal static bool PreferOpposingDiffuseRecovery(
            SceneTaggerImageQuality Original,
            SceneTaggerImageQuality Recovery)
        {
            if (Recovery.ForegroundSamples < Mathf.RoundToInt(Original.ForegroundSamples * 0.90f)
                || Recovery.ForegroundFraction < Original.ForegroundFraction * 0.90f)
            {
                return false;
            }
            float OriginalShadowGap = Original.MedianLuminance - Original.P25Luminance;
            float RecoveryShadowGap = Recovery.MedianLuminance - Recovery.P25Luminance;
            bool bShadowLifted = Recovery.P25Luminance >= Original.P25Luminance + 12.0f
                && RecoveryShadowGap <= OriginalShadowGap * 0.90f;
            bool bHighlightsBounded = Recovery.MedianLuminance >= Original.MedianLuminance - 4.0f
                && Recovery.MedianLuminance <= Original.MedianLuminance + 32.0f
                && Recovery.P90Luminance <= Mathf.Min(224.0f, Original.P90Luminance + 20.0f)
                && Recovery.ShoulderFraction <= 0.18f
                && Recovery.ClippedFraction <= 0.015f;
            bool bStructureRetained = Recovery.ToneSpan >= Mathf.Max(
                48.0f,
                Original.ToneSpan * 0.72f)
                && Recovery.InteriorDetail >= Mathf.Max(
                    3.0f,
                    Original.InteriorDetail * 0.82f);
            return bShadowLifted && bHighlightsBounded && bStructureRetained;
        }

        internal static bool ShouldAttemptDimensionalShadowBalanceRecovery(
            SceneTaggerImageQuality Metrics,
            bool bSuppressDirectSpecularHighlights)
        {
            bool bExistingHighlightRange = Metrics.P90Luminance >= 112.0f;
            bool bDarkBoundaryRange = Metrics.P90Luminance >= 96.0f
                && Metrics.P25Luminance <= 32.0f
                && Metrics.MedianLuminance < 52.0f;
            return !bSuppressDirectSpecularHighlights
                && Metrics.ForegroundSamples >= 64
                && Metrics.ForegroundFraction >= 0.02f
                && Metrics.P25Luminance <= 64.0f
                && Metrics.MedianLuminance <= 100.0f
                && (bExistingHighlightRange || bDarkBoundaryRange)
                && Metrics.P90Luminance <= 220.0f
                && Metrics.P90Luminance - Metrics.P25Luminance >= 64.0f
                && (Metrics.MedianLuminance - Metrics.P25Luminance >= 24.0f
                    || Metrics.P90Luminance - Metrics.MedianLuminance >= 48.0f)
                && Metrics.ShoulderFraction <= 0.12f
                && Metrics.ClippedFraction <= 0.01f;
        }

        internal static bool PreferDimensionalShadowBalanceRecovery(
            SceneTaggerImageQuality Original,
            SceneTaggerImageQuality Recovery)
        {
            bool bSafeCoverage = Recovery.ForegroundSamples >= 64
                && Recovery.ForegroundSamples >= Mathf.RoundToInt(
                    Original.ForegroundSamples * 0.90f)
                && Recovery.ForegroundFraction >= Original.ForegroundFraction * 0.90f;
            float P25Lift = Recovery.P25Luminance - Original.P25Luminance;
            float MedianLift = Recovery.MedianLuminance - Original.MedianLuminance;
            float HighlightLift = Recovery.P90Luminance - Original.P90Luminance;
            bool bBalancedShadowLift = P25Lift >= 8.0f
                && MedianLift >= 4.0f
                && P25Lift >= Mathf.Max(4.0f, HighlightLift * 0.35f);
            bool bDarkMetalMidtoneLift = Original.P25Luminance <= 24.0f
                && Original.MedianLuminance <= 60.0f
                && P25Lift >= -2.0f
                && MedianLift >= 10.0f
                && HighlightLift >= 6.0f
                && HighlightLift <= 32.0f;
            bool bSafeHighlights = MedianLift <= 24.0f
                && Recovery.P90Luminance <= Mathf.Min(
                    220.0f,
                    Original.P90Luminance + 32.0f)
                && Recovery.ShoulderFraction <= 0.10f
                && Recovery.ClippedFraction <= 0.01f;
            bool bStructureRetained = Recovery.ToneSpan >= Mathf.Max(
                48.0f,
                Original.ToneSpan * 0.75f)
                && Recovery.InteriorDetail >= Mathf.Max(
                    3.0f,
                    Original.InteriorDetail * 0.88f);
            return bSafeCoverage
                && (bBalancedShadowLift || bDarkMetalMidtoneLift)
                && bSafeHighlights
                && bStructureRetained;
        }

        internal static bool ShouldAttemptDimensionalReadabilityFloorRecovery(
            SceneTaggerImageQuality Metrics,
            bool bSuppressDirectSpecularHighlights)
        {
            return !bSuppressDirectSpecularHighlights
                && Metrics.ForegroundSamples >= 64
                && Metrics.ForegroundFraction >= 0.02f
                && Metrics.P25Luminance <= 32.0f
                && Metrics.MedianLuminance < 52.0f
                && Metrics.P90Luminance <= 220.0f
                && Metrics.ShoulderFraction <= 0.10f
                && Metrics.ClippedFraction <= 0.01f;
        }

        internal static bool PreferDimensionalReadabilityFloorRecovery(
            SceneTaggerImageQuality Original,
            SceneTaggerImageQuality Recovery)
        {
            bool bOriginalBelowFloor = Original.ForegroundSamples >= 64
                && Original.ForegroundFraction >= 0.02f
                && Original.P25Luminance <= 32.0f
                && Original.MedianLuminance < 52.0f;
            bool bSafeCoverage = Recovery.ForegroundSamples >= 64
                && Recovery.ForegroundSamples >= Mathf.RoundToInt(
                    Original.ForegroundSamples * 0.90f)
                && Recovery.ForegroundFraction >= Original.ForegroundFraction * 0.90f;
            bool bMeaningfulLift = Recovery.P25Luminance >= Original.P25Luminance - 2.0f
                && Recovery.MedianLuminance >= Original.MedianLuminance + 8.0f;
            bool bSafeHighlights = Recovery.P90Luminance <= 220.0f
                && Recovery.ShoulderFraction <= 0.10f
                && Recovery.ClippedFraction <= 0.01f;
            bool bStructureRetained = Recovery.ToneSpan >= Mathf.Max(
                36.0f,
                Original.ToneSpan * 0.85f)
                && Recovery.InteriorDetail >= Mathf.Max(
                    3.0f,
                    Original.InteriorDetail * 0.85f);
            return bOriginalBelowFloor
                && bSafeCoverage
                && bMeaningfulLift
                && bSafeHighlights
                && bStructureRetained;
        }

        internal static bool ShouldAttemptPlanarDiffuseExposureRecovery(
            SceneTaggerImageQuality Metrics,
            bool bSuppressDirectSpecularHighlights,
            bool bAllowSuppressedSpecularRecovery)
        {
            return bSuppressDirectSpecularHighlights
                && !bAllowSuppressedSpecularRecovery
                && Metrics.ForegroundSamples >= 64
                && Metrics.ForegroundFraction >= 0.02f
                && Metrics.P25Luminance < 52.0f
                && Metrics.MedianLuminance < 88.0f
                && Metrics.P90Luminance < 214.0f
                && Metrics.ShoulderFraction <= 0.16f
                && Metrics.ClippedFraction <= 0.01f;
        }

        internal static bool PreferPlanarDiffuseExposureRecovery(
            SceneTaggerImageQuality Original,
            SceneTaggerImageQuality Recovery)
        {
            if (Recovery.ForegroundSamples < Mathf.RoundToInt(Original.ForegroundSamples * 0.80f)
                || Recovery.ForegroundFraction < Original.ForegroundFraction * 0.80f
                || Recovery.P90Luminance > 224.0f
                || Recovery.MedianLuminance > 170.0f
                || Recovery.ShoulderFraction > 0.18f
                || Recovery.ClippedFraction > 0.015f)
            {
                return false;
            }
            bool bMedianLifted = Recovery.MedianLuminance >= Original.MedianLuminance + 10.0f;
            bool bUpperTonesPreserved = Recovery.P90Luminance >= Original.P90Luminance - 4.0f;
            bool bLowerQuartileLifted = Recovery.P25Luminance >= Original.P25Luminance + 8.0f;
            bool bStrongDistributionLift = Recovery.P25Luminance >= Original.P25Luminance + 4.0f
                && Recovery.MedianLuminance >= Original.MedianLuminance + 12.0f
                && Recovery.P90Luminance >= Original.P90Luminance + 20.0f;
            bool bLowerTonesLifted = bMedianLifted
                && bUpperTonesPreserved
                && (bLowerQuartileLifted || bStrongDistributionLift);
            bool bStructureRetained = Recovery.ToneSpan >= Mathf.Max(
                24.0f,
                Original.ToneSpan * 0.70f)
                && Recovery.InteriorDetail >= Mathf.Max(
                    1.5f,
                    Original.InteriorDetail * 0.75f);
            return bLowerTonesLifted && bStructureRetained;
        }
    }
}
