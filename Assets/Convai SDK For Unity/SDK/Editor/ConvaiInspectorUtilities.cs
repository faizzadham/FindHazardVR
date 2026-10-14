using Convai.Editor.UI;
using Convai.Shared.Types;
using UnityEditor;
using UnityEngine;

namespace Convai.Editor
{
    /// <summary>
    ///     Whether inspectors reveal the fields that only make sense while developing the SDK itself.
    /// </summary>
    /// <remarks>
    ///     Off unless the <c>Convai.Editor.ShowDeveloperInspectorSettings</c> Editor preference is set,
    ///     and the SDK ships no menu item to set it — a consumer has no reason to meet these fields, and a
    ///     "Developer" drawer in a shipped product reads as someone else's workbench left in the box.
    ///     Setting the preference is therefore a concern of the SDK's own development project, whose
    ///     internal tooling is not part of this package. No shipped command toggles it — set the
    ///     preference directly when you need these fields.
    /// </remarks>
    internal static class ConvaiInspectorDeveloperSettings
    {
        internal const string InspectorDeveloperVisibilityPrefKey = "Convai.Editor.ShowDeveloperInspectorSettings";

        internal static bool AreInspectorDeveloperSettingsVisible =>
            EditorPrefs.GetBool(InspectorDeveloperVisibilityPrefKey, false);
    }

    internal static class ConvaiInspectorFieldUtility
    {
        private static readonly string[] PublicServerEndpointOptions = { "Connect" };

        internal static void DrawServerEndpointField(SerializedProperty property, GUIContent label)
        {
            if (property == null)
                return;

            if (ConvaiInspectorDeveloperSettings.AreInspectorDeveloperSettingsVisible)
            {
                EditorGUILayout.PropertyField(property, label);
                return;
            }

            var endpoint = (ConvaiServerEndpoint)property.enumValueIndex;
            if (endpoint == ConvaiServerEndpoint.Connect)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.Popup(label, 0, PublicServerEndpointOptions);

                return;
            }

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(label, endpoint.ToString());

            ConvaiEditorFrame.WarningBox(
                "Internal Server Route",
                "This room is using an internal server route. Normal projects should use Connect.");
        }
    }
}
