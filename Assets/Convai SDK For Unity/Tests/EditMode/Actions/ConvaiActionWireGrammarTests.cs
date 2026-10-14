using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Covers the type that owns the wire format: that it still renders exactly what it always
    ///     rendered, that reading a name back out of a rendered string agrees with the renderer, and
    ///     that it refuses the authored text it cannot carry — without refusing text it can.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiActionWireGrammarTests
    {
        private static ConvaiActionDefinition Definition(
            string name,
            string description = null,
            ConvaiActionTargetRequirement requirement = ConvaiActionTargetRequirement.None,
            params ConvaiActionParameterDefinition[] parameters) =>
            new()
            {
                ActionName = name,
                Description = description,
                TargetRequirement = requirement,
                Parameters = new List<ConvaiActionParameterDefinition>(parameters)
            };

        // ── Rendering is unchanged ────────────────────────────────────────────────────────

        /// <summary>
        ///     The renderer moved; its output did not.
        /// </summary>
        /// <remarks>
        ///     This is the gate for the whole change. The rendered string is what a Convai Character
        ///     is told an action is called, so a single byte of drift silently changes what every
        ///     customer's character was offered. The cases below are the same ones
        ///     <c>ActionSystemTests</c> asserts against the public entry point; asserting them here
        ///     as well means the grammar cannot be edited without one of the two going red.
        /// </remarks>
        [Test]
        public void Render_ProducesTheSameWireFormAsBefore()
        {
            Assert.That(
                ConvaiActionWireGrammar.Render(
                    Definition("Wave", "Wave hello.")),
                Is.EqualTo("Wave - Wave hello."),
                "A target-less action renders as a bare name plus its description.");

            Assert.That(
                ConvaiActionWireGrammar.Render(
                    Definition("Walk To", "Walk over to a place.", ConvaiActionTargetRequirement.Either)),
                Is.EqualTo("Walk To {target: reference} - Walk over to a place."),
                "An action that needs a target is offered a slot to put one in.");

            Assert.That(
                ConvaiActionWireGrammar.Render(
                    Definition("Hand To", "Give something to someone.", ConvaiActionTargetRequirement.Character,
                        new ConvaiActionParameterDefinition
                        {
                            Name = "recipient", Type = ConvaiActionParameterType.Reference
                        })),
                Is.EqualTo("Hand To {recipient: reference} - Give something to someone."),
                "A parameter that can carry the target is not given a second slot beside it.");

            Assert.That(
                ConvaiActionWireGrammar.Render(
                    Definition("Put", "Put an item into a container.", ConvaiActionTargetRequirement.None,
                        new ConvaiActionParameterDefinition
                        {
                            Name = "item", Description = "Inventory item.", Type = ConvaiActionParameterType.String
                        },
                        new ConvaiActionParameterDefinition
                        {
                            Name = "container",
                            Description = "Destination container.",
                            Type = ConvaiActionParameterType.Reference,
                            Connector = "on"
                        },
                        new ConvaiActionParameterDefinition
                        {
                            Name = "speed",
                            Type = ConvaiActionParameterType.Choice,
                            Connector = "at",
                            Choices = new List<string> { "slow", "fast" }
                        })),
                Is.EqualTo(
                    "Put {item: string} on {container: reference} at {speed: choice [slow|fast]} - " +
                    "Put an item into a container. item: Inventory item. container: Destination container."),
                "Connectors, types, choices and the description tail all render where they always did.");
        }

        [Test]
        public void Render_FoldsAuthoredTextToPrintableAscii()
        {
            string rendered = ConvaiActionWireGrammar.Render(
                Definition("Move To", "Move — quickly", ConvaiActionTargetRequirement.None,
                    new ConvaiActionParameterDefinition
                    {
                        Name = "destination",
                        Description = "Café destination",
                        Type = ConvaiActionParameterType.Reference,
                        Connector = "toward"
                    }));

            Assert.That(rendered, Does.Not.Contain("—"), "An em dash is not printable ASCII.");
            Assert.That(rendered, Does.Contain("Cafe destination"), "An accent folds to its base letter.");
        }

        /// <summary>
        ///     Typographic punctuation arrives as the ASCII that means the same thing, rather than as
        ///     a gap where the author's punctuation used to be.
        /// </summary>
        /// <remarks>
        ///     Measured on a real character: a description reading "it does not build anything — say
        ///     Run The Assembly for that" reached the model as "it does not build anything say Run
        ///     The Assembly for that". The aside had become part of the clause, the author had no way
        ///     of knowing, and the sentence the character was reasoning from was not the one anybody
        ///     wrote.
        /// </remarks>
        [Test]
        public void Render_TranslatesTypographicPunctuationRatherThanDroppingIt()
        {
            string rendered = ConvaiActionWireGrammar.Render(
                Definition("Fit Part", "It fits nothing — say Run The Assembly for that",
                    ConvaiActionTargetRequirement.None));

            Assert.That(rendered, Does.Contain("nothing - say"),
                "An em dash means a dash, and the aside has to survive as one.");
        }

        [TestCase("Ask the visitor’s name", "Ask the visitor's name", TestName = "Fold_CurlyApostrophe")]
        [TestCase("Say “ready” out loud", "Say \"ready\" out loud", TestName = "Fold_CurlyQuotes")]
        [TestCase("Wait, then carry on…", "Wait, then carry on...", TestName = "Fold_Ellipsis")]
        [TestCase("Between 2–5 steps", "Between 2-5 steps", TestName = "Fold_EnDash")]
        public void Render_KeepsTheMeaningOfPunctuationWrittenInANormalEditor(string authored, string expected)
        {
            string rendered = ConvaiActionWireGrammar.Render(
                Definition("Do Something", authored, ConvaiActionTargetRequirement.None));

            Assert.That(rendered, Does.Contain(expected));
        }

        /// <summary>
        ///     The translation never invents the separator that ends an action's name.
        /// </summary>
        /// <remarks>
        ///     Validation refuses an authored action name containing <c>" - "</c>, which is the only
        ///     reason <c>CanonicalNameOf</c> can cut a name out of a rendered line at all. Validation
        ///     reads what the author typed, so an em dash passes it. Spelling that dash out here would
        ///     create the reserved token after approval, and the action would come back as its first
        ///     word — silently, on a name the SDK had already said was fine.
        /// </remarks>
        [Test]
        public void Render_NeverTurnsAnActionNameIntoSomethingCanonicalNameOfWouldTruncate()
        {
            string rendered = ConvaiActionWireGrammar.Render(
                Definition("Sit — Chair", "Sit down on it.", ConvaiActionTargetRequirement.None));

            Assert.That(ConvaiActionWireGrammar.CanonicalNameOf(rendered), Is.EqualTo("Sit Chair"),
                "The whole name has to survive the round trip.");
        }

        /// <summary>The same guarantee for a connector, which also renders ahead of the description.</summary>
        [Test]
        public void Render_NeverTurnsAConnectorIntoTheDescriptionSeparator()
        {
            string rendered = ConvaiActionWireGrammar.Render(
                Definition("Put Down", "Put it down.", ConvaiActionTargetRequirement.None,
                    new ConvaiActionParameterDefinition
                    {
                        Name = "spot",
                        Type = ConvaiActionParameterType.Reference,
                        Connector = "—"
                    }));

            Assert.That(ConvaiActionWireGrammar.CanonicalNameOf(rendered), Is.EqualTo("Put Down"));
        }

        /// <summary>What has no ASCII equivalent is still blanked, because guessing would change meaning.</summary>
        [Test]
        public void Render_StillBlanksWhatHasNoAsciiEquivalent()
        {
            string rendered = ConvaiActionWireGrammar.Render(
                Definition("Do Something", "Go ➜ there", ConvaiActionTargetRequirement.None));

            Assert.That(rendered, Does.Contain("Go there"),
                "An arrow has no exact ASCII spelling, so it goes rather than becoming a guess.");
        }

        // ── The reader agrees with the writer ─────────────────────────────────────────────

        /// <summary>
        ///     Every definition the grammar can render, it can name again from what it rendered.
        /// </summary>
        /// <remarks>
        ///     The property that used to be assumed. Reading the canonical name back was a separate
        ///     scan that named the delimiters a second time, so it could disagree with the renderer
        ///     and did. Sweeping the authored space — with and without a description, parameters,
        ///     connectors, choices and a target requirement — is what makes the agreement a fact
        ///     rather than four examples.
        /// </remarks>
        [Test]
        public void CanonicalNameOf_RecoversTheActionNameFromAnythingRender_Produces()
        {
            string[] names = { "Wave", "Walk To", "Run In Order", "Open" };
            string[] descriptions = { null, "Does a thing.", "Walks over, then waits a moment." };
            ConvaiActionTargetRequirement[] requirements =
            {
                ConvaiActionTargetRequirement.None,
                ConvaiActionTargetRequirement.Object,
                ConvaiActionTargetRequirement.Character,
                ConvaiActionTargetRequirement.Either
            };
            ConvaiActionParameterDefinition[][] parameterSets =
            {
                System.Array.Empty<ConvaiActionParameterDefinition>(),
                new[]
                {
                    new ConvaiActionParameterDefinition { Name = "amount", Type = ConvaiActionParameterType.Number }
                },
                new[]
                {
                    new ConvaiActionParameterDefinition { Name = "item", Type = ConvaiActionParameterType.String },
                    new ConvaiActionParameterDefinition
                    {
                        Name = "container", Type = ConvaiActionParameterType.Reference, Connector = "on"
                    }
                },
                new[]
                {
                    new ConvaiActionParameterDefinition
                    {
                        Name = "reason",
                        Type = ConvaiActionParameterType.Choice,
                        Choices = new List<string> { "path-blocked", "target-missing" }
                    }
                }
            };

            foreach (string name in names)
            foreach (string description in descriptions)
            foreach (ConvaiActionTargetRequirement requirement in requirements)
            foreach (ConvaiActionParameterDefinition[] parameters in parameterSets)
            {
                ConvaiActionDefinition definition = Definition(name, description, requirement, parameters);
                string rendered = ConvaiActionWireGrammar.Render(definition);

                Assert.That(
                    ConvaiActionWireGrammar.CanonicalNameOf(rendered),
                    Is.EqualTo(name),
                    $"Rendered as '{rendered}', which should still be recognisable as '{name}'.");
            }
        }

        /// <summary>
        ///     Where reading a name back out of a rendered string is genuinely not possible, and why
        ///     that is survivable.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The renderer joins an action's name to its first parameter's connector with a
        ///         single space, so <c>Walk</c> + connector <c>to</c> renders as
        ///         <c>Walk to {destination: reference}</c> and reads back as <c>Walk to</c>. That is
        ///         an ordinary way to author an action, not a mistake, so it is not refused — it is
        ///         made harmless instead, by having the callers match the whole rendered string.
        ///     </para>
        ///     <para>
        ///         This test exists because the round-trip test above cannot catch it: every
        ///         parameter set there puts its connector on a <em>later</em> parameter, so the
        ///         property held for every case it swept while being false in general. A property
        ///         test that never generates the shape that breaks it is a test that agrees with you.
        ///     </para>
        /// </remarks>
        [Test]
        public void CanonicalNameOf_CannotRecoverANameTheConnectorAbsorbed_AndTheLookupDoesNotNeedItTo()
        {
            var walk = new ConvaiActionDefinition
            {
                ActionName = "Walk",
                TargetRequirement = ConvaiActionTargetRequirement.Either,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new()
                    {
                        Name = "destination",
                        Type = ConvaiActionParameterType.Reference,
                        Connector = "to"
                    }
                }
            };

            string rendered = walk.ToActionConfigString();
            Assert.That(rendered, Is.EqualTo("Walk to {destination: reference}"));
            Assert.That(ConvaiActionWireGrammar.CanonicalNameOf(rendered), Is.EqualTo("Walk to"),
                "The connector is inside the name as far as any reader of the string is concerned.");

            var catalog = new List<ConvaiActionDefinition> { walk };
            ConvaiActionDefinition resolved = ConvaiActionDefinition.ResolveRendered(
                rendered,
                ConvaiActionDefinition.BuildRenderedLookup(catalog),
                ConvaiActionDefinition.BuildLookup(catalog),
                out string canonicalName);

            Assert.That(resolved, Is.SameAs(walk),
                "Matching the whole rendered string is exact, so no name has to be recovered at all.");
            Assert.That(canonicalName, Is.EqualTo("Walk"),
                "And the name that comes back is the authored one, not the one the string implies.");
        }

        /// <summary>
        ///     Two definitions that render identically are indistinguishable to everyone, including
        ///     the Convai Character, which is shown the same line twice.
        /// </summary>
        [Test]
        public void ValidateCatalog_ReportsTwoActionsThatRenderToTheSameLine()
        {
            var give = new ConvaiActionDefinition
            {
                ActionName = "Give",
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "item", Connector = "to", Type = ConvaiActionParameterType.Auto }
                }
            };
            var giveTo = new ConvaiActionDefinition
            {
                ActionName = "Give to",
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "item", Type = ConvaiActionParameterType.Auto }
                }
            };

            Assert.That(give.ToActionConfigString(), Is.EqualTo(giveTo.ToActionConfigString()),
                "Precondition: these really do collide.");

            IReadOnlyList<ConvaiActionGrammarViolation> violations =
                ConvaiActionWireGrammar.ValidateCatalog(new List<ConvaiActionDefinition> { give, giveTo });

            Assert.That(violations, Is.Not.Empty);
            Assert.That(violations[0].Surface, Is.EqualTo(ConvaiActionGrammarSurface.RenderedAction));
            Assert.That(violations[0].Explanation, Does.Contain("Give"));
        }

        [Test]
        public void ValidateCatalog_SaysNothingAboutACatalogThatIsMerelyOrdinary()
        {
            var catalog = new List<ConvaiActionDefinition>
            {
                Definition("Walk To", "Walk somewhere.", ConvaiActionTargetRequirement.Either),
                Definition("Wave", "Wave hello."),
                new()
                {
                    ActionName = "Walk",
                    TargetRequirement = ConvaiActionTargetRequirement.Either,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new()
                        {
                            Name = "destination",
                            Type = ConvaiActionParameterType.Reference,
                            Connector = "to"
                        }
                    }
                }
            };

            Assert.That(ConvaiActionWireGrammar.ValidateCatalog(catalog), Is.Empty,
                "'Walk to {destination: reference}' and 'Walk To {target: reference}' are different lines.");
            Assert.That(ConvaiActionWireGrammar.ValidateCatalog(null), Is.Empty);
        }

        [Test]
        public void CanonicalNameOf_AndTheDefinitionEntryPoint_AreTheSameImplementation()
        {
            ConvaiActionDefinition definition =
                Definition("Walk To", "Walk over to a place.", ConvaiActionTargetRequirement.Either);
            string rendered = definition.ToActionConfigString();

            Assert.That(
                ConvaiActionWireGrammar.CanonicalNameOf(rendered),
                Is.EqualTo("Walk To"),
                "The availability filter and the patch reconciler both go through this one function.");
        }

        // ── What the format cannot carry ──────────────────────────────────────────────────

        /// <summary>
        ///     The defect the SDK generated from its own authored data.
        /// </summary>
        /// <remarks>
        ///     Nothing checked this before, so an action named with the description separator in it
        ///     rendered into a string that names a different action than the one that was authored —
        ///     and the two consumers that recover a name from a rendered string then addressed
        ///     something that does not exist, silently.
        /// </remarks>
        [Test]
        public void Validate_RefusesAnActionNameThatWouldBeReadAsTwoThings()
        {
            IReadOnlyList<ConvaiActionGrammarViolation> violations =
                ConvaiActionWireGrammar.Validate(Definition("Sit - Chair", "Sit down."));

            Assert.That(violations, Is.Not.Empty, "'Sit - Chair' cannot survive its own rendering.");
            Assert.That(violations[0].Surface, Is.EqualTo(ConvaiActionGrammarSurface.ActionName));
            Assert.That(violations[0].Explanation, Does.Contain("Sit - Chair"),
                "The message names the offending text so it can be found in an inspector.");
        }

        [Test]
        public void Validate_RefusesTheOtherWaysTextCollidesWithTheFormat()
        {
            Assert.That(
                ConvaiActionWireGrammar.Validate(Definition("Open {door}")),
                Is.Not.Empty, "A brace in an action name reads as the start of a parameter slot.");

            Assert.That(
                ConvaiActionWireGrammar.Validate(
                    Definition("Set", null, ConvaiActionTargetRequirement.None,
                        new ConvaiActionParameterDefinition { Name = "level: high" })),
                Is.Not.Empty, "A colon in a parameter name reads as the type marker.");

            Assert.That(
                ConvaiActionWireGrammar.Validate(
                    Definition("Fail", null, ConvaiActionTargetRequirement.None,
                        new ConvaiActionParameterDefinition
                        {
                            Name = "reason",
                            Type = ConvaiActionParameterType.Choice,
                            Choices = new List<string> { "a|b" }
                        })),
                Is.Not.Empty, "A pipe in a choice reads as two choices.");
        }

        /// <summary>
        ///     The rule has to be narrow enough to ship.
        /// </summary>
        /// <remarks>
        ///     A blanket ban on the characters the format uses would reject content this SDK already
        ///     ships — <c>target-unreachable</c>, <c>path-blocked</c>, <c>peer-missing</c> are real
        ///     authored choice values. The separator is a dash <em>with spaces around it</em>, and
        ///     that is the only form anything looks for, so a bare dash is safe everywhere. A
        ///     validation rule that fires on legitimate content is a rule somebody switches off.
        /// </remarks>
        [Test]
        public void Validate_AcceptsThePlainDashRealContentUses()
        {
            ConvaiActionDefinition definition = Definition(
                "Fail On Purpose",
                "Deliberately fails so the failure path can be watched end to end.",
                ConvaiActionTargetRequirement.None,
                new ConvaiActionParameterDefinition
                {
                    Name = "reason",
                    Description = "Which kind of failure to produce.",
                    Type = ConvaiActionParameterType.Choice,
                    Choices = new List<string>
                    {
                        "target-missing", "target-unreachable", "path-blocked", "peer-missing", "invalid-state"
                    }
                });

            Assert.That(ConvaiActionWireGrammar.Validate(definition), Is.Empty,
                "These are shipped choice values; the day this fails is the day the rule gets disabled.");

            Assert.That(
                ConvaiActionWireGrammar.Validate(Definition("X-Ray Bay-2")),
                Is.Empty,
                "A dash inside a word is not the separator.");
        }

        /// <summary>
        ///     A value can only land in one slot, so two slots may not share a name.
        /// </summary>
        /// <remarks>
        ///     Reachable without doing anything strange: call a parameter <c>target</c>, give it a
        ///     type that cannot carry one, and still ask the action for a target. The implicit slot
        ///     is added because no declared parameter can carry a reference, and the action is
        ///     offered as <c>Say To {target: string} {target: reference}</c> — the same name twice,
        ///     with two types, only one of which can be kept.
        /// </remarks>
        [Test]
        public void Validate_RefusesTwoSlotsThatShareAName()
        {
            var definition = Definition("Say To", null, ConvaiActionTargetRequirement.Either,
                new ConvaiActionParameterDefinition
                {
                    Name = "target",
                    Type = ConvaiActionParameterType.String
                });

            Assert.That(definition.ToActionConfigString(),
                Is.EqualTo("Say To {target: string} {target: reference}"),
                "Precondition: the collision really is what gets sent.");

            IReadOnlyList<ConvaiActionGrammarViolation> violations =
                ConvaiActionWireGrammar.Validate(definition);

            Assert.That(violations, Is.Not.Empty);
            Assert.That(violations[0].Explanation, Does.Contain("two slots called 'target'"));
        }

        [Test]
        public void Validate_AcceptsAParameterNamedTargetThatCanActuallyCarryOne()
        {
            var definition = Definition("Say To", null, ConvaiActionTargetRequirement.Either,
                new ConvaiActionParameterDefinition
                {
                    Name = "target",
                    Type = ConvaiActionParameterType.Reference
                });

            Assert.That(definition.ToActionConfigString(), Is.EqualTo("Say To {target: reference}"),
                "One slot, because the declared parameter can carry the target itself.");
            Assert.That(ConvaiActionWireGrammar.Validate(definition), Is.Empty);
        }

        [Test]
        public void Validate_AllowsADescriptionToContainTheSeparator()
        {
            Assert.That(
                ConvaiActionWireGrammar.Validate(
                    Definition("Walk To", "Walks over - then waits.", ConvaiActionTargetRequirement.Either)),
                Is.Empty,
                "The description is already past the separator, so another one cannot confuse anything.");
        }

        [Test]
        public void Validate_ReportsNothingForTheDefinitionsTheSdkShips()
        {
            Assert.That(ConvaiActionWireGrammar.Validate(Definition("Walk To", "Walk somewhere.",
                ConvaiActionTargetRequirement.Either)), Is.Empty);
            Assert.That(ConvaiActionWireGrammar.Validate(Definition("Follow The Player")), Is.Empty);
            Assert.That(ConvaiActionWireGrammar.Validate(Definition("Play Animator State")), Is.Empty);
            Assert.That(ConvaiActionWireGrammar.Validate(null), Is.Empty);
        }
    }
}
