using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Infrastructure.Networking.Transport;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Networking.Media;
using Convai.Runtime.Room;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    public class PttMicrophoneOwnershipTests
    {
        [TearDown]
        public void TearDown() => UnityScheduler.Shutdown();

        [TestCase(false)]
        [TestCase(true)]
        public async Task RepeatedReleasesDisposeEachSourceAfterUnpublication(bool unpublishFails)
        {
            _ = UnityScheduler.Instance;
            var participant = new LocalParticipant();
            var room = new TestRoomFacade("room") { LocalParticipant = participant };
            using var manager = new AudioTrackManager(() => room, new MockAgentRegistry(), null, _ => null);
            var factory = new MicrophoneFactory();
            var adapter = new RoomAudioRuntimeAdapter(() => manager, () => new ConnectedController(),
                () => factory, _ => { }, () => null,
                () => ResolvedTurnTakingOptions.DefaultHandsFree, () => null, () => null, () => null, () => null);

            for (int turn = 0; turn < 3; turn++)
            {
                await adapter.StartListeningAsync(0, default);
                Microphone source = factory.Sources[turn];
                participant.Unpublished = new TaskCompletionSource<bool>();
                int previousUnpublishes = participant.UnpublishCount;
                Task release = adapter.StopAndDrainCaptureAsync(default);
                Assert.That(source.DisposeCount, Is.Zero);
                Assert.That(participant.UnpublishCount, Is.EqualTo(previousUnpublishes), "Drain must finish first.");
                source.Drained.SetResult(true);
                for (int attempt = 0; attempt < 100 && participant.UnpublishCount == previousUnpublishes; attempt++)
                    await Task.Yield();
                Assert.That(participant.UnpublishCount, Is.EqualTo(previousUnpublishes + 1));
                Assert.That(release.IsCompleted, Is.False);
                Assert.That(source.DisposeCount, Is.Zero, "Unpublication still owns the source.");
                if (unpublishFails) participant.Unpublished.SetException(new InvalidOperationException("Transport closed"));
                else participant.Unpublished.SetResult(true);
                await release;
                Assert.That(source.DisposeCount, Is.EqualTo(1));
                Assert.That(source.DisposedOnMainThread, Is.True);
                Assert.That(manager.IsPublishing, Is.False);
            }

            await adapter.StopListeningAsync(default);
            foreach (Microphone source in factory.Sources)
                Assert.That(source.DisposeCount, Is.EqualTo(1), "Later cleanup must not dispose a released source twice.");
        }

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

        private sealed class Microphone : IMicrophoneSource, IMicrophoneCaptureDrain
        {
            public readonly TaskCompletionSource<bool> Drained = new();
            public int DisposeCount { get; private set; }
            public bool DisposedOnMainThread { get; private set; }
            public string Name => "Test microphone";
            public string DeviceName => Name;
            public int DeviceIndex => 0;
            public bool IsCapturing { get; private set; }
            public bool IsMuted { get; set; }
            public bool EnableAcousticEchoCancellation { get; set; }
            public event Action<IMicrophoneSource, MicrophoneSessionInvalidationReason> SessionInvalidated { add { } remove { } }
            public void StartCapture() => IsCapturing = true;
            public void StopCapture() => IsCapturing = false;
            public Task StopAndDrainCaptureAsync(CancellationToken cancellationToken) => Drained.Task;
            public void Dispose()
            {
                DisposeCount++;
                DisposedOnMainThread = UnityScheduler.IsOnMainThread;
            }
        }

        private sealed class LocalParticipant : ILocalParticipant
        {
            public TaskCompletionSource<bool> Unpublished;
            public int UnpublishCount;
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
                AudioPublishOptions options = default, CancellationToken ct = default) =>
                Task.FromResult<ILocalAudioTrack>(new AudioTrack(source));
            public Task<ILocalVideoTrack> PublishVideoTrackAsync(IVideoSource source,
                VideoPublishOptions options = default, CancellationToken ct = default) => throw new NotSupportedException();
            public Task UnpublishTrackAsync(ILocalTrack track, CancellationToken ct = default)
            {
                UnpublishCount++;
                return Unpublished.Task;
            }
            public void SetAudioMuted(bool muted) { }
            public void SetVideoMuted(bool muted) { }
        }

        private sealed class AudioTrack : ILocalAudioTrack
        {
            public AudioTrack(IAudioSource source) => Source = source;
            public string Sid => "audio";
            public string Name => Sid;
            public TrackKind Kind => TrackKind.Audio;
            public bool IsMuted { get; private set; }
            public bool IsPublished => true;
            public IAudioSource Source { get; }
            public event Action<bool> MuteChanged { add { } remove { } }
            public void SetMuted(bool muted) => IsMuted = muted;
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
