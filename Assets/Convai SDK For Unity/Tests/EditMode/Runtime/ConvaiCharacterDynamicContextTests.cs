using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Runtime.Components;
using Convai.Runtime.Actions;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Runtime;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Presentation.DynamicContext;
using Convai.Shared.Actions;
using Convai.Tests.EditMode.Mocks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiCharacterDynamicContextTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private MockRoomAudioService _audioService;
        private MockRoomConnectionService _connectionService;
        private EventHub _eventHub;
        private MockAgentRegistry _agentRegistry;
        private TestLogger _logger;

        [SetUp]
        public void SetUp()
        {
            _eventHub = new EventHub(new ImmediateScheduler());
            _connectionService = new MockRoomConnectionService();
            _audioService = new MockRoomAudioService();
            _agentRegistry = new MockAgentRegistry();
            _logger = new TestLogger();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);

            _createdObjects.Clear();
        }

        [Test]
        public void SetState_NewStateWhileReady_AppendsTrackedState()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);

            character.DynamicContext.SetState("Health", "100");

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Health is 100",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Silent);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void SetState_ChangedStateWhileReady_CoalescesToSingleReplaceOnFlush()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            character.DynamicContext.SetState("Health", "100");
            character.DynamicContext.Flush();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.SetState("Health", "50", ConvaiRespondMode.MustRespond);

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Health is 50\nHealth changed from 100 to 50",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.MustRespond);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void SetStates_MixedBatchWhileReady_CoalescesToSingleReplaceOnFlush()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            character.DynamicContext.SetState("Health", "100");
            character.DynamicContext.Flush();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.SetStates(new Dictionary<string, string>
            {
                ["Health"] = "50",
                ["Ammo"] = "6"
            }, ConvaiRespondMode.MustRespond);

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Health is 50\nHealth changed from 100 to 50\nAmmo is 6",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.MustRespond);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void AddEvent_WhileReady_AppendsRawEvent()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);

            character.DynamicContext.AddEvent("Door opened");

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Door opened",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Auto);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void BatchedUpdates_StateLastWins_EventDedupes_AttentionLastWins_AndReactionAggregates()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "door", "lever");
            MakeReady(character);

            character.DynamicContext.SetState("Health", "100", ConvaiRespondMode.Silent);
            character.DynamicContext.SetState("Health", "80", ConvaiRespondMode.Auto);
            character.DynamicContext.AddEvent("Door opened", ConvaiRespondMode.Auto);
            character.DynamicContext.AddEvent("Door opened", ConvaiRespondMode.Auto);
            character.DynamicContext.SetCurrentAttentionObject("door", ConvaiRespondMode.Silent);
            character.DynamicContext.SetCurrentAttentionObject("lever", ConvaiRespondMode.MustRespond);

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            ConvaiDynamicContextUpdate update = _connectionService.SentDynamicContextUpdates[0];
            AssertUpdate(
                update,
                "Door opened\nHealth is 80",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.MustRespond);
            Assert.AreEqual("lever", update.CurrentAttentionObject);
            AssertHasUpdateId(update);
        }

        [Test]
        public void SetCurrentAttentionObject_WithoutActionConfigObjects_DoesNotSendUpdate()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);

            character.DynamicContext.SetCurrentAttentionObject("lever");
            character.DynamicContext.Flush();

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);
        }

        [Test]
        public void RemoveState_WhileReady_SendsCanonicalReplace()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            character.DynamicContext.SetState("Health", "100");
            character.DynamicContext.Flush();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.RemoveState("Health");

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                string.Empty,
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Silent);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void Reset_WhileReady_SendsReset()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            character.DynamicContext.SetState("Health", "100");
            character.DynamicContext.Flush();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.Reset(removeStatic: true);

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                null,
                ConvaiContextUpdateMode.Reset,
                ConvaiRespondMode.Silent);
            Assert.IsTrue(_connectionService.SentDynamicContextUpdates[0].RemoveStatic);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void GeneratedUpdates_UseUniqueUpdateIds()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);

            character.DynamicContext.SetState("Health", "100");
            character.DynamicContext.Flush();
            string firstUpdateId = _connectionService.SentDynamicContextUpdates[0].UpdateId;

            character.DynamicContext.SetState("Health", "50");
            character.DynamicContext.Flush();
            string secondUpdateId = _connectionService.SentDynamicContextUpdates[1].UpdateId;

            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[1]);
            Assert.AreNotEqual(firstUpdateId, secondUpdateId);
        }

        [Test]
        public void PreReadyTrackedUpdates_FlushOneCanonicalReplace_OnCharacterReady()
        {
            ConvaiCharacter character = CreateCharacter();

            character.DynamicContext.SetState("Health", "100");
            character.DynamicContext.AddEvent("Door opened");

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            MakeReady(character);

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Door opened\nHealth is 100",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Auto);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void PreReadyReset_FlushesDeferredReset_OnCharacterReady()
        {
            ConvaiCharacter character = CreateCharacter();

            character.DynamicContext.SetState("Health", "100");
            character.DynamicContext.Reset();

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            MakeReady(character);

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                null,
                ConvaiContextUpdateMode.Reset,
                ConvaiRespondMode.Silent);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void DisconnectReconnect_ReflushesCanonicalTrackedContext()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            character.DynamicContext.SetState("Health", "100");
            _connectionService.SentDynamicContextUpdates.Clear();

            _connectionService.RaiseConnectionFailed();
            _connectionService.RaiseConnected();
            _eventHub.Publish(CharacterReady.Create(character.CharacterId, $"participant-{character.CharacterId}"));

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Health is 100",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Silent);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void PendingSync_WithEmptyCanonicalContext_SendsEmptyReplace_OnReconnect()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            character.DynamicContext.SetState("Health", "100");
            _connectionService.SentDynamicContextUpdates.Clear();

            _connectionService.RaiseConnectionFailed();
            character.DynamicContext.RemoveState("Health");

            _connectionService.RaiseConnected();
            _eventHub.Publish(CharacterReady.Create(character.CharacterId, $"participant-{character.CharacterId}"));

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                string.Empty,
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Silent);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        [Test]
        public void RawApply_WhenNotInConversation_DoesNotSendOrQueue()
        {
            ConvaiCharacter character = CreateCharacter();

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate("raw update"));

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);

            MakeReady(character);

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);
        }

        [Test]
        public void RawApply_WhenReady_SendsTypedUpdate()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                "full raw context",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.MustRespond));

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "full raw context",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.MustRespond);
        }

        [Test]
        public void RawApply_ActionConfigObjectPatch_PreservesExistingActions()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "cube");
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                null,
                reaction: ConvaiRespondMode.Silent,
                actionConfig: new ConvaiActionConfigPatch
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "lever" }
                    }
                }));

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.AreEqual("cube", character.ActionConfig.Objects[0].Name, "Send must not mutate confirmed state.");

            PublishActionAck(_connectionService.SentDynamicContextUpdates[0], 1, 1, 0, null);

            ConvaiActionConfig runtimeConfig = character.ActionConfig;
            Assert.AreEqual(1, runtimeConfig.Actions.Count);
            Assert.AreEqual("Move To", runtimeConfig.Actions[0]);
            Assert.AreEqual(1, runtimeConfig.Objects.Count);
            Assert.AreEqual("lever", runtimeConfig.Objects[0].Name);
        }

        [Test]
        public void RawApply_ActionConfigAttentionPatch_ValidatesAgainstActiveObjects()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "lever");
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                null,
                reaction: ConvaiRespondMode.Silent,
                actionConfig: new ConvaiActionConfigPatch
                {
                    CurrentAttentionObject = "lever"
                }));

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.IsNull(character.ActionConfig.CurrentAttentionObject, "Send must not mutate confirmed state.");

            PublishActionAck(_connectionService.SentDynamicContextUpdates[0], 1, 1, 0, "lever");

            ConvaiActionConfig runtimeConfig = character.ActionConfig;
            Assert.AreEqual("lever", runtimeConfig.CurrentAttentionObject);
            Assert.AreEqual(1, runtimeConfig.Actions.Count);
            Assert.AreEqual("Move To", runtimeConfig.Actions[0]);
            Assert.AreEqual(1, runtimeConfig.Objects.Count);
            Assert.AreEqual("lever", runtimeConfig.Objects[0].Name);
        }

        [Test]
        public void RawApply_ActionConfigExplicitEmptyActions_ClearsActiveActions()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "cube");
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                null,
                reaction: ConvaiRespondMode.Silent,
                actionConfig: new ConvaiActionConfigPatch
                {
                    Actions = new List<string>()
                }));

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.AreEqual(1, character.ActionConfig.Actions.Count, "Send must not mutate confirmed state.");

            PublishActionAck(_connectionService.SentDynamicContextUpdates[0], 0, 1, 0, null);

            ConvaiActionConfig runtimeConfig = character.ActionConfig;
            Assert.AreEqual(0, runtimeConfig.Actions.Count);
            Assert.AreEqual(0, character.ActionDefinitions.Count, "Public definitions must follow the confirmed request-level subset.");
            Assert.AreEqual(1, character.GetRuntimeActionDefinitionCatalog().Count, "Safety catalog must retain locally executable fallback definitions.");
            Assert.AreEqual(1, runtimeConfig.Objects.Count);
            Assert.AreEqual("cube", runtimeConfig.Objects[0].Name);
        }

        [Test]
        public void ActionConfigPatchReconciler_PreservesOmittedFieldsAndReplacesOrClearsProvidedFields()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            var player = new GameObject("Player binding");
            _createdObjects.Add(player);
            var current = new ConvaiActionConfig
            {
                Actions = new List<string> { "Move To" },
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Cube", Description = " old cube ", GameObjectReference = character.gameObject }
                },
                Characters = new List<ConvaiActionCharacterDefinition>
                {
                    new() { Name = "Player", Bio = " old bio ", GameObjectReference = player }
                },
                CurrentAttentionObject = "Cube"
            };

            bool rebound = ConvaiActionConfigPatchReconciler.TryReconcile(
                current,
                new ConvaiActionConfigPatch
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new()
                        {
                            Name = "  cube  ",
                            Description = "  updated cube  "
                        }
                    }
                },
                null,
                character.GetRuntimeActionDefinitionCatalog(),
                out ConvaiActionConfigReconciliation reboundResult,
                out string reboundError);

            Assert.IsTrue(rebound, reboundError);
            CollectionAssert.AreEqual(new[] { "Move To" }, reboundResult.Snapshot.Actions);
            Assert.AreEqual("cube", reboundResult.Snapshot.Objects[0].Name);
            Assert.AreEqual("updated cube", reboundResult.Snapshot.Objects[0].Description);
            Assert.AreSame(character.gameObject, reboundResult.Snapshot.Objects[0].GameObjectReference);
            Assert.AreEqual("Player", reboundResult.Snapshot.Characters[0].Name);
            Assert.AreEqual("old bio", reboundResult.Snapshot.Characters[0].Bio);
            Assert.AreSame(player, reboundResult.Snapshot.Characters[0].GameObjectReference);
            Assert.AreEqual("cube", reboundResult.Snapshot.CurrentAttentionObject);
            Assert.IsNull(reboundResult.Patch.Actions, "Omitted actions must stay omitted on the wire patch.");
            Assert.IsNull(reboundResult.Patch.Characters, "Omitted characters must stay omitted on the wire patch.");

            bool replaced = ConvaiActionConfigPatchReconciler.TryReconcile(
                reboundResult.Snapshot,
                new ConvaiActionConfigPatch
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new()
                        {
                            Name = "  Lever  ",
                            Description = "  metal lever  ",
                            GameObjectReference = character.gameObject
                        }
                    }
                },
                null,
                character.GetRuntimeActionDefinitionCatalog(),
                out ConvaiActionConfigReconciliation first,
                out string firstError);

            Assert.IsTrue(replaced, firstError);
            CollectionAssert.AreEqual(new[] { "Move To" }, first.Snapshot.Actions);
            Assert.AreEqual("Lever", first.Snapshot.Objects[0].Name);
            Assert.AreEqual("metal lever", first.Snapshot.Objects[0].Description);
            Assert.AreSame(character.gameObject, first.Snapshot.Objects[0].GameObjectReference);
            Assert.AreEqual("Player", first.Snapshot.Characters[0].Name);
            Assert.AreSame(player, first.Snapshot.Characters[0].GameObjectReference);
            Assert.IsNull(first.Snapshot.CurrentAttentionObject, "Replacing objects must clear stale attention.");
            Assert.IsNull(first.Patch.Actions, "Omitted actions must stay omitted on the wire patch.");

            bool cleared = ConvaiActionConfigPatchReconciler.TryReconcile(
                first.Snapshot,
                new ConvaiActionConfigPatch
                {
                    Actions = new List<string>(),
                    Characters = new List<ConvaiActionCharacterDefinition>
                    {
                        new() { Name = "  Guide  ", Bio = "  helps player  " }
                    },
                    CurrentAttentionObject = string.Empty
                },
                null,
                character.GetRuntimeActionDefinitionCatalog(),
                out ConvaiActionConfigReconciliation second,
                out string secondError);

            Assert.IsTrue(cleared, secondError);
            Assert.AreEqual(0, second.Snapshot.Actions.Count);
            Assert.AreEqual("Lever", second.Snapshot.Objects[0].Name, "Omitted objects must be preserved.");
            Assert.AreEqual("Guide", second.Snapshot.Characters[0].Name);
            Assert.AreEqual("helps player", second.Snapshot.Characters[0].Bio);
            Assert.IsNull(second.Snapshot.CurrentAttentionObject);
            Assert.IsNull(second.Patch.Objects);

            Assert.IsTrue(ConvaiActionConfigPatchReconciler.TryReconcile(
                second.Snapshot,
                new ConvaiActionConfigPatch
                {
                    Characters = new List<ConvaiActionCharacterDefinition>()
                },
                null,
                character.GetRuntimeActionDefinitionCatalog(),
                out ConvaiActionConfigReconciliation third,
                out string thirdError), thirdError);
            Assert.AreEqual(0, third.Snapshot.Characters.Count);
            Assert.AreEqual("Lever", third.Snapshot.Objects[0].Name);

            Assert.IsTrue(ConvaiActionConfigPatchReconciler.TryReconcile(
                third.Snapshot,
                new ConvaiActionConfigPatch
                {
                    Objects = new List<ConvaiActionObjectDefinition>()
                },
                null,
                character.GetRuntimeActionDefinitionCatalog(),
                out ConvaiActionConfigReconciliation fourth,
                out string fourthError), fourthError);
            Assert.AreEqual(0, fourth.Snapshot.Objects.Count);
            Assert.AreEqual(0, fourth.Snapshot.Characters.Count);
        }

        [Test]
        public void ActionConfigPatchReconciler_TopLevelAttentionWinsAndUsesAuthoredCasing()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Lever", "Cube");
            ConvaiActionConfig current = character.ActionConfig;

            bool valid = ConvaiActionConfigPatchReconciler.TryReconcile(
                current,
                new ConvaiActionConfigPatch { CurrentAttentionObject = "Cube" },
                new ConvaiActionObjectDefinition { Name = " lever " },
                character.GetRuntimeActionDefinitionCatalog(),
                out ConvaiActionConfigReconciliation result,
                out string error);

            Assert.IsTrue(valid, error);
            Assert.AreEqual("Lever", result.Snapshot.CurrentAttentionObject);
            Assert.AreEqual("Lever", result.TopLevelAttentionObject);
        }

        [Test]
        public void ActionConfigPatchReconciler_RejectsBlankActionsAndInvalidTargetNames()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            ConvaiActionConfig current = character.ActionConfig;
            IReadOnlyList<ConvaiActionDefinition> catalog = character.GetRuntimeActionDefinitionCatalog();

            Assert.IsFalse(ConvaiActionConfigPatchReconciler.TryReconcile(
                current,
                new ConvaiActionConfigPatch { Actions = new List<string> { "  " } },
                null,
                catalog,
                out _,
                out _));
            Assert.IsFalse(ConvaiActionConfigPatchReconciler.TryReconcile(
                current,
                new ConvaiActionConfigPatch { Actions = new List<string> { null } },
                null,
                catalog,
                out _,
                out _));
            Assert.IsFalse(ConvaiActionConfigPatchReconciler.TryReconcile(
                current,
                new ConvaiActionConfigPatch
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "Cube" },
                        new() { Name = " cube " }
                    }
                },
                null,
                catalog,
                out _,
                out _));
            Assert.IsFalse(ConvaiActionConfigPatchReconciler.TryReconcile(
                current,
                new ConvaiActionConfigPatch
                {
                    Characters = new List<ConvaiActionCharacterDefinition> { null }
                },
                null,
                catalog,
                out _,
                out _));
        }

        [Test]
        public void RawApply_InvalidMixedActionPatch_DoesNotSendOrMutateConfirmedState()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                "Mixed text must also be rejected.",
                actionConfig: new ConvaiActionConfigPatch
                {
                    Actions = new List<string> { "Fly To" },
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "Moon" }
                    }
                }));

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);
            CollectionAssert.AreEqual(new[] { "Move To" }, character.ActionConfig.Actions);
            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);
        }

        [Test]
        public void RawApply_UnsupportedAttentionType_DoesNotSend()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                "Unsupported attention payload",
                currentAttentionObject: new object()));

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);
        }

        [Test]
        public void RawApply_SessionWithoutConnectConfig_BecomesActionEnabledOnlyAfterAck()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            character.SetResolvedSessionActionConfig(null);
            character.SetResolvedSessionActionDefinitions(Array.Empty<ConvaiActionDefinition>());
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                null,
                actionConfig: new ConvaiActionConfigPatch
                {
                    Actions = new List<string> { " Move To " },
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = " Cube ", GameObjectReference = character.gameObject }
                    }
                }));

            Assert.IsNull(character.ActionConfig);
            Assert.AreEqual(0, character.ActionDefinitions.Count);
            ConvaiDynamicContextUpdate sent = _connectionService.SentDynamicContextUpdates[0];
            PublishActionAck(sent, 1, 1, 0, null);

            Assert.AreEqual("Move To", character.ActionConfig.Actions[0]);
            Assert.AreEqual(1, character.ActionDefinitions.Count);
            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);
            Assert.AreSame(character.gameObject, character.ActionConfig.Objects[0].GameObjectReference);
        }

        [Test]
        public void RuntimeActionPatch_ErrorMismatchAndTimeout_DoNotCommit()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            MakeReady(character);

            ConvaiDynamicContextUpdate errorUpdate = SendObjectPatch(character, "Lever", "ack-error");
            PublishActionAck(errorUpdate, 1, 1, 0, null, status: "error");
            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);

            ConvaiDynamicContextUpdate mismatchUpdate = SendObjectPatch(character, "Lever", "ack-mismatch");
            PublishActionAck(mismatchUpdate, 99, 1, 0, null);
            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);

            ConvaiDynamicContextUpdate malformedUpdate = SendObjectPatch(character, "Lever", "ack-malformed");
            _eventHub.Publish(DynamicContextUpdateResultReceived.Create(
                "success",
                string.Empty,
                new JObject { ["update_id"] = malformedUpdate.UpdateId }));
            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);

            SendObjectPatch(character, "Lever", "ack-timeout");
            character.ProcessPendingRuntimeActionStateUpdates(DateTime.UtcNow.AddSeconds(31));
            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);
        }

        [Test]
        public void RuntimeActionPatch_OutOfOrderAcksCommitInSendOrder()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            MakeReady(character);

            ConvaiDynamicContextUpdate objectsUpdate = SendObjectPatch(character, "Lever", "ordered-1");
            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                null,
                currentAttentionObject: "lever",
                updateId: "ordered-2"));
            ConvaiDynamicContextUpdate attentionUpdate = _connectionService.SentDynamicContextUpdates[1];

            PublishActionAck(attentionUpdate, 1, 1, 0, "Lever");
            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);
            Assert.IsNull(character.ActionConfig.CurrentAttentionObject);

            PublishActionAck(objectsUpdate, 1, 1, 0, null);
            Assert.AreEqual("Lever", character.ActionConfig.Objects[0].Name);
            Assert.AreEqual("Lever", character.ActionConfig.CurrentAttentionObject);
        }

        [Test]
        public void RuntimeActionPatch_DisconnectClearsPendingMutation()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "Cube");
            MakeReady(character);

            ConvaiDynamicContextUpdate update = SendObjectPatch(character, "Lever", "disconnect-pending");
            _connectionService.RaiseConnectionFailed();
            PublishActionAck(update, 1, 1, 0, null);

            Assert.AreEqual("Cube", character.ActionConfig.Objects[0].Name);
        }

        [Test]
        public void ConvaiCharacter_ImplementsCharacterDynamicContextSurface()
        {
            ConvaiCharacter character = CreateCharacter();

            Assert.NotNull(character.DynamicContext);
            Assert.IsTrue(character is Convai.Runtime.Behaviors.IConvaiCharacterAgent);
            Assert.NotNull(((Convai.Runtime.Behaviors.IConvaiCharacterAgent)character).DynamicContext);
        }

        [Test]
        public void SetCurrentAttentionObject_UnknownAuthoredObject_DoesNotSendUpdate()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "cube");
            MakeReady(character);

            character.DynamicContext.SetCurrentAttentionObject("lever");
            character.DynamicContext.Flush();

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);
        }

        [Test]
        public void RawApply_UnknownAttentionObject_DoesNotSendUpdate()
        {
            ConvaiCharacter character = CreateCharacter();
            ConfigureActionObjects(character, "cube");
            MakeReady(character);

            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                "Player focuses the lever.",
                currentAttentionObject: "lever"));

            Assert.AreEqual(0, _connectionService.SentDynamicContextUpdates.Count);
        }

        [Test]
        public void SetState_LongNewValue_OmitsToClauseInDeltaLine()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            character.DynamicContext.SetState("Notes", "short");
            character.DynamicContext.Flush();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.SetState(
                "Notes",
                "one two three four",
                ConvaiRespondMode.MustRespond);
            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Notes is one two three four\nNotes changed from short",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.MustRespond);
        }

        [Test]
        public void Relay_SetStateWithImmediateFlush_SendsBatchedContext()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            ConvaiDynamicContextRelay relay = character.gameObject.AddComponent<ConvaiDynamicContextRelay>();
            SetPrivateField(relay, "_flushImmediately", true);
            SetPrivateField(relay, "_reactionMode", ConvaiRespondMode.Auto);

            relay.SetState("Health", "100");

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            AssertUpdate(
                _connectionService.SentDynamicContextUpdates[0],
                "Health is 100",
                ConvaiContextUpdateMode.Replace,
                ConvaiRespondMode.Auto);
            AssertHasUpdateId(_connectionService.SentDynamicContextUpdates[0]);
        }

        private ConvaiCharacter CreateCharacter(string characterId = "test-char-id", string characterName = "TestCharacter")
        {
            var go = new GameObject(characterName);
            _createdObjects.Add(go);

            var character = go.AddComponent<ConvaiCharacter>();
            character.Configure(characterId, characterName);
            character.InjectDependencies(new ConvaiCharacterDependencies(
                _eventHub,
                _connectionService,
                _audioService,
                _agentRegistry,
                _logger));

            return character;
        }

        private static void ConfigureActionObjects(ConvaiCharacter character, params string[] objectNames)
        {
            ConvaiActionConfigSource source = character.gameObject.AddComponent<ConvaiActionConfigSource>();
            ConvaiUnityEventActionExecutor executor = character.gameObject.AddComponent<ConvaiUnityEventActionExecutor>();
            SetPrivateField(source, "_definitions", new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = executor }
            });

            var objects = new List<ConvaiActionObjectDefinition>();
            for (int i = 0; i < objectNames.Length; i++)
                objects.Add(new ConvaiActionObjectDefinition { Name = objectNames[i] });

            SetPrivateField(source, "_objects", objects);
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            System.Reflection.FieldInfo field = target.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field, $"Expected field '{fieldName}' on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        private void MakeReady(ConvaiCharacter character)
        {
            _connectionService.RaiseConnected();
            _eventHub.Publish(CharacterReady.Create(character.CharacterId, $"participant-{character.CharacterId}"));
        }

        private void PublishActionAck(
            ConvaiDynamicContextUpdate update,
            int actionsCount,
            int objectsCount,
            int charactersCount,
            string currentAttentionObject,
            string status = "success")
        {
            JObject extras = new()
            {
                ["update_id"] = update.UpdateId,
                ["action_config_updated"] = true,
                ["action_config_created"] = false,
                ["actions_count"] = actionsCount,
                ["objects_count"] = objectsCount,
                ["characters_count"] = charactersCount,
                ["current_attention_object"] = currentAttentionObject == null
                    ? JValue.CreateNull()
                    : currentAttentionObject,
                ["current_attention_object_cleared"] = currentAttentionObject == null,
                ["action_generation_strategy_changed"] = false,
                ["action_generation_strategy_status"] = "unchanged",
                ["prompt_rebuild"] = "deferred"
            };
            _eventHub.Publish(DynamicContextUpdateResultReceived.Create(status, string.Empty, extras));
        }

        private ConvaiDynamicContextUpdate SendObjectPatch(
            ConvaiCharacter character,
            string objectName,
            string updateId)
        {
            character.DynamicContext.Apply(new ConvaiDynamicContextUpdate(
                null,
                updateId: updateId,
                actionConfig: new ConvaiActionConfigPatch
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = objectName }
                    }
                }));

            return _connectionService.SentDynamicContextUpdates[^1];
        }

        private static void AssertUpdate(
            ConvaiDynamicContextUpdate update,
            string expectedText,
            ConvaiContextUpdateMode expectedMode,
            ConvaiRespondMode expectedReaction)
        {
            Assert.AreEqual(expectedText, update.Text);
            Assert.AreEqual(expectedMode, update.Mode);
            Assert.AreEqual(expectedReaction, update.Reaction);
        }

        private static void AssertHasUpdateId(ConvaiDynamicContextUpdate update)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(update.UpdateId));
            StringAssert.StartsWith("unity-", update.UpdateId);
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class TestLogger : ILogger
        {
            public void Log(in Convai.Domain.Logging.LogEntry entry)
            {
                if (entry.HasException) { Error(entry.Exception, entry.Message, entry.Context, entry.Category); return; }
                switch (entry.Level)
                {
                    case Convai.Domain.Logging.LogLevel.Debug:
                    case Convai.Domain.Logging.LogLevel.Trace: Debug(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Info: Info(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Warning: Warning(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Error: Error(entry.Message, entry.Context, entry.Category); break;
                }
            }
            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, LogCategory category = LogCategory.SDK) { }

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Info(string message, LogCategory category = LogCategory.SDK) { }

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Warning(string message, LogCategory category = LogCategory.SDK) { }

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(string message, LogCategory category = LogCategory.SDK) { }

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) { }

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public bool IsEnabled(LogLevel level, LogCategory category) => true;
        }
    }
}
