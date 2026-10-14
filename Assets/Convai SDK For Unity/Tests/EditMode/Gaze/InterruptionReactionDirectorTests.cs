using Convai.Domain.Embodiment.Semantics;
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
    public sealed class InterruptionReactionDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const uint Seed = 4242u;

        private ConvaiGazeProfile _profile;
        private InterruptionReactionDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _director = new InterruptionReactionDirector();
            _random = new DeterministicEmbodimentRandom(Seed);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private void SetEnabled(bool value)
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "enableInterruptionReaction").boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private void SetIntensity(float value)
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "interruptionReactionIntensity").floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void Envelope_StartsAndEndsAtRest()
        {
            Assert.That(InterruptionReactionDirector.Envelope(0f), Is.EqualTo(0f).Within(0.001f),
                "The tilt begins from rest.");
            Assert.That(InterruptionReactionDirector.Envelope(1f), Is.EqualTo(0f).Within(0.001f),
                "The tilt ends exactly at rest.");
            Assert.That(InterruptionReactionDirector.Envelope(0.15f), Is.EqualTo(1f).Within(0.01f),
                "The attack reaches its peak at the configured attack fraction.");
        }

        [Test]
        public void SpeakingToInterrupted_FiresReactionExactlyOnce()
        {
            _director.Tick(DialogueState.Speaking, _profile, Dt, ref _random);
            Assert.IsFalse(_director.WantsReacquisition, "Sanity: no edge yet.");

            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
            Assert.IsTrue(_director.WantsReacquisition, "Speaking->Interrupted must fire the re-acquisition pulse.");
            Assert.IsTrue(_director.WantsBlink, "Speaking->Interrupted must fire the blink pulse.");
            Assert.IsTrue(_director.ReactionActive);

            int fires = 1;
            for (int i = 0; i < 180; i++)
            {
                _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
                if (_director.WantsReacquisition) fires++;
            }

            Assert.That(fires, Is.EqualTo(1),
                "Holding in Interrupted must never re-fire the beat.");
        }

        [Test]
        public void ListeningToInterrupted_DoesNotFire()
        {
            _director.Tick(DialogueState.Listening, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);

            Assert.IsFalse(_director.WantsReacquisition,
                "The startle beat is specifically for barge-ins during Speaking, not Listening.");
            Assert.IsFalse(_director.WantsBlink);
            Assert.IsFalse(_director.ReactionActive);
        }

        [Test]
        public void Refractory_HoldsUntilSpeakingReentered()
        {
            _director.Tick(DialogueState.Speaking, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
            Assert.IsTrue(_director.WantsReacquisition, "Sanity: first interruption fires.");

            // Bounce out of Interrupted without passing back through Speaking — must not re-arm.
            _director.Tick(DialogueState.Attending, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
            Assert.IsFalse(_director.WantsReacquisition,
                "Attending->Interrupted is not the Speaking edge and must not fire.");

            // Re-enter Speaking, then interrupt again — the beat must be re-armed.
            _director.Tick(DialogueState.Speaking, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
            Assert.IsTrue(_director.WantsReacquisition,
                "Re-entering Speaking must re-arm the beat for the next interruption.");
        }

        [Test]
        public void Envelope_CompletesWithinRoughlyOneSecond()
        {
            _director.Tick(DialogueState.Speaking, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);

            int steps = Mathf.CeilToInt(1.2f / Dt);
            for (int i = 0; i < steps; i++)
                _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);

            Assert.IsFalse(_director.ReactionActive, "The envelope must finish within ~1.2s.");
            Assert.That(_director.TiltDegrees, Is.EqualTo(0f), "The tilt must return exactly to rest.");
        }

        [Test]
        public void Disabled_SuppressesReaction()
        {
            SetEnabled(false);

            _director.Tick(DialogueState.Speaking, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);

            Assert.IsFalse(_director.WantsReacquisition);
            Assert.IsFalse(_director.WantsBlink);
            Assert.IsFalse(_director.ReactionActive);
        }

        [Test]
        public void Intensity_ScalesTiltMagnitude()
        {
            SetIntensity(0f);
            _director.Tick(DialogueState.Speaking, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
            float lowPeak = 0f;
            for (int i = 0; i < 30; i++)
            {
                _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
                lowPeak = Mathf.Max(lowPeak, Mathf.Abs(_director.TiltDegrees));
            }

            _director.Reset();
            _random = new DeterministicEmbodimentRandom(Seed);
            SetIntensity(1f);
            _director.Tick(DialogueState.Speaking, _profile, Dt, ref _random);
            _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
            float highPeak = 0f;
            for (int i = 0; i < 30; i++)
            {
                _director.Tick(DialogueState.Interrupted, _profile, Dt, ref _random);
                highPeak = Mathf.Max(highPeak, Mathf.Abs(_director.TiltDegrees));
            }

            Assert.That(highPeak, Is.GreaterThan(lowPeak), "Higher intensity must produce a larger tilt.");
        }
    }
}
