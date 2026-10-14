// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using System;
using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Configuration parameters for adaptive depth-of-field in <see cref="ConvaiOrbitCamera" />.
    ///     <para>
    ///         Defines distance thresholds and aperture ranges that control how DOF adapts to
    ///         camera zoom. Authored as a plain serializable struct for direct Inspector editing.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Call <see cref="Validate" /> after authoring or before use to clamp values to safe ranges.
    /// </remarks>
    [Serializable]
    public struct OrbitDofSettings
    {
        /// <summary>
        ///     Largest f-stop accepted by <see cref="Validate" />. Beyond f/22 the depth-of-field effect is
        ///     indistinguishable from having none at all.
        /// </summary>
        public const float MaximumAperture = 22f;

        [Header("Adaptive Depth of Field")]
        [Tooltip("Distance threshold (meters) for close-up framing. At or below this, the camera uses Close Aperture.")]
        public float closeDistance;

        [Tooltip("Distance threshold (meters) for wide framing. At or beyond this, the camera uses Far Aperture.")]
        public float farDistance;

        [Tooltip("Aperture (f-stop) used at Close Distance. Higher values (e.g., f/8) keep the whole face sharp; lower values (e.g., f/1.4) blur everything but the focus plane.")]
        public float closeAperture;

        [Tooltip("Aperture (f-stop) used at Far Distance. Lower values (e.g., f/2.8) separate the character from the background; higher values keep the scene sharp.")]
        public float farAperture;

        [Header("Smoothing")]
        [Tooltip("Approximate time (seconds) for aperture to settle after zoom changes. Lower = snappier, higher = smoother.")]
        public float apertureSmoothTime;

        [Tooltip("Approximate time (seconds) for focus distance to settle. Typically faster than aperture.")]
        public float focusSmoothTime;

        [Header("Advanced")]
        [Tooltip("Offset added to calculated focus distance. Positive = focus behind pivot, negative = in front.")]
        public float focusBias;

        [Tooltip("Minimum allowed aperture (f-stop). Prevents extreme shallow DOF artifacts.")]
        public float minAperture;

        [Tooltip("Maximum allowed aperture (f-stop). Prevents complete loss of depth separation.")]
        public float maxAperture;

        /// <summary>
        ///     Production-safe defaults for cinematic character focus.
        ///     <para>
        ///         Tuned for face sharpness at all distances. Close-up uses narrow aperture
        ///         (f/8) to keep entire face sharp. Far uses wider aperture (f/2.8) for
        ///         background separation while maintaining subject clarity.
        ///     </para>
        ///     <para>
        ///         Matches Showcase HDRP team spec: aperture 1.2-8.0 range, focus distance
        ///         always equals camera distance for pivot sharpness.
        ///     </para>
        ///     <para>
        ///         Note the aperture ordering: close is the <em>narrower</em> f-stop here. That is a
        ///         deliberate look choice, not the only valid one — swapping the two fields gives the
        ///         conventional "shallow when close" portrait behavior.
        ///     </para>
        /// </summary>
        public static OrbitDofSettings Default => new OrbitDofSettings
        {
            closeDistance = 0.5f,  // Very close to face
            farDistance = 2.5f,    // Max conversation distance
            closeAperture = 8.0f,  // Narrow up close so the entire face stays sharp
            farAperture = 2.8f,    // Wider when pulled back for background separation
            apertureSmoothTime = 0.3f,
            focusSmoothTime = 0.2f,
            focusBias = 0f,
            minAperture = 1.2f,    // Showcase team minimum
            maxAperture = 8.0f     // Showcase team maximum
        };

        /// <summary>
        ///     Normalizes and clamps all fields to safe ranges.
        ///     Call after authoring in Inspector or before supplying to controllers.
        /// </summary>
        public void Validate()
        {
            // Ensure close distance is positive
            closeDistance = Mathf.Max(OrbitDofMath.MinimumFocusDistance, closeDistance);

            // Ensure far distance is greater than close distance
            farDistance = Mathf.Max(closeDistance + 0.01f, farDistance);

            // Clamp apertures to physically plausible range
            closeAperture = Mathf.Clamp(closeAperture, OrbitDofMath.MinimumAperture, MaximumAperture);
            farAperture = Mathf.Clamp(farAperture, OrbitDofMath.MinimumAperture, MaximumAperture);

            // Ensure min/max aperture bounds are valid
            minAperture = Mathf.Max(OrbitDofMath.MinimumAperture, minAperture);
            maxAperture = Mathf.Max(minAperture, maxAperture);
            maxAperture = Mathf.Min(MaximumAperture, maxAperture);

            // Clamp calculated apertures within min/max bounds
            closeAperture = Mathf.Clamp(closeAperture, minAperture, maxAperture);
            farAperture = Mathf.Clamp(farAperture, minAperture, maxAperture);

            // Ensure smooth times are non-negative
            apertureSmoothTime = Mathf.Max(0f, apertureSmoothTime);
            focusSmoothTime = Mathf.Max(0f, focusSmoothTime);

            // Focus bias is unrestricted (can be positive or negative)
        }
    }
}
