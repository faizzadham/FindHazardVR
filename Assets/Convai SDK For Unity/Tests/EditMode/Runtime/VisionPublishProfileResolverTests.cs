using Convai.Runtime.Vision.Publishing;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class VisionPublishProfileResolverTests
    {
        [TestCase(VisionPublishPolicy.AutoCompatible, 10, 750000)]
        [TestCase(VisionPublishPolicy.HighResponsiveness, 15, 1000000)]
        [TestCase(VisionPublishPolicy.LowOverhead, 5, 350000)]
        [TestCase(VisionPublishPolicy.Manual, 10, 750000)]
        public void Resolve_UsesExpectedPolicyDefaults(
            VisionPublishPolicy policy,
            int expectedFrameRate,
            int expectedMaxBitrate)
        {
            VisionPublishProfile profile = VisionPublishProfileResolver.Resolve(policy, 0, 0,
                RuntimePlatform.WindowsPlayer);

            Assert.That(profile.FrameRate, Is.EqualTo(expectedFrameRate));
            Assert.That(profile.MaxBitrate, Is.EqualTo(expectedMaxBitrate));
        }

        [Test]
        public void Resolve_UsesExplicitOverrides_WhenProvided()
        {
            VisionPublishProfile profile = VisionPublishProfileResolver.Resolve(
                VisionPublishPolicy.LowOverhead,
                12,
                900_000,
                RuntimePlatform.WindowsPlayer);

            Assert.That(profile.FrameRate, Is.EqualTo(12));
            Assert.That(profile.MaxBitrate, Is.EqualTo(900_000));
        }
    }
}
