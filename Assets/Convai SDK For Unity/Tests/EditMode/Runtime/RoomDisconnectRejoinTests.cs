using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Services;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Core.DependencyInjection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     A client disconnect ends the session and drops the rejoin room; only an unexpected drop
    ///     keeps it. Exercised through the real manager, coordinator and disconnect adapter.
    /// </summary>
    public class RoomDisconnectRejoinTests
    {
        private const string RoomName = "room-a";
        private readonly List<Object> _objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object value in _objects)
                if (value != null)
                    Object.DestroyImmediate(value);
            _objects.Clear();
        }

        [Test]
        public async Task RoomManagerDisconnectAsync_ClearsRejoinRoom()
        {
            ConvaiRoomManager manager = CreateConnectedManager();

            await manager.DisconnectAsync().AsTask();

            AssertRoomCleared(manager);
        }

        [Test]
        public void RoomManagerDisconnectFromRoom_ClearsRejoinRoom()
        {
            ConvaiRoomManager manager = CreateConnectedManager();

            manager.DisconnectFromRoom();

            AssertRoomCleared(manager);
        }

        [Test]
        public async Task ConnectionCoordinatorDisconnectAsync_ClearsRejoinRoom()
        {
            ConvaiRoomManager manager = CreateConnectedManager();

            await manager.ConnectionCoordinator.DisconnectAsync().AsTask();

            AssertRoomCleared(manager);
        }

        [Test]
        public async Task RoomRuntimeDisconnectAsync_ClearsRejoinRoom()
        {
            ConvaiRoomManager manager = CreateConnectedManager();

            await manager.GetRoomRuntime().DisconnectAsync().AsTask();

            AssertRoomCleared(manager);
        }

        [Test]
        public void UnexpectedDisconnect_KeepsRejoinRoom()
        {
            ConvaiRoomManager manager = CreateConnectedManager();

            RaiseUnexpectedDisconnect(manager);

            AssertRoomKept(manager);
        }

        [Test]
        public async Task EarlyReturningClientDisconnect_DoesNotAffectLaterUnexpectedDisconnect()
        {
            ConvaiRoomManager manager = CreateManager();
            await manager.DisconnectAsync().AsTask();
            Assert.IsNull(manager.ConnectionCoordinator, "the disconnect must take the early-return path");

            Inject(manager);
            RaiseUnexpectedDisconnect(manager);

            AssertRoomKept(manager);
        }

        private ConvaiRoomManager CreateConnectedManager()
        {
            ConvaiRoomManager manager = CreateManager();
            Inject(manager);
            return manager;
        }

        private ConvaiRoomManager CreateManager()
        {
            var go = new GameObject("RoomManager");
            _objects.Add(go);
            var manager = go.AddComponent<ConvaiRoomManager>();
            manager.ConversationInputModePrepareControllersOverrideForTests = (_, _) => true;
            return manager;
        }

        private static void Inject(ConvaiRoomManager manager)
        {
            var eventHub = new EventHub(new ImmediateScheduler());
            var stateMachine = new SessionStateMachine(eventHub, new TestLogger());
            manager.InjectDependencies(new ConvaiRoomManagerDependencies(
                eventHub,
                new TestLogger(),
                sessionStateMachine: stateMachine));
            stateMachine.TryTransition(SessionState.Connecting);
            stateMachine.TryTransition(SessionState.Connected);
            SetConnectionContext(manager, new ConnectionContext(
                RoomName, "character-session", "session-1", "character-a", DateTime.UtcNow.AddMinutes(-1)));
        }

        private static void AssertRoomCleared(ConvaiRoomManager manager)
        {
            ConnectionContext context = GetConnectionContext(manager);
            Assert.IsFalse(context.HasValidRoom, "a client disconnect must not leave a room to rejoin");
            Assert.AreEqual("character-session", context.CharacterSessionId);
        }

        private static void AssertRoomKept(ConvaiRoomManager manager)
        {
            ConnectionContext context = GetConnectionContext(manager);
            Assert.AreEqual(RoomName, context.RoomName, "an unexpected drop keeps the room for a rejoin");
            Assert.IsTrue(context.IsRoomValidForRejoin(60));
        }

        private static void RaiseUnexpectedDisconnect(ConvaiRoomManager manager) =>
            typeof(ConvaiRoomManager)
                .GetMethod("HandleUnexpectedRoomDisconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, null);

        private static FieldInfo ConnectionContextField =>
            typeof(ConvaiRoomManager).GetField("_connectionContext", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static ConnectionContext GetConnectionContext(ConvaiRoomManager manager) =>
            (ConnectionContext)ConnectionContextField.GetValue(manager);

        private static void SetConnectionContext(ConvaiRoomManager manager, ConnectionContext context) =>
            ConnectionContextField.SetValue(manager, context);

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
