// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera.Input
{
    /// <summary>
    ///     Contract for supplying mouse-like orbit, pan, and zoom input to
    ///     <see cref="ConvaiOrbitCamera" />. Implementations translate the host application's
    ///     input backend (legacy Input Manager, Input System package, or a custom source)
    ///     into normalized per-frame deltas consumed by the camera.
    /// </summary>
    /// <remarks>
    ///     Implementations should be allocation-free and fully reentrant. The camera calls
    ///     <see cref="Sample" /> exactly once per frame before invoking the read methods.
    ///     Expected normalization conventions:
    ///     <list type="bullet">
    ///         <item><description><see cref="ReadOrbitDelta" /> returns approximate pixel deltas since the last <see cref="Sample" />.</description></item>
    ///         <item><description><see cref="ReadZoomDelta" /> returns approximate <em>notches</em> (~1.0 per wheel click).</description></item>
    ///     </list>
    /// </remarks>
    public interface ICameraInputReader
    {
        /// <summary>
        ///     Samples the underlying backend once per frame. Implementations typically read
        ///     device state into internal fields so the <c>Read*</c> accessors are idempotent.
        /// </summary>
        void Sample();

        /// <summary>
        ///     The mouse motion accumulated since the previous <see cref="Sample" />,
        ///     expressed as approximate pixel deltas. Consumed while either
        ///     <see cref="IsOrbitActive" /> or <see cref="IsPanActive" /> is <c>true</c>.
        /// </summary>
        Vector2 ReadOrbitDelta();

        /// <summary>
        ///     Scroll-wheel delta since the previous <see cref="Sample" />, normalized to
        ///     approximate notches (~1.0 per wheel click). Positive values conventionally
        ///     dolly the camera in.
        /// </summary>
        float ReadZoomDelta();

        /// <summary>
        ///     Whether the user is actively requesting camera orbit this frame (for example,
        ///     right mouse button held). While this returns <c>false</c>, the camera ignores
        ///     the orbit contribution of <see cref="ReadOrbitDelta" />.
        /// </summary>
        bool IsOrbitActive();

        /// <summary>
        ///     Whether the user is actively requesting a lateral pan this frame (for example,
        ///     middle mouse button held). Pan takes precedence over orbit when both buttons
        ///     are held simultaneously.
        /// </summary>
        bool IsPanActive();

        /// <summary>
        ///     Whether the pointer is hovering an active UI element and the camera should
        ///     ignore input so UI clicks/drags never disturb the view. Implementations should
        ///     return <c>false</c> when no UI framework is present.
        /// </summary>
        bool IsPointerOverUI();

        /// <summary>
        ///     Whether the host application currently wants keyboard focus to suppress camera
        ///     input. Built-in readers return <c>false</c>; applications with text fields or
        ///     custom focus rules can provide this through a custom reader.
        /// </summary>
        bool IsTextInputFocused();
    }
}
