using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Runtime.Actions;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.SceneMetadata;
using Convai.Shared.Actions;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class ConvaiWorldObjectEnvironmentTests
    {
        private readonly List<GameObject> _createdObjects = new();
        private MockRoomConnectionService _connectionService;
        private EventHub _eventHub;
        private MockAgentRegistry _agentRegistry;
        private TestLogger _logger;

        [SetUp]
        public void SetUp()
        {
            ConvaiMetadataRegistry.Clear();
            _eventHub = new EventHub(new ImmediateScheduler());
            _connectionService = new MockRoomConnectionService();
            _agentRegistry = new MockAgentRegistry();
            _logger = new TestLogger();
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiMetadataRegistry.Clear();

            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);

            _createdObjects.Clear();
        }

        [Test]
        public void SeedTrackedStateOntoCharacter_SetsDynamicContextStateKey()
        {
            ConvaiCharacter character = CreateCharacter();
            ConvaiObjectMetadata metadata = CreateWorldObject("chest", "Treasure chest", ("Health", "100"));

            MakeReady(character);
            metadata.SeedTrackedStateOntoCharacter(character);
            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.AreEqual(
                "chest.Health is 100",
                _connectionService.SentDynamicContextUpdates[0].Text);
        }

        [Test]
        public void EvaluateTrackedProperties_PushesTrackedStateToRegisteredCharacter()
        {
            ConvaiCharacter character = CreateCharacter();
            _agentRegistry.RegisterCharacter(character);
            ConvaiObjectMetadata metadata = CreateWorldObject("chest", "Treasure chest", ("Health", "100"));

            MakeReady(character);
            metadata.EvaluateTrackedProperties(_agentRegistry);
            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.AreEqual(
                "chest.Health is 100",
                _connectionService.SentDynamicContextUpdates[0].Text);
        }

        [Test]
        public void EvaluateTrackedProperties_WithRuntimeSource_PushesChangedValueOnly()
        {
            ConvaiCharacter character = CreateCharacter();
            _agentRegistry.RegisterCharacter(character);
            GameObject go = CreateInactiveWorldObject("chest", "Treasure chest");
            RuntimePropertySource source = go.AddComponent<RuntimePropertySource>();
            source.Health = 100;
            ConvaiObjectMetadata metadata = AddMetadata(go, "chest", "Treasure chest", ("Health", "100", source, "Health"));

            go.SetActive(true);
            EnsureRegistered(metadata);
            MakeReady(character);
            _connectionService.SentDynamicContextUpdates.Clear();

            source.Health = 50;
            metadata.EvaluateTrackedProperties(_agentRegistry);
            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.AreEqual(
                "chest.Health is 50",
                _connectionService.SentDynamicContextUpdates[0].Text);

            metadata.EvaluateTrackedProperties(_agentRegistry);
            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
        }

        [Test]
        public void CharacterReady_FlushesPostConnectSceneMetadataExcludingActionConfigObjects()
        {
            ConvaiCharacter character = CreateCharacterWithActionObject("cube");
            CreateWorldObject("cube", "Connect-time cube");
            CreateWorldObject("lever", "Post-connect lever");

            MakeReady(character);

            Assert.AreEqual(1, _connectionService.SentSceneMetadataUpdates.Count);
            IReadOnlyList<Convai.Infrastructure.Protocol.Messages.SceneMetadata> payload =
                _connectionService.SentSceneMetadataUpdates[0];
            Assert.AreEqual(1, payload.Count);
            Assert.AreEqual("lever", payload[0].Name);
        }

        [Test]
        public void Flush_CoFlushesSceneMetadataAfterDynamicContext()
        {
            ConvaiCharacter character = CreateCharacter();
            CreateWorldObject("lever", "Post-connect lever");
            MakeReady(character);
            _connectionService.SentSceneMetadataUpdates.Clear();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.SetState("Health", "100");
            character.MarkPendingSceneMetadataSync();
            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.AreEqual(1, _connectionService.SentSceneMetadataUpdates.Count);
            Assert.AreEqual("lever", _connectionService.SentSceneMetadataUpdates[0][0].Name);
        }

        [Test]
        public void Flush_WithOnlySceneMetadataDirty_SendsSceneMetadata()
        {
            ConvaiCharacter character = CreateCharacter();
            CreateWorldObject("lever", "Post-connect lever");
            MakeReady(character);
            _connectionService.SentSceneMetadataUpdates.Clear();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.MarkPendingSceneMetadataSync();
            character.DynamicContext.Flush();

            Assert.IsEmpty(_connectionService.SentDynamicContextUpdates);
            Assert.AreEqual(1, _connectionService.SentSceneMetadataUpdates.Count);
            Assert.AreEqual("lever", _connectionService.SentSceneMetadataUpdates[0][0].Name);
        }

        [Test]
        public void Flush_ResetAndSceneMetadataDirty_SendsSceneMetadataAfterReset()
        {
            ConvaiCharacter character = CreateCharacter();
            CreateWorldObject("lever", "Post-connect lever");
            MakeReady(character);
            _connectionService.SentSceneMetadataUpdates.Clear();
            _connectionService.SentDynamicContextUpdates.Clear();

            character.DynamicContext.Reset();
            character.MarkPendingSceneMetadataSync();
            character.DynamicContext.Flush();

            Assert.AreEqual(1, _connectionService.SentDynamicContextUpdates.Count);
            Assert.AreEqual(ConvaiContextUpdateMode.Reset, _connectionService.SentDynamicContextUpdates[0].Mode);
            Assert.AreEqual(1, _connectionService.SentSceneMetadataUpdates.Count);
            Assert.AreEqual("lever", _connectionService.SentSceneMetadataUpdates[0][0].Name);
        }

        [Test]
        public void RemovingLastMetadataSendsOneEmptyReplacementAndSkipsEmptyStartup()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            Assert.That(_connectionService.SentSceneMetadataUpdates, Is.Empty);
            ConvaiObjectMetadata metadata = CreateWorldObject("lamp", "A lamp");
            character.MarkPendingSceneMetadataSync(); character.DynamicContext.Flush();
            Assert.That(_connectionService.SentSceneMetadataUpdates, Has.Count.EqualTo(1));
            Object.DestroyImmediate(metadata.gameObject);
            character.MarkPendingSceneMetadataSync(); character.DynamicContext.Flush();
            Assert.That(_connectionService.SentSceneMetadataUpdates, Has.Count.EqualTo(2));
            Assert.That(_connectionService.SentSceneMetadataUpdates[1], Is.Empty);
            character.MarkPendingSceneMetadataSync(); character.DynamicContext.Flush();
            Assert.That(_connectionService.SentSceneMetadataUpdates, Has.Count.EqualTo(2));
        }

        [Test]
        public void SpatialPollingUpdatesMovingObjectsAndRetiresOnlyOwnedFacts()
        {
            ConvaiCharacter character = CreateCharacter();
            _agentRegistry.RegisterCharacter(character);
            ConvaiObjectMetadata metadata = CreateWorldObject("exhibit", "A red exhibit");
            metadata.IncludeSpatialContext = true;
            metadata.transform.position = new Vector3(0, 0, 3);
            string key = "scene-object." + Convai.Shared.Compatibility.ConvaiObjectId.Of(metadata) + ".spatial";
            MakeReady(character);
            character.DynamicContext.SetState("authored.fact", "Keep this fact");
            var producer = new ConvaiSpatialContextProducer();
            try
            {
                producer.Poll(_agentRegistry);
                Assert.That(character.DynamicContext.TryGetStateValue(key, out string value), Is.True);
                Assert.That(value, Does.Contain("3.0 m ahead").And.Contain("stationary"));
                metadata.transform.position = new Vector3(4, 0, 0);
                producer.Poll(_agentRegistry);
                Assert.That(character.DynamicContext.TryGetStateValue(key, out value), Is.True);
                Assert.That(value, Does.Contain("4.0 m right").And.Contain("moving"));
                character.DynamicContext.Flush();
                Assert.That(_connectionService.SentDynamicContextUpdates[^1].Reaction, Is.EqualTo(ConvaiRespondMode.Silent));
                metadata.gameObject.SetActive(false);
                producer.Poll(_agentRegistry);
                Assert.That(character.DynamicContext.TryGetStateValue(key, out _), Is.False);
                Assert.That(character.DynamicContext.TryGetStateValue("authored.fact", out value), Is.True);
                Assert.That(value, Is.EqualTo("Keep this fact"));
            }
            finally { producer.Clear(); }
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
                new MockRoomAudioService(),
                _agentRegistry,
                _logger));

            return character;
        }

        private ConvaiCharacter CreateCharacterWithActionObject(string objectName)
        {
            ConvaiCharacter character = CreateCharacter();
            var source = character.gameObject.AddComponent<ConvaiActionConfigSource>();
            var executor = character.gameObject.AddComponent<ConvaiUnityEventActionExecutor>();
            SetPrivateField(source, "_definitions", new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Move To", Executor = executor }
            });
            SetPrivateField(source, "_objects", new List<ConvaiActionObjectDefinition>
            {
                new() { Name = objectName }
            });
            return character;
        }

        private ConvaiObjectMetadata CreateWorldObject(
            string objectName,
            string description,
            params (string PropertyName, string InitialValue)[] trackedProperties)
        {
            GameObject go = CreateInactiveWorldObject(objectName, description);
            ConvaiObjectMetadata metadata = AddMetadata(go, objectName, description, trackedProperties);

            go.SetActive(true);
            EnsureRegistered(metadata);
            return metadata;
        }

        private GameObject CreateInactiveWorldObject(string objectName, string description)
        {
            var go = new GameObject(objectName);
            _createdObjects.Add(go);
            go.SetActive(false);
            return go;
        }

        private static ConvaiObjectMetadata AddMetadata(
            GameObject go,
            string objectName,
            string description,
            params (string PropertyName, string InitialValue)[] trackedProperties)
        {
            ConvaiObjectMetadata metadata = go.AddComponent<ConvaiObjectMetadata>();
            metadata.ObjectName = objectName;
            metadata.ObjectDescription = description;

            if (trackedProperties.Length > 0)
            {
                var properties = new List<ConvaiTrackedContextProperty>(trackedProperties.Length);
                foreach ((string propertyName, string initialValue) in trackedProperties)
                {
                    var property = new ConvaiTrackedContextProperty();
                    SetPrivateField(property, "_propertyName", propertyName);
                    SetPrivateField(property, "_initialValue", initialValue);
                    properties.Add(property);
                }

                SetPrivateField(metadata, "_trackedProperties", properties);
            }

            return metadata;
        }

        private static ConvaiObjectMetadata AddMetadata(
            GameObject go,
            string objectName,
            string description,
            params (string PropertyName, string InitialValue, Component Source, string SourceMember)[] trackedProperties)
        {
            ConvaiObjectMetadata metadata = go.AddComponent<ConvaiObjectMetadata>();
            metadata.ObjectName = objectName;
            metadata.ObjectDescription = description;

            if (trackedProperties.Length > 0)
            {
                var properties = new List<ConvaiTrackedContextProperty>(trackedProperties.Length);
                foreach ((string propertyName, string initialValue, Component source, string sourceMember) in trackedProperties)
                {
                    var property = new ConvaiTrackedContextProperty();
                    SetPrivateField(property, "_propertyName", propertyName);
                    SetPrivateField(property, "_initialValue", initialValue);
                    SetPrivateField(property, "_sourceComponent", source);
                    SetPrivateField(property, "_sourceMemberName", sourceMember);
                    properties.Add(property);
                }

                SetPrivateField(metadata, "_trackedProperties", properties);
            }

            return metadata;
        }

        private static void EnsureRegistered(ConvaiObjectMetadata metadata)
        {
            if (metadata == null || metadata.IsRegistered) return;
            ConvaiMetadataRegistry.RegisterMetadata(metadata);
        }

        private void MakeReady(ConvaiCharacter character)
        {
            _connectionService.RaiseConnected();
            _eventHub.Publish(CharacterReady.Create(character.CharacterId, $"participant-{character.CharacterId}"));
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            System.Reflection.FieldInfo field = target.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field, $"Expected field '{fieldName}' on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        private sealed class RuntimePropertySource : MonoBehaviour
        {
            public int Health;
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
