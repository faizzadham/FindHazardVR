using System;
using System.Collections;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Runtime.InputPolling
{
    /// <summary>
    ///     Gate for the shared entry point every Convai component now polls through.
    /// </summary>
    /// <remarks>
    ///     Which backend answers is decided at compile time from the project's Active Input
    ///     Handling setting, so one test run can only exercise the configuration this project is
    ///     built with. What is checked here is what holds in all three: the helper never throws,
    ///     reports nothing when nothing is pressed, allocates nothing per call once warmed, and
    ///     translates every <see cref="KeyCode" /> the two backends spell differently. The
    ///     backend-independent behaviour of the Input System path itself is covered against
    ///     replaceable fake devices in <see cref="ConvaiInputDeviceProbeTests" />.
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiInputSystemTests
    {
        [Test]
        public void PollingWithNothingPressed_ReportsNothingAndDoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                Assert.That(ConvaiInputSystem.WasKeyPressedThisFrame(KeyCode.F15), Is.False);
                Assert.That(ConvaiInputSystem.IsKeyHeld(KeyCode.F15), Is.False);
                ConvaiInputSystem.WasAnyKeyPressedThisFrame();
                ConvaiInputSystem.WasMouseButtonPressedThisFrame(0);
                ConvaiInputSystem.WasMouseButtonPressedThisFrame(1);
                ConvaiInputSystem.WasMouseButtonPressedThisFrame(2);
                ConvaiInputSystem.WasTouchStartedThisFrame();
            });
        }

        [Test]
        public void MouseButtonOutsideTheKnownRange_ReportsNoPress()
        {
            Assert.That(ConvaiInputSystem.WasMouseButtonPressedThisFrame(-1), Is.False);
            Assert.That(ConvaiInputSystem.WasMouseButtonPressedThisFrame(3), Is.False);
        }

        [TestCase(KeyCode.Return, "Enter")]
        [TestCase(KeyCode.KeypadEnter, "NumpadEnter")]
        [TestCase(KeyCode.Alpha1, "Digit1")]
        [TestCase(KeyCode.Keypad3, "Numpad3")]
        [TestCase(KeyCode.KeypadPlus, "NumpadPlus")]
        [TestCase(KeyCode.KeypadPeriod, "NumpadPeriod")]
        [TestCase(KeyCode.LeftControl, "LeftCtrl")]
        [TestCase(KeyCode.RightControl, "RightCtrl")]
        [TestCase(KeyCode.LeftCommand, "LeftMeta")]
        [TestCase(KeyCode.RightWindows, "RightMeta")]
        [TestCase(KeyCode.BackQuote, "Backquote")]
        [TestCase(KeyCode.Numlock, "NumLock")]
        [TestCase(KeyCode.Print, "PrintScreen")]
        [TestCase(KeyCode.Menu, "ContextMenu")]
        [TestCase(KeyCode.F10, "F10")]
        [TestCase(KeyCode.Space, "Space")]
        [TestCase(KeyCode.A, "A")]
        public void KeyCodesTheTwoBackendsSpellDifferently_AreTranslated(KeyCode keyCode, string expected)
        {
            Assert.That(ConvaiInputSystem.ConvertToInputSystemKeyName(keyCode), Is.EqualTo(expected));
        }

        [Test]
        public void EveryTranslatedName_IsAValidInputSystemKeyName()
        {
            Type keyType = Type.GetType(ConvaiInputSystem.KeyEnumTypeName, false);
            if (keyType == null)
                Assert.Ignore("The Input System package is not installed; there is no Key enum to check against.");

            foreach (KeyCode keyCode in Enum.GetValues(typeof(KeyCode)))
            {
                string name = ConvaiInputSystem.ConvertToInputSystemKeyName(keyCode);
                if (name == keyCode.ToString()) continue;

                // A hand-written mapping that names a key the package does not have is worse than no
                // mapping: it silently reports the key as never pressed.
                Assert.That(Enum.IsDefined(keyType, name), Is.True,
                    $"{keyCode} maps to '{name}', which the Input System Key enum does not define.");
            }
        }

        [UnityTest]
        public IEnumerator WarmedPollingOfTheConfiguredBackend_AllocatesNothing()
        {
            long bytes = -1;

            yield return AllocationMeasurement.Measure(
                _ => ConvaiInputSystem.WasKeyPressedThisFrame(KeyCode.F15),
                value => bytes = value,
                "ConvaiInputSystem.WasKeyPressedThisFrame");

            Assert.That(bytes, Is.EqualTo(0L),
                "Two shipped components poll a key every frame while the player is idle.");
        }

        [UnityTest]
        public IEnumerator WarmedIdleActivityPolling_AllocatesNothing()
        {
            long bytes = -1;

            // The same five queries the idle reset driver runs on every frame it is enabled.
            yield return AllocationMeasurement.Measure(
                _ =>
                {
                    ConvaiInputSystem.WasMouseButtonPressedThisFrame(0);
                    ConvaiInputSystem.WasMouseButtonPressedThisFrame(1);
                    ConvaiInputSystem.WasMouseButtonPressedThisFrame(2);
                    ConvaiInputSystem.WasTouchStartedThisFrame();
                    ConvaiInputSystem.WasAnyKeyPressedThisFrame();
                },
                value => bytes = value,
                "ConvaiInputSystem idle activity sweep");

            Assert.That(bytes, Is.EqualTo(0L));
        }
    }
}
