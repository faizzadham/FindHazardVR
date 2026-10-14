using System.Collections.Generic;
using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Taxonomy;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Emotion
{
    [TestFixture]
    public sealed class EmotionScoreAccumulatorTests
    {
        private EmotionTaxonomyAsset _taxonomy;

        [SetUp]
        public void SetUp() => _taxonomy = EmotionTaxonomyAsset.CreateDefault();

        [TearDown]
        public void TearDown()
        {
            if (_taxonomy != null) Object.DestroyImmediate(_taxonomy);
            LogAssert.NoUnexpectedReceived();
        }

        // ── existing cases (preserved) ───────────────────────────────────────────────

        [Test]
        public void SetImmediateEmotion_SnapsOutputScoresInstantly()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);

            // Act
            accumulator.SetImmediateEmotion("joy", 0.9f);

            // Assert
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0.9f).Within(1e-3f));
            accumulator.GetDominant(out string label, out float score);
            Assert.That(label, Is.EqualTo("joy"));
            Assert.That(score, Is.EqualTo(0.9f).Within(1e-3f));
        }

        [Test]
        public void Tick_SmoothsToTargetEmotionOverTime()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 10f, decaySpeed: 5f);
            accumulator.SetTargetEmotion("anger", 1f);

            // Act
            for (int i = 0; i < 30; i++)
                accumulator.Tick(0.05f);

            // Assert
            Assert.That(accumulator.OutputScores["anger"], Is.GreaterThan(0.85f),
                "Smoothing should approach the target after ~1.5s.");
        }

        [Test]
        public void Tick_DecaysWhenTargetClearedToNeutral()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 10f);
            accumulator.SetImmediateEmotion("sadness", 1f);

            // Act
            accumulator.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);
            for (int i = 0; i < 30; i++)
                accumulator.Tick(0.05f);

            // Assert
            Assert.That(accumulator.OutputScores["sadness"], Is.LessThan(0.05f),
                "Sadness should decay back toward zero.");
        }

        // ── new cases ────────────────────────────────────────────────────────────────

        [Test]
        public void Constructor_NullTaxonomy_Throws()
        {
            // Arrange / Act / Assert
            Assert.Throws<System.ArgumentNullException>(() =>
                _ = new EmotionScoreAccumulator(null));
        }

        [Test]
        public void GetDominant_WhenAllZero_ReturnsNeutralLabel()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);

            // Act
            accumulator.GetDominant(out string label, out float score);

            // Assert
            Assert.That(label, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(score, Is.EqualTo(0f));
        }

        [Test]
        public void SetImmediateEmotion_ClearsAllOtherEmotionsInstantly()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.SetImmediateEmotion("joy", 1f);

            // Act
            accumulator.SetImmediateEmotion("anger", 0.5f);

            // Assert — joy must be cleared to zero
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0f).Within(1e-5f),
                "SetImmediateEmotion must zero all other emotions.");
            Assert.That(accumulator.OutputScores["anger"], Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void Reset_ClearsAllScoresAndBurst()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 20f);
            accumulator.SetImmediateEmotion("joy", 1f);

            // Act
            accumulator.Reset();

            // Assert — every emotion in the taxonomy should be zero
            foreach (KeyValuePair<string, float> kvp in accumulator.OutputScores)
                Assert.That(kvp.Value, Is.EqualTo(0f).Within(1e-5f),
                    $"Score for '{kvp.Key}' must be zero after Reset.");
        }

        [Test]
        public void OutputScores_AreAlwaysInUnitRange()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.SetImmediateEmotion("joy", 1f);
            accumulator.ConfigureMicroBurst(true, 0.25f, 1.8f, 0.05f);

            // Act — tick 60 frames
            for (int i = 0; i < 60; i++)
                accumulator.Tick(1f / 60f);

            // Assert
            foreach (KeyValuePair<string, float> kvp in accumulator.OutputScores)
            {
                Assert.That(kvp.Value, Is.GreaterThanOrEqualTo(0f),
                    $"Score '{kvp.Key}' below 0.");
                Assert.That(kvp.Value, Is.LessThanOrEqualTo(1f + 1e-4f),
                    $"Score '{kvp.Key}' above 1.");
            }
        }

        [Test]
        public void SetTargetEmotions_MultipleScores_DominantIsHighest()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 50f, decaySpeed: 5f);
            var scores = new Dictionary<string, float>
            {
                { "joy",    0.3f },
                { "anger",  0.9f },
                { "sadness", 0.5f }
            };

            // Act
            accumulator.SetTargetEmotions(scores);
            for (int i = 0; i < 60; i++)
                accumulator.Tick(0.05f);

            accumulator.GetDominant(out string dominant, out _);

            // Assert
            Assert.That(dominant, Is.EqualTo("anger"),
                "Dominant emotion must be the one with the highest score.");
        }

        [Test]
        public void Tick_ZeroDelta_DoesNotChangeScores()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.SetImmediateEmotion("joy", 0.7f);
            float scoreBefore = accumulator.OutputScores["joy"];

            // Act
            accumulator.Tick(0f);

            // Assert
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(scoreBefore).Within(1e-5f),
                "Zero deltaTime tick must be a no-op.");
        }

        [Test]
        public void MicroBurst_Enabled_OvershootsOnSignificantChange()
        {
            // Arrange — use a high overshoot to make the burst clearly observable
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 30f, decaySpeed: 5f);
            accumulator.ConfigureMicroBurst(true, duration: 0.3f, overshoot: 2.0f, threshold: 0.0f);

            // Snap joy to 0 first, then trigger a large positive delta
            accumulator.SetImmediateEmotion("joy", 0f);

            // Act — set target 1.0 to trigger burst (delta 1.0 > threshold 0.0)
            accumulator.SetTargetEmotion("joy", 1f);
            accumulator.Tick(0.05f); // first tick into burst window

            // Assert — with overshoot=2, output must exceed the underlying current score
            float raw = accumulator.OutputScores["joy"];
            Assert.That(raw, Is.GreaterThan(0f),
                "Burst overshoot must produce a positive output on the first tick.");
        }

        // ── Persona baseline ────────────────────────────────────────────────

        [Test]
        public void GetMood_Default_ReturnsNeutralZero()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);

            // Act
            accumulator.GetMood(out string moodLabel, out float moodScore);

            // Assert
            Assert.That(moodLabel, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(moodScore, Is.EqualTo(0f));
        }

        [Test]
        public void Baseline_NoTransient_RendersInOutputScoresButNotDominantOrCurrent()
        {
            // Arrange — §8.0 test 2: no downstream fake emotion.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 10f, decaySpeed: 5f);
            accumulator.SetPersonaBaseline("joy", 0.3f);

            // Act
            for (int i = 0; i < 10; i++)
                accumulator.Tick(0.05f);

            // Assert
            accumulator.GetDominant(out string dominantLabel, out float dominantScore);
            Assert.That(dominantLabel, Is.EqualTo(_taxonomy.Neutral.Label),
                "Baseline must never surface as the dominant emotion.");
            Assert.That(dominantScore, Is.EqualTo(0f));

            accumulator.GetMood(out string moodLabel, out float moodScore);
            Assert.That(moodLabel, Is.EqualTo("joy"));
            Assert.That(moodScore, Is.GreaterThan(0f));

            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0f),
                "Baseline must render into OutputScores.");
        }

        [Test]
        public void Baseline_SameLabelAsTransient_SplitsFromDominantUntilTransientDecays()
        {
            // Arrange — §8.0 test 1: same-label split.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 20f);
            accumulator.SetPersonaBaseline("joy", 0.15f);
            accumulator.SetTargetEmotion("joy", 0.7f);

            // Act — settle the transient up.
            for (int i = 0; i < 30; i++)
                accumulator.Tick(0.05f);

            accumulator.GetDominant(out string dominantLabel, out float dominantScore);
            accumulator.GetMood(out string moodLabel, out float moodScore);

            // Assert — while active, dominant is joy (transient) and mood is joy (baseline) too.
            Assert.That(dominantLabel, Is.EqualTo("joy"));
            Assert.That(dominantScore, Is.GreaterThan(0f));
            Assert.That(moodLabel, Is.EqualTo("joy"));
            Assert.That(moodScore, Is.EqualTo(0.15f).Within(1e-4f));

            // Act — decay the transient back down.
            accumulator.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);
            for (int i = 0; i < 30; i++)
                accumulator.Tick(0.05f);

            accumulator.GetDominant(out dominantLabel, out dominantScore);
            accumulator.GetMood(out moodLabel, out moodScore);

            // Assert — dominant settles to neutral, mood is unaffected and still present.
            Assert.That(dominantLabel, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(dominantScore, Is.LessThan(0.05f));
            Assert.That(moodLabel, Is.EqualTo("joy"));
            Assert.That(moodScore, Is.EqualTo(0.15f).Within(1e-4f));
        }

        [Test]
        public void Baseline_RecedesUnderActiveTransient()
        {
            // Arrange — higher transient => lower baseline contribution to the same/other label.
            var lowTransient = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 20f);
            lowTransient.SetPersonaBaseline("joy", 0.5f);
            lowTransient.SetTargetEmotion("anger", 0.1f);
            for (int i = 0; i < 30; i++) lowTransient.Tick(0.05f);

            var highTransient = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 20f);
            highTransient.SetPersonaBaseline("joy", 0.5f);
            highTransient.SetTargetEmotion("anger", 0.9f);
            for (int i = 0; i < 30; i++) highTransient.Tick(0.05f);

            // Assert
            Assert.That(highTransient.OutputScores["joy"], Is.LessThan(lowTransient.OutputScores["joy"]),
                "A stronger active transient must suppress the baseline contribution more.");
        }

        [Test]
        public void Baseline_SurvivesReset()
        {
            // Arrange — Reset() must not clear the persona baseline.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 10f, decaySpeed: 5f);
            accumulator.SetPersonaBaseline("joy", 0.4f);

            // Act
            accumulator.Reset();
            accumulator.GetMood(out string moodLabel, out float moodScore);

            // Assert
            Assert.That(moodLabel, Is.EqualTo("joy"));
            Assert.That(moodScore, Is.EqualTo(0.4f).Within(1e-4f));
        }

        [Test]
        public void SetPersonaBaseline_NeutralLabel_IsTreatedAsNoBaseline()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 10f, decaySpeed: 5f);

            // Act
            accumulator.SetPersonaBaseline(_taxonomy.Neutral.Label, 0.8f);
            accumulator.GetMood(out string moodLabel, out float moodScore);

            // Assert
            Assert.That(moodLabel, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(moodScore, Is.EqualTo(0f));
        }

        [Test]
        public void SetPersonaBaseline_UnknownLabel_IsTreatedAsNoBaseline_DoesNotThrow()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 10f, decaySpeed: 5f);

            // Act / Assert — an unknown (non-taxonomy) label must never reach Tick's
            // _outputScores[_baselineLabel] indexer and must not throw KeyNotFoundException.
            Assert.DoesNotThrow(() =>
            {
                accumulator.SetPersonaBaseline("not_a_real_label", 0.5f);
                for (int i = 0; i < 10; i++)
                    accumulator.Tick(0.05f);
            });

            accumulator.GetMood(out string moodLabel, out float moodScore);
            Assert.That(moodLabel, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(moodScore, Is.EqualTo(0f));
        }

        [Test]
        public void LegacyEquivalence_ZeroBaselineIntensity_OutputAndDominantUnaffected()
        {
            // Arrange — §8.0 test 3: legacy equivalence. Baseline configured but intensity 0
            // (the default) must not alter OutputScores or GetDominant at all versus a twin
            // accumulator with no baseline call whatsoever.
            var withBaselineCallAtZero = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 8f, decaySpeed: 3f);
            withBaselineCallAtZero.SetPersonaBaseline("joy", 0f);
            withBaselineCallAtZero.SetTargetEmotions(new Dictionary<string, float>
            {
                { "joy", 0.4f },
                { "sadness", 0.2f }
            });

            var withoutBaselineCall = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 8f, decaySpeed: 3f);
            withoutBaselineCall.SetTargetEmotions(new Dictionary<string, float>
            {
                { "joy", 0.4f },
                { "sadness", 0.2f }
            });

            // Act — run an identical scripted tick sequence on both.
            for (int i = 0; i < 45; i++)
            {
                withBaselineCallAtZero.Tick(0.05f);
                withoutBaselineCall.Tick(0.05f);
            }

            // Assert — numerically identical output for every taxonomy label.
            foreach (KeyValuePair<string, float> kvp in withoutBaselineCall.OutputScores)
            {
                Assert.That(withBaselineCallAtZero.OutputScores[kvp.Key], Is.EqualTo(kvp.Value).Within(1e-6f),
                    $"Output for '{kvp.Key}' must be bit-identical when baseline intensity is 0.");
            }

            withBaselineCallAtZero.GetDominant(out string labelA, out float scoreA);
            withoutBaselineCall.GetDominant(out string labelB, out float scoreB);
            Assert.That(labelA, Is.EqualTo(labelB));
            Assert.That(scoreA, Is.EqualTo(scoreB).Within(1e-6f));
        }

        // ── Zero-alloc array overload ──────────────────────────────────────

        [Test]
        public void SetTargetEmotions_ArrayOverload_MatchesDictOverload()
        {
            // Arrange
            var dictAccumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 5f);
            var arrayAccumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 5f);

            var scores = new Dictionary<string, float> { { "joy", 0.6f }, { "anger", 0.2f } };
            var labels = new[] { "joy", "anger", string.Empty };
            var values = new[] { 0.6f, 0.2f, 0f };

            // Act
            dictAccumulator.SetTargetEmotions(scores);
            arrayAccumulator.SetTargetEmotions(labels, values, 2);
            for (int i = 0; i < 40; i++)
            {
                dictAccumulator.Tick(0.05f);
                arrayAccumulator.Tick(0.05f);
            }

            // Assert — numerically identical for every taxonomy label.
            foreach (KeyValuePair<string, float> kvp in dictAccumulator.OutputScores)
            {
                Assert.That(arrayAccumulator.OutputScores[kvp.Key], Is.EqualTo(kvp.Value).Within(1e-6f),
                    $"Array overload output for '{kvp.Key}' must match the dict overload.");
            }
        }

        [Test]
        public void SetTargetEmotions_ArrayOverload_ZeroesLabelsAbsentFromCount()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 30f, decaySpeed: 30f);
            accumulator.SetImmediateEmotion("sadness", 1f);

            // Act — only "joy" is present within count=1; everything else (including sadness) zeroes.
            var labels = new[] { "joy" };
            var values = new[] { 0.5f };
            accumulator.SetTargetEmotions(labels, values, 1);
            for (int i = 0; i < 30; i++) accumulator.Tick(0.05f);

            // Assert
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0.3f));
            Assert.That(accumulator.OutputScores["sadness"], Is.LessThan(0.05f),
                "Labels absent from the array overload's active count must decay to zero.");
        }

        [Test]
        public void SetTargetEmotions_ArrayOverload_ZeroAlloc()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 10f, decaySpeed: 10f);
            var labels = new[] { "joy", "trust" };
            var values = new[] { 0.5f, 0.2f };

            // Warm up.
            for (int i = 0; i < 50; i++)
            {
                accumulator.SetTargetEmotions(labels, values, 2);
                accumulator.Tick(0.016f);
            }

            // Act
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    accumulator.SetTargetEmotions(labels, values, 2);
                    accumulator.Tick(0.016f);
                }
            });

            // Assert
            Assert.That(allocationCount, Is.EqualTo(0L),
                "The array-overload SetTargetEmotions + Tick must produce zero managed allocation events in steady state.");
        }

        [Test]
        public void SetLerpSpeed_UpdatesSmoothing_AtRuntime()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 1f, decaySpeed: 1f);
            accumulator.SetTargetEmotion("joy", 1f);

            // Act — one tick at very slow speed
            accumulator.Tick(0.016f);
            float slowValue = accumulator.OutputScores["joy"];

            // Now switch to fast speed and tick again from same state
            accumulator.Reset();
            accumulator.SetLerpSpeed(100f);
            accumulator.SetTargetEmotion("joy", 1f);
            accumulator.Tick(0.016f);
            float fastValue = accumulator.OutputScores["joy"];

            // Assert
            Assert.That(fastValue, Is.GreaterThan(slowValue),
                "Higher lerpSpeed must produce a larger per-tick increment.");
        }
    }
}
