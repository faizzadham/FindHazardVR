// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Composes final camera position and rotation from orbit state and drift offsets.
    ///     Applies screen-space composition offsets for portrait-style framing.
    /// </summary>
    public sealed class OrbitComposer
    {
        /// <summary>
        ///     Composes the final camera pose from orbit state, drift, and settings.
        /// </summary>
        /// <param name="pivot">Smoothed pivot point in world space.</param>
        /// <param name="state">Smoothed orbit state.</param>
        /// <param name="settings">Camera settings containing offsets and constraints.</param>
        /// <param name="driftOffsets">Idle drift offsets (X = yaw, Y = pitch, Z = distance).</param>
        /// <param name="lensController">Lens controller for FOV/aspect calculations.</param>
        /// <param name="camera">Unity camera for aspect ratio.</param>
        /// <param name="framedPivot">Output: pivot after applying target offset and pan.</param>
        /// <param name="rotation">Output: camera rotation including screen composition offset.</param>
        public void Compose(
            Vector3 pivot,
            OrbitState state,
            OrbitCameraSettings settings,
            Vector3 driftOffsets,
            OrbitLensController lensController,
            UnityEngine.Camera camera,
            out Vector3 framedPivot,
            out Quaternion rotation)
        {
            float yaw = state.Yaw + driftOffsets.x;
            float pitch = Mathf.Clamp(
                state.Pitch + driftOffsets.y,
                settings.pitchMin,
                settings.pitchMax);

            OrbitMath.ComposeFrame(
                pivot,
                settings.targetOffset,
                yaw,
                pitch,
                state.Pan,
                out framedPivot,
                out Quaternion baseRotation);

            rotation = ApplyCompositionOffset(baseRotation, settings, lensController, camera);
        }

        /// <summary>
        ///     Applies screen-space composition offset for portrait-style framing.
        ///     Shifts the view without moving the pivot.
        /// </summary>
        private Quaternion ApplyCompositionOffset(
            Quaternion baseRotation,
            OrbitCameraSettings settings,
            OrbitLensController lensController,
            UnityEngine.Camera camera)
        {
            Vector2 screenOffset = settings.screenComposition;
            if (screenOffset.sqrMagnitude <= 0.000001f)
                return baseRotation;

            float verticalFov = lensController.ResolveVerticalFieldOfView(camera, settings);
            float horizontalFov = UnityEngine.Camera.VerticalToHorizontalFieldOfView(
                verticalFov,
                lensController.ResolveAspect(camera));

            float yawOffset = -Mathf.Atan(Mathf.Tan(horizontalFov * Mathf.Deg2Rad * 0.5f) * screenOffset.x) * Mathf.Rad2Deg;
            float pitchOffset = Mathf.Atan(Mathf.Tan(verticalFov * Mathf.Deg2Rad * 0.5f) * screenOffset.y) * Mathf.Rad2Deg;

            return baseRotation * Quaternion.Euler(pitchOffset, yawOffset, 0f);
        }
    }
}
