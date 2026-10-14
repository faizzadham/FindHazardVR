namespace Convai.Runtime.Animation
{
    /// <summary>
    ///     What the lip-sync playback of a character can say about the response it is showing:
    ///     whether one is on stage, whether its frames have all arrived, how much of it is left,
    ///     and whether it has finished on local evidence.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Speech animation arrives seconds ahead of the sound it belongs to, so the machine
    ///         that plays it is the one place that knows where a response <i>ends</i> before the
    ///         sound gets there. Measured on a live room the last frames of a response landed
    ///         1–1.9 s before they were due, and a response's frames ran out 0.2–0.8 s before the
    ///         audio track's own silence detector reported the voice gone. Body performance that
    ///         waits for the service's end-of-turn message, or even for the silence detector, is
    ///         therefore late by construction; a reading from here lets it begin winding down as a
    ///         person would — with the last words, not after them.
    ///     </para>
    ///     <para>
    ///         Every field is a fact about frames and sound, not a verdict about the conversation.
    ///         Whether the turn is over remains the conversation flow's decision.
    ///     </para>
    /// </remarks>
    internal interface ISpeechPlaybackWitness
    {
        /// <summary>The current reading. Cheap; may be read every frame.</summary>
        SpeechPlaybackReading Current { get; }
    }

    /// <summary>One frame's reading from an <see cref="ISpeechPlaybackWitness" />.</summary>
    internal readonly struct SpeechPlaybackReading
    {
        /// <summary>Reading for a character with nothing on stage.</summary>
        public static readonly SpeechPlaybackReading None = default;

        public SpeechPlaybackReading(
            bool hasResponse,
            bool inputSettled,
            float remainingSeconds,
            bool finished)
        {
            HasResponse = hasResponse;
            InputSettled = inputSettled;
            RemainingSeconds = remainingSeconds;
            Finished = finished;
        }

        /// <summary>A response is on stage: frames have arrived and have not been retired.</summary>
        public bool HasResponse { get; }

        /// <summary>
        ///     No new frames have arrived for long enough that the response is taken as fully
        ///     delivered, so <see cref="RemainingSeconds" /> is how much of it is left to play.
        /// </summary>
        public bool InputSettled { get; }

        /// <summary>
        ///     Seconds of animation still ahead of the playhead. Meaningful only while
        ///     <see cref="HasResponse" />; an end that is not yet known is reported as remaining
        ///     time that keeps growing with the frames.
        /// </summary>
        public float RemainingSeconds { get; }

        /// <summary>
        ///     The voice has stopped and every frame the response had has been shown. This is the
        ///     local end of the response; the service's own confirmation may still be seconds away.
        /// </summary>
        public bool Finished { get; }

        /// <summary>
        ///     The end of the response is known and lies within <paramref name="leadSeconds" />
        ///     of the playhead, or has already passed.
        /// </summary>
        public bool EndsWithin(float leadSeconds) =>
            HasResponse && (Finished || (InputSettled && RemainingSeconds <= leadSeconds));
    }
}
