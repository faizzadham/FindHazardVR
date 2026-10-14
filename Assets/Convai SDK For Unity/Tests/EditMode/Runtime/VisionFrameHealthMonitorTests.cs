using Convai.Runtime.Vision.Sources.Internal;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class VisionFrameHealthMonitorTests
    {
        [Test]
        public void RegisterSample_NullSample_PreservesCurrentState()
        {
            var monitor = new VisionFrameHealthMonitor(2, 4);

            monitor.RegisterSample(true);
            VisionFrameHealthSample sample = monitor.RegisterSample(null);

            Assert.That(sample.IsHealthy, Is.True);
            Assert.That(sample.ConsecutiveInvalidFrames, Is.Zero);
            Assert.That(sample.ShouldWarn, Is.False);
            Assert.That(sample.ShouldEscalateError, Is.False);
        }

        [Test]
        public void RegisterSample_RepeatedInvalidSamples_TriggerWarningAndEscalation()
        {
            var monitor = new VisionFrameHealthMonitor(2, 4);

            VisionFrameHealthSample first = monitor.RegisterSample(false);
            VisionFrameHealthSample second = monitor.RegisterSample(false);
            VisionFrameHealthSample third = monitor.RegisterSample(false);
            VisionFrameHealthSample fourth = monitor.RegisterSample(false);

            Assert.That(first.ShouldWarn, Is.False);
            Assert.That(first.ShouldEscalateError, Is.False);

            Assert.That(second.ShouldWarn, Is.True);
            Assert.That(second.ShouldEscalateError, Is.False);

            Assert.That(third.ShouldWarn, Is.False);
            Assert.That(third.ShouldEscalateError, Is.False);

            Assert.That(fourth.ShouldWarn, Is.False);
            Assert.That(fourth.ShouldEscalateError, Is.True);
        }

        [Test]
        public void RegisterSample_HealthySample_ResetsInvalidBurst()
        {
            var monitor = new VisionFrameHealthMonitor(2, 4);

            monitor.RegisterSample(false);
            monitor.RegisterSample(false);
            VisionFrameHealthSample healthy = monitor.RegisterSample(true);
            VisionFrameHealthSample invalidAfterReset = monitor.RegisterSample(false);

            Assert.That(healthy.IsHealthy, Is.True);
            Assert.That(healthy.ConsecutiveInvalidFrames, Is.Zero);
            Assert.That(invalidAfterReset.ConsecutiveInvalidFrames, Is.EqualTo(1));
            Assert.That(invalidAfterReset.ShouldWarn, Is.False);
        }
    }
}
