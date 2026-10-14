using System;
using System.Collections;
using System.Collections.Generic;

namespace Convai.Domain.Logging
{
    /// <summary>Owned immutable property values; mutable/custom values are frozen as safe text.</summary>
    public sealed class LogProperties : IReadOnlyDictionary<string, object>
    {
        public static readonly LogProperties Empty = new(new Dictionary<string, object>(0));
        private readonly Dictionary<string, object> _values;
        private LogProperties(Dictionary<string, object> values) => _values = values;
        public object this[string key] => _values[key];
        public int Count => _values.Count;
        public IEnumerable<string> Keys => _values.Keys;
        public IEnumerable<object> Values => _values.Values;
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public bool TryGetValue(string key, out object value) => _values.TryGetValue(key, out value);
        public Dictionary<string, object>.Enumerator GetEnumerator() => _values.GetEnumerator();
        IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        internal static object Freeze(object value)
        {
            if (value == null || value is string || value is bool || value is char || value is byte || value is sbyte ||
                value is short || value is ushort || value is int || value is uint || value is long || value is ulong ||
                value is float || value is double || value is decimal || value is DateTime || value is DateTimeOffset ||
                value is TimeSpan || value is Guid || value is Enum) return value;
            return LogDiagnostics.SafeString(value);
        }
        public static LogProperties Snapshot(IReadOnlyDictionary<string, object> values) => Merge(Empty, values);
        internal static LogProperties Merge(LogProperties lower, IReadOnlyDictionary<string, object> higher)
        {
            lower ??= Empty;
            if (higher == null || higher.Count == 0) return lower;
            if (lower.Count == 0 && higher is LogProperties owned) return owned;
            var merged = new Dictionary<string, object>(lower.Count + higher.Count, StringComparer.Ordinal);
            foreach (var item in lower) merged[item.Key] = item.Value;
            try { foreach (var item in higher) merged[item.Key] = Freeze(item.Value); }
            catch { LogDiagnostics.FormattingFailed(); return Empty; }
            return new LogProperties(merged);
        }
        internal LogProperties With(string key, object value, bool remove = false)
        {
            var changed = new Dictionary<string, object>(_values, StringComparer.Ordinal);
            if (remove) changed.Remove(key); else changed[key] = Freeze(value);
            return changed.Count == 0 ? Empty : new LogProperties(changed);
        }
    }
}
