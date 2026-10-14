using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Threading;
using Convai.Application.Services.Transcript;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Models;
using Convai.Runtime.Facades;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Application
{
    /// <summary>
    ///     Protects the reuse contract the transcript engine and facade now rely on: an update
    ///     republishes unchanged turns by reference, and everything a consumer already received keeps
    ///     the content it was delivered with.
    /// </summary>
    public class TranscriptSnapshotReuseTests
    {
        [Test]
        public void InterimUpdate_ReusesRetainedTurnSnapshots_AndRebuildsOnlyTheActiveTurn()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            PublishTypedTurns(eventHub, 3);
            eventHub.Publish(Interim("first"));

            TranscriptTimelineSnapshot before = engine.CurrentTimeline;
            TranscriptTurnSnapshot activeBefore = before.ActiveTurns.Single();

            eventHub.Publish(Interim("second"));

            TranscriptTimelineSnapshot after = engine.CurrentTimeline;
            Assert.AreSame(before.CommittedTurns, after.CommittedTurns,
                "An interim update must republish the settled array, not rebuild it.");
            for (int i = 0; i < before.CommittedTurns.Count; i++)
                Assert.AreSame(before.CommittedTurns[i], after.CommittedTurns[i]);

            Assert.AreNotSame(activeBefore, after.ActiveTurns.Single());
            Assert.AreEqual("second", after.ActiveTurns.Single().DisplayText);
            Assert.Greater(after.Cursor, before.Cursor);
        }

        [Test]
        public void Rename_InvalidatesRenamedTurnsOnly()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "Player", "hello", "typed-1"));
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-1", "Alice", "hello back", true, responseId: "response-1"));
            eventHub.Publish(CharacterTurnCompleted.Create("char-1", null, false));

            TranscriptTurnSnapshot playerBefore = FindTurn(engine, "typed-1");
            TranscriptTurnSnapshot characterBefore = FindTurn(engine, "response-1");

            engine.UpdatePlayerDisplayName("Renamed");

            Assert.AreNotSame(playerBefore, FindTurn(engine, "typed-1"));
            Assert.AreEqual("Renamed", FindTurn(engine, "typed-1").Participant.DisplayName);
            Assert.AreSame(characterBefore, FindTurn(engine, "response-1"),
                "A rename must not invalidate turns it did not touch.");
        }

        [Test]
        public void Interruption_MovesActiveTurnIntoCommittedInOneUpdate_AndKeepsOlderTurns()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            PublishTypedTurns(eventHub, 2);
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-1", "Alice", "Long answer", false, responseId: "response-1"));

            TranscriptTurnSnapshot retainedBefore = FindTurn(engine, "typed-0");
            Assert.AreEqual(1, engine.CurrentTimeline.ActiveTurns.Count);

            eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("session-1"));

            TranscriptTimelineSnapshot after = engine.CurrentTimeline;
            Assert.AreEqual(0, after.ActiveTurns.Count);
            Assert.IsTrue(after.CommittedTurns.Single(turn => turn.TurnId == "response-1").WasInterrupted);
            Assert.AreSame(retainedBefore, FindTurn(engine, "typed-0"));
        }

        [Test]
        public void RemovedTurn_LeavesTheRemainingTurnsUntouched()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            PublishTypedTurns(eventHub, 2);
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", string.Empty, false, TranscriptionPhase.Listening,
                turnId: "empty-1", messageId: "empty-1"));

            TranscriptTurnSnapshot retainedBefore = FindTurn(engine, "typed-1");
            TranscriptUpdateBatch removal = null;
            engine.Changed += batch => removal = batch;

            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1", "You", string.Empty, true, TranscriptionPhase.Completed,
                turnId: "empty-1", messageId: "empty-1"));

            Assert.IsNotNull(removal);
            CollectionAssert.AreEqual(new[] { "empty-1" }, removal.RemovedTurnIds);
            Assert.IsNull(FindTurn(engine, "empty-1"));
            Assert.AreSame(retainedBefore, FindTurn(engine, "typed-1"));
        }

        [Test]
        public void DeliveredBatch_KeepsItsCursorAndContent_AfterLaterUpdates()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            PublishTypedTurns(eventHub, 2);
            eventHub.Publish(Interim("first"));

            var batches = new List<TranscriptUpdateBatch>();
            engine.Changed += batches.Add;

            eventHub.Publish(Interim("second"));
            TranscriptUpdateBatch captured = batches.Single();
            long capturedCursor = captured.Cursor;
            int capturedCommitted = captured.Timeline.CommittedTurns.Count;
            string capturedText = captured.ChangedTurns.Single().DisplayText;

            eventHub.Publish(Interim("third"));
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "later", "typed-later"));

            Assert.AreEqual(capturedCursor, captured.Cursor);
            Assert.AreEqual(capturedCommitted, captured.Timeline.CommittedTurns.Count);
            Assert.AreEqual(capturedText, captured.ChangedTurns.Single().DisplayText);
            Assert.AreEqual("second", captured.ChangedTurns.Single().DisplayText);
        }

        [Test]
        public void Clear_ResetsCaches_AndEmitsRemovalsWithARisingCursor()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            PublishTypedTurns(eventHub, 3);
            eventHub.Publish(Interim("open"));
            long cursorBefore = engine.CurrentTimeline.Cursor;

            TranscriptUpdateBatch clearBatch = null;
            engine.Changed += batch => clearBatch ??= batch;
            engine.Clear();

            Assert.IsNotNull(clearBatch);
            Assert.AreEqual(4, clearBatch.RemovedTurnIds.Count);
            Assert.Greater(clearBatch.Cursor, cursorBefore);
            long clearCursor = clearBatch.Cursor;
            Assert.AreEqual(0, engine.CurrentTimeline.CommittedTurns.Count);
            Assert.AreEqual(0, engine.CurrentTimeline.ActiveTurns.Count);
            Assert.AreEqual(0, engine.CurrentTimeline.TurnsById.Count);

            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "after clear", "typed-new"));

            Assert.AreEqual("after clear", engine.CurrentTimeline.CommittedTurns.Single().DisplayText);
            Assert.Greater(engine.CurrentTimeline.Cursor, clearCursor);
        }

        [Test]
        public void TimelineIndexes_AgreeWithPublishedTurns_OverASeededMultiActorSequence()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            var random = new Random(20260909);

            for (int step = 0; step < 120; step++)
            {
                PublishSeededEvent(eventHub, random, step);
                AssertTimelineMatchesRebuild(engine.CurrentTimeline);
            }

            Assert.Greater(engine.CurrentTimeline.TurnsById.Count, 4, "The seeded sequence must build real history.");
        }

        [Test]
        public void ReentrantSubscriber_SeesMonotonicCursors_AndDoesNotDeadlock()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            var cursors = new List<long>();
            bool reentered = false;

            engine.Changed += batch =>
            {
                cursors.Add(batch.Cursor);
                if (reentered) return;

                reentered = true;
                eventHub.Publish(CharacterTranscriptReceived.Create(
                    "char-1", "Alice", "reentrant answer", true, responseId: "response-1"));
            };

            eventHub.Publish(Interim("outer"));

            Assert.IsTrue(reentered);
            Assert.AreEqual(cursors.Count, cursors.Distinct().Count());
            CollectionAssert.AreEqual(cursors.OrderBy(cursor => cursor).ToArray(), cursors.ToArray(),
                "A reentrant publisher must never observe a cursor going backwards.");
            Assert.AreEqual(engine.CurrentTimeline.Cursor, cursors.Max());
        }

        [Test]
        public void TurnOverlayIndex_PrefersOpenTurns_AndReportsAConsistentKeySet()
        {
            TranscriptTurnSnapshot settled = CreateSnapshot("shared", 1);
            TranscriptTurnSnapshot other = CreateSnapshot("settled-only", 2);
            TranscriptTurnSnapshot open = CreateSnapshot("shared", 3);
            TranscriptTurnSnapshot openOnly = CreateSnapshot("open-only", 4);
            var settledById = new Dictionary<string, TranscriptTurnSnapshot>
            {
                { settled.TurnId, settled },
                { other.TurnId, other }
            };

            var index = new TurnOverlayIndex<TranscriptTurnSnapshot>(
                settledById,
                new[] { open, openOnly },
                snapshot => snapshot.TurnId);

            Assert.AreSame(open, index["shared"], "An open turn wins over a settled turn with the same id.");
            Assert.AreSame(other, index["settled-only"]);
            Assert.AreSame(openOnly, index["open-only"]);
            Assert.AreEqual(3, index.Count);
            Assert.IsTrue(index.ContainsKey("open-only"));
            Assert.IsFalse(index.ContainsKey("absent"));
            CollectionAssert.AreEquivalent(
                new[] { "shared", "settled-only", "open-only" },
                index.Keys.ToArray());
            Assert.AreEqual(3, index.Select(pair => pair.Key).Distinct().Count(),
                "Enumeration must not repeat a key that both sides carry.");
            Assert.Throws<KeyNotFoundException>(() => _ = index["absent"]);
        }

        [Test]
        public void LatestTurnOverlayIndex_ResolvesByRoomSequenceAcrossBothSides()
        {
            TranscriptTurnSnapshot settled = CreateSnapshot("settled", 7);
            TranscriptTurnSnapshot staleOpen = CreateSnapshot("stale-open", 3);
            TranscriptTurnSnapshot freshOpen = CreateSnapshot("fresh-open", 9);
            var settledByKey = new Dictionary<string, TranscriptTurnSnapshot> { { "Player:actor:a", settled } };

            var index = new LatestTurnOverlayIndex(
                settledByKey,
                new[]
                {
                    new KeyValuePair<string, TranscriptTurnSnapshot>("Player:actor:a", staleOpen),
                    new KeyValuePair<string, TranscriptTurnSnapshot>("Player:actor:b", freshOpen)
                });

            Assert.AreSame(settled, index["Player:actor:a"], "The newest room sequence wins, not the open turn.");
            Assert.AreSame(freshOpen, index["Player:actor:b"]);
            Assert.AreEqual(2, index.Count);
            CollectionAssert.AreEquivalent(new[] { "Player:actor:a", "Player:actor:b" }, index.Keys.ToArray());
            CollectionAssert.AreEquivalent(new[] { settled, freshOpen }, index.Values.ToArray());
        }

        [Test]
        public void Facade_ReusesTurnModels_AndSharesThemWithTheChangeItDelivers()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            PublishTypedTurns(eventHub, 3);
            eventHub.Publish(Interim("first"));

            TranscriptTimeline before = transcripts.CurrentTimeline;
            TranscriptTurn retainedBefore = before.CommittedTurns[0];

            TranscriptChangeBatch delivered = null;
            transcripts.Changed += batch => delivered = batch;
            eventHub.Publish(Interim("second"));

            TranscriptTimeline after = transcripts.CurrentTimeline;
            Assert.AreSame(before.CommittedTurns, after.CommittedTurns);
            Assert.AreSame(retainedBefore, after.CommittedTurns[0]);
            Assert.AreNotSame(before.ActiveTurns[0], after.ActiveTurns[0]);

            Assert.IsNotNull(delivered);
            TranscriptChange change = delivered.Changes.Single();
            Assert.AreSame(after, delivered.Timeline);
            Assert.AreSame(delivered.Timeline.TurnsById[change.Turn.Id], change.Turn,
                "A delivered change and the timeline it arrived with must describe one turn model.");
            Assert.AreEqual("second", change.Turn.DisplayText);
        }

        [Test]
        public void Facade_UpdatingOneOpenCharacter_ReusesTheOtherAndPreservesHistory()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-a", "A", "synthetic alpha", false, responseId: "response-a"));
            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-b", "B", "synthetic beta", false, responseId: "response-b"));
            TranscriptTimeline before = transcripts.CurrentTimeline;
            TranscriptTurn first = before.TurnsById["response-a"];
            TranscriptTurn other = before.TurnsById["response-b"];

            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-a", "A", "synthetic alpha revised", false, responseId: "response-a"));

            TranscriptTimeline after = transcripts.CurrentTimeline;
            CollectionAssert.AreEqual(new[] { "response-a", "response-b" },
                after.ActiveTurns.Select(turn => turn.Id).ToArray());
            Assert.AreNotSame(first, after.TurnsById["response-a"]);
            Assert.AreSame(other, after.TurnsById["response-b"]);
            Assert.AreEqual("synthetic alpha revised", after.TurnsById["response-a"].DisplayText);
            Assert.AreEqual("synthetic beta", other.DisplayText);
            Assert.AreEqual("synthetic alpha", first.DisplayText);

            eventHub.Publish(CharacterTranscriptReceived.Create(
                "char-b", "Renamed B", "synthetic beta revised", false, responseId: "response-b"));
            Assert.AreSame(after.TurnsById["response-a"], transcripts.CurrentTimeline.TurnsById["response-a"]);
            Assert.AreNotSame(other, transcripts.CurrentTimeline.TurnsById["response-b"]);
            Assert.AreEqual("synthetic beta revised", transcripts.CurrentTimeline.TurnsById["response-b"].DisplayText);
            Assert.AreEqual("B", other.Speaker.DisplayName);
            Assert.AreEqual("Renamed B", transcripts.CurrentTimeline.TurnsById["response-b"].Speaker.DisplayName);
        }

        [Test]
        public void Facade_Clear_ReleasesEveryCacheWithoutDependingOnGarbageCollection()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            PublishTypedTurns(eventHub, 3);
            eventHub.Publish(Interim("active"));
            TranscriptTimeline historical = transcripts.CurrentTimeline;
            transcripts.Clear();

            object mapper = PrivateField(transcripts, "_timelineMapper");
            foreach (string field in new[] { "_committedModels", "_reusableModels", "_committedById" })
                Assert.That(((IDictionary)PrivateField(mapper, field)).Count, Is.Zero, field);
            foreach (string field in new[] { "_activeSource", "_activeTurns", "_committedSource", "_committedTurns" })
                Assert.That(((ICollection)PrivateField(mapper, field)).Count, Is.Zero, field);
            Assert.That(transcripts.CurrentTimeline.TurnsById.Count, Is.Zero);
            Assert.That(((TranscriptTimelineSnapshot)PrivateField(mapper, "_snapshot")).TurnsById.Count, Is.Zero);
            Assert.That(historical.CommittedTurns.Count, Is.EqualTo(3));
            Assert.That(historical.ActiveTurns.Count, Is.EqualTo(1));
        }

        [Test]
        public void Facade_CommittedCorrection_ReusesUntouchedModelsAcrossQueriesAndBatch()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            PublishTypedTurns(eventHub, 3);
            TranscriptTimeline before = transcripts.CurrentTimeline;
            TranscriptChangeBatch correction = null;
            transcripts.Changed += batch => correction = batch;
            eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "corrected", "typed-1"));
            TranscriptTimeline after = transcripts.CurrentTimeline;

            Assert.That(after.TurnsById["typed-1"], Is.Not.SameAs(before.TurnsById["typed-1"]));
            Assert.That(after.TurnsById["typed-0"], Is.SameAs(before.TurnsById["typed-0"]));
            Assert.That(after.TurnsById["typed-2"], Is.SameAs(before.TurnsById["typed-2"]));
            Assert.That(before.TurnsById["typed-1"].DisplayText, Is.EqualTo("typed text 1"));
            Assert.That(correction.Changes.Single().Kind, Is.EqualTo(TranscriptChangeKind.Corrected));
            Assert.That(correction.Changes.Single().Turn, Is.SameAs(after.TurnsById["typed-1"]));
            Assert.That(transcripts.GetTurn("typed-1"), Is.SameAs(after.TurnsById["typed-1"]));
            var participant = new TranscriptParticipantRef(TranscriptParticipantKind.Player, "player-1", "You", null);
            Assert.That(transcripts.GetLatestTurn(participant), Is.SameAs(after.TurnsById["typed-2"]));
            foreach (TranscriptTurn turn in transcripts.GetTurns(new TranscriptQuery { IncludeActiveTurns = false }))
                Assert.That(turn, Is.SameAs(after.TurnsById[turn.Id]));
        }

        [Test]
        public void Facade_ChangeSubscriber_CanReadFromAnotherThreadAndReenter()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            using var transcripts = new ConvaiTranscripts(engine);
            bool callbackReadFinished = false;
            Exception error = null;
            TranscriptChangeBatch outer = null;
            Thread reader = null;
            transcripts.Changed += batch =>
            {
                if (outer != null) return;
                outer = batch;
                reader = new Thread(() =>
                {
                    try { _ = transcripts.CurrentTimeline.TurnsById.Count; }
                    catch (Exception exception) { error = exception; }
                }) { IsBackground = true };
                reader.Start();
                callbackReadFinished = reader.Join(TimeSpan.FromSeconds(2));
                eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "nested", "nested"));
            };
            PublishTypedTurns(eventHub, 1);
            Assert.That(reader.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(callbackReadFinished, Is.True, "Do not hold the mapper lock across subscribers.");
            Assert.That(error, Is.Null);
            Assert.That(outer.Timeline.TurnsById.Count, Is.EqualTo(1));
            Assert.That(transcripts.CurrentTimeline.TurnsById.Count, Is.EqualTo(2));
        }

        [Test]
        public void Facade_ReadOverlappingUpdateAndDisposal_PreservesSnapshotAndDropsMapper()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            var observed = new ObservedTranscriptEngine(engine);
            using var transcripts = new ConvaiTranscripts(observed);
            PublishTypedTurns(eventHub, 3);
            TranscriptTimeline historical = transcripts.CurrentTimeline;
            using var snapshotRead = new ManualResetEventSlim();
            using var releaseRead = new ManualResetEventSlim();
            using var updateFinished = new ManualResetEventSlim();
            observed.TimelineRead = () =>
            {
                snapshotRead.Set();
                if (!releaseRead.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Reader was not released.");
            };
            TranscriptTimeline captured = null;
            Exception readError = null, updateError = null;
            var reader = new Thread(() =>
            {
                try { captured = transcripts.CurrentTimeline; }
                catch (Exception error) { readError = error; }
            }) { IsBackground = true };
            var writer = new Thread(() =>
            {
                try
                {
                    eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "later", "later"));
                    transcripts.Dispose();
                }
                catch (Exception error) { updateError = error; }
                finally { updateFinished.Set(); }
            }) { IsBackground = true };
            reader.Start();
            bool reachedSnapshot = snapshotRead.Wait(TimeSpan.FromSeconds(2));
            writer.Start();
            bool completedBeforeRead = updateFinished.Wait(TimeSpan.FromSeconds(2));
            releaseRead.Set();
            Assert.That(reader.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(writer.Join(TimeSpan.FromSeconds(2)), Is.True);
            observed.TimelineRead = null;
            Assert.That(reachedSnapshot && completedBeforeRead, Is.True,
                "Engine reads must occur outside the mapper lock so publication and disposal can finish.");
            Assert.That(readError, Is.Null);
            Assert.That(updateError, Is.Null);
            Assert.That(captured.TurnsById.Count, Is.EqualTo(3));
            Assert.That(historical.TurnsById.Count, Is.EqualTo(3));
            Assert.That(PrivateField(transcripts, "_timelineMapper"), Is.Null);
            Assert.That(transcripts.CurrentTimeline.TurnsById.Count, Is.EqualTo(4));
            Assert.That(transcripts.GetTurns().Count, Is.EqualTo(4));
            Assert.That(transcripts.GetTurn("later"), Is.Not.Null);
            Assert.That(PrivateField(transcripts, "_timelineMapper"), Is.Null,
                "Post-disposal reads must not recreate a retained history cache.");
        }

        [Test]
        public void Facade_MappingReadAndChangedBatch_CannotMutateCachesConcurrently()
        {
            EventHub eventHub = CreateEventHub();
            using var engine = new RoomTranscriptEngine(eventHub);
            PublishTypedTurns(eventHub, 3);
            var observed = new ObservedTranscriptEngine(engine);
            using var transcripts = new ConvaiTranscripts(observed);
            using var mappingEntered = new ManualResetEventSlim();
            using var releaseMapping = new ManualResetEventSlim();
            observed.MapTimeline = snapshot => new TranscriptTimelineSnapshot(
                snapshot.Cursor, snapshot.ActiveTurns,
                new ObservedTurnList(snapshot.CommittedTurns, () =>
                {
                    mappingEntered.Set();
                    if (!releaseMapping.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Mapping was not released.");
                }), snapshot.TurnsById, snapshot.LatestTurnByParticipant);
            TranscriptTimeline first = null;
            Exception readError = null, writeError = null;
            var reader = new Thread(() =>
            {
                try { first = transcripts.CurrentTimeline; }
                catch (Exception error) { readError = error; }
            }) { IsBackground = true };
            var writer = new Thread(() =>
            {
                try { eventHub.Publish(PlayerTranscriptReceived.CreateTypedText("player-1", "You", "later", "later")); }
                catch (Exception error) { writeError = error; }
            }) { IsBackground = true };
            reader.Start();
            bool mappingStarted = mappingEntered.Wait(TimeSpan.FromSeconds(2));
            writer.Start();
            bool writerWaited = SpinWait.SpinUntil(
                () => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2));
            releaseMapping.Set();
            Assert.That(reader.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(writer.Join(TimeSpan.FromSeconds(2)), Is.True);
            observed.MapTimeline = null;
            Assert.That(mappingStarted && writerWaited, Is.True,
                "Change-batch mapping must wait until the concurrent read finishes mapping its immutable snapshot.");
            Assert.That(readError, Is.Null);
            Assert.That(writeError, Is.Null);
            Assert.That(first.TurnsById.Count, Is.EqualTo(3));
            Assert.That(transcripts.CurrentTimeline.TurnsById.Count, Is.EqualTo(4));
        }

        private sealed class ObservedTurnList : IReadOnlyList<TranscriptTurnSnapshot>
        {
            private readonly IReadOnlyList<TranscriptTurnSnapshot> _turns;
            private readonly Action _firstRead;
            internal ObservedTurnList(IReadOnlyList<TranscriptTurnSnapshot> turns, Action firstRead)
            { _turns = turns; _firstRead = firstRead; }
            public int Count => _turns.Count;
            public TranscriptTurnSnapshot this[int index]
            {
                get { if (index == 0) _firstRead(); return _turns[index]; }
            }
            public IEnumerator<TranscriptTurnSnapshot> GetEnumerator() => _turns.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static object PrivateField(object owner, string name) =>
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

        private sealed class ObservedTranscriptEngine : IRoomTranscriptEngine
        {
            private readonly IRoomTranscriptEngine _inner;
            internal Action TimelineRead;
            internal Func<TranscriptTimelineSnapshot, TranscriptTimelineSnapshot> MapTimeline;
            internal ObservedTranscriptEngine(IRoomTranscriptEngine inner) => _inner = inner;
            public TranscriptTimelineSnapshot CurrentTimeline
            {
                get
                {
                    TranscriptTimelineSnapshot snapshot = _inner.CurrentTimeline;
                    TimelineRead?.Invoke();
                    return MapTimeline?.Invoke(snapshot) ?? snapshot;
                }
            }
            public TranscriptCaptionSnapshot CurrentCaptions => _inner.CurrentCaptions;
            public event Action<TranscriptUpdateBatch> Changed { add => _inner.Changed += value; remove => _inner.Changed -= value; }
            public event Action<TranscriptCaptionSnapshot> CaptionsChanged { add => _inner.CaptionsChanged += value; remove => _inner.CaptionsChanged -= value; }
            public IReadOnlyList<TranscriptTurnSnapshot> GetTurns(TranscriptQuery query) => _inner.GetTurns(query);
            public TranscriptTurnSnapshot GetTurn(string id) => _inner.GetTurn(id);
            public TranscriptTurnSnapshot GetLatestTurn(TranscriptParticipantRef participant) => _inner.GetLatestTurn(participant);
            public void UpdatePlayerDisplayName(string name) => _inner.UpdatePlayerDisplayName(name);
            public void Clear() => _inner.Clear();
            public void Dispose() => _inner.Dispose();
        }

        private static void AssertTimelineMatchesRebuild(TranscriptTimelineSnapshot timeline)
        {
            TranscriptTurnSnapshot[] all = timeline.ActiveTurns
                .Concat(timeline.CommittedTurns)
                .OrderBy(turn => turn.RoomSequence)
                .ToArray();

            CollectionAssert.AreEqual(
                all.Where(IsCommitted).Select(turn => turn.TurnId).ToArray(),
                timeline.CommittedTurns.Select(turn => turn.TurnId).ToArray(),
                "Committed turns must be exactly the settled turns, in room order.");
            CollectionAssert.AreEqual(
                all.Where(turn => !IsCommitted(turn)).Select(turn => turn.TurnId).ToArray(),
                timeline.ActiveTurns.Select(turn => turn.TurnId).ToArray(),
                "Active turns must be exactly the open turns, in room order.");

            Dictionary<string, TranscriptTurnSnapshot> expectedById = new(StringComparer.Ordinal);
            Dictionary<string, TranscriptTurnSnapshot> expectedLatest = new(StringComparer.Ordinal);
            foreach (TranscriptTurnSnapshot turn in all)
            {
                expectedById[turn.TurnId] = turn;
                expectedLatest[ActorKey(turn)] = turn;
                if (!string.IsNullOrWhiteSpace(turn.Participant.PlayerOrCharacterId))
                    expectedLatest[$"{turn.Participant.Kind}:actor:{turn.Participant.PlayerOrCharacterId}"] = turn;
            }

            CollectionAssert.AreEquivalent(expectedById.Keys, timeline.TurnsById.Keys.ToArray());
            Assert.AreEqual(expectedById.Count, timeline.TurnsById.Count);
            foreach (KeyValuePair<string, TranscriptTurnSnapshot> pair in expectedById)
                Assert.AreSame(pair.Value, timeline.TurnsById[pair.Key]);

            CollectionAssert.AreEquivalent(expectedLatest.Keys, timeline.LatestTurnByParticipant.Keys.ToArray());
            Assert.AreEqual(expectedLatest.Count, timeline.LatestTurnByParticipant.Count);
            foreach (KeyValuePair<string, TranscriptTurnSnapshot> pair in expectedLatest)
                Assert.AreSame(pair.Value, timeline.LatestTurnByParticipant[pair.Key]);
        }

        private static void PublishSeededEvent(EventHub eventHub, Random random, int step)
        {
            string actor = random.Next(2) == 0 ? "char-1" : "char-2";
            switch (random.Next(7))
            {
                case 0:
                    eventHub.Publish(PlayerTranscriptReceived.Create(
                        "player-1", "You", $"interim {step}", false,
                        turnId: $"voice-{step / 5}", messageId: $"voice-{step / 5}"));
                    break;
                case 1:
                    eventHub.Publish(PlayerTranscriptReceived.Create(
                        "player-1", "You", $"final {step}", true, TranscriptionPhase.AsrFinal,
                        turnId: $"voice-{step / 5}", messageId: $"voice-{step / 5}"));
                    break;
                case 2:
                    eventHub.Publish(PlayerTranscriptReceived.CreateTypedText(
                        "player-1", "You", $"typed {step}", $"typed-{step}"));
                    break;
                case 3:
                    eventHub.Publish(CharacterTranscriptReceived.Create(
                        actor, actor, $"answer {step}", false, responseId: $"{actor}-response-{step / 4}"));
                    break;
                case 4:
                    eventHub.Publish(CharacterTranscriptReceived.Create(
                        actor, actor, $"answer {step} end", true, responseId: $"{actor}-response-{step / 4}"));
                    break;
                case 5:
                    eventHub.Publish(CharacterTurnCompleted.Create(actor, null, step % 3 == 0));
                    break;
                default:
                    eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking($"session-{step}"));
                    eventHub.Publish(PlayerSpeakingStateChanged.StoppedSpeaking($"session-{step}"));
                    break;
            }
        }

        private static bool IsCommitted(TranscriptTurnSnapshot turn) =>
            turn.State == TranscriptTurnState.Committed || turn.State == TranscriptTurnState.Interrupted;

        private static string ActorKey(TranscriptTurnSnapshot turn)
        {
            TranscriptParticipantRef participant = turn.Participant;
            if (!string.IsNullOrWhiteSpace(participant.ParticipantId))
                return $"{participant.Kind}:participant:{participant.ParticipantId}";
            if (!string.IsNullOrWhiteSpace(participant.PlayerOrCharacterId))
                return $"{participant.Kind}:actor:{participant.PlayerOrCharacterId}";
            return $"{participant.Kind}:anonymous";
        }

        private static TranscriptTurnSnapshot CreateSnapshot(string turnId, long roomSequence) =>
            new(
                turnId,
                roomSequence,
                new TranscriptParticipantRef(TranscriptParticipantKind.Player, "a", "A", null),
                DateTime.UnixEpoch,
                DateTime.UnixEpoch,
                null,
                TranscriptLifecycle.Stable,
                "text",
                string.Empty,
                false,
                Array.Empty<TranscriptSegmentSnapshot>());

        private static PlayerTranscriptReceived Interim(string text) =>
            PlayerTranscriptReceived.Create(
                "player-1", "You", text, false,
                turnId: "active-turn", messageId: "active-turn");

        private static void PublishTypedTurns(EventHub eventHub, int count)
        {
            for (int i = 0; i < count; i++)
                eventHub.Publish(PlayerTranscriptReceived.CreateTypedText(
                    "player-1", "You", $"typed text {i}", $"typed-{i}"));
        }

        private static TranscriptTurnSnapshot FindTurn(RoomTranscriptEngine engine, string turnId) =>
            engine.CurrentTimeline.TurnsById.TryGetValue(turnId, out TranscriptTurnSnapshot turn) ? turn : null;

        private static EventHub CreateEventHub() => new(new ImmediateScheduler());

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
