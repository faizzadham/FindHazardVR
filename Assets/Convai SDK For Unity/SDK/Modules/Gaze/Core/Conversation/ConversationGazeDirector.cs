using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Runtime.Embodiment;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>
    ///     Decides who a character in a conversation is looking at, and how committed that look
    ///     is: the "everybody turns to whoever is talking" behaviour of a group, plus everything
    ///     that happens in the gaps between turns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>It produces a policy overlay, not a target.</b> The result rewrites this tick's
    ///         resolved <c>GazeStatePolicy</c> (engagement, head participation, body
    ///         turn, aversion, and whether the player anchor is a candidate at all); the ordinary
    ///         target arbiter then picks the person on its own. Every commitment ramp, smoothing
    ///         pass, aversion beat and face scan is inherited unchanged.
    ///     </para>
    ///     <para>
    ///         <b>Attention, not timeouts.</b> The director it replaced ran a floor, a 2.5 s hold,
    ///         an 8 s linger and a 1.4 s glance — four grids a viewer learns in a minute. Here
    ///         every person in the room carries one number: how much of this character's attention
    ///         they have. Speech, being addressed, being expected to answer and arriving all
    ///         <i>refresh</i> that number; nothing else does, and between refreshes it decays
    ///         exponentially. The look goes to the largest of them. Every behaviour the old
    ///         director special-cased falls out of that: the hand-off at the end of a turn is the
    ///         responder's refresh, the linger is the decay, standing down is the decay reaching
    ///         the floor. There is no timer to learn because there is no timer.
    ///     </para>
    ///     <para>
    ///         <b>It reads the room, not the microphone.</b> Who holds the floor, who is expected
    ///         to answer, when somebody started talking and how long it has been quiet are all
    ///         <see cref="ConversationRoomModel" />'s answers, derived once for everybody. That is
    ///         what makes a group behave like a group: three listeners share one account of the
    ///         conversation and differ only in where they stand and what they draw.
    ///     </para>
    ///     <para>
    ///         <b>Two listeners cannot react together.</b> A reaction is drawn per character from
    ///         a log-normal scaled by salience — a nearer, more central speaker is reacted to
    ///         sooner — and then booked through the room, which pushes it until it is at least
    ///         <see cref="ConversationGazeTuning.MinSeparationSeconds" /> from every other
    ///         listener's slot for the same onset. Independent draws are not enough: two of them
    ///         will land on the same frame often enough to be noticed, and one shared frame is all
    ///         it takes for a room to read as scripted.
    ///     </para>
    ///     <para>
    ///         <b>It owns the gaps as well as the turns.</b> Three beats that used to be
    ///         directors, special cases or nothing at all are decisions this one makes, because
    ///         each of them needs to know something only the room can answer. While nobody is
    ///         talking it glances between the people standing there, never twice at the same one
    ///         and never on the frame the character beside it does. While it holds the floor it
    ///         looks round the rest of its audience, oftener the bigger that audience is, and
    ///         lends nothing but its eyes to doing it. And when somebody starts talking over
    ///         whoever holds the floor its eyes go there long before the room decides whether that
    ///         was an interruption — the head waits for the verdict, which is the difference
    ///         between noticing and turning.
    ///     </para>
    ///     <para>
    ///         <b>Deliberately not a dialogue state.</b> Reporting listeners as <c>Attending</c>
    ///         was tried and rejected: that row commits at 0.9 with body turns allowed, so every
    ///         character in a room would behave exactly like the one being spoken to. Listeners
    ///         stay in <c>Idle</c> and this overlay rides on top of it.
    ///     </para>
    ///     <para>
    ///         Pure POCO — no <c>UnityEngine.Object</c>, no scene access — deterministic given its
    ///         random source, and allocation-free after construction.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationGazeDirector
    {
        /// <summary>How many people this character can hold an opinion about. A room, not a crowd.</summary>
        private const int Capacity = 8;

        /// <summary>
        ///     How much more attention somebody else needs before the look moves to them. Without
        ///     it two people hovering around the same value trade the look every few frames.
        /// </summary>
        private const float SwitchHysteresis = 0.15f;

        /// <summary>
        ///     Below this, nobody in the room is worth looking at any more and idle life resumes.
        ///     With the default decay that is about eleven seconds after the last refresh — a
        ///     consequence of the decay rather than a timeout somebody chose.
        /// </summary>
        private const float StandDownAttention = 0.15f;

        /// <summary>
        ///     How big a raise counts as something happening rather than something continuing.
        ///     Hysteresis exists to stop two comparable values trading the look as they drift; an
        ///     event is not a drift, so a jump this large may move the look on its own merit.
        ///     Without it the arithmetic is unforgiving: everybody decays at the same rate, so a
        ///     gap that is not opened at the moment of the event never opens at all, and the
        ///     listener that should look to whoever is about to answer stays on the person who
        ///     just finished — which is the defect this director was written to fix.
        /// </summary>
        private const float EventJumpThreshold = 0.05f;

        /// <summary>
        ///     How much further away than the attention distance somebody already being looked at
        ///     may drift before the look is given up.
        /// </summary>
        /// <remarks>
        ///     Every gate here is a threshold on a measurement that moves: two people standing
        ///     still are still breathing, walking, and being reported through a head bone that
        ///     sways with an idle clip, and the arm of a chair one of them is sitting on crosses
        ///     the line of sight at whatever rate they rock. A bare threshold turns that into a
        ///     decision — dropped, re-acquired, dropped — and the character makes a movement
        ///     nobody chose. Coming <i>in</i> is unchanged, because the moment somebody becomes
        ///     lookable is a real moment; going <i>out</i> has to be unambiguous.
        /// </remarks>
        private const float RetainDistanceScale = 1.15f;

        /// <summary>How much wider than the attention angle a look already made is allowed to be.</summary>
        private const float RetainYawSlackDegrees = 10f;

        /// <summary>
        ///     How long a wall has to stand between the character and whoever it is watching
        ///     before the look is given up. The rays are cast ten times a second, so a single
        ///     measurement is exactly the resolution at which a passer-by, a swinging arm or a
        ///     door frame reads as a wall.
        /// </summary>
        private const float RetainOcclusionSeconds = 0.3f;

        /// <summary>Attention whoever holds the floor carries while they are talking.</summary>
        private const float SpeakerAttention = 1f;

        /// <summary>
        ///     Attention somebody talking who does <i>not</i> hold the floor carries. Enough to be
        ///     watched when nobody else is; not enough to take the look off the person whose turn
        ///     it is, which is the whole of what makes an interjection an interjection. If they
        ///     keep going the room hands them the floor, and that promotion to a full one is the
        ///     moment the heads move — so the interruption rule needs no rule of its own here.
        /// </summary>
        private const float ChallengerAttention = 0.8f;

        /// <summary>Attention a speaker's addressee carries for as long as that turn lasts.</summary>
        private const float AddresseeAttention = 0.6f;

        /// <summary>Attention whoever is expected to answer carries once the floor empties.</summary>
        private const float ResponderAttention = 0.75f;

        /// <summary>
        ///     How long somebody has to have been the most interesting person in the room before
        ///     the look moves to them, when nothing happened to put them there.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Hysteresis decides <i>whether</i> a lead is big enough to move the look. It has
        ///         nothing to say about how long that lead has existed, and the two are different
        ///         questions. Values here cross with nothing happening at all: the person being
        ///         spoken to is pinned at their share while the speaker who paused for breath
        ///         decays past them, so a lead opens purely because a second went by. Follow it
        ///         the instant it opens and the speaker's next word — a real event, so it takes
        ///         the look straight back — has already cost a turn out and a turn back, half a
        ///         second apart, at the sixty degrees people actually stand at. Two arithmetically
        ///         correct decisions and one movement nobody chose.
        ///     </para>
        ///     <para>
        ///         So a lead nobody caused has to stand for about as long as a look lasts before
        ///         the look follows it. Something that actually happens — an
        ///         <see cref="Slot.Event" />, which is what a speech onset, the floor changing
        ///         hands or somebody walking in produce — goes through on the frame it happens.
        ///         That is the difference between an event and a drift, and it is the whole of
        ///         what this rule measures.
        ///     </para>
        /// </remarks>
        private const float DriftHoldSeconds = 1.2f;

        /// <summary>Attention somebody who has just walked into the room carries.</summary>
        private const float ArrivalAttention = 0.5f;

        /// <summary>How long an arrival stays interesting.</summary>
        private const float ArrivalPulseSeconds = 1.5f;

        /// <summary>
        ///     The look to whoever answers next comes a little later than a look at somebody who
        ///     has just started talking: nothing has happened yet, the listener is anticipating.
        /// </summary>
        private const float ResponderLatencyScale = 1.5f;

        /// <summary>
        ///     The head follows the floor changing hands a little sooner than it reacts to a fresh
        ///     voice: the listener was already watching the interrupter with its eyes.
        /// </summary>
        private const float PromotionLatencyScale = 0.6f;

        /// <summary>
        ///     An onset reaction that landed this recently counts as the promotion too — the
        ///     listener is turning anyway, and a second booking would stall the turn it is making.
        /// </summary>
        private const float PromotionGraceSeconds = 0.25f;

        /// <summary>
        ///     The eyes' flick to an interrupter is a reflex, but not a synchronised one: each
        ///     listener's comes a fraction of its ordinary reaction later than the sustain rule
        ///     allows, booked through the room so two pairs of eyes do not flick on one frame.
        /// </summary>
        private const float ReflexLatencyScale = 0.5f;

        /// <summary>Attention the person a character was just talking to keeps once its turn ends.</summary>
        private const float PostTurnAttention = 0.9f;

        /// <summary>
        ///     A speaker looks round its audience in the middle of a turn, never in its first
        ///     breath and never while wrapping up: those are the moments it holds its addressee.
        /// </summary>
        private const float SpeakerCheckEarliestSeconds = 3f;

        /// <summary>How long after a turn ends before idle glances between people may resume.</summary>
        private const float SocialIdleAfterTurnSeconds = 3f;

        /// <summary>How quiet the room must have been before idle glances between people begin.</summary>
        private const float SocialIdleSilenceSeconds = 4f;

        /// <summary>Attention an audience check puts on the person being spoken to.</summary>
        private const float AudienceCheckAttention = 0.9f;

        /// <summary>
        ///     What an audience check leaves behind when it ends. A check that kept its own value
        ///     would go on arguing with the speaker it just looked away from, and the look would
        ///     hang there for a second instead of coming back — the check asked its question and
        ///     got its answer.
        /// </summary>
        private const float AudienceCheckSpentAttention = 0.5f;

        /// <summary>
        ///     Head participation in any look that is short by design — an audience check, a
        ///     speaker's look round the room, an idle glance, noticing somebody arrive.
        /// </summary>
        /// <remarks>
        ///     One number for all of them because they are one thing: a beat the character has
        ///     already decided not to turn its body for. A short look that recruits the head like
        ///     a committed one is not a glance at all, it is a turn made and unmade inside a
        ///     second, and at the angles people actually stand apart at that is fifty degrees out
        ///     and back. What keeps the eyes out of the corner of the socket at this share is the
        ///     ladder's eye budget, which forces the head to take whatever lies beyond the band
        ///     the eyes may rest in; the share only decides how much of a small look the head
        ///     bothers to join.
        /// </remarks>
        private const float GlanceHeadScale = 0.35f;

        /// <summary>Engagement during a check, relative to attending.</summary>
        private const float AudienceCheckEngagementScale = 0.7f;

        /// <summary>Head participation when the character was already looking at the new speaker.</summary>
        private const float EyesOnlyHeadScale = 0.3f;

        /// <summary>
        ///     Head participation while the eyes have gone to somebody talking over the floor
        ///     holder. A fifth of a turn is a look out of the corner of the eye, which is what a
        ///     person does about an interruption they have not yet decided to accept.
        /// </summary>
        private const float ReflexHeadScale = 0.2f;

        /// <summary>
        ///     How far into a challenger's bid for the floor the eyes go. Half of it, so a
        ///     backchannel — a "mm-hmm", a cough, one false positive from voice detection — is
        ///     over before anything moves, and a real interruption is looked at well before the
        ///     room admits it. Derived from the floor rule rather than authored, so a character
        ///     tuned to be hard to interrupt is also slower to look at whoever is trying.
        /// </summary>
        private const float ReflexSustainFraction = 0.5f;

        /// <summary>Attention one idle glance puts on the person it lands on.</summary>
        /// <remarks>
        ///     Above the stand-down floor, so the glance is the largest thing in a quiet room and
        ///     wins the look; well below a speaker, so the first word of a real turn takes it
        ///     straight back.
        /// </remarks>
        private const float SocialIdleAttention = 0.4f;

        /// <summary>
        ///     Shortest gap between two idle glances, whatever the authored interval says. Two
        ///     glances closer together than this read as one restless sweep rather than as a
        ///     character noticing the people it is standing with.
        /// </summary>
        private const float MinSocialIdleGapSeconds = 2f;

        /// <summary>How much shorter the wait between a speaker's audience checks gets per extra listener.</summary>
        /// <remarks>
        ///     Somebody addressing four people looks round more often than somebody addressing
        ///     two — the audience is what has to be held, and there is more of it.
        /// </remarks>
        private const float SpeakerCheckGroupScale = 0.7f;

        /// <summary>
        ///     Floor under the scaled interval. However big the room gets, a speaker that looks
        ///     away oftener than this is not checking its audience, it is distracted.
        /// </summary>
        private const float SpeakerCheckMinIntervalSeconds = 4f;

        /// <summary>
        ///     How wide a window two characters have to book a pulse in before the room considers
        ///     them the same event and pushes them apart. Two people glancing round a quiet room
        ///     on the same frame is the shared edge this module exists to break, in miniature.
        /// </summary>
        private const float PulseBucketSeconds = 2f;

        /// <summary>Event channel an idle glance books its moment on.</summary>
        private const int SocialIdleChannel = 3;

        /// <summary>Event channel a speaker's audience check books its moment on.</summary>
        private const int SpeakerCheckChannel = 4;

        /// <summary>Within this much of the speaker already, a reaction is eyes and nothing else.</summary>
        private const float AlreadyLookingDegrees = 10f;

        /// <summary>
        ///     Below this angle between the speaker and the person being checked, a glance between
        ///     them is too small to read as a glance and only registers as a twitch.
        /// </summary>
        private const float MinimumCheckSeparationDegrees = 12f;

        /// <summary>
        ///     How long a listener settles on a new speaker before any check may fire. Turning to
        ///     somebody and immediately looking away reads as a twitch, not as interest.
        /// </summary>
        private const float SettleBeforeChecksSeconds = 2.5f;

        /// <summary>How far apart, at most, two listeners' first checks of a turn are pushed.</summary>
        private const float PhaseSpreadSeconds = 6f;

        /// <summary>Widest normal deviate a reaction latency is drawn from. Nobody takes ten times as long as usual.</summary>
        private const float MaxReactionZ = 2.5f;

        /// <summary>Distance at which salience is neither raised nor lowered.</summary>
        private const float SalienceReferenceDistance = 4f;

        /// <summary>Angle at which salience is neither raised nor lowered (half of it, as the formula reads).</summary>
        private const float SalienceReferenceDegrees = 60f;

        /// <summary>How much distance moves salience.</summary>
        private const float SalienceDistanceWeight = 0.25f;

        /// <summary>How much the angle off forward moves salience.</summary>
        private const float SalienceAngleWeight = 0.25f;

        /// <summary>Fastest salience: near, in front, impossible to miss.</summary>
        private const float MinSalience = 0.6f;

        /// <summary>Slowest salience: far, off to the side, noticed late.</summary>
        private const float MaxSalience = 1.4f;

        /// <summary>What this character thinks of one person in the room.</summary>
        private struct Slot
        {
            /// <summary>Room key this slot belongs to.</summary>
            public int Key;

            /// <summary>How much of this character's attention they hold, 0–1.</summary>
            public float Attention;

            /// <summary>Whether a reaction has been drawn for their current run of speech.</summary>
            public bool HasReaction;

            /// <summary>Identity of the onset the drawn reaction belongs to.</summary>
            public int ReactionEvent;

            /// <summary>Room time this character may react to that onset.</summary>
            public float ReactionAt;

            /// <summary>The reaction needs no head movement, because the eyes were already there.</summary>
            public bool EyesOnly;

            /// <summary>They are inside their arrival window, so a look at them is a glance rather than attention.</summary>
            public bool Arrival;

            /// <summary>
            ///     The attention they hold is this character's idle glance at them rather than
            ///     anything the conversation put there, so the look is a glance and its
            ///     commitment comes from the idle-glance settings.
            /// </summary>
            public bool Social;

            /// <summary>Something happened to them this tick, as opposed to their value merely standing still.</summary>
            public bool Event;

            /// <summary>
            ///     A raise that has been decided but not yet felt: the responder look at the end
            ///     of a turn and the glance at somebody arriving both reach a listener after a
            ///     reaction latency of their own, booked through the room like a speech onset —
            ///     otherwise every listener would look to the next speaker on the very frame the
            ///     floor emptied, which is the same shared edge the onset reservation exists to
            ///     break, wearing a different hat.
            /// </summary>
            public bool HasPending;

            public float PendingAt;

            public float PendingValue;

            public int PendingEvent;

            /// <summary>
            ///     The turn in which this participant's promotion from challenger to floor holder
            ///     was booked, and whether that booking has landed. The promotion is the moment
            ///     the heads move on an interruption, and it arrives on one shared frame for every
            ///     listener unless each books its own moment for it.
            /// </summary>
            public int PromotedTurn;

            public bool PromotionLanded;

            /// <summary>When this listener's eyes may flick to this participant as an interrupter (0 = not booked).</summary>
            public float ReflexAt;

            /// <summary>
            ///     The times each booking asked for. A resolved slot can move as other listeners
            ///     book the same event, so every open booking is re-resolved each tick from the
            ///     time it originally wanted (negative = no reservation, e.g. an eyes-only reaction).
            /// </summary>
            public float ReactionWantedAt;

            public float PendingWantedAt;

            public float ReflexWantedAt;

            /// <summary>
            ///     The lane each booking was made in, frozen at the moment it was made. The lane
            ///     carries the turn, and re-deriving it every tick would migrate a booking the
            ///     instant the floor moved — which is precisely when its resolved time must not
            ///     jump, because a listener may already be acting on it.
            /// </summary>
            public int ReactionLane;

            public int PendingLane;

            public int ReflexLane;

            /// <summary>
            ///     Which booking the flick is, frozen the same way the lane is. Re-deriving it
            ///     would be re-deriving the speaker's onset, and a booking has to be given back
            ///     under the identity it was made under or it is not given back at all.
            /// </summary>
            public int ReflexEvent;

            /// <summary>Which key the pending raise was booked under (a promotion books under a salted one).</summary>
            public int PendingListener;

            public bool PendingIsPromotion;
        }

        private Slot[] _slots = new Slot[Capacity];
        private Slot[] _scratch = new Slot[Capacity];
        private readonly float[] _distance = new float[Capacity];
        private readonly float[] _yaw = new float[Capacity];
        private readonly bool[] _eligible = new bool[Capacity];
        private readonly ConversationGazeStandDown[] _rejection = new ConversationGazeStandDown[Capacity];
        private int _count;

        private bool _seeded;
        private bool _hasStartTime;
        private float _startTime;

        // The room the open bookings were made in, and the key they were made under. A booking
        // this director drops has to be handed back, and the paths that drop one do not all have
        // a snapshot in hand — the component being disabled is the extreme case, and it is also
        // the one that matters most, because the room it booked in outlives it.
        private ConversationRoomSnapshot _bookedRoom;
        private int _bookedSelfKey;

        /// <summary>Per-character constant near 1 — nobody watches with exactly the same intensity.</summary>
        private float _intensityBias = 1f;

        /// <summary>Per-character phase in 0..1, so two listeners cannot share a check schedule.</summary>
        private float _phase;

        private int _focusKey;
        private float _attendingSince;

        // Whoever has been quietly out-scoring the person being looked at, and since when. It
        // resets the moment somebody else takes the lead or the lead closes, so the clock always
        // measures one standing lead rather than the sum of several that did not last.
        private int _driftKey;
        private float _driftSince;

        private int _lastTurnIndex;
        private int _lastResponderKey;

        // The raise booked for whoever was expected to answer — who it was for, which booking it
        // is, and which turn it belonged to — so it can be withdrawn if the conversation stops
        // expecting them before it lands.
        private int _responderKey;
        private int _responderEvent;
        private int _responderTurn;

        // ── Own turn ─────────────────────────────────────────────────────────
        private bool _wasInOwnTurn;
        private float _ownTurnSince;
        private float _socialSuppressUntil;
        private DeterministicEmbodimentRandom _reflexRandom;

        private float _audienceCountdown;
        private int _pulseKey;
        private float _pulseRemaining;

        // Idle glances between people, while nobody is talking.
        private bool _socialArmed;
        private float _socialCountdown;
        private int _socialKey;
        private int _socialEvent;
        private float _socialRemaining;
        private int _socialLastKey;

        // The speaker's own look round its audience, while it holds the floor.
        private bool _speakerArmed;
        private float _speakerCountdown;
        private int _speakerKey;
        private float _speakerStartAt;
        private float _speakerRemaining;

        /// <summary>Last decision (diagnostics seam).</summary>
        public ConversationGazeState Current { get; private set; } =
            ConversationGazeState.StoodDown(ConversationGazeStandDown.ModeOff);

        /// <summary>Everyone this character currently holds an opinion about. Diagnostics and tests.</summary>
        internal int TrackedCount => _count;

        /// <summary>How much attention <paramref name="key" /> holds, or 0 for somebody unknown.</summary>
        /// <param name="key">Room key to read.</param>
        internal float AttentionOf(int key)
        {
            int index = IndexOf(key);
            return index >= 0 ? _slots[index].Attention : 0f;
        }

        /// <summary>Clears every attention value, timer and latch (component disable, rig rebind).</summary>
        /// <remarks>
        ///     The bookings go back to the room first, and the room is deliberately not reset with
        ///     them: a controller being disabled leaves the conversation rather than ending it
        ///     (<c>RemoveParticipant</c>, not <c>Reset</c>), so the lanes this character booked in
        ///     go on spacing the listeners who are still in the room. Dropping the slots without
        ///     handing the bookings back would leave a departed character's beats spacing theirs.
        /// </remarks>
        public void Reset()
        {
            for (int i = 0; i < _count; i++) ReleaseBookings(i);

            _bookedRoom = default;
            _bookedSelfKey = 0;

            for (int i = 0; i < Capacity; i++)
            {
                _slots[i] = default;
                _scratch[i] = default;
                _distance[i] = 0f;
                _yaw[i] = 0f;
                _eligible[i] = false;
                _rejection[i] = ConversationGazeStandDown.None;
            }

            _count = 0;
            _seeded = false;
            _hasStartTime = false;
            _startTime = 0f;
            _intensityBias = 1f;
            _phase = 0f;
            _focusKey = 0;
            _attendingSince = 0f;
            _driftKey = 0;
            _driftSince = 0f;
            _lastTurnIndex = 0;
            _lastResponderKey = 0;
            _responderKey = 0;
            _responderEvent = 0;
            _responderTurn = 0;
            _wasInOwnTurn = false;
            _ownTurnSince = 0f;
            _socialSuppressUntil = 0f;
            _audienceCountdown = 0f;
            _pulseKey = 0;
            _pulseRemaining = 0f;
            _socialArmed = false;
            _socialCountdown = 0f;
            _socialKey = 0;
            _socialEvent = 0;
            _socialRemaining = 0f;
            _socialLastKey = 0;
            _speakerArmed = false;
            _speakerCountdown = 0f;
            _speakerKey = 0;
            _speakerStartAt = 0f;
            _speakerRemaining = 0f;
            Current = ConversationGazeState.StoodDown(ConversationGazeStandDown.ModeOff);
        }

        /// <summary>Advances the director by one tick and returns this tick's overlay.</summary>
        /// <param name="room">The room's account of the conversation, derived this frame.</param>
        /// <param name="self">This character: who it is, where it stands, where it is already looking.</param>
        /// <param name="tuning">The character's profile, as numbers this director understands.</param>
        /// <param name="deltaTime">Seconds since this character's last cognition tick.</param>
        /// <param name="random">This character's deterministic stream.</param>
        public ConversationGazeState Tick(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            float deltaTime,
            ref DeterministicEmbodimentRandom random)
        {
            float dt = Mathf.Max(0f, deltaTime);

            if (!_seeded)
            {
                _seeded = true;
                // ±8%: enough that two listeners are visibly not the same performance, small
                // enough that neither reads as differently configured.
                _intensityBias = 0.92f + random.Value * 0.16f;
                _phase = random.Value;
                // The reflex path runs after the decision, where the caller's random is no
                // longer in hand; a stream of its own, seeded from the caller's, keeps it
                // deterministic per character.
                _reflexRandom = new DeterministicEmbodimentRandom((uint)(random.Value * 4294967295.0));
            }

            if (!room.IsValid) return Stand(ConversationGazeStandDown.NoSpeaker);

            // Kept, not copied out of: everything this tick reads still comes from the argument.
            // This is only so a booking can be handed back from somewhere that has no snapshot.
            _bookedRoom = room;
            _bookedSelfKey = self.Key;

            if (!_hasStartTime)
            {
                // Everybody standing in the room when this character woke up was always there as
                // far as it is concerned. Without this, a scene load is eight simultaneous
                // arrivals and every character spends its first seconds staring at a stranger.
                _hasStartTime = true;
                _startTime = room.Time;
            }

            Remap(in room);
            Decay(dt, tuning.DecaySeconds);
            MeasureGeometry(in room, in self, in tuning);

            if (self.Mode == GazeSpeakerAttention.Off)
            {
                ClearSocialIdle(spend: false);
                ClearSpeakerCheck();
                return Stand(ConversationGazeStandDown.ModeOff);
            }

            // The character's own turn is sovereign: its state policy already says how it should
            // look at its conversation partner, and layering attention on top of that is what made
            // every listener indistinguishable from the addressee. The one thing a speaker does
            // that its row cannot express is look round the rest of the room, so that — and
            // nothing else — runs here.
            if (self.InOwnTurn)
            {
                if (!_wasInOwnTurn)
                {
                    _wasInOwnTurn = true;
                    _ownTurnSince = room.Time;
                }

                ClearSocialIdle(spend: false);
                return TickSpeakerCheck(in room, in self, in tuning, dt, ref random);
            }

            if (_wasInOwnTurn)
            {
                // The turn just ended. The person it was talking to keeps its attention — a
                // speaker who finishes and immediately glances at a bystander reads as having
                // lost interest in its own listener; the look stays put and waits for the reply.
                // Idle glances between people wait a beat as well.
                _wasInOwnTurn = false;
                _socialSuppressUntil = room.Time + SocialIdleAfterTurnSeconds;
                int addressee = self.CurrentAimTargetKey != ConversationRoomModel.NobodyKey &&
                                self.CurrentAimTargetKey != self.Key
                    ? self.CurrentAimTargetKey
                    : ConversationRoomModel.PlayerKey;
                int addresseeIndex = IndexOf(addressee);
                if (addresseeIndex >= 0) Raise(addresseeIndex, PostTurnAttention);
            }

            ClearSpeakerCheck();
            RefreshAttention(in room, in self, in tuning, ref random);
            TickAudienceCheck(in room, in self, in tuning, dt, ref random);
            TickSocialIdle(in room, in self, in tuning, dt, ref random);
            ConversationGazeState decision = Decide(in room, in self, in tuning, ref random);
            return ApplyInterruptionReflex(in room, in self, in tuning, in decision);
        }

        // ── Bookkeeping ──────────────────────────────────────────────────────

        /// <summary>
        ///     Re-indexes this character's attention onto this frame's participant list. Keys are
        ///     stable, list positions are not — somebody leaving shifts everybody after them — so
        ///     the values travel by key and a newcomer starts at nothing.
        /// </summary>
        private void Remap(in ConversationRoomSnapshot room)
        {
            int count = Mathf.Min(room.ParticipantCount, Capacity);

            for (int i = 0; i < count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                int key = participant != null ? participant.Key : 0;

                _scratch[i] = default;
                _scratch[i].Key = key;
                if (key == 0) continue;

                for (int j = 0; j < _count; j++)
                {
                    if (_slots[j].Key != key) continue;

                    _scratch[i] = _slots[j];
                    break;
                }
            }

            // Somebody who has left takes their slot with them, and every booking in it is a beat
            // that will never be paid. Handed back before the slot goes: the lane belongs to the
            // person the beat was aimed at, and the listeners still in the room are spaced in it.
            for (int i = 0; i < _count; i++)
            {
                int key = _slots[i].Key;
                if (key == 0) continue;

                bool kept = false;
                for (int j = 0; j < count; j++)
                {
                    if (_scratch[j].Key != key) continue;

                    kept = true;
                    break;
                }

                if (!kept) ReleaseBookings(i);
            }

            // A swap, not a copy: both arrays are preallocated and the tick allocates nothing.
            (_slots, _scratch) = (_scratch, _slots);
            _count = count;
        }

        /// <summary>
        ///     Hands back every booking this slot still holds. Used where the slot itself is going
        ///     away, so the flags are not cleared — there is nothing left to clear them on.
        /// </summary>
        /// <param name="index">Slot whose bookings are given up.</param>
        private void ReleaseBookings(int index)
        {
            // A reaction that wanted nothing was never booked: somebody already looking at the
            // speaker turns no head, so the room was never asked to keep it apart from anything.
            if (_slots[index].HasReaction && _slots[index].ReactionWantedAt >= 0f)
                _bookedRoom.ReleaseReaction(
                    _slots[index].ReactionLane, _bookedSelfKey, _slots[index].ReactionEvent);

            if (_slots[index].HasPending)
                _bookedRoom.ReleaseReaction(
                    _slots[index].PendingLane, _slots[index].PendingListener, _slots[index].PendingEvent);

            if (_slots[index].ReflexAt > 0f)
                _bookedRoom.ReleaseReaction(
                    _slots[index].ReflexLane, _bookedSelfKey, _slots[index].ReflexEvent);
        }

        private void DropReaction(int index)
        {
            if (!_slots[index].HasReaction) return;
            if (_slots[index].ReactionWantedAt >= 0f)
                _bookedRoom.ReleaseReaction(
                    _slots[index].ReactionLane, _bookedSelfKey, _slots[index].ReactionEvent);
            _slots[index].HasReaction = false;
            _slots[index].ReactionWantedAt = -1f;
        }

        /// <summary>Gives up the raise booked at this slot, if there is one, and forgets it.</summary>
        /// <param name="index">Slot whose pending raise is withdrawn.</param>
        private void DropPending(int index)
        {
            if (!_slots[index].HasPending) return;

            _slots[index].HasPending = false;
            _bookedRoom.ReleaseReaction(
                _slots[index].PendingLane, _slots[index].PendingListener, _slots[index].PendingEvent);
        }

        /// <summary>Gives up the eye flick booked at this slot, if there is one, and forgets it.</summary>
        /// <param name="index">Slot whose flick is withdrawn.</param>
        private void DropReflex(int index)
        {
            if (_slots[index].ReflexAt <= 0f) return;

            _slots[index].ReflexAt = 0f;
            _bookedRoom.ReleaseReaction(
                _slots[index].ReflexLane, _bookedSelfKey, _slots[index].ReflexEvent);
        }

        /// <summary>Everything decays; speech and the other refreshes then put back what they are worth.</summary>
        private void Decay(float deltaTime, float decaySeconds)
        {
            float factor = deltaTime > 0f ? Mathf.Exp(-deltaTime / Mathf.Max(0.01f, decaySeconds)) : 1f;

            for (int i = 0; i < _count; i++)
            {
                _slots[i].Attention *= factor;
                _slots[i].Event = false;
            }
        }

        /// <summary>
        ///     Where everybody is relative to this character, and whether they can be looked at
        ///     at all: near enough, within reach of the head or the body, and actually visible.
        ///     Measured once per tick because four different rules want the same numbers, and
        ///     <see cref="_eligible" /> is the single gate all four of them read — so somebody
        ///     ruled out here is never attended, never glanced at while the room is quiet, never
        ///     picked for an audience check and never reflexively looked at.
        /// </summary>
        private void MeasureGeometry(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning)
        {
            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key)
                {
                    _distance[i] = 0f;
                    _yaw[i] = 0f;
                    _eligible[i] = false;
                    _rejection[i] = ConversationGazeStandDown.NoAnchor;
                    continue;
                }

                // The person already being looked at is judged on the way out, not on the way in.
                bool retaining = participant.Key == _focusKey;

                Vector3 toParticipant = participant.HeadPoint - self.HeadPoint;
                _distance[i] = toParticipant.magnitude;
                _yaw[i] = SignedYaw(self.Forward, toParticipant);
                _eligible[i] = Eligible(
                    toParticipant.sqrMagnitude > 1e-6f, _distance[i], Mathf.Abs(_yaw[i]),
                    in tuning, retaining, out ConversationGazeStandDown rejection);
                _rejection[i] = rejection;

                // A wall is the last gate, not the first: somebody out of earshot or behind the
                // character is better explained by the gate that would have stopped them anyway,
                // and "they are behind a wall" is only the answer when nothing else was wrong.
                bool hidden = retaining
                    ? self.CannotSeeFor(participant.Key, room.Time, RetainOcclusionSeconds)
                    : self.CannotSee(participant.Key);
                if (!_eligible[i] || !hidden) continue;

                _eligible[i] = false;
                _rejection[i] = ConversationGazeStandDown.NoLineOfSight;
            }
        }

        /// <summary>
        ///     Whether this character is allowed and able to look at somebody.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Body turns are what makes a speaker behind the character reachable at all: with
        ///         them allowed the reorientation director brings the body round, so the head
        ///         angle stops being the limit.
        ///     </para>
        ///     <para>
        ///         <paramref name="retaining" /> widens both limits for the one person the
        ///         character is already looking at. Somebody standing at the edge of either gate
        ///         crosses it several times a second — they shift their weight, the character
        ///         turns a few degrees, the reported head point sways with an idle clip — and
        ///         without the margin the look is dropped and re-taken on that rhythm.
        ///     </para>
        /// </remarks>
        /// <param name="hasPoint">Whether there is a direction to them at all.</param>
        /// <param name="distance">Metres between the two of them.</param>
        /// <param name="yawDegrees">Unsigned angle off this character's forward.</param>
        /// <param name="tuning">The authored limits.</param>
        /// <param name="retaining">Whether this is the person currently being looked at.</param>
        /// <param name="reason">Which gate refused them, when one did.</param>
        private static bool Eligible(
            bool hasPoint,
            float distance,
            float yawDegrees,
            in ConversationGazeTuning tuning,
            bool retaining,
            out ConversationGazeStandDown reason)
        {
            if (!hasPoint)
            {
                reason = ConversationGazeStandDown.NoAnchor;
                return false;
            }

            float maxDistance = retaining ? tuning.MaxDistance * RetainDistanceScale : tuning.MaxDistance;
            if (tuning.MaxDistance > 0f && distance > maxDistance)
            {
                reason = ConversationGazeStandDown.TooFar;
                return false;
            }

            float maxYaw = retaining ? tuning.MaxYawDegrees + RetainYawSlackDegrees : tuning.MaxYawDegrees;
            if (!tuning.AllowBodyTurn && yawDegrees > maxYaw)
            {
                reason = ConversationGazeStandDown.TooWide;
                return false;
            }

            reason = ConversationGazeStandDown.None;
            return true;
        }

        // ── The attention model ──────────────────────────────────────────────

        /// <summary>
        ///     The four things that put attention back onto somebody: they are talking, they are
        ///     the one being talked to, they are the one everybody is waiting to hear from, or
        ///     they have just walked in. Each raises their value rather than setting it, so a
        ///     speaker who is also the expected responder is not quietly demoted to 0.75.
        /// </summary>
        private void RefreshAttention(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random)
        {
            float now = room.Time;
            bool floorEmpty = room.FloorKey == ConversationRoomModel.NobodyKey;

            // Who is expected to answer is an event, not a condition. Refreshing it every tick
            // while the room is quiet would hold a listener in the conversation for ever, and
            // there would be no such thing as a conversation ending.
            bool responderEvent =
                floorEmpty &&
                (room.TurnIndex != _lastTurnIndex || room.ExpectedResponderKey != _lastResponderKey);
            _lastTurnIndex = room.TurnIndex;
            _lastResponderKey = room.ExpectedResponderKey;

            // A raise booked for the person who was going to answer belongs to that person and to
            // that turn. When the player turns from B to C in the gap, or the turn moves on before
            // the booking lands, the look B was going to get is no longer anybody's intention —
            // and paying it anyway is a turn of the head toward somebody the conversation left
            // behind half a second ago, followed by a second turn to C. The booking is withdrawn
            // where it stands; the branch below makes a fresh one for whoever it is now.
            if (_responderKey != ConversationRoomModel.NobodyKey &&
                (room.TurnIndex != _responderTurn || room.ExpectedResponderKey != _responderKey))
                CancelPendingResponder();

            // Who is being talked to is only knowable once this character has noticed the turn at
            // all — otherwise a listener glances at the addressee in the gap before it has
            // reacted to the person addressing them, which is a flick nobody asked for.
            int floorIndex = IndexOf(room.FloorKey);
            // The addressee is raised only once this listener has actually noticed the speaker:
            // before that, a 0.6 on the person being spoken to would beat a speaker still at 0 and
            // send the look to the answerer before the question.
            int addressee = floorIndex >= 0 && ReactionElapsed(floorIndex, now)
                ? FloorAddresseeKey(in room)
                : ConversationRoomModel.NobodyKey;

            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key) continue;

                // Open bookings are re-resolved every tick: the room spaces listeners by the rank
                // of their wanted times, so a slot can move as others book the same event.
                if (_slots[i].HasReaction && _slots[i].ReactionWantedAt >= 0f)
                    _slots[i].ReactionAt = room.ReserveReaction(
                        _slots[i].ReactionLane, self.Key, _slots[i].ReactionEvent,
                        _slots[i].ReactionWantedAt, tuning.MinSeparationSeconds);
                if (_slots[i].HasPending)
                    _slots[i].PendingAt = room.ReserveReaction(
                        _slots[i].PendingLane, _slots[i].PendingListener, _slots[i].PendingEvent,
                        _slots[i].PendingWantedAt, tuning.MinSeparationSeconds);

                if (participant.IsSpeaking)
                {
                    int eventId = ReactionEventId(participant);
                    if (!_slots[i].HasReaction || _slots[i].ReactionEvent != eventId)
                    {
                        OpenReaction(i, eventId, participant, in room, in self, in tuning, ref random);
                        _slots[i].PromotionLanded = false;
                        _slots[i].PromotedTurn = -1;
                    }

                    // Not before the reaction lands: a listener who has not noticed yet has no
                    // more attention on the speaker than they had a moment ago.
                    if (now >= _slots[i].ReactionAt)
                    {
                        bool holdsFloor = participant.Key == room.FloorKey;
                        if (holdsFloor && !_slots[i].PromotionLanded && _slots[i].PromotedTurn != room.TurnIndex)
                        {
                            // Taking the floor is a second event. A listener whose onset reaction
                            // landed a while ago was watching a challenger; the room handing that
                            // challenger the floor is what moves the head, and it happens on one
                            // frame for everybody — so it is booked like the onset was, with a
                            // shorter latency, and lands one listener at a time. A reaction that
                            // only just landed is the promotion too: that listener is turning anyway.
                            _slots[i].PromotedTurn = room.TurnIndex;
                            if (_slots[i].EyesOnly || now - _slots[i].ReactionAt <= PromotionGraceSeconds)
                                _slots[i].PromotionLanded = true;
                            else
                                SchedulePending(i, SpeakerAttention, eventId, PromotionLatencyScale,
                                    in room, in self, in tuning, ref random, promotion: true);
                        }

                        Raise(i, holdsFloor && _slots[i].PromotionLanded ? SpeakerAttention : ChallengerAttention);
                    }
                }
                else
                {
                    // They stopped talking, so there is nothing left to flick at. The room gets
                    // the moment back: a flick nobody will make must not go on spacing the looks
                    // that this listener does make at the same face.
                    if (_slots[i].HasReaction && now < _slots[i].ReactionAt)
                        DropReaction(i);
                    if (_slots[i].HasPending && _slots[i].PendingIsPromotion)
                        DropPending(i);
                    DropReflex(i);
                }

                if (responderEvent && participant.Key == room.ExpectedResponderKey)
                {
                    SchedulePending(i, ResponderAttention, ResponderEventId(in room), ResponderLatencyScale,
                        in room, in self, in tuning, ref random);
                    _responderKey = participant.Key;
                    _responderEvent = _slots[i].PendingEvent;
                    _responderTurn = room.TurnIndex;
                }

                // The support the person being spoken to carries does not end when the floor
                // does. An empty floor is the middle of a hand-off, not the end of one: the
                // question has been asked, the answer has not started, and for those few tenths
                // of a second the person about to give it is exactly as interesting as they were
                // while they were being asked. Reading it off the floor alone withdrew it on that
                // single tick — their value began decaying, the speaker who had just stopped was
                // still warm enough to hold the look through the hysteresis, and the head went to
                // them anyway when the booked raise landed. Two movements where there was one
                // intention.
                //
                // It is keyed to that booking rather than to a clock, so it lasts exactly as long
                // as the thing it is bridging to and not a moment more: the raise lands and takes
                // over, or the conversation stops expecting them and the booking is withdrawn
                // with it. A quiet room still runs out of people worth looking at.
                bool bridgingToTheAnswer =
                    floorIndex < 0 &&
                    participant.Key == _responderKey &&
                    participant.Key == room.ExpectedResponderKey &&
                    _slots[i].HasPending &&
                    _slots[i].PendingEvent == _responderEvent;

                if ((addressee != ConversationRoomModel.NobodyKey && participant.Key == addressee) ||
                    bridgingToTheAnswer)
                    RaiseHeld(i, AddresseeAttention);

                bool arrived = participant.JoinedAt > _startTime && now - participant.JoinedAt < ArrivalPulseSeconds;
                _slots[i].Arrival = arrived;
                if (arrived && !_slots[i].HasPending && _slots[i].Attention < ArrivalAttention)
                    SchedulePending(i, ArrivalAttention, ArrivalEventId(participant), 1f,
                        in room, in self, in tuning, ref random);

                if (_slots[i].HasPending && now >= _slots[i].PendingAt)
                {
                    _slots[i].HasPending = false;
                    if (_slots[i].PendingIsPromotion) _slots[i].PromotionLanded = true;
                    Raise(i, _slots[i].PendingValue);
                }
            }
        }


        /// <summary>
        ///     Drops the raise booked for whoever was expected to answer, if it has not been paid
        ///     yet. Only that booking: a pending promotion, arrival pulse or idle glance on the
        ///     same person was made for a different reason and is none of this rule's business.
        /// </summary>
        private void CancelPendingResponder()
        {
            int index = IndexOf(_responderKey);
            if (index >= 0 && _slots[index].HasPending && _slots[index].PendingEvent == _responderEvent)
                DropPending(index);

            _responderKey = ConversationRoomModel.NobodyKey;
            _responderEvent = 0;
        }

        /// <summary>
        ///     Books a raise for later: the same latency law and the same room reservation a
        ///     speech onset gets, so listeners look to the next speaker one after another rather
        ///     than on the frame the turn ended. A later booking for the same event replaces
        ///     nothing — the first one stands.
        /// </summary>
        private void SchedulePending(
            int index,
            float value,
            int eventId,
            float latencyScale,
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random,
            bool promotion = false)
        {
            if (_slots[index].HasPending && _slots[index].PendingEvent == eventId) return;

            // A raise this one replaces is a raise nobody will pay, and the room is holding a
            // moment for it. Given back before the new one is made, or the two would be spaced
            // against each other and this listener would push its own beat later.
            DropPending(index);

            // A promotion books in the SAME lane as the speaker's onset, under a salted listener
            // key: the heads that turn on the onset and the heads that turn when the floor
            // moves are one crowd turning to one person, and they are spaced as one crowd.
            int listenerKey = promotion ? self.Key ^ PromotionListenerSalt : self.Key;
            int lane = room.LaneFor(_slots[index].Key);
            float at = BookMoment(
                index, lane, listenerKey, eventId, latencyScale, in room, in self, in tuning, ref random,
                out float wantedAt);
            _slots[index].PendingWantedAt = wantedAt;
            _slots[index].PendingLane = lane;
            _slots[index].PendingListener = listenerKey;
            _slots[index].PendingIsPromotion = promotion;

            _slots[index].HasPending = true;
            _slots[index].PendingAt = at;
            _slots[index].PendingValue = value;
            _slots[index].PendingEvent = eventId;
        }

        /// <summary>Keeps a listener's promotion booking apart from its onset booking in one lane.</summary>
        private const int PromotionListenerSalt = 0x40000000;

        /// <summary>
        ///     Draws when this character would act on its own, then asks the room to move it far
        ///     enough from everybody else acting on the same thing that the two read as two
        ///     people. Every scheduled beat in this director goes through here — a reaction, a
        ///     look to whoever answers next, an idle glance, a speaker's look round the room — so
        ///     there is exactly one place where "no two characters on the same frame" is decided.
        /// </summary>
        /// <param name="index">Slot the beat is aimed at, whose geometry sets the salience.</param>
        /// <param name="laneId">What the beat is spaced against — whoever it lands on, this turn.</param>
        /// <param name="listenerKey">Who the booking belongs to, salted when one listener holds several.</param>
        /// <param name="eventId">Which of this listener's beats it is. Listeners landing on one face are pushed apart.</param>
        /// <param name="latencyScale">Multiplier on the drawn latency: anticipation is slower than a reaction.</param>
        /// <param name="room">The room, which owns the reservation.</param>
        /// <param name="self">This character, which is who the reservation is booked for.</param>
        /// <param name="tuning">Latency law.</param>
        /// <param name="random">This character's deterministic stream.</param>
        private float BookMoment(
            int index,
            int laneId,
            int listenerKey,
            int eventId,
            float latencyScale,
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random,
            out float wantedAt)
        {
            float latency = ReactionLatency(in tuning, _distance[index], Mathf.Abs(_yaw[index]), ref random) *
                            latencyScale;
            wantedAt = room.Time + latency;
            return room.ReserveReaction(laneId, listenerKey, eventId, wantedAt, tuning.MinSeparationSeconds);
        }

        /// <summary>
        ///     Identity of a pulse two characters could plausibly perform together: who it is
        ///     aimed at (supplied by the target lane), and which clock window it was decided in. Two characters glancing
        ///     at the same person within one window share it and are separated; the same character
        ///     glancing at the same person again later is a different event, so it draws a fresh
        ///     moment rather than being handed back the one it already used.
        /// </summary>
        /// <param name="channel">Which beat this is, so two kinds of pulse never collide.</param>
        /// <param name="time">Room time the pulse was decided at; retain the full bucket for long sessions.</param>
        internal static int PulseEventId(int channel, float time)
        {
            int bucket = Mathf.FloorToInt(time / PulseBucketSeconds);
            return unchecked(int.MinValue + bucket * 16 + channel);
        }

        /// <summary>Distinct from every onset event id: onsets are keyed on the speaker, these on the turn.</summary>
        private static int ResponderEventId(in ConversationRoomSnapshot room) => -1000 - room.TurnIndex;

        private static int ArrivalEventId(ConversationParticipant participant) =>
            -2000000 - (participant.Key & 0xFFFF);

        /// <summary>
        ///     Raises somebody's attention to <paramref name="value" />, never lowers it, and
        ///     notes it as an event when the raise was a jump rather than a top-up.
        /// </summary>
        /// <param name="index">Slot to raise.</param>
        /// <param name="value">Attention this refresh is worth.</param>
        private void Raise(int index, float value)
        {
            float previous = _slots[index].Attention;
            if (value <= previous) return;

            _slots[index].Attention = value;
            if (value - previous >= EventJumpThreshold) _slots[index].Event = true;
        }

        /// <summary>
        ///     Raises somebody's attention without calling it an event. For the standing
        ///     conditions — being the person a turn is addressed to — which describe how the room
        ///     is arranged rather than something that just happened in it.
        /// </summary>
        /// <param name="index">Slot to raise.</param>
        /// <param name="value">Attention the condition is worth.</param>
        private void RaiseHeld(int index, float value) =>
            _slots[index].Attention = Mathf.Max(_slots[index].Attention, value);

        /// <summary>
        ///     Draws this character's reaction to one speech onset and books it with the room.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The latency is log-normal — most reactions land near the median and a few take
        ///         much longer, which is what human reaction times actually look like — and scaled
        ///         by salience, so a speaker who is near and in front is noticed sooner than one
        ///         who is across the room and off to the side. Two listeners standing in different
        ///         places therefore differ before either of them has drawn anything.
        ///     </para>
        ///     <para>
        ///         Somebody already looking at the speaker skips both: there is no latency because
        ///         there is nothing to turn, and no booking because a booking exists to keep
        ///         <i>head turns</i> apart and this one moves no head.
        ///     </para>
        /// </remarks>
        private void OpenReaction(
            int index,
            int eventId,
            ConversationParticipant speaker,
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random)
        {
            DropReaction(index);
            bool alreadyLooking =
                (self.CurrentAimTargetKey != ConversationRoomModel.NobodyKey &&
                 self.CurrentAimTargetKey == speaker.Key) ||
                Mathf.Abs(Mathf.DeltaAngle(self.CurrentAimYawDegrees, _yaw[index])) < AlreadyLookingDegrees;

            float reactAt;
            float wantedAt = -1f;
            int lane = room.LaneFor(speaker.Key);
            if (alreadyLooking)
            {
                reactAt = room.Time;
            }
            else
            {
                float latency = ReactionLatency(in tuning, _distance[index], Mathf.Abs(_yaw[index]), ref random);
                wantedAt = room.Time + latency;
                reactAt = room.ReserveReaction(lane, self.Key, eventId, wantedAt, tuning.MinSeparationSeconds);
            }
            _slots[index].ReactionWantedAt = wantedAt;
            _slots[index].ReactionLane = lane;

            _slots[index].HasReaction = true;
            _slots[index].ReactionEvent = eventId;
            _slots[index].ReactionAt = reactAt;
            _slots[index].EyesOnly = alreadyLooking;
        }

        /// <summary>Reaction latency for one onset: a log-normal draw, scaled by how salient the speaker is.</summary>
        private static float ReactionLatency(
            in ConversationGazeTuning tuning,
            float distance,
            float yawDegrees,
            ref DeterministicEmbodimentRandom random)
        {
            // Box–Muller from two uniforms: the character's own stream, so the same seed always
            // produces the same person.
            float u1 = Mathf.Max(1e-6f, random.Value);
            float u2 = random.Value;
            float z = Mathf.Clamp(
                Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2),
                -MaxReactionZ, MaxReactionZ);

            float salience = Mathf.Clamp(
                1f +
                SalienceDistanceWeight * (distance / SalienceReferenceDistance - 1f) +
                SalienceAngleWeight * (yawDegrees / SalienceReferenceDegrees - 0.5f),
                MinSalience, MaxSalience);

            return Mathf.Max(0f, tuning.ReactionMedianSeconds * Mathf.Exp(tuning.ReactionSigma * z) * salience);
        }

        /// <summary>Whether this character has noticed whatever <paramref name="index" /> is doing.</summary>
        private bool ReactionElapsed(int index, float now) =>
            !_slots[index].HasReaction || now >= _slots[index].ReactionAt;

        // ── Audience checks ──────────────────────────────────────────────────

        /// <summary>
        ///     While watching somebody talk for a while, a listener glances at the person being
        ///     talked to. The pulse decides the look outright rather than bidding for it: a check
        ///     is worth 0.9 and a live speaker is worth 1, so an argmax could never let it through
        ///     — and a check that never fires is not a check.
        /// </summary>
        private void TickAudienceCheck(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            float deltaTime,
            ref DeterministicEmbodimentRandom random)
        {
            if (_pulseRemaining > 0f)
            {
                _pulseRemaining -= deltaTime;
                int live = IndexOf(_pulseKey);

                if (_pulseRemaining > 0f && live >= 0)
                {
                    Raise(live, AudienceCheckAttention);
                    return;
                }

                if (live >= 0)
                    _slots[live].Attention = Mathf.Min(_slots[live].Attention, AudienceCheckSpentAttention);

                _pulseKey = 0;
                _pulseRemaining = 0f;
                ArmAudienceCheck(in tuning, ref random, first: false);
                return;
            }

            if (!tuning.EnableAudienceChecks) return;

            int focusIndex = IndexOf(_focusKey);
            if (focusIndex < 0) return;

            ConversationParticipant focused = room.GetParticipant(focusIndex);
            if (focused == null || !focused.IsSpeaking) return;
            if (room.Time - _attendingSince < SettleBeforeChecksSeconds) return;

            _audienceCountdown -= deltaTime;
            if (_audienceCountdown > 0f) return;

            int addressee = FloorAddresseeKey(in room);
            int index = addressee == self.Key || addressee == _focusKey ? -1 : IndexOf(addressee);
            // Too close to the speaker to read as a look away, or too far round to be reached
            // without the body this beat has already ruled out. Both are the same question asked
            // at the two ends of the range: is this a glance at all?
            if (index < 0 || !_eligible[index] || !WithinGlanceReach(index, in tuning) ||
                Mathf.Abs(Mathf.DeltaAngle(_yaw[index], _yaw[focusIndex])) < MinimumCheckSeparationDegrees)
            {
                ArmAudienceCheck(in tuning, ref random, first: false);
                return;
            }

            _pulseRemaining = Mathf.Max(0f, tuning.AudienceCheckDuration) * random.Range(0.7f, 1.3f);
            if (_pulseRemaining <= 0f)
            {
                ArmAudienceCheck(in tuning, ref random, first: false);
                return;
            }

            _pulseKey = addressee;
            Raise(index, AudienceCheckAttention);
        }

        /// <summary>
        ///     Re-arms the check timer. The first one after taking somebody up is pushed out by
        ///     this character's own phase, which is what stops two listeners glancing in the same
        ///     frame however close together they took up the speaker.
        /// </summary>
        private void ArmAudienceCheck(
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random,
            bool first)
        {
            _audienceCountdown = random.Range(tuning.AudienceCheckIntervalMin, tuning.AudienceCheckIntervalMax);
            if (first) _audienceCountdown += _phase * PhaseSpreadSeconds;
        }

        // ── Social idle ──────────────────────────────────────────────────────

        /// <summary>
        ///     What a character does with the people around it when nobody is talking: every so
        ///     often it looks at one of them for a moment, and then it does not look at that one
        ///     next time.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This is the whole of what <c>CharacterGlanceDirector</c> used to do, moved to
        ///         where the room is known. The old one fired a glance at whichever character the
        ///         arbiter happened to score highest, on a clock of its own, with no idea whether
        ///         anybody was talking or whether the character standing next to it was glancing
        ///         on the same frame. Here it is one more thing the attention model expresses: the
        ///         glance is a raise like any other, booked through the room like any other, and
        ///         the look goes to it because in a quiet room it is the largest thing there is.
        ///     </para>
        ///     <para>
        ///         It runs only while the room is genuinely quiet — nobody speaking, and nobody
        ///         holding enough of this character's attention to be worth watching, which is the
        ///         same test that stands the director down for
        ///         <see cref="ConversationGazeStandDown.NoSpeaker" />. The first word of a real
        ///         turn ends it.
        ///     </para>
        /// </remarks>
        private void TickSocialIdle(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            float deltaTime,
            ref DeterministicEmbodimentRandom random)
        {
            // Idle glances between people belong to a room that has actually gone quiet — not
            // to the pause after a sentence, and not to the seconds after this character's own
            // turn, when its look belongs to whoever it was talking to. And not while something
            // scripted already owns the gaze: the curiosity glance at the player is the other
            // half of this character's idle life, it outranks every provider candidate, and two
            // of them running at once is one glance interrupting another.
            if (!tuning.EnableSocialIdle || self.ScriptedLookActive || !SociallyIdle(in room, in self) ||
                room.SilenceSeconds < SocialIdleSilenceSeconds || room.Time < _socialSuppressUntil)
            {
                // Whoever was being glanced at keeps whatever attention they had: spending it
                // here would land on the very person who has just started talking.
                ClearSocialIdle(spend: false);
                _socialArmed = false;
                return;
            }

            if (_socialRemaining > 0f)
            {
                _socialRemaining -= deltaTime;
                int live = IndexOf(_socialKey);
                if (_socialRemaining > 0f && live >= 0 && _eligible[live])
                {
                    _slots[live].Social = true;
                    RaiseHeld(live, SocialIdleAttention);
                    return;
                }

                // The glance asked its question. Spending it is what brings the character back to
                // its own life instead of leaving it staring at somebody for a whole decay.
                ClearSocialIdle(spend: true);
                ArmSocialIdle(in tuning, ref random, first: false);
                return;
            }

            if (_socialKey != ConversationRoomModel.NobodyKey)
            {
                int booked = IndexOf(_socialKey);
                bool lost =
                    booked < 0 || !_eligible[booked] || !ModeAllows(self.Mode, _socialKey) ||
                    (_slots[booked].HasPending && _slots[booked].PendingEvent != _socialEvent);
                if (lost)
                {
                    ClearSocialIdle(spend: false);
                    ArmSocialIdle(in tuning, ref random, first: false);
                    return;
                }

                // Still waiting for the moment the room gave it.
                if (_slots[booked].HasPending) return;

                // The raise has been paid in this tick, so the glance starts now.
                _socialLastKey = _socialKey;
                _socialRemaining = Mathf.Max(0f, tuning.SocialIdleDuration);
                if (_socialRemaining <= 0f)
                {
                    ClearSocialIdle(spend: true);
                    ArmSocialIdle(in tuning, ref random, first: false);
                }

                return;
            }

            if (!_socialArmed)
            {
                ArmSocialIdle(in tuning, ref random, first: true);
                return;
            }

            _socialCountdown -= deltaTime;
            if (_socialCountdown > 0f) return;

            int pick = PickSocialTarget(in room, in self, in tuning, ref random);
            if (pick < 0)
            {
                ArmSocialIdle(in tuning, ref random, first: false);
                return;
            }

            ConversationParticipant target = room.GetParticipant(pick);
            _socialKey = target.Key;
            _socialEvent = PulseEventId(SocialIdleChannel, room.Time);
            // Flagged from the moment it is booked, not from the moment it lands: the raise
            // arrives a tick before the glance is read back, and a quiet room that counted its own
            // glance as somebody worth attending would cancel it on the frame it was paid in.
            _slots[pick].Social = true;
            SchedulePending(pick, SocialIdleAttention, _socialEvent, 1f, in room, in self, in tuning, ref random);
        }

        /// <summary>
        ///     Whether the room is quiet enough for idle life: nobody talking, and nobody left
        ///     with enough attention to still be part of a conversation. The glance this character
        ///     is in the middle of does not count against it, or it would end itself.
        /// </summary>
        private bool SociallyIdle(in ConversationRoomSnapshot room, in ConversationGazeSelf self)
        {
            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key) continue;
                if (participant.IsSpeaking) return false;
                if (!_slots[i].Social && _slots[i].Attention >= StandDownAttention) return false;
            }

            return true;
        }

        /// <summary>
        ///     Which person this glance lands on: somebody reachable, somebody this character is
        ///     allowed to look at, and — when there is anybody else at all — not the one it looked
        ///     at last. Nearer people are glanced at more often, which is what standing in a group
        ///     rather than in a queue looks like.
        /// </summary>
        /// <remarks>
        ///     The player is deliberately not a candidate. A character alone with a player has the
        ///     curiosity glance for exactly this beat, and two systems glancing at the same person
        ///     on two clocks is the pile-up this rewrite exists to end.
        /// </remarks>
        private int PickSocialTarget(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random)
        {
            int pick = PickSocialTargetExcept(in room, in self, in tuning, ref random, _socialLastKey);
            if (pick >= 0) return pick;

            // With one other character in the room, "not the same one twice" would mean never
            // again. Variety when there is variety to be had; company otherwise.
            return PickSocialTargetExcept(
                in room, in self, in tuning, ref random, ConversationRoomModel.NobodyKey);
        }

        /// <summary>Weighted pick among the people this character may glance at, minus one of them.</summary>
        /// <param name="room">This frame's room.</param>
        /// <param name="self">This character.</param>
        /// <param name="tuning">The character's comfort angles, which decide what a glance can reach.</param>
        /// <param name="random">This character's deterministic stream.</param>
        /// <param name="excludedKey">Somebody to leave out, or <c>0</c> to consider everybody.</param>
        private int PickSocialTargetExcept(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random,
            int excludedKey)
        {
            int chosen = -1;
            float total = 0f;

            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key) continue;
                if (participant.IsPlayer || participant.Key == excludedKey) continue;
                if (!_eligible[i] || !ModeAllows(self.Mode, participant.Key)) continue;
                // Reachable as a glance, not merely reachable. The eligibility gate asks what
                // the character is ALLOWED to look at, which is a different question from what
                // it can look at with its eyes and a comfortable neck — and this beat has
                // already said the body stays out of it.
                if (!WithinGlanceReach(i, in tuning)) continue;
                // Somebody with a raise already booked is in the middle of being noticed for a
                // better reason than this one, and a glance would overwrite the booking.
                if (_slots[i].HasPending) continue;

                // A reservoir pick: each candidate takes the place of the current one with a
                // probability that is its share of the weight so far, so one pass over the room
                // draws from the whole of it without keeping a list.
                float weight = 1f / (1f + _distance[i]);
                total += weight;
                if (random.Range(0f, total) <= weight) chosen = i;
            }

            return chosen;
        }

        /// <summary>
        ///     Sets the wait before the next idle glance, never shorter than the gap that keeps
        ///     two of them from reading as one restless sweep. The first glance after a room falls
        ///     quiet is pushed out by this character's own phase, so a room that stops talking
        ///     together does not start glancing together.
        /// </summary>
        /// <param name="tuning">Authored cadence.</param>
        /// <param name="random">This character's deterministic stream.</param>
        /// <param name="first">Whether this is the first glance of a quiet spell.</param>
        private void ArmSocialIdle(
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random,
            bool first)
        {
            _socialArmed = true;
            _socialCountdown = Mathf.Max(
                MinSocialIdleGapSeconds,
                random.Range(tuning.SocialIdleIntervalMin, tuning.SocialIdleIntervalMax));
            if (first) _socialCountdown += _phase * PhaseSpreadSeconds;
        }

        /// <summary>Ends whatever idle glance is running or booked.</summary>
        /// <param name="spend">
        ///     Whether the person glanced at loses the attention the glance gave them. True when
        ///     the glance ran its course; false when something more important took the room, so
        ///     that a raise the conversation has just made is not undone by this.
        /// </param>
        private void ClearSocialIdle(bool spend)
        {
            int index = IndexOf(_socialKey);
            if (index >= 0)
            {
                _slots[index].Social = false;
                if (_slots[index].HasPending && _slots[index].PendingEvent == _socialEvent)
                    DropPending(index);
                if (spend) _slots[index].Attention = 0f;
            }

            _socialKey = ConversationRoomModel.NobodyKey;
            _socialEvent = 0;
            _socialRemaining = 0f;
        }

        // ── The speaker's own audience ───────────────────────────────────────

        /// <summary>
        ///     What the character does with the rest of the room while it holds the floor: every
        ///     so often it looks at somebody who is not the person it is talking to, and comes
        ///     back. Addressing three people while looking at one of them is how a character
        ///     reads as talking <i>at</i> somebody instead of <i>to</i> a group.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The decision it returns is not the ordinary overlay. The character's own
        ///         dialogue state already says how it looks at the person it is talking to, and
        ///         that row is right; all this asks for is the eyes, for as long as the glance
        ///         lasts. The wiring layer applies a
        ///         <see cref="ConversationGazeLook.SpeakerCheck" /> by standing the player anchor
        ///         aside and naming the person being checked, and leaves every other number in the
        ///         row alone.
        ///     </para>
        ///     <para>
        ///         A pair is not an audience: with one other person in the room there is nobody to
        ///         look round at, and a character that looked away anyway would only be avoiding
        ///         eye contact. The wait shortens with the size of the group, because the bigger
        ///         the audience the more of it there is to hold.
        ///     </para>
        /// </remarks>
        private ConversationGazeState TickSpeakerCheck(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            float deltaTime,
            ref DeterministicEmbodimentRandom random)
        {
            // Wrapping up ends a check in flight as well: a speaker holds whoever it is
            // talking to as it hands over.
            if (self.TurnEnding)
            {
                ClearSpeakerCheck();
                _speakerArmed = false;
                return Stand(ConversationGazeStandDown.OwnTurn);
            }

            if (_speakerRemaining > 0f)
            {
                _speakerRemaining -= deltaTime;
                int live = IndexOf(_speakerKey);
                if (_speakerRemaining > 0f && live >= 0 && _eligible[live])
                    return Attend(live, room.GetParticipant(live), in tuning, LookKind.SpeakerCheck);

                ClearSpeakerCheck();
                ArmSpeakerCheck(in tuning, ref random, CountReachableOthers(in room, in self), first: false);
                return Stand(ConversationGazeStandDown.OwnTurn);
            }

            if (!tuning.EnableAudienceChecks) return Stand(ConversationGazeStandDown.OwnTurn);

            // Not in the first breath of a turn, and not while wrapping it up. The check that
            // fired in the last second of a turn and ran on into the settle read as the speaker
            // finishing and glancing at somebody else — a look nobody makes; a speaker holds
            // whoever they are talking to as they hand over.
            if (self.TurnEnding || room.Time - _ownTurnSince < SpeakerCheckEarliestSeconds)
            {
                ClearSpeakerCheck();
                _speakerArmed = false;
                return Stand(ConversationGazeStandDown.OwnTurn);
            }

            int others = CountReachableOthers(in room, in self);
            if (others < 2)
            {
                ClearSpeakerCheck();
                _speakerArmed = false;
                return Stand(ConversationGazeStandDown.OwnTurn);
            }

            if (!_speakerArmed)
            {
                ArmSpeakerCheck(in tuning, ref random, others, first: true);
                return Stand(ConversationGazeStandDown.OwnTurn);
            }

            if (_speakerKey != ConversationRoomModel.NobodyKey)
            {
                int booked = IndexOf(_speakerKey);
                if (booked < 0 || !_eligible[booked])
                {
                    ClearSpeakerCheck();
                    ArmSpeakerCheck(in tuning, ref random, others, first: false);
                    return Stand(ConversationGazeStandDown.OwnTurn);
                }

                // Booked, but not yet: the room decides which of two speakers looks round first.
                if (room.Time < _speakerStartAt) return Stand(ConversationGazeStandDown.OwnTurn);

                _speakerRemaining = Mathf.Max(0f, tuning.AudienceCheckDuration) * random.Range(0.7f, 1.3f);
                if (_speakerRemaining <= 0f)
                {
                    ClearSpeakerCheck();
                    ArmSpeakerCheck(in tuning, ref random, others, first: false);
                    return Stand(ConversationGazeStandDown.OwnTurn);
                }

                return Attend(booked, room.GetParticipant(booked), in tuning, LookKind.SpeakerCheck);
            }

            _speakerCountdown -= deltaTime;
            if (_speakerCountdown > 0f) return Stand(ConversationGazeStandDown.OwnTurn);

            int pick = PickSpeakerCheckTarget(in room, in self, in tuning, ref random);
            if (pick < 0)
            {
                ArmSpeakerCheck(in tuning, ref random, others, first: false);
                return Stand(ConversationGazeStandDown.OwnTurn);
            }

            ConversationParticipant target = room.GetParticipant(pick);
            _speakerKey = target.Key;
            _speakerStartAt = BookMoment(
                pick, room.LaneFor(target.Key), self.Key,
                PulseEventId(SpeakerCheckChannel, room.Time), 1f,
                in room, in self, in tuning, ref random, out _);
            return Stand(ConversationGazeStandDown.OwnTurn);
        }

        /// <summary>How many people this character could actually look at, the player included.</summary>
        private int CountReachableOthers(in ConversationRoomSnapshot room, in ConversationGazeSelf self)
        {
            int others = 0;

            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key) continue;
                if (!_eligible[i] || !ModeAllows(self.Mode, participant.Key)) continue;

                others++;
            }

            return others;
        }

        /// <summary>
        ///     Who a speaker looks round at: anybody but the person it is addressing, whoever
        ///     holds the floor, and the player.
        /// </summary>
        /// <remarks>
        ///     The player is excluded because a character's look at the player is what its
        ///     dialogue state is already for. This check exists to reach the people that row
        ///     cannot name.
        /// </remarks>
        private int PickSpeakerCheckTarget(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random)
        {
            int addressee = FloorAddresseeKey(in room);
            int chosen = -1;
            float total = 0f;

            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key || participant.IsPlayer) continue;
                if (participant.Key == addressee || participant.Key == room.FloorKey) continue;
                if (!_eligible[i] || !ModeAllows(self.Mode, participant.Key)) continue;
                // A speaker lends this beat its eyes and nothing else, so somebody it could only
                // reach by turning is not somebody it can look round at.
                if (!WithinGlanceReach(i, in tuning)) continue;

                float weight = 1f / (1f + _distance[i]);
                total += weight;
                if (random.Range(0f, total) <= weight) chosen = i;
            }

            return chosen;
        }

        /// <summary>Sets the wait before the speaker's next look round the room.</summary>
        /// <param name="tuning">Authored cadence.</param>
        /// <param name="random">This character's deterministic stream.</param>
        /// <param name="others">How big the audience is, which is what shortens the wait.</param>
        /// <param name="first">Whether this is the first check of this turn, which the phase spreads.</param>
        private void ArmSpeakerCheck(
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random,
            int others,
            bool first)
        {
            _speakerArmed = true;
            float interval = random.Range(tuning.AudienceCheckIntervalMin, tuning.AudienceCheckIntervalMax) *
                             Mathf.Pow(SpeakerCheckGroupScale, Mathf.Max(0, others - 2));
            _speakerCountdown = Mathf.Max(SpeakerCheckMinIntervalSeconds, interval);
            if (first) _speakerCountdown += _phase * PhaseSpreadSeconds;
        }

        /// <summary>Ends whatever look round the room is running or booked.</summary>
        private void ClearSpeakerCheck()
        {
            _speakerKey = ConversationRoomModel.NobodyKey;
            _speakerStartAt = 0f;
            _speakerRemaining = 0f;
        }

        // ── The interruption reflex ──────────────────────────────────────────

        /// <summary>
        ///     Somebody has started talking over the person who holds the floor. The room will not
        ///     hand them the turn until they have kept it up, and it is right not to — but a
        ///     person does not wait for that verdict before looking. The eyes go, and only the
        ///     eyes; the head follows when the floor actually moves, which is the moment the
        ///     ordinary attention model takes the decision back.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Who counts as a challenger is the room's rule, mirrored rather than invented: a
        ///         floor holder who is still talking is only ever challenged by the player, so two
        ///         characters overlapping does not start a staring contest. Half of the floor rule's
        ///         own patience passes before the eyes move, which is what keeps a "mm-hmm" from
        ///         moving anything at all.
        ///     </para>
        ///     <para>
        ///         The one person who reaches this seam without being an interrupter is the
        ///         holder whose promotion this listener has booked but not yet felt — the look is
        ///         held on them so the eyes do not snap back to the previous speaker for the few
        ///         tenths of a second the head still has to wait. That is the ordinary turn to
        ///         whoever now holds the floor, arriving early, and it is issued as
        ///         <see cref="LookKind.PromotionCatchUp" />: same eyes-ahead shape, conversational
        ///         tempo. Only a genuine interrupter — somebody the room has <i>not</i> given the
        ///         floor — moves at a reflex's speed.
        ///     </para>
        /// </remarks>
        private ConversationGazeState ApplyInterruptionReflex(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            in ConversationGazeState decision)
        {
            // Only an ordinary look is interrupted: a glance is already somewhere else on
            // purpose, and a character attending nobody has nothing to flick away from.
            if (!decision.Active) return decision;
            if (decision.Look != ConversationGazeLook.Attention &&
                decision.Look != ConversationGazeLook.EyesOnly)
                return decision;
            if (room.FloorKey == ConversationRoomModel.NobodyKey) return decision;

            float sustain = tuning.InterruptionSeconds * ReflexSustainFraction;
            int challenger = -1;
            bool catchUp = false;
            float earliestOnset = float.PositiveInfinity;

            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key || !participant.IsSpeaking) continue;
                // The floor holder is not an interrupter — unless this listener's eyes went to
                // them as one and its head has not caught up yet. Dropping the reflex the frame
                // the floor moved sent the eyes back to the previous speaker for the few tenths
                // of a second the promotion booking still had to run, then out again: a flick
                // nobody decided. The reflex holds until the head's own moment arrives.
                bool isHolder = participant.Key == room.FloorKey;
                bool promotionPending = _slots[i].HasPending && _slots[i].PendingIsPromotion;
                if (isHolder && !promotionPending) continue;
                // A holder still talking loses the floor only to the player — but that is a rule
                // about challengers, and a holder whose promotion is pending is not one.
                if (!isHolder && room.FloorHolderSpeaking && participant.Key != ConversationRoomModel.PlayerKey)
                    continue;
                if (!_eligible[i] || !ModeAllows(self.Mode, participant.Key)) continue;
                if (!ReactionElapsed(i, room.Time)) continue;
                if (room.Time - participant.SpeechOnsetTime < sustain) continue;
                if (participant.SpeechOnsetTime >= earliestOnset) continue;

                earliestOnset = participant.SpeechOnsetTime;
                challenger = i;
                // Whoever ends up chosen is either somebody talking OVER the floor holder, or
                // the holder themself with this listener's head still to catch up. The two are
                // the same seam and opposite events, so which one it was travels to the look.
                catchUp = isHolder;
            }

            if (challenger < 0) return decision;

            ConversationParticipant target = room.GetParticipant(challenger);
            int attended = decision.Focus == ConversationGazeFocus.Player
                ? ConversationRoomModel.PlayerKey
                : decision.CharacterKey;
            if (attended == target.Key) return decision;

            // Even a reflex is this listener's own: booked through the room with a short
            // latency, so three pairs of eyes do not flick to the interrupter on one frame.
            if (_slots[challenger].ReflexAt <= 0f)
            {
                float latency = ReactionLatency(in tuning, _distance[challenger], Mathf.Abs(_yaw[challenger]), ref _reflexRandom) *
                                ReflexLatencyScale;
                _slots[challenger].ReflexWantedAt = room.Time + latency;
                _slots[challenger].ReflexLane = room.LaneFor(target.Key);
                _slots[challenger].ReflexEvent = ReactionEventId(target) ^ ReflexEventSalt;
            }

            // Re-resolved every tick: the room spaces listeners by the rank of their wanted
            // times, so a slot can move as the others book the same flick. Spaced in the lane
            // the person flicked at owns, at the room's full separation: a pair of eyes arriving
            // on a face beside another pair reads as one cue moving both, and it makes no
            // difference to that which of them was a reflex.
            _slots[challenger].ReflexAt = room.ReserveReaction(
                _slots[challenger].ReflexLane, self.Key, _slots[challenger].ReflexEvent,
                _slots[challenger].ReflexWantedAt, tuning.MinSeparationSeconds);

            if (room.Time < _slots[challenger].ReflexAt) return decision;

            return Attend(challenger, target, in tuning, catchUp ? LookKind.PromotionCatchUp : LookKind.Reflex);
        }

        /// <summary>Keeps reflex event ids apart from the onset ids they derive from.</summary>
        private const int ReflexEventSalt = 0x5A5A5A;

        // ── The decision ─────────────────────────────────────────────────────

        /// <summary>
        ///     Who this tick's look goes to: the eligible person holding the most of this
        ///     character's attention, unless a check is running or nobody is worth it any more.
        /// </summary>
        private ConversationGazeState Decide(
            in ConversationRoomSnapshot room,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            ref DeterministicEmbodimentRandom random)
        {
            if (_pulseRemaining > 0f)
            {
                int pulseIndex = IndexOf(_pulseKey);
                if (pulseIndex >= 0 && _eligible[pulseIndex])
                    return Attend(pulseIndex, room.GetParticipant(pulseIndex), in tuning, LookKind.AudienceCheck);

                _pulseKey = 0;
                _pulseRemaining = 0f;
            }

            int best = -1;
            float bestAttention = 0f;
            int pending = -1;
            float rejectedWeight = -1f;
            ConversationGazeStandDown rejection = ConversationGazeStandDown.NoSpeaker;

            for (int i = 0; i < _count; i++)
            {
                ConversationParticipant participant = room.GetParticipant(i);
                if (participant == null || participant.Key == self.Key) continue;
                if (!ModeAllows(self.Mode, participant.Key)) continue;

                if (!_eligible[i])
                {
                    // Somebody actually talking is the interesting rejection, whatever their
                    // attention has decayed to: "the speaker is too far away" is the answer
                    // somebody asking why nothing happened is looking for.
                    float weight = participant.IsSpeaking ? 1f : _slots[i].Attention;
                    if (weight > rejectedWeight)
                    {
                        rejectedWeight = weight;
                        rejection = _rejection[i];
                    }

                    continue;
                }

                if (participant.IsSpeaking && !ReactionElapsed(i, room.Time)) pending = i;
                if (_slots[i].Attention <= bestAttention) continue;

                bestAttention = _slots[i].Attention;
                best = i;
            }

            int currentIndex = IndexOf(_focusKey);
            bool currentUsable =
                currentIndex >= 0 &&
                _eligible[currentIndex] &&
                ModeAllows(self.Mode, _focusKey) &&
                _slots[currentIndex].Attention >= StandDownAttention;

            // Measured every tick, whatever the branch below decides: a clock that only ran on
            // the frames somebody asked it would count several lapsed leads as one standing one.
            bool driftHasStood = TrackDriftLead(
                best >= 0 && currentUsable && best != currentIndex &&
                bestAttention >= _slots[currentIndex].Attention + SwitchHysteresis
                    ? _slots[best].Key
                    : ConversationRoomModel.NobodyKey,
                room.Time);

            int chosen;
            if (best < 0 || bestAttention < StandDownAttention)
                chosen = currentUsable ? currentIndex : -1;
            else if (!currentUsable)
                chosen = ReactionElapsed(best, room.Time) ? best : -1;
            else if (best == currentIndex)
                chosen = currentIndex;
            else if (ReactionElapsed(best, room.Time) &&
                     (driftHasStood ||
                      (_slots[best].Event && bestAttention > _slots[currentIndex].Attention)))
                chosen = best;
            else
                // Somebody else is rising but has not earned the look yet. People finish looking
                // at one person before they look at the next.
                chosen = currentIndex;

            if (chosen < 0)
            {
                if (pending >= 0) return Stand(ConversationGazeStandDown.Reacting);
                if (rejectedWeight >= StandDownAttention) return Stand(rejection);
                return Stand(ConversationGazeStandDown.NoSpeaker);
            }

            ConversationParticipant target = room.GetParticipant(chosen);
            if (_focusKey != target.Key)
            {
                _focusKey = target.Key;
                _attendingSince = room.Time;
                ArmAudienceCheck(in tuning, ref random, first: true);
            }

            bool eyesOnly =
                _slots[chosen].EyesOnly &&
                target.IsSpeaking &&
                _slots[chosen].HasReaction &&
                _slots[chosen].ReactionEvent == ReactionEventId(target);

            LookKind kind = _slots[chosen].Social
                ? LookKind.SocialIdle
                : eyesOnly
                    ? LookKind.EyesOnly
                    : LookKind.Attention;

            return Attend(chosen, target, in tuning, kind);
        }

        /// <summary>
        ///     What kind of look one <see cref="Attend" /> is making. The scales, the nature and
        ///     whether the body may come round all follow from it, so the difference between a
        ///     glance and a look lives in one switch rather than in four booleans that could
        ///     disagree with each other.
        /// </summary>
        private enum LookKind
        {
            /// <summary>Following whoever is being attended.</summary>
            Attention,

            /// <summary>The same, reached without turning the head.</summary>
            EyesOnly,

            /// <summary>A listener checking the person the speaker is talking to.</summary>
            AudienceCheck,

            /// <summary>A speaker looking round the rest of its audience.</summary>
            SpeakerCheck,

            /// <summary>An idle glance across a quiet room.</summary>
            SocialIdle,

            /// <summary>The eyes going to somebody talking over the floor holder.</summary>
            Reflex,

            /// <summary>
            ///     The eyes staying on somebody the room has just handed the floor to, while
            ///     this listener's head waits for its own booked moment. It looks exactly like
            ///     the reflex it grew out of and is the opposite event: the reflex is a startle
            ///     at somebody talking out of turn, this is the ordinary turn to the new
            ///     speaker, so it moves at conversational tempo rather than at a reflex's.
            /// </summary>
            PromotionCatchUp
        }

        /// <summary>Builds the overlay for a look at <paramref name="target" />.</summary>
        /// <param name="index">Slot of the person being looked at.</param>
        /// <param name="target">The person being looked at.</param>
        /// <param name="tuning">The character's profile, as numbers this director understands.</param>
        /// <param name="kind">What kind of look this is.</param>
        private ConversationGazeState Attend(
            int index,
            ConversationParticipant target,
            in ConversationGazeTuning tuning,
            LookKind kind)
        {
            float attention = Mathf.Clamp01(_slots[index].Attention);

            // Attention scales the commitment rather than deciding it: somebody the character is
            // half-interested in is watched a little less intently, not a lot less.
            float engagement = tuning.Engagement * (0.85f + 0.15f * attention) * _intensityBias;
            float head = tuning.HeadContribution * _intensityBias;

            switch (kind)
            {
                case LookKind.EyesOnly:
                    head *= EyesOnlyHeadScale;
                    break;
                case LookKind.AudienceCheck:
                case LookKind.SpeakerCheck:
                    engagement *= AudienceCheckEngagementScale;
                    break;
                case LookKind.SocialIdle:
                    // Idle life has its own commitment, authored beside the interval that decides
                    // how often it happens. The Idle policy row commits to nothing at all, so a
                    // glance that inherited the conversation's engagement would be the most
                    // intense thing in a quiet room.
                    engagement = tuning.SocialIdleEngagement * _intensityBias;
                    break;
                case LookKind.Reflex:
                case LookKind.PromotionCatchUp:
                    head *= ReflexHeadScale;
                    break;
            }

            // Noticing somebody walk in is a glance whatever kind of look carried it, so the
            // arrival flag decides this alongside the kind rather than being read afterwards.
            bool glance = kind is LookKind.AudienceCheck or LookKind.SpeakerCheck or LookKind.SocialIdle ||
                          _slots[index].Arrival;
            // Every glance, one share. A ceiling rather than a scale, so a beat that has already
            // lowered the head for its own reasons is never raised back up to meet it.
            if (glance) head = Mathf.Min(head, tuning.HeadContribution * GlanceHeadScale * _intensityBias);

            Current = new ConversationGazeState
            {
                Active = true,
                Focus = target.IsPlayer ? ConversationGazeFocus.Player : ConversationGazeFocus.Character,
                CharacterKey = target.IsPlayer ? 0 : target.Key,
                Look = kind switch
                {
                    LookKind.EyesOnly => ConversationGazeLook.EyesOnly,
                    LookKind.AudienceCheck => ConversationGazeLook.AudienceCheck,
                    LookKind.SpeakerCheck => ConversationGazeLook.SpeakerCheck,
                    LookKind.SocialIdle => ConversationGazeLook.SocialIdle,
                    // The catch-up rides the same seam as the reflex it grew out of: eyes
                    // ahead, head still owed. Only its tempo differs, and that is the nature.
                    LookKind.Reflex or LookKind.PromotionCatchUp => ConversationGazeLook.Reflex,
                    _ => ConversationGazeLook.Attention
                },
                IsHolding = !target.IsSpeaking,
                Engagement = Mathf.Clamp01(engagement),
                HeadContribution = Mathf.Clamp01(head),
                // A glance is eyes and a little neck, and so is a reflex. Neither turns the body.
                AllowBodyTurn = tuning.AllowBodyTurn && kind is LookKind.Attention or LookKind.EyesOnly,
                AversionStrength = Mathf.Clamp01(tuning.AversionStrength),
                Nature = kind == LookKind.Reflex
                    ? GazeLookNature.Reflex
                    : glance
                        ? GazeLookNature.Glance
                        : GazeLookNature.Attention,
                Reason = ConversationGazeStandDown.None
            };
            return Current;
        }

        /// <summary>Stands attention down, keeping the focus only while a reaction is still in flight.</summary>
        private ConversationGazeState Stand(ConversationGazeStandDown reason)
        {
            if (reason != ConversationGazeStandDown.Reacting)
            {
                _focusKey = 0;
                _pulseKey = 0;
                _pulseRemaining = 0f;
            }

            Current = ConversationGazeState.StoodDown(reason);
            return Current;
        }

        // ── Small answers ────────────────────────────────────────────────────

        /// <summary>
        ///     Notes who is currently out-scoring the person being looked at by more than the
        ///     hysteresis, and answers whether they have been doing it for
        ///     <see cref="DriftHoldSeconds" />.
        /// </summary>
        /// <param name="key">Whoever holds that lead, or nobody.</param>
        /// <param name="now">Room time.</param>
        private bool TrackDriftLead(int key, float now)
        {
            if (key != _driftKey)
            {
                _driftKey = key;
                _driftSince = now;
            }

            return key != ConversationRoomModel.NobodyKey && now - _driftSince >= DriftHoldSeconds;
        }

        /// <summary>
        ///     Whether a look this character has already decided not to turn its body for can
        ///     actually reach <paramref name="index" /> — the neck's comfortable travel plus the
        ///     band the eyes may rest in. See <see cref="ConversationGazeTuning.GlanceReachDegrees" />.
        /// </summary>
        /// <param name="index">Slot being considered.</param>
        /// <param name="tuning">The character's comfort angles.</param>
        private bool WithinGlanceReach(int index, in ConversationGazeTuning tuning) =>
            Mathf.Abs(_yaw[index]) <= tuning.GlanceReachDegrees;

        /// <summary>Whether the product-level switch lets this character follow <paramref name="key" />.</summary>
        private static bool ModeAllows(GazeSpeakerAttention mode, int key) =>
            key == ConversationRoomModel.PlayerKey
                ? mode is GazeSpeakerAttention.Player or GazeSpeakerAttention.Anyone
                : mode is GazeSpeakerAttention.Characters or GazeSpeakerAttention.Anyone;

        /// <summary>
        ///     Who the floor holder is talking to. A character only ever answers the player, and
        ///     a player turn points at whoever the room was told they are addressing — read from
        ///     the floor rather than from the participant so it survives the pauses inside a turn.
        /// </summary>
        private static int FloorAddresseeKey(in ConversationRoomSnapshot room)
        {
            if (room.FloorKey == ConversationRoomModel.NobodyKey) return ConversationRoomModel.NobodyKey;
            if (room.FloorKey != ConversationRoomModel.PlayerKey) return ConversationRoomModel.PlayerKey;

            ConversationParticipant player = room.Player;
            return player != null ? player.AddresseeKey : ConversationRoomModel.NobodyKey;
        }

        /// <summary>
        ///     Identity of one run of speech, agreed on by everybody because it is derived only
        ///     from the shared snapshot. That agreement is what lets the room keep two listeners'
        ///     reactions to the <i>same</i> onset apart.
        /// </summary>
        private static int ReactionEventId(ConversationParticipant speaker) =>
            (speaker.Key * 397) ^ Mathf.RoundToInt(speaker.SpeechOnsetTime * 1000f);

        /// <summary>Signed horizontal angle from <paramref name="forward" /> to <paramref name="toTarget" />.</summary>
        private static float SignedYaw(Vector3 forward, Vector3 toTarget)
        {
            forward.y = 0f;
            toTarget.y = 0f;
            if (forward.sqrMagnitude <= 1e-6f || toTarget.sqrMagnitude <= 1e-6f) return 0f;

            return Vector3.SignedAngle(forward, toTarget, Vector3.up);
        }

        /// <summary>Slot index of <paramref name="key" />, or -1.</summary>
        private int IndexOf(int key)
        {
            if (key == ConversationRoomModel.NobodyKey) return -1;

            for (int i = 0; i < _count; i++)
                if (_slots[i].Key == key)
                    return i;

            return -1;
        }
    }
}
