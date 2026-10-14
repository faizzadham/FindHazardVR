using Convai.Modules.Emotion.Components;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Tests for <see cref="ConvaiEmotionController.SelectMicroEmotionBias" />: the
    ///     pure selection of which source (dominant transient vs. mood) feeds the
    ///     micro-expression director's idle bias.
    /// </summary>
    [TestFixture]
    public sealed class EmotionMicroBiasSelectionTests
    {
        [Test]
        public void MoodInactive_SelectsDominantTransient()
        {
            ConvaiEmotionController.SelectMicroEmotionBias(
                "anger", 0.6f, "neutral", 0f, out string label, out float score);

            Assert.That(label, Is.EqualTo("anger"));
            Assert.That(score, Is.EqualTo(0.6f));
        }

        [Test]
        public void MoodStrongerThanDominant_SelectsMood()
        {
            ConvaiEmotionController.SelectMicroEmotionBias(
                "anger", 0.2f, "joy", 0.5f, out string label, out float score);

            Assert.That(label, Is.EqualTo("joy"));
            Assert.That(score, Is.EqualTo(0.5f));
        }

        [Test]
        public void Tie_SelectsDominantTransient()
        {
            ConvaiEmotionController.SelectMicroEmotionBias(
                "anger", 0.4f, "joy", 0.4f, out string label, out float score);

            Assert.That(label, Is.EqualTo("anger"));
            Assert.That(score, Is.EqualTo(0.4f));
        }

        [Test]
        public void BothZero_PassesThroughDominant()
        {
            ConvaiEmotionController.SelectMicroEmotionBias(
                "neutral", 0f, "neutral", 0f, out string label, out float score);

            Assert.That(label, Is.EqualTo("neutral"));
            Assert.That(score, Is.EqualTo(0f));
        }

        [Test]
        public void DominantStrongerThanMood_SelectsDominant()
        {
            ConvaiEmotionController.SelectMicroEmotionBias(
                "surprise", 0.7f, "sadness", 0.1f, out string label, out float score);

            Assert.That(label, Is.EqualTo("surprise"));
            Assert.That(score, Is.EqualTo(0.7f));
        }
    }
}
