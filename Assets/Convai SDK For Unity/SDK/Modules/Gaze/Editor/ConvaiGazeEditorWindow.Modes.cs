using System.Collections.Generic;
using Convai.Editor.UI;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Diagnostics;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Providers;
using Convai.Shared.Compatibility;
using UnityEditor;
using UnityEngine;

namespace Convai.Modules.Gaze.Editor
{
    internal sealed partial class ConvaiGazeEditorWindow
    {
        private static readonly GUIContent AddPersonalityButton = new(
            "Add a Personality", "Assigns a default Gaze Profile so this character looks around its own way.");

        private static readonly GUIContent OwnCopyButton = new(
            "Give this character its own copy",
            "Saves a copy of this personality and assigns it to this character alone.");

        private static readonly GUIContent AimPointLabel = new(
            "Aim Point",
            "Where on the player the character actually looks. Automatic suits every normal camera rig.");

        /// <summary>One segment per <see cref="GazeAnchorAimMode" /> value, in declaration order.</summary>
        private static readonly GUIContent[] AimPointOptions =
        {
            new("Automatic", "A camera's exact position, lifted to eye line on any other anchor."),
            new("Exact", "The anchor's exact world position."),
            new("Offset", "The Aim Offset below, authored in the anchor's own space.")
        };

        private static readonly GUIContent FocusStyleLabel = new(
            "Focus Style",
            "Relaxed keeps subtle eye life while focused. Locked suppresses look-aways entirely, " +
            "for presenter and kiosk characters.");

        /// <summary>One segment per <see cref="GazeFocusFidelity" /> value, in declaration order.</summary>
        private static readonly GUIContent[] FocusStyleOptions =
        {
            new("Relaxed", "Keeps subtle eye life and head gestures while focused. Right for dialogue characters."),
            new("Locked", "Suppresses look-aways while focused. Blinks and body turns remain.")
        };

        private readonly GazeSnapshot _snapshot = new();
        private UnityEditor.Editor _profileEditor;
        private ConvaiGazeProfile _profileEditorTarget;

        // ------------------------------------------------------------------ feel

        /// <summary>
        ///     The complete personality surface. The inspector shows three dials; this shows the
        ///     whole profile behind them, by hosting the profile's own inspector rather than
        ///     re-implementing it — one authored surface, no chance of the two drifting.
        /// </summary>
        private void DrawFeelMode()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(14f);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(10f);

                    SerializedObject serialized = ControllerSerialized;
                    SerializedProperty profileProperty = serialized.FindProperty("profile");

                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(profileProperty, new GUIContent(
                        "Personality",
                        "The Gaze Profile this character uses. Leave empty to use the SDK defaults."));
                    if (EditorGUI.EndChangeCheck())
                    {
                        serialized.ApplyModifiedProperties();
                        InvalidateModels();
                    }

                    var profile = profileProperty?.objectReferenceValue as ConvaiGazeProfile;
                    if (profile == null)
                    {
                        ConvaiEditorTheme.InfoBox(GazeEditorStrings.FeelNoProfileTitle, GazeEditorStrings.FeelNoProfileBody);
                        if (ConvaiEditorTheme.GhostButtonLayout(AddPersonalityButton))
                        {
                            GazeSetupService.ApplyFix(_controller, GazeFixId.AssignDefaultProfile);
                            InvalidateModels();
                        }
                        GUILayout.Space(14f);
                        return;
                    }

                    DrawSharedProfileNotice(profile);

                    GUILayout.Space(8f);
                    EnsureProfileEditor(profile);
                    _profileEditor?.OnInspectorGUI();

                    GUILayout.Space(16f);
                }
                GUILayout.Space(14f);
            }
        }

        private void EnsureProfileEditor(ConvaiGazeProfile profile)
        {
            if (_profileEditor != null && _profileEditorTarget == profile) return;

            ReleaseProfileEditor();
            _profileEditorTarget = profile;
            _profileEditor = UnityEditor.Editor.CreateEditor(profile);
        }

        /// <summary>
        ///     Destroys the hosted profile editor. A <see cref="UnityEditor.Editor" /> is a
        ///     <see cref="Object" />, so leaving one behind when the window closes or the selection
        ///     changes leaks it for the rest of the session.
        /// </summary>
        private void ReleaseProfileEditor()
        {
            if (_profileEditor != null) DestroyImmediate(_profileEditor);
            _profileEditor = null;
            _profileEditorTarget = null;
        }

        /// <summary>
        ///     Profiles are assets, so two characters can share one. Editing a shared profile
        ///     changes both, and finding that out by surprise is the trap this notice exists to
        ///     close.
        /// </summary>
        private void DrawSharedProfileNotice(ConvaiGazeProfile profile)
        {
            // Counted during the model refresh, not per repaint — it used to be a full scene scan
            // inside the draw call.
            int users = _sharedProfileUsers;
            if (users <= 1) return;

            using (ConvaiEditorFrame.Panel())
            {
                GUILayout.Label($"{GazeEditorStrings.FeelSharedNotice} ({users} characters)",
                    ConvaiEditorTheme.CaptionWrapped);
                if (ConvaiEditorTheme.GhostButtonLayout(OwnCopyButton))
                    DuplicateProfileForController(profile);
            }
        }

        private void DuplicateProfileForController(ConvaiGazeProfile profile)
        {
            string sourcePath = AssetDatabase.GetAssetPath(profile);
            if (string.IsNullOrEmpty(sourcePath))
            {
                EditorUtility.DisplayDialog("Convai Gaze",
                    "This personality is not a saved asset, so it cannot be copied.", "OK");
                return;
            }

            string destination = EditorUtility.SaveFilePanelInProject(
                "Save a copy of this personality",
                $"{_controller.name}_GazeProfile", "asset",
                "Choose where the copy lives. The character will use the copy from now on.");
            if (string.IsNullOrEmpty(destination)) return;

            if (!AssetDatabase.CopyAsset(sourcePath, destination))
            {
                EditorUtility.DisplayDialog("Convai Gaze", "The copy could not be created.", "OK");
                return;
            }

            AssetDatabase.Refresh();
            var copy = AssetDatabase.LoadAssetAtPath<ConvaiGazeProfile>(destination);
            if (copy == null) return;

            SerializedObject serialized = ControllerSerialized;
            serialized.FindProperty("profile").objectReferenceValue = copy;
            serialized.ApplyModifiedProperties();
            InvalidateModels();
        }

        // ------------------------------------------------------------------ targets

        /// <summary>
        ///     Scene-wide: everything a character can decide to look at, plus the advanced
        ///     targeting fields evicted from the component inspector so its common path stays two
        ///     controls.
        /// </summary>
        private void DrawTargetsMode()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(14f);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawSectionTitle(GazeEditorStrings.TargetsTitle);
                    DrawBody(GazeEditorStrings.TargetsBody);
                    GUILayout.Space(6f);

                    if (_targetRows.Count == 0) DrawBody(GazeEditorStrings.TargetsEmpty);
                    else
                        for (int i = 0; i < _targetRows.Count; i++)
                            DrawTargetRow(_targetRows[i]);

                    GUILayout.Space(8f);
                    DrawAddTargetButton();

                    DrawAdvancedTargeting();
                    GUILayout.Space(16f);
                }
                GUILayout.Space(14f);
            }
        }

        /// <summary>One row of the Targets list: anything in the scene a character can decide to look at.</summary>
        private readonly struct GazeTargetRow
        {
            public GazeTargetRow(GameObject owner, string state)
            {
                Owner = owner;
                State = state;
            }

            public readonly GameObject Owner;

            /// <summary>Plain-English status, including which of the two target components it is.</summary>
            public readonly string State;
        }

        private readonly List<GazeTargetRow> _targetRows = new(8);

        /// <summary>
        ///     Rebuilds the scene's target list during the model refresh rather than inside the draw
        ///     call.
        /// </summary>
        /// <remarks>
        ///     Two components can make an object a gaze candidate, and this list used to show only
        ///     one of them. A scene whose objects were marked with World Object Target — the
        ///     Actions-metadata variant — read as "nothing here yet", which is the worst possible
        ///     answer for a page whose whole job is to say what the character can look at.
        /// </remarks>
        private void RefreshTargetRows()
        {
            _targetRows.Clear();

            ConvaiGazeTarget[] targets = ConvaiObjectFind.All<ConvaiGazeTarget>(FindObjectsInactive.Include);
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == null) continue;
                _targetRows.Add(new GazeTargetRow(
                    targets[i].gameObject,
                    targets[i].isActiveAndEnabled ? "noticed while idle" : "disabled"));
            }

            WorldObjectGazeTargetProvider[] worldObjects = ConvaiObjectFind.All<WorldObjectGazeTargetProvider>(FindObjectsInactive.Include);
            for (int i = 0; i < worldObjects.Length; i++)
            {
                if (worldObjects[i] == null) continue;
                _targetRows.Add(new GazeTargetRow(
                    worldObjects[i].gameObject,
                    worldObjects[i].isActiveAndEnabled
                        ? "noticed while idle (world object)"
                        : "disabled (world object)"));
            }
        }

        private static void DrawTargetRow(GazeTargetRow row)
        {
            if (row.Owner == null) return;

            const float gap = 6f;
            Rect rect = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
            ButtonScratch.text = row.Owner.name;
            // The state is measured too, so a long object name narrows its button rather than
            // pushing the state caption into a sliver where it wraps out of this 20px row.
            float stateWidth = ConvaiEditorTextMetrics.Width(ConvaiEditorTheme.CaptionWrapped, row.State);
            float buttonWidth = Mathf.Min(
                ConvaiEditorTheme.GhostButtonWidth(ButtonScratch), Mathf.Max(0f, rect.width - gap - stateWidth));

            if (ConvaiEditorTheme.GhostButton(new Rect(rect.x, rect.y, buttonWidth, rect.height), ButtonScratch))
            {
                EditorGUIUtility.PingObject(row.Owner);
                Selection.activeGameObject = row.Owner;
            }

            float stateX = rect.x + buttonWidth + gap;
            GUI.Label(
                new Rect(stateX, rect.y, Mathf.Max(0f, rect.xMax - stateX), rect.height),
                row.State, ConvaiEditorTheme.CaptionWrapped);
            GUILayout.Space(2f);
        }

        private void DrawAddTargetButton()
        {
            GameObject selected = Selection.activeGameObject;
            bool canAdd = selected != null && selected.GetComponent<ConvaiGazeTarget>() == null;

            using (new EditorGUI.DisabledScope(!canAdd))
            {
                ButtonScratch.text = selected == null
                    ? "Select an object in the scene first"
                    : canAdd
                        ? $"{GazeEditorStrings.TargetsAddButton} ({selected.name})"
                        : $"'{selected.name}' is already marked";

                if (ConvaiEditorTheme.GhostButtonLayout(ButtonScratch, 24f))
                {
                    Undo.AddComponent<ConvaiGazeTarget>(selected);
                    InvalidateModels();
                }
            }
        }

        /// <summary>
        ///     The six controls the component inspector deliberately does not show. They exist for
        ///     split-screen, cutscene rigs, kiosks and presenters — roughly one project in ten —
        ///     and putting them on the first-run surface is what made that surface unreadable.
        /// </summary>
        private void DrawAdvancedTargeting()
        {
            DrawSectionTitle(GazeEditorStrings.TargetsAdvancedTitle);
            DrawBody(GazeEditorStrings.TargetsAdvancedBody);
            GUILayout.Space(4f);

            SerializedObject serialized = ControllerSerialized;

            // Not inside a card, so the label column a Convai card sets does not reach these rows
            // and Unity's 150px one cut the longer labels off mid-word. Sized from the right-hand
            // pane the way a card sizes it from the view.
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Round(Mathf.Clamp((position.width - LeftPaneWidth) * 0.45f, 160f, 300f));
            try
            {
                DrawAdvancedField(serialized, "autoCreatePlayerAnchor", "Create A Player Anchor Automatically",
                    "On by default. Turn this off only if you provide your own gaze target source.");
                // Read before the segments draw: a click changes the mode mid-event, and the offset row
                // appearing in that same event would ask the layout for a rect it never reserved.
                SerializedProperty aimMode = serialized.FindProperty("playerAnchorAimMode");
                bool aimsAtOffset = aimMode != null && aimMode.enumValueIndex == (int)GazeAnchorAimMode.LocalOffset;

                ConvaiEditorTheme.EnumSegments(AimPointLabel, AimPointOptions, aimMode);
                // The anchor reads the offset only in the offset mode, so it is noise in the other two.
                if (aimsAtOffset)
                    DrawAdvancedField(serialized, "playerAnchorAimOffset", "Aim Offset",
                        "Used when Aim Point is set to an offset — authored in the anchor's own space.");
                ConvaiEditorTheme.EnumSegments(FocusStyleLabel, FocusStyleOptions, serialized.FindProperty("focusFidelity"));
                DrawAdvancedField(serialized, "allowScriptedOverridesDuringExactFocus", "Let Scripts Override A Locked Focus",
                    "Whether a GazeAt() call from your code can pull a locked character's gaze away.");
                DrawAdvancedField(serialized, "lockBlocksGlances", "Ignore Glances While Focused",
                    "While focus is held, brief glances are absorbed so nothing tugs the gaze off the player.");
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }

            if (!serialized.ApplyModifiedProperties()) return;

            InvalidateModels();
            if (UnityEngine.Application.isPlaying)
            {
                // Play-mode edits go through the runtime properties so the live solve re-targets
                // immediately instead of on the next enable.
                _controller.PlayerAnchorAimMode = (GazeAnchorAimMode)serialized
                    .FindProperty("playerAnchorAimMode").intValue;
                _controller.PlayerAnchorAimOffset = serialized
                    .FindProperty("playerAnchorAimOffset").vector3Value;
                _controller.FocusFidelity = (GazeFocusFidelity)serialized
                    .FindProperty("focusFidelity").intValue;
                _controller.AllowScriptedOverridesDuringExactFocus = serialized
                    .FindProperty("allowScriptedOverridesDuringExactFocus").boolValue;
                _controller.LockBlocksGlances = serialized.FindProperty("lockBlocksGlances").boolValue;
            }
        }

        private static void DrawAdvancedField(
            SerializedObject serialized, string fieldName, string label, string tooltip)
        {
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null) return;
            EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip), true);
        }

        // ------------------------------------------------------------------ live

        private void DrawLiveMode()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(14f);
                using (new EditorGUILayout.VerticalScope())
                {
                    if (!UnityEngine.Application.isPlaying)
                    {
                        DrawSectionTitle(GazeEditorStrings.LiveOfflineTitle);
                        DrawBody(GazeEditorStrings.LiveOfflineBody);
                        return;
                    }

                    _controller.CaptureSnapshot(_snapshot);

                    DrawSectionTitle("Right now");
                    DrawLiveRow("Looking at", _snapshot.Reading.Target != null
                        ? _snapshot.Reading.Target.name
                        : _snapshot.Reading.TargetKind.ToString());
                    DrawLiveRow("Conversation state", ConvaiDialogueStateLabels.Name(_snapshot.DialogueState));
                    DrawLiveRow("How committed", _snapshot.Reading.Engagement.ToString("0.00"));
                    DrawLiveRow("Eyes", _snapshot.EyePhase);
                    DrawLiveRow("Blink", _snapshot.BlinkWeight.ToString("0.00"));
                    DrawLiveRow("Turning its body", _snapshot.IsReorienting ? "yes" : "no");
                    DrawLiveRow("Following the conversation", _snapshot.SpeakerAttention);

                    if (!float.IsNaN(_snapshot.ContactErrorDegrees))
                        DrawLiveRow("Off target by", $"{_snapshot.ContactErrorDegrees:0.0}°");
                    if (_snapshot.PlayerAttention >= 0f)
                        DrawLiveRow("You are", _snapshot.PlayerLooking ? "looking at it" : "looking away");

                    DrawSectionTitle("Angles");
                    DrawLiveRow("Head", $"{_snapshot.HeadAngles.x:0.0}° / {_snapshot.HeadAngles.y:0.0}°");
                    DrawLiveRow("Chest", $"{_snapshot.TorsoAngles.x:0.0}° / {_snapshot.TorsoAngles.y:0.0}°");
                    DrawLiveRow("Left eye", $"{_snapshot.LeftEyeAngles.x:0.0}° / {_snapshot.LeftEyeAngles.y:0.0}°");
                    DrawLiveRow("Right eye", $"{_snapshot.RightEyeAngles.x:0.0}° / {_snapshot.RightEyeAngles.y:0.0}°");

                    if (_snapshot.RecentTrace.Count > 0)
                    {
                        DrawSectionTitle("What just happened");
                        for (int i = 0; i < _snapshot.RecentTrace.Count; i++)
                        {
                            GazeTraceEntry entry = _snapshot.RecentTrace[i];
                            GUILayout.Label($"[{entry.Time:0.0}s] {entry.Message}", ConvaiEditorStyles.MicroLabel);
                        }
                    }

                    GUILayout.Space(16f);
                }
                GUILayout.Space(14f);
            }
        }

        private static void DrawLiveRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, ConvaiEditorStyles.RowLabel, GUILayout.Width(150f));
                GUILayout.Label(value, ConvaiEditorStyles.MicroLabel);
            }
        }
    }
}
