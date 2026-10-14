using Convai.Modules.Gaze.Core.Policy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Unit tests for <see cref="PupilArousalModel" />, which drives pupil response through
    ///     the material seam: a pure POCO, so these run without a scene.
    /// </summary>
    public sealed class PupilArousalModelTests
    {
        private const float FixedDt = 1f / 60f;

        private static void TickFor(PupilArousalModel model, float emotionIntensity, float engagement, float seconds)
        {
            float remaining = seconds;
            while (remaining > 0f)
            {
                float dt = Mathf.Min(FixedDt, remaining);
                model.Tick(emotionIntensity, engagement, dt);
                remaining -= dt;
            }
        }

        [Test]
        public void StepInput_ReachesApproximately63PercentAfterOneSecond()
        {
            var model = new PupilArousalModel();

            // emotionIntensity=1, engagement=1 -> target = 0.6*1 + 0.4*1 = 1.0, a clean step to 1.
            TickFor(model, 1f, 1f, 1f);

            // Exponential smoothing with a 1s time constant reaches 1 - e^-1 ≈ 0.6321 of the
            // step after exactly one time constant of elapsed simulated time.
            Assert.That(model.Dilation, Is.EqualTo(1f - Mathf.Exp(-1f)).Within(0.01f));
        }

        [TestCase(1f, 0f, 0.6f)]
        [TestCase(0f, 1f, 0.4f)]
        [TestCase(0.5f, 0.5f, 0.5f)]
        [TestCase(1f, 1f, 1f)]
        [TestCase(0f, 0f, 0f)]
        public void MappingWeights_SixtyForEmotionFortyForEngagement(
            float emotionIntensity, float engagement, float expectedTarget)
        {
            var model = new PupilArousalModel();

            // A very large dt (many time constants) drives the smoothed value arbitrarily close
            // to the instantaneous target in a single tick, isolating the mapping weights from
            // the smoothing behavior under test elsewhere.
            model.Tick(emotionIntensity, engagement, 100f);

            Assert.That(model.Dilation, Is.EqualTo(expectedTarget).Within(0.0001f));
        }

        [Test]
        public void OutOfRangeInputs_ClampToZeroOneBeforeAndAfterSmoothing()
        {
            var model = new PupilArousalModel();

            model.Tick(5f, 5f, 100f);
            Assert.That(model.Dilation, Is.EqualTo(1f).Within(0.0001f));

            var negativeModel = new PupilArousalModel();
            negativeModel.Tick(-3f, -3f, 100f);
            Assert.That(negativeModel.Dilation, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void Dilation_IsAlwaysWithinZeroOneRange_AcrossATickSequence()
        {
            var model = new PupilArousalModel();
            var random = new System.Random(1234);

            for (int i = 0; i < 200; i++)
            {
                float emotion = (float)random.NextDouble() * 3f - 1f;   // includes out-of-range values
                float engagement = (float)random.NextDouble() * 3f - 1f;
                model.Tick(emotion, engagement, FixedDt);

                Assert.That(model.Dilation, Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void InputsDrop_DilationDecaysBackTowardZero()
        {
            var model = new PupilArousalModel();

            TickFor(model, 1f, 1f, 3f); // settle near full arousal
            float peak = model.Dilation;
            Assert.That(peak, Is.GreaterThan(0.9f));

            TickFor(model, 0f, 0f, 1f); // arousal inputs drop to zero
            float afterOneSecond = model.Dilation;

            Assert.That(afterOneSecond, Is.LessThan(peak),
                "Dilation must decay when emotion/engagement inputs drop.");
            Assert.That(afterOneSecond, Is.EqualTo(peak * Mathf.Exp(-1f)).Within(0.01f),
                "Decay follows the same ~1s exponential time constant as the rise.");
        }

        [Test]
        public void RepeatedIdenticalTickSequences_AreDeterministic()
        {
            var first = new PupilArousalModel();
            var second = new PupilArousalModel();

            for (int i = 0; i < 90; i++)
            {
                float emotion = 0.3f + 0.2f * Mathf.Sin(i * 0.1f);
                float engagement = 0.5f + 0.1f * Mathf.Cos(i * 0.07f);
                first.Tick(emotion, engagement, FixedDt);
                second.Tick(emotion, engagement, FixedDt);
            }

            Assert.That(second.Dilation, Is.EqualTo(first.Dilation).Within(0.0000001f));
        }

        [Test]
        public void Reset_ReturnsDilationToZero()
        {
            var model = new PupilArousalModel();
            TickFor(model, 1f, 1f, 2f);
            Assert.That(model.Dilation, Is.GreaterThan(0f));

            model.Reset();

            Assert.That(model.Dilation, Is.EqualTo(0f));
        }
    }
}
