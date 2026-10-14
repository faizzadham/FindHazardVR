using Convai.Domain.Models;

namespace Convai.Infrastructure.Networking
{
    /// <summary>
    ///     Represents the local player's identity, transcription callbacks and reported microphone mute state.
    ///     Extends <see cref="IConvaiPlayerEvents" /> to receive transcription callbacks.
    /// </summary>
    public interface IPlayerSession : IConvaiPlayerEvents
    {
        /// <summary>Gets the unique identifier for the local player.</summary>
        public string PlayerId { get; }

        /// <summary>Gets the display name for the local player.</summary>
        public string PlayerName { get; }

        /// <summary>Gets whether the microphone is currently muted.</summary>
        public bool IsMicMuted { get; }

        /// <summary>Records the mute state reported by room audio control; does not start or stop capture.</summary>
        /// <param name="mute">True to mute; false to unmute.</param>
        public void SetMicMuted(bool mute);
    }

    internal interface IPlayerTypedTranscriptSink
    {
        void PublishTypedText(string transcript, string messageId, SpeakerInfo speakerInfo = default);
    }
}
