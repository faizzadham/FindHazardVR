using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Convai.Domain.Abstractions;
using Convai.Domain.Emotion;
using Convai.Domain.Narrative;
using Convai.Modules.Narrative;
using Convai.Infrastructure.Services;
using Convai.RestAPI.Internal.Models;
using Convai.Runtime;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.DynamicContext;
using Convai.Tests.EditMode.Fixtures;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class NarrativeDesignDataServiceReadContextTests
    {
        private const string CharacterId = "00000000-0000-4000-8000-000000000001";
        private static readonly FieldInfo SettingsInstance = typeof(ConvaiSettings)
            .GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        private ConvaiSettings _previousSettings;
        private ConvaiSettings _settings;
        private NarrativeServer _server;
        private GameObject _characterObject;

        [SetUp]
        public void SetUp()
        {
            _previousSettings = (ConvaiSettings)SettingsInstance.GetValue(null);
            _server = new NarrativeServer();
            _settings = ScriptableObject.CreateInstance<ConvaiSettings>();
            SetField(_settings, "_apiMode", ConvaiApiMode.Upgraded);
            SetField(_settings, "_apiEnvironment", ConvaiApiEnvironment.Custom);
            SetField(_settings, "_customCharacterApiBaseUrl", _server.BaseUrl);
            SetField(_settings, "_customRestBaseUrl", _server.BaseUrl);
            SetField(_settings, "_characterWorkspaceId", "workspace");
            SettingsInstance.SetValue(null, _settings);
        }

        [TearDown]
        public async Task TearDown()
        {
            SettingsInstance.SetValue(null, _previousSettings);
            if (_characterObject != null) Object.DestroyImmediate(_characterObject);
            if (_settings != null) Object.DestroyImmediate(_settings);
            if (_server != null)
                await AsyncTestDeadline.WithinAsync(_server.StopAsync(), "The loopback Narrative server must stop.");
        }

        [Test]
        public async Task Service_OnlyProbesCharacterWhenAuthoringIsExplicit(
            [Values("sections", "triggers", "all")] string operation,
            [Values(false, true)] bool useDraft)
        {
            // Runtime Narrative access must work even if Character metadata access is denied.
            _server.AllowCharacterMetadata = useDraft;
            var service = useDraft
                ? new NarrativeDesignDataService(() => "key", useDraft: true)
                : new NarrativeDesignDataService(() => "key");
            if (operation != "triggers")
            {
                var sections = await CompleteAsync(service.FetchSectionsAsync(CharacterId));
                Assert.That(sections.Success, Is.True, sections.Error);
                Assert.That(sections.Data, Has.Count.EqualTo(1));
                Assert.That(sections.Data[0].SectionId, Is.EqualTo("section"));
                Assert.That(sections.Data[0].SectionName, Is.EqualTo(useDraft ? "Draft" : "Bare"));
            }
            if (operation != "sections")
            {
                var triggers = await CompleteAsync(service.FetchTriggersAsync(CharacterId));
                Assert.That(triggers.Success, Is.True, triggers.Error);
                Assert.That(triggers.Data, Has.Count.EqualTo(1));
                Assert.That(triggers.Data[0].TriggerId, Is.EqualTo("trigger"));
                Assert.That(triggers.Data[0].TriggerName, Is.EqualTo("Continue"));
                Assert.That(triggers.Data[0].TriggerMessage, Is.EqualTo("Next"));
                Assert.That(triggers.Data[0].DestinationSection, Is.EqualTo("section"));
            }
            AssertRequests(useDraft, operation == "all" ? 2 : 1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ManagerSync_PreservesRuntimeDefaultAndForwardsAuthoringIntent(bool useDraft)
        {
            _server.AllowCharacterMetadata = useDraft;
            _characterObject = new GameObject("Narrative context test");
            var manager = _characterObject.AddComponent<ConvaiNarrativeDesignManager>();
            manager.SetCharacter(new BareCharacterAgent());

            var config = new UnitySectionEventConfig("section", "Previous");
            int started = 0, ended = 0;
            config.OnSectionStart.AddListener(() => started++);
            config.OnSectionEnd.AddListener(() => ended++);
            SetField(manager, "_sectionConfigs", new List<UnitySectionEventConfig> { config });
            // Keep the existing async method usable as a method group.
            Func<string, Task<SectionSyncResult>> runtimeSync = manager.FetchAndSyncFromBackendAsync;
            SectionSyncResult result = await CompleteAsync(useDraft
                ? manager.FetchAndSyncFromBackendAsync(useDraft: true, apiKey: "key")
                : runtimeSync("key"));

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(manager.SectionConfigs, Has.Count.EqualTo(1));
            Assert.That(manager.SectionConfigs[0].SectionName, Is.EqualTo(useDraft ? "Draft" : "Bare"));
            Assert.That(manager.SectionConfigs[0], Is.SameAs(config));
            Assert.That(config.SectionId, Is.EqualTo("section"));
            config.OnSectionStart.Invoke();
            config.OnSectionEnd.Invoke();
            Assert.That(started, Is.EqualTo(1));
            Assert.That(ended, Is.EqualTo(1));
            AssertRequests(useDraft, 1);
            Assert.That(manager.LastSyncedCharacterId, Is.EqualTo(CharacterId));
            Assert.That(manager.LastSyncedCharacterReference, Is.EqualTo(CharacterId));
        }

        [Test]
        public async Task Service_ExplicitVersionPreservesSelectionWithoutDraftProbe(
            [Values("1.0", "2.3.4", "api-release-reference")] string selector,
            [Values(false, true)] bool useDraft)
        {
            string reference = CharacterId + "-" + selector;
            var service = new NarrativeDesignDataService(() => "key", useDraft);
            var sections = await CompleteAsync(service.FetchSectionsAsync(reference));
            var triggers = await CompleteAsync(service.FetchTriggersAsync(reference));

            Assert.That(sections.Success, Is.True, sections.Error);
            Assert.That(triggers.Success, Is.True, triggers.Error);
            AssertSelectedRequests(reference, 2);
            Assert.That(ConvaiRestOptionsFactory.CreateForCharacterRead(reference, "key", useDraft)
                .CharacterApiV2.UseDraft, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Service_SelectedLiveStaysBare_WithoutAuthoringProbeOrRewrite(bool useDraft)
        {
            var service = new NarrativeDesignDataService(() => "key", useDraft,
                preserveSelectedReference: true);

            var sections = await CompleteAsync(service.FetchSectionsAsync(CharacterId));
            var triggers = await CompleteAsync(service.FetchTriggersAsync(CharacterId));

            Assert.That(sections.Success, Is.True, sections.Error);
            Assert.That(triggers.Success, Is.True, triggers.Error);
            AssertSelectedRequests(CharacterId, 2);
            Assert.That(ConvaiRestOptionsFactory.CreateForCharacterRead(CharacterId, "key", useDraft,
                preserveSelectedReference: true).CharacterApiV2.UseDraft, Is.False);
        }

        [TestCase("live")]
        [TestCase("1.0")]
        public async Task CharacterFacade_UsesSelectedReferenceAndKeepsStableIdentity(string selector)
        {
            ConvaiCharacter character = CreateCharacter(selector);
            var sections = await CompleteAsync(character.NarrativeDesign.FetchSectionsAsync("key"));
            var triggers = await CompleteAsync(character.NarrativeDesign.FetchTriggersAsync("key"));

            Assert.That(sections.Success, Is.True, sections.Error);
            Assert.That(triggers.Success, Is.True, triggers.Error);
            Assert.That(character.CharacterId, Is.EqualTo(CharacterId));
            AssertSelectedRequests(character.CharacterReference, 2);
        }

        [TestCase("live")]
        [TestCase("1.0")]
        public async Task ManagerSync_UsesSelectedVersionEvenForAuthoringRequest(string selector)
        {
            ConvaiCharacter character = CreateCharacter(selector);
            var manager = _characterObject.AddComponent<ConvaiNarrativeDesignManager>();
            manager.SetCharacter(character);
            var trigger = _characterObject.AddComponent<ConvaiNarrativeDesignTrigger>();
            SetField(trigger, "_characterComponent", character);

            var result = await CompleteAsync(manager.FetchAndSyncFromBackendAsync(useDraft: true, apiKey: "key"));

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(manager.GetCharacterId(), Is.EqualTo(CharacterId));
            Assert.That(manager.GetCharacterReadReference(), Is.EqualTo(character.CharacterReference));
            Assert.That(trigger.GetCharacterId(), Is.EqualTo(CharacterId));
            Assert.That(trigger.GetCharacterReadReference(), Is.EqualTo(character.CharacterReference));
            Assert.That(manager.LastSyncedCharacterId, Is.EqualTo(CharacterId));
            Assert.That(manager.LastSyncedCharacterReference, Is.EqualTo(character.CharacterReference));
            AssertSelectedRequests(character.CharacterReference, 1);

            manager.ClearAllSectionConfigs();
            Assert.That(manager.LastSyncedCharacterReference, Is.Null);
        }

        [Test]
        public async Task ManagerSync_SerializedLiveCharacterBeforeRuntimeBinding_NeverRewritesToDraft()
        {
            ConvaiCharacter character = CreateCharacter("live");
            var manager = _characterObject.AddComponent<ConvaiNarrativeDesignManager>();
            SetField(manager, "_characterComponent", character);

            var result = await CompleteAsync(manager.FetchAndSyncFromBackendAsync(useDraft: true, apiKey: "key"));

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(manager.LastSyncedCharacterReference, Is.EqualTo(CharacterId));
            AssertSelectedRequests(CharacterId, 1);
        }

        [TestCase("1.0")]
        [TestCase("api-release-reference")]
        public async Task VersionedReads_InLegacyMode_FailBeforeSendingRequests(string selector)
        {
            SetField(_settings, "_apiMode", ConvaiApiMode.Legacy);
            ConvaiCharacter character = CreateCharacter(selector);
            var service = new NarrativeDesignDataService(() => "key");
            var sections = await CompleteAsync(service.FetchSectionsAsync(character.CharacterReference));
            var triggers = await CompleteAsync(character.NarrativeDesign.FetchTriggersAsync("key"));

            Assert.That(sections.Success, Is.False);
            Assert.That(triggers.Success, Is.False);
            Assert.That(sections.Error, Does.Contain("API Mode Upgraded"));
            Assert.That(triggers.Error, Does.Contain("API Mode Upgraded"));
            Assert.That(_server.Requests, Is.Empty);
        }

        [Test]
        public async Task LiveCharacterReads_InLegacyMode_UseBareLegacyRoutes()
        {
            SetField(_settings, "_apiMode", ConvaiApiMode.Legacy);
            ConvaiCharacter character = CreateCharacter("live");

            var sections = await CompleteAsync(character.NarrativeDesign.FetchSectionsAsync("key"));
            var triggers = await CompleteAsync(character.NarrativeDesign.FetchTriggersAsync("key"));

            Assert.That(sections.Success, Is.True, sections.Error);
            Assert.That(triggers.Success, Is.True, triggers.Error);
            Assert.That(character.CharacterReference, Is.EqualTo(CharacterId));
            Assert.That(_server.Requests.Select(uri => uri.AbsolutePath), Is.EquivalentTo(new[]
            {
                "/character/narrative/list-sections", "/character/narrative/list-triggers"
            }));
        }

        [Test]
        public async Task BareServiceCalls_KeepLegacyRoutes()
        {
            SetField(_settings, "_apiMode", ConvaiApiMode.Legacy);
            var service = new NarrativeDesignDataService(() => "key");

            var sections = await CompleteAsync(service.FetchSectionsAsync(CharacterId));
            var triggers = await CompleteAsync(service.FetchTriggersAsync(CharacterId));

            Assert.That(sections.Success, Is.True, sections.Error);
            Assert.That(triggers.Success, Is.True, triggers.Error);
            Assert.That(_server.Requests.Select(uri => uri.AbsolutePath), Is.EquivalentTo(new[]
            {
                "/character/narrative/list-sections", "/character/narrative/list-triggers"
            }));
        }

        [Test]
        public async Task PinnedLabelWithoutSavedApiReference_ReturnsFailedFetchesWithoutSynchronousThrow()
        {
            ConvaiCharacter character = CreateCharacter("invalid");
            Task<NarrativeFetchResult<List<NarrativeSectionInfo>>> sections = null;
            Task<NarrativeFetchResult<List<NarrativeTriggerInfo>>> triggers = null;
            Assert.DoesNotThrow(() => sections = character.NarrativeDesign.FetchSectionsAsync("key"));
            Assert.DoesNotThrow(() => triggers = character.NarrativeDesign.FetchTriggersAsync("key"));

            Assert.That((await CompleteAsync(sections)).Success, Is.False);
            Assert.That((await CompleteAsync(triggers)).Success, Is.False);
            Assert.That(_server.Requests, Is.Empty);
        }

        [Test]
        public async Task ManagerSync_DiscardsResponseWhenSelectionChangesDuringFetch()
        {
            ConvaiCharacter character = CreateCharacter("1.0");
            var manager = _characterObject.AddComponent<ConvaiNarrativeDesignManager>();
            manager.SetCharacter(character);
            var previous = new UnitySectionEventConfig("section", "Previously synced");
            SetField(manager, "_sectionConfigs", new List<UnitySectionEventConfig> { previous });
            SetField(manager, "_lastSyncedCharacterId", CharacterId);
            SetField(manager, "_lastSyncedCharacterReference", CharacterId);
            _server.ReleaseNarrativeResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<SectionSyncResult> fetch = manager.FetchAndSyncFromBackendAsync("key");
            try
            {
                await CompleteAsync(_server.NarrativeRequestReceived.Task);
                SetField(character, "_characterVersion", "2.0");
                SetField(character, "_characterVersionReference", CharacterId + "-2.0");
            }
            finally
            {
                _server.ReleaseNarrativeResponse.TrySetResult(true);
            }
            var result = await CompleteAsync(fetch);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("selection changed"));
            Assert.That(manager.SectionConfigs[0], Is.SameAs(previous));
            Assert.That(previous.SectionName, Is.EqualTo("Previously synced"));
            Assert.That(manager.LastSyncedCharacterReference, Is.EqualTo(CharacterId));
            Assert.That(manager.IsFetching, Is.False);
        }

        [Test]
        public void TriggerCache_RecordsFetchedReferenceAndClearsUnknownProvenance()
        {
            CreateCharacter("live");
            var trigger = _characterObject.AddComponent<ConvaiNarrativeDesignTrigger>();
            var cached = new List<TriggerData>
            {
                new("trigger", "Continue", "Next", "section", CharacterId)
            };
            string reference = CharacterId + "-1.0";

            trigger.SetAvailableTriggers(cached, reference);
            trigger.SelectTriggerByIndex(0);
            Assert.That(trigger.LastFetchedCharacterReference, Is.EqualTo(reference));
            Assert.That(trigger.TriggerId, Is.EqualTo("trigger"));

            // The compatible single-argument API cannot establish which version was fetched.
            trigger.SetAvailableTriggers(cached);
            Assert.That(trigger.LastFetchedCharacterReference, Is.Null);
            Assert.That(trigger.TriggerId, Is.EqualTo("trigger"));

            trigger.SetAvailableTriggers(cached, reference);
            trigger.SetAvailableTriggers(null, reference);
            Assert.That(trigger.LastFetchedCharacterReference, Is.Null);
            Assert.That(trigger.AvailableTriggers, Is.Empty);
            Assert.That(trigger.TriggerId, Is.Null.Or.Empty);
        }

        private ConvaiCharacter CreateCharacter(string selector)
        {
            _characterObject = new GameObject("Narrative version test");
            var character = _characterObject.AddComponent<ConvaiCharacter>();
            SetField(character, "_characterId", CharacterId);
            SetField(character, "_characterVersionMode", selector == "live"
                ? ConvaiCharacterVersionMode.Live : ConvaiCharacterVersionMode.Specific);
            SetField(character, "_characterVersion", selector);
            // Live works before discovery; pins require an exact API reference, never just a label.
            SetField(character, "_characterVersionReference", selector == "live" || selector == "invalid"
                ? string.Empty : CharacterId + "-" + selector);
            return character;
        }

        private void AssertSelectedRequests(string reference, int reads)
        {
            Uri[] requests = _server.Requests.ToArray();
            Assert.That(requests, Has.Length.EqualTo(reads));
            Assert.That(requests.All(uri => uri.AbsolutePath.Contains("/narrative/")), Is.True);
            foreach (Uri uri in requests)
                Assert.That(uri.Query, Is.EqualTo("?character_id=" + reference + "&workspace_id=workspace"));
        }

        private void AssertRequests(bool useDraft, int narrativeReads)
        {
            Uri[] requests = _server.Requests.ToArray();
            Assert.That(requests.Count(uri => uri.AbsolutePath == "/character/get"),
                Is.EqualTo(useDraft ? narrativeReads : 0));
            Uri[] narrative = requests.Where(uri => uri.AbsolutePath.Contains("/narrative/")).ToArray();
            Assert.That(narrative, Has.Length.EqualTo(narrativeReads));
            foreach (Uri uri in narrative)
                Assert.That(uri.Query, Is.EqualTo("?character_id=" + CharacterId +
                    (useDraft ? "-draft" : "") + "&workspace_id=workspace"));
        }

        private static async Task<T> CompleteAsync<T>(Task<T> task)
        {
            await AsyncTestDeadline.WithinAsync(task, "The Narrative read must complete against the loopback server.");
            return await task;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }

        private sealed class NarrativeServer
        {
            private readonly HttpListener _listener = new();
            private readonly Task _serveTask;
            private volatile bool _stopping;
            public volatile bool AllowCharacterMetadata;
            public TaskCompletionSource<bool> NarrativeRequestReceived { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> ReleaseNarrativeResponse;
            public ConcurrentQueue<Uri> Requests { get; } = new();
            public string BaseUrl { get; }

            public NarrativeServer()
            {
                var portReservation = new TcpListener(IPAddress.Loopback, 0);
                portReservation.Start();
                int port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
                portReservation.Stop();
                BaseUrl = "http://127.0.0.1:" + port + "/";
                _listener.Prefixes.Add(BaseUrl);
                _listener.Start();
                _serveTask = Task.Run(ServeAsync);
            }

            public Task StopAsync()
            {
                _stopping = true;
                ReleaseNarrativeResponse?.TrySetResult(true);
                _listener.Close();
                return _serveTask;
            }

            private async Task ServeAsync()
            {
                try
                {
                    while (!_stopping)
                    {
                        HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                        Uri uri = context.Request.Url;
                        Requests.Enqueue(uri);
                        if (uri.AbsolutePath.Contains("/narrative/"))
                        {
                            NarrativeRequestReceived.TrySetResult(true);
                            if (ReleaseNarrativeResponse != null)
                                await ReleaseNarrativeResponse.Task.ConfigureAwait(false);
                        }
                        bool characterRequest = uri.AbsolutePath == "/character/get";
                        string response;
                        if (characterRequest && !AllowCharacterMetadata)
                        {
                            context.Response.StatusCode = 403;
                            response = "{\"detail\":\"Character metadata access is unavailable\"}";
                        }
                        else if (characterRequest)
                        {
                            response = @"{""user_id"":""user"",""character_id"":""00000000-0000-4000-8000-000000000001"",""character_name"":""Test"",""voice_type"":""test"",""language_code"":""en"",""description"":"""",""model_type"":"""",""is_narrative_driven"":false,""timestamp"":""2026-01-01T00:00:00Z"",""memory_settings"":{},""character_traits"":{},""metadata_filter"":{},""resolved_revision_id"":""revision"",""edit_character_access"":true}";
                        }
                        else if (uri.AbsolutePath.EndsWith("sections/list", StringComparison.Ordinal) ||
                            uri.AbsolutePath.EndsWith("list-sections", StringComparison.Ordinal))
                        {
                            var section = JObject.Parse(@"{""character_id"":""00000000-0000-4000-8000-000000000001"",""section_id"":""section"",""section_name"":""Bare"",""objective"":null,""decisions"":null,""triggers"":null,""parents"":null,""bt_constants"":null,""behavior_tree_code"":null,""node_position"":null}");
                            section["section_name"] = uri.Query.Contains("-draft") ? "Draft" : "Bare";
                            response = new JArray(section).ToString();
                        }
                        else
                        {
                            response = @"[{""character_id"":""00000000-0000-4000-8000-000000000001"",""trigger_id"":""trigger"",""trigger_name"":""Continue"",""trigger_message"":""Next"",""destination_section"":""section"",""node_position"":null}]";
                        }

                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (_stopping) { }
                catch (ObjectDisposedException) when (_stopping) { }
            }
        }

        private sealed class BareCharacterAgent : IConvaiCharacterAgent
        {
            public string CharacterId => NarrativeDesignDataServiceReadContextTests.CharacterId;
            public string CharacterName => "Custom bare character";
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext => null;
            public EmotionDetectionMode EmotionDetectionMode => default;
            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }
        }
    }
}
