namespace Convai.Modules.Gaze.Components
{
    /// <summary>
    ///     Whether the character should treat a gaze anchor as somebody's face.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Looking at a person is not looking at a point. The gaze moves between their eyes and
    ///         their mouth, a degree or two either side of centre, and holds each for a second or
    ///         two — which is what makes a character read as looking <i>at</i> somebody rather than
    ///         through them.
    ///     </para>
    ///     <para>
    ///         Aimed at something that is not a face, that same behaviour is just inaccuracy: the
    ///         character sits one to two degrees off the thing it is supposed to be looking at. On a
    ///         desktop game the player is a camera — a viewpoint with no eyes and no mouth — and
    ///         looking beside it is exactly the "why isn't it looking at me" that this setting
    ///         exists to answer.
    ///     </para>
    /// </remarks>
    public enum GazeAnchorFacePresence
    {
        /// <summary>
        ///     Decide from what the anchor is: an anchor assigned by hand stands in for the player,
        ///     so it is treated as a face; a bare camera the SDK found for itself is a viewpoint and
        ///     is aimed at exactly.
        /// </summary>
        Auto = 0,

        /// <summary>
        ///     There is a face here. Correct in VR, where the camera sits between the player's
        ///     eyes, and for any anchor parented to a visible head.
        /// </summary>
        Face = 1,

        /// <summary>Aim exactly at the anchor. Correct for a disembodied desktop camera.</summary>
        Point = 2
    }
}
