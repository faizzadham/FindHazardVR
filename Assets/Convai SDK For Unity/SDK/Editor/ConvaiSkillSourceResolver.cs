using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Convai.Editor
{
    /// <summary>How the Convai SDK reached this project. It decides nothing; it labels the source.</summary>
    internal enum ConvaiSkillSourceLayout
    {
        EmbeddedPackage,
        PackageCache,
        Assets
    }

    /// <summary>One place the SDK skill could live, before anything has been validated.</summary>
    internal readonly struct ConvaiSkillSourceCandidate
    {
        internal ConvaiSkillSourceCandidate(string sdkRoot, ConvaiSkillSourceLayout layout, string declaredVersion)
        {
            SdkRoot = sdkRoot;
            Layout = layout;
            DeclaredVersion = declaredVersion;
        }

        internal string SdkRoot { get; }
        internal ConvaiSkillSourceLayout Layout { get; }

        /// <summary>Version Unity already knows for this candidate, when the layout has one.</summary>
        internal string DeclaredVersion { get; }
    }

    /// <summary>
    ///     A validated, physical skill source: the folder Claude and legacy instruction files can
    ///     actually open. Unity's logical <c>Packages/&lt;name&gt;</c> path is not one of those.
    /// </summary>
    internal readonly struct ConvaiSkillSource
    {
        internal ConvaiSkillSource(
            string sdkRoot,
            string skillRoot,
            string skillFilePath,
            string sdkVersion,
            ConvaiSkillSourceLayout layout,
            string warning)
        {
            SdkRoot = sdkRoot;
            SkillRoot = skillRoot;
            SkillFilePath = skillFilePath;
            SdkVersion = sdkVersion;
            Layout = layout;
            Warning = warning;
        }

        internal string SdkRoot { get; }
        internal string SkillRoot { get; }
        internal string SkillFilePath { get; }
        internal string SdkVersion { get; }
        internal ConvaiSkillSourceLayout Layout { get; }

        /// <summary>Non-blocking note, such as a second SDK distribution shadowing this one.</summary>
        internal string Warning { get; }

        internal bool IsValid => !string.IsNullOrEmpty(SkillRoot);
    }

    /// <summary>
    ///     Resolves the physical Convai SDK skill folder for embedded UPM, PackageCache UPM, and the
    ///     standard Assets layout. Registry, Git, and tarball installs all resolve through Unity's
    ///     <see cref="PackageInfo.resolvedPath" />; the cache suffix is never constructed by hand.
    /// </summary>
    internal static class ConvaiSkillSourceResolver
    {
        internal const string PackageName = "com.convai.convai-sdk-for-unity";
        internal const string SkillName = "convai-unity-sdk";
        internal const string SkillsFolderName = "AIAssistantSkills";
        internal const string SkillFileName = "SKILL.md";

        /// <summary>Folder the internal UPM-to-UAS converter writes, and the only supported Assets layout.</summary>
        internal const string AssetsSdkFolderName = "Convai SDK For Unity";

        internal const string UnknownVersion = "unknown";

        private const string PackageCacheSegment = "PackageCache";

        private static readonly Regex VersionField =
            new("\"version\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

        /// <summary>Resolves afresh from the live Editor. Nothing here is cached; callers re-resolve per operation.</summary>
        internal static bool TryResolve(out ConvaiSkillSource source, out string error) =>
            TryResolveFrom(EnumerateCandidates(), out source, out error);

        /// <summary>
        ///     Picks the first candidate that actually carries <c>SKILL.md</c>. Later valid candidates
        ///     become a duplicate-distribution warning rather than a blocker.
        /// </summary>
        internal static bool TryResolveFrom(
            IReadOnlyList<ConvaiSkillSourceCandidate> candidates,
            out ConvaiSkillSource source,
            out string error)
        {
            source = default;
            error = string.Empty;
            if (candidates == null || candidates.Count == 0)
            {
                error = NotFoundMessage(Array.Empty<string>());
                return false;
            }

            var valid = new List<ConvaiSkillSourceCandidate>();
            var inspected = new List<string>();
            foreach (ConvaiSkillSourceCandidate candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.SdkRoot)) continue;
                string skillRoot = GetSkillRoot(candidate.SdkRoot);
                inspected.Add(skillRoot);
                if (File.Exists(Path.Combine(skillRoot, SkillFileName))) valid.Add(candidate);
            }

            if (valid.Count == 0)
            {
                error = NotFoundMessage(inspected);
                return false;
            }

            ConvaiSkillSourceCandidate chosen = valid[0];
            string chosenRoot = Path.GetFullPath(chosen.SdkRoot);
            string warning = valid.Count == 1
                ? null
                : "More than one Convai SDK distribution provides the skill: " +
                  string.Join(", ", valid.Select(item => Normalize(item.SdkRoot))) +
                  $". Using {Normalize(chosenRoot)}. Remove the duplicate distribution to avoid mismatched guidance.";

            source = new ConvaiSkillSource(
                chosenRoot,
                GetSkillRoot(chosenRoot),
                Path.Combine(GetSkillRoot(chosenRoot), SkillFileName),
                ResolveVersion(chosen),
                chosen.Layout,
                warning);
            return true;
        }

        internal static string GetSkillRoot(string sdkRoot) =>
            Path.Combine(sdkRoot, SkillsFolderName, SkillName);

        /// <summary>
        ///     Formats a physical path for an instruction file: project-relative when the source lives
        ///     inside the project, absolute otherwise. Always forward slashes, because agents read it.
        /// </summary>
        internal static string ToInstructionPointer(string projectRoot, string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return null;
            string full = Path.GetFullPath(absolutePath);
            if (string.IsNullOrWhiteSpace(projectRoot)) return Normalize(full);

            string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length > root.Length + 1 &&
                full.StartsWith(root, PathComparison) &&
                (full[root.Length] == Path.DirectorySeparatorChar || full[root.Length] == Path.AltDirectorySeparatorChar))
                return Normalize(full.Substring(root.Length + 1));

            return Normalize(full);
        }

        internal static string Normalize(string path) => path?.Replace('\\', '/');

        /// <summary>
        ///     Conservatively preserve case outside Windows: macOS volumes can also be case-sensitive.
        ///     An absolute pointer is preferable to incorrectly treating an external source as contained.
        /// </summary>
        internal static StringComparison PathComparison =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        internal static StringComparer PathComparer =>
            PathComparison == StringComparison.Ordinal
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;

        /// <summary>Detects the layout from the resolved path so a relocated cache is still labelled correctly.</summary>
        internal static ConvaiSkillSourceLayout ClassifyPackagePath(string resolvedPath)
        {
            if (string.IsNullOrWhiteSpace(resolvedPath)) return ConvaiSkillSourceLayout.EmbeddedPackage;
            string[] segments = Normalize(Path.GetFullPath(resolvedPath)).Split('/');
            return segments.Any(segment => string.Equals(segment, PackageCacheSegment, StringComparison.OrdinalIgnoreCase))
                ? ConvaiSkillSourceLayout.PackageCache
                : ConvaiSkillSourceLayout.EmbeddedPackage;
        }

        private static IReadOnlyList<ConvaiSkillSourceCandidate> EnumerateCandidates()
        {
            var candidates = new List<ConvaiSkillSourceCandidate>(3);
            AddDistinct(candidates, FromPackage(SafeFindForAssembly()));

            foreach (PackageInfo package in SafeGetAllRegisteredPackages())
                if (string.Equals(package?.name, PackageName, StringComparison.Ordinal))
                    AddDistinct(candidates, FromPackage(package));

            string projectRoot = GetProjectRoot();
            if (!string.IsNullOrEmpty(projectRoot))
                AddDistinct(candidates, new ConvaiSkillSourceCandidate(
                    Path.Combine(projectRoot, "Assets", AssetsSdkFolderName),
                    ConvaiSkillSourceLayout.Assets,
                    null));

            return candidates;
        }

        /// <summary>Appends a candidate unless the same physical root is already present.</summary>
        internal static void AddDistinct(
            ICollection<ConvaiSkillSourceCandidate> candidates,
            ConvaiSkillSourceCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.SdkRoot)) return;
            string full = CanonicalRoot(candidate.SdkRoot);
            // Same reason as the containment test: on a case-sensitive filesystem two roots that
            // differ only in case are different directories, and folding them would silently drop
            // a real candidate.
            if (candidates.Any(existing => string.Equals(CanonicalRoot(existing.SdkRoot), full, PathComparison)))
                return;
            candidates.Add(candidate);
        }

        /// <summary>
        ///     One spelling per directory. <see cref="Path.GetFullPath(string)" /> keeps a trailing
        ///     separator, so without this the same root compares unequal to itself.
        /// </summary>
        private static string CanonicalRoot(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static ConvaiSkillSourceCandidate FromPackage(PackageInfo package)
        {
            if (package == null || !string.Equals(package.name, PackageName, StringComparison.Ordinal)) return default;
            if (string.IsNullOrWhiteSpace(package.resolvedPath)) return default;
            return new ConvaiSkillSourceCandidate(
                package.resolvedPath,
                ClassifyPackagePath(package.resolvedPath),
                package.version);
        }

        private static PackageInfo SafeFindForAssembly()
        {
            try { return PackageInfo.FindForAssembly(typeof(ConvaiSkillSourceResolver).Assembly); }
            catch (Exception) { return null; }
        }

        private static IEnumerable<PackageInfo> SafeGetAllRegisteredPackages()
        {
            try { return PackageInfo.GetAllRegisteredPackages() ?? Array.Empty<PackageInfo>(); }
            catch (Exception) { return Array.Empty<PackageInfo>(); }
        }

        private static string GetProjectRoot()
        {
            try
            {
                return Directory.GetParent(UnityEngine.Application.dataPath)?.FullName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveVersion(ConvaiSkillSourceCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.DeclaredVersion)) return candidate.DeclaredVersion.Trim();
            string manifest = Path.Combine(candidate.SdkRoot, "package.json");
            if (!File.Exists(manifest)) return UnknownVersion;
            try
            {
                Match match = VersionField.Match(File.ReadAllText(manifest));
                return match.Success ? match.Groups[1].Value : UnknownVersion;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return UnknownVersion;
            }
        }

        private static string NotFoundMessage(IReadOnlyList<string> inspected)
        {
            string locations = inspected.Count == 0
                ? string.Empty
                : " Looked in: " + string.Join(", ", inspected.Select(Normalize)) + ".";
            return
                $"Cannot locate the packaged Convai skill. Expected '{SkillsFolderName}/{SkillName}/{SkillFileName}' " +
                $"under the installed Convai SDK ('{PackageName}' or 'Assets/{AssetsSdkFolderName}')." + locations;
        }
    }
}
