using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     Behavior tests for <see cref="OneEuroFilter" />: the whole point of an adaptive
    ///     cut-off is that jitter rejection and tracking lag stop being the same knob, so the two
    ///     are measured against the SAME tuning — the one the gaze target arbiter actually ships —
    ///     rather than each against a setting chosen to make it pass.
    /// </summary>
    /// <remarks>
    ///     The negative controls matter as much as the assertions. A filter that never opens its
    ///     cut-off passes the noise test and fails the ramp; a filter that always opens it passes
    ///     the ramp and fails the noise. Both halves are pinned here with beta forced to zero, so
    ///     these tests cannot be satisfied by a pass-through or by a plain low pass.
    /// </remarks>
    public sealed class OneEuroFilterTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>
        ///     The gaze target arbiter's shipped conditioning tuning, in metres: 1 Hz at rest,
        ///     opening by 5 Hz per metre/second of measured speed, speed estimated through a 1 Hz
        ///     low pass. Mirrored here so the filter's properties are proven at the numbers that
        ///     are actually used, not at a convenient pair.
        /// </summary>
        private static readonly OneEuroTuning Tuning = new(1f, 5f, 1f);

        /// <summary>The same filter with the adaptive term disabled — a plain 1 Hz low pass.</summary>
        private static readonly OneEuroTuning FixedLowPass = new(1f, 0f, 1f);

        [Test]
        public void WhiteNoiseOnAStaticInput_IsReducedTenfoldInVariance()
        {
            float ratio = NoiseVarianceRatio(Tuning, out float inputVariance);

            Assert.That(inputVariance, Is.GreaterThan(0f), "The noise source must actually be noisy.");
            Assert.That(ratio, Is.GreaterThan(10f),
                "A still target carrying 5 mm of bone noise must come out at least ten times " +
                $"quieter in variance; measured {ratio:0.0}x.");
        }

        [Test]
        public void RampAtOneMetrePerSecond_LagsUnderThreeCentimetres()
        {
            float lag = SteadyStateRampLag(Tuning);

            Assert.That(lag, Is.LessThan(0.03f),
                "Someone walking must be tracked, not trailed: at 1 m/s the filter may sit no " +
                $"more than 3 cm behind once the speed estimate has settled; measured {lag * 100f:0.0} cm.");
        }

        [Test]
        public void FixedCutoff_TradesTheOneAgainstTheOther_SoTheAdaptiveTermIsLoadBearing()
        {
            // Negative control. With beta at zero the filter is a plain 1 Hz low pass: it wins
            // the noise test by more and loses the ramp test outright. If a future change makes
            // the adaptive term inert, THIS is the test that says so.
            float fixedRatio = NoiseVarianceRatio(FixedLowPass, out _);
            float fixedLag = SteadyStateRampLag(FixedLowPass);

            Assert.That(fixedRatio, Is.GreaterThan(NoiseVarianceRatio(Tuning, out _)),
                "A fixed cut-off should reject MORE jitter — that is never what was in doubt.");
            Assert.That(fixedLag, Is.GreaterThan(0.03f),
                "and should fail the ramp, which is the lag the beta term exists to buy back; " +
                $"measured {fixedLag * 100f:0.0} cm.");
        }

        [Test]
        public void Step_ConvergesWithinThreeTenthsOfASecond()
        {
            var filter = new OneEuroFilter();
            filter.Step(0f, in Tuning, Dt);

            float settled = float.PositiveInfinity;
            for (int i = 1; i <= 60; i++)
            {
                float value = filter.Step(1f, in Tuning, Dt);
                if (Mathf.Abs(value - 1f) > 0.02f) continue;

                settled = i * Dt;
                break;
            }

            Assert.That(settled, Is.LessThan(0.3f),
                "A step is a discontinuity, which is maximal speed, which opens the cut-off: " +
                $"reaching 98% must take well under a third of a second; measured {settled:0.000} s.");
        }

        [Test]
        public void Reset_MakesTheNextOutputExactlyTheValue()
        {
            var filter = new OneEuroFilter();
            for (int i = 0; i < 60; i++) filter.Step(3f, in Tuning, Dt);

            filter.Reset(-7.5f);

            Assert.That(filter.Current, Is.EqualTo(-7.5f), "Reset re-states the value immediately.");
            Assert.That(filter.Step(-7.5f, in Tuning, Dt), Is.EqualTo(-7.5f),
                "A teleport must arrive whole: the step after a reset carries no trace of the " +
                "history it jumped from.");
            Assert.That(filter.Speed, Is.EqualTo(0f),
                "and no speed either, or the jump would go on driving the cut-off open.");
        }

        [Test]
        public void Reset_IsASeedAndNotABypass()
        {
            var filter = new OneEuroFilter();
            filter.Reset(0f);

            // A centimetre of movement, i.e. a speed that leaves the cut-off near its floor.
            float first = filter.Step(0.01f, in Tuning, Dt);

            Assert.That(first, Is.GreaterThan(0f).And.LessThan(0.005f),
                "Filtering resumes from the seeded value — the input is followed, at the filter's " +
                "own rate. A reset that also skipped the next step would turn a caller that " +
                "resets on the wrong condition into a pass-through, silently.");
        }

        [Test]
        public void ZeroDeltaTime_HoldsTheValue()
        {
            var filter = new OneEuroFilter();
            filter.Step(2f, in Tuning, Dt);

            Assert.That(filter.Step(9f, in Tuning, 0f), Is.EqualTo(2f));
            Assert.That(filter.Step(9f, in Tuning, -1f), Is.EqualTo(2f));
        }

        [Test]
        public void FrameRateIndependence_SteadyStateLagIsTheSameAtEveryRate()
        {
            // The alpha derivation plus differencing the INPUT rather than the output is what
            // makes this true, and neither is visible in the output — so it is measured. A
            // 1 m/s ramp must sit the same distance behind at 30 Hz as at 240 Hz.
            float slow = SteadyStateRampLag(Tuning, 1f / 30f);
            float middle = SteadyStateRampLag(Tuning, 1f / 60f);
            float fast = SteadyStateRampLag(Tuning, 1f / 240f);

            Assert.That(slow, Is.EqualTo(fast).Within(0.001f),
                $"30 Hz lags {slow * 100f:0.00} cm, 240 Hz lags {fast * 100f:0.00} cm.");
            Assert.That(middle, Is.EqualTo(fast).Within(0.001f),
                $"60 Hz lags {middle * 100f:0.00} cm, 240 Hz lags {fast * 100f:0.00} cm.");
        }

        [Test]
        public void Vector3Filter_FiltersEachAxisIndependently()
        {
            var filter = new OneEuroVector3Filter();
            var scalar = new OneEuroFilter();

            float x = 0f;
            Vector3 filtered = Vector3.zero;
            float filteredX = 0f;

            for (int i = 0; i < 120; i++)
            {
                x += 1f * Dt;
                filtered = filter.Step(new Vector3(x, 1.6f, 2f), in Tuning, Dt);
                filteredX = scalar.Step(x, in Tuning, Dt);
            }

            Assert.That(filtered.x, Is.EqualTo(filteredX).Within(1e-5f),
                "The moving axis must behave exactly as the scalar filter does.");
            Assert.That(filtered.y, Is.EqualTo(1.6f).Within(1e-5f));
            Assert.That(filtered.z, Is.EqualTo(2f).Within(1e-5f),
                "A still axis must not be dragged by a moving one — that is what per-axis buys.");
            Assert.That(filter.Current, Is.EqualTo(filtered));
        }

        [Test]
        public void Vector3Reset_SeedsEveryAxis()
        {
            var filter = new OneEuroVector3Filter();
            for (int i = 0; i < 30; i++) filter.Step(new Vector3(1f, 2f, 3f), in Tuning, Dt);

            var teleport = new Vector3(-4f, 0.5f, 12f);
            filter.Reset(teleport);

            Assert.That(filter.Step(teleport, in Tuning, Dt), Is.EqualTo(teleport));
        }

        // ------------------------------------------------------------------ measurements

        /// <summary>
        ///     Input variance over output variance for a static input carrying 5 mm of white
        ///     noise — a talking character's head bone, roughly. Deterministic: a fixed-seed
        ///     generator, because a test whose verdict moves with the seed is not a measurement.
        /// </summary>
        private static float NoiseVarianceRatio(in OneEuroTuning tuning, out float inputVariance)
        {
            // Uniform in ±8.7 mm, i.e. a standard deviation of 5 mm.
            const float NoiseHalfWidth = 0.0087f;
            const int WarmUpFrames = 180;
            const int MeasuredFrames = 1620;

            var random = new System.Random(7);
            var filter = new OneEuroFilter();

            double inputSum = 0d, inputSumSquares = 0d;
            double outputSum = 0d, outputSumSquares = 0d;

            for (int i = 0; i < WarmUpFrames + MeasuredFrames; i++)
            {
                float input = 1f + (float)((random.NextDouble() * 2d - 1d) * NoiseHalfWidth);
                float output = filter.Step(input, in tuning, Dt);
                if (i < WarmUpFrames) continue;

                inputSum += input;
                inputSumSquares += (double)input * input;
                outputSum += output;
                outputSumSquares += (double)output * output;
            }

            double inputMean = inputSum / MeasuredFrames;
            double outputMean = outputSum / MeasuredFrames;
            inputVariance = (float)(inputSumSquares / MeasuredFrames - inputMean * inputMean);
            double outputVariance = outputSumSquares / MeasuredFrames - outputMean * outputMean;
            return outputVariance <= 0d ? float.PositiveInfinity : (float)(inputVariance / outputVariance);
        }

        /// <summary>Worst lag over the last second of a 3 s ramp at 1 m/s — steady state, not the entry.</summary>
        private static float SteadyStateRampLag(in OneEuroTuning tuning, float deltaTime = Dt)
        {
            var filter = new OneEuroFilter();
            int steps = Mathf.RoundToInt(3f / deltaTime);
            float truth = 0f;
            float lag = 0f;

            for (int i = 0; i < steps; i++)
            {
                truth += 1f * deltaTime;
                float filtered = filter.Step(truth, in tuning, deltaTime);
                if (truth >= 2f) lag = Mathf.Max(lag, Mathf.Abs(truth - filtered));
            }

            return lag;
        }
    }
}
