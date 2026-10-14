using System.Collections.Generic;
using Convai.Modules.LipSync;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class LipSyncPlaybackEngineTests
    {
        [SetUp]
        public void SetUp()
        {
            _engine = new LipSyncPlaybackEngine(new LipSyncEngineConfig(
                smoothingFactor: 0f,
                timeOffsetSeconds: 0f,
                fadeOutDuration: 0.1f));
            _clock = new ManualPlaybackClock();
        }

        private LipSyncPlaybackEngine _engine;
        private ManualPlaybackClock _clock;

        [Test]
        public void BeginStream_WithValidInputs_TransitionsEngineToBuffering()
        {
            // Arrange & Act
            _engine.BeginStream(new[] { "A", "B" }, 60f);

            // Assert
            Assert.AreEqual(PlaybackState.Buffering, _engine.State);
        }

        [Test]
        public void Tick_BeforeAudioStartSignal_RemainsBufferingAndReturnsNotUpdated()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(10);
            _clock.SetElapsed(0.02d);

            // Act
            bool updated = _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, _engine.State);
        }

        [Test]
        public void Tick_AfterAudioStartSignal_TransitionsToPlayingAndUpdatesOutput()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(10);
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0.02d);

            // Act
            bool updated = _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void Tick_WithInsufficientInitialHeadroom_RemainsBufferingUntilMoreFramesArrive()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(4);
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0d);

            // Act
            bool updated = _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, _engine.State);
        }

        [Test]
        public void Tick_AfterInsufficientInitialHeadroom_WhenMoreFramesArrive_TransitionsToPlaying()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(4);
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);
            FeedRamp(8);

            // Act
            bool updated = _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void Tick_WhenPlaybackPassesBufferWithoutStreamEnd_TransitionsToStarving()
        {
            // Arrange
            StartAndEnterPlayingState();

            // Act
            _clock.SetElapsed(10d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.AreEqual(PlaybackState.Starving, _engine.State);
        }

        [Test]
        public void Tick_WhenPlaybackPassesBufferAfterStreamEnd_TransitionsToFadingOut()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(10);
            _engine.NotifyStreamEnd();
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0.02d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Act
            _clock.SetElapsed(10d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void Tick_DuringFadeOut_CompletesAndTransitionsBackToIdle()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(10);
            _engine.NotifyStreamEnd();
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0.02d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);
            _clock.SetElapsed(10d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Act
            TickEngineMultipleFrames(30);

            // Assert
            Assert.AreEqual(PlaybackState.Idle, _engine.State);
        }

        [Test]
        public void Stop_WhenPlaying_ResetsToIdleAndClearsOutput()
        {
            // Arrange
            StartAndEnterPlayingState();

            // Act
            _engine.Stop();

            // Assert
            Assert.AreEqual(PlaybackState.Idle, _engine.State);
            Assert.AreEqual(0f, _engine.OutputValues[0], 0.0001f);
        }

        [Test]
        public void Stop_WhenCalledTwice_RemainsIdempotent()
        {
            // Arrange
            StartAndEnterPlayingState();

            // Act
            _engine.Stop();
            _engine.Stop();

            // Assert
            Assert.AreEqual(PlaybackState.Idle, _engine.State);
        }

        [Test]
        public void StopSmooth_WhenPlaying_TransitionsToFadingOut()
        {
            // Arrange
            StartAndEnterPlayingState();

            // Act
            _engine.StopSmooth();

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void SettleToRest_WhenPlaying_SettlesAndReachesIdleWithinItsDuration()
        {
            StartAndEnterPlayingState();

            _engine.SettleToRest();

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
            Assert.IsTrue(_engine.IsSettling, "the natural end is a settle, not the timed cut");

            int ticks = 0;
            double elapsed = _clock.ElapsedSeconds;
            while (_engine.State == PlaybackState.FadingOut && ticks < 60)
            {
                elapsed += 1d / 60d;
                _clock.SetElapsed(elapsed);
                _engine.Tick(elapsed, 1f / 60f);
                ticks++;
            }

            Assert.AreEqual(PlaybackState.Idle, _engine.State);
            Assert.That(ticks, Is.LessThanOrEqualTo(33), "default settle runs 1.5 x 0.35 s, about 32 frames");
        }

        [Test]
        public void StopSmooth_WhenPlaying_IsTheTimedCutNotASettle()
        {
            StartAndEnterPlayingState();

            _engine.StopSmooth();

            Assert.IsFalse(_engine.IsSettling);
        }

        [Test]
        public void StateChanged_WhenStateTransitions_FiresSingleTransitionEvent()
        {
            // Arrange
            List<(PlaybackState from, PlaybackState to)> transitions = new();
            _engine.StateChanged += (from, to) => transitions.Add((from, to));

            // Act
            _engine.BeginStream(new[] { "A" }, 60f);

            // Assert
            Assert.AreEqual(1, transitions.Count);
            Assert.AreEqual(PlaybackState.Idle, transitions[0].from);
            Assert.AreEqual(PlaybackState.Buffering, transitions[0].to);
        }

        [Test]
        public void GetRemainingSeconds_WhilePlaying_ReturnsPositiveValue()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(60);
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0.2d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Act
            float remaining = _engine.GetRemainingSeconds(_clock.ElapsedSeconds);

            // Assert
            Assert.Greater(remaining, 0f);
        }

        [Test]
        public void OutputValues_AfterBeginStream_MatchesChannelCount()
        {
            // Arrange & Act
            _engine.BeginStream(new[] { "A", "B", "C" }, 60f);

            // Assert
            Assert.AreEqual(3, _engine.OutputValues.Length);
        }

        [Test]
        public void StarvingState_WhenNewFramesRestoreHeadroom_TransitionsBackToPlaying()
        {
            // Arrange
            StartAndEnterPlayingState();
            _clock.SetElapsed(0.5d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);
            FeedRamp(60);

            // Act
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        [Description("Regression: stream-end flag must survive late data while starving and still produce fade-out.")]
        public void StreamEndAfterStarving_WithLateData_StillTransitionsToFadingOut()
        {
            // Arrange
            StartAndEnterPlayingState();
            _clock.SetElapsed(0.5d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);
            _engine.NotifyStreamEnd();
            FeedRamp(5);

            // Act
            _clock.SetElapsed(10d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void NotifyAudioPlaybackStopped_WhenPlaying_TransitionsToFadingOut()
        {
            // Arrange
            StartAndEnterPlayingState();

            // Act
            _engine.NotifyAudioPlaybackStopped();

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void NotifyAudioPlaybackStarted_WhenFadingOutWithBuffer_ResumesPlaying()
        {
            // Arrange
            StartAndEnterPlayingState();
            _engine.NotifyAudioPlaybackStopped();
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);

            // Act
            _engine.NotifyAudioPlaybackStarted();

            // Assert
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void FeedFrames_DuringFadingOut_DoesNotRestartPlayback()
        {
            // Arrange
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(10);
            _engine.NotifyStreamEnd();
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0.02d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);
            _clock.SetElapsed(10d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Act
            FeedRamp(30);

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [TestCase(0d)]
        [TestCase(1.5d / 60d)]
        [TestCase(3d / 60d)]
        public void OutputSampling_WithCatmullRomInterpolation_StaysWithinNormalizedRange(double elapsed)
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(smoothingFactor: 0f, timeOffsetSeconds: 0f));
            engine.BeginStream(new[] { "V" }, 60f);
            engine.FeedFrames(new[] { new[] { 0f }, new[] { 0f }, new[] { 1f }, new[] { 1f } });
            engine.NotifyAudioPlaybackStarted();

            // Act
            engine.Tick(elapsed, 1f / 60f);
            float output = engine.OutputValues[0];

            // Assert
            Assert.That(output, Is.InRange(0f, 1f));
        }

        [Test]
        public void Stop_AfterRestartingStream_RequiresNewAudioPlaybackStartGate()
        {
            // Arrange
            StartAndEnterPlayingState();
            _engine.Stop();
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(10);
            _clock.SetElapsed(0.02d);

            // Act
            bool updated = _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);

            // Assert
            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, _engine.State);
        }

        [Test]
        public void Tick_WithSendAheadRetention_SamplesStartAndTailBeyondConfiguredBuffer()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(
                smoothingFactor: 0f,
                timeOffsetSeconds: 0f,
                maxBufferedSeconds: 1f,
                minResumeHeadroomSeconds: 0.05f,
                retainFutureFrames: true));
            engine.BeginStream(new[] { "A" }, 60f);
            engine.FeedFrames(CreateRampFrames(360));
            engine.NotifyAudioPlaybackStarted();

            // Act
            bool startUpdated = engine.Tick(0.02d, 1f / 60f);
            float startValue = engine.OutputValues[0];
            bool tailUpdated = engine.Tick(5.8d, 1f / 60f);
            float tailValue = engine.OutputValues[0];

            // Assert
            Assert.IsTrue(startUpdated);
            Assert.IsTrue(tailUpdated);
            Assert.Less(startValue, 0.1f);
            Assert.Greater(tailValue, 0.8f);
        }

        [Test]
        public void TimelineRebaseBlend_SoftensDiscontinuityAndConvergesWithinEightyMilliseconds()
        {
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(
                smoothingFactor: 0f,
                timeOffsetSeconds: 0f,
                minResumeHeadroomSeconds: 0f,
                retainFutureFrames: true));
            engine.BeginStream(new[] { "A" }, 60f);
            var frames = new float[60][];
            for (int i = 0; i < frames.Length; i++) frames[i] = new[] { i < 30 ? 0f : 1f };
            engine.FeedFrames(frames);
            engine.NotifyAudioPlaybackStarted();
            engine.Tick(0d, 1f / 60f);

            engine.BeginTimelineRebaseBlend(0.5d, 0.08f);
            engine.Tick(0.5d, 1f / 60f);
            float atJump = engine.OutputValues[0];
            engine.Tick(0.54d, 1f / 60f);
            float halfway = engine.OutputValues[0];
            engine.Tick(0.58d, 1f / 60f);
            float settled = engine.OutputValues[0];

            Assert.AreEqual(0f, atJump, 0.0001f);
            Assert.That(halfway, Is.InRange(0.4f, 0.6f));
            Assert.AreEqual(1f, settled, 0.0001f);
        }

        private void StartAndEnterPlayingState()
        {
            _engine.BeginStream(new[] { "A" }, 60f);
            FeedRamp(10);
            _engine.NotifyAudioPlaybackStarted();
            _clock.SetElapsed(0.02d);
            _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);
        }

        private void FeedRamp(int count)
        {
            _engine.FeedFrames(CreateRampFrames(count));
        }

        private static float[][] CreateRampFrames(int count)
        {
            float[][] frames = new float[count][];
            for (int i = 0; i < count; i++) frames[i] = new[] { (float)i / count };
            return frames;
        }

        private void TickEngineMultipleFrames(int frameCount)
        {
            for (int i = 0; i < frameCount; i++) _engine.Tick(_clock.ElapsedSeconds, 1f / 60f);
        }
    }
}
