using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Mood-drift timeline tests for <see cref="EmotionScoreAccumulator" />'s
    ///     drift channel: default-off bit-identical output, sustained-transient accrual, decay,
    ///     label-switch reseeding, cap enforcement, <see cref="EmotionScoreAccumulator.Reset" />
    ///     semantics, <see cref="EmotionScoreAccumulator.GetMood" /> channel selection, fold
    ///     suppression, and the invariant that <see cref="EmotionScoreAccumulator.GetDominant" />
    ///     never surfaces the drift label).
    /// </summary>
    [TestFixture]
    public sealed class EmotionScoreAccumulatorMoodDriftTests
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

        [Test]
        public void Disabled_OutputScoresIdentical_ToTwinAccumulator_NeverConfigured()
        {
            // Arrange — one accumulator explicitly configured with drift disabled, one never
            // configured at all. Both must produce bit-identical output over an identical
            // scripted timeline (default-off is bit-identical).
            var configuredDisabled = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            configuredDisabled.ConfigureMoodDrift(false, 0.02f, 0.05f, 0.25f);

            var neverConfigured = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);

            // Act — a scripted timeline: sustain joy, then switch to anger, then clear.
            for (int i = 0; i < 300; i++)
            {
                if (i == 0)
                {
                    configuredDisabled.SetTargetEmotion("joy", 0.8f);
                    neverConfigured.SetTargetEmotion("joy", 0.8f);
                }
                else if (i == 150)
                {
                    configuredDisabled.SetTargetEmotion("anger", 0.6f);
                    neverConfigured.SetTargetEmotion("anger", 0.6f);
                }
                else if (i == 250)
                {
                    configuredDisabled.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);
                    neverConfigured.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);
                }

                configuredDisabled.Tick(0.1f);
                neverConfigured.Tick(0.1f);
            }

            // Assert
            foreach (var kvp in neverConfigured.OutputScores)
            {
                Assert.That(configuredDisabled.OutputScores[kvp.Key], Is.EqualTo(kvp.Value).Within(1e-6f),
                    $"Output for '{kvp.Key}' must be bit-identical when drift is disabled.");
            }

            configuredDisabled.GetDominant(out string labelA, out float scoreA);
            neverConfigured.GetDominant(out string labelB, out float scoreB);
            Assert.That(labelA, Is.EqualTo(labelB));
            Assert.That(scoreA, Is.EqualTo(scoreB).Within(1e-6f));

            configuredDisabled.GetMood(out string moodLabelA, out float moodScoreA);
            neverConfigured.GetMood(out string moodLabelB, out float moodScoreB);
            Assert.That(moodLabelA, Is.EqualTo(moodLabelB));
            Assert.That(moodScoreA, Is.EqualTo(moodScoreB).Within(1e-6f));
        }

        [Test]
        public void SustainedTransient_AccruesDrift_AboveThreshold_BelowCap()
        {
            // Arrange — defaults: driftRate 0.02, recoveryRate 0.05, maxIntensity 0.25.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.02f, 0.05f, 0.25f);
            accumulator.SetTargetEmotion("joy", 0.8f);

            // Act — 120s @ 0.5s steps.
            for (int i = 0; i < 240; i++)
                accumulator.Tick(0.5f);

            // Assert
            accumulator.GetMood(out string moodLabel, out float moodScore);
            Assert.That(moodLabel, Is.EqualTo("joy"));
            Assert.That(moodScore, Is.GreaterThanOrEqualTo(0.15f));
            Assert.That(moodScore, Is.LessThanOrEqualTo(0.25f + 1e-3f));
        }

        [Test]
        public void TransientGone_DriftDecaysTowardZero_AtRecoveryRate()
        {
            // Arrange — build up drift, then clear the sustaining transient.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.02f, 0.05f, 0.25f);
            accumulator.SetTargetEmotion("joy", 0.8f);
            for (int i = 0; i < 240; i++)
                accumulator.Tick(0.5f);

            accumulator.GetMood(out _, out float peakScore);
            Assert.That(peakScore, Is.GreaterThan(0f));

            // Act — instantly zero the sustaining transient (SetImmediateEmotion snaps every
            // label's current score, not just targets, so decay starts on the very next tick
            // instead of racing the transient's own exponential falloff) and confirm drift
            // monotonically decays.
            accumulator.SetImmediateEmotion(_taxonomy.Neutral.Label, 0f);
            float previous = peakScore;
            for (int i = 0; i < 20; i++)
            {
                accumulator.Tick(0.5f);
                accumulator.GetMood(out _, out float current);
                Assert.That(current, Is.LessThanOrEqualTo(previous + 1e-5f), "Drift must decay monotonically once the sustaining transient is gone.");
                previous = current;
            }

            Assert.That(previous, Is.LessThan(peakScore));
        }

        [Test]
        public void DominantLabelSwitch_OldDriftDecaysFirst_NewLabelOnlySeedsBelowThreshold()
        {
            // Arrange — build up drift for joy.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.02f, 0.5f, 0.25f); // fast recovery for a tight test
            accumulator.SetTargetEmotion("joy", 0.8f);
            for (int i = 0; i < 240; i++)
                accumulator.Tick(0.5f);

            accumulator.GetMood(out string beforeLabel, out float beforeScore);
            Assert.That(beforeLabel, Is.EqualTo("joy"));
            Assert.That(beforeScore, Is.GreaterThan(0.01f));

            // Act — instantly switch the dominant transient to anger (SetImmediateEmotion snaps
            // current scores, so anger is unambiguously dominant on the very next tick).
            // Immediately after, drift must still report the OLD label (decaying), not the new one.
            accumulator.SetImmediateEmotion("anger", 0.8f);
            accumulator.Tick(0.1f);

            accumulator.GetMood(out string justAfterLabel, out float justAfterScore);
            Assert.That(justAfterLabel, Is.EqualTo("joy"), "Drift must decay under its old label before reseeding.");
            Assert.That(justAfterScore, Is.GreaterThan(0f));

            // Continue ticking until the old drift has decayed below the reseed threshold and the
            // new label has had time to rise.
            for (int i = 0; i < 400; i++)
                accumulator.Tick(0.5f);

            accumulator.GetMood(out string finalLabel, out float finalScore);
            Assert.That(finalLabel, Is.EqualTo("anger"), "Once decayed below the reseed threshold, drift must reseed to the new dominant label.");
            Assert.That(finalScore, Is.GreaterThan(0f));
        }

        [Test]
        public void Cap_Honored_WhenTransientScoreExceedsCap()
        {
            // Arrange — a maximal transient (score 1.0) with a small cap.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.05f, 0.05f, 0.2f);
            accumulator.SetTargetEmotion("joy", 1f);

            // Act — long enough to fully converge.
            for (int i = 0; i < 600; i++)
                accumulator.Tick(0.5f);

            // Assert
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("joy"));
            Assert.That(score, Is.LessThanOrEqualTo(0.2f + 1e-3f));
        }

        [Test]
        public void Reset_ClearsDrift_ButPreservesAnchorMoodSlot()
        {
            // Arrange — build drift AND set a persona baseline anchor.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.05f, 0.05f, 0.25f);
            accumulator.SetPersonaBaseline("trust", 0.3f);
            accumulator.SetTargetEmotion("joy", 0.9f);
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.5f);

            // Act
            accumulator.Reset();

            // Assert — the anchor slot survives Reset, drift is cleared.
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("trust"));
            Assert.That(score, Is.EqualTo(0.3f).Within(1e-4f));
        }

        [Test]
        public void GetMood_ReportsDrift_WhenAnchorIsNone()
        {
            // Arrange — no persona baseline/runtime mood configured at all.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.05f, 0.05f, 0.25f);
            accumulator.SetTargetEmotion("joy", 0.8f);

            // Act
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.5f);

            // Assert
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("joy"));
            Assert.That(score, Is.GreaterThan(0f));
        }

        [Test]
        public void GetMood_ReportsStrongerChannel_WhenBothAnchorAndDriftAreActive()
        {
            // Arrange — a strong anchor mood plus a weaker drift channel (small cap).
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.05f, 0.05f, 0.1f);
            accumulator.SetPersonaBaseline("trust", 0.5f);
            accumulator.SetTargetEmotion("joy", 0.8f);
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.5f);

            // Assert — anchor (0.5) beats drift (capped at 0.1).
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("trust"));
            Assert.That(score, Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void Fold_SuppressedByStrongTransient_WhileOldDriftDecays()
        {
            // Arrange — build drift for joy, then introduce a fully-suppressing new transient.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.05f, 0.05f, 0.25f);
            accumulator.SetTargetEmotion("joy", 0.8f);
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.5f);

            // Act — instantly snap the dominant transient to anger @ 1 (SetImmediateEmotion zeros
            // joy's own transient contribution immediately and makes anger unambiguously
            // dominant/fully-suppressing on the very next tick).
            accumulator.SetImmediateEmotion("anger", 1f);
            accumulator.Tick(0.05f);

            // Assert — joy's output must be 0: its transient is 0, and the still-decaying "joy"
            // drift fold is fully suppressed by the maximal (1.0) anger transient.
            Assert.That(accumulator.OutputScores["joy"], Is.LessThan(0.01f));
        }

        [Test]
        public void GetDominant_NeverReportsDriftLabel_WithoutAnActiveTransient()
        {
            // Arrange — build drift, then let the transient fully decay to 0.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureMoodDrift(true, 0.05f, 0.02f, 0.25f);
            accumulator.SetTargetEmotion("joy", 0.8f);
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.5f);

            accumulator.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);
            for (int i = 0; i < 60; i++)
                accumulator.Tick(0.5f);

            // Assert — GetDominant is transient-only, never surfaces the drift label even
            // while a non-zero drift value is still fading out.
            accumulator.GetDominant(out string dominantLabel, out float dominantScore);
            Assert.That(dominantLabel, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(dominantScore, Is.EqualTo(0f));
        }
    }
}
