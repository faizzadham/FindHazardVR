using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Emotion.Compilation;
using Convai.Modules.Emotion.Direction;
using Convai.Modules.Emotion.Profiles;
using Convai.Modules.Emotion.Taxonomy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    public sealed class EmotionExpressionPlannerTests
    {
        private EmotionTaxonomyAsset _taxonomy;

        [SetUp]
        public void SetUp() => _taxonomy = EmotionTaxonomyAsset.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_taxonomy);

        [Test]
        public void EmptyRecipeList_CompilesProductionDefaultLibrary()
        {
            CompiledEmotionModel model = EmotionProfileCompiler.Compile(_taxonomy, null);

            Assert.That(model.Recipes.Length, Is.EqualTo(8));
            Assert.That(model.Labels.Length, Is.EqualTo(_taxonomy.Emotions.Count));
        }

        [Test]
        public void Joy_UsesTemporalAttackAndReleasesWithoutOvershoot()
        {
            CompiledEmotionModel model = EmotionProfileCompiler.Compile(_taxonomy, null);
            var planner = new EmotionExpressionPlanner(model);
            var scores = new float[model.Labels.Length];
            int joy = System.Array.IndexOf(model.Labels, "joy");
            scores[joy] = 1f;

            planner.Tick(scores, false, 0f, 1f / 60f);
            float first = planner.Weights[(int)StandardBlendshape.MouthSmileLeft];
            for (int i = 0; i < 60; i++) planner.Tick(scores, false, 0f, 1f / 60f);
            float apex = planner.Weights[(int)StandardBlendshape.MouthSmileLeft];

            Assert.That(first, Is.GreaterThan(0f));
            Assert.That(apex, Is.GreaterThan(first));
            Assert.That(apex, Is.LessThanOrEqualTo(68f * 0.9f + 0.01f));

            scores[joy] = 0f;
            for (int i = 0; i < 180; i++) planner.Tick(scores, false, 0f, 1f / 60f);
            Assert.That(planner.Weights[(int)StandardBlendshape.MouthSmileLeft], Is.LessThan(0.05f));
        }

        [Test]
        public void AntagonisticChannels_AreNormalizedToAnatomicalCap()
        {
            var recipes = new[]
            {
                new EmotionExpressionRecipe("joy", 0.03f, 0f, 0.05f, 1f,
                    new EmotionExpressionChannel(StandardBlendshape.MouthSmileLeft, EmotionFacialRegion.Mouth, 100f)),
                new EmotionExpressionRecipe("sadness", 0.03f, 0f, 0.05f, 1f,
                    new EmotionExpressionChannel(StandardBlendshape.MouthFrownLeft, EmotionFacialRegion.Mouth, 100f))
            };
            CompiledEmotionModel model = EmotionProfileCompiler.Compile(_taxonomy, recipes);
            var planner = new EmotionExpressionPlanner(model);
            var scores = new float[model.Labels.Length];
            scores[System.Array.IndexOf(model.Labels, "joy")] = 1f;
            scores[System.Array.IndexOf(model.Labels, "sadness")] = 1f;
            for (int i = 0; i < 90; i++) planner.Tick(scores, false, 0f, 1f / 60f);

            float total = planner.Weights[(int)StandardBlendshape.MouthSmileLeft] +
                          planner.Weights[(int)StandardBlendshape.MouthFrownLeft];
            Assert.That(total, Is.LessThanOrEqualTo(80.01f));
        }
    }
}
