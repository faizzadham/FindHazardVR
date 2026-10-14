using System.Collections.Generic;
using System.Linq;
using Convai.Editor.AI;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Conversation;
using Convai.Tests.EditMode.Fixtures;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.AI
{
    /// <summary>
    ///     Covers <c>Convai.ConfigureConversationTargeting</c>: what it writes, what it refuses, and — the
    ///     part a tuning tool lives or dies by — what it leaves alone.
    /// </summary>
    /// <remarks>
    ///     The omission cases are the ones worth having. A tool that silently rewrites the four
    ///     targeting numbers to their defaults every time an assistant changes the mode would be
    ///     indistinguishable from a working one until somebody noticed their tuned scene had been
    ///     reset, and no response field would say so.
    /// </remarks>
    public sealed class ConvaiMultiCharacterAuthoringTests
    {
        private ConvaiMcpSceneFixture _sceneFixture;
        private ConvaiManager _manager;
        private ConvaiRoomManager _room;
        private ConvaiCharacter _sofia;
        private ConvaiCharacter _james;
        private string _temporaryTestScenePath;

        [SetUp]
        public void SetUp()
        {
            _sceneFixture = ConvaiMcpSceneFixture.Begin();
            _sceneFixture.IsolateScenePopulation<ConvaiCharacter>();
            _sceneFixture.IsolateScenePopulation<Camera>();

            var managerObject = new GameObject("[Convai Manager]");
            _manager = managerObject.AddComponent<ConvaiManager>();
            _room = managerObject.AddComponent<ConvaiRoomManager>();

            _sofia = new GameObject("Sofia").AddComponent<ConvaiCharacter>();
            _james = new GameObject("James").AddComponent<ConvaiCharacter>();
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

            TestAssetSandbox.Clear(nameof(ConvaiMultiCharacterAuthoringTests));
        }

        [Test]
        public void Configure_DryRunReportsTheChangeWithoutWritingIt()
        {
            JObject response = Configure(new ConvaiConfigureConversationTargetingRequest
            {
                TargetingMode = ConversationTargetingMode.Proximity,
                DryRun = true
            });

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?["changes"]?.Values<string>(), Does.Contain("Chosen By: Proximity"));
            Assert.That(
                _manager.ConversationTargeting.Mode,
                Is.EqualTo(ConversationTargetingMode.LookAt),
                "A dry run must not touch the scene.");
        }

        [Test]
        public void Configure_AppliesOnlyTheValuesTheRequestNamed()
        {
            Configure(new ConvaiConfigureConversationTargetingRequest
            {
                SwitchMargin = 18f,
                DryRun = false
            });

            ConversationTargetingOptions defaults = ConversationTargetingOptions.CreateDefault();
            Assert.That(_manager.ConversationTargeting.SwitchMargin, Is.EqualTo(18f).Within(0.001f));
            Assert.That(
                _manager.ConversationTargeting.MaxAngle,
                Is.EqualTo(defaults.MaxAngle).Within(0.001f),
                "An omitted value must be left exactly as the project authored it.");
            Assert.That(
                _manager.ConversationTargeting.SwitchDelaySeconds,
                Is.EqualTo(defaults.SwitchDelaySeconds).Within(0.001f));
            Assert.That(_manager.ConversationTargeting.Mode, Is.EqualTo(defaults.Mode));
        }

        [Test]
        public void Configure_ReportsNoChangesWhenEveryValueAlreadyMatches()
        {
            ConversationTargetingOptions defaults = ConversationTargetingOptions.CreateDefault();

            JObject response = Configure(new ConvaiConfigureConversationTargetingRequest
            {
                TargetingMode = defaults.Mode,
                MaxAngle = defaults.MaxAngle,
                DryRun = false
            });

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?["changes"]?.Values<string>(), Is.Empty);
        }

        [Test]
        public void Configure_RefusesAnAngleOutsideItsRangeAndWritesNothing()
        {
            JObject response = Configure(new ConvaiConfigureConversationTargetingRequest
            {
                TargetingMode = ConversationTargetingMode.Proximity,
                MaxAngle = 400f,
                DryRun = false
            });

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("INVALID_MAX_ANGLE"));
            Assert.That(
                _manager.ConversationTargeting.Mode,
                Is.EqualTo(ConversationTargetingMode.LookAt),
                "A refused request must not apply the half of itself that was valid.");
        }

        [Test]
        public void Configure_RoomSelectionIsTheExactSet()
        {
            Configure(new ConvaiConfigureConversationTargetingRequest
            {
                IncludedCharacterInstanceIds = new[] { Id(_sofia.gameObject) },
                DryRun = false
            });

            Assert.That(_manager.UsesCharacterConnectionSelection, Is.True);
            Assert.That(_manager.CharactersToConnect, Does.Contain(_sofia));
            Assert.That(
                _manager.CharactersToConnect,
                Has.No.Member(_james),
                "A character left out of the set is excluded, not merely not-added.");
        }

        [Test]
        public void Configure_IncludeAllReturnsTheManagerToTheShippedDefault()
        {
            Configure(new ConvaiConfigureConversationTargetingRequest
            {
                IncludedCharacterInstanceIds = new[] { Id(_sofia.gameObject) },
                DryRun = false
            });

            Configure(new ConvaiConfigureConversationTargetingRequest
            {
                IncludeAllCharacters = true,
                DryRun = false
            });

            Assert.That(_manager.UsesCharacterConnectionSelection, Is.False);
            Assert.That(_manager.CharactersToConnect, Is.Empty);
        }

        [Test]
        public void Configure_RefusesTwoContradictoryRoomSelections()
        {
            JObject response = Configure(new ConvaiConfigureConversationTargetingRequest
            {
                IncludeAllCharacters = true,
                IncludedCharacterInstanceIds = new[] { Id(_sofia.gameObject) },
                DryRun = false
            });

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("CONFLICTING_ROOM_SELECTION"));
        }

        [Test]
        public void Configure_RefusesARoomWithNobodyInIt()
        {
            JObject response = Configure(new ConvaiConfigureConversationTargetingRequest
            {
                IncludedCharacterInstanceIds = new long[0],
                DryRun = false
            });

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("EMPTY_ROOM_SELECTION"));
        }

        [Test]
        public void Configure_SetsAndClearsTheInitialCharacter()
        {
            Configure(new ConvaiConfigureConversationTargetingRequest
            {
                InitialCharacterInstanceId = Id(_james.gameObject),
                DryRun = false
            });
            Assert.That(_manager.InitialCharacter, Is.EqualTo(_james));

            Configure(new ConvaiConfigureConversationTargetingRequest
            {
                ClearInitialCharacter = true,
                DryRun = false
            });
            Assert.That(_manager.InitialCharacter, Is.Null);
        }

        [Test]
        public void Configure_RefusesAnInstanceIdThatIsNotACharacter()
        {
            JObject response = Configure(new ConvaiConfigureConversationTargetingRequest
            {
                InitialCharacterInstanceId = Id(_manager.gameObject),
                DryRun = false
            });

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("INITIAL_CHARACTER_NOT_FOUND"));
        }

        [Test]
        public void Configure_WarnsThatARoomWithSeveralCharactersNeedsAccountAccess()
        {
            JObject response = Configure(new ConvaiConfigureConversationTargetingRequest { DryRun = true });

            Assert.That(
                response["data"]?["warnings"]?.Values<string>().Any(warning =>
                    warning.Contains("account feature")),
                Is.True,
                "The one thing that stops a correct scene from working is not visible anywhere else " +
                "before Play Mode.");
        }

        [Test]
        public void SetConversationTarget_DryRunDoesNotChangePlayModeOrContactARoom()
        {
            JObject response = JObject.FromObject(ConvaiMcpTools.SetConversationTarget(
                new ConvaiSetConversationTargetRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject),
                    CharacterInstanceId = Id(_sofia.gameObject),
                    DryRun = true
                }).GetAwaiter().GetResult());

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("executed"), Is.False);
            Assert.That(response["data"]?.Value<bool>("requiresPlayMode"), Is.True);
            Assert.That(EditorApplication.isPlaying, Is.False);
        }

        [Test]
        public void SetConversationTarget_ExecutionRequiresPlayMode()
        {
            JObject response = JObject.FromObject(ConvaiMcpTools.SetConversationTarget(
                new ConvaiSetConversationTargetRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject),
                    CharacterInstanceId = Id(_sofia.gameObject),
                    DryRun = false
                }).GetAwaiter().GetResult());

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("PLAY_MODE_REQUIRED"));
            Assert.That(EditorApplication.isPlaying, Is.False);
        }

        [Test]
        public void SetConversationTarget_RequiresACharacter()
        {
            JObject response = JObject.FromObject(ConvaiMcpTools.SetConversationTarget(
                new ConvaiSetConversationTargetRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject),
                    CharacterInstanceId = 0
                }).GetAwaiter().GetResult());

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("CHARACTER_REQUIRED"));
        }

        [Test]
        public void UpdateCharacterRoster_DryRunReportsOperationWithoutContactingARoom()
        {
            JObject response = JObject.FromObject(ConvaiMcpTools.UpdateCharacterRoster(
                new ConvaiUpdateCharacterRosterRequest
                {
                    Operation = ConvaiCharacterRosterOperation.Add,
                    RoomManagerInstanceId = Id(_room.gameObject),
                    CharacterInstanceId = Id(_sofia.gameObject),
                    DryRun = true
                }).GetAwaiter().GetResult());

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<bool>("executed"), Is.False);
            Assert.That(response["data"]?.Value<string>("operation"), Is.EqualTo("Add"));
            Assert.That(response["data"]?.Value<bool>("requiresPlayMode"), Is.True);
        }

        [Test]
        public void UpdateCharacterRoster_ReplacementIsRemoveOnly()
        {
            JObject response = JObject.FromObject(ConvaiMcpTools.UpdateCharacterRoster(
                new ConvaiUpdateCharacterRosterRequest
                {
                    Operation = ConvaiCharacterRosterOperation.Add,
                    CharacterInstanceId = Id(_sofia.gameObject),
                    ReplacementTargetCharacterInstanceId = Id(_james.gameObject)
                }).GetAwaiter().GetResult());

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("REPLACEMENT_ONLY_FOR_REMOVE"));
        }

        [Test]
        public void UpdateCharacterRoster_RejectsUnknownOperations()
        {
            JObject response = JObject.FromObject(ConvaiMcpTools.UpdateCharacterRoster(
                new ConvaiUpdateCharacterRosterRequest
                {
                    Operation = (ConvaiCharacterRosterOperation)99,
                    RoomManagerInstanceId = Id(_room.gameObject),
                    CharacterInstanceId = Id(_sofia.gameObject),
                    DryRun = true
                }).GetAwaiter().GetResult());

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("INVALID_ROSTER_OPERATION"));
        }

        [Test]
        public void SetupMultiCharacterRoster_DryRunValidatesAndDoesNotWrite()
        {
            SetCharacterId(_sofia, "sofia-id");
            SetCharacterId(_james, "james-id");

            JObject response = JObject.FromObject(ConvaiMcpTools.SetupMultiCharacterRoster(
                new ConvaiSetupMultiCharacterRosterRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject),
                    CharacterInstanceIds = new[] { Id(_sofia.gameObject), Id(_james.gameObject) },
                    InitialCharacterInstanceId = Id(_james.gameObject),
                    DryRun = true
                }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?["characterInstanceIds"]?.Count(), Is.EqualTo(2));
            Assert.That(_manager.UsesCharacterConnectionSelection, Is.False);
            Assert.That(_manager.InitialCharacter, Is.Null);
        }

        [Test]
        public void SetupMultiCharacterRoster_RefusesDuplicateCharacterIds()
        {
            SetCharacterId(_sofia, "same-id");
            SetCharacterId(_james, "same-id");

            JObject response = JObject.FromObject(ConvaiMcpTools.SetupMultiCharacterRoster(
                new ConvaiSetupMultiCharacterRosterRequest
                {
                    CharacterInstanceIds = new[] { Id(_sofia.gameObject), Id(_james.gameObject) }
                }));

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("DUPLICATE_CHARACTER_ID"));
        }

        [Test]
        public void SetupMultiCharacterRoster_AcceptsACharacterFromAnotherLoadedScene()
        {
            SetCharacterId(_sofia, "sofia-id");
            SetCharacterId(_james, "james-id");
            EnsureTestSceneCanLoadAdditiveScenes();
            Scene additive = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.MoveGameObjectToScene(_james.gameObject, additive);
                JObject response = JObject.FromObject(ConvaiMcpTools.SetupMultiCharacterRoster(
                    new ConvaiSetupMultiCharacterRosterRequest
                    {
                        ManagerInstanceId = Id(_manager.gameObject),
                        CharacterInstanceIds = new[] { Id(_sofia.gameObject), Id(_james.gameObject) },
                        DryRun = true
                    }));

                Assert.That(response.Value<bool>("success"), Is.True);
            }
            finally
            {
                if (additive.IsValid() && additive.isLoaded)
                    EditorSceneManager.CloseScene(additive, true);
            }
        }

        [Test]
        public void LiveToolPreviewsAcceptManagerAndRoomFromAnotherLoadedScene()
        {
            EnsureTestSceneCanLoadAdditiveScenes();
            Scene additive = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.MoveGameObjectToScene(_manager.gameObject, additive);
                JObject target = JObject.FromObject(ConvaiMcpTools.SetConversationTarget(
                    new ConvaiSetConversationTargetRequest
                    {
                        ManagerInstanceId = Id(_manager.gameObject),
                        CharacterInstanceId = Id(_sofia.gameObject),
                        DryRun = true
                    }).GetAwaiter().GetResult());
                JObject roster = JObject.FromObject(ConvaiMcpTools.UpdateCharacterRoster(
                    new ConvaiUpdateCharacterRosterRequest
                    {
                        Operation = ConvaiCharacterRosterOperation.Add,
                        RoomManagerInstanceId = Id(_room.gameObject),
                        CharacterInstanceId = Id(_sofia.gameObject),
                        DryRun = true
                    }).GetAwaiter().GetResult());

                Assert.That(target.Value<bool>("success"), Is.True);
                Assert.That(roster.Value<bool>("success"), Is.True);
            }
            finally
            {
                if (additive.IsValid() && additive.isLoaded)
                    EditorSceneManager.CloseScene(additive, true);
            }
        }

        [Test]
        public void SimulateConversationTargeting_ReportsTheBestEligibleCharacterWithoutMutation()
        {
            var camera = new GameObject("Targeting Camera").AddComponent<Camera>();
            camera.transform.position = Vector3.zero;
            camera.transform.forward = Vector3.forward;
            _sofia.transform.position = new Vector3(0f, 0f, 5f);
            _james.transform.position = new Vector3(8f, 0f, 5f);

            JObject response = JObject.FromObject(ConvaiMcpTools.SimulateConversationTargeting(
                new ConvaiSimulateConversationTargetingRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject),
                    ViewCameraInstanceId = Id(camera.gameObject)
                }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(
                response["data"]?.Value<long>("geometricProposalTargetInstanceId"),
                Is.EqualTo(Id(_sofia.gameObject)));
            Assert.That(response["data"]?.Value<bool>("temporalSwitchPolicyApplied"), Is.False);
            Assert.That(response["data"]?["wouldSwitch"], Is.Null);
            Assert.That(response["data"]?["candidates"]?.Count(), Is.EqualTo(2));
        }

        [Test]
        public void SimulateConversationTargeting_FallsBackToTheOwnedPlayerTransform()
        {
            var player = new GameObject("Targeting Player").AddComponent<ConvaiPlayer>();
            player.transform.forward = Vector3.forward;
            _manager.SetExplicitPlayer(player);
            _sofia.transform.position = new Vector3(0f, 0f, 5f);
            _james.transform.position = new Vector3(8f, 0f, 5f);

            JObject response = JObject.FromObject(ConvaiMcpTools.SimulateConversationTargeting(
                new ConvaiSimulateConversationTargetingRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject)
                }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<string>("viewSource"), Is.EqualTo("Player"));
            Assert.That(response["data"]?.Value<long>("viewTransformInstanceId"),
                Is.EqualTo(Id(player.gameObject)));
            Assert.That(response["data"]?.Value<long>("geometricProposalTargetInstanceId"),
                Is.EqualTo(Id(_sofia.gameObject)));
        }

        [Test]
        public void SimulateConversationTargeting_UsesLiveFallbackWhenCurrentTargetIsIneligible()
        {
            var camera = new GameObject("Targeting Camera").AddComponent<Camera>();
            camera.transform.forward = Vector3.forward;
            _sofia.transform.position = new Vector3(100f, 0f, 0f);
            _james.transform.position = new Vector3(0f, 0f, 5f);
            _manager.SetExplicitCharacters(new[] { _sofia, _james });
            _manager.SetCharactersToConnect(new[] { _sofia });
            _manager.SetInitialCharacter(_james);

            JObject response = JObject.FromObject(ConvaiMcpTools.SimulateConversationTargeting(
                new ConvaiSimulateConversationTargetingRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject),
                    ViewCameraInstanceId = Id(camera.gameObject)
                }));

            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(response["data"]?.Value<long>("geometricProposalTargetInstanceId"), Is.Zero);
            Assert.That(response["data"]?.Value<long>("proposedTargetInstanceId"),
                Is.EqualTo(Id(_sofia.gameObject)));
            Assert.That(response["data"]?.Value<bool>("recoveryFallbackApplied"), Is.True);
        }

        [Test]
        public void SimulateConversationTargeting_RecoveryFallbackUsesRoomMembershipOrder()
        {
            long result = ConvaiMcpTools.ResolveRecoveryFallbackId(
                new[] { Id(_james.gameObject), Id(_sofia.gameObject) },
                new[] { Id(_sofia.gameObject), Id(_james.gameObject) });

            Assert.That(result, Is.EqualTo(Id(_james.gameObject)),
                "The live fallback follows authoritative room order, not scene discovery order.");
        }

        [Test]
        public void SimulateConversationTargeting_LiveCandidatesIncludeDynamicallyAddedRoomMembers()
        {
            var camila = new GameObject("Camila").AddComponent<ConvaiCharacter>();
            IReadOnlyList<ConvaiCharacter> result = ConvaiMcpTools.ResolveSimulationCharacterSource(
                hasLiveSession: true,
                roomCharacters: new[] { _sofia, camila },
                ownedCharacters: new[] { _sofia });

            Assert.That(result, Is.EqualTo(new[] { _sofia, camila }),
                "A member added to the live room must not be omitted by the stale authored ownership list.");
        }

        [Test]
        public void SimulateConversationTargeting_AcceptsAManagerFromAnotherLoadedScene()
        {
            var camera = new GameObject("Targeting Camera").AddComponent<Camera>();
            camera.transform.forward = Vector3.forward;
            _sofia.transform.position = new Vector3(0f, 0f, 5f);
            EnsureTestSceneCanLoadAdditiveScenes();
            Scene additive = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.MoveGameObjectToScene(_manager.gameObject, additive);
                JObject response = JObject.FromObject(ConvaiMcpTools.SimulateConversationTargeting(
                    new ConvaiSimulateConversationTargetingRequest
                    {
                        ManagerInstanceId = Id(_manager.gameObject),
                        ViewCameraInstanceId = Id(camera.gameObject)
                    }));

                Assert.That(response.Value<bool>("success"), Is.True);
                Assert.That(response["data"]?.Value<long>("managerInstanceId"),
                    Is.EqualTo(Id(_manager.gameObject)));
            }
            finally
            {
                if (additive.IsValid() && additive.isLoaded)
                    EditorSceneManager.CloseScene(additive, true);
            }
        }

        [Test]
        public void WaitForCharacterReady_DoesNotEnterPlayMode()
        {
            JObject response = JObject.FromObject(ConvaiMcpTools.WaitForCharacterReady(
                new ConvaiWaitForCharacterReadyRequest
                {
                    RoomManagerInstanceId = Id(_room.gameObject),
                    CharacterInstanceId = Id(_sofia.gameObject)
                }).GetAwaiter().GetResult());

            Assert.That(response.Value<bool>("success"), Is.False);
            Assert.That(FailureCode(response), Is.EqualTo("PLAY_MODE_REQUIRED"));
            Assert.That(EditorApplication.isPlaying, Is.False);
        }

        [Test]
        public void WaitForMultiCharacterState_AllowsSnapshotRequestsAndDoesNotEnterPlayMode()
        {
            JObject snapshot = JObject.FromObject(ConvaiMcpTools.WaitForMultiCharacterState(
                new ConvaiWaitForMultiCharacterStateRequest { ManagerInstanceId = Id(_manager.gameObject) })
                .GetAwaiter().GetResult());
            Assert.That(FailureCode(snapshot), Is.EqualTo("PLAY_MODE_REQUIRED"));

            JObject response = JObject.FromObject(ConvaiMcpTools.WaitForMultiCharacterState(
                new ConvaiWaitForMultiCharacterStateRequest
                {
                    ManagerInstanceId = Id(_manager.gameObject),
                    ExpectedRosterSize = 2
                }).GetAwaiter().GetResult());
            Assert.That(FailureCode(response), Is.EqualTo("PLAY_MODE_REQUIRED"));
            Assert.That(EditorApplication.isPlaying, Is.False);
        }

        private static JObject Configure(ConvaiConfigureConversationTargetingRequest request) =>
            JObject.FromObject(ConvaiMcpTools.ConfigureConversationTargeting(request));

        private static string FailureCode(JObject response) => response["data"]?.Value<string>("code");

        private static long Id(Object value) => ConvaiMcpEntityRef.ToToolId(value);

        private static void SetCharacterId(ConvaiCharacter character, string characterId)
        {
            var serialized = new SerializedObject(character);
            serialized.FindProperty("_characterId").stringValue = characterId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private void EnsureTestSceneCanLoadAdditiveScenes()
        {
            if (!string.IsNullOrEmpty(_sceneFixture.TestScene.path)) return;

            _temporaryTestScenePath = AssetDatabase.GenerateUniqueAssetPath(
                TestAssetSandbox.Path(nameof(ConvaiMultiCharacterAuthoringTests), "LoadedScenesTest.unity"));
            Assert.That(
                EditorSceneManager.SaveScene(_sceneFixture.TestScene, _temporaryTestScenePath),
                Is.True);
        }
    }
}
