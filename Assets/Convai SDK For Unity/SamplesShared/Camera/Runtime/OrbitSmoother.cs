// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Critically damps orbit state towards target state using per-parameter velocities.
    ///     Separates smoothing logic from camera orchestration for cleaner testing.
    /// </summary>
    public sealed class OrbitSmoother
    {
        private float _yawVelocity;
        private float _pitchVelocity;
        private float _distanceVelocity;
        private Vector2 _panVelocity;

        /// <summary>
        ///     Advances the current state towards the target state using critical damping.
        ///     Returns the smoothed state for this frame.
        /// </summary>
        /// <param name="current">Current (smoothed) orbit state.</param>
        /// <param name="target">Target (unsmoothed) orbit state driven by input.</param>
        /// <param name="deltaTime">Frame time in seconds.</param>
        /// <param name="settings">Camera settings containing smooth times.</param>
        /// <returns>New smoothed state after applying damping.</returns>
        public OrbitState Advance(
            OrbitState current,
            OrbitState target,
            float deltaTime,
            OrbitCameraSettings settings)
        {
            float yaw = DampAngle(
                current.Yaw,
                target.Yaw,
                ref _yawVelocity,
                settings.rotationSmoothTime,
                deltaTime);

            float pitch = DampAngle(
                current.Pitch,
                target.Pitch,
                ref _pitchVelocity,
                settings.rotationSmoothTime,
                deltaTime);

            float distance = Damp(
                current.Distance,
                target.Distance,
                ref _distanceVelocity,
                settings.distanceSmoothTime,
                deltaTime);

            float panX = Damp(
                current.Pan.x,
                target.Pan.x,
                ref _panVelocity.x,
                settings.panSmoothTime,
                deltaTime);

            float panY = Damp(
                current.Pan.y,
                target.Pan.y,
                ref _panVelocity.y,
                settings.panSmoothTime,
                deltaTime);

            return new OrbitState(yaw, pitch, distance, new Vector2(panX, panY));
        }

        /// <summary>
        ///     Resets all velocities to zero. Call when snapping to a target pose
        ///     or resetting the camera to avoid residual velocity from previous motion.
        /// </summary>
        public void Reset()
        {
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
            _distanceVelocity = 0f;
            _panVelocity = Vector2.zero;
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

        private static float DampAngle(
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

            return Mathf.SmoothDampAngle(current, target, ref velocity, smoothTime, Mathf.Infinity, deltaTime);
        }
    }
}
