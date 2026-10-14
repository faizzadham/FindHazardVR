// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Resolves camera collision using sphere-cast and smooths the collision distance
    ///     to avoid jarring snaps when the camera pops out from behind obstacles.
    /// </summary>
    public sealed class OrbitCollisionResolver
    {
        private float _collisionDistance;
        private float _collisionDistanceVelocity;

        /// <summary>
        ///     Resolves the final camera distance after collision checks and smoothing.
        /// </summary>
        /// <param name="framedPivot">Pivot point after applying target offset and pan.</param>
        /// <param name="rotation">Camera rotation (yaw/pitch).</param>
        /// <param name="desiredDistance">Ideal distance before collision.</param>
        /// <param name="deltaTime">Frame time in seconds.</param>
        /// <param name="settings">Camera settings containing collision parameters.</param>
        /// <returns>Final distance clamped and smoothed to avoid obstacles.</returns>
        public float Resolve(
            Vector3 framedPivot,
            Quaternion rotation,
            float desiredDistance,
            float deltaTime,
            OrbitCameraSettings settings)
        {
            float targetDistance = desiredDistance;

            if (settings.collisionEnabled && desiredDistance > 0f)
            {
                Vector3 direction = rotation * Vector3.back;
                if (Physics.SphereCast(
                        framedPivot,
                        settings.collisionRadius,
                        direction,
                        out RaycastHit hit,
                        desiredDistance,
                        settings.collisionLayers,
                        QueryTriggerInteraction.Ignore))
                {
                    targetDistance = Mathf.Clamp(
                        hit.distance - settings.collisionSkin,
                        settings.collisionMinimumDistance,
                        desiredDistance);
                }
            }

            if (!settings.collisionEnabled || deltaTime <= 0f)
            {
                _collisionDistance = targetDistance;
                _collisionDistanceVelocity = 0f;
                return _collisionDistance;
            }

            // Snap instantly when moving closer (obstacle appeared), smooth when moving away.
            float smoothTime = targetDistance < _collisionDistance ? 0f : settings.collisionSmoothTime;
            _collisionDistance = Damp(
                _collisionDistance,
                targetDistance,
                ref _collisionDistanceVelocity,
                smoothTime,
                deltaTime);

            return Mathf.Clamp(
                _collisionDistance,
                settings.collisionMinimumDistance,
                desiredDistance);
        }

        /// <summary>
        ///     Resets collision state to the specified distance. Call when snapping or teleporting.
        /// </summary>
        public void Reset(float initialDistance)
        {
            _collisionDistance = initialDistance;
            _collisionDistanceVelocity = 0f;
        }

        private static float Damp(
            float current,
            float target,
            ref float velocity,
            float smoothTime,
            float deltaTime)
        {
            if (smoothTime <= 0f || deltaTime <= 0f)
            {
                velocity = 0f;
                return target;
            }

            return Mathf.SmoothDamp(current, target, ref velocity, smoothTime, Mathf.Infinity, deltaTime);
        }
    }
}
