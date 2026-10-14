using System.Collections;
using System.Collections.Generic;
using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Modules.BodyAnimation;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Runtime.Embodiment;
using Convai.Runtime.Animation;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyAnimation
{
    /// <summary>
    ///     End-to-end PlayMode smoke test for <see cref="ConvaiBodyAnimationController" />:
    ///     build → tick → teardown → rebuild on a real, code-built Humanoid rig (no imported
    ///     avatar asset). Also covers the "no Humanoid avatar" inert path, which must degrade
    ///     gracefully without throwing.
    /// </summary>
    /// <remarks>
    ///     <see cref="ConvaiBodyAnimationController.BuildRuntime" /> hard-requires
    ///     <c>_animator.avatar.isValid &amp;&amp; _animator.avatar.isHuman</c>. This suite builds
    ///     a minimal 17-bone Humanoid skeleton procedurally via
    ///     <see cref="AvatarBuilder.BuildHumanAvatar" /> (T-pose bone positions, default human
    ///     limits) so the real controller can be exercised without an imported avatar asset. If
    ///     <see cref="Avatar.isValid" /> comes back false for the current Unity version, the
    ///     avatar-dependent test reports the diagnostic via <see cref="Assert.Ignore(string)" />
    ///     rather than failing.
    /// </remarks>
    public sealed class BodyAnimationRuntimeSmokeTests
    {
        // Essential Humanoid bones (Unity's 15 required + Chest/Neck), using the exact
        // HumanTrait.BoneName strings for each humanName.
        private static readonly string[] HumanNames =
        {
            "Hips", "Spine", "Chest", "Neck", "Head",
            "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
            "RightUpperLeg", "RightLowerLeg", "RightFoot"
        };

        private readonly List<Object> _cleanup = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _cleanup.Count; i++)
            {
                if (_cleanup[i] != null)
                    Object.DestroyImmediate(_cleanup[i]);
            }
            _cleanup.Clear();
        }

        [UnityTest]
        public IEnumerator Controller_BuildsTicksAndTearsDown()
        {
            if (!TryBuildHumanoidRig("SmokeCharacter", out GameObject root, out Avatar avatar, out string diagnostic))
            {
                if (root != null) Object.DestroyImmediate(root);
                if (avatar != null) Object.DestroyImmediate(avatar);
                Assert.Ignore(
                    "Procedural Humanoid avatar could not be validated on this Unity version " +
                    $"— {diagnostic}. Skipping the real-rig build/tick/teardown smoke test.");
                yield break;
            }

            _cleanup.Add(avatar);
            _cleanup.Add(root);

            // Build the whole rig while inactive so no component's OnEnable fires until every
            // dependency (animator avatar, animation set) is wired up.
            root.SetActive(false);

            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = true;

            var context = root.AddComponent<EmbodimentContext>();
            var controller = root.AddComponent<ConvaiBodyAnimationController>();

            ConvaiBodyAnimationSet set = BuildAnimationSet(_cleanup);
            SetPrivateField(controller, "_animationSet", set);

            int readyCount = 0;
            bool builtWhenReady = false;
            BodyAnimationActionHandle actionFromReady = null;
            controller.RuntimeReady += () =>
            {
                readyCount++;
                builtWhenReady = controller.IsRuntimeBuilt;
                actionFromReady = controller.PlayAction("missing-smoke-action");
            };

            root.SetActive(true);

            for (int i = 0; i < 5; i++) yield return null;

            Assert.That(readyCount, Is.EqualTo(1), "The actual build must announce readiness once.");
            Assert.That(builtWhenReady, Is.True, "RuntimeReady must follow runtime initialization.");
            Assert.That(actionFromReady, Is.Not.Null);
            Assert.That(actionFromReady.FailureReason, Is.EqualTo("unknown action"),
                "PlayAction from RuntimeReady must reach action lookup, not the deferred/not-built path.");

            Assert.IsTrue(controller.IsRuntimeBuilt,
                "A Humanoid Animator + valid animation set must build the runtime.");
            Assert.NotNull(context.CoSpeechPerformanceSource,
                "A built Body Animation runtime must publish its optional co-speech source.");
            Assert.That(animator.applyRootMotion, Is.False,
                "Body Animation owns root motion while its graph is active.");

            BodyAnimationSnapshot snapshot = controller.CaptureSnapshot();
            Assert.AreEqual(6, snapshot.Layers.Count,
                "Expected all mixer ports: Locomotion, Talk, Action, Pointing, Moving Talk and Talk Beat.");
            Assert.AreEqual("Idle", snapshot.Layers[0].State,
                "The Locomotion layer must settle on the Idle state once built.");

            // Disable → runtime tears down.
            controller.enabled = false;
            yield return null;
            Assert.IsFalse(controller.IsRuntimeBuilt,
                "Disabling the controller must tear down the runtime.");
            Assert.IsNull(context.CoSpeechPerformanceSource,
                "Disabling must unregister the co-speech source without leaving a stale owner.");
            Assert.That(animator.applyRootMotion, Is.True,
                "Disabling must restore the Animator's original Apply Root Motion value.");

            // Re-enable → runtime rebuilds cleanly (also folds in the teardown-then-rebuild
            // no-throw coverage that a dedicated PlayableGraph leak test would otherwise need).
            Assert.DoesNotThrow(() => controller.enabled = true);
            for (int i = 0; i < 5; i++) yield return null;

            Assert.IsTrue(controller.IsRuntimeBuilt, "Re-enabling must rebuild the runtime.");
            Assert.That(readyCount, Is.EqualTo(2), "A rebuild must announce readiness once again.");
            Assert.That(builtWhenReady, Is.True);
            Assert.That(actionFromReady.FailureReason, Is.EqualTo("unknown action"));
            Assert.NotNull(context.CoSpeechPerformanceSource);
            snapshot = controller.CaptureSnapshot();
            Assert.AreEqual(6, snapshot.Layers.Count);
            Assert.AreEqual("Idle", snapshot.Layers[0].State);
            int baselinePlayableCount = snapshot.GraphPlayableCount;

            ConvaiBodyAnimationSet replacement = BuildAnimationSet(_cleanup);
            SetPrivateField(replacement, "_displayName", "Smoke Replacement");
            controller.SetAnimationSet(replacement);
            float handoffDeadline = Time.realtimeSinceStartup + 2f;
            do
            {
                yield return null;
                snapshot = controller.CaptureSnapshot();
            } while (snapshot.GraphPlayableCount != baselinePlayableCount &&
                     Time.realtimeSinceStartup < handoffDeadline);
            Assert.AreEqual("Smoke Replacement", snapshot.SetName,
                "A safe Set swap must complete through the in-graph root handoff.");
            Assert.AreEqual(6, snapshot.Layers.Count);
            Assert.AreEqual(baselinePlayableCount, snapshot.GraphPlayableCount,
                "A completed Set handoff must retire the old subgraph without playable growth.");

            controller.enabled = false;
            yield return null;

            // Destroying the whole rig (and therefore the controller) must not throw during
            // OnDestroy teardown.
            Assert.DoesNotThrow(() => Object.DestroyImmediate(root));
            _cleanup.Remove(root);
        }

        [UnityTest]
        public IEnumerator TwentyFiveCharacter_SteadyStateManualTick_AllocatesZeroBytes()
        {
            if (!TryBuildHumanoidRig("BodyAnimationSoakTemplate", out GameObject template, out Avatar avatar,
                    out string diagnostic))
            {
                if (template != null) Object.DestroyImmediate(template);
                if (avatar != null) Object.DestroyImmediate(avatar);
                Assert.Ignore($"Procedural Humanoid avatar unavailable: {diagnostic}");
                yield break;
            }

            _cleanup.Add(avatar);
            template.SetActive(false);
            var roots = new GameObject[25];
            var controllers = new ConvaiBodyAnimationController[25];
            ConvaiBodyAnimationSet set = BuildAnimationSet(_cleanup);
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = i == 0 ? template : Object.Instantiate(template);
                root.name = $"BodyAnimationSoak_{i:00}";
                _cleanup.Add(root);
                roots[i] = root;
            }

            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                Animator animator = root.AddComponent<Animator>();
                animator.avatar = avatar;
                root.AddComponent<EmbodimentContext>();
                var controller = root.AddComponent<ConvaiBodyAnimationController>();
                SetPrivateField(controller, "_animationSet", set);
                controllers[i] = controller;
                root.SetActive(true);
            }

            for (int i = 0; i < 10; i++) yield return null;
            for (int i = 0; i < controllers.Length; i++)
                Assert.That(controllers[i].IsRuntimeBuilt, Is.True, $"Controller {i} did not build.");

            for (int frame = 0; frame < 30; frame++)
                for (int i = 0; i < controllers.Length; i++)
                    ((IEmbodimentTickable)controllers[i]).EmbodimentTick(1f / 60f);

            GC.Collect();
            long allocated = AllocationCounter.Measure(() =>
            {
                for (int frame = 0; frame < 300; frame++)
                    for (int i = 0; i < controllers.Length; i++)
                        ((IEmbodimentTickable)controllers[i]).EmbodimentTick(1f / 60f);
            });

            Assert.That(allocated, Is.Zero,
                "The complete 25-character Body Animation steady-state tick must produce zero managed allocation events.");
        }

        [UnityTest]
        public IEnumerator Controller_NoAvatar_StaysInertWithoutThrowing()
        {
            var root = new GameObject("NoAvatarCharacter");
            _cleanup.Add(root);

            root.SetActive(false);

            root.AddComponent<Animator>(); // no avatar assigned
            root.AddComponent<EmbodimentContext>();
            var controller = root.AddComponent<ConvaiBodyAnimationController>();

            ConvaiBodyAnimationSet set = BuildAnimationSet(_cleanup);
            SetPrivateField(controller, "_animationSet", set);

            LogAssert.Expect(LogType.Warning, new Regex("needs a valid Humanoid avatar"));
            Assert.DoesNotThrow(() => root.SetActive(true));

            for (int i = 0; i < 3; i++) yield return null;

            Assert.IsFalse(controller.IsRuntimeBuilt,
                "Without a valid Humanoid avatar the controller must stay inert, never throw.");

            Assert.DoesNotThrow(() => controller.enabled = false);
            yield return null;

            Assert.DoesNotThrow(() => Object.DestroyImmediate(root));
            _cleanup.Remove(root);
        }

        // ------------------------------------------------------------------ helpers

        private static ConvaiBodyAnimationSet BuildAnimationSet(List<Object> cleanup)
        {
            var set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            set.name = "SmokeAnimationSet";
            cleanup.Add(set);

            var idleClip = new AnimationClip { name = "SmokeIdle", wrapMode = WrapMode.Loop };
            idleClip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 0f));
            cleanup.Add(idleClip);

            var talkClip = new AnimationClip { name = "SmokeTalk", wrapMode = WrapMode.Loop };
            talkClip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 0.8f, 0f));
            cleanup.Add(talkClip);

            var idleEntry = new IdleEntry();
            idleEntry.Initialize(idleClip);

            var talkEntry = new TalkEntry();
            talkEntry.Initialize(talkClip);

            var upperBodyMask = new AvatarMask();
            upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Root, false);
            upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftLeg, false);
            upperBodyMask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightLeg, false);
            cleanup.Add(upperBodyMask);

            set.InitializeContent(
                "Smoke Set",
                new List<IdleEntry> { idleEntry },
                new List<TalkEntry> { talkEntry },
                null,
                upperBodyMask);

            return set;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName} on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        /// <summary>
        ///     Builds a minimal T-pose Humanoid skeleton (17 bones) under a fresh root
        ///     <see cref="GameObject" /> and attempts to compile it into a valid
        ///     <see cref="Avatar" /> via <see cref="AvatarBuilder.BuildHumanAvatar" />, relying
        ///     on <see cref="HumanLimit.useDefaultValues" /> to infer muscle limits from the
        ///     authored bone positions.
        /// </summary>
        private static bool TryBuildHumanoidRig(
            string rootName, out GameObject root, out Avatar avatar, out string diagnostic)
        {
            root = new GameObject(rootName);

            Transform hips = CreateBone(root.transform, "Hips", new Vector3(0f, 1.00f, 0f));
            Transform spine = CreateBone(hips, "Spine", new Vector3(0f, 0.12f, 0f));
            Transform chest = CreateBone(spine, "Chest", new Vector3(0f, 0.12f, 0f));
            Transform neck = CreateBone(chest, "Neck", new Vector3(0f, 0.15f, 0f));
            Transform head = CreateBone(neck, "Head", new Vector3(0f, 0.10f, 0f));

            Transform leftUpperArm = CreateBone(chest, "LeftUpperArm", new Vector3(0.15f, 0.05f, 0f));
            Transform leftLowerArm = CreateBone(leftUpperArm, "LeftLowerArm", new Vector3(0.28f, 0f, 0f));
            Transform leftHand = CreateBone(leftLowerArm, "LeftHand", new Vector3(0.25f, 0f, 0f));

            Transform rightUpperArm = CreateBone(chest, "RightUpperArm", new Vector3(-0.15f, 0.05f, 0f));
            Transform rightLowerArm = CreateBone(rightUpperArm, "RightLowerArm", new Vector3(-0.28f, 0f, 0f));
            Transform rightHand = CreateBone(rightLowerArm, "RightHand", new Vector3(-0.25f, 0f, 0f));

            Transform leftUpperLeg = CreateBone(hips, "LeftUpperLeg", new Vector3(0.09f, -0.05f, 0f));
            Transform leftLowerLeg = CreateBone(leftUpperLeg, "LeftLowerLeg", new Vector3(0f, -0.45f, 0f));
            Transform leftFoot = CreateBone(leftLowerLeg, "LeftFoot", new Vector3(0f, -0.42f, 0.05f));

            Transform rightUpperLeg = CreateBone(hips, "RightUpperLeg", new Vector3(-0.09f, -0.05f, 0f));
            Transform rightLowerLeg = CreateBone(rightUpperLeg, "RightLowerLeg", new Vector3(0f, -0.45f, 0f));
            Transform rightFoot = CreateBone(rightLowerLeg, "RightFoot", new Vector3(0f, -0.42f, 0.05f));

            Transform[] bones =
            {
                hips, spine, chest, neck, head,
                leftUpperArm, leftLowerArm, leftHand,
                rightUpperArm, rightLowerArm, rightHand,
                leftUpperLeg, leftLowerLeg, leftFoot,
                rightUpperLeg, rightLowerLeg, rightFoot
            };

            var human = new HumanBone[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                human[i] = new HumanBone
                {
                    humanName = HumanNames[i],
                    boneName = bones[i].name,
                    limit = new HumanLimit { useDefaultValues = true }
                };
            }

            var skeleton = new SkeletonBone[bones.Length + 1];
            skeleton[0] = new SkeletonBone
            {
                name = root.name,
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one
            };
            for (int i = 0; i < bones.Length; i++)
            {
                skeleton[i + 1] = new SkeletonBone
                {
                    name = bones[i].name,
                    position = bones[i].localPosition,
                    rotation = bones[i].localRotation,
                    scale = Vector3.one
                };
            }

            var description = new HumanDescription
            {
                human = human,
                skeleton = skeleton,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false
            };

            avatar = AvatarBuilder.BuildHumanAvatar(root, description);

            if (avatar == null)
            {
                diagnostic = "AvatarBuilder.BuildHumanAvatar returned null.";
                return false;
            }

            if (!avatar.isValid)
            {
                diagnostic = "Built Avatar has isValid == false.";
                return false;
            }

            if (!avatar.isHuman)
            {
                diagnostic = "Built Avatar has isHuman == false.";
                return false;
            }

            diagnostic = string.Empty;
            return true;
        }

        private static Transform CreateBone(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
        }
    }
}
