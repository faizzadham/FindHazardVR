using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Logging;
using Convai.Runtime.Networking.Media;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Sources;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using UnityEngine;

namespace Convai.Runtime.Adapters.Networking
{
    public partial class ConvaiRoomManager
    {
        internal RoomOwnershipRebindOutcome HandleOwnedAgentStateChanged()
        {
            if (TryApplyLiveRosterChange())
                return PublishOwnershipRebindOutcome(RoomOwnershipRebindOutcome.AppliedImmediately, null);

            RoomOwnershipChangeResult result =
                _roomCompositionService.HandleOwnedAgentStateChanged(CreateCompositionContext(),
                    CreateCompositionState());

            if (result.Artifacts != null)
                ApplyCompositionArtifacts(result.Artifacts, result.ResetConnectionContext);

            if (result.ClearPendingReconnect)
                ClearPendingOwnershipReconnect();

            if (result.SetPendingReconnect)
                SetPendingOwnershipReconnect(result.PendingReconnectCharacterId);

            if (result.TransitionErrorToDisconnected)
                UpdateSessionState(SessionState.Disconnected);

            if (result.FailureReason != RoomStartupFailureReason.None)
                _roomCompositionService.LogOwnershipRebindFailure(_logger, result.FailureReason);

            return PublishOwnershipRebindOutcome(result.Outcome, result.RequestedCharacterId);
        }

        // Reused across roster changes so an ordinary scene edit does not allocate.
        private readonly List<IConvaiCharacterAgent> _rosterAdditions = new();
        private readonly List<int> _rosterRemovalIndices = new();
        private readonly List<IConvaiCharacterAgent> _rosterMemberCharacters = new();

        /// <summary>
        ///     Applies a character appearing or disappearing to the live room, instead of waiting for a
        ///     reconnect.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Spawning a character from a prefab mid-session is an ordinary thing to do, and until
        ///         now it did nothing visible: ownership resolution noticed the new character, decided
        ///         the composition had changed, and queued a reconnect that would only happen if
        ///         something else disconnected the room. The service has accepted live roster changes
        ///         all along — <see cref="AddCharacterAsync" /> and <see cref="RemoveCharacterAsync" />
        ///         — and nothing called them.
        ///     </para>
        ///     <para>
        ///         <see cref="LiveRosterPlanner" /> makes the decision, so that it can be tested without
        ///         a room, a session or a scene; everything Unity-side is answered here and passed in.
        ///         Anything it declines falls through to the reconnect path exactly as before.
        ///     </para>
        /// </remarks>
        private bool TryApplyLiveRosterChange()
        {
            MultiCharacterRoomSession session = CurrentMultiCharacterSession;
            RoomOwnershipSnapshot ownership = _roomOwnershipProvider?.CaptureOwnership();

            IReadOnlyList<CharacterRoomMembership> memberships =
                session?.Characters ?? (IReadOnlyList<CharacterRoomMembership>)Array.Empty<CharacterRoomMembership>();

            _rosterMemberCharacters.Clear();
            for (int i = 0; i < memberships.Count; i++)
                _rosterMemberCharacters.Add(memberships[i].Character);

            LiveRosterVerdict verdict = LiveRosterPlanner.Plan(
                CurrentState == SessionState.Connected,
                session is { IsReady: true },
                ownership?.Characters != null,
                ReferenceEquals(ownership?.Player, Player),
                _activeCharacter,
                ownership?.ConversationTarget?.Character,
                ownership?.ConversationTarget?.IsExplicit ?? false,
                ownership?.Characters,
                ownership?.RetainedCharacters,
                _rosterMemberCharacters,
                _rosterAdditions,
                _rosterRemovalIndices);

            if (verdict != LiveRosterVerdict.Apply)
            {
                // The named reason is the whole point of the verdict: a character that quietly never
                // joins looks the same as a scene fault. Gated, because this is the ordinary answer
                // during startup and on every ownership change that is not a roster edit.
                if (LoggingConfig.IsDebugEnabled(LogCategory.SDK))
                    {
                        if (_logger?.IsEnabled(LogLevel.Debug, LogCategory.SDK) == true)
                            _logger?.Debug(
                            $"Live roster change not applied ({verdict}); the reconnect path decides instead.",
                            LogCategory.SDK);
                    }

                // One verdict is not a passing condition and has to be said out loud: a connected
                // single-character room has no roster at all, so a character appearing now cannot
                // join however long it waits. Debug logging is off by default, and without this the
                // only symptom is a character standing there saying nothing.
                if (verdict == LiveRosterVerdict.SessionNotReady && session == null &&
                    CurrentState == SessionState.Connected)
                    ReportSingleCharacterRoomCannotGrow(ownership?.Characters);

                return false;
            }

            // The live apply delivers exactly what a queued ownership reconnect was waiting to
            // deliver — player and starting-character drift cannot reach Apply — so a pending
            // reconnect left set here could only reconnect into an identical composition.
            if (HasPendingOwnershipReconnect)
                ClearPendingOwnershipReconnect();

            for (int i = 0; i < _rosterAdditions.Count; i++) SendRosterAdd(_rosterAdditions[i]);
            for (int i = 0; i < _rosterRemovalIndices.Count; i++)
            {
                CharacterRoomMembership leaving = memberships[_rosterRemovalIndices[i]];
                SendRosterRemove(
                    leaving,
                    ResolveHandoverMembershipId(session, leaving, memberships, _rosterRemovalIndices));
            }

            return true;
        }

        /// <summary>
        ///     Names a membership that can take the conversation over, whenever there is one.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Offered on every removal rather than only when the leaving membership looks like
        ///         the one holding the conversation. The service decides whether a replacement is
        ///         needed, and it decides from <i>its own</i> record of the active membership; the
        ///         copy this client keeps can differ from that record, and when it did the edit came
        ///         back refused with <c>replacement_target_required</c> — a removal that never
        ///         happened because the client had guessed the service did not need one.
        ///     </para>
        ///     <para>
        ///         Offering one that turns out to be unnecessary costs nothing: the service only
        ///         reads the field when it is removing the active membership. So the safe direction
        ///         is to always name a candidate and let the side that knows decide.
        ///     </para>
        ///     <para>
        ///         Only a <see cref="CharacterRoomStatus.Ready" /> member is offered, because a
        ///         member that has not been announced yet cannot receive the conversation either —
        ///         the service refuses those too. Every member leaving in this same edit is skipped,
        ///         because handing the conversation to a character that is itself on its way out
        ///         just moves the problem one membership along.
        ///     </para>
        /// </remarks>
        private static string ResolveHandoverMembershipId(
            MultiCharacterRoomSession session,
            CharacterRoomMembership leaving,
            IReadOnlyList<CharacterRoomMembership> memberships,
            List<int> removalIndices)
        {
            if (session == null || leaving == null || memberships == null) return null;

            for (int i = 0; i < memberships.Count; i++)
            {
                if (removalIndices != null && removalIndices.Contains(i)) continue;

                CharacterRoomMembership candidate = memberships[i];
                if (candidate == null || ReferenceEquals(candidate, leaving)) continue;
                if (candidate.Status != CharacterRoomStatus.Ready) continue;
                if (candidate.Character is Behaviour { isActiveAndEnabled: false }) continue;

                return candidate.MembershipId;
            }

            return null;
        }

        /// <summary>
        ///     Reapplies ownership to the live roster once the session can accept edits.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A character that appears or disappears while a connection is in flight is declined
        ///         with <c>RejectedTransitionState</c>, and nothing asks again once the room settles —
        ///         the ownership refresh that runs on connect sees no further drift, because the drift
        ///         already happened. This is the re-ask: wait for the session's initial character to
        ///         be ready, then run the same live roster decision every other roster event runs.
        ///         When the roster already matches, the planner answers <c>NoRosterChange</c> and
        ///         nothing is sent.
        ///     </para>
        ///     <para>
        ///         Also called after a roster edit lands, and for the same reason one step later. An
        ///         edit is a round trip; ownership can change again while it is in flight, and that
        ///         change is judged against a roster the service has not updated yet — so it reads
        ///         as no change at all and is dropped. Nothing asked again afterwards, because
        ///         ownership had already finished moving. Disabling a character and re-enabling it
        ///         quickly left it out of the room until the next reconnect, while doing the same
        ///         slowly worked: the difference was only whether the second change landed before
        ///         the first round trip returned.
        ///     </para>
        ///     <para>
        ///         Only after an edit that <i>succeeded</i>. Re-asking after a refusal would send the
        ///         same refused edit again immediately, and keep doing it.
        ///     </para>
        /// </remarks>
        internal void ScheduleLiveRosterReconcile()
        {
            MultiCharacterRoomSession session = CurrentMultiCharacterSession;
            if (session == null) return;
            ReconcileLiveRosterWhenReady(session);
        }

        /// <summary>
        ///     How long the re-ask waits for a session to become ready before giving up on it.
        /// </summary>
        /// <remarks>
        ///     Retiring a session faults the readiness signal, so the ordinary end of a room does
        ///     release this wait. What it does not cover is a room that stays up while its initial
        ///     character never arrives: there the wait had nothing to end it, and every roster edit
        ///     added another one. A reconcile is an optimisation — if readiness never comes there is
        ///     nothing to reconcile — so a bounded wait loses nothing and cannot accumulate.
        /// </remarks>
        private const float LiveRosterReconcileTimeoutSeconds = 30f;

        private async void ReconcileLiveRosterWhenReady(MultiCharacterRoomSession session)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(LiveRosterReconcileTimeoutSeconds));
                await session.WaitUntilReadyAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // The room never became ready inside the window. Logged rather than silent: a
                // character that quietly stayed out of the room is the symptom this path exists to
                // prevent, and "the re-ask gave up" is the one line that separates it from a fault.
                ConvaiLogger.Debug(
                    "[ConvaiRoomManager] The live roster re-ask stopped waiting: the room's first " +
                    $"character was not ready within {LiveRosterReconcileTimeoutSeconds:0} seconds.",
                    LogCategory.SDK);
                return;
            }
            catch (Exception)
            {
                // The session retired or its initial character failed; the roster died with it.
                return;
            }

            if (!ReferenceEquals(CurrentMultiCharacterSession, session)) return;
            if (CurrentState != SessionState.Connected) return;

            if (TryApplyLiveRosterChange())
                PublishOwnershipRebindOutcome(RoomOwnershipRebindOutcome.AppliedImmediately, null);
        }

        private bool _reportedSingleCharacterRoomCannotGrow;

        /// <summary>
        ///     Says why a character that appeared during play is not in the conversation, when the
        ///     room it appeared into can never take it.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A room opened for one character is created without a roster — the connect request
        ///         names a single character and the reply carries no room session — so there is
        ///         nothing for a live roster edit to address. Every other declined verdict is either
        ///         transient or is handled by the reconnect path; this one is permanent for as long
        ///         as the connection lasts, and it is the one a project cannot diagnose from the
        ///         outside: the character is injected, owned, enabled and silent.
        ///     </para>
        ///     <para>
        ///         Said once per connection. The latch clears when the composition is rebuilt, so a
        ///         later room reports it again rather than staying quiet about a new occurrence.
        ///     </para>
        /// </remarks>
        private void ReportSingleCharacterRoomCannotGrow(IReadOnlyList<IConvaiCharacterAgent> ownedCharacters)
        {
            if (_reportedSingleCharacterRoomCannotGrow) return;

            string waiting = DescribeCharactersOutsideTheRoom(ownedCharacters);
            if (waiting == null) return;

            _reportedSingleCharacterRoomCannotGrow = true;
            if (_logger?.IsEnabled(LogLevel.Warning, LogCategory.SDK) == true)
                _logger?.Warning(
                $"{waiting} cannot join this conversation: the room was opened for a single " +
                "character, so it has no roster to join. It will be included the next time the room " +
                "connects. To let characters come and go during play, have every character you want " +
                "active in the scene before the room connects — a character that is present but " +
                "disabled does not count, because the room is opened for the active ones.",
                LogCategory.SDK);
        }

        /// <summary>
        ///     Names the owned characters that are not the one the room was opened for, or
        ///     <c>null</c> when there are none.
        /// </summary>
        private string DescribeCharactersOutsideTheRoom(IReadOnlyList<IConvaiCharacterAgent> ownedCharacters)
        {
            if (ownedCharacters == null) return null;

            string names = null;
            int count = 0;
            for (int i = 0; i < ownedCharacters.Count; i++)
            {
                IConvaiCharacterAgent candidate = ownedCharacters[i];
                if (candidate == null || ReferenceEquals(candidate, _activeCharacter)) continue;

                count++;
                names = names == null ? $"'{candidate.CharacterName}'" : $"{names}, '{candidate.CharacterName}'";
            }

            return count == 0 ? null : names;
        }

        private async void SendRosterAdd(IConvaiCharacterAgent character)
        {
            Task<CharacterRosterUpdateResult> canonicalCompletion = null;
            try
            {
                await AddCharacterTrackedAsync(
                    character,
                    (session, canonicalTask) =>
                    {
                        canonicalCompletion = canonicalTask;
                    });
            }
            catch (Exception exception)
            {
                if (canonicalCompletion != null)
                {
                    if ((exception is TimeoutException || exception is OperationCanceledException) &&
                        !canonicalCompletion.IsCompleted)
                        {
                            if (_logger?.IsEnabled(LogLevel.Warning, LogCategory.SDK) == true)
                                _logger?.Warning(
                                $"The caller stopped waiting for '{character.CharacterName}' to join " +
                                "the room. The canonical roster response is still being tracked.",
                                LogCategory.SDK);
                        }
                    return;
                }

                // The room carries on with the roster it has. Saying which character failed and why is
                // the whole value here: a character that silently never joins looks like a scene fault.
                ReportRosterAddFailure(character, exception);
            }
        }

        private async void SendRosterRemove(CharacterRoomMembership membership, string handoverMembershipId)
        {
            Task<CharacterRosterUpdateResult> canonicalCompletion = null;
            try
            {
                await RemoveCharacterTrackedAsync(
                    membership.MembershipId,
                    handoverMembershipId,
                    (session, canonicalTask) =>
                    {
                        canonicalCompletion = canonicalTask;
                    });
            }
            catch (Exception exception)
            {
                if (canonicalCompletion != null)
                {
                    if ((exception is TimeoutException || exception is OperationCanceledException) &&
                        !canonicalCompletion.IsCompleted)
                        {
                            if (_logger?.IsEnabled(LogLevel.Warning, LogCategory.SDK) == true)
                                _logger?.Warning(
                                $"The caller stopped waiting for " +
                                $"'{membership.Character?.CharacterName ?? membership.CharacterId}' to leave " +
                                "the room. The canonical roster response is still being tracked.",
                                LogCategory.SDK);
                        }
                    return;
                }

                ReportRosterRemoveFailure(membership, exception);
            }
        }

        private async Task ObserveRosterAddAsync(
            MultiCharacterRoomSession session,
            IConvaiCharacterAgent character,
            Task<CharacterRosterUpdateResult> canonicalCompletion)
        {
            try
            {
                await canonicalCompletion;
                if (!ReferenceEquals(CurrentMultiCharacterSession, session)) return;
                if (_logger?.IsEnabled(LogLevel.Info, LogCategory.SDK) == true)
                    _logger?.Info(
                    $"'{character.CharacterName}' joined the room without reconnecting.",
                    LogCategory.SDK);
                PublishRosterChanged(RoomRosterChange.Joined, character);
                ScheduleLiveRosterReconcile();
            }
            catch (Exception exception)
            {
                if (!ReferenceEquals(CurrentMultiCharacterSession, session) &&
                    exception is not TimeoutException)
                    return;
                ReportRosterAddFailure(character, exception);
            }
        }

        private async Task ObserveRosterRemoveAsync(
            MultiCharacterRoomSession session,
            CharacterRoomMembership membership,
            Task<CharacterRosterUpdateResult> canonicalCompletion)
        {
            try
            {
                await canonicalCompletion;
                if (!ReferenceEquals(CurrentMultiCharacterSession, session)) return;
                if (_logger?.IsEnabled(LogLevel.Info, LogCategory.SDK) == true)
                    _logger?.Info(
                    $"'{membership.Character?.CharacterName ?? membership.CharacterId}' left the room " +
                    "without reconnecting.",
                    LogCategory.SDK);
                PublishRosterChanged(RoomRosterChange.Left, membership.Character, null, membership);
                ScheduleLiveRosterReconcile();
            }
            catch (Exception exception)
            {
                if (!ReferenceEquals(CurrentMultiCharacterSession, session) &&
                    exception is not TimeoutException)
                    return;
                ReportRosterRemoveFailure(membership, exception);
            }
        }

        private void ReportRosterAddFailure(IConvaiCharacterAgent character, Exception exception)
        {
            string reason = DescribeRosterFailure(exception);
            if (_logger?.IsEnabled(LogLevel.Error, LogCategory.SDK) == true)
                _logger?.Error(
                $"'{character.CharacterName}' could not join the room: {reason}",
                LogCategory.SDK);
            PublishRosterChanged(RoomRosterChange.Refused, character, reason);
        }

        private void ReportRosterRemoveFailure(CharacterRoomMembership membership, Exception exception)
        {
            string reason = DescribeRosterFailure(exception);
            if (_logger?.IsEnabled(LogLevel.Error, LogCategory.SDK) == true)
                _logger?.Error(
                $"'{membership.Character?.CharacterName ?? membership.CharacterId}' could not leave " +
                $"the room: {reason}",
                LogCategory.SDK);
                PublishRosterChanged(
                    RoomRosterChange.Refused,
                    membership.Character,
                    reason,
                    membership);
        }

        /// <summary>
        ///     The reason a roster edit was refused, including the service's own code for it.
        /// </summary>
        /// <remarks>
        ///     The service answers a refused roster edit with a machine-readable code —
        ///     <c>stale_roster_epoch</c>, <c>membership_not_found</c>, <c>roster_capacity_exceeded</c>
        ///     and the rest — and a message that is the same sentence every time. Logging only the
        ///     message turned every refusal into "Roster update rejected", which names the outcome
        ///     and hides the cause; the code was already carried this far and simply thrown away.
        /// </remarks>
        private static string DescribeRosterFailure(Exception exception)
        {
            if (exception is not CharacterRosterUpdateException rosterFailure ||
                string.IsNullOrWhiteSpace(rosterFailure.Code))
                return exception.Message;

            return $"{exception.Message} ({rosterFailure.Code})";
        }

        /// <summary>
        ///     Reports a roster edit on the event hub, with the roster size read after the edit.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Read after rather than counted by the caller: the add and the remove paths would
        ///         each have to work out what the room holds now, and one of them would eventually be
        ///         wrong about a membership the service also changed.
        ///     </para>
        ///     <para>
        ///         A membership is passed in for a removal because the character is gone from the
        ///         roster by the time this runs, so there is nothing left to look it up by.
        ///     </para>
        /// </remarks>
        private void PublishRosterChanged(
            RoomRosterChange change,
            IConvaiCharacterAgent character,
            string reason = null,
            CharacterRoomMembership membership = null)
        {
            MultiCharacterRoomSession session = CurrentMultiCharacterSession;
            string characterId = character?.CharacterId;
            if (string.IsNullOrEmpty(characterId)) characterId = membership?.CharacterId;

            // The same fallback the Console line uses, for the same reason: a membership whose local
            // character has already been destroyed still has an id worth naming.
            string characterName = character?.CharacterName;
            if (string.IsNullOrEmpty(characterName)) characterName = characterId;

            _sessionDiagnostics?.PublishRosterChanged(
                change,
                membership?.MembershipId ?? session?.FindByCharacter(character)?.MembershipId,
                characterId,
                characterName,
                session?.Characters.Count ?? 0,
                reason);
        }

        private bool PrepareOwnershipCompositionForNextConnect()
        {
            RoomCompositionPreparationResult result =
                _roomCompositionService.PrepareOwnershipCompositionForNextConnect(CreateCompositionContext(),
                    CreateCompositionState());

            if (!result.Success)
            {
                if (result.PublishedOutcome.HasValue)
                    PublishOwnershipRebindOutcome(result.PublishedOutcome.Value, result.PublishedCharacterId);

                _roomCompositionService.LogOwnershipRebindFailure(_logger, result.FailureReason);
                return false;
            }

            if (result.Artifacts != null)
                ApplyCompositionArtifacts(result.Artifacts, result.ResetConnectionContext);

            if (result.ClearPendingReconnect)
                ClearPendingOwnershipReconnect();

            if (result.PublishedOutcome.HasValue)
                PublishOwnershipRebindOutcome(result.PublishedOutcome.Value, result.PublishedCharacterId);

            return true;
        }

        private RoomCompositionContext CreateCompositionContext() =>
            new()
            {
                AgentRegistry = AgentRegistry,
                OwnershipProvider = _roomOwnershipProvider,
                EventHub = _eventHub,
                ControllerFactory = _controllerFactory,
                CredentialProvider = _credentialProvider,
                EndUserIdentityProvider = _endUserIdentityProvider,
                EndUserMetadataProvider = _endUserMetadataProvider,
                SessionPersistence = _sessionPersistence,
                Logger = _logger,
                SectionNameResolver = _sectionNameResolver,
                ConnectionType = EffectiveConnectionType,
                VideoTrackName = ResolveVideoTrackName(),
                ServerEndpoint = EffectiveServerEndpoint,
                Debug = EffectiveDebug,
                ConnectOnStart = EffectiveConnectOnStart,
                ReconnectPolicy = _reconnectPolicy,
                TransportProvider = _transportProvider,
                RemoteAudioPreferences = _remoteAudioPreferences,
                RoomControllerEventBinder = _roomControllerEventBinder,
                SessionDiagnostics = _sessionDiagnostics,
                HandleMicMuteChanged = HandleMicMuteChanged,
                CurrentRoomProvider = () => CurrentRoom,
                GetVisionComponentFlags = GetVisionComponentFlags,
                PersistentDataPath = UnityEngine.Application.persistentDataPath,
                ClientBargeInAecActive = () => _roomAudioRuntimeAdapter?.ClientBargeInAecActive,
                ResolveLipSyncTransportOptions = ResolveLipSyncTransportOptions,
                PostToMainThread = UnityScheduler.Post
            };

        private RoomCompositionState CreateCompositionState() =>
            new()
            {
                IsInjected = _isInjected,
                HasStarted = _roomConnectionRuntimeAdapter?.HasStarted ?? false,
                CurrentState = CurrentState,
                HasPendingOwnershipReconnect = HasPendingOwnershipReconnect,
                PendingOwnershipRequestedCharacterId = _pendingOwnershipRequestedCharacterId,
                ConnectionContext = _connectionContext,
                Player = Player,
                ActiveCharacter = _activeCharacter,
                CharacterList = _characterList,
                RoomController = _convaiRoomController
            };

        private void ApplyCompositionArtifacts(RoomCompositionArtifacts artifacts, bool resetConnectionContext)
        {
            if (artifacts == null)
                return;

            DisposeCurrentComposition();

            if (resetConnectionContext)
                _connectionContext = ConnectionContext.Empty;

            Player = artifacts.Player;
            if (Player != null)
                Player.OnTextMessageSent += HandlePlayerTextMessage;

            _activeCharacter = artifacts.ActiveCharacter;
            _characterList = artifacts.Characters;
            _playerSession = artifacts.PlayerSession;
            _convaiRoomController = artifacts.RoomController;
            _audioTrackManager = artifacts.AudioTrackManager;
            _bargeInCoordinator = new BargeInCoordinator(
                () => _audioTrackManager,
                () => CurrentResolvedTurnTakingOptions,
                marker => _clientLatencyMetricsCollector?.RecordBargeInMarker(marker));
            _debugMetricsFileWriter = artifacts.DebugMetricsFileWriter;
            _clientLatencyMetricsCollector = artifacts.ClientLatencyMetricsCollector;
            _roomControllerEventBinder?.Attach(_convaiRoomController);

            ConvaiLogger.Debug(
                $"Resolved {CharacterList.Count} Character(s), using primary Character '{_activeCharacter?.CharacterName ?? _activeCharacter?.CharacterId}'.",
                LogCategory.SDK);
            ConvaiLogger.Debug("Room controller created via factory.", LogCategory.SDK);
        }

        private void DisposeCurrentComposition()
        {
            // A new composition is a new room; whatever was said about the previous one's shape no
            // longer describes this one.
            _reportedSingleCharacterRoomCannotGrow = false;

            ResetConnectionScopedRuntimeState();
            _roomConnectionRuntimeAdapter?.ClearResolvedCredentials();

            if (Player != null)
                Player.OnTextMessageSent -= HandlePlayerTextMessage;

            UnsubscribeFromRtvMetrics();
            _clientLatencyMetricsCollector?.Dispose();
            _clientLatencyMetricsCollector = null;
            _debugMetricsFileWriter?.Dispose();
            _debugMetricsFileWriter = null;
            _roomControllerEventBinder?.Detach();
            _bargeInCoordinator?.Dispose();
            _audioTrackManager?.Dispose();
            _convaiRoomController?.Dispose();
            _playerSession?.Dispose();

            _audioTrackManager = null;
            _bargeInCoordinator = null;
            _convaiRoomController = null;
            _playerSession = null;
            _activeCharacter = null;
            _characterList = null;
            Player = null;
        }

        private void SetPendingOwnershipReconnect(string requestedCharacterId)
        {
            HasPendingOwnershipReconnect = true;
            _pendingOwnershipRequestedCharacterId = requestedCharacterId ?? string.Empty;
        }

        private void ClearPendingOwnershipReconnect()
        {
            HasPendingOwnershipReconnect = false;
            _pendingOwnershipRequestedCharacterId = null;
        }

        private RoomOwnershipRebindOutcome PublishOwnershipRebindOutcome(
            RoomOwnershipRebindOutcome outcome,
            string requestedCharacterId)
        {
            _lastOwnershipRebindOutcome = ToPublicOwnershipRebindStatus(outcome).ToString();
            _sessionDiagnostics?.PublishOwnershipRebindState(
                ToPublicOwnershipRebindStatus(outcome),
                HasPendingOwnershipReconnect,
                CurrentState,
                _activeCharacter?.CharacterId,
                requestedCharacterId);
            return outcome;
        }

        private static RoomOwnershipRebindStatus ToPublicOwnershipRebindStatus(RoomOwnershipRebindOutcome outcome) =>
            outcome switch
            {
                RoomOwnershipRebindOutcome.DeferredUntilStartup => RoomOwnershipRebindStatus.DeferredUntilStartup,
                RoomOwnershipRebindOutcome.AppliedImmediately => RoomOwnershipRebindStatus.AppliedImmediately,
                RoomOwnershipRebindOutcome.PendingReconnect => RoomOwnershipRebindStatus.PendingReconnect,
                RoomOwnershipRebindOutcome.RejectedTransitionState => RoomOwnershipRebindStatus.RejectedTransitionState,
                RoomOwnershipRebindOutcome.RejectedInvalidOwnership => RoomOwnershipRebindStatus
                    .RejectedInvalidOwnership,
                _ => RoomOwnershipRebindStatus.RejectedTransitionState
            };

        private (bool hasPublisher, bool hasFrameSource) GetVisionComponentFlags()
        {
            bool hasPublisher = false;
            bool hasFrameSource = false;

            foreach (MonoBehaviour component in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!hasPublisher && component is IVisionPublisher) hasPublisher = true;
                if (!hasFrameSource && component is IVisionFrameSource) hasFrameSource = true;
                if (hasPublisher && hasFrameSource) break;
            }

            return (hasPublisher, hasFrameSource);
        }

        private LipSyncTransportOptions ResolveLipSyncTransportOptions() =>
            LipSyncTransportResolver.Resolve(CharacterList, _eventHub);

        private LipSyncTransportOptions ResolveLipSyncTransportOptions(
            IReadOnlyList<IConvaiCharacterAgent> characterList) =>
            LipSyncTransportResolver.Resolve(characterList, _eventHub);

        private string ResolveVideoTrackName()
        {
            if (UsesRoomConfigAsset && !string.IsNullOrWhiteSpace(_roomConfigAsset.VideoTrackName))
                return _roomConfigAsset.VideoTrackName;

            if (!string.IsNullOrWhiteSpace(VideoTrackName))
                return VideoTrackName;

            foreach (MonoBehaviour component in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component is IVisionPublisher publisher &&
                    !string.IsNullOrWhiteSpace(publisher.VideoTrackName))
                    return publisher.VideoTrackName;
            }

            return VideoPublishOptions.Default.TrackName;
        }
    }
}
