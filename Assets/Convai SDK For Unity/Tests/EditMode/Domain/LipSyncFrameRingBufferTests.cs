using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    [TestFixture]
    public class LipSyncFrameRingBufferTests
    {
        [Test]
        public void AppendFrames_AfterLongIngress_RemainsBoundedByConfiguredDuration()
        {
            // Arrange
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "A", "B", "C", "D" });

            // Act
            AppendStressData(buffer, 2048);

            // Assert
            Assert.IsTrue(buffer.HasContent);
            Assert.AreEqual(4, buffer.ChannelCount);
            Assert.LessOrEqual(buffer.FrameCount, 120);
            Assert.LessOrEqual(buffer.Duration, 2.01f);
        }

        [Test]
        public void AppendFrames_WhenRetainingFutureFrames_PreservesFullSendAheadWindow()
        {
            // Arrange
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "A" });

            // Act
            buffer.AppendFrames(CreateRampFrames(360), 0f, 60f, 1f, retainFutureFrames: true);

            // Assert
            Assert.AreEqual(360, buffer.FrameCount);
            Assert.Less(buffer.StartTime, 0.001f);
            Assert.Greater(buffer.EndTime, 5.9f);
        }

        [Test]
        public void AppendFrames_WhenRetainingFiveMinuteResponse_PreservesFrameZero()
        {
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "A" });
            float[][] oneSecond = CreateRampFrames(60);

            for (int second = 0; second < 300; second++)
                buffer.AppendFrames(oneSecond, second, 60f, 1f, retainFutureFrames: true);

            Assert.AreEqual(300 * 60, buffer.FrameCount);
            Assert.Less(buffer.StartTime, 0.001f);
            Assert.Greater(buffer.EndTime, 299.9f);
        }

        [Test]
        public void AppendFrames_WhenIndexedResponseExceedsHardCap_PreservesNewest300Seconds()
        {
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "A" });
            float[][] oneSecond = CreateRampFrames(60);

            for (int second = 0; second < 301; second++)
                buffer.AppendFrames(oneSecond, second, 60f, 1f, retainFutureFrames: true);

            Assert.AreEqual((300 * 60) + 2, buffer.FrameCount);
            Assert.Greater(buffer.StartTime, 0.9f);
            Assert.Greater(buffer.EndTime, 300.9f);
        }

        [Test]
        public void PruneBefore_AfterPlaybackAdvances_KeepsNeighborFramesForInterpolation()
        {
            // Arrange
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "A" });
            buffer.AppendFrames(CreateRampFrames(360), 0f, 60f, 1f, retainFutureFrames: true);

            // Act
            int removed = buffer.PruneBefore(3f);

            // Assert
            Assert.Greater(removed, 0);
            Assert.Less(buffer.FrameCount, 360);
            Assert.Less(buffer.StartTime, 3f);
            Assert.IsTrue(buffer.TryGetFrameWindow(3d, out _, out _, out _, out _, out _));
        }

        [Test]
        public void SetChannelLayout_WithDifferentLayout_ClearsExistingFrames()
        {
            // Arrange
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "A", "B" });
            buffer.AppendFrames(
                new[] { new[] { 0.1f, 0.2f }, new[] { 0.3f, 0.4f } },
                0f,
                60f,
                2f);

            // Act
            buffer.SetChannelLayout(new[] { "A", "B", "C" });

            // Assert
            Assert.AreEqual(0, buffer.FrameCount);
        }

        [Test]
        public void SetChannelLayout_WithSameLayout_PreservesExistingFrames()
        {
            // Arrange
            FrameRingBuffer buffer = new();
            string[] layout = { "A", "B" };
            buffer.SetChannelLayout(layout);
            buffer.AppendFrames(new[] { new[] { 0.1f, 0.2f } }, 0f, 60f, 2f);

            // Act
            buffer.SetChannelLayout(layout);

            // Assert
            Assert.AreEqual(1, buffer.FrameCount);
        }

        [Test]
        public void SetChannelLayout_WithNullLayout_ResetsStateToEmpty()
        {
            // Arrange
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "A" });
            buffer.AppendFrames(new[] { new[] { 0.5f } }, 0f, 60f, 2f);

            // Act
            buffer.SetChannelLayout(null);

            // Assert
            Assert.AreEqual(0, buffer.ChannelCount);
            Assert.IsFalse(buffer.HasContent);
        }

        [TestCase(-1d)]
        [TestCase(0d)]
        [TestCase(10d)]
        public void TryGetFrameWindow_WithEdgeElapsedValues_ReturnsStableWindow(double elapsed)
        {
            // Arrange
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "X" });
            buffer.AppendFrames(new[] { new[] { 0.5f } }, 0f, 60f, 2f);

            // Act
            bool found = buffer.TryGetFrameWindow(elapsed, out float[] p0, out float[] p1, out float[] p2,
                out float[] p3, out float alpha);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(0.5f, p0[0], 0.0001f);
            Assert.AreEqual(0.5f, p1[0], 0.0001f);
            Assert.AreEqual(0.5f, p2[0], 0.0001f);
            Assert.AreEqual(0.5f, p3[0], 0.0001f);
            Assert.That(alpha, Is.InRange(0f, 1f));
        }

        [Test]
        public void TryGetFrameWindow_WithMidSegmentElapsed_ProvidesNeighborFramesAndAlpha()
        {
            // Arrange
            FrameRingBuffer buffer = new();
            buffer.SetChannelLayout(new[] { "V" });
            buffer.AppendFrames(
                new[] { new[] { 0.0f }, new[] { 0.2f }, new[] { 0.4f }, new[] { 0.6f }, new[] { 0.8f } },
                0f,
                60f,
                2f);

            // Act
            bool found = buffer.TryGetFrameWindow(2d / 60d, out float[] p0, out float[] p1, out float[] p2,
                out float[] p3, out float alpha);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(0.2f, p0[0], 0.0001f);
            Assert.AreEqual(0.4f, p1[0], 0.0001f);
            Assert.AreEqual(0.6f, p2[0], 0.0001f);
            Assert.AreEqual(0.8f, p3[0], 0.0001f);
            Assert.That(alpha, Is.InRange(0f, 1f));
        }

        private static void AppendStressData(FrameRingBuffer buffer, int chunkCount)
        {
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                float[][] frames = new float[6][];
                for (int i = 0; i < frames.Length; i++)
                {
                    float v = (chunkIndex + i) % 100 / 100f;
                    frames[i] = new[] { v, v * 0.8f, v * 0.6f, v * 0.4f };
                }

                float start = chunkIndex * 6f / 60f;
                buffer.AppendFrames(frames, start, 60f, 2f);
            }
        }

        private static float[][] CreateRampFrames(int count)
        {
            float[][] frames = new float[count][];
            for (int i = 0; i < count; i++) frames[i] = new[] { i / (float)count };
            return frames;
        }
    }
}
