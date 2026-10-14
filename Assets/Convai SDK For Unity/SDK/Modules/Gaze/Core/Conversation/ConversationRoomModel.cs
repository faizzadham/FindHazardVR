using System.Collections.Generic;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>
    ///     What the room knows about the conversation happening in it: who is in it, who is
    ///     talking, who holds the floor, who is expected to answer, and how long it has been
    ///     quiet. One instance per scene, derived once per frame, read by every character.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>It exists because every character used to work this out alone.</b> Each gaze
    ///         controller derived "who is talking" from the same events on the same frame, so
    ///         listeners were correlated by construction — three heads turning together on one
    ///         shared edge — and nobody could reason about the group at all: not about who was
    ///         being addressed, not about who answered last, not about how many people were
    ///         standing there. Every one of those questions has exactly one answer, and this is
    ///         where it lives.
    ///     </para>
    ///     <para>
    ///         <b>It never reads the scene.</b> There is no <c>Transform</c>, no
    ///         <c>UnityEngine.Object</c> and no registry lookup in here; everything is pushed in
    ///         by the controllers through <see cref="ReportParticipant" /> and
    ///         <see cref="ReportPlayer" />. That keeps it a plain object that an EditMode test can
    ///         drive frame by frame, and it keeps the cost of a room of eight characters at one
    ///         short list walk per frame rather than eight.
    ///     </para>
    ///     <para>
    ///         <b>The floor rules are a move, not a rewrite.</b> <see cref="UpdateFloor" /> is
    ///         <c>SpeakerAttentionDirector.UpdateFloor</c> ported across, keyed by participant
    ///         instead of by a two-valued focus enum: an empty floor is claimed after
    ///         <c>ClaimSeconds</c>; a held one has to be taken with sustained speech; the player
    ///         outranks a character as a challenger; a holder who is still talking loses only to
    ///         the player; a holder who has gone quiet keeps it for <c>HoldSeconds</c>. The
    ///         numbers, and the reasons for them, are unchanged.
    ///     </para>
    ///     <para>
    ///         <b>Allocation-free once the room has filled.</b> Participants come from a pool and
    ///         go back to it, reaction slots live in fixed arrays, and nothing on the refresh path
    ///         formats a string or takes a closure.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationRoomModel
    {
        /// <summary>The player's key. Negative so it can never collide with an object-id hash.</summary>
        internal const int PlayerKey = -1;

        /// <summary>Nobody: an empty floor, an unknown addressee, a responder the room cannot name.</summary>
        internal const int NobodyKey = 0;

        /// <summary>
        ///     How many lanes can be open at once. A lane is one person who can be looked at, and
        ///     a room this model will admit holds eight of them.
        /// </summary>
        private const int ReactionLaneCapacity = 8;

        /// <summary>
        ///     How many bookings one lane can hold. Everybody in the room may hold several
        ///     against the same person at once — noticing them start, following the floor moving
        ///     to them, flicking to them as an interrupter — so this is listeners times beats,
        ///     not listeners.
        /// </summary>
        private const int ReactionSlotCapacity = 16;

        /// <summary>Rooms this size are the norm; the pool only grows past it for a genuinely bigger one.</summary>
        private const int InitialRoomCapacity = 8;

        private static ConversationRoomModel _shared;

        private readonly List<ConversationParticipant> _participants = new(InitialRoomCapacity);
        private readonly List<ConversationParticipant> _pool = new(InitialRoomCapacity);

        // ── Reaction slots ───────────────────────────────────────────────────
        private readonly int[] _reactionLaneIds = new int[ReactionLaneCapacity];
        private readonly bool[] _reactionLaneUsed = new bool[ReactionLaneCapacity];
        private readonly int[] _reactionSlotCounts = new int[ReactionLaneCapacity];
        private readonly int[] _reactionSlotCursors = new int[ReactionLaneCapacity];
        private readonly int[] _reactionListeners = new int[ReactionLaneCapacity * ReactionSlotCapacity];
        private readonly int[] _reactionBookings = new int[ReactionLaneCapacity * ReactionSlotCapacity];
        private readonly float[] _reactionTimes = new float[ReactionLaneCapacity * ReactionSlotCapacity];
        private readonly float[] _reactionResolvedTimes = new float[ReactionLaneCapacity * ReactionSlotCapacity];
        private int _reactionLaneCursor;
        private readonly int[] _rankListeners = new int[ReactionSlotCapacity];
        private readonly int[] _rankBookings = new int[ReactionSlotCapacity];
        private readonly float[] _rankWanted = new float[ReactionSlotCapacity];
        private readonly int[] _rankSlots = new int[ReactionSlotCapacity];

        // ── The player's staged report ───────────────────────────────────────
        private bool _playerReported;
        private bool _playerReportPending;
        private Vector3 _playerEyePoint;
        private Vector3 _playerForward;
        private bool _playerServerSpeaking;
        private bool _playerLocalActive;
        private float _playerLevel;
        private int _playerAddresseeKey;
        private bool _playerTypedPending;
        private float _playerTypedAt = float.NegativeInfinity;

        // ── Clock ────────────────────────────────────────────────────────────
        private float _time;
        private int _lastRefreshFrame = int.MinValue;
        private bool _refreshed;
        private float _lastSpeechTime;
        private bool _hasSpeechTime;

        // ── Floor ────────────────────────────────────────────────────────────
        private int _floorKey;
        private float _floorSince;
        private bool _floorHolderSpeaking;
        private int _lastFloorKey;
        private float _lastFloorEndTime;
        private int _turnIndex;
        private float _floorHoldRemaining;
        private int _claimantKey;
        private float _claimHeldSeconds;

        private ConversationOnset _lastOnset = ConversationOnset.None;

        /// <summary>
        ///     The one room every controller reports into. Lazily created, because a project that
        ///     never enables a gaze controller should not pay for it, and never destroyed, because
        ///     the room outlives any single character in it.
        /// </summary>
        internal static ConversationRoomModel Shared => _shared ??= new ConversationRoomModel();

        /// <summary>
        ///     Empties the shared room. A test seam: the instance is kept and cleared in place, so
        ///     anything that has already resolved <see cref="Shared" /> stays pointed at the room
        ///     the next test will fill.
        /// </summary>
        internal static void ResetShared() => _shared?.Reset();

        /// <summary>
        ///     This frame's derivation. Invalid (<see cref="ConversationRoomSnapshot.IsValid" />
        ///     false) until the first <see cref="Refresh" />.
        /// </summary>
        internal ConversationRoomSnapshot Current { get; private set; }

        /// <summary>
        ///     Room time of the last derivation. Deliberately not called <c>Time</c>: a member of
        ///     that name would shadow <c>UnityEngine.Time</c> inside this class, and the first
        ///     person to reach for it here would get a very confusing error.
        /// </summary>
        internal float LastRefreshTime => _time;

        /// <summary>
        ///     A character reporting itself. Called once per cognition tick by that character's
        ///     own gaze controller, which is the only thing that can answer these questions
        ///     without a scene lookup.
        /// </summary>
        /// <param name="key">The character's <c>ConvaiCharacterGazeRegistry.Entry.Key</c>.</param>
        /// <param name="headPoint">Eye-line point other people look at.</param>
        /// <param name="forward">World-space facing.</param>
        /// <param name="isSpeaking">Whether it is producing speech right now.</param>
        /// <param name="level">Speech energy, for salience. Zero when nothing measures it.</param>
        /// <param name="displayName">Fallback name for diagnostics.</param>
        /// <param name="displayNameSource">Optional live name source, read only by diagnostic consumers.</param>
        /// <remarks>
        ///     <see cref="NobodyKey" /> and <see cref="PlayerKey" /> are reserved, so a report
        ///     carrying either is dropped rather than admitted under a meaning it does not have.
        /// </remarks>
        internal void ReportParticipant(
            int key,
            Vector3 headPoint,
            Vector3 forward,
            bool isSpeaking,
            float level,
            string displayName,
            Transform displayNameSource = null)
        {
            if (key == NobodyKey || key == PlayerKey) return;

            ConversationParticipant participant = GetOrAdmit(key, displayName);
            participant.HeadPoint = headPoint;
            participant.Forward = forward;
            participant.Level = level;
            participant.IsSpeaking = isSpeaking;
            // The service only ever has a character answering the player, so that is the only
            // addressee a character can honestly report. When it stops talking it stops
            // addressing anybody, and the floor's own memory carries the turn from there.
            participant.AddresseeKey = isSpeaking ? PlayerKey : NobodyKey;
            if (!string.IsNullOrEmpty(displayName)) participant.DisplayName = displayName;
            if (displayNameSource != null && participant.DisplayNameSource != displayNameSource)
                participant.DisplayName = displayNameSource.name;
            participant.DisplayNameSource = displayNameSource;
            participant.LastReportTime = float.PositiveInfinity;
        }

        /// <summary>
        ///     The player, as one character sees them. Every controller reports the same person,
        ///     so the last write in a frame wins — they are all reading the same event hub and the
        ///     same anchor, so they agree about everything except which anchor resolved first.
        /// </summary>
        /// <param name="eyePoint">Where the player's eyes are (camera, XR head, or anchor).</param>
        /// <param name="forward">Which way the player is facing.</param>
        /// <param name="serverSpeaking">The service's verdict — one round trip late.</param>
        /// <param name="localActive">Local evidence: the microphone gate or the push-to-talk control.</param>
        /// <param name="localLevel">How far above its noise floor the microphone is.</param>
        /// <param name="addresseeKey">The character the player is addressing, or <see cref="NobodyKey" />.</param>
        /// <param name="typedThisTick">A typed message went out this tick.</param>
        /// <remarks>
        ///     The player is speaking when <i>any</i> of the three says so, and the onset is
        ///     therefore the earliest of them. That is the whole point of taking the local
        ///     evidence: a listener reacts to the player's first word, and the service's verdict
        ///     arriving a few hundred milliseconds later only confirms a turn the room already
        ///     started.
        /// </remarks>
        internal void ReportPlayer(
            Vector3 eyePoint,
            Vector3 forward,
            bool serverSpeaking,
            bool localActive,
            float localLevel,
            int addresseeKey,
            bool typedThisTick)
        {
            _playerReported = true;
            _playerReportPending = true;
            _playerEyePoint = eyePoint;
            _playerForward = forward;
            _playerServerSpeaking = serverSpeaking;
            _playerLocalActive = localActive;
            _playerLevel = localLevel;
            _playerAddresseeKey = addresseeKey == PlayerKey ? NobodyKey : addresseeKey;
            if (typedThisTick) _playerTypedPending = true;
        }

        /// <summary>
        ///     Takes a participant out of the room immediately, rather than waiting for its report
        ///     to time out. What a gaze controller calls when it is disabled: the character is
        ///     gone now, and a second of ghost presence is a second of everybody else still
        ///     looking at it.
        /// </summary>
        internal void RemoveParticipant(int key)
        {
            if (key == NobodyKey) return;

            for (int i = 0; i < _participants.Count; i++)
            {
                if (_participants[i].Key != key) continue;

                Recycle(_participants[i]);
                _participants.RemoveAt(i);
                if (key == PlayerKey) _playerReported = false;
                if (_floorKey == key) ReleaseFloor(_time);
                if (_claimantKey == key) ClearClaim();
                return;
            }
        }

        /// <summary>
        ///     Derives the room from this frame's reports. Runs once per frame however many
        ///     controllers call it: the first caller does the work and every later one in the same
        ///     frame reads the same <see cref="Current" />.
        /// </summary>
        /// <param name="now">Room time, in seconds (<c>Time.time</c> in play mode).</param>
        /// <param name="frame">Frame counter — the once-per-frame guard.</param>
        /// <param name="tuning">Floor timings and the room's own bookkeeping windows.</param>
        /// <remarks>
        ///     Characters tick in an arbitrary order, so a character that ticks after the frame's
        ///     first refresh has its report picked up by the next one. Everything in here is
        ///     therefore at most one frame old, which is a frame less than the round trip the
        ///     server edge already costs and is invisible against a reaction latency measured in
        ///     hundreds of milliseconds.
        /// </remarks>
        internal void Refresh(float now, int frame, in ConversationRoomTuning tuning)
        {
            if (_refreshed && frame == _lastRefreshFrame) return;

            float deltaTime = _refreshed ? Mathf.Max(0f, now - _time) : 0f;
            _lastRefreshFrame = frame;
            _refreshed = true;
            _time = now;

            if (!_hasSpeechTime)
            {
                // Silence is measured from when the room started, not from time zero, or the very
                // first frame would report an hour of quiet just because Time.time is large.
                _hasSpeechTime = true;
                _lastSpeechTime = now;
            }

            ApplyPlayerReport(now, in tuning);
            StampReports(now);
            ExpireParticipants(now, in tuning);
            DetectSpeechEdges(now);
            UpdateFloor(now, deltaTime, in tuning);
            Publish(now, frame);
        }

        /// <summary>Empties the room and forgets the conversation. Component disable, scene change, test setup.</summary>
        internal void Reset()
        {
            for (int i = 0; i < _participants.Count; i++) Recycle(_participants[i]);
            _participants.Clear();

            _playerReported = false;
            _playerReportPending = false;
            _playerEyePoint = default;
            _playerForward = Vector3.forward;
            _playerServerSpeaking = false;
            _playerLocalActive = false;
            _playerLevel = 0f;
            _playerAddresseeKey = NobodyKey;
            _playerTypedPending = false;
            _playerTypedAt = float.NegativeInfinity;

            _time = 0f;
            _lastRefreshFrame = int.MinValue;
            _refreshed = false;
            _lastSpeechTime = 0f;
            _hasSpeechTime = false;

            _floorKey = NobodyKey;
            _floorSince = 0f;
            _floorHolderSpeaking = false;
            _lastFloorKey = NobodyKey;
            _lastFloorEndTime = 0f;
            _turnIndex = 0;
            _floorHoldRemaining = 0f;
            ClearClaim();

            _lastOnset = ConversationOnset.None;
            Current = default;

            for (int i = 0; i < ReactionLaneCapacity; i++)
            {
                _reactionLaneUsed[i] = false;
                _reactionLaneIds[i] = 0;
                _reactionSlotCounts[i] = 0;
                _reactionSlotCursors[i] = 0;
            }

            _reactionLaneCursor = 0;
        }

        // ── Reports ──────────────────────────────────────────────────────────

        /// <summary>
        ///     Turns the staged player report into a participant, and the three pieces of evidence
        ///     into one answer to "is the player talking".
        /// </summary>
        private void ApplyPlayerReport(float now, in ConversationRoomTuning tuning)
        {
            if (!_playerReported) return;

            ConversationParticipant player = GetOrAdmit(PlayerKey, "Player");
            player.HeadPoint = _playerEyePoint;
            player.Forward = _playerForward;
            player.Level = _playerLevel;
            player.AddresseeKey = _playerAddresseeKey;

            if (_playerTypedPending)
            {
                _playerTypedPending = false;
                _playerTypedAt = now;
            }

            // Recomputed every frame rather than only when a report lands, because the typed
            // window closes on its own: a message sent 1.4 s ago stops being a turn whether or
            // not anybody has said anything since.
            bool typedWindow = _playerTypedAt > float.NegativeInfinity &&
                               now - _playerTypedAt <= tuning.TypedFloorSeconds;
            player.IsSpeaking = _playerLocalActive || _playerServerSpeaking || typedWindow;

            if (_playerReportPending)
            {
                _playerReportPending = false;
                player.LastReportTime = now;
            }
        }

        /// <summary>Marks everybody who reported since the last refresh as present, as of now.</summary>
        private void StampReports(float now)
        {
            for (int i = 0; i < _participants.Count; i++)
            {
                ConversationParticipant participant = _participants[i];
                if (!float.IsPositiveInfinity(participant.LastReportTime)) continue;
                participant.LastReportTime = now;
            }
        }

        /// <summary>Drops anybody who has stopped reporting: disabled, destroyed, or unloaded with their scene.</summary>
        private void ExpireParticipants(float now, in ConversationRoomTuning tuning)
        {
            for (int i = _participants.Count - 1; i >= 0; i--)
            {
                ConversationParticipant participant = _participants[i];
                if (now - participant.LastReportTime <= tuning.ExpireSeconds) continue;

                int key = participant.Key;
                Recycle(participant);
                _participants.RemoveAt(i);
                if (key == PlayerKey) _playerReported = false;
                if (_floorKey == key) ReleaseFloor(now);
                if (_claimantKey == key) ClearClaim();
            }
        }

        /// <summary>
        ///     Stamps the onset and end of every run of speech. The onset is what a reaction
        ///     latency is measured from, so it is recorded at the first frame the room believed
        ///     somebody was talking and never revised upward by a later confirmation.
        /// </summary>
        private void DetectSpeechEdges(float now)
        {
            for (int i = 0; i < _participants.Count; i++)
            {
                ConversationParticipant participant = _participants[i];
                bool speaking = participant.IsSpeaking;

                if (speaking && !participant.WasSpeaking)
                {
                    participant.SpeechOnsetTime = now;
                    _lastOnset = new ConversationOnset(participant.Key, now, ResolveOnsetSource(participant));
                }
                else if (!speaking && participant.WasSpeaking)
                {
                    participant.SpeechEndTime = now;
                }

                participant.WasSpeaking = speaking;
                if (speaking)
                {
                    _lastSpeechTime = now;
                    _hasSpeechTime = true;
                }
            }
        }

        /// <summary>
        ///     Which evidence carried an onset. The local channel wins whenever it is up, because
        ///     it is the one that got there first — the server's verdict on the same turn is a
        ///     confirmation, and attributing the onset to it would hide exactly the latency this
        ///     model exists to remove.
        /// </summary>
        private ConversationOnsetSource ResolveOnsetSource(ConversationParticipant participant)
        {
            if (participant.Key != PlayerKey) return ConversationOnsetSource.Character;
            bool typedNow = _playerTypedAt > float.NegativeInfinity && _playerTypedAt >= _time;
            return _playerLocalActive || typedNow
                ? ConversationOnsetSource.Local
                : ConversationOnsetSource.Server;
        }

        // ── The floor ────────────────────────────────────────────────────────

        /// <summary>
        ///     Maintains who holds the conversational floor. Ported from
        ///     <c>SpeakerAttentionDirector.UpdateFloor</c> — same three rules, same numbers, keyed
        ///     by participant so that a room of any size gets the answer the two-party version
        ///     could only give for one speaker at a time.
        /// </summary>
        /// <remarks>
        ///     An empty floor is taken almost at once (<c>ClaimSeconds</c>, long enough only to
        ///     ignore a single noisy frame). Taking it from somebody who holds it needs sustained
        ///     speech (<c>InterruptionSeconds</c>), so a "mm-hmm", a cough, or one false positive
        ///     from voice detection does not move the room's eyes. A holder who stops keeps it for
        ///     <c>HoldSeconds</c>, which carries the room across the pauses in and around a turn.
        ///     The player outranks a character as a challenger, and a holder who is still talking
        ///     loses the floor only to the player — without that last rule the two of them trade
        ///     it back and forth for as long as they overlap.
        /// </remarks>
        private void UpdateFloor(float now, float deltaTime, in ConversationRoomTuning tuning)
        {
            ConversationParticipant holder = Find(_floorKey);
            bool holderStillSpeaking = holder != null && holder.IsSpeaking;
            _floorHolderSpeaking = holderStillSpeaking;

            int challengerKey = ResolveChallenger(holderStillSpeaking);

            if (challengerKey == NobodyKey)
            {
                ClearClaim();
            }
            else
            {
                if (challengerKey != _claimantKey)
                {
                    _claimantKey = challengerKey;
                    _claimHeldSeconds = 0f;
                }

                _claimHeldSeconds += deltaTime;

                // An empty floor is there for the taking; a held one has to be taken.
                // Taking a held floor needs sustained speech — except from the person everybody
                // is waiting on. The addressee answering a player who has finished is the
                // expected next turn, not an interruption; making it accumulate the interruption
                // window meant every listener treated the answer as somebody talking over the
                // player, eyes first and head half a second later, with a flick back in between.
                bool expectedAnswer = !holderStillSpeaking && challengerKey == ResolveExpectedResponder();
                float required = _floorKey == NobodyKey || expectedAnswer
                    ? tuning.ClaimSeconds
                    : Mathf.Max(tuning.ClaimSeconds, tuning.InterruptionSeconds);

                if (_claimHeldSeconds >= required)
                {
                    TakeFloor(challengerKey, now, in tuning);
                    return;
                }
            }

            // Nobody took it, and the holder is still talking: the floor simply stays theirs.
            if (holderStillSpeaking)
            {
                _floorHoldRemaining = tuning.HoldSeconds;
                return;
            }

            if (_floorKey == NobodyKey) return;

            // The holder has gone quiet. The floor stays theirs for a while — this is the pause
            // in a turn, not the end of the conversation.
            _floorHoldRemaining -= deltaTime;
            if (_floorHoldRemaining > 0f) return;

            ReleaseFloor(now);
        }

        /// <summary>
        ///     Who, if anybody, is currently trying to take the floor.
        /// </summary>
        /// <remarks>
        ///     Among characters the challenger is whoever started talking first, so two characters
        ///     overlapping produces one stable answer instead of one that flips with list order.
        ///     The player then outranks any of them: a person talking over a character is the
        ///     event in the room, and the service is about to cut that character off anyway.
        /// </remarks>
        private int ResolveChallenger(bool holderStillSpeaking)
        {
            int challengerKey = NobodyKey;
            float earliestOnset = float.PositiveInfinity;

            for (int i = 0; i < _participants.Count; i++)
            {
                ConversationParticipant participant = _participants[i];
                if (!participant.IsSpeaking) continue;
                if (participant.Key == _floorKey || participant.Key == PlayerKey) continue;
                if (participant.SpeechOnsetTime >= earliestOnset) continue;

                earliestOnset = participant.SpeechOnsetTime;
                challengerKey = participant.Key;
            }

            ConversationParticipant player = Find(PlayerKey);
            if (player != null && player.IsSpeaking && _floorKey != PlayerKey) challengerKey = PlayerKey;

            // A holder who is still talking only loses the floor to the player.
            if (holderStillSpeaking && challengerKey != PlayerKey) challengerKey = NobodyKey;

            return challengerKey;
        }

        private void TakeFloor(int key, float now, in ConversationRoomTuning tuning)
        {
            _floorKey = key;
            _floorSince = now;
            _floorHolderSpeaking = true;
            _floorHoldRemaining = tuning.HoldSeconds;
            _turnIndex++;
            ClearClaim();
        }

        /// <summary>
        ///     Lets the floor go, and remembers whose it was: an empty floor still points at the
        ///     turn that just ended, which is what tells the room who is expected to answer.
        /// </summary>
        private void ReleaseFloor(float now)
        {
            if (_floorKey == NobodyKey) return;

            _lastFloorKey = _floorKey;
            _lastFloorEndTime = now;
            _floorKey = NobodyKey;
            _floorSince = now;
            _floorHolderSpeaking = false;
            _floorHoldRemaining = 0f;
            _turnIndex++;
        }

        private void ClearClaim()
        {
            _claimantKey = NobodyKey;
            _claimHeldSeconds = 0f;
        }

        /// <summary>
        ///     Who the room expects to speak next. A player turn points at whoever they were
        ///     addressing; a character's turn points back at the player; and while the floor is
        ///     empty the turn that just ended still points somewhere, which is what keeps a
        ///     listener with the conversation through the gap instead of drifting off.
        /// </summary>
        private int ResolveExpectedResponder()
        {
            if (_floorKey == PlayerKey) return PlayerAddresseeKey();
            if (_floorKey != NobodyKey) return PlayerKey;
            if (_lastFloorKey == PlayerKey) return PlayerAddresseeKey();
            return _lastFloorKey != NobodyKey ? PlayerKey : NobodyKey;
        }

        private int PlayerAddresseeKey()
        {
            ConversationParticipant player = Find(PlayerKey);
            return player != null ? player.AddresseeKey : NobodyKey;
        }

        private void Publish(float now, int frame)
        {
            float silence = Mathf.Max(0f, now - _lastSpeechTime);

            Current = new ConversationRoomSnapshot(
                this,
                _participants,
                now,
                frame,
                _floorKey,
                _floorSince,
                _floorHolderSpeaking,
                _lastFloorKey,
                _lastFloorEndTime,
                _turnIndex,
                ResolveExpectedResponder(),
                silence,
                in _lastOnset);
        }

        // ── Reaction slots ───────────────────────────────────────────────────

        /// <summary>
        ///     Books the moment this listener may act, pushed later until it is at least
        ///     <paramref name="minSeparation" /> away from every other booking in the same lane.
        /// </summary>
        /// <param name="laneId">
        ///     What this booking is spaced <i>against</i>. For everything aimed at a person that
        ///     is the person, so two listeners landing on one face are separated however
        ///     differently they got there.
        /// </param>
        /// <param name="listenerKey">Who is asking.</param>
        /// <param name="bookingId">Which of this listener's beats this is, so one listener may hold several in a lane.</param>
        /// <param name="wantedAt">When they would act if nobody else existed.</param>
        /// <param name="minSeparation">The smallest gap that reads as two people reacting separately.</param>
        /// <returns>The room time this listener should act at.</returns>
        /// <remarks>
        ///     <para>
        ///         This is the one thing a per-character random draw cannot do. Two listeners
        ///         drawing independently from the same latency distribution will sometimes draw
        ///         the same number, and when they do, two heads turn on the same frame and the
        ///         room reads as scripted. Separation has to be enforced by something that can see
        ///         both draws, and the room is the only thing that can.
        ///     </para>
        ///     <para>
        ///         <b>The lane is the target, not the cause.</b> A viewer does not see what moved
        ///         a head; they see two faces arriving at a third one together. Spacing each cause
        ///         in a lane of its own left every pair of causes free to collide — one listener
        ///         noticing somebody start, another following the floor as it moved to the same
        ///         person, a third flicking to them as an interrupter, all inside a frame or two —
        ///         which is the same defect the separation exists to prevent, wearing three hats.
        ///         So what a booking is spaced against is who it lands on.
        ///     </para>
        ///     <para>
        ///         Asking again for the same booking returns the same answer, so a listener may
        ///         call this every frame while it waits without walking its own moment away from
        ///         itself — which is also why one listener needs more than one slot in a lane.
        ///         Storage is bounded on both rings, so a long session cannot grow it.
        ///     </para>
        ///     <para>
        ///         A slot that has to move goes to whichever side of the occupied one it was
        ///         already nearer to, so a listener that drew a faster reaction keeps it even if
        ///         it asked last. Two listeners asking for the same instant still separate in the
        ///         order they asked — there is nothing else to go on.
        ///     </para>
        /// </remarks>
        internal float ReserveReaction(
            int laneId, int listenerKey, int bookingId, float wantedAt, float minSeparation)
        {
            int lane = FindOrOpenLane(laneId);
            int baseIndex = lane * ReactionSlotCapacity;
            int count = _reactionSlotCounts[lane];

            // The first booking stands: a listener asking again gets the slot its original
            // wanted time now resolves to, which may have moved as others booked.
            bool known = false;
            for (int i = 0; i < count; i++)
            {
                if (_reactionListeners[baseIndex + i] != listenerKey ||
                    _reactionBookings[baseIndex + i] != bookingId)
                    continue;
                known = true;
                break;
            }

            if (!known)
            {
                int slot = _reactionSlotCursors[lane];
                _reactionListeners[baseIndex + slot] = listenerKey;
                _reactionBookings[baseIndex + slot] = bookingId;
                _reactionTimes[baseIndex + slot] = wantedAt;
                _reactionResolvedTimes[baseIndex + slot] = wantedAt;
                _reactionSlotCursors[lane] = (slot + 1) % ReactionSlotCapacity;
                if (count < ReactionSlotCapacity) _reactionSlotCounts[lane] = count + 1;
                count = _reactionSlotCounts[lane];
            }

            // Resolve by RANK of wanted time, not by order of booking. Listeners book an onset in
            // tick order - the same order every time - so a scheme that pushes each newcomer past
            // the slots already taken turns the room into a queue: the first character in the
            // scene always looks first. Sorting the wanted times and spacing them out in that
            // order keeps whatever the draws and the geometry decided, and only spreads it.
            float separation = Mathf.Max(0f, minSeparation);
            for (int i = 0; i < count; i++)
            {
                _rankListeners[i] = _reactionListeners[baseIndex + i];
                _rankBookings[i] = _reactionBookings[baseIndex + i];
                _rankWanted[i] = _reactionTimes[baseIndex + i];
                _rankSlots[i] = i;
            }

            for (int i = 1; i < count; i++)
            {
                int slot = _rankSlots[i];
                int listener = _rankListeners[i];
                int booking = _rankBookings[i];
                float wanted = _rankWanted[i];
                int j = i - 1;
                while (j >= 0 && (_rankWanted[j] > wanted ||
                                  (_rankWanted[j] == wanted && _rankListeners[j] > listener) ||
                                  (_rankWanted[j] == wanted && _rankListeners[j] == listener &&
                                   _rankBookings[j] > booking)))
                {
                    _rankSlots[j + 1] = _rankSlots[j];
                    _rankListeners[j + 1] = _rankListeners[j];
                    _rankBookings[j + 1] = _rankBookings[j];
                    _rankWanted[j + 1] = _rankWanted[j];
                    j--;
                }

                _rankSlots[j + 1] = slot;
                _rankListeners[j + 1] = listener;
                _rankBookings[j + 1] = booking;
                _rankWanted[j + 1] = wanted;
            }

            float resolved = wantedAt;
            float previous = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                int slot = baseIndex + _rankSlots[i];
                // A landed look remains history even if a later cancellation changes the ranks.
                float at = _reactionResolvedTimes[slot] < _time
                    ? _reactionResolvedTimes[slot]
                    : Mathf.Max(_rankWanted[i], previous + separation);
                _reactionResolvedTimes[slot] = at;
                if (_rankListeners[i] == listenerKey && _rankBookings[i] == bookingId) resolved = at;
                previous = at;
            }

            return resolved;
        }

        /// <summary>
        ///     Hands a booking back. What a listener calls when the beat it booked is no longer
        ///     going to happen — the person it was for left, the conversation stopped expecting
        ///     them, the glance was overtaken, the character was disabled.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The resolve loop is listener-blind: it spaces whatever it finds in the lane,
        ///         and it cannot tell a beat somebody is still waiting for from one that was
        ///         called off. So a withdrawn booking still sitting in the future goes on pushing
        ///         its own listener's next booking past it — the responder look that is cancelled
        ///         at the end of one turn and made again at the start of the next lands a whole
        ///         separation late, which is the head turning after the answer has begun.
        ///     </para>
        ///     <para>
        ///         Nothing else removes one. A slot leaves a lane when the ring wraps over it,
        ///         which is fifteen bookings later and long past the moment it mattered.
        ///     </para>
        /// </remarks>
        /// <param name="laneId">The lane the booking was made in, as it was made — never re-derived.</param>
        /// <param name="listenerKey">Who booked it, salted exactly as it was booked.</param>
        /// <param name="bookingId">Which of that listener's beats it was.</param>
        internal void ReleaseReaction(int laneId, int listenerKey, int bookingId)
        {
            // Deliberately not FindOrOpenLane: giving a booking back is no reason to spend one of
            // the eight lanes on a person nobody has ever booked anything for.
            int lane = -1;
            for (int i = 0; i < ReactionLaneCapacity; i++)
            {
                if (!_reactionLaneUsed[i] || _reactionLaneIds[i] != laneId) continue;

                lane = i;
                break;
            }

            if (lane < 0) return;

            int baseIndex = lane * ReactionSlotCapacity;
            int count = _reactionSlotCounts[lane];
            for (int i = 0; i < count; i++)
            {
                if (_reactionListeners[baseIndex + i] != listenerKey ||
                    _reactionBookings[baseIndex + i] != bookingId)
                    continue;

                // A moment that has already happened is history, not a booking: the head turned,
                // and the looks after it are spaced against that turn whether or not the director
                // still holds the slot. Only a moment still ahead can be given back.
                if (_reactionResolvedTimes[baseIndex + i] < _time) return;

                // Compacted, not blanked. Everything below the count is the lane, so a hole left
                // in the middle would be read as a booking at whatever the arrays happened to
                // hold, and the count is what every other loop in here trusts.
                for (int j = i; j < count - 1; j++)
                {
                    _reactionListeners[baseIndex + j] = _reactionListeners[baseIndex + j + 1];
                    _reactionBookings[baseIndex + j] = _reactionBookings[baseIndex + j + 1];
                    _reactionTimes[baseIndex + j] = _reactionTimes[baseIndex + j + 1];
                    _reactionResolvedTimes[baseIndex + j] = _reactionResolvedTimes[baseIndex + j + 1];
                }

                count--;
                _reactionSlotCounts[lane] = count;
                _reactionListeners[baseIndex + count] = 0;
                _reactionBookings[baseIndex + count] = 0;
                _reactionTimes[baseIndex + count] = 0f;
                _reactionResolvedTimes[baseIndex + count] = 0f;

                // The free slots are the ones above the count now. Pointing the cursor at the
                // first of them is what keeps the ring's own rule true — a reservation lands on
                // free ground until the lane is genuinely full — instead of overwriting a
                // booking somebody is still waiting for.
                _reactionSlotCursors[lane] = count;
                return;
            }
        }

        private int FindOrOpenLane(int laneId)
        {
            for (int i = 0; i < ReactionLaneCapacity; i++)
                if (_reactionLaneUsed[i] && _reactionLaneIds[i] == laneId)
                    return i;

            int lane = -1;
            for (int offset = 0; offset < ReactionLaneCapacity; offset++)
            {
                int candidate = (_reactionLaneCursor + offset) % ReactionLaneCapacity;
                if (_reactionLaneUsed[candidate] && _reactionSlotCounts[candidate] > 0) continue;
                lane = candidate;
                break;
            }

            // Reclaim the oldest spent target before displacing any future reservation.
            if (lane < 0)
            {
                float oldest = float.PositiveInfinity;
                for (int candidate = 0; candidate < ReactionLaneCapacity; candidate++)
                {
                    float latest = float.NegativeInfinity;
                    for (int i = 0; i < _reactionSlotCounts[candidate]; i++)
                        latest = Mathf.Max(latest,
                            _reactionResolvedTimes[candidate * ReactionSlotCapacity + i]);
                    if (latest >= _time || latest >= oldest) continue;
                    lane = candidate;
                    oldest = latest;
                }
            }

            if (lane < 0) lane = _reactionLaneCursor;
            _reactionLaneCursor = (lane + 1) % ReactionLaneCapacity;
            _reactionLaneIds[lane] = laneId;
            _reactionLaneUsed[lane] = true;
            _reactionSlotCounts[lane] = 0;
            _reactionSlotCursors[lane] = 0;
            return lane;
        }

        /// <summary>The lane every booking aimed at somebody belongs to: that person.</summary>
        /// <remarks>
        ///     <para>
        ///         The person and nothing else. Keying it by the turn as well was tried and is
        ///         exactly wrong: the floor changing hands is the moment a whole room turns to one
        ///         face, and a lane that changed with the turn split that one wave across two of
        ///         them — the listener whose eyes had already flicked to the interrupter was
        ///         spaced against one set of bookings and the listener following the promotion
        ///         against another, so the two arrived together, which is the defect.
        ///     </para>
        ///     <para>
        ///         Nothing accumulates: a lane holds a bounded ring of slots and spent bookings
        ///         sit in the past, where the separation rule has nothing to push.
        ///     </para>
        /// </remarks>
        /// <param name="targetKey">Who the booking lands on.</param>
        internal static int LaneFor(int targetKey) => unchecked(targetKey * 486187739);

        // ── Participants ─────────────────────────────────────────────────────

        private ConversationParticipant Find(int key)
        {
            if (key == NobodyKey) return null;

            for (int i = 0; i < _participants.Count; i++)
                if (_participants[i].Key == key)
                    return _participants[i];

            return null;
        }

        /// <summary>
        ///     Finds a participant, or lets a new one into the room. A new arrival joins at the
        ///     last derived room time rather than at the moment of the report, so that every
        ///     participant admitted between two refreshes shares one arrival time and a stagger
        ///     computed from it cannot depend on tick order.
        /// </summary>
        private ConversationParticipant GetOrAdmit(int key, string displayName)
        {
            ConversationParticipant existing = Find(key);
            if (existing != null) return existing;

            ConversationParticipant participant;
            int last = _pool.Count - 1;
            if (last >= 0)
            {
                participant = _pool[last];
                _pool.RemoveAt(last);
            }
            else
            {
                participant = new ConversationParticipant();
            }

            participant.Key = key;
            participant.DisplayName = string.IsNullOrEmpty(displayName) ? "?" : displayName;
            participant.HeadPoint = default;
            participant.Forward = Vector3.forward;
            participant.IsSpeaking = false;
            participant.WasSpeaking = false;
            participant.SpeechOnsetTime = 0f;
            participant.SpeechEndTime = 0f;
            participant.Level = 0f;
            participant.AddresseeKey = NobodyKey;
            participant.JoinedAt = _time;
            participant.LastReportTime = _time;

            _participants.Add(participant);
            return participant;
        }

        private void Recycle(ConversationParticipant participant)
        {
            if (participant == null) return;

            participant.Key = NobodyKey;
            participant.DisplayName = null;
            participant.IsSpeaking = false;
            participant.WasSpeaking = false;
            _pool.Add(participant);
        }
    }
}
