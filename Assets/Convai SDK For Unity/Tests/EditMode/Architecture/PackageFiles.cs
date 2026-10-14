using System;
using System.IO;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     How the architecture guards read the package tree. Every guard that walks arbitrary
    ///     package content — meta files, scenes, prefabs, imported art — reads it through here
    ///     rather than through <see cref="File" /> directly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this exists.</b> Windows' classic path limit is 260 characters, and Unity's
    ///         scripting runtime opts out of the OS-level long-path support even where the machine has
    ///         it enabled. A guard that walks the package with plain <c>File.ReadAllText</c> therefore
    ///         throws <see cref="DirectoryNotFoundException" /> the moment any file in it exceeds that
    ///         — and a character exported from an authoring tool into a sample folder reaches 265
    ///         characters without anyone doing anything unusual.
    ///     </para>
    ///     <para>
    ///         <b>Why it matters more than the file it fails on.</b> The throw is not a failed check;
    ///         it is a check that never ran. Four guards reported a crash instead of a verdict, so
    ///         until the path was shortened nothing was guarding cross-sample references, editor
    ///         leakage into samples, or shared-asset placement at all. A guard whose coverage can be
    ///         switched off by an unrelated art import is not a guard, and how deep a developer's
    ///         checkout sits is not something the SDK gets to have an opinion about.
    ///     </para>
    ///     <para>
    ///         <b>How.</b> The extended-length prefix (<c>\\?\</c>) bypasses the limit for a fully
    ///         qualified path. It is applied only where it is needed — on Windows, past the limit —
    ///         so the ordinary case keeps the ordinary path in exception messages.
    ///     </para>
    /// </remarks>
    internal static class PackageFiles
    {
        /// <summary>The classic Windows path limit, past which a path needs the extended prefix.</summary>
        private const int MaxPath = 260;

        private const string ExtendedPrefix = @"\\?\";
        private const string ExtendedUncPrefix = @"\\?\UNC\";

        /// <summary>Reads a package file's text, regardless of how long its path is.</summary>
        internal static string ReadAllText(string path) => File.ReadAllText(Addressable(path));

        /// <summary>Reads a package file's lines, regardless of how long its path is.</summary>
        internal static string[] ReadAllLines(string path) => File.ReadAllLines(Addressable(path));

        /// <summary>
        ///     <paramref name="path" /> in a form the file APIs can open. Returned unchanged unless
        ///     this is Windows and the fully qualified path is past <see cref="MaxPath" />.
        /// </summary>
        private static string Addressable(string path)
        {
            if (Path.DirectorySeparatorChar != '\\') return path;
            if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return path;

            string full = Path.GetFullPath(path);
            if (full.Length < MaxPath) return path;

            // A UNC path (\\server\share\…) takes the UNC form of the prefix; the plain form would
            // address a server named "?" instead.
            return full.StartsWith(@"\\", StringComparison.Ordinal)
                ? ExtendedUncPrefix + full.Substring(2)
                : ExtendedPrefix + full;
        }
    }
}
