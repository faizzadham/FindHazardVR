using System;
using System.Collections;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyLanguage
{
    /// <summary>
    ///     Live controller-glue coverage for the scripted API — the
    ///     piece the EditMode POCO tests cannot reach: on an actually-ticking
    ///     <see cref="ConvaiBodyLanguageController" />, <see cref="ConvaiBodyLanguageController.Nod" />
    ///     handles complete on program end, a request on an unusable controller degrades to an
    ///     already-completed handle (Fix 1), <see cref="ConvaiBodyLanguageController.PulseGesture" />
    ///     completes on dispatch, <see cref="ConvaiBodyLanguageController.ClearScriptedOverrides" />
    ///     completes a scripted handle without throwing (Fix 2), and
    ///     <see cref="ConvaiBodyLanguageController.Current" /> publishes through the context slot.
    /// </summary>
    /// <remarks>
    ///     PlayMode (not EditMode) because the controller only runs its tick / registers its
    ///     runtime slots while <c>Application.isPlaying</c> — modeled directly on this folder's
    ///     <c>HeadGestureChannelIntegrationTests</c> harness (itself modeled on the
    ///     <c>BodyPoseOrderingTests</c>): a plain Animator so StandardRigBinding resolves bones
    ///     by name, an <see cref="EmbodimentContext" />, and a fast-slew default profile with the
    ///     head-gesture refractory zeroed for determinism.
    /// </remarks>
    public sealed class ScriptedApiControllerGlueTests
    {
        /// <summary>
        ///     Waits for <paramref name="isDone" /> or a wall-clock (<see cref="Time.realtimeSinceStartup" />)
        ///     deadline, whichever comes first. Replaces a frame-COUNT budget, which is only
        ///     valid under an assumed per-frame <c>Time.deltaTime</c> — this headless runner's
        ///     frames can each carry far less simulated time than an interactive editor frame,
        ///     so the same frame count can cover far less than a 1.15s Nod program's
        ///     simulated duration. Real time is what the program actually
        ///     needs, in any environment.
        /// </summary>
        private static IEnumerator WaitUntil(Func<bool> isDone, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!isDone() && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        private GameObject _root;
        private ConvaiBodyLanguageController _controller;
        private EmbodimentContext _context;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("ScriptedApiControllerGlueRoot");

            Transform spine = NewChild(_root.transform, "Spine", new Vector3(0f, 1f, 0f));
            Transform chest = NewChild(spine, "Chest", new Vector3(0f, 0.15f, 0f));
            Transform upperChest = NewChild(chest, "UpperChest", new Vector3(0f, 0.15f, 0f));
            Transform neck = NewChild(upperChest, "Neck", new Vector3(0f, 0.1f, 0f));
            NewChild(neck, "Head", new Vector3(0f, 0.1f, 0f));

            _root.AddComponent<Animator>();
            _context = _root.AddComponent<EmbodimentContext>();

            var profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(profile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(profile, "postureFadeSeconds", 0.01f);
            SetPrivateField(profile, "policyTransitionSeconds", 0f);
            SetPrivateField(profile, "headGestureNodMaxPitchDegrees", 8f);
            SetPrivateField(profile, "headGestureRefractorySeconds", 0f);
            SetPrivateField(profile, "headGestureRefractoryVarianceSeconds", 0f);

            _controller = _root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(_controller, "profile", profile);
            _controller.enabled = false;
            _controller.enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [UnityTest]
        public IEnumerator Nod_LiveController_ReturnsActiveHandle_ThatCompletesWithinFrameBudget()
        {
            yield return null;

            HeadGestureHandle handle = _controller.Nod(HeadGestureKind.Nod);

            Assert.IsNotNull(handle, "Nod must never return null.");
            Assert.IsTrue(handle.IsActive, "A live controller must accept and track the request.");
            Assert.IsFalse(handle.Completion.IsCompleted, "The handle must not complete before the program ends.");

            yield return WaitUntil(() => handle.Completion.IsCompleted, 8f);

            Assert.IsTrue(handle.Completion.IsCompleted, "The handle must complete once the program ends.");
            Assert.IsFalse(handle.IsActive, "A completed handle must report inactive.");
        }

        [UnityTest]
        public IEnumerator Nod_DisabledController_ReturnsAlreadyCompletedHandle()
        {
            yield return null;

            _controller.enabled = false;
            yield return null;

            HeadGestureHandle handle = _controller.Nod(HeadGestureKind.Nod);

            Assert.IsNotNull(handle, "Nod must never return null, even on a disabled controller.");
            Assert.IsTrue(handle.Completion.IsCompleted,
                "A disabled controller can never tick, so the handle must be already completed (Fix 1).");
            Assert.IsFalse(handle.IsActive);
        }

        [UnityTest]
        public IEnumerator Nod_InertController_ReturnsAlreadyCompletedHandle()
        {
            // A controller whose rig has no Spine goes inert (one logged error, no per-tick work).
            // A separate rig-less root exercises that path without disturbing the SetUp rig.
            var inertRoot = new GameObject("InertBodyLanguageRoot");
            try
            {
                LogAssert.ignoreFailingMessages = true; // the inert path logs one rig error by design
                inertRoot.AddComponent<Animator>();
                inertRoot.AddComponent<EmbodimentContext>();
                var profile = ConvaiBodyLanguageProfile.CreateDefault();
                var inertController = inertRoot.AddComponent<ConvaiBodyLanguageController>();
                SetPrivateField(inertController, "profile", profile);
                inertController.enabled = false;
                inertController.enabled = true;

                yield return null;

                Assert.IsTrue(inertController.IsInert, "A rig without a Spine bone must make the controller inert.");

                HeadGestureHandle handle = inertController.Nod(HeadGestureKind.Nod);

                Assert.IsNotNull(handle);
                Assert.IsTrue(handle.Completion.IsCompleted,
                    "An inert controller never processes handles, so the handle must be already completed (Fix 1).");
                Assert.IsFalse(handle.IsActive);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(inertRoot);
            }
        }

        [UnityTest]
        public IEnumerator TriggerReaction_DisabledController_IsSafeNoOp()
        {
            // Mirrors Nod_DisabledController_ReturnsAlreadyCompletedHandle (Fix 1): TriggerReaction
            // returns void (no handle), so "safe no-op" means it must never throw on a controller
            // that cannot tick.
            yield return null;

            _controller.enabled = false;
            yield return null;

            Assert.DoesNotThrow(() => _controller.TriggerReaction(ReactionKind.SurpriseFlinch),
                "TriggerReaction on a disabled controller must be a safe no-op, never throw.");
        }

        [UnityTest]
        public IEnumerator TriggerReaction_LiveController_DoesNotThrow_AndControllerKeepsTicking()
        {
            yield return null;

            Assert.DoesNotThrow(() => _controller.TriggerReaction(ReactionKind.SurpriseFlinch));

            // Smoke assert the controller is still alive/ticking afterward (no exception left it
            // in a broken state).
            for (int i = 0; i < 5; i++)
                yield return null;

            Assert.IsFalse(_controller.IsInert);
        }

        [UnityTest]
        public IEnumerator PulseGesture_LiveController_ReturnsCompletedHandle()
        {
            yield return null;

            GestureCueHandle handle = _controller.PulseGesture(new GestureCue(GestureCueKind.Affirmative, 1f));

            Assert.IsNotNull(handle, "PulseGesture must never return null.");
            Assert.IsTrue(handle.Completion.IsCompleted,
                "PulseGesture resolves on dispatch outcome — the handle must be completed on return.");
        }

        [UnityTest]
        public IEnumerator ClearScriptedOverrides_CompletesScriptedHandle_WithoutThrowing_DirectorStillFunctions()
        {
            yield return null;

            HeadGestureHandle handle = _controller.Nod(HeadGestureKind.Tilt);
            Assert.IsTrue(handle.IsActive);

            Assert.DoesNotThrow(() => _controller.ClearScriptedOverrides(),
                "ClearScriptedOverrides must never throw.");
            Assert.IsTrue(handle.Completion.IsCompleted, "The scripted handle must complete after clearing (Fix 2).");

            // Smoke assert that the controller/director still function afterward: a fresh request
            // is accepted and completes normally.
            HeadGestureHandle after = _controller.Nod(HeadGestureKind.Nod);
            Assert.IsTrue(after.IsActive, "A fresh request after clearing must be accepted (directors still function).");

            yield return WaitUntil(() => after.Completion.IsCompleted, 8f);

            Assert.IsTrue(after.Completion.IsCompleted, "The post-clear request must complete normally.");
        }

        [UnityTest]
        public IEnumerator AfterTicks_CurrentAndContextSlot_PublishNonDefaultReading()
        {
            // Warm a handful of frames so LateUpdate publishes at least once.
            for (int i = 0; i < 5; i++)
                yield return null;

            Assert.AreSame(_controller, _context.BodyLanguageSource,
                "The controller must register itself as the character's body language source.");

            BodyLanguageReading viaController = _controller.Current;
            BodyLanguageReading viaSlot = _context.BodyLanguageSource.Current;

            // Both views read the same private field, so they must agree exactly.
            Assert.That(viaSlot.DialogueState, Is.EqualTo(viaController.DialogueState));
            Assert.That(viaSlot.BreathPhase, Is.EqualTo(viaController.BreathPhase));

            // A ticking breath oscillator advances the phase off its zero start within a few
            // frames — proof the reading is being published from live state, not left at None.
            bool sawNonZeroPhase = viaController.BreathPhase != 0f;
            for (int i = 0; i < 30 && !sawNonZeroPhase; i++)
            {
                yield return null;
                if (_controller.Current.BreathPhase != 0f) sawNonZeroPhase = true;
            }

            Assert.IsTrue(sawNonZeroPhase,
                "After ticking, Current must publish a live (non-default) breath phase.");
        }

        private static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName} on {target.GetType().Name}.");
            field.SetValue(target, value);
        }
    }
}
