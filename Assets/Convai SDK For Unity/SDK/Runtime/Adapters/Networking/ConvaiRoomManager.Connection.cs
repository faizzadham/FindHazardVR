using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.Logging;
using Convai.Runtime.Networking.Media;
using Convai.Runtime.Room;
using UnityEngine;

namespace Convai.Runtime.Adapters.Networking
{
    public partial class ConvaiRoomManager
    {
        private readonly SemaphoreSlim _rosterMutationGate = new(1, 1);
        private readonly SemaphoreSlim _interactionTargetMutationGate = new(1, 1);
        private const int CanonicalRoutingRecoveryTimeoutSeconds = 30;

        internal ResolvedTurnTakingOptions CurrentResolvedTurnTakingOptions =>
            _currentResolvedTurnTakingOptions ?? ResolvedTurnTakingOptions.DefaultHandsFree;

        internal bool IsConversationInputModeTransitionInProgress => _conversationInputModeTransitionInProgress;

        public IConvaiOperation<RoomSession> ConnectAsync(CancellationToken cancellationToken = default) =>
            ConnectionCoordinator?.ConnectAsync(cancellationToken) ??
            ConvaiOperation<RoomSession>.Failed(
                new ConvaiOperationException(SessionErrorCodes.ConnectionFailed,
                    "[ConvaiRoomManager] ConnectAsync called before room coordinators were initialized."));

        public IConvaiOperation<RoomSession> ConnectAsync(
            RoomSessionConnectOptions options,
            CancellationToken cancellationToken = default)
        {
            RoomSessionConnectOptions pendingOptions = options?.Clone();
            if (pendingOptions != null && !TryQueuePendingConnectOptions(pendingOptions))
                return ConnectAsync(cancellationToken);

            IConvaiOperation<RoomSession> operation = ConnectAsync(cancellationToken);
            if (pendingOptions == null)
                return operation;

            return ConvaiOperation<RoomSession>.FromTask(
                ClearPendingConnectOptionsWhenUnusedAsync(operation.AsTask(), pendingOptions));
        }

        /// <inheritdoc />
        public IConvaiOperation<RoomSession> JoinMultiCharacterRoomAsync(
            MultiCharacterJoinOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null)
                return ConvaiOperation<RoomSession>.Failed(new ArgumentNullException(nameof(options)));

            return ConnectAsync(options.ToConnectOptions(), cancellationToken);
        }

        public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
            IConvaiCharacterAgent character,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<InteractionTargetResult>.FromTask(
                SetInteractionTargetCoreAsync(character, cancellationToken));

        private async Task<InteractionTargetResult> SetInteractionTargetCoreAsync(
            IConvaiCharacterAgent character,
            CancellationToken cancellationToken)
        {
            MultiCharacterRoomSession session = CurrentMultiCharacterSession ??
                throw new InvalidOperationException("No multi-character room session is active.");
            CharacterRoomMembership membership = session.FindByCharacter(character) ??
                throw new ArgumentException("The character is not a member of the current room.", nameof(character));
            return await SetInteractionTargetCoreAsync(session, membership, cancellationToken);
        }

        public IConvaiOperation<CharacterRosterUpdateResult> AddCharacterAsync(
            IConvaiCharacterAgent character,
            string characterSessionId = null,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<CharacterRosterUpdateResult>.FromTask(
                AddCharacterCoreAsync(character, characterSessionId, cancellationToken));

        internal Task<CharacterRosterUpdateResult> AddCharacterTrackedAsync(
            IConvaiCharacterAgent character,
            Action<MultiCharacterRoomSession, Task<CharacterRosterUpdateResult>> canonicalCommandRegistered,
            string characterSessionId = null,
            CancellationToken cancellationToken = default) =>
            AddCharacterCoreAsync(
                character,
                characterSessionId,
                cancellationToken,
                canonicalCommandRegistered);

        private async Task<CharacterRosterUpdateResult> AddCharacterCoreAsync(
            IConvaiCharacterAgent character,
            string characterSessionId,
            CancellationToken cancellationToken,
            Action<MultiCharacterRoomSession, Task<CharacterRosterUpdateResult>>
                canonicalCommandRegistered = null)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (string.IsNullOrWhiteSpace(character.CharacterId))
                throw new ArgumentException("The character must have a character ID.", nameof(character));

            RoomConnectionRuntimeAdapter.EnsureUnversionedRosterCharacter(character);

            MultiCharacterRoomSession session = CurrentMultiCharacterSession ??
                throw new InvalidOperationException("No multi-character room session is active.");
            if (session.FindByCharacter(character) != null)
                throw new ArgumentException(
                    "This local character instance is already a member of the current room. " +
                    "Use another instance when adding a clone.",
                    nameof(character));

            // The connect path checks the same ceiling on the whole roster at once. Adding one at a
            // time reaches it just as surely, and reaching it that way is the harder one to see: the
            // room worked for forty-nine characters and the fiftieth is where it stops.
            if (session.Characters.Count >= MultiCharacterRoomLimits.MaxCharacters)
                throw new InvalidOperationException(
                    MultiCharacterRoomLimits.DescribeOverflow(session.Characters.Count + 1));

            // And the same identity rule, for the same reason: the roster this joins is one the
            // connect path already proved clean, so the arriving character is the only one that can
            // break it. Refused here rather than in the room, where the collision would show up as
            // one character answering in another's place.
            if (MultiCharacterRosterIdentity.ConflictsWithRoster(
                    CollectRosterCharacters(session),
                    character,
                    out IConvaiCharacterAgent conflicting))
                throw new ArgumentException(
                    MultiCharacterRosterIdentity.DescribeDuplicate(conflicting, character),
                    nameof(character));

            return await UpdateRosterCoreAsync(
                session,
                character,
                string.IsNullOrWhiteSpace(characterSessionId) ? null : characterSessionId,
                null,
                null,
                cancellationToken,
                canonicalCommandRegistered);
        }

        /// <summary>
        ///     The characters a room's memberships stand for, as a plain list.
        /// </summary>
        /// <remarks>
        ///     Built rather than cached: a roster edit is a rare, deliberate call, and a cache of
        ///     the thing being edited is one more place for the room and the roster to disagree.
        ///     Memberships whose character has been destroyed are dropped — there is no identity
        ///     left to clash with.
        /// </remarks>
        private static List<IConvaiCharacterAgent> CollectRosterCharacters(MultiCharacterRoomSession session)
        {
            IReadOnlyList<CharacterRoomMembership> memberships = session.Characters;
            var characters = new List<IConvaiCharacterAgent>(memberships.Count);
            for (int i = 0; i < memberships.Count; i++)
                if (memberships[i]?.Character is { } member)
                    characters.Add(member);

            return characters;
        }

        public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
            IConvaiCharacterAgent character,
            string replacementTargetMembershipId = null,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<CharacterRosterUpdateResult>.FromTask(
                RemoveCharacterCoreAsync(character, replacementTargetMembershipId, cancellationToken));

        private async Task<CharacterRosterUpdateResult> RemoveCharacterCoreAsync(
            IConvaiCharacterAgent character,
            string replacementTargetMembershipId,
            CancellationToken cancellationToken)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            MultiCharacterRoomSession session = CurrentMultiCharacterSession ??
                throw new InvalidOperationException("No multi-character room session is active.");
            CharacterRoomMembership membership = session.FindByCharacter(character) ??
                throw new ArgumentException("The character is not a member of the current room.", nameof(character));
            return await RemoveCharacterCoreAsync(
                session,
                membership.MembershipId,
                replacementTargetMembershipId,
                cancellationToken);
        }

        public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
            string membershipId,
            string replacementTargetMembershipId = null,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<CharacterRosterUpdateResult>.FromTask(
                RemoveCharacterByMembershipCoreAsync(
                    membershipId,
                    replacementTargetMembershipId,
                    cancellationToken));

        internal Task<CharacterRosterUpdateResult> RemoveCharacterTrackedAsync(
            string membershipId,
            string replacementTargetMembershipId,
            Action<MultiCharacterRoomSession, Task<CharacterRosterUpdateResult>> canonicalCommandRegistered,
            CancellationToken cancellationToken = default) =>
            RemoveCharacterByMembershipCoreAsync(
                membershipId,
                replacementTargetMembershipId,
                cancellationToken,
                canonicalCommandRegistered);

        private async Task<CharacterRosterUpdateResult> RemoveCharacterByMembershipCoreAsync(
            string membershipId,
            string replacementTargetMembershipId,
            CancellationToken cancellationToken,
            Action<MultiCharacterRoomSession, Task<CharacterRosterUpdateResult>>
                canonicalCommandRegistered = null)
        {
            MultiCharacterRoomSession session = CurrentMultiCharacterSession ??
                throw new InvalidOperationException("No multi-character room session is active.");
            if (session.FindByMembershipId(membershipId) == null)
                throw new ArgumentException("The membership is not part of the current room.", nameof(membershipId));
            return await RemoveCharacterCoreAsync(
                session,
                membershipId,
                replacementTargetMembershipId,
                cancellationToken,
                canonicalCommandRegistered);
        }

        private async Task<CharacterRosterUpdateResult> RemoveCharacterCoreAsync(
            MultiCharacterRoomSession session,
            string membershipId,
            string replacementTargetMembershipId,
            CancellationToken cancellationToken,
            Action<MultiCharacterRoomSession, Task<CharacterRosterUpdateResult>>
                canonicalCommandRegistered = null)
        {
            if (!string.IsNullOrWhiteSpace(replacementTargetMembershipId) &&
                session.FindByMembershipId(replacementTargetMembershipId) == null)
                throw new ArgumentException(
                    "The replacement target is not part of the current room.",
                    nameof(replacementTargetMembershipId));
            if (string.Equals(membershipId, replacementTargetMembershipId, StringComparison.Ordinal))
                throw new ArgumentException(
                    "The replacement target cannot be the membership being removed.",
                    nameof(replacementTargetMembershipId));

            return await UpdateRosterCoreAsync(
                session,
                null,
                null,
                membershipId,
                replacementTargetMembershipId,
                cancellationToken,
                canonicalCommandRegistered);
        }

        private async Task<CharacterRosterUpdateResult> UpdateRosterCoreAsync(
            MultiCharacterRoomSession session,
            IConvaiCharacterAgent addedCharacter,
            string characterSessionId,
            string removedMembershipId,
            string replacementTargetMembershipId,
            CancellationToken cancellationToken,
            Action<MultiCharacterRoomSession, Task<CharacterRosterUpdateResult>>
                canonicalCommandRegistered = null)
        {
            await ConvaiTimers.WaitAsync(_rosterMutationGate, cancellationToken);
            ConversationTargetRoutingLease routingLease = null;
            bool releaseRosterMutationGate = true;
            try
            {
                EnsureCurrentMultiCharacterSession(session);
                ObserveConversationTargetSession(session);
                ValidateRosterMutation(
                    session,
                    addedCharacter,
                    removedMembershipId,
                    replacementTargetMembershipId);

                RTVIHandler handler = RtvHandler ??
                    throw new InvalidOperationException("The room data channel is not ready.");
                CharacterRoomMembership replacement = string.IsNullOrWhiteSpace(replacementTargetMembershipId)
                    ? null
                    : session.FindByMembershipId(replacementTargetMembershipId);
                CharacterRoomMembership removedMembership = string.IsNullOrWhiteSpace(removedMembershipId)
                    ? null
                    : session.FindByMembershipId(removedMembershipId);
                bool removesActiveTarget = !string.IsNullOrWhiteSpace(removedMembershipId) &&
                                           string.Equals(
                                               session.ActiveMembershipId,
                                               removedMembershipId,
                                               StringComparison.Ordinal);
                if (replacement != null || removesActiveTarget)
                    routingLease = BeginConversationTargetRouting(
                        replacement?.Character as ConvaiCharacter,
                        isTargetCommand: false);
                routingLease ??= new ConversationTargetRoutingLease(() => { });
                string commandId = Guid.NewGuid().ToString("N");
                using var timeoutCts = ConvaiTimers.CreateTimeoutSource(TimeSpan.FromSeconds(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);
                CharacterRosterCommandRegistration registration = session.RegisterTrackedRosterCommand(
                    commandId,
                    addedCharacter,
                    removedMembershipId,
                    linkedCts.Token);
                Task<CharacterRosterUpdateResult> response = registration.CallerCompletion;
                int commandRosterEpoch = session.RosterEpoch;
                routingLease.MarkCanonicalCommandRegistered();
                Task<CharacterRosterUpdateResult> canonicalOutcome = TrackCanonicalRoutingAsync(
                    session,
                    registration.CanonicalCompletion,
                    commandRosterEpoch,
                    "character-roster update",
                    routingLease,
                    reason => session.ClaimRosterCommandTimeout(commandId, reason));
                // Missing-command-id responses can only be correlated while exactly one roster
                // command is pending. Transfer semaphore ownership to the canonical lifetime so a
                // caller timeout cannot register a second ambiguous command.
                releaseRosterMutationGate = false;
                _ = ReleaseRosterMutationGateAfterCanonicalAsync(canonicalOutcome);
                ObserveCanonicalRoutingFault(canonicalOutcome);
                if (addedCharacter != null)
                    _ = ObserveRosterAddAsync(session, addedCharacter, canonicalOutcome);
                else if (removedMembership != null)
                    _ = ObserveRosterRemoveAsync(session, removedMembership, canonicalOutcome);

                IReadOnlyList<RTVICharacterRosterUpdate.CharacterRosterAddition> additions =
                    addedCharacter == null
                        ? Array.Empty<RTVICharacterRosterUpdate.CharacterRosterAddition>()
                        : new[]
                        {
                            new RTVICharacterRosterUpdate.CharacterRosterAddition(
                                addedCharacter.CharacterId,
                                string.IsNullOrWhiteSpace(characterSessionId) ? null : characterSessionId)
                        };
                IReadOnlyList<string> removals = string.IsNullOrWhiteSpace(removedMembershipId)
                    ? Array.Empty<string>()
                    : new[] { removedMembershipId };
                try
                {
                    try
                    {
                        canonicalCommandRegistered?.Invoke(session, canonicalOutcome);
                        await handler.SendDataAsync(new RTVICharacterRosterUpdate(
                            commandId,
                            session.RoomSessionId,
                            commandRosterEpoch,
                            additions,
                            removals,
                            replacementTargetMembershipId));
                    }
                    catch (Exception exception)
                    {
                        session.FailRosterCommand(commandId, exception);
                        try
                        {
                            await response;
                        }
                        catch
                        {
                            // Observe the caller view before preserving the original send failure.
                        }
                        throw;
                    }
                    return await response;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "Timed out waiting for the character-roster-update acknowledgement.");
                }
                finally
                {
                    if (!response.IsCompleted) linkedCts.Cancel();
                    routingLease?.ReleaseAfterCallerCompletion();
                }
            }
            finally
            {
                routingLease?.ReleaseAfterCallerCompletion();
                if (releaseRosterMutationGate)
                    _rosterMutationGate.Release();
            }
        }

        private async Task ReleaseRosterMutationGateAfterCanonicalAsync(Task canonicalOutcome)
        {
            try
            {
                await canonicalOutcome;
            }
            catch
            {
                // The canonical observers own reporting. This continuation owns only serialization.
            }
            finally
            {
                _rosterMutationGate.Release();
            }
        }

        private void ValidateRosterMutation(
            MultiCharacterRoomSession session,
            IConvaiCharacterAgent addedCharacter,
            string removedMembershipId,
            string replacementTargetMembershipId)
        {
            if (addedCharacter != null && session.FindByCharacter(addedCharacter) != null)
                throw new InvalidOperationException(
                    "The local character was added while this roster update was waiting.");
            if (!string.IsNullOrWhiteSpace(removedMembershipId) &&
                session.FindByMembershipId(removedMembershipId) == null)
                throw new InvalidOperationException(
                    "The character membership was removed while this roster update was waiting.");
            if (!string.IsNullOrWhiteSpace(replacementTargetMembershipId) &&
                session.FindByMembershipId(replacementTargetMembershipId) == null)
                throw new InvalidOperationException(
                    "The replacement target was removed while this roster update was waiting.");
            if (!string.IsNullOrWhiteSpace(removedMembershipId) &&
                string.Equals(removedMembershipId, replacementTargetMembershipId, StringComparison.Ordinal))
                throw new ArgumentException(
                    "The replacement target cannot be the membership being removed.",
                    nameof(replacementTargetMembershipId));
        }

        public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
            string membershipId,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<InteractionTargetResult>.FromTask(
                SetInteractionTargetByMembershipCoreAsync(membershipId, cancellationToken));

        /// <summary>
        ///     Starts a target change while exposing the canonical response separately from the
        ///     caller's bounded wait. Used by the manager to keep input closed across a timeout.
        /// </summary>
        internal Task<InteractionTargetResult> SetInteractionTargetTrackedAsync(
            string membershipId,
            Action<MultiCharacterRoomSession, Task<InteractionTargetResult>> canonicalCommandRegistered,
            CancellationToken cancellationToken = default) =>
            SetInteractionTargetByMembershipCoreAsync(
                membershipId,
                cancellationToken,
                canonicalCommandRegistered);

        /// <summary>
        ///     Fails closed when a registered target command never receives an authoritative
        ///     response. Input is made unavailable before the ambiguous session is retired.
        /// </summary>
        private bool RecoverAmbiguousConversationRouting(
            MultiCharacterRoomSession session,
            int commandEpoch,
            int currentEpoch,
            string commandDescription)
        {
            if (AgentRegistry is not IMultiCharacterSessionRegistry registry ||
                !registry.TryClaimMultiCharacterSessionForRecovery(session))
                return false;

            return ConversationTargetAmbiguityRecovery.RecoverIfCurrent(
                true,
                commandEpoch,
                currentEpoch,
                () => UpdateSessionState(SessionState.Disconnecting),
                session.Retire,
                () =>
                {
                    // A client disconnect: the ambiguous room is abandoned, never rejoined.
                    Task disconnect = DisconnectAsync().AsTask();
                    _ = disconnect.ContinueWith(
                        static task => ConvaiLogger.Error(
                            $"Ambiguous conversation-routing recovery could not disconnect cleanly: " +
                            $"{task.Exception?.GetBaseException().Message}",
                            LogCategory.SDK),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                });
        }

        /// <summary>
        ///     Owns a routing lease until the canonical response, a newer authoritative epoch, or
        ///     fail-closed room recovery resolves the ambiguity.
        /// </summary>
        private async Task<T> TrackCanonicalRoutingAsync<T>(
            MultiCharacterRoomSession session,
            Task<T> canonicalCompletion,
            int commandEpoch,
            string commandDescription,
            ConversationTargetRoutingLease routingLease,
            Func<Exception, CanonicalRoutingTimeoutDisposition> claimUnresolvedCommand,
            bool releaseOlderTargetCommandsOnCanonicalResponse = false)
        {
            bool recovered = false;
            bool authoritativeTargetResponse = false;
            try
            {
                Task completed = await ConvaiTimers.WhenAny(
                    canonicalCompletion,
                    ConvaiTimers.Delay(TimeSpan.FromSeconds(CanonicalRoutingRecoveryTimeoutSeconds)));
                // The deadline and acknowledgement can become runnable together. Prefer the
                // authoritative response whenever it has actually completed rather than turning a
                // scheduler tie into an unnecessary disconnect.
                if (ReferenceEquals(completed, canonicalCompletion) || canonicalCompletion.IsCompleted)
                {
                    try
                    {
                        T result = await canonicalCompletion;
                        authoritativeTargetResponse = releaseOlderTargetCommandsOnCanonicalResponse;
                        return result;
                    }
                    catch (InteractionTargetCommandException)
                    {
                        // A rejected target response may still carry a newer authoritative route.
                        authoritativeTargetResponse = releaseOlderTargetCommandsOnCanonicalResponse;
                        throw;
                    }
                }

                var timeout = new TimeoutException(
                    $"No authoritative {commandDescription} response arrived within " +
                    $"{CanonicalRoutingRecoveryTimeoutSeconds} seconds.");
                // Claiming and checking the epoch happen under the session's lock. This closes the
                // deadline race where an acknowledgement applied state between IsCompleted and a
                // separate epoch snapshot, only to have recovery disconnect the reconciled room.
                CanonicalRoutingTimeoutDisposition disposition =
                    claimUnresolvedCommand?.Invoke(timeout) ??
                    CanonicalRoutingTimeoutDisposition.RequiresRecovery;
                if (disposition == CanonicalRoutingTimeoutDisposition.AlreadyResolved)
                {
                    try
                    {
                        T result = await canonicalCompletion;
                        authoritativeTargetResponse = releaseOlderTargetCommandsOnCanonicalResponse;
                        return result;
                    }
                    catch (InteractionTargetCommandException)
                    {
                        authoritativeTargetResponse = releaseOlderTargetCommandsOnCanonicalResponse;
                        throw;
                    }
                }

                if (disposition == CanonicalRoutingTimeoutDisposition.RequiresRecovery)
                    recovered = RecoverAmbiguousConversationRouting(
                        session,
                        commandEpoch,
                        commandEpoch,
                        commandDescription);

                timeout = new TimeoutException(
                    timeout.Message + " " +
                    (recovered
                        ? "The ambiguous room was retired and disconnected before input reopened."
                        : disposition == CanonicalRoutingTimeoutDisposition.Superseded
                            ? "A newer authoritative target response superseded it."
                            : "Its room is no longer current."));
                throw timeout;
            }
            finally
            {
                if (recovered)
                    routingLease.ReleaseAfterAmbiguousRecovery();
                else if (authoritativeTargetResponse)
                    routingLease.ReleaseAfterAuthoritativeTargetResponse();
                else
                    routingLease.ReleaseAfterCanonicalCompletion();
            }
        }

        private static void ObserveCanonicalRoutingFault(Task canonicalOutcome) =>
            _ = canonicalOutcome?.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        private async Task<InteractionTargetResult> SetInteractionTargetByMembershipCoreAsync(
            string membershipId,
            CancellationToken cancellationToken,
            Action<MultiCharacterRoomSession, Task<InteractionTargetResult>> canonicalCommandRegistered = null)
        {
            MultiCharacterRoomSession session = CurrentMultiCharacterSession ??
                throw new InvalidOperationException("No multi-character room session is active.");
            CharacterRoomMembership membership = session.FindByMembershipId(membershipId) ??
                throw new ArgumentException("The membership is not part of the current room.", nameof(membershipId));
            return await SetInteractionTargetCoreAsync(
                session,
                membership.MembershipId,
                membership.Character as ConvaiCharacter,
                cancellationToken,
                canonicalCommandRegistered);
        }

        public IConvaiOperation<InteractionTargetResult> ClearInteractionTargetAsync(
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<InteractionTargetResult>.FromTask(
                ClearInteractionTargetCoreAsync(cancellationToken));

        private async Task<InteractionTargetResult> ClearInteractionTargetCoreAsync(
            CancellationToken cancellationToken)
        {
            MultiCharacterRoomSession session = CurrentMultiCharacterSession ??
                throw new InvalidOperationException("No multi-character room session is active.");
            return await SetInteractionTargetCoreAsync(session, null, null, cancellationToken);
        }

        private async Task<InteractionTargetResult> SetInteractionTargetCoreAsync(
            MultiCharacterRoomSession session,
            CharacterRoomMembership membership,
            CancellationToken cancellationToken) =>
            await SetInteractionTargetCoreAsync(
                session,
                membership.MembershipId,
                membership.Character as ConvaiCharacter,
                cancellationToken);

        private async Task<InteractionTargetResult> SetInteractionTargetCoreAsync(
            MultiCharacterRoomSession session,
            string targetMembershipId,
            ConvaiCharacter pendingTarget,
            CancellationToken cancellationToken,
            Action<MultiCharacterRoomSession, Task<InteractionTargetResult>> canonicalCommandRegistered = null)
        {
            await ConvaiTimers.WaitAsync(_interactionTargetMutationGate, cancellationToken);
            ConversationTargetRoutingLease routingLease = null;
            try
            {
                EnsureCurrentMultiCharacterSession(session);
                if (!string.IsNullOrWhiteSpace(targetMembershipId) &&
                    session.FindByMembershipId(targetMembershipId) == null)
                    throw new InvalidOperationException(
                        "The interaction target was removed while this update was waiting.");

                RTVIHandler handler = RtvHandler ??
                    throw new InvalidOperationException("The room data channel is not ready.");
                routingLease = BeginConversationTargetRouting(
                    pendingTarget,
                    isTargetCommand: true);
                string commandId = Guid.NewGuid().ToString("N");
                using var timeoutCts = ConvaiTimers.CreateTimeoutSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);
                InteractionTargetCommandRegistration registration =
                    session.RegisterTrackedTargetCommand(commandId, linkedCts.Token);
                Task<InteractionTargetResult> response = registration.CallerCompletion;
                int commandRouteEpoch = session.RouteEpoch;
                routingLease.MarkCanonicalCommandRegistered();
                Task<InteractionTargetResult> canonicalOutcome = TrackCanonicalRoutingAsync(
                    session,
                    registration.CanonicalCompletion,
                    commandRouteEpoch,
                    "interaction-target",
                    routingLease,
                    reason => session.ClaimTargetCommandTimeout(
                        commandId,
                        commandRouteEpoch,
                        reason),
                    releaseOlderTargetCommandsOnCanonicalResponse: true);
                Task<InteractionTargetResult> publishedCanonicalOutcome =
                    ObserveCanonicalTargetCommandAsync(session, canonicalOutcome);
                ObserveCanonicalRoutingFault(publishedCanonicalOutcome);
                try
                {
                    try
                    {
                        // Manager-level failure observers await the publication wrapper, not the
                        // raw canonical task. A rejected response that also carries a newer route
                        // therefore always publishes that authoritative route before Failed.
                        canonicalCommandRegistered?.Invoke(session, publishedCanonicalOutcome);
                        await handler.SendDataAsync(new RTVIInteractionTarget(
                            commandId,
                            session.RoomSessionId,
                            targetMembershipId,
                            commandRouteEpoch));
                    }
                    catch (Exception exception)
                    {
                        session.FailTargetCommand(commandId, exception);
                        throw;
                    }
                    return await response;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for the interaction-target acknowledgement.");
                }
                finally
                {
                    if (!response.IsCompleted) linkedCts.Cancel();
                    routingLease.ReleaseAfterCallerCompletion();
                }
            }
            finally
            {
                routingLease?.ReleaseAfterCallerCompletion();
                _interactionTargetMutationGate.Release();
            }
        }

        private void EnsureCurrentMultiCharacterSession(MultiCharacterRoomSession session)
        {
            if (!ReferenceEquals(CurrentMultiCharacterSession, session))
                throw new InvalidOperationException(
                    "The multi-character room changed while this update was waiting.");
        }

        public IConvaiOperation<Unit> DisconnectAsync(CancellationToken cancellationToken = default) =>
            ConvaiOperation<Unit>.FromTask(DisconnectAsyncCore(cancellationToken));

        private async Task<Unit> DisconnectAsyncCore(CancellationToken cancellationToken)
        {
            PreparePushToTalkControllersForDisconnect();
            ResetPttSession();

            if (ConnectionCoordinator == null)
                return Unit.Value;

            return await ConnectionCoordinator.DisconnectAsync(cancellationToken).AsTask();
        }

        public void DisconnectFromRoom()
        {
            DisconnectAsync().AsTask().ContinueWith(
                static t => ConvaiLogger.Error(
                    $"DisconnectAsync failed: {t.Exception?.GetBaseException().Message}",
                    LogCategory.SDK),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void HandleCoordinatorStateChanged(SessionStateChanged stateChanged)
        {
            OnSessionStateChanged?.Invoke(stateChanged);

            if (stateChanged.NewState == SessionState.Connected &&
                stateChanged.OldState != SessionState.Connected)
            {
                SubscribeToRtvMetrics();
                RequestAutoStartMicrophone("session-connected");
                Connected?.Invoke();
                return;
            }

            if (stateChanged.NewState is SessionState.Disconnected or SessionState.Error)
                ClearActiveSessionTurnTakingState();
        }

        private static string DescribeSessionState(SessionState state) => state switch
        {
            SessionState.Connecting => "transport connecting",
            SessionState.Connected => "character ready",
            SessionState.Reconnecting => "transport reconnecting",
            SessionState.Disconnecting => "disconnecting",
            SessionState.Disconnected => "disconnected",
            SessionState.Error => "error",
            _ => state.ToString()
        };

        private void SubscribeToRtvMetrics()
        {
            UnsubscribeFromRtvMetrics();
            RTVIHandler handler = _convaiRoomController?.RTVIHandler;
            if (handler == null) return;
            _metricsRtviHandler = handler;
            handler.OnMetricsReceived += ForwardRtvMetrics;
        }

        private void UnsubscribeFromRtvMetrics()
        {
            if (_metricsRtviHandler == null) return;

            _metricsRtviHandler.OnMetricsReceived -= ForwardRtvMetrics;
            _metricsRtviHandler = null;
        }

        private void ForwardRtvMetrics(RTVIMetricsPayload payload)
        {
            if (EffectiveDebug)
            {
                LogRtvMetrics(payload);
                if (_debugMetricsFileWriter != null && payload != null)
                {
                    var parts = new List<string>();
                    if (payload.Ttfb != null && payload.Ttfb.HasValues) parts.Add($"ttfb={payload.Ttfb}");
                    if (payload.Processing != null && payload.Processing.HasValues)
                        parts.Add($"processing={payload.Processing}");
                    if (payload.Custom != null && payload.Custom.HasValues) parts.Add($"custom={payload.Custom}");
                    if (parts.Count > 0)
                        _debugMetricsFileWriter.WriteLine("rtvi_metrics", string.Join(" ", parts));
                }
            }

            OnRtvMetricsReceived?.Invoke(payload);
        }

        private void LogRtvMetrics(RTVIMetricsPayload payload)
        {
            if (payload == null) return;

            bool hasTtfb = payload.Ttfb != null && payload.Ttfb.HasValues;
            bool hasProcessing = payload.Processing != null && payload.Processing.HasValues;
            bool hasCustom = payload.Custom != null && payload.Custom.HasValues;
            if (!hasTtfb && !hasProcessing && !hasCustom) return;

            var parts = new List<string>();
            if (hasTtfb) parts.Add($"ttfb={payload.Ttfb}");
            if (hasProcessing) parts.Add($"processing={payload.Processing}");
            if (hasCustom) parts.Add($"custom={payload.Custom}");
            ConvaiLogger.Info($"RTVI metrics: {string.Join(" | ", parts)}", LogCategory.Transport);
        }

        private void HandleUnexpectedRoomDisconnected()
        {
            ConvaiLogger.Info(
                "Room disconnected unexpectedly; clearing runtime media/session state.",
                LogCategory.SDK);
            ResetConnectionScopedRuntimeState();
            _roomConnectionRuntimeAdapter?.ClearResolvedCredentials();
            if (_roomDisconnectRuntimeAdapter != null)
            {
                _roomDisconnectRuntimeAdapter.HandleUnexpectedDisconnect(
                    CurrentState != SessionState.Disconnected,
                    "Handled unexpected room disconnect");
                return;
            }

            _bargeInCoordinator?.ResetForConnectionBoundary();
            try
            {
                _audioTrackManager?.SetMicMuted(true);
                _audioTrackManager?.ClearState();
                CompleteDisconnectionTracking(
                    DisconnectReason.TransportError,
                    CurrentState != SessionState.Disconnected,
                    "Handled unexpected room disconnect");
            }
            finally
            {
                _bargeInCoordinator?.ResetForConnectionBoundary();
            }
        }

        private void HandlePlayerTextMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                ConvaiLogger.Warning("HandlePlayerTextMessage received empty text; ignoring.",
                    LogCategory.SDK);
                return;
            }

            if (!IsConnected)
            {
                ConvaiLogger.Warning(
                    "HandlePlayerTextMessage called while not connected; message dropped.",
                    LogCategory.SDK);
                return;
            }

            if (RtvHandler == null)
            {
                ConvaiLogger.Warning(
                    "HandlePlayerTextMessage: RtvHandler is null; message dropped.",
                    LogCategory.SDK);
                return;
            }

            string messageId = Guid.NewGuid().ToString("N");
            _playerSession?.PublishTypedText(text, messageId);
            RtvHandler.SendData(new RTVIUserTextMessage(text, messageId));
            ConvaiLogger.Debug($"Sent user text message: {text}", LogCategory.SDK);
        }

        private void HandleRemoteAudioTrackSubscribed(IRemoteAudioTrack audioTrack, string participantSid,
            string participantIdentity)
        {
            _characterLifecycleCoordinator?.HandleRemoteAudioTrackSubscribed(
                audioTrack,
                participantSid,
                participantIdentity);
            PublishRecoveredCharacterReady(
                _characterLifecycleCoordinator?.TryRecoverCharacterReadyFromAudioTrack(
                    participantIdentity,
                    participantSid),
                "audio-track");
        }

        private void HandleRemoteAudioTrackUnsubscribed(string participantSid, string characterId) =>
            _characterLifecycleCoordinator?.HandleRemoteAudioTrackUnsubscribed(participantSid);

        private void RequestAutoStartMicrophone(string reason)
        {
            if (_autoStartMicrophoneCompleted)
            {
                ConvaiLogger.Debug(
                    $"Auto-start microphone already completed for current connection; ignoring trigger ({reason}).",
                    LogCategory.SDK);
                return;
            }

            if (_autoStartMicrophonePending)
            {
                ConvaiLogger.Debug(
                    $"Auto-start microphone already scheduled; ignoring duplicate trigger ({reason}).",
                    LogCategory.SDK);
                return;
            }

            _autoStartMicrophonePending = true;
            _autoStartMicrophoneRequestId++;
            _autoStartMicrophoneCoroutine = StartCoroutine(AutoStartMicrophoneCoroutine(_autoStartMicrophoneRequestId));
            ConvaiLogger.Debug(
                $"Scheduled auto-start microphone request {_autoStartMicrophoneRequestId} ({reason}).",
                LogCategory.SDK);
        }

        private void ResetAutoStartMicrophoneState()
        {
            _autoStartMicrophoneRequestId++;
            _autoStartMicrophoneCompleted = false;
            _autoStartMicrophonePending = false;

            if (_autoStartMicrophoneCoroutine == null)
                return;

            StopCoroutine(_autoStartMicrophoneCoroutine);
            _autoStartMicrophoneCoroutine = null;
        }

        /// <summary>
        ///     How long auto-start waits before warning that the room still has not confirmed its
        ///     first character. The microphone remains closed after the warning.
        /// </summary>
        /// <remarks>
        ///     Generous, because the cost of waiting is a late microphone and the cost of not
        ///     waiting is speech nobody hears. It only ever elapses on a room that is unusually
        ///     slow or failed to announce its character, which the warning then reports once.
        /// </remarks>
        private const float AutoStartMicrophoneReadinessWaitSeconds = 10f;

        private IEnumerator AutoStartMicrophoneCoroutine(int requestId)
        {
            yield return new WaitForSeconds(_reconnectPolicy.AutoMicStartDelaySeconds);

            if (requestId != _autoStartMicrophoneRequestId)
                yield break;

            // Blocked browser audio playback does not block the microphone: capture needs no gesture, and in
            // Chrome a granted microphone is itself what lets the character's audio autoplay. A scene click or
            // EnableAudioAndStartListening() starts playback when the browser still refuses it.

            if (!IsConnected)
            {
                _autoStartMicrophonePending = false;
                _autoStartMicrophoneCoroutine = null;
                yield break;
            }

            if (!_currentResolvedTurnTakingOptions.ShouldAutoStartMicrophoneAfterConnect)
            {
                ConvaiLogger.Debug(
                    "Skipping auto-start microphone because the resolved turn-taking policy " +
                    "uses open-on-first-press push-to-talk startup.",
                    LogCategory.SDK);
                _autoStartMicrophonePending = false;
                _autoStartMicrophoneCoroutine = null;
                yield break;
            }

            // The microphone must not open before the character can hear it. A room that is
            // connected has not necessarily had its character announced by the service, and speech
            // captured in that window reaches nobody — the player talks and is answered by silence,
            // with nothing anywhere saying why. A single-character room needs no wait: its session
            // only reaches Connected once the character's own readiness signal arrives.
            float readinessDeadline = Time.realtimeSinceStartup + AutoStartMicrophoneReadinessWaitSeconds;
            bool readinessWarningEmitted = false;
            while (true)
            {
                if (requestId != _autoStartMicrophoneRequestId)
                    yield break;

                MultiCharacterRoomSession session = CurrentMultiCharacterSession;
                AutoStartMicrophoneReadinessAction action = AutoStartMicrophoneReadinessGate.Decide(
                    IsConnected,
                    session != null,
                    session?.IsReady ?? false,
                    Time.realtimeSinceStartup >= readinessDeadline,
                    readinessWarningEmitted);
                if (action == AutoStartMicrophoneReadinessAction.OpenMicrophone)
                    break;
                if (action == AutoStartMicrophoneReadinessAction.Abort)
                {
                    _autoStartMicrophonePending = false;
                    _autoStartMicrophoneCoroutine = null;
                    yield break;
                }

                if (action == AutoStartMicrophoneReadinessAction.WarnAndContinueWaiting)
                {
                    readinessWarningEmitted = true;
                    ConvaiLogger.Warning(
                        "Still waiting for the room to confirm its first character after " +
                        $"{AutoStartMicrophoneReadinessWaitSeconds:0.#}s. The microphone remains " +
                        "closed and will open when that character is ready.",
                        LogCategory.SDK);
                }

                yield return null;
            }

            if (requestId != _autoStartMicrophoneRequestId)
                yield break;

            int microphoneIndex = 0;
            if (_runtimeSettingsService != null && _microphoneDeviceService != null)
            {
                string preferredDeviceId = _runtimeSettingsService.Current.PreferredMicrophoneDeviceId;
                int resolvedIndex = _microphoneDeviceService.ResolvePreferredDeviceIndex(preferredDeviceId);
                if (resolvedIndex >= 0) microphoneIndex = resolvedIndex;
            }

            bool startMuted = _currentResolvedTurnTakingOptions.ShouldStartMutedAfterAutoStart;
            if (startMuted)
            {
                _conversationRoutingMicrophoneGate.AcceptPolicyMuteAfterConnectionBoundary();
                ApplyEffectiveMicMute(true);
            }

            StartListeningCoreAsync(
                    microphoneIndex,
                    restoreUserMute: !startMuted)
                .AsTask().ContinueWith(
                static t => ConvaiLogger.Error(
                    $"StartListeningAsync failed: {t.Exception?.GetBaseException().Message}",
                    LogCategory.SDK),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.FromCurrentSynchronizationContext());

            _autoStartMicrophoneCompleted = true;
            _autoStartMicrophonePending = false;
            _autoStartMicrophoneCoroutine = null;
        }

        private void UpdateSessionState(SessionState newState, SessionError? error = null)
        {
            // Recovery marks the room unavailable synchronously before the disconnect adapter gets
            // past microphone teardown. The adapter then asks for the same state; do not force and
            // republish an invalid Disconnecting -> Disconnecting transition.
            if (CurrentState == newState && error == null) return;

            if (newState != SessionState.Connected)
            {
                _sessionDiagnostics?.UpdateSessionState(newState, error);
                return;
            }

            // Every Connected notification fans out from the transition below: event hub
            // subscribers in subscription order, and through the coordinator OnSessionStateChanged
            // and Connected. Any of them may answer with SetConversationInputModeAsync, which is
            // refused until the prepared turn-taking state is live, so promote it before anyone is
            // told. This is the only place a Connected transition is requested.
            ConversationInputMode previousMode = ActiveConversationInputMode;
            _hasConnectedSessionTurnTakingState = _sessionTurnTakingSourceOptions != null;
            ConversationInputMode sessionMode = ActiveConversationInputMode;
            _sessionDiagnostics?.UpdateSessionState(newState, error);

            // Announce the session's mode only after the transition, so its handlers can switch it
            // too. A Connected listener that already switched the mode has announced the new one.
            if (ActiveConversationInputMode == sessionMode)
                RaiseConversationInputModeChangedIfNeeded(previousMode, sessionMode);
        }

        private void RecordConnectionSuccess(string roomName, string characterSessionId, string sessionId,
            string characterId)
        {
            IConvaiCharacterAgent activeCharacter = _activeCharacter;
            bool enableSessionResume = activeCharacter?.EnableSessionResume ?? false;
            ResetConnectionScopedRuntimeState();
            ObserveConversationTargetSession(CurrentMultiCharacterSession);
            _lastSessionErrorCode = null;
            _lastSessionErrorMessage = null;
            LastSuccessfulConnectionUtc = DateTime.UtcNow;
            _connectionContext = _sessionDiagnostics?.RecordConnectionSuccess(
                roomName,
                characterSessionId,
                sessionId,
                characterId,
                enableSessionResume) ?? ConnectionContext.Empty;
            RequestAutoStartMicrophone("room-connected");
        }

        private void RecordConnectionFailure(ConnectionFailure failure)
        {
            ClearActiveSessionTurnTakingState();
            _lastSessionErrorCode = failure.Code;
            _lastSessionErrorMessage = failure.Message;
            SessionError sessionError = _sessionDiagnostics?.RecordConnectionFailure(failure) ??
                                        failure.ToSessionError(CurrentSessionId);
            OnSessionError?.Invoke(sessionError);
        }

        private ReconnectPolicy ResolveConfiguredReconnectPolicy() =>
            CreateConfiguredReconnectPolicy();

        private void CompleteDisconnectionTracking(
            DisconnectReason reason,
            bool updateSessionState,
            string completionMessage)
        {
            ResetConnectionScopedRuntimeState();
            ClearActiveSessionTurnTakingState();
            _connectionContext = _sessionDiagnostics?.CompleteDisconnectionTracking(
                _connectionContext,
                reason,
                updateSessionState,
                completionMessage) ?? ConnectionContext.Empty;
        }

        private void ResetConnectionScopedRuntimeState()
        {
            ResetPttSession();
            ResetAutoStartMicrophoneState();
            ResetConversationTargetRoutingState();
            _characterLifecycleCoordinator?.ResetRecoveredReadinessState();
        }

        internal RoomSessionConnectOptions ConsumePendingConnectOptions()
        {
            lock (_connectOptionsLock)
            {
                RoomSessionConnectOptions options = _pendingConnectOptions;
                _pendingConnectOptions = null;
                return options;
            }
        }

        private bool TryQueuePendingConnectOptions(RoomSessionConnectOptions pendingOptions)
        {
            if (pendingOptions == null)
                return false;

            lock (_connectOptionsLock)
            {
                if (_pendingConnectOptions != null)
                    return false;

                _pendingConnectOptions = pendingOptions;
                return true;
            }
        }

        private async Task<RoomSession> ClearPendingConnectOptionsWhenUnusedAsync(
            Task<RoomSession> connectTask,
            RoomSessionConnectOptions pendingOptions)
        {
            try
            {
                return await connectTask;
            }
            finally
            {
                ClearPendingConnectOptionsIfUnconsumed(pendingOptions);
            }
        }

        private void ClearPendingConnectOptionsIfUnconsumed(RoomSessionConnectOptions pendingOptions)
        {
            if (pendingOptions == null)
                return;

            lock (_connectOptionsLock)
            {
                if (ReferenceEquals(_pendingConnectOptions, pendingOptions))
                {
                    pendingOptions.ClearExplicitAuthToken();
                    _pendingConnectOptions = null;
                }
            }
        }

        internal void SetCurrentResolvedTurnTakingOptions(ResolvedTurnTakingOptions options) =>
            _currentResolvedTurnTakingOptions = options ?? ResolvedTurnTakingOptions.DefaultHandsFree;

        internal void PrepareSessionTurnTakingState(
            TurnTakingOptions sourceOptions,
            ResolvedTurnTakingOptions resolvedOptions)
        {
            _sessionTurnTakingSourceOptions = sourceOptions?.Clone();
            _currentResolvedTurnTakingOptions = resolvedOptions ?? ResolvedTurnTakingOptions.DefaultHandsFree;
        }

        internal void UpdateConnectedSessionTurnTakingState(
            TurnTakingOptions sourceOptions,
            ResolvedTurnTakingOptions resolvedOptions)
        {
            ConversationInputMode previousMode = ActiveConversationInputMode;
            _sessionTurnTakingSourceOptions = sourceOptions?.Clone();
            _currentResolvedTurnTakingOptions = resolvedOptions ?? ResolvedTurnTakingOptions.DefaultHandsFree;
            _hasConnectedSessionTurnTakingState = _sessionTurnTakingSourceOptions != null;
            RaiseConversationInputModeChangedIfNeeded(previousMode, ActiveConversationInputMode);
        }

        private void ClearActiveSessionTurnTakingState()
        {
            ConversationInputMode previousMode = ActiveConversationInputMode;
            _sessionTurnTakingSourceOptions = null;
            _hasConnectedSessionTurnTakingState = false;
            _currentResolvedTurnTakingOptions = ResolvedTurnTakingOptions.DefaultHandsFree;
            RaiseConversationInputModeChangedIfNeeded(previousMode, ActiveConversationInputMode);
        }

        private void RaiseConversationInputModeChangedIfNeeded(
            ConversationInputMode previousMode,
            ConversationInputMode nextMode)
        {
            if (previousMode == nextMode)
                return;

            ConversationInputModeChanged?.Invoke(nextMode);
        }

        private void OnSessionStateMachineStateChanged(SessionStateChanged stateChanged)
        {
            _sessionDiagnostics?.HandleStateChanged(stateChanged);
            ConvaiLogger.Debug(
                $"Session lifecycle transition: {DescribeSessionState(stateChanged.OldState)} -> {DescribeSessionState(stateChanged.NewState)}",
                LogCategory.SDK);
            _roomConnectionRuntimeAdapter?.NotifyStateChanged(stateChanged);
        }

        private void HandleCharacterReadyEvent(CharacterReady readyEvent)
        {
            // The service said it itself, so any recovery armed for this character has served its
            // purpose and must not fire behind the real signal.
            _characterLifecycleCoordinator?.DisarmRecovery(readyEvent.MembershipId);
            _characterLifecycleCoordinator?.HandleCharacterReady(readyEvent);
        }

        /// <summary>
        ///     Wakes an armed character-ready recovery after its grace, so a readiness signal that
        ///     never arrives still resolves. See <see cref="CharacterReadyRecoveryPolicy" />.
        /// </summary>
        private void ScheduleCharacterReadyRecoveryRecheck(string membershipId)
        {
            if (string.IsNullOrEmpty(membershipId) || !isActiveAndEnabled) return;
            StartCoroutine(CompleteCharacterReadyRecoveryAfterGrace(membershipId));
        }

        private IEnumerator CompleteCharacterReadyRecoveryAfterGrace(string membershipId)
        {
            // Realtime, because a paused or slowed game does not slow the service down.
            yield return new WaitForSecondsRealtime(
                (float)CharacterReadyRecoveryPolicy.DefaultGraceSeconds);

            CharacterReady? recovered = _characterLifecycleCoordinator?.TryCompleteArmedRecovery(membershipId);
            if (recovered.HasValue)
            {
                // Worth saying plainly: on a healthy connection this never fires, so seeing it means
                // the service's own readiness signal did not arrive and the grace is what unblocked
                // the character.
                {
                    if (_logger?.IsEnabled(LogLevel.Warning, LogCategory.SDK) == true)
                        _logger?.Warning(
                        $"'{recovered.Value.CharacterId}' was marked ready without a readiness signal " +
                        $"from the service, after waiting {CharacterReadyRecoveryPolicy.DefaultGraceSeconds:0.#}s. " +
                        "The conversation continues; if this repeats, the room is starting characters " +
                        "without announcing them.",
                        LogCategory.SDK);
                }
                PublishRecoveredCharacterReady(recovered, "grace-expired");
            }
        }

        private void HandleCharacterSpeechEvidence(CharacterSpeechStateChanged speechEvent)
        {
            _bargeInCoordinator?.ObserveCharacterSpeech(speechEvent);

            if (!speechEvent.IsSpeaking)
                return;

            PublishRecoveredCharacterReady(
                _characterLifecycleCoordinator?.TryRecoverCharacterReadyFromSpeech(speechEvent),
                "speech");
        }

        private void HandlePlayerSpeakingEvidence(PlayerSpeakingStateChanged speakingEvent)
        {
            if (!speakingEvent.IsSpeaking)
                return;

            _clientLatencyMetricsCollector?.RecordBargeInMarker(
                BargeInMarker.Create(
                    BargeInMarkerStage.ServerSpeechStarted,
                    BargeInTrigger.ServerVoiceActivity));
            _bargeInCoordinator?.Commit(BargeInTrigger.ServerVoiceActivity);
        }

        private void HandleClientVoiceActivity(ClientVoiceActivityStateChanged stateChanged)
        {
            switch (stateChanged.Stage)
            {
                case ClientVoiceActivityStage.Candidate:
                    _clientLatencyMetricsCollector?.RecordBargeInMarker(
                        BargeInMarker.Create(
                            BargeInMarkerStage.ClientSpeechCandidate,
                            BargeInTrigger.ClientVoiceActivity,
                            aecActive: stateChanged.IsAcousticEchoCancellationActive));

                    // A project that asked for echo cancellation waits until it is genuinely
                    // running before a candidate may duck. Until then the candidate is most
                    // likely the character's own residual echo, and ducking on it produces an
                    // audible duck-restore flutter over the first words. Projects without AEC
                    // keep the early duck; there the server owns the interruption anyway.
                    if (!CurrentResolvedTurnTakingOptions.EnableAcousticEchoCancellation ||
                        stateChanged.IsAcousticEchoCancellationActive)
                    {
                        _bargeInCoordinator?.Duck(BargeInTrigger.ClientVoiceActivity);
                    }

                    break;

                case ClientVoiceActivityStage.Confirmed:
                    _clientLatencyMetricsCollector?.RecordBargeInMarker(
                        BargeInMarker.Create(
                            BargeInMarkerStage.ClientSpeechConfirmed,
                            BargeInTrigger.ClientVoiceActivity,
                            aecActive: stateChanged.IsAcousticEchoCancellationActive));

                    // Only commit locally when the detector is consuming PCM from an AEC pipeline
                    // with a live rendered-audio reference. A configured preference alone does not
                    // prove that echo cancellation initialized successfully.
                    if (stateChanged.IsAcousticEchoCancellationActive)
                    {
                        SendInterruption(
                            BargeInTrigger.ClientVoiceActivity,
                            requireActivePlayback: true);
                    }

                    break;

                case ClientVoiceActivityStage.Cancelled:
                case ClientVoiceActivityStage.Ended:
                    _bargeInCoordinator?.CancelDuck();
                    break;
            }
        }

        private void HandleCharacterTtsEvidence(CharacterTtsTextChunk ttsEvent) =>
            PublishRecoveredCharacterReady(
                _characterLifecycleCoordinator?.TryRecoverCharacterReadyFromTts(ttsEvent),
                "tts-text");

        private void HandleLipSyncEvidence(LipSyncPackedDataReceived lipSyncEvent) =>
            PublishRecoveredCharacterReady(
                _characterLifecycleCoordinator?.TryRecoverCharacterReadyFromLipSync(lipSyncEvent),
                "lip-sync");

        private void PublishRecoveredCharacterReady(CharacterReady? recoveredReady, string source)
        {
            if (!recoveredReady.HasValue || _eventHub == null)
                return;

            CharacterReady readyEvent = recoveredReady.Value;
            ConvaiLogger.Info(
                $"Recovering missing CharacterReady from {source}: characterId={readyEvent.CharacterId}, participantId={readyEvent.ParticipantId}",
                LogCategory.SDK);
            _eventHub.Publish(readyEvent);
        }

        private void OnRemoteAudioPreferenceChanged(string characterId, bool enabled) =>
            RemoteAudioEnabledChanged?.Invoke(characterId, enabled);
    }
}
