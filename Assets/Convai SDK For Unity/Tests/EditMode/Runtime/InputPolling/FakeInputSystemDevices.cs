using System.Collections.Generic;

namespace Convai.Tests.EditMode.Runtime.InputPolling
{
    /// <summary>
    ///     Stand-ins shaped like the Input System types the SDK reflects over.
    /// </summary>
    /// <remarks>
    ///     The SDK reads the new input backend through reflection because the package is optional.
    ///     Testing that reflection against the real package would make the gate depend on a device
    ///     the Editor may or may not have, and would give no way to unplug one. These fakes have the
    ///     same shape — a static <c>current</c>, a key-enum indexer, nested controls, and
    ///     <c>wasPressedThisFrame</c> / <c>isPressed</c> — so the probe cannot tell the difference,
    ///     and a test can replace or remove the device at will.
    ///
    ///     Public on purpose: a delegate bound to a member of an internal type is not accessible
    ///     from the assembly under test.
    /// </remarks>
    public enum FakeKey
    {
        None = 0,
        Enter,
        NumpadEnter,
        NumLock,
        LeftCtrl,
        LeftMeta,
        Digit1,
        F10,
        A
    }

    public sealed class FakeButtonControl
    {
        public bool wasPressedThisFrame { get; set; }

        public bool isPressed { get; set; }
    }

    public sealed class FakeTouchControl
    {
        public FakeButtonControl press { get; } = new();
    }

    public sealed class FakeKeyboardDevice
    {
        private readonly Dictionary<FakeKey, FakeButtonControl> _controls = new();

        public static FakeKeyboardDevice current { get; set; }

        public FakeButtonControl anyKey { get; } = new();

        public FakeButtonControl this[FakeKey key] => Control(key);

        public FakeButtonControl Control(FakeKey key)
        {
            if (!_controls.TryGetValue(key, out FakeButtonControl control))
            {
                control = new FakeButtonControl();
                _controls[key] = control;
            }

            return control;
        }
    }

    public sealed class FakeTouchscreenDevice
    {
        public static FakeTouchscreenDevice current { get; set; }

        public FakeTouchControl primaryTouch { get; } = new();
    }
}
