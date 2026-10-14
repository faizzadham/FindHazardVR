using Convai.Editor.ConfigurationWindow.Services;
using UnityEngine;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     One validation check drawn the way every Convai inspector draws it, from a
    ///     <see cref="SetupHealthStatus" />.
    /// </summary>
    internal static class ConvaiInspectorChecks
    {
        internal static void Row(SetupHealthStatus status, string title, string message) =>
            Theme.CheckRow(Tint(status), Word(status), title, message);

        /// <summary>The colour a check's dot and status word take.</summary>
        internal static Color Tint(SetupHealthStatus status) =>
            status switch
            {
                SetupHealthStatus.Healthy => Theme.StatusReady,
                SetupHealthStatus.Warning => Theme.StatusWarn,
                _ => Theme.StatusError
            };

        /// <summary>The verdict in a word, so the row does not rely on colour alone; none when healthy.</summary>
        private static string Word(SetupHealthStatus status) =>
            status switch
            {
                SetupHealthStatus.Healthy => null,
                SetupHealthStatus.Warning => "Check this",
                _ => "Blocked"
            };
    }
}
