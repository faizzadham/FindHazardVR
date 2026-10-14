using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class BackchannelDirectorTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private BackchannelDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _director = new BackchannelDirector();
            _random = new DeterministicEmbodimentRandom(1234u);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private void SetProbability(float probability)
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "acknowledgeNodProbability").floatValue = probability;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private void Run(float seconds, bool isListening = true, bool suppressed = false)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
                _director.Tick(_profile, isListening, suppressed, Dt, ref _random);
        }

        private float RunToPeakOffset(float seconds, bool isListening = true, bool suppressed = false)
        {
            float peak = 0f;
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                _director.Tick(_profile, isListening, suppressed, Dt, ref _random);
                peak = Mathf.Max(peak, Mathf.Abs(_director.GestureOffset.y));
            }
            return peak;
        }

        [Test]
        public void Envelope_StartsAndEndsAtRest()
        {
            Assert.That(BackchannelDirector.Shape(0f), Is.EqualTo(0f).Within(0.001f),
                "A nod begins from rest (< 0.05° at p=0).");
            Assert.That(BackchannelDirector.Shape(1f), Is.EqualTo(0f).Within(0.001f),
                "A nod ends at rest (< 0.05° at p=1).");
            Assert.That(BackchannelDirector.Shape(0.4f), Is.GreaterThan(0.95f),
                "The dip crests at the configured peak.");
            // Equal time from the crest (0.4): 0.2 s before it on the way down, 0.2 s after it on
            // the way up. The slower return is still higher there.
            Assert.That(BackchannelDirector.Shape(0.6f), Is.GreaterThan(BackchannelDirector.Shape(0.2f) + 0.05f),
                "The return is slower than the dip: one lobe, quick down, gentle up.");
        }

        [Test]
        public void NoNod_OnTheListeningEntryEdge()
        {
            // The entry edge is "the player has started talking"; a head that bobs on it reads
            // as a tic, and with the eyes holding the face, as the eyes rolling up and down.
            SetProbability(1f);

            float peak = RunToPeakOffset(BackchannelDirector.FirstNodMinSeconds - Dt);

            Assert.That(peak, Is.EqualTo(0f).Within(0.001f),
                "Nothing nods in the first two seconds of listening.");
        }

        [Test]
        public void AcknowledgeNod_LandsOnTheFirstPauseAfterSettlingIn_AndIsDownward()
        {
            SetProbability(1f);
            Run(BackchannelDirector.FirstNodMinSeconds + 0.1f);
            Assert.That(_director.GestureOffset.y, Is.EqualTo(0f).Within(0.001f), "Sanity: still nothing.");

            // The speaker pauses: this is where the acknowledgement belongs.
            _director.Tick(_profile, true, false, Dt, ref _random, pauseCue: true);
            Assert.IsTrue(_director.IsNodding, "The first pause after settling in takes the nod.");

            float minY = 0f;
            for (int i = 0; i < Mathf.CeilToInt(0.6f / Dt); i++)
            {
                _director.Tick(_profile, true, false, Dt, ref _random);
                minY = Mathf.Min(minY, _director.GestureOffset.y);
            }

            Assert.That(minY, Is.LessThan(-2.5f), "The nod pitches the head down to about the 3° peak.");
        }

        [Test]
        public void APauseBeforeSettlingIn_DoesNotNod()
        {
            SetProbability(1f);
            Run(0.5f);
            _director.Tick(_profile, true, false, Dt, ref _random, pauseCue: true);

            Assert.IsFalse(_director.IsNodding);
        }

        [Test]
        public void AScheduledNodThatIsNearlyDue_IsBroughtForwardOntoAPause()
        {
            SetProbability(0f);
            // Past the settling window; the interval (3.5–8 s) is at most 3 s from due once
            // 5 s have passed, so a pause here must take it.
            Run(5f);
            Assert.IsFalse(_director.IsNodding, "Sanity: nothing has fired on its own yet.");

            _director.Tick(_profile, true, false, Dt, ref _random, pauseCue: true);

            Assert.IsTrue(_director.IsNodding);
        }

        [Test]
        public void NodReturnsToRest_AfterItsDuration()
        {
            SetProbability(1f);
            // Run well past the nod duration; between nods the offset is exactly zero.
            Run(1.2f);

            Assert.That(_director.GestureOffset, Is.EqualTo(Vector2.zero),
                "Once the nod completes the head gesture returns exactly to rest.");
            Assert.IsFalse(_director.IsNodding);
        }

        [Test]
        public void NoOutput_OutsideListening()
        {
            SetProbability(1f);
            float peak = RunToPeakOffset(5f, isListening: false);
            Assert.That(peak, Is.EqualTo(0f), "Nods never play outside the Listening state.");
        }

        [Test]
        public void NoOutput_WhileSuppressed()
        {
            SetProbability(1f);
            float peak = RunToPeakOffset(5f, isListening: true, suppressed: true);
            Assert.That(peak, Is.EqualTo(0f),
                "Nods hard-suppress while the character speaks or has no target to nod at.");
        }

        [Test]
        public void Suppression_PausesWithoutReArmingEntryNod()
        {
            SetProbability(1f);
            Run(0.9f); // settling in; the first nod is ≥ 3.5 s away and never on the entry edge
            Assert.IsFalse(_director.IsNodding, "Sanity: nothing has nodded yet.");

            Run(1f, suppressed: true);          // speech-energy flicker mid-listening
            float peak = RunToPeakOffset(1.5f); // resume listening

            Assert.That(peak, Is.EqualTo(0f),
                "Un-suppressing mid-listening must not re-roll the acknowledgment nod — the " +
                "cadence continues (≥ 3.5 s away); only a fresh Listening entry re-arms it.");
        }

        [Test]
        public void CancelMidNod_FadesOutSmoothly()
        {
            SetProbability(1f);
            Run(BackchannelDirector.FirstNodMinSeconds + 0.1f);
            _director.TriggerNod(1f);
            Run(0.2f); // near the crest of the dip
            float atCancel = Mathf.Abs(_director.GestureOffset.y);
            Assert.That(atCancel, Is.GreaterThan(2f), "Sanity: the nod is mid-flight.");

            // The offset feeds the bones post-spring, so cancellation must ease, not step.
            float previous = atCancel;
            for (int i = 0; i < 30; i++)
            {
                _director.Tick(_profile, true, true, Dt, ref _random);
                float current = Mathf.Abs(_director.GestureOffset.y);
                Assert.That(current, Is.LessThanOrEqualTo(previous + 0.001f),
                    "The cancelled offset decays monotonically.");
                Assert.That(previous - current, Is.LessThanOrEqualTo(45f * Dt + 0.001f),
                    "The decay rate is bounded — no single-frame step.");
                previous = current;
            }

            Assert.That(previous, Is.EqualTo(0f).Within(0.001f),
                "The residual fades fully to rest within half a second.");
        }

        [Test]
        public void Schedule_ProducesSeveralNods_OverAListeningWindow()
        {
            SetProbability(0f); // isolate the interval scheduler from the entry nod
            int nods = 0;
            bool wasNodding = false;
            for (int i = 0; i < Mathf.CeilToInt(40f / Dt); i++)
            {
                _director.Tick(_profile, true, false, Dt, ref _random);
                if (_director.IsNodding && !wasNodding) nods++;
                wasNodding = _director.IsNodding;
            }

            Assert.That(nods, Is.GreaterThanOrEqualTo(3),
                "Listening produces sparse nods on the randomized cadence.");
            Assert.That(nods, Is.LessThanOrEqualTo(12),
                "The cadence stays sparse — no machine-gun nodding.");
        }

        [Test]
        public void Schedule_IsSeedDeterministic()
        {
            var a = new BackchannelDirector();
            var b = new BackchannelDirector();
            var randomA = new DeterministicEmbodimentRandom(99u);
            var randomB = new DeterministicEmbodimentRandom(99u);

            for (int i = 0; i < Mathf.CeilToInt(30f / Dt); i++)
            {
                a.Tick(_profile, true, false, Dt, ref randomA);
                b.Tick(_profile, true, false, Dt, ref randomB);
                Assert.That(a.GestureOffset, Is.EqualTo(b.GestureOffset),
                    $"Same seed → identical nod schedule and envelope (tick {i}).");
            }
        }
    }
}
