using System;
using System.Collections.Generic;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;

namespace Convai.Infrastructure.Networking.Native
{
    /// <summary>Native default when no application logger was injected.</summary>
    internal sealed class NativeTransportFallbackLogger : ILogger
    {
        public void Log(in LogEntry entry) => ConvaiLogger.Instance.Log(in entry);
        public void Log(LogLevel level, string message, LogCategory category = (LogCategory)(-1)) =>
            ConvaiLogger.Instance.Log(level, message, category);
        public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => ConvaiLogger.Instance.Log(level, message, context, category);
        public bool IsEnabled(LogLevel level, LogCategory category = (LogCategory)(-1)) => ConvaiLogger.Instance.IsEnabled(level, category);
        public void Debug(string message, LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Debug, message, category);
        public void Debug(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Debug, message, context, category);
        public void Info(string message, LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Info, message, category);
        public void Info(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Info, message, context, category);
        public void Warning(string message, LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Warning, message, category);
        public void Warning(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Warning, message, context, category);
        public void Error(string message, LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Error, message, category);
        public void Error(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Log(LogLevel.Error, message, context, category);
        public void Error(Exception exception, string message = null, LogCategory category = (LogCategory)(-1)) =>
            ConvaiLogger.Instance.Error(exception, message, category);
        public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => ConvaiLogger.Instance.Error(exception, message, context, category);
    }
}
