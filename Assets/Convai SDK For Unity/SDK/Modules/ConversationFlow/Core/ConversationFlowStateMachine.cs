using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;

namespace Convai.Modules.ConversationFlow.Core
{
    /// <summary>
    ///     POCO dialogue-phase state machine. Takes per-frame input + dt, returns the current
    ///     <see cref="DialogueStateReading" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The state machine is deliberately framework-free so it is unit-testable in
    ///         edit mode. The authoritative transition rules are:
    ///     </para>
    ///     <list type="number">
    ///         <item><description>Character not ready -> <see cref="DialogueState.Idle" />.</description></item>
    ///         <item>
    ///             <description>
    ///                 Player starts speaking -> <see cref="DialogueState.Listening" />.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Player stops speaking (no pending turn) -> <see cref="DialogueState.Attending" />
    ///                 for <see cref="ConversationFlowTimings.AttendingGracePeriod" />.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Player finalizes transcript -> <see cref="DialogueState.Thinking" />
    ///                 until the character begins speaking or the max hold elapses.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Character speech begins (transport or LipSync) ->
    ///                 <see cref="DialogueState.Speaking" />.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Character speech ends, interrupted ->
    ///                 <see cref="DialogueState.Interrupted" /> -> <see cref="DialogueState.Attending" />.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Character speech ends, normal -> <see cref="DialogueState.Settling" /> ->
    ///                 <see cref="DialogueState.Attending" /> -> <see cref="DialogueState.Idle" />
    ///                 after an inactivity timeout.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         <see cref="DialogueState.Reacting" /> is reserved for explicit one-shot signals
    ///         (emotion spike, barge-in acknowledgement) and is driven by <see cref="Pulse" />
    ///         rather than automatic derivation.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationFlowStateMachine
    {
        private DialogueState _primary = DialogueState.Idle;
        private DialogueState _blendTo = DialogueState.Idle;
        private float _blendWeight;
        private float _timeInPrimary;

        private float _thinkingEntryTime;
        private float _attendingEntryTime;
        private float _settlingEntryTime;
        private float _interruptedEntryTime;
        private float _reactingRemaining;
        private float _elapsedSeconds;
        private float _lastEngagementTime = float.NegativeInfinity;
        private float _unaddressedSeconds;
        private float _lastDeltaTime;

        /// <summary>Current authoritative reading. Updated each call to <see cref="Tick" />.</summary>
        public DialogueStateReading Current { get; private set; } = DialogueStateReading.Idle;

        /// <summary>
        ///     Raised when <see cref="DialogueStateReading.Primary" /> changes (NOT on blend
        ///     progress). Consumers use this to trigger one-shot reactions.
        /// </summary>
        public event System.Action<DialogueStateReading> Changed;

        /// <summary>
        ///     Advances the state machine by <paramref name="deltaTime" /> and returns the
        ///     resulting reading.
        /// </summary>
        public DialogueStateReading Tick(
            in ConversationFlowInputs inputs,
            in ConversationFlowTimings timings,
            float deltaTime)
        {
            if (deltaTime < 0f) deltaTime = 0f;
            _lastDeltaTime = deltaTime;
            _elapsedSeconds += deltaTime;
            UpdateEngagementClock(inputs);

            DialogueState desired = Derive(inputs, timings);

            // A character that is not ready is not in the conversation at all, and rule 1 says so
            // unconditionally. A reaction beat requested before it joined — or left in flight when
            // it dropped out — must not be the one thing that overrides that, so it is dropped
            // rather than held: by the time the character is ready, whatever it was reacting to has
            // passed.
            if (!inputs.IsCharacterReady)
                _reactingRemaining = 0f;

            if (_reactingRemaining > 0f)
            {
                _reactingRemaining -= deltaTime;
                if (_reactingRemaining <= 0f)
                {
                    _reactingRemaining = 0f;
                }
                else
                {
                    desired = DialogueState.Reacting;
                }
            }

            if (desired != _primary)
                TransitionTo(desired, timings);

            AdvanceBlend(deltaTime, timings.TransitionDuration);
            _timeInPrimary += deltaTime;

            float energy = ComputeEnergy(inputs, timings);

            Current = new DialogueStateReading(
                _primary,
                _blendTo,
                _blendWeight,
                _timeInPrimary,
                energy);

            return Current;
        }

        /// <summary>
        ///     Forces a short pulse through <see cref="DialogueState.Reacting" /> for
        ///     <paramref name="duration" /> seconds. The state machine returns to its derived
        ///     state automatically afterwards.
        /// </summary>
        public void Pulse(DialogueState state, float duration)
        {
            if (state != DialogueState.Reacting) return;
            if (duration <= 0f) return;
            _reactingRemaining = duration;
        }

        /// <summary>Resets the state machine; used on character shutdown.</summary>
        public void Reset()
        {
            _primary = DialogueState.Idle;
            _blendTo = DialogueState.Idle;
            _blendWeight = 0f;
            _timeInPrimary = 0f;
            _reactingRemaining = 0f;
            _thinkingEntryTime = 0f;
            _attendingEntryTime = 0f;
            _settlingEntryTime = 0f;
            _interruptedEntryTime = 0f;
            _elapsedSeconds = 0f;
            _lastEngagementTime = float.NegativeInfinity;
            _unaddressedSeconds = 0f;
            _lastDeltaTime = 0f;
            Current = DialogueStateReading.Idle;
        }

        private DialogueState Derive(in ConversationFlowInputs inputs, in ConversationFlowTimings timings)
        {
            if (!inputs.IsCharacterReady)
                return DialogueState.Idle;

            if (inputs.IsCharacterSpeaking && ShouldHoldThinkingBeforeSpeech(inputs, timings))
            {
                return DialogueState.Thinking;
            }

            // One arbitrated verdict, not an OR over sources. The OR that used to stand here could
            // only ever extend a turn: every source had to fall silent before the character stopped
            // performing, so the slowest one set the pace — and the slowest one was a message from
            // the service that is sent after the sound has already stopped. Whether the character is
            // speaking is decided upstream by SpeechBoundaryArbiter, which lets whichever source
            // knows first say so, in both directions.
            if (inputs.IsCharacterSpeaking)
                return DialogueState.Speaking;

            // Listening and Thinking are the two states that mean "this turn is mine", so they are
            // gated on being the character the player is addressing. The aggregator already refuses
            // to report another character's turn, but the rule belongs here too: it is the invariant
            // the whole multi-character model rests on, and a caller that got it wrong would
            // otherwise have every character in the room listening at once.
            if (inputs.IsAddressedByPlayer && inputs.IsPlayerSpeaking)
                return DialogueState.Listening;

            // The speaking turn just ended, and the character winds down from it.
            //
            // This used to be gated on the turn-completed message, because the service was the only
            // thing that could end a turn. Now the voice can end it too, and that message arrives
            // seconds later — so keying the settle beat on the message meant the character skipped
            // its wind-down entirely and then settled, with its sigh, long after it had finished
            // talking. The beat belongs to leaving Speaking, whichever source said so.
            //
            // An interruption is still allowed in from Settling: a barge-in that lands a frame or
            // two after the voice already went quiet must still reach the freeze, not be swallowed.
            // A turn-completed that is merely late is not in this condition at all — by then the
            // character has already wound down, and the message is stale news rather than a second
            // ending.
            if (_primary == DialogueState.Speaking ||
                _primary == DialogueState.Interrupted ||
                inputs.WasRecentlyInterrupted)
            {
                if (inputs.WasRecentlyInterrupted || _primary == DialogueState.Interrupted)
                {
                    // First entry: _interruptedEntryTime has not been set yet (TransitionTo records
                    // it after Derive returns). Return immediately so the freeze window is measured
                    // from the actual transition instant, not from state-machine construction.
                    if (_primary != DialogueState.Interrupted)
                        return DialogueState.Interrupted;

                    if (_elapsedSeconds - _interruptedEntryTime < timings.InterruptedFreezeDuration)
                        return DialogueState.Interrupted;
                    return ResolveEngagedRestState(timings);
                }

                return DialogueState.Settling;
            }

            // Pending player turn without active player speech - Thinking window.
            if (inputs.IsAddressedByPlayer && inputs.HasPendingPlayerTurn)
            {
                // If we've already timed out from Thinking and transitioned to Attending,
                // don't bounce back to Thinking just because pendingTurn is still true.
                if (_primary == DialogueState.Attending)
                {
                    float heldTime = _elapsedSeconds - _thinkingEntryTime;
                    if (heldTime >= timings.ThinkingMaxHold)
                        return DialogueState.Attending;
                }

                if (_primary != DialogueState.Thinking)
                    return DialogueState.Thinking;

                float held = _elapsedSeconds - _thinkingEntryTime;
                if (held < timings.ThinkingMinHold)
                    return DialogueState.Thinking;

                if (held >= timings.ThinkingMaxHold)
                    return ResolveEngagedRestState(timings);
                return DialogueState.Thinking;
            }

            // Transient Attending after the player finished speaking, followed by an engaged
            // hold before the character is allowed to cool back to Idle.
            if (_primary == DialogueState.Listening || _primary == DialogueState.Attending)
            {
                if (_elapsedSeconds - _attendingEntryTime < timings.AttendingGracePeriod)
                    return DialogueState.Attending;
                return ResolveEngagedRestState(timings);
            }

            // Finish off any trailing Settling window.
            if (_primary == DialogueState.Settling)
            {
                if (_elapsedSeconds - _settlingEntryTime < timings.SettlingDuration)
                    return DialogueState.Settling;
                return ResolveEngagedRestState(timings);
            }

            return ResolveEngagedRestState(timings);
        }

        private void TransitionTo(DialogueState next, in ConversationFlowTimings timings)
        {
            DialogueState previous = _primary;
            _primary = next;
            _blendTo = next;
            _blendWeight = timings.TransitionDuration > 0f ? 0f : 1f;
            _timeInPrimary = 0f;

            switch (next)
            {
                case DialogueState.Thinking:
                    _thinkingEntryTime = _elapsedSeconds;
                    break;
                case DialogueState.Attending:
                    _attendingEntryTime = _elapsedSeconds;
                    break;
                case DialogueState.Settling:
                    _settlingEntryTime = _elapsedSeconds;
                    break;
                case DialogueState.Interrupted:
                    _interruptedEntryTime = _elapsedSeconds;
                    break;
            }

            if (previous != next)
                Changed?.Invoke(new DialogueStateReading(next, next, _blendWeight, 0f, 0f));
        }

        private void AdvanceBlend(float deltaTime, float transitionDuration)
        {
            if (transitionDuration <= 0f)
            {
                _blendWeight = 1f;
                return;
            }

            _blendWeight += deltaTime / transitionDuration;
            if (_blendWeight > 1f) _blendWeight = 1f;
        }

        private void UpdateEngagementClock(in ConversationFlowInputs inputs)
        {
            if (!inputs.IsCharacterReady)
            {
                _lastEngagementTime = float.NegativeInfinity;
                return;
            }

            // The player is talking to somebody else. That is not the absence of a signal, it is a
            // signal: the conversation has moved, and this character is out of it now.
            //
            // Letting the idle timeout handle it was wrong in a way that showed. The delay is a
            // minute — long on purpose, so a character you are still with does not cool between
            // sentences — and every character the conversation had ever touched sat in an engaged
            // stance for that whole minute afterwards. Walking away from one to talk to another
            // left the first leaning in behind the player's back.
            //
            // But wiping the clock the instant it happens is just as wrong, and worse to watch.
            // Who the player is addressing is re-evaluated many times a second and moves through
            // pending, looked-at and room answers, so it can flicker for a frame or two at a turn
            // boundary. Wiping on that flicker dropped the character straight to Idle — where the
            // player is not even a candidate — so it looked away into its idle life and snapped
            // back a moment later, right after finishing a sentence.
            //
            // So the clock stops being refreshed and ages out over a short grace instead. Becoming
            // addressed is still instant, because taking the turn must not wait for a debounce;
            // only letting go is damped. Its own turn also still finishes: Speaking derives from
            // the character's audio, not from this clock.
            if (!inputs.IsAddressedByPlayer)
            {
                _unaddressedSeconds += _lastDeltaTime;
                return;
            }

            _unaddressedSeconds = 0f;

            if (inputs.IsPlayerSpeaking ||
                inputs.HasPendingPlayerTurn ||
                // Local evidence that somebody is starting to talk to it. It counts as engagement
                // and nothing more, which is the whole point: engagement lifts the character out of
                // Idle into Attending — "somebody is addressing me" — and Attending is the state
                // this beat actually is. It can never reach Listening from here, because Listening
                // means a turn is under way and only the service gets to say that.
                inputs.IsPlayerLocallyActive ||
                // Doing what you were asked is engagement. Without this a character sent to walk
                // somewhere decays to Idle mid-errand, and every behaviour keyed off the state —
                // most visibly gaze — stops treating the player as present.
                inputs.IsPerformingAction ||
                inputs.IsCharacterSpeaking ||
                inputs.IsLipSyncSpeaking ||
                inputs.TurnJustCompleted ||
                inputs.WasRecentlyInterrupted ||
                _primary == DialogueState.Listening ||
                _primary == DialogueState.Thinking ||
                _primary == DialogueState.Speaking ||
                _primary == DialogueState.Settling ||
                _primary == DialogueState.Interrupted)
            {
                _lastEngagementTime = _elapsedSeconds;
            }
        }

        private bool ShouldHoldThinkingBeforeSpeech(
            in ConversationFlowInputs inputs,
            in ConversationFlowTimings timings)
        {
            if (_primary == DialogueState.Thinking)
                return _elapsedSeconds - _thinkingEntryTime < timings.ThinkingMinHold;

            return inputs.IsAddressedByPlayer && inputs.HasPendingPlayerTurn;
        }

        private DialogueState ResolveEngagedRestState(in ConversationFlowTimings timings)
        {
            if (!HasRecentEngagement(timings))
                return DialogueState.Idle;

            return DialogueState.Attending;
        }

        /// <summary>
        ///     How long the player may be addressing somebody else before this character gives up
        ///     its engaged stance.
        /// </summary>
        /// <remarks>
        ///     A debounce, not a feel setting. Long enough to outlast the flicker of a re-evaluated
        ///     addressee at a turn boundary, short enough that a character the player has genuinely
        ///     walked away from cools within about a second rather than waiting out the idle delay.
        /// </remarks>
        private const float UnaddressedGraceSeconds = 0.6f;

        private bool HasRecentEngagement(in ConversationFlowTimings timings)
        {
            if (timings.IdleReturnDelay <= 0f)
                return false;

            if (float.IsNegativeInfinity(_lastEngagementTime))
                return false;

            if (_unaddressedSeconds >= UnaddressedGraceSeconds)
                return false;

            return _elapsedSeconds - _lastEngagementTime < timings.IdleReturnDelay;
        }

        private float ComputeEnergy(in ConversationFlowInputs inputs, in ConversationFlowTimings timings)
        {
            return _primary switch
            {
                DialogueState.Idle => 0.1f,
                DialogueState.Listening => 0.35f,
                DialogueState.Attending => 0.4f,
                DialogueState.Thinking => 0.25f,
                DialogueState.Speaking => timings.SpeakingBaseEnergy,
                DialogueState.Reacting => 0.9f,
                DialogueState.Interrupted => 0.2f,
                DialogueState.Settling => 0.3f,
                _ => 0.1f
            };
        }
    }
}
