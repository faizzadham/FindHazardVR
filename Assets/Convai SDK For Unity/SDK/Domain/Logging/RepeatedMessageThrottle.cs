using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Convai.Domain.Logging
{
    /// <summary>Explicit repeat suppression, bounded to 1,024 inactive-expiring keys.</summary>
    internal sealed class RepeatedMessageThrottle
    {
        internal const int MaximumKeys = 1024;
        internal const double ExpirationSeconds = 300;
        internal static double ElapsedSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        private struct Entry { internal double Said, Seen; internal int Suppressed; }
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly List<string> _expired = new();
        private readonly object _gate = new();
        private double _nextSweep;
        private static int _generation;
        private int _observedGeneration = Volatile.Read(ref _generation);
        internal static void ConfigurationChanged() => Interlocked.Increment(ref _generation);
        internal RepeatedMessageThrottle(double intervalSeconds) => IntervalSeconds = Math.Max(0, intervalSeconds);
        internal double IntervalSeconds { get; }
        internal int Count { get { lock (_gate) return _entries.Count; } }
        internal bool ShouldSay(string key, double now, out int suppressedSinceLast)
        {
            suppressedSinceLast = 0;
            if (string.IsNullOrEmpty(key)) return false;
            lock (_gate)
            {
                int generation = Volatile.Read(ref _generation);
                if (generation != _observedGeneration)
                { _entries.Clear(); _nextSweep = 0; _observedGeneration = generation; }
                if (now >= _nextSweep)
                {
                    _expired.Clear();
                    foreach (var item in _entries) if (now - item.Value.Seen >= ExpirationSeconds) _expired.Add(item.Key);
                    foreach (string expired in _expired) _entries.Remove(expired);
                    _expired.Clear(); _nextSweep = now + 1;
                }
                bool exists = _entries.TryGetValue(key, out var entry);
                if (exists && now - entry.Seen < ExpirationSeconds && now - entry.Said < IntervalSeconds)
                {
                    entry.Seen = now;
                    if (entry.Suppressed < int.MaxValue) entry.Suppressed++;
                    _entries[key] = entry; return false;
                }
                suppressedSinceLast = exists && now - entry.Seen < ExpirationSeconds ? entry.Suppressed : 0;
                if (!exists && _entries.Count >= MaximumKeys)
                {
                    string oldest = null; double earliest = double.MaxValue;
                    foreach (var item in _entries) if (item.Value.Seen < earliest) { earliest = item.Value.Seen; oldest = item.Key; }
                    if (oldest != null) _entries.Remove(oldest);
                }
                _entries[key] = new Entry { Said = now, Seen = now }; return true;
            }
        }
        internal void Reset() { lock (_gate) { _entries.Clear(); _expired.Clear(); _nextSweep = 0; } }
    }
}
