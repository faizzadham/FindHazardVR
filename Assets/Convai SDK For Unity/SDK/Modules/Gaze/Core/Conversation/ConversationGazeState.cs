using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Targeting;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>Who a character following a conversation is currently attending.</summary>
    internal enum ConversationGazeFocus
    {
        /// <summary>Nobody — the character is not following anyone else's turn.</summary>
        None = 0,

        /// <summary>The player anchor.</summary>
        Player = 1,

        /// <summary>Another Convai character.</summary>
        Character = 2
    }

    /// <summary>
    ///     Why a character is not following the conversation. Reported to diagnostics so
    ///     "why isn't it looking at whoever is talking" has an answer that names the actual gate.
    /// </summary>
    internal enum ConversationGazeStandDown
    {
        /// <summary>Attending — no stand-down.</summary>
        None = 0,

        /// <summary><c>Attend To Speaker</c> is <c>Off</c>.</summary>
        ModeOff = 1,

        /// <summary>The character is in its own turn; its conversational gaze owns the look.</summary>
        OwnTurn = 2,

        /// <summary>Nobody in the room has enough of this character's attention left to be worth looking at.</summary>
        NoSpeaker = 3,

        /// <summary>The person worth attending is beyond the attention distance.</summary>
        TooFar = 4,

        /// <summary>They are too far off the character's forward and body turns are disallowed.</summary>
        TooWide = 5,

        /// <summary>Somebody holds the floor but the room has no point to look at for them.</summary>
        NoAnchor = 6,

        /// <summary>The reaction latency for this onset has not elapsed yet.</summary>
        Reacting = 7,

        /// <summary>
        ///     A wall stands between the two of them. The character can hear them and knows they
        ///     are talking; it simply cannot see them, so it does not turn.
        /// </summary>
        NoLineOfSight = 8
    }

    /// <summary>
    ///     What kind of look one conversation decision is. The wiring layer applies four of these
    ///     the same way and the fifth quite differently, so the kind travels with the decision
    ///     rather than being inferred from a handful of booleans that could disagree.
    /// </summary>
    internal enum ConversationGazeLook
    {
        /// <summary>Following whoever the character is attending. The ordinary case.</summary>
        Attention = 0,

        /// <summary>
        ///     The same look, reached without turning: the character was already pointed at the
        ///     person who started talking, so its eyes settle and its head stays where it is.
        /// </summary>
        EyesOnly = 1,

        /// <summary>A listener checking the person the speaker is talking to, and coming back.</summary>
        AudienceCheck = 2,

        /// <summary>
        ///     The character holding the floor glancing round the rest of its audience. Its own
        ///     dialogue state still owns how it looks at the person it is talking to; this only
        ///     lends the eyes for the length of the glance.
        /// </summary>
        SpeakerCheck = 3,

        /// <summary>
        ///     A glance between people in a room where nobody is talking — the idle social life
        ///     that used to be a director of its own.
        /// </summary>
        SocialIdle = 4,

        /// <summary>
        ///     The eyes going to somebody talking over the person who holds the floor, before the
        ///     room has decided whether that is an interruption or a noise. Eyes, not head.
        /// </summary>
        Reflex = 5
    }

    /// <summary>
    ///     This character, as the conversation director needs to see it: who it is in the room,
    ///     where its eyes are, and where they are already pointing.
    /// </summary>
    /// <remarks>
    ///     The last two fields are the ones that make a reaction human. Somebody already looking
    ///     at the person who just started talking does not "react" at all — their eyes are there,
    ///     and at most they settle. A listener facing the other way turns, and turning is what
    ///     takes a couple of hundred milliseconds. Without knowing where the character is already
    ///     aimed, both cases have to be given the same latency, and one of them is then wrong.
    /// </remarks>
    internal readonly struct ConversationGazeSelf
    {
        /// <summary>This character's key in <see cref="ConversationRoomModel" />.</summary>
        public readonly int Key;

        /// <summary>Where this character's eyes are, in world space.</summary>
        public readonly Vector3 HeadPoint;

        /// <summary>Which way this character is facing, in world space.</summary>
        public readonly Vector3 Forward;

        /// <summary>
        ///     The character is in its own conversational turn. Attention layers on top of Idle
        ///     only — a character being spoken to already has a policy row that governs its gaze,
        ///     and overriding it would make every listener behave exactly like the addressee.
        /// </summary>
        public readonly bool InOwnTurn;

        /// <summary>The product-level switch, already narrowed by whatever the wiring layer cannot offer.</summary>
        public readonly GazeSpeakerAttention Mode;

        /// <summary>
        ///     Signed horizontal angle from this character's forward to whatever it is already
        ///     looking at. Signed, so a target 30° left and one 30° right are not mistaken for the
        ///     same direction.
        /// </summary>
        public readonly float CurrentAimYawDegrees;

        /// <summary>Room key of whoever it is already looking at, or 0 when that is not a person.</summary>
        public readonly int CurrentAimTargetKey;

        /// <summary>
        ///     The people in the room this character cannot see, or <c>null</c> when nothing is
        ///     checking — which is the default, and means everybody is visible.
        /// </summary>
        public readonly ConversationOcclusionSet Occluded;

        /// <summary>Creates the character's own side of a conversation tick.</summary>
        /// <param name="key">This character's room key.</param>
        /// <param name="headPoint">World-space eye point.</param>
        /// <param name="forward">World-space facing.</param>
        /// <param name="inOwnTurn">Whether the character is in its own turn.</param>
        /// <param name="mode">The effective <c>Attend To Speaker</c> mode.</param>
        /// <param name="currentAimYawDegrees">Signed yaw to the current look target.</param>
        /// <param name="currentAimTargetKey">Room key of the current look target, or 0.</param>
        /// <param name="turnEnding">Whether this character's own turn is wrapping up.</param>
        /// <param name="occluded">Who this character cannot see, or <c>null</c> for nobody.</param>
        /// <param name="scriptedLookActive">Whether a scripted look already owns the character's gaze.</param>
        public ConversationGazeSelf(
            int key,
            Vector3 headPoint,
            Vector3 forward,
            bool inOwnTurn,
            GazeSpeakerAttention mode,
            float currentAimYawDegrees = 0f,
            int currentAimTargetKey = 0,
            bool turnEnding = false,
            ConversationOcclusionSet occluded = null,
            bool scriptedLookActive = false)
        {
            ScriptedLookActive = scriptedLookActive;
            Key = key;
            HeadPoint = headPoint;
            Forward = forward;
            InOwnTurn = inOwnTurn;
            Mode = mode;
            CurrentAimYawDegrees = currentAimYawDegrees;
            CurrentAimTargetKey = currentAimTargetKey;
            TurnEnding = turnEnding;
            Occluded = occluded;
        }

        /// <summary>
        ///     Whether a wall stands between this character and <paramref name="key" />. False
        ///     whenever nothing is checking, so the question is safe to ask unconditionally.
        /// </summary>
        /// <param name="key">Room key of the person being asked about.</param>
        public bool CannotSee(int key) => Occluded != null && Occluded.IsOccluded(key);

        /// <summary>
        ///     Whether that wall has stood there for at least <paramref name="seconds" />. The
        ///     question somebody already being looked at is asked, so a ray broken for one
        ///     measurement by a shoulder or a doorway does not end a look that is going fine.
        /// </summary>
        /// <param name="key">Room key of the person being asked about.</param>
        /// <param name="now">Room time.</param>
        /// <param name="seconds">How long the occlusion has to have lasted to count.</param>
        public bool CannotSeeFor(int key, float now, float seconds) =>
            Occluded != null && Occluded.IsOccludedFor(key, now, seconds);

        /// <summary>
        ///     A scripted look — a curiosity glance, an authored <c>LookAt</c>, an action's look
        ///     point — already owns this character's gaze. The conversation still decides who is
        ///     worth attending, but its own idle beats stand aside: two systems glancing at two
        ///     people on two clocks is the pile-up the room model exists to end, and the scripted
        ///     stack wins the arbitration anyway, so the glance the director booked would only be
        ///     spent unseen.
        /// </summary>
        public readonly bool ScriptedLookActive;

        /// <summary>
        ///     True while this character's own turn is wrapping up — its final transcript has
        ///     arrived, or it is settling after speech. A speaker holds its addressee through
        ///     that; it does not look round the room.
        /// </summary>
        public readonly bool TurnEnding;
    }

    /// <summary>This tick's conversation decision — a policy overlay, not a target.</summary>
    internal struct ConversationGazeState
    {
        /// <summary>Whether the character is following somebody else's turn this tick.</summary>
        public bool Active;

        /// <summary>Who is being attended.</summary>
        public ConversationGazeFocus Focus;

        /// <summary>Room key of the attended character when <see cref="Focus" /> is a character; else 0.</summary>
        public int CharacterKey;

        /// <summary>What kind of look this is, which is what tells the wiring layer how to apply it.</summary>
        public ConversationGazeLook Look;

        /// <summary>
        ///     The look is a brief check on the person being spoken to rather than on the speaker.
        ///     Eyes and a little neck, never the body.
        /// </summary>
        public bool IsAudienceCheck => Look == ConversationGazeLook.AudienceCheck;

        /// <summary>
        ///     Whether this decision is the character following somebody else's turn, as opposed
        ///     to a beat that merely borrows the same seam: an idle glance across a quiet room, or
        ///     a speaker looking round its own audience. Diagnostics ask "is it in the
        ///     conversation", and both of those answer no.
        /// </summary>
        public bool IsAttending =>
            Active &&
            Look != ConversationGazeLook.SocialIdle &&
            Look != ConversationGazeLook.SpeakerCheck;

        /// <summary>
        ///     The look is carried rather than driven by live speech: whoever is being attended
        ///     has stopped talking but still has the room's attention. People finish looking at
        ///     one person before they look at the next.
        /// </summary>
        public bool IsHolding;

        /// <summary>Engagement this tick's look asks for.</summary>
        public float Engagement;

        /// <summary>Head participation this tick's look asks for.</summary>
        public float HeadContribution;

        /// <summary>Whether this look may bring the body round.</summary>
        public bool AllowBodyTurn;

        /// <summary>Natural aversion to run while attending.</summary>
        public float AversionStrength;

        /// <summary>
        ///     What kind of look this is. A pulse — an audience check, or noticing somebody who
        ///     has just arrived — is a <see cref="GazeLookNature.Glance" />; following a speaker
        ///     is <see cref="GazeLookNature.Attention" />.
        /// </summary>
        public GazeLookNature Nature;

        /// <summary>Why the character is not attending (<see cref="ConversationGazeStandDown.None" /> while it is).</summary>
        public ConversationGazeStandDown Reason;

        /// <summary>An inactive decision carrying the gate that produced it.</summary>
        /// <param name="reason">Which gate stood the character down.</param>
        public static ConversationGazeState StoodDown(ConversationGazeStandDown reason) => new()
        {
            Active = false,
            Focus = ConversationGazeFocus.None,
            Nature = GazeLookNature.Attention,
            Reason = reason
        };
    }
}
