using System;
using Convai.Editor.Inspectors.Framework;
using Convai.Runtime.Actions;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai inspector for <see cref="ConvaiActionFeedbackRelay" /> :
    ///     mode segments paired with a live plain-language explanation of the selected value, a
    ///     cooldown field, a scripted-line table (reason → line) with a token legend shown only
    ///     while a scripted mode is selected, a missing-dispatcher inline fix, and a Live section
    ///     showing the most recently composed feedback.
    /// </summary>
    /// <remarks>
    ///     Built on <see cref="ConvaiInspectorEditor" /> (Convai header/purpose/status-chip via its
    ///     declared hooks) but owns its own <see cref="OnInspectorGUI" /> so every field can carry a
    ///     bespoke explanation instead of the framework's generic per-field auto layout — every
    ///     serialized field of the component is drawn exactly once, by this editor.
    /// </remarks>
    [CustomEditor(typeof(ConvaiActionFeedbackRelay))]
    internal sealed class ConvaiActionFeedbackRelayEditor : ConvaiInspectorEditor
    {
        private const string PurposeText =
            "Lets the Convai Character react to what actually happened — explaining failures in its " +
            "own words or staying silent.";

        private const string FailureModeSectionId = "FailureMode";
        private const string SuccessModeSectionId = "SuccessMode";
        private const string TimingSectionId = "Timing";
        private const string ScriptedLinesSectionId = "ScriptedLines";
        private const string LastFeedbackSectionId = "LastFeedback";

        private static readonly GUIContent FailureModeSectionTitle = new("When An Action Fails");
        private static readonly GUIContent SuccessModeSectionTitle = new("When Everything Succeeds");
        private static readonly GUIContent TimingSectionTitle = new("Timing");
        private static readonly GUIContent ScriptedLinesSectionTitle = new("Scripted Lines");
        private static readonly GUIContent LastFeedbackSectionTitle = new("Last Feedback");

        /// <summary>One segment per <see cref="ConvaiActionFeedbackMode" /> value, in declaration order.</summary>
        private static readonly GUIContent[] FeedbackModeOptions =
        {
            new("Off", ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.Off)),
            new("Silent", ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.SilentContext)),
            new("Narrate", ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.NarrateInCharacter)),
            new("Scripted", ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.ScriptedSpeech))
        };

        private static readonly GUIContent TokenLegendBody = new(
            "Tokens you can use in the lines below: {action} — the action's name. {target} — the " +
            "target's name, if there is one. {reason} — why it failed (failure lines only).");

        private static readonly GUIContent AddScriptedLineButton = new(
            "+ Add Line",
            "Adds another failure reason → line entry.");

        private static readonly GUIContent RemoveScriptedLineButton = new(
            Glyphs.Status.Fail,
            "Remove this line.");

        private const string MissingDispatcherTitle = "Missing Action Runner";

        private const string MissingDispatcherBody =
            "This relay needs a Convai Action Runner on the same object to know when actions " +
            "start and finish.";

        private const string AddDispatcherButton = "Add Action Runner";

        private static readonly GUIContent ChipMissingDispatcher = new(
            "Missing Action Runner",
            "No Convai Action Runner was found on this object, so this relay cannot report " +
            "anything yet.");

        private static readonly GUIContent NoFeedbackYetBody = new(
            "No feedback composed yet this session. This fills in the first time a batch finishes.");

        private SerializedProperty _failureMode;
        private SerializedProperty _successMode;
        private SerializedProperty _cooldown;
        private SerializedProperty _scriptedFailureLines;
        private SerializedProperty _scriptedSuccessLine;

        private GUIContent _failureModeLabel;
        private GUIContent _successModeLabel;
        private GUIContent _cooldownLabel;
        private GUIContent _successLineLabel;

        private ConvaiActionFeedbackRelay _relay;
        private ConvaiActionDispatcher _dispatcher;
        private bool _dispatcherDirty;

        private bool _hasLastFeedback;
        private string _lastFeedbackFact = string.Empty;
        private bool _lastFeedbackNarrated;
        private DateTime _lastFeedbackTime;

        protected override string Title => "Action Feedback Relay";
        protected override string Purpose => PurposeText;

        protected override GUIContent StatusChip => _dispatcher == null ? ChipMissingDispatcher : null;
        protected override Color StatusChipTint => Theme.StatusWarn;

        protected override void OnEnable()
        {
            base.OnEnable();

            _failureMode = serializedObject.FindProperty("_failureFeedbackMode");
            _successMode = serializedObject.FindProperty("_successFeedbackMode");
            _cooldown = serializedObject.FindProperty("_cooldownSeconds");
            _scriptedFailureLines = serializedObject.FindProperty("_scriptedFailureLines");
            _scriptedSuccessLine = serializedObject.FindProperty("_scriptedSuccessLine");

            _failureModeLabel = new GUIContent("Failure Feedback Mode", _failureMode.tooltip);
            _successModeLabel = new GUIContent("Success Feedback Mode", _successMode.tooltip);
            _cooldownLabel = new GUIContent("Speech Cooldown (Seconds)", _cooldown.tooltip);
            _successLineLabel = new GUIContent("Success Line", _scriptedSuccessLine.tooltip);

            _relay = target as ConvaiActionFeedbackRelay;
            if (_relay != null)
                _relay.OnFeedbackComposed += HandleFeedbackComposed;

            EditorApplication.hierarchyChanged += MarkDispatcherDirty;
            RefreshDispatcher();
        }

        protected override void OnDisable()
        {
            if (_relay != null)
                _relay.OnFeedbackComposed -= HandleFeedbackComposed;

            EditorApplication.hierarchyChanged -= MarkDispatcherDirty;
            base.OnDisable();
        }

        private void MarkDispatcherDirty()
        {
            _dispatcherDirty = true;
            Repaint();
        }

        private void RefreshDispatcher()
        {
            _dispatcherDirty = false;
            _dispatcher = _relay != null ? _relay.GetComponent<ConvaiActionDispatcher>() : null;
        }

        private void HandleFeedbackComposed(string fact, bool narrated)
        {
            _hasLastFeedback = true;
            _lastFeedbackFact = fact ?? string.Empty;
            _lastFeedbackNarrated = narrated;
            _lastFeedbackTime = DateTime.Now;
            Repaint();
        }

        protected override void OnBeforeInspectorGUI()
        {
            if (_dispatcherDirty)
                RefreshDispatcher();
        }

        /// <summary>
        ///     Every serialized field of the relay is drawn in <see cref="DrawHeaderExtras" /> with its
        ///     own plain-language explanation, so the base's generic per-field section renderer has
        ///     nothing left to draw.
        /// </summary>
        protected override void DrawBody()
        {
        }

        protected override void DrawHeaderExtras()
        {
            DrawMissingDispatcherWarning();
            DrawModeSection(FailureModeSectionId, FailureModeSectionTitle, _failureMode, _failureModeLabel);
            DrawModeSection(SuccessModeSectionId, SuccessModeSectionTitle, _successMode, _successModeLabel);
            DrawSection(TimingSectionId, TimingSectionTitle, Glyphs.Range, DrawCooldown);
            DrawScriptedLinesSectionIfNeeded();
        }

        protected override void DrawLiveSection()
        {
            DrawSection(LastFeedbackSectionId, LastFeedbackSectionTitle, Glyphs.Live, DrawLastFeedback,
                accent: Theme.StatusInfo);
        }

        private void DrawLastFeedback()
        {
            if (!_hasLastFeedback)
            {
                GUILayout.Label(NoFeedbackYetBody, Theme.MutedWrapped);
                return;
            }

            Theme.BeginPanel(_lastFeedbackNarrated ? Theme.StatusReady : (Color?)null);
            GUILayout.Label(new GUIContent(_lastFeedbackFact), Theme.BodyWrapped);
            Theme.EndPanel(4f);

            string channel = _lastFeedbackNarrated ? "Spoken" : "Silent";
            GUILayout.Label(new GUIContent($"{channel} · {_lastFeedbackTime:T}"), Theme.MicroLabel);
        }

        private void DrawMissingDispatcherWarning()
        {
            if (_dispatcher != null)
                return;

            WarningBox(MissingDispatcherTitle, MissingDispatcherBody, AddDispatcherButton, AddDispatcher);
        }

        private void AddDispatcher()
        {
            if (_relay == null)
                return;

            Undo.AddComponent<ConvaiActionDispatcher>(_relay.gameObject);
            _dispatcherDirty = true;
        }

        private void DrawModeSection(string sectionId, GUIContent title, SerializedProperty modeProperty, GUIContent label)
        {
            DrawSection(sectionId, title, Glyphs.Routing, () =>
            {
                Theme.EnumSegments(label, FeedbackModeOptions, modeProperty);

                var mode = (ConvaiActionFeedbackMode)modeProperty.intValue;
                GUILayout.Label(new GUIContent(ConvaiActionFeedbackModeExplanations.Explain(mode)), Theme.MutedWrapped);
            });
        }

        private void DrawCooldown()
        {
            EditorGUILayout.PropertyField(_cooldown, _cooldownLabel);
            if (_cooldown.floatValue < 0f)
                _cooldown.floatValue = 0f;
        }

        private void DrawScriptedLinesSectionIfNeeded()
        {
            bool failureIsScripted = (ConvaiActionFeedbackMode)_failureMode.intValue == ConvaiActionFeedbackMode.ScriptedSpeech;
            bool successIsScripted = (ConvaiActionFeedbackMode)_successMode.intValue == ConvaiActionFeedbackMode.ScriptedSpeech;
            if (!failureIsScripted && !successIsScripted)
                return;

            DrawSection(ScriptedLinesSectionId, ScriptedLinesSectionTitle, Glyphs.Identity, () =>
            {
                Theme.BeginPanel(null);
                GUILayout.Label(TokenLegendBody, Theme.MutedWrapped);
                Theme.EndPanel(6f);

                if (successIsScripted)
                {
                    EditorGUILayout.PropertyField(_scriptedSuccessLine, _successLineLabel);
                    GUILayout.Space(6f);
                }

                if (failureIsScripted)
                    DrawScriptedFailureTable();
            });
        }

        private const string ScriptedReasonColumnTooltip = "Which failure reason this line answers.";

        private void DrawScriptedFailureTable()
        {
            for (int i = 0; i < _scriptedFailureLines.arraySize; i++)
            {
                SerializedProperty element = _scriptedFailureLines.GetArrayElementAtIndex(i);
                SerializedProperty reasonProperty = element.FindPropertyRelative("_reason");
                SerializedProperty lineProperty = element.FindPropertyRelative("_line");

                Rect row = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
                var reasonRect = new Rect(row.x, row.y, 120f, row.height);
                var lineRect = new Rect(row.x + 126f, row.y, row.width - 126f - 24f, row.height);
                var removeRect = new Rect(row.xMax - 18f, row.y + 1f, 18f, 18f);

                EditorGUI.PropertyField(reasonRect, reasonProperty, GUIContent.none);
                EditorGUI.PropertyField(lineRect, lineProperty, GUIContent.none);

                // Overlaid, non-interactive tooltip zones: EditorGUI.PropertyField with an empty
                // label reserves no label space, so a real tooltip needs a separate hover target
                // drawn on top (Labels do not consume input, so the fields underneath stay clickable).
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.Label(reasonRect, new GUIContent(string.Empty, ScriptedReasonColumnTooltip));
                    GUI.Label(lineRect, new GUIContent(string.Empty, lineProperty.tooltip));
                }

                if (Theme.IconButton(removeRect, RemoveScriptedLineButton))
                {
                    _scriptedFailureLines.DeleteArrayElementAtIndex(i);
                    GUIUtility.ExitGUI();
                }

                GUILayout.Space(2f);
            }

            GUILayout.Space(4f);
            if (Theme.GhostButtonLayout(AddScriptedLineButton, 20f))
                _scriptedFailureLines.InsertArrayElementAtIndex(_scriptedFailureLines.arraySize);
        }
    }
}
