using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GlanceAtTests
    {
        private GameObject _root;
        private ConvaiGazeController _controller;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("GlanceTestCharacter");
            _root.AddComponent<ConvaiCharacter>();
            _controller = _root.AddComponent<ConvaiGazeController>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void GlanceAt_PushesCommittedLowPriorityHeldEntry()
        {
            GazeHandle handle = _controller.GlanceAt(new Vector3(1f, 1.6f, 1f), 1.2f);

            Assert.IsNotNull(handle);
            Assert.IsTrue(handle.IsActive);
            Assert.That(_controller.ScriptedStack.Count, Is.EqualTo(1));

            GazeTargetStack.Entry entry = _controller.ScriptedStack.ResolveActive(Time.time);
            Assert.IsNotNull(entry);
            Assert.That(entry.Priority, Is.LessThan(0), "A glance sits below the default GazeAt priority (0).");
            Assert.That(entry.EngagementOverride, Is.EqualTo(1f).Within(0.001f), "Glances are committed.");
            Assert.IsFalse(entry.AllowBodyTurn, "A glance never turns the body.");
            Assert.That(entry.Deadline, Is.LessThan(float.PositiveInfinity), "A glance auto-expires.");
        }

        [Test]
        public void GlanceAt_NullTransform_IsNoOp()
        {
            Assert.IsNull(_controller.GlanceAt((Transform)null));
            Assert.That(_controller.ScriptedStack.Count, Is.EqualTo(0));
        }

        [Test]
        public void GlanceAt_ClampsVeryShortDurationToMinimum()
        {
            _controller.GlanceAt(new Vector3(1f, 1f, 1f), 0.01f);

            GazeTargetStack.Entry entry = _controller.ScriptedStack.ResolveActive(Time.time);
            Assert.That(entry.Deadline - Time.time, Is.GreaterThanOrEqualTo(0.2f - 0.01f),
                "Sub-0.2 s glances are clamped so the eye stage can visibly land the glance.");
        }

        [Test]
        public void ExplicitGazeAt_OutranksActiveGlance_AndHandsBackOnRelease()
        {
            _controller.GlanceAt(new Vector3(1f, 1.6f, 1f), 5f);
            GazeHandle explicitLook = _controller.GazeAt(new Vector3(-1f, 1.6f, 1f)); // default priority 0

            GazeTargetStack.Entry active = _controller.ScriptedStack.ResolveActive(Time.time);
            Assert.That(active.Id, Is.EqualTo(explicitLook.EntryId), "An explicit GazeAt outranks a glance.");

            explicitLook.Release();

            active = _controller.ScriptedStack.ResolveActive(Time.time);
            Assert.IsNotNull(active, "Releasing the explicit request hands back to the still-held glance.");
            Assert.IsFalse(active.AllowBodyTurn, "The remaining entry is the glance.");
        }

        [Test]
        public void Glance_Expires_AndCompletesHandle()
        {
            GazeHandle handle = _controller.GlanceAt(new Vector3(1f, 1f, 1f), 1.2f);
            float afterHold = Time.time + 1.3f;

            Assert.IsNull(_controller.ScriptedStack.ResolveActive(afterHold), "The glance is pruned after its hold.");

            _controller.ProcessScriptedHandles(GazeTargetDecision.None);
            Assert.IsTrue(handle.Completion.IsCompleted, "An expired glance completes its handle.");
            Assert.IsFalse(handle.IsActive);
        }

        [Test]
        public void Glance_OutranksProviderTargetWhileHeld_ThenPolicyResumes()
        {
            // Arbiter-level: a glance wins over the policy (player) target while held; once it
            // expires the arbiter returns to the provider target — the "resume" beat that both
            // the eye-contact locks and normal engaged states rely on.
            var arbiter = new GazeTargetArbiter();
            var stack = new GazeTargetStack();
            ConvaiGazeProfile profile = ConvaiGazeProfile.CreateDefault();
            const float dt = 1f / 60f;

            var player = new GazeTargetCandidate(
                GazeTargetKind.Player, 10, 1f, null, new Vector3(0f, 1.6f, 3f), "Player");
            var candidates = new List<GazeTargetCandidate> { player };

            stack.Push(null, new Vector3(3f, 1.6f, 0f), false,
                priority: -5, engagementOverride: 1f, allowBodyTurn: false,
                deadline: 0.5f, name: "glance");

            GazeTargetDecision decision = default;
            for (float t = 0f; t < 0.45f; t += dt)
                decision = arbiter.Tick(candidates, stack.ResolveActive(t), true, profile, dt);
            Assert.That(decision.Kind, Is.EqualTo(GazeTargetKind.Scripted),
                "While held, the glance outranks the provider target.");

            for (float t = 0.5f; t < 1.2f; t += dt)
                decision = arbiter.Tick(candidates, stack.ResolveActive(t), true, profile, dt);
            Assert.That(decision.Kind, Is.EqualTo(GazeTargetKind.Player),
                "After the glance expires the provider (player) target resumes.");

            Object.DestroyImmediate(profile);
        }
    }
}
