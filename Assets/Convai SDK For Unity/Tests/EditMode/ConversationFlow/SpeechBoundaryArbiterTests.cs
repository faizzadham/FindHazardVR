using Convai.Modules.ConversationFlow.Core;
using Convai.Runtime.Animation;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.ConversationFlow
{
    [TestFixture]
    public sealed class SpeechBoundaryArbiterTests
    {
        private const float Frame = 1f / 60f;

        /// <summary>
        ///     The hold these tests configure, plus a margin. Named rather than written as a
        ///     literal at each call site: a duration shorter than the hold turns "the voice ended
        ///     the turn" into "the turn had not ended yet", and the assertion still reads as though
        ///     it proved something.
        /// </summary>
        private const float Hold = 0.35f;
        private const float PastHold = Hold + 0.1f;

        [TearDown]
        public void TearDown() => LogAssert.NoUnexpectedReceived();

        private static SpeechBoundaryArbiterConfig Config(
            bool endOnVoice = true,
            float voiceEndHoldSeconds = Hold)
            => new(endOnVoice, voiceEndHoldSeconds);

        /// <summary>Runs <paramref name="seconds" /> of frames and returns the final verdict.</summary>
        private static bool Run(
            SpeechBoundaryArbiter arbiter,
            bool service,
            bool? voice,
            in SpeechBoundaryArbiterConfig config,
            float seconds)
        {
            bool speaking = arbiter.IsSpeaking;
            int frames = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.CeilToInt(seconds / Frame));
            for (int i = 0; i < frames; i++)
                speaking = arbiter.Step(service, voice, config, Frame);
            return speaking;
        }

        // ── The defect itself ──────────────────────────────────────────────────────────

        [Test]
        public void LocalSilence_EndsTurn_WhileServiceStillSaysSpeaking()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();

            Run(arbiter, true, true, config, 0.5f);
            Assert.IsTrue(arbiter.IsSpeaking, "A turn both sources agree on must be speaking.");

            bool speaking = Run(arbiter, true, false, config, PastHold);

            Assert.IsFalse(speaking,
                "Local evidence that the voice stopped must end the turn without waiting for the service.");
            Assert.AreEqual(SpeechBoundarySource.Voice, arbiter.EndedBy);
        }

        [Test]
        public void LateServiceStop_IsANoOp_AfterLocalAlreadyEndedTheTurn()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();

            Run(arbiter, true, true, config, 0.5f);
            Run(arbiter, true, false, config, PastHold);
            Assert.IsFalse(arbiter.IsSpeaking);

            // The service is still asserting "speaking" for a while, then finally releases it.
            bool speaking = Run(arbiter, true, false, config, 0.8f);
            Assert.IsFalse(speaking,
                "A stale service flag must not restart a turn local evidence already ended.");

            speaking = arbiter.Step(false, false, config, Frame);
            Assert.IsFalse(speaking, "The service catching up must change nothing.");
        }

        [Test]
        public void ServiceLag_IsMeasured_EvenWhenLocalEndingsAreDisabled()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config(endOnVoice: false);

            Run(arbiter, true, true, config, 0.5f);
            Run(arbiter, true, false, config, 0.5f);

            Assert.IsTrue(arbiter.IsSpeaking,
                "With local endings off, only the service may end a turn.");
            Assert.Greater(arbiter.CurrentServiceLagSeconds, 0.4f,
                "The lag must be measured regardless of whether we are allowed to act on it.");

            arbiter.Step(false, false, config, Frame);
            Assert.IsFalse(arbiter.IsSpeaking);
            Assert.AreEqual(SpeechBoundarySource.Service, arbiter.EndedBy);
            Assert.Greater(arbiter.LastVoiceLeadSeconds, 0.4f,
                "The improvement forgone must still be reported.");
        }

        // ── The service keeps its authority ────────────────────────────────────────────

        [Test]
        public void ServiceStop_EndsTurn_ImmediatelyEvenWhileVoiceIsStillAudible()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();

            Run(arbiter, true, true, config, 0.5f);
            bool speaking = arbiter.Step(false, true, config, Frame);

            Assert.IsFalse(speaking,
                "An interruption or a cancelled response must cut the performance while audio is still draining.");
            Assert.AreEqual(SpeechBoundarySource.Service, arbiter.EndedBy);
        }

        // ── Pauses must not end turns ──────────────────────────────────────────────────

        [Test]
        public void ShortLocalDropout_InsideConfirmWindow_NeverLeavesTheArbiter()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config(voiceEndHoldSeconds: 0.35f);

            Run(arbiter, true, true, config, 0.5f);

            // Silent for less than the confirmation window, then audible again.
            for (int i = 0; i < 15; i++)
            {
                bool speaking = arbiter.Step(true, false, config, Frame);
                Assert.IsTrue(speaking, "A dropout inside the confirmation window must never be reported.");
            }

            Assert.IsTrue(arbiter.Step(true, true, config, Frame));
            Assert.IsFalse(arbiter.IsConfirmingEnd, "Returning audio must cancel the pending ending.");
            Assert.AreEqual(SpeechBoundarySource.None, arbiter.EndedBy, "No turn ended at all.");
        }

        [Test]
        public void VoiceEndHold_DelaysEveryVoiceEnding_WithoutAffectingTheService()
        {
            SpeechBoundaryArbiterConfig config =
                Config(voiceEndHoldSeconds: 0.2f);

            var arbiter = new SpeechBoundaryArbiter();
            Run(arbiter, true, true, config, 0.5f);

            Assert.IsTrue(Run(arbiter, true, false, config, 0.1f),
                "The body keeps performing for the authored hold.");
            Assert.IsFalse(Run(arbiter, true, false, config, 0.2f),
                "And lets go once it has elapsed.");

            var serviceEnded = new SpeechBoundaryArbiter();
            Run(serviceEnded, true, true, config, 0.5f);
            Assert.IsFalse(
                serviceEnded.Step(false, true, config, Frame),
                "The hold must not delay a service-driven ending — an interruption has to be immediate.");
        }

        // ── Starting ───────────────────────────────────────────────────────────────────

        [Test]
        public void AudibleVoice_StartsTurn_BeforeTheServiceSaysAnything()
        {
            var arbiter = new SpeechBoundaryArbiter();
            bool speaking = arbiter.Step(false, true, Config(), Frame);

            Assert.IsTrue(speaking, "Audible audio is unambiguous evidence that the character is talking.");
            Assert.AreEqual(SpeechEvidenceRung.AudiblePlayback, arbiter.Rung);
        }

        [Test]
        public void VoiceReturningAfterALocalEnding_StartsAFreshTurn()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();

            Run(arbiter, true, true, config, 0.5f);
            Run(arbiter, true, false, config, PastHold);
            Assert.IsFalse(arbiter.IsSpeaking);

            Assert.IsTrue(arbiter.Step(true, true, config, Frame),
                "The stale-service latch must never block local evidence that the voice came back.");
        }

        // ── Absence of a witness ───────────────────────────────────────────────────────

        [Test]
        public void NoLocalEvidence_ReproducesServiceOnlyBehaviourExactly()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();

            Assert.IsTrue(arbiter.Step(true, null, config, Frame));
            Assert.AreEqual(SpeechEvidenceRung.ServiceOnly, arbiter.Rung);

            Assert.IsTrue(Run(arbiter, true, null, config, 2f),
                "With no witness there is nothing to end the turn early.");

            Assert.IsFalse(arbiter.Step(false, null, config, Frame));
            Assert.AreEqual(SpeechBoundarySource.Service, arbiter.EndedBy);
        }

        [Test]
        public void NullLocalEvidence_IsNotTheSameAsSilence()
        {
            SpeechBoundaryArbiterConfig config = Config(voiceEndHoldSeconds: 0f);

            var absent = new SpeechBoundaryArbiter();
            Run(absent, true, null, config, 1f);

            var silent = new SpeechBoundaryArbiter();
            Run(silent, true, true, config, 0.3f);
            Run(silent, true, false, config, 0.7f);

            Assert.IsTrue(absent.IsSpeaking, "No witness means no verdict.");
            Assert.IsFalse(silent.IsSpeaking, "A witness reporting silence is a verdict.");
        }

        // ── The switch ─────────────────────────────────────────────────────────────────

        [Test]
        public void LocalEndingsOff_LeavesTheTurnEntirelyToTheService()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config(endOnVoice: false);

            Run(arbiter, true, true, config, 0.5f);
            Assert.IsTrue(Run(arbiter, true, false, config, 3f),
                "Off means off, however long the voice has been silent.");
        }

        // ── Discipline ─────────────────────────────────────────────────────────────────

        [Test]
        public void IdenticalInputSequences_ProduceIdenticalOutputs()
        {
            SpeechBoundaryArbiterConfig config = Config();
            var a = new SpeechBoundaryArbiter();
            var b = new SpeechBoundaryArbiter();

            bool[] service = { true, true, true, true, true, true, false, false };
            bool?[] local = { true, true, false, true, false, false, false, false };

            for (int i = 0; i < service.Length; i++)
            {
                for (int f = 0; f < 20; f++)
                {
                    bool ra = a.Step(service[i], local[i], config, Frame);
                    bool rb = b.Step(service[i], local[i], config, Frame);
                    Assert.AreEqual(ra, rb, $"Divergence at segment {i} frame {f}.");
                }
            }

            Assert.AreEqual(a.EndedBy, b.EndedBy);
            Assert.AreEqual(a.Rung, b.Rung);
        }

        [Test]
        public void Reset_ClearsEveryLatch()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();

            // Past the hold on purpose: the latches must have something in them before Reset can
            // be shown to clear them. Stopping short would assert that EndedBy is None when no turn
            // had ended, which proves nothing.
            Run(arbiter, true, true, config, 0.5f);
            Run(arbiter, true, false, config, PastHold);
            Assert.AreEqual(SpeechBoundarySource.Voice, arbiter.EndedBy, "Precondition: a turn ended.");

            arbiter.Reset();

            Assert.IsFalse(arbiter.IsSpeaking);
            Assert.AreEqual(SpeechBoundarySource.None, arbiter.EndedBy);
            Assert.AreEqual(SpeechEvidenceRung.ServiceOnly, arbiter.Rung);
            Assert.AreEqual(0f, arbiter.LastVoiceLeadSeconds);
        }

        // ── Regression: the ladder must not shadow the rung that knows ─────────────────

        // ── The instrument must be able to disagree ────────────────────────────────────

        [Test]
        public void TheSuiteWouldFail_AgainstTheOldOrBehaviour()
        {
            // The old rule was "speaking while EITHER source says so", which cannot end a turn on
            // local evidence. Modelled here so the suite is shown to bite: if this assertion ever
            // matched the arbiter's real output, every test above would be vacuous.
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();

            Run(arbiter, true, true, config, 0.5f);
            bool arbitrated = Run(arbiter, true, false, config, PastHold);

            const bool serviceSaysSpeaking = true;
            const bool localSaysSpeaking = false;
            bool legacyOr = serviceSaysSpeaking || localSaysSpeaking;

            Assert.IsTrue(legacyOr, "The legacy rule keeps the turn alive on the service flag alone.");
            Assert.AreNotEqual(legacyOr, arbitrated,
                "The arbiter must disagree with the behaviour it replaced, or it changed nothing.");
        }

        // -- Lip-sync playback as a witness -------------------------------------------

        private static SpeechPlaybackReading Playing(float remaining) =>
            new(hasResponse: true, inputSettled: true, remainingSeconds: remaining, finished: false);

        private static readonly SpeechPlaybackReading FinishedReading =
            new(hasResponse: true, inputSettled: true, remainingSeconds: 0f, finished: true);

        [Test]
        public void PlaybackFinished_EndsTurnAtOnce_WithoutTheVoiceHold()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();
            Run(arbiter, true, true, config, 0.5f);

            // One frame of "last frame shown, voice gone" is enough: no hold, no service.
            bool speaking = arbiter.Step(true, false, FinishedReading, in config, Frame);

            Assert.IsFalse(speaking, "lip sync knows the response ended; there is no pause to rule out");
            Assert.AreEqual(SpeechBoundarySource.Playback, arbiter.EndedBy);
            Assert.AreEqual(SpeechEvidenceRung.LipSyncPlayback, arbiter.Rung);
        }

        [Test]
        public void PlaybackStillHasFrames_KeepsTurn_ThroughASilenceLongerThanTheHold()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();
            Run(arbiter, true, true, config, 0.5f);

            bool speaking = true;
            int frames = UnityEngine.Mathf.CeilToInt((PastHold * 2f) / Frame);
            for (int i = 0; i < frames; i++)
                speaking = arbiter.Step(true, false, Playing(1.5f), in config, Frame);

            Assert.IsTrue(speaking, "a quiet stretch with frames still ahead is a pause inside the response");
            Assert.IsFalse(arbiter.IsConfirmingEnd);
        }

        [Test]
        public void PlaybackFinished_DoesNotRestartOnTheStaleServiceFlag()
        {
            var arbiter = new SpeechBoundaryArbiter();
            SpeechBoundaryArbiterConfig config = Config();
            Run(arbiter, true, true, config, 0.5f);
            arbiter.Step(true, false, FinishedReading, in config, Frame);

            bool speaking = arbiter.Step(true, false, null, in config, Frame);

            Assert.IsFalse(speaking, "the service flag has not been released yet: not a new turn");
        }

    }
}
