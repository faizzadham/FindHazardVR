using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Covers what a command may bring in with it, and what happens to a command a caller
    ///     injects rather than the backend sending.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiActionIngressTests
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

        private ConvaiActionConfig ConfigWith(string objectName) =>
            new()
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = objectName, GameObjectReference = Spawn(objectName) }
                }
            };

        private sealed class AcceptingExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public System.Threading.Tasks.Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, System.Threading.CancellationToken cancellationToken) =>
                System.Threading.Tasks.Task.FromResult(ConvaiActionExecutionResult.Succeeded("ok"));
        }

        private static ConvaiActionDefinition WalkTo() =>
            new()
            {
                ActionName = "Walk To",
                Description = "Walk over to a place.",
                TargetRequirement = ConvaiActionTargetRequirement.Either
            };

        /// <summary>
        ///     The same action with a behavior bound to it.
        /// </summary>
        /// <remarks>
        ///     The admission stage refuses an action with nothing to run it, before it looks at
        ///     targets at all — so a test about what admission decides has to give it something
        ///     runnable, or it is only testing the earlier refusal.
        /// </remarks>
        private ConvaiActionDefinition ExecutableWalkTo()
        {
            ConvaiActionDefinition definition = WalkTo();
            definition.Executor = Spawn("walk-to-host").AddComponent<AcceptingExecutor>();
            return definition;
        }

        // ── What the wire may set ────────────────────────────────────────────────────────

        /// <summary>
        ///     A payload cannot reach into the pipeline's own state.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Commands used to be deserialized straight onto the type the whole pipeline carries,
        ///         so every public property on it was settable from the wire. <c>enriched</c> decides
        ///         whether the SDK reads the command at all and <c>parameters</c> is what reading
        ///         produces — a payload setting them would have gone through untouched, carrying
        ///         values nothing had checked.
        ///     </para>
        ///     <para>
        ///         The live backend sends neither, so this has never happened. That is exactly why it
        ///         is worth a test: nothing else would notice if it started.
        ///     </para>
        /// </remarks>
        [Test]
        public void TryParseBatch_ReadsOnlyTheNameAndTargetTheBackendActuallySends()
        {
            var payload = JObject.Parse(@"{
                ""actions"": [
                    {
                        ""name"": ""Walk To"",
                        ""target"": ""The Gallery"",
                        ""enriched"": true,
                        ""actionString"": ""anything at all"",
                        ""waitForBotSpeech"": true,
                        ""delayAfterBotSpeechSeconds"": 99.0,
                        ""parameters"": { ""target"": { ""stringValue"": ""Somewhere Else"" } }
                    }
                ]
            }");

            Assert.That(
                ConvaiActionResponseParser.TryParseBatch(
                    payload, out IReadOnlyList<ConvaiActionCommand> actions, out int skipped),
                Is.True);
            Assert.That(skipped, Is.Zero);
            Assert.That(actions, Has.Count.EqualTo(1));

            ConvaiActionCommand command = actions[0];
            Assert.That(command.Name, Is.EqualTo("Walk To"));
            Assert.That(command.Target, Is.EqualTo("The Gallery"));

            Assert.That(command.Enriched, Is.False,
                "The reader decides this, not the payload.");
            Assert.That(command.Parameters, Is.Empty,
                "Parameters are what reading produces; they cannot arrive pre-made.");
            Assert.That(command.WaitForBotSpeech, Is.False);
            Assert.That(command.DelayAfterBotSpeechSeconds, Is.Zero);
            Assert.That(command.ActionString, Is.EqualTo("Walk To The Gallery"),
                "Rebuilt from the two fields that are real, not taken from the payload.");
        }

        [Test]
        public void TryParseBatch_StillCountsEntriesItCannotRead()
        {
            var payload = JObject.Parse(@"{ ""actions"": [ { ""name"": """" }, 7, { ""name"": ""Wave"" } ] }");

            Assert.That(
                ConvaiActionResponseParser.TryParseBatch(
                    payload, out IReadOnlyList<ConvaiActionCommand> actions, out int skipped),
                Is.True);

            Assert.That(actions, Has.Count.EqualTo(1));
            Assert.That(skipped, Is.EqualTo(2), "A batch that loses entries has to say how many.");
        }

        // ── A command the caller built ───────────────────────────────────────────────────

        /// <summary>
        ///     Enrichment fills the slots nobody claimed; it does not overwrite the caller.
        /// </summary>
        /// <remarks>
        ///     Reading used to begin by clearing the parameter dictionary. On the wire that was
        ///     harmless — the backend sends only a name and a target, so it was always empty. The
        ///     moment reading became unconditional it would have started wiping the parameters of
        ///     anyone who builds a command in their own code and hands it to
        ///     <c>ConvaiActionDispatcher.EnqueueActions</c>.
        /// </remarks>
        [Test]
        public void Enrich_KeepsParametersTheCallerSuppliedItself()
        {
            var command = new ConvaiActionCommand("Walk To", "The Gallery");
            command.Parameters["target"] = new ConvaiActionParameterValue
            {
                Type = ConvaiActionParameterType.String,
                StringValue = "Chosen By Hand",
                RawValue = "Chosen By Hand"
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                command, ConfigWith("The Gallery"), new List<ConvaiActionDefinition> { WalkTo() });

            Assert.That(enriched.Parameters["target"].StringValue, Is.EqualTo("Chosen By Hand"),
                "The caller already knew what the command meant.");
        }

        /// <summary>
        ///     Reading is idempotent, which is what lets it be unconditional.
        /// </summary>
        [Test]
        public void Enrich_OfAnAlreadyEnrichedCommandChangesNothing()
        {
            var definitions = new List<ConvaiActionDefinition> { WalkTo() };
            ConvaiActionConfig config = ConfigWith("The Gallery");

            ConvaiActionCommand once = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Walk To", "'The Gallery'"), config, definitions);
            ConvaiActionCommand twice = ConvaiActionResponseParser.Enrich(once, config, definitions);

            Assert.That(twice.Name, Is.EqualTo(once.Name));
            Assert.That(twice.Target, Is.EqualTo(once.Target));
            Assert.That(twice.Parameters["target"].StringValue,
                Is.EqualTo(once.Parameters["target"].StringValue));
        }

        /// <summary>
        ///     A command whose name matches a definition is still read.
        /// </summary>
        /// <remarks>
        ///     The dispatcher used to read a command only when the definition lookup <em>failed</em>.
        ///     Commands from the backend arrive already read, so nothing showed — but anything handed
        ///     to <c>EnqueueActions</c> with a name that matched took the other branch and was never
        ///     cleaned or parsed at all. The SDK's own Test Run and Live tools went down that branch,
        ///     which means they exercised a different pipeline from a real conversation.
        /// </remarks>
        [Test]
        public void Enrich_CleansAndParsesEvenWhenTheNameAlreadyMatchesADefinition()
        {
            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Walk To", "'The Gallery'"),
                ConfigWith("The Gallery"),
                new List<ConvaiActionDefinition> { WalkTo() });

            Assert.That(enriched.Target, Is.EqualTo("The Gallery"), "The model's quotes come off.");
            Assert.That(enriched.Parameters.ContainsKey("target"), Is.True,
                "And the target is parked where resolution looks for it.");
        }

        /// <summary>
        ///     The reader must split against the slots the template actually presented.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Measured against the live backend, not imagined. Asked *"is the power generator
        ///         reading between 20 and 80?"*, a Convai Character answered with exactly the
        ///         template it had been shown, filled in:
        ///         <c>Compare Reading {low: 20} {high: 80} {target: Power Generator}</c>. The model
        ///         did nothing wrong and the backend passed it through unchanged.
        ///     </para>
        ///     <para>
        ///         The SDK threw the answer away. <c>{target: reference}</c> is rendered for an action
        ///         that needs a target but declares no parameter to carry one — so the template has
        ///         three slots while <c>Definition.Parameters</c> has two. The split was sized by the
        ///         declared parameters, produced three values, and padded the list <em>down</em> to
        ///         two. The third value — the target — was discarded, after which the action was
        ///         dropped for having no target, and the explanation said the character "named
        ///         nothing".
        ///     </para>
        /// </remarks>
        [Test]
        public void Enrich_KeepsTheValueForTheImplicitTargetSlotTheTemplateOffered()
        {
            var compareReading = new ConvaiActionDefinition
            {
                ActionName = "Compare Reading",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "low", Type = ConvaiActionParameterType.Number },
                    new() { Name = "high", Type = ConvaiActionParameterType.Number }
                }
            };

            Assert.That(compareReading.ToActionConfigString(),
                Is.EqualTo("Compare Reading {low: number} {high: number} {target: reference}"),
                "Precondition: the template really does present three slots.");

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Compare Reading {low: 20} {high: 80} {target: Power Generator}"),
                ConfigWith("Power Generator"),
                new List<ConvaiActionDefinition> { compareReading });

            Assert.That(enriched.Parameters["low"].NumberValue, Is.EqualTo(20f));
            Assert.That(enriched.Parameters["high"].NumberValue, Is.EqualTo(80f));
            Assert.That(enriched.Parameters.ContainsKey("target"), Is.True,
                "The slot the template offered has to have somewhere to land.");
            Assert.That(enriched.Parameters["target"].StringValue, Is.EqualTo("Power Generator"));
            Assert.That(enriched.Parameters["target"].Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Provided));
        }

        /// <summary>
        ///     The same three slots, answered as one object rather than as three filled slots.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Measured live on 2026-08-12: shown the same template, the same character answered
        ///         <c>Show Assembly Step {step: 4, target: "Assembly Bench"}</c> on one turn and
        ///         <c>{step: 4} {target: …}</c> on another. Both are the model filling in what it
        ///         was shown; only the punctuation between the values differs.
        ///     </para>
        ///     <para>
        ///         Left alone, the outer brace and the comma travelled inside the values: <c>step</c>
        ///         arrived as <c>4,</c>, which is not a number, and the visitor who asked for step
        ///         four got step two and then step three. The room said nothing about it, because
        ///         from the executor's side a step it cannot read is simply "carry on".
        ///     </para>
        /// </remarks>
        [TestCase("Compare Reading {low: 20, high: 80, target: \"Power Generator\"}",
            TestName = "Enrich_ReadsThreeSlotsWrittenAsOneObject")]
        [TestCase("Compare Reading (low=20, high=80, target='Power Generator')",
            TestName = "Enrich_ReadsThreeSlotsWrittenAsOneBracketedObject")]
        [TestCase("Compare Reading {\"low\": 20, \"high\": 80, \"target\": \"Power Generator\"}",
            TestName = "Enrich_ReadsThreeSlotsWrittenAsJson")]
        public void Enrich_ReadsEverySlotOutOfASingleObject(string sent)
        {
            var compareReading = new ConvaiActionDefinition
            {
                ActionName = "Compare Reading",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "low", Type = ConvaiActionParameterType.Number },
                    new() { Name = "high", Type = ConvaiActionParameterType.Number }
                }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand(sent),
                ConfigWith("Power Generator"),
                new List<ConvaiActionDefinition> { compareReading });

            Assert.That(enriched.Parameters["low"].NumberValue, Is.EqualTo(20f),
                "The separator between two fields belongs to neither of them.");
            Assert.That(enriched.Parameters["high"].NumberValue, Is.EqualTo(80f));
            Assert.That(enriched.Parameters["target"].StringValue, Is.EqualTo("Power Generator"),
                "And the closing bracket is the object's, not the last value's.");
        }

        /// <summary>
        ///     A slot the Convai Character deliberately left out stays empty, and the one it did
        ///     name still arrives.
        /// </summary>
        /// <remarks>
        ///     Measured live: asked for the next step, the character answered
        ///     <c>Show Assembly Step {target: Assembly Bench}</c> — leaving <c>step</c> out is what
        ///     the action's own description asks for when the visitor says "next". Refusing the
        ///     labels for not covering every slot sent the blob to the whitespace guess, and
        ///     <c>step</c> came back holding <c>{target:</c>.
        /// </remarks>
        [Test]
        public void Enrich_BelievesALabelEvenWhenTheOtherSlotsWereLeftOut()
        {
            var showStep = new ConvaiActionDefinition
            {
                ActionName = "Show Assembly Step",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "step", Type = ConvaiActionParameterType.Number }
                }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Show Assembly Step {target: Assembly Bench}"),
                ConfigWith("Assembly Bench"),
                new List<ConvaiActionDefinition> { showStep });

            Assert.That(enriched.Parameters["target"].StringValue, Is.EqualTo("Assembly Bench"));
            Assert.That(enriched.Parameters["step"].Presence,
                Is.EqualTo(ConvaiActionParameterPresence.Missing),
                "Leaving a slot out is an answer. Filling it with the other slot's label is not.");
        }

        /// <summary>
        ///     An action named with its parameters in brackets is still that action.
        /// </summary>
        /// <remarks>
        ///     Measured live: <c>Follow Me(mode='follow')</c> was dropped as an action this
        ///     character does not have, and the visitor's "follow me" did nothing. The name test
        ///     accepted only whitespace or a brace after the name; a model reaching for a different
        ///     bracket was enough to lose the command.
        /// </remarks>
        [TestCase("Follow Me(mode='follow')", "follow", TestName = "Enrich_ReadsParametersWrittenInBrackets")]
        [TestCase("Follow Me[mode=stop]", "stop", TestName = "Enrich_ReadsParametersWrittenInSquareBrackets")]
        [TestCase("Follow Me {\"mode\": \"stop\"}", "stop", TestName = "Enrich_ReadsAParameterWrittenAsJson")]
        public void Enrich_FindsTheActionHoweverTheParametersWereAttached(string sent, string expected)
        {
            var followMe = new ConvaiActionDefinition
            {
                ActionName = "Follow Me",
                TargetRequirement = ConvaiActionTargetRequirement.None,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new()
                    {
                        Name = "mode",
                        Type = ConvaiActionParameterType.Choice,
                        Choices = new List<string> { "follow", "stop" }
                    }
                }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand(sent), null, new List<ConvaiActionDefinition> { followMe });

            Assert.That(enriched.Name, Is.EqualTo("Follow Me"),
                "The action was named; only the punctuation after it differed.");
            Assert.That(enriched.Parameters["mode"].StringValue, Is.EqualTo(expected));
            Assert.That(enriched.Parameters["mode"].IsConstraintMatch, Is.True);
        }

        /// <summary>
        ///     A longer action name is not the shorter one with something after it.
        /// </summary>
        /// <remarks>
        ///     The boundary test that replaced a list of accepted punctuation still has to refuse
        ///     this, or every action would match every action that starts the same way.
        /// </remarks>
        [Test]
        public void Enrich_DoesNotTakeALongerWordForAShorterActionName()
        {
            var walkTo = new ConvaiActionDefinition
            {
                ActionName = "Walk To",
                TargetRequirement = ConvaiActionTargetRequirement.None
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Walk Toward The Bench"), null,
                new List<ConvaiActionDefinition> { walkTo });

            Assert.That(enriched.Name, Is.EqualTo("Walk Toward The Bench"),
                "'Walk Toward' is not 'Walk To' — the word carries on.");
        }

        /// <summary>
        ///     An action whose name the Convai Character wrote back without its article is still that
        ///     action.
        /// </summary>
        /// <remarks>
        ///     Measured on the Terminal demo: shown <c>Light The Room</c>, the character answered
        ///     <c>Light Room</c>, and the command was dropped as an action this character does not
        ///     have — so the room stayed dark over a missing <c>The</c>.
        /// </remarks>
        [Test]
        public void Enrich_MatchesANameTheCharacterWroteWithoutItsArticle()
        {
            var lightTheRoom = new ConvaiActionDefinition
            {
                ActionName = "Light The Room",
                TargetRequirement = ConvaiActionTargetRequirement.None
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Light Room"), null,
                new List<ConvaiActionDefinition> { lightTheRoom });

            Assert.That(enriched.Name, Is.EqualTo("Light The Room"));
        }

        /// <summary>And an article the character added that the author never wrote.</summary>
        [Test]
        public void Enrich_MatchesANameTheCharacterGaveAnArticleItWasNotShown()
        {
            var readStatus = new ConvaiActionDefinition
            {
                ActionName = "Scan Room",
                TargetRequirement = ConvaiActionTargetRequirement.None
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Scan The Room"), null,
                new List<ConvaiActionDefinition> { readStatus });

            Assert.That(enriched.Name, Is.EqualTo("Scan Room"));
        }

        /// <summary>
        ///     The name the character actually wrote comes off before the parameters are read, not
        ///     the authored name.
        /// </summary>
        /// <remarks>
        ///     The half of this that is easy to miss. Matching the action is worth nothing if the
        ///     leftover still carries the name: the carver would read <c>Light</c> as the answer to
        ///     the first slot, and the lights would be asked for a mode nobody offered.
        /// </remarks>
        [Test]
        public void Enrich_TakesTheArticlelessNameOffBeforeReadingParameters()
        {
            var lightTheRoom = new ConvaiActionDefinition
            {
                ActionName = "Light The Room",
                TargetRequirement = ConvaiActionTargetRequirement.None,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new()
                    {
                        Name = "mode",
                        Type = ConvaiActionParameterType.Choice,
                        Choices = new List<string> { "on", "off", "toggle" }
                    }
                }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Light Room {mode: \"on\"}"), null,
                new List<ConvaiActionDefinition> { lightTheRoom });

            Assert.That(enriched.Name, Is.EqualTo("Light The Room"));
            Assert.That(enriched.Parameters["mode"].StringValue, Is.EqualTo("on"),
                "The action's own name must not be carved up as a parameter value.");
        }

        /// <summary>
        ///     Two actions that reduce to the same words are a tie, and a tie is refused rather than
        ///     guessed.
        /// </summary>
        /// <remarks>
        ///     Running one of two equally-likely actions moves a character on a coin toss. A drop is
        ///     reported in the console and leads the developer to the real problem — two actions that
        ///     differ only by an article — so a drop is the better answer.
        /// </remarks>
        [Test]
        public void Enrich_RefusesToGuessBetweenTwoActionsThatDifferOnlyByAnArticle()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Light Room", TargetRequirement = ConvaiActionTargetRequirement.None },
                new() { ActionName = "Light The Room", TargetRequirement = ConvaiActionTargetRequirement.None }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Light a Room"), null, definitions);

            Assert.That(enriched.Name, Is.EqualTo("Light a Room"),
                "Neither action may be picked when both answer to the same words.");
        }

        /// <summary>
        ///     The word scan must stop where the name stops, not run on into what the character wrote
        ///     for the parameters.
        /// </summary>
        /// <remarks>
        ///     Longest-match decides between two actions, so a name that reaches one word further by
        ///     eating a parameter label would beat the action actually named. Nothing about the tie
        ///     rule saves this: the scores genuinely differ.
        /// </remarks>
        [Test]
        public void Enrich_DoesNotLetALongerNameWinByEatingAParameterLabel()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Light The Room",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "mode", Type = ConvaiActionParameterType.String }
                    }
                },
                new() { ActionName = "Light The Room Mode", TargetRequirement = ConvaiActionTargetRequirement.None }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Light Room {mode: \"on\"}"), null, definitions);

            Assert.That(enriched.Name, Is.EqualTo("Light The Room"),
                "'mode' is a slot label, not the fourth word of an action's name.");
        }

        /// <summary>
        ///     Punctuation the match stepped over must not reappear as somebody's answer.
        /// </summary>
        [Test]
        public void Enrich_DoesNotLeaveTheNamesOwnPunctuationInAParameterValue()
        {
            var ring = new ConvaiActionDefinition
            {
                ActionName = "Ring The Bell!",
                TargetRequirement = ConvaiActionTargetRequirement.None,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "how", Type = ConvaiActionParameterType.String }
                }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Ring Bell? loud"), null,
                new List<ConvaiActionDefinition> { ring });

            Assert.That(enriched.Name, Is.EqualTo("Ring The Bell!"));
            Assert.That(enriched.Parameters["how"].StringValue, Is.EqualTo("loud"));
        }

        /// <summary>
        ///     The tolerance is for articles only — it does not make one action's name reach another's.
        /// </summary>
        [Test]
        public void Enrich_DoesNotLetTheArticleToleranceMatchADifferentAction()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Reset The Assembly", TargetRequirement = ConvaiActionTargetRequirement.None },
                new() { ActionName = "Start Over", TargetRequirement = ConvaiActionTargetRequirement.None }
            };

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Run Start"), null, definitions);

            Assert.That(enriched.Name, Is.EqualTo("Run Start"),
                "'Run Start' names neither action; a blend of two names must still be reported.");
        }

        /// <summary>
        ///     And the command that carried it is admitted rather than dropped.
        /// </summary>
        [Test]
        public void FilterExecutableBatch_AdmitsACommandThatFilledTheImplicitTargetSlot()
        {
            var compareReading = new ConvaiActionDefinition
            {
                ActionName = "Compare Reading",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Executor = Spawn("host").AddComponent<AcceptingExecutor>(),
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "low", Type = ConvaiActionParameterType.Number },
                    new() { Name = "high", Type = ConvaiActionParameterType.Number }
                }
            };

            var drops = new ConvaiActionDropCollector();
            IReadOnlyList<ConvaiActionCommand> accepted = ConvaiActionResponseParser.FilterExecutableBatch(
                new[]
                {
                    new ConvaiActionCommand("Compare Reading {low: 20} {high: 80} {target: Power Generator}")
                },
                ConfigWith("Power Generator"),
                new List<ConvaiActionDefinition> { compareReading },
                drops);

            Assert.That(accepted, Has.Count.EqualTo(1),
                "This exact command was dropped as 'the Convai Character named nothing'.");
            Assert.That(accepted[0].AdmittedTargetName, Is.EqualTo("Power Generator"));
        }

        // ── Reading without refusing ─────────────────────────────────────────────────────

        /// <summary>
        ///     A locally injected command gets the explanation, not the refusal.
        /// </summary>
        /// <remarks>
        ///     The backend path turns commands away because a stale or hallucinated one should never
        ///     reach an Action Behavior. A command written by hand in your own code is neither, so
        ///     turning it away would change what existing callers get — while the explanation is the
        ///     part that was missing and is worth having.
        /// </remarks>
        [Test]
        public void ReadWithoutRefusing_ReportsAnUnknownActionAndStillReturnsIt()
        {
            var drops = new ConvaiActionDropCollector();

            IReadOnlyList<ConvaiActionCommand> read = ConvaiActionResponseParser.ReadWithoutRefusing(
                new[] { new ConvaiActionCommand("Nonexistent Action", "The Gallery") },
                ConfigWith("The Gallery"),
                new List<ConvaiActionDefinition> { WalkTo() },
                drops);

            Assert.That(read, Has.Count.EqualTo(1), "The caller's command is still theirs to run.");
            Assert.That(drops.DroppedCount, Is.GreaterThan(0), "And they are told why nothing will run it.");
        }

        [Test]
        public void FilterExecutableBatch_StillRefusesTheSameCommand()
        {
            var drops = new ConvaiActionDropCollector();

            IReadOnlyList<ConvaiActionCommand> accepted = ConvaiActionResponseParser.FilterExecutableBatch(
                new[] { new ConvaiActionCommand("Nonexistent Action", "The Gallery") },
                ConfigWith("The Gallery"),
                new List<ConvaiActionDefinition> { WalkTo() },
                drops);

            Assert.That(accepted, Is.Empty,
                "The two paths share one reading and differ only in whether they refuse.");
            Assert.That(drops.DroppedCount, Is.GreaterThan(0));
        }

        [Test]
        public void FilterExecutableBatch_RecordsWhatItAdmittedTheCommandOn()
        {
            var drops = new ConvaiActionDropCollector();

            IReadOnlyList<ConvaiActionCommand> accepted = ConvaiActionResponseParser.FilterExecutableBatch(
                new[] { new ConvaiActionCommand("Walk To", "The Gallery") },
                ConfigWith("The Gallery"),
                new List<ConvaiActionDefinition> { ExecutableWalkTo() },
                drops);

            Assert.That(accepted, Has.Count.EqualTo(1));
            Assert.That(accepted[0].AdmittedTargetName, Is.EqualTo("The Gallery"),
                "Carried so dispatch can notice if the scene moved while the command waited.");
        }
    }
}
