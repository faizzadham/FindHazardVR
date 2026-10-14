using Convai.Domain.Models;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    public sealed class SpeakerInfoTests
    {
        [TestCase("speaker-1", "Rishav", true)]
        [TestCase("speaker-1", null, true)]
        [TestCase(null, "Rishav", true)]
        [TestCase(null, null, false)]
        public void Validity_RequiresAnIdentity(string speakerId, string speakerName, bool expected)
        {
            var speaker = new SpeakerInfo(speakerId, speakerName, null);
            Assert.That(speaker.IsValid, Is.EqualTo(expected));
        }

        [TestCase("local-player", "You", true)]
        [TestCase(null, "Guest", true)]
        [TestCase("speaker-1", "Rishav", false)]
        public void DefaultPlayerDetection_UsesReservedOrMissingId(
            string speakerId,
            string speakerName,
            bool expected)
        {
            var speaker = new SpeakerInfo(speakerId, speakerName, null);
            Assert.That(speaker.IsDefaultPlayer, Is.EqualTo(expected));
        }

        [TestCase("speaker-1", "Rishav", "Rishav")]
        [TestCase("speaker-1", null, "speaker-1")]
        [TestCase(null, null, "Unknown")]
        public void DisplayName_UsesNameThenIdThenFallback(
            string speakerId,
            string speakerName,
            string expected)
        {
            var speaker = new SpeakerInfo(speakerId, speakerName, null);
            Assert.That(speaker.GetDisplayName(), Is.EqualTo(expected));
        }

        [Test]
        public void NullConstructorValues_AreNormalized()
        {
            var speaker = new SpeakerInfo(null, null, null);
            Assert.That((speaker.SpeakerId, speaker.SpeakerName, speaker.ParticipantId),
                Is.EqualTo((string.Empty, string.Empty, string.Empty)));
        }

        [Test]
        public void TranscriptMessage_RoundTripsSpeakerIdentity()
        {
            TranscriptMessage message = TranscriptMessage.ForPlayer(
                "Hello", true, "speaker-1", "Rishav", "participant-1");

            SpeakerInfo speaker = SpeakerInfo.FromMessage(message);

            Assert.That((speaker.SpeakerId, speaker.SpeakerName, speaker.ParticipantId, speaker.SpeakerType),
                Is.EqualTo(("speaker-1", "Rishav", "participant-1", SpeakerType.Player)));
        }
    }
}
