using Convai.Modules.BodyLanguage.Core.Gestures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Pure-math tests for <see cref="HeadGestureProgram" />'s envelopes:
    ///     the new signed single-dip <see cref="HeadGestureProgram.BeatNod" /> co-speech envelope,
    ///     and the retuned <see cref="HeadGestureProgram.Nod" /> double-bob's second-lobe ratio.
    ///     <see cref="HeadGestureDirectorTests" /> covers the director-level consequences (durations,
    ///     amplitude variance, neck lead); this file is scoped to the envelope shapes themselves.
    /// </summary>
    public sealed class HeadGestureProgramTests
    {
        private const int ScanSamples = 2000;

        [Test]
        public void BeatNod_StartsAndEndsAtZero_WithNearZeroDerivative()
        {
            const float h = 1e-3f;

            Assert.That(HeadGestureProgram.BeatNod(0f), Is.EqualTo(0f).Within(1e-5f),
                "BeatNod must start at exactly zero.");
            Assert.That(HeadGestureProgram.BeatNod(1f), Is.EqualTo(0f).Within(1e-5f),
                "BeatNod must end at exactly zero.");

            float derivativeAtStart = (HeadGestureProgram.BeatNod(h) - HeadGestureProgram.BeatNod(0f)) / h;
            float derivativeAtEnd = (HeadGestureProgram.BeatNod(1f) - HeadGestureProgram.BeatNod(1f - h)) / h;

            Assert.That(Mathf.Abs(derivativeAtStart), Is.LessThan(0.5f),
                "BeatNod must ease in with a near-zero derivative at p=0 (C1 endpoint), not jump.");
            Assert.That(Mathf.Abs(derivativeAtEnd), Is.LessThan(0.5f),
                "BeatNod must ease out with a near-zero derivative at p=1 (C1 endpoint), not jump.");
        }

        [Test]
        public void BeatNod_PeaksAtThirtyPercent_WithValueOne()
        {
            float peak = float.NegativeInfinity;
            float peakP = 0f;
            for (int i = 0; i <= ScanSamples; i++)
            {
                float p = (float)i / ScanSamples;
                float v = HeadGestureProgram.BeatNod(p);
                if (v > peak) { peak = v; peakP = p; }
            }

            Assert.That(peakP, Is.EqualTo(0.30f).Within(0.02f), "BeatNod's peak must sit at 30% of the program's phase.");
            Assert.That(peak, Is.EqualTo(1f).Within(0.01f), "BeatNod's peak value must be exactly 1.");
        }

        [Test]
        public void BeatNod_SettlesWithSmallNegativeOvershoot()
        {
            float min = float.PositiveInfinity;
            float minP = 0f;
            for (int i = 0; i <= ScanSamples; i++)
            {
                float p = (float)i / ScanSamples;
                float v = HeadGestureProgram.BeatNod(p);
                if (v < min) { min = v; minP = p; }
            }

            Assert.That(minP, Is.InRange(0.85f, 0.92f), "BeatNod's settle-overshoot minimum must sit in [0.85, 0.92].");
            Assert.That(min, Is.EqualTo(-0.08f).Within(0.02f), "BeatNod's overshoot magnitude must be ~0.08 below zero.");
        }

        [Test]
        public void BeatNod_IsContinuousAcrossItsThreePieces()
        {
            const float step = 1e-3f;
            float maxJump = 0f;
            float previous = HeadGestureProgram.BeatNod(0f);
            for (float p = step; p <= 1f; p += step)
            {
                float current = HeadGestureProgram.BeatNod(p);
                maxJump = Mathf.Max(maxJump, Mathf.Abs(current - previous));
                previous = current;
            }

            Assert.That(maxJump, Is.LessThan(0.02f),
                "BeatNod must be continuous across its three EaseInOutQuad pieces — no adjacent-sample jump at 1e-3 sampling.");
        }

        [Test]
        public void Nod_GlobalPeakIsOne()
        {
            float peak = 0f;
            for (int i = 0; i <= ScanSamples; i++)
            {
                float p = (float)i / ScanSamples;
                peak = Mathf.Max(peak, HeadGestureProgram.Nod(p));
            }

            Assert.That(peak, Is.EqualTo(1f).Within(0.02f),
                "The Nod envelope's first-lobe crest must be normalized to exactly 1.0 ( LobeDecay/LobeNormalization retune).");
        }

        [Test]
        public void Nod_SecondLobePeaksAtFortyFivePercentOfFirst()
        {
            // Nod's lobes(p) = (1-cos(4πp))/2 has period 0.5: the first full up-down lobe spans
            // [0, 0.5), the second [0.5, 1.0) — see the class remarks / LobeNormalization comment
            // for the derivation this test locks in.
            float firstLobePeak = 0f;
            float secondLobePeak = 0f;
            for (int i = 0; i <= ScanSamples; i++)
            {
                float p = (float)i / ScanSamples;
                float v = HeadGestureProgram.Nod(p);
                if (p < 0.5f) firstLobePeak = Mathf.Max(firstLobePeak, v);
                else secondLobePeak = Mathf.Max(secondLobePeak, v);
            }

            float ratio = firstLobePeak > 0f ? secondLobePeak / firstLobePeak : 0f;
            Assert.That(ratio, Is.EqualTo(0.45f).Within(0.05f),
                $"The second lobe must peak at ~45% of the first ( retune, was ~55%); measured ratio={ratio:0.000}.");
        }
    }
}
