using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GazeProfileTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        [Test]
        public void CreateDefault_ProvidesPolicyForEveryDialogueState()
        {
            foreach (DialogueState state in System.Enum.GetValues(typeof(DialogueState)))
            {
                GazeStatePolicy policy = _profile.GetStatePolicy(state);
                Assert.That(policy.Engagement, Is.InRange(0f, 1f),
                    $"Engagement for {state} must be normalized.");
                Assert.That(policy.HeadContribution, Is.InRange(0f, 1f),
                    $"HeadContribution for {state} must be normalized.");
            }
        }

        [Test]
        public void DefaultIdlePolicy_SuppressesPlayerTarget()
        {
            GazeStatePolicy idle = _profile.GetStatePolicy(DialogueState.Idle);

            Assert.IsFalse(idle.AllowPlayerTarget, "Idle must not track the player by default.");
            Assert.That(idle.Engagement, Is.EqualTo(0f), "Idle engagement defaults to zero.");
            Assert.IsFalse(idle.AllowBodyTurn, "Idle must not trigger body turns.");
        }

        [Test]
        public void DefaultSpeakingPolicy_IsFullCommit()
        {
            GazeStatePolicy speaking = _profile.GetStatePolicy(DialogueState.Speaking);

            Assert.That(speaking.Engagement, Is.EqualTo(1f), "Speaking locks fully onto the player.");
            Assert.IsTrue(speaking.AllowPlayerTarget);
            Assert.IsTrue(speaking.AllowBodyTurn, "Speaking allows turning to face the player.");
            Assert.That(speaking.AversionMode, Is.EqualTo(GazeAversionMode.None),
                "Speaking keeps unbroken contact by default.");
        }

        [Test]
        public void DefaultThinkingPolicy_UsesCognitiveAversion()
        {
            GazeStatePolicy thinking = _profile.GetStatePolicy(DialogueState.Thinking);

            Assert.That(thinking.AversionMode, Is.EqualTo(GazeAversionMode.Cognitive));
            Assert.That(thinking.AversionStrength, Is.GreaterThan(0f));
            Assert.IsFalse(thinking.AllowBodyTurn, "Thinking should not spin the body.");
        }

        [Test]
        public void GetStatePolicy_UnknownState_FallsBackToIdle()
        {
            // Cast an out-of-range value to simulate a future enum member.
            GazeStatePolicy fallback = _profile.GetStatePolicy((DialogueState)999);
            GazeStatePolicy idle = _profile.GetStatePolicy(DialogueState.Idle);

            Assert.That(fallback.Engagement, Is.EqualTo(idle.Engagement));
            Assert.That(fallback.AllowPlayerTarget, Is.EqualTo(idle.AllowPlayerTarget));
        }

        [Test]
        public void GetStatePolicy_SanitizesOutOfRangeAuthoredValues()
        {
            // Serialized data can carry out-of-range values (hand-edited YAML); resolution clamps.
            var serialized = new SerializedObject(_profile);
            SerializedProperty list = GazeProfileSerializedPaths.Find(serialized, "statePolicies");
            SerializedProperty first = list.GetArrayElementAtIndex(0);
            first.FindPropertyRelative("Engagement").floatValue = 7f;
            first.FindPropertyRelative("FixationLiveliness").floatValue = 9f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            GazeStatePolicy idle = _profile.GetStatePolicy(DialogueState.Idle);

            Assert.That(idle.Engagement, Is.LessThanOrEqualTo(1f));
            Assert.That(idle.FixationLiveliness, Is.LessThanOrEqualTo(2f));
        }

        [Test]
        public void TryGetEmotionModifier_EnabledByDefault()
        {
            // Ratified 2026-07-28: emotion modulation shipped off, so no project ever saw an angry
            // character's gaze read as angry. It costs nothing without the Emotion module present —
            // the modulator reads a neutral emotion and produces unit scales.
            Assert.IsTrue(_profile.TryGetEmotionModifier("joy", out _),
                "Emotion modulation is on by default and the shipped signatures must resolve.");
        }

        [Test]
        public void Defaults_AreInternallyConsistent()
        {
            Assert.That(_profile.PlayerFullRelevanceDistance, Is.LessThanOrEqualTo(_profile.PlayerMaxDistance));
            Assert.That(_profile.AmbientIntervalMax, Is.GreaterThanOrEqualTo(_profile.AmbientIntervalMin));
            Assert.That(_profile.CuriosityGlanceIntervalMax, Is.GreaterThanOrEqualTo(_profile.CuriosityGlanceIntervalMin));
            Assert.That(_profile.EyeSoftLimitFraction, Is.InRange(0.5f, 1f));
            Assert.That(_profile.TraceVerbosity, Is.EqualTo(GazeTraceVerbosity.Off),
                "A shipped character must be silent by default: State routes every target " +
                "transition through ConvaiLogger.Info, which the SDK's own default log level lets " +
                "through. Warnings and errors bypass this gate and still reach the console.");
        }
    }
}
