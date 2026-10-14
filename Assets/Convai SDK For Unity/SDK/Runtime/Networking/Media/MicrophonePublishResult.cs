namespace Convai.Runtime.Networking.Media
{
    /// <summary>
    ///     Outcome of a microphone publish, carrying the session error code that explains a failure so
    ///     callers can report a denied or missing microphone instead of a generic publish failure.
    /// </summary>
    internal readonly struct MicrophonePublishResult
    {
        private MicrophonePublishResult(bool succeeded, string failureCode, string failureMessage)
        {
            Succeeded = succeeded;
            FailureCode = failureCode;
            FailureMessage = failureMessage;
        }

        /// <summary>True when the microphone track was published.</summary>
        public bool Succeeded { get; }

        /// <summary>
        ///     The <see cref="Convai.Domain.Errors.SessionErrorCodes" /> code that explains the failure, or null
        ///     when the failure has no specific cause.
        /// </summary>
        public string FailureCode { get; }

        /// <summary>A developer-facing explanation of the failure, or null.</summary>
        public string FailureMessage { get; }

        public static MicrophonePublishResult Success { get; } = new(true, null, null);

        public static MicrophonePublishResult Failure(string failureCode = null, string failureMessage = null) =>
            new(false, failureCode, failureMessage);
    }
}
