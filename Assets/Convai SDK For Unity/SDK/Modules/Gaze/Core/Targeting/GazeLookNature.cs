namespace Convai.Modules.Gaze.Core.Targeting
{
    /// <summary>
    ///     Why the character is looking at something — the intent behind a look, as opposed to
    ///     where the look is aimed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this has to travel.</b> The targeting stage knows a great deal about a look
    ///         that the actuators need and could not otherwise recover: whether it is a passing
    ///         glance or a decision to give somebody attention. Without it every consumer has to
    ///         guess from the geometry, and each one guessed differently — the movement tempo asked
    ///         "is there a target at all", the chest asked "does the rig have a torso", and the
    ///         head's share of a glance was a compile-time constant no personality could reach. The
    ///         result was a character whose casual glance across a room was executed as the same
    ///         movement as turning to face the person it was talking to.
    ///     </para>
    ///     <para>
    ///         Deliberately coarse. The distinction is categorical — it is about what the character
    ///         means by the look — and a continuous "importance" scalar would invite callers to
    ///         invent gradations nobody can see. The <i>amount</i> is carried separately, as the
    ///         relevance that won the target.
    ///     </para>
    /// </remarks>
    internal enum GazeLookNature
    {
        /// <summary>
        ///     A passing look. Idle curiosity, noticing another character across the room, checking
        ///     something while walking. The eyes do most of it, the head comes along a little, and
        ///     the chest stays out of it entirely — a glance that turns the body is not a glance.
        ///     Unhurried, because nothing is asking for the character's attention.
        /// </summary>
        Glance = 0,

        /// <summary>
        ///     Ordinary attention: the person being spoken to, a target being followed, a scripted
        ///     look. The character means it, so the whole ladder is available and the movement runs
        ///     at conversational tempo. This includes looking at the player — somebody entering
        ///     conversational range is not an emergency.
        /// </summary>
        Attention = 1,

        /// <summary>
        ///     Something demanded attention now: a startle, or re-acquiring after a camera cut or a
        ///     teleport. Reserved for genuine reflexes; everything voluntary is
        ///     <see cref="Attention" />.
        /// </summary>
        Reflex = 2
    }
}
