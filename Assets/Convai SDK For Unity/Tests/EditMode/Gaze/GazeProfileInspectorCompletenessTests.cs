using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Every serialized Gaze Profile field must appear in the inspector exactly once.
    /// </summary>
    /// <remarks>
    ///     A curated inspector trades Unity's automatic "draw everything" for a hand-written list,
    ///     and the cost of that trade is that a field added to the profile and forgotten here
    ///     becomes silently unreachable — the user cannot see it, cannot change it, and gets no
    ///     hint it exists. This test is what makes the trade safe. Drawing one twice is the other
    ///     half: two controls writing the same value fight each other on screen.
    /// </remarks>
    public sealed class GazeProfileInspectorCompletenessTests
    {
        private const string InspectorAssetPath =
            "Packages/com.convai.convai-sdk-for-unity/SDK/Editor/Embodiment/Inspectors/ConvaiGazeProfileInspector.cs";

        private static readonly Regex DrawCall = new(@"DrawLabelled\(""([A-Za-z0-9_]+)""\)", RegexOptions.Compiled);

        /// <summary>
        ///     Fields the inspector reaches through a dedicated editor rather than a generic
        ///     property field. They are still drawn — just not by name — so they count as covered.
        /// </summary>
        private static readonly Dictionary<string, string> DrawnByDedicatedEditor = new()
        {
            ["statePolicies"] = "GazeStatePolicyTable draws this as one row per conversation state, " +
                                "because the raw struct array was the last place a user had to " +
                                "understand the data model to change behaviour."
        };

        private static List<string> DrawnFields()
        {
            string path = Path.GetFullPath(InspectorAssetPath);
            Assert.IsTrue(File.Exists(path),
                $"The gaze profile inspector was not found at {InspectorAssetPath}. If it moved, " +
                "update this guard — do not delete it.");

            var drawn = new List<string>();
            foreach (Match match in DrawCall.Matches(File.ReadAllText(path)))
                drawn.Add(match.Groups[1].Value);

            drawn.AddRange(DrawnByDedicatedEditor.Keys);
            return drawn;
        }

        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>
        ///     Every setting a user is meant to reach, by plain name, with the nested settings blocks
        ///     flattened. This walk is deliberately its own — not <c>GazeProfileSerializedPaths</c>'s
        ///     — so that a bug in the path resolver cannot make the completeness guard agree with it.
        /// </summary>
        private static List<string> SerializedFieldNames()
        {
            var names = new List<string>();

            foreach (FieldInfo field in typeof(ConvaiGazeProfile).GetFields(FieldFlags))
            {
                if (!IsUserFacingSerializedField(field)) continue;

                // A settings block contributes its own fields, not itself: the user reaches
                // "playerMaxDistance", never "targeting".
                if (field.FieldType.DeclaringType == typeof(ConvaiGazeProfile) && !field.FieldType.IsEnum)
                {
                    foreach (FieldInfo nested in field.FieldType.GetFields(FieldFlags))
                        if (IsUserFacingSerializedField(nested))
                            names.Add(nested.Name);

                    continue;
                }

                names.Add(field.Name);
            }

            return names;
        }

        /// <summary>
        ///     Serialized, and therefore something the user is meant to reach. The profile has no
        ///     hidden serialized state to exclude here — <c>GazeProfileAssetTests</c> fails the
        ///     build if any appears — so everything it serializes has to be drawn.
        /// </summary>
        private static bool IsUserFacingSerializedField(FieldInfo field)
        {
            if (field.IsNotSerialized || field.IsStatic) return false;
            return field.IsPublic || field.GetCustomAttribute<SerializeField>() != null;
        }

        [Test]
        public void EverySerializedField_IsDrawnByTheInspector()
        {
            List<string> drawn = DrawnFields();
            var missing = new List<string>();

            foreach (string field in SerializedFieldNames())
                if (!drawn.Contains(field))
                    missing.Add(field);

            Assert.IsEmpty(missing,
                "These Gaze Profile settings exist but no inspector section draws them, so a user " +
                "can never reach them:\n  " + string.Join("\n  ", missing));
        }

        [Test]
        public void NoField_IsDrawnTwice()
        {
            List<string> drawn = DrawnFields();
            var seen = new HashSet<string>();
            var duplicates = new List<string>();

            foreach (string field in drawn)
                if (!seen.Add(field))
                    duplicates.Add(field);

            Assert.IsEmpty(duplicates,
                "These settings are drawn in more than one section, so two controls write the same " +
                "value:\n  " + string.Join("\n  ", duplicates));
        }

        [Test]
        public void FieldsClaimedByADedicatedEditor_AreActuallyWiredToOne()
        {
            string source = File.ReadAllText(Path.GetFullPath(InspectorAssetPath));

            // A field excused from the by-name check has to actually be drawn by something. Without
            // this, the excuse list becomes a way to make a missing field's test pass.
            Assert.IsTrue(source.Contains("GazeStatePolicyTable.Draw"),
                "statePolicies is excused from the by-name check because GazeStatePolicyTable draws " +
                "it, but the inspector no longer calls that table - the setting is now unreachable.");
        }

        [Test]
        public void EveryDrawnName_IsARealField()
        {
            List<string> fields = SerializedFieldNames();
            var unknown = new List<string>();

            foreach (string drawn in DrawnFields())
                if (!fields.Contains(drawn))
                    unknown.Add(drawn);

            Assert.IsEmpty(unknown,
                "The inspector draws names that are not serialized fields — these render as a " +
                "'Missing Setting' warning box to the user:\n  " + string.Join("\n  ", unknown));
        }

        [Test]
        public void TheProfileInspector_IsTheRegisteredEditor()
        {
            ConvaiGazeProfile profile = ConvaiGazeProfile.CreateDefault();
            try
            {
                UnityEditor.Editor editor = UnityEditor.Editor.CreateEditor(profile);
                Assert.IsNotNull(editor);
                Assert.AreEqual("ConvaiGazeProfileInspector", editor.GetType().Name,
                    "The custom inspector must win over Unity's default, or none of the plain-English " +
                    "labelling reaches the user.");
                Object.DestroyImmediate(editor);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
