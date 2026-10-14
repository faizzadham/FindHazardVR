// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Abstraction for render pipeline-specific Volume/Post-Processing systems.
    ///     <para>
    ///         Allows <see cref="OrbitDepthOfFieldController" /> to apply DOF settings
    ///         across URP, HDRP, and Built-in pipelines without coupling to concrete APIs.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Implementations use conditional compilation (#if URP_10_0_OR_NEWER) to avoid
    ///     dependencies on missing packages. Factory pattern (<see cref="OrbitVolumeAdapterFactory" />)
    ///     auto-detects active pipeline at runtime.
    /// </remarks>
    public interface IOrbitVolumeAdapter
    {
        /// <summary>
        ///     True if this adapter supports the currently active render pipeline.
        ///     <para>
        ///         Use to check compatibility before calling TryApply* methods.
        ///     </para>
        /// </summary>
        bool IsSupported { get; }

        /// <summary>
        ///     Attempts to apply depth-of-field settings to the camera's Volume component.
        /// </summary>
        /// <param name="camera">Camera to apply DOF to. Must have Volume component (URP/HDRP).</param>
        /// <param name="aperture">Aperture (f-stop) value. Lower = shallower DOF.</param>
        /// <param name="focusDistance">Focus distance in meters from camera.</param>
        /// <returns>True if successfully applied, false if Volume missing or disabled.</returns>
        /// <example>
        ///     <code>
        ///     if (adapter.TryApplyDepthOfField(camera, 2.8f, 1.5f))
        ///         Debug.Log("DOF applied: f/2.8 at 1.5m");
        ///     else
        ///         Debug.LogWarning("No Volume component found");
        ///     </code>
        /// </example>
        bool TryApplyDepthOfField(
            UnityEngine.Camera camera,
            float aperture,
            float focusDistance);

        /// <summary>
        ///     Optionally applies exposure compensation to the camera's Volume component.
        ///     <para>
        ///         Not all adapters support this (Built-in has limited post-processing).
        ///     </para>
        /// </summary>
        /// <param name="camera">Camera to apply exposure to.</param>
        /// <param name="exposureCompensation">Exposure offset in EV (stops). Positive = brighter.</param>
        /// <returns>True if successfully applied, false if not supported or Volume missing.</returns>
        bool TryApplyExposure(
            UnityEngine.Camera camera,
            float exposureCompensation);
    }
}
