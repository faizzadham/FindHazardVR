using Convai.Runtime.Animation;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>Settable <see cref="ISpeechEnergyProvider" /> for unit tests. Does not sample audio.</summary>
    public sealed class FakeSpeechEnergyProvider : ISpeechEnergyProvider
    {
        public float Current { get; set; }

        public void Sample(float deltaTime) { }
    }
}
