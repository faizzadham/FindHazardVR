// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

#if URP_10_0_OR_NEWER
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     URP (Universal Render Pipeline) Volume adapter for depth-of-field.
    ///     <para>
    ///         Uses UnityEngine.Rendering.Universal.DepthOfField with Bokeh mode for
    ///         high-quality background blur. Requires Volume component on camera.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Compiled only when URP 10.0+ package installed (via conditional compilation).
    ///     Falls back to <see cref="OrbitBuiltinVolumeAdapter" /> if URP not present.
    /// </remarks>
    public sealed class OrbitUrpVolumeAdapter : IOrbitVolumeAdapter
    {
        /// <inheritdoc />
        public bool IsSupported => true;

        /// <inheritdoc />
        public bool TryApplyDepthOfField(
            UnityEngine.Camera camera,
            float aperture,
            float focusDistance)
        {
            if (camera == null)
                return false;

            // Auto-setup: Create Volume + Profile if missing
            Volume volume = camera.GetComponent<Volume>();
            if (volume == null)
            {
                volume = camera.gameObject.AddComponent<Volume>();
                volume.isGlobal = false;
                volume.priority = 200f;
                volume.weight = 1f;

                // Add collider for local volume
                SphereCollider collider = camera.gameObject.AddComponent<SphereCollider>();
                collider.isTrigger = true;
                collider.radius = 0.3f;
            }

            if (volume.profile == null)
            {
                volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
                volume.profile.name = "ConvaiOrbitCamera_AutoDOF";
            }

            // Get or add DepthOfField override
            if (!volume.profile.TryGet(out DepthOfField dof))
            {
                dof = volume.profile.Add<DepthOfField>(true);
            }

            if (dof == null)
                return false;

            // Enable and configure for physical camera mode
            dof.active = true;

            // Use Bokeh mode for best quality (Gaussian is faster but lower quality)
            dof.mode.overrideState = true;
            dof.mode.value = DepthOfFieldMode.Bokeh;

            // Set aperture (f-stop) - controls DOF intensity
            dof.aperture.overrideState = true;
            dof.aperture.value = Mathf.Max(0.1f, aperture);

            // Set focus distance
            dof.focusDistance.overrideState = true;
            dof.focusDistance.value = Mathf.Max(0.1f, focusDistance);

            return true;
        }

        /// <inheritdoc />
        public bool TryApplyExposure(
            UnityEngine.Camera camera,
            float exposureCompensation)
        {
            if (camera == null)
                return false;

            Volume volume = camera.GetComponent<Volume>();
            if (volume == null || volume.profile == null)
                return false;

            // Get or add ColorAdjustments override
            if (!volume.profile.TryGet(out ColorAdjustments colorAdj))
            {
                colorAdj = volume.profile.Add<ColorAdjustments>(true);
            }

            if (colorAdj == null)
                return false;

            colorAdj.active = true;
            colorAdj.postExposure.overrideState = true;
            colorAdj.postExposure.value = exposureCompensation;

            return true;
        }
    }
}
#endif
