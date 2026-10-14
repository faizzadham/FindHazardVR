using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Enforces the inspector/window split mechanically instead of by discipline.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The Gaze component inspector carries five things: is it working, what is wrong, who
    ///         it watches, how it feels, what it is doing now. Everything else belongs in the Gaze
    ///         editor window. That rule held for exactly as long as someone remembered it last
    ///         time — the previous inspector accumulated eight targeting controls, a live provider
    ///         list and a paragraph on arbitration before anyone noticed.
    ///     </para>
    ///     <para>
    ///         So the allowlist below is a deliberate review gate: adding a field to the inspector
    ///         means editing this test, which means someone has to argue that the field belongs on
    ///         a first-run surface.
    ///     </para>
    /// </remarks>
    public sealed class GazeInspectorSurfaceGuardTests
    {
        private const string InspectorPath =
            "Packages/com.convai.convai-sdk-for-unity/SDK/Modules/Gaze/Editor/ConvaiGazeControllerEditor.cs";

        /// <summary>
        ///     The only controller fields the component inspector may bind. Anything else is
        ///     window surface.
        /// </summary>
        private static readonly HashSet<string> Allowed = new()
        {
            "profile",              // the personality, and the dials drawn from it
            "playerAnchorOverride", // "Who is the player?"
            "eyeContactMode",       // "Eye contact style"

            // Whether the character follows other people's turns is a scene decision of the same
            // kind as eye contact style — one dropdown, changed per character rather than per
            // profile, and the first thing anyone reaches for in a room with more than one
            // participant. It is common path, not depth.
            "attendToSpeaker"       // "Follows the conversation"
        };

        private static readonly Regex FindProperty = new(@"FindProperty\(""([A-Za-z0-9_]+)""\)", RegexOptions.Compiled);

        private static List<string> BoundFields()
        {
            string path = Path.GetFullPath(InspectorPath);
            Assert.IsTrue(File.Exists(path),
                $"The gaze inspector was not found at {InspectorPath}. If it moved, update this " +
                "guard — do not delete it.");

            var bound = new List<string>();
            foreach (Match match in FindProperty.Matches(File.ReadAllText(path)))
                bound.Add(match.Groups[1].Value);

            return bound;
        }

        [Test]
        public void TheInspector_BindsOnlyTheAllowedFields()
        {
            var unexpected = new List<string>();
            foreach (string field in BoundFields())
                if (!Allowed.Contains(field))
                    unexpected.Add(field);

            Assert.IsEmpty(unexpected,
                "These fields are drawn on the Gaze component inspector but are not on its " +
                "allowlist. The inspector carries the common path only; depth goes in the Gaze " +
                "editor window. If one of these genuinely belongs here, add it to the allowlist and " +
                "say why in the review:\n  " + string.Join("\n  ", unexpected));
        }

        [Test]
        public void TheInspector_LinksToTheEditorWindow()
        {
            string source = File.ReadAllText(Path.GetFullPath(InspectorPath));
            Assert.IsTrue(source.Contains("ConvaiGazeEditorWindow.ShowFor"),
                "The footer link is the only documented route to the deeper surface. Without it the " +
                "window is unreachable from any flow a user actually follows.");
        }

        [Test]
        public void TheInspector_DoesNotLeakInternalVocabulary()
        {
            string source = File.ReadAllText(Path.GetFullPath(InspectorPath));

            // Only the user-visible strings matter, so this checks the quoted label text rather
            // than identifiers or the class documentation, which legitimately name the old fields
            // while explaining why they are gone.
            var offenders = new List<string>();
            foreach (Match match in Regex.Matches(source, @"new GUIContent\(\s*""([^""]+)"""))
            {
                string label = match.Groups[1].Value.ToLowerInvariant();
                foreach (string term in new[] { "anchor", "fidelity", "arbiter", "provider", "tier", "saccade" })
                    if (label.Contains(term))
                        offenders.Add($"'{match.Groups[1].Value}' (contains '{term}')");
            }

            Assert.IsEmpty(offenders,
                "These control labels use internal vocabulary on the module's first-run surface:\n  " +
                string.Join("\n  ", offenders));
        }
    }
}
