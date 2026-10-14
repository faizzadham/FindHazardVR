using System;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;
using NUnit.Framework;

namespace Convai.Tests.EditMode
{
    /// <summary>
    ///     Comprehensive unit tests for the logging system including ILogSink, LogEntry, and ConvaiLogger.
    /// </summary>
    public class LoggingSystemTests
    {
        private TestLogSink _testSink;

        [SetUp]
        public void SetUp()
        {
            _testSink = new TestLogSink();
            ConvaiLogger.ClearSinks();
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiLogger.ClearSinks();
            _testSink?.Dispose();
        }

        #region LogEntry Tests

        [Test]
        public void LogEntry_Create_SetsPropertiesCorrectly()
        {
            var entry = LogEntry.Create(LogLevel.Info, LogCategory.SDK, "Test message");

            Assert.AreEqual(LogLevel.Info, entry.Level);
            Assert.AreEqual(LogCategory.SDK, entry.Category);
            Assert.AreEqual("Test message", entry.Message);
            Assert.IsNull(entry.Exception);
            Assert.LessOrEqual((DateTime.UtcNow - entry.Timestamp).TotalSeconds, 1);
        }

        [Test]
        public void LogEntry_CreateWithException_IncludesException()
        {
            Exception testException = new InvalidOperationException("Test exception");
            var entry = LogEntry.CreateWithException(LogLevel.Error, LogCategory.Transport, "Error occurred",
                testException);

            Assert.AreEqual(LogLevel.Error, entry.Level);
            Assert.AreEqual(LogCategory.Transport, entry.Category);
            Assert.AreEqual("Error occurred", entry.Message);
            Assert.AreEqual(testException, entry.Exception);
        }

        [Test]
        public void LogEntry_FactoryMethods_CreateCorrectLevels()
        {
            LogEntry trace = LogEntry.Trace(LogCategory.SDK, "Trace");
            LogEntry debug = LogEntry.Debug(LogCategory.SDK, "Debug");
            LogEntry info = LogEntry.Info(LogCategory.SDK, "Info");
            LogEntry warning = LogEntry.Warning(LogCategory.SDK, "Warning");
            LogEntry error = LogEntry.Error(LogCategory.SDK, "Error");

            Assert.AreEqual(LogLevel.Trace, trace.Level);
            Assert.AreEqual(LogLevel.Debug, debug.Level);
            Assert.AreEqual(LogLevel.Info, info.Level);
            Assert.AreEqual(LogLevel.Warning, warning.Level);
            Assert.AreEqual(LogLevel.Error, error.Level);
        }

        [Test]
        public void LogEntry_HasException_ReturnsTrueWhenExceptionPresent()
        {
            var withException = LogEntry.CreateWithException(LogLevel.Error, LogCategory.SDK, "Error", new Exception());
            var withoutException = LogEntry.Create(LogLevel.Info, LogCategory.SDK, "Info");

            Assert.IsTrue(withException.HasException);
            Assert.IsFalse(withoutException.HasException);
        }

        #endregion

        #region ILogSink Tests

        [Test]
        public void TestLogSink_Write_CapturesLogEntry()
        {
            LogEntry entry = LogEntry.Info(LogCategory.SDK, "Test message");
            _testSink.Write(entry);

            Assert.AreEqual(1, _testSink.Entries.Count);
            Assert.AreEqual("Test message", _testSink.Entries[0].Message);
        }

        [Test]
        public void TestLogSink_SetEnabled_ControlsWriting()
        {
            _testSink.SetEnabled(false);
            LogEntry entry = LogEntry.Info(LogCategory.SDK, "Should not be logged");
            _testSink.WriteIfEnabled(entry);

            Assert.AreEqual(0, _testSink.Entries.Count);

            _testSink.SetEnabled(true);
            _testSink.WriteIfEnabled(entry);

            Assert.AreEqual(1, _testSink.Entries.Count);
        }

        [Test]
        public void TestLogSink_Flush_IsCalled()
        {
            _testSink.Flush();
            Assert.IsTrue(_testSink.WasFlushed);
        }

        #endregion

        #region ConvaiLogger Sink Management Tests

        [Test]
        public void ConvaiLogger_RegisterSink_AddsSink()
        {
            ConvaiLogger.RegisterSink(_testSink);
            Assert.AreEqual(1, ConvaiLogger.SinkCount);
        }

        [Test]
        public void ConvaiLogger_RegisterSink_DoesNotAddDuplicates()
        {
            ConvaiLogger.RegisterSink(_testSink);
            ConvaiLogger.RegisterSink(_testSink);
            Assert.AreEqual(1, ConvaiLogger.SinkCount);
        }

        [Test]
        public void ConvaiLogger_UnregisterSink_RemovesSink()
        {
            ConvaiLogger.RegisterSink(_testSink);
            ConvaiLogger.UnregisterSink(_testSink);
            Assert.AreEqual(0, ConvaiLogger.SinkCount);
        }

        [Test]
        public void ConvaiLogger_ClearSinks_RemovesAllSinks()
        {
            var sink1 = new TestLogSink();
            var sink2 = new TestLogSink();
            ConvaiLogger.RegisterSink(sink1);
            ConvaiLogger.RegisterSink(sink2);

            ConvaiLogger.ClearSinks();

            Assert.AreEqual(0, ConvaiLogger.SinkCount);
            Assert.IsTrue(sink1.WasDisposed);
            Assert.IsTrue(sink2.WasDisposed);
        }

        #endregion
    }
}
