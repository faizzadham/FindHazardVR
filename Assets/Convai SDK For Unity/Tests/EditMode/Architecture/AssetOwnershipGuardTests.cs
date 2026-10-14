using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Convai.Editor.Ownership;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     There is exactly one place in this SDK that decides whether a settings asset belongs to
    ///     the user or to the package.
    /// </summary>
    /// <remarks>
    ///     This guard exists because the alternative was measured, not imagined. Body Animation and
    ///     Emotion each grew a private <c>IsEditableProjectAsset</c> that tested a path for
    ///     <c>"Assets/"</c>, and the two had already drifted apart by the time anyone compared them —
    ///     one disabled its controls on an SDK asset, the other left them live and futile. Gaze, Body
    ///     Language and Conversation Flow never grew one at all, so they let the user write to a
    ///     package asset with no warning and no effect.
    ///     <para>
    ///         A path prefix is a seductive one-liner: it is three lines to write and it looks
    ///         obviously correct in isolation. That is precisely why it must not be written a sixth
    ///         time. Ownership is a product decision — what the notice says, whether the copy happens
    ///         for the user or is demanded of them, what the copy is named and where it lands — and a
    ///         module answering it privately re-decides all of that by accident.
    ///     </para>
    /// </remarks>
    public sealed class AssetOwnershipGuardTests
    {
        /// <summary>The folder that owns the question. Everything under it may test a path.</summary>
        private const string OwnershipFolder = "Ownership";

        /// <summary>
        ///     A string test against a project or package root. Deliberately narrow: this catches the
        ///     ownership idiom, not every mention of the words.
        /// </summary>
        private static readonly Regex PathRootCheck = new(
            @"(StartsWith|Contains|IndexOf)\s*\(\s*""(Assets|Packages)/",
            RegexOptions.Compiled);

        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        [Test]
        public void OnlyTheOwnershipSeamDecidesWhetherAnAssetBelongsToTheUser()
        {
            string sdkRoot = Path.Combine(PackageRoot, "SDK");
            if (!Directory.Exists(sdkRoot)) Assert.Ignore("SDK source not present in this project.");

            var offenders = new List<string>();

            foreach (string file in Directory.GetFiles(sdkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains($"/{OwnershipFolder}/")) continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!PathRootCheck.IsMatch(lines[i])) continue;

                    offenders.Add(
                        $"{normalized.Substring(normalized.IndexOf("/SDK/", System.StringComparison.Ordinal))}" +
                        $":{i + 1}  {lines[i].Trim()}");
                }
            }

            Assert.That(
                offenders, Is.Empty,
                "These files decide asset ownership by testing a path themselves. Ask " +
                "ConvaiAssetOwnership instead (IsProjectAsset / IsSdkAsset / Of / OfCached) so every " +
                "module gives the user the same answer, the same wording and the same way out:\n  " +
                string.Join("\n  ", offenders));
        }

        /// <summary>
        ///     An editor area that writes to a settings asset must ask who owns it.
        /// </summary>
        /// <remarks>
        ///     The seam only holds if surfaces route through it, and three of them did not: Gaze's
        ///     dials, Body Language's demeanor presets and LipSync's map editor all wrote straight to
        ///     whatever asset was assigned — including one inside the package, where the write is
        ///     discarded without a word. None of them was doing anything obviously wrong; the
        ///     question simply never came up, because nothing made it come up.
        ///     <para>
        ///         <b>It keys on what the <c>SerializedObject</c> wraps, not on the file it lives
        ///         in.</b> The first draft of this guard keyed on <c>ApplyModifiedProperties</c>
        ///         anywhere in a module and flagged LipSync and Narrative, whose MCP tools write scene
        ///         components — always the user's, never the package's. Chasing that false positive to
        ///         the wrong file is how LipSync got written off as clean twice before the real site,
        ///         its map-asset inspector, turned up.
        ///     </para>
        ///     <para>
        ///         Scoped per editor area rather than per file, because a helper like
        ///         <c>ConvaiLipSyncMapAuthoring</c> legitimately writes a mapping it is handed and is
        ///         guarded by the inspector that calls it. Asking the area to have answered the
        ///         question somewhere is the level at which the answer is actually decided.
        ///     </para>
        /// </remarks>
        [Test]
        public void EveryEditorAreaThatWritesASettingsAssetAsksWhoOwnsIt()
        {
            string sdkRoot = Path.Combine(PackageRoot, "SDK");
            if (!Directory.Exists(sdkRoot)) Assert.Ignore("SDK source not present in this project.");

            // A SerializedObject built over something that reads as a settings asset, captured with
            // the variable it lands in. Components are deliberately absent: a component lives in the
            // user's scene and is always theirs.
            //
            // The variable matters. ConvaiRoomManagerEditor builds one over the room profile purely
            // to READ it and commits its own component's SerializedObject elsewhere in the same file;
            // a guard that only asked "does this file mention ApplyModifiedProperties" called that an
            // offender. Requiring the constructed view to be the one committed is what separates
            // "reads a settings asset" from "writes one".
            var settingsAssetWrite = new Regex(
                @"(?<var>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+SerializedObject\(\s*(?<arg>[A-Za-z_][A-Za-z0-9_\.]*)\s*\)",
                RegexOptions.Compiled);

            var assetLike = new Regex(
                @"(profile|config|mapping|map|taxonomy|preset|asset|settings)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var writingAreas = new Dictionary<string, List<string>>();
            var askingAreas = new HashSet<string>();

            foreach (string file in Directory.GetFiles(sdkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains($"/{OwnershipFolder}/")) continue;

                string text = File.ReadAllText(file);
                string area = AreaOf(normalized);
                if (area == null) continue;

                if (text.Contains("ConvaiAssetOwnership") || text.Contains("ConvaiOwnedEdit") ||
                    text.Contains("ConvaiOwnershipNotice") || text.Contains("ConvaiCopyOnWrite"))
                    askingAreas.Add(area);

                if (!text.Contains("ApplyModifiedProperties")) continue;

                foreach (Match match in settingsAssetWrite.Matches(text))
                {
                    string argument = match.Groups["arg"].Value;
                    string leaf = argument.Substring(argument.LastIndexOf('.') + 1).TrimStart('_');
                    if (!assetLike.IsMatch(leaf)) continue;

                    // Only when this very view is committed. A read-only view of a settings asset is
                    // not a write, however many other things the file applies.
                    if (!text.Contains($"{match.Groups["var"].Value}.ApplyModifiedProperties")) continue;

                    if (!writingAreas.TryGetValue(area, out List<string> sites))
                        writingAreas[area] = sites = new List<string>();

                    string relative = normalized.Substring(
                        normalized.IndexOf("/SDK/", System.StringComparison.Ordinal) + 1);
                    if (!sites.Contains(relative)) sites.Add(relative);
                }
            }

            var silent = writingAreas
                .Where(pair => !askingAreas.Contains(pair.Key))
                .Select(pair => $"{pair.Key} ({string.Join(", ", pair.Value)})")
                .ToList();

            Assert.That(
                silent, Is.Empty,
                "These editor areas write to a settings asset without ever asking who owns it, so a " +
                "write to an asset inside the package goes nowhere and says nothing. Route it through " +
                "ConvaiOwnedEdit (draw paths), ConvaiCopyOnWrite.EnsureWritable (commands), or " +
                "ConvaiOwnershipNotice.BeginAssetEdit (an asset's own inspector):\n  " +
                string.Join("\n  ", silent));
        }

        /// <summary>
        ///     The editor area a file belongs to — a module's editor code, or one area under
        ///     <c>SDK/Editor</c>. Returns <c>null</c> for runtime code, which has no inspectors.
        /// </summary>
        private static string AreaOf(string normalizedPath)
        {
            const string modules = "/SDK/Modules/";
            int moduleIndex = normalizedPath.IndexOf(modules, System.StringComparison.Ordinal);
            if (moduleIndex >= 0)
            {
                string tail = normalizedPath.Substring(moduleIndex + modules.Length);
                int slash = tail.IndexOf('/');
                if (slash < 0) return null;

                string module = tail.Substring(0, slash);
                return tail.Contains("/Editor/") || tail.Contains("/Editor.") ? $"Modules/{module}" : null;
            }

            const string editor = "/SDK/Editor/";
            int editorIndex = normalizedPath.IndexOf(editor, System.StringComparison.Ordinal);
            if (editorIndex < 0) return null;

            string editorTail = normalizedPath.Substring(editorIndex + editor.Length);
            int editorSlash = editorTail.IndexOf('/');
            return editorSlash < 0 ? "Editor" : $"Editor/{editorTail.Substring(0, editorSlash)}";
        }

        /// <summary>
        ///     Every settings asset the SDK ships is inspectable, and an asset inspector has no
        ///     character to copy for — so the read-only guard has to sit on the shared base rather
        ///     than in each of the nine inspectors that derive from it.
        /// </summary>
        [Test]
        public void TheSharedProfileInspectorBaseGuardsSdkOwnedAssets()
        {
            string basePath = Path.Combine(
                PackageRoot, "SDK", "Editor", "Embodiment", "Inspectors", "EmbodimentProfileEditorBase.cs");

            if (!File.Exists(basePath)) Assert.Ignore("Profile editor base not present in this project.");

            Assert.That(
                File.ReadAllText(basePath), Does.Contain("BeginAssetEdit"),
                "The shared profile inspector base no longer guards SDK-owned assets, so every " +
                "settings asset that ships with the SDK is editable in place again — silently, in an " +
                "installed project.");
        }

        /// <summary>
        ///     A read-only notice may say a file cannot be edited here; it may not say who wrote it
        ///     unless that is known.
        /// </summary>
        /// <remarks>
        ///     "Cannot be changed here" is true of anything under <c>Packages/</c>. "Part of the
        ///     Convai SDK" is a claim about provenance, and it was being made about every package —
        ///     so a studio keeping its Convai settings in a package of its own was told its own file
        ///     came from us. The verdict and the provenance are separate claims and only one of them
        ///     generalizes.
        /// </remarks>
        [Test]
        public void TheReadOnlyNoticeOnlyClaimsConvaiForConvaisOwnContent()
        {
            Assert.That(
                ConvaiAssetOwnership.ReadOnlyTitle(false), Does.Not.Contain("Convai"),
                "An asset in someone else's package is reported as part of the Convai SDK.");
            Assert.That(
                ConvaiAssetOwnership.ReadOnlyLead(false), Does.Not.Contain("Convai"),
                "An asset in someone else's package is described as shipping with the Convai SDK.");

            Assert.That(ConvaiAssetOwnership.ReadOnlyTitle(true), Does.Contain("Convai"));
            Assert.That(ConvaiAssetOwnership.ReadOnlyLead(true), Does.Contain("Convai"));
        }

        /// <summary>
        ///     Both surfaces that explain a read-only asset take that wording from one place.
        /// </summary>
        /// <remarks>
        ///     A second hand-written copy would be right on the day it was typed and wrong the first
        ///     time the sentence was improved — which is the whole failure this seam exists to end,
        ///     reproduced inside the seam itself.
        /// </remarks>
        [Test]
        public void TheReadOnlyWordingIsWrittenInExactlyOnePlace()
        {
            string sdkRoot = Path.Combine(PackageRoot, "SDK");
            if (!Directory.Exists(sdkRoot)) Assert.Ignore("SDK source not present in this project.");

            const string ownershipDeclaration = "ReadOnlyTitle(bool";
            var offenders = new List<string>();

            foreach (string file in Directory.GetFiles(sdkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (!text.Contains("part of the Convai SDK") || text.Contains(ownershipDeclaration))
                    continue;

                string normalized = file.Replace('\\', '/');
                offenders.Add(normalized.Substring(
                    normalized.IndexOf("/SDK/", System.StringComparison.Ordinal)));
            }

            Assert.That(
                offenders, Is.Empty,
                "These files spell the read-only notice out themselves instead of asking " +
                "ConvaiAssetOwnership.ReadOnlyTitle / ReadOnlyLead, so they will keep claiming an " +
                "asset came from Convai after the shared wording learns better:\n  " +
                string.Join("\n  ", offenders));
        }
    }
}
