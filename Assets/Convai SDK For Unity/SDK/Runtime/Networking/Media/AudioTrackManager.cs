using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Room;
using UnityEngine;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Runtime.Networking.Media
{
    /// <summary>
    ///     Manages audio track operations for Convai room connections.
    ///     Handles microphone publishing, track subscription, and Character audio routing.
    ///     Uses platform-agnostic abstractions for cross-platform compatibility.
    ///     Implements IAudioTrackManager for dependency injection and mocking.
    /// </summary>
    internal class AudioTrackManager : IAudioTrackManager
    {
        private readonly IAgentRegistry _agentRegistry;
        private readonly bool _allowNullAudioTrackInFactory;
        private readonly Func<string, AudioSource> _audioSourceResolver;
        private readonly IAudioStreamFactory _audioStreamFactory;
        private readonly Dictionary<string, RemoteAudioPlaybackRegistration> _remoteAudioRegistrations = new();
        private readonly Dictionary<string, AudioSource> _participantAudioOutputs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ParticipantAudioSubscription> _participantAudioSubscriptions =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, IDisposable> _participantAudioStreams = new(StringComparer.Ordinal);
        private readonly IEventHub _eventHub;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _microphoneOperationLock = new(1, 1);
        private readonly IRemotePlayerRegistry _remotePlayerRegistry;
        private readonly Func<IRoomFacade> _roomFacadeProvider;
        private readonly object _syncRoot = new();
        private ILocalAudioTrack _currentAudioTrack;
        private AudioPublishOptions _currentMicrophonePublishOptions;
        private bool _disposed;
        private bool _hasCurrentMicrophonePublishOptions;
        private bool _unresolvedParticipantRouteLogged;

        private IMicrophoneSource _microphoneSource;

        /// <summary>
        ///     Initializes a new instance of the <see cref="AudioTrackManager" /> class.
        /// </summary>
        /// <param name="roomFacadeProvider">
        ///     Provider function that returns the current room facade.
        ///     Using a provider allows the room to be recreated between connections while maintaining the same AudioTrackManager.
        /// </param>
        /// <param name="agentRegistry">Registry for Character audio routing.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="audioSourceResolver">Function to resolve AudioSource for a character ID. Required for audio routing.</param>
        /// <param name="remotePlayerRegistry">Optional registry for remote player audio (multiplayer).</param>
        /// <param name="audioStreamFactory">Factory for creating audio streams. Required for character audio routing.</param>
        /// <param name="eventHub">
        ///     Optional event hub to publish CharacterAudioPlaybackStateChanged. If null, playback events are
        ///     not published.
        /// </param>
        public AudioTrackManager(
            Func<IRoomFacade> roomFacadeProvider,
            IAgentRegistry agentRegistry,
            ILogger logger,
            Func<string, AudioSource> audioSourceResolver,
            IRemotePlayerRegistry remotePlayerRegistry = null,
            IAudioStreamFactory audioStreamFactory = null,
            IEventHub eventHub = null)
        {
            _roomFacadeProvider = roomFacadeProvider ?? throw new ArgumentNullException(nameof(roomFacadeProvider));
            _agentRegistry = agentRegistry ?? throw new ArgumentNullException(nameof(agentRegistry));
            _audioSourceResolver = audioSourceResolver ?? throw new ArgumentNullException(nameof(audioSourceResolver));
            _logger = logger.WithTag(nameof(AudioTrackManager), LogCategory.Audio);
            _remotePlayerRegistry = remotePlayerRegistry;
            _audioStreamFactory = audioStreamFactory;
            _allowNullAudioTrackInFactory = audioStreamFactory != null;
            _eventHub = eventHub;
        }

        /// <summary>
        ///     Gets the current room facade instance from the provider.
        /// </summary>
        private IRoomFacade RoomFacade => _roomFacadeProvider();

        /// <summary>
        ///     Raised when the microphone mute state changes.
        /// </summary>
        public event Action<bool> OnMicMuteChanged;

        /// <summary>
        ///     Raised when an audio track is subscribed from a remote participant.
        /// </summary>
        public event Action<IRemoteAudioTrack, IRemoteParticipant> OnAudioTrackSubscribed;

        /// <summary>
        ///     Raised when an audio track is unsubscribed from a remote participant.
        /// </summary>
        public event Action<IRemoteAudioTrack, IRemoteParticipant> OnAudioTrackUnsubscribed;

        /// <summary>
        ///     Gets a value indicating whether the microphone is currently muted.
        /// </summary>
        public bool IsMicMuted { get; private set; }

        /// <summary>
        ///     Gets a value indicating whether the microphone is currently publishing.
        /// </summary>
        public bool IsPublishing => _currentAudioTrack != null;

        public bool BindParticipantAudioOutput(string participantIdentity, AudioSource source)
        {
            if (string.IsNullOrWhiteSpace(participantIdentity)) return false;
            List<ParticipantAudioSubscription> subscriptions = null;
            lock (_syncRoot)
            {
                if (source == null) _participantAudioOutputs.Remove(participantIdentity);
                else _participantAudioOutputs[participantIdentity] = source;

                foreach (ParticipantAudioSubscription subscription in _participantAudioSubscriptions.Values)
                {
                    if (!string.Equals(
                            subscription.ParticipantIdentity,
                            participantIdentity,
                            StringComparison.Ordinal)) continue;
                    subscriptions ??= new List<ParticipantAudioSubscription>();
                    subscriptions.Add(subscription);
                }
            }

            if (subscriptions == null) return true;

            foreach (ParticipantAudioSubscription subscription in subscriptions)
            {
                if (source == null)
                {
                    RemoveParticipantAudioRoute(subscription);
                    continue;
                }

                TryRouteBoundParticipantAudio(
                    subscription.AudioTrack,
                    subscription.ParticipantSid,
                    subscription.ParticipantIdentity);
            }
            return true;
        }

        public bool SetParticipantAudioEnabled(string participantIdentity, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(participantIdentity)) return false;
            lock (_syncRoot)
            {
                if (!_participantAudioOutputs.TryGetValue(participantIdentity, out AudioSource source) || source == null)
                    return false;
                source.mute = !enabled;
                return true;
            }
        }

        /// <summary>
        ///     Clears internal track and microphone references.
        ///     Attempts to stop active microphone capture before dropping references to avoid
        ///     transport-side capture callbacks after room teardown.
        ///     Also clears all remote audio streams to ensure complete cleanup.
        /// </summary>
        public void ClearState()
        {
            IMicrophoneSource sourceToStop = null;

            lock (_syncRoot)
            {
                sourceToStop = _microphoneSource;
                _currentAudioTrack = null;
                _microphoneSource = null;
            }

            if (sourceToStop != null)
            {
                try
                {
                    sourceToStop.StopCapture();
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Warning) == true)
                        _logger?.Warning(
                        $"Failed to stop microphone source during ClearState: {ex.Message}");
                }
            }

            ClearRemoteAudio();

            _logger?.Debug("State cleared (track, microphone, and remote audio streams reset)");
        }

        /// <summary>
        ///     Publishes a microphone audio track to the room using platform-agnostic types.
        /// </summary>
        /// <param name="microphoneSource">The microphone source abstraction to publish.</param>
        /// <param name="options">Audio publish options.</param>
        /// <returns>A task that completes with true if publishing succeeded; otherwise, false.</returns>
        public async Task<bool> PublishMicrophoneAsync(IMicrophoneSource microphoneSource, AudioPublishOptions options)
        {
            ThrowIfDisposed();

            if (microphoneSource == null) throw new ArgumentNullException(nameof(microphoneSource));

            await ConvaiTimers.WaitAsync(_microphoneOperationLock).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            try
            {
                MicrophonePublishResult result =
                    await PublishMicrophoneCoreAsync(microphoneSource, options).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
                return result.Succeeded;
            }
            finally
            {
                _microphoneOperationLock.Release();
            }
        }

        internal async Task<MicrophonePublishResult> RepublishMicrophoneAsync(IMicrophoneSource microphoneSource,
            AudioPublishOptions? options = null)
        {
            ThrowIfDisposed();

            if (microphoneSource == null) throw new ArgumentNullException(nameof(microphoneSource));

            await ConvaiTimers.WaitAsync(_microphoneOperationLock).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            try
            {
                return await PublishMicrophoneCoreAsync(microphoneSource,
                    options ?? GetCurrentMicrophonePublishOptionsOrDefault()).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            }
            finally
            {
                _microphoneOperationLock.Release();
            }
        }

        internal bool IsCurrentMicrophoneSource(IMicrophoneSource microphoneSource)
        {
            lock (_syncRoot)
                return ReferenceEquals(_microphoneSource, microphoneSource);
        }

        internal AudioPublishOptions GetCurrentMicrophonePublishOptionsOrDefault()
        {
            lock (_syncRoot)
                return _hasCurrentMicrophonePublishOptions
                    ? _currentMicrophonePublishOptions
                    : AudioPublishOptions.DefaultMicrophone;
        }

        private async Task<MicrophonePublishResult> PublishMicrophoneCoreAsync(IMicrophoneSource microphoneSource,
            AudioPublishOptions options)
        {
            ThrowIfDisposed();

            if (microphoneSource == null) throw new ArgumentNullException(nameof(microphoneSource));

            IRoomFacade room = RoomFacade;
            if (room?.LocalParticipant == null)
            {
                _logger?.Error("PublishMicrophoneAsync aborted: LocalParticipant is null");
                return MicrophonePublishResult.Failure();
            }

            try
            {
                await UnpublishMicrophoneCoreAsync(clearPublishOptions: false).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);

                // Delegate to the platform-specific local participant implementation
                ILocalAudioTrack track = await RunOnMainThreadAsync(() =>
                        room.LocalParticipant.PublishAudioTrackAsync(
                            microphoneSource,
                            options,
                            CancellationToken.None))
                    .ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);

                if (track == null)
                {
                    _logger?.Error("PublishMicrophoneAsync failed: track is null");
                    return MicrophonePublishResult.Failure();
                }

                lock (_syncRoot)
                {
                    _microphoneSource = microphoneSource;
                    _currentAudioTrack = track;
                    _currentMicrophonePublishOptions = options;
                    _hasCurrentMicrophonePublishOptions = true;

                    if (IsMicMuted && _microphoneSource != null) _microphoneSource.IsMuted = true;
                }

                _logger?.Info("Microphone published successfully");
                return MicrophonePublishResult.Success;
            }
            catch (Exception ex) when (ex.GetBaseException() is ConvaiOperationException operationFailure)
            {
                // An explained failure such as a denied permission is an expected outcome, not a fault.
                if (_logger?.IsEnabled(LogLevel.Warning) == true)
                    _logger?.Warning($"Microphone was not published: {operationFailure.Message}");
                return MicrophonePublishResult.Failure(operationFailure.Code, operationFailure.Message);
            }
            catch (Exception ex)
            {
                if (_logger?.IsEnabled(LogLevel.Error) == true)
                    _logger?.Error($"Exception in PublishMicrophoneAsync: {ex}");
                return MicrophonePublishResult.Failure();
            }
        }

        /// <summary>
        ///     Unpublishes the current microphone audio track.
        /// </summary>
        /// <returns>A task that completes when unpublishing is done.</returns>
        public async Task UnpublishMicrophoneAsync()
        {
            ThrowIfDisposed();

            await ConvaiTimers.WaitAsync(_microphoneOperationLock).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            try
            {
                await UnpublishMicrophoneCoreAsync(clearPublishOptions: true).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            }
            finally
            {
                _microphoneOperationLock.Release();
            }
        }

        private async Task UnpublishMicrophoneCoreAsync(bool clearPublishOptions)
        {
            ThrowIfDisposed();

            ILocalAudioTrack trackToUnpublish;
            IMicrophoneSource sourceToStop;

            lock (_syncRoot)
            {
                trackToUnpublish = _currentAudioTrack;
                sourceToStop = _microphoneSource;
                _currentAudioTrack = null;
                _microphoneSource = null;
                if (clearPublishOptions)
                    _hasCurrentMicrophonePublishOptions = false;
            }

            if (sourceToStop != null)
            {
                try
                {
                    await RunOnMainThreadAsync(() => sourceToStop.StopCapture()).ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
                }
                catch (Exception ex)
                {
                    if (_logger?.IsEnabled(LogLevel.Error) == true)
                        _logger?.Error(
                        $"Exception in UnpublishMicrophoneAsync while stopping microphone: {ex}");
                }
            }

            if (trackToUnpublish != null)
            {
                IRoomFacade room = RoomFacade;
                if (room?.LocalParticipant != null)
                {
                    try
                    {
                        await room.LocalParticipant.UnpublishTrackAsync(trackToUnpublish, CancellationToken.None)
                            .ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
                    }
                    catch (Exception ex)
                    {
                        if (_logger?.IsEnabled(LogLevel.Warning) == true)
                            _logger?.Warning(
                            $"Exception in UnpublishMicrophoneAsync while unpublishing track (may be stale): {ex.Message}");
                    }
                }
                else
                {
                    _logger?.Debug(
                        "UnpublishMicrophoneAsync: clearing stale track reference (room not available)");
                }
            }
        }

        /// <summary>
        ///     Sets the microphone mute state.
        /// </summary>
        /// <param name="muted">True to mute the microphone; false to unmute.</param>
        public void SetMicMuted(bool muted)
        {
            ThrowIfDisposed();

            bool changed;
            lock (_syncRoot)
            {
                changed = IsMicMuted != muted;
                IsMicMuted = muted;

                if (_microphoneSource != null)
                {
                    try
                    {
                        _microphoneSource.IsMuted = muted;
                        if (_logger?.IsEnabled(LogLevel.Info) == true)
                            _logger?.Info(
                            $"Microphone mute state changed: {(muted ? "MUTED" : "UNMUTED")}");
                    }
                    catch (Exception ex)
                    {
                        if (_logger?.IsEnabled(LogLevel.Error) == true)
                            _logger?.Error($"SetMicMuted failed to set mute on MicrophoneSource: {ex}");
                    }
                }
                else
                {
                    if (_logger?.IsEnabled(LogLevel.Debug) == true)
                        _logger?.Debug(
                        $"SetMicMuted called but MicrophoneSource is null (muted={muted})");
                }
            }

            if (changed)
            {
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    _logger?.Debug($"Microphone mute state changed event fired: muted={muted}");
                OnMicMuteChanged?.Invoke(muted);
            }
        }

        /// <summary>
        ///     Toggles the microphone mute state.
        /// </summary>
        public void ToggleMicMute() => SetMicMuted(!IsMicMuted);

        /// <summary>
        ///     Releases all resources used by the <see cref="AudioTrackManager" />.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            ClearRemoteAudio();

            lock (_syncRoot)
            {
                if (_microphoneSource != null)
                {
                    try
                    {
                        _microphoneSource.StopCapture();
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log(LogLevel.Debug,
                            $"StopCapture failed during Dispose: {ex.Message}");
                    }

                    _microphoneSource = null;
                }

                _currentAudioTrack = null;
                _hasCurrentMicrophonePublishOptions = false;
            }

            _remotePlayerRegistry?.Clear();
            _microphoneOperationLock.Dispose();

            GC.SuppressFinalize(this);
        }

        private static void ApplyAudioMuteState(AudioSource source, bool isMuted)
        {
            source.mute = isMuted;
        }

        /// <summary>
        ///     Reads the measured audio playhead for a character's remote stream: seconds of source
        ///     audio actually rendered since the current playback signal started. Returns false when
        ///     the stream is missing or does not expose the legacy playhead. WebGL exposes its
        ///     browser media clock through the separate internal media-timeline capability.
        /// </summary>
        public bool TryGetAudioPlayhead(string characterId, out double playedSeconds)
        {
            playedSeconds = 0d;
            if (string.IsNullOrEmpty(characterId)) return false;
            return TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration registration) &&
                   registration.TryGetPlayhead(out playedSeconds);
        }

        internal bool TryGetAudioTimeline(string characterId, out AudioTimelineSnapshot snapshot)
        {
            snapshot = default;
            if (string.IsNullOrEmpty(characterId)) return false;

            return TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration registration) &&
                   registration.TryGetTimeline(out snapshot);
        }

        internal bool TryGetAudioMediaTimeline(string characterId, out AudioMediaTimelineSnapshot snapshot)
        {
            snapshot = default;
            if (string.IsNullOrEmpty(characterId)) return false;

            return TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration registration) &&
                   registration.TryGetMediaTimeline(out snapshot);
        }

        internal int DuckActiveCharacterAudio(float targetGain, float durationSeconds)
        {
            int affected = 0;
            foreach (RemoteAudioPlaybackRegistration registration in _remoteAudioRegistrations.Values)
            {
                if (registration.Duck(targetGain, durationSeconds))
                    affected++;
            }

            return affected;
        }

        internal bool DuckCharacterAudio(
            string characterId,
            float targetGain,
            float durationSeconds) =>
            !string.IsNullOrEmpty(characterId) &&
            TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration registration) &&
            registration.Duck(targetGain, durationSeconds);

        internal bool HasActiveCharacterAudioPlayback
        {
            get
            {
                foreach (RemoteAudioPlaybackRegistration registration in _remoteAudioRegistrations.Values)
                {
                    if (registration.IsPlaying || registration.IsDucked)
                        return true;
                }

                return false;
            }
        }

        internal bool IsCharacterAudioPlaybackActive(string characterId) =>
            !string.IsNullOrEmpty(characterId) &&
            TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration registration) &&
            (registration.IsPlaying || registration.IsDucked);

        internal int CommitActiveCharacterAudioInterruption(float durationSeconds)
        {
            int affected = 0;
            foreach (RemoteAudioPlaybackRegistration registration in _remoteAudioRegistrations.Values)
            {
                if (registration.CommitInterruption(durationSeconds))
                    affected++;
            }

            return affected;
        }

        internal bool CommitCharacterAudioInterruption(
            string characterId,
            float durationSeconds) =>
            !string.IsNullOrEmpty(characterId) &&
            TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration registration) &&
            registration.CommitInterruption(durationSeconds);

        internal int RestoreInterruptedCharacterAudio(float durationSeconds)
        {
            int affected = 0;
            foreach (RemoteAudioPlaybackRegistration registration in _remoteAudioRegistrations.Values)
            {
                if (registration.Restore(durationSeconds))
                    affected++;
            }

            return affected;
        }

        internal bool RestoreInterruptedCharacterAudio(
            string characterId,
            float durationSeconds) =>
            !string.IsNullOrEmpty(characterId) &&
            TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration registration) &&
            registration.Restore(durationSeconds);

        private bool TryResolveCharacter(string participantSid, string participantIdentity,
            out IConvaiCharacterAgent agent)
        {
            if (_agentRegistry is IMultiCharacterSessionRegistry multiRegistry &&
                multiRegistry.CurrentMultiCharacterSession is { } multiSession)
            {
                CharacterRoomMembership membership = multiSession.Resolve(
                    null,
                    participantIdentity,
                    participantSid);
                if (membership?.Character != null)
                {
                    agent = membership.Character;
                    if (!string.IsNullOrWhiteSpace(participantSid))
                        multiSession.BindParticipant(membership, participantSid);
                    return true;
                }

                agent = null;
                return false;
            }

            if (!string.IsNullOrEmpty(participantSid) &&
                _agentRegistry.TryGetCharacterByParticipantId(participantSid, out agent))
                return true;

            if (!string.IsNullOrEmpty(participantIdentity) &&
                _agentRegistry.TryGetCharacter(participantIdentity, out agent))
                return true;

            IReadOnlyList<IConvaiCharacterAgent> all = _agentRegistry.Characters;
            if (all != null && all.Count == 1)
            {
                agent = all[0];
                return true;
            }

            if (!_unresolvedParticipantRouteLogged)
            {
                _unresolvedParticipantRouteLogged = true;
                if (_logger?.IsEnabled(LogLevel.Warning, LogCategory.Audio) == true)
                    _logger?.Warning(
                    $"Rejected ambiguous remote audio route: participantSid='{participantSid}', identity='{participantIdentity}', registeredCharacters={all?.Count ?? 0}.",
                    LogCategory.Audio);
            }

            agent = null;
            return false;
        }

        private bool TryGetRegistrationByCharacter(
            string characterId,
            out RemoteAudioPlaybackRegistration registration)
        {
            foreach (KeyValuePair<string, RemoteAudioPlaybackRegistration> pair in _remoteAudioRegistrations)
            {
                if (!string.Equals(pair.Value.CharacterId, characterId, StringComparison.Ordinal)) continue;
                registration = pair.Value;
                return true;
            }

            registration = null;
            return false;
        }

        private void RemoveRegistration(RemoteAudioPlaybackRegistration registration, bool stopOutput)
        {
            if (registration == null) return;
            _remoteAudioRegistrations.Remove(registration.ParticipantSid);
            registration.Dispose();
            if (stopOutput) registration.StopOutput();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioTrackManager));
        }

        /// <summary>
        ///     Runs an action on the Unity main thread and waits for completion.
        ///     Required because Unity microphone APIs must be called from the main thread.
        /// </summary>
        private static Task RunOnMainThreadAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            UnityScheduler.Instance.ScheduleOnMainThread(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return tcs.Task;
        }

        private static Task<T> RunOnMainThreadAsync<T>(Func<Task<T>> action)
        {
            var tcs = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            UnityScheduler.Instance.ScheduleOnMainThread(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            return AwaitScheduledTaskAsync(tcs.Task);
        }

        private static async Task<T> AwaitScheduledTaskAsync<T>(Task<Task<T>> scheduledTask)
        {
            Task<T> innerTask = await scheduledTask.ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
            return await innerTask.ConfigureAwait(ConvaiAwait.ResumeOnCapturedContext);
        }

        #region Remote Player Management (Future Multiplayer)

        /// <summary>
        ///     Registers a remote player for audio routing.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        /// <param name="displayName">The display name of the remote player.</param>
        public void RegisterRemotePlayer(string participantId, string displayName)
        {
            ThrowIfDisposed();
            _remotePlayerRegistry?.RegisterPlayer(participantId, displayName);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger?.Debug($"Registered remote player: {participantId} ({displayName})");
        }

        /// <summary>
        ///     Unregisters a remote player from audio routing.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        public void UnregisterRemotePlayer(string participantId)
        {
            ThrowIfDisposed();
            _remotePlayerRegistry?.UnregisterPlayer(participantId);
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger?.Debug($"Unregistered remote player: {participantId}");
        }

        /// <summary>
        ///     Subscribes to audio from a remote player.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        /// <remarks>
        ///     In the current implementation, track subscription is handled automatically by the transport layer.
        ///     This method is provided for future explicit subscription control.
        /// </remarks>
        public void SubscribeToPlayerAudio(string participantId)
        {
            ThrowIfDisposed();
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger?.Debug($"Subscribe to player audio requested: {participantId}");
        }

        /// <summary>
        ///     Unsubscribes from audio from a remote player.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        /// <remarks>
        ///     In the current implementation, track unsubscription is handled automatically by the transport layer.
        ///     This method is provided for future explicit subscription control.
        /// </remarks>
        public void UnsubscribeFromPlayerAudio(string participantId)
        {
            ThrowIfDisposed();
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger?.Debug($"Unsubscribe from player audio requested: {participantId}");
        }

        #endregion

        #region Character Audio Management

        /// <summary>
        ///     Sets the mute state for a Character's audio output.
        /// </summary>
        /// <param name="characterId">The Character identifier.</param>
        /// <param name="muted">True to mute; false to unmute.</param>
        /// <returns>True if the Character was found and mute state was set; otherwise, false.</returns>
        public bool SetCharacterAudioMuted(string characterId, bool muted)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(characterId))
            {
                _logger?.Debug("Attempted to set mute on null/empty Character ID");
                return false;
            }

            if (!_agentRegistry.TryGetCharacter(characterId, out IConvaiCharacterAgent _))
            {
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                    _logger?.Debug(
                    $"Character '{characterId}' is not registered; cannot update mute state.");
                return false;
            }

            _agentRegistry.SetCharacterMuted(characterId, muted);

            AudioSource audioSource = _audioSourceResolver(characterId);
            if (audioSource != null)
            {
                audioSource.mute = muted;
                if (_logger?.IsEnabled(LogLevel.Info) == true)
                    _logger?.Info(
                    $"Character audio mute state changed: characterId={characterId}, muted={muted}");
            }

            return true;
        }

        /// <summary>
        ///     Gets the mute state for a Character's audio output.
        /// </summary>
        /// <param name="characterId">The Character identifier.</param>
        /// <returns>True if the Character's audio is muted; false otherwise.</returns>
        public bool IsCharacterAudioMuted(string characterId)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(characterId)) return false;

            return _agentRegistry.TryGetCharacter(characterId, out IConvaiCharacterAgent _) &&
                   _agentRegistry.IsCharacterMuted(characterId);
        }

        /// <summary>
        ///     Handles the event when a remote audio track is subscribed for a Character participant.
        ///     Uses platform-agnostic abstraction types.
        /// </summary>
        /// <param name="audioTrack">The remote audio track abstraction that was subscribed.</param>
        /// <param name="participantSid">The unique session identifier for the participant.</param>
        /// <param name="participantIdentity">The identity string of the participant (typically the Character ID).</param>
        /// <remarks>
        ///     Call this method when a remote audio track is received for a Character, such as when joining a session or when a
        ///     new track is published.
        /// </remarks>
        public void HandleRemoteAudioTrackSubscribed(IRemoteAudioTrack audioTrack, string participantSid,
            string participantIdentity)
        {
            ThrowIfDisposed();

            bool isDebugEnabled = _logger?.IsEnabled(LogLevel.Debug, LogCategory.Audio) ?? false;

            if (_logger?.IsEnabled(LogLevel.Info) == true)
                _logger?.Info(
                $"Audio track subscription started for participant: {participantIdentity}");

            if (isDebugEnabled)
            {
                _logger.Debug("HandleRemoteAudioTrackSubscribed called:");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - Participant SID: {participantSid}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - Participant Identity: {participantIdentity}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug(
                    $"  - AudioTrack: {(audioTrack != null ? $"valid (Name: {audioTrack.Name}, Sid: {audioTrack.Sid})" : "NULL")}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - Room reference: {(RoomFacade != null ? "valid" : "NULL")}");
            }

            if (!_allowNullAudioTrackInFactory && audioTrack == null)
            {
                _logger?.Warning(
                    "Remote audio track is null and no custom factory provided. ABORTING.");
                return;
            }

            RetainParticipantAudioSubscription(audioTrack, participantSid, participantIdentity);
            if (TryRouteBoundParticipantAudio(audioTrack, participantSid, participantIdentity)) return;

            if (isDebugEnabled)
            {
                IReadOnlyList<IConvaiCharacterAgent> allCharacters = _agentRegistry.Characters;
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug(
                    $"Character Registry state: {allCharacters.Count} Characters registered");
                foreach (IConvaiCharacterAgent character in allCharacters)
                {
                    AudioSource charAudioSource = _audioSourceResolver(character.CharacterId);
                    _agentRegistry.TryGetParticipantId(character.CharacterId, out string participantIdStr);
                    bool isMuted = _agentRegistry.IsCharacterMuted(character.CharacterId);
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.Debug(
                        $"  - CharacterId: {character.CharacterId}, ParticipantId: '{participantIdStr}', HasAudioSource: {charAudioSource != null}, IsMuted: {isMuted}");
                }
            }

            if (!TryResolveCharacter(participantSid, participantIdentity, out IConvaiCharacterAgent agent))
            {
                if (_logger?.IsEnabled(LogLevel.Error) == true)
                    _logger?.Error(
                    $"FAILED to resolve Character for incoming audio track. SID: {participantSid}, Identity: {participantIdentity}. Audio will NOT play!");
                return;
            }

            string characterId = agent.CharacterId;
            if (_logger?.IsEnabled(LogLevel.Info) == true)
                _logger?.Info(
                $"Successfully resolved Character: {characterId}");

            AudioSource targetSource = ResolveCharacterAudioSource(agent, characterId);
            if (targetSource == null)
            {
                if (_logger?.IsEnabled(LogLevel.Error) == true)
                    _logger?.Error(
                    $"Character '{characterId}' does not have an AudioSource assigned. Audio will NOT play!");
                return;
            }

            if (isDebugEnabled)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"Found AudioSource for Character '{characterId}':");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.enabled: {targetSource.enabled}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.volume: {targetSource.volume}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.mute: {targetSource.mute}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.isPlaying: {targetSource.isPlaying}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug(
                    $"Configuring AudioSource for Character '{characterId}'...");
            }

            bool isMutedState = _agentRegistry.IsCharacterMuted(characterId);
            ApplyAudioMuteState(targetSource, isMutedState);

            if (isDebugEnabled)
            {
                _logger.Debug("AudioSource configured:");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.volume: {targetSource.volume}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.mute: {targetSource.mute}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.playOnAwake: {targetSource.playOnAwake}");
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.Debug($"  - AudioSource.spatialBlend: {targetSource.spatialBlend}");
            }

            _agentRegistry.TryGetParticipantId(characterId, out string currentParticipantId);
            if (!string.IsNullOrEmpty(participantSid) && currentParticipantId != participantSid)
            {
                if (isDebugEnabled)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.Debug(
                        $"Updating ParticipantId from '{currentParticipantId}' to '{participantSid}'");
                }

                _agentRegistry.SetParticipantId(characterId, participantSid);
            }

            bool membershipRouted = _agentRegistry is IMultiCharacterSessionRegistry multiRegistry &&
                                    multiRegistry.CurrentMultiCharacterSession?.FindByCharacter(agent) != null;
            if (!membershipRouted &&
                TryGetRegistrationByCharacter(characterId, out RemoteAudioPlaybackRegistration existingRegistration))
            {
                if (isDebugEnabled)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.Debug(
                        $"Disposing existing audio stream for Character '{characterId}'");
                }

                RemoveRegistration(existingRegistration, stopOutput: false);
            }

            if (isDebugEnabled) _logger.Debug("Creating AudioStream with factory...");

            // Use the platform-agnostic audio stream factory
            IDisposable stream = _audioStreamFactory?.Create(audioTrack, targetSource);
            if (stream != null)
            {
                if (!RegisterCharacterAudioPlayback(
                        audioTrack,
                        participantSid,
                        characterId,
                        targetSource,
                        stream))
                    return;
            }
            else
            {
                if (_logger?.IsEnabled(LogLevel.Error) == true)
                    _logger?.Error(
                    $"FAILED! AudioStream factory returned null for Character '{characterId}'. Audio will NOT play!");
            }

            if (_logger?.IsEnabled(LogLevel.Info) == true)
                _logger?.Info(
                $"Audio track subscription completed for participant: {participantIdentity}");

            // Fire abstraction-based event
            if (audioTrack?.Participant != null)
            {
                OnAudioTrackSubscribed?.Invoke(audioTrack, audioTrack.Participant);
            }
        }

        /// <summary>
        ///     Finds where this character's voice should play.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Both registry lookups depend on <c>ConvaiAudioOutput</c> having registered, and
        ///         that is a component's <c>OnEnable</c> — which Unity may run after
        ///         <c>ConvaiCharacter</c>'s. A character re-enabled during play re-subscribes its
        ///         audio track from inside <c>ConvaiCharacter.OnEnable</c>, so the track can arrive
        ///         while the registration for it has not been made yet: measured, and the character
        ///         answered in text and stayed silent.
        ///     </para>
        ///     <para>
        ///         The last resort asks the character itself, which cannot be out of order because it
        ///         is not a registration at all. Audio routing should not depend on component order,
        ///         and now does not.
        ///     </para>
        /// </remarks>
        private AudioSource ResolveCharacterAudioSource(IConvaiCharacterAgent character, string characterId)
        {
            if (_agentRegistry is ICharacterInstanceAudioRegistry instanceRegistry &&
                instanceRegistry.TryGetAudioSource(character, out AudioSource instanceSource))
                return instanceSource;

            AudioSource registered = _audioSourceResolver(characterId);
            if (registered != null) return registered;

            return character is Component component ? component.GetComponent<AudioSource>() : null;
        }

        private bool TryRouteBoundParticipantAudio(
            IRemoteAudioTrack audioTrack,
            string participantSid,
            string participantIdentity)
        {
            AudioSource source;
            lock (_syncRoot)
            {
                if (!_participantAudioOutputs.TryGetValue(participantIdentity ?? string.Empty, out source) || source == null)
                    return false;
            }

            string streamKey = string.IsNullOrWhiteSpace(participantSid)
                ? participantIdentity
                : participantSid;
            IDisposable stream = _audioStreamFactory?.Create(audioTrack, source);
            if (stream == null) return true;

            if (TryResolveExplicitlyBoundCharacter(
                    participantSid,
                    participantIdentity,
                    out IConvaiCharacterAgent character))
            {
                string characterId = character.CharacterId;
                ApplyAudioMuteState(source, _agentRegistry.IsCharacterMuted(characterId));
                if (!string.IsNullOrWhiteSpace(participantSid))
                    _agentRegistry.SetParticipantId(characterId, participantSid);
                RegisterCharacterAudioPlayback(
                    audioTrack,
                    participantSid,
                    characterId,
                    source,
                    stream);
                if (_logger?.IsEnabled(LogLevel.Info) == true)
                    _logger?.Info(
                    $"Routed bound Character audio: character='{characterId}', identity='{participantIdentity}', sid='{participantSid}'.");
                return true;
            }

            lock (_syncRoot)
            {
                if (_participantAudioStreams.TryGetValue(streamKey, out IDisposable existing)) existing.Dispose();
                _participantAudioStreams[streamKey] = stream;
            }
            if (_logger?.IsEnabled(LogLevel.Info) == true)
                _logger?.Info($"Routed participant audio: identity='{participantIdentity}', sid='{participantSid}'.");
            return true;
        }

        private bool TryResolveExplicitlyBoundCharacter(
            string participantSid,
            string participantIdentity,
            out IConvaiCharacterAgent agent)
        {
            if (_agentRegistry is IMultiCharacterSessionRegistry multiRegistry &&
                multiRegistry.CurrentMultiCharacterSession is { } session)
            {
                CharacterRoomMembership membership = session.Resolve(
                    null,
                    participantIdentity,
                    participantSid);
                if (membership?.Character != null)
                {
                    agent = membership.Character;
                    if (!string.IsNullOrWhiteSpace(participantSid))
                        session.BindParticipant(membership, participantSid);
                    return true;
                }

                agent = null;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(participantIdentity) &&
                _agentRegistry.TryGetCharacter(participantIdentity, out agent))
                return true;
            if (!string.IsNullOrWhiteSpace(participantSid) &&
                _agentRegistry.TryGetCharacterByParticipantId(participantSid, out agent))
                return true;

            agent = null;
            return false;
        }

        private bool RegisterCharacterAudioPlayback(
            IRemoteAudioTrack audioTrack,
            string participantSid,
            string characterId,
            AudioSource targetSource,
            IDisposable stream)
        {
            if (stream is not IAudioPlaybackStateSource)
            {
                if (_logger?.IsEnabled(LogLevel.Warning) == true)
                    _logger?.Warning(
                    $"Audio stream does not expose playback-state source: character='{characterId}', streamType='{stream.GetType().Name}'.");
            }

            string registrationSid = !string.IsNullOrEmpty(participantSid)
                ? participantSid
                : audioTrack?.Participant?.Sid;
            if (string.IsNullOrEmpty(registrationSid))
            {
                stream.Dispose();
                if (_logger?.IsEnabled(LogLevel.Error) == true)
                    _logger?.Error(
                    $"Remote audio registration requires participant SID: character='{characterId}'.");
                return false;
            }

            RemoveParticipantAudioStream(registrationSid);
            if (_remoteAudioRegistrations.TryGetValue(
                    registrationSid,
                    out RemoteAudioPlaybackRegistration participantRegistration))
                RemoveRegistration(participantRegistration, stopOutput: false);

            Action startedHandler = () =>
                _eventHub?.Publish(CharacterAudioPlaybackStateChanged.Started(characterId));
            Action stoppedHandler = () =>
                _eventHub?.Publish(CharacterAudioPlaybackStateChanged.Stopped(characterId));
            var registration = new RemoteAudioPlaybackRegistration(
                registrationSid,
                characterId,
                audioTrack,
                audioTrack?.Participant,
                targetSource,
                stream,
                startedHandler,
                stoppedHandler);
            _remoteAudioRegistrations.Add(registrationSid, registration);
            // WebGL invokes PlaybackStarted immediately when an already-playing element gains
            // its first subscriber. Make the timing source discoverable before allowing that
            // callback to reach lip sync so the first response gets the same media clock as
            // all subsequent responses.
            registration.StartPlaybackTracking();
            if (_logger?.IsEnabled(LogLevel.Info) == true)
                _logger?.Info(
                $"AudioStream created successfully for Character '{characterId}'");
            return true;
        }

        private void RetainParticipantAudioSubscription(
            IRemoteAudioTrack audioTrack,
            string participantSid,
            string participantIdentity)
        {
            string streamKey = GetParticipantStreamKey(participantSid, participantIdentity);
            if (string.IsNullOrWhiteSpace(streamKey)) return;

            lock (_syncRoot)
            {
                _participantAudioSubscriptions[streamKey] = new ParticipantAudioSubscription(
                    streamKey,
                    audioTrack,
                    participantSid,
                    participantIdentity);
            }
        }

        private static string GetParticipantStreamKey(string participantSid, string participantIdentity) =>
            string.IsNullOrWhiteSpace(participantSid) ? participantIdentity : participantSid;

        private void RemoveParticipantAudioStream(string streamKey)
        {
            if (string.IsNullOrWhiteSpace(streamKey)) return;
            lock (_syncRoot)
            {
                if (!_participantAudioStreams.TryGetValue(streamKey, out IDisposable stream)) return;
                stream.Dispose();
                _participantAudioStreams.Remove(streamKey);
            }
        }

        private void RemoveParticipantAudioRoute(ParticipantAudioSubscription subscription)
        {
            RemoveParticipantAudioStream(subscription.StreamKey);
            string registrationSid = !string.IsNullOrWhiteSpace(subscription.ParticipantSid)
                ? subscription.ParticipantSid
                : subscription.AudioTrack?.Participant?.Sid;
            if (!string.IsNullOrWhiteSpace(registrationSid) &&
                _remoteAudioRegistrations.TryGetValue(
                    registrationSid,
                    out RemoteAudioPlaybackRegistration registration))
                RemoveRegistration(registration, stopOutput: true);
        }

        /// <summary>
        ///     Handles the event when a remote audio track is unsubscribed for a given participant.
        ///     Disposes of the associated audio stream and stops the audio source if present.
        /// </summary>
        /// <param name="participantSid">The unique identifier of the participant whose audio track was unsubscribed.</param>
        public void HandleRemoteAudioTrackUnsubscribed(string participantSid)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(participantSid)) return;

            lock (_syncRoot)
            {
                _participantAudioSubscriptions.Remove(participantSid);
                if (_participantAudioStreams.TryGetValue(participantSid, out IDisposable participantStream))
                {
                    participantStream.Dispose();
                    _participantAudioStreams.Remove(participantSid);
                    return;
                }
            }

            if (!_remoteAudioRegistrations.TryGetValue(
                    participantSid,
                    out RemoteAudioPlaybackRegistration registration)) return;

            RemoveRegistration(registration, stopOutput: true);
            if (registration.Track != null && registration.Participant != null)
                OnAudioTrackUnsubscribed?.Invoke(registration.Track, registration.Participant);
        }

        /// <summary>
        ///     Disposes and clears all remote audio streams managed by this instance.
        ///     Call this method to release resources associated with remote audio tracks.
        /// </summary>
        public void ClearRemoteAudio()
        {
            foreach (KeyValuePair<string, RemoteAudioPlaybackRegistration> entry in _remoteAudioRegistrations)
                entry.Value.Dispose();

            _remoteAudioRegistrations.Clear();
            lock (_syncRoot)
            {
                foreach (IDisposable stream in _participantAudioStreams.Values) stream.Dispose();
                _participantAudioStreams.Clear();
                _participantAudioSubscriptions.Clear();
            }
            _unresolvedParticipantRouteLogged = false;
        }

        private readonly struct ParticipantAudioSubscription
        {
            public ParticipantAudioSubscription(
                string streamKey,
                IRemoteAudioTrack audioTrack,
                string participantSid,
                string participantIdentity)
            {
                StreamKey = streamKey;
                AudioTrack = audioTrack;
                ParticipantSid = participantSid ?? string.Empty;
                ParticipantIdentity = participantIdentity ?? string.Empty;
            }

            public string StreamKey { get; }
            public IRemoteAudioTrack AudioTrack { get; }
            public string ParticipantSid { get; }
            public string ParticipantIdentity { get; }
        }

        #endregion
    }
}
