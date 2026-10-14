using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi;
using Convai.RestAPI;
using Convai.RestAPI.Internal;
using Convai.RestAPI.Internal.Models;
using Convai.RestAPI.Services;
using Convai.RestAPI.Transport;
using Convai.Runtime;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RestApiIdentityAndMemoryServiceTests
    {
        private CapturingTransport _transport = null!;
        private ConvaiRestClient _client = null!;

        [SetUp]
        public void SetUp()
        {
            _transport = new CapturingTransport();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("test-api-key")
            {
                CustomTransport = _transport
            });
        }

        [TearDown]
        public void TearDown() => _client.Dispose();

        [Test]
        public async Task EndUsers_ListAsync_SendsExpectedIdentityListPayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_users\":[],\"total_count\":0,\"has_more\":false}",
                request.Url);

            await _client.EndUsers.ListAsync(limit: 25, cursor: "cursor-1", activeAfter: "2026-01-01");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured end-user list request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/user/end-users/list"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["limit"]?.Value<int>(), Is.EqualTo(25));
            Assert.That(payload["cursor"]?.Value<string>(), Is.EqualTo("cursor-1"));
            Assert.That(payload["active_after"]?.Value<string>(), Is.EqualTo("2026-01-01"));
            Assert.That(payload["active_before"], Is.Null);
        }

        [Test]
        public async Task EndUsers_ListAsync_UsesIndependentPlatformBaseUrlAndCredential()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("platform-api-key")
            {
                CustomTransport = _transport,
                Environment = ConvaiEnvironment.Beta,
                BetaBaseUrl = "https://legacy-beta.example/",
                PlatformBaseUrl = "https://platform.example/"
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_users\":[],\"total_count\":0,\"has_more\":false}",
                request.Url);

            await _client.EndUsers.ListAsync();

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected platform request.");
            Assert.That(request.Url.ToString(), Is.EqualTo("https://platform.example/user/end-users/list"));
            Assert.That(request.Headers["CONVAI-API-KEY"], Is.EqualTo("platform-api-key"));
        }

        [Test]
        public async Task EndUsers_ListAsync_UsesPlatformAuthTokenHeaderWhenConfigured()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("short-lived-token")
            {
                AuthenticationMode = ConvaiAuthenticationMode.AuthToken,
                CustomTransport = _transport,
                PlatformBaseUrl = "https://platform.example/"
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_users\":[],\"total_count\":0,\"has_more\":false}",
                request.Url);

            await _client.EndUsers.ListAsync();

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected platform request.");
            Assert.That(request.Headers["API-AUTH-TOKEN"], Is.EqualTo("short-lived-token"));
            Assert.That(request.Headers.ContainsKey("CONVAI-API-KEY"), Is.False);
        }

        [Test]
        public async Task EndUsers_UpdateMetadataAsync_SendsPatchPayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"end_user_id\":\"player-42\",\"last_active_ts\":\"\",\"last_ltm_usage_ts\":\"\",\"end_user_metadata\":{\"name\":\"Player 42\",\"rank\":4},\"message\":\"ok\"}",
                request.Url);

            await _client.EndUsers.UpdateMetadataAsync(
                "player-42",
                new Dictionary<string, object>
                {
                    ["name"] = "Player 42",
                    ["rank"] = 4
                });

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured end-user update request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/user/end-users/update"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["end_user_id"]?.Value<string>(), Is.EqualTo("player-42"));
            Assert.That(payload["end_user_metadata"]?["name"]?.Value<string>(), Is.EqualTo("Player 42"));
            Assert.That(payload["end_user_metadata"]?["rank"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(payload["speaker_id"], Is.Null);
        }

        [Test]
        public async Task Memory_ListAsync_UsesCharacterAndEndUserIdentityOnly()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"memories\":[],\"total_count\":0,\"page\":1,\"page_size\":50,\"has_more\":false}",
                request.Url);

            await _client.Memory.ListAsync("char-123", "player-42", page: 2, pageSize: 10);

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured memory list request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/memory/list"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["character_id"]?.Value<string>(), Is.EqualTo("char-123"));
            Assert.That(payload["end_user_id"]?.Value<string>(), Is.EqualTo("player-42"));
            Assert.That(payload["page"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(payload["page_size"]?.Value<int>(), Is.EqualTo(10));
            Assert.That(payload["speaker_id"], Is.Null);
        }

        [Test]
        public async Task Characters_GetMemoryEnabledAsync_ReadsCharacterMemoryFlag()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"response\":{\"character_id\":\"char-123\",\"memory_settings\":{\"enabled\":true}}}",
                request.Url);

            bool enabled = await _client.Characters.GetMemoryEnabledAsync("char-123");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character details request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/get"));
            Assert.That(enabled, Is.True);
        }

        [Test]
        public async Task Characters_GetMemoryEnabledAsync_UsesConfiguredEnvironmentBaseUrl()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("test-api-key")
            {
                CustomTransport = _transport,
                Environment = ConvaiEnvironment.Beta,
                BetaBaseUrl = "https://beta.example.convai/"
            });

            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"response\":{\"character_id\":\"char-123\",\"memory_settings\":{\"enabled\":false}}}",
                request.Url);

            await _client.Characters.GetMemoryEnabledAsync("char-123");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character details request.");

            Assert.That(request.Url.ToString(), Is.EqualTo("https://beta.example.convai/character/get"));
        }

        [Test]
        public async Task Characters_GetDetailsAsync_CharacterV2AdapterUsesCanonicalGetWithoutAuthoringClient()
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("legacy-key")
            {
                CustomTransport = _transport,
                CharacterApiV2 = ConvaiCharacterApiOptions.Preview("character-v2-key")
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"user_id\":\"user-1\",\"character_id\":\"character-42\"," +
                "\"character_name\":\"Test\",\"voice_type\":\"test\",\"language_code\":\"en\"," +
                "\"description\":\"\",\"model_type\":\"\",\"is_narrative_driven\":false," +
                "\"timestamp\":\"2026-01-01T00:00:00Z\",\"memory_settings\":{}," +
                "\"character_traits\":{},\"metadata_filter\":{}}",
                request.Url);

            CharacterDetails details = await _client.Characters.GetDetailsAsync("character-42");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected Character v2 request.");
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/get"));
            Assert.That(request.Url.Query, Is.EqualTo("?character_id=character-42"));
            Assert.That(request.Headers["CONVAI-API-KEY"], Is.EqualTo("character-v2-key"));
            Assert.That(details.CharacterID, Is.EqualTo("character-42"));
        }

        [Test]
        public async Task Characters_GetDetailsAsync_ProjectFactoryUsesProductionCharacterApi()
        {
            _client.Dispose();
            ConvaiRestClientOptions options = ConvaiRestOptionsFactory.Create(
                "production-key",
                ConvaiApiEnvironment.Production,
                null, ConvaiApiMode.Upgraded);
            options.CustomTransport = _transport;
            _client = new ConvaiRestClient(options);
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"user_id\":\"user-1\",\"character_id\":\"character-42\"," +
                "\"character_name\":\"Test\",\"voice_type\":\"test\",\"language_code\":\"en\"," +
                "\"description\":\"\",\"model_type\":\"\",\"is_narrative_driven\":false," +
                "\"timestamp\":\"2026-01-01T00:00:00Z\",\"memory_settings\":{}," +
                "\"character_traits\":{},\"metadata_filter\":{}}",
                request.Url);

            await _client.Characters.GetDetailsAsync("character-42");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected Character API request.");
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.Url.Host, Is.EqualTo("api2.convai.com"));
            Assert.That(request.Url.PathAndQuery, Is.EqualTo("/character/get?character_id=character-42"));
            Assert.That(request.Headers["CONVAI-API-KEY"], Is.EqualTo("production-key"));
        }

        [Test]
        public async Task Characters_GetDetailsAsync_ProjectFactoryLegacyModeUsesLegacyApi()
        {
            _client.Dispose();
            ConvaiRestClientOptions options = ConvaiRestOptionsFactory.Create(
                "legacy-key",
                ConvaiApiEnvironment.Production,
                null,
                ConvaiApiMode.Legacy);
            options.CustomTransport = _transport;
            _client = new ConvaiRestClient(options);
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(
                HttpStatusCode.OK,
                "{\"response\":{\"character_id\":\"character-42\"}}",
                request.Url);

            await _client.Characters.GetDetailsAsync("character-42");

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected legacy Character API request.");
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Url.Host, Is.EqualTo("api.convai.com"));
            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/get"));
            Assert.That(JObject.Parse(request.Body ?? "{}")["charID"]?.Value<string>(),
                Is.EqualTo("character-42"));
        }

        [Test]
        public async Task Narrative_ReadsUseProductionCanonicalGetRoutes()
        {
            UseProjectFactory(ConvaiApiMode.Upgraded);
            _transport.ResponseFactory = request => request.Url.AbsolutePath.EndsWith("sections/list")
                ? ConvaiHttpResponse.Success(
                    HttpStatusCode.OK,
                    "[{\"character_id\":\"character-42\",\"section_id\":\"section-1\"," +
                    "\"section_name\":\"Opening\",\"objective\":\"Greet\",\"decisions\":[]," +
                    "\"triggers\":[],\"parents\":[],\"updated_character_data\":{}," +
                    "\"bt_constants\":{},\"behavior_tree_code\":\"\",\"node_position\":[1,2]}]",
                    request.Url)
                : ConvaiHttpResponse.Success(
                    HttpStatusCode.OK,
                    "[{\"character_id\":\"character-42\",\"trigger_id\":\"trigger-1\"," +
                    "\"trigger_name\":\"Start\",\"trigger_message\":\"Begin\"," +
                    "\"destination_section\":\"section-1\",\"node_position\":[3,4]}]",
                    request.Url);

            IReadOnlyList<SectionData> sections = await _client.Narrative.GetSectionsAsync("character-42");
            ConvaiHttpRequest sectionsRequest = _transport.LastRequest!;
            IReadOnlyList<TriggerData> triggers = await _client.Narrative.GetTriggersAsync("character-42");
            ConvaiHttpRequest triggersRequest = _transport.LastRequest!;

            Assert.That(sectionsRequest.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(sectionsRequest.Url.Host, Is.EqualTo("api2.convai.com"));
            Assert.That(sectionsRequest.Url.PathAndQuery,
                Is.EqualTo("/character/narrative/sections/list?character_id=character-42"));
            Assert.That(sections[0].SectionId, Is.EqualTo("section-1"));
            Assert.That(triggersRequest.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(triggersRequest.Url.PathAndQuery,
                Is.EqualTo("/character/narrative/triggers/list?character_id=character-42"));
            Assert.That(triggers[0].TriggerId, Is.EqualTo("trigger-1"));
        }

        [TestCase(ConvaiApiMode.Legacy)]
        [TestCase(ConvaiApiMode.Upgraded)]
        public async Task Animations_AlwaysKeepSignedMediaAndServerPaginationOnLegacy(ConvaiApiMode mode)
        {
            UseProjectFactory(mode);
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK,
                request.Url.AbsolutePath.EndsWith("animations/list")
                    ? "{\"animations\":[],\"transaction_id\":\"tx\",\"total_pages\":4,\"page\":2,\"total\":180}"
                    : "{\"transaction_id\":\"tx\",\"animation\":{\"animation_id\":\"a\",\"fbx_gcp_file\":\"https://files.example/a.fbx\"}}",
                request.Url);
            var list = await _client.Animations.GetListAsync(2, "success");
            var listRequest = _transport.LastRequest!;
            var details = await _client.Animations.GetAsync("a");
            Assert.That(listRequest.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(listRequest.Url.Host, Is.EqualTo("api.convai.com"));
            var body = JObject.Parse(listRequest.Body!);
            Assert.That(body.Value<string>("page"), Is.EqualTo("2"));
            Assert.That(body.Value<string>("status"), Is.EqualTo("success"));
            Assert.That(body.Value<string>("generate_signed_urls"), Is.EqualTo("true"));
            Assert.That(list.TotalPages, Is.EqualTo(4));
            Assert.That(_transport.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(details.Animation.FbxGcpFile, Is.EqualTo("https://files.example/a.fbx"));
        }

        [Test]
        public async Task NarrativeAndAnimationReads_LegacyModePreservesLegacyPostRoutes()
        {
            UseProjectFactory(ConvaiApiMode.Legacy);
            _transport.ResponseFactory = request =>
                request.Url.AbsolutePath.EndsWith("list-sections") ||
                request.Url.AbsolutePath.EndsWith("list-triggers")
                ? ConvaiHttpResponse.Success(HttpStatusCode.OK, "[]", request.Url)
                : request.Url.AbsolutePath.EndsWith("animations/get")
                    ? ConvaiHttpResponse.Success(
                        HttpStatusCode.OK,
                        "{\"transaction_id\":\"tx\",\"animation\":{" +
                        "\"animation_id\":\"animation-1\",\"user_id\":\"user-1\"," +
                        "\"animation_name\":\"Wave\",\"status\":\"success\"," +
                        "\"csv_gcp_file\":\"\",\"fbx_gcp_file\":\"\"," +
                        "\"thumbnail_gcp_file\":\"\",\"retry_count\":0," +
                        "\"animation_videos\":{\"fpv_video\":\"\",\"tpv_video\":\"\"}," +
                        "\"created_at\":\"2026-01-01T00:00:00Z\"}," +
                        "\"upload_urls\":{\"fpv_video\":\"\",\"tpv_video\":\"\"}}",
                        request.Url)
                : ConvaiHttpResponse.Success(
                    HttpStatusCode.OK,
                    "{\"animations\":[],\"transaction_id\":\"tx\",\"total_pages\":1," +
                    "\"page\":1,\"total\":0}",
                    request.Url);

            await _client.Narrative.GetSectionsAsync("character-42");
            ConvaiHttpRequest sectionsRequest = _transport.LastRequest!;
            await _client.Narrative.GetTriggersAsync("character-42");
            ConvaiHttpRequest triggersRequest = _transport.LastRequest!;
            await _client.Animations.GetListAsync(1, "success");
            ConvaiHttpRequest animationListRequest = _transport.LastRequest!;
            await _client.Animations.GetAsync("animation-1");
            ConvaiHttpRequest animationGetRequest = _transport.LastRequest!;

            Assert.That(sectionsRequest.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(sectionsRequest.Url.AbsolutePath,
                Is.EqualTo("/character/narrative/list-sections"));
            Assert.That(triggersRequest.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(triggersRequest.Url.AbsolutePath,
                Is.EqualTo("/character/narrative/list-triggers"));
            Assert.That(animationListRequest.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(animationListRequest.Url.AbsolutePath, Is.EqualTo("/animations/list"));
            Assert.That(animationGetRequest.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(animationGetRequest.Url.AbsolutePath, Is.EqualTo("/animations/get"));
        }

        [Test]
        public async Task Characters_UpdateAsync_MergesCharacterIdIntoPayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);

            await _client.Characters.UpdateAsync(
                "char-123",
                new Dictionary<string, object>
                {
                    ["displayName"] = "Guide"
                });

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character update request.");
            JObject payload = JObject.Parse(request.Body ?? "{}");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/update"));
            Assert.That(payload["charID"]?.Value<string>(), Is.EqualTo("char-123"));
            Assert.That(payload["displayName"]?.Value<string>(), Is.EqualTo("Guide"));
        }

        private void UseProjectFactory(ConvaiApiMode apiMode)
        {
            _client.Dispose();
            ConvaiRestClientOptions options = ConvaiRestOptionsFactory.Create(
                "test-api-key",
                ConvaiApiEnvironment.Production,
                null,
                apiMode);
            options.CustomTransport = _transport;
            _client = new ConvaiRestClient(options);
        }

        [Test]
        public async Task Characters_UpdateAsync_CharacterIdParameterWinsOverPayloadCharId()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);

            await _client.Characters.UpdateAsync(
                "char-authoritative",
                new Dictionary<string, object>
                {
                    ["charID"] = "char-wrong",
                    ["displayName"] = "Guide"
                });

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character update request.");
            JObject payload = JObject.Parse(request.Body ?? "{}");

            Assert.That(payload["charID"]?.Value<string>(), Is.EqualTo("char-authoritative"));
        }

        [Test]
        public async Task Characters_SetMemoryEnabledAsync_SendsFocusedUpdatePayload()
        {
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);

            await _client.Characters.SetMemoryEnabledAsync("char-123", enabled: true);

            ConvaiHttpRequest request = _transport.LastRequest
                                        ?? throw new AssertionException("Expected captured character update request.");

            Assert.That(request.Url.AbsolutePath, Is.EqualTo("/character/update"));
            JObject payload = JObject.Parse(request.Body ?? "{}");
            Assert.That(payload["charID"]?.Value<string>(), Is.EqualTo("char-123"));
            Assert.That(payload["memorySettings"]?["enabled"]?.Value<bool>(), Is.True);
        }

        [TestCase(false, "")]
        [TestCase(true, "-draft")]
        public async Task Narrative_ReadContextPreservesIdentityAndNullableBackendFields(bool draft, string suffix)
        {
            const string characterId = "00000000-0000-4000-8000-000000000001";
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("key")
            {
                CustomTransport = _transport,
                CharacterApiV2 = ConvaiCharacterApiOptions.Production("key").WithReadContext(" workspace-1 ", draft)
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Success(HttpStatusCode.OK,
                request.Url.AbsolutePath == "/character/get" ? CharacterReadJson(editable: true, initialized: true) :
                request.Url.AbsolutePath.EndsWith("sections/list")
                    ? @"[{""character_id"":""canonical"",""section_id"":""s1"",""section_name"":""Opening"",""objective"":""Greet"",""bt_constants"":null,""decisions"":[{""criteria"":""*"",""next_section_id"":""s2""}],""triggers"":[{""trigger_id"":""t1"",""destination_section"":""s2""}],""parents"":null,""updated_character_data"":null,""behavior_tree_code"":null,""node_position"":null}]"
                    : @"[{""character_id"":""canonical"",""trigger_id"":""t1"",""trigger_name"":""Start"",""trigger_message"":""Begin"",""destination_section"":null,""node_position"":null}]",
                request.Url);
            var sections = await _client.Narrative.GetSectionsAsync(characterId);
            Assert.That(_transport.LastRequest!.Url.Query,
                Is.EqualTo("?character_id=" + characterId + suffix + "&workspace_id=workspace-1"));
            Assert.That(sections[0].CharacterId, Is.EqualTo("canonical"));
            Assert.That(sections[0].BehaviorTreeConstants, Is.Null);
            Assert.That(sections[0].GetDecisions()[0].NextSectionId, Is.EqualTo("s2"));
            Assert.That(sections[0].GetTriggerOverrides()[0].TriggerId, Is.EqualTo("t1"));
            Assert.That(sections[0].GetUpdatedCharacterDataDict(), Is.Empty);
            var triggers = await _client.Narrative.GetTriggersAsync(characterId + "-1.0");
            Assert.That(_transport.LastRequest!.Url.Query, Does.Contain(characterId + "-1.0"));
            Assert.That(_transport.LastRequest.Url.Query, Does.Not.Contain("-1.0-draft"));
            Assert.That(triggers[0].CharacterId, Is.EqualTo("canonical"));
            Assert.That(triggers[0].TriggerMessage, Is.EqualTo("Begin"));
            Assert.That(triggers[0].DestinationSection, Is.Empty);
        }

        [TestCase(false, false, 1)]
        [TestCase(false, true, 1)]
        [TestCase(true, false, 1)]
        [TestCase(true, true, 2)]
        public async Task AuthoringCharacter_ChoosesAccessibleDraftWithoutBreakingPublicOrUnversionedReads(
            bool editable, bool initialized, int expectedRequests)
        {
            const string id = "00000000-0000-4000-8000-000000000001";
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("key")
            {
                CustomTransport = _transport,
                CharacterApiV2 = ConvaiCharacterApiOptions.Production("key").WithReadContext("workspace", useDraft: true)
            });
            _transport.ResponseFactory = request =>
            {
                Assert.That(request.Url.Query, Does.Contain("workspace_id=workspace"));
                return ConvaiHttpResponse.Success(HttpStatusCode.OK, CharacterReadJson(editable, initialized), request.Url);
            };
            var result = await _client.Characters.GetDetailsAsync(id);
            Assert.That(_transport.RequestCount, Is.EqualTo(expectedRequests));
            Assert.That(_transport.LastRequest!.Url.Query,
                Does.Contain("character_id=" + id + (expectedRequests == 2 ? "-draft&" : "&")));
            Assert.That(result.CharacterID, Is.EqualTo(id));
        }

        [TestCase("-draft")]
        [TestCase("-latest")]
        [TestCase("-1.0")]
        public void ExplicitReference_PreservesBackendErrorWithoutProbingOrRetrying(string selector)
        {
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("key")
            {
                CustomTransport = _transport,
                CharacterApiV2 = ConvaiCharacterApiOptions.Production("key").WithReadContext(null, useDraft: true)
            });
            _transport.ResponseFactory = request => ConvaiHttpResponse.Failure(HttpStatusCode.Conflict,
                @"{""code"":""character_versioning_not_initialized""}", request.Url);
            Assert.ThrowsAsync<ConvaiRestException>(() =>
                _client.Characters.GetDetailsAsync("00000000-0000-4000-8000-000000000001" + selector));
            Assert.That(_transport.RequestCount, Is.EqualTo(1));
        }

        [Test]
        public async Task MemoryReadAndWrite_StayOnTheSameMutableLegacyRecordInUpgradedMode()
        {
            UseProjectFactory(ConvaiApiMode.Upgraded);
            bool enabled = false;
            _transport.ResponseFactory = request =>
            {
                Assert.That(request.Url.Host, Is.EqualTo("api.convai.com"));
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
                if (request.Url.AbsolutePath.EndsWith("update"))
                    enabled = JObject.Parse(request.Body!).SelectToken("memorySettings.enabled")!.Value<bool>();
                return ConvaiHttpResponse.Success(HttpStatusCode.OK,
                    new JObject { ["memory_settings"] = new JObject { ["enabled"] = enabled } }.ToString(), request.Url);
            };
            await _client.Characters.SetMemoryEnabledAsync("character", true);
            Assert.That(await _client.Characters.GetMemoryEnabledAsync("character"), Is.True);
        }

        private static string CharacterReadJson(bool editable, bool initialized)
        {
            var json = JObject.Parse(@"{""user_id"":""u"",""character_id"":""00000000-0000-4000-8000-000000000001"",""character_name"":""Test"",""voice_type"":""test"",""language_code"":""en"",""description"":"""",""model_type"":"""",""is_narrative_driven"":false,""timestamp"":""2026-01-01T00:00:00Z"",""memory_settings"":{},""character_traits"":{},""metadata_filter"":{}}");
            json["edit_character_access"] = editable;
            json["resolved_revision_id"] = initialized ? "revision" : null;
            return json.ToString();
        }

        [TestCase(401, ConvaiRestErrorCategory.Authentication)]
        [TestCase(409, ConvaiRestErrorCategory.Conflict)]
        [TestCase(422, ConvaiRestErrorCategory.BadRequest)]
        [TestCase(429, ConvaiRestErrorCategory.RateLimited)]
        [TestCase(500, ConvaiRestErrorCategory.ServerError)]
        public void UpgradedRead_PreservesErrorBodyAndHeadersWithoutLegacyRetry(int status, ConvaiRestErrorCategory category)
        {
            UseProjectFactory(ConvaiApiMode.Upgraded);
            _transport.ResponseFactory = request => ConvaiHttpResponse.Failure((HttpStatusCode)status,
                @"{""detail"":""backend failure""}", request.Url, headers: new Dictionary<string, string>
                {
                    ["x-request-id"] = "trace-123",
                    ["Retry-After"] = "17"
                });
            var error = Assert.ThrowsAsync<ConvaiRestException>(() => _client.Narrative.GetSectionsAsync("character"));
            Assert.That(error!.Category, Is.EqualTo(category));
            Assert.That(error.StatusCodeInt, Is.EqualTo(status));
            Assert.That(error.ResponseBody, Does.Contain("backend failure"));
            Assert.That(error.RequestTraceId, Is.EqualTo("trace-123"));
            Assert.That(error.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(17)));
            Assert.That(error.Url, Is.EqualTo(_transport.LastRequest!.Url));
            Assert.That(_transport.RequestCount, Is.EqualTo(1));
        }

        [Test]
        public void UpgradedRead_FailuresPreserveExactRequestUri(
            [Values("character", "sections", "triggers")] string resource,
            [Values("", "-draft", "-1.0")] string suffix,
            [Values("http", "parse", "transport", "timeout")] string failure)
        {
            const string characterId = "00000000-0000-4000-8000-000000000001";
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("key")
            {
                CustomTransport = _transport,
                CharacterApiV2 = ConvaiCharacterApiOptions.Production("key")
                    .WithReadContext("workspace & 1", useDraft: suffix == "-draft")
            });
            _transport.ResponseFactory = request =>
            {
                if (suffix == "-draft" && _transport.RequestCount == 1)
                    return ConvaiHttpResponse.Success(HttpStatusCode.OK,
                        CharacterReadJson(editable: true, initialized: true), request.Url);

                return failure switch
                {
                    "http" => ConvaiHttpResponse.Failure(HttpStatusCode.NotFound, "{}", request.Url),
                    "parse" => ConvaiHttpResponse.Success(HttpStatusCode.OK, "not-json", request.Url),
                    "timeout" => ConvaiHttpResponse.TransportFailure(request.Url, "deadline", isTimeout: true),
                    _ => ConvaiHttpResponse.TransportFailure(request.Url, "connection failed")
                };
            };

            var error = Assert.ThrowsAsync<ConvaiRestException>(() =>
                ReadUpgradedResourceAsync(resource, characterId + (suffix == "-1.0" ? suffix : "")));

            string path = resource == "character"
                ? "character/get"
                : "character/narrative/" + resource + "/list";
            var expectedUri = new Uri("https://api2.convai.com/" + path +
                                      "?character_id=" + characterId + suffix +
                                      "&workspace_id=workspace%20%26%201");
            Assert.That(_transport.LastRequest!.Url, Is.EqualTo(expectedUri));
            Assert.That(error!.Url, Is.EqualTo(expectedUri));
            Assert.That((error.InnerException as ConvaiApiException)?.RequestUri, Is.EqualTo(expectedUri));
            Assert.That(_transport.RequestCount, Is.EqualTo(suffix == "-draft" ? 2 : 1));
            Assert.That(error.Category, Is.EqualTo(failure switch
            {
                "http" => ConvaiRestErrorCategory.NotFound,
                "parse" => ConvaiRestErrorCategory.ParseError,
                "timeout" => ConvaiRestErrorCategory.Timeout,
                _ => ConvaiRestErrorCategory.Transport
            }));
        }

        [TestCase("character")]
        [TestCase("sections")]
        [TestCase("triggers")]
        public void UpgradedDraftRead_ProbeFailureReportsCharacterRequestUri(string resource)
        {
            const string characterId = "00000000-0000-4000-8000-000000000001";
            _client.Dispose();
            _client = new ConvaiRestClient(new ConvaiRestClientOptions("key")
            {
                CustomTransport = _transport,
                CharacterApiV2 = ConvaiCharacterApiOptions.Production("key")
                    .WithReadContext("workspace-1", useDraft: true)
            });
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Failure(HttpStatusCode.NotFound, "{}", request.Url);

            var error = Assert.ThrowsAsync<ConvaiRestException>(() => ReadUpgradedResourceAsync(resource, characterId));

            Assert.That(error!.Url, Is.EqualTo(new Uri("https://api2.convai.com/character/get?character_id=" +
                                                     characterId + "&workspace_id=workspace-1")));
            Assert.That(error.Url, Is.EqualTo(_transport.LastRequest!.Url));
            Assert.That(_transport.RequestCount, Is.EqualTo(1));
        }

        private Task ReadUpgradedResourceAsync(string resource, string characterId) => resource switch
        {
            "character" => _client.Characters.GetDetailsAsync(characterId),
            "sections" => _client.Narrative.GetSectionsAsync(characterId),
            "triggers" => _client.Narrative.GetTriggersAsync(characterId),
            _ => throw new ArgumentOutOfRangeException(nameof(resource))
        };

        [Test]
        public void UpgradedRead_TimeoutAndCallerCancellationRemainDistinct()
        {
            UseProjectFactory(ConvaiApiMode.Upgraded);
            _transport.ResponseFactory = request => ConvaiHttpResponse.TransportFailure(request.Url, "deadline", isTimeout: true);
            var error = Assert.ThrowsAsync<ConvaiRestException>(() => _client.Narrative.GetSectionsAsync("character"));
            Assert.That(error!.Category, Is.EqualTo(ConvaiRestErrorCategory.Timeout));
            using var source = new CancellationTokenSource();
            source.Cancel();
            Assert.CatchAsync<OperationCanceledException>(() => _client.Narrative.GetSectionsAsync("character", source.Token));
        }

        [Test]
        public void EndUsers_ListAsync_PreservesBackendErrorDetails()
        {
            const string body = "{\"error\":\"identity service unavailable\"}";
            _transport.ResponseFactory = request => ConvaiHttpResponse.Failure(
                HttpStatusCode.ServiceUnavailable, body, request.Url);

            ConvaiRestException error = Assert.ThrowsAsync<ConvaiRestException>(
                async () => await _client.EndUsers.ListAsync());

            Assert.That(error.Category, Is.EqualTo(ConvaiRestErrorCategory.ServerError));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(error.ResponseBody, Is.EqualTo(body));
            Assert.That(error.InnerException, Is.TypeOf<Convai.Api.Transport.ConvaiApiException>());
        }

        private sealed class CapturingTransport : IConvaiHttpTransport
        {
            [CanBeNull] public ConvaiHttpRequest LastRequest { get; private set; }
            [CanBeNull] public Func<ConvaiHttpRequest, ConvaiHttpResponse> ResponseFactory { get; set; }
            public int RequestCount { get; private set; }

            public Task<ConvaiHttpResponse> SendAsync(
                ConvaiHttpRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestCount++;
                LastRequest = request;
                ConvaiHttpResponse response = ResponseFactory?.Invoke(request)
                                              ?? ConvaiHttpResponse.Success(HttpStatusCode.OK, "{}", request.Url);
                return Task.FromResult(response);
            }

            public Task<byte[]> DownloadBytesAsync(Uri url, CancellationToken cancellationToken = default) =>
                Task.FromResult(Array.Empty<byte>());

            public void Dispose()
            {
            }
        }
    }
}
