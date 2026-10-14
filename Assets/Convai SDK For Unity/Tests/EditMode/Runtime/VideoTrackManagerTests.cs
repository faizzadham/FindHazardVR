using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.Logging;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.EventSystem;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime.Vision.Transport;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class VideoTrackManagerTests
    {
        [SetUp]
        public void SetUp()
        {
            _eventHub = new EventHub(new ImmediateScheduler());
            _localParticipant = new TestLocalParticipant();
            _roomFacade = new TestRoomFacade { LocalParticipant = _localParticipant };
            _logger = new TestLogger();
            _manager = new VideoTrackManager(() => _roomFacade, _eventHub, _logger);
        }

        [TearDown]
        public void TearDown() => _manager?.Dispose();

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestVideoSource : IVideoSource
        {
            private bool _disposed;

            public TestVideoSource(string name = "test-source")
            {
                Name = name;
            }

            public int StartCaptureCount { get; private set; }
            public int StopCaptureCount { get; private set; }
            public int DisposeCount { get; private set; }

            public string Name { get; }
            public bool IsCapturing { get; private set; }
            public int Width => 1280;
            public int Height => 720;

            public void StartCapture()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TestVideoSource));

                StartCaptureCount++;
                IsCapturing = true;
            }

            public void StopCapture()
            {
                StopCaptureCount++;
                IsCapturing = false;
            }

            public void Dispose()
            {
                DisposeCount++;
                _disposed = true;
                IsCapturing = false;
            }
        }

        private sealed class TestTextureVideoSource : ITextureVideoSource
        {
            public bool IsCapturing { get; private set; }
            public string Name { get; } = "texture-source";
            public int Width => SourceTexture != null ? SourceTexture.width : 0;
            public int Height => SourceTexture != null ? SourceTexture.height : 0;
            public Texture SourceTexture { get; private set; }
            public int TargetFrameRate { get; set; } = 30;

            public void SetTexture(Texture texture) => SourceTexture = texture;
            public void StartCapture() => IsCapturing = true;
            public void StopCapture() => IsCapturing = false;
            public void Dispose() => IsCapturing = false;
        }

        private sealed class TestVideoSourceFactory : IVideoSourceFactory
        {
            public TestTextureVideoSource LastCreatedTextureSource { get; private set; }

            public IVideoSource CreateFromRenderTexture(RenderTexture texture, string name = null)
            {
                LastCreatedTextureSource = new TestTextureVideoSource();
                LastCreatedTextureSource.SetTexture(texture);
                return LastCreatedTextureSource;
            }

            public IVideoSource CreateFromCamera(Camera camera, int width, int height, string name = null) => null;
            public IVideoSource CreateFromCanvasCapture(string name = null, int targetFrameRate = 15) => null;
        }

        private sealed class TestLocalVideoTrack : ILocalVideoTrack
        {
            public TestLocalVideoTrack(IVideoSource source, string sid = "video-track-1")
            {
                Source = source;
                Sid = sid;
                Name = source?.Name ?? "video";
            }

            public string Sid { get; }
            public string Name { get; }
            public TrackKind Kind => TrackKind.Video;
            public bool IsMuted { get; private set; }

            public bool IsPublished { get; private set; } = true;
            public IVideoSource Source { get; }

            public event Action<bool> MuteChanged;

            public void SetMuted(bool muted)
            {
                IsMuted = muted;
                MuteChanged?.Invoke(muted);
            }

            public void MarkUnpublished() => IsPublished = false;
        }

        private sealed class TestLocalParticipant : ILocalParticipant
        {
            private readonly List<ILocalTrack> _localTracks = new();

            public int PublishVideoCallCount { get; private set; }
            public int UnpublishCallCount { get; private set; }
            public IVideoSource LastPublishedSource { get; private set; }
            public ILocalTrack LastUnpublishedTrack { get; private set; }
            public bool? WasSourceCapturingWhenUnpublishCalled { get; private set; }
            public bool ReturnNullTrack { get; set; }
            public bool CancelPublish { get; set; }
            public bool CancelUnpublish { get; set; }
            public Exception UnpublishFailure { get; set; }

            public string Sid => "local-participant";
            public string Identity => "local";
            public string Name => "Local";
            public ParticipantMetadata Metadata => default;
            public bool IsAgent => false;
            public IReadOnlyList<ILocalTrack> LocalTracks => _localTracks;

            public event Action<ParticipantMetadata> MetadataUpdated
            {
                add { }
                remove { }
            }

            public event Action<ILocalTrack> TrackPublished;
            public event Action<ILocalTrack> TrackUnpublished;

            public Task<ILocalAudioTrack> PublishAudioTrackAsync(IAudioSource source,
                AudioPublishOptions options = default, CancellationToken ct = default) =>
                Task.FromResult<ILocalAudioTrack>(null);

            public Task<ILocalVideoTrack> PublishVideoTrackAsync(IVideoSource source,
                VideoPublishOptions options = default, CancellationToken ct = default)
            {
                PublishVideoCallCount++;
                LastPublishedSource = source;

                if (CancelPublish)
                    return Task.FromCanceled<ILocalVideoTrack>(new CancellationToken(true));

                if (ReturnNullTrack) return Task.FromResult<ILocalVideoTrack>(null);

                TestLocalVideoTrack track = new(source, $"video-track-{PublishVideoCallCount}");
                _localTracks.Add(track);
                TrackPublished?.Invoke(track);
                return Task.FromResult<ILocalVideoTrack>(track);
            }

            public Task UnpublishTrackAsync(ILocalTrack track, CancellationToken ct = default)
            {
                UnpublishCallCount++;
                LastUnpublishedTrack = track;

                if (UnpublishFailure != null)
                    return Task.FromException(UnpublishFailure);

                if (CancelUnpublish)
                    return Task.FromCanceled(new CancellationToken(true));

                if (track is TestLocalVideoTrack videoTrack)
                {
                    WasSourceCapturingWhenUnpublishCalled = videoTrack.Source?.IsCapturing;
                    videoTrack.MarkUnpublished();
                }

                _localTracks.Remove(track);
                TrackUnpublished?.Invoke(track);
                return Task.CompletedTask;
            }

            public void SetAudioMuted(bool muted) { }

            public void SetVideoMuted(bool muted)
            {
                for (int i = 0; i < _localTracks.Count; i++)
                    if (_localTracks[i] is ILocalVideoTrack videoTrack)
                        videoTrack.SetMuted(muted);
            }
        }

        private sealed class TestRoomFacade : IRoomFacade
        {
            public string Sid => "room";
            public string Name => "Test Room";
            public RoomState State => RoomState.Connected;
            public bool IsConnected => true;
            public ILocalParticipant LocalParticipant { get; set; }
            public IReadOnlyList<IRemoteParticipant> RemoteParticipants => Array.Empty<IRemoteParticipant>();
            public IEnumerable<IParticipant> AllParticipants => Array.Empty<IParticipant>();
            public int RemoteParticipantCount => 0;

            public event Action<IRemoteParticipant> ParticipantJoined
            {
                add { }
                remove { }
            }

            public event Action<IRemoteParticipant> ParticipantLeft
            {
                add { }
                remove { }
            }

            public event Action<IParticipant, ParticipantMetadata> ParticipantMetadataUpdated
            {
                add { }
                remove { }
            }

            public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackSubscribed
            {
                add { }
                remove { }
            }

            public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackUnsubscribed
            {
                add { }
                remove { }
            }

            public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackSubscribed
            {
                add { }
                remove { }
            }

            public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackUnsubscribed
            {
                add { }
                remove { }
            }

            public event Action<TrackSubscriptionEventArgs> TrackSubscribed
            {
                add { }
                remove { }
            }

            public event Action<TrackSubscriptionEventArgs> TrackUnsubscribed
            {
                add { }
                remove { }
            }

            public event Action<RoomState> StateChanged
            {
                add { }
                remove { }
            }

            public event Action Reconnecting
            {
                add { }
                remove { }
            }

            public event Action Reconnected
            {
                add { }
                remove { }
            }

            public event Action<DisconnectReason> Disconnected
            {
                add { }
                remove { }
            }

            public IRemoteParticipant GetParticipantBySid(string sid) => null;
            public IRemoteParticipant GetParticipantByIdentity(string identity) => null;

            public bool TryGetParticipantBySid(string sid, out IRemoteParticipant participant)
            {
                participant = null;
                return false;
            }

            public bool TryGetParticipantByIdentity(string identity, out IRemoteParticipant participant)
            {
                participant = null;
                return false;
            }
        }

        private IEventHub _eventHub;
        private TestLocalParticipant _localParticipant;
        private TestRoomFacade _roomFacade;
        private VideoTrackManager _manager;
        private TestLogger _logger;

        [Test]
        public async Task PublishVideoAsync_WithVideoSource_StartsCaptureAndPublishesTrack()
        {
            TestVideoSource source = new("canvas-source");

            bool success = await _manager.PublishVideoAsync(
                source,
                VideoPublishOptions.Default.WithTrackName("webgl-scene"));

            Assert.IsTrue(success);
            Assert.AreEqual(1, source.StartCaptureCount);
            Assert.AreEqual(1, _localParticipant.PublishVideoCallCount);
            Assert.AreSame(source, _localParticipant.LastPublishedSource);
            Assert.IsTrue(_manager.IsPublishing);
            Assert.AreEqual("webgl-scene", _manager.CurrentTrackName);
            Assert.AreEqual("video-track-1", _manager.CurrentTrackSid);
        }

        [Test]
        public async Task PublishVideoAsync_WhenParticipantReturnsNullTrack_CleansUpSource()
        {
            TestVideoSource source = new("canvas-source");
            _localParticipant.ReturnNullTrack = true;

            bool success = await _manager.PublishVideoAsync(
                source,
                VideoPublishOptions.Default.WithTrackName("webgl-scene"));

            Assert.IsFalse(success);
            Assert.AreEqual(1, source.StartCaptureCount);
            Assert.AreEqual(1, source.StopCaptureCount);
            Assert.AreEqual(1, source.DisposeCount);
            Assert.IsFalse(_manager.IsPublishing);
            LogEntry error = _logger.Entries.Single(entry => entry.Level == LogLevel.Error);
            Assert.That(error.Source, Is.EqualTo(nameof(VideoTrackManager)));
            Assert.That(error.Category, Is.EqualTo(LogCategory.Vision));
            Assert.That(error.Message, Is.EqualTo("Failed to publish video track - PublishVideoTrackAsync returned null"));
        }

        [Test]
        public async Task UnpublishVideoAsync_StopsAndDisposesCurrentSource()
        {
            TestVideoSource source = new("canvas-source");
            await _manager.PublishVideoAsync(source, VideoPublishOptions.Default.WithTrackName("webgl-scene"));

            await _manager.UnpublishVideoAsync();

            Assert.AreEqual(1, _localParticipant.UnpublishCallCount);
            Assert.AreEqual(false, _localParticipant.WasSourceCapturingWhenUnpublishCalled);
            Assert.AreEqual(1, source.StopCaptureCount);
            Assert.AreEqual(1, source.DisposeCount);
            Assert.IsFalse(_manager.IsPublishing);
            Assert.IsNull(_manager.CurrentTrackName);
            Assert.IsNull(_manager.CurrentTrackSid);
        }

        [Test]
        public async Task UnpublishVideoAsync_WhenTransportFails_PropagatesFailureAndDisposesSource()
        {
            TestVideoSource source = new("canvas-source");
            await _manager.PublishVideoAsync(source, VideoPublishOptions.Default);
            var failure = new InvalidOperationException("Synthetic transport failure");
            _localParticipant.UnpublishFailure = failure;
            bool reportedSuccess = false;
            _manager.OnTrackUnpublished += _ => reportedSuccess = true;

            Exception observed = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await _manager.UnpublishVideoAsync());

            Assert.That(observed, Is.SameAs(failure));
            Assert.That(source.StopCaptureCount, Is.EqualTo(1));
            Assert.That(source.DisposeCount, Is.EqualTo(1));
            Assert.That(_manager.IsPublishing, Is.False);
            Assert.That(_manager.CurrentTrackSid, Is.Null);
            Assert.That(reportedSuccess, Is.False);
        }

        [Test]
        public void PublishVideoAsync_WhenPublishIsCanceled_CleansUpNewSource()
        {
            TestVideoSource source = new("canvas-source");
            _localParticipant.CancelPublish = true;

            Assert.CatchAsync<TaskCanceledException>(async () =>
                await _manager.PublishVideoAsync(
                    source,
                    VideoPublishOptions.Default.WithTrackName("webgl-scene")));

            Assert.AreEqual(1, source.StartCaptureCount);
            Assert.AreEqual(1, source.StopCaptureCount);
            Assert.AreEqual(1, source.DisposeCount);
            Assert.IsFalse(_manager.IsPublishing);
        }

        [Test]
        public async Task PublishVideoAsync_WhenPrePublishUnpublishIsCanceled_CleansUpNewSource()
        {
            TestVideoSource currentSource = new("current-source");
            TestVideoSource newSource = new("new-source");

            await _manager.PublishVideoAsync(
                currentSource,
                VideoPublishOptions.Default.WithTrackName("current-track"));

            _localParticipant.CancelUnpublish = true;

            Assert.CatchAsync<TaskCanceledException>(async () =>
                await _manager.PublishVideoAsync(
                    newSource,
                    VideoPublishOptions.Default.WithTrackName("new-track")));

            Assert.AreEqual(0, newSource.StartCaptureCount);
            Assert.AreEqual(1, newSource.StopCaptureCount);
            Assert.AreEqual(1, newSource.DisposeCount);
            Assert.IsFalse(_manager.IsPublishing);
            Assert.AreEqual(1, currentSource.DisposeCount);
        }

        [Test]
        public async Task PublishVideoAsync_WhenSuccessful_DoesNotDisposeOwnedSourceUntilUnpublish()
        {
            TestVideoSource source = new("canvas-source");

            bool success = await _manager.PublishVideoAsync(
                source,
                VideoPublishOptions.Default.WithTrackName("webgl-scene"));

            Assert.IsTrue(success);
            Assert.AreEqual(0, source.StopCaptureCount);
            Assert.AreEqual(0, source.DisposeCount);

            await _manager.UnpublishVideoAsync();

            Assert.AreEqual(1, source.StopCaptureCount);
            Assert.AreEqual(1, source.DisposeCount);
        }

        [Test]
        public async Task PublishVideoAsync_WithRenderTexture_PropagatesConfiguredFrameRateToTextureSource()
        {
            var factory = new TestVideoSourceFactory();
            var renderTexture = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32);
            renderTexture.Create();

            var manager = new VideoTrackManager(() => _roomFacade, _eventHub, _logger, factory);
            try
            {
                bool success = await manager.PublishVideoAsync(
                    renderTexture,
                    VideoPublishOptions.Default.WithTrackName("unity-scene").WithMaxFrameRate(12));

                Assert.IsTrue(success);
                Assert.IsNotNull(factory.LastCreatedTextureSource);
                Assert.AreEqual(12, factory.LastCreatedTextureSource.TargetFrameRate);
                Assert.AreSame(factory.LastCreatedTextureSource, _localParticipant.LastPublishedSource);
            }
            finally
            {
                manager.Dispose();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }
    }
}
