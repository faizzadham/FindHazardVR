// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Processes raw input deltas (mouse motion, scroll wheel) into orbit state changes.
    ///     Stateless pure logic for easy testing and reuse.
    /// </summary>
    public sealed class OrbitInputAccumulator
    {
        /// <summary>
        ///     Accumulates orbit rotation (right-mouse drag) into the target state.
        /// </summary>
        /// <param name="current">Current target state.</param>
        /// <param name="mouseDelta">Mouse delta in approximate pixels (X = horizontal, Y = vertical).</param>
        /// <param name="settings">Camera settings containing sensitivity and invert-Y flag.</param>
        /// <returns>New target state with yaw and pitch adjusted.</returns>
        public OrbitState AccumulateOrbit(
            OrbitState current,
            Vector2 mouseDelta,
            OrbitCameraSettings settings)
        {
            float sensitivity = settings.orbitSensitivity;
            float newYaw = current.Yaw + mouseDelta.x * sensitivity;
            float newPitch = current.Pitch + (settings.invertY ? mouseDelta.y : -mouseDelta.y) * sensitivity;
            return current.WithYaw(newYaw).WithPitch(newPitch);
        }

        /// <summary>
        ///     Accumulates pan offset (middle-mouse drag) into the target state.
        ///     Pan is scaled by current distance so screen-space motion feels consistent
        ///     regardless of zoom level (Maya / Blender convention).
        /// </summary>
        /// <param name="current">Current target state.</param>
        /// <param name="mouseDelta">Mouse delta in approximate pixels.</param>
        /// <param name="currentDistance">Current orbit distance for distance-aware scaling.</param>
        /// <param name="settings">Camera settings containing pan sensitivity and distance constraints.</param>
        /// <returns>New target state with pan offset adjusted.</returns>
        public OrbitState AccumulatePan(
            OrbitState current,
            Vector2 mouseDelta,
            float currentDistance,
            OrbitCameraSettings settings)
        {
            float panScale = settings.panSensitivity * Mathf.Max(settings.distanceMin, currentDistance);
            Vector2 newPan = current.Pan;
            newPan.x -= mouseDelta.x * panScale;
            newPan.y -= mouseDelta.y * panScale;
            return current.WithPan(newPan);
        }

        /// <summary>
        ///     Accumulates zoom (scroll wheel) into the target state.
        ///     Uses a distance-aware step curve: scroll moves less when close (fine control)
        ///     and more when far (rapid traversal).
        /// </summary>
        /// <param name="current">Current target state.</param>
        /// <param name="zoomDelta">Scroll wheel delta in approximate notches (~1.0 per click).</param>
        /// <param name="settings">Camera settings containing zoom sensitivity and step curve.</param>
        /// <returns>New target state with distance adjusted.</returns>
        public OrbitState AccumulateZoom(
            OrbitState current,
            float zoomDelta,
            OrbitCameraSettings settings)
        {
            // Distance-aware step: a scroll notch moves a short distance when the camera is
            // close to the subject (fine focus) and a longer distance when far away (rapid
            // traversal of open space).
            float range = Mathf.Max(1e-4f, settings.distanceMax - settings.distanceMin);
            float zoomT = Mathf.Clamp01((current.Distance - settings.distanceMin) / range);
            float step = Mathf.Lerp(settings.zoomStepNear, settings.zoomStepFar, zoomT) * settings.zoomSensitivity;
            float newDistance = current.Distance - zoomDelta * step;
            return current.WithDistance(newDistance);
        }

        /// <summary>
        ///     Clamps the target state to respect pitch, distance, and pan limits.
        /// </summary>
        /// <param name="state">Unclamped target state.</param>
        /// <param name="settings">Camera settings containing min/max constraints.</param>
        /// <returns>Clamped state suitable for smoothing.</returns>
        public OrbitState ClampTargetState(OrbitState state, OrbitCameraSettings settings)
        {
            float clampedPitch = Mathf.Clamp(state.Pitch, settings.pitchMin, settings.pitchMax);
            float clampedDistance = Mathf.Clamp(state.Distance, settings.distanceMin, settings.distanceMax);
            Vector2 clampedPan = new Vector2(
                Mathf.Clamp(state.Pan.x, -settings.panClampX, settings.panClampX),
                Mathf.Clamp(state.Pan.y, -settings.panClampY, settings.panClampY));

            return new OrbitState(state.Yaw, clampedPitch, clampedDistance, clampedPan);
        }

        /// <summary>
        ///     Clamps the smoothed state to respect pitch, distance, and pan limits.
        ///     Typically applied after smoothing to ensure collision distance doesn't exceed bounds.
        /// </summary>
        /// <param name="state">Unclamped smoothed state.</param>
        /// <param name="settings">Camera settings containing min/max constraints.</param>
        /// <param name="collisionDistance">Optional collision-adjusted distance to clamp separately.</param>
        /// <returns>Clamped state for final pose composition.</returns>
        public OrbitState ClampOrbitState(
            OrbitState state,
            OrbitCameraSettings settings,
            float collisionDistance = float.MaxValue)
        {
            float clampedPitch = Mathf.Clamp(state.Pitch, settings.pitchMin, settings.pitchMax);
            float clampedDistance = Mathf.Clamp(state.Distance, settings.distanceMin, settings.distanceMax);
            Vector2 clampedPan = new Vector2(
                Mathf.Clamp(state.Pan.x, -settings.panClampX, settings.panClampX),
                Mathf.Clamp(state.Pan.y, -settings.panClampY, settings.panClampY));

            if (collisionDistance < float.MaxValue)
            {
                float clampedCollisionDistance = Mathf.Clamp(
                    collisionDistance,
                    settings.collisionMinimumDistance,
                    settings.distanceMax);
                clampedDistance = Mathf.Min(clampedDistance, clampedCollisionDistance);
            }

            return new OrbitState(state.Yaw, clampedPitch, clampedDistance, clampedPan);
        }
    }
}
