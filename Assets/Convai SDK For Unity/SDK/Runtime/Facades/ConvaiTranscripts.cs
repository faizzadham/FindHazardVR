using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Convai.Application.Services.Transcript;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Runtime.Utilities;
using Newtonsoft.Json;

namespace Convai.Runtime.Facades
{
    /// <summary>
    ///     Unity-owned transcript timeline facade exposed from <c>ConvaiManager.Transcripts</c>.
    /// </summary>
    public sealed class ConvaiTranscripts : IDisposable
    {
        private readonly IRoomTranscriptEngine _engine;
        private readonly List<TranscriptSubscription> _subscriptions = new();
        private readonly List<CaptionSubscription> _captionSubscriptions = new();
        private readonly Dictionary<string, TranscriptCaption> _lastCaptionsByActor = new();
        private readonly object _timelineGate = new();
        private TranscriptTimelineMapper _timelineMapper = new();
        private bool _disposed;

        internal ConvaiTranscripts(IRoomTranscriptEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _engine.Changed += HandleEngineChanged;
            _engine.CaptionsChanged += HandleCaptionsChanged;
            foreach (TranscriptCaption caption in _engine.CurrentCaptions.Captions)
                _lastCaptionsByActor[CaptionKey(caption)] = caption;
        }

        /// <summary>
        ///     Maps one immutable engine snapshot. Reads are synchronized with change-batch mapping
        ///     and disposal; after disposal, reads remain available without retaining a mapper cache.
        ///     Subscription and presentation operations remain owned by the Unity thread.
        /// </summary>
        public TranscriptTimeline CurrentTimeline
        {
            get
            {
                TranscriptTimelineSnapshot snapshot = _engine.CurrentTimeline;
                lock (_timelineGate)
                    return (_timelineMapper ?? new TranscriptTimelineMapper()).Map(snapshot);
            }
        }

        public TranscriptCaptionSnapshot CurrentCaptions => _engine.CurrentCaptions;

        /// <summary>
        ///     Whether shipped transcript presentation components should render transcript updates.
        ///     Canonical room history continues recording while presentation is disabled.
        /// </summary>
        public bool IsPresentationEnabled { get; private set; } = true;

        public event Action<TranscriptChangeBatch> Changed;
        public event Action<TranscriptTurn> TurnUpdated;
        public event Action<TranscriptTurn> TurnCommitted;
        public event Action<TranscriptTurn> TurnCorrected;
        public event Action<string> TurnRemoved;
        public event Action<TranscriptCaptionSnapshot> CaptionsChanged;
        public event Action<bool> PresentationEnabledChanged;

        public IReadOnlyList<TranscriptTurn> GetTurns(TranscriptQuery query = null)
        {
            IReadOnlyList<TranscriptTurnSnapshot> turns = _engine.GetTurns(query);
            TranscriptTimelineSnapshot timeline = _engine.CurrentTimeline;
            lock (_timelineGate)
            {
                TranscriptTimelineMapper mapper = _timelineMapper ?? new TranscriptTimelineMapper();
                mapper.Map(timeline);
                return turns.Select(turn => mapper.MapChangedTurn(timeline, turn))
                    .Where(turn => turn != null).ToArray();
            }
        }

        public TranscriptTurn GetTurn(string turnId) => MapTurn(_engine.GetTurn(turnId));

        public TranscriptTurn GetLatestTurn(TranscriptParticipantRef participant) =>
            MapTurn(_engine.GetLatestTurn(participant));

        private TranscriptTurn MapTurn(TranscriptTurnSnapshot turn)
        {
            if (turn == null) return null;
            TranscriptTimelineSnapshot timeline = _engine.CurrentTimeline;
            lock (_timelineGate)
            {
                TranscriptTimelineMapper mapper = _timelineMapper ?? new TranscriptTimelineMapper();
                mapper.Map(timeline);
                return mapper.MapChangedTurn(timeline, turn);
            }
        }

        public IDisposable Subscribe(
            Action<TranscriptChange> callback,
            TranscriptSubscriptionOptions options = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            TranscriptSubscriptionOptions effectiveOptions = (options ?? new TranscriptSubscriptionOptions()).Copy();
            var subscription = new TranscriptSubscription(this, callback, effectiveOptions);
            _subscriptions.Add(subscription);

            if (subscription.Options.ReplayExisting)
            {
                foreach (TranscriptTurn turn in CurrentTimeline.Turns.Where(subscription.Options.Matches))
                {
                    subscription.TryInvoke(new TranscriptChange(
                        turn.IsCommitted ? TranscriptChangeKind.Committed : TranscriptChangeKind.Added,
                        turn));
                }
            }

            return subscription;
        }

        public IDisposable SubscribeCommitted(
            Action<TranscriptChange> callback,
            TranscriptSubscriptionOptions options = null)
        {
            TranscriptSubscriptionOptions effectiveOptions = (options ?? new TranscriptSubscriptionOptions()).Copy();
            effectiveOptions.IncludeActive = false;
            effectiveOptions.IncludeTerminal = true;
            return Subscribe(callback, effectiveOptions);
        }

        /// <summary>
        ///     Subscribes to live speech-aligned captions. Use <see cref="Subscribe" /> for chat/history turns.
        /// </summary>
        public IDisposable SubscribeCaptions(
            Action<TranscriptCaption> callback,
            TranscriptCaptionSubscriptionOptions options = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            TranscriptCaptionSubscriptionOptions effectiveOptions =
                (options ?? new TranscriptCaptionSubscriptionOptions()).Copy();
            var subscription = new CaptionSubscription(this, callback, effectiveOptions);
            _captionSubscriptions.Add(subscription);

            if (effectiveOptions.ReplayLatest)
            {
                foreach (TranscriptCaption caption in CurrentCaptions.Captions.Where(effectiveOptions.Matches))
                    callback(caption);
            }

            return subscription;
        }

        public void Clear() => _engine.Clear();

        internal void SetPresentationEnabled(bool enabled)
        {
            if (_disposed || IsPresentationEnabled == enabled) return;

            IsPresentationEnabled = enabled;
            SafeEventInvoker.Invoke(
                PresentationEnabledChanged,
                enabled,
                null,
                "ConvaiTranscripts.PresentationEnabledChanged",
                LogCategory.Events);
        }

        public string Export(TranscriptExportFormat format)
        {
            TranscriptTurn[] turns = CurrentTimeline.CommittedTurns
                .OrderBy(turn => turn.RoomSequence)
                .ToArray();

            switch (format)
            {
                case TranscriptExportFormat.Json:
                    return JsonConvert.SerializeObject(turns, Formatting.Indented);

                case TranscriptExportFormat.Markdown:
                    return ExportMarkdown(turns);

                default:
                    return ExportPlainText(turns);
            }
        }

        public void Dispose()
        {
            lock (_timelineGate)
            {
                if (_disposed) return;
                _disposed = true;
                _timelineMapper = null;
            }

            _engine.Changed -= HandleEngineChanged;
            _engine.CaptionsChanged -= HandleCaptionsChanged;
            _subscriptions.Clear();
            _captionSubscriptions.Clear();
            _lastCaptionsByActor.Clear();
        }

        private void HandleEngineChanged(TranscriptUpdateBatch batch)
        {
            TranscriptChangeBatch changeBatch;
            lock (_timelineGate)
            {
                if (_disposed) return;
                changeBatch = ToChangeBatch(batch);
            }

            SafeEventInvoker.Invoke(Changed, changeBatch, null, "ConvaiTranscripts.Changed", LogCategory.Events);

            for (int i = 0; i < changeBatch.Changes.Count; i++)
            {
                TranscriptChange change = changeBatch.Changes[i];
                if (change.Kind == TranscriptChangeKind.Removed)
                {
                    SafeEventInvoker.Invoke(
                        TurnRemoved,
                        change.TurnId,
                        null,
                        "ConvaiTranscripts.TurnRemoved",
                        LogCategory.Events);
                    NotifySubscriptions(change);
                    continue;
                }

                if (change.Turn == null) continue;

                if (change.Kind == TranscriptChangeKind.Corrected)
                    SafeEventInvoker.Invoke(
                        TurnCorrected,
                        change.Turn,
                        null,
                        "ConvaiTranscripts.TurnCorrected",
                        LogCategory.Events);
                else if (IsCommitChange(change.Kind))
                    SafeEventInvoker.Invoke(
                        TurnCommitted,
                        change.Turn,
                        null,
                        "ConvaiTranscripts.TurnCommitted",
                        LogCategory.Events);
                else
                    SafeEventInvoker.Invoke(
                        TurnUpdated,
                        change.Turn,
                        null,
                        "ConvaiTranscripts.TurnUpdated",
                        LogCategory.Events);

                NotifySubscriptions(change);
            }
        }

        private TranscriptChangeBatch ToChangeBatch(TranscriptUpdateBatch batch)
        {
            TranscriptTimeline timeline = _timelineMapper.Map(batch.Timeline);

            int capacity = batch.ChangedTurns.Count + batch.RemovedTurnIds.Count;
            if (capacity == 0) return new TranscriptChangeBatch(timeline, Array.Empty<TranscriptChange>());

            var buffer = new TranscriptChange[capacity];
            int count = 0;

            for (int i = 0; i < batch.ChangedTurns.Count; i++)
            {
                // Reuses the model the timeline mapping already produced for this exact snapshot, so
                // change.Turn and the timeline entry are one object.
                TranscriptTurn turn = _timelineMapper.MapChangedTurn(batch.Timeline, batch.ChangedTurns[i]);
                if (turn == null) continue;

                TranscriptChangeKind kind =
                    ContainsId(batch.CompletedTurnIds, turn.Id) ? TranscriptChangeKind.Committed :
                    ContainsId(batch.InterruptedTurnIds, turn.Id) ? TranscriptChangeKind.Interrupted :
                    ContainsId(batch.CorrectedTurnIds, turn.Id) ? TranscriptChangeKind.Corrected :
                    ContainsId(batch.AddedTurnIds, turn.Id) ? TranscriptChangeKind.Added :
                    TranscriptChangeKind.Updated;

                buffer[count++] = new TranscriptChange(kind, turn);
            }

            for (int i = 0; i < batch.RemovedTurnIds.Count; i++)
                buffer[count++] = new TranscriptChange(TranscriptChangeKind.Removed, null, batch.RemovedTurnIds[i]);

            IReadOnlyList<TranscriptChange> changes = buffer;
            if (count != buffer.Length)
            {
                var trimmed = new TranscriptChange[count];
                Array.Copy(buffer, trimmed, count);
                changes = trimmed;
            }

            return new TranscriptChangeBatch(timeline, changes);
        }

        private static bool ContainsId(IReadOnlyList<string> ids, string id)
        {
            if (ids == null) return false;

            for (int i = 0; i < ids.Count; i++)
                if (string.Equals(ids[i], id, StringComparison.Ordinal))
                    return true;

            return false;
        }

        private void NotifySubscriptions(TranscriptChange change)
        {
            foreach (TranscriptSubscription subscription in _subscriptions.ToArray())
                subscription.TryInvoke(change);
        }

        private void RemoveSubscription(TranscriptSubscription subscription) => _subscriptions.Remove(subscription);

        private void HandleCaptionsChanged(TranscriptCaptionSnapshot snapshot)
        {
            if (snapshot == null) return;

            var currentKeys = new HashSet<string>(snapshot.Captions.Select(CaptionKey));
            foreach (string staleKey in _lastCaptionsByActor.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
                _lastCaptionsByActor.Remove(staleKey);

            SafeEventInvoker.Invoke(
                CaptionsChanged,
                snapshot,
                null,
                "ConvaiTranscripts.CaptionsChanged",
                LogCategory.Events);

            foreach (TranscriptCaption caption in snapshot.Captions)
            {
                string key = CaptionKey(caption);
                if (_lastCaptionsByActor.TryGetValue(key, out TranscriptCaption previous) &&
                    string.Equals(previous.TurnId, caption.TurnId, StringComparison.Ordinal) &&
                    string.Equals(previous.Text, caption.Text, StringComparison.Ordinal) &&
                    previous.State == caption.State)
                    continue;

                _lastCaptionsByActor[key] = caption;
                foreach (CaptionSubscription subscription in _captionSubscriptions.ToArray())
                {
                    if (subscription.IsDisposed || !subscription.Options.Matches(caption)) continue;
                    subscription.Invoke(caption);
                }
            }
        }

        private void RemoveCaptionSubscription(CaptionSubscription subscription) =>
            _captionSubscriptions.Remove(subscription);

        private static string CaptionKey(TranscriptCaption caption) =>
            !string.IsNullOrWhiteSpace(caption.Speaker?.ParticipantId)
                ? $"{caption.Speaker?.Type}:participant:{caption.Speaker.ParticipantId}"
                : $"{caption.Speaker?.Type}:actor:{caption.Speaker?.Id}";

        private static bool IsCommitChange(TranscriptChangeKind kind) =>
            kind == TranscriptChangeKind.Committed || kind == TranscriptChangeKind.Interrupted;

        private static bool IsTerminalChange(TranscriptChangeKind kind) =>
            IsCommitChange(kind) || kind == TranscriptChangeKind.Corrected;

        private static string ExportPlainText(IEnumerable<TranscriptTurn> turns)
        {
            var builder = new StringBuilder();
            foreach (TranscriptTurn turn in turns)
            {
                string speaker = string.IsNullOrWhiteSpace(turn.Speaker?.DisplayName)
                    ? turn.Speaker?.Type.ToString() ?? "Unknown"
                    : turn.Speaker.DisplayName;
                builder.AppendLine($"{speaker}: {turn.DisplayText}");
            }

            return builder.ToString();
        }

        private static string ExportMarkdown(IEnumerable<TranscriptTurn> turns)
        {
            var builder = new StringBuilder();
            foreach (TranscriptTurn turn in turns)
            {
                string speaker = string.IsNullOrWhiteSpace(turn.Speaker?.DisplayName)
                    ? turn.Speaker?.Type.ToString() ?? "Unknown"
                    : turn.Speaker.DisplayName;
                builder.AppendLine($"**{speaker}:** {turn.DisplayText}");
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private sealed class TranscriptSubscription : IDisposable
        {
            private readonly Action<TranscriptChange> _callback;
            private readonly HashSet<string> _matchedTurnIds = new();
            private readonly ConvaiTranscripts _owner;

            public TranscriptSubscription(
                ConvaiTranscripts owner,
                Action<TranscriptChange> callback,
                TranscriptSubscriptionOptions options)
            {
                _owner = owner;
                _callback = callback;
                Options = options;
                TerminalOnly = !options.IncludeActive && options.IncludeTerminal;
            }

            public TranscriptSubscriptionOptions Options { get; }
            public bool TerminalOnly { get; }
            public bool IsDisposed { get; private set; }

            public void TryInvoke(TranscriptChange change)
            {
                if (IsDisposed || change == null) return;

                if (change.Kind == TranscriptChangeKind.Removed)
                {
                    if (_matchedTurnIds.Remove(change.TurnId))
                        _callback(change);
                    return;
                }

                TranscriptTurn turn = change.Turn;
                if (!Options.Matches(turn)) return;
                if (TerminalOnly && !IsTerminalChange(change.Kind)) return;

                _matchedTurnIds.Add(turn.Id);
                _callback(change);
            }

            public void Dispose()
            {
                if (IsDisposed) return;
                IsDisposed = true;
                _matchedTurnIds.Clear();
                _owner.RemoveSubscription(this);
            }
        }

        private sealed class CaptionSubscription : IDisposable
        {
            private readonly Action<TranscriptCaption> _callback;
            private readonly ConvaiTranscripts _owner;

            public CaptionSubscription(
                ConvaiTranscripts owner,
                Action<TranscriptCaption> callback,
                TranscriptCaptionSubscriptionOptions options)
            {
                _owner = owner;
                _callback = callback;
                Options = options;
            }

            public TranscriptCaptionSubscriptionOptions Options { get; }
            public bool IsDisposed { get; private set; }

            public void Invoke(TranscriptCaption caption) => _callback(caption);

            public void Dispose()
            {
                if (IsDisposed) return;
                IsDisposed = true;
                _owner.RemoveCaptionSubscription(this);
            }
        }
    }
}
