using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.Embodiment.Semantics;
using Convai.Editor.AI;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using Convai.Modules.BodyAnimation.Editor.AI;
using Convai.Runtime.Components;
using Newtonsoft.Json.Linq;
using Convai.Editor.Ownership;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.AI
{
    /// <summary>
    ///     Contract and behaviour coverage for the Convai Body Animation MCP tools.
    /// </summary>
    /// <remarks>
    ///     Two tests carry this file. <see cref="DiagnoseReportsExactlyWhatTheSetupServiceReports" />
    ///     keeps the assistant-facing surface a projection of <c>BodyAnimationSetupService</c>
    ///     rather than a second opinion about the same character. The
    ///     <c>Readiness…</c> and <c>Feature…</c> tests prove the distinction this module's
    ///     diagnostic exists for: "not set up" and "set up, but this character has no clips for it"
    ///     must be separable by a caller that cannot see the scene.
    /// </remarks>
    public sealed class ConvaiBodyAnimationMcpToolTests
    {
        private static readonly string _toolFolder =
            $"{TestAssetSandbox.Root}/{nameof(ConvaiBodyAnimationMcpToolTests)}";

        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;
        private const string PackageTestRoot = "Packages/com.convai.convai-sdk-for-unity/Tests/EditMode/AI";
        private const string PackageToolFolderName = "ConvaiBodyAnimationOwnershipFixture";
        private const string PackageToolFolder = PackageTestRoot + "/" + PackageToolFolderName;

        private readonly List<string> _createdPackageAssetPaths = new();

        private readonly List<string> _createdAssetPaths = new();

        /// <summary>
        ///     Where copy-on-write puts a character's own settings when it has no prefab to sit
        ///     beside. Not a folder this fixture owns — a user's own Body Animation assets live here
        ///     — so what the tool writes is recorded by path and removed file by file, and the folder
        ///     itself only goes if this run is what created it.
        /// </summary>
        private const string ModuleAssetFolder = "Assets/Convai/BodyAnimation";

        /// <summary>Assets the tool wrote outside <see cref="_toolFolder" />, deleted one by one.</summary>
        private readonly List<string> _createdModuleAssetPaths = new();

        /// <summary>
        ///     What was in <see cref="ModuleAssetFolder" /> before the test ran. Nothing in here is
        ///     ever deleted, whatever a character ends up pointing at.
        /// </summary>
        private readonly HashSet<string> _preexistingModuleAssets = new();

        private bool _moduleFolderExistedBefore;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
            TestAssetSandbox.Ensure(nameof(ConvaiBodyAnimationMcpToolTests));

            _moduleFolderExistedBefore = AssetDatabase.IsValidFolder(ModuleAssetFolder);
            _preexistingModuleAssets.Clear();
            if (_moduleFolderExistedBefore)
                foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { ModuleAssetFolder }))
                    _preexistingModuleAssets.Add(AssetDatabase.GUIDToAssetPath(guid));
        }

        [TearDown]
        public void TearDown()
        {
            _sceneFixture.End();

            // Everything this fixture wrote lives under one folder it owns, so cleanup can never
            // reach a shipped asset even if a test failed halfway through.
            _createdAssetPaths.Clear();
            if (AssetDatabase.IsValidFolder(_toolFolder))
            {
                TestAssetSandbox.Clear(nameof(ConvaiBodyAnimationMcpToolTests));
                AssetDatabase.Refresh();
            }

            // The package-side fixture is deleted whether or not a test reached its own cleanup,
            // because anything left here ships.
            _createdPackageAssetPaths.Clear();
            if (AssetDatabase.IsValidFolder(PackageToolFolder))
            {
                AssetDatabase.DeleteAsset(PackageToolFolder);
                AssetDatabase.Refresh();
            }

            CleanUpModuleAssets();
        }

        /// <summary>
        ///     Removes the settings assets the tool wrote on a test character's behalf.
        /// </summary>
        /// <remarks>
        ///     Applying a configuration or consenting to a copy gives the character its own profile
        ///     and config under <see cref="ModuleAssetFolder" />, which is outside every folder this
        ///     fixture owns. Left alone they accumulate a new numbered copy per run
        ///     ("Tuned_BodyAnimationConfig 1", "… 2"), which is exactly the untracked residue that
        ///     makes a later test count look like it moved. Deleting the folder instead would be a
        ///     fixture reaching into a folder a user's own assets live in, so only the recorded
        ///     paths go, and the folder only if this run created it and nothing else moved in.
        /// </remarks>
        private void CleanUpModuleAssets()
        {
            bool deleted = false;
            foreach (string path in _createdModuleAssetPaths)
                deleted |= AssetDatabase.DeleteAsset(path);
            _createdModuleAssetPaths.Clear();
            _preexistingModuleAssets.Clear();

            if (!_moduleFolderExistedBefore &&
                AssetDatabase.IsValidFolder(ModuleAssetFolder) &&
                AssetDatabase.FindAssets(string.Empty, new[] { ModuleAssetFolder }).Length == 0)
            {
                deleted |= AssetDatabase.DeleteAsset(ModuleAssetFolder);
            }

            if (deleted) AssetDatabase.Refresh();
        }

        // ------------------------------------------------------------------ contract

        [Test]
        public void CatalogCarriesEveryBodyAnimationTool()
        {
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.ConfigureBodyAnimation"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.DiagnoseBodyAnimation"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.InspectBodyAnimationContent"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.TuneBodyAnimationPersonality"));
        }

        [Test]
        public void SchemasAreClosedAndOfferEveryRuntimeChoice()
        {
            JObject configure = Json(ConvaiBodyAnimationMcpTools.ConfigureSchema());
            JObject diagnose = Json(ConvaiBodyAnimationMcpTools.DiagnoseSchema());
            JObject content = Json(ConvaiBodyAnimationMcpTools.InspectContentSchema());
            JObject tune = Json(ConvaiBodyAnimationMcpTools.TuneSchema());

            foreach (JObject schema in new[] { configure, diagnose, content, tune })
                Assert.That(schema.Value<bool>("additionalProperties"), Is.False,
                    "An open schema lets an assistant pass a field the tool silently ignores.");

            Assert.That(
                configure["properties"]?["speedProfile"]?["enum"]?.Values<string>(),
                Is.EquivalentTo(Enum.GetNames(typeof(LocomotionSpeedProfile))),
                "'speedProfile' drifted from LocomotionSpeedProfile.");

            Assert.That(
                tune["properties"]?["archetype"]?["enum"]?.Values<string>(),
                Is.EquivalentTo(BodyAnimationPersonality.Archetypes.Select(a => a.Demeanor.ToString())),
                "'archetype' drifted from the documented archetype list.");
        }

        [Test]
        public void TuningFieldsDeclareNoDefaultSoOmittingOneLeavesItAlone()
        {
            JObject configure = Json(ConvaiBodyAnimationMcpTools.ConfigureSchema());
            JObject tune = Json(ConvaiBodyAnimationMcpTools.TuneSchema());

            string[] configureTuning =
            {
                "speedProfile", "autoJogDistanceMeters", "minJogDistanceMeters",
                "accelerationMetersPerSecondSquared", "rotationDegreesPerSecond"
            };
            string[] tuneTuning = { "archetype", "howExpressive", "howCalm", "keepsBusyWhenAlone", "howOftenSeconds" };

            foreach (string field in configureTuning)
                Assert.That(configure["properties"]?[field]?["default"], Is.Null,
                    $"'{field}' declares a default, so omitting it reads as a request to reset it.");

            foreach (string field in tuneTuning)
                Assert.That(tune["properties"]?[field]?["default"], Is.Null,
                    $"'{field}' declares a default, so omitting it reads as a request to reset it.");
        }

        [Test]
        public void EveryResponseCarriesTheDeclaredEnvelope()
        {
            ConvaiCharacter character = CreateCharacter("Envelope", humanoid: false);

            foreach (JObject response in new[]
                     {
                         Json(Configure(new ConfigureBodyAnimationRequest
                         {
                             CharacterInstanceId = Id(character.gameObject)
                         })),
                         Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
                         {
                             CharacterInstanceId = Id(character.gameObject)
                         })),
                         Json(ConvaiBodyAnimationMcpTools.InspectContent(new InspectBodyAnimationContentRequest
                         {
                             CharacterInstanceId = Id(character.gameObject)
                         })),
                         Json(Tune(new TuneBodyAnimationPersonalityRequest
                         {
                             CharacterInstanceId = Id(character.gameObject)
                         }))
                     })
            {
                Assert.That(response["success"], Is.Not.Null);
                Assert.That(response["message"], Is.Not.Null);
                Assert.That(response["data"], Is.Not.Null);
            }
        }

        // ------------------------------------------------------------------ configure

        [Test]
        public void ConfigurePreviewChangesNothing()
        {
            ConvaiCharacter character = CreateCharacter("Preview");

            JObject response = Json(Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(character.GetComponentInChildren<ConvaiBodyAnimationController>(true), Is.Null,
                "A preview added the component.");
            Assert.That(character.GetComponentInChildren<ConvaiNavMeshLocomotion>(true), Is.Null,
                "A preview added movement.");
            Assert.That(response["data"]?["changes"]?.Values<string>(), Is.Not.Empty,
                "A preview that plans nothing on a bare character is not describing the work.");
        }

        [Test]
        public void ConfigureApplyAddsTheComponentAndIsUndoneInOneStep()
        {
            ConvaiCharacter character = CreateCharacter("Applied");

            Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                IncludeMovement = false,
                DryRun = false
            });

            Assert.That(character.GetComponentInChildren<ConvaiBodyAnimationController>(true), Is.Not.Null,
                "Apply did not add the Body Animation component.");

            Undo.PerformUndo();

            Assert.That(character.GetComponentInChildren<ConvaiBodyAnimationController>(true), Is.Null,
                "The whole configuration was not collapsed into one undo step.");
        }

        [Test]
        public void ConfigureAddsMovementOnlyWhenAsked()
        {
            ConvaiCharacter without = CreateCharacter("NoMovement");
            Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(without.gameObject), IncludeMovement = false, DryRun = false
            });

            ConvaiCharacter with = CreateCharacter("WithMovement");
            Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(with.gameObject), IncludeMovement = true, DryRun = false
            });

            Assert.That(without.GetComponentInChildren<ConvaiNavMeshLocomotion>(true), Is.Null,
                "Movement was added to a character that asked not to move.");
            Assert.That(with.GetComponentInChildren<ConvaiNavMeshLocomotion>(true), Is.Not.Null);
        }

        [Test]
        public void ConfigureWritesOnlyTheMovementSettingsTheRequestNames()
        {
            ConvaiCharacter character = CreateCharacter("Selective");
            Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject), DryRun = false
            });

            ConvaiNavMeshLocomotion locomotion =
                character.GetComponentInChildren<ConvaiNavMeshLocomotion>(true);
            var before = new SerializedObject(locomotion);
            float untouchedAcceleration = before.FindProperty("_acceleration").floatValue;

            Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                MinJogDistanceMeters = 9f,
                DryRun = false
            });

            var after = new SerializedObject(locomotion);
            Assert.That(after.FindProperty("_minJogDistance").floatValue, Is.EqualTo(9f).Within(0.001f));
            Assert.That(after.FindProperty("_acceleration").floatValue,
                Is.EqualTo(untouchedAcceleration).Within(0.001f),
                "A setting the request never named was overwritten.");
        }

        [Test]
        public void ConfigureOnARigThatCannotHostBodyAnimationReportsAndWritesNothing()
        {
            ConvaiCharacter character = CreateCharacter("NoRig", humanoid: false);

            JObject response = Json(Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                DryRun = false
            }));

            Assert.That(response["data"]?["blockers"]?.Any(), Is.True,
                "A character with no Humanoid rig was not told why it cannot host the module.");
            Assert.That(character.GetComponentInChildren<ConvaiBodyAnimationController>(true), Is.Null,
                "A component was added to a character that cannot use it.");
            Assert.That(response["data"]?["nextSteps"]?.Values<string>().Any(step => step.Length > 0), Is.True);
        }

        [Test]
        public void ConfigureRejectsAnAssetPathThatDoesNotExistAndTouchesNothing()
        {
            ConvaiCharacter character = CreateCharacter("BadPath");

            JObject response = Json(Configure(new ConfigureBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                ProfileAssetPath = "Assets/DoesNotExist.asset",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("INVALID_ASSET"));
            Assert.That(response.Value<string>("message"), Does.Contain("never creates one"));
            Assert.That(character.GetComponentInChildren<ConvaiBodyAnimationController>(true), Is.Null);
        }

        [Test]
        public void ConfigureWithNoCharacterInTheSceneReportsRatherThanThrows()
        {
            JObject response = Json(Configure(new ConfigureBodyAnimationRequest()));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"),
                Is.EqualTo(ConvaiMcpResolvers.CharacterErrorCode));
        }

        // ------------------------------------------------------------------ diagnose: readiness

        /// <summary>
        ///     The distinction this module's diagnostic exists for. "Not set up" has three causes
        ///     with three different next steps, and a caller that cannot see the scene must be able
        ///     to tell them apart from a working character.
        /// </summary>
        [Test]
        public void ReadinessSeparatesNotInstalledFromBlockedFromNeedsContent()
        {
            ConvaiCharacter bare = CreateCharacter("Bare");
            Assert.That(ReadinessOf(bare), Is.EqualTo(nameof(BodyAnimationReadiness.NotInstalled)));

            ConvaiCharacter blocked = CreateCharacter("Blocked", humanoid: false);
            Undo.AddComponent<ConvaiBodyAnimationController>(blocked.gameObject);
            Assert.That(ReadinessOf(blocked), Is.EqualTo(nameof(BodyAnimationReadiness.Blocked)));

            ConvaiCharacter empty = CreateCharacter("NoContent");
            Undo.AddComponent<ConvaiBodyAnimationController>(empty.gameObject);
            Assert.That(ReadinessOf(empty), Is.EqualTo(nameof(BodyAnimationReadiness.NeedsContent)),
                "A rigged character with no animation set must read as needing content, not as broken.");

            ConvaiCharacter working = CreateCharacter("Working");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(working.gameObject);
            AssignSet(controller, CreateSet("Working", withTalk: true));
            Assert.That(ReadinessOf(working), Is.EqualTo(nameof(BodyAnimationReadiness.Working)));
        }

        [Test]
        public void DiagnoseOnACharacterWithoutBodyAnimationSaysWhatToDoNext()
        {
            ConvaiCharacter character = CreateCharacter("Missing");

            JObject response = Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response["data"]?.Value<bool>("present"), Is.False);
            string[] steps = response["data"]?["nextSteps"]?.Values<string>().ToArray();
            Assert.That(steps, Is.Not.Null.And.Not.Empty);
            Assert.That(steps[0], Does.Contain("Add Component"),
                "A beginner is not told the gesture that adds the component.");
        }

        [Test]
        public void DiagnoseOnABareGameObjectReportsRatherThanThrows()
        {
            JObject response = Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest()));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"),
                Is.EqualTo(ConvaiMcpResolvers.CharacterErrorCode));
        }

        // ------------------------------------------------------------------ diagnose: content gating

        /// <summary>
        ///     The other half of the required distinction: a character that is fully set up can
        ///     still have a behaviour that does nothing, and the reason is a missing clip rather
        ///     than a missing step.
        /// </summary>
        [Test]
        public void FeatureStateSeparatesNeedsContentFromContentIdle()
        {
            ConvaiCharacter character = CreateCharacter("Gated");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);

            // Nothing tagged Ambient, and the setting off — the shipped default.
            ConvaiBodyAnimationSet plain = CreateSet("Plain", withTalk: true);
            AssignSet(controller, plain);
            AssignConfig(controller, CreateConfig("Plain", ambientEnabled: false));
            Assert.That(FeatureState(character, "Ambient Activities"),
                Is.EqualTo(nameof(BodyAnimationFeatureStateKind.OffByChoice)));

            // Setting on, still nothing tagged — set up, but no clips for it.
            AssignConfig(controller, CreateConfig("On", ambientEnabled: true));
            Assert.That(FeatureState(character, "Ambient Activities"),
                Is.EqualTo(nameof(BodyAnimationFeatureStateKind.NeedsContent)),
                "A behaviour that is on with nothing authored must read as needing content.");

            // Clips tagged, setting off — the invisible authoring mistake.
            ConvaiBodyAnimationSet tagged = CreateSet("Tagged", withTalk: true, withAmbientAction: true);
            AssignSet(controller, tagged);
            AssignConfig(controller, CreateConfig("Off", ambientEnabled: false));
            Assert.That(FeatureState(character, "Ambient Activities"),
                Is.EqualTo(nameof(BodyAnimationFeatureStateKind.ContentIdle)),
                "Tagged content behind a setting that is off must be reported, not silently ignored.");

            // Both — working.
            AssignConfig(controller, CreateConfig("Both", ambientEnabled: true));
            Assert.That(FeatureState(character, "Ambient Activities"),
                Is.EqualTo(nameof(BodyAnimationFeatureStateKind.Working)));
        }

        [Test]
        public void AnEmptyPoolReadsAsNeedingContentAndNotAsAFailure()
        {
            ConvaiCharacter character = CreateCharacter("Pools");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("Pools", withTalk: true));

            JObject response = Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response.Value<bool>("success"), Is.True,
                "A character with no Listen clips is not a failure.");
            Assert.That(FeatureState(character, "Listening"),
                Is.EqualTo(nameof(BodyAnimationFeatureStateKind.NeedsContent)));
            Assert.That(FeatureState(character, "Talk Gestures"),
                Is.EqualTo(nameof(BodyAnimationFeatureStateKind.Working)));
        }

        [Test]
        public void AMissingContentTierReadsAsAFallbackAndNotAsADefect()
        {
            ConvaiCharacter character = CreateCharacter("Tiers");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("Tiers", withTalk: true));

            Assert.That(FeatureState(character, "Walking while talking"),
                Is.EqualTo(nameof(BodyAnimationFeatureStateKind.FallbackTier)),
                "A talk pool with no Additive Clip is a documented fallback, not a fault.");
        }

        [Test]
        public void RigScaleIsReportedOnlyWhenItIsActuallyCalibrated()
        {
            ConvaiCharacter character = CreateCharacter("Scale");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("Scale", withTalk: true));

            JObject rig = (JObject)Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }))["data"]?["rig"];

            Assert.That(rig, Is.Not.Null);
            Assert.That(rig.Value<bool>("calibrated"), Is.False,
                "An uncalibrated rig must stay quiet rather than adding noise.");
            Assert.That(rig.Value<float>("motionScale"), Is.EqualTo(1f).Within(0.001f));
            Assert.That(rig.Value<string>("message"), Does.Contain("no speed correction"));
        }

        // ------------------------------------------------------------------ diagnose: parity

        /// <summary>
        ///     The load-bearing test: the MCP surface must be a projection of the setup service, not
        ///     a second check engine. If this fails, someone added a condition to the tool layer, and
        ///     an assistant can now describe a character differently from the editor.
        /// </summary>
        [Test]
        public void DiagnoseReportsExactlyWhatTheSetupServiceReports()
        {
            ConvaiCharacter character = CreateCharacter("Parity");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("Parity", withTalk: true));

            JObject response = Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(controller);
            var serialized = new SerializedObject(controller);
            BodyAnimationTroubleshooterInput input = BodyAnimationTroubleshooter.GatherFrom(
                controller,
                serialized.FindProperty("_animationSet"),
                serialized.FindProperty("_config"),
                serialized.FindProperty("profile"),
                serialized.FindProperty("_animatorOverride"),
                serialized.FindProperty("_locomotionProviderOverride"),
                new List<string>(8),
                out _,
                out _);
            var findings = new List<BodyAnimationTroubleshooterFinding>(16);
            BodyAnimationTroubleshooter.Evaluate(in input, findings);

            string[] reportedChecks = response["data"]?["checks"]?
                .Select(check =>
                    $"{check.Value<string>("label")}|{check.Value<string>("detail")}|{check.Value<string>("state")}")
                .ToArray();
            string[] serviceChecks = preflight.Checks
                .Select(check => $"{check.Label}|{check.Detail}|{check.State}")
                .ToArray();
            Assert.That(reportedChecks, Is.EqualTo(serviceChecks),
                "The tool's checks drifted from BodyAnimationSetupService.Inspect.");

            string[] reportedIssues = response["data"]?["issues"]?
                .Select(issue => $"{issue.Value<string>("severity")}|{issue.Value<string>("message")}")
                .ToArray();
            string[] serviceIssues = findings
                .Where(finding => finding.Severity != BodyAnimationTroubleshooterSeverity.Ok)
                .Select(finding => $"{finding.Severity}|{finding.Message}")
                .ToArray();
            Assert.That(reportedIssues, Is.EqualTo(serviceIssues),
                "The tool's issues drifted from BodyAnimationTroubleshooter.Evaluate.");
        }

        [Test]
        public void SuggestedRepairArgumentsAddressTheCharacterNotTheComponent()
        {
            ConvaiCharacter character = CreateCharacter("Suggestions");
            Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);

            JObject response = Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            long characterId = Id(character.gameObject);
            foreach (JToken issue in response["data"]?["issues"] ?? new JArray())
            {
                Assert.That(
                    issue["suggestedArguments"]?.Value<long>("characterInstanceId"),
                    Is.EqualTo(characterId),
                    "A suggested repair addresses the component, so following it returns INVALID_CHARACTER.");
            }
        }

        [Test]
        public void DiagnoseIsReadOnly()
        {
            ConvaiCharacter character = CreateCharacter("ReadOnly");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("ReadOnly", withTalk: true));

            int componentsBefore = character.GetComponents<Component>().Length;
            EditorSceneManager.MarkSceneDirty(TestScene);

            ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            });

            Assert.That(character.GetComponents<Component>().Length, Is.EqualTo(componentsBefore),
                "Diagnose changed the character.");
        }

        [Test]
        public void SceneSurveyAgreesWithDiagnose()
        {
            ConvaiCharacter character = CreateCharacter("Surveyed");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("Surveyed", withTalk: true));

            JObject diagnose = Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));
            JObject inspect = Json(ConvaiMcpTools.InspectScene(new ConvaiSceneInspectionRequest()));

            JToken surveyed = inspect["data"]?["characters"]?
                .First(entry => entry.Value<string>("name") == character.gameObject.name)["modules"]?
                .FirstOrDefault(module => module.Value<string>("moduleId") == "convai.body-animation");

            Assert.That(surveyed, Is.Not.Null, "InspectScene does not report the Body Animation module.");
            Assert.That(surveyed.Value<bool>("present"), Is.True);
            Assert.That(surveyed.Value<bool>("working"),
                Is.EqualTo(diagnose["data"]?["readiness"]?.Value<bool>("isWorking")));
        }

        // ------------------------------------------------------------------ inspect content

        [Test]
        public void InspectContentListsTheActionNamesPlayActionTakes()
        {
            ConvaiCharacter character = CreateCharacter("Content");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("Content", withTalk: true, withAmbientAction: true));

            JObject response = Json(ConvaiBodyAnimationMcpTools.InspectContent(
                new InspectBodyAnimationContentRequest { CharacterInstanceId = Id(character.gameObject) }));

            Assert.That(response["data"]?.Value<bool>("hasContent"), Is.True);
            string[] names = response["data"]?["actions"]?.Select(a => a.Value<string>("name")).ToArray();
            Assert.That(names, Does.Contain("stretch"),
                "The action list is what an assistant reads before writing PlayAction code.");
            Assert.That(response["data"]?["locomotion"]?.Value<int>("totalSlots"),
                Is.EqualTo(BodyAnimationContentCoverage.TotalSlots));
        }

        [Test]
        public void InspectContentWorksFromAnAssetPathWithNoCharacter()
        {
            ConvaiBodyAnimationSet set = CreateSet("Standalone", withTalk: true);

            JObject response = Json(ConvaiBodyAnimationMcpTools.InspectContent(
                new InspectBodyAnimationContentRequest
                {
                    AnimationSetAssetPath = AssetDatabase.GetAssetPath(set)
                }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<string>("resolvedVia"), Is.EqualTo("Animation Set asset path"));
        }

        /// <summary>
        ///     The coverage grid the window draws and the numbers this tool reports come from one
        ///     table. If they can disagree, the extraction was pointless.
        /// </summary>
        [Test]
        public void ReportedCoverageMatchesTheSharedCoverageTable()
        {
            ConvaiBodyAnimationSet set = CreateSet("Coverage", withTalk: true);

            JObject response = Json(ConvaiBodyAnimationMcpTools.InspectContent(
                new InspectBodyAnimationContentRequest
                {
                    AnimationSetAssetPath = AssetDatabase.GetAssetPath(set)
                }));

            SerializedProperty locomotion = BodyAnimationContentCoverage.LocomotionPropertyOf(set);
            var expected = new List<string>();
            foreach (LocomotionCoverageCell cell in BodyAnimationContentCoverage.WalkCells)
                expected.Add($"{cell.ColumnLabel}:{BodyAnimationContentCoverage.CountFilled(locomotion, cell)}");
            foreach (LocomotionCoverageCell cell in BodyAnimationContentCoverage.JogCells)
                expected.Add($"{cell.ColumnLabel}:{BodyAnimationContentCoverage.CountFilled(locomotion, cell)}");

            string[] reported = response["data"]?["locomotion"]?["coverage"]?
                .Select(cell => $"{cell.Value<string>("column")}:{cell.Value<int>("filled")}")
                .ToArray();

            Assert.That(reported, Is.EqualTo(expected.ToArray()),
                "The tool's coverage drifted from BodyAnimationContentCoverage.");
        }

        // ------------------------------------------------------------------ tune personality

        [Test]
        public void TunePreviewWritesNothing()
        {
            ConvaiCharacter character = CreateCharacter("TunePreview");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            ConvaiBodyAnimationConfig config = CreateConfig("TunePreview", ambientEnabled: false);
            AssignSet(controller, CreateSet("TunePreview", withTalk: true));
            AssignConfig(controller, config);

            float before = config.GestureLiveliness;

            JObject response = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                HowExpressive = 1.8f
            }));

            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(config.GestureLiveliness, Is.EqualTo(before).Within(0.001f),
                "A preview wrote to the config asset.");
            Assert.That(response["data"]?["changes"]?.Values<string>(), Is.Not.Empty);
        }

        [Test]
        public void TuneOnASharedConfigRefusesWithoutConsentAndCopiesNothing()
        {
            ConvaiBodyAnimationConfig shared = CreateConfig("Shared", ambientEnabled: false);
            ConvaiBodyAnimationSet set = CreateSet("Shared", withTalk: true);

            ConvaiCharacter first = CreateCharacter("First");
            ConvaiBodyAnimationController firstController =
                Undo.AddComponent<ConvaiBodyAnimationController>(first.gameObject);
            AssignSet(firstController, set);
            AssignConfig(firstController, shared);

            ConvaiCharacter second = CreateCharacter("Second");
            ConvaiBodyAnimationController secondController =
                Undo.AddComponent<ConvaiBodyAnimationController>(second.gameObject);
            AssignSet(secondController, set);
            AssignConfig(secondController, shared);

            float before = shared.GestureLiveliness;
            int assetsBefore = AssetDatabase.FindAssets("t:ConvaiBodyAnimationConfig", new[] { _toolFolder }).Length;

            JObject response = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(first.gameObject),
                HowExpressive = 1.8f,
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("CONFIG_SHARED_CONSENT_REQUIRED"));
            Assert.That(response["data"]?.Value<int>("sharedByCharacterCount"), Is.EqualTo(2));
            Assert.That(response["data"]?["otherCharacters"]?.Values<string>(), Does.Contain("Second"));
            Assert.That(shared.GestureLiveliness, Is.EqualTo(before).Within(0.001f));
            Assert.That(
                AssetDatabase.FindAssets("t:ConvaiBodyAnimationConfig", new[] { _toolFolder }).Length,
                Is.EqualTo(assetsBefore),
                "A refusal still created an asset.");
        }

        [Test]
        public void TuneWithConsentCopiesTheConfigAndLeavesTheOtherCharacterAlone()
        {
            ConvaiBodyAnimationConfig shared = CreateConfig("Consent", ambientEnabled: false);
            ConvaiBodyAnimationSet set = CreateSet("Consent", withTalk: true);

            ConvaiCharacter first = CreateCharacter("Tuned");
            ConvaiBodyAnimationController firstController =
                Undo.AddComponent<ConvaiBodyAnimationController>(first.gameObject);
            AssignSet(firstController, set);
            AssignConfig(firstController, shared);

            ConvaiCharacter second = CreateCharacter("Untouched");
            ConvaiBodyAnimationController secondController =
                Undo.AddComponent<ConvaiBodyAnimationController>(second.gameObject);
            AssignSet(secondController, set);
            AssignConfig(secondController, shared);

            float sharedBefore = shared.GestureLiveliness;

            JObject response = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(first.gameObject),
                HowExpressive = 1.8f,
                MakeConfigUnique = true,
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.True, response.Value<string>("message"));

            ConvaiBodyAnimationConfig tuned = BodyAnimationSetupService.ResolveAssignedConfig(firstController);
            ConvaiBodyAnimationConfig untouched =
                BodyAnimationSetupService.ResolveAssignedConfig(secondController);

            Assert.That(tuned, Is.Not.EqualTo(shared), "The character was not given its own copy.");
            Assert.That(tuned.GestureLiveliness, Is.EqualTo(1.8f).Within(0.001f));
            Assert.That(untouched, Is.EqualTo(shared),
                "The other character was moved off the shared config.");
            Assert.That(shared.GestureLiveliness, Is.EqualTo(sharedBefore).Within(0.001f),
                "The shared config was changed, so every other character using it was retuned.");
        }

        [Test]
        public void TuneOnASingleCharacterConfigWritesInPlaceWithNoCopy()
        {
            ConvaiCharacter character = CreateCharacter("Solo");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            ConvaiBodyAnimationConfig config = CreateConfig("Solo", ambientEnabled: false);
            AssignSet(controller, CreateSet("Solo", withTalk: true));
            AssignConfig(controller, config);

            int assetsBefore = AssetDatabase.FindAssets("t:ConvaiBodyAnimationConfig", new[] { _toolFolder }).Length;

            JObject response = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                HowCalm = 1.6f,
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.True, response.Value<string>("message"));
            Assert.That(config.Calmness, Is.EqualTo(1.6f).Within(0.001f));
            Assert.That(
                AssetDatabase.FindAssets("t:ConvaiBodyAnimationConfig", new[] { _toolFolder }).Length,
                Is.EqualTo(assetsBefore),
                "A config only this character uses was needlessly duplicated.");
        }

        [Test]
        public void TuneAppliesAnArchetypeAsOneDocumentedCombination()
        {
            ConvaiCharacter character = CreateCharacter("Archetype");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            ConvaiBodyAnimationConfig config = CreateConfig("Archetype", ambientEnabled: false);
            AssignSet(controller, CreateSet("Archetype", withTalk: true));
            AssignConfig(controller, config);

            BodyAnimationArchetype energetic = BodyAnimationPersonality.Archetypes
                .Single(a => a.Demeanor == CharacterDemeanor.Energetic);

            Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Archetype = CharacterDemeanor.Energetic,
                DryRun = false
            });

            Assert.That(BodyAnimationPersonality.Matches(config, energetic), Is.True,
                "The archetype did not write the documented values.");
        }

        [Test]
        public void TurningOnAnAmbientSettingWithNoAmbientContentWarnsRatherThanPretending()
        {
            ConvaiCharacter character = CreateCharacter("EmptyAmbient");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            AssignSet(controller, CreateSet("EmptyAmbient", withTalk: true));
            AssignConfig(controller, CreateConfig("EmptyAmbient", ambientEnabled: false));

            JObject response = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                KeepsBusyWhenAlone = true,
                DryRun = false
            }));

            string[] warnings = response["data"]?["warnings"]?.Values<string>().ToArray();
            Assert.That(warnings, Is.Not.Null.And.Not.Empty,
                "Turning on a switch with nothing behind it was silently accepted.");
            Assert.That(warnings[0], Does.Contain("Ambient"));
        }

        /// <summary>
        ///     Found by driving the tool against a real character: the SDK's shipped config is
        ///     resolved by exactly one character in a fresh scene, so a share-count-only guard let
        ///     an assistant edit the default every future character inherits — and the next package
        ///     update would silently overwrite the change.
        /// </summary>
        [Test]
        public void TuneRefusesToEditTheShippedConfigInPlaceEvenWhenOnlyOneCharacterUsesIt()
        {
            ConvaiBodyAnimationProfile shipped = BodyAnimationSetupService.TryLoadDefaultProfile();
            Assert.That(shipped, Is.Not.Null, "The SDK's shipped Body Animation profile was not found.");
            Assert.That(shipped.Config, Is.Not.Null);
            Assert.That(ConvaiAssetOwnership.IsProjectAsset(shipped.Config), Is.False,
                "This test needs a config that ships with the package, outside Assets/.");

            ConvaiCharacter character = CreateCharacter("Shipped");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("profile").objectReferenceValue = shipped;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            float before = shipped.Config.GestureLiveliness;

            JObject preview = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                HowExpressive = 1.7f
            }));
            Assert.That(preview["data"]?["sharing"]?.Value<bool>("willCopyConfig"), Is.True,
                "A preview promised to tune the SDK's shipped config in place.");
            Assert.That(preview["data"]?["sharing"]?.Value<bool>("shipsWithSdk"), Is.True);

            JObject applied = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                HowExpressive = 1.7f,
                DryRun = false
            }));
            Assert.That(applied.Value<bool>("success"), Is.False,
                "The shipped config was edited without consent.");
            Assert.That(applied["data"]?.Value<string>("code"), Is.EqualTo("CONFIG_SHARED_CONSENT_REQUIRED"));
            Assert.That(shipped.Config.GestureLiveliness, Is.EqualTo(before).Within(0.001f),
                "The SDK's shipped config was modified.");
        }

        /// <summary>
        ///     The tool and the inspector must decide "is this config safe to tune in place?" the
        ///     same way. They used to answer differently — the tool counted characters and checked
        ///     the package, the inspector only counted characters — which is how a user could be
        ///     warned by an assistant and not by the editor about the same config.
        /// </summary>
        [Test]
        public void TheToolAndTheInspectorShareOneOwnershipRule()
        {
            ConvaiBodyAnimationProfile shipped = BodyAnimationSetupService.TryLoadDefaultProfile();
            Assert.That(shipped?.Config, Is.Not.Null);

            ConvaiCharacter character = CreateCharacter("Ownership");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("profile").objectReferenceValue = shipped;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ConvaiAssetOwnership ownership = BodyAnimationPersonality.OwnershipOf(shipped.Config);
            Assert.That(ownership.RequiresProjectCopy, Is.True);
            Assert.That(ownership.EditingAffectsOthers, Is.True,
                "A config that ships with the SDK must be copied before tuning, whatever the share count.");
            Assert.That(ownership.IsWritable, Is.False,
                "The personality controls must be unavailable on a config that cannot be written.");
            Assert.That(ownership.NoticeMessage, Is.Not.Empty,
                "An unavailable control without an explanation reads as a broken editor.");

            JObject preview = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                HowExpressive = 1.7f
            }));

            Assert.That(preview["data"]?["sharing"]?.Value<bool>("willCopyConfig"),
                Is.EqualTo(ownership.EditingAffectsOthers),
                "The tool's copy decision drifted from the shared ownership rule.");
            Assert.That(preview["data"]?["sharing"]?.Value<bool>("shipsWithSdk"),
                Is.EqualTo(ownership.RequiresProjectCopy));
        }

        /// <summary>
        ///     A config this character owns outright is tunable in place and says nothing — the
        ///     other half of the rule, so the guard above cannot pass by always warning.
        /// </summary>
        [Test]
        public void APrivateProjectConfigIsTunableInPlaceAndRaisesNoNotice()
        {
            ConvaiBodyAnimationConfig config = CreateConfig("Private", ambientEnabled: false);

            ConvaiAssetOwnership ownership = BodyAnimationPersonality.OwnershipOf(config);

            Assert.That(ownership.RequiresProjectCopy, Is.False);
            Assert.That(ownership.IsWritable, Is.True);
            Assert.That(ownership.EditingAffectsOthers, Is.False);
            Assert.That(ownership.NoticeMessage, Is.Empty);
        }

        [Test]
        public void TuneOnACharacterWithNoConfigAssetSaysSoRatherThanThrowing()
        {
            ConvaiCharacter character = CreateCharacter("NoConfig");
            Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);

            JObject response = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                HowExpressive = 1.5f,
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("NO_CONFIG_ASSET"));
        }

        // ------------------------------------------------------------------ tool calls

        /// <summary>
        ///     Calls <see cref="ConvaiBodyAnimationMcpTools.Configure(ConfigureBodyAnimationRequest)" />
        ///     and records whatever it wrote for the character.
        /// </summary>
        /// <remarks>
        ///     Every call in this file goes through these wrappers rather than through the tool
        ///     directly, so a test added later cannot apply a configuration without its assets being
        ///     tracked.
        /// </remarks>
        private object Configure(ConfigureBodyAnimationRequest request)
        {
            object response = ConvaiBodyAnimationMcpTools.Configure(request);
            TrackModuleAssetsOf(request);
            return response;
        }

        /// <summary>
        ///     Calls <see cref="ConvaiBodyAnimationMcpTools.Tune(TuneBodyAnimationPersonalityRequest)" />
        ///     and records the copies the consented copy-on-write path made.
        /// </summary>
        private object Tune(TuneBodyAnimationPersonalityRequest request)
        {
            object response = ConvaiBodyAnimationMcpTools.Tune(request);

            // The tool names both copies it made, which is the most direct record of what to remove.
            // The character is read as well, because Configure reports no paths at all.
            JToken sharing = JObject.FromObject(response)["data"]?["sharing"];
            TrackModuleAsset(sharing?.Value<string>("configAssetPath"));
            TrackModuleAsset(sharing?.Value<string>("profileAssetPath"));
            TrackModuleAssetsOf(request);
            return response;
        }

        private void TrackModuleAssetsOf(ConfigureBodyAnimationRequest request) =>
            TrackModuleAssetsOf(request?.CharacterInstanceId ?? 0);

        private void TrackModuleAssetsOf(TuneBodyAnimationPersonalityRequest request) =>
            TrackModuleAssetsOf(request?.CharacterInstanceId ?? 0);

        /// <summary>
        ///     Records the profile and config a character now points at, which is how the assets
        ///     <c>Configure</c> creates are found — it reports none of them in its response.
        /// </summary>
        private void TrackModuleAssetsOf(long characterInstanceId)
        {
            if (characterInstanceId == 0) return;
            if (!ConvaiMcpEntityRef.TryResolve(characterInstanceId, out GameObject host)) return;

            ConvaiBodyAnimationController controller =
                host.GetComponentInChildren<ConvaiBodyAnimationController>(true);
            if (controller == null) return;

            var serialized = new SerializedObject(controller);
            var profile =
                serialized.FindProperty("profile").objectReferenceValue as ConvaiBodyAnimationProfile;

            TrackModuleAsset(AssetDatabase.GetAssetPath(profile));
            TrackModuleAsset(AssetDatabase.GetAssetPath(
                serialized.FindProperty("_config").objectReferenceValue));
            if (profile != null) TrackModuleAsset(AssetDatabase.GetAssetPath(profile.Config));
        }

        /// <summary>
        ///     Records one path for deletion, but only inside the module folder and only if it was
        ///     not already there — an asset a user authored is never this fixture's to remove.
        /// </summary>
        private void TrackModuleAsset(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!path.StartsWith(ModuleAssetFolder + "/", StringComparison.Ordinal)) return;
            if (_preexistingModuleAssets.Contains(path)) return;
            if (_createdModuleAssetPaths.Contains(path)) return;

            _createdModuleAssetPaths.Add(path);
        }

        // ------------------------------------------------------------------ helpers

        private ConvaiCharacter CreateCharacter(string name, bool humanoid = true)
        {
            var host = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(host, "test");
            SceneManager.MoveGameObjectToScene(host, TestScene);

            if (humanoid)
            {
                Animator animator = Undo.AddComponent<Animator>(host);
                animator.avatar = HumanoidRigFixture.BuildAvatar(host, TestScene);
                Assert.That(animator.avatar, Is.Not.Null.And.Matches<Avatar>(a => a.isHuman),
                    "The test fixture could not build a Humanoid avatar on this editor.");
            }

            return Undo.AddComponent<ConvaiCharacter>(host);
        }

        private string ReadinessOf(ConvaiCharacter character) =>
            Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }))["data"]?["readiness"]?.Value<string>("state");

        private string FeatureState(ConvaiCharacter character, string featureName)
        {
            JToken feature = Json(ConvaiBodyAnimationMcpTools.Diagnose(new DiagnoseBodyAnimationRequest
                {
                    CharacterInstanceId = Id(character.gameObject)
                }))["data"]?["features"]?
                .FirstOrDefault(entry => entry.Value<string>("name") == featureName);

            Assert.That(feature, Is.Not.Null, $"Diagnose reports no '{featureName}' behaviour.");
            return feature.Value<string>("state");
        }

        private ConvaiBodyAnimationSet CreateSet(
            string name, bool withTalk = false, bool withAmbientAction = false)
        {
            var idle = new IdleEntry();
            idle.Initialize(Clip($"{name}_Idle"));
            var idles = new List<IdleEntry> { idle };

            var talks = new List<TalkEntry>();
            if (withTalk)
            {
                var talk = new TalkEntry();
                talk.Initialize(Clip($"{name}_Talk"));
                talks.Add(talk);
            }

            var actions = new List<ActionEntry>();
            if (withAmbientAction)
            {
                var action = new ActionEntry();
                action.Initialize("stretch", Clip($"{name}_Stretch"), ActionMaskMode.UpperBody);
                action.SetAmbient(true);
                actions.Add(action);
            }

            var set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            set.InitializeContent(name, idles, talks, actions, SaveMask($"{name}_Mask"));
            return SaveAsset(set, $"{name}_Set");
        }

        /// <summary>
        ///     A clip saved as a project asset, so the animation set that references it survives the
        ///     serialization round-trip <see cref="AssetDatabase.CreateAsset" /> performs.
        /// </summary>
        private AnimationClip Clip(string name)
        {
            var clip = new AnimationClip { name = name };
            clip.SetCurve(string.Empty, typeof(Transform), "localPosition.x",
                AnimationCurve.Constant(0f, 1f, 0f));
            return SaveSubAsset(clip, name);
        }

        private AvatarMask SaveMask(string name) => SaveSubAsset(new AvatarMask { name = name }, name);

        private ConvaiBodyAnimationConfig CreateConfig(string name, bool ambientEnabled)
        {
            ConvaiBodyAnimationConfig config = ConvaiBodyAnimationConfig.CreateDefault();
            config.hideFlags = HideFlags.None;

            var serialized = new SerializedObject(config);
            serialized.FindProperty("_enableAmbientActivities").boolValue = ambientEnabled;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            return SaveAsset(config, $"{name}_Config");
        }

        private T SaveAsset<T>(T asset, string name) where T : Object
        {
            EnsureFolder();
            string path = AssetDatabase.GenerateUniqueAssetPath(
                TestAssetSandbox.Path(nameof(ConvaiBodyAnimationMcpToolTests), $"{name}.asset"));
            AssetDatabase.CreateAsset(asset, path);
            _createdAssetPaths.Add(path);
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        /// <summary>
        ///     Clips and masks referenced by a saved set must themselves be assets, or the
        ///     reference serialises as null the moment the set is written.
        /// </summary>
        private T SaveSubAsset<T>(T asset, string name) where T : Object => SaveAsset(asset, name);

        private void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(_toolFolder)) return;
            AssetDatabase.CreateFolder("Assets", "ConvaiBodyAnimationMcpToolTests");
        }

        private static void AssignSet(ConvaiBodyAnimationController controller, ConvaiBodyAnimationSet set)
        {
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("_animationSet").objectReferenceValue = set;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignConfig(
            ConvaiBodyAnimationController controller, ConvaiBodyAnimationConfig config)
        {
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("_config").objectReferenceValue = config;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        ///     Writes a fixture asset inside the package, which is the only way to produce something
        ///     the ownership rule reads as SDK-owned. The folder is removed in TearDown.
        /// </summary>
        private T SavePackageAsset<T>(T asset, string name) where T : Object
        {
            if (!AssetDatabase.IsValidFolder(PackageToolFolder))
                AssetDatabase.CreateFolder(PackageTestRoot, PackageToolFolderName);

            string path = AssetDatabase.GenerateUniqueAssetPath($"{PackageToolFolder}/{name}.asset");
            AssetDatabase.CreateAsset(asset, path);
            _createdPackageAssetPaths.Add(path);
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        private static long Id(GameObject value) => ConvaiMcpEntityRef.ToToolId(value);

        private static JObject Json(object response) => JObject.FromObject(response);

        [Test]
        public void TuneRefusesToEditAPackageOwnedConfigWithoutConsent()
        {
            // CreateInstance, not CreateDefault: the code-defined default is a HideAndDontSave
            // runtime fallback and Unity refuses to persist it, which reads at the call site as an
            // ownership answer rather than as an asset that was never written.
            ConvaiBodyAnimationConfig packageOwned = SavePackageAsset(
                ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>(), "PackageOwnedConfig");

            // Asserted before the ownership call, not after it: every ownership answer keys off this
            // path, so a fixture that never persisted would make the rule look wrong when it is the
            // fixture that is missing.
            string fixturePath = AssetDatabase.GetAssetPath(packageOwned);
            Assert.That(packageOwned, Is.Not.Null, "The package-side fixture asset was not created.");
            Assert.That(fixturePath, Is.Not.Empty,
                "The fixture was never persisted, so it has no path to be judged by.");
            Assert.That(fixturePath, Does.StartWith("Packages/"),
                $"The fixture has to live under Packages/ to be SDK-owned; it is at '{fixturePath}'.");

            Assert.That(ConvaiAssetOwnership.IsProjectAsset(packageOwned), Is.False,
                "A config under Packages/ must not read as a project asset, or this proves nothing.");

            ConvaiCharacter character = CreateCharacter("PackageOwned");
            ConvaiBodyAnimationController controller =
                Undo.AddComponent<ConvaiBodyAnimationController>(character.gameObject);
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("_config").objectReferenceValue = packageOwned;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ConvaiAssetOwnership ownership = BodyAnimationPersonality.OwnershipOf(packageOwned);
            Assert.That(ownership.RequiresProjectCopy, Is.True,
                "A config that ships with the SDK has to be copied before it can be tuned.");
            Assert.That(ownership.IsWritable, Is.False);
            Assert.That(ownership.NoticeMessage, Is.Not.Empty,
                "An unavailable control without an explanation reads as a broken editor.");

            float before = packageOwned.GestureLiveliness;
            JObject refused = Json(Tune(new TuneBodyAnimationPersonalityRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                HowExpressive = before + 0.25f,
                DryRun = false
            }));

            Assert.That(refused.Value<bool>("success"), Is.False,
                "Tuning a package-owned config without consent must be refused, not applied.");
            Assert.That(packageOwned.GestureLiveliness, Is.EqualTo(before),
                "The shipped config was written to despite the refusal.");
        }

    }
}
