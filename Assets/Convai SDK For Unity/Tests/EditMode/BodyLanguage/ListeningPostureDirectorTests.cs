using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="ListeningPostureDirector" />: lean-in engage/decay, stillness
    ///     rising while listening, tilt-hold cadence, the gaze-aversion gate, and the guarantee
    ///     that this director never requests Nod/Shake.
    /// </summary>
    public sealed class ListeningPostureDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float TiltCadenceSeconds = 6f;
        private const float TiltIntensity = 0.5f;

        [Test]
        public void Listening_LeanInEngagesOverTime()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            for (int i = 0; i < 300; i++) // 5 seconds — well past the ~1s engage slew
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            Assert.That(director.LeanInBias, Is.EqualTo(0.6f).Within(0.02f),
                "Lean-in must settle at the policy's ListeningLeanIn while Listening.");
        }

        [Test]
        public void LeavingListening_LeanInDecaysBackToZero()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            for (int i = 0; i < 300; i++)
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
            Assert.That(director.LeanInBias, Is.GreaterThan(0.3f), "Sanity: lean-in engaged.");

            for (int i = 0; i < 300; i++)
                director.Tick(DialogueState.Idle, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            Assert.That(director.LeanInBias, Is.EqualTo(0f).Within(0.02f),
                "Lean-in must decay back to zero once the state leaves Listening.");
        }

        [Test]
        public void ListeningPostureDisabled_NeverEngagesLeanIn()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            for (int i = 0; i < 300; i++)
                director.Tick(DialogueState.Listening, false, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            Assert.That(director.LeanInBias, Is.EqualTo(0f).Within(0.01f),
                "ListeningPostureEnabled=false must keep lean-in at zero even while Listening.");
        }

        [Test]
        public void Listening_StillnessFactorRisesTowardOne()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            for (int i = 0; i < 300; i++)
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            Assert.That(director.StillnessFactor, Is.EqualTo(1f).Within(0.02f),
                "Stillness must rise toward 1 while attentively listening.");
        }

        [Test]
        public void NotListening_StillnessFactorFallsTowardZero()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            for (int i = 0; i < 300; i++)
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            for (int i = 0; i < 300; i++)
                director.Tick(DialogueState.Idle, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            Assert.That(director.StillnessFactor, Is.EqualTo(0f).Within(0.02f));
        }

        [Test]
        public void TiltHold_RequestedOnCadence_WhileListeningAndEnabled()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            bool sawTiltHold = false;
            const int ticks = 60 * 20; // 20 seconds — comfortably past the 6s mean cadence
            for (int i = 0; i < ticks; i++)
            {
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold) sawTiltHold = true;
            }

            Assert.IsTrue(sawTiltHold, "A tilt-hold must be requested at least once over 20s of continuous Listening.");
        }

        [Test]
        public void TiltHold_NeverRequested_WhileGazeIsAverting()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            bool sawTiltHold = false;
            const int ticks = 60 * 30; // 30 seconds, generous margin over the mean cadence
            for (int i = 0; i < ticks; i++)
            {
                director.Tick(DialogueState.Listening, true, 0.6f, true /* gazeIsAverting */, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold) sawTiltHold = true;
            }

            Assert.IsFalse(sawTiltHold, "A tilt-hold must never be scheduled or requested while gaze is averting.");
        }

        [Test]
        public void TiltHold_NeverRequested_WhenNotListeningOrDisabled()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            bool sawTiltHold = false;
            for (int i = 0; i < 60 * 30; i++)
            {
                director.Tick(DialogueState.Idle, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold) sawTiltHold = true;
            }

            Assert.IsFalse(sawTiltHold, "A tilt-hold must never fire outside Listening.");
        }

        [Test]
        public void TiltHold_WhenRequested_CarriesAValidIntensity_AndActuallyFires()
        {
            // The director's ONLY head-gesture-shaped output is a tilt-hold (WantsTiltHold +
            // TiltHoldIntensity); the controller wires those exclusively to HeadGestureKind.Tilt,
            // so a nod/shake is structurally impossible. Assert real behavior rather than mere
            // API presence: a tilt-hold actually fires over a listening window, and whenever it
            // does its intensity stays within the valid amplitude domain (never an out-of-range
            // value). The final sawTiltHold assertion guarantees the in-range check ran at least
            // once, so this test cannot silently pass on a never-firing director.
            var director = new ListeningPostureDirector();
            director.Seed(1);

            bool sawTiltHold = false;
            for (int i = 0; i < 60 * 20; i++)
            {
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold)
                {
                    sawTiltHold = true;
                    Assert.That(director.TiltHoldIntensity, Is.InRange(0f, 1f),
                        "A requested tilt-hold must carry an intensity within the valid amplitude domain.");
                }
            }

            Assert.IsTrue(sawTiltHold,
                "Sanity: a tilt-hold must fire at least once so the intensity assertion actually runs.");
        }

        [Test]
        public void Determinism_SameSeed_ProducesIdenticalSchedule()
        {
            var directorA = new ListeningPostureDirector();
            directorA.Seed(777);
            var directorB = new ListeningPostureDirector();
            directorB.Seed(777);

            for (int i = 0; i < 60 * 30; i++)
            {
                directorA.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                directorB.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

                Assert.That(directorA.WantsTiltHold, Is.EqualTo(directorB.WantsTiltHold));
                Assert.That(directorA.LeanInBias, Is.EqualTo(directorB.LeanInBias));
            }
        }

        [Test]
        public void Reset_ReturnsToInactiveState()
        {
            var director = new ListeningPostureDirector();
            director.Seed(1);

            for (int i = 0; i < 300; i++)
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            director.Reset();

            Assert.That(director.LeanInBias, Is.EqualTo(0f));
            Assert.That(director.StillnessFactor, Is.EqualTo(0f));
            Assert.IsFalse(director.WantsTiltHold);
        }

        // ── User-pause backchannel pull-forward ─────

        /// <summary>Ticks until a fresh tilt-hold cadence has just been (re-)sampled, then returns its total.</summary>
        private static float TickUntilFreshCadenceStarts(ListeningPostureDirector director)
        {
            // First tilt-hold fire re-samples a fresh target on the SAME tick it fires, so the
            // very next tick observes a freshly-started cadence at 100% remaining.
            for (int i = 0; i < 60 * 30; i++)
            {
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold)
                {
                    director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                    Assert.That(director.TiltCadenceTotalSeconds, Is.GreaterThan(0f), "Sanity: a fresh cadence must be scheduled right after a fire.");
                    return director.TiltCadenceTotalSeconds;
                }
            }

            Assert.Fail("A tilt-hold never fired within 30s — cannot set up the pull-forward scenario.");
            return 0f;
        }

        [Test]
        public void NotifyUserPause_WithHalfCadenceRemaining_FiresTiltHoldWithinWindow()
        {
            var director = new ListeningPostureDirector();
            director.Seed(2);

            float total = TickUntilFreshCadenceStarts(director);

            // Advance to roughly 50% remaining (well above the 30% pull-forward threshold).
            int halfTicks = Mathf.RoundToInt((total * 0.5f) / Dt);
            for (int i = 0; i < halfTicks; i++)
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            Assert.That(director.TiltCadenceRemainingSeconds / director.TiltCadenceTotalSeconds, Is.GreaterThan(0.3f).And.LessThan(0.7f),
                "Sanity: roughly half the cadence should remain before NotifyUserPause.");

            director.NotifyUserPause();

            bool firedWithinWindow = false;
            int windowTicks = Mathf.CeilToInt(0.9f / Dt) + 2; // small margin for discretization
            for (int i = 0; i < windowTicks; i++)
            {
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold)
                {
                    firedWithinWindow = true;
                    break;
                }
            }

            Assert.IsTrue(firedWithinWindow,
                "A tilt-hold scheduled with >=30% cadence remaining must fire within the 0.9s pause window after NotifyUserPause().");
        }

        [Test]
        public void WithoutNotifyUserPause_TiltHoldFiresOnItsOwnSchedule_NotWithin09s()
        {
            var director = new ListeningPostureDirector();
            director.Seed(2);

            float total = TickUntilFreshCadenceStarts(director);

            int halfTicks = Mathf.RoundToInt((total * 0.5f) / Dt);
            for (int i = 0; i < halfTicks; i++)
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            // No NotifyUserPause() call. With ~half of a 6s-mean cadence remaining, it must not
            // fire within the next 0.9s.
            bool firedWithin09s = false;
            int windowTicks = Mathf.CeilToInt(0.9f / Dt);
            for (int i = 0; i < windowTicks; i++)
            {
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold) firedWithin09s = true;
            }

            Assert.IsFalse(firedWithin09s,
                "Without NotifyUserPause(), a cadence with ~50% of a multi-second cadence remaining must not fire within 0.9s.");
        }

        [Test]
        public void SecondPauseInsideOneWindow_DoesNotDoubleFire()
        {
            var director = new ListeningPostureDirector();
            director.Seed(3);

            float total = TickUntilFreshCadenceStarts(director);

            int halfTicks = Mathf.RoundToInt((total * 0.5f) / Dt);
            for (int i = 0; i < halfTicks; i++)
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);

            director.NotifyUserPause();
            // A second pause arrives almost immediately, before the pulled-forward tilt-hold has
            // actually fired.
            director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
            director.NotifyUserPause();

            int fireCount = 0;
            int windowTicks = Mathf.CeilToInt(2f / Dt); // generous margin past both windows
            for (int i = 0; i < windowTicks; i++)
            {
                director.Tick(DialogueState.Listening, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold) fireCount++;
            }

            Assert.That(fireCount, Is.EqualTo(1),
                "A second NotifyUserPause() call before the first pulled-forward tilt-hold fires must not cause a double-fire.");
        }

        [Test]
        public void NotifyUserPause_WhileNotListening_HasNoEffect_TiltHoldNeverFires()
        {
            var director = new ListeningPostureDirector();
            director.Seed(4);

            director.NotifyUserPause();

            bool sawTiltHold = false;
            for (int i = 0; i < 60 * 2; i++)
            {
                director.Tick(DialogueState.Idle, true, 0.6f, false, Dt, TiltCadenceSeconds, TiltIntensity);
                if (director.WantsTiltHold) sawTiltHold = true;
            }

            Assert.IsFalse(sawTiltHold, "NotifyUserPause() outside Listening must never cause a tilt-hold (controller-side gate; director stays inert while disengaged).");
        }
    }
}
