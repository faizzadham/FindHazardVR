using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.Logging;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Coordinators;
using Convai.Runtime.Room;
using Convai.Shared.Compatibility;
using UnityEngine;

namespace Convai.Runtime.Adapters.Networking
{
    public partial class ConvaiRoomManager
    {
        internal Action<bool> ConversationInputModeSetMicMutedOverrideForTests { get; set; }
        internal Func<bool, bool> ConversationInputModeSetSttMutedOverrideForTests { get; set; }
        internal Func<bool> ConversationInputModeForceStopOverrideForTests { get; set; }
        internal Func<ResolvedTurnTakingOptions, string, bool>
            ConversationInputModePrepareControllersOverrideForTests { get; set; }

        public IConvaiOperation<Unit> SetConversationInputModeAsync(
            ConversationInputMode mode,
            CancellationToken cancellationToken = default)
        {
            ConvaiOperationException invalidStateException = CreateInvalidConversationInputModeStateException();
            if (invalidStateException != null)
                return ConvaiOperation<Unit>.Failed(invalidStateException);

            return ConvaiOperation<Unit>.FromTask(SetConversationInputModeAsyncCore(mode, cancellationToken));
        }

        private async Task<Unit> SetConversationInputModeAsyncCore(
            ConversationInputMode mode,
            CancellationToken cancellationToken)
        {
            await ConvaiTimers.WaitAsync(_conversationInputModeTransitionGate, cancellationToken);
            try
            {
                ConvaiOperationException invalidStateException = CreateInvalidConversationInputModeStateException();
                if (invalidStateException != null)
                    throw invalidStateException;

                ConversationInputMode currentMode = ActiveConversationInputMode;
                if (currentMode == mode)
                    return Unit.Value;

                _conversationInputModeTransitionInProgress = true;

                TurnTakingOptions currentSourceOptions = _sessionTurnTakingSourceOptions?.Clone();
                if (currentSourceOptions == null)
                {
                    throw new ConvaiOperationException(
                        SessionErrorCodes.SessionInvalidState,
                        "[ConvaiRoomManager] Conversation input mode switch requires an active connected session.");
                }

                TurnTakingOptions nextSourceOptions = currentSourceOptions.Clone();
                nextSourceOptions.Mode = mode;

                ResolvedTurnTakingOptions currentResolvedOptions = CurrentResolvedTurnTakingOptions;
                ResolvedTurnTakingOptions nextResolvedOptions =
                    TurnTakingOptionsResolver.ResolveFromSource(nextSourceOptions);

                if (_logger?.IsEnabled(LogLevel.Info, LogCategory.SDK) == true)
                    _logger?.Info(
                    $"Switching conversation input mode: {currentMode} -> {mode}.",
                    LogCategory.SDK);

                switch ((currentMode, mode))
                {
                    case (ConversationInputMode.PushToTalk, ConversationInputMode.HandsFree):
                        await TransitionPushToTalkToHandsFreeAsync(
                            currentResolvedOptions,
                            nextSourceOptions,
                            nextResolvedOptions,
                            cancellationToken);
                        break;

                    case (ConversationInputMode.HandsFree, ConversationInputMode.PushToTalk):
                        await TransitionHandsFreeToPushToTalkAsync(
                            currentResolvedOptions,
                            nextSourceOptions,
                            nextResolvedOptions,
                            cancellationToken);
                        break;

                    default:
                        UpdateConnectedSessionTurnTakingState(nextSourceOptions, nextResolvedOptions);
                        break;
                }

                return Unit.Value;
            }
            finally
            {
                _conversationInputModeTransitionInProgress = false;
                _conversationInputModeTransitionGate.Release();
            }
        }

        private async Task TransitionPushToTalkToHandsFreeAsync(
            ResolvedTurnTakingOptions currentResolvedOptions,
            TurnTakingOptions nextSourceOptions,
            ResolvedTurnTakingOptions nextResolvedOptions,
            CancellationToken cancellationToken)
        {
            if (!PreparePushToTalkControllersForModeTransition(
                    nextResolvedOptions,
                    "runtime-switch:push-to-talk-to-handsfree"))
            {
                SetConversationInputModeMicMuted(true);
                PreparePushToTalkControllersForModeTransition(
                    currentResolvedOptions,
                    "runtime-switch:push-to-talk-to-handsfree-rollback");
                throw CreateConversationInputModeControlException(
                    "Push-to-talk capture could not be finalized before switching to Hands Free.");
            }

            if (currentResolvedOptions.EnableServerSttToggle && !SetConversationInputModeSttMuted(false))
            {
                SetConversationInputModeMicMuted(true);
                PreparePushToTalkControllersForModeTransition(
                    currentResolvedOptions,
                    "runtime-switch:push-to-talk-to-handsfree-rollback");
                throw CreateConversationInputModeControlException(
                    "Backend speech recognition could not be enabled for Hands Free mode.");
            }

            bool aecChanged = currentResolvedOptions.EnableAcousticEchoCancellation !=
                              nextResolvedOptions.EnableAcousticEchoCancellation;
            try
            {
                await EnsureMicrophonePublishedForRuntimeModeAsync(aecChanged, cancellationToken);
                SetConversationInputModeMicMuted(false);
                UpdateConnectedSessionTurnTakingState(nextSourceOptions, nextResolvedOptions);
            }
            catch
            {
                SetConversationInputModeMicMuted(true);
                if (currentResolvedOptions.EnableServerSttToggle)
                    SetConversationInputModeSttMuted(true);
                PreparePushToTalkControllersForModeTransition(
                    currentResolvedOptions,
                    "runtime-switch:push-to-talk-to-handsfree-rollback");
                throw;
            }
        }

        private async Task TransitionHandsFreeToPushToTalkAsync(
            ResolvedTurnTakingOptions currentResolvedOptions,
            TurnTakingOptions nextSourceOptions,
            ResolvedTurnTakingOptions nextResolvedOptions,
            CancellationToken cancellationToken)
        {
            SetConversationInputModeMicMuted(true);
            if (!ForceConversationInputModeUserStoppedSpeaking())
            {
                SetConversationInputModeMicMuted(false);
                throw CreateConversationInputModeControlException(
                    "The active Hands Free turn could not be finalized before switching to Push To Talk.");
            }

            if (nextResolvedOptions.EnableServerSttToggle && !SetConversationInputModeSttMuted(true))
            {
                SetConversationInputModeMicMuted(false);
                throw CreateConversationInputModeControlException(
                    "Backend speech recognition could not be paused for Push To Talk mode.");
            }

            if (!PreparePushToTalkControllersForModeTransition(
                    nextResolvedOptions,
                    "runtime-switch:handsfree-to-push-to-talk"))
            {
                if (nextResolvedOptions.EnableServerSttToggle)
                    SetConversationInputModeSttMuted(false);
                SetConversationInputModeMicMuted(false);
                PreparePushToTalkControllersForModeTransition(
                    currentResolvedOptions,
                    "runtime-switch:handsfree-to-push-to-talk-rollback");
                throw CreateConversationInputModeControlException(
                    "Push-to-talk controllers could not prepare for the requested input mode.");
            }

            try
            {
                if (nextResolvedOptions.PushToTalkStartupMode == PushToTalkMicStartupMode.OpenOnFirstPress)
                {
                    await StopListeningAsync(cancellationToken).AsTask();
                    SetConversationInputModeMicMuted(true);
                }
                else
                {
                    bool aecChanged = currentResolvedOptions.EnableAcousticEchoCancellation !=
                                      nextResolvedOptions.EnableAcousticEchoCancellation;
                    await EnsureMicrophonePublishedForRuntimeModeAsync(aecChanged, cancellationToken);
                    SetConversationInputModeMicMuted(nextResolvedOptions.StartMutedInPushToTalk);
                }

                UpdateConnectedSessionTurnTakingState(nextSourceOptions, nextResolvedOptions);
            }
            catch
            {
                if (nextResolvedOptions.EnableServerSttToggle)
                    SetConversationInputModeSttMuted(false);
                SetConversationInputModeMicMuted(false);
                PreparePushToTalkControllersForModeTransition(
                    currentResolvedOptions,
                    "runtime-switch:handsfree-to-push-to-talk-rollback");
                throw;
            }
        }

        private async Task EnsureMicrophonePublishedForRuntimeModeAsync(
            bool republishIfAlreadyPublished,
            CancellationToken cancellationToken)
        {
            if (_roomAudioRuntimeAdapter != null)
            {
                await _roomAudioRuntimeAdapter.EnsurePublishedMicrophoneAsync(
                    republishIfAlreadyPublished,
                    cancellationToken);
                return;
            }

            await StartListeningAsync(cancellationToken: cancellationToken).AsTask();
        }

        private bool PreparePushToTalkControllersForModeTransition(
            ResolvedTurnTakingOptions nextResolvedOptions,
            string reason)
        {
            if (ConversationInputModePrepareControllersOverrideForTests != null)
                return ConversationInputModePrepareControllersOverrideForTests(nextResolvedOptions, reason);

            bool prepared = true;
            foreach (ConvaiPushToTalkController controller in ResolveActivePushToTalkControllers())
                prepared &= controller.PrepareForConversationInputModeTransition(nextResolvedOptions, reason);

            return prepared;
        }

        private bool PreparePushToTalkControllersForDisconnect() =>
            PreparePushToTalkControllersForModeTransition(
                CurrentResolvedTurnTakingOptions,
                "explicit-disconnect");

        private void PreparePushToTalkControllersForConversationTargetRouting(
            bool preservesCurrentTurnBoundary)
        {
            if (ActiveConversationInputMode != ConversationInputMode.PushToTalk) return;

            const string reason = "conversation-target-routing";
            bool prepared;
            if (ConversationInputModePrepareControllersOverrideForTests != null)
                prepared = ConversationInputModePrepareControllersOverrideForTests(
                    CurrentResolvedTurnTakingOptions,
                    reason);
            else
            {
                prepared = true;
                foreach (ConvaiPushToTalkController controller in ResolveActivePushToTalkControllers())
                    prepared &= controller.PrepareForConversationTargetRouting(
                        reason,
                        preservesCurrentTurnBoundary);
            }

            if (!prepared)
                _logger?.Warning(
                    "Push-to-talk capture could not close cleanly before conversation routing. " +
                    "The microphone remains force-muted for the routing window.",
                    LogCategory.SDK);
        }

        private void CommitPushToTalkControllersForConversationTargetChange()
        {
            if (ActiveConversationInputMode != ConversationInputMode.PushToTalk) return;

            foreach (ConvaiPushToTalkController controller in ResolveActivePushToTalkControllers())
                controller.CommitConversationTargetChange("conversation-target-confirmed");
        }

        internal bool IsPushToTalkUtteranceInProgress
        {
            get
            {
                if (ActiveConversationInputMode != ConversationInputMode.PushToTalk) return false;
                foreach (ConvaiPushToTalkController controller in ResolveActivePushToTalkControllers())
                    if (controller.IsCapturingOrFinalizing)
                        return true;
                return false;
            }
        }

        private void SetConversationInputModeMicMuted(bool muted)
        {
            if (ConversationInputModeSetMicMutedOverrideForTests != null)
            {
                ConversationInputModeSetMicMutedOverrideForTests(muted);
                return;
            }

            SetMicMuted(muted);
        }

        private bool SetConversationInputModeSttMuted(bool muted) =>
            ConversationInputModeSetSttMutedOverrideForTests?.Invoke(muted) ?? SetSttMuted(muted);

        private bool ForceConversationInputModeUserStoppedSpeaking() =>
            ConversationInputModeForceStopOverrideForTests?.Invoke() ?? ForceUserStoppedSpeaking();

        private static ConvaiOperationException CreateConversationInputModeControlException(string message) =>
            new(SessionErrorCodes.ConnectionServiceUnavailable, $"[ConvaiRoomManager] {message}");

        private List<ConvaiPushToTalkController> ResolveActivePushToTalkControllers()
        {
            ConvaiManager manager = ResolveOwningManager();
            ConvaiPushToTalkController[] controllers =
                ConvaiObjectFind.All<ConvaiPushToTalkController>(FindObjectsInactive.Include);
            var matches = new List<ConvaiPushToTalkController>(controllers.Length);
            for (int i = 0; i < controllers.Length; i++)
            {
                ConvaiPushToTalkController controller = controllers[i];
                if (controller == null)
                    continue;

                if (manager == null || controller.BelongsToManager(manager))
                    matches.Add(controller);
            }

            return matches;
        }

        private ConvaiManager ResolveOwningManager()
        {
            ConvaiManager activeManager = ConvaiManager.ActiveManager;
            if (activeManager != null &&
                activeManager.TryGetRoomManager(out ConvaiRoomManager activeRoomManager) &&
                ReferenceEquals(activeRoomManager, this))
            {
                return activeManager;
            }

            return GetComponent<ConvaiManager>();
        }

        private ConvaiOperationException CreateInvalidConversationInputModeStateException()
        {
            if (CurrentState == SessionState.Connected && IsConnected && _hasConnectedSessionTurnTakingState)
                return null;

            return new ConvaiOperationException(
                SessionErrorCodes.SessionInvalidState,
                $"[ConvaiRoomManager] Conversation input mode switching requires an active connected room. Current state: {CurrentState}.");
        }
    }
}
