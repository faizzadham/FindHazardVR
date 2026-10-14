using System.Collections.Generic;
using System.IO;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Guards the settings a customer meets first: the ones Convai ships, against the ones a
    ///     customer gets when they make their own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Four talk values had been hand-tuned on the shipped asset and never brought back to
    ///         the field initialisers, so <b>Create → Convai → Embodiment → Body Animation Config</b>
    ///         produced a character nothing in the SDK resembled. The gap was not subtle: the talk
    ///         overlay could reach full weight against the shipped 0.45 — the control whose own
    ///         tooltip says lower values keep more of the idle pose under speech gestures — and the
    ///         talk layer held 0.65 through silence against the shipped 0.2. A hand-made config
    ///         gestured roughly twice as hard as every Convai sample, and the Inspector said nothing
    ///         about why.
    ///     </para>
    ///     <para>
    ///         Every serialized field is compared rather than those four, because the next
    ///         divergence will be in a field nobody thought to name here. This mirrors
    ///         <c>EmotionReleaseSurfaceGuardTests.AHandMadePersonality_MatchesTheOneSetupWouldHaveMade</c>,
    ///         which exists because the same thing shipped once in Emotion.
    ///     </para>
    /// </remarks>
    public sealed class BodyAnimationShippedConfigGuardTests
    {
        private static readonly string ShippedConfigPath = Path.Combine(
            "Packages", "com.convai.convai-sdk-for-unity", "SamplesShared", "Profiles",
            "Embodiment", "BodyAnimation", "ConvaiBodyAnimationConfig_Default.asset").Replace('\\', '/');

        /// <summary>
        ///     Fields that are allowed to differ, with the reason. Empty on purpose: a value worth
        ///     shipping is a value worth defaulting to, and an exception here needs an argument.
        /// </summary>
        private static readonly HashSet<string> Exempt = new();

        [Test]
        public void TheShippedConfig_MatchesTheOneTheCreateMenuMakes()
        {
            var shipped = AssetDatabase.LoadAssetAtPath<ConvaiBodyAnimationConfig>(ShippedConfigPath);
            Assert.That(shipped, Is.Not.Null, $"Shipped config missing: {ShippedConfigPath}");

            ConvaiBodyAnimationConfig handMade = ConvaiBodyAnimationConfig.CreateDefault();
            try
            {
                var shippedFields = new SerializedObject(shipped);
                var handMadeFields = new SerializedObject(handMade);

                var drifted = new List<string>();
                SerializedProperty walker = shippedFields.GetIterator();

                // enterChildren on the first step only: this compares the config's own fields, not
                // the innards of every AnimationCurve on it.
                bool stepped = walker.NextVisible(true);
                while (stepped)
                {
                    string name = walker.propertyPath;
                    if (name != "m_Script" && !Exempt.Contains(name))
                    {
                        SerializedProperty theirs = handMadeFields.FindProperty(name);
                        if (theirs != null && !SerializedProperty.DataEquals(walker, theirs))
                            drifted.Add($"{name}: shipped {Describe(walker)}, hand-made {Describe(theirs)}");
                    }

                    stepped = walker.NextVisible(false);
                }

                Assert.That(drifted, Is.Empty,
                    "A config made from the Create menu is a different character from the one Convai " +
                    "ships, and nothing in the Inspector says so. Bring the field initialisers in " +
                    "ConvaiBodyAnimationConfig up to the shipped values (or exempt the field here, " +
                    "with the reason):\n" + string.Join("\n", drifted));
            }
            finally
            {
                Object.DestroyImmediate(handMade);
            }
        }

        /// <summary>
        ///     The shipped config must read as one of the four archetypes the picker offers, and as
        ///     the one Convai calls the SDK default.
        /// </summary>
        /// <remarks>
        ///     The Inspector highlights the archetype a config currently is, so a shipped config
        ///     matching none of them would leave the picker blank on Convai's own sample character,
        ///     under documentation promising the active one is highlighted. Emotion shipped exactly
        ///     that on three of its four personalities before a guard like this one caught it.
        /// </remarks>
        [Test]
        public void TheShippedConfig_ReadsAsTheDefaultArchetype()
        {
            var shipped = AssetDatabase.LoadAssetAtPath<ConvaiBodyAnimationConfig>(ShippedConfigPath);
            Assert.That(shipped, Is.Not.Null, $"Shipped config missing: {ShippedConfigPath}");

            var identified = new List<string>();
            foreach (BodyAnimationArchetype archetype in BodyAnimationPersonality.Archetypes)
                if (BodyAnimationPersonality.Matches(shipped, archetype))
                    identified.Add(archetype.Name);

            Assert.That(identified, Is.EqualTo(new[] { CharacterDemeanors.DisplayName(CharacterDemeanor.Warm) }),
                identified.Count == 0
                    ? "The shipped config matches no archetype, so the Inspector's picker shows nothing " +
                      "selected for every character using it."
                    : "The shipped config matches more than one archetype, so which one the picker " +
                      "highlights depends on their declaration order: " + string.Join(", ", identified));
        }

        /// <summary>The value in the message a human has to act on, not the property's type name.</summary>
        private static string Describe(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return property.floatValue.ToString("0.####");
                case SerializedPropertyType.Integer: return property.intValue.ToString();
                case SerializedPropertyType.Boolean: return property.boolValue ? "on" : "off";
                case SerializedPropertyType.Enum: return property.enumValueIndex.ToString();
                case SerializedPropertyType.String: return $"\"{property.stringValue}\"";
                default: return property.propertyType.ToString();
            }
        }
    }
}
