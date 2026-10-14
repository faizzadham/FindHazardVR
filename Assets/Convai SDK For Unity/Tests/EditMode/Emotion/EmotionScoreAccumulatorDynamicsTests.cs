using System.Collections.Generic;
using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Profiles;
using Convai.Modules.Emotion.Taxonomy;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Per-emotion temporal-dynamics tests for
    ///     <see cref="EmotionScoreAccumulator.SetPerEmotionDynamics" />: legacy bit-identical
    ///     default, per-label attack/decay overrides, fallback to global speed, runtime global
    ///     changes, and zero-alloc Tick.
    /// </summary>
    [TestFixture]
    public sealed class EmotionScoreAccumulatorDynamicsTests
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
        public void EmptyOverrides_IsBitIdenticalToGlobalSpeedAccumulator()
        {
            // Arrange — twin accumulators: one gets an explicit empty-list SetPerEmotionDynamics
            // call, the other never calls it at all. Both must be numerically identical for every
            // taxonomy label across an identical scripted target+tick sequence.
            var withEmptyOverrides = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 8f, decaySpeed: 3f);
            withEmptyOverrides.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>());

            var withoutOverridesCall = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 8f, decaySpeed: 3f);

            var scores = new Dictionary<string, float>
            {
                { "joy", 0.8f },
                { "anger", 0.6f },
                { "sadness", 0.3f }
            };

            withEmptyOverrides.SetTargetEmotions(scores);
            withoutOverridesCall.SetTargetEmotions(scores);

            // Act — run an identical scripted tick sequence including a decay phase.
            for (int i = 0; i < 40; i++)
            {
                withEmptyOverrides.Tick(0.05f);
                withoutOverridesCall.Tick(0.05f);
            }

            withEmptyOverrides.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);
            withoutOverridesCall.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);

            for (int i = 0; i < 40; i++)
            {
                withEmptyOverrides.Tick(0.05f);
                withoutOverridesCall.Tick(0.05f);
            }

            // Assert — numerically identical output for every taxonomy label.
            foreach (KeyValuePair<string, float> kvp in withoutOverridesCall.OutputScores)
            {
                Assert.That(withEmptyOverrides.OutputScores[kvp.Key], Is.EqualTo(kvp.Value).Within(1e-6f),
                    $"Output for '{kvp.Key}' must be bit-identical with empty per-emotion dynamics.");
            }

            withEmptyOverrides.GetDominant(out string labelA, out float scoreA);
            withoutOverridesCall.GetDominant(out string labelB, out float scoreB);
            Assert.That(labelA, Is.EqualTo(labelB));
            Assert.That(scoreA, Is.EqualTo(scoreB).Within(1e-6f));
        }

        [Test]
        public void NullOverrides_IsBitIdenticalToGlobalSpeedAccumulator()
        {
            // Arrange — SetPerEmotionDynamics(null) must behave exactly like an empty list.
            var withNullOverrides = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 6f, decaySpeed: 2.5f);
            withNullOverrides.SetPerEmotionDynamics(null);

            var withoutOverridesCall = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 6f, decaySpeed: 2.5f);

            withNullOverrides.SetTargetEmotion("fear", 0.9f);
            withoutOverridesCall.SetTargetEmotion("fear", 0.9f);

            // Act
            for (int i = 0; i < 30; i++)
            {
                withNullOverrides.Tick(0.05f);
                withoutOverridesCall.Tick(0.05f);
            }

            // Assert
            foreach (KeyValuePair<string, float> kvp in withoutOverridesCall.OutputScores)
            {
                Assert.That(withNullOverrides.OutputScores[kvp.Key], Is.EqualTo(kvp.Value).Within(1e-6f),
                    $"Output for '{kvp.Key}' must be bit-identical with null per-emotion dynamics.");
            }
        }

        [Test]
        public void PerEmotionAttack_FasterOverrideRisesFasterThanNonOverriddenLabel()
        {
            // Arrange — "anger" gets a fast attack override; the global speed is slow. A
            // non-overridden label ("sadness") must rise at the slow global rate.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 1f, decaySpeed: 1f);
            accumulator.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>
            {
                new("anger", attackSpeed: 20f, decaySpeed: 1f)
            });

            accumulator.SetTargetEmotions(new Dictionary<string, float>
            {
                { "anger", 1f },
                { "sadness", 1f }
            });

            // Act — a single tick from rest.
            accumulator.Tick(0.05f);

            // Assert — anger (fast override) must have risen further than sadness (slow global).
            Assert.That(accumulator.OutputScores["anger"], Is.GreaterThan(accumulator.OutputScores["sadness"]),
                "A per-emotion fast attack override must rise faster than a non-overridden label given the same target/dt.");
        }

        [Test]
        public void PerEmotionDecay_SlowerOverrideLingersLongerThanNonOverriddenLabel()
        {
            // Arrange — "sadness" gets a slow decay override; the global decay speed is fast.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 20f, decaySpeed: 20f);
            accumulator.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>
            {
                new("sadness", attackSpeed: 20f, decaySpeed: 0.5f)
            });

            accumulator.SetImmediateEmotion("joy", 0f); // ensure a clean baseline
            accumulator.SetTargetEmotions(new Dictionary<string, float>
            {
                { "sadness", 1f },
                { "anger", 1f }
            });

            // Settle both up first.
            for (int i = 0; i < 20; i++)
                accumulator.Tick(0.05f);

            // Act — decay both to zero.
            accumulator.SetTargetEmotion(_taxonomy.Neutral.Label, 0f);
            for (int i = 0; i < 10; i++)
                accumulator.Tick(0.05f);

            // Assert — sadness (slow decay override) must linger higher than anger (fast global decay).
            Assert.That(accumulator.OutputScores["sadness"], Is.GreaterThan(accumulator.OutputScores["anger"]),
                "A per-emotion slow decay override must linger longer than a non-overridden label after target -> 0.");
        }

        [Test]
        public void NonOverriddenLabel_FallsBackToGlobalSpeed()
        {
            // Arrange — an override on "anger" must not affect "joy" (unlisted): joy must match a
            // twin accumulator with no overrides at all.
            var withOverrideOnOtherLabel = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 4f, decaySpeed: 4f);
            withOverrideOnOtherLabel.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>
            {
                new("anger", attackSpeed: 20f, decaySpeed: 20f)
            });

            var withoutAnyOverride = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 4f, decaySpeed: 4f);

            withOverrideOnOtherLabel.SetTargetEmotion("joy", 0.7f);
            withoutAnyOverride.SetTargetEmotion("joy", 0.7f);

            // Act
            for (int i = 0; i < 15; i++)
            {
                withOverrideOnOtherLabel.Tick(0.05f);
                withoutAnyOverride.Tick(0.05f);
            }

            // Assert
            Assert.That(withOverrideOnOtherLabel.OutputScores["joy"],
                Is.EqualTo(withoutAnyOverride.OutputScores["joy"]).Within(1e-6f),
                "A label with no authored override must use the global speed unchanged.");
        }

        [Test]
        public void SetLerpSpeed_AtRuntime_StillAppliesToNonOverriddenLabels()
        {
            // Arrange — "anger" is overridden; "joy" is not. A runtime global speed change must
            // still move joy's per-tick increment, while anger keeps using its override.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 1f, decaySpeed: 1f);
            accumulator.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>
            {
                new("anger", attackSpeed: 5f, decaySpeed: 5f)
            });

            accumulator.SetTargetEmotions(new Dictionary<string, float>
            {
                { "joy", 1f },
                { "anger", 1f }
            });
            accumulator.Tick(0.05f);
            float joySlow = accumulator.OutputScores["joy"];
            float angerBeforeSpeedChange = accumulator.OutputScores["anger"];

            // Act — bump the global speed at runtime.
            accumulator.SetLerpSpeed(100f);
            accumulator.Tick(0.05f);
            float joyFastIncrement = accumulator.OutputScores["joy"] - joySlow;

            accumulator.SetTargetEmotions(new Dictionary<string, float>
            {
                { "joy", 1f },
                { "anger", 1f }
            });

            // Assert — joy's increment grew after the runtime global speed bump.
            Assert.That(joyFastIncrement, Is.GreaterThan(0f));
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(joySlow),
                "Runtime SetLerpSpeed must still apply to a non-overridden label.");
            Assert.That(angerBeforeSpeedChange, Is.GreaterThan(0f));
        }

        [Test]
        public void SetPerEmotionDynamics_CalledTwice_RecomputesFromScratch()
        {
            // Arrange — a second call with a different (or empty) list must fully replace the
            // first call's overrides, not merge with them.
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 2f, decaySpeed: 2f);
            accumulator.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>
            {
                new("anger", attackSpeed: 20f, decaySpeed: 20f)
            });

            // Act — clear overrides entirely.
            accumulator.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>());
            accumulator.SetTargetEmotion("anger", 1f);
            accumulator.Tick(0.05f);
            float angerWithClearedOverride = accumulator.OutputScores["anger"];

            var globalOnly = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 2f, decaySpeed: 2f);
            globalOnly.SetTargetEmotion("anger", 1f);
            globalOnly.Tick(0.05f);

            // Assert — anger now behaves at the global speed, matching a twin with no override ever set.
            Assert.That(angerWithClearedOverride, Is.EqualTo(globalOnly.OutputScores["anger"]).Within(1e-6f),
                "A later SetPerEmotionDynamics call must fully replace prior overrides.");
        }

        [Test]
        public void SetPerEmotionDynamics_NaNSpeed_FallsBackToGlobal_NoNaNInOutput()
        {
            // Arrange — a programmatic override with a NaN attack speed must not poison the
            // accumulator's alpha/next math; the label should behave exactly like a twin
            // accumulator using the global speed (no override) for that axis.
            var withNaNOverride = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 3f, decaySpeed: 1.5f);
            withNaNOverride.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>
            {
                new("anger", attackSpeed: float.NaN, decaySpeed: 1.5f)
            });

            var globalOnly = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 3f, decaySpeed: 1.5f);

            withNaNOverride.SetTargetEmotion("anger", 0.9f);
            globalOnly.SetTargetEmotion("anger", 0.9f);

            // Act
            for (int i = 0; i < 20; i++)
            {
                withNaNOverride.Tick(0.05f);
                globalOnly.Tick(0.05f);
            }

            // Assert — finite output matching the global-speed twin, not NaN.
            foreach (KeyValuePair<string, float> kvp in withNaNOverride.OutputScores)
            {
                Assert.That(float.IsNaN(kvp.Value), Is.False, $"Output for '{kvp.Key}' must never be NaN.");
                Assert.That(kvp.Value, Is.EqualTo(globalOnly.OutputScores[kvp.Key]).Within(1e-6f),
                    $"A NaN attack override must fall back to the global speed for '{kvp.Key}'.");
            }
        }

        [Test]
        public void Tick_WithPerEmotionDynamics_ZeroAlloc()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy, lerpSpeed: 5f, decaySpeed: 2f);
            accumulator.SetPerEmotionDynamics(new List<EmotionDynamicsEntry>
            {
                new("anger", attackSpeed: 12f, decaySpeed: 6f),
                new("sadness", attackSpeed: 3f, decaySpeed: 0.8f)
            });
            accumulator.SetTargetEmotion("anger", 0.7f);

            // Warm up.
            for (int i = 0; i < 50; i++)
                accumulator.Tick(0.016f);

            // Act
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 200; i++)
                    accumulator.Tick(0.016f);
            });

            // Assert
            Assert.That(allocationCount, Is.EqualTo(0L),
                "Tick with per-emotion dynamics configured must produce zero managed allocation events in steady state.");
        }
    }
}
