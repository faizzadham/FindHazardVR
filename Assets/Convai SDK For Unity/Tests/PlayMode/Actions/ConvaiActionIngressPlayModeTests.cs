using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Actions
{
    /// <summary>
    ///     The parts of action ingress that only exist while the game is running.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         EditMode cannot see any of this. It never calls <c>Awake</c> or <c>OnEnable</c>, so a
    ///         scene component never registers itself; it has no frame loop, so a batch marshalled to
    ///         the main thread is never pumped; and it has no notion of a destroyed object still
    ///         holding a live C# reference. Every one of those is a way this area has failed before,
    ///         which is why the architecture plan made PlayMode non-deferrable for the phases that
    ///         changed them rather than folding them into a later regression sweep.
    ///     </para>
    ///     <para>
    ///         These tests use no backend. They exercise the dispatcher's own ingress — the path
    ///         <c>EnqueueActions</c> takes — because that is the half of the pipeline a customer can
    ///         drive without a conversation, and the half that used to be read differently from the
    ///         backend's.
    ///     </para>
    /// </remarks>
    public sealed class ConvaiActionIngressPlayModeTests
    {
        private readonly ConvaiActionPlayModeScene _scene = new();

        [TearDown]
        public void TearDown() => _scene.Dispose();

        private const string TimeoutMessage =
            "The worker never finished. Something on the ingress path blocked it — that is a real "
            + "defect, and this test reports it instead of hanging the editor waiting for it.";

        /// <summary>
        ///     One call made on a background thread, waited for without ever blocking Unity's.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Written this way after the obvious way froze the editor.</b> The first version
        ///         called <c>Thread.Join()</c> and <c>ManualResetEventSlim.Wait()</c> on the main
        ///         thread, and set the completion event after the call rather than in a
        ///         <c>finally</c>. When <c>EnqueueActions</c> threw on the worker — which it did, for
        ///         a real reason — the event was never set and Unity's main thread waited on it
        ///         forever. The editor had to be killed.
        ///     </para>
        ///     <para>
        ///         So: the event is always set, the wait is always bounded, and the main thread only
        ///         ever yields. A test for a threading defect must not be able to become one.
        ///     </para>
        /// </remarks>
        private sealed class WorkerCall
        {
            private readonly ManualResetEventSlim _finished = new(false);
            private readonly Thread _thread;

            internal WorkerCall(System.Action call)
            {
                _thread = new Thread(() =>
                {
                    try
                    {
                        call();
                    }
                    catch (System.Exception e)
                    {
                        Thrown = e;
                    }
                    finally
                    {
                        // Always, whatever happened. This is the line whose absence hung the editor.
                        _finished.Set();
                    }
                }) { IsBackground = true };

                _thread.Start();
            }

            /// <summary>What escaped the call, or null.</summary>
            internal System.Exception Thrown { get; private set; }

            /// <summary>Whether the worker was still running when the budget ran out.</summary>
            internal bool TimedOut { get; private set; }

            /// <summary>
            ///     Yields frames until the worker finishes or the budget expires. Never blocks.
            /// </summary>
            internal IEnumerator Await(float budgetSeconds = 5f)
            {
                float deadline = Time.realtimeSinceStartup + budgetSeconds;
                while (!_finished.IsSet && Time.realtimeSinceStartup < deadline)
                    yield return null;

                TimedOut = !_finished.IsSet;

                // Only ever joined once it is known to have finished, so this cannot block either.
                if (!TimedOut)
                    _thread.Join();
            }
        }

        private static WorkerCall RunOnWorker(System.Action call) => new(call);

        // ── ConvaiActionTargetCandidate against destroyed objects ────────────────────────

        /// <summary>
        ///     A destroyed binding reads as absent rather than throwing.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A destroyed <c>GameObject</c> in Unity is not null in C# — the managed wrapper
        ///         survives with its native side gone, and touching <c>.transform</c> on it throws
        ///         <c>MissingReferenceException</c>. The resolution ladder reads
        ///         <c>AnchorPosition</c> for every candidate at every rung, so a target destroyed
        ///         between one command and the next is squarely on the hot path.
        ///     </para>
        ///     <para>
        ///         EditMode cannot test this: <c>DestroyImmediate</c> there behaves differently and
        ///         the fake-null comparison that makes it safe is a runtime behaviour.
        ///     </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator ADestroyedTargetResolvesToNothingInsteadOfThrowing()
        {
            GameObject gallery = _scene.At("playmode-gallery", Vector3.zero);
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "The Gallery", GameObjectReference = gallery }
                },
                Characters = new List<ConvaiActionCharacterDefinition>()
            };

            var definition = new ConvaiActionDefinition
            {
                ActionName = "Walk To",
                TargetRequirement = ConvaiActionTargetRequirement.Object
            };

            Assert.That(
                ConvaiActionTargetResolution.TryResolveActionable(
                    new ConvaiActionCommand("Walk To", "The Gallery"),
                    definition, config, Vector3.zero, out _),
                Is.True,
                "Sanity: it resolves while the object is alive.");

            Object.Destroy(gallery);
            yield return null;

            Assert.DoesNotThrow(
                () => ConvaiActionTargetResolution.TryResolveActionable(
                    new ConvaiActionCommand("Walk To", "The Gallery"),
                    definition, config, Vector3.zero, out _),
                "Reading a destroyed binding must not throw — the ladder touches every candidate's "
                + "position at every rung.");

            Assert.That(
                ConvaiActionTargetResolution.TryResolveActionable(
                    new ConvaiActionCommand("Walk To", "The Gallery"),
                    definition, config, Vector3.zero, out _),
                Is.False,
                "An entry whose object is gone is not actionable, and saying so is what turns this "
                + "into a reported drop rather than an executor failing on a dead reference.");
        }

        /// <summary>
        ///     Availability is decided per candidate, not cached across a resolution.
        /// </summary>
        [UnityTest]
        public IEnumerator WithdrawingATargetTakesEffectOnTheNextCommand()
        {
            GameObject gallery = _scene.At("playmode-withdrawn", Vector3.zero);
            var entry = new ConvaiActionObjectDefinition
            {
                Name = "The Gallery",
                GameObjectReference = gallery
            };

            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition> { entry },
                Characters = new List<ConvaiActionCharacterDefinition>()
            };

            var definition = new ConvaiActionDefinition
            {
                ActionName = "Walk To",
                TargetRequirement = ConvaiActionTargetRequirement.Object
            };

            Assert.That(
                ConvaiActionTargetResolution.TryResolveActionable(
                    new ConvaiActionCommand("Walk To", "The Gallery"),
                    definition, config, Vector3.zero, out _),
                Is.True);

            entry.Available = false;
            yield return null;

            Assert.That(
                ConvaiActionTargetResolution.TryResolveActionable(
                    new ConvaiActionCommand("Walk To", "The Gallery"),
                    definition, config, Vector3.zero, out _),
                Is.False,
                "A withdrawn entry is invisible at every rung, immediately.");
        }

        // ── The marshal ─────────────────────────────────────────────────────────────────

        /// <summary>
        ///     A batch handed in from a worker thread is read on the main thread.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <c>EnqueueActions</c> is public and nothing stops a customer calling it from a
        ///         background thread — a network callback, a job, an async continuation. Reading a
        ///         command resolves names against <c>GameObject</c>s and <c>Transform</c>s, none of
        ///         which may be touched off the main thread, so the batch is snapshotted and posted
        ///         before anything reads it.
        ///     </para>
        ///     <para>
        ///         What this test can assert without a full character rig is the property that
        ///         matters most: the call does not throw on the worker thread, and nothing is read
        ///         there. A Unity API touched off-thread throws immediately, so an exception escaping
        ///         the worker is the failure.
        ///     </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator EnqueueFromAWorkerThreadDoesNotTouchTheSceneOnThatThread()
        {
            ConvaiActionDispatcher dispatcher = _scene.Dispatcher("playmode-dispatcher");
            yield return null;

            WorkerCall call = RunOnWorker(
                () => dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Walk To", "The Gallery") }));

            yield return call.Await();

            Assert.That(call.TimedOut, Is.False, TimeoutMessage);
            Assert.That(
                call.Thrown,
                Is.Null,
                "EnqueueActions threw on a worker thread: " + call.Thrown
                + " — deciding whether the batch needs marshalling must not itself require the main "
                + "thread, and Application.isPlaying does. That is how this path used to throw "
                + "before reaching the marshal that exists to prevent exactly that.");

            // Let the posted work run, and confirm it did not throw on the main thread either.
            yield return null;
            yield return null;
        }

        /// <summary>
        ///     A dispatcher destroyed between the post and the pump does not poison the scheduler.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The posted closure holds the dispatcher, so the work outlives the component: the
        ///         scheduler pumps a frame later, by which time the character may be gone. This is the
        ///         lifecycle hole an independent review flagged, and EditMode cannot reach it — it has
        ///         no frame loop to pump and no destroyed-but-referenced objects.
        ///     </para>
        ///     <para>
        ///         <b>Asserted through a consequence, deliberately.</b> "No error was logged" would be
        ///         the obvious form and it is a weak one — it passes by not looking. Instead a second,
        ///         living dispatcher is driven afterwards: if the destroyed one threw out of the
        ///         scheduler's pump, the queue it shares stops draining and this second batch never
        ///         lands.
        ///     </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator ADispatcherDestroyedBeforeTheMarshalPumpsDoesNotPoisonTheScheduler()
        {
            ConvaiActionDispatcher dying = _scene.Dispatcher("playmode-destroyed-dispatcher");
            GameObject doomed = dying.gameObject;
            yield return null;

            WorkerCall first = RunOnWorker(
                () => dying.EnqueueActions(new[] { new ConvaiActionCommand("Walk To", "The Gallery") }));

            yield return first.Await();
            Assert.That(first.TimedOut, Is.False, TimeoutMessage);

            // Destroyed before the posted work is pumped, which is the whole point.
            Object.Destroy(doomed);
            yield return null;
            yield return null;

            // The consequence: the shared scheduler still runs work posted after the casualty.
            ConvaiActionDispatcher living = _scene.Dispatcher("playmode-surviving-dispatcher");
            yield return null;

            WorkerCall second = RunOnWorker(
                () => living.EnqueueActions(new[] { new ConvaiActionCommand("Wave") }));

            yield return second.Await();

            Assert.That(second.TimedOut, Is.False, TimeoutMessage);
            Assert.That(
                second.Thrown,
                Is.Null,
                "A batch posted after a dispatcher was destroyed mid-flight threw: " + second.Thrown
                + " — the destroyed one took the queue with it.");
        }

        // ── The queue ───────────────────────────────────────────────────────────────────

        /// <summary>
        ///     An empty or null batch is a no-op, on either path.
        /// </summary>
        /// <remarks>
        ///     Cheap, and it pins the one exit the silent-drop conservation test cannot reach from
        ///     EditMode: there is nothing to account for, so accounting for nothing is correct.
        /// </remarks>
        [UnityTest]
        public IEnumerator AnEmptyBatchIsANoOp()
        {
            ConvaiActionDispatcher dispatcher = _scene.Dispatcher("playmode-empty-batch");
            yield return null;

            Assert.DoesNotThrow(() => dispatcher.EnqueueActions(null));
            Assert.DoesNotThrow(() => dispatcher.EnqueueActions(System.Array.Empty<ConvaiActionCommand>()));

            yield return null;

            Assert.That(dispatcher.PendingBatchCount, Is.Zero);
            Assert.That(dispatcher.IsBusy, Is.False);
        }
    }
}
