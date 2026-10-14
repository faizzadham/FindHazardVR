// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

#if ENABLE_INPUT_SYSTEM
using UnityEngine;
using UnityEngine.InputSystem;

namespace Convai.Sample.Camera.Input
{
    /// <summary>
    ///     <see cref="ICameraInputReader" /> backed by the Unity Input System package
    ///     (<see cref="Mouse" />). Available when the new Input System is enabled in
    ///     Player Settings &gt; Active Input Handling (New or Both).
    /// </summary>
    /// <remarks>
    ///     The Input System's <see cref="Mouse.scroll" /> value is platform-dependent; on
    ///     Windows it typically reports ~120 units per notch while macOS trackpads report
    ///     fractional values. The reader normalizes it to ~1.0 per notch so tuning
    ///     <see cref="OrbitCameraSettings.zoomSensitivity" /> feels consistent across
    ///     platforms and input backends.
    /// </remarks>
    public sealed class InputSystemCameraInputReader : ICameraInputReader
    {
        private const float ScrollToNotchScale = 1f / 120f;

        private Vector2 _orbitDelta;
        private float _zoomDelta;

        /// <inheritdoc />
        public void Sample()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                _orbitDelta = Vector2.zero;
                _zoomDelta = 0f;
                return;
            }

            _orbitDelta = mouse.delta.ReadValue();

            float rawScroll = mouse.scroll.ReadValue().y;
            // Windows reports scroll wheel in 120-unit increments, while macOS / Linux
            // trackpads report fractional values. Only normalize large values so smooth
            // trackpad input is preserved.
            _zoomDelta = Mathf.Abs(rawScroll) > 10f ? rawScroll * ScrollToNotchScale : rawScroll;
        }

        /// <inheritdoc />
        public Vector2 ReadOrbitDelta() => _orbitDelta;

        /// <inheritdoc />
        public float ReadZoomDelta() => _zoomDelta;

        /// <inheritdoc />
        public bool IsOrbitActive()
        {
            Mouse mouse = Mouse.current;
            return mouse != null && mouse.rightButton.isPressed;
        }

        /// <inheritdoc />
        public bool IsPanActive()
        {
            Mouse mouse = Mouse.current;
            return mouse != null && mouse.middleButton.isPressed;
        }

        /// <inheritdoc />
        public bool IsPointerOverUI() => CameraInputGuards.IsPointerOverUI();

        /// <inheritdoc />
        public bool IsTextInputFocused() => CameraInputGuards.IsTextInputFocused();
    }
}
#endif
