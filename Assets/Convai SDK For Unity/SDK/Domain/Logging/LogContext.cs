using System;
using System.Collections.Generic;
using System.Threading;

namespace Convai.Domain.Logging
{
    /// <summary>Copy-on-write global and async scoped context. Explicit properties win over both.</summary>
    public static class LogContext
    {
        private static readonly object GlobalLock = new();
        private static LogProperties _global = LogProperties.Empty;
        private static readonly AsyncLocal<LogProperties> Scoped = new();
        private static readonly AsyncLocal<string> Correlation = new();
        [ThreadStatic] private static LogProperties _cachedGlobal, _cachedScoped, _combined;
        public static string CorrelationId { get => Correlation.Value; set => Correlation.Value = value; }
        public static IDisposable PushCorrelationId(string correlationId = null)
        {
            var scope = new CorrelationScope(Correlation.Value);
            Correlation.Value = correlationId ?? Guid.NewGuid().ToString("N");
            return scope;
        }
        public static void SetGlobalProperty(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) return;
            value = LogProperties.Freeze(value);
            lock (GlobalLock) Volatile.Write(ref _global, _global.With(key, value));
        }
        public static void RemoveGlobalProperty(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (GlobalLock) Volatile.Write(ref _global, _global.With(key, null, true));
        }
        public static IReadOnlyDictionary<string, object> GetGlobalProperties() => Volatile.Read(ref _global);
        public static void SetScopedProperty(string key, object value)
        {
            if (!string.IsNullOrEmpty(key)) Scoped.Value = (Scoped.Value ?? LogProperties.Empty).With(key, value);
        }
        public static IReadOnlyDictionary<string, object> GetScopedProperties() => Scoped.Value;
        public static LogProperties GetAllProperties()
        {
            var global = Volatile.Read(ref _global);
            var scoped = Scoped.Value;
            if (ReferenceEquals(global, _cachedGlobal) && ReferenceEquals(scoped, _cachedScoped)) return _combined;
            _combined = LogProperties.Merge(global, scoped);
            _cachedGlobal = global; _cachedScoped = scoped;
            return _combined;
        }
        public static void ClearGlobalProperties() { lock (GlobalLock) Volatile.Write(ref _global, LogProperties.Empty); }
        public static void ClearScopedProperties() => Scoped.Value = null;
        public static void ClearAll()
        { Correlation.Value = null; ClearGlobalProperties(); ClearScopedProperties(); _cachedGlobal = _cachedScoped = _combined = null; }
        private sealed class CorrelationScope : IDisposable
        {
            private readonly string _previous;
            private bool _disposed;
            internal CorrelationScope(string previous) => _previous = previous;
            public void Dispose() { if (_disposed) return; _disposed = true; Correlation.Value = _previous; }
        }
    }
}
