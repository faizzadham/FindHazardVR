using System;
using System.Collections.Generic;
using Convai.Domain.Logging;

namespace Convai.Tests.EditMode.Fixtures
{
    internal sealed class SilentLogger : ILogger
    {
        public void Log(in Convai.Domain.Logging.LogEntry entry)
        {
            if (entry.HasException) { Error(entry.Exception, entry.Message, entry.Context, entry.Category); return; }
            switch (entry.Level)
            {
                case Convai.Domain.Logging.LogLevel.Debug:
                case Convai.Domain.Logging.LogLevel.Trace: Debug(entry.Message, entry.Context, entry.Category); break;
                case Convai.Domain.Logging.LogLevel.Info: Info(entry.Message, entry.Context, entry.Category); break;
                case Convai.Domain.Logging.LogLevel.Warning: Warning(entry.Message, entry.Context, entry.Category); break;
                case Convai.Domain.Logging.LogLevel.Error: Error(entry.Message, entry.Context, entry.Category); break;
            }
        }
        public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }
        public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) { }
        public void Debug(string message, LogCategory category = LogCategory.SDK) { }
        public void Debug(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) { }
        public void Info(string message, LogCategory category = LogCategory.SDK) { }
        public void Info(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) { }
        public void Warning(string message, LogCategory category = LogCategory.SDK) { }
        public void Warning(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) { }
        public void Error(string message, LogCategory category = LogCategory.SDK) { }
        public void Error(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) { }
        public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) { }
        public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = LogCategory.SDK) { }
        public bool Enabled { get; set; } = true;
        public bool IsEnabled(LogLevel level, LogCategory category) => Enabled;
    }
}
