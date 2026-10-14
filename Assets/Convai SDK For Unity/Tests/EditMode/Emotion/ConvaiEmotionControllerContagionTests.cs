using System.Reflection;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Profiles;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Controller-level mood-pickup tests: a two-character rig where one
    ///     character (A) is locked to a strong emotion and the other (B, contagion-enabled)
    ///     witnesses it through the registry-backed scan. Covers in-range echo gain capped at
    ///     <see cref="ConvaiEmotionProfile.ContagionMaxIntensity" />, the never-dominant/never-event
    ///     invariants, out-of-range exclusion, the disabled-is-bit-identical guarantee, and
    ///     self-exclusion when no other character is registered.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerContagionTests
    {
        private EmbodimentTestRig _rigA;
        private EmbodimentTestRig _rigB;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harnessA;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harnessB;

        [SetUp]
        public void SetUp()
        {
            EmotionContagionRegistry.Clear();
            _rigA = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerContagionTests) + "_A");
            _rigB = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerContagionTests) + "_B");
            _harnessA = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rigA);
            _harnessB = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rigB);
        }

        [TearDown]
        public void TearDown()
        {
            // A log this fixture did not expect fails the test that produced it. The pin held
            // LogAssert.ignoreFailingMessages for the whole fixture instead, under which these
            // tests could not fail for a logging reason at all.
            LogAssert.NoUnexpectedReceived();
            _rigA?.Dispose();
            _rigB?.Dispose();
            EmotionContagionRegistry.Clear();
        }

        private static ConvaiEmotionProfile CreateContagionProfile(bool enabled, float strength, float radius, float maxIntensity)
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetPrivateField(profile, "contagionEnabled", enabled);
            SetPrivateField(profile, "contagionStrength", strength);
            SetPrivateField(profile, "contagionRadius", radius);
            SetPrivateField(profile, "contagionMaxIntensity", maxIntensity);
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

        private void TickBoth(int steps, float dt)
        {
            for (int i = 0; i < steps; i++)
            {
                _harnessA.Tick(dt);
                _harnessB.Tick(dt);
            }
        }

        [Test]
        public void WitnessInRange_GainsJoyEcho_WithoutBecomingDominant_OrFiringEvent()
        {
            // Arrange
            ConvaiEmotionProfile profileA = ConvaiEmotionProfile.CreateDefault();
            ConvaiEmotionProfile profileB = CreateContagionProfile(true, strength: 0.5f, radius: 4f, maxIntensity: 0.2f);
            try
            {
                _rigA.Root.transform.position = Vector3.zero;
                _rigB.Root.transform.position = new Vector3(1f, 0f, 0f);

                _harnessA.ApplyProfile(profileA);
                _harnessB.ApplyProfile(profileB);

                bool dominantChangedFiredOnB = false;
                _harnessB.Controller.DominantEmotionChanged += (label, score) => dominantChangedFiredOnB = true;

                _harnessA.Controller.LockEmotion("joy", 1f);

                // Act — tick both past several scan intervals (0.25s each).
                TickBoth(120, 0.05f); // 6s simulated

                // Assert
                Assert.That(_harnessA.Controller.Current.DominantLabel, Is.EqualTo("joy"));
                Assert.That(_harnessB.Controller.Current.DominantLabel, Is.EqualTo("neutral"),
                    "The witness's own resolved emotion must stay neutral — the echo is a render fold only.");

                float joyEcho = _harnessB.Controller.Current.GetScore("joy");
                Assert.That(joyEcho, Is.GreaterThan(0f), "B must gain a nonzero joy echo from witnessing A.");
                Assert.That(joyEcho, Is.LessThanOrEqualTo(0.2f + 1e-3f),
                    "The echo must never exceed Contagion Max Intensity.");
                Assert.That(dominantChangedFiredOnB, Is.False,
                    "The echo must never fire DominantEmotionChanged on the witness.");
            }
            finally
            {
                Object.DestroyImmediate(profileA);
                Object.DestroyImmediate(profileB);
            }
        }

        [Test]
        public void WitnessOutOfRange_GainsNoEcho()
        {
            ConvaiEmotionProfile profileA = ConvaiEmotionProfile.CreateDefault();
            ConvaiEmotionProfile profileB = CreateContagionProfile(true, strength: 0.5f, radius: 2f, maxIntensity: 0.2f);
            try
            {
                _rigA.Root.transform.position = Vector3.zero;
                _rigB.Root.transform.position = new Vector3(50f, 0f, 0f); // far outside the 2m radius

                _harnessA.ApplyProfile(profileA);
                _harnessB.ApplyProfile(profileB);

                _harnessA.Controller.LockEmotion("joy", 1f);

                TickBoth(120, 0.05f);

                Assert.That(_harnessB.Controller.Current.GetScore("joy"), Is.EqualTo(0f),
                    "A witness beyond Contagion Radius must gain no echo.");
            }
            finally
            {
                Object.DestroyImmediate(profileA);
                Object.DestroyImmediate(profileB);
            }
        }

        [Test]
        public void ContagionDisabled_IsBitIdentical_ToNoWitnessAtAll()
        {
            ConvaiEmotionProfile profileA = ConvaiEmotionProfile.CreateDefault();
            ConvaiEmotionProfile profileBDisabled = CreateContagionProfile(false, strength: 0.5f, radius: 4f, maxIntensity: 0.2f);
            try
            {
                _rigA.Root.transform.position = Vector3.zero;
                _rigB.Root.transform.position = new Vector3(1f, 0f, 0f);

                _harnessA.ApplyProfile(profileA);
                _harnessB.ApplyProfile(profileBDisabled);

                _harnessA.Controller.LockEmotion("joy", 1f);

                TickBoth(120, 0.05f);

                Assert.That(_harnessB.Controller.Current.GetScore("joy"), Is.EqualTo(0f),
                    "Contagion Enabled = false must be bit-identical to the feature never existing.");
            }
            finally
            {
                Object.DestroyImmediate(profileA);
                Object.DestroyImmediate(profileBDisabled);
            }
        }

        [Test]
        public void NoOtherCharacterInScene_SelfExclusion_SetsNoContagionTarget()
        {
            ConvaiEmotionProfile profileB = CreateContagionProfile(true, strength: 0.5f, radius: 4f, maxIntensity: 0.2f);
            try
            {
                // Only B remains registered — self-exclusion must leave the echo target empty
                // rather than a lone character echoing its own emotion back onto itself.
                _rigA.Dispose();
                _rigA = null;

                _harnessB.ApplyProfile(profileB);
                _harnessB.Controller.LockEmotion("joy", 0.9f);

                for (int i = 0; i < 120; i++)
                    _harnessB.Tick(0.05f);

                object accumulator = GetPrivateField(_harnessB.Controller, "_accumulator");
                Assert.IsNotNull(accumulator);

                object echoTargetLabel = GetPrivateField(accumulator, "_echoTargetLabel");
                var echoTargetIntensity = (float)GetPrivateField(accumulator, "_echoTargetIntensity");

                Assert.That(echoTargetLabel, Is.Null,
                    "A lone registered character must never become its own contagion candidate.");
                Assert.That(echoTargetIntensity, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profileB);
            }
        }
    }
}
