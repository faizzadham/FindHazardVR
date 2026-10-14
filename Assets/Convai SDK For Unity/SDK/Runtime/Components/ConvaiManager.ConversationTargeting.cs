using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Runtime.Conversation;
using Convai.Runtime.Logging;
using Convai.Runtime.Room;
using Convai.Runtime.Utilities;
using Convai.Shared.Compatibility;
using UnityEngine;

// The manager already has a C# event called ConversationTargetChanged, and inside this class the
// member wins over the type of the same name. Aliasing the domain event keeps both surfaces named
// for what they are — the pair of public names is the point, not an accident to rename around.
using ConversationTargetChangedEvent = Convai.Domain.DomainEvents.Runtime.ConversationTargetChanged;

namespace Convai.Runtime.Components
{
    /// <summary>
    ///     Keeps the conversation pointed at the character the player is addressing, in rooms that
    ///     hold more than one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this is not a component you add.</b> A scene where one character works must keep
    ///         working when a second one is dropped into it, and a rule that only takes effect once
    ///         somebody finds and adds a component is a manual step by another name. The policy
    ///         therefore lives on the manager that every working scene already has.
    ///     </para>
    ///     <para>
    ///         Rooms with one character never reach any of this: the single character holds the
    ///         conversation and there is nothing to decide.
    ///     </para>
    /// </remarks>
    public partial class ConvaiManager
    {
        /// <summary>
        ///     How often the target is re-evaluated. Fifteen times a second is below the delay any
        ///     switch waits out anyway, and spares a per-frame pass that would change nothing.
        /// </summary>
        private const float TargetEvaluationIntervalSeconds = 1f / 15f;

        [SerializeField]
        [HideInInspector]
        [Tooltip("How the character the player is talking to is chosen in multi-character rooms.")]
        private ConversationTargetingOptions _conversationTargeting = ConversationTargetingOptions.CreateDefault();

        [SerializeField]
        [HideInInspector]
        [Tooltip("Camera used as the player's view. Falls back to the main camera.")]
        private Camera _conversationViewCamera;

        private readonly List<ConversationTargetCandidate> _targetCandidates = new();
        private readonly List<ConvaiCharacter> _targetCharacters = new();
        private readonly Dictionary<long, CachedAimPoint> _targetAimPoints = new();
        private readonly List<long> _aimPointEvictions = new();
        private readonly ConversationTargetSwitchPolicy _targetSwitchPolicy = new();
        private readonly ConversationTargetRequestDeferral _deferredConversationTarget = new();

        private IConversationTargetProvider _targetProvider;
        private SubscriptionToken _playerSpeakingToken;
        private bool _playerSpeakingSubscribed;
        private bool _roomConversationTargetSubscribed;
        private bool _playerIsSpeaking;
        private bool _reportedMissingConversationView;
        private float _nextTargetEvaluationTime;
        private int _prunedAimPointRosterEpoch = -1;
        private ConversationTargetSwitchVerdict _lastTargetVerdict = ConversationTargetSwitchVerdict.AlreadyActive;

        /// <summary>
        ///     Why the conversation is where it is: the outcome of the most recent evaluation.
        /// </summary>
        /// <remarks>
        ///     Held targets are the hard thing to diagnose, because holding is what this is supposed to
        ///     do most of the time and a held target looks exactly like a broken one from outside.
        ///     This says which it is.
        /// </remarks>
        public ConversationTargetSwitchVerdict ConversationTargetingStatus => _lastTargetVerdict;

        /// <summary>Tuning for automatic conversation targeting. Never null.</summary>
        public ConversationTargetingOptions ConversationTargeting => _conversationTargeting;

        /// <summary>
        ///     The character the player is currently addressing, or <c>null</c> when the room holds no
        ///     multi-character session.
        /// </summary>
        public ConvaiCharacter ConversationTarget
        {
            get
            {
                MultiCharacterRoomSession session = _roomManager?.CurrentMultiCharacterSession;
                if (session == null) return null;
                CharacterRoomMembership membership = session.FindByMembershipId(session.ActiveMembershipId);
                return membership?.Character as ConvaiCharacter;
            }
        }

        /// <summary>
        ///     Raised after the client claims the routing window and just before it sends the move.
        /// </summary>
        /// <remarks>
        ///     Sending can still fail. <see cref="ConversationTargetChanged" /> fires on an
        ///     authoritative response, which is a round
        ///     trip later. Anything the service does as a consequence of the move — including ending
        ///     the previous character's turn — therefore reaches the game <i>before</i> that event, and
        ///     reading the two in order makes the cause look like the effect. Use this one to show the
        ///     player something immediately, and to timestamp the request when diagnosing.
        /// </remarks>
        public event Action<ConvaiCharacter> ConversationTargetRequested;

        /// <summary>Raised when a service response reconciles the authoritative target.</summary>
        /// <remarks>
        ///     This follows the canonical room response even when the original caller's wait timed
        ///     out. The argument is the character the service says owns the conversation, not
        ///     necessarily the character named by the original request, and can be <c>null</c> when
        ///     the authoritative response clears the route. A successful request for the already
        ///     active target still fires, preserving one terminal callback per explicit target
        ///     request. Roster/lifecycle transitions that reconcile in one routing window may be
        ///     coalesced to the final authoritative target.
        /// </remarks>
        public event Action<ConvaiCharacter> ConversationTargetChanged;

        /// <summary>
        ///     Points the conversation at one character. Safe to call at any time; it does nothing when
        ///     the room is not multi-character or the character is not in it.
        /// </summary>
        /// <remarks>
        ///     This is the verb for scripted conversations — a dialogue menu, a quest step, a trigger
        ///     volume. It does not require <see cref="ConversationTargetingMode.Manual" />, but under
        ///     the automatic modes the next evaluation may move the conversation again. If the
        ///     player is speaking, the most recent scripted request is held until that utterance
        ///     ends so one sentence cannot be split across two characters.
        /// </remarks>
        public void TalkTo(ConvaiCharacter character) => TalkToCore(character, null);

        internal ConversationTargetRequestObservation TalkToObserved(ConvaiCharacter character)
        {
            var observation = new ConversationTargetRequestObservation();
            TalkToCore(character, observation);
            return observation;
        }

        private void TalkToCore(
            ConvaiCharacter character,
            ConversationTargetRequestObservation observation)
        {
            if (character == null)
            {
                observation?.Fail(new ArgumentNullException(nameof(character)));
                return;
            }

            MultiCharacterRoomSession session = _roomManager?.CurrentMultiCharacterSession;
            CharacterRoomMembership membership = session?.FindByCharacter(character);
            if (membership == null)
            {
                ConvaiLogger.Warning(
                    $"[ConvaiManager] Cannot talk to '{character.gameObject.name}': it is not in the current " +
                    "room. Only characters that joined this connection can be addressed.",
                    LogCategory.SDK);
                observation?.Fail(new InvalidOperationException(
                    "The character is not in the current multi-character room."));
                return;
            }

            _targetSwitchPolicy.Reset();
            SendInteractionTarget(membership.MembershipId, character, observation);
        }

        /// <summary>
        ///     Replaces the SDK's rule for choosing the target. Pass <c>null</c> to restore the
        ///     behaviour configured by <see cref="ConversationTargeting" />.
        /// </summary>
        public void SetConversationTargetProvider(IConversationTargetProvider provider)
        {
            _targetProvider = provider;
            _targetSwitchPolicy.Reset();
        }

        private void TickConversationTargeting()
        {
            if (_deferredConversationTarget.IsPending && !IsPlayerMidUtterance)
                FlushDeferredConversationTarget();

            if (!TryBeginTargetEvaluation(out MultiCharacterRoomSession session))
                return;

            RefreshTargetCandidates(session);
            if (_targetCharacters.Count == 0) return;

            ConvaiCharacter current = ConversationTarget;
            bool currentCanStillHear = current != null && _targetCharacters.Contains(current);

            // One eligible character is normally nothing to decide. It becomes a decision when the
            // character holding the conversation is not one of them — disabled, or dropped by the
            // room — because then the player is addressing somebody who cannot answer, and the
            // conversation would sit there until something else moved it.
            if (_targetCharacters.Count < 2 && currentCanStillHear) return;

            // Resolved once per evaluation, and never invented. Measuring from a stand-in origin
            // would answer with whichever character happened to sit near it, which reads as the
            // conversation moving for no reason rather than as there being nothing to measure from.
            Transform view = ResolveViewTransform();
            if (view == null)
            {
                ReportMissingConversationView();
                return;
            }

            _reportedMissingConversationView = false;

            long proposedId = ResolveProposedTargetId(current, view);
            if (proposedId == ConversationTargetSolver.NoTarget)
            {
                // Nobody qualified. Holding is the right answer while the character holding the
                // conversation can still hear — that is the rule that stops a glance across an
                // empty room from clearing the target. It is the wrong answer when it cannot:
                // somebody who can answer beats somebody who cannot, whether or not the player
                // happens to be looking at them.
                if (currentCanStillHear) return;
                proposedId = ConvaiObjectId.Of(_targetCharacters[0]);
            }

            ConversationTargetSwitchVerdict verdict = _targetSwitchPolicy.Evaluate(
                proposedId,
                current != null ? ConvaiObjectId.Of(current) : ConversationTargetSolver.NoTarget,
                Time.unscaledTime,
                _conversationTargeting.SwitchDelaySeconds,
                IsPlayerMidUtterance,
                _roomManager.IsConversationTargetRoutingInFlight);

            RecordTargetingVerdict(verdict, proposedId);
            if (verdict != ConversationTargetSwitchVerdict.Commit)
                return;

            ConvaiCharacter chosen = FindCandidateById(proposedId);
            CharacterRoomMembership membership = session.FindByCharacter(chosen);
            if (membership != null) SendInteractionTarget(membership.MembershipId, chosen);
        }

        private bool TryBeginTargetEvaluation(out MultiCharacterRoomSession session)
        {
            session = null;
            if (_conversationTargeting == null) return false;
            if (_targetProvider == null && _conversationTargeting.Mode == ConversationTargetingMode.Manual)
                return false;

            if (Time.unscaledTime < _nextTargetEvaluationTime) return false;
            _nextTargetEvaluationTime = Time.unscaledTime + TargetEvaluationIntervalSeconds;

            // "The player is speaking" is a latch fed by a pair of events, and a session that drops
            // mid-sentence delivers the first without ever delivering the second. Left alone the latch
            // would hold the conversation in place for the rest of the run, so a disconnected room
            // clears it rather than inheriting it.
            if (_roomManager == null || !_roomManager.IsConnected)
            {
                _playerIsSpeaking = false;
                _deferredConversationTarget.Clear();
                _targetSwitchPolicy.Reset();

                return false;
            }

            session = _roomManager.CurrentMultiCharacterSession;
            return session is { IsReady: true };
        }

        private long ResolveProposedTargetId(ConvaiCharacter current, Transform view)
        {
            if (_targetProvider == null)
                return ConversationTargetSolver.Solve(
                    new ConversationTargetQuery(view.position, view.forward, _conversationTargeting),
                    _targetCandidates);

            ConvaiCharacter chosen;
            try
            {
                chosen = _targetProvider.ResolveTarget(_targetCharacters, current);
            }
            catch (Exception exception)
            {
                // One faulty provider must not take the conversation down with it.
                ConvaiLogger.Error(
                    $"[ConvaiManager] The conversation target provider threw ({exception.GetType().Name}). " +
                    "Leaving the conversation where it is.",
                    LogCategory.SDK);
                return ConversationTargetSolver.NoTarget;
            }

            return chosen != null && _targetCharacters.Contains(chosen)
                ? ConvaiObjectId.Of(chosen)
                : ConversationTargetSolver.NoTarget;
        }

        /// <summary>
        ///     Rebuilds the candidate list from the room's current membership.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This reads the roster every evaluation rather than caching it against
        ///         <see cref="MultiCharacterRoomSession.RosterEpoch" />, because the epoch answers a
        ///         different question. A character's readiness is not part of the roster's identity:
        ///         memberships appear together and then become <see cref="CharacterRoomStatus.Ready" />
        ///         one at a time, and <c>MarkReady</c> does not advance the epoch. Nor does
        ///         <see cref="MultiCharacterRoomSession.IsReady" /> mean everyone is ready — it means
        ///         the <i>initial</i> character is.
        ///     </para>
        ///     <para>
        ///         Caching on the epoch therefore latched whatever happened to be ready on the first
        ///         evaluation. Where that was one character, the roster never looked bigger again and
        ///         the conversation could never move — a fault whose appearance depended on connection
        ///         timing, which is the worst way for one to appear.
        ///     </para>
        ///     <para>
        ///         Only the body-extent lookup is cached, because that is the part that costs a
        ///         hierarchy search; the rest is a handful of field reads fifteen times a second.
        ///     </para>
        /// </remarks>
        private void RefreshTargetCandidates(MultiCharacterRoomSession session)
        {
            _targetCharacters.Clear();
            _targetCandidates.Clear();

            PruneAimPointCache(session.RosterEpoch);

            string activeMembershipId = session.ActiveMembershipId;
            IReadOnlyList<CharacterRoomMembership> memberships = session.Characters;
            for (int i = 0; i < memberships.Count; i++)
            {
                CharacterRoomMembership membership = memberships[i];
                if (membership.Status != CharacterRoomStatus.Ready) continue;
                if (membership.Character is not ConvaiCharacter character) continue;
                if (character == null || !character.isActiveAndEnabled) continue;

                _targetCharacters.Add(character);
                _targetCandidates.Add(new ConversationTargetCandidate(
                    ConvaiObjectId.Of(character),
                    ResolveCachedAimPoint(character).Resolve(),
                    string.Equals(membership.MembershipId, activeMembershipId, StringComparison.Ordinal)));
            }
        }

        /// <summary>
        ///     Publishes the reason the conversation moved or stayed, once per change of reason.
        /// </summary>
        /// <remarks>
        ///     Logged at <c>Debug</c> so it costs nothing at the default verbosity, and only when the
        ///     reason changes so that a steady hold does not fill the Console. Reading it needs
        ///     <c>Edit &gt; Project Settings &gt; Convai SDK &gt; Global Log Level = Debug</c>.
        /// </remarks>
        private void RecordTargetingVerdict(ConversationTargetSwitchVerdict verdict, long proposedId)
        {
            if (verdict == _lastTargetVerdict) return;
            _lastTargetVerdict = verdict;

            if (!LoggingConfig.IsDebugEnabled(LogCategory.SDK)) return;

            ConvaiCharacter proposed = FindCandidateById(proposedId);
            ConvaiLogger.Debug(
                $"[ConvaiManager] Conversation target {verdict} " +
                $"(proposed '{(proposed != null ? proposed.gameObject.name : "none")}', " +
                $"{_targetCharacters.Count} ready characters).",
                LogCategory.SDK);
        }

        /// <summary>
        ///     One resolved aim point and the character it was resolved for.
        /// </summary>
        /// <remarks>
        ///     The character is kept beside the entry rather than used as the dictionary key. A
        ///     destroyed <c>UnityEngine.Object</c> is still a live dictionary key but compares equal
        ///     to <c>null</c>, which makes "is this entry dead" and "can I remove this entry" two
        ///     different questions about the same reference. Keying by id keeps them separate: the id
        ///     removes the entry, the reference answers whether it should be removed.
        /// </remarks>
        private readonly struct CachedAimPoint
        {
            internal CachedAimPoint(ConvaiCharacter character, ConversationAimPoint point)
            {
                Character = character;
                Point = point;
            }

            internal ConvaiCharacter Character { get; }
            internal ConversationAimPoint Point { get; }
        }

        /// <summary>
        ///     Drops cached aim points for characters that no longer exist.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         An entry is never read again once its character is destroyed, but nothing removes
        ///         it either, and it holds that character's resolved renderer array alive. In a room
        ///         whose roster changes during play — the case this feature exists for — that is one
        ///         retained entry per character that has ever left.
        ///     </para>
        ///     <para>
        ///         Run when the roster's identity changes, so the steady state costs two integer
        ///         comparisons and allocates nothing. The cache size is the second trigger and not a
        ///         belt-and-braces one: the epoch is the service's number, raised when the service
        ///         chooses to raise it, and a cache holding more characters than the manager owns is
        ///         drift this can see for itself.
        ///     </para>
        ///     <para>
        ///         Only destroyed characters are evicted: one that is merely disabled is coming back,
        ///         and recomputing its aim point means another hierarchy search for no gain.
        ///     </para>
        /// </remarks>
        private void PruneAimPointCache(int rosterEpoch)
        {
            if (_targetAimPoints.Count == 0)
            {
                _prunedAimPointRosterEpoch = rosterEpoch;
                return;
            }

            if (rosterEpoch == _prunedAimPointRosterEpoch && _targetAimPoints.Count <= Characters.Count)
                return;
            _prunedAimPointRosterEpoch = rosterEpoch;

            _aimPointEvictions.Clear();
            foreach (KeyValuePair<long, CachedAimPoint> entry in _targetAimPoints)
                if (entry.Value.Character == null)
                    _aimPointEvictions.Add(entry.Key);

            for (int i = 0; i < _aimPointEvictions.Count; i++)
                _targetAimPoints.Remove(_aimPointEvictions[i]);
            _aimPointEvictions.Clear();
        }

        /// <summary>
        ///     The point to measure a character by, resolved once per character and reused.
        /// </summary>
        internal ConversationAimPoint ResolveCachedAimPoint(ConvaiCharacter character)
        {
            long id = ConvaiObjectId.Of(character);
            if (_targetAimPoints.TryGetValue(id, out CachedAimPoint cached) && cached.Character != null)
                return cached.Point;

            ConversationAimPoint resolved = ConversationAimPoint.For(character.transform);
            _targetAimPoints[id] = new CachedAimPoint(character, resolved);
            return resolved;
        }

        private ConvaiCharacter FindCandidateById(long id)
        {
            for (int i = 0; i < _targetCharacters.Count; i++)
                if (_targetCharacters[i] != null && ConvaiObjectId.Of(_targetCharacters[i]) == id)
                    return _targetCharacters[i];
            return null;
        }

        /// <summary>
        ///     Says once that there is nothing to measure the conversation from.
        /// </summary>
        /// <remarks>
        ///     Reachable when the camera and the player are both gone from a room that is still
        ///     connected — destroying the player rig mid-session does it. Targeting stops rather than
        ///     guessing, and the conversation stays with whoever already has it.
        /// </remarks>
        private void ReportMissingConversationView()
        {
            if (_reportedMissingConversationView) return;
            _reportedMissingConversationView = true;

            ConvaiLogger.Warning(
                "[ConvaiManager] No camera or player to measure from, so the conversation is staying " +
                "with the character that has it. Tag a camera as MainCamera, or set Convai Manager > " +
                "Who The Player Talks To > Player Camera.",
                LogCategory.SDK);
        }

        /// <summary>
        ///     The transform standing in for the player's view. The camera is the honest answer in
        ///     first and third person alike; the player object is the fallback for scenes that have no
        ///     camera tagged.
        /// </summary>
        private Transform ResolveViewTransform() => ResolveViewTransform(Camera.main);

        internal Transform ResolveViewTransform(Camera automaticMainCamera)
        {
            if (_conversationViewCamera != null) return _conversationViewCamera.transform;

            // Camera.main is a fallback, not an authored override. Do not write it into the
            // serialized field: a project may replace or retag its main camera at runtime, and the
            // next evaluation must follow the new view instead of retaining the first one forever.
            if (automaticMainCamera != null) return automaticMainCamera.transform;

            // Authoring tools can resolve the explicitly assigned player before a runtime host exists.
            ConvaiPlayer player = Player;
            if (player == null && !UnityEngine.Application.isPlaying) player = _explicitPlayer;
            return player != null ? player.transform : null;
        }

        /// <summary>
        ///     Whether the player is part-way through saying something.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This is the only thing that pins the target. Moving it while the player is still
        ///         talking would send the back half of a sentence to a different character, and no
        ///         later correction recovers from that.
        ///     </para>
        ///     <para>
        ///         <b>A speaking character does not pin it.</b> The service allows only one character
        ///         to speak at a time and cancels the previous one on every target change, so refusing
        ///         to move while a character is answering would not protect that answer — it would only
        ///         delay a switch the service is going to make abrupt regardless. Holding here bought
        ///         nothing and read as being stuck.
        ///     </para>
        /// </remarks>
        private bool IsPlayerMidUtterance =>
            _playerIsSpeaking || (_roomManager?.IsPushToTalkUtteranceInProgress ?? false);

        /// <summary>
        ///     Sends one change of conversation target and reports every phase of it.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Everything after the state is claimed is inside the try.</b> The in-flight
        ///         state closes the send window — the field, the microphone and the send path all
        ///         refuse while it is set — so anything that can throw between claiming it and the
        ///         <c>finally</c> that releases it can lock the conversation for the rest of the
        ///         run. Raising the first event outside the try did exactly that: one subscriber
        ///         throwing left the flag set forever, the switch policy answered
        ///         <c>HeldForPendingCommand</c> from then on, and availability stayed
        ///         <c>Preparing</c> with nothing in the Console to connect it to.
        ///     </para>
        ///     <para>
        ///         The events go out through <see cref="SafeEventInvoker" /> as well, which is what
        ///         every other event surface in the SDK does: one game's faulty handler is that
        ///         game's bug to see in the Console, not a reason for the conversation to stop.
        ///     </para>
        ///     <para>
        ///         <b>Two sends can overlap.</b> A scripted <see cref="TalkTo" /> does not wait for
        ///         an automatic switch to finish, so the state has to belong to the most recent send
        ///         rather than to whichever completes last. Without the generation check the first
        ///         send's completion cleared the second's pending target, and the window the
        ///         pending target exists to close silently reopened — precisely when a game was
        ///         driving the conversation itself.
        ///     </para>
        /// </remarks>
        private async void SendInteractionTarget(
            string membershipId,
            ConvaiCharacter character,
            ConversationTargetRequestObservation observation = null)
        {
            if (string.IsNullOrWhiteSpace(membershipId) || _roomManager == null)
            {
                observation?.Fail(new InvalidOperationException(
                    "The conversation target cannot be sent without a room membership and room manager."));
                return;
            }
            if (IsPlayerMidUtterance)
            {
                MultiCharacterRoomSession session = _roomManager.CurrentMultiCharacterSession;
                if (session == null)
                {
                    observation?.Fail(new InvalidOperationException(
                        "The multi-character room ended before the target could be deferred."));
                    return;
                }

                _deferredConversationTarget.Hold(session, membershipId, character);
                observation?.MarkDeferred();
                ConvaiLogger.Debug(
                    $"[ConvaiManager] Conversation target change to " +
                    $"'{(character != null ? character.gameObject.name : membershipId)}' is queued " +
                    "until the player's current utterance ends.",
                    LogCategory.SDK);
                return;
            }

            // A held TalkTo can survive until the first targeting tick after speech ends. A newer
            // immediate TalkTo wins before that tick and must disown the stale entry, otherwise the
            // old request is flushed later and registers the target a second time.
            _deferredConversationTarget.Clear();

            Task<InteractionTargetResult> canonicalCompletion = null;

            try
            {
                await _roomManager.SetInteractionTargetTrackedAsync(
                    membershipId,
                    (session, canonicalTask) =>
                    {
                        canonicalCompletion = canonicalTask;
                        observation?.Register(canonicalTask);
                        // ConvaiRoomManager invokes this only after it has synchronously claimed the
                        // routing lease. Event handlers may immediately try to send text or PTT, so
                        // publishing Requested any earlier would reopen the same-frame hole this
                        // state exists to close.
                        SafeEventInvoker.Invoke(
                            ConversationTargetRequested,
                            character,
                            null,
                            "ConvaiManager.ConversationTargetRequested",
                            LogCategory.SDK);
                        PublishConversationTargetPhase(
                            ConversationTargetChangePhase.Requested,
                            character,
                            membershipId);
                        _ = ObserveCanonicalConversationTargetAsync(
                            session,
                            canonicalTask,
                            character,
                            membershipId);
                    });
            }
            catch (Exception exception)
            {
                if (canonicalCompletion == null)
                {
                    observation?.Fail(exception);
                    ReportConversationTargetFailure(character, membershipId, exception);
                }
                else if ((exception is TimeoutException || exception is OperationCanceledException) &&
                         !canonicalCompletion.IsCompleted)
                {
                    // The caller's ten-second wait expired, but the registered command remains
                    // authoritative. Keep text, push-to-talk and hands-free audio closed until its
                    // response arrives or the room retires the command on disconnect.
                    ConvaiLogger.Warning(
                        $"[ConvaiManager] The acknowledgement wait for " +
                        $"'{(character != null ? character.gameObject.name : membershipId)}' ended " +
                        $"before the room resolved the command ({exception.Message}). Player input " +
                        "remains paused until the authoritative response arrives or the room disconnects.",
                        LogCategory.SDK);
                }
            }
        }

        /// <summary>Flushes the last scripted target held by the player's speaking boundary.</summary>
        private void FlushDeferredConversationTarget()
        {
            if (IsPlayerMidUtterance || _roomManager == null) return;

            MultiCharacterRoomSession session = _roomManager.CurrentMultiCharacterSession;
            if (!_deferredConversationTarget.TryTake(
                    session,
                    out string membershipId,
                    out ConvaiCharacter character))
                return;

            _targetSwitchPolicy.Reset();
            SendInteractionTarget(membershipId, character);
        }

        /// <summary>
        ///     Reports the canonical outcome after the room-level routing lease has released.
        ///     Session retirement faults this task too, so disconnect always completes it.
        /// </summary>
        private async Task ObserveCanonicalConversationTargetAsync(
            MultiCharacterRoomSession session,
            Task<InteractionTargetResult> canonicalCompletion,
            ConvaiCharacter requestedCharacter,
            string requestedMembershipId)
        {
            try
            {
                await canonicalCompletion;
                if (!ReferenceEquals(_roomManager?.CurrentMultiCharacterSession, session)) return;
            }
            catch (InteractionTargetCommandException exception)
            {
                if (!ReferenceEquals(_roomManager?.CurrentMultiCharacterSession, session)) return;
                // The session's canonical event has already published any newer route carried by
                // this rejection. This observer owns only the failure half of the outcome.
                ReportConversationTargetFailure(requestedCharacter, requestedMembershipId, exception);
            }
            catch (Exception exception)
            {
                // A no-ack timeout deliberately detaches the ambiguous session before this
                // operation-owned observer resumes. Still publish its terminal Failed phase; all
                // other stale-session outcomes remain suppressed.
                if (!ReferenceEquals(_roomManager?.CurrentMultiCharacterSession, session) &&
                    exception is not TimeoutException)
                    return;
                ReportConversationTargetFailure(requestedCharacter, requestedMembershipId, exception);
            }
        }

        /// <summary>Publishes one authoritative target response to both public event surfaces.</summary>
        internal void HandleCanonicalConversationTargetResult(
            MultiCharacterRoomSession session,
            InteractionTargetResult result)
        {
            // The raw session task can complete and then yield before this high-level continuation
            // runs. A reconnect in that gap must not publish an old room's target against the new
            // room. Keep the null-manager allowance for the narrow pure observer tests.
            if (_roomManager != null &&
                !ReferenceEquals(_roomManager.CurrentMultiCharacterSession, session))
                return;

            CharacterRoomMembership membership =
                session?.FindByMembershipId(result.ActiveMembershipId);
            ConvaiCharacter canonicalCharacter = membership?.Character as ConvaiCharacter;

            SafeEventInvoker.Invoke(
                ConversationTargetChanged,
                canonicalCharacter,
                null,
                "ConvaiManager.ConversationTargetChanged",
                LogCategory.SDK);
            PublishConversationTargetPhase(
                ConversationTargetChangePhase.Confirmed,
                canonicalCharacter,
                result.ActiveMembershipId);
        }

        private void ReportConversationTargetFailure(
            ConvaiCharacter requestedCharacter,
            string requestedMembershipId,
            Exception exception)
        {
            ConvaiLogger.Warning(
                $"[ConvaiManager] Could not move the conversation to " +
                $"'{(requestedCharacter != null ? requestedCharacter.gameObject.name : requestedMembershipId)}': " +
                $"{exception.Message}",
                LogCategory.SDK);
            PublishConversationTargetPhase(
                ConversationTargetChangePhase.Failed,
                requestedCharacter,
                requestedMembershipId,
                exception.Message);
        }

        /// <summary>
        ///     The character the conversation is moving to, while it is still moving.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A target change is a round trip, and it was measured at over a second — long
        ///         enough for a player to finish typing into it. During that window the room still
        ///         reports the previous character as active while the service is already ending that
        ///         character's turn, so anything sent lands between the two and is answered by
        ///         neither.
        ///     </para>
        ///     <para>
        ///         Naming the character being moved to is the honest answer to "who am I talking
        ///         to": the conversation is already on its way there. Availability treats the window
        ///         as not-yet-ready, so the field closes and the send is refused rather than
        ///         swallowed.
        ///     </para>
        /// </remarks>
        internal ConvaiCharacter PendingConversationTarget => _roomManager?.PendingConversationTarget;

        /// <summary>
        ///     Mirrors the C# events onto the event hub, so integrations that read one surface see
        ///     the whole conversation rather than most of it.
        /// </summary>
        /// <remarks>
        ///     The failure phase has no C# event on purpose — adding one would be a fourth thing to
        ///     subscribe to for a request outcome. A rejection can still reconcile a newer
        ///     canonical route; that real move is published through the changed event first.
        /// </remarks>
        private void PublishConversationTargetPhase(
            ConversationTargetChangePhase phase,
            ConvaiCharacter character,
            string membershipId,
            string reason = null)
        {
            IEventHub hub = EventsOrNull?.Raw;
            if (hub == null) return;

            hub.Publish(ConversationTargetChangedEvent.Create(
                phase,
                character != null ? character.CharacterId : null,
                character != null ? character.CharacterName : null,
                membershipId,
                reason));
        }

        /// <summary>
        ///     Attaches each targeting subscription as soon as the thing it needs exists.
        /// </summary>
        /// <remarks>
        ///     The event hub is ready before the player is resolved, so one shared "subscribed" flag
        ///     would attach the hub subscription, mark the work done, and silently never attach the
        ///     player one. Each latches separately.
        /// </remarks>
        private void SubscribeConversationTargetingEvents()
        {
            if (!_roomConversationTargetSubscribed && _roomManager != null)
            {
                _roomManager.CanonicalConversationTargetReconciled +=
                    HandleCanonicalConversationTargetReconciled;
                _roomConversationTargetSubscribed = true;
            }

            if (_playerSpeakingSubscribed) return;

            IEventHub hub = EventsOrNull?.Raw;
            if (hub == null) return;

            _playerSpeakingToken = hub.Subscribe<PlayerSpeakingStateChanged>(evt =>
            {
                _playerIsSpeaking = evt.IsSpeaking;
                if (!evt.IsSpeaking) FlushDeferredConversationTarget();
            });
            _playerSpeakingSubscribed = true;
        }

        private void HandleCanonicalConversationTargetReconciled(
            MultiCharacterRoomSession session,
            CharacterRoomMembership previous,
            CharacterRoomMembership current)
        {
            if (!ReferenceEquals(_roomManager?.CurrentMultiCharacterSession, session)) return;

            ConvaiCharacter canonicalCharacter = current?.Character as ConvaiCharacter;
            HandleCanonicalConversationTargetResult(
                session,
                new InteractionTargetResult(
                    string.Empty,
                    current?.MembershipId,
                    previous?.MembershipId,
                    session.RouteEpoch,
                    !ReferenceEquals(previous, current)));
        }

        private void UnsubscribeConversationTargetingEvents()
        {
            if (_roomConversationTargetSubscribed && _roomManager != null)
            {
                _roomManager.CanonicalConversationTargetReconciled -=
                    HandleCanonicalConversationTargetReconciled;
                _roomConversationTargetSubscribed = false;
            }

            if (!_playerSpeakingSubscribed) return;

            EventsOrNull?.Raw?.Unsubscribe(_playerSpeakingToken);
            _playerSpeakingSubscribed = false;
            _playerIsSpeaking = false;
            _deferredConversationTarget.Clear();
        }
    }
}
