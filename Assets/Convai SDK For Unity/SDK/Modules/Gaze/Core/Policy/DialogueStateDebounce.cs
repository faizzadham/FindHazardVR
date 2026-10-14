using Convai.Domain.Embodiment.Semantics;

namespace Convai.Modules.Gaze.Core.Policy
{
    /// <summary>
    ///     The gaze module's single reading of the conversation's dialogue state. A newly
    ///     reported state is adopted only once it has held for
    ///     <see cref="ConfirmationWindowSeconds" />, so a state that alternates between two
    ///     values on consecutive frames never reaches a gaze consumer at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Dialogue state is authored upstream from several pieces of evidence — service
    ///         events, local speech energy, lip-sync activity — which do not all change on the
    ///         same frame. At an utterance boundary that shows up here as
    ///         <see cref="DialogueState.Speaking" /> and <see cref="DialogueState.Settling" />
    ///         swapping on consecutive frames for a fraction of a second. Read raw, every one of
    ///         those edges is a real decision to gaze: the aversion mode flips, the head's share
    ///         of the shift steps between two rows of the state table, and a body turn in flight
    ///         is retired and re-planned. The character reads as jittering at exactly the moment
    ///         it should be settling.
    ///     </para>
    ///     <para>
    ///         So gaze does not act on a state until it has been asked for consistently. The cost
    ///         is that a genuine transition arrives up to one window late, which is below the
    ///         module's own onset latencies and therefore invisible; the benefit is that a
    ///         boundary which has not made up its mind produces no motion whatsoever.
    ///     </para>
    ///     <para>
    ///         <b>Reflexes are exempt.</b> <see cref="DialogueState.Interrupted" /> and
    ///         <see cref="DialogueState.Reacting" /> are by definition the states whose whole
    ///         value is that they land immediately — a startle that arrives an eighth of a second
    ///         after the interruption is not a startle. They are adopted on the frame they are
    ///         first reported. Leaving one is debounced like anything else.
    ///     </para>
    ///     <para>
    ///         Engine-free and allocation-free: one instance per controller, ticked once per
    ///         frame, no state beyond four fields.
    ///     </para>
    /// </remarks>
    internal sealed class DialogueStateDebounce
    {
        /// <summary>
        ///     How long a newly reported dialogue state must hold before gaze adopts it.
        /// </summary>
        /// <remarks>
        ///     Chosen to sit well above the few-frame disagreement an utterance boundary produces
        ///     (measured in play mode as 0.2-0.3 s of ~15 ms flicker) and well below the shortest
        ///     dialogue beat a viewer can read as a beat. It is deliberately not a profile field:
        ///     this is not a taste control, it is the width of a known upstream race, and a
        ///     character tuned to zero would simply have the defect back.
        /// </remarks>
        internal const float ConfirmationWindowSeconds = 0.12f;

        private DialogueState _current = DialogueState.Idle;
        private DialogueState _pending = DialogueState.Idle;
        private float _pendingElapsed;
        private bool _initialized;

        /// <summary>
        ///     The state gaze is acting on. <see cref="DialogueState.Idle" /> before the first
        ///     <see cref="Tick" /> and after a <see cref="Reset" />.
        /// </summary>
        public DialogueState Current => _current;

        /// <summary>
        ///     Whether a different state is currently being confirmed — the source disagrees with
        ///     <see cref="Current" /> but has not held long enough to be adopted.
        /// </summary>
        public bool HasPendingState => _initialized && _pending != _current;

        /// <summary>
        ///     Advances the confirmation window by <paramref name="deltaTime" /> against the raw
        ///     state reported this frame, and returns the state gaze should act on.
        /// </summary>
        /// <param name="raw">The dialogue state the conversation-flow source reports this frame.</param>
        /// <param name="deltaTime">Seconds since the previous tick. Negative values count as zero.</param>
        /// <returns>
        ///     The adopted state, which is also <see cref="Current" />. The first tick after
        ///     construction or <see cref="Reset" /> adopts <paramref name="raw" /> outright: there
        ///     is nothing to disagree with yet, and opening a fresh binding with a window of
        ///     stale <see cref="DialogueState.Idle" /> would be a defect of its own.
        /// </returns>
        public DialogueState Tick(DialogueState raw, float deltaTime)
        {
            if (!_initialized)
            {
                _initialized = true;
                _current = raw;
                _pending = raw;
                _pendingElapsed = 0f;
                return _current;
            }

            // Already acting on it — or it is a reflex, and waiting would destroy the point of it.
            if (raw == _current || IsReflex(raw))
            {
                _current = raw;
                _pending = raw;
                _pendingElapsed = 0f;
                return _current;
            }

            // A different answer than last frame's candidate restarts the window rather than
            // extending it: "Settling, Speaking, Settling" is not a fifth of a second of Settling.
            if (raw != _pending)
            {
                _pending = raw;
                _pendingElapsed = 0f;
                return _current;
            }

            _pendingElapsed += deltaTime > 0f ? deltaTime : 0f;
            if (_pendingElapsed < ConfirmationWindowSeconds) return _current;

            _current = raw;
            _pendingElapsed = 0f;
            return _current;
        }

        /// <summary>
        ///     Forgets the adopted state and any half-confirmed candidate, so the next
        ///     <see cref="Tick" /> opens a fresh binding. Called when the module goes down.
        /// </summary>
        public void Reset()
        {
            _current = DialogueState.Idle;
            _pending = DialogueState.Idle;
            _pendingElapsed = 0f;
            _initialized = false;
        }

        /// <summary>
        ///     Whether <paramref name="state" /> is a reflex beat that must land on the frame it
        ///     is reported. Pure, so the exemption list is pinned by a test rather than by
        ///     inspection of the caller.
        /// </summary>
        internal static bool IsReflex(DialogueState state) =>
            state == DialogueState.Interrupted || state == DialogueState.Reacting;
    }
}
