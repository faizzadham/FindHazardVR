using System;
using System.Collections.Generic;
using Convai.Modules.BodyAnimation;
using Convai.Modules.BodyAnimation.Core.Locomotion;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Drives <see cref="PlayActionAtRunner" /> end-to-end through a stubbed
    ///     <see cref="IAnchorMovementDrive" /> and a fake action-play callback — no NavMeshAgent,
    ///     no scene, fully deterministic. Covers the Approaching → Aligning → PlayingAction
    ///     phase sequence, the degrade-without-retry policy, and cancellation at each phase.
    /// </summary>
    public class PlayActionAtRunnerTests
    {
        // ------------------------------------------------------------------ stub drive

        private sealed class StubAnchorMovementDrive : IAnchorMovementDrive
        {
            public bool MoveToResult = true;
            public Vector3 LastMoveToTarget;
            public int MoveToCallCount;
            public int StopCallCount;
            public int BeginAlignmentCount;
            public int EndAlignmentCount;
            public Vector3 LastAlignedPosition;
            public float LastAlignedYaw;

            public bool MoveTo(Vector3 worldPosition)
            {
                MoveToCallCount++;
                LastMoveToTarget = worldPosition;
                return MoveToResult;
            }

            public void Stop() => StopCallCount++;

            public event Action<bool> MoveEnded;

            public void RaiseMoveEnded(bool arrived) => MoveEnded?.Invoke(arrived);

            public bool IsSettled { get; set; }
            public Vector3 RootPosition { get; set; }
            public float RootYawDegrees { get; set; }

            public void BeginAlignment() => BeginAlignmentCount++;

            public void SetAlignmentPose(Vector3 position, float yawDegrees)
            {
                RootPosition = position;
                RootYawDegrees = yawDegrees;
                LastAlignedPosition = position;
                LastAlignedYaw = yawDegrees;
            }

            public void EndAlignment() => EndAlignmentCount++;
        }

        // ------------------------------------------------------------------ fixture

        private readonly List<Object> _cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
        }

        private ActionEntry MakeEntry(string name = "sit")
        {
            var clip = new AnimationClip { name = name };
            _cleanup.Add(clip);

            var entry = new ActionEntry();
            entry.Initialize(name, clip, ActionMaskMode.FullBody);
            return entry;
        }

        private static PlayActionAtRunner MakeRunner(
            ActionEntry entry,
            StubAnchorMovementDrive drive,
            out List<BodyAnimationActionHandle> playedHandles,
            Func<BodyAnimationActionHandle> playActionResult = null,
            ActionAnchorOptions options = null,
            AnchorPose? anchorPose = null,
            List<string> degradeLog = null)
        {
            var handles = new List<BodyAnimationActionHandle>();
            playedHandles = handles;

            AnchorPose pose = anchorPose ?? new AnchorPose(Vector3.zero, 0f);
            options ??= new ActionAnchorOptions();
            List<string> log = degradeLog ?? new List<string>();

            return new PlayActionAtRunner(
                entry,
                in ActionPlayOptionsDefault,
                options,
                in pose,
                drive,
                (playEntry, playOptions) =>
                {
                    BodyAnimationActionHandle handle = playActionResult != null
                        ? playActionResult()
                        : new BodyAnimationActionHandle(playEntry.ActionName);
                    handles.Add(handle);
                    return handle;
                },
                log.Add);
        }

        private static readonly ActionPlayOptions ActionPlayOptionsDefault = default;

        // ------------------------------------------------------------------ approach

        [Test]
        public void Start_IssuesMoveTo_WithComputedApproachPoint()
        {
            var drive = new StubAnchorMovementDrive();
            ActionEntry entry = MakeEntry();
            var options = new ActionAnchorOptions();
            var anchor = new AnchorPose(new Vector3(2f, 0f, 3f), 0f);

            PlayActionAtRunner runner = MakeRunner(
                entry, drive, out _, options: options, anchorPose: anchor);
            runner.Start();

            Assert.AreEqual(1, drive.MoveToCallCount);
            Assert.AreEqual(PlayActionAtPhase.Approaching, runner.Handle.Phase);
            Assert.AreEqual(new Vector3(2f, 0f, 3.5f), drive.LastMoveToTarget); // default offset (0,0,0.5)
        }

        [Test]
        public void NoPath_DegradesImmediately_SkipsApproachAndAlign()
        {
            var drive = new StubAnchorMovementDrive { MoveToResult = false };
            ActionEntry entry = MakeEntry();
            var log = new List<string>();

            PlayActionAtRunner runner = MakeRunner(entry, drive, out List<BodyAnimationActionHandle> handles, degradeLog: log);
            runner.Start();

            Assert.AreEqual(PlayActionAtPhase.PlayingAction, runner.Handle.Phase);
            Assert.AreEqual(1, handles.Count);
            Assert.AreEqual(1, log.Count);
            Assert.AreEqual(0, drive.BeginAlignmentCount);
        }

        [Test]
        public void ArrivalThenSettle_EntersAligning_WhenWithinEnvelope()
        {
            var drive = new StubAnchorMovementDrive { RootPosition = new Vector3(0.05f, 0f, 0.45f), RootYawDegrees = 2f };
            ActionEntry entry = MakeEntry();

            PlayActionAtRunner runner = MakeRunner(entry, drive, out _);
            runner.Start();

            drive.RaiseMoveEnded(true);
            Assert.AreEqual(PlayActionAtPhase.Approaching, runner.Handle.Phase); // still waiting for settle

            drive.IsSettled = true;
            runner.Tick(0.016f);

            Assert.AreEqual(PlayActionAtPhase.Aligning, runner.Handle.Phase);
            Assert.AreEqual(1, drive.BeginAlignmentCount);
        }

        [Test]
        public void OutsideEnvelope_DegradesToPlayingAction_NoRetry()
        {
            var drive = new StubAnchorMovementDrive { RootPosition = new Vector3(5f, 0f, 5f), RootYawDegrees = 0f };
            ActionEntry entry = MakeEntry();
            var log = new List<string>();

            PlayActionAtRunner runner = MakeRunner(entry, drive, out List<BodyAnimationActionHandle> handles, degradeLog: log);
            runner.Start();

            drive.RaiseMoveEnded(true);
            drive.IsSettled = true;
            runner.Tick(0.016f);

            Assert.AreEqual(PlayActionAtPhase.PlayingAction, runner.Handle.Phase);
            Assert.AreEqual(1, handles.Count);
            Assert.AreEqual(1, log.Count);
            Assert.AreEqual(1, drive.MoveToCallCount); // no retry MoveTo
            Assert.AreEqual(0, drive.BeginAlignmentCount);
        }

        [Test]
        public void MoveCanceledExternally_ResolvesCanceled()
        {
            var drive = new StubAnchorMovementDrive();
            ActionEntry entry = MakeEntry();

            PlayActionAtRunner runner = MakeRunner(entry, drive, out _);
            runner.Start();

            drive.RaiseMoveEnded(false);

            Assert.IsTrue(runner.Handle.IsDone);
            Assert.AreEqual(PlayActionAtPhase.Canceled, runner.Handle.Phase);
            Assert.IsFalse(runner.Handle.Completion.Result);
        }

        // ------------------------------------------------------------------ align → act

        [Test]
        public void AlignmentLerp_ReachesTargetPose_ThenPlaysAction()
        {
            // Within the default 0.4m/45° envelope of the approach point (0,0,0.5).
            var drive = new StubAnchorMovementDrive { RootPosition = new Vector3(0f, 0f, 0.45f), RootYawDegrees = 0f };
            ActionEntry entry = MakeEntry();
            var options = new ActionAnchorOptions(); // 0.3s alignment duration
            var anchor = new AnchorPose(Vector3.zero, 0f);

            PlayActionAtRunner runner = MakeRunner(
                entry, drive, out List<BodyAnimationActionHandle> handles, options: options, anchorPose: anchor);
            runner.Start();
            drive.RaiseMoveEnded(true);
            drive.IsSettled = true;
            runner.Tick(0.016f); // enters Aligning

            Assert.AreEqual(PlayActionAtPhase.Aligning, runner.Handle.Phase);

            runner.Tick(0.5f); // longer than the 0.3s alignment duration — snaps to target

            Assert.AreEqual(PlayActionAtPhase.PlayingAction, runner.Handle.Phase);
            Assert.AreEqual(1, drive.EndAlignmentCount);
            Assert.AreEqual(1, handles.Count);

            // Begin/end symmetry: every BeginAlignment on the drive is matched by exactly one
            // EndAlignment (the seam that must toggle NavMeshAgent.updatePosition + Warp back).
            Assert.AreEqual(drive.BeginAlignmentCount, drive.EndAlignmentCount);
        }

        [Test]
        public void InnerActionCompletes_ResolvesOuterCompletedTrue()
        {
            var drive = new StubAnchorMovementDrive { MoveToResult = false }; // degrade straight to Act
            ActionEntry entry = MakeEntry();
            BodyAnimationActionHandle inner = null;

            PlayActionAtRunner runner = MakeRunner(
                entry, drive, out _,
                playActionResult: () =>
                {
                    inner = new BodyAnimationActionHandle(entry.ActionName);
                    return inner;
                });
            runner.Start();

            Assert.AreEqual(PlayActionAtPhase.PlayingAction, runner.Handle.Phase);
            Assert.IsFalse(runner.Handle.IsDone);

            inner.ResolveCompleted();
            runner.Tick(0.016f);

            Assert.IsTrue(runner.Handle.IsDone);
            Assert.AreEqual(PlayActionAtPhase.Completed, runner.Handle.Phase);
            Assert.IsTrue(runner.Handle.Completion.Result);
        }

        [Test]
        public void InnerActionInterrupted_ResolvesOuterCanceledFalse()
        {
            var drive = new StubAnchorMovementDrive { MoveToResult = false };
            ActionEntry entry = MakeEntry();
            BodyAnimationActionHandle inner = null;

            PlayActionAtRunner runner = MakeRunner(
                entry, drive, out _,
                playActionResult: () =>
                {
                    inner = new BodyAnimationActionHandle(entry.ActionName);
                    return inner;
                });
            runner.Start();

            inner.ResolveInterrupted();
            runner.Tick(0.016f);

            Assert.IsTrue(runner.Handle.IsDone);
            Assert.AreEqual(PlayActionAtPhase.Canceled, runner.Handle.Phase);
            Assert.IsFalse(runner.Handle.Completion.Result);
        }

        [Test]
        public void PlayActionRefused_ResolvesCanceled()
        {
            var drive = new StubAnchorMovementDrive { MoveToResult = false };
            ActionEntry entry = MakeEntry();

            PlayActionAtRunner runner = MakeRunner(
                entry, drive, out _, playActionResult: () => null);
            runner.Start();

            Assert.IsTrue(runner.Handle.IsDone);
            Assert.AreEqual(PlayActionAtPhase.Canceled, runner.Handle.Phase);
            Assert.IsFalse(runner.Handle.Completion.Result);
        }

        // ------------------------------------------------------------------ cancel per-phase

        [Test]
        public void Cancel_DuringApproaching_StopsLocomotion_AndResolvesCanceled()
        {
            var drive = new StubAnchorMovementDrive();
            ActionEntry entry = MakeEntry();

            PlayActionAtRunner runner = MakeRunner(entry, drive, out _);
            runner.Start();

            runner.Handle.Cancel();

            Assert.AreEqual(1, drive.StopCallCount);
            Assert.IsTrue(runner.Handle.IsDone);
            Assert.AreEqual(PlayActionAtPhase.Canceled, runner.Handle.Phase);
            Assert.IsFalse(runner.Handle.Completion.Result);

            // Idempotent — a second cancel (or a late MoveEnded) is a no-op.
            runner.Handle.Cancel();
            drive.RaiseMoveEnded(true);
            Assert.AreEqual(1, drive.StopCallCount);
        }

        [Test]
        public void Cancel_DuringAligning_FreezesInPlace_AndResolvesCanceled()
        {
            var drive = new StubAnchorMovementDrive { RootPosition = new Vector3(0f, 0f, 0.45f), RootYawDegrees = 0f };
            ActionEntry entry = MakeEntry();

            PlayActionAtRunner runner = MakeRunner(entry, drive, out _);
            runner.Start();
            drive.RaiseMoveEnded(true);
            drive.IsSettled = true;
            runner.Tick(0.016f);
            Assert.AreEqual(PlayActionAtPhase.Aligning, runner.Handle.Phase);

            runner.Handle.Cancel();

            Assert.AreEqual(1, drive.EndAlignmentCount);
            Assert.AreEqual(drive.BeginAlignmentCount, drive.EndAlignmentCount); // begin/end symmetry
            Assert.IsTrue(runner.Handle.IsDone);
            Assert.AreEqual(PlayActionAtPhase.Canceled, runner.Handle.Phase);
            Assert.IsFalse(runner.Handle.Completion.Result);
        }

        [Test]
        public void Cancel_DuringApproaching_NeverEntersAlignment_NoBeginEndCalls()
        {
            // Canceled before arrival — the drive's alignment seam (which owns
            // NavMeshAgent.updatePosition/Warp coordination in production) must never see a
            // Begin without an End, and here it must see neither.
            var drive = new StubAnchorMovementDrive();
            ActionEntry entry = MakeEntry();

            PlayActionAtRunner runner = MakeRunner(entry, drive, out _);
            runner.Start();
            runner.Handle.Cancel();

            Assert.AreEqual(0, drive.BeginAlignmentCount);
            Assert.AreEqual(0, drive.EndAlignmentCount);
        }

        [Test]
        public void Cancel_DuringPlayingAction_StopsInnerAction_AndResolvesCanceledImmediately()
        {
            var drive = new StubAnchorMovementDrive { MoveToResult = false };
            ActionEntry entry = MakeEntry();
            var innerStopCalls = 0;

            PlayActionAtRunner runner = MakeRunner(
                entry, drive, out _,
                playActionResult: () =>
                {
                    var handle = new BodyAnimationActionHandle(entry.ActionName)
                    {
                        StopRequested = () => innerStopCalls++
                    };
                    return handle;
                });
            runner.Start();
            Assert.AreEqual(PlayActionAtPhase.PlayingAction, runner.Handle.Phase);

            runner.Handle.Cancel();

            Assert.AreEqual(1, innerStopCalls);
            Assert.IsTrue(runner.Handle.IsDone);
            Assert.AreEqual(PlayActionAtPhase.Canceled, runner.Handle.Phase);
            Assert.IsFalse(runner.Handle.Completion.Result);
        }

        [Test]
        public void Cancel_AfterCompletion_IsNoOp()
        {
            var drive = new StubAnchorMovementDrive { MoveToResult = false };
            ActionEntry entry = MakeEntry();
            BodyAnimationActionHandle inner = null;

            PlayActionAtRunner runner = MakeRunner(
                entry, drive, out _,
                playActionResult: () =>
                {
                    inner = new BodyAnimationActionHandle(entry.ActionName);
                    return inner;
                });
            runner.Start();
            inner.ResolveCompleted();
            runner.Tick(0.016f);
            Assert.IsTrue(runner.Handle.IsDone);

            Assert.DoesNotThrow(() => runner.Handle.Cancel());
            Assert.IsTrue(runner.Handle.Completion.Result); // unchanged
        }
    }
}
