using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     EditMode coverage for the reporting of action commands that never ran.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These paths matter more than most, because a mistake in them is invisible by
    ///         definition: a command dropped without an explanation produces no error, no spoken
    ///         failure and no row in any tool — exactly the symptom that made this class of fault
    ///         take an afternoon to diagnose rather than a minute.
    ///     </para>
    ///     <para>
    ///         So the tests pin two things together: that every door produces an actionable
    ///         sentence, and that producing it costs nothing when nobody is listening.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionDropReportingTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        private static ConvaiActionDefinition BoundAction(
            string name,
            ConvaiActionTargetRequirement requirement,
            MonoBehaviour executor) =>
            new()
            {
                ActionName = name,
                Description = "Test action.",
                TargetRequirement = requirement,
                Executor = executor
            };

        /// <summary>
        ///     A behavior that accepts anything: the tests here are about what reaches an executor,
        ///     never about what one does.
        /// </summary>
        private sealed class AcceptingExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public System.Threading.Tasks.Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, System.Threading.CancellationToken cancellationToken) =>
                System.Threading.Tasks.Task.FromResult(ConvaiActionExecutionResult.Succeeded("ok"));
        }

        // ── The gate ─────────────────────────────────────────────────────────────────────

        /// <summary>
        ///     With nobody listening, a dropped command is still counted but never described. This is
        ///     the whole performance contract: the sentence, the candidate list and the joins that
        ///     build them are the expensive part, and a shipped build must not pay for diagnostics it
        ///     does not show.
        /// </summary>
        [Test]
        public void DroppedCommands_AreCountedButNotDescribedWhenNobodyIsListening()
        {
            var drops = new ConvaiActionDropCollector(false);

            ConvaiActionResponseParser.FilterExecutableBatch(
                new[] { new ConvaiActionCommand("No Such Action") },
                new ConvaiActionConfig(),
                new List<ConvaiActionDefinition>(),
                drops);

            Assert.That(drops.DroppedCount, Is.EqualTo(1), "The command must still be counted.");
            Assert.That(drops.Reports, Is.Empty, "Nothing was listening, so nothing should have been built.");
            Assert.That(
                drops.CountsByReason[ConvaiActionResponseParser.RejectionUnknownOrUnexecutableAction],
                Is.EqualTo(1));
        }

        // ── One door per reason ──────────────────────────────────────────────────────────

        /// <summary>
        ///     An action name Unity does not have. Before this, the single most likely drop in a real
        ///     project produced no output whatsoever.
        /// </summary>
        [Test]
        public void UnknownAction_IsExplainedAndNamesWhatCanActuallyRun()
        {
            var host = new GameObject("drop-tests-unknown");
            try
            {
                var executor = host.AddComponent<AcceptingExecutor>();
                var definitions = new List<ConvaiActionDefinition>
                {
                    BoundAction("Wave", ConvaiActionTargetRequirement.None, executor)
                };
                var drops = new ConvaiActionDropCollector(true);

                ConvaiActionResponseParser.FilterExecutableBatch(
                    new[] { new ConvaiActionCommand("Teleport") },
                    new ConvaiActionConfig(),
                    definitions,
                    drops);

                Assert.That(drops.Reports.Count, Is.EqualTo(1));
                ConvaiActionDropReport report = drops.Reports[0];
                Assert.That(report.Reason, Is.EqualTo(ConvaiActionDropReason.UnknownOrUnexecutableAction));
                Assert.That(report.ActionName, Is.EqualTo("Teleport"));
                Assert.That(report.Explanation, Does.Contain("Teleport"));
                Assert.That(report.Explanation, Does.Contain("Wave"),
                    "The explanation has to say what the character can run, or it is not actionable.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        ///     An action that exists but has no Action Behavior bound reads differently from one that
        ///     does not exist, because the fix is different: bind a behavior, versus correct a name.
        /// </summary>
        [Test]
        public void ActionWithNoBehaviorBound_SaysSoRatherThanCallingItUnknown()
        {
            var definitions = new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Wave", Description = "Wave.", Executor = null }
            };
            var drops = new ConvaiActionDropCollector(true);

            ConvaiActionResponseParser.FilterExecutableBatch(
                new[] { new ConvaiActionCommand("Wave") },
                new ConvaiActionConfig(),
                definitions,
                drops);

            Assert.That(drops.Reports.Count, Is.EqualTo(1));
            Assert.That(drops.Reports[0].Explanation, Does.Contain("Action Behavior"));
        }

        /// <summary>
        ///     A target that matches nothing names both what was asked for and what was on offer —
        ///     the two facts that turn this from a mystery into an alias.
        /// </summary>
        [Test]
        public void UnresolvedTarget_NamesWhatWasAskedForAndWhatWasOffered()
        {
            var host = new GameObject("drop-tests-target");
            try
            {
                var executor = host.AddComponent<AcceptingExecutor>();
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "Gallery Room", Description = "The east room." }
                    }
                };
                var definitions = new List<ConvaiActionDefinition>
                {
                    BoundAction("Walk To", ConvaiActionTargetRequirement.Either, executor)
                };
                var drops = new ConvaiActionDropCollector(true);

                ConvaiActionResponseParser.FilterExecutableBatch(
                    new[] { new ConvaiActionCommand("Walk To", "The Observatory") },
                    config,
                    definitions,
                    drops);

                Assert.That(drops.Reports.Count, Is.EqualTo(1));
                ConvaiActionDropReport report = drops.Reports[0];
                Assert.That(report.Reason, Is.EqualTo(ConvaiActionDropReason.RequiredTargetUnresolved));
                Assert.That(report.RequestedTarget, Is.EqualTo("The Observatory"));
                Assert.That(report.Explanation, Does.Contain("The Observatory"));
                Assert.That(report.Explanation, Does.Contain("Gallery Room"));
                Assert.That(report.Explanation, Does.Contain("alias"),
                    "The sentence has to end in what to do about it.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        ///     Naming nothing is a different fault from naming something unknown, and the two need
        ///     opposite fixes — the action's description versus the target list. Telling a developer
        ///     to add an alias when no name was ever sent sends them somewhere there is nothing to
        ///     find.
        /// </summary>
        [Test]
        public void TargetlessCommand_ReadsDifferentlyFromAnUnknownName()
        {
            var host = new GameObject("drop-tests-targetless");
            try
            {
                var executor = host.AddComponent<AcceptingExecutor>();
                var definitions = new List<ConvaiActionDefinition>
                {
                    BoundAction("Walk To", ConvaiActionTargetRequirement.Either, executor)
                };
                var drops = new ConvaiActionDropCollector(true);

                ConvaiActionResponseParser.FilterExecutableBatch(
                    new[] { new ConvaiActionCommand("Walk To") },
                    new ConvaiActionConfig(),
                    definitions,
                    drops);

                Assert.That(drops.Reports.Count, Is.EqualTo(1));
                ConvaiActionDropReport report = drops.Reports[0];
                Assert.That(report.RequestedTarget, Is.Empty);
                Assert.That(report.Explanation, Does.Contain("named nothing"));
                Assert.That(report.Explanation, Does.Not.Contain("alias"),
                    "Nothing was named, so an alias cannot be the fix.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        ///     A name that matched a real entry with nothing built behind it is not the same fault as
        ///     a name that matched nothing, and the advice is opposite: link an object (or say it is
        ///     deliberate) versus add an alias. Telling an author to add an alias for a name that
        ///     already matches sends them to look for something that is not there.
        /// </summary>
        [Test]
        public void NameThatMatchesAnEntryWithNothingInTheScene_SaysSoRatherThanSuggestingAnAlias()
        {
            var host = new GameObject("drop-tests-unbuilt");
            try
            {
                var executor = host.AddComponent<AcceptingExecutor>();
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "The Gallery", Description = "Talked about, never built." }
                    }
                };
                var definitions = new List<ConvaiActionDefinition>
                {
                    BoundAction("Walk To", ConvaiActionTargetRequirement.Either, executor)
                };
                var drops = new ConvaiActionDropCollector(true);

                ConvaiActionResponseParser.FilterExecutableBatch(
                    new[] { new ConvaiActionCommand("Walk To", "The Gallery") }, config, definitions, drops);

                Assert.That(drops.Reports.Count, Is.EqualTo(1));
                string explanation = drops.Reports[0].Explanation;
                Assert.That(explanation, Does.Contain("nothing in the scene answers to it"));
                Assert.That(explanation, Does.Contain("Text Only"));
                Assert.That(explanation, Does.Not.Contain("as an alias"),
                    "The name already matches; an alias would fix nothing.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        ///     Asking to greet a statue of somebody. The name matches, the kind does not, and saying
        ///     only "unresolved" hides the one fact that explains it.
        /// </summary>
        [Test]
        public void NameThatMatchesTheWrongKindOfThing_SaysWhichKindItFound()
        {
            var host = new GameObject("drop-tests-wrongkind");
            try
            {
                var executor = host.AddComponent<AcceptingExecutor>();
                var statue = new GameObject("drop-tests-statue");
                try
                {
                    var config = new ConvaiActionConfig
                    {
                        Objects = new List<ConvaiActionObjectDefinition>
                        {
                            new() { Name = "Sofia", Description = "A statue of her.", GameObjectReference = statue }
                        }
                    };
                    var definitions = new List<ConvaiActionDefinition>
                    {
                        BoundAction("Greet", ConvaiActionTargetRequirement.Character, executor)
                    };
                    var drops = new ConvaiActionDropCollector(true);

                    ConvaiActionResponseParser.FilterExecutableBatch(
                        new[] { new ConvaiActionCommand("Greet", "Sofia") }, config, definitions, drops);

                    Assert.That(drops.Reports.Count, Is.EqualTo(1));
                    string explanation = drops.Reports[0].Explanation;
                    Assert.That(explanation, Does.Contain("does have something by that name"));
                    Assert.That(explanation, Does.Contain("an object"));
                    Assert.That(explanation, Does.Contain("needs a person"));
                }
                finally
                {
                    Object.DestroyImmediate(statue);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        // ── Reason vocabulary ────────────────────────────────────────────────────────────

        /// <summary>
        ///     The wire names predate the enum and are read by tooling and by the summary line, so
        ///     they are contract rather than formatting. Pinning them here means a future rename of
        ///     an enum member cannot quietly change what the outside world sees.
        /// </summary>
        [Test]
        public void ReasonKeys_AreStableWireNames()
        {
            // Asserted as one table rather than as TestCases because the reason type is internal and
            // a [TestCase] argument would have to widen it just to be listed.
            Assert.That(ConvaiActionDropReport.ReasonKey(ConvaiActionDropReason.MalformedEntry),
                Is.EqualTo("malformed_entry"));
            Assert.That(ConvaiActionDropReport.ReasonKey(ConvaiActionDropReason.RuntimeSourceUnavailable),
                Is.EqualTo("runtime_source_unavailable"));
            Assert.That(ConvaiActionDropReport.ReasonKey(ConvaiActionDropReason.UnknownOrUnexecutableAction),
                Is.EqualTo("unknown_or_unexecutable_action"));
            Assert.That(ConvaiActionDropReport.ReasonKey(ConvaiActionDropReason.RequiredTargetUnresolved),
                Is.EqualTo("required_target_unresolved"));
            Assert.That(ConvaiActionDropReport.ReasonKey(ConvaiActionDropReason.ReferenceParameterUnresolved),
                Is.EqualTo("reference_parameter_unresolved"));
            Assert.That(ConvaiActionDropReport.ReasonKey(ConvaiActionDropReason.QueueBusy),
                Is.EqualTo("queue_busy"));
            Assert.That(ConvaiActionDropReport.ReasonKey(ConvaiActionDropReason.DispatcherUnavailable),
                Is.EqualTo("dispatcher_unavailable"));
        }

        /// <summary>
        ///     The bridge that lets the feedback relay's existing authored lines cover drops without
        ///     a second line set. A target that could not be found has to arrive as TargetMissing, or
        ///     "I can't find {target}." never gets chosen for it.
        /// </summary>
        [Test]
        public void DropReasons_MapOntoTheFailureVocabularyTheRelayAlreadySpeaks()
        {
            Assert.That(ConvaiActionDropReport.ToFailureReason(
                    ConvaiActionDropReason.RequiredTargetUnresolved),
                Is.EqualTo(ConvaiActionFailureReason.TargetMissing),
                "Otherwise the relay's \"I can't find {target}.\" line is never chosen for a drop.");
            Assert.That(ConvaiActionDropReport.ToFailureReason(
                    ConvaiActionDropReason.ReferenceParameterUnresolved),
                Is.EqualTo(ConvaiActionFailureReason.TargetMissing));
            Assert.That(ConvaiActionDropReport.ToFailureReason(ConvaiActionDropReason.QueueBusy),
                Is.EqualTo(ConvaiActionFailureReason.Busy),
                "A transient refusal reported as a state problem sends the reader to check a " +
                "rig that is working perfectly well.");
            Assert.That(ConvaiActionDropReport.ToFailureReason(
                    ConvaiActionDropReason.UnknownOrUnexecutableAction),
                Is.EqualTo(ConvaiActionFailureReason.InvalidState));
            Assert.That(ConvaiActionDropReport.ToFailureReason(ConvaiActionDropReason.MalformedEntry),
                Is.EqualTo(ConvaiActionFailureReason.Custom));
        }

        /// <summary>
        ///     What the character says is about the world, never about the SDK: the developer's
        ///     explanation names action configs and aliases, and none of that belongs in a line a
        ///     player can hear.
        /// </summary>
        [Test]
        public void WhatTheCharacterIsToldIsAboutTheWorld_NotAboutTheSdk()
        {
            var report = new ConvaiActionDropReport(
                ConvaiActionDropReason.RequiredTargetUnresolved,
                "Walk To",
                "The Observatory",
                "Gallery Room",
                "Dropped 'Walk To': it asked for 'The Observatory' ... Add an alias on the intended " +
                "Convai Action Target. Offered right now: Gallery Room.");

            string fact = ConvaiActionFeedbackComposer.ComposeDrop(report).Fact;

            Assert.That(fact, Does.Contain("The Observatory"));
            Assert.That(fact, Does.Not.Contain("alias"));
            Assert.That(fact, Does.Not.Contain("Convai Action Target"));
            Assert.That(fact, Does.Not.Contain("Dropped"));
        }
    }
}
