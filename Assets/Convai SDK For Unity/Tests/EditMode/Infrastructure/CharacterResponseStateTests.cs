using Convai.Infrastructure.Networking;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public class CharacterResponseStateTests
    {
        [Test]
        public void EnsureResponseId_IsStableUntilParticipantStateClears()
        {
            var state = new CharacterResponseState();

            string first = state.EnsureResponseId("participant-1");
            string repeated = state.EnsureResponseId("participant-1");
            state.Clear("participant-1");
            string next = state.EnsureResponseId("participant-1");

            Assert.AreEqual(first, repeated);
            Assert.AreNotEqual(first, next);
            StringAssert.StartsWith("participant-1:character-response-", first);
        }

        [Test]
        public void ResolveTranscriptIdentity_UsesInteractionFallbackThenAcceptsExplicitServerIdentity()
        {
            var state = new CharacterResponseState();
            state.RegisterInteraction("participant-1", "interaction-1");

            CharacterResponseState.TranscriptIdentity first = state.ResolveTranscriptIdentity(
                "participant-1", null, null, null, null);
            CharacterResponseState.TranscriptIdentity next = state.ResolveTranscriptIdentity(
                "participant-1", "response-2", "message-2", "turn-2", "envelope-2");

            Assert.AreEqual("interaction-1", first.TurnId);
            Assert.AreEqual("interaction-1", first.MessageId);
            Assert.AreEqual("interaction-1", first.ResponseId);
            Assert.AreEqual("turn-2", next.TurnId);
            Assert.AreEqual("message-2", next.MessageId);
            Assert.AreEqual("response-2", next.ResponseId);
        }

        [TestCase(null)]
        [TestCase("session-1:r2")]
        public void ResolveTranscriptIdentity_KeepsTurnIdAcrossSpeechStartUntilNextResponse(string responseId)
        {
            var state = new CharacterResponseState();
            string localResponse = state.BeginResponse("participant-1");

            CharacterResponseState.TranscriptIdentity beforeSpeech = state.ResolveTranscriptIdentity(
                "participant-1", null, null, null, "packet-1");
            state.ResolveSpeechOwner(
                "participant-1", new LipSyncResponseOwner("session-1:r2", null, null, null), true);
            CharacterResponseState.TranscriptIdentity afterSpeech = state.ResolveTranscriptIdentity(
                "participant-1", responseId, null, null, "packet-2");
            string nextResponse = state.BeginResponse("participant-1");
            CharacterResponseState.TranscriptIdentity next = state.ResolveTranscriptIdentity(
                "participant-1", null, null, null, "packet-3");

            Assert.AreEqual(localResponse, beforeSpeech.TurnId);
            Assert.AreEqual(localResponse, afterSpeech.TurnId);
            Assert.AreEqual(responseId ?? localResponse, afterSpeech.MessageId);
            Assert.AreEqual("session-1:r2", afterSpeech.ResponseId);
            Assert.AreEqual(nextResponse, next.TurnId);
            Assert.AreEqual(nextResponse, next.ResponseId);
        }

        [Test]
        public void ResolveSpeechOwner_StopReusesActiveOwnerAndCompletionClearsIt()
        {
            var state = new CharacterResponseState();
            var incoming = new LipSyncResponseOwner("response-1", 7, 2, 4);

            LipSyncResponseOwner started = state.ResolveSpeechOwner("participant-1", incoming, true);
            LipSyncResponseOwner stopped = state.ResolveSpeechOwner("participant-1", default, false);
            state.CompleteSpeech("participant-1");
            LipSyncResponseOwner afterCompletion = state.ResolveSpeechOwner("participant-1", default, false);

            Assert.AreEqual(incoming.CanonicalKey, started.CanonicalKey);
            Assert.AreEqual(incoming.CanonicalKey, stopped.CanonicalKey);
            Assert.IsFalse(afterCompletion.HasIdentity);
        }

        [Test]
        public void ResolveProjectionResponseId_UsesSpeechOwnerThenStoredResponse()
        {
            var state = new CharacterResponseState();
            state.EnsureResponseId("participant-1");
            string stored = state.ResolveProjectionResponseId("participant-1", default);

            string explicitOwner = state.ResolveProjectionResponseId(
                "participant-1",
                new LipSyncResponseOwner("response-2", null, null, null));

            StringAssert.StartsWith("participant-1:character-response-", stored);
            Assert.AreEqual("response-2", explicitOwner);
        }

        [Test]
        public void PromoteAnonymousParticipant_PreservesResponseIdentity()
        {
            var state = new CharacterResponseState();
            string anonymousResponse = state.BeginResponse(string.Empty);

            state.PromoteAnonymousParticipant("participant-1");
            CharacterResponseState.TranscriptIdentity resolved = state.ResolveTranscriptIdentity(
                "participant-1", null, null, null, "packet-1");

            Assert.AreEqual(anonymousResponse, resolved.TurnId);
            Assert.AreEqual(anonymousResponse, resolved.ResponseId);
        }
    }
}
