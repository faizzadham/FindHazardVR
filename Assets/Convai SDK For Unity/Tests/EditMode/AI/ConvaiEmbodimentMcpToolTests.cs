using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Editor.AI;
using Convai.Editor.Embodiment.AI;
using Convai.Editor.Embodiment.Setup;
using Convai.Modules.Embodiment.Components;
using Convai.Modules.Embodiment.Presets;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Gaze.Components;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Newtonsoft.Json;
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
    ///     Contract and behaviour coverage for the Convai Embodiment MCP tools.
    /// </summary>
    /// <remarks>
    ///     The load-bearing tests are the three parity ones —
    ///     <see cref="DiagnoseReportsExactlyWhatTheRigServiceReports" />,
    ///     <see cref="DiagnoseReportsExactlyWhatThePresetTroubleshooterReports" /> and
    ///     <see cref="DiagnoseReportsExactlyWhatEachFeatureSurveyorReports" />. They are what keep the
    ///     assistant-facing surface a projection of the existing setup services rather than a second
    ///     opinion about the same character; adding a check to the MCP layer alone fails them.
    ///     <see cref="ToolsNeverCreateOrModifyAnAsset" /> keeps the "never authors a preset" promise
    ///     from decaying into a comment.
    /// </remarks>
    public sealed class ConvaiEmbodimentMcpToolTests
    {
        private static readonly string _tempFolder =
            $"{TestAssetSandbox.Root}/{nameof(ConvaiEmbodimentMcpToolTests)}";

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
                TestAssetSandbox.Clear(nameof(ConvaiEmbodimentMcpToolTests));
                _createdAssets = false;
                AssetDatabase.Refresh();
            }

            _sceneFixture.End();
        }

        // ------------------------------------------------------------------- contract

        [Test]
        public void CatalogCarriesEveryEmbodimentTool()
        {
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.ConfigureEmbodiment"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.DiagnoseEmbodiment"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.InspectEmbodimentPresets"));
        }

        [Test]
        public void SchemasAreClosed()
        {
            JObject configure = JObject.FromObject(ConvaiEmbodimentMcpTools.ConfigureSchema());
            Assert.That(configure.Value<bool>("additionalProperties"), Is.False);
            Assert.That(configure["required"]?.Values<string>(),
                Is.EquivalentTo(new[] { "characterInstanceId" }));

            JObject diagnose = JObject.FromObject(ConvaiEmbodimentMcpTools.DiagnoseSchema());
            Assert.That(diagnose.Value<bool>("additionalProperties"), Is.False);

            JObject presets = JObject.FromObject(ConvaiEmbodimentMcpTools.InspectPresetsSchema());
            Assert.That(presets.Value<bool>("additionalProperties"), Is.False);
        }

        [Test]
        public void EveryCapabilityToolThisLayerRoutesToExistsInTheCatalog()
        {
            foreach (string toolId in ConvaiEmbodimentCapabilityTools.AllRoutedToolIds)
            {
                Assert.That(ConvaiMcpToolCatalog.All, Does.Contain(toolId),
                    $"The capability routing table points at {toolId}, which no longer exists.");
            }
        }

        [Test]
        public void EveryResponseCarriesTheStandardEnvelope()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Envelope");

            foreach (object response in new[]
                     {
                         ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiEmbodimentMcpTools.InspectPresets(new InspectEmbodimentPresetsRequest())
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
            ConvaiCharacter character = CreateHumanoidCharacter("Preview");
            int componentsBefore = character.GetComponents<Component>().Length;

            JObject response = Json(ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Capabilities = new[] { "Gaze", "Emotion" },
                DryRun = true
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(response["data"]?["changes"]?.Values<string>().Count(), Is.GreaterThan(0));

            Assert.That(character.GetComponents<Component>().Length, Is.EqualTo(componentsBefore));
            Assert.That(character.GetComponentInChildren<StandardRigBinding>(true), Is.Null);
            Assert.That(character.GetComponentInChildren<ConvaiGazeController>(true), Is.Null);
            Assert.That(character.GetComponentInChildren<ConvaiEmotionController>(true), Is.Null);
        }

        [Test]
        public void ConfigureAddsTheRigAndTheNamedFeaturesInOneUndoStep()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Apply");

            JObject response = Json(ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Capabilities = new[] { "Gaze", "convai.emotion" },
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.False);
            Assert.That(character.GetComponentInChildren<StandardRigBinding>(true), Is.Not.Null);
            Assert.That(character.GetComponentInChildren<ConvaiGazeController>(true), Is.Not.Null);
            Assert.That(character.GetComponentInChildren<ConvaiEmotionController>(true), Is.Not.Null);

            Undo.PerformUndo();

            Assert.That(character.GetComponentInChildren<StandardRigBinding>(true), Is.Null,
                "Setting a character up must collapse into one undo step.");
            Assert.That(character.GetComponentInChildren<ConvaiGazeController>(true), Is.Null);
            Assert.That(character.GetComponentInChildren<ConvaiEmotionController>(true), Is.Null);
        }

        [Test]
        public void ConfigureLeavesAFeatureThatIsAlreadyThereAlone()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Existing");
            ConvaiGazeController existing = Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            JObject response = Json(ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Capabilities = new[] { "Gaze" },
                SetUpRig = false,
                DryRun = false
            }));

            Assert.That(response["data"]?["capabilities"]?[0]?.Value<string>("action"),
                Is.EqualTo("AlreadyPresent"));
            Assert.That(character.GetComponents<ConvaiGazeController>().Length, Is.EqualTo(1));
            Assert.That(character.GetComponentInChildren<ConvaiGazeController>(true), Is.SameAs(existing));
        }

        [Test]
        public void ConfigureRefusesAnObjectThatIsNotAConvaiCharacterAndSaysWhatToDo()
        {
            var host = new GameObject("PlainObject");
            Undo.RegisterCreatedObjectUndo(host, "test");
            SceneManager.MoveGameObjectToScene(host, TestScene);
            int componentsBefore = host.GetComponents<Component>().Length;

            JObject response = Json(ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(host),
                Capabilities = new[] { "Gaze" },
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("NOT_A_CONVAI_CHARACTER"));
            Assert.That(response["data"]?.Value<string>("advice"), Does.Contain("Add Component"));
            Assert.That(host.GetComponents<Component>().Length, Is.EqualTo(componentsBefore));
        }

        [Test]
        public void ConfigureRefusesAFeatureNameItDoesNotKnowAndListsTheRealOnes()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Unknown");

            JObject response = Json(ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Capabilities = new[] { "Telepathy" },
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("UNKNOWN_CAPABILITY"));

            string[] offered = response["data"]?["validCapabilities"]?
                .Select(entry => entry.Value<string>("name")).ToArray() ?? Array.Empty<string>();
            Assert.That(offered, Does.Contain("Gaze"));
            Assert.That(offered, Does.Contain("Body Animation"));
            Assert.That(character.GetComponentInChildren<StandardRigBinding>(true), Is.Null);
        }

        [Test]
        public void ConfigureRefusesAPresetPathThatIsNotAPresetAndNamesTheMenuPath()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("BadPreset");

            JObject response = Json(ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                PresetAssetPath = "Assets/DoesNotExist.asset",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("INVALID_PRESET"));
            Assert.That(response["data"]?.Value<string>("createPresetMenuPath"),
                Does.Contain("Assets → Create"));
            Assert.That(character.GetComponentInChildren<ConvaiEmbodimentPresetBinding>(true), Is.Null);
        }

        [Test]
        public void ConfigureAssignsAnExistingPresetWithoutTouchingTheAsset()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("WithPreset");
            ConvaiEmbodimentPreset preset = CreatePresetAsset("Shared");
            string path = AssetDatabase.GetAssetPath(preset);

            JObject response = Json(ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                PresetAssetPath = path,
                SetUpRig = false,
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.True);

            var binding = character.GetComponentInChildren<ConvaiEmbodimentPresetBinding>(true);
            Assert.That(binding, Is.Not.Null);
            Assert.That(binding.Preset, Is.SameAs(preset));
            Assert.That(EditorUtility.IsDirty(preset), Is.False,
                "Assigning a preset must not modify the asset.");
        }

        [Test]
        public void ToolsNeverCreateOrModifyAnAsset()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("NoAuthoring");
            string[] before = AllEmbodimentAssetPaths();

            ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Capabilities = new[] { "Gaze", "Emotion", "Body Animation", "Body Language" },
                DryRun = false
            });
            ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            });
            ConvaiEmbodimentMcpTools.InspectPresets(new InspectEmbodimentPresetsRequest());

            Assert.That(AllEmbodimentAssetPaths(), Is.EquivalentTo(before),
                "An Embodiment tool created or removed a project asset. None of them may author one.");
        }

        // ------------------------------------------------------------------- diagnose

        [Test]
        public void DiagnoseOnABareCharacterReportsFindingsRatherThanThrowing()
        {
            ConvaiCharacter character = CreateCharacter("Bare");

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?["rig"]?["findings"]?.Count(), Is.GreaterThan(0));
            Assert.That(response["data"]?["nextSteps"]?.Count(), Is.GreaterThan(0));
            Assert.That(response["data"]?.Value<string>("readiness"), Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void DiagnoseReportsExactlyWhatTheRigServiceReports()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("RigParity");
            EmbodimentRigSetupService.Apply(character.gameObject);

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            EmbodimentSetupReport expected = EmbodimentRigSetupService.Inspect(character.gameObject);
            JToken[] reported = response["data"]?["rig"]?["findings"]?.ToArray() ?? Array.Empty<JToken>();

            Assert.That(reported.Length, Is.EqualTo(expected.Findings.Count));
            for (int i = 0; i < expected.Findings.Count; i++)
            {
                Assert.That(reported[i].Value<string>("id"), Is.EqualTo(expected.Findings[i].Id));
                Assert.That(reported[i].Value<string>("severity"),
                    Is.EqualTo(expected.Findings[i].Severity.ToString()));
                Assert.That(reported[i].Value<string>("message"), Is.EqualTo(expected.Findings[i].Message));
            }

            Assert.That(response["data"]?["rig"]?.Value<string>("status"),
                Is.EqualTo(expected.HeaderStatus));
        }

        [Test]
        public void DiagnoseReportsExactlyWhatThePresetTroubleshooterReports()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("PresetParity");
            ConvaiEmbodimentPreset preset = CreatePresetAsset("Cross");
            ConvaiEmbodimentPresetBinding binding =
                Undo.AddComponent<ConvaiEmbodimentPresetBinding>(character.gameObject);
            var serialized = new SerializedObject(binding);
            serialized.FindProperty("preset").objectReferenceValue = preset;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            EmbodimentSetupReport expected =
                EmbodimentPresetTroubleshooter.Evaluate(preset, character.gameObject);
            JToken[] reported = response["data"]?["preset"]?["findings"]?.ToArray() ?? Array.Empty<JToken>();

            Assert.That(reported.Length, Is.EqualTo(expected.Findings.Count));
            for (int i = 0; i < expected.Findings.Count; i++)
                Assert.That(reported[i].Value<string>("id"), Is.EqualTo(expected.Findings[i].Id));

            Assert.That(response["data"]?["preset"]?.Value<string>("status"),
                Is.EqualTo(expected.HeaderStatus));
        }

        [Test]
        public void DiagnoseReportsExactlyWhatEachFeatureSurveyorReports()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("FeatureParity");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            Undo.AddComponent<ConvaiEmotionController>(character.gameObject);

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            JToken[] reported = response["data"]?["capabilities"]?.ToArray() ?? Array.Empty<JToken>();

            // The catalog decides which features are this character's embodiment; a surveyor only
            // decides how one of them is doing. Walking the survey registry instead would demand
            // that every surveyor in the editor be an embodiment capability, which stopped being
            // true when Actions — a Runtime capability, not an embodiment module — began reporting
            // through the same registry for the scene-wide tools.
            int checkedModules = 0;

            foreach (EmbodimentModuleDescriptor descriptor in EmbodimentModuleCatalog.Modules)
            {
                if (descriptor.ModuleId == "convai.embodiment") continue;

                IConvaiModuleSurveyor surveyor = ConvaiModuleSurveyRegistry.All
                    .SingleOrDefault(candidate => candidate.ModuleId == descriptor.ModuleId);
                if (surveyor == null) continue;

                ConvaiModuleSurveyResult expected = surveyor.Survey(character.gameObject);
                JToken actual = reported.SingleOrDefault(
                    entry => entry.Value<string>("moduleId") == expected.ModuleId);

                Assert.That(actual, Is.Not.Null, $"{expected.ModuleId} is missing from capabilities.");
                Assert.That(actual.Value<string>("readiness"), Is.EqualTo(expected.Readiness.ToString()));
                Assert.That(actual.Value<string>("summary"), Is.EqualTo(expected.Summary));
                Assert.That(actual.Value<string>("blocker"), Is.EqualTo(expected.Blocker));
                Assert.That(actual["findings"]?.Count(), Is.EqualTo(expected.Findings.Count));
                checkedModules++;
            }

            Assert.That(checkedModules, Is.GreaterThan(0),
                "No catalog feature had a surveyor, so this test proved nothing about parity.");
        }

        [Test]
        public void DiagnoseDoesNotListTheEmbodimentLayerAsOneOfTheCharactersFeatures()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("NoSelf");

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            string[] moduleIds = response["data"]?["capabilities"]?
                .Select(entry => entry.Value<string>("moduleId")).ToArray() ?? Array.Empty<string>();

            Assert.That(moduleIds, Does.Not.Contain("convai.embodiment"));
            Assert.That(moduleIds, Does.Contain("convai.gaze"));
        }

        [Test]
        public void ReadinessSeparatesNotInstalledFromBlockedFromInert()
        {
            ConvaiCharacter humanoid = CreateHumanoidCharacter("Ready");

            Assert.That(ReadinessOf(humanoid, "convai.gaze"), Is.EqualTo("NotInstalled"),
                "A feature that is not on the character must read NotInstalled.");

            Undo.AddComponent<ConvaiEmotionController>(humanoid.gameObject);
            string emotion = ReadinessOf(humanoid, "convai.emotion");
            Assert.That(new[] { "Working", "Blocked", "Inert" }, Does.Contain(emotion));

            // A character with no face at all cannot show expression, whatever else is right.
            Assert.That(emotion, Is.Not.EqualTo("NotInstalled"));

            // A rig that cannot drive a feature is Blocked, not Inert and not NotInstalled: the
            // three mean different things and lead to different next steps.
            ConvaiCharacter rigless = CreateCharacter("NoRig");
            Undo.AddComponent<Convai.Modules.BodyLanguage.Components.ConvaiBodyLanguageController>(
                rigless.gameObject);

            Assert.That(ReadinessOf(rigless, "convai.body-language"), Is.EqualTo("Blocked"),
                "Body Language on a character with no Humanoid rig must read Blocked.");
        }

        [Test]
        public void TheCharacterReadsBlockedWhenAnyFeatureOnItIsBlocked()
        {
            // Body Language cannot run without a Humanoid spine, so this character has one healthy
            // feature and one that cannot work.
            ConvaiCharacter character = CreateCharacter("WorstNotBest");
            Undo.AddComponent<Convai.Modules.BodyLanguage.Components.ConvaiBodyLanguageController>(
                character.gameObject);
            Undo.AddComponent<Convai.Modules.ConversationFlow.Components.ConvaiConversationFlowController>(
                character.gameObject);

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response["data"]?.Value<string>("readiness"), Is.EqualTo("Blocked"),
                "A character with a blocked feature must not read Working just because another " +
                "feature is fine — that buries the one thing the user has to act on.");

            Assert.That(
                response["data"]?["issues"]?.Any(issue => issue.Value<string>("severity") == "Error"),
                Is.True);
        }

        [Test]
        public void AWorkingFeatureWithAWarningStillReachesTheIssueList()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("WorkingWithWarning");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            JToken gaze = response["data"]?["capabilities"]?
                .SingleOrDefault(entry => entry.Value<string>("moduleId") == "convai.gaze");
            Assert.That(gaze, Is.Not.Null);

            string[] warnings = gaze["findings"]?
                .Where(finding => finding.Value<string>("severity") is "Warning" or "Error")
                .Select(finding => finding.Value<string>("message")).ToArray() ?? Array.Empty<string>();

            string[] issues = response["data"]?["issues"]?
                .Select(issue => issue.Value<string>("message")).ToArray() ?? Array.Empty<string>();

            foreach (string warning in warnings)
            {
                Assert.That(issues, Does.Contain(warning),
                    "A feature that works can still have something worth acting on. Dropping those " +
                    "because its readiness word is Working makes a real warning invisible.");
            }
        }

        [Test]
        public void EveryPresentButNotWorkingFeatureCarriesABlockerSentence()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Blockers");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            Undo.AddComponent<ConvaiEmotionController>(character.gameObject);

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            foreach (JToken capability in response["data"]?["capabilities"] ?? new JArray())
            {
                string readiness = capability.Value<string>("readiness");
                if (readiness is not ("Blocked" or "Inert")) continue;

                Assert.That(capability.Value<string>("blocker"), Is.Not.Null.And.Not.Empty,
                    $"{capability.Value<string>("name")} is {readiness} and says nothing about why. " +
                    "A beginner cannot act on that.");
            }
        }

        [Test]
        public void DiagnoseCanOmitFeatureFindingsButNeverTheirReadiness()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Terse");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                IncludeCapabilities = false
            }));

            foreach (JToken capability in response["data"]?["capabilities"] ?? new JArray())
            {
                Assert.That(capability.Value<string>("readiness"), Is.Not.Null.And.Not.Empty);
                Assert.That(capability.Value<string>("summary"), Is.Not.Null.And.Not.Empty);
                Assert.That(capability["findings"]?.Type, Is.EqualTo(JTokenType.Null));
            }
        }

        [Test]
        public void EveryFeatureNamesItsOwnMenuPathAndTools()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Pointers");

            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            foreach (JToken capability in response["data"]?["capabilities"] ?? new JArray())
            {
                Assert.That(capability.Value<string>("addComponentMenuPath"),
                    Does.StartWith("Add Component → Convai → Embodiment → "));
                Assert.That(capability.Value<string>("createSettingsMenuPath"),
                    Does.StartWith("Assets → Create → Convai → Embodiment → "),
                    $"{capability.Value<string>("name")} does not name where its settings asset " +
                    "comes from. That path is derived from the profile type's CreateAssetMenu.");
            }
        }

        // -------------------------------------------------------------------- presets

        [Test]
        public void InspectPresetsFindsAPresetAndReportsTheTroubleshootersVerdict()
        {
            ConvaiEmbodimentPreset preset = CreatePresetAsset("Listed");
            string path = AssetDatabase.GetAssetPath(preset);

            JObject response = Json(ConvaiEmbodimentMcpTools.InspectPresets(
                new InspectEmbodimentPresetsRequest { FolderPaths = new[] { _tempFolder } }));

            JToken listed = response["data"]?["presets"]?
                .SingleOrDefault(entry => entry.Value<string>("assetPath") == path);

            Assert.That(listed, Is.Not.Null);
            Assert.That(listed.Value<string>("status"),
                Is.EqualTo(EmbodimentPresetTroubleshooter.Evaluate(preset).HeaderStatus));
            Assert.That(EditorUtility.IsDirty(preset), Is.False, "Inspecting a preset must not dirty it.");
        }

        [Test]
        public void InspectPresetsListsEveryFeatureAPresetCanCarry()
        {
            JObject response = Json(ConvaiEmbodimentMcpTools.InspectPresets(
                new InspectEmbodimentPresetsRequest()));

            string[] names = response["data"]?["capabilities"]?
                .Select(entry => entry.Value<string>("name")).ToArray() ?? Array.Empty<string>();

            Assert.That(names, Is.EquivalentTo(EmbodimentModuleCatalog.DisplayNamesInDisplayOrder()));
        }

        // ---------------------------------------------------------------- integration

        [Test]
        public void SceneWideToolsSeeTheEmbodimentLayerThroughTheSurveySeam()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("SceneWide");

            JObject inspect = Json(ConvaiMcpTools.InspectScene(new ConvaiSceneInspectionRequest()));
            JToken described = inspect["data"]?["characters"]?
                .SingleOrDefault(entry => entry.Value<string>("name") == character.gameObject.name);

            Assert.That(described, Is.Not.Null);

            JToken embodiment = described["modules"]?
                .SingleOrDefault(module => module.Value<string>("moduleId") == "convai.embodiment");

            Assert.That(embodiment, Is.Not.Null,
                "InspectScene must see the Embodiment layer through the survey registry.");
            Assert.That(embodiment.Value<string>("readiness"), Is.Not.Null.And.Not.Empty);
            Assert.That(embodiment.Value<string>("name"), Is.EqualTo("Embodiment"));

            foreach (JToken module in described["modules"] ?? new JArray())
                Assert.That(module.Value<string>("readiness"), Is.Not.Null.And.Not.Empty,
                    $"{module.Value<string>("name")} reports no readiness word.");
        }

        [Test]
        public void GuidanceForTheLayerNamesAllThreeToolsAndItsDocumentation()
        {
            JObject response = Json(ConvaiMcpTools.GetGuidance(new ConvaiGuidanceRequest
            {
                Topic = ConvaiGuidanceTopic.Embodiment
            }));

            string[] tools = response["data"]?["ConvaiTools"]?.Values<string>().ToArray()
                             ?? Array.Empty<string>();
            Assert.That(tools, Does.Contain("Convai.DiagnoseEmbodiment"));
            Assert.That(tools, Does.Contain("Convai.ConfigureEmbodiment"));
            Assert.That(tools, Does.Contain("Convai.InspectEmbodimentPresets"));

            Assert.That(string.Join(" ", response["data"]?["Documentation"]?.Values<string>()
                                         ?? Array.Empty<string>()),
                Does.Contain("EMBODIMENT.md"));

            string workflow = string.Join(" ", response["data"]?["Workflow"]?.Values<string>()
                                               ?? Array.Empty<string>());
            Assert.That(workflow, Does.Contain("Inert"));
            Assert.That(workflow, Does.Contain("Blocked"));
        }

        [Test]
        public void ResponsesLeakNoSecretsOrPathsOutsideTheProject()
        {
            ConvaiCharacter character = CreateHumanoidCharacter("Sanitized");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            foreach (object response in new[]
                     {
                         ConvaiEmbodimentMcpTools.Configure(new ConfigureEmbodimentRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
                             { CharacterInstanceId = Id(character.gameObject) }),
                         ConvaiEmbodimentMcpTools.InspectPresets(new InspectEmbodimentPresetsRequest())
                     })
            {
                string serialized = JsonConvert.SerializeObject(response, Formatting.None);
                Assert.That(serialized, Does.Not.Contain("apiKey").IgnoreCase);
                // Fully qualified: the SDK has its own Convai.Application namespace, and an
                // unqualified Application here binds to that instead of Unity's.
                Assert.That(serialized, Does.Not.Contain(UnityEngine.Application.dataPath),
                    "A response carried an absolute filesystem path. Asset paths must be project-relative.");
            }
        }

        // --------------------------------------------------------------------- helpers

        private string ReadinessOf(ConvaiCharacter character, string moduleId)
        {
            JObject response = Json(ConvaiEmbodimentMcpTools.Diagnose(new DiagnoseEmbodimentRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            return response["data"]?["capabilities"]?
                .SingleOrDefault(entry => entry.Value<string>("moduleId") == moduleId)
                ?.Value<string>("readiness");
        }

        private ConvaiCharacter CreateCharacter(string name)
        {
            var host = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(host, "test");
            SceneManager.MoveGameObjectToScene(host, TestScene);
            return Undo.AddComponent<ConvaiCharacter>(host);
        }

        private ConvaiCharacter CreateHumanoidCharacter(string name)
        {
            ConvaiCharacter character = CreateCharacter(name);
            Avatar avatar = HumanoidRigFixture.BuildAvatar(character.gameObject, TestScene);
            Assert.That(avatar, Is.Not.Null, "This editor refused to build the test Humanoid avatar.");

            Animator animator = Undo.AddComponent<Animator>(character.gameObject);
            animator.avatar = avatar;
            return character;
        }

        private ConvaiEmbodimentPreset CreatePresetAsset(string name)
        {
            TestAssetSandbox.Ensure(nameof(ConvaiEmbodimentMcpToolTests));

            var preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            string path = $"{_tempFolder}/{name}.asset";
            AssetDatabase.CreateAsset(preset, path);
            AssetDatabase.SaveAssets();
            _createdAssets = true;
            return AssetDatabase.LoadAssetAtPath<ConvaiEmbodimentPreset>(path);
        }

        /// <summary>
        ///     Every embodiment asset in the project, so a test can prove a tool call created none.
        /// </summary>
        private static string[] AllEmbodimentAssetPaths()
        {
            var paths = new List<string>(64);
            var types = new List<string> { "ConvaiEmbodimentPreset", "ConvaiEmbodimentPresetLibrary" };

            IReadOnlyList<EmbodimentModuleDescriptor> modules = EmbodimentModuleCatalog.Modules;
            for (int i = 0; i < modules.Count; i++)
                if (modules[i].ProfileType != null) types.Add(modules[i].ProfileType.Name);

            for (int i = 0; i < types.Count; i++)
            {
                string[] guids = AssetDatabase.FindAssets("t:" + types[i]);
                for (int g = 0; g < guids.Length; g++) paths.Add(AssetDatabase.GUIDToAssetPath(guids[g]));
            }

            paths.Sort(StringComparer.Ordinal);
            return paths.ToArray();
        }

        private static long Id(GameObject value) => ConvaiMcpEntityRef.ToToolId(value);

        private static JObject Json(object response) => JObject.FromObject(response);
    }
}
