// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Controls how a transform-backed orbit target resolves its pivot point.
    /// </summary>
    public enum OrbitTargetTrackingMode
    {
        /// <summary>
        ///     Captures the target's current world point relative to an anchor transform and
        ///     follows that anchor afterwards. This keeps the camera stable when the assigned
        ///     target is an animated bone while still following character locomotion.
        /// </summary>
        StablePivot = 0,

        /// <summary>
        ///     Uses the target transform's current world position every frame. This is the
        ///     legacy behavior and intentionally includes animation-driven target motion.
        /// </summary>
        TransformPosition = 1,

        /// <summary>
        ///     Captures the target's current world point once and keeps using that point until
        ///     the pivot is captured again.
        /// </summary>
        FixedWorldPoint = 2
    }

    /// <summary>
    ///     Default <see cref="IOrbitTarget" /> implementation backed by a single
    ///     <see cref="Transform" />. The wrapped transform, tracking mode, and anchor can be
    ///     swapped at runtime without allocating.
    /// </summary>
    public sealed class TransformOrbitTarget : IOrbitTarget
    {
        private Transform _transform;
        private Transform _anchor;
        private Transform _autoAnchor;
        private OrbitTargetTrackingMode _trackingMode;
        private Vector3 _capturedAnchorLocalPivot;
        private Vector3 _capturedWorldPivot;
        private bool _hasCapturedPivot;

        /// <summary>Creates a new <see cref="TransformOrbitTarget" /> around the supplied transform.</summary>
        /// <param name="transform">Transform that supplies the world-space pivot.</param>
        /// <param name="trackingMode">How the target transform should resolve its pivot.</param>
        /// <param name="anchor">
        ///     Optional stable reference frame used by <see cref="OrbitTargetTrackingMode.StablePivot" />.
        ///     If omitted, the target's parent Animator transform is used when available, otherwise
        ///     the target root is used.
        /// </param>
        public TransformOrbitTarget(
            Transform transform,
            OrbitTargetTrackingMode trackingMode = OrbitTargetTrackingMode.StablePivot,
            Transform anchor = null)
        {
            _transform = transform;
            _trackingMode = trackingMode;
            _anchor = anchor;
            CapturePivot();
        }

        /// <summary>
        ///     The wrapped transform. May be assigned <c>null</c> to temporarily disable the target.
        ///     Assigning a new transform captures the current pivot for stabilized/fixed modes.
        /// </summary>
        public Transform Transform
        {
            get => _transform;
            set
            {
                if (_transform == value) return;

                _transform = value;
                _autoAnchor = null;
                CapturePivot();
            }
        }

        /// <summary>How this target resolves its world-space pivot.</summary>
        public OrbitTargetTrackingMode TrackingMode
        {
            get => _trackingMode;
            set
            {
                if (_trackingMode == value) return;

                _trackingMode = value;
                CapturePivot();
            }
        }

        /// <summary>
        ///     Stable reference frame used by <see cref="OrbitTargetTrackingMode.StablePivot" />.
        ///     Leave empty to auto-resolve from the target's parent Animator or root transform.
        /// </summary>
        public Transform Anchor
        {
            get => _anchor;
            set
            {
                if (_anchor == value) return;

                _anchor = value;
                _autoAnchor = null;
                CapturePivot();
            }
        }

        /// <summary>
        ///     Captures the target's current point as the pivot for stabilized/fixed tracking.
        ///     Call after retargeting, teleporting, or changing character scale/hierarchy.
        /// </summary>
        public void CapturePivot()
        {
            _hasCapturedPivot = false;

            if (_transform == null)
                return;

            _autoAnchor = null;
            _capturedWorldPivot = _transform.position;
            _capturedAnchorLocalPivot = Vector3.zero;
            _hasCapturedPivot = true;

            Transform resolvedAnchor = ResolveAnchor();
            if (resolvedAnchor != null)
                _capturedAnchorLocalPivot = resolvedAnchor.InverseTransformPoint(_capturedWorldPivot);
        }

        /// <inheritdoc />
        public bool TryGetWorldPosition(out Vector3 position)
        {
            if (_transform == null)
            {
                position = default;
                return false;
            }

            switch (_trackingMode)
            {
                case OrbitTargetTrackingMode.TransformPosition:
                    position = _transform.position;
                    return true;

                case OrbitTargetTrackingMode.FixedWorldPoint:
                    if (_hasCapturedPivot)
                    {
                        position = _capturedWorldPivot;
                        return true;
                    }

                    position = _transform.position;
                    return true;

                case OrbitTargetTrackingMode.StablePivot:
                default:
                    if (!_hasCapturedPivot)
                        CapturePivot();

                    Transform resolvedAnchor = ResolveAnchor();
                    if (resolvedAnchor == null || !_hasCapturedPivot)
                    {
                        position = _transform.position;
                        return true;
                    }

                    position = resolvedAnchor.TransformPoint(_capturedAnchorLocalPivot);
                    return true;
            }
        }

        private Transform ResolveAnchor()
        {
            if (_anchor != null)
                return _anchor;

            if (_transform == null)
                return null;

            if (_autoAnchor != null)
                return _autoAnchor;

            Animator animator = _transform.GetComponentInParent<Animator>();
            if (animator != null)
            {
                _autoAnchor = animator.transform;
                return _autoAnchor;
            }

            _autoAnchor = _transform.root != null ? _transform.root : _transform;
            return _autoAnchor;
        }
    }
}
