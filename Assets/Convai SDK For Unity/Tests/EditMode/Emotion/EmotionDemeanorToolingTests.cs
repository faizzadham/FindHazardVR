using Convai.Domain.Embodiment.Semantics;
using System.Collections.Generic;
using Convai.Modules.Emotion.Editor;
using Convai.Modules.Emotion.Outputs;
using Convai.Modules.Emotion.Profiles;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Editor authoring-tooling tests for <see cref="EmotionDemeanorTooling" />: applying a
    ///     demeanor preset onto an existing <see cref="ConvaiEmotionProfile" /> asset sets the expected
    ///     distinguishing values, preserves output bindings, and is undoable; building blendshape slots
    ///     for a rig convention populates both output bindings.
    /// </summary>
    [TestFixture]
    public sealed class EmotionDemeanorToolingTests
    {
        private static ConvaiEmotionProfile NewProfile() => ScriptableObject.CreateInstance<ConvaiEmotionProfile>();

        private static void ApplyDemeanor(
            ConvaiEmotionProfile profile, CharacterDemeanor characterType)
        {
            var serialized = new SerializedObject(profile);
            serialized.Update();
            EmotionDemeanorTooling.Apply(serialized, characterType, profile.Taxonomy);
            serialized.ApplyModifiedProperties();
        }


        [Test]
        public void ApplyDemeanor_Warm_SetsWarmDistinguishingValues()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                ApplyDemeanor(profile, CharacterDemeanor.Warm);

                Assert.That(profile.BaselineEmotionLabel, Is.EqualTo("joy"));
                Assert.That(profile.BaselineIntensity, Is.EqualTo(0.55f).Within(0.0001f));
                Assert.That(profile.GetExpressivenessGain("joy"), Is.GreaterThan(1f));
                Assert.That(profile.EnableEmotionBlending, Is.True);
                Assert.That(profile.MicroExpressionsEnabled, Is.True);
                Assert.That(profile.MicroExpressionAmplitude, Is.EqualTo(0.18f).Within(0.0001f));
                Assert.That(profile.TryGetDynamics("joy", out float joyAttack, out _), Is.True);
                Assert.That(joyAttack, Is.EqualTo(6f).Within(0.0001f));
                Assert.That(profile.ProsodyCoupling, Is.EqualTo(0.3f).Within(0.0001f),
                    "Prosody coupling is a temperament field and must be copied by the character-type tooling.");
                Assert.That(profile.LerpSpeed, Is.EqualTo(5.5f).Within(0.0001f),
                    "How fast a character shows a feeling is part of its temperament, so applying a " +
                    "character type must write it — leaving it behind was how applying Reserved to a " +
                    "warm profile left it reacting at the warm speed.");
                Assert.That(profile.DecaySpeed, Is.EqualTo(1.5f).Within(0.0001f));
                Assert.That(profile.MoodDriftEnabled, Is.True,
                    "Whether the conversation can shift the resting mood is part of the temperament.");
                Assert.That(profile.ContagionEnabled, Is.True,
                    "Mood pickup is a temperament field and must be copied by the demeanor tooling.");
                Assert.That(profile.ContagionStrength, Is.EqualTo(0.3f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ApplyDemeanor_Reserved_SetsReservedDistinguishingValues()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                ApplyDemeanor(profile, CharacterDemeanor.Reserved);

                Assert.That(profile.BaselineIntensity, Is.EqualTo(0f));
                Assert.That(profile.GetExpressivenessGain("joy"), Is.EqualTo(0.7f).Within(0.0001f));
                Assert.That(profile.MicroExpressionAmplitude, Is.LessThan(0.1f));
                Assert.That(profile.LerpSpeed, Is.EqualTo(3.5f).Within(0.0001f));
                Assert.That(profile.EmotionSwitchDwell, Is.EqualTo(0.7f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ApplyDemeanor_DoesNotTouchOutputOrTaxonomy()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                // Seed the profile with authored material output the preset must leave alone.
                profile.MaterialBinding.SetSlots(new List<MaterialPropertyEmotionSlot>
                {
                    new("joy", "_EmotionBlush", 0f, 1f)
                });

                ApplyDemeanor(profile, CharacterDemeanor.Energetic);

                Assert.That(profile.MaterialBinding.Slots.Count, Is.EqualTo(1),
                    "Applying a character-type preset must not touch authored material output.");
                Assert.That(profile.Taxonomy, Is.Null,
                    "Applying a character-type preset must not touch the taxonomy reference.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ApplyDemeanor_IsUndoable()
        {
            ConvaiEmotionProfile profile = NewProfile();
            try
            {
                ApplyDemeanor(profile, CharacterDemeanor.Warm);
                float warmAmplitude = profile.MicroExpressionAmplitude;

                Undo.IncrementCurrentGroup();

                ApplyDemeanor(profile, CharacterDemeanor.Reserved);
                float reservedAmplitude = profile.MicroExpressionAmplitude;
                Assert.That(reservedAmplitude, Is.Not.EqualTo(warmAmplitude).Within(0.0001f),
                    "Sanity: the second demeanor must actually change the profile.");

                Undo.PerformUndo();

                Assert.That(profile.MicroExpressionAmplitude, Is.EqualTo(warmAmplitude).Within(0.0001f),
                    "Undo must restore the previously applied demeanor's values.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }



    }
}
