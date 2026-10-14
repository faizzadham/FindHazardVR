using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Runtime.Actions;
using Convai.Runtime.Logging;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Coverage for the shared action behavior framework: how each
    ///     <see cref="ConvaiActionExecutionResult" /> factory maps onto
    ///     <see cref="ConvaiActionFailureReason" /> and passes through
    ///     <see cref="ConvaiActionStepReport" />; the <see cref="ConvaiTargetedActionExecutor" />
    ///     template flow — target precondition, peer resolution and caching, the once-only
    ///     missing-peer report, and invocation parameter overrides; and the decline path
    ///     <see cref="ConvaiCharacterActionExecutor{TPeer}" /> gives every behavior built on it.
    ///     Driven through scene-free stub subclasses, so no module needs to be installed.
    /// </summary>
    [TestFixture]
    public class ConvaiTargetedActionExecutorTests
    {
        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null &&
                    gameObject.name.StartsWith("TargetedExecutorTests_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        // ── ConvaiActionFailureReason mapping ─────────────────────────────────────────────

        [Test]
        public void Succeeded_HasNoneFailureReason() =>
            Assert.That(ConvaiActionExecutionResult.Succeeded().FailureReason, Is.EqualTo(ConvaiActionFailureReason.None));

        [Test]
        public void Unhandled_HasNoneFailureReason() =>
            Assert.That(ConvaiActionExecutionResult.Unhandled("no peer").FailureReason, Is.EqualTo(ConvaiActionFailureReason.None));

        [Test]
        public void Failed_NoMessage_MapsToNone() =>
            Assert.That(ConvaiActionExecutionResult.Failed().FailureReason, Is.EqualTo(ConvaiActionFailureReason.None));

        [Test]
        public void Failed_MessageOnly_MapsToCustom() =>
            Assert.That(ConvaiActionExecutionResult.Failed("something broke").FailureReason,
                Is.EqualTo(ConvaiActionFailureReason.Custom));

        [Test]
        public void Failed_MessageAndException_StillMapsToCustom() =>
            Assert.That(ConvaiActionExecutionResult.Failed("boom", new InvalidOperationException()).FailureReason,
                Is.EqualTo(ConvaiActionFailureReason.Custom));

        [Test]
        public void Failed_WithExplicitReason_UsesProvidedReason() =>
            Assert.That(
                ConvaiActionExecutionResult.Failed("no path", ConvaiActionFailureReason.PathBlocked).FailureReason,
                Is.EqualTo(ConvaiActionFailureReason.PathBlocked));

        [Test]
        public void Canceled_MapsToInterrupted() =>
            Assert.That(ConvaiActionExecutionResult.Canceled().FailureReason, Is.EqualTo(ConvaiActionFailureReason.Interrupted));

        [Test]
        public void TimedOut_MapsToTimeout() =>
            Assert.That(ConvaiActionExecutionResult.TimedOut().FailureReason, Is.EqualTo(ConvaiActionFailureReason.Timeout));

        [Test]
        public void StepReport_FailureReason_PassesThroughResult()
        {
            ConvaiActionExecutionResult result =
                ConvaiActionExecutionResult.Failed("no path", ConvaiActionFailureReason.PathBlocked);
            var report = new ConvaiActionStepReport(null, result, false, "no path.");

            Assert.That(report.FailureReason, Is.EqualTo(ConvaiActionFailureReason.PathBlocked));
        }

        // ── ConvaiTargetedActionExecutor template flow ────────────────────────────────────

        [Test]
        public void ExecuteAsync_RequiresTargetTrueWithoutResolvedTarget_ReturnsUnhandledWithoutRunningCore()
        {
            StubTargetedExecutor executor = CreateStub();
            ConvaiActionInvocation invocation = CreateInvocation("Test", resolvedTarget: null);

            Task<ConvaiActionExecutionResult> task = executor.ExecuteAsync(invocation, default);

            Assert.That(task.IsCompletedSuccessfully, Is.True);
            Assert.That(task.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
            Assert.That(executor.CoreCallCount, Is.EqualTo(0));
        }

        [Test]
        public async Task ExecuteAsync_RequiresTargetTrueWithResolvedTarget_RunsCore()
        {
            StubTargetedExecutor executor = CreateStub();
            GameObject target = new("TargetedExecutorTests_Target");
            ConvaiActionInvocation invocation = CreateInvocation("Test", CreateResolvedTarget(target));

            ConvaiActionExecutionResult result = await executor.ExecuteAsync(invocation, default);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(executor.CoreCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ExecuteAsync_RequiresTargetFalse_AllowsNullInvocation()
        {
            StubTargetedExecutor executor = CreateStub();
            executor.RequireTargetOverride = false;

            ConvaiActionExecutionResult result = await executor.ExecuteAsync(null, default);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(executor.CoreCallCount, Is.EqualTo(1));
        }

        [Test]
        public void TryResolvePeer_UsesExplicitlyAssignedField_WithoutHierarchyLookup()
        {
            GameObject root = new("TargetedExecutorTests_Explicit");
            StubTargetedExecutor executor = root.AddComponent<StubTargetedExecutor>();
            StubPeer explicitPeer = root.AddComponent<StubPeer>();
            executor.Peer = explicitPeer;

            bool resolved = executor.ResolvePeer(out StubPeer peer);

            Assert.That(resolved, Is.True);
            Assert.That(peer, Is.SameAs(explicitPeer));
        }

        [Test]
        public void TryResolvePeer_FallsBackToParentHierarchy_WithoutWritingTheAuthoredField()
        {
            GameObject parent = new("TargetedExecutorTests_Parent");
            StubPeer parentPeer = parent.AddComponent<StubPeer>();
            GameObject child = new("TargetedExecutorTests_Child");
            child.transform.SetParent(parent.transform);
            StubTargetedExecutor executor = child.AddComponent<StubTargetedExecutor>();

            bool resolved = executor.ResolvePeer(out StubPeer peer);

            Assert.That(resolved, Is.True);
            Assert.That(peer, Is.SameAs(parentPeer));
            Assert.That(executor.Peer, Is.Null, "The authored field is user intent and must stay empty.");
            Assert.That(executor.ResolvePeer(out StubPeer again), Is.True);
            Assert.That(again, Is.SameAs(parentPeer), "The runtime cache should serve the second resolve.");
        }

        [Test]
        public void TryResolvePeer_FallsBackToChildren_WhenNoneInParents()
        {
            GameObject root = new("TargetedExecutorTests_ChildFallbackRoot");
            StubTargetedExecutor executor = root.AddComponent<StubTargetedExecutor>();
            GameObject childGo = new("TargetedExecutorTests_ChildFallbackChild");
            childGo.transform.SetParent(root.transform);
            StubPeer childPeer = childGo.AddComponent<StubPeer>();

            bool resolved = executor.ResolvePeer(out StubPeer peer);

            Assert.That(resolved, Is.True);
            Assert.That(peer, Is.SameAs(childPeer));
        }

        [Test]
        public void TryResolvePeer_ReturnsFalse_WhenPeerNotFoundAnywhere()
        {
            StubTargetedExecutor executor = CreateStub();

            bool resolved = executor.ResolvePeer(out StubPeer peer);

            Assert.That(resolved, Is.False);
            Assert.That(peer, Is.Null);
        }

        [Test]
        public void UnhandledMissingPeer_LogsOnce_AcrossMultipleCalls()
        {
            var sink = new TestLogSink();
            ConvaiLogger.RegisterSink(sink);
            try
            {
                StubTargetedExecutor executor = CreateStub();

                ConvaiActionExecutionResult first = executor.MissingPeerResult();
                ConvaiActionExecutionResult second = executor.MissingPeerResult();
                ConvaiActionExecutionResult third = executor.MissingPeerResult();

                Assert.That(first.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
                Assert.That(second.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
                Assert.That(third.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
                Assert.That(sink.CountByLevel(LogLevel.Warning), Is.EqualTo(1),
                    "Missing-peer diagnostics must log once per component instance, not per call.");
                Assert.That(sink.CountByCategory(LogCategory.Character), Is.EqualTo(1));
                Assert.That(sink.Entries[0].Message, Does.Contain("No StubPeer found"));
            }
            finally
            {
                ConvaiLogger.UnregisterSink(sink);
            }
        }

        [Test]
        public void GetOverride_Float_PrefersInvocationParameter_OverInspectorDefault()
        {
            ConvaiActionInvocation invocation = CreateInvocationWithNumberParameter("duration", 5f);

            Assert.That(StubTargetedExecutor.FloatOverride(invocation, "duration", 2f), Is.EqualTo(5f));
            Assert.That(StubTargetedExecutor.FloatOverride(invocation, "missing", 2f), Is.EqualTo(2f));
            Assert.That(StubTargetedExecutor.FloatOverride(null, "duration", 2f), Is.EqualTo(2f));
        }

        [Test]
        public void GetOverride_Bool_PrefersInvocationParameter_OverInspectorDefault()
        {
            var command = new ConvaiActionCommand("Test")
            {
                Parameters =
                {
                    ["allowBodyTurn"] = new ConvaiActionParameterValue { Type = ConvaiActionParameterType.Bool, BoolValue = false }
                }
            };
            var invocation = new ConvaiActionInvocation(command, null, null, null, 0, 0);

            Assert.That(StubTargetedExecutor.BoolOverride(invocation, "allowBodyTurn", true), Is.False);
            Assert.That(StubTargetedExecutor.BoolOverride(invocation, "missing", true), Is.True);
            Assert.That(StubTargetedExecutor.BoolOverride(null, "allowBodyTurn", true), Is.True);
        }

        [Test]
        public void GetOverride_String_PrefersInvocationParameter_OverInspectorDefault()
        {
            var command = new ConvaiActionCommand("Test")
            {
                Parameters =
                {
                    ["mode"] = new ConvaiActionParameterValue { Type = ConvaiActionParameterType.String, StringValue = "fast" }
                }
            };
            var invocation = new ConvaiActionInvocation(command, null, null, null, 0, 0);

            Assert.That(StubTargetedExecutor.StringOverride(invocation, "mode", "slow"), Is.EqualTo("fast"));
            Assert.That(StubTargetedExecutor.StringOverride(invocation, "missing", "slow"), Is.EqualTo("slow"));
            Assert.That(StubTargetedExecutor.StringOverride(null, "mode", "slow"), Is.EqualTo("slow"));
        }

        [Test]
        public async Task ExecuteAsync_CancellationDuringCore_PropagatesOperationCanceledException()
        {
            StubTargetedExecutor executor = CreateStub();
            executor.RequireTargetOverride = false;
            executor.CoreImpl = async (_, ct) =>
            {
                await Task.Delay(2000, ct);
                return ConvaiActionExecutionResult.Succeeded();
            };

            using var cts = new CancellationTokenSource();
            Task<ConvaiActionExecutionResult> task = executor.ExecuteAsync(null, cts.Token);
            cts.Cancel();

            OperationCanceledException caught = null;
            try
            {
                await task;
            }
            catch (OperationCanceledException ex)
            {
                caught = ex;
            }

            Assert.That(caught, Is.Not.Null, "Cancellation must propagate out of ExecuteAsync as OperationCanceledException.");
        }

        // ── Character-component base: the degrade-gracefully path every module behavior inherits ──

        [Test]
        public void CharacterActionExecutor_WithoutItsComponent_DeclinesWithoutRunning()
        {
            GameObject root = new("TargetedExecutorTests_NoCharacterComponent");
            StubCharacterExecutor executor = root.AddComponent<StubCharacterExecutor>();

            Task<ConvaiActionExecutionResult> task = executor.ExecuteAsync(null, default);

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled),
                "A missing character component is a soft decline, so another behavior still gets its turn.");
            Assert.That(executor.CoreCallCount, Is.Zero, "The core must not run without its component.");
        }

        [Test]
        public void CharacterActionExecutor_FindsItsComponentOnTheCharacter_AndRunsTheCore()
        {
            GameObject character = new("TargetedExecutorTests_Character");
            character.AddComponent<Animator>();
            GameObject behaviorHost = new("TargetedExecutorTests_BehaviorHost");
            behaviorHost.transform.SetParent(character.transform);
            StubCharacterExecutor executor = behaviorHost.AddComponent<StubCharacterExecutor>();

            Task<ConvaiActionExecutionResult> task = executor.ExecuteAsync(null, default);

            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.Result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(executor.CoreCallCount, Is.EqualTo(1));
            Assert.That(executor.LastResolvedComponent, Is.Not.Null,
                "The component is resolved from the parent hierarchy, not only from this GameObject.");
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────

        private static StubTargetedExecutor CreateStub()
        {
            GameObject root = new("TargetedExecutorTests_Stub");
            return root.AddComponent<StubTargetedExecutor>();
        }

        private static ConvaiResolvedActionTarget CreateResolvedTarget(GameObject gameObject) =>
            ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition
            {
                Name = gameObject.name,
                GameObjectReference = gameObject
            });

        private static ConvaiActionInvocation CreateInvocation(string actionName, ConvaiResolvedActionTarget resolvedTarget) =>
            new(new ConvaiActionCommand(actionName), null, resolvedTarget, null, 0, 0);

        private static ConvaiActionInvocation CreateInvocationWithNumberParameter(string parameterName, float value)
        {
            var command = new ConvaiActionCommand("Test")
            {
                Parameters =
                {
                    [parameterName] = new ConvaiActionParameterValue
                    {
                        Type = ConvaiActionParameterType.Number,
                        NumberValue = value
                    }
                }
            };
            return new ConvaiActionInvocation(command, null, null, null, 0, 0);
        }

        // ── Inner test types ──────────────────────────────────────────────────────────────

        private sealed class StubPeer : MonoBehaviour
        {
        }

        /// <summary>
        ///     Stands in for any module behavior built on
        ///     <see cref="ConvaiCharacterActionExecutor{TPeer}" />. Uses <see cref="Animator" /> as its
        ///     required component purely because it is an engine type every test can add — the base's
        ///     behavior is identical whatever the component is.
        /// </summary>
        private sealed class StubCharacterExecutor : ConvaiCharacterActionExecutor<Animator>
        {
            public int CoreCallCount { get; private set; }
            public Animator LastResolvedComponent { get; private set; }

            protected override bool RequiresTarget => false;

            protected override Task<ConvaiActionExecutionResult> ExecuteCoreAsync(
                Animator characterComponent,
                ConvaiActionInvocation invocation,
                CancellationToken cancellationToken)
            {
                CoreCallCount++;
                LastResolvedComponent = characterComponent;
                return Task.FromResult(ConvaiActionExecutionResult.Succeeded());
            }
        }

        private sealed class StubTargetedExecutor : ConvaiTargetedActionExecutor
        {
            [SerializeField] private StubPeer _peer;


            public bool RequireTargetOverride { get; set; } = true;
            public int CoreCallCount { get; private set; }

            public Func<ConvaiActionInvocation, CancellationToken, Task<ConvaiActionExecutionResult>> CoreImpl { get; set; }

            public StubPeer Peer
            {
                get => _peer;
                set => _peer = value;
            }

            protected override bool RequiresTarget => RequireTargetOverride;

            protected override async Task<ConvaiActionExecutionResult> ExecuteCoreAsync(
                ConvaiActionInvocation invocation,
                CancellationToken cancellationToken)
            {
                CoreCallCount++;
                return CoreImpl != null
                    ? await CoreImpl(invocation, cancellationToken)
                    : ConvaiActionExecutionResult.Succeeded();
            }

            public bool ResolvePeer(out StubPeer peer) => TryResolvePeer(ref _peer, out peer);

            public ConvaiActionExecutionResult MissingPeerResult() => UnhandledMissingPeer<StubPeer>();

            public static float FloatOverride(ConvaiActionInvocation invocation, string parameterName, float defaultValue) =>
                GetOverride(invocation, parameterName, defaultValue);

            public static bool BoolOverride(ConvaiActionInvocation invocation, string parameterName, bool defaultValue) =>
                GetOverride(invocation, parameterName, defaultValue);

            public static string StringOverride(ConvaiActionInvocation invocation, string parameterName, string defaultValue) =>
                GetOverride(invocation, parameterName, defaultValue);
        }
    }
}
