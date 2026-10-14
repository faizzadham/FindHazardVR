using System;
using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    [TestFixture]
    public class LipSyncFadeControllerTests
    {
        [Test]
        public void Begin_WithNullCurrentValues_StartsFadeWithoutThrowing()
        {
            // Arrange
            FadeController fadeController = new();

            // Act
            fadeController.Begin(null, 0.2f);

            // Assert
            Assert.IsTrue(fadeController.IsActive);
        }

        [Test]
        public void Tick_BeforeCompletion_ScalesOutputTowardZero()
        {
            // Arrange
            FadeController fadeController = new();
            float[] output = { 1f, 0.5f };
            fadeController.Begin(new[] { 1f, 0.5f }, 1f);

            // Act
            bool stillFading = fadeController.Tick(0.1f, output);

            // Assert
            Assert.IsTrue(stillFading);
            Assert.That(output[0], Is.LessThan(1f));
        }

        [Test]
        public void Tick_AfterFadeDuration_CompletesAndZerosOutput()
        {
            // Arrange
            FadeController fadeController = new();
            float[] output = { 1f, 0.5f };
            fadeController.Begin(new[] { 1f, 0.5f }, 0.1f);

            // Act
            bool stillFading = true;
            for (int i = 0; i < 4 && stillFading; i++) stillFading = fadeController.Tick(1f, output);

            // Assert
            Assert.IsFalse(stillFading);
            Assert.AreEqual(0f, output[0], 0.0001f);
            Assert.AreEqual(0f, output[1], 0.0001f);
        }

        // -- Settle: the natural close ------------------------------------------------------

        [Test]
        public void Settle_FromRest_ComesToRestWithinDurationWithoutOvershoot()
        {
            FadeController fade = new();
            float[] output = { 0.8f, 0.3f };
            fade.BeginSettle(new[] { 0.8f, 0.3f }, null, 0.35f);
            Assert.IsTrue(fade.IsSettling);

            float previous = output[0];
            bool active = true;
            for (int i = 0; i < 60 && active; i++)
            {
                active = fade.Tick(1f / 60f, output);
                Assert.That(output[0], Is.LessThanOrEqualTo(previous + 1e-5f), "a settle from rest never rises");
                Assert.That(output[0], Is.GreaterThanOrEqualTo(0f), "a settle never overshoots below rest");
                previous = output[0];
            }

            Assert.IsFalse(active, "the settle must finish within one and a half times its duration");
            Assert.AreEqual(0f, output[0]);
            Assert.AreEqual(0f, output[1]);
        }

        [Test]
        public void Settle_ContinuesTheMotionTheFramesLeftOffWith()
        {
            // The mouth was still opening at 2 units/s when the frames ran out...
            FadeController opening = new();
            float[] outputOpening = { 0.5f };
            opening.BeginSettle(new[] { 0.5f }, new[] { 2f }, 0.35f);
            opening.Tick(1f / 60f, outputOpening);
            Assert.That(outputOpening[0], Is.GreaterThan(0.5f),
                "a jaw still rising rises a little further before it comes back — no kink");

            // ...and one that was already closing keeps closing at about the same rate.
            FadeController closing = new();
            float[] outputClosing = { 0.5f };
            closing.BeginSettle(new[] { 0.5f }, new[] { -2f }, 0.35f);
            closing.Tick(1f / 60f, outputClosing);
            float expectedStep = 2f / 60f;
            Assert.That(0.5f - outputClosing[0], Is.EqualTo(expectedStep).Within(expectedStep * 0.5f),
                "the first step of the close matches the velocity the frames had");
        }

        [Test]
        public void Settle_VelocityIsContinuous_AcrossTheHandoverAndBetweenFrames()
        {
            // The frames were closing the mouth at 1 unit/s. The settle's first step must be that
            // same speed — that is the whole point of seeding it — and thereafter the speed may
            // only change gradually, never jump.
            FadeController fade = new();
            float[] output = { 1f };
            fade.BeginSettle(new[] { 1f }, new[] { -1f }, 0.35f);

            float dt = 1f / 60f;
            fade.Tick(dt, output);
            float firstStep = 1f - output[0];
            Assert.That(firstStep, Is.EqualTo(1f * dt).Within(0.006f),
                "the first step continues the frames' own speed");

            float last = output[0];
            float lastStep = firstStep;
            for (int i = 1; i < 30; i++)
            {
                fade.Tick(dt, output);
                float step = last - output[0];
                Assert.That(Math.Abs(step - lastStep), Is.LessThan(0.035f),
                    $"step {i} changed abruptly: {lastStep:F4} -> {step:F4}");
                lastStep = step;
                last = output[0];
            }
        }

        [Test]
        public void Reset_AfterBegin_ClearsActiveStateAndProgress()
        {
            // Arrange
            FadeController fadeController = new();
            fadeController.Begin(new[] { 1f }, 0.2f);

            // Act
            fadeController.Reset();

            // Assert
            Assert.IsFalse(fadeController.IsActive);
            Assert.AreEqual(0f, fadeController.Progress, 0.0001f);
        }
    }
}
