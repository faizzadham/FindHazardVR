using System.Collections.Generic;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>Which evidence first said that somebody had started talking.</summary>
    internal enum ConversationOnsetSource
    {
        /// <summary>No onset has been seen yet.</summary>
        None = 0,

        /// <summary>
        ///     Local evidence about the player: the microphone gate, the push-to-talk control, or
        ///     a typed message. It costs no round trip, so it is always the earliest thing the
        ///     room can know about the person sitting at the keyboard.
        /// </summary>
        Local = 1,

        /// <summary>
        ///     The service's own verdict on the player. One round trip late, so it confirms an
        ///     onset the room already has rather than announcing one.
        /// </summary>
        Server = 2,

        /// <summary>A character started speaking.</summary>
        Character = 3
    }

    /// <summary>The most recent speech onset the room saw, and where it came from.</summary>
    internal readonly struct ConversationOnset
    {
        /// <summary>Who started talking (<see cref="ConversationRoomModel.PlayerKey" /> for the player).</summary>
        public readonly int Key;

        /// <summary>Room time of the onset.</summary>
        public readonly float Time;

        /// <summary>The evidence that carried it.</summary>
        public readonly ConversationOnsetSource Source;

        /// <summary>Creates an onset record.</summary>
        public ConversationOnset(int key, float time, ConversationOnsetSource source)
        {
            Key = key;
            Time = time;
            Source = source;
        }

        /// <summary>Nobody has started talking yet.</summary>
        public static ConversationOnset None => new(0, 0f, ConversationOnsetSource.None);
    }

    /// <summary>
    ///     One person in the room — a Convai character, or the player under
    ///     <see cref="ConversationRoomModel.PlayerKey" />.
    /// </summary>
    /// <remarks>
    ///     A pooled class rather than a struct: participants are handed out by reference from a
    ///     preallocated pool and reused as characters come and go, so reading one costs no copy
    ///     and a room that has stabilised allocates nothing at all. The model is the only writer;
    ///     everything here is read-only to a consumer.
    /// </remarks>
    internal sealed class ConversationParticipant
    {
        /// <summary>
        ///     Stable identity — a character's <c>ConvaiCharacterGazeRegistry.Entry.Key</c>, or
        ///     <see cref="ConversationRoomModel.PlayerKey" />.
        /// </summary>
        public int Key { get; internal set; }

        /// <summary>Whether this participant is the player rather than a character.</summary>
        public bool IsPlayer => Key == ConversationRoomModel.PlayerKey;

        /// <summary>Name for diagnostics. Never formatted or concatenated on a tick path.</summary>
        public string DisplayName
        {
            get => DisplayNameSource != null ? DisplayNameSource.name : _displayName;
            internal set
            {
                _displayName = value;
                DisplayNameSource = null;
            }
        }

        private string _displayName;
        internal Transform DisplayNameSource { get; set; }

        /// <summary>Eye-line point: a character's head bone, or the player's camera/anchor.</summary>
        public Vector3 HeadPoint { get; internal set; }

        /// <summary>Which way this participant is facing, in world space.</summary>
        public Vector3 Forward { get; internal set; }

        /// <summary>
        ///     Whether they are talking right now. For the player this is the union of the local
        ///     evidence, the service's verdict, and a recent typed message — see
        ///     <see cref="ConversationRoomModel.ReportPlayer" />.
        /// </summary>
        public bool IsSpeaking { get; internal set; }

        /// <summary>Room time their current (or most recent) run of speech began.</summary>
        public float SpeechOnsetTime { get; internal set; }

        /// <summary>Room time they last stopped speaking. Zero until they have stopped once.</summary>
        public float SpeechEndTime { get; internal set; }

        /// <summary>How loud they are, on whatever scale the reporter uses. Salience, not gain.</summary>
        public float Level { get; internal set; }

        /// <summary>
        ///     Who they are talking to. The player's is whatever the room was told; a speaking
        ///     character's is the player, because that is the only addressee the service has.
        /// </summary>
        public int AddresseeKey { get; internal set; }

        /// <summary>Room time they entered the room. A new arrival is this much less than now old.</summary>
        public float JoinedAt { get; internal set; }

        /// <summary>Room time of their last report, which is what the expiry measures against.</summary>
        internal float LastReportTime { get; set; }

        /// <summary>
        ///     <see cref="IsSpeaking" /> as of the previous derivation. The model keeps it here
        ///     rather than in a parallel array so that a participant coming back out of the pool
        ///     cannot inherit somebody else's speech edge.
        /// </summary>
        internal bool WasSpeaking { get; set; }
    }

    /// <summary>
    ///     Everything the room derived this frame: who holds the floor, who is expected to
    ///     answer, how long it has been quiet, and the participants themselves.
    /// </summary>
    /// <remarks>
    ///     A view, not a copy. It carries the model's live participant list by reference, so
    ///     taking a snapshot allocates nothing — and a snapshot kept past the frame it was taken
    ///     in would read the next frame's participants. Nothing keeps one: read it, decide,
    ///     discard.
    /// </remarks>
    internal readonly struct ConversationRoomSnapshot
    {
        private readonly IReadOnlyList<ConversationParticipant> _participants;

        /// <summary>
        ///     The model this snapshot came out of. Carried so that a reader deciding what to do
        ///     about the conversation can book a reaction slot in the same breath — separating two
        ///     listeners' reactions is arithmetic only the room can do, and handing every consumer
        ///     a second reference to the room it is already reading would be one more thing to
        ///     keep in step.
        /// </summary>
        private readonly ConversationRoomModel _room;

        /// <summary>Room time the derivation ran.</summary>
        public readonly float Time;

        /// <summary>Frame the derivation ran on. Every caller in one frame reads the same value.</summary>
        public readonly int Frame;

        /// <summary>Who holds the conversational floor; 0 when nobody does.</summary>
        public readonly int FloorKey;

        /// <summary>Room time the floor last changed hands (including to nobody).</summary>
        public readonly float FloorSince;

        /// <summary>
        ///     Whether the floor holder is actually producing speech, as opposed to holding the
        ///     floor through a pause in their own turn.
        /// </summary>
        public readonly bool FloorHolderSpeaking;

        /// <summary>Who held the floor before it was last released; 0 when it has never been released.</summary>
        public readonly int LastFloorKey;

        /// <summary>Room time the floor was last released.</summary>
        public readonly float LastFloorEndTime;

        /// <summary>Increments on every floor change, so "is this still the same turn" is one comparison.</summary>
        public readonly int TurnIndex;

        /// <summary>
        ///     Who the room expects to speak next: the addressee of a player turn, the player
        ///     after a character's turn, and while the floor is empty whoever the last turn
        ///     pointed at.
        /// </summary>
        public readonly int ExpectedResponderKey;

        /// <summary>How long since anybody last spoke. Zero while somebody is.</summary>
        public readonly float SilenceSeconds;

        /// <summary>How many people are in the room, the player included.</summary>
        public readonly int ParticipantCount;

        /// <summary>The most recent speech onset, and the evidence that carried it.</summary>
        public readonly ConversationOnset LastOnset;

        /// <summary>Creates a snapshot over a live participant list.</summary>
        internal ConversationRoomSnapshot(
            ConversationRoomModel room,
            IReadOnlyList<ConversationParticipant> participants,
            float time,
            int frame,
            int floorKey,
            float floorSince,
            bool floorHolderSpeaking,
            int lastFloorKey,
            float lastFloorEndTime,
            int turnIndex,
            int expectedResponderKey,
            float silenceSeconds,
            in ConversationOnset lastOnset)
        {
            _room = room;
            _participants = participants;
            Time = time;
            Frame = frame;
            FloorKey = floorKey;
            FloorSince = floorSince;
            FloorHolderSpeaking = floorHolderSpeaking;
            LastFloorKey = lastFloorKey;
            LastFloorEndTime = lastFloorEndTime;
            TurnIndex = turnIndex;
            ExpectedResponderKey = expectedResponderKey;
            SilenceSeconds = silenceSeconds;
            ParticipantCount = participants == null ? 0 : participants.Count;
            LastOnset = lastOnset;
        }

        /// <summary>Whether this snapshot came from a room that has been refreshed at least once.</summary>
        public bool IsValid => _participants != null;

        /// <summary>The participant at <paramref name="index" /> in arrival order, or <c>null</c>.</summary>
        public ConversationParticipant GetParticipant(int index) =>
            _participants != null && index >= 0 && index < _participants.Count ? _participants[index] : null;

        /// <summary>Looks a participant up by key. False for 0, for an unknown key, or on an invalid snapshot.</summary>
        public bool TryGetParticipant(int key, out ConversationParticipant participant)
        {
            participant = null;
            if (_participants == null || key == 0) return false;

            for (int i = 0; i < _participants.Count; i++)
            {
                ConversationParticipant candidate = _participants[i];
                if (candidate == null || candidate.Key != key) continue;

                participant = candidate;
                return true;
            }

            return false;
        }

        /// <summary>The player, or <c>null</c> when nothing has reported one.</summary>
        public ConversationParticipant Player =>
            TryGetParticipant(ConversationRoomModel.PlayerKey, out ConversationParticipant player) ? player : null;

        /// <summary>The floor holder, or <c>null</c> when the floor is empty.</summary>
        public ConversationParticipant FloorHolder =>
            TryGetParticipant(FloorKey, out ConversationParticipant holder) ? holder : null;

        /// <summary>
        ///     Books the moment this listener may act —
        ///     <see cref="ConversationRoomModel.ReserveReaction" /> on the room that derived this
        ///     snapshot. On a snapshot with no room behind it the wanted time is handed straight
        ///     back, so a caller never has to decide whether it has one.
        /// </summary>
        /// <param name="laneId">What the booking is spaced against — for anything aimed at somebody, them.</param>
        /// <param name="listenerKey">Who is asking.</param>
        /// <param name="bookingId">Which of this listener's beats this is.</param>
        /// <param name="wantedAt">When they would act if nobody else existed.</param>
        /// <param name="minSeparation">The smallest gap that reads as two people reacting separately.</param>
        /// <returns>The room time this listener should act at.</returns>
        public float ReserveReaction(
            int laneId, int listenerKey, int bookingId, float wantedAt, float minSeparation) =>
            _room != null
                ? _room.ReserveReaction(laneId, listenerKey, bookingId, wantedAt, minSeparation)
                : wantedAt;

        /// <summary>
        ///     Hands a booking back — <see cref="ConversationRoomModel.ReleaseReaction" /> on the
        ///     room that derived this snapshot, and nothing at all on a snapshot with no room
        ///     behind it, so a caller never has to decide whether it has one.
        /// </summary>
        /// <param name="laneId">The lane the booking was made in, as it was made.</param>
        /// <param name="listenerKey">Who booked it, salted exactly as it was booked.</param>
        /// <param name="bookingId">Which of that listener's beats it was.</param>
        public void ReleaseReaction(int laneId, int listenerKey, int bookingId) =>
            _room?.ReleaseReaction(laneId, listenerKey, bookingId);

        /// <summary>The lane every booking aimed at <paramref name="targetKey" /> is spaced in.</summary>
        /// <param name="targetKey">Who the booking lands on.</param>
        public int LaneFor(int targetKey) => ConversationRoomModel.LaneFor(targetKey);
    }
}
