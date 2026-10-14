using System;
using System.Collections;
using System.Linq;
using Convai.Application.Services.Transcript;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Models;
using Convai.Runtime.Facades;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Application
{
    public class RoomTranscriptEngineTests
    {
        // Retained-history scaling gate. The engine gate and its facade twin measure the same
        // interim update at three history depths, so a per-update cost that grows with retained
        // turns fails here rather than in a live session.
        [UnityTest]
        public IEnumerator InterimUpdate_WithRetainedHistory_StaysWithinAllocationBudget(
            [Values(20, 100, 300)] int retainedTurns)
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            SeedRetainedTurns(eventHub, retainedTurns);
            PlayerTranscriptReceived[] updates = CreateInterimUpdates();
            int changed = 0;
            engine.Changed += _ => changed++;
            long cursor = engine.CurrentTimeline.Cursor;
            long bytes = 0;
            yield return AllocationMeasurement.Measure(
                i => eventHub.Publish(updates[i & 1]),
                value => bytes = value,
                $"engine-interim-{retainedTurns}");

            Assert.That(changed, Is.EqualTo(AllocationMeasurement.WarmupCalls + 2 * AllocationMeasurement.MeasuredCalls));
            Assert.That(engine.CurrentTimeline.Cursor - cursor, Is.EqualTo(changed));
            Assert.That(engine.CurrentTimeline.CommittedTurns.Count, Is.EqualTo(retainedTurns));
            Assert.That(engine.CurrentTimeline.ActiveTurns.Single().DisplayText, Is.EqualTo("fixture-b"));
            Assert.That(bytes, Is.LessThanOrEqualTo(EngineInterimCeilingBytes * AllocationMeasurement.MeasuredCalls),
                "Warmed per-operation allocation must not exceed the recorded ceiling for this history depth.");
        }

        [UnityTest]
        public IEnumerator FacadeInterimUpdate_WithRetainedHistory_StaysWithinAllocationBudget(
            [Values(20, 100, 300)] int retainedTurns)
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            SeedRetainedTurns(eventHub, retainedTurns);
            PlayerTranscriptReceived[] updates = CreateInterimUpdates();
            int changed = 0;
            int turnUpdates = 0;
            transcripts.Changed += _ => changed++;
            transcripts.TurnUpdated += _ => turnUpdates++;
            using IDisposable subscription = transcripts.Subscribe(
                _ => { },
                new TranscriptSubscriptionOptions { ReplayExisting = false });
            long bytes = 0;
            yield return AllocationMeasurement.Measure(
                i => eventHub.Publish(updates[i & 1]),
                value => bytes = value,
                $"facade-interim-{retainedTurns}");

            Assert.That(changed, Is.EqualTo(AllocationMeasurement.WarmupCalls + 2 * AllocationMeasurement.MeasuredCalls));
            Assert.That(turnUpdates, Is.EqualTo(changed));
            Assert.That(transcripts.CurrentTimeline.CommittedTurns.Count, Is.EqualTo(retainedTurns));
            Assert.That(transcripts.CurrentTimeline.ActiveTurns.Single().DisplayText, Is.EqualTo("fixture-b"));
            Assert.That(bytes, Is.LessThanOrEqualTo(FacadeInterimCeilingBytes * AllocationMeasurement.MeasuredCalls),
                "Warmed per-operation allocation must not exceed the recorded ceiling for this history depth.");
        }

        // The scaling gate proper: a per-update cost that is still proportional to retained history
        // shows up as a ratio, not as an absolute number that a generous ceiling can hide.
        [UnityTest]
        public IEnumerator InterimUpdateAllocation_StaysFlatAsRetainedHistoryGrows()
        {
            long hundredTurns = 0;
            yield return MeasureEngineInterimUpdates(100, value => hundredTurns = value);
            long threeHundredTurns = 0;
            yield return MeasureEngineInterimUpdates(300, value => threeHundredTurns = value);

            Assert.That(hundredTurns, Is.GreaterThan(0));
            Assert.That(
                threeHundredTurns,
                Is.LessThanOrEqualTo((long)(hundredTurns * 1.1)),
                $"Per-update allocation must not grow with retained history: " +
                $"100 turns={hundredTurns} B, 300 turns={threeHundredTurns} B.");
        }

        [Test]
        public void Final_Player_Text_Is_Preserved_When_Next_Interim_Arrives_In_Same_Session()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Hey. Hello.", true, TranscriptionPhase.AsrFinal,
                turnId: "session-1", messageId: "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "You actually", false,
                turnId: "session-1", messageId: "session-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.ActiveTurns.Single();
            Assert.AreEqual("session-1", turn.TurnId);
            Assert.AreEqual("Hey. Hello. You actually", turn.DisplayText);
            Assert.AreEqual(TranscriptLifecycle.Streaming, turn.Lifecycle);
        }

        [Test]
        public void Completed_Acoustic_Sessions_Remain_Segments_Of_One_Open_Player_Turn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "First phrase", true, TranscriptionPhase.AsrFinal,
                turnId: "session-1", messageId: "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", string.Empty, true, TranscriptionPhase.Completed,
                turnId: "session-1", messageId: "session-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Second phrase", false,
                turnId: "session-2", messageId: "session-2"));

            Assert.AreEqual(0, engine.CurrentTimeline.CommittedTurns.Count);
            TranscriptTurnSnapshot active = engine.CurrentTimeline.ActiveTurns.Single();
            Assert.AreEqual("session-1", active.TurnId);
            Assert.AreEqual("First phrase Second phrase", active.DisplayText);
            Assert.AreEqual(2, active.Segments.Count);
        }

        [Test]
        public void Character_Response_Completes_Player_Turn_And_Player_Retry_Starts_New_Turn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Question one", true, TranscriptionPhase.AsrFinal,
                turnId: "turn-1", messageId: "turn-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", string.Empty, true, TranscriptionPhase.Completed,
                turnId: "turn-1", messageId: "turn-1"));
            eventHub.Publish(CharacterTranscriptReceived.Create("char-1", "Alice", "Answer one", true));

            TranscriptTurnSnapshot completedPlayerTurn = engine.CurrentTimeline.CommittedTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Player);
            Assert.AreEqual("turn-1", completedPlayerTurn.TurnId);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Question two", false,
                turnId: "turn-2", messageId: "turn-2"));

            TranscriptTurnSnapshot newPlayerTurn = engine.CurrentTimeline.ActiveTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Player);
            Assert.AreEqual("turn-2", newPlayerTurn.TurnId);
            Assert.AreEqual("Question two", newPlayerTurn.DisplayText);
        }

        [Test]
        public void Player_Transcript_Interrupts_Active_Character_Turn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(CharacterTranscriptReceived.Create("char-1", "Alice", "Long answer", true));
            TranscriptTurnSnapshot firstCharacterTurn = engine.CurrentTimeline.ActiveTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Character);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Please repeat", false,
                turnId: "turn-2", messageId: "turn-2"));

            TranscriptTurnSnapshot interruptedCharacterTurn = engine.CurrentTimeline.CommittedTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Character);
            Assert.AreEqual(firstCharacterTurn.TurnId, interruptedCharacterTurn.TurnId);
            Assert.IsTrue(interruptedCharacterTurn.WasInterrupted);
        }

        [Test]
        public void Character_ResponseId_Updates_One_Turn_From_Interim_To_Final()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-1", "Alice", "Hel", false, responseId: "response-1"));
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-1", "Alice", "Hello!", true, responseId: "response-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.ActiveTurns.Single();
            Assert.AreEqual("response-1", turn.TurnId);
            Assert.AreEqual("response-1", turn.MessageId);
            Assert.AreEqual("response-1", turn.ResponseId);
            Assert.AreEqual("Hello!", turn.DisplayText);
            Assert.AreEqual(TranscriptLifecycle.Stable, turn.Lifecycle);
        }

        [Test]
        public void Character_New_ResponseId_Starts_Fresh_Turn_For_Same_Character()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-1", "Alice", "First answer", true, responseId: "response-1"));
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-1", "Alice", "Second answer", true, responseId: "response-2"));

            TranscriptTurnSnapshot completedTurn = engine.CurrentTimeline.CommittedTurns.Single();
            TranscriptTurnSnapshot activeTurn = engine.CurrentTimeline.ActiveTurns.Single();
            Assert.AreEqual("response-1", completedTurn.TurnId);
            Assert.AreEqual("First answer", completedTurn.DisplayText);
            Assert.AreEqual("response-2", activeTurn.TurnId);
            Assert.AreEqual("Second answer", activeTurn.DisplayText);
        }

        [Test]
        public void Duplicate_Player_Update_With_Same_MessageId_Is_Routed_Once()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            int changedCount = 0;
            engine.Changed += _ => changedCount++;

            PlayerTranscriptReceived update = PlayerTranscriptReceived.Create(
                "player-1", "You", "Hello", false,
                turnId: "turn-1", messageId: "turn-1");
            eventHub.Publish(update);
            eventHub.Publish(update);

            Assert.AreEqual(1, changedCount);
            Assert.AreEqual(1, engine.CurrentTimeline.ActiveTurns.Count);
        }

        [Test]
        public void Same_Player_Text_Without_TurnId_Is_Not_Dropped_After_Previous_Turns_Complete()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Again", false, TranscriptionPhase.AsrFinal));
            eventHub.Publish(CharacterTranscriptReceived.Create("char-1", "Alice", "First answer", true));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Again", false, TranscriptionPhase.AsrFinal));
            eventHub.Publish(CharacterTranscriptReceived.Create("char-1", "Alice", "Second answer", true));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", "Again", false, TranscriptionPhase.AsrFinal));

            TranscriptTurnSnapshot[] playerTurns = engine.CurrentTimeline.ActiveTurns
                .Concat(engine.CurrentTimeline.CommittedTurns)
                .Where(turn => turn.Participant.Kind == TranscriptParticipantKind.Player)
                .ToArray();
            Assert.AreEqual(3, playerTurns.Length);
            Assert.AreEqual(3, playerTurns.Select(turn => turn.TurnId).Distinct().Count());
            Assert.IsTrue(playerTurns.All(turn => turn.DisplayText == "Again"));
        }

        [Test]
        public void Character_TurnCompleted_Can_Close_Turn_By_ParticipantId_When_CharacterId_Is_Missing()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            TranscriptMessage message = new(
                string.Empty, "Alice", "Hello!", true, DateTime.UtcNow,
                participantId: "participant-1", speakerType: SpeakerType.Character);

            eventHub.Publish(new CharacterTranscriptReceived(message));
            eventHub.Publish(CharacterTurnCompleted.Create(string.Empty, "participant-1", false));

            Assert.AreEqual(0, engine.CurrentTimeline.ActiveTurns.Count);
            Assert.AreEqual(1, engine.CurrentTimeline.CommittedTurns.Count);
            Assert.AreEqual("participant-1", engine.CurrentTimeline.CommittedTurns.Single().Participant.ParticipantId);
        }

        [Test]
        public void Empty_Character_Completion_Does_Not_Close_Open_Turns()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            TranscriptMessage message = new(
                string.Empty, "Alice", "Hello!", true, DateTime.UtcNow,
                participantId: "participant-1", speakerType: SpeakerType.Character);

            eventHub.Publish(new CharacterTranscriptReceived(message));
            eventHub.Publish(CharacterTurnCompleted.Create(string.Empty, string.Empty, false));

            Assert.AreEqual(1, engine.CurrentTimeline.ActiveTurns.Count);
            Assert.AreEqual(0, engine.CurrentTimeline.CommittedTurns.Count);
        }

        [Test]
        public void Typed_Text_Local_Row_Is_Created_Immediately_With_MessageId()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "hello", "typed-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual("typed-1", turn.TurnId);
            Assert.AreEqual("typed-1", turn.MessageId);
            Assert.AreEqual("hello", turn.DisplayText);
            Assert.AreEqual(TranscriptSegmentSourceKind.PlayerTypedText, turn.Segments.Single().SourceKind);
        }

        [Test]
        public void Typed_Text_Echo_With_Matching_MessageId_Updates_Same_Row()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "helo", "typed-1"));
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "hello", "typed-1"));

            TranscriptTurnSnapshot turn = engine.CurrentTimeline.CommittedTurns.Single();
            Assert.AreEqual(1, engine.CurrentTimeline.CommittedTurns.Count);
            Assert.AreEqual("typed-1", turn.TurnId);
            Assert.AreEqual("hello", turn.DisplayText);
        }

        [Test]
        public void Typed_Text_Echo_With_Unknown_MessageId_Creates_New_Row()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "first", "typed-1"));
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "second", "typed-2"));

            Assert.AreEqual(2, engine.CurrentTimeline.CommittedTurns.Count);
            CollectionAssert.AreEquivalent(
                new[] { "typed-1", "typed-2" },
                engine.CurrentTimeline.CommittedTurns.Select(turn => turn.TurnId).ToArray());
        }

        [Test]
        public void UpdatePlayerDisplayName_Corrects_PlayerTurns_AndPublishesOnlyOnce()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText(
                "player-1", "Player", "hello", "typed-1"));
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-1", "Alice", "hello back", true, responseId: "response-1"));

            int batchCount = 0;
            TranscriptUpdateBatch lastBatch = null;
            engine.Changed += batch =>
            {
                batchCount++;
                lastBatch = batch;
            };

            engine.UpdatePlayerDisplayName("Rishav");

            TranscriptTurnSnapshot playerTurn = engine.CurrentTimeline.CommittedTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Player);
            TranscriptTurnSnapshot characterTurn = engine.CurrentTimeline.ActiveTurns
                .Single(turn => turn.Participant.Kind == TranscriptParticipantKind.Character);
            Assert.AreEqual("Rishav", playerTurn.Participant.DisplayName);
            Assert.AreEqual("Alice", characterTurn.Participant.DisplayName);
            CollectionAssert.AreEqual(new[] { "typed-1" }, lastBatch.CorrectedTurnIds);
            Assert.AreEqual(1, batchCount);

            engine.UpdatePlayerDisplayName("Rishav");

            Assert.AreEqual(1, batchCount);
        }

        private static IEnumerator MeasureEngineInterimUpdates(int retainedTurns, Action<long> measured)
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            SeedRetainedTurns(eventHub, retainedTurns);
            PlayerTranscriptReceived[] updates = CreateInterimUpdates();
            yield return AllocationMeasurement.Measure(
                i => eventHub.Publish(updates[i & 1]),
                measured,
                $"engine-scaling-{retainedTurns}");
        }

        private static void SeedRetainedTurns(EventHub eventHub, int retainedTurns)
        {
            for (int i = 0; i < retainedTurns; i++)
                eventHub.Publish(PlayerTranscriptReceived.CreateTypedText(
                    "allocation-player", "Synthetic", "fixture", $"retained-{i}"));
        }

        private static PlayerTranscriptReceived[] CreateInterimUpdates() => new[]
        {
            PlayerTranscriptReceived.Create("allocation-player", "Synthetic", "fixture-a", false,
                turnId: "active", messageId: "active"),
            PlayerTranscriptReceived.Create("allocation-player", "Synthetic", "fixture-b", false,
                turnId: "active", messageId: "active")
        };

        // Measured on Unity 6000.3.21f1, Editor Edit Mode: 2,892 B per engine update and 4,732 B per
        // engine-plus-facade update at 20, 100 and 300 retained turns alike. One ceiling for every
        // depth is the claim itself — the cost of an update no longer depends on how much history
        // the room holds.
        private const long EngineInterimCeilingBytes = 3200;
        private const long FacadeInterimCeilingBytes = 5200;

        private static EventHub CreateEventHub() => new(new ImmediateScheduler());

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
