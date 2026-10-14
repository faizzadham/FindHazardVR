using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Mood-pickup timeline tests for <see cref="EmotionScoreAccumulator" />'s
    ///     echo channel: default-off bit-identical output, easing toward a target at the locked
    ///     attack/release rates, label-switch drain-then-reseed, fold suppression by a strong own
    ///     transient, the mood invariants (never <see cref="EmotionScoreAccumulator.GetDominant" />,
    ///     never <see cref="EmotionScoreAccumulator.GetMood" />), and <see cref="EmotionScoreAccumulator.Reset" />
    ///     clearing the echo while preserving the anchor mood slot.
    /// </summary>
    [TestFixture]
    public sealed class EmotionScoreAccumulatorContagionTests
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
            // Arrange — one accumulator explicitly configured with contagion disabled, one never
            // configured at all. Both must produce bit-identical output (default-off is
            // bit-identical) even when SetContagionTarget is still called on the disabled one.
            var configuredDisabled = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            configuredDisabled.ConfigureContagion(false);

            var neverConfigured = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);

            for (int i = 0; i < 200; i++)
            {
                if (i == 0)
                {
                    configuredDisabled.SetTargetEmotion("sadness", 0.4f);
                    neverConfigured.SetTargetEmotion("sadness", 0.4f);
                }

                configuredDisabled.SetContagionTarget("joy", 0.2f);
                configuredDisabled.Tick(0.1f);
                neverConfigured.Tick(0.1f);
            }

            foreach (var kvp in neverConfigured.OutputScores)
            {
                Assert.That(configuredDisabled.OutputScores[kvp.Key], Is.EqualTo(kvp.Value).Within(1e-6f),
                    $"Output for '{kvp.Key}' must be bit-identical when contagion is disabled.");
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
        public void EnabledTarget_EasesToward_TargetIntensity()
        {
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureContagion(true);
            accumulator.SetContagionTarget("joy", 0.18f);

            for (int i = 0; i < 200; i++)
            {
                accumulator.SetContagionTarget("joy", 0.18f);
                accumulator.Tick(0.05f);
            }

            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0.18f).Within(0.01f));
        }

        [Test]
        public void NoTarget_EchoDecaysToZero_AtReleaseRate()
        {
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureContagion(true);
            accumulator.SetContagionTarget("joy", 0.2f);
            for (int i = 0; i < 200; i++)
            {
                accumulator.SetContagionTarget("joy", 0.2f);
                accumulator.Tick(0.05f);
            }
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0.1f), "Sanity: echo must have risen.");

            // Act — clear the target (no candidate this scan) and let the echo decay.
            accumulator.SetContagionTarget(null, 0f);
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.05f);

            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void LabelSwitch_OldEchoDrainsFirst_ThenReseedsToNewLabel()
        {
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureContagion(true);
            accumulator.SetContagionTarget("joy", 0.2f);
            for (int i = 0; i < 200; i++)
            {
                accumulator.SetContagionTarget("joy", 0.2f);
                accumulator.Tick(0.05f);
            }
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0.1f));

            // Act — switch the candidate to a different label.
            accumulator.SetContagionTarget("anger", 0.2f);
            accumulator.Tick(0.02f);

            // Assert — joy's echo must not have vanished instantly; anger must not yet have risen
            // (the channel is still draining the old label before reseeding).
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0f),
                "The old label's echo must decay before the channel reseeds to the new label.");
            Assert.That(accumulator.OutputScores["anger"], Is.EqualTo(0f).Within(1e-4f));

            // Continue ticking with the new target sustained until it settles.
            for (int i = 0; i < 400; i++)
            {
                accumulator.SetContagionTarget("anger", 0.2f);
                accumulator.Tick(0.05f);
            }

            Assert.That(accumulator.OutputScores["anger"], Is.EqualTo(0.2f).Within(0.01f));
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void Fold_SuppressedByStrongOwnTransient()
        {
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureContagion(true);
            accumulator.SetContagionTarget("joy", 0.2f);
            for (int i = 0; i < 200; i++)
            {
                accumulator.SetContagionTarget("joy", 0.2f);
                accumulator.Tick(0.05f);
            }
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0.1f), "Sanity: echo must have risen.");

            // Act — a maximal OWN transient on a different label must fully suppress joy's echo
            // fold (joy's own transient target is 0, so its output is purely the echo fold).
            accumulator.SetImmediateEmotion("anger", 1f);
            accumulator.SetContagionTarget("joy", 0.2f);
            accumulator.Tick(0.02f);

            Assert.That(accumulator.OutputScores["joy"], Is.LessThan(0.01f),
                "A maximal own transient must suppress the echo fold, just like drift/baseline.");
        }

        [Test]
        public void GetDominant_NeverReportsEchoLabel()
        {
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureContagion(true);
            accumulator.SetContagionTarget("joy", 0.9f); // even an implausibly large target
            for (int i = 0; i < 200; i++)
            {
                accumulator.SetContagionTarget("joy", 0.9f);
                accumulator.Tick(0.05f);
            }

            accumulator.GetDominant(out string dominantLabel, out float dominantScore);
            Assert.That(dominantLabel, Is.EqualTo(_taxonomy.Neutral.Label),
                "The echo channel must never surface via GetDominant, which is transient-only.");
            Assert.That(dominantScore, Is.EqualTo(0f));
        }

        [Test]
        public void GetMood_NeverReportsEcho()
        {
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureContagion(true);
            accumulator.SetContagionTarget("joy", 0.9f);
            for (int i = 0; i < 200; i++)
            {
                accumulator.SetContagionTarget("joy", 0.9f);
                accumulator.Tick(0.05f);
            }

            accumulator.GetMood(out string moodLabel, out float moodScore);
            Assert.That(moodLabel, Is.EqualTo(_taxonomy.Neutral.Label),
                "The echo channel is a render fold, never a mood channel — GetMood must not report it.");
            Assert.That(moodScore, Is.EqualTo(0f));
        }

        [Test]
        public void Reset_ClearsEcho_ButPreservesAnchorMoodSlot()
        {
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.ConfigureContagion(true);
            accumulator.SetPersonaBaseline("trust", 0.3f);
            accumulator.SetContagionTarget("joy", 0.2f);
            for (int i = 0; i < 200; i++)
            {
                accumulator.SetContagionTarget("joy", 0.2f);
                accumulator.Tick(0.05f);
            }
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0.1f), "Sanity: echo must have risen.");

            // Act
            accumulator.Reset();

            // Assert — the echo is gone from output, anchor mood survives.
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0f));
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("trust"));
            Assert.That(score, Is.EqualTo(0.3f).Within(1e-4f));

            // A stale target from before Reset must not silently reseed the echo afterward.
            accumulator.Tick(0.05f);
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0f));
        }
    }
}
