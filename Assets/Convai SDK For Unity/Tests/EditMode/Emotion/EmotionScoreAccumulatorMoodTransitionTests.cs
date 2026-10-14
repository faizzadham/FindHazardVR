using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Runtime mood-API tests for <see cref="EmotionScoreAccumulator" />'s two-slot
    ///     persona-baseline/mood crossfade: smooth same-label retargets, label-switch crossfades,
    ///     clearing to "no mood", instant snaps, transient suppression of the fold-in, and the
    ///     the standing invariants (transient-only <see cref="EmotionScoreAccumulator.GetDominant" />,
    ///     stronger-slot reporting, and baseline persistence across <see cref="EmotionScoreAccumulator.Reset" />).
    /// </summary>
    [TestFixture]
    public sealed class EmotionScoreAccumulatorMoodTransitionTests
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
        public void SameLabelRetarget_RisesMonotonicallyToNewTarget()
        {
            // Arrange — start a rise toward 0.3, then retarget the SAME label to a higher
            // intensity mid-transition (same slot, no swap).
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaselineTarget("joy", 0.3f, transitionSeconds: 1f);
            for (int i = 0; i < 10; i++)
                accumulator.Tick(0.05f);

            accumulator.SetPersonaBaselineTarget("joy", 0.6f, transitionSeconds: 1f);

            // Act / Assert — monotonic rise for 2x transitionSeconds of ticking, reaching the target.
            float previous = -1f;
            int steps = Mathf.CeilToInt(2f / 0.05f);
            for (int i = 0; i < steps; i++)
            {
                accumulator.Tick(0.05f);
                accumulator.GetMood(out string label, out float score);
                Assert.That(label, Is.EqualTo("joy"));
                Assert.That(score, Is.GreaterThanOrEqualTo(previous - 1e-5f), "Mood rise must be monotonic.");
                previous = score;
            }

            accumulator.GetMood(out string finalLabel, out float finalScore);
            Assert.That(finalLabel, Is.EqualTo("joy"));
            Assert.That(finalScore, Is.EqualTo(0.6f).Within(0.02f));
        }

        [Test]
        public void LabelSwitch_OldDecaysToExactZero_NewRisesBounded()
        {
            // Arrange — snap to "joy" first, then crossfade to "anger".
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaseline("joy", 0.5f);
            accumulator.SetPersonaBaselineTarget("anger", 0.4f, transitionSeconds: 0.5f);

            // Act
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.05f);

            // Assert — new label settles at target, never overshoots; old label's output fold is
            // gone (its transient current score was always 0, and its mood slot decayed to 0).
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("anger"));
            Assert.That(score, Is.EqualTo(0.4f).Within(0.02f));
            Assert.That(score, Is.LessThanOrEqualTo(0.4f + 1e-4f), "Mood score must never overshoot the target.");
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void ClearMood_DecaysToZero_AndGetMoodReturnsNeutral()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaseline("joy", 0.5f);

            // Act — empty label means "transition to no mood".
            accumulator.SetPersonaBaselineTarget(string.Empty, 0f, transitionSeconds: 0.5f);
            for (int i = 0; i < 200; i++)
                accumulator.Tick(0.05f);

            // Assert
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(score, Is.EqualTo(0f));
        }

        [Test]
        public void TransitionSecondsZero_SnapsImmediately()
        {
            // Arrange / Act — no ticks at all; a <= 0 transitionSeconds must snap synchronously.
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaseline("joy", 0.3f);
            accumulator.SetPersonaBaselineTarget("anger", 0.7f, transitionSeconds: 0f);

            // Assert
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("anger"));
            Assert.That(score, Is.EqualTo(0.7f).Within(1e-5f));
        }

        [Test]
        public void FoldIn_SuppressedByStrongTransient_DuringTransition()
        {
            // Arrange — a maximal transient (anger @ 1) must fully suppress the mood fold-in for
            // a DIFFERENT label (joy) by the fold formula: slotIntensity * (1 - Clamp01(transient)).
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaseline("joy", 0.6f);
            accumulator.SetImmediateEmotion("anger", 1f);

            // Act
            accumulator.Tick(0.05f);

            // Assert
            Assert.That(accumulator.OutputScores["joy"], Is.EqualTo(0f).Within(1e-4f),
                "A maxCurrentTransient of 1 must fully suppress the mood fold-in contribution.");
        }

        [Test]
        public void GetDominant_NeverReportsMoodLabel_AbsentTransients()
        {
            // Arrange — a strong, settled persona baseline with zero transient activity.
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaseline("joy", 0.8f);

            // Act
            for (int i = 0; i < 60; i++)
                accumulator.Tick(0.05f);

            // Assert — GetDominant is transient-only, never surfaces the mood/baseline.
            accumulator.GetDominant(out string dominantLabel, out float dominantScore);
            Assert.That(dominantLabel, Is.EqualTo(_taxonomy.Neutral.Label));
            Assert.That(dominantScore, Is.EqualTo(0f));
        }

        [Test]
        public void Reset_PreservesMoodSlots()
        {
            // Arrange
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaseline("joy", 0.4f);
            accumulator.Tick(0.05f);

            // Act — Reset() must not clear the persona baseline/mood slots.
            accumulator.Reset();

            // Assert — GetMood still reports the persisted baseline immediately after Reset...
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("joy"));
            Assert.That(score, Is.EqualTo(0.4f).Within(1e-4f));

            // ...and the next Tick re-folds it into OutputScores even though Reset() zeroed it.
            accumulator.Tick(0.05f);
            Assert.That(accumulator.OutputScores["joy"], Is.GreaterThan(0f));
        }

        [Test]
        public void GetMood_ReportsStrongerSlot_MidCrossfade()
        {
            // Arrange — a strong settled baseline ("joy" @ 0.8), then a SLOW crossfade to a
            // weaker target ("anger" @ 0.2) so the outgoing slot is still stronger after one tick.
            var accumulator = new EmotionScoreAccumulator(_taxonomy);
            accumulator.SetPersonaBaseline("joy", 0.8f);
            accumulator.SetPersonaBaselineTarget("anger", 0.2f, transitionSeconds: 5f);

            // Act
            accumulator.Tick(0.05f);

            // Assert — GetMood reports the STRONGER slot, which is still the outgoing one.
            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("joy"), "Early in a slow crossfade, the outgoing slot is still stronger.");
            Assert.That(score, Is.GreaterThan(0.2f));
        }
    }
}
