using Convai.Modules.ConversationFlow.Core;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.ConversationFlow
{
    /// <summary>
    ///     Contract tests for <see cref="ConversationFlowTimings" />: clamping, defaults,
    ///     and guard-condition semantics.
    /// </summary>
    [TestFixture]
    public sealed class ConversationFlowTimingsTests
    {
        [TearDown]
        public void TearDown() => LogAssert.NoUnexpectedReceived();

        [Test]
        public void Constructor_NegativeValues_ClampedToZero()
        {
            // Arrange / Act
            var timings = new ConversationFlowTimings(
                transitionDuration: -1f,
                thinkingMinHold: -1f,
                thinkingMaxHold: -1f,
                attendingGracePeriod: -1f,
                settlingDuration: -1f,
                idleReturnDelay: -1f,
                interruptedFreezeDuration: -1f,
                speakingBaseEnergy: -1f);

            // Assert
            Assert.That(timings.TransitionDuration, Is.EqualTo(0f));
            Assert.That(timings.ThinkingMinHold, Is.EqualTo(0f));
            Assert.That(timings.AttendingGracePeriod, Is.EqualTo(0f));
            Assert.That(timings.SettlingDuration, Is.EqualTo(0f));
            Assert.That(timings.IdleReturnDelay, Is.EqualTo(0f));
            Assert.That(timings.InterruptedFreezeDuration, Is.EqualTo(0f));
            Assert.That(timings.SpeakingBaseEnergy, Is.EqualTo(0f));
        }

        [Test]
        public void Constructor_ThinkingMaxHoldLessThanMin_ClampedToMin()
        {
            // Arrange / Act
            var timings = new ConversationFlowTimings(
                transitionDuration: 0.25f,
                thinkingMinHold: 1.0f,
                thinkingMaxHold: 0.5f, // less than min
                attendingGracePeriod: 0.3f,
                settlingDuration: 0.6f,
                idleReturnDelay: 60f,
                interruptedFreezeDuration: 0.25f,
                speakingBaseEnergy: 0.6f);

            // Assert — max hold must be at least equal to min hold
            Assert.That(timings.ThinkingMaxHold, Is.GreaterThanOrEqualTo(timings.ThinkingMinHold));
        }

        [Test]
        public void Constructor_SpeakingBaseEnergy_ClampedToUnitRange()
        {
            // Arrange / Act — above 1
            var above = new ConversationFlowTimings(0f, 0f, 0f, 0f, 0f, 0f, 0f, 5f);
            // Arrange / Act — below 0
            var below = new ConversationFlowTimings(0f, 0f, 0f, 0f, 0f, 0f, 0f, -1f);

            // Assert
            Assert.That(above.SpeakingBaseEnergy, Is.LessThanOrEqualTo(1f));
            Assert.That(below.SpeakingBaseEnergy, Is.GreaterThanOrEqualTo(0f));
        }

        [Test]
        public void Default_HasExpectedValues()
        {
            // Arrange / Act
            ConversationFlowTimings d = ConversationFlowTimings.Default;

            // Assert — verify the published defaults from the XML doc
            Assert.That(d.TransitionDuration, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(d.ThinkingMinHold, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(d.ThinkingMaxHold, Is.EqualTo(2.5f).Within(1e-4f));
            Assert.That(d.AttendingGracePeriod, Is.EqualTo(0.3f).Within(1e-4f));
            Assert.That(d.SettlingDuration, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(d.IdleReturnDelay, Is.EqualTo(60f).Within(1e-4f));
            Assert.That(d.InterruptedFreezeDuration, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(d.SpeakingBaseEnergy, Is.EqualTo(0.6f).Within(1e-4f));
        }
    }
}
