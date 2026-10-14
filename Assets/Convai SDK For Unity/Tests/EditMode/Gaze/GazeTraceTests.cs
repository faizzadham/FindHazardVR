using System.Collections.Generic;
using Convai.Domain.Logging;
using Convai.Modules.Gaze.Core.Diagnostics;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Pins the two halves of the diagnostics contract that the release-polish round separated:
    ///     the console gate ships closed, and the in-memory ring buffer stays open behind it so the
    ///     inspector's Live panel still has something to show.
    /// </summary>
    public sealed class GazeTraceTests
    {
        private readonly List<GazeTraceEntry> _entries = new();

        private static GazeTrace NewTrace(ILogger logger = null) => new("TestCharacter", () => 0f, logger ?? new TestLogger());

        [Test]
        public void NewTrace_StartsSilent()
        {
            Assert.That(NewTrace().Verbosity, Is.EqualTo(GazeTraceVerbosity.Off),
                "A trace constructed before its profile resolves must not be briefly chatty.");
        }

        [Test]
        public void StateEntries_AreRecordedEvenWhileTheConsoleGateIsClosed()
        {
            GazeTrace trace = NewTrace();

            trace.State("Target changed to 'Player'.");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(1),
                "A silent console must not also blind the Live panel — the ring buffer is not " +
                "gated by Verbosity.");
            Assert.That(_entries[0].Message, Is.EqualTo("Target changed to 'Player'."));
        }

        [Test]
        public void WarningsAndErrors_AreRecordedWhileSilent()
        {
            var logger = new TestLogger();
            GazeTrace trace = NewTrace(logger);

            trace.Warning("Rig binding has no semantic Head mapping.");
            trace.Error("Something is badly wrong.");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(2),
                "Warnings and errors bypass the gate in both directions: logged and recorded.");
            Assert.That(logger.Entries, Has.Count.EqualTo(2));
            Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(logger.Entries[1].Level, Is.EqualTo(LogLevel.Error));
            Assert.That(logger.Entries[1].Source, Is.EqualTo(nameof(GazeTrace)));
            Assert.That(logger.Entries[1].Category, Is.EqualTo(LogCategory.Gaze));
            Assert.That(logger.Entries[1].Message, Does.Contain("[TestCharacter] Something is badly wrong."));
        }

        [TestCase(GazeTraceVerbosity.State, LogLevel.Info)]
        [TestCase(GazeTraceVerbosity.Detail, LogLevel.Debug)]
        [TestCase(GazeTraceVerbosity.Firehose, LogLevel.Debug)]
        public void EnabledEntries_UseTheOwnedLogger(GazeTraceVerbosity verbosity, LogLevel level)
        {
            var logger = new TestLogger();
            GazeTrace trace = NewTrace(logger);
            trace.Verbosity = verbosity;

            if (verbosity == GazeTraceVerbosity.State) trace.State("state");
            else if (verbosity == GazeTraceVerbosity.Detail) trace.Detail("detail");
            else trace.Firehose("firehose");

            Assert.That(logger.Entries, Has.Count.EqualTo(1));
            Assert.That(logger.Entries[0].Level, Is.EqualTo(level));
            Assert.That(logger.Entries[0].Source, Is.EqualTo(nameof(GazeTrace)));
            Assert.That(logger.Entries[0].Category, Is.EqualTo(LogCategory.Gaze));
        }

        [Test]
        public void Firehose_IsNeverRecorded()
        {
            GazeTrace trace = NewTrace();
            trace.Verbosity = GazeTraceVerbosity.Firehose;

            trace.Firehose("per-tick dump");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(0),
                "Per-tick dumps must never evict the transition history the buffer exists to keep.");
        }

        [Test]
        public void IsEnabled_StillGatesDetailCallSites()
        {
            GazeTrace trace = NewTrace();

            Assert.IsFalse(trace.IsEnabled(GazeTraceVerbosity.Detail),
                "Detail call sites test this before interpolating, so a silent character allocates nothing.");

            trace.Verbosity = GazeTraceVerbosity.Detail;
            Assert.IsTrue(trace.IsEnabled(GazeTraceVerbosity.Detail));
        }

        [Test]
        public void RingBuffer_KeepsTheMostRecentEntries()
        {
            GazeTrace trace = NewTrace();

            for (int i = 0; i < GazeTrace.Capacity + 5; i++)
                trace.State($"entry {i}");

            Assert.That(trace.CopyRecentEntries(_entries), Is.EqualTo(GazeTrace.Capacity));
            Assert.That(_entries[^1].Message, Is.EqualTo($"entry {GazeTrace.Capacity + 4}"));
            Assert.That(trace.TotalRecorded, Is.EqualTo(GazeTrace.Capacity + 5));
        }
    }
}
