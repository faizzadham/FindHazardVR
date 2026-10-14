using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Runtime;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class LoggingMetadataTests
    {
        private ConvaiSettings _previous, _settings;
        private TestLogSink _sink;
        private static readonly FieldInfo SettingsInstance = typeof(ConvaiSettings).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        [SetUp] public void SetUp()
        {
            _previous = ConvaiSettings.Instance;
            _settings = ScriptableObject.CreateInstance<ConvaiSettings>();
            SettingsInstance.SetValue(null, _settings);
            _settings.SetGlobalLogLevel(LogLevel.Trace);
            LoggingConfig.InvalidateCache();
            ConvaiLogger.ClearSinks(); LogContext.ClearAll();
            _sink = new TestLogSink(); ConvaiLogger.RegisterSink(_sink);
        }
        [TearDown] public void TearDown()
        {
            ConvaiLogger.ClearSinks(); LogContext.ClearAll();
            SettingsInstance.SetValue(null, _previous); LoggingConfig.InvalidateCache();
            UnityEngine.Object.DestroyImmediate(_settings);
            ConvaiLogger.RegisterSink(new UnityConsoleSink());
        }
        [Test] public void SeverityAndOverloadMatrixPreservesCountsAndTrace()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || CONVAI_DEBUG_LOGGING
            const LogLevel maximum = LogLevel.Trace;
#else
            const LogLevel maximum = LogLevel.Info;
#endif
            ILogger logger = ConvaiLogger.Instance.WithSource("matrix", LogCategory.Audio);
            foreach (LogLevel level in Enum.GetValues(typeof(LogLevel)))
            {
                _sink.Clear();
                logger.Log(level, "literal"); logger.Log(level, "context", new Dictionary<string, object>());
                Assert.That(_sink.Entries.Count, Is.EqualTo(level == LogLevel.Off || level > maximum ? 0 : 2));
                foreach (var entry in _sink.Entries)
                { Assert.That(entry.Level, Is.EqualTo(level)); Assert.That(entry.Category, Is.EqualTo(LogCategory.Audio)); Assert.That(entry.Source, Is.EqualTo("matrix")); }
            }
            logger.Log((LogLevel)(-1), "invalid"); logger.Log((LogLevel)99, "invalid");
            Assert.That(_sink.Entries.Count, Is.EqualTo(maximum == LogLevel.Trace ? 2 : 0));
        }
        [Test] public void StaticSeverityFamiliesPreserveMetadataAcrossEveryTemplateArity()
        {
            LogContext.SetGlobalProperty("ambient", "present");
            using var correlation = LogContext.PushCorrelationId("matrix-correlation");
            var properties = new Dictionary<string, object> { ["explicit"] = "present" };
            void Verify(LogLevel level, int expected)
            {
                Assert.That(_sink.Entries.Count, Is.EqualTo(expected), level.ToString());
                foreach (var entry in _sink.Entries)
                {
                    Assert.That(entry.Level, Is.EqualTo(level));
                    Assert.That(entry.Category, Is.EqualTo(LogCategory.Audio));
                    Assert.That(entry.Source, Is.EqualTo(nameof(LoggingMetadataTests)));
                    Assert.That(entry.Context["ambient"], Is.EqualTo("present"));
                    Assert.That(entry.CorrelationId, Is.EqualTo("matrix-correlation"));
                }
                _sink.Clear();
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || CONVAI_DEBUG_LOGGING
            const int diagnosticCount = 6;
#else
            const int diagnosticCount = 0;
#endif
            ConvaiLogger.Error("literal", LogCategory.Audio);
            ConvaiLogger.Error((object)42, LogCategory.Audio);
            ConvaiLogger.ErrorWithContext("structured", properties, LogCategory.Audio);
            ConvaiLogger.Error(LogCategory.Audio, "{0}", 42);
            ConvaiLogger.Error(LogCategory.Audio, "{0}/{1}", 42, 43);
            ConvaiLogger.Error(LogCategory.Audio, "{0}/{1}/{2}", 42, 43, 44);
            Verify(LogLevel.Error, 6);
            ConvaiLogger.Warning("literal", LogCategory.Audio);
            ConvaiLogger.Warning((object)42, LogCategory.Audio);
            ConvaiLogger.WarningWithContext("structured", properties, LogCategory.Audio);
            ConvaiLogger.Warning(LogCategory.Audio, "{0}", 42);
            ConvaiLogger.Warning(LogCategory.Audio, "{0}/{1}", 42, 43);
            ConvaiLogger.Warning(LogCategory.Audio, "{0}/{1}/{2}", 42, 43, 44);
            Verify(LogLevel.Warning, 6);
            ConvaiLogger.Info("literal", LogCategory.Audio);
            ConvaiLogger.Info((object)42, LogCategory.Audio);
            ConvaiLogger.InfoWithContext("structured", properties, LogCategory.Audio);
            ConvaiLogger.Info(LogCategory.Audio, "{0}", 42);
            ConvaiLogger.Info(LogCategory.Audio, "{0}/{1}", 42, 43);
            ConvaiLogger.Info(LogCategory.Audio, "{0}/{1}/{2}", 42, 43, 44);
            Verify(LogLevel.Info, 6);
            ConvaiLogger.Debug("literal", LogCategory.Audio);
            ConvaiLogger.Debug((object)42, LogCategory.Audio);
            ConvaiLogger.DebugWithContext("structured", properties, LogCategory.Audio);
            ConvaiLogger.Debug(LogCategory.Audio, "{0}", 42);
            ConvaiLogger.Debug(LogCategory.Audio, "{0}/{1}", 42, 43);
            ConvaiLogger.Debug(LogCategory.Audio, "{0}/{1}/{2}", 42, 43, 44);
            Verify(LogLevel.Debug, diagnosticCount);
            ConvaiLogger.Trace("literal", LogCategory.Audio);
            ConvaiLogger.Trace((object)42, LogCategory.Audio);
            ConvaiLogger.TraceWithContext("structured", properties, LogCategory.Audio);
            ConvaiLogger.Trace(LogCategory.Audio, "{0}", 42);
            ConvaiLogger.Trace(LogCategory.Audio, "{0}/{1}", 42, 43);
            ConvaiLogger.Trace(LogCategory.Audio, "{0}/{1}/{2}", 42, 43, 44);
            Verify(LogLevel.Trace, diagnosticCount);
        }
        [Test] public void StaticDebugAndTraceCallsEraseArgumentExpressionsInProduction()
        {
            int evaluations = 0;
            int Evaluate() => ++evaluations;
            ConvaiLogger.Debug(Evaluate(), LogCategory.SDK);
            ConvaiLogger.Debug(LogCategory.SDK, "{0}", Evaluate());
            ConvaiLogger.Debug(LogCategory.SDK, "{0}/{1}", Evaluate(), Evaluate());
            ConvaiLogger.Trace(Evaluate(), LogCategory.SDK);
            ConvaiLogger.Trace(LogCategory.SDK, "{0}", Evaluate());
            ConvaiLogger.TraceWithContext(Evaluate().ToString(), null, LogCategory.SDK);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || CONVAI_DEBUG_LOGGING
            Assert.That(evaluations, Is.EqualTo(7));
            Assert.That(_sink.Entries.Count, Is.EqualTo(6));
#else
            Assert.That(evaluations, Is.Zero);
            Assert.That(_sink.Entries, Is.Empty);
#endif
        }
        [Test] public void RejectedCallsDoNotFormatOrReadContext()
        {
            _settings.SetGlobalLogLevel(LogLevel.Off);
            long failures = LogDiagnostics.FormattingFailures;
            ConvaiLogger.Info(new ThrowingValue(), LogCategory.SDK);
            ConvaiLogger.Info(LogCategory.SDK, "{broken", new ThrowingValue());
            ConvaiLogger.Instance.WithSource("bound").Info("ignored", new ExplodingProperties());
            Assert.That(_sink.Entries, Is.Empty);
            Assert.That(LogDiagnostics.FormattingFailures, Is.EqualTo(failures));
            _settings.SetGlobalLogLevel(LogLevel.Trace); ConvaiLogger.ClearSinks();
            Assert.That(LoggingConfig.IsEnabled(LogLevel.Info, LogCategory.SDK), Is.False);
        }
        [Test] public async Task WorkerLoggingReadsThePublishedSnapshotWithoutLoadingSettings()
        {
            _settings.SetGlobalLogLevel(LogLevel.Off);
            SettingsInstance.SetValue(null, null);
            try
            {
                await Task.Run(() =>
                {
                    Assert.That(ConvaiLogger.Instance.IsEnabled(LogLevel.Info, LogCategory.SDK), Is.False);
                    ConvaiLogger.Info("worker", LogCategory.SDK);
                    ConvaiLogger.Instance.WithSource("worker").Info("bound");
                });
                Assert.That(SettingsInstance.GetValue(null), Is.Null);
                Assert.That(_sink.Entries, Is.Empty);
            }
            finally { SettingsInstance.SetValue(null, _settings); }
        }
        [Test] public void SourceAndCategoryAreMetadataAndExplicitCategoryWins()
        {
            var logger = ConvaiLogger.Instance.WithSource("parent", LogCategory.Audio).WithSource("child", LogCategory.Vision);
            logger.Info("first"); logger.Info("second", LogCategory.SDK);
            Assert.That(_sink.Entries.Select(x => x.Source), Is.All.EqualTo("child"));
            Assert.That(_sink.Entries.Select(x => x.Category), Is.EqualTo(new[] { LogCategory.Vision, LogCategory.SDK }));
            Assert.That(_sink.Entries[0].Message, Is.EqualTo("first"));
            ConvaiLogger.Info("partial", LogCategory.SDK, "/private/folder/Character.Audio.cs");
            Assert.That(_sink.Entries[2].Source, Is.EqualTo("Character"));
            Assert.That(LogFormatting.Format(_sink.Entries[2], false, LogStackTraceLevels.None), Does.Contain("[Character] partial").And.Not.Contains("private"));
        }
        [Test] public void TemplatesWithStringArgumentsPreserveArityAndCallerSource()
        {
            ConvaiLogger.Info(LogCategory.SDK, "{0}/{1}", "one", "two");
            ConvaiLogger.Info(LogCategory.SDK, "{0}/{1}/{2}", "one", "two", "three");
            Assert.That(_sink.Entries.Select(x => x.Message), Is.EqualTo(new[] { "one/two", "one/two/three" }));
            Assert.That(_sink.Entries.Select(x => x.Source), Is.All.EqualTo(nameof(LoggingMetadataTests)));
        }
        [Test] public void ContextSnapshotsFreezeValuesAndHonorPrecedenceAcrossOverloads()
        {
            var value = new System.Text.StringBuilder("before");
            LogContext.SetGlobalProperty("winner", "global"); LogContext.SetGlobalProperty("mutable", value);
            LogContext.SetScopedProperty("winner", "scoped");
            var ambient = LogContext.GetAllProperties();
            Assert.That(LogContext.GetAllProperties(), Is.SameAs(ambient));
            var properties = new Dictionary<string, object> { ["winner"] = "explicit", ["value"] = value };
            using (LogContext.PushCorrelationId("correlation"))
            {
                ((ILogger)ConvaiLogger.Instance).Info("basic");
                ConvaiLogger.Instance.WithSource("source").Error(new Exception("synthetic"), "explicit", properties);
            }
            properties["winner"] = "changed"; value.Append(" after"); LogContext.SetScopedProperty("winner", "later");
            Assert.That(_sink.Entries[0].Context["winner"], Is.EqualTo("scoped"));
            Assert.That(_sink.Entries[1].Context["winner"], Is.EqualTo("explicit"));
            Assert.That(_sink.Entries[1].Context["value"], Is.EqualTo("before"));
            Assert.That(_sink.Entries[0].Context["mutable"], Is.EqualTo("before"));
            Assert.That(_sink.Entries.Select(x => x.CorrelationId), Is.All.EqualTo("correlation"));
            Assert.That(ambient, Is.Not.InstanceOf<IDictionary<string, object>>());
        }
        [Test] public async Task ScopedPropertiesRemainIsolatedAcrossAsyncBranches()
        {
            LogContext.SetScopedProperty("branch", "parent");
            await Task.Run(() => { LogContext.SetScopedProperty("branch", "child"); Assert.That(LogContext.GetAllProperties()["branch"], Is.EqualTo("child")); });
            Assert.That(LogContext.GetAllProperties()["branch"], Is.EqualTo("parent"));
        }
        [Test] public void SettingsCopyOverridesAndLastDuplicateWinsForEveryCategory()
        {
            foreach (LogCategory category in Enum.GetValues(typeof(LogCategory)))
            {
                var overrides = new[] { new LogLevelOverride(category, LogLevel.Trace), new LogLevelOverride(category, LogLevel.Error) };
                _settings.SetCategoryOverrides(overrides); overrides[1] = new LogLevelOverride(category, LogLevel.Off);
                Assert.That(_settings.GetLogLevel(category), Is.EqualTo(LogLevel.Error));
                Assert.That(LoggingConfig.GetLogLevel(category), Is.EqualTo(LogLevel.Error));
                Assert.That(LoggingConfig.IsEnabled(LogLevel.Info, category), Is.False);
                Assert.That(LogFormatting.GetCategoryName(category), Is.EqualTo(category.ToString()));
            }
        }
        [TestCase(true, LogStackTraceLevels.Warning | LogStackTraceLevels.Error)]
        [TestCase(false, LogStackTraceLevels.None)]
        public void LegacyStackFlagMigratesAndNewSettingsDefaultToError(bool legacy, LogStackTraceLevels expected)
        {
            Assert.That(_settings.StackTraceLevels, Is.EqualTo(LogStackTraceLevels.Error));
            JsonUtility.FromJsonOverwrite("{\"_stackTraceLevels\":-1,\"_includeStackTraces\":" + legacy.ToString().ToLowerInvariant() + "}", _settings);
            _settings.OnAfterDeserialize(); Assert.That(_settings.StackTraceLevels, Is.EqualTo(expected));
        }
        [Test] public void TimestampsAreUtcEventTimesAndNullOrThrowingFormattingIsSafe()
        {
            var before = DateTime.UtcNow;
            ConvaiLogger.Info((object)null, LogCategory.SDK);
            ConvaiLogger.Info(new ThrowingValue(), LogCategory.SDK);
            ConvaiLogger.Info(LogCategory.SDK, "{broken", 42);
            Assert.That(_sink.Entries[0].Message, Is.Empty);
            Assert.That(_sink.Entries[0].Timestamp.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(_sink.Entries[0].Timestamp, Is.InRange(before, DateTime.UtcNow));
            Assert.That(_sink.Entries[1].Message, Is.EqualTo(LogDiagnostics.FormattingFallback));
            Assert.That(_sink.Entries[2].Message, Is.EqualTo(LogDiagnostics.FormattingFallback));
        }
        [Test] public void NestedFormattingAndOversizedBuffersRemainIntactAndBounded()
        {
            var entry = LogEntry.Error(LogCategory.SDK, "outer", new NestedException());
            string formatted = LogFormatting.Format(in entry, false, LogStackTraceLevels.Error);
            Assert.That(formatted, Does.Contain("outer").And.Contain("nested-original"));
            var longEntry = LogEntry.Info(LogCategory.SDK, new string('x', 100000));
            Assert.That(LogFormatting.Format(in longEntry, false, LogStackTraceLevels.None).Length, Is.GreaterThan(100000));
            Assert.That(LogFormatting.RetainedBuilderCapacity, Is.LessThanOrEqualTo(4096));
        }
        [Test] public void RecursiveTemplateValuesHaveABoundedFallback()
        {
            long failures = LogDiagnostics.FormattingFailures;
            ConvaiLogger.Info(LogCategory.SDK, "{0}", new RecursiveTemplateValue());
            Assert.That(_sink.Entries.Count, Is.InRange(1, 9));
            Assert.That(_sink.Entries.Any(entry => entry.Message == LogDiagnostics.FormattingFallback), Is.True);
            Assert.That(LogDiagnostics.FormattingFailures, Is.GreaterThan(failures));
        }
        [Test] public void TimestampTextPreservesEveryMillisecondForShortAndLargeMessages()
        {
            // Inject a deterministic event time without exposing a production timestamp hook.
            ConstructorInfo constructor = typeof(LogEntry).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var second = new DateTime(2026, 1, 15, 12, 34, 56, DateTimeKind.Utc);
            foreach (string message in new[] { "short", new string('x', 8192) })
            for (int millisecond = 0; millisecond < 1000; millisecond++)
            {
                DateTime timestamp = second.AddMilliseconds(millisecond);
                var entry = (LogEntry)constructor.Invoke(new object[]
                    { timestamp, LogLevel.Info, LogCategory.SDK, message, null, null, null, null });
                string expected = timestamp.ToLocalTime().ToString("[HH:mm:ss.fff]", System.Globalization.CultureInfo.InvariantCulture);
                StringAssert.StartsWith(expected, LogFormatting.Format(in entry, false, LogStackTraceLevels.None));
            }
        }
        [Test] public void LargePlainMessagesPreserveTheCompleteTextContractWithoutAnOversizedBuilder()
        {
            string message = new('x', 8192);
            using var correlation = LogContext.PushCorrelationId("large-correlation");
            foreach (LogCategory category in Enum.GetValues(typeof(LogCategory)))
            foreach (bool colored in new[] { false, true })
            {
                var entry = LogEntry.CreateEvent(LogLevel.Info, category, message, "large-source");
                string expected = $"[{entry.Timestamp.ToLocalTime():HH:mm:ss.fff}][Info][{category}][large-correlation]: [large-source] {message}";
                if (colored) expected = "<color=grey>" + expected + "</color>";
                Assert.That(LogFormatting.Format(in entry, colored, LogStackTraceLevels.None), Is.EqualTo(expected));
                Assert.That(LogFormatting.RetainedBuilderCapacity, Is.LessThanOrEqualTo(4096));
            }
        }
        [Test] public void ConsoleExceptionIsSingularAndNoStackOptionReachesUnityBoundary()
        {
            ConvaiLogger.ClearSinks(); ConvaiLogger.RegisterSink(new UnityConsoleSink());
            _settings.SetColoredOutput(false); _settings.SetStackTraceLevels(LogStackTraceLevels.None);
            var received = new List<(string message, string stack)>();
            UnityEngine.Application.LogCallback callback = (message, stack, type) =>
            { if (message.Contains("logging-boundary") || message.Contains("inner-original")) received.Add((message, stack)); };
            UnityEngine.Application.logMessageReceived += callback;
            try
            {
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("logging-boundary"));
                ConvaiLogger.Instance.Error(new Exception("logging-boundary", new Exception("inner-original")), "event");
                Assert.That(received.Count, Is.EqualTo(1));
                Assert.That(received[0].message, Does.Contain("inner-original"));
                Assert.That(received[0].stack, Is.Empty);
            }
            finally { UnityEngine.Application.logMessageReceived -= callback; }
        }
        [Test] public void IndependentSinkFiltersAndFailureRecoveryWork()
        {
            ConvaiLogger.ClearSinks();
            var sink = new CallbackSink { AcceptedCategory = LogCategory.Audio, Fail = true };
            ConvaiLogger.RegisterSink(sink);
            Assert.That(ConvaiLogger.Instance.IsEnabled(LogLevel.Info, LogCategory.SDK), Is.False);
            for (int i = 0; i < 8; i++) ConvaiLogger.Info("attempt", LogCategory.Audio);
            Assert.That(sink.Writes, Is.EqualTo(3));
            sink.Fail = false; ConvaiLogger.RegisterSink(sink); ConvaiLogger.Info("recovered", LogCategory.Audio);
            Assert.That(sink.Writes, Is.EqualTo(4));
        }
        [Test] public void RecursiveSinkLoggingAndRemovalCannotReenterOrDeadlock()
        {
            ConvaiLogger.ClearSinks(); var sink = new CallbackSink();
            sink.OnWrite = () => { ConvaiLogger.Info("nested", LogCategory.Audio); ConvaiLogger.ClearSinks(); };
            ConvaiLogger.RegisterSink(sink); ConvaiLogger.Info("outer", LogCategory.Audio);
            Assert.That(sink.Writes, Is.EqualTo(1));
            Assert.That(SpinWait.SpinUntil(() => sink.Disposed, 2000), Is.True);
            Assert.That(ConvaiLogger.SinkCount, Is.Zero);
        }
        [Test] public void RetirementWaitsForWriteAndRejectsSubsequentEntries()
        {
            ConvaiLogger.ClearSinks();
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var sink = new CallbackSink { OnWrite = () => { entered.Set(); release.Wait(2000); } };
            ConvaiLogger.RegisterSink(sink);
            var writer = Task.Run(() => ConvaiLogger.Info("active", LogCategory.Audio));
            Assert.That(entered.Wait(2000), Is.True);
            var removal = Task.Run(() => ConvaiLogger.UnregisterSink(sink));
            try { Assert.That(SpinWait.SpinUntil(() => ConvaiLogger.SinkCount == 0, 2000), Is.True); Assert.That(sink.Disposed, Is.False); }
            finally { release.Set(); }
            Assert.That(Task.WaitAll(new[] { writer, removal }, 2000), Is.True);
            ConvaiLogger.Info("retired", LogCategory.Audio);
            Assert.That(sink.Writes, Is.EqualTo(1)); Assert.That(sink.Disposed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => ConvaiLogger.RegisterSink(sink));
        }
        [Test] public void InitializationNotifiesOncePerSession()
        {
            ConvaiLogger.ResetSession(); int calls = 0; Action<ConvaiLogger> listener = _ => calls++;
            ConvaiLogger.OnInitializationCompleted += listener;
            try { for (int i = 0; i < 10; i++) ConvaiLogger.Initialize(); Assert.That(calls, Is.EqualTo(1)); }
            finally { ConvaiLogger.OnInitializationCompleted -= listener; }
        }
        [Test] public void ProducersCanOverlapRegistrationFlushAndRetirement()
        {
            ConvaiLogger.ClearSinks();
            using var start = new ManualResetEventSlim();
            int activeWrites = 0;
            var persistent = new CallbackSink
            {
                OnWrite = () =>
                {
                    Assert.That(Interlocked.Increment(ref activeWrites), Is.EqualTo(1));
                    Thread.SpinWait(20);
                    Interlocked.Decrement(ref activeWrites);
                },
                OnFlush = () => Assert.That(Volatile.Read(ref activeWrites), Is.Zero)
            };
            ConvaiLogger.RegisterSink(persistent);
            long failures = LogDiagnostics.SinkFailures;
            var tasks = new List<Task>();
            for (int producer = 0; producer < 4; producer++)
                tasks.Add(Task.Run(() =>
                {
                    start.Wait();
                    for (int i = 0; i < 1000; i++) ConvaiLogger.Info("concurrent", LogCategory.Audio);
                }));
            tasks.Add(Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 100; i++)
                {
                    var temporary = new CallbackSink();
                    ConvaiLogger.RegisterSink(temporary);
                    ConvaiLogger.FlushAllSinks();
                    ConvaiLogger.UnregisterSink(temporary);
                    Assert.That(temporary.Disposed, Is.True);
                }
            }));
            start.Set();
            Assert.That(Task.WaitAll(tasks.ToArray(), 10000), Is.True, "Concurrent logging operations did not finish.");
            Assert.That(persistent.Writes, Is.EqualTo(4000));
            Assert.That(LogDiagnostics.SinkFailures, Is.EqualTo(failures));
            ConvaiLogger.UnregisterSink(persistent);
            Assert.That(persistent.Disposed, Is.True);
        }
        private sealed class RecursiveTemplateValue
        {
            public override string ToString()
            { ConvaiLogger.Info(LogCategory.SDK, "{0}", this); return "recursive-value"; }
        }
        private sealed class ThrowingValue { public override string ToString() => throw new InvalidOperationException("formatter"); }
        private sealed class NestedException : Exception
        {
            public override string ToString()
            { var nested = LogEntry.Info(LogCategory.SDK, "inner"); LogFormatting.Format(in nested, false, LogStackTraceLevels.None); return "nested-original"; }
        }
        private sealed class ExplodingProperties : IReadOnlyDictionary<string, object>
        {
            public int Count => throw new InvalidOperationException();
            public object this[string key] => throw new NotSupportedException();
            public IEnumerable<string> Keys => throw new NotSupportedException();
            public IEnumerable<object> Values => throw new NotSupportedException();
            public bool ContainsKey(string key) => false;
            public bool TryGetValue(string key, out object value) { value = null; return false; }
            public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => throw new NotSupportedException();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class CallbackSink : ILogSink
        {
            public Action OnWrite, OnFlush; public bool Fail; public int Writes; public volatile bool Disposed;
            public LogCategory AcceptedCategory = LogCategory.Audio;
            public string Name => "callback"; public bool IsEnabled => !Disposed;
            public bool IsEnabledFor(LogLevel level, LogCategory category) => IsEnabled && category == AcceptedCategory;
            public void Write(in LogEntry entry) { Assert.That(Disposed, Is.False); Writes++; OnWrite?.Invoke(); if (Fail) throw new Exception("sink failure"); }
            public void Flush() { Assert.That(Disposed, Is.False); OnFlush?.Invoke(); }
            public void Dispose() => Disposed = true;
            public void SetEnabled(bool enabled) { }
        }
    }
}
