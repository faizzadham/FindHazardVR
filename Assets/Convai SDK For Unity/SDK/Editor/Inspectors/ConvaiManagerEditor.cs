using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.DomainEvents.Session;
using Convai.Editor.Inspectors.Framework;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Conversation;
using Convai.Runtime.Core.Composition;
using Convai.Runtime.Room;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Chips = Convai.Editor.UI.ConvaiEditorChips;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Object = UnityEngine.Object;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai inspector for <see cref="ConvaiManager" /> — the component that starts the SDK for a
    ///     scene and owns the room, the player and the characters while it runs.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every serialized field on this component is <c>[HideInInspector]</c>, and deliberately
    ///         so: the manager is meant to work with no configuration, and the values it does hold are
    ///         either project-wide (they belong in Convai Settings) or owned by another component
    ///         (they belong on the Room Manager). That left the default inspector showing nothing but
    ///         the script slot, which reads as "this component is broken" rather than "this component
    ///         needs nothing from you".
    ///     </para>
    ///     <para>
    ///         So this inspector answers the two questions someone selecting the manager actually has:
    ///         is my scene wired up, and — once playing — what is it doing. Most of what it shows is a
    ///         signpost to where the setting really lives.
    ///     </para>
    ///     <para>
    ///         It presents fields in exactly two places, and both appear only once they can change
    ///         something: the room selection when the scene holds more than one character, and the
    ///         conversation-targeting rule when a room can hold more than one. A setting shown before
    ///         it can do anything is noise in the one place a beginner is looking for the setting that
    ///         can.
    ///     </para>
    /// </remarks>
    [CustomEditor(typeof(ConvaiManager))]
    internal sealed class ConvaiManagerEditor : ConvaiInspectorEditor
    {
        private const string SetupSectionId = "SceneSetup";
        private const string TargetingSectionId = "ConversationTargeting";
        private const string LiveSectionId = "Live";

        private static readonly GUIContent _targetingModeLabel = new(
            "Chosen By",
            "How the SDK decides which character the player is talking to.");

        /// <summary>One segment per <see cref="ConversationTargetingMode" /> value, in declaration order.</summary>
        private static readonly GUIContent[] TargetingModeOptions =
        {
            new("Look At", "The character nearest the centre of the player's view."),
            new("Proximity", "The nearest character, wherever the player is looking."),
            new("Manual", "Only your code moves the conversation, through ConvaiManager.TalkTo(character).")
        };

        private static readonly GUIContent _targetingMaxDistanceLabel = new(
            "Range (Metres)",
            "How far away a character can be and still be addressed. This is a guard against " +
            "addressing somebody in the next room, not the rule that picks between characters, so " +
            "it is generous on purpose.");

        private static readonly GUIContent _targetingMaxAngleLabel = new(
            "Look Angle (Degrees)",
            "How far from the centre of view a character can be and still be addressed. It is " +
            "measured from the centre outwards, so the value is half the width of the cone.");

        private static readonly GUIContent _targetingSwitchMarginLabel = new(
            "Switch Margin (Degrees)",
            "How much closer to the centre of view a different character must be before the " +
            "conversation moves to them. Raise it when two characters stand close together and the " +
            "conversation flickers between them.");

        private static readonly GUIContent _targetingSwitchDelayLabel = new(
            "Switch Delay (Seconds)",
            "How long a different character must stay the best choice before the conversation moves " +
            "to them. Stops a sweep of the view addressing everyone it passes.");

        private static readonly GUIContent _targetingViewCameraLabel = new(
            "Player Camera",
            "The camera treated as the player's view. Leave it empty to use the main camera.");

        private static readonly GUIContent RoomManagerLabel = new(
            "Room Manager", "The component that connects this scene to the Convai service.");

        private static readonly GUIContent SceneInstallerLabel = new(
            "Scene Installer", "Optional. Runs extra setup for this scene after the SDK starts.");

        private static readonly GUIContent CharactersLabel = new(
            "Characters in Scene", "Every Convai Character found in the open scenes.");

        private static readonly GUIContent RoomCharactersLabel = new(
            "Joining the Room",
            "How many of those characters will be in the room the next time it connects.");

        private static readonly GUIContent RoomMembersLabel = new(
            "Characters in Room", "The characters this connection actually opened the room for.");

        private static readonly GUIContent InitialCharacterLabel = new(
            "Initial Character",
            "Optional. The character that takes the first turn when the room connects. Leave it " +
            "empty and the first character in the scene starts. It chooses where the conversation " +
            "begins, not where it stays — the rule below moves it from there.");

        private static readonly GUIContent PlayerLabel = new(
            "Player", "The Convai Player this manager is speaking on behalf of.");

        private static readonly GUIContent ConnectedLabel = new(
            "Connected", "Whether the room connection is open right now.");

        private static readonly GUIContent SpeakingToLabel = new(
            "Talking To",
            "The character the player is addressing right now. In a room with one character this " +
            "is that character; in a room with several it is whoever targeting has settled on.");

        private static readonly GUIContent _availabilityLabel = new(
            "Player Can Talk",
            "Whether a message sent right now would reach the character being addressed. A " +
            "connected room is not the same answer: a character has to be announced by the " +
            "service first, and Preparing is that gap.");

        private static readonly GUIContent _targetingVerdictLabel = new(
            "Last Decision",
            "Why the conversation is where it is. A target that is holding correctly and one that " +
            "is stuck look identical from outside; this is what separates them.");

        private static readonly GUIContent _roomShapeLabel = new(
            "Room Holds",
            "A room opened for a single character keeps no roster, so nobody can join it while it " +
            "is connected. A room opened for several can be added to and removed from during play.");

        private static readonly GUIContent _rosterGroupLabel = new("Room Roster");

        private static readonly GUIContent CharacterSelectionCaption = new("Characters Joining the Room");

        private static readonly GUIContent IncludeEveryoneButton = new(
            "Include Everyone", "Send every character this manager owns, including any added later.");

        private static readonly GUIContent IncludeNobodyButton = new(
            "Include Nobody", "Clear the selection so you can tick only the characters you want.");

        /// <summary>Height of one character row in the room selection list.</summary>
        private const float CharacterRowHeight = 22f;

        /// <summary>Space between a character row's edge and its content.</summary>
        /// <remarks>
        ///     Deliberately the design system's own content inset, so a name in this list opens on
        ///     the same pixel as the labels above and below the panel it sits in.
        /// </remarks>
        private static float CharacterRowInset => Theme.ContentInset;

        private ConvaiManager Manager => (ConvaiManager)target;

        protected override string Title => "Convai Manager";

        protected override string Subtitle => "Scene runtime host";

        protected override string Purpose =>
            "Starts Convai for this scene and owns the room, the player and the characters while it " +
            "runs. Characters in an ordinary scene are discovered automatically; connection settings " +
            "live on the Convai Room Manager.";

        protected override string EditorStateHostId => "ConvaiManagerEditor";

        /// <summary>
        ///     Outside Play mode this reports whether the scene has what the manager needs; in Play
        ///     mode it reports whether the manager actually started.
        /// </summary>
        protected override GUIContent StatusChip =>
            EditorApplication.isPlaying
                ? Manager.IsInitialized ? Chips.Running(Manager.IsConnected).Content : Chips.Inactive.Content
                : IsSceneReady ? Chips.Ready.Content : Chips.NotSetUp.Content;

        protected override Color StatusChipTint =>
            EditorApplication.isPlaying
                ? Manager.IsInitialized ? Chips.Running(Manager.IsConnected).Tint : Chips.Inactive.Tint
                : IsSceneReady ? Chips.Ready.Tint : Chips.NotSetUp.Tint;

        /// <summary>
        ///     Keeps the Live section live.
        /// </summary>
        /// <remarks>
        ///     Everything the Live section reports moves without anyone touching the Inspector: a
        ///     roster edit is a round trip, availability changes when the service announces a
        ///     character, and targeting re-evaluates fifteen times a second. Without this the
        ///     section shows whatever was true at the last repaint, which is worse than showing
        ///     nothing — it was measured naming a character that had already left the room as the
        ///     one being addressed, while the game itself had correctly moved on.
        /// </remarks>
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;

        /// <summary>Whether this manager's scene contains a Room Manager.</summary>
        /// <remarks>
        ///     Scanned on demand rather than per repaint: this runs only when the inspector rebuilds
        ///     its chip, and a scene-wide search on every repaint of a docked inspector is the kind of
        ///     cost that shows up as editor stutter with no visible cause.
        /// </remarks>
        private bool HasRoomManager => _cachedRoomManager != null;
        private ConvaiPlayer ExplicitPlayer => _explicitPlayerProp?.objectReferenceValue as ConvaiPlayer;
        private bool HasResolvedPlayer => CanResolvePlayer(_cachedPlayers.Count, ExplicitPlayer);
        private int ActiveCharacterCount => _cachedCharacters.Count(character => character.isActiveAndEnabled);
        private int SelectedCharacterCount => _cachedCharacters.Count(IsCharacterSelected);
        private int SelectedActiveCharacterCount =>
            _cachedCharacters.Count(character => character.isActiveAndEnabled && IsCharacterSelected(character));
        private bool HasCharacters => SelectedActiveCharacterCount > 0;
        /// <summary>
        ///     Whether the room has somebody to start on.
        /// </summary>
        /// <remarks>
        ///     An assigned character that is not active yet does not make the scene broken: it is
        ///     enabled during play, and until then the room starts on the default. The warning beside
        ///     the field says so, which is more use than a Ready badge going red over a working scene.
        /// </remarks>
        private bool HasValidInitialCharacter =>
            _initialCharacterProp?.objectReferenceValue == null
                ? SelectedActiveCharacterCount >= 1
                : _initialCharacterProp.objectReferenceValue is ConvaiCharacter targetCharacter &&
                  _cachedCharacters.Contains(targetCharacter) &&
                  IsCharacterSelected(targetCharacter);
        private bool IsSceneReady =>
            HasRoomManager && HasResolvedPlayer && HasCharacters && HasValidInitialCharacter;

        private ConvaiRoomManager _cachedRoomManager;
        private ConvaiSceneInstaller _cachedSceneInstaller;
        private ConvaiSceneInstaller _discoveredSceneInstaller;
        private readonly List<ConvaiPlayer> _cachedPlayers = new();
        private readonly List<ConvaiCharacter> _discoveredCharacters = new();
        private readonly List<ConvaiCharacter> _cachedCharacters = new();
        private readonly List<ConvaiCharacter> _includedCharacters = new();

        /// <summary>
        ///     Selected characters this scene leaves inactive, rebuilt each time the warning that
        ///     names them is drawn.
        /// </summary>
        /// <remarks>
        ///     Held as a field rather than allocated per repaint: an inspector redraws on every
        ///     mouse move over it, and this list exists so the warning's button knows what it
        ///     offered to enable.
        /// </remarks>
        private readonly List<ConvaiCharacter> _inactiveSelectedCharacters = new();
        private SerializedProperty _sceneInstallerProp;
        private SerializedProperty _explicitPlayerProp;
        private SerializedProperty _explicitCharactersProp;
        private SerializedProperty _useCharacterConnectionSelectionProp;
        private SerializedProperty _includedCharactersProp;
        private SerializedProperty _targetingModeProp;
        private SerializedProperty _targetingMaxDistanceProp;
        private SerializedProperty _targetingMaxAngleProp;
        private SerializedProperty _targetingSwitchMarginProp;
        private SerializedProperty _targetingSwitchDelayProp;
        private SerializedProperty _targetingViewCameraProp;
        private SerializedProperty _initialCharacterProp;

        protected override void OnEnable()
        {
            base.OnEnable();
            _sceneInstallerProp = serializedObject.FindProperty("_sceneInstaller");
            _explicitPlayerProp = serializedObject.FindProperty("_explicitPlayer");
            _explicitCharactersProp = serializedObject.FindProperty("_explicitCharacters");
            _useCharacterConnectionSelectionProp =
                serializedObject.FindProperty("_useCharacterConnectionSelection");
            _includedCharactersProp = serializedObject.FindProperty("_includedCharacters");
            _targetingModeProp = serializedObject.FindProperty("_conversationTargeting._mode");
            _targetingMaxDistanceProp = serializedObject.FindProperty("_conversationTargeting._maxDistance");
            _targetingMaxAngleProp = serializedObject.FindProperty("_conversationTargeting._maxAngle");
            _targetingSwitchMarginProp = serializedObject.FindProperty("_conversationTargeting._switchMargin");
            _targetingSwitchDelayProp =
                serializedObject.FindProperty("_conversationTargeting._switchDelaySeconds");
            _targetingViewCameraProp = serializedObject.FindProperty("_conversationViewCamera");
            _initialCharacterProp = serializedObject.FindProperty("_explicitConversationTarget");
            RefreshSceneReferences();
        }

        /// <summary>Refreshes the cached scene lookups once per pass rather than per repaint.</summary>
        protected override void OnBeforeInspectorGUI()
        {
            // Cheap enough once per inspector pass, and it keeps the chip honest when the user adds a
            // Room Manager while this inspector is open.
            if (Event.current.type == EventType.Layout)
                RefreshSceneReferences();
        }

        private void RefreshSceneReferences()
        {
            Scene scene = Manager.gameObject.scene;
            _cachedRoomManager = FindSceneComponent<ConvaiRoomManager>(scene);
            _cachedSceneInstaller = _sceneInstallerProp?.objectReferenceValue as ConvaiSceneInstaller;
            _discoveredSceneInstaller = FindSceneComponent<ConvaiSceneInstaller>(scene);
            RefreshLoadedSceneComponents(_cachedPlayers);
            RefreshLoadedSceneComponents(_discoveredCharacters);

            IReadOnlyList<ConvaiCharacter> ownedCharacters = ConvaiRuntimeHost.ResolveOwnedCharacters(
                _cachedSceneInstaller,
                ReadCharacterReferences(_explicitCharactersProp),
                _initialCharacterProp?.objectReferenceValue as ConvaiCharacter,
                _discoveredCharacters);
            _cachedCharacters.Clear();
            for (int i = 0; i < ownedCharacters.Count; i++)
                if (ownedCharacters[i] != null)
                    _cachedCharacters.Add(ownedCharacters[i]);

            _includedCharacters.Clear();
            _includedCharacters.AddRange(ReadCharacterReferences(_includedCharactersProp));
        }

        protected override void DrawBody()
        {
            if (EditorApplication.isPlaying)
                return;

            if (!HasRoomManager)
            {
                WarningBox(
                    "No Room Manager in this scene",
                    "The manager starts the SDK, but a Convai Room Manager is what actually connects " +
                    "to the service. Without one, characters in this scene will never speak.");
            }

            if (!HasResolvedPlayer && _cachedPlayers.Count == 0)
                WarningBox(
                    "No player in the loaded scenes",
                    "Add one Convai Player so the room knows who is speaking.");
            else if (!HasResolvedPlayer)
                WarningBox(
                    "Multiple players need a choice",
                    "The loaded scenes have more than one Convai Player. Bind the intended local player before connecting.");

            if (_cachedSceneInstaller == null && _discoveredSceneInstaller != null)
                WarningBox(
                    "Scene Installer is not assigned",
                    "A Convai Scene Installer exists here, but it does not control character ownership until it is assigned to this manager.");

            if (_cachedCharacters.Count == 0)
                WarningBox(
                    "No characters in the loaded scenes",
                    "Add and enable a Convai Character component. Ordinary scenes discover it automatically.");
            else if (SelectedCharacterCount == 0)
                ErrorBox(
                    "No characters selected for the room",
                    "Select at least one owned character to include in the next room connection.");
            else if (SelectedActiveCharacterCount == 0)
                WarningBox(
                    "No selected characters are active",
                    "Enable at least one selected Convai Character component and its GameObject before connecting.");
            else if (_initialCharacterProp?.objectReferenceValue is ConvaiCharacter initialCharacter &&
                     (!_cachedCharacters.Contains(initialCharacter) || !IsCharacterSelected(initialCharacter)))
                ErrorBox(
                    "Initial character is unavailable",
                    "Choose a character that is included in this manager's room selection.");
            // Assigning a character that starts the scene disabled is legitimate — it is enabled
            // during play — but it cannot open a room it is not in yet, so say what will actually
            // happen rather than calling the scene broken.
            else if (_initialCharacterProp?.objectReferenceValue is ConvaiCharacter inactiveInitial &&
                     !inactiveInitial.isActiveAndEnabled)
                WarningBox(
                    $"'{inactiveInitial.name}' is not active yet",
                    "It is the character the player addresses first only if it is enabled before " +
                    "the room connects. Until then the room opens on " +
                    DefaultInitialCharacterName() + " instead.");
            else if (SelectedActiveCharacterCount > 1 && _initialCharacterProp?.objectReferenceValue == null)
                // Leaving the choice open is a valid scene, not a fault: the room opens on the
                // first character in scene order. Naming that character is what the reader needs,
                // but naming it *first* was measured to mislead — see DescribeAddressingRule.
                InfoBox(
                    $"{SelectedActiveCharacterCount} characters will join this room",
                    DescribeAddressingRule(DefaultInitialCharacterName()));

            DrawSingleCharacterRoomWarning();

            DrawSection(SetupSectionId, "Scene Setup", Glyphs.Routing, () =>
            {
                // What the scene is, then what you set. The two used to run together as one
                // ladder of rows, so a field sat among readings looking like another reading that
                // had somehow become editable. The rule below separates them.
                Theme.KeyValueRow(RoomManagerLabel, NameOrMissing(_cachedRoomManager));
                Theme.KeyValueRow(PlayerLabel, PlayerSummary());
                Theme.KeyValueRow(CharactersLabel, CharacterSummary());
                Theme.KeyValueRow(RoomCharactersLabel, RoomCharacterSummary());

                // Shown only once a Scene Installer exists. A row reading "None" in a project that
                // has never heard of one teaches nothing and costs a beginner a search.
                bool hasSceneInstaller = _cachedSceneInstaller != null || _discoveredSceneInstaller != null;
                if (hasSceneInstaller)
                    Theme.KeyValueRow(SceneInstallerLabel, NameOrNone(_cachedSceneInstaller));

                bool showsPlayerField = _cachedPlayers.Count > 1 || ExplicitPlayer != null;

                // Selected, not selected-and-active. A character that starts the scene disabled and
                // is enabled during play is an ordinary setup the SDK supports, and it still joins
                // the room — so which of the two speaks first is a real question. Gating on the
                // active count hid the answer to it in exactly those scenes.
                bool showsInitialCharacterField =
                    SelectedCharacterCount > 1 || _initialCharacterProp?.objectReferenceValue != null;
                bool showsCharacterList = _discoveredCharacters.Count > 1 || UsesCharacterConnectionSelection;

                if (hasSceneInstaller || showsPlayerField || showsInitialCharacterField || showsCharacterList)
                    Theme.HorizontalRule(Theme.Divider);

                if (hasSceneInstaller)
                    EditorGUILayout.PropertyField(_sceneInstallerProp, SceneInstallerLabel);

                if (showsPlayerField)
                    EditorGUILayout.PropertyField(_explicitPlayerProp, PlayerLabel);

                if (showsInitialCharacterField)
                {
                    EditorGUILayout.PropertyField(_initialCharacterProp, InitialCharacterLabel);

                    // Assigning this field reads as pinning the conversation to that character, and
                    // under the automatic modes it does not: it chooses who opens the room, and the
                    // rule below moves the conversation from there. Said where the assignment is
                    // made, and only to the person who has made one.
                    if (_initialCharacterProp?.objectReferenceValue != null && SelectedCharacterCount > 1)
                    {
                        GUILayout.Space(2f);
                        GUILayout.Label(DescribeAddressingRuleForAssignedCharacter(), Theme.MutedWrapped);
                    }
                }

                if (showsCharacterList)
                    DrawCharacterConnectionSelection();

                // One closing sentence, and only when it says something the controls above have not.
                // The character list already explains itself when it is drawn, so repeating "the
                // selection decides who joins" underneath it was the same guidance twice.
                string setupNote = _cachedSceneInstaller != null
                    ? "The assigned Scene Installer decides which characters this manager owns."
                    : _discoveredSceneInstaller != null
                        ? "This scene has a Scene Installer, but it decides nothing until it is assigned above. Characters are still found automatically."
                        : showsCharacterList
                            ? null
                            : "Characters are found automatically. There is no list to keep up to date.";

                if (setupNote != null)
                {
                    GUILayout.Space(6f);
                    GUILayout.Label(setupNote, Theme.MutedWrapped);
                }
            });

            DrawConversationTargetingSection();
        }

        /// <summary>
        ///     Says, before Play Mode, that this scene will open a room no later character can join.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A room is opened for the characters that are active when it connects. One active
        ///         character means a room with no roster, and a room with no roster cannot grow: a
        ///         character enabled or spawned afterwards waits for the next connection. That rule
        ///         is in the documentation and the Console says it at the moment it bites — but the
        ///         moment it bites is halfway through Play Mode, on a scene the author believed was
        ///         a two-character scene when they left the editor.
        ///     </para>
        ///     <para>
        ///         Drawn only for the one shape that is measurable and almost always a mistake: the
        ///         project has selected more than one character for the room and exactly one of them
        ///         is active. Nothing here can see the other way into this — a character instantiated
        ///         from a prefab during play — so this claims only what it can count.
        ///     </para>
        ///     <para>
        ///         Outside the else-chain above on purpose. Those messages all answer "who speaks
        ///         first"; this one answers "what shape of room is this", and in the scene where an
        ///         inactive character is also the Initial Character both answers are worth having.
        ///     </para>
        /// </remarks>
        private void DrawSingleCharacterRoomWarning()
        {
            if (SelectedCharacterCount <= 1 || SelectedActiveCharacterCount != 1) return;

            _inactiveSelectedCharacters.Clear();
            for (int i = 0; i < _cachedCharacters.Count; i++)
            {
                ConvaiCharacter character = _cachedCharacters[i];
                if (character == null || character.isActiveAndEnabled) continue;
                if (!IsCharacterSelected(character)) continue;
                _inactiveSelectedCharacters.Add(character);
            }

            if (_inactiveSelectedCharacters.Count == 0) return;

            bool one = _inactiveSelectedCharacters.Count == 1;
            string names = string.Join(", ", _inactiveSelectedCharacters.Select(character => $"'{character.name}'"));

            WarningBox(
                "This room will not be able to grow",
                "Only one selected character is active, so the room opens for that character alone " +
                $"and keeps no roster. {names} cannot join it after it connects — a character enabled " +
                "during play waits for the next connection instead. Enable " +
                $"{(one ? "it" : "them")} before connecting, or turn off Auto Connect on the " +
                "characters and connect once every character is active.",
                one ? "Enable It" : "Enable Them",
                EnableInactiveSelectedCharacters);
        }

        /// <summary>
        ///     Turns on every selected character this scene left inactive, as one undo step.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Both halves of "inactive" are turned back on, because <c>isActiveAndEnabled</c> is
        ///         false for either and a fix that repaired only the GameObject would leave the
        ///         warning standing with nothing left for the reader to click.
        ///     </para>
        ///     <para>
        ///         Each character's own scene is marked, not the manager's. In a multi-scene setup
        ///         the characters need not live beside the manager, and marking only the manager's
        ///         scene would leave the edit unsaved in the scene it was actually made in.
        ///     </para>
        /// </remarks>
        private void EnableInactiveSelectedCharacters()
        {
            int group = Undo.GetCurrentGroup();
            for (int i = 0; i < _inactiveSelectedCharacters.Count; i++)
            {
                ConvaiCharacter character = _inactiveSelectedCharacters[i];
                if (character == null) continue;

                if (!character.gameObject.activeSelf)
                {
                    Undo.RecordObject(character.gameObject, "Enable Convai Character");
                    character.gameObject.SetActive(true);
                }

                if (!character.enabled)
                {
                    Undo.RecordObject(character, "Enable Convai Character");
                    character.enabled = true;
                }

                EditorUtility.SetDirty(character);
                EditorSceneManager.MarkSceneDirty(character.gameObject.scene);
            }

            Undo.SetCurrentGroupName("Enable Convai Characters");
            Undo.CollapseUndoOperations(group);
        }

        /// <summary>
        ///     Draws the rule that decides who the player is talking to, and only when a room can
        ///     actually hold more than one character.
        /// </summary>
        /// <remarks>
        ///     A scene with one character has nothing to decide, and a setting that cannot change
        ///     anything is noise in the one place a beginner is looking for the setting that can. It
        ///     appears when the second character does — which is also the moment somebody starts
        ///     wondering how the SDK picks between them.
        /// </remarks>
        private void DrawConversationTargetingSection()
        {
            // Counted by selection rather than by what is active right now, for the same reason the
            // Initial Character field is: a character enabled during play joins the room, and how
            // the SDK picks between two characters is a question that scene has.
            if (SelectedCharacterCount < 2 && !IsConversationTargetingCustomised) return;

            DrawSection(TargetingSectionId, "Who The Player Talks To", Glyphs.Routing, () =>
            {
                Theme.EnumSegments(_targetingModeLabel, TargetingModeOptions, _targetingModeProp);

                var mode = (ConversationTargetingMode)_targetingModeProp.enumValueIndex;
                if (mode == ConversationTargetingMode.Manual)
                {
                    GUILayout.Space(4f);
                    GUILayout.Label(
                        "Nothing moves the conversation on its own. It stays with the character that " +
                        "opened the room until your code calls ConvaiManager.TalkTo(character) — from " +
                        "a dialogue menu, a quest step, a trigger volume.",
                        Theme.MutedWrapped);
                    return;
                }

                EditorGUILayout.PropertyField(_targetingMaxDistanceProp, _targetingMaxDistanceLabel);

                if (mode == ConversationTargetingMode.LookAt)
                {
                    EditorGUILayout.PropertyField(_targetingMaxAngleProp, _targetingMaxAngleLabel);
                    EditorGUILayout.PropertyField(_targetingSwitchMarginProp, _targetingSwitchMarginLabel);
                }

                EditorGUILayout.PropertyField(_targetingSwitchDelayProp, _targetingSwitchDelayLabel);
                EditorGUILayout.PropertyField(_targetingViewCameraProp, _targetingViewCameraLabel);

                // Two paragraphs, not three. "One character speaks at a time" is a consequence of
                // the rule described immediately above it, so it belongs to that sentence rather
                // than standing as a note of its own.
                GUILayout.Space(4f);
                GUILayout.Label(
                    (mode == ConversationTargetingMode.LookAt
                        ? "The character nearest the centre of the player's view is addressed. The " +
                          "margin and the delay are what stop the conversation flickering between " +
                          "two characters standing close together. "
                        : "The character nearest the player is addressed, wherever they are looking. " +
                          "The delay is what stops the conversation flickering between two " +
                          "characters the same distance away. ") +
                    "Only one character speaks at a time, so moving the conversation ends the answer " +
                    "already in progress.",
                    Theme.MutedWrapped);

                // Stated here rather than as a warning box, and stated once. A room with several
                // characters is an account feature, and a project without it finds out in Play Mode
                // from a room that stops connecting at all — including the character that worked
                // before the second one arrived. But nothing in the editor can check the account, so
                // a box claiming a verdict it has not measured would nag every project that does
                // have access. This is the same register as the sentence above it: a fact about the
                // service, in the section that is only drawn once it applies.
                GUILayout.Space(4f);
                GUILayout.Label(
                    "A room holding several characters is a Convai account feature. If the account " +
                    "this API key belongs to does not have it, the room refuses to connect and the " +
                    "Console names the reason.",
                    Theme.MutedWrapped);
            });
        }

        /// <summary>Whether the scene has moved any targeting value off its shipped default.</summary>
        /// <remarks>
        ///     <para>
        ///         A scene that tuned these and later dropped to one character would otherwise lose
        ///         the section — and with it any way to see what it had been set to.
        ///     </para>
        ///     <para>
        ///         Every value the section can change is checked, not only the two that are obvious
        ///         from the outside. Checking the mode and the camera alone made this true of exactly
        ///         the scenes that had never touched the numbers, which is the opposite of what the
        ///         paragraph above asks for: the four that actually get tuned in play — the range,
        ///         the angle, the margin and the delay — were the ones it could not see.
        ///     </para>
        /// </remarks>
        private bool IsConversationTargetingCustomised
        {
            get
            {
                if (_targetingModeProp == null) return false;
                if (_targetingModeProp.enumValueIndex != (int)ConversationTargetingMode.LookAt) return true;
                if (_targetingViewCameraProp?.objectReferenceValue != null) return true;

                ConversationTargetingOptions defaults = ConversationTargetingOptions.CreateDefault();
                return DiffersFromDefault(_targetingMaxDistanceProp, defaults.MaxDistance) ||
                       DiffersFromDefault(_targetingMaxAngleProp, defaults.MaxAngle) ||
                       DiffersFromDefault(_targetingSwitchMarginProp, defaults.SwitchMargin) ||
                       DiffersFromDefault(_targetingSwitchDelayProp, defaults.SwitchDelaySeconds);
            }
        }

        private static bool DiffersFromDefault(SerializedProperty property, float shippedDefault) =>
            property != null && !Mathf.Approximately(property.floatValue, shippedDefault);

        /// <summary>
        ///     What the manager is doing right now, including who the player is addressing and
        ///     whether that character can hear them.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This answers the questions the multi-character troubleshooting guide asks, and
        ///         until it did they were answerable only from the Console at Debug verbosity or from
        ///         the MCP diagnosis. "Is the other character ready?" and "is the conversation
        ///         holding or stuck?" are the two that decide what to do next, and neither had a
        ///         visible answer anywhere else in the editor.
        ///     </para>
        ///     <para>
        ///         <b>Addressed, not active.</b> The row names
        ///         <see cref="ConvaiManager.AddressedCharacter" /> rather than the host active
        ///         character, because in a room with a roster those are two different characters
        ///         whenever targeting has moved the conversation — and the one the player is talking
        ///         to is the one they are asking about.
        ///     </para>
        /// </remarks>
        protected override void DrawLiveSection()
        {
            DrawSection(LiveSectionId, "Live", Glyphs.Live, () =>
            {
                IReadOnlyList<ConvaiCharacter> characters = Manager.Characters;
                ConvaiCharacter addressed = Manager.AddressedCharacter;
                ConvaiConversationAvailability availability = Manager.ConversationAvailability;
                MultiCharacterRoomSession session = LiveMultiCharacterSession;

                Theme.KeyValueRow(
                    ConnectedLabel,
                    Manager.IsConnected ? "Yes" : "No",
                    Manager.IsConnected ? Theme.StatusReady : Theme.TextMuted);
                Theme.KeyValueRow(PlayerLabel, NameOrNone(Manager.Player));
                Theme.KeyValueRow(CharactersLabel, characters.Count.ToString());
                Theme.KeyValueRow(RoomMembersLabel, LiveRoomMemberSummary(session));
                Theme.KeyValueRow(SpeakingToLabel, NameOrNone(addressed));
                Theme.KeyValueRow(
                    _availabilityLabel,
                    DescribeAvailability(availability),
                    AvailabilityTint(availability));

                if (Manager.IsConnected)
                    Theme.KeyValueRow(
                        _roomShapeLabel,
                        session != null ? "Several characters" : "One character only",
                        session != null ? Theme.StatusInfo : Theme.TextMuted);

                if (session == null) return;

                Theme.KeyValueRow(
                    _targetingVerdictLabel,
                    ObjectNames.NicifyVariableName(Manager.ConversationTargetingStatus.ToString()));

                GUILayout.Space(6f);
                Theme.GroupCaption(_rosterGroupLabel);
                DrawRoster(session);
            }, accent: Theme.StatusInfo);
        }

        /// <summary>
        ///     The live room roster, or <c>null</c> when this room has none.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Read from the room manager rather than cached: a roster edited during play is the
        ///         case this exists to show, and an inspector that cached it would show the roster the
        ///         room had when it was last selected.
        ///     </para>
        ///     <para>
        ///         The manager own GameObject is asked first. A manager that persists across scenes
        ///         leaves its scene during <c>Awake</c>, and the scene scan that finds the room in Edit
        ///         Mode then finds nothing — which would hide the roster in exactly the running scenes
        ///         it is for. In the canonical setup both components share one GameObject, so this
        ///         costs one <c>GetComponent</c>.
        ///     </para>
        /// </remarks>
        private MultiCharacterRoomSession LiveMultiCharacterSession
        {
            get
            {
                ConvaiRoomManager room = Manager.GetComponent<ConvaiRoomManager>() ?? _cachedRoomManager;
                return room != null ? room.CurrentMultiCharacterSession : null;
            }
        }

        /// <summary>
        ///     One row per character in the room, saying what the service thinks of each.
        /// </summary>
        /// <remarks>
        ///     A failed membership carries the only copy of why it failed, so the row prints it. The
        ///     alternative is a character listed as simply not working, which sends the reader into
        ///     the scene to look for a fault that is not there.
        /// </remarks>
        private void DrawRoster(MultiCharacterRoomSession session)
        {
            IReadOnlyList<CharacterRoomMembership> memberships = session.Characters;
            if (memberships.Count == 0)
            {
                GUILayout.Label("The room has no members yet.", Theme.MutedWrapped);
                return;
            }

            for (int i = 0; i < memberships.Count; i++)
            {
                CharacterRoomMembership membership = memberships[i];
                bool isTarget = string.Equals(
                    membership.MembershipId,
                    session.ActiveMembershipId,
                    StringComparison.Ordinal);

                string name = membership.Character?.CharacterName;
                if (string.IsNullOrWhiteSpace(name)) name = membership.CharacterId;
                if (string.IsNullOrWhiteSpace(name)) name = "Unnamed character";

                // A member whose GameObject has been turned off is the one case where the room and
                // the scene disagree, and it is exactly the case worth seeing: the room can still
                // report it Ready while there is nothing in the scene to talk to. Said first,
                // because it outranks whatever the room believes.
                bool sceneCharacterIsGone = membership.Character is Behaviour { isActiveAndEnabled: false };

                string status = sceneCharacterIsGone ? "Not in the scene" : membership.Status.ToString();
                if (isTarget) status += " · addressed";
                if (membership.IsInitial) status += " · opened the room";

                Theme.KeyValueRow(
                    new GUIContent(name),
                    status,
                    sceneCharacterIsGone
                        ? Theme.StatusError
                        : membership.Status switch
                        {
                            CharacterRoomStatus.Ready => Theme.StatusReady,
                            CharacterRoomStatus.Failed => Theme.StatusError,
                            _ => Theme.StatusWarn
                        });

                // A note belongs to the row above it, and without a gap underneath it reads as
                // belonging to the row below instead — which in a roster means attributing one
                // character's problem to another.
                if (sceneCharacterIsGone)
                {
                    GUILayout.Label(
                        "    Disabled or destroyed, but still holding a seat. It leaves the room on " +
                        "the next roster edit; if it does not, enable Debug logging for the reason.",
                        Theme.MutedWrapped);
                    GUILayout.Space(6f);
                }
                else if (membership.Status == CharacterRoomStatus.Failed &&
                         !string.IsNullOrWhiteSpace(membership.FailureCode))
                {
                    GUILayout.Label($"    {membership.FailureCode}", Theme.MutedWrapped);
                    GUILayout.Space(6f);
                }
            }
        }

        /// <summary>The verdict in the words a reader needs, not only its enum name.</summary>
        private static string DescribeAvailability(ConvaiConversationAvailability availability) =>
            availability switch
            {
                ConvaiConversationAvailability.NoCharacter => "No — nobody is being addressed",
                ConvaiConversationAvailability.Offline => "No — the room is not connected",
                ConvaiConversationAvailability.Connecting => "Not yet — connecting",
                ConvaiConversationAvailability.Preparing => "Not yet — this character is still joining",
                ConvaiConversationAvailability.Ready => "Yes",
                ConvaiConversationAvailability.Answering => "Yes — the character is answering",
                ConvaiConversationAvailability.Unavailable => "No — this character is unavailable",
                _ => availability.ToString()
            };

        private static Color AvailabilityTint(ConvaiConversationAvailability availability) =>
            availability switch
            {
                ConvaiConversationAvailability.Ready => Theme.StatusReady,
                ConvaiConversationAvailability.Answering => Theme.StatusReady,
                ConvaiConversationAvailability.Unavailable => Theme.StatusError,
                ConvaiConversationAvailability.Connecting => Theme.StatusWarn,
                ConvaiConversationAvailability.Preparing => Theme.StatusWarn,
                _ => Theme.TextMuted
            };

        private static string NameOrMissing(Object value) => value != null ? value.name : "Missing";

        private static string NameOrNone(Object value) => value != null ? value.name : "None";

        private string PlayerSummary()
        {
            if (ExplicitPlayer != null)
                return $"{ExplicitPlayer.name} (assigned)";

            return _cachedPlayers.Count switch
            {
                0 => "Missing",
                1 => _cachedPlayers[0].name,
                _ => $"{_cachedPlayers.Count} found"
            };
        }

        private bool UsesCharacterConnectionSelection =>
            _useCharacterConnectionSelectionProp?.boolValue == true;

        /// <summary>
        ///     Name of the character the room starts on while Initial Character is unassigned. Comes
        ///     from the same rule the runtime uses, so the Inspector cannot promise a different one.
        /// </summary>
        private string DefaultInitialCharacterName()
        {
            ConvaiCharacter resolved = ConvaiRuntimeHost.ResolveDefaultInitialCharacter(
                _cachedCharacters,
                UsesCharacterConnectionSelection,
                _includedCharacters);
            return resolved != null ? resolved.gameObject.name : "the first character";
        }

        private bool IsCharacterSelected(ConvaiCharacter character) =>
            _cachedCharacters.Contains(character) &&
            ConvaiRuntimeHost.IsCharacterSelectedForConnection(
                character,
                UsesCharacterConnectionSelection,
                _includedCharacters);

        /// <summary>The configured targeting mode, defaulting the way the runtime does.</summary>
        private ConversationTargetingMode TargetingMode => _targetingModeProp != null
            ? (ConversationTargetingMode)_targetingModeProp.enumValueIndex
            : ConversationTargetingMode.LookAt;

        /// <summary>
        ///     Who the player talks to in a scene that has left the starting character to the SDK.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The rule comes first and the named character second, because they are not equally
        ///         important and the earlier wording put them the other way round. A box headed with
        ///         one character's name reads as an instruction — that this is the character the
        ///         player has to talk to, or the one about to say something — and neither is true.
        ///         Nothing here obliges anybody: the room simply has to open on somebody, and this
        ///         one is first in the scene.
        ///     </para>
        ///     <para>
        ///         The named character is only ever reached when the rule has nobody to pick, so it
        ///         is described as what it is — the fallback — rather than as the start of a
        ///         sequence. Under <see cref="ConversationTargetingMode.Manual" /> there is no rule
        ///         to fall back from, so that reading says so plainly instead.
        ///     </para>
        ///     <para>
        ///         Only drawn where a second character exists, which is also the condition for the
        ///         section it points at being drawn. A sentence sending the reader to a section that
        ///         is not on screen would be worse than saying nothing.
        ///     </para>
        /// </remarks>
        private string DescribeAddressingRule(string startingCharacterName) => TargetingMode switch
        {
            ConversationTargetingMode.Manual =>
                "Nothing moves the conversation on its own: your own code chooses who the player " +
                $"talks to, with ConvaiManager.TalkTo. It starts on {startingCharacterName}, the " +
                "first character in the scene — assign Initial Character below to start on a " +
                "different one.",
            ConversationTargetingMode.Proximity =>
                "The player talks to whichever character is nearest — the rule is Who The Player " +
                $"Talks To below. Until somebody is near enough, {startingCharacterName} is the one " +
                "being addressed, as the first character in the scene. Assign Initial Character " +
                "below to use a different one.",
            _ =>
                "The player talks to whichever character they are looking at — the rule is Who The " +
                $"Player Talks To below. Until they look at somebody, {startingCharacterName} is the " +
                "one being addressed, as the first character in the scene. Assign Initial Character " +
                "below to use a different one."
        };

        /// <summary>
        ///     What an assigned Initial Character does and does not decide.
        /// </summary>
        /// <remarks>
        ///     Assigning this field reads as pinning the conversation to that character. Under either
        ///     automatic mode it does not: targeting re-evaluates fifteen times a second from the
        ///     moment the room is ready, so the assignment holds only until the player looks at, or
        ///     walks up to, somebody else.
        /// </remarks>
        private string DescribeAddressingRuleForAssignedCharacter() => TargetingMode switch
        {
            ConversationTargetingMode.Manual =>
                "Nothing moves the conversation away from this character on its own — your own code " +
                "moves it, with ConvaiManager.TalkTo.",
            ConversationTargetingMode.Proximity =>
                "This is who the player is addressing before anybody is near enough. Once the room " +
                "is live they talk to whichever character is nearest — see Who The Player Talks To " +
                "below.",
            _ =>
                "This is who the player is addressing before they look at anybody. Once the room is " +
                "live they talk to whichever character they are looking at — see Who The Player " +
                "Talks To below."
        };

        private string CharacterSummary()
        {
            if (_cachedCharacters.Count == 0) return "None";

            if (_discoveredCharacters.Count == _cachedCharacters.Count)
                return ActiveCharacterCount == _cachedCharacters.Count
                    ? _cachedCharacters.Count.ToString()
                    : $"{_cachedCharacters.Count} — {_cachedCharacters.Count - ActiveCharacterCount} disabled";

            return $"{_discoveredCharacters.Count} — {_cachedCharacters.Count} managed here";
        }

        /// <summary>
        ///     How many of this scene's characters will be in the room, counted the way the runtime
        ///     counts them.
        /// </summary>
        /// <remarks>
        ///     A disabled character is reported separately rather than folded into the total. It is
        ///     the one difference between what the scene looks like and what the room will hold, and
        ///     it is the difference somebody is looking for when a character does not answer.
        /// </remarks>
        private string RoomCharacterSummary()
        {
            int owned = _cachedCharacters.Count;
            if (owned == 0) return "None";

            int joining = SelectedActiveCharacterCount;
            int held = SelectedCharacterCount - joining;

            string counted = !UsesCharacterConnectionSelection && joining == owned
                ? $"All {owned}"
                : $"{joining} of {owned}";

            return held > 0 ? $"{counted} — {held} disabled" : counted;
        }

        /// <summary>How many characters the live room actually opened for.</summary>
        /// <remarks>
        ///     A room without a roster holds exactly the character it was opened for, and reporting
        ///     that as "1" rather than leaving the row blank is what makes the Room Holds row below
        ///     it read as a fact about this room rather than a fault.
        /// </remarks>
        private string LiveRoomMemberSummary(MultiCharacterRoomSession session)
        {
            if (session != null) return session.Characters.Count.ToString();
            return Manager.IsConnected ? "1" : "None yet";
        }

        private void DrawCharacterConnectionSelection()
        {
            GUILayout.Space(10f);
            Theme.GroupCaption(CharacterSelectionCaption);
            GUILayout.Label(
                UsesCharacterConnectionSelection
                    ? "Only the ticked characters join. A character added to the scene later starts unticked."
                    : "Every character here joins. Untick one to leave it out of the room.",
                Theme.MutedWrapped);

            GUILayout.Space(4f);

            // The rows live in their own recessed panel. Loose in the body they were a run of bare
            // tick boxes with no more separation from the fields above them than the fields had
            // from each other, and a reader could not see where the list began or ended.
            using (Theme.PanelScope())
            {
                int rowIndex = 0;
                for (int i = 0; i < _discoveredCharacters.Count; i++)
                {
                    ConvaiCharacter character = _discoveredCharacters[i];
                    if (character == null) continue;
                    DrawCharacterSelectionRow(character, rowIndex++);
                }
            }

            GUILayout.Space(6f);
            DrawCharacterSelectionButtons();
        }

        /// <summary>
        ///     One character's row: the tick that decides whether it joins, and the one thing the
        ///     tick cannot say about it.
        /// </summary>
        /// <remarks>
        ///     Drawn into a reserved rect rather than a horizontal layout scope so the row has a
        ///     height of its own and a striped background. A list whose rows are only as tall as
        ///     their tick box reads as loose controls that happen to be stacked; the stripe and the
        ///     breathing space are what make it read as one list with rows in it.
        /// </remarks>
        private void DrawCharacterSelectionRow(ConvaiCharacter character, int rowIndex)
        {
            bool owned = _cachedCharacters.Contains(character);
            bool selected = owned && IsCharacterSelected(character);

            // The tick box already says whether a character joins, so the status speaks only for
            // the two cases it cannot: a character this manager does not control, and one that is
            // ticked but switched off in the scene.
            string status = !owned
                ? "Not managed here"
                : !character.isActiveAndEnabled
                    ? "Disabled in scene"
                    : string.Empty;

            Rect row = GUILayoutUtility.GetRect(0f, CharacterRowHeight, GUILayout.ExpandWidth(true));
            if (rowIndex % 2 == 1)
                Theme.Fill(row, Theme.RowAlt);

            float statusWidth = status.Length == 0
                ? 0f
                : Mathf.Min(Theme.TextWidth(Theme.MicroLabelRight, status) + 8f, row.width * 0.5f);

            var toggleRect = new Rect(
                row.x + CharacterRowInset,
                row.y,
                Mathf.Max(0f, row.width - statusWidth - (CharacterRowInset * 2f)),
                row.height);

            using (new EditorGUI.DisabledScope(!owned))
            {
                bool nextSelected = EditorGUI.ToggleLeft(toggleRect, character.gameObject.name, selected);
                if (nextSelected != selected)
                    SetCharacterSelected(character, nextSelected);
            }

            if (statusWidth <= 0f) return;

            var statusRect = new Rect(
                row.xMax - statusWidth - CharacterRowInset, row.y, statusWidth, row.height);
            GUI.Label(statusRect, status, Theme.MicroLabelRightTinted(Theme.TextMuted));
        }

        /// <summary>
        ///     The two whole-list gestures, as the section's secondary-action row.
        /// </summary>
        /// <remarks>
        ///     They were a pair of full-width slabs directly under the last tick box, which gave
        ///     them the weight of the section's primary action while what they actually do is save
        ///     three clicks. Ghost buttons at their natural width read as what they are.
        /// </remarks>
        private void DrawCharacterSelectionButtons()
        {
            int clicked = Theme.GhostButtonRow(IncludeEveryoneButton, IncludeNobodyButton);
            if (clicked < 0) return;

            // Include Everyone clears the selection flag; Include Nobody sets it with an empty list.
            _useCharacterConnectionSelectionProp.boolValue = clicked == 1;
            _includedCharactersProp.arraySize = 0;
            _includedCharacters.Clear();
        }

        private void SetCharacterSelected(ConvaiCharacter character, bool selected)
        {
            if (!UsesCharacterConnectionSelection)
            {
                _useCharacterConnectionSelectionProp.boolValue = true;
                _includedCharacters.Clear();
                _includedCharacters.AddRange(_cachedCharacters);
            }

            if (selected)
            {
                if (!_includedCharacters.Contains(character))
                    _includedCharacters.Add(character);
            }
            else
            {
                _includedCharacters.Remove(character);
            }

            WriteCharacterReferences(_includedCharactersProp, _includedCharacters);
        }

        private static T FindSceneComponent<T>(Scene scene) where T : Component
        {
            var components = new List<T>();
            RefreshSceneComponents(scene, components);
            return components.Count > 0 ? components[0] : null;
        }

        internal static bool CanResolvePlayer(int discoveredPlayerCount, ConvaiPlayer explicitPlayer) =>
            explicitPlayer != null || discoveredPlayerCount == 1;

        private static IReadOnlyList<ConvaiCharacter> ReadCharacterReferences(SerializedProperty property)
        {
            var characters = new List<ConvaiCharacter>();
            if (property == null || !property.isArray) return characters;

            for (int i = 0; i < property.arraySize; i++)
                if (property.GetArrayElementAtIndex(i).objectReferenceValue is ConvaiCharacter character)
                    characters.Add(character);

            return characters;
        }

        private static void WriteCharacterReferences(
            SerializedProperty property,
            IReadOnlyList<ConvaiCharacter> characters)
        {
            if (property == null || !property.isArray) return;

            property.arraySize = characters?.Count ?? 0;
            for (int i = 0; i < property.arraySize; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = characters[i];
        }

        private static void RefreshLoadedSceneComponents<T>(ICollection<T> destination) where T : Component
        {
            destination.Clear();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                AppendSceneComponents(SceneManager.GetSceneAt(sceneIndex), destination);
        }

        private static void RefreshSceneComponents<T>(Scene scene, ICollection<T> destination)
            where T : Component
        {
            destination.Clear();
            AppendSceneComponents(scene, destination);
        }

        private static void AppendSceneComponents<T>(Scene scene, ICollection<T> destination)
            where T : Component
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                T[] components = roots[i].GetComponentsInChildren<T>(true);
                for (int j = 0; j < components.Length; j++)
                    if (components[j] != null)
                        destination.Add(components[j]);
            }
        }
    }
}
