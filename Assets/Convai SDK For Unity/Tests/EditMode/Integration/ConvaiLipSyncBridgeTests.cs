using System;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Integration
{
    [TestFixture]
    [Category("Integration")]
    public class ConvaiLipSyncBridgeTests
    {
        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        [Test]
        public void Bind_WithMatchingCharacterAndProfile_BeginsBufferingWhenDataArrives()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 3)));

            // Assert
            Assert.AreEqual(PlaybackState.Buffering, engine.State);
            Assert.Greater(engine.BufferedDuration, 0f);
        }

        [Test]
        public void Bind_WithMismatchedProfile_DropsDataAndKeepsEngineIdle()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.MetaHuman, 3)));

            // Assert
            Assert.AreEqual(PlaybackState.Idle, engine.State);
            Assert.AreEqual(0f, engine.BufferedDuration, 0.0001f);
        }

        [Test]
        public void Bind_WhenEventCharacterIdIsEmpty_DropsEventAndKeepsEngineIdle()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(LipSyncPackedDataReceived.Create(string.Empty, "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 3)));

            // Assert
            Assert.AreEqual(PlaybackState.Idle, engine.State);
            Assert.AreEqual(0f, engine.BufferedDuration, 0.0001f);
        }

        [Test]
        public void Bind_WhenEventCharacterIdTargetsDifferentCharacter_DropsEventAndKeepsEngineIdle()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-2", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 3)));

            // Assert
            Assert.AreEqual(PlaybackState.Idle, engine.State);
            Assert.AreEqual(0f, engine.BufferedDuration, 0.0001f);
        }

        [Test]
        public void OnAudioPlaybackStateChanged_WhenPlaybackStarts_AllowsEngineToEnterPlaying()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void OnPackedDataReceived_WhenPlaybackStartedBeforeFirstChunk_ReappliesGateAndEntersPlaying()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void OnPackedDataReceived_WhenPlaybackHadStoppedBeforeFirstChunk_RemainsBuffering()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, engine.State);
        }

        [Test]
        public void OnAudioPlaybackStateChanged_WhenWebGLPlaybackStartsBeforeSpeech_AllowsEngineToEnterPlaying()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            MockRoomAudioService roomAudioService = new()
            {
                RequiresUserGestureForAudio = true, IsAudioPlaybackActive = true
            };
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));

            // Act
            bool updated = engine.Tick(0.02d, 1f / 60f);
            eventHub.Publish(CharacterSpeechStateChanged.Create("char-1", true));

            // Assert
            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void OnPackedDataReceived_WhenWebGLSpeechAndPlaybackArriveBeforeFirstChunk_ReappliesGateAndEntersPlaying()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            MockRoomAudioService roomAudioService = new()
            {
                RequiresUserGestureForAudio = true, IsAudioPlaybackActive = true
            };
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(CharacterSpeechStateChanged.Create("char-1", true));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void OnAudioPlaybackStateChanged_WhenPlaybackStopsAfterPlaying_StartsFadeOut()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, engine.State);

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, engine.State);
        }

        [Test]
        public void OnAudioPlaybackStateChanged_WhenIndexedPlaybackStops_DoesNotFadeBufferedAheadData()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(LipSyncProfileId.ARKit, 12, 0, "response-1", 1, 0, 1)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, engine.State);

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));

            // Assert
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void OnAudioPlaybackStateChanged_WhenIndexedCompletionIsPending_FadesLastPoseToNeutral()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(LipSyncProfileId.ARKit, 12, 0, "response-1", 1, 0, 1)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.1d, 1f / 60f);
            engine.Tick(0.12d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, engine.State);
            Assert.Greater(engine.OutputValues[0], 0f);
            eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1",
                "participant-1",
                12,
                12,
                200,
                200d,
                200d,
                60d));

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, engine.State);
            for (int i = 0; i < 30; i++)
                engine.Tick(0.1d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Idle, engine.State);
            Assert.AreEqual(0f, engine.OutputValues[0], 0.0001f);
        }

        [Test]
        public void OnSpeechStateChanged_WhenIndexedStreamStops_DoesNotMarkStreamEnd()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(LipSyncProfileId.ARKit, 8, 0, "response-1", 1, 0, 1)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);

            // Act
            eventHub.Publish(CharacterSpeechStateChanged.Create("char-1", false));
            engine.Tick(10d, 1f / 60f);

            // Assert
            Assert.AreEqual(PlaybackState.Starving, engine.State);
        }

        [Test]
        public void OnSpeechStateChanged_WhenIndexedStreamStartsWithoutAudioPlaybackEvent_KeepsPlaybackBuffered()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(LipSyncProfileId.ARKit, 12, 0, "response-1", 1, 0, 1)));
            Assert.AreEqual(PlaybackState.Buffering, engine.State);

            // Act
            eventHub.Publish(CharacterSpeechStateChanged.Create("char-1", true));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, engine.State);
        }

        [Test]
        public void OnAudioPlaybackStateChanged_WhenIndexedStreamBuffered_StartsPlaybackGate()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(LipSyncProfileId.ARKit, 12, 0, "response-1", 1, 0, 1)));
            eventHub.Publish(CharacterSpeechStateChanged.Create("char-1", true));
            Assert.AreEqual(PlaybackState.Buffering, engine.State);

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsTrue(updated);
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void OnBlendshapeTurnStatsReceived_WhenIndexedFrameCountsMatch_MarksStreamEnd()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(LipSyncProfileId.ARKit, 8, 0, "response-1", 1, 0, 1)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);

            // Act
            eventHub.Publish(BlendshapeTurnStatsReceived.Create(
                "char-1",
                "participant-1",
                8,
                8,
                1600,
                200d,
                200d,
                60d));
            bridge.Tick(0.5f);
            engine.Tick(10d, 1f / 60f);

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, engine.State);
        }

        [Test]
        public void OnTimelineResetRequested_WhenActiveIndexedOwnerMatchesWithoutValidThrough_FadesSmoothly()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateIndexedChunk(LipSyncProfileId.ARKit, 8, 0, "response-1", 1, 0, 1)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);
            Assert.AreEqual(PlaybackState.Playing, engine.State);

            // Act
            eventHub.Publish(LipSyncTimelineResetRequested.Create(
                "char-1",
                "participant-1",
                "response-1",
                1,
                0,
                2,
                null,
                "interrupt"));

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, engine.State);
        }

        [Test]
        public void OnAudioPlaybackStateChanged_WhenPlaybackRestartsDuringFade_ResumesPlaying()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Stopped("char-1"));
            Assert.AreEqual(PlaybackState.FadingOut, engine.State);

            // Act
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));

            // Assert
            Assert.AreEqual(PlaybackState.Playing, engine.State);
        }

        [Test]
        public void OnPackedDataReceived_WhenWebGLAudioIsAlreadyActive_RemainsBufferingWithoutCharacterAudioEvent()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            MockRoomAudioService roomAudioService = new()
            {
                RequiresUserGestureForAudio = true, IsAudioPlaybackActive = true
            };
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, engine.State);
        }

        [Test]
        public void OnPackedDataReceived_WhenWebGLAudioIsInactive_RemainsBuffering()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            MockRoomAudioService roomAudioService = new()
            {
                RequiresUserGestureForAudio = true, IsAudioPlaybackActive = false
            };
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit, roomAudioService);
            bridge.Bind(eventHub, "char-1");

            // Act
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 12)));
            bool updated = engine.Tick(0.02d, 1f / 60f);

            // Assert
            Assert.IsFalse(updated);
            Assert.AreEqual(PlaybackState.Buffering, engine.State);
        }

        [Test]
        [Description(
            "Regression: Stop-speaking signal must force stream-end and fade-out instead of starving indefinitely.")]
        public void OnSpeechStateChanged_WhenSpeakingStops_TransitionsEngineTowardFadeOut()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 8)));
            eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("char-1"));
            engine.Tick(0.02d, 1f / 60f);

            // Act
            eventHub.Publish(CharacterSpeechStateChanged.Create("char-1", false));
            engine.Tick(10d, 1f / 60f);

            // Assert
            Assert.AreEqual(PlaybackState.FadingOut, engine.State);
        }

        [Test]
        public void Unbind_AfterBinding_IgnoresSubsequentEvents()
        {
            // Arrange
            LipSyncPlaybackEngine engine = new(new LipSyncEngineConfig(timeOffsetSeconds: 0f));
            EventHub eventHub = new(new ImmediateScheduler());
            using ConvaiLipSyncBridge bridge = new(engine, LipSyncProfileId.ARKit);
            bridge.Bind(eventHub, "char-1");
            bridge.Unbind();

            // Act
            eventHub.Publish(LipSyncPackedDataReceived.Create("char-1", "participant-1",
                CreateChunk(LipSyncProfileId.ARKit, 3)));

            // Assert
            Assert.AreEqual(PlaybackState.Idle, engine.State);
        }

        private static LipSyncPackedChunk CreateChunk(LipSyncProfileId profileId, int frameCount)
        {
            float[][] frames = new float[frameCount][];
            for (int i = 0; i < frameCount; i++) frames[i] = new[] { i / (float)Math.Max(1, frameCount) };

            return new LipSyncPackedChunk(profileId, 60f, new[] { "jawOpen" }, frames);
        }

        private static LipSyncPackedChunk CreateIndexedChunk(
            LipSyncProfileId profileId,
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
                profileId,
                60f,
                new[] { "jawOpen" },
                frames,
                responseId,
                neuroSyncTurnId,
                epoch,
                startFrameIndex,
                sequence);
        }
    }
}
