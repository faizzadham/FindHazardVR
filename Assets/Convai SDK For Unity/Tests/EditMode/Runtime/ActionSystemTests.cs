using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Abstractions;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Runtime.Actions;
using Convai.Runtime.Logging;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.Networking.Media;
using Convai.Runtime.Room;
using Convai.Sample.Behaviors;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ActionSystemTests
    {
        private TestLogSink _logSink;

        [SetUp]
        public void SetUp()
        {
            _logSink = new TestLogSink();
            ConvaiLogger.RegisterSink(_logSink);
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiLogger.UnregisterSink(_logSink);
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith("ActionSystemTests_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        // ── Resolution via dispatcher (ConvaiResolvedAction is internal) ─────────────────

        [Test]
        public async Task ResolvedAction_ResolvesObjectTarget_ByExactName()
        {
            var fixture = CreateDispatcherFixtureWithObjects(
                actionNames: new[] { "Move To" },
                objectNames: new[] { "cube" });

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.ResolvedTarget?.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
            Assert.That(captured.ResolvedTarget?.ObjectBinding?.Name, Is.EqualTo("cube"));
            Assert.That(captured.Command.Name, Is.EqualTo("Move To"));
        }

        [Test]
        public async Task ResolvedAction_UsesReferenceParameter_WhenCommandTargetIsEmpty()
        {
            var fixture = CreateDispatcherFixtureWithObjects(
                actionNames: new[] { "Move To" },
                objectNames: new[] { "cube" });
            ConvaiActionConfigSource source = fixture.GameObject.GetComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = fixture.GameObject.GetComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.Object,
                    Executor = executor,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new()
                        {
                            Name = "destination",
                            Type = ConvaiActionParameterType.Reference,
                            Connector = "toward"
                        }
                    }
                }
            });

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[]
            {
                new ConvaiActionCommand("Move To")
                {
                    Parameters = new Dictionary<string, ConvaiActionParameterValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["destination"] = new()
                        {
                            Type = ConvaiActionParameterType.Reference,
                            RawValue = "cube",
                            StringValue = "cube",
                            ResolvedReference = new ConvaiActionParameterReference("cube", ConvaiActionTargetKind.Object)
                        }
                    }
                }
            });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.ResolvedTarget?.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
            Assert.That(captured.ResolvedTarget?.Name, Is.EqualTo("cube"));
        }

        [Test]
        public async Task ResolvedAction_EnrichesRawNameSuffix_WhenRtviDidNotPreprocessCommand()
        {
            var fixture = CreateDispatcherFixtureWithObjects(
                actionNames: new[] { "Move To" },
                objectNames: new[] { "cube" });
            ConvaiActionConfigSource source = fixture.GameObject.GetComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = fixture.GameObject.GetComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.Object,
                    Executor = executor,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "destination", Type = ConvaiActionParameterType.Reference }
                    }
                }
            });

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Move To cube") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.Definition?.ActionName, Is.EqualTo("Move To"));
            Assert.That(captured.Command.Name, Is.EqualTo("Move To"));
            Assert.That(captured.GetString("destination"), Is.EqualTo("cube"));
            Assert.That(captured.ResolvedTarget?.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
        }

        [Test]
        public async Task ResolvedAction_HandlesMissingTarget()
        {
            var fixture = CreateDispatcherFixtureWithObjects(actionNames: new[] { "Dance" });

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Dance") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.ResolvedTarget, Is.Null);
            Assert.That(captured.Command.Name, Is.EqualTo("Dance"));
        }

        [Test]
        public async Task ResolvedAction_LeavesUnresolvedTarget_WhenUnknown()
        {
            var fixture = CreateDispatcherFixtureWithObjects(
                actionNames: new[] { "Move To" },
                objectNames: new[] { "cube" });

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "unknown") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.ResolvedTarget, Is.Null);
            Assert.That(captured.Command.Target, Is.EqualTo("unknown"));
        }

        [Test]
        public async Task ResolvedAction_ResolvesCharacterTarget_ByExactName()
        {
            var fixture = CreateDispatcherFixtureWithCharacters(
                actionNames: new[] { "Follow" },
                characterNames: new[] { "Player" });

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Follow", "Player") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.ResolvedTarget?.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
            Assert.That(captured.ResolvedTarget?.CharacterBinding?.Name, Is.EqualTo("Player"));
        }

        [Test]
        public async Task ResolvedAction_UsesRequiredCharacterKind_WhenObjectAndCharacterNamesCollide()
        {
            var fixture = CreateDispatcherFixtureWithMixedTargets(
                requirement: ConvaiActionTargetRequirement.Character,
                targetName: "SharedTarget",
                addObject: true,
                addCharacter: true);

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "SharedTarget") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.ResolvedTarget?.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
            Assert.That(captured.ResolvedTarget?.CharacterBinding?.Name, Is.EqualTo("SharedTarget"));
        }

        [Test]
        public async Task ResolvedAction_UsesRequiredObjectKind_WhenObjectAndCharacterNamesCollide()
        {
            var fixture = CreateDispatcherFixtureWithMixedTargets(
                requirement: ConvaiActionTargetRequirement.Object,
                targetName: "SharedTarget",
                addObject: true,
                addCharacter: true);

            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepStarted.AddListener(inv => captured = inv);
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "SharedTarget") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.ResolvedTarget?.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
            Assert.That(captured.ResolvedTarget?.ObjectBinding?.Name, Is.EqualTo("SharedTarget"));
        }

        // ── Queue / policy / cancellation ─────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_ExecutesBatchesSequentially_WithQueuePolicy()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            RecordingActionExecutor executor = fixture.Executor;
            executor.DelayMs = 15;

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube"), CreateAction("Pick Up", "cube") });
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Drop", "cube") });
            await WaitUntilAsync(() => executor.ExecutedActions.Count == 3);

            CollectionAssert.AreEqual(
                new[] { "Move To cube", "Pick Up cube", "Drop cube" },
                executor.ExecutedActions);
        }

        [Test]
        public async Task Dispatcher_DropIncomingPolicy_IgnoresSecondBatchWhileBusy()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.DropIncoming);
            RecordingActionExecutor executor = fixture.Executor;
            executor.DelayMs = 25;

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Drop", "cube") });
            await WaitUntilAsync(() => executor.ExecutedActions.Count == 1);

            CollectionAssert.AreEqual(new[] { "Move To cube" }, executor.ExecutedActions);
        }

        [Test]
        public async Task Dispatcher_ReplaceCurrentPolicy_RunsReplacementBatchAfterCancellingActiveBatch()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.ReplaceCurrent);
            RecordingActionExecutor executor = fixture.Executor;
            // Long enough that the replacement batch reliably arrives while the first step is
            // still in flight, even when the editor hitches between the waits below.
            executor.DelayMs = 1000;

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => executor.ExecutedActions.Contains("Move To cube"), timeoutMs: 2000);
            await Task.Delay(20);
            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Drop", "cube") });
            await WaitUntilAsync(() => executor.ExecutedActions.Contains("Drop cube"), timeoutMs: 4000);

            Assert.That(executor.CancellationObserved, Is.True,
                "ReplaceCurrent should cancel the in-flight step before running the replacement batch.");
            CollectionAssert.AreEqual(new[] { "Move To cube", "Drop cube" }, executor.ExecutedActions);
        }

        [Test]
        public async Task Dispatcher_CancelsRunningBatch_WhenDisabled()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            RecordingActionExecutor executor = fixture.Executor;
            executor.DelayMs = 250;

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });

            // Wait for the step to actually be in flight rather than sleeping a fixed 40ms and
            // hoping. Under a loaded full-suite run the batch had not always started yet, so the
            // disable below landed before there was anything to cancel and the assert failed for a
            // reason that had nothing to do with cancellation. ExecutedActions is appended at the
            // top of ExecuteAsync, so it is the earliest observable "this step is running".
            await WaitUntilAsync(() => executor.ExecutedActions.Count == 1, timeoutMs: 4000);

            fixture.GameObject.SetActive(false);

            // Likewise: observe the cancellation instead of assuming it lands inside a fixed window.
            await WaitUntilAsync(() => executor.CancellationObserved, timeoutMs: 4000);

            Assert.That(executor.CancellationObserved, Is.True);
        }

        [Test]
        public async Task Dispatcher_FailsStep_WhenNoDefinitionMatchesAction()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepFailed.AddListener(inv => captured = inv);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Unknown Action", "cube") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.Command.Name, Is.EqualTo("Unknown Action"));
        }

        // ── Definition lookup ─────────────────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_FiresFailed_WhenExecutorIsNull()
        {
            var fixture = CreateDispatcherFixtureWithNullExecutor(actionName: "Dance");
            bool failedFired = false;
            fixture.Dispatcher.OnStepFailed.AddListener(_ => failedFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Dance") });
            await WaitUntilAsync(() => failedFired);

            Assert.That(failedFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_FiresFailed_WhenExecutorDoesNotImplementInterface()
        {
            var fixture = CreateDispatcherFixtureWithNonExecutorMonoBehaviour(actionName: "Dance");
            bool failedFired = false;
            fixture.Dispatcher.OnStepFailed.AddListener(_ => failedFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Dance") });
            await WaitUntilAsync(() => failedFired);

            Assert.That(failedFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_LooksUpDefinition_CaseInsensitive()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            ConvaiActionInvocation captured = null;
            fixture.Dispatcher.OnStepSucceeded.AddListener(inv => captured = inv);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("MOVE TO", "cube") });
            await WaitUntilAsync(() => captured != null);

            Assert.That(captured.Definition?.ActionName, Is.EqualTo("Move To"));
        }

        // ── Target validation ─────────────────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_FiresFailed_WhenObjectRequiredButNoTarget()
        {
            var fixture = CreateDispatcherFixtureWithRequirement(
                ConvaiActionTargetRequirement.Object,
                includeObjectInConfig: false);
            bool failedFired = false;
            fixture.Dispatcher.OnStepFailed.AddListener(_ => failedFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To") });
            await WaitUntilAsync(() => failedFired);

            Assert.That(failedFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_FiresFailed_WhenObjectRequiredButCharacterResolved()
        {
            var fixture = CreateDispatcherFixtureWithMixedTargets(
                requirement: ConvaiActionTargetRequirement.Object,
                targetName: "Player",
                addObject: false,
                addCharacter: true);
            bool failedFired = false;
            fixture.Dispatcher.OnStepFailed.AddListener(_ => failedFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "Player") });
            await WaitUntilAsync(() => failedFired);

            Assert.That(failedFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_Succeeds_WhenNoneRequirementAndNoTarget()
        {
            var fixture = CreateDispatcherFixtureWithRequirement(
                ConvaiActionTargetRequirement.None,
                includeObjectInConfig: false);
            bool succeededFired = false;
            fixture.Dispatcher.OnStepSucceeded.AddListener(_ => succeededFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Dance") });
            await WaitUntilAsync(() => succeededFired);

            Assert.That(succeededFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_Succeeds_WhenEitherRequirementAndObjectTarget()
        {
            var fixture = CreateDispatcherFixtureWithMixedTargets(
                requirement: ConvaiActionTargetRequirement.Either,
                targetName: "cube",
                addObject: true,
                addCharacter: false);
            bool succeededFired = false;
            fixture.Dispatcher.OnStepSucceeded.AddListener(_ => succeededFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => succeededFired);

            Assert.That(succeededFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_Succeeds_WhenEitherRequirementAndCharacterTarget()
        {
            var fixture = CreateDispatcherFixtureWithMixedTargets(
                requirement: ConvaiActionTargetRequirement.Either,
                targetName: "Player",
                addObject: false,
                addCharacter: true);
            bool succeededFired = false;
            fixture.Dispatcher.OnStepSucceeded.AddListener(_ => succeededFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "Player") });
            await WaitUntilAsync(() => succeededFired);

            Assert.That(succeededFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_EmitsStepCompletedReport_WhenTargetRequirementFails()
        {
            var fixture = CreateDispatcherFixtureWithMixedTargets(
                requirement: ConvaiActionTargetRequirement.Object,
                targetName: "Player",
                addObject: false,
                addCharacter: true);
            SetPrivateField(fixture.Dispatcher, "_failurePolicy", ConvaiActionBatchFailurePolicy.StopBatch);
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "Player") });
            await WaitUntilAsync(() => completedReport != null);

            Assert.That(completedReport.Invocation.Command.Name, Is.EqualTo("Move To"));
            Assert.That(completedReport.Invocation.Command.Target, Is.EqualTo("Player"));
            Assert.That(completedReport.Invocation.ResolvedTarget.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed));
            Assert.That(completedReport.BatchAborted, Is.True);
            StringAssert.Contains("Action 'Move To'", completedReport.FailureMessage);
            StringAssert.Contains("target 'Player'", completedReport.FailureMessage);
            StringAssert.Contains("required Object", completedReport.FailureMessage);
            StringAssert.Contains("resolved Character", completedReport.FailureMessage);
            StringAssert.Contains("batch will abort", completedReport.FailureMessage);
        }

        [Test]
        public async Task DebugProbe_RecordsFailureReason_WhenTargetRequirementFails()
        {
            var fixture = CreateDispatcherFixtureWithMixedTargets(
                requirement: ConvaiActionTargetRequirement.Object,
                targetName: "Player",
                addObject: false,
                addCharacter: true);
            SetPrivateField(fixture.Dispatcher, "_failurePolicy", ConvaiActionBatchFailurePolicy.StopBatch);
            ConvaiActionDebugProbe probe = fixture.GameObject.AddComponent<ConvaiActionDebugProbe>();
            SetPrivateField(probe, "_logToConsole", false);
            SetPrivateField(probe, "_character", fixture.GameObject.GetComponent<ConvaiCharacter>());
            SetPrivateField(probe, "_dispatcher", fixture.Dispatcher);
            InvokePrivateMethod(probe, "OnEnable");

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "Player") });
            await WaitUntilAsync(() => GetPrivateField<string>(probe, "_lastFailureReason")?.Length > 0);

            string lastFailureReason = GetPrivateField<string>(probe, "_lastFailureReason");
            StringAssert.Contains("Action 'Move To'", lastFailureReason);
            StringAssert.Contains("target 'Player'", lastFailureReason);
            StringAssert.Contains("required Object", lastFailureReason);
            StringAssert.Contains("resolved Character", lastFailureReason);
        }

        // ── Failure policy ────────────────────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_StopBatch_AbortsBatchOnStepFailed()
        {
            var fixture = CreateDispatcherFixtureWithFailure(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.StopBatch);
            bool batchAborted = false;
            fixture.Dispatcher.OnBatchAborted.AddListener(() => batchAborted = true);

            fixture.Dispatcher.EnqueueActions(new[]
            {
                CreateAction("Move To", "cube"),
                CreateAction("Pick Up", "cube")
            });
            await WaitUntilAsync(() => batchAborted);

            Assert.That(batchAborted, Is.True);
            Assert.That(fixture.Executor.ExecutedActions.Count, Is.EqualTo(1),
                "Only first step should run before abort");
        }

        [Test]
        public async Task Dispatcher_ContinueBatch_ContinuesAfterStepFailed()
        {
            var fixture = CreateDispatcherFixtureWithFailure(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.ContinueBatch);
            bool batchCompleted = false;
            fixture.Dispatcher.OnBatchCompleted.AddListener(() => batchCompleted = true);

            fixture.Dispatcher.EnqueueActions(new[]
            {
                CreateAction("Move To", "cube"),
                CreateAction("Pick Up", "cube")
            });
            await WaitUntilAsync(() => batchCompleted);

            Assert.That(batchCompleted, Is.True);
            Assert.That(fixture.Executor.ExecutedActions.Count, Is.EqualTo(2),
                "Both steps should run under ContinueBatch");
        }

        [Test]
        public async Task Dispatcher_SuccessWithMessage_CompletesBatchWithoutAbort()
        {
            var fixture = CreateDispatcherFixture(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.StopBatch);
            fixture.Executor.ResultToReturn = ConvaiActionExecutionResult.Succeeded("No held object to drop.");
            bool batchAborted = false;
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnBatchAborted.AddListener(() => batchAborted = true);
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[]
            {
                CreateAction("Drop"),
                CreateAction("Move To", "cube")
            });
            await WaitUntilAsync(() => completedReport != null && fixture.Executor.ExecutedActions.Count == 2);

            Assert.That(batchAborted, Is.False);
            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(completedReport.Message, Is.EqualTo("No held object to drop."));
            Assert.That(completedReport.FailureMessage, Is.Empty);
        }

        [Test]
        public async Task Dispatcher_PerActionContinueOverride_ContinuesWhenGlobalStopsBatch()
        {
            var fixture = CreateDispatcherFixtureWithFailure(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.StopBatch);
            ConvaiActionConfigSource source = fixture.GameObject.GetComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = fixture.GameObject.GetComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = executor,
                    FailurePolicyOverride = ConvaiActionFailurePolicyOverride.ContinueBatch
                },
                new()
                {
                    ActionName = "Pick Up",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = executor
                }
            });
            // Both steps fail; the batch therefore ends aborted (the un-overridden second step
            // stops it) — the point under test is that the ContinueBatch override on the first
            // step let the second step execute at all.
            bool batchEnded = false;
            fixture.Dispatcher.OnBatchCompleted.AddListener(() => batchEnded = true);
            fixture.Dispatcher.OnBatchAborted.AddListener(() => batchEnded = true);

            fixture.Dispatcher.EnqueueActions(new[]
            {
                CreateAction("Move To", "cube"),
                CreateAction("Pick Up", "cube")
            });
            await WaitUntilAsync(() => batchEnded);

            Assert.That(fixture.Executor.ExecutedActions.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task Dispatcher_PerActionStopOverride_AbortsWhenGlobalContinuesBatch()
        {
            var fixture = CreateDispatcherFixtureWithFailure(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.ContinueBatch);
            ConvaiActionConfigSource source = fixture.GameObject.GetComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = fixture.GameObject.GetComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = executor,
                    FailurePolicyOverride = ConvaiActionFailurePolicyOverride.StopBatch
                },
                new()
                {
                    ActionName = "Pick Up",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = executor
                }
            });
            bool batchAborted = false;
            fixture.Dispatcher.OnBatchAborted.AddListener(() => batchAborted = true);

            fixture.Dispatcher.EnqueueActions(new[]
            {
                CreateAction("Move To", "cube"),
                CreateAction("Pick Up", "cube")
            });
            await WaitUntilAsync(() => batchAborted);

            Assert.That(fixture.Executor.ExecutedActions.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Dispatcher_StopBatch_AbortsOnUnhandledResult()
        {
            var fixture = CreateDispatcherFixture(
                batchPolicy: ConvaiActionBatchPolicy.Queue,
                failurePolicy: ConvaiActionBatchFailurePolicy.StopBatch);
            fixture.Executor.ResultToReturn = ConvaiActionExecutionResult.Unhandled("test unhandled");
            bool batchAborted = false;
            fixture.Dispatcher.OnBatchAborted.AddListener(() => batchAborted = true);

            fixture.Dispatcher.EnqueueActions(new[]
            {
                CreateAction("Move To", "cube"),
                CreateAction("Move To", "cube")
            });
            await WaitUntilAsync(() => batchAborted);

            Assert.That(batchAborted, Is.True,
                "Unhandled executor result under StopBatch should abort the batch");
        }

        // ── Timeout ───────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_FiresTimedOut_WhenStepExceedsTimeout()
        {
            var fixture = CreateDispatcherFixtureWithTimeout(
                timeoutSeconds: 0.05f,
                executorDelayMs: 200);
            ConvaiActionInvocation failedInvocation = null;
            fixture.Dispatcher.OnStepFailed.AddListener(inv => failedInvocation = inv);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => failedInvocation != null, timeoutMs: 2000);

            Assert.That(failedInvocation, Is.Not.Null);
        }

        [Test]
        public async Task Dispatcher_TimedOut_TriggersBatchAbort_WithStopBatchPolicy()
        {
            var fixture = CreateDispatcherFixtureWithTimeout(
                timeoutSeconds: 0.05f,
                executorDelayMs: 200,
                failurePolicy: ConvaiActionBatchFailurePolicy.StopBatch);
            bool batchAborted = false;
            fixture.Dispatcher.OnBatchAborted.AddListener(() => batchAborted = true);

            fixture.Dispatcher.EnqueueActions(new[]
            {
                CreateAction("Move To", "cube"),
                CreateAction("Pick Up", "cube")
            });
            await WaitUntilAsync(() => batchAborted, timeoutMs: 2000);

            Assert.That(batchAborted, Is.True);
        }

        [Test]
        public async Task Dispatcher_AppliesItsOwnTimeout_WhenActionAuthoredNone()
        {
            // The failure this guards is silent and permanent: an action behavior that never
            // returns holds the step open, every later batch queues behind it, and nothing is
            // reported — the character simply stops acting for the rest of the session. Nearly no
            // action authors a timeout, so the character's own limit is what has to answer.
            var fixture = CreateDispatcherFixtureWithTimeout(
                timeoutSeconds: 0f,
                executorDelayMs: 400);
            SetPrivateField(fixture.Dispatcher, "_defaultStepTimeoutSeconds", 0.05f);
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => completedReport != null, timeoutMs: 2000);

            Assert.That(completedReport, Is.Not.Null, "A step that never finishes must still be reported.");
            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.TimedOut));
            StringAssert.Contains("its own Timeout Seconds", completedReport.FailureMessage,
                "The character's safety net must not be described as the action's own limit.");
        }

        [Test]
        public async Task Dispatcher_LetsActionOutlastItsCharacterDefault_WhenItAuthorsItsOwnTimeout()
        {
            // An action that deliberately runs long — a walk across a level, a directed sequence —
            // says so with its own Timeout Seconds, and that must beat the character's net in both
            // directions. A net that silently capped authored intent would be a worse bug than the
            // one it was added to fix.
            var fixture = CreateDispatcherFixtureWithTimeout(
                timeoutSeconds: 5f,
                executorDelayMs: 120);
            SetPrivateField(fixture.Dispatcher, "_defaultStepTimeoutSeconds", 0.05f);
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => completedReport != null, timeoutMs: 3000);

            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
        }

        [Test]
        public async Task Dispatcher_BatchCancellation_NotMistakenForTimeout()
        {
            var fixture = CreateDispatcherFixtureWithTimeout(
                timeoutSeconds: 5f,
                executorDelayMs: 500);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await Task.Delay(40);
            fixture.GameObject.SetActive(false);
            await Task.Delay(40);

            Assert.That(fixture.Executor.CancellationObserved, Is.True,
                "Batch cancellation should be observed by the executor, not silently timed out");
        }

        // ── Result events ─────────────────────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_FiresOnStepSucceeded_OnSuccess()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            bool succeededFired = false;
            fixture.Dispatcher.OnStepSucceeded.AddListener(_ => succeededFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => succeededFired);

            Assert.That(succeededFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_EmitsStepCompletedReport_OnSuccess()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => completedReport != null);

            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(completedReport.BatchAborted, Is.False);
            Assert.That(completedReport.Message, Is.Empty);
            Assert.That(completedReport.FailureMessage, Is.Empty);
        }

        [Test]
        public async Task Dispatcher_FiresOnStepFailed_OnFailure()
        {
            var fixture = CreateDispatcherFixtureWithFailure(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.ContinueBatch);
            bool failedFired = false;
            fixture.Dispatcher.OnStepFailed.AddListener(_ => failedFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => failedFired);

            Assert.That(failedFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_EmitsStepCompletedReport_OnFailure()
        {
            var fixture = CreateDispatcherFixtureWithFailure(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.StopBatch);
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => completedReport != null);

            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed));
            Assert.That(completedReport.BatchAborted, Is.True);
            StringAssert.Contains("test failure", completedReport.FailureMessage);
            StringAssert.Contains("batch will abort", completedReport.FailureMessage);
        }

        [Test]
        public async Task Dispatcher_EmitsStepCompletedReport_OnTimedOut()
        {
            var fixture = CreateDispatcherFixtureWithTimeout(
                timeoutSeconds: 0.05f,
                executorDelayMs: 200,
                failurePolicy: ConvaiActionBatchFailurePolicy.StopBatch);
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => completedReport != null, timeoutMs: 2000);

            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.TimedOut));
            Assert.That(completedReport.BatchAborted, Is.True);

            // The report names the action that ran out of time rather than restating its status. A
            // step that never finishes is precisely the one whose report has to identify itself:
            // every later action queues behind it, so "TimedOut" alone leaves nothing to act on.
            StringAssert.Contains("Move To", completedReport.FailureMessage);
            StringAssert.Contains("did not finish", completedReport.FailureMessage);
            StringAssert.Contains("Remaining batch will abort", completedReport.FailureMessage);
        }

        [Test]
        public async Task Dispatcher_EmitsStepCompletedReport_OnUnhandled()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            fixture.Executor.ResultToReturn = ConvaiActionExecutionResult.Unhandled("test unhandled");
            ConvaiActionStepReport completedReport = null;
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completedReport = report);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => completedReport != null);

            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
            Assert.That(completedReport.BatchAborted, Is.True);
            StringAssert.Contains("test unhandled", completedReport.FailureMessage);
        }

        [Test]
        public async Task Dispatcher_EmitsStepCompletedReport_OnCanceled()
        {
            var fixture = CreateDispatcherFixture(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.StopBatch);
            fixture.Executor.HoldUntilCancelled = true;
            var completion = new TaskCompletionSource<ConvaiActionStepReport>();
            fixture.Dispatcher.OnStepCompleted.AddListener(report => completion.TrySetResult(report));

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await AsyncTestDeadline.WithinAsync(fixture.Executor.Started.Task, "The executor must start.", 1000);
            fixture.GameObject.SetActive(false);
            await AsyncTestDeadline.WithinAsync(completion.Task, "Disabling must complete the action report.", 1000);
            ConvaiActionStepReport completedReport = await completion.Task;

            Assert.That(fixture.Executor.CancellationObserved, Is.True);
            Assert.That(completedReport.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Canceled));
            Assert.That(completedReport.BatchAborted, Is.True);
            StringAssert.Contains("Canceled", completedReport.FailureMessage);
        }

        [Test]
        public void ActionConfigSource_DeduplicatesDefinitions_BeforeSerializingActionConfig()
        {
            GameObject gameObject = CreateCharacterGameObject("char-dedupe", "Dedupe Test", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();

            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = executor },
                new() { ActionName = "move to", Executor = executor }
            });

            ConvaiActionConfig config = source.BuildActionConfig();

            Assert.That(config.Actions.Count, Is.EqualTo(1));
            Assert.That(_logSink.Entries.Count(entry => entry.Level == LogLevel.Warning &&
                System.Text.RegularExpressions.Regex.IsMatch(entry.Message, "Duplicate action definition 'move to'|Duplicate action definition 'Move To'")), Is.EqualTo(1));
            Assert.That(config.Actions[0], Is.EqualTo("Move To"));
        }

        [Test]
        public void ActionConfigSource_NormalizesValidInitialAttentionObject()
        {
            GameObject gameObject = CreateCharacterGameObject("char-attention-valid", "Attention Valid Test", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();

            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = executor }
            });
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = " cube " }
            });
            SetPrivateField(source, "_initialAttentionObject", "CUBE");

            ConvaiActionConfig config = source.BuildActionConfig();

            Assert.That(config, Is.Not.Null);
            Assert.That(config.CurrentAttentionObject, Is.EqualTo("cube"));
        }

        [Test]
        public void ActionConfigSource_OmitsInvalidInitialAttentionObject()
        {
            GameObject gameObject = CreateCharacterGameObject("char-attention-invalid", "Attention Invalid Test", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();

            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = executor }
            });
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "cube" }
            });
            SetPrivateField(source, "_initialAttentionObject", "lever");

            ConvaiActionConfig config = source.BuildActionConfig();

            Assert.That(config, Is.Not.Null);
            Assert.That(config.CurrentAttentionObject, Is.Null.Or.Empty);
            Assert.That(_logSink.Entries.Count(entry => entry.Level == LogLevel.Warning &&
                System.Text.RegularExpressions.Regex.IsMatch(entry.Message, "Initial attention object 'lever'")), Is.EqualTo(1));
        }

        [Test]
        public void ActionConfigSource_OmitsConfig_WhenOnlyTargetsAndAttentionAreAuthored()
        {
            GameObject gameObject = CreateCharacterGameObject("char-targets-only", "Targets Only Test", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();

            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "cube" }
            });
            source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
            {
                new() { Name = "Player" }
            });
            SetPrivateField(source, "_initialAttentionObject", "cube");

            ConvaiActionConfig config = source.BuildActionConfig();

            Assert.That(config, Is.Null);
            Assert.That(_logSink.Entries.Count(entry => entry.Level == LogLevel.Warning &&
                System.Text.RegularExpressions.Regex.IsMatch(entry.Message, "action definitions")), Is.EqualTo(1));
        }

        [Test]
        public void ActionConfigSource_OmitsInvalidExecutorDefinitions_BeforeSerializingActionConfig()
        {
            GameObject gameObject = CreateCharacterGameObject("char-invalid-action", "Invalid Action", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = executor },
                new() { ActionName = "Dance", Executor = null },
                new() { ActionName = "Look", Executor = gameObject.AddComponent<NonExecutorMonoBehaviour>() }
            });

            ConvaiActionConfig config = source.BuildActionConfig();

            CollectionAssert.AreEqual(new[] { "Move To" }, config.Actions);

            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void ActionConfigSource_KeepsValidDuplicate_WhenEarlierDuplicateIsNotExecutable()
        {
            GameObject gameObject = CreateCharacterGameObject("char-valid-duplicate", "Valid Duplicate", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = null },
                new() { ActionName = "move to", Executor = executor }
            });

            ConvaiActionConfig config = source.BuildActionConfig();
            IReadOnlyList<ConvaiActionDefinition> definitions = source.GetEffectiveDefinitions(requireExecutable: true);

            CollectionAssert.AreEqual(new[] { "move to" }, config.Actions);
            Assert.That(definitions.Count, Is.EqualTo(1));
            Assert.That(definitions[0].Executor, Is.SameAs(executor));

            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void ActionResolution_BuildsObjectNameToEntityLookup_FromActionConfigObjectsOnly()
        {
            var cube = new GameObject("Cube Entity");
            var lever = new GameObject("Lever Entity");
            try
            {
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "cube", GameObjectReference = cube },
                        new() { Name = "lever", GameObjectReference = lever }
                    }
                };

                Dictionary<string, GameObject> lookup = ConvaiResolvedActionTarget.BuildObjectEntityLookup(config);

                Assert.That(lookup.Count, Is.EqualTo(2));
                Assert.That(lookup["cube"], Is.SameAs(cube));
                Assert.That(lookup["lever"], Is.SameAs(lever));
                Assert.That(lookup.ContainsKey("scene-only-object"), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
                UnityEngine.Object.DestroyImmediate(lever);
            }
        }

        [Test]
        public void ActionConfigValidator_ReportsAuthoringProblems()
        {
            GameObject gameObject = CreateCharacterGameObject("char-validator", "Validator", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = executor },
                new() { ActionName = "move to", Executor = executor },
                new() { ActionName = "   ", Executor = executor },
                new() { ActionName = "Dance", Executor = null }
            });
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "SharedTarget", Description = "", GameObjectReference = null }
            });
            source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
            {
                new() { Name = "SharedTarget", Bio = "", GameObjectReference = null }
            });
            SetPrivateField(source, "_initialAttentionObject", "missing_object");

            IReadOnlyList<ConvaiActionConfigDiagnostic> diagnostics =
                ConvaiActionConfigValidator.Validate(source);

            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("Duplicate action definition")));
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("blank action name")));
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("no action behavior bound to it")));
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("Duplicate target name")));
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("missing object description")));
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("missing character bio")));
            // The wording changed with the Scene Knowledge link work: the diagnostic now names what
            // the user can do about it, and no longer says "GameObject reference" at a user at all.
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("no scene object linked to it")));
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Message.Contains("Initial attention object")));
            Assert.That(diagnostics.Any(diagnostic => diagnostic.Severity == ConvaiActionConfigDiagnosticSeverity.Error));

            UnityEngine.Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void ActionConfigValidator_MissingTargetMessage_IsScopedClearAndActionable()
        {
            GameObject gameObject = CreateCharacterGameObject("target-copy", "Target Copy", out _);
            try
            {
                ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
                RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Look At",
                        Executor = executor,
                        TargetRequirement = ConvaiActionTargetRequirement.Either
                    }
                });

                ConvaiActionConfigDiagnostic finding = ConvaiActionConfigValidator.Validate(source)
                    .Single(diagnostic => diagnostic.Message.Contains("Action 'Look At' needs"));

                Assert.That(finding.Severity, Is.EqualTo(ConvaiActionConfigDiagnosticSeverity.Warning));
                Assert.That(finding.Context, Is.EqualTo("Action definition #1"));
                Assert.That(finding.Message, Does.Contain("Scene Knowledge"));
                Assert.That(finding.Message, Does.Contain("other actions are unaffected"));
                Assert.That(finding.Message, Does.Not.Contain("actionable"));
                Assert.That(finding.Message, Does.Not.Contain("targets are named"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ActionConfigValidator_AutoRegisteringSceneTarget_SatisfiesTargetSetup()
        {
            GameObject characterObject = CreateCharacterGameObject("scene-target", "Scene Target", out _);
            var targetObject = new GameObject("Red Cube");
            try
            {
                ConvaiActionConfigSource source = characterObject.AddComponent<ConvaiActionConfigSource>();
                RecordingActionExecutor executor = characterObject.AddComponent<RecordingActionExecutor>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Look At",
                        Executor = executor,
                        TargetRequirement = ConvaiActionTargetRequirement.Either
                    }
                });
                ConvaiActionTarget target = targetObject.AddComponent<ConvaiActionTarget>();

                IReadOnlyList<ConvaiActionConfigDiagnostic> diagnostics =
                    ConvaiActionConfigValidator.Validate(source, new[] { target });

                Assert.That(diagnostics.Any(diagnostic =>
                    diagnostic.Message.Contains("Action 'Look At' needs")), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(targetObject);
                UnityEngine.Object.DestroyImmediate(characterObject);
            }
        }

        [Test]
        public void ActionConfigValidator_ActionSetTargetWarning_MatchesInlineActionGuidance()
        {
            GameObject gameObject = CreateCharacterGameObject("set-target-copy", "Set Target Copy", out _);
            ConvaiActionSet actionSet = ConvaiActionSet.CreateDefault();
            try
            {
                ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
                actionSet.name = "Shared Movement";
                actionSet.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Look At",
                        TargetRequirement = ConvaiActionTargetRequirement.Either
                    }
                });
                source.ReplaceActionSets(new List<ConvaiActionSet> { actionSet });

                ConvaiActionConfigDiagnostic finding = ConvaiActionConfigValidator.Validate(source)
                    .Single(diagnostic => diagnostic.Message.Contains("Action 'Look At' needs"));

                Assert.That(finding.Severity, Is.EqualTo(ConvaiActionConfigDiagnosticSeverity.Warning));
                Assert.That(finding.Context, Is.EqualTo("Action set 'Shared Movement' definition #1"));
                Assert.That(finding.Message, Does.Contain("Scene Knowledge"));
                Assert.That(finding.Message, Does.Contain("other actions are unaffected"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actionSet);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ActionDefinition_ToActionConfigString_RendersTypedParametersDeterministically()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Put",
                Description = "Put an item into a container.",
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new()
                    {
                        Name = "item",
                        Description = "Inventory item.",
                        Type = ConvaiActionParameterType.String
                    },
                    new()
                    {
                        Name = "container",
                        Description = "Destination container.",
                        Type = ConvaiActionParameterType.Reference,
                        Connector = "on"
                    },
                    new()
                    {
                        Name = "speed",
                        Type = ConvaiActionParameterType.Choice,
                        Connector = "at",
                        Choices = new List<string> { "slow", "fast" }
                    }
                }
            };

            Assert.That(definition.ToActionConfigString(),
                Is.EqualTo("Put {item: string} on {container: reference} at {speed: choice [slow|fast]} - Put an item into a container. item: Inventory item. container: Destination container."));
        }

        /// <summary>
        ///     An action that needs something to act on must offer a slot to put it in.
        /// </summary>
        /// <remarks>
        ///     The requirement was known to the SDK and never said on the wire, so an action with no
        ///     parameters of its own — the ordinary shape of "walk to somewhere" — reached the Convai
        ///     Character as a bare name. It would then pick the action, name the destination in what
        ///     it said out loud, and send the command with an empty target, because nothing in the
        ///     shape it was given suggested the destination belonged in the command.
        /// </remarks>
        [Test]
        public void ActionDefinition_ToActionConfigString_OffersASlotForTheTargetItRequires()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Walk To",
                Description = "Walk over to a place.",
                TargetRequirement = ConvaiActionTargetRequirement.Either
            };

            Assert.That(definition.ToActionConfigString(),
                Is.EqualTo("Walk To {target: reference} - Walk over to a place."));
        }

        /// <summary>An action that requires nothing is offered exactly as before.</summary>
        [Test]
        public void ActionDefinition_ToActionConfigString_AddsNoSlotWhenNoTargetIsRequired()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Wave",
                Description = "Wave hello.",
                TargetRequirement = ConvaiActionTargetRequirement.None
            };

            Assert.That(definition.ToActionConfigString(), Is.EqualTo("Wave - Wave hello."));
        }

        /// <summary>
        ///     An action whose own parameter can carry the target keeps that one slot. Two slots
        ///     asking for the same thing invites the Convai Character to split it across both.
        /// </summary>
        [Test]
        public void ActionDefinition_ToActionConfigString_DoesNotAddASecondSlotWhenAParameterAlreadyCarriesTheTarget()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Hand To",
                Description = "Give something to someone.",
                TargetRequirement = ConvaiActionTargetRequirement.Character,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "recipient", Type = ConvaiActionParameterType.Reference }
                }
            };

            Assert.That(definition.ToActionConfigString(),
                Is.EqualTo("Hand To {recipient: reference} - Give something to someone."));
        }

        [Test]
        public void ActionDefinition_ToActionConfigString_UsesAsciiWireText()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Move To",
                Description = "Move — quickly",
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new()
                    {
                        Name = "destination",
                        Description = "Café destination",
                        Type = ConvaiActionParameterType.Reference,
                        Connector = "toward"
                    }
                }
            };

            // "Move - quickly", not "Move quickly": an em dash in prose is a dash, and folding it to
            // nothing used to join the two halves into a sentence the author had not written. The
            // accent still folds to its base letter, which has no ASCII spelling of its own.
            Assert.That(definition.ToActionConfigString(),
                Is.EqualTo("Move To toward {destination: reference} - Move - quickly destination: Cafe destination"));
        }

        [Test]
        public void ActionDefinition_AnswerDelivery_NeverReachesTheWire()
        {
            var acting = new ConvaiActionDefinition
            {
                ActionName = "Read Status",
                Description = "Say what state it is in",
                AnswerDelivery = ConvaiActionAnswerDelivery.UseCharacterSetting
            };
            var answering = new ConvaiActionDefinition
            {
                ActionName = "Read Status",
                Description = "Say what state it is in",
                AnswerDelivery = ConvaiActionAnswerDelivery.TellThePlayer
            };

            Assert.That(answering.ToActionConfigString(), Is.EqualTo(acting.ToActionConfigString()),
                "Answer Delivery is authoring-only. If it changed the rendered template it would " +
                "change what the Convai Character is offered, which this setting must never do.");
        }

        [Test]
        public void ActionDefinition_AnswerDelivery_DefaultsToDeferringAndSurvivesClone()
        {
            var untouched = new ConvaiActionDefinition { ActionName = "Walk To" };

            Assert.That(untouched.AnswerDelivery, Is.EqualTo(ConvaiActionAnswerDelivery.UseCharacterSetting),
                "The zero value must be 'defer', so a definition authored before this setting " +
                "existed keeps behaving exactly as it did.");

            var authored = new ConvaiActionDefinition
            {
                ActionName = "Read Status",
                AnswerDelivery = ConvaiActionAnswerDelivery.TellThePlayer
            };

            Assert.That(authored.Clone().AnswerDelivery, Is.EqualTo(ConvaiActionAnswerDelivery.TellThePlayer),
                "Definitions are cloned on their way to the session catalog; a field left out of " +
                "Clone() disappears with no error anywhere.");
        }

        [Test]
        public void ActionConfigSource_BuildActionConfig_SerializesRenderedActionTemplates()
        {
            GameObject gameObject = CreateCharacterGameObject("char-rendered-actions", "Rendered Actions", out _);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();

            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Put",
                    Executor = executor,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "item", Type = ConvaiActionParameterType.String },
                        new() { Name = "container", Type = ConvaiActionParameterType.Reference, Connector = "on" }
                    }
                }
            });

            ConvaiActionConfig config = source.BuildActionConfig();

            CollectionAssert.AreEqual(new[] { "Put {item: string} on {container: reference}" }, config.Actions);
        }

        [Test]
        public void ActionResponseParser_MapsBraceWrappedValuesToTypedParameters()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Put",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "item", Type = ConvaiActionParameterType.String },
                        new() { Name = "container", Type = ConvaiActionParameterType.String }
                    }
                }
            };

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Put {red key} {wood drawer}"), null, definitions);

            Assert.That(command.Parameters["item"].StringValue, Is.EqualTo("red key"));
            Assert.That(command.Parameters["container"].StringValue, Is.EqualTo("wood drawer"));
            Assert.That(command.ActionString, Is.EqualTo("Put {red key} {wood drawer}"));
        }

        [Test]
        public void ActionResponseParser_SplitsNamedAnchorsAndConnectorValues()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Put",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "item", Type = ConvaiActionParameterType.String },
                        new() { Name = "container", Type = ConvaiActionParameterType.String, Connector = "on" }
                    }
                }
            };

            ConvaiActionCommand named = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Put", "item: key container: drawer"), null, definitions);
            ConvaiActionCommand connector = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Put", "key on drawer"), null, definitions);

            Assert.That(named.Parameters["item"].StringValue, Is.EqualTo("key"));
            Assert.That(named.Parameters["container"].StringValue, Is.EqualTo("drawer"));
            Assert.That(connector.Parameters["item"].StringValue, Is.EqualTo("key"));
            Assert.That(connector.Parameters["container"].StringValue, Is.EqualTo("drawer"));
        }

        [Test]
        public void ActionResponseParser_CoercesReferenceNumberBoolAndChoice()
        {
            GameObject cube = new("ActionSystemTests_cube");
            try
            {
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "Cube", Description = "A cube.", GameObjectReference = cube }
                    }
                };
                var definitions = new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Configure",
                        Parameters = new List<ConvaiActionParameterDefinition>
                        {
                            new() { Name = "target", Type = ConvaiActionParameterType.Reference },
                            new() { Name = "seconds", Type = ConvaiActionParameterType.Number },
                            new() { Name = "enabled", Type = ConvaiActionParameterType.Bool },
                            new() { Name = "mode", Type = ConvaiActionParameterType.Choice, Choices = new List<string> { "fast", "slow" } }
                        }
                    }
                };

                ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                    new ConvaiActionCommand("Configure", "{Cube} {2.5} {yes} {fast}"), config, definitions);

                Assert.That(command.Parameters["target"].ResolvedReference?.Name, Is.EqualTo("Cube"));
                Assert.That(command.Parameters["target"].ResolvedReference?.Kind, Is.EqualTo(ConvaiActionTargetKind.Object));
                Assert.That(command.Parameters["seconds"].NumberValue, Is.EqualTo(2.5f).Within(0.001f));
                Assert.That(command.Parameters["enabled"].BoolValue, Is.True);
                Assert.That(command.Parameters["mode"].IsConstraintMatch, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
        }

        /// <summary>
        ///     Models quote their values, and the wire quotes them again. An unstripped <c>'follow'</c>
        ///     matches no authored choice, so the parameter silently fell back to the authored default
        ///     while the console said only that the value "is not in authored choices" — with the
        ///     value printed inside quotes of its own, which reads as a formatting artefact rather
        ///     than as the fault.
        /// </summary>
        [TestCase("'follow'", TestName = "ActionResponseParser_StripsSingleQuotesAroundAChoice")]
        [TestCase("\"follow\"", TestName = "ActionResponseParser_StripsDoubleQuotesAroundAChoice")]
        [TestCase("''follow''", TestName = "ActionResponseParser_StripsRepeatedQuotesAroundAChoice")]
        [TestCase("‘follow’", TestName = "ActionResponseParser_StripsTypographicQuotesAroundAChoice")]
        [TestCase(" 'follow' ", TestName = "ActionResponseParser_StripsQuotesAroundAPaddedChoice")]
        [TestCase("{\"mode\": \"follow\"}", TestName = "ActionResponseParser_ReadsAChoiceWrittenAsJson")]
        [TestCase("{mode: \"follow\"}", TestName = "ActionResponseParser_ReadsAChoiceInAFilledSlot")]
        public void ActionResponseParser_StripsQuotesModelsWrapValuesIn(string quoted)
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Follow Me",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new()
                        {
                            Name = "mode",
                            Type = ConvaiActionParameterType.Choice,
                            Choices = new List<string> { "follow", "stop" }
                        }
                    }
                }
            };

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Follow Me", quoted), null, definitions);

            Assert.That(command.Parameters["mode"].StringValue, Is.EqualTo("follow"),
                "The quotes belong to the wire format, not to the value.");
            Assert.That(command.Parameters["mode"].IsConstraintMatch, Is.True,
                "A quoted choice is the authored choice; treating it as a mismatch drops the caller " +
                "back to the authored default without ever saying so.");
        }

        /// <summary>
        ///     An action with one slot keeps everything the Convai Character sent for it. There is
        ///     nothing sharing the string, so there is nothing to carve — and carving it anyway
        ///     destroyed values.
        /// </summary>
        /// <remarks>
        ///     The value splitter used to run for every action, however many slots it had. A line
        ///     containing two quoted phrases matched the quoted-group stage, which found two groups
        ///     where the template had one slot; the extra was padded away and the parameter arrived
        ///     as <c>first</c>. Nothing said so — a truncated line reads as a line.
        /// </remarks>
        [TestCase("\"first\" and \"second\"", TestName = "ActionResponseParser_KeepsALineHoldingTwoQuotedPhrases")]
        [TestCase("{20} {80}", TestName = "ActionResponseParser_KeepsAValueHoldingTwoBracedGroups")]
        public void ActionResponseParser_DoesNotCarveTheValueOfASingleSlotAction(string sent)
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Say Something",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "line", Type = ConvaiActionParameterType.String }
                    }
                }
            };

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Say Something", sent), null, definitions);

            Assert.That(command.Parameters["line"].StringValue, Is.EqualTo(sent),
                "One slot takes the whole value. Keeping part of it and dropping the rest in " +
                "silence is worse than either keeping all of it or refusing it.");
        }

        /// <summary>
        ///     A Convai Character that answers a slot with a whole JSON object, inside the action
        ///     text rather than in the target field, still reaches the Action Behavior with the value
        ///     it meant.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>This is the shape that was measured live</b>, and it is not the same path as the
        ///         one <see cref="ActionResponseParser_StripsQuotesModelsWrapValuesIn" /> covers.
        ///         There the object arrives in the target field and is cleaned whole. Here it arrives
        ///         glued to the name, so the braces are taken off by the value splitter first and what
        ///         reaches the cleaner is already <c>"gesture": "wave"</c> — a quoted key and a quoted
        ///         value, with no braces left to say so.
        ///     </para>
        ///     <para>
        ///         Two separate faults met on this input. The quote strip took the two ends for one
        ///         wrapping pair and spliced them into <c>gesture": "wave</c>; and the label drop only
        ///         recognised an unquoted <c>gesture:</c>, so even once the splice was stopped the key
        ///         stayed glued on and the Choice fell back to its default. The character announced a
        ///         wave and stood still. Both halves are asserted below, so fixing one and calling it
        ///         done cannot pass.
        ///     </para>
        /// </remarks>
        [Test]
        public void ActionResponseParser_ReadsASlotTheCharacterAnsweredWithAJsonObject()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Play Gesture",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new()
                        {
                            Name = "gesture",
                            Type = ConvaiActionParameterType.Choice,
                            Choices = new List<string> { "wave", "clap" }
                        }
                    }
                }
            };

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Play Gesture {\"gesture\": \"wave\"}", null), null, definitions);

            Assert.That(command.Parameters["gesture"].StringValue, Is.EqualTo("wave"),
                "The object is the Convai Character filling in the slot it was shown. Its value is " +
                "'wave' whether or not it quoted the key.");
            Assert.That(command.Parameters["gesture"].IsConstraintMatch, Is.True,
                "Reading it as anything else drops the parameter back to its authored default " +
                "without ever saying so.");
        }

        /// <summary>
        ///     The backend commonly sends the target inside the action text — <c>Walk To The
        ///     Gallery</c> — rather than in its own field. Enrichment strips the action name and parks
        ///     what is left under the implicit target key, and an action that declares no parameters
        ///     of its own (the ordinary shape of "walk to somewhere") has nothing else carrying it.
        ///     The required-target check therefore has to consult that key, or the command is dropped
        ///     as targetless while holding the target it was given.
        /// </summary>
        [Test]
        public void ActionResponseParser_AcceptsATargetSentInsideTheActionText()
        {
            GameObject gallery = new("ActionSystemTests_impliedGallery");
            try
            {
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "The Gallery", Description = "The east room.", GameObjectReference = gallery }
                    }
                };
                var definitions = new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Walk To",
                        Description = "Walk over to a place.",
                        TargetRequirement = ConvaiActionTargetRequirement.Either,
                        Executor = null
                    }
                };

                // No Target field: the whole thing arrives as the action string, exactly as the wire
                // delivers it.
                var command = new ConvaiActionCommand("Walk To The Gallery");
                ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(command, config, definitions);

                Assert.That(enriched.Parameters.ContainsKey("target"), Is.True,
                    "Enrichment must park the leftover name under the implicit target key.");

                var drops = new ConvaiActionDropCollector(true);
                ConvaiActionResponseParser.FilterExecutableBatch(
                    new[] { command }, config, definitions, drops);

                // The definition here has no executor, so the batch is rejected as unexecutable —
                // never as target-less. That distinction is the whole point of this test.
                Assert.That(drops.CountsByReason.ContainsKey("required_target_unresolved"), Is.False,
                    "A command carrying its target inside the action text must not be dropped as targetless.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gallery);
            }
        }

        /// <summary>
        ///     Unity rebuilds a <c>[Serializable]</c> instance field by field and never runs a
        ///     constructor, so any default that lives in a property initializer is silently lost for
        ///     entries loaded from a scene. For <c>Available</c> that meant every authored target came
        ///     back unavailable and was skipped at every rung of the resolution ladder — targetless
        ///     actions kept working while every targeted one was dropped before reaching a handler.
        ///     This pins the encoding that survives: the zero value must mean available.
        /// </summary>
        [Test]
        public void ActionTargets_AreAvailableWhenRebuiltWithoutAConstructor()
        {
            var actionObject = (ConvaiActionObjectDefinition)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(ConvaiActionObjectDefinition));
            var actionCharacter = (ConvaiActionCharacterDefinition)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(ConvaiActionCharacterDefinition));

            Assert.That(actionObject.Available, Is.True,
                "An object rebuilt the way Unity rebuilds it must default to available, or it can never resolve.");
            Assert.That(actionCharacter.Available, Is.True,
                "A character rebuilt the way Unity rebuilds it must default to available.");

            actionObject.Available = false;
            Assert.That(actionObject.Available, Is.False, "SetTargetAvailable(false) must still take effect.");
            actionObject.Available = true;
            Assert.That(actionObject.Available, Is.True, "…and must be reversible.");
        }

        /// <summary>
        ///     The end-to-end shape of the bug: a target registered in the config, rebuilt the way a
        ///     scene load rebuilds it, has to resolve by name.
        /// </summary>
        [Test]
        public void ActionResponseParser_ResolvesATargetRebuiltTheWayASceneLoadRebuildsIt()
        {
            var gallery = (ConvaiActionObjectDefinition)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(ConvaiActionObjectDefinition));
            gallery.Name = "The Gallery";
            gallery.Description = "The east room.";

            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition> { gallery }
            };

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "The Gallery", config, ConvaiActionTargetRequirement.Either);

            Assert.That(resolved, Is.Not.Null,
                "A registered target must resolve after a scene load, or every targeted action is dropped.");
            Assert.That(resolved.Name, Is.EqualTo("The Gallery"));
        }

        /// <summary>
        ///     An action is presented as <c>Name {target: reference} - description</c>, and a model
        ///     asked to use it fills the slot <em>in place</em>: <c>{target: "The Rooms"}</c>. The
        ///     braces and the slot's own name are template syntax, not part of the name being asked
        ///     for, and left on they match no target — the command is then dropped as targetless
        ///     while carrying a perfectly good target.
        /// </summary>
        [TestCase("{target: \"The Gallery\"}", TestName = "ActionResponseParser_UnwrapsAFilledTemplateSlot")]
        [TestCase("{target: The Gallery}", TestName = "ActionResponseParser_UnwrapsAnUnquotedTemplateSlot")]
        [TestCase("{The Gallery}", TestName = "ActionResponseParser_UnwrapsBracesWithNoSlotName")]
        [TestCase("- {target: 'The Gallery'}", TestName = "ActionResponseParser_UnwrapsASlotBehindASeparator")]
        [TestCase("{\"target\": \"The Gallery\"}", TestName = "ActionResponseParser_UnwrapsASlotWrittenAsJson")]
        public void ActionResponseParser_ResolvesATargetTheModelWroteAsATemplateSlot(string sent)
        {
            GameObject gallery = new("ActionSystemTests_slotGallery");
            try
            {
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "The Gallery", Description = "The east room.", GameObjectReference = gallery }
                    }
                };
                var definitions = new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Look At",
                        Description = "Look at something.",
                        TargetRequirement = ConvaiActionTargetRequirement.Either
                    }
                };

                ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                    new ConvaiActionCommand("Look At", sent), config, definitions);

                Assert.That(command.Target, Is.EqualTo("The Gallery"),
                    "The slot is syntax the model copied; only the name inside it is the target.");

                ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                    command.Target, config, ConvaiActionTargetRequirement.Either);
                Assert.That(resolved, Is.Not.Null);
                Assert.That(resolved.Name, Is.EqualTo("The Gallery"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gallery);
            }
        }

        /// <summary>
        ///     A colon inside a real name is not a slot separator. Only an unspaced first word
        ///     followed by a colon is treated as the slot's name.
        /// </summary>
        [Test]
        public void ActionResponseParser_KeepsAColonThatIsPartOfTheName()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Look At",
                    TargetRequirement = ConvaiActionTargetRequirement.Either
                }
            };

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Look At", "{Bay 2: North}"), null, definitions);

            Assert.That(command.Target, Is.EqualTo("Bay 2: North"),
                "The first word is followed by a space, so the colon belongs to the name.");
        }

        /// <summary>
        ///     Actions are shown to the model as <c>Name - description</c>, and models complete the
        ///     pattern they are shown: asked to walk somewhere, one answers <c>Walk To - The
        ///     Gallery</c>. With the action name stripped off the front, the separator stays glued to
        ///     the target, matches no registered target, and the command is dropped before it reaches
        ///     any handler — so the action does nothing at all while targetless actions on the same
        ///     character keep working.
        /// </summary>
        [TestCase("- The Gallery", TestName = "ActionResponseParser_StripsTheDashModelsEchoFromTheTemplate")]
        [TestCase("-The Gallery", TestName = "ActionResponseParser_KeepsADashWithNoSpaceAsPartOfTheName")]
        [TestCase(": The Gallery", TestName = "ActionResponseParser_StripsAColonSeparator")]
        [TestCase("— The Gallery", TestName = "ActionResponseParser_StripsAnEmDashSeparator")]
        public void ActionResponseParser_ResolvesATargetTheModelPrefixedWithASeparator(string sent)
        {
            GameObject gallery = new("ActionSystemTests_gallery");
            try
            {
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "The Gallery", Description = "The east room.", GameObjectReference = gallery }
                    }
                };
                var definitions = new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Walk To",
                        Description = "Walk over to a place.",
                        TargetRequirement = ConvaiActionTargetRequirement.Either
                    }
                };

                ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                    new ConvaiActionCommand("Walk To", sent), config, definitions);

                // "-The Gallery" has no space after the dash, so it is a name, not a separator —
                // and it is expected to stay unresolved rather than be silently repaired.
                bool separatorWasStripped = sent.Length > 1 && char.IsWhiteSpace(sent[1]);
                ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                    command.Target, config, ConvaiActionTargetRequirement.Either);

                if (separatorWasStripped)
                {
                    Assert.That(resolved, Is.Not.Null,
                        "A target the model prefixed with the template's own separator must still resolve.");
                    Assert.That(resolved.Name, Is.EqualTo("The Gallery"));
                }
                else
                {
                    Assert.That(command.Target, Is.EqualTo(sent),
                        "Without a space the dash is part of the name and must be left alone.");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gallery);
            }
        }

        /// <summary>
        ///     A quote that is part of the text is not a wrapper. Stripping only matched pairs that
        ///     enclose the whole value is what keeps an apostrophe inside a name intact.
        /// </summary>
        [TestCase("the visitor's bag", "the visitor's bag")]
        [TestCase("'unbalanced", "'unbalanced")]
        [TestCase("unbalanced'", "unbalanced'")]
        [TestCase("'", "'")]
        public void ActionResponseParser_KeepsQuotesThatAreNotWrappers(string raw, string expected)
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Say",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "text", Type = ConvaiActionParameterType.String }
                    }
                }
            };

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Say", raw), null, definitions);

            Assert.That(command.Parameters["text"].StringValue, Is.EqualTo(expected));
        }

        [Test]
        public void ActionResponseParser_RenderedActionNameUsesTargetForParameters()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Put",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "item", Type = ConvaiActionParameterType.String },
                        new() { Name = "container", Type = ConvaiActionParameterType.String, Connector = "on" }
                    }
                }
            };
            string rendered = definitions[0].ToActionConfigString();

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand(rendered, "red key on drawer"),
                null,
                definitions);

            Assert.That(command.Name, Is.EqualTo("Put"));
            Assert.That(command.Parameters["item"].StringValue, Is.EqualTo("red key"));
            Assert.That(command.Parameters["container"].StringValue, Is.EqualTo("drawer"));
        }

        [Test]
        public void ActionResponseParser_ChoiceMatchingTrimsAuthoredChoices()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Set Beacon",
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new()
                        {
                            Name = "color",
                            Type = ConvaiActionParameterType.Choice,
                            Choices = new List<string> { " red ", " green " }
                        }
                    }
                }
            };

            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Set Beacon", "GREEN"),
                null,
                definitions);

            Assert.That(command.Parameters["color"].IsConstraintMatch, Is.True);
        }

        [Test]
        public void ActionResponseParser_UnknownActionPreservesTargetParameter()
        {
            ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Unknown", "raw target"), null, Array.Empty<ConvaiActionDefinition>());

            Assert.That(command.Name, Is.EqualTo("Unknown"));
            Assert.That(command.Target, Is.EqualTo("raw target"));
            Assert.That(command.Parameters["target"].StringValue, Is.EqualTo("raw target"));
        }

        [Test]
        public void ActionInvocation_GetReferencePreservesResolvedReferenceKind()
        {
            GameObject gameObject = CreateCharacterGameObject("char-kind", "Reference Kind", out ConvaiCharacter character);
            GameObject objectTarget = new("ActionSystemTests_SharedObject");
            GameObject characterTarget = new("ActionSystemTests_SharedCharacter");
            try
            {
                ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
                RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Inspect",
                        Executor = executor,
                        Parameters = new List<ConvaiActionParameterDefinition>
                        {
                            new() { Name = "subject", Type = ConvaiActionParameterType.Reference }
                        }
                    }
                });
                source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Shared", GameObjectReference = objectTarget }
                });
                source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
                {
                    new() { Name = "Shared", GameObjectReference = characterTarget }
                });

                ConvaiActionCommand command = new("Inspect")
                {
                    Parameters = new Dictionary<string, ConvaiActionParameterValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["subject"] = new()
                        {
                            Type = ConvaiActionParameterType.Reference,
                            RawValue = "Shared",
                            StringValue = "Shared",
                            ResolvedReference = new ConvaiActionParameterReference("Shared", ConvaiActionTargetKind.Character)
                        }
                    }
                };
                ConvaiActionInvocation invocation = CreateInvocation(command, null, null, character);

                ConvaiResolvedActionTarget reference = invocation.GetReference("subject");

                Assert.That(reference?.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
                Assert.That(reference?.GameObjectReference, Is.EqualTo(characterTarget));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(objectTarget);
                UnityEngine.Object.DestroyImmediate(characterTarget);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public async Task TypedExecutor_ReceivesStronglyBoundParameters()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            var typedExecutor = fixture.GameObject.AddComponent<RecordingTypedPutExecutor>();
            ConvaiActionConfigSource source = fixture.GameObject.GetComponent<ConvaiActionConfigSource>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Put",
                    Executor = typedExecutor,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "item", Type = ConvaiActionParameterType.String },
                        new() { Name = "container", Type = ConvaiActionParameterType.Reference, Connector = "on" }
                    }
                }
            });
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "drawer", GameObjectReference = fixture.GameObject }
            });

            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Put", "red key on drawer"),
                fixture.Character.ActionConfig,
                GetRuntimeActionDefinitions(fixture.Character));

            fixture.Dispatcher.EnqueueActions(new[] { enriched });
            await WaitUntilAsync(() => typedExecutor.LastParameters != null);

            Assert.That(typedExecutor.LastParameters.Item, Is.EqualTo("red key"));
            Assert.That(typedExecutor.LastParameters.Container?.Name, Is.EqualTo("drawer"));
        }

        [Test]
        public async Task Dispatcher_WaitsForSpeechGate_BeforeFirstAction()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            SetPrivateField(fixture.Dispatcher, "_speechGateTimeoutSeconds", 1f);
            ConvaiActionConfigSource source = fixture.GameObject.GetComponent<ConvaiActionConfigSource>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    Executor = fixture.Executor,
                    WaitForBotSpeech = true
                }
            });

            fixture.Dispatcher.EnqueueActions(new[]
            {
                new ConvaiActionCommand("Move To")
                {
                    WaitForBotSpeech = true
                }
            });

            await Task.Delay(80);
            Assert.That(fixture.Executor.ExecutedActions, Is.Empty);

            RaiseCharacterSpeechStarted(fixture.Character);
            await WaitUntilAsync(() => fixture.Executor.ExecutedActions.Count == 1);

            CollectionAssert.AreEqual(new[] { "Move To" }, fixture.Executor.ExecutedActions);
        }

        [Test]
        public async Task Dispatcher_FiresOnBatchAborted_WhenAborted()
        {
            var fixture = CreateDispatcherFixtureWithFailure(
                ConvaiActionBatchPolicy.Queue,
                ConvaiActionBatchFailurePolicy.StopBatch);
            bool abortedFired = false;
            fixture.Dispatcher.OnBatchAborted.AddListener(() => abortedFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => abortedFired);

            Assert.That(abortedFired, Is.True);
        }

        [Test]
        public async Task Dispatcher_FiresOnBatchCompleted_WhenNormal()
        {
            var fixture = CreateDispatcherFixture(batchPolicy: ConvaiActionBatchPolicy.Queue);
            bool completedFired = false;
            fixture.Dispatcher.OnBatchCompleted.AddListener(() => completedFired = true);

            fixture.Dispatcher.EnqueueActions(new[] { CreateAction("Move To", "cube") });
            await WaitUntilAsync(() => completedFired);

            Assert.That(completedFired, Is.True);
        }

        // ── Adapter / config source integration ──────────────────────────────────────────

        [Test]
        public async Task RoomConnectionRuntimeAdapter_UsesPerCallActionOverride_BeforeCharacterSource()
        {
            GameObject gameObject = CreateCharacterGameObject("00000000-0000-4000-8000-000000000001", "Override Test", out ConvaiCharacter character);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    Executor = executor,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "destination", Type = ConvaiActionParameterType.Reference }
                    }
                }
            });
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "cube" }
            });

            var overrideConfig = new ConvaiActionConfig
            {
                Actions = new List<string> { "Wave" },
                Objects = new List<ConvaiActionObjectDefinition> { new() { Name = "lever" } },
                CurrentAttentionObject = "lever"
            };

            CapturingRoomController controller = new();
            RoomConnectionRuntimeAdapter adapter = CreateRuntimeAdapter(character, controller, new RoomSessionConnectOptions
            {
                TurnTaking = TurnTakingOptions.CreateHandsFreeDefault(),
                ActionConfigOverride = overrideConfig
            });

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(controller.LastJoinOptions?.ResolvedActionConfig?.Actions[0], Is.EqualTo("Wave"));
            Assert.That(controller.LastJoinOptions?.ResolvedActionConfig?.Objects[0].Name, Is.EqualTo("lever"));
            Assert.That(controller.LastJoinOptions?.ResolvedActionConfig?.CurrentAttentionObject, Is.EqualTo("lever"));
        }

        [Test]
        public async Task RoomConnectionRuntimeAdapter_UsesPerCallDefinitionOverride_ForRuntimeExecutionBindings()
        {
            GameObject gameObject = CreateCharacterGameObject("00000000-0000-4000-8000-000000000002", "Definition Override Test", out ConvaiCharacter character);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor sourceExecutor = gameObject.AddComponent<RecordingActionExecutor>();
            RecordingActionExecutor overrideExecutor = gameObject.AddComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = sourceExecutor }
            });

            var overrideConfig = new ConvaiActionConfig
            {
                Actions = new List<string> { "Wave" }
            };

            CapturingRoomController controller = new();
            RoomConnectionRuntimeAdapter adapter = CreateRuntimeAdapter(character, controller, new RoomSessionConnectOptions
            {
                TurnTaking = TurnTakingOptions.CreateHandsFreeDefault(),
                ActionConfigOverride = overrideConfig,
                ActionDefinitionsOverride = new List<ConvaiActionDefinition>
                {
                    new() { ActionName = "Wave", Executor = overrideExecutor }
                }
            });

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.True);
            IReadOnlyList<ConvaiActionDefinition> runtimeDefinitions = GetRuntimeActionDefinitions(character);
            Assert.That(runtimeDefinitions.Count, Is.EqualTo(1));
            Assert.That(runtimeDefinitions[0].ActionName, Is.EqualTo("Wave"));
            Assert.That(runtimeDefinitions[0].Executor, Is.EqualTo(overrideExecutor));
        }

        [Test]
        public async Task RoomConnectionRuntimeAdapter_FallsBackToCharacterActionSource_WhenNoOverride()
        {
            GameObject gameObject = CreateCharacterGameObject("00000000-0000-4000-8000-000000000003", "Source Test", out ConvaiCharacter character);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    Executor = executor,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new()
                        {
                            Name = "destination",
                            Type = ConvaiActionParameterType.Reference,
                            Connector = "toward"
                        }
                    }
                }
            });
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "cube" }
            });
            SetPrivateField(source, "_initialAttentionObject", "cube");

            CapturingRoomController controller = new();
            RoomConnectionRuntimeAdapter adapter = CreateRuntimeAdapter(character, controller, null);

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(controller.LastJoinOptions?.ResolvedActionConfig?.Actions[0], Is.EqualTo("Move To toward {destination: reference}"));
            Assert.That(controller.LastJoinOptions?.ResolvedActionConfig?.Objects[0].Name, Is.EqualTo("cube"));
            Assert.That(controller.LastJoinOptions?.ResolvedActionConfig?.CurrentAttentionObject, Is.EqualTo("cube"));

            IReadOnlyList<ConvaiActionDefinition> runtimeDefinitions = GetRuntimeActionDefinitions(character);
            Assert.That(runtimeDefinitions.Count, Is.EqualTo(1));
            Assert.That(runtimeDefinitions[0].ActionName, Is.EqualTo("Move To"));
        }

        [Test]
        public async Task RoomConnectionRuntimeAdapter_UsesCharacterSessionId_WhenResumeEnabled()
        {
            GameObject gameObject = CreateCharacterGameObject("00000000-0000-4000-8000-000000000004", "Session Test", out ConvaiCharacter character);
            SetPrivateField(character, "_enableSessionResume", true);
            character.SetCharacterSessionId("manual-character-session");

            CapturingRoomController controller = new();
            RoomConnectionRuntimeAdapter adapter = CreateRuntimeAdapter(character, controller, null);

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(controller.LastStoredSessionId, Is.EqualTo("manual-character-session"));
            Assert.That(controller.LastJoinOptions?.CharacterSessionId, Is.EqualTo("manual-character-session"));
            Assert.That(character.CharacterSessionId, Is.EqualTo("character-session-id"));
        }

        [Test]
        public async Task RoomConnectionRuntimeAdapter_ReusesServerSessionId_OnNextConnection()
        {
            GameObject gameObject = CreateCharacterGameObject("00000000-0000-4000-8000-000000000005", "Reconnect Test",
                out ConvaiCharacter character);
            SetPrivateField(character, "_enableSessionResume", true);

            CapturingRoomController controller = new();
            RoomConnectionRuntimeAdapter adapter = CreateRuntimeAdapter(character, controller, null);

            RoomConnectionAttemptResult first = await adapter.ConnectAsync(CancellationToken.None);
            Assert.That(first.Succeeded, Is.True);
            Assert.That(controller.LastJoinOptions?.CharacterSessionId, Is.Null);

            RoomConnectionAttemptResult reconnect = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(reconnect.Succeeded, Is.True);
            Assert.That(controller.LastStoredSessionId, Is.EqualTo("character-session-id"));
            Assert.That(controller.LastJoinOptions?.CharacterSessionId, Is.EqualTo("character-session-id"));
        }

        [Test]
        public async Task RoomConnectionRuntimeAdapter_AlwaysFresh_DoesNotReuseCharacterSessionId()
        {
            GameObject gameObject = CreateCharacterGameObject("00000000-0000-4000-8000-000000000006", "Fresh Session Test",
                out ConvaiCharacter character);
            SetPrivateField(character, "_enableSessionResume", true);
            character.SetCharacterSessionId("previous-character-session");

            CapturingRoomController controller = new();
            RoomConnectionRuntimeAdapter adapter = CreateRuntimeAdapter(
                character,
                controller,
                null,
                reconnectPolicy: ReconnectPolicy.AlwaysCreateNew);

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(controller.LastStoredSessionId, Is.Null);
            Assert.That(controller.LastJoinOptions?.CharacterSessionId, Is.Null);
        }

        [Test]
        public async Task RoomConnectionRuntimeAdapter_DoesNotLoadStoredSession_WhenCharacterSessionIdBlank()
        {
            GameObject gameObject = CreateCharacterGameObject("00000000-0000-4000-8000-000000000007", "Blank Session Test", out ConvaiCharacter character);
            SetPrivateField(character, "_enableSessionResume", true);
            character.ClearCharacterSessionId();

            CapturingRoomController controller = new();
            InMemorySessionPersistence persistence = new();
            persistence.SaveSession(character.CharacterId, "hidden-stored-session");
            RoomConnectionRuntimeAdapter adapter = CreateRuntimeAdapter(character, controller, null, persistence);

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(controller.LastStoredSessionId, Is.Null);
            Assert.That(controller.LastJoinOptions?.CharacterSessionId, Is.Null);
            Assert.That(character.CharacterSessionId, Is.EqualTo("character-session-id"));
        }

        [Test]
        public async Task Dispatcher_TargetRequirementNone_DoesNotPromoteReferenceParameterToResolvedTarget()
        {
            GameObject gameObject = CreateCharacterGameObject("char-param-only", "Param Only", out _);
            GameObject item = new("ActionSystemTests_ParamOnlyItem");
            GameObject container = new("ActionSystemTests_ParamOnlyContainer");
            try
            {
                ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
                RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Put",
                        TargetRequirement = ConvaiActionTargetRequirement.None,
                        Executor = executor,
                        Parameters = new List<ConvaiActionParameterDefinition>
                        {
                            new() { Name = "item", Type = ConvaiActionParameterType.Reference },
                            new() { Name = "container", Type = ConvaiActionParameterType.Reference, Connector = "on" }
                        }
                    }
                });
                source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "silver_key", GameObjectReference = item },
                    new() { Name = "display_pedestal", GameObjectReference = container }
                });
                ConvaiActionDispatcher dispatcher = gameObject.AddComponent<ConvaiActionDispatcher>();
                ConvaiActionInvocation started = null;
                dispatcher.OnStepStarted.AddListener(invocation => started = invocation);

                ConvaiActionCommand command = ConvaiActionResponseParser.Enrich(
                    new ConvaiActionCommand("Put", "silver_key on display_pedestal"),
                    gameObject.GetComponent<ConvaiCharacter>().ActionConfig,
                    GetRuntimeActionDefinitions(gameObject.GetComponent<ConvaiCharacter>()));

                dispatcher.EnqueueActions(new[] { command });
                await WaitUntilAsync(() => started != null);

                Assert.That(started.ResolvedTarget, Is.Null);
                Assert.That(started.GetReference("item")?.Name, Is.EqualTo("silver_key"));
                Assert.That(started.GetReference("container")?.Name, Is.EqualTo("display_pedestal"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(item);
                UnityEngine.Object.DestroyImmediate(container);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public async Task Dispatcher_ParameterFallbackTargetResolutionPreservesReferenceKind()
        {
            GameObject gameObject = CreateCharacterGameObject("char-kind-fallback", "Kind Fallback", out _);
            GameObject objectTarget = new("ActionSystemTests_SharedObject");
            GameObject characterTarget = new("ActionSystemTests_SharedCharacter");
            try
            {
                ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
                RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Follow",
                        TargetRequirement = ConvaiActionTargetRequirement.Character,
                        Executor = executor,
                        Parameters = new List<ConvaiActionParameterDefinition>
                        {
                            new() { Name = "character", Type = ConvaiActionParameterType.Reference }
                        }
                    }
                });
                source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Shared", GameObjectReference = objectTarget }
                });
                source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
                {
                    new() { Name = "Shared", GameObjectReference = characterTarget }
                });
                ConvaiActionDispatcher dispatcher = gameObject.AddComponent<ConvaiActionDispatcher>();
                ConvaiActionInvocation started = null;
                dispatcher.OnStepStarted.AddListener(invocation => started = invocation);
                ConvaiActionCommand command = new("Follow")
                {
                    Parameters = new Dictionary<string, ConvaiActionParameterValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["character"] = new()
                        {
                            Type = ConvaiActionParameterType.Reference,
                            RawValue = "Shared",
                            StringValue = "Shared",
                            ResolvedReference = new ConvaiActionParameterReference("Shared", ConvaiActionTargetKind.Character)
                        }
                    }
                };

                dispatcher.EnqueueActions(new[] { command });
                await WaitUntilAsync(() => started != null);

                Assert.That(started.ResolvedTarget?.Kind, Is.EqualTo(ConvaiActionTargetKind.Character));
                Assert.That(started.ResolvedTarget?.GameObjectReference, Is.EqualTo(characterTarget));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(objectTarget);
                UnityEngine.Object.DestroyImmediate(characterTarget);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        // ── Fixture helpers ───────────────────────────────────────────────────────────────

        private static ConvaiActionCommand CreateAction(string name, string target = null) => new(name, target);

        private static ConvaiActionInvocation CreateInvocation(
            ConvaiActionCommand command,
            ConvaiActionDefinition definition,
            ConvaiResolvedActionTarget resolvedTarget,
            ConvaiCharacter character) =>
            new(command, definition, resolvedTarget, character, 0, 0);

        /// <summary>
        ///     One ceremony for every dispatcher fixture: character rig + config source + recording
        ///     executor + dispatcher. Authored lists go through the internal Replace* seams; the
        ///     dispatcher's inspector knobs go through <see cref="SetPrivateField" />.
        /// </summary>
        private static (GameObject GameObject, ConvaiCharacter Character, ConvaiActionDispatcher Dispatcher,
            RecordingActionExecutor Executor) BuildDispatcherFixture(
            string characterId,
            string characterName,
            Func<RecordingActionExecutor, GameObject, List<ConvaiActionDefinition>> actions,
            List<ConvaiActionObjectDefinition> objects = null,
            List<ConvaiActionCharacterDefinition> characters = null,
            ConvaiActionBatchPolicy batchPolicy = ConvaiActionBatchPolicy.Queue,
            ConvaiActionBatchFailurePolicy failurePolicy = ConvaiActionBatchFailurePolicy.StopBatch,
            Action<RecordingActionExecutor> configureExecutor = null)
        {
            GameObject gameObject = CreateCharacterGameObject(characterId, characterName, out ConvaiCharacter character);
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            RecordingActionExecutor executor = gameObject.AddComponent<RecordingActionExecutor>();
            configureExecutor?.Invoke(executor);

            source.ReplaceDefinitions(actions(executor, gameObject));
            if (objects != null)
                source.ReplaceObjects(objects);
            if (characters != null)
                source.ReplaceCharacters(characters);

            ConvaiActionDispatcher dispatcher = gameObject.AddComponent<ConvaiActionDispatcher>();
            SetPrivateField(dispatcher, "_batchPolicy", batchPolicy);
            SetPrivateField(dispatcher, "_failurePolicy", failurePolicy);
            return (gameObject, character, dispatcher, executor);
        }

        private static List<ConvaiActionDefinition> TargetlessActions(
            RecordingActionExecutor executor,
            IReadOnlyList<string> actionNames,
            float timeoutSeconds = 0f)
        {
            var definitions = new List<ConvaiActionDefinition>();
            foreach (string name in actionNames)
                definitions.Add(new ConvaiActionDefinition
                {
                    ActionName = name,
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = executor,
                    TimeoutSeconds = timeoutSeconds
                });
            return definitions;
        }

        private static List<ConvaiActionObjectDefinition> ObjectsNamed(params string[] names) =>
            ObjectsNamed((IReadOnlyList<string>)names);

        private static List<ConvaiActionObjectDefinition> ObjectsNamed(IReadOnlyList<string> names)
        {
            var objects = new List<ConvaiActionObjectDefinition>();
            foreach (string name in names)
                objects.Add(new ConvaiActionObjectDefinition { Name = name });
            return objects;
        }

        private static List<ConvaiActionCharacterDefinition> CharactersNamed(params string[] names) =>
            CharactersNamed((IReadOnlyList<string>)names);

        private static List<ConvaiActionCharacterDefinition> CharactersNamed(IReadOnlyList<string> names)
        {
            var characters = new List<ConvaiActionCharacterDefinition>();
            foreach (string name in names)
                characters.Add(new ConvaiActionCharacterDefinition { Name = name });
            return characters;
        }

        private static (GameObject GameObject, ConvaiCharacter Character, ConvaiActionDispatcher Dispatcher,
            RecordingActionExecutor Executor) CreateDispatcherFixture(
            ConvaiActionBatchPolicy batchPolicy,
            ConvaiActionBatchFailurePolicy failurePolicy = ConvaiActionBatchFailurePolicy.StopBatch) =>
            BuildDispatcherFixture(
                "char-dispatcher", "Dispatcher Test",
                (executor, _) => TargetlessActions(executor, new[] { "Move To", "Pick Up", "Drop" }),
                objects: ObjectsNamed("cube"),
                batchPolicy: batchPolicy,
                failurePolicy: failurePolicy);

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher)
            CreateDispatcherFixtureWithObjects(
            IReadOnlyList<string> actionNames,
            IReadOnlyList<string> objectNames = null)
        {
            var fixture = BuildDispatcherFixture(
                "char-resolver", "Resolver Test",
                (executor, _) => TargetlessActions(executor, actionNames),
                objects: objectNames == null ? new List<ConvaiActionObjectDefinition>() : ObjectsNamed(objectNames));
            return (fixture.GameObject, fixture.Dispatcher);
        }

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher)
            CreateDispatcherFixtureWithCharacters(
            IReadOnlyList<string> actionNames,
            IReadOnlyList<string> characterNames)
        {
            var fixture = BuildDispatcherFixture(
                "char-char-resolver", "CharResolver Test",
                (executor, _) => TargetlessActions(executor, actionNames),
                characters: CharactersNamed(characterNames));
            return (fixture.GameObject, fixture.Dispatcher);
        }

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher)
            CreateDispatcherFixtureWithNullExecutor(string actionName)
        {
            var fixture = BuildDispatcherFixture(
                "char-null-exec", "NullExec Test",
                (_, _) => new List<ConvaiActionDefinition> { new() { ActionName = actionName, Executor = null } });
            return (fixture.GameObject, fixture.Dispatcher);
        }

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher)
            CreateDispatcherFixtureWithNonExecutorMonoBehaviour(string actionName)
        {
            var fixture = BuildDispatcherFixture(
                "char-bad-exec", "BadExec Test",
                (_, gameObject) => new List<ConvaiActionDefinition>
                {
                    new() { ActionName = actionName, Executor = gameObject.AddComponent<NonExecutorMonoBehaviour>() }
                });
            return (fixture.GameObject, fixture.Dispatcher);
        }

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher)
            CreateDispatcherFixtureWithRequirement(
            ConvaiActionTargetRequirement requirement,
            bool includeObjectInConfig)
        {
            var fixture = BuildDispatcherFixture(
                "char-req", "Requirement Test",
                (executor, _) => new List<ConvaiActionDefinition>
                {
                    new() { ActionName = "Move To", TargetRequirement = requirement, Executor = executor },
                    new() { ActionName = "Dance", TargetRequirement = requirement, Executor = executor }
                },
                objects: includeObjectInConfig ? ObjectsNamed("cube") : null,
                failurePolicy: ConvaiActionBatchFailurePolicy.ContinueBatch);
            return (fixture.GameObject, fixture.Dispatcher);
        }

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher)
            CreateDispatcherFixtureWithMixedTargets(
            ConvaiActionTargetRequirement requirement,
            string targetName,
            bool addObject,
            bool addCharacter)
        {
            var fixture = BuildDispatcherFixture(
                "char-mixed", "MixedTarget Test",
                (executor, _) => new List<ConvaiActionDefinition>
                {
                    new() { ActionName = "Move To", TargetRequirement = requirement, Executor = executor }
                },
                objects: addObject ? ObjectsNamed(targetName) : null,
                characters: addCharacter ? CharactersNamed(targetName) : null,
                failurePolicy: ConvaiActionBatchFailurePolicy.ContinueBatch);
            return (fixture.GameObject, fixture.Dispatcher);
        }

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher, RecordingActionExecutor Executor)
            CreateDispatcherFixtureWithFailure(
            ConvaiActionBatchPolicy batchPolicy,
            ConvaiActionBatchFailurePolicy failurePolicy)
        {
            var fixture = BuildDispatcherFixture(
                "char-fail", "Failure Test",
                (executor, _) => TargetlessActions(executor, new[] { "Move To", "Pick Up" }),
                batchPolicy: batchPolicy,
                failurePolicy: failurePolicy,
                configureExecutor: executor => executor.ResultToReturn = ConvaiActionExecutionResult.Failed("test failure"));
            return (fixture.GameObject, fixture.Dispatcher, fixture.Executor);
        }

        private static (GameObject GameObject, ConvaiActionDispatcher Dispatcher, RecordingActionExecutor Executor)
            CreateDispatcherFixtureWithTimeout(
            float timeoutSeconds,
            int executorDelayMs,
            ConvaiActionBatchFailurePolicy failurePolicy = ConvaiActionBatchFailurePolicy.StopBatch)
        {
            var fixture = BuildDispatcherFixture(
                "char-timeout", "Timeout Test",
                (executor, _) => TargetlessActions(executor, new[] { "Move To", "Pick Up" }, timeoutSeconds),
                failurePolicy: failurePolicy,
                configureExecutor: executor => executor.DelayMs = executorDelayMs);
            return (fixture.GameObject, fixture.Dispatcher, fixture.Executor);
        }

        private static RoomConnectionRuntimeAdapter CreateRuntimeAdapter(
            ConvaiCharacter character,
            CapturingRoomController controller,
            RoomSessionConnectOptions invocationOptions,
            ISessionPersistence sessionPersistence = null,
            ReconnectPolicy reconnectPolicy = null)
        {
            RoomDisconnectRuntimeAdapter disconnectAdapter = new(
                () => null,
                () => controller,
                (_, _) => { },
                (_, _, _) => { },
                () => { });

            return new RoomConnectionRuntimeAdapter(
                () => SessionState.Disconnected,
                () => false,
                () => true,
                () => 1000,
                () => true,
                () => character,
                () => new IConvaiCharacterAgent[] { character },
                new AgentRegistry(),
                () => ConnectionContext.Empty,
                _ => { },
                () => reconnectPolicy ?? ReconnectPolicy.Default,
                _ => { },
                _ => { },
                () => controller,
                () => ConvaiConnectionType.Audio,
                () => "https://core.convai.com/connect",
                () => null,
                () => null,
                TurnTakingOptions.CreateHandsFreeDefault,
                UserVadSettings.CreateDefault,
                () => null,
                () => null,
                () => invocationOptions,
                (_, _) => { },
                _ => { },
                () => sessionPersistence ?? new InMemorySessionPersistence(),
                disconnectAdapter,
                (_, _) => { },
                (_, _, _, _) => { },
                _ => { });
        }

        private static GameObject CreateCharacterGameObject(string characterId, string characterName,
            out ConvaiCharacter character)
        {
            GameObject gameObject = new($"ActionSystemTests_{characterName}");
            character = gameObject.AddComponent<ConvaiCharacter>();
            SetPrivateField(character, "_characterId", characterId);
            SetPrivateField(character, "_characterName", characterName);
            return gameObject;
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 1000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new AssertionException("Timed out waiting for condition.");

                await Task.Delay(10);
            }
        }

        /// <summary>
        ///     Reflection escape hatch for serialized inspector knobs only (dispatcher policies,
        ///     speech-gate timeout, character ids). Authored action/object/character lists go
        ///     through the internal Replace* seams on <see cref="ConvaiActionConfigSource" /> instead.
        /// </summary>
        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            field.SetValue(instance, value);
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            return (T)field.GetValue(instance);
        }

        private static void InvokePrivateMethod(object instance, string methodName)
        {
            MethodInfo method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(instance.GetType().FullName, methodName);

            method.Invoke(instance, null);
        }

        private static void RaiseCharacterSpeechStarted(ConvaiCharacter character)
        {
            FieldInfo field = typeof(ConvaiCharacter).GetField(
                "OnSpeechStarted",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "ConvaiCharacter.OnSpeechStarted backing field should exist.");
            (field.GetValue(character) as Action)?.Invoke();
        }

        private static IReadOnlyList<ConvaiActionDefinition> GetRuntimeActionDefinitions(ConvaiCharacter character)
        {
            MethodInfo method = typeof(ConvaiCharacter).GetMethod(
                "GetRuntimeActionDefinitions",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "GetRuntimeActionDefinitions should exist.");
            return method.Invoke(character, null) as IReadOnlyList<ConvaiActionDefinition>;
        }

        // ── Inner test types ──────────────────────────────────────────────────────────────

        private sealed class RecordingActionExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public readonly List<string> ExecutedActions = new();
            public readonly TaskCompletionSource<bool> Started = new();
            public bool HoldUntilCancelled { get; set; }
            public int DelayMs { get; set; }
            public bool CancellationObserved { get; private set; }
            public ConvaiActionExecutionResult ResultToReturn { get; set; } = ConvaiActionExecutionResult.Succeeded();

            public async Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation,
                CancellationToken cancellationToken)
            {
                ExecutedActions.Add(invocation.Command.ToString());
                Started.TrySetResult(true);

                try
                {
                    if (HoldUntilCancelled)
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    else if (DelayMs > 0)
                        await Task.Delay(DelayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }

                return ResultToReturn;
            }
        }

        private sealed class PutParameters
        {
            [ConvaiActionParameter("item")]
            public string Item { get; set; }

            [ConvaiActionParameter("container")]
            public ConvaiResolvedActionTarget Container { get; set; }
        }

        private sealed class RecordingTypedPutExecutor : ConvaiActionExecutor<PutParameters>
        {
            public PutParameters LastParameters { get; private set; }

            protected override Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation,
                PutParameters parameters,
                CancellationToken cancellationToken)
            {
                LastParameters = parameters;
                return Task.FromResult(ConvaiActionExecutionResult.Succeeded());
            }
        }

        private sealed class NonExecutorMonoBehaviour : MonoBehaviour
        {
        }

        private sealed class InMemorySessionPersistence : ISessionPersistence
        {
            private readonly Dictionary<string, string> _sessions = new();

            public string LoadSession(string characterId) =>
                _sessions.TryGetValue(characterId ?? string.Empty, out string sessionId) ? sessionId : null;

            public void SaveSession(string characterId, string sessionId) =>
                _sessions[characterId ?? string.Empty] = sessionId;

            public void ClearSession(string characterId) => _sessions.Remove(characterId ?? string.Empty);
            public void ClearAllSessions() => _sessions.Clear();
            public bool HasSession(string characterId) => _sessions.ContainsKey(characterId ?? string.Empty);
        }

        private sealed class CapturingRoomController : IConvaiRoomController
        {
            public RoomJoinOptions LastJoinOptions { get; private set; }
            public string LastStoredSessionId { get; private set; }
            public bool HasRoomDetails => true;
            public bool IsConnectedToRoom => true;
            public bool IsMicMuted => false;
            public string SessionID => "session-id";
            public string CharacterSessionID => "character-session-id";
            public string RoomName => "room-name";
            public string RoomURL => "wss://room-url";
            public string Token => "token";
            public string ResolvedSpeakerId => string.Empty;
            public string RequestTraceId => string.Empty;
            public string ResolvedEndUserId => string.Empty;
            public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata => null;
            public RTVIHandler RTVIHandler => null;
            public IRoomFacade CurrentRoom => null;

            public event Action OnRoomConnectionSuccessful
            {
                add { }
                remove { }
            }

            public event Action OnRoomConnectionFailed
            {
                add { }
                remove { }
            }

            public event Action<bool> OnMicMuteChanged
            {
                add { }
                remove { }
            }

            public event Action OnRoomReconnecting
            {
                add { }
                remove { }
            }

            public event Action OnRoomReconnected
            {
                add { }
                remove { }
            }

            public event Action OnUnexpectedRoomDisconnected
            {
                add { }
                remove { }
            }

            public event Action<IRemoteAudioTrack, string, string> OnRemoteAudioTrackSubscribed
            {
                add { }
                remove { }
            }

            public event Action<string, string> OnRemoteAudioTrackUnsubscribed
            {
                add { }
                remove { }
            }

            public Task<RoomConnectionAttemptResult> InitializeAsync(
                string connectionType,
                string coreServerUrl,
                string characterId,
                string storedSessionId,
                bool enableSessionResume,
                string dynamicInfoText,
                bool keepDynamicInfoInContext) =>
                InitializeAsync(
                    connectionType,
                    coreServerUrl,
                    characterId,
                    storedSessionId,
                    enableSessionResume,
                    dynamicInfoText,
                    keepDynamicInfoInContext,
                    null,
                    CancellationToken.None);

            public Task<RoomConnectionAttemptResult> InitializeAsync(
                string connectionType,
                string coreServerUrl,
                string characterId,
                string storedSessionId,
                bool enableSessionResume,
                string dynamicInfoText,
                bool keepDynamicInfoInContext,
                RoomJoinOptions joinOptions,
                CancellationToken cancellationToken = default)
            {
                LastStoredSessionId = storedSessionId;
                LastJoinOptions = joinOptions;
                return Task.FromResult(RoomConnectionAttemptResult.Success());
            }

            public void DisconnectFromRoom()
            {
            }

            public Task DisconnectFromRoomAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void SetMicMuted(bool mute) { }
            public void ToggleMicMute() { }
            public bool SetCharacterAudioMuted(string characterId, bool mute) => true;
            public bool MuteCharacter(string characterId) => true;
            public bool UnmuteCharacter(string characterId) => true;
            public bool IsCharacterAudioMuted(string characterId) => false;
            public void SetAudioSubscriptionPolicy(Func<string, bool> policy) { }
            public void ApplyRemoteAudioPreference(string characterId, bool enabled) { }
            public void Dispose() { }
        }
    }
}
