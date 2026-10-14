using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the pure ring-buffer bookkeeping in <see cref="ConvaiActionDebugBatchHistory" />
    ///     — no GUI/EditorWindow dependency, so it
    ///     is a plain unit test with an injected deterministic clock.
    /// </summary>
    [TestFixture]
    public class ConvaiActionDebugBatchHistoryTests
    {
        [Test]
        public void RecordStep_BeforeAnyBeginStep_DoesNotThrowAndIsIgnored()
        {
            var history = new ConvaiActionDebugBatchHistory(() => 0d);

            Assert.DoesNotThrow(() =>
                history.RecordStep("Move To", ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None));
            Assert.IsNull(history.Current);
            Assert.AreEqual(0, history.Entries.Count);
        }

        [Test]
        public void BeginStep_Then_RecordStep_TracksActionNameStatusAndFailureReason()
        {
            double time = 0d;
            var history = new ConvaiActionDebugBatchHistory(() => time);

            history.BeginStep(batchIndex: 1);
            time = 0.25d; // 250ms later
            history.RecordStep("Pick Up", ConvaiActionExecutionStatus.Failed, ConvaiActionFailureReason.TargetMissing);

            Assert.IsNotNull(history.Current);
            Assert.AreEqual(1, history.Current.BatchIndex);
            Assert.AreEqual(1, history.Current.Steps.Count);

            ConvaiActionDebugBatchHistory.StepEntry step = history.Current.Steps[0];
            Assert.AreEqual("Pick Up", step.ActionName);
            Assert.AreEqual(ConvaiActionExecutionStatus.Failed, step.Status);
            Assert.AreEqual(ConvaiActionFailureReason.TargetMissing, step.FailureReason);
            Assert.AreEqual(250d, step.DurationMs, 0.001d);
        }

        [Test]
        public void BeginStep_SameBatchIndexTwice_AccumulatesStepsInOneEntry()
        {
            double time = 0d;
            var history = new ConvaiActionDebugBatchHistory(() => time);

            history.BeginStep(batchIndex: 5);
            time = 0.1d;
            history.RecordStep("Move To", ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None);

            history.BeginStep(batchIndex: 5);
            time = 0.2d;
            history.RecordStep("Open", ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None);

            Assert.AreEqual(2, history.Current.Steps.Count, "Both steps should land in the same batch entry.");
            Assert.AreEqual(5, history.Current.BatchIndex);
        }

        [Test]
        public void BeginStep_DifferentBatchIndex_StartsAFreshEntry()
        {
            double time = 0d;
            var history = new ConvaiActionDebugBatchHistory(() => time);

            history.BeginStep(batchIndex: 1);
            history.RecordStep("Move To", ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None);

            history.BeginStep(batchIndex: 2);

            Assert.AreEqual(2, history.Current.BatchIndex);
            Assert.AreEqual(0, history.Current.Steps.Count, "A new batch index starts with no steps yet.");
        }

        [Test]
        public void FinalizeBatch_MovesCurrentIntoEntriesAndClearsCurrent()
        {
            var history = new ConvaiActionDebugBatchHistory(() => 0d);
            history.BeginStep(batchIndex: 1);
            history.RecordStep("Move To", ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None);

            history.FinalizeBatch(aborted: false);

            Assert.IsNull(history.Current);
            Assert.AreEqual(1, history.Entries.Count);
            Assert.AreEqual(1, history.Entries[0].BatchIndex);
            Assert.IsFalse(history.Entries[0].Aborted);
        }

        [Test]
        public void FinalizeBatch_WithoutAnyStep_IsANoOp()
        {
            var history = new ConvaiActionDebugBatchHistory(() => 0d);

            Assert.DoesNotThrow(() => history.FinalizeBatch(aborted: true));
            Assert.AreEqual(0, history.Entries.Count);
        }

        [Test]
        public void FinalizeBatch_Aborted_FlagsTheEntry()
        {
            var history = new ConvaiActionDebugBatchHistory(() => 0d);
            history.BeginStep(batchIndex: 3);
            history.RecordStep("Drop", ConvaiActionExecutionStatus.Failed, ConvaiActionFailureReason.InvalidState);

            history.FinalizeBatch(aborted: true);

            Assert.IsTrue(history.Entries[0].Aborted);
        }

        [Test]
        public void FinalizeBatch_MoreThanMaxEntries_TrimsOldestFirst()
        {
            var history = new ConvaiActionDebugBatchHistory(() => 0d);

            for (int i = 0; i < ConvaiActionDebugBatchHistory.MaxEntries + 3; i++)
            {
                history.BeginStep(batchIndex: i);
                history.RecordStep("Move To", ConvaiActionExecutionStatus.Succeeded, ConvaiActionFailureReason.None);
                history.FinalizeBatch(aborted: false);
            }

            IReadOnlyList<ConvaiActionDebugBatchHistory.BatchEntry> entries = history.Entries;
            Assert.AreEqual(ConvaiActionDebugBatchHistory.MaxEntries, entries.Count);

            // Oldest (batch indices 0, 1, 2) were trimmed; the buffer keeps the most recent ones.
            Assert.AreEqual(3, entries[0].BatchIndex);
            Assert.AreEqual(ConvaiActionDebugBatchHistory.MaxEntries + 2, entries[entries.Count - 1].BatchIndex);
        }
    }
}
