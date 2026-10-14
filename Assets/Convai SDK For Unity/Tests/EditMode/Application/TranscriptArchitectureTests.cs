using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Application.Services.Transcript;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Models;
using Convai.Runtime.Facades;
using Convai.Runtime.Settings;
using Convai.Shared.Abstractions;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Application
{
    public class TranscriptArchitectureTests
    {
        [Test]
        public void TimelineTurns_ReusesOrderedCollectionAndPreservesRoomSequence()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "first", "typed-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "second", false,
                turnId: "turn-2", messageId: "turn-2"));
            TranscriptTimeline timeline = TranscriptTimeline.FromSnapshot(engine.CurrentTimeline);

            IReadOnlyList<TranscriptTurn> firstRead = timeline.Turns;
            IReadOnlyList<TranscriptTurn> secondRead = timeline.Turns;

            Assert.AreSame(firstRead, secondRead);
            CollectionAssert.AreEqual(new[] { "typed-1", "turn-2" }, firstRead.Select(turn => turn.Id).ToArray());
            Assert.AreSame(timeline.CommittedTurns.Single(), firstRead[0]);
            Assert.AreSame(timeline.ActiveTurns.Single(), firstRead[1]);
            Assert.Greater(firstRead[1].RoomSequence, firstRead[0].RoomSequence);
        }

        [Test]
        public void CurrentTimeline_ReusesMappedTimelineWhileEngineSnapshotIsUnchanged()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);

            TranscriptTimeline firstRead = transcripts.CurrentTimeline;
            TranscriptTimeline secondRead = transcripts.CurrentTimeline;

            Assert.AreSame(firstRead, secondRead);
        }

        [Test]
        public void CurrentTimeline_MapsNewTimelineAfterEngineChange()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            TranscriptTimeline beforeChange = transcripts.CurrentTimeline;

            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "hello", "typed-1"));
            TranscriptTimeline afterChange = transcripts.CurrentTimeline;

            Assert.AreNotSame(beforeChange, afterChange);
            Assert.AreEqual("hello", afterChange.CommittedTurns.Single().DisplayText);
        }

        [Test]
        public void VadStopLeavesPlayerTurnOpen_UntilProcessedFinalCommitsIt()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            var changes = new List<TranscriptChangeKind>();
            transcripts.Changed += batch => changes.AddRange(batch.Changes.Select(change => change.Kind));

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1",
                "You",
                "helo",
                true,
                TranscriptionPhase.AsrFinal,
                turnId: "turn-1",
                messageId: "turn-1"));
            eventHub.Publish(PlayerSpeakingStateChanged.StoppedSpeaking("turn-1"));

            TranscriptTurn active = transcripts.CurrentTimeline.ActiveTurns.Single();
            Assert.AreEqual("helo", active.DisplayText);
            Assert.AreEqual(TranscriptTurnState.Stable, active.State);
            Assert.AreEqual(0, changes.Count(kind => kind == TranscriptChangeKind.Committed));

            eventHub.Publish(FinalUserTranscriptionReceived.Create(
                "hello",
                new SpeakerInfo("player-1", "You", string.Empty),
                "turn-1"));

            TranscriptTurn corrected = transcripts.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual("hello", corrected.DisplayText);
            Assert.AreEqual(TranscriptTextSource.ProcessedFinal, corrected.PrimaryTextSource);
            Assert.AreEqual(1, changes.Count(kind => kind == TranscriptChangeKind.Committed));
            Assert.AreEqual(0, changes.Count(kind => kind == TranscriptChangeKind.Corrected));
        }

        [Test]
        public void LocalPlayerName_RemainsAuthoritativeThroughProcessedFinal()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            var speakerInfo = new SpeakerInfo("srv-id", "Server Name", "p1");

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "srv-id",
                "Rishav",
                "hi",
                true,
                TranscriptionPhase.AsrFinal,
                speakerInfo: speakerInfo,
                turnId: "turn-1",
                messageId: "turn-1"));

            Assert.AreEqual("Rishav", engine.CurrentTimeline.ActiveTurns.Single().Participant.DisplayName);

            eventHub.Publish(FinalUserTranscriptionReceived.Create(
                "hi",
                speakerInfo,
                "turn-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual("Rishav", turn.Participant.DisplayName);
            Assert.AreEqual("srv-id", turn.Participant.PlayerOrCharacterId);
            Assert.AreEqual("p1", turn.Participant.ParticipantId);
        }

        [Test]
        public void ProcessedFinalWithSpeakerAttribution_ReusesAsrSessionTurn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "local-player",
                "You",
                "Hey, hello, how are you",
                false,
                TranscriptionPhase.Interim,
                turnId: "session-1",
                messageId: "session-1"));

            var attributedSpeaker = new SpeakerInfo("speaker-1", "Player", "participant-1");
            eventHub.Publish(PlayerTranscriptReceived.Create(
                attributedSpeaker.SpeakerId,
                attributedSpeaker.SpeakerName,
                "Hey, hello—how are you doing today?",
                true,
                TranscriptionPhase.ProcessedFinal,
                speakerInfo: attributedSpeaker,
                turnId: "session-1",
                messageId: "session-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.ActiveTurns
                .Concat(engine.CurrentTimeline.CommittedTurns)
                .Single();
            Assert.AreEqual("session-1", turn.TurnId);
            Assert.AreEqual("Player", turn.Participant.DisplayName);
            Assert.AreEqual("Hey, hello—how are you doing today?", turn.DisplayText);
        }

        [Test]
        public void CoordinatorProcessedFinalAfterDirectFinal_DoesNotCreateSecondTurn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            var attributedSpeaker = new SpeakerInfo("speaker-1", "Player", "participant-1");

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "local-player",
                "You",
                "Hey, hello, how are you",
                false,
                TranscriptionPhase.Interim,
                turnId: "session-1",
                messageId: "session-1"));
            eventHub.Publish(FinalUserTranscriptionReceived.Create(
                "Hey, hello—how are you doing today?",
                attributedSpeaker,
                "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                attributedSpeaker.SpeakerId,
                attributedSpeaker.SpeakerName,
                "Hey, hello—how are you doing today?",
                true,
                TranscriptionPhase.ProcessedFinal,
                speakerInfo: attributedSpeaker,
                turnId: "session-1",
                messageId: "session-1"));

            Assert.AreEqual(0, engine.CurrentTimeline.ActiveTurns.Count);
            TranscriptTurnSnapshot turn = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual("session-1", turn.TurnId);
            Assert.AreEqual("Player", turn.Participant.DisplayName);
            Assert.AreEqual("Hey, hello—how are you doing today?", turn.DisplayText);
        }

        [Test]
        public void FinalWithoutMessageId_EnrichesLatestUnattributedPlayerTurn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "local-player",
                "You",
                "Hey, helo",
                true,
                TranscriptionPhase.AsrFinal,
                turnId: "session-1",
                messageId: "session-1"));
            eventHub.Publish(PlayerSpeakingStateChanged.StoppedSpeaking("session-1"));

            var attributedSpeaker = new SpeakerInfo("speaker-1", "Player", "participant-1");
            eventHub.Publish(FinalUserTranscriptionReceived.Create(
                "Hey, hello",
                attributedSpeaker));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual("session-1", turn.TurnId);
            Assert.AreEqual("Player", turn.Participant.DisplayName);
            Assert.AreEqual("participant-1", turn.Participant.ParticipantId);
            Assert.AreEqual("Hey, hello", turn.DisplayText);
        }

        [Test]
        public void RawBotPreviewIsExcluded_BotOutputBecomesHistory()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            TranscriptMessage message = TranscriptMessage.Create(
                "character-1",
                "Camila",
                "unsafe token",
                false,
                participantId: "participant-1",
                speakerType: SpeakerType.Character);
            eventHub.Publish(new CharacterTranscriptReceived(
                message,
                "bot-turn-1",
                sourceKind: TranscriptSegmentSourceKind.BotLlmPreview,
                lifecycle: TranscriptLifecycle.Streaming,
                updateId: "packet-1"));

            Assert.AreEqual(0, engine.CurrentTimeline.ActiveTurns.Count);

            eventHub.Publish(new CharacterTranscriptReceived(
                TranscriptMessage.Create(
                    "character-1",
                    "Camila",
                    "Safe answer.",
                    true,
                    participantId: "participant-1",
                    speakerType: SpeakerType.Character),
                "bot-turn-1",
                sourceKind: TranscriptSegmentSourceKind.BotOutput,
                lifecycle: TranscriptLifecycle.Stable,
                updateId: "packet-2",
                isSpoken: true,
                aggregatedBy: "sentence"));
            eventHub.Publish(CharacterTurnCompleted.Create("character-1", "participant-1", false));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual("Safe answer.", turn.DisplayText);
            Assert.AreEqual(TranscriptTextSource.BotOutput, turn.PrimaryTextSource);
        }

        [Test]
        public void InterruptedBotTurnRetainsReceivedText_AndRejectsStalePackets()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            CharacterTranscriptReceived output = CreateBotOutput("Partial answer", "bot-turn-1", "packet-1");
            eventHub.Publish(output);
            eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("player-turn-1"));

            TranscriptTurnSnapshot interrupted = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.IsTrue(interrupted.WasInterrupted);
            Assert.AreEqual("Partial answer", interrupted.DisplayText);

            eventHub.Publish(CreateBotOutput(" stale tail", "bot-turn-1", "packet-2"));

            Assert.AreEqual(0, engine.CurrentTimeline.ActiveTurns.Count);
            Assert.AreEqual(1, engine.CurrentTimeline.CommittedTurns.Count);
            Assert.AreEqual("Partial answer", engine.CurrentTimeline.CommittedTurns.Single().DisplayText);
        }

        [Test]
        public void SpokenProgress_DoesNotChangeTheRoomTranscript()
        {
            // The wire shape of word progress: one bot-output per spoken word, each repeating its sentence,
            // with no envelope id.
            string[] texts = { "Hello there.", "Hello there.", "Hello there.", "How are you?", "How are you?" };
            TranscriptSpokenProgress?[] progress =
            {
                new TranscriptSpokenProgress(TranscriptSpokenStatus.New, string.Empty, "Hello there."),
                new TranscriptSpokenProgress(TranscriptSpokenStatus.InProgress, "Hello", " there."),
                new TranscriptSpokenProgress(TranscriptSpokenStatus.Completed, "Hello there.", string.Empty),
                new TranscriptSpokenProgress(TranscriptSpokenStatus.InProgress, "How", " are you?"),
                new TranscriptSpokenProgress(TranscriptSpokenStatus.InProgress, "How are", " you?")
            };

            TranscriptTurnSnapshot withProgress = PublishBotOutputs(texts, progress);
            TranscriptTurnSnapshot withoutProgress = PublishBotOutputs(texts, new TranscriptSpokenProgress?[texts.Length]);

            Assert.AreEqual("Hello there. How are you?", withProgress.DisplayText);
            Assert.AreEqual(withoutProgress.DisplayText, withProgress.DisplayText);
            Assert.AreEqual(withoutProgress.Revision, withProgress.Revision);
            Assert.AreEqual(withoutProgress.State, withProgress.State);
        }

        [Test]
        public void CharacterTranscriptReceived_OriginalClrSignature_RemainsCallable_WithoutProgress()
        {
            // Optional arguments are baked into callers. Precompiled consumers still bind this exact signature.
            ConstructorInfo constructor = typeof(CharacterTranscriptReceived).GetConstructor(new[]
            {
                typeof(TranscriptMessage), typeof(string), typeof(string), typeof(string),
                typeof(TranscriptSegmentSourceKind), typeof(TranscriptLifecycle?), typeof(string), typeof(bool),
                typeof(string)
            });
            Assert.That(constructor, Is.Not.Null);
            TranscriptMessage message = TranscriptMessage.Create(
                "character-1", "Camila", "Hello there.", true, speakerType: SpeakerType.Character);

            var e = (CharacterTranscriptReceived)constructor.Invoke(new object[]
            {
                message, "turn-1", null, null, TranscriptSegmentSourceKind.BotOutput, null, "packet-1", true,
                "sentence"
            });

            Assert.That(e.TurnId, Is.EqualTo("turn-1"));
            Assert.That(e.AggregatedBy, Is.EqualTo("sentence"));
            Assert.That(e.OutputSegmentId, Is.Null);
            Assert.That(e.SpokenProgress, Is.Null);
        }

        [Test]
        public void TtsCaptionsAreSpeechAligned_AndDoNotCreateChatHistory()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            var received = new List<TranscriptCaption>();
            using IDisposable subscription = transcripts.SubscribeCaptions(received.Add);

            eventHub.Publish(CharacterTtsTextChunk.Create("participant-1", "Hello "));
            eventHub.Publish(CharacterTtsTextChunk.Create("participant-1", "world"));

            Assert.AreEqual(0, transcripts.CurrentTimeline.Turns.Count);
            Assert.AreEqual("Hello world", received.Last().Text);
            Assert.IsFalse(received.Last().IsFinal);

            eventHub.Publish(CharacterTurnCompleted.Create(string.Empty, "participant-1", false));

            Assert.IsTrue(received.Last().IsFinal);
            Assert.AreEqual(TranscriptCaptionState.Completed, received.Last().State);
        }

        [Test]
        public void SubscriptionsCopyOptions_AndFirstStreamingUpdateIsAdded()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            var options = new TranscriptSubscriptionOptions
            {
                ReplayExisting = false,
                IncludeActive = true,
                IncludeTerminal = false
            };
            var changes = new List<TranscriptChangeKind>();
            transcripts.Changed += batch => changes.AddRange(batch.Changes.Select(change => change.Kind));

            using IDisposable subscription = transcripts.SubscribeCommitted(_ => { }, options);

            Assert.IsTrue(options.IncludeActive);
            Assert.IsFalse(options.IncludeTerminal);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1",
                "You",
                "hello",
                false,
                turnId: "turn-1",
                messageId: "turn-1"));

            Assert.AreEqual(TranscriptChangeKind.Added, changes.Single());
        }

        [Test]
        public void DirectAndCoordinatorProcessedFinal_ProduceOneTerminalCallback()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            var terminalChanges = new List<TranscriptChangeKind>();
            using IDisposable subscription = transcripts.SubscribeCommitted(
                change => terminalChanges.Add(change.Kind),
                new TranscriptSubscriptionOptions { ReplayExisting = false });

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "local-player", "You", "hello", false, TranscriptionPhase.Interim,
                turnId: "session-1", messageId: "session-1"));

            var speaker = new SpeakerInfo("player-1", "Player", "participant-1");
            eventHub.Publish(FinalUserTranscriptionReceived.Create("Hello.", speaker, "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                speaker.SpeakerId,
                speaker.SpeakerName,
                "Hello.",
                true,
                TranscriptionPhase.ProcessedFinal,
                speakerInfo: speaker,
                turnId: "session-1",
                messageId: "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                speaker.SpeakerId,
                speaker.SpeakerName,
                string.Empty,
                true,
                TranscriptionPhase.Completed,
                speakerInfo: speaker,
                turnId: "session-1",
                messageId: "session-1"));

            CollectionAssert.AreEqual(new[] { TranscriptChangeKind.Committed }, terminalChanges);
            Assert.AreEqual("Hello.", transcripts.CurrentTimeline.CommittedTurns.Single().DisplayText);
        }

        [Test]
        public void TypedProcessedFinalAndEcho_ProduceOneCorrection()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            var terminalChanges = new List<TranscriptChangeKind>();
            using IDisposable subscription = transcripts.SubscribeCommitted(
                change => terminalChanges.Add(change.Kind),
                new TranscriptSubscriptionOptions { ReplayExisting = false });

            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "helo", "typed-1"));
            var speaker = new SpeakerInfo("player-1", "You", "participant-1");
            eventHub.Publish(FinalUserTranscriptionReceived.Create("hello", speaker, "typed-1"));
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "hello", "typed-1", speaker));

            CollectionAssert.AreEqual(
                new[] { TranscriptChangeKind.Committed, TranscriptChangeKind.Corrected },
                terminalChanges);
            Assert.AreEqual("hello", transcripts.CurrentTimeline.CommittedTurns.Single().DisplayText);
        }

        [Test]
        public void Clear_NotifiesSubscribersOfRemovedTurns()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            var changes = new List<TranscriptChange>();
            string removedTurnId = null;
            transcripts.TurnRemoved += id => removedTurnId = id;
            using IDisposable subscription = transcripts.Subscribe(
                changes.Add,
                new TranscriptSubscriptionOptions { ReplayExisting = false });

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "hello", false,
                turnId: "turn-1", messageId: "turn-1"));
            engine.Clear();

            Assert.AreEqual(2, changes.Count);
            Assert.AreEqual(TranscriptChangeKind.Added, changes[0].Kind);
            Assert.AreEqual(TranscriptChangeKind.Removed, changes[1].Kind);
            Assert.AreEqual("turn-1", changes[1].TurnId);
            Assert.AreEqual("turn-1", removedTurnId);
        }

        [Test]
        public void RuntimeTranscriptToggle_OnlyControlsPresentationRouting()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            var settings = new TestRuntimeSettingsService(true);
            using var applier = new RuntimeSettingsTranscriptApplier(
                settings,
                transcripts.SetPresentationEnabled);

            settings.SetTranscriptEnabled(false);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText(
                "player-1",
                "You",
                "captured while hidden",
                "typed-1"));

            Assert.IsFalse(transcripts.IsPresentationEnabled);
            Assert.AreEqual(1, engine.CurrentTimeline.CommittedTurns.Count);
            Assert.AreEqual("captured while hidden", engine.CurrentTimeline.CommittedTurns.Single().DisplayText);

            settings.SetTranscriptEnabled(true);
            Assert.IsTrue(transcripts.IsPresentationEnabled);
            Assert.AreEqual(1, engine.CurrentTimeline.CommittedTurns.Count);
        }

        [Test]
        public void ProcessedFinalWithoutIdentity_IsIgnoredWhenMultipleTurnsAreEligible()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "first", "typed-1"));
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "second", "typed-2"));

            eventHub.Publish(FinalUserTranscriptionReceived.Create("wrong", SpeakerInfo.Empty));

            CollectionAssert.AreEqual(
                new[] { "first", "second" },
                engine.CurrentTimeline.CommittedTurns.Select(turn => turn.DisplayText).ToArray());
        }

        [Test]
        public void VadBurstStops_RemainOneSemanticPlayerTurn_AndNextCharacterReplyIsVisible()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            DateTime startedAt = new(2026, 7, 18, 11, 0, 0, DateTimeKind.Utc);

            eventHub.Publish(new PlayerTranscriptReceived(
                new TranscriptMessage(
                    "player-1",
                    "You",
                    "first phrase",
                    true,
                    startedAt,
                    speakerType: SpeakerType.Player),
                TranscriptionPhase.AsrFinal,
                turnId: "session-1",
                messageId: "session-1"));
            eventHub.Publish(new PlayerSpeakingStateChanged(
                "session-1",
                false,
                startedAt.AddMilliseconds(100)));
            eventHub.Publish(new PlayerTranscriptReceived(
                new TranscriptMessage(
                    "player-1",
                    "You",
                    string.Empty,
                    true,
                    startedAt.AddMilliseconds(300),
                    speakerType: SpeakerType.Player),
                TranscriptionPhase.Completed,
                turnId: "session-1",
                messageId: "session-1"));

            eventHub.Publish(new PlayerTranscriptReceived(
                new TranscriptMessage(
                    "player-1",
                    "You",
                    "second phrase",
                    true,
                    startedAt.AddMilliseconds(500),
                    speakerType: SpeakerType.Player),
                TranscriptionPhase.AsrFinal,
                turnId: "session-2",
                messageId: "session-2"));
            eventHub.Publish(new PlayerSpeakingStateChanged(
                "session-2",
                false,
                startedAt.AddMilliseconds(600)));
            eventHub.Publish(new PlayerTranscriptReceived(
                new TranscriptMessage(
                    "player-1",
                    "You",
                    string.Empty,
                    true,
                    startedAt.AddMilliseconds(800),
                    speakerType: SpeakerType.Player),
                TranscriptionPhase.Completed,
                turnId: "session-2",
                messageId: "session-2"));

            TranscriptTurnSnapshot semanticTurn = engine.CurrentTimeline.ActiveTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Player);
            Assert.AreEqual("first phrase second phrase", semanticTurn.DisplayText);
            Assert.AreEqual(2, semanticTurn.Segments.Count);

            eventHub.Publish(new FinalUserTranscriptionReceived(
                "corrected complete utterance",
                SpeakerInfo.Empty,
                string.Empty,
                startedAt.AddSeconds(1)));
            eventHub.Publish(new CharacterTranscriptReceived(
                new TranscriptMessage(
                    "character-1",
                    "Camila",
                    "character reply",
                    true,
                    startedAt.AddSeconds(1.1),
                    participantId: "participant-1",
                    speakerType: SpeakerType.Character),
                "response-2",
                responseId: "response-2",
                sourceKind: TranscriptSegmentSourceKind.BotOutput,
                lifecycle: TranscriptLifecycle.Stable,
                updateId: "packet-2"));

            TranscriptTurnSnapshot playerTurn = engine.CurrentTimeline.CommittedTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Player);
            Assert.AreEqual("corrected complete utterance", playerTurn.DisplayText);
            Assert.AreEqual(TranscriptTextSource.ProcessedFinal, playerTurn.PrimaryTextSource);
            Assert.AreEqual(
                "character reply",
                engine.CurrentTimeline.ActiveTurns.Single(turn =>
                    turn.Participant.Kind == TranscriptParticipantKind.Character).DisplayText);
        }

        [Test]
        public void ProcessedFinalWithoutIdentity_CorrectsOpenVoiceTurnDespiteOlderHistory()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            DateTime startedAt = new(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc);

            eventHub.Publish(new PlayerTranscriptReceived(
                new TranscriptMessage(
                    "player-1",
                    "You",
                    "earlier typed turn",
                    true,
                    startedAt,
                    speakerType: SpeakerType.Player),
                TranscriptionPhase.Completed,
                turnId: "typed-1",
                messageId: "typed-1",
                sourceKind: TranscriptSegmentSourceKind.PlayerTypedText));
            eventHub.Publish(new PlayerTranscriptReceived(
                new TranscriptMessage(
                    "player-1",
                    "You",
                    "uncorrected speech",
                    true,
                    startedAt.AddSeconds(10),
                    speakerType: SpeakerType.Player),
                TranscriptionPhase.AsrFinal,
                turnId: "session-2",
                messageId: "session-2"));

            eventHub.Publish(new PlayerSpeakingStateChanged(
                string.Empty,
                false,
                startedAt.AddSeconds(10.1)));
            eventHub.Publish(new FinalUserTranscriptionReceived(
                "corrected speech",
                SpeakerInfo.Empty,
                string.Empty,
                startedAt.AddSeconds(10.12)));

            TranscriptTurnSnapshot corrected = engine.CurrentTimeline.CommittedTurns
                .Single(turn => turn.TurnId == "session-2");
            Assert.AreEqual("corrected speech", corrected.DisplayText);
            Assert.AreEqual(TranscriptTextSource.ProcessedFinal, corrected.PrimaryTextSource);

            eventHub.Publish(new CharacterTranscriptReceived(
                new TranscriptMessage(
                    "character-1",
                    "Camila",
                    "character reply",
                    true,
                    startedAt.AddSeconds(10.2),
                    participantId: "participant-1",
                    speakerType: SpeakerType.Character),
                "session-2",
                sourceKind: TranscriptSegmentSourceKind.BotOutput,
                lifecycle: TranscriptLifecycle.Stable,
                updateId: "packet-1"));

            Assert.AreEqual(
                "character reply",
                engine.CurrentTimeline.ActiveTurns.Single(turn =>
                    turn.Participant.Kind == TranscriptParticipantKind.Character).DisplayText);
        }

        [Test]
        public void StaleCharacterPacket_DoesNotSilentlyCommitOpenPlayerTurn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(CreateBotOutput("done", "bot-turn-1", "packet-1"));
            eventHub.Publish(CharacterTurnCompleted.Create("character-1", "participant-1", false));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "still speaking", false,
                turnId: "player-turn-1", messageId: "player-turn-1"));

            eventHub.Publish(CreateBotOutput("stale", "bot-turn-1", "packet-2"));

            TranscriptTurnSnapshot playerTurn = engine.CurrentTimeline.ActiveTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Player);
            Assert.AreEqual("player-turn-1", playerTurn.TurnId);
            Assert.AreEqual("still speaking", playerTurn.DisplayText);
        }

        [Test]
        public void ProcessedFinal_ReplacesWholeMultiSegmentTurn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "hello there", true, TranscriptionPhase.AsrFinal,
                turnId: "session-1", messageId: "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "how", false, TranscriptionPhase.Interim,
                turnId: "session-1", messageId: "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "how are you", true, TranscriptionPhase.AsrFinal,
                turnId: "session-1", messageId: "session-1"));

            eventHub.Publish(FinalUserTranscriptionReceived.Create(
                "Hello there, how are you?",
                new SpeakerInfo("player-1", "You", string.Empty),
                "session-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual("Hello there, how are you?", turn.DisplayText);
            Assert.AreEqual(2, turn.Segments.Count);
        }

        [Test]
        public void BotOutput_WinsOverLegacyProjection_InEitherArrivalOrder()
        {
            AssertBotProjection("normalized", TranscriptSegmentSourceKind.BotOutput,
                "legacy", TranscriptSegmentSourceKind.LegacyBotTranscript, "normalized");
            AssertBotProjection("legacy", TranscriptSegmentSourceKind.LegacyBotTranscript,
                "normalized", TranscriptSegmentSourceKind.BotOutput, "normalized");
        }

        [Test]
        public void DistinctBotUpdates_PreserveRepeatedSentence()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(CreateBotOutput("Go.", "bot-turn-1", "packet-1"));
            eventHub.Publish(CreateBotOutput("Go.", "bot-turn-1", "packet-2"));

            Assert.AreEqual("Go. Go.", engine.CurrentTimeline.ActiveTurns.Single().DisplayText);
        }

        [Test]
        public void InterleavedLegacyProjection_DoesNotUnlockRepeatedBotOutput()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(CreateCharacterUpdate(
                "I would love to.", TranscriptSegmentSourceKind.LegacyBotTranscript, null));
            eventHub.Publish(CreateCharacterUpdate(
                "I would love to.", TranscriptSegmentSourceKind.BotOutput, null));
            eventHub.Publish(CreateCharacterUpdate(
                " Once upon a time", TranscriptSegmentSourceKind.LegacyBotTranscript, null));
            eventHub.Publish(CreateCharacterUpdate(
                "I would love to.", TranscriptSegmentSourceKind.BotOutput, null));
            eventHub.Publish(CreateCharacterUpdate(
                " there was a character.", TranscriptSegmentSourceKind.LegacyBotTranscript, null));
            eventHub.Publish(CreateCharacterUpdate(
                "I would love to.", TranscriptSegmentSourceKind.BotOutput, null));
            eventHub.Publish(CreateCharacterUpdate(
                "Once upon a time there was a character.", TranscriptSegmentSourceKind.BotOutput, null));

            Assert.AreEqual(
                "I would love to. Once upon a time there was a character.",
                engine.CurrentTimeline.ActiveTurns.Single().DisplayText);
        }

        [Test]
        public void ListeningPhase_PublishesCreatedTurn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            TranscriptUpdateBatch received = null;
            engine.Changed += batch => received = batch;

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", string.Empty, false, TranscriptionPhase.Listening,
                turnId: "session-1", messageId: "session-1"));

            Assert.NotNull(received);
            Assert.AreEqual("session-1", received.AddedTurnIds.Single());
            Assert.AreEqual(TranscriptTurnState.Listening, engine.CurrentTimeline.ActiveTurns.Single().State);
        }

        [Test]
        public void CharacterHistory_IsNotDroppedWhilePlayerSpeaking()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("player-turn-1"));
            eventHub.Publish(CreateBotOutput("retain this", "bot-turn-1", "packet-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.ActiveTurns.Single();
            Assert.AreEqual(TranscriptParticipantKind.Character, turn.Participant.Kind);
            Assert.AreEqual("retain this", turn.DisplayText);
        }

        [Test]
        public void FingerprintCache_IsBounded()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            MethodInfo deduplicate = typeof(RoomTranscriptEngine).GetMethod(
                "IsDuplicateUpdateLocked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo fingerprints = typeof(RoomTranscriptEngine).GetField(
                "_lastUpdateFingerprintByMessageId",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(deduplicate);
            Assert.NotNull(fingerprints);
            for (int index = 0; index < 2100; index++)
            {
                deduplicate.Invoke(engine, new object[]
                {
                    $"packet-{index}",
                    TranscriptLifecycle.Streaming,
                    TranscriptSegmentSourceKind.BotOutput,
                    "text"
                });
            }

            var cache = (Dictionary<string, string>)fingerprints.GetValue(engine);
            Assert.LessOrEqual(cache.Count, 2048);
        }

        [Test]
        public void CaptionIdentityTransition_ReusesParticipantKey()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            using IDisposable subscription = transcripts.SubscribeCaptions(_ => { });

            eventHub.Publish(CharacterTtsTextChunk.Create("participant-1", "Hel"));
            eventHub.Publish(new CharacterTranscriptReceived(
                TranscriptMessage.Create(
                    "character-1",
                    "Camila",
                    "Hello",
                    true,
                    participantId: "participant-1",
                    speakerType: SpeakerType.Character),
                "bot-turn-1"));
            eventHub.Publish(CharacterTtsTextChunk.Create("participant-1", "lo"));

            FieldInfo field = typeof(ConvaiTranscripts).GetField(
                "_lastCaptionsByActor",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var captionsByActor = (Dictionary<string, TranscriptCaption>)field.GetValue(transcripts);
            Assert.AreEqual(1, captionsByActor.Count);
            Assert.AreEqual("Hello", transcripts.CurrentCaptions.Captions.Single().Text);
        }

        private static void AssertBotProjection(
            string firstText,
            TranscriptSegmentSourceKind firstSource,
            string secondText,
            TranscriptSegmentSourceKind secondSource,
            string expected)
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(CreateCharacterUpdate(firstText, firstSource, "packet-1"));
            eventHub.Publish(CreateCharacterUpdate(secondText, secondSource, "packet-2"));
            Assert.AreEqual(expected, engine.CurrentTimeline.ActiveTurns.Single().DisplayText);
        }

        private static CharacterTranscriptReceived CreateCharacterUpdate(
            string text,
            TranscriptSegmentSourceKind source,
            string updateId)
        {
            TranscriptMessage message = TranscriptMessage.Create(
                "character-1",
                "Camila",
                text,
                true,
                participantId: "participant-1",
                speakerType: SpeakerType.Character);
            return new CharacterTranscriptReceived(
                message,
                "bot-turn-1",
                sourceKind: source,
                lifecycle: TranscriptLifecycle.Stable,
                updateId: updateId);
        }

        private static CharacterTranscriptReceived CreateBotOutput(string text, string turnId, string updateId)
        {
            TranscriptMessage message = TranscriptMessage.Create(
                "character-1",
                "Camila",
                text,
                true,
                participantId: "participant-1",
                speakerType: SpeakerType.Character);
            return new CharacterTranscriptReceived(
                message,
                turnId,
                sourceKind: TranscriptSegmentSourceKind.BotOutput,
                lifecycle: TranscriptLifecycle.Stable,
                updateId: updateId);
        }

        private static TranscriptTurnSnapshot PublishBotOutputs(string[] texts, TranscriptSpokenProgress?[] progress)
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            for (int i = 0; i < texts.Length; i++)
            {
                TranscriptMessage message = TranscriptMessage.Create(
                    "character-1",
                    "Camila",
                    texts[i],
                    true,
                    participantId: "participant-1",
                    speakerType: SpeakerType.Character);
                eventHub.Publish(new CharacterTranscriptReceived(
                    message,
                    "bot-turn-1",
                    null,
                    null,
                    TranscriptSegmentSourceKind.BotOutput,
                    TranscriptLifecycle.Stable,
                    null,
                    true,
                    "sentence",
                    i < 3 ? 1 : 2,
                    progress[i]));
            }

            return engine.CurrentTimeline.ActiveTurns.Single();
        }

        private static EventHub CreateEventHub() => new(new ImmediateScheduler());

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestRuntimeSettingsService : IConvaiRuntimeSettingsService
        {
            public TestRuntimeSettingsService(bool transcriptEnabled)
            {
                Current = CreateSnapshot(transcriptEnabled);
            }

            public ConvaiRuntimeSettingsSnapshot Current { get; private set; }

            public event Action<ConvaiRuntimeSettingsChanged> Changed;

            public ConvaiRuntimeSettingsApplyResult Apply(ConvaiRuntimeSettingsPatch patch) =>
                throw new NotSupportedException();

            public ConvaiRuntimeSettingsApplyResult ResetToDefaults() => throw new NotSupportedException();

            public void SetTranscriptEnabled(bool enabled)
            {
                ConvaiRuntimeSettingsSnapshot previous = Current;
                Current = CreateSnapshot(enabled);
                Changed?.Invoke(new ConvaiRuntimeSettingsChanged(
                    previous,
                    Current,
                    ConvaiRuntimeSettingsChangeMask.TranscriptEnabled));
            }

            private static ConvaiRuntimeSettingsSnapshot CreateSnapshot(bool transcriptEnabled) =>
                new("Player", transcriptEnabled, true, null);
        }
    }
}
