using Convai.Runtime.Animation;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Covers the defect where a finished sentence popped the Mouth/Jaw region's Emotion weight
    ///     back up on the same fast timeline as the LipSync ramp-down (e.g. "surprise" JawOpen
    ///     jumping ~8% to ~38% in 0.4 s). <see cref="RegionBlendConfig.GetInterpolatedWeights" /> must
    ///     read LipSync from the fast speech factor and Emotion/Custom from the slower, eased emotion
    ///     factor, and <see cref="EmotionReturnRamp" /> must actually deliver that slower, eased curve.
    /// </summary>
    [TestFixture]
    public sealed class SpeechBlendRampsTests
    {
        private const float FrameDelta = 1f / 60f;

        [Test]
        public void GetInterpolatedWeights_LipSyncFollowsSpeechFactor_EmotionFollowsEmotionFactor()
        {
            RegionBlendConfig config = RegionBlendConfig.Create(
                idleEmotion: 0.2f, idleLipSync: 0f,
                speakingEmotion: 1f, speakingLipSync: 1f,
                idleCustom: 0.1f, speakingCustom: 0.9f);

            // Mid-return: lip sync has already fully returned to idle (speechFactor = 0) while the
            // emotion factor is still mostly at its speaking value — this is exactly the moment the
            // defect fired.
            config.GetInterpolatedWeights(
                speechFactor: 0f, emotionFactor: 0.8f,
                out float emotionWeight, out float lipSyncWeight, out float customWeight);

            Assert.That(lipSyncWeight, Is.EqualTo(0f).Within(1e-5f),
                "LipSync weight must track speechFactor only.");
            Assert.That(emotionWeight, Is.EqualTo(Mathf.Lerp(0.2f, 1f, 0.8f)).Within(1e-5f),
                "Emotion weight must track emotionFactor, not speechFactor.");
            Assert.That(customWeight, Is.EqualTo(Mathf.Lerp(0.1f, 0.9f, 0.8f)).Within(1e-5f),
                "Custom weight must track emotionFactor, not speechFactor.");
        }

        [Test]
        public void GetInterpolatedWeights_FactorsIndependent_DivergingValuesProduceDivergingWeights()
        {
            RegionBlendConfig config = RegionBlendConfig.Create(
                idleEmotion: 0f, idleLipSync: 0f,
                speakingEmotion: 1f, speakingLipSync: 1f);

            config.GetInterpolatedWeights(speechFactor: 0f, emotionFactor: 1f,
                out float emotionWeight, out float lipSyncWeight, out _);

            Assert.That(lipSyncWeight, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(emotionWeight, Is.EqualTo(1f).Within(1e-5f),
                "With speechFactor at 0 and emotionFactor at 1, the two layers must be able to " +
                "disagree — proving they are no longer driven by a single shared factor.");
        }

        [Test]
        public void EmotionReturnRamp_ReachesIdleOnlyAfterApproximatelyReturnDuration()
        {
            const float rampUp = 0.15f;
            const float returnDuration = 1.2f;

            var ramp = new EmotionReturnRamp();
            ramp.Step(1f, 10f, rampUp, returnDuration); // snap to fully speaking
            Assert.That(ramp.Value, Is.EqualTo(1f).Within(1e-4f));

            float elapsed = 0f;
            while (ramp.Value > 0f && elapsed < returnDuration * 3f)
            {
                ramp.Step(0f, FrameDelta, rampUp, returnDuration);
                elapsed += FrameDelta;
            }

            Assert.That(elapsed, Is.GreaterThanOrEqualTo(returnDuration - FrameDelta),
                "The emotion factor must not reach idle noticeably before EmotionReturnDuration has elapsed.");
            Assert.That(elapsed, Is.LessThan(returnDuration + FrameDelta * 4f),
                "The emotion factor must reach idle at approximately EmotionReturnDuration, not drift long.");
        }

        [Test]
        public void EmotionReturnRamp_ReturnIsMonotonicAndEasedAtBothEnds()
        {
            const float rampUp = 0.15f;
            const float returnDuration = 1.2f;

            var ramp = new EmotionReturnRamp();
            ramp.Step(1f, 10f, rampUp, returnDuration);

            float previous = ramp.Value;
            float firstStepDelta = 0f;
            float lastStepDelta = 0f;
            float midStepDelta = 0f;
            int stepIndex = 0;
            int totalSteps = Mathf.CeilToInt(returnDuration / FrameDelta);

            for (stepIndex = 0; stepIndex < totalSteps; stepIndex++)
            {
                ramp.Step(0f, FrameDelta, rampUp, returnDuration);
                float delta = previous - ramp.Value;

                // Monotonic: the value must never increase while returning to idle.
                Assert.That(delta, Is.GreaterThanOrEqualTo(-1e-5f),
                    $"Emotion return must be monotonic (step {stepIndex}).");

                if (stepIndex == 0) firstStepDelta = delta;
                if (stepIndex == totalSteps / 2) midStepDelta = delta;
                lastStepDelta = delta;

                previous = ramp.Value;
            }

            Assert.That(ramp.Value, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(firstStepDelta, Is.LessThan(midStepDelta),
                "The return must start gently (near-zero velocity), not with the mid-ramp rate.");
            Assert.That(lastStepDelta, Is.LessThan(midStepDelta),
                "The return must end gently (near-zero velocity), not with the mid-ramp rate.");
        }

        [Test]
        public void EmotionReturnRamp_ReEntryMidReturn_RampsUpFromCurrentValueWithoutSnapping()
        {
            const float rampUp = 0.15f;
            const float returnDuration = 1.2f;

            var ramp = new EmotionReturnRamp();
            ramp.Step(1f, 10f, rampUp, returnDuration);

            // Return partway, then speech resumes.
            for (int i = 0; i < 30; i++)
                ramp.Step(0f, FrameDelta, rampUp, returnDuration);

            float valueBeforeReEntry = ramp.Value;
            Assert.That(valueBeforeReEntry, Is.GreaterThan(0f).And.LessThan(1f));

            float valueAfterOneStep = ramp.Step(1f, FrameDelta, rampUp, returnDuration);

            Assert.That(valueAfterOneStep, Is.GreaterThan(valueBeforeReEntry),
                "Re-entering speech must resume the up-ramp, not stay frozen.");

            float maxPossibleStep = FrameDelta / rampUp + 1e-4f;
            Assert.That(valueAfterOneStep - valueBeforeReEntry, Is.LessThanOrEqualTo(maxPossibleStep),
                "Re-entering speech mid-return must continue smoothly from the current value, never snap.");
        }

        [Test]
        public void SpeechBlendRamps_StepLinear_ReachesTargetWithinRampDownDuration()
        {
            const float rampDown = 0.4f;
            float value = 1f;
            float elapsed = 0f;

            while (value > 0f && elapsed < rampDown * 3f)
            {
                value = SpeechBlendRamps.StepLinear(value, 0f, FrameDelta, rampDown);
                elapsed += FrameDelta;
            }

            Assert.That(value, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(elapsed, Is.LessThanOrEqualTo(rampDown + FrameDelta),
                "LipSync's own ramp-down speed must be unchanged: it still reaches 0 within SpeechRampDownDuration.");
        }
    }
}
