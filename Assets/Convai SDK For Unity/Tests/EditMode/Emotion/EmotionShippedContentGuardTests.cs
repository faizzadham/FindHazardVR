using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Guards what the package actually ships for Emotion: no source clinging to a removed
    ///     type, and no personality asset carrying a key the schema no longer has or missing one it
    ///     does.
    /// </summary>
    /// <remarks>
    ///     Both classes of rot had already happened here. Four shipped profiles carried 134 authored
    ///     blendshape slots each that the runtime never read, and the canonically named one was
    ///     missing twenty-three fields including the whole micro-expression layer — absent keys read
    ///     code defaults, so nothing misbehaved and nothing reported it, but the assets were useless
    ///     as starting points to duplicate.
    /// </remarks>
    public sealed class EmotionShippedContentGuardTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        private static string EmotionProfileFolder => Path.Combine(
            PackageRoot, "SamplesShared", "Profiles", "Embodiment", "Modules", "Emotion");

        /// <summary>
        ///     Types and members deleted when the slot-list and animator output paths were retired.
        ///     Source that still names one of these is either dead or about to fail at runtime.
        /// </summary>
        private static readonly string[] RemovedApi =
        {
            "BlendshapeEmotionBinding",
            "AnimatorParameterEmotionBinding",
            "EmotionSlotBinding",
            "NeutralAlternator",
            "RealisticEmotionSlots",
            "SemanticExpressionsEnabled",
            "NeutralAlternationEnabled",
            "CreateBlendshapeRuntimeBinding",
            "CreateAnimatorRuntimeBinding",
        };

        [Test]
        public void NoPackageSource_ReferencesARemovedEmotionType()
        {
            var violations = new List<string>();

            foreach (string path in Directory.EnumerateFiles(
                         Path.Combine(PackageRoot, "SDK"), "*.cs", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(path);
                foreach (string removed in RemovedApi)
                    if (content.Contains(removed))
                        violations.Add($"{Relative(path)} still names '{removed}'");
            }

            Assert.That(violations, Is.Empty, string.Join("\n", violations));
        }

        [Test]
        public void NoShippedDocumentation_ReferencesARemovedEmotionType()
        {
            string docs = Path.Combine(PackageRoot, "Documentation~");
            if (!Directory.Exists(docs)) Assert.Pass("No shipped documentation folder.");

            var violations = new List<string>();
            foreach (string path in Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly))
            {
                string content = File.ReadAllText(path);
                foreach (string removed in RemovedApi)
                    if (content.Contains(removed))
                        violations.Add($"{Relative(path)} still documents '{removed}'");
            }

            Assert.That(violations, Is.Empty,
                "Shipped docs describe API that no longer exists:\n" + string.Join("\n", violations));
        }

        [Test]
        public void EveryShippedPersonality_CarriesExactlyTheCurrentSchema()
        {
            IReadOnlyList<string> expected = SerializedFieldNames();
            var violations = new List<string>();

            foreach (string path in ShippedPersonalities())
            {
                HashSet<string> keys = TopLevelKeys(path);

                List<string> missing = expected.Where(k => !keys.Contains(k)).ToList();
                List<string> unknown = keys.Where(k => !expected.Contains(k)).ToList();

                if (missing.Count > 0)
                    violations.Add($"{Path.GetFileName(path)} is missing: {string.Join(", ", missing)}");
                if (unknown.Count > 0)
                    violations.Add($"{Path.GetFileName(path)} carries retired keys: {string.Join(", ", unknown)}");
            }

            Assert.That(violations, Is.Empty,
                "A shipped personality is only useful as a starting point if it is a complete, current " +
                "snapshot:\n" + string.Join("\n", violations));
        }

        [Test]
        public void EveryShippedPersonality_RestsAtAUsableStrength()
        {
            // One shipped asset rested at full joy intensity (1.0) rather than at its character
            // type's value, so that character sat permanently at maximum expression. The ceiling is
            // the top of the band a resting face can occupy before it reads as a stuck grin; the
            // cheerful character types sit just under it on purpose.
            var violations = new List<string>();

            foreach (string path in ShippedPersonalities())
            {
                var profile = AssetDatabase.LoadAssetAtPath<ConvaiEmotionProfile>(AssetPath(path));
                if (profile == null) continue;

                if (string.IsNullOrWhiteSpace(profile.BaselineEmotionLabel)) continue;
                if (profile.BaselineIntensity > 0.6f)
                    violations.Add($"{profile.name} rests at {profile.BaselineIntensity:0.00} " +
                                   $"of '{profile.BaselineEmotionLabel}'");
            }

            Assert.That(violations, Is.Empty,
                "A resting mood this strong reads as a stuck expression rather than a temperament:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void ShippedPersonalities_AreNamedAfterTheCharacterNotTheRig()
        {
            // Expression is rig-independent, so a rig name in a personality's filename is a promise
            // the asset cannot keep.
            string[] rigTerms = { "CC3", "CC4", "ARKit", "MetaHuman", "Extended" };
            var violations = new List<string>();

            foreach (string path in ShippedPersonalities())
            {
                string name = Path.GetFileNameWithoutExtension(path);
                foreach (string term in rigTerms)
                    if (name.Contains(term))
                        violations.Add($"{name} names the rig convention '{term}'");
            }

            Assert.That(violations, Is.Empty, string.Join("\n", violations));
        }

        // ------------------------------------------------------------------ helpers

        private static IEnumerable<string> ShippedPersonalities() =>
            Directory.Exists(EmotionProfileFolder)
                ? Directory.EnumerateFiles(EmotionProfileFolder, "*.asset")
                    .Where(p => Path.GetFileName(p).Contains("_Emotion_"))
                : Enumerable.Empty<string>();

        private static IReadOnlyList<string> SerializedFieldNames()
        {
            var probe = ScriptableObject.CreateInstance<ConvaiEmotionProfile>();
            try
            {
                var names = new List<string>();
                var serialized = new SerializedObject(probe);
                SerializedProperty iterator = serialized.GetIterator();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyPath == "m_Script") continue;
                    names.Add(iterator.propertyPath);
                }
                return names;
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        /// <summary>Top-level serialized keys in a YAML asset — the two-space-indented ones.</summary>
        private static HashSet<string> TopLevelKeys(string path)
        {
            var keys = new HashSet<string>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (!line.StartsWith("  ") || line.StartsWith("   ")) continue;
                // "  - label: joy" is a list entry, not a top-level key.
                if (line.StartsWith("  -")) continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;

                string key = line.Substring(2, colon - 2).Trim();
                if (key.StartsWith("m_")) continue;
                keys.Add(key);
            }
            return keys;
        }

        private static string AssetPath(string absolute) =>
            "Packages/com.convai.convai-sdk-for-unity/" + Relative(absolute).Replace('\\', '/');

        private static string Relative(string absolute) =>
            absolute.Substring(PackageRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');
    }
}
