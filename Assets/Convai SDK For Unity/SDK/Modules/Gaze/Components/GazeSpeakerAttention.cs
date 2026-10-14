namespace Convai.Modules.Gaze.Components
{
    /// <summary>
    ///     Whether a character turns to whoever is currently speaking in the room, and to whom.
    ///     This is the "everyone looks at the person talking" behaviour of a group conversation:
    ///     it applies only while the character is <b>not</b> in its own turn, so it never competes
    ///     with the character's own conversational gaze.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The mode is the product-level switch; how the look is performed (how committed it
    ///         is, how far the character will turn, how long it waits before reacting) is tuned in
    ///         <c>ConvaiGazeProfile</c>'s Conversation Attention group, so one shared profile
    ///         governs a whole cast.
    ///     </para>
    ///     <para>
    ///         Attending another character requires that character to publish itself through a
    ///         <c>CharacterGazeTargetProvider</c> ("Character Target") — the same component that
    ///         already makes characters lookable. Attending the player uses the character's
    ///         player anchor. Distance, angle, and line-of-sight gates apply in every mode, so a
    ///         speaker across the level or behind the character's back is ignored rather than
    ///         producing an unnatural turn.
    ///     </para>
    /// </remarks>
    public enum GazeSpeakerAttention
    {
        /// <summary>
        ///     Never follow another participant's turn. The character's gaze is driven only by its
        ///     own conversation and its ambient life — the behaviour of releases before this
        ///     setting existed.
        /// </summary>
        Off = 0,

        /// <summary>
        ///     Turn to the player while the player is speaking, but ignore other characters'
        ///     turns. For scenes where the cast attends the user but does not converse among
        ///     itself.
        /// </summary>
        Player = 1,

        /// <summary>
        ///     Turn to another character while that character is speaking, but never to the
        ///     player on the player's turn. For crowds and background conversations that should
        ///     not react to the user.
        /// </summary>
        Characters = 2,

        /// <summary>
        ///     Turn to whoever holds the floor, player or character (default). The player barging
        ///     in over a speaking character takes the floor immediately.
        /// </summary>
        Anyone = 3
    }
}
