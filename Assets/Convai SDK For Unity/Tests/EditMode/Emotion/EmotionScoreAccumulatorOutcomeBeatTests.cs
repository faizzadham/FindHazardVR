using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     The outcome beat is a brief mood nudge that rides on top of whatever mood is active and
    ///     lifts off again. These tests pin the property that motivated it: the beat must never
    ///     consume the mood underneath it.
    /// </summary>
    /// <remarks>
    ///     The beat used to be implemented as <c>SetMood</c> followed by <c>ClearMood</c>, and
    ///     clearing means "return to the AUTHORED baseline" — so a two-second reaction to an action
    ///     succeeding silently discarded a gameplay <c>SetMood</c> and any accumulated drift.
    /// </remarks>
    public sealed class EmotionScoreAccumulatorOutcomeBeatTests
    {
        private EmotionTaxonomyAsset _taxonomy;

        [SetUp]
        public void SetUp()
        {
            _taxonomy = EmotionTaxonomyAsset.CreateDefault();
            _taxonomy.EnsureBuilt();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_taxonomy);

        private EmotionScoreAccumulator NewAccumulator() => new(_taxonomy);

        private static void Advance(EmotionScoreAccumulator accumulator, float seconds)
        {
            const float step = 1f / 60f;
            for (float t = 0f; t < seconds; t += step) accumulator.Tick(step);
        }

        [Test]
        public void Beat_RisesDuringHold_ThenLiftsOff()
        {
            EmotionScoreAccumulator accumulator = NewAccumulator();

            accumulator.SetOutcomeBeat("joy", 0.3f, holdSeconds: 1f, transitionSeconds: 0.3f);
            Advance(accumulator, 0.6f);

            accumulator.GetMood(out string duringLabel, out float duringScore);
            Assert.That(duringLabel, Is.EqualTo("joy"));
            Assert.That(duringScore, Is.GreaterThan(0.2f), "The beat must actually be felt while it holds.");

            Advance(accumulator, 3f);
            accumulator.GetMood(out _, out float afterScore);
            Assert.That(afterScore, Is.EqualTo(0f).Within(0.001f),
                "The beat must expire on its own without anyone clearing it.");
        }

        [Test]
        public void Beat_DoesNotConsumeAGameplayMood()
        {
            EmotionScoreAccumulator accumulator = NewAccumulator();

            // Gameplay sets a strong, deliberate mood.
            accumulator.SetPersonaBaselineTarget("anger", 0.8f, transitionSeconds: 0f);
            Advance(accumulator, 0.5f);
            accumulator.GetMood(out string beforeLabel, out float beforeScore);
            Assert.That(beforeLabel, Is.EqualTo("anger"));

            // An action succeeds and fires a beat, which then expires.
            accumulator.SetOutcomeBeat("joy", 0.3f, holdSeconds: 1f, transitionSeconds: 0.3f);
            Advance(accumulator, 4f);

            accumulator.GetMood(out string afterLabel, out float afterScore);
            Assert.That(afterLabel, Is.EqualTo("anger"),
                "The gameplay mood must still be there once the beat lifts off.");
            Assert.That(afterScore, Is.EqualTo(beforeScore).Within(0.01f),
                "The beat must not have eroded the gameplay mood's intensity either.");
        }

        [Test]
        public void Beat_DoesNotConsumeAnAuthoredBaseline()
        {
            EmotionScoreAccumulator accumulator = NewAccumulator();

            accumulator.SetPersonaBaseline("joy", 0.22f);
            Advance(accumulator, 0.5f);

            accumulator.SetOutcomeBeat("anger", 0.3f, holdSeconds: 1f, transitionSeconds: 0.3f);
            Advance(accumulator, 4f);

            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("joy"));
            Assert.That(score, Is.EqualTo(0.22f).Within(0.01f));
        }

        [Test]
        public void ClearOutcomeBeat_LeavesOtherMoodChannelsUntouched()
        {
            EmotionScoreAccumulator accumulator = NewAccumulator();

            accumulator.SetPersonaBaseline("trust", 0.4f);
            accumulator.SetOutcomeBeat("joy", 0.9f, holdSeconds: 5f, transitionSeconds: 0.1f);
            Advance(accumulator, 0.5f);
            accumulator.GetMood(out string beatLabel, out _);
            Assert.That(beatLabel, Is.EqualTo("joy"), "Sanity: the strong beat is the reported mood while active.");

            accumulator.ClearOutcomeBeat();
            Advance(accumulator, 0.1f);

            accumulator.GetMood(out string label, out float score);
            Assert.That(label, Is.EqualTo("trust"));
            Assert.That(score, Is.EqualTo(0.4f).Within(0.01f));
        }

        [Test]
        public void UnknownOrNeutralOrZeroIntensityBeat_ClearsInsteadOfStarting()
        {
            EmotionScoreAccumulator accumulator = NewAccumulator();
            accumulator.SetPersonaBaseline("joy", 0.2f);

            foreach ((string label, float intensity) in new[]
                     {
                         ("nosuchlabel", 0.5f),
                         ("neutral", 0.5f),
                         ("anger", 0f),
                         (string.Empty, 0.5f)
                     })
            {
                accumulator.SetOutcomeBeat(label, intensity, holdSeconds: 5f, transitionSeconds: 0.1f);
                Advance(accumulator, 0.5f);

                accumulator.GetMood(out string moodLabel, out _);
                Assert.That(moodLabel, Is.EqualTo("joy"),
                    $"'{label}' @ {intensity} is not a usable beat, so the underlying mood must still be reported.");
            }
        }

        [Test]
        public void Retargeting_MidBeat_OntoTheSameLabel_ResumesRatherThanRestarting()
        {
            EmotionScoreAccumulator accumulator = NewAccumulator();

            accumulator.SetOutcomeBeat("joy", 0.4f, holdSeconds: 1f, transitionSeconds: 0.3f);
            Advance(accumulator, 0.5f);
            accumulator.GetMood(out _, out float firstScore);

            accumulator.SetOutcomeBeat("joy", 0.4f, holdSeconds: 1f, transitionSeconds: 0.3f);
            accumulator.Tick(1f / 60f);
            accumulator.GetMood(out _, out float afterRetarget);

            Assert.That(afterRetarget, Is.EqualTo(firstScore).Within(0.02f),
                "Back-to-back outcomes must read as one continuous reaction, not a restart from zero.");
        }
    }
}
