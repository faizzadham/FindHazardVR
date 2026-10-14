using Convai.Editor.Inspectors.Framework;
using Convai.Runtime;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;
using View = Convai.Editor.Inspectors.ConvaiRoomControlInspectorView;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiRoomManagerProfile" />: the room defaults a project
    ///     reuses across scenes.
    /// </summary>
    /// <remarks>
    ///     Laid out to match the Convai Room Manager component inspector card for card, and drawing
    ///     the same shared view, so the asset and the scene component cannot describe the same settings
    ///     two different ways.
    /// </remarks>
    [CustomEditor(typeof(ConvaiRoomManagerProfile))]
    internal sealed class ConvaiRoomManagerProfileEditor : ConvaiInspectorEditor
    {
        private const string TitleText = "Convai Room Manager Profile";

        private const string PurposeText =
            "Reusable room defaults. Assign this asset to a Convai Room Manager so several scenes " +
            "share one connection, listening, vision and reconnect setup.";

        private const string SectionConnectionId = "Connection";

        private static readonly GUIContent ConnectionSection = new("Connection");
        private static readonly GUIContent ReusableAssetChip = new("Reusable Asset");

        private SerializedProperty _connectionTypeProp;
        private SerializedProperty _connectOnStartProp;
        private ConvaiRoomControlProperties _room;

        protected override string Title => TitleText;
        protected override string Purpose => PurposeText;
        protected override GUIContent StatusChip => ReusableAssetChip;
        protected override Color StatusChipTint => Theme.StatusInfo;

        protected override void OnEnable()
        {
            base.OnEnable();

            _connectionTypeProp = serializedObject.FindProperty("_connectionType");
            _connectOnStartProp = serializedObject.FindProperty("_connectOnStart");
            _room = new ConvaiRoomControlProperties
            {
                ConnectionTypeForDisplay = _connectionTypeProp,
                VideoTrackName = serializedObject.FindProperty("_videoTrackName"),
                ServerEndpoint = serializedObject.FindProperty("_serverEndpoint"),
                TurnTakingOptions = serializedObject.FindProperty("_turnTakingOptions"),
                UserVadSettings = serializedObject.FindProperty("_userVadSettings"),
                VisionContextMode = serializedObject.FindProperty("_visionContextMode"),
                VisionInputSettings = serializedObject.FindProperty("_visionInputSettings"),
                VisionRespondModes = serializedObject.FindProperty("_visionRespondModes"),
                RoomRejoinTtlSeconds = serializedObject.FindProperty("_roomRejoinTtlSeconds"),
                ResumePolicy = serializedObject.FindProperty("_resumePolicy"),
                MaxReconnectAttempts = serializedObject.FindProperty("_maxReconnectAttempts"),
                SpawnAgentOnRejoin = serializedObject.FindProperty("_spawnAgentOnRejoin"),
                StartWaitTimeoutMs = serializedObject.FindProperty("_startWaitTimeoutMs"),
                AutoMicStartDelaySeconds = serializedObject.FindProperty("_autoMicStartDelaySeconds")
            };
        }

        protected override void DrawBody()
        {
            DrawSection(View.ConversationSectionId, View.ConversationSection, Glyphs.Content,
                () => View.DrawConversation(_room, false), summary: View.ConversationSummary(_room));
            DrawConnectionSection();

            DrawSection(View.ListeningSectionId, View.ListeningSection, Glyphs.Capture,
                () => View.DrawListening(_room, false), false, summary: View.ListeningSummary(_room));
            DrawSection(View.InterruptionsSectionId, View.InterruptionsSection, Glyphs.Reaction,
                () => View.DrawInterruptions(_room, false), false, summary: View.InterruptionsSummary(_room));
            DrawSection(View.VisionSectionId, View.VisionSection, Glyphs.Visibility,
                () => View.DrawVision(_room, false), false, summary: View.VisionSummary(_room));
            DrawSection(View.ReconnectSectionId, View.ReconnectSection, Glyphs.Routing,
                () => View.DrawReconnect(_room, false), false, summary: View.ReconnectSummary(_room));
        }

        /// <summary>
        ///     The two decisions a room makes about connecting, exactly where the component inspector
        ///     puts them.
        /// </summary>
        private void DrawConnectionSection() =>
            DrawSection(SectionConnectionId, ConnectionSection, Glyphs.Routing, () =>
            {
                if (_connectionTypeProp != null)
                    Theme.EnumSegments(
                        ConvaiInspectorContent.ConnectionType, ConvaiInspectorContent.ConnectionTypeOptions,
                        _connectionTypeProp);

                if (_connectOnStartProp != null)
                    EditorGUILayout.PropertyField(_connectOnStartProp, ConvaiInspectorContent.ConnectOnStart);
            });
    }
}
