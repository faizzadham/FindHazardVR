using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Modules.Vision;
using Convai.Runtime;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.Core.Modules;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.NarrativeDesign;
using Convai.Runtime.Logging;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Context;
using Convai.Runtime.Vision.Publishing;
using Convai.Runtime.Vision.Sources;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;
using RtviSceneMetadata = Convai.Infrastructure.Protocol.Messages.SceneMetadata;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiVisionPublisherModuleTests
    {
        private TestLogger _logger;
        private TestLogSink _globalSink;

        [SetUp]
        public void SetUp()
        {
            _logger = new TestLogger();
            _globalSink = new TestLogSink();
            ConvaiLogger.RegisterSink(_globalSink);
        }

        [TearDown]
        public void TearDown() => ConvaiLogger.UnregisterSink(_globalSink);

        private class FakeVisionFrameSource : MonoBehaviour, IVisionFrameSource
        {
            public bool AutoReadyOnStart { get; set; } = true;
            public int StopCaptureCount { get; private set; }
            public bool IsCapturing { get; protected set; }
            public bool IsFrameReady { get; set; }
            public long FrameCount => 0;
            public (int Width, int Height) FrameDimensions => (0, 0);
            public float TargetFrameRate => 15f;
            public string SourceId => "fake";
            public RenderTexture CurrentRenderTexture { get; set; }
            public event Action FrameReady;

            public virtual void StartCapture()
            {
                IsCapturing = true;
                if (!AutoReadyOnStart || CurrentRenderTexture == null)
                    return;

                MarkFrameReady();
            }

            public virtual void StopCapture()
            {
                StopCaptureCount++;
                IsCapturing = false;
                IsFrameReady = false;
            }

            public void MarkFrameReady()
            {
                if (CurrentRenderTexture == null)
                    return;

                IsFrameReady = true;
                FrameReady?.Invoke();
            }
        }

        private sealed class DelayedVisionFrameSource : FakeVisionFrameSource
        {
            public bool StartupPending { get; private set; }
            public override void StartCapture() => StartupPending = true;

            public void CompleteStartup()
            {
                if (!StartupPending) return;
                StartupPending = false;
                base.StartCapture();
            }

            public override void StopCapture()
            {
                StartupPending = false;
                base.StopCapture();
            }
        }

        private sealed class FakeStatusVisionFrameSource : FakeVisionFrameSource, IVisionFrameSourceStatusProvider
        {
            public VisionSourceState StartState { get; set; } = VisionSourceState.Starting;
            public VisionSourceState State { get; private set; } = VisionSourceState.Idle;
            public VisionSourceErrorKind ErrorKind { get; private set; } = VisionSourceErrorKind.None;
            public string StatusMessage { get; private set; }
            public bool HasUsableFrame => IsFrameReady && CurrentRenderTexture != null;
            public event Action StatusChanged;

            public override void StartCapture()
            {
                base.StartCapture();
                SetStatus(StartState);
                if (AutoReadyOnStart && HasUsableFrame)
                    SetStatus(VisionSourceState.Ready);
            }

            public override void StopCapture()
            {
                base.StopCapture();
                SetStatus(VisionSourceState.Stopped);
            }

            public void SetFailed(
                VisionSourceErrorKind errorKind = VisionSourceErrorKind.Unknown,
                string message = "Source failed") =>
                SetStatus(VisionSourceState.Failed, errorKind, message);

            public void SetReady()
            {
                MarkFrameReady();
                SetStatus(VisionSourceState.Ready);
            }

            private void SetStatus(
                VisionSourceState state,
                VisionSourceErrorKind errorKind = VisionSourceErrorKind.None,
                string message = null)
            {
                State = state;
                ErrorKind = errorKind;
                StatusMessage = message;
                StatusChanged?.Invoke();
            }
        }

        // Like a webcam: Starting and not capturing until startup completes. Unlike the fakes above,
        // a stopped source never raises FrameReady, because stopping cancels its startup.
        private sealed class SlowStartingVisionFrameSource : FakeVisionFrameSource, IVisionFrameSourceStatusProvider
        {
            private bool _startupPending;
            public VisionSourceState State { get; private set; } = VisionSourceState.Idle;
            public VisionSourceErrorKind ErrorKind => VisionSourceErrorKind.None;
            public string StatusMessage => null;
            public bool HasUsableFrame => IsFrameReady;
            public event Action StatusChanged;

            public override void StartCapture()
            {
                _startupPending = true;
                SetState(VisionSourceState.Starting);
            }

            public override void StopCapture()
            {
                _startupPending = false;
                base.StopCapture();
                SetState(VisionSourceState.Stopped);
            }

            public void DeliverFirstFrame()
            {
                if (!_startupPending) return;
                _startupPending = false;
                IsCapturing = true;
                State = VisionSourceState.Ready;
                MarkFrameReady();
                StatusChanged?.Invoke();
            }

            private void SetState(VisionSourceState state)
            {
                State = state;
                StatusChanged?.Invoke();
            }
        }

        private sealed class FakeRoomConnectionService : IConvaiRoomConnectionService
        {
            private event Action ConnectedHandlers;
            private event Action<SessionStateChanged> SessionStateChangedHandlers;

            public int ConnectedSubscriberCount { get; private set; }

            /// <summary>Returns Video connection type to enable vision publishing in tests.</summary>
            public ConvaiConnectionType ConnectionType { get; set; } = ConvaiConnectionType.Video;

            public SessionState CurrentState { get; private set; } = SessionState.Disconnected;
            public bool IsConnected { get; set; }
            public bool HasRoomDetails => false;
            public bool HasPendingOwnershipReconnect => false;
            public ConversationInputMode ActiveConversationInputMode => ConversationInputMode.HandsFree;
            public IRoomFacade CurrentRoom { get; set; }
            public RTVIHandler RtvHandler => null;
            public MultiCharacterRoomSession CurrentMultiCharacterSession => null;

            public event Action Connected
            {
                add
                {
                    ConnectedSubscriberCount++;
                    ConnectedHandlers += value;
                }
                remove
                {
                    ConnectedSubscriberCount--;
                    ConnectedHandlers -= value;
                }
            }

#pragma warning disable CS0067
            public event Action<SessionError> OnSessionError;
            public event Action<ConversationInputMode> ConversationInputModeChanged;
#pragma warning restore CS0067
            public event Action<SessionStateChanged> OnSessionStateChanged
            {
                add => SessionStateChangedHandlers += value;
                remove => SessionStateChangedHandlers -= value;
            }

            public void RaiseConnected()
            {
                SessionState previousState = CurrentState;
                CurrentState = SessionState.Connected;
                IsConnected = true;
                ConnectedHandlers?.Invoke();
                SessionStateChangedHandlers?.Invoke(
                    SessionStateChanged.Create(previousState, SessionState.Connected, "video-test-session"));
            }

            public void RaiseState(SessionState state)
            {
                SessionState previousState = CurrentState;
                CurrentState = state;
                IsConnected = state == SessionState.Connected;
                SessionStateChangedHandlers?.Invoke(
                    SessionStateChanged.Create(previousState, state, "video-test-session"));
            }

            public IConvaiOperation<RoomSession> ConnectAsync(CancellationToken cancellationToken = default) =>
                ConvaiOperation<RoomSession>.Succeeded(new RoomSession(
                    "video-test-session",
                    "video-test-room",
                    "local-player",
                    DateTime.UtcNow));

            public IConvaiOperation<RoomSession> ConnectAsync(
                RoomSessionConnectOptions options,
                CancellationToken cancellationToken = default) =>
                ConnectAsync(cancellationToken);

            public IConvaiOperation<RoomSession> JoinMultiCharacterRoomAsync(
                MultiCharacterJoinOptions options,
                CancellationToken cancellationToken = default) =>
                ConnectAsync(cancellationToken);

            public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
                IConvaiCharacterAgent character,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<InteractionTargetResult>.Succeeded(default);

            public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
                string membershipId,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<InteractionTargetResult>.Succeeded(default);

            public IConvaiOperation<InteractionTargetResult> ClearInteractionTargetAsync(
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<InteractionTargetResult>.Succeeded(default);

            public IConvaiOperation<CharacterRosterUpdateResult> AddCharacterAsync(
                IConvaiCharacterAgent character,
                string characterSessionId = null,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

            public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
                string membershipId,
                string replacementTargetMembershipId = null,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

            public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
                IConvaiCharacterAgent character,
                string replacementTargetMembershipId = null,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

            public IConvaiOperation<Unit> DisconnectAsync(CancellationToken cancellationToken = default) =>
                ConvaiOperation<Unit>.Succeeded(
                    Unit.Value);

            public IConvaiOperation<Unit> SetConversationInputModeAsync(
                ConversationInputMode mode,
                CancellationToken cancellationToken = default) =>
                ConvaiOperation<Unit>.Succeeded(Unit.Value);

            public bool SendNarrativeTrigger(ConvaiNarrativeTriggerRequest request) => false;
            public bool UpdateSceneMetadata(IReadOnlyList<RtviSceneMetadata> sceneMetadata) => false;
            public bool UpdateTemplateKeys(Dictionary<string, string> templateKeys) => false;
            public bool SetTtsEnabled(bool ttsEnabled) => false;
            public bool SetSttMuted(bool muted) => false;
            public bool InterruptBot() => false;
            public bool KillPipeline() => false;
            public bool ForceUserStoppedSpeaking() => false;
            public bool ResetIdleTimer() => false;
            public bool RequestVisionStatus(string updateId = null) => false;
            public bool TriggerVision(ConvaiVisionTriggerRequest request) => false;
            public bool UpdateRespondMode(ConvaiRespondModeLane lane, ConvaiRespondMode mode, string updateId = null) => false;
        }

        private sealed class FakeVideoSourceFactory : IVideoSourceFactory
        {
            public List<FakeVideoSource> CreatedSources { get; } = new();
            public List<RenderTexture> CreatedTextures { get; } = new();
            public Exception CreationException { get; set; }

            public IVideoSource CreateFromRenderTexture(RenderTexture texture, string name = null)
            {
                if (CreationException != null) throw CreationException;
                CreatedTextures.Add(texture);
                var source = new FakeVideoSource(name ?? "fake-video-source");
                CreatedSources.Add(source);
                return source;
            }

            public IVideoSource CreateFromCamera(Camera camera, int width, int height, string name = null) => null;
            public IVideoSource CreateFromCanvasCapture(string name = null, int targetFrameRate = 15)
            {
                var source = new FakeVideoSource(name ?? "fake-canvas-source");
                CreatedSources.Add(source);
                return source;
            }
        }

        private sealed class FakeVideoSource : IVideoSource
        {
            public FakeVideoSource(string name) => Name = name;

            public int StartCaptureCount { get; private set; }
            public int StopCaptureCount { get; private set; }
            public int DisposeCount { get; private set; }
            public string Name { get; }
            public bool IsCapturing { get; private set; }
            public int Width => 1280;
            public int Height => 720;

            public void StartCapture()
            {
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
                IsCapturing = false;
            }
        }

        private sealed class FakeLocalVideoTrack : ILocalVideoTrack
        {
            public FakeLocalVideoTrack(IVideoSource source, string sid)
            {
                Source = source;
                Sid = sid;
                Name = source?.Name ?? "fake-video";
            }

            public IVideoSource Source { get; }
            public string Sid { get; }
            public string Name { get; }
            public TrackKind Kind => TrackKind.Video;
            public bool IsMuted { get; private set; }
            public bool IsPublished { get; private set; } = true;
            public event Action<bool> MuteChanged;

            public void SetMuted(bool muted)
            {
                IsMuted = muted;
                MuteChanged?.Invoke(muted);
            }

            public void MarkUnpublished() => IsPublished = false;
        }

        private sealed class FakeLocalParticipant : ILocalParticipant
        {
            private readonly List<ILocalTrack> _localTracks = new();

            public int PublishVideoCallCount { get; private set; }
            public int UnpublishCallCount { get; private set; }
            public Exception UnpublishException { get; set; }
            public Task PublishCompletion { get; set; }
            public List<VideoPublishOptions> PublishedOptions { get; } = new();
            public IReadOnlyList<ILocalTrack> LocalTracks => _localTracks;
            public string Sid => "local-participant";
            public string Identity => "local";
            public string Name => "Local";
            public ParticipantMetadata Metadata => default;
            public bool IsAgent => false;
            public event Action<ParticipantMetadata> MetadataUpdated { add { } remove { } }
            public event Action<ILocalTrack> TrackPublished;
            public event Action<ILocalTrack> TrackUnpublished;

            public Task<ILocalAudioTrack> PublishAudioTrackAsync(
                IAudioSource source,
                AudioPublishOptions options = default,
                CancellationToken ct = default) =>
                Task.FromResult<ILocalAudioTrack>(null);

            public async Task<ILocalVideoTrack> PublishVideoTrackAsync(
                IVideoSource source,
                VideoPublishOptions options = default,
                CancellationToken ct = default)
            {
                PublishVideoCallCount++;
                PublishedOptions.Add(options);
                if (PublishCompletion != null) await PublishCompletion;
                var track = new FakeLocalVideoTrack(source, $"video-track-{PublishVideoCallCount}");
                _localTracks.Add(track);
                TrackPublished?.Invoke(track);
                return track;
            }

            public Task UnpublishTrackAsync(ILocalTrack track, CancellationToken ct = default)
            {
                UnpublishCallCount++;
                if (UnpublishException != null)
                    return Task.FromException(UnpublishException);

                if (track is FakeLocalVideoTrack videoTrack)
                    videoTrack.MarkUnpublished();
                _localTracks.Remove(track);
                TrackUnpublished?.Invoke(track);
                return Task.CompletedTask;
            }

            public void SetAudioMuted(bool muted) { }

            public void SetVideoMuted(bool muted) { }
        }

        private sealed class FakeRoomFacade : IRoomFacade
        {
            public string Sid => "room";
            public string Name => "Test Room";
            public RoomState State => RoomState.Connected;
            public bool IsConnected => true;
            public ILocalParticipant LocalParticipant { get; set; }
            public IReadOnlyList<IRemoteParticipant> RemoteParticipants => Array.Empty<IRemoteParticipant>();
            public IEnumerable<IParticipant> AllParticipants => Array.Empty<IParticipant>();
            public int RemoteParticipantCount => 0;
            public event Action<IRemoteParticipant> ParticipantJoined { add { } remove { } }
            public event Action<IRemoteParticipant> ParticipantLeft { add { } remove { } }
            public event Action<IParticipant, ParticipantMetadata> ParticipantMetadataUpdated { add { } remove { } }
            public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackSubscribed { add { } remove { } }
            public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackUnsubscribed { add { } remove { } }
            public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackSubscribed { add { } remove { } }
            public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackUnsubscribed { add { } remove { } }
            public event Action<TrackSubscriptionEventArgs> TrackSubscribed { add { } remove { } }
            public event Action<TrackSubscriptionEventArgs> TrackUnsubscribed { add { } remove { } }
            public event Action<RoomState> StateChanged { add { } remove { } }
            public event Action Reconnecting { add { } remove { } }
            public event Action Reconnected { add { } remove { } }
            public event Action<DisconnectReason> Disconnected { add { } remove { } }

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

        private sealed class FakeTransportProvider : ITransportProvider
        {
            private readonly IVideoSourceFactory _videoSourceFactory = new FakeVideoSourceFactory();

            public TransportCapabilities Capabilities => TransportCapabilities.Native();
            public IRealtimeTransport CreateTransport() => null;
            public IMicrophoneSourceFactory CreateMicrophoneFactory() => null;
            public IAudioStreamFactory CreateAudioStreamFactory() => null;
            public IVideoSourceFactory CreateVideoSourceFactory() => _videoSourceFactory;
            public IConvaiRoomControllerFactory CreateRoomControllerFactory() => null;
        }

        private sealed class FakeModuleContext : IModuleContext
        {
            private readonly Dictionary<Type, object> _services = new();

            public FakeModuleContext(ConvaiRuntime runtime) => Runtime = runtime;

            public ConvaiRuntime Runtime { get; }
            public IEventHub Events => Runtime.Events;
            public IAgentRegistry Agents => Runtime.Agents;
            public ITransportProvider Transport => Runtime.Transport;
            public IRuntimePreferences Preferences => Runtime.RuntimePreferences;
            public ILogger Logger => null;
            public IConvaiRoomAudioService RoomAudio => null;
            public ICredentialProvider Credentials => null;

            public bool TryGetModuleService<TService>(out TService service) where TService : class
            {
                if (_services.TryGetValue(typeof(TService), out object value))
                {
                    service = value as TService;
                    return service != null;
                }

                service = null;
                return false;
            }

            public void ProvideModuleService<TService>(TService instance) where TService : class =>
                _services[typeof(TService)] = instance;
        }

        private static ConvaiRuntime CreateRuntime()
        {
            var registry = new AgentRegistry();
            return new ConvaiRuntimeBuilder()
                .UseEventHub(new EventHub(new ImmediateScheduler(), new TestLogger()))
                .UseAgentRegistry(registry)
                .UseTransport(new FakeTransportProvider())
                .UseRoomRuntime(() => new RoomRuntimeBuilder()
                    .WithConnectionAdapter(new TestRoomConnectionRuntimeAdapter())
                    .WithAudioAdapter(new TestRoomAudioRuntimeAdapter())
                    .WithAgentRegistry(registry)
                    .Build())
                .Build();
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestRoomConnectionRuntimeAdapter : IRoomConnectionRuntimeAdapter
        {
            public event Action<SessionStateChanged> StateChanged;

            public SessionState CurrentState => SessionState.Disconnected;
            public bool IsConnected => false;
            public string CurrentRoomName => string.Empty;
            public string CurrentSessionId => string.Empty;
            public bool CanConnect => true;
            public bool HasStarted => true;

            public Task<bool> WaitForStartCompletionAsync(CancellationToken ct) => Task.FromResult(true);
            public bool PrepareConnection() => true;
            public Task<bool> WaitForConnectionResolutionAsync(CancellationToken ct) => Task.FromResult(true);
            public Task<RoomConnectionAttemptResult> ConnectAsync(CancellationToken ct) =>
                Task.FromResult(RoomConnectionAttemptResult.Success());
            public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

            public void NotifyStateChanged(SessionStateChanged stateChanged) => StateChanged?.Invoke(stateChanged);
        }

        private sealed class TestRoomAudioRuntimeAdapter : IRoomAudioRuntimeAdapter
        {
            public bool IsMicMuted => false;
            public void SetMicMuted(bool muted) { }
            public Task StartListeningAsync(int microphoneIndex, CancellationToken ct) => Task.CompletedTask;
            public Task StopListeningAsync(CancellationToken ct) => Task.CompletedTask;
            public bool SetCharacterMuted(string characterId, bool muted) => true;
            public bool IsCharacterMuted(string characterId) => false;
        }

        private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMs = 1000)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate())
                    return;

                await Task.Delay(10);
            }

            Assert.Fail($"Condition not met within {timeoutMs}ms.");
        }

        private static ConvaiVisionPublisher CreatePublisher(GameObject go)
        {
            var publisher = go.AddComponent<ConvaiVisionPublisher>();
            typeof(ConvaiVisionPublisher)
                .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(publisher, null);
            return publisher;
        }

        [Test]
        public async Task RegisterAsync_ResolvesLocalFrameSource()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var go = new GameObject("publisher");
            var publisher = CreatePublisher(go);

            try
            {
                var frameSource = go.AddComponent<FakeVisionFrameSource>();

                await publisher.RegisterAsync(context);

                Assert.That(publisher.FrameSource, Is.SameAs(frameSource));
            }
            finally
            {
                await publisher.StopAsync();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task StartAsync_UsesModuleContextConnectionService_And_StopAsync_UnregistersCallbacks()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var roomConnection = new FakeRoomConnectionService();
            context.ProvideModuleService<IConvaiRoomConnectionService>(roomConnection);

            var go = new GameObject("publisher");
            var publisher = CreatePublisher(go);

            try
            {
                go.AddComponent<FakeVisionFrameSource>();

                await publisher.RegisterAsync(context);
                await publisher.StartAsync(context);

                Assert.That(roomConnection.ConnectedSubscriberCount, Is.EqualTo(1));

                await publisher.StopAsync();

                Assert.That(roomConnection.ConnectedSubscriberCount, Is.EqualTo(0));
            }
            finally
            {
                await publisher.StopAsync();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task StartAsync_ManualPolicy_DoesNotAutoStartCapture_WhenAlreadyConnected()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var roomConnection = new FakeRoomConnectionService
            {
                IsConnected = true
            };
            context.ProvideModuleService<IConvaiRoomConnectionService>(roomConnection);

            var go = new GameObject("publisher");
            var publisher = CreatePublisher(go);

            try
            {
                var frameSource = go.AddComponent<FakeVisionFrameSource>();

                publisher.SetPublishPolicy(VisionPublishPolicy.Manual);

                await publisher.RegisterAsync(context);
                await publisher.StartAsync(context);
                await Task.Yield();

                Assert.That(frameSource.IsCapturing, Is.False);
            }
            finally
            {
                await publisher.StopAsync();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task EnablePublishing_ManualPolicy_StartsCapture_WhenConnected()
        {
            var runtime = CreateRuntime();
            var context = new FakeModuleContext(runtime);
            var roomConnection = new FakeRoomConnectionService
            {
                IsConnected = true
            };
            context.ProvideModuleService<IConvaiRoomConnectionService>(roomConnection);

            var go = new GameObject("publisher");
            var publisher = CreatePublisher(go);

            try
            {
                var frameSource = go.AddComponent<FakeVisionFrameSource>();

                publisher.SetPublishPolicy(VisionPublishPolicy.Manual);

                await publisher.RegisterAsync(context);
                await publisher.StartAsync(context);

                publisher.EnablePublishing(true);
                await Task.Yield();

                Assert.That(frameSource.IsCapturing, Is.True);
            }
            finally
            {
                await publisher.StopAsync();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task SetPublishingEnabled_ReenabledBeforeConnect_KeepsConnectedSubscriptionAndPublishesLater()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor, logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                coordinator.SetPublishingEnabled(false);
                coordinator.SetPublishingEnabled(true);

                Assert.That(roomConnection.ConnectedSubscriberCount, Is.EqualTo(1));

                roomConnection.RaiseConnected();
                await Task.Yield();
                await Task.Yield();

                Assert.That(frameSource.IsCapturing, Is.True);
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task ApplyConfiguration_PreservesExplicitDisableAcrossRefresh()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant },
                IsConnected = true
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor, logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                await Task.Yield();
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));

                coordinator.SetPublishingEnabled(false);
                await Task.Yield();

                coordinator.ApplyConfiguration(
                    VisionPublishPolicy.AutoCompatible,
                    0,
                    0,
                    "unity-scene");
                roomConnection.RaiseConnected();
                await Task.Yield();

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));
                Assert.That(frameSource.IsCapturing, Is.False);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task SetPublishingEnabled_WhenBackgroundUnpublishFails_ReportsSanitizedFailure()
        {
            const string sensitiveDetail = "SENSITIVE_UNPUBLISH_DETAIL";
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant },
                IsConnected = true
            };
            string capturedError = null;
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                backgroundFailureReporter: message => capturedError = message, logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                await Task.Yield();
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));

                localParticipant.UnpublishException = new InvalidOperationException(sensitiveDetail);

                coordinator.SetPublishingEnabled(false);
                await WaitForConditionAsync(() => capturedError != null);

                Assert.That(localParticipant.UnpublishCallCount, Is.EqualTo(1));
                Assert.That(frameSource.IsCapturing, Is.False);
                Assert.That(
                    capturedError,
                    Is.EqualTo("Vision background publishing stop failed (InvalidOperationException)."));
                Assert.That(capturedError, Does.Not.Contain(sensitiveDetail));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task ApplyConfiguration_ManualPolicyWithoutExplicitEnable_DoesNotAutoPublish()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant },
                IsConnected = true
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor, logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);
                coordinator.ApplyConfiguration(VisionPublishPolicy.Manual, 0, 0, "unity-scene");

                await coordinator.StartAsync();
                await Task.Yield();

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));
                Assert.That(frameSource.IsCapturing, Is.False);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task ApplyConfiguration_WhenPublishingOptionsChange_RepublishesWithNewOptions()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant },
                IsConnected = true
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor, logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                await Task.Yield();
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(1));

                coordinator.ApplyConfiguration(
                    VisionPublishPolicy.HighResponsiveness,
                    12,
                    900_000,
                    "new-track");
                await Task.Yield();
                await Task.Yield();

                Assert.That(localParticipant.UnpublishCallCount, Is.EqualTo(1));
                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(2));
                Assert.That(localParticipant.PublishedOptions[1].TrackName, Is.EqualTo("new-track"));
                Assert.That(localParticipant.PublishedOptions[1].MaxFrameRate, Is.EqualTo(12));
                Assert.That(localParticipant.PublishedOptions[1].MaxBitrate, Is.EqualTo(900_000));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_StatusSourceTimeout_DoesNotHangAndAllowsLaterPublish()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(100), logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await WaitForConditionAsync(() => _logger.Entries.Any(entry => entry.Level == LogLevel.Error));

                LogEntry error = _logger.Entries.Single(entry => entry.Level == LogLevel.Error);
                Assert.That(error.Source, Is.EqualTo(nameof(VisionPublishCoordinator)));
                Assert.That(error.Category, Is.EqualTo(LogCategory.Vision));
                Assert.That(_globalSink.CountByLevel(LogLevel.Error), Is.Zero, "Injected diagnostics must not also reach global sinks.");
                Assert.That(error.Message, Does.StartWith("Frame source did not become ready within "));

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));

                frameSource.SetReady();
                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_TimeoutWhileOwnedSourceStarts_KeepsStartupAndPublishesLaterFrame()
        {
            var go = new GameObject("slow-source");
            var source = go.AddComponent<SlowStartingVisionFrameSource>();
            var texture = new RenderTexture(32, 32, 0);
            var participant = new FakeLocalParticipant();
            using var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(100), logger: _logger);
            try
            {
                source.CurrentRenderTexture = texture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    new FakeRoomConnectionService
                    {
                        IsConnected = true,
                        CurrentRoom = new FakeRoomFacade { LocalParticipant = participant }
                    });
                coordinator.SetFrameSource(source);
                await coordinator.StartAsync();
                await WaitForConditionAsync(() => _logger.Entries.Any(entry =>
                    entry.Message.StartsWith("Frame source did not become ready within ")));

                Assert.That(source.StopCaptureCount, Is.Zero, "the publish wait cancelled a camera that was still starting");

                source.DeliverFirstFrame();
                await WaitForConditionAsync(() => participant.PublishVideoCallCount == 1);
                Assert.That(coordinator.IsPublishing, Is.True);

                await coordinator.StopPublishingAsync();
                Assert.That(source.StopCaptureCount, Is.EqualTo(1), "publishing must still stop the camera it started");
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public async Task PublishWait_StatusSourceDelayedFrame_PublishesBeforeTimeout()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(300), logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(50);
                frameSource.SetReady();

                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_CancellationBeforeTimeout_PropagatesCancellation()
        {
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromSeconds(5), logger: _logger);
            using var cts = new CancellationTokenSource();

            try
            {
                MethodInfo waitForFrameSourceReady = typeof(VisionPublishCoordinator).GetMethod(
                    "WaitForFrameSourceReadyAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(waitForFrameSourceReady, Is.Not.Null);

                var waitTask = (Task)waitForFrameSourceReady.Invoke(
                    coordinator,
                    new object[] { frameSource, cts.Token });
                cts.Cancel();

                OperationCanceledException exception =
                    await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(waitTask,
                        "Cancelling before the timeout must cancel the readiness wait.");
                Assert.That(exception.CancellationToken, Is.EqualTo(cts.Token));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
            }
        }

        [Test]
        public async Task PublishWait_AlreadyCancelledReadySource_PropagatesCancellation()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromSeconds(5), logger: _logger);
            using var cts = new CancellationTokenSource();

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;
                frameSource.StartCapture();
                cts.Cancel();

                MethodInfo waitForFrameSourceReady = typeof(VisionPublishCoordinator).GetMethod(
                    "WaitForFrameSourceReadyAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(waitForFrameSourceReady, Is.Not.Null);

                var waitTask = (Task)waitForFrameSourceReady.Invoke(
                    coordinator,
                    new object[] { frameSource, cts.Token });

                OperationCanceledException exception =
                    await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(waitTask,
                        "An already-cancelled token must cancel the readiness wait.");
                Assert.That(exception.CancellationToken, Is.EqualTo(cts.Token));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_AwaitingPermission_SuspendsTimeoutUntilSourceReady()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(100), logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.StartState = VisionSourceState.AwaitingPermission;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(220);

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));

                frameSource.SetReady();
                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_SourceFailure_AbortsCurrentAttemptAndAllowsRecovery()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeStatusVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(300), logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();

                frameSource.SetFailed(VisionSourceErrorKind.DeviceUnavailable, "camera offline");
                await WaitForConditionAsync(() => _logger.Entries.Any(entry => entry.Level == LogLevel.Error));

                LogEntry error = _logger.Entries.Single(entry => entry.Level == LogLevel.Error);
                Assert.That(error.Source, Is.EqualTo(nameof(VisionPublishCoordinator)));
                Assert.That(error.Category, Is.EqualTo(LogCategory.Vision));
                Assert.That(_globalSink.CountByLevel(LogLevel.Error), Is.Zero, "Injected diagnostics must not also reach global sinks.");
                Assert.That(error.Message, Does.Contain("State=Failed, Error=DeviceUnavailable"));

                Assert.That(localParticipant.PublishVideoCallCount, Is.EqualTo(0));

                frameSource.SetReady();
                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public async Task PublishWait_LegacySource_DelayedFrameStillPublishes()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var localParticipant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = localParticipant }
            };
            var coordinator = new VisionPublishCoordinator(
                () => RuntimePlatform.OSXEditor,
                TimeSpan.FromMilliseconds(300), logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.AutoReadyOnStart = false;
                frameSource.CurrentRenderTexture = renderTexture;
                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Delay(50);
                frameSource.MarkFrameReady();

                await WaitForConditionAsync(() => localParticipant.PublishVideoCallCount == 1);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public void OnValidate_ClampsPublishOverridesToNonNegativeValues()
        {
            var go = new GameObject("publisher");

            try
            {
                var publisher = CreatePublisher(go);
                typeof(ConvaiVisionPublisher).GetField("publishFrameRateOverride")?.SetValue(publisher, -3);
                typeof(ConvaiVisionPublisher).GetField("publishBitrateOverride")?.SetValue(publisher, -99);

                typeof(ConvaiVisionPublisher)
                    .GetMethod("OnValidate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(publisher, null);

                Assert.That(publisher.publishFrameRateOverride, Is.EqualTo(0));
                Assert.That(publisher.publishBitrateOverride, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        ///     A frame source the application opened for itself must survive a session ending.
        /// </summary>
        /// <remarks>
        ///     Applications routinely show the viewer what the assistant can see, which means the
        ///     camera is already running before publishing ever starts and is still wanted after it
        ///     stops. Stopping that borrowed source also interrupts the application's preview.
        /// </remarks>
        [Test]
        public async Task StopPublishing_LeavesAFrameSourceItDidNotStart_Capturing()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var participant = new FakeLocalParticipant();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = participant }
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor, logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;

                // The application opens its own camera first, exactly as a viewfinder would.
                frameSource.StartCapture();

                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Yield();
                await Task.Yield();

                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(1));
                // Disconnected is the enum's default value, so this is the ordinary end of a session.
                roomConnection.RaiseState(SessionState.Disconnected);
                await Task.Yield();
                await Task.Yield();

                await coordinator.StopPublishingAsync();
                Assert.That(participant.UnpublishCallCount, Is.EqualTo(1));
                frameSource.MarkFrameReady();
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(1));
                Assert.That(
                    frameSource.IsCapturing,
                    Is.True,
                    "publishing stopped a camera it borrowed rather than one it opened");
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        /// <summary>
        ///     A frame source publishing started is still publishing's to stop.
        /// </summary>
        [Test]
        public async Task StopPublishing_StopsAFrameSourceItStarted()
        {
            var renderTexture = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
            var frameSourceObject = new GameObject("frame-source");
            var frameSource = frameSourceObject.AddComponent<FakeVisionFrameSource>();
            var roomConnection = new FakeRoomConnectionService
            {
                CurrentRoom = new FakeRoomFacade { LocalParticipant = new FakeLocalParticipant() }
            };
            var coordinator = new VisionPublishCoordinator(() => RuntimePlatform.OSXEditor, logger: _logger);

            try
            {
                renderTexture.Create();
                frameSource.CurrentRenderTexture = renderTexture;

                coordinator.ConfigureDependencies(
                    new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(),
                    roomConnection);
                coordinator.SetFrameSource(frameSource);

                await coordinator.StartAsync();
                roomConnection.RaiseConnected();
                await Task.Yield();
                await Task.Yield();

                Assert.That(frameSource.IsCapturing, Is.True, "publishing should have opened the camera");

                await coordinator.StopPublishingAsync();

                Assert.That(
                    frameSource.IsCapturing,
                    Is.False,
                    "publishing left running a camera that only it was using");
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(frameSourceObject);
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task StopPublishing_CancelsOwnedDelayedStartup(bool completeStartup)
        {
            var go = new GameObject("delayed-source");
            var source = go.AddComponent<DelayedVisionFrameSource>();
            var connection = new FakeRoomConnectionService { IsConnected = true };
            using var coordinator = new VisionPublishCoordinator(logger: _logger);
            try
            {
                coordinator.ConfigureDependencies(null, null, connection);
                coordinator.SetFrameSource(source);
                await coordinator.StartAsync();
                Assert.That(source.StartupPending, Is.True);
                if (completeStartup) source.CompleteStartup();

                await coordinator.StopPublishingAsync();
                source.CompleteStartup(); // A late permission result must not reopen the device.

                Assert.That(source.StopCaptureCount, Is.EqualTo(1));
                Assert.That(source.StartupPending, Is.False);
                Assert.That(source.IsCapturing, Is.False);
                Assert.That(coordinator.IsPublishing, Is.False);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task SetFrameSource_StopsPreviousOwnedStartup_LeavesBorrowedReplacementRunning()
        {
            var oldObject = new GameObject("old-source");
            var newObject = new GameObject("borrowed-source");
            var oldSource = oldObject.AddComponent<DelayedVisionFrameSource>();
            var newSource = newObject.AddComponent<FakeVisionFrameSource>();
            using var coordinator = new VisionPublishCoordinator(logger: _logger);
            try
            {
                coordinator.ConfigureDependencies(null, null, new FakeRoomConnectionService { IsConnected = true });
                coordinator.SetFrameSource(oldSource);
                await coordinator.StartAsync();
                newSource.StartCapture();
                coordinator.SetFrameSource(newSource);
                await coordinator.StopPublishingAsync();
                oldSource.CompleteStartup();
                Assert.That(oldSource.StopCaptureCount, Is.EqualTo(1));
                Assert.That(oldSource.IsCapturing, Is.False);
                Assert.That(newSource.IsCapturing, Is.True);
                Assert.That(newSource.StopCaptureCount, Is.Zero);
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(oldObject);
                UnityEngine.Object.DestroyImmediate(newObject);
            }
        }

        [Test]
        public async Task SetFrameSource_WhilePublishing_RepublishesFromReplacement()
        {
            var oldObject = new GameObject("old-source");
            var newObject = new GameObject("new-source");
            var oldSource = oldObject.AddComponent<FakeVisionFrameSource>();
            var newSource = newObject.AddComponent<FakeVisionFrameSource>();
            var oldTexture = new RenderTexture(32, 32, 0);
            var newTexture = new RenderTexture(64, 32, 0);
            var participant = new FakeLocalParticipant();
            var factory = new FakeVideoSourceFactory();
            using var coordinator = new VisionPublishCoordinator(logger: _logger);
            try
            {
                oldSource.CurrentRenderTexture = oldTexture;
                newSource.CurrentRenderTexture = newTexture;
                coordinator.ConfigureDependencies(new EventHub(new ImmediateScheduler(), new TestLogger()), factory,
                    new FakeRoomConnectionService { IsConnected = true,
                        CurrentRoom = new FakeRoomFacade { LocalParticipant = participant } });
                coordinator.SetFrameSource(oldSource);
                await coordinator.StartAsync();
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(1));

                var republished = new TaskCompletionSource<bool>();
                participant.TrackPublished += _ => republished.TrySetResult(true);
                coordinator.SetFrameSource(newSource);
                Assert.That(await Task.WhenAny(republished.Task, Task.Delay(2000)), Is.SameAs(republished.Task));

                Assert.That(participant.UnpublishCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedTextures.Last(), Is.SameAs(newTexture));
                Assert.That(oldSource.IsCapturing, Is.False);
                Assert.That(newSource.IsCapturing, Is.True);
                Assert.That(coordinator.IsPublishing, Is.True);
                await coordinator.StopPublishingAsync();
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(oldObject);
                UnityEngine.Object.DestroyImmediate(newObject);
                UnityEngine.Object.DestroyImmediate(oldTexture);
                UnityEngine.Object.DestroyImmediate(newTexture);
            }
        }

        [Test]
        public async Task FrameReady_TextureReplacement_RepublishesWithoutRestartingCapture()
        {
            var go = new GameObject("frame-source");
            var source = go.AddComponent<FakeVisionFrameSource>();
            var first = new RenderTexture(32, 32, 0);
            var second = new RenderTexture(64, 32, 0);
            var participant = new FakeLocalParticipant();
            var factory = new FakeVideoSourceFactory();
            using var coordinator = new VisionPublishCoordinator(logger: _logger);
            try
            {
                source.CurrentRenderTexture = first;
                coordinator.ConfigureDependencies(new EventHub(new ImmediateScheduler(), new TestLogger()), factory,
                    new FakeRoomConnectionService { IsConnected = true,
                        CurrentRoom = new FakeRoomFacade { LocalParticipant = participant } });
                coordinator.SetFrameSource(source);
                await coordinator.StartAsync();
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(1));

                var republished = new TaskCompletionSource<bool>();
                participant.TrackPublished += _ => republished.TrySetResult(true);
                source.CurrentRenderTexture = second;
                UnityEngine.Object.DestroyImmediate(first);
                source.MarkFrameReady();
                Assert.That(await Task.WhenAny(republished.Task, Task.Delay(2000)), Is.SameAs(republished.Task));
                Assert.That(participant.UnpublishCallCount, Is.EqualTo(1));
                Assert.That(factory.CreatedTextures.Last(), Is.SameAs(second));
                Assert.That(source.StopCaptureCount, Is.Zero);
                Assert.That(coordinator.IsPublishing, Is.True);
                await coordinator.StopPublishingAsync();
                source.MarkFrameReady();
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(2));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(go);
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public async Task StopPublishing_LatePublishSuccess_UnpublishesThroughOriginalRoom()
        {
            var go = new GameObject("frame-source");
            var source = go.AddComponent<FakeVisionFrameSource>();
            var texture = new RenderTexture(32, 32, 0);
            var completion = new TaskCompletionSource<bool>();
            var participant = new FakeLocalParticipant { PublishCompletion = completion.Task };
            var replacement = new FakeLocalParticipant();
            var factory = new FakeVideoSourceFactory();
            var connection = new FakeRoomConnectionService { IsConnected = true,
                CurrentRoom = new FakeRoomFacade { LocalParticipant = participant } };
            using var coordinator = new VisionPublishCoordinator(logger: _logger);
            try
            {
                source.CurrentRenderTexture = texture;
                coordinator.ConfigureDependencies(new EventHub(new ImmediateScheduler(), new TestLogger()), factory, connection);
                coordinator.SetFrameSource(source);
                await coordinator.StartAsync();
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(1));
                Task stopped = coordinator.StopPublishingAsync().AsTask();
                Assert.That(stopped.IsCompleted, Is.False, "stop must retire a late successful publication");
                connection.CurrentRoom = new FakeRoomFacade { LocalParticipant = replacement };
                completion.SetResult(true); // Transport deliberately ignores cancellation.
                Assert.That(await Task.WhenAny(stopped, Task.Delay(2000)), Is.SameAs(stopped));
                await stopped;
                Assert.That(participant.UnpublishCallCount, Is.EqualTo(1));
                Assert.That(replacement.UnpublishCallCount, Is.Zero);
                Assert.That(factory.CreatedSources.Single().DisposeCount, Is.EqualTo(1));
                Assert.That(coordinator.IsPublishing, Is.False);
            }
            finally
            {
                completion.TrySetResult(true);
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public async Task StartAsync_AfterUnpublishFailure_CanPublishAgain()
        {
            var go = new GameObject("frame-source");
            var source = go.AddComponent<FakeVisionFrameSource>();
            var texture = new RenderTexture(32, 32, 0);
            var participant = new FakeLocalParticipant();
            using var coordinator = new VisionPublishCoordinator(logger: _logger);
            try
            {
                source.CurrentRenderTexture = texture;
                coordinator.ConfigureDependencies(new EventHub(new ImmediateScheduler(), new TestLogger()),
                    new FakeVideoSourceFactory(), new FakeRoomConnectionService { IsConnected = true,
                        CurrentRoom = new FakeRoomFacade { LocalParticipant = participant } });
                coordinator.SetFrameSource(source);
                await coordinator.StartAsync();
                participant.UnpublishException = new InvalidOperationException("transport unavailable");
                Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.StopPublishingAsync());
                participant.UnpublishException = null;
                await coordinator.StartAsync();
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(2));
                Assert.That(coordinator.IsPublishing, Is.True);
                await coordinator.StopPublishingAsync();
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public async Task Publish_SynchronousFactoryFailure_StopsOnceAndAllowsExplicitRetry()
        {
            var go = new GameObject("status-source");
            var source = go.AddComponent<FakeStatusVisionFrameSource>();
            var texture = new RenderTexture(32, 32, 0);
            var participant = new FakeLocalParticipant();
            var factory = new FakeVideoSourceFactory { CreationException = new InvalidOperationException("factory failed") };
            using var coordinator = new VisionPublishCoordinator(logger: _logger);
            try
            {
                source.CurrentRenderTexture = texture;
                coordinator.SetFrameSource(source);
                coordinator.ConfigureDependencies(new EventHub(new ImmediateScheduler(), new TestLogger()), factory,
                    new FakeRoomConnectionService { IsConnected = true,
                        CurrentRoom = new FakeRoomFacade { LocalParticipant = participant } });
                await coordinator.StartAsync();
                Assert.That(source.StopCaptureCount, Is.EqualTo(1));
                Assert.That(source.IsCapturing, Is.False);
                Assert.That(participant.PublishVideoCallCount, Is.Zero);
                await coordinator.StopPublishingAsync();

                factory.CreationException = null;
                await coordinator.StartAsync();
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(1));
                Assert.That(coordinator.IsPublishing, Is.True);
                source.StopCapture();
                await coordinator.StopPublishingAsync();
                Assert.That(coordinator.IsPublishing, Is.False);
                Assert.That(participant.PublishVideoCallCount, Is.EqualTo(1));
                Assert.That(participant.UnpublishCallCount, Is.EqualTo(1));
            }
            finally
            {
                coordinator.Dispose();
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

    }
}
