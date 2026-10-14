using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps every shipped Action Behavior indifferent to which GameObject it sits on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Action behaviors are supported both on the Convai Character itself and on a child
    ///         object that holds the character's behaviors. A behavior that reads its own
    ///         <c>transform</c> for a world-space position or rotation quietly assumes the first
    ///         layout, and on the second it acts relative to the wrong object.
    ///     </para>
    ///     <para>
    ///         This guard exists because of a real defect, not a hypothetical one: Return To Start
    ///         captured the spot to walk home to from <c>transform.position</c> in <c>Awake</c>, so a
    ///         character whose behaviors lived on a child object that had been nudged off the origin
    ///         walked home to the wrong place — with no error, no warning, and nothing in the
    ///         inspector to suggest why. The fix was
    ///         <c>ConvaiActionExecutorBase.CharacterTransform</c>; this test is what stops the next
    ///         behavior from reintroducing the assumption.
    ///     </para>
    ///     <para>
    ///         Only world-space members are flagged. A behavior is still free to use its own
    ///         <c>transform</c> for parenting, hierarchy walks, or anything else that genuinely means
    ///         "this component's own object" — the rule is about where the character *is*, not about
    ///         touching the property at all.
    ///     </para>
    /// </remarks>
    public sealed class ActionBehaviorHostAgnosticGuardTests
    {
        /// <summary>
        ///     Members whose value only makes sense relative to the Convai Character. Reading any of
        ///     these off a behavior's own transform is the bug this guard catches.
        /// </summary>
        private static readonly string[] WorldSpaceMembers =
        {
            "position",
            "rotation",
            "forward",
            "right",
            "up",
            "localPosition",
            "localRotation",
            "eulerAngles",
            "localEulerAngles",
            "LookAt",
            "Rotate",
            "Translate",
            "RotateAround",
            "SetPositionAndRotation"
        };

        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        [Test]
        [Category("Architecture")]
        public void ActionBehaviors_DoNotReadWorldSpaceFromTheirOwnTransform()
        {
            string sdkRoot = Path.Combine(PackageRoot, "SDK");
            Assert.That(Directory.Exists(sdkRoot), Is.True, $"Expected SDK sources at '{sdkRoot}'.");

            var violations = new List<string>();
            int scanned = 0;

            foreach (string file in Directory.EnumerateFiles(sdkRoot, "*ActionExecutor.cs", SearchOption.AllDirectories))
            {
                scanned++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsCommentLine(lines[i]))
                        continue;

                    string member = FindBareTransformWorldSpaceUse(lines[i]);
                    if (member != null)
                        violations.Add($"{Path.GetFileName(file)}:{i + 1} reads transform.{member}");
                }
            }

            Assert.Greater(scanned, 0, "Expected at least one shipped Action Behavior source to be discovered.");
            Assert.IsEmpty(violations,
                "An Action Behavior must read world-space position and rotation from " +
                "ConvaiActionExecutorBase.CharacterTransform, never from its own transform: behaviors are " +
                "supported both on the Convai Character and on a child object that holds them, and on the " +
                "second layout its own transform is not the character's:\n" +
                string.Join(Environment.NewLine, violations));
        }

        /// <summary>
        ///     Returns the world-space member accessed through a bare <c>transform</c> on
        ///     <paramref name="line" />, or null when there is none. A <c>transform</c> preceded by an
        ///     identifier character or a dot belongs to something else — <c>locomotion.transform</c>,
        ///     <c>CharacterTransform</c>, <c>_homeSpot.transform</c> — and is exactly what this rule
        ///     asks behaviors to use, so it must not be flagged.
        /// </summary>
        private static string FindBareTransformWorldSpaceUse(string line)
        {
            const string token = "transform.";
            int index = line.IndexOf(token, StringComparison.Ordinal);
            while (index >= 0)
            {
                bool isBare = index == 0 ||
                              (!IsIdentifierCharacter(line[index - 1]) && line[index - 1] != '.');
                if (isBare)
                {
                    int memberStart = index + token.Length;
                    foreach (string member in WorldSpaceMembers)
                    {
                        if (MatchesMemberAt(line, memberStart, member))
                            return member;
                    }
                }

                index = line.IndexOf(token, index + 1, StringComparison.Ordinal);
            }

            return null;
        }

        private static bool MatchesMemberAt(string line, int start, string member)
        {
            if (start + member.Length > line.Length)
                return false;

            if (string.CompareOrdinal(line, start, member, 0, member.Length) != 0)
                return false;

            int after = start + member.Length;
            return after >= line.Length || !IsIdentifierCharacter(line[after]);
        }

        /// <summary>
        ///     Whether the line is prose rather than code. XML documentation legitimately names
        ///     <c>transform.position</c> when explaining why behaviors must not use it, and a guard
        ///     that flags its own explanation is a guard people delete.
        /// </summary>
        private static bool IsCommentLine(string line)
        {
            string trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("*", StringComparison.Ordinal) ||
                   trimmed.StartsWith("/*", StringComparison.Ordinal);
        }

        private static bool IsIdentifierCharacter(char character) =>
            char.IsLetterOrDigit(character) || character == '_';
    }
}
