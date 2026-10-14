using Convai.Runtime.Animation;

namespace Convai.Modules.ConversationFlow.Core
{
    /// <summary>
    ///     Which kind of evidence the arbiter is currently using to decide whether the character's
    ///     voice is audible. Ordered best-first; reported for diagnostics so the ladder is never
    ///     silent about which rung it is standing on.
    /// </summary>
    internal enum SpeechEvidenceRung
    {
        /// <summary>No local evidence is available; the service's verdict is all there is.</summary>
        ServiceOnly = 0,

        /// <summary>The remote audio track is reporting whether the character's voice is audible.</summary>
        AudiblePlayback = 1,

        /// <summary>
        ///     Lip-sync playback is on stage as well: it knows when a response has shown its last
        ///     frame, which is the end of the response and not merely a pause in it.
        /// </summary>
        LipSyncPlayback = 2
    }

    /// <summary>What ended the most recent speaking turn.</summary>
    internal enum SpeechBoundarySource
    {
        /// <summary>No turn has ended yet.</summary>
        None = 0,

        /// <summary>The voice stopping ended it, ahead of the service.</summary>
        Voice = 1,

        /// <summary>The service ended it.</summary>
        Service = 2,

        /// <summary>Lip-sync playback reported the response finished.</summary>
        Playback = 3
    }

    /// <summary>
    ///     Decides <i>when</i> a character is speaking, from the service's verdict and from local
    ///     evidence that its voice is actually coming out of the speakers.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The service owns what a turn <i>means</i> — who holds it, whether it was interrupted,
    ///         whether the conversation has moved on. It does not own when the sound stops, and it
    ///         cannot: that instant happens on this machine, and the message saying so arrives after
    ///         it. Every performance layered on top of speaking — gesture, posture, breath, where
    ///         the character looks — was waiting for that message.
    ///     </para>
    ///     <para>
    ///         So the rule is <b>whichever says so first</b>, in both directions. A turn starts when
    ///         either source says it started, and ends when either says it ended. A late message
    ///         then arrives as a no-op instead of as a delay, and an early one still cuts the
    ///         performance short exactly as it does today — which is what has to happen when the
    ///         player interrupts, or when the service cancels a response whose audio is still in
    ///         flight.
    ///     </para>
    ///     <para>
    ///         <b>An ending is confirmed before it is reported.</b> Local silence must hold for the
    ///         rung's confirmation window before the arbiter calls the turn over, and evidence
    ///         returning inside that window cancels the pending ending without anything downstream
    ///         ever seeing it. No half-ended turn leaves this class, so no consumer has to cope with
    ///         one, and nothing downstream needs to learn a new state.
    ///     </para>
    ///     <para>
    ///         There is deliberately one local source rather than a ladder of them. LipSync also has
    ///         an opinion about when a response ends, but on the send-ahead path it derives that
    ///         opinion from the service's message — so preferring it would have meant waiting on the
    ///         very thing this class exists to get ahead of, while discarding the one source that
    ///         independently knows. Rank evidence by what it knows on its own, not by what it is
    ///         called.
    ///     </para>
    ///     <para>
    ///         Pure C# and deterministic: no wall clock, no randomness, no allocation. All time is
    ///         accumulated from the supplied <c>deltaTime</c>, so an identical input sequence always
    ///         produces an identical output sequence.
    ///     </para>
    /// </remarks>
    internal sealed class SpeechBoundaryArbiter
    {
        private bool _isSpeaking;
        private bool _voiceAudible;
        private bool _serviceSpeaking;

        private bool _endPending;
        private float _pendingEndElapsed;

        // How long local evidence has been reporting silence while the service still says the
        // character is speaking. This is the delay the whole seam exists to remove, so it is
        // measured rather than assumed — and measured even when local endings are switched off, so
        // the diagnostic tells the truth about what the setting is costing or saving.
        private bool _measuringServiceLag;
        private float _serviceLagElapsed;

        // Set when local evidence ends a turn the service still believes is running. Without it the
        // very next step would see a "speaking" service flag against a not-speaking arbiter and
        // start the turn over again, one frame after ending it. The service has to release the flag
        // and raise it again before it may start anything; local evidence is unaffected, so a real
        // resumption still starts a turn immediately.
        private bool _awaitingServiceRelease;

        /// <summary>Whether the character is speaking, all evidence considered.</summary>
        public bool IsSpeaking => _isSpeaking;

        /// <summary>The best local evidence available on the most recent step.</summary>
        public SpeechEvidenceRung Rung { get; private set; } = SpeechEvidenceRung.ServiceOnly;

        /// <summary>What ended the most recent turn. Latched until the next turn ends.</summary>
        public SpeechBoundarySource EndedBy { get; private set; } = SpeechBoundarySource.None;

        /// <summary>
        ///     How long local evidence had already been reporting silence when the most recent turn
        ///     ended, in seconds. Positive whenever local evidence got there first — whether or not
        ///     it was allowed to act on that — and zero when the service was not behind. This is the
        ///     improvement, in the only form worth quoting: a measurement.
        /// </summary>
        public float LastVoiceLeadSeconds { get; private set; }

        /// <summary>
        ///     Live view of <see cref="LastVoiceLeadSeconds" /> for the turn in progress: how long
        ///     local evidence has been silent while the service still says otherwise.
        /// </summary>
        public float CurrentServiceLagSeconds => _measuringServiceLag ? _serviceLagElapsed : 0f;

        /// <summary>Whether an ending is being confirmed right now.</summary>
        public bool IsConfirmingEnd => _endPending;

        /// <summary>
        ///     Advances the arbiter by <paramref name="deltaTime" /> and returns whether the
        ///     character is speaking.
        /// </summary>
        /// <param name="serviceSpeaking">The service's current verdict.</param>
        /// <param name="voiceAudible">
        ///     Whether the character's voice is audible, or <c>null</c> when nothing on this machine
        ///     can say — the character has no audio track to listen to. <c>null</c> is not the same
        ///     as <c>false</c>: <c>false</c> is a verdict of silence and ends turns, <c>null</c> is
        ///     the absence of a witness and changes nothing.
        /// </param>
        /// <param name="config">The hold and the local-ending switch.</param>
        /// <param name="deltaTime">Seconds since the previous step.</param>
        public bool Step(
            bool serviceSpeaking,
            bool? voiceAudible,
            in SpeechBoundaryArbiterConfig config,
            float deltaTime) =>
            Step(serviceSpeaking, voiceAudible, null, in config, deltaTime);

        /// <param name="playback">
        ///     What lip-sync playback says about the response on stage, or <c>null</c> when the
        ///     character has no lip sync. A response whose frames have all been shown after the voice
        ///     stopped is finished — there is no pause-versus-ending ambiguity left to wait out, so
        ///     the turn ends at once. A response that still has frames ahead of the playhead is
        ///     still going, however quiet the voice is right now.
        /// </param>
        public bool Step(
            bool serviceSpeaking,
            bool? voiceAudible,
            SpeechPlaybackReading? playback,
            in SpeechBoundaryArbiterConfig config,
            float deltaTime)
        {
            if (deltaTime < 0f) deltaTime = 0f;

            bool canHear = voiceAudible.HasValue;
            bool canSeePlayback = playback.HasValue && playback.Value.HasResponse;
            Rung = canSeePlayback ? SpeechEvidenceRung.LipSyncPlayback
                : canHear ? SpeechEvidenceRung.AudiblePlayback
                : SpeechEvidenceRung.ServiceOnly;

            bool serviceWasSpeaking = _serviceSpeaking;
            _serviceSpeaking = serviceSpeaking;
            _voiceAudible = canHear && voiceAudible.Value;

            if (!_serviceSpeaking)
                _awaitingServiceRelease = false;

            TickServiceLag(canHear, deltaTime);

            if (!_isSpeaking)
                return TryStart(serviceWasSpeaking);

            // The service ending a turn is authoritative and immediate. It is how an interruption
            // reaches the body, and how a cancelled response stops a performance whose audio has
            // not drained yet — neither may wait on a confirmation window.
            if (!_serviceSpeaking)
            {
                EndTurn(SpeechBoundarySource.Service);
                return false;
            }

            if (canSeePlayback && config.EndOnVoice)
            {
                if (playback.Value.Finished)
                {
                    // The last frame has been shown and the voice is gone. This is the end, known
                    // before the silence detector's own hold and long before the service's message.
                    EndTurn(SpeechBoundarySource.Playback);
                    _awaitingServiceRelease = true;
                    return false;
                }

                if (playback.Value.RemainingSeconds > 0f)
                {
                    // Frames are still ahead of the playhead: whatever the voice is doing, the
                    // response is not over. A quiet stretch here is a pause inside it.
                    _endPending = false;
                    _pendingEndElapsed = 0f;
                    return true;
                }
            }

            bool voiceMayEnd = canHear && config.EndOnVoice && !_voiceAudible;
            if (!voiceMayEnd)
            {
                // Either the voice is still audible, there is no witness, or ending on the voice is
                // switched off. Any pending confirmation is abandoned: the character carried on.
                _endPending = false;
                _pendingEndElapsed = 0f;
                return true;
            }

            if (!_endPending)
            {
                _endPending = true;
                _pendingEndElapsed = 0f;
            }

            _pendingEndElapsed += deltaTime;
            if (_pendingEndElapsed < config.VoiceEndHoldSeconds)
                return true;

            EndTurn(SpeechBoundarySource.Voice);
            _awaitingServiceRelease = true;
            return false;
        }

        /// <summary>Resets to the not-speaking state. Used on character shutdown.</summary>
        public void Reset()
        {
            _isSpeaking = false;
            _voiceAudible = false;
            _serviceSpeaking = false;
            _endPending = false;
            _pendingEndElapsed = 0f;
            _measuringServiceLag = false;
            _serviceLagElapsed = 0f;
            _awaitingServiceRelease = false;
            LastVoiceLeadSeconds = 0f;
            Rung = SpeechEvidenceRung.ServiceOnly;
            EndedBy = SpeechBoundarySource.None;
        }

        private void TickServiceLag(bool canHear, float deltaTime)
        {
            if (canHear && !_voiceAudible && _serviceSpeaking && _isSpeaking)
            {
                if (!_measuringServiceLag)
                {
                    _measuringServiceLag = true;
                    _serviceLagElapsed = 0f;
                }

                _serviceLagElapsed += deltaTime;
                return;
            }

            // The voice came back: whatever silence was accumulating was a pause inside the turn,
            // not a lead on its ending, so it is discarded outright.
            if (_voiceAudible)
            {
                _measuringServiceLag = false;
                _serviceLagElapsed = 0f;
                return;
            }

            // The service caught up. Stop accumulating but KEEP the total: this is the frame
            // EndTurn reports on, and zeroing here destroyed the measurement a step before it was
            // read — the arbiter would end the turn correctly and then report a lead of zero,
            // which is the one outcome that makes the whole change look like it did nothing.
            _measuringServiceLag = false;
        }

        private bool TryStart(bool serviceWasSpeaking)
        {
            // Audible audio is unambiguous: something is coming out of this character's mouth.
            if (_voiceAudible)
            {
                Begin();
                return true;
            }

            // A service flag that has been continuously raised since local evidence ended the last
            // turn is stale, not a new turn. Only a fresh rising edge counts.
            if (_serviceSpeaking && !(_awaitingServiceRelease && serviceWasSpeaking))
            {
                Begin();
                return true;
            }

            return false;
        }

        private void Begin()
        {
            _isSpeaking = true;
            _awaitingServiceRelease = false;
            _endPending = false;
            _pendingEndElapsed = 0f;
            _measuringServiceLag = false;
            _serviceLagElapsed = 0f;
        }

        private void EndTurn(SpeechBoundarySource source)
        {
            // Reported whoever ended the turn: when the service got there first this is zero, and
            // when it did not, this is the delay that was avoided — or, with local endings switched
            // off, the delay that was accepted. Both are worth knowing.
            LastVoiceLeadSeconds = _serviceLagElapsed;
            _isSpeaking = false;
            _endPending = false;
            _pendingEndElapsed = 0f;
            _measuringServiceLag = false;
            _serviceLagElapsed = 0f;
            EndedBy = source;
        }
    }

    /// <summary>Tuning for <see cref="SpeechBoundaryArbiter" />.</summary>
    internal readonly struct SpeechBoundaryArbiterConfig
    {
        /// <summary>
        ///     Whether hearing the voice stop may end a turn ahead of the service. When false the
        ///     arbiter still starts turns on the voice and still measures how far behind the service
        ///     is, but the ending waits for the service exactly as it did before this seam existed.
        /// </summary>
        public bool EndOnVoice { get; }

        /// <summary>How long the character keeps performing after its voice stops.</summary>
        /// <remarks>
        ///     One number doing two jobs, because operationally they are the same wait: long enough
        ///     that a pause between sentences is not mistaken for the end of an answer, short enough
        ///     that the performance does not outlive the voice. It is also the stagger that keeps an
        ///     ending from reading as a switch being thrown — the mouth closes on its own shorter
        ///     fade, and the body follows it out.
        /// </remarks>
        public float VoiceEndHoldSeconds { get; }

        public SpeechBoundaryArbiterConfig(bool endOnVoice, float voiceEndHoldSeconds)
        {
            EndOnVoice = endOnVoice;
            VoiceEndHoldSeconds = voiceEndHoldSeconds < 0f ? 0f : voiceEndHoldSeconds;
        }

        /// <summary>Defaults: end on the voice, after a hold that clears a sentence gap.</summary>
        public static SpeechBoundaryArbiterConfig Default =>
            new(endOnVoice: true, voiceEndHoldSeconds: 0.4f);
    }
}
