using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Abstractions;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.DomainEvents.Narrative;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.DomainEvents.Vision;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Protocol;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Runtime.Actions;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Room;
using Convai.RestAPI.Internal;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Convai.Infrastructure.Networking
{
    /// <summary>
    ///     Bridges RTVI (Real-Time Voice Inference) protocol messages to Character/player systems
    ///     while keeping transport concerns abstracted.
    /// </summary>
    public sealed class RTVIHandler
    {
        private readonly IAgentRegistry _agentRegistry;
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly IEventHub _eventHub;

        private readonly ProtocolGateway _gateway;
        private readonly CharacterResponseState _characterResponseState = new();
        private readonly LipSyncProtocolIngress _lipSyncIngress;
        private readonly ILogger _logger;
        private readonly PlayerConversationInput _playerConversationInput;
        private readonly INarrativeSectionNameResolver _sectionNameResolver;
        private readonly IRealtimeTransport _transport;
        internal PttCommitTracker PttCommits { get; private set; }
        internal event Action PttSessionInvalidated;

        internal string ResolvePttParticipant(string characterId = null)
        {
            if (!string.IsNullOrEmpty(characterId) && _agentRegistry.TryGetParticipantId(characterId, out string participant))
                return participant;
            return string.IsNullOrEmpty(characterId) ? ResolveParticipantId(null) : null;
        }

        internal async Task SendPttControlAsync(object message, string participantId, CancellationToken ct)
        {
            IRemoteParticipant participant = _transport.Room?.GetParticipantBySid(participantId)
                ?? _transport.Room?.GetParticipantByIdentity(participantId);
            if (string.IsNullOrWhiteSpace(participant?.Identity))
                throw new InvalidOperationException("PTT target participant is unavailable.");
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
            await _transport.SendDataAsync(bytes, destinationIdentities: new[] { participant.Identity }, ct: ct);
        }

        internal void ResetPttRequests()
        {
            PttCommits?.Dispose();
            PttCommits = new PttCommitTracker((id, participant, ct) =>
                SendPttControlAsync(new RTVIForceUserStoppedSpeaking(id), participant, ct));
        }


        /// <summary>
        ///     Keeps a repeated action-drop fault to one line every few seconds, keyed by character
        ///     and drop signature.
        /// </summary>
        /// <remarks>
        ///     A Convai Character asks for the same missing target on every turn, and a warning per
        ///     turn buries the console it was meant to inform. This used to be a set that was never
        ///     cleared, which is a different thing: the first occurrence was said and every later one
        ///     was silent for the life of the connection — including the attempt made after the
        ///     author changed something to fix it. See <see cref="RepeatedMessageThrottle" />.
        /// </remarks>
        private readonly RepeatedMessageThrottle _actionDropThrottle =
            new(ActionDropReportIntervalSeconds);

        /// <summary>
        ///     How long one distinct action-drop fault stays quiet after being explained.
        /// </summary>
        /// <remarks>
        ///     Longer than the lip-sync ingress's five seconds, and for the opposite reason: a
        ///     dropped command is at most one per conversational turn rather than one per audio
        ///     packet, so the console is never in danger of being buried, and the explanation is
        ///     several sentences long. Half a minute is roughly "once per thing you try".
        /// </remarks>
        private const double ActionDropReportIntervalSeconds = 30d;

        /// <summary>
        ///     Initializes a new instance of the <see cref="RTVIHandler" /> class and registers inbound protocol handlers.
        /// </summary>
        /// <param name="gateway">Protocol gateway used to dispatch inbound messages.</param>
        /// <param name="transport">Realtime transport used to send outbound data packets.</param>
        /// <param name="agentRegistry">Agent registry used to resolve participants to characters.</param>
        /// <param name="playerSession">Player session used for player transcription callbacks.</param>
        /// <param name="dispatcher">Dispatcher used to marshal callbacks to the main thread.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="eventHub">Optional event hub used for publishing domain events.</param>
        /// <param name="sectionNameResolver">Optional resolver for human-readable narrative section names.</param>
        /// <param name="lipSyncTransportOptions">Lip sync transport options negotiated for this room session.</param>
        public RTVIHandler(
            ProtocolGateway gateway,
            IRealtimeTransport transport,
            IAgentRegistry agentRegistry,
            IPlayerSession playerSession,
            IMainThreadDispatcher dispatcher,
            ILogger logger,
            IEventHub eventHub = null,
            INarrativeSectionNameResolver sectionNameResolver = null,
            LipSyncTransportOptions lipSyncTransportOptions = default)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _agentRegistry = agentRegistry ?? throw new ArgumentNullException(nameof(agentRegistry));
            if (playerSession == null) throw new ArgumentNullException(nameof(playerSession));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).WithTag(nameof(RTVIHandler), LogCategory.Transport);
            _eventHub = eventHub;
            _sectionNameResolver = sectionNameResolver;

            _playerConversationInput = new PlayerConversationInput(playerSession, dispatcher, eventHub, _logger);
            _lipSyncIngress = new LipSyncProtocolIngress(
                agentRegistry,
                eventHub,
                _logger,
                lipSyncTransportOptions);

            ResetPttRequests();
            _transport.StateChanged += state =>
            {
                if (state is TransportState.Disconnected or TransportState.Reconnecting)
                {
                    PttSessionInvalidated?.Invoke();
                    ResetPttRequests();
                }
            };
            RegisterInboundHandlers();
        }

        /// <summary>
        ///     Serializes and sends an outbound message over the transport data channel.
        /// </summary>
        /// <param name="data">Payload object to serialize to JSON.</param>
        public void SendData(object data)
        {
            if (data == null) return;

            _ = SendDataAsync(data).ContinueWith(
                task => _logger.Error(
                    $"Failed to send outbound data: {task.Exception?.GetBaseException().Message}",
                    LogCategory.Transport),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        ///     Serializes and sends an outbound message, completing only after the transport has
        ///     accepted it. Canonical commands await this path so a send failure faults their
        ///     registered response immediately instead of masquerading as a missing acknowledgement.
        /// </summary>
        public async Task SendDataAsync(object data, CancellationToken cancellationToken = default)
        {
            if (data == null) return;

            string json = JsonConvert.SerializeObject(data);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _transport.SendDataAsync(bytes, ct: cancellationToken);

            if (data is RTVIUserTextMessage userTextMessage)
                _playerConversationInput.RegisterTypedText(userTextMessage.MessageId, userTextMessage.Text);

            var outboundMessage = data as RTVISendMessageBase;
            if (_eventHub != null && outboundMessage != null)
                _eventHub.Publish(OutboundRtviMessageSent.Create(outboundMessage.Type, outboundMessage.Id));

            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                _logger.Debug(BuildOutboundLogMessage(data, bytes.Length), LogCategory.Transport);
        }

        internal static string BuildOutboundLogMessage(object data, int payloadBytes)
        {
            var outboundMessage = data as RTVISendMessageBase;
            string messageType = string.IsNullOrWhiteSpace(outboundMessage?.Type)
                ? data?.GetType().Name ?? "unknown"
                : outboundMessage.Type;
            return $"Sent RTVI data: type={messageType}, payloadBytes={payloadBytes}.";
        }

        /// <summary>
        ///     Attempts to detect and handle a raw LipSync server message from a protocol packet,
        ///     bypassing the normal JSON-deserialization gateway path for performance.
        /// </summary>
        /// <param name="packet">The raw protocol packet to inspect.</param>
        /// <returns>
        ///     <c>true</c> if the packet was identified as a LipSync message (handled or dropped); <c>false</c> if it should
        ///     be processed normally.
        /// </returns>
        public bool TryHandleLipSyncServerMessage(in ProtocolPacket packet) =>
            _lipSyncIngress.TryHandlePacket(packet);

        private void RegisterInboundHandlers()
        {
            _gateway.RegisterHandler("user-started-speaking", _ => _playerConversationInput.HandleStartedSpeaking());
            _gateway.RegisterHandler<UserTranscriptionPayload>("user-transcription",
                message => _playerConversationInput.HandleTranscription(message.Payload));
            _gateway.RegisterHandler("user-stopped-speaking", _ => _playerConversationInput.HandleStoppedSpeaking());

            _gateway.RegisterHandler("bot-llm-started",
                message => HandleCharacterLlmStarted(message.Packet.ParticipantId));
            _gateway.RegisterHandler("bot-llm-stopped",
                message => HandleCharacterLlmStopped(message.Packet.ParticipantId));
            _gateway.RegisterHandler<BotTranscriptionPayload>("bot-llm-text",
                message => HandleCharacterTranscription(
                    message,
                    TranscriptSegmentSourceKind.BotLlmPreview,
                    TranscriptLifecycle.Streaming));
            _gateway.RegisterHandler<BotTranscriptionPayload>("bot-output",
                message => HandleCharacterTranscription(
                    message,
                    TranscriptSegmentSourceKind.BotOutput,
                    TranscriptLifecycle.Stable));
            _gateway.RegisterHandler<BotTranscriptionPayload>("bot-transcription",
                message => HandleCharacterTranscription(
                    message,
                    TranscriptSegmentSourceKind.LegacyBotTranscript,
                    TranscriptLifecycle.Stable));

            _gateway.RegisterHandler("bot-tts-started",
                message => HandleCharacterTtsStarted(message.Packet.ParticipantId));
            _gateway.RegisterHandler("bot-tts-stopped",
                message => HandleCharacterTtsStopped(message.Packet.ParticipantId));

            _gateway.RegisterHandler("bot-started-speaking",
                HandleCharacterStartedSpeaking);
            _gateway.RegisterHandler("bot-stopped-speaking",
                HandleCharacterStoppedSpeaking);

            _gateway.RegisterHandler("bot-ready", HandleCharacterReady);
            _gateway.RegisterHandler("character-status", HandleCharacterStatus);
            _gateway.RegisterHandler("character-removed", HandleCharacterRemoved);
            _gateway.RegisterHandler<BotTranscriptionPayload>("bot-tts-text",
                message => HandleCharacterTtsText(message));

            _gateway.RegisterHandler("server-message", HandleServerMessage);
            _gateway.RegisterHandler("action-response", HandleActionResponse);
            _gateway.RegisterHandler("error", HandlePipelineError);
            _gateway.RegisterHandler("bot-turn-completed",
                message => HandleCharacterTurnCompleted(message.Packet.ParticipantId, message.Envelope.Json));
            _gateway.RegisterHandler("metrics", HandleMetrics);
        }

        /// <summary>
        ///     Raised when an RTVI metrics message is received (only when debug was enabled in the connect request).
        ///     Use for troubleshooting latency (ttfb, processing) or custom metrics (e.g. NeuroSync).
        /// </summary>
        public event Action<RTVIMetricsPayload> OnMetricsReceived;

        private void HandleMetrics(ProtocolMessage message)
        {
            if (message.Envelope.Payload == null || message.Envelope.Payload.Type == JTokenType.Null)
                return;

            RTVIMetricsPayload payload;
            try
            {
                payload = message.Envelope.Payload.ToObject<RTVIMetricsPayload>();
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                    _logger.Debug($"Failed to parse metrics payload: {ex.Message}", LogCategory.Transport);
                return;
            }

            if (payload == null) return;

            void Invoke()
            {
                try
                {
                    OnMetricsReceived?.Invoke(payload);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                        _logger.Debug($"OnMetricsReceived subscriber threw: {ex.Message}",
                        LogCategory.Transport);
                }
            }

            if (!_dispatcher.TryDispatch(Invoke))
                Invoke();
        }

        private void HandleCharacterLlmStarted(string participantId)
        {
            string safeParticipantId = ResolveParticipantId(participantId);
            _characterResponseState.BeginResponse(safeParticipantId);
            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Character))
                _logger.Debug(
                $"Character LLM started for participant: {safeParticipantId}",
                LogCategory.Character);
        }

        private void HandleCharacterLlmStopped(string participantId) => _logger.Debug(
            $"Character LLM stopped for participant: {participantId}", LogCategory.Character);

        private void HandleCharacterTtsStarted(string participantId) => _logger.Debug(
            $"Character TTS started for participant: {participantId}", LogCategory.Character);

        private void HandleCharacterTtsStopped(string participantId) => _logger.Debug(
            $"Character TTS stopped for participant: {participantId}", LogCategory.Character);

        private void HandleCharacterStartedSpeaking(ProtocolMessage message)
            => HandleCharacterStartedSpeaking(message.Packet.ParticipantId,
                message.Envelope.Payload as JObject ?? JObject.Parse(message.Envelope.Json));

        private void HandleCharacterStartedSpeaking(string sender, JObject payload)
        {
            string participantId = ResolveParticipantId(sender);
            LipSyncResponseOwner incoming = ReadSpeechOwner(payload);
            LipSyncResponseOwner owner = _characterResponseState.ResolveSpeechOwner(
                participantId,
                incoming,
                isSpeaking: true);
            if (_logger.IsEnabled(LogLevel.Info, LogCategory.Character))
                _logger.Info($"Character started speaking for participant: {participantId}",
                LogCategory.Character);
            PublishSpeechStateChanged(participantId, true, owner);
        }

        private void HandleCharacterStoppedSpeaking(ProtocolMessage message)
            => HandleCharacterStoppedSpeaking(message.Packet.ParticipantId,
                message.Envelope.Payload as JObject ?? JObject.Parse(message.Envelope.Json));

        private void HandleCharacterStoppedSpeaking(string sender, JObject payload)
        {
            string participantId = ResolveParticipantId(sender);
            LipSyncResponseOwner incoming = ReadSpeechOwner(payload);
            LipSyncResponseOwner owner = _characterResponseState.ResolveSpeechOwner(
                participantId,
                incoming,
                isSpeaking: false);
            if (_logger.IsEnabled(LogLevel.Info, LogCategory.Character))
                _logger.Info($"Character stopped speaking for participant: {participantId}",
                LogCategory.Character);
            PublishSpeechStateChanged(participantId, false, owner);
            _characterResponseState.CompleteSpeech(participantId, owner.ResponseId);
        }

        private static LipSyncResponseOwner ReadSpeechOwner(JObject payload)
        {
            string responseId = ResolveFirstNonEmpty(
                payload?.Value<string>("response_id"),
                payload?.Value<string>("responseId"),
                payload?.Value<string>("utterance_id"),
                payload?.Value<string>("utteranceId"));
            int? turnId = payload?.Value<int?>("neurosync_turn_id") ?? payload?.Value<int?>("turn_id");
            int? epoch = payload?.Value<int?>("epoch");
            int? sequence = payload?.Value<int?>("sequence");
            return new LipSyncResponseOwner(responseId, turnId, epoch, sequence);
        }

        private void PublishSpeechStateChanged(
            string participantId,
            bool isSpeaking,
            in LipSyncResponseOwner owner)
        {
            if (_eventHub == null)
            {
                _logger.Debug("EventHub is null - skipping CharacterSpeechStateChanged publish",
                    LogCategory.Events);
                return;
            }

            string characterId = ResolveCharacterId(participantId);
            DateTime timestamp = DateTime.UtcNow;
            _eventHub.Publish(new LipSyncResponseLifecycleChanged(
                characterId,
                participantId,
                isSpeaking,
                owner,
                timestamp));

            string projectionResponseId = _characterResponseState.ResolveProjectionResponseId(participantId, owner);

            var speechEvent = new CharacterSpeechStateChanged(
                characterId,
                isSpeaking,
                timestamp,
                projectionResponseId);
            _eventHub.Publish(speechEvent);
            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Events))
                _logger.Debug(
                $"Published CharacterSpeechStateChanged: characterId={characterId}, isSpeaking={isSpeaking}, owner={owner.CanonicalKey}",
                LogCategory.Events);
        }

        private void HandleCharacterTurnCompleted(string participantId, string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                _logger.Warning("Received bot-turn-completed with null json.", LogCategory.Character);
                return;
            }

            JObject envelope = JObject.Parse(json);
            var payload = (envelope["payload"] as JObject ?? envelope["data"] as JObject ?? envelope)
                .ToObject<BotTurnCompletedPayload>();
            if (payload == null)
            {
                _logger.Warning("Received bot-turn-completed with null payload.", LogCategory.Character);
                return;
            }

            if (_eventHub == null)
            {
                _logger.Debug("EventHub is null - skipping CharacterTurnCompleted publish",
                    LogCategory.Events);
                return;
            }

            string resolvedParticipantId = ResolveParticipantId(participantId);
            string characterId = ResolveCharacterId(resolvedParticipantId, true);
            if (string.IsNullOrWhiteSpace(characterId) && string.IsNullOrWhiteSpace(resolvedParticipantId))
            {
                _logger.Warning(
                    "Dropping CharacterTurnCompleted publish: unable to resolve characterId or participantId.",
                    LogCategory.Character);
                return;
            }

            var turnCompletedEvent = CharacterTurnCompleted.Create(
                characterId,
                resolvedParticipantId,
                payload.WasInterrupted,
                payload.ResponseId
            );
            _eventHub.Publish(turnCompletedEvent);
            _characterResponseState.Clear(resolvedParticipantId, payload.ResponseId);
            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Events))
                _logger.Debug(
                $"Published CharacterTurnCompleted: characterId={characterId}, interrupted={payload.WasInterrupted}",
                LogCategory.Events);
        }

        private void HandleCharacterEmotion(string participantId, RTVIBotEmotionMessage payload)
        {
            if (payload == null)
            {
                _logger.Warning("Received bot-emotion with null payload.", LogCategory.Character);
                return;
            }

            string emotion = payload.Emotion ?? "neutral";
            // Backend always sends scale 1-3 (default 2). Guard against a missing/0 scale so it
            // falls back to mid intensity instead of clamping down to subtle (1) downstream.
            int intensity = payload.Scale > 0 ? payload.Scale : 2;

            if (_logger.IsEnabled(LogLevel.Info, LogCategory.Character))
                _logger.Info(
                $"Character emotion received for participant {participantId}: {emotion} (intensity: {intensity})",
                LogCategory.Character);

            if (_eventHub == null)
            {
                _logger.Debug("EventHub is null - skipping CharacterEmotionChanged publish",
                    LogCategory.Events);
                return;
            }

            string characterId = ResolveCharacterId(participantId);
            DateTime timestamp = payload.TimestampMilliseconds.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(payload.TimestampMilliseconds.Value).UtcDateTime
                : DateTime.UtcNow;
            var emotionEvent = new CharacterEmotionChanged(characterId, emotion, intensity, timestamp,
                payload.Sequence ?? -1, payload.UtteranceId ?? string.Empty,
                payload.Confidence ?? 1f, payload.DurationMilliseconds ?? 0);
            _eventHub.Publish(emotionEvent);
            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Events))
                _logger.Debug(
                $"Published CharacterEmotionChanged: characterId={characterId}, emotion={emotion}, intensity={intensity}",
                LogCategory.Events);
        }

        private void HandleCharacterTranscription(
            ProtocolMessage<BotTranscriptionPayload> message,
            TranscriptSegmentSourceKind sourceKind,
            TranscriptLifecycle lifecycle)
        {
            string participantId = ResolveParticipantId(message.Packet.ParticipantId);
            BotTranscriptionPayload payload = message.Payload;
            if (payload == null) return;

            string text = payload.Text ?? string.Empty;
            bool isFinal = lifecycle != TranscriptLifecycle.Streaming;

            if (_playerConversationInput.ShouldSuppressMirroredCharacterText(text))
            {
                LogCharacterTranscription("Suppressed typed text echo", participantId, sourceKind, lifecycle,
                    text.Length, LogCategory.Character);
                return;
            }

            LogCharacterTranscription("Received", participantId, sourceKind, lifecycle, text.Length,
                LogCategory.Character);

            if (_eventHub == null)
            {
                _logger.Warning("EventHub is null - cannot publish CharacterTranscriptReceived event!",
                    LogCategory.Events);
                return;
            }

            (string characterId, string characterName) = ResolveCharacterInfo(participantId);
            if (string.IsNullOrWhiteSpace(characterId) && string.IsNullOrWhiteSpace(participantId))
            {
                _logger.Warning(
                    "Dropping CharacterTranscriptReceived publish: unable to resolve characterId or participantId.",
                    LogCategory.Character);
                return;
            }

            var transcriptMessage = TranscriptMessage.Create(
                characterId,
                characterName,
                text,
                isFinal,
                participantId: participantId
            );
            CharacterResponseState.TranscriptIdentity identity = _characterResponseState.ResolveTranscriptIdentity(
                participantId,
                payload.ResponseId,
                payload.MessageId,
                payload.TurnId,
                message.Envelope.Id);
            _eventHub.Publish(new CharacterTranscriptReceived(
                transcriptMessage,
                identity.TurnId,
                identity.MessageId,
                identity.ResponseId,
                sourceKind,
                lifecycle,
                message.Envelope.Id,
                payload.Spoken ?? sourceKind != TranscriptSegmentSourceKind.BotLlmPreview,
                payload.AggregatedBy,
                payload.SegmentId,
                ResolveSpokenProgress(payload)));
            LogCharacterTranscription("Published CharacterTranscriptReceived", participantId, sourceKind, lifecycle,
                text.Length, LogCategory.Events);
        }

        // Voices that report word timings resend each bot-output segment once per spoken word, split at
        // that word. The split rides next to the text instead of replacing it, so lifecycle, identity and
        // the room transcript keep coming from the text alone.
        private static TranscriptSpokenProgress? ResolveSpokenProgress(BotTranscriptionPayload payload)
        {
            if (payload.SpokenStatus == null && payload.SpokenProgress == null) return null;

            return new TranscriptSpokenProgress(
                ParseSpokenStatus(payload.SpokenStatus),
                payload.SpokenProgress?.AccumulatedText,
                payload.SpokenProgress?.RemainingText);
        }

        private static TranscriptSpokenStatus ParseSpokenStatus(string status) =>
            status switch
            {
                "new" => TranscriptSpokenStatus.New,
                "in-progress" => TranscriptSpokenStatus.InProgress,
                "completed" => TranscriptSpokenStatus.Completed,
                _ => TranscriptSpokenStatus.Unknown
            };

        private void LogCharacterTranscription(string action, string participantId,
            TranscriptSegmentSourceKind sourceKind, TranscriptLifecycle lifecycle, int textLength, LogCategory category)
        {
            if (!_logger.IsEnabled(LogLevel.Debug, category)) return;

            // Constant names: enum ToString allocates on this per-segment path.
            _logger.Debug(
                $"{action} character transcription: source={TranscriptDiagnosticNames.Source(sourceKind)}, lifecycle={TranscriptDiagnosticNames.Lifecycle(lifecycle)}, participant={participantId}, length={textLength}",
                category);
        }

        private void LogCharacterTtsText(string action, string participantId, int textLength, LogCategory category)
        {
            if (!_logger.IsEnabled(LogLevel.Debug, category)) return;

            _logger.Debug($"{action} character TTS text: participant={participantId}, length={textLength}", category);
        }

        private void HandleServerMessage(ProtocolMessage message)
        {
            if (message.Envelope.Payload is not JObject payload)
            {
                _logger.Warning("Received server-message without payload.", LogCategory.Transport);
                return;
            }

            string innerType = payload.Value<string>("type") ?? string.Empty;
            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                _logger.Debug($"Received server-message with type: {innerType}", LogCategory.Transport);

            if (_lipSyncIngress.TryHandleServerMessage(
                    innerType,
                    payload,
                    message.Packet.ParticipantId))
                return;

            switch (innerType)
            {
                case "bot-started-speaking":
                    HandleCharacterStartedSpeaking(message.Packet.ParticipantId, payload);
                    break;
                case "bot-stopped-speaking":
                    HandleCharacterStoppedSpeaking(message.Packet.ParticipantId, payload);
                    break;
                case "bot-turn-completed":
                    HandleCharacterTurnCompleted(message.Packet.ParticipantId, payload.ToString(Formatting.None));
                    break;
                case "behavior-tree-response":
                    {
                        var data = payload.ToObject<BehaviorTreeResponsePayload>();
                        if (data != null && !string.IsNullOrEmpty(data.NarrativeSectionId))
                        {
                            string participantId = message.Packet.ParticipantId ?? string.Empty;
                            string sectionDisplay = FormatSectionForLogging(data.NarrativeSectionId);

                            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Character))
                                _logger.Debug(
                                $"Behavior tree response received - Section: {sectionDisplay}, " +
                                $"BT Code: {(string.IsNullOrEmpty(data.BtCode) ? "None" : "Present")}, " +
                                $"BT Constants: {(string.IsNullOrEmpty(data.BtConstants) ? "None" : "Present")}",
                                LogCategory.Character);

                            string characterId = ResolveCharacterId(participantId);

                            if (_eventHub != null)
                            {
                                var sectionChangedEvent = NarrativeSectionChanged.Create(
                                    data.NarrativeSectionId,
                                    characterId,
                                    participantId,
                                    data.BtCode,
                                    data.BtConstants
                                );
                                _eventHub.Publish(sectionChangedEvent);
                                if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Events))
                                    _logger.Debug(
                                    $"Published NarrativeSectionChanged: section={sectionDisplay}, characterId={characterId}",
                                    LogCategory.Events);
                            }
                        }

                        break;
                    }
                case "final-user-transcription":
                    {
                        var data = payload.ToObject<FinalUserTranscriptionPayload>();
                        _playerConversationInput.HandleProcessedFinal(data);

                        break;
                    }
                case "bot-emotion":
                    {
                        var data = payload.ToObject<RTVIBotEmotionMessage>();
                        HandleCharacterEmotion(message.Packet.ParticipantId, data);
                        break;
                    }
                case "usage-limit-reached":
                    {
                        var data = payload.ToObject<UsageLimitReachedPayload>();
                        if (data != null)
                        {
                            if (_logger.IsEnabled(LogLevel.Warning, LogCategory.Transport))
                                _logger.Warning($"Usage limit reached: {data.QuotaType} - {data.Message}",
                                LogCategory.Transport);
                            _eventHub?.Publish(UsageLimitReached.Create(
                                data.QuotaType ?? "unknown",
                                data.Message ?? string.Empty
                            ));
                            _eventHub?.Publish(SessionError.Create(
                                SessionErrorCodes.ServerUsageLimitReached,
                                $"Usage limit reached ({data.QuotaType}): {data.Message}",
                                isRecoverable: false
                            ));
                        }

                        break;
                    }
                case "user-idle-warning":
                    {
                        var data = payload.ToObject<UserIdleWarningPayload>();
                        if (data != null)
                        {
                            if (_logger.IsEnabled(LogLevel.Warning, LogCategory.Transport))
                                _logger.Warning(
                                $"User idle warning: remaining_seconds={data.RemainingSeconds}, message={data.Message}",
                                LogCategory.Transport);
                            _eventHub?.Publish(UserIdleWarningReceived.Create(
                                data.RemainingSeconds,
                                data.Message ?? string.Empty
                            ));
                        }

                        break;
                    }
                case "llm-no-response":
                    {
                        var data = payload.ToObject<LlmNoResponsePayload>();
                        string participantId = message.Packet.ParticipantId ?? string.Empty;
                        string characterId = ResolveCharacterId(participantId, true);
                        string reason = data?.Reason ?? string.Empty;

                        if (_logger.IsEnabled(LogLevel.Info, LogCategory.Character))
                            _logger.Info(
                            $"LLM no-response received. characterId={characterId}, participantId={participantId}, reason={reason}",
                            LogCategory.Character);
                        _eventHub?.Publish(LlmNoResponseReceived.Create(characterId, participantId, reason));

                        break;
                    }
                case "interaction-created":
                    {
                        var data = payload.ToObject<InteractionCreatedPayload>();
                        if (data != null)
                        {
                            string participantId = message.Packet.ParticipantId ?? string.Empty;
                            string characterId = ResolveCharacterId(participantId, true);
                            string interactionId = data.InteractionId ?? string.Empty;
                            string characterSessionId = data.CharacterSessionId ?? string.Empty;
                            _characterResponseState.RegisterInteraction(participantId, interactionId);

                            if (_logger.IsEnabled(LogLevel.Info, LogCategory.Character))
                                _logger.Info(
                                $"Interaction created. characterId={characterId}, participantId={participantId}, interactionId={interactionId}, characterSessionId={characterSessionId}",
                                LogCategory.Character);
                            _eventHub?.Publish(InteractionCreated.Create(
                                characterId,
                                participantId,
                                interactionId,
                                characterSessionId));
                        }

                        break;
                    }
                case "vad-stt-started":
                    {
                        _logger.Info("Backend VAD STT started listening.", LogCategory.Player);
                        _eventHub?.Publish(VadSttStateChanged.Started());
                        break;
                    }
                case "vad-stt-stopped":
                    {
                        _logger.Info("Backend VAD STT stopped listening.", LogCategory.Player);
                        _eventHub?.Publish(VadSttStateChanged.Stopped());
                        break;
                    }
                case "visemes":
                    {
                        var data = payload.ToObject<VisemesPayload>();
                        if (data?.Visemes != null && data.Visemes.Count > 0)
                        {
                            string participantId = message.Packet.ParticipantId ?? string.Empty;
                            string characterId = ResolveCharacterId(participantId);
                            var visemes = new Dictionary<string, float>(data.Visemes);

                            _eventHub?.Publish(VisemesReceived.Create(characterId, participantId, visemes));
                        }

                        break;
                    }
                case "action-response":
                    {
                        PublishActionResponse(message.Packet.ParticipantId, payload);
                        break;
                    }
                case "moderation-response":
                    {
                        var data = payload.ToObject<ModerationResponsePayload>();
                        if (data != null)
                        {
                            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                                _logger.Debug(
                                $"Moderation response: flagged={data.Result}, reason={data.Reason}",
                                LogCategory.Transport);
                            _eventHub?.Publish(ModerationResponseReceived.Create(
                                data.Result,
                                data.UserInput ?? string.Empty,
                                data.Reason ?? string.Empty
                            ));
                        }

                        break;
                    }
                case "server-response":
                    {
                        HandleServerResponse(payload, message.Packet.ParticipantId);
                        break;
                    }
                case "character-status":
                    {
                        HandleCharacterStatusPayload(payload, message.Packet.ParticipantId);
                        break;
                    }
                case "character-removed":
                    {
                        HandleCharacterRemovedPayload(payload);
                        break;
                    }
                default:
                    {
                        if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                            _logger.Debug($"Unhandled server-message type: {innerType}",
                            LogCategory.Transport);
                        break;
                    }
            }
        }

        private void HandleActionResponse(ProtocolMessage message)
        {
            JObject payload = message.Envelope.Payload as JObject ?? message.Envelope.Raw;
            PublishActionResponse(message.Packet.ParticipantId, payload);
        }

        private void PublishActionResponse(string participantId, JObject payload)
        {
            string safeParticipantId = participantId ?? string.Empty;
            string characterId = ResolveCharacterId(safeParticipantId);
            var drops = new ConvaiActionDropCollector();
            bool parsed = ConvaiActionResponseParser.TryParseBatch(
                payload,
                out IReadOnlyList<ConvaiActionCommand> rawActions,
                out int skippedActions);
            if (!parsed)
            {
                rawActions = Array.Empty<ConvaiActionCommand>();
                skippedActions = 1;
            }

            if (skippedActions > 0)
            {
                if (drops.WantsDetail)
                    drops.Add(ConvaiActionDropReportFactory.MalformedEntry(skippedActions));

                drops.Count(
                    ConvaiActionDropReason.MalformedEntry,
                    drops.WantsDetail ? skippedActions - 1 : skippedActions);
            }

            IReadOnlyList<ConvaiActionCommand> actions = Array.Empty<ConvaiActionCommand>();
            if (TryResolveCharacterAgent(safeParticipantId, out IConvaiCharacterAgent agent) &&
                agent is IConvaiActionRuntimeSource actionRuntimeSource)
            {
                IReadOnlyList<ConvaiActionDefinition> executableCatalog =
                    actionRuntimeSource is IConvaiActionDefinitionCatalogSource catalogSource
                        ? catalogSource.ActionDefinitionCatalog
                        : actionRuntimeSource.ActionDefinitions;
                // Local-resolution view: a command may name a scene target that was never part of
                // the confirmed wire config (ConvaiActionTarget components, target groups), and it
                // must still resolve. Falls back to the confirmed config when unavailable.
                var resolutionSource = actionRuntimeSource as IConvaiActionResolutionSource;
                ConvaiActionConfig resolutionConfig =
                    resolutionSource?.ResolutionActionConfig ?? actionRuntimeSource.ActionConfig;

                // The character's position travels with the config: without it the filter keeps the
                // first of two same-named targets while the dispatcher walks to the nearest, so a
                // command could be admitted on one target and performed on another.
                actions = ConvaiActionResponseParser.FilterExecutableBatch(
                    rawActions,
                    resolutionConfig,
                    executableCatalog,
                    drops,
                    resolutionSource?.ResolutionOrigin);
            }
            else if (rawActions.Count > 0)
            {
                if (drops.WantsDetail)
                    drops.Add(ConvaiActionDropReportFactory.RuntimeSourceUnavailable(
                        rawActions.Count, characterId));

                drops.Count(
                    ConvaiActionDropReason.RuntimeSourceUnavailable,
                    drops.WantsDetail ? rawActions.Count - 1 : rawActions.Count);
            }

            if (_logger.IsEnabled(LogLevel.Info, LogCategory.Actions))
                _logger.Info(
                $"Action response filtered. participantId='{safeParticipantId}', characterId='{characterId}', accepted={actions.Count}, rejected={drops.DroppedCount}, reasons={FormatActionRejectionReasons(drops.CountsByReason)}",
                LogCategory.Actions);

            ReportDrops(characterId, drops);
            ReportNoActionSelected(characterId, actions.Count, drops.DroppedCount);

            _eventHub?.Publish(ConvaiActionResponseFilterDiagnostic.Create(
                characterId,
                safeParticipantId,
                actions.Count,
                drops));
            _eventHub?.Publish(CharacterActionReceived.Create(characterId, actions));
        }

        /// <summary>
        ///     Says why each dropped command was dropped, once per distinct fault.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A model that keeps asking for a target the scene does not have will ask for it
        ///         every turn, and a warning per turn buries the console rather than informing it —
        ///         while saying nothing at all is what made this class of fault undiagnosable in the
        ///         first place. Once per distinct fault per interval serves both: the first
        ///         occurrence is impossible to miss, the hundredth costs a suffix, and the one after
        ///         the author changed something is said again.
        ///     </para>
        ///     <para>
        ///         It was "once per distinct fault, for the whole connection" until this was
        ///         measured, and that is a mute rather than a throttle — see the remarks on
        ///         <see cref="_actionDropThrottle" />.
        ///     </para>
        /// </remarks>
        private void ReportDrops(string characterId, ConvaiActionDropCollector drops)
        {
            IReadOnlyList<ConvaiActionDropReport> reports = drops.Reports;
            if (reports.Count == 0)
                return;

            double nowElapsed = RepeatedMessageThrottle.ElapsedSeconds;
            for (int i = 0; i < reports.Count; i++)
            {
                ConvaiActionDropReport report = reports[i];
                if (!_actionDropThrottle.ShouldSay(
                        $"{characterId}|{report.Signature}", nowElapsed, out int suppressed))
                    continue;

                if (_logger.IsEnabled(LogLevel.Warning, LogCategory.Actions))
                    _logger.Warning(
                    $"[Actions] {report.Explanation}{DescribeSuppressedRepeats(suppressed)}",
                    LogCategory.Actions);
            }
        }

        /// <summary>
        ///     Says how many times a fault repeated while it was being kept quiet, or nothing at all
        ///     when it did not.
        /// </summary>
        /// <remarks>
        ///     The count is the part that makes a throttled line honest: without it, one line reads
        ///     as one occurrence, and "it happened once" and "it happened on all forty turns" are the
        ///     same sentence — which is the difference between a fluke and the thing to fix next.
        /// </remarks>
        private static string DescribeSuppressedRepeats(int suppressed) =>
            suppressed > 0
                ? $" (and {suppressed} more like it in the last " +
                  $"{ActionDropReportIntervalSeconds:F0}s)"
                : string.Empty;

        /// <summary>
        ///     Separates "the Convai Character chose no action" from "it chose one and the SDK
        ///     dropped it".
        /// </summary>
        /// <remarks>
        ///     Both look identical from the outside — the character talks and nothing happens — and
        ///     they need opposite investigations: one is a backend/authoring question about whether
        ///     the action was described well enough to be chosen, the other is a Unity-side wiring or
        ///     naming problem. Without this line the two are indistinguishable, which is how time
        ///     gets spent checking a scene that was never at fault.
        /// </remarks>
        private void ReportNoActionSelected(string characterId, int acceptedCount, int droppedCount)
        {
            if (acceptedCount > 0 || droppedCount > 0)
                return;

            if (!_actionDropThrottle.ShouldSay(
                    $"{characterId}|no-action-selected", RepeatedMessageThrottle.ElapsedSeconds, out _))
                return;

            _logger.Info(
                "[Actions] The Convai Character replied without choosing an action. Nothing was " +
                "dropped — none was selected. If one was expected, the action's description is what " +
                "the character reads when deciding, so that is where to look first.",
                LogCategory.Actions);
        }

        private static string FormatActionRejectionReasons(IReadOnlyDictionary<string, int> reasons)
        {
            if (reasons == null || reasons.Count == 0)
                return "none";

            var keys = new List<string>(reasons.Keys);
            keys.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) builder.Append(',');
                string key = keys[i];
                builder.Append(key).Append(':').Append(reasons[key]);
            }

            return builder.ToString();
        }

        private void HandleServerResponse(JObject payload, string participantId = null)
        {
            if (payload == null) return;

            string eventType = payload.Value<string>("event_type") ?? string.Empty;
            JObject extras = payload["extras"] as JObject ?? new JObject();
            CopyCommonServerResponseFieldsToExtras(payload, extras);

            // Server-response event_type casing is not part of the backend contract; normalize once
            // so every typed acknowledgement route uses the same matching behavior.
            switch (eventType.ToLowerInvariant())
            {
                case "force-user-stopped-speaking":
                    PttCommits.Receive(participantId, payload);
                    return;
                case "context-update":
                    HandleDynamicContextServerResponse(payload, extras);
                    return;
                case "vision-status":
                    _eventHub?.Publish(VisionContextStatusReceived.Create(
                        payload.Value<string>("status"),
                        payload.Value<string>("message"),
                        extras));
                    return;
                case "vision-trigger":
                    _eventHub?.Publish(VisionContextTriggerReceived.Create(
                        payload.Value<string>("status"),
                        payload.Value<string>("message"),
                        extras));
                    return;
                case "respond-mode-update":
                    CopyServerResponseFieldToExtras(payload, extras, "modality");
                    CopyServerResponseFieldToExtras(payload, extras, "mode");
                    _eventHub?.Publish(RespondModeUpdateResultReceived.Create(
                        payload.Value<string>("status"),
                        payload.Value<string>("message"),
                        extras));
                    return;
                case "interaction-target":
                    HandleInteractionTargetResponse(payload, extras);
                    return;
                case "character-roster-update":
                    HandleCharacterRosterUpdateResponse(payload, extras);
                    return;
                default:
                    if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Transport))
                        _logger.Debug($"Unhandled server-response event_type: {eventType}",
                        LogCategory.Transport);
                    return;
            }
        }

        private void HandleInteractionTargetResponse(JObject payload, JObject extras)
        {
            if (_agentRegistry is not IMultiCharacterSessionRegistry registry ||
                registry.CurrentMultiCharacterSession == null) return;

            string ReadString(string name) => extras.Value<string>(name) ?? payload.Value<string>(name);
            int ReadInt(string name) => extras.Value<int?>(name) ?? payload.Value<int?>(name) ?? 0;
            bool ReadBool(string name) => extras.Value<bool?>(name) ?? payload.Value<bool?>(name) ?? false;

            registry.CurrentMultiCharacterSession.CompleteTargetCommand(
                ReadString("command_id"),
                payload.Value<string>("status"),
                payload.Value<string>("message"),
                ReadString("active_membership_id"),
                ReadString("previous_membership_id"),
                ReadInt("route_epoch"),
                ReadBool("changed"));
        }

        private void HandleCharacterRosterUpdateResponse(JObject payload, JObject extras)
        {
            if (_agentRegistry is not IMultiCharacterSessionRegistry registry ||
                registry.CurrentMultiCharacterSession == null) return;

            string ReadString(string name) => extras.Value<string>(name) ?? payload.Value<string>(name);
            int ReadInt(string name) => extras.Value<int?>(name) ?? payload.Value<int?>(name) ?? 0;

            MultiCharacterRoomSession session = registry.CurrentMultiCharacterSession;
            session.CompleteRosterCommand(
                session.ResolvePendingRosterCommandId(ReadString("command_id")),
                payload.Value<string>("status"),
                payload.Value<string>("message"),
                ReadString("code"),
                ReadMembershipDetails(extras["added"]),
                ReadMembershipDetails(extras["removed"]),
                extras["removed_membership_ids"]?.ToObject<List<string>>(),
                ReadString("active_membership_id"),
                ReadInt("route_epoch"),
                ReadInt("roster_epoch"));
        }

        private static IReadOnlyList<RoomCharacterDetails> ReadMembershipDetails(JToken token) =>
            token == null || token.Type == JTokenType.Null
                ? Array.Empty<RoomCharacterDetails>()
                : token.ToObject<List<RoomCharacterDetails>>() ??
                  (IReadOnlyList<RoomCharacterDetails>)Array.Empty<RoomCharacterDetails>();

        private void HandleDynamicContextServerResponse(JObject payload, JObject extras)
        {
            // Common server-response fields are normalized before lane-specific handlers run.
            // Context-update adds token, budget, revision, and prompt-rebuild fields for its event.
            CopyServerResponseFieldToExtras(payload, extras, "context_revision");
            CopyServerResponseFieldToExtras(payload, extras, "revision");
            CopyServerResponseFieldToExtras(payload, extras, "token_count");
            CopyServerResponseFieldToExtras(payload, extras, "word_count");
            CopyServerResponseFieldToExtras(payload, extras, "static_token_count");
            CopyServerResponseFieldToExtras(payload, extras, "runtime_token_count");
            CopyServerResponseFieldToExtras(payload, extras, "remaining_tokens");
            CopyServerResponseFieldToExtras(payload, extras, "remaining_words");
            CopyServerResponseFieldToExtras(payload, extras, "interrupted");
            CopyServerResponseFieldToExtras(payload, extras, "prompt_rebuild");
            CopyServerResponseFieldToExtras(payload, extras, "action_config_updated");
            CopyServerResponseFieldToExtras(payload, extras, "action_config_created");
            CopyServerResponseFieldToExtras(payload, extras, "actions_count");
            CopyServerResponseFieldToExtras(payload, extras, "objects_count");
            CopyServerResponseFieldToExtras(payload, extras, "characters_count");
            CopyServerResponseFieldToExtras(payload, extras, "current_attention_object");
            CopyServerResponseFieldToExtras(payload, extras, "current_attention_object_cleared");
            CopyServerResponseFieldToExtras(payload, extras, "action_generation_strategy_changed");
            CopyServerResponseFieldToExtras(payload, extras, "action_generation_strategy_status");

            _eventHub?.Publish(DynamicContextUpdateResultReceived.Create(
                payload.Value<string>("status"),
                payload.Value<string>("message"),
                extras));
        }

        private static void CopyCommonServerResponseFieldsToExtras(JObject payload, JObject extras)
        {
            CopyServerResponseFieldToExtras(payload, extras, "update_id");
            CopyServerResponseFieldToExtras(payload, extras, "requested_run_llm");
            CopyServerResponseFieldToExtras(payload, extras, "actual_run_llm");
            CopyServerResponseFieldToExtras(payload, extras, "downgrade_reason");
            CopyServerResponseFieldToExtras(payload, extras, "llm_triggered");
            CopyServerResponseFieldToExtras(payload, extras, "downgraded");
        }

        private static void CopyServerResponseFieldToExtras(JObject payload, JObject extras, string fieldName)
        {
            if (payload[fieldName] == null || extras[fieldName] != null) return;
            extras[fieldName] = payload[fieldName].DeepClone();
        }

        private void HandlePipelineError(ProtocolMessage message)
        {
            if (message.Envelope.Payload is not JObject payload)
            {
                _logger.Warning("Received error message without payload.", LogCategory.Transport);
                return;
            }

            string errorText = payload.Value<string>("error") ?? "Unknown pipeline error";
            bool isFatal = payload.Value<bool>("fatal");

            if (isFatal)
                {
                    if (_logger.IsEnabled(LogLevel.Error, LogCategory.Transport))
                        _logger.Error($"Fatal pipeline error: {errorText}", LogCategory.Transport);
                }
            else
                {
                    if (_logger.IsEnabled(LogLevel.Warning, LogCategory.Transport))
                        _logger.Warning($"Pipeline error: {errorText}", LogCategory.Transport);
                }

            string errorCode = isFatal
                ? SessionErrorCodes.ServerFatalError
                : SessionErrorCodes.ServerError;

            _eventHub?.Publish(SessionError.Create(
                errorCode,
                errorText,
                isRecoverable: !isFatal
            ));
        }

        /// <summary>
        ///     Resolves a characterId from a participantId via the registry.
        ///     Falls back to the participantId itself when resolution fails.
        ///     When <paramref name="fallbackToFirst" /> is true and participantId is empty,
        ///     returns the first registered character's ID (common for bot-ready messages).
        /// </summary>
        private string ResolveCharacterId(string participantId, bool fallbackToFirst = false)
        {
            if (TryResolveCharacterAgent(participantId, out IConvaiCharacterAgent agent))
                return agent.CharacterId;

            if (fallbackToFirst && TryResolveSingleCharacter(out agent))
                return agent.CharacterId;

            if (fallbackToFirst && _agentRegistry != null && string.IsNullOrEmpty(participantId))
            {
                IReadOnlyList<IConvaiCharacterAgent> allCharacters = _agentRegistry.Characters;
                if (allCharacters.Count > 0) return allCharacters[0].CharacterId;
            }

            return participantId ?? string.Empty;
        }

        /// <summary>
        ///     Resolves a characterId and display name from a participantId via the registry.
        ///     Returns the participantId as characterId and "Character" as display name when resolution fails.
        /// </summary>
        private (string CharacterId, string DisplayName) ResolveCharacterInfo(string participantId)
        {
            if (TryResolveCharacterAgent(participantId, out IConvaiCharacterAgent agent) ||
                TryResolveSingleCharacter(out agent))
            {
                string name = !string.IsNullOrEmpty(agent.CharacterName)
                    ? agent.CharacterName
                    : agent.CharacterId;
                return (agent.CharacterId, name);
            }

            return (participantId ?? string.Empty, "Character");
        }

        /// <summary>
        ///     Normalizes missing participant IDs when the room contains exactly one character
        ///     with an established transport binding. Some RTVI projections omit participant
        ///     identity on early packets and add it on later packets for the same response.
        /// </summary>
        private string ResolveParticipantId(string participantId)
        {
            if (!string.IsNullOrWhiteSpace(participantId))
            {
                // In a single-character room, identity may appear only after anonymous
                // response packets have already established correlation state.
                if (TryResolveSingleCharacter(out _))
                    _characterResponseState.PromoteAnonymousParticipant(participantId);

                return participantId;
            }

            if (_agentRegistry == null || !TryResolveSingleCharacter(out IConvaiCharacterAgent agent))
                return string.Empty;

            return _agentRegistry.TryGetParticipantId(agent.CharacterId, out string mappedParticipantId) &&
                   !string.IsNullOrWhiteSpace(mappedParticipantId)
                ? mappedParticipantId
                : string.Empty;
        }

        private bool TryResolveCharacterAgent(string participantId, out IConvaiCharacterAgent agent)
        {
            agent = null;
            if (_agentRegistry == null || string.IsNullOrWhiteSpace(participantId)) return false;

            if (_agentRegistry is IMultiCharacterSessionRegistry multiRegistry &&
                multiRegistry.CurrentMultiCharacterSession is { } multiSession)
            {
                CharacterRoomMembership membership = multiSession.Resolve(
                    null,
                    participantId,
                    participantId);
                if (membership?.Character != null)
                {
                    agent = membership.Character;
                    return true;
                }

                CharacterRoomMembership uniqueCharacter = multiSession.FindUniqueByCharacterId(participantId);
                if (uniqueCharacter?.Character != null)
                {
                    agent = uniqueCharacter.Character;
                    return true;
                }

                return false;
            }

            if (_agentRegistry.TryGetCharacterByParticipantId(participantId, out agent)) return agent != null;

            // Some transports surface identity/characterId instead of participant SID.
            return _agentRegistry.TryGetCharacter(participantId, out agent) && agent != null;
        }

        private bool TryResolveSingleCharacter(out IConvaiCharacterAgent agent)
        {
            agent = null;
            if (_agentRegistry == null) return false;

            IReadOnlyList<IConvaiCharacterAgent> allCharacters = _agentRegistry.Characters;
            if (allCharacters == null || allCharacters.Count != 1) return false;

            agent = allCharacters[0];
            return agent != null && !string.IsNullOrWhiteSpace(agent.CharacterId);
        }

        private void HandleCharacterReady(ProtocolMessage message)
        {
            string participantId = message.Packet.ParticipantId;
            if (string.IsNullOrEmpty(participantId))
                _logger.Info("Received bot-ready (participant ID not available)", LogCategory.Character);
            else
            {
                if (_logger.IsEnabled(LogLevel.Info, LogCategory.Character))
                    _logger.Info($"Received bot-ready for participant: {participantId}",
                    LogCategory.Character);
            }

            CharacterRoomMembership membership = ResolveMembership(message.Envelope.Payload, participantId);
            MarkAndPublishCharacterReady(membership, participantId, "bot-ready");
        }

        private void MarkAndPublishCharacterReady(
            CharacterRoomMembership membership,
            string participantId,
            string signalName)
        {
            string normalizedParticipantId = participantId ?? string.Empty;
            if (membership != null &&
                _agentRegistry is IMultiCharacterSessionRegistry registry)
            {
                registry.CurrentMultiCharacterSession?.MarkReady(membership, normalizedParticipantId);
                if (!string.IsNullOrWhiteSpace(normalizedParticipantId))
                    _agentRegistry.SetParticipantId(membership.CharacterId, normalizedParticipantId);
            }

            if (_eventHub == null) return;

            string characterId = membership?.CharacterId ?? ResolveCharacterId(normalizedParticipantId, true);
            if (_logger.IsEnabled(LogLevel.Debug, LogCategory.Character))
                _logger.Debug(
                $"Resolved {signalName}: participantId='{participantId ?? "(null)"}' -> characterId='{characterId}'",
                LogCategory.Character);

            var characterReadyEvent = membership == null
                ? CharacterReady.Create(characterId, normalizedParticipantId)
                : CharacterReady.Create(
                    characterId,
                    normalizedParticipantId,
                    membership.MembershipId,
                    membership.CharacterSessionId,
                    membership.ParticipantIdentity);
            _eventHub.Publish(characterReadyEvent);
            if (_logger.IsEnabled(LogLevel.Info, LogCategory.Events))
                _logger.Info($"Published CharacterReady event: characterId={characterId}",
                LogCategory.Events);
        }

        private void HandleCharacterStatus(ProtocolMessage message)
        {
            HandleCharacterStatusPayload(message.Envelope.Payload as JObject, message.Packet.ParticipantId);
        }

        private void HandleCharacterStatusPayload(JObject payload, string participantId)
        {
            JObject data = payload?["data"] as JObject ?? payload;
            JObject about = data?["about"] as JObject ?? data;
            if (about == null || _agentRegistry is not IMultiCharacterSessionRegistry registry) return;
            MultiCharacterRoomSession session = registry.CurrentMultiCharacterSession;
            if (session == null) return;

            string status = data?.Value<string>("status") ?? about?.Value<string>("status");
            RoomCharacterDetails details = CreateLifecycleMembershipDetails(about, status);
            CharacterRoomMembership membership = session.Resolve(
                about.Value<string>("membership_id"),
                about.Value<string>("participant_identity"),
                participantId);
            membership ??= session.UpsertMembership(details, _agentRegistry.Characters);
            if (membership == null) return;

            session.ObserveRosterEpoch(about.Value<int?>("roster_epoch") ?? data?.Value<int?>("roster_epoch") ?? 0);
            if (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                string subjectParticipantId = ResolveCharacterStatusParticipantId(
                    session,
                    membership,
                    participantId);
                MarkAndPublishCharacterReady(membership, subjectParticipantId, "character-status");
                return;
            }
            if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "dispatch_failed", StringComparison.OrdinalIgnoreCase)) return;

            string failureCode = data?.Value<string>("failure_code") ?? about?.Value<string>("failure_code");
            session.MarkFailed(membership, failureCode);
        }

        private string ResolveCharacterStatusParticipantId(
            MultiCharacterRoomSession session,
            CharacterRoomMembership membership,
            string relayParticipantId)
        {
            if (!string.IsNullOrWhiteSpace(membership?.ParticipantId))
                return membership.ParticipantId;
            if (string.IsNullOrWhiteSpace(relayParticipantId))
                return string.Empty;

            CharacterRoomMembership relayMembership = session?.Resolve(null, null, relayParticipantId);
            if (ReferenceEquals(relayMembership, membership))
                return relayParticipantId;
            if (_agentRegistry.TryGetCharacterByParticipantId(
                    relayParticipantId,
                    out IConvaiCharacterAgent relayCharacter) &&
                ReferenceEquals(relayCharacter, membership?.Character))
                return relayParticipantId;

            // Legacy single-character rooms used the packet sender as the ready participant.
            // In a roster room the coordinator may relay status for another membership, so
            // an unverified sender must never become the subject character's participant SID.
            return session?.Characters.Count == 1 ? relayParticipantId : string.Empty;
        }

        private void HandleCharacterRemoved(ProtocolMessage message) =>
            HandleCharacterRemovedPayload(message.Envelope.Payload as JObject);

        private void HandleCharacterRemovedPayload(JObject payload)
        {
            if (_agentRegistry is not IMultiCharacterSessionRegistry registry) return;
            JObject data = payload?["data"] as JObject ?? payload;
            registry.CurrentMultiCharacterSession?.RemoveMembership(
                data?.Value<string>("membership_id"),
                data?.Value<int?>("roster_epoch") ?? -1);
        }

        private static RoomCharacterDetails CreateLifecycleMembershipDetails(JObject about, string status)
        {
            if (about == null) return null;
            var details = new JObject
            {
                ["membership_id"] = about.Value<string>("membership_id"),
                ["character_id"] = about.Value<string>("character_id"),
                ["session_id"] = about.Value<string>("session_id"),
                ["character_session_id"] = about.Value<string>("character_session_id"),
                ["participant_identity"] = about.Value<string>("participant_identity"),
                ["is_initial"] = about.Value<bool?>("is_initial") ?? false,
                ["provisioning_status"] = status ?? about.Value<string>("provisioning_status"),
                ["failure_code"] = about.Value<string>("failure_code")
            };
            return details.ToObject<RoomCharacterDetails>();
        }

        private CharacterRoomMembership ResolveMembership(JToken rawPayload, string participantId)
        {
            if (_agentRegistry is not IMultiCharacterSessionRegistry registry) return null;
            MultiCharacterRoomSession session = registry.CurrentMultiCharacterSession;
            if (session == null) return null;

            JObject payload = rawPayload as JObject;
            JObject data = payload?["data"] as JObject ?? payload;
            JObject about = data?["about"] as JObject ?? data;
            return session.Resolve(
                about?.Value<string>("membership_id"),
                about?.Value<string>("participant_identity"),
                participantId);
        }

        private void HandleCharacterTtsText(ProtocolMessage<BotTranscriptionPayload> message)
        {
            BotTranscriptionPayload payload = message.Payload;
            string participantId = ResolveParticipantId(message.Packet.ParticipantId);
            string text = payload?.Text ?? string.Empty;

            if (_playerConversationInput.ShouldSuppressMirroredCharacterText(text))
            {
                LogCharacterTtsText("Suppressed typed text echo", participantId, text.Length, LogCategory.Character);
                return;
            }

            LogCharacterTtsText("Received", participantId, text.Length, LogCategory.Character);

            if (_eventHub == null)
            {
                _logger.Warning("EventHub is null - cannot publish CharacterTtsTextChunk event!",
                    LogCategory.Events);
                return;
            }

            // Avoid publishing CharacterTranscriptReceived here to prevent duplicate transcript events.
            var botTtsTextEvent = CharacterTtsTextChunk.Create(participantId, text);
            _eventHub.Publish(botTtsTextEvent);
            LogCharacterTtsText("Published CharacterTtsTextChunk", participantId, text.Length, LogCategory.Events);
        }

        private static string ResolveFirstNonEmpty(params string[] values)
        {
            if (values == null) return string.Empty;

            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    return value;

            return string.Empty;
        }

        /// <summary>
        ///     Formats a section ID for logging, including the human-readable name if available.
        /// </summary>
        /// <param name="sectionId">The section ID to format.</param>
        /// <returns>A formatted string like '"Section Name" (id)' or just the ID if name is unavailable.</returns>
        private string FormatSectionForLogging(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId)) return "(none)";

            if (_sectionNameResolver != null &&
                _sectionNameResolver.TryGetSectionName(sectionId, out string sectionName))
                return $"\"{sectionName}\" ({sectionId})";

            return sectionId;
        }
    }
}
