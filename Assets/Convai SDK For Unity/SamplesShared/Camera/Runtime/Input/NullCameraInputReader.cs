// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera.Input
{
    /// <summary>
    ///     Safe no-op <see cref="ICameraInputReader" />. Used as the fallback when no
    ///     supported input backend is available, or to temporarily freeze camera input
    ///     without tearing down the controller.
    /// </summary>
    public sealed class NullCameraInputReader : ICameraInputReader
    {
        /// <summary>Shared stateless singleton instance.</summary>
        public static readonly NullCameraInputReader Instance = new NullCameraInputReader();

        private NullCameraInputReader() { }

        /// <inheritdoc />
        public void Sample() { }

        /// <inheritdoc />
        public Vector2 ReadOrbitDelta() => Vector2.zero;

        /// <inheritdoc />
        public float ReadZoomDelta() => 0f;

        /// <inheritdoc />
        public bool IsOrbitActive() => false;

        /// <inheritdoc />
        public bool IsPanActive() => false;

        /// <inheritdoc />
        public bool IsPointerOverUI() => false;

        /// <inheritdoc />
        public bool IsTextInputFocused() => false;
    }
}
