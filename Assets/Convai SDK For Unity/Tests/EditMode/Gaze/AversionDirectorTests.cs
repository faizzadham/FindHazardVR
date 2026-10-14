using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class AversionDirectorTests
    {
        private const float Dt = 1f / 60f;

        private AversionDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _director = new AversionDirector();
            _random = new DeterministicEmbodimentRandom(77u);
        }

        private (int beats, float maxOffset) Run(
            GazeAversionMode mode, float strength, float seconds, bool engaged = true,
            GazeAversionBias bias = GazeAversionBias.CognitiveDefault)
        {
            int beats = 0;
            bool wasAverting = false;
            float maxOffset = 0f;

            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                _director.Tick(mode, strength, bias, engaged, Dt, ref _random);
                if (_director.IsAverting && !wasAverting) beats++;
                wasAverting = _director.IsAverting;
                maxOffset = Mathf.Max(maxOffset, _director.Offset.magnitude);
            }

            return (beats, maxOffset);
        }

        [Test]
        public void CognitiveAversion_ProducesLookAwayBeats()
        {
            (int beats, float maxOffset) = Run(GazeAversionMode.Cognitive, 0.7f, 30f);

            Assert.That(beats, Is.GreaterThanOrEqualTo(3), "Thinking must break contact in beats.");
            Assert.That(maxOffset, Is.GreaterThan(8f), "Cognitive look-aways are visible, not micro.");
        }

        [Test]
        public void CognitiveBeats_BiasUpward()
        {
            bool sawUpwardBeat = false;
            for (int i = 0; i < 3600; i++)
            {
                _director.Tick(GazeAversionMode.Cognitive, 0.8f, GazeAversionBias.CognitiveDefault, true, Dt, ref _random);
                if (_director.IsAverting && _director.Offset.y > 4f) sawUpwardBeat = true;
            }
            Assert.IsTrue(sawUpwardBeat, "Cognitive aversion looks up ('recalling') at least sometimes.");
        }

        [Test]
        public void NoneMode_KeepsUnbrokenContact()
        {
            (int beats, float maxOffset) = Run(GazeAversionMode.None, 1f, 20f);

            Assert.That(beats, Is.EqualTo(0));
            Assert.That(maxOffset, Is.EqualTo(0f), "Speaking default: full lock, no breaks.");
        }

        [Test]
        public void ZeroStrength_KeepsUnbrokenContact()
        {
            (int beats, _) = Run(GazeAversionMode.Natural, 0f, 20f);
            Assert.That(beats, Is.EqualTo(0));
        }

        [Test]
        public void Disengaged_EasesOffsetBackToZero()
        {
            Run(GazeAversionMode.Cognitive, 1f, 6f);

            for (int i = 0; i < 120; i++)
                _director.Tick(GazeAversionMode.Cognitive, 1f, GazeAversionBias.CognitiveDefault, engaged: false, Dt, ref _random);

            Assert.That(_director.Offset.magnitude, Is.LessThan(0.1f));
            Assert.IsFalse(_director.IsAverting);
        }

        [Test]
        public void StrengthScale_ChangesBeatFrequency()
        {
            (int weakBeats, _) = Run(GazeAversionMode.Cognitive, 0.15f, 40f);

            _director.Reset();
            _random = new DeterministicEmbodimentRandom(77u);
            (int strongBeats, _) = Run(GazeAversionMode.Cognitive, 1f, 40f);

            Assert.That(strongBeats, Is.GreaterThan(weakBeats),
                "Higher strength must break contact more often.");
        }

        // ── Emotional gaze signature: AversionBias ─────────────────────────

        [Test]
        public void CognitiveDefaultBias_IsEnumZero_SoOlderSerializedRowsStayCompatible()
        {
            // EmotionGazeModifier rows serialized before AversionBias existed have no value for it;
            // Unity default-inits missing serialized enum fields to 0, which must resolve to
            // the legacy mode-based direction pick, not a new biased shape.
            Assert.That((int)GazeAversionBias.CognitiveDefault, Is.EqualTo(0));
        }

        [Test]
        public void UpBias_ProducesUpwardBeatsOnly()
        {
            bool sawBeat = false;
            for (int i = 0; i < 3600; i++)
            {
                _director.Tick(GazeAversionMode.Natural, 0.8f, GazeAversionBias.Up, true, Dt, ref _random);
                if (_director.IsAverting)
                {
                    sawBeat = true;
                    Assert.That(_director.Offset.y, Is.GreaterThan(0f), "Up bias must pitch upward only.");
                }
            }
            Assert.IsTrue(sawBeat, "Expected at least one aversion beat.");
        }

        [Test]
        public void DownBias_ProducesDownwardBeatsOnly()
        {
            bool sawBeat = false;
            for (int i = 0; i < 3600; i++)
            {
                _director.Tick(GazeAversionMode.Natural, 0.8f, GazeAversionBias.Down, true, Dt, ref _random);
                if (_director.IsAverting)
                {
                    sawBeat = true;
                    Assert.That(_director.Offset.y, Is.LessThan(0f), "Down bias must pitch downward only.");
                }
            }
            Assert.IsTrue(sawBeat, "Expected at least one aversion beat.");
        }

        [Test]
        public void SideBias_ProducesLevelSidewaysBeats()
        {
            bool sawBeat = false;
            for (int i = 0; i < 3600; i++)
            {
                _director.Tick(GazeAversionMode.Cognitive, 0.8f, GazeAversionBias.Side, true, Dt, ref _random);
                if (_director.IsAverting)
                {
                    sawBeat = true;
                    Assert.That(Mathf.Abs(_director.Offset.y), Is.LessThanOrEqualTo(2f),
                        "Side bias keeps the beat level (small pitch only).");
                    // The beat target (EyeOffset), not the eased head ramp (Offset), carries the
                    // shape contract: the ramp passes through arbitrarily small magnitudes.
                    Assert.That(Mathf.Abs(_director.EyeOffset.x), Is.GreaterThan(2f),
                        "Side bias must produce meaningful yaw displacement.");
                }
            }
            Assert.IsTrue(sawBeat, "Expected at least one aversion beat.");
        }

        [Test]
        public void DownSideBias_ProducesDownwardAndSidewaysBeats()
        {
            bool sawBeat = false;
            for (int i = 0; i < 3600; i++)
            {
                _director.Tick(GazeAversionMode.Natural, 0.8f, GazeAversionBias.DownSide, true, Dt, ref _random);
                if (_director.IsAverting)
                {
                    sawBeat = true;
                    Assert.That(_director.Offset.y, Is.LessThan(0f), "DownSide bias must pitch downward.");
                    // The beat target (EyeOffset) carries the shape contract; the eased head
                    // ramp (Offset) passes through arbitrarily small x while it climbs.
                    Assert.That(Mathf.Abs(_director.EyeOffset.x), Is.GreaterThan(2f),
                        "DownSide bias must also carry a side component.");
                }
            }
            Assert.IsTrue(sawBeat, "Expected at least one aversion beat.");
        }

        [Test]
        public void CognitiveDefaultBias_ForceCognitiveBeatShapeIsUnaffectedByBias()
        {
            // ForceCognitiveBeat (turn-taking planning break) never takes a bias parameter — it
            // always samples the classic up/side cognitive shape regardless of what bias the
            // caller would otherwise be feeding Tick(), matching the plan's "a planning break is
            // cognitive, not emotional" rule.
            _director.ForceCognitiveBeat(1.5f, 0.9f, ref _random);

            Assert.IsTrue(_director.IsAverting);
            Assert.That(_director.EyeOffset.y, Is.GreaterThan(0f),
                "Forced planning-break beats keep the cognitive up shape.");
        }

        [Test]
        public void CognitiveDefaultBias_IsDeterministic()
        {
            var randomA = new DeterministicEmbodimentRandom(555u);
            var randomB = new DeterministicEmbodimentRandom(555u);
            var directorA = new AversionDirector();
            var directorB = new AversionDirector();

            for (int i = 0; i < 300; i++)
            {
                directorA.Tick(GazeAversionMode.Cognitive, 0.8f, GazeAversionBias.CognitiveDefault, true, Dt, ref randomA);
                directorB.Tick(GazeAversionMode.Cognitive, 0.8f, GazeAversionBias.CognitiveDefault, true, Dt, ref randomB);

                Assert.That(directorA.Offset.x, Is.EqualTo(directorB.Offset.x));
                Assert.That(directorA.Offset.y, Is.EqualTo(directorB.Offset.y));
                Assert.That(directorA.IsAverting, Is.EqualTo(directorB.IsAverting));
            }
        }

        // ------------------------------------------------------------------ beat size

        /// <summary>
        ///     How big a look-away is allowed to get. Sampled at full strength — the multiplier
        ///     is <c>Lerp(0.6..1)</c> for cognitive and <c>Lerp(0.5..1)</c> for natural, so
        ///     strength 1 is the widest the sampler can ever go and the only strength at which
        ///     an upper bound means anything.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Bounded on both sides deliberately. An upper bound alone is satisfied by a
        ///         director that has stopped producing beats at all, and this suite's other tests
        ///         only check that beats happen, not that they still reach the top of their range.
        ///     </para>
        ///     <para>
        ///         The offsets read here are <see cref="AversionDirector.EyeOffset" />: the raw
        ///         sampled beat, before the head's eased share of it. That is the quantity the
        ///         ranges are authored in — <see cref="AversionDirector.Offset" /> would also be
        ///         measuring the ease's progress through a beat that may end before it arrives.
        ///     </para>
        /// </remarks>
        /// <param name="mode">Which beat shape to sample.</param>
        /// <param name="maxYaw">The widest yaw the range allows.</param>
        /// <param name="minObservedYaw">A yaw the sampler must reach in 500 draws, so the bound bites downward too.</param>
        /// <param name="maxPitch">The largest pitch magnitude the range allows.</param>
        /// <param name="minObservedPitch">A pitch magnitude the sampler must reach in 500 draws.</param>
        [TestCase(GazeAversionMode.Cognitive, 14f, 13f, 10f, 9f)]
        [TestCase(GazeAversionMode.Natural, 9f, 8.5f, 6f, 5.5f)]
        public void BeatOffsets_StayWithinTheirAuthoredRange(
            GazeAversionMode mode, float maxYaw, float minObservedYaw, float maxPitch, float minObservedPitch)
        {
            float widestYaw = 0f;
            float widestPitch = 0f;

            for (int i = 0; i < 500; i++)
            {
                _director.ForceBeat(mode, 1f, 1f, ref _random);
                widestYaw = Mathf.Max(widestYaw, Mathf.Abs(_director.EyeOffset.x));
                widestPitch = Mathf.Max(widestPitch, Mathf.Abs(_director.EyeOffset.y));
            }

            Assert.That(widestYaw, Is.LessThanOrEqualTo(maxYaw),
                $"{mode} beats reached {widestYaw:0.0}° of yaw. A contact break is a glance aside, " +
                "not a look at something else — past this the head visibly leaves the conversation.");
            Assert.That(widestPitch, Is.LessThanOrEqualTo(maxPitch),
                $"{mode} beats reached {widestPitch:0.0}° of pitch.");

            Assert.That(widestYaw, Is.GreaterThanOrEqualTo(minObservedYaw),
                $"{mode} beats never got past {widestYaw:0.0}° of yaw in 500 draws — the range has " +
                "collapsed, and an aversion nobody can see is the same as none.");
            Assert.That(widestPitch, Is.GreaterThanOrEqualTo(minObservedPitch),
                $"{mode} beats never got past {widestPitch:0.0}° of pitch in 500 draws.");
        }
    }
}
