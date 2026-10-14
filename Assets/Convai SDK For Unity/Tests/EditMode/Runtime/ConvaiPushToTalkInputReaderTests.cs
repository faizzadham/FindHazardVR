using System.Collections.Generic;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR;

namespace Convai.Tests.EditMode.Runtime
{
    public class ConvaiPushToTalkInputReaderTests
    {
        [TestCase(KeyCode.JoystickButton0, XRNode.RightHand, true)]
        [TestCase(KeyCode.JoystickButton1, XRNode.RightHand, false)]
        [TestCase(KeyCode.JoystickButton2, XRNode.LeftHand, true)]
        [TestCase(KeyCode.JoystickButton3, XRNode.LeftHand, false)]
        public void IsHeld_WhenXrIsActive_ReadsMappedControllerButton(
            KeyCode keyCode,
            XRNode expectedNode,
            bool expectsPrimaryButton)
        {
            var source = new FakeXrButtonStateSource
            {
                HasRunningInputSubsystem = true,
                IsConnected = true,
                ButtonStates = new Queue<bool>(new[] { true })
            };
            int fallbackCalls = 0;
            var reader = new ConvaiPushToTalkInputReader(source, _ =>
            {
                fallbackCalls++;
                return false;
            });

            Assert.That(reader.IsHeld(keyCode), Is.True);
            Assert.That(source.LastNode, Is.EqualTo(expectedNode));
            Assert.That(
                source.LastUsage.name,
                Is.EqualTo(expectsPrimaryButton
                    ? CommonUsages.primaryButton.name
                    : CommonUsages.secondaryButton.name));
            Assert.That(fallbackCalls, Is.Zero);
        }

        [Test]
        public void IsHeld_WhenMappedButtonIsPressedThenReleased_ReportsBothTransitions()
        {
            var source = new FakeXrButtonStateSource
            {
                HasRunningInputSubsystem = true,
                IsConnected = true,
                ButtonStates = new Queue<bool>(new[] { true, false })
            };
            var reader = new ConvaiPushToTalkInputReader(source, _ => true);

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.True);
            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.False);
        }

        [Test]
        public void IsHeld_WhenMappedButtonCannotBeRead_FailsClosedWithoutFallback()
        {
            var source = new FakeXrButtonStateSource
            {
                HasRunningInputSubsystem = true,
                IsConnected = true,
                CanReadButton = false
            };
            int fallbackCalls = 0;
            var reader = new ConvaiPushToTalkInputReader(source, _ =>
            {
                fallbackCalls++;
                return true;
            });

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.False);
            Assert.That(fallbackCalls, Is.Zero);
        }

        [Test]
        public void IsHeld_WhenControllerDisconnectsWhileHeld_FailsClosed()
        {
            var source = new FakeXrButtonStateSource
            {
                HasRunningInputSubsystem = true,
                IsConnected = true,
                ButtonStates = new Queue<bool>(new[] { true })
            };
            var reader = new ConvaiPushToTalkInputReader(source, _ => true);

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.True);

            source.IsConnected = false;

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.False);
        }

        [Test]
        public void IsHeld_WhenXrStopsAfterControllerSelection_RestoresFallback()
        {
            var source = new FakeXrButtonStateSource
            {
                HasRunningInputSubsystem = true,
                IsConnected = true,
                ButtonStates = new Queue<bool>(new[] { true })
            };
            int fallbackCalls = 0;
            var reader = new ConvaiPushToTalkInputReader(source, _ =>
            {
                fallbackCalls++;
                return true;
            });

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.True);

            source.HasRunningInputSubsystem = false;
            source.IsConnected = false;

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.True);
            Assert.That(fallbackCalls, Is.EqualTo(1));
        }

        [Test]
        public void IsHeld_WhenXrRunsWithoutHandController_UsesFallbackForMappedJoystickButton()
        {
            var source = new FakeXrButtonStateSource
            {
                HasRunningInputSubsystem = true,
                IsConnected = false
            };
            int fallbackCalls = 0;
            var reader = new ConvaiPushToTalkInputReader(source, _ =>
            {
                fallbackCalls++;
                return true;
            });

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.True);
            Assert.That(fallbackCalls, Is.EqualTo(1));
            Assert.That(source.ReadCount, Is.Zero);
        }

        [Test]
        public void IsHeld_WhenKeyboardKeyIsConfigured_UsesFallbackDuringActiveXr()
        {
            var source = new FakeXrButtonStateSource { HasRunningInputSubsystem = true };
            KeyCode fallbackKey = KeyCode.None;
            var reader = new ConvaiPushToTalkInputReader(source, keyCode =>
            {
                fallbackKey = keyCode;
                return true;
            });

            Assert.That(reader.IsHeld(KeyCode.T), Is.True);
            Assert.That(fallbackKey, Is.EqualTo(KeyCode.T));
            Assert.That(source.ReadCount, Is.Zero);
        }

        [Test]
        public void IsHeld_WhenXrIsInactive_UsesFallbackForJoystickButton()
        {
            var source = new FakeXrButtonStateSource { HasRunningInputSubsystem = false };
            KeyCode fallbackKey = KeyCode.None;
            var reader = new ConvaiPushToTalkInputReader(source, keyCode =>
            {
                fallbackKey = keyCode;
                return true;
            });

            Assert.That(reader.IsHeld(KeyCode.JoystickButton0), Is.True);
            Assert.That(fallbackKey, Is.EqualTo(KeyCode.JoystickButton0));
            Assert.That(source.ReadCount, Is.Zero);
        }

        [Test]
        public void IsHeld_WhenJoystickButtonHasNoXrMapping_UsesFallbackDuringActiveXr()
        {
            var source = new FakeXrButtonStateSource { HasRunningInputSubsystem = true };
            KeyCode fallbackKey = KeyCode.None;
            var reader = new ConvaiPushToTalkInputReader(source, keyCode =>
            {
                fallbackKey = keyCode;
                return true;
            });

            Assert.That(reader.IsHeld(KeyCode.JoystickButton4), Is.True);
            Assert.That(fallbackKey, Is.EqualTo(KeyCode.JoystickButton4));
            Assert.That(source.ReadCount, Is.Zero);
        }

        private sealed class FakeXrButtonStateSource : IConvaiXrButtonStateSource
        {
            public bool HasRunningInputSubsystem { get; set; }
            public bool IsConnected { get; set; }
            public bool CanReadButton { get; set; } = true;
            public Queue<bool> ButtonStates { get; set; } = new();
            public XRNode LastNode { get; private set; }
            public InputFeatureUsage<bool> LastUsage { get; private set; }
            public int ReadCount { get; private set; }

            public bool HasController(XRNode node) => IsConnected;

            public bool TryGetButton(XRNode node, InputFeatureUsage<bool> usage, out bool isPressed)
            {
                ReadCount++;
                LastNode = node;
                LastUsage = usage;

                if (!IsConnected || !CanReadButton || ButtonStates.Count == 0)
                {
                    isPressed = false;
                    return false;
                }

                isPressed = ButtonStates.Dequeue();
                return true;
            }
        }
    }
}
