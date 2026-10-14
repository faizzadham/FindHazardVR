using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Networking.Media;
using Convai.Runtime.Room;
using Convai.Tests.EditMode.Fixtures;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     A platform that explains why the microphone could not be published must reach the app as that
    ///     reason, not as a generic publish failure, and must never escape as an unhandled exception.
    /// </summary>
    public class MicrophonePublishFailureTests
    {
        [TearDown]
        public void TearDown() => UnityScheduler.Shutdown();

        [TestCase(SessionErrorCodes.AudioMicPermissionDenied)]
        [TestCase(SessionErrorCodes.AudioMicUnavailable)]
        public async Task ExplainedFailure_IsPublishedWithItsOwnCode(string failureCode)
        {
            const string reason = "Microphone access was denied by the browser.";
            List<SessionError> errors = await StartListeningAgainstFailure(
                new ConvaiOperationException(failureCode, reason));

            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].ErrorCode, Is.EqualTo(failureCode));
            Assert.That(errors[0].Message, Is.EqualTo(reason));
        }

        [Test]
        public async Task UnexplainedFailure_IsStillPublishedAsPublishFailed()
        {
            List<SessionError> errors =
                await StartListeningAgainstFailure(new InvalidOperationException("Transport closed"));

            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].ErrorCode, Is.EqualTo(SessionErrorCodes.AudioMicPublishFailed));
        }

        [Test]
        public async Task FailedPublish_DisposesTheSourceAndLeavesNothingPublished()
        {
            _ = UnityScheduler.Instance;
            var room = new TestRoomFacade("room")
            {
                LocalParticipant = new FailingParticipant(
                    new ConvaiOperationException(SessionErrorCodes.AudioMicPermissionDenied, "denied"))
            };
            using var manager = new AudioTrackManager(() => room, new MockAgentRegistry(), null, _ => null);
            var factory = new MicrophoneFactory();
            RoomAudioRuntimeAdapter adapter = CreateAdapter(manager, factory, new FakeEventHub());

            await adapter.StartListeningAsync(0, default);

            Assert.That(factory.Sources, Has.Count.EqualTo(1));
            Assert.That(factory.Sources[0].DisposeCount, Is.EqualTo(1));
            Assert.That(manager.IsPublishing, Is.False);
        }

        [Test]
        public async Task ListeningAgainAfterADenial_TriesThePlatformAgain()
        {
            _ = UnityScheduler.Instance;
            var participant = new FailingParticipant(
                new ConvaiOperationException(SessionErrorCodes.AudioMicPermissionDenied, "denied"));
            var room = new TestRoomFacade("room") { LocalParticipant = participant };
            using var manager = new AudioTrackManager(() => room, new MockAgentRegistry(), null, _ => null);
            var eventHub = new FakeEventHub();
            var errors = new List<SessionError>();
            eventHub.Subscribe<SessionError>(errors.Add);
            RoomAudioRuntimeAdapter adapter = CreateAdapter(manager, new MicrophoneFactory(), eventHub);

            await adapter.StartListeningAsync(0, default);
            await adapter.StartListeningAsync(0, default);

            Assert.That(participant.PublishAttempts, Is.EqualTo(2),
                "A denial must release the publish locks, or the player can never retry after allowing access.");
            Assert.That(errors, Has.Count.EqualTo(2));
        }

        private static async Task<List<SessionError>> StartListeningAgainstFailure(Exception failure)
        {
            _ = UnityScheduler.Instance;
            var room = new TestRoomFacade("room") { LocalParticipant = new FailingParticipant(failure) };
            using var manager = new AudioTrackManager(() => room, new MockAgentRegistry(), null, _ => null);
            var eventHub = new FakeEventHub();
            var errors = new List<SessionError>();
            eventHub.Subscribe<SessionError>(errors.Add);
            RoomAudioRuntimeAdapter adapter = CreateAdapter(manager, new MicrophoneFactory(), eventHub);

            await adapter.StartListeningAsync(0, default);

            return errors;
        }

        private static RoomAudioRuntimeAdapter CreateAdapter(AudioTrackManager manager, MicrophoneFactory factory,
            FakeEventHub eventHub) =>
            new(() => manager, () => new ConnectedController(), () => factory, _ => { },
                () => null, () => ResolvedTurnTakingOptions.DefaultHandsFree, () => null, () => null,
                () => eventHub, () => null);

        private sealed class MicrophoneFactory : IMicrophoneSourceFactory
        {
            public readonly List<Microphone> Sources = new();
            public string[] GetAvailableDevices() => new[] { "Test microphone" };

            public IMicrophoneSource Create(string deviceName, int deviceIndex = 0, GameObject hostObject = null)
            {
                var source = new Microphone();
                Sources.Add(source);
                return source;
            }
        }

        private sealed class Microphone : IMicrophoneSource
        {
            public int DisposeCount { get; private set; }
            public string Name => "Test microphone";
            public string DeviceName => Name;
            public int DeviceIndex => 0;
            public bool IsCapturing => false;
            public bool IsMuted { get; set; }
            public bool EnableAcousticEchoCancellation { get; set; }
            public event Action<IMicrophoneSource, MicrophoneSessionInvalidationReason> SessionInvalidated { add { } remove { } }
            public void StartCapture() { }
            public void StopCapture() { }
            public void Dispose() => DisposeCount++;
        }

        private sealed class FailingParticipant : ILocalParticipant
        {
            private readonly Exception _failure;
            public FailingParticipant(Exception failure) => _failure = failure;
            public int PublishAttempts { get; private set; }
            public string Sid => "local";
            public string Identity => Sid;
            public string Name => Sid;
            public ParticipantMetadata Metadata => default;
            public bool IsAgent => false;
            public IReadOnlyList<ILocalTrack> LocalTracks => Array.Empty<ILocalTrack>();
            public event Action<ParticipantMetadata> MetadataUpdated { add { } remove { } }
            public event Action<ILocalTrack> TrackPublished { add { } remove { } }
            public event Action<ILocalTrack> TrackUnpublished { add { } remove { } }
            public Task<ILocalAudioTrack> PublishAudioTrackAsync(IAudioSource source,
                AudioPublishOptions options = default, CancellationToken ct = default)
            {
                PublishAttempts++;
                return Task.FromException<ILocalAudioTrack>(_failure);
            }
            public Task<ILocalVideoTrack> PublishVideoTrackAsync(IVideoSource source,
                VideoPublishOptions options = default, CancellationToken ct = default) => throw new NotSupportedException();
            public Task UnpublishTrackAsync(ILocalTrack track, CancellationToken ct = default) => Task.CompletedTask;
            public void SetAudioMuted(bool muted) { }
            public void SetVideoMuted(bool muted) { }
        }

        private sealed class ConnectedController : IConvaiRoomController
        {
            public bool HasRoomDetails => true;
            public bool IsConnectedToRoom => true;
            public bool IsMicMuted => false;
            public string SessionID => "session";
            public string CharacterSessionID => null;
            public string RoomName => "room";
            public string RoomURL => null;
            public string Token => null;
            public string ResolvedSpeakerId => null;
            public string RequestTraceId => null;
            public string ResolvedEndUserId => null;
            public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata => null;
            public RTVIHandler RTVIHandler => null;
            public IRoomFacade CurrentRoom => null;
            public event Action OnRoomConnectionSuccessful { add { } remove { } }
            public event Action OnRoomConnectionFailed { add { } remove { } }
            public event Action<bool> OnMicMuteChanged { add { } remove { } }
            public event Action OnRoomReconnecting { add { } remove { } }
            public event Action OnRoomReconnected { add { } remove { } }
            public event Action OnUnexpectedRoomDisconnected { add { } remove { } }
            public event Action<IRemoteAudioTrack, string, string> OnRemoteAudioTrackSubscribed { add { } remove { } }
            public event Action<string, string> OnRemoteAudioTrackUnsubscribed { add { } remove { } }
            public Task<RoomConnectionAttemptResult> InitializeAsync(string connectionType, string coreServerUrl,
                string characterId, string storedSessionId, bool enableSessionResume, string dynamicInfoText,
                bool keepDynamicInfoInContext) => throw new NotSupportedException();
            public Task<RoomConnectionAttemptResult> InitializeAsync(string connectionType, string coreServerUrl,
                string characterId, string storedSessionId, bool enableSessionResume, string dynamicInfoText,
                bool keepDynamicInfoInContext, RoomJoinOptions joinOptions, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public void DisconnectFromRoom() { }
            public Task DisconnectFromRoomAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void SetMicMuted(bool mute) { }
            public void ToggleMicMute() { }
            public bool SetCharacterAudioMuted(string characterId, bool mute) => false;
            public bool MuteCharacter(string characterId) => false;
            public bool UnmuteCharacter(string characterId) => false;
            public bool IsCharacterAudioMuted(string characterId) => false;
            public void SetAudioSubscriptionPolicy(Func<string, bool> policy) { }
            public void ApplyRemoteAudioPreference(string characterId, bool enabled) { }
            public void Dispose() { }
        }
    }
}
