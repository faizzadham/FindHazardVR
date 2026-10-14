using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     POCO coverage for <see cref="ExpressivenessCurves" />: anchor
    ///     exactness at every named anchor, monotonicity across the whole 0..1 domain, and the
    ///     <see cref="ExpressivenessPreset" /> → anchor mapping.
    /// </summary>
    public sealed class ExpressivenessCurvesTests
    {
        [TestCase(0f, 0.35f)]
        [TestCase(0.25f, 0.62f)]
        [TestCase(0.5f, 1.0f)]
        [TestCase(0.75f, 1.35f)]
        [TestCase(1f, 1.75f)]
        public void AmplitudeGain_AnchorsExactly(float expressiveness, float expected) =>
            Assert.That(ExpressivenessCurves.AmplitudeGain(expressiveness), Is.EqualTo(expected));

        [TestCase(0f, 0.55f)]
        [TestCase(0.25f, 0.75f)]
        [TestCase(0.5f, 1.0f)]
        [TestCase(0.75f, 1.25f)]
        [TestCase(1f, 1.5f)]
        public void FrequencyGain_AnchorsExactly(float expressiveness, float expected) =>
            Assert.That(ExpressivenessCurves.FrequencyGain(expressiveness), Is.EqualTo(expected));

        [TestCase(0f, 0.0f)]
        [TestCase(0.25f, 0.45f)]
        [TestCase(0.5f, 1.0f)]
        [TestCase(0.75f, 1.25f)]
        [TestCase(1f, 1.5f)]
        public void RichnessGain_AnchorsExactly(float expressiveness, float expected) =>
            Assert.That(ExpressivenessCurves.RichnessGain(expressiveness), Is.EqualTo(expected));

        [Test]
        public void AllCurves_AreMonotonicallyNonDecreasing_AcrossTheDomain()
        {
            float prevAmplitude = ExpressivenessCurves.AmplitudeGain(0f);
            float prevFrequency = ExpressivenessCurves.FrequencyGain(0f);
            float prevRichness = ExpressivenessCurves.RichnessGain(0f);

            for (int i = 1; i <= 100; i++)
            {
                float e = i / 100f;

                float amplitude = ExpressivenessCurves.AmplitudeGain(e);
                float frequency = ExpressivenessCurves.FrequencyGain(e);
                float richness = ExpressivenessCurves.RichnessGain(e);

                Assert.That(amplitude, Is.GreaterThanOrEqualTo(prevAmplitude));
                Assert.That(frequency, Is.GreaterThanOrEqualTo(prevFrequency));
                Assert.That(richness, Is.GreaterThanOrEqualTo(prevRichness));

                prevAmplitude = amplitude;
                prevFrequency = frequency;
                prevRichness = richness;
            }
        }

        [Test]
        public void Curves_ClampInputOutsideZeroOne()
        {
            Assert.That(ExpressivenessCurves.AmplitudeGain(-1f), Is.EqualTo(ExpressivenessCurves.AmplitudeGain(0f)));
            Assert.That(ExpressivenessCurves.AmplitudeGain(2f), Is.EqualTo(ExpressivenessCurves.AmplitudeGain(1f)));
        }

        [TestCase(ExpressivenessPreset.Subtle, 0.25f)]
        [TestCase(ExpressivenessPreset.Natural, 0.5f)]
        [TestCase(ExpressivenessPreset.Expressive, 0.75f)]
        [TestCase(ExpressivenessPreset.Theatrical, 1f)]
        public void For_ResolvesFixedPresetAnchors(ExpressivenessPreset preset, float expected) =>
            Assert.That(ExpressivenessCurves.For(preset), Is.EqualTo(expected));

        [Test]
        public void For_Custom_DoesNotThrow_ReturnsNaturalAnchor() =>
            Assert.That(ExpressivenessCurves.For(ExpressivenessPreset.Custom), Is.EqualTo(0.5f));
    }
}
