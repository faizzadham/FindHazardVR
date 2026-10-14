using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Convai.Editor
{
    /// <summary>
    ///     The copied skill files and their content hashes. Hashing normalizes line endings so a
    ///     CRLF checkout and an LF checkout of the same skill compare equal; normalization is for
    ///     comparison only and never rewrites the source.
    /// </summary>
    internal static class ConvaiSkillPayload
    {
        /// <summary>Ownership stamp written into the mirror. It is never part of its own hash.</summary>
        internal const string StampFileName = ".convai-skill.json";

        internal const string ManifestFileName = "skill-manifest.json";
        internal const string HashPrefix = "sha256:";

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".json", ".txt", ".yaml", ".yml", ".csv"
        };

        private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            StampFileName, ".DS_Store", "Thumbs.db", "desktop.ini"
        };

        /// <summary>Relative payload paths, forward-slashed and ordinal-sorted so renames change the aggregate.</summary>
        internal static IReadOnlyList<string> EnumerateRelativePaths(string root)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => ToRelativePath(root, path))
                .Where(relative => !IsExcluded(relative))
                .OrderBy(relative => relative, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>Source <c>.meta</c> files, OS clutter, and the stamp are not skill payload.</summary>
        internal static bool IsExcluded(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return true;
            if (relativePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;
            return relativePath
                .Split('/')
                .Any(segment => ExcludedFileNames.Contains(segment));
        }

        internal static IReadOnlyDictionary<string, string> ComputeFileHashes(string root)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string relative in EnumerateRelativePaths(root))
                hashes[relative] = ComputeFileHash(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return hashes;
        }

        internal static string ComputeFileHash(string absolutePath)
        {
            byte[] content = TextExtensions.Contains(Path.GetExtension(absolutePath))
                ? new UTF8Encoding(false).GetBytes(NormalizeLineEndings(File.ReadAllText(absolutePath)))
                : File.ReadAllBytes(absolutePath);
            using var sha = SHA256.Create();
            return HashPrefix + ToHex(sha.ComputeHash(content));
        }

        /// <summary>
        ///     Folds the sorted path/hash pairs into one value. Paths are included, so an addition,
        ///     deletion, or rename moves the aggregate even when no file content changed.
        /// </summary>
        internal static string ComputeAggregateHash(IReadOnlyDictionary<string, string> files)
        {
            var builder = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                builder.Append(entry.Key).Append('\n').Append(entry.Value).Append('\n');

            using var sha = SHA256.Create();
            return HashPrefix + ToHex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(builder.ToString())));
        }

        /// <summary>Copies the payload into an empty destination. The source is never modified.</summary>
        internal static void CopyTo(string sourceRoot, string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);
            foreach (string relative in EnumerateRelativePaths(sourceRoot))
            {
                string destination = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destinationRoot);
                File.Copy(Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)), destination, true);
            }
        }

        /// <summary>Reads the skill's own version from its manifest, independent of the SDK version.</summary>
        internal static string ReadSkillVersion(string skillRoot)
        {
            string manifest = Path.Combine(skillRoot, ManifestFileName);
            if (!File.Exists(manifest)) return ConvaiSkillSourceResolver.UnknownVersion;
            try
            {
                var parsed = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifest));
                string version = parsed.Value<string>("version");
                return string.IsNullOrWhiteSpace(version) ? ConvaiSkillSourceResolver.UnknownVersion : version.Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or Newtonsoft.Json.JsonException)
            {
                return ConvaiSkillSourceResolver.UnknownVersion;
            }
        }

        internal static string NormalizeLineEndings(string content) =>
            content?.Replace("\r\n", "\n").Replace('\r', '\n');

        private static string ToRelativePath(string root, string absolutePath) =>
            absolutePath
                .Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

        private static string ToHex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }
    }
}
