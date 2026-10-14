using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The enforcement mechanism for the project's naming rule on the Gaze Profile.
    /// </summary>
    /// <remarks>
    ///     The rule permits clinical vocabulary — Saccade, VOR, OMR, Oculomotor — in internal and
    ///     tooling types only. The Gaze Profile is a customer-facing ScriptableObject, and before
    ///     <see cref="GazeProfileLabels" /> it rendered every field with Unity's derived label, so
    ///     a non-technical user was reading "Saccade Duration Per Degree" and "Synthetic
    ///     Interpupillary Distance". Documentation cannot hold that line; this test can.
    /// </remarks>
    public sealed class GazeProfileLabelTests
    {
        /// <summary>
        ///     Substrings that must never reach a customer-facing label. Deliberately includes the
        ///     module's own internal vocabulary (interest, commitment, policy, LOD, firehose)
        ///     alongside the research terms — a user does not know what a "policy blend speed" is
        ///     either.
        /// </summary>
        private static readonly string[] Denylist =
        {
            "saccade", "vergence", "oculomotor", "interpupillary", "proxemic", "lod", "firehose",
            "verbosity", "recruitment", "commitment", "interest", "policy", "orbit", "fixation",
            "ambient", "pitch", "yaw", "sharpness", "threshold", "modulation", "actuation",
            "relevance", "deadzone", "refractory", "clustering", "bias", "jitter"
        };

        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>
        ///     The settings a user actually reads a label for. The profile groups them into nested
        ///     blocks, so this walks into each block and yields its fields rather than the block —
        ///     nobody ever sees a label for "targeting".
        /// </summary>
        private static IEnumerable<FieldInfo> SerializedFields()
        {
            foreach (FieldInfo field in typeof(ConvaiGazeProfile).GetFields(FieldFlags))
            {
                if (!IsSerialized(field)) continue;

                if (field.FieldType.DeclaringType == typeof(ConvaiGazeProfile) && !field.FieldType.IsEnum)
                {
                    foreach (FieldInfo nested in field.FieldType.GetFields(FieldFlags))
                        if (IsSerialized(nested))
                            yield return nested;

                    continue;
                }

                yield return field;
            }
        }

        private static bool IsSerialized(FieldInfo field)
        {
            if (field.IsNotSerialized || field.IsStatic) return false;
            return field.IsPublic || field.GetCustomAttribute<SerializeField>() != null;
        }

        [Test]
        public void EveryOverride_NamesARealSerializedField()
        {
            var names = new HashSet<string>();
            foreach (FieldInfo field in SerializedFields()) names.Add(field.Name);

            foreach (string overridden in GazeProfileLabels.OverriddenFields)
                Assert.IsTrue(names.Contains(overridden),
                    $"The label table renames '{overridden}', which is not a serialized field on " +
                    "ConvaiGazeProfile. A dead entry means a field was renamed and the label was left behind.");
        }

        [Test]
        public void EveryFieldWhoseDerivedLabelCarriesJargon_HasAPlainEnglishOverride()
        {
            var offenders = new List<string>();

            foreach (FieldInfo field in SerializedFields())
            {
                if (GazeProfileLabels.HasOverride(field.Name)) continue;

                string derived = field.Name.ToLowerInvariant();
                foreach (string term in Denylist)
                {
                    if (!derived.Contains(term)) continue;
                    offenders.Add($"{field.Name} (contains '{term}')");
                    break;
                }
            }

            Assert.IsEmpty(offenders,
                "These Gaze Profile fields would render with engine or research vocabulary as their " +
                "inspector label. Add a plain-English entry to GazeProfileLabels:\n  " +
                string.Join("\n  ", offenders));
        }

        /// <summary>
        ///     Terms that are wrong in a <em>label</em>, as opposed to terms that merely signal a
        ///     field name needs one.
        /// </summary>
        /// <remarks>
        ///     Narrower than <see cref="Denylist" /> on purpose. That list contains ordinary English
        ///     words — "interest", "bias", "threshold" — because a field called
        ///     <c>interestDecayPerSecond</c> does need renaming. But "Fully Interested Within" is
        ///     good copy, and banning the word from replacements too would force worse English to
        ///     satisfy a test. Only genuinely clinical or engine vocabulary is forbidden here.
        /// </remarks>
        private static readonly string[] LabelDenylist =
        {
            "saccade", "vergence", "oculomotor", "interpupillary", "proxemic", "firehose",
            "verbosity", "actuation", "deadzone", "refractory", "recruitment", "modulation"
        };

        [Test]
        public void NoOverride_ReintroducesJargon()
        {
            var offenders = new List<string>();

            foreach (string fieldName in GazeProfileLabels.OverriddenFields)
            {
                SerializedFieldLabel(fieldName, out string label);
                string lowered = label.ToLowerInvariant();

                foreach (string term in LabelDenylist)
                {
                    if (!lowered.Contains(term)) continue;
                    offenders.Add($"{fieldName} → '{label}' (still contains '{term}')");
                    break;
                }
            }

            Assert.IsEmpty(offenders,
                "A replacement label still carries the clinical vocabulary it was meant to remove:\n  " +
                string.Join("\n  ", offenders));
        }

        [Test]
        public void EveryOverride_ReadsAsASetting_NotAsAnIdentifier()
        {
            foreach (string fieldName in GazeProfileLabels.OverriddenFields)
            {
                SerializedFieldLabel(fieldName, out string label);

                Assert.IsNotEmpty(label, $"{fieldName} has an empty label.");
                Assert.IsFalse(label.Contains("_"),
                    $"{fieldName} → '{label}' looks like an identifier, not a label.");
                Assert.AreNotEqual(fieldName, label,
                    $"{fieldName}'s override is the field name itself.");
            }
        }

        /// <summary>
        ///     Resolves the authored replacement text for a field. The table is private, so this
        ///     goes through the same reflection the guard is asserting about — which is fine: the
        ///     table is editor tooling, and a test that could not see it could not guard it.
        /// </summary>
        private static void SerializedFieldLabel(string fieldName, out string label)
        {
            FieldInfo table = typeof(GazeProfileLabels)
                .GetField("Labels", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(table, "GazeProfileLabels.Labels is missing — the guard cannot read the table.");

            var labels = (Dictionary<string, string>)table.GetValue(null);
            Assert.IsTrue(labels.TryGetValue(fieldName, out label), $"No entry for {fieldName}.");
        }
    }
}
