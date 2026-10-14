using UnityEngine;

namespace Convai.Runtime.Components
{
    /// <summary>
    ///     The SDK's single answer to "was this pressed?", across every input backend a host
    ///     project can be configured with.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Four components used to carry their own copy of the same Input System reflection
    ///         block, three of them re-resolving types, properties and enum values on every frame.
    ///         Everything now goes through the cached <see cref="ConvaiInputDeviceProbe" />
    ///         instances below, so idle polling allocates nothing.
    ///     </para>
    ///     <para>
    ///         Both backends are consulted rather than one being chosen at compile time. When a
    ///         project enables both, a press reported by either is a press: the legacy Input Manager
    ///         is asked first because it is the cheaper call, and the Input System is asked only
    ///         when the legacy backend saw nothing. When neither define is present the legacy API is
    ///         still attempted, guarded, because a project in that state has no other source of
    ///         input at all.
    ///     </para>
    ///     <para>
    ///         The backend switches are compile-time constants rather than <c>#if</c> blocks around
    ///         the calls, so every branch is type-checked in every configuration instead of only in
    ///         the one the current project happens to be set to.
    ///     </para>
    /// </remarks>
    internal static class ConvaiInputSystem
    {
        internal const string KeyboardTypeName = "UnityEngine.InputSystem.Keyboard, Unity.InputSystem";
        internal const string KeyEnumTypeName = "UnityEngine.InputSystem.Key, Unity.InputSystem";
        internal const string MouseTypeName = "UnityEngine.InputSystem.Mouse, Unity.InputSystem";
        internal const string TouchscreenTypeName = "UnityEngine.InputSystem.Touchscreen, Unity.InputSystem";

        internal const string AnyKeyControlPath = "anyKey";
        internal const string PrimaryTouchPressControlPath = "primaryTouch/press";

#if ENABLE_LEGACY_INPUT_MANAGER
        private const bool LegacyInputManagerEnabled = true;
#else
        private const bool LegacyInputManagerEnabled = false;
#endif

#if ENABLE_INPUT_SYSTEM
        private const bool InputSystemEnabled = true;
#else
        private const bool InputSystemEnabled = false;
#endif

        private const bool NoBackendConfigured = !LegacyInputManagerEnabled && !InputSystemEnabled;

        private static readonly string[] s_mouseButtonControlPaths = { "leftButton", "rightButton", "middleButton" };

        private static readonly ConvaiInputDeviceProbe s_keyboard = new(KeyboardTypeName, KeyEnumTypeName);
        private static readonly ConvaiInputDeviceProbe s_mouse = new(MouseTypeName);
        private static readonly ConvaiInputDeviceProbe s_touchscreen = new(TouchscreenTypeName);

        /// <summary>Was <paramref name="keyCode" /> pressed down during this frame?</summary>
        internal static bool WasKeyPressedThisFrame(KeyCode keyCode)
        {
            if (LegacyInputManagerEnabled)
            {
                if (Input.GetKeyDown(keyCode)) return true;
            }

            if (InputSystemEnabled)
            {
                if (s_keyboard.WasKeyPressedThisFrame(keyCode)) return true;
            }

            if (NoBackendConfigured)
            {
                try
                {
                    return Input.GetKeyDown(keyCode);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>Is <paramref name="keyCode" /> held down right now?</summary>
        internal static bool IsKeyHeld(KeyCode keyCode)
        {
            if (LegacyInputManagerEnabled)
            {
                if (Input.GetKey(keyCode)) return true;
            }

            if (InputSystemEnabled)
            {
                if (s_keyboard.IsKeyHeld(keyCode)) return true;
            }

            if (NoBackendConfigured)
            {
                try
                {
                    return Input.GetKey(keyCode);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>Was any key pressed down during this frame?</summary>
        internal static bool WasAnyKeyPressedThisFrame()
        {
            if (LegacyInputManagerEnabled)
            {
                if (Input.anyKeyDown) return true;
            }

            if (InputSystemEnabled)
            {
                if (s_keyboard.WasControlPressedThisFrame(AnyKeyControlPath)) return true;
            }

            return false;
        }

        /// <summary>Was mouse button <paramref name="button" /> (0 left, 1 right, 2 middle) pressed this frame?</summary>
        internal static bool WasMouseButtonPressedThisFrame(int button)
        {
            if (button < 0 || button >= s_mouseButtonControlPaths.Length) return false;

            if (LegacyInputManagerEnabled)
            {
                if (Input.GetMouseButtonDown(button)) return true;
            }

            if (InputSystemEnabled)
            {
                if (s_mouse.WasControlPressedThisFrame(s_mouseButtonControlPaths[button])) return true;
            }

            return false;
        }

        /// <summary>Did a touch begin during this frame?</summary>
        internal static bool WasTouchStartedThisFrame()
        {
            if (LegacyInputManagerEnabled)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    if (Input.GetTouch(i).phase == TouchPhase.Began) return true;
                }
            }

            if (InputSystemEnabled)
            {
                if (s_touchscreen.WasControlPressedThisFrame(PrimaryTouchPressControlPath)) return true;
            }

            return false;
        }

        /// <summary>
        ///     Translates a <see cref="KeyCode" /> to the Input System <c>Key</c> name for the same
        ///     physical key.
        /// </summary>
        /// <remarks>
        ///     The two enumerations disagree on more than the digits: keypad keys are "Numpad", the
        ///     modifiers are abbreviated, and the platform command keys collapse onto one "Meta"
        ///     name. A key missing from this map used to be reported as never pressed.
        /// </remarks>
        internal static string ConvertToInputSystemKeyName(KeyCode keyCode)
        {
            switch (keyCode)
            {
                case KeyCode.Alpha0: return "Digit0";
                case KeyCode.Alpha1: return "Digit1";
                case KeyCode.Alpha2: return "Digit2";
                case KeyCode.Alpha3: return "Digit3";
                case KeyCode.Alpha4: return "Digit4";
                case KeyCode.Alpha5: return "Digit5";
                case KeyCode.Alpha6: return "Digit6";
                case KeyCode.Alpha7: return "Digit7";
                case KeyCode.Alpha8: return "Digit8";
                case KeyCode.Alpha9: return "Digit9";
                case KeyCode.Keypad0: return "Numpad0";
                case KeyCode.Keypad1: return "Numpad1";
                case KeyCode.Keypad2: return "Numpad2";
                case KeyCode.Keypad3: return "Numpad3";
                case KeyCode.Keypad4: return "Numpad4";
                case KeyCode.Keypad5: return "Numpad5";
                case KeyCode.Keypad6: return "Numpad6";
                case KeyCode.Keypad7: return "Numpad7";
                case KeyCode.Keypad8: return "Numpad8";
                case KeyCode.Keypad9: return "Numpad9";
                case KeyCode.KeypadDivide: return "NumpadDivide";
                case KeyCode.KeypadMultiply: return "NumpadMultiply";
                case KeyCode.KeypadMinus: return "NumpadMinus";
                case KeyCode.KeypadPlus: return "NumpadPlus";
                case KeyCode.KeypadPeriod: return "NumpadPeriod";
                case KeyCode.KeypadEquals: return "NumpadEquals";
                case KeyCode.KeypadEnter: return "NumpadEnter";
                case KeyCode.LeftControl: return "LeftCtrl";
                case KeyCode.RightControl: return "RightCtrl";
                case KeyCode.LeftCommand: return "LeftMeta";
                case KeyCode.RightCommand: return "RightMeta";
                case KeyCode.LeftWindows: return "LeftMeta";
                case KeyCode.RightWindows: return "RightMeta";
                case KeyCode.Return: return "Enter";
                case KeyCode.BackQuote: return "Backquote";
                case KeyCode.Numlock: return "NumLock";
                case KeyCode.Print: return "PrintScreen";
                case KeyCode.Menu: return "ContextMenu";
                default: return keyCode.ToString();
            }
        }
    }
}
