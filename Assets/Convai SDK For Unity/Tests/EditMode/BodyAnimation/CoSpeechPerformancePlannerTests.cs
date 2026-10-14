using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Performance;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class CoSpeechPerformancePlannerTests
    {
        private ConvaiBodyAnimationConfig _config;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            Set("_enableAdvancedCoSpeech", true);
            Set("_coSpeechAccentProbability", 1f);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_config);

        [Test]
        public void FinalEnglishTranscript_PublishesSemanticGestureWhileSpeaking()
        {
            var planner = new CoSpeechPerformancePlanner(_config, 42);
            planner.NotifyTranscript("First, I will explain three things to you", true);
            planner.Tick(DialogueState.Speaking, 0.6f, 0.1f, false, default);

            CoSpeechPerformanceReading reading = planner.Current;
            Assert.That(reading.QualityTier, Is.EqualTo(CoSpeechQualityTier.Transcript));
            Assert.That(reading.HasGesture, Is.True);
            Assert.That(reading.Gesture.Kind, Is.EqualTo(GestureCueKind.Enumerate));
        }

        /// <summary>
        ///     The split that stops "Enable Referential Gestures" from being a no-op on a set with
        ///     no referential clips: a cue the character actually MEANT — handed over by the
        ///     referential-gesture director because nothing authored could carry it — is published
        ///     for peer performers whether or not the advanced (speculative) tier is enabled.
        /// </summary>
        [Test]
        public void SemanticCue_IsPublishedEvenWithAdvancedCoSpeechOff()
        {
            Set("_enableAdvancedCoSpeech", false);
            var planner = new CoSpeechPerformancePlanner(_config, 42);

            planner.NotifyGestureCue(GestureCueKind.PalmToPlayer);
            planner.Tick(DialogueState.Speaking, 0.6f, 0.1f, false, default);

            Assert.That(planner.Current.HasGesture, Is.True,
                "a meant cue must reach a peer performer regardless of the speculative tier");
            Assert.That(planner.Current.Gesture.Kind, Is.EqualTo(GestureCueKind.PalmToPlayer));
        }

        /// <summary>
        ///     The other half of that split: accents invented from the energy envelope alone, with
        ///     no semantic evidence, stay behind the opt-in switch.
        /// </summary>
        [Test]
        public void EnergyDerivedAccents_StaySilentWithAdvancedCoSpeechOff()
        {
            Set("_enableAdvancedCoSpeech", false);
            var planner = new CoSpeechPerformancePlanner(_config, 7);

            planner.Tick(DialogueState.Speaking, 0.1f, 0.1f, false, default);
            planner.Tick(DialogueState.Speaking, 0.9f, 0.1f, false, default);
            planner.Tick(DialogueState.Speaking, 1f, 0.1f, false, default);

            Assert.That(planner.Current.HasGesture, Is.False);
        }

        [Test]
        public void InterimTranscript_DoesNotPublishGesture()
        {
            var planner = new CoSpeechPerformancePlanner(_config, 42);
            planner.NotifyTranscript("hello", false);
            planner.Tick(DialogueState.Speaking, 0.2f, 0.1f, false, default);
            Assert.That(planner.Current.HasGesture, Is.False);
        }

        [Test]
        public void Refractory_PreventsBackToBackEnergyAccents()
        {
            var planner = new CoSpeechPerformancePlanner(_config, 7);
            planner.Tick(DialogueState.Speaking, 0.1f, 0.1f, false, default);
            planner.Tick(DialogueState.Speaking, 0.9f, 0.1f, false, default);
            int sequence = planner.Current.GestureSequence;
            planner.Tick(DialogueState.Speaking, 0.1f, 0.1f, false, default);
            planner.Tick(DialogueState.Speaking, 1f, 0.1f, false, default);
            Assert.That(planner.Current.GestureSequence, Is.EqualTo(sequence));
        }

        [Test]
        public void Interrupted_CancelsPendingSemanticRequest()
        {
            var planner = new CoSpeechPerformancePlanner(_config, 1);
            planner.NotifyTranscript("hello", true);
            planner.Tick(DialogueState.Interrupted, 0f, 0.1f, false, default);
            planner.Tick(DialogueState.Speaking, 0.5f, 0.1f, false, default);
            Assert.That(planner.Current.Gesture.Kind, Is.EqualTo(GestureCueKind.None));
        }

        [Test]
        public void NormalSpeechEnd_DiscardsLateSemanticRequestBeforeNextTurn()
        {
            var planner = new CoSpeechPerformancePlanner(_config, 1);
            planner.Tick(DialogueState.Speaking, 0.4f, 0.1f, false, default);
            planner.NotifyTranscript("Hello", true);
            planner.Tick(DialogueState.Idle, 0f, 0.1f, false, default);
            planner.Tick(DialogueState.Speaking, 0.4f, 0.1f, false, default);
            Assert.That(planner.Current.Gesture.Kind, Is.EqualTo(GestureCueKind.None));
        }

        private void Set(string fieldName, object value)
        {
            FieldInfo field = typeof(ConvaiBodyAnimationConfig).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(_config, value);
        }
    }
}
