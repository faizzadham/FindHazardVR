using System.Reflection;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Data-model tests for <see cref="ConvaiEmotionProfile" />'s
    ///     <c>thinkingReactionStrength</c>/<c>reactingAccentStrength</c>/
    ///     <c>interruptedFlinchStrength</c> fields: default, <c>OnValidate</c> clamp01, and NaN
    ///     fallback. Per-temperament values live alongside the other demeanor preset checks in
    ///     <see cref="EmotionProfileDemeanorPresetTests" />.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfileBeatReactionTests
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
        ///     The beat reactions are on, and quiet, for a character nobody configured.
        /// </summary>
        /// <remarks>
        ///     They shipped off while the module was pre-release, so that adding them could not
        ///     change anyone's output. A released module cannot hide its own behaviour behind a
        ///     zero a first-time user would never find, so <c>CreateDefault</c> now installs the
        ///     Composed temperament whole. The expected values are read from that table rather than
        ///     written out again here: one number in one place is what stops the shipped default
        ///     and the test's idea of it drifting apart, which is exactly how this test went stale.
        /// </remarks>
        [Test]
        public void CreateDefault_ShipsTheComposedBeatReactions()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(profile.ThinkingReactionStrength,
                    Is.EqualTo(EmotionPersonalityTable.Composed.ThinkingReactionStrength).Within(0.0001f));
                Assert.That(profile.ReactingAccentStrength,
                    Is.EqualTo(EmotionPersonalityTable.Composed.ReactingAccentStrength).Within(0.0001f));
                Assert.That(profile.InterruptedFlinchStrength,
                    Is.EqualTo(EmotionPersonalityTable.Composed.InterruptedFlinchStrength).Within(0.0001f));

                Assert.That(profile.ThinkingReactionStrength, Is.GreaterThan(0f),
                    "A character nobody configured still reacts to the conversation — a default of " +
                    "zero is the module shipping switched off.");
                Assert.That(profile.ReactingAccentStrength, Is.GreaterThan(0f));
                Assert.That(profile.InterruptedFlinchStrength, Is.GreaterThan(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [TestCase("thinkingReactionStrength")]
        [TestCase("reactingAccentStrength")]
        [TestCase("interruptedFlinchStrength")]
        public void OnValidate_ClampsFieldToUnitRange(string fieldName)
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, fieldName, 5f);
                InvokeOnValidate(profile);
                Assert.That(GetPublicAccessorValue(profile, fieldName), Is.EqualTo(1f));

                SetPrivateField(profile, fieldName, -5f);
                InvokeOnValidate(profile);
                Assert.That(GetPublicAccessorValue(profile, fieldName), Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [TestCase("thinkingReactionStrength")]
        [TestCase("reactingAccentStrength")]
        [TestCase("interruptedFlinchStrength")]
        public void OnValidate_NaNFallsBackToZero(string fieldName)
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, fieldName, float.NaN);

                InvokeOnValidate(profile);

                Assert.That(GetPublicAccessorValue(profile, fieldName), Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static float GetPublicAccessorValue(ConvaiEmotionProfile profile, string fieldName)
        {
            string propertyName = fieldName switch
            {
                "thinkingReactionStrength" => nameof(ConvaiEmotionProfile.ThinkingReactionStrength),
                "reactingAccentStrength" => nameof(ConvaiEmotionProfile.ReactingAccentStrength),
                "interruptedFlinchStrength" => nameof(ConvaiEmotionProfile.InterruptedFlinchStrength),
                _ => null
            };
            Assert.NotNull(propertyName, $"No mapped public accessor for field {fieldName}.");

            PropertyInfo property = typeof(ConvaiEmotionProfile).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(property, $"Missing public accessor {propertyName}.");
            return (float)property.GetValue(profile);
        }
    }
}
