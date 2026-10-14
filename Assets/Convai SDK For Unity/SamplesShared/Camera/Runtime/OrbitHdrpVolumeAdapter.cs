// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

#if HDRP_10_0_OR_NEWER
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     HDRP (High Definition Render Pipeline) Volume adapter for depth-of-field.
    ///     <para>
    ///         Uses UnityEngine.Rendering.HighDefinition.DepthOfField with PhysicalCamera mode,
    ///         matching Showcase HDRP implementation pattern. Requires Volume component on camera.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Compiled only when HDRP 10.0+ package installed (via conditional compilation).
    ///     HDRP uses physical camera properties more directly than URP, requiring
    ///     aperture set via Camera.aperture rather than Volume override.
    /// </remarks>
    public sealed class OrbitHdrpVolumeAdapter : IOrbitVolumeAdapter
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

            // Enable and configure for physical camera mode (Showcase pattern)
            dof.active = true;

            dof.focusMode.overrideState = true;
            dof.focusMode.value = DepthOfFieldMode.UsePhysicalCamera;

            dof.focusDistanceMode.overrideState = true;
            dof.focusDistanceMode.value = FocusDistanceMode.Camera;

            // HDRP uses physical camera properties
            // Aperture set via camera.usePhysicalProperties and camera.aperture
            if (!camera.usePhysicalProperties)
            {
                camera.usePhysicalProperties = true;
            }

            camera.aperture = Mathf.Max(0.1f, aperture);

            // Set focus distance via Volume
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

            // Get or add Exposure override (HDRP-specific)
            if (!volume.profile.TryGet(out Exposure exposure))
            {
                exposure = volume.profile.Add<Exposure>(true);
            }

            if (exposure == null)
                return false;

            exposure.active = true;
            exposure.mode.overrideState = true;
            exposure.mode.value = ExposureMode.Fixed;

            exposure.fixedExposure.overrideState = true;
            exposure.fixedExposure.value = exposureCompensation;

            return true;
        }
    }
}
#endif
