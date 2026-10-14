using System.Collections.Generic;
using Convai.Domain.Logging;

namespace Convai.Tests.EditMode
{
    /// <summary>
    ///     Test implementation of ILogSink for unit testing purposes.
    ///     Captures all log entries for verification.
    /// </summary>
    public sealed class TestLogSink : ILogSink
    {
        /// <summary>
        ///     Gets the list of captured log entries.
        /// </summary>
        public List<LogEntry> Entries { get; } = new();

        /// <summary>
        ///     Gets whether Flush was called.
        /// </summary>
        public bool WasFlushed { get; private set; }

        /// <summary>
        ///     Gets whether Dispose was called.
        /// </summary>
        public bool WasDisposed { get; private set; }

        /// <inheritdoc />
        public string Name => "TestSink";

        /// <inheritdoc />
        public bool IsEnabled { get; private set; } = true;

        /// <inheritdoc />
        public bool IsEnabledFor(LogLevel level, LogCategory category) => IsEnabled;
        public void Write(in LogEntry entry) => Entries.Add(entry);

        /// <inheritdoc />
        public void Flush() => WasFlushed = true;

        /// <inheritdoc />
        public void SetEnabled(bool enabled) => IsEnabled = enabled;

        /// <inheritdoc />
        public void Dispose() => WasDisposed = true;

        /// <summary>
        ///     Clears all captured entries and resets state.
        /// </summary>
        public void Clear()
        {
            Entries.Clear();
            WasFlushed = false;
        }

        /// <summary>
        ///     Gets the count of entries at a specific log level.
        /// </summary>
        public int CountByLevel(LogLevel level)
        {
            int count = 0;
            foreach (LogEntry entry in Entries)
                if (entry.Level == level)
                    count++;
            return count;
        }

        /// <summary>
        ///     Gets the count of entries for a specific category.
        /// </summary>
        public int CountByCategory(LogCategory category)
        {
            int count = 0;
            foreach (LogEntry entry in Entries)
                if (entry.Category == category)
                    count++;
            return count;
        }
    }
}
