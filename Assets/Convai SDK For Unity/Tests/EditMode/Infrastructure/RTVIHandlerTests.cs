using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Application.Services.Transcript;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.DomainEvents.Vision;
using Convai.Domain.Emotion;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Protocol;
using Convai.Infrastructure.Protocol.Messages;
using Convai.RestAPI.Internal;
using Convai.Runtime.Actions;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Room;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Mocks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [Test]
        public async Task PttCommit_TargetsAgentAndRoutesOnlyMatchingCompletion()
        {
            RtviTestContext context = CreateContext();
            context.Transport.Room = new TestRoomFacade("room", new TestRemoteParticipant("bot-sid", "bot-identity"));
            Task<PttCommitResult> commit = context.Handler.PttCommits.CommitAsync("release-1", "bot-sid", default);
            JObject sent = JObject.Parse(context.Transport.SentPayloads.Single());
            Assert.That(sent.Value<string>("type"), Is.EqualTo("force-user-stopped-speaking"));
            Assert.That(sent.Value<string>("id"), Is.EqualTo("release-1"));
            Assert.That(context.Transport.SentDestinations.Single(), Is.EqualTo(new[] { "bot-identity" }));
            var result = new JObject
            {
                ["event_type"] = "force-user-stopped-speaking", ["status"] = "success",
                ["extras"] = new JObject { ["stop_id"] = "release-1", ["status"] = "empty" }
            };
            context.Gateway.ProcessIncoming(CreateServerMessagePacket("server-response", result, "other-bot"));
            Assert.That(commit.IsCompleted, Is.False);
            context.Gateway.ProcessIncoming(CreateServerMessagePacket("server-response", result, "bot-sid"));
            Assert.That((await commit).Status, Is.EqualTo(PttCommitStatus.Empty));

            Task<PttCommitResult> next = context.Handler.PttCommits.CommitAsync("release-2", "bot-sid", default);
            context.Transport.RaiseStateChanged(TransportState.Reconnecting);
            Assert.That((await next).Reason, Is.EqualTo("session_closed"));
            context.Handler.PttCommits.Dispose();
        }

        [Test]
        public void SendData_Publishes_OutboundRtviMessageSent()
        {
            EventHub eventHub = CreateEventHub();
            RTVIHandler handler = CreateHandler(eventHub, out _, out _);
            OutboundRtviMessageSent captured = default;
            eventHub.Subscribe<OutboundRtviMessageSent>(e => captured = e);

            handler.SendData(new RTVIResetIdleTimer());

            Assert.AreEqual("reset-idle-timer", captured.MessageType);
            Assert.IsFalse(string.IsNullOrWhiteSpace(captured.MessageId));
        }

        [Test]
        public void BuildOutboundLogMessage_ExcludesPayloadContent()
        {
            const string outboundText = "private-outbound-text";
            var message = new RTVIUserTextMessage(outboundText, "typed-1");
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(message);

            string logMessage = RTVIHandler.BuildOutboundLogMessage(
                message,
                Encoding.UTF8.GetByteCount(json));

            StringAssert.Contains(outboundText, json);
            StringAssert.DoesNotContain(outboundText, logMessage);
            StringAssert.Contains("type=user_text_message", logMessage);
            StringAssert.Contains($"payloadBytes={Encoding.UTF8.GetByteCount(json)}", logMessage);
        }

        [Test]
        public void SendDataAsync_PropagatesTransportFailureBeforePublishingSuccess()
        {
            RtviTestContext context = CreateContext();
            context.Transport.SendFailure = new InvalidOperationException("data channel unavailable");
            int sentEvents = 0;
            context.EventHub.Subscribe<OutboundRtviMessageSent>(_ => sentEvents++);

            InvalidOperationException error = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await context.Handler.SendDataAsync(new RTVIResetIdleTimer()));

            Assert.That(error.Message, Is.EqualTo("data channel unavailable"));
            Assert.That(sentEvents, Is.Zero,
                "A canonical command must not look sent when the transport rejected its bytes.");
        }

        [Test]
        public void SendDataAsync_FailedTypedText_DoesNotRegisterEchoCorrelation()
        {
            const string text = "typed text that was not sent";
            RtviTestContext context = CreateContext();
            context.Transport.SendFailure = new InvalidOperationException("data channel unavailable");

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await context.Handler.SendDataAsync(new RTVIUserTextMessage(text, "typed-failed")));

            FinalUserTranscriptionReceived captured = default;
            context.EventHub.Subscribe<FinalUserTranscriptionReceived>(e => captured = e);
            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject { ["text"] = text }));

            Assert.That(captured.MessageId, Is.Empty);
            Assert.That(context.PlayerSession.TypedTranscriptions, Is.Empty);
            Assert.That(context.PlayerSession.Transcriptions, Has.Count.EqualTo(1));
            Assert.That(context.PlayerSession.Transcriptions[0].Phase, Is.EqualTo(TranscriptionPhase.ProcessedFinal));
        }

        [Test]
        public void ServerMessage_UserIdleWarning_Publishes_Event()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            UserIdleWarningReceived captured = default;
            eventHub.Subscribe<UserIdleWarningReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "user-idle-warning",
                new JObject
                {
                    ["remaining_seconds"] = 300,
                    ["message"] = "Idle warning"
                }));

            Assert.AreEqual(300, captured.RemainingSeconds);
            Assert.AreEqual("Idle warning", captured.Message);
        }

        [Test]
        public void ServerMessage_ServerResponseContextUpdate_Publishes_DynamicContextResult()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            DynamicContextUpdateResultReceived captured = default;
            eventHub.Subscribe<DynamicContextUpdateResultReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "server-response",
                new JObject
                {
                    ["event_type"] = "context-update",
                    ["status"] = "success",
                    ["message"] = "Context updated",
                    ["extras"] = new JObject
                    {
                        ["update_id"] = "ctx-1",
                        ["context_revision"] = 7,
                        ["token_count"] = 120,
                        ["static_token_count"] = 30,
                        ["runtime_token_count"] = 90,
                        ["remaining_tokens"] = 29880,
                        ["requested_run_llm"] = "true",
                        ["actual_run_llm"] = "false",
                        ["downgrade_reason"] = "user_speaking",
                        ["interrupted"] = true,
                        ["llm_triggered"] = false,
                        ["prompt_rebuild"] = "deferred",
                        ["action_config_updated"] = true,
                        ["action_config_created"] = false,
                        ["actions_count"] = 2,
                        ["objects_count"] = 1,
                        ["characters_count"] = 1,
                        ["current_attention_object"] = "Lever",
                        ["current_attention_object_cleared"] = false,
                        ["action_generation_strategy_changed"] = true,
                        ["action_generation_strategy_status"] = "applied"
                    }
                }));

            Assert.AreEqual("success", captured.Status);
            Assert.AreEqual("Context updated", captured.Message);
            Assert.AreEqual("ctx-1", captured.UpdateId);
            Assert.AreEqual(7, captured.ContextRevision);
            Assert.AreEqual(120, captured.TokenCount);
            Assert.AreEqual(30, captured.StaticTokenCount);
            Assert.AreEqual(90, captured.RuntimeTokenCount);
            Assert.AreEqual(29880, captured.RemainingTokens);
            Assert.AreEqual("true", captured.RequestedRunLlm);
            Assert.AreEqual("false", captured.ActualRunLlm);
            Assert.AreEqual("user_speaking", captured.DowngradeReason);
            Assert.IsTrue(captured.Interrupted);
            Assert.IsFalse(captured.LlmTriggered);
            Assert.IsFalse(captured.PromptRebuild);
            Assert.AreEqual("deferred", captured.PromptRebuildStatus);
            Assert.AreEqual(true, captured.ActionConfigUpdated);
            Assert.AreEqual(false, captured.ActionConfigCreated);
            Assert.AreEqual(2, captured.ActionsCount);
            Assert.AreEqual(1, captured.ObjectsCount);
            Assert.AreEqual(1, captured.CharactersCount);
            Assert.AreEqual("Lever", captured.CurrentAttentionObject);
            Assert.AreEqual(false, captured.CurrentAttentionObjectCleared);
            Assert.AreEqual(true, captured.ActionGenerationStrategyChanged);
            Assert.AreEqual("applied", captured.ActionGenerationStrategyStatus);
            Assert.NotNull(captured.RawExtras);
        }

        [Test]
        public void ServerMessage_ServerResponseContextUpdate_ToleratesMixedTypedResultFields()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            DynamicContextUpdateResultReceived captured = default;
            eventHub.Subscribe<DynamicContextUpdateResultReceived>(e => captured = e);

            Assert.DoesNotThrow(() => gateway.ProcessIncoming(CreateServerMessagePacket(
                "server-response",
                new JObject
                {
                    ["event_type"] = "context-update",
                    ["status"] = "success",
                    ["message"] = "Context updated",
                    ["update_id"] = "ctx-2",
                    ["context_revision"] = "8",
                    ["token_count"] = "121",
                    ["static_token_count"] = 31,
                    ["runtime_token_count"] = "90",
                    ["remaining_tokens"] = 29879,
                    ["requested_run_llm"] = true,
                    ["actual_run_llm"] = "auto",
                    ["downgrade_reason"] = null,
                    ["interrupted"] = "false",
                    ["llm_triggered"] = "auto",
                    ["prompt_rebuild"] = 1
                })));

            Assert.AreEqual("ctx-2", captured.UpdateId);
            Assert.AreEqual(8, captured.ContextRevision);
            Assert.AreEqual(121, captured.TokenCount);
            Assert.AreEqual(31, captured.StaticTokenCount);
            Assert.AreEqual(90, captured.RuntimeTokenCount);
            Assert.AreEqual(29879, captured.RemainingTokens);
            Assert.AreEqual("true", captured.RequestedRunLlm);
            Assert.AreEqual("auto", captured.ActualRunLlm);
            Assert.IsFalse(captured.Interrupted);
            Assert.IsFalse(captured.LlmTriggered);
            Assert.IsTrue(captured.PromptRebuild);
            Assert.AreEqual("1", captured.PromptRebuildStatus);
        }

        [Test]
        public void ServerMessage_ServerResponseVisionStatus_Publishes_VisionStatusResult()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            VisionContextStatusReceived captured = default;
            eventHub.Subscribe<VisionContextStatusReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "server-response",
                new JObject
                {
                    ["event_type"] = "vision-status",
                    ["status"] = "success",
                    ["message"] = "Vision buffer ready",
                    ["extras"] = new JObject
                    {
                        ["update_id"] = "vision-status-1",
                        ["vision_status_outcome"] = "ok",
                        ["active_source"] = "camera",
                        ["active_source_label"] = "unity-scene",
                        ["last_frame_age_ms"] = 120,
                        ["vision_buffer"] = new JObject
                        {
                            ["frame_count"] = 5
                        }
                    }
                }));

            Assert.AreEqual("success", captured.Status);
            Assert.AreEqual("Vision buffer ready", captured.Message);
            Assert.AreEqual("vision-status-1", captured.UpdateId);
            Assert.AreEqual("ok", captured.Outcome);
            Assert.AreEqual("camera", captured.ActiveSource);
            Assert.AreEqual("unity-scene", captured.ActiveSourceLabel);
            Assert.AreEqual(120, captured.LastFrameAgeMs);
            Assert.AreEqual(5, captured.RawExtras?["vision_buffer"]?["frame_count"]?.Value<int>());
        }

        [Test]
        public void ServerMessage_ServerResponseVisionTrigger_Publishes_VisionTriggerResult()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            VisionContextTriggerReceived captured = default;
            eventHub.Subscribe<VisionContextTriggerReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "server-response",
                new JObject
                {
                    ["event_type"] = "vision-trigger",
                    ["status"] = "success",
                    ["message"] = "Vision attached",
                    ["extras"] = new JObject
                    {
                        ["update_id"] = "vision-trigger-1",
                        ["vision_trigger_outcome"] = "attached",
                        ["requested_respond_mode"] = "must_respond",
                        ["actual_respond_mode"] = "must_respond",
                        ["requested_run_llm"] = true,
                        ["actual_run_llm"] = true,
                        ["llm_triggered"] = true,
                        ["downgraded"] = false,
                        ["vision_frames_attached"] = 5,
                        ["vision_attach_outcome"] = "attached",
                        ["vision_image_tokens_est"] = 1500,
                        ["attached_frame_pts"] = new JArray(10, 20)
                    }
                }));

            Assert.AreEqual("success", captured.Status);
            Assert.AreEqual("Vision attached", captured.Message);
            Assert.AreEqual("vision-trigger-1", captured.UpdateId);
            Assert.AreEqual("attached", captured.Outcome);
            Assert.AreEqual("must_respond", captured.RequestedRespondMode);
            Assert.AreEqual("must_respond", captured.ActualRespondMode);
            Assert.IsTrue(captured.LlmTriggered);
            Assert.IsFalse(captured.Downgraded);
            Assert.AreEqual(5, captured.FramesAttached);
            Assert.AreEqual("attached", captured.AttachOutcome);
            Assert.AreEqual(1500, captured.ImageTokensEstimate);
            CollectionAssert.AreEqual(new long[] { 10, 20 }, captured.AttachedFramePts);
        }

        [Test]
        public void ServerMessage_ServerResponseVisionTrigger_ErrorAck_StillPublishesWithSafeDefaults()
        {
            // Frame-binding failures come back as status=error with sparse extras; the event must
            // surface the rejection without throwing on the absent typed fields.
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            VisionContextTriggerReceived captured = default;
            eventHub.Subscribe<VisionContextTriggerReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "server-response",
                new JObject
                {
                    ["event_type"] = "vision-trigger",
                    ["status"] = "error",
                    ["message"] = "Invalid frame indices",
                    ["extras"] = new JObject
                    {
                        ["update_id"] = "vision-trigger-err",
                        ["vision_trigger_outcome"] = "invalid_frame_indices",
                        ["downgraded"] = true,
                        ["downgrade_reason"] = "invalid_frame_indices"
                    }
                }));

            Assert.AreEqual("error", captured.Status);
            Assert.AreEqual("vision-trigger-err", captured.UpdateId);
            Assert.AreEqual("invalid_frame_indices", captured.Outcome);
            Assert.IsTrue(captured.Downgraded);
            Assert.AreEqual("invalid_frame_indices", captured.DowngradeReason);
            Assert.IsFalse(captured.LlmTriggered);
            Assert.AreEqual(0, captured.FramesAttached);
            Assert.AreEqual(0, captured.ImageTokensEstimate);
            Assert.IsNotNull(captured.AttachedFramePts);
            Assert.AreEqual(0, captured.AttachedFramePts.Count);
        }

        [Test]
        public void ServerMessage_ServerResponseRespondModeUpdate_Publishes_RespondModeResult()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            RespondModeUpdateResultReceived captured = default;
            eventHub.Subscribe<RespondModeUpdateResultReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "server-response",
                new JObject
                {
                    ["event_type"] = "respond-mode-update",
                    ["status"] = "success",
                    ["extras"] = new JObject
                    {
                        ["modality"] = "vision",
                        ["mode"] = "auto",
                        ["respond_modes"] = new JObject { ["vision"] = "auto", ["trigger"] = "must_respond" }
                    }
                }));

            Assert.AreEqual("success", captured.Status);
            Assert.AreEqual("vision", captured.Modality);
            Assert.AreEqual("auto", captured.Mode);
            Assert.AreEqual("auto", captured.RawExtras?["respond_modes"]?["vision"]?.ToString());
        }

        [Test]
        public void ServerMessage_ServerResponse_EventTypeMatchingIsCaseInsensitive()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            VisionContextStatusReceived captured = default;
            eventHub.Subscribe<VisionContextStatusReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "server-response",
                new JObject
                {
                    ["event_type"] = "Vision-Status",
                    ["status"] = "success",
                    ["extras"] = new JObject { ["update_id"] = "vision-status-cased" }
                }));

            Assert.AreEqual("vision-status-cased", captured.UpdateId,
                "event_type matching must tolerate casing variants like the pre-vision handler did.");
        }

        [Test]
        public void ServerMessage_LlmNoResponse_Publishes_Character_Context()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            LlmNoResponseReceived captured = default;
            eventHub.Subscribe<LlmNoResponseReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "llm-no-response",
                new JObject
                {
                    ["reason"] = "abstain"
                },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual("abstain", captured.Reason);
        }

        [Test]
        public void ServerMessage_InteractionCreated_Publishes_Interaction_Context()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            InteractionCreated captured = default;
            eventHub.Subscribe<InteractionCreated>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "interaction-created",
                new JObject
                {
                    ["interaction_id"] = "a4fce023-d850-4210-9d10-8c98228d1b4b",
                    ["character_session_id"] = "0ded6a03-aeec-4c8b-a64b-0ee910695203"
                },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual("a4fce023-d850-4210-9d10-8c98228d1b4b", captured.InteractionId);
            Assert.AreEqual("0ded6a03-aeec-4c8b-a64b-0ee910695203", captured.CharacterSessionId);
        }

        [Test]
        public void ServerMessage_FinalUserTranscription_Publishes_Dedicated_Event()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            FinalUserTranscriptionReceived captured = default;
            eventHub.Subscribe<FinalUserTranscriptionReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject
                {
                    ["text"] = "Hello there",
                    ["message_id"] = "typed-1",
                    ["speaker_id"] = "speaker-1",
                    ["speaker_name"] = "Rishav",
                    ["participant_id"] = "PA_1"
                }));

            Assert.AreEqual("Hello there", captured.Text);
            Assert.AreEqual("typed-1", captured.MessageId);
            Assert.AreEqual("speaker-1", captured.SpeakerId);
            Assert.AreEqual("Rishav", captured.SpeakerName);
            Assert.AreEqual("PA_1", captured.ParticipantId);
        }

        [Test]
        public void TypedTextEcho_UserTranscriptionCycle_Does_Not_Reach_PlayerConversationInput()
        {
            EventHub eventHub = CreateEventHub();
            RecordingPlayerSession playerSession = new();
            RTVIHandler handler = CreateHandler(eventHub, playerSession, out ProtocolGateway gateway, out _);

            handler.SendData(new RTVIUserTextMessage("hello", "typed-1"));

            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            gateway.ProcessIncoming(CreateUserTranscriptionPacket("hello", isFinal: true));
            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.IsEmpty(playerSession.StartedSessionIds);
            Assert.IsEmpty(playerSession.StoppedSessions);
            Assert.IsEmpty(playerSession.Transcriptions);
        }

        [Test]
        public void TypedTextEcho_FinalUserTranscriptionWithoutMessageId_Updates_Typed_Row_By_Pending_Id()
        {
            EventHub eventHub = CreateEventHub();
            RecordingPlayerSession playerSession = new();
            RTVIHandler handler = CreateHandler(eventHub, playerSession, out ProtocolGateway gateway, out _);
            FinalUserTranscriptionReceived captured = default;
            eventHub.Subscribe<FinalUserTranscriptionReceived>(e => captured = e);

            handler.SendData(new RTVIUserTextMessage("hello how you doing", "typed-1"));

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "final-user-transcription",
                new JObject
                {
                    ["text"] = "hello how you doing"
                }));

            Assert.AreEqual("typed-1", captured.MessageId);
            Assert.IsEmpty(playerSession.Transcriptions);
            Assert.AreEqual(1, playerSession.TypedTranscriptions.Count);
            Assert.AreEqual("typed-1", playerSession.TypedTranscriptions[0].MessageId);
            Assert.AreEqual("hello how you doing", playerSession.TypedTranscriptions[0].Text);
        }

        [Test]
        public void TypedTextEcho_Suppression_Clears_And_Later_RealSpeech_Still_Uses_Normal_Path()
        {
            EventHub eventHub = CreateEventHub();
            RecordingPlayerSession playerSession = new();
            RTVIHandler handler = CreateHandler(eventHub, playerSession, out ProtocolGateway gateway, out _);

            handler.SendData(new RTVIUserTextMessage("hello", "typed-1"));

            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            gateway.ProcessIncoming(CreateUserTranscriptionPacket("hello", isFinal: true));
            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            gateway.ProcessIncoming(CreateUserTranscriptionPacket("real speech", isFinal: false));
            gateway.ProcessIncoming(CreateUserTranscriptionPacket("real speech done", isFinal: true));
            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.That(
                SpinWait.SpinUntil(() => playerSession.StoppedSessions.Count == 1, TimeSpan.FromSeconds(1)),
                Is.True,
                "The real speech session did not complete after the ASR-final grace window.");

            Assert.AreEqual(1, playerSession.StartedSessionIds.Count);
            Assert.AreEqual(1, playerSession.StoppedSessions.Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    TranscriptionPhase.Listening,
                    TranscriptionPhase.Interim,
                    TranscriptionPhase.AsrFinal,
                    TranscriptionPhase.Completed
                },
                playerSession.Transcriptions.Select(entry => entry.Phase).ToArray());
            Assert.AreEqual("real speech", playerSession.Transcriptions[1].Text);
            Assert.AreEqual("real speech done", playerSession.Transcriptions[2].Text);
        }

        [Test]
        public void TypedTextEcho_MismatchedSpeechInsideSuppressionWindow_Starts_Normal_Path()
        {
            EventHub eventHub = CreateEventHub();
            RecordingPlayerSession playerSession = new();
            RTVIHandler handler = CreateHandler(eventHub, playerSession, out ProtocolGateway gateway, out _);
            int startedEvents = 0;
            eventHub.Subscribe<PlayerSpeakingStateChanged>(e =>
            {
                if (e.IsSpeaking) startedEvents++;
            });

            handler.SendData(new RTVIUserTextMessage("typed text", "typed-1"));

            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            gateway.ProcessIncoming(CreateUserTranscriptionPacket("real speech", isFinal: false));
            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));

            Assert.AreEqual(1, startedEvents);
            Assert.AreEqual(1, playerSession.StartedSessionIds.Count);
            Assert.AreEqual(1, playerSession.StoppedSessions.Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    TranscriptionPhase.Listening,
                    TranscriptionPhase.Interim
                },
                playerSession.Transcriptions.Select(entry => entry.Phase).ToArray());
            Assert.AreEqual("real speech", playerSession.Transcriptions[1].Text);
        }

        [Test]
        public void TypedTextEcho_BotTranscriptAndTtsEcho_Do_Not_Publish_Character_Events()
        {
            EventHub eventHub = CreateEventHub();
            RTVIHandler handler = CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            int characterTranscriptCount = 0;
            int ttsChunkCount = 0;
            eventHub.Subscribe<CharacterTranscriptReceived>(_ => characterTranscriptCount++);
            eventHub.Subscribe<CharacterTtsTextChunk>(_ => ttsChunkCount++);

            handler.SendData(new RTVIUserTextMessage("hello", "typed-1"));

            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-started-speaking"));
            gateway.ProcessIncoming(CreateUserTranscriptionPacket("hello", isFinal: true));
            gateway.ProcessIncoming(CreateSimpleInboundPacket("user-stopped-speaking"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-llm-text", "Hello.", "participant-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-output", "Hello.", "participant-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-transcription", "Hello.", "participant-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-tts-text", "Hello.", "participant-1"));

            Assert.AreEqual(0, characterTranscriptCount);
            Assert.AreEqual(0, ttsChunkCount);
        }

        [Test]
        public void TypedTextEcho_BotDifferentText_Still_Publishes_Character_Events()
        {
            EventHub eventHub = CreateEventHub();
            RTVIHandler handler = CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            var characterTexts = new List<string>();
            var ttsTexts = new List<string>();
            eventHub.Subscribe<CharacterTranscriptReceived>(e => characterTexts.Add(e.Text));
            eventHub.Subscribe<CharacterTtsTextChunk>(e => ttsTexts.Add(e.Text));

            handler.SendData(new RTVIUserTextMessage("hello", "typed-1"));

            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-llm-text", "Hi there.", "participant-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-output", "Hi there.", "participant-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-transcription", "Hi there.", "participant-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket("bot-tts-text", "Hi there.", "participant-1"));

            CollectionAssert.AreEqual(new[] { "Hi there.", "Hi there.", "Hi there." }, characterTexts);
            CollectionAssert.AreEqual(new[] { "Hi there." }, ttsTexts);
        }

        [Test]
        public void BotTranscription_ResponseId_Is_Preserved_On_CharacterTranscript()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            CharacterTranscriptReceived captured = default;
            eventHub.Subscribe<CharacterTranscriptReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription",
                "Hi there.",
                "participant-1",
                responseId: "response-1"));

            Assert.AreEqual("response-1", captured.ResponseId);
            Assert.AreEqual("response-1", captured.MessageId);
            Assert.AreEqual("response-1", captured.TurnId);
            Assert.AreEqual("Hi there.", captured.Text);
        }

        [Test]
        public void BotOutput_Preserves_Standard_Metadata_And_PacketIdentity()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            CharacterTranscriptReceived captured = default;
            eventHub.Subscribe<CharacterTranscriptReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-output",
                "Normalized answer.",
                "participant-1",
                responseId: "response-1",
                turnId: "turn-1",
                spoken: false,
                aggregatedBy: "sentence",
                packetId: "packet-1"));

            Assert.AreEqual(TranscriptSegmentSourceKind.BotOutput, captured.SourceKind);
            Assert.AreEqual(TranscriptLifecycle.Stable, captured.Lifecycle);
            Assert.AreEqual("turn-1", captured.TurnId);
            Assert.AreEqual("packet-1", captured.UpdateId);
            Assert.IsFalse(captured.IsSpoken);
            Assert.AreEqual("sentence", captured.AggregatedBy);
            Assert.IsNull(captured.OutputSegmentId);
            Assert.IsNull(captured.SpokenProgress);
        }

        [Test]
        public void BotOutput_WordProgress_CarriesSegmentAndSplit_WithoutChangingTranscriptSemantics()
        {
            CharacterTranscriptReceived captured = CaptureBotOutput(new JObject
            {
                ["text"] = "Your balance is $42.50",
                ["aggregated_by"] = "sentence",
                ["segment_id"] = 2340,
                ["will_be_spoken"] = true,
                ["spoken_status"] = "in-progress",
                ["spoken_progress"] = new JObject
                {
                    ["accumulated_text"] = "Your balance",
                    ["remaining_text"] = " is $42.50"
                }
            });

            Assert.AreEqual(2340L, captured.OutputSegmentId);
            Assert.IsTrue(captured.SpokenProgress.HasValue);
            TranscriptSpokenProgress progress = captured.SpokenProgress.Value;
            Assert.AreEqual(TranscriptSpokenStatus.InProgress, progress.Status);
            Assert.AreEqual("Your balance", progress.SpokenText);
            Assert.AreEqual(" is $42.50", progress.RemainingText);

            Assert.AreEqual("Your balance is $42.50", captured.Text);
            Assert.AreEqual(TranscriptSegmentSourceKind.BotOutput, captured.SourceKind);
            Assert.AreEqual(TranscriptLifecycle.Stable, captured.Lifecycle);
            Assert.IsTrue(captured.IsSpoken);
            Assert.AreEqual("sentence", captured.AggregatedBy);
        }

        [TestCase("new", TranscriptSpokenStatus.New)]
        [TestCase("in-progress", TranscriptSpokenStatus.InProgress)]
        [TestCase("completed", TranscriptSpokenStatus.Completed)]
        [TestCase("paused", TranscriptSpokenStatus.Unknown)]
        public void BotOutput_SpokenStatus_MapsWireValue(string wireStatus, TranscriptSpokenStatus expected)
        {
            CharacterTranscriptReceived captured = CaptureBotOutput(new JObject
            {
                ["text"] = "Hello there.",
                ["segment_id"] = 7,
                ["spoken_status"] = wireStatus,
                ["spoken_progress"] = new JObject
                {
                    ["accumulated_text"] = "Hello",
                    ["remaining_text"] = " there."
                }
            });

            Assert.AreEqual(expected, captured.SpokenProgress.Value.Status);
            Assert.AreEqual("Hello", captured.SpokenProgress.Value.SpokenText);
            Assert.AreEqual(" there.", captured.SpokenProgress.Value.RemainingText);
        }

        [Test]
        public void BotOutput_StatusWithoutSplit_ReportsEmptyTexts()
        {
            CharacterTranscriptReceived captured = CaptureBotOutput(new JObject
            {
                ["text"] = "Hello there.",
                ["spoken_status"] = "completed"
            });

            Assert.AreEqual(TranscriptSpokenStatus.Completed, captured.SpokenProgress.Value.Status);
            Assert.AreEqual(string.Empty, captured.SpokenProgress.Value.SpokenText);
            Assert.AreEqual(string.Empty, captured.SpokenProgress.Value.RemainingText);
        }

        [Test]
        public void BotOutput_NewSegment_IsNotSpokenAndHasWholeTextRemaining()
        {
            CharacterTranscriptReceived captured = CaptureBotOutput(new JObject
            {
                ["text"] = "Hello there.",
                ["aggregated_by"] = "sentence",
                ["segment_id"] = 11,
                ["spoken"] = false,
                ["will_be_spoken"] = true,
                ["spoken_status"] = "new",
                ["spoken_progress"] = new JObject
                {
                    ["accumulated_text"] = "",
                    ["remaining_text"] = "Hello there."
                }
            });

            Assert.AreEqual(11L, captured.OutputSegmentId);
            Assert.AreEqual(TranscriptSpokenStatus.New, captured.SpokenProgress.Value.Status);
            Assert.AreEqual(string.Empty, captured.SpokenProgress.Value.SpokenText);
            Assert.AreEqual("Hello there.", captured.SpokenProgress.Value.RemainingText);
            Assert.AreEqual("Hello there.", captured.Text);
            Assert.IsFalse(captured.IsSpoken);
        }

        [Test]
        public void BotOutput_SplitWithoutStatus_ReportsUnknownStatus()
        {
            CharacterTranscriptReceived captured = CaptureBotOutput(new JObject
            {
                ["text"] = "Hello there.",
                ["segment_id"] = 7,
                ["spoken_progress"] = new JObject
                {
                    ["accumulated_text"] = "Hello",
                    ["remaining_text"] = " there."
                }
            });

            Assert.AreEqual(TranscriptSpokenStatus.Unknown, captured.SpokenProgress.Value.Status);
            Assert.AreEqual("Hello", captured.SpokenProgress.Value.SpokenText);
            Assert.AreEqual(" there.", captured.SpokenProgress.Value.RemainingText);
        }

        [Test]
        public void BotOutput_SegmentThatWillNotBeSpoken_HasIdButNoProgress()
        {
            CharacterTranscriptReceived captured = CaptureBotOutput(new JObject
            {
                ["text"] = "Shown, not spoken.",
                ["aggregated_by"] = "sentence",
                ["segment_id"] = 9,
                ["spoken"] = false,
                ["will_be_spoken"] = false
            });

            Assert.AreEqual(9L, captured.OutputSegmentId);
            Assert.IsNull(captured.SpokenProgress);
            Assert.IsFalse(captured.IsSpoken);
        }

        private static CharacterTranscriptReceived CaptureBotOutput(JObject data)
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            CharacterTranscriptReceived captured = default;
            int published = 0;
            eventHub.Subscribe<CharacterTranscriptReceived>(e =>
            {
                captured = e;
                published++;
            });

            JObject envelope = new()
            {
                ["label"] = "rtvi-ai",
                ["type"] = "bot-output",
                ["data"] = data
            };
            gateway.ProcessIncoming(new ProtocolPacket(
                Encoding.UTF8.GetBytes(envelope.ToString()),
                "participant-1",
                "rtvi-ai",
                true));

            Assert.AreEqual(1, published);
            return captured;
        }

        [Test]
        public void CharacterTranscripts_MissingThenKnownParticipant_KeepOneResponseIdentity()
        {
            EventHub eventHub = CreateEventHub();
            using var transcriptEngine = new RoomTranscriptEngine(eventHub);
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));

            var captured = new List<CharacterTranscriptReceived>();
            eventHub.Subscribe<CharacterTranscriptReceived>(captured.Add);

            gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription",
                "It is great to see you.",
                string.Empty,
                packetId: "packet-1"));
            registry.SetParticipantId("char-1", "participant-1");
            gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-output",
                "It is great to see you.",
                "participant-1",
                packetId: "packet-2"));

            Assert.AreEqual(2, captured.Count);
            Assert.AreEqual(string.Empty, captured[0].Message.ParticipantId);
            Assert.AreEqual("participant-1", captured[1].Message.ParticipantId);
            Assert.AreEqual(captured[0].TurnId, captured[1].TurnId);
            Assert.AreEqual(captured[0].ResponseId, captured[1].ResponseId);
            Assert.AreEqual(1, transcriptEngine.CurrentTimeline.ActiveTurns.Count);
            Assert.AreEqual(
                "It is great to see you.",
                transcriptEngine.CurrentTimeline.ActiveTurns.Single().DisplayText);
        }

        [TestCase(null)]
        [TestCase("session-1:r2")]
        public void StableTranscriptAfterSpeechStart_StaysInOneTurn(string responseId)
        {
            EventHub eventHub = CreateEventHub();
            using var transcriptEngine = new RoomTranscriptEngine(eventHub);
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            ProcessStableTranscriptsAroundSpeechStart(gateway, responseId);

            TranscriptTurnSnapshot[] characterTurns = CharacterTurns(transcriptEngine);
            Assert.AreEqual(1, characterTurns.Length);
            StringAssert.Contains("First sentence.", characterTurns[0].DisplayText);
            StringAssert.Contains("Second sentence.", characterTurns[0].DisplayText);
            StringAssert.Contains("Third sentence.", characterTurns[0].DisplayText);
        }

        [TestCase(null)]
        [TestCase("session-1:r2")]
        public void StableTranscriptAfterSpeechStart_TurnCarriesServerResponseId(string responseId)
        {
            EventHub eventHub = CreateEventHub();
            using var transcriptEngine = new RoomTranscriptEngine(eventHub);
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            var captured = new List<CharacterTranscriptReceived>();
            eventHub.Subscribe<CharacterTranscriptReceived>(captured.Add);

            ProcessStableTranscriptsAroundSpeechStart(gateway, responseId);

            Assert.AreEqual(3, captured.Count);
            StringAssert.StartsWith("participant-1:character-response-", captured[0].ResponseId);
            Assert.AreEqual("session-1:r2", captured[2].ResponseId);
            Assert.AreEqual(captured[0].TurnId, captured[2].TurnId);
            Assert.AreEqual(
                "session-1:r2",
                CharacterTurns(transcriptEngine).Single().ResponseId);
        }

        private static TranscriptTurnSnapshot[] CharacterTurns(RoomTranscriptEngine engine) =>
            engine.CurrentTimeline.ActiveTurns
                .Concat(engine.CurrentTimeline.CommittedTurns)
                .Where(turn => turn.Participant.Kind == TranscriptParticipantKind.Character)
                .ToArray();

        private static void ProcessStableTranscriptsAroundSpeechStart(ProtocolGateway gateway, string responseId)
        {
            gateway.ProcessIncoming(CreateInboundPacket("bot-llm-started", "participant-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription", "First sentence.", "participant-1", packetId: "packet-1"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription", "Second sentence.", "participant-1", packetId: "packet-2"));
            gateway.ProcessIncoming(CreateBotSpeechPacket(
                "bot-started-speaking", "participant-1", "session-1:r2"));
            gateway.ProcessIncoming(CreateBotTranscriptionPacket(
                "bot-transcription", "Third sentence.", "participant-1", responseId: responseId, packetId: "packet-3"));
        }

        [Test]
        public void ServerMessage_VadSttStarted_Publishes_Event()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            VadSttStateChanged captured = default;
            eventHub.Subscribe<VadSttStateChanged>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket("vad-stt-started", new JObject()));

            Assert.IsTrue(captured.IsActive);
        }

        [Test]
        public void ServerMessage_VadSttStopped_Publishes_Event()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out _);
            VadSttStateChanged captured = default;
            eventHub.Subscribe<VadSttStateChanged>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket("vad-stt-stopped", new JObject()));

            Assert.IsFalse(captured.IsActive);
        }

        [Test]
        public void ServerMessage_Visemes_Publishes_Raw_Event()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            VisemesReceived captured = default;
            eventHub.Subscribe<VisemesReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "visemes",
                new JObject
                {
                    ["visemes"] = new JObject
                    {
                        ["pp"] = 0.8f,
                        ["aa"] = 0.2f
                    }
                },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual(0.8f, captured.Visemes["pp"]);
            Assert.AreEqual(0.2f, captured.Visemes["aa"]);
        }

        [Test]
        public void ServerMessage_BlendshapeTurnStats_Publishes_Event_With_AudioDuration()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            BlendshapeTurnStatsReceived captured = default;
            eventHub.Subscribe<BlendshapeTurnStatsReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "blendshape-turn-stats",
                new JObject
                {
                    ["stats"] = new JObject
                    {
                        ["total_blendshapes"] = 150,
                        ["total_audio_bytes"] = 48000,
                        ["total_turn_duration_ms"] = 3000.0,
                        ["total_audio_duration_ms"] = 2800.0,
                        ["fps"] = 50.0
                    }
                },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual(150, captured.TotalBlendshapes);
            Assert.AreEqual(2800d, captured.TotalAudioDurationMs);
            Assert.IsFalse(captured.FrameCountMatches);
        }

        [Test]
        public void ServerMessage_BlendshapeTurnStats_DoesNotPublishSyntheticSpeechStop()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");
            int speechStopCount = 0;
            eventHub.Subscribe<CharacterSpeechStateChanged>(e =>
            {
                if (!e.IsSpeaking) speechStopCount++;
            });

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "blendshape-turn-stats",
                new JObject
                {
                    ["stats"] = new JObject
                    {
                        ["total_blendshapes"] = 150,
                        ["total_audio_bytes"] = 48000,
                        ["total_turn_duration_ms"] = 3000.0,
                        ["total_audio_duration_ms"] = 2800.0,
                        ["fps"] = 50.0
                    }
                },
                "participant-1"));

            Assert.AreEqual(0, speechStopCount);
        }

        [Test]
        public void BotStartedSpeaking_PreservesResponseOwnerInSpeechEvent()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            CharacterSpeechStateChanged captured = default;
            eventHub.Subscribe<CharacterSpeechStateChanged>(e => captured = e);

            gateway.ProcessIncoming(CreateBotSpeechPacket(
                "bot-started-speaking", "participant-1", "response-42"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.IsTrue(captured.IsSpeaking);
            Assert.AreEqual("response-42", captured.UtteranceId);
        }

        [Test]
        public void BotSpeechLifecycle_PreservesCanonicalOwnerAndPublicProjection()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            LipSyncResponseLifecycleChanged lifecycle = default;
            CharacterSpeechStateChanged publicProjection = default;
            eventHub.Subscribe<LipSyncResponseLifecycleChanged>(e => lifecycle = e);
            eventHub.Subscribe<CharacterSpeechStateChanged>(e => publicProjection = e);

            gateway.ProcessIncoming(CreateBotSpeechPacket(
                "bot-started-speaking",
                "participant-1",
                "response-42",
                7,
                3,
                19));

            Assert.AreEqual("char-1", lifecycle.CharacterId);
            Assert.AreEqual("participant-1", lifecycle.ParticipantId);
            Assert.IsTrue(lifecycle.IsSpeaking);
            Assert.AreEqual("response-42", lifecycle.Owner.ResponseId);
            Assert.AreEqual(7, lifecycle.Owner.TurnId);
            Assert.AreEqual(3, lifecycle.Owner.Epoch);
            Assert.AreEqual(19, lifecycle.Owner.Sequence);
            Assert.AreEqual("response:response-42", lifecycle.Owner.CanonicalKey);
            Assert.AreEqual(lifecycle.Timestamp, publicProjection.Timestamp);
            Assert.AreEqual("response-42", publicProjection.UtteranceId);
        }

        [Test]
        public void LipSyncResponseOwner_UsesStrictIdentityPrecedence()
        {
            var full = new LipSyncResponseOwner("response-1", 4, 2, 10);
            var matchingResponse = new LipSyncResponseOwner("response-1", 99, 7, 11);
            var matchingTurnEpoch = new LipSyncResponseOwner(null, 4, 2, 12);
            var turnOnly = new LipSyncResponseOwner(null, 4, null, 13);
            var conflictingResponse = new LipSyncResponseOwner("response-2", 4, 2, 14);

            Assert.IsTrue(full.Matches(matchingResponse));
            Assert.IsTrue(full.Matches(matchingTurnEpoch));
            Assert.IsFalse(full.Matches(turnOnly));
            Assert.IsFalse(full.Matches(conflictingResponse));
            Assert.IsTrue(turnOnly.Matches(new LipSyncResponseOwner(null, 4, null)));
            Assert.IsFalse(turnOnly.Matches(new LipSyncResponseOwner(null, 4, 2)));
        }

        [Test]
        public void LipSyncResponseOwner_DefaultValue_IsSafeAndEmpty()
        {
            LipSyncResponseOwner owner = default;

            Assert.DoesNotThrow(() => _ = owner.HasIdentity);
            Assert.IsFalse(owner.HasIdentity);
            Assert.AreEqual(string.Empty, owner.ResponseId);
            Assert.AreEqual(string.Empty, owner.CanonicalKey);
            Assert.IsFalse(owner.Matches(new LipSyncResponseOwner("response-1", 1, 1)));
        }

        [Test]
        public void BotSpeechLifecycle_WithScalarPayload_PublishesMetadataFreeLifecycle()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");
            var lifecycle = new List<LipSyncResponseLifecycleChanged>();
            eventHub.Subscribe<LipSyncResponseLifecycleChanged>(lifecycle.Add);

            gateway.ProcessIncoming(CreateBotSpeechScalarPacket(
                "bot-started-speaking", "participant-1", "participant-1"));
            gateway.ProcessIncoming(CreateBotSpeechScalarPacket(
                "bot-stopped-speaking", "participant-1", false));

            Assert.AreEqual(2, lifecycle.Count);
            Assert.IsTrue(lifecycle[0].IsSpeaking);
            Assert.IsFalse(lifecycle[1].IsSpeaking);
            Assert.IsFalse(lifecycle[0].Owner.HasIdentity);
            Assert.IsFalse(lifecycle[1].Owner.HasIdentity);
        }

        [Test]
        public void BlendshapeFrameStatsTracker_TenThousandCompletedResponses_RemainsBounded()
        {
            var tracker = new BlendshapeFrameStatsTracker(8);

            for (int i = 0; i < 10_000; i++)
            {
                var owner = new LipSyncResponseOwner($"response-{i}", i, 1);
                tracker.Add(owner, 60);
                Assert.IsTrue(tracker.TryTake(owner, out int frames));
                Assert.AreEqual(60, frames);
                Assert.LessOrEqual(tracker.Count, 8);
                Assert.AreEqual(tracker.Count, tracker.OrderCount);
            }

            Assert.AreEqual(0, tracker.Count);
            Assert.AreEqual(0, tracker.OrderCount);

            for (int i = 0; i < 10_000; i++)
                tracker.Add(new LipSyncResponseOwner($"missing-stats-{i}", i, 1), 60);

            Assert.AreEqual(8, tracker.Count);
            Assert.AreEqual(8, tracker.OrderCount);
        }

        [Test]
        public void ServerMessage_BlendshapeTurnStats_PublishesTopLevelOwnerFields()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            BlendshapeTurnStatsReceived captured = default;
            eventHub.Subscribe<BlendshapeTurnStatsReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "blendshape-turn-stats",
                new JObject
                {
                    ["response_id"] = "session:r4",
                    ["neurosync_turn_id"] = 4,
                    ["epoch"] = 2,
                    ["sequence"] = 21,
                    ["stats"] = new JObject
                    {
                        ["total_blendshapes"] = 150,
                        ["total_audio_bytes"] = 48000,
                        ["total_turn_duration_ms"] = 3000.0,
                        ["total_audio_duration_ms"] = 2800.0,
                        ["fps"] = 50.0
                    }
                },
                "participant-1"));

            Assert.AreEqual("session:r4", captured.ResponseId);
            Assert.AreEqual(4, captured.NeuroSyncTurnId);
            Assert.AreEqual(2, captured.Epoch);
            Assert.AreEqual(21, captured.Sequence);
            Assert.IsTrue(captured.HasOwnerMetadata);
        }

        [Test]
        public void ServerMessage_NeurosyncAudioTimelineAnchor_PublishesEvent()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            LipSyncAudioTimelineAnchorReceived captured = default;
            eventHub.Subscribe<LipSyncAudioTimelineAnchorReceived>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "neurosync-audio-timeline-anchor",
                new JObject
                {
                    ["response_id"] = "session:r4",
                    ["neurosync_turn_id"] = 4,
                    ["epoch"] = 1,
                    ["sequence"] = 13,
                    ["audio_start_ms"] = 300.0,
                    ["audio_duration_ms"] = 300.0,
                    ["sample_rate"] = 44100,
                    ["channels"] = 1
                },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("session:r4", captured.ResponseId);
            Assert.AreEqual(4, captured.NeuroSyncTurnId);
            Assert.AreEqual(1, captured.Epoch);
            Assert.AreEqual(13, captured.Sequence);
            Assert.AreEqual(300.0, captured.AudioStartMs, 0.001);
            Assert.AreEqual(300.0, captured.AudioDurationMs, 0.001);
            Assert.AreEqual(44100, captured.SampleRate);
            Assert.AreEqual(1, captured.Channels);
            Assert.IsTrue(captured.IsValid);
        }

        [Test]
        public void ServerMessage_NeurosyncAudioTimelineAnchor_ConvertsSampleIndexesWithoutPublicApiChange()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            LipSyncAudioTimelineAnchorReceived captured = default;
            AudioTimelineSampleAnchor sampleAnchor = default;
            eventHub.Subscribe<LipSyncAudioTimelineAnchorReceived>(e => captured = e);
            eventHub.Subscribe<AudioTimelineSampleAnchor>(e => sampleAnchor = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "neurosync-audio-timeline-anchor",
                new JObject
                {
                    ["response_id"] = "session:r5",
                    ["audio_start_sample"] = 24000,
                    ["final_audio_sample"] = 72000,
                    ["sample_rate"] = 48000,
                    ["channels"] = 1
                },
                "participant-1"));

            Assert.AreEqual(500d, captured.AudioStartMs, 0.001d);
            Assert.AreEqual(1000d, captured.AudioDurationMs, 0.001d);
            Assert.AreEqual(48000, captured.SampleRate);
            Assert.AreEqual(24000, sampleAnchor.ResponseAudioStartSample);
            Assert.AreEqual(72000, sampleAnchor.FinalAudioSample);
            Assert.AreEqual(48000, sampleAnchor.SampleRate);
        }

        [TestCase(null, 0.0, Description = "Missing response_id")]
        [TestCase("session:r4", -5.0, Description = "Negative audio_start_ms")]
        public void ServerMessage_NeurosyncAudioTimelineAnchor_InvalidPayload_IsDropped(
            string responseId, double audioStartMs)
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            int received = 0;
            eventHub.Subscribe<LipSyncAudioTimelineAnchorReceived>(_ => received++);

            var payload = new JObject
            {
                ["neurosync_turn_id"] = 4,
                ["audio_start_ms"] = audioStartMs,
                ["audio_duration_ms"] = 300.0
            };
            if (responseId != null) payload["response_id"] = responseId;

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "neurosync-audio-timeline-anchor", payload, "participant-1"));

            Assert.AreEqual(0, received);
        }

        [Test]
        public void ServerMessage_NeurosyncBlendshapesCancel_PublishesTimelineReset()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");
            LipSyncTimelineResetRequested captured = default;
            eventHub.Subscribe<LipSyncTimelineResetRequested>(e => captured = e);

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "neurosync-blendshapes-cancel",
                new JObject
                {
                    ["response_id"] = "session:r4",
                    ["neurosync_turn_id"] = 4,
                    ["epoch"] = 2,
                    ["sequence"] = 18,
                    ["valid_through_frame_index"] = 179,
                    ["reason"] = "interruption"
                },
                "participant-1"));

            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
            Assert.AreEqual("session:r4", captured.ResponseId);
            Assert.AreEqual(4, captured.NeuroSyncTurnId);
            Assert.AreEqual(2, captured.Epoch);
            Assert.AreEqual(18, captured.Sequence);
            Assert.AreEqual(179, captured.ValidThroughFrameIndex);
            Assert.AreEqual("interruption", captured.Reason);
        }

        [Test]
        public void ServerMessage_ActionResponse_PublishesOrderedActions()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var actionHost = new GameObject("rtvi-ordered-actions");
            try
            {
                registry.RegisterCharacter(CreateMovePickActionAgent(actionHost));
                registry.SetParticipantId("char-1", "participant-1");

                CharacterActionReceived captured = default;
                eventHub.Subscribe<CharacterActionReceived>(e => captured = e);

                gateway.ProcessIncoming(CreateServerMessagePacket(
                    "action-response",
                    new JObject
                    {
                        ["actions"] = new JArray(
                            new JObject
                            {
                                ["name"] = "Move To",
                                ["target"] = "cube"
                            },
                            new JObject
                            {
                                ["name"] = "Pick Up",
                                ["target"] = "cube"
                            })
                    },
                    "participant-1"));

                Assert.AreEqual("char-1", captured.CharacterId);
                Assert.AreEqual(2, captured.Actions.Count);
                Assert.AreEqual("Move To", captured.Actions[0].Name);
                Assert.AreEqual("cube", captured.Actions[0].Target);
                Assert.AreEqual("Pick Up", captured.Actions[1].Name);
                Assert.AreEqual("cube", captured.Actions[1].Target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actionHost);
            }
        }

        [Test]
        public void ServerMessage_ActionResponse_PublishesEnrichedParameterizedActions()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var drawer = new GameObject("rtvi-drawer");
            try
            {
                var character = new TestCharacterAgent("char-1", "Camila")
                {
                    ActionConfig = new ConvaiActionConfig
                    {
                        Objects = new List<ConvaiActionObjectDefinition>
                        {
                            new() { Name = "drawer", GameObjectReference = drawer }
                        }
                    },
                    ActionDefinitions = new List<ConvaiActionDefinition>
                    {
                        new()
                        {
                            ActionName = "Put",
                            Parameters = new List<ConvaiActionParameterDefinition>
                            {
                                new() { Name = "item", Type = ConvaiActionParameterType.String },
                                new() { Name = "container", Type = ConvaiActionParameterType.Reference, Connector = "on" }
                            }
                        }
                    }
                };
                TestActionExecutor executor = drawer.AddComponent<TestActionExecutor>();
                character.ActionDefinitions[0].Executor = executor;
                registry.RegisterCharacter(character);
                registry.SetParticipantId("char-1", "participant-1");

                CharacterActionReceived captured = default;
                eventHub.Subscribe<CharacterActionReceived>(e => captured = e);

                gateway.ProcessIncoming(CreateServerMessagePacket(
                    "action-response",
                    new JObject
                    {
                        ["actions"] = new JArray(
                            new JObject
                            {
                                ["name"] = "Put",
                                ["target"] = "red key on drawer"
                            })
                    },
                    "participant-1"));

                Assert.AreEqual("char-1", captured.CharacterId);
                Assert.AreEqual("Put", captured.Actions[0].Name);
                Assert.AreEqual("red key", captured.Actions[0].Parameters["item"].StringValue);
                Assert.AreEqual("drawer", captured.Actions[0].Parameters["container"].ResolvedReference?.Name);
                Assert.AreEqual(ConvaiActionTargetKind.Object, captured.Actions[0].Parameters["container"].ResolvedReference?.Kind);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(drawer);
            }
        }

        [Test]
        public void ServerMessage_ActionResponse_EmptyArrayStillPublishesNoOpBatch()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            registry.SetParticipantId("char-1", "participant-1");

            CharacterActionReceived captured = default;
            bool invoked = false;
            eventHub.Subscribe<CharacterActionReceived>(e =>
            {
                invoked = true;
                captured = e;
            });

            gateway.ProcessIncoming(CreateServerMessagePacket(
                "action-response",
                new JObject
                {
                    ["actions"] = new JArray()
                },
                "participant-1"));

            Assert.IsTrue(invoked);
            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual(0, captured.Actions.Count);
        }

        [Test]
        public void ActionResponse_DirectAndNested_RejectInvalidActionsAndTargetsBeforePublishing()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var actionHost = new GameObject("rtvi-action-safety-regression");
            try
            {
                registry.RegisterCharacter(CreateMovePickActionAgent(actionHost, includePickUp: false));
                registry.SetParticipantId("char-1", "participant-1");

                var captured = new List<CharacterActionReceived>();
                var filterDiagnostics = new List<ConvaiActionResponseFilterDiagnostic>();
                eventHub.Subscribe<CharacterActionReceived>(e => captured.Add(e));
                eventHub.Subscribe<ConvaiActionResponseFilterDiagnostic>(e => filterDiagnostics.Add(e));
                JArray actions = CreateActionSafetyRegressionPayload();

                gateway.ProcessIncoming(CreateServerMessagePacket(
                    "action-response",
                    new JObject { ["actions"] = actions.DeepClone() },
                    "participant-1"));
                gateway.ProcessIncoming(CreateDirectActionResponsePacket(
                    (JArray)actions.DeepClone(),
                    "participant-1"));

                Assert.AreEqual(2, captured.Count);
                Assert.AreEqual(2, filterDiagnostics.Count);
                for (int i = 0; i < captured.Count; i++)
                {
                    Assert.AreEqual(1, captured[i].Actions.Count);
                    Assert.AreEqual("Move To", captured[i].Actions[0].Name);
                    Assert.AreEqual("Cube", captured[i].Actions[0].Target);

                    ConvaiActionResponseFilterDiagnostic diagnostic = filterDiagnostics[i];
                    Assert.AreEqual("char-1", diagnostic.CharacterId);
                    Assert.AreEqual(4, diagnostic.ReceivedCount);
                    Assert.AreEqual(1, diagnostic.AcceptedCount);
                    Assert.AreEqual(3, diagnostic.RejectedCount);
                    Assert.AreEqual(2,
                        diagnostic.RejectedByReason[
                            ConvaiActionResponseParser.RejectionUnknownOrUnexecutableAction]);
                    Assert.AreEqual(1,
                        diagnostic.RejectedByReason[
                            ConvaiActionResponseParser.RejectionRequiredTargetUnresolved]);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actionHost);
            }
        }

        [Test]
        public void ActionResponse_FullyRejectedBatchStillPublishesEmptyBatch()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var actionHost = new GameObject("rtvi-fully-rejected-actions");
            try
            {
                registry.RegisterCharacter(CreateMovePickActionAgent(actionHost, includePickUp: false));
                registry.SetParticipantId("char-1", "participant-1");
                CharacterActionReceived captured = default;
                bool invoked = false;
                eventHub.Subscribe<CharacterActionReceived>(e =>
                {
                    invoked = true;
                    captured = e;
                });

                gateway.ProcessIncoming(CreateServerMessagePacket(
                    "action-response",
                    new JObject
                    {
                        ["actions"] = new JArray(
                            new JObject { ["name"] = "Dance", ["target"] = "Door" },
                            new JObject { ["name"] = "Move To", ["target"] = "Door" })
                    },
                    "participant-1"));

                Assert.IsTrue(invoked);
                Assert.AreEqual(0, captured.Actions.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actionHost);
            }
        }

        [Test]
        public void ActionResponse_CharacterFallbackDefinitionOutsideActiveSubset_IsStillSafetyCheckedAndAccepted()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var actionHost = new GameObject("rtvi-character-fallback-action");
            try
            {
                ConvaiCharacter character = actionHost.AddComponent<ConvaiCharacter>();
                character.Configure("char-1", "Camila");
                TestActionExecutor executor = actionHost.AddComponent<TestActionExecutor>();
                var definition = new ConvaiActionDefinition
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.Object,
                    Executor = executor
                };
                character.SetResolvedSessionActionConfig(new ConvaiActionConfig
                {
                    Actions = new List<string>(),
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "Cube", GameObjectReference = actionHost }
                    }
                });
                character.SetResolvedSessionActionDefinitions(Array.Empty<ConvaiActionDefinition>());
                character.SetResolvedSessionActionDefinitionCatalog(new[] { definition });
                registry.RegisterCharacter(character);
                registry.SetParticipantId("char-1", "participant-1");

                CharacterActionReceived captured = default;
                eventHub.Subscribe<CharacterActionReceived>(e => captured = e);

                gateway.ProcessIncoming(CreateDirectActionResponsePacket(
                    new JArray(new JObject { ["name"] = "Move To", ["target"] = "Cube" }),
                    "participant-1"));

                Assert.AreEqual(0, character.ActionDefinitions.Count);
                Assert.AreEqual(1, captured.Actions.Count);
                Assert.AreEqual("Move To", captured.Actions[0].Name);
                Assert.AreEqual("Cube", captured.Actions[0].Target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actionHost);
            }
        }

        [Test]
        public void ServerMessage_ActionResponse_SkipsMalformedActionEntries()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var actionHost = new GameObject("rtvi-malformed-actions");
            try
            {
                registry.RegisterCharacter(CreateMovePickActionAgent(actionHost));
                registry.SetParticipantId("char-1", "participant-1");

                CharacterActionReceived captured = default;
                bool invoked = false;
                eventHub.Subscribe<CharacterActionReceived>(e =>
                {
                    invoked = true;
                    captured = e;
                });

                Assert.DoesNotThrow(() => gateway.ProcessIncoming(CreateServerMessagePacket(
                    "action-response",
                    new JObject
                    {
                        ["actions"] = new JArray(
                            null,
                            "bad entry",
                            new JObject
                            {
                                ["name"] = "   ",
                                ["target"] = "ignored"
                            },
                            new JObject
                            {
                                ["name"] = "Move To",
                                ["target"] = "cube"
                            })
                    },
                    "participant-1")));

                Assert.IsTrue(invoked);
                Assert.AreEqual("char-1", captured.CharacterId);
                Assert.AreEqual(1, captured.Actions.Count);
                Assert.AreEqual("Move To", captured.Actions[0].Name);
                Assert.AreEqual("cube", captured.Actions[0].Target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actionHost);
            }
        }

        [Test]
        public void DirectActionResponse_WithDataEnvelope_PublishesEnrichedParameterizedActions()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var drawer = new GameObject("direct-rtvi-drawer");
            try
            {
                var character = new TestCharacterAgent("char-1", "Camila")
                {
                    ActionConfig = new ConvaiActionConfig
                    {
                        Objects = new List<ConvaiActionObjectDefinition>
                        {
                            new() { Name = "drawer", GameObjectReference = drawer }
                        }
                    },
                    ActionDefinitions = new List<ConvaiActionDefinition>
                    {
                        new()
                        {
                            ActionName = "Put",
                            Parameters = new List<ConvaiActionParameterDefinition>
                            {
                                new() { Name = "item", Type = ConvaiActionParameterType.String },
                                new() { Name = "container", Type = ConvaiActionParameterType.Reference, Connector = "on" }
                            }
                        }
                    }
                };
                TestActionExecutor executor = drawer.AddComponent<TestActionExecutor>();
                character.ActionDefinitions[0].Executor = executor;
                registry.RegisterCharacter(character);
                registry.SetParticipantId("char-1", "participant-1");

                CharacterActionReceived captured = default;
                eventHub.Subscribe<CharacterActionReceived>(e => captured = e);

                gateway.ProcessIncoming(CreateDirectActionResponsePacket(
                    new JArray(
                        new JObject
                        {
                            ["name"] = "Put",
                            ["target"] = "red key on drawer"
                        }),
                    "participant-1"));

                Assert.AreEqual("char-1", captured.CharacterId);
                Assert.AreEqual("Put", captured.Actions[0].Name);
                Assert.AreEqual("red key", captured.Actions[0].Parameters["item"].StringValue);
                Assert.AreEqual("drawer", captured.Actions[0].Parameters["container"].ResolvedReference?.Name);
                Assert.AreEqual(ConvaiActionTargetKind.Object, captured.Actions[0].Parameters["container"].ResolvedReference?.Kind);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(drawer);
            }
        }

        [Test]
        public void DirectActionResponse_SkipsMalformedActionEntries()
        {
            EventHub eventHub = CreateEventHub();
            CreateHandler(eventHub, out ProtocolGateway gateway, out TestAgentRegistry registry);
            var actionHost = new GameObject("direct-rtvi-malformed-actions");
            try
            {
                registry.RegisterCharacter(CreateMovePickActionAgent(actionHost));
                registry.SetParticipantId("char-1", "participant-1");

                CharacterActionReceived captured = default;
                bool invoked = false;
                eventHub.Subscribe<CharacterActionReceived>(e =>
                {
                    invoked = true;
                    captured = e;
                });

                Assert.DoesNotThrow(() => gateway.ProcessIncoming(CreateDirectActionResponsePacket(
                    new JArray(
                        null,
                        "bad entry",
                        new JObject
                        {
                            ["name"] = "   ",
                            ["target"] = "ignored"
                        },
                        new JObject
                        {
                            ["name"] = "Move To",
                            ["target"] = "cube"
                        }),
                    "participant-1")));

                Assert.IsTrue(invoked);
                Assert.AreEqual("char-1", captured.CharacterId);
                Assert.AreEqual(1, captured.Actions.Count);
                Assert.AreEqual("Move To", captured.Actions[0].Name);
                Assert.AreEqual("cube", captured.Actions[0].Target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actionHost);
            }
        }

        private static EventHub CreateEventHub() => new(new ImmediateScheduler(), new TestLogger());

        private static RtviTestContext CreateContext(LipSyncTransportOptions lipSyncOptions = default)
        {
            var logger = new TestLogger();
            var eventHub = new EventHub(new ImmediateScheduler(), logger);
            var gateway = new ProtocolGateway(
                logDebug: message => logger.Debug(message, LogCategory.Transport),
                logError: message => logger.Error(message, LogCategory.Transport));
            var transport = new RecordingTransport();
            var registry = new TestAgentRegistry();
            var playerSession = new RecordingPlayerSession();
            var dispatcher = new RecordingDispatcher();
            var handler = new RTVIHandler(
                gateway,
                transport,
                registry,
                playerSession,
                dispatcher,
                logger,
                eventHub,
                lipSyncTransportOptions: lipSyncOptions);

            return new RtviTestContext(
                handler,
                gateway,
                eventHub,
                transport,
                registry,
                playerSession,
                dispatcher,
                logger);
        }

        private static RTVIHandler CreateHandler(EventHub eventHub, out ProtocolGateway gateway,
            out TestAgentRegistry agentRegistry) =>
            CreateHandler(eventHub, new RecordingPlayerSession(), out gateway, out agentRegistry);

        private static RTVIHandler CreateHandler(EventHub eventHub, RecordingPlayerSession playerSession,
            out ProtocolGateway gateway, out TestAgentRegistry agentRegistry)
        {
            gateway = new ProtocolGateway();
            agentRegistry = new TestAgentRegistry();
            return new RTVIHandler(
                gateway,
                new RecordingTransport(),
                agentRegistry,
                playerSession,
                new ImmediateDispatcher(),
                new TestLogger(),
                eventHub);
        }

        private static ProtocolPacket CreateSimpleInboundPacket(string type)
        {
            JObject outer = new()
            {
                ["type"] = type
            };

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                string.Empty,
                "rtvi-ai",
                true);
        }

        private static ProtocolPacket CreateInboundPacket(
            string type,
            string participantId = "",
            JToken data = null,
            string packetId = null)
        {
            JObject outer = new()
            {
                ["type"] = type
            };
            if (data != null) outer["data"] = data;
            if (!string.IsNullOrWhiteSpace(packetId)) outer["id"] = packetId;

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                participantId,
                "rtvi-ai",
                true);
        }

        private static ProtocolPacket CreateUserTranscriptionPacket(string text, bool isFinal)
        {
            JObject outer = new()
            {
                ["type"] = "user-transcription",
                ["data"] = new JObject
                {
                    ["text"] = text,
                    ["final"] = isFinal
                }
            };

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                string.Empty,
                "rtvi-ai",
                true);
        }

        private static ProtocolPacket CreateBotTranscriptionPacket(
            string type,
            string text,
            string participantId,
            string responseId = null,
            string messageId = null,
            string turnId = null,
            bool? spoken = null,
            string aggregatedBy = null,
            string packetId = null)
        {
            JObject data = new()
            {
                ["text"] = text
            };

            if (!string.IsNullOrWhiteSpace(responseId)) data["response_id"] = responseId;
            if (!string.IsNullOrWhiteSpace(messageId)) data["message_id"] = messageId;
            if (!string.IsNullOrWhiteSpace(turnId)) data["turn_id"] = turnId;
            if (spoken.HasValue) data["spoken"] = spoken.Value;
            if (!string.IsNullOrWhiteSpace(aggregatedBy)) data["aggregated_by"] = aggregatedBy;

            JObject outer = new()
            {
                ["type"] = type,
                ["data"] = data
            };
            if (!string.IsNullOrWhiteSpace(packetId)) outer["id"] = packetId;

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                participantId,
                "rtvi-ai",
                true);
        }

        private static ProtocolPacket CreateBotSpeechPacket(
            string type,
            string participantId,
            string responseId,
            int? turnId = null,
            int? epoch = null,
            int? sequence = null)
        {
            JObject data = new()
            {
                ["response_id"] = responseId
            };
            if (turnId.HasValue) data["neurosync_turn_id"] = turnId.Value;
            if (epoch.HasValue) data["epoch"] = epoch.Value;
            if (sequence.HasValue) data["sequence"] = sequence.Value;

            JObject outer = new()
            {
                ["type"] = type,
                ["data"] = data
            };

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                participantId,
                "rtvi-ai",
                true);
        }

        private static ProtocolPacket CreateBotSpeechScalarPacket(
            string type,
            string participantId,
            JToken data)
        {
            JObject outer = new()
            {
                ["type"] = type,
                ["data"] = data
            };

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                participantId,
                "rtvi-ai",
                true);
        }

        private static ProtocolPacket CreateServerMessagePacket(string innerType, JObject payload,
            string participantId = "")
        {
            payload ??= new JObject();
            payload["type"] = innerType;

            JObject outer = new()
            {
                ["type"] = "server-message",
                ["data"] = payload
            };

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                participantId,
                "rtvi-ai",
                true);
        }

        private static ProtocolPacket CreateDirectActionResponsePacket(JArray actions, string participantId = "")
        {
            JObject data = new()
            {
                ["actions"] = actions
            };
            JObject outer = new()
            {
                ["type"] = "action-response",
                ["data"] = data
            };

            return new ProtocolPacket(
                Encoding.UTF8.GetBytes(outer.ToString()),
                participantId,
                "rtvi-ai",
                true);
        }

        private static JArray CreateActionSafetyRegressionPayload() =>
            new(
                new JObject { ["name"] = "Dance Door" },
                new JObject { ["name"] = "Move To", ["target"] = "Door" },
                new JObject { ["name"] = "Fly To Moon" },
                new JObject { ["name"] = "Move To", ["target"] = "Cube" });

        private static TestCharacterAgent CreateMovePickActionAgent(
            GameObject actionHost,
            bool includePickUp = true)
        {
            TestActionExecutor executor = actionHost.AddComponent<TestActionExecutor>();
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.Object,
                    Executor = executor
                }
            };
            if (includePickUp)
            {
                definitions.Add(new ConvaiActionDefinition
                {
                    ActionName = "Pick Up",
                    TargetRequirement = ConvaiActionTargetRequirement.Object,
                    Executor = executor
                });
            }

            return new TestCharacterAgent("char-1", "Camila")
            {
                ActionConfig = new ConvaiActionConfig
                {
                    Actions = includePickUp
                        ? new List<string> { "Move To", "Pick Up" }
                        : new List<string> { "Move To" },
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "Cube", GameObjectReference = actionHost }
                    }
                },
                ActionDefinitions = definitions
            };
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class ImmediateDispatcher : IMainThreadDispatcher
        {
            public bool TryDispatch(Action action)
            {
                action?.Invoke();
                return true;
            }
        }

        private sealed class RecordingDispatcher : IMainThreadDispatcher
        {
            public bool AcceptDispatch { get; set; } = true;
            public int DispatchAttempts { get; private set; }

            public bool TryDispatch(Action action)
            {
                DispatchAttempts++;
                if (!AcceptDispatch) return false;
                action?.Invoke();
                return true;
            }
        }

        private sealed class RecordingPlayerSession : IPlayerSession, IPlayerTypedTranscriptSink
        {
            public readonly List<string> StartedSessionIds = new();
            public readonly List<StoppedSession> StoppedSessions = new();
            public readonly List<TranscriptionEntry> Transcriptions = new();
            public readonly List<TypedTranscriptionEntry> TypedTranscriptions = new();

            public string PlayerId => "player-1";
            public string PlayerName => "Player";
            public bool IsMicMuted { get; private set; }

            public void SetMicMuted(bool mute) => IsMicMuted = mute;
            public void OnPlayerTranscriptionReceived(string transcript, TranscriptionPhase transcriptionPhase) =>
                Transcriptions.Add(new TranscriptionEntry(transcript, transcriptionPhase));

            public void OnPlayerTranscriptionReceived(string transcript, TranscriptionPhase transcriptionPhase,
                SpeakerInfo speakerInfo) => Transcriptions.Add(new TranscriptionEntry(transcript, transcriptionPhase));

            public void OnPlayerStartedSpeaking(string sessionId)
            {
                StartedSessionIds.Add(sessionId);
            }

            public void OnPlayerStoppedSpeaking(string sessionId, bool didProduceFinalTranscript)
            {
                StoppedSessions.Add(new StoppedSession(sessionId, didProduceFinalTranscript));
            }

            public void PublishTypedText(string transcript, string messageId, SpeakerInfo speakerInfo = default) =>
                TypedTranscriptions.Add(new TypedTranscriptionEntry(transcript, messageId, speakerInfo));
        }

        private readonly struct TypedTranscriptionEntry
        {
            public TypedTranscriptionEntry(string text, string messageId, SpeakerInfo speakerInfo)
            {
                Text = text;
                MessageId = messageId;
                SpeakerInfo = speakerInfo;
            }

            public string Text { get; }
            public string MessageId { get; }
            public SpeakerInfo SpeakerInfo { get; }
        }

        private readonly struct TranscriptionEntry
        {
            public TranscriptionEntry(string text, TranscriptionPhase phase)
            {
                Text = text;
                Phase = phase;
            }

            public string Text { get; }
            public TranscriptionPhase Phase { get; }
        }

        private readonly struct StoppedSession
        {
            public StoppedSession(string sessionId, bool didProduceFinalTranscript)
            {
                SessionId = sessionId;
                DidProduceFinalTranscript = didProduceFinalTranscript;
            }

            public string SessionId { get; }
            public bool DidProduceFinalTranscript { get; }
        }

        private sealed class RecordingTransport : IRealtimeTransport
        {
            public readonly List<string> SentPayloads = new();
            public readonly List<string[]> SentDestinations = new();
            public Exception SendFailure { get; set; }

            public event Action<DataPacket> DataReceived
            {
                add { }
                remove { }
            }

            public event Action<TransportSessionInfo> Connected
            {
                add { }
                remove { }
            }

            public event Action<DisconnectReason> Disconnected
            {
                add { }
                remove { }
            }

            public event Action<TransportError> ConnectionFailed
            {
                add { }
                remove { }
            }

            public event Action Reconnecting
            {
                add { }
                remove { }
            }

            public event Action Reconnected
            {
                add { }
                remove { }
            }

            public event Action<TransportState> StateChanged;
            public void RaiseStateChanged(TransportState state) => StateChanged?.Invoke(state);

            public event Action<TransportParticipantInfo> ParticipantConnected
            {
                add { }
                remove { }
            }

            public event Action<TransportParticipantInfo> ParticipantDisconnected
            {
                add { }
                remove { }
            }

            public event Action<TrackInfo> TrackSubscribed
            {
                add { }
                remove { }
            }

            public event Action<TrackInfo> TrackUnsubscribed
            {
                add { }
                remove { }
            }

            public event Action<bool> MicrophoneEnabledChanged
            {
                add { }
                remove { }
            }

            public event Action<bool> MicrophoneMuteChanged
            {
                add { }
                remove { }
            }

            public event Action<bool> AudioPlaybackStateChanged
            {
                add { }
                remove { }
            }

            public Task SendDataAsync(ReadOnlyMemory<byte> payload, bool reliable = true, string topic = null,
                string[] destinationIdentities = null, CancellationToken ct = default)
            {
                SentPayloads.Add(Encoding.UTF8.GetString(payload.Span));
                SentDestinations.Add(destinationIdentities);
                return SendFailure == null ? Task.CompletedTask : Task.FromException(SendFailure);
            }

            public TransportState State => TransportState.Connected;
            public TransportSessionInfo? CurrentSession => null;
            public TransportCapabilities Capabilities => default;
            public AudioRuntimeState AudioState => default;
            public bool IsConnected => true;
            public IRoomFacade Room { get; set; }
            public Task<bool> ConnectAsync(string url, string token, TransportConnectOptions options = null,
                CancellationToken ct = default) => Task.FromResult(true);
            public Task DisconnectAsync(DisconnectReason reason = DisconnectReason.ClientInitiated,
                CancellationToken ct = default) => Task.CompletedTask;
            public void EnableAudio() { }
            public Task<bool> EnableMicrophoneAsync(int microphoneDeviceIndex = 0, CancellationToken ct = default) =>
                Task.FromResult(true);
            public Task DisableMicrophoneAsync(CancellationToken ct = default) => Task.CompletedTask;
            public void SetMicrophoneMuted(bool muted) { }
            public bool IsMicrophoneEnabled => true;
            public bool IsMicrophoneMuted => false;
            public bool CanEnableMicrophone() => true;
            public bool CanEnableAudio() => true;
            public void Dispose() { }
        }

        private sealed class TestActionExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation,
                CancellationToken cancellationToken) =>
                Task.FromResult(ConvaiActionExecutionResult.Succeeded());
        }

        private sealed class TestCharacterAgent : IConvaiCharacterAgent, IConvaiActionRuntimeSource
        {
            public TestCharacterAgent(string characterId, string characterName)
            {
                CharacterId = characterId;
                CharacterName = characterName;
            }

            public string CharacterId { get; }
            public string CharacterName { get; }
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext { get; } = new MockDynamicContext();
            public EmotionDetectionMode EmotionDetectionMode => Convai.Domain.Emotion.EmotionDetectionMode.Off;
            public ConvaiActionConfig ActionConfig { get; set; }
            public IReadOnlyList<ConvaiActionDefinition> ActionDefinitions { get; set; } =
                Array.Empty<ConvaiActionDefinition>();
            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }
        }

        private sealed class TestAgentRegistry : IAgentRegistry, IMultiCharacterSessionRegistry
        {
            private readonly Dictionary<string, IConvaiCharacterAgent> _characters = new();
            private readonly Dictionary<string, string> _characterToParticipant = new();
            private readonly Dictionary<string, string> _participantToCharacter = new();
            private readonly List<IConvaiPlayerAgent> _players = new();

            public IReadOnlyList<IConvaiCharacterAgent> Characters => new List<IConvaiCharacterAgent>(_characters.Values);
            public IReadOnlyList<IConvaiPlayerAgent> Players => _players;
            public IConvaiPlayerAgent LocalPlayer => _players.Count > 0 ? _players[0] : null;
            public MultiCharacterRoomSession CurrentMultiCharacterSession { get; private set; }
            public event Action<IConvaiCharacterAgent> CharacterRegistered;
            public event Action<IConvaiCharacterAgent> CharacterUnregistered;
            public event Action<IConvaiPlayerAgent> PlayerRegistered;

            public void RegisterCharacter(IConvaiCharacterAgent character, string ownerId = null)
            {
                _characters[character.CharacterId] = character;
                CharacterRegistered?.Invoke(character);
            }

            public void RegisterPlayer(IConvaiPlayerAgent player)
            {
                _players.Add(player);
                PlayerRegistered?.Invoke(player);
            }

            public void Unregister(IConvaiCharacterAgent character)
            {
                if (character == null) return;
                _characters.Remove(character.CharacterId);
                CharacterUnregistered?.Invoke(character);
            }

            public void Unregister(IConvaiPlayerAgent player) => _players.Remove(player);
            public bool TryGetCharacter(string characterId, out IConvaiCharacterAgent agent) =>
                _characters.TryGetValue(characterId ?? string.Empty, out agent);
            public string GetOwner(IConvaiCharacterAgent character) => null;
            public IReadOnlyList<IConvaiCharacterAgent> GetCharactersByOwner(string ownerId) => Array.Empty<IConvaiCharacterAgent>();
            public int GetCharacterCountByOwner(string ownerId) => 0;
            public bool TryGetCharacterById(string characterId, out IConvaiCharacterAgent agent) =>
                TryGetCharacter(characterId, out agent);
            public bool TryGetAudioSource(string characterId, out AudioSource source)
            {
                source = null;
                return false;
            }

            public void SetAudioSource(string characterId, AudioSource source) { }

            public void SetParticipantId(string characterId, string participantId)
            {
                if (string.IsNullOrWhiteSpace(characterId))
                    return;

                if (_characterToParticipant.TryGetValue(characterId, out string existingParticipant) &&
                    !string.IsNullOrWhiteSpace(existingParticipant))
                    _participantToCharacter.Remove(existingParticipant);

                if (string.IsNullOrWhiteSpace(participantId))
                {
                    _characterToParticipant.Remove(characterId);
                    return;
                }

                _characterToParticipant[characterId] = participantId;
                _participantToCharacter[participantId] = characterId;
            }

            public bool TryGetParticipantId(string characterId, out string participantId) =>
                _characterToParticipant.TryGetValue(characterId ?? string.Empty, out participantId);

            public bool TryGetCharacterByParticipantId(string participantId, out IConvaiCharacterAgent agent)
            {
                agent = null;
                if (string.IsNullOrWhiteSpace(participantId) ||
                    !_participantToCharacter.TryGetValue(participantId, out string characterId))
                    return false;

                return _characters.TryGetValue(characterId, out agent);
            }

            public void ClearTransportBindings()
            {
                _characterToParticipant.Clear();
                _participantToCharacter.Clear();
            }

            public MultiCharacterRoomSession Configure(
                RoomDetails details,
                IReadOnlyList<IConvaiCharacterAgent> characters)
            {
                CurrentMultiCharacterSession = new MultiCharacterRoomSession(details, characters);
                return CurrentMultiCharacterSession;
            }

            public bool TryClaimMultiCharacterSessionForRecovery(MultiCharacterRoomSession expected)
            {
                if (!ReferenceEquals(CurrentMultiCharacterSession, expected)) return false;
                CurrentMultiCharacterSession = null;
                return true;
            }

            public void ClearMultiCharacterSession() => CurrentMultiCharacterSession = null;

            public void SetCharacterMuted(string characterId, bool muted) { }
            public bool IsCharacterMuted(string characterId) => false;
        }

        private sealed class TestLogger : Convai.Domain.Logging.ILogger
        {
            public void Log(in Convai.Domain.Logging.LogEntry entry) =>
                Entries.Add(new TestLogRecord(entry.Level, entry.Message, entry.Category, entry.Exception, entry.Source));
            public readonly List<TestLogRecord> Entries = new();
            public bool Enabled { get; set; } = true;
            public LogLevel Level { get; set; } = LogLevel.Trace;
            public LogCategory? DisabledCategory { get; set; }
            public readonly Dictionary<LogCategory, LogLevel> CategoryLevels = new();

            public bool IsEnabled(LogLevel level, LogCategory category) =>
                Enabled && level != LogLevel.Off && category != DisabledCategory &&
                level <= (CategoryLevels.TryGetValue(category, out LogLevel categoryLevel) ? categoryLevel : Level);
            // Retain even disabled calls so tests catch missing caller-side guards, not just sink filtering.
            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) =>
                Entries.Add(new TestLogRecord(level, message, category, null));

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Entries.Add(new TestLogRecord(level, message, category, null));

            public void Debug(string message, LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Debug, message, category);

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Debug, message, context, category);

            public void Info(string message, LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Info, message, category);

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Info, message, context, category);

            public void Warning(string message, LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Warning, message, category);

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Warning, message, context, category);

            public void Error(string message, LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Error, message, category);

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Log(LogLevel.Error, message, context, category);

            public void Error(Exception exception, string message, LogCategory category = LogCategory.SDK) =>
                Entries.Add(new TestLogRecord(LogLevel.Error, message, category, exception));

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) =>
                Entries.Add(new TestLogRecord(LogLevel.Error, message, category, exception));

            public bool Contains(string text) => Entries.Any(entry => entry.Message.Contains(text));
        }

        private readonly struct TestLogRecord
        {
            public TestLogRecord(LogLevel level, string message, LogCategory category, Exception exception, string source = null)
            {
                Level = level;
                Message = message ?? string.Empty;
                Category = category;
                Exception = exception;
                Source = source;
            }

            public string Source { get; }
            public LogLevel Level { get; }
            public string Message { get; }
            public LogCategory Category { get; }
            public Exception Exception { get; }
        }

        private sealed class RtviTestContext
        {
            public RtviTestContext(
                RTVIHandler handler,
                ProtocolGateway gateway,
                EventHub eventHub,
                RecordingTransport transport,
                TestAgentRegistry registry,
                RecordingPlayerSession playerSession,
                RecordingDispatcher dispatcher,
                TestLogger logger)
            {
                Handler = handler;
                Gateway = gateway;
                EventHub = eventHub;
                Transport = transport;
                Registry = registry;
                PlayerSession = playerSession;
                Dispatcher = dispatcher;
                Logger = logger;
            }

            public RTVIHandler Handler { get; }
            public ProtocolGateway Gateway { get; }
            public EventHub EventHub { get; }
            public RecordingTransport Transport { get; }
            public TestAgentRegistry Registry { get; }
            public RecordingPlayerSession PlayerSession { get; }
            public RecordingDispatcher Dispatcher { get; }
            public TestLogger Logger { get; }
        }
    }
}
