using System;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class LipSyncTimelineAssemblerTests
    {
        [Test]
        public void AddChunk_OutOfOrderIndexedChunks_EmitsOnlyContiguousFrames()
        {
            var assembler = new LipSyncTimelineAssembler();
            LipSyncTimelineAssemblerResult later = assembler.AddChunk(CreateChunk("r1", 2, 2, 2));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.WaitingForGap, later.Action);
            Assert.AreEqual(0, later.FrameCount);

            LipSyncTimelineAssemblerResult first = assembler.AddChunk(CreateChunk("r1", 0, 0, 2));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.EmitFrames, first.Action);
            Assert.AreEqual(4, first.FrameCount);
            Assert.AreEqual(0f, first.Frames[0][0], 0.0001f);
            Assert.AreEqual(3f, first.Frames[3][0], 0.0001f);
        }

        [Test]
        public void AddChunk_NewOwner_RetiresOldOwnerAndDropsStragglers()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk("r1", 0, 0, 2));

            LipSyncTimelineAssemblerResult replacement = assembler.AddChunk(CreateChunk("r2", 0, 10, 2));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.OwnerChanged, replacement.Action);
            Assert.AreEqual(2, replacement.FrameCount);
            Assert.AreEqual("r2", assembler.ActiveOwner.ResponseId);

            LipSyncTimelineAssemblerResult old = assembler.AddChunk(CreateChunk("r1", 2, 2, 2));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.DropStaleOwner, old.Action);
            Assert.AreEqual(0, old.FrameCount);
        }

        [Test]
        public void CancelOwner_OldOwner_DoesNotClearActiveOwner()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk("r1", 0, 0, 2));
            assembler.AddChunk(CreateChunk("r2", 0, 10, 2));

            LipSyncTimelineAssemblerResult result = assembler.CancelOwner(
                LipSyncTimelineResetRequested.Create("char", "participant", "r1", null, null, null, null, "interruption"));

            Assert.AreEqual(LipSyncTimelineAssemblerAction.DropStaleOwner, result.Action);
            Assert.AreEqual("r2", assembler.ActiveOwner.ResponseId);
        }

        [Test]
        public void CancelOwner_ActiveOwner_HardClearsAndRetiresOwner()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk("r1", 0, 0, 2));

            LipSyncTimelineAssemblerResult result = assembler.CancelOwner(
                LipSyncTimelineResetRequested.Create("char", "participant", "r1", null, null, null, null, "interruption"));

            Assert.AreEqual(LipSyncTimelineAssemblerAction.HardReset, result.Action);
            Assert.IsFalse(assembler.HasActiveOwner);
            Assert.AreEqual(LipSyncTimelineAssemblerAction.DropStaleOwner,
                assembler.AddChunk(CreateChunk("r1", 2, 2, 2)).Action);
        }

        [Test]
        public void AddChunk_AfterReset_AllowsNewResponseWithSameEpoch()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk("r1", 0, 0, 1, 1));

            LipSyncTimelineAssemblerResult reset = assembler.CancelOwner(
                LipSyncTimelineResetRequested.Create("char", "participant", "r1", 1, 1, null, null, "interruption"));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.HardReset, reset.Action);

            LipSyncTimelineAssemblerResult next = assembler.AddChunk(CreateChunk("r2", 0, 10, 2, 2));

            Assert.AreEqual(LipSyncTimelineAssemblerAction.EmitFrames, next.Action);
            Assert.AreEqual(2, next.FrameCount);
            Assert.AreEqual("r2", assembler.ActiveOwner.ResponseId);
        }

        [Test]
        public void AddChunk_IndexedChunks_ReportAbsoluteFirstFrameIndex()
        {
            var assembler = new LipSyncTimelineAssembler();
            LipSyncTimelineAssemblerResult first = assembler.AddChunk(CreateChunk("r1", 0, 0, 2));
            Assert.AreEqual(0, first.FirstFrameIndex);

            LipSyncTimelineAssemblerResult second = assembler.AddChunk(CreateChunk("r1", 2, 2, 3));
            Assert.AreEqual(2, second.FirstFrameIndex);
            Assert.AreEqual(3, second.FrameCount);
        }

        [Test]
        public void CancelOwner_WithValidThroughIndex_TruncatesAndRetiresOwner()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk("r1", 0, 0, 4));

            LipSyncTimelineAssemblerResult result = assembler.CancelOwner(
                LipSyncTimelineResetRequested.Create("char", "participant", "r1", null, null, null, 2, "interruption"));

            Assert.AreEqual(LipSyncTimelineAssemblerAction.TruncateAfter, result.Action);
            Assert.AreEqual(2, result.ValidThroughFrameIndex);
            Assert.IsFalse(assembler.HasActiveOwner);
            Assert.AreEqual(LipSyncTimelineAssemblerAction.DropStaleOwner,
                assembler.AddChunk(CreateChunk("r1", 4, 4, 2)).Action);
        }

        [Test]
        public void IsActiveOwner_WhenResponseIdsConflict_DoesNotMatchSharedTurnAlias()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk("r1", 0, 0, 2, 7));

            Assert.IsTrue(assembler.IsActiveOwner("r1", null, null));
            Assert.IsFalse(assembler.IsActiveOwner("r2", 7, 1));
        }

        [Test]
        public void IsActiveOwner_WhenEpochsConflict_DoesNotMatchSharedTurnAlias()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk(null, 0, 0, 2, 7, 1));

            Assert.IsTrue(assembler.IsActiveOwner(null, 7, 1));
            Assert.IsFalse(assembler.IsActiveOwner(null, 7, 2));
        }

        [Test]
        public void IsActiveOwner_WhenOnlyOneSideHasResponse_UsesSharedTurnAndEpoch()
        {
            var assembler = new LipSyncTimelineAssembler();
            assembler.AddChunk(CreateChunk("r1", 0, 0, 2, 7, 3));

            Assert.IsTrue(assembler.IsActiveOwner(null, 7, 3));
            Assert.IsFalse(assembler.IsActiveOwner(null, 7, 4));
            Assert.IsFalse(assembler.IsActiveOwner(null, 7, null));
        }

        [Test]
        public void ResolveExpiredGap_AfterTimeout_SynthesizesMissingIndexesAndFlushesFutureFrames()
        {
            double now = 0d;
            var assembler = new LipSyncTimelineAssembler(() => now);

            LipSyncTimelineAssemblerResult first = assembler.AddChunk(CreateChunk("r1", 0, 1, 1));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.EmitFrames, first.Action);

            LipSyncTimelineAssemblerResult future = assembler.AddChunk(CreateChunk("r1", 3, 3, 2));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.WaitingForGap, future.Action);

            now = 0.199d;
            Assert.AreEqual(LipSyncTimelineAssemblerAction.None, assembler.ResolveExpiredGap().Action);

            now = 0.2d;
            LipSyncTimelineAssemblerResult recovered = assembler.ResolveExpiredGap();
            Assert.AreEqual(LipSyncTimelineAssemblerAction.GapRecovered, recovered.Action);
            Assert.AreEqual(1, recovered.FirstFrameIndex);
            Assert.AreEqual(4, recovered.FrameCount);
            Assert.That(recovered.Frames[0][0], Is.LessThan(1f).And.GreaterThan(0f));
            Assert.That(recovered.Frames[1][0], Is.LessThan(recovered.Frames[0][0]));
            Assert.AreEqual(3f, recovered.Frames[2][0], 0.0001f);
            Assert.AreEqual(4f, recovered.Frames[3][0], 0.0001f);
        }

        [Test]
        public void ResolveExpiredGap_WhenAudioApproachesMissingFrame_RecoversBeforeWallTimeout()
        {
            double now = 0d;
            var assembler = new LipSyncTimelineAssembler(() => now);
            assembler.AddChunk(CreateChunk("r1", 0, 1, 1));
            assembler.AddChunk(CreateChunk("r1", 3, 3, 2));

            now = 0.01d;
            LipSyncTimelineAssemblerResult recovered = assembler.ResolveExpiredGap(playbackSeconds: 0.015d);

            Assert.AreEqual(LipSyncTimelineAssemblerAction.GapRecovered, recovered.Action);
            Assert.AreEqual(1, recovered.FirstFrameIndex);
            Assert.AreEqual(4, recovered.FrameCount);
        }

        [Test]
        public void ResolveExpiredGap_WhenMissingFrameIsSafelyAhead_ContinuesReorderWait()
        {
            double now = 0d;
            var assembler = new LipSyncTimelineAssembler(() => now);
            assembler.AddChunk(CreateChunk("r1", 0, 0, 12));
            assembler.AddChunk(CreateChunk("r1", 14, 14, 2));

            now = 0.01d;
            LipSyncTimelineAssemblerResult waiting = assembler.ResolveExpiredGap(playbackSeconds: 0.1d);

            Assert.AreEqual(LipSyncTimelineAssemblerAction.None, waiting.Action);
        }

        private static LipSyncPackedChunk CreateChunk(
            string responseId,
            int startFrame,
            int firstValue,
            int frameCount,
            int? turnId = null,
            int? epoch = 1)
        {
            float[][] frames = new float[frameCount][];
            for (int i = 0; i < frameCount; i++) frames[i] = new[] { firstValue + (float)i };

            return new LipSyncPackedChunk(
                LipSyncProfileId.ARKit,
                60f,
                new[] { "jawOpen" },
                frames,
                responseId,
                turnId,
                epoch,
                startFrame,
                null);
        }
    }
}
