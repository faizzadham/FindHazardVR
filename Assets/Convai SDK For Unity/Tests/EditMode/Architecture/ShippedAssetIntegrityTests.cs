using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Convai.Editor.Utilities;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Reference-integrity guards for the assets this package actually ships.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Unity does not fail a build — or even a domain reload — when a serialized GUID
    ///         stops resolving. A preset slot pointing at a deleted profile silently falls back
    ///         to the module's runtime default, and a component whose script was removed shows
    ///         up only as a "missing script" box in the Inspector of whoever opens the scene.
    ///         Both failure modes shipped undetected before these tests existed: the shared
    ///         embodiment preset's Gaze slot pointed at a nonexistent profile, and the LipSync
    ///         Sample carried components from two long-removed modules.
    ///     </para>
    ///     <para>
    ///         These are file-scanning tests on purpose. They read the shipped YAML rather than
    ///         the loaded object graph, so they catch a broken reference even when Unity has
    ///         already resolved it away to a default and nothing in play mode looks wrong.
    ///     </para>
    /// </remarks>
    [Category("Architecture")]
    public sealed class ShippedAssetIntegrityTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        /// <summary>Folders whose assets are shipped to and opened by customers.</summary>
        private static readonly string[] ShippedAssetFolders =
        {
            "SamplesShared", "Samples", "Prefabs", "Resources"
        };

        private static readonly Regex GuidReference = new(
            @"guid: (?<guid>[0-9a-f]{32})", RegexOptions.Compiled);

        /// <summary>A MonoBehaviour whose serialized class name identifies it as ours.</summary>
        private static readonly Regex ConvaiScriptReference = new(
            @"m_Script: \{fileID: \d+, guid: (?<guid>[0-9a-f]{32}), type: 3\}[\r\n]+" +
            @"\s*m_Name:[^\r\n]*[\r\n]+" +
            @"\s*m_EditorClassIdentifier: (?<class>Convai[^\r\n]*)",
            RegexOptions.Compiled);

        private static readonly Regex PresetProfileSlot = new(
            @"moduleId: (?<module>[^\r\n]+)[\r\n]+" +
            @"\s*profile: \{fileID: \d+, guid: (?<guid>[0-9a-f]{32}), type: \d+\}",
            RegexOptions.Compiled);

        private static readonly Regex ComponentEntry = new(
            @"- component: \{fileID: (?<id>\d+)\}", RegexOptions.Compiled);

        [Test]
        public void ClientVadModel_DeclaresItsInferenceImporterDependency()
        {
            string modelPath = Path.Combine(
                PackageRoot,
                "SDK",
                "Modules",
                "ClientVoiceActivity",
                "Sentis",
                "Resources",
                "Convai",
                "SileroVad16k.onnx");
            Assert.IsTrue(File.Exists(modelPath), $"Client VAD model not found at {modelPath}");

            string manifest = PackageFiles.ReadAllText(Path.Combine(PackageRoot, "package.json"));
            Match dependency = Regex.Match(
                manifest,
                @"""com\.unity\.ai\.inference""\s*:\s*""(?<version>[^""]+)""");

            Assert.IsTrue(dependency.Success,
                "The shipped Silero ONNX model requires com.unity.ai.inference. Declaring it only " +
                "in the development project's Packages/manifest.json masks a broken UPM install: " +
                "customers do not inherit the host project's dependencies.");
            Assert.That(dependency.Groups["version"].Value, Is.EqualTo("2.2.1"),
                "Keep the package dependency aligned with the Unity.InferenceEngine API and " +
                "ONNX importer version used by the client VAD module.");
        }

        /// <summary>
        ///     Every GUID declared by a <c>.meta</c> anywhere in the package. Built once —
        ///     the package carries a few thousand meta files and each test would otherwise
        ///     re-walk the tree.
        /// </summary>
        private static HashSet<string> _packageGuids;

        private static HashSet<string> PackageGuids
        {
            get
            {
                if (_packageGuids != null) return _packageGuids;

                _packageGuids = new HashSet<string>(StringComparer.Ordinal);
                foreach (string meta in Directory.EnumerateFiles(PackageRoot, "*.meta", SearchOption.AllDirectories))
                {
                    Match match = GuidReference.Match(PackageFiles.ReadAllText(meta));
                    if (match.Success) _packageGuids.Add(match.Groups["guid"].Value);
                }

                return _packageGuids;
            }
        }

        private static IEnumerable<string> ShippedAssetFiles()
        {
            foreach (string folder in ShippedAssetFolders)
            {
                string path = Path.Combine(PackageRoot, folder);
                if (!Directory.Exists(path)) continue;

                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    string extension = Path.GetExtension(file);
                    if (extension is ".asset" or ".prefab" or ".unity")
                        yield return file;
                }
            }
        }

        /// <summary>
        ///     A TextMeshPro font asset's back-pointer to the source <c>.ttf</c> it was baked
        ///     from. Editor-only and intentionally unresolvable in a shipped package.
        /// </summary>
        private static bool IsFontSourceReference(string line) =>
            line.Contains("m_SourceFontFileGUID", StringComparison.Ordinal) ||
            line.Contains("m_SourceFontFile_EditorRef", StringComparison.Ordinal) ||
            line.Contains("sourceFontFileGUID", StringComparison.Ordinal);

        /// <summary>
        ///     TextMesh Pro's runtime shader and default font, which no package ships.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         These two resolve in any project that has imported TMP Essential Resources and in
        ///         none that has not, because Unity unpacks them into the consuming project's own
        ///         <c>Assets/TextMesh Pro/</c> folder. There is no UPM dependency that expresses
        ///         "and the user must also run this menu item", and redistributing Unity's shader
        ///         inside this package is not ours to do — so the dependency is real, unavoidable,
        ///         and therefore declared here rather than left to fail the guard forever.
        ///     </para>
        ///     <para>
        ///         An exemption is only honest if the user is told. They are:
        ///         <c>ConvaiSceneSetupApi.ValidateCurrentScene</c> reports the missing import as an
        ///         error, <c>GameObject > Convai > Validate Scene Setup</c> offers to run Unity's
        ///         importer, and <c>SETUP.md</c> lists it as a prerequisite. If that check is ever
        ///         removed, this exemption stops being defensible and should go with it.
        ///     </para>
        ///     <para>
        ///         The guids come from the check itself so the two cannot drift apart, and nothing
        ///         else is exempt: a dangling reference to anything Convai owns still fails.
        ///     </para>
        /// </remarks>
        private static bool IsTextMeshProEssentialsReference(string guid) =>
            guid == ConvaiTextMeshProEssentials.SdfShaderGuid ||
            guid == ConvaiTextMeshProEssentials.DefaultFontGuid;

        private static string Relative(string absolutePath) =>
            absolutePath.Substring(PackageRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');

        /// <summary>
        ///     A <c>ConvaiEmbodimentPreset</c> slot that does not resolve is worse than an
        ///     empty slot: the preset advertises a profile, the module silently uses its
        ///     built-in defaults instead, and nothing anywhere says so.
        /// </summary>
        [Test]
        public void EmbodimentPresetProfileSlots_AllResolveToShippedAssets()
        {
            var broken = new List<string>();

            foreach (string file in ShippedAssetFiles())
            {
                if (Path.GetExtension(file) != ".asset") continue;

                string text = PackageFiles.ReadAllText(file);
                if (!text.Contains("ConvaiEmbodimentPreset")) continue;

                foreach (Match slot in PresetProfileSlot.Matches(text))
                {
                    string guid = slot.Groups["guid"].Value;
                    if (PackageGuids.Contains(guid)) continue;

                    broken.Add(
                        $"{Relative(file)}: module '{slot.Groups["module"].Value.Trim()}' " +
                        $"points at missing profile guid {guid}");
                }
            }

            Assert.IsEmpty(broken,
                "Embodiment preset slots must reference a profile asset that ships with the package. " +
                "A dangling guid degrades silently to the module's runtime default:\n" +
                string.Join("\n", broken));
        }

        /// <summary>
        ///     Catches components left behind by a removed or renamed module — the residue that
        ///     renders as "The associated script can not be loaded" the first time a customer
        ///     opens the sample.
        /// </summary>
        [Test]
        public void ShippedScenesAndPrefabs_ReferenceNoRemovedConvaiScripts()
        {
            var missing = new List<string>();

            foreach (string file in ShippedAssetFiles())
            {
                string text = PackageFiles.ReadAllText(file);
                foreach (Match reference in ConvaiScriptReference.Matches(text))
                {
                    string guid = reference.Groups["guid"].Value;
                    if (PackageGuids.Contains(guid)) continue;

                    missing.Add($"{Relative(file)}: {reference.Groups["class"].Value.Trim()} (guid {guid})");
                }
            }

            Assert.IsEmpty(missing,
                "A shipped scene/prefab references a Convai script that no longer exists in the " +
                "package. Remove the leftover component rather than restoring the type:\n" +
                string.Join("\n", missing));
        }

        /// <summary>
        ///     The catch-all: every asset reference in a shipped scene/prefab/asset must resolve.
        /// </summary>
        /// <remarks>
        ///     Unlike the file-scanning guards above, this one asks <see cref="AssetDatabase" />,
        ///     so a reference into another package (TextMeshPro shaders, URP volume components)
        ///     resolves correctly instead of reading as broken. Unity's built-in resources use
        ///     all-zero guid prefixes and are skipped — they never resolve through the asset
        ///     database by design, and a TextMeshPro font asset's pointer back to the source
        ///     <c>.ttf</c> it was baked from is exempt: the SDF atlas is embedded in the asset,
        ///     the source font is deliberately not redistributed, and the reference exists only
        ///     so a licensed user can regenerate the atlas. TextMesh Pro's runtime shader and
        ///     default font are exempt for the reason given on
        ///     <see cref="IsTextMeshProEssentialsReference" /> — they ship in no package at all.
        /// </remarks>
        [Test]
        public void ShippedAssets_ContainNoUnresolvableAssetReferences()
        {
            const string builtInGuidPrefix = "0000000000000000";
            var unresolved = new List<string>();

            foreach (string file in ShippedAssetFiles())
            {
                var reported = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in File.ReadLines(file))
                {
                    if (IsFontSourceReference(line)) continue;

                    foreach (Match reference in GuidReference.Matches(line))
                    {
                        string guid = reference.Groups["guid"].Value;
                        if (guid.StartsWith(builtInGuidPrefix, StringComparison.Ordinal)) continue;
                        if (IsTextMeshProEssentialsReference(guid)) continue;
                        if (!reported.Add(guid)) continue;
                        if (!string.IsNullOrEmpty(UnityEditor.AssetDatabase.GUIDToAssetPath(guid))) continue;

                        unresolved.Add($"{Relative(file)}: guid {guid}");
                    }
                }
            }

            Assert.IsEmpty(unresolved,
                "A shipped asset references something that no longer exists. Clear the reference or " +
                "restore the asset — a dangling reference reads as an authored intent that silently " +
                "does nothing:\n" + string.Join("\n", unresolved));
        }

        /// <summary>
        ///     The TextMesh Pro exemption above is justified by the user being told. This checks
        ///     that they still are.
        /// </summary>
        /// <remarks>
        ///     Exempting a guid removes a guard; the only thing that keeps it from being
        ///     concealment is the edit-time check that reports the missing import and offers to run
        ///     it. Deleting that check would leave a customer with a NullReferenceException on
        ///     scene open and a test suite that says everything is fine — so the exemption and the
        ///     check fail together, on purpose. File-scanned rather than invoked because the
        ///     resources are present in this project, so the code path cannot be exercised here.
        /// </remarks>
        [Test]
        public void TextMeshProEssentialsExemption_IsBackedByAnEditTimeCheck()
        {
            string setupApi = PackageFiles.ReadAllText(Path.Combine(
                PackageRoot, "SDK", "Editor", "ConvaiSceneSetupApi.cs"));

            Assert.That(setupApi, Does.Contain(nameof(ConvaiTextMeshProEssentials)),
                "ConvaiSceneSetupApi must still report a missing TextMesh Pro Essentials import. " +
                "Without it, ShippedAssets_ContainNoUnresolvableAssetReferences exempts two guids " +
                "and nothing anywhere tells the user the dependency exists.");

            string wizard = PackageFiles.ReadAllText(Path.Combine(
                PackageRoot, "SDK", "Editor", "ConvaiSetupWizard.cs"));

            Assert.That(wizard, Does.Contain(nameof(ConvaiTextMeshProEssentials.TryImport)),
                "The setup wizard must still offer to run TextMesh Pro's importer. Reporting a " +
                "problem the user then has to go and solve elsewhere is the weaker half of the fix.");
        }

        /// <summary>
        ///     A GameObject listing a component id that no document defines is what a hand-edited
        ///     (or half-merged) scene leaves behind; Unity tolerates it, then drops the entry the
        ///     next time the scene is saved, which makes the damage invisible in review.
        /// </summary>
        [Test]
        public void ShippedScenesAndPrefabs_HaveNoDanglingComponentEntries()
        {
            var dangling = new List<string>();

            foreach (string file in ShippedAssetFiles())
            {
                string extension = Path.GetExtension(file);
                if (extension is not (".prefab" or ".unity")) continue;

                string text = PackageFiles.ReadAllText(file);
                var anchors = new HashSet<string>(
                    Regex.Matches(text, @"^--- !u!\d+ &(?<id>\d+)", RegexOptions.Multiline)
                        .Select(m => m.Groups["id"].Value),
                    StringComparer.Ordinal);

                foreach (Match entry in ComponentEntry.Matches(text))
                {
                    string id = entry.Groups["id"].Value;
                    if (id == "0" || anchors.Contains(id)) continue;

                    dangling.Add($"{Relative(file)}: component entry {id} has no matching document");
                }
            }

            Assert.IsEmpty(dangling,
                "A shipped scene/prefab lists a component whose document is missing:\n" +
                string.Join("\n", dangling));
        }
    }
}
