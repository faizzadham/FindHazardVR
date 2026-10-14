using System.Collections.Generic;
using System.Linq;
using Convai.Modules.Emotion.Editor;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Guards the profile's authoring surface: every serialized field has exactly one named
    ///     home, and no user-visible label or tooltip carries engine or research vocabulary.
    /// </summary>
    /// <remarks>
    ///     Both failure modes had already happened. Fields kept being added without a home, and the
    ///     inspector titled its sections "Blending &amp; Hysteresis" and "Per-Emotion Dynamics" over
    ///     labels Unity derived from identifiers like <c>lerpSpeed</c> and
    ///     <c>complementBlendScale</c>. These tests make both fail loudly instead of accumulating.
    /// </remarks>
    public sealed class EmotionConfigSectionGuardTests
    {
        private ConvaiEmotionProfile _profile;
        private SerializedObject _serialized;

        [SetUp]
        public void SetUp()
        {
            _profile = ScriptableObject.CreateInstance<ConvaiEmotionProfile>();
            _serialized = new SerializedObject(_profile);
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_profile);

        private List<string> SerializedFieldNames()
        {
            var names = new List<string>();
            SerializedProperty iterator = _serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script") continue;
                names.Add(iterator.propertyPath);
            }
            return names;
        }

        [Test]
        public void EverySerializedField_HasExactlyOneHome()
        {
            var mapped = new HashSet<string>();
            EmotionConfigSections.CollectMappedFields(mapped);

            List<string> unmapped = SerializedFieldNames().Where(n => !mapped.Contains(n)).ToList();

            Assert.That(unmapped, Is.Empty,
                "These profile fields have no section, so they would be invisible to the user: " +
                string.Join(", ", unmapped));
        }

        [Test]
        public void NoFieldAppearsInTwoSections()
        {
            var seen = new Dictionary<string, string>();
            var duplicates = new List<string>();

            foreach (EmotionConfigSection section in EmotionConfigSections.Sections)
            {
                foreach (string field in section.Fields)
                {
                    if (seen.TryGetValue(field, out string first))
                        duplicates.Add($"{field} (in both {first} and {section.Id})");
                    else
                        seen[field] = section.Id;
                }
            }

            Assert.That(duplicates, Is.Empty, string.Join("; ", duplicates));
        }

        [Test]
        public void EveryMappedField_ResolvesToARealSerializedProperty()
        {
            var real = new HashSet<string>(SerializedFieldNames());
            var ghosts = new List<string>();

            foreach (EmotionConfigSection section in EmotionConfigSections.Sections)
            foreach (string field in section.Fields)
                if (!real.Contains(field)) ghosts.Add($"{section.Id}/{field}");

            Assert.That(ghosts, Is.Empty,
                "These sections reference fields the profile no longer has: " + string.Join(", ", ghosts));
        }

        [Test]
        public void EveryRenamedField_ResolvesToARealSerializedProperty()
        {
            var real = new HashSet<string>(SerializedFieldNames());
            List<string> ghosts = EmotionConfigLabels.OverriddenFields.Where(f => !real.Contains(f)).ToList();

            Assert.That(ghosts, Is.Empty,
                "These labels rename fields the profile no longer has: " + string.Join(", ", ghosts));
        }

        [Test]
        public void EverySection_HasATitleAndASummary()
        {
            foreach (EmotionConfigSection section in EmotionConfigSections.Sections)
            {
                Assert.That(section.Title, Is.Not.Null.And.Not.Empty, section.Id);
                Assert.That(section.Summary, Is.Not.Null.And.Not.Empty,
                    $"{section.Id} has no one-line explanation, so the user has to guess what it is for.");
            }
        }

        /// <summary>
        ///     A declared gate must be a real serialized <c>bool</c> on the profile. A gate naming a
        ///     renamed or deleted field silently resolves to "no gate", so the field it guards would
        ///     go back to looking live while doing nothing — the exact failure the gate table exists
        ///     to prevent.
        /// </summary>
        [Test]
        public void EveryDeclaredGate_IsARealBooleanField()
        {
            foreach (KeyValuePair<string, string> pair in EmotionConfigSections.AllGates)
            {
                SerializedProperty gate = _serialized.FindProperty(pair.Value);
                Assert.That(gate, Is.Not.Null,
                    $"'{pair.Key}' declares gate '{pair.Value}', which the profile does not have.");
                Assert.That(gate.propertyType, Is.EqualTo(SerializedPropertyType.Boolean),
                    $"'{pair.Key}' declares gate '{pair.Value}', which is not a toggle.");
            }
        }

        /// <summary>A gate must guard a field that exists, and must never guard itself.</summary>
        [Test]
        public void EveryGatedField_IsARealFieldOtherThanItsOwnGate()
        {
            var real = new HashSet<string>(SerializedFieldNames());

            foreach (KeyValuePair<string, string> pair in EmotionConfigSections.AllGates)
            {
                Assert.That(real, Contains.Item(pair.Key),
                    $"Gate declared for '{pair.Key}', which the profile no longer has.");
                Assert.That(pair.Key, Is.Not.EqualTo(pair.Value),
                    $"'{pair.Key}' is declared as its own gate, which would disable the toggle itself.");
            }
        }

        /// <summary>
        ///     The four conversation-beat reactions are composed by the micro-expression layer, so
        ///     with that layer off they do nothing at all. They were previously grouped under the
        ///     unrelated micro-burst toggle, which both greyed them out when Micro Burst was off and
        ///     left them looking live when Small Movements was — wrong in both directions.
        /// </summary>
        [TestCase("listeningReactionStrength")]
        [TestCase("thinkingReactionStrength")]
        [TestCase("reactingAccentStrength")]
        [TestCase("interruptedFlinchStrength")]
        public void BeatReaction_IsGatedByTheSmallMovementLayer(string field)
        {
            Assert.That(EmotionConfigSections.GateForField(field),
                Is.EqualTo("microExpressionsEnabled"),
                $"'{field}' is inert unless the micro-expression layer is running, so that is the " +
                "toggle it must be gated by.");
        }

        /// <summary>
        ///     A section's own toggle must sit in the same section as the fields it gates, so the
        ///     reason a control is unavailable is visible without hunting for it.
        /// </summary>
        [Test]
        public void EveryGate_IsDrawnInTheSameSectionAsWhatItGates()
        {
            var sectionOf = new Dictionary<string, string>();
            foreach (EmotionConfigSection section in EmotionConfigSections.Sections)
                foreach (string field in section.Fields)
                    sectionOf[field] = section.Id;

            foreach (KeyValuePair<string, string> pair in EmotionConfigSections.AllGates)
            {
                Assert.That(sectionOf.TryGetValue(pair.Key, out string gatedSection), Is.True, pair.Key);
                Assert.That(sectionOf.TryGetValue(pair.Value, out string gateSection), Is.True, pair.Value);
                Assert.That(gateSection, Is.EqualTo(gatedSection),
                    $"'{pair.Key}' sits in {gatedSection} but its toggle '{pair.Value}' is drawn in " +
                    $"{gateSection}, so a user sees a disabled control with no visible reason.");
            }
        }

        /// <summary>
        ///     Vocabulary that must never reach a customer-facing label, section title or tooltip.
        ///     Engine internals, research terms, and this module's own development-era names.
        /// </summary>
        /// <remarks>
        ///     Matched on whole words, so "Personality" does not trip the "Persona" rule. Unity's
        ///     own user-facing vocabulary is deliberately absent: a user configuring a face will see
        ///     the word "blendshape" on their own mesh importer, so banning it would force us to
        ///     invent a worse synonym for something they already know.
        ///     <para>
        ///         Development-era plan labels are absent for a different reason: the release
        ///         residue check forbids them across the whole package rather than only on labels,
        ///         so naming them here would be a second, narrower copy of a rule that already holds.
        ///     </para>
        /// </remarks>
        private static readonly string[] BannedTerms =
        {
            "Lerp", "Hysteresis", "Prosody", "Taxonomy", "Plutchik", "FACS", "Demeanor",
            "Contagion", "Overshoot", "Dwell", "Complement", "Alternation", "NRCLex",
            "Blendshape Binding", "Blendshape Slots", "Micro Burst", "Micro-Burst",
            "Baseline", "Persona", "bit-for-bit", "opt-in gate"
        };

        /// <summary>Whole-word, case-insensitive containment. "Persona" must not match "Personality".</summary>
        private static bool ContainsTerm(string text, string term) =>
            !string.IsNullOrEmpty(text) &&
            System.Text.RegularExpressions.Regex.IsMatch(
                text,
                @"\b" + System.Text.RegularExpressions.Regex.Escape(term) + @"\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        [Test]
        public void NoUserVisibleLabelOrTooltip_UsesBannedVocabulary()
        {
            var violations = new List<string>();

            foreach (string text in EmotionConfigLabels.AllUserVisibleText)
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (string banned in BannedTerms)
                    if (ContainsTerm(text, banned))
                        violations.Add($"'{banned}' in \"{text}\"");
            }

            Assert.That(violations, Is.Empty, string.Join("\n", violations));
        }

        [Test]
        public void NoSectionTitleOrSummary_UsesBannedVocabulary()
        {
            var violations = new List<string>();

            foreach (EmotionConfigSection section in EmotionConfigSections.Sections)
            foreach (string text in new[] { section.Title, section.Summary })
            foreach (string banned in BannedTerms)
                if (ContainsTerm(text, banned))
                    violations.Add($"'{banned}' in {section.Id}: \"{text}\"");

            Assert.That(violations, Is.Empty, string.Join("\n", violations));
        }

        [Test]
        public void EveryFieldWhoseDerivedNameCarriesBannedVocabulary_IsRenamed()
        {
            var violations = new List<string>();

            SerializedProperty iterator = _serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script") continue;
                if (EmotionConfigLabels.HasOverride(iterator.propertyPath)) continue;

                foreach (string banned in BannedTerms)
                    if (ContainsTerm(iterator.displayName, banned))
                        violations.Add($"{iterator.propertyPath} renders as \"{iterator.displayName}\" ('{banned}')");
            }

            Assert.That(violations, Is.Empty,
                "These fields fall through to Unity's derived label, which carries internal " +
                "vocabulary:\n" + string.Join("\n", violations));
        }
    }
}
