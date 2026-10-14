using Convai.Modules.Emotion.Core;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Unit tests for <see cref="MicroExpressionDirector" />: deterministic idle drift,
    ///     speech-accent onset/decay derived from a caller-supplied energy sample, emotion-bias weighting, and
    ///     zero-allocation steady state.
    /// </summary>
    [TestFixture]
    public sealed class MicroExpressionDirectorTests
    {
        private GameObject _owner;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject(nameof(MicroExpressionDirectorTests));
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
        }

        [Test]
        public void Seed_SameOwner_ProducesIdenticalDriftSequence()
        {
            var directorA = new MicroExpressionDirector();
            directorA.Seed(_owner.transform);
            var directorB = new MicroExpressionDirector();
            directorB.Seed(_owner.transform);

            const float dt = 1f / 60f;
            for (int i = 0; i < 120; i++)
            {
                directorA.SetEmotionBias("neutral", 0f);
                directorB.SetEmotionBias("neutral", 0f);
                directorA.Tick(dt, amplitude: 0.15f, stillness: 0.5f, speechAccentStrength: 0.3f, speechEnergy: 0f);
                directorB.Tick(dt, amplitude: 0.15f, stillness: 0.5f, speechAccentStrength: 0.3f, speechEnergy: 0f);

                for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
                {
                    var channel = (MicroExpressionChannel)c;
                    Assert.That(directorB.GetChannelWeight(channel), Is.EqualTo(directorA.GetChannelWeight(channel)),
                        $"Channel {channel} diverged at tick {i}: same seed must reproduce an identical sequence.");
                }
            }
        }

        [Test]
        public void Tick_FlatZeroSpeechEnergy_NoAccent_IdleDriftOnly()
        {
            var director = new MicroExpressionDirector();
            director.Seed(_owner.transform);
            director.SetEmotionBias("neutral", 0f);

            const float dt = 1f / 60f;
            float maxBrowOuter = 0f;
            for (int i = 0; i < 180; i++)
            {
                director.Tick(dt, amplitude: 0.15f, stillness: 0.5f, speechAccentStrength: 0.3f, speechEnergy: 0f);
                maxBrowOuter = Mathf.Max(maxBrowOuter, director.GetChannelWeight(MicroExpressionChannel.BrowOuterUp));
            }

            // Idle-only ceiling: amplitude(0.15) * stillness(0.5) * bias(<=1) can never exceed
            // ~0.15 for the drift term alone; a triggered accent would push this well past that.
            Assert.That(maxBrowOuter, Is.LessThan(0.2f),
                "With flat zero speech energy, no accent should ever fire; only idle drift (small amplitude) should be visible.");
        }

        [Test]
        public void Tick_RisingSpeechEnergy_FiresBrowRaiseAccent()
        {
            var director = new MicroExpressionDirector();
            director.Seed(_owner.transform);
            director.SetEmotionBias("neutral", 0f);

            const float dt = 1f / 60f;
            // Warm up on silence first so idle drift settles into its normal small range.
            for (int i = 0; i < 30; i++)
                director.Tick(dt, amplitude: 0.15f, stillness: 0.5f, speechAccentStrength: 0.3f, speechEnergy: 0f);

            float beforeAccent = director.GetChannelWeight(MicroExpressionChannel.BrowOuterUp);

            // Sharp rising edge well above the onset threshold.
            float duringAccent = beforeAccent;
            for (int i = 0; i < 10; i++)
            {
                director.Tick(dt, amplitude: 0.15f, stillness: 0.5f, speechAccentStrength: 0.3f, speechEnergy: 0.9f);
                duringAccent = Mathf.Max(duringAccent, director.GetChannelWeight(MicroExpressionChannel.BrowOuterUp));
            }

            Assert.That(duringAccent, Is.GreaterThan(beforeAccent + 0.05f),
                "A rising speech-energy edge above threshold must fire a visible brow-raise accent on BrowOuterUp.");
        }

        // SetEmotionBias_*_Favors* tests assert on GetEmotionBias (the bias table SetEmotionBias
        // writes) rather than GetChannelWeight (Tick's composed output). GetChannelWeight also
        // folds in each channel's INDEPENDENT seeded idle-drift phase, which is legitimately
        // random per channel/owner and can occasionally outweigh a 4x bias ratio in a single
        // tick's snapshot — that would make these tests assert on RNG luck instead of on what
        // SetEmotionBias actually did. See GetEmotionBias's XML doc for the full rationale.

        [Test]
        public void SetEmotionBias_Joy_FavorsBrowOuterUpAndCheekRaise()
        {
            var director = new MicroExpressionDirector();
            director.Seed(_owner.transform);

            director.SetEmotionBias("joy", 1f);

            float browOuter = director.GetEmotionBias(MicroExpressionChannel.BrowOuterUp);
            float cheekRaise = director.GetEmotionBias(MicroExpressionChannel.CheekRaise);
            float browInner = director.GetEmotionBias(MicroExpressionChannel.BrowInnerUp);

            Assert.That(browOuter, Is.GreaterThan(browInner));
            Assert.That(cheekRaise, Is.GreaterThan(browInner));
        }

        [Test]
        public void SetEmotionBias_Sadness_FavorsBrowInnerUp()
        {
            var director = new MicroExpressionDirector();
            director.Seed(_owner.transform);

            director.SetEmotionBias("sadness", 1f);

            float browInner = director.GetEmotionBias(MicroExpressionChannel.BrowInnerUp);
            float browOuter = director.GetEmotionBias(MicroExpressionChannel.BrowOuterUp);
            float cheekRaise = director.GetEmotionBias(MicroExpressionChannel.CheekRaise);

            Assert.That(browInner, Is.GreaterThan(browOuter));
            Assert.That(browInner, Is.GreaterThan(cheekRaise));
        }

        // ── Zero-allocation steady state ──────────────────────────────────────────

        [Test]
        public void Tick_SteadyState_AllocatesNothing()
        {
            var director = new MicroExpressionDirector();
            director.Seed(_owner.transform);

            string emotionLabel = "joy";
            const float dt = 1f / 60f;
            for (int i = 0; i < 500; i++)
            {
                director.SetEmotionBias(emotionLabel, 0.6f);
                director.Tick(dt, 0.15f, 0.5f, 0.3f, 0.5f + 0.5f * Mathf.Sin(i * 0.1f));
            }

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    director.SetEmotionBias(emotionLabel, 0.6f);
                    director.Tick(dt, 0.15f, 0.5f, 0.3f, 0.5f + 0.5f * Mathf.Sin(i * 0.1f));
                }
            });

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"MicroExpressionDirector.SetEmotionBias/Tick must produce zero managed allocation events in steady state; measured {allocationCount} allocation events.");
        }
    }
}
