using System;

namespace Convai.Domain.DomainEvents.Runtime
{
    /// <summary>
    ///     What noticed the player locally.
    /// </summary>
    public enum LocalPlayerActivitySource
    {
        /// <summary>The open microphone is hearing sound loud enough to be someone talking.</summary>
        Microphone = 0,

        /// <summary>The player is holding the push-to-talk control.</summary>
        PushToTalk = 1
    }

    /// <summary>
    ///     Raised on this machine, without waiting for the service, when the player does something
    ///     that means they are starting to address the character.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A hint, never a turn.</b> This is not
    ///         <see cref="PlayerSpeakingStateChanged" />, which is the service's own verdict and is
    ///         what commits a turn, produces a transcript, and moves a character into its listening
    ///         state. This event is a guess made from local evidence, and it exists for one reason:
    ///         the service's verdict costs a network round trip, and a character that only reacts
    ///         when the round trip lands looks like it noticed you late. Treat it as "somebody is
    ///         starting to talk to me" and nothing more — never route a message, commit a turn, or
    ///         bill anything on it.
    ///     </para>
    ///     <para>
    ///         It works in both turn-taking modes, from whichever evidence that mode has.
    ///         Hands-free has an open microphone, so a level gate on the captured audio raises it.
    ///         Push-to-talk has something better and earlier — the player pressing the control is a
    ///         certainty, not a guess — so the press raises it directly, before any sound arrives.
    ///     </para>
    ///     <para>
    ///         The two sources are independent and may overlap; a consumer that cares about "is the
    ///         player active at all" should treat the sources as a set rather than as one flag, the
    ///         way <c>Convai.Modules.ConversationFlow</c> does.
    ///     </para>
    /// </remarks>
    public readonly struct LocalPlayerActivityChanged
    {
        /// <summary>Whether <see cref="Source" /> currently sees the player.</summary>
        public bool IsActive { get; }

        /// <summary>Which local evidence this event is reporting.</summary>
        public LocalPlayerActivitySource Source { get; }

        /// <summary>
        ///     How loud the microphone was, relative to the noise it had settled on, when the level
        ///     gate raised this. Zero for every other source, and for the falling edge.
        /// </summary>
        /// <remarks>
        ///     Useful for a microphone meter. It is a ratio above the measured noise floor rather
        ///     than an absolute level, so it does not track the player's input gain.
        /// </remarks>
        public float Level { get; }

        /// <summary>When the local evidence changed (UTC).</summary>
        public DateTime Timestamp { get; }

        /// <summary>Creates a new <see cref="LocalPlayerActivityChanged" /> event.</summary>
        public LocalPlayerActivityChanged(
            bool isActive,
            LocalPlayerActivitySource source,
            float level,
            DateTime timestamp)
        {
            IsActive = isActive;
            Source = source;
            Level = level;
            Timestamp = timestamp;
        }

        /// <summary>Creates the event with the current UTC timestamp.</summary>
        public static LocalPlayerActivityChanged Create(
            bool isActive,
            LocalPlayerActivitySource source,
            float level = 0f) =>
            new(isActive, source, level, DateTime.UtcNow);
    }
}
