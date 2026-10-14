using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionsSessionLog" /> — the pure ring-buffer aggregation behind
    ///     the Actions Editor's Live view and Test Run result panel: batch/step lifecycle recording
    ///     with an injected clock, test-run batch tagging via <c>ExpectTestRun</c> tokens, ring caps
    ///     (batches, total steps, feedback), and version bumping for cached-view invalidation.
    /// </summary>
    [TestFixture]
    public class ConvaiActionsSessionLogTests
    {
        private double _now;
        private ConvaiActionsSessionLog _log;

        [SetUp]
        public void SetUp()
        {
            _now = 100d;
            _log = new ConvaiActionsSessionLog(() => _now);
        }

        [Test]
        public void BatchLifecycle_RecordsStepsWithDurationsAndOutcome()
        {
            _log.OnBatchStarted();
            _log.OnStepStarted(7, 0, "Move To", "Red Cube");
            _now += 0.25d;
            _log.OnStepCompleted(ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None, string.Empty);
            _log.OnStepStarted(7, 1, "Open", "Door");
            _now += 0.5d;
            _log.OnStepCompleted(ConvaiActionExecutionStatus.Failed, ConvaiActionFailureReason.PathBlocked, "no path");
            _log.OnBatchFinished(aborted: true);

            Assert.AreEqual(1, _log.Batches.Count);
            ConvaiActionsSessionLog.BatchRecord batch = _log.Batches[0];
            Assert.AreEqual(7, batch.BatchIndex);
            Assert.IsTrue(batch.Finished);
            Assert.IsTrue(batch.Aborted);
            Assert.IsNull(_log.CurrentBatch);

            Assert.AreEqual(2, batch.Steps.Count);
            Assert.AreEqual("Move To", batch.Steps[0].ActionName);
            Assert.AreEqual("Red Cube", batch.Steps[0].TargetName);
            Assert.AreEqual(ConvaiActionExecutionStatus.Succeeded, batch.Steps[0].Status);
            Assert.AreEqual(250d, batch.Steps[0].DurationMs, 0.001d);

            Assert.AreEqual(ConvaiActionExecutionStatus.Failed, batch.Steps[1].Status);
            Assert.AreEqual(ConvaiActionFailureReason.PathBlocked, batch.Steps[1].FailureReason);
            Assert.AreEqual("no path", batch.Steps[1].FailureMessage);
            Assert.AreEqual(500d, batch.Steps[1].DurationMs, 0.001d);
        }

        [Test]
        public void CurrentBatch_IsExposedWhileRunning_AndClearedOnFinish()
        {
            _log.OnBatchStarted();
            Assert.IsNotNull(_log.CurrentBatch);
            _log.OnStepStarted(0, 0, "Wave", string.Empty);
            Assert.AreEqual(1, _log.CurrentBatch.Steps.Count);
            Assert.IsFalse(_log.CurrentBatch.Steps[0].Completed);

            _log.OnBatchFinished(aborted: false);
            Assert.IsNull(_log.CurrentBatch);
            Assert.IsTrue(_log.Batches[0].Finished);
        }

        [Test]
        public void StrayStepWithoutBatchStarted_StillCreatesABatch()
        {
            _log.OnStepStarted(3, 0, "Nod", string.Empty);

            Assert.IsNotNull(_log.CurrentBatch);
            Assert.AreEqual(3, _log.CurrentBatch.BatchIndex);
            Assert.AreEqual(1, _log.CurrentBatch.Steps.Count);
        }

        [Test]
        public void ExpectTestRun_TagsTheNextMatchingBatch_WithItsToken()
        {
            int token = _log.ExpectTestRun("Move To");

            _log.OnBatchStarted();
            _log.OnStepStarted(0, 0, "Move To", "Red Cube");
            _log.OnStepCompleted(ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None, string.Empty);
            _log.OnBatchFinished(aborted: false);

            ConvaiActionsSessionLog.BatchRecord batch = _log.FindByToken(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(batch.IsTestRun);
            Assert.AreEqual(token, batch.TestRunToken);
        }

        [Test]
        public void ExpectTestRun_DoesNotTagANonMatchingBatch()
        {
            int token = _log.ExpectTestRun("Move To");

            _log.OnBatchStarted();
            _log.OnStepStarted(0, 0, "Wave", string.Empty);
            _log.OnBatchFinished(aborted: false);

            Assert.IsFalse(_log.Batches[0].IsTestRun);
            Assert.IsNull(_log.FindByToken(token));

            // The pending expectation survives until its batch actually arrives.
            _log.OnBatchStarted();
            _log.OnStepStarted(1, 0, "Move To", string.Empty);
            Assert.IsNotNull(_log.FindByToken(token));
        }

        [Test]
        public void TestRunTokenMatching_IsCaseInsensitive()
        {
            int token = _log.ExpectTestRun("move to");
            _log.OnBatchStarted();
            _log.OnStepStarted(0, 0, "Move To", string.Empty);

            Assert.IsNotNull(_log.FindByToken(token));
        }

        [Test]
        public void BatchRing_CapsAtMaxBatches_DroppingOldestFirst()
        {
            for (int i = 0; i < ConvaiActionsSessionLog.MaxBatches + 5; i++)
            {
                _log.OnBatchStarted();
                _log.OnStepStarted(i, 0, "Wave", string.Empty);
                _log.OnStepCompleted(ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None, string.Empty);
                _log.OnBatchFinished(aborted: false);
            }

            Assert.AreEqual(ConvaiActionsSessionLog.MaxBatches, _log.Batches.Count);
            Assert.AreEqual(5, _log.Batches[0].BatchIndex);
        }

        [Test]
        public void StepRing_CapsTotalStepsAcrossBatches()
        {
            const int stepsPerBatch = 50;
            const int batchCount = 15; // 750 steps total > MaxTotalSteps (500).
            for (int b = 0; b < batchCount; b++)
            {
                _log.OnBatchStarted();
                for (int s = 0; s < stepsPerBatch; s++)
                {
                    _log.OnStepStarted(b, s, "Wave", string.Empty);
                    _log.OnStepCompleted(ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None, string.Empty);
                }

                _log.OnBatchFinished(aborted: false);
            }

            int totalSteps = 0;
            for (int i = 0; i < _log.Batches.Count; i++)
                totalSteps += _log.Batches[i].Steps.Count;

            Assert.LessOrEqual(totalSteps, ConvaiActionsSessionLog.MaxTotalSteps);
            Assert.Less(_log.Batches.Count, batchCount, "Oldest batches should have been trimmed.");
        }

        [Test]
        public void FeedbackRing_CapsAtMaxEntries_KeepingNewest()
        {
            for (int i = 0; i < ConvaiActionsSessionLog.MaxFeedbackEntries + 5; i++)
                _log.OnFeedback("12:00:00", $"fact {i}", narrated: i % 2 == 0);

            Assert.AreEqual(ConvaiActionsSessionLog.MaxFeedbackEntries, _log.Feedback.Count);
            Assert.AreEqual("fact 5", _log.Feedback[0].Fact);
            Assert.AreEqual($"fact {ConvaiActionsSessionLog.MaxFeedbackEntries + 4}",
                _log.Feedback[_log.Feedback.Count - 1].Fact);
        }

        [Test]
        public void Version_MovesOnEveryMutation()
        {
            int version = _log.Version;
            _log.OnBatchStarted();
            Assert.Greater(_log.Version, version);

            version = _log.Version;
            _log.OnStepStarted(0, 0, "Wave", string.Empty);
            Assert.Greater(_log.Version, version);

            version = _log.Version;
            _log.OnStepCompleted(ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None, string.Empty);
            Assert.Greater(_log.Version, version);

            version = _log.Version;
            _log.OnFeedback("12:00:00", "fact", false);
            Assert.Greater(_log.Version, version);

            version = _log.Version;
            _log.Clear();
            Assert.Greater(_log.Version, version);
        }

        [Test]
        public void Clear_ResetsEverything()
        {
            _log.ExpectTestRun("Move To");
            _log.OnBatchStarted();
            _log.OnStepStarted(0, 0, "Move To", string.Empty);
            _log.OnFeedback("12:00:00", "fact", true);

            _log.Clear();

            Assert.AreEqual(0, _log.Batches.Count);
            Assert.AreEqual(0, _log.Feedback.Count);
            Assert.IsNull(_log.CurrentBatch);

            // A cleared pending expectation must not tag post-clear batches.
            _log.OnBatchStarted();
            _log.OnStepStarted(0, 0, "Move To", string.Empty);
            Assert.IsFalse(_log.Batches[0].IsTestRun);
        }
    }
}
