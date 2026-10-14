using System;
using System.Collections.Generic;

namespace Convai.Domain.Logging
{
    internal sealed class TaggedLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly string _source;
        private readonly LogCategory _category;
        internal TaggedLogger(ILogger inner, string source) : this(inner, source, LogCategory.SDK) { }
        internal TaggedLogger(ILogger inner, string source, LogCategory category)
        { _inner = inner ?? throw new ArgumentNullException(nameof(inner)); _source = source; _category = category; }
        internal ILogger Retag(string source, LogCategory category) => source == _source && category == _category
            ? this : new TaggedLogger(_inner, source, category);
        private LogCategory Resolve(LogCategory category) => (int)category == -1 ? _category : category;
        public bool IsEnabled(LogLevel level, LogCategory category = (LogCategory)(-1)) => LogLevelPolicy.CanEmit(level) && _inner.IsEnabled(level, Resolve(category));
        public void Log(in LogEntry entry) { if (IsEnabled(entry.Level, entry.Category)) _inner.Log(in entry); }
        private void Emit(LogLevel level, string message, LogCategory category,
            IReadOnlyDictionary<string, object> context = null, Exception exception = null)
        {
            category = Resolve(category);
            if (!IsEnabled(level, category)) return;
            var entry = LogEntry.CreateEvent(level, category, message, _source, exception, context);
            _inner.Log(in entry);
        }
        public void Log(LogLevel level, string message, LogCategory category = (LogCategory)(-1)) => Emit(level, message, category);
        public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(level, message, category, context);
        public void Debug(string message, LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Debug, message, category);
        public void Debug(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Debug, message, category, context);
        public void Info(string message, LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Info, message, category);
        public void Info(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Info, message, category, context);
        public void Warning(string message, LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Warning, message, category);
        public void Warning(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Warning, message, category, context);
        public void Error(string message, LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Error, message, category);
        public void Error(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Error, message, category, context);
        public void Error(Exception exception, string message = null, LogCategory category = (LogCategory)(-1)) =>
            Emit(LogLevel.Error, message, category, exception: exception);
        public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Error, message, category, context, exception);
    }
    public static class LoggerTaggingExtensions
    {
        /// <summary>Binds source metadata and the category used when calls omit a category.</summary>
        public static ILogger WithSource(this ILogger logger, string source, LogCategory defaultCategory = LogCategory.SDK)
        {
            if (logger == null || string.IsNullOrWhiteSpace(source)) return logger;
            return logger is TaggedLogger tagged ? tagged.Retag(source, defaultCategory) : new TaggedLogger(logger, source, defaultCategory);
        }
        internal static ILogger WithTag(this ILogger logger, string tag, LogCategory defaultCategory = LogCategory.SDK) =>
            logger.WithSource(tag, defaultCategory);
    }
}
