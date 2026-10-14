using System.Collections;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Runtime.InputPolling
{
    /// <summary>
    ///     Gate for the shared input lookup: it must answer the same as the per-frame reflection it
    ///     replaced, survive a device being replaced or unplugged, and cost nothing once warmed.
    /// </summary>
    /// <remarks>
    ///     Idle profiling attributed 1,243 B/frame to two components doing nothing but asking
    ///     whether a key had been pressed. Caching a reflection lookup is only safe if the cache is
    ///     dropped when the device behind it changes, so the replacement cases below matter as much
    ///     as the allocation one.
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiInputDeviceProbeTests
    {
        private const string KeyboardTypeName =
            "Convai.Tests.EditMode.Runtime.InputPolling.FakeKeyboardDevice, Convai.Tests.EditMode";

        private const string KeyEnumTypeName =
            "Convai.Tests.EditMode.Runtime.InputPolling.FakeKey, Convai.Tests.EditMode";

        private const string TouchscreenTypeName =
            "Convai.Tests.EditMode.Runtime.InputPolling.FakeTouchscreenDevice, Convai.Tests.EditMode";

        [SetUp]
        public void SetUp()
        {
            FakeKeyboardDevice.current = new FakeKeyboardDevice();
            FakeTouchscreenDevice.current = new FakeTouchscreenDevice();
        }

        [TearDown]
        public void TearDown()
        {
            FakeKeyboardDevice.current = null;
            FakeTouchscreenDevice.current = null;
        }

        private static ConvaiInputDeviceProbe Keyboard() => new(KeyboardTypeName, KeyEnumTypeName);

        [Test]
        public void AbsentInputSystemPackage_ReportsNoInputAndDoesNotThrow()
        {
            var probe = new ConvaiInputDeviceProbe(
                "UnityEngine.InputSystem.Keyboard, NotAnAssemblyThatExists",
                "UnityEngine.InputSystem.Key, NotAnAssemblyThatExists");

            Assert.That(probe.IsAvailable, Is.False, "An unresolvable device type must report itself unavailable.");
            Assert.That(probe.HasDevice, Is.False);
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.False);
            Assert.That(probe.IsKeyHeld(KeyCode.Return), Is.False);
            Assert.That(probe.WasControlPressedThisFrame("anyKey"), Is.False);
        }

        [Test]
        public void NoDeviceConnected_ReportsNoInput()
        {
            FakeKeyboardDevice.current = null;
            ConvaiInputDeviceProbe probe = Keyboard();

            Assert.That(probe.IsAvailable, Is.True, "The type resolved; only the device is missing.");
            Assert.That(probe.HasDevice, Is.False);
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.False);
        }

        [Test]
        public void PressedKey_IsReportedThroughTheKeyEnumIndexer()
        {
            ConvaiInputDeviceProbe probe = Keyboard();
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.False);

            FakeKeyboardDevice.current.Control(FakeKey.Enter).wasPressedThisFrame = true;

            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.True);
        }

        [Test]
        public void KeypadEnter_ResolvesToTheNumpadEnterKey()
        {
            ConvaiInputDeviceProbe probe = Keyboard();
            FakeKeyboardDevice.current.Control(FakeKey.NumpadEnter).wasPressedThisFrame = true;

            // The chat field accepts either Enter key; KeypadEnter had no mapping and could never fire.
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.KeypadEnter), Is.True);
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.False);
        }

        [Test]
        public void HeldKey_IsReportedSeparatelyFromPressedThisFrame()
        {
            ConvaiInputDeviceProbe probe = Keyboard();
            FakeKeyboardDevice.current.Control(FakeKey.F10).isPressed = true;

            Assert.That(probe.IsKeyHeld(KeyCode.F10), Is.True, "Push-to-talk reads the held state.");
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.F10), Is.False);
        }

        [Test]
        public void KeyWithNoEquivalentOnTheDevice_NeverReportsAPress()
        {
            ConvaiInputDeviceProbe probe = Keyboard();

            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Joystick8Button19), Is.False);
            // Cached as a failure, not retried: the second call must behave the same.
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Joystick8Button19), Is.False);
        }

        [Test]
        public void NestedControlPath_IsTraversedToTheButton()
        {
            var probe = new ConvaiInputDeviceProbe(TouchscreenTypeName);
            Assert.That(probe.WasControlPressedThisFrame("primaryTouch/press"), Is.False);

            FakeTouchscreenDevice.current.primaryTouch.press.wasPressedThisFrame = true;

            Assert.That(probe.WasControlPressedThisFrame("primaryTouch/press"), Is.True);
            Assert.That(probe.WasControlPressedThisFrame("noSuchControl"), Is.False);
        }

        [Test]
        public void ReplacedDevice_IsReadInsteadOfTheOneTheCacheWasBuiltFrom()
        {
            ConvaiInputDeviceProbe probe = Keyboard();
            FakeKeyboardDevice replaced = FakeKeyboardDevice.current;
            replaced.Control(FakeKey.Enter).wasPressedThisFrame = true;
            replaced.anyKey.wasPressedThisFrame = true;
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.True);
            Assert.That(probe.WasControlPressedThisFrame("anyKey"), Is.True);

            // Hot-plug: the old device is still "pressed", so a stale binding would keep saying yes.
            FakeKeyboardDevice.current = new FakeKeyboardDevice();

            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.False);
            Assert.That(probe.WasControlPressedThisFrame("anyKey"), Is.False);

            FakeKeyboardDevice.current.Control(FakeKey.Enter).wasPressedThisFrame = true;
            FakeKeyboardDevice.current.anyKey.wasPressedThisFrame = true;

            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.Return), Is.True);
            Assert.That(probe.WasControlPressedThisFrame("anyKey"), Is.True);
        }

        [Test]
        public void DeviceRemovedThenRestored_ResumesReporting()
        {
            ConvaiInputDeviceProbe probe = Keyboard();
            FakeKeyboardDevice.current.Control(FakeKey.A).wasPressedThisFrame = true;
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.A), Is.True);

            FakeKeyboardDevice.current = null;
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.A), Is.False);

            FakeKeyboardDevice.current = new FakeKeyboardDevice();
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.A), Is.False);

            FakeKeyboardDevice.current.Control(FakeKey.A).wasPressedThisFrame = true;
            Assert.That(probe.WasKeyPressedThisFrame(KeyCode.A), Is.True);
        }

        [UnityTest]
        public IEnumerator WarmedKeyPolling_AllocatesNothing()
        {
            ConvaiInputDeviceProbe probe = Keyboard();
            long bytes = -1;

            yield return AllocationMeasurement.Measure(
                _ => probe.WasKeyPressedThisFrame(KeyCode.Return),
                value => bytes = value,
                "ConvaiInputDeviceProbe.WasKeyPressedThisFrame");

            Assert.That(bytes, Is.EqualTo(0L),
                "Idle key polling runs every frame in two components; it must allocate nothing.");
        }

        [UnityTest]
        public IEnumerator WarmedHeldKeyPolling_AllocatesNothing()
        {
            ConvaiInputDeviceProbe probe = Keyboard();
            long bytes = -1;

            yield return AllocationMeasurement.Measure(
                _ => probe.IsKeyHeld(KeyCode.F10),
                value => bytes = value,
                "ConvaiInputDeviceProbe.IsKeyHeld");

            Assert.That(bytes, Is.EqualTo(0L), "Push-to-talk polls the held state every frame.");
        }

        [UnityTest]
        public IEnumerator WarmedControlPathPolling_AllocatesNothing()
        {
            var probe = new ConvaiInputDeviceProbe(TouchscreenTypeName);
            long bytes = -1;

            yield return AllocationMeasurement.Measure(
                _ => probe.WasControlPressedThisFrame("primaryTouch/press"),
                value => bytes = value,
                "ConvaiInputDeviceProbe.WasControlPressedThisFrame");

            Assert.That(bytes, Is.EqualTo(0L), "The idle reset driver polls five controls every frame.");
        }
    }
}
