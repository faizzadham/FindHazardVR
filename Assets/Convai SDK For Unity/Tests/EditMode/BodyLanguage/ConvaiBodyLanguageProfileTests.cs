using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Data-model tests for <see cref="ConvaiBodyLanguageProfile" />: the shipped defaults,
    ///     OnValidate clamping, Idle policy fallback, and the emotion table.
    /// </summary>
    public sealed class ConvaiBodyLanguageProfileTests
    {
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private static void InvokeOnValidate(ConvaiBodyLanguageProfile profile)
        {
            MethodInfo onValidate = typeof(ConvaiBodyLanguageProfile).GetMethod(
                "OnValidate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onValidate, "ConvaiBodyLanguageProfile must declare OnValidate.");
            onValidate.Invoke(profile, null);
        }

        [Test]
        public void CreateDefault_ReturnsProfileWithExpectedPlanDefaults()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                Assert.NotNull(profile, "CreateDefault must return an instance.");
                Assert.That(profile.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));

                Assert.That(profile.StatePolicies.Count, Is.EqualTo(8),
                    "Every dialogue state must be authored in the shipped defaults.");

                BodyLanguageStatePolicy listening = profile.GetPolicy(DialogueState.Listening);
                Assert.IsTrue(listening.ListeningPostureEnabled, "Listening posture must be on while Listening.");
                Assert.That(listening.ListeningLeanIn, Is.EqualTo(0.6f).Within(1e-4f),
                    "Listening lean-in defaults to 60%.");

                BodyLanguageStatePolicy speaking = profile.GetPolicy(DialogueState.Speaking);
                Assert.IsTrue(speaking.GesticulationEnabled, "gesticulation is on while Speaking.");
                Assert.That(speaking.GesticulationIntensity, Is.GreaterThan(0.5f));

                BodyLanguageStatePolicy idle = profile.GetPolicy(DialogueState.Idle);
                Assert.That(idle.BreathRateCpm, Is.EqualTo(13f).Within(0.5f),
                    "Resting breath is ~13 cycles per minute, authored on the Idle state entry — " +
                    "the per-state table is the single source of breath rate and depth.");

                Assert.IsTrue(idle.FidgetsEnabled, "fidgets are on (low) while Idle.");
                Assert.That(idle.FidgetRate, Is.GreaterThan(0f).And.LessThan(0.5f));

                // Signals defaults mirror the analyzer's built-in configuration.
                Assert.That(profile.AttackSeconds, Is.EqualTo(0.05f).Within(1e-4f));
                Assert.That(profile.ReleaseSeconds, Is.EqualTo(0.15f).Within(1e-4f));
                Assert.That(profile.OnsetThresholdAboveBaseline, Is.EqualTo(0.12f).Within(1e-4f));
                Assert.That(profile.RefractorySeconds, Is.EqualTo(0.22f).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void CreateDefault_HasSensibleStanceAndSwayDefaults()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                Assert.IsTrue(profile.EnableWeightShifts, "Weight shifts must be on by default.");
                Assert.That(profile.WeightShiftIntervalSeconds, Is.EqualTo(20f).Within(1e-4f));
                Assert.That(profile.WeightShiftIntervalVarianceSeconds, Is.EqualTo(8f).Within(1e-4f));
                Assert.That(profile.WeightShiftTransferSeconds, Is.EqualTo(2.2f).Within(1e-4f));
                Assert.That(profile.MaxPelvisOffsetCentimeters, Is.EqualTo(3f).Within(1e-4f));
                Assert.That(profile.MaxPelvisObliquityDegrees, Is.EqualTo(2.5f).Within(1e-4f));
                Assert.That(profile.MaxPelvisYawDegrees, Is.EqualTo(3f).Within(1e-4f));
                Assert.IsTrue(profile.EnableLegCompensation, "Leg compensation must be on by default.");
                Assert.IsTrue(profile.EnableAmbientSway, "Ambient sway must be on by default.");
                Assert.That(profile.MaxSwayDegrees, Is.EqualTo(0.6f).Within(1e-4f));

                // AmbientDrift is the per-state PosturalSwayDirector amplitude: non-zero and
                // distinct per state everywhere the body should keep drifting, and exactly zero
                // while Interrupted, where the whole point is that it holds still.
                Assert.That(profile.GetPolicy(DialogueState.Idle).AmbientDrift, Is.GreaterThan(0f));
                Assert.That(profile.GetPolicy(DialogueState.Interrupted).AmbientDrift, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsStanceAndSwayFields()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "weightShiftIntervalSeconds", 999f);
                SetPrivateField(profile, "weightShiftIntervalVarianceSeconds", -5f);
                SetPrivateField(profile, "weightShiftTransferSeconds", 99f);
                SetPrivateField(profile, "maxPelvisOffsetCentimeters", 99f);
                SetPrivateField(profile, "maxPelvisObliquityDegrees", -5f);
                SetPrivateField(profile, "maxPelvisYawDegrees", 99f);
                SetPrivateField(profile, "maxSwayDegrees", 99f);

                InvokeOnValidate(profile);

                Assert.That(profile.WeightShiftIntervalSeconds, Is.InRange(6f, 90f));
                Assert.That(profile.WeightShiftIntervalVarianceSeconds, Is.InRange(0f, 30f));
                Assert.That(profile.WeightShiftTransferSeconds, Is.InRange(0.8f, 5f));
                Assert.That(profile.MaxPelvisOffsetCentimeters, Is.InRange(0f, 6f));
                Assert.That(profile.MaxPelvisObliquityDegrees, Is.InRange(0f, 6f));
                Assert.That(profile.MaxPelvisYawDegrees, Is.InRange(0f, 8f));
                Assert.That(profile.MaxSwayDegrees, Is.InRange(0f, 2f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsOutOfRangeValues()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "attackSeconds", -1f);
                SetPrivateField(profile, "releaseHysteresisFraction", 42f);
                SetPrivateField(profile, "policyTransitionSeconds", 500f);
                SetPrivateField(profile, "maxOpennessDegrees", 720f);

                InvokeOnValidate(profile);

                Assert.That(profile.AttackSeconds, Is.GreaterThan(0f));
                Assert.That(profile.ReleaseHysteresisFraction, Is.InRange(0.05f, 1f));
                Assert.That(profile.PolicyTransitionSeconds, Is.LessThanOrEqualTo(20f));
                Assert.That(profile.MaxOpennessDegrees, Is.LessThanOrEqualTo(30f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsGesticulationFields()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "beatMinIntervalSeconds", 50f);
                SetPrivateField(profile, "beatIntervalVarianceSeconds", -5f);
                SetPrivateField(profile, "beatHeadIntensity", 5f);
                SetPrivateField(profile, "posturePulseAmplitude", -5f);
                SetPrivateField(profile, "posturePulseAttackSeconds", 50f);
                SetPrivateField(profile, "posturePulseDecaySeconds", -5f);
                SetPrivateField(profile, "energyToIntensityGain", 99f);
                SetPrivateField(profile, "statisticalCadenceIntervalSeconds", 999f);
                SetPrivateField(profile, "statisticalCadenceVarianceSeconds", -5f);
                SetPrivateField(profile, "upperBodySuppressionPostureWeight", 5f);
                SetPrivateField(profile, "semanticCueRefractorySeconds", -5f);
                SetPrivateField(profile, "maxShrugDegrees", 50f);
                SetPrivateField(profile, "maxFingerCurlDegrees", 50f);
                SetPrivateField(profile, "maxWristMicroDegrees", 50f);

                InvokeOnValidate(profile);

                Assert.That(profile.BeatMinIntervalSeconds, Is.InRange(0.3f, 3f));
                Assert.That(profile.BeatIntervalVarianceSeconds, Is.InRange(0f, 2f));
                Assert.That(profile.BeatHeadIntensity, Is.InRange(0f, 1f));
                Assert.That(profile.PosturePulseAmplitude, Is.InRange(0f, 1f));
                Assert.That(profile.PosturePulseAttackSeconds, Is.InRange(0.02f, 0.2f));
                Assert.That(profile.PosturePulseDecaySeconds, Is.InRange(0.1f, 1f));
                Assert.That(profile.EnergyToIntensityGain, Is.InRange(0f, 2f));
                Assert.That(profile.StatisticalCadenceIntervalSeconds, Is.InRange(1f, 6f));
                Assert.That(profile.StatisticalCadenceVarianceSeconds, Is.InRange(0f, 3f));
                Assert.That(profile.UpperBodySuppressionPostureWeight, Is.InRange(0f, 1f));
                Assert.That(profile.SemanticCueRefractorySeconds, Is.InRange(0.5f, 10f));
                Assert.That(profile.MaxShrugDegrees, Is.InRange(0f, 10f));
                Assert.That(profile.MaxFingerCurlDegrees, Is.InRange(0f, 6f));
                Assert.That(profile.MaxWristMicroDegrees, Is.InRange(0f, 5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsReactionFields()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "maxFlinchDegrees", 99f);
                SetPrivateField(profile, "maxAmusementBounceDegrees", -5f);

                InvokeOnValidate(profile);

                Assert.That(profile.MaxFlinchDegrees, Is.InRange(0f, 12f));
                Assert.That(profile.MaxAmusementBounceDegrees, Is.InRange(0f, 4f));
                Assert.IsTrue(profile.EnableReactions, "Reactions default on.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_SanitizesAuthoredPolicyAndEmotionRows()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                var policies = new List<BodyLanguageStatePolicy>
                {
                    new()
                    {
                        State = DialogueState.Idle,
                        GesticulationIntensity = 9f,
                        ListeningLeanIn = -2f,
                        PostureOpennessBias = 5f,
                        SagittalLeanBias = -5f,
                        AmbientDrift = 3f,
                        BreathRateCpm = 500f,
                        BreathDepth = 7f,
                        BreathIrregularity = -1f,
                        FidgetRate = 2f
                    }
                };
                SetPrivateField(profile, "statePolicies", policies);

                var modifiers = new List<BodyLanguageEmotionModifier>
                {
                    new() { EmotionLabel = "joy", OpennessBias = 9f, GestureIntensityScale = 99f }
                };
                SetPrivateField(profile, "emotionModifiers", modifiers);

                InvokeOnValidate(profile);

                BodyLanguageStatePolicy idle = profile.GetPolicy(DialogueState.Idle);
                Assert.That(idle.GesticulationIntensity, Is.InRange(0f, 1f));
                Assert.That(idle.ListeningLeanIn, Is.InRange(0f, 1f));
                Assert.That(idle.PostureOpennessBias, Is.InRange(-1f, 1f));
                Assert.That(idle.SagittalLeanBias, Is.InRange(-1f, 1f));
                Assert.That(idle.AmbientDrift, Is.InRange(0f, 1f));
                Assert.That(idle.BreathRateCpm, Is.InRange(4f, 30f));
                Assert.That(idle.BreathDepth, Is.InRange(0f, 1f));
                Assert.That(idle.BreathIrregularity, Is.InRange(0f, 1f));
                Assert.That(idle.FidgetRate, Is.InRange(0f, 1f));

                Assert.That(profile.EmotionModifiers[0].OpennessBias, Is.InRange(-1f, 1f));
                Assert.That(profile.EmotionModifiers[0].GestureIntensityScale, Is.InRange(0f, 2f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void GetPolicy_FallsBackToIdle_ForEveryUnlistedState()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                var idleOnly = new List<BodyLanguageStatePolicy>
                {
                    new()
                    {
                        State = DialogueState.Idle,
                        AmbientDrift = 0.42f,
                        BreathRateCpm = 12.5f,
                        BreathDepth = 0.5f
                    }
                };
                SetPrivateField(profile, "statePolicies", idleOnly);

                foreach (DialogueState state in System.Enum.GetValues(typeof(DialogueState)))
                {
                    BodyLanguageStatePolicy policy = profile.GetPolicy(state);
                    Assert.That(policy.AmbientDrift, Is.EqualTo(0.42f).Within(1e-4f),
                        $"{state} must fall back to the Idle entry when unlisted.");
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void GetPolicy_WithNoIdleEntry_ReturnsConservativeBuiltInFallback()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "statePolicies", new List<BodyLanguageStatePolicy>());

                BodyLanguageStatePolicy policy = profile.GetPolicy(DialogueState.Speaking);
                Assert.That(policy.State, Is.EqualTo(DialogueState.Speaking));
                Assert.IsFalse(policy.GesticulationEnabled, "Built-in fallback must be conservative.");
                Assert.That(policy.BreathRateCpm, Is.InRange(4f, 30f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void DefaultEmotionTable_ContainsBigSixPlusNeutral()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                string[] expected = { "joy", "sadness", "anger", "fear", "surprise", "disgust", "neutral" };
                foreach (string label in expected)
                {
                    bool found = false;
                    for (int i = 0; i < profile.EmotionModifiers.Count; i++)
                    {
                        if (string.Equals(profile.EmotionModifiers[i].EmotionLabel, label,
                                System.StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }
                    Assert.IsTrue(found, $"Default emotion table must contain '{label}'.");
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TryGetEmotionModifier_RespectsTheGate_AndIsCaseInsensitive()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                // The gate is set explicitly in both directions rather than inherited from the
                // shipped default, so this stays a test of the gate whatever that default is.
                SetPrivateField(profile, "enableEmotionModulation", true);
                Assert.IsTrue(profile.TryGetEmotionModifier("JOY", out BodyLanguageEmotionModifier joy),
                    "Lookup must be case-insensitive.");
                Assert.That(joy.OpennessBias, Is.GreaterThan(0f), "Joy opens the chest.");
                Assert.IsFalse(profile.TryGetEmotionModifier("not-an-emotion", out _),
                    "An unauthored label has no modifier, gate or no gate.");

                SetPrivateField(profile, "enableEmotionModulation", false);
                Assert.IsFalse(profile.TryGetEmotionModifier("joy", out _),
                    "Turning modulation off must suppress the whole table, not just part of it.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ResolveExpressiveness_DefaultsToNaturalAnchor()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                Assert.That(profile.ExpressivenessPreset, Is.EqualTo(ExpressivenessPreset.Natural));
                Assert.That(profile.ResolveExpressiveness(), Is.EqualTo(0.5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [TestCase(ExpressivenessPreset.Subtle, 0.25f)]
        [TestCase(ExpressivenessPreset.Expressive, 0.75f)]
        [TestCase(ExpressivenessPreset.Theatrical, 1f)]
        public void ResolveExpressiveness_FixedPresets_ResolveToAnchor(ExpressivenessPreset preset, float expected)
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "expressivenessPreset", preset);
                Assert.That(profile.ResolveExpressiveness(), Is.EqualTo(expected));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ResolveExpressiveness_Custom_UsesCustomExpressivenessValue()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "expressivenessPreset", ExpressivenessPreset.Custom);
                SetPrivateField(profile, "customExpressiveness", 0.82f);
                Assert.That(profile.ResolveExpressiveness(), Is.EqualTo(0.82f).Within(1e-5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void OnValidate_ClampsCustomExpressiveness_ToZeroOneRange()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                SetPrivateField(profile, "customExpressiveness", 5f);
                InvokeOnValidate(profile);
                Assert.That(profile.CustomExpressiveness, Is.EqualTo(1f));

                SetPrivateField(profile, "customExpressiveness", -3f);
                InvokeOnValidate(profile);
                Assert.That(profile.CustomExpressiveness, Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
