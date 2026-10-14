using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Gestures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     T3: reading publication. <see cref="ConvaiBodyLanguageController"/> lives
    ///     under Application.isPlaying-gated registration (mirrors the module's own established
    ///     IHeadGestureChannel lifecycle, not Gaze's eager one), so publication is exercised at
    ///     the POCO composition level here — the exact field mapping
    ///     <c>RefreshCurrentReading()</c> performs — plus the reading's own range/default
    ///     contract. The <see cref="IBodyLanguageSource" /> registration plumbing itself is covered
    ///     generically by <see cref="Convai.Tests.EditMode.Embodiment.CharacterServiceRegistryTests" />.
    /// </summary>
    public sealed class BodyLanguageReadingPublicationTests
    {
        private sealed class FakeBodyLanguageSource : IBodyLanguageSource
        {
            public BodyLanguageReading Current { get; set; }
        }

        [Test]
        public void Reading_ComposesFromDirectorAndSolverState_FieldsMatchSources()
        {
            var director = new HeadGestureDirector();
            director.Seed(1);
            director.TryRequest(HeadGestureKind.Tilt, 0.7f, out _);

            const float postureOpenness = 0.42f;
            const float postureLean = -0.3f;
            const float shoulderTension = 0.15f;
            const float normalizedBreathPhase = 0.6f;
            const GestureSuppression suppression = GestureSuppression.UpperBody;
            const GestureCueKind lastCue = GestureCueKind.Greeting;

            var reading = new BodyLanguageReading(
                DialogueState.Speaking,
                postureOpenness,
                postureLean,
                shoulderTension,
                normalizedBreathPhase,
                suppression,
                director.IsPlaying,
                director.ActiveKind,
                lastCue);

            Assert.That(reading.DialogueState, Is.EqualTo(DialogueState.Speaking));
            Assert.That(reading.PostureOpenness, Is.EqualTo(postureOpenness));
            Assert.That(reading.PostureLean, Is.EqualTo(postureLean));
            Assert.That(reading.ShoulderTension, Is.EqualTo(shoulderTension));
            Assert.That(reading.BreathPhase, Is.EqualTo(normalizedBreathPhase));
            Assert.That(reading.Suppression, Is.EqualTo(suppression));
            Assert.IsTrue(reading.HasActiveHeadGesture, "The director accepted a Tilt request — must report active.");
            Assert.That(reading.ActiveHeadGestureKind, Is.EqualTo(HeadGestureKind.Tilt));
            Assert.That(reading.LastGestureCueKind, Is.EqualTo(lastCue));
        }

        [Test]
        public void Reading_FieldsStayWithinDocumentedRanges_AcrossManyTicks()
        {
            var director = new HeadGestureDirector();
            director.Seed(2);
            const float dt = 1f / 60f;

            for (int i = 0; i < 600; i++)
            {
                if (i % 90 == 0)
                    director.TryRequest((HeadGestureKind)(i / 90 % 3), 1f, out _);

                director.Tick(dt, 15f, 20f, 10f, refractorySeconds: 0.2f, refractoryVarianceSeconds: 0.1f);

                var reading = new BodyLanguageReading(
                    DialogueState.Speaking, 0.5f, -0.2f, 0.1f, 0.3f,
                    GestureSuppression.None, director.IsPlaying, director.ActiveKind, GestureCueKind.None);

                Assert.That(reading.PostureOpenness, Is.InRange(-1f, 1f));
                Assert.That(reading.PostureLean, Is.InRange(-1f, 1f));
                Assert.That(reading.ShoulderTension, Is.InRange(-1f, 1f));
                Assert.That(reading.BreathPhase, Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void Publication_ThroughFakeSource_RoundTripsTheSameReading()
        {
            // Stands in for "Current and Context.BodyLanguageSource.Current agree" (the
            // controller trivially satisfies this — both read the same private field — the
            // interesting contract is that ANY IBodyLanguageSource implementer, including a
            // fake, round-trips a reading without alteration).
            var reading = new BodyLanguageReading(
                DialogueState.Listening, 0.1f, 0.2f, 0.3f, 0.4f,
                GestureSuppression.None, true, HeadGestureKind.Shake, GestureCueKind.Uncertain);

            IBodyLanguageSource source = new FakeBodyLanguageSource { Current = reading };

            Assert.That(source.Current.DialogueState, Is.EqualTo(DialogueState.Listening));
            Assert.That(source.Current.ActiveHeadGestureKind, Is.EqualTo(HeadGestureKind.Shake));
            Assert.That(source.Current.LastGestureCueKind, Is.EqualTo(GestureCueKind.Uncertain));
        }

        [Test]
        public void None_IsFullyAtRest()
        {
            BodyLanguageReading none = BodyLanguageReading.None;

            Assert.That(none.DialogueState, Is.EqualTo(DialogueState.Idle));
            Assert.That(none.PostureOpenness, Is.EqualTo(0f));
            Assert.That(none.PostureLean, Is.EqualTo(0f));
            Assert.That(none.ShoulderTension, Is.EqualTo(0f));
            Assert.That(none.BreathPhase, Is.EqualTo(0f));
            Assert.That(none.Suppression, Is.EqualTo(GestureSuppression.None));
            Assert.IsFalse(none.HasActiveHeadGesture);
            Assert.That(none.LastGestureCueKind, Is.EqualTo(GestureCueKind.None));
            Assert.That(none.WeightShift, Is.EqualTo(0f));
            Assert.That(none.Expressiveness, Is.EqualTo(0.5f));
            Assert.That(none.ActiveReaction, Is.EqualTo(ReactionKind.None));
        }

        [Test]
        public void FullConstructor_FlowsWeightShiftExpressivenessAndActiveReaction()
        {
            const float weightShift = -0.4f;
            const float expressiveness = 0.75f;
            const ReactionKind activeReaction = ReactionKind.SurpriseFlinch;

            var reading = new BodyLanguageReading(
                DialogueState.Reacting, 0.1f, 0.2f, 0.3f, 0.4f,
                GestureSuppression.None, false, HeadGestureKind.Nod, GestureCueKind.None,
                weightShift, expressiveness, activeReaction);

            Assert.That(reading.WeightShift, Is.EqualTo(weightShift));
            Assert.That(reading.Expressiveness, Is.EqualTo(expressiveness));
            Assert.That(reading.ActiveReaction, Is.EqualTo(activeReaction));
        }

        [Test]
        public void LegacyConstructor_Delegates_WithDocumentedDefaults()
        {
            var reading = new BodyLanguageReading(
                DialogueState.Speaking, 0.1f, 0.2f, 0.3f, 0.4f,
                GestureSuppression.None, false, HeadGestureKind.Nod, GestureCueKind.None);

            Assert.That(reading.WeightShift, Is.EqualTo(0f));
            Assert.That(reading.Expressiveness, Is.EqualTo(0.5f));
            Assert.That(reading.ActiveReaction, Is.EqualTo(ReactionKind.None));
        }
    }
}
