// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Built-in render pipeline adapter with limited/no DOF support.
    ///     <para>
    ///         Built-in pipeline lacks Volume system (URP/HDRP feature). Post Processing Stack v2
    ///         can be used manually, but automatic integration not provided to avoid dependency.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     This adapter logs warnings when DOF attempted on Built-in pipeline and returns false.
    ///     Users should migrate to URP for full DOF support, or manually integrate Post Processing v2.
    /// </remarks>
    public sealed class OrbitBuiltinVolumeAdapter : IOrbitVolumeAdapter
    {
        private bool _warningLogged;

        /// <inheritdoc />
        public bool IsSupported => false;

        /// <inheritdoc />
        public bool TryApplyDepthOfField(
            UnityEngine.Camera camera,
            float aperture,
            float focusDistance)
        {
            if (!_warningLogged)
            {
                Debug.LogWarning(
                    "[ConvaiOrbitCamera] Depth-of-field not supported on Built-in render pipeline. " +
                    "Upgrade to URP or HDRP for adaptive DOF. Alternatively, manually integrate Post Processing Stack v2.",
                    camera);
                _warningLogged = true;
            }

            return false;
        }

        /// <inheritdoc />
        public bool TryApplyExposure(
            UnityEngine.Camera camera,
            float exposureCompensation)
        {
            // No-op, same limitation as DOF
            return false;
        }
    }
}
