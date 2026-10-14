using System;
using UnityEngine;

namespace Convai.Runtime.Animation
{
    /// <summary>
    ///     Per-region blend weights that control how the Emotion, LipSync and Custom layers
    ///     contribute to a facial region. The compositor interpolates between <c>Idle*</c> and
    ///     <c>Speaking*</c> weights using the smoothed speech blend factor.
    /// </summary>
    /// <remarks>
    ///     A dedicated baked-clip channel existed here until the facial-clip system was removed.
    ///     User-authored facial content now rides the Custom channel via
    ///     <see cref="FacialBlendshapeCompositorHost.RegisterCustomLayer" />, which is the single
    ///     documented extension point rather than one of two general-purpose channels.
    /// </remarks>
    [Serializable]
    public struct RegionBlendConfig
    {
        [Header("Idle (not speaking)")]
        [Range(0f, 1f)] public float IdleEmotionWeight;
        [Range(0f, 1f)] public float IdleLipSyncWeight;
        [Range(0f, 1f)] public float IdleCustomWeight;

        [Header("Speaking")]
        [Range(0f, 1f)] public float SpeakingEmotionWeight;
        [Range(0f, 1f)] public float SpeakingLipSyncWeight;
        [Range(0f, 1f)] public float SpeakingCustomWeight;

        [Header("Blend Mode")]
        public FacialBlendMode Mode;

        [Header("Normalization")]
        [Tooltip("When enabled, the composed result is clamped to 100 and excess is proportionally reduced.")]
        public bool EnableNormalization;

        public static RegionBlendConfig Create(
            float idleEmotion, float idleLipSync,
            float speakingEmotion, float speakingLipSync,
            FacialBlendMode mode = FacialBlendMode.WeightedAdditive,
            float idleCustom = 0f, float speakingCustom = 0f,
            bool enableNormalization = false)
        {
            return new RegionBlendConfig
            {
                IdleEmotionWeight = Mathf.Clamp01(idleEmotion),
                IdleLipSyncWeight = Mathf.Clamp01(idleLipSync),
                IdleCustomWeight = Mathf.Clamp01(idleCustom),
                SpeakingEmotionWeight = Mathf.Clamp01(speakingEmotion),
                SpeakingLipSyncWeight = Mathf.Clamp01(speakingLipSync),
                SpeakingCustomWeight = Mathf.Clamp01(speakingCustom),
                Mode = mode,
                EnableNormalization = enableNormalization
            };
        }

        /// <summary>
        ///     Returns the effective weight for each content layer. LipSync is driven by
        ///     <paramref name="speechFactor" /> (the fast lip-sync ramp); Emotion and Custom are
        ///     driven by <paramref name="emotionFactor" /> (the slower, eased emotion-return ramp)
        ///     so a finished sentence does not snap the emotion/jaw weight back up on the same
        ///     timeline as the mouth shape fading out.
        /// </summary>
        public void GetInterpolatedWeights(
            float speechFactor,
            out float emotionWeight,
            out float lipSyncWeight,
            out float customWeight) =>
            GetInterpolatedWeights(speechFactor, speechFactor, out emotionWeight, out lipSyncWeight, out customWeight);

        /// <summary>
        ///     Returns the effective weight for each content layer with the emotion and custom
        ///     layers on their own factor, so their return after speech can be slower than the
        ///     lip-sync ramp. <see cref="GetInterpolatedWeights(float, out float, out float, out float)" />
        ///     drives all three from one factor, as it always did.
        /// </summary>
        public void GetInterpolatedWeights(
            float speechFactor,
            float emotionFactor,
            out float emotionWeight,
            out float lipSyncWeight,
            out float customWeight)
        {
            emotionWeight = Mathf.Lerp(IdleEmotionWeight, SpeakingEmotionWeight, emotionFactor);
            lipSyncWeight = Mathf.Lerp(IdleLipSyncWeight, SpeakingLipSyncWeight, speechFactor);
            customWeight = Mathf.Lerp(IdleCustomWeight, SpeakingCustomWeight, emotionFactor);
        }

        /// <summary>
        ///     Composes the final blendshape value from all weighted layer contributions.
        /// </summary>
        public float Compose(
            float emotionVal, float emotionWeight,
            float lipSyncVal, float lipSyncWeight,
            float customVal, float customWeight)
        {
            float result;
            switch (Mode)
            {
                case FacialBlendMode.WeightedAdditive:
                    result = (emotionVal * emotionWeight) +
                             (lipSyncVal * lipSyncWeight) +
                             (customVal * customWeight);
                    break;

                case FacialBlendMode.Max:
                    result = Mathf.Max(
                        emotionVal * emotionWeight,
                        Mathf.Max(
                            lipSyncVal * lipSyncWeight,
                            customVal * customWeight));
                    break;

                case FacialBlendMode.Override:
                {
                    float ls = lipSyncVal * lipSyncWeight;
                    if (ls > 0.0001f) { result = ls; break; }
                    float em = emotionVal * emotionWeight;
                    if (em > 0.0001f) { result = em; break; }
                    result = customVal * customWeight;
                    break;
                }

                default:
                    result = emotionVal * emotionWeight;
                    break;
            }

            if (EnableNormalization && result > 100f)
                result = 100f;

            return result;
        }
    }
}
