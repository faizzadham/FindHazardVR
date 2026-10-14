namespace Convai.Modules.ConversationFlow.Core
{
    /// <summary>
    ///     Immutable snapshot of the raw per-frame signals the conversation-flow state
    ///     machine uses to decide transitions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ConversationFlowSignalAggregator" /> folds domain events into this
    ///         shape so the state machine itself (pure POCO) can be edit-mode tested without
    ///         a Unity scene.
    ///     </para>
    /// </remarks>
    public readonly struct ConversationFlowInputs
    {
        /// <summary>Character has signaled ready-to-interact via <c>CharacterReady</c>.</summary>
        public bool IsCharacterReady { get; }

        /// <summary>VAD reports the player is currently producing voice.</summary>
        public bool IsPlayerSpeaking { get; }

        /// <summary>
        ///     At least one final player transcript has been committed since the last
        ///     character turn started; cleared when the character begins speaking.
        /// </summary>
        public bool HasPendingPlayerTurn { get; }

        /// <summary>
        ///     Whether the character is performing a speaking turn, all evidence considered.
        /// </summary>
        /// <remarks>
        ///     The arbitrated verdict from <c>SpeechBoundaryArbiter</c>, not a raw transport flag:
        ///     the service's message and hearing the voice stop, whichever knows first. This is the
        ///     one the state machine acts on, and the only one a consumer should treat as "is it
        ///     talking".
        /// </remarks>
        public bool IsCharacterSpeaking { get; }

        /// <summary>LipSync is actually playing speech frames this moment.</summary>
        /// <remarks>
        ///     A presentation signal: it stays true through the visual fade-out and is smoothed by
        ///     the facial compositor's ramp, so it lags the voice by design. Deliberately <b>not</b>
        ///     part of the speaking decision — that reads <see cref="IsCharacterSpeaking" />, which
        ///     is arbitrated. It still counts as engagement in the idle-return clock: a character
        ///     whose mouth is moving is plainly still in the conversation, and being slightly
        ///     generous about that is harmless where being slightly generous about <i>speaking</i>
        ///     was the whole defect.
        /// </remarks>
        public bool IsLipSyncSpeaking { get; }

        /// <summary>A turn-completed signal with <c>wasInterrupted=true</c> arrived recently.</summary>
        public bool WasRecentlyInterrupted { get; }

        /// <summary>A turn-completed signal arrived in the current frame.</summary>
        public bool TurnJustCompleted { get; }

        /// <summary>
        ///     The character is carrying out something it was asked to do. Counts as engagement:
        ///     running an errand for the player is being with the player, even in silence.
        /// </summary>
        public bool IsPerformingAction { get; }

        /// <summary>
        ///     Something on this machine thinks the player is starting to address the character —
        ///     the open microphone heard them, or they pressed to talk. A guess, arriving before the
        ///     service's own verdict, and never enough to take a turn on.
        /// </summary>
        public bool IsPlayerLocallyActive { get; }

        /// <summary>
        ///     Whether the player is addressing <i>this</i> character. Always true in a room holding
        ///     one; in a multi-character room, true for the addressed character only.
        /// </summary>
        public bool IsAddressedByPlayer { get; }

        public ConversationFlowInputs(
            bool isCharacterReady,
            bool isPlayerSpeaking,
            bool hasPendingPlayerTurn,
            bool isCharacterSpeaking,
            bool isLipSyncSpeaking,
            bool wasRecentlyInterrupted,
            bool turnJustCompleted,
            bool isPerformingAction = false,
            bool isPlayerLocallyActive = false,
            bool isAddressedByPlayer = true)
        {
            IsPerformingAction = isPerformingAction;
            IsPlayerLocallyActive = isPlayerLocallyActive;
            IsAddressedByPlayer = isAddressedByPlayer;
            IsCharacterReady = isCharacterReady;
            IsPlayerSpeaking = isPlayerSpeaking;
            HasPendingPlayerTurn = hasPendingPlayerTurn;
            IsCharacterSpeaking = isCharacterSpeaking;
            IsLipSyncSpeaking = isLipSyncSpeaking;
            WasRecentlyInterrupted = wasRecentlyInterrupted;
            TurnJustCompleted = turnJustCompleted;
        }
    }
}
