using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Runtime.Utilities;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards the rule that an emotion is always chosen from the character's vocabulary and
    ///     never typed: every serialized field that holds the name of an emotion carries
    ///     <see cref="ConvaiEmotionLabelAttribute" />, which is what turns it into a dropdown.
    /// </summary>
    /// <remarks>
    ///     A typed emotion name is a setting that looks configured and does nothing the moment it is
    ///     misspelled, and these fields are spread over six files in two modules — which is exactly
    ///     how three of them had grown dropdowns and the rest had stayed text boxes. The guard is
    ///     name-based on purpose: a new "moodLabel" field anywhere in the package fails here until
    ///     it is either marked or listed below with a reason.
    /// </remarks>
    public sealed class EmotionLabelDropdownGuardTests
    {
        private static readonly string[] PackageAssemblies =
        {
            "Convai.Runtime",
            "Convai.Modules.Emotion",
            "Convai.Modules.BodyAnimation",
            "Convai.Modules.BodyLanguage",
            "Convai.Modules.Gaze",
            "Convai.Modules.Embodiment"
        };

        /// <summary>
        ///     Fields whose name reads like an emotion setting but is not one an author picks.
        /// </summary>
        private static readonly Dictionary<string, string> Exempt = new()
        {
            ["Convai.Runtime.Presentation.Events.CharacterEmotionRelayData._emotion"] =
                "Runtime event output — what the character felt, not a setting anyone authors."
        };

        [Test]
        [Category("Architecture")]
        public void EverySerializedEmotionNameField_IsChosenFromAList()
        {
            var violations = new List<string>();
            int marked = 0;

            foreach (FieldInfo field in EmotionNameFields())
            {
                string id = $"{field.DeclaringType?.FullName}.{field.Name}";
                if (Exempt.ContainsKey(id)) continue;

                if (field.GetCustomAttribute<ConvaiEmotionLabelAttribute>() != null)
                {
                    marked++;
                    continue;
                }

                violations.Add(id);
            }

            Assert.Greater(marked, 0,
                "No emotion-name field was found at all — the scan itself is broken, not the code.");
            Assert.IsEmpty(violations,
                "These serialized fields hold an emotion name and must be marked " +
                "[ConvaiEmotionLabel] so the Inspector offers the character's vocabulary instead of " +
                "a text box (or listed as exempt with a reason):\n" +
                string.Join(Environment.NewLine, violations));
        }

        /// <summary>
        ///     Serialized string fields across the shipped modules whose name says they hold an
        ///     emotion.
        /// </summary>
        private static IEnumerable<FieldInfo> EmotionNameFields()
        {
            foreach (string assemblyName in PackageAssemblies)
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == assemblyName);
                if (assembly == null) continue;

                foreach (Type type in assembly.GetTypes())
                {
                    FieldInfo[] fields = type.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    foreach (FieldInfo field in fields)
                    {
                        if (field.FieldType != typeof(string)) continue;
                        if (!IsSerialized(field)) continue;
                        if (!NamesAnEmotion(field.Name)) continue;
                        yield return field;
                    }
                }
            }
        }

        private static bool IsSerialized(FieldInfo field) =>
            field.GetCustomAttribute<SerializeField>() != null ||
            (field.IsPublic && field.GetCustomAttribute<NonSerializedAttribute>() == null);

        /// <summary>
        ///     Whether a field name says it holds an emotion. Deliberately generous: a false
        ///     positive costs one line in <see cref="Exempt" /> with a reason, a false negative
        ///     costs a text box shipping to a customer.
        /// </summary>
        private static bool NamesAnEmotion(string fieldName)
        {
            string name = fieldName.ToLowerInvariant();
            return name.Contains("emotion") || name.Contains("mood") || name.Contains("reaction");
        }
    }
}
