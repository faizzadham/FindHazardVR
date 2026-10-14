using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Infrastructure.Networking.Connection;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    ///     Pins the completion contract of a client-initiated disconnect. The backend is exercised with a
    ///     room that never connected: <c>Room.Disconnect()</c> returns at once on such a room and raises no
    ///     event, which is exactly what a released room does after a local disconnect.
    /// </summary>
    [TestFixture]
    public sealed class LiveKitRoomBackendDisconnectTests
    {
        private LiveKitRoomBackend _backend;
        private List<ConnectionState> _stateChanges;
        private int _roomDisconnectedCount;

        [SetUp]
        public void SetUp()
        {
            _backend = new LiveKitRoomBackend(new SilentLogger());
            _stateChanges = new List<ConnectionState>();
            _roomDisconnectedCount = 0;
            _backend.StateChanged += state => _stateChanges.Add(state);
            _backend.RoomDisconnected += (_, _) => _roomDisconnectedCount++;
        }

        [TearDown]
        public void TearDown() => _backend.Dispose();

        [Test]
        public void DisconnectAsync_WhenConnected_CompletesWhenTheRoomIsReleased()
        {
            _backend.StateMachine.ForceTransition(ConnectionState.Connected);
            _stateChanges.Clear();

            Task disconnect = _backend.DisconnectAsync();

            Assert.That(disconnect.IsCompleted, Is.True,
                "A local disconnect has no room event to wait for; it must complete when Room.Disconnect() returns.");
            Assert.That(disconnect.IsFaulted, Is.False);
            Assert.That(_backend.State, Is.EqualTo(ConnectionState.Disconnected));
            Assert.That(_stateChanges,
                Is.EqualTo(new[] { ConnectionState.Disconnecting, ConnectionState.Disconnected }));
        }

        [Test]
        public void DisconnectAsync_WhenConnected_DoesNotRaiseRoomDisconnected()
        {
            _backend.StateMachine.ForceTransition(ConnectionState.Connected);

            Task disconnect = _backend.DisconnectAsync();

            Assert.That(disconnect.IsCompleted, Is.True);
            Assert.That(_roomDisconnectedCount, Is.Zero,
                "RoomDisconnected is reserved for disconnects the room itself reports; the caller of DisconnectAsync already knows.");
        }

        [Test]
        public void DisconnectAsync_WhenReconnecting_EndsDisconnectedWithOneTransition()
        {
            _backend.StateMachine.ForceTransition(ConnectionState.Reconnecting);
            _stateChanges.Clear();

            Task disconnect = _backend.DisconnectAsync();

            Assert.That(disconnect.IsCompleted, Is.True);
            Assert.That(_backend.State, Is.EqualTo(ConnectionState.Disconnected));
            Assert.That(_stateChanges, Is.EqualTo(new[] { ConnectionState.Disconnected }));
        }

        [Test]
        public void DisconnectAsync_WhenAlreadyDisconnected_IsANoOp()
        {
            Task disconnect = _backend.DisconnectAsync();

            Assert.That(disconnect.IsCompleted, Is.True);
            Assert.That(_backend.State, Is.EqualTo(ConnectionState.Disconnected));
            Assert.That(_stateChanges, Is.Empty);
            Assert.That(_roomDisconnectedCount, Is.Zero);
        }

        [Test]
        public void DisconnectAsync_WithCancelledToken_StillDisconnects()
        {
            _backend.StateMachine.ForceTransition(ConnectionState.Connected);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Task disconnect = _backend.DisconnectAsync(cancellationToken: cancellation.Token);

            Assert.That(disconnect.IsCompleted, Is.True);
            Assert.That(disconnect.IsCanceled, Is.False,
                "Once the disconnect request is issued the room is gone; cancellation cannot leave the backend half-connected.");
            Assert.That(_backend.State, Is.EqualTo(ConnectionState.Disconnected));
        }

        [Test]
        public void DisconnectAsync_AfterDispose_Throws()
        {
            _backend.Dispose();

            Assert.ThrowsAsync<ObjectDisposedException>(() => _backend.DisconnectAsync());
        }
    }
}
