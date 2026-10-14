using System;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.ConversationFlow.Components;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.ConversationFlow
{
    /// <summary>
    ///     Component-level tests for <see cref="ConvaiConversationFlowController" /> that go
    ///     beyond <see cref="ConvaiConversationFlowControllerInvariantsTests" />.  Focus:
    ///     conversation-flow slot registration, driver registry lifecycle, current-state
    ///     semantics, and the <see cref="IConversationFlowSource.Changed" /> event contract.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiConversationFlowControllerComponentTests
    {
        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            // Must reset the process-wide static registry before creating any controller
            // so that ActiveCount starts at 0 and no multi-driver warning is logged.
            ConvaiConversationFlowDriverRegistry.Reset();
            _rig = EmbodimentTestRig.Create(nameof(ConvaiConversationFlowControllerComponentTests));
            _harness = new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(_rig);
        }

        [TearDown]
        public void TearDown()
        {
            // Reset before dispose so the Unregister call in OnDisable is a silent no-op.
            ConvaiConversationFlowDriverRegistry.Reset();
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        // ── Context slot registration ──────────────────────────────────────────

        [Test]
        public void OnEnable_ConversationFlowSlot_IsSet()
        {
            IConversationFlowSource slot = _rig.Context.ConversationFlowSource;

            Assert.That(slot, Is.Not.Null);
            Assert.That(slot, Is.SameAs(_harness.Controller));
        }

        [Test]
        public void OnDisable_ConversationFlowSlot_IsCleared()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            _harness.Disable();

            Assert.That(_rig.Context.ConversationFlowSource, Is.Null);
        }

        [Test]
        public void ReenableAfterDisable_ConversationFlowSlot_IsReregistered()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            _harness.Disable();
            ConvaiConversationFlowDriverRegistry.Reset();
            _harness.Enable();

            Assert.That(_rig.Context.ConversationFlowSource, Is.SameAs(_harness.Controller));
        }

        // ── Driver registry lifecycle ──────────────────────────────────────────

        [Test]
        public void OnEnable_DriverRegistry_ActiveCountIsOne()
        {
            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.EqualTo(1));
        }

        [Test]
        public void OnDisable_DriverRegistry_ActiveCountIsZero()
        {
            // Unsubscribe from ScopeChanged before disable so the controller's own
            // handler doesn't attempt rebind after the registry clears.
            ConvaiConversationFlowDriverRegistry.Reset();
            _harness.Disable();

            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.EqualTo(0));
        }

        [Test]
        public void ReenableAfterDisable_DriverRegistry_ActiveCountIsOne()
        {
            ConvaiConversationFlowDriverRegistry.Reset();
            _harness.Disable();
            ConvaiConversationFlowDriverRegistry.Reset();
            _harness.Enable();

            Assert.That(ConvaiConversationFlowDriverRegistry.ActiveCount, Is.EqualTo(1));
        }

        [Test]
        public void MultipleDrivers_ExplicitTargetConsumesPlayerSignalsOnlyForTarget()
        {
            ConvaiCharacter targetCharacter = AddCharacter(_rig, "char-target");
            _rig.Context.Populate(_rig.EventHub, null);

            using EmbodimentTestRig otherRig = EmbodimentTestRig.Create("OtherCharacter");
            AddCharacter(otherRig, "char-other");
            var otherHarness =
                new EmbodimentReceiverHarness<ConvaiConversationFlowController, ConvaiConversationFlowProfile>(
                    otherRig);

            _harness.Controller.SetConversationTargetResolverForTests(() => targetCharacter);
            otherHarness.Controller.SetConversationTargetResolverForTests(() => targetCharacter);

            _rig.EventHub.Publish(CharacterReady.Create("char-target", participantId: string.Empty));
            otherRig.EventHub.Publish(CharacterReady.Create("char-other", participantId: string.Empty));
            _rig.EventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("turn-1"));
            otherRig.EventHub.Publish(PlayerSpeakingStateChanged.StartedSpeaking("turn-1"));
            _harness.Tick(1f / 60f);
            otherHarness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.Primary, Is.EqualTo(DialogueState.Listening),
                "The selected character must consume the room-level player speech signal.");

            // A character the player is not addressing takes none of the addressee's beats. Its
            // own gaze and body language decide what a bystander does from Idle — the Attending row
            // commits to the player and may turn the body, so borrowing it would make every
            // character in the room lock onto the player at once.
            Assert.That(otherHarness.Controller.Current.Primary, Is.EqualTo(DialogueState.Idle),
                "A non-target character must not mirror the selected character's player-turn state.");
        }

        /// <summary>
        ///     Reacting had a row in every gaze personality and in the profile table, and nothing
        ///     in the SDK ever entered it. This is the verb that makes it reachable, so it is
        ///     covered on the component rather than only on the state machine underneath.
        /// </summary>
        [Test]
        public void PulseReaction_PlaysTheBeatThenGivesTheConversationBack()
        {
            MakeReady();

            _harness.Controller.PulseReaction(0.5f);
            _harness.Tick(1f / 60f);
            Assert.That(_harness.Controller.Current.Primary, Is.EqualTo(DialogueState.Reacting));

            for (int i = 0; i < 60; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.Primary, Is.Not.EqualTo(DialogueState.Reacting),
                "The beat ends by itself.");
        }

        [Test]
        public void PulseReaction_WithNoDuration_DoesNothing()
        {
            MakeReady();

            _harness.Controller.PulseReaction(0f);
            _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.Primary, Is.Not.EqualTo(DialogueState.Reacting));
        }

        // ── Current-state semantics ────────────────────────────────────────────

        [Test]
        public void Current_AfterEnable_IsIdle()
        {
            Assert.That(_harness.Controller.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void Current_After100Ticks_WithNoInputs_RemainsIdle()
        {
            float dt = 1f / 60f;
            for (int i = 0; i < 100; i++)
                _harness.Tick(dt);

            Assert.That(_harness.Controller.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        /// <summary>
        ///     Brings the character into the conversation. Reacting is gated on readiness like
        ///     every other state, so a pulse before this point is deliberately dropped.
        /// </summary>
        private void MakeReady()
        {
            AddCharacter(_rig, "char-pulse");
            // Repopulating is what re-resolves the character id on the aggregator; without it the
            // readiness event below is for a character this controller has not heard of.
            _rig.Context.Populate(_rig.EventHub, null);
            _harness.Controller.SetConversationTargetResolverForTests(() => null);
            _rig.EventHub.Publish(CharacterReady.Create("char-pulse", participantId: string.Empty));
            _harness.Tick(1f / 60f);
        }

        private static ConvaiCharacter AddCharacter(EmbodimentTestRig rig, string characterId)
        {
            ConvaiCharacter character = rig.AddComponent<ConvaiCharacter>();
            var serialized = new SerializedObject(character);
            serialized.FindProperty("_characterId").stringValue = characterId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return character;
        }

    }
}
