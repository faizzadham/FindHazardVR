using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Convai.RestAPI.Internal;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Async;
using UnityEngine;

namespace Convai.Runtime.Room
{
    /// <summary>What the room says about one character's place in it.</summary>
    /// <remarks>
    ///     <see cref="Starting" /> is the one worth acting on. The room accepts a character as soon
    ///     as the roster is agreed, which is before the service has announced it — so a member can
    ///     be in the room and unable to hear a word, and anything sent in that window reaches
    ///     nobody.
    /// </remarks>
    public enum CharacterRoomStatus
    {
        /// <summary>In the room; the service has not announced it yet. Cannot be talked to.</summary>
        Starting = 0,

        /// <summary>Announced by the service and able to hold the conversation.</summary>
        Ready,

        /// <summary>The service gave up on this character. It is not coming back on this connection.</summary>
        Failed
    }

    /// <summary>One backend membership bound to one local character instance.</summary>
    public sealed class CharacterRoomMembership
    {
        internal CharacterRoomMembership(RoomCharacterDetails details, IConvaiCharacterAgent character)
        {
            Character = character;
            Update(details, character);
        }

        internal void Update(RoomCharacterDetails details, IConvaiCharacterAgent character = null)
        {
            if (details == null) return;
            MembershipId = details.MembershipId ?? MembershipId ?? string.Empty;
            CharacterId = details.CharacterId ?? CharacterId ?? string.Empty;
            SessionId = details.SessionId ?? SessionId ?? string.Empty;
            CharacterSessionId = details.CharacterSessionId ?? CharacterSessionId ?? string.Empty;
            ParticipantIdentity = details.ParticipantIdentity ?? ParticipantIdentity ?? string.Empty;
            IsInitial = details.IsInitial || IsInitial;
            ProvisioningStatus = details.ProvisioningStatus ?? ProvisioningStatus ?? string.Empty;
            FailureCode = details.FailureCode ?? FailureCode;
            Character ??= character;
            if (string.Equals(ProvisioningStatus, "dispatch_failed", StringComparison.OrdinalIgnoreCase))
            {
                Status = CharacterRoomStatus.Failed;
                SignalFailed(FailureCode);
            }
        }

        /// <summary>The room's own id for this seat. Stable for as long as the character holds it.</summary>
        /// <remarks>
        ///     This, not the Character ID, is what addresses a member: every roster verb takes it,
        ///     and it is the one identifier the service and the SDK both route by.
        /// </remarks>
        public string MembershipId { get; private set; } = string.Empty;

        /// <summary>Convai dashboard Character ID of the character in this seat.</summary>
        public string CharacterId { get; private set; } = string.Empty;

        /// <summary>The service session this membership belongs to.</summary>
        public string SessionId { get; private set; } = string.Empty;

        /// <summary>
        ///     The resumable conversation session for this character, when session resume is on.
        ///     Empty otherwise.
        /// </summary>
        public string CharacterSessionId { get; private set; } = string.Empty;

        /// <summary>The transport identity this character speaks through.</summary>
        public string ParticipantIdentity { get; private set; } = string.Empty;

        /// <summary>Whether the room was opened for this character.</summary>
        /// <remarks>
        ///     The room's readiness is this character's readiness — see
        ///     <see cref="MultiCharacterRoomSession.IsReady" /> — and it is the one member a room
        ///     cannot be created without.
        /// </remarks>
        public bool IsInitial { get; private set; }

        /// <summary>The service's own word for how far this character's startup has got.</summary>
        /// <remarks>
        ///     Raw service text, kept for diagnostics. Read <see cref="Status" /> to decide anything.
        /// </remarks>
        public string ProvisioningStatus { get; private set; } = string.Empty;

        /// <summary>The scene character this seat is bound to, or <c>null</c> if none was matched.</summary>
        public IConvaiCharacterAgent Character { get; private set; }

        /// <summary>Whether this character can hold the conversation yet.</summary>
        public CharacterRoomStatus Status { get; internal set; }

        /// <summary>Why the character failed to start. <c>null</c> unless <see cref="Status" /> is Failed.</summary>
        /// <remarks>
        ///     The only record of the reason: a failed membership is otherwise indistinguishable
        ///     from a character somebody forgot to set up.
        /// </remarks>
        public string FailureCode { get; internal set; }

        /// <summary>The transport participant this character's audio arrives on, once bound.</summary>
        public string ParticipantId { get; internal set; }

        /// <summary>
        ///     Completes when this character is ready to talk, or faults when it fails to start.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A roster update is acknowledged as soon as the service accepts it, which is before
        ///         the character can say anything: the membership comes back
        ///         <see cref="CharacterRoomStatus.Starting" />. Without this the only way to wait was a
        ///         polling loop, and every caller wrote the same one — the shipped sample included.
        ///     </para>
        ///     <para>
        ///         Cancelling the token abandons the wait; it does not cancel the character's startup.
        ///     </para>
        /// </remarks>
        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
        {
            Task readiness = _ready.Task;
            if (!cancellationToken.CanBeCanceled)
            {
                await readiness;
                return;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => cancelled.TrySetCanceled(cancellationToken));
            await await ConvaiTimers.WhenAny(readiness, cancelled.Task);
        }

        internal void SignalReady() => _ready.TrySetResult(true);

        internal void SignalFailed(string failureCode) =>
            _ready.TrySetException(new InvalidOperationException(
                $"Character '{CharacterId}' failed to start ({failureCode ?? "unknown"})."));

        internal void SignalRetired(Exception reason) => _ready.TrySetException(reason);

        private readonly TaskCompletionSource<bool> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Client-side projection of the canonical multi-character room roster.</summary>
    public sealed class MultiCharacterRoomSession
    {
        private const string RetiredSessionMessage =
            "The multi-character room session is no longer active.";

        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _initialReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<CharacterRoomMembership> _characters;
        private readonly ReadOnlyCollection<CharacterRoomMembership> _readOnlyCharacters;
        private readonly Dictionary<string, CharacterRoomMembership> _byMembershipId;
        private readonly Dictionary<string, CharacterRoomMembership> _byParticipantIdentity;
        private readonly Dictionary<string, CharacterRoomMembership> _byParticipantId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<InteractionTargetResult>> _pendingTargetCommands =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingRosterCommand> _pendingRosterCommands = new(StringComparer.Ordinal);
        private bool _retired;
        private bool _routingRecoveryClaimed;
        private bool _isApplyingTargetCommandResponse;

        internal MultiCharacterRoomSession(
            RoomDetails details,
            IReadOnlyList<IConvaiCharacterAgent> localCharacters)
        {
            RoomSessionId = details?.RoomSessionId ?? string.Empty;
            ActiveMembershipId = details?.ActiveMembershipId ?? string.Empty;
            RouteEpoch = details?.RouteEpoch ?? 0;
            RosterEpoch = details?.RosterEpoch ?? 0;
            PartialDispatch = details?.PartialDispatch ?? false;
            _characters = new List<CharacterRoomMembership>();
            _readOnlyCharacters = _characters.AsReadOnly();
            _byMembershipId = new Dictionary<string, CharacterRoomMembership>(StringComparer.Ordinal);
            _byParticipantIdentity = new Dictionary<string, CharacterRoomMembership>(StringComparer.Ordinal);

            IReadOnlyList<RoomCharacterDetails> responseCharacters = details?.Characters;
            if (responseCharacters == null) return;

            var availableLocals = new List<IConvaiCharacterAgent>();
            if (localCharacters != null)
            {
                for (int i = 0; i < localCharacters.Count; i++)
                {
                    IConvaiCharacterAgent localCharacter = localCharacters[i];
                    if (localCharacter != null) availableLocals.Add(localCharacter);
                }
            }

            for (int i = 0; i < responseCharacters.Count; i++)
            {
                RoomCharacterDetails responseCharacter = responseCharacters[i];
                IConvaiCharacterAgent local = ResolveLocalCharacter(responseCharacter, availableLocals);
                if (local != null) availableLocals.Remove(local);
                var membership = new CharacterRoomMembership(responseCharacter, local);
                if (local is ConvaiCharacter convaiCharacter &&
                    !string.IsNullOrWhiteSpace(membership.CharacterSessionId))
                    convaiCharacter.SetCurrentCharacterSessionId(membership.CharacterSessionId);
                _characters.Add(membership);
                if (!string.IsNullOrWhiteSpace(membership.MembershipId))
                    _byMembershipId[membership.MembershipId] = membership;
                if (!string.IsNullOrWhiteSpace(membership.ParticipantIdentity))
                    _byParticipantIdentity[membership.ParticipantIdentity] = membership;
                if (membership.IsInitial) InitialCharacter = membership;
            }

            InitialCharacter ??= _characters.Count > 0 ? _characters[0] : null;
            if (InitialCharacter?.Status == CharacterRoomStatus.Failed)
                _initialReady.TrySetException(new InvalidOperationException(
                    $"Initial character failed to start ({InitialCharacter.FailureCode ?? "unknown"})."));
        }

        private static IConvaiCharacterAgent ResolveLocalCharacter(
            RoomCharacterDetails response,
            IReadOnlyList<IConvaiCharacterAgent> candidates)
        {
            if (response == null || candidates == null) return null;
            if (!string.IsNullOrWhiteSpace(response.CharacterSessionId))
            {
                for (int i = 0; i < candidates.Count; i++)
                    if (candidates[i] is ConvaiCharacter character &&
                        string.Equals(
                            character.CharacterSessionId,
                            response.CharacterSessionId,
                            StringComparison.Ordinal))
                        return character;
            }

            for (int i = 0; i < candidates.Count; i++)
                if (string.Equals(
                        candidates[i]?.CharacterId,
                        response.CharacterId,
                        StringComparison.Ordinal))
                    return candidates[i];
            return null;
        }

        /// <summary>The service's id for this room. Use it to join the same room from elsewhere.</summary>
        public string RoomSessionId { get; }

        /// <summary>The seat the player's speech and text are currently routed to.</summary>
        /// <remarks>
        ///     Moved by <c>ConvaiManager.TalkTo</c> and by automatic targeting. It is a round trip
        ///     behind the request, so during a change this still names the character being moved
        ///     away from.
        /// </remarks>
        public string ActiveMembershipId { get; private set; }

        /// <summary>Raised each time the routing target changes, so a late reply can be recognised.</summary>
        public int RouteEpoch { get; private set; }

        /// <summary>
        ///     Raised each time the roster's membership changes — a character joining or leaving.
        /// </summary>
        /// <remarks>
        ///     Identity only. A member becoming <see cref="CharacterRoomStatus.Ready" /> does
        ///     <i>not</i> raise it, so this cannot be used to cache anything that depends on
        ///     readiness.
        /// </remarks>
        public int RosterEpoch { get; private set; }

        /// <summary>Whether the service accepted the room with fewer characters than were asked for.</summary>
        public bool PartialDispatch { get; }

        /// <summary>Outstanding canonical target commands retained for late reconciliation.</summary>
        internal int PendingTargetCommandCount
        {
            get
            {
                lock (_gate) return _pendingTargetCommands.Count;
            }
        }

        /// <summary>
        ///     True only while a command-correlated target response is synchronously applying its
        ///     session event. Room-level observers use the canonical command task for those events
        ///     so they can publish after routing gates release without duplicating this transition.
        /// </summary>
        internal bool IsApplyingTargetCommandResponse => _isApplyingTargetCommandResponse;

        /// <summary>Outstanding canonical roster commands retained for late reconciliation.</summary>
        internal int PendingRosterCommandCount
        {
            get
            {
                lock (_gate) return _pendingRosterCommands.Count;
            }
        }

        /// <summary>Every seat in the room, in the order the service returned them.</summary>
        public IReadOnlyList<CharacterRoomMembership> Characters => _readOnlyCharacters;

        /// <summary>The seat the room was opened for.</summary>
        public CharacterRoomMembership InitialCharacter { get; }

        /// <summary>
        ///     Whether the room has started — meaning its <see cref="InitialCharacter" /> is ready.
        /// </summary>
        /// <remarks>
        ///     <b>Not "everyone is ready".</b> Members become ready one at a time, and this answers
        ///     only for the one the room was opened for. To wait on another character, use that
        ///     membership's <see cref="CharacterRoomMembership.WaitUntilReadyAsync" />.
        /// </remarks>
        public bool IsReady
        {
            get
            {
                lock (_gate)
                    return !_retired && InitialCharacter?.Status == CharacterRoomStatus.Ready;
            }
        }

        /// <summary>Raised when a member's <see cref="CharacterRoomStatus" /> changes.</summary>
        public event Action<CharacterRoomMembership> CharacterStatusChanged;

        /// <summary>Raised when a character takes a seat in this room.</summary>
        public event Action<CharacterRoomMembership> CharacterAdded;

        /// <summary>Raised when a character gives up its seat.</summary>
        public event Action<CharacterRoomMembership> CharacterRemoved;

        /// <summary>Raised whenever a newer authoritative roster epoch is observed.</summary>
        /// <remarks>
        ///     An epoch may advance even when an existing membership remains in the same nonterminal
        ///     state, so callers waiting on reconciliation cannot infer this from add, remove, or
        ///     status events alone.
        /// </remarks>
        public event Action<int> RosterEpochChanged;

        /// <summary>Raised once when this session is replaced or cleared.</summary>
        /// <remarks>Long-running observers should treat this as a terminal result for this session.</remarks>
        public event Action Retired;

        /// <summary>
        ///     Raised when an authoritative routing response is applied, with the seat it left and
        ///     the seat it moved to, in that order. Either may be <c>null</c>. A successful
        ///     acknowledgement for an already-authoritative route raises a no-op callback where
        ///     both arguments reference the current seat.
        /// </summary>
        public event Action<CharacterRoomMembership, CharacterRoomMembership> InteractionTargetChanged;

        /// <summary>
        ///     Completes once the room has started, or faults if it is retired first.
        /// </summary>
        /// <remarks>
        ///     Waits on <see cref="IsReady" />, which is about the <see cref="InitialCharacter" />
        ///     alone. Cancelling the token abandons the wait; it does not stop the room starting.
        /// </remarks>
        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
        {
            Task readinessTask;
            lock (_gate)
            {
                if (_retired) throw CreateRetiredSessionException();
                if (InitialCharacter?.Status == CharacterRoomStatus.Ready) return;
                readinessTask = _initialReady.Task;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                await readinessTask;
                return;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => cancelled.TrySetCanceled(cancellationToken));
            Task completed = await ConvaiTimers.WhenAny(readinessTask, cancelled.Task);
            await completed;
        }

        internal void Retire()
        {
            lock (_gate)
            {
                if (_retired) return;
                _retired = true;

                _initialReady.TrySetException(CreateRetiredSessionException());

                // A caller awaiting a character that will now never start must not wait forever.
                for (int i = 0; i < _characters.Count; i++)
                    _characters[i].SignalRetired(CreateRetiredSessionException());

                foreach (TaskCompletionSource<InteractionTargetResult> completion in _pendingTargetCommands.Values)
                    completion.TrySetException(CreateRetiredSessionException());
                foreach (PendingRosterCommand pending in _pendingRosterCommands.Values)
                    pending.Completion.TrySetException(CreateRetiredSessionException());

                _pendingTargetCommands.Clear();
                _pendingRosterCommands.Clear();
            }

            Retired?.Invoke();
        }

        private static InvalidOperationException CreateRetiredSessionException() =>
            new(RetiredSessionMessage);

        /// <summary>The seat a scene character holds in this room, or <c>null</c> if it holds none.</summary>
        public CharacterRoomMembership FindByCharacter(IConvaiCharacterAgent character)
        {
            if (character == null) return null;
            lock (_gate)
                return _characters.Find(item => ReferenceEquals(item.Character, character));
        }

        /// <summary>The seat with this membership id, or <c>null</c> if the room has no such seat.</summary>
        public CharacterRoomMembership FindByMembershipId(string membershipId)
        {
            if (string.IsNullOrWhiteSpace(membershipId)) return null;
            lock (_gate) return _byMembershipId.TryGetValue(membershipId, out var membership) ? membership : null;
        }

        internal CharacterRoomMembership FindUniqueByCharacterId(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return null;
            lock (_gate)
            {
                CharacterRoomMembership match = null;
                for (int i = 0; i < _characters.Count; i++)
                {
                    CharacterRoomMembership candidate = _characters[i];
                    if (!string.Equals(candidate.CharacterId, characterId, StringComparison.Ordinal)) continue;
                    if (match != null) return null;
                    match = candidate;
                }
                return match;
            }
        }

        internal CharacterRoomMembership Resolve(
            string membershipId,
            string participantIdentity,
            string participantId)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(membershipId) &&
                    _byMembershipId.TryGetValue(membershipId, out var membership))
                    return membership;
                if (!string.IsNullOrWhiteSpace(participantIdentity) &&
                    _byParticipantIdentity.TryGetValue(participantIdentity, out membership))
                    return membership;
                return !string.IsNullOrWhiteSpace(participantId) &&
                       _byParticipantId.TryGetValue(participantId, out membership)
                    ? membership
                    : null;
            }
        }

        internal void MarkReady(CharacterRoomMembership membership, string participantId)
        {
            if (membership == null) return;
            lock (_gate)
            {
                membership.Status = CharacterRoomStatus.Ready;
                if (!string.IsNullOrWhiteSpace(participantId))
                {
                    if (!string.IsNullOrWhiteSpace(membership.ParticipantId) &&
                        !string.Equals(membership.ParticipantId, participantId, StringComparison.Ordinal))
                        RemoveParticipantIdIndex(membership);
                    membership.ParticipantId = participantId;
                    _byParticipantId[participantId] = membership;
                }
            }

            membership.SignalReady();
            CharacterStatusChanged?.Invoke(membership);
            if (ReferenceEquals(membership, InitialCharacter)) _initialReady.TrySetResult(true);
        }

        internal void BindParticipant(CharacterRoomMembership membership, string participantId)
        {
            if (membership == null || string.IsNullOrWhiteSpace(participantId)) return;
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(membership.ParticipantId) &&
                    !string.Equals(membership.ParticipantId, participantId, StringComparison.Ordinal))
                    RemoveParticipantIdIndex(membership);
                membership.ParticipantId = participantId;
                _byParticipantId[participantId] = membership;
            }
        }

        internal void MarkFailed(CharacterRoomMembership membership, string failureCode)
        {
            if (membership == null) return;
            lock (_gate)
            {
                membership.Status = CharacterRoomStatus.Failed;
                membership.FailureCode = failureCode;
            }

            membership.SignalFailed(failureCode);
            CharacterStatusChanged?.Invoke(membership);
            if (ReferenceEquals(membership, InitialCharacter))
                _initialReady.TrySetException(new InvalidOperationException(
                    $"Initial character failed to start ({failureCode ?? "unknown"})."));
        }

        internal void ObserveRosterEpoch(int rosterEpoch)
        {
            lock (_gate)
            {
                if (rosterEpoch <= RosterEpoch) return;
                RosterEpoch = rosterEpoch;
            }

            RosterEpochChanged?.Invoke(rosterEpoch);
        }

        internal CharacterRoomMembership UpsertMembership(
            RoomCharacterDetails details,
            IReadOnlyList<IConvaiCharacterAgent> localCharacters = null,
            IConvaiCharacterAgent preferredCharacter = null,
            int sourceRosterEpoch = -1)
        {
            if (details == null || string.IsNullOrWhiteSpace(details.MembershipId)) return null;

            CharacterRoomMembership membership;
            bool added = false;
            lock (_gate)
            {
                if (_byMembershipId.TryGetValue(details.MembershipId, out membership))
                {
                    // A delayed command acknowledgement still proves its add was accepted, but an
                    // unrelated newer lifecycle event may already have advanced this membership.
                    // Preserve the newer projection instead of regressing Ready back to Starting.
                    if (sourceRosterEpoch > 0 && sourceRosterEpoch < RosterEpoch)
                        return membership;

                    DeindexMembership(membership);
                    membership.Update(details, preferredCharacter);
                    IndexMembership(membership);
                    return membership;
                }

                IConvaiCharacterAgent local = preferredCharacter ??
                    ResolvePendingLocalCharacter(details) ??
                    ResolveAvailableLocalCharacter(details, localCharacters);
                membership = new CharacterRoomMembership(details, local);
                _characters.Add(membership);
                _byMembershipId[membership.MembershipId] = membership;
                IndexMembership(membership);
                added = true;
            }

            if (membership.Character is ConvaiCharacter convaiCharacter &&
                !string.IsNullOrWhiteSpace(membership.CharacterSessionId))
                convaiCharacter.SetCurrentCharacterSessionId(membership.CharacterSessionId);

            if (added)
            {
                CharacterAdded?.Invoke(membership);
                CharacterStatusChanged?.Invoke(membership);
            }
            return membership;
        }

        internal CharacterRoomMembership RemoveMembership(
            string membershipId,
            int rosterEpoch = -1,
            bool publishTargetChange = true,
            bool publishRemovalEvent = true)
        {
            if (string.IsNullOrWhiteSpace(membershipId)) return null;

            CharacterRoomMembership removed;
            bool changed = false;
            bool targetRemoved = false;
            bool initialRemoved = false;
            bool targetNotificationOwnedByRosterCommand = false;
            lock (_gate)
            {
                if (_byMembershipId.TryGetValue(membershipId, out removed))
                {
                    _characters.Remove(removed);
                    _byMembershipId.Remove(membershipId);
                    DeindexMembership(removed);
                    if (string.Equals(ActiveMembershipId, membershipId, StringComparison.Ordinal))
                    {
                        foreach (PendingRosterCommand pending in _pendingRosterCommands.Values)
                            if (string.Equals(
                                    pending.RemovedMembershipId,
                                    membershipId,
                                    StringComparison.Ordinal))
                            {
                                targetNotificationOwnedByRosterCommand = true;
                                pending.RemovalEventSuppressed = true;
                                break;
                            }
                        ActiveMembershipId = string.Empty;
                        targetRemoved = true;
                    }
                    initialRemoved = ReferenceEquals(removed, InitialCharacter);
                    changed = true;
                }
            }

            if (!changed)
            {
                if (rosterEpoch >= 0) ObserveRosterEpoch(rosterEpoch);
                return null;
            }

            var removedBeforeReady = new InvalidOperationException(
                $"Character '{removed.CharacterId}' left the room before it became ready.");
            removed.SignalRetired(removedBeforeReady);
            if (initialRemoved)
                _initialReady.TrySetException(new InvalidOperationException(
                    $"Initial character '{removed.CharacterId}' left the room before it became ready."));

            if (targetRemoved && publishTargetChange && !targetNotificationOwnedByRosterCommand)
                InteractionTargetChanged?.Invoke(removed, null);
            if (changed && publishRemovalEvent && !targetNotificationOwnedByRosterCommand)
                CharacterRemoved?.Invoke(removed);
            if (rosterEpoch >= 0) ObserveRosterEpoch(rosterEpoch);
            return removed;
        }

        internal Task<CharacterRosterUpdateResult> RegisterRosterCommand(
            string commandId,
            IConvaiCharacterAgent addedCharacter,
            string removedMembershipId,
            CancellationToken cancellationToken) =>
            RegisterTrackedRosterCommand(
                commandId,
                addedCharacter,
                removedMembershipId,
                cancellationToken).CallerCompletion;

        /// <summary>
        ///     Registers a roster command while keeping its canonical response alive after one
        ///     caller's timeout or cancellation.
        /// </summary>
        internal CharacterRosterCommandRegistration RegisterTrackedRosterCommand(
            string commandId,
            IConvaiCharacterAgent addedCharacter,
            string removedMembershipId,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<CharacterRosterUpdateResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_retired || _routingRecoveryClaimed)
                {
                    completion.TrySetException(CreateRetiredSessionException());
                    return new CharacterRosterCommandRegistration(completion.Task, completion.Task);
                }

                _pendingRosterCommands[commandId] = new PendingRosterCommand(
                    completion,
                    addedCharacter,
                    removedMembershipId,
                    !string.IsNullOrWhiteSpace(removedMembershipId) &&
                    _byMembershipId.TryGetValue(removedMembershipId, out var removed)
                        ? removed
                        : null,
                    !string.IsNullOrWhiteSpace(removedMembershipId) &&
                    string.Equals(ActiveMembershipId, removedMembershipId, StringComparison.Ordinal));
            }
            return new CharacterRosterCommandRegistration(
                WaitForCommandAsync(completion.Task, cancellationToken),
                completion.Task);
        }

        /// <summary>Faults and removes a roster command that could not be put on the wire.</summary>
        internal void FailRosterCommand(string commandId, Exception reason)
        {
            PendingRosterCommand pending;
            lock (_gate)
            {
                if (!_pendingRosterCommands.TryGetValue(commandId ?? string.Empty, out pending)) return;
                _pendingRosterCommands.Remove(commandId);
            }

            pending.Completion.TrySetException(reason ?? new InvalidOperationException(
                "The character-roster command could not be sent."));
            PublishSuppressedRosterRemoval(pending, publishTargetState: true);
        }

        /// <summary>
        ///     Atomically claims a missing roster response for fail-closed recovery. Roster epochs
        ///     do not supersede a missing delta, so any still-pending command makes the projection
        ///     ambiguous and prevents later acknowledgements from mutating it before retirement.
        /// </summary>
        internal CanonicalRoutingTimeoutDisposition ClaimRosterCommandTimeout(
            string commandId,
            Exception reason)
        {
            PendingRosterCommand pending;
            lock (_gate)
            {
                if (!_pendingRosterCommands.TryGetValue(commandId ?? string.Empty, out pending))
                    return CanonicalRoutingTimeoutDisposition.AlreadyResolved;

                _pendingRosterCommands.Remove(commandId);
                _routingRecoveryClaimed = true;
                pending.Completion.TrySetException(reason);
            }

            PublishSuppressedRosterRemoval(pending, publishTargetState: true);
            return CanonicalRoutingTimeoutDisposition.RequiresRecovery;
        }

        internal string ResolvePendingRosterCommandId(string commandId)
        {
            if (!string.IsNullOrWhiteSpace(commandId)) return commandId;
            lock (_gate)
            {
                if (_pendingRosterCommands.Count != 1) return string.Empty;
                foreach (string pendingId in _pendingRosterCommands.Keys) return pendingId;
                return string.Empty;
            }
        }

        internal void CompleteRosterCommand(
            string commandId,
            string status,
            string message,
            string code,
            IReadOnlyList<RoomCharacterDetails> addedDetails,
            IReadOnlyList<RoomCharacterDetails> removedDetails,
            IReadOnlyList<string> removedMembershipIds,
            string activeMembershipId,
            int routeEpoch,
            int rosterEpoch)
        {
            PendingRosterCommand pending;
            lock (_gate)
            {
                if (_routingRecoveryClaimed) return;
                if (!_pendingRosterCommands.TryGetValue(commandId ?? string.Empty, out pending)) return;
                _pendingRosterCommands.Remove(commandId);
            }

            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                // A rejection can still carry the room's newer authoritative route and epochs.
                // Apply those before surfacing the command error, just like target-command
                // rejections do, so recovery starts from what the service actually owns.
                ApplyRosterState(
                    activeMembershipId,
                    routeEpoch,
                    rosterEpoch,
                    pending.RemovalEventSuppressed ? pending.RemovedMembership : null);
                PublishSuppressedRosterRemoval(pending, publishTargetState: false);
                pending.Completion.TrySetException(new CharacterRosterUpdateException(
                    code,
                    string.IsNullOrWhiteSpace(message) ? "Character roster update failed." : message));
                return;
            }

            var added = new List<CharacterRoomMembership>();
            var removed = new List<CharacterRoomMembership>();
            var deferredRemovalEvents = new List<CharacterRoomMembership>();
            CharacterRoomMembership previousTarget = FindByMembershipId(ActiveMembershipId) ??
                                                       (pending.RemovedWasActiveTarget
                                                           ? pending.RemovedMembership
                                                           : null);
            bool atomicTargetHandover = previousTarget != null &&
                                        !string.Equals(
                                            previousTarget.MembershipId,
                                            activeMembershipId ?? string.Empty,
                                            StringComparison.Ordinal) &&
                                        routeEpoch >= RouteEpoch;
            if (addedDetails != null)
                for (int i = 0; i < addedDetails.Count; i++)
                {
                    RoomCharacterDetails details = addedDetails[i];
                    // A lifecycle event may already have created and advanced this membership. A
                    // delayed command ACK still proves the add was accepted, but its older details
                    // must not regress a Ready member back to Starting. Reuse the current projection
                    // when present; create the missing command-correlated seat otherwise.
                    CharacterRoomMembership item = UpsertMembership(
                        details,
                        preferredCharacter: pending.AddedCharacter,
                        sourceRosterEpoch: rosterEpoch);
                    if (item != null) added.Add(item);
                }

            // Roster acknowledgements contain this command's accepted deltas, not a full snapshot.
            // A larger epoch observed from an unrelated lifecycle status cannot supersede a lost
            // add/remove. Apply removals idempotently even when the ACK is older than that global
            // epoch, while Math.Max in RemoveMembership preserves the newer epoch counter.
            if (removedDetails != null)
                for (int i = 0; i < removedDetails.Count; i++)
                {
                    CharacterRoomMembership item = RemoveMembership(
                        removedDetails[i]?.MembershipId,
                        -1,
                        publishTargetChange: !atomicTargetHandover,
                        publishRemovalEvent: !atomicTargetHandover);
                    item ??= pending.RemovedMembership;
                    if (atomicTargetHandover && item != null && !deferredRemovalEvents.Contains(item))
                        deferredRemovalEvents.Add(item);
                    if (item != null && !removed.Contains(item)) removed.Add(item);
                }
            if (removedMembershipIds != null)
                for (int i = 0; i < removedMembershipIds.Count; i++)
                {
                    CharacterRoomMembership item = RemoveMembership(
                        removedMembershipIds[i],
                        -1,
                        publishTargetChange: !atomicTargetHandover,
                        publishRemovalEvent: !atomicTargetHandover);
                    item ??= pending.RemovedMembership;
                    if (atomicTargetHandover && item != null && !deferredRemovalEvents.Contains(item))
                        deferredRemovalEvents.Add(item);
                    if (item != null && !removed.Contains(item)) removed.Add(item);
                }

            if (pending.RemovalEventSuppressed &&
                pending.RemovedMembership != null &&
                !deferredRemovalEvents.Contains(pending.RemovedMembership))
                deferredRemovalEvents.Add(pending.RemovedMembership);

            ApplyRosterState(
                activeMembershipId,
                routeEpoch,
                rosterEpoch,
                atomicTargetHandover ? previousTarget : null);
            for (int i = 0; i < deferredRemovalEvents.Count; i++)
                CharacterRemoved?.Invoke(deferredRemovalEvents[i]);
            pending.RemovalEventSuppressed = false;
            pending.Completion.TrySetResult(new CharacterRosterUpdateResult(
                commandId,
                added,
                removed,
                ActiveMembershipId,
                RouteEpoch,
                RosterEpoch));
        }

        private void PublishSuppressedRosterRemoval(
            PendingRosterCommand pending,
            bool publishTargetState)
        {
            if (pending?.RemovalEventSuppressed != true || pending.RemovedMembership == null) return;

            pending.RemovalEventSuppressed = false;
            if (publishTargetState)
            {
                CharacterRoomMembership current = FindByMembershipId(ActiveMembershipId);
                InteractionTargetChanged?.Invoke(pending.RemovedMembership, current);
            }
            CharacterRemoved?.Invoke(pending.RemovedMembership);
        }

        private void ApplyRosterState(
            string activeMembershipId,
            int routeEpoch,
            int rosterEpoch,
            CharacterRoomMembership previousTarget = null)
        {
            ApplyInteractionTarget(activeMembershipId, routeEpoch, previousTarget);
            ObserveRosterEpoch(rosterEpoch);
        }

        private IConvaiCharacterAgent ResolvePendingLocalCharacter(RoomCharacterDetails details)
        {
            foreach (PendingRosterCommand pending in _pendingRosterCommands.Values)
            {
                IConvaiCharacterAgent candidate = pending.AddedCharacter;
                if (candidate == null || IsCharacterBound(candidate)) continue;
                if (string.Equals(candidate.CharacterId, details.CharacterId, StringComparison.Ordinal))
                    return candidate;
            }
            return null;
        }

        private IConvaiCharacterAgent ResolveAvailableLocalCharacter(
            RoomCharacterDetails details,
            IReadOnlyList<IConvaiCharacterAgent> candidates)
        {
            IConvaiCharacterAgent candidate = ResolveLocalCharacter(details, candidates);
            return candidate != null && !IsCharacterBound(candidate) ? candidate : null;
        }

        private bool IsCharacterBound(IConvaiCharacterAgent character) =>
            character != null && _characters.Exists(item => ReferenceEquals(item.Character, character));

        private void IndexMembership(CharacterRoomMembership membership)
        {
            if (!string.IsNullOrWhiteSpace(membership.ParticipantIdentity))
                _byParticipantIdentity[membership.ParticipantIdentity] = membership;
            if (!string.IsNullOrWhiteSpace(membership.ParticipantId))
                _byParticipantId[membership.ParticipantId] = membership;
        }

        private void DeindexMembership(CharacterRoomMembership membership)
        {
            if (!string.IsNullOrWhiteSpace(membership.ParticipantIdentity) &&
                _byParticipantIdentity.TryGetValue(membership.ParticipantIdentity, out var identityMatch) &&
                ReferenceEquals(identityMatch, membership))
                _byParticipantIdentity.Remove(membership.ParticipantIdentity);
            if (!string.IsNullOrWhiteSpace(membership.ParticipantId) &&
                _byParticipantId.TryGetValue(membership.ParticipantId, out var participantMatch) &&
                ReferenceEquals(participantMatch, membership))
                _byParticipantId.Remove(membership.ParticipantId);
        }

        private void RemoveParticipantIdIndex(CharacterRoomMembership membership)
        {
            if (_byParticipantId.TryGetValue(membership.ParticipantId, out var participantMatch) &&
                ReferenceEquals(participantMatch, membership))
                _byParticipantId.Remove(membership.ParticipantId);
        }

        private sealed class PendingRosterCommand
        {
            public PendingRosterCommand(
                TaskCompletionSource<CharacterRosterUpdateResult> completion,
                IConvaiCharacterAgent addedCharacter,
                string removedMembershipId,
                CharacterRoomMembership removedMembership,
                bool removedWasActiveTarget)
            {
                Completion = completion;
                AddedCharacter = addedCharacter;
                RemovedMembershipId = removedMembershipId;
                RemovedMembership = removedMembership;
                RemovedWasActiveTarget = removedWasActiveTarget;
            }

            public TaskCompletionSource<CharacterRosterUpdateResult> Completion { get; }
            public IConvaiCharacterAgent AddedCharacter { get; }
            public string RemovedMembershipId { get; }
            public CharacterRoomMembership RemovedMembership { get; }
            public bool RemovedWasActiveTarget { get; }
            public bool RemovalEventSuppressed { get; set; }
        }

        internal bool ApplyInteractionTarget(
            string activeMembershipId,
            int routeEpoch,
            CharacterRoomMembership previousOverride = null)
        {
            CharacterRoomMembership previous;
            CharacterRoomMembership current;
            lock (_gate)
            {
                string normalizedMembershipId = activeMembershipId ?? string.Empty;
                if (routeEpoch < RouteEpoch ||
                    (routeEpoch == RouteEpoch && previousOverride == null))
                    return false;

                _byMembershipId.TryGetValue(ActiveMembershipId ?? string.Empty, out previous);
                previous ??= previousOverride;
                ActiveMembershipId = normalizedMembershipId;
                RouteEpoch = routeEpoch;
                _byMembershipId.TryGetValue(ActiveMembershipId ?? string.Empty, out current);
            }
            InteractionTargetChanged?.Invoke(previous, current);
            return true;
        }

        internal Task<InteractionTargetResult> RegisterTargetCommand(
            string commandId,
            CancellationToken cancellationToken) =>
            RegisterTrackedTargetCommand(commandId, cancellationToken).CallerCompletion;

        /// <summary>
        ///     Registers one command and exposes both the caller's cancellable wait and the
        ///     canonical response that remains authoritative after that caller leaves.
        /// </summary>
        internal InteractionTargetCommandRegistration RegisterTrackedTargetCommand(
            string commandId,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<InteractionTargetResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_retired || _routingRecoveryClaimed)
                {
                    completion.TrySetException(CreateRetiredSessionException());
                    return new InteractionTargetCommandRegistration(completion.Task, completion.Task);
                }

                _pendingTargetCommands[commandId] = completion;
            }
            return new InteractionTargetCommandRegistration(
                WaitForCommandAsync(completion.Task, cancellationToken),
                completion.Task);
        }

        /// <summary>Faults a registered command that could not be put on the wire.</summary>
        internal void FailTargetCommand(string commandId, Exception reason)
        {
            TaskCompletionSource<InteractionTargetResult> completion;
            lock (_gate)
            {
                if (!_pendingTargetCommands.TryGetValue(commandId ?? string.Empty, out completion)) return;
                _pendingTargetCommands.Remove(commandId);
            }

            completion.TrySetException(reason ?? new InvalidOperationException(
                "The interaction-target command could not be sent."));
        }

        /// <summary>
        ///     Atomically decides whether a missing target response is still ambiguous. A newer
        ///     route epoch carries the complete authoritative target and safely supersedes it;
        ///     otherwise this claims recovery before an acknowledgement can apply state.
        /// </summary>
        internal CanonicalRoutingTimeoutDisposition ClaimTargetCommandTimeout(
            string commandId,
            int commandRouteEpoch,
            Exception reason)
        {
            lock (_gate)
            {
                if (!_pendingTargetCommands.TryGetValue(
                        commandId ?? string.Empty,
                        out TaskCompletionSource<InteractionTargetResult> completion))
                    return CanonicalRoutingTimeoutDisposition.AlreadyResolved;

                _pendingTargetCommands.Remove(commandId);
                if (RouteEpoch > commandRouteEpoch)
                {
                    completion.TrySetException(reason);
                    return CanonicalRoutingTimeoutDisposition.Superseded;
                }

                _routingRecoveryClaimed = true;
                completion.TrySetException(reason);
                return CanonicalRoutingTimeoutDisposition.RequiresRecovery;
            }
        }

        /// <summary>
        ///     Gives one caller a cancellable view of a canonical command without cancelling the
        ///     command itself. A timeout only means that caller stopped waiting; the service may
        ///     still acknowledge the command later, and that acknowledgement must still reconcile
        ///     the room projection.
        /// </summary>
        private static async Task<T> WaitForCommandAsync<T>(
            Task<T> commandTask,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
                return await commandTask;

            var cancelled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => cancelled.TrySetCanceled(cancellationToken));
            Task<T> completed = await ConvaiTimers.WhenAny(commandTask, cancelled.Task);
            if (!ReferenceEquals(completed, commandTask))
            {
                // The canonical task remains alive for reconciliation. Observe a later rejection
                // after this caller has left so it cannot surface as an unobserved task exception.
                _ = commandTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            return await completed;
        }

        internal void CompleteTargetCommand(
            string commandId,
            string status,
            string message,
            string activeMembershipId,
            string previousMembershipId,
            int routeEpoch,
            bool changed)
        {
            TaskCompletionSource<InteractionTargetResult> completion;
            lock (_gate)
            {
                if (_routingRecoveryClaimed) return;
                if (!_pendingTargetCommands.TryGetValue(commandId ?? string.Empty, out completion)) return;
                _pendingTargetCommands.Remove(commandId);
            }

            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                bool rejectionCanonicalChanged;
                _isApplyingTargetCommandResponse = true;
                try
                {
                    rejectionCanonicalChanged = ApplyInteractionTarget(activeMembershipId, routeEpoch);
                }
                finally
                {
                    _isApplyingTargetCommandResponse = false;
                }
                completion.TrySetException(new InteractionTargetCommandException(
                    string.IsNullOrWhiteSpace(message) ? "Interaction target update failed." : message,
                    ActiveMembershipId,
                    RouteEpoch,
                    rejectionCanonicalChanged));
                return;
            }

            bool canonicalChanged;
            _isApplyingTargetCommandResponse = true;
            try
            {
                canonicalChanged = ApplyInteractionTarget(activeMembershipId, routeEpoch);
                if (!canonicalChanged)
                {
                    CharacterRoomMembership current;
                    lock (_gate)
                        _byMembershipId.TryGetValue(ActiveMembershipId ?? string.Empty, out current);
                    InteractionTargetChanged?.Invoke(current, current);
                }
            }
            finally
            {
                _isApplyingTargetCommandResponse = false;
            }
            completion.TrySetResult(new InteractionTargetResult(
                commandId,
                ActiveMembershipId,
                previousMembershipId,
                RouteEpoch,
                changed && canonicalChanged));
        }

    }

    /// <summary>Atomic outcome of claiming a canonical command whose response deadline elapsed.</summary>
    internal enum CanonicalRoutingTimeoutDisposition
    {
        AlreadyResolved = 0,
        Superseded,
        RequiresRecovery
    }

    /// <summary>The cancellable caller wait and authoritative lifetime of one roster command.</summary>
    internal readonly struct CharacterRosterCommandRegistration
    {
        internal CharacterRosterCommandRegistration(
            Task<CharacterRosterUpdateResult> callerCompletion,
            Task<CharacterRosterUpdateResult> canonicalCompletion)
        {
            CallerCompletion = callerCompletion;
            CanonicalCompletion = canonicalCompletion;
        }

        internal Task<CharacterRosterUpdateResult> CallerCompletion { get; }
        internal Task<CharacterRosterUpdateResult> CanonicalCompletion { get; }
    }

    /// <summary>
    ///     The two lifetimes of one interaction-target command: a caller may stop waiting, while
    ///     the room must continue tracking the service response as canonical state.
    /// </summary>
    internal readonly struct InteractionTargetCommandRegistration
    {
        internal InteractionTargetCommandRegistration(
            Task<InteractionTargetResult> callerCompletion,
            Task<InteractionTargetResult> canonicalCompletion)
        {
            CallerCompletion = callerCompletion;
            CanonicalCompletion = canonicalCompletion;
        }

        internal Task<InteractionTargetResult> CallerCompletion { get; }
        internal Task<InteractionTargetResult> CanonicalCompletion { get; }
    }

    /// <summary>
    ///     A rejected target command can still carry a newer canonical route. The exception keeps
    ///     that state beside the rejection so high-level observers can report both truths.
    /// </summary>
    internal sealed class InteractionTargetCommandException : InvalidOperationException
    {
        internal InteractionTargetCommandException(
            string message,
            string activeMembershipId,
            int routeEpoch,
            bool canonicalChanged) : base(message)
        {
            ActiveMembershipId = activeMembershipId ?? string.Empty;
            RouteEpoch = routeEpoch;
            CanonicalChanged = canonicalChanged;
        }

        internal string ActiveMembershipId { get; }
        internal int RouteEpoch { get; }
        internal bool CanonicalChanged { get; }
    }

    /// <summary>What the service did with a request to move the conversation.</summary>
    /// <remarks>
    ///     <see cref="Changed" /> is the part worth reading. Asking for the character that already
    ///     holds the conversation succeeds and moves nothing, and a caller that treats every
    ///     success as a move will announce a switch that did not happen.
    /// </remarks>
    public readonly struct InteractionTargetResult
    {
        /// <summary>Creates the result. The SDK fills this in from the service's reply.</summary>
        public InteractionTargetResult(
            string commandId,
            string activeMembershipId,
            string previousMembershipId,
            int routeEpoch,
            bool changed)
        {
            CommandId = commandId ?? string.Empty;
            ActiveMembershipId = activeMembershipId ?? string.Empty;
            PreviousMembershipId = previousMembershipId ?? string.Empty;
            RouteEpoch = routeEpoch;
            Changed = changed;
        }

        /// <summary>The service's id for this request, for correlating logs.</summary>
        public string CommandId { get; }

        /// <summary>The seat the conversation is routed to now.</summary>
        public string ActiveMembershipId { get; }

        /// <summary>The seat it was routed to before. Empty when there was none.</summary>
        public string PreviousMembershipId { get; }

        /// <summary>The room's routing epoch after this request.</summary>
        public int RouteEpoch { get; }

        /// <summary>Whether this actually moved the conversation, or asked for where it already was.</summary>
        public bool Changed { get; }
    }

    /// <summary>
    ///     What one live roster edit did — returned by <c>ConvaiRoomManager.AddCharacterAsync</c>
    ///     and <c>RemoveCharacterAsync</c>.
    /// </summary>
    /// <remarks>
    ///     A member arrives <see cref="CharacterRoomStatus.Starting" />: the service has accepted it
    ///     into the room and has not announced it yet. Await the new membership's
    ///     <see cref="CharacterRoomMembership.WaitUntilReadyAsync" /> before talking to it.
    /// </remarks>
    public sealed class CharacterRosterUpdateResult
    {
        internal CharacterRosterUpdateResult(
            string commandId,
            IReadOnlyList<CharacterRoomMembership> added,
            IReadOnlyList<CharacterRoomMembership> removed,
            string activeMembershipId,
            int routeEpoch,
            int rosterEpoch)
        {
            CommandId = commandId ?? string.Empty;
            Added = added ?? Array.Empty<CharacterRoomMembership>();
            Removed = removed ?? Array.Empty<CharacterRoomMembership>();
            ActiveMembershipId = activeMembershipId ?? string.Empty;
            RouteEpoch = routeEpoch;
            RosterEpoch = rosterEpoch;
        }

        /// <summary>The service's id for this edit, for correlating logs.</summary>
        public string CommandId { get; }

        /// <summary>The seats this edit created. Never null; empty for a removal.</summary>
        public IReadOnlyList<CharacterRoomMembership> Added { get; }

        /// <summary>The seats this edit freed. Never null; empty for an addition.</summary>
        public IReadOnlyList<CharacterRoomMembership> Removed { get; }

        /// <summary>The seat the conversation is routed to after the edit.</summary>
        /// <remarks>
        ///     Removing the character that held the conversation moves it, so this can name a
        ///     different character than before the call.
        /// </remarks>
        public string ActiveMembershipId { get; }

        /// <summary>The room's routing epoch after the edit.</summary>
        public int RouteEpoch { get; }

        /// <summary>The room's roster epoch after the edit.</summary>
        public int RosterEpoch { get; }
    }

    /// <summary>Thrown when the service refuses a live roster edit.</summary>
    /// <remarks>
    ///     The room carries on with the roster it has. Catch this rather than letting it escape: a
    ///     refused edit is an ordinary outcome — a plan limit, a character the account cannot use —
    ///     not a broken SDK.
    /// </remarks>
    public sealed class CharacterRosterUpdateException : InvalidOperationException
    {
        internal CharacterRosterUpdateException(string code, string message) : base(message)
        {
            Code = code ?? string.Empty;
        }

        /// <summary>The service's refusal code. Empty when it gave none.</summary>
        /// <remarks>Match on this rather than on the message, which is written for a human.</remarks>
        public string Code { get; }
    }

    internal interface IMultiCharacterSessionRegistry
    {
        MultiCharacterRoomSession CurrentMultiCharacterSession { get; }
        MultiCharacterRoomSession Configure(RoomDetails details, IReadOnlyList<IConvaiCharacterAgent> characters);
        bool TryClaimMultiCharacterSessionForRecovery(MultiCharacterRoomSession expected);
        void ClearMultiCharacterSession();
    }

    internal interface ICharacterInstanceAudioRegistry
    {
        void SetAudioSource(IConvaiCharacterAgent character, AudioSource source);
        bool TryGetAudioSource(IConvaiCharacterAgent character, out AudioSource source);
    }
}
