using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Modules;
using Convai.Runtime.Core.Registry;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Tests.EditMode.Core
{
    public sealed class ConvaiRuntimeLifecycleTests
    {
        [Test]
        public async Task StartAsync_WhenModuleStartFails_PublishesStoppedTransition()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            ConvaiRuntime runtime = new ConvaiRuntimeBuilder()
                .UseEventHub(eventHub)
                .UseAgentRegistry(new AgentRegistry())
                .UseRoomRuntime(() => new RoomRuntimeBuilder()
                    .WithConnectionAdapter(new TestConnectionRuntimeAdapter())
                    .WithAudioAdapter(new TestAudioRuntimeAdapter())
                    .WithAgentRegistry(new AgentRegistry())
                    .Build())
                .AddModule(new FailingStartModule())
                .Build();

            InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runtime.StartAsync());

            Assert.That(exception.Message, Is.EqualTo("module start failed"));
            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Stopped));
            Assert.That(transitions, Has.Count.EqualTo(2));
            Assert.That(transitions[0].PreviousState, Is.EqualTo(RuntimeState.Created));
            Assert.That(transitions[0].NewState, Is.EqualTo(RuntimeState.Starting));
            Assert.That(transitions[1].PreviousState, Is.EqualTo(RuntimeState.Starting));
            Assert.That(transitions[1].NewState, Is.EqualTo(RuntimeState.Stopped));

            await runtime.DisposeAsync();
        }

        [Test]
        public async Task DisposeAsync_BeforeStartup_PublishesDisposedTransitionOnce()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            ConvaiRuntime runtime = CreateRuntime(eventHub);

            await runtime.DisposeAsync();
            await runtime.DisposeAsync();

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(transitions, Has.Count.EqualTo(1));
            Assert.That(transitions[0].PreviousState, Is.EqualTo(RuntimeState.Created));
            Assert.That(transitions[0].NewState, Is.EqualTo(RuntimeState.Disposed));
        }

        [Test]
        public async Task StopAsync_BeforeStartup_PublishesContiguousStoppedThenDisposedTransitions()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            ConvaiRuntime runtime = CreateRuntime(eventHub);

            await runtime.StopAsync();
            await runtime.StopAsync();
            await runtime.DisposeAsync();

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(transitions, Has.Count.EqualTo(2));
            Assert.That(transitions[0].PreviousState, Is.EqualTo(RuntimeState.Created));
            Assert.That(transitions[0].NewState, Is.EqualTo(RuntimeState.Stopped));
            Assert.That(transitions[1].PreviousState, Is.EqualTo(RuntimeState.Stopped));
            Assert.That(transitions[1].NewState, Is.EqualTo(RuntimeState.Disposed));
        }

        [Test]
        public async Task DisposeAsync_AfterStartup_PublishesStoppedThenDisposedTransitions()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            ConvaiRuntime runtime = CreateRuntime(eventHub);
            await runtime.StartAsync();
            transitions.Clear();

            await runtime.DisposeAsync();

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(transitions, Has.Count.EqualTo(3));
            Assert.That(transitions[0].PreviousState, Is.EqualTo(RuntimeState.Running));
            Assert.That(transitions[0].NewState, Is.EqualTo(RuntimeState.Stopping));
            Assert.That(transitions[1].PreviousState, Is.EqualTo(RuntimeState.Stopping));
            Assert.That(transitions[1].NewState, Is.EqualTo(RuntimeState.Stopped));
            Assert.That(transitions[2].PreviousState, Is.EqualTo(RuntimeState.Stopped));
            Assert.That(transitions[2].NewState, Is.EqualTo(RuntimeState.Disposed));
        }

        [Test]
        public async Task DisposeAsync_ConcurrentCalls_CoalesceStopShutdownAndDisposedPublication()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            var module = new BlockingLifecycleModule { BlockStop = true };
            ConvaiRuntime runtime = CreateRuntime(eventHub, room, module);
            await runtime.StartAsync();
            transitions.Clear();

            Task firstDispose = runtime.DisposeAsync().AsTask();
            await module.StopEntered;
            Task secondDispose = runtime.DisposeAsync().AsTask();

            Assert.That(secondDispose, Is.SameAs(firstDispose));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(module.MaximumConcurrentStopCalls, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.Zero);

            module.CompleteStop();
            await Task.WhenAll(firstDispose, secondDispose);

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(module.MaximumConcurrentStopCalls, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
            AssertDisposedIsTerminal(transitions);
        }

        [Test]
        public async Task PauseAsync_WhenDisposeBegins_WaitsForMutationAndCanonicalStop()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            var module = new BlockingLifecycleModule
            {
                BlockPause = true,
                BlockStop = true,
            };
            ConvaiRuntime runtime = CreateRuntime(eventHub, room, module);
            await runtime.StartAsync();
            transitions.Clear();

            Task pause = runtime.PauseAsync(RuntimePauseReason.UserRequested).AsTask();
            await module.PauseEntered;
            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Stopping));
            Assert.That(module.StopCallCount, Is.Zero);
            Assert.That(room.ShutdownCallCount, Is.Zero);

            module.CompletePause();
            await module.StopEntered;
            await pause;

            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Paused),
                "The module mutates after its pause await; disposal must wait and then stop it.");
            Assert.That(dispose.IsCompleted, Is.False);

            module.CompleteStop();
            await dispose;

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Stopped));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
            Assert.That(transitions.Count(item => item.NewState == RuntimeState.Paused), Is.Zero);
            AssertDisposedIsTerminal(transitions);
        }

        [Test]
        public async Task ResumeAsync_WhenDisposeBegins_WaitsForMutationAndCanonicalStop()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            var module = new BlockingLifecycleModule();
            ConvaiRuntime runtime = CreateRuntime(eventHub, room, module);
            await runtime.StartAsync();
            await runtime.PauseAsync(RuntimePauseReason.UserRequested);
            module.BlockResume = true;
            module.BlockStop = true;
            transitions.Clear();

            Task resume = runtime.ResumeAsync().AsTask();
            await module.ResumeEntered;
            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Stopping));
            Assert.That(module.StopCallCount, Is.Zero);
            Assert.That(room.ShutdownCallCount, Is.Zero);

            module.CompleteResume();
            await module.StopEntered;
            await resume;

            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Active),
                "The module mutates after its resume await; disposal must wait and then stop it.");
            Assert.That(dispose.IsCompleted, Is.False);

            module.CompleteStop();
            await dispose;

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Stopped));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
            Assert.That(transitions.Count(item => item.NewState == RuntimeState.Running), Is.Zero);
            AssertDisposedIsTerminal(transitions);
        }

        [Test]
        public async Task StopAsync_WhenCallerCancels_DisposeStillJoinsNonCancelableCleanup()
        {
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            var module = new BlockingLifecycleModule
            {
                BlockStop = true,
                ThrowIfStopTokenCanBeCanceled = true,
            };
            ConvaiRuntime runtime = CreateRuntime(null, room, module);
            await runtime.StartAsync();
            using var cancellation = new CancellationTokenSource();

            Task callerStop = runtime.StopAsync(cancellation.Token).AsTask();
            await module.StopEntered;
            cancellation.Cancel();

            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(callerStop,
                "a caller's StopAsync surfaces cancellation once its own token is cancelled");
            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(module.StopTokenCanBeCanceled, Is.False,
                "The canonical teardown must never inherit a public caller token.");
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(room.ShutdownCallCount, Is.Zero);

            module.CompleteStop();
            await dispose;

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Stopped));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task StopAsync_WhenDisposeOverlaps_CoalescesStopAndPreservesDisposedAsTerminalState()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            var module = new BlockingLifecycleModule { BlockStop = true };
            ConvaiRuntime runtime = CreateRuntime(eventHub, room, module);
            await runtime.StartAsync();
            transitions.Clear();

            Task firstStop = runtime.StopAsync().AsTask();
            await module.StopEntered;
            Task secondStop = runtime.StopAsync().AsTask();
            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(secondStop, Is.SameAs(firstStop));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(module.MaximumConcurrentStopCalls, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.Zero);

            module.CompleteStop();
            await Task.WhenAll(firstStop, secondStop, dispose);

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(module.MaximumConcurrentStopCalls, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
            AssertDisposedIsTerminal(transitions);
        }

        [Test]
        public async Task StartAsync_WhenDisposeBeginsBeforeModuleFailure_JoinsCanonicalStop()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            var module = new BlockingStartModule();
            ConvaiRuntime runtime = new ConvaiRuntimeBuilder()
                .UseEventHub(eventHub)
                .UseAgentRegistry(new AgentRegistry())
                .UseRoomRuntime(() => new RoomRuntimeBuilder()
                    .WithConnectionAdapter(new TestConnectionRuntimeAdapter())
                    .WithAudioAdapter(new TestAudioRuntimeAdapter())
                    .WithAgentRegistry(new AgentRegistry())
                    .Build())
                .AddModule(module)
                .Build();

            Task start = AwaitStartAsync(runtime);
            await module.StartEntered;
            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(module.StopCallCount, Is.Zero);

            module.FailStart();

            InvalidOperationException exception =
                await AsyncTestDeadline.ThrowsWithinAsync<InvalidOperationException>(start,
                    "a start that fails after disposal faults the caller's task");
            await dispose;

            Assert.That(exception.Message, Is.EqualTo("module start failed after disposal"));
            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(transitions, Has.Count.EqualTo(4));
            Assert.That(transitions[0].PreviousState, Is.EqualTo(RuntimeState.Created));
            Assert.That(transitions[0].NewState, Is.EqualTo(RuntimeState.Starting));
            Assert.That(transitions[1].PreviousState, Is.EqualTo(RuntimeState.Starting));
            Assert.That(transitions[1].NewState, Is.EqualTo(RuntimeState.Stopping));
            Assert.That(transitions[2].PreviousState, Is.EqualTo(RuntimeState.Stopping));
            Assert.That(transitions[2].NewState, Is.EqualTo(RuntimeState.Stopped));
            Assert.That(transitions[3].PreviousState, Is.EqualTo(RuntimeState.Stopped));
            Assert.That(transitions[3].NewState, Is.EqualTo(RuntimeState.Disposed));
        }

        [Test]
        public async Task StartAsync_WhenDisposeBegins_WaitsForMutationThenStopsOnce()
        {
            var firstModule = new BlockingStartModule { BlockStop = true };
            var secondModule = new RecordingLifecycleModule("second-module");
            ConvaiRuntime runtime = new ConvaiRuntimeBuilder()
                .UseEventHub(new EventHub(new ImmediateScheduler(), new TestLogger()))
                .UseAgentRegistry(new AgentRegistry())
                .UseRoomRuntime(() => new RoomRuntimeBuilder()
                    .WithConnectionAdapter(new TestConnectionRuntimeAdapter())
                    .WithAudioAdapter(new TestAudioRuntimeAdapter())
                    .WithAgentRegistry(new AgentRegistry())
                    .Build())
                .AddModule(firstModule)
                .AddModule(secondModule)
                .Build();

            Task start = AwaitStartAsync(runtime);
            await firstModule.StartEntered;
            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(firstModule.StopCallCount, Is.Zero);

            firstModule.CompleteStart();
            await firstModule.StopEntered;
            await start;

            Assert.That(firstModule.IsStarted, Is.True,
                "The module mutates after its start await; disposal must wait and then stop it.");
            Assert.That(firstModule.StartCallCount, Is.EqualTo(1));
            Assert.That(firstModule.StopCallCount, Is.EqualTo(1));
            Assert.That(firstModule.MaximumConcurrentStopCalls, Is.EqualTo(1));
            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(secondModule.StartCallCount, Is.Zero,
                "Startup must not advance to another module after disposal owns the lifecycle.");
            Assert.That(secondModule.StopCallCount, Is.EqualTo(1));

            firstModule.CompleteStop();
            await dispose;

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(firstModule.StopCallCount, Is.EqualTo(1));
            Assert.That(firstModule.IsStarted, Is.False);
        }

        [Test]
        public async Task AddAndStartModuleAsync_WhenDisposeBegins_WaitsForMutationThenStopsOnce()
        {
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            ConvaiRuntime runtime = CreateRuntime(null, room, null);
            await runtime.StartAsync();
            var module = new BlockingStartModule { BlockStop = true };

            Task addAndStart = runtime.AddAndStartModuleAsync(module).AsTask();
            await module.StartEntered;
            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(module.StopCallCount, Is.Zero);
            Assert.That(room.ShutdownCallCount, Is.Zero);

            module.CompleteStart();
            await module.StopEntered;
            await addAndStart;

            Assert.That(module.IsStarted, Is.True,
                "The dynamic module mutates after its start await; disposal must wait and then stop it.");
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(dispose.IsCompleted, Is.False);

            module.CompleteStop();
            await dispose;

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.IsStarted, Is.False);
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task StopAndRemoveModuleAsync_WhenCallerCancels_DisposeWaitsForPhysicalRemoval()
        {
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            var module = new BlockingLifecycleModule { BlockStop = true };
            ConvaiRuntime runtime = CreateRuntime(null, room, module);
            await runtime.StartAsync();
            using var cancellation = new CancellationTokenSource();

            Task removalView = runtime.StopAndRemoveModuleAsync(module, cancellation.Token).AsTask();
            await module.StopEntered;
            cancellation.Cancel();
            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(removalView,
                "a caller's StopAndRemoveModuleAsync surfaces cancellation once its own token is cancelled");

            Task dispose = runtime.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Active));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(room.ShutdownCallCount, Is.Zero);

            module.CompleteStop();
            await dispose;

            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Stopped));
            Assert.That(module.StopCallCount, Is.EqualTo(1),
                "Canonical shutdown must not stop a module already removed by physical cleanup.");
            Assert.That(runtime.Modules.Contains(module), Is.False);
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task StatePublication_WhenSubscriberDisposesDuringRunning_PreservesFifoForAllSubscribers()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var firstSubscriberStates = new List<RuntimeState>();
            var secondSubscriberStates = new List<RuntimeState>();
            ConvaiRuntime runtime = null;
            Task dispose = null;
            eventHub.Subscribe<RuntimeStateChanged>(
                transition =>
                {
                    firstSubscriberStates.Add(transition.NewState);
                    if (transition.NewState == RuntimeState.Running)
                        dispose = runtime.DisposeAsync().AsTask();
                },
                EventDeliveryPolicy.Immediate);
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => secondSubscriberStates.Add(transition.NewState),
                EventDeliveryPolicy.Immediate);
            runtime = CreateRuntime(eventHub);

            await runtime.StartAsync();
            Assert.That(dispose, Is.Not.Null);
            await dispose;

            RuntimeState[] expectedStates =
            {
                RuntimeState.Starting,
                RuntimeState.Running,
                RuntimeState.Stopping,
                RuntimeState.Stopped,
                RuntimeState.Disposed,
            };
            Assert.That(firstSubscriberStates, Is.EqualTo(expectedStates));
            Assert.That(secondSubscriberStates, Is.EqualTo(expectedStates),
                "The later subscriber must observe Running before the re-entrant stop, " +
                "and Stopped before Disposed.");
        }

        [Test]
        public async Task StatePublication_WhenPublisherThrows_DrainsQueuedTransitionsAndRecovers()
        {
            var eventHub = new ThrowOnceEventHub(RuntimeState.Running);
            var observedStates = new List<RuntimeState>();
            ConvaiRuntime runtime = null;
            Task dispose = null;
            eventHub.Subscribe<RuntimeStateChanged>(
                transition =>
                {
                    if (transition.NewState == RuntimeState.Running)
                        dispose = runtime.DisposeAsync().AsTask();
                },
                EventDeliveryPolicy.Immediate);
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => observedStates.Add(transition.NewState),
                EventDeliveryPolicy.Immediate);
            runtime = CreateRuntime(eventHub);

            InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runtime.StartAsync());
            Assert.That(exception.Message, Is.EqualTo("simulated event publication failure"));
            Assert.That(dispose, Is.Not.Null);
            await dispose;

            Assert.That(eventHub.ThrowCount, Is.EqualTo(1));
            Assert.That(observedStates, Is.EqualTo(new[]
            {
                RuntimeState.Starting,
                RuntimeState.Running,
                RuntimeState.Stopping,
                RuntimeState.Stopped,
                RuntimeState.Disposed,
            }), "A failed publisher must not strand the queued stop or later disposal transition.");
        }

        [Test]
        public async Task LifecycleOperations_AfterDispose_PreserveInvalidStateFailures()
        {
            ConvaiRuntime runtime = CreateRuntime();
            await runtime.DisposeAsync();

            Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.StartAsync());
            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runtime.PauseAsync(RuntimePauseReason.UserRequested));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.ResumeAsync());
        }

        [Test]
        public async Task DisposeAsync_WhenStoppingPublicationThrows_StillStopsModulesAndShutsDownRoom()
        {
            var eventHub = new ThrowOnceEventHub(RuntimeState.Stopping);
            var room = new CountingRoomRuntime(CreateTestRoomRuntime());
            var module = new BlockingLifecycleModule();
            ConvaiRuntime runtime = CreateRuntime(eventHub, room, module);
            await runtime.StartAsync();

            await runtime.DisposeAsync();

            Assert.That(eventHub.ThrowCount, Is.EqualTo(1));
            Assert.That(module.StopCallCount, Is.EqualTo(1));
            Assert.That(module.CurrentState, Is.EqualTo(TestModuleState.Stopped));
            Assert.That(room.ShutdownCallCount, Is.EqualTo(1));
            Assert.That(runtime.State, Is.EqualTo(RuntimeState.Disposed));
        }

        [Test]
        public async Task StartCompletion_CommitsStateAndQueuesPublicationAtomically()
        {
            var eventHub = new EventHub(new ImmediateScheduler(), new TestLogger());
            var transitions = new List<RuntimeStateChanged>();
            eventHub.Subscribe<RuntimeStateChanged>(
                transition => transitions.Add(transition),
                EventDeliveryPolicy.Immediate);
            var module = new BlockingStartModule();
            ConvaiRuntime runtime = CreateRuntime(eventHub, CreateTestRoomRuntime(), module);
            Task start = Task.Run(async () => await runtime.StartAsync());
            await module.StartEntered;

            FieldInfo publicationLockField = typeof(ConvaiRuntime).GetField(
                "_statePublicationLock",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(publicationLockField, Is.Not.Null);
            object publicationLock = publicationLockField.GetValue(runtime);

            Monitor.Enter(publicationLock);
            try
            {
                module.CompleteStart();
                Assert.That(
                    SpinWait.SpinUntil(() => module.StartMutationCompleted.IsCompleted, 1000),
                    Is.True,
                    "The module did not finish its startup mutation in time.");
                Assert.That(
                    SpinWait.SpinUntil(() => runtime.State != RuntimeState.Starting, 250),
                    Is.False,
                    "Running became externally visible before its transition was queued.");
            }
            finally
            {
                Monitor.Exit(publicationLock);
            }

            await start;
            await runtime.StopAsync();
            await runtime.DisposeAsync();

            RuntimeState previous = RuntimeState.Created;
            foreach (RuntimeStateChanged transition in transitions)
            {
                Assert.That(transition.PreviousState, Is.EqualTo(previous));
                previous = transition.NewState;
            }
        }

        private static async Task AwaitStartAsync(ConvaiRuntime runtime) => await runtime.StartAsync();

        private static void AssertDisposedIsTerminal(IReadOnlyList<RuntimeStateChanged> transitions)
        {
            Assert.That(transitions.Count(item => item.NewState == RuntimeState.Disposed), Is.EqualTo(1));
            Assert.That(transitions[^1].NewState, Is.EqualTo(RuntimeState.Disposed));
            Assert.That(transitions.Any(item => item.PreviousState == RuntimeState.Disposed), Is.False);
        }

        private static ConvaiRuntime CreateRuntime(IEventHub eventHub = null) =>
            CreateRuntime(eventHub, CreateTestRoomRuntime(), null);

        private static ConvaiRuntime CreateRuntime(
            IEventHub eventHub,
            IRoomRuntime room,
            IConvaiModule module)
        {
            var builder = new ConvaiRuntimeBuilder()
                .UseEventHub(eventHub ?? new EventHub(new ImmediateScheduler(), new TestLogger()))
                .UseAgentRegistry(new AgentRegistry())
                .UseRoomRuntime(() => room);
            if (module != null) builder.AddModule(module);
            return builder.Build();
        }

        private static IRoomRuntime CreateTestRoomRuntime() => new RoomRuntimeBuilder()
            .WithConnectionAdapter(new TestConnectionRuntimeAdapter())
            .WithAudioAdapter(new TestAudioRuntimeAdapter())
            .WithAgentRegistry(new AgentRegistry())
            .Build();

        private sealed class CountingRoomRuntime : IRoomRuntime
        {
            private readonly IRoomRuntime _inner;

            public CountingRoomRuntime(IRoomRuntime inner) =>
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));

            public int ShutdownCallCount { get; private set; }
            public bool IsActive => _inner.IsActive;
            public RoomSession Session => _inner.Session;
            public IRoomConnectionCoordinator Connection => _inner.Connection;
            public IRoomAudioCoordinator Audio => _inner.Audio;
            public IRoomOwnershipCoordinator Ownership => _inner.Ownership;
            public IRoomDiagnostics Diagnostics => _inner.Diagnostics;

            public IConvaiOperation<RoomSession> ConnectAsync(CancellationToken ct = default) =>
                _inner.ConnectAsync(ct);

            public IConvaiOperation<Unit> DisconnectAsync(CancellationToken ct = default) =>
                _inner.DisconnectAsync(ct);

            public void Initialize(RoomSession session) => _inner.Initialize(session);

            public void Shutdown()
            {
                ShutdownCallCount++;
                _inner.Shutdown();
            }
        }

        private enum TestModuleState
        {
            Inactive,
            Active,
            Paused,
            Stopped,
        }

        private sealed class BlockingLifecycleModule : IConvaiModule
        {
            private readonly TaskCompletionSource<bool> _pauseCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _pauseEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _resumeCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _resumeEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _stopCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _stopEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _activeStopCalls;

            public bool BlockPause { get; set; }
            public bool BlockResume { get; set; }
            public bool BlockStop { get; set; }
            public bool ThrowIfStopTokenCanBeCanceled { get; set; }
            public Task PauseEntered => _pauseEntered.Task;
            public Task ResumeEntered => _resumeEntered.Task;
            public Task StopEntered => _stopEntered.Task;
            public int StopCallCount { get; private set; }
            public int MaximumConcurrentStopCalls { get; private set; }
            public bool StopTokenCanBeCanceled { get; private set; }
            public TestModuleState CurrentState { get; private set; }
            public string ModuleId => "blocking-lifecycle";
            public string DisplayName => "blocking-lifecycle";
            public IReadOnlyList<string> RequiredModules => Array.Empty<string>();
            public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
            public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();
            public bool IsActive => true;

            public void CompletePause() => _pauseCompletion.TrySetResult(true);
            public void CompleteResume() => _resumeCompletion.TrySetResult(true);
            public void CompleteStop() => _stopCompletion.TrySetResult(true);

            public ValueTask RegisterAsync(IModuleContext context, CancellationToken ct = default) => default;
            public ValueTask StartAsync(IModuleContext context, CancellationToken ct = default)
            {
                CurrentState = TestModuleState.Active;
                return default;
            }

            public async ValueTask PauseAsync(
                RuntimePauseReason reason,
                CancellationToken ct = default)
            {
                _pauseEntered.TrySetResult(true);
                if (BlockPause) await _pauseCompletion.Task;
                CurrentState = TestModuleState.Paused;
            }

            public async ValueTask ResumeAsync(CancellationToken ct = default)
            {
                _resumeEntered.TrySetResult(true);
                if (BlockResume) await _resumeCompletion.Task;
                CurrentState = TestModuleState.Active;
            }

            public async ValueTask StopAsync(CancellationToken ct = default)
            {
                StopCallCount++;
                StopTokenCanBeCanceled = ct.CanBeCanceled;
                _stopEntered.TrySetResult(true);
                if (ThrowIfStopTokenCanBeCanceled && ct.CanBeCanceled)
                {
                    throw new InvalidOperationException(
                        "caller cancellation reached physical module teardown");
                }

                _activeStopCalls++;
                MaximumConcurrentStopCalls = Math.Max(MaximumConcurrentStopCalls, _activeStopCalls);
                try
                {
                    if (BlockStop) await _stopCompletion.Task;
                    CurrentState = TestModuleState.Stopped;
                }
                finally
                {
                    _activeStopCalls--;
                }
            }
        }

        private sealed class ThrowOnceEventHub : EventHub, IEventHub
        {
            private readonly RuntimeState _throwOnState;
            private bool _hasThrown;

            public ThrowOnceEventHub(RuntimeState throwOnState)
                : base(new ImmediateScheduler(), new TestLogger()) =>
                _throwOnState = throwOnState;

            public int ThrowCount { get; private set; }

            void IEventHub.Publish<TEvent>(TEvent @event)
            {
                base.Publish(@event);
                if (_hasThrown || @event is not RuntimeStateChanged stateChanged ||
                    stateChanged.NewState != _throwOnState)
                    return;

                _hasThrown = true;
                ThrowCount++;
                throw new InvalidOperationException("simulated event publication failure");
            }
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestLogger : ILogger
        {
            public void Log(in Convai.Domain.Logging.LogEntry entry)
            {
                if (entry.HasException) { Error(entry.Exception, entry.Message, entry.Context, entry.Category); return; }
                switch (entry.Level)
                {
                    case Convai.Domain.Logging.LogLevel.Debug:
                    case Convai.Domain.Logging.LogLevel.Trace: Debug(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Info: Info(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Warning: Warning(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Error: Error(entry.Message, entry.Context, entry.Category); break;
                }
            }
            public bool IsEnabled(LogLevel level, LogCategory category) => false;
            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }
            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Debug(string message, LogCategory category = LogCategory.SDK) { }
            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Info(string message, LogCategory category = LogCategory.SDK) { }
            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Warning(string message, LogCategory category = LogCategory.SDK) { }
            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Error(string message, LogCategory category = LogCategory.SDK) { }
            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
            public void Error(Exception exception, string message, LogCategory category = LogCategory.SDK) { }
            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            { }
        }

        private sealed class TestConnectionRuntimeAdapter : IRoomConnectionRuntimeAdapter
        {
            public event Action<SessionStateChanged> StateChanged;

            public SessionState CurrentState => SessionState.Disconnected;
            public bool IsConnected => false;
            public string CurrentRoomName => string.Empty;
            public string CurrentSessionId => string.Empty;
            public bool CanConnect => true;
            public bool HasStarted => true;

            public Task<bool> WaitForStartCompletionAsync(CancellationToken ct) => Task.FromResult(true);
            public bool PrepareConnection() => true;
            public Task<bool> WaitForConnectionResolutionAsync(CancellationToken ct) => Task.FromResult(true);
            public Task<RoomConnectionAttemptResult> ConnectAsync(CancellationToken ct) =>
                Task.FromResult(RoomConnectionAttemptResult.Success());
            public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

            public void NotifyStateChanged(SessionStateChanged stateChanged) => StateChanged?.Invoke(stateChanged);
        }

        private sealed class TestAudioRuntimeAdapter : IRoomAudioRuntimeAdapter
        {
            public bool IsMicMuted => false;
            public void SetMicMuted(bool muted) { }
            public Task StartListeningAsync(int microphoneIndex, CancellationToken ct) => Task.CompletedTask;
            public Task StopListeningAsync(CancellationToken ct) => Task.CompletedTask;
            public bool SetCharacterMuted(string characterId, bool muted) => true;
            public bool IsCharacterMuted(string characterId) => false;
        }

        private sealed class FailingStartModule : IConvaiModule
        {
            public string ModuleId => "failing-start";
            public string DisplayName => "failing-start";
            public IReadOnlyList<string> RequiredModules => Array.Empty<string>();
            public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
            public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();
            public bool IsActive => true;

            public ValueTask RegisterAsync(IModuleContext context, CancellationToken ct = default) => default;

            public ValueTask StartAsync(IModuleContext context, CancellationToken ct = default) =>
                throw new InvalidOperationException("module start failed");

            public ValueTask PauseAsync(RuntimePauseReason reason, CancellationToken ct = default) => default;
            public ValueTask ResumeAsync(CancellationToken ct = default) => default;
            public ValueTask StopAsync(CancellationToken ct = default) => default;
        }

        private sealed class BlockingStartModule : IConvaiModule
        {
            private readonly TaskCompletionSource<bool> _startCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _startEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _startMutationCompleted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _stopCompletion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _stopEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _activeStopCalls;

            public Task StartEntered => _startEntered.Task;
            public Task StartMutationCompleted => _startMutationCompleted.Task;
            public Task StopEntered => _stopEntered.Task;
            public bool BlockStop { get; set; }
            public int StartCallCount { get; private set; }
            public int StopCallCount { get; private set; }
            public int MaximumConcurrentStopCalls { get; private set; }
            public bool IsStarted { get; private set; }
            public string ModuleId => "blocking-start";
            public string DisplayName => "blocking-start";
            public IReadOnlyList<string> RequiredModules => Array.Empty<string>();
            public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
            public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();
            public bool IsActive => true;

            public void FailStart() =>
                _startCompletion.TrySetException(new InvalidOperationException("module start failed after disposal"));

            public void CompleteStart() => _startCompletion.TrySetResult(true);

            public void CompleteStop() => _stopCompletion.TrySetResult(true);

            public ValueTask RegisterAsync(IModuleContext context, CancellationToken ct = default) => default;

            public async ValueTask StartAsync(IModuleContext context, CancellationToken ct = default)
            {
                StartCallCount++;
                _startEntered.TrySetResult(true);
                await _startCompletion.Task;
                IsStarted = true;
                _startMutationCompleted.TrySetResult(true);
            }

            public ValueTask PauseAsync(RuntimePauseReason reason, CancellationToken ct = default) => default;
            public ValueTask ResumeAsync(CancellationToken ct = default) => default;
            public async ValueTask StopAsync(CancellationToken ct = default)
            {
                StopCallCount++;
                _activeStopCalls++;
                MaximumConcurrentStopCalls = Math.Max(MaximumConcurrentStopCalls, _activeStopCalls);
                _stopEntered.TrySetResult(true);
                try
                {
                    if (BlockStop)
                        await _stopCompletion.Task;
                    IsStarted = false;
                }
                finally
                {
                    _activeStopCalls--;
                }
            }
        }

        private sealed class RecordingLifecycleModule : IConvaiModule
        {
            public RecordingLifecycleModule(string moduleId) => ModuleId = moduleId;

            public int StartCallCount { get; private set; }
            public int StopCallCount { get; private set; }
            public string ModuleId { get; }
            public string DisplayName => ModuleId;
            public IReadOnlyList<string> RequiredModules => Array.Empty<string>();
            public IReadOnlyList<Type> RequiredServices => Array.Empty<Type>();
            public IReadOnlyList<Type> ProvidedServices => Array.Empty<Type>();
            public bool IsActive => true;

            public ValueTask RegisterAsync(IModuleContext context, CancellationToken ct = default) => default;

            public ValueTask StartAsync(IModuleContext context, CancellationToken ct = default)
            {
                StartCallCount++;
                return default;
            }

            public ValueTask PauseAsync(RuntimePauseReason reason, CancellationToken ct = default) => default;
            public ValueTask ResumeAsync(CancellationToken ct = default) => default;

            public ValueTask StopAsync(CancellationToken ct = default)
            {
                StopCallCount++;
                return default;
            }
        }
    }
}
