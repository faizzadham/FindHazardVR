using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.Embodiment.Semantics;
using Convai.Domain.Emotion;
using Convai.Editor.AI;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Editor;
using Convai.Modules.Emotion.Editor.AI;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Components;
using Newtonsoft.Json.Linq;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Tests.EditMode.AI
{
    /// <summary>
    ///     Contract and behaviour coverage for the Convai Emotion MCP tools.
    /// </summary>
    /// <remarks>
    ///     Three tests here are load-bearing rather than incidental.
    ///     <see cref="DiagnoseReportsExactlyWhatTheSetupServiceAndTroubleshooterReport" /> keeps the
    ///     assistant-facing surface a projection of <c>EmotionSetupService</c> rather than a second
    ///     opinion about the same character. <see cref="DetectionModesAreNotSwapped" /> holds the
    ///     one regression this module has already shipped once. And the behaviour tests hold the
    ///     rule that a stored field value is never reported as what a user observes.
    /// </remarks>
    public sealed class ConvaiEmotionMcpToolTests
    {
        private static readonly string _tempFolder =
            $"{TestAssetSandbox.Root}/{nameof(ConvaiEmotionMcpToolTests)}";

        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;
        private bool _createdAssets;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
        }

        [TearDown]
        public void TearDown()
        {
            if (_createdAssets)
            {
                TestAssetSandbox.Clear(nameof(ConvaiEmotionMcpToolTests));
                _createdAssets = false;
                AssetDatabase.Refresh();
            }

            _sceneFixture.End();
        }

        /// <summary>
        ///     Gives this test the loaded scene population to itself, so a question the product asks
        ///     about every open scene is not answered by whatever scene the developer left open.
        /// </summary>
        private void TakeTheSceneOver<T>() where T : Component => _sceneFixture.IsolateScenePopulation<T>();

        // ------------------------------------------------------------------ contract

        [Test]
        public void CatalogCarriesEveryEmotionTool()
        {
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.ConfigureEmotion"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.DiagnoseEmotion"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.InspectEmotionPersonalities"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.TuneEmotionPersonality"));
        }

        [Test]
        public void SchemasAreClosed()
        {
            JObject configure = JObject.FromObject(ConvaiEmotionMcpTools.ConfigureSchema());
            Assert.That(configure.Value<bool>("additionalProperties"), Is.False);
            Assert.That(configure["required"]?.Values<string>(),
                Is.EquivalentTo(new[] { "characterInstanceId" }));

            JObject diagnose = JObject.FromObject(ConvaiEmotionMcpTools.DiagnoseSchema());
            Assert.That(diagnose.Value<bool>("additionalProperties"), Is.False);

            JObject personalities =
                JObject.FromObject(ConvaiEmotionMcpTools.InspectPersonalitiesSchema());
            Assert.That(personalities.Value<bool>("additionalProperties"), Is.False);

            JObject tune = JObject.FromObject(ConvaiEmotionMcpTools.TuneSchema());
            Assert.That(tune.Value<bool>("additionalProperties"), Is.False);
            Assert.That(tune["required"]?.Values<string>(),
                Is.EquivalentTo(new[] { "characterInstanceId" }));
        }

        [Test]
        public void TuningFieldsDeclareNoDefaultSoOmittingOneLeavesItAlone()
        {
            JToken properties = JObject.FromObject(ConvaiEmotionMcpTools.TuneSchema())["properties"];

            foreach (string field in new[]
                     {
                         "characterType", "restingMood", "restingMoodStrength", "howStronglyItShows",
                         "howQuicklyItReacts", "neverSitsPerfectlyStill", "moodFollowsConversation",
                         "showsMoreThanOneEmotion", "picksUpOtherCharactersMoods"
                     })
            {
                Assert.That(properties?[field], Is.Not.Null, $"{field} is missing from the schema.");
                Assert.That(properties[field]["default"], Is.Null,
                    $"{field} declares a default, which tells an assistant that omitting it asks " +
                    "for that value. Omitting it must leave the personality alone.");
            }
        }

        [Test]
        public void EveryResponseCarriesTheStandardEnvelope()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Envelope");

            foreach (object response in new[]
                     {
                         ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiEmotionMcpTools.InspectPersonalities(
                             new InspectEmotionPersonalitiesRequest()),
                         ConvaiEmotionMcpTools.Tune(new TuneEmotionPersonalityRequest
                             { CharacterInstanceId = Id(character.gameObject) })
                     })
            {
                JObject json = Json(response);
                Assert.That(json["success"], Is.Not.Null);
                Assert.That(json["message"], Is.Not.Null);
                Assert.That(json["data"], Is.Not.Null);
            }
        }

        // ------------------------------------------------------------------ configure

        [Test]
        public void ConfigurePreviewChangesNothing()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Preview");

            JObject response = Json(ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                EmotionDetection = "Accurate",
                RestingMood = "joy"
            }));

            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(response["data"]?["changes"]?.Values<string>().Any(), Is.True);
            Assert.That(character.GetComponentInChildren<ConvaiEmotionController>(true), Is.Null,
                "A preview added the component.");
        }

        [Test]
        public void ConfigureAddsTheComponentAndWritesOnlyTheCharacter()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Apply");

            Json(ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                EmotionDetection = "Accurate",
                DryRun = false
            }));

            var controller = character.GetComponentInChildren<ConvaiEmotionController>(true);
            Assert.That(controller, Is.Not.Null);
            Assert.That(controller.EmotionDetectionMode, Is.EqualTo(EmotionDetectionMode.Llm));
            Assert.That(EmotionSetupService.ResolveAssignedProfile(controller), Is.Null,
                "Configure assigned a personality that was never asked for, or created one.");
        }

        [Test]
        public void ConfigureRefusesACharacterWithNoFaceAndAddsNothing()
        {
            ConvaiCharacter character = CreateCharacter("NoFace");

            JObject response = Json(ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("EMOTION_FACE"));
            Assert.That(response["data"]?.Value<string>("blocker"), Does.Contain("blendshapes"));
            Assert.That(character.GetComponentInChildren<ConvaiEmotionController>(true), Is.Null);
        }

        [Test]
        public void ConfigureRejectsAPersonalityPathThatDoesNotExistAndCreatesNothing()
        {
            ConvaiCharacter character = CreateCharacterWithFace("BadPath");

            JObject response = Json(ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                PersonalityAssetPath = "Assets/DoesNotExist_Emotion.asset",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("PERSONALITY_NOT_FOUND"));
            Assert.That(AssetDatabase.LoadAssetAtPath<ConvaiEmotionProfile>(
                "Assets/DoesNotExist_Emotion.asset"), Is.Null);
        }

        [Test]
        public void ConfigureRejectsAnUnknownRestingMoodAndNamesWhatTheCharacterKnows()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Typo");
            ConvaiEmotionController controller = AddController(character);
            AssignPersonality(controller, CreatePersonalityAsset("Typo", CharacterDemeanor.Warm));

            JObject response = Json(ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                RestingMood = "joyy"
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("UNKNOWN_EMOTION"));
            Assert.That(response["data"]?["knownEmotions"]?.Values<string>(), Does.Contain("joy"));
        }

        [Test]
        public void ConfigureGivesARestingMoodAUsableStrengthRatherThanLeavingItAtZero()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Strength");
            ConvaiEmotionController controller = AddController(character);
            AssignPersonality(controller, CreatePersonalityAsset("Strength", CharacterDemeanor.Warm));
            SetControllerFloat(controller, "initialMoodIntensity", 0f);

            Json(ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                RestingMood = "sadness",
                DryRun = false
            }));

            Assert.That(ReadControllerFloat(controller, "initialMoodIntensity"), Is.GreaterThan(0f),
                "A resting mood was set at 0 strength, which shows nothing.");
        }

        // ------------------------------------------------------------------ diagnose

        [Test]
        public void DiagnoseOnABareCharacterReportsNotInstalledRatherThanThrowing()
        {
            ConvaiCharacter character = CreateCharacter("Bare");

            JObject response = Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("present"), Is.False);
            Assert.That(response["data"]?["readiness"]?.Value<string>("state"),
                Is.EqualTo(nameof(EmotionReadiness.NotInstalled)));
            Assert.That(string.Join(" ", response["data"]?["nextSteps"]?.Values<string>()),
                Does.Contain("Convai → Embodiment → Emotion").Or.Contain("blendshapes"));
        }

        [Test]
        public void DiagnoseReportsExactlyWhatTheSetupServiceAndTroubleshooterReport()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Parity");
            ConvaiEmotionController controller = AddController(character);
            AssignPersonality(controller, CreatePersonalityAsset("Parity", CharacterDemeanor.Warm));
            SetDetection(controller, EmotionDetectionMode.Off);

            JObject response = Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            EmotionPreflight preflight = EmotionSetupService.Inspect(controller);
            var findings = new List<EmotionFinding>();
            EmotionTroubleshooter.Evaluate(controller, in preflight, findings);

            JToken[] checks = response["data"]?["checks"]?.ToArray() ?? Array.Empty<JToken>();
            Assert.That(checks.Length, Is.EqualTo(preflight.Checks.Count),
                "The tool reports a different number of checks than the setup service produces.");
            for (int i = 0; i < checks.Length; i++)
            {
                Assert.That(checks[i].Value<string>("label"), Is.EqualTo(preflight.Checks[i].Label));
                Assert.That(checks[i].Value<string>("detail"), Is.EqualTo(preflight.Checks[i].Detail));
                Assert.That(checks[i].Value<string>("state"),
                    Is.EqualTo(preflight.Checks[i].State.ToString()));
            }

            string issues = string.Join(" ", response["data"]?["issues"]?
                .Select(issue => issue.Value<string>("message")) ?? Array.Empty<string>());
            for (int i = 0; i < findings.Count; i++)
            {
                Assert.That(issues, Does.Contain(findings[i].Message),
                    $"The tool dropped or reworded the '{findings[i].Title}' finding, so it and the " +
                    "Inspector now describe this character differently.");
            }
        }

        [Test]
        public void DiagnoseSeparatesInertFromWorking()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Inert");
            ConvaiEmotionController controller = AddController(character);
            SetDetection(controller, EmotionDetectionMode.Off);

            Assert.That(ReadState(character), Is.EqualTo(nameof(EmotionReadiness.Inert)),
                "A character that receives no feelings was reported as working.");

            SetDetection(controller, EmotionDetectionMode.Nrclex);
            Assert.That(ReadState(character), Is.EqualTo(nameof(EmotionReadiness.Working)));
        }

        [Test]
        public void DiagnoseReportsBlockedWhenTheCharacterHasNoFace()
        {
            ConvaiCharacter character = CreateCharacter("Blocked");
            AddController(character);

            Assert.That(ReadState(character), Is.EqualTo(nameof(EmotionReadiness.Blocked)));
        }

        [Test]
        public void DiagnoseIsReadOnly()
        {
            ConvaiCharacter character = CreateCharacterWithFace("ReadOnly");
            ConvaiEmotionController controller = AddController(character);
            ConvaiEmotionProfile personality = CreatePersonalityAsset("ReadOnly", CharacterDemeanor.Warm);
            AssignPersonality(controller, personality);
            SetDetection(controller, EmotionDetectionMode.Off);

            bool restingBefore = personality.MicroExpressionsEnabled;
            EmotionDetectionMode detectionBefore = controller.EmotionDetectionMode;

            Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(personality.MicroExpressionsEnabled, Is.EqualTo(restingBefore));
            Assert.That(controller.EmotionDetectionMode, Is.EqualTo(detectionBefore));
        }

        // ------------------------------------------------------------------ the two landmines

        [Test]
        public void DetectionModesAreNotSwapped()
        {
            // The enum's declaration order is Off, Llm, Nrclex, which is NOT the order these are
            // presented in. Using a presentation index as an enum value — or the reverse — is what
            // shipped Responsive and Accurate the wrong way round. Both directions are asserted
            // because the bug is only visible when the two disagree.
            Assert.That(EmotionDetectionModes.ShortNameFor(EmotionDetectionMode.Nrclex),
                Is.EqualTo("Responsive"));
            Assert.That(EmotionDetectionModes.ShortNameFor(EmotionDetectionMode.Llm),
                Is.EqualTo("Accurate"));
            Assert.That((int)EmotionDetectionMode.Llm, Is.LessThan((int)EmotionDetectionMode.Nrclex),
                "The enum's declaration order changed, so the presentation table must be re-checked.");

            ConvaiCharacter character = CreateCharacterWithFace("Detection");

            Json(ConvaiEmotionMcpTools.Configure(new ConfigureEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                EmotionDetection = "Responsive",
                DryRun = false
            }));

            var controller = character.GetComponentInChildren<ConvaiEmotionController>(true);
            Assert.That(controller.EmotionDetectionMode, Is.EqualTo(EmotionDetectionMode.Nrclex),
                "Asking for Responsive selected the other provider — the shipped bug, again.");

            JObject diagnose = Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));
            Assert.That(diagnose["data"]?["detection"]?.Value<string>("mode"), Is.EqualTo("Responsive"));
        }

        [Test]
        public void AGatedReactionIsReportedAsOffEvenThoughItsStoredValueIsAboveZero()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Gated");
            ConvaiEmotionController controller = AddController(character);
            ConvaiEmotionProfile personality = CreatePersonalityAsset("Gated", CharacterDemeanor.Warm);
            SetProfileFloat(personality, "listeningReactionStrength", 0.35f);
            SetProfileBool(personality, "microExpressionsEnabled", false);
            AssignPersonality(controller, personality);

            Assert.That(personality.ListeningReactionStrength, Is.EqualTo(0.35f).Within(1e-4f),
                "The fixture did not store the value this test is about.");

            JToken listening = ReadBehaviour(character, "Listening reactions");
            Assert.That(listening.Value<bool>("effective"), Is.False,
                "A reaction stored at 0.35 was reported as on while the layer that plays it is off.");
            Assert.That(listening.Value<string>("why"), Does.Contain("Never sits perfectly still"));
        }

        [Test]
        public void PickingUpOtherCharactersMoodsIsReportedAsOffInASceneWithOneCharacter()
        {
            // This behaviour is a question about every loaded scene, so the answer depends on scenes
            // this test never created: the shipped LipSync Sample scene alone holds an emotion-bearing
            // character, and with it open the assertion below inverts. The scene population has to be
            // part of the fixture rather than part of the developer's editor state.
            TakeTheSceneOver<ConvaiEmotionController>();

            ConvaiCharacter character = CreateCharacterWithFace("Alone");
            ConvaiEmotionController controller = AddController(character);
            ConvaiEmotionProfile personality = CreatePersonalityAsset("Alone", CharacterDemeanor.Warm);
            SetProfileBool(personality, "contagionEnabled", true);
            AssignPersonality(controller, personality);

            JToken contagion = ReadBehaviour(character, "Picks up other characters' moods");
            Assert.That(contagion.Value<bool>("effective"), Is.False,
                "A toggle that is on but has nobody to react to was reported as on.");
            Assert.That(contagion.Value<string>("why"), Does.Contain("no other Convai character"));
        }

        // ------------------------------------------------------------------ resting mood chain

        [Test]
        public void RestingMoodReportsWhichLinkWon()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Chain");
            ConvaiEmotionController controller = AddController(character);
            ConvaiEmotionProfile personality = CreatePersonalityAsset("Chain", CharacterDemeanor.Composed);
            AssignPersonality(controller, personality);

            // Composed rests on its own faint trust, reported as coming from the personality.
            Assert.That(ReadRestingMood(character).Value<string>("decidedBy"),
                Is.EqualTo(nameof(EmotionRestingMoodSource.ProfileBaseline)));

            // Clearing it is the case where nothing decides the resting mood at all.
            SetProfileString(personality, "baselineEmotionLabel", string.Empty);
            SetProfileFloat(personality, "baselineIntensity", 0f);
            Assert.That(ReadRestingMood(character).Value<string>("decidedBy"),
                Is.EqualTo(nameof(EmotionRestingMoodSource.None)));

            // The personality's own resting mood.
            SetProfileString(personality, "baselineEmotionLabel", "joy");
            SetProfileFloat(personality, "baselineIntensity", 0.22f);
            JToken fromProfile = ReadRestingMood(character);
            Assert.That(fromProfile.Value<string>("decidedBy"),
                Is.EqualTo(nameof(EmotionRestingMoodSource.ProfileBaseline)));
            Assert.That(fromProfile.Value<string>("effectiveLabel"), Is.EqualTo("joy"));

            // This character overriding it.
            SetControllerString(controller, "initialMoodLabel", "sadness");
            SetControllerFloat(controller, "initialMoodIntensity", 0.4f);
            JToken fromOverride = ReadRestingMood(character);
            Assert.That(fromOverride.Value<string>("decidedBy"),
                Is.EqualTo(nameof(EmotionRestingMoodSource.InitialMoodOverride)));
            Assert.That(fromOverride.Value<string>("effectiveLabel"), Is.EqualTo("sadness"));
            Assert.That(fromOverride.Value<string>("suppressed"), Does.Contain("joy"));

            // Forcing a neutral rest SUPPRESSES the personality rather than falling through to it —
            // the one case where "no mood" and "use the personality's mood" differ.
            SetControllerString(controller, "initialMoodLabel", "neutral");
            JToken forcedNeutral = ReadRestingMood(character);
            Assert.That(forcedNeutral.Value<string>("decidedBy"),
                Is.EqualTo(nameof(EmotionRestingMoodSource.ForcedNeutralOverride)));
            Assert.That(forcedNeutral.Value<float>("effectiveStrength"), Is.EqualTo(0f));
            Assert.That(forcedNeutral.Value<string>("suppressed"), Does.Contain("joy"),
                "Forcing neutral reported the personality's mood as winning.");
        }

        [Test]
        public void ARestingMoodLabelThatDoesNotResolveIsReportedRatherThanSilentlyIgnored()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Unknown");
            ConvaiEmotionController controller = AddController(character);
            ConvaiEmotionProfile personality = CreatePersonalityAsset("Unknown", CharacterDemeanor.Warm);
            AssignPersonality(controller, personality);
            SetControllerString(controller, "initialMoodLabel", "joyy");

            JToken resting = ReadRestingMood(character);
            Assert.That(resting.Value<bool>("labelResolves"), Is.False);
            Assert.That(resting.Value<string>("decidedBy"),
                Is.Not.EqualTo(nameof(EmotionRestingMoodSource.InitialMoodOverride)),
                "A label the vocabulary does not define was reported as having taken effect.");
        }

        // ------------------------------------------------------------------ tune

        [Test]
        public void TuneRefusesAnSdkShippedPersonalityWithoutConsentAndWritesNothing()
        {
            ConvaiEmotionProfile shipped = LoadShippedPersonality();
            if (shipped == null) Assert.Ignore("This project has no SDK-shipped emotion personality.");

            ConvaiCharacter character = CreateCharacterWithFace("Shipped");
            ConvaiEmotionController controller = AddController(character);
            AssignPersonality(controller, shipped);

            bool before = shipped.MoodDriftEnabled;
            JObject response = Json(ConvaiEmotionMcpTools.Tune(new TuneEmotionPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                MoodFollowsConversation = !before,
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"),
                Is.EqualTo("PERSONALITY_SHARED_CONSENT_REQUIRED"));
            Assert.That(shipped.MoodDriftEnabled, Is.EqualTo(before),
                "An SDK-shipped personality was written to.");
            Assert.That(EmotionSetupService.ResolveAssignedProfile(controller), Is.EqualTo(shipped),
                "The character was moved off the shipped personality without consent.");
        }

        [Test]
        public void TuneDryRunCreatesNoAssetAndPreviewsTheSameChangesTheRealRunWouldMake()
        {
            ConvaiCharacter character = CreateCharacterWithFace("DryRun");
            ConvaiEmotionController controller = AddController(character);
            ConvaiEmotionProfile personality = CreatePersonalityAsset("DryRun", CharacterDemeanor.Composed);
            AssignPersonality(controller, personality);

            string[] before = AssetDatabase.FindAssets("t:ConvaiEmotionProfile");

            JObject preview = Json(ConvaiEmotionMcpTools.Tune(new TuneEmotionPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                ShowsMoreThanOneEmotion = !personality.EnableEmotionBlending
            }));

            Assert.That(preview["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(preview["data"]?.Value<bool>("applied"), Is.False);
            Assert.That(AssetDatabase.FindAssets("t:ConvaiEmotionProfile").Length,
                Is.EqualTo(before.Length), "A preview created an asset.");

            string[] previewed = preview["data"]?["changedFields"]?
                .Select(field => field.Value<string>("label")).ToArray() ?? Array.Empty<string>();
            Assert.That(previewed, Does.Contain("Shows more than one emotion at once"));
        }

        [Test]
        public void TuneOnAPrivatePersonalityWritesItInPlaceAndIsIdempotent()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Private");
            ConvaiEmotionController controller = AddController(character);
            ConvaiEmotionProfile personality = CreatePersonalityAsset("Private", CharacterDemeanor.Composed);
            AssignPersonality(controller, personality);
            bool target = !personality.MoodDriftEnabled;

            JObject applied = Json(ConvaiEmotionMcpTools.Tune(new TuneEmotionPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                MoodFollowsConversation = target,
                DryRun = false
            }));

            Assert.That(applied["data"]?.Value<bool>("applied"), Is.True);
            Assert.That(applied["data"]?.Value<string>("createdAssetPath"), Is.Empty,
                "A personality this character already owns was copied anyway.");
            Assert.That(personality.MoodDriftEnabled, Is.EqualTo(target));

            JObject again = Json(ConvaiEmotionMcpTools.Tune(new TuneEmotionPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                MoodFollowsConversation = target,
                DryRun = false
            }));
            Assert.That(again["data"]?["changedFields"]?.Any(), Is.False,
                "Re-applying the same value reported a change.");
        }

        [Test]
        public void TuneRefusesACharacterWithNoPersonalityRatherThanCreatingOne()
        {
            ConvaiCharacter character = CreateCharacterWithFace("NoPersonality");
            AddController(character);

            string[] before = AssetDatabase.FindAssets("t:ConvaiEmotionProfile");
            JObject response = Json(ConvaiEmotionMcpTools.Tune(new TuneEmotionPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                CharacterType = "Warm",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("PERSONALITY_MISSING"));
            Assert.That(AssetDatabase.FindAssets("t:ConvaiEmotionProfile").Length,
                Is.EqualTo(before.Length), "A personality was created from nothing.");
        }

        [Test]
        public void TuneRejectsAnUnknownCharacterType()
        {
            ConvaiCharacter character = CreateCharacterWithFace("BadType");
            ConvaiEmotionController controller = AddController(character);
            AssignPersonality(controller, CreatePersonalityAsset("BadType", CharacterDemeanor.Warm));

            JObject response = Json(ConvaiEmotionMcpTools.Tune(new TuneEmotionPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                CharacterType = "Grumpy"
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("UNKNOWN_CHARACTER_TYPE"));
        }

        // ------------------------------------------------------------------ personalities

        [Test]
        public void InspectPersonalitiesReportsCharacterTypeAndOwnership()
        {
            CreatePersonalityAsset("Listed", CharacterDemeanor.Energetic);

            JObject response = Json(ConvaiEmotionMcpTools.InspectPersonalities(
                new InspectEmotionPersonalitiesRequest { FolderPaths = new[] { _tempFolder } }));

            JToken listed = response["data"]?["personalities"]?
                .FirstOrDefault(item => item.Value<string>("name") == "Listed");
            Assert.That(listed, Is.Not.Null);
            Assert.That(listed.Value<string>("characterType"), Is.EqualTo("Energetic"));
            Assert.That(listed.Value<bool>("shipsWithSdk"), Is.False);
            Assert.That(listed.Value<bool>("isEditableInPlace"), Is.True);
        }

        [Test]
        public void InspectPersonalitiesInAnEmptyFolderReportsNoneWithARouteRatherThanAnError()
        {
            JObject response = Json(ConvaiEmotionMcpTools.InspectPersonalities(
                new InspectEmotionPersonalitiesRequest
                {
                    FolderPaths = new[] { _tempFolder + "_Empty" }
                }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<int>("count"), Is.EqualTo(0));
            Assert.That(response["data"]?.Value<string>("setUpRoute"), Is.Not.Empty);
        }

        // ------------------------------------------------------------------ integration

        [Test]
        public void SceneSurveyAgreesWithDiagnose()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Survey");
            AddController(character);

            JObject diagnose = Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            ConvaiModuleSurveyResult survey = ConvaiModuleSurveyRegistry.All
                .Single(surveyor => surveyor.ModuleId == "convai.emotion")
                .Survey(character.gameObject);

            Assert.That(survey.IsPresent, Is.EqualTo(diagnose["data"]?.Value<bool>("present")));
            Assert.That(survey.IsFunctional, Is.EqualTo(diagnose["data"]?.Value<bool>("isWorking")));
            Assert.That(survey.Summary, Is.EqualTo(diagnose["data"]?.Value<string>("summary")));
        }

        [Test]
        public void GuidanceForTheModuleNamesAllFourToolsAndItsDocumentation()
        {
            JObject response = Json(ConvaiMcpTools.GetGuidance(new ConvaiGuidanceRequest
            {
                Topic = ConvaiGuidanceTopic.Emotion
            }));

            string[] tools = response["data"]?["ConvaiTools"]?.Values<string>().ToArray()
                             ?? Array.Empty<string>();
            Assert.That(tools, Does.Contain("Convai.ConfigureEmotion"));
            Assert.That(tools, Does.Contain("Convai.DiagnoseEmotion"));
            Assert.That(tools, Does.Contain("Convai.InspectEmotionPersonalities"));
            Assert.That(tools, Does.Contain("Convai.TuneEmotionPersonality"));
            Assert.That(string.Join(" ", response["data"]?["Documentation"]?.Values<string>()
                                         ?? Array.Empty<string>()),
                Does.Contain("EMOTIONS.md"));
        }

        [Test]
        public void NoUserFacingTextLeaksTheWireNamesForTheDetectionProviders()
        {
            ConvaiCharacter character = CreateCharacterWithFace("Vocabulary");
            ConvaiEmotionController controller = AddController(character);
            AssignPersonality(controller, CreatePersonalityAsset("Vocabulary", CharacterDemeanor.Warm));

            string text = Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            })).ToString();

            Assert.That(text, Does.Not.Contain("Nrclex"));
            Assert.That(text, Does.Not.Contain("NRCLex"));
            Assert.That(text, Does.Not.Contain("Llm"));
        }

        // ------------------------------------------------------------------ helpers

        private ConvaiCharacter CreateCharacter(string name)
        {
            var host = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(host, "test");
            SceneManager.MoveGameObjectToScene(host, TestScene);
            return Undo.AddComponent<ConvaiCharacter>(host);
        }

        /// <summary>A character with a skinned mesh that has blendshapes — the one hard requirement.</summary>
        private ConvaiCharacter CreateCharacterWithFace(string name)
        {
            ConvaiCharacter character = CreateCharacter(name);

            var faceHost = new GameObject("Face");
            Undo.RegisterCreatedObjectUndo(faceHost, "test");
            faceHost.transform.SetParent(character.transform, false);

            SkinnedMeshRenderer renderer = Undo.AddComponent<SkinnedMeshRenderer>(faceHost);
            renderer.sharedMesh = BuildBlendshapeMesh();
            return character;
        }

        /// <summary>
        ///     A one-triangle mesh carrying ARKit-named blendshapes, so rig-convention detection has
        ///     something real to recognise rather than reporting an unknown rig on every test.
        /// </summary>
        private static Mesh BuildBlendshapeMesh()
        {
            var mesh = new Mesh
            {
                name = "ConvaiEmotionTestFace",
                vertices = new[] { Vector3.zero, Vector3.up, Vector3.right },
                triangles = new[] { 0, 1, 2 }
            };
            mesh.RecalculateNormals();

            var deltas = new[] { Vector3.up * 0.01f, Vector3.up * 0.01f, Vector3.up * 0.01f };
            var zero = new Vector3[3];
            foreach (string shape in new[]
                     {
                         "mouthSmileLeft", "mouthSmileRight", "browInnerUp", "browOuterUpLeft",
                         "browOuterUpRight", "browDownLeft", "browDownRight", "cheekSquintLeft",
                         "cheekSquintRight", "eyeSquintLeft", "eyeSquintRight", "jawOpen",
                         "mouthFrownLeft", "mouthFrownRight", "noseSneerLeft", "noseSneerRight"
                     })
                mesh.AddBlendShapeFrame(shape, 100f, deltas, zero, zero);

            return mesh;
        }

        private ConvaiEmotionController AddController(ConvaiCharacter character) =>
            Undo.AddComponent<ConvaiEmotionController>(character.gameObject);

        private ConvaiEmotionProfile CreatePersonalityAsset(string name, CharacterDemeanor type)
        {
            TestAssetSandbox.Ensure(nameof(ConvaiEmotionMcpToolTests));

            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreatePreset(type, null);
            string path = $"{_tempFolder}/{name}.asset";
            AssetDatabase.CreateAsset(profile, path);
            _createdAssets = true;
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<ConvaiEmotionProfile>(path);
        }

        private static ConvaiEmotionProfile LoadShippedPersonality()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:ConvaiEmotionProfile"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                var profile = AssetDatabase.LoadAssetAtPath<ConvaiEmotionProfile>(path);
                if (profile != null) return profile;
            }

            return null;
        }

        private static void AssignPersonality(
            ConvaiEmotionController controller, ConvaiEmotionProfile personality)
        {
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("profile").objectReferenceValue = personality;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Written by VALUE, never enumValueIndex — see <see cref="DetectionModesAreNotSwapped" />.</summary>
        private static void SetDetection(ConvaiEmotionController controller, EmotionDetectionMode mode)
        {
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("detectionMode").intValue = (int)mode;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetControllerString(
            ConvaiEmotionController controller, string field, string value)
        {
            var serialized = new SerializedObject(controller);
            serialized.FindProperty(field).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetControllerFloat(
            ConvaiEmotionController controller, string field, float value)
        {
            var serialized = new SerializedObject(controller);
            serialized.FindProperty(field).floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static float ReadControllerFloat(ConvaiEmotionController controller, string field) =>
            new SerializedObject(controller).FindProperty(field).floatValue;

        private static void SetProfileBool(ConvaiEmotionProfile profile, string field, bool value)
        {
            var serialized = new SerializedObject(profile);
            SerializedProperty property = serialized.FindProperty(field);
            Assert.That(property, Is.Not.Null, $"ConvaiEmotionProfile no longer serialises {field}.");
            property.boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetProfileFloat(ConvaiEmotionProfile profile, string field, float value)
        {
            var serialized = new SerializedObject(profile);
            SerializedProperty property = serialized.FindProperty(field);
            Assert.That(property, Is.Not.Null, $"ConvaiEmotionProfile no longer serialises {field}.");
            property.floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetProfileString(ConvaiEmotionProfile profile, string field, string value)
        {
            var serialized = new SerializedObject(profile);
            SerializedProperty property = serialized.FindProperty(field);
            Assert.That(property, Is.Not.Null, $"ConvaiEmotionProfile no longer serialises {field}.");
            property.stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private string ReadState(ConvaiCharacter character) =>
            Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }))["data"]?["readiness"]?.Value<string>("state");

        private JToken ReadRestingMood(ConvaiCharacter character) =>
            Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }))["data"]?["restingMood"];

        private JToken ReadBehaviour(ConvaiCharacter character, string label)
        {
            JToken behaviour = Json(ConvaiEmotionMcpTools.Diagnose(new DiagnoseEmotionRequest
                {
                    CharacterInstanceId = Id(character.gameObject)
                }))["data"]?["behaviour"]?
                .FirstOrDefault(item => item.Value<string>("label") == label);
            Assert.That(behaviour, Is.Not.Null, $"The diagnosis has no '{label}' behaviour row.");
            return behaviour;
        }

        private static long Id(GameObject value) => ConvaiMcpEntityRef.ToToolId(value);

        private static JObject Json(object response) => JObject.FromObject(response);

        [Test]
        public void SceneSurveyReportsExactlyWhatTheTroubleshooterReports()
        {
            ConvaiCharacter character = CreateCharacterWithFace("SurveyParity");
            ConvaiEmotionController controller = AddController(character);
            SetDetection(controller, EmotionDetectionMode.Off);

            // Deliberately no personality: this is the arrangement the two surfaces disagreed on.
            EmotionPreflight preflight = EmotionSetupService.Inspect(controller);
            var findings = new List<EmotionFinding>();
            EmotionTroubleshooter.Evaluate(controller, in preflight, findings);

            Assert.That(findings.Count, Is.GreaterThan(0),
                "Sanity: a character with detection off must produce at least one finding.");

            ConvaiModuleSurveyResult survey =
                new ConvaiEmotionModuleSurveyor().Survey(character.gameObject);
            string[] surveyed = survey.Findings.Select(f => f.Message).ToArray();

            foreach (EmotionFinding finding in findings)
                Assert.That(surveyed, Does.Contain(finding.Message),
                    $"The survey dropped or reworded the '{finding.Title}' finding.");

            // And nothing beyond the shared set plus the preflight rows it is allowed to carry.
            string[] allowed = findings.Select(f => f.Message)
                .Concat(preflight.Checks.Select(c => c.Detail))
                .ToArray();
            foreach (string message in surveyed)
                Assert.That(allowed, Does.Contain(message),
                    "The survey reported something neither the troubleshooter nor the setup service " +
                    $"produced, so an assistant and the Inspector describe this character differently: {message}");
        }

    }
}
