using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Tests.EditMode.AI
{
    /// <summary>
    ///     Builds a minimal but genuinely valid Humanoid avatar for tests that need one.
    /// </summary>
    /// <remarks>
    ///     Body Animation refuses to run on anything but a Humanoid rig, so a test that wants to
    ///     reach the "set up and working" state cannot fake it with a bare
    ///     <see cref="Animator" /> — the setup service checks
    ///     <see cref="Avatar.isHuman" />, exactly as it should. The fifteen required bones are the
    ///     smallest skeleton <see cref="AvatarBuilder.BuildHumanAvatar" /> accepts.
    /// </remarks>
    internal static class HumanoidRigFixture
    {
        /// <summary>Unity's human names for the fifteen required bones, in skeleton order.</summary>
        private static readonly string[] RequiredHumanNames =
        {
            "Hips", "Spine", "Neck", "Head",
            "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
            "RightUpperLeg", "RightLowerLeg", "RightFoot"
        };

        /// <summary>
        ///     Builds the skeleton under <paramref name="root" /> and compiles it into an avatar.
        ///     Returns <c>null</c> when this editor refuses the description, so the caller can fail
        ///     with a clear message rather than a confusing downstream assertion.
        /// </summary>
        internal static Avatar BuildAvatar(GameObject root, Scene scene)
        {
            Transform hips = Bone(root.transform, "Hips", new Vector3(0f, 1.00f, 0f), scene);
            Transform spine = Bone(hips, "Spine", new Vector3(0f, 0.12f, 0f), scene);
            Transform chest = Bone(spine, "Chest", new Vector3(0f, 0.12f, 0f), scene);
            Transform neck = Bone(chest, "Neck", new Vector3(0f, 0.15f, 0f), scene);
            Transform head = Bone(neck, "Head", new Vector3(0f, 0.10f, 0f), scene);

            Transform leftUpperArm = Bone(chest, "LeftUpperArm", new Vector3(0.15f, 0.05f, 0f), scene);
            Transform leftLowerArm = Bone(leftUpperArm, "LeftLowerArm", new Vector3(0.28f, 0f, 0f), scene);
            Transform leftHand = Bone(leftLowerArm, "LeftHand", new Vector3(0.25f, 0f, 0f), scene);

            Transform rightUpperArm = Bone(chest, "RightUpperArm", new Vector3(-0.15f, 0.05f, 0f), scene);
            Transform rightLowerArm = Bone(rightUpperArm, "RightLowerArm", new Vector3(-0.28f, 0f, 0f), scene);
            Transform rightHand = Bone(rightLowerArm, "RightHand", new Vector3(-0.25f, 0f, 0f), scene);

            Transform leftUpperLeg = Bone(hips, "LeftUpperLeg", new Vector3(0.09f, -0.05f, 0f), scene);
            Transform leftLowerLeg = Bone(leftUpperLeg, "LeftLowerLeg", new Vector3(0f, -0.45f, 0f), scene);
            Transform leftFoot = Bone(leftLowerLeg, "LeftFoot", new Vector3(0f, -0.42f, 0.05f), scene);

            Transform rightUpperLeg = Bone(hips, "RightUpperLeg", new Vector3(-0.09f, -0.05f, 0f), scene);
            Transform rightLowerLeg = Bone(rightUpperLeg, "RightLowerLeg", new Vector3(0f, -0.45f, 0f), scene);
            Transform rightFoot = Bone(rightLowerLeg, "RightFoot", new Vector3(0f, -0.42f, 0.05f), scene);

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
                    humanName = RequiredHumanNames[i],
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

        private static Transform Bone(Transform parent, string name, Vector3 localPosition, Scene scene)
        {
            var bone = new GameObject(name);
            if (scene.IsValid() && scene.isLoaded) SceneManager.MoveGameObjectToScene(bone, scene);
            bone.transform.SetParent(parent, false);
            bone.transform.localPosition = localPosition;
            return bone.transform;
        }
    }
}
