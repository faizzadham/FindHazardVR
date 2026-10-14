using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Modules.BodyLanguage.Core.Signals;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="GesticulationDirector" />: fast-channel scheduling determinism,
    ///     anti-metronome variance, refractory, suppression obedience, channel separation
    ///     (semantic never fires from energy), refusal fallback, and the no-provider statistical
    ///     cadence.
    /// </summary>
    public sealed class GesticulationDirectorTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>Spy performer used by the semantic-channel tests — never called by the fast channel.</summary>
        private sealed class SpyPerformer : IConversationalGesturePerformer
        {
            public int TryPerformCallCount { get; private set; }
            public bool AcceptNextCall = true;
            public GestureSuppression CurrentSuppression { get; set; } = GestureSuppression.None;

            public event System.Action<GestureCue, GesturePerformanceResult> Completed;

            public bool TryPerform(in GestureCue cue)
            {
                TryPerformCallCount++;
                return AcceptNextCall;
            }

            public void RaiseCompleted(GestureCue cue, GesturePerformanceResult result) => Completed?.Invoke(cue, result);
        }

        private static SpeechPulse Emphasis(float strength = 1f) => new(SpeechPulseKind.Emphasis, strength, 0f);
        private static SpeechPulse NoPulse => default;

        private static void TickFastChannelOnly(
            GesticulationDirector director,
            DialogueState state,
            in SpeechPulse pulse,
            bool hasProvider = true,
            GestureSuppression suppression = GestureSuppression.None,
            float beatMinIntervalSeconds = 0.6f,
            float beatIntervalVarianceSeconds = 0.35f)
        {
            director.Tick(
                state,
                gesticulationEnabled: true,
                gesticulationIntensity: 1f,
                in pulse,
                hasSpeechEnergyProvider: hasProvider,
                suppression: suppression,
                gestureIntensityScale: 1f,
                gestureRateScale: 1f,
                deltaTime: Dt,
                beatMinIntervalSeconds: beatMinIntervalSeconds,
                beatIntervalVarianceSeconds: beatIntervalVarianceSeconds,
                beatHeadIntensity: 0.35f,
                posturePulseAmplitude: 0.3f,
                posturePulseAttackSeconds: 0.08f,
                posturePulseDecaySeconds: 0.35f,
                energyToIntensityGain: 1f,
                statisticalCadenceIntervalSeconds: 2.5f,
                statisticalCadenceVarianceSeconds: 1f,
                upperBodySuppressionWeight: 0.4f,
                trace: null);
        }

        [Test]
        public void EmphasisPulse_WhileSpeaking_RequestsHeadBeat()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            TickFastChannelOnly(director, DialogueState.Speaking, Emphasis());

            Assert.IsTrue(director.WantsHeadBeat, "An Emphasis pulse while Speaking must request a head-beat.");
            Assert.That(director.HeadBeatIntensity, Is.GreaterThan(0f));
        }

        [Test]
        public void NotSpeakingOrReacting_NeverRequestsBeat_EvenWithEmphasisPulses()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            foreach (DialogueState state in new[]
                     {
                         DialogueState.Idle, DialogueState.Attending, DialogueState.Listening,
                         DialogueState.Thinking, DialogueState.Interrupted, DialogueState.Settling
                     })
            {
                TickFastChannelOnly(director, state, Emphasis());
                Assert.IsFalse(director.WantsHeadBeat, $"{state} must never trigger a fast-channel beat.");
            }
        }

        [Test]
        public void Reacting_WithEmphasisPulse_AlsoRequestsBeat()
        {
            // The fast channel fires on an Emphasis pulse in both Speaking and Reacting; the
            // per-state policy table's shorthand does not narrow that — see the class remarks on
            // GesticulationDirector for the full resolution.
            var director = new GesticulationDirector();
            director.Seed(1);

            TickFastChannelOnly(director, DialogueState.Reacting, Emphasis());

            Assert.IsTrue(director.WantsHeadBeat, "Reacting with live emphasis must also be fast-channel eligible.");
        }

        [Test]
        public void ClearPosturePulse_ZeroesActivePulse_AndKeepsItZero()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            TickFastChannelOnly(director, DialogueState.Speaking, Emphasis());
            TickFastChannelOnly(director, DialogueState.Speaking, NoPulse);
            Assert.That(director.PosturePulseValue, Is.GreaterThan(0f),
                "A fired beat must produce a rising posture pulse.");

            director.ClearPosturePulse();
            Assert.That(director.PosturePulseValue, Is.EqualTo(0f),
                "ClearPosturePulse must zero the in-flight posture pulse immediately.");

            // A subsequent tick with no new beat must keep it at zero (envelope not reactivated).
            TickFastChannelOnly(director, DialogueState.Interrupted, NoPulse);
            Assert.That(director.PosturePulseValue, Is.EqualTo(0f),
                "After a clear, the posture pulse stays zero until a new beat fires.");
        }

        [Test]
        public void Refractory_PreventsTwoBeatsCloserThanMinInterval()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            // Zero variance so the refractory window is deterministic.
            TickFastChannelOnly(director, DialogueState.Speaking, Emphasis(), beatMinIntervalSeconds: 0.6f, beatIntervalVarianceSeconds: 0f);
            Assert.IsTrue(director.WantsHeadBeat, "First emphasis must fire.");

            int beatsWithinRefractory = 0;
            int ticksInWindow = (int)(0.6f / Dt) - 2; // stay inside the refractory window
            for (int i = 0; i < ticksInWindow; i++)
            {
                TickFastChannelOnly(director, DialogueState.Speaking, Emphasis(), beatMinIntervalSeconds: 0.6f, beatIntervalVarianceSeconds: 0f);
                if (director.WantsHeadBeat) beatsWithinRefractory++;
            }

            Assert.That(beatsWithinRefractory, Is.EqualTo(0),
                "No second beat may fire before the minimum interval elapses.");
        }

        [Test]
        public void Determinism_SameSeedAndPulseSequence_ProducesIdenticalBeatSchedule()
        {
            var directorA = new GesticulationDirector();
            directorA.Seed(4242);
            var directorB = new GesticulationDirector();
            directorB.Seed(4242);

            var beatTicksA = new List<int>();
            var beatTicksB = new List<int>();

            for (int i = 0; i < 600; i++)
            {
                // Fire an emphasis pulse every 20 ticks — frequent enough that the refractory +
                // variance actually gates most of them, exercising the seeded jitter.
                SpeechPulse pulse = (i % 20 == 0) ? Emphasis() : NoPulse;

                TickFastChannelOnly(directorA, DialogueState.Speaking, pulse, beatIntervalVarianceSeconds: 0.4f);
                TickFastChannelOnly(directorB, DialogueState.Speaking, pulse, beatIntervalVarianceSeconds: 0.4f);

                if (directorA.WantsHeadBeat) beatTicksA.Add(i);
                if (directorB.WantsHeadBeat) beatTicksB.Add(i);
            }

            Assert.That(beatTicksA, Is.EqualTo(beatTicksB),
                "Identical seed + pulse sequence must produce an identical beat schedule.");
        }

        [Test]
        public void AntiMetronomeVariance_InterBeatIntervalsAreNotConstant()
        {
            var director = new GesticulationDirector();
            director.Seed(99);

            var beatTicks = new List<int>();
            for (int i = 0; i < 3000; i++)
            {
                SpeechPulse pulse = (i % 15 == 0) ? Emphasis() : NoPulse;
                TickFastChannelOnly(director, DialogueState.Speaking, pulse, beatIntervalVarianceSeconds: 0.35f);
                if (director.WantsHeadBeat) beatTicks.Add(i);
            }

            Assert.That(beatTicks.Count, Is.GreaterThan(3), "Sanity: enough beats must have fired to measure spacing.");

            var intervals = new List<float>();
            for (int i = 1; i < beatTicks.Count; i++)
                intervals.Add(beatTicks[i] - beatTicks[i - 1]);

            float mean = 0f;
            foreach (float v in intervals) mean += v;
            mean /= intervals.Count;

            float variance = 0f;
            foreach (float v in intervals) variance += (v - mean) * (v - mean);
            variance /= intervals.Count;
            float stdDev = (float)System.Math.Sqrt(variance);

            float coefficientOfVariation = mean > 0f ? stdDev / mean : 0f;

            // A perfectly constant (metronomic) schedule has CoV == 0. 0.05 is comfortably above
            // floating-point noise but well below what a genuinely jittered schedule produces —
            // enough to distinguish "has jitter" from "is constant" without being flaky.
            Assert.That(coefficientOfVariation, Is.GreaterThan(0.05f),
                $"Inter-beat intervals must vary (anti-metronome); measured CoV={coefficientOfVariation:0.000}.");
        }

        [Test]
        public void BeatThinning_FiredFractionIsInExpectedRange_AndNeverViolatesMinInterval()
        {
            // Spacing chosen so an eligible pulse's own refractory (1.2s, 's new
            // default) has always fully drained by the time the NEXT eligible pulse arrives —
            // isolating the ~55% thinning draw as the only gate, so the measured fired fraction
            // reflects BeatThinningProbability directly, not a refractory-starved undercount.
            var director = new GesticulationDirector();
            director.Seed(31337);

            const float beatMinIntervalSeconds = 1.2f;
            const int pulseSpacingTicks = 80; // 80 * (1/60)s ≈ 1.333s > 1.2s beatMinIntervalSeconds
            const int eligiblePulseCount = 120;

            int firedCount = 0;
            int lastFireTick = -1;
            float minObservedIntervalSeconds = float.MaxValue;
            int tick = 0;

            for (int p = 0; p < eligiblePulseCount; p++)
            {
                for (int i = 0; i < pulseSpacingTicks; i++)
                {
                    SpeechPulse pulse = i == 0 ? Emphasis() : NoPulse;
                    TickFastChannelOnly(director, DialogueState.Speaking, pulse,
                        beatMinIntervalSeconds: beatMinIntervalSeconds, beatIntervalVarianceSeconds: 0f);

                    if (director.WantsHeadBeat)
                    {
                        firedCount++;
                        if (lastFireTick >= 0)
                            minObservedIntervalSeconds = Mathf.Min(minObservedIntervalSeconds, (tick - lastFireTick) * Dt);
                        lastFireTick = tick;
                    }
                    tick++;
                }
            }

            float firedFraction = (float)firedCount / eligiblePulseCount;
            Assert.That(firedFraction, Is.InRange(0.40f, 0.70f),
                $"Beat thinning must fire roughly half of eligible pulses (~55% target); measured fraction={firedFraction:0.000} over {eligiblePulseCount} pulses.");

            if (minObservedIntervalSeconds < float.MaxValue)
                Assert.That(minObservedIntervalSeconds, Is.GreaterThanOrEqualTo(beatMinIntervalSeconds - 1e-3f),
                    "Consecutive fires must never violate the minimum beat interval.");
        }

        // ── Phrase-end nod ─────────────────────────────────

        [Test]
        public void ReleasePulse_StrongEnough_RequestsPhraseEndNod_At06xIntensity()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            var releasePulse = new SpeechPulse(SpeechPulseKind.Release, 1f, 0f);
            TickFastChannelOnly(director, DialogueState.Speaking, releasePulse,
                beatMinIntervalSeconds: 1.2f, beatIntervalVarianceSeconds: 0f);

            Assert.IsTrue(director.WantsPhraseEndNod, "A strong Release pulse must request a phrase-end nod.");
            Assert.IsFalse(director.WantsHeadBeat, "A phrase-end nod is a distinct request from an ordinary beat, not both in the same tick.");
            Assert.That(director.PhraseEndNodIntensity, Is.EqualTo(0.35f * 1f * 0.6f).Within(1e-4f),
                "Phrase-end nod intensity must be 0.6x the equivalent normal-beat amplitude.");
        }

        [Test]
        public void ReleasePulse_TooWeak_DoesNotRequestPhraseEndNod()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            var weakRelease = new SpeechPulse(SpeechPulseKind.Release, 0.1f, 0f);
            TickFastChannelOnly(director, DialogueState.Speaking, weakRelease,
                beatMinIntervalSeconds: 1.2f, beatIntervalVarianceSeconds: 0f);

            Assert.IsFalse(director.WantsPhraseEndNod, "A Release pulse below the strength floor must not request a phrase-end nod.");
        }

        [Test]
        public void PhraseEndNod_SharesBeatRefractory_WithOrdinaryBeats()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            var releasePulse = new SpeechPulse(SpeechPulseKind.Release, 1f, 0f);
            TickFastChannelOnly(director, DialogueState.Speaking, releasePulse,
                beatMinIntervalSeconds: 1.2f, beatIntervalVarianceSeconds: 0f);
            Assert.IsTrue(director.WantsPhraseEndNod, "Sanity: the first Release must have armed the phrase-end nod.");

            TickFastChannelOnly(director, DialogueState.Speaking, Emphasis(),
                beatMinIntervalSeconds: 1.2f, beatIntervalVarianceSeconds: 0f);
            Assert.IsFalse(director.WantsHeadBeat,
                "The phrase-end nod must consume the same beat refractory an ordinary beat would.");
        }

        [Test]
        public void Suppression_FullBody_SuppressesFastChannelEntirely()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            TickFastChannelOnly(director, DialogueState.Speaking, Emphasis(), suppression: GestureSuppression.FullBody);

            Assert.IsFalse(director.WantsHeadBeat, "FullBody suppression must produce zero fast-channel beats.");
            Assert.That(director.PosturePulseValue, Is.EqualTo(0f), "FullBody suppression must produce zero posture pulse.");
        }

        [Test]
        public void Suppression_UpperBody_DoesNotAttenuateHeadBeatIntensity()
        {
            // UpperBody suppression means "posture reduced, breath stays" — the
            // head-beat is a head-channel gesture composed by Gaze over the talk clip, not a
            // posture output, so it must read identically to an unsuppressed beat of the same
            // pulse. See GesticulationDirector.FireBeat remarks.
            var directorFull = new GesticulationDirector();
            directorFull.Seed(1);
            var directorReduced = new GesticulationDirector();
            directorReduced.Seed(1);

            TickFastChannelOnly(directorFull, DialogueState.Speaking, Emphasis(), suppression: GestureSuppression.None);
            TickFastChannelOnly(directorReduced, DialogueState.Speaking, Emphasis(), suppression: GestureSuppression.UpperBody);

            Assert.IsTrue(directorFull.WantsHeadBeat);
            Assert.IsTrue(directorReduced.WantsHeadBeat, "UpperBody suppression must still allow a beat.");
            Assert.That(directorReduced.HeadBeatIntensity, Is.EqualTo(directorFull.HeadBeatIntensity).Within(1e-5f),
                "UpperBody suppression must not attenuate head-beat intensity — only posture is suppressed.");
        }

        [Test]
        public void Suppression_UpperBody_StillReducesPosturePulseValue()
        {
            // Companion to the head-beat test above: the POSTURE-pulse (folded into the
            // continuous lean target) must still be reduced by upperBodySuppressionWeight even
            // though the head-beat is not. The pulse is triggered on tick 1 (FireBeat sets the
            // envelope's intensity/active flag) and its risen value is only reflected by
            // TickPosturePulseEnvelope on the FOLLOWING tick, so a second (no-pulse) tick is
            // needed to observe PosturePulseValue > 0.
            var directorFull = new GesticulationDirector();
            directorFull.Seed(1);
            var directorReduced = new GesticulationDirector();
            directorReduced.Seed(1);

            TickFastChannelOnly(directorFull, DialogueState.Speaking, Emphasis(), suppression: GestureSuppression.None);
            TickFastChannelOnly(directorReduced, DialogueState.Speaking, Emphasis(), suppression: GestureSuppression.UpperBody);

            TickFastChannelOnly(directorFull, DialogueState.Speaking, NoPulse, suppression: GestureSuppression.None);
            TickFastChannelOnly(directorReduced, DialogueState.Speaking, NoPulse, suppression: GestureSuppression.UpperBody);

            Assert.That(directorFull.PosturePulseValue, Is.GreaterThan(0f), "Sanity: the unsuppressed posture pulse must have risen.");
            Assert.That(directorReduced.PosturePulseValue, Is.GreaterThan(0f),
                "UpperBody suppression must still allow a (reduced) posture pulse.");
            Assert.That(directorReduced.PosturePulseValue, Is.LessThan(directorFull.PosturePulseValue),
                "UpperBody suppression must scale the posture-pulse down, not leave it unaffected.");
        }

        [Test]
        public void Suppression_FullBody_SemanticChannelRefusesLocally_WithoutCallingPerformer()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            bool accepted = director.TryEmitCue(
                new GestureCue(GestureCueKind.Affirmative, 1f), spy, GestureSuppression.FullBody,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsFalse(accepted);
            Assert.That(spy.TryPerformCallCount, Is.EqualTo(0),
                "FullBody suppression must refuse locally without ever calling TryPerform.");
        }

        [Test]
        public void Suppression_UpperBody_SemanticChannelRefusesLocally_WithoutCallingPerformer()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            bool accepted = director.TryEmitCue(
                new GestureCue(GestureCueKind.Affirmative, 1f), spy, GestureSuppression.UpperBody,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsFalse(accepted);
            Assert.That(spy.TryPerformCallCount, Is.EqualTo(0),
                "UpperBody suppression must also refuse locally without calling TryPerform.");
        }

        [Test]
        public void SemanticChannel_NeverInvokedFromFastChannelEnergyTicks()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            // Feed many ticks of pure Emphasis/Onset pulses through the FAST CHANNEL ONLY (the
            // real controller never calls TryEmitCue from EmbodimentTick either) — the spy must
            // never see a single TryPerform call.
            for (int i = 0; i < 1000; i++)
            {
                SpeechPulse pulse = (i % 3 == 0) ? Emphasis() : new SpeechPulse(SpeechPulseKind.Onset, 1f, 0f);
                TickFastChannelOnly(director, DialogueState.Speaking, pulse);
            }

            Assert.That(spy.TryPerformCallCount, Is.EqualTo(0),
                "The fast channel must never call IConversationalGesturePerformer.TryPerform.");
        }

        [Test]
        public void TryEmitCue_None_IsAlwaysRefused()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            bool accepted = director.TryEmitCue(
                GestureCue.None, spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsFalse(accepted);
            Assert.That(spy.TryPerformCallCount, Is.EqualTo(0), "GestureCueKind.None must never reach the performer.");
        }

        [Test]
        public void TryEmitCue_NoPerformer_RefusesAndSubstitutesHeadBeatAndPosturePulse()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            bool accepted = director.TryEmitCue(
                new GestureCue(GestureCueKind.Greeting, 1f), null, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsFalse(accepted);
            Assert.IsTrue(director.WantsHeadBeat, "No performer registered must substitute a head-beat.");
            Assert.IsTrue(director.ProceduralFallbackRequested);
        }

        [Test]
        public void TryEmitCue_PerformerRefuses_SubstitutesHeadBeatAndPosturePulse()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer { AcceptNextCall = false };

            bool accepted = director.TryEmitCue(
                new GestureCue(GestureCueKind.Affirmative, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsFalse(accepted);
            Assert.That(spy.TryPerformCallCount, Is.EqualTo(1), "The performer must have been asked once.");
            Assert.IsTrue(director.WantsHeadBeat, "A refused cue must substitute a head-beat/posture-pulse.");
            Assert.IsTrue(director.ProceduralFallbackRequested);
        }

        [Test]
        public void TryEmitCue_PerformerAccepts_ReportsAcceptedAndDoesNotSubstitute()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer { AcceptNextCall = true };

            bool accepted = director.TryEmitCue(
                new GestureCue(GestureCueKind.Affirmative, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsTrue(accepted);
            Assert.IsFalse(director.WantsHeadBeat, "An accepted cue must not also trigger the refusal-fallback beat.");
            Assert.IsFalse(director.ProceduralFallbackRequested);
        }

        [Test]
        public void SemanticRefractory_PreventsRapidRepeatedEmissions()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            director.TryEmitCue(
                new GestureCue(GestureCueKind.Affirmative, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 2.5f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);
            Assert.That(spy.TryPerformCallCount, Is.EqualTo(1));

            bool secondAccepted = director.TryEmitCue(
                new GestureCue(GestureCueKind.Negative, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 2.5f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            Assert.IsFalse(secondAccepted, "A cue attempted within the semantic refractory must be refused.");
            Assert.That(spy.TryPerformCallCount, Is.EqualTo(1), "The performer must not be called again within the refractory.");
            Assert.IsFalse(director.ProceduralFallbackRequested,
                "A refractory rejection is not a visual refusal and must not start procedural fallback.");
        }

        [Test]
        public void NoSpeechEnergyProvider_StatisticalCadenceFiresBeats_AtClearlyLowerRateThanEnergyDriven()
        {
            var director = new GesticulationDirector();
            director.Seed(7);

            int beatCount = 0;
            const int ticks = 60 * 8; // 8 seconds
            SpeechPulse noPulse = NoPulse;
            for (int i = 0; i < ticks; i++)
            {
                director.Tick(
                    DialogueState.Speaking, gesticulationEnabled: true, gesticulationIntensity: 1f,
                    in noPulse, hasSpeechEnergyProvider: false, suppression: GestureSuppression.None,
                    gestureIntensityScale: 1f, gestureRateScale: 1f, deltaTime: Dt,
                    beatMinIntervalSeconds: 0.6f, beatIntervalVarianceSeconds: 0.35f,
                    beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f,
                    posturePulseAttackSeconds: 0.08f, posturePulseDecaySeconds: 0.35f,
                    energyToIntensityGain: 1f, statisticalCadenceIntervalSeconds: 2.5f,
                    statisticalCadenceVarianceSeconds: 1f, upperBodySuppressionWeight: 0.4f, trace: null);

                Assert.IsTrue(director.IsStatisticalCadenceActive, "With no provider, statistical cadence must be active every tick while Speaking.");
                if (director.WantsHeadBeat) beatCount++;
            }

            // At the busiest energy-driven cadence (beatMinIntervalSeconds=0.6s) 8s could produce
            // up to ~13 beats; statistical cadence at a 2.5s mean should produce far fewer —
            // asserting a generous upper bound proves "clearly lower rate" without being flaky.
            Assert.That(beatCount, Is.GreaterThan(0), "Statistical cadence must actually fire beats over 8 seconds.");
            Assert.That(beatCount, Is.LessThan(8), "Statistical cadence must be clearly slower than the busiest energy-driven cadence.");
        }

        [Test]
        public void SpeechEnergyProviderPresent_ButQuiet_ProducesNoBeats_AndIsNotStatisticalCadence()
        {
            var director = new GesticulationDirector();
            director.Seed(1);

            for (int i = 0; i < 300; i++)
            {
                TickFastChannelOnly(director, DialogueState.Speaking, NoPulse, hasProvider: true);
                Assert.IsFalse(director.IsStatisticalCadenceActive,
                    "A present-but-quiet provider must not be mistaken for the no-provider fallback.");
                Assert.IsFalse(director.WantsHeadBeat, "No pulse must never produce a beat.");
            }
        }

        // ── Shoulder shrug ───────────────────────────────────────

        private static void TickIdle(GesticulationDirector director, float dt)
        {
            SpeechPulse noPulse = NoPulse;
            director.Tick(
                DialogueState.Idle, gesticulationEnabled: false, gesticulationIntensity: 0f,
                in noPulse, hasSpeechEnergyProvider: true, suppression: GestureSuppression.None,
                gestureIntensityScale: 1f, gestureRateScale: 1f, deltaTime: dt,
                beatMinIntervalSeconds: 0.6f, beatIntervalVarianceSeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f,
                posturePulseAttackSeconds: 0.08f, posturePulseDecaySeconds: 0.35f,
                energyToIntensityGain: 1f, statisticalCadenceIntervalSeconds: 2.5f,
                statisticalCadenceVarianceSeconds: 1f, upperBodySuppressionWeight: 0.4f, trace: null);
        }

        [Test]
        public void UncertainCue_TriggersShrug_RisesThenHoldsThenDecaysToZero()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            director.TryEmitCue(
                new GestureCue(GestureCueKind.Uncertain, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            const float dt = 1f / 60f;
            float peak = 0f;
            float valueAtHalfAttack = 0f;
            bool sampledHalfAttack = false;
            for (int i = 0; i < 120; i++) // 2 seconds — attack(0.4) + hold(0.3) + decay(0.8) = 1.5s total
            {
                TickIdle(director, dt);
                peak = Mathf.Max(peak, director.ShrugValue);
                if (!sampledHalfAttack && i == 12) // ~0.2s, mid-attack
                {
                    valueAtHalfAttack = director.ShrugValue;
                    sampledHalfAttack = true;
                }
            }

            Assert.That(valueAtHalfAttack, Is.GreaterThan(0f), "The shrug must rise during its attack phase.");
            Assert.That(peak, Is.EqualTo(1f).Within(1e-4f), "The shrug must reach its full-hold peak.");
            Assert.That(director.ShrugValue, Is.EqualTo(0f).Within(1e-4f),
                "The shrug must have fully decayed back to zero after attack+hold+decay elapses.");
        }

        [Test]
        public void Shrug_C1Endpoints_StartsAndEndsAtZero()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            Assert.That(director.ShrugValue, Is.EqualTo(0f), "No shrug in flight before any Uncertain cue.");

            director.TryEmitCue(
                new GestureCue(GestureCueKind.Uncertain, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            // First tick after the trigger: elapsed is one dt, still very close to the C1 zero
            // endpoint (the envelope starts at 0 and eases in with zero slope).
            TickIdle(director, 1f / 600f); // a tiny dt so the very first sample is near-zero
            Assert.That(director.ShrugValue, Is.LessThan(0.05f));
        }

        [Test]
        public void NonUncertainCue_DoesNotTriggerShrug()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            director.TryEmitCue(
                new GestureCue(GestureCueKind.Affirmative, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            TickIdle(director, 1f / 60f);
            Assert.That(director.ShrugValue, Is.EqualTo(0f));
        }

        [Test]
        public void UncertainCue_ShrugFiresEvenWhenClipRefused()
        {
            // UpperBody suppression refuses the clip locally (no performer call at all), but the
            // shrug is procedural — it still arms.
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            director.TryEmitCue(
                new GestureCue(GestureCueKind.Uncertain, 1f), spy, GestureSuppression.UpperBody,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            TickIdle(director, 0.2f); // inside the attack window
            Assert.That(director.ShrugValue, Is.GreaterThan(0f),
                "The shrug must still rise even though the semantic clip was refused.");
            Assert.That(spy.TryPerformCallCount, Is.EqualTo(0), "UpperBody suppression must refuse locally without calling the performer.");
        }

        [Test]
        public void UncertainCue_Refractory_PreventsASecondShrugWithin6Seconds()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            director.TryEmitCue(
                new GestureCue(GestureCueKind.Uncertain, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            const float dt = 1f / 60f;
            for (int i = 0; i < 90; i++) TickIdle(director, dt); // let the first shrug fully decay (1.5s)
            Assert.That(director.ShrugValue, Is.EqualTo(0f).Within(1e-4f));

            // Second attempt at ~1.5s (well inside the 6s refractory) must be silently ignored.
            director.TryEmitCue(
                new GestureCue(GestureCueKind.Uncertain, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);

            for (int i = 0; i < 30; i++) TickIdle(director, dt); // 0.5s — would be well into an attack if armed
            Assert.That(director.ShrugValue, Is.EqualTo(0f),
                "A second Uncertain cue within the 6s refractory must not re-arm the shrug.");
        }

        [Test]
        public void Reset_ZeroesShrugValue()
        {
            var director = new GesticulationDirector();
            director.Seed(1);
            var spy = new SpyPerformer();

            director.TryEmitCue(
                new GestureCue(GestureCueKind.Uncertain, 1f), spy, GestureSuppression.None,
                gestureIntensityScale: 1f, semanticCueRefractorySeconds: 0f,
                beatHeadIntensity: 0.35f, posturePulseAmplitude: 0.3f, trace: null);
            TickIdle(director, 0.2f);
            Assert.That(director.ShrugValue, Is.GreaterThan(0f), "Sanity: the shrug must be in flight.");

            director.Reset();

            Assert.That(director.ShrugValue, Is.EqualTo(0f), "Reset (controller disable) must zero the shrug immediately.");
        }
    }
}
