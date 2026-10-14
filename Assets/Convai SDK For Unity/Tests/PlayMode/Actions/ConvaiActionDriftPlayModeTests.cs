using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Actions
{
    /// <summary>
    ///     An action that holds until the test lets it go, so a batch can be caught mid-flight.
    /// </summary>
    /// <remarks>
    ///     The window this suite needs — admitted, then the scene changes, then dispatched — does not
    ///     exist unless something occupies the dispatcher. A real action does: a character walking
    ///     across a room is busy for seconds, which is exactly how long the world has to move under a
    ///     command it already agreed to. This executor is that walk, with its duration under the
    ///     test's control instead of the scene's.
    /// </remarks>
    internal sealed class HoldingActionExecutor : MonoBehaviour, IConvaiActionExecutor
    {
        private readonly TaskCompletionSource<bool> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the dispatcher has actually reached this action.</summary>
        internal bool Running { get; private set; }

        public async Task<ConvaiActionExecutionResult> ExecuteAsync(
            ConvaiActionInvocation invocation, CancellationToken cancellationToken)
        {
            Running = true;

            // Cancellation is honoured rather than ignored, so a cancelled batch cannot leave this
            // awaiting forever and take the editor with it — the same rule the ingress suite's
            // worker helper follows for the same reason.
            using (cancellationToken.Register(() => _released.TrySetResult(false)))
                await _released.Task;

            return ConvaiActionExecutionResult.Succeeded();
        }

        /// <summary>Lets the held action finish.</summary>
        internal void Release() => _released.TrySetResult(true);
    }

    /// <summary>Records what it was handed, which is how the tests read the dispatch-time answer.</summary>
    internal sealed class RecordingActionExecutor : MonoBehaviour, IConvaiActionExecutor
    {
        /// <summary>How many times the dispatcher actually ran this action.</summary>
        internal int Runs { get; private set; }

        /// <summary>The target resolved at dispatch, not at admission.</summary>
        internal ConvaiResolvedActionTarget LastTarget { get; private set; }

        public Task<ConvaiActionExecutionResult> ExecuteAsync(
            ConvaiActionInvocation invocation, CancellationToken cancellationToken)
        {
            Runs++;
            LastTarget = invocation?.ResolvedTarget;
            return Task.FromResult(ConvaiActionExecutionResult.Succeeded());
        }
    }

    /// <summary>
    ///     The admission/dispatch drift report — what the character agreed to, against what it did.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is a PlayMode question by construction.</b> Drift only exists because time
    ///         passes between the two resolutions: a command is judged the moment it arrives and
    ///         performed once the queue reaches it. EditMode has no queue that drains, no frame in
    ///         which a target can be withdrawn, and no <c>OnEnable</c> to register a
    ///         <c>ConvaiActionTarget</c> in the first place, so there is nowhere in it for the world
    ///         to move.
    ///     </para>
    ///     <para>
    ///         The setup is deliberately the ordinary one rather than a contrivance: a batch that is
    ///         still running, a second batch queued behind it, and the scene changing while the
    ///         character is busy. That is the shape of every real occurrence — the character says it
    ///         will walk to the door and something happens to the door before it gets there.
    ///     </para>
    ///     <para>
    ///         <b>The limit this suite also pins.</b> Drift is compared <em>by name</em>. Two distinct
    ///         objects sharing a name and swapping which is nearer therefore produce no report, and
    ///         the last test here asserts that silence on purpose. It is not an oversight: the only
    ///         way to notice would be to carry a live reference across the queue, and a reference held
    ///         across a queue is precisely the stale handle the name-only design exists to avoid. The
    ///         test states what the design promises, not what it declines to promise.
    ///     </para>
    /// </remarks>
    public sealed class ConvaiActionDriftPlayModeTests
    {
        private const string HoldAction = "Hold Position";
        private const string WalkAction = "Walk To";

        private readonly ConvaiActionPlayModeScene _scene = new();
        private readonly List<string> _log = new();

        [SetUp]
        public void SetUp()
        {
            _log.Clear();
            UnityEngine.Application.logMessageReceived += Capture;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Application.logMessageReceived -= Capture;
            _scene.Dispose();
        }

        private void Capture(string condition, string stackTrace, LogType type) => _log.Add(condition);

        /// <summary>Whether any captured line contains all of the given fragments.</summary>
        private bool Logged(params string[] fragments)
        {
            for (int i = 0; i < _log.Count; i++)
            {
                bool all = true;
                for (int f = 0; f < fragments.Length; f++)
                {
                    if (_log[i].IndexOf(fragments[f], StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    all = false;
                    break;
                }

                if (all)
                    return true;
            }

            return false;
        }

        private bool LoggedDrift() => Logged("was accepted for");

        // ── Setup ───────────────────────────────────────────────────────────────────────

        /// <summary>
        ///     A character with a dispatcher, a holdable action and a targeted one.
        /// </summary>
        private ConvaiActionDispatcher BuildDispatcher(
            out HoldingActionExecutor hold, out RecordingActionExecutor walk)
        {
            ConvaiActionDispatcher dispatcher = _scene.Dispatcher("playmode-drift-character");
            GameObject host = dispatcher.gameObject;

            hold = host.AddComponent<HoldingActionExecutor>();
            walk = host.AddComponent<RecordingActionExecutor>();

            ConvaiActionConfigSource source = host.GetComponent<ConvaiActionConfigSource>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = HoldAction,
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = hold
                },
                new()
                {
                    ActionName = WalkAction,
                    TargetRequirement = ConvaiActionTargetRequirement.Object,
                    Executor = walk
                }
            });

            return dispatcher;
        }

        /// <summary>Adds an enabled scene target under an explicit name.</summary>
        private ConvaiActionTarget TargetNamed(string targetName, string objectName, Vector3 position)
        {
            ConvaiActionTarget target = _scene.At(objectName, position).AddComponent<ConvaiActionTarget>();
            target.TargetName = targetName;
            target.enabled = true;
            return target;
        }

        /// <summary>
        ///     Yields frames until a condition holds, and fails rather than waiting forever.
        /// </summary>
        private static IEnumerator Until(Func<bool> condition, string what, float budgetSeconds = 5f)
        {
            float deadline = Time.realtimeSinceStartup + budgetSeconds;
            while (!condition() && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.That(condition(), Is.True, $"Timed out waiting for {what}.");
        }

        // ── The report ──────────────────────────────────────────────────────────────────

        /// <summary>
        ///     A target withdrawn while the character is busy is reported, and the step does not run.
        /// </summary>
        /// <remarks>
        ///     The plainest drift there is: the character agreed to walk to the door, and by the time
        ///     it was free the door was no longer something it could act on. Nothing here is a fault
        ///     — admission was right when it ran and dispatch is right now — which is exactly why
        ///     nothing else in the pipeline has a reason to mention it.
        /// </remarks>
        [UnityTest]
        public IEnumerator AWithdrawnTargetIsReportedWhenTheQueueReachesTheCommand()
        {
            Assert.That(
                LoggingConfig.IsWarningEnabled(LogCategory.Actions),
                Is.True,
                "The drift report is a warning on the Actions category. With Actions warnings "
                + "silenced there is nothing for this test to observe, and it would pass by looking "
                + "at a console nobody wrote to.");

            ConvaiActionDispatcher dispatcher = BuildDispatcher(
                out HoldingActionExecutor hold, out RecordingActionExecutor walk);
            ConvaiActionTarget door = TargetNamed("Door", "door-object", new Vector3(2f, 0f, 0f));
            yield return null;

            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand(HoldAction) });
            yield return Until(() => hold.Running, "the held action to start");

            // Admitted now, against the scene as it currently stands.
            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand(WalkAction, "Door") });
            yield return null;

            // And the world moves while the character is still busy with the first batch.
            door.enabled = false;

            hold.Release();
            yield return Until(() => LoggedDrift(), "the drift report");

            Assert.That(
                Logged("was accepted for", "Door", "nothing at all"),
                Is.True,
                "The report must name what was agreed to and what the name means now; without both "
                + "it says only that something changed, which is the part the reader can already see.");

            Assert.That(
                walk.Runs,
                Is.Zero,
                "With the target withdrawn there is nothing to walk to, so the step is declined. "
                + "Drift is reported either way — it says why the decline happened, rather than "
                + "leaving a command that was accepted a moment ago looking arbitrarily refused.");
        }

        /// <summary>
        ///     When the name means something else by dispatch, the step runs on the something else.
        /// </summary>
        /// <remarks>
        ///     The half that matters more than the warning. Resolving again at dispatch is correct —
        ///     the freshest answer is the right one — so the executor must receive the current target,
        ///     not the admitted one. What the report adds is that the substitution was visible at all.
        /// </remarks>
        [UnityTest]
        public IEnumerator TheStepRunsOnTheTargetTheNameMeansAtDispatch()
        {
            ConvaiActionDispatcher dispatcher = BuildDispatcher(
                out HoldingActionExecutor hold, out RecordingActionExecutor walk);

            ConvaiActionTarget door = TargetNamed("Door", "door-object", new Vector3(2f, 0f, 0f));
            TargetNamed("Front Door", "front-door-object", new Vector3(20f, 0f, 0f));
            yield return null;

            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand(HoldAction) });
            yield return Until(() => hold.Running, "the held action to start");

            // "Door" is an exact match while it exists, so admission resolves to it and not to the
            // fuzzier "Front Door" standing further away.
            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand(WalkAction, "Door") });
            yield return null;

            door.enabled = false;

            hold.Release();
            yield return Until(() => walk.Runs > 0, "the walk step to run");

            Assert.That(
                walk.LastTarget?.Name,
                Is.EqualTo("Front Door"),
                "Dispatch resolves again, so the executor is handed what the name means now. Freezing "
                + "the admitted answer and passing that on would hand an executor a target the scene "
                + "no longer offers.");

            Assert.That(
                Logged("was accepted for", "Door", "Front Door"),
                Is.True,
                "The character agreed to one thing and did another, correctly, and this line is the "
                + "only place that difference is ever stated.");
        }

        // ── The limit, asserted rather than assumed ─────────────────────────────────────

        /// <summary>
        ///     Two things sharing a name swapping places is not reported, by design.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Raised in independent review and deliberately not fixed. The character walks to a
        ///         different object from the one admission picked and says nothing, because the only
        ///         thing that changed is which object answers to a name — and the report compares
        ///         names.
        ///     </para>
        ///     <para>
        ///         Closing it would mean carrying the resolved target's identity across the queue,
        ///         which is the stale reference the name-only design was chosen to avoid: the handle
        ///         would then be the thing that goes wrong, in the same window, with no report at all.
        ///         This test exists so the limit is a recorded decision rather than a gap somebody
        ///         later "fixes" without knowing what it bought.
        ///     </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator TwoTargetsSharingANameSwappingPlacesIsNotReported()
        {
            ConvaiActionDispatcher dispatcher = BuildDispatcher(
                out HoldingActionExecutor hold, out RecordingActionExecutor walk);

            ConvaiActionTarget near = TargetNamed("Door", "door-near", new Vector3(2f, 0f, 0f));
            TargetNamed("Door", "door-far", new Vector3(30f, 0f, 0f));
            yield return null;

            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand(HoldAction) });
            yield return Until(() => hold.Running, "the held action to start");

            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand(WalkAction, "Door") });
            yield return null;

            // The near one leaves, so the far one is what "Door" now means — a different object
            // under the same name.
            near.enabled = false;

            hold.Release();

            // Waiting on the step rather than on a timeout is what makes the silence below a
            // measurement instead of a race: drift is reported inside the same resolve that
            // produces the invocation, so if there were a report it would already be in the log by
            // the time the executor ran.
            yield return Until(() => walk.Runs > 0, "the walk step to run");

            Assert.That(
                walk.LastTarget?.Name,
                Is.EqualTo("Door"),
                "Sanity: the name still resolves, to the other object.");

            Assert.That(
                LoggedDrift(),
                Is.False,
                "No drift is reported here and that is the design: names are compared, so a swap "
                + "between two things called the same thing is invisible. If this ever goes red, the "
                + "comparison started carrying identity — which is a decision to make deliberately, "
                + "not a test to update.");
        }
    }
}
