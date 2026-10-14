using System;
using System.Collections;
using System.Collections.Generic;

namespace Convai.Domain.Models
{
    /// <summary>
    ///     Read-only union of an immutable index over settled turns and the handful of turns that are
    ///     still open. Publishing a timeline therefore costs the open turns, not the whole history.
    /// </summary>
    /// <remarks>
    ///     An open turn wins over a settled entry with the same id, and the first open entry wins over
    ///     a later one, which is the precedence the previously copied dictionaries produced.
    /// </remarks>
    internal sealed class TurnOverlayIndex<T> : IReadOnlyDictionary<string, T> where T : class
    {
        private readonly IReadOnlyList<T> _open;
        private readonly Func<T, string> _keyOf;
        private readonly IReadOnlyDictionary<string, T> _settled;

        public TurnOverlayIndex(
            IReadOnlyDictionary<string, T> settled,
            IReadOnlyList<T> open,
            Func<T, string> keyOf)
        {
            _settled = settled ?? throw new ArgumentNullException(nameof(settled));
            _open = open ?? (IReadOnlyList<T>)Array.Empty<T>();
            _keyOf = keyOf ?? throw new ArgumentNullException(nameof(keyOf));
        }

        public int Count
        {
            get
            {
                int count = _settled.Count;
                for (int i = 0; i < _open.Count; i++)
                {
                    string key = KeyAt(i);
                    if (key == null || IndexOfOpen(key) != i || _settled.ContainsKey(key)) continue;
                    count++;
                }

                return count;
            }
        }

        public T this[string key] =>
            TryGetValue(key, out T value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys
        {
            get
            {
                foreach (KeyValuePair<string, T> pair in this)
                    yield return pair.Key;
            }
        }

        public IEnumerable<T> Values
        {
            get
            {
                foreach (KeyValuePair<string, T> pair in this)
                    yield return pair.Value;
            }
        }

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out T value)
        {
            if (key != null)
            {
                int index = IndexOfOpen(key);
                if (index >= 0)
                {
                    value = _open[index];
                    return true;
                }
            }

            return _settled.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, T>> GetEnumerator()
        {
            for (int i = 0; i < _open.Count; i++)
            {
                string key = KeyAt(i);
                if (key == null || IndexOfOpen(key) != i) continue;
                yield return new KeyValuePair<string, T>(key, _open[i]);
            }

            foreach (KeyValuePair<string, T> pair in _settled)
            {
                if (IndexOfOpen(pair.Key) >= 0) continue;
                yield return pair;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private string KeyAt(int index)
        {
            T candidate = _open[index];
            return candidate == null ? null : _keyOf(candidate);
        }

        private int IndexOfOpen(string key)
        {
            for (int i = 0; i < _open.Count; i++)
            {
                if (string.Equals(KeyAt(i), key, StringComparison.Ordinal)) return i;
            }

            return -1;
        }
    }

    /// <summary>
    ///     Read-only union of the latest settled turn per participant key and the open turns, resolved
    ///     by room sequence so the newest turn wins whichever side it came from.
    /// </summary>
    internal sealed class LatestTurnOverlayIndex : IReadOnlyDictionary<string, TranscriptTurnSnapshot>
    {
        private readonly KeyValuePair<string, TranscriptTurnSnapshot>[] _open;
        private readonly IReadOnlyDictionary<string, TranscriptTurnSnapshot> _settled;

        public LatestTurnOverlayIndex(
            IReadOnlyDictionary<string, TranscriptTurnSnapshot> settled,
            KeyValuePair<string, TranscriptTurnSnapshot>[] open)
        {
            _settled = settled ?? throw new ArgumentNullException(nameof(settled));
            _open = open ?? Array.Empty<KeyValuePair<string, TranscriptTurnSnapshot>>();
        }

        public int Count
        {
            get
            {
                int count = _settled.Count;
                for (int i = 0; i < _open.Length; i++)
                {
                    string key = _open[i].Key;
                    if (FirstOpenIndex(key) != i || _settled.ContainsKey(key)) continue;
                    count++;
                }

                return count;
            }
        }

        public TranscriptTurnSnapshot this[string key] =>
            TryGetValue(key, out TranscriptTurnSnapshot value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys
        {
            get
            {
                foreach (KeyValuePair<string, TranscriptTurnSnapshot> pair in this)
                    yield return pair.Key;
            }
        }

        public IEnumerable<TranscriptTurnSnapshot> Values
        {
            get
            {
                foreach (KeyValuePair<string, TranscriptTurnSnapshot> pair in this)
                    yield return pair.Value;
            }
        }

        public bool ContainsKey(string key) => TryGetValue(key, out _);

        public bool TryGetValue(string key, out TranscriptTurnSnapshot value)
        {
            TranscriptTurnSnapshot best = null;
            if (key != null)
            {
                for (int i = 0; i < _open.Length; i++)
                {
                    if (!string.Equals(_open[i].Key, key, StringComparison.Ordinal)) continue;
                    TranscriptTurnSnapshot candidate = _open[i].Value;
                    if (candidate != null && (best == null || candidate.RoomSequence > best.RoomSequence))
                        best = candidate;
                }
            }

            if (_settled.TryGetValue(key, out TranscriptTurnSnapshot settled) &&
                (best == null || settled.RoomSequence > best.RoomSequence))
                best = settled;

            value = best;
            return best != null;
        }

        public IEnumerator<KeyValuePair<string, TranscriptTurnSnapshot>> GetEnumerator()
        {
            for (int i = 0; i < _open.Length; i++)
            {
                string key = _open[i].Key;
                if (FirstOpenIndex(key) != i) continue;
                TryGetValue(key, out TranscriptTurnSnapshot value);
                yield return new KeyValuePair<string, TranscriptTurnSnapshot>(key, value);
            }

            foreach (KeyValuePair<string, TranscriptTurnSnapshot> pair in _settled)
            {
                if (FirstOpenIndex(pair.Key) >= 0) continue;
                yield return pair;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private int FirstOpenIndex(string key)
        {
            for (int i = 0; i < _open.Length; i++)
            {
                if (string.Equals(_open[i].Key, key, StringComparison.Ordinal)) return i;
            }

            return -1;
        }
    }
}
