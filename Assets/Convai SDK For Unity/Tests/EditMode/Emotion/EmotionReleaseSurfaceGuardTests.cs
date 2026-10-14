using Convai.Domain.Embodiment.Semantics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Domain.Emotion;
using Convai.Editor.UI;
using Convai.Modules.Emotion.Editor;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Emotion;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Guards the three Emotion surfaces a customer meets first: the detection choice, the
    ///     shipped personalities, and the documented preset API.
    /// </summary>
    /// <remarks>
    ///     Every case here is a defect that shipped. The detection dropdown offered the two providers
    ///     swapped, because it used an enum's declaration index as an index into its own label array.
    ///     Three of the four shipped personalities matched no character type, so the Inspector's
    ///     picker showed nothing selected on Convai's own sample characters. And the documented
    ///     preset calls named a two-argument overload that does not exist, so the runnable sample in
    ///     the docs did not compile.
    /// </remarks>
    public sealed class EmotionReleaseSurfaceGuardTests
    {
        private static string PackageRoot => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity"));

        // ------------------------------------------------------------------ detection choice

        /// <summary>
        ///     Each presented option must reach the backend as the provider its wording promises.
        ///     This is the assertion that would have caught the swap: it compares what the user reads
        ///     against the string <see cref="EmotionConfigResolver" /> actually puts on the wire.
        /// </summary>
        [Test]
        public void EachDetectionOption_SelectsTheProviderItsWordingPromises()
        {
            var expected = new Dictionary<EmotionDetectionMode, string>
            {
                { EmotionDetectionMode.Off, null },
                { EmotionDetectionMode.Nrclex, "nrclex" },
                { EmotionDetectionMode.Llm, "llm" }
            };

            for (int i = 0; i < EmotionDetectionModes.Order.Length; i++)
            {
                EmotionDetectionMode mode = EmotionDetectionModes.ValueAt(i);
                Assert.That(EmotionConfigResolver.Resolve(mode)?.Provider,
                    Is.EqualTo(expected[mode]),
                    $"Option {i} (\"{EmotionDetectionModes.Options[i].text}\") selects {mode}, " +
                    "which does not reach the backend as the provider that wording describes.");
            }
        }

        /// <summary>Presentation index and enum value must round-trip, in both directions.</summary>
        [Test]
        public void DetectionModeIndexAndValue_RoundTrip()
        {
            for (int i = 0; i < EmotionDetectionModes.Order.Length; i++)
                Assert.That(EmotionDetectionModes.IndexOf(EmotionDetectionModes.ValueAt(i)), Is.EqualTo(i));

            foreach (EmotionDetectionMode mode in System.Enum.GetValues(typeof(EmotionDetectionMode)))
                Assert.That(EmotionDetectionModes.ValueAt(EmotionDetectionModes.IndexOf(mode)), Is.EqualTo(mode),
                    $"{mode} is not offered by the dropdown, so a character set to it would silently " +
                    "read as something else.");
        }

        [Test]
        public void EveryDetectionOption_HasALabelAndAnExplanation()
        {
            Assert.That(EmotionDetectionModes.Options.Length,
                Is.EqualTo(EmotionDetectionModes.Order.Length),
                "Every offered mode needs a label, and every label needs a mode.");

            foreach (EmotionDetectionMode mode in EmotionDetectionModes.Order)
            {
                Assert.That(EmotionDetectionModes.DescriptionFor(mode),
                    Is.Not.Null.And.Not.Empty, mode.ToString());
                Assert.That(EmotionDetectionModes.ShortNameFor(mode),
                    Is.Not.Null.And.Not.Empty, mode.ToString());
            }
        }

        /// <summary>
        ///     Turning emotions on from the troubleshooter must select a live provider, not Off.
        /// </summary>
        [Test]
        public void TheDefaultDetectionMode_IsALiveProvider()
        {
            Assert.That(EmotionDetectionModes.Default, Is.Not.EqualTo(EmotionDetectionMode.Off));
            Assert.That(EmotionConfigResolver.Resolve(EmotionDetectionModes.Default), Is.Not.Null);
        }

        // ------------------------------------------------------------------ shipped personalities

        private static string EmotionProfileFolder => Path.Combine(
            "Packages", "com.convai.convai-sdk-for-unity", "SamplesShared", "Profiles",
            "Embodiment", "Modules", "Emotion");

        private static ConvaiEmotionProfile LoadShipped(string assetName)
        {
            string path = Path.Combine(EmotionProfileFolder, assetName).Replace('\\', '/');
            var profile = AssetDatabase.LoadAssetAtPath<ConvaiEmotionProfile>(path);
            Assert.That(profile, Is.Not.Null, $"Shipped personality missing: {path}");
            return profile;
        }

        /// <summary>
        ///     Which shipped asset is which character type. Held as a local table rather than as
        ///     <c>TestCase</c> arguments because <c>CharacterDemeanor</c> is internal to the
        ///     module and a public test method cannot take an internal parameter type.
        /// </summary>
        private static readonly (string asset, CharacterDemeanor type)[] ShippedPersonalities =
        {
            ("ConvaiSamplesShared_Emotion_Composed.asset", CharacterDemeanor.Composed),
            ("ConvaiSamplesShared_Emotion_Warm.asset", CharacterDemeanor.Warm),
            ("ConvaiSamplesShared_Emotion_Energetic.asset", CharacterDemeanor.Energetic),
            ("ConvaiSamplesShared_Emotion_Reserved.asset", CharacterDemeanor.Reserved)
        };

        /// <summary>
        ///     Each shipped personality must still identify as the character type it is named for.
        ///     They had been hand-tuned away from the factories, so the Inspector's character-type
        ///     picker highlighted nothing on three of Convai's own sample characters — under
        ///     documentation promising the active one is highlighted.
        /// </summary>
        [Test]
        public void EveryShippedPersonality_IdentifiesAsItsCharacterType()
        {
            var failures = new List<string>();

            foreach ((string asset, CharacterDemeanor type) in ShippedPersonalities)
            {
                ConvaiEmotionProfile profile = LoadShipped(asset);
                CharacterDemeanor? identified = EmotionDemeanorTooling.Identify(profile);

                if (identified != type)
                {
                    failures.Add(
                        $"{asset} reads as {(identified.HasValue ? identified.Value.ToString() : "no character type")}, " +
                        $"not {type} — so the Inspector's picker will show nothing selected for any " +
                        "character using it.");
                }
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        /// <summary>
        ///     The four character types must differ in resting warmth in the order their own
        ///     descriptions promise: a guard shows nothing, a clerk shows civility, the default
        ///     shows warmth, a host shows open cheer.
        /// </summary>
        /// <remarks>
        ///     This ordering was inverted where it mattered most: Energetic — "Host, tour guide,
        ///     streamer" — rested at 0.12 while Warm rested at 0.22, so the loudest type had the
        ///     flattest resting face in the SDK. Its 1.6 joy gain hid the problem in review,
        ///     because gain is applied to incoming detection events and never to the resting fold.
        /// </remarks>
        [Test]
        public void TheCharacterTypes_FormARestingWarmthLadder()
        {
            float reserved = RestingSmileUnits(CharacterDemeanor.Reserved);
            float composed = RestingSmileUnits(CharacterDemeanor.Composed);
            float warm = RestingSmileUnits(CharacterDemeanor.Warm);
            float energetic = RestingSmileUnits(CharacterDemeanor.Energetic);

            Assert.That(reserved, Is.EqualTo(0f),
                "Reserved is the one type chosen to show nothing at rest.");
            Assert.That(composed, Is.GreaterThan(reserved),
                "A receptionist resting at absolute zero reads as vacant.");
            Assert.That(warm, Is.GreaterThan(composed),
                "Warm is the approachable default and must out-warm the merely civil one.");
            Assert.That(energetic, Is.GreaterThanOrEqualTo(warm),
                "A host must not rest less cheerfully than the default character type.");
        }

        /// <summary>
        ///     What a character type's resting mood actually puts on the mouth: the smile-shape
        ///     weight its baseline drives through that emotion's own recipe, per side.
        /// </summary>
        /// <remarks>
        ///     Measured rather than read off the baseline number, because equal intensities are not
        ///     equal faces — <c>trust</c> drives the smile shapes at weights 31/33 where <c>joy</c>
        ///     drives them at 68/71, so the same 0.45 is fourteen units on one and thirty-one on the
        ///     other. A ladder asserted on raw intensity would call two visibly different resting
        ///     faces equally warm, which is the confusion these numbers were tuned to end.
        /// </remarks>
        private static float RestingSmileUnits(CharacterDemeanor type)
        {
            EmotionPersonalityValues values = EmotionPersonalityTable.For(type);
            if (string.IsNullOrWhiteSpace(values.BaselineEmotionLabel) || values.BaselineIntensity <= 0f)
                return 0f;

            EmotionExpressionRecipe recipe = EmotionExpressionRecipe.CreateDefaultSet().Find(
                r => string.Equals(r.EmotionLabel, values.BaselineEmotionLabel,
                    System.StringComparison.OrdinalIgnoreCase));

            Assert.That(recipe, Is.Not.Null,
                $"{type} rests on '{values.BaselineEmotionLabel}', which the built-in expression " +
                "library has no recipe for — so its resting mood renders nothing at all.");

            float total = 0f;
            int sides = 0;
            foreach (EmotionExpressionChannel channel in recipe.Channels)
            {
                if (channel.Semantic != StandardBlendshape.MouthSmileLeft &&
                    channel.Semantic != StandardBlendshape.MouthSmileRight) continue;
                total += channel.FullWeight;
                sides++;
            }

            return sides == 0 ? 0f : values.BaselineIntensity * total / sides;
        }

        /// <summary>
        ///     The two cheerful character types must rest at a smile a customer can actually see,
        ///     on the first click and without going looking for a slider.
        /// </summary>
        /// <remarks>
        ///     Baseline intensity drives the smile shapes directly: <c>MouthSmileLeft</c> at full
        ///     weight 68 through a linear intensity curve, so a baseline of 0.22 asked for roughly
        ///     fifteen units of smile on a hundred-unit blendshape — measurable, invisible. The
        ///     floor is deliberately well under the shipped values so ordinary tuning does not trip
        ///     it; it exists to catch a return to the invisible range.
        /// </remarks>
        [Test]
        public void TheCheerfulCharacterTypes_RestAtAVisibleSmile()
        {
            const float VisibleSmile = 0.4f;

            foreach (CharacterDemeanor type in new[] { CharacterDemeanor.Warm, CharacterDemeanor.Energetic })
            {
                EmotionPersonalityValues values = EmotionPersonalityTable.For(type);

                Assert.That(values.BaselineEmotionLabel, Is.EqualTo("joy"), type.ToString());
                Assert.That(values.BaselineIntensity, Is.GreaterThanOrEqualTo(VisibleSmile),
                    $"{type} rests at {values.BaselineIntensity:0.##} joy, which puts about " +
                    $"{values.BaselineIntensity * 68f:0} units of smile on a hundred-unit blendshape — " +
                    "below what a customer reads as a friendly face.");
            }
        }

        /// <summary>
        ///     A personality made from the Create menu must behave like one the setup button makes.
        /// </summary>
        /// <remarks>
        ///     These are two doors onto the same thing, and they disagreed: the serialized fields
        ///     defaulted mixing and small movements off while every character type turns both on, so
        ///     a personality created by hand was the flattest one in the project and nothing in the
        ///     Inspector said why. Field initialisers are only read when an asset is first created,
        ///     so this cannot be checked by reading the setup path — it has to instantiate the raw
        ///     object, which is exactly what the Create menu does.
        /// </remarks>
        [Test]
        public void AHandMadePersonality_MatchesTheOneSetupWouldHaveMade()
        {
            var handMade = ScriptableObject.CreateInstance<ConvaiEmotionProfile>();
            ConvaiEmotionProfile fromSetup = ConvaiEmotionProfile.CreateDefault();

            try
            {
                // Collected rather than asserted one at a time, so a mismatch names every switch
                // that drifted instead of stopping at the first.
                var drifted = new List<string>();
                if (handMade.EnableEmotionBlending != fromSetup.EnableEmotionBlending) drifted.Add("emotion mixing");
                if (handMade.MicroExpressionsEnabled != fromSetup.MicroExpressionsEnabled) drifted.Add("small movements");
                if (handMade.MoodDriftEnabled != fromSetup.MoodDriftEnabled) drifted.Add("mood drift");
                if (handMade.ContagionEnabled != fromSetup.ContagionEnabled) drifted.Add("mood pickup");
                if (handMade.MicroBurstEnabled != fromSetup.MicroBurstEnabled) drifted.Add("the onset accent");

                Assert.That(drifted, Is.Empty,
                    "A personality created from the Create menu differs from the one setup creates, so a " +
                    "user who made one by hand gets a quieter character with nothing saying why. Differs on: " +
                    string.Join(", ", drifted));
            }
            finally
            {
                Object.DestroyImmediate(handMade);
                Object.DestroyImmediate(fromSetup);
            }
        }

        /// <summary>
        ///     Applying a character type must reproduce that type exactly — otherwise the button that
        ///     applies it and the highlight that reports it disagree the moment it is pressed.
        /// </summary>
        [Test]
        public void ApplyingACharacterType_MakesTheProfileIdentifyAsIt()
        {
            var failures = new List<string>();

            foreach (CharacterDemeanor type in EmotionPersonalityTable.Order)
            {
                var profile = ScriptableObject.CreateInstance<ConvaiEmotionProfile>();
                try
                {
                    var serialized = new SerializedObject(profile);
                    serialized.Update();
                    EmotionDemeanorTooling.Apply(serialized, type, null);
                    serialized.ApplyModifiedProperties();

                    if (EmotionDemeanorTooling.Identify(profile) != type)
                        failures.Add($"Applying {type} produces a profile that does not read as {type}.");
                }
                finally
                {
                    Object.DestroyImmediate(profile);
                }
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        /// <summary>
        ///     Tuning a field a character type authors must not leave the Inspector with four quiet
        ///     pills and no name. That was the defect: identity was inferred, so it vanished.
        /// </summary>
        [Test]
        public void ARetunedPersonality_IsReportedCustom_NotUnselected()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(CharacterDemeanor.Warm, null);
            try
            {
                Assert.IsFalse(EmotionPersonality.IsCustomized(profile),
                    "Sanity: a Warm preset must still identify as Warm.");

                var serialized = new SerializedObject(profile);
                serialized.Update();
                SerializedProperty lerp = serialized.FindProperty("lerpSpeed");
                Assert.IsNotNull(lerp, "The personality still serializes lerpSpeed.");
                lerp.floatValue = 0.13f;
                serialized.ApplyModifiedProperties();

                Assert.IsTrue(EmotionPersonality.IsCustomized(profile),
                    "Retuning how quickly it reacts must drop the Warm identity.");
                Assert.IsFalse(EmotionDemeanorTooling.Identify(profile).HasValue);
                Assert.AreEqual(
                    profile.name + " (" + ConvaiEditorProfileField.CustomLabel + ")",
                    ConvaiEditorProfileField.Summarize(profile, EmotionPersonality.IsCustomized(profile)));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     The setup button and the character-type button must produce the same character. They
        ///     were separate code paths, and the setup path set fields the apply path never wrote.
        /// </summary>
        [Test]
        public void SetupAndApply_ProduceTheSameCharacter()
        {
            var failures = new List<string>();

            foreach (CharacterDemeanor type in EmotionPersonalityTable.Order)
            {
                ConvaiEmotionProfile fromSetup = EmotionSetupService.BuildProfile(type);
                try
                {
                    if (EmotionDemeanorTooling.Identify(fromSetup) != type)
                        failures.Add($"Setting a character up as {type} does not produce {type}.");
                }
                finally
                {
                    Object.DestroyImmediate(fromSetup);
                }
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        /// <summary>
        ///     The four types must be distinguishable. A table entry copy-pasted and left unedited
        ///     would otherwise ship as two buttons that do the same thing, and
        ///     <see cref="EmotionDemeanorTooling.Identify" /> would always report the first one.
        /// </summary>
        [Test]
        public void NoTwoCharacterTypes_AreTheSame()
        {
            var profiles = new Dictionary<CharacterDemeanor, ConvaiEmotionProfile>();
            try
            {
                foreach (CharacterDemeanor type in EmotionPersonalityTable.Order)
                    profiles[type] = EmotionSetupService.BuildProfile(type);

                foreach (CharacterDemeanor a in EmotionPersonalityTable.Order)
                foreach (CharacterDemeanor b in EmotionPersonalityTable.Order)
                {
                    if (a == b) continue;
                    Assert.That(EmotionDemeanorTooling.Matches(profiles[a], b), Is.False,
                        $"{a} and {b} are the same set of values, so they cannot be told apart.");
                }
            }
            finally
            {
                foreach (ConvaiEmotionProfile profile in profiles.Values)
                    Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     Every character type must drive a face. An empty recipe list means the character's
        ///     emotion state updates while nothing moves — the module's most damaging historical
        ///     defect, and one that only shows up at runtime.
        /// </summary>
        [Test]
        public void EveryCharacterType_InstallsExpressionRecipes()
        {
            foreach (CharacterDemeanor type in EmotionPersonalityTable.Order)
            {
                ConvaiEmotionProfile profile = EmotionSetupService.BuildProfile(type);
                try
                {
                    Assert.That(profile.ExpressionRecipes, Is.Not.Empty, type.ToString());
                }
                finally
                {
                    Object.DestroyImmediate(profile);
                }
            }
        }

        // ------------------------------------------------------------------ documented API

        /// <summary>
        ///     Every <c>ConvaiEmotionProfile.Create…</c> call written in the customer documentation
        ///     must exist with the arity the page shows. The page documented all four preset
        ///     factories as taking a second rig argument they have never taken, and its runnable
        ///     sample additionally named a rig-convention member that does not exist — two compile
        ///     errors for anyone who copied it.
        /// </summary>
        [Test]
        public void EveryDocumentedPresetCall_Exists()
        {
            string page = Path.Combine(PackageRoot, "Documentation~", "EMOTIONS.md");
            Assert.That(File.Exists(page), Is.True, page);

            string text = File.ReadAllText(page);
            var failures = new List<string>();

            foreach (Match match in Regex.Matches(text, @"ConvaiEmotionProfile\.(Create\w+)\s*\(([^)]*)\)"))
            {
                string methodName = match.Groups[1].Value;
                string arguments = match.Groups[2].Value.Trim();
                int arity = arguments.Length == 0 ? 0 : arguments.Split(',').Length;

                MethodInfo[] overloads = typeof(ConvaiEmotionProfile)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == methodName)
                    .ToArray();

                if (overloads.Length == 0)
                {
                    failures.Add($"{methodName} is documented but is not public API.");
                    continue;
                }

                if (overloads.All(m => m.GetParameters().Length != arity))
                {
                    failures.Add(
                        $"{methodName} is documented with {arity} argument(s); the real overloads take " +
                        string.Join(" or ", overloads.Select(m => m.GetParameters().Length.ToString())) + ".");
                }
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures.Distinct()));
        }
    }
}
