using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Solvers
{
    /// <summary>
    ///     Resolves and calibrates the gaze bone chain from the character's rig binding:
    ///     the root reference frame, torso/neck/head chain, and the eye bones with their
    ///     rest orientations (captured once at bind time so the eye stage can express
    ///     rotations relative to "looking straight ahead" regardless of authored bind roll).
    /// </summary>
    internal sealed class GazeChainCalibration
    {
        public Transform Root { get; private set; }
        public Transform Chest { get; private set; }
        public Transform UpperChest { get; private set; }
        public Transform Neck { get; private set; }
        public Transform Head { get; private set; }
        public Transform LeftEye { get; private set; }
        public Transform RightEye { get; private set; }

        /// <summary>Left-eye local rotation captured at bind time (rest pose).</summary>
        public Quaternion LeftEyeRestLocal { get; private set; } = Quaternion.identity;

        /// <summary>Right-eye local rotation captured at bind time (rest pose).</summary>
        public Quaternion RightEyeRestLocal { get; private set; } = Quaternion.identity;

        /// <summary>Left-eye parent world rotation captured at bind time.</summary>
        public Quaternion LeftEyeParentAtBind { get; private set; } = Quaternion.identity;

        /// <summary>Right-eye parent world rotation captured at bind time.</summary>
        public Quaternion RightEyeParentAtBind { get; private set; } = Quaternion.identity;

        /// <summary>Head (aim parent) world rotation captured at bind time.</summary>
        public Quaternion AimParentAtBind { get; private set; } = Quaternion.identity;

        /// <summary>Root forward at bind time — the "looking straight ahead" reference.</summary>
        public Vector3 RestForwardAtBind { get; private set; } = Vector3.forward;

        private bool _hasAxisCalibration;
        private Vector3 _rootForwardLocal = Vector3.forward;
        private Vector3 _rootUpLocal = Vector3.up;
        private Vector3 _leftEyeForwardLocal = Vector3.forward;
        private Vector3 _rightEyeForwardLocal = Vector3.forward;

        public bool HasAxisCalibration => _hasAxisCalibration;
        public bool IsBound { get; private set; }

        /// <summary>
        ///     Current world-space "eyes at rest" forward: the bind-time forward carried
        ///     along by the aim parent (head/neck), so it follows every head movement while
        ///     staying immune to authored bind roll on the eye bones themselves.
        /// </summary>
        public Vector3 CurrentEyeRestForward
        {
            get
            {
                Transform aimParent = Head != null ? Head : Neck;
                if (aimParent == null)
                    return Root != null ? Root.forward : RestForwardAtBind;

                return (aimParent.rotation * Quaternion.Inverse(AimParentAtBind)) * RestForwardAtBind;
            }
        }

        public Vector3 CurrentLeftEyeRestForward => GetEyeRestForward(LeftEye, LeftEyeRestLocal, _leftEyeForwardLocal);
        public Vector3 CurrentRightEyeRestForward => GetEyeRestForward(RightEye, RightEyeRestLocal, _rightEyeForwardLocal);

        /// <summary>
        ///     Measures the gaze shift this rig currently needs: the yaw/pitch from the eye line
        ///     to <paramref name="targetPoint" />, plus how far the animation has already moved
        ///     the head off its neutral.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Lives here because this type owns what the rig looks like, and because the
        ///         whole chain has to agree on one answer: the actuator ladder divides this
        ///         number up, and every stage downstream is checked against it. When each stage
        ///         measured for itself they could disagree, and the difference had nowhere to go
        ///         but the eyes' clamp.
        ///     </para>
        ///     <para>
        ///         <b>Measured from the eye line, not the head pivot.</b> The head bone sits
        ///         behind and below the eyes — about 7 cm each on a CC4 rig — so aiming the head
        ///         pivot at the target carries the eyes past it and leaves them holding a
        ///         standing counter-offset (~2.7° at 1.5 m, more at conversational range). The
        ///         head still rotates about its own pivot; only the direction it is asked to
        ///         point is measured from where the eyes actually are, which moves slightly as
        ///         the head turns — a second-order residual the per-frame re-solve absorbs.
        ///     </para>
        /// </remarks>
        public bool TryMeasureShift(Vector3 targetPoint, out GazeShiftMeasurement measurement)
        {
            measurement = default;
            if (Root == null) return false;

            bool calibrated = TryGetGazeReferenceFrame(out GazeReferenceFrame frame);

            bool hasTarget = calibrated
                ? GazeSolverMath.TryGetYawPitch(frame, EyeCenterPosition, targetPoint,
                    out float requiredYaw, out float requiredPitch)
                : GazeSolverMath.TryGetYawPitch(Root, EyeCenterPosition, targetPoint,
                    out requiredYaw, out requiredPitch);
            if (!hasTarget) return false;

            // Head-carried "straight ahead". Averaging the per-eye rest forwards here would
            // inject a constant fake deviation on rigs whose eye bones use unusual authored
            // local axes.
            Vector3 restForward = CurrentEyeRestForward;
            bool hasAnimated = calibrated
                ? GazeSolverMath.TryGetDirectionYawPitch(frame, restForward,
                    out float animatedYaw, out float animatedPitch)
                : GazeSolverMath.TryGetDirectionYawPitch(Root, restForward,
                    out animatedYaw, out animatedPitch);
            if (!hasAnimated)
            {
                animatedYaw = 0f;
                animatedPitch = 0f;
            }

            measurement = new GazeShiftMeasurement(requiredYaw, requiredPitch, animatedYaw, animatedPitch);
            return true;
        }

        public bool TryGetGazeReferenceFrame(out GazeReferenceFrame frame)
        {
            frame = default;
            if (!_hasAxisCalibration || Root == null) return false;
            frame = new GazeReferenceFrame(Root.TransformDirection(_rootForwardLocal), Root.TransformDirection(_rootUpLocal));
            return frame.IsValid;
        }

        public bool HasHeadChain => Head != null || Neck != null;
        public bool HasTorso => Chest != null || UpperChest != null;
        public bool HasEyeBones => LeftEye != null && RightEye != null;

        /// <summary>Pivot used for head-chain angle math (head → neck → root fallback).</summary>
        public Vector3 HeadPivotPosition =>
            Head != null ? Head.position :
            Neck != null ? Neck.position :
            Root != null ? Root.position + Vector3.up * 1.6f : Vector3.zero;

        /// <summary>Midpoint between the eyes (falls back to the head pivot).</summary>
        public Vector3 EyeCenterPosition =>
            HasEyeBones ? (LeftEye.position + RightEye.position) * 0.5f : HeadPivotPosition;

        /// <summary>Resolves bones from the context's rig binding.</summary>
        public void Bind(EmbodimentContext context, Transform fallbackRoot)
        {
            Clear();

            IStandardRigBinding rigBinding = context?.EnsureRigBinding();
            Root = rigBinding?.Root != null ? rigBinding.Root : fallbackRoot;
            if (rigBinding == null) return;

            if (rigBinding is StandardRigBinding standardBinding)
            {
                _hasAxisCalibration = standardBinding.TryGetGazeAxisCalibration(
                    out _rootForwardLocal, out _rootUpLocal,
                    out _leftEyeForwardLocal, out _rightEyeForwardLocal);
            }

            if (rigBinding.TryGetBone(StandardBone.Chest, out Transform chest)) Chest = chest;
            if (rigBinding.TryGetBone(StandardBone.UpperChest, out Transform upperChest)) UpperChest = upperChest;
            if (rigBinding.TryGetBone(StandardBone.Neck, out Transform neck)) Neck = neck;
            if (rigBinding.TryGetBone(StandardBone.Head, out Transform head)) Head = head;
            if (rigBinding.TryGetBone(StandardBone.LeftEye, out Transform leftEye)) LeftEye = leftEye;
            if (rigBinding.TryGetBone(StandardBone.RightEye, out Transform rightEye)) RightEye = rightEye;

            CaptureEyeRest();
            IsBound = true;
        }

        /// <summary>Direct bind for tests and custom rigs (no context required).</summary>
        public void BindManual(
            Transform root,
            Transform chest,
            Transform upperChest,
            Transform neck,
            Transform head,
            Transform leftEye,
            Transform rightEye)
        {
            Clear();
            Root = root;
            Chest = chest;
            UpperChest = upperChest;
            Neck = neck;
            Head = head;
            LeftEye = leftEye;
            RightEye = rightEye;
            CaptureEyeRest();
            IsBound = true;
        }

        public void Clear()
        {
            Root = null;
            Chest = null;
            UpperChest = null;
            Neck = null;
            Head = null;
            LeftEye = null;
            RightEye = null;
            LeftEyeRestLocal = Quaternion.identity;
            RightEyeRestLocal = Quaternion.identity;
            LeftEyeParentAtBind = Quaternion.identity;
            RightEyeParentAtBind = Quaternion.identity;
            AimParentAtBind = Quaternion.identity;
            RestForwardAtBind = Vector3.forward;
            _hasAxisCalibration = false;
            _rootForwardLocal = Vector3.forward;
            _rootUpLocal = Vector3.up;
            _leftEyeForwardLocal = Vector3.forward;
            _rightEyeForwardLocal = Vector3.forward;
            IsBound = false;
        }

        /// <summary>Restores any previously bound eye bones before a hot rebind releases them.</summary>
        public void RestoreEyeRest()
        {
            if (LeftEye != null) LeftEye.localRotation = LeftEyeRestLocal;
            if (RightEye != null) RightEye.localRotation = RightEyeRestLocal;
        }

        private void CaptureEyeRest()
        {
            // For calibrated rigs the "looking straight ahead" reference is the authored
            // gaze forward axis, not the Transform's +Z convention — a rig calibrated with
            // forward = +X would otherwise inherit a permanent 90° rest error that saturates
            // the eye stage's oculomotor clamp and lays the synthetic vergence eye pair
            // along the gaze axis instead of laterally.
            Vector3 restForward = Root != null ? Root.forward : Vector3.forward;
            if (_hasAxisCalibration)
            {
                Vector3 calibratedForward = Root != null
                    ? Root.TransformDirection(_rootForwardLocal)
                    : _rootForwardLocal;
                if (calibratedForward.sqrMagnitude > 1e-6f)
                    restForward = calibratedForward.normalized;
            }

            RestForwardAtBind = restForward;

            // The head's rest orientation comes from the rig's authored rest pose, never from
            // the pose the bones happen to be in when gaze binds. Bind runs in OnEnable, but a
            // rebind (Body Animation building its runtime rig) runs after the Animator has
            // already posed the character, and the scene itself can store a non-neutral pose.
            // Capturing the live head rotation as "straight ahead" then carries whatever the
            // idle clip was doing at that instant into every later measurement: the reflex
            // cancels a deviation that is not there, the head rests that many degrees off
            // centre, and the character turns further one way than the other. Measured on the
            // shipped sample as a 2–21° bias that changed from one Play session to the next,
            // and a 30° asymmetry between looking left and looking right.
            // Without a humanoid avatar the live pose is still the reference (the old behaviour):
            // a rig that authors a non-neutral head or eye rotation, or that is re-posed between
            // binds, relies on the bind-time pose being what it means by "straight ahead", and
            // remembering an earlier bind's pose instead broke a rebind after a mesh swap. The
            // session-dependent bias is therefore fixed for humanoid rigs only; a rig without an
            // avatar needs an authored neutral pose on its binding to get the same guarantee.
            Animator animator = Root != null ? Root.GetComponentInChildren<Animator>(true) : null;
            RestPoseLookup rest = RestPoseLookup.From(animator);

            Transform aimParent = Head != null ? Head : Neck;
            if (aimParent != null) AimParentAtBind = rest.WorldRotation(aimParent, Root);

            if (LeftEye != null)
            {
                LeftEyeRestLocal = rest.LocalRotation(LeftEye);
                if (LeftEye.parent != null) LeftEyeParentAtBind = rest.WorldRotation(LeftEye.parent, Root);
            }

            if (RightEye != null)
            {
                RightEyeRestLocal = rest.LocalRotation(RightEye);
                if (RightEye.parent != null) RightEyeParentAtBind = rest.WorldRotation(RightEye.parent, Root);
            }
        }

        /// <summary>
        ///     The rig's authored rest pose (the avatar's skeleton, as imported), so a bone's
        ///     neutral orientation can be asked for regardless of what animation currently has
        ///     it doing. Falls back to the live pose for any bone the avatar does not describe,
        ///     and entirely for a rig with no humanoid avatar. Bind-time only, so its
        ///     allocations are not per-frame.
        /// </summary>
        private readonly struct RestPoseLookup
        {
            private readonly SkeletonBone[] _skeleton;

            private RestPoseLookup(SkeletonBone[] skeleton)
            {
                _skeleton = skeleton;
            }

            public static RestPoseLookup From(Animator animator)
            {
                if (animator == null || animator.avatar == null || !animator.avatar.isValid)
                    return new RestPoseLookup(null);
                SkeletonBone[] skeleton = animator.avatar.humanDescription.skeleton;
                return new RestPoseLookup(skeleton != null && skeleton.Length > 0 ? skeleton : null);
            }

            /// <summary>
            ///     The bone's rest local rotation: from the avatar when it lists the bone, and the
            ///     live local rotation otherwise - which on a rig with no humanoid avatar is the
            ///     pose the bones are in at bind time.
            /// </summary>
            public Quaternion LocalRotation(Transform bone)
            {
                if (bone == null) return Quaternion.identity;
                if (_skeleton != null)
                {
                    string boneName = bone.name;
                    for (int i = 0; i < _skeleton.Length; i++)
                        if (_skeleton[i].name == boneName)
                            return _skeleton[i].rotation;
                }

                return bone.localRotation;
            }

            /// <summary>
            ///     The bone's world rotation in the rest pose, carried by the root's CURRENT
            ///     rotation: rest local rotations composed from the root down to the bone.
            /// </summary>
            public Quaternion WorldRotation(Transform bone, Transform root)
            {
                if (bone == null) return Quaternion.identity;
                // Without an avatar there is nothing to compose from but the live pose.
                if (_skeleton == null || root == null) return bone.rotation;

                Quaternion relative = Quaternion.identity;
                Transform t = bone;
                while (t != null && t != root)
                {
                    relative = LocalRotation(t) * relative;
                    t = t.parent;
                }

                // The bone is not under the root at all: nothing to compose against.
                if (t == null) return bone.rotation;
                return root.rotation * relative;
            }
        }

        private Vector3 GetEyeRestForward(Transform eye, Quaternion restLocalRotation, Vector3 localForward)
        {
            if (_hasAxisCalibration && eye != null)
            {
                Quaternion parentRotation = eye.parent != null ? eye.parent.rotation : Quaternion.identity;
                Vector3 forward = (parentRotation * restLocalRotation) * localForward;
                if (forward.sqrMagnitude > 1e-6f) return forward.normalized;
            }

            return CurrentEyeRestForward;
        }
    }
}
