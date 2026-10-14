using Convai.Modules.BodyAnimation.Core.Policy;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="PersonaScalars" />: every mapping
    ///     must be neutral (identity) at the sliders' default value of 1, and must move
    ///     monotonically at 0.5 and 2 exactly per the documented formulas.
    /// </summary>
    public sealed class PersonaScalarsTests
    {
        private ConvaiBodyAnimationConfig _config;

        [TearDown]
        public void TearDown()
        {
            if (_config != null) Object.DestroyImmediate(_config);
            _config = null;
        }

        private ConvaiBodyAnimationConfig CreateConfig(float liveliness, float calmness)
        {
            _config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(_config);
            serialized.FindProperty("_gestureLiveliness").floatValue = liveliness;
            serialized.FindProperty("_calmness").floatValue = calmness;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return _config;
        }

        [Test]
        public void NeutralConfig_EveryMappingIsIdentity()
        {
            ConvaiBodyAnimationConfig config = CreateConfig(1f, 1f);

            Assert.AreEqual(1f, PersonaScalars.ResolveGestureWeightScale(config), 1e-6f);
            Assert.AreEqual(1f, PersonaScalars.ResolveVariantSwitchProbability(config), 1e-6f);
            Assert.AreEqual(0.8f, PersonaScalars.ResolveBeatRefractorySeconds(config, 0.8f), 1e-6f);
            Assert.AreEqual(1f, PersonaScalars.ResolveIdleIntervalScale(config), 1e-6f);
            Assert.AreEqual(1f, PersonaScalars.ResolveTalkFadeInScale(config), 1e-6f);
        }

        [Test]
        public void GestureWeightScale_PinnedAtHalfAndDouble()
        {
            Assert.AreEqual(0.5f, PersonaScalars.ResolveGestureWeightScale(CreateConfig(0.5f, 1f)), 1e-6f);
            Assert.AreEqual(2f, PersonaScalars.ResolveGestureWeightScale(CreateConfig(2f, 1f)), 1e-6f);
        }

        [Test]
        public void VariantSwitchProbability_ClampedToOne_AboveLivelinessOne()
        {
            Assert.AreEqual(0.5f, PersonaScalars.ResolveVariantSwitchProbability(CreateConfig(0.5f, 1f)), 1e-6f);
            // Liveliness > 1 cannot switch "more than every wrap" — probability clamps at 1.
            Assert.AreEqual(1f, PersonaScalars.ResolveVariantSwitchProbability(CreateConfig(2f, 1f)), 1e-6f);
        }

        [Test]
        public void BeatRefractorySeconds_ScalesInverselyWithLiveliness()
        {
            const float baseSeconds = 1.2f;

            // liveliness = 0.5 -> refractory / 0.5 = double the wait (calmer beat rate).
            Assert.AreEqual(baseSeconds / 0.5f, PersonaScalars.ResolveBeatRefractorySeconds(CreateConfig(0.5f, 1f), baseSeconds), 1e-6f);

            // liveliness = 2 -> refractory / 2 = half the wait (faster beat rate).
            Assert.AreEqual(baseSeconds / 2f, PersonaScalars.ResolveBeatRefractorySeconds(CreateConfig(2f, 1f), baseSeconds), 1e-6f);
        }

        [Test]
        public void BeatRefractorySeconds_LivelinessZero_ClampsToFloorDivisor()
        {
            const float baseSeconds = 1.2f;

            // A zero-liveliness config must not divide by zero: the divisor floors at 0.25.
            Assert.AreEqual(baseSeconds / 0.25f, PersonaScalars.ResolveBeatRefractorySeconds(CreateConfig(0f, 1f), baseSeconds), 1e-6f);
        }

        [Test]
        public void IdleIntervalScale_PinnedAtHalfAndDouble()
        {
            Assert.AreEqual(0.5f, PersonaScalars.ResolveIdleIntervalScale(CreateConfig(1f, 0.5f)), 1e-6f);
            Assert.AreEqual(2f, PersonaScalars.ResolveIdleIntervalScale(CreateConfig(1f, 2f)), 1e-6f);
        }

        [Test]
        public void TalkFadeInScale_PinnedAtHalfAndDouble()
        {
            // 1 + 0.25 * (0.5 - 1) = 0.875
            Assert.AreEqual(0.875f, PersonaScalars.ResolveTalkFadeInScale(CreateConfig(1f, 0.5f)), 1e-6f);
            // 1 + 0.25 * (2 - 1) = 1.25
            Assert.AreEqual(1.25f, PersonaScalars.ResolveTalkFadeInScale(CreateConfig(1f, 2f)), 1e-6f);
        }
    }
}
