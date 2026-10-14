using System.Collections.Generic;
using Convai.Modules.BodyLanguage.Core.Pose;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Coverage for <see cref="HandMicroSolver" />: unbound safety, bound-rig
    ///     write bounds, determinism, the static-rig no-compounding guard contract, and the
    ///     zero-alloc steady-state gate. Bound-rig cases build a minimal code-authored Humanoid
    ///     avatar (wrists + eight finger proximal phalanges) the same way
    ///     <c>BodyAnimationRuntimeSmokeTests</c> does; if <see cref="Avatar.isValid" /> comes back
    ///     false for the current Unity version, those cases report the diagnostic via
    ///     <see cref="Assert.Ignore(string)" /> rather than failing.
    /// </summary>
    public sealed class HandMicroSolverTests
    {
        private static readonly string[] CoreHumanNames =
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
                if (_cleanup[i] != null)
                    Object.DestroyImmediate(_cleanup[i]);
            _cleanup.Clear();
        }

        [Test]
        public void Unbound_TickDoesNotThrow_AndWritesNothing()
        {
            var solver = new HandMicroSolver();

            Assert.DoesNotThrow(() => solver.Tick(1.23f, 1f, 1f / 60f));
            Assert.IsFalse(solver.IsBound);
        }

        [Test]
        public void Bind_NullAnimator_Unbinds()
        {
            var solver = new HandMicroSolver();
            solver.Bind(null);

            Assert.IsFalse(solver.IsBound);
            Assert.IsFalse(solver.HasFingers);
            Assert.DoesNotThrow(() => solver.Tick(0f, 1f, 1f / 60f));
        }

        [Test]
        public void Reset_UnbindsAndRestoresGuard_WithoutThrowing()
        {
            var solver = new HandMicroSolver();
            solver.Bind(null);
            Assert.DoesNotThrow(() => solver.Reset());
            Assert.IsFalse(solver.IsBound);
        }

        [Test]
        public void BoundRig_WeightZero_BonesUntouchedAfterTick()
        {
            if (!TryBuildHumanoidRigWithHands(out Animator animator, out Transform leftHand, out Transform rightHand, out string diagnostic))
            {
                Assert.Ignore($"Could not build a test Humanoid+hands avatar: {diagnostic}");
                return;
            }

            Quaternion leftBefore = leftHand.localRotation;
            Quaternion rightBefore = rightHand.localRotation;

            var solver = new HandMicroSolver();
            solver.Bind(animator);
            Assert.IsTrue(solver.IsBound, "Both wrists must resolve on the built rig.");

            solver.Tick(10f, 0f, 1f / 60f);

            Assert.AreEqual(leftBefore, leftHand.localRotation);
            Assert.AreEqual(rightBefore, rightHand.localRotation);
        }

        [Test]
        public void BoundRig_FullWeight_WritesBoundedByMaxDegrees()
        {
            if (!TryBuildHumanoidRigWithHands(out Animator animator, out Transform leftHand, out Transform rightHand, out string diagnostic))
            {
                Assert.Ignore($"Could not build a test Humanoid+hands avatar: {diagnostic}");
                return;
            }

            var solver = new HandMicroSolver { MaxFingerCurlDegrees = 2.5f, MaxWristMicroDegrees = 2f };
            solver.Bind(animator);
            Assert.IsTrue(solver.IsBound);

            Quaternion leftBefore = leftHand.localRotation;
            Quaternion rightBefore = rightHand.localRotation;

            for (float t = 0f; t < 5f; t += 1f / 60f)
                solver.Tick(t, 1f, 1f / 60f);

            // Bound check: the wrist swing this recipe can ever produce is |sin+0.5*sin|*0.667 <=
            // 1.5*0.667 ~= 1.0 of the max, plus float slack — assert well within a generous cap.
            float leftAngle = Quaternion.Angle(leftBefore, leftHand.localRotation);
            float rightAngle = Quaternion.Angle(rightBefore, rightHand.localRotation);
            Assert.That(leftAngle, Is.LessThanOrEqualTo(solver.MaxWristMicroDegrees + 0.1f));
            Assert.That(rightAngle, Is.LessThanOrEqualTo(solver.MaxWristMicroDegrees + 0.1f));
        }

        [Test]
        public void BoundRig_Determinism_SameRigIdentityRebound_ProducesIdenticalRotationSequence()
        {
            // A fresh two-separate-GameObjects comparison would be confounded by
            // Transform.GetSiblingIndex() differing between two independently-created scene
            // roots (CreateSeed walks the parent chain including sibling index) — that is a
            // property of the test harness, not the solver. Instead, rebind the SAME rig
            // identity (same Animator/Transform chain -> same seed by construction) and replay
            // an identical time sequence from the same restored base pose (Bind's own guard
            // restore returns the bone to its pre-write rotation first).
            if (!TryBuildHumanoidRigWithHands(out Animator animator, out Transform leftHand, out _, out string diagnostic))
            {
                Assert.Ignore($"Could not build a test Humanoid+hands avatar: {diagnostic}");
                return;
            }

            var solver = new HandMicroSolver();
            solver.Bind(animator);

            var trajectoryA = new List<Quaternion>();
            for (float t = 0f; t < 1f; t += 1f / 60f)
            {
                solver.Tick(t, 1f, 1f / 60f);
                trajectoryA.Add(leftHand.localRotation);
            }

            solver.Reset();
            solver.Bind(animator);

            var trajectoryB = new List<Quaternion>();
            for (float t = 0f; t < 1f; t += 1f / 60f)
            {
                solver.Tick(t, 1f, 1f / 60f);
                trajectoryB.Add(leftHand.localRotation);
            }

            Assert.That(trajectoryB.Count, Is.EqualTo(trajectoryA.Count));
            for (int i = 0; i < trajectoryA.Count; i++)
                Assert.AreEqual(trajectoryA[i], trajectoryB[i],
                    $"Rotation at step {i} must match — the same rig identity must reseed to the same phases.");
        }

        [Test]
        public void BoundRig_StaticRig_TwoTicksWithoutExternalRepose_DoNotCompound()
        {
            if (!TryBuildHumanoidRigWithHands(out Animator animator, out Transform leftHand, out Transform rightHand, out string diagnostic))
            {
                Assert.Ignore($"Could not build a test Humanoid+hands avatar: {diagnostic}");
                return;
            }

            var solver = new HandMicroSolver();
            solver.Bind(animator);

            solver.Tick(1f, 1f, 1f / 60f);
            Quaternion leftAfterFirst = leftHand.localRotation;
            Quaternion rightAfterFirst = rightHand.localRotation;

            // Same time argument (a static-clock re-tick, mirroring a repeated frame with no
            // animator repose) — the guard must unwind to the same pre-write pose before
            // reapplying the identical delta, never integrating on top of the previous write.
            solver.Tick(1f, 1f, 1f / 60f);

            Assert.AreEqual(leftAfterFirst, leftHand.localRotation);
            Assert.AreEqual(rightAfterFirst, rightHand.localRotation);
        }

        [Test]
        public void BoundRig_ZeroAlloc_InSteadyState()
        {
            if (!TryBuildHumanoidRigWithHands(out Animator animator, out _, out _, out string diagnostic))
            {
                Assert.Ignore($"Could not build a test Humanoid+hands avatar: {diagnostic}");
                return;
            }

            var solver = new HandMicroSolver();
            solver.Bind(animator);

            const int warmup = 500;
            const int measured = 500;
            float t = 0f;
            for (int i = 0; i < warmup; i++)
            {
                solver.Tick(t, 1f, 1f / 60f);
                t += 1f / 60f;
            }

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < measured; i++)
                {
                    solver.Tick(t, 1f, 1f / 60f);
                    t += 1f / 60f;
                }
            });

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"HandMicroSolver.Tick must produce zero managed allocation events in steady state; measured {allocationCount} allocation events.");
        }

        /// <summary>
        ///     Builds a minimal T-pose Humanoid skeleton (17 core bones + wrists' eight finger
        ///     proximal phalanges) and attempts to compile it into a valid <see cref="Avatar" />
        ///     via <see cref="AvatarBuilder.BuildHumanAvatar" />, relying on
        ///     <see cref="HumanLimit.useDefaultValues" /> for every bone's muscle limits.
        /// </summary>
        private bool TryBuildHumanoidRigWithHands(
            out Animator animator, out Transform leftHand, out Transform rightHand, out string diagnostic)
        {
            var root = new GameObject("HandMicroSolverTestRig");
            _cleanup.Add(root);

            Transform hips = CreateBone(root.transform, "Hips", new Vector3(0f, 1.00f, 0f));
            Transform spine = CreateBone(hips, "Spine", new Vector3(0f, 0.12f, 0f));
            Transform chest = CreateBone(spine, "Chest", new Vector3(0f, 0.12f, 0f));
            Transform neck = CreateBone(chest, "Neck", new Vector3(0f, 0.15f, 0f));
            Transform head = CreateBone(neck, "Head", new Vector3(0f, 0.10f, 0f));

            Transform leftUpperArm = CreateBone(chest, "LeftUpperArm", new Vector3(0.15f, 0.05f, 0f));
            Transform leftLowerArm = CreateBone(leftUpperArm, "LeftLowerArm", new Vector3(0.28f, 0f, 0f));
            Transform leftHandT = CreateBone(leftLowerArm, "LeftHand", new Vector3(0.25f, 0f, 0f));

            Transform rightUpperArm = CreateBone(chest, "RightUpperArm", new Vector3(-0.15f, 0.05f, 0f));
            Transform rightLowerArm = CreateBone(rightUpperArm, "RightLowerArm", new Vector3(-0.28f, 0f, 0f));
            Transform rightHandT = CreateBone(rightLowerArm, "RightHand", new Vector3(-0.25f, 0f, 0f));

            Transform leftUpperLeg = CreateBone(hips, "LeftUpperLeg", new Vector3(0.09f, -0.05f, 0f));
            Transform leftLowerLeg = CreateBone(leftUpperLeg, "LeftLowerLeg", new Vector3(0f, -0.45f, 0f));
            Transform leftFoot = CreateBone(leftLowerLeg, "LeftFoot", new Vector3(0f, -0.42f, 0.05f));

            Transform rightUpperLeg = CreateBone(hips, "RightUpperLeg", new Vector3(-0.09f, -0.05f, 0f));
            Transform rightLowerLeg = CreateBone(rightUpperLeg, "RightLowerLeg", new Vector3(0f, -0.45f, 0f));
            Transform rightFoot = CreateBone(rightLowerLeg, "RightFoot", new Vector3(0f, -0.42f, 0.05f));

            // Finger proximal phalanges as small children of
            // each wrist, one HumanBodyBones entry per finger (thumbs excluded per the solver).
            Transform leftIndex = CreateBone(leftHandT, "LeftIndexProximal", new Vector3(0.03f, -0.01f, 0.01f));
            Transform leftMiddle = CreateBone(leftHandT, "LeftMiddleProximal", new Vector3(0.03f, -0.01f, 0.00f));
            Transform leftRing = CreateBone(leftHandT, "LeftRingProximal", new Vector3(0.03f, -0.01f, -0.01f));
            Transform leftLittle = CreateBone(leftHandT, "LeftLittleProximal", new Vector3(0.03f, -0.01f, -0.02f));

            Transform rightIndex = CreateBone(rightHandT, "RightIndexProximal", new Vector3(-0.03f, -0.01f, 0.01f));
            Transform rightMiddle = CreateBone(rightHandT, "RightMiddleProximal", new Vector3(-0.03f, -0.01f, 0.00f));
            Transform rightRing = CreateBone(rightHandT, "RightRingProximal", new Vector3(-0.03f, -0.01f, -0.01f));
            Transform rightLittle = CreateBone(rightHandT, "RightLittleProximal", new Vector3(-0.03f, -0.01f, -0.02f));

            var coreBones = new[]
            {
                hips, spine, chest, neck, head,
                leftUpperArm, leftLowerArm, leftHandT,
                rightUpperArm, rightLowerArm, rightHandT,
                leftUpperLeg, leftLowerLeg, leftFoot,
                rightUpperLeg, rightLowerLeg, rightFoot
            };

            var fingerBones = new[]
            {
                (HumanBodyBones.LeftIndexProximal, leftIndex),
                (HumanBodyBones.LeftMiddleProximal, leftMiddle),
                (HumanBodyBones.LeftRingProximal, leftRing),
                (HumanBodyBones.LeftLittleProximal, leftLittle),
                (HumanBodyBones.RightIndexProximal, rightIndex),
                (HumanBodyBones.RightMiddleProximal, rightMiddle),
                (HumanBodyBones.RightRingProximal, rightRing),
                (HumanBodyBones.RightLittleProximal, rightLittle)
            };

            var humanBoneList = new List<HumanBone>(coreBones.Length + fingerBones.Length);
            for (int i = 0; i < coreBones.Length; i++)
            {
                humanBoneList.Add(new HumanBone
                {
                    humanName = CoreHumanNames[i],
                    boneName = coreBones[i].name,
                    limit = new HumanLimit { useDefaultValues = true }
                });
            }
            foreach ((HumanBodyBones semantic, Transform bone) in fingerBones)
            {
                humanBoneList.Add(new HumanBone
                {
                    humanName = HumanTrait.BoneName[(int)semantic],
                    boneName = bone.name,
                    limit = new HumanLimit { useDefaultValues = true }
                });
            }

            var allBonesForSkeleton = new List<Transform>(coreBones);
            foreach ((_, Transform bone) in fingerBones) allBonesForSkeleton.Add(bone);

            var skeleton = new SkeletonBone[allBonesForSkeleton.Count + 1];
            skeleton[0] = new SkeletonBone
            {
                name = root.name,
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one
            };
            for (int i = 0; i < allBonesForSkeleton.Count; i++)
            {
                skeleton[i + 1] = new SkeletonBone
                {
                    name = allBonesForSkeleton[i].name,
                    position = allBonesForSkeleton[i].localPosition,
                    rotation = allBonesForSkeleton[i].localRotation,
                    scale = Vector3.one
                };
            }

            var description = new HumanDescription
            {
                human = humanBoneList.ToArray(),
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
            _cleanup.Add(avatar);

            animator = null;
            leftHand = leftHandT;
            rightHand = rightHandT;

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

            animator = root.AddComponent<Animator>();
            animator.avatar = avatar;

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
