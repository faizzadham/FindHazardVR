using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Convai.Domain.Logging;
using Convai.Runtime.Core.Async;
using Unity.Profiling;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Runtime.Logging
{
    /// <summary>Synchronous logging facade. All paths share filtering and entry construction.</summary>
    public sealed class ConvaiLogger : ILogger
    {
        private static readonly ConvaiLogger Singleton = new();
        private static readonly object RegistryLock = new();
        private static readonly ConditionalWeakTable<ILogSink, object> RetiredSinks = new();
        private static volatile Registration[] Sinks = { new(new UnityConsoleSink()) };
        [ThreadStatic] private static int _sinkDepth;
        private static readonly ConcurrentDictionary<string, string> Sources = new(StringComparer.Ordinal);
        private static readonly Func<string, string> SourceFactory = CreateSourceTag;
        private static readonly ProfilerMarker LogMarker = new("ConvaiLogger.LogMessage");
        private static int _initialized;
        public static Action<ConvaiLogger> OnInitializationCompleted = delegate { };
        public static ConvaiLogger Instance => Singleton;
        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0) OnInitializationCompleted?.Invoke(Singleton);
        }
        private sealed class Registration
        {
            internal readonly ILogSink Sink;
            private readonly object _gate = new();
            internal volatile bool Retired;
            private int _failures;
            private bool _disposed;
            internal Registration(ILogSink sink) => Sink = sink;
            internal void Restore() => Interlocked.Exchange(ref _failures, 0);
            private void Failed() { Interlocked.Increment(ref _failures); LogDiagnostics.SinkFailed(); }
            internal bool Accepts(LogLevel level, LogCategory category)
            {
                if (Retired || Volatile.Read(ref _failures) >= 3 || _sinkDepth != 0) return false;
                // Filters are thread-safe metadata reads. Only delivery/lifetime operations need
                // the gate; Write rechecks the filter after acquiring ownership of the sink.
                _sinkDepth++;
                try { return Sink.IsEnabled && Sink.IsEnabledFor(level, category) && !Retired; }
                catch { Failed(); return false; }
                finally { _sinkDepth--; }
            }
            internal void Write(in LogEntry entry)
            {
                lock (_gate)
                {
                    if (Retired || _disposed || _failures >= 3) return;
                    _sinkDepth++;
                    try
                    {
                        if (Sink.IsEnabled && Sink.IsEnabledFor(entry.Level, entry.Category))
                        { Sink.Write(in entry); _failures = 0; }
                    }
                    catch { Failed(); }
                    finally { _sinkDepth--; }
                }
            }
            internal void Flush()
            {
                lock (_gate)
                {
                    if (Retired || _disposed || _failures >= 3) return;
                    _sinkDepth++;
                    try { Sink.Flush(); } catch { Failed(); }
                    finally { _sinkDepth--; }
                }
            }
            internal void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _sinkDepth++;
                    try { Sink.Dispose(); } catch { Failed(); }
                    finally { _sinkDepth--; }
                }
            }
            internal void CompleteRetirement()
            {
                // A callback cannot synchronously wait on another producer's callback: both
                // may be removing sinks. Only disposal is deferred; log delivery stays synchronous.
                // A WebGL player has no thread pool, so there the next player-loop tick disposes it.
                if (_sinkDepth == 0) Dispose();
                else if (ConvaiTimers.UsePlayerLoop) ConvaiTimers.Queue.Schedule(0, Dispose);
                else ThreadPool.QueueUserWorkItem(_ => Dispose());
            }
        }
        public static int SinkCount => Sinks.Length;
        public static void RegisterSink(ILogSink sink)
        {
            if (sink == null) return;
            lock (RegistryLock)
            {
                foreach (var registration in Sinks)
                    if (ReferenceEquals(registration.Sink, sink)) { registration.Restore(); return; }
                if (RetiredSinks.TryGetValue(sink, out _))
                    throw new ObjectDisposedException(sink.Name, "A retired sink cannot be registered again. Create a new sink instance.");
                var next = new Registration[Sinks.Length + 1];
                Array.Copy(Sinks, next, Sinks.Length); next[next.Length - 1] = new Registration(sink); Sinks = next;
            }
        }
        /// <summary>Retires and disposes a sink after any active write finishes.</summary>
        public static void UnregisterSink(ILogSink sink)
        {
            Registration retired = null;
            lock (RegistryLock)
            {
                var next = new List<Registration>(Sinks);
                foreach (var registration in Sinks)
                    if (ReferenceEquals(registration.Sink, sink))
                    { retired = registration; retired.Retired = true; RetiredSinks.GetValue(sink, _ => new object()); next.Remove(registration); break; }
                if (retired != null) Sinks = next.ToArray();
            }
            retired?.CompleteRetirement();
        }
        public static void ClearSinks()
        {
            Registration[] old;
            lock (RegistryLock)
            {
                old = Sinks;
                foreach (var registration in old)
                { registration.Retired = true; RetiredSinks.GetValue(registration.Sink, _ => new object()); }
                Sinks = Array.Empty<Registration>();
            }
            foreach (var registration in old) registration.CompleteRetirement();
        }
        public static void FlushAllSinks()
        {
            if (_sinkDepth != 0) return;
            foreach (var registration in Sinks) registration.Flush();
        }
        public static void Shutdown() { FlushAllSinks(); ClearSinks(); }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetSession()
        {
            ClearSinks(); LogContext.ClearAll(); Sources.Clear(); RepeatedMessageThrottle.ConfigurationChanged();
            RegisterSink(new UnityConsoleSink());
            Interlocked.Exchange(ref _initialized, 0);
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallCleanup()
        {
            UnityEngine.Application.quitting -= Shutdown;
            UnityEngine.Application.quitting += Shutdown;
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallEditorCleanup()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            UnityEditor.EditorApplication.quitting -= Shutdown;
            UnityEditor.EditorApplication.quitting += Shutdown;
        }
#endif
        private static LogCategory ResolveCategory(LogCategory category) => (int)category == -1 ? LogCategory.SDK : category;
        private static bool ShouldLog(LogLevel level, LogCategory category)
        {
            if (_sinkDepth != 0) { LogDiagnostics.RecursiveEntry(); return false; }
            if (!LoggingConfig.Allows(level, category)) return false;
            foreach (var sink in Sinks) if (sink.Accepts(level, category)) return true;
            return false;
        }
        public bool IsEnabled(LogLevel level, LogCategory category = (LogCategory)(-1)) => ShouldLog(level, ResolveCategory(category));
        internal static string ResolveSourceTag(string path) => string.IsNullOrEmpty(path) ? null : Sources.GetOrAdd(path, SourceFactory);
        private static string CreateSourceTag(string path)
        {
            int separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            string name = Path.GetFileNameWithoutExtension(path.Substring(separator + 1));
            int partial = name.IndexOf('.');
            return partial > 0 ? name.Substring(0, partial) : name;
        }
        private static void Emit(LogLevel level, string message, LogCategory category,
            IReadOnlyDictionary<string, object> context = null, Exception exception = null, string file = null)
        {
            category = ResolveCategory(category);
            if (!ShouldLog(level, category)) return;
            EmitAccepted(level, message, category, context, exception, ResolveSourceTag(file));
        }
        private static void EmitAccepted(LogLevel level, string message, LogCategory category,
            IReadOnlyDictionary<string, object> context = null, Exception exception = null, string source = null)
        {
            using var marker = LogMarker.Auto();
            var entry = LogEntry.CreateEvent(level, category, message, source, exception, context);
            Dispatch(in entry);
        }
        public void Log(in LogEntry entry)
        {
            if (ShouldLog(entry.Level, entry.Category)) Dispatch(in entry);
        }
        private static void Dispatch(in LogEntry entry)
        {
            if (_sinkDepth != 0) { LogDiagnostics.RecursiveEntry(); return; }
            foreach (var sink in Sinks) sink.Write(in entry);
        }
        public void Log(LogLevel level, string message, LogCategory category = (LogCategory)(-1)) => Emit(level, message, category);
        public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(level, message, category, context);
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Trace(string message, LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Trace, message, category, file: callerFilePath);
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Trace(object message, LogCategory category, [CallerFilePath] string callerFilePath = "")
        {
            if (!ShouldLog(LogLevel.Trace, category)) return;
            EmitAccepted(LogLevel.Trace, LogDiagnostics.SafeString(message), category, source: ResolveSourceTag(callerFilePath));
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void TraceWithContext(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Trace, message, category, context, file: callerFilePath);
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Trace<T0>(LogCategory category, string template, T0 arg0,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Trace, category)) return;
            string message = LogDiagnostics.Format(template, arg0);
            EmitAccepted(LogLevel.Trace, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Trace<T0, T1>(LogCategory category, string template, T0 arg0, T1 arg1,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Trace, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1);
            EmitAccepted(LogLevel.Trace, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Trace<T0, T1, T2>(LogCategory category, string template, T0 arg0, T1 arg1, T2 arg2,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Trace, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1, arg2);
            EmitAccepted(LogLevel.Trace, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Debug(string message, LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Debug, message, category, file: callerFilePath);
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Debug(object message, LogCategory category, [CallerFilePath] string callerFilePath = "")
        {
            if (!ShouldLog(LogLevel.Debug, category)) return;
            EmitAccepted(LogLevel.Debug, LogDiagnostics.SafeString(message), category, source: ResolveSourceTag(callerFilePath));
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void DebugWithContext(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Debug, message, category, context, file: callerFilePath);
        void ILogger.Debug(string message, LogCategory category) => Emit(LogLevel.Debug, message, category);
        public void Debug(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Debug, message, category, context);
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Debug<T0>(LogCategory category, string template, T0 arg0,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Debug, category)) return;
            string message = LogDiagnostics.Format(template, arg0);
            EmitAccepted(LogLevel.Debug, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Debug<T0, T1>(LogCategory category, string template, T0 arg0, T1 arg1,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Debug, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1);
            EmitAccepted(LogLevel.Debug, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD"), Conditional("CONVAI_DEBUG_LOGGING")]
        public static void Debug<T0, T1, T2>(LogCategory category, string template, T0 arg0, T1 arg1, T2 arg2,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Debug, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1, arg2);
            EmitAccepted(LogLevel.Debug, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Info(string message, LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Info, message, category, file: callerFilePath);
        public static void Info(object message, LogCategory category, [CallerFilePath] string callerFilePath = "")
        {
            if (!ShouldLog(LogLevel.Info, category)) return;
            EmitAccepted(LogLevel.Info, LogDiagnostics.SafeString(message), category, source: ResolveSourceTag(callerFilePath));
        }
        public static void InfoWithContext(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Info, message, category, context, file: callerFilePath);
        void ILogger.Info(string message, LogCategory category) => Emit(LogLevel.Info, message, category);
        public void Info(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Info, message, category, context);
        public static void Info<T0>(LogCategory category, string template, T0 arg0,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Info, category)) return;
            string message = LogDiagnostics.Format(template, arg0);
            EmitAccepted(LogLevel.Info, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Info<T0, T1>(LogCategory category, string template, T0 arg0, T1 arg1,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Info, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1);
            EmitAccepted(LogLevel.Info, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Info<T0, T1, T2>(LogCategory category, string template, T0 arg0, T1 arg1, T2 arg2,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Info, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1, arg2);
            EmitAccepted(LogLevel.Info, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Warning(string message, LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Warning, message, category, file: callerFilePath);
        public static void Warning(object message, LogCategory category, [CallerFilePath] string callerFilePath = "")
        {
            if (!ShouldLog(LogLevel.Warning, category)) return;
            EmitAccepted(LogLevel.Warning, LogDiagnostics.SafeString(message), category, source: ResolveSourceTag(callerFilePath));
        }
        public static void WarningWithContext(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Warning, message, category, context, file: callerFilePath);
        void ILogger.Warning(string message, LogCategory category) => Emit(LogLevel.Warning, message, category);
        public void Warning(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Warning, message, category, context);
        public static void Warning<T0>(LogCategory category, string template, T0 arg0,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Warning, category)) return;
            string message = LogDiagnostics.Format(template, arg0);
            EmitAccepted(LogLevel.Warning, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Warning<T0, T1>(LogCategory category, string template, T0 arg0, T1 arg1,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Warning, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1);
            EmitAccepted(LogLevel.Warning, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Warning<T0, T1, T2>(LogCategory category, string template, T0 arg0, T1 arg1, T2 arg2,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Warning, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1, arg2);
            EmitAccepted(LogLevel.Warning, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Error(string message, LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Error, message, category, file: callerFilePath);
        public static void Error(object message, LogCategory category, [CallerFilePath] string callerFilePath = "")
        {
            if (!ShouldLog(LogLevel.Error, category)) return;
            EmitAccepted(LogLevel.Error, LogDiagnostics.SafeString(message), category, source: ResolveSourceTag(callerFilePath));
        }
        public static void ErrorWithContext(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Error, message, category, context, file: callerFilePath);
        void ILogger.Error(string message, LogCategory category) => Emit(LogLevel.Error, message, category);
        public void Error(string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Error, message, category, context);
        public static void Error<T0>(LogCategory category, string template, T0 arg0,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Error, category)) return;
            string message = LogDiagnostics.Format(template, arg0);
            EmitAccepted(LogLevel.Error, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Error<T0, T1>(LogCategory category, string template, T0 arg0, T1 arg1,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Error, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1);
            EmitAccepted(LogLevel.Error, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Error<T0, T1, T2>(LogCategory category, string template, T0 arg0, T1 arg1, T2 arg2,
            [CallerFilePath] object callerFilePath = null)
        {
            if (!ShouldLog(LogLevel.Error, category)) return;
            string message = LogDiagnostics.Format(template, arg0, arg1, arg2);
            EmitAccepted(LogLevel.Error, message, category, source: ResolveSourceTag(callerFilePath as string));
        }
        public static void Exception(string message, LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Error, message, category, file: callerFilePath);
        public static void Exception(Exception ex, LogCategory category, [CallerFilePath] string callerFilePath = "") =>
            Emit(LogLevel.Error, null, category, exception: ex, file: callerFilePath);
        public void Error(Exception exception, string message = null, LogCategory category = (LogCategory)(-1)) =>
            Emit(LogLevel.Error, message, category, exception: exception);
        public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
            LogCategory category = (LogCategory)(-1)) => Emit(LogLevel.Error, message, category, context, exception);
    }
}
