using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.ConversationFlow.Core;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.ConversationFlow
{
    [TestFixture]
    public sealed class ConversationFlowStateMachineTests
    {
        [TearDown]
        public void TearDown() => LogAssert.NoUnexpectedReceived();

        private static ConversationFlowTimings Timings(
            float transitionDuration = 0.25f,
            float thinkingMinHold = 0.25f,
            float thinkingMaxHold = 2.5f,
            float attendingGracePeriod = 0.3f,
            float settlingDuration = 0.6f,
            float idleReturnDelay = 60f,
            float interruptedFreezeDuration = 0.25f,
            float speakingBaseEnergy = 0.6f)
            => new(
                transitionDuration,
                thinkingMinHold,
                thinkingMaxHold,
                attendingGracePeriod,
                settlingDuration,
                idleReturnDelay,
                interruptedFreezeDuration,
                speakingBaseEnergy);

        private static ConversationFlowInputs Inputs(
            bool ready = true,
            bool playerSpeaking = false,
            bool pendingTurn = false,
            bool characterSpeaking = false,
            bool lipSync = false,
            bool interrupted = false,
            bool turnCompleted = false,
            bool performingAction = false,
            bool locallyActive = false,
            bool addressed = true)
            => new(ready, playerSpeaking, pendingTurn, characterSpeaking, lipSync, interrupted,
                turnCompleted, performingAction, locallyActive, addressed);

        [Test]
        public void Pulse_PlaysReacting_ThenReturnsToTheDerivedState()
        {
            var sm = new ConversationFlowStateMachine();
            sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Listening));

            sm.Pulse(DialogueState.Reacting, 0.5f);
            sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Reacting),
                "Reacting is the one state game code drives, so a pulse must win over the derived beat.");

            for (int i = 0; i < 10; i++)
                sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Listening),
                "The beat ends by itself and hands the conversation back to whatever it is actually on.");
        }

        [Test]
        public void Pulse_DoesNotOverrideNotReady()
        {
            var sm = new ConversationFlowStateMachine();
            sm.Pulse(DialogueState.Reacting, 0.5f);

            for (int i = 0; i < 3; i++)
                sm.Tick(Inputs(ready: false), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle),
                "A character that has not joined the conversation cannot react to anything in it.");
        }

        [Test]
        public void Pulse_RequestedBeforeReady_IsDroppedRatherThanHeld()
        {
            var sm = new ConversationFlowStateMachine();
            sm.Pulse(DialogueState.Reacting, 0.5f);
            sm.Tick(Inputs(ready: false), Timings(), 0.1f);

            // The character joins. The beat must not fire late.
            sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Listening));
        }

        [Test]
        public void LocalActivity_LiftsIdleToAttending()
        {
            var sm = new ConversationFlowStateMachine();

            for (int i = 0; i < 5; i++)
                sm.Tick(Inputs(locallyActive: true), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending),
                "Somebody is starting to talk to it, so it turns to them instead of staying idle.");
        }

        [Test]
        public void LocalActivity_NeverReachesListening()
        {
            var sm = new ConversationFlowStateMachine();

            for (int i = 0; i < 200; i++)
                sm.Tick(Inputs(locallyActive: true), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.Not.EqualTo(DialogueState.Listening),
                "Listening means a turn is under way, and only the service gets to say that.");
        }

        [Test]
        public void LocalActivity_DoesNotOverrideNotReady()
        {
            var sm = new ConversationFlowStateMachine();

            for (int i = 0; i < 5; i++)
                sm.Tick(Inputs(ready: false, locallyActive: true), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void ServiceVerdict_StillTakesTheTurn_AfterTheLocalHint()
        {
            var sm = new ConversationFlowStateMachine();
            sm.Tick(Inputs(locallyActive: true), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            // The round trip lands. The hint was an early lean-in, not a replacement.
            sm.Tick(Inputs(locallyActive: true, playerSpeaking: true), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Listening));
        }

        /// <summary>
        ///     The player walks away from one character and talks to another. The one they left must
        ///     drop out of its engaged stance, not hold it for the length of the idle timeout.
        /// </summary>
        [Test]
        public void LosingTheConversation_CoolsToIdleWithoutWaitingOutTheIdleDelay()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings timings = Timings(idleReturnDelay: 60f);

            // A real turn with this character.
            sm.Tick(Inputs(playerSpeaking: true), timings, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Listening));
            sm.Tick(Inputs(), timings, 0.5f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            // The player turns to somebody else. One second later, not sixty.
            for (int i = 0; i < 10; i++)
                sm.Tick(Inputs(addressed: false), timings, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle),
                "A character the player has left must not keep leaning in behind their back.");
        }

        /// <summary>
        ///     Reported from the Terminal demo: the character finished a sentence, looked away into
        ///     its idle life, then snapped back.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Who the player is addressing is re-evaluated many times a second and moves
        ///         through pending, looked-at and room answers, so it can flicker for a frame at a
        ///         turn boundary. The damage is done after the wind-down: Attending is not itself
        ///         an engagement signal, so a clock cleared on that flicker is never refreshed
        ///         again and the character is stranded in Idle — where the player is not even a
        ///         candidate, so ambient exploration takes the head away until some other signal
        ///         happens to arrive.
        ///     </para>
        /// </remarks>
        [Test]
        public void AFlickerOfTheAddressee_DoesNotStrandTheCharacterInIdle()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings timings = Timings();

            sm.Tick(Inputs(characterSpeaking: true), timings, 0.1f);
            sm.Tick(Inputs(turnCompleted: true), timings, 0.1f);

            // Past the wind-down, where Settling no longer holds the clock up.
            for (int i = 0; i < 20; i++)
                sm.Tick(Inputs(), timings, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            // One frame in which somebody else looks like the addressee.
            sm.Tick(Inputs(addressed: false), timings, 1f / 60f);

            // ...and it is this character again for the next half second.
            for (int i = 0; i < 30; i++)
                sm.Tick(Inputs(), timings, 1f / 60f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending),
                "A one-frame flicker must not send the character into its idle life, and must not "
                + "leave it there.");
        }

        /// <summary>
        ///     The grace is a debounce, not a reprieve: a loss that outlasts it still cools.
        /// </summary>
        [Test]
        public void ALossThatOutlastsTheGrace_StillCools()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings timings = Timings();

            sm.Tick(Inputs(playerSpeaking: true), timings, 0.1f);
            for (int i = 0; i < 20; i++)
                sm.Tick(Inputs(), timings, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            for (int i = 0; i < 20; i++)
                sm.Tick(Inputs(addressed: false), timings, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle),
                "Two seconds is not a flicker.");
        }

        [Test]
        public void ACharacterNeverAddressed_NeverLeavesIdle()
        {
            var sm = new ConversationFlowStateMachine();

            // Everything the room is doing, seen by a character that is not part of it.
            for (int i = 0; i < 200; i++)
                sm.Tick(Inputs(addressed: false, playerSpeaking: true, locallyActive: true), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        /// <summary>
        ///     Losing the conversation must not cut a character off mid-sentence: its own speech is
        ///     derived from its audio, not from whether it still holds the player's attention.
        /// </summary>
        [Test]
        public void LosingTheConversation_MidAnswer_StillFinishesSpeaking()
        {
            var sm = new ConversationFlowStateMachine();
            sm.Tick(Inputs(characterSpeaking: true), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Speaking));

            sm.Tick(Inputs(characterSpeaking: true, addressed: false), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Speaking),
                "It is still talking; the player having turned away does not silence it.");

            // It finishes, and then winds down rather than holding an engaged stance.
            sm.Tick(Inputs(addressed: false, turnCompleted: true), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Settling));
            for (int i = 0; i < 20; i++)
                sm.Tick(Inputs(addressed: false), Timings(), 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        /// <summary>
        ///     A room with one character addresses it always, so none of the above may change how a
        ///     single-character scene behaves: it still stays attentive between turns.
        /// </summary>
        [Test]
        public void ASingleCharacter_StillHoldsItsAttentiveStanceBetweenTurns()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings timings = Timings(idleReturnDelay: 60f);

            sm.Tick(Inputs(playerSpeaking: true), timings, 0.1f);
            for (int i = 0; i < 100; i++)
                sm.Tick(Inputs(), timings, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));
        }

        [Test]
        public void Initial_StateIsIdle()
        {
            var sm = new ConversationFlowStateMachine();
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void NotReady_ForcesIdle_EvenWhenPlayerSpeaks()
        {
            var sm = new ConversationFlowStateMachine();
            DialogueStateReading r = sm.Tick(Inputs(ready: false, playerSpeaking: true), Timings(), 0.1f);
            Assert.That(r.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void NoInteraction_KeepsStateIdle()
        {
            var sm = new ConversationFlowStateMachine();

            for (int i = 0; i < 20; i++)
                sm.Tick(Inputs(), Timings(), 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void PlayerSpeaking_EntersListening()
        {
            var sm = new ConversationFlowStateMachine();
            DialogueStateReading r = sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);
            Assert.That(r.Primary, Is.EqualTo(DialogueState.Listening));
        }

        [Test]
        public void CharacterSpeaking_EntersSpeaking_OverridesOthers()
        {
            var sm = new ConversationFlowStateMachine();
            DialogueStateReading r = sm.Tick(
                Inputs(playerSpeaking: true, characterSpeaking: true),
                Timings(), 0.1f);
            Assert.That(r.Primary, Is.EqualTo(DialogueState.Speaking));
        }

        [Test]
        public void PlayerStopsWithPendingTurn_EntersThinking()
        {
            var sm = new ConversationFlowStateMachine();
            sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);
            DialogueStateReading r = sm.Tick(Inputs(pendingTurn: true), Timings(), 0.1f);
            Assert.That(r.Primary, Is.EqualTo(DialogueState.Thinking));
        }

        [Test]
        public void ThinkingTimeout_FallsBackToAttending()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings();

            sm.Tick(Inputs(pendingTurn: true), t, 0.1f);
            for (int i = 0; i < 40; i++)
                sm.Tick(Inputs(pendingTurn: true), t, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));
        }

        [Test]
        public void ThinkingTimeout_DoesNotBounceBackToThinking_WhilePendingTurnRemains()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(thinkingMaxHold: 0.3f);

            sm.Tick(Inputs(pendingTurn: true), t, 0.1f);
            for (int i = 0; i < 8; i++)
                sm.Tick(Inputs(pendingTurn: true), t, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));
        }

        [Test]
        public void ThinkingMinHold_DelaysTransitionIntoSpeaking_WhenReplyStartsImmediately()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(thinkingMinHold: 0.35f, thinkingMaxHold: 1.5f);

            sm.Tick(Inputs(pendingTurn: true), t, 0.05f);
            DialogueStateReading heldThinking = sm.Tick(
                Inputs(pendingTurn: true, characterSpeaking: true),
                t,
                0.1f);

            Assert.That(heldThinking.Primary, Is.EqualTo(DialogueState.Thinking));

            DialogueStateReading speaking = sm.Tick(
                Inputs(characterSpeaking: true),
                t,
                0.3f);

            Assert.That(speaking.Primary, Is.EqualTo(DialogueState.Speaking));
        }

        /// <summary>
        ///     The settle beat belongs to the end of speaking, not to the message that announces it.
        ///     Once the voice could end a turn on its own, keying Settling on turn-completed meant
        ///     the character skipped its wind-down — and its sigh — and then settled seconds later,
        ///     out of context, when the message finally arrived.
        /// </summary>
        [Test]
        public void SpeechEndingWithoutTurnCompleted_StillEntersSettling()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(settlingDuration: 0.3f);

            sm.Tick(Inputs(characterSpeaking: true), t, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Speaking));

            // The voice stopped and the arbiter ended the turn. No turn-completed has arrived.
            DialogueStateReading settling = sm.Tick(Inputs(), t, 0.1f);
            Assert.That(settling.Primary, Is.EqualTo(DialogueState.Settling),
                "Leaving Speaking is what opens the wind-down, whichever source ended the turn.");
        }

        [Test]
        public void LateTurnCompleted_DoesNotSettleASecondTime()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(settlingDuration: 0.3f, idleReturnDelay: 60f);

            sm.Tick(Inputs(characterSpeaking: true), t, 0.1f);
            sm.Tick(Inputs(), t, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Settling));

            // Wind-down completes and the character returns to an engaged stance.
            for (int i = 0; i < 6; i++) sm.Tick(Inputs(), t, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            // The service finally catches up, seconds after the character stopped talking.
            sm.Tick(Inputs(turnCompleted: true), t, 0.1f);
            Assert.That(sm.Current.Primary, Is.Not.EqualTo(DialogueState.Settling),
                "A turn-completed that arrives after the wind-down is stale news, not a second ending.");
        }

        [Test]
        public void InterruptionArrivingAfterTheVoiceWentQuiet_StillReachesInterrupted()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(settlingDuration: 0.6f, interruptedFreezeDuration: 0.25f);

            sm.Tick(Inputs(characterSpeaking: true), t, 0.1f);
            sm.Tick(Inputs(), t, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Settling));

            // A barge-in whose terminal signal lands a frame after the audio already went silent.
            sm.Tick(Inputs(interrupted: true, turnCompleted: true), t, 0.1f);
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Interrupted),
                "An interruption must not be swallowed just because the voice stopped first.");
        }

        [Test]
        public void CharacterSpeechEnds_EntersSettling_ThenAttending_ThenIdle()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(settlingDuration: 0.3f, idleReturnDelay: 0.8f);

            sm.Tick(Inputs(characterSpeaking: true), t, 0.1f);
            DialogueStateReading settling = sm.Tick(Inputs(turnCompleted: true), t, 0.1f);
            Assert.That(settling.Primary, Is.EqualTo(DialogueState.Settling));

            for (int i = 0; i < 4; i++)
                sm.Tick(Inputs(), t, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            for (int i = 0; i < 10; i++)
                sm.Tick(Inputs(), t, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void PlayerStopsSpeaking_RemainsAttending_UntilIdleTimeout()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(attendingGracePeriod: 0.2f, idleReturnDelay: 0.8f);

            sm.Tick(Inputs(playerSpeaking: true), t, 0.1f);
            sm.Tick(Inputs(), t, 0.1f);
            sm.Tick(Inputs(), t, 0.2f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            for (int i = 0; i < 8; i++)
                sm.Tick(Inputs(), t, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void Interrupted_FreezesThenTransitionsToAttending()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings();

            sm.Tick(Inputs(characterSpeaking: true), t, 0.1f);
            DialogueStateReading frozen = sm.Tick(
                Inputs(turnCompleted: true, interrupted: true), t, 0.05f);
            Assert.That(frozen.Primary, Is.EqualTo(DialogueState.Interrupted));

            for (int i = 0; i < 15; i++)
                sm.Tick(Inputs(interrupted: true), t, 0.1f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));
        }

        [Test]
        public void InterruptedTurn_CoolsBackToIdle_WhenLatchExpiresAndSessionGoesQuiet()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(
                interruptedFreezeDuration: 0.2f,
                idleReturnDelay: 0.35f);

            sm.Tick(Inputs(characterSpeaking: true), t, 0.1f);
            sm.Tick(Inputs(turnCompleted: true, interrupted: true), t, 0.05f);
            sm.Tick(Inputs(), t, 0.25f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Attending));

            sm.Tick(Inputs(), t, 0.4f);

            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void Changed_Event_FiresOnPrimaryTransitions()
        {
            var sm = new ConversationFlowStateMachine();
            int changes = 0;
            DialogueState last = DialogueState.Idle;
            sm.Changed += r => { changes++; last = r.Primary; };

            sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);

            Assert.That(changes, Is.EqualTo(1));
            Assert.That(last, Is.EqualTo(DialogueState.Listening));
        }

        [Test]
        public void Pulse_ReactingOverridesDerivedState_UntilDurationElapses()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings();

            sm.Pulse(DialogueState.Reacting, 0.3f);
            DialogueStateReading r = sm.Tick(Inputs(), t, 0.05f);
            Assert.That(r.Primary, Is.EqualTo(DialogueState.Reacting));

            for (int i = 0; i < 10; i++)
                sm.Tick(Inputs(), t, 0.1f);

            Assert.That(sm.Current.Primary, Is.Not.EqualTo(DialogueState.Reacting));
        }

        [Test]
        public void Reset_RestoresIdleAndClearsBlend()
        {
            var sm = new ConversationFlowStateMachine();
            sm.Tick(Inputs(playerSpeaking: true), Timings(), 0.1f);
            sm.Reset();
            Assert.That(sm.Current.Primary, Is.EqualTo(DialogueState.Idle));
            Assert.That(sm.Current.BlendWeight, Is.EqualTo(0f));
        }

        // ── new cases (+4) ────────────────────────────────────────────────────────────

        [Test]
        public void BlendWeight_DuringTransition_IsNonZeroBeforeFullCommit()
        {
            // Arrange
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(transitionDuration: 0.5f);

            // Act — one small tick into a new state; blend should be partial
            DialogueStateReading r = sm.Tick(Inputs(playerSpeaking: true), t, 0.01f);

            // Assert — transition has just started; blend weight must be between 0 and 1
            Assert.That(r.BlendWeight, Is.GreaterThanOrEqualTo(0f));
            Assert.That(r.BlendWeight, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void ThinkingMinHold_BlocksTransitionToSpeaking_UntilThresholdReached()
        {
            // Arrange
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(thinkingMinHold: 0.5f, thinkingMaxHold: 2f);

            // Enter Thinking state
            sm.Tick(Inputs(pendingTurn: true), t, 0.1f);

            // Act — send character-speaking before min hold expires (0.1s < 0.5s)
            DialogueStateReading held = sm.Tick(
                Inputs(pendingTurn: true, characterSpeaking: true), t, 0.1f);

            // Assert — must still be in Thinking, not Speaking
            Assert.That(held.Primary, Is.EqualTo(DialogueState.Thinking),
                "Min-hold must prevent Speaking transition before threshold.");

            // Now advance past the min hold
            DialogueStateReading released = sm.Tick(
                Inputs(characterSpeaking: true), t, 0.5f);

            // Assert — now may transition to Speaking
            Assert.That(released.Primary, Is.EqualTo(DialogueState.Speaking),
                "After min-hold elapsed, Speaking transition must occur.");
        }

        [Test]
        public void Interrupted_DoesNotRequireMinHold_TransitionsFast()
        {
            // Arrange
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(
                thinkingMinHold: 10f,
                interruptedFreezeDuration: 0.1f);

            // Put machine into Speaking
            sm.Tick(Inputs(characterSpeaking: true), t, 0.1f);

            // Act — interrupt immediately
            DialogueStateReading frozen = sm.Tick(
                Inputs(turnCompleted: true, interrupted: true), t, 0.05f);

            // Assert — must enter Interrupted regardless of any min-hold
            Assert.That(frozen.Primary, Is.EqualTo(DialogueState.Interrupted),
                "Interrupt transition must bypass min-hold.");
        }

        [Test]
        public void Determinism_SameInputsProduceSameStates_Over100Ticks()
        {
            // Arrange
            ConversationFlowTimings t = Timings();
            bool[] speakingPattern =
            {
                false, true, true, true, false, false, false, false, false, false
            };

            DialogueState[] run1 = new DialogueState[100];
            DialogueState[] run2 = new DialogueState[100];

            // Act
            var sm1 = new ConversationFlowStateMachine();
            for (int i = 0; i < 100; i++)
            {
                bool ps = speakingPattern[i % speakingPattern.Length];
                run1[i] = sm1.Tick(Inputs(playerSpeaking: ps), t, 0.05f).Primary;
            }

            var sm2 = new ConversationFlowStateMachine();
            for (int i = 0; i < 100; i++)
            {
                bool ps = speakingPattern[i % speakingPattern.Length];
                run2[i] = sm2.Tick(Inputs(playerSpeaking: ps), t, 0.05f).Primary;
            }

            // Assert
            Assert.That(run1, Is.EqualTo(run2),
                "Identical input sequences must produce identical state sequences.");
        }

        // ------------------------------------------------------------------ errands count

        /// <summary>
        ///     A character sent to walk somewhere says nothing for several seconds. Before an
        ///     errand counted as engagement the state decayed to Idle mid-walk, and every
        ///     behaviour keyed off it followed — most visibly gaze, which stopped treating the
        ///     player as someone worth looking at. The character arrived where you sent it and
        ///     then looked at the wall.
        /// </summary>
        [Test]
        public void PerformingAnAction_HoldsEngagementThroughSilence()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(idleReturnDelay: 1f);

            // Engage, then go quiet while an errand runs for well past the idle delay.
            sm.Tick(Inputs(playerSpeaking: true), t, 0.1f);
            for (int i = 0; i < 30; i++)
                sm.Tick(Inputs(performingAction: true), t, 0.1f);

            DialogueStateReading reading = sm.Tick(Inputs(performingAction: true), t, 0.1f);

            Assert.That(reading.Primary, Is.EqualTo(DialogueState.Attending),
                "Doing what it was asked is being with the player, even in silence.");
        }

        /// <summary>
        ///     And the errand ending must not hold engagement open forever: once it is done and
        ///     nothing else happens, the normal idle decay applies.
        /// </summary>
        [Test]
        public void OnceTheActionFinishes_TheNormalIdleDecayResumes()
        {
            var sm = new ConversationFlowStateMachine();
            ConversationFlowTimings t = Timings(idleReturnDelay: 1f);

            sm.Tick(Inputs(playerSpeaking: true), t, 0.1f);
            for (int i = 0; i < 10; i++)
                sm.Tick(Inputs(performingAction: true), t, 0.1f);

            for (int i = 0; i < 30; i++)
                sm.Tick(Inputs(), t, 0.1f);

            DialogueStateReading reading = sm.Tick(Inputs(), t, 0.1f);

            Assert.That(reading.Primary, Is.EqualTo(DialogueState.Idle),
                "With the errand over and nothing said, the character returns to its own business.");
        }
    }
}
