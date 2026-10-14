using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.CharacterApi;
using Convai.CharacterApi.Contracts.Model;
using Convai.Editor.UI;
using Convai.RestAPI;
using Convai.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.SceneTagging
{
    /// <summary>
    ///     Scene Auto Tagger window shell: hero, Setup/Review mode bar, pinned footer and the shared
    ///     session plumbing. Setup and Review drawing live in the sibling partial files.
    /// </summary>
    internal sealed partial class SceneTaggerWindow : EditorWindow
    {
        private enum Mode { Setup, Review }

        private const string DocsUrl = "https://docs.convai.com/api-docs/plugins-and-integrations/convai-unity-sdk";
        private const float FooterHeight = 64f, Inset = 14f, CardGap = 8f;

        private static readonly GUIContent Title = new("Scene Auto Tagger");
        private static readonly GUIContent Subtitle = new("Turn scene objects into reviewed Convai world objects");
        private static readonly GUIContent[] ModeTabs =
        {
            new("Setup", "Choose the character, guidance, scope and quality."),
            new("Review", "Inspect captures, correct the text, then apply.")
        };
        private static readonly GUIContent ChipReady = new("Vision ready", "Credentials are configured. Captures go to Convai Vision through the bundled Core.");
        private static readonly GUIContent ChipCredentials = new("Credentials needed", "Add an API key in Project Settings > Convai SDK.");
        private static readonly GUIContent ChipCoreUnavailable = new("Core unavailable", "The native analysis library could not be loaded. Check the recovery banner below.");
        private static readonly GUIContent RecheckLabel = new("Recheck");
        private static readonly GUIContent BusyPill = new();
        private static readonly GUIContent SettingsLabel = new("Settings", "Fine-tune discovery, review automation, and applied scene objects.");
        private const string ConsentKey = "Convai.SceneTagger.Consent.scene-auto-tagger-convai-vision-v3";
        private static readonly GUIContent CancelLabel = new("Cancel");
        private static readonly GUIContent ClearSearch = new(ConvaiEditorGlyphs.Affordance.Remove, "Clear search");

        private SceneTaggerSession Session => SceneTaggerSession.Current;
        private SceneTaggerRow _selected;
        private SceneTaggerCaptureService _preview;
        private Texture2D _image;
        private byte[] _imageBytes;
        private Vector2 _scroll;
        private string _search = "", _error = "", _catalogError = "";
        private bool _editingView, _committing, _releaseTextFocus;
        private string _nativeError = "", _emptyDiscovery = "";
        private int _filter;
        private Mode _mode;
        private SceneTaggerCharacterCatalog _catalog;
        private SceneTaggerThumbnailCache _thumbnails;
        private CancellationTokenSource _lifetime;
        private ConvaiRestClient _downloadClient;
        private string SettingsKey => "Convai.SceneTagger." + Hash128.Compute(UnityEngine.Application.dataPath);
        private bool Interactive => !Session.Busy && !_committing && !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("Convai/Scene Auto Tagger", priority = ConvaiEditorMenu.FeatureEditors + 8)]
        internal static void Open() => GetWindow<SceneTaggerWindow>("Scene Auto Tagger");

        private void OnEnable()
        {
            minSize = new Vector2(900, 600);
            _split = EditorPrefs.GetFloat(SettingsKey + ".Split", .36f);
            _lifetime = new CancellationTokenSource();
            _thumbnails = new SceneTaggerThumbnailCache(DownloadAsync);
            _thumbnails.Changed += Repaint;
            Session.Changed += Repaint;
            if (Session.Rows.Count == 0 && EditorPrefs.HasKey(SettingsKey))
                Session.Settings = JsonUtility.FromJson<SceneTaggerSettings>(EditorPrefs.GetString(SettingsKey)) ?? new SceneTaggerSettings();
            _mode = Session.Rows.Count > 0 ? Mode.Review : Mode.Setup;
            EditorApplication.update += UpdatePreview;
            Selection.selectionChanged += ClearEmptyDiscovery;
            RecheckReadiness();
            if (HasCredentials) Run(LoadCharactersAsync, false);
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(SettingsKey, JsonUtility.ToJson(Session.Settings));
            Session.Changed -= Repaint;
            EditorApplication.update -= UpdatePreview;
            Selection.selectionChanged -= ClearEmptyDiscovery;
            _thumbnails.Changed -= Repaint;
            _thumbnails.Dispose();
            _lifetime.Cancel();
            _downloadClient?.Dispose();
            _downloadClient = null;
            DisposePreview();
            if (_image) DestroyImmediate(_image);
        }

        private void OnGUI()
        {
            if (_releaseTextFocus)
            {
                GUI.FocusControl(null);
                EditorGUIUtility.editingTextField = false;
                _releaseTextFocus = false;
            }
            Theme.EnsureStyles();
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), Theme.WindowBg);

            bool hasKey = !string.IsNullOrWhiteSpace(ConvaiSettings.Instance?.ApiKey);
            ChipReady.tooltip = "Analysis policy: Convai Vision – " + Session.Settings.Quality + " image detail";
            bool coreAvailable = string.IsNullOrEmpty(_nativeError);
            Theme.WindowHero(position.width, Title, Subtitle, !hasKey ? ChipCredentials : coreAvailable ? ChipReady : ChipCoreUnavailable,
                hasKey && coreAvailable ? Theme.StatusReady : Theme.StatusWarn, DocsUrl);

            using (new EditorGUI.DisabledScope(!Interactive || _editingView))
            {
                int clicked = Theme.ModeBar(ModeTabs, (int)_mode);
                if (clicked >= 0) RequestMode((Mode)clicked);
            }
            // One 30 px row: caption and Settings share a centre line instead of stacking unevenly.
            Rect statusRow = GUILayoutUtility.GetRect(0f, 30f, GUILayout.ExpandWidth(true));
            float settingsWidth = Theme.GhostButtonWidth(SettingsLabel);
            var settingsRect = new Rect(statusRow.xMax - Inset - settingsWidth, statusRow.y + 4f, settingsWidth, 22f);
            GUI.Label(new Rect(statusRow.x + Inset, statusRow.y + 7f, settingsRect.x - statusRow.x - Inset * 2f, 16f), AgentStatus, Theme.CardSubtitle);
            using (new EditorGUI.DisabledScope(!Interactive || _editingView))
                if (Theme.GhostButton(settingsRect, SettingsLabel)) PopupWindow.Show(settingsRect, new SettingsPopup(this));
            string error = string.IsNullOrEmpty(_error) ? Session.LastError : _error;
            if (!string.IsNullOrEmpty(error)) Theme.ErrorBox("Something went wrong", error);
            if (!HasCredentials || !string.IsNullOrEmpty(_nativeError))
            {
                Theme.WarningBox("Scene analysis is unavailable", !HasCredentials ? "Set a Convai API key to enable scene analysis." : _nativeError, "Open Convai Settings", OpenSettings);
                if (Theme.GhostButtonLayout(RecheckLabel)) RecheckReadiness();
            }

            string reloadWarning = SessionState.GetString("Convai.SceneTagger.ReloadWarning", "");
            if (!string.IsNullOrEmpty(reloadWarning))
                Theme.WarningBox("Previous review cleared", reloadWarning, "Dismiss", () => SessionState.EraseString("Convai.SceneTagger.ReloadWarning"));
            if (_mode == Mode.Review && Session.Rows.Count > 0) DrawReviewSummary();
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            float contentWidth = position.width - (Inset * 2f) - 14f;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(Inset);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(contentWidth)))
                {
                    GUILayout.Space(8f);
                    if (_mode == Mode.Setup) DrawSetup(contentWidth); else DrawReview(contentWidth);
                    GUILayout.Space(12f);
                }
            }
            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        private void RequestMode(Mode mode)
        {
            // Dialogs must run after the current IMGUI layout and scroll scopes have unwound.
            EditorApplication.delayCall += () => { if (this) SetMode(mode); };
        }

        private void SetMode(Mode mode)
        {
            if (mode == _mode) return;
            if (Session.Busy || _committing || _editingView) return;
            if (mode == Mode.Setup && Session.Rows.Count > 0)
            {
                if (!EditorUtility.DisplayDialog("Return to setup?", "The current review, edits, decisions, and checked rows will be discarded. Applied Convai objects stay in the scene.", "Return to setup", "Cancel")) return;
                Session.Clear(); Select(null);
            }
            _mode = mode;
            _releaseTextFocus = true;
            Repaint();
        }

        private void DrawFooter()
        {
            Rect row = GUILayoutUtility.GetRect(0f, FooterHeight, GUILayout.ExpandWidth(true));
            Theme.Fill(row, Theme.HeroBg);
            Theme.DividerLine(new Rect(row.x, row.y, row.width, 1f), Theme.Divider);

            float x = row.x + Inset;
            if (Session.Busy)
            {
                BusyPill.text = Session.StateName;
                float pillWidth = Theme.PillWidth(BusyPill, true);
                Theme.Pill(new Rect(x, row.y + 10f, pillWidth, 20f), BusyPill, Theme.Accent, true);
                GUI.Label(new Rect(x + pillWidth + 10f, row.y + 8f, row.width - pillWidth - 270f, 24f), Session.Progress, Theme.MutedWrapped);
                GUI.Label(new Rect(row.xMax - 250f, row.y + 10f, 155f, 20f), $"{Session.Completed} of {Session.Total} complete", Theme.MicroLabelRight);
                float cancelWidth = Theme.GhostButtonWidth(CancelLabel);
                if (Theme.GhostButton(new Rect(row.xMax - 14f - cancelWidth, row.y + 9f, cancelWidth, 22f), CancelLabel)) Session.Cancel();
                Rect track = new Rect(x, row.yMax - 14f, row.width - Inset * 2f, 3f);
                EditorGUI.DrawRect(track, Theme.Divider);
                track.width *= Session.Total > 0 ? Mathf.Clamp01((float)Session.Completed / Session.Total) : 0;
                EditorGUI.DrawRect(track, Theme.Accent);
                return;
            }
            if (_mode == Mode.Setup)
            {
                // A state pill only when there is a state worth naming; "Ready" beside the caption is noise.
                bool flagged = Session.State == SceneTaggerState.Cancelled || Session.State == SceneTaggerState.NeedsAttention;
                float textX = x;
                if (flagged)
                {
                    BusyPill.text = Session.StateName;
                    float pillWidth = Theme.PillWidth(BusyPill, true);
                    Theme.Pill(new Rect(x, row.y + 22f, pillWidth, 20f), BusyPill,
                        Session.State == SceneTaggerState.NeedsAttention ? Theme.StatusWarn : Theme.StatusIdle, true);
                    textX += pillWidth + 10f;
                }
                GUI.Label(new Rect(textX, row.y + 12f, row.width * 0.68f - textX, 40f), Session.Progress, Theme.CaptionWrapped);
            }

            if (_mode == Mode.Setup) DrawSetupFooter(row); else DrawReviewFooter(row);
        }

        private static bool CompactButton(GUIContent content)
        {
            Rect rect = GUILayoutUtility.GetRect(Theme.GhostButtonWidth(content), 22f, GUILayout.ExpandWidth(false));
            return Theme.GhostButton(rect, content);
        }

        private static bool HasCredentials => !string.IsNullOrWhiteSpace(ConvaiSettings.Instance?.ApiKey);
        private static void OpenSettings() => SettingsService.OpenProjectSettings("Project/Convai SDK");
        private string AgentStatus => !HasCredentials ? "Set a Convai API key to enable scene analysis."
            : string.IsNullOrEmpty(Session.Settings.CharacterId) ? "Choose a Convai character for this tagging run."
            : !string.IsNullOrEmpty(_nativeError) ? "The native analysis library needs attention."
            : $"{Session.Settings.CharacterName} is ready for scene analysis.";
        private void RecheckReadiness()
        {
            try { _ = SceneTaggerCoreAdapter.Instance; _nativeError = ""; }
            catch (Exception exception) { _nativeError = exception.Message; }
            Repaint();
        }

        private string ExploreDisabledReason()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Return to Edit Mode before exploring.";
            if (!Interactive) return "Cancel or wait for the current operation to finish.";
            if (_editingView) return "Finish or cancel Edit View before exploring.";
            if (!HasCredentials) return "Set a Convai API key to enable scene analysis.";
            if (string.IsNullOrWhiteSpace(Session.Settings.CharacterId)) return "Choose a Convai character for this tagging run.";
            if (!string.IsNullOrEmpty(_nativeError)) return _nativeError;
            if (PrefabStageUtility.GetCurrentPrefabStage() != null || !SceneManager.GetActiveScene().isLoaded)
                return "Open an editable scene first.";
            if (Session.Settings.SelectedOnly && Selection.gameObjects.Length == 0) return "Select one or more objects, or switch to Current Scene.";
            return null;
        }

        private async Task ExploreAsync()
        {
            if (ExploreDisabledReason() is string reason) throw new InvalidOperationException(reason);
            if (Session.Rows.Count > 0 && !EditorUtility.DisplayDialog("Replace current review?", "The current review, edits, decisions, and checked rows will be replaced. Applied Convai objects stay in the scene.", "Replace", "Cancel")) return;
            DisposePreview(); _selected = null; _emptyDiscovery = "";
            Session.Discover();
            if (Session.Rows.Count == 0)
            {
                _emptyDiscovery = Session.Settings.SelectedOnly ? "No selected objects could be scanned" : "No eligible objects found";
                return;
            }
            SetMode(Mode.Review);
            await Session.RunAsync(Session.Rows.ToArray(), showSummary: true);
        }

        #region Character catalog and downloads

        private SceneTaggerCharacterCatalog Catalog => _catalog ??= new SceneTaggerCharacterCatalog(ListCharactersAsync);

        private static Task<PageCharacterListItem> ListCharactersAsync(int limit, int offset, CancellationToken token)
        {
            var settings = ConvaiSettings.Instance;
            if (string.IsNullOrWhiteSpace(settings?.ApiKey))
                throw new InvalidOperationException("Configure credentials in Project Settings > Convai SDK first.");
            var options = ConvaiRestOptionsFactory.CreateForAuthoring().CharacterApiV2
                ?? (settings.ApiEnvironment == ConvaiApiEnvironment.Beta ? ConvaiCharacterApiOptions.Preview(settings.ApiKey)
                : settings.ApiEnvironment == ConvaiApiEnvironment.Custom && !string.IsNullOrWhiteSpace(settings.CharacterApiBaseUrlOverride)
                    ? ConvaiCharacterApiOptions.Custom(new Uri(settings.CharacterApiBaseUrlOverride), settings.ApiKey)
                    : ConvaiCharacterApiOptions.Production(settings.ApiKey));
            return ListWithClientAsync(options, limit, offset, token);
        }

        private static async Task<PageCharacterListItem> ListWithClientAsync(ConvaiCharacterApiOptions options, int limit, int offset, CancellationToken token)
        {
            using var client = new ConvaiCharacterApiClient(options);
            return await client.Characters.ListAsync(limit, offset, token);
        }

        private Task<byte[]> DownloadAsync(string url, CancellationToken token)
        {
            _downloadClient ??= new ConvaiRestClient(ConvaiRestOptionsFactory.Create(ConvaiSettings.Instance?.ApiKey));
            return _downloadClient.DownloadFileAsync(url, token);
        }

        private async Task LoadCharactersAsync()
        {
            _catalogError = "";
            try { await Catalog.LoadMoreAsync(_lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _catalogError = "Could not load account characters. Check the configured credentials and character API endpoint.";
            }
            finally { if (this) Repaint(); }
        }

        #endregion

        #region Shared session operations

        private void ReplaceWithSelectedChildren()
        {
            var parent = _selected?.Target;
            GameObject[] children = Selection.gameObjects;
            if (!parent || children.Length == 0 || children.Any(child => child == parent || !child.transform.IsChildOf(parent.transform)))
                throw new InvalidOperationException("Select one or more children of the review object in the Hierarchy first.");
            var settings = Session.Settings.Copy();
            settings.SelectedOnly = true;
            var replacements = SceneTaggerDiscovery.Discover(settings);
            if (replacements.Count == 0) throw new InvalidOperationException("The selected children have no supported capture geometry.");
            SceneTaggerRow previous = _selected;
            DisposePreview();
            Session.Rows.Remove(previous);
            foreach (var row in replacements) if (!Session.Rows.Any(existing => existing.Target == row.Target)) Session.Rows.Add(row);
            Select(null);
        }

        private async Task CommitView(bool analyze)
        {
            _committing = true;
            SceneTaggerRow row = _selected;
            try
            {
                if (!SceneTaggerDiscovery.Validate(row)) throw new InvalidOperationException("Object geometry changed. Add the object again before saving this view.");
                SceneTaggerEvidence evidence = await _preview.CommitAsync(_lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                Session.ReplaceEvidence(row, evidence);
                row.Checked = true;
                row.Source = SceneTaggerSource.AdjustedView;
                DisposePreview();
                if (analyze) await Session.RunAsync(new[] { row });
            }
            finally { _committing = false; }
        }

        private void UpdatePreview()
        {
            if (_drafting && EditorApplication.timeSinceStartup >= _nextDraftRepaint)
            { _nextDraftRepaint = EditorApplication.timeSinceStartup + .5; Repaint(); }
            if (!_editingView || _preview == null || _committing) return;
            if (_selected == null || !_selected.IsCurrent || !Session.Rows.Contains(_selected) || _selected.Target.transform.localToWorldMatrix != _selected.WorldMatrix || _selected.Renderers.Any(r => !r)) { DisposePreview(); _error = "Edit View stopped because the object or scene changed."; return; }
            Act(_preview.Render);
            Repaint();
        }

        private void ClearEmptyDiscovery() { _emptyDiscovery = ""; Session.DiscoveryReport = null; Repaint(); }

        private string _selectedSearchSnapshot;

        private void Select(SceneTaggerRow row)
        {
            if (row == _selected) return;
            _releaseTextFocus = true;
            DisposePreview(); _selected = row; _selectedSearchSnapshot = row == null ? null : SceneTaggerReview.SearchText(row); _error = ""; Session.ReportError("");
        }
        private void DisposePreview() { _editingView = false; _preview?.Dispose(); _preview = null; }
        private void Act(Action action) { try { _error = ""; action(); } catch (Exception exception) { _error = exception.Message; } }

        private async void Run(Func<Task> action, bool uploads = true)
        {
            // Release IMGUI's text buffer before analysis can replace a suggestion or clear its note.
            if (uploads) _releaseTextFocus = true;
            if (uploads && !EditorPrefs.GetBool(ConsentKey, false))
            {
                if (!EditorUtility.DisplayDialog("Allow AI scene analysis?", "The Auto Tagger sends captured images and optional guidance to the Convai character you select for this run.", "Accept", "Decline")) return;
                EditorPrefs.SetBool(ConsentKey, true);
            }
            try { _error = ""; Session.ReportError(""); await action(); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _error = exception.Message; }
            if (this) Repaint();
        }

        #endregion
    }

    internal sealed class SceneTaggerImageWindow : EditorWindow
    {
        private Texture2D _image;
        internal static void Show(byte[] png)
        {
            var window = CreateInstance<SceneTaggerImageWindow>();
            window.titleContent = new GUIContent("Retained capture");
            window._image = new Texture2D(2, 2);
            window._image.LoadImage(png);
            window.position = new Rect(100, 100, window._image.width, window._image.height);
            window.ShowUtility();
        }
        private void OnGUI() { if (_image) GUI.DrawTexture(new Rect(0, 0, position.width, position.height), _image, ScaleMode.ScaleToFit); }
        private void OnDisable() { if (_image) DestroyImmediate(_image); }
    }
}
