using System;
using System.Globalization;
using Convai.Editor.ConfigurationWindow.Services;
using Convai.Editor.Diagnostics;
using Convai.Editor.Inspectors.Framework;
using Convai.Infrastructure.Networking;
using Convai.Modules.Vision;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Sources;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using Convai.Editor.UI;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;
using View = Convai.Editor.Inspectors.ConvaiRoomControlInspectorView;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiRoomManager" />: how the player talks, how the scene
    ///     connects and a rolling setup health report, then one collapsed card per room feature —
    ///     Listening, Interruptions, Vision, Reconnect — the room source and the runtime readout.
    /// </summary>
    [CustomEditor(typeof(ConvaiRoomManager))]
    internal sealed class ConvaiRoomManagerEditor : ConvaiInspectorEditor
    {
        private const string TitleText = "Convai Room Manager";

        private const string PurposeText =
            "Owns the conversation room for this scene — connection, turn-taking and reconnect behavior.";

        private const string SectionConfigurationId = "Configuration";
        private const string SectionRuntimeId = "Runtime";

        /// <remarks>
        ///     Kept as "Scene" so a project that already collapsed or expanded this section keeps its
        ///     choice across the rename to Connection. The id is storage, not copy.
        /// </remarks>
        private const string SectionConnectionId = "Scene";

        private const string SectionValidationId = "Validation";

        private static readonly GUIContent ConnectionSection = new("Connection");
        private static readonly GUIContent ValidationSection = new("Validation");
        private static readonly GUIContent RoomSourceSection = new("Room Source");
        private static readonly GUIContent RuntimeSection = new("Runtime");

        private static readonly GUIContent PushToTalkChip = new("Push To Talk");
        private static readonly GUIContent HandsFreeChip = new("Hands Free");

        private static readonly GUIContent AddVisionComponentsButton = new("Add Missing Components");

        private static readonly GUIContent RunValidationButton = new(
            "Run Full Validation", "Re-run every check now and report the result in the Console.");

        private static readonly GUIContent OpenProjectSettingsButton = new(
            "Project Settings", "Open Project Settings → Convai SDK, where the API key and server live.");

        private static readonly GUIContent ClearLegacyOverrideButton = new(
            "Clear Saved URL", "Remove the unused server URL this older scene still stores.");

        private static readonly GUIContent OpenSdkSettingsButton = new(
            "Convai SDK Settings", "Open Project Settings → Convai SDK.");

        private static readonly GUIContent CopyConnectResponseButton = new(
            "Copy Connect Response",
            "Copy the whole connect response as JSON — room, session and trace IDs and anything else the " +
            "server sent — with SDK, Unity and platform details. The room token is redacted.");

        private static readonly GUIContent SessionGroup = new("Session");
        private static readonly GUIContent InEffectGroup = new("Settings In Effect");
        private static readonly GUIContent WiringGroup = new("Wiring");

        private static readonly GUIContent SessionStateLabel = new(
            "Session State", "Where this room is in its connect, run and disconnect cycle.");

        private static readonly GUIContent ConnectedLabel = new(
            "Connected", "Whether the room connection is open right now.");

        private static readonly GUIContent CurrentRoomLabel = new("Room", "The room this scene has joined.");

        private static readonly GUIContent SessionIdLabel = new(
            "Session ID", "Identifies this conversation to the service. Quote it in a support request.");

        private static readonly GUIContent ConnectResponseLabel = new(
            "Connect Response",
            "When the server last answered a connect in this Editor session. Kept after Play mode stops, " +
            "until the next connect.");

        private static readonly GUIContent CoreServerLabel = new(
            "Core Server", "Taken from Project Settings → Convai SDK, for every scene in the project.");

        private static readonly GUIContent VisionContextLabel = new(
            "Dynamic Vision", "Whether the character is being sent camera frames, and under which rule.");

        private static readonly GUIContent EffectiveConnectionLabel = new(
            "Connection Type", "The connection this room will actually open, once a profile has had its say.");

        private static readonly GUIContent RoomControllerLabel = new(
            "Room Controller", "The implementation driving this room. Diagnostic.");

        private static readonly GUIContent TransportLabel = new(
            "Transport", "The transport carrying this room's audio and messages. Diagnostic.");

        private const string NotConnectedValue = "Not connected";
        private const string NotCapturedValue = "Not captured yet";

        private SerializedProperty _autoMicStartDelaySecondsProp;
        private SetupHealthReport _cachedSetupHealthReport;
        private SerializedProperty _configurationSourceInitializedProp;
        private SerializedProperty _configurationSourceProp;
        private SerializedProperty _connectionTypeProp;
        private SerializedProperty _connectOnStartProp;
        private SerializedProperty _coreServerBaseUrlProp;
        private SerializedProperty _debugProp;
        private SerializedProperty _maxReconnectAttemptsProp;
        private ConvaiEditorRefreshTimer _validationTimer;
        private SerializedProperty _resumePolicyProp;
        private SerializedProperty _roomConfigAssetProp;
        private SerializedObject _roomConfigSerializedObject;
        private ConvaiRoomManager _roomManager;
        private SerializedProperty _roomPushToTalkKeyProp;
        private SerializedProperty _roomRejoinTtlSecondsProp;
        private SerializedProperty _serverEndpointProp;
        private SerializedProperty _spawnAgentOnRejoinProp;
        private SerializedProperty _startWaitTimeoutMsProp;
        private SerializedProperty _turnTakingOptionsProp;
        private SerializedProperty _userVadSettingsProp;
        private SerializedProperty _visionContextModeProp;
        private SerializedProperty _visionInputSettingsProp;
        private SerializedProperty _visionRespondModesProp;

        /// <summary>The card properties for an Inline room, built once per enable.</summary>
        private ConvaiRoomControlProperties _inlineProperties;

        /// <summary>The card properties for the assigned profile, rebuilt only when the profile changes.</summary>
        private ConvaiRoomControlProperties _profileProperties;

        private SerializedObject _profilePropertiesSource;
        private string _profileNote;
        private UnityEngine.Object _profileNoteSource;

        private ConvaiConnectResponseSnapshot _connectResponseShown;
        private string _connectResponseReading = NotCapturedValue;

        protected override string Title => TitleText;
        protected override string Purpose => PurposeText;

        protected override GUIContent StatusChip =>
            _roomManager != null &&
            _roomManager.EffectiveTurnTakingOptions.Mode == ConversationInputMode.PushToTalk
                ? PushToTalkChip
                : HandsFreeChip;

        protected override Color StatusChipTint => Theme.StatusInfo;

        private bool IsAssetModeSelected =>
            _configurationSourceProp != null &&
            (ConvaiConfigSourceMode)_configurationSourceProp.enumValueIndex == ConvaiConfigSourceMode.Asset;

        private bool HasLegacyCoreServerOverride =>
            _coreServerBaseUrlProp != null && !string.IsNullOrWhiteSpace(_coreServerBaseUrlProp.stringValue);

        protected override void OnEnable()
        {
            base.OnEnable();

            _roomManager = (ConvaiRoomManager)target;

            _configurationSourceProp = serializedObject.FindProperty("_configurationSource");
            _configurationSourceInitializedProp = serializedObject.FindProperty("_configurationSourceInitialized");
            _roomConfigAssetProp = serializedObject.FindProperty("_roomConfigAsset");
            _connectionTypeProp = serializedObject.FindProperty("_connectionType");
            _coreServerBaseUrlProp = serializedObject.FindProperty("<CoreServerBaseURL>k__BackingField");
            _serverEndpointProp = serializedObject.FindProperty("<ServerEndpoint>k__BackingField");
            _connectOnStartProp = serializedObject.FindProperty("<ConnectOnStart>k__BackingField");
            _turnTakingOptionsProp = serializedObject.FindProperty("_turnTakingOptions");
            _userVadSettingsProp = serializedObject.FindProperty("_userVadSettings");
            _visionContextModeProp = serializedObject.FindProperty("_visionContextMode");
            _visionInputSettingsProp = serializedObject.FindProperty("_visionInputSettings");
            _visionRespondModesProp = serializedObject.FindProperty("_visionRespondModes");
            _roomPushToTalkKeyProp = serializedObject.FindProperty("_pushToTalkKey");
            _debugProp = serializedObject.FindProperty("<Debug>k__BackingField");
            _roomRejoinTtlSecondsProp = serializedObject.FindProperty("_roomRejoinTtlSeconds");
            _resumePolicyProp = serializedObject.FindProperty("_resumePolicy");
            _maxReconnectAttemptsProp = serializedObject.FindProperty("_maxReconnectAttempts");
            _spawnAgentOnRejoinProp = serializedObject.FindProperty("_spawnAgentOnRejoin");
            _startWaitTimeoutMsProp = serializedObject.FindProperty("_startWaitTimeoutMs");
            _autoMicStartDelaySecondsProp = serializedObject.FindProperty("_autoMicStartDelaySeconds");

            _inlineProperties = new ConvaiRoomControlProperties
            {
                PushToTalkKey = _roomPushToTalkKeyProp,
                ConnectionTypeForDisplay = _connectionTypeProp,
                ServerEndpoint = _serverEndpointProp,
                TurnTakingOptions = _turnTakingOptionsProp,
                UserVadSettings = _userVadSettingsProp,
                VisionContextMode = _visionContextModeProp,
                VisionInputSettings = _visionInputSettingsProp,
                VisionRespondModes = _visionRespondModesProp,
                RoomRejoinTtlSeconds = _roomRejoinTtlSecondsProp,
                ResumePolicy = _resumePolicyProp,
                MaxReconnectAttempts = _maxReconnectAttemptsProp,
                SpawnAgentOnRejoin = _spawnAgentOnRejoinProp,
                StartWaitTimeoutMs = _startWaitTimeoutMsProp,
                AutoMicStartDelaySeconds = _autoMicStartDelaySecondsProp
            };

            EnsureConfigurationSourceMigration();
        }

        protected override void OnBeforeInspectorGUI() => EnsureConfigurationSourceMigration();

        /// <summary>Keeps the Runtime section live.</summary>
        /// <remarks>
        ///     Everything that section reports moves without anyone touching the Inspector: the
        ///     session state, whether the room is connected, the room and session it joined. Without
        ///     this it shows whatever was true at the last repaint, and a stale live reading is worse
        ///     than none — it cannot be told apart from a current one.
        /// </remarks>
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;

        protected override void DrawBody()
        {
            // Without the source/connection properties there is no bespoke page left to draw, so fall
            // through to the attribute-driven renderer rather than showing an empty inspector.
            if (!HasRequiredProperties())
            {
                DrawGeneratedSections();
                return;
            }

            ConvaiRoomControlProperties room = CurrentRoomProperties(out bool readOnly);

            // Everyday decisions first and open; then one collapsed card per feature, each with a
            // summary so a closed card still says what state it is in. Flat cards rather than an
            // "Advanced" wrapper: a card inside a card inside a panel read as a rendering fault.
            DrawConversationSection(room, readOnly);
            DrawConnectionSection();
            DrawValidationSection();

            if (room != null)
            {
                DrawRoomCard(View.ListeningSectionId, View.ListeningSection, Glyphs.Capture,
                    View.ListeningSummary(room), () => View.DrawListening(room, readOnly), readOnly);
                DrawRoomCard(View.InterruptionsSectionId, View.InterruptionsSection, Glyphs.Reaction,
                    View.InterruptionsSummary(room), () => View.DrawInterruptions(room, readOnly), readOnly);
                DrawRoomCard(View.VisionSectionId, View.VisionSection, Glyphs.Visibility,
                    View.VisionSummary(room), () => View.DrawVision(room, readOnly), readOnly);
                DrawRoomCard(View.ReconnectSectionId, View.ReconnectSection, Glyphs.Routing,
                    View.ReconnectSummary(room), () => View.DrawReconnect(room, readOnly), readOnly);
            }

            DrawRoomSourceSection();
            DrawRuntimeSection();
        }

        private bool HasRequiredProperties() =>
            _configurationSourceProp != null &&
            _roomConfigAssetProp != null &&
            _connectionTypeProp != null &&
            _connectOnStartProp != null;

        private void EnsureConfigurationSourceMigration()
        {
            if (_configurationSourceInitializedProp == null || _roomConfigAssetProp == null ||
                _configurationSourceProp == null)
                return;

            if (_configurationSourceInitializedProp.boolValue) return;

            _configurationSourceProp.enumValueIndex =
                _roomConfigAssetProp.objectReferenceValue != null
                    ? (int)ConvaiConfigSourceMode.Asset
                    : (int)ConvaiConfigSourceMode.Inline;
            _configurationSourceInitializedProp.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            serializedObject.Update();
        }

        /// <summary>One feature card, collapsed until opened, with the profile note when it owns the values.</summary>
        private void DrawRoomCard(
            string sectionId, GUIContent title, string glyph, string summary, Action body, bool readOnly) =>
            DrawSection(sectionId, title, glyph, () =>
            {
                if (readOnly)
                    DrawProfileNote();
                body();
            }, defaultExpanded: false, summary: summary);

        private void DrawConversationSection(ConvaiRoomControlProperties room, bool readOnly)
        {
            // No summary: the header chip already says Hands Free or Push To Talk.
            if (!DrawSection(View.ConversationSectionId, View.ConversationSection, Glyphs.Content))
                return;

            DrawSectionBody(() =>
            {
                if (room?.TurnTakingOptions == null)
                {
                    WarningBox(
                        "Turn-taking unavailable",
                        IsAssetModeSelected
                            ? "Assign a Room Manager Profile asset or switch Room Setup Source back to Inline."
                            : "This Room Manager's saved data does not contain turn-taking settings. "
                              + "Remove the Convai Room Manager component and add it again to rebuild them.");
                    return;
                }

                if (readOnly)
                    DrawProfileNote();

                View.DrawConversation(room, readOnly);
            });
        }

        /// <summary>
        ///     The two decisions a scene makes about connecting, drawn together at the top.
        /// </summary>
        /// <remarks>
        ///     Connection Type decides whether the character can see the scene at all, which makes it an
        ///     ordinary first-day setup choice rather than advanced tuning — so it sits here, in the
        ///     open, and not inside a collapsed card where a reader would have to already know it
        ///     existed. The Vision card reads it but never offers it: a setting editable in two places
        ///     is a setting a user can disagree with themselves about.
        /// </remarks>
        private void DrawConnectionSection()
        {
            if (!DrawSection(SectionConnectionId, ConnectionSection, Glyphs.Routing)) return;
            DrawSectionBody(() =>
            {
                SerializedProperty connectionTypeProp = GetConnectionTypeProperty(out bool readOnlyAssetValue);
                SerializedProperty connectOnStartProp = GetConnectOnStartProperty(out _);
                if (connectionTypeProp == null && connectOnStartProp == null)
                {
                    WarningBox(
                        "Connection settings unavailable",
                        IsAssetModeSelected
                            ? "Assign a Room Manager Profile asset or switch Room Setup Source back to Inline."
                            : "This Room Manager's saved data does not contain connection settings. "
                              + "Remove the Convai Room Manager component and add it again to rebuild them.");
                    return;
                }

                if (readOnlyAssetValue)
                    DrawProfileNote();

                using (new EditorGUI.DisabledScope(readOnlyAssetValue))
                {
                    if (connectionTypeProp != null)
                        Theme.EnumSegments(
                            ConvaiInspectorContent.ConnectionType, ConvaiInspectorContent.ConnectionTypeOptions,
                            connectionTypeProp);

                    if (connectOnStartProp != null)
                        EditorGUILayout.PropertyField(connectOnStartProp, ConvaiInspectorContent.StartsConnected);
                }

                DrawVideoRequirementsHint(connectionTypeProp);
            });
        }

        /// <summary>
        ///     Video is the only connection type with extra scene requirements, so what it needs is named
        ///     — and offered — where the choice is made, instead of only in Validation further down.
        /// </summary>
        private void DrawVideoRequirementsHint(SerializedProperty connectionTypeProp)
        {
            if (connectionTypeProp == null ||
                (ConvaiConnectionType)connectionTypeProp.enumValueIndex != ConvaiConnectionType.Video)
                return;

            (bool hasPublisher, bool hasFrameSource) = GetVisionComponentFlags();
            if (hasPublisher && hasFrameSource)
                return;

            WarningBox(
                "Video needs a camera feed",
                $"A Video connection sends camera frames to the character, which needs " +
                $"{GetMissingVideoRequirementsMessage(hasPublisher, hasFrameSource)} on this GameObject or a " +
                "child. Until then the character connects but sees nothing.",
                AddVisionComponentsButton.text,
                () => AddMissingVisionComponents(hasPublisher, hasFrameSource));
        }

        /// <summary>
        ///     Adds whichever of the two vision components is missing, on the Room Manager's own
        ///     GameObject, in one undoable step.
        /// </summary>
        /// <remarks>
        ///     <see cref="CameraVisionFrameSource" /> is the frame source to default to: it renders from a
        ///     scene camera, so it needs no device permission and no XR package, and a project that wants
        ///     the webcam or a Quest passthrough feed can swap the component afterwards.
        /// </remarks>
        private void AddMissingVisionComponents(bool hasPublisher, bool hasFrameSource)
        {
            if (_roomManager == null)
                return;

            GameObject host = _roomManager.gameObject;

            if (!hasPublisher)
                Undo.AddComponent<ConvaiVisionPublisher>(host);

            if (!hasFrameSource)
                Undo.AddComponent<CameraVisionFrameSource>(host);

            InvalidateValidationCache(true);
            EditorUtility.SetDirty(host);
        }

        /// <summary>
        ///     The scene's setup checks, coloured by what they actually found.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The header takes the colour of the worst check and carries the verdict as a summary,
        ///         so a collapsed section still reports it. A healthy scene gets no message box — the
        ///         dots below already say it.
        ///     </para>
        /// </remarks>
        private void DrawValidationSection()
        {
            SetupHealthReport report = GetSetupHealthReport();
            bool blocked = report.HasBlockingIssues;
            bool warned = report.HasWarnings || HasRoomSpecificIssues();

            Color accent = blocked ? Theme.StatusError : warned ? Theme.StatusWarn : Theme.StatusReady;
            string summary = blocked ? "Needs fixing" : warned ? "Review" : "All checks passed";

            if (!DrawSection(
                    SectionValidationId, ValidationSection, Glyphs.Validation, accent: accent, summary: summary))
                return;

            DrawSectionBody(() =>
            {
                if (blocked)
                    ErrorBox(
                        "Setup incomplete",
                        "Something this scene needs is still missing. The checks below name it.");
                else if (warned)
                    WarningBox(
                        "Almost ready",
                        "Nothing here stops the scene running, but the checks below are worth a look.");

                foreach (SetupHealthCheckResult result in report.Results)
                    ConvaiInspectorChecks.Row(result.Status, result.Title, result.Message);

                DrawRoomSpecificValidationMessages();

                GUILayout.Space(2f);
                int clicked = Theme.GhostButtonRow(RunValidationButton, OpenProjectSettingsButton);
                if (clicked == 0)
                {
                    InvalidateValidationCache(true);
                    ConvaiSetupWizard.ValidateSceneSetup();
                }
                else if (clicked == 1)
                    SettingsService.OpenProjectSettings("Project/Convai SDK");
            });
        }

        private void DrawRoomSourceSection()
        {
            if (!DrawSection(SectionConfigurationId, RoomSourceSection, Glyphs.Profile, defaultExpanded: false,
                    summary: IsAssetModeSelected ? "Profile Asset" : "Inline"))
                return;
            DrawSectionBody(() =>
            {
                Theme.EnumSegments(
                    ConvaiInspectorContent.RoomSetupSource, ConvaiInspectorContent.SetupSourceOptions,
                    _configurationSourceProp);
                if (IsAssetModeSelected)
                    EditorGUILayout.PropertyField(_roomConfigAssetProp, ConvaiInspectorContent.RoomConfigAsset);

                if (IsAssetModeSelected && _roomConfigAssetProp.objectReferenceValue == null)
                    WarningBox(
                        "No profile assigned",
                        "Assign a Room Manager Profile asset or switch Room Setup Source back to Inline.");
                else
                    GUILayout.Label(
                        IsAssetModeSelected
                            ? "Every card above reads its values from this profile. Edit the asset to change them."
                            : "Every card above is set on this component.",
                        Theme.MutedWrapped);
            });
        }

        /// <summary>
        ///     The properties every card draws — this component's own, or the assigned profile's,
        ///     read-only, so the inspector reports the room it will actually join.
        /// </summary>
        private ConvaiRoomControlProperties CurrentRoomProperties(out bool readOnly)
        {
            readOnly = false;
            if (!IsAssetModeSelected)
                return _inlineProperties;

            if (_roomConfigAssetProp.objectReferenceValue is not ConvaiRoomManagerProfile roomConfig)
                return null;

            SerializedObject asset = GetRoomConfigSerializedObject(roomConfig);
            if (asset == null)
                return null;

            readOnly = true;
            if (_profileProperties != null && ReferenceEquals(_profilePropertiesSource, asset))
                return _profileProperties;

            _profilePropertiesSource = asset;
            _profileProperties = new ConvaiRoomControlProperties
            {
                PushToTalkKey = _roomPushToTalkKeyProp,
                ConnectionTypeForDisplay = asset.FindProperty("_connectionType"),
                VideoTrackName = asset.FindProperty("_videoTrackName"),
                ServerEndpoint = asset.FindProperty("_serverEndpoint"),
                TurnTakingOptions = asset.FindProperty("_turnTakingOptions"),
                UserVadSettings = asset.FindProperty("_userVadSettings"),
                VisionContextMode = asset.FindProperty("_visionContextMode"),
                VisionInputSettings = asset.FindProperty("_visionInputSettings"),
                VisionRespondModes = asset.FindProperty("_visionRespondModes"),
                RoomRejoinTtlSeconds = asset.FindProperty("_roomRejoinTtlSeconds"),
                ResumePolicy = asset.FindProperty("_resumePolicy"),
                MaxReconnectAttempts = asset.FindProperty("_maxReconnectAttempts"),
                SpawnAgentOnRejoin = asset.FindProperty("_spawnAgentOnRejoin"),
                StartWaitTimeoutMs = asset.FindProperty("_startWaitTimeoutMs"),
                AutoMicStartDelaySeconds = asset.FindProperty("_autoMicStartDelaySeconds")
            };
            return _profileProperties;
        }

        /// <summary>One muted line naming the profile a read-only card's values come from.</summary>
        private void DrawProfileNote()
        {
            UnityEngine.Object profile = _roomConfigAssetProp.objectReferenceValue;
            if (profile == null)
                return;

            if (!ReferenceEquals(profile, _profileNoteSource))
            {
                _profileNoteSource = profile;
                _profileNote = $"From Room Manager Profile “{profile.name}”. Edit the asset to change these.";
            }

            GUILayout.Label(_profileNote, Theme.MutedWrapped);
        }

        private void DrawRuntimeSection()
        {
            if (!DrawSection(
                    SectionRuntimeId, RuntimeSection, Glyphs.Live, defaultExpanded: false, accent: Theme.StatusInfo))
                return;
            DrawSectionBody(() =>
            {
                string projectServerUrl =
                    ConvaiSettings.Instance != null ? ConvaiSettings.Instance.ServerUrl : string.Empty;
                string legacyOverride = _coreServerBaseUrlProp?.stringValue ?? string.Empty;
                bool hasLegacyOverride = !string.IsNullOrWhiteSpace(legacyOverride);
                bool connected = _roomManager.IsConnected;
                ConvaiConnectResponseSnapshot response = RefreshConnectResponseReading();

                // Readings, not fields: the same two-column readout the Convai Manager's Live section
                // uses, so the two inspectors report a running scene in one voice.
                Theme.GroupCaption(SessionGroup);
                Theme.KeyValueRow(
                    SessionStateLabel,
                    ObjectNames.NicifyVariableName(_roomManager.CurrentState.ToString()),
                    connected ? Theme.StatusReady : Theme.TextMuted);
                Theme.KeyValueRow(
                    ConnectedLabel,
                    connected ? "Yes" : "No",
                    connected ? Theme.StatusReady : Theme.TextMuted);
                Theme.KeyValueRow(CurrentRoomLabel, ValueOrNotConnected(_roomManager.CurrentRoomName));
                Theme.KeyValueRow(SessionIdLabel, ValueOrNotConnected(_roomManager.CurrentSessionId));
                Theme.KeyValueRow(
                    ConnectResponseLabel, _connectResponseReading, response != null ? null : (Color?)Theme.TextMuted);

                GUILayout.Space(6f);
                Theme.GroupCaption(InEffectGroup);
                Theme.KeyValueRow(
                    EffectiveConnectionLabel, _roomManager.EffectiveConnectionType.ToString());
                Theme.KeyValueRow(
                    VisionContextLabel,
                    _roomManager.EffectiveVisionContextEnabled
                        ? $"{_roomManager.EffectiveVisionContextMode} — sending frames"
                        : $"{_roomManager.EffectiveVisionContextMode} — off",
                    _roomManager.EffectiveVisionContextEnabled ? Theme.StatusInfo : Theme.TextMuted);
                Theme.KeyValueRow(
                    CoreServerLabel,
                    string.IsNullOrWhiteSpace(projectServerUrl) ? "Not set" : projectServerUrl);

                bool hasWiring =
                    !string.IsNullOrWhiteSpace(_roomManager.RoomControllerTypeName) ||
                    !string.IsNullOrWhiteSpace(_roomManager.TransportAccessorTypeName);
                if (hasWiring)
                {
                    GUILayout.Space(6f);
                    Theme.GroupCaption(WiringGroup);
                    if (!string.IsNullOrWhiteSpace(_roomManager.RoomControllerTypeName))
                        Theme.KeyValueRow(RoomControllerLabel, _roomManager.RoomControllerTypeName);
                    if (!string.IsNullOrWhiteSpace(_roomManager.TransportAccessorTypeName))
                        Theme.KeyValueRow(TransportLabel, _roomManager.TransportAccessorTypeName);
                }

                // Clear sits on the box that explains it. As a third button in the row below it
                // pushed Convai SDK Settings past the edge of a normal-width Inspector.
                if (hasLegacyOverride)
                {
                    GUILayout.Space(6f);
                    Theme.InfoBox(
                        "A saved server URL here is ignored",
                        $"This older scene still stores '{legacyOverride.Trim()}'. Every connection now uses the " +
                        "Core Server above, which comes from Project Settings. Clearing the saved value tidies " +
                        "the scene and changes nothing.",
                        ClearLegacyOverrideButton.text,
                        ClearLegacyCoreServerOverride);
                }

                // Copy appears once there is something to copy; the row skips an absent button
                // without leaving a gap.
                GUILayout.Space(4f);
                int clicked = Theme.GhostButtonRow(
                    response != null ? CopyConnectResponseButton : null,
                    OpenSdkSettingsButton);
                if (clicked == 0)
                    CopyConnectResponse(response);
                else if (clicked == 1)
                    SettingsService.OpenProjectSettings("Project/Convai SDK");

                Theme.HorizontalRule(Theme.Divider);
                EditorGUILayout.PropertyField(_debugProp, ConvaiInspectorContent.Debug);
            });
        }

        /// <summary>Formats the connect-response reading once per response rather than once per repaint.</summary>
        private ConvaiConnectResponseSnapshot RefreshConnectResponseReading()
        {
            ConvaiConnectResponseSnapshot snapshot = ConvaiConnectResponseLog.Latest;
            if (ReferenceEquals(snapshot, _connectResponseShown))
                return snapshot;

            _connectResponseShown = snapshot;
            _connectResponseReading = snapshot == null
                ? NotCapturedValue
                : $"Received {snapshot.ReceivedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
            return snapshot;
        }

        private static void CopyConnectResponse(ConvaiConnectResponseSnapshot response)
        {
            string json = ConvaiConnectResponseReport.Build(response);
            if (string.IsNullOrEmpty(json))
                return;

            EditorGUIUtility.systemCopyBuffer = json;
            EditorWindow.focusedWindow?.ShowNotification(
                new GUIContent("Connect response copied (token redacted)"), 1.5d);
        }

        /// <summary>A live reading, or the one phrase that explains every blank one at once.</summary>
        private static string ValueOrNotConnected(string value) =>
            string.IsNullOrWhiteSpace(value) ? NotConnectedValue : value;

        private SerializedProperty GetConnectionTypeProperty(out bool readOnlyAssetValue) =>
            GetProfileOrOwnProperty(_connectionTypeProp, "_connectionType", out readOnlyAssetValue);

        private SerializedProperty GetConnectOnStartProperty(out bool readOnlyAssetValue) =>
            GetProfileOrOwnProperty(_connectOnStartProp, "_connectOnStart", out readOnlyAssetValue);

        private SerializedProperty GetProfileOrOwnProperty(
            SerializedProperty own, string profilePath, out bool readOnlyAssetValue)
        {
            readOnlyAssetValue = false;
            if (!IsAssetModeSelected)
                return own;

            if (_roomConfigAssetProp.objectReferenceValue is not ConvaiRoomManagerProfile roomConfig)
                return null;

            SerializedObject roomConfigSerializedObject = GetRoomConfigSerializedObject(roomConfig);
            if (roomConfigSerializedObject == null)
                return null;

            readOnlyAssetValue = true;
            return roomConfigSerializedObject.FindProperty(profilePath);
        }

        private SerializedObject GetRoomConfigSerializedObject(ConvaiRoomManagerProfile roomConfig)
        {
            if (roomConfig == null)
            {
                _roomConfigSerializedObject = null;
                return null;
            }

            if (_roomConfigSerializedObject == null || _roomConfigSerializedObject.targetObject != roomConfig)
                _roomConfigSerializedObject = new SerializedObject(roomConfig);

            _roomConfigSerializedObject.UpdateIfRequiredOrScript();
            return _roomConfigSerializedObject;
        }

        private bool HasRoomSpecificIssues()
        {
            if (IsAssetModeSelected && _roomConfigAssetProp.objectReferenceValue == null)
                return true;

            if (_roomManager != null && _roomManager.EffectiveConnectionType == ConvaiConnectionType.Video)
            {
                (bool hasPublisher, bool hasFrameSource) = GetVisionComponentFlags();
                if (!hasPublisher || !hasFrameSource)
                    return true;
            }

            return false;
        }

        private void DrawRoomSpecificValidationMessages()
        {
            if (IsAssetModeSelected && _roomConfigAssetProp.objectReferenceValue == null)
            {
                // A warning, not a blocker, and the distinction is measurable:
                // ConvaiRoomManager.UsesRoomConfigAsset is `mode == Asset && _roomConfigAsset != null`,
                // so an unassigned profile does not stop the room — every Effective* value quietly
                // falls back to the inline ones. The silent fallback is the part worth saying out loud.
                ConvaiInspectorChecks.Row(
                    SetupHealthStatus.Warning,
                    "Room Manager Profile",
                    "Room Setup Source is set to Profile Asset, but no profile is assigned. " +
                    "The room still connects — it quietly uses this component's own settings instead, " +
                    "so the profile you meant to apply is not being applied.");
            }

            if (_roomManager == null || _roomManager.EffectiveConnectionType != ConvaiConnectionType.Video)
                return;

            (bool hasPublisher, bool hasFrameSource) = GetVisionComponentFlags();
            if (!hasPublisher || !hasFrameSource)
                ConvaiInspectorChecks.Row(
                    SetupHealthStatus.Warning,
                    "Dynamic Vision Requirements",
                    "Dynamic vision is missing " +
                    $"{GetMissingVideoRequirementsMessage(hasPublisher, hasFrameSource)}.");
        }

        private SetupHealthReport GetSetupHealthReport()
        {
            if (_validationTimer.ShouldRefresh(_cachedSetupHealthReport != null))
                _cachedSetupHealthReport = SetupHealthService.BuildReport();

            return _cachedSetupHealthReport;
        }

        private void InvalidateValidationCache(bool forceImmediateRefresh = false)
        {
            _cachedSetupHealthReport = null;
            _validationTimer.Invalidate(forceImmediateRefresh);
        }

        private (bool hasPublisher, bool hasFrameSource) GetVisionComponentFlags()
        {
            bool hasPublisher = false;
            bool hasFrameSource = false;

            foreach (MonoBehaviour component in _roomManager.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!hasPublisher && component is IVisionPublisher) hasPublisher = true;
                if (!hasFrameSource && component is IVisionFrameSource) hasFrameSource = true;
                if (hasPublisher && hasFrameSource) break;
            }

            return (hasPublisher, hasFrameSource);
        }

        private static string GetMissingVideoRequirementsMessage(bool hasPublisher, bool hasFrameSource)
        {
            if (!hasPublisher && !hasFrameSource)
                return "ConvaiVisionPublisher and a vision frame source";
            if (!hasPublisher)
                return "ConvaiVisionPublisher";
            return "a vision frame source";
        }

        private void ClearLegacyCoreServerOverride()
        {
            if (!HasLegacyCoreServerOverride) return;

            Undo.RecordObject(_roomManager, "Clear Legacy Convai Server URL Override");
            _coreServerBaseUrlProp.stringValue = string.Empty;
            EditorUtility.SetDirty(_roomManager);
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();
        }
    }
}
