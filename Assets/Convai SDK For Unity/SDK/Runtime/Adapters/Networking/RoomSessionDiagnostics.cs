using System;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Services;
using Convai.Infrastructure.Networking.Transport;

namespace Convai.Runtime.Adapters.Networking
{
    internal sealed class RoomSessionDiagnostics
    {
        private readonly IEventHub _eventHub;
        private readonly ILogger _logger;
        private readonly Action _onConnected;
        private readonly Action<SessionState> _onConnectionStateChanged;
        private readonly Action<RoomOwnershipRebindStateChanged> _onOwnershipRebindStateChanged;
        private readonly Action<SessionError> _onSessionError;
        private readonly Action<SessionStateChanged> _onSessionStateChanged;
        private readonly Func<string> _sessionIdProvider;
        private readonly ISessionService _sessionService;
        private readonly ISessionStateMachine _sessionStateMachine;

        public RoomSessionDiagnostics(
            ISessionStateMachine sessionStateMachine,
            ISessionService sessionService,
            IEventHub eventHub,
            ILogger logger,
            Func<string> sessionIdProvider,
            Action onConnected = null,
            Action<SessionError> onSessionError = null,
            Action<SessionStateChanged> onSessionStateChanged = null,
            Action<SessionState> onConnectionStateChanged = null,
            Action<RoomOwnershipRebindStateChanged> onOwnershipRebindStateChanged = null)
        {
            _sessionStateMachine = sessionStateMachine;
            _sessionService = sessionService;
            _eventHub = eventHub;
            _logger = logger.WithTag("ConvaiRoomManager", LogCategory.Transport);
            _sessionIdProvider = sessionIdProvider ?? throw new ArgumentNullException(nameof(sessionIdProvider));
            _onConnected = onConnected;
            _onSessionError = onSessionError;
            _onSessionStateChanged = onSessionStateChanged;
            _onConnectionStateChanged = onConnectionStateChanged;
            _onOwnershipRebindStateChanged = onOwnershipRebindStateChanged;
        }

        public void UpdateSessionState(SessionState newState, SessionError? error = null)
        {
            if (_sessionStateMachine == null)
            {
                _logger?.Warning("Session state machine not initialized; skipping state update.");
                return;
            }

            string sessionId = _sessionIdProvider();
            if (_sessionStateMachine.TryTransition(newState, sessionId, error))
                return;

            if (_logger?.IsEnabled(LogLevel.Warning) == true)
                _logger?.Warning($"Invalid transition to {newState}; forcing transition.");
            _sessionStateMachine.ForceTransition(newState, sessionId, error);
        }

        public ConnectionContext RecordConnectionSuccess(
            string roomName,
            string characterSessionId,
            string sessionId,
            string characterId,
            bool enableSessionResume)
        {
            var connectionContext = new ConnectionContext(
                roomName,
                characterSessionId,
                sessionId,
                characterId,
                DateTime.UtcNow);

            if (_sessionService != null)
            {
                _sessionService.SetActiveSession(characterId, sessionId);

                if (enableSessionResume && !string.IsNullOrEmpty(characterSessionId))
                    _sessionService.StoreSession(characterId, characterSessionId);
            }

            _onConnected?.Invoke();
            return connectionContext;
        }

        public SessionError RecordConnectionFailure(ConnectionFailure failure)
        {
            var sessionError = failure.ToSessionError(_sessionIdProvider());
            UpdateSessionState(SessionState.Error, sessionError);
            _eventHub?.Publish(sessionError);
            _onSessionError?.Invoke(sessionError);
            return sessionError;
        }

        public RoomOwnershipRebindStateChanged PublishOwnershipRebindState(
            RoomOwnershipRebindStatus outcome,
            bool hasPendingReconnect,
            SessionState sessionState,
            string activeCharacterId,
            string requestedCharacterId)
        {
            var stateChanged = RoomOwnershipRebindStateChanged.Create(
                outcome,
                hasPendingReconnect,
                sessionState,
                activeCharacterId,
                requestedCharacterId);
            _eventHub?.Publish(stateChanged);
            _onOwnershipRebindStateChanged?.Invoke(stateChanged);
            return stateChanged;
        }

        /// <summary>
        ///     Publishes a live roster edit so a game can react to it without polling the room.
        /// </summary>
        /// <remarks>
        ///     Refusals go out on the same event as successes. A character that silently never joins
        ///     is indistinguishable from a scene fault, and the Console line saying why is not
        ///     something a build can act on.
        /// </remarks>
        public RoomRosterChanged PublishRosterChanged(
            RoomRosterChange change,
            string membershipId,
            string characterId,
            string characterName,
            int rosterSize,
            string reason = null)
        {
            var rosterChanged = RoomRosterChanged.Create(
                change,
                membershipId,
                characterId,
                characterName,
                rosterSize,
                reason);
            _eventHub?.Publish(rosterChanged);
            return rosterChanged;
        }

        /// <summary>
        ///     Settles the connection context for a session that has ended. A client-initiated
        ///     disconnect drops the room so the next connect starts fresh instead of rejoining the
        ///     previous agent; any other reason keeps it for a rejoin within the TTL. Unexpected
        ///     drops carry no reason from the transport, so they all arrive here as
        ///     <see cref="DisconnectReason.TransportError" />.
        /// </summary>
        public ConnectionContext CompleteDisconnectionTracking(
            ConnectionContext connectionContext,
            DisconnectReason reason,
            bool updateSessionState,
            string completionMessage)
        {
            if (updateSessionState)
                UpdateSessionState(SessionState.Disconnected);

            ConnectionContext updatedContext = connectionContext ?? ConnectionContext.Empty;
            if (updatedContext.HasValidRoom)
            {
                updatedContext = reason == DisconnectReason.ClientInitiated
                    ? updatedContext.WithoutRoom()
                    : updatedContext.WithDisconnection(DateTime.UtcNow);
            }

            _sessionService?.ClearActiveSession();
            if (_logger?.IsEnabled(LogLevel.Info) == true)
                _logger?.Info($"{completionMessage}");
            return updatedContext;
        }

        public void HandleStateChanged(SessionStateChanged stateChanged)
        {
            _onSessionStateChanged?.Invoke(stateChanged);
            _onConnectionStateChanged?.Invoke(stateChanged.NewState);
        }
    }
}
