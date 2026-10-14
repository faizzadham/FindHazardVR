using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Emotion
{
    [TestFixture]
    public sealed class EmotionReadingTests
    {
        [Test]
        public void Constructor_DefensivelyCopiesScoreTable()
        {
            var scores = new Dictionary<string, float>
            {
                ["joy"] = 0.75f
            };

            var reading = new EmotionReading("joy", 0.75f, scores, 0.4f, 1.2f);
            scores["joy"] = 0.1f;
            scores["anger"] = 1f;

            Assert.That(reading.GetScore("joy"), Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(reading.GetScore("anger"), Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void CopyScoresTo_UsesCallerOwnedDictionary()
        {
            var reading = new EmotionReading(
                "joy",
                0.75f,
                new Dictionary<string, float> { ["joy"] = 0.75f },
                0.4f,
                1.2f);
            var destination = new Dictionary<string, float> { ["stale"] = 1f };

            reading.CopyScoresTo(destination);

            Assert.That(destination.ContainsKey("stale"), Is.False);
            Assert.That(destination["joy"], Is.EqualTo(0.75f).Within(1e-6f));
        }

        // ── Additive MoodLabel/MoodScore ────────────────────────────────

        [Test]
        public void FiveArgConstructor_BackCompat_YieldsNeutralMoodDefaults()
        {
            // Arrange / Act — §5/B4: the 5-arg path must explicitly init mood to documented defaults.
            var reading = new EmotionReading(
                "anger",
                0.6f,
                new Dictionary<string, float> { ["anger"] = 0.6f },
                0.2f,
                3f);

            // Assert
            Assert.That(reading.MoodLabel, Is.EqualTo(EmotionReading.NeutralLabel));
            Assert.That(reading.MoodScore, Is.EqualTo(0f));
        }

        [Test]
        public void SevenArgConstructor_CarriesMood()
        {
            // Arrange / Act
            var reading = new EmotionReading(
                "anger",
                0.6f,
                new Dictionary<string, float> { ["anger"] = 0.6f },
                0.2f,
                3f,
                "joy",
                0.15f);

            // Assert
            Assert.That(reading.DominantLabel, Is.EqualTo("anger"));
            Assert.That(reading.MoodLabel, Is.EqualTo("joy"));
            Assert.That(reading.MoodScore, Is.EqualTo(0.15f).Within(1e-6f));
        }

        [Test]
        public void SevenArgConstructor_ClampsMoodScoreAndDefaultsEmptyLabel()
        {
            // Arrange / Act
            var reading = new EmotionReading(
                "neutral",
                0f,
                EmotionReading.EmptyScores,
                0f,
                0f,
                string.Empty,
                1.5f);

            // Assert
            Assert.That(reading.MoodLabel, Is.EqualTo(EmotionReading.NeutralLabel));
            Assert.That(reading.MoodScore, Is.EqualTo(1f));
        }

        [Test]
        public void Neutral_StaticReading_HasNeutralMood()
        {
            // Assert — Neutral factory chains through the 5-arg ctor.
            Assert.That(EmotionReading.Neutral.MoodLabel, Is.EqualTo(EmotionReading.NeutralLabel));
            Assert.That(EmotionReading.Neutral.MoodScore, Is.EqualTo(0f));
        }
    }
}
