using Convai.Modules.Gaze.Core.Conversation;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The shared room model: who is in the conversation, who holds the floor, who is
    ///     expected to answer, and how the room hands out reaction slots so that two listeners
    ///     cannot react on the same frame.
    /// </summary>
    /// <remarks>
    ///     Every case pins exactly one rule and says which, so a mutation to that rule turns this
    ///     case red and nothing else. The floor cases are the four ported from the retired
    ///     <c>SpeakerAttentionDirectorTests</c>, asserting the same numbers against the new seam —
    ///     the floor logic moved, it was not retuned.
    /// </remarks>
    public sealed class ConversationRoomModelTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>Two characters, chosen so neither can collide with the reserved 0 / -1 keys.</summary>
        private const int KeyA = 101;

        private const int KeyB = 202;

        private ConversationRoomModel _room;
        private ConversationRoomTuning _tuning;
        private float _now;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            ConversationRoomModel.ResetShared();
            _room = ConversationRoomModel.Shared;
            _tuning = ConversationRoomTuning.Default;
            _now = 0f;
            _frame = 0;
        }

        [TearDown]
        public void TearDown() => ConversationRoomModel.ResetShared();

        [Test]
        public void ParticipantDiagnosticName_FollowsRenameReplacementAndExplicitLabel()
        {
            var first = new GameObject("FirstOwner");
            var second = new GameObject("SecondOwner");
            try
            {
                _room.ReportParticipant(KeyA, Vector3.zero, Vector3.forward, false, 0f, null, first.transform);
                Derive();
                Assert.That(_room.Current.TryGetParticipant(KeyA, out var participant), Is.True);
                Assert.That(participant.DisplayName, Is.EqualTo("FirstOwner"));
                first.name = "RenamedOwner";
                Assert.That(participant.DisplayName, Is.EqualTo("RenamedOwner"));
                _room.ReportParticipant(KeyA, Vector3.zero, Vector3.forward, false, 0f, null, second.transform);
                Assert.That(participant.DisplayName, Is.EqualTo("SecondOwner"));
                _room.ReportParticipant(KeyA, Vector3.zero, Vector3.forward, false, 0f, "ExplicitLabel");
                second.name = "IgnoredRename";
                Assert.That(participant.DisplayName, Is.EqualTo("ExplicitLabel"));
                Assert.That(participant.DisplayNameSource, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        // ── Harness ──────────────────────────────────────────────────────────

        private void ReportCharacter(int key, bool speaking) =>
            _room.ReportParticipant(
                key,
                new Vector3(key * 0.01f, 1.6f, 0f),
                Vector3.forward,
                speaking,
                speaking ? 0.5f : 0f,
                key == KeyA ? "A" : "B");

        private void ReportPlayer(bool localActive, bool serverSpeaking, int addressee, bool typed = false) =>
            _room.ReportPlayer(
                Vector3.zero,
                Vector3.forward,
                serverSpeaking,
                localActive,
                localActive ? 0.4f : 0f,
                addressee,
                typed);

        /// <summary>One frame: the reports are already in, so this only advances the clock and derives.</summary>
        private void Derive()
        {
            _now += Dt;
            _frame++;
            _room.Refresh(_now, _frame, in _tuning);
        }

        /// <summary>
        ///     Runs the room for <paramref name="seconds" /> with a fixed configuration: both
        ///     characters present, the player present, and whoever is named here talking.
        /// </summary>
        private void Run(
            float seconds,
            bool aSpeaking = false,
            bool bSpeaking = false,
            bool playerLocal = false,
            bool playerServer = false,
            int addressee = 0)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            for (int i = 0; i < steps; i++)
            {
                ReportCharacter(KeyA, aSpeaking);
                ReportCharacter(KeyB, bSpeaking);
                ReportPlayer(playerLocal, playerServer, addressee);
                Derive();
            }
        }

        // ── The floor ────────────────────────────────────────────────────────

        /// <summary>Pins the claim window: an empty floor is taken after ClaimSeconds (0.12), not before.</summary>
        [Test]
        public void AnEmptyFloorIsClaimedOnlyAfterTheClaimWindow()
        {
            Run(0.10f, aSpeaking: true);
            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.NobodyKey),
                "0.10 s of speech is inside the 0.12 s claim window.");

            Run(0.05f, aSpeaking: true);
            Assert.That(_room.Current.FloorKey, Is.EqualTo(KeyA));
        }

        /// <summary>
        ///     Pins the interruption rule: a challenger under InterruptionSeconds does not take a
        ///     held floor. Ported from the retired speaker-attention suite, same 0.6 s and 0.25 s.
        /// </summary>
        [Test]
        public void ABriefInterjection_DoesNotTakeTheFloorFromTheSpeaker()
        {
            Run(1f, aSpeaking: true);
            Assert.That(_room.Current.FloorKey, Is.EqualTo(KeyA));

            // A cough, an "mm-hmm", or one false positive from voice detection.
            Run(0.25f, aSpeaking: true, playerLocal: true);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(KeyA),
                "A room does not turn its heads for an interjection.");
        }

        /// <summary>Pins the other half of the same rule: sustained speech does take a held floor.</summary>
        [Test]
        public void ASustainedInterruption_DoesTakeTheFloor()
        {
            Run(1f, aSpeaking: true);
            Run(1.2f, aSpeaking: true, playerLocal: true);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey));
        }

        /// <summary>
        ///     Pins the expected answer: the addressee answering a player who has finished takes
        ///     the floor in the claim window, not after the interruption window. It is the next
        ///     turn, not somebody talking over the player.
        /// </summary>
        [Test]
        public void TheAddresseeAnsweringASilentPlayer_TakesTheFloorAtOnce()
        {
            Run(1f, playerLocal: true, addressee: KeyA);
            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey));

            // The player stops; within the hold, A answers.
            Run(0.3f, addressee: KeyA);
            Run(0.25f, aSpeaking: true, addressee: KeyA);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(KeyA),
                "The answer everybody was waiting for is the next turn, not an interruption.");
        }

        /// <summary>The same silence rule does not extend to somebody who was not expected to answer.</summary>
        [Test]
        public void ABystanderAnsweringASilentPlayer_StillNeedsTheInterruptionWindow()
        {
            Run(1f, playerLocal: true, addressee: KeyA);
            Run(0.3f, addressee: KeyA);
            Run(0.25f, bSpeaking: true, addressee: KeyA);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey));
        }

        /// <summary>
        ///     Pins "a holder who is still talking loses the floor only to the player". Without it
        ///     the character re-qualifies as a challenger the instant the player takes the floor
        ///     and the two of them trade it every InterruptionSeconds for as long as they overlap.
        /// </summary>
        [Test]
        public void TheFloorDoesNotPingPong_WhileThePlayerTalksOverACharacter()
        {
            Run(1f, aSpeaking: true);
            Run(1f, aSpeaking: true, playerLocal: true);
            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey));

            for (int i = 0; i < 240; i++)
            {
                Run(Dt, aSpeaking: true, playerLocal: true);
                Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey),
                    $"The floor went back to the character {i * Dt:0.00}s into the overlap.");
            }
        }

        /// <summary>Pins the hold: a speaker who stops keeps the floor for HoldSeconds, which is what carries a pause.</summary>
        [Test]
        public void APauseInsideATurn_DoesNotReleaseTheSpeaker()
        {
            Run(1f, aSpeaking: true);

            // The audio stops between sentences. The floor is still theirs.
            Run(1.2f);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(KeyA));
            Assert.That(_room.Current.FloorHolderSpeaking, Is.False,
                "Holding the floor is not the same as talking.");
        }

        /// <summary>Pins the release: past HoldSeconds of silence the floor is empty and remembered.</summary>
        [Test]
        public void ATurnThatReallyEnds_ReleasesTheFloor()
        {
            _tuning = new ConversationRoomTuning(holdSeconds: 1f);

            Run(1f, aSpeaking: true);
            Run(2f);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.NobodyKey),
                "The floor is not held forever.");
            Assert.That(_room.Current.LastFloorKey, Is.EqualTo(KeyA));
            Assert.That(_room.Current.LastFloorEndTime, Is.EqualTo(2f).Within(0.1f));
        }

        /// <summary>Pins TurnIndex: it moves on every floor change, taking and releasing alike.</summary>
        [Test]
        public void TurnIndexMovesOnEveryFloorChange()
        {
            _tuning = new ConversationRoomTuning(holdSeconds: 1f);

            Run(1f, aSpeaking: true);
            Assert.That(_room.Current.TurnIndex, Is.EqualTo(1), "A took the floor.");

            Run(2f);
            Assert.That(_room.Current.TurnIndex, Is.EqualTo(2), "A released it.");

            Run(1f, playerLocal: true);
            Assert.That(_room.Current.TurnIndex, Is.EqualTo(3), "The player took it.");
        }

        // ── Who answers next ─────────────────────────────────────────────────

        /// <summary>Pins the responder rule for a player turn: whoever the player is addressing.</summary>
        [Test]
        public void WhileThePlayerHoldsTheFloor_TheResponderIsTheAddressee()
        {
            Run(1f, playerLocal: true, addressee: KeyB);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey));
            Assert.That(_room.Current.ExpectedResponderKey, Is.EqualTo(KeyB));
        }

        /// <summary>Pins the responder rule for a character's turn: the player answers a character.</summary>
        [Test]
        public void WhileACharacterHoldsTheFloor_TheResponderIsThePlayer()
        {
            Run(1f, aSpeaking: true);

            Assert.That(_room.Current.ExpectedResponderKey, Is.EqualTo(ConversationRoomModel.PlayerKey));
        }

        /// <summary>
        ///     Pins the empty-floor responder — the room's answer to the felt defect where a
        ///     listener went back to the character who spoke last instead of to the character the
        ///     player had just turned to. An empty floor still points at the turn that ended.
        /// </summary>
        [Test]
        public void AfterAPlayerTurnEnds_TheResponderIsStillTheAddressee()
        {
            _tuning = new ConversationRoomTuning(holdSeconds: 1f);

            Run(1f, aSpeaking: true);
            Run(1.5f, playerLocal: true, addressee: KeyB);
            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey));

            Run(2f, addressee: KeyB);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.NobodyKey));
            Assert.That(_room.Current.ExpectedResponderKey, Is.EqualTo(KeyB),
                "B is who the player was talking to, so B is who the room is waiting for.");
        }

        // ── Onsets ───────────────────────────────────────────────────────────

        /// <summary>
        ///     Pins "the onset is the earliest edge seen". The local gate fires immediately and
        ///     the service's verdict lands 0.6 s later; the onset must stay at the local edge,
        ///     because that latency is the whole reason the local channel is read at all.
        /// </summary>
        [Test]
        public void ThePlayerOnset_IsTheLocalEdge_AndTheServerEdgeOnlyConfirmsIt()
        {
            Run(0.6f, playerLocal: true);
            float onset = _room.Current.Player.SpeechOnsetTime;

            Assert.That(onset, Is.EqualTo(Dt).Within(0.001f), "The first frame of local evidence is the onset.");
            Assert.That(_room.Current.LastOnset.Source, Is.EqualTo(ConversationOnsetSource.Local));

            Run(0.5f, playerLocal: true, playerServer: true);

            Assert.That(_room.Current.Player.SpeechOnsetTime, Is.EqualTo(onset).Within(0.001f),
                "The server edge confirms a turn the room already started; it does not restart it.");
        }

        /// <summary>
        ///     The negative control for the case above: with no local evidence the room has
        ///     nothing but the service's verdict, and reports it as such. Without this, a model
        ///     that hard-coded Local would still pass the test above.
        /// </summary>
        [Test]
        public void WithoutLocalEvidence_ThePlayerOnsetIsTheServerEdge()
        {
            Run(0.5f);
            Run(0.5f, playerServer: true);

            Assert.That(_room.Current.LastOnset.Source, Is.EqualTo(ConversationOnsetSource.Server));
            Assert.That(_room.Current.LastOnset.Key, Is.EqualTo(ConversationRoomModel.PlayerKey));
            Assert.That(_room.Current.Player.SpeechOnsetTime, Is.EqualTo(0.5f).Within(0.05f));
        }

        /// <summary>Pins SilenceSeconds: measured from the last word anybody said, and zero while somebody talks.</summary>
        [Test]
        public void SilenceIsMeasuredFromTheLastWordAnybodySpoke()
        {
            Run(1f, aSpeaking: true);
            Assert.That(_room.Current.SilenceSeconds, Is.EqualTo(0f).Within(0.001f));

            Run(2f);

            Assert.That(_room.Current.SilenceSeconds, Is.EqualTo(2f).Within(0.05f));
        }

        // ── Membership ───────────────────────────────────────────────────────

        /// <summary>Pins the expiry: a participant that stops reporting leaves after ExpireSeconds.</summary>
        [Test]
        public void AParticipantThatStopsReporting_LeavesTheRoom()
        {
            Run(1f);
            Assert.That(_room.Current.ParticipantCount, Is.EqualTo(3), "A, B and the player.");

            // A is disabled: nothing reports it any more.
            int steps = Mathf.RoundToInt(1.2f / Dt);
            for (int i = 0; i < steps; i++)
            {
                ReportCharacter(KeyB, false);
                ReportPlayer(false, false, 0);
                Derive();
            }

            Assert.That(_room.Current.TryGetParticipant(KeyA, out _), Is.False);
            Assert.That(_room.Current.ParticipantCount, Is.EqualTo(2));
        }

        /// <summary>
        ///     Pins the reserved keys. 0 means nobody and -1 means the player, so a character
        ///     reported under either would silently become one of them.
        /// </summary>
        [Test]
        public void TheReservedKeysAreNotAdmittedAsCharacters()
        {
            _room.ReportParticipant(0, Vector3.zero, Vector3.forward, true, 1f, "nobody");
            _room.ReportParticipant(
                ConversationRoomModel.PlayerKey, Vector3.zero, Vector3.forward, true, 1f, "impostor");
            Derive();

            Assert.That(_room.Current.ParticipantCount, Is.EqualTo(0));
            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.NobodyKey));
        }

        // ── Reaction slots ───────────────────────────────────────────────────

        /// <summary>
        ///     Pins the separation rule. Three listeners reacting to the same event at the same
        ///     instant is exactly the "they all turned together" defect; the room pushes each one
        ///     past the last, in the order they asked, so the result is stable run to run.
        /// </summary>
        [Test]
        public void ThreeListenersAskingAtOnce_AreSeparatedInTheOrderTheyAsked()
        {
            const float separation = 0.15f;

            float first = _room.ReserveReaction(7, KeyA, 7, 1f, separation);
            float second = _room.ReserveReaction(7, KeyB, 7, 1f, separation);
            float third = _room.ReserveReaction(7, 303, 7, 1f, separation);

            Assert.That(first, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(second, Is.EqualTo(1f + separation).Within(0.0001f));
            Assert.That(third, Is.EqualTo(1f + 2f * separation).Within(0.0001f));
            Assert.That(Mathf.Abs(second - first), Is.GreaterThanOrEqualTo(separation - 0.0001f));
            Assert.That(Mathf.Abs(third - second), Is.GreaterThanOrEqualTo(separation - 0.0001f));
        }

        /// <summary>
        ///     Pins the stability rule: a listener may ask every frame while it waits, and the
        ///     answer must not move — a slot that drifted would push its own reaction away
        ///     forever and the listener would never turn at all.
        /// </summary>
        [Test]
        public void AskingAgainForTheSameEvent_ReturnsTheSameSlot()
        {
            float reserved = _room.ReserveReaction(7, KeyA, 7, 1f, 0.15f);
            _room.ReserveReaction(7, KeyB, 7, 1f, 0.15f);

            for (int i = 0; i < 10; i++)
                Assert.That(_room.ReserveReaction(7, KeyA, 7, 1f, 0.15f), Is.EqualTo(reserved).Within(0.0001f));
        }

        /// <summary>
        ///     Pins the lane scoping: slots separate bookings within one lane, not across lanes.
        ///     Two things landing on two different people must not push each other apart.
        /// </summary>
        [Test]
        public void SlotsInDifferentLanes_DoNotPushEachOtherApart()
        {
            float turnSlot = _room.ReserveReaction(7, KeyA, 7, 1f, 0.15f);
            float arrivalSlot = _room.ReserveReaction(8, KeyA, 8, 1f, 0.15f);

            Assert.That(turnSlot, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(arrivalSlot, Is.EqualTo(1f).Within(0.0001f));
        }

        /// <summary>
        ///     Pins the other half of the lane rule, which is why the lane exists at all: two
        ///     listeners arriving on one face are separated however differently they got there.
        ///     Booked per cause, these three would have been three lanes of one and would all
        ///     have landed on the same frame.
        /// </summary>
        [Test]
        public void ThreeDifferentBeatsLandingOnOneFace_AreStillSeparated()
        {
            const float separation = 0.15f;
            int lane = ConversationRoomModel.LaneFor(KeyA);

            float onset = _room.ReserveReaction(lane, KeyA, 100, 1f, separation);
            float promotion = _room.ReserveReaction(lane, KeyB, 200, 1f, separation);
            float reflex = _room.ReserveReaction(lane, 303, 300, 1f, separation);

            Assert.That(Mathf.Abs(promotion - onset), Is.GreaterThanOrEqualTo(separation - 0.0001f));
            Assert.That(Mathf.Abs(reflex - promotion), Is.GreaterThanOrEqualTo(separation - 0.0001f));
        }

        /// <summary>
        ///     Pins the second identity: one listener may hold several bookings in one lane —
        ///     it noticed somebody start talking AND is following the floor as it moves to them —
        ///     and asking for one must never hand back the other.
        /// </summary>
        [Test]
        public void OneListenerWithTwoBeatsInALane_KeepsThemApart()
        {
            const float separation = 0.15f;
            int lane = ConversationRoomModel.LaneFor(KeyA);

            float onset = _room.ReserveReaction(lane, KeyA, 100, 1f, separation);
            float promotion = _room.ReserveReaction(lane, KeyA, 200, 1f, separation);

            Assert.That(onset, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(promotion, Is.EqualTo(1f + separation).Within(0.0001f));
            Assert.That(_room.ReserveReaction(lane, KeyA, 100, 1f, separation),
                Is.EqualTo(onset).Within(0.0001f), "Asking again handed back the other booking.");
        }

        /// <summary>
        ///     Pins the withdrawal rule: a booking that is handed back stops spacing the ones
        ///     that come after it. The listener that cancels its own beat and makes another for
        ///     the next turn is the case — the two are one head turn, not two.
        /// </summary>
        /// <remarks>
        ///     Red before the fix: nothing removed a booking, so the cancelled one stayed in the
        ///     lane and the replacement resolved a whole separation late.
        /// </remarks>
        [Test]
        public void AReleasedBooking_StopsSpacingTheBookingsAfterIt()
        {
            const float separation = 0.35f;
            int lane = ConversationRoomModel.LaneFor(KeyA);

            _room.ReserveReaction(lane, KeyB, 100, 0.15f, separation);
            _room.ReleaseReaction(lane, KeyB, 100);

            Assert.That(_room.ReserveReaction(lane, KeyB, 200, 0.28f, separation),
                Is.EqualTo(0.28f).Within(0.0001f),
                "The withdrawn booking was still pushing this listener's next one.");
        }

        /// <summary>
        ///     The negative control for the case above, run through the un-released path: while
        ///     the first booking still stands it must go on spacing the second, or the release
        ///     above would be proving nothing.
        /// </summary>
        [Test]
        public void ABookingThatWasNeverReleased_StillSpacesTheBookingsAfterIt()
        {
            const float separation = 0.35f;
            int lane = ConversationRoomModel.LaneFor(KeyA);

            _room.ReserveReaction(lane, KeyB, 100, 0.15f, separation);

            Assert.That(_room.ReserveReaction(lane, KeyB, 200, 0.28f, separation),
                Is.EqualTo(0.15f + separation).Within(0.0001f));
        }

        /// <summary>
        ///     Pins what a release must not disturb: the lane is compacted, so every booking that
        ///     was not the one withdrawn keeps the moment it already had, and the next reservation
        ///     lands on free ground rather than on top of one of them.
        /// </summary>
        [Test]
        public void ReleasingOneBookingInALane_LeavesTheOthersWhereTheyWere()
        {
            const float separation = 0.15f;
            int lane = ConversationRoomModel.LaneFor(KeyA);

            _room.ReserveReaction(lane, KeyA, 100, 1f, separation);
            float kept = _room.ReserveReaction(lane, KeyB, 200, 1.5f, separation);
            _room.ReleaseReaction(lane, KeyA, 100);

            Assert.That(_room.ReserveReaction(lane, KeyB, 200, 1.5f, separation),
                Is.EqualTo(kept).Within(0.0001f), "The surviving booking moved, or was overwritten.");
            Assert.That(_room.ReserveReaction(lane, 303, 300, 1.6f, separation),
                Is.EqualTo(1.5f + separation).Within(0.0001f),
                "A later booking was no longer spaced against the one that stayed.");
        }

        /// <summary>Releasing something nobody booked is a no-op, not a hole in a lane.</summary>
        [Test]
        public void ReleasingABookingThatWasNeverMade_ChangesNothing()
        {
            const float separation = 0.15f;
            int lane = ConversationRoomModel.LaneFor(KeyA);

            float onset = _room.ReserveReaction(lane, KeyA, 100, 1f, separation);
            _room.ReleaseReaction(lane, KeyB, 999);
            _room.ReleaseReaction(ConversationRoomModel.LaneFor(KeyB), KeyA, 100);

            Assert.That(_room.ReserveReaction(lane, KeyA, 100, 1f, separation),
                Is.EqualTo(onset).Within(0.0001f));
        }

        // ── The frame guard ──────────────────────────────────────────────────

        /// <summary>
        ///     Pins the once-per-frame guard. Every character in the room calls Refresh, and they
        ///     must all read one derivation — a room that re-derived per caller would advance the
        ///     floor's claim timer once per character and take the floor N times faster.
        /// </summary>
        [Test]
        public void TheRoomDerivesOncePerFrame_HoweverManyCharactersAsk()
        {
            ReportCharacter(KeyA, true);
            _room.Refresh(1f, 42, in _tuning);
            int turnIndex = _room.Current.TurnIndex;

            for (int i = 0; i < 30; i++)
                _room.Refresh(1f + i, 42, in _tuning);

            Assert.That(_room.Current.Time, Is.EqualTo(1f).Within(0.0001f),
                "A later caller in the same frame reads the derivation the first one produced.");
            Assert.That(_room.Current.TurnIndex, Is.EqualTo(turnIndex));
        }

        /// <summary>Pins the player being a participant like anybody else, under PlayerKey.</summary>
        [Test]
        public void ThePlayerIsAParticipant()
        {
            Run(0.5f, playerLocal: true, addressee: KeyA);

            Assert.That(_room.Current.TryGetParticipant(
                ConversationRoomModel.PlayerKey, out ConversationParticipant player), Is.True);
            Assert.That(player.IsPlayer, Is.True);
            Assert.That(player.IsSpeaking, Is.True);
            Assert.That(player.AddresseeKey, Is.EqualTo(KeyA));
            Assert.That(_room.Current.ParticipantCount, Is.EqualTo(3));
        }

        /// <summary>
        ///     Pins the typed turn: a message the microphone never hears still holds the floor,
        ///     and stops holding it once the window closes.
        /// </summary>
        [Test]
        public void ATypedMessage_TakesTheFloorAndThenLetsItGo()
        {
            _tuning = new ConversationRoomTuning(holdSeconds: 0.2f, typedFloorSeconds: 1.4f);

            ReportCharacter(KeyA, false);
            ReportCharacter(KeyB, false);
            ReportPlayer(false, false, KeyA, true);
            Derive();

            Run(0.5f, addressee: KeyA);
            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.PlayerKey));

            Run(2f, addressee: KeyA);

            Assert.That(_room.Current.Player.IsSpeaking, Is.False, "The typed window closed.");
            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.NobodyKey));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancellingAfterWantedTimeButBeforeResolvedTime_ReleasesTheReservation(bool shiftedWithoutPolling)
        {
            if (shiftedWithoutPolling)
            {
                _room.ReserveReaction(7, 2, 2, 1f, 0.35f);
                _room.ReserveReaction(7, 1, 1, 1f, 0.35f);
            }
            else
            {
                _room.ReserveReaction(7, 1, 1, 1f, 0.35f);
                Assert.That(_room.ReserveReaction(7, 2, 2, 1f, 0.35f), Is.EqualTo(1.35f).Within(0.0001f));
            }
            _room.Refresh(1.1f, 1, in _tuning);
            _room.ReleaseReaction(7, 2, 2);
            Assert.That(_room.ReserveReaction(7, 3, 3, 1.2f, 0.35f), Is.EqualTo(1.35f).Within(0.0001f));
        }

        [Test]
        public void CancellingALandedReaction_PreservesItsResolvedHistory()
        {
            _room.ReserveReaction(7, 1, 1, 1f, 0.35f);
            _room.ReserveReaction(7, 2, 2, 1f, 0.35f);
            _room.Refresh(1.4f, 1, in _tuning);
            _room.ReleaseReaction(7, 2, 2);
            Assert.That(_room.ReserveReaction(7, 3, 3, 1.4f, 0.35f), Is.EqualTo(1.7f).Within(0.0001f));
        }

        [TestCase(3)]
        [TestCase(4)]
        public void PulseAfter128Seconds_ReceivesANewFutureReservation(int channel)
        {
            int first = ConversationGazeDirector.PulseEventId(channel, 2f);
            _room.ReserveReaction(7, KeyA, first, 2.4f, 0.15f);
            _room.Refresh(130f, 1, in _tuning);
            int next = ConversationGazeDirector.PulseEventId(channel, 130f);
            float reserved = _room.ReserveReaction(7, KeyA, next, 130.4f, 0.15f);
            Assert.That(reserved, Is.EqualTo(130.4f).Within(0.0001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NewTarget_ReusesAnInactiveLaneBeforeAnActiveLane(bool keepLandedHistory)
        {
            // Fill the eight target lanes; lane 1 is next in the ring and is still active.
            for (int lane = 1; lane <= 8; lane++)
                _room.ReserveReaction(lane, KeyA, 1, lane == 5 && keepLandedHistory ? 0.1f : 10f, 0.15f);
            if (!keepLandedHistory) _room.ReleaseReaction(5, KeyA, 1);
            _room.Refresh(1f, 1, in _tuning);
            _room.ReserveReaction(9, KeyA, 1, 10f, 0.15f);
            Assert.That(_room.ReserveReaction(1, KeyB, 2, 10f, 0.15f),
                Is.EqualTo(10.15f).Within(0.0001f), "The active target lost its first listener's reservation.");
        }

    }
}
