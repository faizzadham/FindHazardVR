using System.Reflection;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Modules.BodyLanguage.Core.Policy;
using Convai.Modules.BodyLanguage.Data;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="PostureDirector" />: spot values flow through
    ///     (Speaking ⇒ open posture target), targets slew rather than snap, and emotion bias
    ///     composes with the state policy bias.
    /// </summary>
    public sealed class PostureDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float SlewSeconds = 1.5f;

        private static BodyLanguageStatePolicy SpeakingPolicy() => new()
        {
            State = DialogueState.Speaking,
            PostureOpennessBias = 0.2f,
            SagittalLeanBias = 0.1f
        };

        private static BodyLanguageStatePolicy ListeningPolicy() => new()
        {
            State = DialogueState.Listening,
            PostureOpennessBias = 0.05f,
            SagittalLeanBias = 0.35f
        };

        [Test]
        public void Speaking_ProducesOpenPostureTarget()
        {
            var director = new PostureDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();

            for (int i = 0; i < 600; i++)
                director.Tick(in speaking, emotion, SlewSeconds, Dt);

            Assert.That(director.OpennessTarget, Is.GreaterThan(0f),
                "Speaking must settle on an open posture target.");
        }

        [Test]
        public void Listening_ProducesLeanInTarget()
        {
            var director = new PostureDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy listening = ListeningPolicy();

            for (int i = 0; i < 600; i++)
                director.Tick(in listening, emotion, SlewSeconds, Dt);

            Assert.That(director.LeanTarget, Is.GreaterThan(0.2f),
                "Listening must settle on a lean-in target.");
        }

        [Test]
        public void StateFlip_TargetSlews_NeverSnaps()
        {
            var director = new PostureDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy listening = ListeningPolicy();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();

            for (int i = 0; i < 300; i++)
                director.Tick(in listening, emotion, SlewSeconds, Dt);
            float leanAtListening = director.LeanTarget;

            director.Tick(in speaking, emotion, SlewSeconds, Dt);

            Assert.That(director.LeanTarget, Is.Not.EqualTo(speaking.SagittalLeanBias).Within(1e-5f),
                "A single tick after a state flip must not snap to the new target.");
            Assert.That(director.LeanTarget, Is.LessThan(leanAtListening),
                "The target must be moving toward the new goal, not stuck at the old one.");
        }

        [Test]
        public void EmotionBias_ComposesWithStateBias()
        {
            var director = new PostureDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = new() { State = DialogueState.Idle, PostureOpennessBias = 0f };

            // Simulate an emotion modulator that already resolved a positive openness bias
            // (equivalent to a joyful reading) via the real Tick path is covered elsewhere;
            // here we assert the director composes whatever the modulator reports.
            for (int i = 0; i < 600; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt);

            Assert.That(director.OpennessTarget, Is.EqualTo(0f).Within(1e-3f),
                "With an identity emotion modulator, Idle's zero bias must settle at zero.");
        }

        [Test]
        public void RealEmotion_MorphsSustainedPostureTargetPerAcceptanceMatrix()
        {
            // Acceptance lock-in: a real backend emotion — resolved through the
            // modulator's own Tick path, NOT an identity stand-in — must move the sustained
            // posture target in the documented direction, layered on top of the state-policy
            // bias. This is exactly the sustained silhouette that stays alive during speech,
            // so it is what makes joy/anger/sadness read on the body while the character talks.
            ConvaiBodyLanguageProfile profile = ProfileWithModulationEnabled();
            BodyLanguageStatePolicy speaking = SpeakingPolicy(); // openness 0.2, lean 0.1

            // Joy opens the chest (openness bias +0.4) on top of Speaking's 0.2.
            Assert.That(SettledOpenness(profile, speaking, "joy"), Is.GreaterThan(0.5f),
                "Joy must raise the openness target well above Speaking's own 0.2 bias.");

            // Anger drives a strong forward lean (lean bias +0.5) on top of Speaking's 0.1.
            Assert.That(SettledLean(profile, speaking, "anger"), Is.GreaterThan(0.5f),
                "Anger must drive a strong forward lean on top of the state bias.");

            // Sadness closes/slumps (openness bias -0.5), pulling Speaking's +0.2 openness negative.
            Assert.That(SettledOpenness(profile, speaking, "sadness"), Is.LessThan(0f),
                "Sadness must pull the openness target below neutral (a slump), not stay open.");
        }

        private static float SettledOpenness(ConvaiBodyLanguageProfile profile, BodyLanguageStatePolicy state, string label) =>
            SettleWithEmotion(profile, state, label).OpennessTarget;

        private static float SettledLean(ConvaiBodyLanguageProfile profile, BodyLanguageStatePolicy state, string label) =>
            SettleWithEmotion(profile, state, label).LeanTarget;

        private static PostureDirector SettleWithEmotion(ConvaiBodyLanguageProfile profile, BodyLanguageStatePolicy state, string label)
        {
            var director = new PostureDirector();
            var emotion = new EmotionBodyModulator();
            // No score table published (EmptyScores) exercises the dominant-label blend path a
            // basic ConvaiEmotionController drives — the modifier applies at its full authored bias.
            var reading = new EmotionReading(label, 1f, EmotionReading.EmptyScores, 0f, 0f);
            emotion.Tick(profile, in reading);

            for (int i = 0; i < 600; i++)
                director.Tick(in state, emotion, SlewSeconds, Dt);

            return director;
        }

        // CreateDefault ships with emotion modulation OFF (opt-in); enable it via the same
        // private-field reflection EmotionBodyModulatorTests uses, so this test exercises the
        // real authored emotion table (joy/anger/sadness) rather than the identity path.
        private static ConvaiBodyLanguageProfile ProfileWithModulationEnabled()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            FieldInfo field = typeof(ConvaiBodyLanguageProfile)
                .GetField("enableEmotionModulation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "Missing field enableEmotionModulation.");
            field.SetValue(profile, true);
            return profile;
        }

        [Test]
        public void FirstTick_Snaps()
        {
            var director = new PostureDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();

            director.Tick(in speaking, emotion, SlewSeconds, Dt);

            Assert.That(director.OpennessTarget, Is.EqualTo(0.2f).Within(1e-4f),
                "The first tick must snap so a fresh director never eases in from zero.");
        }

        [Test]
        public void Reset_ReturnsToZeroAndNextTickSnaps()
        {
            var director = new PostureDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            for (int i = 0; i < 300; i++)
                director.Tick(in speaking, emotion, SlewSeconds, Dt);

            director.Reset();
            Assert.That(director.OpennessTarget, Is.EqualTo(0f));

            director.Tick(in speaking, emotion, SlewSeconds, Dt);
            Assert.That(director.OpennessTarget, Is.EqualTo(0.2f).Within(1e-4f));
        }
    }
}
