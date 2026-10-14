using System;
using System.Linq;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Models.LipSync;
using Convai.Infrastructure.Networking;
using Convai.Modules.LipSync;
using Convai.Tests.EditMode.Fixtures;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Behavioral tests for the runtime controller's clock lifecycle and drift correction:
    ///     fresh-clock invariant, offset starts, proportional slew, snap, stall pause/resume, and
    ///     the drift-monitor offset override restore.
    /// </summary>
    [TestFixture]
    public class LipSyncRuntimeControllerTests
    {
        private const float Dt = 1f / 60f;

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private GameObject _host;
        private ManualPlaybackClock _clock;
        private LipSyncRuntimeController _controller;
        private EventHub _eventHub;
        private MockRoomAudioService _roomAudio;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("lipsync-controller-test");
            _clock = new ManualPlaybackClock();
            _controller = new LipSyncRuntimeController(_clock);
            _eventHub = new EventHub(new ImmediateScheduler());
            _roomAudio = new MockRoomAudioService { AudioPlayheadSeconds = 0d };

            _controller.EnsureInitialized(_host.transform, CreateConfig(), null);
            _controller.SetRoomAudioService(_roomAudio);
            _controller.Bind(_eventHub, "char-1", null);
        }

        [TearDown]
        public void TearDown()
        {
            LipSyncDriftMonitor.Enabled = false;
            LipSyncDriftMonitor.TimeOffsetOverrideSeconds = null;
            LipSyncDriftMonitor.Clear();
            _controller.Dispose();
            UnityEngine.Object.DestroyImmediate(_host);
        }

        private static LipSyncRuntimeConfig CreateConfig() => new(
            LipSyncProfileId.ARKit,
            null,
            Array.Empty<SkinnedMeshRenderer>(),
            fadeOutDuration: 0.2f,
            smoothingFactor: 0f,
            timeOffsetSeconds: 0f,
            maxBufferedSeconds: 3f,
            minResumeHeadroomSeconds: 0.12f,
            deliverChunksAhead: true,
            fadeInDuration: 0f);

        /// <summary>
        ///     Runs the engine until its fade-out has finished and it has settled at
        ///     <see cref="PlaybackState.Idle" />.
        /// </summary>
        /// <remarks>
        ///     The configured fade is 0.2 s, so roughly twelve ticks; the cap is generous and the
        ///     assertion names the state it gave up waiting for rather than letting the test fail
        ///     later on a symptom.
        /// </remarks>
        private void TickUntilIdle()
        {
            for (int i = 0; i < 120 && _controller.EngineState != PlaybackState.Idle; i++)
                _controller.Tick(Dt);

            Assert.AreEqual(PlaybackState.Idle, _controller.EngineState,
                "arrange: the interrupted stream's fade should have finished by now");
        }

        private void PublishIndexedChunk(string responseId, int frameCount, int startFrameIndex, int sequence,
            int turnId = 1)
        {
            float[][] frames = new float[frameCount][];
            for (int i = 0; i < frameCount; i++) frames[i] = new[] { i / (float)Math.Max(1, frameCount) };

            var chunk = new LipSyncPackedChunk(
                LipSyncProfileId.ARKit,
                60f,
                new[] { "jawOpen" },
                frames,
                responseId,
                turnId,
                1,
                startFrameIndex,
                sequence);
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1", chunk));
        }

        private void PublishIndexedRampChunk(
            string responseId,
            int timelineFrameCount,
            int startFrameIndex,
            int frameCount,
            int sequence)
        {
            var frames = new float[frameCount][];
            float denominator = Math.Max(1, timelineFrameCount - 1);
            for (int i = 0; i < frameCount; i++)
                frames[i] = new[] { (startFrameIndex + i) / denominator };

            var chunk = new LipSyncPackedChunk(
                LipSyncProfileId.ARKit,
                60f,
                new[] { "jawOpen" },
                frames,
                responseId,
                1,
                1,
                startFrameIndex,
                sequence);
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1", chunk));
        }

        /// <summary>Buffers 2 s of frames, starts audio, and ticks once so the engine is Playing.</summary>
        private void EnterPlaying(string responseId = "r1")
        {
            PublishIndexedChunk(responseId, 120, 0, 1);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _controller.Tick(Dt);
            Assert.AreEqual(PlaybackState.Playing, _controller.EngineState, "arrange: engine should be playing");
        }

        [Test]
        public void Tick_AudioAheadOfClock_SlewsBoundedStepTowardTarget()
        {
            EnterPlaying();

            // Audio playhead 100 ms ahead: inside the snap threshold, so the correction must be
            // a bounded slew step (error * gain capped at the max step), not a jump.
            _roomAudio.AudioPlayheadSeconds = 0.1d;
            _controller.Tick(Dt);

            Assert.IsTrue(_clock.LastRebaseValue.HasValue);
            Assert.AreEqual(0.008d, _clock.LastRebaseValue.Value, 0.0015d);
        }

        [Test]
        public void Tick_AudioFarAheadOfClock_SnapsToTarget()
        {
            EnterPlaying();

            _roomAudio.AudioPlayheadSeconds = 0.5d;
            _controller.Tick(Dt);

            Assert.IsTrue(_clock.LastRebaseValue.HasValue);
            Assert.AreEqual(0.5d, _clock.LastRebaseValue.Value, 0.001d);
        }

        [Test]
        public void Tick_AudioPlayheadFrozen_PausesClock_ThenResumeRebases()
        {
            EnterPlaying();

            // Establish an advancing target first, then freeze it.
            _roomAudio.AudioPlayheadSeconds = 0.05d;
            _controller.Tick(Dt);
            int pausesBefore = _clock.PauseCount;

            for (int i = 0; i < 8; i++) _controller.Tick(Dt); // 8 * 16.7 ms > 80 ms stall threshold
            Assert.Greater(_clock.PauseCount, pausesBefore, "clock should pause on frozen playhead");

            _roomAudio.AudioPlayheadSeconds = 0.3d;
            _controller.Tick(Dt);
            Assert.Greater(_clock.ResumeCount, 0, "clock should resume when playhead advances");
            Assert.AreEqual(0.3d, _clock.LastRebaseValue ?? -1d, 0.001d,
                "resume must re-anchor to the playhead");
        }

        [Test]
        public void Tick_ClockJumpedAheadOfFrozenPlayhead_SnapsBackBeforeEngineSamples()
        {
            // Editor focus loss: the realtime clock source keeps running while the audio device is
            // paused, so on refocus the visual clock has jumped far ahead of the frozen playhead.
            EnterPlaying();
            _roomAudio.AudioPlayheadSeconds = 0.05d;
            _controller.Tick(Dt);

            _clock.SetElapsed(5.05d); // clock ran 5 s ahead while audio stayed at 0.05 s

            _controller.Tick(Dt);

            Assert.AreEqual(0.05d, _clock.LastRebaseValue ?? -1d, 0.001d,
                "clock must snap back to the rendered-audio playhead on the first tick");
            Assert.AreEqual(PlaybackState.Playing, _controller.EngineState,
                "the engine must never sample with the jumped clock (would truncate frames and starve)");
        }

        [Test]
        public void NewStreamAfterHardReset_StartsFreshClock_InsteadOfResumingStaleOne()
        {
            EnterPlaying("r1");
            _clock.SetElapsed(2.3d); // simulate a clock that ran ahead

            _eventHub.Publish(LipSyncTimelineResetRequested.Create(
                "char-1", "participant-1", "r1", 1, 1, 2, null, "interruption"));

            // An interruption fades the mouth closed rather than snapping it shut: the bridge's
            // hard-reset path calls StopSmooth, so the engine winds the old stream down over the
            // configured fade rather than dropping straight to Idle. The fade itself is owned by
            // ConvaiLipSyncBridgeTests; this test starts where it ends.
            Assert.AreEqual(PlaybackState.FadingOut, _controller.EngineState);
            TickUntilIdle();

            // Residual audio-start with no stream must not start the clock.
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _controller.Tick(Dt);
            int startCountBefore = _clock.StartCount;

            // The next response carries a new turn id, matching real backend turn numbering.
            PublishIndexedChunk("r2", 120, 0, 1, turnId: 2);
            _controller.Tick(Dt);

            Assert.AreEqual(startCountBefore + 1, _clock.StartCount, "new stream must Start a fresh clock");
            Assert.Less(_clock.ElapsedSeconds, 0.5d,
                "fresh stream must not inherit the stale 2.3s elapsed");
            Assert.AreEqual(PlaybackState.Playing, _controller.EngineState);
        }

        [Test]
        public void GateOpen_AfterAudioAlreadyPlaying_StartsClockAtAudioOffset()
        {
            // Chunk first (owner adopted with no audio), then audio that has already played 0.25s
            // by the time the gate opens: the clock must start at that offset, not zero.
            PublishIndexedChunk("r1", 120, 0, 1);
            _roomAudio.AudioPlayheadSeconds = 0.25d;
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _controller.Tick(Dt);

            Assert.AreEqual(0.25d, _clock.LastStartOffset ?? -1d, 0.01d);
            Assert.AreEqual(0.25d, _clock.ElapsedSeconds, 0.01d);
        }

        [Test]
        public void TimeOffsetOverride_IsRestored_EvenWhenMonitorDisabledFirst()
        {
            EnterPlaying();
            float baseRemaining = _controller.GetTalkingTimeRemaining();

            LipSyncDriftMonitor.Enabled = true;
            LipSyncDriftMonitor.TimeOffsetOverrideSeconds = 0.5f;
            _controller.Tick(Dt);
            Assert.AreEqual(baseRemaining - 0.5f, _controller.GetTalkingTimeRemaining(), 0.02f,
                "override should shift the engine time offset");

            // Regression: the window disables the monitor AND clears the override; the restore
            // must still be applied even though monitoring is off.
            LipSyncDriftMonitor.Enabled = false;
            LipSyncDriftMonitor.TimeOffsetOverrideSeconds = null;
            _controller.Tick(Dt);
            Assert.AreEqual(baseRemaining, _controller.GetTalkingTimeRemaining(), 0.02f,
                "base config must be restored after the override is cleared");
        }

        [TestCase(8)]
        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        [TestCase(300)]
        public void Tick_SampleLockedTimeline_HasZeroCumulativeDrift(int durationSeconds)
        {
            const int sampleRate = 48000;
            const long baseline = 5000;
            int tickCount = durationSeconds * 60;
            int timelineFrameCount = tickCount + 2;
            var random = new System.Random(0x51ED + durationSeconds);
            int startFrame = 0;
            int sequence = 1;
            while (startFrame < timelineFrameCount)
            {
                int frameCount = Math.Min(random.Next(1, 97), timelineFrameCount - startFrame);
                PublishIndexedRampChunk(
                    "sample-locked",
                    timelineFrameCount,
                    startFrame,
                    frameCount,
                    sequence++);
                startFrame += frameCount;
            }

            _roomAudio.AudioTimeline = new AudioTimelineSnapshot(
                baseline, sampleRate, 1, 1, 0, AudioTimelinePlaybackState.Playing,
                baseline, baseline, 0, 0, 0);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            var signedErrors = new double[tickCount];
            for (int tick = 0; tick < tickCount; tick++)
            {
                double expectedSeconds = (tick + 1) / 60d;
                long sourceFrame = baseline + ((tick + 1L) * (sampleRate / 60));
                _roomAudio.AudioTimeline = new AudioTimelineSnapshot(
                    sourceFrame, sampleRate, 1, 1, 0, AudioTimelinePlaybackState.Playing,
                    sourceFrame, sourceFrame, 0, 0, 0);

                _controller.Tick(Dt);
                double renderedSeconds = _controller.GetBlendshapeSnapshot().GetValue(0) *
                                         ((timelineFrameCount - 1) / 60d);
                signedErrors[tick] = renderedSeconds - expectedSeconds;
            }

            const int settlingTicks = 60;
            double[] settledErrors = signedErrors.Skip(Math.Min(settlingTicks, signedErrors.Length - 1)).ToArray();
            double slope = ComputeLinearRegressionSlope(settledErrors, Dt);
            double[] absoluteErrors = settledErrors.Select(Math.Abs).OrderBy(error => error).ToArray();
            double p95 = absoluteErrors[(int)Math.Floor((absoluteErrors.Length - 1) * 0.95d)];
            double max = absoluteErrors[absoluteErrors.Length - 1];

            Assert.That(Math.Abs(slope), Is.LessThan(0.0001d),
                $"{durationSeconds}s response accumulated drift at {slope * 1000d:F4} ms/s");
            Assert.That(p95, Is.LessThanOrEqualTo(0.04d));
            Assert.That(max, Is.LessThanOrEqualTo(0.08d));
        }

        [TestCase(120)]
        [TestCase(300)]
        public void Tick_BrowserMediaTimeline_HasZeroCumulativeDrift(int durationSeconds)
        {
            const double baseline = 100d;
            int tickCount = durationSeconds * 60;
            int timelineFrameCount = tickCount + 2;
            var random = new System.Random(0xB05E + durationSeconds);
            int startFrame = 0;
            int sequence = 1;
            while (startFrame < timelineFrameCount)
            {
                int frameCount = Math.Min(random.Next(1, 97), timelineFrameCount - startFrame);
                PublishIndexedRampChunk(
                    "browser-media-locked",
                    timelineFrameCount,
                    startFrame,
                    frameCount,
                    sequence++);
                startFrame += frameCount;
            }

            _roomAudio.AudioPlayheadSeconds = null;
            _roomAudio.AudioMediaTimeline = new AudioMediaTimelineSnapshot(
                baseline,
                AudioTimelinePlaybackState.Playing,
                baseline,
                signalGeneration: 1,
                analyserAvailable: true);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            var signedErrors = new double[tickCount];
            for (int tick = 0; tick < tickCount; tick++)
            {
                double expectedSeconds = (tick + 1) / 60d;
                _roomAudio.AudioMediaTimeline = new AudioMediaTimelineSnapshot(
                    baseline + expectedSeconds,
                    AudioTimelinePlaybackState.Playing,
                    baseline,
                    signalGeneration: 1,
                    analyserAvailable: true);

                _controller.Tick(Dt);
                double renderedSeconds = _controller.GetBlendshapeSnapshot().GetValue(0) *
                                         ((timelineFrameCount - 1) / 60d);
                signedErrors[tick] = renderedSeconds - expectedSeconds;
            }

            const int settlingTicks = 60;
            double[] settledErrors = signedErrors.Skip(Math.Min(settlingTicks, signedErrors.Length - 1)).ToArray();
            double slope = ComputeLinearRegressionSlope(settledErrors, Dt);
            double[] absoluteErrors = settledErrors.Select(Math.Abs).OrderBy(error => error).ToArray();
            double p95 = absoluteErrors[(int)Math.Floor((absoluteErrors.Length - 1) * 0.95d)];
            double max = absoluteErrors[absoluteErrors.Length - 1];

            Assert.That(Math.Abs(slope), Is.LessThan(0.0001d),
                $"{durationSeconds}s WebGL response accumulated drift at {slope * 1000d:F4} ms/s");
            Assert.That(p95, Is.LessThanOrEqualTo(0.04d));
            Assert.That(max, Is.LessThanOrEqualTo(0.08d));
        }

        private static double ComputeLinearRegressionSlope(double[] errors, double sampleIntervalSeconds)
        {
            double meanX = (errors.Length - 1) * sampleIntervalSeconds * 0.5d;
            double meanY = errors.Average();
            double covariance = 0d;
            double variance = 0d;

            for (int i = 0; i < errors.Length; i++)
            {
                double centeredX = (i * sampleIntervalSeconds) - meanX;
                covariance += centeredX * (errors[i] - meanY);
                variance += centeredX * centeredX;
            }

            return variance > 0d ? covariance / variance : 0d;
        }
    }
}
