using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Every profile-bearing component inspector shows which settings asset it is running on,
    ///     the same way.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         All five had drifted apart. Gaze had no field at all — the only entry point was a
    ///         button that creates a new asset, so a shipped profile could be assigned only through
    ///         the Debug inspector or by hand-editing the scene, which is how the LipSync sample
    ///         acquired two dangling references. Emotion had the field filed under a collapsed
    ///         <c>Advanced</c> section while its name was appended to a header the user could not
    ///         act on. Body Animation's sat below the readout it explains. Body Language's was a
    ///         plain card at the bottom of the inspector. Conversation Flow's said nothing about its
    ///         state.
    ///     </para>
    ///     <para>
    ///         A guard rather than a convention, because the failure is invisible: each inspector
    ///         looks reasonable on its own and the inconsistency only shows when a user meets the
    ///         second one. That is also why a mixed result would be worse than the original — it
    ///         makes the divergence look deliberate.
    ///     </para>
    /// </remarks>
    public sealed class ProfileFieldConsistencyGuardTests
    {
        private const string PackageRoot = "Packages/com.convai.convai-sdk-for-unity/";

        /// <summary>
        ///     The five inspectors that own a settings asset, and the section each one draws it in.
        /// </summary>
        private static readonly string[] ProfileBearingInspectors =
        {
            "SDK/Modules/Emotion/Editor/ConvaiEmotionControllerEditor.cs",
            "SDK/Modules/Gaze/Editor/ConvaiGazeControllerEditor.cs",
            "SDK/Modules/BodyLanguage/Editor/ConvaiBodyLanguageControllerEditor.cs",
            "SDK/Modules/BodyAnimation/Editor/Inspectors/ConvaiBodyAnimationControllerEditor.cs",
            "SDK/Editor/Embodiment/Inspectors/EmbodimentComponentEditors.cs"
        };

        private static string Read(string relative)
        {
            string path = Path.GetFullPath(PackageRoot + relative);
            Assert.IsTrue(File.Exists(path),
                $"A profile-bearing inspector was not found at {relative}. If it moved, update this " +
                "guard — do not delete it.");
            return File.ReadAllText(path);
        }

        [Test]
        public void EveryProfileBearingInspector_DrawsTheAssetThroughTheSharedField()
        {
            var offenders = new List<string>();

            foreach (string relative in ProfileBearingInspectors)
                if (!Read(relative).Contains("ConvaiEditorProfileField.Draw"))
                    offenders.Add(relative);

            Assert.IsEmpty(offenders,
                "These inspectors do not draw their settings asset through ConvaiEditorProfileField " +
                "(SDK/Editor/UI). The asset must be the first row of the section that tunes it and " +
                "must be drawn whether or not one is assigned, so that seeing it, swapping it and " +
                "clearing it are the same gesture:\n  " + string.Join("\n  ", offenders));
        }

        [Test]
        public void EveryProfileBearingInspector_ReportsItsAssetInASectionSummary()
        {
            var offenders = new List<string>();

            foreach (string relative in ProfileBearingInspectors)
                if (!Read(relative).Contains("ConvaiEditorProfileField.Summarize"))
                    offenders.Add(relative);

            Assert.IsEmpty(offenders,
                "These inspectors do not pass ConvaiEditorProfileField.Summarize as their section " +
                "summary, so a collapsed section does not say which asset the character is on — and " +
                "the empty state gets a wording of its own instead of the shared one:\n  " +
                string.Join("\n  ", offenders));
        }

        /// <summary>
        ///     The section framework has a <c>summary</c> parameter for exactly this. Two inspectors
        ///     had instead concatenated the asset name into the section <em>title</em>, which puts
        ///     state in the one place a user cannot act on and makes the header a different shape
        ///     from every other section in the SDK.
        /// </summary>
        [Test]
        public void NoInspector_ConcatenatesStateIntoASectionTitle()
        {
            var titleConcatenation = new Regex(@"DrawSection\([^;]*?\$""[^""]*\s+—\s+", RegexOptions.Singleline);
            var offenders = new List<string>();

            foreach (string relative in ProfileBearingInspectors)
                if (titleConcatenation.IsMatch(Read(relative)))
                    offenders.Add(relative);

            Assert.IsEmpty(offenders,
                "These inspectors build a section title by appending state to it. Pass the state as " +
                "DrawSection's summary parameter instead — it is right-aligned, it is what every " +
                "other section does, and it keeps the title a stable thing to look for:\n  " +
                string.Join("\n  ", offenders));
        }
    }
}
