using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     An aversion beat moves the head, so it must move it at the speed this head moves.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The beat offset is composed downstream of the actuator, so the ladder's duration law
    ///         never sees it and the director's own ease is the only thing shaping it. That ease ran
    ///         at one rate for every beat, so a 22-degree cognitive look-away finished in about the
    ///         same third of a second as a 6-degree conversational one — nearly four times the
    ///         speed a deliberate look that size would travel at. Coming straight after the
    ///         character stopped speaking it read as a flinch.
    ///     </para>
    /// </remarks>
    internal sealed class AversionBeatSpeedTests
    {
        private const float BaseSeconds = 0.45f;
        private const float SecondsPerDegree = 0.0125f;

        /// <summary>Seconds for the head to cover 95% of a beat of this size.</summary>
        private static float SettleSeconds(float beatDegrees, float strength)
        {
            var random = new DeterministicEmbodimentRandom(12345u);
            var director = new AversionDirector();
            director.SetHeadMovementLaw(BaseSeconds, SecondsPerDegree);
            director.ForceBeat(GazeAversionMode.Cognitive, 10f, strength, ref random);

            const float dt = 1f / 120f;
            float elapsed = 0f;
            director.Tick(GazeAversionMode.Cognitive, strength, GazeAversionBias.CognitiveDefault, true, dt, ref random);
            float peak = director.EyeOffset.magnitude;
            Assert.That(peak, Is.GreaterThan(0.5f), "The beat must have a size to travel.");

            while (elapsed < 5f)
            {
                director.Tick(GazeAversionMode.Cognitive, strength, GazeAversionBias.CognitiveDefault, true, dt, ref random);
                elapsed += dt;
                if (director.Offset.magnitude >= peak * 0.95f) return elapsed;
            }

            return float.PositiveInfinity;
        }

        [Test]
        public void ABiggerBeat_TakesLongerThanASmallerOne()
        {
            float small = SettleSeconds(6f, 0.2f);
            float large = SettleSeconds(22f, 1f);

            Assert.That(large, Is.GreaterThan(small),
                "One rate for every beat is what made the large ones read as flinches.");
        }

        [Test]
        public void ABeat_NeverArrivesFasterThanADeliberateLookOfThatSize()
        {
            var random = new DeterministicEmbodimentRandom(999u);
            var director = new AversionDirector();
            director.SetHeadMovementLaw(BaseSeconds, SecondsPerDegree);
            director.ForceBeat(GazeAversionMode.Cognitive, 10f, 1f, ref random);

            const float dt = 1f / 120f;
            director.Tick(GazeAversionMode.Cognitive, 1f, GazeAversionBias.CognitiveDefault, true, dt, ref random);
            float amplitude = director.EyeOffset.magnitude;

            // What the head chain would take to turn this far on purpose.
            float deliberate = BaseSeconds + SecondsPerDegree * amplitude;

            float elapsed = dt;
            while (elapsed < 5f && director.Offset.magnitude < amplitude * 0.95f)
            {
                director.Tick(GazeAversionMode.Cognitive, 1f, GazeAversionBias.CognitiveDefault, true, dt, ref random);
                elapsed += dt;
            }

            Assert.That(elapsed, Is.GreaterThanOrEqualTo(deliberate * 0.8f),
                $"A {amplitude:0.0} degree beat arrived in {elapsed:0.00}s; a look that size takes "
                + $"{deliberate:0.00}s. Glancing away must not outrun turning to look.");
        }

        [Test]
        public void TheEyesStillGetTheBeatAsAStep()
        {
            var random = new DeterministicEmbodimentRandom(7u);
            var director = new AversionDirector();
            director.SetHeadMovementLaw(BaseSeconds, SecondsPerDegree);
            director.ForceBeat(GazeAversionMode.Cognitive, 10f, 1f, ref random);
            director.Tick(GazeAversionMode.Cognitive, 1f, GazeAversionBias.CognitiveDefault, true, 1f / 120f, ref random);

            Assert.That(director.EyeOffset.magnitude, Is.GreaterThan(director.Offset.magnitude),
                "Eyes are ballistic: they take the raw step while the head eases onto it. Easing "
                + "the eyes too would smear one glance into a stutter of catch-up saccades.");
        }

        [Test]
        public void TheHeadsShareOfABeat_LeavesFromRest()
        {
            // The exponential ease this pins against left at peak slew: 1.7° in the first frame
            // of a beat, sixty degrees a second from a standing start — the "sudden little head
            // reposition" a contributor trace showed every few seconds of listening.
            var random = new DeterministicEmbodimentRandom(31u);
            var director = new AversionDirector();
            director.SetHeadMovementLaw(BaseSeconds, SecondsPerDegree);
            director.ForceBeat(GazeAversionMode.Cognitive, 10f, 1f, ref random);
            const float dt = 1f / 60f;

            director.Tick(GazeAversionMode.Cognitive, 1f, GazeAversionBias.CognitiveDefault, true, dt, ref random);
            float firstFrame = director.Offset.magnitude;
            director.Tick(GazeAversionMode.Cognitive, 1f, GazeAversionBias.CognitiveDefault, true, dt, ref random);
            float secondFrame = director.Offset.magnitude;

            Assert.That(firstFrame / dt, Is.LessThan(8f),
                $"First-frame head speed was {firstFrame / dt:0.0} deg/s; a movement starts from rest.");
            Assert.That(secondFrame - firstFrame, Is.GreaterThan(firstFrame),
                "It accelerates into the beat rather than starting at speed.");
        }

        [Test]
        public void ABeatEnds_WhenTheStateThatDrewItEnds()
        {
            var random = new DeterministicEmbodimentRandom(32u);
            var director = new AversionDirector();
            director.SetHeadMovementLaw(BaseSeconds, SecondsPerDegree);
            director.ForceBeat(GazeAversionMode.Cognitive, 10f, 1f, ref random);
            const float dt = 1f / 60f;
            for (int i = 0; i < 30; i++)
                director.Tick(GazeAversionMode.Cognitive, 1f, GazeAversionBias.CognitiveDefault, true, dt, ref random);
            Assert.That(director.EyeOffset.magnitude, Is.GreaterThan(5f), "Sanity: a thinking beat is in flight.");

            // Thinking ends, listening begins: the twenty-degree look-away must not carry over.
            director.Tick(GazeAversionMode.Natural, 0.08f, GazeAversionBias.CognitiveDefault, true, dt, ref random);
            Assert.That(director.EyeOffset, Is.EqualTo(Vector2.zero), "The eyes come back the frame the mode changes.");
            Assert.IsFalse(director.IsAverting);
        }
    }
}
