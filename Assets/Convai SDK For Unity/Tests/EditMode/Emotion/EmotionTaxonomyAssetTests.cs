using Convai.Domain.Embodiment.Taxonomy;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    [TestFixture]
    public sealed class EmotionTaxonomyAssetTests
    {
        private EmotionTaxonomyAsset _taxonomy;

        [SetUp]
        public void SetUp() => _taxonomy = EmotionTaxonomyAsset.CreateDefault();

        [TearDown]
        public void TearDown()
        {
            if (_taxonomy != null) Object.DestroyImmediate(_taxonomy);
        }

        [Test]
        public void Default_TaxonomyHasNeutral()
        {
            Assert.That(_taxonomy.Neutral, Is.Not.Null);
            Assert.That(_taxonomy.Neutral.IsNeutral, Is.True);
            Assert.That(_taxonomy.Neutral.Label, Is.EqualTo("neutral"));
        }

        [Test]
        public void TryResolve_CanonicalLabel_ReturnsDescriptor()
        {
            Assert.That(_taxonomy.TryResolve("joy", out EmotionDescriptor d), Is.True);
            Assert.That(d.Label, Is.EqualTo("joy"));
        }

        [Test]
        public void TryResolve_Alias_ResolvesToCanonical()
        {
            Assert.That(_taxonomy.TryResolve("happy", out EmotionDescriptor d), Is.True);
            Assert.That(d.Label, Is.EqualTo("joy"));
        }

        [Test]
        public void TryResolve_CommonLlmDriftAliases_ResolveToCanonical()
        {
            Assert.That(_taxonomy.TryResolve("excited", out EmotionDescriptor excited), Is.True);
            Assert.That(excited.Label, Is.EqualTo("joy"));

            Assert.That(_taxonomy.TryResolve("curious", out EmotionDescriptor curious), Is.True);
            Assert.That(curious.Label, Is.EqualTo("anticipation"));

            Assert.That(_taxonomy.TryResolve("worried", out EmotionDescriptor worried), Is.True);
            Assert.That(worried.Label, Is.EqualTo("fear"));
        }

        [Test]
        public void TryResolve_IsCaseInsensitive()
        {
            Assert.That(_taxonomy.TryResolve("ANGER", out EmotionDescriptor d), Is.True);
            Assert.That(d.Label, Is.EqualTo("anger"));
        }

        [Test]
        public void TryResolve_UnknownLabel_ReturnsFalse()
        {
            Assert.That(_taxonomy.TryResolve("nonexistent", out _), Is.False);
        }

        [Test]
        public void TryResolve_NullOrWhitespace_ReturnsFalse()
        {
            Assert.That(_taxonomy.TryResolve(null, out _), Is.False);
            Assert.That(_taxonomy.TryResolve("   ", out _), Is.False);
        }
    }
}
