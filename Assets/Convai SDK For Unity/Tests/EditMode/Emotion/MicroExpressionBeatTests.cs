using System.Reflection;
using Convai.Modules.Emotion.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Unit tests for <see cref="MicroExpressionDirector" />'s dialogue-beat layer: the
    ///     Thinking sustained envelope (mirrors the listening envelope) and the shared
    ///     Reacting/Interrupted one-shot beat-accent envelope — hard legacy-off invariant (never
    ///     triggered/called is bit-identical), opt-in contribution, ease-to-zero, retrigger
    ///     mid-decay, determinism, and strength-0 no-op.
    /// </summary>
    [TestFixture]
    public sealed class MicroExpressionBeatTests
    {
        private GameObject _owner;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject(nameof(MicroExpressionBeatTests));
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
        public void NeverCalled_BitIdenticalToBaselineTwin()
        {
            MicroExpressionDirector neverCalled = CreateSeeded(_owner);
            MicroExpressionDirector explicitlyInactive = CreateSeeded(_owner);

            const float dt = 1f / 60f;
            for (int i = 0; i < 180; i++)
            {
                // neverCalled: SetThinkingState/TriggerBeatAccent are simply never invoked.
                explicitlyInactive.SetThinkingState(false, 0f);

                neverCalled.Tick(dt, amplitude: 0.2f, stillness: 0.6f, speechAccentStrength: 0.3f, speechEnergy: 0f);
                explicitlyInactive.Tick(dt, amplitude: 0.2f, stillness: 0.6f, speechAccentStrength: 0.3f, speechEnergy: 0f);

                for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
                {
                    var channel = (MicroExpressionChannel)c;
                    Assert.That(explicitlyInactive.GetChannelWeight(channel), Is.EqualTo(neverCalled.GetChannelWeight(channel)),
                        $"Channel {channel} diverged at tick {i}: dialogue-beat layer never engaged must be bit-identical.");
                }
            }
        }

        [Test]
        public void Thinking_Active_RisesAndEasesToExactlyZeroAfter()
        {
            MicroExpressionDirector baseline = CreateSeeded(_owner);
            MicroExpressionDirector thinking = CreateSeeded(_owner);

            const float dt = 1f / 60f;

            // Activate for a while (0.5s attack time constant): envelope should rise well above baseline.
            for (int i = 0; i < 120; i++)
            {
                baseline.SetThinkingState(false, 0f);
                thinking.SetThinkingState(true, 0.8f);

                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                thinking.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            Assert.That(thinking.GetChannelWeight(MicroExpressionChannel.BrowDown),
                Is.GreaterThan(baseline.GetChannelWeight(MicroExpressionChannel.BrowDown) + 0.01f),
                "An active Thinking state must contribute a visible concentration look on BrowDown.");

            // Stop thinking; run long enough for the 0.8s decay time constant to fully settle past
            // its floor snap before comparing against the never-thinking baseline.
            for (int i = 0; i < 400; i++)
            {
                baseline.SetThinkingState(false, 0f);
                thinking.SetThinkingState(false, 0f);

                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                thinking.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
            {
                var channel = (MicroExpressionChannel)c;
                Assert.That(thinking.GetChannelWeight(channel), Is.EqualTo(baseline.GetChannelWeight(channel)),
                    $"Channel {channel}: after fully easing out, Thinking contribution must be exactly 0 again.");
            }
        }

        [Test]
        public void ReactingBeat_ProducesTransientBrowOuterUpBump_ThatFullyDecays()
        {
            MicroExpressionDirector baseline = CreateSeeded(_owner);
            MicroExpressionDirector reacting = CreateSeeded(_owner);

            const float dt = 1f / 60f;

            reacting.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Reacting, 0.9f);

            // Run through the fast attack (0.12s time constant): should be well above baseline.
            for (int i = 0; i < 20; i++)
            {
                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                reacting.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            Assert.That(reacting.GetChannelWeight(MicroExpressionChannel.BrowOuterUp),
                Is.GreaterThan(baseline.GetChannelWeight(MicroExpressionChannel.BrowOuterUp) + 0.01f),
                "A Reacting beat accent must produce a visible transient bump on BrowOuterUp.");

            // Run long enough for the 0.6s decay time constant to fully settle.
            for (int i = 0; i < 400; i++)
            {
                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                reacting.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
            {
                var channel = (MicroExpressionChannel)c;
                Assert.That(reacting.GetChannelWeight(channel), Is.EqualTo(baseline.GetChannelWeight(channel)),
                    $"Channel {channel}: the Reacting one-shot must fully decay back to the baseline twin.");
            }
        }

        [Test]
        public void InterruptedBeat_ProducesTransientSquintBump_ThatFullyDecays()
        {
            MicroExpressionDirector baseline = CreateSeeded(_owner);
            MicroExpressionDirector interrupted = CreateSeeded(_owner);

            const float dt = 1f / 60f;

            interrupted.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Interrupted, 0.9f);

            // Run through the fast attack (0.09s time constant): should be well above baseline.
            for (int i = 0; i < 20; i++)
            {
                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                interrupted.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            Assert.That(interrupted.GetChannelWeight(MicroExpressionChannel.Squint),
                Is.GreaterThan(baseline.GetChannelWeight(MicroExpressionChannel.Squint) + 0.01f),
                "An Interrupted beat accent must produce a visible transient bump on Squint.");

            // Run long enough for the 0.5s decay time constant to fully settle.
            for (int i = 0; i < 400; i++)
            {
                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                interrupted.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
            }

            for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
            {
                var channel = (MicroExpressionChannel)c;
                Assert.That(interrupted.GetChannelWeight(channel), Is.EqualTo(baseline.GetChannelWeight(channel)),
                    $"Channel {channel}: the Interrupted one-shot must fully decay back to the baseline twin.");
            }
        }

        [Test]
        public void RetriggerMidDecay_ReAttacksFromCurrentEnvelopeValue()
        {
            MicroExpressionDirector director = CreateSeeded(_owner);
            const float dt = 1f / 60f;

            director.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Reacting, 0.8f);

            // Attack time constant is 0.12s; the floor-snap attack-completion condition
            // (peak - envelope < 0.0005) is met at t ~= 0.12 * ln(0.8 / 0.0005) ~= 0.885s,
            // i.e. tick ~53 at dt = 1/60. Run 70 ticks (~1.17s) to guarantee the attack has
            // fully completed (envelope pinned at peak, _beatAttacking flipped false) before
            // any decay-phase assertions below.
            for (int i = 0; i < 70; i++)
                director.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);

            Assert.That(GetBeatAttacking(director), Is.False,
                "Sanity: the attack must have completed before we start observing decay.");

            float decayStart = director.GetChannelWeight(MicroExpressionChannel.BrowOuterUp);

            // Run another 30 ticks (~0.5s) into the 0.6s decay time constant: the envelope must
            // have measurably decreased, proving this window is genuinely decay, not attack.
            for (int i = 0; i < 30; i++)
                director.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);

            float midDecayValue = director.GetChannelWeight(MicroExpressionChannel.BrowOuterUp);

            Assert.That(midDecayValue, Is.LessThan(decayStart),
                "Sanity: the envelope must be visibly decaying (strictly decreasing) before the retrigger.");
            Assert.That(GetBeatAttacking(director), Is.False,
                "Sanity: still decaying (not re-attacking) immediately before the retrigger.");

            // Retrigger mid-decay: envelope must rise again from its current (decayed) value
            // (re-attack), not keep decaying.
            director.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Reacting, 0.8f);
            for (int i = 0; i < 15; i++)
                director.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);

            Assert.That(director.GetChannelWeight(MicroExpressionChannel.BrowOuterUp), Is.GreaterThan(midDecayValue),
                "Retriggering mid-decay must re-attack the shared beat envelope, not let it keep decaying.");
        }

        private static bool GetBeatAttacking(MicroExpressionDirector director)
        {
            FieldInfo field = typeof(MicroExpressionDirector).GetField(
                "_beatAttacking", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "MicroExpressionDirector must declare a private '_beatAttacking' field.");
            return (bool)field.GetValue(director);
        }

        [Test]
        public void StrengthZeroTrigger_IsANoOp()
        {
            MicroExpressionDirector baseline = CreateSeeded(_owner);
            MicroExpressionDirector triggeredWithZero = CreateSeeded(_owner);

            triggeredWithZero.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Reacting, 0f);
            triggeredWithZero.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Interrupted, -1f);

            const float dt = 1f / 60f;
            for (int i = 0; i < 30; i++)
            {
                baseline.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);
                triggeredWithZero.Tick(dt, 0.2f, 0.6f, 0.3f, 0f);

                for (int c = 0; c < (int)MicroExpressionChannel.Count; c++)
                {
                    var channel = (MicroExpressionChannel)c;
                    Assert.That(triggeredWithZero.GetChannelWeight(channel), Is.EqualTo(baseline.GetChannelWeight(channel)),
                        $"Channel {channel} diverged at tick {i}: a non-positive strength trigger must be a total no-op.");
                }
            }
        }

        [Test]
        public void SameSeedSameSequence_IsDeterministic()
        {
            MicroExpressionDirector directorA = CreateSeeded(_owner);
            MicroExpressionDirector directorB = CreateSeeded(_owner);

            const float dt = 1f / 60f;
            for (int i = 0; i < 240; i++)
            {
                bool thinking = i % 53 < 20; // arbitrary on/off pattern, identical for both.
                float strength = 0.4f;

                directorA.SetThinkingState(thinking, strength);
                directorB.SetThinkingState(thinking, strength);

                if (i == 30)
                {
                    directorA.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Reacting, 0.6f);
                    directorB.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Reacting, 0.6f);
                }
                else if (i == 120)
                {
                    directorA.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Interrupted, 0.7f);
                    directorB.TriggerBeatAccent(MicroExpressionDirector.BeatAccent.Interrupted, 0.7f);
                }

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
