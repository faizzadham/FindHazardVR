// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Pure calculation utilities for adaptive depth-of-field.
    ///     <para>
    ///         Computes aperture (f-stop) and focus distance from camera orbit distance using
    ///         smooth interpolation curves. Inspired by film-style distance-based exposure control.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     All methods are static and allocation-free. Designed to be called each frame without
    ///     garbage collection overhead.
    /// </remarks>
    public static class OrbitDofMath
    {
        /// <summary>
        ///     Smallest f-stop this system will ever produce. Faster lenses than f/0.7 do not exist in
        ///     practice, and lower values make post-process depth of field break down visually.
        /// </summary>
        public const float MinimumAperture = 0.7f;

        /// <summary>
        ///     Smallest focus distance (meters) this system will ever produce. Keeps a camera parked on
        ///     the pivot from focusing at zero, which yields an undefined circle of confusion.
        /// </summary>
        public const float MinimumFocusDistance = 0.1f;

        /// <summary>
        ///     Result of adaptive DOF calculation containing aperture and focus distance.
        /// </summary>
        public struct AdaptiveDofResult
        {
            /// <summary>Calculated aperture (f-stop). Lower values = shallower depth of field.</summary>
            public float Aperture;

            /// <summary>Calculated focus distance in meters from camera to in-focus plane.</summary>
            public float FocusDistance;
        }

        /// <summary>
        ///     Calculates adaptive aperture and focus distance from camera orbit distance.
        /// </summary>
        /// <param name="cameraDistance">Current camera orbit distance in meters.</param>
        /// <param name="closeDistance">Distance threshold for close-up behavior (e.g., 0.78m).</param>
        /// <param name="farDistance">Distance threshold for wide-angle behavior (e.g., 2.5m).</param>
        /// <param name="closeAperture">Aperture applied at (or nearer than) <paramref name="closeDistance" />.</param>
        /// <param name="farAperture">Aperture applied at (or beyond) <paramref name="farDistance" />.</param>
        /// <returns>Calculated aperture and focus distance.</returns>
        /// <remarks>
        ///     The mapping is literal: <paramref name="closeAperture" /> is what a camera sitting at
        ///     <paramref name="closeDistance" /> gets, and <paramref name="farAperture" /> is what it gets at
        ///     <paramref name="farDistance" />. Which of the two is the narrower f-stop is a look decision that
        ///     belongs to the caller's settings, not to this function — see <see cref="OrbitDofSettings.Default" />
        ///     for the shipped cinematic choice (narrow at close so the whole face stays sharp, wide at far for
        ///     background separation).
        /// </remarks>
        /// <example>
        ///     <code>
        ///     AdaptiveDofResult dof = OrbitDofMath.Evaluate(
        ///         cameraDistance: 1.64f,
        ///         closeDistance: 0.78f,
        ///         farDistance: 2.5f,
        ///         closeAperture: 8.0f,
        ///         farAperture: 2.8f);
        ///     // dof.Aperture ≈ 5.4f (mid-range, eased)
        ///     // dof.FocusDistance = 1.64f (matches camera distance)
        ///     </code>
        /// </example>
        public static AdaptiveDofResult Evaluate(
            float cameraDistance,
            float closeDistance,
            float farDistance,
            float closeAperture,
            float farAperture)
        {
            // Sanitize inputs to prevent division by zero and ensure valid ranges
            float sanitizedCloseDistance = Mathf.Max(MinimumFocusDistance, closeDistance);
            float sanitizedFarDistance = Mathf.Max(sanitizedCloseDistance + 0.01f, farDistance);
            float sanitizedCameraDistance = Mathf.Max(MinimumFocusDistance, cameraDistance);

            // Normalize camera distance to [0, 1] range between close and far thresholds
            float t = Mathf.InverseLerp(sanitizedCloseDistance, sanitizedFarDistance, sanitizedCameraDistance);

            // Apply smooth easing for cinematic feel (S-curve)
            float smoothT = SmoothStep01(t);

            // Interpolate aperture between the two authored endpoints. Endpoints are floored at the
            // physical minimum f/0.7 so a mis-authored value can never produce a negative f-stop.
            float aperture = Mathf.Lerp(
                Mathf.Max(MinimumAperture, closeAperture),
                Mathf.Max(MinimumAperture, farAperture),
                smoothT);

            // Focus distance matches camera distance to keep pivot sharp
            float focusDistance = sanitizedCameraDistance;

            return new AdaptiveDofResult
            {
                Aperture = Mathf.Max(MinimumAperture, aperture),
                FocusDistance = focusDistance
            };
        }

        /// <summary>
        ///     Smooth step interpolation (cubic Hermite) for value in [0, 1].
        ///     Produces S-curve easing: slow at start/end, fast in middle.
        /// </summary>
        /// <param name="value">Input value, will be clamped to [0, 1].</param>
        /// <returns>Smoothed value in [0, 1] using formula: t² * (3 - 2t).</returns>
        /// <remarks>
        ///     Common in graphics for smooth transitions. Equivalent to GLSL smoothstep().
        /// </remarks>
        private static float SmoothStep01(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }
    }
}
