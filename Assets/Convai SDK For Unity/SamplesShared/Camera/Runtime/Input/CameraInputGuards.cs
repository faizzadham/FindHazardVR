// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;
using UnityEngine.EventSystems;

namespace Convai.Sample.Camera.Input
{
    /// <summary>
    ///     Shared helpers used by the built-in <see cref="ICameraInputReader" /> implementations
    ///     to detect UI-related conditions in a backend-agnostic way without inspecting
    ///     project-specific UI component types.
    /// </summary>
    internal static class CameraInputGuards
    {
        /// <summary>
        ///     Whether the <see cref="EventSystem" /> reports the pointer is over a UI element.
        ///     Safe to call when no <see cref="EventSystem" /> exists.
        /// </summary>
        public static bool IsPointerOverUI()
        {
            EventSystem eventSystem = EventSystem.current;
            return eventSystem != null && eventSystem.IsPointerOverGameObject();
        }

        /// <summary>
        ///     Whether keyboard focus should suppress camera input. The built-in readers do not
        ///     infer this from UI component types; projects with text fields or custom focus
        ///     rules should provide their own <see cref="ICameraInputReader" />.
        /// </summary>
        public static bool IsTextInputFocused() => false;
    }
}
