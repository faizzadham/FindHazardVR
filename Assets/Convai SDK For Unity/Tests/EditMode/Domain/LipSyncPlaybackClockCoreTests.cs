using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    [TestFixture]
    public class LipSyncPlaybackClockCoreTests
    {
        [Test]
        public void GetElapsed_WithoutStart_ReturnsZero()
        {
            // Arrange
            PlaybackClockCore core = new();

            // Act
            double elapsed = core.GetElapsed(10d);

            // Assert
            Assert.AreEqual(0d, elapsed, 0.0001d);
        }

        [Test]
        public void PauseAndResume_WithValidOrder_ExcludesPausedDurationFromElapsed()
        {
            // Arrange
            PlaybackClockCore core = new();
            core.Start(0d);
            core.Pause(4d);
            core.Resume(10d);

            // Act
            double elapsed = core.GetElapsed(12d);

            // Assert
            Assert.AreEqual(6d, elapsed, 0.0001d);
        }

        [Test]
        public void Pause_WhenAlreadyPaused_IsIdempotent()
        {
            // Arrange
            PlaybackClockCore core = new();
            core.Start(0d);
            core.Pause(3d);

            // Act
            core.Pause(6d);
            double elapsed = core.GetElapsed(9d);

            // Assert
            Assert.AreEqual(3d, elapsed, 0.0001d);
        }

        [Test]
        public void Resume_WithoutPause_DoesNotAlterElapsedComputation()
        {
            // Arrange
            PlaybackClockCore core = new();
            core.Start(0d);

            // Act
            core.Resume(5d);
            double elapsed = core.GetElapsed(10d);

            // Assert
            Assert.AreEqual(10d, elapsed, 0.0001d);
        }

        [Test]
        public void Reset_AfterStart_ClearsStartedAndPausedState()
        {
            // Arrange
            PlaybackClockCore core = new();
            core.Start(0d);
            core.Pause(1d);

            // Act
            core.Reset();

            // Assert
            Assert.IsFalse(core.IsStarted);
            Assert.IsFalse(core.IsPaused);
            Assert.AreEqual(0d, core.GetElapsed(99d), 0.0001d);
        }
    }
}
