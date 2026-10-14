using System;
using System.Threading;
using Convai.Domain.Logging;
using UnityEngine;

namespace Convai.Runtime.Logging
{
    /// <summary>Synchronous Unity output, with per-call stack options and an independent filter.</summary>
    public sealed class UnityConsoleSink : ILogSink
    {
        private LoggingConfig.Snapshot _filter = new(LogLevel.Trace, null, false, LogStackTraceLevels.None);
        private volatile bool _enabled = true;
        public string Name => "UnityConsole";
        public bool IsEnabled => _enabled;
        public void SetFilter(LogLevel level, LogLevelOverride[] categories = null) =>
            Volatile.Write(ref _filter, new LoggingConfig.Snapshot(level, categories, false, LogStackTraceLevels.None));
        public bool IsEnabledFor(LogLevel level, LogCategory category)
        {
            var filter = Volatile.Read(ref _filter);
            return _enabled && level >= LogLevel.Error && level <= LogLevel.Trace &&
                (uint)category < filter.Levels.Length && level <= filter.Levels[(int)category];
        }
        public void Write(in LogEntry entry)
        {
            if (!IsEnabledFor(entry.Level, entry.Category)) return;
            var config = LoggingConfig.Current;
            string message = LogFormatting.Format(in entry, config.Colored, config.Stacks);
            LogType type = entry.Level == LogLevel.Error ? LogType.Error : entry.Level == LogLevel.Warning ? LogType.Warning : LogType.Log;
            bool stack = (config.Stacks & (LogStackTraceLevels)(1 << (int)entry.Level)) != 0;
            // Exceptions already contain their original stack; Unity must not append another.
            Debug.LogFormat(type, stack && !entry.HasException ? LogOption.None : LogOption.NoStacktrace, null, "{0}", message);
        }
        public void SetEnabled(bool enabled) => _enabled = enabled;
        public void Flush() { }
        public void Dispose() => _enabled = false;
    }
}
