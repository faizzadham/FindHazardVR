using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionsInsightsModel" />'s pure
    ///     aggregation of <see cref="ConvaiActionsSessionLog" /> step records into per-action usage
    ///     rows (counts, split, durations, last failure, last run) and the Markdown report behind
    ///     the Insights section's Copy Report button.
    /// </summary>
    [TestFixture]
    public class ConvaiActionsInsightsModelTests
    {
        private double _clock;

        private ConvaiActionsSessionLog CreateLog()
        {
            _clock = 0d;
            return new ConvaiActionsSessionLog(() => _clock);
        }

        private void RecordStep(
            ConvaiActionsSessionLog log,
            string actionName,
            ConvaiActionExecutionStatus status,
            double durationSeconds,
            ConvaiActionFailureReason failureReason = ConvaiActionFailureReason.None,
            string failureMessage = "")
        {
            log.OnBatchStarted();
            log.OnStepStarted(0, 0, actionName, string.Empty);
            _clock += durationSeconds;
            log.OnStepCompleted(status, failureReason, failureMessage);
            log.OnBatchFinished(aborted: false);
        }

        [Test]
        public void Build_AggregatesCountsSplitAndDurations_PerAction()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Wave", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            RecordStep(log, "Wave", ConvaiActionExecutionStatus.Succeeded, 0.3d);
            RecordStep(log, "Wave", ConvaiActionExecutionStatus.Failed, 0.2d,
                ConvaiActionFailureReason.TargetMissing);
            RecordStep(log, "wave", ConvaiActionExecutionStatus.Unhandled, 0.0d);

            List<ConvaiActionsInsightsRow> rows = ConvaiActionsInsightsModel.Build(log);

            Assert.AreEqual(1, rows.Count, "Action names must aggregate case-insensitively.");
            ConvaiActionsInsightsRow row = rows[0];
            Assert.AreEqual(4, row.RunCount);
            Assert.AreEqual(2, row.SucceededCount);
            Assert.AreEqual(1, row.FailedCount);
            Assert.AreEqual(1, row.UnhandledCount);
            Assert.AreEqual(150d, row.AverageDurationMs, 0.001d);
            Assert.AreEqual(300d, row.MaxDurationMs, 0.001d);
            Assert.AreEqual(ConvaiActionExecutionStatus.Unhandled, row.LastStatus);
        }

        [Test]
        public void Build_TracksLastFailureReason_PreferringStructuredReason()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Wave", ConvaiActionExecutionStatus.Failed, 0.1d,
                ConvaiActionFailureReason.None, "first message");
            RecordStep(log, "Wave", ConvaiActionExecutionStatus.Failed, 0.1d,
                ConvaiActionFailureReason.PathBlocked);

            List<ConvaiActionsInsightsRow> rows = ConvaiActionsInsightsModel.Build(log);

            Assert.AreEqual(nameof(ConvaiActionFailureReason.PathBlocked), rows[0].LastFailureReason);
        }

        [Test]
        public void Build_SkipsIncompleteSteps_AndBlankNames()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Wave", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            log.OnBatchStarted();
            log.OnStepStarted(1, 0, "Wave", string.Empty); // Still running — must not count.
            log.OnStepStarted(1, 1, "  ", string.Empty);

            List<ConvaiActionsInsightsRow> rows = ConvaiActionsInsightsModel.Build(log);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(1, rows[0].RunCount);
        }

        [Test]
        public void Build_DefaultSort_PutsMostFailedFirst()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Healthy", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            RecordStep(log, "Healthy", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            RecordStep(log, "Flaky", ConvaiActionExecutionStatus.Failed, 0.1d);
            RecordStep(log, "Declined", ConvaiActionExecutionStatus.Unhandled, 0.1d);
            RecordStep(log, "Declined", ConvaiActionExecutionStatus.Unhandled, 0.1d);

            List<ConvaiActionsInsightsRow> rows = ConvaiActionsInsightsModel.Build(log);

            Assert.AreEqual("Declined", rows[0].ActionName);
            Assert.AreEqual("Flaky", rows[1].ActionName);
            Assert.AreEqual("Healthy", rows[2].ActionName);
        }

        [Test]
        public void Build_SortVariants_OrderByUsageAndName()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Beta", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            RecordStep(log, "Alpha", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            RecordStep(log, "Alpha", ConvaiActionExecutionStatus.Succeeded, 0.1d);

            List<ConvaiActionsInsightsRow> mostUsed =
                ConvaiActionsInsightsModel.Build(log, ConvaiActionsInsightsSort.MostUsed);
            Assert.AreEqual("Alpha", mostUsed[0].ActionName);
            Assert.AreEqual("Beta", mostUsed[1].ActionName);

            List<ConvaiActionsInsightsRow> byName =
                ConvaiActionsInsightsModel.Build(log, ConvaiActionsInsightsSort.Name);
            Assert.AreEqual("Alpha", byName[0].ActionName);
            Assert.AreEqual("Beta", byName[1].ActionName);
        }

        [Test]
        public void FindRow_MatchesCaseInsensitively_AndReturnsNullForUnknown()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Wave", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            List<ConvaiActionsInsightsRow> rows = ConvaiActionsInsightsModel.Build(log);

            Assert.NotNull(ConvaiActionsInsightsModel.FindRow(rows, "wave"));
            Assert.IsNull(ConvaiActionsInsightsModel.FindRow(rows, "Bow"));
            Assert.IsNull(ConvaiActionsInsightsModel.FindRow(rows, "  "));
        }

        [Test]
        public void MarkdownReport_ContainsHeaderTableAndEscapedCells()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Wave | Bow", ConvaiActionExecutionStatus.Failed, 0.25d,
                ConvaiActionFailureReason.None, "line one\nline two");
            List<ConvaiActionsInsightsRow> rows = ConvaiActionsInsightsModel.Build(log);

            string report = ConvaiActionsInsightsModel.BuildMarkdownReport(rows, "Friday");

            StringAssert.Contains("# Action usage — Friday", report);
            StringAssert.Contains("| Action | Runs | Completed | Failed | Declined |", report);
            StringAssert.Contains("Wave \\| Bow", report);
            StringAssert.DoesNotContain("line one\nline two", report);
            StringAssert.Contains("250", report);
        }

        [Test]
        public void MarkdownReport_EmptySession_SaysSo()
        {
            string report = ConvaiActionsInsightsModel.BuildMarkdownReport(
                new List<ConvaiActionsInsightsRow>(), null);

            StringAssert.Contains("Convai Character", report);
            StringAssert.Contains("No actions ran this session.", report);
        }

        [Test]
        public void MarkdownReport_ProblemsFirstStillIncludesHealthyActions()
        {
            ConvaiActionsSessionLog log = CreateLog();
            RecordStep(log, "Broken", ConvaiActionExecutionStatus.Failed, 0.2d);
            RecordStep(log, "Healthy", ConvaiActionExecutionStatus.Succeeded, 0.1d);
            List<ConvaiActionsInsightsRow> rows = ConvaiActionsInsightsModel.Build(
                log, ConvaiActionsInsightsSort.MostFailed);

            string report = ConvaiActionsInsightsModel.BuildMarkdownReport(rows, "Friday");

            StringAssert.Contains("| Broken |", report);
            StringAssert.Contains("| Healthy |", report,
                "The selected Insights order must never filter healthy actions from the copied summary.");
        }

        [Test]
        public void InsightsCopy_UsesBeginnerReadableOutcomeAndOrderingLanguage()
        {
            string outcomes = ConvaiActionsEditorStrings.BuildInsightsOutcomes(4, 2, 1, 1).text;

            StringAssert.Contains("4 runs", outcomes);
            StringAssert.Contains("2 succeeded", outcomes);
            StringAssert.Contains("1 failed", outcomes);
            StringAssert.Contains("1 declined", outcomes);
            StringAssert.Contains("Every recorded action is still included",
                ConvaiActionsEditorStrings.BuildInsightsOrderExplanation(
                    ConvaiActionsInsightsSort.MostFailed).text);
            StringAssert.Contains("Markdown", ConvaiActionsEditorStrings.InsightsCopyReportButton.tooltip);
        }
    }
}
