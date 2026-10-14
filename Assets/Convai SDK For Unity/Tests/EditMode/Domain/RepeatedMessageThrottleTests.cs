using System;
using Convai.Domain.Logging;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    /// <summary>
    ///     The difference between a throttle and a mute, pinned.
    /// </summary>
    /// <remarks>
    ///     The action-response handler had a mute for a long time and it read as a throttle: a set of
    ///     signatures already reported, never cleared, for the whole connection. Every assertion here
    ///     is one of the things that made it undiagnosable — the second attempt answered with
    ///     silence, and a single line standing for forty occurrences.
    /// </remarks>
    [TestFixture]
    public sealed class RepeatedMessageThrottleTests
    {
        [Test]
        public void TrackingIsBoundedExpiresAndResetsOnConfigurationChanges()
        {
            var throttle = new RepeatedMessageThrottle(30);
            for (int i = 0; i < 10000; i++) throttle.ShouldSay(i.ToString(), i / 10000d, out _);
            Assert.That(throttle.Count, Is.EqualTo(1024));
            throttle.ShouldSay("new", 302, out _);
            Assert.That(throttle.Count, Is.EqualTo(1));
            Assert.That(throttle.ShouldSay("new", 303, out _), Is.False);
            RepeatedMessageThrottle.ConfigurationChanged();
            Assert.That(throttle.ShouldSay("new", 304, out int suppressed), Is.True);
            Assert.That(suppressed, Is.Zero);
        }

        private const double Start = 1000;

        [Test]
        public void TheFirstOccurrenceIsAlwaysSaid()
        {
            var throttle = new RepeatedMessageThrottle(30d);

            Assert.That(throttle.ShouldSay("a", Start, out int suppressed), Is.True);
            Assert.That(suppressed, Is.Zero, "Nothing was held back before the first time.");
        }

        [Test]
        public void ARepeatInsideTheIntervalIsHeldBack()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("a", (Start + 29), out _), Is.False,
                "A fault the Convai Character repeats every turn must not be said every turn.");
        }

        /// <summary>
        ///     The assertion the old mute could never satisfy.
        /// </summary>
        [Test]
        public void TheSameFaultIsSaidAgainOnceTheIntervalHasPassed()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("a", (Start + 30), out _), Is.True,
                "Somebody changed something and tried again. Silence reads as success.");
        }

        [Test]
        public void HowManyWereHeldBackIsReportedWithTheNextLine()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);
            throttle.ShouldSay("a", (Start + 1), out _);
            throttle.ShouldSay("a", (Start + 2), out _);

            Assert.That(throttle.ShouldSay("a", (Start + 31), out int suppressed), Is.True);
            Assert.That(suppressed, Is.EqualTo(2),
                "One line standing for three occurrences without saying so is the difference " +
                "between a fluke and the thing to fix next.");
        }

        [Test]
        public void TheCountDescribesTheGapThatJustEndedRatherThanEveryGap()
        {
            var throttle = new RepeatedMessageThrottle(10d);
            throttle.ShouldSay("a", Start, out _);
            throttle.ShouldSay("a", (Start + 1), out _);
            throttle.ShouldSay("a", (Start + 10), out int firstGap);
            throttle.ShouldSay("a", (Start + 20), out int secondGap);

            Assert.That(firstGap, Is.EqualTo(1));
            Assert.That(secondGap, Is.Zero, "Nothing was held back during the second gap.");
        }

        [Test]
        public void DistinctFaultsAreThrottledIndependently()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("b", Start, out _), Is.True,
                "Two different faults in one batch are two different things to fix.");
        }

        [Test]
        public void ResetMakesEveryFaultSpeakAgain()
        {
            var throttle = new RepeatedMessageThrottle(30d);
            throttle.ShouldSay("a", Start, out _);
            throttle.Reset();

            Assert.That(throttle.ShouldSay("a", (Start + 1), out int suppressed), Is.True);
            Assert.That(suppressed, Is.Zero, "A reset forgets the gap as well as the fault.");
        }

        [Test]
        public void AZeroIntervalSaysEveryOccurrence()
        {
            var throttle = new RepeatedMessageThrottle(0d);
            throttle.ShouldSay("a", Start, out _);

            Assert.That(throttle.ShouldSay("a", Start, out _), Is.True);
        }

        [Test]
        public void ANegativeIntervalIsTreatedAsZeroRatherThanAsForever()
        {
            var throttle = new RepeatedMessageThrottle(-5d);

            Assert.That(throttle.IntervalSeconds, Is.Zero,
                "Clamping upward would be a mute reachable by a typo.");
        }

        [Test]
        public void AnEmptyKeyIsNeverSaid()
        {
            var throttle = new RepeatedMessageThrottle(30d);

            Assert.That(throttle.ShouldSay(null, Start, out _), Is.False);
            Assert.That(throttle.ShouldSay(string.Empty, Start, out _), Is.False);
        }
    }
}
