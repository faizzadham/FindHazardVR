using System;
using System.Collections.Generic;
using System.Threading;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Room;
using Convai.Infrastructure.Networking;
using UnityEngine;

namespace Convai.Tests.EditMode.Mocks
{
    /// <summary>
    ///     Lightweight mock for IConvaiRoomAudioService used in edit-mode tests.
    /// </summary>
    public sealed class MockRoomAudioService : IConvaiRoomAudioService, IConvaiIntentAwareRoomAudioService,
        IConvaiRoomAudioTimelineService,
        IConvaiRoomAudioMediaTimelineService
    {
        private readonly HashSet<string> _mutedCharacters = new();
        private readonly HashSet<string> _remoteAudioEnabledCharacters = new();

        public event Action<bool> MicMuteChanged;
        public event Action<string, bool> RemoteAudioEnabledChanged;

        public bool IsMicMuted { get; private set; }

        public bool RequiresUserGestureForAudio { get; set; }

        public bool IsAudioPlaybackActive { get; set; } = true;

        public bool CanEnableAudioPlayback { get; set; } = true;

        public Action<string> TurnBoundaryOperationRecorded { get; set; }

        public int ToggleMicMuteCallCount { get; private set; }

        public int StartListeningCallCount { get; private set; }

        public Func<bool> ToggleMicMuteOverride { get; set; }

        public void EnableAudioPlayback() => IsAudioPlaybackActive = true;

        public IConvaiOperation<Unit> StartListeningAsync(int microphoneIndex = 0,
            CancellationToken cancellationToken = default)
        {
            StartListeningCallCount++;
            return ConvaiOperation<Unit>.Succeeded(Unit.Value);
        }

        public IConvaiOperation<Unit> StopListeningAsync(CancellationToken cancellationToken = default) =>
            ConvaiOperation<Unit>.Succeeded(Unit.Value);

        public void SetMicMuted(bool muted)
        {
            if (IsMicMuted == muted) return;

            IsMicMuted = muted;
            TurnBoundaryOperationRecorded?.Invoke($"mic:{muted}");
            MicMuteChanged?.Invoke(muted);
        }

        public bool ToggleMicMute()
        {
            ToggleMicMuteCallCount++;
            if (ToggleMicMuteOverride != null) return ToggleMicMuteOverride();

            bool muted = !IsMicMuted;
            SetMicMuted(muted);
            return muted;
        }

        public bool SetCharacterMuted(string characterId, bool muted)
        {
            if (string.IsNullOrEmpty(characterId)) return false;

            if (muted)
                _mutedCharacters.Add(characterId);
            else
                _mutedCharacters.Remove(characterId);

            return true;
        }

        public bool IsCharacterMuted(string characterId) =>
            characterId != null && _mutedCharacters.Contains(characterId);

        public bool SetRemoteAudioEnabled(string characterId, bool enabled)
        {
            if (string.IsNullOrEmpty(characterId)) return false;

            bool wasEnabled = _remoteAudioEnabledCharacters.Contains(characterId);
            if (enabled == wasEnabled) return true;

            if (enabled)
                _remoteAudioEnabledCharacters.Add(characterId);
            else
                _remoteAudioEnabledCharacters.Remove(characterId);

            RemoteAudioEnabledChanged?.Invoke(characterId, enabled);
            return true;
        }

        public bool IsRemoteAudioEnabled(string characterId) =>
            characterId != null && _remoteAudioEnabledCharacters.Contains(characterId);

        public bool BindParticipantAudioOutput(string participantIdentity, AudioSource audioSource) =>
            !string.IsNullOrWhiteSpace(participantIdentity);

        public bool SetParticipantAudioEnabled(string participantIdentity, bool enabled) =>
            !string.IsNullOrWhiteSpace(participantIdentity);

        /// <summary>Playhead value returned by TryGetCharacterAudioPlayhead; null simulates a platform without one.</summary>
        public double? AudioPlayheadSeconds { get; set; }
        internal AudioTimelineSnapshot? AudioTimeline { get; set; }
        internal AudioMediaTimelineSnapshot? AudioMediaTimeline { get; set; }

        public bool TryGetCharacterAudioPlayhead(string characterId, out double playedSeconds)
        {
            playedSeconds = AudioPlayheadSeconds ?? 0d;
            return AudioPlayheadSeconds.HasValue && !string.IsNullOrEmpty(characterId);
        }

        bool IConvaiRoomAudioTimelineService.TryGetCharacterAudioTimeline(
            string characterId,
            out AudioTimelineSnapshot snapshot)
        {
            snapshot = AudioTimeline ?? default;
            return AudioTimeline.HasValue && !string.IsNullOrEmpty(characterId);
        }

        bool IConvaiRoomAudioMediaTimelineService.TryGetCharacterAudioMediaTimeline(
            string characterId,
            out AudioMediaTimelineSnapshot snapshot)
        {
            snapshot = AudioMediaTimeline ?? default;
            return AudioMediaTimeline.HasValue && !string.IsNullOrEmpty(characterId);
        }

        public void RaiseMicMuteChanged(bool muted) => MicMuteChanged?.Invoke(muted);

        public void RaiseRemoteAudioEnabledChanged(string characterId, bool enabled) =>
            RemoteAudioEnabledChanged?.Invoke(characterId, enabled);
    }
}
