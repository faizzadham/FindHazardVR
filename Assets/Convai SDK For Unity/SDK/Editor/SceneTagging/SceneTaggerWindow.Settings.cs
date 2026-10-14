using System.Linq;
using UnityEditor;
using UnityEngine;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.SceneTagging
{
    internal sealed partial class SceneTaggerWindow
    {
        private static readonly GUIContent SceneContextLabel = new("Scene context");
        private static readonly GUIContent FrozenFocusLabel = new("Description focus");
        private static readonly GUIContent EditSetupLabel = new("Edit in setup");
        private static readonly GUIContent DiscoveryFilteringLabel = new("Discovery filtering", "Applied to Current Scene discovery. Explicitly selected objects bypass this threshold; uncapturable or hard-excluded objects remain excluded.");
        private static readonly GUIContent DuplicateObjectsLabel = new("Duplicate objects", "Only geometry- or image-verified copies can group. Nearby clusters span up to 10 m horizontally and 3 m vertically. Navigation uses a physical member. After the first Apply, this review keeps its grouping plan.");
        private static readonly GUIContent AutoIncludeLabel = new("Auto-include suggestions");
        private static readonly GUIContent IncludeMatchingLabel = new("Include matching");
        private static readonly GUIContent UnityOptionsLabel = new("Unity options");
        private static readonly GUIContent MinimumRelevanceLabel = new("Minimum local relevance", "Filters objects locally before capture. Higher values analyze fewer objects.");
        private static readonly GUIContent MinimumConfidenceLabel = new("Minimum AI confidence", "AI confidence is uncalibrated. Inspect included items before applying.");
        private static readonly GUIContent ResetDecisionsLabel = new("Reset decisions", "Include all unapplied suggestions again. Text and notes are kept.");
        private static readonly GUIContent OptionExcludePlainEngineShapes = new("Exclude plain engine shapes", "Skip low-information built-in primitives during automatic discovery. Explicit selection bypasses this filter.");
        private static readonly GUIContent OptionCandidateLimit = new("Candidate limit", "Maximum objects retained from one discovery run.");
        private static readonly GUIContent OptionConcurrentRequests = new("Concurrent requests", "Maximum simultaneous Vision requests; more requests may increase backend load.");
        private static readonly GUIContent OptionReuseMetadataAcrossRuns = new("Reuse metadata across runs", "Reuse matching names and descriptions from the local cache. Previous images are unavailable.");
        private static readonly GUIContent OptionSaveDebugCapturesLocally = new("Save debug captures locally", "Write captured images under Library/ConvaiSceneTagger for troubleshooting.");
        private static readonly GUIContent OptionRuntimeSpatialContext = new("Runtime spatial context", "Publish object location and spatial facts to characters at runtime.");
        private static readonly GUIContent OptionActionTargets = new("Action targets", "Create owned action targets for approved objects when applying.");
        private static readonly GUIContent OptionGazeTargets = new("Gaze targets", "Create optional gaze companions through the installed embodiment integration.");
        private static readonly GUIContent OptionGenerateStandingPositions = new("Generate standing positions", "Query the existing NavMesh for reachable positions facing each approved object. No NavMesh is baked.");
        private static readonly GUIContent[] GroupingOptions = { new("Keep separate"), new("Nearby clusters (recommended)"), new("Group all verified") };

        private sealed class GuidancePopup : PopupWindowContent
        {
            private readonly SceneTaggerWindow _owner;
            private Vector2 _scroll;
            internal GuidancePopup(SceneTaggerWindow owner) => _owner = owner;
            public override Vector2 GetWindowSize() => new(440f, 370f);
            public override void OnGUI(Rect rect)
            {
                Theme.EnsureStyles();
                using var panel = new EditorGUILayout.VerticalScope(EditorStyles.inspectorDefaultMargins);
                GUILayout.Label("Guidance used", Theme.CardTitle);
                var settings = _owner.Session.RunSettings;
                Theme.Paragraph(settings == null ? "No optional guidance. This review used object images only." : $"Character: {settings.CharacterName} · {settings.Quality} image detail", Theme.CaptionWrapped);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                using (new EditorGUI.DisabledScope(true))
                {
                    Theme.GroupCaption(SceneContextLabel);
                    Theme.ProseField(GUIContent.none, settings?.SceneDescription ?? "", "Convai.SceneTagger.FrozenContext", null, 60f);
                    Theme.GroupCaption(FrozenFocusLabel);
                    Theme.ProseField(GUIContent.none, settings?.Focus ?? "", "Convai.SceneTagger.FrozenFocus", null, 60f);
                }
                EditorGUILayout.EndScrollView();
                Theme.Paragraph("Locked to this review so every retry and checked-row analysis uses the same guidance. Images remain authoritative.", Theme.CaptionWrapped);
                using (new EditorGUI.DisabledScope(!_owner.CanBatch))
                    if (Theme.GhostButtonLayout(EditSetupLabel)) { editorWindow.Close(); _owner.RequestMode(Mode.Setup); }
            }
        }

        private sealed class SettingsPopup : PopupWindowContent
        {
            private readonly SceneTaggerWindow _owner;
            private Vector2 _scroll;
            internal SettingsPopup(SceneTaggerWindow owner) => _owner = owner;
            public override Vector2 GetWindowSize() => new(420f, _owner.Session.Rows.Count > 0 ? 560f : 440f);
            public override void OnGUI(Rect rect)
            {
                Theme.EnsureStyles();
                SceneTaggerSession session = _owner.Session;
                SceneTaggerSettings settings = session.Settings;
                using var disabled = new EditorGUI.DisabledScope(!_owner.Interactive || _owner._editingView);
                using var panel = new EditorGUILayout.VerticalScope(EditorStyles.inspectorDefaultMargins);
                float previousLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 170f;
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                GUILayout.Label("Settings", Theme.CardTitle);
                GUILayout.Space(4f);
                Theme.GroupCaption(DiscoveryFilteringLabel);
                settings.Relevance = EditorGUILayout.Slider(MinimumRelevanceLabel, settings.Relevance, 0, 1);
                Theme.Paragraph("Current Scene discovery only. Selected objects bypass it.", Theme.CaptionWrapped);
                GUILayout.Space(8f);
                Theme.GroupCaption(DuplicateObjectsLabel);
                using (new EditorGUI.DisabledScope(session.ApplyStarted))
                {
                    int grouping = Theme.SegmentedPicker(GroupingOptions, (int)settings.Grouping);
                    if (grouping >= 0 && (int)settings.Grouping != grouping)
                    { settings.Grouping = (SceneTaggerGrouping)grouping; session.EnsureUniqueNames(); }
                }
                Theme.Paragraph("Verified copies only. Nearby clusters span 10 m across and 3 m up. Locked after the first Apply.", Theme.CaptionWrapped);
                if (session.Rows.Count > 0)
                {
                    GUILayout.Space(8f);
                    Theme.GroupCaption(AutoIncludeLabel);
                    settings.AutoIncludeConfidence = EditorGUILayout.Slider(MinimumConfidenceLabel, settings.AutoIncludeConfidence, 0, 1);
                    if (Theme.GhostButtonLayout(IncludeMatchingLabel))
                    {
                        foreach (var row in session.Rows.Where(r => SceneTaggerReview.Ready(r) && r.Confidence >= settings.AutoIncludeConfidence)) row.Included = true;
                        session.Notify();
                    }
                    Theme.Paragraph("Runs only after analysis; it does not choose which objects are analyzed.", Theme.CaptionWrapped);
                    if (Theme.GhostButtonLayout(ResetDecisionsLabel))
                    { foreach (var row in session.Rows.Where(r => r.Status != SceneTaggerStatus.Applied)) row.Included = true; session.Notify(); }
                }
                GUILayout.Space(8f);
                Theme.GroupCaption(UnityOptionsLabel);
                settings.CandidateLimit = EditorGUILayout.IntSlider(OptionCandidateLimit, settings.CandidateLimit, 1, 300);
                settings.Concurrency = EditorGUILayout.IntSlider(OptionConcurrentRequests, settings.Concurrency, 1, 6);
                settings.ExcludeBasicShapes = EditorGUILayout.ToggleLeft(OptionExcludePlainEngineShapes, settings.ExcludeBasicShapes);
                settings.CacheEnabled = EditorGUILayout.ToggleLeft(OptionReuseMetadataAcrossRuns, settings.CacheEnabled);
                settings.DebugCaptures = EditorGUILayout.ToggleLeft(OptionSaveDebugCapturesLocally, settings.DebugCaptures);
                settings.Spatial = EditorGUILayout.ToggleLeft(OptionRuntimeSpatialContext, settings.Spatial);
                settings.Actions = EditorGUILayout.ToggleLeft(OptionActionTargets, settings.Actions);
                settings.Gaze = EditorGUILayout.ToggleLeft(OptionGazeTargets, settings.Gaze);
                settings.GenerateMovementPoints = EditorGUILayout.ToggleLeft(OptionGenerateStandingPositions, settings.GenerateMovementPoints);
                EditorGUILayout.EndScrollView();
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }
    }
}
