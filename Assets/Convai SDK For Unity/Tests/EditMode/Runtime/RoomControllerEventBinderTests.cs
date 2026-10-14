using System;
using Convai.Infrastructure.Networking;
using Convai.Runtime.Adapters.Networking;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class RoomControllerEventBinderTests
    {
        [Test]
        public void Attach_ForwardsOnlyNonConnectionEvents()
        {
            int micMuteChanges = 0;
            int unexpectedDisconnects = 0;
            int subscribedCount = 0;
            int unsubscribedCount = 0;

            var binder = new RoomControllerEventBinder(
                _ => micMuteChanges++,
                () => unexpectedDisconnects++,
                (_, participantId, characterId) =>
                {
                    subscribedCount++;
                    Assert.AreEqual("participant-1", participantId);
                    Assert.AreEqual("char-1", characterId);
                },
                (participantId, characterId) =>
                {
                    unsubscribedCount++;
                    Assert.AreEqual("participant-1", participantId);
                    Assert.AreEqual("char-1", characterId);
                });

            var events = new TestRoomControllerEvents();
            binder.Attach(events);

            events.RaiseRoomConnectionSuccessful();
            events.RaiseRoomConnectionFailed();
            events.RaiseRoomReconnecting();
            events.RaiseRoomReconnected();
            events.RaiseMicMuteChanged(true);
            events.RaiseUnexpectedRoomDisconnected();
            events.RaiseRemoteAudioTrackSubscribed(null, "participant-1", "char-1");
            events.RaiseRemoteAudioTrackUnsubscribed("participant-1", "char-1");

            Assert.AreEqual(1, micMuteChanges);
            Assert.AreEqual(1, unexpectedDisconnects);
            Assert.AreEqual(1, subscribedCount);
            Assert.AreEqual(1, unsubscribedCount);
        }

        [Test]
        public void Detach_StopsForwardingCallbacks()
        {
            int callbackCount = 0;
            var binder = new RoomControllerEventBinder(
                _ => callbackCount++,
                () => callbackCount++,
                (_, _, _) => callbackCount++,
                (_, _) => callbackCount++);

            var events = new TestRoomControllerEvents();
            binder.Attach(events);
            binder.Detach();

            events.RaiseMicMuteChanged(true);
            events.RaiseUnexpectedRoomDisconnected();
            events.RaiseRemoteAudioTrackSubscribed(null, "participant-1", "char-1");
            events.RaiseRemoteAudioTrackUnsubscribed("participant-1", "char-1");

            Assert.AreEqual(0, callbackCount);
        }

        private sealed class TestRoomControllerEvents : IRoomControllerEvents
        {
            public event Action OnRoomConnectionSuccessful;
            public event Action OnRoomConnectionFailed;
            public event Action<bool> OnMicMuteChanged;
            public event Action OnRoomReconnecting;
            public event Action OnRoomReconnected;
            public event Action OnUnexpectedRoomDisconnected;
            public event Action<IRemoteAudioTrack, string, string> OnRemoteAudioTrackSubscribed;
            public event Action<string, string> OnRemoteAudioTrackUnsubscribed;

            public void RaiseRoomConnectionSuccessful() => OnRoomConnectionSuccessful?.Invoke();
            public void RaiseRoomConnectionFailed() => OnRoomConnectionFailed?.Invoke();
            public void RaiseMicMuteChanged(bool muted) => OnMicMuteChanged?.Invoke(muted);
            public void RaiseRoomReconnecting() => OnRoomReconnecting?.Invoke();
            public void RaiseRoomReconnected() => OnRoomReconnected?.Invoke();
            public void RaiseUnexpectedRoomDisconnected() => OnUnexpectedRoomDisconnected?.Invoke();
            public void RaiseRemoteAudioTrackSubscribed(IRemoteAudioTrack track, string participantId, string characterId) =>
                OnRemoteAudioTrackSubscribed?.Invoke(track, participantId, characterId);
            public void RaiseRemoteAudioTrackUnsubscribed(string participantId, string characterId) =>
                OnRemoteAudioTrackUnsubscribed?.Invoke(participantId, characterId);
        }
    }
}
