using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using System;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class BodyAnimationLayerArbiterTests
    {
        [Test]
        public void FullBodyAction_DucksEveryOverlayContinuously()
        {
            var arbiter = new BodyAnimationLayerArbiter();
            var input = new LayerArbitrationInput(1f, 1f, 0.4f, 1f, 0.7f, 0.8f, 0.4f, true);

            arbiter.Resolve(in input);

            Assert.That(arbiter.GetFinalWeight(LayerPorts.Locomotion), Is.EqualTo(1f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.Action), Is.EqualTo(0.4f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.Talk), Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.Pointing), Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.TalkBeat), Is.LessThan(0.3f));
        }

        [Test]
        public void UpperBodyAction_WinsArmsWhilePreservingBaseLocomotion()
        {
            var arbiter = new BodyAnimationLayerArbiter();
            var input = new LayerArbitrationInput(1f, 0.8f, 1f, 1f, 0.6f, 1f, 0f, true);

            arbiter.Resolve(in input);

            Assert.That(arbiter.GetFinalWeight(LayerPorts.Locomotion), Is.EqualTo(1f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.Action), Is.EqualTo(1f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.Pointing), Is.Zero);
            Assert.That(arbiter.GetFinalWeight(LayerPorts.Talk), Is.Zero);
            Assert.That(arbiter.GetFinalWeight(LayerPorts.TalkMoving), Is.Zero);
            Assert.That(arbiter.GetFinalWeight(LayerPorts.TalkBeat), Is.Zero);
        }

        [Test]
        public void ConversationalHold_DoesNotSuppressTalk()
        {
            var arbiter = new BodyAnimationLayerArbiter();
            var input = new LayerArbitrationInput(1f, 0.75f, 1f, 0f, 0.5f, 0.25f, 0f, false);

            arbiter.Resolve(in input);

            Assert.That(arbiter.GetFinalWeight(LayerPorts.Talk), Is.EqualTo(0.75f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.TalkMoving), Is.EqualTo(0.5f));
            Assert.That(arbiter.GetFinalWeight(LayerPorts.TalkBeat), Is.EqualTo(0.25f));
        }

        [Test]
        public void Resolve_SteadyState_AllocatesZeroBytes()
        {
            var arbiters = new BodyAnimationLayerArbiter[25];
            for (int i = 0; i < arbiters.Length; i++)
                arbiters[i] = new BodyAnimationLayerArbiter();
            var input = new LayerArbitrationInput(1f, 0.7f, 0.2f, 0.4f, 0.5f, 0.3f, 0f, true);

            for (int i = 0; i < arbiters.Length; i++)
                arbiters[i].Resolve(in input);
            long allocated = AllocationCounter.Measure(() =>
            {
                for (int frame = 0; frame < 1000; frame++)
                    for (int i = 0; i < arbiters.Length; i++)
                        arbiters[i].Resolve(in input);
            });

            Assert.That(allocated, Is.Zero);
        }
    }
}
