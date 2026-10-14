using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Truth-table coverage for <see cref="HeadGestureArbiter" />:
    ///     no-channel passthrough (the bit-identity guarantee the controller wiring relies on),
    ///     external-wins arbitration, the shared post-external refractory, the aversion fade
    ///     gate, weight scaling, determinism, and a zero-allocation steady-state tick.
    /// </summary>
    public sealed class HeadGestureArbiterTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>Minimal test double for <see cref="IHeadGestureChannel" /> — no consumer bookkeeping needed here.</summary>
        private sealed class FakeChannel : IHeadGestureChannel
        {
            public bool HasOffset;
            public HeadGestureOffset Offset;

            public void RegisterConsumer(object consumer) { }
            public void UnregisterConsumer(object consumer) { }

            public bool TryGetOffset(out HeadGestureOffset offset)
            {
                offset = HasOffset ? Offset : HeadGestureOffset.None;
                return HasOffset;
            }
        }

        private HeadGestureArbiter _arbiter;

        [SetUp]
        public void SetUp()
        {
            _arbiter = new HeadGestureArbiter();
        }

        private void Tick(Vector2 backchannelOffset, IHeadGestureChannel channel, bool isAverting = false, float dt = Dt)
        {
            _arbiter.SenseExternal(channel, isAverting, dt);
            _arbiter.Compose(backchannelOffset);
        }

        [Test]
        public void NoChannel_OutputEqualsBackchannelOffset_Exactly()
        {
            var backchannel = new Vector2(1.25f, -3.75f);

            Tick(backchannel, null);

            Assert.That(_arbiter.Offset.x, Is.EqualTo(backchannel.x), "X must be bit-equal, not merely close.");
            Assert.That(_arbiter.Offset.y, Is.EqualTo(backchannel.y), "Y must be bit-equal, not merely close.");
            Assert.That(_arbiter.RollDegrees, Is.EqualTo(0f));
            Assert.IsFalse(_arbiter.ExternalActive);
        }

        [Test]
        public void ChannelWithNoActiveOffset_OutputEqualsBackchannelOffset_Exactly()
        {
            var channel = new FakeChannel { HasOffset = false };
            var backchannel = new Vector2(2f, 4f);

            Tick(backchannel, channel);

            Assert.That(_arbiter.Offset.x, Is.EqualTo(backchannel.x));
            Assert.That(_arbiter.Offset.y, Is.EqualTo(backchannel.y));
            Assert.IsFalse(_arbiter.ExternalActive);
        }

        [Test]
        public void ChannelVanishingDuringRefractory_ResetsToPurePassthrough_Immediately()
        {
            // Adversarial-review regression: if the producer unregisters while the
            // post-completion refractory is still draining, the no-channel state must be
            // bit-identical passthrough IMMEDIATELY — a stale refractory must not keep
            // suppressing the backchannel after the channel is gone.
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(0f, 4f, 0f, 1f) };
            Tick(Vector2.zero, channel);
            Assert.IsTrue(_arbiter.ExternalActive, "Precondition: external program active.");

            channel.HasOffset = false; // program completes — refractory arms
            Tick(Vector2.zero, channel);
            Assert.IsTrue(_arbiter.ExternalActive, "Precondition: refractory keeps ExternalActive true while the channel exists.");

            var backchannel = new Vector2(1.5f, -2.5f);
            Tick(backchannel, null); // producer unregistered

            Assert.IsFalse(_arbiter.ExternalActive,
                "No channel means no external state — the stale refractory must reset at once.");
            Assert.That(_arbiter.Offset.x, Is.EqualTo(backchannel.x), "Passthrough must be bit-equal.");
            Assert.That(_arbiter.Offset.y, Is.EqualTo(backchannel.y), "Passthrough must be bit-equal.");
            Assert.That(_arbiter.RollDegrees, Is.EqualTo(0f));
        }

        [Test]
        public void ChannelWithZeroWeight_TreatedAsInactive_BackchannelPassesThrough()
        {
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(5f, 5f, 5f, 0f) };
            var backchannel = new Vector2(1f, 1f);

            Tick(backchannel, channel);

            Assert.That(_arbiter.Offset, Is.EqualTo(backchannel));
            Assert.IsFalse(_arbiter.ExternalActive, "Zero weight must not count as an active external program.");
        }

        [Test]
        public void ExternalActive_WinsOverBackchannel_AndRaisesSuppressionFlag()
        {
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(3f, -2f, 1f, 1f) };
            var backchannel = new Vector2(9f, 9f); // must be fully ignored while external wins

            Tick(backchannel, channel);

            Assert.That(_arbiter.Offset.x, Is.EqualTo(-2f).Within(0.001f), "Yaw axis maps from HeadGestureOffset.YawDegrees.");
            Assert.That(_arbiter.Offset.y, Is.EqualTo(3f).Within(0.001f), "Pitch axis maps from HeadGestureOffset.PitchDegrees.");
            Assert.That(_arbiter.RollDegrees, Is.EqualTo(1f).Within(0.001f));
            Assert.IsTrue(_arbiter.ExternalActive, "The controller ORs this into the backchannel's suppressed input.");
        }

        [Test]
        public void ExternalCompletes_BackchannelStaysSuppressedForCooldown_ThenReleases()
        {
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(0f, 0f, 0f, 1f) };
            Tick(Vector2.zero, channel);
            Assert.IsTrue(_arbiter.ExternalActive, "Sanity: external is active while playing.");

            channel.HasOffset = false; // program completes

            // Refractory window (~0.75s) must keep ExternalActive true. The arbiter itself still
            // passes the given backchannel value through during the cooldown — actually
            // suppressing the nod is the CONTROLLER's job (it feeds ExternalActive into
            // BackchannelDirector's own suppressed input, which is what drives that value to
            // zero in the real pipeline); this test isolates the cooldown-duration guarantee.
            var probe = new Vector2(2f, 2f);
            int stepsInWindow = Mathf.FloorToInt(0.7f / Dt);
            for (int i = 0; i < stepsInWindow; i++)
            {
                Tick(probe, channel);
                Assert.IsTrue(_arbiter.ExternalActive, $"Still inside the cooldown at step {i}.");
                Assert.That(_arbiter.Offset, Is.EqualTo(probe),
                    "The arbiter passes the given backchannel value through verbatim during the " +
                    "cooldown — it is the CALLER's job to suppress that value at the source.");
            }

            // Well past the cooldown, ExternalActive must release.
            int stepsPastWindow = Mathf.CeilToInt(0.2f / Dt);
            bool released = false;
            for (int i = 0; i < stepsPastWindow; i++)
            {
                Tick(Vector2.zero, channel);
                if (!_arbiter.ExternalActive)
                {
                    released = true;
                    break;
                }
            }

            Assert.IsTrue(released, "The refractory must release ExternalActive well within 0.9s of completion.");
        }

        [Test]
        public void AversionDuringExternal_FadesContributionToZero_MonotonicallyAndWithoutSnap()
        {
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(10f, 0f, 0f, 1f) };

            // Establish full-weight external output first (no aversion).
            for (int i = 0; i < 10; i++)
                Tick(Vector2.zero, channel, isAverting: false);
            float before = Mathf.Abs(_arbiter.Offset.y);
            Assert.That(before, Is.EqualTo(10f).Within(0.01f), "Sanity: full external pitch before aversion.");

            float previous = before;
            int steps = Mathf.CeilToInt(0.3f / Dt);
            for (int i = 0; i < steps; i++)
            {
                Tick(Vector2.zero, channel, isAverting: true);
                float current = Mathf.Abs(_arbiter.Offset.y);
                Assert.That(current, Is.LessThanOrEqualTo(previous + 1e-4f), "Fade must be monotonically decreasing.");
                previous = current;
            }

            Assert.That(previous, Is.EqualTo(0f).Within(0.01f), "Fully faded out within ~0.2s of aversion starting.");
        }

        [Test]
        public void AversionEnds_ExternalContributionFadesBackIn()
        {
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(10f, 0f, 0f, 1f) };

            for (int i = 0; i < 20; i++)
                Tick(Vector2.zero, channel, isAverting: true);
            Assert.That(Mathf.Abs(_arbiter.Offset.y), Is.EqualTo(0f).Within(0.01f), "Sanity: faded out.");

            float previous = 0f;
            int steps = Mathf.CeilToInt(0.3f / Dt);
            for (int i = 0; i < steps; i++)
            {
                Tick(Vector2.zero, channel, isAverting: false);
                float current = Mathf.Abs(_arbiter.Offset.y);
                Assert.That(current, Is.GreaterThanOrEqualTo(previous - 1e-4f), "Fade-in must be monotonically increasing.");
                previous = current;
            }

            Assert.That(previous, Is.EqualTo(10f).Within(0.05f), "Fully faded back in within ~0.2s of aversion ending.");
        }

        [Test]
        public void WeightScaling_ScalesOutputLinearly()
        {
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(8f, 4f, 2f, 0.5f) };

            Tick(Vector2.zero, channel);

            Assert.That(_arbiter.Offset.y, Is.EqualTo(4f).Within(0.001f), "Pitch scaled by weight 0.5.");
            Assert.That(_arbiter.Offset.x, Is.EqualTo(2f).Within(0.001f), "Yaw scaled by weight 0.5.");
            Assert.That(_arbiter.RollDegrees, Is.EqualTo(1f).Within(0.001f), "Roll scaled by weight 0.5.");
        }

        [Test]
        public void Determinism_SameInputsProduceSameOutputs()
        {
            var channelA = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(6f, -3f, 2f, 0.8f) };
            var channelB = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(6f, -3f, 2f, 0.8f) };
            var arbiterA = new HeadGestureArbiter();
            var arbiterB = new HeadGestureArbiter();

            for (int i = 0; i < 30; i++)
            {
                bool averting = i % 7 == 0;
                arbiterA.SenseExternal(channelA, averting, Dt);
                arbiterA.Compose(new Vector2(1f, 1f));
                arbiterB.SenseExternal(channelB, averting, Dt);
                arbiterB.Compose(new Vector2(1f, 1f));

                Assert.That(arbiterA.Offset, Is.EqualTo(arbiterB.Offset), $"Tick {i}: identical inputs must produce identical outputs.");
                Assert.That(arbiterA.RollDegrees, Is.EqualTo(arbiterB.RollDegrees), $"Tick {i}: roll must match too.");
                Assert.That(arbiterA.ExternalActive, Is.EqualTo(arbiterB.ExternalActive), $"Tick {i}: suppression flag must match too.");
            }
        }

        [Test]
        public void SteadyStateTick_AllocatesNothing()
        {
            var channel = new FakeChannel { HasOffset = true, Offset = new HeadGestureOffset(2f, 1f, 0.5f, 1f) };
            var backchannelOffset = new Vector2(0.5f, 0.5f);

            // Warm up (JIT, first-touch) before measuring, mirroring the BodyLanguage zero-alloc
            // gate's own pattern (Tests/EditMode/BodyLanguage/BodyLanguageZeroAllocTests.cs).
            for (int i = 0; i < 500; i++)
            {
                channel.HasOffset = i % 20 != 0; // exercise both the active and inactive branch
                _arbiter.SenseExternal(channel, i % 13 == 0, Dt);
                _arbiter.Compose(backchannelOffset);
            }

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    channel.HasOffset = i % 20 != 0;
                    _arbiter.SenseExternal(channel, i % 13 == 0, Dt);
                    _arbiter.Compose(backchannelOffset);
                }
            });

            Assert.That(allocationCount, Is.EqualTo(0L), "The arbiter's steady-state tick must produce zero managed allocation events.");
        }
    }
}
