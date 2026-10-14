namespace Convai.Domain.Models
{
    /// <summary>
    ///     How far the voice has come through one segment of character output, as reported by the backend.
    /// </summary>
    public enum TranscriptSpokenStatus
    {
        /// <summary>
        ///     The backend reported a status this SDK does not recognise, or sent progress without a status.
        /// </summary>
        Unknown = 0,

        /// <summary>The segment was announced before synthesis started; none of it has been heard yet.</summary>
        New = 1,

        /// <summary>The voice is inside the segment; <see cref="TranscriptSpokenProgress.SpokenText" /> grows word by word.</summary>
        InProgress = 2,

        /// <summary>The last word of the segment has been spoken.</summary>
        Completed = 3
    }

    /// <summary>
    ///     Word-level speaking progress within one segment of character output.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Reported when the character's voice provider returns word timings. The segment text is split at
    ///         the word being spoken: <see cref="SpokenText" /> is what the listener has heard so far, including
    ///         that word, and <see cref="RemainingText" /> is what is still to come.
    ///     </para>
    ///     <para>
    ///         Progress describes playback, not text. The event that carries it repeats the full segment text,
    ///         so progress never changes transcript history; it only tells presentation code how far the voice
    ///         has come.
    ///     </para>
    /// </remarks>
    public readonly struct TranscriptSpokenProgress
    {
        private readonly string _spokenText;
        private readonly string _remainingText;

        /// <summary>
        ///     Creates a progress report. The texts read as empty strings when <c>null</c>, including on
        ///     <c>default</c>.
        /// </summary>
        public TranscriptSpokenProgress(TranscriptSpokenStatus status, string spokenText, string remainingText)
        {
            Status = status;
            _spokenText = spokenText;
            _remainingText = remainingText;
        }

        /// <summary>Where the voice is within the segment.</summary>
        public TranscriptSpokenStatus Status { get; }

        /// <summary>Text of the segment spoken so far, including the word being spoken.</summary>
        public string SpokenText => _spokenText ?? string.Empty;

        /// <summary>Text of the segment not yet spoken.</summary>
        public string RemainingText => _remainingText ?? string.Empty;
    }
}
