using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Convai.Runtime.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps the shipped Action Behavior library reshapeable: nothing outside the library itself
    ///     is allowed to know which concrete behaviors exist.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This guard exists because of a real failure. Editor authoring code used to call
    ///         <c>Undo.AddComponent&lt;UnityEventActionExecutor&gt;(host)</c> and reflect on that
    ///         behavior's private event field to detect an unwired placeholder — so deleting one
    ///         component out of the library broke the editor's compile, in three places, none of
    ///         which had anything to do with the behavior being removed. The fix was a single seam
    ///         (<c>ConvaiActionsAuthoringDefaults</c>); this test is what stops the coupling from
    ///         growing back one convenient reference at a time.
    ///     </para>
    ///     <para>
    ///         Framework types are deliberately not covered — editor code is expected to name
    ///         <c>ConvaiActionExecutorBase</c>, <c>IConvaiActionExecutor</c>, and the binder. The rule
    ///         is only about <em>concrete behaviors in the library</em>.
    ///     </para>
    /// </remarks>
    public sealed class ActionLibraryCouplingGuardTests
    {
        /// <summary>
        ///     The one file allowed to name a concrete Action Behavior: the seam that answers "which
        ///     behavior stands in for an action nobody has wired up yet".
        /// </summary>
        private static readonly string[] SeamFileNames =
        {
            "ConvaiActionsAuthoringDefaults.cs"
        };

        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        [Test]
        [Category("Architecture")]
        public void EditorCode_DoesNotNameConcreteActionBehaviors()
        {
            HashSet<string> behaviorNames = ActionExecutorArchitectureGuardTests.ShippedExecutorTypes()
                .Select(type => type.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.That(behaviorNames, Is.Not.Empty, "Expected at least one shipped Action Behavior to be discovered.");

            string editorRoot = Path.Combine(PackageRoot, "SDK", "Editor");
            Assert.That(Directory.Exists(editorRoot), Is.True, $"Expected editor sources at '{editorRoot}'.");

            var violations = new List<string>();
            foreach (string file in Directory.EnumerateFiles(editorRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (SeamFileNames.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                    continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (string behaviorName in behaviorNames)
                    {
                        if (ContainsIdentifier(lines[i], behaviorName))
                            violations.Add($"{Path.GetFileName(file)}:{i + 1} names {behaviorName}");
                    }
                }
            }

            Assert.IsEmpty(violations,
                "Editor code must not name a concrete Action Behavior — describe what it needs through the " +
                "archetype metadata or the authoring-defaults seam instead, so the shipped library can change " +
                "without editor code following it:\n" + string.Join(Environment.NewLine, violations));
        }

        [Test]
        [Category("Architecture")]
        public void EveryShippedActionBehavior_IsPickableFromTheCatalog()
        {
            var missing = new List<string>();
            int count = 0;

            foreach (Type type in ActionExecutorArchitectureGuardTests.ShippedExecutorTypes())
            {
                count++;
                var archetype = type.GetCustomAttribute<ConvaiActionArchetypeAttribute>();
                if (archetype == null || string.IsNullOrWhiteSpace(archetype.DisplayName))
                    missing.Add(type.FullName);
            }

            Assert.Greater(count, 0, "Expected at least one shipped Action Behavior to be discovered.");
            Assert.IsEmpty(missing,
                "Every shipped Action Behavior needs a [ConvaiActionArchetype] with a display name, or an author " +
                "can never find it in the Actions Editor catalog — a behavior nobody can discover is not shipped, " +
                "it is hidden:\n" + string.Join(Environment.NewLine, missing));
        }

        [Test]
        [Category("Architecture")]
        public void ActionBehaviors_DoNotUseWallClockDelays()
        {
            var violations = new List<string>();

            foreach (string file in EnumerateBehaviorSources())
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("Task.Delay", StringComparison.Ordinal) ||
                        lines[i].Contains("Thread.Sleep", StringComparison.Ordinal))
                        violations.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }

            Assert.IsEmpty(violations,
                "Action Behaviors must wait frame-wise through ConvaiActionAsyncUtility, never on wall-clock time: " +
                "Task.Delay ignores pausing and time scale, and Thread.Sleep is not even legal on WebGL:\n" +
                string.Join(Environment.NewLine, violations));
        }

        [Test]
        [Category("Architecture")]
        public void PublicSdkSources_DoNotContainDemoOrLabImplementationResidue()
        {
            string sdkRoot = Path.Combine(PackageRoot, "SDK");
            string[] forbidden =
            {
                "ConvaiDev",
                "ConvaiDemoKit",
                "Actions Lab",
                "Lab:",
                "Development-only"
            };

            var violations = new List<string>();
            foreach (string file in Directory.EnumerateFiles(sdkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    for (int token = 0; token < forbidden.Length; token++)
                    {
                        if (lines[i].Contains(forbidden[token], StringComparison.OrdinalIgnoreCase))
                            violations.Add($"{Path.GetFileName(file)}:{i + 1} contains '{forbidden[token]}'");
                    }
                }
            }

            Assert.IsEmpty(violations,
                "Public SDK sources must not expose development-demo implementation names or Lab copy:\n" +
                string.Join(Environment.NewLine, violations));
        }

        /// <summary>
        ///     Source files of the shipped behaviors, found by filename so the scan covers sources
        ///     rather than compiled metadata (which cannot tell a frame-wise wait from a wall-clock one).
        /// </summary>
        private static IEnumerable<string> EnumerateBehaviorSources()
        {
            string sdkRoot = Path.Combine(PackageRoot, "SDK");
            Assert.That(Directory.Exists(sdkRoot), Is.True, $"Expected SDK sources at '{sdkRoot}'.");

            return Directory.EnumerateFiles(sdkRoot, "*ActionExecutor.cs", SearchOption.AllDirectories);
        }

        /// <summary>
        ///     Whether <paramref name="line" /> uses <paramref name="identifier" /> as a whole word.
        ///     A substring match would flag <c>ConvaiWaitActionExecutorTests</c> for naming
        ///     <c>ConvaiWaitActionExecutor</c>, and a guard that cries wolf is a guard people learn to
        ///     ignore.
        /// </summary>
        private static bool ContainsIdentifier(string line, string identifier)
        {
            int index = line.IndexOf(identifier, StringComparison.Ordinal);
            while (index >= 0)
            {
                bool startsCleanly = index == 0 || !IsIdentifierCharacter(line[index - 1]);
                int after = index + identifier.Length;
                bool endsCleanly = after >= line.Length || !IsIdentifierCharacter(line[after]);

                if (startsCleanly && endsCleanly)
                    return true;

                index = line.IndexOf(identifier, index + 1, StringComparison.Ordinal);
            }

            return false;
        }

        private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';
    }
}
