using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Guards the config's editing surface. Every serialized field must be discoverable, and must
    ///     have a named home in <see cref="BodyAnimationConfigSections" /> — otherwise a setting
    ///     quietly becomes unreachable, which is exactly how the persona sliders ended up buried in a
    ///     raw property dump.
    /// </summary>
    public sealed class BodyAnimationConfigInspectorCompletenessTests
    {
        private static List<FieldInfo> SerializedFields()
        {
            FieldInfo[] fields = typeof(ConvaiBodyAnimationConfig).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);

            var serialized = new List<FieldInfo>(fields.Length);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].GetCustomAttribute<SerializeField>() != null) serialized.Add(fields[i]);
            }
            return serialized;
        }

        [Test]
        public void EverySerializedRuntimeField_IsDiscoverableByCompleteInspectorIterator()
        {
            var config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            try
            {
                var serialized = new SerializedObject(config);
                foreach (FieldInfo field in SerializedFields())
                {
                    Assert.NotNull(serialized.FindProperty(field.Name),
                        $"Serialized runtime field '{field.Name}' is not available to the Config Inspector.");
                }

                UnityEditor.Editor editor = UnityEditor.Editor.CreateEditor(config);
                Assert.NotNull(editor, "ConvaiBodyAnimationConfig must have a creatable custom inspector.");
                Object.DestroyImmediate(editor);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        /// <summary>
        ///     The gate that stops the surface regressing: a field added to the config without a
        ///     section fails here rather than silently disappearing from the UI.
        /// </summary>
        [Test]
        public void EverySerializedRuntimeField_HasExactlyOneNamedSection()
        {
            var mapped = new HashSet<string>();
            BodyAnimationConfigSections.CollectMappedFields(mapped);

            var missing = new List<string>();
            foreach (FieldInfo field in SerializedFields())
            {
                if (!mapped.Contains(field.Name)) missing.Add(field.Name);
            }

            Assert.IsEmpty(missing,
                "These config fields have no named section, so users cannot find them. Add each to a " +
                $"section in {nameof(BodyAnimationConfigSections)}: {string.Join(", ", missing)}");
        }

        [Test]
        public void SectionTable_ListsNoFieldTwice()
        {
            var seen = new HashSet<string>();
            var duplicates = new List<string>();

            for (int i = 0; i < BodyAnimationConfigSections.Sections.Length; i++)
            {
                string[] fields = BodyAnimationConfigSections.Sections[i].Fields;
                for (int f = 0; f < fields.Length; f++)
                {
                    if (!seen.Add(fields[f])) duplicates.Add(fields[f]);
                }
            }

            Assert.IsEmpty(duplicates,
                $"A field may belong to exactly one section: {string.Join(", ", duplicates)}");
        }

        /// <summary>A section entry naming a field the config does not have would draw nothing.</summary>
        [Test]
        public void SectionTable_NamesNoFieldTheConfigDoesNotHave()
        {
            var config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            try
            {
                var serialized = new SerializedObject(config);
                var unknown = new List<string>();

                for (int i = 0; i < BodyAnimationConfigSections.Sections.Length; i++)
                {
                    BodyAnimationConfigSection section = BodyAnimationConfigSections.Sections[i];
                    for (int f = 0; f < section.Fields.Length; f++)
                    {
                        if (serialized.FindProperty(section.Fields[f]) == null)
                            unknown.Add($"{section.Title}/{section.Fields[f]}");
                    }
                }

                Assert.IsEmpty(unknown,
                    $"These section entries name fields the config does not have: {string.Join(", ", unknown)}");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        /// <summary>
        ///     The persona scalars are the highest-value controls in the module — one animation set,
        ///     many characters. They used to be reachable only by expanding a raw property dump.
        /// </summary>
        [Test]
        public void PersonaScalars_LiveInThePersonalitySection()
        {
            BodyAnimationConfigSection personality = default;
            bool found = false;
            for (int i = 0; i < BodyAnimationConfigSections.Sections.Length; i++)
            {
                if (BodyAnimationConfigSections.Sections[i].Id != BodyAnimationConfigSections.Personality)
                    continue;
                personality = BodyAnimationConfigSections.Sections[i];
                found = true;
                break;
            }

            Assert.IsTrue(found, "The Personality section must exist.");
            CollectionAssert.Contains(personality.Fields, "_gestureLiveliness");
            CollectionAssert.Contains(personality.Fields, "_calmness");
            Assert.IsTrue(personality.ExpandedByDefault,
                "Personality is the first thing a user should see, so it opens by default.");
        }

        /// <summary>
        ///     Moving Talk is authored through one control. The suppress switch that used to
        ///     shadow it is gone entirely — field, carrier and all — so nothing may reintroduce a
        ///     second way to say the same thing.
        /// </summary>
        [Test]
        public void NoSuppressSwitch_ShadowsMovingTalkMode()
        {
            CollectionAssert.DoesNotContain(BodyAnimationConfigSections.HiddenFields, "_suppressTalkWhileMoving");

            for (int i = 0; i < BodyAnimationConfigSections.Sections.Length; i++)
            {
                BodyAnimationConfigSection section = BodyAnimationConfigSections.Sections[i];
                CollectionAssert.DoesNotContain(section.Fields, "_suppressTalkWhileMoving");
            }
        }

        /// <summary>Every archetype must be reachable and must move the persona scalars.</summary>
        [Test]
        public void Archetypes_AreDistinctAndSetThePersonaScalars()
        {
            Assert.IsTrue(BodyAnimationPersonality.Archetypes.Length >= 4);

            var config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            try
            {
                for (int i = 0; i < BodyAnimationPersonality.Archetypes.Length; i++)
                {
                    BodyAnimationArchetype archetype = BodyAnimationPersonality.Archetypes[i];
                    BodyAnimationPersonality.Apply(config, archetype);

                    Assert.IsTrue(BodyAnimationPersonality.Matches(config, archetype),
                        $"Applying the '{archetype.Name}' archetype must make it the matching one.");
                    Assert.That(config.GestureLiveliness, Is.EqualTo(archetype.Liveliness).Within(0.001f));
                    Assert.That(config.Calmness, Is.EqualTo(archetype.Calmness).Within(0.001f));
                }
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }
    }
}
