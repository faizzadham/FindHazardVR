using System.Collections.Generic;
using Convai.Modules.BodyLanguage.Core.Signals;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    public sealed class SpeechPulseAnalyzerTests
    {
        private const float Dt = 1f / 60f;

        private static void RunSteps(
            SpeechPulseAnalyzer analyzer,
            int steps,
            System.Func<int, float> energyAt,
            List<SpeechPulse> pulses,
            float deltaTime = Dt)
        {
            for (int i = 0; i < steps; i++)
                if (analyzer.Step(energyAt(i), deltaTime, out SpeechPulse pulse))
                    pulses.Add(pulse);
        }

        [Test]
        public void Silence_ProducesNoPulses()
        {
            var analyzer = new SpeechPulseAnalyzer();
            var pulses = new List<SpeechPulse>();

            RunSteps(analyzer, 600, _ => 0f, pulses);

            Assert.That(pulses, Is.Empty, "A silent stream must never produce a pulse.");
        }

        [Test]
        public void SyntheticEnvelope_FiresOnsetThenEmphasisThenRelease_InOrder()
        {
            var analyzer = new SpeechPulseAnalyzer();
            var pulses = new List<SpeechPulse>();

            // Ramp 0 -> 0.5 over 1s (slow: no emphasis-worthy derivative).
            RunSteps(analyzer, 60, i => 0.5f * (i + 1) / 60f, pulses);
            // Plateau at 0.5 for 0.5s.
            RunSteps(analyzer, 30, _ => 0.5f, pulses);
            // Sharp burst to 1.0 for 0.1s.
            RunSteps(analyzer, 6, _ => 1.0f, pulses);
            // Back to plateau.
            RunSteps(analyzer, 60, _ => 0.5f, pulses);
            // Decay to silence.
            RunSteps(analyzer, 180, _ => 0f, pulses);

            Assert.That(pulses, Is.Not.Empty, "The synthetic envelope must produce at least one pulse.");

            int onsetIndex = pulses.FindIndex(p => p.Kind == SpeechPulseKind.Onset);
            int emphasisIndex = pulses.FindIndex(p => p.Kind == SpeechPulseKind.Emphasis);
            int releaseIndex = pulses.FindIndex(p => p.Kind == SpeechPulseKind.Release);

            Assert.That(onsetIndex, Is.GreaterThanOrEqualTo(0), "The rise must fire an Onset.");
            Assert.That(emphasisIndex, Is.GreaterThanOrEqualTo(0), "The sharp burst must fire an Emphasis.");
            Assert.That(releaseIndex, Is.GreaterThanOrEqualTo(0), "The decay must fire a Release.");

            Assert.That(onsetIndex, Is.LessThan(emphasisIndex),
                "Onset must fire before any Emphasis.");
            Assert.That(emphasisIndex, Is.LessThan(releaseIndex),
                "Emphasis (from the burst) must fire before the Release (from the decay).");

            int onsetCount = pulses.FindAll(p => p.Kind == SpeechPulseKind.Onset).Count;
            int releaseCount = pulses.FindAll(p => p.Kind == SpeechPulseKind.Release).Count;
            Assert.That(onsetCount, Is.EqualTo(1), "Exactly one Onset must fire for a single rise.");
            Assert.That(releaseCount, Is.EqualTo(1), "Exactly one Release must fire for a single decay.");
        }

        [Test]
        public void TwoBurstsWithinRefractoryWindow_FireOnlyOneEmphasis()
        {
            var analyzer = new SpeechPulseAnalyzer();
            var pulses = new List<SpeechPulse>();

            RunSteps(analyzer, 60, i => 0.5f * (i + 1) / 60f, pulses); // ramp -> active
            RunSteps(analyzer, 30, _ => 0.5f, pulses); // settle
            RunSteps(analyzer, 6, _ => 1.0f, pulses); // burst 1
            RunSteps(analyzer, 3, _ => 0.5f, pulses); // ~0.05s gap: inside the 0.22s refractory
            RunSteps(analyzer, 6, _ => 1.0f, pulses); // burst 2
            RunSteps(analyzer, 60, _ => 0.5f, pulses);

            int emphasisCount = pulses.FindAll(p => p.Kind == SpeechPulseKind.Emphasis).Count;
            Assert.That(emphasisCount, Is.EqualTo(1),
                "Two bursts inside the refractory window must collapse into a single Emphasis.");
        }

        [Test]
        public void TwoBurstsSpacedBeyondRefractoryWindow_FireTwoEmphases()
        {
            var analyzer = new SpeechPulseAnalyzer();
            var pulses = new List<SpeechPulse>();

            RunSteps(analyzer, 60, i => 0.5f * (i + 1) / 60f, pulses); // ramp -> active
            RunSteps(analyzer, 30, _ => 0.5f, pulses); // settle
            RunSteps(analyzer, 6, _ => 1.0f, pulses); // burst 1
            RunSteps(analyzer, 20, _ => 0.5f, pulses); // ~0.33s gap: beyond the 0.22s refractory
            RunSteps(analyzer, 6, _ => 1.0f, pulses); // burst 2
            RunSteps(analyzer, 60, _ => 0.5f, pulses);

            int emphasisCount = pulses.FindAll(p => p.Kind == SpeechPulseKind.Emphasis).Count;
            Assert.That(emphasisCount, Is.EqualTo(2),
                "Two bursts spaced beyond the refractory window must each fire their own Emphasis.");
        }

        [Test]
        public void Determinism_SameSequence_ProducesIdenticalPulses()
        {
            List<SpeechPulse> Run(SpeechPulseAnalyzer analyzer)
            {
                var pulses = new List<SpeechPulse>();
                RunSteps(analyzer, 60, _ => 0f, pulses);
                RunSteps(analyzer, 60, i => 0.5f * (i + 1) / 60f, pulses);
                RunSteps(analyzer, 30, _ => 0.5f, pulses);
                RunSteps(analyzer, 6, _ => 1.0f, pulses);
                RunSteps(analyzer, 3, _ => 0.5f, pulses);
                RunSteps(analyzer, 6, _ => 1.0f, pulses);
                RunSteps(analyzer, 60, _ => 0.5f, pulses);
                RunSteps(analyzer, 180, _ => 0f, pulses);
                return pulses;
            }

            var analyzerA = new SpeechPulseAnalyzer();
            var analyzerB = new SpeechPulseAnalyzer();

            List<SpeechPulse> pulsesA = Run(analyzerA);
            List<SpeechPulse> pulsesB = Run(analyzerB);

            // A third pass reusing the first instance after an explicit Reset() must also match.
            analyzerA.Reset();
            List<SpeechPulse> pulsesAReset = Run(analyzerA);

            Assert.That(pulsesA.Count, Is.EqualTo(pulsesB.Count));
            Assert.That(pulsesA.Count, Is.EqualTo(pulsesAReset.Count));

            for (int i = 0; i < pulsesA.Count; i++)
            {
                Assert.That(pulsesA[i].Kind, Is.EqualTo(pulsesB[i].Kind),
                    $"Pulse #{i} kind mismatch between two fresh analyzer instances.");
                Assert.That(pulsesA[i].Time, Is.EqualTo(pulsesB[i].Time),
                    $"Pulse #{i} time mismatch between two fresh analyzer instances.");

                Assert.That(pulsesA[i].Kind, Is.EqualTo(pulsesAReset[i].Kind),
                    $"Pulse #{i} kind mismatch after Reset().");
                Assert.That(pulsesA[i].Time, Is.EqualTo(pulsesAReset[i].Time),
                    $"Pulse #{i} time mismatch after Reset().");
            }
        }

        [Test]
        public void GentleSlowRise_FiresOnsetButNoEmphasis()
        {
            var analyzer = new SpeechPulseAnalyzer();
            var pulses = new List<SpeechPulse>();

            // A slow 2-second rise to a modest 0.3: crosses threshold but the derivative
            // never comes close to the emphasis threshold.
            RunSteps(analyzer, 120, i => 0.3f * (i + 1) / 120f, pulses);

            bool hasOnset = pulses.Exists(p => p.Kind == SpeechPulseKind.Onset);
            bool hasEmphasis = pulses.Exists(p => p.Kind == SpeechPulseKind.Emphasis);

            Assert.That(hasOnset, Is.True, "A gentle rise crossing the adaptive threshold must still fire an Onset.");
            Assert.That(hasEmphasis, Is.False, "A gentle rise must never fire an Emphasis.");
        }

        [Test]
        public void NonPositiveDeltaTime_ProducesNoPulsesAndNoNaN()
        {
            var analyzer = new SpeechPulseAnalyzer();

            bool zeroFired = analyzer.Step(0.5f, 0f, out SpeechPulse zeroPulse);
            bool negativeFired = analyzer.Step(0.5f, -0.1f, out SpeechPulse negativePulse);

            Assert.That(zeroFired, Is.False, "deltaTime == 0 must never fire a pulse.");
            Assert.That(negativeFired, Is.False, "Negative deltaTime must never fire a pulse.");
            Assert.That(zeroPulse.Kind, Is.EqualTo(SpeechPulseKind.None));
            Assert.That(negativePulse.Kind, Is.EqualTo(SpeechPulseKind.None));

            Assert.That(float.IsNaN(analyzer.Envelope), Is.False, "Envelope must never become NaN.");
            Assert.That(float.IsNaN(analyzer.Baseline), Is.False, "Baseline must never become NaN.");
            Assert.That(float.IsNaN(analyzer.Time), Is.False, "Time must never become NaN.");

            // A normal step afterwards must behave exactly as if the bad calls never happened:
            // no exception, no NaN. Whether it happens to fire a pulse is incidental here.
            analyzer.Step(0.5f, Dt, out SpeechPulse normalPulse);
            Assert.That(float.IsNaN(analyzer.Envelope), Is.False);
            Assert.That(float.IsNaN(normalPulse.Strength), Is.False);
        }
    }
}
