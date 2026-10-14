using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards the shape of the <c>Convai</c> menu — the first thing a user sees of this SDK and
    ///     the one surface every module can extend without asking anyone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why.</b> The menu is a shared resource with no owner. Each module adds its own
    ///         entry in its own file, every addition looks reasonable on its own, and the result was
    ///         seventeen top-level rows of which seven opened the same window at different sections
    ///         of its own sidebar and two opened the same troubleshooter. Nobody decided that; it
    ///         accumulated. These guards make the accumulation visible at the moment it happens
    ///         rather than the next time someone screenshots the menu.
    ///     </para>
    ///     <para>
    ///         <b>Scope.</b> Package source under <c>SDK/</c> only. <c>Plugins/</c> is third-party
    ///         and out of bounds for this repository's conventions, and the dev project's
    ///         <c>Convai/Developer</c> tools live in <c>Assets/</c> and never ship.
    ///     </para>
    /// </remarks>
    public sealed class ConvaiEditorMenuGuardTests
    {
        /// <summary>
        ///     The ceiling on top-level <c>Convai/</c> rows. Not a magic number: it is the menu the
        ///     SDK deliberately ships — three configuration entries, five feature editors, one
        ///     troubleshooter — plus no headroom. Raising it is a decision about what a user is
        ///     asked to read on first open, so it should be made here, in one place, rather than by
        ///     a module quietly adding a tenth row.
        /// </summary>
        // Scene Auto Tagger is an explicitly requested scene-wide authoring workflow with its own review session.
        private const int TopLevelRowCeiling = 10;

        private const string MenuPrefix = "Convai/";

        private static readonly Regex MenuItemPattern = new(
            @"\[MenuItem\(\s*""(?<path>Convai/[^""]+)""(?<rest>[^\]]*)\]",
            RegexOptions.CultureInvariant);

        // Matches the priority in either attribute form: the positional third argument
        // ([MenuItem(path, false, 40)]) or the named one ([MenuItem(path, priority = 40)]).
        private static readonly Regex NamedPriorityPattern = new(
            @"priority\s*=\s*(?<expr>[A-Za-z0-9_.\s+\-]+?)\s*$",
            RegexOptions.CultureInvariant);

        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        private static string SdkRoot => Path.Combine(PackageRoot, "SDK");

        private readonly struct MenuEntry
        {
            internal MenuEntry(string path, string priority, bool isValidator, string where)
            {
                Path = path;
                Priority = priority;
                IsValidator = isValidator;
                Where = where;
            }

            internal string Path { get; }
            internal string Priority { get; }
            internal bool IsValidator { get; }
            internal string Where { get; }
        }

        [Test]
        [Category("Architecture")]
        public void ConvaiMenu_StaysWithinItsRowCeiling()
        {
            string[] rows = ReadMenuEntries()
                .Where(entry => !entry.IsValidator)
                .Select(entry => entry.Path)
                .Where(path => !path.Substring(MenuPrefix.Length).Contains('/'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.LessOrEqual(rows.Length, TopLevelRowCeiling,
                "The Convai menu has grown past the length it was deliberately cut to. A new row is a "
                + "claim that something cannot be reached from the surface that owns its subject — if "
                + "that is true, raise the ceiling here and say why:\n"
                + string.Join("\n", rows));
        }

        [Test]
        [Category("Architecture")]
        public void ConvaiMenu_GivesEveryEntryItsOwnPriority()
        {
            var byPriority = new Dictionary<string, List<MenuEntry>>(StringComparer.Ordinal);

            foreach (MenuEntry entry in ReadMenuEntries())
            {
                // A validator deliberately repeats its command's path and priority.
                if (entry.IsValidator) continue;
                if (string.IsNullOrEmpty(entry.Priority)) continue;

                if (!byPriority.TryGetValue(entry.Priority, out List<MenuEntry> sharing))
                    byPriority[entry.Priority] = sharing = new List<MenuEntry>(2);
                sharing.Add(entry);
            }

            string[] collisions = byPriority
                .Where(pair => pair.Value.Select(entry => entry.Path)
                    .Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(pair => $"{pair.Key} — "
                                + string.Join(", ", pair.Value.Select(entry => $"{entry.Path} ({entry.Where})")))
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToArray();

            Assert.IsEmpty(collisions,
                "Two menu entries share a priority, so the order Unity draws them in is whichever "
                + "assembly happened to load first. ConvaiEditorMenu declares the bands precisely so "
                + "that each entry picks an offset no sibling uses:\n"
                + string.Join("\n", collisions));
        }

        [Test]
        [Category("Architecture")]
        public void ConvaiMenu_HasNoTwoRowsOntoTheSameCommand()
        {
            var byMethod = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string file in Directory.EnumerateFiles(SdkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = PackageFiles.ReadAllLines(file);
                string relative = Path.GetRelativePath(PackageRoot, file).Replace('\\', '/');

                for (int i = 0; i < lines.Length; i++)
                {
                    Match match = MenuItemPattern.Match(lines[i]);
                    if (!match.Success) continue;
                    if (IsValidator(match.Groups["rest"].Value)) continue;

                    // The attributes on one method stack immediately above it; walk down past any
                    // further attributes to the declaration they all belong to.
                    string owner = null;
                    for (int j = i + 1; j < lines.Length && j <= i + 6; j++)
                    {
                        string candidate = lines[j].Trim();
                        if (candidate.StartsWith("[", StringComparison.Ordinal) || candidate.Length == 0) continue;
                        owner = $"{relative}: {candidate}";
                        break;
                    }

                    if (owner == null) continue;

                    if (!byMethod.TryGetValue(owner, out List<string> paths))
                        byMethod[owner] = paths = new List<string>(2);
                    paths.Add(match.Groups["path"].Value);
                }
            }

            string[] duplicates = byMethod
                .Where(pair => pair.Value.Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(pair => $"{pair.Key} ← {string.Join(" + ", pair.Value)}")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToArray();

            Assert.IsEmpty(duplicates,
                "One command is offered under two menu paths. An alias does not preserve anyone's "
                + "muscle memory; it makes a user choose between two rows that do the same thing:\n"
                + string.Join("\n", duplicates));
        }

        private static IEnumerable<MenuEntry> ReadMenuEntries()
        {
            foreach (string file in Directory.EnumerateFiles(SdkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(PackageRoot, file).Replace('\\', '/');

                foreach (Match match in MenuItemPattern.Matches(PackageFiles.ReadAllText(file)))
                {
                    string rest = match.Groups["rest"].Value;
                    yield return new MenuEntry(
                        match.Groups["path"].Value,
                        ReadPriority(rest),
                        IsValidator(rest),
                        relative);
                }
            }
        }

        /// <summary>
        ///     The priority as written, not as evaluated: the bands are constants in another
        ///     assembly, and two entries that disagree textually — <c>Diagnostics + 1</c> against a
        ///     bare <c>41</c> — are a readability problem worth reporting on their own.
        /// </summary>
        private static string ReadPriority(string rest)
        {
            string trimmed = rest.Trim().TrimEnd(')').Trim();
            if (trimmed.Length == 0) return null;

            Match named = NamedPriorityPattern.Match(trimmed);
            if (named.Success) return Normalize(named.Groups["expr"].Value);

            // Positional: ", isValidateFunction, priority".
            string[] parts = trimmed.TrimStart(',').Split(',');
            return parts.Length >= 2 ? Normalize(parts[1]) : null;
        }

        private static string Normalize(string expression) =>
            Regex.Replace(expression.Trim(), @"\s+", " ");

        /// <summary>
        ///     True for a validate function — the second argument is <c>true</c>. Its path and
        ///     priority are required to match its command's, so it is never a collision.
        /// </summary>
        private static bool IsValidator(string rest)
        {
            string[] parts = rest.Trim().TrimEnd(')').TrimStart(',').Split(',');
            return parts.Length >= 1 &&
                   string.Equals(parts[0].Trim(), "true", StringComparison.Ordinal);
        }
    }
}
