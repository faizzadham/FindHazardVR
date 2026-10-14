using Convai.Runtime.Room;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    /// <summary>
    ///     Covers when evidence of a character's presence may stand in for the service saying so.
    /// </summary>
    /// <remarks>
    ///     The rule that was missing is the whole subject here: telling a readiness signal that is
    ///     <b>late</b> from one that is <b>missing</b>. Treating the first scrap of evidence as proof
    ///     announced characters the service was not routing to yet, and everything the player said in
    ///     that window went nowhere unlogged.
    /// </remarks>
    [TestFixture]
    public sealed class CharacterReadyRecoveryPolicyTests
    {
        private const double Grace = CharacterReadyRecoveryPolicy.DefaultGraceSeconds;

        private static CharacterReadyEvidenceVerdict Evaluate(
            CharacterRoomStatus status = CharacterRoomStatus.Starting,
            bool alreadyArmed = false,
            double secondsSinceArmed = 0d,
            bool hasMembership = true,
            CharacterReadyEvidence evidence = CharacterReadyEvidence.Presence) =>
            CharacterReadyRecoveryPolicy.Evaluate(
                hasMembership, status, alreadyArmed, secondsSinceArmed, evidence, Grace);

        [Test]
        public void FirstEvidenceArmsInsteadOfAnnouncing()
        {
            Assert.That(
                Evaluate(),
                Is.EqualTo(CharacterReadyEvidenceVerdict.ArmRecovery),
                "A character joining a live room has its audio track before the service announces "
                + "it. Announcing on that evidence is what lost the player's first message.");
        }

        [Test]
        public void EvidenceInsideTheGraceStillWaits()
        {
            Assert.That(
                Evaluate(alreadyArmed: true, secondsSinceArmed: Grace - 0.01d),
                Is.EqualTo(CharacterReadyEvidenceVerdict.ArmRecovery));
        }

        [Test]
        public void TheMeasuredHeadStartFallsInsideTheGrace()
        {
            // The live measurement that produced this rule: 1.44 s between the audio track and the
            // service's readiness signal. If the grace ever drops below it, the bug returns.
            Assert.That(
                Evaluate(alreadyArmed: true, secondsSinceArmed: 1.44d),
                Is.EqualTo(CharacterReadyEvidenceVerdict.ArmRecovery),
                "The grace must cover the readiness delay actually observed on a live room.");
        }

        [Test]
        public void ACharacterThatIsSpeakingIsReadyImmediately()
        {
            Assert.That(
                Evaluate(evidence: CharacterReadyEvidence.Speech),
                Is.EqualTo(CharacterReadyEvidenceVerdict.RecoverNow),
                "Nothing that is not ready produces speech. Holding a talking character in "
                + "Preparing would block the player's input while they can hear it answering — the "
                + "same lie as announcing it early, pointed the other way.");
        }

        [Test]
        public void SpeechDoesNotWaitOutAGraceThatIsAlreadyRunning()
        {
            Assert.That(
                Evaluate(alreadyArmed: true, secondsSinceArmed: 0d,
                    evidence: CharacterReadyEvidence.Speech),
                Is.EqualTo(CharacterReadyEvidenceVerdict.RecoverNow),
                "An armed grace must not delay conclusive evidence that arrives while it runs.");
        }

        [Test]
        public void SpeechStillCannotRecoverAFailedOrReadyCharacter()
        {
            Assert.That(
                Evaluate(CharacterRoomStatus.Failed, evidence: CharacterReadyEvidence.Speech),
                Is.EqualTo(CharacterReadyEvidenceVerdict.Ignore));
            Assert.That(
                Evaluate(CharacterRoomStatus.Ready, evidence: CharacterReadyEvidence.Speech),
                Is.EqualTo(CharacterReadyEvidenceVerdict.Ignore));
        }

        [Test]
        public void PresenceAloneNeverRecoversWithoutWaiting()
        {
            // The whole separation: a subscribed audio track is routine before readiness, so it
            // must not be allowed to stand in for it on arrival.
            Assert.That(
                Evaluate(evidence: CharacterReadyEvidence.Presence),
                Is.EqualTo(CharacterReadyEvidenceVerdict.ArmRecovery));
        }

        [Test]
        public void SilencePastTheGraceRecovers()
        {
            Assert.That(
                Evaluate(alreadyArmed: true, secondsSinceArmed: Grace),
                Is.EqualTo(CharacterReadyEvidenceVerdict.RecoverNow),
                "The recovery exists for a readiness signal that never comes; the grace must not "
                + "turn that failure into a permanent hang.");
        }

        [Test]
        public void AReadyCharacterIsLeftAlone()
        {
            Assert.That(
                Evaluate(CharacterRoomStatus.Ready),
                Is.EqualTo(CharacterReadyEvidenceVerdict.Ignore));
            Assert.That(
                Evaluate(CharacterRoomStatus.Ready, alreadyArmed: true, secondsSinceArmed: Grace * 4),
                Is.EqualTo(CharacterReadyEvidenceVerdict.Ignore),
                "An armed recovery must not fire behind a character the service has since "
                + "announced — that is exactly what the grace is for.");
        }

        [Test]
        public void AFailedCharacterIsNeverRecovered()
        {
            Assert.That(
                Evaluate(CharacterRoomStatus.Failed, alreadyArmed: true, secondsSinceArmed: Grace * 4),
                Is.EqualTo(CharacterReadyEvidenceVerdict.Ignore),
                "Recovering a failed character hides the failure behind one that never answers.");
        }

        [Test]
        public void EvidenceWithoutAMembershipIsIgnored()
        {
            Assert.That(
                Evaluate(hasMembership: false),
                Is.EqualTo(CharacterReadyEvidenceVerdict.Ignore));
        }
    }
}
