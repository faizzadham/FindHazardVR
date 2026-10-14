using Convai.Editor.Inspectors.Framework;
using Convai.Runtime.Components;
using UnityEditor;
using UnityEngine;
using Convai.Runtime.Room;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiPushToTalkController" /> — drives push-to-talk pressing
    ///     and releasing for a target character, resolving the active manager and character
    ///     automatically when no explicit references are assigned.
    /// </summary>
    [CustomEditor(typeof(ConvaiPushToTalkController))]
    internal sealed class ConvaiPushToTalkControllerEditor : ConvaiInspectorEditor
    {
        private const string SectionLiveId = "Live";

        private static readonly GUIContent LiveSection = new("Live");
        private static readonly GUIContent StateLabel = new("PTT State", "The push-to-talk state right now.");

        /// <summary>The field's own name, "Use Active Conversation Character", ran past the label column.</summary>
        private static readonly GUIContent UseActiveCharacterLabel = new(
            "Follow Active Character",
            "When no target character is assigned, follows the character the player is talking to " +
            "(ConvaiManager.AddressedCharacter).");

        protected override GUIContent FieldLabel(string fieldName) =>
            fieldName == "_useActiveConversationCharacter" ? UseActiveCharacterLabel : null;

        protected override string Title => "Push To Talk";
        protected override string Subtitle => "Convai Push To Talk Controller";

        protected override string Purpose =>
            "Opens the microphone while the player holds the push-to-talk key, and closes it when " +
            "they let go. Leave the fields below empty and it will find the character the player is " +
            "talking to on its own.";

        protected override void DrawLiveSection() =>
            DrawSection(SectionLiveId, LiveSection, Glyphs.Live, DrawLive, accent: Theme.StatusInfo);

        private void DrawLive()
        {
            var controller = (ConvaiPushToTalkController)target;
            Theme.KeyValueRow(StateLabel, controller.State.ToString());
            if (!string.IsNullOrEmpty(controller.CompatibilityReason))
                InfoBox("Compatibility", controller.CompatibilityReason);
            if (controller.State == PttState.Error)
                WarningBox("Push-to-talk blocked", controller.BlockedReason, "Retry PTT (Reconnect)",
                    () => _ = controller.RetryAsync());
        }

        protected override string EditorStateHostId => "ConvaiPushToTalkControllerEditor";
    }
}
