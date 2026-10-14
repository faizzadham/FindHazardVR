using Convai.Runtime.Animation.ProceduralPose;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     Behavior tests for <see cref="ProceduralPoseCompositor" /> (v2 plan §3.1): bind-time
    ///     spine-chain weight redistribution (parity with the retired BodyLanguage-private
    ///     chain calibration), single-writer accumulate/apply composition, the shared
    ///     guard's static-rig no-integration guarantee across BOTH the frame-protocol apply and
    ///     Gaze's late torso-aim entry, the per-channel motor-filter layer,
    ///     and the zero-allocation steady-state gate.
    /// </summary>
    public sealed class ProceduralPoseCompositorTests
    {
        private GameObject _root;
        private Transform _hips;
        private Transform _spine;
        private Transform _chest;
        private Transform _upperChest;
        private Transform _leftShoulder;
        private Transform _rightShoulder;
        private Transform _neck;
        private Transform _head;
        private Transform _leftUpperLeg;
        private Transform _leftLowerLeg;
        private Transform _leftFoot;
        private Transform _rightUpperLeg;
        private Transform _rightLowerLeg;
        private Transform _rightFoot;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Root");
            _hips = NewChild(_root.transform, "Hips");
            _spine = NewChild(_hips, "Spine");
            _chest = NewChild(_spine, "Chest");
            _upperChest = NewChild(_chest, "UpperChest");
            _leftShoulder = NewChild(_upperChest, "LeftShoulder");
            _rightShoulder = NewChild(_upperChest, "RightShoulder");
            _neck = NewChild(_upperChest, "Neck");
            _head = NewChild(_neck, "Head");

            // Knee bend deepened from a 0.05 to a 0.15 forward/back offset
            // (extension ratio ~0.994 -> ~0.949) — the old 0.05 depth put this "normally bent"
            // fixture's extension just ABOVE the new ProceduralPoseCompositor.LegChainNearFullExtension
            // 0.99 gate (while still under TwoBoneLegSolver's own 0.995 gate), which would have
            // silently disabled leg compensation for every test below that relies on it. The
            // straight-line hip-to-foot distance (0.9) is unchanged by this offset (the two
            // segments' z components cancel), so only the bend depth — not the leg's overall
            // height — changes.
            _leftUpperLeg = NewChild(_hips, "LeftUpperLeg", Vector3.zero);
            _leftLowerLeg = NewChild(_leftUpperLeg, "LeftLowerLeg", new Vector3(0f, -0.45f, 0.15f));
            _leftFoot = NewChild(_leftLowerLeg, "LeftFoot", new Vector3(0f, -0.45f, -0.15f));
            _rightUpperLeg = NewChild(_hips, "RightUpperLeg", Vector3.zero);
            _rightLowerLeg = NewChild(_rightUpperLeg, "RightLowerLeg", new Vector3(0f, -0.45f, 0.15f));
            _rightFoot = NewChild(_rightLowerLeg, "RightFoot", new Vector3(0f, -0.45f, -0.15f));
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        private static Transform NewChild(Transform parent, string name) =>
            NewChild(parent, name, new Vector3(0f, 0.1f, 0f));

        private static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        #region Bind redistribution parity (ports the retired chain-calibration weight tests)

        [Test]
        public void FullChain_DistributesWeightsAcrossAllThreeBones()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            Assert.IsTrue(compositor.IsBound);
            Assert.That(compositor.SpineWeight, Is.GreaterThan(0f));
            Assert.That(compositor.ChestWeight, Is.GreaterThan(0f));
            Assert.That(compositor.UpperChestWeight, Is.GreaterThan(0f));
            Assert.That(compositor.SpineWeight + compositor.ChestWeight + compositor.UpperChestWeight,
                Is.EqualTo(1f).Within(1e-4f), "Full-chain weights must sum to 1.");
            Assert.IsTrue(compositor.HasShoulders);
        }

        [Test]
        public void MissingUpperChest_RedistributesItsShareOntoSurvivingBones()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, null, _leftShoulder, _rightShoulder);

            Assert.That(compositor.UpperChestWeight, Is.EqualTo(0f));
            Assert.That(compositor.SpineWeight + compositor.ChestWeight,
                Is.EqualTo(1f).Within(1e-4f), "The missing UpperChest's share must redistribute onto Spine/Chest.");
            Assert.That(compositor.SpineWeight, Is.GreaterThan(0f));
            Assert.That(compositor.ChestWeight, Is.GreaterThan(0f));
        }

        [Test]
        public void MissingChestAndUpperChest_PutsFullWeightOnSpine()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, null, null, _leftShoulder, _rightShoulder);

            Assert.That(compositor.SpineWeight, Is.EqualTo(1f).Within(1e-4f),
                "With only Spine surviving, it must carry the full posture/breath swing.");
            Assert.That(compositor.ChestWeight, Is.EqualTo(0f));
            Assert.That(compositor.UpperChestWeight, Is.EqualTo(0f));
        }

        [Test]
        public void MissingOneShoulder_DisablesTensionChannel_SpineChainUnaffected()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, null);

            Assert.IsFalse(compositor.HasShoulders, "One missing shoulder must disable the tension channel.");
            Assert.That(compositor.SpineWeight + compositor.ChestWeight + compositor.UpperChestWeight,
                Is.EqualTo(1f).Within(1e-4f), "Spine-chain weights must stay intact when only shoulders are missing.");
        }

        [Test]
        public void MissingBothShoulders_DisablesTensionChannel()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, null, null);

            Assert.IsFalse(compositor.HasShoulders);
        }

        [Test]
        public void PostureSilhouette_EqualOpennessAndLean_DoNotCancelAcrossChain()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            compositor.BeginFrame();
            compositor.AddPostureSilhouette(opennessDegrees: 8f, leanDegrees: 8f);
            compositor.ApplyAccumulated(1f / 60f);

            Assert.That(Quaternion.Angle(Quaternion.identity, _spine.localRotation), Is.GreaterThan(0.1f),
                "Lean must remain readable on the lower spine even when openness has equal magnitude.");
            Assert.That(Quaternion.Angle(Quaternion.identity, _upperChest.localRotation), Is.GreaterThan(0.1f),
                "Openness must remain readable on UpperChest instead of cancelling lean globally.");
        }

        [Test]
        public void NoSpine_IsNotBound()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(null, _chest, _upperChest, _leftShoulder, _rightShoulder);

            Assert.IsFalse(compositor.IsBound, "Spine is required upstream — the compositor must not report bound without it.");
        }

        [Test]
        public void Clear_ResetsToUnboundState()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);
            Assert.IsTrue(compositor.IsBound);

            compositor.Clear();

            Assert.IsFalse(compositor.IsBound);
            Assert.That(compositor.SpineWeight, Is.EqualTo(0f));
            Assert.IsFalse(compositor.HasShoulders);
        }

        [Test]
        public void HasHeadChain_BothNeckAndHead_IsTrue()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head);

            Assert.IsTrue(compositor.HasHeadChain);
        }

        [Test]
        public void HasHeadChain_MissingHead_IsFalse()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, null);

            Assert.IsFalse(compositor.HasHeadChain);
        }

        #endregion

        #region Accumulate + Apply single-writer composition

        [Test]
        public void ApplyAccumulated_WritesEachSpineBoneOnceWithWeightedSwing()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            compositor.BeginFrame();
            compositor.AddSpineChainSwing(10f, 0f);
            compositor.ApplyAccumulated(1f / 60f);

            AssertWeightedSwingApplied(_spine, compositor.SpineWeight, 10f, 0f);
            AssertWeightedSwingApplied(_chest, compositor.ChestWeight, 10f, 0f);
            AssertWeightedSwingApplied(_upperChest, compositor.UpperChestWeight, 10f, 0f);
        }

        /// <summary>
        ///     Verifies a spine-chain bone's own weighted contribution via its LOCAL rotation
        ///     (each bone here starts at an identity local rotation, per <c>SetUp</c>), not a
        ///     naive world-rotation reconstruction: Spine/Chest/UpperChest are written in
        ///     parent-to-child order within the SAME <c>ApplyAccumulated</c> call, so a bone
        ///     written after its own ancestor (e.g. Chest after Spine) has its WORLD rotation
        ///     already carried along by that ancestor's write before its own delta is even
        ///     computed — comparing against the bone's ORIGINAL (pre-frame) world rotation would
        ///     wrongly fold that inherited chain motion into the "error". The bone's LOCAL
        ///     rotation has no such ambiguity: for an identity-start local rotation, a delta
        ///     built from the bone's OWN (possibly-already-rotated) parent axes always resolves,
        ///     once conjugated back into that same parent's frame, to this canonical
        ///     reference-independent quaternion (the identity
        ///     <c>R⁻¹ · AngleAxis(θ, R·v) · R = AngleAxis(θ, v)</c> holds per factor, so the
        ///     whole swing composition collapses to axis-agnostic local axes).
        /// </summary>
        private static void AssertWeightedSwingApplied(
            Transform bone, float weight, float sagittalDegrees, float lateralDegrees)
        {
            Quaternion expectedLocal = Quaternion.AngleAxis(lateralDegrees * weight, Vector3.forward) *
                Quaternion.AngleAxis(-sagittalDegrees * weight, Vector3.right);

            Assert.That(Quaternion.Angle(bone.localRotation, expectedLocal), Is.LessThan(1e-3f),
                $"{bone.name} must receive exactly its weight-share ({weight:0.000}) of the accumulated swing.");
        }

        [Test]
        public void StaticRig_RepeatedCycles_DoNotIntegrate_AndSharedGuardSeesTheComposite()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            Quaternion upperChestBaseLocal = _upperChest.localRotation;

            compositor.BeginFrame();
            compositor.AddSpineChainSwing(5f, 0f);
            compositor.ApplyAccumulated(1f / 60f);
            Quaternion afterFirstCycle = _upperChest.localRotation;

            compositor.BeginFrame();
            compositor.AddSpineChainSwing(5f, 0f);
            compositor.ApplyAccumulated(1f / 60f);
            Quaternion afterSecondCycle = _upperChest.localRotation;

            Assert.That(Quaternion.Angle(afterFirstCycle, afterSecondCycle), Is.LessThan(1e-4f),
                "Two identical cycles on a static rig (no external re-pose) must leave the bone at the SAME rotation each apply — no compounding.");

            // Third cycle: the frame-protocol apply AND ComposeTorsoAim both write UpperChest
            // within the SAME frame — the shared guard must treat both as one composite.
            compositor.BeginFrame();
            compositor.AddSpineChainSwing(5f, 0f);
            compositor.ApplyAccumulated(1f / 60f);
            compositor.ComposeTorsoAim(_root.transform, 8f, 0f);

            // The next frame's restore must unwind the ENTIRE composite (spine swing + torso
            // aim) back to the true original base pose in one shot.
            compositor.BeginFrame();
            Assert.That(Quaternion.Angle(_upperChest.localRotation, upperChestBaseLocal), Is.LessThan(1e-4f),
                "The next BeginFrame must restore the full composite (frame-protocol write + ComposeTorsoAim write) back to the original base pose.");
        }

        [Test]
        public void LongMixedAccumulationRun_SpineChainStaysSwingOnly_NoAccumulatedTwist()
        {
            // Regression for the swing-only composition invariant (previously covered at
            // PostureSolver level; the write path now lives here). A guard-restored clean base
            // every frame means twist error cannot compound across frames — it must stay small
            // on every single frame of a long, constantly-changing run.
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            for (int i = 0; i < 2000; i++)
            {
                float t = i * 0.01f;
                compositor.BeginFrame();
                compositor.AddSpineChainSwing(Mathf.Sin(t) * 10f, Mathf.Cos(t * 1.3f) * 10f);
                compositor.ApplyAccumulated(1f / 60f);
            }

            Quaternion delta = _chest.localRotation;
            Vector3 twistAxis = _chest.parent != null ? _chest.parent.up : Vector3.up;
            var rotationAxis = new Vector3(delta.x, delta.y, delta.z);
            float twistComponent = Vector3.Dot(rotationAxis, twistAxis.normalized);

            Assert.That(Mathf.Abs(twistComponent), Is.LessThan(0.05f),
                "Compositor spine-chain writes must stay swing-only: no accumulated roll/twist around the spine's long axis.");
        }

        #endregion

        #region ComposeTorsoAim

        [Test]
        public void ComposeTorsoAim_BothChestAndUpperChestPresent_Splits45_55()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            Quaternion chestWorldBefore = _chest.rotation;

            compositor.ComposeTorsoAim(_root.transform, 10f, 6f);

            // The share split is a slerp along the aim's own axis, NOT two independently-built
            // scaled yaw/pitch deltas: yaw and pitch do not commute, so scaling the pair twice
            // leaves a parasitic roll on the descendant bone. See AimSwingCompositionTests.
            Quaternion aim = ProceduralPoseMath.TorsoAimDelta(_root.transform, 10f, 6f);
            ProceduralPoseMath.SplitAimSwing(aim, 0.45f,
                out Quaternion expectedChestDelta, out Quaternion expectedUpperChestDelta);
            Quaternion expectedChestWorld = expectedChestDelta * chestWorldBefore;

            Assert.That(Quaternion.Angle(_chest.rotation, expectedChestWorld), Is.LessThan(1e-3f),
                "Chest must receive exactly the 0.45 share of the torso aim.");

            // UpperChest is Chest's child and is written AFTER it in the same ComposeTorsoAim
            // call, so by the time UpperChest's own delta is applied, its WORLD rotation has
            // already been carried along by Chest's own write (plain parent/child propagation) —
            // the expected base is Chest's POST-write world rotation, not UpperChest's ORIGINAL
            // pre-call one (they coincide only because this rig starts fully unrotated, but the
            // inherited Chest delta must still be folded in for the comparison to be exact).
            Assert.That(Quaternion.Angle(_upperChest.rotation, expectedUpperChestDelta * expectedChestWorld), Is.LessThan(1e-3f),
                "UpperChest must receive exactly the 0.55 share of the torso aim.");
        }

        /// <summary>
        ///     The property the 45/55 split exists to preserve: the two bones together must aim
        ///     exactly where a single unsplit write would, with no roll introduced along the way.
        /// </summary>
        [Test]
        public void ComposeTorsoAim_SplitAcrossTwoBones_MatchesTheUnsplitAimWithoutRoll()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            compositor.ComposeTorsoAim(_root.transform, 22f, -14f);

            Quaternion unsplit = ProceduralPoseMath.TorsoAimDelta(_root.transform, 22f, -14f);
            Vector3 achievedForward = _upperChest.rotation * Vector3.forward;

            Assert.That(Vector3.Angle(achievedForward, unsplit * Vector3.forward), Is.LessThan(0.01f),
                "The split torso aim must reach the same direction as an unsplit write.");

            Vector3 achievedUp = Vector3.ProjectOnPlane(_upperChest.rotation * Vector3.up, achievedForward);
            Vector3 unsplitUp = Vector3.ProjectOnPlane(unsplit * Vector3.up, achievedForward);
            Assert.That(Vector3.Angle(achievedUp, unsplitUp), Is.LessThan(0.01f),
                "Splitting the torso aim must not introduce roll around the aim axis.");
        }

        [Test]
        public void ComposeTorsoAim_OnlyChestPresent_GetsFullShare()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, null, _leftShoulder, _rightShoulder);

            Quaternion chestWorldBefore = _chest.rotation;

            compositor.ComposeTorsoAim(_root.transform, 10f, 6f);

            Quaternion expectedDelta = ProceduralPoseMath.TorsoAimDelta(_root.transform, 10f, 6f);
            Assert.That(Quaternion.Angle(_chest.rotation, expectedDelta * chestWorldBefore), Is.LessThan(1e-3f),
                "With only Chest present, it must receive the full (unshared) torso aim.");
        }

        [Test]
        public void ComposeTorsoAim_BeforeAnyBeginFrame_DoesNotThrow_AndWritesBones()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            Quaternion chestBefore = _chest.rotation;

            Assert.DoesNotThrow(() => compositor.ComposeTorsoAim(_root.transform, 10f, 5f));

            Assert.That(Quaternion.Angle(_chest.rotation, chestBefore), Is.GreaterThan(0.01f),
                "ComposeTorsoAim before any BeginFrame must still write the bones — EnsureFrameStarted covers the missing BeginFrame internally.");
        }

        #endregion

        #region Motor filter layer

        // Replaces the retired VelocityCap_LimitsFirstFrameChainSwingChange test: the inert
        // 720°/s CapVelocity net (and its MaxSwingChangeDegreesPerSecond knob) is gone —
        // per-channel MotorFilters with human biomechanical caps limit every accumulated
        // channel instead. Mirrors the compositor's internal MotorLimits table.
        private const float SpineTonicMaxSpeedDegreesPerSecond = 45f;

        [Test]
        public void MotorFilter_AbruptTonicSpineStep_PerFrameBoneChangeRespectsTonicCaps()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            const float dt = 1f / 120f;

            // Frame 0: a zero apply initializes every filter at rest on 0, so the abrupt 30°
            // step below is a real step against filter state, not the bind-time snap.
            compositor.BeginFrame();
            compositor.ApplyAccumulated(dt);

            Quaternion previousWorld = _upperChest.rotation;
            float maxPerFrameWorldDelta = 0f;
            float firstFrameApplied = float.NaN;

            // 2.5 s at 120 Hz: full accelerate/cruise/brake trajectory onto the 30° target.
            for (int i = 0; i < 300; i++)
            {
                compositor.BeginFrame();
                compositor.AddSpineChainSwing(30f, 0f);
                compositor.ApplyAccumulated(dt);

                // The full chain (weights summing to 1) carries the total swing, so
                // UpperChest's WORLD delta per frame tracks the total applied change.
                maxPerFrameWorldDelta = Mathf.Max(
                    maxPerFrameWorldDelta, Quaternion.Angle(_upperChest.rotation, previousWorld));
                previousWorld = _upperChest.rotation;

                if (i == 0) firstFrameApplied = compositor.AppliedSpineSagittalDegrees;
            }

            Assert.That(firstFrameApplied, Is.LessThan(1f),
                "An abrupt 30° tonic step must NOT reach the bones on its first frame — the old 720°/s net was inert, this must not be.");
            Assert.That(maxPerFrameWorldDelta,
                Is.LessThanOrEqualTo(SpineTonicMaxSpeedDegreesPerSecond * dt + 0.05f),
                "The applied bone rotation change per frame must respect the tonic velocity cap.");
            Assert.That(compositor.AppliedSpineSagittalDegrees, Is.EqualTo(30f).Within(0.05f),
                "The filtered tonic channel must still converge onto the full 30° target.");
        }

        [Test]
        public void MotorFilter_BallisticSpineAttack_TracksEnvelopeWithMinimalPeakLoss()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder);

            const float dt = 1f / 120f;
            const float attackSeconds = 0.1f;
            const float peakDegrees = 3f;

            compositor.BeginFrame();
            compositor.ApplyAccumulated(dt); // initialize filters at rest on 0

            Quaternion upperChestWorldBase = _upperChest.rotation;
            float peakApplied = 0f;
            float peakWorldDelta = 0f;

            // A 0.1 s half-cosine attack to 3°, then a short hold — a reaction-flinch-shaped
            // transient whose peak velocity (~47°/s) exceeds the TONIC budget but sits well
            // inside the BALLISTIC one, so the ballistic lane must track it near-losslessly.
            for (int i = 1; i <= 24; i++)
            {
                float t = i * dt;
                float target = t < attackSeconds
                    ? 0.5f * peakDegrees * (1f - Mathf.Cos(Mathf.PI * t / attackSeconds))
                    : peakDegrees;

                compositor.BeginFrame();
                compositor.AddSpineChainSwingBallistic(target, 0f);
                compositor.ApplyAccumulated(dt);

                peakApplied = Mathf.Max(peakApplied, compositor.AppliedSpineSagittalDegrees);
                peakWorldDelta = Mathf.Max(
                    peakWorldDelta, Quaternion.Angle(_upperChest.rotation, upperChestWorldBase));
            }

            Assert.That(peakApplied, Is.GreaterThanOrEqualTo(0.95f * peakDegrees),
                $"The ballistic lane must track a 0.1 s attack with < 5% peak loss; peaked at {peakApplied}° of {peakDegrees}°.");
            Assert.That(peakWorldDelta, Is.GreaterThan(0.9f * peakDegrees),
                "The ballistic contribution must actually reach the bones, not just the motion meter.");
        }

        #endregion

        #region Breath head stabilization (v2 plan §4.1, B5)

        [Test]
        public void AddBreathHeadStabilization_ComposesWithHeadGesture_OneWritePerBone()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head);

            Quaternion neckWorldBefore = _neck.rotation;

            const float breathSagittal = 4f;
            const float stabilization01 = 0.8f;
            const float gesturePitch = 3f;
            const float gestureYaw = 2f;
            const float gestureRoll = 1f;
            const float neckGestureShare = 0.4f;

            compositor.BeginFrame();
            compositor.AddHeadGesture(gesturePitch, gestureYaw, gestureRoll);
            compositor.AddBreathHeadStabilization(breathSagittal, stabilization01);
            compositor.ApplyAccumulated(1f / 60f);

            float counter = -breathSagittal * stabilization01;
            float expectedHeadPitch = gesturePitch + counter * 0.6f;
            float expectedNeckPitch = gesturePitch * neckGestureShare + counter * 0.4f;

            // Head is written BEFORE Neck within ApplyAccumulated, using Neck's (live) axes as
            // its reference — so comparing Head's WORLD rotation against a delta rebuilt from
            // Neck's post-frame (already-written) axes double-counts Neck's own contribution.
            // Head's LOCAL rotation has no such ambiguity: starting from an identity local
            // rotation, a delta built from the bone's own (whatever-state) parent axes always
            // collapses, once expressed back in that parent's own frame, to this canonical
            // reference-independent quaternion — see
            // ApplyAccumulated_WritesEachSpineBoneOnceWithWeightedSwing's AssertWeightedSwingApplied
            // for the same identity applied to the spine chain.
            Quaternion expectedHeadLocal = Quaternion.AngleAxis(gestureYaw, Vector3.up) *
                Quaternion.AngleAxis(-expectedHeadPitch, Vector3.right) *
                Quaternion.AngleAxis(gestureRoll, Vector3.forward);
            Assert.That(Quaternion.Angle(_head.localRotation, expectedHeadLocal), Is.LessThan(1e-3f),
                "Head must receive exactly one write combining the gesture pitch/yaw/roll with its 0.6 share of the breath-stabilization counter-pitch.");

            // Neck's own reference (UpperChest) is never itself written by this call, so its
            // world-rotation comparison is unaffected by write-order compounding and stays valid.
            Quaternion expectedNeckDelta = ProceduralPoseMath.PitchYawRollDelta(
                _neck.parent, expectedNeckPitch, gestureYaw * neckGestureShare, gestureRoll * neckGestureShare);
            Assert.That(Quaternion.Angle(_neck.rotation, expectedNeckDelta * neckWorldBefore), Is.LessThan(1e-3f),
                "Neck must receive exactly one write combining its gesture share with its 0.4 share of the breath-stabilization counter-pitch.");
        }

        [Test]
        public void AddBreathHeadStabilization_OnlyHeadBone_GetsFullCounter()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, head: _head);

            Assert.IsFalse(compositor.HasHeadChain, "Precondition: no Neck bound, so the gesture-gated HasHeadChain must be false.");
            Quaternion headWorldBefore = _head.rotation;

            compositor.BeginFrame();
            compositor.AddBreathHeadStabilization(4f, 0.8f);
            compositor.ApplyAccumulated(1f / 60f);

            float counter = -4f * 0.8f;
            Quaternion expectedDelta = ProceduralPoseMath.PitchYawRollDelta(_head.parent, counter, 0f, 0f);

            Assert.That(Quaternion.Angle(_head.rotation, expectedDelta * headWorldBefore), Is.LessThan(1e-3f),
                "With only Head bound (no Neck, so HasHeadChain is false), stabilization must still write the FULL counter-pitch to Head alone.");
        }

        #endregion

        #region Neck-lead explicit path

        [Test]
        public void AddNeckGesture_ExplicitPath_WritesNeckFromNeckAccums_NotShareSplit()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head);

            Quaternion neckWorldBefore = _neck.rotation;

            const float gesturePitch = 3f;
            const float gestureYaw = 2f;
            const float gestureRoll = 1f;
            // Deliberately NOT a fixed 0.4x share of the head values — proves the explicit neck
            // channel is genuinely independent of the legacy share-split.
            const float neckPitch = 5f;
            const float neckYaw = -1f;
            const float neckRoll = 0.5f;

            compositor.BeginFrame();
            compositor.AddHeadGesture(gesturePitch, gestureYaw, gestureRoll);
            compositor.AddNeckGesture(neckPitch, neckYaw, neckRoll);
            compositor.ApplyAccumulated(1f / 60f);

            // Head is written BEFORE Neck within ApplyAccumulated — see
            // AddBreathHeadStabilization_ComposesWithHeadGesture_OneWritePerBone for why the
            // LOCAL rotation (not a world-rotation reconstruction from Neck's post-frame axes)
            // is the only reference-independent way to verify Head's own contribution here.
            Quaternion expectedHeadLocal = Quaternion.AngleAxis(gestureYaw, Vector3.up) *
                Quaternion.AngleAxis(-gesturePitch, Vector3.right) *
                Quaternion.AngleAxis(gestureRoll, Vector3.forward);
            Assert.That(Quaternion.Angle(_head.localRotation, expectedHeadLocal), Is.LessThan(1e-3f),
                "Head must still receive exactly its own gesture accumulator, unaffected by the explicit neck channel.");

            // Neck's own reference (UpperChest) is never written by this call, so its world
            // comparison is unaffected by write-order compounding and stays valid as-is.
            Quaternion expectedNeckDelta = ProceduralPoseMath.PitchYawRollDelta(_neck.parent, neckPitch, neckYaw, neckRoll);
            Assert.That(Quaternion.Angle(_neck.rotation, expectedNeckDelta * neckWorldBefore), Is.LessThan(1e-3f),
                "With AddNeckGesture called this frame, Neck must be written AS-IS from the neck accumulators — not gesturePitch*NeckGestureShare.");
        }

        [Test]
        public void AddNeckGesture_NotCalled_FallsBackToLegacyShareSplit()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head);

            Quaternion neckWorldBefore = _neck.rotation;
            const float gesturePitch = 3f;
            const float gestureYaw = 2f;
            const float gestureRoll = 1f;

            compositor.BeginFrame();
            compositor.AddHeadGesture(gesturePitch, gestureYaw, gestureRoll);
            // No AddNeckGesture call this frame — must fall back to the legacy share-split.
            compositor.ApplyAccumulated(1f / 60f);

            float expectedNeckPitch = gesturePitch * ProceduralPoseCompositor.NeckGestureShare;
            float expectedNeckYaw = gestureYaw * ProceduralPoseCompositor.NeckGestureShare;
            float expectedNeckRoll = gestureRoll * ProceduralPoseCompositor.NeckGestureShare;
            Quaternion expectedNeckDelta = ProceduralPoseMath.PitchYawRollDelta(
                _neck.parent, expectedNeckPitch, expectedNeckYaw, expectedNeckRoll);

            Assert.That(Quaternion.Angle(_neck.rotation, expectedNeckDelta * neckWorldBefore), Is.LessThan(1e-3f),
                "Without an AddNeckGesture call, Neck must fall back to the legacy NeckGestureShare split of the head gesture — byte-compatible with every caller that predates the explicit neck channel.");
        }

        #endregion

        #region Pelvis channel (v2 plan §4.2)

        [Test]
        public void HasLegChain_TrueOnlyWhenHipsAndAllSixLegBonesResolve()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(
                _spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips,
                _leftUpperLeg, _leftLowerLeg, _leftFoot, _rightUpperLeg, _rightLowerLeg, _rightFoot);

            Assert.IsTrue(compositor.HasLegChain);

            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips);
            Assert.IsFalse(compositor.HasLegChain, "Missing leg bones must disable the leg chain even with Hips bound.");
        }

        [Test]
        public void AddPelvis_RotatesAndTranslatesHips()
        {
            var compositor = new ProceduralPoseCompositor { LegCompensationEnabled = false };
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips);

            Vector3 hipsWorldPosBefore = _hips.position;
            Quaternion hipsWorldRotBefore = _hips.rotation;

            compositor.BeginFrame();
            compositor.AddPelvis(0.03f, 2f, 3f);
            compositor.ApplyAccumulated(1f / 60f);

            Assert.That(Vector3.Distance(_hips.position, hipsWorldPosBefore), Is.GreaterThan(0.01f),
                "AddPelvis must translate Hips laterally.");
            Assert.That(Quaternion.Angle(_hips.rotation, hipsWorldRotBefore), Is.GreaterThan(0.5f),
                "AddPelvis must rotate Hips (obliquity + yaw).");
        }

        [Test]
        public void AddPelvis_BelowEpsilon_WritesNothing()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips);

            Vector3 hipsPosBefore = _hips.localPosition;
            Quaternion hipsRotBefore = _hips.localRotation;

            compositor.BeginFrame();
            compositor.AddPelvis(1e-7f, 1e-6f, 1e-6f);
            compositor.ApplyAccumulated(1f / 60f);

            Assert.That(_hips.localPosition, Is.EqualTo(hipsPosBefore));
            Assert.That(_hips.localRotation, Is.EqualTo(hipsRotBefore));
        }

        [Test]
        public void StaticRig_RepeatedPelvisCycles_PositionDoesNotIntegrate_AndRestoresOnComposite()
        {
            var compositor = new ProceduralPoseCompositor { LegCompensationEnabled = false };
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips);

            Vector3 hipsBaseLocalPos = _hips.localPosition;

            compositor.BeginFrame();
            compositor.AddPelvis(0.02f, 1f, 1f);
            compositor.ApplyAccumulated(1f / 60f);
            Vector3 afterFirstCycle = _hips.localPosition;

            compositor.BeginFrame();
            compositor.AddPelvis(0.02f, 1f, 1f);
            compositor.ApplyAccumulated(1f / 60f);
            Vector3 afterSecondCycle = _hips.localPosition;

            Assert.That(Vector3.Distance(afterFirstCycle, afterSecondCycle), Is.LessThan(1e-4f),
                "Two identical pelvis cycles on a static rig (no external re-pose) must leave Hips at the SAME local position each apply — no compounding.");

            compositor.BeginFrame();
            Assert.That(Vector3.Distance(_hips.localPosition, hipsBaseLocalPos), Is.LessThan(1e-4f),
                "The next BeginFrame must restore Hips' local position back to the original base pose.");
        }

        [Test]
        public void PelvisAppliesBeforeSpineChain_ChestReflectsMovedParent()
        {
            // Two compositors on identical rigs: one applies ONLY the spine swing, the other
            // applies the SAME spine swing plus a pelvis yaw. If (and only if) the pelvis write
            // lands before the spine chain, Spine's reference (its parent, Hips) already carries
            // the pelvis yaw by the time the spine's own SwingDelta is built against it — so the
            // pelvis-inclusive run's Chest must diverge from the spine-only run's Chest by
            // roughly the pelvis yaw, not merely by rounding noise.
            var compositorSpineOnly = new ProceduralPoseCompositor { LegCompensationEnabled = false };
            compositorSpineOnly.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips);
            compositorSpineOnly.BeginFrame();
            compositorSpineOnly.AddSpineChainSwing(4f, 0f);
            compositorSpineOnly.ApplyAccumulated(1f / 60f);
            Quaternion chestSpineOnly = _chest.rotation;

            Object.DestroyImmediate(_root);
            SetUp(); // Fresh, untouched rig for the pelvis-inclusive run.
            var compositorWithPelvis = new ProceduralPoseCompositor { LegCompensationEnabled = false };
            compositorWithPelvis.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips);
            compositorWithPelvis.BeginFrame();
            compositorWithPelvis.AddPelvis(0f, 0f, 6f); // yaw only, easiest to reason about independent of translation.
            compositorWithPelvis.AddSpineChainSwing(4f, 0f);
            compositorWithPelvis.ApplyAccumulated(1f / 60f);
            Quaternion chestWithPelvis = _chest.rotation;

            Assert.That(Quaternion.Angle(chestWithPelvis, chestSpineOnly), Is.GreaterThan(4f),
                "Chest must reflect the pelvis yaw (inherited via the moved Hips parent, applied BEFORE the spine chain) on top of the spine's own swing.");
        }

        [Test]
        public void LegCompensation_RePinsFeet_AfterPelvisWeightShift()
        {
            var compositor = new ProceduralPoseCompositor { LegCompensationEnabled = true };
            compositor.BindManual(
                _spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips,
                _leftUpperLeg, _leftLowerLeg, _leftFoot, _rightUpperLeg, _rightLowerLeg, _rightFoot);

            Assert.IsTrue(compositor.HasLegChain, "Sanity: the leg chain must resolve for this test.");

            Vector3 leftFootBefore = _leftFoot.position;
            Vector3 rightFootBefore = _rightFoot.position;

            compositor.BeginFrame();
            compositor.AddPelvis(0.03f, 0f, 0f);
            compositor.ApplyAccumulated(1f / 60f);

            Assert.That(Vector3.Distance(_leftFoot.position, leftFootBefore), Is.LessThan(2e-3f),
                "With leg compensation active, the left foot must stay (near) pinned despite the pelvis shift.");
            Assert.That(Vector3.Distance(_rightFoot.position, rightFootBefore), Is.LessThan(2e-3f),
                "With leg compensation active, the right foot must stay (near) pinned despite the pelvis shift.");
        }

        [Test]
        public void LegChainNearFullExtension_TrueOnStraightLegRig_FalseOnBentLegRig()
        {
            var bentCompositor = new ProceduralPoseCompositor();
            bentCompositor.BindManual(
                _spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips,
                _leftUpperLeg, _leftLowerLeg, _leftFoot, _rightUpperLeg, _rightLowerLeg, _rightFoot);
            bentCompositor.BeginFrame();

            Assert.IsFalse(bentCompositor.LegChainNearFullExtension,
                "The SetUp fixture's moderately bent knees must not read as near-full-extension.");

            // Straighten both legs (collinear, extension ~= 1.0) on the SAME transforms.
            _leftLowerLeg.localPosition = new Vector3(0f, -0.45f, 0f);
            _leftFoot.localPosition = new Vector3(0f, -0.45f, 0f);
            _rightLowerLeg.localPosition = new Vector3(0f, -0.45f, 0f);
            _rightFoot.localPosition = new Vector3(0f, -0.45f, 0f);

            var straightCompositor = new ProceduralPoseCompositor();
            straightCompositor.BindManual(
                _spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips,
                _leftUpperLeg, _leftLowerLeg, _leftFoot, _rightUpperLeg, _rightLowerLeg, _rightFoot);
            straightCompositor.BeginFrame();

            Assert.IsTrue(straightCompositor.LegChainNearFullExtension,
                "A straight (fully extended) leg chain must read as near-full-extension.");
        }

        [Test]
        public void ApplyPelvis_StraightLegRig_LeavesLegBonesUntouched_GateChainWorksEndToEnd()
        {
            // Even with LegCompensationEnabled explicitly true and a leg
            // chain that resolves, a near-full-extension rig must never run the leg solver — the
            // compositor enforces this gate itself (not merely relying on a well-behaved caller).
            _leftLowerLeg.localPosition = new Vector3(0f, -0.45f, 0f);
            _leftFoot.localPosition = new Vector3(0f, -0.45f, 0f);
            _rightLowerLeg.localPosition = new Vector3(0f, -0.45f, 0f);
            _rightFoot.localPosition = new Vector3(0f, -0.45f, 0f);

            var compositor = new ProceduralPoseCompositor { LegCompensationEnabled = true };
            compositor.BindManual(
                _spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips,
                _leftUpperLeg, _leftLowerLeg, _leftFoot, _rightUpperLeg, _rightLowerLeg, _rightFoot);

            compositor.BeginFrame();
            Assert.IsTrue(compositor.LegChainNearFullExtension, "Sanity: this rig must read as near-full-extension.");

            Quaternion leftUpperBefore = _leftUpperLeg.localRotation;
            Quaternion leftLowerBefore = _leftLowerLeg.localRotation;
            Quaternion leftFootBefore = _leftFoot.localRotation;
            Quaternion rightUpperBefore = _rightUpperLeg.localRotation;
            Quaternion rightLowerBefore = _rightLowerLeg.localRotation;
            Quaternion rightFootBefore = _rightFoot.localRotation;

            // A weight shift large enough to normally trigger a very visible leg-compensation solve.
            compositor.AddPelvis(0.03f, 2f, 3f);
            compositor.ApplyAccumulated(1f / 60f);

            Assert.That(_leftUpperLeg.localRotation, Is.EqualTo(leftUpperBefore),
                "A near-full-extension chain must never run the leg solver, even with LegCompensationEnabled=true.");
            Assert.That(_leftLowerLeg.localRotation, Is.EqualTo(leftLowerBefore));
            Assert.That(_leftFoot.localRotation, Is.EqualTo(leftFootBefore));
            Assert.That(_rightUpperLeg.localRotation, Is.EqualTo(rightUpperBefore));
            Assert.That(_rightLowerLeg.localRotation, Is.EqualTo(rightLowerBefore));
            Assert.That(_rightFoot.localRotation, Is.EqualTo(rightFootBefore));
        }

        #endregion

        #region Zero allocation

        [Test]
        public void SteadyStateFrameProtocol_AllocatesNothing()
        {
            var compositor = new ProceduralPoseCompositor();
            compositor.BindManual(_spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head);

            const int warmupIterations = 500;
            const int measuredIterations = 500;

            for (int i = 0; i < warmupIterations; i++) RunOneCycle(compositor, i);

            long allocationCount = MeasureAllocationCount(compositor, warmupIterations, measuredIterations);

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"BeginFrame + Add* + ApplyAccumulated must produce zero managed allocation events in steady state; measured {allocationCount} allocation events over {measuredIterations} ticks.");
        }

        private static void RunOneCycle(ProceduralPoseCompositor compositor, int i)
        {
            compositor.BeginFrame();
            compositor.AddSpineChainSwing(2f + i % 3, 0.5f);
            compositor.AddShoulderLift(0.3f);
            compositor.AddShoulderTension(0.2f);
            compositor.AddHeadGesture(0.1f, 0.1f, 0.1f);
            compositor.ApplyAccumulated(1f / 60f);
        }

        [Test]
        public void SteadyStateFrameProtocol_WithPelvisAndLegCompensation_AllocatesNothing()
        {
            var compositor = new ProceduralPoseCompositor { LegCompensationEnabled = true };
            compositor.BindManual(
                _spine, _chest, _upperChest, _leftShoulder, _rightShoulder, _neck, _head, _hips,
                _leftUpperLeg, _leftLowerLeg, _leftFoot, _rightUpperLeg, _rightLowerLeg, _rightFoot);

            const int warmupIterations = 500;
            const int measuredIterations = 500;

            for (int i = 0; i < warmupIterations; i++) RunOneCycleWithPelvis(compositor, i);

            long allocationCount = MeasureAllocationCountWithPelvis(compositor, warmupIterations, measuredIterations);

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"BeginFrame + AddPelvis + leg-compensation ApplyAccumulated must produce zero managed allocation events in steady state; measured {allocationCount} allocation events over {measuredIterations} ticks.");
        }

        private static void RunOneCycleWithPelvis(ProceduralPoseCompositor compositor, int i)
        {
            compositor.BeginFrame();
            compositor.AddSpineChainSwing(2f + i % 3, 0.5f);
            compositor.AddPelvis(0.01f * ((i % 2 == 0) ? 1f : -1f), 0.5f, 0.5f);
            compositor.ApplyAccumulated(1f / 60f);
        }

        private static long MeasureAllocationCountWithPelvis(ProceduralPoseCompositor compositor, int startIndex, int iterations)
        {
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < iterations; i++) RunOneCycleWithPelvis(compositor, startIndex + i);
            });

            return allocationCount;
        }

        private static long MeasureAllocationCount(ProceduralPoseCompositor compositor, int startIndex, int iterations)
        {
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < iterations; i++) RunOneCycle(compositor, startIndex + i);
            });

            return allocationCount;
        }

        #endregion
    }
}
