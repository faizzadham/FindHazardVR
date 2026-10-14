using System;
using System.Collections.Generic;
using Convai.Domain.Logging;

namespace Convai.Tests.EditMode
{
    /// <summary>Captures injected diagnostics without depending on global sinks or console formatting.</summary>
    internal sealed class TestLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public bool IsEnabled(LogLevel level, LogCategory category) => level >= LogLevel.Error && level <= LogLevel.Trace;
        public void Log(in LogEntry entry)
        {
            if (IsEnabled(entry.Level, entry.Category)) Entries.Add(entry);
        }

        public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) =>
            Log(LogEntry.CreateEvent(level, category, message));
        public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) => Log(LogEntry.CreateEvent(level, category, message, context: context));
        public void Debug(string message, LogCategory category = LogCategory.SDK) => Log(LogLevel.Debug, message, category);
        public void Debug(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) =>
            Log(LogLevel.Debug, message, context, category);
        public void Info(string message, LogCategory category = LogCategory.SDK) => Log(LogLevel.Info, message, category);
        public void Info(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) =>
            Log(LogLevel.Info, message, context, category);
        public void Warning(string message, LogCategory category = LogCategory.SDK) => Log(LogLevel.Warning, message, category);
        public void Warning(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) =>
            Log(LogLevel.Warning, message, context, category);
        public void Error(string message, LogCategory category = LogCategory.SDK) => Log(LogLevel.Error, message, category);
        public void Error(string message, IReadOnlyDictionary<string, object> context, LogCategory category = LogCategory.SDK) =>
            Log(LogLevel.Error, message, context, category);
        public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) =>
            Log(LogEntry.CreateEvent(LogLevel.Error, category, message, exception: exception));
        public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) => Log(LogEntry.CreateEvent(LogLevel.Error, category, message, exception: exception, context: context));
    }
}
