using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Editor.AI;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core;
using Convai.Modules.Gaze.Editor;
using Convai.Modules.Gaze.Editor.AI;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Tests.EditMode.AI
{
    /// <summary>
    ///     Contract and behaviour coverage for the Convai Gaze MCP tools.
    /// </summary>
    /// <remarks>
    ///     The load-bearing test here is
    ///     <see cref="DiagnoseReportsExactlyWhatTheSetupServiceReports" />: it is what keeps the
    ///     assistant-facing surface a projection of <c>GazeSetupService</c> rather than a second
    ///     opinion about the same character. Adding a check to the MCP layer alone fails it.
    /// </remarks>
    public sealed class ConvaiGazeMcpToolTests
    {
        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
        }

        [TearDown]
        public void TearDown()
        {
            _sceneFixture.End();
        }

        // ------------------------------------------------------------------ contract

        [Test]
        public void CatalogCarriesEveryGazeTool()
        {
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.ConfigureGaze"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.DiagnoseGaze"));
            Assert.That(ConvaiMcpToolCatalog.All, Does.Contain("Convai.MarkGazeTarget"));
        }

        [Test]
        public void SchemasAreClosedAndOfferEveryRuntimeChoice()
        {
            JObject configure = JObject.FromObject(ConvaiGazeMcpTools.ConfigureSchema());
            Assert.That(configure.Value<bool>("additionalProperties"), Is.False);
            Assert.That(configure["required"]?.Values<string>(), Is.EquivalentTo(new[] { "characterInstanceId" }));

            // A renamed runtime enum member must fail here rather than in a customer's assistant.
            AssertEnumMatches<GazeEyeContactMode>(configure, "eyeContactMode");
            AssertEnumMatches<GazeFocusFidelity>(configure, "focusFidelity");
            AssertEnumMatches<GazeAnchorAimMode>(configure, "playerAnchorAimMode");
            AssertEnumMatches<GazeBodyTurnStyle>(configure, "bodyTurnStyle");
            Assert.That(
                configure["properties"]?["capabilities"]?["items"]?["enum"]?.Values<string>(),
                Is.EquivalentTo(Enum.GetNames(typeof(GazeCapabilityId))));

            JObject diagnose = JObject.FromObject(ConvaiGazeMcpTools.DiagnoseSchema());
            Assert.That(diagnose.Value<bool>("additionalProperties"), Is.False);

            JObject mark = JObject.FromObject(ConvaiGazeMcpTools.MarkTargetSchema());
            Assert.That(mark.Value<bool>("additionalProperties"), Is.False);
            Assert.That(mark["required"]?.Values<string>(), Is.EquivalentTo(new[] { "gameObjectInstanceIds" }));
        }

        [Test]
        public void TuningFieldsDeclareNoDefaultSoOmittingOneLeavesItAlone()
        {
            JToken properties = JObject.FromObject(ConvaiGazeMcpTools.ConfigureSchema())["properties"];
            string[] tuningFields =
            {
                "eyeContactMode", "focusFidelity", "playerAnchorInstanceId", "clearPlayerAnchorOverride",
                "playerAnchorAimMode", "bodyTurnStyle", "allowScriptedOverrides", "lockBlocksGlances",
                "autoCreatePlayerAnchor"
            };

            foreach (string field in tuningFields)
            {
                Assert.That(properties?[field], Is.Not.Null, $"Missing tuning field {field}.");
                Assert.That(properties[field]["default"], Is.Null,
                    $"'{field}' declares a default, which tells an assistant that omitting it asks " +
                    "for that value. Omitting a tuning field must leave the project's own setting alone.");
            }
        }

        // ------------------------------------------------------------------ configure

        [Test]
        public void ConfigurePreviewChangesNothing()
        {
            ConvaiCharacter character = CreateCharacter("Preview");

            JObject response = Json(ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                EyeContactMode = GazeEyeContactMode.ConversationLock
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(response["data"]?["changes"]?.Values<string>(), Is.Not.Empty);
            Assert.That(character.GetComponent<ConvaiGazeController>(), Is.Null,
                "A preview added the Gaze component.");
        }

        [Test]
        public void ConfigureApplyAddsGazeAndIsUndoneInOneStep()
        {
            ConvaiCharacter character = CreateCharacter("Applied");

            JObject response = Json(ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                EyeContactMode = GazeEyeContactMode.ConversationLock,
                DryRun = false
            }));

            var controller = character.GetComponent<ConvaiGazeController>();
            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(controller, Is.Not.Null);
            Assert.That(controller.EyeContactMode, Is.EqualTo(GazeEyeContactMode.ConversationLock));

            Undo.PerformUndo();
            Assert.That(character.GetComponent<ConvaiGazeController>(), Is.Null,
                "Configuring gaze must collapse into a single undo step.");
        }

        [Test]
        public void ConfigureWritesOnlyTheSettingsTheRequestNames()
        {
            ConvaiCharacter character = CreateCharacter("Untouched");
            var controller = Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            controller.LockBlocksGlances = false;
            controller.FocusFidelity = GazeFocusFidelity.Exact;

            ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                EyeContactMode = GazeEyeContactMode.AlwaysLock,
                DryRun = false
            });

            Assert.That(controller.EyeContactMode, Is.EqualTo(GazeEyeContactMode.AlwaysLock));
            Assert.That(controller.LockBlocksGlances, Is.False, "An unnamed setting was reset.");
            Assert.That(controller.FocusFidelity, Is.EqualTo(GazeFocusFidelity.Exact), "An unnamed setting was reset.");
        }

        [Test]
        public void ConfigureGivesAFirstTimeCharacterTheRecommendedExtras()
        {
            ConvaiCharacter character = CreateCharacter("Recommended");

            ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                DryRun = false
            });

            Assert.That(Present(character, GazeCapabilityId.PlayerAttention), Is.True);
            Assert.That(Present(character, GazeCapabilityId.AttentionGrounding), Is.True);
        }

        [Test]
        public void ConfigureLeavesExtrasAloneWhenTheFieldIsOmitted()
        {
            ConvaiCharacter character = CreateCharacter("Kept");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            Undo.AddComponent<GazeJointAttention>(character.gameObject);

            ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                EyeContactMode = GazeEyeContactMode.Natural,
                DryRun = false
            });

            Assert.That(Present(character, GazeCapabilityId.JointAttention), Is.True,
                "Omitting 'capabilities' stripped an extra the project had turned on.");
            Assert.That(Present(character, GazeCapabilityId.PlayerAttention), Is.False,
                "Omitting 'capabilities' added the recommended set to an already-configured character.");
        }

        [Test]
        public void ConfigureWithAnEmptyCapabilityArrayRemovesThemAll()
        {
            ConvaiCharacter character = CreateCharacter("Stripped");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            Undo.AddComponent<GazeJointAttention>(character.gameObject);

            ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Capabilities = Array.Empty<string>(),
                DryRun = false
            });

            Assert.That(Present(character, GazeCapabilityId.JointAttention), Is.False);
        }

        [Test]
        public void ConfigureRejectsAProfilePathThatDoesNotExistAndNamesTheMenuPath()
        {
            ConvaiCharacter character = CreateCharacter("NoProfile");

            JObject response = Json(ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                ProfileAssetPath = "Assets/Nothing/Here.asset",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("INVALID_PROFILE"));
            Assert.That(response.ToString(), Does.Contain("Assets → Create → Convai → Embodiment → Gaze Profile"));
            Assert.That(character.GetComponent<ConvaiGazeController>(), Is.Null,
                "A rejected request still mutated the scene.");
        }

        [Test]
        public void ConfigureRejectsAnUnknownCapabilityWithoutTouchingTheScene()
        {
            ConvaiCharacter character = CreateCharacter("BadCapability");

            JObject response = Json(ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                Capabilities = new[] { "TelepathicAwareness" },
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("INVALID_CAPABILITY"));
            Assert.That(character.GetComponent<ConvaiGazeController>(), Is.Null);
        }

        [Test]
        public void ConfigureOnARigThatCannotHostGazeReportsTheBlockerAndDoesNotThrow()
        {
            ConvaiCharacter character = CreateCharacter("NoHead");

            JObject response = Json(ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("complete"), Is.False);
            Assert.That(response["data"]?["blockers"], Is.Not.Empty);
            Assert.That(response["data"]?["blockers"]?[0]?.Value<string>("message"),
                Does.Contain("head bone").IgnoreCase);
        }

        // ------------------------------------------------------------------ diagnose

        [Test]
        public void DiagnoseOnACharacterWithoutGazeSaysWhatToDoNext()
        {
            ConvaiCharacter character = CreateCharacter("Bare");

            JObject response = Json(ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("present"), Is.False);
            Assert.That(string.Join(" ", response["data"]?["nextSteps"]?.Values<string>() ?? Enumerable.Empty<string>()),
                Does.Contain("Add Component → Convai → Embodiment → Gaze"));
        }

        [Test]
        public void DiagnoseWithNoCharacterInTheSceneReportsRatherThanThrows()
        {
            JObject response = Json(ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest()));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo(ConvaiMcpResolvers.CharacterErrorCode));
        }

        [Test]
        public void DiagnoseNamesTheResolvedBonesAndTheEyeBackend()
        {
            ConvaiCharacter character = CreateCharacter("Rigged");
            AddHeadBone(character.gameObject, "Head");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            JObject response = Json(ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            Assert.That(response["data"]?["rig"]?.Value<string>("headBone"), Is.EqualTo("Head"));
            Assert.That(response["data"]?["rig"]?.Value<string>("eyeBackend"), Is.EqualTo("Head only"));
            Assert.That(response["data"]?["rig"]?.Value<string>("eyeBackendMessage"),
                Does.Contain("Character Rig"));
        }

        [Test]
        public void DiagnoseNamesWhichLinkOfTheAnchorChainWon()
        {
            ConvaiCharacter character = CreateCharacter("Watcher");
            AddHeadBone(character.gameObject, "Head");
            var controller = Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            var target = new GameObject("Split Screen Camera");
            Undo.RegisterCreatedObjectUndo(target, "test");
            SceneManager.MoveGameObjectToScene(target, TestScene);
            controller.PlayerAnchorOverride = target.transform;

            JObject response = Json(ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            JToken watches = response["data"]?["watches"];
            Assert.That(watches?.Value<string>("resolvedBy"), Is.EqualTo("PlayerAnchorOverride"));
            Assert.That(watches?.Value<string>("anchorName"), Is.EqualTo("Split Screen Camera"));
            Assert.That(watches?.Value<string>("message"), Does.Contain("Player Anchor Override"));
        }

        /// <summary>
        ///     Found by driving the tool for real: an issue points at the Gaze component, but the
        ///     repair it suggests takes a <em>character</em>. Building the suggestion from the
        ///     component id sends an assistant straight into INVALID_CHARACTER.
        /// </summary>
        [Test]
        public void SuggestedRepairArgumentsAddressTheCharacterNotTheComponent()
        {
            ConvaiCharacter character = CreateCharacter("Suggested");
            var controller = Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            long characterId = Id(character.gameObject);

            JObject response = Json(ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest
            {
                CharacterInstanceId = characterId
            }));

            JToken[] issues = response["data"]?["issues"]?.ToArray() ?? Array.Empty<JToken>();
            Assert.That(issues, Is.Not.Empty, "A character with no head bone must report issues.");

            foreach (JToken issue in issues)
            {
                Assert.That(
                    issue["suggestedArguments"]?.Value<long>("characterInstanceId"),
                    Is.EqualTo(characterId),
                    $"Issue {issue.Value<string>("code")} suggests a repair addressed to the wrong object.");
            }

            // And the suggestion must actually resolve, rather than merely look plausible.
            JObject followUp = Json(ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = issues[0]["suggestedArguments"].Value<long>("characterInstanceId")
            }));
            Assert.That(followUp.Value<bool>("success"), Is.True,
                "Following the tool's own suggested repair failed to resolve the character.");
            Assert.That(controller, Is.Not.Null);
        }

        [Test]
        public void DiagnoseIsReadOnly()
        {
            ConvaiCharacter character = CreateCharacter("ReadOnly");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            int before = TestScene.GetRootGameObjects().Sum(root => root.GetComponentsInChildren<Component>(true).Length);

            ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest { CharacterInstanceId = Id(character.gameObject) });

            int after = TestScene.GetRootGameObjects().Sum(root => root.GetComponentsInChildren<Component>(true).Length);
            Assert.That(after, Is.EqualTo(before));
        }

        /// <summary>
        ///     The load-bearing test: the MCP surface must be a projection of the setup service, not
        ///     a second check engine. If this fails, someone added a condition to the tool layer, and
        ///     an assistant can now describe a character differently from the editor.
        /// </summary>
        [Test]
        public void DiagnoseReportsExactlyWhatTheSetupServiceReports()
        {
            ConvaiCharacter character = CreateCharacter("Parity");
            AddHeadBone(character.gameObject, "Head");
            var controller = Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            JObject response = Json(ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));

            GazePreflight preflight = GazeSetupService.Inspect(controller);
            var serialized = new SerializedObject(controller);
            GazeSetupInput input = GazeSetupTroubleshooter.GatherFrom(
                controller, serialized.FindProperty("profile"),
                serialized.FindProperty("autoCreatePlayerAnchor").boolValue);
            var findings = new List<GazeSetupFinding>(8);
            GazeSetupTroubleshooter.Evaluate(in input, findings);

            string[] reportedChecks = response["data"]?["checks"]?
                .Select(check => $"{check.Value<string>("label")}|{check.Value<string>("detail")}|{check.Value<string>("state")}")
                .ToArray();
            string[] serviceChecks = preflight.Checks
                .Select(check => $"{check.Label}|{check.Detail}|{check.State}")
                .ToArray();
            Assert.That(reportedChecks, Is.EqualTo(serviceChecks),
                "The tool's checks drifted from GazeSetupService.Inspect.");

            string[] reportedIssues = response["data"]?["issues"]?
                .Select(issue => $"{issue.Value<string>("severity")}|{issue.Value<string>("message")}")
                .ToArray();
            string[] serviceIssues = findings
                .Where(finding => finding.Severity != GazeSetupSeverity.Ok)
                .Select(finding => $"{finding.Severity}|{finding.Message}")
                .ToArray();
            Assert.That(reportedIssues, Is.EqualTo(serviceIssues),
                "The tool's issues drifted from GazeSetupTroubleshooter.Evaluate.");

            Assert.That(response["data"]?.Value<bool>("ready"), Is.EqualTo(preflight.IsReady));
            Assert.That(response["data"]?.Value<bool>("isWorking"), Is.EqualTo(preflight.IsFunctional));
        }

        [Test]
        public void SceneSurveyAgreesWithDiagnose()
        {
            ConvaiCharacter character = CreateCharacter("Surveyed");
            AddHeadBone(character.gameObject, "Head");
            Undo.AddComponent<ConvaiGazeController>(character.gameObject);

            JObject diagnose = Json(ConvaiGazeMcpTools.Diagnose(new DiagnoseGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject)
            }));
            JObject inspect = Json(ConvaiMcpTools.InspectScene(new ConvaiSceneInspectionRequest()));

            JToken surveyed = inspect["data"]?["characters"]?
                .First(entry => entry.Value<string>("name") == character.gameObject.name)["modules"]?
                .FirstOrDefault(module => module.Value<string>("moduleId") == "convai.gaze");

            Assert.That(surveyed, Is.Not.Null, "InspectScene does not report the Gaze module.");
            Assert.That(surveyed.Value<bool>("present"), Is.True);
            Assert.That(surveyed.Value<bool>("working"), Is.EqualTo(diagnose["data"]?.Value<bool>("isWorking")));
        }

        // ------------------------------------------------------------------ mark target

        [Test]
        public void MarkTargetPreviewAddsNothingAndApplyIsIdempotent()
        {
            var prop = new GameObject("Painting");
            Undo.RegisterCreatedObjectUndo(prop, "test");
            SceneManager.MoveGameObjectToScene(prop, TestScene);
            var request = new MarkGazeTargetRequest { GameObjectInstanceIds = new[] { Id(prop) } };

            JObject preview = Json(ConvaiGazeMcpTools.MarkTarget(request));
            Assert.That(preview["data"]?["results"]?[0]?.Value<string>("action"), Is.EqualTo("Added"));
            Assert.That(prop.GetComponent<ConvaiGazeTarget>(), Is.Null);

            request.DryRun = false;
            JObject first = Json(ConvaiGazeMcpTools.MarkTarget(request));
            JObject second = Json(ConvaiGazeMcpTools.MarkTarget(request));

            Assert.That(first["data"]?["results"]?[0]?.Value<string>("action"), Is.EqualTo("Added"));
            Assert.That(second["data"]?["results"]?[0]?.Value<string>("action"), Is.EqualTo("AlreadyMarked"));
            Assert.That(prop.GetComponents<ConvaiGazeTarget>().Length, Is.EqualTo(1));

            request.Remove = true;
            JObject removed = Json(ConvaiGazeMcpTools.MarkTarget(request));
            Assert.That(removed["data"]?["results"]?[0]?.Value<string>("action"), Is.EqualTo("Removed"));
            Assert.That(prop.GetComponent<ConvaiGazeTarget>(), Is.Null);
        }

        [Test]
        public void MarkTargetWarnsWhenAPropWouldOutrankThePlayer()
        {
            var prop = new GameObject("Attention Hog");
            Undo.RegisterCreatedObjectUndo(prop, "test");
            SceneManager.MoveGameObjectToScene(prop, TestScene);

            JObject response = Json(ConvaiGazeMcpTools.MarkTarget(new MarkGazeTargetRequest
            {
                GameObjectInstanceIds = new[] { Id(prop) },
                Priority = 20
            }));

            Assert.That(
                string.Join(" ", response["data"]?["warnings"]?.Values<string>() ?? Enumerable.Empty<string>()),
                Does.Contain("instead of the player"));
        }

        [Test]
        public void MarkTargetWithoutObjectsAsksForThem()
        {
            JObject response = Json(ConvaiGazeMcpTools.MarkTarget(new MarkGazeTargetRequest()));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(response["data"]?.Value<string>("code"), Is.EqualTo("NO_TARGETS"));
        }

        // ------------------------------------------------------------------ service extensions

        [Test]
        public void AnchorReportNamesThePlayerAnchorProviderBetweenOverrideAndCamera()
        {
            ConvaiCharacter character = CreateCharacter("Provider");
            var controller = Undo.AddComponent<ConvaiGazeController>(character.gameObject);
            var provider = Undo.AddComponent<PlayerAnchorTargetProvider>(character.gameObject);

            var anchorObject = new GameObject("Cutscene Rig");
            Undo.RegisterCreatedObjectUndo(anchorObject, "test");
            SceneManager.MoveGameObjectToScene(anchorObject, TestScene);
            provider.ExplicitAnchor = anchorObject.transform;

            GazeAnchorReport report = GazeSetupService.InspectAnchor(controller);

            Assert.That(report.Source, Is.EqualTo(GazeAnchorSource.PlayerAnchorProvider));
            Assert.That(report.Anchor, Is.EqualTo(anchorObject.transform));
            Assert.That(report.ProviderPresent, Is.True);
        }

        [Test]
        public void FacingCheckMeasuresInEditModeAndFailsARigTurnedSideways()
        {
            ConvaiCharacter forward = CreateCharacter("FacesForward");
            AddHeadBone(forward.gameObject, "Head");
            var forwardController = Undo.AddComponent<ConvaiGazeController>(forward.gameObject);

            ConvaiCharacter sideways = CreateCharacter("FacesSideways");
            Transform head = AddHeadBone(sideways.gameObject, "Head");
            head.localRotation = Quaternion.Euler(0f, 90f, 0f);
            var sidewaysController = Undo.AddComponent<ConvaiGazeController>(sideways.gameObject);

            GazeFacingReport pass = GazeSetupService.InspectFacing(forwardController);
            GazeFacingReport fail = GazeSetupService.InspectFacing(sidewaysController);

            Assert.That(pass.State, Is.EqualTo(GazeFacingState.Pass),
                "The facing check no longer measures outside Play Mode.");
            Assert.That(pass.AngleDegrees, Is.LessThan(1f));
            Assert.That(fail.State, Is.EqualTo(GazeFacingState.Fail));
            Assert.That(fail.AngleDegrees, Is.EqualTo(90f).Within(1f));
        }

        [Test]
        public void EveryTroubleshooterFindingWithAKnownRepairCarriesIt()
        {
            var input = new GazeSetupInput { AutoCreatePlayerAnchor = false };
            var findings = new List<GazeSetupFinding>(8);
            GazeSetupTroubleshooter.Evaluate(in input, findings);

            GazeSetupFinding headBone = findings.Single(finding => finding.Title == "Head Bone");
            GazeSetupFinding profile = findings.Single(finding => finding.Title == "Profile");

            Assert.That(headBone.Fix, Is.EqualTo(GazeFixId.AddRigBinding));
            Assert.That(profile.Fix, Is.EqualTo(GazeFixId.AssignDefaultProfile));
            Assert.That(GazeSetupService.DescribeFix(profile.Fix), Is.EqualTo("Add a Personality"));
        }

        // ------------------------------------------------------------------ helpers

        private ConvaiCharacter CreateCharacter(string name)
        {
            var host = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(host, "test");
            SceneManager.MoveGameObjectToScene(host, TestScene);
            return Undo.AddComponent<ConvaiCharacter>(host);
        }

        private static Transform AddHeadBone(GameObject character, string boneName)
        {
            var binding = character.GetComponent<StandardRigBinding>() ??
                          Undo.AddComponent<StandardRigBinding>(character);
            var bone = new GameObject(boneName);
            Undo.RegisterCreatedObjectUndo(bone, "test");
            bone.transform.SetParent(character.transform, false);

            SerializedObject serialized = new(binding);
            SerializedProperty headOverride = serialized.FindProperty("headOverride");
            Assert.That(headOverride, Is.Not.Null, "StandardRigBinding no longer serialises headOverride.");
            headOverride.objectReferenceValue = bone.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return bone.transform;
        }

        private static bool Present(ConvaiCharacter character, GazeCapabilityId id) =>
            GazeCapabilities.IsPresentUnder(character.transform, GazeCapabilities.ProviderTypeOf(id));

        private static long Id(GameObject value) => ConvaiMcpEntityRef.ToToolId(value);

        private static void AssertEnumMatches<T>(JObject schema, string property) where T : struct, Enum =>
            Assert.That(
                schema["properties"]?[property]?["enum"]?.Values<string>(),
                Is.EquivalentTo(Enum.GetNames(typeof(T))),
                $"'{property}' drifted from {typeof(T).Name}.");

        private static JObject Json(object response) => JObject.FromObject(response);

        [Test]
        public void ConfigureGaze_CreatesNoAsset_AndSaysWhereProfilesComeFrom()
        {
            ConvaiCharacter character = CreateCharacter("Gaze_NoAssetWrites");
            string[] before = AssetDatabase.FindAssets("t:ConvaiGazeProfile");

            JObject applied = Json(ConvaiGazeMcpTools.Configure(new ConfigureGazeRequest
            {
                CharacterInstanceId = Id(character.gameObject),
                DryRun = false
            }));

            Assert.That(applied.Value<bool>("success"), Is.True);
            Assert.That(
                AssetDatabase.FindAssets("t:ConvaiGazeProfile").Length, Is.EqualTo(before.Length),
                "Configure wrote a Gaze Profile asset. It must never author one.");

            Assert.That(
                character.GetComponent<ConvaiGazeController>(), Is.Not.Null,
                "Configure still has to add the component it promised.");

            string notes = applied["data"]?["notes"]?.ToString() ?? string.Empty;
            Assert.That(
                notes, Does.Contain("Create → Convai → Embodiment → Gaze Profile"),
                "A character left without a profile must be told where profiles come from.");
        }

    }
}
