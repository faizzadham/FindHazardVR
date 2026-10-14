using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Data-model tests for <see cref="ConvaiEmotionProfile" />'s mood-pickup
    ///     block: defaults, <c>OnValidate</c> clamps/NaN fallbacks, and the Warm/Expressive
    ///     demeanor factory values.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfileContagionTests
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

        [Test]
        public void CreateDefault_ContagionDefaults()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(profile.ContagionEnabled, Is.False);
                Assert.That(profile.ContagionStrength, Is.EqualTo(0.3f));
                Assert.That(profile.ContagionRadius, Is.EqualTo(4f));
                Assert.That(profile.ContagionMaxIntensity, Is.EqualTo(0.2f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsContagionStrength_ToUnitRange()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "contagionStrength", 5f);
                InvokeOnValidate(profile);
                Assert.That(profile.ContagionStrength, Is.EqualTo(1f));

                SetPrivateField(profile, "contagionStrength", -5f);
                InvokeOnValidate(profile);
                Assert.That(profile.ContagionStrength, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsContagionRadius_ToDeclaredRange()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "contagionRadius", 100f);
                InvokeOnValidate(profile);
                Assert.That(profile.ContagionRadius, Is.EqualTo(20f));

                SetPrivateField(profile, "contagionRadius", -5f);
                InvokeOnValidate(profile);
                Assert.That(profile.ContagionRadius, Is.EqualTo(0.5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsContagionMaxIntensity_ToUnitRange()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "contagionMaxIntensity", 5f);
                InvokeOnValidate(profile);
                Assert.That(profile.ContagionMaxIntensity, Is.EqualTo(1f));

                SetPrivateField(profile, "contagionMaxIntensity", -5f);
                InvokeOnValidate(profile);
                Assert.That(profile.ContagionMaxIntensity, Is.EqualTo(0f));
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
                SetPrivateField(profile, "contagionStrength", float.NaN);
                SetPrivateField(profile, "contagionRadius", float.NaN);
                SetPrivateField(profile, "contagionMaxIntensity", float.NaN);

                InvokeOnValidate(profile);

                Assert.That(profile.ContagionStrength, Is.EqualTo(0.3f));
                Assert.That(profile.ContagionRadius, Is.EqualTo(4f));
                Assert.That(profile.ContagionMaxIntensity, Is.EqualTo(0.2f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WarmPreset_EnablesContagion_AtExpectedStrength()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Warm, null);
            try
            {
                Assert.That(profile.ContagionEnabled, Is.True);
                Assert.That(profile.ContagionStrength, Is.EqualTo(0.3f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void EnergeticPreset_EnablesContagion_AtExpectedStrength()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Energetic, null);
            try
            {
                Assert.That(profile.ContagionEnabled, Is.True);
                Assert.That(profile.ContagionStrength, Is.EqualTo(0.45f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void NeutralAndReservedPresets_LeaveContagionDisabled()
        {
            ConvaiEmotionProfile neutral = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Composed, null);
            ConvaiEmotionProfile reserved = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Reserved, null);
            try
            {
                Assert.That(neutral.ContagionEnabled, Is.False);
                Assert.That(reserved.ContagionEnabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(neutral);
                Object.DestroyImmediate(reserved);
            }
        }
    }
}
