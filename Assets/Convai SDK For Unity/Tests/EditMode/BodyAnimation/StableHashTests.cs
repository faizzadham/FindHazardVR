using Convai.Modules.BodyAnimation.Core;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="StableHash" />: same input always produces the same
    ///     value (asserted against a hard-coded constant, so a future refactor cannot silently
    ///     change every character's reproducible sequence), different inputs diverge, and
    ///     null/empty are handled without throwing.
    /// </summary>
    public sealed class StableHashTests
    {
        // FNV-1a 32-bit of "convai-character-alpha" — recomputed once and pinned here.
        // Changing this constant means every shipped character's seeded sequence would replay
        // differently; that must be a deliberate, reviewed decision, not an accidental one.
        private const int ExpectedHashOfAlpha = -1722825814;

        [Test]
        public void SameInput_ProducesSameValue()
        {
            int first = StableHash.Of("convai-character-alpha");
            int second = StableHash.Of("convai-character-alpha");

            Assert.AreEqual(first, second);
        }

        [Test]
        public void KnownInput_MatchesPinnedConstant()
        {
            Assert.AreEqual(ExpectedHashOfAlpha, StableHash.Of("convai-character-alpha"),
                "A change here means every existing character's seeded variant/ambient sequence " +
                "would silently replay differently — verify that is intentional before updating.");
        }

        [Test]
        public void DifferentInputs_ProduceDifferentValues()
        {
            int a = StableHash.Of("Character/Left/Arm");
            int b = StableHash.Of("Character/Right/Arm");

            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void NullInput_DoesNotThrow_AndIsStable()
        {
            int first = StableHash.Of(null);
            int second = StableHash.Of(null);

            Assert.AreEqual(first, second);
        }

        [Test]
        public void EmptyInput_DoesNotThrow_AndMatchesNull()
        {
            Assert.AreEqual(StableHash.Of(null), StableHash.Of(string.Empty),
                "Null and empty must be treated identically — both are 'no identity known'.");
        }
    }
}
