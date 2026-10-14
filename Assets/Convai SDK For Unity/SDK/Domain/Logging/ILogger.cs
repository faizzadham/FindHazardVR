using System;
using System.Collections.Generic;

namespace Convai.Domain.Logging
{
    /// <summary>
    ///     Hierarchical log levels for the Convai SDK.
    ///     Lower numeric values = more severe. Standard convention: logs at configured level and below are shown.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>No logging.</summary>
        Off = 0,

        /// <summary>Critical errors that require immediate attention.</summary>
        Error = 1,

        /// <summary>Warnings about potential issues.</summary>
        Warning = 2,

        /// <summary>General informational messages.</summary>
        Info = 3,

        /// <summary>Detailed debugging information.</summary>
        Debug = 4,

        /// <summary>Verbose trace-level logging.</summary>
        Trace = 5
    }

    /// <summary>
    ///     Core logging interface for the Convai SDK.
    /// </summary>
    /// <remarks>
    ///     Supports both simple logging and structured logging with context data.
    ///     Use structured logging overloads when you need to attach key-value pairs
    ///     for better searchability in log aggregation systems.
    /// </remarks>
    internal static class LogLevelPolicy
    {
        internal static bool CanEmit(LogLevel level)
        {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD && !CONVAI_DEBUG_LOGGING
            return level >= LogLevel.Error && level <= LogLevel.Info;
#else
            return level >= LogLevel.Error && level <= LogLevel.Trace;
#endif
        }
    }

    [Flags]
    public enum LogStackTraceLevels
    {
        None = 0, Error = 1 << 1, Warning = 1 << 2, Info = 1 << 3,
        Debug = 1 << 4, Trace = 1 << 5, All = Error | Warning | Info | Debug | Trace
    }

    public interface ILogger
    {
        /// <summary>Deliver an already constructed entry, preserving source and context.</summary>
        void Log(in LogEntry entry);

        /// <summary>
        ///     Logs a message at the specified level with a typed category.
        /// </summary>
        /// <param name="level">The log level.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="category">The log category.</param>
        public void Log(LogLevel level, string message, LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs a message at the specified level with structured context data.
        /// </summary>
        /// <param name="level">The log level.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="context">Key-value pairs to attach to the log entry.</param>
        /// <param name="category">The log category.</param>
        public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs a debug message with a typed category.
        /// </summary>
        public void Debug(string message, LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs a debug message with structured context data.
        /// </summary>
        public void Debug(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs an info message with a typed category.
        /// </summary>
        public void Info(string message, LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs an info message with structured context data.
        /// </summary>
        public void Info(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs a warning message with a typed category.
        /// </summary>
        public void Warning(string message, LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs a warning message with structured context data.
        /// </summary>
        public void Warning(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs an error message with a typed category.
        /// </summary>
        public void Error(string message, LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs an error message with structured context data.
        /// </summary>
        public void Error(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs an error with an exception.
        /// </summary>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Optional additional message.</param>
        /// <param name="category">The log category.</param>
        public void Error(Exception exception, string message = null, LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Logs an error with an exception and structured context data.
        /// </summary>
        /// <param name="exception">The exception to log.</param>
        /// <param name="message">Optional additional message.</param>
        /// <param name="context">Key-value pairs to attach to the log entry.</param>
        /// <param name="category">The log category.</param>
        public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1));

        /// <summary>
        ///     Checks if logging is enabled for the specified level and category.
        ///     Use this to avoid expensive string formatting when logging is disabled.
        /// </summary>
        /// <param name="level">The log level to check.</param>
        /// <param name="category">The log category to check.</param>
        /// <returns>True if logging is enabled for this level and category.</returns>
        public bool IsEnabled(LogLevel level, LogCategory category = (LogCategory)(-1));
    }
}
