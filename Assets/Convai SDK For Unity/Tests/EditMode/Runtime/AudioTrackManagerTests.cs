using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.Emotion;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Networking.Media;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Unit tests for AudioTrackManager covering publishing, subscription,
    ///     mute functionality, character audio routing, and cleanup scenarios.
    /// </summary>
    [TestFixture]
    public class AudioTrackManagerTests
    {
        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        [SetUp]
        public void SetUp()
        {
            _logger = new TestLogger();
            _agentRegistry = new MockAgentRegistry();
            _audioStreamFactory = new TestAudioStreamFactory();
            _audioSources = new Dictionary<string, AudioSource>();
            _createdObjects = new List<GameObject>();
            _nullRoom = null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _createdObjects.Clear();
            _audioSources.Clear();
            _audioStreamFactory.Dispose();
        }

        private sealed class TestLogger : ILogger
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
            public List<string> ErrorMessages { get; } = new();
            public bool DebugEnabled { get; set; } = true;

            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Debug(message, category);

            public void Info(string message, LogCategory category = LogCategory.SDK) => InfoMessages.Add(message);

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Info(message, category);

            public void Warning(string message, LogCategory category = LogCategory.SDK) => WarningMessages.Add(message);

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Warning(message, category);

            public void Error(string message, LogCategory category = LogCategory.SDK) => ErrorMessages.Add(message);

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Error(message, category);

            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) =>
                ErrorMessages.Add(message ?? exception.Message);

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Error(exception, message, category);

            public bool IsEnabled(LogLevel level, LogCategory category) => level != LogLevel.Debug || DebugEnabled;
        }

        private sealed class TestAudioStreamFactory : IAudioStreamFactory, IDisposable
        {
            public int CreateCallCount { get; private set; }
            public int DisposeCallCount { get; private set; }
            public AudioSource LastSource { get; private set; }
            public bool ReturnNull { get; set; }
            public Func<IDisposable> StreamFactoryOverride { get; set; }

            public IDisposable Create(IRemoteAudioTrack track, AudioSource source)
            {
                CreateCallCount++;
                LastSource = source;
                if (ReturnNull) return null;
                if (StreamFactoryOverride != null) return StreamFactoryOverride();
                return new TestAudioStream(() => DisposeCallCount++);
            }

            public void Dispose() { }
        }

        private sealed class TestAudioStream : IDisposable
        {
            private readonly Action _onDispose;

            public TestAudioStream(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose() => _onDispose?.Invoke();
        }

        private sealed class TestPlaybackAudioStream : IDisposable, IAudioPlaybackStateSource,
            IAudioMediaTimelineSnapshotSource, IBargeInPlaybackControl
        {
            private readonly Action _onDispose;
            private Action _playbackStarted;

            public TestPlaybackAudioStream(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public event Action PlaybackStarted
            {
                add
                {
                    _playbackStarted += value;
                    if (StartImmediatelyOnSubscribe)
                        value?.Invoke();
                }
                remove => _playbackStarted -= value;
            }

            public event Action PlaybackStopped;
            public int DuckCount { get; private set; }
            public int CommitCount { get; private set; }
            public int RestoreCount { get; private set; }
            public float LastTargetGain { get; private set; }
            public float LastDurationSeconds { get; private set; }
            public bool PlaybackAlreadyActiveAfterRestore { get; set; }
            public bool StartImmediatelyOnSubscribe { get; set; }
            public bool IsDisposed { get; private set; }
            public AudioMediaTimelineSnapshot MediaTimeline { get; set; } =
                new(
                    1d,
                    AudioTimelinePlaybackState.Playing,
                    signalStartPositionSeconds: 1d,
                    signalGeneration: 1,
                    analyserAvailable: true);

            public void RaisePlaybackStarted() => _playbackStarted?.Invoke();
            public void RaisePlaybackStopped() => PlaybackStopped?.Invoke();

            public bool TryGetAudioMediaTimelineSnapshot(out AudioMediaTimelineSnapshot snapshot)
            {
                snapshot = MediaTimeline;
                return snapshot.IsValid;
            }

            public void Duck(float targetGain, float durationSeconds)
            {
                DuckCount++;
                LastTargetGain = targetGain;
                LastDurationSeconds = durationSeconds;
            }

            public void CommitInterruption(float durationSeconds)
            {
                CommitCount++;
                LastDurationSeconds = durationSeconds;
            }

            public bool Restore(float durationSeconds)
            {
                RestoreCount++;
                LastDurationSeconds = durationSeconds;
                return PlaybackAlreadyActiveAfterRestore;
            }

            public void Dispose()
            {
                IsDisposed = true;
                _onDispose?.Invoke();
            }
        }

        /// <summary>
        ///     Minimal mock implementation of IConvaiCharacterAgent for testing.
        /// </summary>
        private sealed class TestCharacterAgent : IConvaiCharacterAgent
        {
            public TestCharacterAgent(string characterId, string characterName = "Test Character")
            {
                CharacterId = characterId;
                CharacterName = characterName;
            }

            public string CharacterId { get; }
            public string CharacterName { get; }
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext { get; } = new MockDynamicContext();
            public EmotionDetectionMode EmotionDetectionMode => Convai.Domain.Emotion.EmotionDetectionMode.Off;
            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }
        }

        private TestLogger _logger;
        private MockAgentRegistry _agentRegistry;
        private TestAudioStreamFactory _audioStreamFactory;
        private Dictionary<string, AudioSource> _audioSources;
        private List<GameObject> _createdObjects;
        private IRoomFacade _nullRoom;

        private AudioSource CreateAudioSource(string characterId)
        {
            var go = new GameObject($"AudioSource_{characterId}");
            _createdObjects.Add(go);
            var source = go.AddComponent<AudioSource>();
            _audioSources[characterId] = source;
            return source;
        }

        private AudioTrackManager CreateManager(Func<IRoomFacade> roomFacadeProvider = null)
        {
            return new AudioTrackManager(
                roomFacadeProvider ?? (() => _nullRoom),
                _agentRegistry,
                _logger,
                characterId => _audioSources.TryGetValue(characterId, out AudioSource src) ? src : null,
                null,
                _audioStreamFactory
            );
        }

        private AudioTrackManager CreateManager(IEventHub eventHub, Func<IRoomFacade> roomFacadeProvider = null)
        {
            return new AudioTrackManager(
                roomFacadeProvider ?? (() => _nullRoom),
                _agentRegistry,
                _logger,
                characterId => _audioSources.TryGetValue(characterId, out AudioSource src) ? src : null,
                null,
                _audioStreamFactory,
                eventHub
            );
        }

        private AudioTrackManager CreateManagerWithContinuouslyPlayingStream(
            out TestPlaybackAudioStream playbackStream)
        {
            AudioTrackManager manager = CreateManagerWithContinuouslyPlayingStreams(
                new[] { "test-char-1" },
                out Dictionary<string, TestPlaybackAudioStream> playbackStreams);
            playbackStream = playbackStreams["test-char-1"];
            return manager;
        }

        private AudioTrackManager CreateManagerWithContinuouslyPlayingStreams(
            IReadOnlyList<string> characterIds,
            out Dictionary<string, TestPlaybackAudioStream> playbackStreams)
        {
            var createdStreams = new Dictionary<string, TestPlaybackAudioStream>(StringComparer.Ordinal);
            int nextStreamIndex = 0;
            _audioStreamFactory.StreamFactoryOverride = () =>
            {
                string characterId = characterIds[nextStreamIndex++];
                var stream = new TestPlaybackAudioStream(() => { })
                {
                    PlaybackAlreadyActiveAfterRestore = true
                };
                createdStreams.Add(characterId, stream);
                return stream;
            };

            AudioTrackManager manager = CreateManager();
            for (int i = 0; i < characterIds.Count; i++)
            {
                string characterId = characterIds[i];
                CreateAudioSource(characterId);
                _agentRegistry.RegisterCharacter(new TestCharacterAgent(characterId, $"Test {i + 1}"));
                manager.HandleRemoteAudioTrackSubscribed(
                    null,
                    $"participant-{i + 1}",
                    characterId);
                createdStreams[characterId].RaisePlaybackStarted();
            }

            playbackStreams = createdStreams;
            return manager;
        }

        [Test]
        public void AudioTrackManager_ImplementsIAudioTrackManager()
        {
            using AudioTrackManager manager = CreateManager();
            Assert.IsTrue(manager is IAudioTrackManager);
        }

        [Test]
        public void Constructor_ThrowsOnNullRoomProvider()
        {
            Assert.Throws<ArgumentNullException>(() => new AudioTrackManager(
                null,
                _agentRegistry,
                _logger,
                id => null
            ));
        }

        [Test]
        public void Constructor_ThrowsOnNullAgentRegistry()
        {
            Assert.Throws<ArgumentNullException>(() => new AudioTrackManager(
                () => _nullRoom,
                null,
                _logger,
                id => null
            ));
        }

        [Test]
        public void Constructor_ThrowsOnNullAudioSourceResolver()
        {
            Assert.Throws<ArgumentNullException>(() => new AudioTrackManager(
                () => _nullRoom,
                _agentRegistry,
                _logger,
                null
            ));
        }

        [Test]
        public void IsMicMuted_DefaultsFalse()
        {
            using AudioTrackManager manager = CreateManager();
            Assert.IsFalse(manager.IsMicMuted);
        }

        [Test]
        public void SetMicMuted_UpdatesState()
        {
            using AudioTrackManager manager = CreateManager();
            manager.SetMicMuted(true);
            Assert.IsTrue(manager.IsMicMuted);

            manager.SetMicMuted(false);
            Assert.IsFalse(manager.IsMicMuted);
        }

        [Test]
        public void SetMicMuted_RaisesEventOnChange()
        {
            using AudioTrackManager manager = CreateManager();
            bool? eventValue = null;
            manager.OnMicMuteChanged += muted => eventValue = muted;

            manager.SetMicMuted(true);
            Assert.AreEqual(true, eventValue);

            manager.SetMicMuted(false);
            Assert.AreEqual(false, eventValue);
        }

        [Test]
        public void SetMicMuted_DoesNotRaiseEventWhenUnchanged()
        {
            using AudioTrackManager manager = CreateManager();
            int eventCount = 0;
            manager.OnMicMuteChanged += _ => eventCount++;

            manager.SetMicMuted(false);
            Assert.AreEqual(0, eventCount, "Should not raise event when state unchanged");
        }

        [Test]
        public void ToggleMicMute_TogglesState()
        {
            using AudioTrackManager manager = CreateManager();
            Assert.IsFalse(manager.IsMicMuted);

            manager.ToggleMicMute();
            Assert.IsTrue(manager.IsMicMuted);

            manager.ToggleMicMute();
            Assert.IsFalse(manager.IsMicMuted);
        }

        [Test]
        public void SetCharacterAudioMuted_ReturnsFalseForNullCharacterId()
        {
            using AudioTrackManager manager = CreateManager();
            Assert.IsFalse(manager.SetCharacterAudioMuted(null, true));
            Assert.IsFalse(manager.SetCharacterAudioMuted("", true));
        }

        [Test]
        public void SetCharacterAudioMuted_ReturnsFalseForUnregisteredCharacter()
        {
            using AudioTrackManager manager = CreateManager();
            Assert.IsFalse(manager.SetCharacterAudioMuted("unknown-character", true));
        }

        [Test]
        public void SetCharacterAudioMuted_UpdatesRegisteredCharacter()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            AudioSource audioSource = CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            bool result = manager.SetCharacterAudioMuted(characterId, true);

            Assert.IsTrue(result);
            Assert.IsTrue(audioSource.mute);
        }

        [Test]
        public void IsCharacterAudioMuted_ReturnsFalseForNullCharacterId()
        {
            using AudioTrackManager manager = CreateManager();
            Assert.IsFalse(manager.IsCharacterAudioMuted(null));
            Assert.IsFalse(manager.IsCharacterAudioMuted(""));
        }

        [Test]
        public void IsCharacterAudioMuted_ReturnsCorrectState()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);
            _agentRegistry.SetCharacterMuted(characterId, true);

            Assert.IsTrue(manager.IsCharacterAudioMuted(characterId));
        }

        [Test]
        public void ClearState_ClearsRemoteAudio()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            AudioSource audioSource = CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.AreEqual(1, _audioStreamFactory.CreateCallCount, "Stream should be created");

            manager.ClearState();

            Assert.AreEqual(1, _audioStreamFactory.DisposeCallCount, "ClearState should dispose audio streams");
        }

        [Test]
        public void ClearRemoteAudio_DisposesAllStreams()
        {
            using AudioTrackManager manager = CreateManager();

            for (int i = 1; i <= 3; i++)
            {
                string characterId = $"test-char-{i}";
                CreateAudioSource(characterId);
                var agent = new TestCharacterAgent(characterId, $"Test{i}");
                _agentRegistry.RegisterCharacter(agent);
                manager.HandleRemoteAudioTrackSubscribed(null, $"participant-{i}", characterId);
            }

            Assert.AreEqual(3, _audioStreamFactory.CreateCallCount);

            manager.ClearRemoteAudio();

            Assert.AreEqual(3, _audioStreamFactory.DisposeCallCount, "All streams should be disposed");
        }

        [Test]
        public void Dispose_ClearsAllResources()
        {
            AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);
            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            manager.Dispose();

            Assert.AreEqual(1, _audioStreamFactory.DisposeCallCount);

            Assert.Throws<ObjectDisposedException>(() => manager.SetMicMuted(true));
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            AudioTrackManager manager = CreateManager();
            manager.Dispose();
            manager.Dispose();
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_CreatesAudioStream()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.AreEqual(1, _audioStreamFactory.CreateCallCount);
        }

        [Test]
        public void BindParticipantAudioOutput_RoutesTrackSubscribedBeforeBinding()
        {
            using AudioTrackManager manager = CreateManager();
            AudioSource source = CreateAudioSource("human-participant");

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", "human-1");
            Assert.AreEqual(0, _audioStreamFactory.CreateCallCount);

            Assert.IsTrue(manager.BindParticipantAudioOutput("human-1", source));

            Assert.AreEqual(1, _audioStreamFactory.CreateCallCount);
            Assert.AreSame(source, _audioStreamFactory.LastSource);
        }

        [Test]
        public void BindParticipantAudioOutput_RebindsSubscribedTrackToNewSource()
        {
            using AudioTrackManager manager = CreateManager();
            AudioSource firstSource = CreateAudioSource("human-participant-first");
            AudioSource secondSource = CreateAudioSource("human-participant-second");
            manager.BindParticipantAudioOutput("human-1", firstSource);
            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", "human-1");

            Assert.IsTrue(manager.BindParticipantAudioOutput("human-1", secondSource));

            Assert.AreEqual(2, _audioStreamFactory.CreateCallCount);
            Assert.AreEqual(1, _audioStreamFactory.DisposeCallCount);
            Assert.AreSame(secondSource, _audioStreamFactory.LastSource);
        }

        [Test]
        public void BindParticipantAudioOutput_CharacterPlayback_PublishesPlaybackState()
        {
            EventHub eventHub = new(new ImmediateScheduler());
            TestPlaybackAudioStream playbackStream = null;
            _audioStreamFactory.StreamFactoryOverride = () =>
            {
                playbackStream = new TestPlaybackAudioStream(() => { });
                return playbackStream;
            };
            using AudioTrackManager manager = CreateManager(eventHub);
            const string characterId = "test-char-1";
            AudioSource source = CreateAudioSource(characterId);
            _agentRegistry.RegisterCharacter(new TestCharacterAgent(characterId, "Test"));
            _agentRegistry.SetParticipantId(characterId, "participant-1");
            var playbackStates = new List<bool>();
            eventHub.Subscribe<CharacterAudioPlaybackStateChanged>(evt =>
            {
                if (evt.CharacterId == characterId) playbackStates.Add(evt.IsPlaying);
            });

            Assert.IsTrue(manager.BindParticipantAudioOutput(characterId, source));
            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);
            playbackStream.RaisePlaybackStarted();
            playbackStream.RaisePlaybackStopped();

            CollectionAssert.AreEqual(new[] { true, false }, playbackStates);
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_PreservesAudioSourcePlaybackSettings()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            AudioSource audioSource = CreateAudioSource(characterId);
            audioSource.playOnAwake = true;
            audioSource.loop = true;
            audioSource.volume = 0.5f;
            audioSource.priority = 42;
            audioSource.spatialBlend = 1f;
            audioSource.minDistance = 2f;
            audioSource.maxDistance = 20f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.IsTrue(audioSource.playOnAwake);
            Assert.IsTrue(audioSource.loop);
            Assert.AreEqual(0.5f, audioSource.volume);
            Assert.AreEqual(42, audioSource.priority);
            Assert.AreEqual(1f, audioSource.spatialBlend);
            Assert.AreEqual(2f, audioSource.minDistance);
            Assert.AreEqual(20f, audioSource.maxDistance);
            Assert.AreEqual(AudioRolloffMode.Linear, audioSource.rolloffMode);
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_AppliesRegistryMuteWithoutResettingVolume()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            AudioSource audioSource = CreateAudioSource(characterId);
            audioSource.volume = 0.35f;
            audioSource.spatialBlend = 1f;
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);
            _agentRegistry.SetCharacterMuted(characterId, true);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.IsTrue(audioSource.mute);
            Assert.AreEqual(0.35f, audioSource.volume);
            Assert.AreEqual(1f, audioSource.spatialBlend);
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_LogsErrorWhenCharacterNotFound()
        {
            using AudioTrackManager manager = CreateManager();

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", "unknown-character");

            Assert.IsTrue(_logger.ErrorMessages.Count > 0);
            Assert.IsTrue(_logger.ErrorMessages[0].Contains("FAILED to resolve Character"));
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_LogsErrorWhenNoAudioSource()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.IsTrue(_logger.ErrorMessages.Count > 0);
            Assert.IsTrue(_logger.ErrorMessages[0].Contains("does not have an AudioSource"));
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_DisposesExistingStream()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);
            Assert.AreEqual(1, _audioStreamFactory.CreateCallCount);
            Assert.AreEqual(0, _audioStreamFactory.DisposeCallCount);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-2", characterId);
            Assert.AreEqual(2, _audioStreamFactory.CreateCallCount);
            Assert.AreEqual(1, _audioStreamFactory.DisposeCallCount);
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_WhenPlaybackStarts_PublishesCharacterAudioPlaybackStateChanged()
        {
            EventHub eventHub = new(new ImmediateScheduler());
            TestPlaybackAudioStream playbackStream = null;
            _audioStreamFactory.StreamFactoryOverride = () =>
            {
                playbackStream = new TestPlaybackAudioStream(() => { });
                return playbackStream;
            };

            using AudioTrackManager manager = CreateManager(eventHub);
            string characterId = "test-char-1";
            CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            CharacterAudioPlaybackStateChanged? publishedEvent = null;
            eventHub.Subscribe<CharacterAudioPlaybackStateChanged>(evt => publishedEvent = evt);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);
            playbackStream.RaisePlaybackStarted();

            Assert.IsTrue(publishedEvent.HasValue);
            Assert.AreEqual(characterId, publishedEvent.Value.CharacterId);
            Assert.IsTrue(publishedEvent.Value.IsPlaying);
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_WhenFirstWebGLElementIsAlreadyPlaying_ExposesTimingBeforeStartEvent()
        {
            EventHub eventHub = new(new ImmediateScheduler());
            _audioStreamFactory.StreamFactoryOverride = () =>
                new TestPlaybackAudioStream(() => { }) { StartImmediatelyOnSubscribe = true };

            using AudioTrackManager manager = CreateManager(eventHub);
            const string characterId = "test-char-1";
            CreateAudioSource(characterId);
            _agentRegistry.RegisterCharacter(new TestCharacterAgent(characterId, "Test"));

            bool timingWasAvailableDuringStart = false;
            eventHub.Subscribe<CharacterAudioPlaybackStateChanged>(evt =>
            {
                if (!evt.IsPlaying) return;

                timingWasAvailableDuringStart =
                    manager.TryGetAudioMediaTimeline(evt.CharacterId, out AudioMediaTimelineSnapshot snapshot) &&
                    snapshot.State == AudioTimelinePlaybackState.Playing;
            });

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.IsTrue(
                timingWasAvailableDuringStart,
                "The first playback callback must not run before its WebGL media clock is registered.");
        }

        [Test]
        public void BargeInPlayback_OnlyAffectsActiveStream_AndRestoresNextResponse()
        {
            EventHub eventHub = new(new ImmediateScheduler());
            var playbackStates = new List<bool>();
            eventHub.Subscribe<CharacterAudioPlaybackStateChanged>(evt => playbackStates.Add(evt.IsPlaying));
            TestPlaybackAudioStream playbackStream = null;
            _audioStreamFactory.StreamFactoryOverride = () =>
            {
                playbackStream = new TestPlaybackAudioStream(() => { });
                return playbackStream;
            };

            using AudioTrackManager manager = CreateManager(eventHub);
            const string characterId = "test-char-1";
            CreateAudioSource(characterId);
            _agentRegistry.RegisterCharacter(new TestCharacterAgent(characterId, "Test"));

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.That(manager.CommitActiveCharacterAudioInterruption(0.12f), Is.Zero);

            playbackStream.RaisePlaybackStarted();
            CollectionAssert.AreEqual(new[] { true }, playbackStates);
            Assert.That(manager.DuckActiveCharacterAudio(0.25f, 0.05f), Is.EqualTo(1));
            Assert.That(playbackStream.DuckCount, Is.EqualTo(1));
            Assert.That(playbackStream.LastTargetGain, Is.EqualTo(0.25f));

            Assert.That(manager.CommitActiveCharacterAudioInterruption(0.12f), Is.EqualTo(1));
            Assert.That(playbackStream.CommitCount, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { true, false }, playbackStates);

            Assert.That(manager.RestoreInterruptedCharacterAudio(0.1f), Is.EqualTo(1));
            Assert.That(playbackStream.RestoreCount, Is.EqualTo(1));
            CollectionAssert.AreEqual(
                new[] { true, false },
                playbackStates,
                "restore must wait for fresh PCM before publishing the next playback start");

            playbackStream.RaisePlaybackStarted();
            CollectionAssert.AreEqual(new[] { true, false, true }, playbackStates);
        }

        [Test]
        public void BargeInPlayback_WhenRestoreReportsContinuousPlayback_RepublishesStarted()
        {
            EventHub eventHub = new(new ImmediateScheduler());
            var playbackStates = new List<bool>();
            eventHub.Subscribe<CharacterAudioPlaybackStateChanged>(evt => playbackStates.Add(evt.IsPlaying));
            TestPlaybackAudioStream playbackStream = null;
            _audioStreamFactory.StreamFactoryOverride = () =>
            {
                playbackStream = new TestPlaybackAudioStream(() => { })
                {
                    PlaybackAlreadyActiveAfterRestore = true
                };
                return playbackStream;
            };

            using AudioTrackManager manager = CreateManager(eventHub);
            const string characterId = "test-char-1";
            CreateAudioSource(characterId);
            _agentRegistry.RegisterCharacter(new TestCharacterAgent(characterId, "Test"));

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);
            playbackStream.RaisePlaybackStarted();
            Assert.That(
                manager.TryGetAudioMediaTimeline(characterId, out AudioMediaTimelineSnapshot initialTimeline),
                Is.True);
            Assert.That(initialTimeline.State, Is.EqualTo(AudioTimelinePlaybackState.Playing));

            Assert.That(manager.CommitActiveCharacterAudioInterruption(0.12f), Is.EqualTo(1));
            Assert.That(
                playbackStream.MediaTimeline.State,
                Is.EqualTo(AudioTimelinePlaybackState.Playing),
                "the browser element intentionally remains playing while its gain is zero");
            Assert.That(
                manager.TryGetAudioMediaTimeline(characterId, out _),
                Is.False,
                "logical interruption must hide the raw playing browser snapshot");

            playbackStream.RaisePlaybackStarted();
            CollectionAssert.AreEqual(
                new[] { true, false },
                playbackStates,
                "a browser playing callback must not resurrect muted playback");

            Assert.That(manager.RestoreInterruptedCharacterAudio(0.1f), Is.EqualTo(1));

            CollectionAssert.AreEqual(new[] { true, false, true }, playbackStates);
            Assert.That(manager.TryGetAudioMediaTimeline(characterId, out _), Is.True);
        }

        [Test]
        public void BargeInCoordinator_VoiceBeforeFirstCharacterSpeech_DoesNotInterruptContinuousWebGLTrack()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            Assert.IsTrue(manager.HasActiveCharacterAudioPlayback,
                "WebGL reports the continuously attached media element as playing between turns");
            Assert.IsFalse(coordinator.HasActivePlayback,
                "transport activity alone is not evidence that the character is speaking");

            bool ducked = coordinator.Duck(BargeInTrigger.ClientVoiceActivity);
            bool committed = coordinator.Commit(BargeInTrigger.ServerVoiceActivity);

            Assert.IsFalse(ducked);
            Assert.IsFalse(committed);
            Assert.IsFalse(coordinator.IsInterrupted);
            Assert.AreEqual(0, playbackStream.DuckCount);
            Assert.AreEqual(0, playbackStream.CommitCount);
        }

        [Test]
        public void BargeInCoordinator_VoiceDuringCharacterSpeech_InterruptsAndRestoresNextResponse()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1", "response-1"));
            Assert.IsTrue(coordinator.HasActivePlayback);
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));
            Assert.IsTrue(coordinator.IsInterrupted);
            Assert.AreEqual(1, playbackStream.CommitCount);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StoppedSpeaking("test-char-1", "response-1"));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1", "response-2"));

            Assert.IsFalse(coordinator.IsInterrupted);
            Assert.AreEqual(1, playbackStream.RestoreCount);
        }

        [Test]
        public void BargeInCoordinator_ManualInterruptionBeforeSpeech_DoesNotMuteFirstResponse()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            Assert.IsFalse(coordinator.Commit(BargeInTrigger.Manual));
            Assert.IsFalse(coordinator.IsInterrupted);
            Assert.AreEqual(0, playbackStream.CommitCount);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1", "response-1"));

            Assert.IsTrue(coordinator.HasActivePlayback);
            Assert.AreEqual(0, playbackStream.RestoreCount);
            Assert.AreEqual(0, playbackStream.CommitCount);
        }

        [Test]
        public void BargeInCoordinator_OnlyAffectsCharactersWithCurrentSpeechEvidence()
        {
            using AudioTrackManager manager = CreateManagerWithContinuouslyPlayingStreams(
                new[] { "character-a", "character-b" },
                out Dictionary<string, TestPlaybackAudioStream> playbackStreams);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("character-a", "response-a-1"));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StoppedSpeaking("character-b", "response-b-0"));

            Assert.IsTrue(
                coordinator.HasActivePlayback,
                "a stop from character B must not erase character A's speaking state");
            Assert.IsTrue(coordinator.Duck(BargeInTrigger.ClientVoiceActivity));
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));

            Assert.AreEqual(1, playbackStreams["character-a"].DuckCount);
            Assert.AreEqual(1, playbackStreams["character-a"].CommitCount);
            Assert.AreEqual(0, playbackStreams["character-b"].DuckCount);
            Assert.AreEqual(0, playbackStreams["character-b"].CommitCount);
        }

        [Test]
        public void BargeInCoordinator_CharacterStopsAfterDuck_IsRestoredInsteadOfInterrupted()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1"));
            Assert.IsTrue(coordinator.Duck(BargeInTrigger.ClientVoiceActivity));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StoppedSpeaking("test-char-1"));

            Assert.IsFalse(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));
            Assert.IsFalse(coordinator.IsInterrupted);
            Assert.AreEqual(0, playbackStream.CommitCount);
            Assert.AreEqual(1, playbackStream.RestoreCount);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1"));

            Assert.IsTrue(coordinator.HasActivePlayback);
            Assert.AreEqual(1, playbackStream.RestoreCount);
        }

        [Test]
        public void BargeInCoordinator_CommitIncludesSpeakingCharacterThatBecameActiveAfterDuck()
        {
            using AudioTrackManager manager = CreateManagerWithContinuouslyPlayingStreams(
                new[] { "character-a", "character-b" },
                out Dictionary<string, TestPlaybackAudioStream> playbackStreams);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            playbackStreams["character-b"].RaisePlaybackStopped();
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("character-a", "response-a-1"));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("character-b", "response-b-1"));

            Assert.IsTrue(coordinator.Duck(BargeInTrigger.ClientVoiceActivity));
            Assert.AreEqual(1, playbackStreams["character-a"].DuckCount);
            Assert.AreEqual(0, playbackStreams["character-b"].DuckCount);

            playbackStreams["character-b"].RaisePlaybackStarted();
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));

            Assert.AreEqual(1, playbackStreams["character-a"].CommitCount);
            Assert.AreEqual(1, playbackStreams["character-b"].CommitCount);
        }

        [Test]
        public void BargeInCoordinator_RestoresEachInterruptedCharacterOnItsOwnNextResponse()
        {
            using AudioTrackManager manager = CreateManagerWithContinuouslyPlayingStreams(
                new[] { "character-a", "character-b" },
                out Dictionary<string, TestPlaybackAudioStream> playbackStreams);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("character-a", "response-a-1"));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("character-b", "response-b-1"));
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.Manual));
            Assert.IsTrue(coordinator.IsInterrupted);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StoppedSpeaking("character-b", "response-b-1"));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("character-b", "response-b-2"));

            Assert.AreEqual(0, playbackStreams["character-a"].RestoreCount);
            Assert.AreEqual(1, playbackStreams["character-b"].RestoreCount);
            Assert.IsTrue(
                coordinator.IsInterrupted,
                "character A must remain interrupted until its own next response");

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StoppedSpeaking("character-a", "response-a-1"));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("character-a", "response-a-2"));

            Assert.AreEqual(1, playbackStreams["character-a"].RestoreCount);
            Assert.IsFalse(coordinator.IsInterrupted);
        }

        [Test]
        public void BargeInCoordinator_DuplicateStartWithoutUtteranceId_DoesNotUndoInterruption()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1"));
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1"));

            Assert.IsTrue(coordinator.IsInterrupted);
            Assert.AreEqual(0, playbackStream.RestoreCount);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StoppedSpeaking("test-char-1"));
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1"));

            Assert.IsFalse(coordinator.IsInterrupted);
            Assert.AreEqual(1, playbackStream.RestoreCount);
        }

        [Test]
        public void BargeInCoordinator_ConnectionBoundaryReset_DoesNotRestorePlayback()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);

            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1", "response-1"));
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));

            coordinator.ResetForConnectionBoundary();
            coordinator.ResetForConnectionBoundary();

            Assert.IsFalse(coordinator.IsInterrupted);
            Assert.IsFalse(coordinator.HasActivePlayback);
            Assert.AreEqual(0, playbackStream.RestoreCount);
        }

        [Test]
        public async Task RoomDisconnectRuntimeAdapter_ResetsBargeInBeforeRemovingAudioRegistrations()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1", "response-1"));
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));

            int resetCallCount = 0;
            bool firstResetRanBeforeStreamDisposal = false;
            var disconnectAdapter = new RoomDisconnectRuntimeAdapter(
                () => manager,
                () => null,
                (_, _) => { },
                (_, _, _) => { },
                () =>
                {
                    resetCallCount++;
                    if (resetCallCount == 1)
                        firstResetRanBeforeStreamDisposal = !playbackStream.IsDisposed;
                    coordinator.ResetForConnectionBoundary();
                });

            await disconnectAdapter.DisconnectAsync(CancellationToken.None);

            Assert.IsTrue(firstResetRanBeforeStreamDisposal);
            Assert.AreEqual(2, resetCallCount);
            Assert.IsTrue(playbackStream.IsDisposed);
            Assert.IsFalse(coordinator.IsInterrupted);
            Assert.AreEqual(
                0,
                playbackStream.RestoreCount,
                "teardown must not briefly restore or republish interrupted WebGL playback");
        }

        [Test]
        public void RoomDisconnectRuntimeAdapter_FailureClearsLateSpeechEvidence()
        {
            using AudioTrackManager manager =
                CreateManagerWithContinuouslyPlayingStream(out TestPlaybackAudioStream playbackStream);
            using var coordinator = new BargeInCoordinator(
                () => manager,
                () => ResolvedTurnTakingOptions.DefaultHandsFree);
            coordinator.ObserveCharacterSpeech(
                CharacterSpeechStateChanged.StartedSpeaking("test-char-1", "response-1"));
            Assert.IsTrue(coordinator.Commit(BargeInTrigger.ServerVoiceActivity));

            int resetCallCount = 0;
            var disconnectAdapter = new RoomDisconnectRuntimeAdapter(
                () => manager,
                () => throw new InvalidOperationException("disconnect failed"),
                (_, _) => { },
                (_, _, _) => { },
                () =>
                {
                    coordinator.ResetForConnectionBoundary();
                    resetCallCount++;
                    if (resetCallCount == 1)
                    {
                        coordinator.ObserveCharacterSpeech(
                            CharacterSpeechStateChanged.StartedSpeaking(
                                "test-char-1",
                                "late-response"));
                    }
                });

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await disconnectAdapter.DisconnectAsync(CancellationToken.None));
            Assert.AreEqual(2, resetCallCount);
            Assert.IsTrue(playbackStream.IsDisposed);

            TestPlaybackAudioStream reconnectedPlaybackStream = null;
            _audioStreamFactory.StreamFactoryOverride = () =>
            {
                reconnectedPlaybackStream = new TestPlaybackAudioStream(() => { });
                return reconnectedPlaybackStream;
            };
            manager.HandleRemoteAudioTrackSubscribed(
                null,
                "participant-2",
                "test-char-1");
            reconnectedPlaybackStream.RaisePlaybackStarted();

            Assert.IsFalse(
                coordinator.HasActivePlayback,
                "late speech evidence from the failed disconnect must not affect the next connection");
        }

        [Test]
        public void HandleRemoteAudioTrackUnsubscribed_DisposesStream()
        {
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            AudioSource audioSource = CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);
            _agentRegistry.SetParticipantId(characterId, "participant-1");

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);
            manager.HandleRemoteAudioTrackUnsubscribed("participant-1");

            Assert.AreEqual(1, _audioStreamFactory.DisposeCallCount);
            Assert.IsFalse(audioSource.isPlaying);
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_UnknownParticipantWithMultipleCharacters_IsRejectedOnce()
        {
            using AudioTrackManager manager = CreateManager();
            for (int i = 1; i <= 2; i++)
            {
                string characterId = $"test-char-{i}";
                CreateAudioSource(characterId);
                _agentRegistry.RegisterCharacter(new TestCharacterAgent(characterId));
            }

            manager.HandleRemoteAudioTrackSubscribed(null, "unknown-sid-1", "unknown-identity-1");
            manager.HandleRemoteAudioTrackSubscribed(null, "unknown-sid-2", "unknown-identity-2");

            Assert.AreEqual(0, _audioStreamFactory.CreateCallCount);
            Assert.AreEqual(
                1,
                _logger.WarningMessages.FindAll(message =>
                    message.Contains("Rejected ambiguous remote audio route")).Count);
        }

        [Test]
        public void HandleRemoteAudioTrackUnsubscribed_DisposesParticipantRegistrationAfterRegistryBindingClears()
        {
            using AudioTrackManager manager = CreateManager();
            const string characterId = "test-char-1";
            CreateAudioSource(characterId);
            _agentRegistry.RegisterCharacter(new TestCharacterAgent(characterId));

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);
            _agentRegistry.ClearTransportBindings();
            manager.HandleRemoteAudioTrackUnsubscribed("participant-1");

            Assert.AreEqual(1, _audioStreamFactory.DisposeCallCount);
            Assert.IsFalse(manager.TryGetAudioPlayhead(characterId, out _));
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_SkipsDebugLoggingWhenDisabled()
        {
            _logger.DebugEnabled = false;
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.AreEqual(0, _logger.DebugMessages.Count,
                "Debug logs should be skipped when debug level is disabled");
        }

        [Test]
        public void HandleRemoteAudioTrackSubscribed_LogsDebugWhenEnabled()
        {
            _logger.DebugEnabled = true;
            using AudioTrackManager manager = CreateManager();
            string characterId = "test-char-1";
            CreateAudioSource(characterId);
            var agent = new TestCharacterAgent(characterId, "Test");
            _agentRegistry.RegisterCharacter(agent);

            manager.HandleRemoteAudioTrackSubscribed(null, "participant-1", characterId);

            Assert.IsTrue(_logger.DebugMessages.Count > 0,
                "Debug logs should be present when debug level is enabled");
        }

        [Test]
        public void IsPublishing_DefaultsFalse()
        {
            using AudioTrackManager manager = CreateManager();
            Assert.IsFalse(manager.IsPublishing);
        }
    }
}
