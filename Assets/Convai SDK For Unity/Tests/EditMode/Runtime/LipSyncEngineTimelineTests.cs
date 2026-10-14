using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Covers the absolute-timeline send-ahead engine behavior: frames placed at
    ///     start_frame_index/fps positions, truncation on partial cancel, and the initial
    ///     headroom bypass for send-ahead streams.
    /// </summary>
    [TestFixture]
    public class LipSyncEngineTimelineTests
    {
        private static LipSyncEngineConfig SendAheadConfig(float minResumeHeadroomSeconds = 0.05f) => new(
            smoothingFactor: 0f,
            timeOffsetSeconds: 0f,
            maxBufferedSeconds: 1f,
            minResumeHeadroomSeconds: minResumeHeadroomSeconds,
            retainFutureFrames: true,
            fadeInDuration: 0f);

        [Test]
        public void FeedFramesAt_PlacesFramesAtAbsoluteTimelinePosition()
        {
            LipSyncPlaybackEngine engine = new(SendAheadConfig());
            engine.BeginStream(new[] { "A" }, 60f, sendAheadTimeline: true);
            // 60 frames ramping 0..~1 whose first frame sits at t = 1.0s on the turn timeline.
            engine.FeedFramesAt(CreateRampFrames(60), 1.0d);
            engine.NotifyAudioPlaybackStarted();

            bool updated = engine.Tick(1.5d, 1f / 60f);

            Assert.IsTrue(updated);
            // t = 1.5s is frame 30 of the ramp (value 30/60 = 0.5).
            Assert.AreEqual(0.5f, engine.OutputValues[0], 0.02f);
        }

        [Test]
        public void TruncateAfter_KeepsReleasedTailAndFadesAtBoundary()
        {
            LipSyncPlaybackEngine engine = new(SendAheadConfig());
            engine.BeginStream(new[] { "A" }, 60f, sendAheadTimeline: true);
            engine.FeedFramesAt(CreateRampFrames(120), 0d);
            engine.NotifyAudioPlaybackStarted();
            engine.Tick(0.02d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, engine.State);

            engine.TruncateAfter(1.0d);

            // The kept tail (through 1.0s) still plays...
            bool midUpdated = engine.Tick(0.5d, 1f / 60f);
            Assert.IsTrue(midUpdated);
            Assert.AreEqual(PlaybackState.Playing, engine.State);

            // ...and past the boundary the stream ends with a fade instead of starving.
            engine.Tick(1.5d, 1f / 60f);
            Assert.AreEqual(PlaybackState.FadingOut, engine.State);
        }

        [Test]
        public void BeginStream_SendAheadTimeline_BypassesInitialHeadroomWait()
        {
            LipSyncPlaybackEngine engine = new(SendAheadConfig(minResumeHeadroomSeconds: 0.5f));
            engine.BeginStream(new[] { "A" }, 60f, sendAheadTimeline: true);
            engine.FeedFramesAt(CreateRampFrames(6), 0d);
            engine.NotifyAudioPlaybackStarted();

            bool updated = engine.Tick(0.02d, 1f / 60f);

            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void BeginStream_WithoutSendAhead_KeepsInitialHeadroomWait()
        {
            LipSyncPlaybackEngine engine = new(SendAheadConfig(minResumeHeadroomSeconds: 0.5f));
            engine.BeginStream(new[] { "A" }, 60f);
            engine.FeedFrames(CreateRampFrames(6));
            engine.NotifyAudioPlaybackStarted();

            bool updated = engine.Tick(0.02d, 1f / 60f);

            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, engine.State);
        }

        [Test]
        public void FadeIn_BlendsFromPriorPoseTowardSampledFrames()
        {
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(
                smoothingFactor: 0f,
                timeOffsetSeconds: 0f,
                retainFutureFrames: true,
                fadeInDuration: 0.2f));
            engine.BeginStream(new[] { "A" }, 60f, sendAheadTimeline: true);
            // Constant 1.0 frames; the displayed pose before playback is 0.
            float[][] frames = new float[60][];
            for (int i = 0; i < 60; i++) frames[i] = new[] { 1f };
            engine.FeedFramesAt(frames, 0d);
            engine.NotifyAudioPlaybackStarted();

            engine.Tick(0.02d, 1f / 60f);
            float atStart = engine.OutputValues[0];
            engine.Tick(0.12d, 1f / 60f);
            float midFade = engine.OutputValues[0];
            engine.Tick(0.5d, 1f / 60f);
            float afterFade = engine.OutputValues[0];

            Assert.Less(atStart, 0.1f, "first played frame should still be near the prior pose");
            Assert.AreEqual(0.5f, midFade, 0.1f, "half-way through the ramp the pose should be blended");
            Assert.AreEqual(1f, afterFade, 0.01f, "fade-in must complete by its configured duration");
        }

        private static float[][] CreateRampFrames(int count)
        {
            float[][] frames = new float[count][];
            for (int i = 0; i < count; i++) frames[i] = new[] { i / (float)count };
            return frames;
        }
    }
}
