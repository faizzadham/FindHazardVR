// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Abstraction for anything that can supply a world-space pivot for
    ///     <see cref="ConvaiOrbitCamera" />. Implementations decide how the point is produced
    ///     (a single <see cref="Transform" />, a weighted blend of transforms, a collider
    ///     center, or an animated virtual point).
    /// </summary>
    /// <remarks>
    ///     Implementations must be allocation-free on the hot path. <see cref="TryGetWorldPosition" />
    ///     is queried every <c>LateUpdate</c>.
    /// </remarks>
    public interface IOrbitTarget
    {
        /// <summary>
        ///     Attempts to resolve the current world-space pivot.
        /// </summary>
        /// <param name="position">The resolved pivot in world space.</param>
        /// <returns>
        ///     <c>true</c> if a valid position was produced this frame; <c>false</c> to signal
        ///     that the camera should hold its last known pose.
        /// </returns>
        bool TryGetWorldPosition(out Vector3 position);
    }
}
