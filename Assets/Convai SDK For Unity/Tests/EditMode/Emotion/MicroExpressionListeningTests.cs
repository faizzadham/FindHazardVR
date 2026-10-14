using Convai.Modules.Emotion.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Unit tests for <see cref="MicroExpressionDirector" />'s listening-reaction
    ///     layer: the hard legacy-off invariant (never called / strength 0 is bit-identical to
    ///     before this layer existed), opt-in attentive contribution that scales with strength,
    ///     smooth ease-out to exactly 0, and determinism.
    /// </summary>
    [TestFixture]
    public sealed class MicroExpressionListeningTests
    {
        private GameObject _owner;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject(nameof(MicroExpressionListeningTests));
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
        }

        private static MicroExpressionDirector CreateSeeded(GameObject owner)
        {
            var director = new MicroExpressionDirector();
            director.Seed(owner.transform);
            director.SetEmotionBias("neutral", 0f);
            return director;
        }

        [Test]
        public void SetListeningState_NeverCalled_BitIdenticalToExplicitlyInactive()
        {
            MicroExpressionDirector neverCalled = CreateSeeded(_owner);
            MicroExpressionDirector explicitlyInactive = CreateSeeded(_owner);

            const float dt = 1f / 60f;
            for (int i = 0; i < 180; i++)
            {
                // neverCalled: SetListeningState is simply never invoked (the legacy call shape).
                explicitlyInactive.SetListeningState(false, 0f);

                neverCalled.Tick(dt, amplitude: 0.2f, stillness: 0.6f, speechAccentStrength: 0.3f, speechEnergy: 0f);
                explicitlyInactive.Tick(dt, amplitude: 0.2f, stillness: 0.6f, speechAccentStrength: 0.3f, speechEnergy: 0f);

                for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
                {
                    var channel = (MicroExpressionChannel)c;
                    Assert.That(explicitlyInactive.GetChannelWeight(channel), Is.EqualTo(neverCalled.GetChannelWeight(channel)),
                        $"Channel {channel} diverged at tick {i}: listening never active/strength 0 must be bit-identical.");
                }
            }
        }

        [Test]
        public void Listening_Active_ProducesNonzeroAttentiveContribution_AndScalesWithStrength()
        {
            MicroExpressionDirector baseline = CreateSeeded(_owner);
            MicroExpressionDirector lowStrength = CreateSeeded(_owner);
            MicroExpressionDirector highStrength = CreateSeeded(_owner);

            const float dt = 1f / 60f;
            float baselineBrow = 0f, lowBrow = 0f, highBrow = 0f;

            // Run long enough for the attack envelope (0.6s time constant) to settle well above 0.
            for (int i = 0; i < 300; i++)
            {
                baseline.SetListeningState(false, 0f);
                lowStrength.SetListeningState(true, 0.3f);
                highStrength.SetListeningState(true, 0.9f);

                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                lowStrength.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                highStrength.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);

                baselineBrow = baseline.GetChannelWeight(MicroExpressionChannel.BrowOuterUp);
                lowBrow = lowStrength.GetChannelWeight(MicroExpressionChannel.BrowOuterUp);
                highBrow = highStrength.GetChannelWeight(MicroExpressionChannel.BrowOuterUp);
            }

            Assert.That(lowBrow, Is.GreaterThan(baselineBrow + 0.01f),
                "An active listening state must contribute a visible attentive lift on BrowOuterUp.");
            Assert.That(highBrow, Is.GreaterThan(lowBrow),
                "Higher listening-reaction strength must produce a larger attentive contribution than lower strength.");
        }

        [Test]
        public void Listening_EndsAfterActive_EasesOutToExactlyZeroContribution()
        {
            MicroExpressionDirector baseline = CreateSeeded(_owner);
            MicroExpressionDirector wasListening = CreateSeeded(_owner);

            const float dt = 1f / 60f;

            // Activate for a while (envelope ramps up).
            for (int i = 0; i < 120; i++)
            {
                baseline.SetListeningState(false, 0f);
                wasListening.SetListeningState(true, 0.8f);

                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                wasListening.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            Assert.That(wasListening.GetChannelWeight(MicroExpressionChannel.BrowOuterUp),
                Is.GreaterThan(baseline.GetChannelWeight(MicroExpressionChannel.BrowOuterUp) + 0.01f),
                "Sanity: listening must be visibly active before testing ease-out.");

            // Stop listening; run long enough for the 1.2s decay time constant to fully settle
            // (well past its floor snap) before comparing against the never-listening baseline.
            for (int i = 0; i < 600; i++)
            {
                baseline.SetListeningState(false, 0f);
                wasListening.SetListeningState(false, 0f);

                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                wasListening.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
            {
                var channel = (MicroExpressionChannel)c;
                Assert.That(wasListening.GetChannelWeight(channel), Is.EqualTo(baseline.GetChannelWeight(channel)),
                    $"Channel {channel}: after fully easing out, listening contribution must be exactly 0 again.");
            }
        }

        [Test]
        public void Listening_SameSeedSameSequence_IsDeterministic()
        {
            MicroExpressionDirector directorA = CreateSeeded(_owner);
            MicroExpressionDirector directorB = CreateSeeded(_owner);

            const float dt = 1f / 60f;
            for (int i = 0; i < 240; i++)
            {
                bool listening = i % 47 < 20; // arbitrary on/off pattern, identical for both.
                float strength = 0.4f;

                directorA.SetListeningState(listening, strength);
                directorB.SetListeningState(listening, strength);

                directorA.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                directorB.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);

                for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
                {
                    var channel = (MicroExpressionChannel)c;
                    Assert.That(directorB.GetChannelWeight(channel), Is.EqualTo(directorA.GetChannelWeight(channel)),
                        $"Channel {channel} diverged at tick {i}: identical seed + identical setter/tick sequence must reproduce identically.");
                }
            }
        }
    }
}
