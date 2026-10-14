using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime;
using Convai.Runtime.SceneMetadata;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Editor.SceneTagging
{
    [InitializeOnLoad]
    internal sealed class SceneTaggerSession
    {
        internal static readonly SceneTaggerSession Current = new();
        internal readonly List<SceneTaggerRow> Rows = new();
        private readonly List<SceneTaggerRow> _removed = new();
        private readonly Dictionary<string, (string ownerId, string name, string description)> _applied = new();
        internal readonly List<SceneTaggerRow> AddedRows = new();
        internal int AddedCount, RestoredCount, SkippedCount;
        internal void RemoveRows(IEnumerable<SceneTaggerRow> rows)
        {
            if (Busy) return;
            foreach (var row in rows.ToArray())
                if (row.Status != SceneTaggerStatus.Applied && Rows.Remove(row)) { row.Checked = false; row.Evidence = row.RejectedEvidence = null; row.PreviousUsable = null; row.Source = SceneTaggerSource.RestoredReview; _removed.Add(row); }
            Notify();
        }
        internal SceneTaggerSettings Settings = new();
        internal bool Busy { get; private set; }
        internal bool ApplyStarted { get; private set; }
        internal string Progress { get; private set; } = "Choose a character and discover scene objects.";
        internal SceneTaggerState State { get; private set; }
        internal int Completed { get; private set; }
        internal int Total { get; private set; }
        internal string LastError { get; private set; } = "";
        internal string LastApplySummary { get; private set; } = "";
        internal SceneTaggerSettings RunSettings { get; private set; }
        internal SceneTaggerDiscoveryReport DiscoveryReport;
        internal bool ShowSummary;
        private bool _recapturing, _reconcileUndoPending;
        internal string StateName => State switch
        {
            SceneTaggerState.Idle => "Ready", SceneTaggerState.ReviewReady => "Review ready",
            SceneTaggerState.NeedsAttention => "Needs attention", _ => State.ToString()
        };
        internal void ReportError(string error) { LastError = error; Notify(); }
        internal event Action Changed;
        private CancellationTokenSource _cancellation;
        private int _generation;
        private const long EvidenceBudget = 256L * 1024 * 1024;
        private readonly Dictionary<string, CacheEntry> _cache = new();
        internal Func<(string key, string url)> Credentials = () =>
        {
            var options = ConvaiRestOptionsFactory.Create();
            return (ConvaiSettings.Instance?.ApiKey, options.Environment == Convai.RestAPI.ConvaiEnvironment.Production ? options.ProductionBaseUrl : options.BetaBaseUrl);
        };
        private const string CachePath = "Library/ConvaiSceneTagger/cache.json";
        internal Func<SceneTaggerRequest, CancellationToken, Task<SceneTaggerResponse>> Analyze =
            (request, token) => SceneTaggerCoreAdapter.Instance.AnalyzeAsync(request, token);

        static SceneTaggerSession()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (Current.Rows.Count > 0)
                {
                    const string warning = "Scene Auto Tagger review was cleared by a script reload or Play Mode transition. Unapplied text and images were not saved. Applied scene metadata is unchanged.";
                    SessionState.SetString("Convai.SceneTagger.ReloadWarning", warning);
                    Debug.LogWarning(warning);
                }
                Current.Cancel();
            };
            Undo.undoRedoPerformed += () => Current.ReconcileUndo();
            EditorSceneManager.activeSceneChangedInEditMode += (_, _) => Current.Clear();
            EditorSceneManager.sceneClosed += _ => Current.Cancel();
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingEditMode) Current.Cancel(); };
        }

        internal void Notify() => Changed?.Invoke();
        internal void Cancel()
        {
            if (!Busy) return;
            _generation++;
            _cancellation?.Cancel();
            State = SceneTaggerState.Cancelled;
            Progress = _recapturing ? "Recapture cancelled. The previous image, suggestion, and review state were kept."
                : "Exploration cancelled. Completed suggestions remain available for review.";
            if (SceneTaggerCoreAdapter.HasActiveCalls) Progress += " Waiting for active requests to finish.";
            Notify();
        }
        internal void Clear()
        {
            Cancel();
            Rows.Clear(); _removed.Clear(); _applied.Clear(); AddedRows.Clear();
            RunSettings = null; ShowSummary = false; LastError = LastApplySummary = "";
            DiscoveryReport = null;
            State = SceneTaggerState.Idle; Completed = Total = 0;
            Progress = "Choose a character and explore scene objects.";
            ApplyStarted = false;
            Notify();
        }
        internal void Discover(bool appendSelection = false)
        {
            if (Busy) return;
            State = SceneTaggerState.Discovering; LastError = "";
            Progress = appendSelection ? "Preparing selected objects..." : "Filtering significant scene objects...";
            Notify();
            var settings = (appendSelection ? RunSettings ?? Settings : Settings).Copy();
            if (!appendSelection) { RunSettings = settings.Copy(); ShowSummary = false; LastApplySummary = ""; }
            if (appendSelection) settings.SelectedOnly = true;
            List<SceneTaggerRow> discovered = SceneTaggerDiscovery.Discover(settings, out DiscoveryReport);
            if (!appendSelection) { Rows.Clear(); _removed.Clear(); _applied.Clear(); ApplyStarted = false; }
            AddedRows.Clear(); AddedCount = RestoredCount = 0;
            SkippedCount = appendSelection ? Math.Max(0, Selection.gameObjects.Length - discovered.Count) : 0;
            foreach (SceneTaggerRow row in discovered)
            {
                if (Rows.Any(r => r.Target == row.Target)) { SkippedCount++; continue; }
                SceneTaggerRow restored = _removed.FirstOrDefault(r => r.Target == row.Target && r.Fingerprint == row.Fingerprint && SceneTaggerDiscovery.Validate(r));
                if (restored != null) { _removed.Remove(restored); Rows.Add(restored); AddedRows.Add(restored); RestoredCount++; }
                else { Rows.Add(row); AddedRows.Add(row); AddedCount++; }
            }
            Progress = Rows.Count == 0 ? DiscoveryReport.EmptyMessage : $"{Rows.Count} objects ready for capture.";
            State = SceneTaggerState.Idle;
            Notify();
        }

        internal async Task RunAsync(IEnumerable<SceneTaggerRow> requested, bool recapture = false, bool showSummary = false)
        {
            if (Busy) return;
            var targets = requested.Where(r => r.IsCurrent && Rows.Contains(r) && r.Status != SceneTaggerStatus.Applied).Distinct().ToList();
            if (targets.Count == 0) return;
            if (string.IsNullOrWhiteSpace((RunSettings ?? Settings).CharacterId)) throw new InvalidOperationException("Select a Convai character first.");
            var credentials = Credentials();
            string apiKey = credentials.key;
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Configure a Convai API key in project settings first.");
            SceneTaggerCoreAdapter core = SceneTaggerCoreAdapter.Instance;
            RunSettings ??= Settings.Copy();
            SceneTaggerSettings settings = RunSettings.Copy();
            string baseUrl = credentials.url;
            settings.EndpointIdentity = baseUrl;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            int generation = ++_generation;
            Busy = true; _recapturing = recapture; LastError = ""; ShowSummary = false;
            Completed = 0; Total = targets.Count; State = SceneTaggerState.Capturing;
            LoadCache(settings);
            try
            {
                var representatives = new Dictionary<string, SceneTaggerRow>(StringComparer.Ordinal);
                var pending = new List<SceneTaggerRow>();
                foreach (SceneTaggerRow row in targets)
                {
                    Completed = targets.IndexOf(row);
                    token.ThrowIfCancellationRequested();
                    if (!SceneTaggerDiscovery.Validate(row)) { row.Error = "Object geometry changed. Discover or add the object again."; row.Status = SceneTaggerStatus.Attention; continue; }
                    if (!recapture && row.Evidence == null && TryCache(row, settings)) continue;
                    if (row.Evidence == null || recapture)
                    {
                        SceneTaggerEvidence previous = row.Evidence;
                        SceneTaggerStatus oldStatus = row.Status;
                        if (!row.PendingAnalysis && (oldStatus == SceneTaggerStatus.Ready || oldStatus == SceneTaggerStatus.Applied))
                            row.PreviousUsable = SceneTaggerReviewBackup.From(row);
                        try
                        {
                            Progress = "Capturing " + row.Target.name;
                            row.Status = SceneTaggerStatus.Capturing;
                            Notify();
                            if (string.IsNullOrWhiteSpace(row.Note) && representatives.TryGetValue(row.Fingerprint, out SceneTaggerRow representative))
                            {
                                ReplaceEvidence(row, representative.Evidence);
                                row.Source = SceneTaggerSource.IdenticalGeometry;
                            }
                            else
                            {
                                using var capture = new SceneTaggerCaptureService(row, 384);
                                SceneTaggerEvidence evidence = await capture.CaptureAsync(settings, token, targets.Count == 1 ? 1u : recapture ? 2u : 0u);
                                token.ThrowIfCancellationRequested();
                                ReplaceEvidence(row, evidence);
                                representatives[row.Fingerprint] = row;
                            }
                            if (settings.DebugCaptures)
                            {
                                // Optional export must not discard an otherwise usable capture.
                                try { SaveDebug(row); }
                                catch (IOException) { row.Error = "Captured successfully, but the debug image could not be saved."; }
                                catch (UnauthorizedAccessException) { row.Error = "Captured successfully, but the debug folder is not writable."; }
                            }
                        }
                        catch (OperationCanceledException) { row.Evidence = previous; row.Status = oldStatus; throw; }
                        catch (Exception exception)
                        {
                            row.Evidence = previous;
                            row.Status = previous != null ? oldStatus : SceneTaggerStatus.Attention;
                            row.Error = exception.Message;
                            if (exception is SceneTaggerEvidenceBudgetException) break;
                            continue;
                        }
                    }
                    if (!string.IsNullOrEmpty(row.Evidence?.Failure) && !(row.Evidence.LowInformation && row.AllowLowInformation))
                    { row.Error = row.Evidence.Failure; row.Status = row.Evidence.LowInformation ? SceneTaggerStatus.Skipped : SceneTaggerStatus.Attention; continue; }
                    pending.Add(row);
                }
                token.ThrowIfCancellationRequested();
                State = SceneTaggerState.Analyzing; Completed = 0;
                Progress = "Analyzing scene objects in parallel...";
                Notify();
                // One request per exact representative; notes always keep their own target identity.
                var pendingStamps = pending.ToDictionary(r => r.Id, r => (r.ImageRevision, r.NoteRevision, r.EditRevision));
                var completed = new HashSet<SceneTaggerRow>();
                var groups = pending.GroupBy(r => r.EditRevision == 0 && string.IsNullOrWhiteSpace(r.Note) ? r.Fingerprint + ":" + EvidenceKey(r.Evidence) : r.Id).ToArray();
                SceneTaggerRow[] unique = groups.Select(g => g.First()).ToArray();
                int capacity = settings.Grid * settings.Grid;
                using var slots = new SemaphoreSlim(Mathf.Clamp(settings.Concurrency, 1, 6));
                var jobs = new List<Task>();
                for (int start = 0; start < unique.Length; start += capacity)
                {
                    SceneTaggerRow[] batch = unique.Skip(start).Take(capacity).ToArray();
                    jobs.Add(ProcessWithSlot(batch));
                }
                await Task.WhenAll(jobs);
                token.ThrowIfCancellationRequested();
                foreach (var group in groups)
                {
                    SceneTaggerRow representative = group.First();
                    foreach (SceneTaggerRow row in group.Skip(1))
                    {
                        if (!IsCurrentResult(row, pendingStamps[row.Id])) continue;
                        if (!completed.Contains(representative))
                        {
                            row.Error = representative.Error;
                            row.Status = StatusAfterFailedAnalysis(row.Status, row.PendingAnalysis);
                            continue;
                        }
                        if (!SceneTaggerDiscovery.Validate(row))
                        {
                            row.Error = "Object geometry changed during analysis. Discover or add the object again.";
                            row.Status = SceneTaggerStatus.Attention;
                            row.PreviousUsable = null;
                            continue;
                        }
                        CopyProposal(representative, row);
                        completed.Add(row);
                    }
                }
                ReconcileVisualDuplicates(core, completed);
                EnsureUniqueNames();
                SaveCache(settings);
                Completed = Total; State = Rows.Any(SceneTaggerReview.Attention) ? SceneTaggerState.NeedsAttention : SceneTaggerState.ReviewReady;
                ShowSummary = showSummary;
                Progress = $"Review ready: {Rows.Count(SceneTaggerReview.Ready)} ready, {Rows.Count(SceneTaggerReview.Attention)} need attention.";

                async Task ProcessWithSlot(SceneTaggerRow[] batch)
                {
                    await slots.WaitAsync(token);
                    try { await AnalyzeBatchAsync(batch, settings, apiKey, baseUrl, generation, token, true, completed); }
                    finally { slots.Release(); Completed += batch.Length; Notify(); }
                }
            }
            catch (OperationCanceledException) { if (State == SceneTaggerState.Cancelled) ShowSummary = showSummary && Rows.Count > 0; }
            catch (Exception exception) { LastError = exception.Message; State = SceneTaggerState.NeedsAttention; ShowSummary = showSummary; }
            finally
            {
                foreach (SceneTaggerRow row in targets)
                    if (row.PendingAnalysis && row.Status != SceneTaggerStatus.Ready && row.Status != SceneTaggerStatus.Applied) RestorePreviousUsable(row);
                Busy = false;
                if (_reconcileUndoPending) ReconcileUndo();
                Notify();
            }
        }

        internal async Task<string> DraftContextAsync(Func<CancellationToken, Task<byte[]>> capture, CancellationToken lifetime)
        {
            if (Busy) throw new InvalidOperationException("Wait for the current analysis to finish.");
            SceneTaggerSettings settings = Settings.Copy();
            var credentials = Credentials();
            if (string.IsNullOrWhiteSpace(settings.CharacterId) || string.IsNullOrWhiteSpace(credentials.key))
                throw new InvalidOperationException("Choose a character and configure project credentials first.");
            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            CancellationToken token = _cancellation.Token;
            int generation = ++_generation;
            Busy = true; LastError = ""; Completed = 0; Total = 1; State = SceneTaggerState.Analyzing; Progress = "Drafting scene context"; Notify();
            try
            {
                byte[] png = await capture(token);
                token.ThrowIfCancellationRequested();
                if (settings.DebugCaptures)
                {
                    Directory.CreateDirectory("Library/ConvaiSceneTagger");
                    File.WriteAllBytes("Library/ConvaiSceneTagger/scene-context.png", png);
                }
                var request = new SceneTaggerRequest { ApiKey = credentials.key, BaseUrl = credentials.url,
                    CharacterId = settings.CharacterId, SceneContext = true, Png = png,
                    Cells = new[] { new SceneTaggerCell { Id = "scene-context" } } };
                SceneTaggerResponse response = await RequestWithRetriesAsync(request, token);
                token.ThrowIfCancellationRequested();
                if (generation != _generation) throw new OperationCanceledException();
                if (response.HttpStatus < 200 || response.HttpStatus >= 300) throw new InvalidOperationException(response.Error ?? "Scene context request failed.");
                var item = response.Objects.SingleOrDefault(o => o.Index == 1 && o.Id == "scene-context");
                if (item == null || string.IsNullOrWhiteSpace(item.Description)) throw new InvalidOperationException("Vision did not return a matching scene description.");
                Completed = 1; State = SceneTaggerState.Idle;
                Progress = "Scene context drafted. Review it before analyzing objects.";
                return item.Description;
            }
            catch (OperationCanceledException)
            {
                State = SceneTaggerState.Cancelled; Completed = Total = 0;
                Progress = "Scene context draft cancelled. Existing guidance was kept.";
                throw;
            }
            catch (Exception)
            {
                State = SceneTaggerState.NeedsAttention; Completed = Total = 0;
                Progress = "Scene context draft failed. Existing guidance was kept; see the draft feedback.";
                throw;
            }
            finally { Busy = false; if (_reconcileUndoPending) ReconcileUndo(); Notify(); }
        }

        internal void ReplaceEvidence(SceneTaggerRow row, SceneTaggerEvidence evidence)
        {
            SceneTaggerReviewBackup backup = !row.PendingAnalysis &&
                (row.Status == SceneTaggerStatus.Ready || row.Status == SceneTaggerStatus.Applied) ? SceneTaggerReviewBackup.From(row) : row.PreviousUsable;
            long used = Rows.Where(r => r != row).SelectMany(r => new[] { r.Evidence, r.PreviousUsable?.Evidence, r.RejectedEvidence })
                .Concat(new[] { evidence, backup?.Evidence }).Where(e => e != null).Distinct().Sum(e => (long)e.Png.Length);
            if (used > EvidenceBudget) throw new SceneTaggerEvidenceBudgetException();
            row.PreviousUsable = backup;
            row.RejectedEvidence = null;
            row.Evidence = evidence;
            row.ImageRevision++;
            row.PendingAnalysis = true;
            row.Error = "";
            row.Source = SceneTaggerSource.Capture;
            row.Status = SceneTaggerStatus.Candidate;
            Notify();
        }

        internal static bool RestorePreviousUsable(SceneTaggerRow row)
        {
            SceneTaggerReviewBackup backup = row.PreviousUsable;
            if (backup == null || row.EditRevision != backup.EditRevision || row.NoteRevision != backup.NoteRevision) return false;
            if (row.Evidence != backup.Evidence) row.RejectedEvidence = row.Evidence;
            row.Evidence = backup.Evidence; row.Name = backup.Name; row.Description = backup.Description;
            row.Source = backup.Source; row.Confidence = backup.Confidence; row.Status = backup.Status;
            row.ImageRevision++; row.PendingAnalysis = false; row.PreviousUsable = null;
            row.Error = "The retake did not complete. The previous result and image were kept. " + row.Error;
            return true;
        }

        private async Task AnalyzeBatchAsync(SceneTaggerRow[] targets, SceneTaggerSettings settings, string apiKey, string baseUrl, int generation, CancellationToken token, bool recover, HashSet<SceneTaggerRow> completed)
        {
            var rows = targets.ToList();
            Vector3 center = targets.Aggregate(Vector3.zero, (sum, r) => sum + r.Bounds.center) / targets.Length;
            rows.AddRange(Rows.Where(r => r.Included && r.Evidence != null && string.IsNullOrEmpty(r.Evidence.Failure) && r.IsCurrent && !rows.Contains(r) && SceneTaggerDiscovery.Validate(r))
                .OrderBy(r => (r.Bounds.center - center).sqrMagnitude).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(settings.Grid * settings.Grid - rows.Count));
            var stamps = targets.ToDictionary(r => r.Id, r => (r.ImageRevision, r.NoteRevision, r.EditRevision));
            var oldStatuses = targets.ToDictionary(r => r.Id, r => r.Status);
            var request = new SceneTaggerRequest
            {
                ApiKey = apiKey, BaseUrl = baseUrl, CharacterId = settings.CharacterId, SceneDescription = settings.SceneDescription, Focus = settings.Focus,
                Png = ComposeSheet(rows, settings), Cells = rows.Select((r, i) => new SceneTaggerCell
                { Id = r.Id, ContextOnly = i >= targets.Length, Name = r.Name, Description = r.Description, Note = i < targets.Length ? r.Note : "" }).ToArray()
            };
            foreach (SceneTaggerRow row in targets) { row.Status = SceneTaggerStatus.Analyzing; row.Error = ""; }
            Notify();
            try
            {
                SceneTaggerResponse response = await RequestWithRetriesAsync(request, token, retryServer: false);
                token.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                if (recover && (response.HttpStatus >= 500 || response.HttpStatus == 0 && response.CanRetryTransport))
                {
                    Progress = targets.Length > 1 ? "Vision request failed. Retrying smaller groups once..." : "Vision request failed. Retrying once...";
                    Notify();
                    // One server/transport retry per target. Split large failures to reduce response
                    // generation time, and keep retries sequential within the existing concurrency slot.
                    int size = Math.Max(1, (targets.Length + 1) / 2);
                    for (int start = 0; start < targets.Length; start += size)
                        await AnalyzeBatchAsync(targets.Skip(start).Take(size).ToArray(), settings, apiKey, baseUrl, generation, token, false, completed);
                    return;
                }
                var missing = new List<SceneTaggerRow>();
                for (int i = 0; i < targets.Length; i++)
                {
                    SceneTaggerRow row = targets[i];
                    if (!IsCurrentResult(row, stamps[row.Id])) continue;
                    if (!SceneTaggerDiscovery.Validate(row))
                    {
                        row.Error = "Object geometry changed during analysis. Discover or add the object again.";
                        row.Status = SceneTaggerStatus.Attention;
                        row.PreviousUsable = null;
                        continue;
                    }
                    var matches = response.Objects.Where(o => o.Index == i + 1).ToArray();
                    if (response.HttpStatus < 200 || response.HttpStatus >= 300 || matches.Length != 1 || matches[0].Id != row.Id || response.Objects.Count(o => o.Id == row.Id) != 1 || string.IsNullOrWhiteSpace(matches[0].Name) || string.IsNullOrWhiteSpace(matches[0].Description) || !float.IsFinite(matches[0].Confidence) || matches[0].Confidence < 0 || matches[0].Confidence > 1)
                    {
                        row.Error = response.Error ?? "The response did not match this object.";
                        row.Status = StatusAfterFailedAnalysis(oldStatuses[row.Id], row.PendingAnalysis);
                        if (response.HttpStatus >= 200 && response.HttpStatus < 300) missing.Add(row);
                        continue;
                    }
                    SceneTaggerObjectResult result = matches[0];
                    if (!string.IsNullOrWhiteSpace(row.Note) && SceneTaggerText.Normalize(result.Name, ConvaiObjectMetadata.MaxNameLength) == row.Name && SceneTaggerText.Normalize(result.Description, ConvaiObjectMetadata.MaxDescriptionLength) == row.Description)
                    {
                        row.Error = "The correction did not change the proposal. Refine or clear the note before Apply.";
                        row.Status = StatusAfterFailedAnalysis(oldStatuses[row.Id], row.PendingAnalysis);
                        continue;
                    }
                    row.Name = SceneTaggerText.Normalize(result.Name, ConvaiObjectMetadata.MaxNameLength);
                    row.Description = SceneTaggerText.Normalize(result.Description, ConvaiObjectMetadata.MaxDescriptionLength);
                    row.Confidence = float.IsNaN(result.Confidence) ? 0 : Mathf.Clamp01(result.Confidence);
                    row.Note = "";
                    row.PendingAnalysis = false;
                    row.PreviousUsable = null;
                    row.RejectedEvidence = null;
                    row.Status = SceneTaggerStatus.Ready;
                    row.Error = "";
                    completed.Add(row);
                    if (settings.CacheEnabled) _cache[CacheKey(row, settings)] = new CacheEntry { Name = row.Name, Description = row.Description, Confidence = row.Confidence };
                }
                if (recover && missing.Count > 0) await AnalyzeBatchAsync(missing.ToArray(), settings, apiKey, baseUrl, generation, token, false, completed);
            }
            catch (OperationCanceledException)
            {
                foreach (SceneTaggerRow row in targets) if (row.Status == SceneTaggerStatus.Analyzing) row.Status = oldStatuses[row.Id];
                throw;
            }
            catch (Exception exception)
            {
                foreach (SceneTaggerRow row in targets) if (IsCurrentResult(row, stamps[row.Id]))
                { row.Error = exception.Message; row.Status = StatusAfterFailedAnalysis(oldStatuses[row.Id], row.PendingAnalysis); }
            }
            finally
            {
                foreach (SceneTaggerRow row in targets) if (row.Status == SceneTaggerStatus.Analyzing) row.Status = oldStatuses[row.Id];
                Notify();
            }
        }

        private static SceneTaggerStatus StatusAfterFailedAnalysis(SceneTaggerStatus previous, bool pendingImage) =>
            !pendingImage && (previous == SceneTaggerStatus.Ready || previous == SceneTaggerStatus.Applied) ? previous : SceneTaggerStatus.Attention;

        internal async Task<SceneTaggerResponse> RequestWithRetriesAsync(SceneTaggerRequest request, CancellationToken token, bool retryServer = true)
        {
            int throttles = 0, serverRetries = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                SceneTaggerResponse response = await Analyze(request, token);
                if (response.HttpStatus == 429 && throttles++ < 3)
                { Progress = "Vision is rate limited. Retrying in 20 seconds..."; Notify(); await Task.Delay(TimeSpan.FromSeconds(20), token); continue; }
                if (retryServer && (response.HttpStatus == 0 && response.CanRetryTransport || response.HttpStatus >= 500) && serverRetries++ < 1)
                { Progress = response.HttpStatus == 0 ? "Vision connection failed. Retrying once..." : $"Vision returned HTTP {response.HttpStatus}. Retrying once..."; Notify(); continue; }
                return response;
            }
        }
        internal static bool IsCurrentResult(SceneTaggerRow row, (int image, int note, int edit) stamp) => row.IsCurrent &&
            row.ImageRevision == stamp.image && row.NoteRevision == stamp.note && row.EditRevision == stamp.edit;

        internal static byte[] ComposeSheet(IReadOnlyList<SceneTaggerRow> rows, SceneTaggerSettings settings)
        {
            int side = settings.Grid * settings.CellSize;
            var sheet = new Texture2D(side, side, TextureFormat.RGBA32, false);
            var clear = new Color32[side * side];
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(30, 30, 30, 255);
            sheet.SetPixels32(clear);
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    try
                    {
                        if (!source.LoadImage(rows[i].Evidence.Png)) throw new InvalidDataException("Retained image could not be decoded.");
                        int x = i % settings.Grid * settings.CellSize, y = side - (i / settings.Grid + 1) * settings.CellSize;
                        Color32[] cell = ResizePixels(source, settings.CellSize);
                        DrawNumber(cell, settings.CellSize, i + 1);
                        sheet.SetPixels32(x, y, settings.CellSize, settings.CellSize, cell);
                    }
                    finally { Object.DestroyImmediate(source); }
                }
                sheet.Apply();
                return sheet.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(sheet); }
        }
        private static Color32[] ResizePixels(Texture2D source, int size)
        {
            var pixels = new Color32[size * size];
            Color32[] input = source.GetPixels32();
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float sx = Mathf.Clamp((x + .5f) * source.width / size - .5f, 0, source.width - 1);
                float sy = Mathf.Clamp((y + .5f) * source.height / size - .5f, 0, source.height - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Mathf.Min(x0 + 1, source.width - 1), y1 = Mathf.Min(y0 + 1, source.height - 1);
                pixels[y * size + x] = Color32.Lerp(Color32.Lerp(input[y0 * source.width + x0], input[y0 * source.width + x1], sx - x0),
                    Color32.Lerp(input[y1 * source.width + x0], input[y1 * source.width + x1], sx - x0), sy - y0);
            }
            return pixels;
        }
        private static readonly string[] Digits = { "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001", "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111" };
        private static void DrawNumber(Color32[] pixels, int side, int number)
        {
            string text = number.ToString();
            for (int y = side - 32; y < side; y++) for (int x = 0; x < text.Length * 20 + 12; x++) pixels[y * side + x] = new Color32(0, 0, 0, 255);
            for (int d = 0; d < text.Length; d++) for (int y = 0; y < 5; y++) for (int x = 0; x < 3; x++)
                if (Digits[text[d] - '0'][y * 3 + x] == '1')
                    for (int dy = 0; dy < 4; dy++) for (int dx = 0; dx < 4; dx++) pixels[(side - 6 - y * 4 - dy) * side + 6 + d * 20 + x * 4 + dx] = new Color32(255, 255, 255, 255);
        }

        private void ReconcileVisualDuplicates(SceneTaggerCoreAdapter core, HashSet<SceneTaggerRow> completed)
        {
            var representatives = new List<SceneTaggerRow>();
            foreach (SceneTaggerRow row in Rows.Where(r => r.Status == SceneTaggerStatus.Ready && !r.PendingAnalysis && r.Evidence?.DescriptorValid == true && string.IsNullOrEmpty(r.Note) && SceneTaggerDiscovery.Validate(r)).OrderByDescending(r => r.Confidence))
            {
                SceneTaggerRow match = representatives.FirstOrDefault(r => r.Evidence.Policy == row.Evidence.Policy && r.Evidence.BroadFace == row.Evidence.BroadFace && core.Equivalent(r.Evidence.Descriptor, row.Evidence.Descriptor));
                if (match == null) representatives.Add(row);
                else if (completed.Contains(row) && row.EditRevision == 0) { row.ProofGroup = match.ProofGroup; CopyProposal(match, row); }
            }
        }
        private static void CopyProposal(SceneTaggerRow source, SceneTaggerRow target)
        {
            if (target.EditRevision != 0 || !string.IsNullOrEmpty(target.Note) || source.Status != SceneTaggerStatus.Ready) return;
            target.Name = source.Name;
            target.Description = source.Description;
            target.Confidence = source.Confidence;
            target.PendingAnalysis = source.PendingAnalysis;
            if (!target.PendingAnalysis) { target.PreviousUsable = null; target.RejectedEvidence = null; }
            target.Status = source.Status;
            target.Error = source.Error;
        }
        internal List<List<SceneTaggerRow>> Groups(IEnumerable<SceneTaggerRow> rows)
        {
            var result = new List<List<SceneTaggerRow>>();
            foreach (SceneTaggerRow row in rows.Where(r => r.IsCurrent))
            {
                List<SceneTaggerRow> group = Settings.Grouping == SceneTaggerGrouping.KeepSeparate ? null : result.FirstOrDefault(g =>
                    g[0].ProofGroup == row.ProofGroup && g[0].Target.scene == row.Target.scene &&
                    (Settings.Grouping == SceneTaggerGrouping.GroupAllVerified || g.All(member =>
                        Vector2.Distance(new Vector2(member.Bounds.center.x, member.Bounds.center.z), new Vector2(row.Bounds.center.x, row.Bounds.center.z)) <= 10 &&
                        Mathf.Abs(member.Bounds.center.y - row.Bounds.center.y) <= 3)));
                if (group == null) result.Add(new List<SceneTaggerRow> { row }); else group.Add(row);
            }
            return result;
        }
        internal void EnsureUniqueNames()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (List<SceneTaggerRow> group in Groups(Rows.Where(r => r.Status == SceneTaggerStatus.Ready)))
            {
                string original = group[0].Name, name = original;
                int suffix = 2;
                while (!used.Add(name)) { string tail = " " + suffix++; name = SceneTaggerText.Normalize(original, ConvaiObjectMetadata.MaxNameLength - tail.Length) + tail; }
                foreach (SceneTaggerRow row in group) if (row.EditRevision == 0) row.Name = name;
            }
        }
        internal void ReleaseAppliedImages()
        {
            foreach (SceneTaggerRow row in Rows.Where(r => r.Status == SceneTaggerStatus.Applied))
            { row.Evidence = null; row.PreviousUsable = null; row.RejectedEvidence = null; row.Source = SceneTaggerSource.ReleasedImage; }
            Notify();
        }
        internal void Apply()
        {
            if (Busy) return;
            List<SceneTaggerRow> rows = Rows.Where(r => r.CanApply).ToList();
            if (rows.Count == 0) return;
            // An unsuccessful Apply must not freeze a tentative grouping choice.
            foreach (SceneTaggerRow row in rows)
                if (!row.Target.GetComponent<ConvaiAutoTagOwnership>()) row.LogicalGroupId = null;
            foreach (List<SceneTaggerRow> group in Groups(Rows.Where(r => r.Included && (r.CanApply || r.Status == SceneTaggerStatus.Applied))))
            {
                string id = group.Select(r => r.LogicalGroupId).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? Guid.NewGuid().ToString("N");
                foreach (SceneTaggerRow row in group) if (string.IsNullOrEmpty(row.LogicalGroupId)) row.LogicalGroupId = id;
            }
            var ids = new HashSet<string>(rows.Select(r => r.LogicalGroupId));
            State = SceneTaggerState.Applying; Completed = 0; Total = rows.Count; LastError = ""; Notify();
            Progress = SceneTaggerApplyService.Apply(Rows.Where(r => r.Included && (r.CanApply || r.Status == SceneTaggerStatus.Applied) && ids.Contains(r.LogicalGroupId))
                .GroupBy(r => r.LogicalGroupId).Select(g => g.ToList()).ToList(), Settings);
            ApplyStarted |= rows.Any(r => r.Status == SceneTaggerStatus.Applied);
            foreach (var row in rows.Where(r => r.Status == SceneTaggerStatus.Applied))
                _applied[row.Id] = (row.Target.GetComponent<ConvaiAutoTagOwnership>().ObjectId, row.Name, row.Description);
            LastApplySummary = Progress; Completed = Total; State = SceneTaggerState.ReviewReady;
            Notify();
        }

        internal void ReconcileUndo()
        {
            if (Busy) { _reconcileUndoPending = true; return; }
            _reconcileUndoPending = false;
            bool changed = false;
            foreach (var row in Rows)
            {
                if (!_applied.TryGetValue(row.Id, out var applied) || !row.IsCurrent) continue;
                var owner = row.Target.GetComponent<ConvaiAutoTagOwnership>();
                var metadata = row.Target.GetComponent<ConvaiObjectMetadata>();
                if (row.Name != applied.name || row.Description != applied.description || row.PendingAnalysis || !string.IsNullOrWhiteSpace(row.Note)) continue;
                bool matches = owner && owner.ObjectId == applied.ownerId && owner.MetadataUnchanged && metadata && metadata.ObjectName == applied.name && metadata.ObjectDescription == applied.description;
                SceneTaggerStatus status = matches ? SceneTaggerStatus.Applied : metadata ? SceneTaggerStatus.Attention : SceneTaggerStatus.Ready;
                if (row.Status == status) continue;
                row.Status = status;
                row.Error = status == SceneTaggerStatus.Attention ? "Applied metadata changed. Existing authored values will be preserved." : "";
                changed = true;
            }
            if (changed)
            {
                LastApplySummary = Progress = "Applied scene metadata changed through Undo/Redo; review state was refreshed.";
                Notify();
            }
        }

        private static string EvidenceKey(SceneTaggerEvidence evidence)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(evidence.Png));
        }
        private static string CacheKey(SceneTaggerRow row, SceneTaggerSettings settings)
        {
            string data = row.Fingerprint + "|" + settings.CharacterId + "|" + settings.SceneDescription + "|" + settings.Focus + "|" + settings.Quality + "|unity-capture-v1|core-0.10.0-abi4|" + settings.EndpointIdentity + "|" + SceneTaggerCoreAdapter.Instance.Policy + "|" + SceneTaggerCoreAdapter.Instance.Protocol;
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(data)));
        }
        private void LoadCache(SceneTaggerSettings settings)
        {
            _cache.Clear();
            if (!settings.CacheEnabled || !File.Exists(CachePath) || new FileInfo(CachePath).Length > 8 * 1024 * 1024) return;
            try { foreach (var pair in JsonConvert.DeserializeObject<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)).Take(5000)) _cache[pair.Key] = pair.Value; }
            catch (Exception) { Progress = "The local metadata cache could not be read; capturing fresh evidence."; }
        }
        private bool TryCache(SceneTaggerRow row, SceneTaggerSettings settings)
        {
            if (!settings.CacheEnabled || !string.IsNullOrWhiteSpace(row.Note) ||
                !_cache.TryGetValue(CacheKey(row, settings), out CacheEntry entry) ||
                string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Description) || !float.IsFinite(entry.Confidence)) return false;
            row.Name = SceneTaggerText.Normalize(entry.Name, ConvaiObjectMetadata.MaxNameLength);
            row.Description = SceneTaggerText.Normalize(entry.Description, ConvaiObjectMetadata.MaxDescriptionLength);
            row.Confidence = entry.Confidence;
            row.Source = SceneTaggerSource.MetadataCache;
            row.Error = ""; row.PendingAnalysis = false;
            row.PreviousUsable = null; row.RejectedEvidence = null;
            row.Status = SceneTaggerStatus.Ready;
            return true;
        }
        private void SaveCache(SceneTaggerSettings settings)
        {
            if (!settings.CacheEnabled) return;
            string json = JsonConvert.SerializeObject(_cache.TakeLast(5000).ToDictionary(p => p.Key, p => p.Value));
            if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) return;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
            File.WriteAllText(CachePath + ".tmp", json);
            if (File.Exists(CachePath)) File.Replace(CachePath + ".tmp", CachePath, null); else File.Move(CachePath + ".tmp", CachePath);
        }
        private static void SaveDebug(SceneTaggerRow row)
        {
            string path = "Library/ConvaiSceneTagger/Debug";
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, row.Id + ".png"), row.Evidence.Png);
        }
        private sealed class CacheEntry { public string Name, Description; public float Confidence; }
    }
}
