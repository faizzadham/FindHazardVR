using System.Collections.Generic;
using Convai.Domain.Embodiment.Taxonomy;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Guards the vocabulary's complement table, which is what the "show more than one emotion
    ///     at once" feature actually blends in.
    /// </summary>
    /// <remarks>
    ///     Seven of the nine entries used to ship with an empty complement list, so blending was a
    ///     documented no-op for every emotion except joy and trust — <c>Complement Blend Scale</c>
    ///     and <c>Max Simultaneous Emotions</c> could not be observed at all. These tests make that
    ///     regression fail loudly instead of silently.
    /// </remarks>
    public sealed class EmotionTaxonomyComplementsTests
    {
        private EmotionTaxonomyAsset _taxonomy;

        [SetUp]
        public void SetUp() => _taxonomy = EmotionTaxonomyAsset.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_taxonomy);

        [Test]
        public void EveryNonNeutralEmotion_DeclaresAtLeastOneComplement()
        {
            _taxonomy.EnsureBuilt();

            var missing = new List<string>();
            foreach (EmotionDescriptor descriptor in _taxonomy.Emotions)
            {
                if (descriptor.IsNeutral) continue;
                if (descriptor.Complements == null || descriptor.Complements.Count == 0)
                    missing.Add(descriptor.Label);
            }

            Assert.That(missing, Is.Empty,
                "Blending has nothing to blend for these emotions, so the feature would silently " +
                $"do nothing for them: {string.Join(", ", missing)}");
        }

        [Test]
        public void EveryComplement_ResolvesToANonNeutralEntryOtherThanItself()
        {
            _taxonomy.EnsureBuilt();

            foreach (EmotionDescriptor descriptor in _taxonomy.Emotions)
            {
                if (descriptor.Complements == null) continue;

                foreach (string complement in descriptor.Complements)
                {
                    Assert.That(_taxonomy.TryResolve(complement, out EmotionDescriptor resolved), Is.True,
                        $"'{descriptor.Label}' names a complement '{complement}' the vocabulary cannot resolve.");
                    Assert.That(resolved.IsNeutral, Is.False,
                        $"'{descriptor.Label}' names the neutral entry as a complement, which blending skips.");
                    Assert.That(resolved.Label, Is.Not.EqualTo(descriptor.Label),
                        $"'{descriptor.Label}' names itself as a complement.");
                }
            }
        }

        [Test]
        public void NeutralEntry_DeclaresNoComplements()
        {
            _taxonomy.EnsureBuilt();

            Assert.That(_taxonomy.Neutral.Complements, Is.Empty,
                "Neutral is the rest state; blending it with anything is meaningless.");
        }
    }
}
