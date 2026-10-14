using System;
using System.Collections.Generic;

namespace Convai.Domain.Models
{
    /// <summary>
    ///     Immutable room-wide transcript timeline snapshot.
    /// </summary>
    /// <remarks>
    ///     Collections and their entries must never be mutated after publication. Reuse the same
    ///     collection reference only while its contents are unchanged; publish a replacement
    ///     collection and replacement turn snapshot for each visible change. Facade mapping uses
    ///     these references as immutable cache keys.
    /// </remarks>
    public sealed class TranscriptTimelineSnapshot
    {
        public TranscriptTimelineSnapshot(
            long cursor,
            IReadOnlyList<TranscriptTurnSnapshot> activeTurns,
            IReadOnlyList<TranscriptTurnSnapshot> committedTurns,
            IReadOnlyDictionary<string, TranscriptTurnSnapshot> turnsById,
            IReadOnlyDictionary<string, TranscriptTurnSnapshot> latestTurnByParticipant)
        {
            Cursor = cursor;
            ActiveTurns = activeTurns ?? Array.Empty<TranscriptTurnSnapshot>();
            CommittedTurns = committedTurns ?? Array.Empty<TranscriptTurnSnapshot>();
            TurnsById = turnsById ?? new Dictionary<string, TranscriptTurnSnapshot>();
            LatestTurnByParticipant = latestTurnByParticipant ?? new Dictionary<string, TranscriptTurnSnapshot>();
        }

        public static TranscriptTimelineSnapshot Empty { get; } = new(
            0,
            Array.Empty<TranscriptTurnSnapshot>(),
            Array.Empty<TranscriptTurnSnapshot>(),
            new Dictionary<string, TranscriptTurnSnapshot>(),
            new Dictionary<string, TranscriptTurnSnapshot>());

        public long Cursor { get; }

        public IReadOnlyList<TranscriptTurnSnapshot> ActiveTurns { get; }

        public IReadOnlyList<TranscriptTurnSnapshot> CommittedTurns { get; }

        public IReadOnlyDictionary<string, TranscriptTurnSnapshot> TurnsById { get; }

        public IReadOnlyDictionary<string, TranscriptTurnSnapshot> LatestTurnByParticipant { get; }
    }
}
