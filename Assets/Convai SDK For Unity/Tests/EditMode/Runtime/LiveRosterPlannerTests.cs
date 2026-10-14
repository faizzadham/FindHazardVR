using System.Collections.Generic;
using Convai.Domain.Emotion;
using Convai.Runtime.Behaviors;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Room;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    /// <summary>
    ///     Covers the decision behind "a character that appears mid-session joins the room".
    /// </summary>
    /// <remarks>
    ///     The feature is invisible when it declines — the character is simply not in the conversation,
    ///     which is also what a scene fault looks like. These assert the verdict rather than only the
    ///     outcome, so a change that starts declining for a new reason cannot pass as "no change".
    /// </remarks>
    [TestFixture]
    public sealed class LiveRosterPlannerTests
    {
        private List<IConvaiCharacterAgent> _added;
        private List<int> _removed;

        [SetUp]
        public void SetUp()
        {
            _added = new List<IConvaiCharacterAgent>();
            _removed = new List<int>();
        }

        private LiveRosterVerdict Plan(
            IReadOnlyList<IConvaiCharacterAgent> desired,
            IReadOnlyList<IConvaiCharacterAgent> current,
            bool connected = true,
            bool sessionReady = true,
            bool ownershipAvailable = true,
            bool playerUnchanged = true,
            IConvaiCharacterAgent roomStartingCharacter = null,
            IConvaiCharacterAgent desiredStartingCharacter = null,
            bool desiredStartingCharacterIsExplicit = false,
            IReadOnlyList<IConvaiCharacterAgent> retained = null) =>
            LiveRosterPlanner.Plan(
                connected,
                sessionReady,
                ownershipAvailable,
                playerUnchanged,
                roomStartingCharacter,
                desiredStartingCharacter,
                desiredStartingCharacterIsExplicit,
                desired,
                retained ?? desired,
                current,
                _added,
                _removed);

        [Test]
        public void ACharacterThatAppearsInTheSceneJoinsTheRoom()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia, james },
                current: new IConvaiCharacterAgent[] { sofia });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply));
            Assert.That(_added, Is.EqualTo(new[] { james }));
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void ACharacterThatDisappearsLeavesTheRoom()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia },
                current: new IConvaiCharacterAgent[] { sofia, james });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply));
            Assert.That(_added, Is.Empty);
            Assert.That(_removed, Is.EqualTo(new[] { 1 }),
                "The index has to point at James's position so the caller can find his membership.");
        }

        [Test]
        public void OneCharacterSwappedForAnotherIsBothAJoinAndALeave()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");
            var marina = new FakeAgent("Marina");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia, marina },
                current: new IConvaiCharacterAgent[] { sofia, james });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply));
            Assert.That(_added, Is.EqualTo(new[] { marina }));
            Assert.That(_removed, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void AnUnchangedRosterIsNotARosterChange()
        {
            var sofia = new FakeAgent("Sofia");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia },
                current: new IConvaiCharacterAgent[] { sofia });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.NoRosterChange));
            Assert.That(_added, Is.Empty);
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void OrderIsNotARosterChange()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { james, sofia },
                current: new IConvaiCharacterAgent[] { sofia, james });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.NoRosterChange),
                "Reordering a hierarchy must not re-add and re-remove everybody.");
        }

        [Test]
        public void EmptyingTheRoomIsRefusedAndLeftToTheReconnectPath()
        {
            var sofia = new FakeAgent("Sofia");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { },
                current: new IConvaiCharacterAgent[] { sofia });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.WouldEmptyTheRoom));
            Assert.That(_removed, Is.Empty, "A refused plan must not leave the caller a list to act on.");
        }

        [Test]
        public void RemovingEverybodyWhileSomebodyElseArrivesIsStillApplied()
        {
            var sofia = new FakeAgent("Sofia");
            var marina = new FakeAgent("Marina");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { marina },
                current: new IConvaiCharacterAgent[] { sofia });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply),
                "The room never empties here — Marina takes Sofia's place.");
            Assert.That(_added, Is.EqualTo(new[] { marina }));
            Assert.That(_removed, Is.EqualTo(new[] { 0 }));
        }

        [Test]
        public void AChangedPlayerIsAReconnect()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia, james },
                current: new IConvaiCharacterAgent[] { sofia },
                playerUnchanged: false);

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.PlayerChanged));
            Assert.That(_added, Is.Empty);
        }

        [Test]
        public void AnAssignedInitialCharacterTheRoomDidNotStartOnIsAReconnect()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");
            var marina = new FakeAgent("Marina");

            // The project assigned Marina as Initial Character. That decides how the room is
            // created, and this room was not created that way, so it belongs to the reconnect path.
            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia, james },
                current: new IConvaiCharacterAgent[] { sofia },
                roomStartingCharacter: sofia,
                desiredStartingCharacter: marina,
                desiredStartingCharacterIsExplicit: true);

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.StartingCharacterChanged));
            Assert.That(_added, Is.Empty);
        }

        [Test]
        public void TheCharacterThatOpenedTheRoomCanLeaveIt()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            // James opened the room and has now been disabled, so the derived starting character is
            // Sofia. Nothing was decided; the answer moved because the roster did.
            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia },
                current: new IConvaiCharacterAgent[] { james, sofia },
                roomStartingCharacter: james,
                desiredStartingCharacter: sofia);

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply));
            Assert.That(_removed, Is.EqualTo(new[] { 0 }));
            Assert.That(_added, Is.Empty);
        }

        [Test]
        public void TheCharacterThatOpenedTheRoomCanRejoinAfterLeaving()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            // The second half, and the one that proves the rule rather than a special case. James
            // has already left, so the room's own record still names him while the derived answer
            // is Sofia — and they can never agree again. Guarding on that disagreement made every
            // later edit impossible, so re-enabling him did nothing at all.
            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia, james },
                current: new IConvaiCharacterAgent[] { sofia },
                roomStartingCharacter: james,
                desiredStartingCharacter: sofia);

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply));
            Assert.That(_added, Is.EqualTo(new[] { james }));
            Assert.That(_removed, Is.Empty);
        }

        [Test]
        public void ADerivedStartingCharacterMovingIsNotADecision()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            // No Initial Character is assigned, so scene order decides — and scene order answers
            // differently the moment the roster changes. That is not something to reconnect for.
            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { james, sofia },
                current: new IConvaiCharacterAgent[] { james },
                roomStartingCharacter: james,
                desiredStartingCharacter: sofia);

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply));
            Assert.That(_added, Is.EqualTo(new[] { sofia }));
        }

        [Test]
        public void ADisabledCharacterKeepsItsSeat()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            // James was disabled, so he cannot join a room being opened now — but the project still
            // wants him in this one. Removing him would turn hiding a character into leaving the
            // conversation, and coming back into a fresh membership with a fresh round trip.
            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia },
                current: new IConvaiCharacterAgent[] { sofia, james },
                retained: new IConvaiCharacterAgent[] { sofia, james });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.NoRosterChange));
            Assert.That(_removed, Is.Empty);
            Assert.That(_added, Is.Empty);
        }

        [Test]
        public void ACharacterTheProjectNoLongerWantsStillLeaves()
        {
            var sofia = new FakeAgent("Sofia");
            var james = new FakeAgent("James");

            // The other half of the same rule, and the reason it is two lists rather than none:
            // dropped from ownership or from the room selection is a decision about the roster, and
            // it still empties the seat.
            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia },
                current: new IConvaiCharacterAgent[] { sofia, james },
                retained: new IConvaiCharacterAgent[] { sofia });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.Apply));
            Assert.That(_removed, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void ARoomThatIsNotConnectedHasNoRosterToEdit()
        {
            var sofia = new FakeAgent("Sofia");

            Assert.That(
                Plan(new IConvaiCharacterAgent[] { sofia }, new IConvaiCharacterAgent[] { }, connected: false),
                Is.EqualTo(LiveRosterVerdict.NotConnected));
        }

        [Test]
        public void ARoomStillStartingHasNoSettledRosterYet()
        {
            var sofia = new FakeAgent("Sofia");

            Assert.That(
                Plan(new IConvaiCharacterAgent[] { sofia }, new IConvaiCharacterAgent[] { }, sessionReady: false),
                Is.EqualTo(LiveRosterVerdict.SessionNotReady),
                "Editing the roster of a room that has not finished starting races its own creation.");
        }

        [Test]
        public void OwnershipThatCannotBeCapturedIsNotAnEmptyRoster()
        {
            var sofia = new FakeAgent("Sofia");

            Assert.That(
                Plan(null, new IConvaiCharacterAgent[] { sofia }),
                Is.EqualTo(LiveRosterVerdict.OwnershipUnavailable),
                "Treating a failed capture as 'nobody should be here' would empty the room.");
        }

        [Test]
        public void ARetiredMembershipDoesNotCountTowardsEmptyingTheRoom()
        {
            var sofia = new FakeAgent("Sofia");

            // A membership whose character has gone is already not in the room; removing the one real
            // member alongside it still empties it.
            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { },
                current: new IConvaiCharacterAgent[] { null, sofia });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.WouldEmptyTheRoom));
        }

        [Test]
        public void ANullCharacterInTheSceneIsNotAJoin()
        {
            var sofia = new FakeAgent("Sofia");

            LiveRosterVerdict verdict = Plan(
                desired: new IConvaiCharacterAgent[] { sofia, null },
                current: new IConvaiCharacterAgent[] { sofia });

            Assert.That(verdict, Is.EqualTo(LiveRosterVerdict.NoRosterChange));
            Assert.That(_added, Is.Empty);
        }

        private sealed class FakeAgent : IConvaiCharacterAgent
        {
            internal FakeAgent(string name) => CharacterName = name;

            public string CharacterId => CharacterName;
            public string CharacterName { get; }
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext => null;
            public EmotionDetectionMode EmotionDetectionMode => default;

            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }

            public override string ToString() => CharacterName;
        }
    }
}
