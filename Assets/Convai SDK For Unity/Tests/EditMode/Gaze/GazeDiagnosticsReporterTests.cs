using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Diagnostics;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The gaze module's edge-triggered reporting, tested without a scene, a rig or a frame.
    /// </summary>
    /// <remarks>
    ///     Every one of these behaviours used to live inline in the controller, where the only way
    ///     to check "does it log once or every frame?" was to run a character and read the console.
    ///     Both failure modes have shipped before — a module that says nothing when a target is
    ///     lost, and one that writes a line per tick — so they are worth pinning directly.
    /// </remarks>
    public sealed class GazeDiagnosticsReporterTests
    {
        private GazeDiagnosticsReporter _reporter;
        private GazeTrace _trace;
        private List<string> _lines;

        [SetUp]
        public void SetUp()
        {
            _reporter = new GazeDiagnosticsReporter();
            _lines = new List<string>();
            _trace = new GazeTrace("Test") { Verbosity = GazeTraceVerbosity.Detail };
        }

        private int TraceCount()
        {
            _lines.Clear();
            var entries = new List<GazeTraceEntry>();
            _trace.CopyRecentEntries(entries);
            foreach (GazeTraceEntry entry in entries) _lines.Add(entry.Message);
            return _lines.Count;
        }

        // ── Target transitions ───────────────────────────────────────────────

        [Test]
        public void FirstTarget_IsReportedAsATransitionFromNone()
        {
            bool reported = _reporter.TryReportTargetTransition(
                _trace, GazeTargetKind.Player, "Player", 1, false, 0f, out GazeTargetChange change);

            Assert.IsTrue(reported, "Acquiring a target from nothing is a transition.");
            Assert.AreEqual(GazeTargetKind.None, change.FromKind);
            Assert.AreEqual(GazeTargetKind.Player, change.ToKind);
        }

        [Test]
        public void HoldingTheSameTarget_ReportsNothingAfterTheFirstTick()
        {
            _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Player, "Player", 1, false, 0f, out _);

            for (int i = 0; i < 100; i++)
                Assert.IsFalse(
                    _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Player, "Player", 1, false, i, out _),
                    "Holding one target must never report again — this is the per-frame spam guard.");
        }

        [Test]
        public void SameKindDifferentName_IsATransition()
        {
            _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Character, "Alice", 1, false, 0f, out _);

            Assert.IsTrue(
                _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Character, "Bob", 1, false, 1f, out GazeTargetChange change),
                "Looking from one character to another is a target change even though the kind is the same.");
            Assert.AreEqual("Alice", change.FromName);
            Assert.AreEqual("Bob", change.ToName);
        }

        [Test]
        public void LosingTheTarget_ReportsWithAReleasedReason()
        {
            _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Player, "Player", 1, false, 0f, out _);

            Assert.IsTrue(
                _reporter.TryReportTargetTransition(_trace, GazeTargetKind.None, "-", 1, false, 1f, out GazeTargetChange change));
            Assert.AreEqual("target released/lost", change.Reason);
        }

        [Test]
        public void ATeleportOnTheSameTarget_IsNotATransition()
        {
            _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Player, "Player", 1, false, 0f, out _);

            // The character is still looking at the same thing; it just had to re-acquire it.
            // Reporting this as a target change would fire TargetChanged for a camera cut.
            Assert.IsFalse(
                _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Player, "Player", 2, true, 1f, out _),
                "A teleport that keeps the same target must not raise a target change.");
        }

        [Test]
        public void AfterReset_TheSameTargetIsReportedAgain()
        {
            _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Player, "Player", 1, false, 0f, out _);
            _reporter.Reset();

            Assert.IsTrue(
                _reporter.TryReportTargetTransition(_trace, GazeTargetKind.Player, "Player", 1, false, 1f, out _),
                "Reset must forget what was announced, or a re-enabled character stays silent.");
        }

        // ── Latched booleans ─────────────────────────────────────────────────

        [Test]
        public void Occlusion_ReportsOncePerFlip()
        {
            _reporter.ReportPlayerLineOfSight(_trace, false);
            Assert.AreEqual(0, TraceCount(), "Starting visible is the assumed state, so it says nothing.");

            _reporter.ReportPlayerLineOfSight(_trace, true);
            Assert.AreEqual(1, TraceCount());

            for (int i = 0; i < 50; i++) _reporter.ReportPlayerLineOfSight(_trace, true);
            Assert.AreEqual(1, TraceCount(), "Staying occluded must not keep logging.");

            _reporter.ReportPlayerLineOfSight(_trace, false);
            Assert.AreEqual(2, TraceCount());
        }

        [Test]
        public void FocusDegraded_ReportsOncePerFlip()
        {
            _reporter.ReportFocusDegraded(_trace, true);
            Assert.AreEqual(1, TraceCount());

            for (int i = 0; i < 50; i++) _reporter.ReportFocusDegraded(_trace, true);
            Assert.AreEqual(1, TraceCount());

            _reporter.ReportFocusDegraded(_trace, false);
            Assert.AreEqual(2, TraceCount());
        }

        // ── Reach limit ──────────────────────────────────────────────────────

        private void TickOutOfReach(float seconds, float step = 0.1f)
        {
            for (float t = 0f; t < seconds; t += step)
                _reporter.ReportReachLimit(_trace, step, true, 1f, 30f, "Player");
        }

        [Test]
        public void ReachLimit_StaysSilentUntilTheErrorHasPersisted()
        {
            TickOutOfReach(GazeDiagnosticsReporter.ReachLimitHoldSeconds - 0.2f);

            Assert.AreEqual(0, TraceCount(),
                "A brief overshoot during a big gaze shift is normal and must not be announced.");
        }

        [Test]
        public void ReachLimit_ReportsOnceWhenSustained_ThenOnceOnRecovery()
        {
            TickOutOfReach(GazeDiagnosticsReporter.ReachLimitHoldSeconds + 0.5f);
            Assert.AreEqual(1, TraceCount());

            TickOutOfReach(2f);
            Assert.AreEqual(1, TraceCount(), "A target that stays out of reach must not log every tick.");

            _reporter.ReportReachLimit(_trace, 0.1f, true, 1f, 1f, "Player");
            Assert.AreEqual(2, TraceCount(), "Recovery is worth exactly one line.");
        }

        [Test]
        public void ReachLimit_IgnoresPartiallyEngagedTargets()
        {
            for (int i = 0; i < 100; i++)
                _reporter.ReportReachLimit(_trace, 0.1f, true, 0.5f, 30f, "Player");

            Assert.AreEqual(0, TraceCount(),
                "Half-engaged gaze is not trying to reach the target, so a residual is not a defect.");
        }

        [Test]
        public void ReachLimit_IgnoresANaNError()
        {
            for (int i = 0; i < 100; i++)
                _reporter.ReportReachLimit(_trace, 0.1f, true, 1f, float.NaN, "Player");

            Assert.AreEqual(0, TraceCount(), "No contact error has been measured yet — that is not a reach failure.");
        }

        // ── Firehose ─────────────────────────────────────────────────────────

        private static GazeFirehoseSample Sample() => new(
            1f, Vector2.zero, Vector2.zero, Vector2.zero, "Fixate", 0f, 0f, GazeTargetKind.Player, "Player");

        [Test]
        public void Firehose_IsSilentBelowItsVerbosity()
        {
            _trace.Verbosity = GazeTraceVerbosity.Detail;
            GazeFirehoseSample sample = Sample();

            for (int i = 0; i < 100; i++)
                Assert.IsFalse(_reporter.ReportFirehose(_trace, 1f, GazeTraceVerbosity.Detail, 10f, in sample));
        }

        [Test]
        public void Firehose_IsRateLimited()
        {
            _trace.Verbosity = GazeTraceVerbosity.Firehose;
            GazeFirehoseSample sample = Sample();

            // 10 Hz over one simulated second of 100 Hz ticks is ten lines, not a hundred. A
            // firehose line is logged rather than recorded, so the emit count is read off the
            // return value instead of the trace buffer.
            int emitted = 0;
            for (int i = 0; i < 100; i++)
                if (_reporter.ReportFirehose(_trace, 0.01f, GazeTraceVerbosity.Firehose, 10f, in sample))
                    emitted++;

            Assert.That(emitted, Is.InRange(9, 11));
        }
    }
}
