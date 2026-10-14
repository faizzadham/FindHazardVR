using Convai.Modules.Gaze.Core.Behaviors;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The eyes' short drop and lift as a character comes to rest at the end of a walk.
    /// </summary>
    public sealed class ArrivalSettleDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float Drop = 4f;
        private const float Duration = 0.7f;

        private ArrivalSettleDirector _director;

        [SetUp]
        public void SetUp() => _director = new ArrivalSettleDirector();

        private float RunPeak(float seconds, bool traveling)
        {
            float deepest = 0f;
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                _director.Tick(traveling, Drop, Duration, Dt);
                deepest = Mathf.Min(deepest, _director.PitchOffsetDegrees);
            }

            return deepest;
        }

        [Test]
        public void WhileWalking_TheEyesAreUntouched()
        {
            RunPeak(3f, traveling: true);

            Assert.That(_director.PitchOffsetDegrees, Is.EqualTo(0f));
            Assert.IsFalse(_director.IsSettling);
        }

        [Test]
        public void OnComingToRest_TheEyesDropAndComeBack()
        {
            RunPeak(1f, traveling: true);

            float deepest = RunPeak(Duration * 0.6f, traveling: false);
            Assert.That(deepest, Is.LessThan(-1f), "The eyes visibly drop.");
            Assert.That(deepest, Is.GreaterThanOrEqualTo(-Drop - 0.01f), "…but never past the authored depth.");

            RunPeak(Duration, traveling: false);
            Assert.That(_director.PitchOffsetDegrees, Is.EqualTo(0f), "…and come back up.");
            Assert.IsFalse(_director.IsSettling);
        }

        [Test]
        public void StandingStill_NeverRetriggers()
        {
            RunPeak(1f, traveling: true);
            RunPeak(Duration + 0.5f, traveling: false);

            float deepest = RunPeak(10f, traveling: false);

            Assert.That(deepest, Is.EqualTo(0f),
                "It is one beat per arrival, not something that keeps happening while it stands there.");
        }

        [Test]
        public void ASecondWalk_SettlesAgain()
        {
            RunPeak(1f, traveling: true);
            RunPeak(Duration + 0.5f, traveling: false);

            RunPeak(1f, traveling: true);
            float deepest = RunPeak(Duration * 0.6f, traveling: false);

            Assert.That(deepest, Is.LessThan(-1f));
        }

        [Test]
        public void ADepthOfZero_IsTheOffSwitch()
        {
            for (int i = 0; i < 60; i++) _director.Tick(true, 0f, Duration, Dt);
            for (int i = 0; i < 60; i++)
            {
                _director.Tick(false, 0f, Duration, Dt);
                Assert.That(_director.PitchOffsetDegrees, Is.EqualTo(0f));
            }
        }

        /// <summary>
        ///     The envelope starts and ends at rest and peaks once. A settle that began or ended
        ///     abruptly would read as a flick rather than a body coming to rest.
        /// </summary>
        [Test]
        public void TheEnvelopeStartsAndEndsAtRest_AndPeaksOnce()
        {
            Assert.That(ArrivalSettleDirector.Envelope(0f), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(ArrivalSettleDirector.Envelope(1f), Is.EqualTo(0f).Within(1e-4f));

            int risings = 0;
            float previous = ArrivalSettleDirector.Envelope(0f);
            bool wasRising = true;
            for (int i = 1; i <= 200; i++)
            {
                float value = ArrivalSettleDirector.Envelope(i / 200f);
                bool rising = value > previous;
                if (rising && !wasRising) risings++;
                wasRising = rising;
                previous = value;
            }

            Assert.That(risings, Is.EqualTo(0), "One peak — it goes down and comes back, once.");
        }

        /// <summary>The drop is quicker than the lift, the way a settling movement decays.</summary>
        [Test]
        public void TheDropIsQuickerThanTheLift()
        {
            Assert.That(ArrivalSettleDirector.Envelope(0.35f), Is.EqualTo(1f).Within(0.01f),
                "The bottom of the beat comes early.");
        }
    }
}
