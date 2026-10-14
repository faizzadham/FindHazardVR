using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class PendingTurnTranscriptTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void FinalTranscript_SurvivesDeliveryAroundCognitionAndDebounce(int deliveryPhase)
        {
            var debounce = new DialogueStateDebounce();
            var transcript = new PendingTurnTranscript();
            debounce.Tick(DialogueState.Listening, 0.02f);
            if (deliveryPhase == 0) transcript.Latch(42); // Before cognition sees the new source state.
            for (int frame = 0; frame < 12; frame++)
            {
                var state = debounce.Tick(DialogueState.Speaking, 0.02f);
                transcript.ObserveState(state, debounce.HasPendingState);
                if (deliveryPhase == 1 && frame == 0) transcript.Latch(42); // Between cognition and Expression.
                if (deliveryPhase == 2 && frame == 2) transcript.Latch(42); // Mid-confirmation window.
                if (state != DialogueState.Speaking)
                {
                    Assert.IsFalse(transcript.TryConsume(state, out _));
                    continue;
                }
                Assert.IsTrue(transcript.TryConsume(state, out int words));
                Assert.That(words, Is.EqualTo(42));
                Assert.IsFalse(transcript.TryConsume(state, out _), "A final transcript must be consumed once.");
                return;
            }
            Assert.Fail("The speaking state never confirmed.");
        }

        [Test]
        public void FinalAfterCognition_WaitsUntilTheNextCognitionBeforeItCanBeRejected()
        {
            var debounce = new DialogueStateDebounce();
            var transcript = new PendingTurnTranscript();
            var state = debounce.Tick(DialogueState.Listening, 0.02f);
            transcript.ObserveState(state, debounce.HasPendingState);
            transcript.Latch(12);
            Assert.IsFalse(transcript.TryConsume(state, out _));
            for (int frame = 0; frame < 12; frame++)
            {
                state = debounce.Tick(DialogueState.Speaking, 0.02f);
                transcript.ObserveState(state, debounce.HasPendingState);
            }
            Assert.IsTrue(transcript.TryConsume(state, out int words));
            Assert.That(words, Is.EqualTo(12));
        }

        [TestCase(DialogueState.Listening)]
        [TestCase(DialogueState.Settling)]
        [TestCase(DialogueState.Idle)]
        [TestCase(DialogueState.Interrupted)]
        [TestCase(DialogueState.Reacting)]
        public void RejectedTurn_DoesNotReplayIntoTheNextSpeakingTurn(DialogueState rejected)
        {
            var debounce = new DialogueStateDebounce();
            var transcript = new PendingTurnTranscript();
            debounce.Tick(DialogueState.Thinking, 0.02f);
            transcript.Latch(42);
            for (int frame = 0; frame < 12; frame++)
            {
                var state = debounce.Tick(rejected, 0.02f);
                transcript.ObserveState(state, debounce.HasPendingState);
            }
            Assert.IsFalse(transcript.TryConsume(DialogueState.Speaking, out _));
        }

        [Test]
        public void UnconfirmedSpeakingEdgeThatReturnsToListening_DropsItsTranscript()
        {
            var debounce = new DialogueStateDebounce();
            var transcript = new PendingTurnTranscript();
            debounce.Tick(DialogueState.Listening, 0.02f);
            transcript.Latch(42);
            var state = debounce.Tick(DialogueState.Speaking, 0.02f);
            transcript.ObserveState(state, debounce.HasPendingState);
            state = debounce.Tick(DialogueState.Listening, 0.02f);
            transcript.ObserveState(state, debounce.HasPendingState);
            Assert.IsFalse(transcript.TryConsume(DialogueState.Speaking, out _));
        }

        [Test]
        public void ClearOnSourceChange_DoesNotReplayAPreviousCharactersTranscript()
        {
            var transcript = new PendingTurnTranscript();
            transcript.Latch(42);
            transcript.Clear();
            Assert.IsFalse(transcript.TryConsume(DialogueState.Speaking, out _));
        }
    }
}
