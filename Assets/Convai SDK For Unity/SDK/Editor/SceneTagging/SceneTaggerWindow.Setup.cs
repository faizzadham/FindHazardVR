using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Convai.CharacterApi.Contracts.Model;
using Convai.Editor.UI;
using UnityEditor;
using UnityEngine;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.SceneTagging
{
    /// <summary>Setup mode: character card grid, scene guidance, analysis options and advanced settings.</summary>
    internal sealed partial class SceneTaggerWindow
    {
        private const float MinCardWidth = 120f, GridMaxHeight = 360f, ScrollbarWidth = 18f;

        private static readonly GUIContent CharacterHeader = new("1 · Character");
        private static readonly GUIContent GuidanceHeader = new("2 · Scene guidance");
        private static readonly GUIContent SearchPlaceholder = new("Search your characters");
        private static readonly GUIContent LoadCharacters = new("Load account characters");
        private static readonly GUIContent LoadMore = new("Load more characters");
        private static readonly GUIContent Retry = new("Retry");
        private static readonly GUIContent OpenInConvai = new("Open in Convai", "Manage this character on the Convai dashboard.");
        private static readonly GUIContent DiscoverLabel = new("Discover objects", "Find candidate objects in the scene and open Review.");
        private static readonly GUIContent DescriptionCaption = new("Scene description (optional)");
        private static readonly GUIContent DescriptionPlaceholder = new("Describe the environment and its purpose.");
        private static readonly GUIContent DraftFromView = new("Draft from Scene view", "Send the current Scene view image to Vision and fill this field with its description.");
        private static readonly GUIContent FocusCaption = new("Description focus (optional)");
        private static readonly GUIContent FocusPlaceholder = new("For example: prioritize interactive machinery and name visible controls.");
        private static readonly GUIContent OptionsHeader = new("Analysis options");
        private static readonly GUIContent ScopeCaption = new("Scope"), QualityCaption = new("Quality");
        private static readonly GUIContent[] ScopeOptions =
        {
            new("Current Scene", "Find relevant objects in the active scene. Explicit selection bypasses local relevance filtering."),
            new("Selected Objects", "Analyze the selected scene objects and their owned geometry.")
        };
        private static readonly GUIContent[] QualityOptions =
        {
            new("Fast", "Quick general descriptions using smaller object images."),
            new("Accurate", "More image detail for artwork, text and specific subjects. Takes longer; exact identification is not guaranteed.")
        };
        private static readonly GUIContent OpenSettingsLabel = new("Open Convai Settings"), ClearCharacterSearch = new("Clear search");
        private static readonly GUIContent DownloadingAvatar = new(ConvaiEditorGlyphs.Discovery, "Downloading character image…");
        private static readonly GUIContent IncludeTaggedLabel = new("Re-analyze tagged objects", "Include existing Convai world objects in discovery. Manual metadata is preserved by Apply.");
        private static readonly GUIContent ScratchCount = new();
        private static readonly GUIContent ScratchTileName = new();
        private Vector2 _gridScroll;
        private bool _drafting;
        private double _draftStarted, _nextDraftRepaint;
        private string _draftCancellationReason;
        private bool _refreshDraftField;
        private string _draftFeedback = "";

        private void DrawSetup(float contentWidth)
        {
            if (!HasCredentials)
            {
                Theme.EmptyState(ConvaiEditorGlyphs.Contract, "Connect Convai to continue", "Sign in to Convai or add an API key before using Scene Auto Tagger.", OpenSettingsLabel, OpenSettings);
                if (Theme.GhostButtonLayout(RecheckLabel)) { RecheckReadiness(); if (HasCredentials) Run(LoadCharactersAsync, false); }
                return;
            }
            if (!string.IsNullOrEmpty(_emptyDiscovery) && Session.DiscoveryReport != null)
            {
                bool canIncludeTagged = Session.DiscoveryReport.AlreadyTagged > 0 && !Session.Settings.IncludeTagged;
                Theme.InfoBox(_emptyDiscovery, Session.DiscoveryReport.EmptyMessage,
                    canIncludeTagged ? "Include tagged objects" : null,
                    canIncludeTagged ? () => { Session.Settings.IncludeTagged = true; _emptyDiscovery = ""; Session.Clear(); } : null);
            }
            using (new EditorGUI.DisabledScope(!Interactive && !_drafting))
            {
                if (position.width >= 960f)
                {
                    float left = Mathf.Floor(contentWidth * 0.52f), right = contentWidth - left - CardGap;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(left))) DrawCharacterCard(left);
                        GUILayout.Space(CardGap);
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(right))) DrawGuidanceCard();
                    }
                }
                else
                {
                    DrawCharacterCard(contentWidth);
                    DrawGuidanceCard();
                }
            }
        }

        private void DrawSetupFooter(Rect row)
        {
            string reason = ExploreDisabledReason();
            DiscoverLabel.text = Session.Settings.SelectedOnly ? "Explore selected objects" : "Explore current scene";
            DiscoverLabel.tooltip = reason ?? "Use the optional guidance, find relevant objects in the selected scope, and create editable suggestions with the configured Convai character. The scene is not modified until Apply.";
            var rect = new Rect(row.xMax - Inset - 210f, row.y + 16f, 210f, 28f);
            using (new EditorGUI.DisabledScope(reason != null))
                if (Theme.PrimaryButton(rect, DiscoverLabel)) Run(ExploreAsync);
        }

        #region Character card

        private void DrawCharacterCard(float width)
        {
            using (Theme.CardScope())
            {
                Theme.SectionHeader(ConvaiEditorGlyphs.Identity, CharacterHeader);

                if (!Catalog.Loaded)
                {
                    if (Catalog.Loading)
                        Theme.EmptyState(ConvaiEditorGlyphs.Discovery, "Loading characters", "Fetching the character list from your Convai account.");
                    else if (_catalogError.Length > 0)
                        Theme.EmptyState(ConvaiEditorGlyphs.Status.Warn, "Could not load characters", _catalogError, Retry, () => Run(LoadCharactersAsync, false));
                    else
                        Theme.EmptyState(ConvaiEditorGlyphs.Identity, "No characters loaded", "Load the characters from your Convai account to choose one.", LoadCharacters, () => Run(LoadCharactersAsync, false));
                    DrawSelectedCharacterStrip();
                    return;
                }

                Rect searchRow = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
                ScratchCount.text = $"Showing {Catalog.Visible.Count} of {Catalog.Total} characters";
                float countWidth = Theme.TextWidth(Theme.MicroLabelRight, ScratchCount) + 8f;
                string query = Theme.SearchField(new Rect(searchRow.x, searchRow.y, searchRow.width - countWidth - 8f, 24f), Catalog.Query, SearchPlaceholder,
                    "Filters by character name. Searching loads the whole account list once.", ClearSearch, "Convai.SceneTagger.CharacterSearch");
                GUI.Label(new Rect(searchRow.xMax - countWidth, searchRow.y + 4f, countWidth, 18f), ScratchCount, Theme.MicroLabelRight);
                if (query != Catalog.Query) { Catalog.Query = query; if (Catalog.NeedsSearchLoad) Run(LoadCharactersAsync, false); }
                GUILayout.Space(6f);

                DrawCharacterGrid(width - (2f * Theme.ContentInset) - ScrollbarWidth);

                if (!string.IsNullOrEmpty(_catalogError)) Theme.WarningBox("Could not load more characters", _catalogError);
                if (Catalog.HasMore)
                    using (new EditorGUI.DisabledScope(Catalog.Loading))
                        if (Theme.GhostButtonLayout(LoadMore)) Run(LoadCharactersAsync, false);

                DrawSelectedCharacterStrip();
            }
        }

        private void DrawCharacterGrid(float innerWidth)
        {
            IReadOnlyList<CharacterListItem> items = Catalog.Visible;
            if (items.Count == 0)
            {
                Theme.EmptyState(ConvaiEditorGlyphs.Discovery, "No matches", "No character name contains that text.", ClearCharacterSearch, () => Catalog.Query = "");
                return;
            }
            int columns = Mathf.Max(2, Mathf.FloorToInt((innerWidth + CardGap) / (MinCardWidth + CardGap)));
            float cardWidth = Mathf.Floor((innerWidth - (CardGap * (columns - 1))) / columns);
            float cardHeight = cardWidth + 26f;
            int rows = Mathf.CeilToInt(items.Count / (float)columns);
            float contentHeight = rows * (cardHeight + CardGap);

            _gridScroll = Theme.BeginScrollRegion(_gridScroll, contentHeight, GridMaxHeight);
            for (int r = 0; r < rows; r++)
            {
                Rect slot = GUILayoutUtility.GetRect(0f, cardHeight + CardGap, GUILayout.ExpandWidth(true));
                for (int c = 0; c < columns; c++)
                {
                    int index = (r * columns) + c;
                    if (index >= items.Count) break;
                    var card = new Rect(slot.x + (c * (cardWidth + CardGap)), slot.y, cardWidth, cardHeight);
                    CharacterListItem item = items[index];
                    bool visible = r * (cardHeight + CardGap) + cardHeight >= _gridScroll.y &&
                        r * (cardHeight + CardGap) <= _gridScroll.y + Mathf.Min(contentHeight, GridMaxHeight);
                    if (DrawCharacterTile(card, item, item.CharacterId == Session.Settings.CharacterId, visible))
                    {
                        if (_drafting && Session.Settings.CharacterId != item.CharacterId)
                        {
                            _draftCancellationReason = "Draft cancelled because the character changed. Your guidance was kept.";
                            _draftFeedback = _draftCancellationReason;
                            Session.Cancel();
                        }
                        Session.Settings.CharacterId = item.CharacterId;
                        Session.Settings.CharacterName = string.IsNullOrWhiteSpace(item.CharacterName) ? "Unnamed character" : item.CharacterName;
                        GUIUtility.keyboardControl = 0;
                    }
                }
            }
            Theme.EndScrollRegion(6f);
        }

        private bool DrawCharacterTile(Rect card, CharacterListItem item, bool selected, bool visible)
        {
            bool hover = card.Contains(Event.current.mousePosition);
            Theme.FillRounded(card, selected ? Theme.CardBgSelected : hover ? Theme.CardBgHover : Theme.CardBg, 8f);

            var image = new Rect(card.x + 1f, card.y + 1f, card.width - 2f, card.width - 2f);
            Texture2D texture = null;
            bool pending = false;
            if (visible && Event.current.type == EventType.Repaint)
            foreach (string url in SceneTaggerCharacterCatalog.ThumbnailUrls(item))
            {
                texture = _thumbnails.Remote(url, _lifetime.Token);
                pending |= _thumbnails.Pending(url);
                if (texture || !_thumbnails.Failed(url)) break;
            }
            if (Event.current.type == EventType.Repaint)
            {
                if (texture)
                {
                    GUI.DrawTexture(image, texture, ScaleMode.ScaleAndCrop, true, 0f, Color.white, Vector4.zero, new Vector4(7f, 7f, 0f, 0f));
                }
                else
                {
                    Color tint = Theme.LabelTint((item.CharacterId ?? "").GetHashCode());
                    Theme.FillRounded(image, Theme.Tint(tint), 7f);
                    GUI.Label(image, SceneTaggerThumbnailCache.Initials(item.CharacterName), Theme.TileNumberTinted(tint));
                }
            }

            if (pending) GUI.Label(new Rect(image.xMax - 24f, image.y + 5f, 20f, 20f), DownloadingAvatar, Theme.CenteredMini(Theme.TextMuted));
            ScratchTileName.text = string.IsNullOrWhiteSpace(item.CharacterName) ? "Unnamed character" : item.CharacterName;
            ScratchTileName.tooltip = ScratchTileName.text;
            GUI.Label(new Rect(card.x + 8f, image.yMax + 4f, card.width - 16f, 18f), ScratchTileName, selected ? Theme.CardNameSelected : Theme.CardName);

            Theme.StrokeRounded(card, selected ? Theme.Fade(Theme.Accent, 0.9f) : hover ? Theme.Fade(Theme.Accent, 0.4f) : Theme.CardBorder, 8f, selected ? 2f : 1f);
            if (selected) Theme.StatusDot(new Rect(card.xMax - 24f, card.y, 24f, 24f), Theme.Accent, true);

            EditorGUIUtility.AddCursorRect(card, MouseCursor.Link);
            return GUI.Button(card, GUIContent.none, GUIStyle.none);
        }

        private void DrawSelectedCharacterStrip()
        {
            GUILayout.Space(6f);
            using (Theme.PanelScope())
            {
                Rect row = GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true));
                bool none = Session.Settings.CharacterName.Length == 0;
                float buttonWidth = Theme.GhostButtonWidth(OpenInConvai);
                GUI.Label(new Rect(row.x + 8f, row.y + 2f, row.width - buttonWidth - 24f, 16f), none ? "No character selected" : Session.Settings.CharacterName, Theme.CardTitle);
                GUI.Label(new Rect(row.x + 8f, row.y + 18f, row.width - buttonWidth - 24f, 14f),
                    none ? "Pick a card above." : "Knowledge Bank and character settings", Theme.CardSubtitle);
                OpenInConvai.tooltip = "Manage " + Session.Settings.CharacterName + " in Convai, then return here.";
                using (new EditorGUI.DisabledScope(none))
                if (Theme.GhostButton(new Rect(row.xMax - buttonWidth - 8f, row.y + 7f, buttonWidth, 22f), OpenInConvai))
                    UnityEngine.Application.OpenURL("https://convai.com/dashboard/character/" + Uri.EscapeDataString(Session.Settings.CharacterId));
            }
        }

        #endregion

        #region Scene guidance card

        private void DrawGuidanceCard()
        {
            SceneTaggerSettings settings = Session.Settings;
            using (Theme.CardScope())
            {
                Theme.SectionHeader(ConvaiEditorGlyphs.Content, GuidanceHeader);

                // Caption and its button share one row so the button is not visually above the caption.
                Rect captionRow = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
                DraftFromView.text = _drafting ? "Cancel draft" : "Draft from Scene view";
                float draftWidth = Theme.GhostButtonWidth(DraftFromView);
                Theme.GroupCaption(new Rect(captionRow.x, captionRow.y, captionRow.width - draftWidth - 16f, captionRow.height), DescriptionCaption);
                using (new EditorGUI.DisabledScope(settings.CharacterId.Length == 0))
                    if (Theme.GhostButton(new Rect(captionRow.xMax - draftWidth, captionRow.y + 2f, draftWidth, 22f), DraftFromView))
                    { if (_drafting) Session.Cancel(); else Run(DraftContext); }
                if (_refreshDraftField)
                {
                    if (GUI.GetNameOfFocusedControl() == "Convai.SceneTagger.SceneDescription")
                    { GUI.FocusControl(null); EditorGUIUtility.editingTextField = false; }
                    _refreshDraftField = false;
                }
                settings.SceneDescription = SceneTaggerText.Limit(Theme.ProseField(GUIContent.none, settings.SceneDescription, "Convai.SceneTagger.SceneDescription", DescriptionPlaceholder, 64f), SceneTaggerText.SceneContextLimit);
                DrawCounter(GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true)), settings.SceneDescription.Length, SceneTaggerText.SceneContextLimit);
                if (!string.IsNullOrEmpty(_draftFeedback)) Theme.Paragraph(_drafting && string.IsNullOrEmpty(_draftCancellationReason)
                    ? $"Waiting for scene context · {(int)(EditorApplication.timeSinceStartup - _draftStarted)}s. You can cancel and keep your current guidance."
                    : _draftFeedback, Theme.CaptionWrapped);

                GUILayout.Space(8f);
                Theme.GroupCaption(FocusCaption);
                settings.Focus = SceneTaggerText.Limit(Theme.ProseField(GUIContent.none, settings.Focus, "Convai.SceneTagger.Focus", FocusPlaceholder, 64f), SceneTaggerText.FocusLimit);
                DrawCounter(GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true)), settings.Focus.Length, SceneTaggerText.FocusLimit);

                GUILayout.Space(10f);
                using (Theme.PanelScope())
                {
                    GUILayout.Label(OptionsHeader, Theme.CardTitle);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUILayout.VerticalScope())
                        {
                            Theme.GroupCaption(ScopeCaption);
                            int scope = Theme.SegmentedPicker(ScopeOptions, settings.SelectedOnly ? 1 : 0);
                            if (scope >= 0 && settings.SelectedOnly != (scope == 1)) { settings.SelectedOnly = scope == 1; ClearEmptyDiscovery(); }
                        }
                        GUILayout.Space(12f);
                        using (new EditorGUILayout.VerticalScope())
                        {
                            Theme.GroupCaption(QualityCaption);
                            int quality = Theme.SegmentedPicker(QualityOptions, (int)settings.Quality);
                            if (quality >= 0) settings.Quality = (SceneTaggerQuality)quality;
                        }
                    }
                    GUILayout.Space(4f);
                    settings.IncludeTagged = EditorGUILayout.ToggleLeft(IncludeTaggedLabel, settings.IncludeTagged);
                }

            }
        }

        private async Task DraftContext()
        {
            Camera camera = SceneView.lastActiveSceneView?.camera;
            if (!camera) throw new InvalidOperationException("Open a Scene view and frame the environment first.");
            if (string.IsNullOrWhiteSpace(Session.Settings.CharacterId)) throw new InvalidOperationException("Choose a character first.");
            string old = Session.Settings.SceneDescription, character = Session.Settings.CharacterId;
            _drafting = true; _draftFeedback = "Drafting scene context from the current view...";
            _draftCancellationReason = null;
            _draftStarted = _nextDraftRepaint = EditorApplication.timeSinceStartup;
            try
            {
                string description = await Session.DraftContextAsync(token => SceneTaggerCaptureService.CaptureSceneViewAsync(camera, token), _lifetime.Token);
                if (Session.Settings.CharacterId != character || Session.Settings.SceneDescription != old)
                    _draftFeedback = "The guidance or character changed. Your current text was kept.";
                else { Session.Settings.SceneDescription = SceneTaggerText.Normalize(description, SceneTaggerText.SceneContextLimit); _refreshDraftField = true; _draftFeedback = $"Draft ready in {(int)(EditorApplication.timeSinceStartup - _draftStarted)}s — review it before exploring."; }
            }
            catch (OperationCanceledException) { _draftFeedback = _draftCancellationReason ?? "Draft cancelled. Your guidance was kept."; }
            catch (Exception exception)
            {
                _draftFeedback = "Couldn't create a scene-context draft: " + exception.Message;
                Session.ReportError("");
            }
            finally { _drafting = false; }

        }

        #endregion
    }
}
