using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Modules.Embodiment.Presets;
using Convai.Modules.Emotion.Profiles;
using Convai.Modules.Emotion.Taxonomy;
using Convai.Modules.Gaze.Data;
using Convai.Modules.LipSync.Profiles;
using Convai.Runtime;
using Convai.Runtime.Animation;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     Conventions every Convai authoring asset has to hold to, checked against the types
    ///     themselves rather than against a reviewer noticing.
    /// </summary>
    [TestFixture]
    [Category("Architecture")]
    public sealed class ProfileAuthoringConventionGuardTests
    {
        /// <summary>
        ///     Every ScriptableObject a user authors, each of which has a Convai inspector that draws
        ///     the whole body itself.
        /// </summary>
        private static readonly Type[] AuthoringAssets =
        {
            typeof(ConvaiGazeProfile),
            typeof(ConvaiEmotionProfile),
            typeof(EmotionTaxonomyAsset),
            typeof(ConvaiBodyLanguageProfile),
            typeof(ConvaiBodyAnimationProfile),
            typeof(ConvaiBodyAnimationConfig),
            typeof(ConvaiBodyAnimationSet),
            typeof(ConvaiConversationFlowProfile),
            typeof(ConvaiFacialCompositionProfile),
            typeof(ConvaiEmbodimentPreset),
            typeof(ConvaiEmbodimentPresetLibrary),
            typeof(ConvaiLipSyncProfile),
            typeof(ConvaiCharacterProfile),
            typeof(ConvaiRoomManagerProfile)
        };

        [Test]
        public void NoAuthoringAsset_CarriesAHeaderAttribute()
        {
            // Every one of these has a Convai inspector that draws each field into its own named
            // section and never falls back to the default inspector, so [Header] renders nowhere.
            //
            // Inert would be harmless; drifting is not. ConvaiBodyLanguageProfile carried fourteen of
            // them, and one read "State Policies" against a section its editor titles "States" — a
            // disagreement invisible in the running Inspector, so nothing but reading both files
            // side by side could have caught it. The section tables in the editors own the grouping.
            var violations = new List<string>();

            foreach (Type type in AuthoringAssets)
                foreach (FieldInfo field in SerializedFieldsOf(type))
                    if (field.GetCustomAttribute<HeaderAttribute>() != null)
                        violations.Add($"{type.Name}.{field.Name}");

            Assert.That(
                violations,
                Is.Empty,
                "[Header] on an asset whose inspector draws every field itself is never shown. " +
                "Put the grouping in that inspector's section table instead:\n  " +
                string.Join("\n  ", violations));
        }

        // Deliberately not asserted here: that every serialized field carries a [Tooltip]. Several of
        // these assets serialize grouping containers (ConvaiGazeProfile's Targeting, Eyes, Blink &
        // Lids and nine more) whose own inner fields are the controls and are individually
        // documented; a tooltip on the container is noise, not documentation. Per-module tooltip
        // coverage is asserted where the shape of the asset makes the rule meaningful — see
        // BodyLanguageReleaseSurfaceGuardTests.

        /// <summary>
        ///     The fields Unity serializes on <paramref name="type" /> itself: private ones carrying
        ///     <c>[SerializeField]</c> plus public instance fields, excluding anything explicitly
        ///     marked <c>[NonSerialized]</c>.
        /// </summary>
        private static IEnumerable<FieldInfo> SerializedFieldsOf(Type type)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            foreach (FieldInfo field in fields)
            {
                if (field.IsInitOnly) continue;
                if (field.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null) continue;

                yield return field;
            }
        }
    }
}
