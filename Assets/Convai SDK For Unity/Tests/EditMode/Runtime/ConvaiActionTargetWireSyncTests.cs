using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Runtime;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Runtime.DynamicContext;
using Convai.Shared.Actions;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Coverage for staged mid-session backend sync of the runtime action-target registry
    ///     (<see cref="ConvaiCharacterActions" />) through the existing dynamic-context batching
    ///     pipeline, and attention-object validation against the merged runtime config.
    /// </summary>
    [TestFixture]
    public class ConvaiActionTargetWireSyncTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private MockRoomAudioService _audioService;
        private MockRoomConnectionService _connectionService;
        private EventHub _eventHub;
        private MockAgentRegistry _agentRegistry;
        private TestLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => Convai.Runtime.Logging.ConvaiLogger.ClearSinks();

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
        public void RegisterObject_ThenFlush_StagesSilentActionConfigSync_WithNoLocalOnlyFields()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            _connectionService.SentDynamicContextUpdates.Clear();

            character.Actions.RegisterObject("lantern", "A brass lantern.");
            character.DynamicContext.Flush();

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update, "Expected one context-update carrying an action_config snapshot.");
            Assert.AreEqual(ConvaiRespondMode.Silent, update.Reaction);
            Assert.IsNull(update.Text);

            ConvaiActionObjectDefinition lantern = FindObject(update.ActionConfig, "lantern");
            Assert.NotNull(lantern);
            Assert.AreEqual("A brass lantern.", lantern.Description);
            Assert.IsNull(lantern.GameObjectReference);
            Assert.IsNull(lantern.InteractionPoint);
        }

        [Test]
        public void TwoRapidRegistrations_CoalesceIntoOneStagedSync()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            _connectionService.SentDynamicContextUpdates.Clear();

            character.Actions.RegisterObject("lantern", "A brass lantern.");
            character.Actions.RegisterObject("torch", "A lit torch.");
            character.DynamicContext.Flush();

            int actionConfigUpdateCount = 0;
            foreach (ConvaiDynamicContextUpdate sent in _connectionService.SentDynamicContextUpdates)
                if (sent.ActionConfig != null)
                    actionConfigUpdateCount++;

            Assert.AreEqual(1, actionConfigUpdateCount, "Expected both registrations to coalesce into one staged sync.");

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(FindObject(update.ActionConfig, "lantern"));
            Assert.NotNull(FindObject(update.ActionConfig, "torch"));
        }

        [Test]
        public void UnregisterTarget_ThenFlush_SnapshotOmitsEntry()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            character.Actions.RegisterObject("lantern", "A brass lantern.");
            character.DynamicContext.Flush();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.Actions.UnregisterTarget("lantern");
            character.DynamicContext.Flush();

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update, "Expected an action-config resync after unregistering.");
            Assert.IsNull(FindObject(update.ActionConfig, "lantern"));
        }

        [Test]
        public void UnavailableTarget_ExcludedFromSnapshot()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            character.Actions.RegisterObject("lantern", "A brass lantern.");
            character.DynamicContext.Flush();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.Actions.SetTargetAvailable("lantern", false);
            character.DynamicContext.Flush();

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update);
            Assert.IsNull(FindObject(update.ActionConfig, "lantern"));
        }

        [Test]
        public void DuplicateNamedRegistrations_SnapshotKeepsOnlyFirstInstance()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            _connectionService.SentDynamicContextUpdates.Clear();

            character.Actions.RegisterObject("chair", "chair one");
            character.Actions.RegisterObject("chair", "chair two");
            character.DynamicContext.Flush();

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update);

            int count = 0;
            foreach (ConvaiActionObjectDefinition o in update.ActionConfig.Objects)
                if (string.Equals(o.Name, "chair", StringComparison.OrdinalIgnoreCase))
                    count++;

            Assert.AreEqual(1, count, "The backend rejects duplicate names, so the snapshot must dedupe.");
        }

        [Test]
        public void PreConnectRegistration_SyncsOnCharacterReady()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();

            character.Actions.RegisterObject("lantern", "A brass lantern.");
            _connectionService.SentDynamicContextUpdates.Clear();

            MakeReady(character);

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update, "Expected pre-connect registrations to sync once the character is ready.");
            Assert.NotNull(FindObject(update.ActionConfig, "lantern"));
        }

        [Test]
        public void Reconnect_WithExistingRegistryEntries_ResyncsActionConfigOnCharacterReady()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            character.Actions.RegisterObject("lantern", "A brass lantern.");
            character.DynamicContext.Flush();
            Assert.NotNull(FindActionConfigUpdate(), "Sanity: first sync must have been sent.");
            _connectionService.SentDynamicContextUpdates.Clear();

            _connectionService.RaiseConnectionFailed();
            _connectionService.RaiseConnected();
            _eventHub.Publish(CharacterReady.Create(character.CharacterId, $"participant-{character.CharacterId}"));

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update,
                "A fresh backend session has no memory of the prior registry state, so ready must resync it.");
            Assert.NotNull(FindObject(update.ActionConfig, "lantern"));
        }

        [Test]
        public void SetCurrentAttentionObject_RegistryRegisteredName_IsAccepted()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            character.Actions.RegisterObject("lantern", "A brass lantern.");
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.SetCurrentAttentionObject("lantern");
            character.DynamicContext.Flush();

            bool found = false;
            foreach (ConvaiDynamicContextUpdate sent in _connectionService.SentDynamicContextUpdates)
                if (Equals(sent.CurrentAttentionObject, "lantern"))
                    found = true;

            Assert.IsTrue(found, "Expected a context-update carrying the registry-registered attention object.");
        }

        [Test]
        public void SetCurrentAttentionObject_UnknownName_StillRejected()
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            MakeReady(character);
            character.Actions.RegisterObject("lantern", "A brass lantern.");
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.SetCurrentAttentionObject("nonexistent");
            character.DynamicContext.Flush();

            foreach (ConvaiDynamicContextUpdate sent in _connectionService.SentDynamicContextUpdates)
                Assert.IsNull(sent.CurrentAttentionObject);
        }

        // ── Action availability re-sync ──────────────────────────────────────────────────

        [Test]
        public void SetActionAvailable_False_ThenFlush_SnapshotOmitsActionString()
        {
            ConvaiCharacter character = CreateCharacterWithTwoActions();
            MakeReady(character);
            _connectionService.SentDynamicContextUpdates.Clear();

            character.Actions.SetActionAvailable("Move To", false);
            character.DynamicContext.Flush();

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update, "Disabling an action must stage a re-sync through the same batching pipeline.");
            Assert.IsFalse(ContainsAction(update.ActionConfig, "Move To"));
            Assert.IsTrue(ContainsAction(update.ActionConfig, "Wave"),
                "Other actions must survive in the replace-semantics snapshot.");
        }

        [Test]
        public void SetActionAvailable_True_RestoresAuthoredDisabledAction_InSnapshot()
        {
            ConvaiCharacter character = CreateCharacterWithTwoActions(secondActionEnabled: false);
            MakeReady(character);
            _connectionService.SentDynamicContextUpdates.Clear();

            character.Actions.SetActionAvailable("Wave", true);
            character.DynamicContext.Flush();

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update);
            Assert.IsTrue(ContainsAction(update.ActionConfig, "Wave"),
                "A mid-session enable override must restore the authored-disabled action's rendered string.");
            Assert.IsTrue(ContainsAction(update.ActionConfig, "Move To"));
        }

        [Test]
        public void AuthoredDisabledAction_ExcludedFromSnapshot_WithoutOverride()
        {
            ConvaiCharacter character = CreateCharacterWithTwoActions(secondActionEnabled: false);
            MakeReady(character);
            _connectionService.SentDynamicContextUpdates.Clear();

            // Any staged sync (here: a target registration) must respect the authored flag.
            character.Actions.RegisterObject("lantern", "A brass lantern.");
            character.DynamicContext.Flush();

            ConvaiDynamicContextUpdate update = FindActionConfigUpdate();
            Assert.NotNull(update);
            Assert.IsFalse(ContainsAction(update.ActionConfig, "Wave"));
            Assert.IsTrue(ContainsAction(update.ActionConfig, "Move To"));
        }

        // ── Fixture helpers ───────────────────────────────────────────────────────────────

        private sealed class NoOpActionExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, CancellationToken cancellationToken) =>
                Task.FromResult(ConvaiActionExecutionResult.Succeeded());
        }

        private ConvaiCharacter CreateCharacterWithOneAction(
            string characterId = "test-char-id", string characterName = "TestCharacter")
        {
            var go = new GameObject($"WireSyncTests_Character_{Guid.NewGuid():N}");
            _createdObjects.Add(go);

            var character = go.AddComponent<ConvaiCharacter>();
            character.Configure(characterId, characterName);
            character.InjectDependencies(new ConvaiCharacterDependencies(
                _eventHub, _connectionService, _audioService, _agentRegistry, _logger));

            ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
            NoOpActionExecutor executor = go.AddComponent<NoOpActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.Either,
                    Executor = executor
                }
            });

            return character;
        }

        /// <summary>
        ///     Same one-action fixture as <see cref="CreateCharacterWithOneAction" /> ("Move To"),
        ///     plus a second "Wave" action whose authored Enabled flag is configurable.
        /// </summary>
        private ConvaiCharacter CreateCharacterWithTwoActions(bool secondActionEnabled = true)
        {
            ConvaiCharacter character = CreateCharacterWithOneAction();
            ConvaiActionConfigSource source = character.GetComponent<ConvaiActionConfigSource>();
            NoOpActionExecutor executor = character.GetComponent<NoOpActionExecutor>();
            var definitions = new List<ConvaiActionDefinition>(source.Definitions)
            {
                new()
                {
                    ActionName = "Wave",
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = executor,
                    Enabled = secondActionEnabled
                }
            };
            source.ReplaceDefinitions(definitions);
            return character;
        }

        private static bool ContainsAction(ConvaiActionConfigPatch config, string actionName)
        {
            if (config?.Actions == null) return false;
            foreach (string rendered in config.Actions)
                if (rendered != null && rendered.StartsWith(actionName, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private void MakeReady(ConvaiCharacter character)
        {
            _connectionService.RaiseConnected();
            _eventHub.Publish(CharacterReady.Create(character.CharacterId, $"participant-{character.CharacterId}"));
        }

        private ConvaiDynamicContextUpdate FindActionConfigUpdate()
        {
            foreach (ConvaiDynamicContextUpdate update in _connectionService.SentDynamicContextUpdates)
                if (update.ActionConfig != null)
                    return update;

            return null;
        }

        private static ConvaiActionObjectDefinition FindObject(ConvaiActionConfigPatch config, string name)
        {
            if (config?.Objects == null) return null;
            foreach (ConvaiActionObjectDefinition o in config.Objects)
                if (o != null && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                    return o;

            return null;
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
