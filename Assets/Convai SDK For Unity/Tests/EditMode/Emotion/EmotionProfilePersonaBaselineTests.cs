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
    ///     Persona-baseline data-model tests for <see cref="ConvaiEmotionProfile" /> and
    ///     <see cref="EmotionExpressivenessEntry" />: defaults, <c>OnValidate</c> clamps, and
    ///     <see cref="ConvaiEmotionProfile.GetExpressivenessGain" />.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfilePersonaBaselineTests
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
        ///     A character whose personality nobody authored must rest on something quiet, and on
        ///     nothing louder.
        /// </summary>
        /// <remarks>
        ///     This used to assert an empty baseline, on the reasoning that arriving with a mood the
        ///     author never chose is what makes an unconfigured character look wrong. The opposite
        ///     turned out to be true in front of a face: an absolutely still resting expression
        ///     reads as broken, not as neutral. <c>trust</c> is the quietest thing that reads as
        ///     alive — closed-lip civility at roughly fourteen units of smile, against Warm's
        ///     thirty-eight — so the point of the original assertion is kept by bounding it rather
        ///     than by zeroing it.
        /// </remarks>
        [Test]
        public void CreateDefault_RestsQuietly()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(profile.BaselineEmotionLabel, Is.EqualTo("trust"),
                    "An unconfigured character rests civil, never cheerful.");
                Assert.That(profile.BaselineIntensity, Is.EqualTo(0.45f).Within(0.0001f));
                Assert.That(profile.Expressiveness, Is.Empty);
                Assert.That(profile.GetExpressivenessGain("joy"), Is.EqualTo(1f),
                    "Absent labels must default to gain 1 (unchanged).");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     Composed rests on civility, and on nothing more than civility.
        /// </summary>
        /// <remarks>
        ///     The upper bound is the assertion that matters. Composed is the conversational
        ///     default — what a character gets when nobody authored a personality — so a resting
        ///     mood strong enough to read as a chosen emotion here would put a face on every
        ///     unconfigured character in every project. <c>trust</c> at 0.45 renders about fourteen
        ///     units of smile against Warm's thirty-eight: present, never cheerful.
        /// </remarks>
        [Test]
        public void ComposedPreset_RestsOnCivilityAtMost()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Composed, null);
            try
            {
                Assert.That(profile.BaselineEmotionLabel, Is.EqualTo("trust"),
                    "Composed rests on trust — a closed-lip pleasantness, not a smile.");
                Assert.That(profile.BaselineIntensity, Is.EqualTo(0.45f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsBaselineIntensity()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "baselineIntensity", 5f);
                InvokeOnValidate(profile);
                Assert.That(profile.BaselineIntensity, Is.EqualTo(1f));

                SetPrivateField(profile, "baselineIntensity", -2f);
                InvokeOnValidate(profile);
                Assert.That(profile.BaselineIntensity, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsExpressivenessGain()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                var entries = new List<EmotionExpressivenessEntry>
                {
                    new("joy", 10f),
                    new("anger", -3f)
                };
                SetPrivateField(profile, "expressiveness", entries);

                InvokeOnValidate(profile);

                Assert.That(profile.GetExpressivenessGain("joy"), Is.EqualTo(2f));
                Assert.That(profile.GetExpressivenessGain("anger"), Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void GetExpressivenessGain_IsCaseInsensitive()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                var entries = new List<EmotionExpressivenessEntry> { new("Joy", 1.5f) };
                SetPrivateField(profile, "expressiveness", entries);

                Assert.That(profile.GetExpressivenessGain("joy"), Is.EqualTo(1.5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
