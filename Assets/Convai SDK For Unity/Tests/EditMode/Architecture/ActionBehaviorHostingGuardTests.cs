using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps every authoring path that creates an action behavior agreeing about which object it
    ///     lands on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Action behaviors may live on the Convai Character or on a child object assigned as its
    ///         action behaviors object. Both run identically — but five separate editor paths used to
    ///         call <c>Undo.AddComponent(source.gameObject, executorType)</c> directly: the Actions
    ///         Editor's inline picker, its shared-action card, its starter-set application, the
    ///         MCP/AI authoring service, and the Troubleshooter's own "add the missing behavior" fix.
    ///         A user who arranged behaviors onto a child had that arrangement silently undone by the
    ///         next thing they clicked, in any of five places, which made an organized hierarchy
    ///         impossible to keep rather than merely undocumented.
    ///     </para>
    ///     <para>
    ///         The rule is narrow on purpose. Editor code legitimately adds plenty of other things
    ///         with the same call — embodiment module controllers, a behavior's required peer, a
    ///         component on an action target — and every one of those belongs on the object it names,
    ///         not on the behaviors object. What must go through the seam is specifically the
    ///         creation of an <em>action behavior</em>, which editor code identifies by an executor
    ///         type.
    ///     </para>
    /// </remarks>
    public sealed class ActionBehaviorHostingGuardTests
    {
        /// <summary>The only file allowed to create an action behavior component.</summary>
        private const string SeamFileName = "ConvaiActionBehaviorHosting.cs";

        /// <summary>
        ///     Identifiers editor code uses to mean "the type of an action behavior". A call adding a
        ///     component of one of these is creating a behavior, whatever else it looks like.
        /// </summary>
        private static readonly string[] ExecutorTypeIdentifiers =
        {
            "executorType",
            "ExecutorType",
            "behaviorType",
            "BehaviorType"
        };

        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        [Test]
        [Category("Architecture")]
        public void OnlyTheHostingSeam_CreatesActionBehaviorComponents()
        {
            string editorRoot = Path.Combine(PackageRoot, "SDK", "Editor");
            Assert.That(Directory.Exists(editorRoot), Is.True, $"Expected editor sources at '{editorRoot}'.");

            var violations = new List<string>();

            foreach (string file in Directory.EnumerateFiles(editorRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), SeamFileName, StringComparison.Ordinal))
                    continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (AddsAnActionBehavior(lines[i]))
                        violations.Add($"{Path.GetFileName(file)}:{i + 1} — {lines[i].Trim()}");
                }
            }

            Assert.IsEmpty(violations,
                $"Creating an action behavior must go through {SeamFileName}, so every authoring path puts " +
                "it on the same object — the character's action behaviors object when one is assigned, the " +
                "character itself otherwise. Adding it directly means this path quietly disagrees with the " +
                "others and undoes the author's arrangement:\n" + string.Join(Environment.NewLine, violations));
        }

        /// <summary>
        ///     Whether <paramref name="line" /> adds a component whose type is an action behavior's.
        ///     Comment lines are skipped so the seam's own explanation of the banned call, and any
        ///     documentation of it, do not read as violations.
        /// </summary>
        private static bool AddsAnActionBehavior(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal) ||
                trimmed.StartsWith("/*", StringComparison.Ordinal))
                return false;

            if (!line.Contains("AddComponent", StringComparison.Ordinal))
                return false;

            foreach (string identifier in ExecutorTypeIdentifiers)
            {
                if (line.Contains(identifier, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
