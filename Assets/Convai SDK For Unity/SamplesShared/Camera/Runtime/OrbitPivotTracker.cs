// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Tracks and smooths the orbit pivot point with teleport detection.
    ///     Manages fallback logic when the target is null or unavailable.
    /// </summary>
    public sealed class OrbitPivotTracker
    {
        private Vector3 _lastPivot;
        private bool _hasLastPivot;
        private Vector3 _smoothedPivot;
        private Vector3 _pivotVelocity;
        private bool _hasSmoothedPivot;

        /// <summary>
        ///     Attempts to resolve the current pivot point from the target or orbit target.
        ///     Falls back to the last known pivot if the target is unavailable.
        /// </summary>
        /// <param name="target">Transform target (may be null).</param>
        /// <param name="orbitTarget">Custom orbit target (may be null).</param>
        /// <param name="builtInTarget">Built-in transform target wrapper.</param>
        /// <param name="pivot">Resolved pivot in world space.</param>
        /// <returns>True if a pivot was resolved, false otherwise.</returns>
        public bool TryResolvePivot(
            Transform target,
            IOrbitTarget orbitTarget,
            TransformOrbitTarget builtInTarget,
            out Vector3 pivot)
        {
            // Prefer explicit IOrbitTarget; otherwise use built-in transform target. Do not require
            // a serialized Transform when a custom orbit target supplies the pivot (parity with legacy TryResolvePivot).
            IOrbitTarget activeTarget = orbitTarget ?? builtInTarget;

            if (activeTarget == null)
            {
                if (_hasLastPivot)
                {
                    pivot = _lastPivot;
                    return true;
                }

                pivot = default;
                return false;
            }

            if (!activeTarget.TryGetWorldPosition(out pivot))
            {
                if (_hasLastPivot)
                {
                    pivot = _lastPivot;
                    return true;
                }

                return false;
            }

            _lastPivot = pivot;
            _hasLastPivot = true;
            return true;
        }

        /// <summary>
        ///     Advances the smoothed pivot towards the target pivot with teleport detection.
        ///     Snaps instantly if the pivot moves beyond the teleport threshold.
        /// </summary>
        /// <param name="pivot">Target pivot from the current frame.</param>
        /// <param name="deltaTime">Frame time in seconds.</param>
        /// <param name="settings">Camera settings containing smooth time and teleport threshold.</param>
        /// <returns>Smoothed pivot for this frame.</returns>
        public Vector3 AdvancePivot(Vector3 pivot, float deltaTime, OrbitCameraSettings settings)
        {
            if (!_hasSmoothedPivot)
            {
                _smoothedPivot = pivot;
                _pivotVelocity = Vector3.zero;
                _hasSmoothedPivot = true;
                return _smoothedPivot;
            }

            float teleportThreshold = settings.targetTeleportThreshold;
            if (teleportThreshold > 0f &&
                (_smoothedPivot - pivot).sqrMagnitude > teleportThreshold * teleportThreshold)
            {
                _smoothedPivot = pivot;
                _pivotVelocity = Vector3.zero;
                return _smoothedPivot;
            }

            _smoothedPivot = Damp(
                _smoothedPivot,
                pivot,
                ref _pivotVelocity,
                settings.targetSmoothTime,
                deltaTime);

            return _smoothedPivot;
        }

        /// <summary>
        ///     Resets pivot tracking state. Call when snapping or changing targets.
        /// </summary>
        public void Reset()
        {
            _hasLastPivot = false;
            _hasSmoothedPivot = false;
            _pivotVelocity = Vector3.zero;
        }

        /// <summary>
        ///     Snaps the smoothed pivot to a specific world position. Used during snap-to-target.
        /// </summary>
        public void SnapTo(Vector3 pivot)
        {
            _smoothedPivot = pivot;
            _lastPivot = pivot;
            _pivotVelocity = Vector3.zero;
            _hasSmoothedPivot = true;
            _hasLastPivot = true;
        }

        private static Vector3 Damp(
            Vector3 current,
            Vector3 target,
            ref Vector3 velocity,
            float smoothTime,
            float deltaTime)
        {
            if (smoothTime <= 0f || deltaTime <= 0f)
            {
                velocity = Vector3.zero;
                return target;
            }

            return Vector3.SmoothDamp(current, target, ref velocity, smoothTime, Mathf.Infinity, deltaTime);
        }
    }
}
