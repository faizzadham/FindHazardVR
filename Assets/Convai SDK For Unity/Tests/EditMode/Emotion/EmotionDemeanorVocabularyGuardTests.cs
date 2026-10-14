using System.Linq;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Emotion.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Checks that Emotion's character-type picker reads the four temperament names from the
    ///     one place that owns them, rather than spelling its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Emotion, Body Animation and Body Language each show the user the same four
    ///         temperaments. They used to spell the names separately, and two were reworded while
    ///         the third was not — so the same character read <em>Warm</em> on two inspectors and a
    ///         different, longer word for the same idea on the third.
    ///     </para>
    ///     <para>
    ///         <see cref="CharacterDemeanors" /> is now the single owner of the names and their
    ///         order. This suite covers Emotion's side of that; the shared vocabulary itself is
    ///         covered by the embodiment suite, and each other module carries the same check for
    ///         its own picker.
    ///     </para>
    /// </remarks>
    [TestFixture]
    [Category("Architecture")]
    public sealed class EmotionDemeanorVocabularyGuardTests
    {
        private static readonly CharacterDemeanor[] Expected =
        {
            CharacterDemeanor.Composed,
            CharacterDemeanor.Warm,
            CharacterDemeanor.Energetic,
            CharacterDemeanor.Reserved
        };

        [Test]
        public void Emotion_PresentsEveryDemeanor_InTheSharedOrder()
        {
            CharacterDemeanor[] presented = EmotionPersonality.Archetypes.Select(a => a.Type).ToArray();
            CollectionAssert.AreEqual(
                Expected,
                presented,
                "Emotion does not present the four temperaments in the shared order.");

            string[] names = EmotionPersonality.Archetypes.Select(a => a.Name).ToArray();
            for (int i = 0; i < presented.Length; i++)
                Assert.AreEqual(
                    CharacterDemeanors.DisplayName(presented[i]),
                    names[i],
                    $"Emotion shows its own spelling of {presented[i]} instead of the shared one.");
        }

        [Test]
        public void Emotion_DescribesEveryDemeanor_InItsOwnWording()
        {
            // The names are shared; the descriptions are not, and must not be — "Reserved" means a
            // different thing to a face than it does to a walk cycle. This only checks Emotion
            // actually wrote one for each.
            foreach (EmotionArchetype archetype in EmotionPersonality.Archetypes)
                Assert.IsNotEmpty(archetype.Description, $"Emotion has no description for {archetype.Type}.");
        }
    }
}
