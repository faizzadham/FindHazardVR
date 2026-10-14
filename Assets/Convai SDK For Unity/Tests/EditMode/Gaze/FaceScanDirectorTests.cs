using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Listener mouth-bias face scan: while the player is speaking, FaceScanDirector
    ///     biases its landmark pick toward the mouth vertex; at rest (or with the bias disabled)
    ///     it stays byte-identical to the legacy uniform eye-eye-mouth pick.
    /// </summary>
    public sealed class FaceScanDirectorTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private FaceScanDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            SetSerializedFloat("faceScanIntervalMean", 0.3f);
            SetSerializedFloat("faceScanIntervalJitter", 0f);
            SetSerializedFloat("faceScanRadiusDegrees", 2f);
            _director = new FaceScanDirector();
            _random = new DeterministicEmbodimentRandom(1234u);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private void SetSerializedFloat(string field, float value)
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, field).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private void SetSerializedBool(string field, bool value)
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, field).boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool IsMouth(Vector2 offset) => offset.y < 0f;

        /// <summary>Runs picks, returning the sequence of distinct landmark offsets chosen (one entry per pick, in order).</summary>
        private List<Vector2> CollectPicks(
            FaceScanDirector director,
            ref DeterministicEmbodimentRandom random,
            int pickCount,
            bool playerSpeaking)
        {
            var picks = new List<Vector2>(pickCount);
            Vector2 last = new(float.NaN, float.NaN);
            int steps = 0;
            int maxSteps = pickCount * 200; // interval is 0.3s == 18 steps at Dt; generous ceiling
            while (picks.Count < pickCount && steps < maxSteps)
            {
                director.Tick(_profile, Dt, true, playerSpeaking, ref random);
                steps++;
                if (director.Offset != last)
                {
                    last = director.Offset;
                    picks.Add(last);
                }
            }
            return picks;
        }

        [Test]
        public void BiasInactive_MouthPickFrequency_MatchesLegacyUniform()
        {
            SetSerializedBool("enableListenerMouthBias", true);
            List<Vector2> picks = CollectPicks(_director, ref _random, 3000, playerSpeaking: false);

            int mouthCount = picks.FindAll(IsMouth).Count;
            float mouthFraction = (float)mouthCount / picks.Count;

            Assert.That(mouthFraction, Is.EqualTo(1f / 3f).Within(0.06f),
                "With the player silent, mouth-pick frequency must stay close to the legacy uniform 1/3.");
        }

        [Test]
        public void BiasActive_StrengthTwo_MouthPickedAtWeightedNeverRepeatRate()
        {
            SetSerializedBool("enableListenerMouthBias", true);
            SetSerializedFloat("listenerMouthBiasStrength", 2f);

            // Warm up past the ~0.5s blend so the bias factor has settled at 1.
            for (int i = 0; i < 40; i++)
                _director.Tick(_profile, Dt, true, true, ref _random);

            List<Vector2> picks = CollectPicks(_director, ref _random, 3000, playerSpeaking: true);

            int mouthCount = picks.FindAll(IsMouth).Count;
            float mouthFraction = (float)mouthCount / picks.Count;

            // The never-repeat rule excludes the current landmark from each draw, so the
            // mouth (weight 2) is only reachable from an eye, where it wins with p = 2/3;
            // from the mouth the eyes split evenly. The stationary distribution of that
            // Markov chain is eyes 0.3 each, mouth 0.4 — the raw-weight 2/4 = 0.5 share is
            // unattainable under never-repeat (it would require strict eye-mouth alternation).
            Assert.That(mouthFraction, Is.EqualTo(0.4f).Within(0.08f),
                "At full bias with strength 2, the mouth share must sit at the weighted " +
                "never-repeat stationary rate (0.4), well above the uniform 1/3.");
        }

        [Test]
        public void MouthBiasFactor_RampsBoundedOverHalfSecond()
        {
            SetSerializedBool("enableListenerMouthBias", true);

            float maxStepPerTick = Dt / 0.5f;
            float previous = 0f;
            for (int i = 0; i < 40; i++)
            {
                _director.Tick(_profile, Dt, true, true, ref _random);
                float delta = _director.MouthBiasFactor - previous;
                Assert.That(delta, Is.LessThanOrEqualTo(maxStepPerTick + 1e-5f),
                    $"Bias factor must not jump by more than one ~0.5s ramp step per tick (tick {i}).");
                previous = _director.MouthBiasFactor;
            }

            Assert.That(_director.MouthBiasFactor, Is.EqualTo(1f).Within(0.001f),
                "After ~0.5s of continuous player speech, the bias factor should have fully ramped in.");
        }

        [Test]
        public void NeverRepeatRule_PreservedUnderBias()
        {
            SetSerializedBool("enableListenerMouthBias", true);
            SetSerializedFloat("listenerMouthBiasStrength", 4f);

            List<Vector2> picks = CollectPicks(_director, ref _random, 500, playerSpeaking: true);

            for (int i = 1; i < picks.Count; i++)
                Assert.That(picks[i], Is.Not.EqualTo(picks[i - 1]),
                    $"Landmark pick {i} repeats the previous pick even under mouth bias.");
        }

        [Test]
        public void DisableFlag_ForcesLegacyBehaviorEvenWhilePlayerSpeaking()
        {
            SetSerializedBool("enableListenerMouthBias", false);
            SetSerializedFloat("listenerMouthBiasStrength", 4f);

            // Run continuously with the player speaking so a working bias would have ramped in.
            for (int i = 0; i < 40; i++)
                _director.Tick(_profile, Dt, true, true, ref _random);

            Assert.That(_director.MouthBiasFactor, Is.EqualTo(0f).Within(1e-6f),
                "Disabling the feature must keep the bias factor at 0 regardless of player speech.");

            List<Vector2> picks = CollectPicks(_director, ref _random, 3000, playerSpeaking: true);
            int mouthCount = picks.FindAll(IsMouth).Count;
            float mouthFraction = (float)mouthCount / picks.Count;

            Assert.That(mouthFraction, Is.EqualTo(1f / 3f).Within(0.06f),
                "With the feature disabled, mouth-pick frequency must match the legacy uniform 1/3 even while the player speaks.");
        }

        [Test]
        public void Determinism_SameSeed_ProducesIdenticalPickSequence()
        {
            SetSerializedBool("enableListenerMouthBias", true);
            SetSerializedFloat("listenerMouthBiasStrength", 2f);

            var directorA = new FaceScanDirector();
            var randomA = new DeterministicEmbodimentRandom(9876u);
            List<Vector2> picksA = CollectPicks(directorA, ref randomA, 200, playerSpeaking: true);

            var directorB = new FaceScanDirector();
            var randomB = new DeterministicEmbodimentRandom(9876u);
            List<Vector2> picksB = CollectPicks(directorB, ref randomB, 200, playerSpeaking: true);

            Assert.That(picksA, Is.EqualTo(picksB),
                "Identical seeds and inputs must produce an identical pick sequence.");
        }
    }
}
