using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Convai.Domain.Models
{
    /// <summary>
    ///     Maps engine timeline snapshots to the public turn models, reusing whatever the engine
    ///     republished unchanged.
    /// </summary>
    /// <remarks>
    ///     The engine hands back the same array instance for a half of the timeline it did not touch,
    ///     and the same turn snapshot instance for a turn it did not touch. Both are used here as the
    ///     reuse key, so an interim update maps the open turns only. Mapped models are immutable, so a
    ///     reused model is safe to publish again.
    /// </remarks>
    internal sealed class TranscriptTimelineMapper
    {
        private static readonly Func<TranscriptTurn, string> TurnIdOf = turn => turn.Id;

        // Two buffers swapped on every committed rebuild: the previous mapping supplies the models
        // for turns whose snapshot the engine did not touch, and the emptied one is refilled.
        private Dictionary<TranscriptTurnSnapshot, TranscriptTurn> _committedModels =
            new(SnapshotReferenceComparer.Instance);
        private Dictionary<TranscriptTurnSnapshot, TranscriptTurn> _reusableModels =
            new(SnapshotReferenceComparer.Instance);

        private IReadOnlyList<TranscriptTurnSnapshot> _activeSource;
        private TranscriptTurn[] _activeTurns = Array.Empty<TranscriptTurn>();
        private Dictionary<string, TranscriptTurn> _committedById = new(StringComparer.Ordinal);
        private IReadOnlyList<TranscriptTurnSnapshot> _committedSource;
        private TranscriptTurn[] _committedTurns = Array.Empty<TranscriptTurn>();
        private IReadOnlyDictionary<string, TranscriptTurn> _index;
        private TranscriptTimelineSnapshot _snapshot;
        private TranscriptTimeline _timeline = TranscriptTimeline.Empty;

        public TranscriptTimeline Map(TranscriptTimelineSnapshot snapshot)
        {
            if (snapshot == null) return TranscriptTimeline.Empty;
            if (ReferenceEquals(snapshot, _snapshot)) return _timeline;

            bool remapped = false;

            if (!ReferenceEquals(snapshot.CommittedTurns, _committedSource))
            {
                _committedSource = snapshot.CommittedTurns;
                MapCommittedTurns(snapshot.CommittedTurns);
                remapped = true;
            }

            if (!ReferenceEquals(snapshot.ActiveTurns, _activeSource))
            {
                _activeTurns = MapActiveTurns(snapshot.ActiveTurns);
                _activeSource = snapshot.ActiveTurns;
                remapped = true;
            }

            if (remapped || _index == null)
                _index = new TurnOverlayIndex<TranscriptTurn>(_committedById, _activeTurns, TurnIdOf);

            _timeline = new TranscriptTimeline(snapshot.Cursor, _activeTurns, _committedTurns, _index);
            _snapshot = snapshot;
            return _timeline;
        }

        /// <summary>
        ///     Returns the model already mapped for <paramref name="snapshot" /> when the timeline this
        ///     mapper last produced still carries that exact snapshot, so a change and the timeline it
        ///     arrived with describe one object rather than two copies.
        /// </summary>
        public TranscriptTurn MapChangedTurn(TranscriptTimelineSnapshot source, TranscriptTurnSnapshot snapshot)
        {
            if (snapshot == null) return null;

            if (ReferenceEquals(source, _snapshot) &&
                source != null &&
                source.TurnsById.TryGetValue(snapshot.TurnId, out TranscriptTurnSnapshot published) &&
                ReferenceEquals(published, snapshot) &&
                _timeline.TurnsById.TryGetValue(snapshot.TurnId, out TranscriptTurn mapped))
                return mapped;

            return TranscriptModelMapper.FromSnapshot(snapshot);
        }

        private void MapCommittedTurns(IReadOnlyList<TranscriptTurnSnapshot> snapshots)
        {
            Dictionary<TranscriptTurnSnapshot, TranscriptTurn> previous = _committedModels;
            Dictionary<TranscriptTurnSnapshot, TranscriptTurn> current = _reusableModels;
            current.Clear();

            int count = snapshots?.Count ?? 0;
            _committedTurns = count == 0 ? Array.Empty<TranscriptTurn>() : new TranscriptTurn[count];
            var byId = new Dictionary<string, TranscriptTurn>(count, StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                TranscriptTurnSnapshot source = snapshots[i];
                if (!previous.TryGetValue(source, out TranscriptTurn turn))
                    turn = TranscriptModelMapper.FromSnapshot(source);

                current[source] = turn;
                _committedTurns[i] = turn;
                if (turn != null) byId[turn.Id] = turn;
            }

            _committedById = byId;
            _committedModels = current;
            // This is private reuse bookkeeping, not a published timeline. Release old
            // snapshots now, including when Clear maps an empty committed history.
            previous.Clear();
            _reusableModels = previous;
        }

        private TranscriptTurn[] MapActiveTurns(IReadOnlyList<TranscriptTurnSnapshot> snapshots)
        {
            int count = snapshots?.Count ?? 0;
            if (count == 0) return Array.Empty<TranscriptTurn>();

            var mapped = new TranscriptTurn[count];
            for (int i = 0; i < count; i++)
            {
                TranscriptTurnSnapshot source = snapshots[i];
                int previousIndex = -1;
                for (int j = 0; j < (_activeSource?.Count ?? 0); j++)
                {
                    if (!ReferenceEquals(source, _activeSource[j])) continue;
                    previousIndex = j;
                    break;
                }

                mapped[i] = previousIndex >= 0
                    ? _activeTurns[previousIndex]
                    : TranscriptModelMapper.FromSnapshot(source);
            }

            return mapped;
        }

        private sealed class SnapshotReferenceComparer : IEqualityComparer<TranscriptTurnSnapshot>
        {
            public static readonly SnapshotReferenceComparer Instance = new();

            public bool Equals(TranscriptTurnSnapshot x, TranscriptTurnSnapshot y) => ReferenceEquals(x, y);

            public int GetHashCode(TranscriptTurnSnapshot obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
