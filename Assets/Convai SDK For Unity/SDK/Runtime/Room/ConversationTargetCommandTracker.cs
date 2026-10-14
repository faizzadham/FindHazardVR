using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Components;

namespace Convai.Runtime.Room
{
    /// <summary>Operation-specific observation of one scripted target request.</summary>
    internal sealed class ConversationTargetRequestObservation
    {
        private readonly TaskCompletionSource<InteractionTargetResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deferred;
        private int _registered;

        internal bool IsDeferred => Volatile.Read(ref _deferred) != 0;
        internal bool IsRegistered => Volatile.Read(ref _registered) != 0;
        internal Task<InteractionTargetResult> Completion => _completion.Task;

        internal void MarkDeferred() => Interlocked.Exchange(ref _deferred, 1);

        internal void Register(Task<InteractionTargetResult> canonicalCompletion)
        {
            if (canonicalCompletion == null)
            {
                Fail(new InvalidOperationException("The canonical target command did not provide a completion task."));
                return;
            }

            Interlocked.Exchange(ref _registered, 1);
            _ = ForwardCanonicalCompletionAsync(canonicalCompletion);
        }

        internal void Fail(Exception exception) =>
            _completion.TrySetException(exception ?? new InvalidOperationException(
                "The conversation-target request failed before canonical registration."));

        private async Task ForwardCanonicalCompletionAsync(Task<InteractionTargetResult> canonicalCompletion)
        {
            try
            {
                _completion.TrySetResult(await canonicalCompletion);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }

    /// <summary>
    ///     Tracks the change of conversation target that is in flight right now.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         While a change is in flight the send window is closed: the chat field, the microphone
    ///         and the send path all refuse, because the room still routes to the character being
    ///         moved away from while the service is already ending that character's turn, and
    ///         anything sent lands between the two.
    ///     </para>
    ///     <para>
    ///         <b>Two changes can overlap.</b> A scripted <c>TalkTo</c> does not wait for an
    ///         automatic switch to finish. A plain boolean cannot tell "my command finished" from
    ///         "another unresolved command is still authoritative", so each command carries a
    ///         generation. A newer target acknowledgement may retire older target generations, but
    ///         roster generations remain until their command-correlated deltas resolve.
    ///     </para>
    ///     <para>
    ///         Kept as a plain object over plain state, like the planners beside it, so the rule can
    ///         be tested without a room, a session or a scene.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationTargetCommandTracker
    {
        private int _generation;
        private readonly Dictionary<int, ConvaiCharacter> _inFlight = new();

        /// <summary>Whether a change of target is on its way to the service.</summary>
        internal bool IsInFlight => _inFlight.Count > 0;

        /// <summary>
        ///     The character the conversation is moving to, while it is still moving. <c>null</c>
        ///     when nothing is in flight.
        /// </summary>
        /// <remarks>
        ///     Naming the character being moved to is the honest answer to "who am I talking to":
        ///     the conversation is already on its way there, and availability treats the window as
        ///     not-yet-ready so the send is refused rather than swallowed.
        /// </remarks>
        internal ConvaiCharacter Pending { get; private set; }

        /// <summary>
        ///     Claims the in-flight state for a new command and returns the token that releases it.
        /// </summary>
        internal int Begin(ConvaiCharacter target)
        {
            int generation = ++_generation;
            _inFlight[generation] = target;
            Pending = target;
            return generation;
        }

        /// <summary>
        ///     Releases the state, if this command still owns it.
        /// </summary>
        /// <remarks>
        ///     A command that has been superseded releases nothing: the newer one is still in
        ///     flight, and its window has to stay closed until it finishes. Safe to call more than
        ///     once, and safe to call for a command that never owned the state.
        /// </remarks>
        internal void Complete(int generation)
        {
            if (!_inFlight.Remove(generation)) return;
            if (_inFlight.Count == 0)
            {
                Pending = null;
                return;
            }

            int newest = int.MinValue;
            ConvaiCharacter newestTarget = null;
            foreach (KeyValuePair<int, ConvaiCharacter> entry in _inFlight)
                if (entry.Key > newest)
                {
                    newest = entry.Key;
                    newestTarget = entry.Value;
                }
            Pending = newestTarget;
        }

        /// <summary>Drops any in-flight state, for a room that has gone away.</summary>
        /// <remarks>
        ///     The generation is raised as well, so a command still in flight against the old room
        ///     cannot release the state the next one claims.
        /// </remarks>
        internal void Reset()
        {
            _generation++;
            _inFlight.Clear();
            Pending = null;
        }
    }

    /// <summary>
    ///     Owns the input and microphone gates across the two lifetimes of a target command.
    /// </summary>
    /// <remarks>
    ///     A caller timeout is not a canonical answer. Once the room has registered the command,
    ///     only its eventual response (or session retirement) may reopen input; otherwise a late
    ///     acknowledgement can move backend routing after text and audio have already resumed.
    /// </remarks>
    internal sealed class ConversationTargetRoutingLease
    {
        private readonly object _gate = new();
        private readonly Action _release;
        private readonly Action _releaseAfterAmbiguousRecovery;
        private readonly Action _releaseAfterAuthoritativeTargetResponse;
        private bool _canonicalCommandRegistered;
        private bool _released;

        internal ConversationTargetRoutingLease(
            Action release,
            Action releaseAfterAmbiguousRecovery = null,
            Action releaseAfterAuthoritativeTargetResponse = null)
        {
            _release = release ?? throw new ArgumentNullException(nameof(release));
            _releaseAfterAmbiguousRecovery = releaseAfterAmbiguousRecovery ?? _release;
            _releaseAfterAuthoritativeTargetResponse = releaseAfterAuthoritativeTargetResponse ?? _release;
        }

        internal bool CanonicalCommandRegistered
        {
            get
            {
                lock (_gate) return _canonicalCommandRegistered;
            }
        }

        /// <summary>Transfers ownership from the caller's wait to the canonical response.</summary>
        internal void MarkCanonicalCommandRegistered()
        {
            lock (_gate) _canonicalCommandRegistered = true;
        }

        /// <summary>
        ///     Releases only when setup failed before the room could track a canonical response.
        /// </summary>
        internal void ReleaseAfterCallerCompletion()
        {
            bool release;
            lock (_gate) release = !_canonicalCommandRegistered && TryClaimRelease();
            if (release) _release();
        }

        /// <summary>Releases after the canonical response completes or the session retires it.</summary>
        internal void ReleaseAfterCanonicalCompletion()
        {
            bool release;
            lock (_gate) release = TryClaimRelease();
            if (release) _release();
        }

        /// <summary>
        ///     Releases this target command and any older target commands that its authoritative
        ///     response supersedes. Newer target commands and every roster command remain gated.
        /// </summary>
        internal void ReleaseAfterAuthoritativeTargetResponse()
        {
            bool release;
            lock (_gate) release = TryClaimRelease();
            if (release) _releaseAfterAuthoritativeTargetResponse();
        }

        /// <summary>
        ///     Releases after fail-closed room recovery without restoring transport audio while
        ///     disconnect is still running.
        /// </summary>
        internal void ReleaseAfterAmbiguousRecovery()
        {
            bool release;
            lock (_gate) release = TryClaimRelease();
            if (release) _releaseAfterAmbiguousRecovery();
        }

        private bool TryClaimRelease()
        {
            if (_released) return false;
            _released = true;
            return true;
        }
    }

    /// <summary>Performs the fail-closed recovery for a target command that never resolves.</summary>
    /// <remarks>
    ///     The order is the contract: make input unavailable, retire the ambiguous room projection,
    ///     then start transport teardown. Only after this returns may the routing lease release. A
    ///     newer canonical route epoch supersedes an older missing response, so that older observer
    ///     must release only its own generation instead of disconnecting a reconciled room.
    /// </remarks>
    internal static class ConversationTargetAmbiguityRecovery
    {
        internal static bool RecoverIfCurrent(
            bool isCurrentSession,
            int commandRouteEpoch,
            int currentRouteEpoch,
            Action markRoomUnavailable,
            Action retireSession,
            Action beginDisconnect)
        {
            if (!isCurrentSession || currentRouteEpoch > commandRouteEpoch) return false;

            markRoomUnavailable?.Invoke();
            retireSession?.Invoke();
            beginDisconnect?.Invoke();
            return true;
        }
    }

    /// <summary>
    ///     Holds the most recent scripted target request while the player's current utterance pins
    ///     the route. The request belongs to one room and one membership, so a reconnect or roster
    ///     change cannot replay it against a different seat.
    /// </summary>
    internal sealed class ConversationTargetRequestDeferral
    {
        private MultiCharacterRoomSession _session;
        private string _membershipId;
        private ConvaiCharacter _character;

        internal bool IsPending => _session != null && !string.IsNullOrWhiteSpace(_membershipId);

        internal void Hold(
            MultiCharacterRoomSession session,
            string membershipId,
            ConvaiCharacter character)
        {
            _session = session;
            _membershipId = membershipId;
            _character = character;
        }

        /// <summary>
        ///     Takes the held request only if its exact room membership still represents the same
        ///     character. Taking always clears it; a stale request must not linger into another
        ///     speaking boundary.
        /// </summary>
        internal bool TryTake(
            MultiCharacterRoomSession currentSession,
            out string membershipId,
            out ConvaiCharacter character)
        {
            MultiCharacterRoomSession heldSession = _session;
            membershipId = _membershipId;
            character = _character;
            Clear();

            if (!ReferenceEquals(heldSession, currentSession) ||
                string.IsNullOrWhiteSpace(membershipId))
                return false;

            CharacterRoomMembership membership = currentSession?.FindByMembershipId(membershipId);
            return membership != null && ReferenceEquals(membership.Character, character);
        }

        internal void Clear()
        {
            _session = null;
            _membershipId = null;
            _character = null;
        }
    }
}
