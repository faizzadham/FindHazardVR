using Convai.Domain.Embodiment.Semantics;

namespace Convai.Modules.Gaze.Core.Policy
{
    /// <summary>Holds a final transcript until cognition confirms or rejects its speaking turn.</summary>
    internal struct PendingTurnTranscript
    {
        private bool _pending;
        private int _wordCount;

        public void Latch(int wordCount)
        {
            _pending = true;
            _wordCount = wordCount;
        }

        // Only cognition can reject a pulse: Expression may run before cognition has observed
        // the source's new state. A pending debounce transition is still an undecided turn.
        public void ObserveState(DialogueState state, bool hasPendingState)
        {
            if (!hasPendingState && state != DialogueState.Thinking && state != DialogueState.Speaking)
                Clear();
        }

        public bool TryConsume(DialogueState state, out int wordCount)
        {
            wordCount = 0;
            if (!_pending || state != DialogueState.Speaking) return false;
            wordCount = _wordCount;
            Clear();
            return true;
        }

        public void Clear()
        {
            _pending = false;
            _wordCount = 0;
        }
    }
}
