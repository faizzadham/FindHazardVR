using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Editor.UI;
using Convai.Runtime.SceneMetadata;
using UnityEditor;
using UnityEngine;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.SceneTagging
{
    /// <summary>Review mode: toolbar, object list pane, detail pane, overview pane and the Apply footer.</summary>
    internal sealed partial class SceneTaggerWindow
    {
        private const float RowHeight = 62f, RowThumb = 34f, ImageSize = 300f;

        private static readonly GUIContent ReviewSearchPlaceholder = new("Search objects, notes, errors");
        private static readonly GUIContent[] FilterOptions = { new("All"), new("Ready"), new("Attention"), new("Applied"), new("Checked") };
        private static readonly GUIContent BulkMenu = new("More ▾", "Batch operations on checked rows and the Hierarchy selection.");
        private static readonly GUIContent IncludeToggle = new("", "Include this object when applying.");
        private static readonly GUIContent CheckToggle = new("", "Check for batch operations.");
        private static readonly GUIContent GoToSetup = new("Go to Setup");
        private static readonly GUIContent ObjectsTile = new("Objects"), ReadyTile = new("Ready"), AttentionTile = new("Attention"), AppliedTile = new("Applied");
        private static readonly GUIContent CaptureHeader = new("Capture"), TextHeader = new("Approved text"), PositionsHeader = new("Standing positions");
        private static readonly GUIContent AnalyzeSaved = new("Analyze saved image"), AnalyzeNotes = new("Analyze with notes"), Recapture = new("Auto recapture & analyze");
        private static readonly GUIContent EditView = new("Edit view"), ResetView = new("Reset view"), UseView = new("Use view"), UseAndAnalyze = new("Use & analyze");
        private static readonly GUIContent FullSize = new("Full size"), FailedRetake = new("View failed retake image");
        private static readonly GUIContent NamePlaceholder = new("Approved name");
        private static readonly GUIContent DescriptionLabel = new("Description");
        private static readonly GUIContent NotesPlaceholder = new("Correction notes for the next analysis, for example: this is a fire extinguisher, not a bottle.");
        private static readonly GUIContent GeneratePositions = new("Generate standing positions"), RegeneratePositions = new("Regenerate standing positions"), ClearPositions = new("Clear generated positions");
        private static readonly GUIContent RemoveRow = new("Remove from review");
        private static readonly GUIContent MoreMenu = new(ConvaiEditorGlyphs.Affordance.Dropdown, "More operations");
        private static readonly GUIContent OrbitHint = new("Drag to orbit · Shift-drag to pan · Scroll to zoom");
        private static readonly GUIContent ScratchName = new(), ScratchStatus = new(), ScratchPill = new(), ScratchCounter = new(), ScratchPrimary = new();

        private static readonly GUIContent BackToSetup = new("Back to setup"), SelectHierarchy = new("Select in Hierarchy"), FocusLabel = new("Focus"), InspectLabel = new("Inspect"), ContinueReview = new("Continue to review");
        private static readonly GUIContent GuidanceUsed = new("Guidance used", "Inspect the frozen guidance for this review."), ImagesOnly = new("Images only", "Inspect the frozen guidance for this review.");
        private static readonly GUIContent CheckAllToggle = new("", "Check or clear all eligible rows currently shown by the search filter.");
        private static readonly GUIContent GeneratePositionsToggle = new("Generate standing positions", "Generate best-effort NavMesh positions beside included objects. Missing positions do not prevent metadata Apply.");
        private static readonly GUIContent RefineSuggestion = new("Refine suggestion", "The note applies only to this object and is not stored in the scene.");
        private static readonly GUIContent AnalyzeWithNote = new("Analyze with note", "Analyze this object's saved image using its note and editable suggestion."), ClearNote = new("Clear note");
        private static readonly GUIContent ScratchSummary = new(), ScratchTooltip = new(), ScratchRowState = new(), ScratchNote = new("Note");
        private static readonly GUIContent ScratchAttention = new(), ScratchSkipped = new(), ScratchAnalyzeChecked = new(), ScratchScore = new();

        private float _split = .36f;
        private bool _draggingSplitter;

        private void DrawReview(float contentWidth)
        {
            DrawReviewToolbar(contentWidth);
            if (Session.ShowSummary) DrawCompletionSummary();
            HandleReviewKeyboard();
            if (Session.Rows.Count == 0)
            {
                Theme.EmptyState(ConvaiEditorGlyphs.Discovery, "All suggestions were removed from this review.",
                    "Add the Hierarchy selection from More, or return to Setup to explore again.",
                    GoToSetup, () => RequestMode(Mode.Setup));
                return;
            }
            float listWidth = Mathf.Clamp(contentWidth * _split, 260f, Mathf.Max(260f, contentWidth - 480f));
            float detailWidth = contentWidth - listWidth - CardGap - 1f;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(listWidth))) DrawListPane(listWidth);
                Rect divider = GUILayoutUtility.GetRect(1f, 1f, GUILayout.Width(1f), GUILayout.ExpandHeight(true));
                if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(divider, Theme.Divider);
                Rect handle = new Rect(divider.x - 4f, divider.y, 9f, divider.height);
                EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeHorizontal);
                Event current = Event.current;
                if (current.type == EventType.MouseDown && handle.Contains(current.mousePosition)) { _draggingSplitter = true; current.Use(); }
                if (_draggingSplitter && current.type == EventType.MouseDrag)
                { _split = Mathf.Clamp(_split + current.delta.x / contentWidth, .2f, .6f); current.Use(); Repaint(); }
                if (_draggingSplitter && current.type == EventType.MouseUp)
                { _draggingSplitter = false; EditorPrefs.SetFloat(SettingsKey + ".Split", _split); current.Use(); }
                GUILayout.Space(CardGap);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(detailWidth))) DrawDetailPane();
            }
        }

        private void DrawReviewFooter(Rect row)
        {
            int ready = Session.Rows.Count(r => r.CanApply);
            ScratchPrimary.text = $"Apply {ready}";
            ScratchPrimary.tooltip = !Interactive ? "Wait for exploration to finish before applying scene changes." : _editingView ? "Finish or cancel Edit View before applying scene changes." : ready == 0 ? "Include at least one completed suggestion first." : "Add or update Convai world objects in one undoable editor operation. Saving remains a separate action.";
            int applied = Session.Rows.Count(r => r.Status == SceneTaggerStatus.Applied);
            // Right-hand controls share the footer's centre line; the two text lines sit left of them.
            var moreRect = new Rect(row.xMax - Inset - 22f, row.y + 21f, 22f, 22f);
            var applyRect = new Rect(moreRect.x - 8f - 100f, row.y + 19f, 100f, 26f);
            var toggleRect = new Rect(applyRect.x - 210f, row.y + 22f, 200f, 20f);
            float textWidth = toggleRect.x - row.x - Inset * 2f;
            GUI.Label(new Rect(row.x + Inset, row.y + 12f, textWidth, 18f), $"Included {Session.Rows.Count(r => r.Included && r.Status != SceneTaggerStatus.Applied)} · Not included {Session.Rows.Count(r => !r.Included && r.Status != SceneTaggerStatus.Applied)} · Applied {applied}", Theme.CardSubtitle);
            string summary = string.IsNullOrEmpty(Session.LastApplySummary) ? "Nothing changes in the scene until Apply." : Session.LastApplySummary;
            ScratchSummary.text = ScratchSummary.tooltip = summary;
            GUI.Label(new Rect(row.x + Inset, row.y + 32f, textWidth, 20f), ScratchSummary, string.IsNullOrEmpty(Session.LastApplySummary) ? Theme.CaptionWrapped : Theme.MicroLabelRightTinted(Theme.StatusReady));
            using (new EditorGUI.DisabledScope(!CanBatch))
            {
                Session.Settings.GenerateMovementPoints = GUI.Toggle(toggleRect, Session.Settings.GenerateMovementPoints, GeneratePositionsToggle);
                using (new EditorGUI.DisabledScope(ready == 0))
                    if (Theme.PrimaryButton(applyRect, ScratchPrimary)) Act(Session.Apply);
                if (Theme.IconButton(moreRect, MoreMenu))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Release applied images"), false, () => { DisposePreview(); Session.ReleaseAppliedImages(); });
                    menu.AddItem(new GUIContent("Remove untouched generated content from selected objects"), false, () => Act(() => SceneTaggerApplyService.RemoveGenerated(Selection.gameObjects)));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Check native libraries"), false, () => Act(() => ShowNotification(new GUIContent("Core " + SceneTaggerCoreAdapter.Instance.Version + " loaded. ABI 4. Internal unsigned release."))));
                    menu.DropDown(moreRect);
                }
            }
        }

        #region Toolbar and list pane

        private void DrawReviewToolbar(float contentWidth)
        {
            // Row 1: navigation left, batch menu right. Row 2: search and filter. Counts live in the list header.
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!Interactive || _editingView))
                {
                    if (CompactButton(BackToSetup)) RequestMode(Mode.Setup);
                    GUILayout.Space(6f);
                    SceneTaggerSettings snapshot = Session.RunSettings;
                    bool guidance = !string.IsNullOrWhiteSpace(snapshot?.SceneDescription) || !string.IsNullOrWhiteSpace(snapshot?.Focus);
                    if (CompactButton(guidance ? GuidanceUsed : ImagesOnly))
                        PopupWindow.Show(GUILayoutUtility.GetLastRect(), new GuidancePopup(this));
                }
                GUILayout.FlexibleSpace();
                if (CompactButton(BulkMenu)) ShowBulkMenu();
            }
            GUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                Rect searchRect = GUILayoutUtility.GetRect(Mathf.Min(260f, contentWidth * 0.3f), 24f, GUILayout.ExpandWidth(false));
                _search = Theme.SearchField(searchRect, _search, ReviewSearchPlaceholder,
                    "Search object names, descriptions, notes, errors and sources.", ClearSearch, "Convai.SceneTagger.ReviewSearch");
                GUILayout.Space(10f);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(Mathf.Min(420f, contentWidth * 0.4f))))
                {
                    GUILayout.Space(1f);
                    int picked = Theme.SegmentedPicker(FilterOptions, _filter);
                    if (picked >= 0) _filter = picked;
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.Space(8f);
        }

        private void ShowBulkMenu()
        {
            ShowRowMenu(() => Session.Rows.Where(r => r.Checked));
        }

        private bool CanBatch => Interactive && !_editingView;
        private void ShowRowMenu(Func<IEnumerable<SceneTaggerRow>> candidates)
        {
            var menu = new GenericMenu();
            SceneTaggerRow[] Eligible() => candidates().Where(SceneTaggerReview.Eligible).ToArray();
            void Add(string title, Func<SceneTaggerRow, bool> predicate, Action<SceneTaggerRow[]> action, bool removal = false)
            {
                SceneTaggerRow[] Candidates() => removal ? candidates().Where(SceneTaggerReview.Removable).ToArray() : Eligible();
                int count = Candidates().Count(predicate);
                var label = new GUIContent($"{title} ({count})");
                if (!CanBatch || count == 0) menu.AddDisabledItem(label);
                else menu.AddItem(label, false, () => Act(() =>
                {
                    if (!CanBatch) throw new InvalidOperationException("Finish or cancel Edit View and wait for the current operation before changing checked rows.");
                    action(Candidates().Where(predicate).ToArray()); Session.Notify();
                }));
            }
            Add("Include checked", r => SceneTaggerReview.Complete(r), rows => { foreach (var row in rows) row.Included = true; });
            Add("Exclude checked", _ => true, rows => { foreach (var row in rows) row.Included = false; });
            menu.AddSeparator("");
            Add("Analyze with notes", r => !string.IsNullOrWhiteSpace(r.Note) && SceneTaggerReview.HasUsableImage(r), rows => AnalyzeRows(rows));
            Add("Analyze all checked", SceneTaggerReview.HasUsableImage, rows => AnalyzeRows(rows));
            Add("Recapture & analyze checked", _ => true, rows => AnalyzeRows(rows, true));
            menu.AddSeparator("");
            Add("Remove checked from this review", _ => true, rows => { Session.RemoveRows(rows); if (!Session.Rows.Contains(_selected)) Select(null); }, removal: true);
            if (CanBatch) menu.AddItem(new GUIContent("Uncheck all"), false, () => { foreach (var row in Session.Rows) row.Checked = false; Repaint(); });
            else menu.AddDisabledItem(new GUIContent("Uncheck all"));
            menu.AddSeparator("");
            var add = new GUIContent($"Add selected scene objects ({Selection.gameObjects.Length})");
            if (CanBatch && Selection.gameObjects.Length > 0) menu.AddItem(add, false, () => Run(AddSelectionAsync));
            else menu.AddDisabledItem(add);
            if (CanBatch && _selected != null) menu.AddItem(new GUIContent("Replace row with selected children"), false, () => Act(ReplaceWithSelectedChildren));
            menu.ShowAsContext();
        }

        private void AnalyzeRows(SceneTaggerRow[] rows, bool recapture = false)
        {
            if (!CanBatch) { Session.ReportError("Finish or cancel Edit View before analyzing checked objects."); return; }
            foreach (var row in rows) if (row.Status == SceneTaggerStatus.Skipped) row.AllowLowInformation = true;
            Run(() => Session.RunAsync(rows, recapture));
        }

        private async System.Threading.Tasks.Task AddSelectionAsync()
        {
            if (!CanBatch) throw new InvalidOperationException("Finish or cancel Edit View before adding objects.");
            Session.Discover(true);
            ShowNotification(new GUIContent($"Added {Session.AddedCount} · Restored {Session.RestoredCount} · Skipped {Session.SkippedCount}." + (Session.AddedRows.Count > 0 ? " Analyzing selected objects…" : "")));
            await Session.RunAsync(Session.AddedRows.ToArray());
        }

        private void DrawListPane(float width)
        {
            Rect pane = EditorGUILayout.BeginVertical();
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(pane, Theme.PaneBg);
            SceneTaggerRow[] rows = VisibleRows().ToArray();
            var eligible = rows.Where(SceneTaggerReview.Removable).ToArray();
            // Header checkbox sits on the same x as the row checkboxes (card inset 8 + 7).
            Rect header = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
            bool all = eligible.Length > 0 && eligible.All(r => r.Checked);
            EditorGUI.showMixedValue = !all && eligible.Any(r => r.Checked);
            using (new EditorGUI.DisabledScope(!CanBatch || eligible.Length == 0))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUI.Toggle(new Rect(header.x + 15f, header.y + 5f, 16f, 16f), CheckAllToggle, all);
                if (EditorGUI.EndChangeCheck()) foreach (var row in eligible) row.Checked = !all;
            }
            EditorGUI.showMixedValue = false;
            int checkedCount = Session.Rows.Count(r => r.Checked);
            GUI.Label(new Rect(header.x + 36f, header.y + 4f, header.width - 44f, 18f),
                $"{rows.Length} of {Session.Rows.Count} objects" + (checkedCount > 0 ? $" · {checkedCount} checked" : ""), Theme.CardTitle);
            if (rows.Length == 0) Theme.EmptyState(ConvaiEditorGlyphs.Discovery, "No suggestions match this search.", "Your edits and decisions are unchanged.");
            GUILayout.Space(4f);
            foreach (SceneTaggerRow row in rows) DrawRowCard(row, width);
            GUILayout.Space(6f);
            EditorGUILayout.EndVertical();
        }

        private void DrawRowCard(SceneTaggerRow row, float width)
        {
            Rect slot = GUILayoutUtility.GetRect(0f, RowHeight + 4f, GUILayout.ExpandWidth(true));
            var card = new Rect(slot.x + 8f, slot.y + 2f, width - 16f, RowHeight);
            bool selected = row == _selected, hover = card.Contains(Event.current.mousePosition);
            (Color tint, string label) = StatusOf(row);

            Theme.FillRounded(card, selected ? Theme.CardBgSelected : hover ? Theme.CardBgHover : Theme.CardBg, 6f);
            Theme.StrokeRounded(card, selected ? Theme.Fade(Theme.Accent, 0.85f) : Theme.CardBorder, 6f);

            using (new EditorGUI.DisabledScope(!CanBatch || !SceneTaggerReview.Removable(row)))
                row.Checked = GUI.Toggle(new Rect(card.x + 7f, card.y + ((card.height - 16f) * 0.5f), 16f, 16f), row.Checked, CheckToggle);

            var thumb = new Rect(card.x + 28f, card.y + ((card.height - RowThumb) * 0.5f), RowThumb, RowThumb);
            Texture2D texture = row.Evidence?.Png != null ? _thumbnails.Local(row.Id + ":" + row.ImageRevision, row.Evidence.Png, 64) : null;
            if (Event.current.type == EventType.Repaint)
            {
                if (texture)
                {
                    GUI.DrawTexture(thumb, texture, ScaleMode.ScaleAndCrop, true, 0f, Color.white, 0f, 5f);
                }
                else
                {
                    Theme.FillRounded(thumb, Theme.Tint(tint), 5f);
                    GUI.Label(thumb, ConvaiEditorGlyphs.Capture, Theme.CenteredMini(tint));
                }
            }

            float textX = thumb.xMax + 8f, textWidth = card.width - (textX - card.x) - 70f;
            ScratchName.text = row.Target ? row.Target.name : "Object missing";
            ScratchName.tooltip = SceneTaggerReview.Tooltip(row);
            ScratchStatus.text = string.IsNullOrEmpty(row.Name) ? "Waiting for suggestion" : row.Name;
            ScratchStatus.tooltip = ScratchName.tooltip;
            Color previous = GUI.color;
            if (!row.Included) GUI.color = Theme.Fade(previous, previous.a * 0.5f);
            GUI.Label(new Rect(textX, card.y + 6f, textWidth, 16f), ScratchName, selected ? Theme.CardNameSelected : Theme.CardName);
            GUI.Label(new Rect(textX, card.y + 24f, textWidth, 14f), ScratchStatus, Theme.CardSubtitle);
            GUI.color = previous;

            ScratchRowState.text = label + (SceneTaggerReview.Complete(row) ? $" · {row.Confidence:P0}" : "");
            ScratchRowState.tooltip = SceneTaggerReview.Recovery(row);
            ScratchNote.tooltip = "Saved refinement note: " + row.Note;
            ScratchTooltip.tooltip = ScratchName.tooltip;
            GUI.Label(new Rect(textX, card.y + 42f, textWidth, 14f), ScratchRowState, Theme.CardSubtitle);
            if (!string.IsNullOrWhiteSpace(row.Note)) Theme.Pill(new Rect(card.xMax - 65f, card.y + 4f, 48f, 18f), ScratchNote, Theme.Accent);
            Theme.StatusDot(new Vector2(card.xMax - 48f, card.y + (card.height * 0.5f)), tint, selected || hover);
            using (new EditorGUI.DisabledScope(!CanBatch || !SceneTaggerReview.Eligible(row)))
                row.Included = GUI.Toggle(new Rect(card.xMax - 30f, card.y + ((card.height - 16f) * 0.5f), 16f, 16f), row.Included, IncludeToggle);

            var hit = new Rect(card.x + 26f, card.y, card.width - 58f, card.height);
            EditorGUIUtility.AddCursorRect(hit, MouseCursor.Link);
            Event current = Event.current;
            if (card.Contains(current.mousePosition) && current.type == EventType.ContextClick)
            { Select(row); ShowRowMenu(() => new[] { row }); current.Use(); }
            if (hit.Contains(current.mousePosition) && current.type == EventType.MouseDown && current.button == 0)
            { Select(row); if (current.clickCount == 2) FocusObject(row); current.Use(); }
            GUI.Label(hit, ScratchTooltip);
        }

        private static string RowTitle(SceneTaggerRow row) =>
            row.Name.Length > 0 ? row.Name : row.Target ? row.Target.name : "Deleted object";

        private static (Color tint, string label) StatusOf(SceneTaggerRow row)
        {
            string label = SceneTaggerReview.Status(row);
            Color tint = !row.IsCurrent || !string.IsNullOrEmpty(row.Error) ? Theme.StatusWarn : SceneTaggerReview.Complete(row) ? Theme.StatusReady : Theme.StatusIdle;
            return (tint, label);
        }

        private IEnumerable<SceneTaggerRow> VisibleRows() => SceneTaggerReview.Sort(Session.Rows.Where(r =>
            SceneTaggerReview.Matches(r, _search, r == _selected ? _selectedSearchSnapshot : null) &&
            (_filter == 0 || _filter == 1 && SceneTaggerReview.Ready(r) || _filter == 2 && SceneTaggerReview.Attention(r) || _filter == 3 && r.Status == SceneTaggerStatus.Applied || _filter == 4 && r.Checked)));

        private void HandleReviewKeyboard()
        {
            if (Event.current.type != EventType.KeyDown || Event.current.keyCode != KeyCode.Space || EditorGUIUtility.editingTextField || !CanBatch || _selected == null || !SceneTaggerReview.Removable(_selected)) return;
            _selected.Checked = !_selected.Checked; Event.current.Use(); Repaint();
        }

        private static void FocusObject(SceneTaggerRow row)
        {
            if (!row.IsCurrent) return;
            SceneView.lastActiveSceneView?.Frame(row.Bounds, false);
        }

        private void DrawReviewSummary()
        {
            // Sticky strip only when something needs a decision; zero counts would just be disabled noise.
            int attention = Session.Rows.Count(SceneTaggerReview.Attention), skipped = Session.Rows.Count(r => r.Status == SceneTaggerStatus.Skipped);
            if (attention + skipped == 0) return;
            Rect strip = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
            ScratchAttention.text = $"{attention} need attention";
            ScratchAttention.tooltip = ScratchSkipped.tooltip = "Open the analysis summary. Review images and unapplied edits are lost on script reload or Play Mode; applied scene data is kept.";
            ScratchSkipped.text = $"{skipped} skipped";
            float x = strip.x + Inset;
            float attentionWidth = Theme.GhostButtonWidth(ScratchAttention), skippedWidth = Theme.GhostButtonWidth(ScratchSkipped);
            using (new EditorGUI.DisabledScope(attention == 0))
                if (Theme.GhostButton(new Rect(x, strip.y + 4f, attentionWidth, 22f), ScratchAttention)) Session.ShowSummary = true;
            x += attentionWidth + 6f;
            using (new EditorGUI.DisabledScope(skipped == 0))
                if (Theme.GhostButton(new Rect(x, strip.y + 4f, skippedWidth, 22f), ScratchSkipped)) Session.ShowSummary = true;
            x += skippedWidth + 12f;
            GUI.Label(new Rect(x, strip.y + 7f, strip.xMax - Inset - x, 16f),
                $"{Session.Rows.Count(SceneTaggerReview.Complete)} complete · {Session.Rows.Count(SceneTaggerReview.NeedsAnalysis)} not analyzed · {Session.Rows.Count(r => r.Included && r.Status != SceneTaggerStatus.Applied)} included", Theme.MicroLabelRight);
        }

        private Vector2 _summaryScroll;
        private void DrawCompletionSummary()
        {
            using (Theme.CardScope())
            {
                GUILayout.Label("Analysis complete", Theme.CardTitle);
                int ready = Session.Rows.Count(SceneTaggerReview.Complete), attention = Session.Rows.Count(SceneTaggerReview.Attention), skipped = Session.Rows.Count(r => r.Status == SceneTaggerStatus.Skipped);
                Theme.Paragraph($"{ready} ready · {attention} need attention · {skipped} skipped", Theme.CaptionWrapped);
                Theme.Paragraph(ready == 0 ? "No suggestions are ready yet. Inspect the remaining objects to retry analysis or adjust their captures." : "Suggestions are ready for review. Nothing changes in the scene until Apply.", Theme.MutedWrapped);
                var needsAttention = Session.Rows.Where(SceneTaggerReview.Attention).ToArray();
                var skippedRows = Session.Rows.Where(r => r.Status == SceneTaggerStatus.Skipped).ToArray();
                if (needsAttention.Length + skippedRows.Length > 0)
                {
                    _summaryScroll = EditorGUILayout.BeginScrollView(_summaryScroll, GUILayout.Height(Mathf.Min(180f, 36f + (needsAttention.Length + skippedRows.Length) * 60f)));
                    if (needsAttention.Length > 0) GUILayout.Label("Needs attention", Theme.CardTitle);
                    foreach (var row in needsAttention)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(RowTitle(row), Theme.CardName);
                            if (CompactButton(InspectLabel)) { Select(row); Session.ShowSummary = false; }
                        }
                        Theme.Paragraph(SceneTaggerReview.Recovery(row), Theme.CaptionWrapped);
                    }
                    if (skippedRows.Length > 0) GUILayout.Label("Skipped before analysis", Theme.CardTitle);
                    foreach (var row in skippedRows)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            using (new EditorGUI.DisabledScope(!CanBatch)) row.Checked = EditorGUILayout.Toggle(row.Checked, GUILayout.Width(20f));
                            GUILayout.Label(RowTitle(row), Theme.CardName);
                            if (CompactButton(FocusLabel)) { Select(row); FocusObject(row); }
                        }
                        Theme.Paragraph(row.Error, Theme.CaptionWrapped);
                    }
                    EditorGUILayout.EndScrollView();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    var checkedRows = Session.Rows.Where(r => r.Checked && SceneTaggerReview.Eligible(r) && SceneTaggerReview.HasUsableImage(r)).ToArray();
                    ScratchAnalyzeChecked.text = $"Analyze checked ({checkedRows.Length})";
                    using (new EditorGUI.DisabledScope(!CanBatch || checkedRows.Length == 0))
                        if (CompactButton(ScratchAnalyzeChecked)) AnalyzeRows(checkedRows);
                    GUILayout.FlexibleSpace();
                    if (CompactButton(ContinueReview)) Session.ShowSummary = false;
                }
            }
        }

        #endregion

        #region Detail pane

        private void DrawDetailPane()
        {
            if (_selected == null || !Session.Rows.Contains(_selected)) { DrawOverviewPane(); return; }
            SceneTaggerRow row = _selected;
            (Color tint, string label) = StatusOf(row);

            Rect header = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
            ScratchPill.text = label;
            float pillWidth = Theme.PillWidth(ScratchPill, true);
            GUI.Label(new Rect(header.x, header.y, header.width - pillWidth - 8f, 26f), RowTitle(row), Theme.SelectedTitle);
            Theme.Pill(new Rect(header.xMax - pillWidth, header.y + 4f, pillWidth, 20f), ScratchPill, tint, true);
            using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(row.Target, typeof(GameObject), true);

            if (!string.IsNullOrEmpty(row.Error)) Theme.WarningBox("Needs attention", row.Error);
            bool working = row.Status == SceneTaggerStatus.Capturing || row.Status == SceneTaggerStatus.Analyzing;
            if (!working && (row.PendingAnalysis || !string.IsNullOrWhiteSpace(row.Note)))
                Theme.InfoBox("Analysis pending", "Analyze the new image or resolve the correction notes before Apply.");

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(!row.IsCurrent))
            {
                if (CompactButton(SelectHierarchy)) { Selection.activeGameObject = row.Target; EditorGUIUtility.PingObject(row.Target); }
                if (CompactButton(FocusLabel)) FocusObject(row);
            }
            int copies = Session.Rows.Count(r => r.ProofGroup == row.ProofGroup && !string.IsNullOrEmpty(row.ProofGroup));
            ScratchScore.text = $"Relevance {row.Significance:P0} · Confidence {SceneTaggerReview.ConfidenceLabel(row)} · {SceneTaggerReview.SourceLabel(row)}" + (copies > 1 ? $" · {copies} verified copies" : "");
            ScratchScore.tooltip = "Why analyzed: " + SceneTaggerReview.Reasons(row);
            GUILayout.Space(4f);
            Theme.Paragraph(ScratchScore, Theme.CaptionWrapped);
            GUILayout.Space(4f);
            DrawCaptureCard(row);
            DrawTextCard(row);
            DrawPositionsCard(row);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!CanBatch || !SceneTaggerReview.Removable(row)))
                if (CompactButton(RemoveRow)) { Session.RemoveRows(new[] { row }); Select(null); }
            }
        }

        private void DrawCaptureCard(SceneTaggerRow row)
        {
            using (Theme.CardScope())
            {
                CaptureHeader.text = _editingView ? "Adjust view" : row.PendingAnalysis ? "Saved view" : "Analysis image";
                CaptureHeader.tooltip = _editingView ? "A local preview of the current unsaved camera adjustment." : row.PendingAnalysis ? "This adjusted view is saved locally but has not been analyzed yet." : "The isolated image used by Convai Vision for this suggestion.";
                Theme.SectionHeader(ConvaiEditorGlyphs.Capture, CaptureHeader);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(ImageSize)))
                        DrawImage(row);
                    GUILayout.Space(12f);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUI.DisabledScope(!Interactive || !SceneTaggerReview.Eligible(row)))
                        {
                            if (_editingView)
                            {
                                Theme.Paragraph("Camera moves make no Vision request.", Theme.CaptionWrapped);
                                bool ready = _preview?.HasRendered == true;
                                UseView.tooltip = ready ? "Save this exact view locally without analyzing it yet. The row is checked for batch analysis." : "Wait for the live preview to become ready.";
                                UseAndAnalyze.tooltip = ready ? "Save this exact view and immediately analyze only this object." : "Wait for the live preview to become ready.";
                                using (new EditorGUI.DisabledScope(!ready))
                                {
                                    if (Theme.PrimaryButtonLayout(UseAndAnalyze, 24f)) Run(() => CommitView(true));
                                    if (Theme.GhostButtonLayout(UseView)) Run(() => CommitView(false), false);
                                }
                                if (Theme.GhostButtonLayout(CancelLabel)) DisposePreview();
                                if (Theme.GhostButtonLayout(ResetView)) Act(() => StartEditingView(row));
                            }
                            else
                            {
                                Theme.Paragraph(row.Evidence == null ? "No retained image yet." : $"Retained {row.Evidence.Png.Length / 1024} KB image · {SceneTaggerReview.SourceLabel(row)}", Theme.CaptionWrapped);
                                using (new EditorGUI.DisabledScope(!SceneTaggerReview.HasUsableImage(row)))
                                    if (Theme.PrimaryButtonLayout(string.IsNullOrWhiteSpace(row.Note) ? AnalyzeSaved : AnalyzeNotes, 24f)) AnalyzeRows(new[] { row });
                                if (Theme.GhostButtonLayout(Recapture)) { DisposePreview(); Run(() => Session.RunAsync(new[] { row }, true)); }
                                if (Theme.GhostButtonLayout(EditView)) Act(() => StartEditingView(row));
                            }
                        }
                        if (!_editingView)
                        {
                            if (row.Evidence?.Png != null && Theme.GhostButtonLayout(FullSize)) SceneTaggerImageWindow.Show(row.Evidence.Png);
                            if (row.RejectedEvidence != null && Theme.GhostButtonLayout(FailedRetake)) SceneTaggerImageWindow.Show(row.RejectedEvidence.Png);
                        }
                    }
                }
                // The placeholder already explains a missing image; only caption a real image or the live preview.
                if (_editingView || row.Evidence?.Png != null)
                    Theme.Paragraph(SceneTaggerReview.EvidenceCaption(row, _editingView), Theme.CaptionWrapped);
            }
        }

        private void StartEditingView(SceneTaggerRow row)
        {
            DisposePreview();
            _preview = new SceneTaggerCaptureService(row, (Session.RunSettings ?? Session.Settings).EvidenceSize);
            _editingView = true;
        }

        private void DrawTextCard(SceneTaggerRow row)
        {
            using (Theme.CardScope())
            {
                TextHeader.text = row.PendingAnalysis ? "Previous suggestion" : "Editable suggestion";
                Theme.SectionHeader(ConvaiEditorGlyphs.Content, TextHeader);
                using (new EditorGUI.DisabledScope(!CanBatch || !SceneTaggerReview.Eligible(row) || row.PendingAnalysis))
                {
                    EditorGUI.BeginChangeCheck();
                    NamePlaceholder.tooltip = "Use a short, useful spoken name, ideally one to three words.";
                    Rect nameRow = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
                    string name = Theme.PlaceholderTextField(new Rect(nameRow.x, nameRow.y, nameRow.width - 60f, 22f), row.Name, NamePlaceholder, "Convai.SceneTagger.Name");
                    DrawCounter(new Rect(nameRow.xMax - 56f, nameRow.y + 3f, 56f, 16f), row.Name.Length, ConvaiObjectMetadata.MaxNameLength);
                    GUILayout.Space(6f);
                    DescriptionLabel.tooltip = "Describe useful visible details in one or two sentences.";
                    string description = Theme.ProseField(DescriptionLabel, row.Description, "Convai.SceneTagger.Description");
                    Rect counterRow = GUILayoutUtility.GetRect(0f, 14f, GUILayout.ExpandWidth(true));
                    DrawCounter(counterRow, row.Description.Length, ConvaiObjectMetadata.MaxDescriptionLength);
                    if (EditorGUI.EndChangeCheck())
                    {
                        row.Name = SceneTaggerText.Limit(name, ConvaiObjectMetadata.MaxNameLength);
                        row.Description = SceneTaggerText.Limit(description, ConvaiObjectMetadata.MaxDescriptionLength);
                        row.EditRevision++;
                    }
                }
                GUILayout.Space(8f);
                Theme.GroupCaption(RefineSuggestion);
                using (new EditorGUI.DisabledScope(!CanBatch || !SceneTaggerReview.Eligible(row)))
                {
                    EditorGUI.BeginChangeCheck();
                    string note = Theme.ProseField(GUIContent.none, row.Note, "Convai.SceneTagger.Note", NotesPlaceholder, 64f);
                    if (EditorGUI.EndChangeCheck()) { row.Note = SceneTaggerText.Limit(note, SceneTaggerText.NoteLimit); row.NoteRevision++; }
                    DrawCounter(GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true)), row.Note.Length, SceneTaggerText.NoteLimit);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(row.Note) || !SceneTaggerReview.HasUsableImage(row)))
                            if (CompactButton(AnalyzeWithNote)) AnalyzeRows(new[] { row });
                        if (!string.IsNullOrEmpty(row.Note) && CompactButton(ClearNote))
                        {
                            GUI.FocusControl(null);
                            EditorGUIUtility.editingTextField = false;
                            row.Note = "";
                            row.NoteRevision++;
                        }
                    }
                }
                Theme.Paragraph(!string.IsNullOrEmpty(row.Note) && !string.IsNullOrEmpty(row.Error) ? "The note was not applied. Edit it or try Analyze with note again." : !string.IsNullOrEmpty(row.Note) && row.Included ? "Analyze this note to replace the included suggestion, or clear it to keep the current text." : "Uses the saved image. The note clears after a successful update.", Theme.CaptionWrapped);
            }
        }

        private void DrawPositionsCard(SceneTaggerRow row)
        {
            ConvaiAutoTagOwnership ownership = row.Target ? row.Target.GetComponent<ConvaiAutoTagOwnership>() : null;
            if (!ownership) return;
            using (Theme.CardScope())
            using (new EditorGUI.DisabledScope(!Interactive))
            {
                Theme.SectionHeader(ConvaiEditorGlyphs.Placement, PositionsHeader);
                bool hasPoints = row.Target.GetComponent<ConvaiObjectMovementPoints>()?.Points.Count > 0;
                int clicked = Theme.GhostButtonRow(hasPoints ? RegeneratePositions : GeneratePositions, ClearPositions);
                if (clicked == 0) Act(() => SceneTaggerMovementGenerator.Generate(row.Target, hasPoints));
                else if (clicked == 1) Act(() => SceneTaggerMovementGenerator.Clear(ownership));
            }
        }

        private static void DrawCounter(Rect rect, int length, int limit)
        {
            ScratchCounter.text = $"{length} / {limit}";
            GUI.Label(rect, ScratchCounter, Theme.MicroLabelRightTinted(length > limit ? Theme.StatusError : Theme.TextMuted));
        }

        private void DrawOverviewPane()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                Theme.StatTile(ObjectsTile, Session.Rows.Count.ToString());
                Theme.StatTile(ReadyTile, Session.Rows.Count(SceneTaggerReview.Ready).ToString(), Theme.StatusReady);
                Theme.StatTile(AttentionTile, Session.Rows.Count(SceneTaggerReview.Attention).ToString(), Theme.StatusWarn);
                Theme.StatTile(AppliedTile, Session.Rows.Count(r => r.Status == SceneTaggerStatus.Applied).ToString(), Theme.AccentBright);
            }
            GUILayout.Space(10f);
            int pending = Session.Rows.Count(SceneTaggerReview.NeedsIncludedAnalysis);
            ScratchPrimary.text = $"Analyze {pending} pending included objects";
            ScratchPrimary.tooltip = "Resume included objects that still need analysis, capturing only when no image is retained.";
            using (new EditorGUI.DisabledScope(!Interactive || pending == 0))
                if (Theme.PrimaryButtonLayout(ScratchPrimary))
                    Run(() => Session.RunAsync(Session.Rows.Where(SceneTaggerReview.NeedsIncludedAnalysis)));
            Theme.EmptyState(ConvaiEditorGlyphs.Visibility, "Select an object", "Pick an object on the left to review its capture and proposed text.");
        }

        #endregion

        #region Capture image and orbit input

        private void DrawImage(SceneTaggerRow row)
        {
            if (row.Evidence?.Png != _imageBytes)
            {
                if (_image) DestroyImmediate(_image);
                _imageBytes = row.Evidence?.Png;
                _image = _imageBytes == null ? null : new Texture2D(2, 2);
                if (_image) _image.LoadImage(_imageBytes);
            }
            Texture texture = _editingView ? _preview?.Preview : _image;
            if (!texture)
            {
                Rect empty = GUILayoutUtility.GetRect(ImageSize, ImageSize, GUILayout.Width(ImageSize), GUILayout.Height(ImageSize));
                Theme.FillRounded(empty, Theme.InnerBg, 6f);
                GUI.Label(new Rect(empty.x + 16f, empty.center.y - 45f, empty.width - 32f, 100f), SceneTaggerReview.EvidencePlaceholder(row), Theme.MutedWrapped);
                return;
            }
            Rect rect = GUILayoutUtility.GetRect(ImageSize, ImageSize, GUILayout.Width(ImageSize), GUILayout.Height(ImageSize));
            Theme.FillRounded(rect, Theme.InnerBg, 6f);
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true, 0f, Color.white, 0f, 6f);
            if (!_editingView) return;
            GUILayout.Label(OrbitHint, Theme.CenteredMini(Theme.TextMuted));
            Event current = Event.current;
            int orbitControl = GUIUtility.GetControlID(FocusType.Passive, rect);
            if (_committing) return;
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                GUIUtility.hotControl = orbitControl;
                current.Use();
            }
            if (current.type == EventType.MouseDrag && GUIUtility.hotControl == orbitControl)
            {
                if (current.shift) _preview.Pan = Vector2.ClampMagnitude(_preview.Pan + new Vector2(-current.delta.x, current.delta.y) * .003f, 1f);
                else _preview.LocalDirection = OrbitDirection(_preview.LocalDirection, current.delta);
                current.Use(); Repaint();
            }
            if (current.type == EventType.MouseUp && GUIUtility.hotControl == orbitControl)
            { GUIUtility.hotControl = 0; current.Use(); }
            if (current.type == EventType.ScrollWheel && rect.Contains(current.mousePosition))
            { _preview.DistanceScale = Mathf.Clamp(_preview.DistanceScale * Mathf.Exp(current.delta.y * .03f), .3f, 5); current.Use(); Repaint(); }
        }

        internal static Vector3 OrbitDirection(Vector3 direction, Vector2 delta)
        {
            Vector3 right = Vector3.Cross(Vector3.up, direction);
            // Core can choose a straight top/bottom view, where the usual pitch axis is zero.
            if (right.sqrMagnitude < .0001f) right = Vector3.right;
            return (Quaternion.AngleAxis(delta.x * .5f, Vector3.up) *
                Quaternion.AngleAxis(delta.y * .5f, right.normalized) * direction).normalized;
        }

        #endregion
    }
}
