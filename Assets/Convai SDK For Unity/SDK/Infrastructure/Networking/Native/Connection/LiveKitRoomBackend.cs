using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking.Native;
using Convai.Infrastructure.Networking.Transport;
using LiveKit;
using DataPacketKind = LiveKit.Proto.DataPacketKind;
// Type alias to disambiguate LiveKit track types from abstraction interfaces
using LKRemoteTrack = LiveKit.IRemoteTrack;

namespace Convai.Infrastructure.Networking.Connection
{
    /// <summary>
    ///     LiveKit-backed native room backend.
    ///     Encapsulates the LiveKit <see cref="Room" /> instance and exposes
    ///     backend events consumed by the native transport layer.
    /// </summary>
    /// <remarks>
    ///     Uses IConnectionStateMachine for validated state transitions.
    ///     Thread-safe for state access and modification.
    ///     All public events are marshalled to the main thread for safe Unity API access.
    /// </remarks>
    internal sealed class LiveKitRoomBackend : IDisposable
    {
        private readonly RoomOptions _defaultOptions;
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly ILogger _logger;

        private bool _disposed;

        /// <summary>
        ///     Creates a new LiveKitRoomBackend with a default ConnectionStateMachine.
        /// </summary>
        /// <param name="logger">Logger for diagnostic messages.</param>
        /// <param name="dispatcher">Main thread dispatcher for event marshalling (optional, but recommended).</param>
        /// <param name="defaultOptions">Default room options (optional).</param>
        public LiveKitRoomBackend(ILogger logger, IMainThreadDispatcher dispatcher = null,
            RoomOptions defaultOptions = null)
            : this(logger, new ConnectionStateMachine(logger), dispatcher, defaultOptions)
        {
        }

        /// <summary>
        ///     Creates a new LiveKitRoomBackend with a custom state machine.
        /// </summary>
        /// <param name="logger">Logger for diagnostic messages.</param>
        /// <param name="stateMachine">State machine for connection state management.</param>
        /// <param name="dispatcher">Main thread dispatcher for event marshalling (optional, but recommended).</param>
        /// <param name="defaultOptions">Default room options (optional).</param>
        public LiveKitRoomBackend(
            ILogger logger,
            IConnectionStateMachine stateMachine,
            IMainThreadDispatcher dispatcher = null,
            RoomOptions defaultOptions = null)
        {
            _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).WithTag(nameof(LiveKitRoomBackend), LogCategory.Transport);
            StateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _dispatcher = dispatcher;
            _defaultOptions = defaultOptions ?? new RoomOptions
            {
                AutoSubscribe = true, Dynacast = true, AdaptiveStream = true
            };

            Room = new Room();
            SubscribeRoomEvents();

            StateMachine.StateChanged += OnStateMachineStateChanged;
        }

        public Room Room { get; private set; }

        public IConnectionStateMachine StateMachine { get; }

        public ConnectionState State => StateMachine.CurrentState;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                if (Room != null && Room.IsConnected)
                {
                    _logger.Debug("Disconnecting Room in Dispose()");
                    Room.Disconnect();
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.Warning($"Exception disconnecting Room on dispose: {ex.Message}");
            }

            UnsubscribeRoomEvents();

            if (StateMachine != null) StateMachine.StateChanged -= OnStateMachineStateChanged;

            _disposed = true;
        }

        /// <summary>
        ///     Raised when the connection state changes.
        /// </summary>
        public event Action<ConnectionState> StateChanged;

        /// <summary>
        ///     Raised when a data packet is received.
        /// </summary>
        public event Action<DataPacket> DataPacketReceived;

        /// <summary>
        ///     Connects the backend to a LiveKit room.
        /// </summary>
        public async Task<LiveKitConnectResult> ConnectAsync(string url, string token,
            RoomOptions options = null, CancellationToken cancellationToken = default)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));

            if (token == null) throw new ArgumentNullException(nameof(token));

            ThrowIfDisposed();

            ResetRoom();

            if (!StateMachine.TryTransition(ConnectionState.Connecting))
            {
                _logger.Warning("Cannot transition to Connecting state");
                return new LiveKitConnectResult(false, "Invalid state transition");
            }

            options ??= _defaultOptions;

            try
            {
                string endpoint = Uri.TryCreate(url, UriKind.Absolute, out Uri parsedUrl)
                    ? parsedUrl.GetLeftPart(UriPartial.Authority)
                    : "<invalid LiveKit URL>";
                if (_logger.IsEnabled(LogLevel.Info))
                    _logger.Info($"Connecting to LiveKit endpoint: {endpoint}");
                ConnectInstruction connect = Room.Connect(url, token, options);
                await connect.AsTask(cancellationToken);

                if (connect.IsError)
                {
                    string message = connect.ToString();
                    if (_logger.IsEnabled(LogLevel.Error))
                        _logger.Error($"Connect failed: {message}");
                    StateMachine.TryTransition(ConnectionState.Disconnected, message);
                    return new LiveKitConnectResult(false, message);
                }

                if (StateMachine.CurrentState != ConnectionState.Connected)
                {
                    if (!StateMachine.TryTransition(ConnectionState.Connected))
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.Warning(
                            $"ConnectAsync fallback transition failed from {StateMachine.CurrentState}; forcing Connected state.");
                        StateMachine.ForceTransition(ConnectionState.Connected);
                    }
                }

                return new LiveKitConnectResult(true, roomName: Room?.Name, sessionId: Room?.Sid);
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("Connect cancelled.");
                StateMachine.TryTransition(ConnectionState.Disconnected, "Cancelled");
                return new LiveKitConnectResult(false, "Cancelled");
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.Error($"Unexpected connect exception: {ex.Message}");
                StateMachine.TryTransition(ConnectionState.Disconnected, ex.Message);
                return new LiveKitConnectResult(false, ex.Message);
            }
        }

        /// <summary>
        ///     Leaves the room and moves the state machine to <see cref="ConnectionState.Disconnected" />.
        /// </summary>
        /// <remarks>
        ///     Completes when <see cref="Room.Disconnect" /> returns. That call sends the FFI disconnect request
        ///     and then releases the room, which unsubscribes it from FFI room events, so a disconnect the
        ///     client starts never raises <see cref="Room.Disconnected" />. Disconnects the room reports itself
        ///     (server, network, FFI panic) still arrive through <see cref="HandleDisconnected" />.
        ///     <paramref name="cancellationToken" /> is not observed: once the request is sent there is nothing
        ///     left to cancel.
        /// </remarks>
        public Task DisconnectAsync(DisconnectReason reason = DisconnectReason.ClientInitiated,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (State == ConnectionState.Disconnected)
            {
                _logger.Debug("Already disconnected, skipping.");
                return Task.CompletedTask;
            }

            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info($"Disconnect requested. Reason: {reason}");

            if (State == ConnectionState.Connected)
            {
                if (!StateMachine.TryTransition(ConnectionState.Disconnecting))
                {
                    _logger.Warning(
                        "Failed to transition to Disconnecting state, forcing transition.");
                    StateMachine.ForceTransition(ConnectionState.Disconnecting);
                }
            }

            try
            {
                Room.Disconnect();
                _logger.Debug("Disconnect completed; native room released.");
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.Warning($"Disconnect threw: {ex.Message}");
            }

            if (!StateMachine.TryTransition(ConnectionState.Disconnected))
                StateMachine.ForceTransition(ConnectionState.Disconnected);

            return Task.CompletedTask;
        }

        public async Task SendDataAsync(ReadOnlyMemory<byte> payload, bool reliable = true,
            string topic = "", CancellationToken cancellationToken = default, string[] destinationIdentities = null)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (Room.LocalParticipant == null)
                throw new InvalidOperationException("Cannot send data because the room has no local participant.");
            var operation = Room.LocalParticipant.PublishData(payload.ToArray(), destinationIdentities, reliable, topic);
            while (!operation.IsDone)
            {
                ThrowIfDisposed();
                await Task.Delay(10, cancellationToken);
            }
            if (operation.IsError)
                throw new InvalidOperationException("LiveKit data publication failed: " + operation.Error);
        }

        /// <summary>
        ///     Creates a fresh Room instance, unsubscribing from old events and subscribing to new ones.
        ///     This ensures a clean state between connection attempts, preventing duplicate participant issues.
        /// </summary>
        /// <remarks>
        ///     Does not wait for <see cref="Room.Disconnected" /> after disconnecting the old room; see
        ///     <see cref="DisconnectAsync" />.
        /// </remarks>
        private void ResetRoom()
        {
            _logger.Debug("Resetting Room instance for fresh connection.");

            if (Room != null)
            {
                UnsubscribeRoomEvents();

                try
                {
                    if (Room.IsConnected)
                    {
                        _logger.Debug("Disconnecting old Room before disposal.");
                        Room.Disconnect();
                    }

                    (Room as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.Warning($"Exception disposing old Room: {ex.Message}");
                }
            }

            Room = new Room();

            SubscribeRoomEvents();

            StateMachine.Reset();
        }

        /// <summary>Raised when the LiveKit room connects.</summary>
        public event Action<Room> RoomConnected;

        /// <summary>Raised when the LiveKit room disconnects.</summary>
        /// <summary>Raised when the LiveKit room disconnects, with the native reason.</summary>
        public event Action<Room, LiveKit.Proto.DisconnectReason> RoomDisconnected;

        /// <summary>Raised when the LiveKit room begins reconnecting.</summary>
        public event Action<Room> RoomReconnecting;

        /// <summary>Raised when the LiveKit room reconnects successfully.</summary>
        public event Action<Room> RoomReconnected;

        /// <summary>Raised when a participant connects to the room.</summary>
        public event Action<Participant> ParticipantConnected;

        /// <summary>Raised when a participant disconnects from the room.</summary>
        public event Action<Participant> ParticipantDisconnected;

        /// <summary>Raised when a remote track is published by a participant.</summary>
        public event Action<RemoteTrackPublication, RemoteParticipant> TrackPublished;

        /// <summary>Raised when a remote track is subscribed.</summary>
        public event Action<LKRemoteTrack, RemoteTrackPublication, RemoteParticipant> TrackSubscribed;

        /// <summary>Raised when a remote track is unsubscribed.</summary>
        public event Action<LKRemoteTrack, RemoteTrackPublication, RemoteParticipant> TrackUnsubscribed;

        /// <summary>Raised when a track is muted.</summary>
        public event Action<TrackPublication, Participant> TrackMuted;

        /// <summary>Raised when a track is unmuted.</summary>
        public event Action<TrackPublication, Participant> TrackUnmuted;

        /// <summary>Raised when the active speakers list changes.</summary>
        public event Action<List<Participant>> ActiveSpeakersChanged;

        private void SubscribeRoomEvents()
        {
            Room.DataReceived += HandleDataReceived;
            Room.DisconnectedWithReason += HandleDisconnected;
            Room.Connected += HandleConnected;
            Room.Reconnecting += HandleReconnecting;
            Room.Reconnected += HandleReconnected;
            Room.ParticipantConnected += HandleParticipantConnected;
            Room.ParticipantDisconnected += HandleParticipantDisconnected;
            Room.TrackPublished += HandleTrackPublished;
            Room.TrackSubscribed += HandleTrackSubscribed;
            Room.TrackUnsubscribed += HandleTrackUnsubscribed;
            Room.TrackMuted += HandleTrackMuted;
            Room.TrackUnmuted += HandleTrackUnmuted;
            Room.ActiveSpeakersChanged += HandleActiveSpeakersChanged;
        }

        private void UnsubscribeRoomEvents()
        {
            Room.DataReceived -= HandleDataReceived;
            Room.DisconnectedWithReason -= HandleDisconnected;
            Room.Connected -= HandleConnected;
            Room.Reconnecting -= HandleReconnecting;
            Room.Reconnected -= HandleReconnected;
            Room.ParticipantConnected -= HandleParticipantConnected;
            Room.ParticipantDisconnected -= HandleParticipantDisconnected;
            Room.TrackPublished -= HandleTrackPublished;
            Room.TrackSubscribed -= HandleTrackSubscribed;
            Room.TrackUnsubscribed -= HandleTrackUnsubscribed;
            Room.TrackMuted -= HandleTrackMuted;
            Room.TrackUnmuted -= HandleTrackUnmuted;
            Room.ActiveSpeakersChanged -= HandleActiveSpeakersChanged;
        }

        private void HandleConnected(Room room)
        {
            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info($"Room connected: {room?.Name ?? "unknown"}");

            ConnectionState currentState = StateMachine.CurrentState;
            if (currentState != ConnectionState.Connected && !StateMachine.TryTransition(ConnectionState.Connected))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.Warning(
                    $"HandleConnected failed to transition state from {currentState} to Connected");
            }

            DispatchToMainThread(() => RoomConnected?.Invoke(room));
        }

        private void HandleDisconnected(Room room, LiveKit.Proto.DisconnectReason reason)
        {
            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info($"Room disconnected: {room?.Name ?? "unknown"} ({reason})");

            if (!StateMachine.TryTransition(ConnectionState.Disconnected))
                StateMachine.ForceTransition(ConnectionState.Disconnected, $"Unexpected disconnect: {reason}");

            DispatchToMainThread(() => RoomDisconnected?.Invoke(room, reason));
        }

        private void HandleReconnecting(Room room)
        {
            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info($"Room reconnecting: {room?.Name ?? "unknown"}");
            StateMachine.TryTransition(ConnectionState.Reconnecting);
            DispatchToMainThread(() => RoomReconnecting?.Invoke(room));
        }

        private void HandleReconnected(Room room)
        {
            if (_logger.IsEnabled(LogLevel.Info))
                _logger.Info($"Room reconnected: {room?.Name ?? "unknown"}");

            ConnectionState currentState = StateMachine.CurrentState;
            if (currentState != ConnectionState.Connected && !StateMachine.TryTransition(ConnectionState.Connected))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.Warning(
                    $"HandleReconnected failed to transition state from {currentState} to Connected");
            }

            DispatchToMainThread(() => RoomReconnected?.Invoke(room));
        }

        private void HandleParticipantConnected(Participant participant) =>
            DispatchToMainThread(() => ParticipantConnected?.Invoke(participant));

        private void HandleParticipantDisconnected(Participant participant) =>
            DispatchToMainThread(() => ParticipantDisconnected?.Invoke(participant));

        private void HandleTrackPublished(RemoteTrackPublication publication, RemoteParticipant participant) =>
            DispatchToMainThread(() => TrackPublished?.Invoke(publication, participant));

        private void HandleTrackSubscribed(LKRemoteTrack track, RemoteTrackPublication publication,
            RemoteParticipant participant) =>
            DispatchToMainThread(() => TrackSubscribed?.Invoke(track, publication, participant));

        private void HandleTrackUnsubscribed(LKRemoteTrack track, RemoteTrackPublication publication,
            RemoteParticipant participant) =>
            DispatchToMainThread(() => TrackUnsubscribed?.Invoke(track, publication, participant));

        private void HandleTrackMuted(TrackPublication publication, Participant participant) =>
            DispatchToMainThread(() => TrackMuted?.Invoke(publication, participant));

        private void HandleTrackUnmuted(TrackPublication publication, Participant participant) =>
            DispatchToMainThread(() => TrackUnmuted?.Invoke(publication, participant));

        private void HandleActiveSpeakersChanged(List<Participant> speakers) =>
            DispatchToMainThread(() => ActiveSpeakersChanged?.Invoke(speakers));

        private void HandleDataReceived(byte[] data, Participant participant, DataPacketKind kind,
            string topic)
        {
            if (data == null) return;

            string participantId = ResolveDataPacketParticipantId(
                participant?.Sid,
                participant?.Identity);
            if (participant == null)
            {
                _logger.Warning(
                    "Received data packet but participant lookup returned null. Participant ID unavailable.");
            }

            DataPacket packet = new(
                data,
                topic ?? string.Empty,
                kind == DataPacketKind.KindReliable
                    ? Transport.DataPacketKind.Reliable
                    : Transport.DataPacketKind.Lossy,
                participantId);

            DispatchToMainThread(() => DataPacketReceived?.Invoke(packet));
        }

        internal static string ResolveDataPacketParticipantId(string participantSid, string participantIdentity) =>
            !string.IsNullOrEmpty(participantSid)
                ? participantSid
                : participantIdentity ?? string.Empty;

        private void
            OnStateMachineStateChanged(ConnectionState oldState, ConnectionState newState, string errorMessage) =>
            DispatchToMainThread(() => StateChanged?.Invoke(newState));

        /// <summary>
        ///     Dispatches an action to the main thread if a dispatcher is available.
        ///     If no dispatcher is configured, executes the action immediately (with a warning logged once).
        /// </summary>
        private void DispatchToMainThread(Action action)
        {
            if (action == null) return;

            if (_dispatcher != null)
            {
                if (!_dispatcher.TryDispatch(action))
                    _logger.Warning("Failed to dispatch action to main thread.");
            }
            else
                action();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LiveKitRoomBackend));
        }
    }

    /// <summary>Result of a LiveKit backend connection attempt.</summary>
    public readonly struct LiveKitConnectResult
    {
        public LiveKitConnectResult(bool success, string errorMessage = null, string roomName = null,
            string sessionId = null)
        {
            Success = success;
            ErrorMessage = errorMessage;
            RoomName = roomName;
            SessionId = sessionId;
        }

        public bool Success { get; }
        public string ErrorMessage { get; }
        public string RoomName { get; }
        public string SessionId { get; }
    }
}
