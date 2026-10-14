using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Core;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Core.Registry;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Core
{
    /// <summary>
    ///     Tests verifying runtime components can be instantiated without Unity (headless).
    ///     Keeping the runtime constructible without MonoBehaviours is what makes it testable.
    /// </summary>
    [Category("Headless")]
    public sealed class HeadlessRuntimeTests
    {
        #region RoomRuntimeBuilder Tests

        [Test]
        public void RoomRuntimeBuilder_CanBuildWithAdapters_WithoutUnity()
        {
            var connectionAdapter = new TestConnectionRuntimeAdapter
            {
                CurrentStateValue = SessionState.Disconnected,
                IsConnectedValue = false,
                CurrentRoomNameValue = "test-room",
                CurrentSessionIdValue = "session-123",
                ConnectAsyncImpl = async ct =>
                {
                    await Task.Delay(1, ct);
                    return RoomConnectionAttemptResult.Success();
                },
                DisconnectAsyncImpl = async ct => await Task.Delay(1, ct),
                WaitForStartCompletionAsyncImpl = async ct =>
                {
                    await Task.Delay(1, ct);
                    return true;
                },
                WaitForConnectionResolutionAsyncImpl = async ct =>
                {
                    await Task.Delay(1, ct);
                    return true;
                }
            };
            var audioAdapter = new TestAudioRuntimeAdapter
            {
                IsMicMutedValue = false,
                StartListeningAsyncImpl = async (_, ct) => await Task.Delay(1, ct),
                StopListeningAsyncImpl = async ct => await Task.Delay(1, ct)
            };

            RoomRuntime runtime = new RoomRuntimeBuilder()
                .WithConnectionAdapter(connectionAdapter)
                .WithAudioAdapter(audioAdapter)
                .WithAgentRegistry(new AgentRegistry())
                .Build();

            // Assert - Runtime is fully functional
            Assert.IsNotNull(runtime);
            Assert.IsNotNull(runtime.Connection);
            Assert.IsNotNull(runtime.Audio);
            Assert.IsNotNull(runtime.Ownership);
            Assert.IsNotNull(runtime.Diagnostics);
            Assert.IsFalse(runtime.IsActive);

            // Cleanup
            runtime.Dispose();
        }

        [Test]
        public void RoomRuntimeBuilder_CreatesCoordinatorsWithCorrectAdapters()
        {
            bool getMicMutedCalled = false;
            bool setMicMutedCalled = false;
            bool setCharacterMutedCalled = false;
            var connectionAdapter = new TestConnectionRuntimeAdapter
            {
                CurrentStateValue = SessionState.Connected,
                IsConnectedValue = true,
                ConnectAsyncImpl = _ => Task.FromResult(RoomConnectionAttemptResult.Success()),
                DisconnectAsyncImpl = _ => Task.CompletedTask,
                WaitForStartCompletionAsyncImpl = _ => Task.FromResult(true),
                WaitForConnectionResolutionAsyncImpl = _ => Task.FromResult(true)
            };
            var audioAdapter = new TestAudioRuntimeAdapter
            {
                GetIsMicMuted = () =>
                {
                    getMicMutedCalled = true;
                    return false;
                },
                SetMicMutedImpl = _ => { setMicMutedCalled = true; },
                SetCharacterMutedImpl = (characterId, muted) =>
                {
                    setCharacterMutedCalled = characterId == "char-1" && muted;
                    return true;
                }
            };

            RoomRuntime runtime = new RoomRuntimeBuilder()
                .WithConnectionAdapter(connectionAdapter)
                .WithAudioAdapter(audioAdapter)
                .WithAgentRegistry(new AgentRegistry())
                .Build();

            // Act - Access coordinator properties that invoke delegates
            _ = runtime.Audio.IsMicMuted;
            runtime.Audio.SetMicMuted(true);
            Assert.IsTrue(((RoomAudioCoordinator)runtime.Audio).SetCharacterMuted("char-1", true));

            Assert.IsTrue(getMicMutedCalled, "IsMicMuted adapter should be called");
            Assert.IsTrue(setMicMutedCalled, "SetMicMuted adapter should be called");
            Assert.IsTrue(setCharacterMutedCalled, "SetCharacterMuted adapter should be called");

            // Cleanup
            runtime.Dispose();
        }

        [Test]
        public void RoomConnectionCoordinator_ForwardsRuntimeStateChangedEvents()
        {
            var adapter = new TestConnectionRuntimeAdapter();
            var coordinator = new RoomConnectionCoordinator(adapter);
            SessionStateChanged? forwarded = null;
            coordinator.StateChanged += evt => forwarded = evt;

            SessionStateChanged stateChanged =
                SessionStateChanged.Create(SessionState.Connecting, SessionState.Connected, "session-123");

            adapter.NotifyStateChanged(stateChanged);

            Assert.IsTrue(forwarded.HasValue);
            Assert.AreEqual(SessionState.Connecting, forwarded.Value.OldState);
            Assert.AreEqual(SessionState.Connected, forwarded.Value.NewState);
        }

        [Test]
        public void RoomRuntimeBuilder_Throws_WhenConnectionAdapterMissing()
        {
            var builder = new RoomRuntimeBuilder()
                .WithAudioAdapter(new TestAudioRuntimeAdapter())
                .WithAgentRegistry(new AgentRegistry());

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.That(ex.Message, Does.Contain("Connection runtime adapter"));
        }

        [Test]
        public void RoomRuntimeBuilder_Throws_WhenAudioAdapterMissing()
        {
            var builder = new RoomRuntimeBuilder()
                .WithConnectionAdapter(new TestConnectionRuntimeAdapter())
                .WithAgentRegistry(new AgentRegistry());

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.That(ex.Message, Does.Contain("Audio runtime adapter"));
        }

        [Test]
        public void RoomRuntimeBuilder_Throws_WhenAgentRegistryMissing()
        {
            var builder = new RoomRuntimeBuilder()
                .WithConnectionAdapter(new TestConnectionRuntimeAdapter())
                .WithAudioAdapter(new TestAudioRuntimeAdapter());

            var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.That(ex.Message, Does.Contain("Agent registry"));
        }

        [Test]
        public async Task RoomConnectionCoordinator_UsesConnectionOrchestrationHooks()
        {
            SessionState state = SessionState.Disconnected;
            bool isConnected = false;
            bool hasStarted = false;
            bool prepareCalled = false;
            int connectCalls = 0;
            var adapter = new TestConnectionRuntimeAdapter
            {
                CurrentStateProvider = () => state,
                IsConnectedProvider = () => isConnected,
                CanConnectProvider = () => true,
                HasStartedProvider = () => hasStarted,
                ConnectAsyncImpl = async ct =>
                {
                    connectCalls++;
                    await Task.Delay(1, ct);
                    state = SessionState.Connected;
                    isConnected = true;
                    return RoomConnectionAttemptResult.Success();
                },
                DisconnectAsyncImpl = async ct => await Task.Delay(1, ct),
                WaitForStartCompletionAsyncImpl = async ct =>
                {
                    await Task.Delay(1, ct);
                    hasStarted = true;
                    return true;
                },
                PrepareConnectionImpl = () =>
                {
                    prepareCalled = true;
                    return true;
                },
                WaitForConnectionResolutionAsyncImpl = async ct =>
                {
                    await Task.Delay(1, ct);
                    return isConnected;
                },
                CurrentRoomNameValue = "test-room",
                CurrentSessionIdValue = "session-123"
            };

            var coordinator = new RoomConnectionCoordinator(adapter);

            RoomSession session = await coordinator.ConnectAsync().AsTask();

            Assert.IsTrue(prepareCalled, "Preparation hook should be invoked before connect.");
            Assert.AreEqual(1, connectCalls, "Only one connect attempt should be issued.");
            Assert.IsTrue(session.IsValid, "Coordinator should surface a valid room session.");
        }

        #endregion

        private sealed class TestConnectionRuntimeAdapter : IRoomConnectionRuntimeAdapter
        {
            public event Action<SessionStateChanged> StateChanged;

            public Func<SessionState> CurrentStateProvider { get; set; }
            public Func<bool> IsConnectedProvider { get; set; }
            public Func<bool> CanConnectProvider { get; set; }
            public Func<bool> HasStartedProvider { get; set; }
            public Func<CancellationToken, Task<bool>> WaitForStartCompletionAsyncImpl { get; set; }
            public Func<bool> PrepareConnectionImpl { get; set; }
            public Func<CancellationToken, Task<bool>> WaitForConnectionResolutionAsyncImpl { get; set; }
            public Func<CancellationToken, Task<RoomConnectionAttemptResult>> ConnectAsyncImpl { get; set; }
            public Func<CancellationToken, Task> DisconnectAsyncImpl { get; set; }
            public SessionState CurrentStateValue { get; set; }
            public bool IsConnectedValue { get; set; }
            public bool CanConnectValue { get; set; } = true;
            public bool HasStartedValue { get; set; } = true;
            public string CurrentRoomNameValue { get; set; } = "room";
            public string CurrentSessionIdValue { get; set; } = "session";

            public SessionState CurrentState => CurrentStateProvider?.Invoke() ?? CurrentStateValue;
            public bool IsConnected => IsConnectedProvider?.Invoke() ?? IsConnectedValue;
            public string CurrentRoomName => CurrentRoomNameValue;
            public string CurrentSessionId => CurrentSessionIdValue;
            public bool CanConnect => CanConnectProvider?.Invoke() ?? CanConnectValue;
            public bool HasStarted => HasStartedProvider?.Invoke() ?? HasStartedValue;

            public Task<bool> WaitForStartCompletionAsync(CancellationToken ct) =>
                WaitForStartCompletionAsyncImpl?.Invoke(ct) ?? Task.FromResult(true);

            public bool PrepareConnection() => PrepareConnectionImpl?.Invoke() ?? true;

            public Task<bool> WaitForConnectionResolutionAsync(CancellationToken ct) =>
                WaitForConnectionResolutionAsyncImpl?.Invoke(ct) ?? Task.FromResult(IsConnected);

            public Task<RoomConnectionAttemptResult> ConnectAsync(CancellationToken ct) =>
                ConnectAsyncImpl?.Invoke(ct) ?? Task.FromResult(RoomConnectionAttemptResult.Success());

            public Task DisconnectAsync(CancellationToken ct) =>
                DisconnectAsyncImpl?.Invoke(ct) ?? Task.CompletedTask;

            public void NotifyStateChanged(SessionStateChanged stateChanged) =>
                StateChanged?.Invoke(stateChanged);
        }

        private sealed class TestAudioRuntimeAdapter : IRoomAudioRuntimeAdapter
        {
            public Func<bool> GetIsMicMuted { get; set; }
            public Action<bool> SetMicMutedImpl { get; set; }
            public Func<int, CancellationToken, Task> StartListeningAsyncImpl { get; set; }
            public Func<CancellationToken, Task> StopListeningAsyncImpl { get; set; }
            public Func<string, bool, bool> SetCharacterMutedImpl { get; set; }
            public Func<string, bool> IsCharacterMutedImpl { get; set; }
            public bool IsMicMutedValue { get; set; }

            public bool IsMicMuted => GetIsMicMuted?.Invoke() ?? IsMicMutedValue;

            public void SetMicMuted(bool muted)
            {
                IsMicMutedValue = muted;
                SetMicMutedImpl?.Invoke(muted);
            }

            public Task StartListeningAsync(int microphoneIndex, CancellationToken ct) =>
                StartListeningAsyncImpl?.Invoke(microphoneIndex, ct) ?? Task.CompletedTask;

            public Task StopListeningAsync(CancellationToken ct) =>
                StopListeningAsyncImpl?.Invoke(ct) ?? Task.CompletedTask;

            public bool SetCharacterMuted(string characterId, bool muted) =>
                SetCharacterMutedImpl?.Invoke(characterId, muted) ?? false;

            public bool IsCharacterMuted(string characterId) =>
                IsCharacterMutedImpl?.Invoke(characterId) ?? false;
        }
    }
}
