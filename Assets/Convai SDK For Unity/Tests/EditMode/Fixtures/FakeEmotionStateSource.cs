using System.Collections.Generic;
using System.Collections.ObjectModel;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>Settable <see cref="IEmotionStateSource" /> for unit tests.</summary>
    public sealed class FakeEmotionStateSource : IEmotionStateSource
    {
        public EmotionReading Current { get; set; } = EmotionReading.Neutral;

        public void SetEmotion(string label, float score, float mouthInfluence = 0f)
        {
            Current = new EmotionReading(
                label,
                score,
                new ReadOnlyDictionary<string, float>(new Dictionary<string, float> { { label, score } }),
                mouthInfluence,
                0f);
        }
    }
}
