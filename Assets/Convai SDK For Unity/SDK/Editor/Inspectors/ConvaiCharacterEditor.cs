using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Editor.ConfigurationWindow.Services;
using Convai.Editor.Diagnostics;
using Convai.Editor.Inspectors.Framework;
using Convai.Editor.Utilities;
using Convai.Infrastructure.Networking;
using Convai.RestAPI;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Styles = Convai.Editor.UI.ConvaiEditorStyles;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiCharacter" />: where the character's setup comes from
    ///     (inline values or a Character Profile asset), its identity, audio and session behaviour, a
    ///     validation section, and a Play-mode debug readout.
    /// </summary>
    [CustomEditor(typeof(ConvaiCharacter))]
    internal sealed class ConvaiCharacterEditor : ConvaiInspectorEditor
    {
        private const string TitleText = "Convai Character";

        private const string PurposeText =
            "One conversational character in this scene — its identity, audio output and session behavior.";

        private const string SectionConfigurationId = "Configuration";
        private const string SectionIdentityId = "Identity";
        private const string SectionAudioId = "Audio";
        private const string SectionSessionId = "Session";
        private const string SectionValidationId = "Validation";
        private const string SectionDebugId = "Debug";

        private static readonly Regex GuidRegex = new(
            @"^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$",
            RegexOptions.Compiled);

        internal static readonly string[] PrimarySectionTitles =
        {
            "Configuration", "Identity", "Audio", "Session", "Validation"
        };

        private static readonly GUIContent ConfigurationSection = new("Configuration");
        private static readonly GUIContent IdentitySection = new("Identity");
        private static readonly GUIContent AudioSection = new("Audio");
        private static readonly GUIContent SessionSection = new("Session");
        private static readonly GUIContent ValidationSection = new("Validation");
        private static readonly GUIContent DebugSection = new("Debug");

        private static readonly GUIContent ProfileModeChip = new("Character Profile");
        private static readonly GUIContent InlineModeChip = new("Inline");

        #region Labels

        private static readonly GUIContent SetupSourceLabel = new(
            "Character Setup Source",
            "Inline keeps this character's setup on the component. Profile Asset reads it from a reusable " +
            "Character Profile, so several scenes and prefabs can share one setup.");

        private static readonly GUIContent ProfileAssetLabel = new(
            "Character Profile Asset",
            "The asset this character's identity, voice and session resume come from.");

        private static readonly GUIContent CharacterIdLabel = new(
            "Character ID",
            "The ID from this character's page on the Convai dashboard, in the form " +
            "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.");

        private static readonly GUIContent CharacterNameLabel = new(
            "Character Name",
            "Shown in transcripts and logs. Fetch replaces it with the name set on the Convai dashboard.");

        private static readonly GUIContent NameTagColorLabel = new(
            "Name Tag Color", "Colour of this character's name in transcripts and name tags.");

        private static readonly GUIContent PlayVoiceLabel = new(
            "Play Voice On Start",
            "Plays this character's voice from the moment the session starts. Turn it off for a text-only " +
            "character; the voice can still be switched on from code.");

        private static readonly GUIContent AutoConnectLabel = new(
            "Auto Connect",
            "Starts this character's conversation as soon as the Room Manager is ready. Leave it off to " +
            "connect from your own code.");

        private static readonly GUIContent OwnerIdLabel = new(
            "Owner ID",
            "Which player this character belongs to in a multiplayer scene. Leave it empty for the local player.");

        private static readonly GUIContent ReadyTimeoutLabel = new(
            "Ready Timeout (s)",
            "How long to wait for the character to be ready after connecting before reporting a failure. " +
            "0 waits for as long as it takes.");

        private static readonly GUIContent ResumeGroup = new("Resume");

        private static readonly GUIContent ResumeLabel = new(
            "Resume Last Conversation",
            "On connect, continue the conversation named by Character Session ID instead of starting a fresh one.");

        private static readonly GUIContent SessionIdLabel = new(
            "Character Session ID",
            "The conversation to resume. Filled in after every successful connect; clear it to start fresh " +
            "next time.");

        private static readonly GUIContent OpeningContextGroup = new("Opening Context");

        private static readonly GUIContent DynamicInfoLabel = new(
            "Initial Dynamic Info",
            "Sends the text below with the connect request, so the character knows it from its first reply " +
            "and keeps it in context.");

        private static readonly GUIContent DynamicInfoTextLabel = new(
            "Text", "What the character should know as the conversation starts — where the player is, what just happened.");

        private static readonly GUIContent DiagnosticsGroup = new("Diagnostics");

        private static readonly GUIContent ConnectResponseLabel = new(
            "Connect Response",
            "The server's reply to the last room connect in this Editor session. It stays available after " +
            "Play mode stops, until the next connect.");

        #endregion

        #region Buttons

        private static readonly GUIContent CopyIdButton = new("Copy", "Copy the Character ID.");
        private static readonly GUIContent CopyIdEmptyButton = new("Copy", "There is no Character ID to copy yet.");

        private static readonly GUIContent FetchButton = new(
            "Fetch", "Replace the name with the one set on the Convai dashboard.");

        private static readonly GUIContent FetchFromProfileButton = new(
            "Fetch", "The name comes from the Character Profile asset. Edit it there.");

        private static readonly GUIContent FetchInvalidIdButton = new(
            "Fetch", "Enter a valid Character ID first.");

        private static readonly GUIContent FetchNoApiKeyButton = new(
            "Fetch", "Add your API key in Project Settings → Convai SDK first.");

        private static readonly GUIContent FetchBusyButton = new("Fetch", "Fetching the name…");

        private static readonly GUIContent OpenDashboardButton = new(
            "Open In Dashboard", "Open this character's page on the Convai dashboard.");

        private static readonly GUIContent FindIdButton = new(
            "Find ID In Dashboard", "Open the Convai dashboard to copy your character's ID.");

        private static readonly GUIContent CopySessionIdButton = new("Copy", "Copy the Character Session ID.");

        private static readonly GUIContent CopySessionIdEmptyButton = new(
            "Copy", "There is no session ID yet. One is filled in after a successful connect.");

        private static readonly GUIContent ClearSessionIdButton = new(
            "Clear", "Forget this session so the next connect starts a fresh conversation.");

        private static readonly GUIContent ClearSessionIdEmptyButton = new("Clear", "There is no session ID to clear.");

        private static readonly GUIContent CopyJsonButton = new(
            "Copy JSON",
            "Copy the whole connect response as JSON — room, session and trace IDs and anything else the " +
            "server sent — with SDK, Unity and platform details. The room token is redacted.");

        private static readonly GUIContent CopyJsonUnavailableButton = new(
            "Copy JSON", "Connect once in Play mode to capture the response.");

        private static readonly GUIContent ProjectSettingsButton = new(
            "Project Settings", "Open Project Settings → Convai SDK, where the API key lives.");

        private static readonly GUIContent QuickstartButton = new(
            "Quickstart Guide", "Open the Unity SDK quickstart in your browser.");

        #endregion

        private const string NotCapturedYet = "Not captured yet";

        private SerializedProperty _autoConnectProp;
        private ConvaiCharacter _character;
        private ConvaiCharacterVersionDropdownState _versionDropdown;

        /// <summary>Other characters in the loaded scenes holding this character's Character ID.</summary>
        private readonly List<ConvaiCharacter> _idConflicts = new();

        private bool _idConflictsDirty = true;
        private bool _sceneCharacterCountDirty = true;
        private bool _hasMultipleSceneCharacters;
        private bool _hasManager;
        private string _idConflictsScannedId;
        private SerializedProperty _characterConfigAssetProp;
        private SerializedProperty _characterIdProp;
        private SerializedProperty _characterVersionModeProp;
        private SerializedProperty _characterVersionProp;
        private SerializedProperty _characterVersionReferenceProp;
        private SerializedProperty _characterNameProp;
        private SerializedProperty _characterReadyTimeoutProp;
        private SerializedProperty _characterSessionIdProp;
        private SerializedProperty _configurationSourceInitializedProp;
        private SerializedProperty _configurationSourceProp;
        private SerializedProperty _dynamicInfoKeepInContextProp;
        private SerializedProperty _dynamicInfoTextProp;
        private SerializedProperty _enableRemoteAudioProp;
        private SerializedProperty _enableSessionResumeProp;
        private SerializedProperty _nameTagColorProp;
        private SerializedProperty _ownerIdProp;

        private bool _isFetchingName;

        /// <summary>Why the last fetch failed, for the Character ID it was run against.</summary>
        private string _fetchError;

        private string _fetchErrorForId;

        /// <summary>Whether the failure was the request itself, where the API key is the usual cause.</summary>
        private bool _fetchErrorOffersSettings;

        /// <summary>The snapshot <see cref="_connectResponseReading" /> was formatted from, so it is formatted once.</summary>
        private ConvaiConnectResponseSnapshot _connectResponseShown;

        private string _connectResponseReading = NotCapturedYet;

        protected override string Title => TitleText;
        protected override string Purpose => PurposeText;

        protected override GUIContent StatusChip => IsAssetModeSelected ? ProfileModeChip : InlineModeChip;

        protected override Color StatusChipTint =>
            IsAssetModeSelected ? Theme.StatusInfo : Theme.StatusIdle;

        private bool UsesCharacterConfigAsset =>
            (ConvaiConfigSourceMode)_configurationSourceProp.enumValueIndex == ConvaiConfigSourceMode.Asset &&
            _characterConfigAssetProp.objectReferenceValue != null;

        private bool IsAssetModeSelected =>
            _configurationSourceProp != null &&
            (ConvaiConfigSourceMode)_configurationSourceProp.enumValueIndex == ConvaiConfigSourceMode.Asset;

        protected override void OnEnable()
        {
            base.OnEnable();

            _character = (ConvaiCharacter)target;
            _versionDropdown = new ConvaiCharacterVersionDropdownState(Repaint);

            _configurationSourceProp = serializedObject.FindProperty("_configurationSource");
            _configurationSourceInitializedProp = serializedObject.FindProperty("_configurationSourceInitialized");
            _characterConfigAssetProp = serializedObject.FindProperty("_characterConfigAsset");
            _characterIdProp = serializedObject.FindProperty("_characterId");
            _characterVersionModeProp = serializedObject.FindProperty("_characterVersionMode");
            _characterVersionProp = serializedObject.FindProperty("_characterVersion");
            _characterVersionReferenceProp = serializedObject.FindProperty("_characterVersionReference");
            _characterNameProp = serializedObject.FindProperty("_characterName");
            _nameTagColorProp = serializedObject.FindProperty("_nameTagColor");
            _autoConnectProp = serializedObject.FindProperty("_autoConnect");
            _enableRemoteAudioProp = serializedObject.FindProperty("_enableRemoteAudio");
            _enableSessionResumeProp = serializedObject.FindProperty("_enableSessionResume");
            _characterSessionIdProp = serializedObject.FindProperty("_characterSessionId");
            _ownerIdProp = serializedObject.FindProperty("_ownerId");
            _characterReadyTimeoutProp = serializedObject.FindProperty("_characterReadyTimeoutSeconds");
            _dynamicInfoTextProp = serializedObject.FindProperty("_initialDynamicInfoText");
            _dynamicInfoKeepInContextProp = serializedObject.FindProperty("_initialDynamicInfoKeepInContext");

            EnsureConfigurationSourceMigration();

            EditorApplication.hierarchyChanged += MarkIdConflictsDirty;
            MarkIdConflictsDirty();
        }

        protected override void OnDisable()
        {
            _versionDropdown?.Dispose();
            _versionDropdown = null;
            EditorApplication.hierarchyChanged -= MarkIdConflictsDirty;
            base.OnDisable();
        }

        protected override void OnBeforeInspectorGUI() => EnsureConfigurationSourceMigration();

        /// <summary>
        ///     The blocking problems — a malformed Character ID, or asset mode with no asset — are
        ///     drawn above the sections so they are seen before anything is tuned.
        /// </summary>
        protected override void DrawHeaderExtras()
        {
            if (!HasRequiredProperties())
                return;

            string validationMessage = ValidateCharacterId(_character.CharacterId);
            if (!string.IsNullOrEmpty(validationMessage))
                WarningBox("Character ID", validationMessage);

            DrawCharacterIdConflict();

            if (IsAssetModeSelected && _characterConfigAssetProp.objectReferenceValue == null)
                ErrorBox(
                    "Character Profile missing",
                    "Character Setup Source is set to Profile Asset, but no Character Profile " +
                    "asset is assigned.");
        }

        /// <summary>
        ///     Says when another character in the loaded scenes is claiming this Character ID.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The likeliest way to get a second character is to duplicate the first, and the
        ///         copy arrives holding its Character ID. Nothing in the editor used to mention it.
        ///         What the author saw instead was a scene that had worked yesterday connecting to
        ///         one character twice, or one character's voice arriving from the other's mouth —
        ///         symptoms that send a reader looking at audio and routing rather than at the field
        ///         two lines above this box.
        ///     </para>
        ///     <para>
        ///         An error rather than a warning, and not only in multi-character scenes: the
        ///         registry keys ownership, participants and audio by Character ID whatever shape
        ///         the room is, so the second character never gets routing of its own.
        ///     </para>
        /// </remarks>
        private void DrawCharacterIdConflict()
        {
            RefreshIdConflicts();
            if (_idConflicts.Count == 0) return;

            ErrorBox(
                "Another character has this Character ID",
                $"{IdConflictNames()} in the loaded scenes {(_idConflicts.Count == 1 ? "uses" : "use")} this " +
                "same ID. The SDK keys ownership, participants and audio by Character ID, so two characters " +
                "sharing one collide instead of each getting their own — the room routes both to whichever " +
                "was registered first. Give this character its own ID from the Convai dashboard.");
        }

        private string IdConflictNames() =>
            string.Join(", ", _idConflicts.Where(other => other != null).Select(other => $"'{other.gameObject.name}'"));

        /// <summary>
        ///     Rescans for the conflict only when something that could change the answer has
        ///     changed.
        /// </summary>
        /// <remarks>
        ///     An inspector repaints on every mouse move across it, and this walks every loaded
        ///     scene. Two things move the answer and both are watched: the hierarchy changing, which
        ///     is where a duplicate arrives from, and this character's own ID being edited, which is
        ///     a serialized change no hierarchy event reports.
        /// </remarks>
        private void RefreshIdConflicts()
        {
            string currentId = _character != null ? _character.CharacterId : null;
            if (!_idConflictsDirty &&
                string.Equals(currentId, _idConflictsScannedId, StringComparison.Ordinal))
                return;

            _idConflictsDirty = false;
            _idConflictsScannedId = currentId;
            ConvaiCharacterIdConflicts.FindOthersSharingId(_character, _idConflicts);
            _hasManager = FindAnyObjectByType<ConvaiManager>() != null;
        }

        private void MarkIdConflictsDirty()
        {
            _idConflictsDirty = true;
            _sceneCharacterCountDirty = true;
            Repaint();
        }

        protected override void DrawBody()
        {
            // Without the identity and mode properties there is no bespoke page left to draw, so fall
            // through to the attribute-driven renderer rather than showing an empty inspector.
            if (!HasRequiredProperties())
            {
                DrawGeneratedSections();
                return;
            }

            DrawConfigurationSection();
            DrawIdentitySection();
            DrawAudioSection();
            DrawSessionSection();
            DrawValidationSection();
        }

        /// <summary>
        ///     Keeps the debug readings live; every one of them changes without the Inspector being
        ///     touched.
        /// </summary>
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;

        protected override void DrawLiveSection()
        {
            if (!DrawSection(SectionDebugId, DebugSection, Glyphs.Live, false, accent: Theme.StatusInfo)) return;
            DrawSectionBody(() =>
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Toggle("Injected", _character.IsInjected);
                    EditorGUILayout.EnumPopup("Session State", _character.SessionState);
                    EditorGUILayout.Toggle("Character Ready", _character.IsCharacterReady);
                    // Ready and available are not the same answer, and the difference is the whole
                    // reason a message can be sent to a connected room and reach nobody. In a room
                    // with a roster the room decides, not this character; Character Ready above
                    // reports only what this character believes about itself.
                    EditorGUILayout.EnumPopup("Player Can Talk", _character.ConversationAvailability);
                    EditorGUILayout.Toggle("Speaking", _character.IsSpeaking);
                    EditorGUILayout.TextField("Current Emotion", _character.CurrentEmotion ?? string.Empty);
                    EditorGUILayout.IntField("Emotion Intensity", _character.CurrentEmotionIntensity);
                }
            });
        }

        private bool HasRequiredProperties() =>
            _configurationSourceProp != null &&
            _characterConfigAssetProp != null &&
            _characterIdProp != null &&
            _characterNameProp != null &&
            _enableRemoteAudioProp != null &&
            _enableSessionResumeProp != null &&
            _characterSessionIdProp != null;

        private void EnsureConfigurationSourceMigration()
        {
            if (_configurationSourceProp == null || _configurationSourceInitializedProp == null ||
                _characterConfigAssetProp == null)
                return;

            if (_configurationSourceInitializedProp.boolValue) return;

            _configurationSourceProp.enumValueIndex =
                _characterConfigAssetProp.objectReferenceValue != null
                    ? (int)ConvaiConfigSourceMode.Asset
                    : (int)ConvaiConfigSourceMode.Inline;
            _configurationSourceInitializedProp.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            serializedObject.Update();
        }

        /// <remarks>
        ///     In asset mode this used to repeat the profile's ID, name, colour, audio and resume as
        ///     read-only rows — rows the Identity, Audio and Session sections below already show,
        ///     read-only, in the same state. One line saying where they come from replaces the copy.
        /// </remarks>
        private void DrawConfigurationSection()
        {
            if (!DrawSection(SectionConfigurationId, ConfigurationSection, Glyphs.Profile)) return;
            DrawSectionBody(() =>
            {
                Theme.EnumSegments(SetupSourceLabel, ConvaiInspectorContent.SetupSourceOptions, _configurationSourceProp);

                if (!IsAssetModeSelected)
                {
                    GUILayout.Label(
                        "Set up on this component. Choose Profile Asset to share one setup across scenes and " +
                        "prefabs.",
                        Theme.MutedWrapped);
                    return;
                }

                EditorGUILayout.PropertyField(_characterConfigAssetProp, ProfileAssetLabel);

                if (_characterConfigAssetProp.objectReferenceValue == null)
                    WarningBox(
                        "No profile assigned",
                        "Assign a Character Profile asset, or switch Character Setup Source back to Inline.");
                else
                    GUILayout.Label(
                        "Identity, voice and session resume below come from this profile and are read-only " +
                        "here. Edit the asset to change them.",
                        Theme.MutedWrapped);
            });
        }

        private void DrawIdentitySection()
        {
            if (!DrawSection(SectionIdentityId, IdentitySection, Glyphs.Identity)) return;
            DrawSectionBody(() =>
            {
                bool fromProfile = IsAssetModeSelected;
                string characterId = _character.CharacterId ?? string.Empty;
                bool idIsValid = string.IsNullOrEmpty(ValidateCharacterId(characterId));

                Rect idField = Theme.FieldWithActions(
                    characterId.Length > 0 ? CopyIdButton : CopyIdEmptyButton, characterId.Length > 0,
                    null, false, out int idClicked);
                if (fromProfile)
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUI.TextField(idField, CharacterIdLabel, characterId);
                else
                    EditorGUI.PropertyField(idField, _characterIdProp, CharacterIdLabel);

                if (idClicked == 0)
                    CopyToClipboard(characterId, "Character ID copied");

                GUIContent fetch = FetchButtonFor(fromProfile, idIsValid, out bool canFetch);
                Rect nameField = Theme.FieldWithActions(fetch, canFetch, null, false, out int nameClicked);
                if (fromProfile)
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUI.TextField(nameField, CharacterNameLabel, _character.CharacterName);
                else
                    EditorGUI.PropertyField(nameField, _characterNameProp, CharacterNameLabel);

                if (nameClicked == 0)
                    FetchCharacterNameFromApi();

                DrawFetchStatus(characterId);

                if (fromProfile)
                    using (new EditorGUI.DisabledScope(true))
                    {
                        _ = _versionDropdown.SetContextAsync(null, null);
                        EditorGUILayout.ColorField(NameTagColorLabel, _character.NameTagColor);
                        ConvaiCharacterVersionEditorFields.DrawReadOnly(
                            _character.CharacterVersionMode, _character.CharacterVersion);
                    }
                else
                {
                    EditorGUILayout.PropertyField(_nameTagColorProp, NameTagColorLabel);
                    ConvaiCharacterVersionEditorFields.Draw(_characterIdProp, _characterVersionModeProp,
                        _characterVersionProp, _characterVersionReferenceProp, _versionDropdown);
                }

                DrawVersionSelectionFeedback();

                GUILayout.Space(4f);
                if (Theme.GhostButtonRow(idIsValid ? OpenDashboardButton : FindIdButton) == 0)
                    UnityEngine.Application.OpenURL(idIsValid
                        ? $"{ConvaiEditorLinks.CharacterDashboardBaseUrl}?id={characterId}"
                        : ConvaiEditorLinks.DashboardHomeUrl);
            });
        }

        /// <summary>The Fetch button in its current state; its tooltip carries the reason it is disabled.</summary>
        private GUIContent FetchButtonFor(bool fromProfile, bool idIsValid, out bool enabled)
        {
            enabled = false;
            if (fromProfile) return FetchFromProfileButton;
            if (!idIsValid) return FetchInvalidIdButton;
            if (ConvaiSettings.Instance == null || !ConvaiSettings.Instance.HasApiKey) return FetchNoApiKeyButton;
            if (_isFetchingName) return FetchBusyButton;

            enabled = true;
            return FetchButton;
        }

        /// <summary>
        ///     What the last fetch is doing or did, under the field it writes to. A failure used to go
        ///     to the Console only, so from the Inspector a failed fetch and a fetch that returned the
        ///     same name looked identical: nothing happened.
        /// </summary>
        private void DrawFetchStatus(string characterId)
        {
            if (_isFetchingName)
            {
                GUILayout.Label("Fetching the name from Convai…", Theme.CaptionWrapped);
                return;
            }

            if (string.IsNullOrEmpty(_fetchError) ||
                !string.Equals(_fetchErrorForId, characterId, StringComparison.Ordinal))
                return;

            if (_fetchErrorOffersSettings)
                WarningBox(
                    "Couldn't fetch the name",
                    _fetchError,
                    ProjectSettingsButton.text,
                    () => SettingsService.OpenProjectSettings("Project/Convai SDK"));
            else
                WarningBox("Couldn't fetch the name", _fetchError);
        }

        private void DrawAudioSection()
        {
            if (!DrawSection(SectionAudioId, AudioSection, Glyphs.Content)) return;
            DrawSectionBody(() =>
            {
                if (IsAssetModeSelected)
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.Toggle(PlayVoiceLabel, _character.EnableRemoteAudioOnStart);
                else
                    EditorGUILayout.PropertyField(_enableRemoteAudioProp, PlayVoiceLabel);

                if (!_character.EnableRemoteAudioOnStart || _character.GetComponent<ConvaiAudioOutput>() != null)
                    return;

                WarningBox(
                    "Nothing will play the voice",
                    "Play Voice On Start is on, but this GameObject has no Convai Audio Output to play it through.",
                    "Add Convai Audio Output",
                    () =>
                    {
                        Undo.AddComponent<ConvaiAudioOutput>(_character.gameObject);
                        EditorUtility.SetDirty(_character);
                    });
            });
        }

        private void DrawVersionSelectionFeedback()
        {
            if (!ConvaiCharacterVersionReference.TryCreate(_character.CharacterId,
                    _character.CharacterVersionMode, _character.CharacterVersion,
                    out _, out string error, _character.SelectedCharacterReference))
            {
                WarningBox("Character version", error);
                return;
            }

            if (_character.CharacterVersionMode == ConvaiCharacterVersionMode.Specific &&
                ConvaiSettings.Instance?.ApiMode != ConvaiApiMode.Upgraded)
                InfoBox("Versioned REST reads",
                    "Set API Mode to Upgraded in Project Settings > Convai SDK > Credentials to fetch " +
                    "the selected character name and narrative data. Legacy reads cannot select a version.");

            if (_sceneCharacterCountDirty)
            {
                _hasMultipleSceneCharacters =
                    ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(_character);
                _sceneCharacterCountDirty = false;
            }

            if (_hasMultipleSceneCharacters && _character.CharacterVersionMode == ConvaiCharacterVersionMode.Specific)
                WarningBox("Versioned connections",
                    "This scene contains multiple Convai Characters. Multi-character rosters do not yet " +
                    "support released version selection. Choose Live for multi-character rooms.");
        }

        /// <summary>
        ///     How and when this character connects, the conversation it resumes, what it is told as
        ///     it starts, and the server's last answer — in that order, each group named.
        /// </summary>
        private void DrawSessionSection()
        {
            if (!DrawSection(SectionSessionId, SessionSection, Glyphs.Routing)) return;
            DrawSectionBody(() =>
            {
                EditorGUILayout.PropertyField(_autoConnectProp, AutoConnectLabel);
                EditorGUILayout.PropertyField(_ownerIdProp, OwnerIdLabel);
                EditorGUILayout.PropertyField(_characterReadyTimeoutProp, ReadyTimeoutLabel);

                Theme.GroupCaption(ResumeGroup);
                if (IsAssetModeSelected)
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.Toggle(ResumeLabel, _character.EnableSessionResume);
                else
                    EditorGUILayout.PropertyField(_enableSessionResumeProp, ResumeLabel);

                DrawSessionIdRow();

                Theme.GroupCaption(OpeningContextGroup);
                EditorGUILayout.PropertyField(_dynamicInfoKeepInContextProp, DynamicInfoLabel);
                if (_dynamicInfoKeepInContextProp != null && _dynamicInfoKeepInContextProp.boolValue)
                    using (new EditorGUI.IndentLevelScope())
                        EditorGUILayout.PropertyField(_dynamicInfoTextProp, DynamicInfoTextLabel);

                Theme.GroupCaption(DiagnosticsGroup);
                DrawConnectResponseRow();
            });
        }

        private void DrawSessionIdRow()
        {
            bool hasSessionId = !string.IsNullOrEmpty(_characterSessionIdProp.stringValue);
            Rect field = Theme.FieldWithActions(
                hasSessionId ? CopySessionIdButton : CopySessionIdEmptyButton, hasSessionId,
                hasSessionId ? ClearSessionIdButton : ClearSessionIdEmptyButton, hasSessionId,
                out int clicked);
            EditorGUI.PropertyField(field, _characterSessionIdProp, SessionIdLabel);

            if (clicked == 0)
                CopyToClipboard(_characterSessionIdProp.stringValue, "Character Session ID copied");
            else if (clicked == 1)
            {
                _characterSessionIdProp.stringValue = string.Empty;
                GUI.FocusControl(null);
            }
        }

        /// <summary>
        ///     When the last connect answered and which room it put the scene in, with the whole answer
        ///     one click away for a backend bug report.
        /// </summary>
        private void DrawConnectResponseRow()
        {
            ConvaiConnectResponseSnapshot snapshot = ConvaiConnectResponseLog.Latest;
            if (!ReferenceEquals(snapshot, _connectResponseShown))
            {
                _connectResponseShown = snapshot;
                _connectResponseReading = snapshot == null
                    ? NotCapturedYet
                    : $"{snapshot.ReceivedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {snapshot.Details.RoomName}";
            }

            bool available = snapshot != null;
            Rect row = Theme.FieldWithActions(
                available ? CopyJsonButton : CopyJsonUnavailableButton, available, null, false, out int clicked);
            Rect value = EditorGUI.PrefixLabel(row, ConnectResponseLabel);
            GUI.Label(value, _connectResponseReading,
                available ? Styles.ReadingValue : Styles.ReadingValueTinted(Theme.TextMuted));

            if (clicked == 0)
                CopyToClipboard(
                    ConvaiConnectResponseReport.Build(
                        snapshot, _character.CharacterId, _character.CharacterName, _character.CharacterReference),
                    "Connect response copied (token redacted)");
        }

        /// <summary>
        ///     The checks, coloured by what they found, in the shape the Room Manager's Validation
        ///     section uses. A healthy character gets no message box — the dots already say it.
        /// </summary>
        private void DrawValidationSection()
        {
            RefreshIdConflicts();
            string idProblem = ValidateCharacterId(_character.CharacterId ?? string.Empty);
            bool profileMissing = IsAssetModeSelected && _characterConfigAssetProp.objectReferenceValue == null;
            bool blocked = !string.IsNullOrEmpty(idProblem) || _idConflicts.Count > 0 || profileMissing;
            bool warned = !_hasManager;

            Color accent = blocked ? Theme.StatusError : warned ? Theme.StatusWarn : Theme.StatusReady;
            string summary = blocked ? "Needs fixing" : warned ? "Review" : "All checks passed";

            if (!DrawSection(SectionValidationId, ValidationSection, Glyphs.Validation, accent: accent, summary: summary))
                return;

            DrawSectionBody(() =>
            {
                ConvaiInspectorChecks.Row(
                    string.IsNullOrEmpty(idProblem) ? SetupHealthStatus.Healthy : SetupHealthStatus.Blocked,
                    "Character ID", idProblem);

                ConvaiInspectorChecks.Row(
                    _idConflicts.Count == 0 ? SetupHealthStatus.Healthy : SetupHealthStatus.Blocked,
                    "Unique in the loaded scenes",
                    _idConflicts.Count == 0 ? null : $"Shared with {IdConflictNames()}.");

                if (IsAssetModeSelected)
                    ConvaiInspectorChecks.Row(
                        profileMissing ? SetupHealthStatus.Blocked : SetupHealthStatus.Healthy,
                        "Character Profile assigned",
                        profileMissing ? "Assign one in Configuration, or switch back to Inline." : null);

                ConvaiInspectorChecks.Row(
                    _hasManager ? SetupHealthStatus.Healthy : SetupHealthStatus.Warning,
                    "Convai Manager in the scene",
                    _hasManager ? null : "Add a Convai Manager so this character can connect.");

                GUILayout.Space(2f);
                int clicked = Theme.GhostButtonRow(ProjectSettingsButton, QuickstartButton);
                if (clicked == 0)
                    SettingsService.OpenProjectSettings("Project/Convai SDK");
                else if (clicked == 1)
                    UnityEngine.Application.OpenURL(ConvaiEditorLinks.DocsUnityQuickstartUrl);
            });
        }

        private static string ValidateCharacterId(string characterId)
        {
            if (string.IsNullOrEmpty(characterId))
                return "Character ID is required. Copy it from your character's page on the Convai dashboard.";

            if (characterId != characterId.Trim())
                return "Character ID has a space before or after it. Delete the extra spaces.";

            if (characterId.Length != 36)
                return $"Character ID should be 36 characters, and this one is {characterId.Length}. "
                       + "Copy the whole ID from the Convai dashboard.";

            if (!GuidRegex.IsMatch(characterId))
                return "Character ID is not in the expected form "
                       + "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx. Copy it again from the Convai "
                       + "dashboard — it is easy to pick up a character's name by mistake.";

            return string.Empty;
        }

        /// <summary>Copies <paramref name="text" /> and says so where the click happened.</summary>
        private static void CopyToClipboard(string text, string confirmation)
        {
            if (string.IsNullOrEmpty(text)) return;

            EditorGUIUtility.systemCopyBuffer = text;
            Notify(EditorWindow.focusedWindow, confirmation);
        }

        private static void Notify(EditorWindow window, string message)
        {
            if (window != null)
                window.ShowNotification(new GUIContent(message), 1.5d);
        }

        private void FetchCharacterNameFromApi() => _ = FetchCharacterNameFromApiAsync(EditorWindow.focusedWindow);

        private async Task FetchCharacterNameFromApiAsync(EditorWindow notifyWindow)
        {
            if (_isFetchingName || UsesCharacterConfigAsset) return;

            string characterId = _character?.CharacterId;
            var settings = ConvaiSettings.Instance;
            if (!string.IsNullOrEmpty(ValidateCharacterId(characterId)) || settings == null || !settings.HasApiKey)
                return;

            _isFetchingName = true;
            _fetchError = null;
            Repaint();

            string characterReference = _character.CharacterReference;
            string fetchedName = null;
            string error = null;
            bool requestFailed = false;
            try
            {
                var options = ConvaiRestOptionsFactory.CreateForCharacterRead(characterReference, settings.ApiKey,
                    preserveSelectedReference: true);
                using var client = new ConvaiRestClient(options);
                CharacterDetails details = await client.Characters.GetDetailsAsync(characterReference);
                fetchedName = details.CharacterName?.Trim();
                if (string.IsNullOrEmpty(fetchedName))
                    error = "Convai returned no name for this character.";
            }
            catch (Exception ex)
            {
                error = $"{ex.Message} Check the API key in Project Settings → Convai SDK and the Character ID.";
                requestFailed = true;
                ConvaiLogger.Error($"Failed to fetch character name: {ex.Message}", LogCategory.Editor);
            }

            EditorApplication.delayCall += () =>
            {
                _isFetchingName = false;
                if (this == null || _character == null || _characterNameProp == null)
                    return;

                if (error != null)
                {
                    _fetchError = error;
                    _fetchErrorForId = characterId;
                    _fetchErrorOffersSettings = requestFailed;
                    Repaint();
                    return;
                }

                if (IsAssetModeSelected ||
                    !ConvaiCharacterVersionReference.TryCreate(_character.CharacterId,
                        _character.CharacterVersionMode, _character.CharacterVersion,
                        out string currentReference, out _, _character.SelectedCharacterReference) ||
                    currentReference != characterReference)
                {
                    Repaint();
                    return;
                }

                serializedObject.UpdateIfRequiredOrScript();
                if (_characterNameProp.stringValue == fetchedName)
                {
                    Notify(notifyWindow, "Name already matches the dashboard");
                    Repaint();
                    return;
                }

                Undo.RecordObject(_character, "Fetch Convai Character Name");
                _characterNameProp.stringValue = fetchedName;
                serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(_character);
                Notify(notifyWindow, $"Name set to “{fetchedName}”");
                Repaint();
            };
        }
    }
}
