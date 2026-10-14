using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Abstractions;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Native;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime.Behaviors;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using TrackKind = Convai.Infrastructure.Networking.Transport.TrackKind;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class NativeRoomControllerAudioSubscriptionTests
    {
        [Test]
        public void TransportReconnect_PreservesCharacterSessionId()
        {
            var transport = new TestRealtimeTransport();
            NativeRoomController controller = CreateController(transport);
            ((IRoomDetailsStateTarget)controller).CharacterSessionID = "character-session-1";
            string sessionIdWhileReconnecting = null;

            controller.OnRoomReconnecting += () =>
                sessionIdWhileReconnecting = controller.CharacterSessionID;

            transport.RaiseReconnecting();
            transport.RaiseReconnected();

            Assert.That(sessionIdWhileReconnecting, Is.EqualTo("character-session-1"));
            Assert.That(controller.CharacterSessionID, Is.EqualTo("character-session-1"));
        }

        [Test]
        public void DisconnectFromRoomAsync_WhenTransportFails_ClearsControllerStateAndPreservesFailure()
        {
            var failure = new InvalidOperationException("Synthetic disconnect failure.");
            var transport = new TestRealtimeTransport { DisconnectFailure = failure };
            var registry = new TestAgentRegistry();
            var playerSession = new TestPlayerSession();
            NativeRoomController controller = CreateController(transport, registry, playerSession);
            var state = (IRoomDetailsStateTarget)controller;
            state.Token = "token";
            state.RoomName = "room";
            state.RoomURL = "wss://example.invalid";
            state.SessionID = "session-1";
            state.CharacterSessionID = "character-session-1";
            state.HasRoomDetails = true;
            transport.RaiseStateChanged(TransportState.Connected);
            controller.SetMicMuted(true);

            InvalidOperationException thrown = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await controller.DisconnectFromRoomAsync());

            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(controller.Token, Is.Null);
            Assert.That(controller.RoomName, Is.Null);
            Assert.That(controller.RoomURL, Is.Null);
            Assert.That(controller.SessionID, Is.Null);
            Assert.That(controller.CharacterSessionID, Is.Null);
            Assert.That(controller.HasRoomDetails, Is.False);
            Assert.That(controller.IsConnectedToRoom, Is.False);
            Assert.That(controller.IsMicMuted, Is.False);
            Assert.That(playerSession.IsMicMuted, Is.False);
            Assert.That(registry.ClearTransportBindingsCallCount, Is.EqualTo(1));
        }

        [Test]
        public void DisconnectFromRoom_WhenTransportFails_ReportsSanitizedFailure()
        {
            const string sensitiveDetails = "Synthetic details must not be logged.";
            var transport = new TestRealtimeTransport
            {
                DisconnectFailure = new InvalidOperationException(sensitiveDetails)
            };
            var logger = new TestLogger();
            NativeRoomController controller = CreateController(transport, logger: logger);

            controller.DisconnectFromRoom();

            LogEntry error = logger.Entries.Single(entry => entry.Level == LogLevel.Error);
            Assert.That(error.Message,
                Is.EqualTo("Background Native room disconnect failed (InvalidOperationException)."));
            Assert.That(error.Source, Is.EqualTo(nameof(NativeRoomController)));
            Assert.That(error.Message, Does.Not.Contain(sensitiveDetails));
            Assert.That(error.Exception, Is.Null, "The transport exception must not leak into diagnostic metadata.");
            Assert.That(error.Category, Is.EqualTo(LogCategory.Transport));
        }

        [Test]
        public void TransportAudioSubscription_WhenTrackWrapperAppearsAfterConnect_NotifiesOnce()
        {
            var transport = new TestRealtimeTransport();
            NativeRoomController controller = CreateController(transport);
            int notifications = 0;
            IRemoteAudioTrack notifiedTrack = null;
            string notifiedParticipantSid = null;
            string notifiedParticipantIdentity = null;

            controller.OnRemoteAudioTrackSubscribed += (track, participantSid, participantIdentity) =>
            {
                notifications++;
                notifiedTrack = track;
                notifiedParticipantSid = participantSid;
                notifiedParticipantIdentity = participantIdentity;
            };

            const string membershipIdentity = "character:membership-1";
            transport.RaiseTrackSubscribed(new TrackInfo("track-1", "participant-1", membershipIdentity,
                TrackKind.Audio, "agent-audio"));

            var participant = new TestRemoteParticipant("participant-1", membershipIdentity);
            var audioTrack = new TestRemoteAudioTrack("track-1", "agent-audio", participant);
            participant.SetAudioTracks(audioTrack);
            transport.Room = new TestRoomFacade("room-1", participant);

            transport.RaiseReconnected();
            transport.RaiseReconnected();

            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(notifiedTrack, Is.SameAs(audioTrack));
            Assert.That(notifiedParticipantSid, Is.EqualTo("participant-1"));
            Assert.That(notifiedParticipantIdentity, Is.EqualTo(membershipIdentity));
        }

        [Test]
        public void TransportAudioSubscription_WhenRoomFacadeChanges_DoesNotApplyStalePendingTrack()
        {
            var transport = new TestRealtimeTransport();
            NativeRoomController controller = CreateController(transport);
            int notifications = 0;
            controller.OnRemoteAudioTrackSubscribed += (_, _, _) => notifications++;

            var oldParticipant = new TestRemoteParticipant("participant-1", "character-1");
            transport.Room = new TestRoomFacade("room-1", oldParticipant);
            transport.RaiseReconnected();

            transport.RaiseTrackSubscribed(new TrackInfo("track-1", "participant-1", "character-1",
                TrackKind.Audio, "old-audio"));

            var newParticipant = new TestRemoteParticipant("participant-1", "character-1");
            newParticipant.SetAudioTracks(new TestRemoteAudioTrack("track-1", "new-audio", newParticipant));
            transport.Room = new TestRoomFacade("room-2", newParticipant);

            transport.RaiseReconnected();

            Assert.That(notifications, Is.Zero);
        }

        [Test]
        public void FacadeAudioSubscription_WhenPendingTrackExists_NotifiesOnceAndClearsPending()
        {
            var transport = new TestRealtimeTransport();
            NativeRoomController controller = CreateController(transport);
            int notifications = 0;
            controller.OnRemoteAudioTrackSubscribed += (_, _, _) => notifications++;

            var participant = new TestRemoteParticipant("participant-1", "character-1");
            var room = new TestRoomFacade("room-1", participant);
            transport.Room = room;
            transport.RaiseReconnected();
            transport.RaiseTrackSubscribed(new TrackInfo("track-1", "participant-1", "character-1",
                TrackKind.Audio, "agent-audio"));

            var audioTrack = new TestRemoteAudioTrack("track-1", "agent-audio", participant);
            participant.SetAudioTracks(audioTrack);

            room.RaiseAudioTrackSubscribed(audioTrack, participant);
            transport.RaiseReconnected();

            Assert.That(notifications, Is.EqualTo(1));
        }

        [Test]
        public void PendingAudioSubscription_WhenTransportIdentityMissing_UsesResolvedParticipantIdentityForPolicy()
        {
            var transport = new TestRealtimeTransport();
            NativeRoomController controller = CreateController(transport);
            string policyIdentity = null;
            controller.SetAudioSubscriptionPolicy(identity =>
            {
                policyIdentity = identity;
                return true;
            });

            transport.RaiseTrackSubscribed(new TrackInfo("track-1", "participant-1", null,
                TrackKind.Audio, "agent-audio"));

            var participant = new TestRemoteParticipant("participant-1", "character-1");
            participant.SetAudioTracks(new TestRemoteAudioTrack("track-1", "agent-audio", participant));
            transport.Room = new TestRoomFacade("room-1", participant);

            transport.RaiseReconnected();

            Assert.That(policyIdentity, Is.EqualTo("character-1"));
        }

        private static NativeRoomController CreateController(
            TestRealtimeTransport transport,
            TestAgentRegistry registry = null,
            TestPlayerSession playerSession = null,
            TestLogger logger = null) =>
            new(
                registry ?? new TestAgentRegistry(),
                playerSession ?? new TestPlayerSession(),
                new TestTransportConfiguration(),
                new TestSessionPersistence(),
                new ImmediateDispatcher(),
                logger ?? new TestLogger(),
                transport: transport);

        private sealed class TestRealtimeTransport : IRealtimeTransport
        {
            public Exception DisconnectFailure { get; set; }
            public TransportState State { get; private set; } = TransportState.Connected;
            public TransportSessionInfo? CurrentSession => null;
            public TransportCapabilities Capabilities => TransportCapabilities.Native();
            public AudioRuntimeState AudioState => default;
            public bool IsConnected => State == TransportState.Connected;
            public IRoomFacade Room { get; set; }
            public bool IsMicrophoneEnabled => false;
            public bool IsMicrophoneMuted => false;

            public event Action<DataPacket> DataReceived;
            public event Action<TransportSessionInfo> Connected;
            public event Action<DisconnectReason> Disconnected;
            public event Action<TransportError> ConnectionFailed;
            public event Action Reconnecting;
            public event Action Reconnected;
            public event Action<TransportState> StateChanged;
            public event Action<TransportParticipantInfo> ParticipantConnected;
            public event Action<TransportParticipantInfo> ParticipantDisconnected;
            public event Action<TrackInfo> TrackSubscribed;
            public event Action<TrackInfo> TrackUnsubscribed;
            public event Action<bool> MicrophoneEnabledChanged;
            public event Action<bool> MicrophoneMuteChanged;
            public event Action<bool> AudioPlaybackStateChanged;

            public Task SendDataAsync(ReadOnlyMemory<byte> payload, bool reliable = true, string topic = null,
                string[] destinationIdentities = null, CancellationToken ct = default) => Task.CompletedTask;

            public Task<bool> ConnectAsync(string url, string token, TransportConnectOptions options = null,
                CancellationToken ct = default) => Task.FromResult(true);

            public Task DisconnectAsync(DisconnectReason reason = DisconnectReason.ClientInitiated,
                CancellationToken ct = default) => DisconnectFailure == null
                ? Task.CompletedTask
                : Task.FromException(DisconnectFailure);

            public void EnableAudio() { }
            public Task<bool> EnableMicrophoneAsync(int microphoneDeviceIndex = 0, CancellationToken ct = default) =>
                Task.FromResult(true);
            public Task DisableMicrophoneAsync(CancellationToken ct = default) => Task.CompletedTask;
            public void SetMicrophoneMuted(bool muted) { }
            public bool CanEnableMicrophone() => true;
            public bool CanEnableAudio() => true;
            public void Dispose() { }

            public void RaiseTrackSubscribed(TrackInfo track) => TrackSubscribed?.Invoke(track);
            public void RaiseStateChanged(TransportState state)
            {
                State = state;
                StateChanged?.Invoke(state);
            }
            public void RaiseReconnecting() => Reconnecting?.Invoke();
            public void RaiseReconnected() => Reconnected?.Invoke();
        }

        private sealed class TestRemoteAudioTrack : IRemoteAudioTrack, IRemoteAudioControlTrack
        {
            public TestRemoteAudioTrack(string sid, string name, IRemoteParticipant participant)
            {
                Sid = sid;
                Name = name;
                Participant = participant;
            }

            public string Sid { get; }
            public string Name { get; }
            public TrackKind Kind => TrackKind.Audio;
            public bool IsMuted => false;
            public IRemoteParticipant Participant { get; }
            public bool IsSubscribed => true;
            public bool IsAttached => false;
            public bool RemoteAudioEnabled { get; private set; } = true;

            public event Action<bool> MuteChanged;

            public IAudioStream CreateAudioStream() => null;
            public void AttachToAudioSource(AudioSource audioSource) { }
            public void Detach() { }
            public void SetRemoteAudioEnabled(bool enabled) => RemoteAudioEnabled = enabled;
        }

        private sealed class TestAgentRegistry : IAgentRegistry
        {
            public int ClearTransportBindingsCallCount { get; private set; }
            public IReadOnlyList<IConvaiCharacterAgent> Characters => Array.Empty<IConvaiCharacterAgent>();
            public IReadOnlyList<IConvaiPlayerAgent> Players => Array.Empty<IConvaiPlayerAgent>();
            public IConvaiPlayerAgent LocalPlayer => null;

            public event Action<IConvaiCharacterAgent> CharacterRegistered;
            public event Action<IConvaiCharacterAgent> CharacterUnregistered;
            public event Action<IConvaiPlayerAgent> PlayerRegistered;

            public void RegisterCharacter(IConvaiCharacterAgent character, string ownerId = null) { }
            public void RegisterPlayer(IConvaiPlayerAgent player) { }
            public void Unregister(IConvaiCharacterAgent character) { }
            public void Unregister(IConvaiPlayerAgent player) { }
            public bool TryGetCharacter(string characterId, out IConvaiCharacterAgent agent)
            {
                agent = null;
                return false;
            }
            public string GetOwner(IConvaiCharacterAgent character) => null;
            public IReadOnlyList<IConvaiCharacterAgent> GetCharactersByOwner(string ownerId) =>
                Array.Empty<IConvaiCharacterAgent>();
            public int GetCharacterCountByOwner(string ownerId) => 0;
            public bool TryGetCharacterById(string characterId, out IConvaiCharacterAgent agent) =>
                TryGetCharacter(characterId, out agent);
            public bool TryGetAudioSource(string characterId, out AudioSource audioSource)
            {
                audioSource = null;
                return false;
            }
            public void SetAudioSource(string characterId, AudioSource audioSource) { }
            public void SetParticipantId(string characterId, string participantId) { }
            public bool TryGetParticipantId(string characterId, out string participantId)
            {
                participantId = null;
                return false;
            }
            public bool TryGetCharacterByParticipantId(string participantId, out IConvaiCharacterAgent agent)
            {
                agent = null;
                return false;
            }
            public void ClearTransportBindings() => ClearTransportBindingsCallCount++;
            public void SetCharacterMuted(string characterId, bool muted) { }
            public bool IsCharacterMuted(string characterId) => false;
        }

        private sealed class TestPlayerSession : IPlayerSession
        {
            public string PlayerId => "player-1";
            public string PlayerName => "Player";
            public bool IsMicMuted { get; private set; }

            public void SetMicMuted(bool mute) => IsMicMuted = mute;
            public void OnPlayerTranscriptionReceived(string transcript, TranscriptionPhase transcriptionPhase) { }
            public void OnPlayerTranscriptionReceived(string transcript, TranscriptionPhase transcriptionPhase,
                SpeakerInfo speakerInfo) { }
            public void OnPlayerStartedSpeaking(string sessionId) { }
            public void OnPlayerStoppedSpeaking(string sessionId, bool didProduceFinalTranscript) { }
        }

        private sealed class TestTransportConfiguration : ITransportConfiguration
        {
            public string ApiKey => "test-key";
            public string CoreServerUrl => "https://example.invalid";
            public ConvaiConnectionType ConnectionType => ConvaiConnectionType.Audio;
            public string VideoTrackName => "video";
            public ConvaiServerEndpoint ServerEndpoint => ConvaiServerEndpoint.Connect;
            public LipSyncTransportOptions LipSyncTransportOptions => LipSyncTransportOptions.Disabled;
            public string EndUserId => null;
            public IReadOnlyDictionary<string, object> EndUserMetadata => null;
            public bool Debug => false;
        }

        private sealed class TestSessionPersistence : ISessionPersistence
        {
            public string LoadSession(string characterId) => null;
            public void SaveSession(string characterId, string sessionId) { }
            public void ClearSession(string characterId) { }
            public void ClearAllSessions() { }
            public bool HasSession(string characterId) => false;
        }

        private sealed class ImmediateDispatcher : IMainThreadDispatcher
        {
            public bool TryDispatch(Action action)
            {
                action?.Invoke();
                return true;
            }
        }
    }
}
