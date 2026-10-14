using System;
using System.Collections.Generic;
using Convai.Domain.Models;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Shared.Types;

namespace Convai.Tests.EditMode.Mocks
{
    internal sealed class TestRoomFacade : IRoomFacade
    {
        private readonly List<IRemoteParticipant> _remoteParticipants;

        public TestRoomFacade(string sid, params IRemoteParticipant[] remoteParticipants)
        {
            Sid = sid;
            Name = sid;
            _remoteParticipants = new List<IRemoteParticipant>(remoteParticipants);
        }

        public string Sid { get; }
        public string Name { get; }
        public RoomState State => RoomState.Connected;
        public bool IsConnected => true;
        public ILocalParticipant LocalParticipant { get; set; }
        public IReadOnlyList<IRemoteParticipant> RemoteParticipants => _remoteParticipants;
        public IEnumerable<IParticipant> AllParticipants => _remoteParticipants;
        public int RemoteParticipantCount => _remoteParticipants.Count;

        public event Action<IRemoteParticipant> ParticipantJoined;
        public event Action<IRemoteParticipant> ParticipantLeft;
        public event Action<IParticipant, ParticipantMetadata> ParticipantMetadataUpdated;
        public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackSubscribed;
        public event Action<IRemoteAudioTrack, IRemoteParticipant> AudioTrackUnsubscribed;
        public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackSubscribed;
        public event Action<IRemoteVideoTrack, IRemoteParticipant> VideoTrackUnsubscribed;
        public event Action<TrackSubscriptionEventArgs> TrackSubscribed;
        public event Action<TrackSubscriptionEventArgs> TrackUnsubscribed;
        public event Action<RoomState> StateChanged;
        public event Action Reconnecting;
        public event Action Reconnected;
        public event Action<DisconnectReason> Disconnected;

        public IRemoteParticipant GetParticipantBySid(string sid)
        {
            TryGetParticipantBySid(sid, out IRemoteParticipant participant);
            return participant;
        }

        public IRemoteParticipant GetParticipantByIdentity(string identity)
        {
            TryGetParticipantByIdentity(identity, out IRemoteParticipant participant);
            return participant;
        }

        public bool TryGetParticipantBySid(string sid, out IRemoteParticipant participant)
        {
            participant = _remoteParticipants.Find(candidate => candidate.Sid == sid);
            return participant != null;
        }

        public bool TryGetParticipantByIdentity(string identity, out IRemoteParticipant participant)
        {
            participant = _remoteParticipants.Find(candidate => candidate.Identity == identity);
            return participant != null;
        }

        public void RaiseAudioTrackSubscribed(IRemoteAudioTrack track, IRemoteParticipant participant) =>
            AudioTrackSubscribed?.Invoke(track, participant);
    }

    internal sealed class TestRemoteParticipant : IRemoteParticipant
    {
        private readonly List<IRemoteAudioTrack> _audioTracks = new();
        private readonly List<IRemoteTrack> _subscribedTracks = new();

        public TestRemoteParticipant(string sid, string identity)
        {
            Sid = sid;
            Identity = identity;
        }

        public string Sid { get; }
        public string Identity { get; }
        public string Name => Identity;
        public ParticipantMetadata Metadata => default;
        public bool IsAgent => true;
        public IReadOnlyList<TrackPublicationInfo> TrackPublications => Array.Empty<TrackPublicationInfo>();
        public IReadOnlyList<IRemoteTrack> SubscribedTracks => _subscribedTracks;
        public IEnumerable<IRemoteAudioTrack> AudioTracks => _audioTracks;
        public IEnumerable<IRemoteVideoTrack> VideoTracks => Array.Empty<IRemoteVideoTrack>();

        public event Action<ParticipantMetadata> MetadataUpdated;
        public event Action<IRemoteTrack, TrackPublicationInfo> TrackSubscribed;
        public event Action<IRemoteTrack, TrackPublicationInfo> TrackUnsubscribed;
        public event Action<TrackPublicationInfo, bool> TrackMuteChanged;

        public void SetAudioTracks(params IRemoteAudioTrack[] tracks)
        {
            _audioTracks.Clear();
            _subscribedTracks.Clear();
            _audioTracks.AddRange(tracks);
            _subscribedTracks.AddRange(tracks);
        }
    }

}
