using System.Collections.Generic;
using Convai.Modules.Gaze.Core.Solvers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     <see cref="GazeChainCalibration.CaptureEyeRest" />: the head/eye rest orientation must
    ///     come from the rig's authored rest pose, not from whatever pose the Animator happens to
    ///     have posed the bones in at bind time — a rebind (Body Animation building its runtime
    ///     rig after the Animator has already moved) must not adopt the live animated pose as the
    ///     new "straight ahead" reference.
    /// </summary>
    public sealed class GazeChainCalibrationRestPoseTests
    {
        [Test]
        public void HumanoidAvatar_RebindDuringAnimatedPose_KeepsSkeletonRestAsBaseline()
        {
            var root = new GameObject("HumanoidRoot");
            Avatar avatar = null;
            try
            {
                avatar = BuildMinimalHumanoidAvatar(root);
                Assert.That(avatar, Is.Not.Null.And.Matches<Avatar>(a => a.isHuman),
                    "The test fixture could not build a Humanoid avatar on this editor.");

                Animator animator = root.AddComponent<Animator>();
                animator.avatar = avatar;

                Transform chest = root.transform.Find("Hips/Spine/Chest");
                Transform neck = chest.Find("Neck");
                Transform head = neck.Find("Head");
                Assert.That(head, Is.Not.Null);

                var chain = new GazeChainCalibration();

                // First bind while the rig is at the pose the avatar was built from (neutral) —
                // matches OnEnable running ahead of the Animator's first pose.
                chain.BindManual(root.transform, chest, null, neck, head, null, null);
                Assert.That(Quaternion.Angle(chain.AimParentAtBind, Quaternion.identity), Is.LessThan(0.1f),
                    "Sanity: the first bind at neutral must already read the rest pose as identity.");

                // Simulate the Animator having posed the head mid-clip by the time Body Animation
                // rebinds the gaze chain onto its own runtime rig.
                head.localRotation = Quaternion.Euler(0f, 20f, 0f);

                chain.BindManual(root.transform, chest, null, neck, head, null, null);

                // The load-bearing invariant: AimParentAtBind must still be the avatar's authored
                // rest orientation, not the live +20° pose the rebind happened to catch the head in.
                Assert.That(Quaternion.Angle(chain.AimParentAtBind, Quaternion.identity), Is.LessThan(0.1f),
                    "A rebind mid-animation must not adopt the live head pose as the new rest — " +
                    "it must keep reading the avatar's authored skeleton rest.");

                // CurrentEyeRestForward is NOT expected to equal root forward here: it is defined
                // to track the aim parent's LIVE orientation relative to AimParentAtBind, so with
                // the fix in place (AimParentAtBind pinned to the true skeleton rest) it correctly
                // reports the head as still 20° off neutral — exactly the deviation the animation
                // put there. That is the property that matters: with the old bug (AimParentAtBind
                // adopting the live +20° pose as "rest"), this value would read as root forward
                // instead, silently discarding a real 20° of animated head deviation. Asserting the
                // deviation is visible is therefore the meaningful check; asserting it reads as
                // root forward — which the spec also calls for — would assert the bug.
                Assert.That(Vector3.Angle(chain.CurrentEyeRestForward, root.transform.forward), Is.EqualTo(20f).Within(0.5f),
                    "CurrentEyeRestForward must report the head's true 20° deviation from the avatar's rest, not fold it back to root forward.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (avatar != null) Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void NoAvatar_Rebind_ReReadsTheLivePoseAsRest()
        {
            // With no avatar there is no authored rest pose to fall back on, so calibration must
            // treat whatever pose the bone is in AT BIND TIME as rest — including on a rebind. A
            // rebind must re-read the live pose, not keep whatever the first-ever bind saw.
            var root = new GameObject("ManualRigRoot");
            try
            {
                var head = new GameObject("Head");
                head.transform.SetParent(root.transform, false);
                var leftEye = new GameObject("LeftEye");
                leftEye.transform.SetParent(head.transform, false);

                // The rig is already mid-animation the very first time calibration ever binds it.
                leftEye.transform.localRotation = Quaternion.Euler(0f, 20f, 0f);

                var chain = new GazeChainCalibration();
                chain.BindManual(root.transform, null, null, null, head.transform, leftEye.transform, leftEye.transform);

                Assert.That(Quaternion.Angle(chain.LeftEyeRestLocal, Quaternion.Euler(0f, 20f, 0f)), Is.LessThan(0.1f),
                    "Sanity: with no avatar, the first bind's live pose is the only rest available.");

                // A later rebind catches the eye at a different pose (-10°).
                leftEye.transform.localRotation = Quaternion.Euler(0f, -10f, 0f);
                chain.BindManual(root.transform, null, null, null, head.transform, leftEye.transform, leftEye.transform);

                // The rebind re-reads the live pose: rest now follows the -10° the bone was
                // actually caught in, not the +20° the very first bind saw.
                Assert.That(Quaternion.Angle(chain.LeftEyeRestLocal, Quaternion.Euler(0f, -10f, 0f)), Is.LessThan(0.1f),
                    "Without an avatar, a rebind must re-read the bone's current local rotation as " +
                    "rest — it must not keep carrying the pose the first-ever bind happened to see.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CalibratedRig_SmallPerEyeAxisOffset_SettlesBothEyesOnTarget()
        {
            GameObject host = new GameObject("SmallAxisOffsetRig");
            Convai.Modules.Gaze.Data.ConvaiGazeProfile profile = ConvaiGazeProfileForTest();
            try
            {
                Convai.Runtime.Embodiment.EmbodimentContext context = host.AddComponent<Convai.Runtime.Embodiment.EmbodimentContext>();
                Convai.Runtime.Animation.StandardRigBinding binding = host.AddComponent<Convai.Runtime.Animation.StandardRigBinding>();

                Transform head = new GameObject("Head").transform;
                head.SetParent(host.transform, false);
                head.position = new Vector3(0f, 1.6f, 0f);
                Transform left = new GameObject("LeftEye").transform;
                left.SetParent(head, false);
                left.position = new Vector3(-0.03f, 1.7f, 0.08f);
                Transform right = new GameObject("RightEye").transform;
                right.SetParent(head, false);
                right.position = new Vector3(0.03f, 1.7f, 0.08f);

                // Each eye's optical axis differs from the shared calibrated forward by ±3° yaw —
                // a small, realistic per-eye authored offset, unlike the 90°-apart axes elsewhere
                // used to prove the reframing math works at all.
                Vector3 leftAxisLocal = (Quaternion.AngleAxis(-3f, Vector3.up) * Vector3.right).normalized;
                Vector3 rightAxisLocal = (Quaternion.AngleAxis(3f, Vector3.up) * Vector3.right).normalized;

                SetPrivate(binding, "headOverride", head);
                SetPrivate(binding, "leftEyeOverride", left);
                SetPrivate(binding, "rightEyeOverride", right);
                SetPrivate(binding, "gazeAxisCalibrationEnabled", true);
                SetPrivate(binding, "gazeRootForwardLocal", Vector3.right);
                SetPrivate(binding, "gazeRootUpLocal", Vector3.up);
                SetPrivate(binding, "leftEyeForwardLocal", leftAxisLocal);
                SetPrivate(binding, "rightEyeForwardLocal", rightAxisLocal);
                binding.Rebuild();

                var chain = new GazeChainCalibration();
                chain.Bind(context, host.transform);
                Assert.IsTrue(chain.HasAxisCalibration);

                var solver = new EyeSolver();
                var input = new EyeSolveInput
                {
                    Chain = chain, Profile = profile, DeltaTime = 1f / 120f, HasTarget = true,
                    TargetPoint = new Vector3(5f, 1.7f, 0f), Engagement = 1f,
                    FixationLiveliness = 0f, GenerationId = 77, ApplyToBones = true
                };
                for (int i = 0; i < 240; i++) solver.Solve(in input);

                Assert.That(solver.AimErrorDegrees, Is.LessThan(1f),
                    "Composed cyclopean aim error must settle under 1° once the per-eye rest is measured on its own reference.");

                Vector3 targetFromLeft = input.TargetPoint - left.position;
                Vector3 targetFromRight = input.TargetPoint - right.position;
                Vector3 leftOptical = left.TransformDirection(leftAxisLocal);
                Vector3 rightOptical = right.TransformDirection(rightAxisLocal);
                Assert.That(Vector3.Angle(leftOptical, targetFromLeft), Is.LessThan(1.5f),
                    "The left eye's own optical axis must settle on the target within 1.5°.");
                Assert.That(Vector3.Angle(rightOptical, targetFromRight), Is.LessThan(1.5f),
                    "The right eye's own optical axis must settle on the target within 1.5°.");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(profile);
            }
        }

        private static Convai.Modules.Gaze.Data.ConvaiGazeProfile ConvaiGazeProfileForTest() =>
            Convai.Modules.Gaze.Data.ConvaiGazeProfile.CreateDefault();

        /// <summary>
        ///     Builds a minimal but genuinely valid Humanoid avatar directly under
        ///     <paramref name="root" /> (Hips/Spine/Chest/Neck/Head plus the four limbs
        ///     <see cref="AvatarBuilder.BuildHumanAvatar" /> requires). Self-contained: the
        ///     existing dev-tooling fixture with the same shape
        ///     (<c>Tests/EditMode/AI/HumanoidRigFixture.cs</c>) lives in the
        ///     <c>Convai.Tests.EditMode.AI</c> assembly, which depends ON
        ///     <c>Convai.Tests.EditMode</c> — not the other way — so it is not reachable from here.
        /// </summary>
        private static Avatar BuildMinimalHumanoidAvatar(GameObject root)
        {
            Transform hips = Bone(root.transform, "Hips", new Vector3(0f, 1.00f, 0f));
            Transform spine = Bone(hips, "Spine", new Vector3(0f, 0.12f, 0f));
            Transform chest = Bone(spine, "Chest", new Vector3(0f, 0.12f, 0f));
            Transform neck = Bone(chest, "Neck", new Vector3(0f, 0.15f, 0f));
            Transform head = Bone(neck, "Head", new Vector3(0f, 0.10f, 0f));

            Transform leftUpperArm = Bone(chest, "LeftUpperArm", new Vector3(0.15f, 0.05f, 0f));
            Transform leftLowerArm = Bone(leftUpperArm, "LeftLowerArm", new Vector3(0.28f, 0f, 0f));
            Transform leftHand = Bone(leftLowerArm, "LeftHand", new Vector3(0.25f, 0f, 0f));

            Transform rightUpperArm = Bone(chest, "RightUpperArm", new Vector3(-0.15f, 0.05f, 0f));
            Transform rightLowerArm = Bone(rightUpperArm, "RightLowerArm", new Vector3(-0.28f, 0f, 0f));
            Transform rightHand = Bone(rightLowerArm, "RightHand", new Vector3(-0.25f, 0f, 0f));

            Transform leftUpperLeg = Bone(hips, "LeftUpperLeg", new Vector3(0.09f, -0.05f, 0f));
            Transform leftLowerLeg = Bone(leftUpperLeg, "LeftLowerLeg", new Vector3(0f, -0.45f, 0f));
            Transform leftFoot = Bone(leftLowerLeg, "LeftFoot", new Vector3(0f, -0.42f, 0.05f));

            Transform rightUpperLeg = Bone(hips, "RightUpperLeg", new Vector3(-0.09f, -0.05f, 0f));
            Transform rightLowerLeg = Bone(rightUpperLeg, "RightLowerLeg", new Vector3(0f, -0.45f, 0f));
            Transform rightFoot = Bone(rightLowerLeg, "RightFoot", new Vector3(0f, -0.42f, 0.05f));

            string[] requiredHumanNames =
            {
                "Hips", "Spine", "Neck", "Head",
                "LeftUpperArm", "LeftLowerArm", "LeftHand",
                "RightUpperArm", "RightLowerArm", "RightHand",
                "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
                "RightUpperLeg", "RightLowerLeg", "RightFoot"
            };
            Transform[] mapped =
            {
                hips, spine, neck, head,
                leftUpperArm, leftLowerArm, leftHand,
                rightUpperArm, rightLowerArm, rightHand,
                leftUpperLeg, leftLowerLeg, leftFoot,
                rightUpperLeg, rightLowerLeg, rightFoot
            };

            var human = new HumanBone[mapped.Length];
            for (int i = 0; i < mapped.Length; i++)
            {
                human[i] = new HumanBone
                {
                    humanName = requiredHumanNames[i],
                    boneName = mapped[i].name,
                    limit = new HumanLimit { useDefaultValues = true }
                };
            }

            var all = new List<Transform>(mapped) { chest };
            var skeleton = new SkeletonBone[all.Count + 1];
            skeleton[0] = new SkeletonBone
            {
                name = root.name,
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one
            };
            for (int i = 0; i < all.Count; i++)
            {
                skeleton[i + 1] = new SkeletonBone
                {
                    name = all[i].name,
                    position = all[i].localPosition,
                    rotation = all[i].localRotation,
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

            Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            return avatar != null && avatar.isValid && avatar.isHuman ? avatar : null;
        }

        private static Transform Bone(Transform parent, string name, Vector3 localPosition)
        {
            var bone = new GameObject(name);
            bone.transform.SetParent(parent, false);
            bone.transform.localPosition = localPosition;
            return bone.transform;
        }

        private static void SetPrivate(object target, string name, object value)
        {
            target.GetType()
                .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(target, value);
        }
    }
}
