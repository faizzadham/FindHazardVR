using LogLevel = Convai.Domain.Logging.LogLevel;
using System;
using System.Collections.Generic;
using System.Threading;
using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Room;
using Convai.Runtime.Utilities;
using UnityEngine;

namespace Convai.Runtime.Adapters.Networking
{
    public partial class ConvaiRoomManager
    {
        private readonly ConversationRoutingMicrophoneGate _conversationRoutingMicrophoneGate = new();
        private readonly ConversationTargetCommandTracker _conversationTargetCommandTracker = new();
        private readonly List<CanonicalConversationTargetNotification> _deferredCanonicalTargetNotifications = new();
        private readonly Dictionary<int, int> _targetRoutingMicrophoneGenerations = new();
        private MultiCharacterRoomSession _observedConversationTargetSession;

        internal event Action<MultiCharacterRoomSession, CharacterRoomMembership, CharacterRoomMembership>
            CanonicalConversationTargetReconciled;

        internal bool IsConversationTargetRoutingInFlight => _conversationTargetCommandTracker.IsInFlight;

        internal ConvaiCharacter PendingConversationTarget => _conversationTargetCommandTracker.Pending;

        public void SetMicMuted(bool mute)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
                _logger?.Debug($"SetMicMuted called: mute={mute}");
            _conversationRoutingMicrophoneGate.SetUserMuted(mute, ApplyEffectiveMicMute);
        }

        /// <summary>
        ///     Closes every player-input route for one canonical routing command. This lives below
        ///     the public room APIs so direct set/clear calls and roster handovers cannot bypass it.
        /// </summary>
        private ConversationTargetRoutingLease BeginConversationTargetRouting(
            ConvaiCharacter pendingTarget,
            bool isTargetCommand)
        {
            ObserveConversationTargetSession(CurrentMultiCharacterSession);
            // A transport send can still fail, so preserve the current character-turn guard until
            // an authoritative response actually moves the route. Capture itself still closes now.
            PreparePushToTalkControllersForConversationTargetRouting(
                preservesCurrentTurnBoundary: true);
            int targetGeneration = _conversationTargetCommandTracker.Begin(pendingTarget);
            int microphoneGeneration;
            try
            {
                microphoneGeneration = _conversationRoutingMicrophoneGate.Begin(
                    shouldSuppress: true,
                    IsMicMuted,
                    ApplyEffectiveMicMute);
            }
            catch
            {
                _conversationTargetCommandTracker.Complete(targetGeneration);
                throw;
            }
            if (isTargetCommand)
                _targetRoutingMicrophoneGenerations[targetGeneration] = microphoneGeneration;
            return new ConversationTargetRoutingLease(() =>
            {
                CompleteConversationTargetRouting(targetGeneration, microphoneGeneration);
            }, () =>
            {
                _deferredCanonicalTargetNotifications.Clear();
                _targetRoutingMicrophoneGenerations.Clear();
                _conversationTargetCommandTracker.Reset();
                _conversationRoutingMicrophoneGate.AbortForConnectionRecovery(ApplyEffectiveMicMute);
            }, () => CompleteAuthoritativeTargetRoutingThrough(targetGeneration));
        }

        private void CompleteConversationTargetRouting(int targetGeneration, int microphoneGeneration)
        {
            _targetRoutingMicrophoneGenerations.Remove(targetGeneration);
            _conversationTargetCommandTracker.Complete(targetGeneration);
            _conversationRoutingMicrophoneGate.Complete(
                microphoneGeneration,
                ApplyEffectiveMicMute);
            FlushCanonicalConversationTargetNotifications();
        }

        /// <summary>
        ///     A target response is a complete authoritative route, so it retires older target
        ///     commands that can no longer apply. Roster leases are deliberately absent from this
        ///     map because their command-correlated add/remove deltas are not superseded by a route.
        /// </summary>
        private void CompleteAuthoritativeTargetRoutingThrough(int targetGeneration)
        {
            var completed = new List<KeyValuePair<int, int>>();
            foreach (KeyValuePair<int, int> entry in _targetRoutingMicrophoneGenerations)
                if (entry.Key <= targetGeneration)
                    completed.Add(entry);

            for (int i = 0; i < completed.Count; i++)
            {
                KeyValuePair<int, int> entry = completed[i];
                _targetRoutingMicrophoneGenerations.Remove(entry.Key);
                _conversationTargetCommandTracker.Complete(entry.Key);
                _conversationRoutingMicrophoneGate.Complete(entry.Value, ApplyEffectiveMicMute);
            }
            FlushCanonicalConversationTargetNotifications();
        }

        private void ResetConversationTargetRoutingState()
        {
            ObserveConversationTargetSession(null);
            _deferredCanonicalTargetNotifications.Clear();
            _targetRoutingMicrophoneGenerations.Clear();
            _conversationTargetCommandTracker.Reset();
            _conversationRoutingMicrophoneGate.ResetForConnectionBoundary();
        }

        /// <summary>
        ///     Bridges the session's one canonical route stream to high-level manager observers.
        ///     Explicit target commands and roster handovers therefore cannot drift into separate
        ///     event contracts or publish the same acknowledgement twice.
        /// </summary>
        private void ObserveConversationTargetSession(MultiCharacterRoomSession session)
        {
            if (ReferenceEquals(_observedConversationTargetSession, session)) return;

            if (_observedConversationTargetSession != null)
                _observedConversationTargetSession.InteractionTargetChanged -=
                    HandleCanonicalConversationTargetReconciled;
            _observedConversationTargetSession = session;
            if (_observedConversationTargetSession != null)
                _observedConversationTargetSession.InteractionTargetChanged +=
                    HandleCanonicalConversationTargetReconciled;
        }

        private void HandleCanonicalConversationTargetReconciled(
            CharacterRoomMembership previous,
            CharacterRoomMembership current)
        {
            MultiCharacterRoomSession session = _observedConversationTargetSession;
            if (!ReferenceEquals(CurrentMultiCharacterSession, session)) return;
            if (!ReferenceEquals(previous, current))
                CommitPushToTalkControllersForConversationTargetChange();
            if (session.IsApplyingTargetCommandResponse)
                return;
            if (_conversationTargetCommandTracker.IsInFlight)
            {
                _deferredCanonicalTargetNotifications.Add(
                    new CanonicalConversationTargetNotification(session, previous, current));
                return;
            }

            CanonicalConversationTargetReconciled?.Invoke(session, previous, current);
        }

        /// <summary>
        ///     Publishes one terminal confirmation per target command after its routing lease has
        ///     released. Session transition batching remains free to coalesce roster/lifecycle
        ///     moves, while two Requested commands still receive two terminal outcomes.
        /// </summary>
        internal async System.Threading.Tasks.Task<InteractionTargetResult> ObserveCanonicalTargetCommandAsync(
            MultiCharacterRoomSession session,
            System.Threading.Tasks.Task<InteractionTargetResult> canonicalOutcome)
        {
            try
            {
                InteractionTargetResult result = await canonicalOutcome;
                if (!ReferenceEquals(CurrentMultiCharacterSession, session)) return result;

                CharacterRoomMembership canonical = session.FindByMembershipId(session.ActiveMembershipId);
                CharacterRoomMembership previous =
                    string.Equals(
                        result.ActiveMembershipId,
                        session.ActiveMembershipId,
                        StringComparison.Ordinal)
                        ? session.FindByMembershipId(result.PreviousMembershipId)
                        : canonical;
                CanonicalConversationTargetReconciled?.Invoke(session, previous, canonical);
                return result;
            }
            catch (InteractionTargetCommandException exception)
            {
                if (exception.CanonicalChanged &&
                    ReferenceEquals(CurrentMultiCharacterSession, session))
                {
                    CharacterRoomMembership canonical = session.FindByMembershipId(session.ActiveMembershipId);
                    CanonicalConversationTargetReconciled?.Invoke(session, canonical, canonical);
                }
                throw;
            }
            catch
            {
                // Failure phases belong to ConvaiManager's operation observer; direct room APIs
                // intentionally expose their fault through the returned operation instead.
                throw;
            }
        }

        private void FlushCanonicalConversationTargetNotifications()
        {
            if (_conversationTargetCommandTracker.IsInFlight ||
                _deferredCanonicalTargetNotifications.Count == 0)
                return;

            CanonicalConversationTargetNotification[] pending =
                _deferredCanonicalTargetNotifications.ToArray();
            _deferredCanonicalTargetNotifications.Clear();
            MultiCharacterRoomSession currentSession = CurrentMultiCharacterSession;
            CharacterRoomMembership canonical = currentSession?.FindByMembershipId(
                currentSession.ActiveMembershipId);
            CharacterRoomMembership previous = canonical;
            for (int i = 0; i < pending.Length; i++)
            {
                CanonicalConversationTargetNotification notification = pending[i];
                if (!ReferenceEquals(currentSession, notification.Session)) continue;
                previous = notification.Previous;
                break;
            }

            // Several roster/lifecycle transitions can apply before their main-thread continuations
            // run. Emit one coalesced fact for the final canonical route. Explicit target commands
            // use their command task above, preserving one terminal outcome per request.
            if (currentSession != null)
                CanonicalConversationTargetReconciled?.Invoke(currentSession, previous, canonical);
        }

        private readonly struct CanonicalConversationTargetNotification
        {
            internal CanonicalConversationTargetNotification(
                MultiCharacterRoomSession session,
                CharacterRoomMembership previous,
                CharacterRoomMembership current)
            {
                Session = session;
                Previous = previous;
                Current = current;
            }

            internal MultiCharacterRoomSession Session { get; }
            internal CharacterRoomMembership Previous { get; }
            internal CharacterRoomMembership Current { get; }
        }

        private void ApplyEffectiveMicMute(bool muted) => _roomAudioCoordinator?.SetMicMuted(muted);

        public IConvaiOperation<Unit> StartListeningAsync(int microphoneIndex = 0,
            CancellationToken cancellationToken = default) =>
            StartListeningCoreAsync(
                microphoneIndex,
                restoreUserMute: true,
                cancellationToken: cancellationToken);

        private IConvaiOperation<Unit> StartListeningCoreAsync(
            int microphoneIndex,
            bool restoreUserMute,
            CancellationToken cancellationToken = default)
        {
            if (restoreUserMute && CurrentState == SessionState.Connected)
                _conversationRoutingMicrophoneGate.RestoreUserMuteAfterConnectionBoundary(
                    ApplyEffectiveMicMute);
            return _roomAudioCoordinator?.StartListeningAsync(microphoneIndex, cancellationToken) ??
                   ConvaiOperation<Unit>.Succeeded(Unit.Value);
        }

        public IConvaiOperation<Unit> StopListeningAsync(CancellationToken cancellationToken = default) =>
            _roomAudioCoordinator?.StopListeningAsync(cancellationToken) ??
            ConvaiOperation<Unit>.Succeeded(Unit.Value);

        public bool SetCharacterMuted(string characterId, bool muted) =>
            !string.IsNullOrEmpty(characterId) &&
            (_roomAudioCoordinator?.SetCharacterMuted(characterId, muted) ?? false);

        public bool IsCharacterMuted(string characterId) =>
            !string.IsNullOrEmpty(characterId) && (_roomAudioCoordinator?.IsCharacterMuted(characterId) ?? false);

        public bool SetRemoteAudioEnabled(string characterId, bool enabled)
        {
            if (string.IsNullOrEmpty(characterId) || _roomAudioCoordinator == null)
                return false;

            if (!AgentRegistry.TryGetCharacter(characterId, out IConvaiCharacterAgent character))
                return false;

            _roomAudioCoordinator.SetRemoteAudioEnabled(character, enabled);
            _convaiRoomController?.ApplyRemoteAudioPreference(characterId, enabled);
            return true;
        }

        public bool IsRemoteAudioEnabled(string characterId)
        {
            if (string.IsNullOrEmpty(characterId) || _roomAudioCoordinator == null)
                return false;

            return AgentRegistry.TryGetCharacter(characterId, out IConvaiCharacterAgent character) &&
                   _roomAudioCoordinator.IsRemoteAudioEnabled(character);
        }

        public bool BindParticipantAudioOutput(string participantIdentity, AudioSource audioSource) =>
            _audioTrackManager?.BindParticipantAudioOutput(participantIdentity, audioSource) ?? false;

        public bool SetParticipantAudioEnabled(string participantIdentity, bool enabled) =>
            _audioTrackManager?.SetParticipantAudioEnabled(participantIdentity, enabled) ?? false;

        public bool ToggleMicMute()
        {
            bool newState = !_conversationRoutingMicrophoneGate.ResolveUserMuted(IsMicMuted);
            SetMicMuted(newState);
            return newState;
        }

        public bool SetCharacterAudioMuted(IConvaiCharacterAgent character, bool mute) =>
            character != null && SetCharacterMuted(character.CharacterId, mute);

        public bool MuteCharacter(IConvaiCharacterAgent character) => SetCharacterAudioMuted(character, true);

        public bool UnmuteCharacter(IConvaiCharacterAgent character) => SetCharacterAudioMuted(character, false);

        public bool IsCharacterAudioMuted(IConvaiCharacterAgent character) =>
            character != null && IsCharacterMuted(character.CharacterId);

        public bool TryGetCharacterAudioPlayhead(string characterId, out double playedSeconds)
        {
            playedSeconds = 0d;
            if (string.IsNullOrEmpty(characterId) || _audioTrackManager == null) return false;

            return _audioTrackManager.TryGetAudioPlayhead(characterId, out playedSeconds);
        }

        bool IConvaiRoomAudioTimelineService.TryGetCharacterAudioTimeline(
            string characterId,
            out AudioTimelineSnapshot snapshot)
        {
            snapshot = default;
            return !string.IsNullOrEmpty(characterId) &&
                   _audioTrackManager != null &&
                   _audioTrackManager.TryGetAudioTimeline(characterId, out snapshot);
        }

        bool IConvaiRoomAudioMediaTimelineService.TryGetCharacterAudioMediaTimeline(
            string characterId,
            out AudioMediaTimelineSnapshot snapshot)
        {
            snapshot = default;
            return !string.IsNullOrEmpty(characterId) &&
                   _audioTrackManager != null &&
                   _audioTrackManager.TryGetAudioMediaTimeline(characterId, out snapshot);
        }

        private void HandleMicMuteChanged(bool isMuted)
        {
            _eventHub?.Publish(Domain.DomainEvents.Runtime.MicMuteChanged.Create(isMuted));
            SafeEventInvoker.Invoke(
                MicMuteChanged,
                isMuted,
                _logger,
                "ConvaiRoomManager.MicMuteChanged",
                Convai.Domain.Logging.LogCategory.Audio);
        }
    }
}
