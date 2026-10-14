using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Modules.BodyLanguage.Core.Policy;
using Convai.Modules.BodyLanguage.Core.Pose;
using Convai.Modules.BodyLanguage.Core.Signals;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     The breath smoothness gate: a deterministic, POCO-only "worst realistic
    ///     case" regression proving the breath modulation bus actually holds its smoothness
    ///     budget under stress, and that a noisy 60s continuous-speech burst still visibly
    ///     breathes rather than flattening out. No scenes, no Unity APIs beyond
    ///     <see cref="Mathf" />/<see cref="Time" />-free math and the seeded
    ///     <see cref="DeterministicEmbodimentRandom" /> stream, so this runs bit-stably in
    ///     EditMode.
    /// </summary>
    public sealed class BreathSmoothnessBudgetTests
    {
        private const float Dt = 1f / 60f;
        private const float SlewSeconds = 1.5f; // matches ConvaiBodyLanguageProfile.PostureTargetSlewSeconds's default (1.5s), the same value the controller feeds BreathingDirector.Tick.
        private const float MaxChestExpansionDegrees = 4.5f; // ConvaiBodyLanguageProfile.maxBreathChestExpansionDegrees default.
        private const float MaxShoulderLiftDegrees = 2.2f; // ConvaiBodyLanguageProfile.maxBreathShoulderLiftDegrees default (unused by the metrics below; kept realistic).
        private const uint SolverSeed = 0xB7EA7000u; // Same seed for both runs so only the director's driven rate/depth/irregularity differ, isolating the bus's effect.
        private const int SixtySecondsAtSixtyFps = 3600;

        // Global breath smoothness budget under test (BreathingDirector.MaxDepthChangePerSecond /
        // MaxRateChangeCpmPerSecond) — mirrored here since the constants are private.
        private const float MaxDepthChangePerSecond = 0.35f;
        private const float MaxRateChangeCpmPerSecond = 25f;

        // ── Idle policy (matches ConvaiBodyLanguageProfile's default Idle row) ──────────────
        private static BodyLanguageStatePolicy IdlePolicy() => new()
        {
            State = DialogueState.Idle,
            BreathRateCpm = 13f,
            BreathDepth = 0.5f,
            BreathIrregularity = 0.1f
        };

        // ── Speaking policy (matches ConvaiBodyLanguageProfile's default Speaking row) ───────
        private static BodyLanguageStatePolicy SpeakingPolicy() => new()
        {
            State = DialogueState.Speaking,
            BreathRateCpm = 14f,
            BreathDepth = 0.6f,
            BreathIrregularity = 0.2f
        };

        private static BreathSolveInput SolveInput(BreathingDirector director) => new()
        {
            DeltaTime = Dt,
            RateCpm = director.RateCpm,
            Depth = director.Depth,
            Irregularity = director.Irregularity,
            MasterWeight = 1f,
            MaxChestExpansionDegrees = MaxChestExpansionDegrees,
            MaxShoulderLiftDegrees = MaxShoulderLiftDegrees
        };

        /// <summary>
        ///     Runs 60s @ 60fps of Idle (isSpeaking false, energy 0) through
        ///     <see cref="BreathingDirector.Tick" /> + <see cref="BreathSolver.Solve" />,
        ///     recording <see cref="BreathSolver.ChestSagittalDegrees" /> every frame — the
        ///     self-calibrating baseline the speech run's jerk/amplitude metrics are compared
        ///     against.
        /// </summary>
        private static float[] RunIdleBaseline()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            var solver = new BreathSolver();
            solver.Seed(SolverSeed);
            BodyLanguageStatePolicy idle = IdlePolicy();

            var chest = new float[SixtySecondsAtSixtyFps];
            for (int i = 0; i < SixtySecondsAtSixtyFps; i++)
            {
                director.Tick(in idle, emotion, SlewSeconds, Dt, 0f, false);
                solver.Solve(SolveInput(director));
                chest[i] = solver.ChestSagittalDegrees;
            }

            return chest;
        }

        /// <summary>
        ///     Scripted noisy-speech run result: per-frame chest sagittal trace plus the
        ///     frame-to-frame published Depth/RateCpm deltas the smoothness-budget assertion
        ///     needs (recorded starting from the SECOND tick, so the first tick's legitimate
        ///     "unpublished -&gt; first snap" jump is never counted as a budget violation — the
        ///     budget only governs steady-state changes once the bus has already published once).
        /// </summary>
        private readonly struct SpeechRunResult
        {
            public readonly float[] Chest;
            public readonly float MaxAbsDepthDelta;
            public readonly float MaxAbsRateDelta;

            public SpeechRunResult(float[] chest, float maxAbsDepthDelta, float maxAbsRateDelta)
            {
                Chest = chest;
                MaxAbsDepthDelta = maxAbsDepthDelta;
                MaxAbsRateDelta = maxAbsRateDelta;
            }
        }

        /// <summary>
        ///     Runs 60s @ 60fps of scripted noisy continuous speech: a seeded on/off phrase gate
        ///     (~2-5s "on" phrases, ~0.4-0.8s "off" gaps) modulating a syllable-rate energy
        ///     envelope, with a <see cref="BreathEventKind.SpeechGapInhale" /> armed on every
        ///     phrase-gap falling edge and a <see cref="BreathEventKind.InhaleBeforeSpeaking" />
        ///     event fired whenever a gap that turns out to have been &gt;= 1s ends (in this
        ///     script that is only ever the initial pre-utterance silence, since steady-state
        ///     gaps are drawn short — a character does not begin mid-sentence, so the very first
        ///     silence-to-speech transition is a natural "draw breath to start speaking" moment;
        ///     every subsequent short intra-utterance gap is handled by the gap-inhale path
        ///     instead, exactly like real phrase pauses).
        /// </summary>
        private static SpeechRunResult RunNoisySpeech()
        {
            var director = new BreathingDirector();
            var emotion = new EmotionBodyModulator();
            var solver = new BreathSolver();
            solver.Seed(SolverSeed);
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            var gateRandom = new DeterministicEmbodimentRandom(0xC0FFEEu);

            var chest = new float[SixtySecondsAtSixtyFps];
            float maxAbsDepthDelta = 0f;
            float maxAbsRateDelta = 0f;
            float previousDepth = 0f;
            float previousRate = 0f;

            bool gateOn = false;
            float phaseElapsed = 0f;
            float phaseTarget = gateRandom.Range(1.0f, 2.0f); // initial pre-utterance silence, deliberately >=1s.
            float simTime = 0f;

            for (int i = 0; i < SixtySecondsAtSixtyFps; i++)
            {
                phaseElapsed += Dt;
                if (phaseElapsed >= phaseTarget)
                {
                    if (gateOn)
                    {
                        // Falling edge: a phrase just ended, entering a short intra-utterance gap.
                        director.TryTriggerSpeechGapInhale(SpeechPulseKind.Release, 0.4f, conservativeMode: false);
                        gateOn = false;
                        phaseTarget = gateRandom.Range(0.4f, 0.8f);
                    }
                    else
                    {
                        // Rising edge: the just-elapsed gap (phaseElapsed) is the actual gap length.
                        if (phaseElapsed >= 1f)
                            director.TriggerEvent(BreathEventKind.InhaleBeforeSpeaking);
                        gateOn = true;
                        phaseTarget = gateRandom.Range(2f, 5f);
                    }
                    phaseElapsed = 0f;
                }

                float energy = gateOn
                    ? Mathf.Clamp01(0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * 3.3f * simTime))
                    : 0f;

                director.Tick(in speaking, emotion, SlewSeconds, Dt, energy, isSpeaking: true);
                solver.Solve(SolveInput(director));
                chest[i] = solver.ChestSagittalDegrees;

                if (i > 0)
                {
                    maxAbsDepthDelta = Mathf.Max(maxAbsDepthDelta, Mathf.Abs(director.Depth - previousDepth));
                    maxAbsRateDelta = Mathf.Max(maxAbsRateDelta, Mathf.Abs(director.RateCpm - previousRate));
                }
                previousDepth = director.Depth;
                previousRate = director.RateCpm;

                simTime += Dt;
            }

            return new SpeechRunResult(chest, maxAbsDepthDelta, maxAbsRateDelta);
        }

        private static float SecondDifferenceRms(float[] trace)
        {
            double sumSquares = 0.0;
            int count = 0;
            for (int i = 2; i < trace.Length; i++)
            {
                float secondDifference = trace[i] - 2f * trace[i - 1] + trace[i - 2];
                sumSquares += (double)secondDifference * secondDifference;
                count++;
            }
            return count > 0 ? Mathf.Sqrt((float)(sumSquares / count)) : 0f;
        }

        private static float PeakToPeak(float[] trace, int start, int length)
        {
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            int end = start + length;
            for (int i = start; i < end; i++)
            {
                if (trace[i] < min) min = trace[i];
                if (trace[i] > max) max = trace[i];
            }
            return max - min;
        }

        [Test]
        public void SpeechBus_NeverExceedsThePublishedSmoothnessBudget()
        {
            SpeechRunResult speech = RunNoisySpeech();

            // The bus's MoveTowards step is exactly budget*Dt every tick once published (proven
            // by BreathingDirector.PublishBus), so the theoretical max per-frame delta is
            // MaxDepthChangePerSecond/60 = 0.35/60 = 0.0058333 and MaxRateChangeCpmPerSecond/60 =
            // 25/60 = 0.4166667. Small epsilons (1e-4 / 1e-3) absorb float accumulation only —
            // this is not a loosened bound, it is the exact budget plus float slop.
            float maxDepthDeltaAllowed = MaxDepthChangePerSecond / 60f + 1e-4f;
            float maxRateDeltaAllowed = MaxRateChangeCpmPerSecond / 60f + 1e-3f;

            Assert.That(speech.MaxAbsDepthDelta, Is.LessThanOrEqualTo(maxDepthDeltaAllowed),
                $"Published Depth changed by {speech.MaxAbsDepthDelta} in a single frame under stress — " +
                $"exceeds the {MaxDepthChangePerSecond}/s smoothness bus budget.");
            Assert.That(speech.MaxAbsRateDelta, Is.LessThanOrEqualTo(maxRateDeltaAllowed),
                $"Published RateCpm changed by {speech.MaxAbsRateDelta} in a single frame under stress — " +
                $"exceeds the {MaxRateChangeCpmPerSecond}cpm/s smoothness bus budget.");
        }

        [Test]
        public void SpeechRun_JerkStaysWithinThreeTimesTheIdleBaseline()
        {
            float[] idleChest = RunIdleBaseline();
            SpeechRunResult speech = RunNoisySpeech();

            float idleJerkRms = SecondDifferenceRms(idleChest);
            float speechJerkRms = SecondDifferenceRms(speech.Chest);

            // Self-calibrating: no absolute magic number, only relative to
            // this same test's own idle baseline. This is the actual regression the modulation
            // bus exists to prevent — without it, fast per-syllable modulators would feed straight
            // through to ChestSagittalDegrees, producing 1-2 Hz flutter with jerk far above 3x
            // idle; every modulator funnels through the slew-limited bus first, so
            // ChestSagittalDegrees only ever receives smoothly-changing Depth/RateCpm inputs.
            Assert.That(speechJerkRms, Is.LessThanOrEqualTo(idleJerkRms * 3f),
                $"Speech-run chest jerk (RMS second-difference = {speechJerkRms}) exceeds 3x the idle " +
                $"baseline ({idleJerkRms}) — the breath is fluttering under stress instead of reading calm.");
        }

        [Test]
        public void SpeechRun_StillBreathes_EveryTenSecondWindowKeepsAtLeastAThirdOfIdleAmplitude()
        {
            float[] idleChest = RunIdleBaseline();
            SpeechRunResult speech = RunNoisySpeech();

            float idlePeakToPeak = PeakToPeak(idleChest, 0, idleChest.Length);
            float minimumRequired = idlePeakToPeak * 0.3f;

            // Checked as 6 non-overlapping 10s partitions rather than a fully sliding window —
            // a breath cycle here is ~2.6-4.6s (13-23cpm), so each 10s block spans 2-4 full
            // cycles and a boundary-straddling worst case is not materially different from this
            // partition; this keeps the check O(n) with plain loops (no LINQ) per repo convention.
            const int windowTicks = 600; // 10s at 60fps.
            int windowCount = SixtySecondsAtSixtyFps / windowTicks;
            for (int w = 0; w < windowCount; w++)
            {
                float windowPeakToPeak = PeakToPeak(speech.Chest, w * windowTicks, windowTicks);
                Assert.That(windowPeakToPeak, Is.GreaterThanOrEqualTo(minimumRequired),
                    $"Speech-run window {w} ({w * 10}-{w * 10 + 10}s) chest peak-to-peak " +
                    $"({windowPeakToPeak}) fell below 30% of the idle baseline's peak-to-peak " +
                    $"({idlePeakToPeak}) — the bus has over-smoothed the breath to a near-flatline.");
            }
        }
    }
}
