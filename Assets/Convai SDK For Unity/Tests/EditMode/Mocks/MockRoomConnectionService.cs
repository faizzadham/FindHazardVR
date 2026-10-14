using System;
using System.Collections.Generic;
using System.Threading;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Runtime;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.NarrativeDesign;
using Convai.Runtime.Room;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Vision.Context;
using Convai.Shared.Types;
using RtviSceneMetadata = Convai.Infrastructure.Protocol.Messages.SceneMetadata;

namespace Convai.Tests.EditMode.Mocks
{
    /// <summary>
    ///     Lightweight mock for IConvaiRoomConnectionService used in edit-mode tests.
    /// </summary>
    public sealed class MockRoomConnectionService : IConvaiRoomConnectionService, IConvaiDynamicContextTransport
    {
        // --- Messaging stubs (record calls for assertions) ---

        public List<ConvaiNarrativeTriggerRequest> SentNarrativeTriggers { get; } = new();
        public List<IReadOnlyList<RtviSceneMetadata>> SentSceneMetadataUpdates { get; } = new();
        public List<Dictionary<string, string>> SentTemplateKeys { get; } = new();
        public List<ConvaiDynamicContextUpdate> SentDynamicContextUpdates { get; } = new();
        public List<string> VisionStatusUpdateIds { get; } = new();
        public List<ConvaiVisionTriggerRequest> VisionTriggerRequests { get; } = new();
        public List<(ConvaiRespondModeLane Lane, ConvaiRespondMode Mode, string UpdateId)> RespondModeUpdates { get; } = new();
        public List<bool> TtsToggleStates { get; } = new();
        public List<bool> SttMutedStates { get; } = new();
        public List<string> TurnControlCalls { get; } = new();
        public Action<string> TurnBoundaryOperationRecorded { get; set; }
        public List<ConversationInputMode> ConversationInputModeRequests { get; } = new();
        public int InterruptBotCallCount { get; private set; }
        public int KillPipelineCallCount { get; private set; }
        public int ForceUserStoppedSpeakingCallCount { get; private set; }
        public int SuccessfulForceUserStoppedSpeakingCallCount { get; private set; }
        public int ResetIdleTimerCallCount { get; private set; }
        public bool SetSttMutedResult { get; set; } = true;
        public bool ForceUserStoppedSpeakingResult { get; set; } = true;
        public event Action Connected;
        public event Action<SessionError> OnSessionError;
        public event Action<SessionStateChanged> OnSessionStateChanged;
        public event Action<ConversationInputMode> ConversationInputModeChanged;

        /// <summary>
        ///     Gets or sets the connection type for testing. Defaults to Audio.
        ///     Set to Video to test vision-related scenarios.
        /// </summary>
        public ConvaiConnectionType ConnectionType { get; set; } = ConvaiConnectionType.Audio;

        public SessionState CurrentState { get; private set; } = SessionState.Disconnected;
        public bool IsConnected { get; private set; }
        public bool HasRoomDetails { get; set; }
        public bool HasPendingOwnershipReconnect { get; set; }
        public ConversationInputMode ActiveConversationInputMode { get; private set; } = ConversationInputMode.HandsFree;
        public IRoomFacade CurrentRoom { get; set; }
        public RTVIHandler RtvHandler { get; set; }
        public MultiCharacterRoomSession CurrentMultiCharacterSession { get; set; }

        public IConvaiOperation<RoomSession> ConnectAsync(CancellationToken cancellationToken = default)
        {
            SessionState oldState = CurrentState;
            CurrentState = SessionState.Connected;
            IsConnected = true;
            Connected?.Invoke();
            OnSessionStateChanged?.Invoke(SessionStateChanged.Create(oldState, CurrentState, "mock-session"));
            return ConvaiOperation<RoomSession>.Succeeded(new RoomSession(
                "mock-session",
                "mock-room",
                "local-player",
                DateTime.UtcNow));
        }

        public IConvaiOperation<RoomSession> ConnectAsync(
            RoomSessionConnectOptions options,
            CancellationToken cancellationToken = default) =>
            ConnectAsync(cancellationToken);

        public IConvaiOperation<RoomSession> JoinMultiCharacterRoomAsync(
            MultiCharacterJoinOptions options,
            CancellationToken cancellationToken = default) =>
            ConnectAsync(cancellationToken);

        public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
            IConvaiCharacterAgent character,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<InteractionTargetResult>.Succeeded(default);

        public IConvaiOperation<InteractionTargetResult> SetInteractionTargetAsync(
            string membershipId,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<InteractionTargetResult>.Succeeded(default);

        public IConvaiOperation<InteractionTargetResult> ClearInteractionTargetAsync(
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<InteractionTargetResult>.Succeeded(default);

        public IConvaiOperation<CharacterRosterUpdateResult> AddCharacterAsync(
            IConvaiCharacterAgent character,
            string characterSessionId = null,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

        public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
            string membershipId,
            string replacementTargetMembershipId = null,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

        public IConvaiOperation<CharacterRosterUpdateResult> RemoveCharacterAsync(
            IConvaiCharacterAgent character,
            string replacementTargetMembershipId = null,
            CancellationToken cancellationToken = default) =>
            ConvaiOperation<CharacterRosterUpdateResult>.Succeeded(default);

        public IConvaiOperation<Unit> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SessionState oldState = CurrentState;
            CurrentState = SessionState.Disconnected;
            IsConnected = false;
            OnSessionStateChanged?.Invoke(SessionStateChanged.Create(oldState, CurrentState, "mock-session"));
            return ConvaiOperation<Unit>.Succeeded(Unit.Value);
        }

        public IConvaiOperation<Unit> SetConversationInputModeAsync(
            ConversationInputMode mode,
            CancellationToken cancellationToken = default)
        {
            ActiveConversationInputMode = mode;
            ConversationInputModeRequests.Add(mode);
            ConversationInputModeChanged?.Invoke(mode);
            return ConvaiOperation<Unit>.Succeeded(Unit.Value);
        }

        public bool SendNarrativeTrigger(ConvaiNarrativeTriggerRequest request)
        {
            SentNarrativeTriggers.Add(request);
            return true;
        }

        public bool UpdateTemplateKeys(Dictionary<string, string> templateKeys)
        {
            SentTemplateKeys.Add(templateKeys);
            return true;
        }

        public bool UpdateSceneMetadata(IReadOnlyList<RtviSceneMetadata> sceneMetadata)
        {
            SentSceneMetadataUpdates.Add(sceneMetadata);
            return true;
        }

        bool IConvaiDynamicContextTransport.SendDynamicContext(ConvaiDynamicContextUpdate update)
        {
            if (update == null) return false;

            SentDynamicContextUpdates.Add(new ConvaiDynamicContextUpdate(
                update.Text,
                update.Mode,
                update.Reaction,
                update.RemoveStatic,
                update.CurrentAttentionObject,
                update.UpdateId,
                update.ActionConfig));
            return true;
        }

        public bool SetTtsEnabled(bool ttsEnabled)
        {
            TtsToggleStates.Add(ttsEnabled);
            return true;
        }

        public bool SetSttMuted(bool muted)
        {
            SttMutedStates.Add(muted);
            TurnControlCalls.Add($"stt:{muted}");
            TurnBoundaryOperationRecorded?.Invoke($"stt:{muted}");
            return SetSttMutedResult;
        }

        public bool InterruptBot()
        {
            InterruptBotCallCount++;
            return true;
        }

        public bool KillPipeline()
        {
            KillPipelineCallCount++;
            return true;
        }

        public bool ForceUserStoppedSpeaking()
        {
            ForceUserStoppedSpeakingCallCount++;
            TurnControlCalls.Add("force-user-stopped-speaking");
            TurnBoundaryOperationRecorded?.Invoke("force-user-stopped-speaking");
            if (ForceUserStoppedSpeakingResult)
                SuccessfulForceUserStoppedSpeakingCallCount++;
            return ForceUserStoppedSpeakingResult;
        }

        public bool ResetIdleTimer()
        {
            ResetIdleTimerCallCount++;
            return true;
        }

        public bool RequestVisionStatus(string updateId = null)
        {
            VisionStatusUpdateIds.Add(updateId);
            return true;
        }

        public bool TriggerVision(ConvaiVisionTriggerRequest request)
        {
            VisionTriggerRequests.Add(request);
            return true;
        }

        public bool UpdateRespondMode(ConvaiRespondModeLane lane, ConvaiRespondMode mode, string updateId = null)
        {
            RespondModeUpdates.Add((lane, mode, updateId));
            return true;
        }

        public void RaiseConnected()
        {
            SessionState oldState = CurrentState;
            CurrentState = SessionState.Connected;
            IsConnected = true;
            Connected?.Invoke();
            OnSessionStateChanged?.Invoke(SessionStateChanged.Create(oldState, CurrentState, "mock-session"));
        }

        public void RaiseConnectionFailed(string code = SessionErrorCodes.ConnectionFailed,
            string message = "Mock connection failure")
        {
            SessionState oldState = CurrentState;
            CurrentState = SessionState.Error;
            var error = SessionError.Create(code, message, "mock-session");
            OnSessionError?.Invoke(error);
            OnSessionStateChanged?.Invoke(SessionStateChanged.Create(oldState, CurrentState, "mock-session", error));
        }
    }
}
