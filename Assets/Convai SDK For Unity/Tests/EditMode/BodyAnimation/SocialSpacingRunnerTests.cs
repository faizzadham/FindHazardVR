using System;
using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Locomotion;
using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="SocialSpacingRunner" />: the NavMesh-sampling wiring around
    ///     <see cref="SocialSpacingPolicy" /> (itself covered in isolation by
    ///     <see cref="SocialSpacingPolicyTests" />) plus its own (re)build/clear lifecycle — the
    ///     lifecycle work item 2's shared <c>LayerStackBuilder</c> now drives on every build and
    ///     every set-swap handoff.
    /// </summary>
    public sealed class SocialSpacingRunnerTests
    {
        private sealed class FakeDrive : ILocomotionDrive, IConvaiLocomotionCommands
        {
            public bool MoveToCalled { get; private set; }
            public bool IsMoving => false;
            public float Speed => 0f;
            public float DesiredSpeed => 0f;
            public float RemainingDistance => 0f;
            public float SignedAngleToSteering => 0f;
            public Vector3 Destination => Vector3.zero;
            public bool InManagedMotion => false;
            public bool RotationDrivenExternally { get; set; }
            public bool PathPending => false;
            public event Action<bool> MoveEnded;
            public void Stop() { }
            public void FreezeAgent(bool frozen) { }
            public void BeginManagedMotion() { }
            public void SetManagedSpeed(float speed) { }
            public void EndManagedMotion() { }
            public void ReleaseAnimationStartGate() { }
            public void CompleteMoveFromAnimation() { }
            public void SetAnimationStartGate(bool enabled) { }
            public void ConfigureSpeeds(float walkSpeed, float jogSpeed) { }
            public bool MoveTo(Vector3 destination) { MoveToCalled = true; return true; }
        }

        private const float ComfortRadius = 0.7f;
        private const float ComfortHoldSeconds = 0.3f;
        private const float Dt = 1f / 30f;

        [Test]
        public void Tick_BeforeRebuild_IsANoOp()
        {
            var runner = new SocialSpacingRunner();
            var root = new GameObject("SocialSpacingRunnerTestRoot");
            var drive = new FakeDrive();
            try
            {
                Assert.DoesNotThrow(() => runner.Tick(
                    root.transform, drive, null, DialogueState.Idle, Dt, false, true, new Vector3(0.2f, 0, 0)));
                Assert.IsFalse(drive.MoveToCalled, "No policy has been built yet — the tick must be inert.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Tick_NoConversantAnchor_NeverFires()
        {
            var runner = new SocialSpacingRunner();
            runner.Rebuild(ComfortRadius, ComfortHoldSeconds, 3);
            var root = new GameObject("SocialSpacingRunnerTestRoot");
            var drive = new FakeDrive();
            var trace = new AnimTrace("SocialSpacingRunnerTest") { Verbosity = AnimTraceVerbosity.Detail };
            try
            {
                for (int i = 0; i < 60; i++)
                    runner.Tick(root.transform, drive, trace, DialogueState.Idle, Dt, false, false, Vector3.zero);

                Assert.IsFalse(drive.MoveToCalled);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Tick_SustainedProximity_FiresAndAttemptsNavMeshSample()
        {
            var runner = new SocialSpacingRunner();
            runner.Rebuild(ComfortRadius, ComfortHoldSeconds, 3);
            var root = new GameObject("SocialSpacingRunnerTestRoot");
            var drive = new FakeDrive();
            var trace = new AnimTrace("SocialSpacingRunnerTest") { Verbosity = AnimTraceVerbosity.Detail };
            var conversant = new Vector3(0.2f, 0f, 0f); // well inside ComfortRadius
            try
            {
                for (int i = 0; i < 60; i++)
                    runner.Tick(root.transform, drive, trace, DialogueState.Idle, Dt, false, true, conversant);

                // An EditMode scene has no baked NavMesh, so the sample always fails — but the
                // policy having fired at all is observable through the "no NavMesh within" trace
                // line (the "attempt still counted" degradation path), which is what proves the
                // runner actually reached the sampling step rather than staying inert.
                var entries = new List<AnimTraceEntry>();
                trace.CopyRecentEntries(entries);
                bool sampledAttempt = entries.Exists(e => e.Message.Contains("no NavMesh within"));

                Assert.IsTrue(sampledAttempt,
                    "sustained proximity must eventually fire the policy and reach the NavMesh sample step");
                Assert.IsFalse(drive.MoveToCalled, "no baked NavMesh in an EditMode scene — MoveTo must not fire");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Clear_MakesSubsequentTicksInertAgain()
        {
            var runner = new SocialSpacingRunner();
            runner.Rebuild(ComfortRadius, ComfortHoldSeconds, 3);
            runner.Clear();

            var root = new GameObject("SocialSpacingRunnerTestRoot");
            var drive = new FakeDrive();
            try
            {
                for (int i = 0; i < 60; i++)
                    runner.Tick(
                        root.transform, drive, null, DialogueState.Idle, Dt, false, true,
                        new Vector3(0.2f, 0, 0));

                Assert.IsFalse(drive.MoveToCalled, "Clear() must drop the policy so ticks go inert again.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
