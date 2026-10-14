// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Immutable snapshot of depth-of-field state.
    ///     <para>
    ///         Stores aperture and focus distance values. Designed as a plain value type for
    ///         allocation-free per-frame updates in <see cref="OrbitDepthOfFieldController" />.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Follows the same pattern as <see cref="OrbitState" />: state is copied, not referenced,
    ///     to avoid mutation bugs and ensure deterministic behavior.
    /// </remarks>
    public struct OrbitDofState
    {
        /// <summary>Current or target aperture (f-stop).</summary>
        public float Aperture;

        /// <summary>Current or target focus distance in meters.</summary>
        public float FocusDistance;

        /// <summary>
        ///     Creates a new DOF state with specified aperture and focus distance.
        /// </summary>
        /// <param name="aperture">Aperture value (f-stop), typically in range [0.7, 22].</param>
        /// <param name="focusDistance">Focus distance in meters, must be positive.</param>
        public OrbitDofState(float aperture, float focusDistance)
        {
            Aperture = aperture;
            FocusDistance = focusDistance;
        }
    }
}
