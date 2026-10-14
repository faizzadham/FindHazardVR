#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Editor.UI;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards the single Convai editor design system against re-forking.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The SDK previously carried five parallel editor-UI implementations — two of them in the
    ///         same assembly, one of which documented itself as the fix for the duplication it was
    ///         part of. They drifted in six measurable ways (pill padding, tint alpha, hero font size,
    ///         button hover, two label styles), and the oldest hardcoded dark greys that rendered as
    ///         dark blocks in Unity's Light skin.
    ///     </para>
    ///     <para>
    ///         Nothing about that was careless — each fork was the cheapest local option at the time.
    ///         These tests exist to make the cheap option the correct one: a second palette, style
    ///         cache or section-state store now fails the build with a message that says where the
    ///         shared one lives.
    ///     </para>
    /// </remarks>
    public class EditorDesignSystemGuardTests
    {
        /// <summary>Assemblies that own Convai editor UI.</summary>
        private static readonly string[] EditorAssemblyNames =
        {
            "Convai.Editor",
            "Convai.Editor.Embodiment",
            "Convai.Modules.BodyAnimation.Editor",
            "Convai.Modules.BodyLanguage.Editor",
            "Convai.Modules.Emotion.Editor",
            "Convai.Modules.Gaze.Editor"
        };

        /// <summary>The palette. Only the token layer holds values; the facade forwards to it.</summary>
        private static readonly string[] AllowedPaletteTypes = { nameof(ConvaiEditorTokens), nameof(ConvaiEditorTheme) };

        private static IEnumerable<Type> EditorTypes()
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            foreach (string name in EditorAssemblyNames)
            {
                Assembly assembly = all.FirstOrDefault(a => a.GetName().Name == name);
                if (assembly == null)
                    continue;

                foreach (Type type in assembly.GetTypes())
                    yield return type;
            }
        }

        private static bool HasMember(Type type, string member) =>
            type.GetMember(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Length > 0;

        [Test]
        public void EditorAssemblies_DeclareExactlyOnePalette()
        {
            // A palette is anything exposing both a brand accent and a card surface — the pair every
            // one of the five forks carried.
            List<string> palettes = EditorTypes()
                .Where(t => HasMember(t, "Accent") && HasMember(t, "CardBg"))
                .Select(t => t.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            CollectionAssert.AreEquivalent(
                AllowedPaletteTypes,
                palettes,
                "A second editor palette appeared. Colours belong in ConvaiEditorTokens " +
                "(SDK/Editor/UI); reach them through ConvaiEditorTheme. Found: " +
                string.Join(", ", palettes));
        }

        [Test]
        public void EditorAssemblies_DeclareExactlyOneSectionStateStore()
        {
            List<string> stores = EditorTypes()
                .Where(t => t.GetMethod(
                    "BuildKey",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null) != null)
                .Select(t => t.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            CollectionAssert.AreEquivalent(
                new[] { nameof(ConvaiEditorSectionState) },
                stores,
                "A second section-state store appeared. Section expansion belongs in " +
                "ConvaiEditorSectionState (SDK/Editor/UI). Found: " + string.Join(", ", stores));
        }

        [Test]
        public void EditorAssemblies_DeclareExactlyOneStyleCache()
        {
            // A style cache is a static type that both builds styles and exposes them.
            List<string> caches = EditorTypes()
                .Where(t => t.IsAbstract && t.IsSealed)
                .Where(t => HasMember(t, "EnsureStyles") || HasMember(t, "EnsureInitialized"))
                .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Any(p => p.PropertyType == typeof(GUIStyle)))
                .Select(t => t.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            CollectionAssert.AreEquivalent(
                new[] { nameof(ConvaiEditorStyles), nameof(ConvaiEditorTheme) },
                caches,
                "A second editor style cache appeared. Styles belong in ConvaiEditorStyles " +
                "(SDK/Editor/UI). Found: " + string.Join(", ", caches));
        }

        #region Source-level bans

        /// <summary>
        ///     Folders excluded from the source-level bans, and why. UI Toolkit is a different
        ///     rendering stack with its own stylesheets (ratified scope decision); the design system
        ///     itself is the one place colours and styles are allowed to live; third-party plugin code
        ///     shipped inside the package is not ours to edit.
        /// </summary>
        private static readonly string[] ExcludedFolders =
        {
            "/SDK/Editor/UI/",
            "/SDK/Editor/ConfigurationWindow/",
            "/SDK/Editor/Settings/",
            "/Plugins/"
        };

        /// <summary>
        ///     Matches an explicit <c>new Color(…)</c> and the target-typed form a colour field or
        ///     local uses (<c>Color x = new(…)</c>). The target-typed spelling hid seven literals in a
        ///     diagnostics window from the original ban, which is why both shapes are matched.
        /// </summary>
        private static readonly Regex ColourLiteral = new(
            @"new\s+Color(32)?\s*\(|\bColor(32)?\s+\w+\s*=\s*new\s*\(", RegexOptions.Compiled);

        private static readonly Regex StyleAllocation = new(@"new\s+GUIStyle\s*\(", RegexOptions.Compiled);

        /// <summary>
        ///     Hard ban, reached 2026-07-30 after the last 60 colour literals were routed through
        ///     <see cref="ConvaiEditorTokens" />. UI colours must use that single palette. The three
        ///     capture/fixture files below also author image pixels, camera backgrounds and sample
        ///     materials; those non-UI colours are exempt from this test only.
        /// </summary>
        [Test]
        public void EditorSourceFiles_ContainNoColourLiterals() =>
            AssertNoMatches(
                ColourLiteral,
                "colour literal",
                "Colours belong in ConvaiEditorTokens (SDK/Editor/UI); reach them through ConvaiEditorTheme.",
                EditorSourceFiles().Where(path => !new[]
                {
                    "/SDK/Editor/SceneTagging/SceneTaggerCaptureService.cs",
                    "/SDK/Editor/SceneTagging/SceneTaggerSession.cs",
                    "/Samples~/SceneTagger/Editor/SceneTaggerSample.cs"
                }.Any(file => path.Replace('\\', '/').EndsWith(file, StringComparison.Ordinal))));

        /// <summary>
        ///     Hard ban, reached 2026-07-30 after the last 47 local <see cref="GUIStyle" /> allocations
        ///     were moved into <see cref="ConvaiEditorStyles" />. A style built at a call site is
        ///     allocated per repaint and drifts from every other surface; add it to the shared cache
        ///     instead, where it is built once and rebuilt only on skin flip.
        /// </summary>
        [Test]
        public void EditorSourceFiles_AllocateNoLocalStyles() =>
            AssertNoMatches(
                StyleAllocation,
                "GUIStyle allocation",
                "Styles belong in ConvaiEditorStyles (SDK/Editor/UI), built once and rebuilt only on skin flip.");

        private static readonly Regex TextColourWrite = new(
            @"\.\s*(on)?(normal|hover|active|focused)\s*\.\s*textColor\s*=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        ///     Only <see cref="ConvaiEditorStyles" /> may write a colour onto a
        ///     <see cref="GUIStyle" />, and only onto an instance it owns.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Every style this system hands out is shared and long-lived, so writing a colour
        ///         onto one is not a local act: the write is permanent, and the next caller to draw
        ///         through that style inherits it. This shipped — the Actions Editor overview reported
        ///         healthy group counts in the error colour, because a tinted reading drawn earlier in
        ///         the session had left red on the style every untinted reading uses.
        ///     </para>
        ///     <para>
        ///         The tinted variants in <see cref="ConvaiEditorStyles" /> are the way to draw in a
        ///         colour: each returns a pooled instance carrying that colour and nothing else. This
        ///         test scans the design system's own folder too, since that is where the defect was.
        ///     </para>
        /// </remarks>
        [Test]
        public void EditorSourceFiles_NeverRecolourASharedStyle()
        {
            var offenders = new List<string>();

            foreach (string path in AllEditorSourceFiles())
            {
                if (Path.GetFileName(path) == "ConvaiEditorStyles.cs")
                    continue;

                int count = TextColourWrite.Matches(File.ReadAllText(path)).Count;
                if (count > 0)
                    offenders.Add($"{Path.GetFileName(path)} ({count})");
            }

            Assert.IsEmpty(
                offenders,
                "A shared GUIStyle was re-coloured at a call site. The write never gets undone, so the " +
                "colour follows the style into every later draw through it, in every Convai window. " +
                "Use a tinted variant from ConvaiEditorStyles (ReadingValueTinted, TableCellTinted, " +
                "PillLabelTinted, …), or add one there — they return a pooled instance per colour.\n" +
                string.Join("\n", offenders));
        }

        /// <summary>
        ///     Every first-party editor source file, including the design system's own folder — unlike
        ///     <see cref="EditorSourceFiles" />, which exempts it because it is where the vocabulary
        ///     the other rules ban is legitimately defined.
        /// </summary>
        private static IEnumerable<string> AllEditorSourceFiles()
        {
            string packageRoot = Path.GetFullPath(Path.Combine(
                UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));
            if (!Directory.Exists(packageRoot))
                Assert.Ignore($"Package root not found at {packageRoot}.");

            foreach (string path in Directory.EnumerateFiles(packageRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = path.Replace('\\', '/');
                if (!normalized.Contains("/Editor/", StringComparison.Ordinal))
                    continue;
                if (normalized.Contains("/Plugins/", StringComparison.Ordinal))
                    continue;

                yield return path;
            }
        }

        /// <summary>
        ///     Codepoints an editor source file may never contain, and why each one is out.
        /// </summary>
        /// <remarks>
        ///     All of these render as a colour emoji or a missing box on at least one of the three
        ///     platforms the SDK ships to, which is worse than the wrong shape: an emoji ignores the
        ///     tint the row asked for and sits off the text baseline. Use
        ///     <see cref="ConvaiEditorGlyphs" /> — its marks come from Geometric Shapes and Arrows,
        ///     which every default system font covers and which inherit their section's accent.
        /// </remarks>
        private static readonly (char Character, string Name, string Instead)[] BannedCharacters =
        {
            ('⚙', "GEAR", "ConvaiEditorGlyphs.Profile"),
            ('⚒', "HAMMER AND PICK", "ConvaiEditorGlyphs.Contract"),
            ('⌕', "TELEPHONE RECORDER (used as a magnifier)", "ConvaiEditorGlyphs.Discovery"),
            ('⌖', "POSITION INDICATOR", "ConvaiEditorGlyphs.Placement"),
            ('✎', "LOWER RIGHT PENCIL", "ConvaiEditorGlyphs.Identity"),
            ('⚠', "WARNING SIGN (renders as a colour emoji)", "ConvaiEditorGlyphs.Status.Warn"),
            ('❌', "CROSS MARK (renders as a colour emoji)", "ConvaiEditorGlyphs.Status.Fail"),
            ('✗', "BALLOT X", "ConvaiEditorGlyphs.Status.Fail"),
            ('ℹ', "INFORMATION SOURCE (renders as a colour emoji)", "ConvaiEditorGlyphs.Status.Info"),
            ('⋯', "MIDLINE HORIZONTAL ELLIPSIS", "a plain \"…\" (U+2026)")
        };

        /// <summary>
        ///     Every glyph the design system publishes must come from a block the default fonts on
        ///     Windows, macOS and Linux all cover, so a section mark cannot silently become a missing
        ///     box or a colour emoji on somebody else's machine.
        /// </summary>
        [Test]
        public void Glyphs_ComeOnlyFromFontSafeBlocks()
        {
            // Printable ASCII (safe by definition), Arrows, Geometric Shapes, and the two Dingbats
            // check/cross marks — which are text-presentation by default and covered everywhere,
            // unlike the emoji-presentation symbols in BannedCharacters.
            (int Low, int High)[] allowed =
            {
                (0x0020, 0x007E), (0x2190, 0x21FF), (0x25A0, 0x25FF), (0x2713, 0x2713), (0x2715, 0x2715)
            };

            var offenders = new List<string>();
            foreach ((string name, string value) in GlyphConstants())
            {
                foreach (char c in value)
                {
                    if (allowed.Any(r => c >= r.Low && c <= r.High)) continue;
                    offenders.Add($"{name} = '{value}' contains U+{(int)c:X4}");
                }
            }

            Assert.IsEmpty(
                offenders,
                "A glyph outside the font-safe blocks appeared. Section marks must come from Arrows " +
                "(U+2190–U+21FF) or Geometric Shapes (U+25A0–U+25FF) so they render as tinted text on " +
                "every platform.\n" + string.Join("\n", offenders));
        }

        /// <summary>
        ///     No editor file may spell an icon inline. An icon literal at a call site is a private
        ///     glyph vocabulary, which is how the Actions window ended up with its own gear, magnifier
        ///     and chevrons while every other surface used the shared marks.
        /// </summary>
        [Test]
        public void EditorSourceFiles_SpellNoIconsInline()
        {
            // A string literal with a symbol in it but no ASCII letter is an icon, not prose. Prose
            // may legitimately contain an arrow ("set Rig → Animation Type to Humanoid"); an icon
            // must come from ConvaiEditorGlyphs.
            var iconLiteral = new Regex("\"((?:[^\"\\\\\\n]|\\\\.)*)\"", RegexOptions.Compiled);
            var offenders = new List<string>();

            foreach (string path in EditorSourceFiles())
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("*", StringComparison.Ordinal))
                        continue;

                    foreach (Match match in iconLiteral.Matches(line))
                    {
                        string content = match.Groups[1].Value;

                        foreach ((char character, string name, string instead) in BannedCharacters)
                        {
                            if (content.IndexOf(character) < 0) continue;
                            offenders.Add(
                                $"{Path.GetFileName(path)}:{i + 1} uses U+{(int)character:X4} {name} — use {instead}");
                        }

                        if (content.Length == 0 || content.Any(char.IsLetter)) continue;
                        if (!content.Any(IsSymbolGlyph)) continue;

                        offenders.Add(
                            $"{Path.GetFileName(path)}:{i + 1} spells an icon inline: \"{content}\"");
                    }
                }
            }

            Assert.IsEmpty(
                offenders,
                "An editor file spells an icon inline instead of using the shared vocabulary. Section " +
                "marks live in ConvaiEditorGlyphs and outcome marks in ConvaiEditorGlyphs.Status " +
                "(SDK/Editor/UI); a collapsible header's chevron is drawn by the design system, never " +
                "by the call site.\n" + string.Join("\n", offenders.Distinct()));
        }

        /// <summary>
        ///     A status chip must report setup health (outside Play mode) or runtime state (in Play
        ///     mode). A chip reading "Editor" reports neither — it names the mode the user is already
        ///     in — and eight surfaces had it while their siblings said "Ready" for the same state.
        /// </summary>
        [Test]
        public void EditorSourceFiles_DeclareNoModeNamingStatusChips()
        {
            // Matches the chip-declaration shape specifically (`new("Editor", …)` /
            // `new GUIContent("Editor", …)`) rather than the bare word, which appears legitimately in
            // paths, log lines and assembly names.
            var chipDeclaration = new Regex(
                @"new\s*(?:GUIContent\s*)?\(\s*""(Editor|Edit Mode|Not Playing|Offline)""",
                RegexOptions.Compiled);

            var offenders = new List<string>();
            foreach (string path in EditorSourceFiles())
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (chipDeclaration.IsMatch(lines[i]))
                        offenders.Add($"{Path.GetFileName(path)}:{i + 1} {lines[i].Trim()}");
                }
            }

            Assert.IsEmpty(
                offenders,
                "A status chip names the editor mode instead of reporting something useful. Outside " +
                "Play mode a chip reports setup health (ConvaiEditorChips.Ready / NeedsAttention / " +
                "NotSetUp / ActionNeeded); in Play mode it reports runtime state (Live / Idle / " +
                "Inactive). Pick an entry from ConvaiEditorChips (SDK/Editor/UI).\n" +
                string.Join("\n", offenders));
        }

        /// <summary>
        ///     A call site may not compute the centre of a status dot itself. The literal-offset form
        ///     (<c>row.y + 9f</c>) is correct for exactly one row height and drifts off-centre in every
        ///     other one, which is how the same dot ended up sitting at four different heights across
        ///     the Actions window.
        /// </summary>
        [Test]
        public void EditorSourceFiles_CentreStatusDotsWithARect()
        {
            // Only the VERTICAL term is policed. A dot's horizontal inset inside a card is a real
            // layout decision and stays the call site's business; its vertical centre is arithmetic
            // that must come from the rect. So a y argument is acceptable when it derives from a
            // height or from an already-computed centre, and a finding when it is a bare literal.
            var dotCentre = new Regex(
                @"StatusDot\s*\(\s*new\s+Vector2\s*\((?<x>[^,]*),(?<y>[^)]*)\)", RegexOptions.Compiled);

            var offenders = new List<string>();
            foreach (string path in EditorSourceFiles())
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    Match match = dotCentre.Match(lines[i]);
                    if (!match.Success)
                        continue;

                    string y = match.Groups["y"].Value;
                    bool derivedFromRect =
                        y.Contains("eight", StringComparison.Ordinal) ||   // height / Height
                        y.Contains("enter", StringComparison.Ordinal) ||   // center / centre / Centre
                        y.Contains("entre", StringComparison.Ordinal) ||
                        y.Contains("yMax", StringComparison.Ordinal);

                    if (!derivedFromRect)
                        offenders.Add($"{Path.GetFileName(path)}:{i + 1} {lines[i].Trim()}");
                }
            }

            Assert.IsEmpty(
                offenders,
                "A status dot's vertical centre is a literal at the call site, which is correct for " +
                "exactly one row height. Pass the rect the dot belongs in and let the design system " +
                "centre it — ConvaiEditorTheme.StatusDot(rect, tint, emphasized) — or derive the centre " +
                "from the rect's own height.\n" + string.Join("\n", offenders));
        }

        /// <summary>
        ///     A group caption may not be drawn by handing <c>GroupLabel</c> to a label call. The
        ///     caption's whole job is to sit on the left edge of the control it names, and the two
        ///     idioms the call sites had split between disagree about exactly that:
        ///     <c>EditorGUILayout.LabelField</c> applies <see cref="UnityEditor.EditorGUI.indentLevel" />
        ///     and <c>GUILayout.Label</c> does not, so inside a section body the same caption hung
        ///     fifteen pixels off its own picker in one inspector and flush against it in the next.
        /// </summary>
        /// <remarks>
        ///     The style itself stays reachable: a fixed-width row label legitimately passes
        ///     <c>ConvaiEditorStyles.RowLabel</c>, and this test names that as the way out for the
        ///     two-column readouts it is meant for.
        /// </remarks>
        [Test]
        public void EditorSourceFiles_DrawGroupCaptionsThroughTheDesignSystem() =>
            AssertNoMatches(
                new Regex(@"(Label|LabelField)\s*\([^;]*\b(ConvaiEditorStyles|Styles|Theme|ConvaiEditorTheme)\.GroupLabel",
                    RegexOptions.Compiled),
                "group caption drawn by hand",
                "Draw it with ConvaiEditorControls.GroupCaption (or Theme.GroupCaption), which owns the " +
                "caption's left edge and its spacing. A fixed-width label in a two-column check row is a " +
                "different job: use ConvaiEditorStyles.RowLabel for that.");

        /// <summary>Any non-ASCII character that reads as a pictogram rather than punctuation.</summary>
        private static bool IsSymbolGlyph(char c) =>
            c >= 0x2190 && c <= 0x2BFF || char.IsSurrogate(c);

        private static IEnumerable<(string Name, string Value)> GlyphConstants()
        {
            foreach (FieldInfo field in typeof(ConvaiEditorGlyphs)
                         .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                         .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            {
                yield return (field.Name, (string)field.GetRawConstantValue());
            }

            foreach (Type nested in typeof(ConvaiEditorGlyphs).GetNestedTypes(
                         BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (FieldInfo field in nested
                             .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                             .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
                {
                    yield return ($"{nested.Name}.{field.Name}", (string)field.GetRawConstantValue());
                }
            }
        }

        private static void AssertNoMatches(Regex pattern, string what, string remedy, IEnumerable<string> paths = null)
        {
            var offenders = new List<string>();

            foreach (string path in paths ?? EditorSourceFiles())
            {
                int count = pattern.Matches(File.ReadAllText(path)).Count;
                if (count > 0)
                    offenders.Add($"{Path.GetFileName(path)} ({count})");
            }

            Assert.IsEmpty(
                offenders,
                $"A {what} appeared in a Convai editor source file. {remedy}\n" + string.Join("\n", offenders));
        }

        /// <summary>Every first-party IMGUI editor source file in the package.</summary>
        private static IEnumerable<string> EditorSourceFiles()
        {
            string packageRoot = Path.GetFullPath(Path.Combine(
                UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));
            if (!Directory.Exists(packageRoot))
                Assert.Ignore($"Package root not found at {packageRoot}.");

            foreach (string path in Directory.EnumerateFiles(packageRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = path.Replace('\\', '/');
                if (!normalized.Contains("/Editor/", StringComparison.Ordinal))
                    continue;
                if (ExcludedFolders.Any(f => normalized.Contains(f, StringComparison.Ordinal)))
                    continue;

                yield return path;
            }
        }

        #endregion

        [Test]
        public void ConvaiInspectorTargets_CarryNoHeaderAttributes()
        {
            // A [Header] on a serialized field is drawn by Unity's PropertyField as a second,
            // unstyled title *inside* the Convai section that already names that group — which is
            // how Body Animation ended up showing "Content", "Animator Wiring" and "Runtime
            // Dependencies" as bare bold text between its own section headers. Grouping belongs to
            // the inspector (ConvaiInspectorSectionAttribute or an explicit section), never to a
            // decorator the design system cannot style.
            var offenders = new List<string>();

            foreach (Type editor in EditorTypes())
            {
                if (!typeof(UnityEditor.Editor).IsAssignableFrom(editor)) continue;
                if (!IsConvaiInspector(editor)) continue;

                // Only editors that keep the base's generated-section body are checked. Those draw
                // every visible serialized field, so a [Header] on any of them is guaranteed to
                // render inside a Convai section. An editor that overrides DrawBody curates which
                // fields it draws, and a [Header] on a field it never draws is harmless.
                if (OverridesDrawBody(editor)) continue;

                var attribute = editor.GetCustomAttribute<UnityEditor.CustomEditor>();
                if (attribute == null) continue;

                Type inspected = (Type)typeof(UnityEditor.CustomEditor)
                    .GetField("m_InspectedType", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(attribute);
                if (inspected == null) continue;

                foreach (FieldInfo field in inspected.GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.GetCustomAttribute<HeaderAttribute>() == null) continue;

                    offenders.Add($"{inspected.Name}.{field.Name} (drawn by {editor.Name})");
                }
            }

            Assert.IsEmpty(
                offenders,
                "A [Header] attribute would draw an unstyled title inside a Convai inspector's own " +
                "sections. Remove it and let the inspector own the grouping.\n" +
                string.Join("\n", offenders));
        }

        private static bool IsConvaiInspector(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (current.Name == "ConvaiInspectorEditor") return true;
            }

            return false;
        }

        /// <summary>Whether this editor replaces the base's generated-section body renderer.</summary>
        private static bool OverridesDrawBody(Type editor)
        {
            MethodInfo drawBody = editor.GetMethod(
                "DrawBody", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return drawBody != null && drawBody.DeclaringType?.Name != "ConvaiInspectorEditor";
        }

        [Test]
        public void Glyphs_GiveEachMeaningItsOwnMark()
        {
            // The glyph vocabulary is only learnable if one mark means one thing. Two meanings
            // sharing a glyph is the drift this guards against.
            List<string> shared = typeof(ConvaiEditorGlyphs)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .GroupBy(f => (string)f.GetRawConstantValue())
                .Where(g => g.Count() > 1)
                .Select(g => $"'{g.Key}' is used by: {string.Join(", ", g.Select(f => f.Name))}")
                .ToList();

            Assert.IsEmpty(
                shared,
                "Two section meanings share one glyph, which breaks the learnable vocabulary. " +
                "Give each meaning its own mark in ConvaiEditorGlyphs.\n" + string.Join("\n", shared));
        }

        [Test]
        public void Facade_ForwardsToTokens_RatherThanHoldingItsOwnValues()
        {
            // If the facade ever grows a literal of its own, these diverge and this fails.
            Assert.AreEqual(ConvaiEditorTokens.Accent, ConvaiEditorTheme.Accent, "Accent diverged.");
            Assert.AreEqual(ConvaiEditorTokens.AccentBright, ConvaiEditorTheme.AccentBright, "AccentBright diverged.");
            Assert.AreEqual(ConvaiEditorTokens.CardBg, ConvaiEditorTheme.CardBg, "CardBg diverged.");
            Assert.AreEqual(ConvaiEditorTokens.InnerBg, ConvaiEditorTheme.InnerBg, "InnerBg diverged.");
            Assert.AreEqual(ConvaiEditorTokens.TextPrimary, ConvaiEditorTheme.TextPrimary, "TextPrimary diverged.");
            Assert.AreEqual(ConvaiEditorTokens.StatusWarn, ConvaiEditorTheme.Warning, "Warning alias diverged.");
            Assert.AreEqual(ConvaiEditorTokens.StatusError, ConvaiEditorTheme.Error, "Error alias diverged.");
            Assert.AreEqual(ConvaiEditorTokens.StatusInfo, ConvaiEditorTheme.Info, "Info alias diverged.");
        }

        [Test]
        public void Styles_AreCached_AndReusedAcrossCalls()
        {
            ConvaiEditorStyles.EnsureStyles();
            GUIStyle body = ConvaiEditorStyles.BodyWrapped;
            GUIStyle muted = ConvaiEditorStyles.MutedWrapped;
            GUIStyle sectionTitle = ConvaiEditorStyles.SectionTitle;

            ConvaiEditorStyles.EnsureStyles();

            // Re-allocating a GUIStyle per repaint is the allocation rule this system exists to keep.
            Assert.AreSame(body, ConvaiEditorStyles.BodyWrapped);
            Assert.AreSame(muted, ConvaiEditorStyles.MutedWrapped);
            Assert.AreSame(sectionTitle, ConvaiEditorStyles.SectionTitle);
        }

        [Test]
        public void SectionState_PersistentAndSessionScopes_ShareOneKeyScheme()
        {
            string key = ConvaiEditorSectionState.BuildKey("Map Debug Window", "Validation Results");
            Assert.AreEqual("Convai.Editor.MapDebugWindow.ValidationResults.Expanded", key);
        }

        /// <summary>
        ///     Every disposable scope struct must be impossible to construct with <c>new T()</c>.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         C# does not treat a struct constructor whose parameters are all optional as the
        ///         parameterless one. <c>new CardScope()</c> therefore compiled, ran no constructor at
        ///         all and produced the zero-initialised value — so <c>BeginCard</c> never opened the
        ///         card while <c>Dispose</c> still called <c>EndCard</c>. Thirty-one call sites across
        ///         the SDK drew no card or panel and closed a layout group they had never opened; the
        ///         Emotions window's layout stack drained a group per section until it reached zero
        ///         and the window collapsed into "EndLayoutGroup: BeginLayoutGroup must be called
        ///         first", with its cards laid out sideways.
        ///     </para>
        ///     <para>
        ///         Requiring one real argument makes the silent form a compile error. Scopes are
        ///         opened through their factory — <c>ConvaiEditorFrame.Card()</c>,
        ///         <c>ConvaiEditorFrame.Panel()</c>, <c>ConvaiEditorSections.Body()</c> — where
        ///         optional parameters behave the way they read.
        ///     </para>
        /// </remarks>
        [Test]
        public void DisposableScopeStructs_CannotBeConstructedWithoutArguments()
        {
            List<string> offenders = EditorTypes()
                .Where(t => t.IsValueType && typeof(IDisposable).IsAssignableFrom(t))
                .Where(t => t.GetConstructors(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(c => c.GetParameters().All(p => p.IsOptional)))
                .Select(t => t.FullName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.IsEmpty(
                offenders,
                "A disposable scope struct has a constructor whose parameters are all optional. " +
                "`new T()` then skips it silently and the scope closes something it never opened. " +
                "Make one parameter required and add a static factory beside the type " +
                "(see ConvaiEditorFrame.Card).");
        }
    }
}
#endif
