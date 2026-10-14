using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyAnimation.Core.Selection;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public class VariantSchedulerTests
    {
        private readonly List<Object> _cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
        }

        private IdleEntry Entry(string name, float weight, params (string label, float mult)[] affinities)
        {
            var clip = new AnimationClip { name = name };
            _cleanup.Add(clip);

            var affinityList = new List<EmotionAffinity>();
            foreach ((string label, float mult) in affinities)
            {
                var affinity = new EmotionAffinity();
                affinity.Initialize(label, mult);
                affinityList.Add(affinity);
            }

            var entry = new IdleEntry();
            entry.Initialize(clip, weight, affinityList);
            return entry;
        }

        private static EmotionReading Emotion(string label, float score) =>
            new(label, score, EmotionReading.EmptyScores, 0f, 0f);

        [Test]
        public void SelectNext_EmptyOrAllInvalid_ReturnsMinusOne()
        {
            var scheduler = new VariantScheduler(42);

            Assert.AreEqual(-1, scheduler.SelectNext(new List<IdleEntry>(), -1, EmotionReading.Neutral, out _));

            var zeroWeight = new List<IdleEntry> { Entry("a", 0f) };
            Assert.AreEqual(-1, scheduler.SelectNext(zeroWeight, -1, EmotionReading.Neutral, out _));
        }

        [Test]
        public void SelectNext_SingleEntry_AlwaysReturnsIt_EvenAsRepeat()
        {
            var scheduler = new VariantScheduler(42);
            var entries = new List<IdleEntry> { Entry("only", 1f) };

            Assert.AreEqual(0, scheduler.SelectNext(entries, -1, EmotionReading.Neutral, out _));
            Assert.AreEqual(0, scheduler.SelectNext(entries, 0, EmotionReading.Neutral, out _));
        }

        [Test]
        public void SelectNext_NeverRepeatsImmediately_WhenAlternativesExist()
        {
            var scheduler = new VariantScheduler(42);
            var entries = new List<IdleEntry> { Entry("a", 1f), Entry("b", 1f), Entry("c", 1f) };

            int last = -1;
            for (int i = 0; i < 200; i++)
            {
                int next = scheduler.SelectNext(entries, last, EmotionReading.Neutral, out _);
                Assert.AreNotEqual(last, next, $"immediate repeat at roll {i}");
                Assert.GreaterOrEqual(next, 0);
                last = next;
            }
        }

        [Test]
        public void SelectNext_RespectsWeights_Statistically()
        {
            var scheduler = new VariantScheduler(1234);
            var entries = new List<IdleEntry> { Entry("common", 9f), Entry("rare", 1f) };

            int commonCount = 0;
            for (int i = 0; i < 1000; i++)
            {
                // lastIndex −1 keeps both candidates in every roll.
                if (scheduler.SelectNext(entries, -1, EmotionReading.Neutral, out _) == 0)
                    commonCount++;
            }

            Assert.Greater(commonCount, 800, "9:1 weighting should dominate");
            Assert.Less(commonCount, 980, "rare entry must still appear");
        }

        [Test]
        public void SelectNext_EmotionAffinity_BiasesSelection()
        {
            var scheduler = new VariantScheduler(99);
            var entries = new List<IdleEntry>
            {
                Entry("neutral_idle", 1f),
                Entry("joy_idle", 1f, ("joy", 8f))
            };

            int joyPicks = 0;
            for (int i = 0; i < 1000; i++)
            {
                if (scheduler.SelectNext(entries, -1, Emotion("joy", 1f), out _) == 1)
                    joyPicks++;
            }

            Assert.Greater(joyPicks, 750, "joy affinity ×8 should dominate under full-score joy");
        }

        [Test]
        public void SelectNext_AffinityZero_ExcludesEntryUnderThatEmotion()
        {
            var scheduler = new VariantScheduler(7);
            var entries = new List<IdleEntry>
            {
                Entry("always", 1f),
                Entry("never_when_angry", 1f, ("anger", 0f))
            };

            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(0,
                    scheduler.SelectNext(entries, -1, Emotion("anger", 1f), out _),
                    "anger affinity 0 must exclude the entry");
            }
        }

        [Test]
        public void NextInterval_StaysInRange()
        {
            var scheduler = new VariantScheduler(5);
            for (int i = 0; i < 100; i++)
            {
                float value = scheduler.NextInterval(8f, 16f);
                Assert.GreaterOrEqual(value, 8f);
                Assert.LessOrEqual(value, 16f);
            }

            Assert.AreEqual(3f, scheduler.NextInterval(3f, 3f), 1e-4f);
        }
    }
}
