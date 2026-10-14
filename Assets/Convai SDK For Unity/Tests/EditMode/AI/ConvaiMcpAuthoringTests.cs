using System;
using System.Linq;
using Convai.Editor.AI;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Vision.Context;
using Convai.Shared.Compatibility;
using Convai.Shared.Types;
using Newtonsoft.Json.Linq;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.AI
{
    public sealed class ConvaiMcpAuthoringTests
    {
        private const string CharacterId = "69090ea4-0c5e-11f1-9d63-42010a7be02c";
        private ConvaiMcpSceneFixture _sceneFixture;

        /// <summary>The scene this test owns, opened and put back by the fixture.</summary>
        private Scene TestScene => _sceneFixture.TestScene;
        private string _temporaryTestScenePath;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
        }

        [TearDown]
        public void TearDown()
        {
            _sceneFixture.End();

            if (!string.IsNullOrEmpty(_temporaryTestScenePath))
            {
                AssetDatabase.DeleteAsset(_temporaryTestScenePath);
                _temporaryTestScenePath = null;
            }

            TestAssetSandbox.Clear(nameof(ConvaiMcpAuthoringTests));
        }

        [Test]
        public void SetupConversationScene_DryRunDoesNotMutateEmptyScene()
        {
            JObject response = Json(ConvaiMcpTools.SetupConversationScene(
                new ConvaiSetupConversationSceneRequest
                {
                    CharacterId = CharacterId,
                    DryRun = true
                }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True);
            Assert.That(TestScene.GetRootGameObjects(), Is.Empty);
            Assert.That(response["data"]?["changes"]?.Values<string>(),
                Does.Contain("Create standalone Convai Player with ConvaiPlayer."));
        }

        [Test]
        public void SetupConversationScene_ApplyCreatesRunnableFoundationAndIsIdempotent()
        {
            var request = new ConvaiSetupConversationSceneRequest
            {
                CharacterId = CharacterId,
                DryRun = false
            };

            JObject first = Json(ConvaiMcpTools.SetupConversationScene(request));
            JObject second = Json(ConvaiMcpTools.SetupConversationScene(request));

            Assert.That(first.Value<bool>("success"), Is.True);
            Assert.That(TestScene.GetRootGameObjects().Count(root => root.name == "[Convai Manager]"), Is.EqualTo(1));
            Assert.That(TestScene.GetRootGameObjects().Count(root => root.name == "Convai Player"), Is.EqualTo(1));
            Assert.That(TestScene.GetRootGameObjects().Count(root => root.name == "Convai Character"), Is.EqualTo(1));
            Assert.That(ConvaiObjectFind.All<ConvaiManager>(FindObjectsInactive.Include).Count(item => item.gameObject.scene == TestScene), Is.EqualTo(1));
            Assert.That(ConvaiObjectFind.All<ConvaiRoomManager>(FindObjectsInactive.Include).Count(item => item.gameObject.scene == TestScene), Is.EqualTo(1));
            Assert.That(ConvaiObjectFind.All<ConvaiPlayer>(FindObjectsInactive.Include).Count(item => item.gameObject.scene == TestScene), Is.EqualTo(1));
            ConvaiCharacter character = ConvaiObjectFind.All<ConvaiCharacter>(FindObjectsInactive.Include)
                .Single(item => item.gameObject.scene == TestScene);
            Assert.That(character.GetComponent<AudioSource>(), Is.Not.Null);
            Assert.That(character.GetComponent<ConvaiAudioOutput>(), Is.Not.Null);
            Assert.That(character.CharacterId, Is.EqualTo(request.CharacterId));
            Assert.That(second["data"]?["changes"]?.Count(), Is.EqualTo(0));
        }

        [Test]
        public void ConfigureCharacter_MissingIdAuthorsComponentsButReportsIncomplete()
        {
            GameObject managerObject = new("[Convai Manager]");
            managerObject.AddComponent<ConvaiManager>();
            GameObject characterObject = new("NPC");

            JObject response = Json(ConvaiMcpTools.ConfigureCharacter(new ConvaiConfigureCharacterRequest
            {
                TargetInstanceId = Id(characterObject),
                ManagerInstanceId = Id(managerObject),
                CharacterName = "NPC",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("complete"), Is.False);
            Assert.That(response["data"]?["requiredInputs"]?.Values<string>(), Does.Contain("characterId"));
            Assert.That(characterObject.GetComponent<ConvaiCharacter>(), Is.Not.Null);
            Assert.That(characterObject.GetComponent<ConvaiAudioOutput>(), Is.Not.Null);
        }

        [Test]
        public void ConfigureCharacter_InvalidIdFailsWithoutMutation()
        {
            GameObject target = new("NPC");

            JObject response = Json(ConvaiMcpTools.ConfigureCharacter(new ConvaiConfigureCharacterRequest
            {
                TargetInstanceId = Id(target),
                CharacterId = "not-a-character-id",
                DryRun = false
            }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(target.GetComponent<ConvaiCharacter>(), Is.Null);
        }

        [Test]
        public void ConfigureActions_DryRunApplyAndRepeatStayConsistent()
        {
            GameObject target = new("NPC");
            target.AddComponent<ConvaiCharacter>();
            var request = new ConvaiConfigureActionsRequest
            {
                CharacterInstanceId = Id(target),
                Definitions = new[]
                {
                    new ConvaiActionDefinitionInput { Name = "Wave", Description = "Wave to the player" }
                },
                DryRun = true
            };

            JObject preview = Json(ConvaiMcpTools.ConfigureActions(request));
            Assert.That(preview.Value<bool>("success"), Is.True);
            Assert.That(preview["data"]?.Value<bool>("complete"), Is.False);
            Assert.That(preview["data"]?["blockedSteps"]?.Values<string>().Single(), Does.Contain("Wave"));
            Assert.That(target.GetComponent<ConvaiActionConfigSource>(), Is.Null);

            request.DryRun = false;
            JObject applied = Json(ConvaiMcpTools.ConfigureActions(request));
            JObject repeated = Json(ConvaiMcpTools.ConfigureActions(request));

            Assert.That(applied.Value<bool>("success"), Is.True);
            Assert.That(target.GetComponent<ConvaiActionConfigSource>(), Is.Not.Null);
            Assert.That(target.GetComponent<ConvaiActionDispatcher>(), Is.Not.Null);
            Assert.That(target.GetComponents<ConvaiUnityEventActionExecutor>(), Has.Length.EqualTo(1));
            Assert.That(repeated["data"]?["changes"]?.Count(), Is.EqualTo(0));
            Assert.That(target.GetComponents<ConvaiUnityEventActionExecutor>(), Has.Length.EqualTo(1));
        }

        [Test]
        public void ConfigureActions_EnabledWritesApplyAndOmissionLeavesTheFlagUnchanged()
        {
            GameObject target = new("NPC");
            target.AddComponent<ConvaiCharacter>();
            var request = new ConvaiConfigureActionsRequest
            {
                CharacterInstanceId = Id(target),
                Definitions = new[]
                {
                    new ConvaiActionDefinitionInput { Name = "Wave", Description = "Wave to the player", Enabled = false }
                },
                DryRun = false
            };

            JObject disabledResponse = Json(ConvaiMcpTools.ConfigureActions(request));
            ConvaiActionConfigSource source = target.GetComponent<ConvaiActionConfigSource>();

            Assert.That(disabledResponse.Value<bool>("success"), Is.True);
            Assert.That(source.Definitions.Single().Enabled, Is.False);

            // Omitting enabled must be a no-op on the authored flag: the repeat reports no changes.
            request.Definitions[0].Enabled = null;
            JObject omitted = Json(ConvaiMcpTools.ConfigureActions(request));
            Assert.That(omitted["data"]?["changes"]?.Count(), Is.EqualTo(0));
            Assert.That(source.Definitions.Single().Enabled, Is.False);

            request.Definitions[0].Enabled = true;
            JObject reEnabled = Json(ConvaiMcpTools.ConfigureActions(request));
            Assert.That(reEnabled.Value<bool>("success"), Is.True);
            Assert.That(source.Definitions.Single().Enabled, Is.True);
        }

        [Test]
        public void DiagnoseActions_ReportsPerActionEnabledStateAndAssignedActionSets()
        {
            GameObject target = new("NPC");
            target.AddComponent<ConvaiCharacter>();
            Json(ConvaiMcpTools.ConfigureActions(new ConvaiConfigureActionsRequest
            {
                CharacterInstanceId = Id(target),
                Definitions = new[]
                {
                    new ConvaiActionDefinitionInput { Name = "Wave", Description = "Wave to the player", Enabled = false }
                },
                DryRun = false
            }));
            ConvaiActionConfigSource source = target.GetComponent<ConvaiActionConfigSource>();

            ConvaiActionSet actionSet = ScriptableObject.CreateInstance<ConvaiActionSet>();
            actionSet.name = "Diagnose Test Set";
            try
            {
                var serializedSet = new SerializedObject(actionSet);
                SerializedProperty setDefinitions = serializedSet.FindProperty("_definitions");
                setDefinitions.arraySize = 1;
                SerializedProperty entry = setDefinitions.GetArrayElementAtIndex(0);
                entry.FindPropertyRelative("ActionName").stringValue = "Nod";
                entry.FindPropertyRelative("Description").stringValue = "Nod once";
                entry.FindPropertyRelative("ExecutorTypeHint").stringValue = "ConvaiWaitActionExecutor";
                serializedSet.ApplyModifiedPropertiesWithoutUndo();

                var serializedSource = new SerializedObject(source);
                SerializedProperty actionSets = serializedSource.FindProperty("_actionSets");
                actionSets.arraySize = 1;
                actionSets.GetArrayElementAtIndex(0).objectReferenceValue = actionSet;
                serializedSource.ApplyModifiedPropertiesWithoutUndo();

                JObject response = Json(ConvaiMcpTools.DiagnoseActions(new ConvaiDiagnoseActionsRequest
                {
                    CharacterInstanceId = Id(target)
                }));

                Assert.That(response.Value<bool>("success"), Is.True);
                JToken actions = response["data"]?["configuration"]?["actions"];
                Assert.That(actions, Is.Not.Null);
                JToken wave = actions.Single(item => item.Value<string>("name") == "Wave");
                JToken nod = actions.Single(item => item.Value<string>("name") == "Nod");
                Assert.That(wave.Value<bool>("enabled"), Is.False);
                Assert.That(nod.Value<bool>("enabled"), Is.True);

                JToken sets = response["data"]?["configuration"]?["actionSets"];
                Assert.That(sets, Is.Not.Null);
                Assert.That(sets.Count(), Is.EqualTo(1));
                Assert.That(sets[0].Value<string>("name"), Is.EqualTo("Diagnose Test Set"));
                Assert.That(sets[0]["actions"]?.Single().Value<string>("name"), Is.EqualTo("Nod"));
                Assert.That(sets[0]["actions"]?.Single().Value<bool>("enabled"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(actionSet);
            }
        }

        [Test]
        public void ConfigureActions_InvalidExecutorFailsBeforeMutation()
        {
            GameObject target = new("NPC");
            ConvaiCharacter character = target.AddComponent<ConvaiCharacter>();
            var request = new ConvaiConfigureActionsRequest
            {
                CharacterInstanceId = Id(target),
                Definitions = new[]
                {
                    new ConvaiActionDefinitionInput
                    {
                        Name = "Wave",
                        ExecutorInstanceId = Id(character)
                    }
                },
                DryRun = false
            };

            JObject response = Json(ConvaiMcpTools.ConfigureActions(request));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(target.GetComponent<ConvaiActionConfigSource>(), Is.Null);
            Assert.That(target.GetComponent<ConvaiActionDispatcher>(), Is.Null);
            Assert.That(target.GetComponent<ConvaiUnityEventActionExecutor>(), Is.Null);
        }

        [Test]
        public void ConfigureTranscripts_ApplyIsIdempotent()
        {
            GameObject target = new("[Convai Manager]");
            target.AddComponent<ConvaiManager>();
            var request = new ConvaiConfigureTranscriptsRequest
            {
                ManagerInstanceId = Id(target),
                HostInstanceId = Id(target),
                Mode = ConvaiTranscriptToolMode.EventRelay,
                DryRun = false
            };

            JObject applied = Json(ConvaiMcpTools.ConfigureTranscripts(request));
            JObject repeated = Json(ConvaiMcpTools.ConfigureTranscripts(request));

            Assert.That(applied.Value<bool>("success"), Is.True);
            Assert.That(target.GetComponents<Convai.Runtime.Presentation.Events.ConvaiTranscriptEventRelay>(), Has.Length.EqualTo(1));
            Assert.That(repeated["data"]?["changes"]?.Count(), Is.EqualTo(0));
        }

        [Test]
        public void ConfigureTranscripts_EveryAdvertisedModeProducesDryRunChanges()
        {
            GameObject target = new("[Convai Manager]");
            target.AddComponent<ConvaiManager>();

            foreach (ConvaiTranscriptToolMode mode in Enum.GetValues(typeof(ConvaiTranscriptToolMode)))
            {
                JObject response = Json(ConvaiMcpTools.ConfigureTranscripts(
                    new ConvaiConfigureTranscriptsRequest
                    {
                        ManagerInstanceId = Id(target),
                        HostInstanceId = Id(target),
                        Mode = mode,
                        DryRun = true
                    }));

                Assert.That(response.Value<bool>("success"), Is.True, $"{mode} should have a handler.");
                Assert.That(response["data"]?.Value<bool>("dryRun"), Is.True, $"{mode} should produce a dry-run plan.");
                Assert.That(response["data"]?["changes"]?.Count(), Is.GreaterThan(0),
                    $"{mode} should produce at least one planned change.");
            }
        }

        [Test]
        public void ConfigureRoom_DryRunApplyIdempotencyUndoAndNoSave()
        {
            GameObject target = new("Room");
            var request = new ConvaiConfigureRoomRequest
            {
                TargetInstanceId = Id(target),
                PushToTalkKey = "T",
                DryRun = true
            };

            JObject preview = Json(ConvaiMcpTools.ConfigureRoom(request));
            Assert.That(preview.Value<bool>("success"), Is.True);
            Assert.That(target.GetComponent<ConvaiManager>(), Is.Null);
            Assert.That(target.GetComponent<ConvaiRoomManager>(), Is.Null);

            request.DryRun = false;
            JObject applied = Json(ConvaiMcpTools.ConfigureRoom(request));
            JObject repeated = Json(ConvaiMcpTools.ConfigureRoom(request));
            ConvaiRoomManager room = target.GetComponent<ConvaiRoomManager>();

            Assert.That(applied.Value<bool>("success"), Is.True);
            Assert.That(target.GetComponent<ConvaiManager>(), Is.Not.Null);
            Assert.That(room, Is.Not.Null);
            Assert.That(room.PushToTalkKey, Is.EqualTo(KeyCode.T));
            Assert.That(repeated["data"]?["changes"]?.Count(), Is.EqualTo(0));
            Assert.That(TestScene.path, Is.Empty);
            Assert.That(applied["data"]?.Value<bool>("sceneSaved"), Is.False);

            Undo.PerformUndo();
            Assert.That(target.GetComponent<ConvaiManager>(), Is.Null);
            Assert.That(target.GetComponent<ConvaiRoomManager>(), Is.Null);
        }

        [Test]
        public void ConfigurePlayer_DryRunApplyIdempotencyUndoAndLeavesCameraAlone()
        {
            GameObject managerObject = new("[Convai Manager]");
            ConvaiManager manager = managerObject.AddComponent<ConvaiManager>();
            GameObject target = new("Player Target");
            GameObject cameraObject = new("Main Camera");
            cameraObject.AddComponent<Camera>();
            var request = new ConvaiConfigurePlayerRequest
            {
                TargetInstanceId = Id(target),
                ManagerInstanceId = Id(managerObject),
                PlayerName = "Player",
                DryRun = true
            };

            Assert.That(Json(ConvaiMcpTools.ConfigurePlayer(request)).Value<bool>("success"), Is.True);
            Assert.That(target.GetComponent<ConvaiPlayer>(), Is.Null);

            request.DryRun = false;
            JObject applied = Json(ConvaiMcpTools.ConfigurePlayer(request));
            JObject repeated = Json(ConvaiMcpTools.ConfigurePlayer(request));
            ConvaiPlayer player = target.GetComponent<ConvaiPlayer>();

            Assert.That(applied.Value<bool>("success"), Is.True);
            Assert.That(player, Is.Not.Null);
            Assert.That(ReadReference(manager, "_explicitPlayer"), Is.EqualTo(player));
            Assert.That(cameraObject.GetComponent<ConvaiPlayer>(), Is.Null);
            Assert.That(repeated["data"]?["changes"]?.Count(), Is.EqualTo(0));

            Undo.PerformUndo();
            Assert.That(target.GetComponent<ConvaiPlayer>(), Is.Null);
            Assert.That(ReadReference(manager, "_explicitPlayer"), Is.Null);
        }

        [Test]
        public void ConfigureCharacter_DryRunApplyIdempotencyUndoAndOwnership()
        {
            GameObject managerObject = new("[Convai Manager]");
            ConvaiManager manager = managerObject.AddComponent<ConvaiManager>();
            GameObject target = new("NPC");
            var request = new ConvaiConfigureCharacterRequest
            {
                TargetInstanceId = Id(target),
                ManagerInstanceId = Id(managerObject),
                CharacterId = CharacterId,
                CharacterName = "NPC",
                DryRun = true
            };

            Assert.That(Json(ConvaiMcpTools.ConfigureCharacter(request)).Value<bool>("success"), Is.True);
            Assert.That(target.GetComponent<ConvaiCharacter>(), Is.Null);

            request.DryRun = false;
            JObject applied = Json(ConvaiMcpTools.ConfigureCharacter(request));
            JObject repeated = Json(ConvaiMcpTools.ConfigureCharacter(request));
            ConvaiCharacter character = target.GetComponent<ConvaiCharacter>();

            Assert.That(applied.Value<bool>("success"), Is.True);
            Assert.That(character, Is.Not.Null);
            Assert.That(target.GetComponent<AudioSource>(), Is.Not.Null);
            Assert.That(target.GetComponent<ConvaiAudioOutput>(), Is.Not.Null);
            Assert.That(ReadReferences(manager, "_explicitCharacters"), Does.Contain(character));
            Assert.That(ReadReference(manager, "_explicitConversationTarget"), Is.EqualTo(character));
            Assert.That(repeated["data"]?["changes"]?.Count(), Is.EqualTo(0));

            Undo.PerformUndo();
            Assert.That(target.GetComponent<ConvaiCharacter>(), Is.Null);
            Assert.That(target.GetComponent<ConvaiAudioOutput>(), Is.Null);
            Assert.That(ReadReferences(manager, "_explicitCharacters"), Is.Empty);
        }

        [Test]
        public void MutatorRejectsTargetFromNonActiveSceneBeforeMutation()
        {
            GameObject target = new("Wrong Scene Target");

            // A second scene that is not the active one. The test's own scene is saved first
            // because Unity refuses to create any additive scene while an untitled one is loaded
            // (see ConvaiMcpSceneFixture). This used to borrow whatever scene the developer had
            // open when there was one, so the test exercised a different path for them than in CI.
            _temporaryTestScenePath = AssetDatabase.GenerateUniqueAssetPath(
                TestAssetSandbox.Path(nameof(ConvaiMcpAuthoringTests), "ActiveTest.unity"));
            Assert.That(EditorSceneManager.SaveScene(TestScene, _temporaryTestScenePath), Is.True);
            Scene wrongScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

            try
            {
                SceneManager.MoveGameObjectToScene(target, wrongScene);
                SceneManager.SetActiveScene(TestScene);

                JObject response = Json(ConvaiMcpTools.ConfigureRoom(new ConvaiConfigureRoomRequest
                {
                    TargetInstanceId = Id(target),
                    DryRun = false
                }));

                Assert.That(response.Value<bool>("success"), Is.False);
                Assert.That(response.Value<string>("message"), Does.Contain("active scene"));
                Assert.That(target.GetComponent<ConvaiManager>(), Is.Null);
            }
            finally
            {
                if (target != null) Object.DestroyImmediate(target);
                if (wrongScene.IsValid() && wrongScene.isLoaded)
                    EditorSceneManager.CloseScene(wrongScene, true);
            }
        }

        [Test]
        public void ExistingProfilesAreAssignedAndInvalidProfileIdentityFailsBeforeMutation()
        {
            string roomPath = AssetDatabase.GenerateUniqueAssetPath(
                TestAssetSandbox.Path(nameof(ConvaiMcpAuthoringTests), "RoomProfileTest.asset"));
            string characterPath = AssetDatabase.GenerateUniqueAssetPath(
                TestAssetSandbox.Path(nameof(ConvaiMcpAuthoringTests), "CharacterProfileTest.asset"));
            ConvaiRoomManagerProfile roomProfile = ScriptableObject.CreateInstance<ConvaiRoomManagerProfile>();
            ConvaiCharacterProfile characterProfile = ScriptableObject.CreateInstance<ConvaiCharacterProfile>();
            AssetDatabase.CreateAsset(roomProfile, roomPath);
            AssetDatabase.CreateAsset(characterProfile, characterPath);
            try
            {
                var characterSerialized = new SerializedObject(characterProfile);
                characterSerialized.FindProperty("_characterId").stringValue = CharacterId;
                characterSerialized.FindProperty("_characterName").stringValue = "Profile NPC";
                characterSerialized.ApplyModifiedPropertiesWithoutUndo();

                GameObject managerObject = new("[Convai Manager]");
                managerObject.AddComponent<ConvaiManager>();
                GameObject characterObject = new("NPC");
                JObject roomResponse = Json(ConvaiMcpTools.ConfigureRoom(new ConvaiConfigureRoomRequest
                {
                    TargetInstanceId = Id(managerObject),
                    ConfigurationMode = ConvaiToolConfigurationMode.ExistingProfile,
                    ProfileAssetPath = roomPath,
                    DryRun = false
                }));
                JObject characterResponse = Json(ConvaiMcpTools.ConfigureCharacter(
                    new ConvaiConfigureCharacterRequest
                    {
                        TargetInstanceId = Id(characterObject),
                        ManagerInstanceId = Id(managerObject),
                        ConfigurationMode = ConvaiToolConfigurationMode.ExistingProfile,
                        ProfileAssetPath = characterPath,
                        DryRun = false
                    }));

                Assert.That(roomResponse.Value<bool>("success"), Is.True);
                Assert.That(managerObject.GetComponent<ConvaiRoomManager>().RoomConfigAsset, Is.EqualTo(roomProfile));
                Assert.That(characterResponse.Value<bool>("success"), Is.True);
                Assert.That(characterObject.GetComponent<ConvaiCharacter>().CharacterConfigAsset,
                    Is.EqualTo(characterProfile));

                characterSerialized.Update();
                characterSerialized.FindProperty("_characterId").stringValue = "invalid";
                characterSerialized.ApplyModifiedPropertiesWithoutUndo();
                GameObject invalidTarget = new("Invalid Profile NPC");
                JObject invalidResponse = Json(ConvaiMcpTools.ConfigureCharacter(
                    new ConvaiConfigureCharacterRequest
                    {
                        TargetInstanceId = Id(invalidTarget),
                        ManagerInstanceId = Id(managerObject),
                        ConfigurationMode = ConvaiToolConfigurationMode.ExistingProfile,
                        ProfileAssetPath = characterPath,
                        DryRun = false
                    }));
                Assert.That(invalidResponse.Value<bool>("success"), Is.False);
                Assert.That(invalidTarget.GetComponent<ConvaiCharacter>(), Is.Null);
            }
            finally
            {
                AssetDatabase.DeleteAsset(roomPath);
                AssetDatabase.DeleteAsset(characterPath);
            }
        }

        [Test]
        public void DiagnoseConversation_DefaultSceneNamesTheStartingCharacterWithoutBlocking()
        {
            GameObject managerObject = new("[Convai Manager]");
            managerObject.AddComponent<ConvaiManager>();
            managerObject.AddComponent<ConvaiRoomManager>();
            new GameObject("Player").AddComponent<ConvaiPlayer>();
            CreateConfiguredCharacter("NPC A", CharacterId);
            CreateConfiguredCharacter("NPC B", CharacterId);

            JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest()));
            string[] codes = IssueCodes(response);

            Assert.That(codes, Does.Not.Contain("PLAYER_OWNERSHIP_MISSING"));
            Assert.That(codes, Does.Not.Contain("CHARACTER_OWNERSHIP_MISSING"));
            Assert.That(codes, Does.Contain("CONVERSATION_TARGET_DEFAULTED"));
            Assert.That(codes, Does.Contain("CHARACTER_ID_DUPLICATE"));
            Assert.That(
                IssueSeverity(response, "CONVERSATION_TARGET_DEFAULTED"),
                Is.EqualTo("Info"),
                "An unassigned Initial Character no longer stops the room, so it must not read as an error.");
        }

        [Test]
        public void DiagnoseConversation_SingleCharacterNeedsNoExplicitOwnershipOrTarget()
        {
            GameObject managerObject = new("[Convai Manager]");
            managerObject.AddComponent<ConvaiManager>();
            managerObject.AddComponent<ConvaiRoomManager>();
            new GameObject("Player").AddComponent<ConvaiPlayer>();
            CreateConfiguredCharacter("NPC", CharacterId);

            JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest()));
            string[] codes = IssueCodes(response);

            Assert.That(codes, Does.Not.Contain("PLAYER_OWNERSHIP_MISSING"));
            Assert.That(codes, Does.Not.Contain("CHARACTER_OWNERSHIP_MISSING"));
            Assert.That(codes, Does.Not.Contain("CONVERSATION_TARGET_DEFAULTED"));
        }

        [Test]
        public void DiagnoseConversation_ExplicitSelectionReportsOnlyChosenCharactersAsConnectable()
        {
            GameObject managerObject = new("[Convai Manager]");
            ConvaiManager manager = managerObject.AddComponent<ConvaiManager>();
            managerObject.AddComponent<ConvaiRoomManager>();
            new GameObject("Player").AddComponent<ConvaiPlayer>();
            ConvaiCharacter included = CreateConfiguredCharacter("Included NPC", CharacterId);
            ConvaiCharacter excluded = CreateConfiguredCharacter(
                "Excluded NPC", "8e9729c2-ed90-11f0-9d63-42010a7be027");

            var serialized = new SerializedObject(manager);
            serialized.FindProperty("_useCharacterConnectionSelection").boolValue = true;
            SerializedProperty includedCharacters = serialized.FindProperty("_includedCharacters");
            includedCharacters.arraySize = 1;
            includedCharacters.GetArrayElementAtIndex(0).objectReferenceValue = included;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest()));
            string[] codes = IssueCodes(response);
            JToken configuration = response["data"]?["configuration"];

            Assert.That(codes, Does.Not.Contain("CHARACTER_SELECTION_EMPTY"));
            Assert.That(codes, Does.Not.Contain("CONVERSATION_TARGET_DEFAULTED"));
            Assert.That(configuration?["connectableCharacterInstanceIds"]?.Values<long>(),
                Is.EqualTo(new[] { Id(included.gameObject) }));
            Assert.That(configuration?["characters"]?
                .Single(character => character.Value<long>("instanceId") == Id(excluded.gameObject))
                .Value<bool>("includedInRoom"), Is.False);
        }

        [Test]
        public void DiagnoseConversation_EmptyExplicitSelectionIsNotReadyToRun()
        {
            GameObject managerObject = new("[Convai Manager]");
            ConvaiManager manager = managerObject.AddComponent<ConvaiManager>();
            managerObject.AddComponent<ConvaiRoomManager>();
            new GameObject("Player").AddComponent<ConvaiPlayer>();
            CreateConfiguredCharacter("NPC", CharacterId);

            var serialized = new SerializedObject(manager);
            serialized.FindProperty("_useCharacterConnectionSelection").boolValue = true;
            serialized.FindProperty("_includedCharacters").arraySize = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest()));

            Assert.That(IssueCodes(response), Does.Contain("CHARACTER_SELECTION_EMPTY"));
            Assert.That(response["data"]?.Value<bool>("readyToRun"), Is.False);
        }

        [Test]
        public void DiagnoseConversation_IncludesCharactersFromAdditivelyLoadedScenes()
        {
            GameObject managerObject = new("[Convai Manager]");
            managerObject.AddComponent<ConvaiManager>();
            managerObject.AddComponent<ConvaiRoomManager>();
            new GameObject("Player").AddComponent<ConvaiPlayer>();
            CreateConfiguredCharacter("NPC A", CharacterId);

            _temporaryTestScenePath = AssetDatabase.GenerateUniqueAssetPath(
                TestAssetSandbox.Path(nameof(ConvaiMcpAuthoringTests), "LoadedScenesTest.unity"));
            Assert.That(EditorSceneManager.SaveScene(TestScene, _temporaryTestScenePath), Is.True);
            Scene additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                ConvaiCharacter additiveCharacter = CreateConfiguredCharacter(
                    "NPC B", "8e9729c2-ed90-11f0-9d63-42010a7be027");
                SceneManager.MoveGameObjectToScene(additiveCharacter.gameObject, additiveScene);
                SceneManager.SetActiveScene(TestScene);

                JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                    new ConvaiDiagnoseConversationRequest()));

                Assert.That(IssueCodes(response), Does.Contain("CONVERSATION_TARGET_DEFAULTED"));
                Assert.That(response["data"]?["configuration"]?.Value<int>("characterCount"), Is.EqualTo(2));
                Assert.That(response["data"]?["configuration"]?["connectableCharacterInstanceIds"]?.Count(),
                    Is.EqualTo(2));
            }
            finally
            {
                EditorSceneManager.CloseScene(additiveScene, true);
                SceneManager.SetActiveScene(TestScene);
            }
        }

        [Test]
        public void DiagnoseConversation_SceneInstallerExcludesCharacterOutsideExplicitBoundary()
        {
            GameObject managerObject = new("[Convai Manager]");
            ConvaiManager manager = managerObject.AddComponent<ConvaiManager>();
            managerObject.AddComponent<ConvaiRoomManager>();
            new GameObject("Player").AddComponent<ConvaiPlayer>();
            ConvaiCharacter owned = CreateConfiguredCharacter("Owned NPC", CharacterId);
            ConvaiCharacter outside = CreateConfiguredCharacter(
                "Outside NPC", "8e9729c2-ed90-11f0-9d63-42010a7be027");
            ConvaiSceneInstaller installer = new GameObject("Scene Installer").AddComponent<ConvaiSceneInstaller>();

            var installerSerialized = new SerializedObject(installer);
            SerializedProperty installerCharacters = installerSerialized.FindProperty("_characters");
            installerCharacters.arraySize = 1;
            installerCharacters.GetArrayElementAtIndex(0).objectReferenceValue = owned;
            installerSerialized.ApplyModifiedPropertiesWithoutUndo();

            var managerSerialized = new SerializedObject(manager);
            managerSerialized.FindProperty("_sceneInstaller").objectReferenceValue = installer;
            managerSerialized.FindProperty("_explicitConversationTarget").objectReferenceValue = owned;
            managerSerialized.ApplyModifiedPropertiesWithoutUndo();

            JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest()));
            string[] codes = IssueCodes(response);
            long[] connectableIds = response["data"]?["configuration"]?["connectableCharacterInstanceIds"]?
                .Values<long>().ToArray() ?? Array.Empty<long>();

            Assert.That(codes, Does.Not.Contain("CHARACTER_OWNERSHIP_MISSING"));
            Assert.That(connectableIds, Is.EquivalentTo(new[] { Id(owned.gameObject) }));
            CollectionAssert.DoesNotContain(connectableIds, Id(outside.gameObject));
        }

        [Test]
        public void DiagnoseConversation_ReportsIncompleteVideoPipeline()
        {
            GameObject managerObject = new("[Convai Manager]");
            ConvaiManager manager = managerObject.AddComponent<ConvaiManager>();
            ConvaiRoomManager room = managerObject.AddComponent<ConvaiRoomManager>();
            GameObject playerObject = new("Player");
            ConvaiPlayer player = playerObject.AddComponent<ConvaiPlayer>();
            ConvaiCharacter character = CreateConfiguredCharacter("NPC", CharacterId);
            Bind(manager, player, character);
            var serialized = new SerializedObject(room);
            serialized.FindProperty("_connectionType").intValue = (int)ConvaiConnectionType.Video;
            serialized.FindProperty("_visionContextMode").intValue = (int)ConvaiVisionContextMode.Auto;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest()));

            Assert.That(IssueCodes(response), Does.Contain("VIDEO_PIPELINE_INCOMPLETE"));
        }

        [Test]
        public void DiagnoseConversation_ReturnsStableMissingFoundationCodes()
        {
            JObject response = Json(ConvaiMcpTools.DiagnoseConversation(
                new ConvaiDiagnoseConversationRequest()));
            string[] codes = response["data"]?["issues"]?
                .Select(issue => issue.Value<string>("code"))
                .ToArray();

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(codes, Does.Contain("MANAGER_MISSING"));
            Assert.That(codes, Does.Contain("ROOM_MISSING"));
            Assert.That(codes, Does.Contain("PLAYER_MISSING"));
            Assert.That(codes, Does.Contain("CHARACTER_MISSING"));
        }

        private static JObject Json(object value) => JObject.FromObject(value);

        private static long Id(Object value) => ConvaiMcpEntityRef.ToToolId(value);

        private static string[] IssueCodes(JObject response) => response["data"]?["issues"]?
            .Select(issue => issue.Value<string>("code"))
            .ToArray() ?? Array.Empty<string>();

        private static string IssueSeverity(JObject response, string code) => response["data"]?["issues"]?
            .FirstOrDefault(issue => issue.Value<string>("code") == code)?
            .Value<string>("severity");

        private static Object ReadReference(ConvaiManager manager, string propertyName) =>
            new SerializedObject(manager).FindProperty(propertyName).objectReferenceValue;

        private static Object[] ReadReferences(ConvaiManager manager, string propertyName)
        {
            SerializedProperty values = new SerializedObject(manager).FindProperty(propertyName);
            return Enumerable.Range(0, values.arraySize)
                .Select(index => values.GetArrayElementAtIndex(index).objectReferenceValue)
                .ToArray();
        }

        private static ConvaiCharacter CreateConfiguredCharacter(string name, string characterId)
        {
            GameObject target = new(name);
            ConvaiCharacter character = target.AddComponent<ConvaiCharacter>();
            target.AddComponent<AudioSource>();
            target.AddComponent<ConvaiAudioOutput>();
            var serialized = new SerializedObject(character);
            serialized.FindProperty("_characterId").stringValue = characterId;
            serialized.FindProperty("_characterName").stringValue = name;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return character;
        }

        private static void Bind(ConvaiManager manager, ConvaiPlayer player, ConvaiCharacter character)
        {
            var serialized = new SerializedObject(manager);
            serialized.FindProperty("_explicitPlayer").objectReferenceValue = player;
            SerializedProperty characters = serialized.FindProperty("_explicitCharacters");
            characters.arraySize = 1;
            characters.GetArrayElementAtIndex(0).objectReferenceValue = character;
            serialized.FindProperty("_explicitConversationTarget").objectReferenceValue = character;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
