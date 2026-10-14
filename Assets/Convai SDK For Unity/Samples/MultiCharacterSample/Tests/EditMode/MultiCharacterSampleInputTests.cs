using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.MultiCharacter
{
    /// <summary>
    /// Covers the two decisions the sample's own input code makes, both of which are pure functions
    /// of their arguments. What the sample used to decide about the conversation itself — who is
    /// addressed, and whether that survives a glance elsewhere — belongs to the SDK now and is
    /// covered by ConversationTargetSolverTests and ConversationTargetSwitchPolicyTests, where the
    /// behaviour lives.
    /// </summary>
    public sealed class MultiCharacterSampleInputTests
    {
        private static readonly MethodInfo _cursorLockDecision =
            typeof(MultiCharacterSampleFirstPersonPlayer).GetMethod(
                "ShouldLockCursorForPrimaryClick",
                BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo _applyJoystickDeadZone =
            typeof(MultiCharacterSampleController).GetMethod(
                "ApplyJoystickDeadZone",
                BindingFlags.NonPublic | BindingFlags.Static);

        [Test]
        public void PrimaryClickOverUiDoesNotLockCursor()
        {
            Assert.That(_cursorLockDecision, Is.Not.Null);
            Assert.That(ShouldLockCursor(primaryClickPressed: true, pointerOverUi: true), Is.False);
        }

        [Test]
        public void PrimaryClickOutsideUiLocksCursor()
        {
            Assert.That(_cursorLockDecision, Is.Not.Null);
            Assert.That(ShouldLockCursor(primaryClickPressed: true, pointerOverUi: false), Is.True);
        }

        [Test]
        public void NoPrimaryClickDoesNotLockCursor()
        {
            Assert.That(_cursorLockDecision, Is.Not.Null);
            Assert.That(ShouldLockCursor(primaryClickPressed: false, pointerOverUi: false), Is.False);
        }

        [Test]
        public void MobileJoystickIgnoresInputInsideDeadZone()
        {
            Assert.That(_applyJoystickDeadZone, Is.Not.Null);

            Vector2 result = ApplyDeadZone(new Vector2(0.05f, 0f), 0.1f);

            Assert.That(result, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void MobileJoystickReachesFullInputAtOuterEdge()
        {
            Vector2 result = ApplyDeadZone(Vector2.up, 0.1f);

            Assert.That(result.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(result.y, Is.EqualTo(1f).Within(0.0001f));
        }

        private static bool ShouldLockCursor(bool primaryClickPressed, bool pointerOverUi) =>
            (bool)_cursorLockDecision.Invoke(null, new object[] { primaryClickPressed, pointerOverUi });

        private static Vector2 ApplyDeadZone(Vector2 input, float deadZone) =>
            (Vector2)_applyJoystickDeadZone.Invoke(null, new object[] { input, deadZone });
    }
}
