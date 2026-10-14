using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Room;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    /// <summary>
    ///     Covers the one answer everything that gates player input asks.
    /// </summary>
    /// <remarks>
    ///     The case that matters most is a connected room whose addressed character the service has
    ///     not announced yet. Every other check in the SDK passes there — injected, owned, room
    ///     connected — and a message sent then reaches nobody, silently. If that case ever reports
    ///     anything but <see cref="ConvaiConversationAvailability.Preparing" />, the gap is back.
    /// </remarks>
    [TestFixture]
    public sealed class ConversationAvailabilityResolverTests
    {
        /// <remarks>
        ///     <paramref name="roomHasRoster" /> defaults to whether a membership was given, because
        ///     for every case but one those are the same fact. The exception — a roster this
        ///     character has no seat in — is the case this overload exists to let a test state.
        /// </remarks>
        private static ConvaiConversationAvailability Resolve(
            SessionState roomState = SessionState.Connected,
            CharacterRoomStatus? membershipStatus = null,
            bool isCharacterReady = true,
            bool isSpeaking = false,
            bool hasCharacter = true,
            bool isInjected = true,
            bool? roomHasRoster = null) =>
            ConversationAvailabilityResolver.Resolve(
                hasCharacter,
                isInjected,
                roomState,
                roomHasRoster ?? membershipStatus.HasValue,
                membershipStatus,
                isCharacterReady,
                isSpeaking);

        [Test]
        public void AConnectedRoomWhoseCharacterIsNotAnnouncedYetIsPreparing()
        {
            Assert.That(
                Resolve(SessionState.Connected, CharacterRoomStatus.Starting, isCharacterReady: false),
                Is.EqualTo(ConvaiConversationAvailability.Preparing),
                "This is the gap that lost the player's first message: everything else says the "
                + "SDK is connected and healthy.");
        }

        [Test]
        public void AConnectedRoomStillPreparesWhenOnlyTheRosterKnows()
        {
            // The character's own ready flag can be set by a recovery before the service confirms
            // it. Where a roster exists it is authoritative, so it must win.
            Assert.That(
                Resolve(SessionState.Connected, CharacterRoomStatus.Starting, isCharacterReady: true),
                Is.EqualTo(ConvaiConversationAvailability.Preparing),
                "The roster is what the room actually routes by; a locally recovered ready flag "
                + "must not unlock input ahead of it.");
        }

        [Test]
        public void AnAnnouncedCharacterIsReady()
        {
            Assert.That(
                Resolve(SessionState.Connected, CharacterRoomStatus.Ready),
                Is.EqualTo(ConvaiConversationAvailability.Ready));
        }

        [Test]
        public void ASingleCharacterRoomFallsBackToTheReadinessSignal()
        {
            // No roster: membershipStatus is null and the service's own signal is all there is.
            Assert.That(
                Resolve(SessionState.Connected, null, isCharacterReady: false, roomHasRoster: false),
                Is.EqualTo(ConvaiConversationAvailability.Preparing));
            Assert.That(
                Resolve(SessionState.Connected, null, isCharacterReady: true, roomHasRoster: false),
                Is.EqualTo(ConvaiConversationAvailability.Ready));
        }

        [Test]
        public void ARosterRoomRefusesACharacterThatHasNoSeatInIt()
        {
            // The discriminating case. A missing membership used to mean "this room keeps no
            // roster", so a character left out of one fell through to its own readiness flag and
            // reported Ready — a character in the scene, apparently able to hear, that the room
            // cannot route a single word to. Only the roster answers here.
            Assert.That(
                Resolve(SessionState.Connected, null, isCharacterReady: true, roomHasRoster: true),
                Is.EqualTo(ConvaiConversationAvailability.Unavailable),
                "A character with no seat in the roster is not reachable, however ready its own "
                + "flag says it is. Reporting Ready here opens a chat field that sends nowhere.");

            Assert.That(
                Resolve(SessionState.Connected, null, isCharacterReady: true, roomHasRoster: true)
                    .CanAcceptPlayerInput(),
                Is.False);
        }

        [Test]
        public void ACharacterWithNoSeatIsUnreachableWhileTheRoomIsStillComingUp()
        {
            // Connecting is the honest answer for a character that is joining. One that is not in
            // the roster is not joining anything, and naming it as settling promises an arrival
            // that never comes.
            foreach (SessionState state in new[] { SessionState.Connecting, SessionState.Reconnecting })
                Assert.That(
                    Resolve(state, null, isCharacterReady: true, roomHasRoster: true),
                    Is.EqualTo(ConvaiConversationAvailability.Unavailable),
                    $"room state {state}");
        }

        [Test]
        public void HavingNoSeatDoesNotOutrankHavingNothingToTalkTo()
        {
            // Order matters: a destroyed or disabled character is NoCharacter whether or not a
            // roster exists, because the scene half is the one the room can be wrong about.
            Assert.That(
                Resolve(hasCharacter: false, roomHasRoster: true),
                Is.EqualTo(ConvaiConversationAvailability.NoCharacter));
            Assert.That(
                Resolve(isInjected: false, roomHasRoster: true),
                Is.EqualTo(ConvaiConversationAvailability.NoCharacter));
        }

        [Test]
        public void ASpeakingCharacterStillAcceptsInput()
        {
            ConvaiConversationAvailability availability =
                Resolve(SessionState.Connected, CharacterRoomStatus.Ready, isSpeaking: true);

            Assert.That(availability, Is.EqualTo(ConvaiConversationAvailability.Answering));
            Assert.That(
                availability.CanAcceptPlayerInput(),
                Is.True,
                "Interrupting an answer is ordinary conversation; refusing it would make the "
                + "player wait out every reply.");
        }

        [TestCase(ConvaiConversationAvailability.Ready)]
        [TestCase(ConvaiConversationAvailability.Answering)]
        public void ATargetChangeSynchronouslyClosesAnOtherwiseReadyInputWindow(
            ConvaiConversationAvailability availability)
        {
            Assert.That(
                ConversationAvailabilityResolver.ApplyRoutingTransition(
                    availability,
                    targetChangeInFlight: true),
                Is.EqualTo(ConvaiConversationAvailability.Preparing),
                "TalkTo can be followed by text or push-to-talk in the same frame, before the next " +
                "availability tick. The property read itself must see the pending route.");
        }

        [Test]
        public void ACompletedTargetChangeLeavesTheAddressedAvailabilityUntouched()
        {
            Assert.That(
                ConversationAvailabilityResolver.ApplyRoutingTransition(
                    ConvaiConversationAvailability.Ready,
                    targetChangeInFlight: false),
                Is.EqualTo(ConvaiConversationAvailability.Ready));
        }

        [Test]
        public void AFailedCharacterIsUnavailableWhateverTheRoomIsDoing()
        {
            foreach (SessionState state in new[]
                     {
                         SessionState.Connected, SessionState.Connecting, SessionState.Reconnecting
                     })
            {
                Assert.That(
                    Resolve(state, CharacterRoomStatus.Failed),
                    Is.EqualTo(ConvaiConversationAvailability.Unavailable),
                    $"A failed character reported as settling promises a recovery that is not "
                    + $"coming (room state {state}).");
            }
        }

        [Test]
        public void RoomStatesMapToTheirOwnAnswers()
        {
            Assert.That(Resolve(SessionState.Disconnected), Is.EqualTo(ConvaiConversationAvailability.Offline));
            Assert.That(Resolve(SessionState.Disconnecting), Is.EqualTo(ConvaiConversationAvailability.Offline));
            Assert.That(Resolve(SessionState.Connecting), Is.EqualTo(ConvaiConversationAvailability.Connecting));
            Assert.That(Resolve(SessionState.Reconnecting), Is.EqualTo(ConvaiConversationAvailability.Connecting));
            Assert.That(Resolve(SessionState.Error), Is.EqualTo(ConvaiConversationAvailability.Unavailable));
        }

        [Test]
        public void NothingToTalkToReportsNoCharacter()
        {
            Assert.That(Resolve(hasCharacter: false), Is.EqualTo(ConvaiConversationAvailability.NoCharacter));
            Assert.That(Resolve(isInjected: false), Is.EqualTo(ConvaiConversationAvailability.NoCharacter));
        }

        [Test]
        public void OnlyReadyAndAnsweringAcceptInput()
        {
            foreach (ConvaiConversationAvailability value in
                     System.Enum.GetValues(typeof(ConvaiConversationAvailability)))
            {
                bool expected = value is ConvaiConversationAvailability.Ready
                    or ConvaiConversationAvailability.Answering;
                Assert.That(
                    value.CanAcceptPlayerInput(),
                    Is.EqualTo(expected),
                    $"{value} must{(expected ? "" : " not")} accept input. A new state added without "
                    + "deciding this would silently inherit an answer nobody chose.");
            }
        }

        [Test]
        public void OnlyConnectingAndPreparingResolveOnTheirOwn()
        {
            foreach (ConvaiConversationAvailability value in
                     System.Enum.GetValues(typeof(ConvaiConversationAvailability)))
            {
                bool expected = value is ConvaiConversationAvailability.Connecting
                    or ConvaiConversationAvailability.Preparing;
                Assert.That(value.IsSettling(), Is.EqualTo(expected), $"{value}");
            }
        }
    }

    [TestFixture]
    public sealed class AutoStartMicrophoneReadinessGateTests
    {
        [Test]
        public void DeadlineWarnsButDoesNotOpenBeforeInitialReadiness()
        {
            AutoStartMicrophoneReadinessAction action = AutoStartMicrophoneReadinessGate.Decide(
                isConnected: true,
                hasMultiCharacterSession: true,
                initialCharacterIsReady: false,
                warningDeadlineElapsed: true,
                warningAlreadyEmitted: false);

            Assert.That(action, Is.EqualTo(AutoStartMicrophoneReadinessAction.WarnAndContinueWaiting));
        }

        [Test]
        public void WarningIsEmittedOnlyOnceWhileReadinessRemainsPending()
        {
            AutoStartMicrophoneReadinessAction action = AutoStartMicrophoneReadinessGate.Decide(
                isConnected: true,
                hasMultiCharacterSession: true,
                initialCharacterIsReady: false,
                warningDeadlineElapsed: true,
                warningAlreadyEmitted: true);

            Assert.That(action, Is.EqualTo(AutoStartMicrophoneReadinessAction.ContinueWaiting));
        }

        [Test]
        public void InitialReadinessOpensTheMicrophoneAfterAnyLengthOfWait()
        {
            AutoStartMicrophoneReadinessAction action = AutoStartMicrophoneReadinessGate.Decide(
                isConnected: true,
                hasMultiCharacterSession: true,
                initialCharacterIsReady: true,
                warningDeadlineElapsed: true,
                warningAlreadyEmitted: true);

            Assert.That(action, Is.EqualTo(AutoStartMicrophoneReadinessAction.OpenMicrophone));
        }

        [Test]
        public void DisconnectAbortsThePendingAutoStart()
        {
            AutoStartMicrophoneReadinessAction action = AutoStartMicrophoneReadinessGate.Decide(
                isConnected: false,
                hasMultiCharacterSession: true,
                initialCharacterIsReady: false,
                warningDeadlineElapsed: false,
                warningAlreadyEmitted: false);

            Assert.That(action, Is.EqualTo(AutoStartMicrophoneReadinessAction.Abort));
        }
    }
}
