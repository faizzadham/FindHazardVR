using System.Reflection;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Controller-level persona-baseline tests: baseline resolution through
    ///     <c>BuildPipeline</c>, expressiveness gain
    ///     application at event time, and mood exposure through <see cref="ConvaiEmotionController.Current" />.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerPersonaBaselineTests
    {
        private const string CharacterId = "persona-baseline-char";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerPersonaBaselineTests));
            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");
            _harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
        }

        [TearDown]
        public void TearDown()
        {
            // A log this fixture did not expect fails the test that produced it. The pin held
            // LogAssert.ignoreFailingMessages for the whole fixture instead, under which these
            // tests could not fail for a logging reason at all.
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        private static ConvaiEmotionProfile CreateProfileWithBaseline(string label, float intensity)
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetPrivateField(profile, "baselineEmotionLabel", label);
            SetPrivateField(profile, "baselineIntensity", intensity);
            return profile;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private static object GetPrivateField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            return field.GetValue(target);
        }

        [Test]
        public void NonzeroBaseline_AtRest_MoodIsBaselineAndDominantIsNeutral()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateProfileWithBaseline("joy", 0.2f);
            try
            {
                _harness.ApplyProfile(profile);
                for (int i = 0; i < 20; i++)
                    _harness.Tick(0.05f);

                // Assert
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("neutral"),
                    "A resting persona baseline must never appear as the dominant (transient) emotion.");
                Assert.That(_harness.Controller.Current.MoodLabel, Is.EqualTo("joy"));
                Assert.That(_harness.Controller.Current.MoodScore, Is.GreaterThan(0f));
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("joy"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.GreaterThan(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void NonzeroBaseline_ServerEmotionOverridesThenSettlesBackToMood()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateProfileWithBaseline("joy", 0.2f);
            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 3));
                for (int i = 0; i < 30; i++)
                    _harness.Tick(0.05f);

                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("anger"),
                    "Server emotion must dominate while active.");

                // Act — clear the override and let it decay back to the baseline.
                _harness.Controller.SetEmotionOverride("neutral", 0f);
                for (int i = 0; i < 60; i++)
                    _harness.Tick(0.05f);

                // Assert — mood persists throughout, unaffected by the transient's rise and fall.
                Assert.That(_harness.Controller.Current.MoodLabel, Is.EqualTo("joy"));
                Assert.That(_harness.Controller.Current.MoodScore, Is.GreaterThan(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }




        [Test]
        public void ExpressivenessGain_ScalesIncomingTransientScore()
        {
            // Arrange
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            var entries = new System.Collections.Generic.List<EmotionExpressivenessEntry>
            {
                new("joy", 2f)
            };
            SetPrivateField(profile, "expressiveness", entries);

            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 2)); // NormalizedIntensity ~0.5 pre-gain
                for (int i = 0; i < 60; i++)
                    _harness.Tick(0.05f);

                // Assert — gain 2 should push the settled score meaningfully higher than the
                // ungained baseline case (clamped to 1).
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("joy"));
                Assert.That(_harness.Controller.Current.DominantScore, Is.GreaterThan(0.7f),
                    "Gain 2 on a mid-intensity server event should push the settled score well above the ungained value.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     A character on the default profile reports its resting mood as the quiet trust that
        ///     profile authors, on the mood channel and only on the mood channel.
        /// </summary>
        /// <remarks>
        ///     This used to assert a neutral mood at zero, back when the default profile rested at
        ///     nothing. The separation it was really guarding is unchanged and is what the second
        ///     half asserts: an authored resting mood must never surface as an active emotion, or
        ///     every downstream consumer would react to a feeling the conversation never produced.
        /// </remarks>
        [Test]
        public void DefaultProfile_RestsOnItsAuthoredMoodWithoutFakingAnActiveEmotion()
        {
            // Arrange / Act
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Tick(1f / 60f);

                // Assert
                Assert.That(_harness.Controller.Current.MoodLabel, Is.EqualTo("trust"));
                Assert.That(_harness.Controller.Current.MoodScore, Is.GreaterThan(0f));
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("neutral"),
                    "A resting mood must never be reported as an active emotion.");
                Assert.That(_harness.Controller.Current.DominantScore, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
