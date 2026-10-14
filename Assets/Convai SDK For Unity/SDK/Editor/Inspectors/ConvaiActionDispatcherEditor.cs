using Convai.Editor.Actions;
using Convai.Editor.Inspectors.Framework;
using Convai.Runtime.Actions;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai, beginner-first inspector for <see cref="ConvaiActionDispatcher" />: a header
    ///     with a live status pill, a one-line explainer, friendly policy
    ///     fields with a plain-language explanation under each selected value, a collapsed Events
    ///     card, and a Live Activity section that shows the current action and queue depth in Play
    ///     Mode. All fields go through the <see cref="SerializedObject" /> pipeline, so Undo, prefab
    ///     overrides, and revert behave exactly like the default inspector.
    /// </summary>
    /// <remarks>
    ///     Built on <see cref="ConvaiInspectorEditor" /> (Convai header/status-chip/purpose via its
    ///     declared hooks) but owns its own <see cref="OnInspectorGUI" /> so every policy field can
    ///     carry a bespoke explanation instead of the framework's generic per-field auto layout —
    ///     every serialized field of the component is drawn exactly once, by this editor.
    /// </remarks>
    [CustomEditor(typeof(ConvaiActionDispatcher))]
    internal sealed class ConvaiActionDispatcherEditor : ConvaiInspectorEditor
    {
        private const string RunSectionId = "Run";
        private const string BehaviorSectionId = "Behavior";
        private const string EventsSectionId = "Events";
        private const string LiveSectionId = "Live";

        private const string RunSectionGlyph = Glyphs.Run;
        private const string BehaviorSectionGlyph = Glyphs.Profile;
        private const string EventsSectionGlyph = Glyphs.Events;
        private const string LiveSectionGlyph = Glyphs.Live;

        private const string DisabledTitle = "Component disabled";

        private static readonly string DisabledMessage =
            ConvaiActionsEditorStrings.DispatcherDisabledBody.text + " " +
            ConvaiActionsEditorStrings.DispatcherDisabledBody.tooltip;

        private static readonly GUIContent[] BatchPolicyOptions =
        {
            new("Queue"),
            new("Replace Current"),
            new("Drop Incoming")
        };

        private static readonly GUIContent[] FailurePolicyOptions =
        {
            new("Stop Batch"),
            new("Continue Batch")
        };

        private static readonly string[] EventPropertyNames =
        {
            "_onBatchStarted",
            "_onStepStarted",
            "_onStepSucceeded",
            "_onStepFailed",
            "_onStepUnhandled",
            "_onStepCompleted",
            "_onBatchCompleted",
            "_onBatchAborted"
        };

        private SerializedProperty _batchPolicy;
        private SerializedProperty _failurePolicy;
        private SerializedProperty _speechGateTimeout;
        private SerializedProperty _stepTimeout;
        private SerializedProperty _cancelOnUserSpeech;
        private SerializedProperty _enablePerformanceReactions;
        private SerializedProperty[] _eventProperties;

        protected override string Title => ConvaiActionsEditorStrings.DispatcherTitle.text;

        protected override string Purpose => ConvaiActionsEditorStrings.DispatcherIntro.text;

        protected override GUIContent StatusChip
        {
            get
            {
                var dispatcher = (ConvaiActionDispatcher)target;
                if (EditorApplication.isPlaying)
                {
                    return dispatcher.IsProcessingLive
                        ? ConvaiActionsEditorStrings.DispatcherChipPerforming
                        : ConvaiActionsEditorStrings.DispatcherChipIdle;
                }

                return dispatcher.enabled
                    ? ConvaiActionsEditorStrings.DispatcherChipReady
                    : ConvaiActionsEditorStrings.DispatcherChipDisabled;
            }
        }

        protected override Color StatusChipTint
        {
            get
            {
                var dispatcher = (ConvaiActionDispatcher)target;
                if (EditorApplication.isPlaying)
                    return dispatcher.IsProcessingLive ? Theme.StatusReady : Theme.TextMuted;

                return dispatcher.enabled ? Theme.StatusReady : Theme.StatusWarn;
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();

            _batchPolicy = serializedObject.FindProperty("_batchPolicy");
            _failurePolicy = serializedObject.FindProperty("_failurePolicy");
            _speechGateTimeout = serializedObject.FindProperty("_speechGateTimeoutSeconds");
            _stepTimeout = serializedObject.FindProperty("_defaultStepTimeoutSeconds");
            _cancelOnUserSpeech = serializedObject.FindProperty("_cancelOnUserSpeech");
            _enablePerformanceReactions = serializedObject.FindProperty("_enablePerformanceReactions");

            _eventProperties = new SerializedProperty[EventPropertyNames.Length];
            for (int i = 0; i < EventPropertyNames.Length; i++)
                _eventProperties[i] = serializedObject.FindProperty(EventPropertyNames[i]);
        }

        /// <summary>Keeps the Live Activity panel updating while the scene plays.</summary>
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;

        protected override void DrawHeaderExtras()
        {
            var dispatcher = (ConvaiActionDispatcher)target;
            if (!dispatcher.enabled)
                WarningBox(DisabledTitle, DisabledMessage);

            DrawRunCard();
            DrawBehaviorCard();
            DrawEventsCard();
        }

        /// <summary>
        ///     Every serialized field of the dispatcher is drawn in <see cref="DrawHeaderExtras" /> with its
        ///     own plain-language explanation, so the base's generic per-field section renderer is
        ///     intentionally not used. The body slot instead carries the Play-mode-only placeholder,
        ///     since <see cref="DrawLiveSection" /> runs only while playing.
        /// </summary>
        protected override void DrawBody()
        {
            if (!EditorApplication.isPlaying)
                DrawLiveOfflineCard();
        }

        private void DrawRunCard()
        {
            DrawSection(RunSectionId, ConvaiActionsEditorStrings.DispatcherRunSectionTitle, RunSectionGlyph, () =>
            {
                Theme.EnumSegments(ConvaiActionsEditorStrings.DispatcherWhileBusyField, BatchPolicyOptions, _batchPolicy);
                DrawFieldHint(ConvaiActionDispatcherPolicyExplanations.ExplainBatchPolicy(
                    (ConvaiActionBatchPolicy)_batchPolicy.intValue));

                Theme.EnumSegments(ConvaiActionsEditorStrings.DispatcherFailureField, FailurePolicyOptions, _failurePolicy);
                DrawFieldHint(ConvaiActionDispatcherPolicyExplanations.ExplainFailurePolicy(
                    (ConvaiActionBatchFailurePolicy)_failurePolicy.intValue));

                EditorGUILayout.PropertyField(_speechGateTimeout, ConvaiActionsEditorStrings.DispatcherSpeechGateField);
                if (_speechGateTimeout.floatValue < 0f)
                    _speechGateTimeout.floatValue = 0f;

                EditorGUILayout.PropertyField(_stepTimeout, ConvaiActionsEditorStrings.DispatcherStepTimeoutField);
                if (_stepTimeout.floatValue < 0f)
                    _stepTimeout.floatValue = 0f;
                DrawFieldHint(ConvaiActionsEditorStrings.ExplainStepTimeout(_stepTimeout.floatValue));
            });
        }

        /// <summary>Plain-language hint under a policy choice, indented to the field column.</summary>
        private static void DrawFieldHint(string hint)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth + 4f);
                GUILayout.Label(new GUIContent(hint), Theme.MutedWrapped);
            }

            EditorGUILayout.Space(6f);
        }

        private void DrawBehaviorCard()
        {
            DrawSection(BehaviorSectionId, ConvaiActionsEditorStrings.DispatcherBehaviorSectionTitle,
                BehaviorSectionGlyph, () =>
                {
                    EditorGUILayout.PropertyField(_cancelOnUserSpeech, ConvaiActionsEditorStrings.DispatcherCancelOnSpeechField);
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.PropertyField(_enablePerformanceReactions, ConvaiActionsEditorStrings.DispatcherReactionsField);
                });
        }

        private void DrawEventsCard()
        {
            DrawSection(EventsSectionId, ConvaiActionsEditorStrings.DispatcherEventsSectionTitle, EventsSectionGlyph,
                () =>
                {
                    for (int i = 0; i < _eventProperties.Length; i++)
                    {
                        if (_eventProperties[i] != null)
                            EditorGUILayout.PropertyField(_eventProperties[i], true);
                    }
                },
                defaultExpanded: false);
        }

        private void DrawLiveOfflineCard()
        {
            DrawSection(LiveSectionId, ConvaiActionsEditorStrings.DispatcherLiveSectionTitle, LiveSectionGlyph,
                () => GUILayout.Label(ConvaiActionsEditorStrings.DispatcherLiveOffline, Theme.MutedWrapped),
                accent: Theme.StatusInfo);
        }

        protected override void DrawLiveSection()
        {
            DrawSection(LiveSectionId, ConvaiActionsEditorStrings.DispatcherLiveSectionTitle, LiveSectionGlyph,
                DrawLiveActivity, accent: Theme.StatusInfo);
        }

        private void DrawLiveActivity()
        {
            var dispatcher = (ConvaiActionDispatcher)target;

            bool performing = dispatcher.IsProcessingLive;
            string currentAction = dispatcher.CurrentActionDisplayNameLive;
            GUIContent stateLine = performing && !string.IsNullOrEmpty(currentAction)
                ? ConvaiActionsEditorStrings.BuildDispatcherPerforming(currentAction)
                : performing
                    ? ConvaiActionsEditorStrings.DispatcherChipPerforming
                    : ConvaiActionsEditorStrings.DispatcherLiveIdle;

            Theme.BeginPanel(performing ? Theme.StatusReady : (Color?)null);
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect dotRect = GUILayoutUtility.GetRect(14f, 16f, GUILayout.Width(14f));
                Theme.StatusDot(dotRect, performing ? Theme.StatusReady : Theme.TextMuted, performing);
                GUILayout.Space(2f);
                GUILayout.Label(stateLine, Theme.BodyWrapped);
            }

            Theme.EndPanel(4f);
            GUILayout.Label(
                ConvaiActionsEditorStrings.BuildDispatcherQueueSummary(
                    dispatcher.PendingBatchCountLive, dispatcher.StartedBatchCountLive),
                Theme.MicroLabel);
        }
    }
}
