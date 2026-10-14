// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

#if ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine;

namespace Convai.Sample.Camera.Input
{
    /// <summary>
    ///     <see cref="ICameraInputReader" /> backed by <see cref="UnityEngine.Input" />
    ///     (the legacy Input Manager). Available when the project has the legacy backend
    ///     enabled in Player Settings &gt; Active Input Handling (Old or Both).
    /// </summary>
    /// <remarks>
    ///     The legacy <c>Mouse X</c> / <c>Mouse Y</c> axes return values scaled by the user's
    ///     mouse sensitivity (default 0.1). The reader un-scales them with the
    ///     <see cref="LegacyAxisToPixelScale" /> factor so behaviour matches the Input System
    ///     reader for the same physical mouse motion.
    /// </remarks>
    public sealed class LegacyCameraInputReader : ICameraInputReader
    {
        // Unity's default "Mouse X" / "Mouse Y" sensitivity is 0.1, meaning GetAxis returns
        // roughly pixelDelta * 0.1. Multiplying by 10 yields approximate per-frame pixel deltas,
        // which keeps orbit sensitivity tuning consistent across input backends.
        private const float LegacyAxisToPixelScale = 10f;

        private const int OrbitMouseButton = 1;
        private const int PanMouseButton = 2;

        private Vector2 _orbitDelta;
        private float _zoomDelta;

        /// <inheritdoc />
        public void Sample()
        {
            _orbitDelta.x = UnityEngine.Input.GetAxisRaw("Mouse X") * LegacyAxisToPixelScale;
            _orbitDelta.y = UnityEngine.Input.GetAxisRaw("Mouse Y") * LegacyAxisToPixelScale;

            // mouseScrollDelta is normalized to ~1.0 per notch on desktop platforms and
            // needs no further scaling.
            _zoomDelta = UnityEngine.Input.mouseScrollDelta.y;
        }

        /// <inheritdoc />
        public Vector2 ReadOrbitDelta() => _orbitDelta;

        /// <inheritdoc />
        public float ReadZoomDelta() => _zoomDelta;

        /// <inheritdoc />
        public bool IsOrbitActive() => UnityEngine.Input.GetMouseButton(OrbitMouseButton);

        /// <inheritdoc />
        public bool IsPanActive() => UnityEngine.Input.GetMouseButton(PanMouseButton);

        /// <inheritdoc />
        public bool IsPointerOverUI() => CameraInputGuards.IsPointerOverUI();

        /// <inheritdoc />
        public bool IsTextInputFocused() => CameraInputGuards.IsTextInputFocused();
    }
}
#endif
