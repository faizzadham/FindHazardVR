using Convai.Runtime.Embodiment;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Convenience wrapper around <see cref="DeterministicEmbodimentRandom" /> with a
    ///     fixed seed for deterministic test output. Reconstruct with the same seed for
    ///     bit-identical replay.
    /// </summary>
    internal static class DeterministicEmbodimentRandomFixture
    {
        /// <summary>Default test seed. Arbitrary constant — do not change without updating determinism tests.</summary>
        public const uint DefaultSeed = 0xC0FFEE;

        internal static DeterministicEmbodimentRandom Create(uint seed = DefaultSeed)
            => new(seed);
    }
}
