using System.Reflection;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Data-model tests for <see cref="ConvaiEmotionProfile" />'s Mood Drift block:
    ///     defaults and <c>OnValidate</c> clamps/NaN fallbacks.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfileMoodDriftTests
    {
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private static void InvokeOnValidate(ConvaiEmotionProfile profile)
        {
            MethodInfo onValidate = typeof(ConvaiEmotionProfile).GetMethod(
                "OnValidate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onValidate, "ConvaiEmotionProfile must declare OnValidate.");
            onValidate.Invoke(profile, null);
        }

        /// <summary>
        ///     A character nobody configured does not accumulate a resting mood, but is wired to
        ///     do so the moment its author turns drift on.
        /// </summary>
        /// <remarks>
        ///     <c>CreateDefault</c> installs the Composed temperament, whose own faint resting
        ///     <c>trust</c> is authored rather than accumulated — drift is off, so nothing the
        ///     conversation does moves it until an author says so. The three rates still ship at
        ///     usable values so that ticking the box is the only step. The switch's own behaviour is
        ///     covered by the drift tests below and by
        ///     <see cref="EmotionProfileDemeanorPresetTests" /> for the temperaments that enable it.
        /// </remarks>
        [Test]
        public void CreateDefault_MoodDriftDefaults()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(profile.MoodDriftEnabled,
                    Is.EqualTo(EmotionPersonalityTable.Composed.MoodDriftEnabled));
                Assert.That(profile.MoodDriftEnabled, Is.False,
                    "The temperament a profile-less character falls back to is the one with no " +
                    "resting mood, so drift is the author's choice rather than a surprise.");
                Assert.That(profile.MoodDriftRate, Is.EqualTo(0.02f));
                Assert.That(profile.MoodRecoveryRate, Is.EqualTo(0.05f));
                Assert.That(profile.MoodDriftMaxIntensity, Is.EqualTo(0.25f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsMoodDriftRate_ToDeclaredRange()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "moodDriftRate", 10f);
                InvokeOnValidate(profile);
                Assert.That(profile.MoodDriftRate, Is.EqualTo(0.5f));

                SetPrivateField(profile, "moodDriftRate", -5f);
                InvokeOnValidate(profile);
                Assert.That(profile.MoodDriftRate, Is.EqualTo(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsMoodRecoveryRate_ToDeclaredRange()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "moodRecoveryRate", 10f);
                InvokeOnValidate(profile);
                Assert.That(profile.MoodRecoveryRate, Is.EqualTo(1f));

                SetPrivateField(profile, "moodRecoveryRate", -5f);
                InvokeOnValidate(profile);
                Assert.That(profile.MoodRecoveryRate, Is.EqualTo(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsMoodDriftMaxIntensity_ToUnitRange()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "moodDriftMaxIntensity", 5f);
                InvokeOnValidate(profile);
                Assert.That(profile.MoodDriftMaxIntensity, Is.EqualTo(1f));

                SetPrivateField(profile, "moodDriftMaxIntensity", -5f);
                InvokeOnValidate(profile);
                Assert.That(profile.MoodDriftMaxIntensity, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_NaNFallsBackToDefaults()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "moodDriftRate", float.NaN);
                SetPrivateField(profile, "moodRecoveryRate", float.NaN);
                SetPrivateField(profile, "moodDriftMaxIntensity", float.NaN);

                InvokeOnValidate(profile);

                Assert.That(profile.MoodDriftRate, Is.EqualTo(0.02f));
                Assert.That(profile.MoodRecoveryRate, Is.EqualTo(0.05f));
                Assert.That(profile.MoodDriftMaxIntensity, Is.EqualTo(0.25f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
