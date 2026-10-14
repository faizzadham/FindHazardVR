// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera.Input
{
    /// <summary>
    ///     Convenience <see cref="ICameraInputReader" /> that resolves the best available
    ///     input backend at construction time. Resolution order (highest priority first):
    ///     <list type="number">
    ///         <item><description>Input System package (<c>InputSystemCameraInputReader</c>).</description></item>
    ///         <item><description>Legacy Input Manager (<c>LegacyCameraInputReader</c>).</description></item>
    ///         <item><description><see cref="NullCameraInputReader" /> when neither backend is available.</description></item>
    ///     </list>
    ///     Users can always replace <see cref="ConvaiOrbitCamera.InputReader" /> directly with
    ///     a custom implementation (for example, one driven by an <c>InputActionAsset</c> or
    ///     a remote procedural camera director).
    /// </summary>
    public sealed class DefaultCameraInputReader : ICameraInputReader
    {
        private readonly ICameraInputReader _inner;

        /// <summary>
        ///     Creates a new <see cref="DefaultCameraInputReader" /> and resolves the inner
        ///     backend. The resolved backend is fixed for the lifetime of the instance.
        /// </summary>
        public DefaultCameraInputReader()
        {
            _inner = Resolve();
        }

        /// <summary>
        ///     The concrete reader selected at construction time. Exposed for diagnostics
        ///     and for consumers that want to special-case behaviour per backend.
        /// </summary>
        public ICameraInputReader Inner => _inner;

        /// <inheritdoc />
        public void Sample() => _inner.Sample();

        /// <inheritdoc />
        public Vector2 ReadOrbitDelta() => _inner.ReadOrbitDelta();

        /// <inheritdoc />
        public float ReadZoomDelta() => _inner.ReadZoomDelta();

        /// <inheritdoc />
        public bool IsOrbitActive() => _inner.IsOrbitActive();

        /// <inheritdoc />
        public bool IsPanActive() => _inner.IsPanActive();

        /// <inheritdoc />
        public bool IsPointerOverUI() => _inner.IsPointerOverUI();

        /// <inheritdoc />
        public bool IsTextInputFocused() => _inner.IsTextInputFocused();

        private static ICameraInputReader Resolve()
        {
#if ENABLE_INPUT_SYSTEM
            return new InputSystemCameraInputReader();
#elif ENABLE_LEGACY_INPUT_MANAGER
            return new LegacyCameraInputReader();
#else
            Debug.LogWarning(
                "[Convai][OrbitCamera] No supported input backend is enabled. " +
                "Enable the Input System package or the legacy Input Manager under " +
                "Project Settings > Player > Active Input Handling. " +
                "Falling back to NullCameraInputReader (camera input disabled).");
            return NullCameraInputReader.Instance;
#endif
        }
    }
}
