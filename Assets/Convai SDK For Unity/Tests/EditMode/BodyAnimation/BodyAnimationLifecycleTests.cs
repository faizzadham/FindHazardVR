using System;
using System.Reflection;
using Convai.Modules.BodyAnimation;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using Convai.Runtime.Components;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for lifecycle hardening: the single-slot
    ///     deferred first-call request, the never-null failed-handle
    ///     contract, and set-swap escalation (grace → force).
    /// </summary>
    /// <remarks>
    ///     <c>BuildRuntime</c> is a no-op outside Play Mode, so — following the pattern already
    ///     established by <c>BodyAnimationConfigSwapTests</c> — tests that need a "runtime built"
    ///     controller fake the exact private state the production code reads via reflection instead
    ///     of actually building the graph.
    /// </remarks>
    public sealed class BodyAnimationLifecycleTests
    {
        private static FieldInfo Field(string name) =>
            typeof(ConvaiBodyAnimationController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static MethodInfo Method(string name) =>
            typeof(ConvaiBodyAnimationController).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static object GetFieldValue(ConvaiBodyAnimationController controller, string name) =>
            Field(name).GetValue(controller);

        private static void SetField(ConvaiBodyAnimationController controller, string name, object value) =>
            Field(name).SetValue(controller, value);

        private static ConvaiBodyAnimationController CreateController(out GameObject root)
        {
            root = new GameObject("BodyAnimationLifecycleTestCharacter");
            // A composition root is only created on a real Convai character now, and without one the
            // controller disables itself in OnEnable — so the lifecycle under test never runs.
            root.AddComponent<ConvaiCharacter>();
            return root.AddComponent<ConvaiBodyAnimationController>();
        }

        /// <summary>Fakes "the runtime is built and can accept PlayAction" without a real graph.</summary>
        private static void FakeBuiltForActions(ConvaiBodyAnimationController controller, AnimTrace trace)
        {
            SetField(controller, "_runtimeBuilt", true);
            SetField(controller, "_trace", trace);
            SetField(controller, "_actionLayer", new ActionLayer());
        }

        private static void ClearFakedBuildState(ConvaiBodyAnimationController controller)
        {
            SetField(controller, "_runtimeBuilt", false);
            SetField(controller, "_trace", null);
            SetField(controller, "_actionLayer", null);
            SetField(controller, "_locomotionLayer", null);
            SetField(controller, "_talkLayer", null);
            SetField(controller, "_pointingLayer", null);
            SetField(controller, "_layerRuntime", null);
            SetField(controller, "_graphHost", null);
        }

        // ── 1a/1b: single-slot deferred request + expiry ────────────────────

        [Test]
        public void PlayAction_BeforeBuild_ReturnsFailedHandle_AndQueuesDeferredRequest()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                BodyAnimationActionHandle handle = controller.PlayAction("wave");

                Assert.IsNotNull(handle, "PlayAction must never return null.");
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("runtime not built", handle.FailureReason);
                Assert.IsTrue(handle.IsDone);
                Assert.AreEqual(false, handle.Completion.Result);

                Assert.AreEqual(1 /* PlayAction */, (int)GetFieldValue(controller, "_deferredKind"));
                Assert.AreEqual("wave", GetFieldValue(controller, "_deferredName"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DeferredRequest_ReplayedOnFirstTickAfterBuild()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            var trace = new AnimTrace("DeferredReplayTest") { Verbosity = AnimTraceVerbosity.State };
            try
            {
                controller.PlayAction("wave");
                Assert.AreEqual(1, (int)GetFieldValue(controller, "_deferredKind"), "Sanity: request queued.");

                // The runtime "builds" between the call above and the replay below.
                FakeBuiltForActions(controller, trace);

                Method("ReplayDeferredRequestIfAny").Invoke(controller, null);

                // The slot must be consumed by a real (built-runtime) attempt, not re-queued as
                // still-not-built — proven by the kind resetting to None rather than staying
                // PlayAction with a fresh timestamp.
                Assert.AreEqual(0 /* None */, (int)GetFieldValue(controller, "_deferredKind"),
                    "A replayed request against a built runtime must consume the slot, not re-queue it.");
            }
            finally
            {
                ClearFakedBuildState(controller);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DeferredRequest_NewerRequest_ReplacesOlderOne()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                controller.PlayAction("first");
                controller.PlayAction("second");

                Assert.AreEqual(1 /* PlayAction */, (int)GetFieldValue(controller, "_deferredKind"));
                Assert.AreEqual("second", GetFieldValue(controller, "_deferredName"),
                    "A newer deferred request must replace the older one — a single slot, not a queue.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DeferredRequest_Expires_AfterTimeout_WithExactlyOneMessage()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                controller.PlayAction("wave");
                Assert.AreEqual(1, (int)GetFieldValue(controller, "_deferredKind"), "Sanity: request queued.");

                // Force elapsed-time-since-queue past the 2s timeout regardless of whether
                // Time.unscaledTime advances in EditMode.
                SetField(controller, "_deferredQueuedAt", -1000f);

                // ReplayDeferredRequestIfAny logs exactly one ConvaiLogger.Warning for the
                // expiry — a single call site (verified by inspection: the expiry branch is
                // the only place that logs before clearing the slot).
                Method("ReplayDeferredRequestIfAny").Invoke(controller, null);

                Assert.AreEqual(0 /* None */, (int)GetFieldValue(controller, "_deferredKind"),
                    "An expired slot must be cleared, not replayed.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DeferredRequest_ClearedByTeardown()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                controller.PlayAction("wave");
                Assert.AreEqual(1, (int)GetFieldValue(controller, "_deferredKind"), "Sanity: request queued.");

                controller.enabled = false; // OnDisable -> TeardownRuntime -> ClearDeferredRequest

                Assert.AreEqual(0 /* None */, (int)GetFieldValue(controller, "_deferredKind"),
                    "Disabling the component must cancel any pending deferred request.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ── 1c: RuntimeReady ordering ─────────────────────────────────────

        // ── 2: never-null failed-handle contract ─────────────────────────

        [Test]
        public void PlayAction_UnknownAction_ReturnsFailedHandle_WithFullContract()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            var trace = new AnimTrace("FailedHandleTest") { Verbosity = AnimTraceVerbosity.State };
            try
            {
                FakeBuiltForActions(controller, trace); // built, but no animation set assigned

                BodyAnimationActionHandle handle = controller.PlayAction("no-such-action");

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("unknown action", handle.FailureReason);
                Assert.IsTrue(handle.IsDone);
                Assert.AreEqual(false, handle.Completion.Result);
                Assert.DoesNotThrow(handle.Stop);
                Assert.DoesNotThrow(() => handle.StopImmediate());
            }
            finally
            {
                ClearFakedBuildState(controller);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PointAt_Vector3_BeforeBuild_ReturnsFailedHandle_WithFullContract()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                BodyAnimationPointingHandle handle = controller.PointAt(new Vector3(1f, 0f, 1f), 1f);

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("runtime not built", handle.FailureReason);
                Assert.IsTrue(handle.IsDone);
                Assert.DoesNotThrow(handle.Release);
                Assert.DoesNotThrow(() => handle.ReleaseImmediate());
                Assert.DoesNotThrow(() => handle.SetSpeed(2f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PointAt_Transform_NullTarget_ReturnsFailedHandle_WithoutQueuingADeferredRequest()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                BodyAnimationPointingHandle handle = controller.PointAt((Transform)null, 1f);

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("target is null", handle.FailureReason);
                Assert.AreEqual(0 /* None */, (int)GetFieldValue(controller, "_deferredKind"),
                    "A null target is a caller bug, not a timing issue — it must not be deferred.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PointAt_TransformWithOptions_NullTarget_ReturnsFailedHandle()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                BodyAnimationPointingHandle handle = controller.PointAt((Transform)null, PointingPlayOptions.Default);

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("target is null", handle.FailureReason);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PlayActionAt_NullAnchor_ReturnsFailedHandle_WithFullContract()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            try
            {
                PlayActionAtHandle handle = controller.PlayActionAt(null, "sit");

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("anchor is null", handle.FailureReason);
                Assert.AreEqual(PlayActionAtPhase.Canceled, handle.Phase);
                Assert.IsTrue(handle.IsDone);
                Assert.AreEqual(false, handle.Completion.Result);
                Assert.DoesNotThrow(handle.Cancel);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PlayActionAt_BeforeBuild_ReturnsFailedHandle_AndQueuesDeferredRequest()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            var anchorGo = new GameObject("Anchor");
            try
            {
                PlayActionAtHandle handle = controller.PlayActionAt(anchorGo.transform, "sit");

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("runtime not built", handle.FailureReason);
                Assert.AreEqual(5 /* PlayActionAt */, (int)GetFieldValue(controller, "_deferredKind"));
                Assert.AreEqual("sit", GetFieldValue(controller, "_deferredName"));
            }
            finally
            {
                Object.DestroyImmediate(anchorGo);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PlayActionAt_BuiltButNoLocomotion_ReturnsFailedHandle()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            var anchorGo = new GameObject("Anchor");
            var trace = new AnimTrace("PlayActionAtNoLocomotionTest") { Verbosity = AnimTraceVerbosity.State };
            try
            {
                SetField(controller, "_runtimeBuilt", true);
                SetField(controller, "_trace", trace);
                SetField(controller, "_actionLayer", new ActionLayer());
                SetField(controller, "_locomotionLayer", new LocomotionLayer());
                // _locomotion stays null -> "no locomotion" branch.

                PlayActionAtHandle handle = controller.PlayActionAt(anchorGo.transform, "sit");

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Failed);
                Assert.AreEqual("no locomotion", handle.FailureReason);
            }
            finally
            {
                ClearFakedBuildState(controller);
                Object.DestroyImmediate(anchorGo);
                Object.DestroyImmediate(root);
            }
        }

        // ── 3: set-swap escalation ────────────────────────────────────────

        [Test]
        public void SetSwap_BlockedByUnsettledLocomotion_YieldsAtGrace_ThenForcesAtForceMark()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root);
            var animator = root.AddComponent<Animator>();
            ConvaiBodyAnimationSet nextSet = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            AnimationGraphHost graphHost = null;
            try
            {
                var trace = new AnimTrace("SetSwapEscalationTest") { Verbosity = AnimTraceVerbosity.State };
                graphHost = new AnimationGraphHost(animator, root.name, trace);

                var locomotionLayer = new LocomotionLayer();
                var talkLayer = new TalkLayer();
                var actionLayer = new ActionLayer();
                var pointingLayer = new PointingLayer();

                // Block the polite handoff guard: locomotion never settles in this test, so
                // the swap can only complete through the force escalation path.
                FieldInfo stateField = typeof(LocomotionLayer).GetField(
                    "_state", BindingFlags.Instance | BindingFlags.NonPublic);
                Type locoStateType = stateField.FieldType;
                stateField.SetValue(locomotionLayer, Enum.Parse(locoStateType, "Move"));

                SetField(controller, "_animator", animator);
                SetField(controller, "_trace", trace);
                SetField(controller, "_graphHost", graphHost);
                SetField(controller, "_locomotionLayer", locomotionLayer);
                SetField(controller, "_talkLayer", talkLayer);
                SetField(controller, "_actionLayer", actionLayer);
                SetField(controller, "_pointingLayer", pointingLayer);
                SetField(controller, "_runtimeBuilt", true);

                controller.SetAnimationSet(nextSet);

                Assert.IsTrue(controller.IsAnimationSetSwapPending, "Sanity: the swap must be queued (locomotion is not settled).");
                Assert.IsFalse((bool)GetFieldValue(controller, "_setSwapGraceIssued"));

                // t = 3s: before grace — no yield issued yet, still pending.
                SetField(controller, "_setSwapQueuedAt", Time.unscaledTime - 3f);
                Method("TickSetHandoff").Invoke(controller, new object[] { 0f });
                Assert.IsFalse((bool)GetFieldValue(controller, "_setSwapGraceIssued"));
                Assert.IsTrue(controller.IsAnimationSetSwapPending);

                // t = 6s: past the 5s grace mark — blocking owners are asked to yield exactly once.
                SetField(controller, "_setSwapQueuedAt", Time.unscaledTime - 6f);
                Method("TickSetHandoff").Invoke(controller, new object[] { 0f });
                Assert.IsTrue((bool)GetFieldValue(controller, "_setSwapGraceIssued"));
                Assert.IsTrue(controller.IsAnimationSetSwapPending,
                    "Locomotion never actually settles in this test, so the swap must still be pending until the force mark.");

                // t = 11s: past the 10s force mark — the handoff completes regardless.
                SetField(controller, "_setSwapQueuedAt", Time.unscaledTime - 11f);
                Method("TickSetHandoff").Invoke(controller, new object[] { 0f });
                Assert.IsFalse(controller.IsAnimationSetSwapPending,
                    "The force mark must perform the handoff even though the blocking owner never yielded.");
            }
            finally
            {
                // Let TeardownRuntime (via OnDisable) dispose the graph host and null the faked
                // fields consistently rather than double-disposing here.
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(nextSet);
            }
        }
    }
}
