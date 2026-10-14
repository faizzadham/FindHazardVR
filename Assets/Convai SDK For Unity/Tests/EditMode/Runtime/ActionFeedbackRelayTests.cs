using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Runtime;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Runtime.Embodiment;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Spoken outcome feedback, realism pack and barge-in cancellation: composer
    ///     templates, batch aggregation, cooldown/mode routing, barge-in cancellation, and the
    ///     multicast action-performance-reactor seam.
    /// </summary>
    [TestFixture]
    public class ActionFeedbackRelayTests
    {
        private readonly List<GameObject> _createdObjects = new();

        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => Convai.Runtime.Logging.ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _createdObjects.Clear();
        }

        // ── Composer: templates per failure reason ──────────────────────────────────────

        [Test]
        public void Composer_TargetMissing_BuildsExpectedFact()
        {
            ConvaiActionStepReport report = BuildReport(
                "Pick Up", ConvaiActionExecutionStatus.Failed, ConvaiActionFailureReason.TargetMissing, "lantern");

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(new[] { report });

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.Failure, outcome.Kind);
            Assert.AreEqual(ConvaiActionFailureReason.TargetMissing, outcome.FailureReason);
            StringAssert.Contains("lantern", outcome.Fact);
        }

        [Test]
        public void Composer_PathBlocked_BuildsExpectedFact()
        {
            ConvaiActionStepReport report = BuildReport(
                "Move To", ConvaiActionExecutionStatus.Failed, ConvaiActionFailureReason.PathBlocked, "door");

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(new[] { report });

            StringAssert.Contains("path is blocked", outcome.Fact);
            StringAssert.Contains("door", outcome.Fact);
        }

        [Test]
        public void Composer_Timeout_BuildsExpectedFact()
        {
            ConvaiActionStepReport report = BuildReport("Open", ConvaiActionExecutionStatus.TimedOut);

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(new[] { report });

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.Failure, outcome.Kind);
            Assert.AreEqual(ConvaiActionFailureReason.Timeout, outcome.FailureReason);
            StringAssert.Contains("in time", outcome.Fact);
        }

        [Test]
        public void Composer_Interrupted_BuildsExpectedFact()
        {
            ConvaiActionStepReport report = BuildReport("Follow", ConvaiActionExecutionStatus.Canceled);

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(new[] { report });

            Assert.AreEqual(ConvaiActionFailureReason.Interrupted, outcome.FailureReason);
            StringAssert.Contains("Follow", outcome.Fact);
        }

        // ── Composer: aggregation ────────────────────────────────────────────────────────

        [Test]
        public void Composer_AggregatesFirstHardFailure_IgnoresLaterSteps()
        {
            ConvaiActionStepReport[] reports =
            {
                BuildReport("Move To", ConvaiActionExecutionStatus.Succeeded),
                BuildReport("Pick Up", ConvaiActionExecutionStatus.Failed, ConvaiActionFailureReason.TargetMissing, "key"),
                BuildReport("Give", ConvaiActionExecutionStatus.Succeeded)
            };

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(reports);

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.Failure, outcome.Kind);
            StringAssert.Contains("key", outcome.Fact);
        }

        [Test]
        public void Composer_AllSucceeded_BuildsSuccessSummary()
        {
            ConvaiActionStepReport[] reports =
            {
                BuildReport("Move To", ConvaiActionExecutionStatus.Succeeded),
                BuildReport("Pick Up", ConvaiActionExecutionStatus.Succeeded)
            };

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(reports);

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.Success, outcome.Kind);
            Assert.IsFalse(outcome.ForceSilent);
            StringAssert.Contains("Move To", outcome.Fact);
            StringAssert.Contains("Pick Up", outcome.Fact);
        }

        [Test]
        public void Composer_UnhandledOnly_ForcesSilent_NeverNarrated()
        {
            ConvaiActionStepReport[] reports = { BuildReport("Feel", ConvaiActionExecutionStatus.Unhandled) };

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(reports);

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.Success, outcome.Kind);
            Assert.IsTrue(outcome.ForceSilent, "Unhandled-only batches must never be eligible for narration.");
        }

        [Test]
        public void Composer_EmptyReports_ProducesNone()
        {
            ConvaiActionFeedbackComposer.Outcome outcome =
                ConvaiActionFeedbackComposer.Compose(new List<ConvaiActionStepReport>());

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.None, outcome.Kind);
        }

        // ── Relay: mode routing over a real dispatcher + character ─────────────────────

        [Test]
        public async Task Relay_FailureMode_NarrateInCharacter_RoutesToMustRespond()
        {
            Fixture f = BuildFixture("char-narrate");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Failed("blocked", ConvaiActionFailureReason.PathBlocked);
            f.Source.ReplaceObjects(new List<ConvaiActionObjectDefinition> { new() { Name = "door" } });
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", TargetRequirement = ConvaiActionTargetRequirement.Object, Executor = f.Executor }
            });
            f.Relay.FailureFeedbackMode = ConvaiActionFeedbackMode.NarrateInCharacter;

            string composedFact = null;
            bool? narrated = null;
            f.Relay.OnFeedbackComposed += (fact, wasNarrated) =>
            {
                composedFact = fact;
                narrated = wasNarrated;
            };

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Move To", "door") });
            await WaitUntilAsync(() => composedFact != null);
            f.Character.DynamicContext.Flush();

            Assert.IsTrue(narrated);
            Assert.AreEqual(1, f.Connection.SentDynamicContextUpdates.Count);
            Assert.AreEqual(ConvaiRespondMode.MustRespond, f.Connection.SentDynamicContextUpdates[0].Reaction);
            Assert.AreEqual(composedFact, f.Connection.SentDynamicContextUpdates[0].Text);
        }

        [Test]
        public async Task Relay_SuccessMode_SilentContext_RoutesToSilent()
        {
            Fixture f = BuildFixture("char-silent");
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Wait", TargetRequirement = ConvaiActionTargetRequirement.None, Executor = f.Executor }
            });
            f.Relay.SuccessFeedbackMode = ConvaiActionFeedbackMode.SilentContext;

            string composedFact = null;
            bool? narrated = null;
            f.Relay.OnFeedbackComposed += (fact, wasNarrated) =>
            {
                composedFact = fact;
                narrated = wasNarrated;
            };

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Wait") });
            await WaitUntilAsync(() => composedFact != null);
            f.Character.DynamicContext.Flush();

            Assert.IsFalse(narrated);
            Assert.AreEqual(1, f.Connection.SentDynamicContextUpdates.Count);
            Assert.AreEqual(ConvaiRespondMode.Silent, f.Connection.SentDynamicContextUpdates[0].Reaction);
        }

        [Test]
        public async Task Relay_ScriptedSpeech_InvokesNarrativeSpeech()
        {
            Fixture f = BuildFixture("char-scripted");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Failed("missing", ConvaiActionFailureReason.TargetMissing);
            f.Source.ReplaceObjects(new List<ConvaiActionObjectDefinition> { new() { Name = "key" } });
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Pick Up", TargetRequirement = ConvaiActionTargetRequirement.Object, Executor = f.Executor }
            });
            f.Relay.FailureFeedbackMode = ConvaiActionFeedbackMode.ScriptedSpeech;

            bool composed = false;
            f.Relay.OnFeedbackComposed += (_, _) => composed = true;

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Pick Up", "key") });
            await WaitUntilAsync(() => composed);

            Assert.AreEqual(1, f.Connection.SentNarrativeTriggers.Count);
            StringAssert.Contains("key", f.Connection.SentNarrativeTriggers[0].WireFieldValue);
        }

        [Test]
        public async Task Relay_Cooldown_SuppressesSecondNarration()
        {
            Fixture f = BuildFixture("char-cooldown");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Failed("missing", ConvaiActionFailureReason.TargetMissing);
            f.Source.ReplaceObjects(new List<ConvaiActionObjectDefinition> { new() { Name = "key" } });
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Pick Up", TargetRequirement = ConvaiActionTargetRequirement.Object, Executor = f.Executor }
            });
            f.Relay.FailureFeedbackMode = ConvaiActionFeedbackMode.NarrateInCharacter;
            f.Relay.CooldownSeconds = 100f;

            var narrationFlags = new List<bool>();
            f.Relay.OnFeedbackComposed += (_, narrated) => narrationFlags.Add(narrated);

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Pick Up", "key") });
            await WaitUntilAsync(() => narrationFlags.Count == 1);

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Pick Up", "key") });
            await WaitUntilAsync(() => narrationFlags.Count == 2);

            Assert.IsTrue(narrationFlags[0], "First failure within cooldown window should narrate.");
            Assert.IsFalse(narrationFlags[1], "Second failure inside the cooldown window must downgrade to SilentContext.");
        }

        // ── Answers: an action that found something out ─────────────────────────────────
        //
        // An action whose whole purpose is to answer a question — read a gauge, count a group,
        // measure a distance — used to have nowhere to put the answer. The success fact was built
        // from action names alone, so the sentence the visitor asked for was discarded before
        // delivery was even considered. These lock down that it survives, and that none of the
        // relay's guards throw it away afterwards.

        [Test]
        public void Composer_SuccessWithAnswer_CarriesTheAnswerNotJustTheActionName()
        {
            ConvaiActionStepReport report = BuildAnsweredReport(
                "Read Status", "The power generator reads 62 kilowatts.");

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(new[] { report });

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.Success, outcome.Kind);
            Assert.IsTrue(outcome.HasAnswer, "A step that answered must mark the outcome as carrying an answer.");
            StringAssert.Contains("62 kilowatts", outcome.Fact);
        }

        [Test]
        public void Composer_AnsweringStep_IsRepresentedByItsAnswerRatherThanItsName()
        {
            ConvaiActionStepReport report = BuildAnsweredReport(
                "Read Status", "The power generator reads 62 kilowatts.");

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(new[] { report });

            StringAssert.DoesNotContain("You completed", outcome.Fact,
                "An action that answered is described by its answer; repeating its name adds nothing.");
        }

        [Test]
        public void Composer_SuccessWithoutAnswer_IsUnchanged()
        {
            ConvaiActionStepReport[] reports =
            {
                BuildReport("Move To", ConvaiActionExecutionStatus.Succeeded),
                BuildReport("Pick Up", ConvaiActionExecutionStatus.Succeeded)
            };

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(reports);

            Assert.IsFalse(outcome.HasAnswer);
            Assert.AreEqual("You completed: Move To, Pick Up.", outcome.Fact,
                "Actions that answer nothing must keep the exact wording shipped projects already get.");
        }

        [Test]
        public void Composer_MixedBatch_KeepsBothTheWalkAndTheAnswer()
        {
            ConvaiActionStepReport[] reports =
            {
                BuildReport("Walk To", ConvaiActionExecutionStatus.Succeeded),
                BuildAnsweredReport("Read Status", "The power generator reads 62 kilowatts.")
            };

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(reports);

            Assert.IsTrue(outcome.HasAnswer);
            StringAssert.Contains("Walk To", outcome.Fact);
            StringAssert.Contains("62 kilowatts", outcome.Fact);
        }

        [Test]
        public void Composer_AnswerThenLaterFailure_KeepsTheAnswerAsWellAsTheFailure()
        {
            // "Tell me the generator's reading, then meet me at the door." The reading succeeded and
            // was thrown away by the blocked door — the answer was produced, then erased.
            ConvaiActionStepReport[] reports =
            {
                BuildAnsweredReport("Read Status", "The power generator reads 62 kilowatts."),
                BuildReport("Walk To", ConvaiActionExecutionStatus.Failed, ConvaiActionFailureReason.PathBlocked, "door")
            };

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(reports);

            Assert.AreEqual(ConvaiActionFeedbackComposer.OutcomeKind.Failure, outcome.Kind);
            Assert.IsTrue(outcome.HasAnswer, "An answer produced before the failure is still true and still wanted.");
            StringAssert.Contains("62 kilowatts", outcome.Fact);
            StringAssert.Contains("path is blocked", outcome.Fact);
        }

        [Test]
        public void Composer_BatchRule_OneStepAskingToBeToldTellsTheWholeBatch()
        {
            ConvaiActionStepReport[] reports =
            {
                BuildAnsweredReport("Scan The Room", "You saw three crates.", ConvaiActionAnswerDelivery.RememberOnly),
                BuildAnsweredReport("Read Status", "The generator reads 62.", ConvaiActionAnswerDelivery.TellThePlayer)
            };

            ConvaiActionFeedbackComposer.Outcome outcome = ConvaiActionFeedbackComposer.Compose(reports);

            Assert.AreEqual(ConvaiActionAnswerDelivery.TellThePlayer, outcome.AnswerDelivery,
                "If any answering step asks to be told, the batch is told.");
        }

        [Test]
        public async Task Relay_TwoAnswersInsideTheCooldownWindow_AreBothVoiced()
        {
            Fixture f = BuildFixture("char-answer-cooldown");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Answered("The generator reads 62 kilowatts.");
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Read Status",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = f.Executor,
                    AnswerDelivery = ConvaiActionAnswerDelivery.TellThePlayer
                }
            });
            f.Relay.CooldownSeconds = 100f;

            var narrationFlags = new List<bool>();
            f.Relay.OnFeedbackComposed += (_, narrated) => narrationFlags.Add(narrated);

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Read Status") });
            await WaitUntilAsync(() => narrationFlags.Count == 1);

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Read Status") });
            await WaitUntilAsync(() => narrationFlags.Count == 2);

            Assert.IsTrue(narrationFlags[0], "The first answer must be voiced.");
            Assert.IsTrue(narrationFlags[1],
                "The cooldown throttles chatter, never an answer — two questions asked in quick " +
                "succession must both be answered out loud.");
        }

        [Test]
        public async Task Relay_AnswerWhileSpeaking_IsDeliveredAfterTheUtteranceRatherThanDiscarded()
        {
            Fixture f = BuildFixture("char-answer-speaking");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Answered("The generator reads 62 kilowatts.");
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Read Status",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = f.Executor,
                    AnswerDelivery = ConvaiActionAnswerDelivery.TellThePlayer
                }
            });

            var narrationFlags = new List<bool>();
            f.Relay.OnFeedbackComposed += (_, narrated) => narrationFlags.Add(narrated);

            // "Let me check…" — the character is mid-sentence when the fast lookup finishes.
            f.SetSpeaking(true);

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Read Status") });
            await WaitUntilAsync(() => f.Executor.ExecutedActions.Count == 1);
            await Task.Delay(100);

            Assert.AreEqual(0, narrationFlags.Count,
                "Nothing is delivered while the character is still talking.");

            f.SetSpeaking(false);
            await WaitUntilAsync(() => narrationFlags.Count == 1);
            f.Character.DynamicContext.Flush();

            Assert.IsTrue(narrationFlags[0],
                "Once the utterance ends the answer is spoken — it must not be silently dropped for " +
                "having arrived at a busy moment.");
            Assert.AreEqual(ConvaiRespondMode.MustRespond, f.Connection.SentDynamicContextUpdates[0].Reaction);
        }

        [Test]
        public async Task Relay_ScriptedSpeechCharacter_SpeaksTheAnswerInsteadOfItsScriptedLine()
        {
            Fixture f = BuildFixture("char-answer-scripted");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Answered("The generator reads 62 kilowatts.");
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Read Status",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = f.Executor,
                    AnswerDelivery = ConvaiActionAnswerDelivery.TellThePlayer
                }
            });
            f.Relay.SuccessFeedbackMode = ConvaiActionFeedbackMode.ScriptedSpeech;

            bool composed = false;
            f.Relay.OnFeedbackComposed += (_, _) => composed = true;

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Read Status") });
            await WaitUntilAsync(() => composed);
            f.Character.DynamicContext.Flush();

            Assert.AreEqual(0, f.Connection.SentNarrativeTriggers.Count,
                "A scripted line carries only {action} and cannot say what was found, so it must not " +
                "be used to answer a question.");
            Assert.AreEqual(1, f.Connection.SentDynamicContextUpdates.Count);
            StringAssert.Contains("62 kilowatts", f.Connection.SentDynamicContextUpdates[0].Text);
        }

        [Test]
        public async Task Relay_AnswerFromAnActionSetToRememberOnly_ReachesMemoryWithoutBeingSpoken()
        {
            Fixture f = BuildFixture("char-answer-remember");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Answered("You saw three crates.");
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Scan The Room",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = f.Executor,
                    AnswerDelivery = ConvaiActionAnswerDelivery.RememberOnly
                }
            });
            f.Relay.SuccessFeedbackMode = ConvaiActionFeedbackMode.NarrateInCharacter;

            bool? narrated = null;
            f.Relay.OnFeedbackComposed += (_, wasNarrated) => narrated = wasNarrated;

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Scan The Room") });
            await WaitUntilAsync(() => narrated != null);
            f.Character.DynamicContext.Flush();

            Assert.IsFalse(narrated, "The action asked to be remembered, and that outranks a talkative character.");
            Assert.AreEqual(1, f.Connection.SentDynamicContextUpdates.Count,
                "An answer that is not spoken is still an answer — it must reach the character's memory.");
            Assert.AreEqual(ConvaiRespondMode.Silent, f.Connection.SentDynamicContextUpdates[0].Reaction);
        }

        [Test]
        public async Task Relay_AnswerSetToMentionIfRelevant_LetsTheCharacterJudge()
        {
            Fixture f = BuildFixture("char-answer-auto");
            f.Executor.ResultToReturn = ConvaiActionExecutionResult.Answered("You saw three crates.");
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Scan The Room",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = f.Executor,
                    AnswerDelivery = ConvaiActionAnswerDelivery.MentionIfRelevant
                }
            });

            bool composed = false;
            f.Relay.OnFeedbackComposed += (_, _) => composed = true;

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Scan The Room") });
            await WaitUntilAsync(() => composed);
            f.Character.DynamicContext.Flush();

            Assert.AreEqual(1, f.Connection.SentDynamicContextUpdates.Count);
            Assert.AreEqual(ConvaiRespondMode.Auto, f.Connection.SentDynamicContextUpdates[0].Reaction,
                "'Mention if relevant' is the one delivery that hands the judgement to the character.");
        }

        // ── Barge-in cancellation ────────────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_CancelOnUserSpeech_ClearsInFlightAndQueuedWork()
        {
            Fixture f = BuildFixture("char-bargein");
            f.Executor.DelayMs = 2000;
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Wait", TargetRequirement = ConvaiActionTargetRequirement.None, Executor = f.Executor }
            });
            SetPrivateField(f.Dispatcher, "_cancelOnUserSpeech", true);

            string interrupted = null;
            f.Dispatcher.OnCancelledByUserSpeech += name => interrupted = name;

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Wait"), new ConvaiActionCommand("Wait") });
            await WaitUntilAsync(() => f.Executor.ExecutedActions.Count == 1);

            InvokePrivate(f.Dispatcher, "HandlePlayerSpeakingStateChanged", PlayerSpeakingStateChanged.StartedSpeaking());

            Assert.IsNotNull(interrupted);
            StringAssert.Contains("Wait", interrupted);

            await Task.Delay(100);
            Assert.AreEqual(1, f.Executor.ExecutedActions.Count, "The second queued step must never execute after a barge-in cancel.");
        }

        [Test]
        public async Task Relay_BargeIn_EmitsSilentInterruptedFact_AndSuppressesGenericAggregatedFact()
        {
            Fixture f = BuildFixture("char-bargein-relay");
            f.Executor.DelayMs = 2000;
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Wait", TargetRequirement = ConvaiActionTargetRequirement.None, Executor = f.Executor }
            });
            SetPrivateField(f.Dispatcher, "_cancelOnUserSpeech", true);
            f.Relay.FailureFeedbackMode = ConvaiActionFeedbackMode.NarrateInCharacter;

            var composed = new List<(string Fact, bool Narrated)>();
            f.Relay.OnFeedbackComposed += (fact, narrated) => composed.Add((fact, narrated));

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Wait") });
            await WaitUntilAsync(() => f.Executor.ExecutedActions.Count == 1);

            InvokePrivate(f.Dispatcher, "HandlePlayerSpeakingStateChanged", PlayerSpeakingStateChanged.StartedSpeaking());

            await WaitUntilAsync(() => composed.Count >= 1, timeoutMs: 500);
            await Task.Delay(150);

            Assert.AreEqual(1, composed.Count,
                "Only the barge-in fact should be emitted; the generic aggregated Canceled fact must be suppressed.");
            StringAssert.Contains("because the player spoke", composed[0].Fact);
            Assert.IsFalse(composed[0].Narrated);
        }

        // ── Realism pack: multicast reactor seam ────────────────────────────────────────

        [Test]
        public void EmbodimentContext_ActionPerformanceReactor_NotifiesAllRegisteredReactors()
        {
            var go = new GameObject("ActionFeedbackRelayTests_MulticastContext");
            _createdObjects.Add(go);

            EmbodimentContext context = go.AddComponent<EmbodimentContext>();
            var reactorA = new FakeActionPerformanceReactor();
            var reactorB = new FakeActionPerformanceReactor();
            context.Contribute<IActionPerformanceReactor>(reactorA);
            context.Contribute<IActionPerformanceReactor>(reactorB);

            context.NotifyActionBatchStarted();
            context.NotifyActionTargetAcquired("lever", new Vector3(1f, 2f, 3f));
            context.NotifyActionOutcome(true);

            Assert.IsTrue(reactorA.BatchStarted);
            Assert.IsTrue(reactorB.BatchStarted);
            Assert.AreEqual("lever", reactorA.LastTargetName);
            Assert.AreEqual("lever", reactorB.LastTargetName);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), reactorA.LastTargetPosition);
            CollectionAssert.AreEqual(new[] { true }, reactorA.OutcomeCalls);
            CollectionAssert.AreEqual(new[] { true }, reactorB.OutcomeCalls);

            context.Withdraw<IActionPerformanceReactor>(reactorA);
            context.NotifyActionOutcome(false);

            CollectionAssert.AreEqual(new[] { true }, reactorA.OutcomeCalls);
            CollectionAssert.AreEqual(new[] { true, false }, reactorB.OutcomeCalls);
        }

        [Test]
        public async Task Dispatcher_PerformanceReactions_NotifyRegisteredReactorThroughBatchLifecycle()
        {
            Fixture f = BuildFixture("char-perf");
            var reactor = new FakeActionPerformanceReactor();
            f.Context.Contribute<IActionPerformanceReactor>(reactor);

            var cube = new GameObject("ActionFeedbackRelayTests_char-perf_Cube");
            _createdObjects.Add(cube);
            f.Source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "cube", GameObjectReference = cube }
            });
            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", TargetRequirement = ConvaiActionTargetRequirement.Object, Executor = f.Executor }
            });

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Move To", "cube") });
            await WaitUntilAsync(() => reactor.OutcomeCalls.Count > 0);

            Assert.IsTrue(reactor.BatchStarted);
            Assert.AreEqual("cube", reactor.LastTargetName);
            Assert.IsTrue(reactor.OutcomeCalls[0]);
        }

        [Test]
        public async Task Dispatcher_PerformanceReactionsDisabled_NeverNotifiesReactor()
        {
            Fixture f = BuildFixture("char-perf-off");
            var reactor = new FakeActionPerformanceReactor();
            f.Context.Contribute<IActionPerformanceReactor>(reactor);
            SetPrivateField(f.Dispatcher, "_enablePerformanceReactions", false);

            f.Source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Wait", TargetRequirement = ConvaiActionTargetRequirement.None, Executor = f.Executor }
            });

            f.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Wait") });
            await WaitUntilAsync(() => f.Executor.ExecutedActions.Count == 1);
            await Task.Delay(50);

            Assert.IsFalse(reactor.BatchStarted);
            Assert.AreEqual(0, reactor.OutcomeCalls.Count);
        }

        // ── Fixture ──────────────────────────────────────────────────────────────────────

        private sealed class Fixture
        {
            public GameObject GameObject { get; set; }
            public ConvaiCharacter Character { get; set; }
            public EmbodimentContext Context { get; set; }
            public ConvaiActionConfigSource Source { get; set; }
            public ConvaiActionDispatcher Dispatcher { get; set; }
            public ConvaiActionFeedbackRelay Relay { get; set; }
            public RecordingExecutor Executor { get; set; }
            public MockRoomConnectionService Connection { get; set; }
            public EventHub EventHub { get; set; }

            /// <summary>
            ///     Drives the character's speech state down the same path the backend uses, so a test
            ///     exercising "an answer arrived while the character was talking" goes through
            ///     <see cref="ConvaiCharacter.IsSpeaking" /> and the real speech events rather than
            ///     poking a private field.
            /// </summary>
            public void SetSpeaking(bool speaking) =>
                EventHub.Publish(new CharacterSpeechStateChanged(
                    Character.CharacterId, speaking, DateTime.UtcNow));
        }

        private Fixture BuildFixture(string characterId)
        {
            var go = new GameObject($"ActionFeedbackRelayTests_{characterId}");
            _createdObjects.Add(go);

            ConvaiCharacter character = go.AddComponent<ConvaiCharacter>();
            SetPrivateField(character, "_characterId", characterId);
            SetPrivateField(character, "_characterName", characterId);

            EmbodimentContext context = go.AddComponent<EmbodimentContext>();
            ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
            RecordingExecutor executor = go.AddComponent<RecordingExecutor>();
            ConvaiActionDispatcher dispatcher = go.AddComponent<ConvaiActionDispatcher>();
            ConvaiActionFeedbackRelay relay = go.AddComponent<ConvaiActionFeedbackRelay>();

            var eventHub = new EventHub(new ImmediateScheduler());
            var connection = new MockRoomConnectionService();
            var audio = new MockRoomAudioService();
            var agents = new MockAgentRegistry();

            character.InjectDependencies(new ConvaiCharacterDependencies(eventHub, connection, audio, agents));

            connection.RaiseConnected();
            eventHub.Publish(CharacterReady.Create(characterId, $"participant-{characterId}"));

            return new Fixture
            {
                GameObject = go,
                Character = character,
                Context = context,
                Source = source,
                Dispatcher = dispatcher,
                Relay = relay,
                Executor = executor,
                Connection = connection,
                EventHub = eventHub
            };
        }

        /// <summary>
        ///     Builds a step report for an action that answered a question, optionally with the
        ///     per-action delivery its definition authorises.
        /// </summary>
        private static ConvaiActionStepReport BuildAnsweredReport(
            string actionName,
            string answer,
            ConvaiActionAnswerDelivery delivery = ConvaiActionAnswerDelivery.UseCharacterSetting)
        {
            var definition = new ConvaiActionDefinition { ActionName = actionName, AnswerDelivery = delivery };
            var invocation = new ConvaiActionInvocation(
                new ConvaiActionCommand(actionName), definition, null, null, 0, 0);

            return new ConvaiActionStepReport(
                invocation,
                ConvaiActionExecutionResult.Answered(answer),
                batchAborted: false,
                failureMessage: string.Empty);
        }

        private static ConvaiActionStepReport BuildReport(
            string actionName,
            ConvaiActionExecutionStatus status,
            ConvaiActionFailureReason reason = ConvaiActionFailureReason.None,
            string targetName = null)
        {
            var definition = new ConvaiActionDefinition { ActionName = actionName };
            var command = new ConvaiActionCommand(actionName);
            ConvaiResolvedActionTarget target = targetName == null
                ? null
                : ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition { Name = targetName });

            var invocation = new ConvaiActionInvocation(command, definition, target, null, 0, 0);

            ConvaiActionExecutionResult result = status switch
            {
                ConvaiActionExecutionStatus.Succeeded => ConvaiActionExecutionResult.Succeeded(),
                ConvaiActionExecutionStatus.Unhandled => ConvaiActionExecutionResult.Unhandled(),
                ConvaiActionExecutionStatus.TimedOut => ConvaiActionExecutionResult.TimedOut(),
                ConvaiActionExecutionStatus.Canceled => ConvaiActionExecutionResult.Canceled(),
                _ => ConvaiActionExecutionResult.Failed("custom failure message", reason)
            };

            return new ConvaiActionStepReport(invocation, result, batchAborted: false, failureMessage: string.Empty);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new AssertionException("Timed out waiting for condition.");
                await Task.Delay(10);
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Expected field '{fieldName}' on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, $"Missing method {methodName}.");
            method.Invoke(target, args);
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class RecordingExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public readonly List<string> ExecutedActions = new();
            public int DelayMs { get; set; }
            public ConvaiActionExecutionResult ResultToReturn { get; set; } = ConvaiActionExecutionResult.Succeeded();

            public async Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation,
                CancellationToken cancellationToken)
            {
                ExecutedActions.Add(invocation.Command?.ToString() ?? invocation.Definition?.ActionName ?? "?");

                if (DelayMs > 0)
                    await Task.Delay(DelayMs, cancellationToken);

                return ResultToReturn;
            }
        }

        private sealed class FakeActionPerformanceReactor : IActionPerformanceReactor
        {
            public bool BatchStarted { get; private set; }
            public string LastTargetName { get; private set; }
            public Vector3 LastTargetPosition { get; private set; }
            public readonly List<bool> OutcomeCalls = new();

            public void OnActionBatchStarted() => BatchStarted = true;

            public void OnActionTargetAcquired(string targetName, Vector3 worldPosition)
            {
                LastTargetName = targetName;
                LastTargetPosition = worldPosition;
            }

            public void OnActionOutcome(bool success) => OutcomeCalls.Add(success);
        }
    }
}
