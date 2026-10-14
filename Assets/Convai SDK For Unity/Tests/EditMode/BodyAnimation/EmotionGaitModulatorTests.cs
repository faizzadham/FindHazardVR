using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="EmotionGaitModulator" /> and <see cref="GaitEmotionArousalTable" />
    ///: neutral/no-source resolves to exactly 1, arousal-derived extremes stay
    ///     clamped to the configured range, an unrecognized label is a no-op, smoothing is
    ///     monotonic and deterministic.
    /// </summary>
    public sealed class EmotionGaitModulatorTests
    {
        private const float Dt = 1f / 60f;
        private const float DefaultRange = 0.15f;

        private static EmotionReading Reading(string label, float score) =>
            new(label, score, EmotionReading.EmptyScores, 0f, 0f);

        [Test]
        public void Neutral_ResolvesToExactlyOne()
        {
            float target = EmotionGaitModulator.ResolveTargetMultiplier(EmotionReading.Neutral, DefaultRange);
            Assert.That(target, Is.EqualTo(1f));
        }

        [Test]
        public void NeutralLabelExplicit_ResolvesToExactlyOne()
        {
            EmotionReading reading = Reading("neutral", 1f);
            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, DefaultRange);
            Assert.That(target, Is.EqualTo(1f));
        }

        [Test]
        public void ZeroScore_ResolvesToExactlyOne()
        {
            EmotionReading reading = Reading("joy", 0f);
            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, DefaultRange);
            Assert.That(target, Is.EqualTo(1f));
        }

        [Test]
        public void UnrecognizedLabel_ResolvesToExactlyOne()
        {
            EmotionReading reading = Reading("some-unmapped-label", 1f);
            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, DefaultRange);
            Assert.That(target, Is.EqualTo(1f));
        }

        [Test]
        public void HighArousalEmotion_IncreasesMultiplierAboveOne()
        {
            EmotionReading reading = Reading("surprise", 1f); // highest-arousal taxonomy entry
            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, DefaultRange);
            Assert.That(target, Is.GreaterThan(1f));
            Assert.That(target, Is.LessThanOrEqualTo(1f + DefaultRange));
        }

        [Test]
        public void LowArousalEmotion_DecreasesMultiplierBelowOne()
        {
            EmotionReading reading = Reading("sadness", 1f); // negative-arousal taxonomy entry
            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, DefaultRange);
            Assert.That(target, Is.LessThan(1f));
            Assert.That(target, Is.GreaterThanOrEqualTo(1f - DefaultRange));
        }

        [Test]
        public void ZeroRange_AlwaysResolvesToExactlyOne()
        {
            EmotionReading reading = Reading("surprise", 1f);
            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, 0f);
            Assert.That(target, Is.EqualTo(1f));
        }

        [Test]
        public void RangeAboveConfiguredMax_IsClampedToAbsoluteBound()
        {
            const float absoluteMaxRange = 0.3f;
            EmotionReading reading = Reading("surprise", 1f); // maximal-magnitude arousal (0.85)

            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, 999f);

            Assert.That(target, Is.LessThanOrEqualTo(1f + absoluteMaxRange),
                "an out-of-range Range input must never push the multiplier past the absolute clamp");
            Assert.That(target, Is.GreaterThanOrEqualTo(1f - absoluteMaxRange));
        }

        [Test]
        public void NegativeRange_ClampsToZero_ResolvesToOne()
        {
            EmotionReading reading = Reading("anger", 1f);
            float target = EmotionGaitModulator.ResolveTargetMultiplier(reading, -1f);
            Assert.That(target, Is.EqualTo(1f));
        }

        [Test]
        public void Tick_SmoothsMonotonicallyTowardTarget()
        {
            var modulator = new EmotionGaitModulator();
            EmotionReading excited = Reading("surprise", 1f);
            float expectedTarget = EmotionGaitModulator.ResolveTargetMultiplier(excited, DefaultRange);

            float previous = modulator.Current; // starts at 1
            bool everIncreased = false;
            for (int i = 0; i < 180; i++) // 3s at 60Hz — well past the ~1s smoothing time
            {
                float current = modulator.Tick(in excited, DefaultRange, Dt);
                Assert.That(current, Is.GreaterThanOrEqualTo(previous - 0.0001f),
                    "the smoothed multiplier must never move backward while easing toward a higher target");
                if (current > previous) everIncreased = true;
                previous = current;
            }

            Assert.IsTrue(everIncreased);
            Assert.That(modulator.Current, Is.EqualTo(expectedTarget).Within(0.01f),
                "after several smoothing windows the multiplier must have settled near the target");
        }

        [Test]
        public void Reset_ReturnsToNeutral()
        {
            var modulator = new EmotionGaitModulator();
            EmotionReading excited = Reading("surprise", 1f);
            for (int i = 0; i < 60; i++)
                modulator.Tick(in excited, DefaultRange, Dt);
            Assert.That(modulator.Current, Is.Not.EqualTo(1f));

            modulator.Reset();

            Assert.That(modulator.Current, Is.EqualTo(1f));
        }

        [Test]
        public void SameInputSequence_ProducesIdenticalResults_Deterministic()
        {
            var a = new EmotionGaitModulator();
            var b = new EmotionGaitModulator();
            EmotionReading joy = Reading("joy", 0.8f);
            EmotionReading sadness = Reading("sadness", 0.5f);

            for (int i = 0; i < 240; i++)
            {
                EmotionReading reading = (i % 2 == 0) ? joy : sadness;
                float resultA = a.Tick(in reading, DefaultRange, Dt);
                float resultB = b.Tick(in reading, DefaultRange, Dt);
                Assert.That(resultA, Is.EqualTo(resultB));
            }
        }
    }
}
