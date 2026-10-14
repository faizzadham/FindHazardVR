namespace Convai.Runtime.Room
{
    /// <summary>
    ///     The client's own ceiling on how many characters one room may hold.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is not the real limit. The account's plan decides that, the service enforces it,
    ///         and a room past it is refused with a message naming the plan. This number exists so a
    ///         roster that is obviously wrong — a spawner in a loop, a list built from the wrong
    ///         collection — is caught where it was built rather than as a wire failure a hundred
    ///         characters later.
    ///     </para>
    ///     <para>
    ///         It is checked on both paths into a roster, because they are two different mistakes
    ///         with the same result: the connect path is asked for everything at once, and the live
    ///         path adds one at a time until the same number is reached.
    ///     </para>
    /// </remarks>
    internal static class MultiCharacterRoomLimits
    {
        /// <summary>The largest roster this client will build.</summary>
        internal const int MaxCharacters = 50;

        /// <summary>Says a roster is too large, and says whose limit is the real one.</summary>
        internal static string DescribeOverflow(int requested) =>
            $"A Convai room supports at most {MaxCharacters} characters, and this one asks for " +
            $"{requested}. The Convai plan for this API key may allow fewer still; use " +
            "Convai Manager > Characters Joining the Room to send only the characters this conversation needs.";
    }
}
