using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Modules.BodyLanguage.Core.Policy;
using Convai.Modules.BodyLanguage.Core.Signals;
using Convai.Modules.BodyLanguage.Data;
using Convai.Tests.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="BreathingDirector" />: spot values flow through
    ///     (Thinking ⇒ irregular breath), and targets slew rather than snap.
    /// </summary>
    public sealed class BreathingDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float SlewSeconds = 1.5f;

        private static BodyLanguageStatePolicy IdlePolicy() => new()
        {
            State = DialogueState.Idle,
            BreathRateCpm = 13f,
            BreathDepth = 0.5f,
            BreathIrregularity = 0.1f
        };

        private static BodyLanguageStatePolicy ThinkingPolicy() => new()
        {
            State = DialogueState.Thinking,
            BreathRateCpm = 12f,
            BreathDepth = 0.5f,
            BreathIrregularity = 0.5f
        };

        [Test]
        public void Thinking_ProducesHigherIrregularityThanIdle()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy thinking = ThinkingPolicy();

            for (int i = 0; i < 600; i++)
                director.Tick(in thinking, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.Irregularity, Is.GreaterThan(0.3f),
                "Thinking must settle on a distinctly higher irregularity than Idle's 0.1.");
        }

        [Test]
        public void StateFlip_RateSlews_NeverSnaps()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            BodyLanguageStatePolicy thinking = ThinkingPolicy();

            for (int i = 0; i < 300; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
            float rateAtIdle = director.RateCpm;

            director.Tick(in thinking, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.RateCpm, Is.Not.EqualTo(thinking.BreathRateCpm).Within(1e-5f),
                "A single tick after a state flip must not snap the breath rate.");
            Assert.That(director.RateCpm, Is.LessThan(rateAtIdle),
                "The rate must be moving toward the new (lower) goal.");
        }

        [Test]
        public void EmotionRateScale_MultipliesStateRate()
        {
            var director = new BreathingDirector();
            var fastEmotion = new EmotionBodyModulator(); // identity (1x) by default without a Tick call
            BodyLanguageStatePolicy idle = IdlePolicy();

            for (int i = 0; i < 600; i++)
                director.Tick(in idle, fastEmotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.RateCpm, Is.EqualTo(13f).Within(0.1f),
                "With an identity emotion modulator, the state's own rate must be used unscaled.");
        }

        [Test]
        public void FirstTick_Snaps()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();

            director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.RateCpm, Is.EqualTo(13f).Within(1e-3f));
            Assert.That(director.Depth, Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void Reset_ReturnsToRestingDefaults()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy thinking = ThinkingPolicy();
            for (int i = 0; i < 300; i++)
                director.Tick(in thinking, emotion, SlewSeconds, Dt, 0f, false);

            director.Reset();

            Assert.That(director.RateCpm, Is.EqualTo(13f));
            Assert.That(director.Depth, Is.EqualTo(0f));
            Assert.That(director.Irregularity, Is.EqualTo(0f));
        }

        // ── Breath events ──────────────────────────────────

        private static void Settle(
            BreathingDirector director, EmotionBodyModulator emotion, in BodyLanguageStatePolicy policy,
            float speechEnergy01 = 0f, bool isSpeaking = false)
        {
            for (int i = 0; i < 600; i++)
                director.Tick(in policy, emotion, SlewSeconds, Dt, speechEnergy01, isSpeaking);
        }

        [Test]
        public void CatchBreathEvent_LiftsRateAndDepth_ThenReturnsToBase()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            Settle(director, emotion, in idle);
            float baseRate = director.RateCpm;
            float baseDepth = director.Depth;

            director.TriggerEvent(BreathEventKind.CatchBreath);
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.CatchBreath));

            // CatchBreath's attack is 0.2s, and
            // RateCpm/Depth are now the bus-published (slew-limited, 25cpm/s / 0.35depth/s)
            // outputs rather than the raw instantaneous values, so reaching a detectable peak
            // takes longer than a single-attack-length tick count. 20 ticks (0.333s) lands mid
            // hold (hold window is [0.2s, 0.3s), comfortably clear of both the attack/hold and
            // hold/decay boundaries against float accumulation error) and gives the bus time to
            // climb from baseline. Hand-derived (tick-by-tick MoveTowards simulation) published
            // values at 20 ticks: RateCpm ~= 18.98 (bus is budget-limited climbing toward a
            // combined target of 13*1.8=23.4) and Depth ~= 0.613 (target 0.5*1.3=0.65) — both
            // comfortably clear their thresholds below (RateCpm > baseRate*1.4 = 18.2, margin
            // ~4%; Depth > baseDepth = 0.5, large margin).
            for (int i = 0; i < 20; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.RateCpm, Is.GreaterThan(baseRate * 1.4f),
                "Catch-breath must visibly raise the breathing rate at its peak.");
            Assert.That(director.Depth, Is.GreaterThan(baseDepth),
                "Catch-breath must deepen the breath at its peak.");

            // Advance well past the total duration (0.2 + 0.1 + 1.0 = 1.3s) PLUS enough extra
            // time for the bus to close whatever gap remains once the event itself ends (worst
            // case, closing a full-amplitude gap takes well under 0.5s at the 25cpm/s / 0.35
            // depth/s budget) — 160 more ticks brings the total since TriggerEvent to 180 ticks
            // (3.0s), several times the ~1.7s (event duration + bus catch-up) actually needed.
            for (int i = 0; i < 160; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.RateCpm, Is.EqualTo(baseRate).Within(1e-3f),
                "After the event elapses the rate must return exactly to base.");
            Assert.That(director.Depth, Is.EqualTo(baseDepth).Within(1e-3f),
                "After the event elapses the depth must return exactly to base.");
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.None));
        }

        [Test]
        public void SighEvent_DeepensAndSlowsTheBreath()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            Settle(director, emotion, in idle);
            float baseRate = director.RateCpm;
            float baseDepth = director.Depth;

            director.TriggerEvent(BreathEventKind.Sigh);
            // Sigh attack is 1.0s; sample near the peak (~1.1s ≈ 66 ticks, inside the hold).
            for (int i = 0; i < 66; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.RateCpm, Is.LessThan(baseRate),
                "A sigh slows the breathing rate.");
            Assert.That(director.Depth, Is.GreaterThan(baseDepth),
                "A sigh deepens the breath.");
        }

        [Test]
        public void ReducedIntensityEvent_PeaksLowerThanFullIntensity()
        {
            var full = new BreathingDirector();
            var reduced = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            Settle(full, emotion, in idle);
            Settle(reduced, emotion, in idle);

            full.TriggerEvent(BreathEventKind.CatchBreath, 1f);
            reduced.TriggerEvent(BreathEventKind.CatchBreath, 0.5f);
            for (int i = 0; i < 12; i++)
            {
                full.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
                reduced.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
            }

            Assert.That(reduced.RateCpm, Is.GreaterThan(13f),
                "A reduced-intensity catch-breath still lifts the rate above base.");
            Assert.That(reduced.RateCpm, Is.LessThan(full.RateCpm),
                "A reduced-intensity event must peak lower than a full-intensity one.");
        }

        [Test]
        public void TriggerNone_IsIgnored()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            Settle(director, emotion, in idle);
            float baseRate = director.RateCpm;

            director.TriggerEvent(BreathEventKind.None);
            for (int i = 0; i < 12; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.RateCpm, Is.EqualTo(baseRate).Within(1e-3f),
                "Triggering BreathEventKind.None must not modulate the breath.");
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.None));
        }

        [Test]
        public void Reset_ClearsActiveBreathEvent()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
            director.TriggerEvent(BreathEventKind.Sigh);
            director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.Sigh));

            director.Reset();

            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.None));
            Assert.That(director.RateCpm, Is.EqualTo(13f),
                "After Reset the event multipliers return to identity and the rate to resting.");
        }

        [Test]
        public void ActiveBreathEvent_AllocatesNothing()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            Settle(director, emotion, in idle);

            // Warm up with events being armed and advanced.
            for (int i = 0; i < 300; i++)
            {
                if (i % 40 == 0) director.TriggerEvent(BreathEventKind.CatchBreath);
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
            }

            System.GC.Collect();
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 600; i++)
                {
                    if (i % 40 == 0) director.TriggerEvent(BreathEventKind.Sigh);
                    director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
                }
            });

            Assert.That(allocationCount, Is.EqualTo(0L),
                "Arming and advancing breath events must not allocate managed memory.");
        }

        // ── Speech-gap inhale anti-pumping ──────────────────

        private static BodyLanguageStatePolicy SpeakingPolicy() => new()
        {
            State = DialogueState.Speaking,
            BreathRateCpm = 14f,
            BreathDepth = 0.6f,
            BreathIrregularity = 0.2f
        };

        [Test]
        public void ContinuousReleaseStream_NeverSpamsInhales_RespectsRefractory()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            Settle(director, emotion, in speaking);

            // A confident Release pulse every ~0.3s — far faster than the 4.0s normal refractory
            // (retimed to phrase scale) — for 20 seconds (~66 candidate
            // gaps). Simulates a rapid, chattery speaker.
            const float pulseIntervalSeconds = 0.3f;
            const float totalSeconds = 20f;
            int ticksPerPulse = (int)(pulseIntervalSeconds / Dt);
            int totalTicks = (int)(totalSeconds / Dt);

            int armedCount = 0;
            for (int i = 0; i < totalTicks; i++)
            {
                director.Tick(in speaking, emotion, SlewSeconds, Dt, 0f, false);
                if (i % ticksPerPulse == 0)
                {
                    bool armed = director.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 1f, conservativeMode: false);
                    if (armed) armedCount++;
                }
            }

            // Upper bound: refractory (4.0s) + the event's own ~0.75s total duration (lockout)
            // together mean a new inhale can only ever arm roughly every 4.0s at best (the
            // refractory is the binding constraint since 4.0s > the event's own duration), so over
            // 20 seconds at most 5-6 could ever arm (20 / 4.0 = 5, plus the always-eligible first
            // attempt at t=0) — tightened from the old <20 bound (sized for the old 1.5s
            // refractory) to <8, still comfortably above the ~5-6 actually expected but a real,
            // much tighter cap than before, nowhere near the ~66 candidate gaps offered.
            Assert.That(armedCount, Is.LessThan(8),
                $"A continuous stream of confident Release pulses every {pulseIntervalSeconds}s must not " +
                $"spam speech-gap inhales — armed {armedCount} times over {totalSeconds}s of ~66 candidate gaps.");
            Assert.That(armedCount, Is.GreaterThan(0),
                "Sanity: at least one speech-gap inhale must still arm across 20 seconds of confident gaps.");
        }

        [Test]
        public void OnsetAndEmphasisPulses_NeverArmASpeechGapInhale()
        {
            var director = new BreathingDirector();

            Assert.IsFalse(director.TryTriggerSpeechGapInhale(SpeechPulseKind.Onset, 1f, conservativeMode: false),
                "Onset must never arm a speech-gap inhale.");
            Assert.IsFalse(director.TryTriggerSpeechGapInhale(SpeechPulseKind.Emphasis, 1f, conservativeMode: false),
                "Emphasis must never arm a speech-gap inhale.");
            Assert.IsFalse(director.TryTriggerSpeechGapInhale(SpeechPulseKind.Sustain, 1f, conservativeMode: false),
                "Sustain must never arm a speech-gap inhale.");
            Assert.IsFalse(director.TryTriggerSpeechGapInhale(SpeechPulseKind.None, 1f, conservativeMode: false),
                "None must never arm a speech-gap inhale.");
        }

        [Test]
        public void WeakRelease_BelowConfidenceThreshold_DoesNotArm()
        {
            var director = new BreathingDirector();

            bool armed = director.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.03f, conservativeMode: false);

            Assert.IsFalse(armed, "A weak/low-confidence Release pulse must not arm a speech-gap inhale.");
        }

        [Test]
        public void ConfidentRelease_ArmsASpeechGapInhale_AndSetsActiveEvent()
        {
            var director = new BreathingDirector();

            bool armed = director.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.9f, conservativeMode: false);

            Assert.IsTrue(armed, "A confident Release pulse must arm a speech-gap inhale.");
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.SpeechGapInhale));
        }

        [Test]
        public void EnvelopeLockout_ActiveEventBlocksANewSpeechGapInhale()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            Settle(director, emotion, in speaking);

            director.TriggerEvent(BreathEventKind.InhaleBeforeSpeaking);
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.InhaleBeforeSpeaking));

            // A confident Release arriving WHILE another event is still in flight must be
            // refused — a speech-gap inhale must never stack onto or cut off a different event.
            bool armed = director.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 1f, conservativeMode: false);

            Assert.IsFalse(armed, "An active breath event must lock out a new speech-gap inhale.");
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.InhaleBeforeSpeaking),
                "The in-flight event must be unaffected by the refused speech-gap inhale attempt.");
        }

        [Test]
        public void StateEntryEvent_ReplacesAnActiveSpeechGapInhale_NeverStacks()
        {
            // Deferred realism under the speech-coupled breath: the other direction
            // of the lockout. A speech-gap inhale can be in flight during Speaking when
            // the character is interrupted or settles — the state-entry catch-breath/sigh fires via
            // TriggerEvent, which REPLACES the active event rather than compounding a second inhale
            // on top of it. This is what stops a sigh or catch-breath from stacking with a Release
            // inhale into an audible-looking pump on the state transition.
            var director = new BreathingDirector();

            Assert.IsTrue(director.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.9f, conservativeMode: false),
                "Precondition: a confident Release arms a speech-gap inhale.");
            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.SpeechGapInhale));

            director.TriggerEvent(BreathEventKind.Sigh);

            Assert.That(director.ActiveEvent, Is.EqualTo(BreathEventKind.Sigh),
                "A state-entry breath event must take over an active speech-gap inhale, never stack with it.");
        }

        [Test]
        public void ConservativeMode_RequiresHigherConfidenceAndLongerRefractory()
        {
            var director = new BreathingDirector();

            // A strength that clears the normal-mode threshold but not the conservative one must
            // be refused in conservative mode (the statistical-cadence-fallback guard).
            bool armedNormal = director.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.2f, conservativeMode: false);
            Assert.IsTrue(armedNormal, "Sanity: 0.2 strength clears the normal-mode confidence threshold.");

            var conservativeDirector = new BreathingDirector();
            bool armedConservative = conservativeDirector.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.2f, conservativeMode: true);
            Assert.IsFalse(armedConservative,
                "The same 0.2 strength must NOT clear the stricter conservative-mode confidence threshold.");
        }

        [Test]
        public void ConservativeMode_LongerRefractory_ArmsFewerInhalesThanNormalMode()
        {
            var normalDirector = new BreathingDirector();
            var normalEmotion = new EmotionBodyModulator();
            var conservativeDirector = new BreathingDirector();
            var conservativeEmotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            Settle(normalDirector, normalEmotion, in speaking);
            Settle(conservativeDirector, conservativeEmotion, in speaking);

            const float pulseIntervalSeconds = 1.0f; // shorter than both the normal (4.0s) and conservative (6.0s) refractory.
            const float totalSeconds = 30f;
            int ticksPerPulse = (int)(pulseIntervalSeconds / Dt);
            int totalTicks = (int)(totalSeconds / Dt);

            int armedNormalCount = 0;
            int armedConservativeCount = 0;
            for (int i = 0; i < totalTicks; i++)
            {
                normalDirector.Tick(in speaking, normalEmotion, SlewSeconds, Dt, 0f, false);
                conservativeDirector.Tick(in speaking, conservativeEmotion, SlewSeconds, Dt, 0f, false);
                if (i % ticksPerPulse == 0)
                {
                    if (normalDirector.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.9f, conservativeMode: false))
                        armedNormalCount++;
                    if (conservativeDirector.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.9f, conservativeMode: true))
                        armedConservativeCount++;
                }
            }

            Assert.That(armedConservativeCount, Is.LessThan(armedNormalCount),
                "Conservative (statistical-cadence-fallback) mode must arm fewer speech-gap inhales " +
                "than normal mode at the same pulse cadence, due to its longer refractory.");
        }

        [Test]
        public void TryTriggerSpeechGapInhale_AllocatesNothing()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            Settle(director, emotion, in speaking);

            // Warm up with the full pulse mix, including successful and refused arm attempts.
            for (int i = 0; i < 3000; i++)
            {
                director.Tick(in speaking, emotion, SlewSeconds, Dt, 0f, false);
                SpeechPulseKind kind = (i % 4 == 0) ? SpeechPulseKind.Release : SpeechPulseKind.Onset;
                float strength = (i % 3 == 0) ? 0.9f : 0.1f;
                director.TryTriggerSpeechGapInhale(kind, strength, conservativeMode: i % 2 == 0);
            }

            System.GC.Collect();
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 6000; i++)
                {
                    director.Tick(in speaking, emotion, SlewSeconds, Dt, 0f, false);
                    SpeechPulseKind kind = (i % 4 == 0) ? SpeechPulseKind.Release : SpeechPulseKind.Onset;
                    float strength = (i % 3 == 0) ? 0.9f : 0.1f;
                    director.TryTriggerSpeechGapInhale(kind, strength, conservativeMode: i % 2 == 0);
                }
            });

            Assert.That(allocationCount, Is.EqualTo(0L),
                "The pulse-to-breath speech-gap-inhale path must not allocate managed memory.");
        }

        // ── Speech-coupled exhale ─────────────────────────────

        [Test]
        public void SustainedVoicedSpeech_ShallowsDepthByExhaleDepthLoss()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            Settle(director, emotion, in speaking);
            float baseDepth = director.Depth;

            // VoicedFractionTauSeconds is 2.0s (phrase-scale
            // retune), so the old 5s (300-tick) window is no longer enough for _voicedFraction to
            // converge close to 1 (5s is only 2.5 tau; 1-e^-2.5 ~= 0.918, not ~1). Extended to 15s
            // (900 ticks, 7.5 tau; 1-e^-7.5 ~= 0.99945) for equivalent convergence margin. The
            // published Depth (bus-limited) tracks the combined target here with
            // negligible lag regardless — the exhale's own required rate of change (<=0.075
            // depth/s at its steepest, near t=0) stays well under the 0.35 depth/s bus budget, so
            // the bus is never the limiting factor, only the voiced-fraction EMA's own tau is.
            for (int i = 0; i < 900; i++) // 15s at 60fps
                director.Tick(in speaking, emotion, SlewSeconds, Dt, speechEnergy01: 1f, isSpeaking: true);

            Assert.That(director.Depth, Is.LessThan(baseDepth),
                "Sustained voiced speech must visibly shallow the breath depth.");
            Assert.That(director.Depth, Is.EqualTo(baseDepth * 0.75f).Within(0.02f),
                "Depth loss must converge on ~ExhaleDepthLoss (0.25) at full sustained voiced speech.");
        }

        [Test]
        public void Silence_RestoresDepthWithinAFewSeconds()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            Settle(director, emotion, in speaking);
            float baseDepth = director.Depth;

            // Timed against VoicedFractionTauSeconds = 2.0s. 10s (600 ticks,
            // 5 tau) here brings _voicedFraction to ~0.993 before silence begins.
            for (int i = 0; i < 600; i++) // 10s at 60fps
                director.Tick(in speaking, emotion, SlewSeconds, Dt, speechEnergy01: 1f, isSpeaking: true);
            Assert.That(director.Depth, Is.LessThan(baseDepth), "Precondition: voiced speech shallowed the breath.");

            // Needs voicedFraction to decay from ~0.993 to below ~0.067 for Depth to land within
            // 0.01 of baseDepth (0.25 * 0.6 * 0.067 ~= 0.01) — at tau=2.0s that requires t >~
            // 5.4s (ln(0.993/0.067) * 2.0). 8s (480 ticks) gives comfortable margin (voicedFraction
            // ~= 0.0182 at 8s, Depth within ~0.003 of baseDepth).
            for (int i = 0; i < 480; i++) // 8s of silence
                director.Tick(in speaking, emotion, SlewSeconds, Dt, speechEnergy01: 0f, isSpeaking: false);

            Assert.That(director.Depth, Is.EqualTo(baseDepth).Within(0.01f),
                "Silence must restore the breath depth within a few seconds.");
        }

        [Test]
        public void Reset_ClearsVoicedFraction()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            Settle(director, emotion, in speaking);
            for (int i = 0; i < 300; i++)
                director.Tick(in speaking, emotion, SlewSeconds, Dt, speechEnergy01: 1f, isSpeaking: true);
            float shallowedDepth = director.Depth;

            director.Reset();
            director.Tick(in speaking, emotion, SlewSeconds, Dt, 0f, false);

            Assert.That(director.Depth, Is.Not.EqualTo(shallowedDepth),
                "Reset must clear the voiced fraction, restoring the unshallowed depth on the next tick.");
            Assert.That(director.Depth, Is.EqualTo(speaking.BreathDepth).Within(1e-3f));
        }

        // ── Idle macro-cycle depth scale ───────────────────

        // ── Exertion → breath (plan N8) ───────────────────────────────────────

        private sealed class FakeExertionSource : Convai.Domain.Embodiment.Interfaces.IExertionSource
        {
            public float Exertion01 { get; set; }
        }

        [Test]
        public void NoExertionSource_DefaultMultipliers_BaselineUnchanged()
        {
            var baseline = new BreathingDirector();
            var withDefaults = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();

            for (int i = 0; i < 600; i++)
            {
                baseline.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
                // Explicit identity multipliers (as a caller would compute with no
                // IExertionSource registered: exertion01 defaults to 0, so
                // 1 + boost * 0 == 1) must match the no-args overload exactly.
                withDefaults.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false, 1f, 1f, 1f);
            }

            Assert.That(withDefaults.RateCpm, Is.EqualTo(baseline.RateCpm).Within(1e-5f),
                "Identity exertion multipliers (no source registered) must not change the published rate.");
            Assert.That(withDefaults.Depth, Is.EqualTo(baseline.Depth).Within(1e-5f),
                "Identity exertion multipliers (no source registered) must not change the published depth.");
        }

        [Test]
        public void FakeExertionSource_FullExertion_RaisesRateAndDepth()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();
            var exertionSource = new FakeExertionSource { Exertion01 = 1f };

            const float exertionRateBoost = 0.4f;
            const float exertionDepthBoost = 0.5f;

            // Settle at baseline (no exertion) first.
            for (int i = 0; i < 300; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
            float baseRate = director.RateCpm;
            float baseDepth = director.Depth;

            // Mirrors ConvaiBodyLanguageController's resolution: rateMultiplier = 1 + boost * Exertion01.
            float rateMultiplier = 1f + exertionRateBoost * exertionSource.Exertion01;
            float depthMultiplier = 1f + exertionDepthBoost * exertionSource.Exertion01;
            for (int i = 0; i < 600; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false, 1f, rateMultiplier, depthMultiplier);

            Assert.That(director.RateCpm, Is.GreaterThan(baseRate),
                "Full exertion must raise the published breathing rate above baseline.");
            Assert.That(director.Depth, Is.GreaterThan(baseDepth),
                "Full exertion must raise the published breathing depth above baseline.");
            Assert.That(director.RateCpm, Is.EqualTo(baseRate * rateMultiplier).Within(0.05f));
            Assert.That(director.Depth, Is.EqualTo(baseDepth * depthMultiplier).Within(0.01f));
        }

        [Test]
        public void ExertionMultipliers_SlewThroughThePublishBus_NeverSnap()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();

            for (int i = 0; i < 300; i++)
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
            float baseRate = director.RateCpm;

            director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false, 1f, 1.4f, 1.5f);

            Assert.That(director.RateCpm, Is.Not.EqualTo(baseRate * 1.4f).Within(1e-5f),
                "A single tick must not snap the rate straight to the new exertion-scaled target.");
            Assert.That(director.RateCpm, Is.GreaterThan(baseRate),
                "The rate must already be moving toward the higher exertion-scaled target.");
        }

        [Test]
        public void MacroDepthScale_1_12_RaisesSettledDepth_ThroughThePublishBus()
        {
            var baseline = new BreathingDirector();
            var scaled = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            BodyLanguageStatePolicy idle = IdlePolicy();

            for (int i = 0; i < 600; i++)
            {
                baseline.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false); // Default macroDepthScale = 1f.
                scaled.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false, 1.12f);
            }

            Assert.That(scaled.Depth, Is.GreaterThan(baseline.Depth),
                "A 1.12 macroDepthScale must raise the settled depth relative to the default (1f) scale.");
            Assert.That(scaled.Depth, Is.EqualTo(baseline.Depth * 1.12f).Within(0.01f),
                "The settled depth must scale by ~1.12x once both directors have fully settled through the publish bus.");
        }
    }
}
