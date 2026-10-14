using Convai.Domain.Embodiment.Readings;
using Convai.Modules.Gaze.Core.Policy;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class EmotionGazeModulatorTests
    {
        private ConvaiGazeProfile _profile;
        private EmotionGazeModulator _modulator;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _modulator = new EmotionGazeModulator();

            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "enableEmotionModulation").boolValue = true;
            SerializedProperty list = GazeProfileSerializedPaths.Find(serialized, "emotionModifiers");
            list.arraySize = 1;
            SerializedProperty entry = list.GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("EmotionLabel").stringValue = "sadness";
            entry.FindPropertyRelative("EngagementScale").floatValue = 0.5f;
            entry.FindPropertyRelative("AversionScale").floatValue = 1.6f;
            entry.FindPropertyRelative("BlinkRateScale").floatValue = 1.3f;
            entry.FindPropertyRelative("LidApertureScale").floatValue = 0.7f;
            // CreateDefault() ships pre-authored rows (sadness/joy/anger); shrinking
            // arraySize keeps the first row's authored values. Zero them out so this
            // fixture's row reads like a freshly added row, or one serialized before these
            // fields existed, which is the premise the "unauthored" tests below rely on.
            entry.FindPropertyRelative("AversionBias").enumValueIndex = (int)GazeAversionBias.CognitiveDefault;
            entry.FindPropertyRelative("SaccadeTempoScale").floatValue = 0f;
            entry.FindPropertyRelative("FixationLivelinessScale").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private static EmotionReading Reading(string label, float score) =>
            new(label, score, null, 0f, 0f);

        [Test]
        public void FullIntensityAuthoredEmotion_AppliesModifiersFully()
        {
            _modulator.Tick(_profile, Reading("sadness", 1f));

            Assert.That(_modulator.EngagementScale, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(_modulator.AversionScale, Is.EqualTo(1.6f).Within(0.001f));
            Assert.That(_modulator.BlinkRateScale, Is.EqualTo(1.3f).Within(0.001f));
        }

        [Test]
        public void HalfIntensity_BlendsTowardNeutral()
        {
            _modulator.Tick(_profile, Reading("sadness", 0.5f));

            Assert.That(_modulator.EngagementScale, Is.EqualTo(0.75f).Within(0.001f),
                "Intensity blends the modifier toward 1 so onsets stay smooth.");
            Assert.That(_modulator.LidApertureScale, Is.EqualTo(0.85f).Within(0.001f),
                "Lid aperture blends toward neutral with intensity, like the other scales.");
        }

        [Test]
        public void FullIntensity_AppliesLidAperture()
        {
            _modulator.Tick(_profile, Reading("sadness", 1f));

            Assert.That(_modulator.LidApertureScale, Is.EqualTo(0.7f).Within(0.001f));
        }

        [Test]
        public void UnauthoredAperture_ReadsAsNeutral()
        {
            // A modifier that never set LidApertureScale (0) must read as neutral (1), not as a
            // fully shut lid.
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "emotionModifiers").GetArrayElementAtIndex(0)
                .FindPropertyRelative("LidApertureScale").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _modulator.Tick(_profile, Reading("sadness", 1f));

            Assert.That(_modulator.LidApertureScale, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void UnknownEmotion_IsNeutral()
        {
            _modulator.Tick(_profile, Reading("joy", 1f));

            Assert.That(_modulator.EngagementScale, Is.EqualTo(1f));
            Assert.That(_modulator.AversionScale, Is.EqualTo(1f));
        }

        [Test]
        public void CaseInsensitiveLabelMatch()
        {
            _modulator.Tick(_profile, Reading("SADNESS", 1f));

            Assert.That(_modulator.EngagementScale, Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void DisabledModulation_IsAlwaysNeutral()
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "enableEmotionModulation").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _modulator.Tick(_profile, Reading("sadness", 1f));

            Assert.That(_modulator.EngagementScale, Is.EqualTo(1f));
        }

        // ── Emotional gaze signature: AversionBias / SaccadeTempoScale / FixationLivelinessScale ──

        [Test]
        public void UnauthoredTempoLivelinessAndBias_ReadAsNeutral()
        {
            // SetUp's row never touches these three fields — a freshly added row, or one
            // serialized before they existed,
            // reads 0 for the two new scales and CognitiveDefault (0) for the bias, all of which
            // must resolve to "unmodified", the same convention as LidApertureScale.
            _modulator.Tick(_profile, Reading("sadness", 1f));

            Assert.That(_modulator.SaccadeTempoScale, Is.EqualTo(1f).Within(0.001f));
            Assert.That(_modulator.FixationLivelinessScale, Is.EqualTo(1f).Within(0.001f));
            Assert.That(_modulator.AversionBias, Is.EqualTo(GazeAversionBias.CognitiveDefault));
        }

        [Test]
        public void AuthoredTempoLivelinessAndBias_ApplyAtFullIntensity()
        {
            var serialized = new SerializedObject(_profile);
            SerializedProperty entry = GazeProfileSerializedPaths.Find(serialized, "emotionModifiers").GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("AversionBias").enumValueIndex = (int)GazeAversionBias.Down;
            entry.FindPropertyRelative("SaccadeTempoScale").floatValue = 0.8f;
            entry.FindPropertyRelative("FixationLivelinessScale").floatValue = 1.2f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _modulator.Tick(_profile, Reading("sadness", 1f));

            Assert.That(_modulator.AversionBias, Is.EqualTo(GazeAversionBias.Down));
            Assert.That(_modulator.SaccadeTempoScale, Is.EqualTo(0.8f).Within(0.001f));
            Assert.That(_modulator.FixationLivelinessScale, Is.EqualTo(1.2f).Within(0.001f));
        }

        [Test]
        public void TempoScale_ClampsToAuthoredRange()
        {
            var serialized = new SerializedObject(_profile);
            SerializedProperty entry = GazeProfileSerializedPaths.Find(serialized, "emotionModifiers").GetArrayElementAtIndex(0);
            // The Range attribute clamps slider edits, not raw serialized writes (e.g. scripted
            // authoring or migrated data) — the modulator itself must defend the 0.7-1.3 bound.
            entry.FindPropertyRelative("SaccadeTempoScale").floatValue = 5f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _modulator.Tick(_profile, Reading("sadness", 1f));

            Assert.That(_modulator.SaccadeTempoScale, Is.EqualTo(1.3f).Within(0.001f));
        }

        [Test]
        public void HalfIntensity_BlendsTempoAndLivelinessTowardNeutral()
        {
            var serialized = new SerializedObject(_profile);
            SerializedProperty entry = GazeProfileSerializedPaths.Find(serialized, "emotionModifiers").GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("SaccadeTempoScale").floatValue = 0.8f;
            entry.FindPropertyRelative("FixationLivelinessScale").floatValue = 1.4f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _modulator.Tick(_profile, Reading("sadness", 0.5f));

            Assert.That(_modulator.SaccadeTempoScale, Is.EqualTo(0.9f).Within(0.001f),
                "Halfway from neutral 1 to sadness's authored 0.8 tempo is 0.9.");
            Assert.That(_modulator.FixationLivelinessScale, Is.EqualTo(1.2f).Within(0.001f));
        }

        [Test]
        public void ZeroIntensity_BiasRevertsToCognitiveDefault()
        {
            var serialized = new SerializedObject(_profile);
            SerializedProperty entry = GazeProfileSerializedPaths.Find(serialized, "emotionModifiers").GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("AversionBias").enumValueIndex = (int)GazeAversionBias.Down;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            _modulator.Tick(_profile, Reading("sadness", 0f));

            Assert.That(_modulator.AversionBias, Is.EqualTo(GazeAversionBias.CognitiveDefault),
                "A discrete direction can't blend toward neutral — it switches off at zero intensity.");
        }

        [Test]
        public void GradualIntensityRamp_ProducesBoundedPerTickDelta()
        {
            // The modulator adds no temporal filter of its own — it rides whatever ramp the
            // emotion reading's DominantScore already provides. Feeding a gradual ramp here
            // must therefore produce a gradual (bounded per-step), never stepped, output.
            var serialized = new SerializedObject(_profile);
            SerializedProperty entry = GazeProfileSerializedPaths.Find(serialized, "emotionModifiers").GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("SaccadeTempoScale").floatValue = 0.7f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            float previous = 1f;
            for (int step = 0; step <= 20; step++)
            {
                float intensity = step / 20f;
                _modulator.Tick(_profile, Reading("sadness", intensity));

                float delta = Mathf.Abs(_modulator.SaccadeTempoScale - previous);
                Assert.That(delta, Is.LessThanOrEqualTo(0.02f),
                    "Each 5% intensity step must move SaccadeTempoScale by at most the " +
                    "proportional 5% of the authored 0.7-1 range.");
                previous = _modulator.SaccadeTempoScale;
            }
        }

        [Test]
        public void RepeatedTicks_AreDeterministic()
        {
            _modulator.Tick(_profile, Reading("sadness", 0.6f));
            float first = _modulator.SaccadeTempoScale;

            var second = new EmotionGazeModulator();
            second.Tick(_profile, Reading("sadness", 0.6f));

            Assert.That(second.SaccadeTempoScale, Is.EqualTo(first).Within(0.0001f));
            Assert.That(second.AversionBias, Is.EqualTo(_modulator.AversionBias));
        }
    }
}
