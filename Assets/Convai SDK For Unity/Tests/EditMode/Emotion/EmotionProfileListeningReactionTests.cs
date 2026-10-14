using System.Reflection;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Data-model tests for <see cref="ConvaiEmotionProfile" />'s
    ///     <c>listeningReactionStrength</c> field: default, <c>OnValidate</c> clamp01, and NaN
    ///     fallback. Per-temperament values live alongside the other demeanor preset checks in
    ///     <see cref="EmotionProfileDemeanorPresetTests" />.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfileListeningReactionTests
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
        ///     A character nobody configured still reacts while the player speaks.
        /// </summary>
        /// <remarks>
        ///     Shipped off while the module was pre-release so the feature could not change
        ///     anyone's output; a released module does not hide its behaviour behind a zero the
        ///     user would have to find. Read from the Composed table rather than restated, so the
        ///     shipped default and the test cannot drift apart.
        /// </remarks>
        [Test]
        public void CreateDefault_ShipsTheComposedListeningReaction()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(profile.ListeningReactionStrength,
                    Is.EqualTo(EmotionPersonalityTable.Composed.ListeningReactionStrength).Within(0.0001f));
                Assert.That(profile.ListeningReactionStrength, Is.GreaterThan(0f),
                    "A default of zero is the listening reaction shipping switched off.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsListeningReactionStrength_ToUnitRange()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "listeningReactionStrength", 5f);
                InvokeOnValidate(profile);
                Assert.That(profile.ListeningReactionStrength, Is.EqualTo(1f));

                SetPrivateField(profile, "listeningReactionStrength", -5f);
                InvokeOnValidate(profile);
                Assert.That(profile.ListeningReactionStrength, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_NaNFallsBackToZero()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "listeningReactionStrength", float.NaN);

                InvokeOnValidate(profile);

                Assert.That(profile.ListeningReactionStrength, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
