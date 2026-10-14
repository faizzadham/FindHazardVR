using System;
using System.Threading;
using Convai.Domain.Logging;
using UnityEngine;

namespace Convai.Runtime.Logging
{
    /// <summary>Published configuration. Logging threads only read managed immutable data.</summary>
    public static class LoggingConfig
    {
        internal sealed class Snapshot
        {
            internal readonly LogLevel Global;
            internal readonly LogLevel[] Levels;
            internal readonly bool Colored;
            internal readonly LogStackTraceLevels Stacks;
            internal Snapshot(LogLevel global, LogLevelOverride[] overrides, bool colored, LogStackTraceLevels stacks)
            {
                Global = global;
                Colored = colored;
                Stacks = stacks;
                int maximum = 0;
                foreach (LogCategory category in Enum.GetValues(typeof(LogCategory))) maximum = Math.Max(maximum, (int)category);
                Levels = new LogLevel[maximum + 1];
                for (int i = 0; i < Levels.Length; i++) Levels[i] = global;
                if (overrides != null) foreach (var item in overrides)
                    if ((uint)item.Category < (uint)Levels.Length) Levels[(int)item.Category] = item.Level;
            }
        }
        private static Snapshot _current = new(LogLevel.Info, null, true, LogStackTraceLevels.Error);
        internal static Snapshot Current => Volatile.Read(ref _current);
        public static LogLevel GlobalLogLevel => Current.Global;
        public static bool ColoredOutput => Current.Colored;
        public static LogStackTraceLevels StackTraceLevels => Current.Stacks;
        [Obsolete("Use StackTraceLevels.")]
        public static bool IncludeStackTraces => Current.Stacks != LogStackTraceLevels.None;

        /// <summary>Call from main-thread settings/bootstrap code, never from a logging worker.</summary>
        public static void InvalidateCache() => Publish(ConvaiSettings.Instance);
        internal static void Publish(ConvaiSettings settings)
        {
            var snapshot = settings == null ? new Snapshot(LogLevel.Info, null, true, LogStackTraceLevels.Error)
                : new Snapshot(settings.GlobalLogLevel, settings.CopyCategoryOverrides(), settings.ColoredOutput, settings.StackTraceLevels);
            Volatile.Write(ref _current, snapshot);
            RepeatedMessageThrottle.ConfigurationChanged();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap() { InvalidateCache(); ConvaiLogger.Initialize(); }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void EditorBootstrap() => UnityEditor.EditorApplication.delayCall += Bootstrap;
#endif
        public static LogLevel GetLogLevel(LogCategory category)
        {
            var snapshot = Current;
            return (uint)category < (uint)snapshot.Levels.Length ? snapshot.Levels[(int)category] : LogLevel.Off;
        }
        public static bool IsEnabled(LogLevel level, LogCategory category) => ConvaiLogger.Instance.IsEnabled(level, category);
        internal static bool Allows(LogLevel level, LogCategory category)
        {
            if (!LogLevelPolicy.CanEmit(level)) return false;
            var snapshot = Current;
            return (uint)category < (uint)snapshot.Levels.Length && level <= snapshot.Levels[(int)category];
        }
        public static bool IsDebugEnabled(LogCategory category) => IsEnabled(LogLevel.Debug, category);
        public static bool IsInfoEnabled(LogCategory category) => IsEnabled(LogLevel.Info, category);
        public static bool IsWarningEnabled(LogCategory category) => IsEnabled(LogLevel.Warning, category);
        public static bool IsErrorEnabled(LogCategory category) => IsEnabled(LogLevel.Error, category);
    }
}
