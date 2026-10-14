using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Guards the shape of the Gaze Profile asset: settings live in blocks, the type carries no
    ///     compatibility layer, and the profile the samples ship actually holds the values its file
    ///     says it does.
    /// </summary>
    /// <remarks>
    ///     The profile's settings are grouped into nested blocks, which changes each one's serialized
    ///     path. The obvious way to absorb that is a hidden second copy of every setting plus a
    ///     one-time migration — and that copy then lives in the asset forever, is the first thing a
    ///     reader trips over, and quietly becomes the place a bug hides. It was deliberately not
    ///     done. These tests keep it from creeping back in and make sure the SDK's own asset is
    ///     authored in the current layout rather than relying on one.
    /// </remarks>
    public sealed class GazeProfileAssetTests
    {
        private const BindingFlags Flags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private const string ShippedProfileAssetPath =
            "Packages/com.convai.convai-sdk-for-unity/SamplesShared/Profiles/Embodiment/Modules/Gaze/" +
            "ConvaiSamplesShared_GazeProfile.asset";

        private static bool IsSerialized(FieldInfo field)
        {
            if (field.IsNotSerialized || field.IsStatic) return false;
            return field.IsPublic || field.GetCustomAttribute<SerializeField>() != null;
        }

        private static bool IsBlock(FieldInfo field) =>
            field.FieldType.DeclaringType == typeof(ConvaiGazeProfile) && !field.FieldType.IsEnum;

        /// <summary>Every setting, paired with the block that owns it.</summary>
        private static IEnumerable<(FieldInfo Block, FieldInfo Setting)> Settings()
        {
            foreach (FieldInfo block in typeof(ConvaiGazeProfile).GetFields(Flags))
            {
                if (!IsSerialized(block) || !IsBlock(block)) continue;

                foreach (FieldInfo setting in block.FieldType.GetFields(Flags))
                    if (IsSerialized(setting))
                        yield return (block, setting);
            }
        }

        // ── Shape ────────────────────────────────────────────────────────────

        [Test]
        public void EverySetting_LivesInASettingsBlock()
        {
            var loose = new List<string>();

            foreach (FieldInfo field in typeof(ConvaiGazeProfile).GetFields(Flags))
                if (IsSerialized(field) && !IsBlock(field))
                    loose.Add(field.Name);

            Assert.IsEmpty(loose,
                "These settings sit at the top level of the profile instead of in a block. The " +
                "grouping is the point of the asset's layout — a setting outside it is invisible to " +
                "the inspector's section walk and to the path resolver:\n  " + string.Join("\n  ", loose));
        }

        [Test]
        public void TheProfile_CarriesNoCompatibilityLayer()
        {
            var found = new List<string>();

            if (typeof(ISerializationCallbackReceiver).IsAssignableFrom(typeof(ConvaiGazeProfile)))
                found.Add("ConvaiGazeProfile implements ISerializationCallbackReceiver — the hook a " +
                          "field-migration shim needs");

            foreach (FieldInfo field in typeof(ConvaiGazeProfile).GetFields(Flags))
            {
                if (!IsSerialized(field)) continue;

                if (field.GetCustomAttribute<HideInInspector>() != null)
                    found.Add($"{field.Name} is a hidden serialized field — hidden storage is how a " +
                              "legacy layout gets kept alive");
                if (field.GetCustomAttribute<FormerlySerializedAsAttribute>() != null)
                    found.Add($"{field.Name} carries [FormerlySerializedAs]");
                if (Regex.IsMatch(field.Name, "legacy|deprecated|obsolete|schemaVersion", RegexOptions.IgnoreCase))
                    found.Add($"{field.Name} is named as a compatibility field");
            }

            foreach ((FieldInfo _, FieldInfo setting) in Settings())
                if (setting.GetCustomAttribute<ObsoleteAttribute>() != null)
                    found.Add($"{setting.Name} is marked [Obsolete]");

            // The decision this encodes: the profile carries the settings it has today and nothing
            // else. A profile authored against an older layout is handled by the release's migration
            // notes, not by a second copy of every setting that ships forever.
            Assert.IsEmpty(found,
                "The Gaze Profile has grown a backward-compatibility layer. That was ruled out " +
                "deliberately — settings are what the asset holds, not a live copy plus a dead " +
                "one:\n  " + string.Join("\n  ", found));
        }

        [Test]
        public void NoTwoSettings_ShareAName()
        {
            var seen = new Dictionary<string, string>();
            var clashes = new List<string>();

            foreach ((FieldInfo block, FieldInfo setting) in Settings())
            {
                if (seen.TryGetValue(setting.Name, out string other))
                    clashes.Add($"{setting.Name} is in both {other} and {block.Name}");
                else
                    seen[setting.Name] = block.Name;
            }

            // Every by-name surface — the inspector, the label table, the path resolver, the
            // archetypes — assumes a setting name identifies exactly one setting.
            Assert.IsEmpty(clashes,
                "Two blocks declare a setting of the same name, so nothing that looks a setting up " +
                "by name can resolve it:\n  " + string.Join("\n  ", clashes));
        }

        // ── The shipped asset ────────────────────────────────────────────────

        [Test]
        public void TheShippedSampleProfile_HoldsTheValuesItsFileDeclares()
        {
            string fullPath = Path.GetFullPath(ShippedProfileAssetPath);
            Assert.IsTrue(File.Exists(fullPath), $"Missing shipped profile: {ShippedProfileAssetPath}");

            var profile = AssetDatabase.LoadAssetAtPath<ConvaiGazeProfile>(ShippedProfileAssetPath);
            Assert.IsNotNull(profile, $"{ShippedProfileAssetPath} did not load as a ConvaiGazeProfile.");

            Dictionary<string, string> authored = ReadScalars(fullPath);
            var lost = new List<string>();
            int compared = 0;

            foreach ((FieldInfo block, FieldInfo setting) in Settings())
            {
                if (!authored.TryGetValue(setting.Name, out string raw)) continue;
                if (!TryParse(raw, setting.FieldType, out object expected)) continue;

                compared++;
                object actual = setting.GetValue(block.GetValue(profile));
                if (!NearlyEqual(expected, actual))
                    lost.Add($"{setting.Name}: file says {raw}, profile reports {actual}");
            }

            // Comparing against the file rather than a table of expected numbers is what keeps this
            // honest: a regression cannot be papered over by editing an expectation.
            Assert.IsEmpty(lost,
                "The shipped Gaze profile does not load as authored, which means its file is written " +
                "against a layout the code no longer has:\n  " + string.Join("\n  ", lost));

            Assert.Greater(compared, 90,
                $"Only {compared} of the profile's settings were found in the asset file. The asset " +
                "is out of step with the type — re-author it rather than lowering this bound.");
        }

        [Test]
        public void TheShippedSampleProfile_AuthorsItsTables()
        {
            var profile = AssetDatabase.LoadAssetAtPath<ConvaiGazeProfile>(ShippedProfileAssetPath);
            Assert.IsNotNull(profile);

            // The two list settings are the ones the scalar sweep above cannot reach, and an empty
            // state table is the failure that reads as "the character stopped looking at me".
            Assert.IsNotEmpty(profile.StatePolicies, "The shipped profile has no conversation-state table.");
            Assert.IsNotEmpty(profile.EmotionModifiers, "The shipped profile has no emotion modifiers.");

            foreach (GazeStatePolicy policy in profile.StatePolicies)
                Assert.That(policy.Engagement, Is.InRange(0f, 1f),
                    $"State {policy.State} has an out-of-range engagement, so the file did not load as authored.");
        }

        // ── YAML reading ─────────────────────────────────────────────────────

        // Four spaces: a setting sits inside its block. The lowercase-start requirement keeps list
        // element fields (State, Engagement, …) and Unity's own m_ keys out of the sweep.
        private static readonly Regex ScalarLine =
            new(@"^    ([a-z][A-Za-z0-9_]*): (-?[0-9][0-9.eE+-]*)\s*$", RegexOptions.Compiled);

        private static Dictionary<string, string> ReadScalars(string fullPath)
        {
            var values = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(fullPath))
            {
                Match match = ScalarLine.Match(line);
                if (match.Success) values[match.Groups[1].Value] = match.Groups[2].Value;
            }

            return values;
        }

        private static bool TryParse(string raw, Type type, out object value)
        {
            value = null;

            if (type == typeof(float))
            {
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return false;
                value = f;
                return true;
            }

            if (type == typeof(bool))
            {
                if (!int.TryParse(raw, out int b)) return false;
                value = b != 0;
                return true;
            }

            if (type.IsEnum)
            {
                if (!int.TryParse(raw, out int e)) return false;
                value = Enum.ToObject(type, e);
                return true;
            }

            return false; // LayerMask serializes as a block; lists have their own test
        }

        private static bool NearlyEqual(object expected, object actual)
        {
            if (expected is float a && actual is float b) return Mathf.Abs(a - b) <= 1e-4f * Mathf.Max(1f, Mathf.Abs(a));
            return Equals(expected, actual);
        }
    }
}
