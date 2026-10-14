using System;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>Settable <see cref="IConversationFlowSource" /> for unit tests.</summary>
    public sealed class FakeConversationFlowSource : IConversationFlowSource
    {
        public event Action<DialogueStateReading> Changed;

        public DialogueStateReading Current { get; private set; } = DialogueStateReading.Idle;

        public void SetState(DialogueState state, float energy = 0f)
        {
            Current = new DialogueStateReading(state, state, 0f, 0f, energy);
            Changed?.Invoke(Current);
        }

        public void SetReading(DialogueStateReading reading)
        {
            Current = reading;
            if (reading.Primary != DialogueStateReading.Idle.Primary)
                Changed?.Invoke(reading);
        }
    }
}
