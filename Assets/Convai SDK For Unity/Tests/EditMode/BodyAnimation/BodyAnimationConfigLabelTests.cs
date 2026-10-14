using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Guards the config's user-facing vocabulary. Unity derives an inspector label from the
    ///     field identifier, which produces "Rate Warp Min", "Motion Handoff Normalized Time" and
    ///     "Firehose Interval Seconds" — internal terms that mean nothing to the customer reading
    ///     them, and which the project's naming rule keeps off user-facing surfaces.
    /// </summary>
    internal sealed class BodyAnimationConfigLabelTests
    {
        /// <summary>
        ///     Words that are meaningful inside the module and meaningless outside it. Any of these
        ///     surviving into a drawn label means a field needs an entry in the label table.
        /// </summary>
        private static readonly string[] BannedWords =
        {
            "Warp",
            "Firehose",
            "Verbosity",
            "Normalized",
            "Exertion",
            "Overlay",
            "Additive",
            "Co Speech",
            "Derivative",
            "Refractory",
            "Layer Weight"
        };

        [Test]
        public void NoDrawnConfigLabel_UsesInternalJargon()
        {
            var config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            try
            {
                var serialized = new SerializedObject(config);
                var offenders = new List<string>();

                for (int i = 0; i < BodyAnimationConfigSections.Sections.Length; i++)
                {
                    string[] fields = BodyAnimationConfigSections.Sections[i].Fields;
                    for (int f = 0; f < fields.Length; f++)
                    {
                        SerializedProperty property = serialized.FindProperty(fields[f]);
                        if (property == null) continue;

                        string label = BodyAnimationConfigLabels.For(property).text;
                        for (int w = 0; w < BannedWords.Length; w++)
                        {
                            if (label.Contains(BannedWords[w]))
                                offenders.Add($"{fields[f]} → \"{label}\" (contains \"{BannedWords[w]}\")");
                        }
                    }
                }

                Assert.IsEmpty(offenders,
                    "These settings still read as engine internals. Add a plain-English entry to " +
                    $"{nameof(BodyAnimationConfigLabels)}: {string.Join("; ", offenders)}");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        /// <summary>A label override naming a field that no longer exists would silently do nothing.</summary>
        [Test]
        public void EveryLabelOverride_NamesARealField()
        {
            var config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            try
            {
                var serialized = new SerializedObject(config);
                var unknown = new List<string>();

                foreach (string fieldName in BodyAnimationConfigLabels.OverriddenFields)
                {
                    if (serialized.FindProperty(fieldName) == null) unknown.Add(fieldName);
                }

                Assert.IsEmpty(unknown,
                    $"These label overrides name fields the config does not have: {string.Join(", ", unknown)}");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        /// <summary>An overridden label must keep the field's authored tooltip — it carries the why.</summary>
        [Test]
        public void OverriddenLabels_PreserveTheAuthoredTooltip()
        {
            var config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            try
            {
                var serialized = new SerializedObject(config);
                SerializedProperty movingTalk = serialized.FindProperty("_movingTalkMode");

                Assert.IsNotNull(movingTalk);
                GUIContent content = BodyAnimationConfigLabels.For(movingTalk);

                Assert.AreNotEqual(movingTalk.displayName, content.text,
                    "This field is expected to carry a plain-English override.");
                Assert.AreEqual(movingTalk.tooltip, content.tooltip,
                    "Renaming a label must never drop the explanation attached to the field.");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }
    }
}
