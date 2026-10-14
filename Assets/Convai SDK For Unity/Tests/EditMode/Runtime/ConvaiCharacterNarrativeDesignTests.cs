using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Narrative;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Domain.Narrative;
using Convai.Modules.Narrative;
using Convai.RestAPI.Internal.Models;
using Convai.Runtime.Components;
using Convai.Runtime.Core.DependencyInjection;
using Convai.Runtime.NarrativeDesign;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using ILogger = Convai.Domain.Logging.ILogger;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiCharacterNarrativeDesignTests
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
        public void NarrativeDesign_PublicSurface_IsCharacterScoped()
        {
            ConvaiCharacter character = CreateCharacter();

            Assert.NotNull(character.NarrativeDesign);
            Assert.AreSame(character.NarrativeDesign, character.NarrativeDesign);
        }

        [Test]
        public void SetTemplateKeys_BeforeReady_FlushesWhenCharacterReady()
        {
            ConvaiCharacter character = CreateCharacter();

            character.NarrativeDesign.SetTemplateKey("PlayerName", "Rishav");
            character.NarrativeDesign.SetTemplateKeys(new Dictionary<string, string>
            {
                ["Quest"] = "Museum"
            });

            Assert.AreEqual(0, _connectionService.SentTemplateKeys.Count);

            MakeReady(character);

            Assert.AreEqual(1, _connectionService.SentTemplateKeys.Count);
            Assert.AreEqual("Rishav", _connectionService.SentTemplateKeys[0]["PlayerName"]);
            Assert.AreEqual("Museum", _connectionService.SentTemplateKeys[0]["Quest"]);
        }

        [Test]
        public void InvokeTrigger_BeforeReady_QueuesSavedTriggerAndTrimsName()
        {
            ConvaiCharacter character = CreateCharacter();

            bool accepted = character.NarrativeDesign.InvokeTrigger(" open_gate ");

            Assert.IsTrue(accepted);
            Assert.AreEqual(0, _connectionService.SentNarrativeTriggers.Count);

            MakeReady(character);

            Assert.AreEqual(1, _connectionService.SentNarrativeTriggers.Count);
            ConvaiNarrativeTriggerRequest request = _connectionService.SentNarrativeTriggers[0];
            Assert.AreEqual(ConvaiNarrativeTriggerMode.SavedTrigger, request.Mode);
            Assert.AreEqual("trigger_name", request.WireFieldName);
            Assert.AreEqual("open_gate", request.WireFieldValue);
        }

        [Test]
        public void InvokeEvent_SendsInlineEventMessage()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);

            bool accepted = character.NarrativeDesign.InvokeEvent("Player inspected the artifact");

            Assert.IsTrue(accepted);
            Assert.AreEqual(1, _connectionService.SentNarrativeTriggers.Count);
            ConvaiNarrativeTriggerRequest request = _connectionService.SentNarrativeTriggers[0];
            Assert.AreEqual(ConvaiNarrativeTriggerMode.InlineEvent, request.Mode);
            Assert.AreEqual("trigger_message", request.WireFieldName);
            Assert.AreEqual("Player inspected the artifact", request.WireFieldValue);
        }

        [Test]
        public void InvokeSpeech_SendsScriptedSpeechWithSpeakTag()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);

            bool accepted = character.NarrativeDesign.InvokeSpeech("Welcome trainee");

            Assert.IsTrue(accepted);
            Assert.AreEqual(1, _connectionService.SentNarrativeTriggers.Count);
            ConvaiNarrativeTriggerRequest request = _connectionService.SentNarrativeTriggers[0];
            Assert.AreEqual(ConvaiNarrativeTriggerMode.ScriptedSpeech, request.Mode);
            Assert.AreEqual("trigger_message", request.WireFieldName);
            Assert.AreEqual("<speak>Welcome trainee</speak>", request.WireFieldValue);
        }

        [Test]
        public void NarrativeSectionChanged_ForCharacter_UpdatesCurrentSectionAndRaisesEvents()
        {
            ConvaiCharacter character = CreateCharacter();
            int sectionChangedCount = 0;
            int sectionDataCount = 0;
            NarrativeSectionData receivedData = null;

            character.NarrativeDesign.OnSectionChanged += (_, _) => sectionChangedCount++;
            character.NarrativeDesign.OnSectionDataReceived += data =>
            {
                sectionDataCount++;
                receivedData = data;
            };

            _eventHub.Publish(NarrativeSectionChanged.Create(
                "section-1",
                character.CharacterId,
                "participant-char-1",
                "bt-code",
                "{\"mood\":\"calm\"}"));

            Assert.AreEqual("section-1", character.NarrativeDesign.CurrentSectionData.SectionId);
            Assert.AreEqual("bt-code", character.NarrativeDesign.CurrentSectionData.BehaviorTreeCode);
            Assert.AreEqual("{\"mood\":\"calm\"}", character.NarrativeDesign.CurrentSectionData.BehaviorTreeConstants);
            Assert.AreEqual(1, sectionChangedCount);
            Assert.AreEqual(1, sectionDataCount);
            Assert.AreSame(character.NarrativeDesign.CurrentSectionData, receivedData);
        }

        [Test]
        public void NarrativeSectionChanged_ForDifferentCharacter_IsIgnored()
        {
            ConvaiCharacter character = CreateCharacter();
            int sectionChangedCount = 0;

            character.NarrativeDesign.OnSectionChanged += (_, _) => sectionChangedCount++;

            _eventHub.Publish(NarrativeSectionChanged.Create("section-1", "other-character", "participant-other"));

            Assert.IsNull(character.NarrativeDesign.CurrentSectionData);
            Assert.AreEqual(0, sectionChangedCount);
        }

        [Test]
        public void NarrativeDesignTriggerComponent_SelectedSavedTrigger_SendsNameOnly()
        {
            ConvaiCharacter character = CreateCharacter();
            MakeReady(character);
            ConvaiNarrativeDesignTrigger trigger = CreateNarrativeTriggerComponent(character);
            trigger.SetAvailableTriggers(new List<TriggerData>
            {
                new("trigger-1", "start_interview", "Saved trigger message", "section-1", character.CharacterId)
            });
            trigger.SelectTriggerByIndex(0);

            bool accepted = trigger.InvokeTrigger();

            Assert.IsTrue(accepted);
            Assert.AreEqual(1, _connectionService.SentNarrativeTriggers.Count);
            ConvaiNarrativeTriggerRequest request = _connectionService.SentNarrativeTriggers[0];
            Assert.AreEqual(ConvaiNarrativeTriggerMode.SavedTrigger, request.Mode);
            Assert.AreEqual("trigger_name", request.WireFieldName);
            Assert.AreEqual("start_interview", request.WireFieldValue);
        }

        [Test]
        public void NarrativeDesignTriggerComponent_SetAvailableTriggers_ReselectsByTriggerId()
        {
            ConvaiCharacter character = CreateCharacter();
            ConvaiNarrativeDesignTrigger trigger = CreateNarrativeTriggerComponent(character);
            trigger.SetAvailableTriggers(new List<TriggerData>
            {
                new("trigger-1", "old_name", "Old message", "section-1", character.CharacterId)
            });
            trigger.SelectTriggerByIndex(0);

            trigger.SetAvailableTriggers(new List<TriggerData>
            {
                new("trigger-2", "other", "Other message", "section-2", character.CharacterId),
                new("trigger-1", "new_name", "New message", "section-3", character.CharacterId)
            });

            Assert.AreEqual(1, trigger.SelectedTriggerIndex);
            Assert.AreEqual("trigger-1", trigger.TriggerId);
            Assert.AreEqual("new_name", trigger.TriggerName);
            Assert.IsTrue(string.IsNullOrEmpty(trigger.TriggerMessage));
        }

        [Test]
        public void NarrativeDesignTriggerComponent_SetAvailableTriggers_ClearsSelectionWhenTriggerMissing()
        {
            ConvaiCharacter character = CreateCharacter();
            ConvaiNarrativeDesignTrigger trigger = CreateNarrativeTriggerComponent(character);
            trigger.SetAvailableTriggers(new List<TriggerData>
            {
                new("trigger-1", "old_name", "Old message", "section-1", character.CharacterId)
            });
            trigger.SelectTriggerByIndex(0);

            trigger.SetAvailableTriggers(new List<TriggerData>
            {
                new("trigger-2", "other", "Other message", "section-2", character.CharacterId)
            });

            Assert.AreEqual(-1, trigger.SelectedTriggerIndex);
            Assert.IsTrue(string.IsNullOrEmpty(trigger.TriggerId));
            Assert.IsTrue(string.IsNullOrEmpty(trigger.TriggerName));
            Assert.IsTrue(string.IsNullOrEmpty(trigger.TriggerMessage));
        }

        private ConvaiCharacter CreateCharacter(string characterId = "char-1")
        {
            var go = new GameObject($"ConvaiCharacterNarrativeDesignTests_{characterId}");
            _createdObjects.Add(go);

            var character = go.AddComponent<ConvaiCharacter>();
            character.Configure(characterId, "Narrative Character");
            character.InjectDependencies(new ConvaiCharacterDependencies(
                _eventHub,
                _connectionService,
                _audioService,
                _agentRegistry,
                _logger));
            _agentRegistry.RegisterCharacter(character);
            return character;
        }

        private ConvaiNarrativeDesignTrigger CreateNarrativeTriggerComponent(ConvaiCharacter character)
        {
            var go = new GameObject($"ConvaiNarrativeDesignTriggerTests_{character.CharacterId}");
            _createdObjects.Add(go);
            var trigger = go.AddComponent<ConvaiNarrativeDesignTrigger>();
            trigger.SetCharacter(character);
            return trigger;
        }

        private void MakeReady(ConvaiCharacter character)
        {
            _connectionService.RaiseConnected();
            _eventHub.Publish(CharacterReady.Create(character.CharacterId, $"participant-{character.CharacterId}"));
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
