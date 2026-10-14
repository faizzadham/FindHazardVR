using Convai.Runtime.Networking.Media;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class CharacterAudibleHoldoffTests
    {
        // Clock values are frame times, so the assertions sit a little either side of the holdoff
        // rather than exactly on it, where binary rounding of the sums decides the answer.
        private const double JustBefore = CharacterAudibleHoldoff.HoldoffSeconds - 0.01;
        private const double JustAfter = CharacterAudibleHoldoff.HoldoffSeconds + 0.01;

        [Test]
        public void ShouldProcess_FalseUntilHoldoffElapsed()
        {
            var holdoff = new CharacterAudibleHoldoff();
            const double start = 10d;

            Assert.That(holdoff.ShouldProcess(true, start), Is.False);
            Assert.That(holdoff.ShouldProcess(true, start + JustBefore), Is.False);
            Assert.That(holdoff.ShouldProcess(true, start + JustAfter), Is.True);
        }

        [Test]
        public void ShouldProcess_ResetsWhenCharacterGoesSilent()
        {
            var holdoff = new CharacterAudibleHoldoff();

            Assert.That(holdoff.ShouldProcess(true, 0d), Is.False);
            Assert.That(holdoff.ShouldProcess(true, JustAfter), Is.True);
            Assert.That(holdoff.ShouldProcess(false, JustAfter + 1d), Is.False);

            const double next = 5d;
            Assert.That(holdoff.ShouldProcess(true, next), Is.False,
                "A new character turn must serve its own holdoff.");
            Assert.That(holdoff.ShouldProcess(true, next + JustBefore), Is.False);
            Assert.That(holdoff.ShouldProcess(true, next + JustAfter), Is.True);
        }
    }
}
