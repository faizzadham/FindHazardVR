using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Editor;
using Convai.Modules.BodyLanguage.Editor;
using Convai.Modules.Emotion.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Holds the four temperament names together across the three modules that show them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Emotion, Body Animation and Body Language each put a demeanor picker in front of the
    ///         user, and each one used to spell the four names itself. Two of them were renamed and
    ///         the third was not, so a user looking at one character saw <em>Warm</em> on two
    ///         inspectors and a different, longer word for the same idea on the third — while the
    ///         third module's own source comment claimed the names matched.
    ///     </para>
    ///     <para>
    ///         The names now have one owner, <see cref="CharacterDemeanors" />. These tests fail if a
    ///         module stops reading from it, presents the four in a different order, or grows a
    ///         hand-typed copy of any retired spelling.
    ///     </para>
    /// </remarks>
    [TestFixture]
    [Category("Architecture")]
    public sealed class DemeanorVocabularyGuardTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        private static readonly CharacterDemeanor[] Expected =
        {
            CharacterDemeanor.Composed,
            CharacterDemeanor.Warm,
            CharacterDemeanor.Energetic,
            CharacterDemeanor.Reserved
        };

        [Test]
        public void Domain_PresentsTheFourTemperaments_InOneFixedOrder()
        {
            CollectionAssert.AreEqual(Expected, CharacterDemeanors.Order.ToArray());
        }

        [Test]
        public void Domain_DisplayNameMatchesTheEnumMemberName_ForEveryDemeanor()
        {
            // The two are identical today. If a display name is ever reworded, that is a deliberate
            // user-facing change and this test is where it gets acknowledged — not a silent drift
            // between what the code calls a temperament and what the inspector calls it.
            foreach (CharacterDemeanor demeanor in Expected)
                Assert.AreEqual(
                    demeanor.ToString(),
                    CharacterDemeanors.DisplayName(demeanor),
                    $"Display name for {demeanor} no longer matches its enum member name.");
        }

        [Test]
        public void Domain_DisplayName_FallsBackToWarm_ForAnUnrecognisedValue()
        {
            Assert.AreEqual("Warm", CharacterDemeanors.DisplayName((CharacterDemeanor)int.MaxValue));
        }

        [Test]
        public void Emotion_PresentsEveryDemeanor_InTheSharedOrder()
        {
            AssertPresentsSharedVocabulary(
                "Emotion",
                EmotionPersonality.Archetypes.Select(a => a.Type),
                EmotionPersonality.Archetypes.Select(a => a.Name));
        }

        [Test]
        public void BodyAnimation_PresentsEveryDemeanor_InTheSharedOrder()
        {
            AssertPresentsSharedVocabulary(
                "Body Animation",
                BodyAnimationPersonality.Archetypes.Select(a => a.Demeanor),
                BodyAnimationPersonality.Archetypes.Select(a => a.Name));
        }

        [Test]
        public void BodyLanguage_PresentsEveryDemeanor_InTheSharedOrder()
        {
            AssertPresentsSharedVocabulary(
                "Body Language",
                BodyLanguageDemeanorPresets.Presets.Select(p => p.Demeanor),
                BodyLanguageDemeanorPresets.Presets.Select(p => p.Name));
        }

        [Test]
        public void EveryModuleDescribesEachDemeanor_WithItsOwnWording()
        {
            // The names are shared; the descriptions are not, and must not be — "Reserved" means a
            // different thing to a face than it does to a walk cycle. This only checks each module
            // actually wrote one.
            foreach (EmotionArchetype archetype in EmotionPersonality.Archetypes)
                Assert.IsNotEmpty(archetype.Description, $"Emotion has no description for {archetype.Type}.");

            foreach (BodyAnimationArchetype archetype in BodyAnimationPersonality.Archetypes)
                Assert.IsNotEmpty(archetype.Description, $"Body Animation has no description for {archetype.Demeanor}.");
        }

        [Test]
        public void NoEmbodimentSourceFile_SpellsARetiredTemperamentName()
        {
            // What a re-introduced literal looks like. These four were the Emotion module's original
            // wording; two other modules copied them, then Emotion moved on and the copies did not.
            string[] retired =
            {
                "Neutral Professional",
                "Warm Friendly",
                "Reserved Stoic",
                "Expressive Animated"
            };

            string[] roots =
            {
                "SDK/Domain/Embodiment",
                "SDK/Modules/Emotion",
                "SDK/Modules/BodyAnimation",
                "SDK/Modules/BodyLanguage",
                "SDK/Editor/Embodiment"
            };

            var violations = new List<string>();
            foreach (string root in roots)
            {
                string absolute = Path.Combine(PackageRoot, root.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(absolute)) continue;

                foreach (string file in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(file);
                    foreach (string term in retired)
                        if (text.Contains(term, StringComparison.Ordinal))
                            violations.Add($"{Path.GetFileName(file)}: \"{term}\"");
                }
            }

            Assert.IsEmpty(
                violations,
                "A retired temperament name is back in the source. Use CharacterDemeanors.DisplayName:\n" +
                string.Join("\n", violations));
        }

        private static void AssertPresentsSharedVocabulary(
            string moduleName, IEnumerable<CharacterDemeanor> demeanors, IEnumerable<string> displayNames)
        {
            CharacterDemeanor[] presented = demeanors.ToArray();
            CollectionAssert.AreEqual(
                Expected,
                presented,
                $"{moduleName} does not present the four temperaments in the shared order.");

            string[] names = displayNames.ToArray();
            for (int i = 0; i < presented.Length; i++)
                Assert.AreEqual(
                    CharacterDemeanors.DisplayName(presented[i]),
                    names[i],
                    $"{moduleName} shows its own spelling of {presented[i]} instead of the shared one.");
        }
    }
}
