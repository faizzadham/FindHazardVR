using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Emotion.Compilation;
using Convai.Modules.Emotion.Direction;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     The consumer side of the profile's Prosody Coupling knob: the global gain the controller
    ///     smooths must actually scale the composed facial weights.
    /// </summary>
    /// <remarks>
    ///     This closes a real defect. The gain used to be handed only to the output bindings, while
    ///     the face went through this planner — which never saw it. So on every shipped profile the
    ///     knob moved animator parameters and shader properties and left the expression itself
    ///     untouched, contradicting both its tooltip and the documentation.
    /// </remarks>
    public sealed class EmotionExpressionPlannerProsodyTests
    {
        private EmotionTaxonomyAsset _taxonomy;

        [SetUp]
        public void SetUp() => _taxonomy = EmotionTaxonomyAsset.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_taxonomy);

        private static float SettleJoySmile(CompiledEmotionModel model, float globalProsodyGain)
        {
            var planner = new EmotionExpressionPlanner(model);
            var scores = new float[model.Labels.Length];
            scores[System.Array.IndexOf(model.Labels, "joy")] = 1f;

            for (int i = 0; i < 240; i++)
                planner.Tick(scores, false, 0f, 1f / 60f, globalProsodyGain);

            return planner.Weights[(int)StandardBlendshape.MouthSmileLeft];
        }

        [Test]
        public void GainOfOne_LeavesWeightsUnchanged()
        {
            CompiledEmotionModel model = EmotionProfileCompiler.Compile(_taxonomy, null);

            float withDefaultArgument = SettleJoySmile(model, 1f);
            var planner = new EmotionExpressionPlanner(model);
            var scores = new float[model.Labels.Length];
            scores[System.Array.IndexOf(model.Labels, "joy")] = 1f;
            for (int i = 0; i < 240; i++) planner.Tick(scores, false, 0f, 1f / 60f);
            float withoutArgument = planner.Weights[(int)StandardBlendshape.MouthSmileLeft];

            Assert.That(withDefaultArgument, Is.EqualTo(withoutArgument).Within(0.0001f),
                "A gain of 1 must be a provable no-op, so coupling 0 changes nothing.");
        }

        [Test]
        public void HigherGain_ProducesHigherWeight_LowerGain_ProducesLower()
        {
            CompiledEmotionModel model = EmotionProfileCompiler.Compile(_taxonomy, null);

            float quiet = SettleJoySmile(model, 0.85f);
            float neutral = SettleJoySmile(model, 1f);
            float emphatic = SettleJoySmile(model, 1.15f);

            Assert.That(neutral, Is.GreaterThan(0f), "Sanity: joy must drive the smile channel.");
            Assert.That(quiet, Is.LessThan(neutral),
                "A gain below 1 (a lull in delivery) must soften the expression.");
            Assert.That(emphatic, Is.GreaterThan(neutral),
                "A gain above 1 (emphatic delivery) must brighten the expression.");
        }

        [Test]
        public void NonFiniteOrNonPositiveGain_FallsBackToOne()
        {
            CompiledEmotionModel model = EmotionProfileCompiler.Compile(_taxonomy, null);
            float neutral = SettleJoySmile(model, 1f);

            Assert.That(SettleJoySmile(model, float.NaN), Is.EqualTo(neutral).Within(0.0001f));
            Assert.That(SettleJoySmile(model, 0f), Is.EqualTo(neutral).Within(0.0001f));
            Assert.That(SettleJoySmile(model, -1f), Is.EqualTo(neutral).Within(0.0001f));
        }
    }
}
