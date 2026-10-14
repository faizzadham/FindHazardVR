using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Covers the two properties that make reading total: a parameter nobody filled in says so,
    ///     and a repair never takes a name away from something this character actually has.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiActionReadingTotalityTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
        }

        private GameObject Spawn(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        private ConvaiActionConfig ConfigWithObject(string name, params string[] aliases)
        {
            var entry = new ConvaiActionObjectDefinition
            {
                Name = name,
                GameObjectReference = Spawn(name),
                Aliases = new List<string>(aliases ?? System.Array.Empty<string>())
            };
            return new ConvaiActionConfig { Objects = new List<ConvaiActionObjectDefinition> { entry } };
        }

        // ── A parameter nobody filled in ─────────────────────────────────────────────────

        /// <summary>
        ///     The difference between "no destination was given" and "the destination is blank".
        /// </summary>
        /// <remarks>
        ///     Unfilled slots are padded so values stay lined up with the authored parameter order.
        ///     That padding used to be indistinguishable from an answer, so an Action Behavior read
        ///     an empty string as an instruction and did something slightly wrong for a reason
        ///     nothing recorded.
        /// </remarks>
        [Test]
        public void Enrich_MarksADeclaredParameterTheCharacterNeverFilledIn()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Escort",
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "person", Type = ConvaiActionParameterType.String },
                    new() { Name = "destination", Type = ConvaiActionParameterType.String, Connector = "to" }
                }
            };
            var definitions = new List<ConvaiActionDefinition> { definition };

            // Only the first slot is answered; nothing reaches the second.
            var command = new ConvaiActionCommand("Escort", "{person: Mira}");
            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(command, null, definitions);

            Assert.That(enriched.Parameters["person"].Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Provided));
            Assert.That(enriched.Parameters["destination"].Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Missing),
                "Nothing was said about the destination; the slot exists only because the action declares it.");
        }

        /// <summary>
        ///     Where the line between Provided and Missing actually falls.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Not "the text is empty" — <em>the split assigned this slot a value at all</em>.
        ///         Here the Convai Character names both slots and leaves the second one blank, so
        ///         the second is Provided with empty text: something was said about it.
        ///     </para>
        ///     <para>
        ///         An earlier version of this test asserted that a whole target of <c>""</c> was also
        ///         Provided. It is not, and should not be: after the wrapping quotes come off there is
        ///         nothing left to assign to any slot, so every slot is Missing. That test was
        ///         asserting a distinction this pipeline cannot draw and would not be more useful for
        ///         drawing — an Action Behavior handed nothing is better told it was handed nothing.
        ///     </para>
        /// </remarks>
        [Test]
        public void Enrich_SeparatesASlotAnsweredBlankFromASlotNothingReached()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Escort",
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "person", Type = ConvaiActionParameterType.String },
                    new() { Name = "destination", Type = ConvaiActionParameterType.String, Connector = "to" }
                }
            };
            var definitions = new List<ConvaiActionDefinition> { definition };

            ConvaiActionCommand answeredBlank = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Escort", "person: Mira to destination:"), null, definitions);

            Assert.That(answeredBlank.Parameters["destination"].Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Provided),
                "The slot was named and left blank — that is an answer, and an odd one worth seeing.");
            Assert.That(answeredBlank.Parameters["destination"].StringValue, Is.Empty);

            ConvaiActionCommand nothingSaid = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Escort", "\"\""), null, definitions);

            Assert.That(nothingSaid.Parameters["destination"].Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Missing),
                "Nothing survived cleaning, so no slot was assigned anything.");
            Assert.That(nothingSaid.Parameters["person"].Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Missing));
        }

        [Test]
        public void ParameterPresence_DefaultsToProvidedForValuesBuiltInUserCode()
        {
            Assert.That(new ConvaiActionParameterValue().Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Provided),
                "Zero must mean Provided, or every hand-built value silently becomes Missing.");
        }

        [Test]
        public void ParameterPresence_SurvivesACopy()
        {
            var value = new ConvaiActionParameterValue { Presence = ConvaiActionParameterPresence.Missing };
            Assert.That(value.Clone().Presence, Is.EqualTo(ConvaiActionParameterPresence.Missing));
        }

        // ── A repair never destroys a real name ──────────────────────────────────────────

        /// <summary>
        ///     Every stripper is a guess that punctuation is decoration, and every one of them can
        ///     destroy a real name. These are the names that would have been destroyed.
        /// </summary>
        /// <remarks>
        ///     The failure this prevents is invisible by construction: the name stops matching, the
        ///     action does nothing, and the repair that caused it looks correct in isolation. Asking
        ///     the vocabulary first bounds the whole class — no future repair, however aggressive,
        ///     can take a name away from something that exists.
        /// </remarks>
        [Test]
        public void Clean_LeavesTextAloneWhenItAlreadyNamesSomethingThisCharacterHas()
        {
            Assert.That(
                ConvaiActionWireText.Clean("{Annex}", ConfigWithObject("{Annex}")),
                Is.EqualTo("{Annex}"),
                "A bay really called '{Annex}' is not a slot the model copied.");

            Assert.That(
                ConvaiActionWireText.Clean("- Special", ConfigWithObject("- Special")),
                Is.EqualTo("- Special"),
                "A prop really called '- Special' is not a separator echo.");

            Assert.That(
                ConvaiActionWireText.Clean("'Q'", ConfigWithObject("'Q'")),
                Is.EqualTo("'Q'"),
                "A room really called \"'Q'\" is not a quoted value.");
        }

        /// <summary>
        ///     The braces are syntax; what is inside them may be a real name.
        /// </summary>
        /// <remarks>
        ///     A target called <c>Bay2:North</c> looks exactly like a slot name followed by a value,
        ///     so unwrapping <c>{Bay2:North}</c> and then dropping everything before the colon takes
        ///     the name away. This was the one repair still guessing after the vocabulary rule was
        ///     introduced — it predates the rule.
        /// </remarks>
        [Test]
        public void Clean_KeepsALabelShapedNameThatIsRealFromInsideBraces()
        {
            Assert.That(
                ConvaiActionWireText.Clean("{Bay2:North}", ConfigWithObject("Bay2:North")),
                Is.EqualTo("Bay2:North"),
                "The braces come off; the name inside them stays.");
        }

        [Test]
        public void Clean_StillDropsASlotNameFromInsideBracesWhenItIsOnlySyntax()
        {
            Assert.That(
                ConvaiActionWireText.Clean("{target: The Gallery}", ConfigWithObject("The Gallery")),
                Is.EqualTo("The Gallery"),
                "Nothing is called 'target: The Gallery', so the label really is the template's.");

            Assert.That(
                ConvaiActionWireText.CleanWithoutVocabulary("{target: The Gallery}"),
                Is.EqualTo("The Gallery"),
                "And with no vocabulary to consult, the repair behaves exactly as it always did.");
        }

        /// <summary>
        ///     A quote at each end is only a wrapper when the opening one closes at the far end. Two
        ///     strings sitting next to each other also begin and end with a quote, and taking those
        ///     for a pair splices them together.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Shown a slot, a Convai Character may answer with a whole object, and once the
        ///         braces are off what is left is a quoted key beside a quoted value. Read as a
        ///         wrapper it becomes <c>gesture": "wave</c>, which names nothing — measured live,
        ///         and the reason a character announced a wave and stood still.
        ///     </para>
        ///     <para>
        ///         The cheap version of this rule — refuse any value with a quote inside it — is
        ///         wrong in the other direction, and was tried: it also refuses <c>''follow''</c> and
        ///         a line quoted around an apostrophe. Both directions are pinned so it cannot come
        ///         back.
        ///     </para>
        /// </remarks>
        [Test]
        public void Clean_TellsAWrappingQuoteFromAQuoteInsideTheText()
        {
            ConvaiActionConfig config = ConfigWithObject("The Gallery");

            Assert.That(
                ConvaiActionWireText.Clean("\"gesture\": \"wave\"", config),
                Is.EqualTo("\"gesture\": \"wave\""),
                "A key and a value are two strings. Stripping their outer ends splices them.");

            Assert.That(
                ConvaiActionWireText.Clean("''follow''", config),
                Is.EqualTo("follow"),
                "One value wrapped twice is still one value.");

            Assert.That(
                ConvaiActionWireText.Clean("\"It's fine\"", config),
                Is.EqualTo("It's fine"),
                "An apostrophe cannot close a double quote, so it is text rather than a wrapper.");
        }

        [Test]
        public void Clean_ProtectsAnAlternateNameJustAsWell()
        {
            Assert.That(
                ConvaiActionWireText.Clean("- Special", ConfigWithObject("Storeroom", "- Special")),
                Is.EqualTo("- Special"),
                "Alternate names are names; the protection cannot stop at the primary one.");
        }

        /// <summary>
        ///     The protection must not defeat the repairs it bounds.
        /// </summary>
        [Test]
        public void Clean_StillRepairsWhenTheRawTextNamesNothing()
        {
            ConvaiActionConfig config = ConfigWithObject("The Gallery");

            Assert.That(ConvaiActionWireText.Clean("- The Gallery", config), Is.EqualTo("The Gallery"));
            Assert.That(ConvaiActionWireText.Clean("'The Gallery'", config), Is.EqualTo("The Gallery"));
            Assert.That(ConvaiActionWireText.Clean("{target: The Gallery}", config), Is.EqualTo("The Gallery"));
            Assert.That(ConvaiActionWireText.Clean("- {target: \"The Gallery\"}", config),
                Is.EqualTo("The Gallery"),
                "Layered decoration still unwraps all the way down.");
        }

        [Test]
        public void Clean_BehavesExactlyAsBeforeWhenNoVocabularyIsSupplied()
        {
            Assert.That(ConvaiActionWireText.CleanWithoutVocabulary("- The Gallery"), Is.EqualTo("The Gallery"));
            Assert.That(ConvaiActionWireText.CleanWithoutVocabulary("X-Ray"), Is.EqualTo("X-Ray"));
            Assert.That(ConvaiActionWireText.CleanWithoutVocabulary("Bay-2"), Is.EqualTo("Bay-2"));
            Assert.That(ConvaiActionWireText.CleanWithoutVocabulary("Bay 2: North"), Is.EqualTo("Bay 2: North"));
        }

        [Test]
        public void Clean_DoesNotProtectAnUnavailableTarget()
        {
            ConvaiActionConfig config = ConfigWithObject("- Special");
            config.Objects[0].Available = false;

            Assert.That(
                ConvaiActionWireText.Clean("- Special", config),
                Is.EqualTo("Special"),
                "A withdrawn target is not part of the vocabulary, so it protects nothing.");
        }

        /// <summary>
        ///     The parameter-label stripper needs the same protection as the value cleaner.
        /// </summary>
        /// <remarks>
        ///     Shown <c>{destination: reference}</c>, a model may answer <c>destination: East Hall</c>
        ///     and the label has to come off. But a target genuinely called <c>Bay 2: North</c> is not
        ///     mimicry, and a target called <c>destination: East Hall</c> — however unlikely — is a
        ///     name that exists.
        /// </remarks>
        [Test]
        public void Enrich_DoesNotStripALabelOffAValueThatIsItselfARealName()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Walk To",
                TargetRequirement = ConvaiActionTargetRequirement.Either,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "target", Type = ConvaiActionParameterType.Reference }
                }
            };

            ConvaiActionConfig config = ConfigWithObject("target: East Hall");
            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Walk To", "target: East Hall"),
                config,
                new List<ConvaiActionDefinition> { definition });

            Assert.That(enriched.Parameters["target"].StringValue, Is.EqualTo("target: East Hall"),
                "An exact registered name wins over the label-shaped syntax it happens to resemble.");
        }

        [Test]
        public void Enrich_StillStripsALabelWhenTheValueIsNotARealName()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Walk To",
                TargetRequirement = ConvaiActionTargetRequirement.Either,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "target", Type = ConvaiActionParameterType.Reference }
                }
            };

            ConvaiActionConfig config = ConfigWithObject("East Hall");
            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Walk To", "target: East Hall"),
                config,
                new List<ConvaiActionDefinition> { definition });

            Assert.That(enriched.Parameters["target"].StringValue, Is.EqualTo("East Hall"));
        }
    }
}
