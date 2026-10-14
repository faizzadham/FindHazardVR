#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards the Convai editor UI against the costs that make an editor feel heavy on somebody
    ///     else's machine.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why static rules and not a benchmark.</b> Editor performance cannot be asserted with
    ///         a stopwatch in CI — the numbers depend on the agent, the skin and whether a repaint even
    ///         happened. What can be asserted is the shape of the code, and every regression this pass
    ///         fixed had the same three shapes: measuring text inside a draw pass, allocating a
    ///         <c>GUIContent</c> in order to measure it, and hand-rolling a refresh throttle instead of
    ///         using the shared one.
    ///     </para>
    ///     <para>
    ///         <b>Why the bans are worth the friction.</b> An IMGUI inspector repaints on every mouse
    ///         move over the Inspector window, and eleven Convai inspectors repaint continuously in
    ///         Play mode. Work put in a draw path is therefore paid tens of times a second on every
    ///         machine that installs the SDK — including the ones that are not the author's.
    ///     </para>
    /// </remarks>
    public class EditorPerformanceGuardTests
    {
        /// <summary>
        ///     Paths these rules do not police, and why each one is out.
        /// </summary>
        /// <remarks>
        ///     <list type="bullet">
        ///         <item><c>/SDK/Editor/UI/</c> — the design system itself, which owns the shared
        ///         measurement cache and the shared throttle and must be free to implement them.</item>
        ///         <item><c>/SDK/Editor/ConfigurationWindow/</c> and <c>/SDK/Editor/Settings/</c> —
        ///         UI Toolkit surfaces. They have no IMGUI draw pass, so none of these rules apply.</item>
        ///         <item><c>/Tests/</c> — a test measures directly on purpose, to prove the cache
        ///         agrees with the thing it replaced.</item>
        ///         <item><c>/Plugins/</c> — third party.</item>
        ///     </list>
        /// </remarks>
        private static readonly string[] ExcludedFolders =
        {
            "/SDK/Editor/UI/",
            "/SDK/Editor/ConfigurationWindow/",
            "/SDK/Editor/Settings/",
            "/Tests/",
            "/Plugins/"
        };

        /// <summary>A direct text measurement: <c>style.CalcHeight(…)</c> or <c>style.CalcSize(…)</c>.</summary>
        private static readonly Regex DirectMeasurement = new(
            @"\.Calc(Height|Size)\s*\(", RegexOptions.Compiled);

        /// <summary>A <see cref="UnityEngine.GUIContent" /> built solely in order to be measured.</summary>
        private static readonly Regex MeasuringAllocation = new(
            @"\.Calc(Height|Size)\s*\(\s*new\s+GUIContent", RegexOptions.Compiled);

        /// <summary>
        ///     The hand-rolled refresh throttle: a stored deadline compared against the editor clock.
        /// </summary>
        private static readonly Regex HandRolledThrottle = new(
            @"EditorApplication\.timeSinceStartup\s*(>=|<|>|<=)", RegexOptions.Compiled);

        /// <summary>
        ///     Measuring text runs the text generator. Inside a draw pass that happens on every
        ///     repaint, which is every mouse move over the Inspector — so measurement goes through
        ///     <c>ConvaiEditorTextMetrics</c>, which memoises on the arguments that determine the
        ///     answer and drops the table when the skin or the display scale changes.
        /// </summary>
        [Test]
        public void EditorSourceFiles_MeasureTextThroughTheSharedCache() =>
            AssertNoMatches(
                DirectMeasurement,
                "direct text measurement",
                "Measure through ConvaiEditorTextMetrics.WrappedHeight / .Width (SDK/Editor/UI). " +
                "CalcHeight and CalcSize run the text generator, and a draw pass runs on every repaint.");

        /// <summary>
        ///     Allocating a <c>GUIContent</c> as an argument to a measurement allocates once per
        ///     repaint for a value that is thrown away immediately. The metrics cache owns a scratch
        ///     content for exactly this, so no call site needs one.
        /// </summary>
        /// <remarks>
        ///     Checked across every editor file including the design system's own, because there is no
        ///     legitimate version of this shape anywhere.
        /// </remarks>
        [Test]
        public void EditorSourceFiles_AllocateNothingToMeasureIt()
        {
            var offenders = new List<string>();

            foreach (string path in EditorSourceFiles(applyExclusions: false))
            {
                int count = MeasuringAllocation.Matches(File.ReadAllText(path)).Count;
                if (count > 0)
                    offenders.Add($"{Path.GetFileName(path)} ({count})");
            }

            Assert.IsEmpty(
                offenders,
                "A GUIContent was allocated purely to be measured, which allocates once per repaint. " +
                "Pass the string to ConvaiEditorTextMetrics instead; it owns the scratch content.\n" +
                string.Join("\n", offenders));
        }

        /// <summary>
        ///     Files allowed to compare the editor clock themselves, and why each one is not a
        ///     draw-path refresh throttle.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Everything on this list paces work that happens <em>outside</em> a GUI pass, where
        ///         there is no <see cref="UnityEngine.Event" /> to gate on and
        ///         <c>ConvaiEditorRefreshTimer</c> therefore does not apply. That is the whole
        ///         boundary: inside a draw path, use the shared timer; driving
        ///         <c>EditorApplication.update</c>, own your clock.
        ///     </para>
        ///     <para>
        ///         The list is deliberately short and named file by file. Adding to it is a decision
        ///         someone has to write down here, which is the point — six editors had each grown
        ///         their own copy of the throttle before it was shared, and none of those was a
        ///         decision either.
        ///     </para>
        /// </remarks>
        private static readonly (string File, string Why)[] ClockExceptions =
        {
            ("SceneTaggerWindow.cs",
                "paces the drafting elapsed-time Repaint from EditorApplication.update, outside a GUI pass"),
            ("ConvaiEmotionTimelineWindow.cs",
                "paces Repaint() from EditorApplication.update — no GUI pass, so no Layout event to gate on"),
            ("ConvaiLipSyncDriftMonitorWindow.cs",
                "same repaint pacing, plus a console-summary interval that is not editor UI at all"),
            ("ConvaiAICodingSetupWindow.cs",
                "polls an external tool registry from a repair state machine driven by EditorApplication.update"),
            ("ConvaiGazeControllerEditor.cs",
                "caches an edit-time rig lookup for a DrawGizmo callback, which runs outside the inspector's GUI pass")
        };

        /// <summary>
        ///     Six editors had each written the same "cache, deadline, refresh only on Layout"
        ///     throttle, at intervals that had already drifted, and two surfaces had simply forgotten
        ///     to — re-scanning every open scene on every mouse move. The pattern now lives in
        ///     <c>ConvaiEditorRefreshTimer</c>, and a fresh copy fails here.
        /// </summary>
        /// <remarks>
        ///     The Layout gate is the part a re-implementation tends to lose. IMGUI reserves layout
        ///     during the Layout pass and draws against those reservations during Repaint, so cached
        ///     content that changes between the two is drawn against a layout computed without it.
        ///     A throttle that refreshes on any event produces clipped and overlapping panels that
        ///     look like a drawing bug rather than a timing one.
        /// </remarks>
        [Test]
        public void EditorSourceFiles_ThrottleDrawPathRefreshesThroughTheSharedTimer()
        {
            var offenders = new List<string>();

            foreach (string path in EditorSourceFiles())
            {
                string file = Path.GetFileName(path);
                if (ClockExceptions.Any(e => string.Equals(e.File, file, StringComparison.Ordinal)))
                    continue;

                int count = HandRolledThrottle.Matches(File.ReadAllText(path)).Count;
                if (count > 0)
                    offenders.Add($"{file} ({count})");
            }

            Assert.IsEmpty(
                offenders,
                "A refresh throttle was hand-rolled against EditorApplication.timeSinceStartup. Use " +
                "ConvaiEditorRefreshTimer (SDK/Editor/UI): it owns the interval and the Layout-event " +
                "gate that a re-implementation tends to drop. If the work genuinely happens outside a " +
                "GUI pass, add the file to ClockExceptions in this test with the reason.\n" +
                string.Join("\n", offenders));
        }

        /// <summary>
        ///     A draw path may not scan the scene. This is the defect that motivated the whole pass:
        ///     the Actions Editor window walked every open scene for characters inside
        ///     <c>OnGUI</c> — while also setting <c>wantsMouseMove</c> and repainting on every
        ///     mouse-move event — and the action-set inspector re-scanned for its "used by" count on
        ///     every repaint.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         What makes this class of bug worth its own rule is that its cost scales with the
        ///         <em>user's</em> scene rather than with anything on screen, so it is invisible in the
        ///         small test scene an author develops against and painful in the large one a customer
        ///         ships. Cache the result and refresh it through <c>ConvaiEditorRefreshTimer</c>.
        ///     </para>
        ///     <para>
        ///         Method-scoped by brace matching rather than file-scoped: a helper that scans and a
        ///         draw method that calls it are the correct arrangement, and only the draw method
        ///         doing it inline is the defect.
        ///     </para>
        /// </remarks>
        [Test]
        public void DrawPaths_DoNotScanTheScene()
        {
            // ConvaiObjectFind is the version-compatibility seam that now wraps every
            // FindObjectsByType call in the package. It must be listed here: without it this rule
            // would stop seeing scene scans the moment a call site moved behind the seam.
            var sceneScan = new Regex(
                @"\b(FindObjectsByType|FindObjectsOfType|FindObjectsOfTypeAll|GetComponentsInChildren" +
                @"|ConvaiObjectFind\.All)\s*[<(]",
                RegexOptions.Compiled);
            var drawSignature = new Regex(
                @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:private|protected|internal|public|static|sealed|override|virtual|new)\s+)*" +
                @"[\w\.<>\[\],\?]+\s+(?<name>Draw\w*|OnGUI|OnInspectorGUI)\s*\(",
                RegexOptions.Compiled);

            var offenders = new List<string>();

            foreach (string path in EditorSourceFiles())
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    Match signature = drawSignature.Match(lines[i]);
                    if (!signature.Success)
                        continue;

                    int end = EndOfMethod(lines, i);
                    string body = string.Join("\n", lines, i, end - i + 1);
                    if (sceneScan.IsMatch(body))
                    {
                        offenders.Add(
                            $"{Path.GetFileName(path)}:{i + 1} {signature.Groups["name"].Value}");
                    }

                    i = end;
                }
            }

            Assert.IsEmpty(
                offenders,
                "A draw path scans the scene. Its cost scales with the user's scene rather than with " +
                "what is on screen, and a draw path runs on every repaint — which is every mouse move " +
                "over the surface. Cache the result and refresh it through ConvaiEditorRefreshTimer " +
                "(SDK/Editor/UI).\n" + string.Join("\n", offenders));
        }

        /// <summary>Index of the line closing the method whose signature is on <paramref name="start" />.</summary>
        private static int EndOfMethod(string[] lines, int start)
        {
            int depth = 0;
            bool opened = false;

            for (int i = start; i < lines.Length; i++)
            {
                depth += lines[i].Count(c => c == '{') - lines[i].Count(c => c == '}');
                if (lines[i].IndexOf('{') >= 0)
                    opened = true;
                if (opened && depth <= 0)
                    return i;
            }

            return lines.Length - 1;
        }

        /// <summary>
        ///     One measurement cache and one refresh throttle, in the same spirit as the design
        ///     system's one palette and one style cache. A second of either is a fork that will drift.
        /// </summary>
        [Test]
        public void DesignSystem_DeclaresOneMeasurementCacheAndOneThrottle()
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(UiFolder(), "ConvaiEditorTextMetrics.cs")),
                "The shared text-measurement cache must live at SDK/Editor/UI/ConvaiEditorTextMetrics.cs — " +
                "the other tests in this class point authors at it by name.");

            Assert.IsTrue(
                File.Exists(Path.Combine(UiFolder(), "ConvaiEditorRefreshTimer.cs")),
                "The shared refresh throttle must live at SDK/Editor/UI/ConvaiEditorRefreshTimer.cs.");
        }

        private static void AssertNoMatches(Regex pattern, string what, string remedy)
        {
            var offenders = new List<string>();

            foreach (string path in EditorSourceFiles())
            {
                int count = pattern.Matches(File.ReadAllText(path)).Count;
                if (count > 0)
                    offenders.Add($"{Path.GetFileName(path)} ({count})");
            }

            Assert.IsEmpty(
                offenders,
                $"A {what} appeared in a Convai editor source file. {remedy}\n" + string.Join("\n", offenders));
        }

        private static string PackageRoot()
        {
            string root = Path.GetFullPath(Path.Combine(
                UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));
            if (!Directory.Exists(root))
                Assert.Ignore($"Package root not found at {root}.");

            return root;
        }

        private static string UiFolder() =>
            Path.Combine(PackageRoot(), "SDK", "Editor", "UI");

        /// <summary>Every first-party IMGUI editor source file these rules apply to.</summary>
        private static IEnumerable<string> EditorSourceFiles(bool applyExclusions = true)
        {
            foreach (string path in Directory.EnumerateFiles(PackageRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string normalized = path.Replace('\\', '/');
                if (!normalized.Contains("/Editor/", StringComparison.Ordinal))
                    continue;
                if (normalized.Contains("/Plugins/", StringComparison.Ordinal))
                    continue;
                if (applyExclusions &&
                    ExcludedFolders.Any(f => normalized.Contains(f, StringComparison.Ordinal)))
                    continue;

                yield return path;
            }
        }
    }
}
#endif
