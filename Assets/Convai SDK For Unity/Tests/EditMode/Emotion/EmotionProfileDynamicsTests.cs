using Convai.Domain.Embodiment.Semantics;
using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Per-emotion temporal-dynamics data-model tests for
    ///     <see cref="ConvaiEmotionProfile" /> and <see cref="EmotionDynamicsEntry" />: defaults,
    ///     <c>OnValidate</c> clamps, and <see cref="ConvaiEmotionProfile.TryGetDynamics" />.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfileDynamicsTests
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
        public void CreateDefault_EmotionDynamicsIsEmpty()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(profile.EmotionDynamics, Is.Empty);
                Assert.That(profile.TryGetDynamics("joy", out float attack, out float decay), Is.False);
                Assert.That(attack, Is.EqualTo(0f));
                Assert.That(decay, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ComposedPreset_EmotionDynamicsIsEmpty()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Composed, null);
            try
            {
                Assert.That(profile.EmotionDynamics, Is.Empty,
                    "Per-emotion dynamics must not change the conversational preset's default (no overrides).");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TryGetDynamics_ReturnsAuthoredEntry()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                var entries = new List<EmotionDynamicsEntry> { new("anger", 12f, 8f) };
                SetPrivateField(profile, "emotionDynamics", entries);

                Assert.That(profile.TryGetDynamics("anger", out float attack, out float decay), Is.True);
                Assert.That(attack, Is.EqualTo(12f));
                Assert.That(decay, Is.EqualTo(8f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TryGetDynamics_IsCaseInsensitive()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                var entries = new List<EmotionDynamicsEntry> { new("Sadness", 1f, 0.5f) };
                SetPrivateField(profile, "emotionDynamics", entries);

                Assert.That(profile.TryGetDynamics("sadness", out float attack, out float decay), Is.True);
                Assert.That(attack, Is.EqualTo(1f));
                Assert.That(decay, Is.EqualTo(0.5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TryGetDynamics_UnknownLabel_ReturnsFalse()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                var entries = new List<EmotionDynamicsEntry> { new("anger", 12f, 8f) };
                SetPrivateField(profile, "emotionDynamics", entries);

                Assert.That(profile.TryGetDynamics("joy", out float attack, out float decay), Is.False);
                Assert.That(attack, Is.EqualTo(0f));
                Assert.That(decay, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsEmotionDynamicsSpeeds()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                var entries = new List<EmotionDynamicsEntry>
                {
                    new("anger", 100f, -3f),
                    new("sadness", float.NaN, float.NaN)
                };
                SetPrivateField(profile, "emotionDynamics", entries);

                InvokeOnValidate(profile);

                Assert.That(profile.TryGetDynamics("anger", out float angerAttack, out float angerDecay), Is.True);
                Assert.That(angerAttack, Is.EqualTo(20f), "Attack speed must clamp to the declared Range upper bound.");
                Assert.That(angerDecay, Is.EqualTo(0.1f));

                Assert.That(profile.TryGetDynamics("sadness", out float sadAttack, out float sadDecay), Is.True);
                Assert.That(sadAttack, Is.EqualTo(5f), "NaN attack speed must be guarded to the entry default.");
                Assert.That(sadDecay, Is.EqualTo(2f), "NaN decay speed must be guarded to the entry default.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
