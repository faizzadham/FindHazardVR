using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Networking.Media;
using Convai.Runtime.Room;

namespace Convai.Runtime.Adapters.Networking
{
    internal sealed class RoomCharacterLifecycleCoordinator
    {
        private readonly Func<IAgentRegistry> _agentRegistryProvider;
        private readonly Func<AudioTrackManager> _audioTrackManagerProvider;
        private readonly Func<IReadOnlyList<IConvaiCharacterAgent>> _characterListProvider;
        private readonly Func<SessionState> _currentStateProvider;
        private readonly Action<SessionState> _updateSessionState;
        private bool _hasRecoveredCharacterReady;

        /// <summary>
        ///     Memberships whose recovery is armed, and when — see
        ///     <see cref="CharacterReadyRecoveryPolicy" /> for why evidence arms rather than completes.
        /// </summary>
        private readonly Dictionary<string, double> _armedRecoveries = new(StringComparer.Ordinal);

        private readonly Func<double> _nowSecondsProvider;
        private readonly Action<string> _scheduleRecoveryRecheck;

        public RoomCharacterLifecycleCoordinator(
            Func<SessionState> currentStateProvider,
            Func<IReadOnlyList<IConvaiCharacterAgent>> characterListProvider,
            Func<IAgentRegistry> agentRegistryProvider,
            Func<AudioTrackManager> audioTrackManagerProvider,
            Action<SessionState> updateSessionState,
            Func<double> nowSecondsProvider = null,
            Action<string> scheduleRecoveryRecheck = null)
        {
            _currentStateProvider =
                currentStateProvider ?? throw new ArgumentNullException(nameof(currentStateProvider));
            _characterListProvider =
                characterListProvider ?? throw new ArgumentNullException(nameof(characterListProvider));
            _agentRegistryProvider = agentRegistryProvider ??
                                     throw new ArgumentNullException(nameof(agentRegistryProvider));
            _audioTrackManagerProvider = audioTrackManagerProvider ??
                                         throw new ArgumentNullException(nameof(audioTrackManagerProvider));
            _updateSessionState = updateSessionState ?? throw new ArgumentNullException(nameof(updateSessionState));

            // Both optional so existing construction sites keep working; without a scheduler the
            // recovery still completes, just on the next piece of evidence rather than on a timer.
            _nowSecondsProvider = nowSecondsProvider ?? (() => UnityEngine.Time.realtimeSinceStartupAsDouble);
            _scheduleRecoveryRecheck = scheduleRecoveryRecheck;
        }

        public void HandleCharacterReady(CharacterReady readyEvent)
        {
            SessionState currentState = _currentStateProvider();
            if (currentState != SessionState.Connecting && currentState != SessionState.Reconnecting) return;

            IAgentRegistry currentRegistry = _agentRegistryProvider();
            if (currentRegistry is IMultiCharacterSessionRegistry multiRegistry &&
                multiRegistry.CurrentMultiCharacterSession is { } multiSession)
            {
                if (string.IsNullOrWhiteSpace(readyEvent.MembershipId) ||
                    !string.Equals(
                        readyEvent.MembershipId,
                        multiSession.InitialCharacter?.MembershipId,
                        StringComparison.Ordinal))
                    return;

                _hasRecoveredCharacterReady = true;
                _updateSessionState(SessionState.Connected);
                return;
            }

            IReadOnlyList<IConvaiCharacterAgent> characterList = _characterListProvider();
            string activeCharacterId = characterList?.Count > 0 ? characterList[0]?.CharacterId : null;

            if (!string.IsNullOrEmpty(activeCharacterId))
            {
                bool matchesByCharacterId = !string.IsNullOrEmpty(readyEvent.CharacterId) &&
                                            string.Equals(activeCharacterId, readyEvent.CharacterId,
                                                StringComparison.OrdinalIgnoreCase);

                bool matchesByParticipantId = false;
                IAgentRegistry agentRegistry = _agentRegistryProvider();
                if (!string.IsNullOrEmpty(readyEvent.ParticipantId) &&
                    agentRegistry != null &&
                    agentRegistry.TryGetCharacter(activeCharacterId, out IConvaiCharacterAgent _))
                {
                    if (agentRegistry.TryGetParticipantId(activeCharacterId, out string existingParticipantId) &&
                        !string.IsNullOrEmpty(existingParticipantId))
                    {
                        matchesByParticipantId = string.Equals(existingParticipantId, readyEvent.ParticipantId,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    else if (characterList != null && characterList.Count == 1)
                    {
                        agentRegistry.SetParticipantId(activeCharacterId, readyEvent.ParticipantId);
                        matchesByParticipantId = true;
                    }
                }

                if (!matchesByCharacterId && !matchesByParticipantId) return;
            }

            _hasRecoveredCharacterReady = true;
            _updateSessionState(SessionState.Connected);
        }

        /// <summary>
        ///     Forgets everything this coordinator inferred about readiness, for a connection that is
        ///     starting over.
        /// </summary>
        /// <remarks>
        ///     Both halves have to go. The recovered-ready latch is the obvious one; the armed
        ///     recoveries are the one worth spelling out, because an arming is a <i>timestamp</i>
        ///     taken during the connection that just ended. Carried into the next one it is already
        ///     older than <see cref="CharacterReadyRecoveryPolicy.DefaultGraceSeconds" />, so the
        ///     first piece of presence evidence would skip the grace entirely and announce a
        ///     character the service has not announced.
        /// </remarks>
        public void ResetRecoveredReadinessState()
        {
            _hasRecoveredCharacterReady = false;
            _armedRecoveries.Clear();
        }

        public CharacterReady? TryRecoverCharacterReadyFromSpeech(CharacterSpeechStateChanged speechEvent)
        {
            if (!speechEvent.IsSpeaking)
                return null;

            return TryRecoverCharacterReady(speechEvent.CharacterId, null, CharacterReadyEvidence.Speech);
        }

        public CharacterReady? TryRecoverCharacterReadyFromTts(CharacterTtsTextChunk ttsEvent)
        {
            if (ttsEvent.IsEmpty)
                return null;

            return TryRecoverCharacterReady(null, ttsEvent.ParticipantId, CharacterReadyEvidence.Speech);
        }

        public CharacterReady? TryRecoverCharacterReadyFromLipSync(LipSyncPackedDataReceived lipSyncEvent)
        {
            if (!lipSyncEvent.IsValid)
                return null;

            return TryRecoverCharacterReady(
                lipSyncEvent.CharacterId, lipSyncEvent.ParticipantId, CharacterReadyEvidence.Speech);
        }

        public CharacterReady? TryRecoverCharacterReadyFromAudioTrack(
            string participantIdentity,
            string participantId) =>
            TryRecoverCharacterReady(participantIdentity, participantId, CharacterReadyEvidence.Presence);

        public void HandleRemoteAudioTrackSubscribed(IRemoteAudioTrack audioTrack, string participantSid,
            string participantIdentity) =>
            _audioTrackManagerProvider()?.HandleRemoteAudioTrackSubscribed(
                audioTrack,
                participantSid,
                participantIdentity);

        public void HandleRemoteAudioTrackUnsubscribed(string participantSid) =>
            _audioTrackManagerProvider()?.HandleRemoteAudioTrackUnsubscribed(participantSid);

        private CharacterReady? TryRecoverCharacterReady(
            string observedCharacterId,
            string observedParticipantId,
            CharacterReadyEvidence evidence)
        {
            IAgentRegistry registry = _agentRegistryProvider();
            if (registry is IMultiCharacterSessionRegistry multiRegistry &&
                multiRegistry.CurrentMultiCharacterSession is { } multiSession)
                return TryRecoverMultiCharacterReady(
                    registry,
                    multiSession,
                    observedCharacterId,
                    observedParticipantId,
                    evidence);

            if (_hasRecoveredCharacterReady)
                return null;

            SessionState currentState = _currentStateProvider();
            if (currentState != SessionState.Connecting && currentState != SessionState.Reconnecting)
                return null;

            if (!TryResolveActiveCharacter(out string activeCharacterId, out bool singleCharacterSession))
                return null;

            bool matchesByCharacterId = !string.IsNullOrWhiteSpace(observedCharacterId) &&
                                        string.Equals(activeCharacterId, observedCharacterId,
                                            StringComparison.OrdinalIgnoreCase);

            string participantId = NormalizeParticipantForActiveCharacter(activeCharacterId, observedParticipantId,
                singleCharacterSession, out bool matchesByParticipantId);

            if (!matchesByCharacterId && !matchesByParticipantId)
                return null;

            _hasRecoveredCharacterReady = true;
            return CharacterReady.Create(activeCharacterId, participantId ?? string.Empty);
        }

        private CharacterReady? TryRecoverMultiCharacterReady(
            IAgentRegistry registry,
            MultiCharacterRoomSession session,
            string observedCharacterId,
            string observedParticipantId,
            CharacterReadyEvidence evidence)
        {
            SessionState currentState = _currentStateProvider();
            if (currentState != SessionState.Connecting &&
                currentState != SessionState.Reconnecting &&
                currentState != SessionState.Connected)
                return null;

            CharacterRoomMembership membership = session.Resolve(
                null,
                observedCharacterId,
                observedParticipantId);
            if (membership == null &&
                !string.IsNullOrWhiteSpace(observedCharacterId) &&
                registry.TryGetCharacter(observedCharacterId, out IConvaiCharacterAgent character))
                membership = session.FindByCharacter(character);

            return CompleteOrArmRecovery(registry, session, membership, observedParticipantId, evidence);
        }

        /// <summary>
        ///     Applies <see cref="CharacterReadyRecoveryPolicy" /> to one piece of evidence: the first
        ///     arms a recovery, and only silence past the grace completes it.
        /// </summary>
        private CharacterReady? CompleteOrArmRecovery(
            IAgentRegistry registry,
            MultiCharacterRoomSession session,
            CharacterRoomMembership membership,
            string observedParticipantId,
            CharacterReadyEvidence evidence)
        {
            string membershipId = membership?.MembershipId ?? string.Empty;
            double armedAt = 0d;
            bool armed = membership != null &&
                         _armedRecoveries.TryGetValue(membershipId, out armedAt);

            double now = _nowSecondsProvider();
            CharacterReadyEvidenceVerdict verdict = CharacterReadyRecoveryPolicy.Evaluate(
                membership != null,
                membership?.Status ?? CharacterRoomStatus.Failed,
                armed,
                armed ? now - armedAt : 0d,
                evidence);

            switch (verdict)
            {
                case CharacterReadyEvidenceVerdict.Ignore:
                    if (membership != null) _armedRecoveries.Remove(membershipId);
                    return null;

                case CharacterReadyEvidenceVerdict.ArmRecovery:
                    if (armed) return null;

                    _armedRecoveries[membershipId] = now;
                    // Evidence can be a single event — one audio-track subscription and nothing
                    // after it — so the grace needs something to wake it. Without a scheduler the
                    // recovery still happens, just on the next piece of evidence.
                    _scheduleRecoveryRecheck?.Invoke(membershipId);
                    return null;
            }

            _armedRecoveries.Remove(membershipId);
            session.MarkReady(membership, observedParticipantId);
            if (!string.IsNullOrWhiteSpace(observedParticipantId))
                registry.SetParticipantId(membership.CharacterId, observedParticipantId);

            return CharacterReady.Create(
                membership.CharacterId,
                observedParticipantId ?? string.Empty,
                membership.MembershipId,
                membership.CharacterSessionId,
                membership.ParticipantIdentity);
        }

        /// <summary>
        ///     Completes an armed recovery whose grace has elapsed, if the service still has not said
        ///     the character is ready. Called by whatever the caller used to schedule the re-check.
        /// </summary>
        public CharacterReady? TryCompleteArmedRecovery(string membershipId)
        {
            if (string.IsNullOrEmpty(membershipId) || !_armedRecoveries.ContainsKey(membershipId))
                return null;

            IAgentRegistry registry = _agentRegistryProvider();
            if (registry is not IMultiCharacterSessionRegistry multiRegistry ||
                multiRegistry.CurrentMultiCharacterSession is not { } session)
            {
                _armedRecoveries.Remove(membershipId);
                return null;
            }

            CharacterRoomMembership membership = session.FindByMembershipId(membershipId);
            // The grace has already elapsed for this membership, so the weak signal that armed it
            // is now enough on its own.
            return CompleteOrArmRecovery(
                registry, session, membership, membership?.ParticipantId, CharacterReadyEvidence.Presence);
        }

        /// <summary>
        ///     Cancels an armed recovery because the service's own readiness signal arrived — the
        ///     whole point of the grace.
        /// </summary>
        public void DisarmRecovery(string membershipId)
        {
            if (!string.IsNullOrEmpty(membershipId))
                _armedRecoveries.Remove(membershipId);
        }

        private bool TryResolveActiveCharacter(out string activeCharacterId, out bool singleCharacterSession)
        {
            IReadOnlyList<IConvaiCharacterAgent> characterList = _characterListProvider();
            activeCharacterId = characterList?.Count > 0 ? characterList[0]?.CharacterId : null;
            singleCharacterSession = characterList != null && characterList.Count == 1;
            return !string.IsNullOrWhiteSpace(activeCharacterId);
        }

        private string NormalizeParticipantForActiveCharacter(string activeCharacterId, string observedParticipantId,
            bool singleCharacterSession, out bool matchesByParticipantId)
        {
            matchesByParticipantId = false;

            if (string.IsNullOrWhiteSpace(activeCharacterId))
                return observedParticipantId;

            IAgentRegistry agentRegistry = _agentRegistryProvider();
            if (agentRegistry == null || !agentRegistry.TryGetCharacter(activeCharacterId, out IConvaiCharacterAgent _))
                return observedParticipantId;

            if (agentRegistry.TryGetParticipantId(activeCharacterId, out string existingParticipantId) &&
                !string.IsNullOrWhiteSpace(existingParticipantId))
            {
                matchesByParticipantId = !string.IsNullOrWhiteSpace(observedParticipantId) &&
                                         string.Equals(existingParticipantId, observedParticipantId,
                                             StringComparison.OrdinalIgnoreCase);
                return existingParticipantId;
            }

            if (!string.IsNullOrWhiteSpace(observedParticipantId) && singleCharacterSession)
            {
                agentRegistry.SetParticipantId(activeCharacterId, observedParticipantId);
                matchesByParticipantId = true;
                return observedParticipantId;
            }

            return observedParticipantId;
        }
    }
}
