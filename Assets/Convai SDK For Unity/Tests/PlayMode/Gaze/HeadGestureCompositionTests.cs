using System;
using System.Collections;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Gestures;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using Convai.Tests.PlayMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.Gaze
{
    /// <summary>
    ///     End-to-end composition proof that head gestures compose with gaze: a real
    ///     <see cref="ConvaiBodyLanguageController" /> publishing its head-gesture channel and a
    ///     real <see cref="ConvaiGazeController" /> consuming it through the
    ///     <c>HeadGestureArbiter</c>, on one rig, gazing at a locked scripted target. Proves the
    ///     three load-bearing guarantees: the scripted nod composes with
    ///     (never replaces) the aim, there is no double-nod once Gaze is the registered consumer,
    ///     and Body Language's no-consumer fallback stays off while Gaze is present.
    /// </summary>
    public sealed class HeadGestureCompositionTests
    {
        [DefaultExecutionOrder(19399)]
        private sealed class LatePoseMover : MonoBehaviour
        {
            public Transform Target;
            public Vector3 NextLocalPosition;
            public bool MoveOnNextLateUpdate;

            private void LateUpdate()
            {
                if (!MoveOnNextLateUpdate || Target == null) return;
                Target.localPosition = NextLocalPosition;
                MoveOnNextLateUpdate = false;
            }
        }
        private GameObject _root;
        private Transform _spine, _chest, _upperChest, _neck, _head;
        private Quaternion _restNeck, _restHead;
        private ConvaiBodyLanguageController _bodyLanguage;
        private ConvaiGazeController _gaze;
        private ConvaiGazeProfile _gazeProfile;
        private ConvaiBodyLanguageProfile _bodyLanguageProfile;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("HeadGestureCompositionRoot");

            _spine = NewChild(_root.transform, "Spine", new Vector3(0f, 1f, 0f));
            _chest = NewChild(_spine, "Chest", new Vector3(0f, 0.15f, 0f));
            _upperChest = NewChild(_chest, "UpperChest", new Vector3(0f, 0.15f, 0f));
            _neck = NewChild(_upperChest, "Neck", new Vector3(0f, 0.1f, 0f));
            _head = NewChild(_neck, "Head", new Vector3(0f, 0.1f, 0f));

            _restNeck = _neck.localRotation;
            _restHead = _head.localRotation;

            // Plain (non-Humanoid) Animator: StandardRigBinding resolves bones through its
            // name-based fallback tables, exactly like BodyPoseOrderingTests and
            // HeadGestureChannelIntegrationTests.
            _root.AddComponent<Animator>();
            _root.AddComponent<EmbodimentContext>();

            _bodyLanguageProfile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(_bodyLanguageProfile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(_bodyLanguageProfile, "postureFadeSeconds", 0.01f);
            SetPrivateField(_bodyLanguageProfile, "policyTransitionSeconds", 0f);
            SetPrivateField(_bodyLanguageProfile, "headGestureNodMaxPitchDegrees", 8f);
            SetPrivateField(_bodyLanguageProfile, "headGestureRefractorySeconds", 0f);
            SetPrivateField(_bodyLanguageProfile, "headGestureRefractoryVarianceSeconds", 0f);

            _bodyLanguage = _root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(_bodyLanguage, "profile", _bodyLanguageProfile);
            _bodyLanguage.enabled = false;
            _bodyLanguage.enabled = true;

            _gazeProfile = ConvaiGazeProfile.CreateDefault();
            // A short head shift so the aim visibly converges within a handful of frames without
            // the test needing to wait seconds; the ordering/composition being proven here is
            // independent of the authored timings (mirrors BodyPoseOrderingTests' own "proves
            // ordering, not tuning" stance).
            SetPrivateField(_gazeProfile, "headOnsetSeconds", 0f);
            SetPrivateField(_gazeProfile, "headTurnBaseSeconds", 0.05f);
            SetPrivateField(_gazeProfile, "headTurnSecondsPerDegree", 0.001f);
            SetPrivateField(_gazeProfile, "maxHeadAngularSpeed", 720f);
            SetPrivateField(_gazeProfile, "enableAmbientExploration", false);
            SetPrivateField(_gazeProfile, "enableListeningNods", false);

            _gaze = _root.AddComponent<ConvaiGazeController>();
            SetPrivateField(_gaze, "profile", _gazeProfile);
            _gaze.enabled = false;
            _gaze.enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_bodyLanguageProfile != null) Object.DestroyImmediate(_bodyLanguageProfile);
            if (_gazeProfile != null) Object.DestroyImmediate(_gazeProfile);
        }

        private static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        /// <summary>
        ///     Writes a serialized field by name, looking one level into nested settings blocks
        ///     when the target keeps its fields grouped (the Gaze Profile does). Which block owns
        ///     a setting is the asset's business, not a test's.
        /// </summary>
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

            FieldInfo field = target.GetType().GetField(fieldName, Flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            foreach (FieldInfo block in target.GetType().GetFields(Flags))
            {
                FieldInfo nested = block.FieldType.GetField(fieldName, Flags);
                if (nested == null) continue;

                object blockValue = block.GetValue(target);
                if (blockValue == null) continue;

                nested.SetValue(blockValue, value);
                return;
            }

            Assert.Fail($"Missing field {fieldName} on {target.GetType().Name}.");
        }

        private static bool InvokeRequestHeadGesture(ConvaiBodyLanguageController controller, HeadGestureKind kind) =>
            (bool)typeof(ConvaiBodyLanguageController)
                .GetMethod("RequestHeadGesture", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, new object[] { kind, 1f });

        /// <summary>Locks gaze onto a large off-axis world point so the head chain visibly recruits.</summary>
        private GazeHandle LockGazeOffAxis()
        {
            Vector3 target = _head.position + new Vector3(2f, 0f, 1f); // ~63deg off-axis
            return _gaze.GazeAt(target, new GazeOptions { Engagement = 1f, HoldSeconds = 0f, AllowBodyTurn = false });
        }

        /// <summary>
        ///     Runs frames for up to <paramref name="realSeconds" /> of wall-clock
        ///     (<see cref="Time.realtimeSinceStartup" />) time, invoking <paramref name="perFrame" />
        ///     every frame — used instead of a fixed frame COUNT, which is only valid under an
        ///     assumed per-frame <c>Time.deltaTime</c>. This headless runner's frames can each
        ///     carry far less simulated time than an interactive editor frame, so real time is
        ///     what a spring settle or a nod program longer than a second actually needs.
        /// </summary>
        private static IEnumerator RunForRealSeconds(float realSeconds, Action perFrame = null)
        {
            float deadline = Time.realtimeSinceStartup + realSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                perFrame?.Invoke();
            }
        }

        [UnityTest]
        public IEnumerator NodDuringLockedGaze_AimErrorStaysBounded()
        {
            GazeHandle handle = LockGazeOffAxis();
            Assert.NotNull(handle);

            // Let the aim converge before requesting the nod, so the assertion isolates the
            // nod's effect on aim rather than the initial settle transient. Wall-clock (not
            // frame-count) bounded — see RunForRealSeconds.
            float settledYaw = 0f;
            yield return RunForRealSeconds(3f, () => settledYaw = _gaze.CaptureSnapshot().HeadAngles.x);
            Assert.That(Mathf.Abs(settledYaw), Is.GreaterThan(5f), "Sanity: the head must have visibly recruited toward the off-axis target.");

            Assert.IsTrue(InvokeRequestHeadGesture(_bodyLanguage, HeadGestureKind.Nod));

            float maxYawDuringNod = 0f;
            yield return RunForRealSeconds(4f, () =>
                maxYawDuringNod = Mathf.Max(maxYawDuringNod, Mathf.Abs(_gaze.CaptureSnapshot().HeadAngles.x)));

            // The nod is a pitch gesture — it must not perturb the yaw aim beyond the profile's
            // own head yaw limit (the solver's soft-clamp ceiling): composing with aim, never
            // fighting or replacing it.
            Assert.That(maxYawDuringNod, Is.LessThanOrEqualTo(_gazeProfile.MaxHeadYawDegrees + 1f),
                "The scripted nod must compose with the aim, not blow past the head's own yaw limit.");
        }

        [UnityTest]
        public IEnumerator NoDoubleNod_ExternalProgramActive_BackchannelContributionSuppressed()
        {
            // Full truth-table coverage of the arbiter's suppression logic (external-wins,
            // shared refractory, aversion gate) lives in the deterministic, non-Unity
            // HeadGestureArbiterTests. This end-to-end test proves the wiring is actually live
            // on a real controller/rig: (1) the arbiter's suppression flag — the exact boolean
            // the controller ORs into BackchannelDirector's own suppressed input — is raised
            // while the external Body Language program plays, and (2) with that flag raised the
            // total pitch excursion stays within one program's amplitude (the observable
            // consequence: no second nod ever stacks on top).
            GazeHandle handle = LockGazeOffAxis();
            Assert.NotNull(handle);
            // Wall-clock (not frame-count) bounded — see RunForRealSeconds.
            yield return RunForRealSeconds(3f);

            Assert.IsTrue(InvokeRequestHeadGesture(_bodyLanguage, HeadGestureKind.Nod));

            float maxPitchExcursion = 0f;
            float baselinePitch = _gaze.CaptureSnapshot().HeadAngles.y;
            bool sawExternalActive = false;
            yield return RunForRealSeconds(4f, () =>
            {
                if (_gaze.HeadGestureExternalActive) sawExternalActive = true;
                float excursion = Mathf.Abs(_gaze.CaptureSnapshot().HeadAngles.y - baselinePitch);
                maxPitchExcursion = Mathf.Max(maxPitchExcursion, excursion);
            });

            Assert.IsTrue(sawExternalActive,
                "The arbiter must have raised its suppression flag while the external program played.");

            float singleProgramAmplitude = _bodyLanguageProfile.HeadGestureNodMaxPitchDegrees;
            Assert.That(maxPitchExcursion, Is.LessThanOrEqualTo(singleProgramAmplitude + 2f),
                "Total pitch excursion during the external nod must stay within one program's " +
                "amplitude — a stacked backchannel nod would push this well past it (no double-nod).");
        }

        [UnityTest]
        public IEnumerator GazeRegistered_BodyLanguageNeverWritesNeckOrHeadDirectly()
        {
            yield return null;

            Assert.That(_bodyLanguage.HeadGestureConsumerCount, Is.GreaterThan(0),
                "Sanity: Gaze must have registered as a consumer on enable.");

            Assert.IsTrue(InvokeRequestHeadGesture(_bodyLanguage, HeadGestureKind.Nod));

            // Body Language's OWN fallback writes go directly onto Neck/Head local rotation
            // through its own write guard; Gaze's solver ALSO writes those bones (that's the
            // whole point of composition), so bone deltas alone cannot distinguish "Gaze wrote
            // it" from "the fallback wrote it". The controller's own published diagnostic flag
            // (HeadGestureFallbackActive) is the unambiguous signal: it must never go true while
            // a consumer is registered.
            for (int i = 0; i < 60; i++)
            {
                yield return null;
                Assert.That(_bodyLanguage.HeadGestureConsumerCount, Is.GreaterThan(0),
                    "Gaze must stay registered for the whole run.");
                Assert.IsFalse(_bodyLanguage.CaptureSnapshot().HeadGestureFallbackActive,
                    "With Gaze registered as a consumer, Body Language's no-consumer fallback must never engage.");
            }
        }

        [UnityTest]
        public IEnumerator GazeDisables_BodyLanguageFallbackTakesOver()
        {
            yield return null;
            Assert.That(_bodyLanguage.HeadGestureConsumerCount, Is.GreaterThan(0));

            _gaze.enabled = false;
            yield return null;

            Assert.That(_bodyLanguage.HeadGestureConsumerCount, Is.EqualTo(0),
                "Disabling Gaze must release its channel registration so the fallback can engage.");

            Assert.IsTrue(InvokeRequestHeadGesture(_bodyLanguage, HeadGestureKind.Nod));

            // Ceiling raised from 60 to 600 — see NodDuringLockedGaze_AimErrorStaysBounded above.
            bool sawDeviation = false;
            for (int i = 0; i < 600 && !sawDeviation; i++)
            {
                yield return null;
                if (Quaternion.Angle(_head.localRotation, _restHead) > 0.5f) sawDeviation = true;
            }

            Assert.IsTrue(sawDeviation,
                "With Gaze disabled (zero consumers), Body Language must self-actuate the nod onto Head.");
        }

        [UnityTest]
        public IEnumerator AlwaysFocus_PlayerAnchorSwitchAndLateMotion_ReacquiresWithoutStaleAim()
        {
            Transform firstAnchor = NewChild(_root.transform, "FocusAnchorA", new Vector3(2f, 1.7f, 2f));
            Transform secondAnchor = NewChild(_root.transform, "FocusAnchorB", new Vector3(0f, 1.7f, 3f));
            _gaze.EyeContactMode = GazeEyeContactMode.AlwaysLock;
            _gaze.PlayerAnchorOverride = firstAnchor;

            yield return RunForRealSeconds(2f);
            float firstYaw = _gaze.CaptureSnapshot().HeadAngles.x;
            Assert.That(firstYaw, Is.GreaterThan(2f));

            // Simulates an XR/HMD late pose change. This component runs immediately before
            // Gaze's LateUpdate, after its cognition tick; removing live resampling leaves the
            // directive at the old positive-yaw point for this rendered frame.
            LatePoseMover mover = _root.AddComponent<LatePoseMover>();
            mover.Target = firstAnchor;
            mover.NextLocalPosition = new Vector3(-2f, 1.7f, 2f);

            // Arm the move only after the component has joined a LateUpdate pass. A MonoBehaviour
            // added mid-frame is not guaranteed to receive LateUpdate in the frame it was added,
            // and when it misses, the anchor moves one frame later than this measurement reads —
            // the snapshot below is taken in the next frame's Update, ahead of that LateUpdate, so
            // the aim looks unchanged and the test fails having never performed the late move.
            // That race is the whole difference between this passing and failing: the required
            // yaw is re-measured from the live anchor every LateUpdate, so one genuine frame flips
            // it from ~+45deg to ~-45deg. An unchanged sign means the move did not happen.
            yield return null;
            float preMoveRequiredYaw = _gaze.CaptureSnapshot().TargetErrorAngles.x;

            mover.MoveOnNextLateUpdate = true;
            yield return null;
            Assert.That(mover.MoveOnNextLateUpdate, Is.False,
                "The late-pose mover never ran its LateUpdate, so the late XR pose was never applied " +
                "and the assertion below would be measuring nothing.");

            // The witness is the directive, not the head. The head lane is a min-jerk shift that
            // starts from zero velocity, so in the frame the target jumps the rendered head moves by
            // a few thousandths of a degree at most (and may still drift the old way). What the
            // late-pose resample guarantees is that the *required* yaw measured in this LateUpdate
            // already points at the new anchor; the head follows over the shift's duration below.
            float movedRequiredYaw = _gaze.CaptureSnapshot().TargetErrorAngles.x;
            Assert.That(movedRequiredYaw, Is.LessThan(-2f),
                "The rendered LateUpdate frame must react to the late XR pose, not the old cognition sample.");
            Assert.That(preMoveRequiredYaw, Is.GreaterThan(2f),
                "Before the late move the directive must still point at the first anchor.");
            yield return RunForRealSeconds(2f);
            Assert.That(_gaze.CaptureSnapshot().HeadAngles.x, Is.LessThan(-2f));

            // Camera/provider handoff must be equally immediate and retain the focused target.
            _gaze.PlayerAnchorOverride = secondAnchor;
            yield return RunForRealSeconds(2f);
            var snapshot = _gaze.CaptureSnapshot();
            Assert.That(snapshot.HeadAngles.x, Is.EqualTo(0f).Within(3f));
            Assert.AreEqual(GazeTargetKind.Player, snapshot.TargetKind);
            Assert.AreEqual(secondAnchor.name, snapshot.TargetName);
        }

        [UnityTest]
        public IEnumerator GazeDisable_UnwindsHeadWriteAndReenableRebinds()
        {
            // Gaze must be the ONLY head writer for this measurement. Body Language is the rig's
            // other one, and its breath head stabilization is deliberately gated on the
            // head-gesture channel having no consumer (ConvaiBodyLanguageController.cs:1529) —
            // the same "applied by Gaze when present, by the compositor fallback when not" rule
            // GazeDisabled_BodyLanguageSelfActuatesNod above relies on. Disabling Gaze
            // unregisters that consumer synchronously, so on the very next frame Body Language
            // (execution order BodyPose 19300, ahead of Gaze 19400) starts self-actuating its
            // breath counter-pitch onto Neck/Head — a live ~0.27deg pose of its own, not a leaked
            // gaze write. Left enabled, it masks exactly the leak this test exists to catch.
            _bodyLanguage.enabled = false;

            LockGazeOffAxis();
            yield return RunForRealSeconds(2f);
            Assert.That(Quaternion.Angle(_restHead, _head.localRotation), Is.GreaterThan(0.2f));

            _gaze.enabled = false;
            yield return null;
            Assert.That(Quaternion.Angle(_restHead, _head.localRotation), Is.LessThan(0.01f),
                "Disable must unwind the guarded head write before releasing the rig.");

            _gaze.enabled = true;
            LockGazeOffAxis();
            yield return RunForRealSeconds(2f);
            Assert.That(Quaternion.Angle(_restHead, _head.localRotation), Is.GreaterThan(0.2f),
                "Re-enable must rebind the chain and resume gaze ownership.");
        }

        [UnityTest]
        public IEnumerator DiagnosticNames_StayCurrentAcrossRenameAndAnchorReplacement()
        {
            _gaze.EyeContactMode = GazeEyeContactMode.AlwaysLock;
            Transform first = NewChild(_root.transform, "FirstNamedAnchor", new Vector3(1f, 1.7f, 2f));
            _gaze.PlayerAnchorOverride = first;
            yield return RunForRealSeconds(0.5f);
            var historical = _gaze.CaptureSnapshot();
            Assert.That(historical.TargetName, Is.EqualTo("FirstNamedAnchor"));
            first.name = "RenamedAnchor";
            Assert.That(_gaze.CaptureSnapshot().TargetName, Is.EqualTo("RenamedAnchor"));
            Assert.That(_gaze.CaptureSnapshot().TargetName, Is.EqualTo("RenamedAnchor"));
            Assert.That(historical.TargetName, Is.EqualTo("FirstNamedAnchor"));

            string eventName = null;
            Action<Convai.Modules.Gaze.Core.Diagnostics.GazeTargetChange> listener = change => eventName = change.ToName;
            _gaze.TargetChanged += listener;
            yield return null;
            first.name = "ObservedRename";
            yield return RunForRealSeconds(0.1f);
            Assert.That(eventName, Is.Null, "Renaming a held target does not emit a target-change event.");

            Transform replacement = NewChild(_root.transform, "ReplacementAnchor", new Vector3(-1f, 1.7f, 2f));
            _gaze.PlayerAnchorOverride = replacement;
            Object.DestroyImmediate(first.gameObject);
            yield return RunForRealSeconds(0.5f);
            Assert.That(_gaze.CaptureSnapshot().TargetName, Is.EqualTo("ReplacementAnchor"));
            Assert.That(eventName, Is.EqualTo("ReplacementAnchor"));
            _gaze.TargetChanged -= listener;
            var provider = _root.GetComponent<Convai.Modules.Gaze.Providers.PlayerAnchorTargetProvider>();
            Assert.That(provider, Is.Not.Null);
            provider.enabled = false;
            replacement.name = "ReenabledAnchor";
            provider.enabled = true;
            Assert.That(provider.TryGetCandidate(null, out var reenabled, false), Is.True);
            Assert.That(reenabled.DebugName, Is.EqualTo("ReenabledAnchor"));
        }

        [UnityTest]
        public IEnumerator SteadyStateControllerFrames_DoNotAllocateBeyondHarnessNoise()
        {
            // This is intentionally a single real controller/rig guard: the multi-character
            // budget belongs to profiler/soak validation, while this catches a new per-frame
            // managed allocation in the ordinary LateUpdate path close to its source.
            _gaze.EyeContactMode = GazeEyeContactMode.AlwaysLock;
            Transform anchor = NewChild(_root.transform, "GcFocusAnchor", new Vector3(1f, 1.7f, 2f));
            _gaze.PlayerAnchorOverride = anchor;

            using var measurement = new AllocationByteMeasurement();
            for (int i = 0; i < 120; i++)
                yield return null;

            // Include cognition and both real LateUpdate writers, outside the coroutine stack.
            // Unity 6 hierarchy samples use assembly-qualified Invoke names.
            string[] scopes =
            {
                "Convai.Runtime.dll!Convai.Runtime.Animation::EmbodimentTickScheduler.Update() [Invoke]",
                "Convai.Modules.BodyLanguage.dll!Convai.Modules.BodyLanguage.Components::ConvaiBodyLanguageController.LateUpdate() [Invoke]",
                "Convai.Modules.Gaze.dll!Convai.Modules.Gaze.Components::ConvaiGazeController.LateUpdate() [Invoke]"
            };
            yield return measurement.MeasureFrames(120, scopes);
            long first = measurement.Bytes;
            yield return measurement.MeasureFrames(120, scopes);
            long second = measurement.Bytes;

            Assert.That(_gaze.CaptureSnapshot().TargetKind, Is.EqualTo(GazeTargetKind.Player),
                "The guard must exercise an active controller bound to the player anchor.");
            // Preserve the byte allowance per 120-frame batch: even 16 B/frame exceeds it.
            const long frameLoopNoiseBytes = 256L;
            Assert.That(Math.Max(first, second), Is.LessThanOrEqualTo(frameLoopNoiseBytes),
                $"Both 120-frame controller batches must stay within {frameLoopNoiseBytes} bytes " +
                $"(first={first}, second={second}).");
        }
    }
}
