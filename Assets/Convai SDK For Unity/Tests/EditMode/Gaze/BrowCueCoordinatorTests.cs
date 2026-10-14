using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.Gaze.Core.Behaviors;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Unit tests for <see cref="BrowCueCoordinator" />: upward-saccade pitch threshold
    ///     and intensity scaling, nod-start/interruption edge triggers, priority, shared rate
    ///     limiting, the surprise-flash rate-limit bypass with its own independent refractory,
    ///     and long-idle determinism (no spurious cues).
    /// </summary>
    [TestFixture]
    public sealed class BrowCueCoordinatorTests
    {
        private const float Dt = 1f / 60f;

        private BrowCueCoordinator _coordinator;

        [SetUp]
        public void SetUp() => _coordinator = new BrowCueCoordinator();

        [Test]
        public void UpwardPitch_AboveThreshold_EmitsSubtleRaise_ScaledByPitch()
        {
            _coordinator.Tick(eyePitchDegrees: 17.5f, nodStarted: false, interruptionFired: false, Dt);

            Assert.IsTrue(_coordinator.HasPendingCue, "A sustained upward eye pitch above threshold must emit a cue.");
            Assert.AreEqual(BrowCueKind.SubtleRaise, _coordinator.PendingKind);
            // Threshold 10°, saturates at 25° -> 17.5° is exactly the midpoint -> intensity ~0.5.
            Assert.That(_coordinator.PendingIntensity, Is.EqualTo(0.5f).Within(0.01f),
                "Intensity must scale linearly with pitch between the threshold and saturation.");
        }

        [Test]
        public void UpwardPitch_BelowThreshold_EmitsNothing()
        {
            _coordinator.Tick(eyePitchDegrees: 5f, nodStarted: false, interruptionFired: false, Dt);
            Assert.IsFalse(_coordinator.HasPendingCue, "Pitch at/under the threshold must not emit a cue.");
        }

        [Test]
        public void UpwardPitch_AtSaturation_ClampsIntensityToOne()
        {
            _coordinator.Tick(eyePitchDegrees: 40f, nodStarted: false, interruptionFired: false, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue);
            Assert.AreEqual(1f, _coordinator.PendingIntensity, 0.0001f,
                "Intensity must clamp to 1 at/above the saturation pitch.");
        }

        [Test]
        public void NodStart_EmitsFlash()
        {
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: true, interruptionFired: false, Dt);

            Assert.IsTrue(_coordinator.HasPendingCue);
            Assert.AreEqual(BrowCueKind.Flash, _coordinator.PendingKind);
        }

        [Test]
        public void Interruption_EmitsSurpriseFlash_AndBypassesTheSharedRateLimit()
        {
            // Exhaust the shared cooldown with a nod first.
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: true, interruptionFired: false, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue, "Sanity: the nod consumed the shared cooldown.");

            // Immediately on the next tick (well inside the 1.5s shared cooldown), an
            // interruption must still fire.
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: true, Dt);

            Assert.IsTrue(_coordinator.HasPendingCue,
                "SurpriseFlash must bypass the shared rate limit even while it is active.");
            Assert.AreEqual(BrowCueKind.SurpriseFlash, _coordinator.PendingKind);
        }

        [Test]
        public void Interruption_TakesPriority_OverNodStart_OnTheSameTick()
        {
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: true, interruptionFired: true, Dt);

            Assert.IsTrue(_coordinator.HasPendingCue);
            Assert.AreEqual(BrowCueKind.SurpriseFlash, _coordinator.PendingKind,
                "An interruption firing this tick must win over a simultaneous nod start.");
        }

        [Test]
        public void RateLimiting_TwoNods_HalfASecondApart_OnlyOneCueFires()
        {
            int cueCount = 0;

            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: true, interruptionFired: false, Dt);
            if (_coordinator.HasPendingCue) cueCount++;

            // Advance 0.5s (well under the 1.5s shared rate limit) with a second nod attempt.
            int steps = (int)(0.5f / Dt);
            for (int i = 0; i < steps; i++)
            {
                bool secondNodTick = i == steps - 1;
                _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: secondNodTick, interruptionFired: false, Dt);
                if (_coordinator.HasPendingCue) cueCount++;
            }

            Assert.AreEqual(1, cueCount, "Two nods 0.5s apart must only produce one cue (shared rate limit).");
        }

        [Test]
        public void RateLimiting_ExpiresAfterInterval_AllowingAFreshCue()
        {
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: true, interruptionFired: false, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue);

            // Advance past the 1.5s shared rate limit with no signals.
            int steps = (int)(1.6f / Dt);
            for (int i = 0; i < steps; i++)
                _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: false, Dt);

            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: true, interruptionFired: false, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue, "A nod after the rate limit expires must produce a fresh cue.");
        }

        [Test]
        public void SurpriseRefractory_SecondInterruptionWithinWindow_DoesNotFire()
        {
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: true, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue);

            // Well within the 1.5s surprise refractory.
            int steps = (int)(0.5f / Dt);
            int surpriseFlashCount = 0;
            for (int i = 0; i < steps; i++)
            {
                _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: true, Dt);
                if (_coordinator.HasPendingCue && _coordinator.PendingKind == BrowCueKind.SurpriseFlash)
                    surpriseFlashCount++;
            }

            Assert.AreEqual(0, surpriseFlashCount,
                "A second interruption within the surprise refractory window must not re-fire.");
        }

        [Test]
        public void SurpriseRefractory_ExpiresAfterInterval_AllowingAFreshSurpriseFlash()
        {
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: true, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue);

            int steps = (int)(1.6f / Dt);
            for (int i = 0; i < steps; i++)
                _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: false, Dt);

            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: true, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue,
                "An interruption after the refractory expires must produce a fresh SurpriseFlash.");
            Assert.AreEqual(BrowCueKind.SurpriseFlash, _coordinator.PendingKind);
        }

        [Test]
        public void LongIdle_NeverProducesASpuriousCue()
        {
            int steps = (int)(30f / Dt);
            for (int i = 0; i < steps; i++)
            {
                _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: false, Dt);
                Assert.IsFalse(_coordinator.HasPendingCue,
                    $"Tick {i}: with no signals at all, no cue should ever fire.");
            }
        }

        [Test]
        public void HasPendingCue_IsClearedEachTick_EvenWhenPreviousTickEmitted()
        {
            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: true, interruptionFired: false, Dt);
            Assert.IsTrue(_coordinator.HasPendingCue);

            _coordinator.Tick(eyePitchDegrees: 0f, nodStarted: false, interruptionFired: false, Dt);
            Assert.IsFalse(_coordinator.HasPendingCue,
                "A pending cue is a single-tick pulse — it must not persist into the next tick.");
        }
    }
}
