using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Runtime.Utilities;
using Unity.Profiling;

namespace Convai.Application.Services.Transcript
{
    internal interface IRoomTranscriptEngine : IDisposable
    {
        public TranscriptTimelineSnapshot CurrentTimeline { get; }

        public TranscriptCaptionSnapshot CurrentCaptions { get; }

        public event Action<TranscriptUpdateBatch> Changed;

        public event Action<TranscriptCaptionSnapshot> CaptionsChanged;

        public IReadOnlyList<TranscriptTurnSnapshot> GetTurns(TranscriptQuery query);

        public TranscriptTurnSnapshot GetTurn(string turnId);

        public TranscriptTurnSnapshot GetLatestTurn(TranscriptParticipantRef participant);

        public void UpdatePlayerDisplayName(string displayName);

        public void Clear();
    }

    /// <summary>
    ///     Canonical room-scoped transcript authority that normalizes raw transcript events into turn snapshots.
    /// </summary>
    internal sealed class RoomTranscriptEngine : IRoomTranscriptEngine,
        IEventSubscriber<PlayerTranscriptReceived>,
        IEventSubscriber<CharacterTranscriptReceived>,
        IEventSubscriber<FinalUserTranscriptionReceived>,
        IEventSubscriber<PlayerSpeakingStateChanged>,
        IEventSubscriber<CharacterSpeechStateChanged>,
        IEventSubscriber<CharacterTurnCompleted>,
        IEventSubscriber<CharacterTtsTextChunk>
    {
        private static readonly ProfilerMarker BuildTimelineMarker = new("Convai.Transcript.BuildTimeline");
        private static readonly Func<TranscriptTurnSnapshot, string> SnapshotTurnId = snapshot => snapshot.TurnId;

        private const int MaxFingerprintEntries = 2048;
        private static readonly TranscriptSegmentSourceKind[] TranscriptSourceKinds =
            (TranscriptSegmentSourceKind[])Enum.GetValues(typeof(TranscriptSegmentSourceKind));

        private readonly SubscriptionToken _characterSpeechToken;
        private readonly SubscriptionToken _characterTranscriptToken;
        private readonly SubscriptionToken _characterTtsTextToken;
        private readonly SubscriptionToken _characterTurnCompletedToken;
        private readonly IEventHub _eventHub;
        private readonly SubscriptionToken _finalUserTranscriptionToken;
        private readonly object _gate = new();
        private readonly ILogger _logger;
        private readonly Dictionary<string, TurnState> _openCharacterTurnsByActor = new();
        private readonly Dictionary<string, TurnState> _openPlayerTurnsByActor = new();
        private readonly Dictionary<string, string> _lastUpdateFingerprintByMessageId = new();
        private readonly Queue<string> _updateFingerprintOrder = new();
        private readonly Dictionary<string, CaptionState> _captionsByActor = new();
        private readonly SubscriptionToken _playerSpeakingToken;
        private readonly SubscriptionToken _playerTranscriptToken;
        private readonly Dictionary<string, TurnState> _turnsById = new();

        // Incrementally maintained views over _turnsById. Room order is append-only because
        // RoomSequence only ever increases, so an interim update touches the open turns alone.
        private readonly List<TurnState> _turnsInRoomOrder = new();
        private readonly List<TurnState> _activeTurnStates = new();
        private readonly List<TurnState> _changedTurnScratch = new();
        private readonly List<string> _turnIdScratch = new();
        private TranscriptTurnSnapshot[] _committedSnapshots = Array.Empty<TranscriptTurnSnapshot>();
        private TranscriptTurnSnapshot[] _activeSnapshots = Array.Empty<TranscriptTurnSnapshot>();
        private Dictionary<string, TranscriptTurnSnapshot> _committedById = new(StringComparer.Ordinal);
        private Dictionary<string, TranscriptTurnSnapshot> _latestCommittedByActorKey = new(StringComparer.Ordinal);
        private KeyValuePair<string, TranscriptTurnSnapshot>[] _activeLatestEntries =
            Array.Empty<KeyValuePair<string, TranscriptTurnSnapshot>>();
        private IReadOnlyDictionary<string, TranscriptTurnSnapshot> _turnIndex;
        private IReadOnlyDictionary<string, TranscriptTurnSnapshot> _latestTurnIndex;
        private bool _committedIndexDirty;
        private bool _activeIndexDirty;
        private TranscriptTimelineSnapshot _currentTimeline = TranscriptTimelineSnapshot.Empty;
        private TranscriptCaptionSnapshot _currentCaptions = TranscriptCaptionSnapshot.Empty;
        private bool _disposed;
        private bool _isLocalPlayerSpeaking;
        private long _nextRoomSequence = 1;
        private long _nextSyntheticId = 1;
        private long _timelineCursor;
        private long _captionCursor;

        public RoomTranscriptEngine(
            IEventHub eventHub,
            ILogger logger = null)
        {
            _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
            _logger = logger.WithTag(nameof(RoomTranscriptEngine), LogCategory.Transcript);

            _playerTranscriptToken = _eventHub.Subscribe<PlayerTranscriptReceived>(HandlePlayerTranscriptReceived);
            _characterTranscriptToken =
                _eventHub.Subscribe<CharacterTranscriptReceived>(HandleCharacterTranscriptReceived);
            _finalUserTranscriptionToken =
                _eventHub.Subscribe<FinalUserTranscriptionReceived>(HandleFinalUserTranscriptionReceived);
            _playerSpeakingToken = _eventHub.Subscribe<PlayerSpeakingStateChanged>(HandlePlayerSpeakingStateChanged);
            _characterSpeechToken =
                _eventHub.Subscribe<CharacterSpeechStateChanged>(HandleCharacterSpeechStateChanged);
            _characterTurnCompletedToken =
                _eventHub.Subscribe<CharacterTurnCompleted>(HandleCharacterTurnCompleted);
            _characterTtsTextToken =
                _eventHub.Subscribe<CharacterTtsTextChunk>(HandleCharacterTtsTextChunk);
        }

        public void OnEvent(CharacterSpeechStateChanged e)
        {
            if (_disposed || !e.IsSpeaking || _isLocalPlayerSpeaking) return;

            TranscriptUpdateBatch batch;
            TranscriptCaptionSnapshot captions;

            lock (_gate)
            {
                CompleteOpenPlayerTurnsLocked(e.Timestamp, out List<TurnState> completedTurns);
                batch = BuildBatchLocked(completedTurns, null, null, TurnIds(completedTurns), null, null);
                captions = FinalizeCaptionsLocked(
                    TranscriptSpeakerType.Player,
                    null,
                    TranscriptCaptionState.Completed,
                    e.Timestamp);
            }

            PublishBatch(batch);
            PublishCaptions(captions);
        }

        public void OnEvent(CharacterTranscriptReceived e)
        {
            if (_disposed) return;

            // Raw model tokens are useful for diagnostics, not safe transcript history.
            if (e.SourceKind == TranscriptSegmentSourceKind.BotLlmPreview) return;

            if (string.IsNullOrWhiteSpace(e.Text)) return;

            TranscriptUpdateBatch batch;

            lock (_gate)
            {
                TranscriptParticipantRef actor = BuildCharacterActor(e);
                string actorKey = BuildActorKey(actor.Kind, actor.PlayerOrCharacterId, actor.ParticipantId);
                string preferredTurnId = ResolveCharacterTurnId(e);
                string updateKey = ResolveFirstNonEmpty(e.UpdateId, preferredTurnId);

                // Ignore stale output delivered after a turn was committed or interrupted.
                if (!string.IsNullOrWhiteSpace(preferredTurnId) &&
                    _turnsById.TryGetValue(preferredTurnId, out TurnState completedTurn) &&
                    completedTurn.Actor.Kind == TranscriptParticipantKind.Character &&
                    completedTurn.IsCompleted)
                    return;

                CompleteOpenPlayerTurnsLocked(e.Timestamp, out List<TurnState> completedPlayerTurns);

                if (IsDuplicateUpdateLocked(updateKey, e.Lifecycle, e.SourceKind, e.Text))
                {
                    batch = BuildBatchLocked(
                        completedPlayerTurns,
                        null,
                        null,
                        TurnIds(completedPlayerTurns),
                        null,
                        null);
                }
                else
                {
                    TurnState turn = null;
                    List<TurnState> completedCharacterTurns = null;
                    bool hasIdentityMatch = !string.IsNullOrWhiteSpace(preferredTurnId) &&
                                            _turnsById.TryGetValue(preferredTurnId, out turn) &&
                                            !turn.IsCompleted;

                    if (!hasIdentityMatch && !string.IsNullOrWhiteSpace(preferredTurnId))
                    {
                        if (_openCharacterTurnsByActor.TryGetValue(actorKey, out TurnState openActorTurn) &&
                            !openActorTurn.IsCompleted)
                        {
                            CompleteTurnLocked(openActorTurn, e.Timestamp, false);
                            completedCharacterTurns = new List<TurnState> { openActorTurn };
                        }

                        turn = CreateTurnLocked(actor, e.Timestamp, preferredTurnId, e.ResponseId);
                        _openCharacterTurnsByActor[actorKey] = turn;
                    }
                    else if (!hasIdentityMatch &&
                             (!_openCharacterTurnsByActor.TryGetValue(actorKey, out turn) || turn.IsCompleted))
                    {
                        turn = CreateTurnLocked(actor, e.Timestamp, preferredTurnId, e.ResponseId);
                        _openCharacterTurnsByActor[actorKey] = turn;
                    }
                    else if (!string.IsNullOrWhiteSpace(e.ResponseId))
                    {
                        turn.ResponseId = e.ResponseId;
                    }

                    if (hasIdentityMatch)
                        _openCharacterTurnsByActor[actorKey] = turn;

                    UpdateTurnActorLocked(turn, actor);

                    SegmentState segment = turn.GetOrCreateCharacterSegment(e.Timestamp, e.SourceKind);
                    bool ignoreLegacyProjection = e.SourceKind == TranscriptSegmentSourceKind.LegacyBotTranscript &&
                                                  segment.SourceKind == TranscriptSegmentSourceKind.BotOutput;

                    if (!ignoreLegacyProjection)
                    {
                        if (e.SourceKind == TranscriptSegmentSourceKind.BotOutput &&
                            segment.SourceKind == TranscriptSegmentSourceKind.LegacyBotTranscript)
                        {
                            segment.CommittedText = string.Empty;
                            segment.InterimText = string.Empty;
                        }

                        ApplyCharacterTranscriptLocked(turn, segment, e.Text, e.IsFinal, e.Timestamp, e.SourceKind);
                    }

                    List<TurnState> changedTurns = ignoreLegacyProjection
                        ? new List<TurnState>()
                        : new List<TurnState> { turn };
                    if (completedPlayerTurns != null && completedPlayerTurns.Count > 0)
                        changedTurns.AddRange(completedPlayerTurns);
                    if (completedCharacterTurns != null && completedCharacterTurns.Count > 0)
                        changedTurns.AddRange(completedCharacterTurns);

                    batch = BuildBatchLocked(
                        changedTurns,
                        null,
                        null,
                        TurnIds(completedPlayerTurns, completedCharacterTurns),
                        null,
                        null);
                }
            }

            PublishBatch(batch);
        }

        public void OnEvent(FinalUserTranscriptionReceived e)
        {
            if (_disposed || string.IsNullOrWhiteSpace(e.Text)) return;

            TranscriptUpdateBatch batch;

            lock (_gate)
            {
                TurnState turn = FindPlayerTurnForProcessedFinalLocked(e);
                if (turn == null)
                    return;

                bool wasCommitted = turn.IsCompleted;
                string previousText = GetTurnDisplayTextLocked(turn);

                bool actorChanged = UpdateTurnActorLocked(turn, BuildPlayerActor(e, turn.Actor.DisplayName));
                SegmentState segment = turn.ActiveSegment ?? turn.Segments.LastOrDefault();
                if (segment == null)
                    segment = GetOrCreatePlayerSegmentLocked(turn, e.MessageId, e.Timestamp, TranscriptionPhase.ProcessedFinal);

                bool transcriptChanged = ApplyPlayerProcessedFinalLocked(turn, segment, e.Text, e.Timestamp);
                bool textChanged = !string.Equals(previousText, GetTurnDisplayTextLocked(turn), StringComparison.Ordinal);

                if (!transcriptChanged && !actorChanged)
                    return;

                if (actorChanged && !transcriptChanged)
                {
                    turn.LastUpdatedAtUtc = e.Timestamp;
                    turn.Revision++;
                }

                bool shouldCommit = !wasCommitted && turn.HasAnyText;
                if (shouldCommit && turn.HasAnyText)
                {
                    CompleteTurnLocked(turn, e.Timestamp, false);
                    _openPlayerTurnsByActor.Remove(turn.ActorKey);
                }

                batch = BuildBatchLocked(
                    new[] { turn },
                    null,
                    null,
                    shouldCommit ? new[] { turn.TurnId } : null,
                    null,
                    null,
                    wasCommitted && textChanged ? new[] { turn.TurnId } : null);
            }

            PublishBatch(batch);
        }

        public void OnEvent(CharacterTurnCompleted e)
        {
            if (_disposed) return;

            TranscriptUpdateBatch batch;
            TranscriptCaptionSnapshot captions;

            lock (_gate)
            {
                CompleteCharacterTurnLocked(
                    e.CharacterId,
                    e.ParticipantId,
                    e.Timestamp,
                    e.WasInterrupted,
                    out List<TurnState> turns);
                batch = BuildBatchLocked(
                    turns,
                    null,
                    null,
                    e.WasInterrupted ? null : TurnIds(turns),
                    e.WasInterrupted ? TurnIds(turns) : null,
                    null);
                captions = FinalizeCaptionsLocked(
                    TranscriptSpeakerType.Character,
                    e.ParticipantId,
                    e.WasInterrupted ? TranscriptCaptionState.Interrupted : TranscriptCaptionState.Completed,
                    e.Timestamp);
            }

            PublishBatch(batch);
            PublishCaptions(captions);
        }

        public void OnEvent(PlayerSpeakingStateChanged e)
        {
            if (_disposed) return;

            TranscriptCaptionSnapshot captions;
            lock (_gate)
            {
                _isLocalPlayerSpeaking = e.IsSpeaking;
                captions = FinalizeCaptionsLocked(
                    e.IsSpeaking ? TranscriptSpeakerType.Character : TranscriptSpeakerType.Player,
                    null,
                    e.IsSpeaking ? TranscriptCaptionState.Interrupted : TranscriptCaptionState.Completed,
                    e.Timestamp);
            }

            if (!e.IsSpeaking)
            {
                // VAD stops delimit acoustic bursts, not semantic transcript turns. The backend
                // can continue one utterance after a short pause, so commit only when a processed
                // final or character output supplies an authoritative conversation boundary.
                PublishCaptions(captions);
                return;
            }

            TranscriptUpdateBatch batch;

            lock (_gate)
            {
                InterruptOpenCharacterTurnsLocked(e.Timestamp, out List<TurnState> interruptedTurns);
                batch = BuildBatchLocked(interruptedTurns, null, null, null, TurnIds(interruptedTurns), null);
            }

            PublishBatch(batch);
            PublishCaptions(captions);
        }

        public void OnEvent(PlayerTranscriptReceived e)
        {
            if (_disposed) return;

            TranscriptUpdateBatch batch = null;
            TranscriptCaptionSnapshot captions = null;

            lock (_gate)
            {
                captions = UpdatePlayerCaptionLocked(e);

                if (e.SourceKind == TranscriptSegmentSourceKind.PlayerTypedText)
                {
                    if (IsDuplicateUpdateLocked(e.MessageId, e.Lifecycle, e.SourceKind, e.Text))
                        return;

                    batch = HandleTypedPlayerTranscriptLocked(e);
                }
                else if (e.Phase == TranscriptionPhase.Completed)
                {
                    batch = HandlePlayerSessionCompletedLocked(e);
                }
                else
                {
                    if (IsDuplicateUpdateLocked(e.MessageId, e.Lifecycle, e.SourceKind, e.Text))
                        return;

                    TranscriptParticipantRef actor = BuildPlayerActor(e);
                    string actorKey = BuildActorKey(actor.Kind, actor.PlayerOrCharacterId, actor.ParticipantId);
                    TurnState turn = GetOrCreatePlayerTurnLocked(
                        actor,
                        actorKey,
                        e.Timestamp,
                        e.MessageId,
                        e.Phase == TranscriptionPhase.ProcessedFinal);
                    bool wasCommitted = turn.IsCompleted;
                    List<TurnState> interruptedCharacterTurns = null;
                    string previousText = GetTurnDisplayTextLocked(turn);

                    SegmentState segment = wasCommitted && e.Phase == TranscriptionPhase.ProcessedFinal
                        ? turn.Segments.LastOrDefault() ??
                          GetOrCreatePlayerSegmentLocked(turn, e.MessageId, e.Timestamp, e.Phase)
                        : GetOrCreatePlayerSegmentLocked(turn, e.MessageId, e.Timestamp, e.Phase);
                    bool actorChanged = UpdateTurnActorLocked(turn, actor);
                    bool turnChanged = true;

                    switch (e.Phase)
                    {
                        case TranscriptionPhase.Listening:
                            ApplyPlayerInterimLocked(turn, segment, string.Empty, e.Timestamp);
                            break;

                        case TranscriptionPhase.Interim:
                            ApplyPlayerInterimLocked(turn, segment, e.Text, e.Timestamp);
                            break;

                        case TranscriptionPhase.AsrFinal:
                            ApplyPlayerAsrFinalLocked(turn, segment, e.Text, e.Timestamp);
                            break;

                        case TranscriptionPhase.ProcessedFinal:
                            turnChanged = ApplyPlayerProcessedFinalLocked(turn, segment, e.Text, e.Timestamp);
                            break;

                        default:
                            return;
                    }

                    if (!turnChanged && !actorChanged)
                        return;

                    if (actorChanged && !turnChanged)
                    {
                        turn.LastUpdatedAtUtc = e.Timestamp;
                        turn.Revision++;
                    }

                    InterruptOpenCharacterTurnsLocked(e.Timestamp, out interruptedCharacterTurns);

                    bool textChanged = !string.Equals(
                        previousText,
                        GetTurnDisplayTextLocked(turn),
                        StringComparison.Ordinal);
                    bool correctedCommittedTurn = wasCommitted &&
                                                  e.Phase == TranscriptionPhase.ProcessedFinal &&
                                                  textChanged;
                    List<TurnState> changedTurns = new() { turn };
                    if (interruptedCharacterTurns != null && interruptedCharacterTurns.Count > 0)
                        changedTurns.AddRange(interruptedCharacterTurns);

                    batch = BuildBatchLocked(
                        changedTurns,
                        null,
                        null,
                        !wasCommitted && turn.State == TranscriptTurnState.Committed
                            ? new[] { turn.TurnId }
                            : null,
                        TurnIds(interruptedCharacterTurns),
                        null,
                        correctedCommittedTurn ? new[] { turn.TurnId } : null);
                }
            }

            PublishBatch(batch);
            PublishCaptions(captions);
        }

        public void OnEvent(CharacterTtsTextChunk e)
        {
            if (_disposed || e.IsEmpty) return;

            TranscriptCaptionSnapshot captions;
            lock (_gate)
                captions = UpdateCharacterCaptionLocked(e);

            PublishCaptions(captions);
        }

        private TranscriptUpdateBatch HandlePlayerSessionCompletedLocked(PlayerTranscriptReceived e)
        {
            TranscriptParticipantRef actor = BuildPlayerActor(e);
            string actorKey = BuildActorKey(actor.Kind, actor.PlayerOrCharacterId, actor.ParticipantId);
            string sourceSessionId = ResolveFirstNonEmpty(e.MessageId, e.TurnId);
            TurnState turn = null;

            if (!string.IsNullOrWhiteSpace(sourceSessionId) &&
                _turnsById.TryGetValue(sourceSessionId, out TurnState exactTurn) &&
                exactTurn.Actor.Kind == TranscriptParticipantKind.Player &&
                !exactTurn.IsCompleted)
            {
                turn = exactTurn;
            }

            if (turn == null &&
                _openPlayerTurnsByActor.TryGetValue(actorKey, out TurnState openTurn) &&
                !openTurn.IsCompleted)
                turn = openTurn;

            if (turn == null)
                return null;

            SegmentState segment = !string.IsNullOrWhiteSpace(sourceSessionId)
                ? turn.Segments.FirstOrDefault(candidate =>
                    string.Equals(candidate.SourceSessionId, sourceSessionId, StringComparison.Ordinal))
                : null;
            segment ??= turn.ActiveSegment;
            if (segment == null || segment.IsClosed)
                return null;

            FinalizePlayerSegmentLocked(turn, segment, e.Timestamp);
            if (turn.HasAnyText)
                return BuildBatchLocked(new[] { turn }, null, null, null, null, null);

            string removedTurnId = turn.TurnId;
            turn.State = TranscriptTurnState.Discarded;
            turn.Revision++;
            RemoveTurnLocked(turn);
            return BuildBatchLocked(null, null, null, null, null, new[] { removedTurnId });
        }

        private TranscriptUpdateBatch HandleTypedPlayerTranscriptLocked(PlayerTranscriptReceived e)
        {
            TranscriptParticipantRef actor = BuildPlayerActor(e);
            string actorKey = BuildActorKey(actor.Kind, actor.PlayerOrCharacterId, actor.ParticipantId);
            TurnState turn = null;
            bool hasExactTurnMatch = !string.IsNullOrWhiteSpace(e.MessageId) &&
                                     _turnsById.TryGetValue(e.MessageId, out turn);
            bool wasAlreadyCommitted = turn != null && turn.IsCompleted;

            List<TurnState> interruptedCharacterTurns = null;
            if (!hasExactTurnMatch)
                InterruptOpenCharacterTurnsLocked(e.Timestamp, out interruptedCharacterTurns);

            if (turn == null)
                turn = CreateTurnLocked(actor, e.Timestamp, e.MessageId);

            string previousText = GetTurnDisplayTextLocked(turn);
            bool actorChanged = UpdateTurnActorLocked(turn, actor);
            bool sameAuthoritativeText = wasAlreadyCommitted &&
                                         string.Equals(previousText, e.Text, StringComparison.Ordinal) &&
                                         (turn.PrimaryTextSource == TranscriptTextSource.TypedText ||
                                          turn.PrimaryTextSource == TranscriptTextSource.ProcessedFinal);
            if (sameAuthoritativeText && !actorChanged)
                return null;

            if (sameAuthoritativeText)
            {
                turn.LastUpdatedAtUtc = e.Timestamp;
                turn.Revision++;
            }

            if (!sameAuthoritativeText)
            {
                SegmentState segment = GetOrCreateTypedPlayerSegmentLocked(turn, e.MessageId, e.Timestamp);
                ApplyTypedPlayerTextLocked(turn, segment, e.Text, e.Timestamp);
            }

            bool textChanged = !string.Equals(previousText, GetTurnDisplayTextLocked(turn), StringComparison.Ordinal);

            List<TurnState> changedTurns = new() { turn };
            if (interruptedCharacterTurns != null && interruptedCharacterTurns.Count > 0)
                changedTurns.AddRange(interruptedCharacterTurns);

            return BuildBatchLocked(
                changedTurns,
                null,
                null,
                wasAlreadyCommitted ? null : new[] { turn.TurnId },
                TurnIds(interruptedCharacterTurns),
                null,
                wasAlreadyCommitted && textChanged ? new[] { turn.TurnId } : null);
        }

        public TranscriptTimelineSnapshot CurrentTimeline
        {
            get
            {
                lock (_gate) return _currentTimeline;
            }
        }

        public TranscriptCaptionSnapshot CurrentCaptions
        {
            get
            {
                lock (_gate) return _currentCaptions;
            }
        }

        public event Action<TranscriptUpdateBatch> Changed;

        public event Action<TranscriptCaptionSnapshot> CaptionsChanged;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _eventHub.Unsubscribe(_playerTranscriptToken);
            _eventHub.Unsubscribe(_characterTranscriptToken);
            _eventHub.Unsubscribe(_finalUserTranscriptionToken);
            _eventHub.Unsubscribe(_playerSpeakingToken);
            _eventHub.Unsubscribe(_characterSpeechToken);
            _eventHub.Unsubscribe(_characterTurnCompletedToken);
            _eventHub.Unsubscribe(_characterTtsTextToken);
        }

        public IReadOnlyList<TranscriptTurnSnapshot> GetTurns(TranscriptQuery query)
        {
            query ??= new TranscriptQuery();
            lock (_gate)
            {
                IEnumerable<TranscriptTurnSnapshot> turns = Enumerable.Empty<TranscriptTurnSnapshot>();

                if (query.IncludeActiveTurns)
                    turns = turns.Concat(_currentTimeline.ActiveTurns);

                if (query.IncludeCommittedTurns)
                    turns = turns.Concat(_currentTimeline.CommittedTurns);

                if (query.ParticipantKind.HasValue)
                    turns = turns.Where(t => t.Participant.Kind == query.ParticipantKind.Value);

                if (!string.IsNullOrWhiteSpace(query.PlayerOrCharacterId))
                {
                    turns = turns.Where(t =>
                        string.Equals(t.Participant.PlayerOrCharacterId, query.PlayerOrCharacterId,
                            StringComparison.Ordinal));
                }

                if (!string.IsNullOrWhiteSpace(query.ParticipantId))
                {
                    turns = turns.Where(t =>
                        string.Equals(t.Participant.ParticipantId, query.ParticipantId, StringComparison.Ordinal));
                }

                return turns
                    .OrderBy(t => t.RoomSequence)
                    .ToArray();
            }
        }

        public TranscriptTurnSnapshot GetTurn(string turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId)) return null;

            lock (_gate)
                return _currentTimeline.TurnsById.TryGetValue(turnId, out TranscriptTurnSnapshot turn) ? turn : null;
        }

        public TranscriptTurnSnapshot GetLatestTurn(TranscriptParticipantRef participant)
        {
            string key = BuildActorKey(participant.Kind, participant.PlayerOrCharacterId, participant.ParticipantId);

            lock (_gate)
            {
                if (_currentTimeline.LatestTurnByParticipant.TryGetValue(key, out TranscriptTurnSnapshot turn))
                    return turn;

                if (!string.IsNullOrWhiteSpace(participant.PlayerOrCharacterId))
                {
                    return _currentTimeline.LatestTurnByParticipant.Values
                        .Where(t => t.Participant.Kind == participant.Kind)
                        .Where(t => string.Equals(t.Participant.PlayerOrCharacterId, participant.PlayerOrCharacterId,
                            StringComparison.Ordinal))
                        .OrderByDescending(t => t.RoomSequence)
                        .FirstOrDefault();
                }

                return null;
            }
        }

        public void UpdatePlayerDisplayName(string displayName)
        {
            if (_disposed || string.IsNullOrWhiteSpace(displayName)) return;

            string effectiveDisplayName = displayName.Trim();
            TranscriptUpdateBatch batch = null;
            TranscriptCaptionSnapshot captions = null;

            lock (_gate)
            {
                var changedTurns = new List<TurnState>();
                var correctedTurnIds = new List<string>();

                foreach (TurnState turn in _turnsById.Values)
                {
                    if (turn.Actor.Kind != TranscriptParticipantKind.Player ||
                        string.Equals(turn.Actor.DisplayName, effectiveDisplayName, StringComparison.Ordinal))
                        continue;

                    var renamedActor = new TranscriptParticipantRef(
                        turn.Actor.Kind,
                        turn.Actor.PlayerOrCharacterId,
                        effectiveDisplayName,
                        turn.Actor.ParticipantId);
                    UpdateTurnActorLocked(turn, renamedActor);
                    turn.Revision++;
                    changedTurns.Add(turn);

                    if (turn.IsCompleted)
                        correctedTurnIds.Add(turn.TurnId);
                }

                if (changedTurns.Count > 0)
                {
                    batch = BuildBatchLocked(
                        changedTurns,
                        null,
                        null,
                        null,
                        null,
                        null,
                        correctedTurnIds);
                }

                bool captionsChanged = false;
                foreach (KeyValuePair<string, CaptionState> pair in _captionsByActor.ToArray())
                {
                    CaptionState caption = pair.Value;
                    if (caption.Speaker?.Type != TranscriptSpeakerType.Player ||
                        string.Equals(caption.Speaker.DisplayName, effectiveDisplayName, StringComparison.Ordinal))
                        continue;

                    var renamedSpeaker = new TranscriptSpeaker(
                        caption.Speaker.Type,
                        caption.Speaker.Id,
                        effectiveDisplayName,
                        caption.Speaker.ParticipantId);
                    _captionsByActor[pair.Key] = caption.WithSpeaker(renamedSpeaker);
                    captionsChanged = true;
                }

                if (captionsChanged)
                    captions = BuildCaptionSnapshotLocked();
            }

            PublishBatch(batch);
            PublishCaptions(captions);
        }

        public void Clear()
        {
            TranscriptUpdateBatch batch;
            TranscriptCaptionSnapshot captions;

            lock (_gate)
            {
                string[] removedIds = _turnsById.Keys.ToArray();
                _turnsById.Clear();
                foreach (TurnState turn in _turnsInRoomOrder)
                {
                    turn.IsIndexedAsActive = false;
                    turn.IsIndexedAsCommitted = false;
                    turn.IsRemoved = true;
                }

                _turnsInRoomOrder.Clear();
                _activeTurnStates.Clear();
                _committedIndexDirty = true;
                _activeIndexDirty = true;
                _openPlayerTurnsByActor.Clear();
                _openCharacterTurnsByActor.Clear();
                _lastUpdateFingerprintByMessageId.Clear();
                _updateFingerprintOrder.Clear();
                _isLocalPlayerSpeaking = false;
                batch = BuildBatchLocked(null, null, null, null, null, removedIds);
                _captionsByActor.Clear();
                captions = BuildCaptionSnapshotLocked();
            }

            PublishBatch(batch);
            PublishCaptions(captions);
        }

        private void HandlePlayerTranscriptReceived(PlayerTranscriptReceived e)
        {
            try
            {
                OnEvent(e);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error, LogCategory.UI) == true)
                    _logger?.Error(
                    $"Failed handling PlayerTranscriptReceived: phase={e.Phase}, turnId={e.TurnId}, messageId={e.MessageId}, text=\"{e.Text}\". {ex}",
                    LogCategory.UI);
            }
        }

        private void HandleCharacterTranscriptReceived(CharacterTranscriptReceived e)
        {
            try
            {
                OnEvent(e);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error, LogCategory.UI) == true)
                    _logger?.Error(
                    $"Failed handling CharacterTranscriptReceived: characterId='{e.CharacterId}', participantId='{e.Message.ParticipantId}', final={e.IsFinal}, text=\"{e.Text}\". {ex}",
                    LogCategory.UI);
            }
        }

        private void HandleFinalUserTranscriptionReceived(FinalUserTranscriptionReceived e)
        {
            try
            {
                OnEvent(e);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error, LogCategory.UI) == true)
                    _logger?.Error(
                    $"Failed handling FinalUserTranscriptionReceived: messageId={e.MessageId}, participantId={e.ParticipantId}, text=\"{e.Text}\". {ex}",
                    LogCategory.UI);
            }
        }

        private void HandlePlayerSpeakingStateChanged(PlayerSpeakingStateChanged e)
        {
            try
            {
                OnEvent(e);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error, LogCategory.UI) == true)
                    _logger?.Error(
                    $"Failed handling PlayerSpeakingStateChanged: isSpeaking={e.IsSpeaking}. {ex}",
                    LogCategory.UI);
            }
        }

        private TurnState FindPlayerTurnForProcessedFinalLocked(FinalUserTranscriptionReceived e)
        {
            if (!string.IsNullOrWhiteSpace(e.MessageId))
            {
                if (_turnsById.TryGetValue(e.MessageId, out TurnState exact) &&
                    exact.Actor.Kind == TranscriptParticipantKind.Player)
                    return exact;

                TurnState segmentMatch = _turnsById.Values
                    .Where(turn => turn.Actor.Kind == TranscriptParticipantKind.Player)
                    .OrderByDescending(turn => turn.RoomSequence)
                    .FirstOrDefault(turn => turn.Segments.Any(segment =>
                        string.Equals(segment.SourceSessionId, e.MessageId, StringComparison.Ordinal)));
                if (segmentMatch != null)
                    return segmentMatch;
            }

            string speakerActorKey = BuildActorKey(
                TranscriptParticipantKind.Player,
                e.SpeakerInfo.SpeakerId,
                e.SpeakerInfo.ParticipantId);

            if (_openPlayerTurnsByActor.TryGetValue(speakerActorKey, out TurnState openBySpeaker))
                return openBySpeaker;

            TurnState activeMatch = _openPlayerTurnsByActor.Values
                .Where(turn => SpeakerMatchesProcessedFinal(turn.Actor, e))
                .OrderByDescending(turn => turn.RoomSequence)
                .FirstOrDefault();
            if (activeMatch != null)
                return activeMatch;

            // A processed final can introduce speaker attribution that was absent from raw ASR.
            // A single open voice turn is therefore a stronger correlation than actor metadata.
            TurnState[] openVoiceCandidates = _openPlayerTurnsByActor.Values
                .Where(IsVoicePlayerTurnAwaitingProcessedFinal)
                .OrderByDescending(turn => turn.RoomSequence)
                .Take(2)
                .ToArray();
            if (openVoiceCandidates.Length == 1)
                return openVoiceCandidates[0];

            TurnState attributedMatch = _turnsById.Values
                .Where(IsVoicePlayerTurnAwaitingProcessedFinal)
                .Where(turn => turn.IsCompleted)
                .Where(turn => SpeakerMatchesProcessedFinal(turn.Actor, e))
                .OrderByDescending(turn => turn.RoomSequence)
                .FirstOrDefault();
            if (attributedMatch != null)
                return attributedMatch;

            TurnState[] unresolvedVoiceCandidates = _turnsById.Values
                .Where(IsVoicePlayerTurnAwaitingProcessedFinal)
                .Where(turn => turn.IsCompleted)
                .OrderByDescending(turn => turn.RoomSequence)
                .Take(2)
                .ToArray();

            if (unresolvedVoiceCandidates.Length == 1)
                return unresolvedVoiceCandidates[0];

            int ambiguousCount = Math.Max(openVoiceCandidates.Length, unresolvedVoiceCandidates.Length);
            if (ambiguousCount > 1)
            {
                if (_logger?.IsEnabled(LogLevel.Warning, LogCategory.UI) == true)
                    _logger?.Warning(
                    $"Ignoring processed final without stable identity because {ambiguousCount} unresolved voice turns are eligible.",
                    LogCategory.UI);
            }

            return null;
        }

        private static bool IsVoicePlayerTurnAwaitingProcessedFinal(TurnState turn) =>
            turn != null &&
            turn.Actor.Kind == TranscriptParticipantKind.Player &&
            string.IsNullOrWhiteSpace(turn.ProcessedOverrideText) &&
            (turn.PrimaryTextSource == TranscriptTextSource.InterimAsr ||
             turn.PrimaryTextSource == TranscriptTextSource.AsrFinal);

        private static bool SpeakerMatchesProcessedFinal(TranscriptParticipantRef actor, FinalUserTranscriptionReceived e)
        {
            bool participantMatches = !string.IsNullOrWhiteSpace(e.ParticipantId) &&
                                      string.Equals(actor.ParticipantId, e.ParticipantId, StringComparison.Ordinal);
            bool speakerMatches = !string.IsNullOrWhiteSpace(e.SpeakerId) &&
                                  string.Equals(actor.PlayerOrCharacterId, e.SpeakerId, StringComparison.Ordinal);
            return participantMatches || speakerMatches;
        }

        private void HandleCharacterSpeechStateChanged(CharacterSpeechStateChanged e)
        {
            try
            {
                OnEvent(e);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error, LogCategory.UI) == true)
                    _logger?.Error(
                    $"Failed handling CharacterSpeechStateChanged: characterId='{e.CharacterId}', isSpeaking={e.IsSpeaking}. {ex}",
                    LogCategory.UI);
            }
        }

        private void HandleCharacterTurnCompleted(CharacterTurnCompleted e)
        {
            try
            {
                OnEvent(e);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error, LogCategory.UI) == true)
                    _logger?.Error(
                    $"Failed handling CharacterTurnCompleted: characterId='{e.CharacterId}', participantId='{e.ParticipantId}', interrupted={e.WasInterrupted}. {ex}",
                    LogCategory.UI);
            }
        }

        private void HandleCharacterTtsTextChunk(CharacterTtsTextChunk e)
        {
            try
            {
                OnEvent(e);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error, LogCategory.UI) == true)
                    _logger?.Error(
                    $"Failed handling CharacterTtsTextChunk: participantId='{e.ParticipantId}', text=\"{e.Text}\". {ex}",
                    LogCategory.UI);
            }
        }

        private TurnState GetOrCreatePlayerTurnLocked(
            TranscriptParticipantRef actor,
            string actorKey,
            DateTime timestamp,
            string preferredTurnId,
            bool allowCompletedExactMatch)
        {
            if (!string.IsNullOrWhiteSpace(preferredTurnId) &&
                _turnsById.TryGetValue(preferredTurnId, out TurnState exactTurn) &&
                exactTurn.Actor.Kind == TranscriptParticipantKind.Player &&
                (!exactTurn.IsCompleted || allowCompletedExactMatch))
                return exactTurn;

            if (_openPlayerTurnsByActor.TryGetValue(actorKey, out TurnState existing) && !existing.IsCompleted)
                return existing;

            TurnState turn = CreateTurnLocked(actor, timestamp, preferredTurnId);
            _openPlayerTurnsByActor[actorKey] = turn;
            return turn;
        }

        private SegmentState GetOrCreatePlayerSegmentLocked(
            TurnState turn,
            string sourceSessionId,
            DateTime timestamp,
            TranscriptionPhase phase)
        {
            string safeSessionId = string.IsNullOrWhiteSpace(sourceSessionId)
                ? $"player-session-{_nextSyntheticId++:D6}"
                : sourceSessionId;

            SegmentState activeSegment = turn.ActiveSegment;

            bool shouldStartNewSegment = activeSegment == null ||
                                         !string.Equals(activeSegment.SourceSessionId, safeSessionId,
                                             StringComparison.Ordinal) ||
                                         (phase == TranscriptionPhase.Interim && activeSegment.CanStartNewSubsegment);

            if (!shouldStartNewSegment) return activeSegment;

            if (activeSegment != null)
                FinalizePlayerSegmentLocked(turn, activeSegment, timestamp);

            string segmentId = $"{safeSessionId}:{turn.NextSegmentIndex++}";
            SegmentState segment = new(
                segmentId,
                safeSessionId,
                turn.Actor,
                timestamp,
                TranscriptSegmentSourceKind.PlayerAsr);

            turn.AddSegment(segment);
            return segment;
        }

        private SegmentState GetOrCreateTypedPlayerSegmentLocked(
            TurnState turn,
            string messageId,
            DateTime timestamp)
        {
            string safeMessageId = string.IsNullOrWhiteSpace(messageId)
                ? $"typed-message-{_nextSyntheticId++:D6}"
                : messageId;

            SegmentState segment = turn.Segments
                .FirstOrDefault(existing => string.Equals(existing.SourceSessionId, safeMessageId, StringComparison.Ordinal));
            if (segment != null)
                return segment;

            segment = new SegmentState(
                $"{safeMessageId}:{turn.NextSegmentIndex++}",
                safeMessageId,
                turn.Actor,
                timestamp,
                TranscriptSegmentSourceKind.PlayerTypedText);

            turn.AddSegment(segment);
            return segment;
        }

        private void ApplyPlayerInterimLocked(TurnState turn, SegmentState segment, string text, DateTime timestamp)
        {
            segment.UpdatedAtUtc = timestamp;
            segment.InterimText = text ?? string.Empty;
            segment.Lifecycle = TranscriptLifecycle.Streaming;
            segment.Actor = turn.Actor;
            segment.SourceKind = TranscriptSegmentSourceKind.PlayerAsr;
            turn.LastUpdatedAtUtc = timestamp;
            turn.State = string.IsNullOrWhiteSpace(text) ? TranscriptTurnState.Listening : TranscriptTurnState.Streaming;
            turn.PrimaryTextSource = TranscriptTextSource.InterimAsr;
            turn.Revision++;
        }

        private void ApplyPlayerAsrFinalLocked(TurnState turn, SegmentState segment, string text, DateTime timestamp)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            segment.UpdatedAtUtc = timestamp;
            segment.SourceKind = TranscriptSegmentSourceKind.PlayerAsr;
            segment.CommittedText = text;
            segment.ProcessedOverrideText = string.Empty;
            segment.InterimText = string.Empty;
            segment.CanStartNewSubsegment = true;
            segment.Lifecycle = TranscriptLifecycle.Stable;
            segment.Actor = turn.Actor;
            turn.LastUpdatedAtUtc = timestamp;
            turn.State = TranscriptTurnState.Stable;
            turn.PrimaryTextSource = TranscriptTextSource.AsrFinal;
            turn.Revision++;
        }

        private bool ApplyPlayerProcessedFinalLocked(TurnState turn, SegmentState segment, string text,
            DateTime timestamp)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            bool alreadyApplied = string.Equals(turn.ProcessedOverrideText, text, StringComparison.Ordinal) &&
                                  turn.PrimaryTextSource == TranscriptTextSource.ProcessedFinal &&
                                  turn.Segments.All(candidate => string.IsNullOrWhiteSpace(candidate.InterimText));
            if (alreadyApplied) return false;

            segment.UpdatedAtUtc = timestamp;
            segment.SourceKind = TranscriptSegmentSourceKind.PlayerProcessedFinal;
            foreach (SegmentState candidate in turn.Segments)
            {
                candidate.ProcessedOverrideText = string.Empty;
                candidate.InterimText = string.Empty;
            }
            segment.ProcessedOverrideText = turn.Segments.Count == 1 ? text : string.Empty;
            segment.CanStartNewSubsegment = true;
            segment.Lifecycle = TranscriptLifecycle.Stable;
            segment.Actor = turn.Actor;
            turn.ProcessedOverrideText = text;
            turn.LastUpdatedAtUtc = timestamp;
            turn.PrimaryTextSource = TranscriptTextSource.ProcessedFinal;
            if (!turn.IsCompleted)
                turn.State = TranscriptTurnState.Stable;
            turn.Revision++;
            return true;
        }

        private void ApplyTypedPlayerTextLocked(TurnState turn, SegmentState segment, string text, DateTime timestamp)
        {
            segment.UpdatedAtUtc = timestamp;
            segment.SourceKind = TranscriptSegmentSourceKind.PlayerTypedText;
            segment.CommittedText = text ?? string.Empty;
            segment.ProcessedOverrideText = string.Empty;
            segment.InterimText = string.Empty;
            segment.CanStartNewSubsegment = false;
            segment.Actor = turn.Actor;
            segment.IsClosed = true;
            segment.StoppedAtUtc = timestamp;
            segment.Lifecycle = TranscriptLifecycle.Completed;

            turn.LastUpdatedAtUtc = timestamp;
            turn.CompletedAtUtc = timestamp;
            turn.WasInterrupted = false;
            turn.State = TranscriptTurnState.Committed;
            turn.PrimaryTextSource = TranscriptTextSource.TypedText;
            turn.ProcessedOverrideText = string.Empty;
            turn.Revision++;
        }

        private void FinalizePlayerSegmentLocked(
            TurnState turn,
            SegmentState segment,
            DateTime timestamp,
            bool keepTurnOpen = true)
        {
            if (segment == null) return;

            segment.UpdatedAtUtc = timestamp;
            segment.StoppedAtUtc = timestamp;

            if (!segment.HasStableText)
            {
                turn.RemoveSegment(segment);
                return;
            }

            segment.InterimText = string.Empty;
            segment.IsClosed = true;
            segment.Lifecycle = keepTurnOpen ? TranscriptLifecycle.Stable : TranscriptLifecycle.Completed;
            turn.LastUpdatedAtUtc = timestamp;
            if (keepTurnOpen)
                turn.State = TranscriptTurnState.Stable;
            turn.Revision++;
        }

        private void ApplyCharacterTranscriptLocked(
            TurnState turn,
            SegmentState segment,
            string text,
            bool isFinal,
            DateTime timestamp,
            TranscriptSegmentSourceKind sourceKind)
        {
            segment.UpdatedAtUtc = timestamp;
            segment.SourceKind = sourceKind;

            if (isFinal)
            {
                segment.CommittedText = TranscriptTextMerge.Merge(segment.CommittedText, text);
                segment.InterimText = string.Empty;
                segment.Lifecycle = TranscriptLifecycle.Stable;
                segment.Actor = turn.Actor;
                turn.LastUpdatedAtUtc = timestamp;
                turn.State = TranscriptTurnState.Stable;
                turn.PrimaryTextSource = sourceKind == TranscriptSegmentSourceKind.LegacyBotTranscript
                    ? TranscriptTextSource.LegacyBotTranscript
                    : TranscriptTextSource.BotOutput;
                turn.Revision++;
                return;
            }

            segment.InterimText = MergeCharacterStreamingText(segment.InterimText, text);
            segment.Lifecycle = TranscriptLifecycle.Streaming;
            segment.Actor = turn.Actor;
            turn.LastUpdatedAtUtc = timestamp;
            turn.State = TranscriptTurnState.Streaming;
            turn.PrimaryTextSource = sourceKind == TranscriptSegmentSourceKind.LegacyBotTranscript
                ? TranscriptTextSource.LegacyBotTranscript
                : TranscriptTextSource.BotOutput;
            turn.Revision++;
        }

        private void CompleteOpenPlayerTurnsLocked(DateTime timestamp, out List<TurnState> completedTurns)
        {
            completedTurns = null;

            foreach (TurnState turn in _openPlayerTurnsByActor.Values.ToArray())
            {
                if (turn.IsCompleted || !turn.HasAnyText) continue;
                CompleteTurnLocked(turn, timestamp, false);
                ClearDuplicateFingerprintsLocked(turn);
                completedTurns ??= new List<TurnState>();
                completedTurns.Add(turn);
            }

            if (completedTurns == null) return;

            foreach (TurnState turn in completedTurns)
                _openPlayerTurnsByActor.Remove(turn.ActorKey);
        }

        private void InterruptOpenCharacterTurnsLocked(DateTime timestamp, out List<TurnState> interruptedTurns)
        {
            interruptedTurns = null;

            foreach (TurnState turn in _openCharacterTurnsByActor.Values.ToArray())
            {
                if (turn.IsCompleted) continue;
                CompleteTurnLocked(turn, timestamp, true);
                ClearDuplicateFingerprintsLocked(turn);
                interruptedTurns ??= new List<TurnState>();
                interruptedTurns.Add(turn);
            }

            if (interruptedTurns == null) return;

            foreach (TurnState turn in interruptedTurns)
                _openCharacterTurnsByActor.Remove(turn.ActorKey);
        }

        private void CompleteCharacterTurnLocked(
            string characterId,
            string participantId,
            DateTime timestamp,
            bool wasInterrupted,
            out List<TurnState> completedTurns)
        {
            completedTurns = null;

            bool hasCharacterId = !string.IsNullOrWhiteSpace(characterId);
            bool hasParticipantId = !string.IsNullOrWhiteSpace(participantId);
            if (!hasCharacterId && !hasParticipantId) return;

            foreach (KeyValuePair<string, TurnState> pair in _openCharacterTurnsByActor.ToArray())
            {
                TurnState turn = pair.Value;
                bool matchesCharacterId = hasCharacterId &&
                                          string.Equals(turn.Actor.PlayerOrCharacterId, characterId,
                                              StringComparison.Ordinal);
                bool matchesParticipantId = hasParticipantId &&
                                            string.Equals(turn.Actor.ParticipantId, participantId,
                                                StringComparison.Ordinal);
                if (!matchesCharacterId && !matchesParticipantId) continue;

                CompleteTurnLocked(turn, timestamp, wasInterrupted);
                ClearDuplicateFingerprintsLocked(turn);
                completedTurns ??= new List<TurnState>();
                completedTurns.Add(turn);
                _openCharacterTurnsByActor.Remove(pair.Key);
            }
        }

        private void CompleteTurnLocked(TurnState turn, DateTime timestamp, bool wasInterrupted)
        {
            if (turn.IsCompleted) return;

            // ActiveSegment is derived from open segments; cache before closing or the getter returns null after IsClosed.
            SegmentState activeSegment = turn.ActiveSegment;
            if (activeSegment != null)
            {
                activeSegment.StoppedAtUtc = timestamp;
                activeSegment.CommittedText = TranscriptTextMerge.Merge(
                    activeSegment.CommittedText,
                    activeSegment.InterimText);
                activeSegment.InterimText = string.Empty;
                activeSegment.Lifecycle = TranscriptLifecycle.Completed;
                activeSegment.IsClosed = true;
            }

            turn.CompletedAtUtc = timestamp;
            turn.WasInterrupted = wasInterrupted;
            turn.LastUpdatedAtUtc = timestamp;
            turn.State = wasInterrupted ? TranscriptTurnState.Interrupted : TranscriptTurnState.Committed;
            if (turn.PrimaryTextSource == TranscriptTextSource.Unknown)
            {
                turn.PrimaryTextSource = turn.Actor.Kind == TranscriptParticipantKind.Character
                    ? TranscriptTextSource.BotOutput
                    : TranscriptTextSource.AsrFinal;
            }

            turn.Revision++;
        }

        private TurnState CreateTurnLocked(
            TranscriptParticipantRef actor,
            DateTime timestamp,
            string preferredTurnId = null,
            string responseId = null)
        {
            string turnId = string.IsNullOrWhiteSpace(preferredTurnId)
                ? $"turn-{_nextSyntheticId++:D6}"
                : preferredTurnId;

            if (_turnsById.ContainsKey(turnId))
                turnId = $"{turnId}-{_nextSyntheticId++:D6}";

            TurnState turn = new(
                this,
                turnId,
                _nextRoomSequence++,
                actor,
                timestamp);

            // Registered before any setter runs, so the first mutation notification finds the turn
            // already in the indexes instead of inserting it twice.
            _turnsById[turnId] = turn;
            _turnsInRoomOrder.Add(turn);
            _activeTurnStates.Add(turn);
            turn.IsIndexedAsActive = true;
            _activeIndexDirty = true;

            if (!string.IsNullOrWhiteSpace(responseId))
                turn.ResponseId = responseId;
            if (!string.IsNullOrWhiteSpace(preferredTurnId))
                turn.DuplicateFingerprintKey = preferredTurnId;

            return turn;
        }

        private bool UpdateTurnActorLocked(TurnState turn, TranscriptParticipantRef actor)
        {
            bool changed = !ParticipantEquals(turn.Actor, actor);
            string oldActorKey = turn.ActorKey;
            turn.Actor = actor;
            string newActorKey = turn.ActorKey;
            foreach (SegmentState segment in turn.Segments)
                segment.Actor = actor;
            if (string.Equals(oldActorKey, newActorKey, StringComparison.Ordinal)) return changed;

            if (_openPlayerTurnsByActor.TryGetValue(oldActorKey, out TurnState openPlayerTurn) &&
                ReferenceEquals(openPlayerTurn, turn))
            {
                _openPlayerTurnsByActor.Remove(oldActorKey);
                _openPlayerTurnsByActor[newActorKey] = turn;
            }

            if (_openCharacterTurnsByActor.TryGetValue(oldActorKey, out TurnState openCharacterTurn) &&
                ReferenceEquals(openCharacterTurn, turn))
            {
                _openCharacterTurnsByActor.Remove(oldActorKey);
                _openCharacterTurnsByActor[newActorKey] = turn;
            }

            return changed;
        }

        private static bool ParticipantEquals(TranscriptParticipantRef left, TranscriptParticipantRef right) =>
            left.Kind == right.Kind &&
            string.Equals(left.PlayerOrCharacterId, right.PlayerOrCharacterId, StringComparison.Ordinal) &&
            string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
            string.Equals(left.ParticipantId, right.ParticipantId, StringComparison.Ordinal);

        private void RemoveTurnLocked(TurnState turn)
        {
            _turnsById.Remove(turn.TurnId);
            _turnsInRoomOrder.Remove(turn);
            turn.IsRemoved = true;
            if (turn.IsIndexedAsActive)
            {
                _activeTurnStates.Remove(turn);
                turn.IsIndexedAsActive = false;
            }

            _activeIndexDirty = true;
            _committedIndexDirty = true;
            _openPlayerTurnsByActor.Remove(turn.ActorKey);
            _openCharacterTurnsByActor.Remove(turn.ActorKey);
            ClearDuplicateFingerprintsLocked(turn);
        }

        /// <summary>
        ///     Single notification point for every turn and segment mutation. Membership of the two
        ///     published index arrays is decided here, so a mutation can never leave a stale snapshot
        ///     in a republished array.
        /// </summary>
        private void OnTurnChangedLocked(TurnState turn)
        {
            // A removed turn must never re-enter an index, whatever a late mutation does to it.
            if (turn.IsRemoved) return;

            bool active = IsActiveState(turn.State);
            if (active != turn.IsIndexedAsActive)
            {
                if (active) InsertActiveTurnLocked(turn);
                else _activeTurnStates.Remove(turn);

                turn.IsIndexedAsActive = active;
                _activeIndexDirty = true;
            }
            else if (active)
            {
                _activeIndexDirty = true;
            }

            if (turn.IsIndexedAsCommitted || IsCommittedState(turn.State))
                _committedIndexDirty = true;
        }

        private void InsertActiveTurnLocked(TurnState turn)
        {
            int index = _activeTurnStates.Count;
            while (index > 0 && _activeTurnStates[index - 1].RoomSequence > turn.RoomSequence)
                index--;

            _activeTurnStates.Insert(index, turn);
        }

        private static bool IsCommittedState(TranscriptTurnState state) =>
            state == TranscriptTurnState.Committed || state == TranscriptTurnState.Interrupted;

        private static bool IsActiveState(TranscriptTurnState state) =>
            state != TranscriptTurnState.Committed &&
            state != TranscriptTurnState.Interrupted &&
            state != TranscriptTurnState.Discarded;

        private TranscriptUpdateBatch BuildBatchLocked(
            IReadOnlyList<TurnState> changedStates,
            IReadOnlyList<string> addedTurnIds,
            IReadOnlyList<string> updatedTurnIds,
            IReadOnlyList<string> completedTurnIds,
            IReadOnlyList<string> interruptedTurnIds,
            IReadOnlyList<string> removedTurnIds,
            IReadOnlyList<string> correctedTurnIds = null)
        {
            _timelineCursor++;
            _currentTimeline = BuildTimelineLocked();

            _changedTurnScratch.Clear();
            if (changedStates != null)
            {
                for (int i = 0; i < changedStates.Count; i++)
                {
                    TurnState turn = changedStates[i];
                    if (turn == null || _changedTurnScratch.Contains(turn)) continue;
                    _changedTurnScratch.Add(turn);
                }
            }

            IReadOnlyList<TranscriptTurnSnapshot> changedTurns = Array.Empty<TranscriptTurnSnapshot>();
            if (_changedTurnScratch.Count > 0)
            {
                var snapshots = new TranscriptTurnSnapshot[_changedTurnScratch.Count];
                for (int i = 0; i < snapshots.Length; i++)
                    snapshots[i] = _changedTurnScratch[i].GetSnapshot();

                SortByRoomSequence(snapshots);
                changedTurns = snapshots;
            }

            IReadOnlyList<string> added = addedTurnIds ?? CollectChangedTurnIdsLocked(
                true,
                completedTurnIds,
                interruptedTurnIds,
                correctedTurnIds,
                null);
            IReadOnlyList<string> updated = updatedTurnIds ?? CollectChangedTurnIdsLocked(
                false,
                completedTurnIds,
                interruptedTurnIds,
                correctedTurnIds,
                added);

            return new TranscriptUpdateBatch(
                _currentTimeline,
                changedTurns,
                added,
                updated,
                completedTurnIds ?? Array.Empty<string>(),
                interruptedTurnIds ?? Array.Empty<string>(),
                correctedTurnIds ?? Array.Empty<string>(),
                removedTurnIds ?? Array.Empty<string>());
        }

        private IReadOnlyList<string> CollectChangedTurnIdsLocked(
            bool firstRevisionOnly,
            IReadOnlyList<string> excludedFirst,
            IReadOnlyList<string> excludedSecond,
            IReadOnlyList<string> excludedThird,
            IReadOnlyList<string> excludedFourth)
        {
            _turnIdScratch.Clear();
            for (int i = 0; i < _changedTurnScratch.Count; i++)
            {
                TurnState turn = _changedTurnScratch[i];
                if (firstRevisionOnly && turn.Revision != 1) continue;

                string turnId = turn.TurnId;
                if (ContainsId(excludedFirst, turnId) ||
                    ContainsId(excludedSecond, turnId) ||
                    ContainsId(excludedThird, turnId) ||
                    ContainsId(excludedFourth, turnId) ||
                    _turnIdScratch.Contains(turnId))
                    continue;

                _turnIdScratch.Add(turnId);
            }

            return _turnIdScratch.Count == 0 ? Array.Empty<string>() : _turnIdScratch.ToArray();
        }

        private static bool ContainsId(IReadOnlyList<string> ids, string id)
        {
            if (ids == null) return false;

            for (int i = 0; i < ids.Count; i++)
                if (string.Equals(ids[i], id, StringComparison.Ordinal))
                    return true;

            return false;
        }

        private static void SortByRoomSequence(TranscriptTurnSnapshot[] snapshots)
        {
            for (int i = 1; i < snapshots.Length; i++)
            {
                TranscriptTurnSnapshot current = snapshots[i];
                int j = i - 1;
                while (j >= 0 && snapshots[j].RoomSequence > current.RoomSequence)
                {
                    snapshots[j + 1] = snapshots[j];
                    j--;
                }

                snapshots[j + 1] = current;
            }
        }

        private static IReadOnlyList<string> TurnIds(
            IReadOnlyList<TurnState> first,
            IReadOnlyList<TurnState> second = null)
        {
            int capacity = (first?.Count ?? 0) + (second?.Count ?? 0);
            if (capacity == 0) return Array.Empty<string>();

            var buffer = new string[capacity];
            int count = AppendTurnIds(first, buffer, 0);
            count = AppendTurnIds(second, buffer, count);
            if (count == 0) return Array.Empty<string>();
            if (count == buffer.Length) return buffer;

            var trimmed = new string[count];
            Array.Copy(buffer, trimmed, count);
            return trimmed;
        }

        private static int AppendTurnIds(IReadOnlyList<TurnState> turns, string[] buffer, int count)
        {
            if (turns == null) return count;

            for (int i = 0; i < turns.Count; i++)
            {
                TurnState turn = turns[i];
                if (turn == null || string.IsNullOrWhiteSpace(turn.TurnId)) continue;

                bool duplicate = false;
                for (int j = 0; j < count; j++)
                {
                    if (!string.Equals(buffer[j], turn.TurnId, StringComparison.Ordinal)) continue;
                    duplicate = true;
                    break;
                }

                if (!duplicate) buffer[count++] = turn.TurnId;
            }

            return count;
        }

        private void PublishBatch(TranscriptUpdateBatch batch)
        {
            if (batch == null) return;
            if (batch.ChangedTurns.Count == 0 && batch.RemovedTurnIds.Count == 0) return;

            SafeEventInvoker.Invoke(
                Changed,
                batch,
                _logger,
                "RoomTranscriptEngine.Changed",
                LogCategory.UI);
        }

        private TranscriptCaptionSnapshot UpdatePlayerCaptionLocked(PlayerTranscriptReceived e)
        {
            if (e.Phase == TranscriptionPhase.ProcessedFinal) return null;

            TranscriptParticipantRef actor = BuildPlayerActor(e);
            TranscriptSpeaker speaker = TranscriptSpeaker.FromParticipant(actor);
            string actorKey = BuildActorKey(actor.Kind, actor.PlayerOrCharacterId, actor.ParticipantId);
            string turnId = ResolveFirstNonEmpty(e.TurnId, e.MessageId, $"caption:{actorKey}");

            _captionsByActor.TryGetValue(actorKey, out CaptionState existing);
            if (existing == null)
            {
                KeyValuePair<string, CaptionState> previousIdentity = _captionsByActor
                    .FirstOrDefault(pair => string.Equals(pair.Value.TurnId, turnId, StringComparison.Ordinal));
                if (previousIdentity.Value != null)
                {
                    existing = previousIdentity.Value;
                    _captionsByActor.Remove(previousIdentity.Key);
                }
            }
            string text = e.Text ?? string.Empty;
            if (e.Phase == TranscriptionPhase.Completed && string.IsNullOrWhiteSpace(text))
                text = existing?.Text ?? string.Empty;

            TranscriptCaptionState state = e.Phase switch
            {
                TranscriptionPhase.AsrFinal => TranscriptCaptionState.Stable,
                TranscriptionPhase.Completed => TranscriptCaptionState.Completed,
                _ => TranscriptCaptionState.Streaming
            };

            _captionsByActor[actorKey] = new CaptionState(turnId, speaker, text, state, e.Timestamp);
            return BuildCaptionSnapshotLocked();
        }

        private TranscriptCaptionSnapshot UpdateCharacterCaptionLocked(CharacterTtsTextChunk e)
        {
            TurnState turn = _openCharacterTurnsByActor.Values
                .Where(candidate => !candidate.IsCompleted)
                .Where(candidate => string.Equals(
                    candidate.Actor.ParticipantId,
                    e.ParticipantId,
                    StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.RoomSequence)
                .FirstOrDefault();

            TranscriptParticipantRef actor = turn?.Actor ?? new TranscriptParticipantRef(
                TranscriptParticipantKind.Character,
                string.Empty,
                "Character",
                e.ParticipantId);
            TranscriptSpeaker speaker = TranscriptSpeaker.FromParticipant(actor);
            string actorKey = BuildActorKey(actor.Kind, actor.PlayerOrCharacterId, actor.ParticipantId);
            string turnId = turn?.TurnId ?? $"caption:{actorKey}";

            _captionsByActor.TryGetValue(actorKey, out CaptionState existing);
            bool continuesExistingCaption = existing != null &&
                                            !existing.IsFinal &&
                                            (string.Equals(existing.TurnId, turnId, StringComparison.Ordinal) ||
                                             IsFallbackCaptionTurnId(existing.TurnId));
            string text = continuesExistingCaption
                ? MergeCharacterStreamingText(existing.Text, e.Text)
                : e.Text;
            TranscriptCaptionState state = e.IsFinal
                ? TranscriptCaptionState.Completed
                : TranscriptCaptionState.Streaming;

            _captionsByActor[actorKey] = new CaptionState(turnId, speaker, text, state, e.Timestamp);
            return BuildCaptionSnapshotLocked();
        }

        private TranscriptCaptionSnapshot FinalizeCaptionsLocked(
            TranscriptSpeakerType speakerType,
            string participantId,
            TranscriptCaptionState state,
            DateTime timestamp)
        {
            bool changed = false;

            foreach (KeyValuePair<string, CaptionState> pair in _captionsByActor.ToArray())
            {
                CaptionState caption = pair.Value;
                if (caption.Speaker?.Type != speakerType || caption.IsFinal) continue;
                if (!string.IsNullOrWhiteSpace(participantId) &&
                    !string.Equals(caption.Speaker?.ParticipantId, participantId, StringComparison.Ordinal))
                    continue;

                _captionsByActor[pair.Key] = caption.WithState(state, timestamp);
                changed = true;
            }

            return changed ? BuildCaptionSnapshotLocked() : null;
        }

        private TranscriptCaptionSnapshot BuildCaptionSnapshotLocked()
        {
            _captionCursor++;
            _currentCaptions = new TranscriptCaptionSnapshot(
                _captionCursor,
                _captionsByActor.Values
                    .OrderBy(caption => caption.UpdatedAtUtc)
                    .Select(caption => caption.ToModel())
                    .ToArray());
            return _currentCaptions;
        }

        private void PublishCaptions(TranscriptCaptionSnapshot captions)
        {
            if (captions == null) return;

            SafeEventInvoker.Invoke(
                CaptionsChanged,
                captions,
                _logger,
                "RoomTranscriptEngine.CaptionsChanged",
                LogCategory.UI);
        }

        private TranscriptTimelineSnapshot BuildTimelineLocked()
        {
            using ProfilerMarker.AutoScope _ = BuildTimelineMarker.Auto();

            if (_committedIndexDirty) RebuildCommittedIndexLocked();
            if (_activeIndexDirty) RebuildActiveIndexLocked();

            // Both overlays are immutable given their inputs, so an update that changed neither side
            // republishes the same instances behind a new cursor.
            _turnIndex ??= new TurnOverlayIndex<TranscriptTurnSnapshot>(
                _committedById,
                _activeSnapshots,
                SnapshotTurnId);
            _latestTurnIndex ??= new LatestTurnOverlayIndex(_latestCommittedByActorKey, _activeLatestEntries);

            return new TranscriptTimelineSnapshot(
                _timelineCursor,
                _activeSnapshots,
                _committedSnapshots,
                _turnIndex,
                _latestTurnIndex);
        }

        /// <summary>
        ///     Rebuilds the settled half of the timeline. Only a commit, interruption, correction,
        ///     rename or removal reaches this; interim updates reuse the published array by reference.
        /// </summary>
        private void RebuildCommittedIndexLocked()
        {
            _committedIndexDirty = false;
            _turnIndex = null;
            _latestTurnIndex = null;

            int count = 0;
            for (int i = 0; i < _turnsInRoomOrder.Count; i++)
                if (IsCommittedState(_turnsInRoomOrder[i].State))
                    count++;

            TranscriptTurnSnapshot[] snapshots = count == 0
                ? Array.Empty<TranscriptTurnSnapshot>()
                : new TranscriptTurnSnapshot[count];
            Dictionary<string, TranscriptTurnSnapshot> byId = new(count, StringComparer.Ordinal);
            Dictionary<string, TranscriptTurnSnapshot> latest = new(StringComparer.Ordinal);
            int index = 0;

            for (int i = 0; i < _turnsInRoomOrder.Count; i++)
            {
                TurnState turn = _turnsInRoomOrder[i];
                bool committed = IsCommittedState(turn.State);
                turn.IsIndexedAsCommitted = committed;
                if (!committed) continue;

                TranscriptTurnSnapshot snapshot = turn.GetSnapshot();
                snapshots[index++] = snapshot;
                byId[snapshot.TurnId] = snapshot;
                latest[turn.ActorKey] = snapshot;
                if (turn.ActorOnlyKey != null) latest[turn.ActorOnlyKey] = snapshot;
            }

            _committedSnapshots = snapshots;
            _committedById = byId;
            _latestCommittedByActorKey = latest;
        }

        private void RebuildActiveIndexLocked()
        {
            _activeIndexDirty = false;
            _turnIndex = null;
            _latestTurnIndex = null;

            int count = _activeTurnStates.Count;
            if (count == 0)
            {
                _activeSnapshots = Array.Empty<TranscriptTurnSnapshot>();
                _activeLatestEntries = Array.Empty<KeyValuePair<string, TranscriptTurnSnapshot>>();
                return;
            }

            var snapshots = new TranscriptTurnSnapshot[count];
            int keyCount = 0;
            for (int i = 0; i < count; i++)
            {
                TurnState turn = _activeTurnStates[i];
                snapshots[i] = turn.GetSnapshot();
                keyCount += turn.ActorOnlyKey == null ? 1 : 2;
            }

            var entries = new KeyValuePair<string, TranscriptTurnSnapshot>[keyCount];
            int index = 0;
            for (int i = 0; i < count; i++)
            {
                TurnState turn = _activeTurnStates[i];
                entries[index++] = new KeyValuePair<string, TranscriptTurnSnapshot>(turn.ActorKey, snapshots[i]);
                if (turn.ActorOnlyKey != null)
                    entries[index++] = new KeyValuePair<string, TranscriptTurnSnapshot>(turn.ActorOnlyKey, snapshots[i]);
            }

            _activeSnapshots = snapshots;
            _activeLatestEntries = entries;
        }

        private static TranscriptParticipantRef BuildPlayerActor(PlayerTranscriptReceived e)
        {
            string actorId = !string.IsNullOrWhiteSpace(e.SpeakerInfo.SpeakerId)
                ? e.SpeakerInfo.SpeakerId
                : e.Message.PlayerOrCharacterId;

            string displayName = !string.IsNullOrWhiteSpace(e.Message.DisplayName)
                ? e.Message.DisplayName
                : e.SpeakerInfo.SpeakerName;

            string participantId = !string.IsNullOrWhiteSpace(e.SpeakerInfo.ParticipantId)
                ? e.SpeakerInfo.ParticipantId
                : e.Message.ParticipantId;

            return new TranscriptParticipantRef(
                TranscriptParticipantKind.Player,
                actorId,
                displayName,
                participantId);
        }

        private static TranscriptParticipantRef BuildPlayerActor(
            FinalUserTranscriptionReceived e,
            string preferredDisplayName)
        {
            string actorId = !string.IsNullOrWhiteSpace(e.SpeakerId) ? e.SpeakerId : "local-player";
            string displayName = PlayerDisplayName.IsAuthored(preferredDisplayName)
                ? preferredDisplayName
                : !string.IsNullOrWhiteSpace(e.SpeakerName)
                    ? e.SpeakerName
                    : PlayerDisplayName.Default;

            return new TranscriptParticipantRef(
                TranscriptParticipantKind.Player,
                actorId,
                displayName,
                e.ParticipantId);
        }

        private static TranscriptParticipantRef BuildCharacterActor(CharacterTranscriptReceived e)
        {
            return new TranscriptParticipantRef(
                TranscriptParticipantKind.Character,
                e.CharacterId,
                e.CharacterName,
                e.Message.ParticipantId);
        }

        private static string BuildActorOnlyKey(TranscriptParticipantRef actor) =>
            string.IsNullOrWhiteSpace(actor.PlayerOrCharacterId)
                ? null
                : $"{actor.Kind}:actor:{actor.PlayerOrCharacterId}";

        private static string BuildActorKey(TranscriptParticipantKind kind, string actorId, string participantId) =>
            !string.IsNullOrWhiteSpace(participantId)
                ? $"{kind}:participant:{participantId}"
                : !string.IsNullOrWhiteSpace(actorId)
                    ? $"{kind}:actor:{actorId}"
                    : $"{kind}:anonymous";

        private static bool IsFallbackCaptionTurnId(string turnId) =>
            !string.IsNullOrWhiteSpace(turnId) &&
            turnId.StartsWith("caption:", StringComparison.Ordinal);

        private bool IsDuplicateUpdateLocked(
            string messageId,
            TranscriptLifecycle lifecycle,
            TranscriptSegmentSourceKind sourceKind,
            string text)
        {
            if (string.IsNullOrWhiteSpace(messageId))
                return false;

            string cacheKey = BuildDuplicateCacheKey(messageId, sourceKind);
            string fingerprint = $"{lifecycle}|{text ?? string.Empty}";
            if (_lastUpdateFingerprintByMessageId.TryGetValue(cacheKey, out string lastFingerprint) &&
                string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal))
                return true;

            if (!_lastUpdateFingerprintByMessageId.ContainsKey(cacheKey))
                _updateFingerprintOrder.Enqueue(cacheKey);
            _lastUpdateFingerprintByMessageId[cacheKey] = fingerprint;

            while (_lastUpdateFingerprintByMessageId.Count > MaxFingerprintEntries &&
                   _updateFingerprintOrder.Count > 0)
            {
                string oldestKey = _updateFingerprintOrder.Dequeue();
                _lastUpdateFingerprintByMessageId.Remove(oldestKey);
            }

            return false;
        }

        private static string BuildDuplicateCacheKey(
            string messageId,
            TranscriptSegmentSourceKind sourceKind) =>
            $"{messageId?.Length ?? 0}:{messageId ?? string.Empty}|{(int)sourceKind}";

        private void ClearDuplicateFingerprintsLocked(TurnState turn)
        {
            if (turn == null) return;

            if (!string.IsNullOrWhiteSpace(turn.TurnId))
                ClearDuplicateFingerprintsForMessageLocked(turn.TurnId);

            if (!string.IsNullOrWhiteSpace(turn.DuplicateFingerprintKey))
                ClearDuplicateFingerprintsForMessageLocked(turn.DuplicateFingerprintKey);
        }

        private void ClearDuplicateFingerprintsForMessageLocked(string messageId)
        {
            foreach (TranscriptSegmentSourceKind sourceKind in TranscriptSourceKinds)
                _lastUpdateFingerprintByMessageId.Remove(BuildDuplicateCacheKey(messageId, sourceKind));
        }

        private static string ResolveCharacterTurnId(CharacterTranscriptReceived e) =>
            ResolveFirstNonEmpty(e.TurnId, e.MessageId, e.ResponseId);

        private static string ResolveFirstNonEmpty(params string[] values)
        {
            if (values == null) return string.Empty;

            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    return value;

            return string.Empty;
        }

        private static string MergeCharacterStreamingText(string existing, string incoming)
        {
            if (string.IsNullOrEmpty(existing)) return incoming ?? string.Empty;
            if (string.IsNullOrEmpty(incoming)) return existing;

            if (incoming.StartsWith(existing, StringComparison.Ordinal)) return incoming;
            if (existing.EndsWith(incoming, StringComparison.Ordinal)) return existing;

            return existing + incoming;
        }

        private static string AppendTurnText(string existing, string incoming)
        {
            return TranscriptTextMerge.Merge(existing, incoming);
        }

        private static string GetTurnDisplayTextLocked(TurnState turn)
        {
            if (turn == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(turn.ProcessedOverrideText)) return turn.ProcessedOverrideText;

            string text = string.Empty;
            foreach (SegmentState segment in turn.Segments)
                text = AppendTurnText(text, segment.DisplayText);
            return text;
        }

        /// <summary>
        ///     Mutable accumulator for one turn. Every setter bumps <see cref="Version" /> and tells the
        ///     engine, which is what makes the published snapshot safe to hand out by reference: an
        ///     unchanged turn returns the same immutable snapshot on the next update.
        /// </summary>
        private sealed class TurnState
        {
            private readonly RoomTranscriptEngine _engine;
            private readonly List<SegmentState> _segments = new();
            private SegmentState _activeSegment;
            private bool _activeSegmentValid = true;
            private TranscriptParticipantRef _actor;
            private string _actorKey;
            private string _actorOnlyKey;
            private DateTime? _completedAtUtc;
            private DateTime _lastUpdatedAtUtc;
            private TranscriptTextSource _primaryTextSource = TranscriptTextSource.Unknown;
            private string _processedOverrideText = string.Empty;
            private string _responseId = string.Empty;
            private int _revision;
            private TranscriptTurnSnapshot _snapshot;
            private int _snapshotVersion = -1;
            private TranscriptTurnState _state = TranscriptTurnState.Listening;
            private int _version;
            private bool _wasInterrupted;

            public TurnState(
                RoomTranscriptEngine engine,
                string turnId,
                long roomSequence,
                TranscriptParticipantRef actor,
                DateTime startedAtUtc)
            {
                _engine = engine;
                TurnId = turnId;
                RoomSequence = roomSequence;
                _actor = actor;
                _actorKey = BuildActorKey(actor.Kind, actor.PlayerOrCharacterId, actor.ParticipantId);
                _actorOnlyKey = BuildActorOnlyKey(actor);
                StartedAtUtc = startedAtUtc;
                _lastUpdatedAtUtc = startedAtUtc;
            }

            public string TurnId { get; }

            public long RoomSequence { get; }

            public DateTime StartedAtUtc { get; }

            public int Version => _version;

            /// <summary>Whether the turn is currently a member of the published active array.</summary>
            public bool IsIndexedAsActive { get; set; }

            /// <summary>Whether the turn was a member of the committed array when it was last built.</summary>
            public bool IsIndexedAsCommitted { get; set; }

            /// <summary>Whether the turn has left the room, after removal or a session reset.</summary>
            public bool IsRemoved { get; set; }

            public TranscriptParticipantRef Actor
            {
                get => _actor;
                set
                {
                    _actor = value;
                    _actorKey = BuildActorKey(value.Kind, value.PlayerOrCharacterId, value.ParticipantId);
                    _actorOnlyKey = BuildActorOnlyKey(value);
                    Touch();
                }
            }

            /// <summary>Participant-scoped index key, kept in step with <see cref="Actor" />.</summary>
            public string ActorKey => _actorKey;

            /// <summary>Actor-scoped index key, or null when the actor has no stable id.</summary>
            public string ActorOnlyKey => _actorOnlyKey;

            public DateTime LastUpdatedAtUtc
            {
                get => _lastUpdatedAtUtc;
                set
                {
                    _lastUpdatedAtUtc = value;
                    Touch();
                }
            }

            public DateTime? CompletedAtUtc
            {
                get => _completedAtUtc;
                set
                {
                    _completedAtUtc = value;
                    Touch();
                }
            }

            public bool WasInterrupted
            {
                get => _wasInterrupted;
                set
                {
                    _wasInterrupted = value;
                    Touch();
                }
            }

            public string ResponseId
            {
                get => _responseId;
                set
                {
                    _responseId = value;
                    Touch();
                }
            }

            public string DuplicateFingerprintKey { get; set; } = string.Empty;

            public string ProcessedOverrideText
            {
                get => _processedOverrideText;
                set
                {
                    _processedOverrideText = value;
                    Touch();
                }
            }

            public TranscriptTurnState State
            {
                get => _state;
                set
                {
                    _state = value;
                    Touch();
                }
            }

            public TranscriptTextSource PrimaryTextSource
            {
                get => _primaryTextSource;
                set
                {
                    _primaryTextSource = value;
                    Touch();
                }
            }

            public int Revision
            {
                get => _revision;
                set
                {
                    _revision = value;
                    Touch();
                }
            }

            public bool IsCompleted => _completedAtUtc.HasValue;

            public int NextSegmentIndex { get; set; } = 1;

            public IReadOnlyList<SegmentState> Segments => _segments;

            /// <summary>
            ///     Last open segment. Cached because snapshot building compares every segment against
            ///     it, which used to rescan the segment list once per segment.
            /// </summary>
            public SegmentState ActiveSegment
            {
                get
                {
                    if (_activeSegmentValid) return _activeSegment;

                    _activeSegment = null;
                    for (int i = _segments.Count - 1; i >= 0; i--)
                    {
                        if (_segments[i].IsClosed) continue;
                        _activeSegment = _segments[i];
                        break;
                    }

                    _activeSegmentValid = true;
                    return _activeSegment;
                }
            }

            public bool HasAnyText
            {
                get
                {
                    for (int i = 0; i < _segments.Count; i++)
                        if (_segments[i].HasText)
                            return true;

                    return false;
                }
            }

            public void Touch()
            {
                _version++;
                _engine.OnTurnChangedLocked(this);
            }

            public void InvalidateActiveSegment() => _activeSegmentValid = false;

            public void AddSegment(SegmentState segment)
            {
                segment.Attach(this);
                _segments.Add(segment);
                _activeSegmentValid = false;
                _lastUpdatedAtUtc = segment.UpdatedAtUtc;
                Touch();
            }

            public void RemoveSegment(SegmentState segment)
            {
                if (_segments.Remove(segment)) segment.Detach();
                _activeSegmentValid = false;
                _lastUpdatedAtUtc = DateTime.UtcNow;
                Touch();
            }

            public SegmentState GetOrCreateCharacterSegment(
                DateTime timestamp,
                TranscriptSegmentSourceKind sourceKind)
            {
                SegmentState segment = ActiveSegment;
                if (segment != null) return segment;

                segment = new SegmentState(
                    $"character-segment-{TurnId}",
                    TurnId,
                    Actor,
                    timestamp,
                    sourceKind);

                AddSegment(segment);
                return segment;
            }

            /// <summary>Immutable projection of this turn, rebuilt only when the turn changed.</summary>
            public TranscriptTurnSnapshot GetSnapshot()
            {
                if (_snapshot != null && _snapshotVersion == _version) return _snapshot;

                _snapshot = BuildSnapshot();
                _snapshotVersion = _version;
                return _snapshot;
            }

            private TranscriptTurnSnapshot BuildSnapshot()
            {
                bool completed = IsCompleted;
                SegmentState active = ActiveSegment;

                TranscriptSegmentSnapshot[] segments = _segments.Count == 0
                    ? Array.Empty<TranscriptSegmentSnapshot>()
                    : new TranscriptSegmentSnapshot[_segments.Count];
                for (int i = 0; i < _segments.Count; i++)
                    segments[i] = _segments[i].GetSnapshot(TurnId, completed);

                string committedText = _processedOverrideText ?? string.Empty;
                string interimText = string.Empty;

                if (string.IsNullOrWhiteSpace(_processedOverrideText))
                {
                    for (int i = 0; i < _segments.Count; i++)
                    {
                        SegmentState segment = _segments[i];
                        if (ReferenceEquals(segment, active) && !segment.IsClosed)
                        {
                            committedText = AppendTurnText(committedText, segment.StableDisplayText);
                            interimText = segment.InterimText ?? string.Empty;
                        }
                        else
                        {
                            committedText = AppendTurnText(committedText, segment.DisplayText);
                        }
                    }
                }

                TranscriptLifecycle lifecycle = completed
                    ? TranscriptLifecycle.Completed
                    : string.IsNullOrWhiteSpace(interimText)
                        ? TranscriptLifecycle.Stable
                        : TranscriptLifecycle.Streaming;

                return new TranscriptTurnSnapshot(
                    TurnId,
                    RoomSequence,
                    _actor,
                    StartedAtUtc,
                    _lastUpdatedAtUtc,
                    _completedAtUtc,
                    lifecycle,
                    committedText,
                    interimText,
                    _wasInterrupted,
                    segments,
                    _responseId,
                    conversationTargetCharacterId: null,
                    state: _state,
                    primaryTextSource: _primaryTextSource,
                    revision: _revision);
            }
        }

        private sealed class CaptionState
        {
            public CaptionState(
                string turnId,
                TranscriptSpeaker speaker,
                string text,
                TranscriptCaptionState state,
                DateTime updatedAtUtc)
            {
                TurnId = turnId ?? string.Empty;
                Speaker = speaker;
                Text = text ?? string.Empty;
                State = state;
                UpdatedAtUtc = updatedAtUtc;
            }

            public string TurnId { get; }
            public TranscriptSpeaker Speaker { get; }
            public string Text { get; }
            public TranscriptCaptionState State { get; }
            public DateTime UpdatedAtUtc { get; }
            public bool IsFinal => State == TranscriptCaptionState.Completed ||
                                   State == TranscriptCaptionState.Interrupted;

            public CaptionState WithState(TranscriptCaptionState state, DateTime timestamp) =>
                new(TurnId, Speaker, Text, state, timestamp);

            public CaptionState WithSpeaker(TranscriptSpeaker speaker) =>
                new(TurnId, speaker, Text, State, UpdatedAtUtc);

            public TranscriptCaption ToModel() => new(TurnId, Speaker, Text, State, UpdatedAtUtc);
        }

        /// <summary>
        ///     Mutable accumulator for one segment. Mutations bump the owning turn so the turn's cached
        ///     snapshot is invalidated together with this segment's own cached projection.
        /// </summary>
        private sealed class SegmentState
        {
            private TranscriptParticipantRef _actor;
            private bool _isClosed;
            private string _committedText = string.Empty;
            private string _interimText = string.Empty;
            private TranscriptLifecycle _lifecycle = TranscriptLifecycle.Streaming;
            private bool _canStartNewSubsegment;
            private TurnState _owner;
            private string _processedOverrideText = string.Empty;
            private TranscriptSegmentSnapshot _snapshot;
            private bool _snapshotTurnCompleted;
            private int _snapshotVersion = -1;
            private TranscriptSegmentSourceKind _sourceKind;
            private DateTime? _stoppedAtUtc;
            private DateTime _updatedAtUtc;
            private int _version;

            public SegmentState(
                string segmentId,
                string sourceSessionId,
                TranscriptParticipantRef actor,
                DateTime startedAtUtc,
                TranscriptSegmentSourceKind sourceKind)
            {
                SegmentId = segmentId;
                SourceSessionId = sourceSessionId;
                _actor = actor;
                StartedAtUtc = startedAtUtc;
                _updatedAtUtc = startedAtUtc;
                _sourceKind = sourceKind;
            }

            public string SegmentId { get; }

            public string SourceSessionId { get; }

            public DateTime StartedAtUtc { get; }

            public TranscriptParticipantRef Actor
            {
                get => _actor;
                set
                {
                    _actor = value;
                    Touch();
                }
            }

            public string CommittedText
            {
                get => _committedText;
                set
                {
                    _committedText = value;
                    Touch();
                }
            }

            public string InterimText
            {
                get => _interimText;
                set
                {
                    _interimText = value;
                    Touch();
                }
            }

            public string ProcessedOverrideText
            {
                get => _processedOverrideText;
                set
                {
                    _processedOverrideText = value;
                    Touch();
                }
            }

            public DateTime UpdatedAtUtc
            {
                get => _updatedAtUtc;
                set
                {
                    _updatedAtUtc = value;
                    Touch();
                }
            }

            public DateTime? StoppedAtUtc
            {
                get => _stoppedAtUtc;
                set
                {
                    _stoppedAtUtc = value;
                    Touch();
                }
            }

            public TranscriptLifecycle Lifecycle
            {
                get => _lifecycle;
                set
                {
                    _lifecycle = value;
                    Touch();
                }
            }

            public TranscriptSegmentSourceKind SourceKind
            {
                get => _sourceKind;
                set
                {
                    _sourceKind = value;
                    Touch();
                }
            }

            public bool CanStartNewSubsegment
            {
                get => _canStartNewSubsegment;
                set
                {
                    _canStartNewSubsegment = value;
                    Touch();
                }
            }

            public bool IsClosed
            {
                get => _isClosed;
                set
                {
                    _isClosed = value;
                    _owner?.InvalidateActiveSegment();
                    Touch();
                }
            }

            public bool HasStableText => !string.IsNullOrWhiteSpace(StableDisplayText);

            public bool HasText => !string.IsNullOrWhiteSpace(DisplayText);

            public string StableDisplayText => !string.IsNullOrWhiteSpace(_processedOverrideText)
                ? _processedOverrideText
                : _committedText ?? string.Empty;

            public string DisplayText
            {
                get
                {
                    if (!string.IsNullOrWhiteSpace(_processedOverrideText) &&
                        string.IsNullOrWhiteSpace(_interimText))
                        return _processedOverrideText;

                    if (string.IsNullOrWhiteSpace(_committedText)) return _interimText ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(_interimText)) return StableDisplayText;

                    return TranscriptTextMerge.Merge(StableDisplayText, _interimText);
                }
            }

            public void Attach(TurnState owner) => _owner = owner;

            public void Detach() => _owner = null;

            public TranscriptLifecycle GetLifecycle(bool turnCompleted)
            {
                if (turnCompleted) return TranscriptLifecycle.Completed;
                if (!string.IsNullOrWhiteSpace(_interimText)) return TranscriptLifecycle.Streaming;
                return TranscriptLifecycle.Stable;
            }

            public TranscriptSegmentSnapshot GetSnapshot(string turnId, bool turnCompleted)
            {
                if (_snapshot != null && _snapshotVersion == _version && _snapshotTurnCompleted == turnCompleted)
                    return _snapshot;

                _snapshot = new TranscriptSegmentSnapshot(
                    SegmentId,
                    turnId,
                    _actor,
                    _committedText,
                    _interimText,
                    _processedOverrideText,
                    StartedAtUtc,
                    _updatedAtUtc,
                    _stoppedAtUtc,
                    GetLifecycle(turnCompleted),
                    _sourceKind);
                _snapshotVersion = _version;
                _snapshotTurnCompleted = turnCompleted;
                return _snapshot;
            }

            private void Touch()
            {
                _version++;
                _owner?.Touch();
            }
        }
    }
}
