using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.Emotion.Editor;
using Convai.Modules.Emotion.Outputs;
using Convai.Modules.Emotion.Profiles;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Inspector-validation tests for <see cref="EmotionProfileValidation" />:
    ///     unknown-label detection per authoring category, the null-taxonomy default-vocabulary
    ///     fallback (with a drift guard against <see cref="EmotionTaxonomyAsset.CreateDefault" />),
    ///     did-you-mean suggestions, alias resolution, empty/payload-less skipping, and
    ///     one-finding-per-duplicate dedup.
    /// </summary>
    [TestFixture]
    public sealed class EmotionProfileValidationTests
    {
        private static ConvaiEmotionProfile NewProfile() => ScriptableObject.CreateInstance<ConvaiEmotionProfile>();

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        /// <summary>
        ///     Builds a taxonomy asset from explicit entries (label, aliases, isNeutral) via
        ///     reflection on the asset's private <c>entries</c> field, since the field has no
        ///     public setter.
        /// </summary>
        private static EmotionTaxonomyAsset CreateTaxonomy(params (string label, string[] aliases, bool isNeutral)[] entryData)
        {
            EmotionTaxonomyAsset taxonomy = ScriptableObject.CreateInstance<EmotionTaxonomyAsset>();
            taxonomy.hideFlags = HideFlags.HideAndDontSave;

            var entries = new List<EmotionTaxonomyEntry>();
            foreach ((string label, string[] aliases, bool isNeutral) in entryData)
                entries.Add(new EmotionTaxonomyEntry(label, aliases, null, 0.5f, isNeutral));

            FieldInfo entriesField = typeof(EmotionTaxonomyAsset).GetField(
                "entries", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(entriesField, "EmotionTaxonomyAsset must declare a private 'entries' field.");
            entriesField.SetValue(taxonomy, entries);

            return taxonomy;
        }

        // ── per-category unknown-label detection ────────────────────────────────

        [Test]
        public void Validate_NullProfile_ProducesNoFindings()
        {
            var results = new List<EmotionProfileValidation.Finding> { default };
            EmotionProfileValidation.Validate(null, results);
            Assert.That(results, Is.Empty, "Validate must clear the caller-owned list even for a null profile.");
        }

        [Test]
        public void Validate_UnknownBaselineLabel_Flagged_KnownLabel_NotFlagged()
        {
            ConvaiEmotionProfile profile = NewProfile();
            EmotionTaxonomyAsset taxonomy = EmotionTaxonomyAsset.CreateDefault();
            try
            {
                SetPrivateField(profile, "taxonomy", taxonomy);
                SetPrivateField(profile, "baselineEmotionLabel", "joyy");

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);

                Assert.That(results.Count, Is.EqualTo(1));
                Assert.That(results[0].Category, Is.EqualTo(EmotionProfileValidation.FindingCategory.Baseline));
                Assert.That(results[0].Label, Is.EqualTo("joyy"));

                SetPrivateField(profile, "baselineEmotionLabel", "joy");
                EmotionProfileValidation.Validate(profile, results);
                Assert.That(results, Is.Empty, "A known label must produce zero findings.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(taxonomy);
            }
        }

        [Test]
        public void Validate_UnknownExpressivenessLabel_Flagged()
        {
            ConvaiEmotionProfile profile = NewProfile();
            EmotionTaxonomyAsset taxonomy = EmotionTaxonomyAsset.CreateDefault();
            try
            {
                SetPrivateField(profile, "taxonomy", taxonomy);
                SetPrivateField(profile, "expressiveness", new List<EmotionExpressivenessEntry>
                {
                    new("joy", 1.2f),
                    new("trussed", 0.8f)
                });

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);

                Assert.That(results.Count, Is.EqualTo(1));
                Assert.That(results[0].Category, Is.EqualTo(EmotionProfileValidation.FindingCategory.Expressiveness));
                Assert.That(results[0].Label, Is.EqualTo("trussed"));
                Assert.That(results[0].Index, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(taxonomy);
            }
        }

        [Test]
        public void Validate_UnknownDynamicsLabel_Flagged()
        {
            ConvaiEmotionProfile profile = NewProfile();
            EmotionTaxonomyAsset taxonomy = EmotionTaxonomyAsset.CreateDefault();
            try
            {
                SetPrivateField(profile, "taxonomy", taxonomy);
                SetPrivateField(profile, "emotionDynamics", new List<EmotionDynamicsEntry>
                {
                    new("angre", 8f, 2f)
                });

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);

                Assert.That(results.Count, Is.EqualTo(1));
                Assert.That(results[0].Category, Is.EqualTo(EmotionProfileValidation.FindingCategory.Dynamics));
                Assert.That(results[0].Label, Is.EqualTo("angre"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(taxonomy);
            }
        }



        [Test]
        public void Validate_UnknownMaterialSlotLabel_Flagged()
        {
            ConvaiEmotionProfile profile = NewProfile();
            EmotionTaxonomyAsset taxonomy = EmotionTaxonomyAsset.CreateDefault();
            try
            {
                SetPrivateField(profile, "taxonomy", taxonomy);
                profile.MaterialBinding.SetSlots(new List<MaterialPropertyEmotionSlot>
                {
                    new("angar", "_EmotionBlush", 0f, 1f)
                });

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);

                Assert.That(results.Count, Is.EqualTo(1));
                Assert.That(results[0].Category, Is.EqualTo(EmotionProfileValidation.FindingCategory.MaterialSlots));
                Assert.That(results[0].Label, Is.EqualTo("angar"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(taxonomy);
            }
        }

        // ── null-taxonomy default-vocabulary fallback + drift guard ──────────

        [Test]
        public void Validate_NullTaxonomy_UsesDefaultVocabulary()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                // profile.Taxonomy is null by default (no asset assigned).
                SetPrivateField(profile, "baselineEmotionLabel", "joyy");

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);
                Assert.That(results.Count, Is.EqualTo(1), "A typo must still be caught with no taxonomy assigned.");

                foreach (string label in EmotionProfileValidation.DefaultVocabularyLabels)
                {
                    SetPrivateField(profile, "baselineEmotionLabel", label);
                    EmotionProfileValidation.Validate(profile, results);
                    Assert.That(results, Is.Empty, $"Default vocabulary label '{label}' must resolve with no taxonomy.");
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void DefaultVocabularyLabels_MatchesEmotionTaxonomyAssetCreateDefault()
        {
            EmotionTaxonomyAsset defaultTaxonomy = EmotionTaxonomyAsset.CreateDefault();
            try
            {
                var actualLabels = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (Convai.Domain.Embodiment.Taxonomy.EmotionDescriptor descriptor in defaultTaxonomy.Emotions)
                    actualLabels.Add(descriptor.Label);

                var hardcodedLabels = new HashSet<string>(
                    EmotionProfileValidation.DefaultVocabularyLabels, System.StringComparer.OrdinalIgnoreCase);

                Assert.That(hardcodedLabels.SetEquals(actualLabels), Is.True,
                    "EmotionProfileValidation.DefaultVocabularyLabels must exactly mirror " +
                    "EmotionTaxonomyAsset.CreateDefault()'s canonical label set (drift guard).");
            }
            finally
            {
                Object.DestroyImmediate(defaultTaxonomy);
            }
        }

        // ── suggestions ───────────────────────────────────────────────────────

        [Test]
        public void Validate_Suggestions_CloseTyposSuggestNearestLabel_GarbageSuggestsNothing()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                SetPrivateField(profile, "baselineEmotionLabel", "joyy");
                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);
                Assert.That(results[0].Suggestion, Is.EqualTo("joy"));

                SetPrivateField(profile, "baselineEmotionLabel", "sadnes");
                EmotionProfileValidation.Validate(profile, results);
                Assert.That(results[0].Suggestion, Is.EqualTo("sadness"));

                SetPrivateField(profile, "baselineEmotionLabel", "xyzq");
                EmotionProfileValidation.Validate(profile, results);
                Assert.That(results[0].Suggestion, Is.Null.Or.Empty,
                    "A label with no close vocabulary match must not suggest anything.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Validate_KnownLabel_CaseAndWhitespaceInsensitive_NotFlagged()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                SetPrivateField(profile, "baselineEmotionLabel", "JOY ");
                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);
                Assert.That(results, Is.Empty, "Resolution must be OrdinalIgnoreCase, matching runtime semantics.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── alias resolution ────────────────────────────────────────────────────

        [Test]
        public void Validate_AliasOnlyLabel_NotFlagged()
        {
            ConvaiEmotionProfile profile = NewProfile();
            EmotionTaxonomyAsset taxonomy = CreateTaxonomy(
                ("neutral", null, true),
                ("joy", new[] { "happy" }, false));
            try
            {
                SetPrivateField(profile, "taxonomy", taxonomy);
                SetPrivateField(profile, "baselineEmotionLabel", "happy");

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);

                Assert.That(results, Is.Empty, "An alias-only label must resolve, not be flagged as unknown.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(taxonomy);
            }
        }

        // ── empty labels and payload-less slots are skipped ─────────────────────

        [Test]
        public void Validate_EmptyLabelsAndPayloadlessSlots_Skipped()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                SetPrivateField(profile, "baselineEmotionLabel", string.Empty);
                SetPrivateField(profile, "expressiveness", new List<EmotionExpressivenessEntry>
                {
                    new(string.Empty, 1f),
                    new("   ", 1f)
                });
                SetPrivateField(profile, "emotionDynamics", new List<EmotionDynamicsEntry>
                {
                    new(string.Empty, 5f, 2f)
                });
                profile.MaterialBinding.SetSlots(new List<MaterialPropertyEmotionSlot>
                {
                    // Unknown label, but no shader property payload — must be skipped.
                    new("stillnosuch", string.Empty, 0f, 1f)
                });

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);

                Assert.That(results, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── duplicate offending labels dedup to one finding per category ────────

        [Test]
        public void Validate_DuplicateTypoLabelInOneCategory_ReportedOnce()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                SetPrivateField(profile, "expressiveness", new List<EmotionExpressivenessEntry>
                {
                    new("joyy", 1.2f),
                    new("joyy", 0.8f),
                    new("JOYY", 1.1f)
                });

                var results = new List<EmotionProfileValidation.Finding>();
                EmotionProfileValidation.Validate(profile, results);

                Assert.That(results.Count, Is.EqualTo(1),
                    "The same (category, label) pair must be reported only once, case-insensitively.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
