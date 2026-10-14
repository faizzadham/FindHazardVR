/// <summary>
///     ConvaiManager partial: whether the player can talk right now, for whoever they are addressing.
/// </summary>

using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Logging;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Conversation;
using Convai.Runtime.Logging;
using Convai.Runtime.Room;
using Convai.Runtime.Utilities;
using Convai.Domain.EventSystem;
using Convai.Shared.Compatibility;
using UnityEngine;

// Same name, two surfaces: the C# event on this class and the domain event on the hub. Inside the
// class the member wins, so the type is aliased rather than either of them renamed.
using ConversationAvailabilityChangedEvent =
    Convai.Domain.DomainEvents.Runtime.ConversationAvailabilityChanged;

namespace Convai.Runtime.Components
{
    public partial class ConvaiManager
    {
        private ConvaiConversationAvailability _conversationAvailability =
            ConvaiConversationAvailability.NoCharacter;

        private readonly List<ConversationTargetCandidate> _preConnectCandidates = new();
        private readonly List<ConvaiCharacter> _preConnectCharacters = new();
        private ConvaiCharacter _lookedAtCharacter;
        private ConvaiCharacter _reportedLookedAtInitialCharacter;
        private float _nextLookedAtEvaluationTime;

        /// <summary>
        ///     The character the player is talking to, in either shape of room and before either
        ///     exists.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Three answers in order of authority. <see cref="ConversationTarget" /> is the room's
        ///         own, and wins whenever there is a room to ask. Before that, the player is still
        ///         addressing somebody — they are looking at them — and saying so is the difference
        ///         between "Connecting to James…" and naming whoever happens to be first in scene
        ///         order while the player looks at somebody else. Last, the character who would take
        ///         the first turn, for a player looking at nobody.
        ///     </para>
        ///     <para>
        ///         The middle answer is a prediction, and an honest one: the moment the room comes up,
        ///         targeting commits to whoever the player is looking at, so it names the character
        ///         they are about to be talking to.
        ///     </para>
        /// </remarks>
        public ConvaiCharacter AddressedCharacter
        {
            get
            {
                // A move already sent outranks the room's answer, which still names the character
                // being moved away from until the acknowledgement lands.
                ConvaiCharacter movingTo = PendingConversationTarget;
                if (movingTo != null) return movingTo;

                ConvaiCharacter roomsAnswer = ConversationTarget ?? ActiveConversationCharacter;
                if (!IsConversationSettled) return _lookedAtCharacter ?? roomsAnswer;

                return IsLookedAtCharacterStillJoining ? _lookedAtCharacter : roomsAnswer;
            }
        }

        /// <summary>
        ///     Whether the player is looking at a character the room holds but cannot route to yet.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This is the one case where the look-at answer outranks a settled room, and it is
        ///         safe for exactly one reason: a character the service has not announced cannot
        ///         receive anything, so naming it makes the whole surface refuse rather than send
        ///         somewhere else. The prompt says its name, the field closes, and
        ///         <c>TrySendTextMessage</c> refuses — all three agree.
        ///     </para>
        ///     <para>
        ///         It is deliberately narrow. A looked-at character that <i>can</i> hear is not named
        ///         here, because the message would still go to whoever holds the conversation until
        ///         targeting moves it — which it does, within the switch delay. Naming it early would
        ///         be the one thing this whole surface exists to prevent.
        ///     </para>
        ///     <para>
        ///         The membership is what is asked, not the character's own readiness flag: a
        ///         character that is not in this room at all has no membership, and answering
        ///         "getting ready" about somebody who is not joining anything would be a permanent
        ///         lie rather than a two-second one.
        ///     </para>
        /// </remarks>
        private bool IsLookedAtCharacterStillJoining =>
            _lookedAtCharacter != null &&
            _lookedAtCharacter.RoomMembershipStatus == CharacterRoomStatus.Starting;

        /// <summary>
        ///     Whether the room has finished deciding, so its own answer is worth more than a guess.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Targeting will not run until the roster's initial character is ready, and until
        ///         then <see cref="ConversationTarget" /> reports the membership the room opened with
        ///         — which is whoever was configured to speak first, not whoever the player is
        ///         looking at. Trusting it during that window told a player looking straight at one
        ///         character that they were connecting to another.
        ///     </para>
        ///     <para>
        ///         Once it is settled the order reverses, and deliberately: the live target carries
        ///         the switch margin and delay that stop a glance from moving the conversation, and
        ///         a raw look-at answer would throw all of that away.
        ///     </para>
        /// </remarks>
        private bool IsConversationSettled
        {
            get
            {
                if (_roomManager == null || !_roomManager.IsConnected) return false;

                MultiCharacterRoomSession session = _roomManager.CurrentMultiCharacterSession;
                return session == null || session.IsReady;
            }
        }

        /// <summary>
        ///     Opens the room on the character the player is looking at, when nothing else has been
        ///     chosen.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A room needs somebody to speak first, and the fallback was the first character in
        ///         scene order. In a two-character scene that means a player who spent the whole
        ///         connection looking straight at one character is answered by the other, and only
        ///         then does targeting move the conversation across. It reads as the game ignoring
        ///         them, and no setting is obviously wrong.
        ///     </para>
        ///     <para>
        ///         <b>Only the fallback changes.</b> An explicitly assigned Initial Character still
        ///         wins outright — a project that chose one meant it.
        ///     </para>
        ///     <para>
        ///         Applied here, on the snapshot the room is composed from, rather than in ownership
        ///         resolution. Ownership feeds change detection: making it follow the player's gaze
        ///         would rebuild the room composition every time they looked around, and in a
        ///         connected room it would read as the starting character changing and queue a
        ///         reconnect. So it is deliberately confined to a room that is not up yet.
        ///     </para>
        /// </remarks>
        private RoomOwnershipSnapshot PreferLookedAtInitialCharacter(RoomOwnershipSnapshot snapshot)
        {
            if (snapshot == null) return null;
            if (_explicitConversationTarget != null) return snapshot;
            if (_roomManager != null && _roomManager.IsConnected) return snapshot;

            // Solved here rather than read from the cached tick. The room is composed in the same
            // frame a character is adopted, and the cached answer is one frame old — computed while
            // that character was not yet owned, so there was nothing to choose between and it was
            // null. Measured: composition resolved 48 ms before the connect began, on a look-at
            // computed before the second character existed.
            ConvaiCharacter lookedAt = _lookedAtCharacter ?? SolveLookedAtCharacter();
            if (lookedAt == null || ReferenceEquals(snapshot.ConversationTarget?.Character, lookedAt))
                return snapshot;

            // Only a character the room would actually accept can start it.
            IReadOnlyList<IConvaiCharacterAgent> characters = snapshot.Characters;
            bool connectable = false;
            for (int i = 0; i < characters.Count; i++)
            {
                if (ReferenceEquals(characters[i], lookedAt))
                {
                    connectable = true;
                    break;
                }
            }

            if (!connectable) return snapshot;

            // Said out loud because ownership has already reported its own pick by scene order, and
            // two lines naming different characters would be worse than either alone. Once per
            // character, so a room that composes several times before connecting says it once.
            if (!ReferenceEquals(_reportedLookedAtInitialCharacter, lookedAt))
            {
                _reportedLookedAtInitialCharacter = lookedAt;
                ConvaiLogger.Info(
                    $"[ConvaiManager] The conversation will start on '{lookedAt.CharacterName}' — " +
                    "the character the player is looking at. Assign Convai Manager > Initial " +
                    "Character to always start on the same one instead.",
                    LogCategory.Bootstrap);
            }

            return new RoomOwnershipSnapshot(
                snapshot.Player,
                characters,
                new ActiveConversationTarget(lookedAt));
        }

        /// <summary>
        ///     Works out who the player is looking at while there is no room to decide it properly.
        /// </summary>
        /// <remarks>
        ///     Reuses the same solver targeting uses, but only to answer — nothing is sent, because
        ///     there is nothing to send it to yet. Evaluated on the same schedule as targeting so the
        ///     name does not flicker, and skipped entirely once a room exists.
        /// </remarks>
        private void RefreshLookedAtCharacter()
        {
            // Kept running after the room settles, which it did not used to be. A character joining
            // a live room is in exactly the position every character is in during the first connect
            // — present, looked at, and not yet able to hear — and the player asked why the answer
            // was different the second time. It is the same question, so it gets the same answer.
            if (Time.unscaledTime < _nextLookedAtEvaluationTime) return;
            _nextLookedAtEvaluationTime = Time.unscaledTime + TargetEvaluationIntervalSeconds;

            _lookedAtCharacter = SolveLookedAtCharacter();
        }

        private ConvaiCharacter SolveLookedAtCharacter()
        {
            // Manual mode means the project decides, and a custom provider is written against a live
            // conversation. Guessing on their behalf before the room exists would be inventing a
            // decision neither of them made.
            if (_conversationTargeting == null ||
                _conversationTargeting.Mode == ConversationTargetingMode.Manual ||
                _targetProvider != null)
                return null;

            Transform view = ResolveViewTransform();
            if (view == null) return null;

            _preConnectCharacters.Clear();
            _preConnectCandidates.Clear();

            IReadOnlyList<ConvaiCharacter> owned = Characters;
            for (int i = 0; i < owned.Count; i++)
            {
                ConvaiCharacter character = owned[i];
                if (character == null || !character.isActiveAndEnabled) continue;
                if (!IsCharacterSelectedForConnection(character)) continue;

                _preConnectCharacters.Add(character);
                _preConnectCandidates.Add(new ConversationTargetCandidate(
                    ConvaiObjectId.Of(character),
                    ResolveCachedAimPoint(character).Resolve(),
                    false));
            }

            if (_preConnectCharacters.Count < 2) return null;

            long chosen = ConversationTargetSolver.Solve(
                new ConversationTargetQuery(view.position, view.forward, _conversationTargeting),
                _preConnectCandidates);
            if (chosen == ConversationTargetSolver.NoTarget) return null;

            for (int i = 0; i < _preConnectCharacters.Count; i++)
            {
                if (ConvaiObjectId.Of(_preConnectCharacters[i]) == chosen)
                    return _preConnectCharacters[i];
            }

            return null;
        }

        /// <summary>
        ///     Whether the player can talk to <see cref="AddressedCharacter" /> right now, and if
        ///     not, what is in the way.
        /// </summary>
        /// <remarks>
        ///     This is what a chat field, a microphone button or a prompt should bind to. Binding to
        ///     <see cref="IsConnected" /> instead is the mistake this exists to remove: the room can
        ///     be connected while the addressed character has not been announced by the service, and
        ///     a message sent in that gap reaches nobody and is reported nowhere.
        /// </remarks>
        public ConvaiConversationAvailability ConversationAvailability => ResolveConversationAvailability();

        /// <summary>
        ///     Raised when <see cref="ConversationAvailability" /> changes — including when the
        ///     player starts addressing a different character, whose availability may differ.
        /// </summary>
        public event Action<ConvaiConversationAvailability> ConversationAvailabilityChanged;

        /// <summary>
        ///     Recomputes the verdict and raises the event when it moves.
        /// </summary>
        /// <remarks>
        ///     Polled rather than assembled from subscriptions on purpose. The verdict depends on the
        ///     room's state, the roster's opinion of one character, that character's own readiness,
        ///     whether it is speaking, and which character is being addressed at all — five sources
        ///     that change independently. Subscribing to all five means being wrong the day one of
        ///     them grows a new path; reading the answer costs an enum comparison and allocates
        ///     nothing.
        /// </remarks>
        private void TickConversationAvailability()
        {
            RefreshLookedAtCharacter();

            ConvaiConversationAvailability current = ResolveConversationAvailability();
            ConvaiCharacter addressed = AddressedCharacter;

            if (current == _conversationAvailability) return;

            ConvaiConversationAvailability previous = _conversationAvailability;
            _conversationAvailability = current;

            // Through SafeEventInvoker, like every other event surface in the SDK. A subscriber
            // throwing here used to take the publish below down with it, so the typed event surface
            // silently missed a transition the C# event had already delivered — the two disagreeing
            // about what happened is worse than either of them being late.
            SafeEventInvoker.Invoke(
                ConversationAvailabilityChanged,
                current,
                null,
                "ConvaiManager.ConversationAvailabilityChanged",
                LogCategory.SDK);

            IEventHub hub = EventsOrNull?.Raw;
            hub?.Publish(ConversationAvailabilityChangedEvent.Create(
                current,
                previous,
                addressed != null ? addressed.CharacterId : null,
                addressed != null ? addressed.CharacterName : null));
        }

        private ConvaiConversationAvailability ResolveConversationAvailability()
        {
            ConvaiCharacter addressed = AddressedCharacter;
            ConvaiConversationAvailability addressedAvailability = addressed != null
                ? addressed.ConversationAvailability
                : ConvaiConversationAvailability.NoCharacter;

            // A conversation part-way through moving cannot take anything. The character it is
            // moving to reports itself ready — it is in the room and announced — but the service
            // has not been told to route to it yet, and the one it is moving away from is having
            // its turn ended. Measured at 1.1 seconds, which is long enough to finish a sentence
            // into, and what was typed there was answered by neither character.
            return ConversationAvailabilityResolver.ApplyRoutingTransition(
                addressedAvailability,
                _roomManager?.IsConversationTargetRoutingInFlight ?? false);
        }
    }
}
