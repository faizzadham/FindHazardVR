// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Pure math helpers consumed by <see cref="ConvaiOrbitCamera" />. Stateless and
    ///     allocation-free to keep per-frame camera updates deterministic, cheap, and trivially
    ///     testable in EditMode.
    /// </summary>
    public static class OrbitMath
    {
        /// <summary>
        ///     Composes a yaw/pitch rotation and camera-relative pan into the framed pivot
        ///     the camera should look at.
        /// </summary>
        public static void ComposeFrame(
            Vector3 target,
            Vector3 targetOffset,
            float yawDegrees,
            float pitchDegrees,
            Vector2 panOffset,
            out Vector3 pivot,
            out Quaternion rotation)
        {
            rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
            Vector3 cameraRight = rotation * Vector3.right;
            Vector3 cameraUp = rotation * Vector3.up;
            pivot = target + targetOffset + (cameraRight * panOffset.x) + (cameraUp * panOffset.y);
        }

        /// <summary>
        ///     Places the camera behind a framed pivot using the supplied orbit rotation
        ///     and distance.
        /// </summary>
        public static Vector3 ComposePosition(Vector3 pivot, Quaternion rotation, float distance)
        {
            return pivot + (rotation * new Vector3(0f, 0f, -distance));
        }

        /// <summary>
        ///     Composes a yaw/pitch rotation, a pivot offset, and a camera-relative 2D pan
        ///     offset into a desired world-space camera position and rotation.
        /// </summary>
        /// <remarks>
        ///     The pan offset is expressed in the camera's local right/up basis (as derived
        ///     from <paramref name="yawDegrees" /> and <paramref name="pitchDegrees" />), so
        ///     a horizontal pan always slides the view sideways relative to the screen,
        ///     regardless of the current orbit angle.
        /// </remarks>
        /// <param name="target">World-space position the camera orbits around.</param>
        /// <param name="targetOffset">Offset added to <paramref name="target" /> before panning.</param>
        /// <param name="yawDegrees">Yaw angle in degrees (around world Y).</param>
        /// <param name="pitchDegrees">Pitch angle in degrees.</param>
        /// <param name="distance">Distance from the resulting pivot in meters.</param>
        /// <param name="panOffset">Camera-relative pan offset in meters (X = right, Y = up).</param>
        /// <param name="position">Resolved world-space camera position.</param>
        /// <param name="rotation">Resolved world-space camera rotation.</param>
        public static void ComposePose(
            Vector3 target,
            Vector3 targetOffset,
            float yawDegrees,
            float pitchDegrees,
            float distance,
            Vector2 panOffset,
            out Vector3 position,
            out Quaternion rotation)
        {
            ComposeFrame(
                target,
                targetOffset,
                yawDegrees,
                pitchDegrees,
                panOffset,
                out Vector3 pivot,
                out rotation);
            position = ComposePosition(pivot, rotation, distance);
        }
    }
}
