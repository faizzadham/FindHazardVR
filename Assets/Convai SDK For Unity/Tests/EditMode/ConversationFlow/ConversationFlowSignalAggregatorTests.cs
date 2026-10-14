using System;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Models;
using Convai.Modules.ConversationFlow.Core;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.ConversationFlow
{
    public sealed class ConversationFlowSignalAggregatorTests
    {
        [TearDown]
        public void TearDown() => LogAssert.NoUnexpectedReceived();

        [Test]
        public void Attach_CanRebind_WhenEventHubArrivesLate()
        {
            const string characterId = "char-1";

            var aggregator = new ConversationFlowSignalAggregator(characterId);
            var dialoguePhase = new StubDialoguePhaseProvider();

            aggregator.Attach(null, dialoguePhase);

            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.Attach(eventHub, dialoguePhase);

            eventHub.Publish(CharacterReady.Create(characterId, participantId: string.Empty));
            eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking(characterId));

            ConversationFlowInputs inputs = aggregator.Sample();

            Assert.IsTrue(inputs.IsCharacterReady, "Late-bound EventHub should still drive CharacterReady.");
            Assert.IsTrue(inputs.IsCharacterSpeaking, "Late-bound EventHub should still drive speech state.");
        }

        [Test]
        public void Sample_ClearsInterruptedLatch_AfterSingleFrame()
        {
            const string characterId = "char-1";

            var aggregator = new ConversationFlowSignalAggregator(characterId);
            var dialoguePhase = new StubDialoguePhaseProvider();
            var eventHub = new EventHub(new ImmediateScheduler());

            aggregator.Attach(eventHub, dialoguePhase);
            eventHub.Publish(CharacterTurnCompleted.Create(characterId, participantId: string.Empty, wasInterrupted: true));

            ConversationFlowInputs first = aggregator.Sample();
            ConversationFlowInputs second = aggregator.Sample();

            Assert.IsTrue(first.WasRecentlyInterrupted);
            Assert.IsFalse(second.WasRecentlyInterrupted,
                "Interrupted turns should be reported as a bounded one-shot instead of a sticky latch.");
        }

        [Test]
        public void CharacterEvents_AreIgnored_WhenCharacterIdIsBlank()
        {
            var aggregator = new ConversationFlowSignalAggregator(null);
            var eventHub = new EventHub(new ImmediateScheduler());

            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());
            eventHub.Publish(CharacterReady.Create("char-1", participantId: string.Empty));
            eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("char-1"));

            ConversationFlowInputs inputs = aggregator.Sample();

            Assert.IsFalse(inputs.IsCharacterReady);
            Assert.IsFalse(inputs.IsCharacterSpeaking);
        }

        [Test]
        public void SetCharacterId_LateBinding_ScopesSubsequentCharacterEvents()
        {
            var aggregator = new ConversationFlowSignalAggregator(null);
            var eventHub = new EventHub(new ImmediateScheduler());

            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());
            aggregator.SetCharacterId("char-1");

            eventHub.Publish(CharacterReady.Create("char-2", participantId: string.Empty));
            eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("char-2"));
            eventHub.Publish(CharacterReady.Create("char-1", participantId: string.Empty));
            eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("char-1"));

            ConversationFlowInputs inputs = aggregator.Sample();

            Assert.IsTrue(inputs.IsCharacterReady);
            Assert.IsTrue(inputs.IsCharacterSpeaking);
        }

        [Test]
        public void MicrophoneAndPushToTalk_AreTrackedSeparately()
        {
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            eventHub.Publish(LocalPlayerActivityChanged.Create(true, LocalPlayerActivitySource.PushToTalk));
            eventHub.Publish(LocalPlayerActivityChanged.Create(true, LocalPlayerActivitySource.Microphone));
            Assert.IsTrue(aggregator.Sample().IsPlayerLocallyActive);

            // The gate hears a pause while the player is still holding the control. One shared flag
            // would let the microphone switch the talk control off.
            eventHub.Publish(LocalPlayerActivityChanged.Create(false, LocalPlayerActivitySource.Microphone));
            Assert.IsTrue(
                aggregator.Sample().IsPlayerLocallyActive,
                "The player is still holding the talk control.");

            eventHub.Publish(LocalPlayerActivityChanged.Create(false, LocalPlayerActivitySource.PushToTalk));
            Assert.IsFalse(aggregator.Sample().IsPlayerLocallyActive);
        }

        [Test]
        public void NoticingLocally_CanBeTurnedOffAndBackOnWithoutLosingTheSignal()
        {
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            aggregator.SetNoticePlayerLocally(false);
            eventHub.Publish(LocalPlayerActivityChanged.Create(true, LocalPlayerActivitySource.Microphone));
            Assert.IsFalse(aggregator.Sample().IsPlayerLocallyActive);

            // Still folded underneath, so switching back on does not wait for the next event.
            aggregator.SetNoticePlayerLocally(true);
            Assert.IsTrue(aggregator.Sample().IsPlayerLocallyActive);
        }

        [Test]
        public void LocalActivity_IsScopedToTheAddressedCharacter()
        {
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.SetAddressedByPlayer(false);
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            eventHub.Publish(LocalPlayerActivityChanged.Create(true, LocalPlayerActivitySource.Microphone));

            Assert.IsFalse(
                aggregator.Sample().IsPlayerLocallyActive,
                "One open microphone must not make every character in the room lean in.");
        }

        [Test]
        public void PlayerTurn_IsNotTaken_WhenAnotherCharacterIsAddressed()
        {
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());

            aggregator.SetAddressedByPlayer(false);
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("player-turn-1"));
            eventHub.Publish(PlayerTranscriptReceived.Create(
                "player-1",
                "Player",
                "hello",
                isFinal: true,
                phase: TranscriptionPhase.ProcessedFinal));

            ConversationFlowInputs inputs = aggregator.Sample();

            Assert.IsFalse(inputs.IsPlayerSpeaking, "A character that is not addressed must not take the turn.");
            Assert.IsFalse(inputs.HasPendingPlayerTurn);
        }

        [Test]
        public void BecomingAddressedMidUtterance_PicksUpTheTurnAlreadyInFlight()
        {
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());

            aggregator.SetAddressedByPlayer(false);
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());
            eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("player-turn-1"));

            Assert.IsFalse(aggregator.Sample().IsPlayerSpeaking);

            // The conversation moves to this character while the player is still talking. No new
            // event arrives, so a scope that dropped the signal could never recover.
            aggregator.SetAddressedByPlayer(true);

            Assert.IsTrue(aggregator.Sample().IsPlayerSpeaking);
        }

        [Test]
        public void PlayerSpeaking_TrueToFalseTwice_ReflectsEachTransition()
        {
            // Arrange
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            // Act — start speaking
            eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("turn-1"));
            bool firstTrue = aggregator.Sample().IsPlayerSpeaking;

            // stop speaking
            eventHub.Publish(PlayerSpeakingStateChanged.StoppedSpeaking("turn-1"));
            bool firstFalse = aggregator.Sample().IsPlayerSpeaking;

            // start again
            eventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("turn-2"));
            bool secondTrue = aggregator.Sample().IsPlayerSpeaking;

            // stop again
            eventHub.Publish(PlayerSpeakingStateChanged.StoppedSpeaking("turn-2"));
            bool secondFalse = aggregator.Sample().IsPlayerSpeaking;

            // Assert
            Assert.IsTrue(firstTrue, "Player should be speaking after StartedSpeaking.");
            Assert.IsFalse(firstFalse, "Player should not be speaking after StoppedSpeaking.");
            Assert.IsTrue(secondTrue, "Player should be speaking again after second StartedSpeaking.");
            Assert.IsFalse(secondFalse, "Player should not be speaking after second StoppedSpeaking.");
        }

        [Test]
        public void Detach_StopsEventRouting_SubsequentEventsIgnored()
        {
            // Arrange
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            // Act — detach by attaching with null, then publish
            aggregator.Attach(null, new StubDialoguePhaseProvider());
            eventHub.Publish(CharacterReady.Create("char-1", participantId: string.Empty));
            eventHub.Publish(CharacterSpeechStateChanged.StartedSpeaking("char-1"));

            ConversationFlowInputs inputs = aggregator.Sample();

            // Assert — events published after detach must not be routed
            Assert.IsFalse(inputs.IsCharacterReady,
                "Events published after detach must not update state.");
            Assert.IsFalse(inputs.IsCharacterSpeaking);
        }

        [Test]
        public void CharacterReady_BeforeOtherEvents_InitializesReadyFlag()
        {
            // Arrange
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            // Act — CharacterReady arrives before any speech state change
            eventHub.Publish(CharacterReady.Create("char-1", participantId: string.Empty));

            ConversationFlowInputs inputs = aggregator.Sample();

            // Assert
            Assert.IsTrue(inputs.IsCharacterReady,
                "IsCharacterReady must be true after CharacterReady event.");
            Assert.IsFalse(inputs.IsCharacterSpeaking,
                "Character should not be speaking until a speech event arrives.");
        }

        [Test]
        public void TurnCompleted_NotInterrupted_DoesNotSetInterruptedLatch()
        {
            // Arrange
            var aggregator = new ConversationFlowSignalAggregator("char-1");
            var eventHub = new EventHub(new ImmediateScheduler());
            aggregator.Attach(eventHub, new StubDialoguePhaseProvider());

            // Act
            eventHub.Publish(CharacterTurnCompleted.Create(
                "char-1", participantId: string.Empty, wasInterrupted: false));

            ConversationFlowInputs inputs = aggregator.Sample();

            // Assert
            Assert.IsFalse(inputs.WasRecentlyInterrupted,
                "A non-interrupted turn completion must not set WasRecentlyInterrupted.");
            Assert.IsTrue(inputs.TurnJustCompleted,
                "TurnJustCompleted must be set on a turn-completed event.");
        }

        private sealed class StubDialoguePhaseProvider : Convai.Domain.Embodiment.Interfaces.IDialoguePhaseProvider
        {
            public bool IsSpeechActive { get; set; }
            public float SpeechBlendFactor => IsSpeechActive ? 1f : 0f;
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
