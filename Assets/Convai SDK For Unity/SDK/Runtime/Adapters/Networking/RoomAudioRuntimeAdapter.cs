using System;
using Convai.Runtime.Room;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime.Core.Coordinators;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Runtime.Networking.Media;
using Convai.Shared.Abstractions;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;
using Convai.Runtime.Core.Async;

namespace Convai.Runtime.Adapters.Networking
{
    internal sealed class RoomAudioRuntimeAdapter : IRoomAudioRuntimeAdapter
    {
        private readonly Func<AudioTrackManager> _audioTrackManagerProvider;
        private readonly Func<IEventHub> _eventHubProvider;
        private readonly Func<GameObject> _gameObjectProvider;
        private readonly ILogger _logger;
        private readonly Func<ResolvedTurnTakingOptions> _currentResolvedTurnTakingOptionsProvider;
        private readonly Func<IMicrophoneDeviceService> _microphoneDeviceServiceProvider;
        private readonly Func<IMicrophoneSourceFactory> _microphoneSourceFactoryProvider;
        private readonly Func<string> _preferredMicrophoneDeviceIdProvider;
        private readonly Func<IConvaiRoomController> _roomControllerProvider;
        private readonly Action<IMicrophoneSourceFactory> _setMicrophoneSourceFactory;
        private readonly Func<ITransportProvider> _transportProviderProvider;
        private readonly SemaphoreSlim _microphoneRefreshGate = new(1, 1);
        private readonly object _trackedMicrophoneSourceLock = new();
        private IClientVoiceActivityDetector _clientVoiceActivityDetector;
        // Read on the audio thread, written on the main thread. Volatile rather than lock-guarded:
        // the audio callback must never wait on a lock the main thread can be holding.
        private volatile MicrophoneLevelGate _microphoneLevelGate;
        private volatile IMicrophonePcmSource _levelGateSource;
        private IMicrophoneSource _trackedMicrophoneSource;

        public RoomAudioRuntimeAdapter(
            Func<AudioTrackManager> audioTrackManagerProvider,
            Func<IConvaiRoomController> roomControllerProvider,
            Func<IMicrophoneSourceFactory> microphoneSourceFactoryProvider,
            Action<IMicrophoneSourceFactory> setMicrophoneSourceFactory,
            Func<ITransportProvider> transportProviderProvider,
            Func<ResolvedTurnTakingOptions> currentResolvedTurnTakingOptionsProvider,
            Func<string> preferredMicrophoneDeviceIdProvider,
            Func<IMicrophoneDeviceService> microphoneDeviceServiceProvider,
            Func<IEventHub> eventHubProvider,
            Func<GameObject> gameObjectProvider,
            ILogger logger = null)
        {
            _audioTrackManagerProvider = audioTrackManagerProvider ??
                                         throw new ArgumentNullException(nameof(audioTrackManagerProvider));
            _roomControllerProvider =
                roomControllerProvider ?? throw new ArgumentNullException(nameof(roomControllerProvider));
            _microphoneSourceFactoryProvider = microphoneSourceFactoryProvider ??
                                               throw new ArgumentNullException(nameof(microphoneSourceFactoryProvider));
            _setMicrophoneSourceFactory = setMicrophoneSourceFactory ??
                                          throw new ArgumentNullException(nameof(setMicrophoneSourceFactory));
            _transportProviderProvider = transportProviderProvider ??
                                         throw new ArgumentNullException(nameof(transportProviderProvider));
            _currentResolvedTurnTakingOptionsProvider = currentResolvedTurnTakingOptionsProvider ??
                                                        throw new ArgumentNullException(
                                                            nameof(currentResolvedTurnTakingOptionsProvider));
            _preferredMicrophoneDeviceIdProvider = preferredMicrophoneDeviceIdProvider ??
                                                   throw new ArgumentNullException(nameof(preferredMicrophoneDeviceIdProvider));
            _microphoneDeviceServiceProvider = microphoneDeviceServiceProvider ??
                                              throw new ArgumentNullException(nameof(microphoneDeviceServiceProvider));
            _eventHubProvider = eventHubProvider ?? throw new ArgumentNullException(nameof(eventHubProvider));
            _gameObjectProvider = gameObjectProvider ?? throw new ArgumentNullException(nameof(gameObjectProvider));
            _logger = logger.WithTag(nameof(RoomAudioRuntimeAdapter), LogCategory.Audio);
        }

        public bool IsMicMuted => _audioTrackManagerProvider()?.IsMicMuted ?? false;

        public void SetMicMuted(bool muted)
        {
            AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
            if (audioTrackManager != null)
                audioTrackManager.SetMicMuted(muted);
            else
                _roomControllerProvider()?.SetMicMuted(muted);
        }

        public async Task StartListeningAsync(int microphoneIndex, CancellationToken ct)
        {
            await ConvaiTimers.WaitAsync(_microphoneRefreshGate, ct);
            try
            {
                await PublishMicrophoneAsync(microphoneIndex, publishSessionErrors: true, ct).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            }
            finally
            {
                _microphoneRefreshGate.Release();
            }
        }

        internal async Task StopAndDrainCaptureAsync(CancellationToken ct)
        {
            await ConvaiTimers.WaitAsync(_microphoneRefreshGate, ct);
            try
            {
                IMicrophoneSource source;
                lock (_trackedMicrophoneSourceLock) source = _trackedMicrophoneSource;
                if (source is not IMicrophoneCaptureDrain drain)
                    throw new NotSupportedException("This audio source does not expose a media sender drain boundary.");
                await drain.StopAndDrainCaptureAsync(ct);
                try
                {
                    _audioTrackManagerProvider()?.SetMicMuted(true);
                    UntrackCurrentMicrophoneSource();
                    if (_audioTrackManagerProvider() is AudioTrackManager manager)
                        await manager.UnpublishMicrophoneAsync();
                }
                finally
                {
                    // The manager borrows the source; this adapter owns its Unity/native resources.
                    await UnityScheduler.PostAsync(source.Dispose);
                }
            }
            finally { _microphoneRefreshGate.Release(); }
        }

        public async Task StopListeningAsync(CancellationToken ct)
        {
            await ConvaiTimers.WaitAsync(_microphoneRefreshGate, ct);
            try
            {
                UntrackCurrentMicrophoneSource();
                AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
                if (audioTrackManager != null)
                    await audioTrackManager.UnpublishMicrophoneAsync().ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            }
            finally
            {
                _microphoneRefreshGate.Release();
            }
        }

        internal async Task EnsurePublishedMicrophoneAsync(bool republishIfAlreadyPublished, CancellationToken ct)
        {
            await ConvaiTimers.WaitAsync(_microphoneRefreshGate, ct);
            try
            {
                AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
                if (audioTrackManager == null || !(_roomControllerProvider()?.IsConnectedToRoom ?? false))
                    return;

                IMicrophoneSource trackedSource;
                lock (_trackedMicrophoneSourceLock)
                {
                    trackedSource = _trackedMicrophoneSource;
                }

                if (trackedSource != null && !republishIfAlreadyPublished)
                    return;

                IMicrophoneSourceFactory microphoneFactory = ResolveMicrophoneFactory();
                if (microphoneFactory == null)
                    return;

                int microphoneIndex = trackedSource != null
                    ? ResolveRecoveryMicrophoneIndex(
                        trackedSource,
                        MicrophoneSessionInvalidationReason.RouteChanged,
                        microphoneFactory)
                    : ResolvePreferredMicrophoneIndex(microphoneFactory);

                await PublishMicrophoneAsync(microphoneIndex, publishSessionErrors: true, ct).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            }
            finally
            {
                _microphoneRefreshGate.Release();
            }
        }

        private async Task PublishMicrophoneAsync(int microphoneIndex, bool publishSessionErrors, CancellationToken ct)
        {

            AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
            if (audioTrackManager == null || !(_roomControllerProvider()?.IsConnectedToRoom ?? false))
            {
                _logger?.Warning(
                    "Microphone publish aborted because the room audio path is not initialized.");
                return;
            }

            IMicrophoneSourceFactory microphoneFactory = ResolveMicrophoneFactory();
            if (microphoneFactory == null)
            {
                _logger?.Error(
                    "Microphone publish failed: IMicrophoneSourceFactory not registered.");
                return;
            }

            ResolvedMicrophoneTarget target = ResolveRequestedTarget(microphoneFactory, microphoneIndex);
            if (target.UsingPlatformDefaultFallback)
                _logger?.Debug(
                    "Microphone enumeration returned no devices; attempting platform default fallback.");

            IMicrophoneSource microphoneSource = null;
            try
            {
                microphoneSource = microphoneFactory.Create(target.DeviceName, target.DeviceIndex, _gameObjectProvider());
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error) == true)
                    _logger?.Error($"Microphone publish failed while creating microphone source: {ex}");
                if (publishSessionErrors)
                    PublishMicrophonePublishFailure(target.UsingPlatformDefaultFallback);
                return;
            }

            microphoneSource.EnableAcousticEchoCancellation =
                (_currentResolvedTurnTakingOptionsProvider() ?? ResolvedTurnTakingOptions.DefaultHandsFree)
                .EnableAcousticEchoCancellation;
            UntrackCurrentMicrophoneSource();
            MicrophonePublishResult result = await audioTrackManager.RepublishMicrophoneAsync(microphoneSource,
                AudioPublishOptions.DefaultMicrophone).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);

            if (!result.Succeeded)
            {
                RunOnMainThread(() => microphoneSource.Dispose());
                if (publishSessionErrors)
                    PublishMicrophonePublishFailure(target.UsingPlatformDefaultFallback, result);
                return;
            }

            TrackMicrophoneSource(microphoneSource);
        }

        private void PublishMicrophonePublishFailure(bool usingPlatformDefaultFallback,
            MicrophonePublishResult result = default)
        {
            // A platform that explains the failure (for example a browser that denied permission) knows
            // more than the device enumeration does, so its reason wins.
            if (result.FailureCode is SessionErrorCodes.AudioMicPermissionDenied
                or SessionErrorCodes.AudioMicUnavailable)
            {
                _eventHubProvider()?.Publish(SessionError.Create(result.FailureCode, result.FailureMessage,
                    null, true));
                return;
            }

            if (usingPlatformDefaultFallback)
            {
                _logger?.Warning(
                    "Microphone enumeration returned no devices and platform default fallback failed.");
                _eventHubProvider()?.Publish(SessionError.Create(SessionErrorCodes.AudioMicUnavailable,
                    "No microphone devices detected", null, true));
                return;
            }

            _logger?.Error(string.IsNullOrEmpty(result.FailureMessage)
                ? "Failed to publish microphone."
                : $"Failed to publish microphone: {result.FailureMessage}");
            _eventHubProvider()?.Publish(SessionError.Create(SessionErrorCodes.AudioMicPublishFailed,
                result.FailureMessage ?? "Failed to publish microphone audio", null, true));
        }

        private void RunOnMainThread(Action action)
        {
            if (action == null)
                return;

            if (UnityScheduler.Instance.IsMainThread())
            {
                action();
                return;
            }

            UnityScheduler.Instance.ScheduleOnMainThread(action);
        }

        private IMicrophoneSourceFactory ResolveMicrophoneFactory()
        {
            IMicrophoneSourceFactory microphoneFactory = _microphoneSourceFactoryProvider();
            if (microphoneFactory != null)
                return microphoneFactory;

            ITransportProvider transportProvider = _transportProviderProvider();
            if (transportProvider == null)
                return null;

            microphoneFactory = transportProvider.CreateMicrophoneFactory();
            _setMicrophoneSourceFactory(microphoneFactory);
            return microphoneFactory;
        }

        private ResolvedMicrophoneTarget ResolveRequestedTarget(IMicrophoneSourceFactory microphoneFactory,
            int microphoneIndex)
        {
            string[] devices = microphoneFactory.GetAvailableDevices() ?? Array.Empty<string>();
            if (devices.Length == 0)
                return new ResolvedMicrophoneTarget(null, 0, true);

            int deviceIndex = Mathf.Clamp(microphoneIndex, 0, devices.Length - 1);
            return new ResolvedMicrophoneTarget(devices[deviceIndex], deviceIndex, false);
        }

        private int ResolvePreferredMicrophoneIndex(IMicrophoneSourceFactory microphoneFactory)
        {
            string[] devices = microphoneFactory.GetAvailableDevices() ?? Array.Empty<string>();
            if (devices.Length == 0)
                return 0;

            IMicrophoneDeviceService microphoneDeviceService = _microphoneDeviceServiceProvider();
            if (microphoneDeviceService == null)
                return 0;

            int preferredIndex =
                microphoneDeviceService.ResolvePreferredDeviceIndex(_preferredMicrophoneDeviceIdProvider());
            if (preferredIndex < 0 || preferredIndex >= devices.Length)
                return 0;

            return preferredIndex;
        }

        private int ResolveRecoveryMicrophoneIndex(IMicrophoneSource source, MicrophoneSessionInvalidationReason reason,
            IMicrophoneSourceFactory microphoneFactory)
        {
            string[] devices = microphoneFactory.GetAvailableDevices() ?? Array.Empty<string>();
            if (devices.Length == 0)
                return 0;

            if (reason == MicrophoneSessionInvalidationReason.DeviceChanged)
            {
                IMicrophoneDeviceService microphoneDeviceService = _microphoneDeviceServiceProvider();
                if (microphoneDeviceService != null)
                {
                    int preferredIndex =
                        microphoneDeviceService.ResolvePreferredDeviceIndex(_preferredMicrophoneDeviceIdProvider());
                    if (preferredIndex >= 0 && preferredIndex < devices.Length)
                        return preferredIndex;
                }

                return 0;
            }

            int matchedIndex = FindDeviceIndex(devices, source?.DeviceName);
            if (matchedIndex >= 0)
                return matchedIndex;

            return Mathf.Clamp(source?.DeviceIndex ?? 0, 0, devices.Length - 1);
        }

        private static int FindDeviceIndex(string[] devices, string deviceName)
        {
            if (devices == null || devices.Length == 0 || string.IsNullOrWhiteSpace(deviceName))
                return -1;

            for (int i = 0; i < devices.Length; i++)
            {
                if (string.Equals(devices[i], deviceName, StringComparison.Ordinal) ||
                    string.Equals(devices[i], deviceName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private void TrackMicrophoneSource(IMicrophoneSource microphoneSource)
        {
            IMicrophoneSource previousSource;
            lock (_trackedMicrophoneSourceLock)
            {
                previousSource = _trackedMicrophoneSource;
                _trackedMicrophoneSource = microphoneSource;
            }

            if (previousSource != null)
                previousSource.SessionInvalidated -= HandleTrackedMicrophoneInvalidated;

            if (microphoneSource != null)
            {
                microphoneSource.SessionInvalidated += HandleTrackedMicrophoneInvalidated;
                StartMicrophoneLevelGate(microphoneSource);
                StartClientVoiceActivityDetection(microphoneSource);
            }
        }

        private void UntrackCurrentMicrophoneSource()
        {
            IClientVoiceActivityDetector detector;
            IMicrophoneSource previousSource;
            lock (_trackedMicrophoneSourceLock)
            {
                detector = _clientVoiceActivityDetector;
                _clientVoiceActivityDetector = null;
                previousSource = _trackedMicrophoneSource;
                _trackedMicrophoneSource = null;
            }

            if (previousSource != null)
                previousSource.SessionInvalidated -= HandleTrackedMicrophoneInvalidated;

            StopMicrophoneLevelGate();

            if (detector != null)
                RunOnMainThread(detector.Dispose);
        }

        /// <summary>
        ///     Starts noticing, from the microphone's own loudness, when the player begins talking.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Unconditional, unlike the Silero detector below: this costs an add and a multiply
        ///         per sample, needs no package, and answers a question every project has — a
        ///         character that only reacts once the service's verdict returns looks like it
        ///         noticed you late. A transport whose microphone does not expose PCM simply does
        ///         not get it, and everything else behaves exactly as before.
        ///     </para>
        /// </remarks>
        private void StartMicrophoneLevelGate(IMicrophoneSource microphoneSource)
        {
            if (microphoneSource is not IMicrophonePcmSource pcmSource)
                return;

            // The gate names itself in its own callback, so the main thread can tell a word from
            // the live gate apart from one queued by a gate that has since been stopped.
            MicrophoneLevelGate gate = null;
            gate = new MicrophoneLevelGate((isActive, level) =>
            {
                MicrophoneLevelGate speaker = gate;
                RunOnMainThread(() =>
                {
                    // Stop publishes its closing word inline when it runs on the main thread, while
                    // an edge raised on the audio thread a moment earlier is still queued behind
                    // it. Ungated, that queued word arrives last and leaves every listener holding
                    // "the player is talking" for a microphone that no longer exists — and nothing
                    // can clear it, because the source that would have is gone.
                    if (!ReferenceEquals(_microphoneLevelGate, speaker)) return;

                    _eventHubProvider()?.Publish(LocalPlayerActivityChanged.Create(
                        isActive, LocalPlayerActivitySource.Microphone, level));
                });
            });

            lock (_trackedMicrophoneSourceLock)
            {
                if (!ReferenceEquals(_trackedMicrophoneSource, microphoneSource) ||
                    _microphoneLevelGate != null)
                {
                    return;
                }

                _levelGateSource = pcmSource;
                _microphoneLevelGate = gate;
                // Subscribed while the fields are still guarded. Outside the lock a stop running
                // concurrently would unsubscribe a handler that had not been attached yet and then
                // this line would attach it to a source nothing will ever detach it from.
                pcmSource.PcmFrame += HandleLevelGatePcmFrame;
            }
        }

        private void StopMicrophoneLevelGate()
        {
            MicrophoneLevelGate gate;
            IMicrophonePcmSource source;
            lock (_trackedMicrophoneSourceLock)
            {
                gate = _microphoneLevelGate;
                source = _levelGateSource;
                // Cleared before unsubscribing, so a frame already in flight on the audio thread
                // finds nothing to feed rather than a gate that is being torn down.
                _microphoneLevelGate = null;
                _levelGateSource = null;
            }

            if (source != null) source.PcmFrame -= HandleLevelGatePcmFrame;

            // The adapter has the last word, rather than the gate's own reset. A frame already in
            // flight on the audio thread can still be inside Observe while this runs, and if its
            // rising edge reached the main thread after the gate's falling one, a consumer would be
            // left holding "the player is talking" for a microphone that no longer exists — and
            // nothing would ever arrive to clear it. Saying it here, after the unsubscribe, means
            // the closing word is always the last one published.
            gate?.Reset();
            RunOnMainThread(() => _eventHubProvider()?.Publish(
                LocalPlayerActivityChanged.Create(false, LocalPlayerActivitySource.Microphone)));
        }

        /// <summary>
        ///     Main thread. Keeps the level gate from hearing the character through the player's
        ///     speakers, using the room-wide answer the SDK already keeps.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Room-wide is the point. Any character's audio comes out of the same speakers, so
        ///         asking whether <i>this</i> character is talking would leave a character that is
        ///         silent turning to the player every time the one beside it answered.
        ///         <c>HasActiveCharacterAudioPlayback</c> is that answer and the SDK maintains it
        ///         already; this only asks it on the thread it is safe to ask on, exactly as the
        ///         client barge-in detector does.
        ///     </para>
        ///     <para>
        ///         Echo cancellation, where it is genuinely running, removes the sound before it is
        ///         ever measured — so a project that has it keeps hearing the player through its own
        ///         character. A configured preference is not proof it initialized, so the source's
        ///         live answer is what is asked.
        ///     </para>
        /// </remarks>
        internal void RefreshMicrophoneEchoSuppression()
        {
            MicrophoneLevelGate gate = _microphoneLevelGate;
            if (gate == null) return;

            // A muted microphone stops delivering frames at all — the transport returns from its
            // audio callback before raising the processed-audio event — so a gate that was open
            // when the mute landed can never observe the silence that would close it, and whatever
            // it last said stands until the player speaks and stops again. Mute yourself mid
            // sentence and the character goes on treating you as talking.
            //
            // Read as state rather than intercepted as a call, because several paths mute through
            // the track manager directly and never pass through this adapter's own SetMicMuted.
            // Reset says the closing word once and is silent on every tick after it.
            if (IsMicMuted)
            {
                gate.Reset();
                return;
            }

            bool characterAudible = _audioTrackManagerProvider()?.HasActiveCharacterAudioPlayback ?? false;
            bool echoCancelled = _levelGateSource?.IsAcousticEchoCancellationActive == true;
            gate.SetSuppressed(characterAudible && !echoCancelled);
        }

        /// <summary>
        ///     Audio thread. Measures loudness and nothing else.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Deliberately free of every other question. Whether a character is audible is asked
        ///         in <see cref="RefreshMicrophoneEchoSuppression" /> on the main thread instead, because
        ///         that answer walks a collection the main thread mutates as characters join and leave —
        ///         reading it from the audio callback could throw mid-frame, and the lock it would need
        ///         is one the audio thread must never wait on.
        ///     </para>
        /// </remarks>
        private void HandleLevelGatePcmFrame(float[] samples, int channels, int sampleRate) =>
            _microphoneLevelGate?.Observe(samples, channels, sampleRate);

        /// <summary>
        ///     Whether echo cancellation is live on the microphone the client barge-in detector
        ///     listens to, or null while no detector is running (WebGL, detection off, not started).
        /// </summary>
        internal bool? ClientBargeInAecActive
        {
            get
            {
                lock (_trackedMicrophoneSourceLock)
                {
                    if (_clientVoiceActivityDetector == null) return null;
                    return _levelGateSource?.IsAcousticEchoCancellationActive == true;
                }
            }
        }

        private void StartClientVoiceActivityDetection(IMicrophoneSource microphoneSource)
        {
            ResolvedTurnTakingOptions options =
                _currentResolvedTurnTakingOptionsProvider() ?? ResolvedTurnTakingOptions.DefaultHandsFree;
            if (options.ClientBargeInMode != Room.ClientBargeInMode.Silero)
                return;

            if (microphoneSource is not IMicrophonePcmSource pcmSource)
            {
                _logger?.Warning(
                    "Client-side Silero VAD is enabled but the active microphone transport does not expose PCM. Server interruption remains active.");
                return;
            }

            IClientVoiceActivityDetectorFactory factory = ClientVoiceActivityDetectorFactoryRegistry.Factory;
            if (factory == null)
            {
                _logger?.Warning(
                    "Client-side Silero VAD is enabled but its optional inference module is unavailable. Install com.unity.ai.inference 2.2.1 or newer; server interruption remains active.");
                return;
            }

            RunOnMainThread(() =>
            {
                lock (_trackedMicrophoneSourceLock)
                {
                    if (!ReferenceEquals(_trackedMicrophoneSource, microphoneSource) ||
                        _clientVoiceActivityDetector != null)
                        return;
                }

                IClientVoiceActivityDetector detector;
                // One holdoff per detector: the detector polls this on the main thread every frame.
                var holdoff = new CharacterAudibleHoldoff();
                try
                {
                    detector = factory.Create(
                        pcmSource,
                        _gameObjectProvider(),
                        () => holdoff.ShouldProcess(
                            _audioTrackManagerProvider()?.HasActiveCharacterAudioPlayback ?? false,
                            Time.unscaledTimeAsDouble),
                        state => _eventHubProvider()?.Publish(state),
                        _logger);
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Warning) == true)
                        _logger?.Warning(
                        $"Client-side Silero VAD could not start ({ex.Message}). Server interruption remains active.");
                    return;
                }

                lock (_trackedMicrophoneSourceLock)
                {
                    if (ReferenceEquals(_trackedMicrophoneSource, microphoneSource) &&
                        _clientVoiceActivityDetector == null)
                    {
                        _clientVoiceActivityDetector = detector;
                        return;
                    }
                }

                detector?.Dispose();
            });
        }

        private void HandleTrackedMicrophoneInvalidated(IMicrophoneSource microphoneSource,
            MicrophoneSessionInvalidationReason reason)
        {
            _ = HandleTrackedMicrophoneInvalidatedAsync(microphoneSource, reason);
        }

        private async Task HandleTrackedMicrophoneInvalidatedAsync(IMicrophoneSource microphoneSource,
            MicrophoneSessionInvalidationReason reason)
        {
            if (microphoneSource == null)
                return;

            lock (_trackedMicrophoneSourceLock)
            {
                if (!ReferenceEquals(_trackedMicrophoneSource, microphoneSource))
                    return;
            }

            if (!await _microphoneRefreshGate.WaitAsync(0).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext))
                return;

            try
            {
                lock (_trackedMicrophoneSourceLock)
                {
                    if (!ReferenceEquals(_trackedMicrophoneSource, microphoneSource))
                        return;
                }

                AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
                if (audioTrackManager == null || !audioTrackManager.IsCurrentMicrophoneSource(microphoneSource))
                    return;

                IMicrophoneSourceFactory microphoneFactory = ResolveMicrophoneFactory();
                if (microphoneFactory == null)
                    return;

                int recoveryIndex = ResolveRecoveryMicrophoneIndex(microphoneSource, reason, microphoneFactory);
                if (_logger?.IsEnabled(LogLevel.Info) == true)
                    _logger?.Info(
                    $"Recovering live microphone after {reason} (deviceIndex={recoveryIndex}).");
                await PublishMicrophoneAsync(recoveryIndex, publishSessionErrors: true, CancellationToken.None)
                    .ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            }
            finally
            {
                _microphoneRefreshGate.Release();
            }
        }

        public bool SetCharacterMuted(string characterId, bool muted)
        {
            if (string.IsNullOrEmpty(characterId))
                return false;

            AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
            if (audioTrackManager != null)
            {
                audioTrackManager.SetCharacterAudioMuted(characterId, muted);
                return true;
            }

            return _roomControllerProvider()?.SetCharacterAudioMuted(characterId, muted) ?? false;
        }

        public bool IsCharacterMuted(string characterId)
        {
            if (string.IsNullOrEmpty(characterId))
                return false;

            AudioTrackManager audioTrackManager = _audioTrackManagerProvider();
            if (audioTrackManager != null)
                return audioTrackManager.IsCharacterAudioMuted(characterId);

            return _roomControllerProvider()?.IsCharacterAudioMuted(characterId) ?? false;
        }

        private readonly struct ResolvedMicrophoneTarget
        {
            internal ResolvedMicrophoneTarget(string deviceName, int deviceIndex, bool usingPlatformDefaultFallback)
            {
                DeviceName = deviceName;
                DeviceIndex = deviceIndex;
                UsingPlatformDefaultFallback = usingPlatformDefaultFallback;
            }

            internal string DeviceName { get; }
            internal int DeviceIndex { get; }
            internal bool UsingPlatformDefaultFallback { get; }
        }
    }
}
