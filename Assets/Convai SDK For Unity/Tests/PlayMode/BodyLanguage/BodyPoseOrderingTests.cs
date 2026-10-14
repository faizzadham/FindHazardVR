#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyLanguage
{
    /// <summary>
    ///     Ordering-proof test: on a real rig with a live Animator
    ///     playing a clip that animates the Spine's local rotation, proves at RUNTIME (not just
    ///     by constant uniqueness) that <c>ConvaiBodyLanguageController</c> writes its posture
    ///     delta AFTER the Animator has posed the skeleton for the frame and BEFORE a probe
    ///     registered at the Gaze execution order reads the bone — i.e. the reserved
    ///     <see cref="EmbodimentExecutionOrders.BodyPose" /> slot genuinely sits between
    ///     <see cref="EmbodimentExecutionOrders.AnimatorConductor" /> and
    ///     <see cref="EmbodimentExecutionOrders.Gaze" /> in the actual Unity callback order,
    ///     not merely in the declared constants.
    /// </summary>
    /// <remarks>
    ///     Editor-only (<c>UnityEditor.Animations.AnimatorController</c> is not available in
    ///     player builds): the in-memory controller and clip are created for the duration of the
    ///     test and never written to disk, so no asset cleanup is required.
    /// </remarks>
    public sealed class BodyPoseOrderingTests
    {
        private const float ClipSpineDegrees = 20f;

        private GameObject _root;
        private Transform _spine;
        private Transform _chest;
        private Transform _upperChest;
        private Animator _animator;
        private ConvaiBodyLanguageController _controller;
        private OrderProbe _probe;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("BodyPoseOrderingRoot");

            // Before any module component: ConvaiBodyLanguageController resolves its context in
            // OnEnable, and AddComponent enables on the spot. Without a context on the object
            // first, the controller correctly reports that it is not on a Convai character, goes
            // inert, and takes the ordering proof with it.
            _root.AddComponent<EmbodimentContext>();

            _spine = NewChild(_root.transform, "Spine", new Vector3(0f, 1f, 0f));
            _chest = NewChild(_spine, "Chest", new Vector3(0f, 0.15f, 0f));
            _upperChest = NewChild(_chest, "UpperChest", new Vector3(0f, 0.15f, 0f));

            _animator = _root.AddComponent<Animator>();
            _animator.runtimeAnimatorController = BuildSpineAnimatingController();
            _animator.applyRootMotion = false;
            _animator.Play("Spine", 0, 0f);

            var profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(profile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(profile, "postureFadeSeconds", 0.01f);
            SetPrivateField(profile, "policyTransitionSeconds", 0f);
            var idlePolicies = new List<BodyLanguageStatePolicy>
            {
                new()
                {
                    State = DialogueState.Idle,
                    PostureOpennessBias = 1f,
                    SagittalLeanBias = 0f,
                    BreathRateCpm = 13f,
                    BreathDepth = 0f
                }
            };
            SetPrivateField(profile, "statePolicies", idlePolicies);
            SetPrivateField(profile, "maxOpennessDegrees", 8f);
            // Stiffest legal spring: this test proves EXECUTION ORDER, not spring feel, so the
            // posture delta must clear the probe's detection threshold within a few frames
            // (the shipped default of 4/s takes seconds to converge and made the test flaky).
            SetPrivateField(profile, "postureSpringSharpness", 20f);
            SetPrivateField(profile, "postureMaxAngularSpeed", 720f);

            _controller = _root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(_controller, "profile", profile);
            // Re-apply now that the profile field is set (OnEnable already ran with the
            // runtime-default profile before AddComponent's caller could set the field).
            _controller.enabled = false;
            _controller.enabled = true;

            _probe = _root.AddComponent<OrderProbe>();
            _probe.SpineBone = _spine;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
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

        /// <summary>
        ///     Builds a minimal in-memory AnimatorController: one layer, one state, playing a
        ///     clip that rotates "Spine" (relative to the animator root) by a constant angle on
        ///     its local X axis. Never written to disk.
        /// </summary>
        private RuntimeAnimatorController BuildSpineAnimatingController()
        {
            var clip = new AnimationClip { name = "SpineTestClip" };
            var binding = EditorCurveBinding.FloatCurve("Spine", typeof(Transform), "localEulerAnglesRaw.x");
            AnimationUtility.SetEditorCurve(clip, binding, ConstantCurve(ClipSpineDegrees));

            var controller = new AnimatorController { name = "BodyPoseOrderingController" };
            controller.AddLayer("Base");
            AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
            AnimatorState state = stateMachine.AddState("Spine");
            state.motion = clip;
            stateMachine.defaultState = state;

            return controller;
        }

        private static AnimationCurve ConstantCurve(float value) =>
            new(new Keyframe(0f, value), new Keyframe(10f, value));

        [UnityTest]
        public IEnumerator PostureDelta_AppliesAfterAnimatorPose_BeforeGazeOrderProbe()
        {
            // Wait until the probe observes the composed pose rather than a fixed frame count:
            // spring convergence speed is a profile tunable, not what this test proves. The
            // ordering proof is unaffected — LastRecordedSpineRotation is only ever written by
            // the probe (order 19400) inside ITS LateUpdate, so if BodyLanguage wrote before
            // the Animator (overwritten) or after the probe (unseen), the recorded rotation
            // would stay at the raw clip pose forever and the cap would expire.
            var clipOnlyRotation = Quaternion.Euler(ClipSpineDegrees, 0f, 0f);
            float angleFromClipOnly = 0f;
            // Ceiling raised from 120 to 1200: the compositor's per-channel
            // motor filter now rate-limits the spine sagittal channel (45°/s tonic cap) on top
            // of the posture spring itself, and this headless runner's per-frame Time.deltaTime
            // can run smaller than a typical editor frame — both only affect HOW LONG
            // convergence takes, never whether it converges, so a larger ceiling is the correct
            // fix (see the wait-until-converged rationale above).
            for (int i = 0; i < 1200 && angleFromClipOnly <= 0.5f; i++)
            {
                yield return null;
                angleFromClipOnly = Quaternion.Angle(_probe.LastRecordedSpineRotation, clipOnlyRotation);
            }

            Assert.IsFalse(_controller.IsInert, "The controller must not be inert on a Spine-only rig.");
            Assert.That(_probe.RecordedCallOrder, Is.Not.Empty, "The probe must have recorded at least one LateUpdate.");

            // The clip alone would hold Spine at a constant 20° local X rotation. If BodyPose
            // actually composed AFTER the animator and the probe read AFTER BodyPose, the
            // probe's recorded rotation must differ from the raw clip pose (the posture delta
            // is visibly layered on top).
            Assert.That(angleFromClipOnly, Is.GreaterThan(0.5f),
                "The probe (running at the Gaze execution order) must observe the animator pose " +
                "composed with the BodyLanguage posture delta — not the raw, untouched clip pose.");
        }

        [UnityTest]
        public IEnumerator ScriptOrderChain_RunsCognitionThenBodyPoseThenGazeOrderProbe()
        {
            // Same wait-until-converged pattern as above: the flag is only ever set inside the
            // probe's own LateUpdate (order 19400) when it reads a Spine pose the BodyPose
            // write (19300) already modified THAT frame — waiting more frames relaxes only the
            // spring-convergence timing, never the same-frame ordering being proven.
            // Ceiling raised from 120 to 1200 for the same reason as
            // PostureDelta_AppliesAfterAnimatorPose_BeforeGazeOrderProbe above.
            for (int i = 0; i < 1200 && !_probe.ObservedBodyPoseWriteBeforeProbe; i++)
                yield return null;

            Assert.That(_probe.ObservedBodyPoseWriteBeforeProbe, Is.True,
                "The BodyLanguage controller's LateUpdate (order 19300) must have already run " +
                "and modified the Spine bone by the time the Gaze-order probe (19400) reads it " +
                "on the same frame.");
        }

        /// <summary>Records the Spine bone's rotation at the Gaze execution order, every LateUpdate.</summary>
        [DefaultExecutionOrder(EmbodimentExecutionOrders.Gaze)]
        private sealed class OrderProbe : MonoBehaviour
        {
            public Transform SpineBone;
            public readonly List<Quaternion> RecordedCallOrder = new();
            public Quaternion LastRecordedSpineRotation { get; private set; } = Quaternion.identity;
            public bool ObservedBodyPoseWriteBeforeProbe { get; private set; }

            private void LateUpdate()
            {
                if (SpineBone == null) return;

                LastRecordedSpineRotation = SpineBone.localRotation;
                RecordedCallOrder.Add(LastRecordedSpineRotation);

                var clipOnlyRotation = Quaternion.Euler(20f, 0f, 0f);
                if (Quaternion.Angle(LastRecordedSpineRotation, clipOnlyRotation) > 0.5f)
                    ObservedBodyPoseWriteBeforeProbe = true;
            }
        }
    }
}
#endif
