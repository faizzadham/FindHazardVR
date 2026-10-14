using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Core.Lifecycle;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="AnimationSetHandoffCoordinator" />: the retiring layer
    ///     stack's lifetime (previously duplicated at two call sites in the controller — the
    ///     handoff-completion path and <c>TeardownRuntime</c>), and the pure grace/force
    ///     escalation timing decision (<see cref="EscalationAction" />).
    /// </summary>
    public sealed class AnimationSetHandoffCoordinatorTests
    {
        private sealed class FakeLayer : IAnimationLayer
        {
            public int TeardownCount { get; private set; }
            public string Name => "Fake";
            public float Weight => 0f;
            public string StateLabel => string.Empty;
            public string ActiveClipName => string.Empty;
            public float ActiveNormalizedTime => 0f;
            public void Initialize(LayerRuntime runtime, int port) { }
            public void Tick(in LayerTickContext context) { }
            public void Teardown() => TeardownCount++;
        }

        // ------------------------------------------------------------------ retiring layers

        [Test]
        public void TeardownRetiringLayers_BeforeBeginRetiring_IsANoOp()
        {
            var coordinator = new AnimationSetHandoffCoordinator();
            Assert.DoesNotThrow(coordinator.TeardownRetiringLayers);
        }

        [Test]
        public void BeginRetiring_ThenTeardown_TearsDownEveryRetiredLayerExactlyOnce()
        {
            var coordinator = new AnimationSetHandoffCoordinator();
            var a = new FakeLayer();
            var b = new FakeLayer();
            var outgoing = new List<IAnimationLayer> { a, b };

            coordinator.BeginRetiring(outgoing);
            coordinator.TeardownRetiringLayers();

            Assert.AreEqual(1, a.TeardownCount);
            Assert.AreEqual(1, b.TeardownCount);
        }

        [Test]
        public void TeardownRetiringLayers_SecondCall_IsANoOp()
        {
            var coordinator = new AnimationSetHandoffCoordinator();
            var a = new FakeLayer();
            coordinator.BeginRetiring(new List<IAnimationLayer> { a });

            coordinator.TeardownRetiringLayers();
            coordinator.TeardownRetiringLayers();

            Assert.AreEqual(1, a.TeardownCount, "a retired stack must be torn down exactly once, however many times Teardown is called.");
        }

        [Test]
        public void BeginRetiring_SnapshotsTheList_LaterMutationOfTheSourceListIsIgnored()
        {
            var coordinator = new AnimationSetHandoffCoordinator();
            var a = new FakeLayer();
            var source = new List<IAnimationLayer> { a };

            coordinator.BeginRetiring(source);
            source.Clear(); // mirrors the controller reusing _layers for the new stack immediately after

            coordinator.TeardownRetiringLayers();

            Assert.AreEqual(1, a.TeardownCount, "BeginRetiring must copy the list, not alias the controller's live _layers.");
        }

        // ------------------------------------------------------------------ escalation timing

        [Test]
        public void EvaluateEscalation_BeforeGrace_NeitherFires()
        {
            EscalationAction action = AnimationSetHandoffCoordinator.EvaluateEscalation(
                elapsedSeconds: 3f, graceAlreadyIssued: false, graceSeconds: 5f, forceSeconds: 10f);

            Assert.IsFalse(action.IssueGrace);
            Assert.IsFalse(action.Force);
        }

        [Test]
        public void EvaluateEscalation_AtGraceMark_IssuesGraceOnce()
        {
            EscalationAction action = AnimationSetHandoffCoordinator.EvaluateEscalation(
                elapsedSeconds: 5f, graceAlreadyIssued: false, graceSeconds: 5f, forceSeconds: 10f);

            Assert.IsTrue(action.IssueGrace);
            Assert.IsFalse(action.Force);
        }

        [Test]
        public void EvaluateEscalation_PastGrace_GraceAlreadyIssued_DoesNotReissue()
        {
            EscalationAction action = AnimationSetHandoffCoordinator.EvaluateEscalation(
                elapsedSeconds: 7f, graceAlreadyIssued: true, graceSeconds: 5f, forceSeconds: 10f);

            Assert.IsFalse(action.IssueGrace, "grace must be issued exactly once per queued swap.");
            Assert.IsFalse(action.Force);
        }

        [Test]
        public void EvaluateEscalation_AtForceMark_Forces()
        {
            EscalationAction action = AnimationSetHandoffCoordinator.EvaluateEscalation(
                elapsedSeconds: 10f, graceAlreadyIssued: true, graceSeconds: 5f, forceSeconds: 10f);

            Assert.IsTrue(action.Force);
        }

        [Test]
        public void EvaluateEscalation_PastForce_GraceNeverIssued_StillIssuesGraceAndForces()
        {
            // A pathological case (e.g. a huge deltaTime jump) skipping straight past both marks
            // must still ask blockers to yield, not silently force without ever having asked.
            EscalationAction action = AnimationSetHandoffCoordinator.EvaluateEscalation(
                elapsedSeconds: 12f, graceAlreadyIssued: false, graceSeconds: 5f, forceSeconds: 10f);

            Assert.IsTrue(action.IssueGrace);
            Assert.IsTrue(action.Force);
        }
    }
}
