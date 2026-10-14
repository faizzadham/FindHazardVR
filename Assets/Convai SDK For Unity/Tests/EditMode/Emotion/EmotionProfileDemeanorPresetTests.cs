using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Emotion.Profiles;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Covers <see cref="ConvaiEmotionProfile.CreatePreset" /> across all four temperaments:
    ///     the values that tell them apart, <c>OnValidate</c> cleanliness (its clamps must not clip
    ///     what a temperament deliberately authored), and cross-temperament sanity.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfileDemeanorPresetTests
    {
        private static void InvokeOnValidate(ConvaiEmotionProfile profile)
        {
            MethodInfo onValidate = typeof(ConvaiEmotionProfile).GetMethod(
                "OnValidate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onValidate, "ConvaiEmotionProfile must declare OnValidate.");
            onValidate.Invoke(profile, null);
        }

        /// <summary>
        ///     Builds a taxonomy asset containing only <paramref name="labels" /> (via reflection
        ///     on the asset's private <c>entries</c> field, since the field has no public setter).
        ///     Used to exercise the sparse-taxonomy skip path in the demeanor preset factories.
        /// </summary>
        private static EmotionTaxonomyAsset CreateSparseTaxonomy(params (string label, bool isNeutral)[] labels)
        {
            EmotionTaxonomyAsset taxonomy = ScriptableObject.CreateInstance<EmotionTaxonomyAsset>();
            taxonomy.hideFlags = HideFlags.HideAndDontSave;

            var entries = new List<EmotionTaxonomyEntry>();
            foreach ((string label, bool isNeutral) in labels)
                entries.Add(new EmotionTaxonomyEntry(label, null, null, 0.5f, isNeutral));

            FieldInfo entriesField = typeof(EmotionTaxonomyAsset).GetField(
                "entries", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(entriesField, "EmotionTaxonomyAsset must declare a private 'entries' field.");
            entriesField.SetValue(taxonomy, entries);

            return taxonomy;
        }

        [Test]
        public void ComposedPreset_RestsWithoutAMood_AndIsWhatCreateDefaultBuilds()
        {
            // Composed is the temperament CreateDefault installs, so a character whose personality
            // nobody authored gets exactly this. It rests on a faint trust rather than on nothing:
            // an unconfigured character used to arrive with an absolutely still face, which reads as
            // broken rather than as neutral. Trust is the quietest thing that reads as alive —
            // closed-lip civility, about fourteen units of smile against Warm's thirty-eight — so
            // this is still not a mood an author would notice as a choice made for them.
            ConvaiEmotionProfile composed = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Composed, null);
            ConvaiEmotionProfile fallback = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(composed.BaselineEmotionLabel, Is.EqualTo("trust"));
                Assert.That(composed.BaselineIntensity, Is.EqualTo(0.45f).Within(0.0001f),
                    "Composed is the civil character type — present at rest, never cheerful.");
                Assert.That(composed.Expressiveness, Is.Empty,
                    "Composed shows every emotion as it arrives, with no per-emotion bias.");
                Assert.That(composed.EmotionDynamics, Is.Empty);

                // Composed is calm, not inert. The small-movement layer and the beat reactions are
                // on but quiet, so the character does not read as a frozen mask while it rests.
                Assert.That(composed.MicroExpressionsEnabled, Is.True);
                Assert.That(composed.EnableEmotionBlending, Is.True);
                Assert.That(composed.ListeningReactionStrength, Is.EqualTo(0.18f).Within(0.0001f));
                Assert.That(composed.ThinkingReactionStrength, Is.EqualTo(0.15f).Within(0.0001f));
                Assert.That(composed.ReactingAccentStrength, Is.EqualTo(0.18f).Within(0.0001f));
                Assert.That(composed.InterruptedFlinchStrength, Is.EqualTo(0.2f).Within(0.0001f));
                Assert.That(composed.ProsodyCoupling, Is.EqualTo(0.15f).Within(0.0001f));

                Assert.That(fallback.BaselineEmotionLabel, Is.EqualTo(composed.BaselineEmotionLabel));
                Assert.That(fallback.BaselineIntensity, Is.EqualTo(composed.BaselineIntensity));
                Assert.That(fallback.LerpSpeed, Is.EqualTo(composed.LerpSpeed));
                Assert.That(fallback.DecaySpeed, Is.EqualTo(composed.DecaySpeed));
                Assert.That(fallback.ProsodyCoupling, Is.EqualTo(composed.ProsodyCoupling));
            }
            finally
            {
                Object.DestroyImmediate(composed);
                Object.DestroyImmediate(fallback);
            }
        }

        [Test]
        public void WarmPreset_HasDistinguishingValues()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Warm, null);
            try
            {
                Assert.That(profile.BaselineEmotionLabel, Is.EqualTo("joy"));
                Assert.That(profile.BaselineIntensity, Is.EqualTo(0.55f).Within(0.0001f));
                Assert.That(profile.GetExpressivenessGain("joy"), Is.GreaterThan(1f));
                Assert.That(profile.GetExpressivenessGain("trust"), Is.GreaterThan(1f));
                Assert.That(profile.GetExpressivenessGain("sadness"), Is.LessThan(1f));
                Assert.That(profile.EnableEmotionBlending, Is.True);
                Assert.That(profile.MicroExpressionsEnabled, Is.True);
                Assert.That(profile.MicroExpressionAmplitude, Is.EqualTo(0.18f).Within(0.0001f));
                Assert.That(profile.ListeningReactionStrength, Is.EqualTo(0.35f).Within(0.0001f));
                Assert.That(profile.ThinkingReactionStrength, Is.EqualTo(0.3f).Within(0.0001f));
                Assert.That(profile.ReactingAccentStrength, Is.EqualTo(0.3f).Within(0.0001f));
                Assert.That(profile.InterruptedFlinchStrength, Is.EqualTo(0.3f).Within(0.0001f));

                Assert.That(profile.TryGetDynamics("joy", out float joyAttack, out _), Is.True);
                Assert.That(joyAttack, Is.EqualTo(6f).Within(0.0001f));
                Assert.That(profile.TryGetDynamics("sadness", out _, out float sadDecay), Is.True);
                Assert.That(sadDecay, Is.EqualTo(1.2f).Within(0.0001f));
                Assert.That(profile.ProsodyCoupling, Is.EqualTo(0.3f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WarmPreset_SparseTaxonomy_SkipsAbsentLabelEntries()
        {
            EmotionTaxonomyAsset sparseTaxonomy = CreateSparseTaxonomy(
                ("neutral", true), ("joy", false));
            ConvaiEmotionProfile profile = null;
            try
            {
                profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Warm, sparseTaxonomy);

                // "joy" exists in the sparse taxonomy: kept.
                Assert.That(profile.GetExpressivenessGain("joy"), Is.GreaterThan(1f));
                Assert.That(profile.TryGetDynamics("joy", out _, out _), Is.True);

                // "trust"/"sadness"/"anger" are absent from the sparse taxonomy: skipped.
                foreach (string absentLabel in new[] { "trust", "sadness", "anger" })
                {
                    Assert.That(profile.GetExpressivenessGain(absentLabel), Is.EqualTo(1f),
                        $"Expected no expressiveness entry for absent label '{absentLabel}' (default gain 1).");
                    Assert.That(profile.TryGetDynamics(absentLabel, out _, out _), Is.False,
                        $"Expected no dynamics entry for absent label '{absentLabel}'.");
                }

                foreach (EmotionExpressivenessEntry entry in profile.Expressiveness)
                    Assert.That(entry.Label, Is.EqualTo("joy"),
                        "Only labels present in the sparse taxonomy should be added.");

                foreach (EmotionDynamicsEntry entry in profile.EmotionDynamics)
                    Assert.That(entry.Label, Is.EqualTo("joy"),
                        "Only labels present in the sparse taxonomy should be added.");
            }
            finally
            {
                if (profile != null) Object.DestroyImmediate(profile);
                Object.DestroyImmediate(sparseTaxonomy);
            }
        }

        [Test]
        public void ReservedPreset_HasDistinguishingValues()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Reserved, null);
            try
            {
                Assert.That(profile.BaselineIntensity, Is.EqualTo(0f), "Reserved preset must not enable a persona baseline.");
                Assert.That(profile.GetExpressivenessGain("joy"), Is.EqualTo(0.7f).Within(0.0001f));
                Assert.That(profile.GetExpressivenessGain("anger"), Is.EqualTo(0.7f).Within(0.0001f));
                Assert.That(profile.MicroExpressionsEnabled, Is.True);
                Assert.That(profile.MicroExpressionAmplitude, Is.LessThan(0.1f));
                Assert.That(profile.EnableEmotionBlending, Is.True);
                Assert.That(profile.EmotionSwitchDwell, Is.EqualTo(0.7f).Within(0.0001f));
                Assert.That(profile.MicroExpressionStillness, Is.EqualTo(0.8f).Within(0.0001f));

                // Reserved is understated, not switched off: every reaction is present at a fraction
                // of Warm's strength. A character that shows literally nothing reads as broken.
                Assert.That(profile.ListeningReactionStrength, Is.EqualTo(0.12f).Within(0.0001f));
                Assert.That(profile.ThinkingReactionStrength, Is.EqualTo(0.22f).Within(0.0001f));
                Assert.That(profile.ReactingAccentStrength, Is.EqualTo(0.12f).Within(0.0001f));
                Assert.That(profile.InterruptedFlinchStrength, Is.EqualTo(0.16f).Within(0.0001f));
                Assert.That(profile.ProsodyCoupling, Is.EqualTo(0.08f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void EnergeticPreset_HasDistinguishingValues()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Energetic, null);
            try
            {
                Assert.That(profile.GetExpressivenessGain("joy"), Is.GreaterThan(1.4f));
                Assert.That(profile.GetExpressivenessGain("surprise"), Is.GreaterThan(1.4f));
                Assert.That(profile.GetExpressivenessGain("anger"), Is.GreaterThan(1f));
                Assert.That(profile.MicroExpressionsEnabled, Is.True);
                Assert.That(profile.MicroExpressionAmplitude, Is.GreaterThan(0.2f));
                Assert.That(profile.LerpSpeed, Is.EqualTo(6f).Within(0.0001f));
                Assert.That(profile.ListeningReactionStrength, Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(profile.ThinkingReactionStrength, Is.EqualTo(0.45f).Within(0.0001f));
                Assert.That(profile.ReactingAccentStrength, Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(profile.InterruptedFlinchStrength, Is.EqualTo(0.5f).Within(0.0001f));

                Assert.That(profile.TryGetDynamics("anger", out float angerAttack, out _), Is.True);
                Assert.That(angerAttack, Is.EqualTo(8f).Within(0.0001f));
                Assert.That(profile.TryGetDynamics("surprise", out float surpriseAttack, out _), Is.True);
                Assert.That(surpriseAttack, Is.EqualTo(8f).Within(0.0001f));
                Assert.That(profile.ProsodyCoupling, Is.EqualTo(0.45f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [TestCase(CharacterDemeanor.Composed)]
        [TestCase(CharacterDemeanor.Warm)]
        [TestCase(CharacterDemeanor.Energetic)]
        [TestCase(CharacterDemeanor.Reserved)]
        public void EveryTemperament_PassesOnValidateWithoutChangingIntendedValues(CharacterDemeanor demeanor)
        {
            // Was driven by reflection over four separate factory method names. The temperament is a
            // parameter now, so the test can name what it is checking and a missing case is a
            // compile error rather than a silently-skipped reflection lookup.
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(demeanor, null);
            try
            {
                float baselineIntensityBefore = profile.BaselineIntensity;
                bool blendingBefore = profile.EnableEmotionBlending;
                float dwellBefore = profile.EmotionSwitchDwell;
                float marginBefore = profile.EmotionSwitchMargin;
                float complementBefore = profile.ComplementBlendScale;
                bool microBefore = profile.MicroExpressionsEnabled;
                float amplitudeBefore = profile.MicroExpressionAmplitude;
                float accentBefore = profile.SpeechAccentStrength;
                float stillnessBefore = profile.MicroExpressionStillness;
                float listeningBefore = profile.ListeningReactionStrength;
                float thinkingBefore = profile.ThinkingReactionStrength;
                float reactingBefore = profile.ReactingAccentStrength;
                float interruptedBefore = profile.InterruptedFlinchStrength;
                float prosodyCouplingBefore = profile.ProsodyCoupling;

                InvokeOnValidate(profile);

                Assert.That(profile.BaselineIntensity, Is.EqualTo(baselineIntensityBefore).Within(0.0001f));
                Assert.That(profile.EnableEmotionBlending, Is.EqualTo(blendingBefore));
                Assert.That(profile.EmotionSwitchDwell, Is.EqualTo(dwellBefore).Within(0.0001f));
                Assert.That(profile.EmotionSwitchMargin, Is.EqualTo(marginBefore).Within(0.0001f));
                Assert.That(profile.ComplementBlendScale, Is.EqualTo(complementBefore).Within(0.0001f));
                Assert.That(profile.MicroExpressionsEnabled, Is.EqualTo(microBefore));
                Assert.That(profile.MicroExpressionAmplitude, Is.EqualTo(amplitudeBefore).Within(0.0001f));
                Assert.That(profile.SpeechAccentStrength, Is.EqualTo(accentBefore).Within(0.0001f));
                Assert.That(profile.MicroExpressionStillness, Is.EqualTo(stillnessBefore).Within(0.0001f));
                Assert.That(profile.ListeningReactionStrength, Is.EqualTo(listeningBefore).Within(0.0001f));
                Assert.That(profile.ThinkingReactionStrength, Is.EqualTo(thinkingBefore).Within(0.0001f));
                Assert.That(profile.ReactingAccentStrength, Is.EqualTo(reactingBefore).Within(0.0001f));
                Assert.That(profile.InterruptedFlinchStrength, Is.EqualTo(interruptedBefore).Within(0.0001f));
                Assert.That(profile.ProsodyCoupling, Is.EqualTo(prosodyCouplingBefore).Within(0.0001f));

                for (int i = 0; i < profile.Expressiveness.Count; i++)
                {
                    EmotionExpressivenessEntry entry = profile.Expressiveness[i];
                    Assert.That(entry.Gain, Is.InRange(0f, 2f));
                }

                for (int i = 0; i < profile.EmotionDynamics.Count; i++)
                {
                    EmotionDynamicsEntry entry = profile.EmotionDynamics[i];
                    Assert.That(entry.AttackSpeed, Is.InRange(0.1f, 20f));
                    Assert.That(entry.DecaySpeed, Is.InRange(0.1f, 20f));
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WarmReservedExpressive_DifferInDistinguishingFields()
        {
            ConvaiEmotionProfile warm = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Warm, null);
            ConvaiEmotionProfile reserved = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Reserved, null);
            ConvaiEmotionProfile expressive = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Energetic, null);
            try
            {
                Assert.That(warm.BaselineIntensity, Is.Not.EqualTo(reserved.BaselineIntensity));
                Assert.That(warm.MicroExpressionAmplitude, Is.Not.EqualTo(reserved.MicroExpressionAmplitude));
                Assert.That(warm.MicroExpressionAmplitude, Is.Not.EqualTo(expressive.MicroExpressionAmplitude));
                Assert.That(reserved.MicroExpressionAmplitude, Is.Not.EqualTo(expressive.MicroExpressionAmplitude));

                Assert.That(reserved.MicroExpressionAmplitude, Is.LessThan(warm.MicroExpressionAmplitude));
                Assert.That(warm.MicroExpressionAmplitude, Is.LessThan(expressive.MicroExpressionAmplitude));

                Assert.That(reserved.GetExpressivenessGain("joy"), Is.LessThan(warm.GetExpressivenessGain("joy")));
                Assert.That(warm.GetExpressivenessGain("joy"), Is.LessThan(expressive.GetExpressivenessGain("joy")));
            }
            finally
            {
                Object.DestroyImmediate(warm);
                Object.DestroyImmediate(reserved);
                Object.DestroyImmediate(expressive);
            }
        }
    }
}
