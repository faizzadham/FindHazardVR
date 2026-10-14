using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="EmotionalGaitRunner" />: the on/off bookkeeping
    ///     around <see cref="EmotionGaitModulator" /> — only touches the delivered multiplier while
    ///     the feature is on, delivers exactly one 1x reset on the single tick it turns off, and
    ///     stays completely inert while off from the start.
    /// </summary>
    public sealed class EmotionalGaitRunnerTests
    {
        private const float Dt = 1f / 60f;
        private const float Range = 0.15f;

        private static EmotionReading Reading(string label, float score) =>
            new(label, score, EmotionReading.EmptyScores, 0f, 0f);

        [Test]
        public void Disabled_NeverConfigured_NeverInvokesDelegate()
        {
            var runner = new EmotionalGaitRunner();
            var calls = new List<float>();
            runner.Configure(calls.Add);

            // A static property cannot be passed by `in` reference (CS8156) — bind it to a local.
            EmotionReading neutral = EmotionReading.Neutral;
            for (int i = 0; i < 10; i++)
                runner.Tick(false, in neutral, Range, Dt);

            Assert.IsEmpty(calls, "off from the start must never touch the delivered multiplier.");
        }

        [Test]
        public void Enabled_InvokesDelegateEveryTick()
        {
            var runner = new EmotionalGaitRunner();
            var calls = new List<float>();
            runner.Configure(calls.Add);
            EmotionReading excited = Reading("surprise", 1f);

            for (int i = 0; i < 5; i++)
                runner.Tick(true, in excited, Range, Dt);

            Assert.AreEqual(5, calls.Count);
        }

        [Test]
        public void TurnedOff_DeliversExactlyOneResetTick_ThenGoesInert()
        {
            var runner = new EmotionalGaitRunner();
            var calls = new List<float>();
            runner.Configure(calls.Add);
            EmotionReading excited = Reading("surprise", 1f);

            runner.Tick(true, in excited, Range, Dt);
            runner.Tick(true, in excited, Range, Dt);
            int countWhileOn = calls.Count;

            // Turns off — exactly one more call, resetting to neutral (1x).
            runner.Tick(false, in excited, Range, Dt);
            Assert.AreEqual(countWhileOn + 1, calls.Count);
            Assert.AreEqual(1f, calls[^1]);

            // Off on subsequent ticks must not touch the delegate again.
            runner.Tick(false, in excited, Range, Dt);
            runner.Tick(false, in excited, Range, Dt);
            Assert.AreEqual(countWhileOn + 1, calls.Count);
        }

        [Test]
        public void Reset_ClearsActiveFlag_SoTurningOffAgainStaysInert()
        {
            var runner = new EmotionalGaitRunner();
            var calls = new List<float>();
            runner.Configure(calls.Add);
            EmotionReading excited = Reading("surprise", 1f);

            runner.Tick(true, in excited, Range, Dt);
            runner.Reset();
            calls.Clear();

            runner.Tick(false, in excited, Range, Dt);
            Assert.IsEmpty(calls, "Reset() must clear the active flag exactly like a real turn-off tick already did.");
        }

        [Test]
        public void NoDelegateConfigured_DoesNotThrow()
        {
            var runner = new EmotionalGaitRunner();
            EmotionReading excited = Reading("surprise", 1f);
            Assert.DoesNotThrow(() => runner.Tick(true, in excited, Range, Dt));
            Assert.DoesNotThrow(() => runner.Tick(false, in excited, Range, Dt));
        }
    }
}
