namespace Convai.Runtime.Room
{
    /// <summary>
    ///     What to do about evidence that a character is alive, when the service has not said so yet.
    /// </summary>
    internal enum CharacterReadyEvidenceVerdict
    {
        /// <summary>Not a candidate — no membership, or it is already ready or failed.</summary>
        Ignore,

        /// <summary>First evidence. Give the real readiness signal time to arrive.</summary>
        ArmRecovery,

        /// <summary>The grace elapsed and the real signal never came. Recover.</summary>
        RecoverNow
    }

    /// <summary>
    ///     How much a piece of evidence proves.
    /// </summary>
    /// <remarks>
    ///     Treating every signal alike is what made the timeout below load-bearing. It is not: a
    ///     character that is producing speech is ready whatever the handshake has managed to say,
    ///     while a subscribed audio track only means a participant joined — which happens before
    ///     readiness on every normal connection.
    /// </remarks>
    internal enum CharacterReadyEvidence
    {
        /// <summary>
        ///     The participant is in the room. Routinely true before the service announces
        ///     readiness, so on its own it proves nothing about whether the character can hear.
        /// </summary>
        Presence,

        /// <summary>
        ///     The character is producing speech — text, lip-sync frames, a speech-start. Nothing
        ///     that is not ready does this, so it needs no grace and gets none.
        /// </summary>
        Speech
    }

    /// <summary>
    ///     Decides when evidence of a character's presence — a remote audio track, a speech chunk,
    ///     lip-sync data — may stand in for the service's own readiness signal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The recovery this policy governs exists for a real failure: a readiness signal that
    ///         never arrives, leaving a character that is plainly talking marked as still starting.
    ///         What it could not do was tell <b>late</b> from <b>missing</b>. It treated the first
    ///         scrap of evidence as proof and marked the character ready immediately.
    ///     </para>
    ///     <para>
    ///         For a character joining a room that is already connected, that is wrong every single
    ///         time: its audio track is subscribed before the service sends readiness, so the SDK
    ///         announced a character the service was not yet routing to. Anything the player said in
    ///         that window went nowhere, and nothing logged it, because as far as the SDK was
    ///         concerned the character was ready. Measured once at 1.44 s of head start.
    ///     </para>
    ///     <para>
    ///         So evidence now <i>arms</i> a recovery instead of completing one. The real signal
    ///         disarms it; only silence past the grace completes it. Kept as a plain decision over
    ///         plain inputs so the timing rule is testable without a room or a clock.
    ///     </para>
    /// </remarks>
    internal static class CharacterReadyRecoveryPolicy
    {
        /// <summary>
        ///     How long a readiness signal is waited for when the only evidence is that the
        ///     participant turned up.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Deliberately not tuned, because it no longer decides anything important.
        ///         <see cref="CharacterReadyEvidence.Speech" /> recovers on the spot, so the case
        ///         this timeout governs is the narrow one where the service both failed to announce
        ///         a character <i>and</i> that character never says anything. There is nothing to
        ///         race: waiting longer costs a character sitting in <c>Preparing</c> that was going
        ///         to sit there silently anyway.
        ///     </para>
        ///     <para>
        ///         An earlier version made this the whole mechanism, set from a single 1.44 s
        ///         observation of the gap between an audio track and the readiness signal. A number
        ///         derived from one sample deciding whether the player can be heard is not a design;
        ///         separating the evidence is.
        ///     </para>
        /// </remarks>
        internal const double DefaultGraceSeconds = 5d;

        internal static CharacterReadyEvidenceVerdict Evaluate(
            bool hasMembership,
            CharacterRoomStatus status,
            bool alreadyArmed,
            double secondsSinceArmed,
            CharacterReadyEvidence evidence = CharacterReadyEvidence.Presence,
            double graceSeconds = DefaultGraceSeconds)
        {
            // Only a character the service has not finished starting can be recovered. One that is
            // ready needs nothing, and one that failed must stay failed — recovering it would hide
            // the failure behind a character that never answers.
            if (!hasMembership || status != CharacterRoomStatus.Starting)
                return CharacterReadyEvidenceVerdict.Ignore;

            // A character that is speaking is ready by definition, and making the player wait out a
            // timeout while they can hear it talking would be the same lie in the other direction.
            if (evidence == CharacterReadyEvidence.Speech)
                return CharacterReadyEvidenceVerdict.RecoverNow;

            if (!alreadyArmed)
                return CharacterReadyEvidenceVerdict.ArmRecovery;

            return secondsSinceArmed >= graceSeconds
                ? CharacterReadyEvidenceVerdict.RecoverNow
                : CharacterReadyEvidenceVerdict.ArmRecovery;
        }
    }
}
