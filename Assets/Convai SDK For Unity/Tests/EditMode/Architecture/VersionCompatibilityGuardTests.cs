using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps the package compiling on its declared minimum editor without needing that editor.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The package supports Unity 6000.0 and up, but development happens on a much newer
    ///         editor where every above-floor API compiles happily. That asymmetry is exactly how the
    ///         package once drifted to compiling on 6000.4 alone — broken below it (missing APIs) and
    ///         above it (obsolete-as-error). A compatibility rule that can only be checked by booting
    ///         six editors will not be checked; these tests turn it into a red result in the normal
    ///         EditMode run, in seconds.
    ///     </para>
    ///     <para>
    ///         The rules are deliberately textual. They do not prove the package runs on its minimum
    ///         editor — only compiling it on that editor proves that. What they prove is that nobody
    ///         reintroduced the specific API forms known to break it, which is the failure mode that
    ///         actually recurs.
    ///     </para>
    /// </remarks>
    public class VersionCompatibilityGuardTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath,
            "..",
            "Packages",
            "com.convai.convai-sdk-for-unity"));

        private static string ToRelativePath(string fullPath) =>
            Path.GetRelativePath(PackageRoot, fullPath).Replace('\\', '/');

        /// <summary>
        ///     Files allowed to name a version-sensitive API directly: the seams themselves, plus the
        ///     guard that has to spell the forbidden text out in order to look for it.
        /// </summary>
        /// <remarks>
        ///     Kept honest by <see cref="SeamFileExemptions_AreAllStillNeeded" />. An entry naming a
        ///     file that does not exist, or one that no longer contains any forbidden API, is a
        ///     permanent unguarded hole in the exact list whose job is to have no holes.
        /// </remarks>
        private static readonly string[] SeamFiles =
        {
            "SDK/SharedUnity/Compatibility/ConvaiObjectFind.cs",
            "SDK/SharedUnity/Compatibility/ConvaiObjectId.cs",
            "SDK/SharedUnity/Compatibility/ConvaiSceneId.cs",

            // The editor half of the identity seam. It exists because the runtime half lives in a
            // runtime assembly and so resolves loaded objects only; this one reaches the asset
            // database. EditorAssemblies_ResolveIdsThroughTheEditorSeam keeps editor code on it.
            "SDK/Editor/Compatibility/ConvaiEditorObjectId.cs",

            "Tests/EditMode/Architecture/VersionCompatibilityGuardTests.cs",

            // Feeds the identity seam a genuine historical instance ID (reflectively, because no
            // supported call syntax still produces one on 6000.5) to prove the legacy MCP id
            // fallback keeps working.
            "Tests/EditMode/AI/ConvaiMcpIdRoundTripTests.cs"
        };

        /// <summary>
        ///     Each entry: the API form that must not appear, and what to write instead. Availability
        ///     per editor was measured from each version's <c>UnityEngine.CoreModule.dll</c> and is
        ///     summarised on the seams themselves; extend this list when a new API splits the range,
        ///     and the failure message will tell the next person what to do.
        /// </summary>
        private static readonly (string Label, Regex Pattern, string Replacement)[] ForbiddenApis =
        {
            ("FindObjectsByType", new Regex(@"\bFindObjectsByType\s*<", RegexOptions.Compiled),
                "ConvaiObjectFind.All<T>(includeInactive) — the short overloads do not exist below " +
                "6000.4 and the FindObjectsSortMode ones are deprecated from 6000.4 onward"),

            // Same migration: its "first" is instance-ID order, which EntityId does not keep.
            // Deprecated from 6000.4 and still only a warning on 6000.6, which is how it slipped
            // into the samples. FindAnyObjectByType exists on every supported editor.
            ("FindFirstObjectByType", new Regex(@"\bFindFirstObjectByType\s*[<(]", RegexOptions.Compiled),
                "FindAnyObjectByType<T>() — FindFirstObjectByType is deprecated from 6000.4 because " +
                "it relies on instance-ID ordering"),

            // The older spellings of the same search, deprecated on every supported editor.
            ("FindObjectOfType", new Regex(@"\bFindObjectsOfType\s*[<(]|\bFindObjectOfType\s*[<(]", RegexOptions.Compiled),
                "FindAnyObjectByType<T>() or ConvaiObjectFind.All<T>(includeInactive) — " +
                "FindObjectOfType and FindObjectsOfType are deprecated on every supported editor"),

            // Whole-word rather than dot-prefixed: an unqualified GetInstanceID() inside a
            // Component is the same API and the same 6000.5 error, and it is the form that once
            // shipped past this guard. Reflective lookups such as GetMethod("GetInstanceID", ...)
            // are not matched, because the name is followed by a quote rather than a paren.
            ("GetInstanceID", new Regex(@"\bGetInstanceID\s*\(", RegexOptions.Compiled),
                "ConvaiObjectId.Of(value) — GetInstanceID() is obsolete-as-error on 6000.5"),

            ("GetEntityId", new Regex(@"\.GetEntityId\s*\(", RegexOptions.Compiled),
                "ConvaiObjectId.Of(value) — EntityId does not exist below 6000.2"),

            // Whole-word, so it catches the generic-argument form the package originally used
            // (HashSet<EntityId>) as well as EntityId.ToULong(...). It does not match
            // EntityIdToObject (covered by its own rule) or a lower-case local named entityId.
            ("EntityId", new Regex(@"\bEntityId\b", RegexOptions.Compiled),
                "ConvaiObjectId (long ids) — EntityId does not exist below 6000.2 and its int " +
                "conversion is obsolete-as-error on 6000.5"),

            // Deliberately any ".handle" access, not just one spelled on an identifier ending in
            // "scene": SceneManager.GetActiveScene().handle, (scene).handle and level.handle are
            // the same API and the same 6000.5 error. No non-scene member named handle exists in
            // the package outside Plugins/, which this scan already excludes, so the broad form
            // costs nothing today and fails loudly rather than silently if that ever changes.
            ("Scene.handle", new Regex(@"\.handle\b", RegexOptions.Compiled),
                "ConvaiSceneId.Of(scene) — Scene.handle is a SceneHandle struct from 6000.3 and stops " +
                "converting to int on 6000.5"),

            // The type does not exist below 6000.3, so naming it at all breaks the floor. Whole-word,
            // which leaves locals such as "enumeratedSceneHandles" alone: there is no word boundary
            // inside an identifier.
            ("SceneHandle", new Regex(@"\bSceneHandle\b", RegexOptions.Compiled),
                "ConvaiSceneId (long ids) — SceneHandle does not exist below 6000.3"),

            ("InstanceIDToObject", new Regex(@"\bInstanceIDToObject\s*\(", RegexOptions.Compiled),
                "ConvaiObjectId.TryResolve(id, out value)"),

            ("EntityIdToObject", new Regex(@"\bEntityIdToObject\s*\(", RegexOptions.Compiled),
                "ConvaiObjectId.TryResolve(id, out value)")
        };

        /// <summary>
        ///     Every C# file the package ships, except third-party code. Shipped samples are
        ///     included on purpose: a customer imports and compiles them on their own editor, so
        ///     sample code has to hold the floor exactly like SDK code does. Third-party trees are
        ///     matched anywhere in the path, because vendored code also lives under
        ///     <c>Samples/LipSyncSample/Plugins/</c>.
        /// </summary>
        private static IEnumerable<string> PackageSourceFiles() =>
            Directory
                .EnumerateFiles(PackageRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string relative = ToRelativePath(path);
                    return !relative.StartsWith("Plugins/", StringComparison.Ordinal) &&
                           !relative.Contains("/Plugins/", StringComparison.Ordinal);
                });

        /// <summary>
        ///     Editor code must resolve object ids through <c>ConvaiEditorObjectId</c>, not through the
        ///     runtime seam directly.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The runtime seam lives in a runtime assembly, so it can only reach objects Unity
        ///         already has loaded. The editor half also loads from the asset database. Editor
        ///         tooling holds ids across domain reloads and scene changes, so it needs the second
        ///         one — and the difference is invisible until it is not: a call site on the runtime
        ///         path works every time it is tried by hand and fails whenever the asset happens to
        ///         have been unloaded.
        ///     </para>
        ///     <para>
        ///         A rule rather than a convention, because that failure mode does not reproduce on
        ///         demand and so would never be caught in review.
        ///     </para>
        /// </remarks>
        [Test]
        public void EditorAssemblies_ResolveIdsThroughTheEditorSeam()
        {
            var runtimeResolve = new Regex(@"\bConvaiObjectId\s*\.\s*TryResolve\b", RegexOptions.Compiled);
            var offenders = new List<string>();

            foreach (string path in PackageSourceFiles())
            {
                string relative = ToRelativePath(path);
                if (SeamFiles.Contains(relative, StringComparer.Ordinal)) continue;
                if (!relative.Contains("/Editor/", StringComparison.Ordinal)) continue;

                string[] lines = PackageFiles.ReadAllText(path).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!runtimeResolve.IsMatch(lines[i])) continue;
                    offenders.Add($"{relative}:{i + 1}");
                }
            }

            Assert.IsEmpty(
                offenders,
                "Editor code must call ConvaiEditorObjectId.TryResolve, which also resolves assets the " +
                "editor has unloaded. ConvaiObjectId.TryResolve reaches loaded objects only, and the " +
                "difference only shows up intermittently:\n  " + string.Join("\n  ", offenders));
        }

        [Test]
        public void PackageSources_UseCompatibilitySeams_NotVersionSensitiveUnityApis()
        {
            var offenders = new List<string>();

            foreach (string path in PackageSourceFiles())
            {
                string relative = ToRelativePath(path);
                if (SeamFiles.Contains(relative, StringComparer.Ordinal)) continue;

                string[] lines = PackageFiles.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    // Documentation may name the underlying API; only real code is a violation.
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("///", StringComparison.Ordinal) ||
                        trimmed.StartsWith("*", StringComparison.Ordinal))
                        continue;

                    foreach ((string label, Regex pattern, string replacement) in ForbiddenApis)
                    {
                        if (!pattern.IsMatch(line)) continue;
                        offenders.Add($"{relative}:{i + 1} uses {label} — use {replacement}.");
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "Version-sensitive Unity APIs must go through the Compatibility seams so the package " +
                "keeps compiling on Unity 6000.0 through 6000.6.\n" +
                "Add \"using Convai.Shared.Compatibility;\" to the file. The seams are internal, so if " +
                "the name still will not resolve, your assembly also needs an InternalsVisibleTo entry " +
                "in SDK/SharedUnity/AssemblyInfo.cs — they are SDK plumbing, not customer API.\n  " +
                string.Join("\n  ", offenders));
        }

        [Test]
        public void SeamFileExemptions_AreAllStillNeeded()
        {
            var stale = new List<string>();

            foreach (string relative in SeamFiles)
            {
                string full = Path.Combine(PackageRoot, relative.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(full))
                {
                    stale.Add($"{relative} — exempted, but the file no longer exists.");
                    continue;
                }

                bool stillNamesOne = PackageFiles.ReadAllLines(full)
                    .Any(line => ForbiddenApis.Any(api => api.Pattern.IsMatch(line)));

                if (!stillNamesOne)
                    stale.Add($"{relative} — exempted, but it no longer names any version-sensitive API. Remove it.");
            }

            Assert.IsEmpty(stale,
                "This list is the one set of files allowed to use the APIs everything else is forbidden " +
                "to use, so a stale entry is a permanent hole in the guard rather than a tidiness " +
                "problem:\n  " + string.Join("\n  ", stale));
        }

        // -------------------------------------------------------------------------------------
        // The declared floor
        // -------------------------------------------------------------------------------------

        /// <summary>The minimum editor the package supports, and the only 6000.0 build it is compiled on.</summary>
        private const string FloorUnity = "6000.0";

        private const string FloorUnityRelease = "80f1";

        /// <summary>The floor as a customer reads it in the editor installer: <c>6000.0.80f1</c>.</summary>
        private static string DeclaredFloor => $"{FloorUnity}.{FloorUnityRelease}";

        /// <summary>What a package manifest says its minimum editor is.</summary>
        private readonly struct FloorDeclaration
        {
            internal FloorDeclaration(string unity, string unityRelease)
            {
                Unity = unity;
                UnityRelease = unityRelease;
            }

            private string Unity { get; }
            private string UnityRelease { get; }

            internal bool IsExpectedFloor => Unity == FloorUnity && UnityRelease == FloorUnityRelease;

            internal string Describe =>
                $"unity=\"{Unity ?? "<missing>"}\" unityRelease=\"{UnityRelease ?? "<missing>"}\"";
        }

        /// <summary>
        ///     Reads the declared floor out of manifest text. Split from the on-disk test so the check
        ///     itself can be shown to reject a wrong floor — a guard that has only ever been observed
        ///     to pass has not been shown to guard anything.
        /// </summary>
        private static FloorDeclaration ReadFloor(string manifestText)
        {
            Match unity = Regex.Match(manifestText, "\"unity\"\\s*:\\s*\"(?<value>[^\"]+)\"");
            Match release = Regex.Match(manifestText, "\"unityRelease\"\\s*:\\s*\"(?<value>[^\"]+)\"");

            return new FloorDeclaration(
                unity.Success ? unity.Groups["value"].Value : null,
                release.Success ? release.Groups["value"].Value : null);
        }

        [Test]
        public void PackageManifest_DeclaresTheSupportedFloor()
        {
            string manifestPath = Path.Combine(PackageRoot, "package.json");
            Assert.IsTrue(File.Exists(manifestPath), $"package.json not found at {manifestPath}");

            FloorDeclaration declared = ReadFloor(PackageFiles.ReadAllText(manifestPath));

            Assert.IsTrue(declared.IsExpectedFloor,
                $"package.json declares {declared.Describe}, but the supported floor is " +
                $"{DeclaredFloor}. Both halves matter: Package Manager refuses to install on an " +
                "editor older than unity and unityRelease combined, so a wrong unityRelease turns " +
                "away editors the package supports — or admits editors it was never compiled on. " +
                "Changing this value means re-running the six-editor compatibility matrix, not " +
                "just editing the string.");
        }

        [Test]
        public void ManifestFloorCheck_RejectsAWrongFloor()
        {
            Assert.IsTrue(ReadFloor("{\"unity\": \"6000.0\", \"unityRelease\": \"80f1\"}").IsExpectedFloor,
                "The correct floor must be accepted, or the other cases prove nothing.");

            Assert.IsFalse(ReadFloor("{\"unity\": \"2023.1\", \"unityRelease\": \"1f1\"}").IsExpectedFloor,
                "A pre-Unity-6 floor must be rejected.");

            Assert.IsFalse(ReadFloor("{\"unity\": \"6000.0\", \"unityRelease\": \"20f1\"}").IsExpectedFloor,
                "A 6000.0 patch below the one the package is compiled on must be rejected — this is " +
                "the half that went unchecked while only \"unity\" was asserted.");

            Assert.IsFalse(ReadFloor("{\"unity\": \"6000.0\"}").IsExpectedFloor,
                "A manifest with no unityRelease must be rejected.");

            Assert.IsFalse(ReadFloor("{\"name\": \"com.convai.convai-sdk-for-unity\"}").IsExpectedFloor,
                "A manifest with no floor at all must be rejected.");
        }

        // -------------------------------------------------------------------------------------
        // Documentation must not contradict the manifest
        // -------------------------------------------------------------------------------------

        /// <summary>
        ///     A precise editor version, as a customer would read it — <c>6000.0.80f1</c>, <c>2023.1</c>.
        ///     Deliberately not prose: "Unity 6" in a sentence is not a floor claim, and a rule that
        ///     tries to decide which sentences are floor claims fails in both directions.
        /// </summary>
        private static readonly Regex EditorVersionMention =
            new(@"\b(?:2019|2020|2021|2022|2023|6000)\.\d+(?:\.\d+[abfp]\d+)?\b", RegexOptions.Compiled);

        /// <summary>
        ///     Shipped pages that name an editor version without naming the floor, each with the
        ///     reason that is correct. <see cref="DocumentationFloorExemptions_AreAllStillNeeded" />
        ///     fails when one stops applying, so the list cannot rot into permanent holes.
        /// </summary>
        private static readonly (string Path, string Justification)[] DocumentationFloorExemptions =
        {
            ("AIAssistantSkills/convai-unity-sdk/SKILL.md",
                "required_editor_version is a machine-read version range in Unity's AI Assistant " +
                "skill schema, which takes MAJOR.MINOR.PATCH. \"80f1\" is not a patch number, so the " +
                "floor cannot be expressed there. The declared \">=6000.0.0\" is looser than the " +
                "package's real floor rather than wrong: an editor that satisfies the manifest also " +
                "satisfies this.")
        };

        private static IEnumerable<string> ShippedDocumentation() =>
            Directory
                .EnumerateFiles(PackageRoot, "*.md", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string relative = ToRelativePath(path);
                    return !relative.StartsWith("Plugins/", StringComparison.Ordinal) &&
                           !relative.Contains("/Plugins/", StringComparison.Ordinal) &&
                           !relative.StartsWith("Documentation~/plans/", StringComparison.Ordinal);
                });

        [Test]
        public void ShippedDocumentation_DoesNotContradictTheDeclaredFloor()
        {
            var offenders = new List<string>();

            foreach (string path in ShippedDocumentation())
            {
                string relative = ToRelativePath(path);
                if (DocumentationFloorExemptions.Any(e =>
                        string.Equals(e.Path, relative, StringComparison.Ordinal)))
                    continue;

                string text = PackageFiles.ReadAllText(path);
                if (!EditorVersionMention.IsMatch(text)) continue;
                if (text.Contains(DeclaredFloor, StringComparison.Ordinal)) continue;

                offenders.Add($"{relative} names an editor version but never names the floor {DeclaredFloor}.");
            }

            Assert.IsEmpty(offenders,
                "Every shipped page that talks about editor versions has to agree with package.json. " +
                $"A page promising an older editor than {DeclaredFloor} sends a customer to install " +
                "the package and be refused by Package Manager, with nothing to tell them why:\n  " +
                string.Join("\n  ", offenders));
        }

        [Test]
        public void DocumentationFloorExemptions_AreAllStillNeeded()
        {
            var stale = new List<string>();

            foreach ((string relative, string _) in DocumentationFloorExemptions)
            {
                string full = Path.Combine(PackageRoot, relative.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(full))
                {
                    stale.Add($"{relative} — exempted, but the file no longer exists.");
                    continue;
                }

                string text = PackageFiles.ReadAllText(full);
                bool wouldFail = EditorVersionMention.IsMatch(text) &&
                                 !text.Contains(DeclaredFloor, StringComparison.Ordinal);

                if (!wouldFail)
                    stale.Add($"{relative} — exempted, but it no longer needs the exemption. Remove it.");
            }

            Assert.IsEmpty(stale,
                "The documentation exemption list has entries that are no longer earning their place:\n  " +
                string.Join("\n  ", stale));
        }
    }
}
