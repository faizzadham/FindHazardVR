using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Logging;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Sources;
using Convai.Runtime.Vision.Transport;
using Convai.Shared.Types;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Runtime.Vision.Publishing
{
    internal sealed class VisionPublishCoordinator : IDisposable
    {
        private enum SourceReadinessResult
        {
            Ready,
            TimedOut,
            Failed
        }

        private static readonly TimeSpan DefaultFrameReadyTimeout = TimeSpan.FromSeconds(2);
        private readonly Action<string> _backgroundFailureReporter;
        private readonly ILogger _logger;
        private readonly Func<RuntimePlatform> _platformProvider;
        private readonly TimeSpan _frameReadyTimeout;
        private readonly object _syncRoot = new();

        private IConvaiRoomConnectionService _connectionService;
        private bool _disposed;
        private IEventHub _eventHub;
        private IVisionFrameSource _frameSource;

        // Own the start request, including permission/initialization before IsCapturing becomes true.
        private IVisionFrameSource _startedFrameSource;
        private RenderTexture _publishedTexture;
        private Task _publishTask;
        private Task _publicationCleanup = Task.CompletedTask;
        private bool _publishingStopped;
        private bool _stoppingFrameSource;
        private CancellationTokenSource _publishCts;
        private bool? _activeUsesWebGLCanvasPublishPath;
        private bool _hasExplicitPublishingEnabled;
        private bool _publishingEnabled = true;
        private int _publishVersion;
        private bool _subscribedToFrameSource;
        private bool _subscribedToRoomEvents;
        private IVideoSourceFactory _videoSourceFactory;
        private IVideoTrackManager _videoTrackManager;

        public VisionPublishCoordinator(
            Func<RuntimePlatform> platformProvider = null,
            TimeSpan? frameReadyTimeout = null,
            Action<string> backgroundFailureReporter = null,
            ILogger logger = null)
        {
            _logger = (logger ?? new ConvaiLogger()).WithTag(nameof(VisionPublishCoordinator), LogCategory.Vision);
            _platformProvider = platformProvider != null ? platformProvider : () => UnityEngine.Application.platform;
            _frameReadyTimeout = frameReadyTimeout ?? DefaultFrameReadyTimeout;
            _backgroundFailureReporter = backgroundFailureReporter ?? ReportBackgroundFailure;
        }

        public bool IsPublishing => _videoTrackManager?.IsPublishing ?? false;

        public VisionPublishPolicy PublishPolicy { get; private set; } = VisionPublishPolicy.AutoCompatible;

        public int FrameRateOverride { get; private set; }

        public int MaxBitrateOverride { get; private set; }

        public string VideoTrackName { get; private set; } = VideoPublishOptions.Default.TrackName;

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            CancelPendingPublish();
            UnsubscribeFromConnectionEvents();
            UnsubscribeFromFrameSource();

            try
            {
                StopFrameSourceIfStartedHere();
            }
            catch
            {
                // Best-effort shutdown.
            }

            _ = ObserveBackgroundOperationAsync(UnpublishCurrentAsync(), "dispose");
        }

        public void ConfigureDependencies(
            IEventHub eventHub,
            IVideoSourceFactory videoSourceFactory,
            IConvaiRoomConnectionService connectionService)
        {
            ThrowIfDisposed();

            _eventHub = eventHub;
            _videoSourceFactory = videoSourceFactory;

            if (ReferenceEquals(_connectionService, connectionService))
                return;

            UnsubscribeFromConnectionEvents();
            _connectionService = connectionService;
            SubscribeToConnectionEvents();
        }

        public void SetFrameSource(IVisionFrameSource frameSource)
        {
            ThrowIfDisposed();

            if (ReferenceEquals(_frameSource, frameSource))
                return;

            // Replacement only carries over a publication that already existed. It must not start one:
            // callers may set the source before ApplyConfiguration has selected a Manual policy.
            bool hadActiveOrPendingPublish = _videoTrackManager?.IsPublishing == true || _publishCts != null;
            CancelPendingPublish();
            UnsubscribeFromFrameSource();
            try
            {
                StopFrameSourceIfStartedHere();
            }
            finally
            {
                _ = ObserveBackgroundOperationAsync(UnpublishCurrentAsync(), "source replacement");
            }
            _frameSource = frameSource;
            SubscribeToFrameSource();
            if (hadActiveOrPendingPublish)
                SchedulePublish();
        }

        public void ApplyConfiguration(
            VisionPublishPolicy publishPolicy,
            int frameRateOverride,
            int maxBitrateOverride,
            string videoTrackName)
        {
            ThrowIfDisposed();

            string resolvedTrackName = string.IsNullOrWhiteSpace(videoTrackName)
                ? VideoPublishOptions.Default.TrackName
                : videoTrackName.Trim();
            bool publishSettingsChanged =
                PublishPolicy != publishPolicy ||
                FrameRateOverride != frameRateOverride ||
                MaxBitrateOverride != maxBitrateOverride ||
                VideoTrackName != resolvedTrackName ||
                (_activeUsesWebGLCanvasPublishPath.HasValue &&
                 _activeUsesWebGLCanvasPublishPath.Value != UsesWebGLCanvasPublishPath());
            bool hasActiveOrPendingPublish = _videoTrackManager?.IsPublishing == true || _publishCts != null;

            PublishPolicy = publishPolicy;
            FrameRateOverride = frameRateOverride;
            MaxBitrateOverride = maxBitrateOverride;
            VideoTrackName = resolvedTrackName;

            if (!_hasExplicitPublishingEnabled)
                _publishingEnabled = publishPolicy != VisionPublishPolicy.Manual;

            if (!_publishingEnabled)
            {
                if (hasActiveOrPendingPublish)
                    _ = ObserveBackgroundOperationAsync(
                        StopPublishingAsync().AsTask(),
                        "configuration stop");
                return;
            }

            if (hasActiveOrPendingPublish && publishSettingsChanged)
            {
                _ = ObserveBackgroundOperationAsync(
                    RestartPublishingAsync(),
                    "configuration restart");
                return;
            }

            SchedulePublish();
        }

        public void SetPublishPolicy(VisionPublishPolicy publishPolicy)
        {
            ThrowIfDisposed();

            ApplyConfiguration(publishPolicy, FrameRateOverride, MaxBitrateOverride, VideoTrackName);
        }

        public void SetPublishingEnabled(bool enabled)
        {
            ThrowIfDisposed();

            _hasExplicitPublishingEnabled = true;
            _publishingEnabled = enabled;
            if (enabled)
            {
                _publishingStopped = false;
                SchedulePublish();
            }
            else
                _ = ObserveBackgroundOperationAsync(
                    StopPublishingAsync().AsTask(),
                    "publishing stop");
        }

        public ValueTask StartAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            _publishingStopped = false;
            SubscribeToConnectionEvents();
            SubscribeToFrameSource();

            if (_publishingEnabled && _connectionService?.IsConnected == true)
                SchedulePublish();

            return default;
        }

        public async ValueTask StopAsync(CancellationToken ct = default)
        {
            if (_disposed)
                return;

            UnsubscribeFromConnectionEvents();
            UnsubscribeFromFrameSource();
            await StopPublishingAsync(ct);
        }

        public async ValueTask StopPublishingAsync(CancellationToken ct = default)
        {
            if (_disposed)
                return;

            _publishingStopped = true;
            CancelPendingPublish();
            try
            {
                StopFrameSourceIfStartedHere();
            }
            finally
            {
                // Cleanup is not caller-cancellable: the old publication must be retired.
                await UnpublishCurrentAsync();
            }
        }

        private Task UnpublishCurrentAsync()
        {
            IVideoTrackManager manager = _videoTrackManager;
            Task attempt = _publishTask;
            _videoTrackManager = null;
            _publishTask = null;
            _publishedTexture = null;
            _activeUsesWebGLCanvasPublishPath = null;
            Task cleanup = UnpublishAfterAttemptAsync(_publicationCleanup, attempt, manager);
            _publicationCleanup = WaitForCleanupCompletionAsync(cleanup);
            return cleanup;
        }

        private static async Task WaitForCleanupCompletionAsync(Task cleanup)
        {
            try { await cleanup; }
            catch
            {
                // The operation's caller observes the error. A completed cleanup must not poison reconnect.
            }
        }

        private static async Task UnpublishAfterAttemptAsync(
            Task previousCleanup, Task attempt, IVideoTrackManager manager)
        {
            try
            {
                await previousCleanup;
                if (attempt != null) await attempt;
            }
            finally
            {
                if (manager != null)
                {
                    try { await manager.UnpublishVideoAsync(); }
                    finally { manager.Dispose(); }
                }
            }
        }

        private void SubscribeToConnectionEvents()
        {
            if (_subscribedToRoomEvents || _connectionService == null)
                return;

            _connectionService.Connected += OnConnected;
            _connectionService.OnSessionStateChanged += OnSessionStateChanged;
            _subscribedToRoomEvents = true;
        }

        private void UnsubscribeFromConnectionEvents()
        {
            if (!_subscribedToRoomEvents || _connectionService == null)
                return;

            _connectionService.Connected -= OnConnected;
            _connectionService.OnSessionStateChanged -= OnSessionStateChanged;
            _subscribedToRoomEvents = false;
        }

        private void StopFrameSourceIfStartedHere()
        {
            IVisionFrameSource source = _startedFrameSource;
            _startedFrameSource = null;
            // StopCapture also cancels a pending permission/startup request.
            _stoppingFrameSource = true;
            try { source?.StopCapture(); }
            finally { _stoppingFrameSource = false; }
        }

        private void SubscribeToFrameSource()
        {
            if (_subscribedToFrameSource || _frameSource == null)
                return;

            _frameSource.FrameReady += OnFrameReady;
            if (_frameSource is IVisionFrameSourceStatusProvider statusProvider)
                statusProvider.StatusChanged += OnFrameSourceStatusChanged;
            _subscribedToFrameSource = true;
        }

        private void UnsubscribeFromFrameSource()
        {
            if (!_subscribedToFrameSource || _frameSource == null)
                return;

            _frameSource.FrameReady -= OnFrameReady;
            if (_frameSource is IVisionFrameSourceStatusProvider statusProvider)
                statusProvider.StatusChanged -= OnFrameSourceStatusChanged;
            _subscribedToFrameSource = false;
        }

        private void OnConnected()
        {
            _publishingStopped = false;
            SchedulePublish();
        }

        private void OnSessionStateChanged(SessionStateChanged stateChanged)
        {
            switch (stateChanged.NewState)
            {
                case SessionState.Connected:
                    OnConnected();
                    return;
                case SessionState.Connecting:
                case SessionState.Reconnecting:
                    // Keep coordinator active while transport/session is progressing.
                    // Stopping here would unsubscribe before Connected is emitted.
                    return;
                default:
                    _ = ObserveBackgroundOperationAsync(
                        StopPublishingAsync().AsTask(),
                        "session stop");
                    return;
            }
        }

        private void OnFrameReady() => OnFrameSourceStatusChanged();

        private void OnFrameSourceStatusChanged()
        {
            if (_disposed || _publishingStopped || _stoppingFrameSource) return;
            if (!ReferenceEquals(_publishedTexture, null) &&
                (!ReferenceEquals(_publishedTexture, _frameSource?.CurrentRenderTexture) ||
                 !IsSourceReady(_frameSource, _frameSource as IVisionFrameSourceStatusProvider)))
            {
                var status = _frameSource as IVisionFrameSourceStatusProvider;
                if (_frameSource?.IsCapturing != true || status?.State == VisionSourceState.Failed ||
                    status?.State == VisionSourceState.Stopped)
                {
                    _ = ObserveBackgroundOperationAsync(StopPublishingAsync().AsTask(), "source stopped");
                    return;
                }
                _ = ObserveBackgroundOperationAsync(RestartPublishingAsync(), "texture replacement");
                return;
            }
            SchedulePublish();
        }

        private void SchedulePublish()
        {
            // Publishing is only allowed once Dynamic Vision Context resolves this room to video-capable.
            // This keeps vision components inert in audio-only rooms, tracking ConvaiRoomManager's resolved
            // vision capability (surfaced here as the connection service's ConnectionType).
            if (_disposed || _publishingStopped || !_publishingEnabled || _connectionService?.IsConnected != true ||
                _videoTrackManager?.IsPublishing == true)
                return;

            // Guard: only publish once the room resolves to video-capable (Dynamic Vision Context enabled).
            if (_connectionService?.ConnectionType != ConvaiConnectionType.Video)
            {
                _logger.Debug(
                    "Vision publishing blocked: Dynamic Vision Context is disabled for this room. " +
                    "Set Dynamic Vision Context mode to Enabled (or Auto with Connection Type set to Video) on the ConvaiRoomManager.",
                    LogCategory.Vision);
                return;
            }

            lock (_syncRoot)
            {
                if (_publishCts != null && !_publishCts.IsCancellationRequested)
                    return;

                _publishCts = new CancellationTokenSource();
                _publishVersion++;
                // Register ownership before StartCapture/transport can synchronously raise callbacks.
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _publishTask = completion.Task;
                _ = CompletePublishAttemptAsync(_publishVersion, _publishCts, completion);
            }
        }

        private async Task CompletePublishAttemptAsync(
            int publishVersion, CancellationTokenSource publishCts, TaskCompletionSource<bool> completion)
        {
            try
            {
                await ObserveBackgroundOperationAsync(
                    PublishWhenReadyAsync(publishVersion, publishCts), "publish cleanup");
            }
            finally
            {
                completion.TrySetResult(true);
            }
        }

        private async Task PublishWhenReadyAsync(int publishVersion, CancellationTokenSource publishCts)
        {
            CancellationToken ct = publishCts.Token;
            IVisionFrameSource frameSource = _frameSource;
            bool published = false;
            bool awaitingFirstFrame = false;
            try
            {
                await _publicationCleanup;
                ct.ThrowIfCancellationRequested();
                VisionPublishProfile profile = VisionPublishProfileResolver.Resolve(
                    PublishPolicy,
                    FrameRateOverride,
                    MaxBitrateOverride,
                    _platformProvider());

                bool usesWebGLCanvasPublishPath = UsesWebGLCanvasPublishPath();
                if (usesWebGLCanvasPublishPath)
                {
                    bool webGLPublished = await PublishWebGLAsync(profile, ct);
                    if (webGLPublished && !ct.IsCancellationRequested && publishVersion == _publishVersion)
                        _activeUsesWebGLCanvasPublishPath = usesWebGLCanvasPublishPath;
                    return;
                }

                if (frameSource == null)
                {
                    _logger.Error("Frame source is null; cannot publish video.",
                        LogCategory.Vision);
                    return;
                }

                var status = frameSource as IVisionFrameSourceStatusProvider;
                if (status?.State == VisionSourceState.Failed)
                    return;

                if (!frameSource.IsCapturing && !ReferenceEquals(_startedFrameSource, frameSource))
                {
                    // AwaitingPermission/Starting may belong to an application-owned preview.
                    if (status?.State != VisionSourceState.AwaitingPermission && status?.State != VisionSourceState.Starting)
                    {
                        _startedFrameSource = frameSource;
                        frameSource.StartCapture();
                    }
                }

                SourceReadinessResult readinessResult = await WaitForFrameSourceReadyAsync(frameSource, ct);
                if (readinessResult != SourceReadinessResult.Ready)
                {
                    LogSourceReadinessFailure(readinessResult, frameSource, _frameReadyTimeout);
                    // A device may still deliver its first frame after this wait (webcam startup allows
                    // 5 s), and that FrameReady schedules the next attempt. Keep an owned startup running.
                    awaitingFirstFrame = readinessResult == SourceReadinessResult.TimedOut;
                    return;
                }

                if (ct.IsCancellationRequested || publishVersion != _publishVersion)
                    return;

                RenderTexture renderTexture = frameSource.CurrentRenderTexture;
                if (renderTexture == null)
                {
                    _logger.Error("Frame source never produced a RenderTexture.",
                        LogCategory.Vision);
                    return;
                }

                EnsureVideoTrackManager();

                VideoPublishOptions options = profile.ApplyTo(VideoPublishOptions.Default
                    .WithTrackName(VideoTrackName)
                    .WithSource(VideoTrackSource.Camera));

                _publishedTexture = renderTexture;
                bool nativePublished = await _videoTrackManager.PublishVideoAsync(renderTexture, options, ct);

                if (nativePublished && !ct.IsCancellationRequested && publishVersion == _publishVersion)
                {
                    published = true;
                    _activeUsesWebGLCanvasPublishPath = usesWebGLCanvasPublishPath;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during teardown or reconfiguration.
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to publish video: {ex.Message}",
                    LogCategory.Vision);
            }
            finally
            {
                try
                {
                    if (!published && publishVersion == _publishVersion)
                    {
                        _publishedTexture = null;
                        if (!awaitingFirstFrame && ReferenceEquals(_startedFrameSource, frameSource))
                            StopFrameSourceIfStartedHere();
                    }
                }
                finally
                {
                    lock (_syncRoot)
                    {
                        if (ReferenceEquals(_publishCts, publishCts)) _publishCts = null;
                        publishCts.Dispose();
                    }
                }
            }
        }

        private async Task<bool> PublishWebGLAsync(VisionPublishProfile profile, CancellationToken ct)
        {
            if (_videoSourceFactory == null)
                throw new InvalidOperationException("VideoSourceFactory not available.");

            EnsureVideoTrackManager();

            IVideoSource canvasSource = _videoSourceFactory.CreateFromCanvasCapture(VideoTrackName, profile.FrameRate);
            VideoPublishOptions options = profile.ApplyTo(VideoPublishOptions.Default
                .WithTrackName(VideoTrackName)
                .WithSource(VideoTrackSource.ScreenShare));

            return await _videoTrackManager.PublishVideoAsync(canvasSource, options, ct);
        }

        private async Task<SourceReadinessResult> WaitForFrameSourceReadyAsync(
            IVisionFrameSource frameSource,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (frameSource == null)
                return SourceReadinessResult.Failed;

            IVisionFrameSourceStatusProvider statusProvider = frameSource as IVisionFrameSourceStatusProvider;
            if (IsSourceReady(frameSource, statusProvider))
                return SourceReadinessResult.Ready;
            if (statusProvider?.State == VisionSourceState.Failed)
                return SourceReadinessResult.Failed;

            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler() => signal.TrySetResult(true);

            frameSource.FrameReady += Handler;
            if (statusProvider != null)
                statusProvider.StatusChanged += Handler;

            TimeSpan timeoutElapsed = TimeSpan.Zero;
            DateTimeOffset? timeoutWindowStart = null;
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    if (IsSourceReady(frameSource, statusProvider))
                        return SourceReadinessResult.Ready;

                    if (statusProvider?.State == VisionSourceState.Failed)
                        return SourceReadinessResult.Failed;

                    bool suspendTimeout = statusProvider?.State == VisionSourceState.AwaitingPermission;
                    if (suspendTimeout)
                    {
                        if (timeoutWindowStart.HasValue)
                        {
                            timeoutElapsed += DateTimeOffset.UtcNow - timeoutWindowStart.Value;
                            timeoutWindowStart = null;
                        }

                        using (ct.Register(() => signal.TrySetCanceled(ct)))
                            await signal.Task;
                    }
                    else
                    {
                        timeoutWindowStart ??= DateTimeOffset.UtcNow;
                        TimeSpan activeElapsed = timeoutElapsed + (DateTimeOffset.UtcNow - timeoutWindowStart.Value);
                        TimeSpan remaining = _frameReadyTimeout - activeElapsed;
                        if (remaining <= TimeSpan.Zero)
                            return SourceReadinessResult.TimedOut;

                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Task completedTask = await ConvaiTimers.WhenAny(signal.Task, ConvaiTimers.Delay(remaining, timeoutCts.Token));
                        if (completedTask != signal.Task)
                        {
                            ct.ThrowIfCancellationRequested();
                            return SourceReadinessResult.TimedOut;
                        }

                        timeoutCts.Cancel();
                        await signal.Task;
                    }

                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            finally
            {
                frameSource.FrameReady -= Handler;
                if (statusProvider != null)
                    statusProvider.StatusChanged -= Handler;
            }
        }

        private static bool IsSourceReady(
            IVisionFrameSource frameSource,
            IVisionFrameSourceStatusProvider statusProvider)
        {
            if (frameSource?.CurrentRenderTexture == null || !frameSource.IsCapturing ||
                statusProvider?.State == VisionSourceState.Failed || statusProvider?.State == VisionSourceState.Stopped)
                return false;

            return frameSource.IsFrameReady || statusProvider?.HasUsableFrame == true;
        }

        private void LogSourceReadinessFailure(
            SourceReadinessResult readinessResult,
            IVisionFrameSource frameSource,
            TimeSpan timeout)
        {
            IVisionFrameSourceStatusProvider statusProvider = frameSource as IVisionFrameSourceStatusProvider;
            string statusDetail = string.IsNullOrWhiteSpace(statusProvider?.StatusMessage)
                ? string.Empty
                : $" {statusProvider.StatusMessage}";

            switch (readinessResult)
            {
                case SourceReadinessResult.TimedOut:
                    _logger.Error(
                        $"Frame source did not become ready within {timeout.TotalSeconds:0.#}s.{statusDetail}",
                        LogCategory.Vision);
                    return;
                case SourceReadinessResult.Failed:
                    _logger.Error(
                        $"Frame source failed before publish. State={statusProvider?.State}, Error={statusProvider?.ErrorKind}.{statusDetail}",
                        LogCategory.Vision);
                    return;
            }
        }

        private void EnsureVideoTrackManager()
        {
            if (_videoTrackManager != null)
                return;

            if (_connectionService == null)
                throw new InvalidOperationException("Room connection service not available.");

            if (_eventHub == null)
                throw new InvalidOperationException("EventHub not available.");

            if (_videoSourceFactory == null)
                throw new InvalidOperationException("VideoSourceFactory not available.");

            IRoomFacade room = _connectionService.CurrentRoom;
            _videoTrackManager = new VideoTrackManager(
                () => room,
                _eventHub,
                _logger,
                _videoSourceFactory);
        }

        private void CancelPendingPublish()
        {
            lock (_syncRoot)
            {
                _publishVersion++;
                _publishCts?.Cancel();
            }
        }

        private bool UsesWebGLCanvasPublishPath() =>
            _platformProvider() == RuntimePlatform.WebGLPlayer;

        private async Task RestartPublishingAsync()
        {
            CancelPendingPublish();
            Task cleanup = UnpublishCurrentAsync();
            SchedulePublish();
            await cleanup;
        }

        internal async Task ObserveBackgroundOperationAsync(
            Task operation,
            string operationName)
        {
            try
            {
                await operation;
            }
            catch (OperationCanceledException)
            {
                // Internal lifecycle cancellation is expected during teardown or supersession.
            }
            catch (Exception exception)
            {
                _backgroundFailureReporter(
                    $"Vision background {operationName} failed ({exception.GetType().Name}).");
            }
        }

        private void ReportBackgroundFailure(string message) =>
            _logger.Error(message, LogCategory.Vision);

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VisionPublishCoordinator));
        }
    }
}
