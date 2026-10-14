using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Domain.Models.LipSync;
using Convai.Infrastructure.Networking;
using Convai.Modules.LipSync;
using Convai.Runtime.Animation;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Integration
{
    /// <summary>
    ///     Covers the response-owned audio-timeline behavior of the bridge: owner-matched turn
    ///     stats with a grace window, valid_through_frame_index truncation, audio timeline anchors,
    ///     and the measured-playhead target used for clock start offset and drift correction.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class ConvaiLipSyncBridgeTimelineTests
    {
        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private LipSyncPlaybackEngine _engine;
        private EventHub _eventHub;

        [SetUp]
        public void SetUp()
        {
            _engine = new LipSyncPlaybackEngine(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            _eventHub = new EventHub(new ImmediateScheduler());
        }

        [Test]
        public void OnTimelineResetRequested_WithValidThroughIndex_KeepsReleasedTailAndFadesAtBoundary()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);

            _eventHub.Publish(LipSyncTimelineResetRequested.Create(
                "char-1", "participant-1", "response-1", 1, 0, 2, 5, "interruption_during_drain"));

            // The kept tail (frames 0..5) still plays instead of hard-stopping...
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
            bool updated = _engine.Tick(0.05d, 1f / 60f);
            Assert.IsTrue(updated);

            // ...and past the boundary the stream fades out instead of starving forever.
            _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void OnTimelineResetRequested_AfterTruncate_LateChunksFromCancelledOwnerAreDropped()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);
            float bufferedBefore = _engine.BufferedDuration;

            _eventHub.Publish(LipSyncTimelineResetRequested.Create(
                "char-1", "participant-1", "response-1", 1, 0, 2, 5, "interruption_during_drain"));
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 12, "response-1", 1, 0, 3)));

            Assert.LessOrEqual(_engine.BufferedDuration, bufferedBefore);
        }

        [Test]
        public void OnBlendshapeTurnStatsReceived_FromDifferentOwner_DoesNotEndActiveStream()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(8, 0, "response-2", 2, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);

            _eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 8, 8, 1600, 200d, 200d, 60d,
                "response-1", 1, 0, 9));
            bridge.Tick(0.5f);
            _engine.Tick(10d, 1f / 60f);

            // Stream not ended: the engine starves waiting for more frames instead of fading.
            Assert.AreEqual(PlaybackState.Starving, _engine.State);
        }

        [Test]
        public void OnBlendshapeTurnStatsReceived_OwnerMatchedByResponseId_EndsStream()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(8, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);

            _eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 8, 8, 1600, 200d, 200d, 60d,
                "response-1", 1, 0, 9));
            bridge.Tick(0.5f);
            _engine.Tick(10d, 1f / 60f);

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void OnBlendshapeTurnStatsReceived_CountMismatch_EndsStreamAfterGraceWindow()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(8, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);

            _eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 20, 8, 1600, 400d, 400d, 60d,
                "response-1", 1, 0, 9));

            // Within the grace window the stream stays open (missing frames may still arrive)...
            _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Starving, _engine.State);

            // ...but once it expires the stream ends with a fade instead of staying open forever.
            bridge.Tick(0.5f);
            _engine.Tick(10.1d, 1f / 60f);
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void AheadChunksForNextResponse_AreBufferedUntilMatchingSpeechOwnerStarts()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            Assert.AreEqual(0.2f, _engine.TotalIngressDuration, 0.001f);

            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(30, 0, "response-2", 2, 0, 1)));
            Assert.AreEqual(0.2f, _engine.TotalIngressDuration, 0.001f,
                "future response must not replace audible response on chunk arrival");

            _eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("char-1", "response-2"));
            Assert.AreEqual(0.5f, _engine.TotalIngressDuration, 0.001f);
        }

        [Test]
        public void SpeechStopWithoutStats_EndsVisualOnlyAfterSampleClockStopsProgressing()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _eventHub.Publish(CharacterSpeechStateChanged.StoppedSpeaking("char-1", "response-1"));

            roomAudioService.AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Underrun);
            bridge.Tick(0.6f);
            _engine.Tick(10d, 1f / 60f);

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void SpeechStopBeforeBufferedAnimationEnd_FadesWhenSampleClockStopsProgressing()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(180, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(1d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);

            roomAudioService.AudioTimeline = CreateAudioTimeline(2000, AudioTimelinePlaybackState.Underrun);
            _eventHub.Publish(CharacterSpeechStateChanged.StoppedSpeaking("char-1", "response-1"));
            bridge.Tick(0.6f);

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State,
                "terminal audio stall must fade the current pose even when animation extends past audio");
        }

        [Test]
        public void MatchingStats_BeforeBufferedAnimationEnd_FadesAfterTerminalSampleStall()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(180, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(1d, 1f / 60f);
            _eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 180, 180, 0, 3000d, 1000d, 60d,
                "response-1", 1, 0, 2));

            roomAudioService.AudioTimeline = CreateAudioTimeline(2000, AudioTimelinePlaybackState.Underrun);
            bridge.Tick(0.5f);
            bridge.Tick(0.6f);

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State,
                "closed animation input must not outlive a terminal audio clock");
        }

        [Test]
        public void IndexedChunksArrivingAfterAudibleAudioStopped_DoNotStartFromSilentSampleProgress()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");

            // Reproduce captured ordering: the audio callback renders the complete response before
            // the main-thread data/lifecycle packets for that response are delivered.
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            roomAudioService.AudioTimeline = CreateAudioTimeline(3750, AudioTimelinePlaybackState.Playing);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));

            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(165, 0, "response-1", 1, 0, 1)));
            var owner = new LipSyncResponseOwner("response-1", 1, 0, 2);
            _eventHub.Publish(new LipSyncResponseLifecycleChanged(
                "char-1", "participant-1", true, in owner, DateTime.UtcNow));

            bool sampled = _engine.Tick(1d, 1f / 60f);
            Assert.IsFalse(sampled, "silent sample-clock progress must not bootstrap a new visual response");
            Assert.AreEqual(PlaybackState.Buffering, _engine.State);

            _eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 165, 165, 0, 2750d, 2750d, 60d,
                "response-1", 1, 0, 3));
            bridge.Tick(0.5f);
            bridge.Tick(0.6f);

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State,
                "closed response without a new audible start must retire without visible playback");
        }

        [Test]
        public void MatchingStats_WithSampleClock_WaitsForFinalFrameTailBeforeCompletion()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing, 48000)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _eventHub.Publish(new AudioTimelineSampleAnchor(
                "char-1", "response-1", 1, 0, 1, 48000, 0, 48000));
            _eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 12, 12, 0, 200d, 200d, 60d,
                "response-1", 1, 0, 1));

            roomAudioService.AudioTimeline = CreateAudioTimeline(
                1000 + 12000, AudioTimelinePlaybackState.Playing, 48000);
            bridge.Tick(0.5f);
            _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Starving, _engine.State);

            roomAudioService.AudioTimeline = CreateAudioTimeline(
                1000 + 61000, AudioTimelinePlaybackState.Playing, 48000);
            bridge.Tick(1f / 60f);
            _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State);
        }

        [Test]
        public void OnAudioTimelineAnchorReceived_BeforeChunks_GatesOpenOnceAudioStarts()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");

            _eventHub.Publish(LipSyncAudioTimelineAnchorReceived.Create(
                "char-1", "participant-1", "response-1", 1, 0, 1, 0d, 300d));
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 2)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            bool updated = _engine.Tick(0.02d, 1f / 60f);

            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void OnAudioTimelineAnchorReceived_MismatchedOwner_DoesNotBlockActiveResponse()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");

            // Backend emits anchors (support detected), but the audible audio belongs to another response.
            _eventHub.Publish(LipSyncAudioTimelineAnchorReceived.Create(
                "char-1", "participant-1", "response-0", 0, 0, 1, 0d, 300d));
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 2)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            bool updatedBeforeMatch = _engine.Tick(0.02d, 1f / 60f);

            Assert.IsTrue(updatedBeforeMatch);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);

            // The matching anchor arrives: the gate opens without further audio events.
            _eventHub.Publish(LipSyncAudioTimelineAnchorReceived.Create(
                "char-1", "participant-1", "response-1", 1, 0, 3, 0d, 300d));
            bool updatedAfterMatch = _engine.Tick(0.04d, 1f / 60f);

            Assert.IsTrue(updatedAfterMatch);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void OnAudioTimelineAnchorReceived_AnchorNeverArrives_DoesNotDelayGate()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");

            // Anchor support detected via another response's anchor; the active owner's anchor
            // never arrives (partial anchor coverage on the backend).
            _eventHub.Publish(LipSyncAudioTimelineAnchorReceived.Create(
                "char-1", "participant-1", "response-0", 0, 0, 1, 0d, 300d));
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 2)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            bool updated = _engine.Tick(0.02d, 1f / 60f);

            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void ResponseLifecycle_StartBeforeFirstChunk_PreservesOriginalSampleBaseline()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            var owner = new LipSyncResponseOwner("response-1", 1, 0, 1);

            _eventHub.Publish(new LipSyncResponseLifecycleChanged(
                "char-1", "participant-1", true, in owner, DateTime.UtcNow));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            roomAudioService.AudioTimeline = CreateAudioTimeline(4000, AudioTimelinePlaybackState.Playing);
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(180, 0, "response-1", 1, 0, 1)));

            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double target));
            Assert.AreEqual(3d, target, 0.001d);
        }

        [Test]
        public void AudioStartDeliveredLate_BindsToExactSignalFrameInsteadOfCurrentCallbackEnd()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(
                    4800,
                    AudioTimelinePlaybackState.Playing,
                    signalStartAbsoluteSourceFrame: 1000,
                    signalGeneration: 1)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));

            // The main-thread event arrives after several 1 kHz source frames were already rendered.
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double target));
            Assert.AreEqual(3.8d, target, 0.001d);
        }

        [Test]
        public void SampleDiscontinuity_IsReportedOnceAfterAudioSkipsForward()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioTimeline = CreateAudioTimeline(
                1250, AudioTimelinePlaybackState.Playing, discontinuityGeneration: 1);
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out _));

            Assert.IsTrue(bridge.ConsumeSampleClockDiscontinuity());
            Assert.IsFalse(bridge.ConsumeSampleClockDiscontinuity());
        }

        [Test]
        public void SampleFormatChange_PreservesElapsedTimelineAcrossNewSourceClock()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(
                    1000, AudioTimelinePlaybackState.Playing, sampleRate: 1000, formatGeneration: 1)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioTimeline = CreateAudioTimeline(
                2000, AudioTimelinePlaybackState.Playing, sampleRate: 1000, formatGeneration: 1);
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double beforeChange));

            roomAudioService.AudioTimeline = CreateAudioTimeline(
                200, AudioTimelinePlaybackState.Playing, sampleRate: 2000, formatGeneration: 2);
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double atChange));
            roomAudioService.AudioTimeline = CreateAudioTimeline(
                2200, AudioTimelinePlaybackState.Playing, sampleRate: 2000, formatGeneration: 2);
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double afterChange));

            Assert.AreEqual(1d, beforeChange, 0.001d);
            Assert.AreEqual(beforeChange, atChange, 0.001d);
            Assert.AreEqual(2d, afterChange, 0.001d);
        }

        [Test]
        public void TryGetAudioTimelineTarget_UsesMeasuredPlayheadFromRoomAudioService()
        {
            MockRoomAudioService roomAudioService = new() { AudioPlayheadSeconds = 2.5d };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));

            Assert.IsFalse(bridge.TryGetAudioTimelineTarget(out _), "No target before audio starts.");

            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double target));
            Assert.AreEqual(2.5d, target, 0.001d);
            Assert.AreEqual(2.5d, bridge.GetPlaybackStartElapsedSeconds(), 0.001d);
        }

        [Test]
        public void TryGetAudioTimelineTarget_AfterMidTurnAudioGap_ResumesFromFrozenPosition()
        {
            MockRoomAudioService roomAudioService = new() { AudioPlayheadSeconds = 2.5d };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            // Audio halts at 2.5s of played source audio; the turn position freezes there.
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            Assert.IsFalse(bridge.TryGetAudioTimelineTarget(out _));

            // Playback resumes: the platform playhead restarts from zero, but the turn position
            // continues from where audio halted.
            roomAudioService.AudioPlayheadSeconds = 0d;
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double resumed));
            Assert.AreEqual(2.5d, resumed, 0.001d);

            roomAudioService.AudioPlayheadSeconds = 1.0d;
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double advanced));
            Assert.AreEqual(3.5d, advanced, 0.001d);
        }

        [Test]
        public void TryGetAudioTimelineTarget_WithSampleClock_DoesNotResetOnAmplitudeSilence()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(180, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioTimeline = CreateAudioTimeline(3500, AudioTimelinePlaybackState.Playing);
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double beforeSilence));
            Assert.AreEqual(2.5d, beforeSilence, 0.001d);

            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            roomAudioService.AudioTimeline = CreateAudioTimeline(4000, AudioTimelinePlaybackState.Playing);

            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double duringSilence));
            Assert.AreEqual(3d, duringSilence, 0.001d);
        }

        [Test]
        public void TryGetAudioTimelineTarget_WithSampleClock_FreezesOnTrueUnderrun()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(180, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioTimeline = CreateAudioTimeline(2500, AudioTimelinePlaybackState.Underrun);
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double stalled));
            Assert.AreEqual(1.5d, stalled, 0.001d);

            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double stillStalled));
            Assert.AreEqual(stalled, stillStalled, 0.0001d);
        }

        [Test]
        public void OnBlendshapeTurnStatsReceived_SkewedTimingTotals_DoNotAlterAudioTimelineTarget()
        {
            MockRoomAudioService roomAudioService = new() { AudioPlayheadSeconds = 10d };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double before));
            Assert.AreEqual(10d, before, 0.001d);

            // Regression: stats totals can be stale by one trailing chunk (the server emits
            // turn-stats before the final chunk arrives), so an apparent frames/audio skew in
            // stats must never rescale the playback timeline.
            _eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 300, 12, 480000, 5000d, 4901d, 61.2d,
                "response-1", 1, 0, 9));

            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out double after));
            Assert.AreEqual(10d, after, 0.001d);
        }

        [Test]
        public void BrowserMedia_AudioBeforeOwnerPacket_PreservesExactSignalBaseline()
        {
            DateTime timestamp = DateTime.UtcNow;
            MockRoomAudioService roomAudioService = new()
            {
                AudioMediaTimeline = CreateMediaTimeline(20d, analyserAvailable: true)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            var owner = new LipSyncResponseOwner("response-1", 1, 0, 1);

            _eventHub.Publish(new LipSyncResponseLifecycleChanged(
                "char-1", "participant-1", true, in owner, timestamp));
            roomAudioService.AudioMediaTimeline = CreateMediaTimeline(
                20.10d, signalStartSeconds: 20.02d, signalGeneration: 1, analyserAvailable: true);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioMediaTimeline = CreateMediaTimeline(
                20.42d, signalStartSeconds: 20.02d, signalGeneration: 1, analyserAvailable: true);
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));

            Assert.IsTrue(bridge.TryGetAuthoritativeTimelineTarget(out double target));
            Assert.AreEqual(0.40d, target, 0.001d);
        }

        [Test]
        public void BrowserMedia_BridgeBindsAfterTrackStart_OpensFirstTurnOnMeasuredSignal()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioMediaTimeline = CreateMediaTimeline(20d, analyserAvailable: true)
            };

            // WebGL can publish its one immediate PlaybackStarted event while registering the
            // already-playing HTML element, before this character bridge has subscribed.
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));
            Assert.AreEqual(PlaybackState.Buffering, _engine.State);

            roomAudioService.AudioMediaTimeline = CreateMediaTimeline(
                20.10d,
                signalStartSeconds: 20.02d,
                signalGeneration: 1,
                analyserAvailable: true);
            bridge.Tick(1f / 60f);
            bool updated = _engine.Tick(0.08d, 1f / 60f);

            Assert.IsTrue(updated,
                "the per-character browser timeline must recover a PlaybackStarted event missed before Bind");
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
            Assert.IsTrue(bridge.TryGetAuthoritativeTimelineTarget(out double target));
            Assert.AreEqual(0.08d, target, 0.001d);
        }

        [Test]
        public void BrowserMedia_BridgeBindsDuringUnderrun_WaitsForNextPlayingEvent()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioMediaTimeline = CreateMediaTimeline(
                    20d,
                    AudioTimelinePlaybackState.Underrun,
                    signalStartSeconds: 20d,
                    signalGeneration: 1,
                    analyserAvailable: true)
            };

            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));
            bridge.Tick(1f / 60f);
            bool updatedBeforePlaying = _engine.Tick(0.05d, 1f / 60f);

            Assert.IsFalse(updatedBeforePlaying);
            Assert.AreEqual(
                PlaybackState.Buffering,
                _engine.State,
                "an element that is stalled during Bind must not be treated as audible playback");

            roomAudioService.AudioMediaTimeline = CreateMediaTimeline(
                20.08d,
                signalStartSeconds: 20d,
                signalGeneration: 1,
                analyserAvailable: true);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            bridge.Tick(1f / 60f);

            Assert.IsTrue(_engine.Tick(0.08d, 1f / 60f));
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void BrowserMedia_LaterSpeechSignal_DoesNotResetActiveResponseClock()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioMediaTimeline = CreateMediaTimeline(
                    10.2d, signalStartSeconds: 10d, signalGeneration: 1, analyserAvailable: true)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioMediaTimeline = CreateMediaTimeline(
                11.2d, signalStartSeconds: 11d, signalGeneration: 2, analyserAvailable: true);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            Assert.IsTrue(bridge.TryGetAuthoritativeTimelineTarget(out double target));
            Assert.AreEqual(1.2d, target, 0.001d, "a natural speech pause must not choose a new response zero");
        }

        [Test]
        public void BrowserMedia_ConsecutiveResponse_BindsFreshSignalWithoutLifecycleFallback()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                10.2d, signalStartSeconds: 10d, signalGeneration: 1, analyserAvailable: true);
            ResponseAudioTimelineState state = new(
                null,
                () => snapshot,
                null,
                () => now);
            var firstOwner = new LipSyncTimelineOwner("response-1", 1, 0);
            var secondOwner = new LipSyncTimelineOwner("response-2", 2, 0);

            state.RecordLifecycleStart(firstOwner, now);
            state.OnOwnerAdopted(firstOwner, audioActive: true);
            Assert.IsTrue(state.TryGetTarget(out double firstTarget));
            Assert.AreEqual(0.2d, firstTarget, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaLocked, state.ClockMode);

            // Between responses the browser monitor observes enough silence to retire generation 1.
            // The next audible onset therefore increments the generation even though Unity did not
            // poll a lip-sync timeline during the gap.
            now = now.AddSeconds(2);
            snapshot = CreateMediaTimeline(12d, signalGeneration: 1, analyserAvailable: true);
            state.RecordLifecycleStart(secondOwner, now);
            state.OnOwnerAdopted(secondOwner, audioActive: true);
            Assert.IsFalse(state.TryGetTarget(out _), "response 2 must wait for its own audible onset");

            now = now.AddMilliseconds(100);
            snapshot = CreateMediaTimeline(
                12.1d, signalStartSeconds: 12.02d, signalGeneration: 2, analyserAvailable: true);

            Assert.IsTrue(state.TryGetTarget(out double secondTarget));
            Assert.AreEqual(0.08d, secondTarget, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaLocked, state.ClockMode);
            Assert.IsFalse(state.BrowserSignalTimedOut);
            Assert.AreEqual(0, state.BrowserFallbackCount);
        }

        [Test]
        public void BrowserMedia_DiscontinuityIsReportedOnce_WhileFrozenPositionDoesNotAdvance()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioMediaTimeline = CreateMediaTimeline(
                    5d, signalStartSeconds: 5d, signalGeneration: 1, analyserAvailable: true)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioMediaTimeline = CreateMediaTimeline(
                5.25d,
                AudioTimelinePlaybackState.Underrun,
                5d,
                1,
                analyserAvailable: true,
                discontinuityGeneration: 1);
            Assert.IsTrue(bridge.TryGetAuthoritativeTimelineTarget(out double frozenTarget));
            Assert.AreEqual(0.25d, frozenTarget, 0.001d);
            Assert.IsTrue(bridge.ConsumeAuthoritativeClockDiscontinuity());
            Assert.IsFalse(bridge.ConsumeAuthoritativeClockDiscontinuity());

            Assert.IsTrue(bridge.TryGetAuthoritativeTimelineTarget(out double stillFrozenTarget));
            Assert.AreEqual(frozenTarget, stillFrozenTarget, 0.0001d);
        }

        [Test]
        public void BrowserMedia_AnalyserUnavailable_FallsBackOnceAfterBoundedWait()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(50d, analyserAvailable: false);
            ResponseAudioTimelineState state = new(
                null,
                () => snapshot,
                null,
                () => now);
            var owner = new LipSyncTimelineOwner("response-1", 1, 0);

            state.RecordLifecycleStart(owner, now);
            state.OnOwnerAdopted(owner, audioActive: true);
            Assert.IsFalse(state.TryGetTarget(out _));

            now = now.AddMilliseconds(501);
            snapshot = CreateMediaTimeline(50.5d, analyserAvailable: false);
            Assert.IsTrue(state.TryGetTarget(out double target));
            Assert.AreEqual(0.5d, target, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaFallback, state.ClockMode);
            Assert.IsTrue(state.BrowserSignalTimedOut);
            Assert.AreEqual(1, state.BrowserFallbackCount);

            Assert.IsTrue(state.TryGetTarget(out _));
            Assert.AreEqual(1, state.BrowserFallbackCount, "the degraded transition must be recorded once");
        }

        // ----------------------------------------------------------------------------------
        // Voice reaching the browser after the 500 ms wait. Measured in a WebGL build
        // (2026-09-25): the lifecycle packet armed the clock, the analyser heard the voice
        // 633 ms later, and the fallback had already bound the lifecycle position, a point in the
        // silence before the voice, so the mouth ran 0.7-1.0 s ahead of it for the whole response.
        // ----------------------------------------------------------------------------------

        [Test]
        public void BrowserMedia_SilentPastFallbackWait_BindsTheOnsetThatArrivesLater()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                43.809d, signalStartSeconds: 27.146d, signalGeneration: 2, analyserAvailable: true);
            ResponseAudioTimelineState state = new(null, () => snapshot, null, () => now);
            var owner = new LipSyncTimelineOwner("response-3", 3, 0);

            state.RecordLifecycleStart(owner, now);
            state.OnOwnerAdopted(owner, audioActive: true);

            now = now.AddMilliseconds(600);
            snapshot = CreateMediaTimeline(
                44.229d, signalStartSeconds: 27.146d, signalGeneration: 2, analyserAvailable: true);
            Assert.IsFalse(state.TryGetTarget(out _),
                "a running analyser that hears silence must keep waiting for this response's voice");

            now = now.AddMilliseconds(100);
            snapshot = CreateMediaTimeline(
                44.819d, signalStartSeconds: 44.767d, signalGeneration: 3, analyserAvailable: true,
                signalActive: true);

            Assert.IsTrue(state.TryGetTarget(out double target));
            Assert.AreEqual(0.052d, target, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaLocked, state.ClockMode);
            Assert.AreEqual(0, state.BrowserFallbackCount);
        }

        [Test]
        public void BrowserMedia_SilentPastLongestWait_FallsBackThenRebindsOnceToTheOnset()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                10d, signalStartSeconds: 2d, signalGeneration: 1, analyserAvailable: true);
            ResponseAudioTimelineState state = new(null, () => snapshot, null, () => now);
            var owner = new LipSyncTimelineOwner("response-2", 2, 0);

            state.RecordLifecycleStart(owner, now);
            state.OnOwnerAdopted(owner, audioActive: true);

            now = now.AddMilliseconds(2001);
            snapshot = CreateMediaTimeline(
                12d, signalStartSeconds: 2d, signalGeneration: 1, analyserAvailable: true);
            Assert.IsTrue(state.TryGetTarget(out double provisional));
            Assert.AreEqual(2d, provisional, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaFallback, state.ClockMode);
            Assert.IsFalse(state.ConsumeAuthoritativeDiscontinuity());

            snapshot = CreateMediaTimeline(
                12.3d, signalStartSeconds: 12.2d, signalGeneration: 2, analyserAvailable: true,
                signalActive: true);
            Assert.IsTrue(state.TryGetTarget(out double measured));
            Assert.AreEqual(0.1d, measured, 0.001d, "the measured onset replaces the provisional zero");
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaLocked, state.ClockMode);
            Assert.IsTrue(state.ConsumeAuthoritativeDiscontinuity(), "the jump must be blended, not snapped");
            Assert.IsFalse(state.ConsumeAuthoritativeDiscontinuity());

            snapshot = CreateMediaTimeline(
                14d, signalStartSeconds: 13.8d, signalGeneration: 3, analyserAvailable: true,
                signalActive: true);
            Assert.IsTrue(state.TryGetTarget(out double afterPause));
            Assert.AreEqual(1.8d, afterPause, 0.001d, "a pause inside the response must not rebind again");
        }

        [Test]
        public void BrowserMedia_VoiceAheadOfLifecyclePacket_BindsItsOnsetLikeTheNativeClock()
        {
            // Measured in a WebGL build: the first response's voice reached the browser 0.62 s
            // before its lifecycle packet, and the lifecycle position made the mouth 608 ms late.
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                7.169d, signalStartSeconds: 6.547d, signalGeneration: 1, analyserAvailable: true,
                signalActive: true);
            ResponseAudioTimelineState state = new(null, () => snapshot, null, () => now);
            var owner = new LipSyncTimelineOwner("response-1", 1, 0);

            state.RecordLifecycleStart(owner, now);
            state.OnOwnerAdopted(owner, audioActive: true);

            now = now.AddMilliseconds(100);
            snapshot = CreateMediaTimeline(
                7.269d, signalStartSeconds: 6.547d, signalGeneration: 1, analyserAvailable: true,
                signalActive: true);
            Assert.IsTrue(state.TryGetTarget(out double target));
            Assert.AreEqual(0.722d, target, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaLocked, state.ClockMode);
        }

        [Test]
        public void BrowserMedia_SignalSoundingThroughReset_IsNotTheNextResponsesOnset()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                10d, signalStartSeconds: 9d, signalGeneration: 4, analyserAvailable: true,
                signalActive: true);
            ResponseAudioTimelineState state = new(null, () => snapshot, null, () => now);
            var owner = new LipSyncTimelineOwner("response-5", 5, 0);

            // The bridge binds (or rebinds) while earlier audio is still sounding.
            state.Reset();
            state.RecordLifecycleStart(owner, now);
            state.OnOwnerAdopted(owner, audioActive: true);

            Assert.IsFalse(state.TryGetTarget(out _), "the signal began before the reset");
        }

        [Test]
        public void BrowserMedia_PauseInsideReplyThenBackToBackReply_DoesNotStartInsideThePreviousReply()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                2.1d, signalStartSeconds: 2d, signalGeneration: 1, analyserAvailable: true,
                signalActive: true);
            ResponseAudioTimelineState state = new(null, () => snapshot, null, () => now);
            var first = new LipSyncTimelineOwner("response-1", 1, 0);
            var second = new LipSyncTimelineOwner("response-2", 2, 0);

            state.RecordLifecycleStart(first, now);
            state.OnOwnerAdopted(first, audioActive: true);
            Assert.IsTrue(state.TryGetTarget(out _));

            // A pause of 500 ms or more inside response 1: its next sentence is a new generation.
            now = now.AddSeconds(4);
            snapshot = CreateMediaTimeline(
                6.1d, signalStartSeconds: 6d, signalGeneration: 2, analyserAvailable: true,
                signalActive: true);
            Assert.IsTrue(state.TryGetTarget(out double firstTarget));
            Assert.AreEqual(4.1d, firstTarget, 0.001d);

            // Response 2 (a tool-call round) starts while that sentence is still sounding.
            now = now.AddSeconds(2);
            snapshot = CreateMediaTimeline(
                8d, signalStartSeconds: 6d, signalGeneration: 2, analyserAvailable: true,
                signalActive: true);
            state.RecordLifecycleStart(second, now);
            state.OnOwnerAdopted(second, audioActive: true);

            Assert.IsFalse(state.TryGetTarget(out _),
                "response 1's sentence is not response 2's onset; it must not start 2 s into it");

            now = now.AddMilliseconds(501);
            snapshot = CreateMediaTimeline(
                8.5d, signalStartSeconds: 6d, signalGeneration: 2, analyserAvailable: true,
                signalActive: true);
            Assert.IsTrue(state.TryGetTarget(out double secondTarget));
            Assert.AreEqual(0.5d, secondTarget, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaFallback, state.ClockMode);
        }

        [Test]
        public void BrowserMedia_StreamReplacedWithRestartedGenerations_StillBindsTheNewVoice()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                20.1d, signalStartSeconds: 20d, signalGeneration: 5, analyserAvailable: true,
                signalActive: true);
            ResponseAudioTimelineState state = new(null, () => snapshot, null, () => now);
            var first = new LipSyncTimelineOwner("response-1", 1, 0);
            var second = new LipSyncTimelineOwner("response-2", 2, 0);

            state.RecordLifecycleStart(first, now);
            state.OnOwnerAdopted(first, audioActive: true);
            Assert.IsTrue(state.TryGetTarget(out _));

            // After a reconnect the new stream's timing probe counts generations from 1 again, and
            // its first voice reaches the browser before response 2's start message.
            now = now.AddSeconds(10);
            snapshot = CreateMediaTimeline(
                0.9d, signalStartSeconds: 0.3d, signalGeneration: 1, analyserAvailable: true,
                signalActive: true);
            state.RecordLifecycleStart(second, now);
            state.OnOwnerAdopted(second, audioActive: true);

            Assert.IsTrue(state.TryGetTarget(out double target));
            Assert.AreEqual(0.6d, target, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaLocked, state.ClockMode);
        }

        [Test]
        public void BrowserMedia_VoiceContinuingFromPreviousResponse_FallsBackAndKeepsItsClock()
        {
            DateTime now = DateTime.UtcNow;
            AudioMediaTimelineSnapshot snapshot = CreateMediaTimeline(
                2.1d, signalStartSeconds: 2d, signalGeneration: 1, analyserAvailable: true,
                signalActive: true);
            ResponseAudioTimelineState state = new(null, () => snapshot, null, () => now);
            var first = new LipSyncTimelineOwner("response-1", 1, 0);
            var owner = new LipSyncTimelineOwner("response-2", 2, 0);

            state.RecordLifecycleStart(first, now);
            state.OnOwnerAdopted(first, audioActive: true);
            Assert.IsTrue(state.TryGetTarget(out _));

            // Back-to-back: response 2 starts while response 1's voice is still sounding, so the
            // sounding signal already belongs to response 1 and response 2 has no onset of its own.
            now = now.AddSeconds(8);
            snapshot = CreateMediaTimeline(
                10d, signalStartSeconds: 2d, signalGeneration: 1, analyserAvailable: true,
                signalActive: true);
            state.RecordLifecycleStart(owner, now);
            state.OnOwnerAdopted(owner, audioActive: true);

            now = now.AddMilliseconds(501);
            snapshot = CreateMediaTimeline(
                10.5d, signalStartSeconds: 2d, signalGeneration: 1, analyserAvailable: true,
                signalActive: true);
            Assert.IsTrue(state.TryGetTarget(out double target),
                "back-to-back speech has no onset of its own to wait for");
            Assert.AreEqual(0.5d, target, 0.001d);
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaFallback, state.ClockMode);

            snapshot = CreateMediaTimeline(
                12d, signalStartSeconds: 11.8d, signalGeneration: 2, analyserAvailable: true,
                signalActive: true);
            Assert.IsTrue(state.TryGetTarget(out double afterPause));
            Assert.AreEqual(2d, afterPause, 0.001d, "a pause in continuing speech is not a new response zero");
            Assert.AreEqual(AudioTimelineClockMode.BrowserMediaFallback, state.ClockMode);
        }

        [Test]
        public void NativeSampleClock_RemainsHigherPrecedenceThanBrowserMediaClock()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing),
                AudioMediaTimeline = CreateMediaTimeline(
                    25d, signalStartSeconds: 20d, signalGeneration: 1, analyserAvailable: true)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(300, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            roomAudioService.AudioTimeline = CreateAudioTimeline(1500, AudioTimelinePlaybackState.Playing);
            Assert.IsTrue(bridge.TryGetSampleLockedTarget(out double target));
            Assert.AreEqual(0.5d, target, 0.001d);
        }

        // ----------------------------------------------------------------------------------
        // Audible end. Captured on a live room (2026-09-02): the service never sent
        // blendshape-turn-stats, its speech-stop arrived 1.2-1.7 s after the voice stopped, and the
        // rendered sample clock kept advancing through the silence — so the finished response was
        // never retired, and every later response was parked behind it until its frames were
        // dropped. Lip sync worked for the first answer only.
        // ----------------------------------------------------------------------------------

        [Test]
        public void NextResponseChunks_AfterActiveResponseAudiblyFinished_TakeOverWithoutServiceConfirmation()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
            _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Starving, _engine.State, "response-1 has shown every frame it had");

            // The voice stops. No stats, no speech-stop from the service; the rendered clock keeps
            // counting silent samples.
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            roomAudioService.AudioTimeline = CreateAudioTimeline(4000, AudioTimelinePlaybackState.Playing);
            bridge.Tick(1f / 60f);
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State, "the mouth closes on its fade at once");

            // The next response lands while the mouth is still closing: it waits for the fade, so
            // the close is never cut into a snap...
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(30, 0, "response-2", 2, 0, 1)));
            Assert.AreEqual(PlaybackState.FadingOut, _engine.State, "a finished response is not replaced mid-fade");

            // ...and takes over the moment the fade completes, with nothing from the service.
            RunFadeToIdle(bridge);

            Assert.AreEqual(0.5f, _engine.TotalIngressDuration, 0.001f,
                "the next response must take over a finished one, not be parked behind it");
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            Assert.IsTrue(_engine.Tick(0.02d, 1f / 60f));
            Assert.AreEqual(PlaybackState.Playing, _engine.State);
        }

        [Test]
        public void ParkedNextResponse_IsPromotedOnceActiveResponseAudiblyFinished()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);

            // Send-ahead: response-2 arrives while response-1 is still audible, and waits.
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(30, 0, "response-2", 2, 0, 1)));
            Assert.AreEqual(0.2f, _engine.TotalIngressDuration, 0.001f,
                "a response that is still audible keeps the stage");

            _engine.Tick(10d, 1f / 60f);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            RunFadeToIdle(bridge);

            Assert.AreEqual(0.5f, _engine.TotalIngressDuration, 0.001f,
                "the parked response must start when the one in front of it has audibly finished");
        }

        [Test]
        public void SpeechStartWithoutOwner_AfterActiveResponseAudiblyFinished_PromotesParkedResponse()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(30, 0, "response-2", 2, 0, 1)));
            _engine.Tick(10d, 1f / 60f);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));

            RunFadeToIdle(bridge, promote: false);

            // The service says "started speaking" without naming the response, as it did on the
            // captured room. That is still the parked response starting.
            _eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("char-1"));

            Assert.AreEqual(0.5f, _engine.TotalIngressDuration, 0.001f);
        }

        [Test]
        public void AheadChunks_WhileActiveResponseStillAudible_StayParked()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);
            _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Starving, _engine.State);

            // Frames ran out but the voice has not stopped: a stall, not an ending.
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(30, 0, "response-2", 2, 0, 1)));
            bridge.Tick(1f / 60f);

            Assert.AreEqual(0.2f, _engine.TotalIngressDuration, 0.001f,
                "a response whose voice is still going must not be replaced");
        }

        [Test]
        public void SpeechStopWithoutStats_SilentSampleProgress_StillRetiresResponse()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);
            _engine.Tick(10d, 1f / 60f);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            _eventHub.Publish(CharacterSpeechStateChanged.StoppedSpeaking("char-1", "response-1"));
            Assert.IsTrue(bridge.TryGetAudioTimelineTarget(out _), "the response is still open");

            // The native track keeps rendering silence: the sample clock advances, the voice does
            // not. That is not progress, and the no-progress watchdog must still expire.
            roomAudioService.AudioTimeline = CreateAudioTimeline(2000, AudioTimelinePlaybackState.Playing);
            bridge.Tick(0.3f);
            roomAudioService.AudioTimeline = CreateAudioTimeline(3000, AudioTimelinePlaybackState.Playing);
            bridge.Tick(0.3f);
            for (int i = 0; i < 60; i++) _engine.Tick(10d, 1f / 60f);
            roomAudioService.AudioTimeline = CreateAudioTimeline(4000, AudioTimelinePlaybackState.Playing);
            bridge.Tick(1f / 60f);

            Assert.AreEqual(PlaybackState.Idle, _engine.State);
            Assert.IsFalse(bridge.TryGetAudioTimelineTarget(out _),
                "a response whose voice ended must retire even though the rendered clock never stalls");
        }

        [Test]
        public void ServiceSpeechStop_AfterVoiceAlreadyEnded_FadesAtOnceInsteadOfHoldingTheCaughtShape()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(180, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(1d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, _engine.State);

            // A barge-in cuts the audio mid-word with two seconds of frames still ahead...
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            bridge.Tick(1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, _engine.State, "a dropout with frames ahead is not an ending");

            // ...and the service confirms the turn is over 26 ms later, as captured live.
            _eventHub.Publish(CharacterSpeechStateChanged.StoppedSpeaking("char-1", "response-1"));

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State,
                "voice gone plus the service word is conclusive: fade now, not after a stall timeout");
        }

        [Test]
        public void SpeechPlaybackReading_ReportsSettledInputRemainingTimeAndTheAudibleEnd()
        {
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = CreateAudioTimeline(1000, AudioTimelinePlaybackState.Playing)
            };
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(_eventHub, "char-1");
            Assert.IsFalse(bridge.GetSpeechPlaybackReading(0d).HasResponse);

            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(60, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);

            SpeechPlaybackReading reading = bridge.GetSpeechPlaybackReading(0.2d);
            Assert.IsTrue(reading.HasResponse);
            Assert.IsFalse(reading.InputSettled, "a chunk just arrived: more may follow");
            Assert.AreEqual(0.8f, reading.RemainingSeconds, 0.02f);
            Assert.IsFalse(reading.Finished);

            // Nothing more arrives for the settle window: the end is now known.
            bridge.Tick(ConvaiLipSyncBridge.InputSettleSeconds + 0.01f);
            reading = bridge.GetSpeechPlaybackReading(0.6d);
            Assert.IsTrue(reading.InputSettled);
            Assert.AreEqual(0.4f, reading.RemainingSeconds, 0.02f);
            Assert.IsTrue(reading.EndsWithin(0.5f));
            Assert.IsFalse(reading.EndsWithin(0.3f));

            // Frames shown, voice gone: finished, on local evidence alone.
            _engine.Tick(10d, 1f / 60f);
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            reading = bridge.GetSpeechPlaybackReading(1.5d);
            Assert.IsTrue(reading.Finished);
            Assert.AreEqual(0f, reading.RemainingSeconds);
        }

        /// <summary>
        ///     Runs the engine fade-out to completion and gives the bridge one tick to act on the
        ///     idle engine (retire, promote a parked response).
        /// </summary>
        private void RunFadeToIdle(ConvaiLipSyncBridge bridge, bool promote = true)
        {
            // The bridge starts the fade on its own tick (voice gone, frames shown)...
            bridge.Tick(1f / 60f);
            // ...and the engine runs it to completion.
            for (int i = 0; i < 60 && _engine.State != PlaybackState.Idle; i++)
                _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Idle, _engine.State, "the fade must complete");
            if (promote) bridge.Tick(1f / 60f);
        }

        [Test]
        public void FramesRunOut_OnSettledInput_CloseWithoutWaitingForTheSilenceDetector()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);

            // The input has been quiet for longer than a response ever pauses between chunks...
            bridge.Tick(ConvaiLipSyncBridge.InputSettleSeconds + 0.01f);
            // ...and the frames run out while the silence detector, with its half-second hold, still
            // says the voice is on. That is the end of the response: close now, hold nothing.
            _engine.Tick(10d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Starving, _engine.State);
            bridge.Tick(1f / 60f);

            Assert.AreEqual(PlaybackState.FadingOut, _engine.State,
                "the last shape must not be held while the detector confirms what the frames already said");
        }

        [Test]
        public void FramesRunOut_WhileChunksStillArrive_HoldTheShape()
        {
            using ConvaiLipSyncBridge bridge = new(_engine, LipSyncProfileId.ARKit);
            bridge.Bind(_eventHub, "char-1");
            _eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(12, 0, "response-1", 1, 0, 1)));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            _engine.Tick(0.02d, 1f / 60f);

            // A chunk landed a moment ago and the voice is audible: running out of frames here is a
            // stall, and a stall keeps its shape rather than snapping the mouth shut mid-word.
            bridge.Tick(0.1f);
            _engine.Tick(10d, 1f / 60f);
            bridge.Tick(1f / 60f);

            Assert.AreEqual(PlaybackState.Starving, _engine.State);
        }

        private static LipSyncPackedChunk CreateIndexedChunk(
            int frameCount,
            int startFrameIndex,
            string responseId,
            int neuroSyncTurnId,
            int epoch,
            int sequence)
        {
            float[][] frames = new float[frameCount][];
            for (int i = 0; i < frameCount; i++) frames[i] = new[] { i / (float)Math.Max(1, frameCount) };

            return new LipSyncPackedChunk(
                LipSyncProfileId.ARKit,
                60f,
                new[] { "jawOpen" },
                frames,
                responseId,
                neuroSyncTurnId,
                epoch,
                startFrameIndex,
                sequence);
        }

        private static AudioTimelineSnapshot CreateAudioTimeline(
            long absoluteSourceFrame,
            AudioTimelinePlaybackState state,
            int sampleRate = 1000,
            long signalStartAbsoluteSourceFrame = -1,
            int signalGeneration = 0,
            int discontinuityGeneration = 0,
            int formatGeneration = 1) =>
            new(absoluteSourceFrame, sampleRate, 1, formatGeneration, 0, state,
                absoluteSourceFrame, absoluteSourceFrame, 0, 0, 0,
                signalStartAbsoluteSourceFrame: signalStartAbsoluteSourceFrame,
                signalGeneration: signalGeneration,
                discontinuityGeneration: discontinuityGeneration);

        private static AudioMediaTimelineSnapshot CreateMediaTimeline(
            double logicalPositionSeconds,
            AudioTimelinePlaybackState state = AudioTimelinePlaybackState.Playing,
            double signalStartSeconds = -1d,
            int signalGeneration = 0,
            bool analyserAvailable = false,
            int discontinuityGeneration = 0,
            bool signalActive = false) =>
            new(
                logicalPositionSeconds,
                state,
                signalStartSeconds,
                signalGeneration,
                discontinuityGeneration,
                analyserAvailable,
                signalActive: signalActive);
    }

    [TestFixture]
    public class IndexedLipSyncSessionTests
    {
        [Test]
        public void BeginActiveResponse_ClearsResponseScopedState()
        {
            IndexedLipSyncSession session = CreateSession();
            session.BeginActiveResponse();
            session.OpenGate();
            session.StatsGraceRemaining = 1f;
            session.CompletionWatchdogRemaining = 2f;
            session.CompletionLastTarget = 3d;
            session.ClosedInputBoundarySeconds = 4d;
            session.BeginClosing(IndexedLipSyncCompletionReason.SampleClockStall);
            session.MarkTerminalSummaryLogged();

            session.BeginActiveResponse();

            Assert.IsTrue(session.HasIndexedStream);
            Assert.IsFalse(session.IsClosing);
            Assert.IsFalse(session.GateOpen);
            Assert.AreEqual(-1f, session.StatsGraceRemaining);
            Assert.AreEqual(-1f, session.CompletionWatchdogRemaining);
            Assert.AreEqual(-1d, session.CompletionLastTarget);
            Assert.IsNull(session.ClosedInputBoundarySeconds);
            Assert.AreEqual(IndexedLipSyncCompletionReason.None, session.CompletionReason);
            Assert.IsFalse(session.TerminalSummaryLogged);
        }

        [Test]
        public void RetireActiveResponse_ClearsGateClosingAndTimers()
        {
            IndexedLipSyncSession session = CreateSession();
            session.BeginActiveResponse();
            session.OpenGate();
            session.StatsGraceRemaining = 1f;
            session.CompletionWatchdogRemaining = 2f;
            session.ClosedInputBoundarySeconds = 3d;
            session.BeginClosing(IndexedLipSyncCompletionReason.StatsGrace);

            session.RetireActiveResponse();

            Assert.IsFalse(session.HasIndexedStream);
            Assert.IsFalse(session.IsClosing);
            Assert.IsFalse(session.GateOpen);
            Assert.AreEqual(-1f, session.StatsGraceRemaining);
            Assert.AreEqual(-1f, session.CompletionWatchdogRemaining);
            Assert.IsNull(session.ClosedInputBoundarySeconds);
        }

        [Test]
        public void ResetWithoutFutureClear_PreservesBufferedResponse()
        {
            IndexedLipSyncSession session = CreateSession();
            AddActiveOwner(session, "response-active");
            session.BeginActiveResponse();
            FutureChunkBufferResult buffered = session.TryBufferFutureChunk(
                CreateSessionChunk("response-future", 12), out _, out _);

            session.Reset(clearFutureResponses: false);

            bool promoted = session.TryTakeFutureResponse(
                default(LipSyncTimelineOwner), out string ownerKey, out var chunks, out int frameCount);
            Assert.AreEqual(FutureChunkBufferResult.Buffered, buffered);
            Assert.IsFalse(session.HasIndexedStream);
            Assert.IsTrue(promoted);
            Assert.AreEqual("response:response-future", ownerKey);
            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(12, frameCount);
        }

        [Test]
        public void ResetWithFutureClear_ClearsAssemblerAndBufferedResponses()
        {
            IndexedLipSyncSession session = CreateSession();
            AddActiveOwner(session, "response-active");
            session.BeginActiveResponse();
            session.TryBufferFutureChunk(CreateSessionChunk("response-future", 12), out _, out _);

            session.Reset(clearFutureResponses: true);

            bool promoted = session.TryTakeFutureResponse(
                default(LipSyncTimelineOwner), out _, out _, out _);
            Assert.IsFalse(session.HasIndexedStream);
            Assert.IsFalse(session.Assembler.HasActiveOwner);
            Assert.IsFalse(promoted);
        }

        [Test]
        public void FutureResponseCapacity_EvictsOldestOwner()
        {
            IndexedLipSyncSession session = CreateSession();
            AddActiveOwner(session, "response-active");
            session.BeginActiveResponse();
            session.TryBufferFutureChunk(CreateSessionChunk("response-1", 12), out _, out _);
            session.TryBufferFutureChunk(CreateSessionChunk("response-2", 12), out _, out _);
            session.TryBufferFutureChunk(CreateSessionChunk("response-3", 12), out _, out _);

            FutureChunkBufferResult result = session.TryBufferFutureChunk(
                CreateSessionChunk("response-4", 12), out string ownerKey, out string droppedOwnerKey);

            Assert.AreEqual(FutureChunkBufferResult.DroppedCapacity, result);
            Assert.AreEqual("response:response-4", ownerKey);
            Assert.AreEqual("response:response-1", droppedOwnerKey);
            Assert.IsFalse(session.TryTakeFutureResponse(
                new LipSyncTimelineOwner("response-1", null, null), out _, out _, out _));
            Assert.IsTrue(session.TryTakeFutureResponse(
                new LipSyncTimelineOwner("response-4", null, null), out _, out _, out int frames));
            Assert.AreEqual(12, frames);
        }

        [Test]
        public void FutureResponseDuration_DropsChunkBeyondThreeSeconds()
        {
            IndexedLipSyncSession session = CreateSession();
            AddActiveOwner(session, "response-active");
            session.BeginActiveResponse();

            FutureChunkBufferResult result = session.TryBufferFutureChunk(
                CreateSessionChunk("response-future", 181), out _, out _);

            Assert.AreEqual(FutureChunkBufferResult.DroppedDuration, result);
        }

        private static IndexedLipSyncSession CreateSession() => new(() => null, () => null);

        private static void AddActiveOwner(IndexedLipSyncSession session, string responseId)
        {
            LipSyncTimelineAssemblerResult result = session.Assembler.AddChunk(CreateSessionChunk(responseId, 12));
            Assert.AreEqual(LipSyncTimelineAssemblerAction.EmitFrames, result.Action);
        }

        private static LipSyncPackedChunk CreateSessionChunk(string responseId, int frameCount)
        {
            float[][] frames = new float[frameCount][];
            for (int i = 0; i < frameCount; i++) frames[i] = new[] { i / (float)Math.Max(1, frameCount) };

            return new LipSyncPackedChunk(
                LipSyncProfileId.ARKit,
                60f,
                new[] { "jawOpen" },
                frames,
                responseId,
                1,
                0,
                0,
                1);
        }
    }

    [TestFixture]
    public class ConvaiLipSyncDiagnosticsTests
    {
        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class RecordingTestLogger : ILogger
        {
            public void Log(in Convai.Domain.Logging.LogEntry entry)
            {
                if (entry.HasException) { Error(entry.Exception, entry.Message, entry.Context, entry.Category); return; }
                switch (entry.Level)
                {
                    case Convai.Domain.Logging.LogLevel.Debug:
                    case Convai.Domain.Logging.LogLevel.Trace: Debug(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Info: Info(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Warning: Warning(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Error: Error(entry.Message, entry.Context, entry.Category); break;
                }
            }
            public List<string> DebugMessages { get; } = new();
            public List<string> InfoMessages { get; } = new();
            public List<string> WarningMessages { get; } = new();
            public bool DebugEnabled { get; set; }

            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }

            public void Debug(string message, LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Debug(message, category);

            public void Info(string message, LogCategory category = LogCategory.SDK) => InfoMessages.Add(message);

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Info(message, category);

            public void Warning(string message, LogCategory category = LogCategory.SDK) => WarningMessages.Add(message);

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Warning(message, category);

            public void Error(string message, LogCategory category = LogCategory.SDK) { }

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }

            public void Error(Exception exception, string message = null,
                LogCategory category = LogCategory.SDK) { }

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }

            public bool IsEnabled(LogLevel level, LogCategory category) => level != LogLevel.Debug || DebugEnabled;
        }

        [Test]
        public void CompletedIndexedResponse_EmitsOneInfoSummary()
        {
            RecordingTestLogger logger = new() { DebugEnabled = false };
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit, logger: logger);
            bridge.Bind(eventHub, "char-1");

            CompleteResponse(bridge, engine, eventHub);

            Assert.AreEqual(1, logger.InfoMessages.Count);
            StringAssert.Contains("Response complete:", logger.InfoMessages[0]);
            StringAssert.Contains("response:response-1", logger.InfoMessages[0]);
            StringAssert.Contains("reason=StatsGrace", logger.InfoMessages[0]);
        }

        [Test]
        public void DebugDisabled_SuppressesDetailedDiagnostics()
        {
            RecordingTestLogger logger = new() { DebugEnabled = false };
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit, logger: logger);
            bridge.Bind(eventHub, "char-1");

            eventHub.Publish(LipSyncPackedDataReceived.Create(
                "char-1", "participant-1", CreateDiagnosticsChunk()));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            Assert.IsEmpty(logger.DebugMessages);
        }

        [Test]
        public void DebugEnabled_RetainsOwnerAndSampleTiming()
        {
            RecordingTestLogger logger = new() { DebugEnabled = true };
            MockRoomAudioService roomAudioService = new()
            {
                AudioTimeline = new AudioTimelineSnapshot(
                    1200, 1000, 1, 1, 32, AudioTimelinePlaybackState.Playing,
                    1232, 1200, 0, 0, 0)
            };
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(
                engine, LipSyncProfileId.ARKit, roomAudioService, logger);
            bridge.Bind(eventHub, "char-1");

            eventHub.Publish(LipSyncPackedDataReceived.Create(
                "char-1", "participant-1", CreateDiagnosticsChunk()));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            Assert.IsTrue(logger.DebugMessages.Exists(message => message.Contains("response:response-1")));
            Assert.IsTrue(logger.DebugMessages.Exists(message => message.Contains("sample=state:Playing")));
            Assert.IsTrue(logger.DebugMessages.Exists(message => message.Contains("frame:1200")));
        }

        [Test]
        public void InvalidSampleAnchorBounds_EmitOneWarning()
        {
            RecordingTestLogger logger = new() { DebugEnabled = false };
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit, logger: logger);
            bridge.Bind(eventHub, "char-1");

            eventHub.Publish(new AudioTimelineSampleAnchor(
                "char-1", "response-1", 1, 0, 1, 48000, 1000, 999));

            Assert.AreEqual(1, logger.WarningMessages.Count);
            StringAssert.Contains("invalid bounds", logger.WarningMessages[0]);
        }

        private static void CompleteResponse(
            ConvaiLipSyncBridge bridge,
            LipSyncPlaybackEngine engine,
            EventHub eventHub)
        {
            eventHub.Publish(LipSyncPackedDataReceived.Create(
                "char-1", "participant-1", CreateDiagnosticsChunk()));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);
            eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1", "participant-1", 8, 8, 1600, 200d, 200d, 60d,
                "response-1", 1, 0, 9));
            bridge.Tick(0.5f);
            engine.Tick(10d, 1f / 60f);
            for (int i = 0; i < 5 && engine.State == PlaybackState.FadingOut; i++)
                engine.Tick(10d, LipSyncConstants.MaxDeltaTimeForFade);
            bridge.Tick(0f);
        }

        private static LipSyncPackedChunk CreateDiagnosticsChunk()
        {
            float[][] frames = new float[8][];
            for (int i = 0; i < frames.Length; i++) frames[i] = new[] { i / 8f };

            return new LipSyncPackedChunk(
                LipSyncProfileId.ARKit,
                60f,
                new[] { "jawOpen" },
                frames,
                "response-1",
                1,
                0,
                0,
                1);
        }
    }
}
