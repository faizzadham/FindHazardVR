#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Convai.Application;
using Convai.Domain.Models.LipSync;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.RestAPI;
using Convai.RestAPI.Internal;
using Convai.RestAPI.Services;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Room;
using Convai.RestAPI.Transport;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RoomConnectInvocationMetadataTests
    {
        [SetUp]
        public void SetUp()
        {
            _transport = new CapturingTransport();
            var options = new ConvaiRestClientOptions("test-api-key") { CustomTransport = _transport };

            _client = new ConvaiRestClient(options);
        }

        [TearDown]
        public void TearDown() => _client.Dispose();

        private CapturingTransport _transport = null!;
        private ConvaiRestClient _client = null!;

        [Test]
        public async Task ConnectAsync_ApiKey_UsesXApiKeyHeader()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            await _client.Rooms.ConnectAsync(new RoomConnectionRequest
            {
                CharacterId = "char-123", CoreServiceUrl = "https://core.convai.com/connect"
            });

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            Assert.That(lastRequest.Headers["x-api-key"], Is.EqualTo("test-api-key"));
            Assert.That(lastRequest.Headers.ContainsKey("API-AUTH-TOKEN"), Is.False);
        }

        [Test]
        public async Task ConnectAsync_AuthToken_UsesDedicatedAuthTokenHeader()
        {
            var tokenTransport = new CapturingTransport();
            tokenTransport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);
            var options = new ConvaiRestClientOptions("test-auth-token")
            {
                AuthenticationMode = ConvaiAuthenticationMode.AuthToken,
                CustomTransport = tokenTransport
            };
            using var tokenClient = new ConvaiRestClient(options);

            await tokenClient.Rooms.ConnectAsync(new RoomConnectionRequest
            {
                CharacterId = "char-123", CoreServiceUrl = "https://core.convai.com/connect"
            });

            ConvaiHttpRequest lastRequest = tokenTransport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            Assert.That(lastRequest.Headers["API-AUTH-TOKEN"], Is.EqualTo("test-auth-token"));
            Assert.That(lastRequest.Headers.ContainsKey("x-api-key"), Is.False);
            Assert.That(lastRequest.Headers.ContainsKey("CONVAI-API-KEY"), Is.False);
        }

        [Test]
        public async Task ConnectAsync_AutoInjectsInvocationMetadata_WhenAbsent()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123", CoreServiceUrl = "https://core.convai.com/connect", Transport = "livekit"
            };

            await _client.Rooms.ConnectAsync(request);

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            Assert.That(lastRequest.Body, Is.Not.Null.And.Not.Empty);

            JObject payload = JObject.Parse(lastRequest.Body!);
            Assert.That(payload["invocation_metadata"]?["source"]?.Value<string>(), Is.EqualTo("unity_sdk"));
            Assert.That(payload["invocation_metadata"]?["client_version"]?.Value<string>(), Is.EqualTo(ConvaiSDK.Version.ToString()));

            Assert.That(lastRequest.Headers.ContainsKey("source"), Is.False);
            Assert.That(lastRequest.Headers.ContainsKey("version"), Is.False);
            Assert.That(payload["core_service_url"], Is.Null);
        }

        [Test]
        public async Task ConnectAsync_UsesCoreClientOptionWhenLegacyRequestUrlIsAbsent()
        {
            _client.Dispose();
            var options = new ConvaiRestClientOptions("test-api-key")
            {
                CustomTransport = _transport,
                CoreBaseUrl = "https://core.convai.com/connect"
            };
            _client = new ConvaiRestClient(options);
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            await _client.Rooms.ConnectAsync(new RoomConnectionRequest { CharacterId = "char-123" });

            Assert.That(_transport.LastRequest!.Url.AbsoluteUri,
                Is.EqualTo("https://core.convai.com/connect"));
        }

        [Test]
        public async Task ConnectAsync_PreservesExplicitInvocationMetadata()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                InvocationMetadata = new RoomInvocationMetadata
                {
                    Source = "custom_source", ClientVersion = "9.9.9"
                }
            };

            await _client.Rooms.ConnectAsync(request);

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            Assert.That(lastRequest.Body, Is.Not.Null.And.Not.Empty);

            JObject payload = JObject.Parse(lastRequest.Body!);
            Assert.That(payload["invocation_metadata"]?["source"]?.Value<string>(), Is.EqualTo("custom_source"));
            Assert.That(payload["invocation_metadata"]?["client_version"]?.Value<string>(), Is.EqualTo("9.9.9"));
        }

        [Test]
        public async Task ValidateApiKeyAsync_DoesNotInjectInvocationMetadata()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(
                    HttpStatusCode.OK,
                    "{\"referral_source_status\":\"ok\",\"status\":\"ok\"}",
                    request.Url);

            await _client.Users.ValidateApiKeyAsync();

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException(
                                                "Expected captured user validation request.");
            Assert.That(lastRequest.Url.AbsolutePath, Is.EqualTo("/user/referral-source-status"));
            Assert.That(lastRequest.Body, Is.Null);
            Assert.That(lastRequest.Headers.ContainsKey("source"), Is.False);
            Assert.That(lastRequest.Headers.ContainsKey("version"), Is.False);
        }

        [Test]
        public async Task ConnectAsync_IncludesEmotionConfig_WhenProvided()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                EmotionConfig = RoomEmotionConfig.Create("llm")
            };

            await _client.Rooms.ConnectAsync(request);

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            Assert.That(lastRequest.Body, Is.Not.Null.And.Not.Empty);

            JObject payload = JObject.Parse(lastRequest.Body!);
            Assert.That(payload["emotion_config"]?["provider"]?.Value<string>(), Is.EqualTo("llm"));
        }

        [Test]
        public async Task ConnectAsync_IncludesEndUserMetadata_WhenProvided()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                EndUserId = "player-42",
                MaxNumParticipants = 2,
                SharedSessionKey = "Dharmik_123",
                EndUserMetadata = new Dictionary<string, object>
                {
                    ["name"] = "Rishav",
                    ["level"] = 7
                }
            };

            await _client.Rooms.ConnectAsync(request);

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            JObject payload = JObject.Parse(lastRequest.Body!);

            Assert.That(payload["end_user_id"]?.Value<string>(), Is.EqualTo("player-42"));
            Assert.That(payload["max_num_participants"]?.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(payload["max_num_participants"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(payload["shared_session_key"]?.Value<string>(), Is.EqualTo("Dharmik_123"));
            Assert.That(payload["end_user_metadata"]?["name"]?.Value<string>(), Is.EqualTo("Rishav"));
            Assert.That(payload["end_user_metadata"]?["level"]?.Value<int>(), Is.EqualTo(7));
        }

        [Test]
        public async Task ConnectAsync_OmitsEmptyEndUserIdentityFields()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                EndUserId = "  ",
                EndUserMetadata = new Dictionary<string, object>()
            };

            await _client.Rooms.ConnectAsync(request);

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            JObject payload = JObject.Parse(lastRequest.Body!);

            Assert.That(payload["end_user_id"], Is.Null);
            Assert.That(payload["end_user_metadata"], Is.Null);
        }

        [Test]
        public async Task ConnectAsync_IncludesActionConfig_WhenProvided()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                ActionConfig = new ConvaiActionConfig
                {
                    Actions = new List<string> { " Move To ", "Pick Up" },
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = " cube ", Description = " red cube " }
                    },
                    Characters = new List<ConvaiActionCharacterDefinition>
                    {
                        new() { Name = " Player ", Bio = " current user " }
                    },
                    CurrentAttentionObject = " cube "
                }
            };

            await _client.Rooms.ConnectAsync(request);

            JObject payload = JObject.Parse(_transport.LastRequest!.Body!);
            Assert.That(payload["action_config"]?["actions"]?[0]?.Value<string>(), Is.EqualTo("Move To"));
            Assert.That(payload["action_config"]?["objects"]?[0]?["name"]?.Value<string>(), Is.EqualTo("cube"));
            Assert.That(payload["action_config"]?["objects"]?[0]?["description"]?.Value<string>(), Is.EqualTo("red cube"));
            Assert.That(payload["action_config"]?["characters"]?[0]?["name"]?.Value<string>(), Is.EqualTo("Player"));
            Assert.That(payload["action_config"]?["characters"]?[0]?["bio"]?.Value<string>(), Is.EqualTo("current user"));
            Assert.That(payload["action_config"]?["current_attention_object"]?.Value<string>(), Is.EqualTo("cube"));
        }

        [Test]
        public async Task ConnectAsync_OmitsEmptyActionConfig_WhenNormalizationRemovesAllEntries()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                ActionConfig = new ConvaiActionConfig
                {
                    Actions = new List<string> { " ", string.Empty },
                    Objects = new List<ConvaiActionObjectDefinition> { new() { Name = " " } },
                    Characters = new List<ConvaiActionCharacterDefinition> { new() { Name = string.Empty } }
                }
            };

            await _client.Rooms.ConnectAsync(request);

            JObject payload = JObject.Parse(_transport.LastRequest!.Body!);
            Assert.That(payload["action_config"], Is.Null);
        }

        [Test]
        public async Task ConnectAsync_OmitsActionConfig_WhenNoActionsRemainAfterNormalization()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                ActionConfig = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "cube", Description = "red cube" }
                    },
                    Characters = new List<ConvaiActionCharacterDefinition>
                    {
                        new() { Name = "Player", Bio = "current user" }
                    },
                    CurrentAttentionObject = "cube"
                }
            };

            await _client.Rooms.ConnectAsync(request);

            JObject payload = JObject.Parse(_transport.LastRequest!.Body!);
            Assert.That(payload["action_config"], Is.Null);
        }

        [Test]
        public async Task ConnectAsync_SerializesBlankActionTargetMetadata_AsEmptyStrings()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                ActionConfig = new ConvaiActionConfig
                {
                    Actions = new List<string> { "Move To" },
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "cube", Description = " " }
                    },
                    Characters = new List<ConvaiActionCharacterDefinition>
                    {
                        new() { Name = "Player", Bio = null }
                    }
                }
            };

            await _client.Rooms.ConnectAsync(request);

            JObject payload = JObject.Parse(_transport.LastRequest!.Body!);
            Assert.That(payload["action_config"]?["objects"]?[0]?["description"]?.Value<string>(), Is.EqualTo(string.Empty));
            Assert.That(payload["action_config"]?["characters"]?[0]?["bio"]?.Value<string>(), Is.EqualTo(string.Empty));
        }

        [Test]
        public void ConnectAsync_RejectsDuplicateActionObjectNames()
        {
            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                ActionConfig = new ConvaiActionConfig
                {
                    Actions = new List<string> { "Move To" },
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "cube" },
                        new() { Name = " Cube " }
                    }
                }
            };

            ConvaiRestException ex = Assert.ThrowsAsync<ConvaiRestException>(() => _client.Rooms.ConnectAsync(request));
            Assert.That(ex!.Message, Does.Contain("Duplicate action_config object name"));
        }

        [Test]
        public void ConnectAsync_RejectsDuplicateActionCharacterNames()
        {
            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                ActionConfig = new ConvaiActionConfig
                {
                    Actions = new List<string> { "Follow" },
                    Characters = new List<ConvaiActionCharacterDefinition>
                    {
                        new() { Name = "Player" },
                        new() { Name = "player" }
                    }
                }
            };

            ConvaiRestException ex = Assert.ThrowsAsync<ConvaiRestException>(() => _client.Rooms.ConnectAsync(request));
            Assert.That(ex!.Message, Does.Contain("Duplicate action_config character name"));
        }

        [Test]
        public void ConnectAsync_RejectsInvalidCurrentAttentionObject()
        {
            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                ActionConfig = new ConvaiActionConfig
                {
                    Actions = new List<string> { "Move To" },
                    Objects = new List<ConvaiActionObjectDefinition> { new() { Name = "cube" } },
                    CurrentAttentionObject = "lever"
                }
            };

            ConvaiRestException ex = Assert.ThrowsAsync<ConvaiRestException>(() => _client.Rooms.ConnectAsync(request));
            Assert.That(ex!.Message, Does.Contain("current_attention_object"));
        }

        [Test]
        public async Task ConnectAsync_IncludesBlendshapeContract_WhenProvided()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            var request = new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit",
                BlendshapeProvider = "neurosync",
                BlendshapeConfig = new ConvaiBlendshapeConfig(
                    true,
                    10,
                    60,
                    "arkit")
            };

            await _client.Rooms.ConnectAsync(request);

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");
            JObject payload = JObject.Parse(lastRequest.Body!);

            Assert.That(payload["blendshape_provider"]?.Value<string>(), Is.EqualTo("neurosync"));
            Assert.That(payload["blendshape_config"]?["enable_chunking"]?.Value<bool>(), Is.True);
            Assert.That(payload["blendshape_config"]?["chunk_size"]?.Value<int>(), Is.EqualTo(10));
            Assert.That(payload["blendshape_config"]?["output_fps"]?.Value<int>(), Is.EqualTo(60));
            Assert.That(payload["blendshape_config"]?["format"]?.Value<string>(), Is.EqualTo("arkit"));
        }

        [Test]
        public async Task GetDetailsAsync_UsesFixedProductionCharacterEndpoint_AndUnwrapsResponseEnvelope()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(
                    HttpStatusCode.OK,
                    "{\"response\":{\"character_id\":\"char-123\",\"character_name\":\"Test Character\",\"character_emotions\":[\"joy\"]}}",
                    request.Url);

            CharacterDetails details = await _client.Characters.GetDetailsAsync("char-123");

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException(
                                                "Expected captured character details request.");
            Assert.That(lastRequest.Url.ToString(), Is.EqualTo("https://api.convai.com/character/get"));
            Assert.That(lastRequest.Body, Is.Not.Null.And.Contains("\"charID\":\"char-123\""));
            Assert.That(details.CharacterID, Is.EqualTo("char-123"));
            Assert.That(details.CharacterName, Is.EqualTo("Test Character"));
        }

        [Test]
        public void ApplyLipSyncMapper_PopulatesBlendshapeFields_WhenTransportContractIsValid()
        {
            var request = new RoomConnectionRequest();

            RoomConnectionRequestLipSyncMapper.Apply(request, CreateLipSyncOptions());

            Assert.That(request.BlendshapeProvider, Is.EqualTo("neurosync"));
            Assert.That(request.BlendshapeConfig, Is.Not.Null);
            ConvaiBlendshapeConfig blendshapeConfig = request.BlendshapeConfig!;
            Assert.That(blendshapeConfig.Format, Is.EqualTo("arkit"));
            Assert.That(blendshapeConfig.OutputFps, Is.EqualTo(60));
            Assert.That(blendshapeConfig.EnableChunking, Is.True);
            Assert.That(blendshapeConfig.ChunkSize, Is.EqualTo(10));
        }

        [Test]
        public void ApplyLipSyncMapper_ClearsBlendshapeFields_WhenTransportContractIsDisabled()
        {
            var request = new RoomConnectionRequest { BlendshapeProvider = "neurosync" };

            RoomConnectionRequestLipSyncMapper.Apply(request, LipSyncTransportOptions.Disabled);

            Assert.That(request.BlendshapeProvider, Is.Null);
            Assert.That(request.BlendshapeConfig, Is.Null);
        }

        [Test]
        public void CreateRoomConnectionRequest_AppliesSharedJoinVideoAndLipSyncFields()
        {
            RoomConnectionRequest request = RoomConnectionRequestFactory.Create(
                "char-123",
                "video",
                "https://core.convai.com/connect",
                "session-xyz",
                " user-42 ",
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                new RoomJoinOptions("room-a", spawnAgent: false, maxNumParticipants: 4),
                CreateLipSyncOptions(),
                endUserMetadata: new Dictionary<string, object>
                {
                    ["name"] = "Rishav"
                });

            Assert.That(request.CharacterId, Is.EqualTo("char-123"));
            Assert.That(request.ConnectionMode, Is.EqualTo(RoomConnectionType.Video));
            Assert.That(request.LlmProvider, Is.EqualTo("dynamic"));
            Assert.That(request.CoreServiceUrl, Is.EqualTo("https://core.convai.com/connect"));
            Assert.That(request.CharacterSessionId, Is.EqualTo("session-xyz"));
            Assert.That(request.EndUserId, Is.EqualTo("user-42"));
            Assert.That(request.EndUserMetadata?["name"], Is.EqualTo("Rishav"));
            Assert.That(request.VideoTrackName, Is.EqualTo("camera-main"));
            Assert.That(request.Mode, Is.EqualTo("join"));
            Assert.That(request.SpawnAgent, Is.False);
            Assert.That(request.MaxNumParticipants, Is.EqualTo(4));
            Assert.That(request.SharedSessionKey, Is.Null);
            Assert.That(request.TurnDetectionConfig, Is.Not.Null);
            Assert.That(request.DefaultSttEnabled, Is.True);
            Assert.That(request.VadParams, Is.Null);
            Assert.That(request.EmotionConfig?.Provider, Is.EqualTo("llm"));
            Assert.That(request.BlendshapeProvider, Is.EqualTo("neurosync"));
            Assert.That(request.BlendshapeConfig?.Format, Is.EqualTo("arkit"));
        }

        [Test]
        public void CreateRoomConnectionRequest_CopiesResolvedActionConfig()
        {
            var joinOptions = RoomJoinOptions.CreateNew("session-xyz");
            joinOptions.ResolvedActionConfig = new ConvaiActionConfig
            {
                Actions = new List<string> { "Move To" },
                Objects = new List<ConvaiActionObjectDefinition> { new() { Name = "cube" } },
                CurrentAttentionObject = "cube"
            };

            RoomConnectionRequest request = RoomConnectionRequestFactory.Create(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "session-xyz",
                "user-42",
                null,
                RoomEmotionConfig.Create("llm"),
                joinOptions,
                LipSyncTransportOptions.Disabled);

            Assert.That(request.ActionConfig, Is.Not.Null);
            Assert.That(request.ActionConfig!.Actions[0], Is.EqualTo("Move To"));
            Assert.That(request.ActionConfig.Objects[0].Name, Is.EqualTo("cube"));
            Assert.That(request.ActionConfig.CurrentAttentionObject, Is.EqualTo("cube"));
            Assert.That(request.ActionConfig, Is.Not.SameAs(joinOptions.ResolvedActionConfig));
            Assert.That(request.MaxNumParticipants, Is.Null);
            Assert.That(request.SharedSessionKey, Is.Null);
        }

        [Test]
        public void CreateRoomConnectionRequest_MapsPushToTalkResolvedOptionsToConcreteRequestFields()
        {
            ResolvedTurnTakingOptions resolvedOptions = TurnTakingOptionsResolver.Resolve(
                TurnTakingOptions.CreatePushToTalkDefault(),
                null);

            RoomConnectionRequest request = RoomConnectionRequestFactory.Create(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "session-xyz",
                "user-42",
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                RoomJoinOptions.CreateNew("session-xyz"),
                resolvedOptions,
                LipSyncTransportOptions.Disabled);

            Assert.That(request.TurnDetectionConfig, Is.Null);
            Assert.That(request.DefaultSttEnabled, Is.False);
        }

        [Test]
        public void CreateRoomConnectionRequest_OmitsVadParams_WhenUsingServerDefault()
        {
            RoomConnectionRequest request = RoomConnectionRequestFactory.Create(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "session-xyz",
                "user-42",
                null,
                RoomEmotionConfig.Create("llm"),
                RoomJoinOptions.CreateNew("session-xyz"),
                TurnTakingOptionsResolver.Resolve(TurnTakingOptions.CreateHandsFreeDefault(), null),
                LipSyncTransportOptions.Disabled);

            Assert.That(request.VadParams, Is.Null);
        }

        [Test]
        public void CreateRoomConnectionRequest_MapsCustomUserVadSettingsToPayload()
        {
            var userVadSettings = new UserVadSettings
            {
                UseServerDefault = false,
                Confidence = 0.81f,
                StartSecs = 0.33f,
                StopSecs = 0.44f,
                MinVolume = 0.55f
            };

            RoomConnectionRequest request = RoomConnectionRequestFactory.Create(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "session-xyz",
                "user-42",
                null,
                RoomEmotionConfig.Create("llm"),
                RoomJoinOptions.CreateNew("session-xyz"),
                TurnTakingOptionsResolver.Resolve(TurnTakingOptions.CreateHandsFreeDefault(), null),
                LipSyncTransportOptions.Disabled,
                userVadSettings: userVadSettings);

            Assert.That(request.VadParams, Is.Not.Null);
            Assert.That(request.VadParams!.Confidence, Is.EqualTo(0.81f));
            Assert.That(request.VadParams.StartSecs, Is.EqualTo(0.33f));
            Assert.That(request.VadParams.StopSecs, Is.EqualTo(0.44f));
            Assert.That(request.VadParams.MinVolume, Is.EqualTo(0.55f));

            string json = RoomConnectionRequestTransportSerializer.SerializeForTransport(
                request,
                new ConvaiRestClientOptions("test-api-key"));
            JObject payload = JObject.Parse(json);
            Assert.That(payload["vad_params"]?["confidence"]?.Value<float>(), Is.EqualTo(0.81f));
            Assert.That(payload["vad_params"]?["start_secs"]?.Value<float>(), Is.EqualTo(0.33f));
            Assert.That(payload["vad_params"]?["stop_secs"]?.Value<float>(), Is.EqualTo(0.44f));
            Assert.That(payload["vad_params"]?["min_volume"]?.Value<float>(), Is.EqualTo(0.55f));
        }

        [Test]
        public void CreateRoomConnectionRequest_IncludesDynamicVisionContextConfig()
        {
            RoomConnectionRequest request = RoomConnectionRequestFactory.Create(
                "char-123",
                "video",
                "https://core.convai.com/connect",
                "session-xyz",
                "user-42",
                "unity-scene",
                RoomEmotionConfig.Create("llm"),
                RoomJoinOptions.CreateNew("session-xyz"),
                TurnTakingOptionsResolver.Resolve(TurnTakingOptions.CreateHandsFreeDefault(), null),
                LipSyncTransportOptions.Disabled,
                visionInputConfig: new RoomVisionInputConfig
                {
                    Enabled = true,
                    SampleIntervalSecs = 0.5f,
                    FramesPerTurn = 12,
                    BufferFrames = 30,
                    SamplingWindows = new List<RoomVisionSamplingWindow>
                    {
                        new() { Count = 6, IntervalMs = 300 },
                        new() { Count = 6, IntervalMs = 1000 }
                    },
                    StalenessSeconds = 10f,
                    MaxResolution = 720,
                    ReplacePreviousVisionContext = true
                },
                respondModes: new RoomRespondModesConfig
                {
                    Vision = "silent",
                    ContextUpdate = "auto",
                    Trigger = "must_respond",
                    SceneMetadata = "silent"
                });

            string json = RoomConnectionRequestTransportSerializer.SerializeForTransport(
                request,
                new ConvaiRestClientOptions("test-api-key"));
            JObject payload = JObject.Parse(json);

            Assert.That(payload["connection_type"]?.Value<string>(), Is.EqualTo("video"));
            Assert.That(payload["video_track_name"]?.Value<string>(), Is.EqualTo("unity-scene"));
            Assert.That(payload["vision_input_config"]?["enabled"]?.Value<bool>(), Is.True);
            Assert.That(payload["vision_input_config"]?["sample_interval_secs"]?.Value<float>(), Is.EqualTo(0.5f));
            Assert.That(payload["vision_input_config"]?["frames_per_turn"]?.Value<int>(), Is.EqualTo(12));
            Assert.That(payload["vision_input_config"]?["buffer_frames"]?.Value<int>(), Is.EqualTo(30));
            Assert.That(payload["vision_input_config"]?["sampling_windows"]?[0]?["count"]?.Value<int>(), Is.EqualTo(6));
            Assert.That(payload["vision_input_config"]?["sampling_windows"]?[0]?["interval_ms"]?.Value<int>(), Is.EqualTo(300));
            Assert.That(payload["vision_input_config"]?["max_resolution"]?.Value<int>(), Is.EqualTo(720));
            Assert.That(payload["vision_input_config"]?["replace_previous_vision_context"]?.Value<bool>(), Is.True);
            Assert.That(payload["respond_modes"]?["vision"]?.Value<string>(), Is.EqualTo("silent"));
            Assert.That(payload["respond_modes"]?["context_update"]?.Value<string>(), Is.EqualTo("auto"));
            Assert.That(payload["respond_modes"]?["trigger"]?.Value<string>(), Is.EqualTo("must_respond"));
            Assert.That(payload["respond_modes"]?["scene_metadata"]?.Value<string>(), Is.EqualTo("silent"));
        }

        [Test]
        public void PrepareRoomInitializationRequest_PreservesNativeStoredSessionFallback_WhenResumeIsDisabled()
        {
            PreparedRoomInitializationRequest preparation = RoomInitializationRequestPreparer.Prepare(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "stored-session",
                false,
                null,
                " user-42 ",
                new Dictionary<string, object>
                {
                    ["name"] = "Rishav"
                },
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                LipSyncTransportOptions.Disabled,
                StoredSessionFallbackPolicy.NativeCompatibility);

            Assert.That(preparation.IsJoinRequest, Is.False);
            Assert.That(preparation.Request.CharacterSessionId, Is.EqualTo("stored-session"));
            Assert.That(preparation.Request.EndUserId, Is.EqualTo("user-42"));
            Assert.That(preparation.Request.EndUserMetadata?["name"], Is.EqualTo("Rishav"));
            Assert.That(preparation.ModeLogMessage, Is.EqualTo("Room create mode (new room)"));
        }

        [Test]
        public void PrepareRoomInitializationRequest_IncludesPlayerNameInEndUserMetadata_WhenMetadataIsMissing()
        {
            PreparedRoomInitializationRequest preparation = RoomInitializationRequestPreparer.Prepare(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "stored-session",
                false,
                null,
                "user-42",
                null,
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                LipSyncTransportOptions.Disabled,
                StoredSessionFallbackPolicy.NativeCompatibility,
                playerName: "  Local Player  ");

            Assert.That(preparation.Request.EndUserMetadata, Is.Not.Null);
            Assert.That(preparation.Request.EndUserMetadata?["name"], Is.EqualTo("Local Player"));
        }

        [Test]
        public void PrepareRoomInitializationRequest_PlayerNameOverridesExistingMetadataName()
        {
            PreparedRoomInitializationRequest preparation = RoomInitializationRequestPreparer.Prepare(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "stored-session",
                false,
                null,
                "user-42",
                new Dictionary<string, object>
                {
                    ["name"] = "Old Name",
                    ["level"] = 7
                },
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                LipSyncTransportOptions.Disabled,
                StoredSessionFallbackPolicy.NativeCompatibility,
                playerName: "Current Player");

            Assert.That(preparation.Request.EndUserMetadata, Is.Not.Null);
            Assert.That(preparation.Request.EndUserMetadata?["name"], Is.EqualTo("Current Player"));
            Assert.That(preparation.Request.EndUserMetadata?["level"], Is.EqualTo(7));
        }

        [Test]
        public void PrepareRoomInitializationRequest_PreservesWebGLStoredSessionFallbackPolicies()
        {
            PreparedRoomInitializationRequest resumeDisabledPreparation = RoomInitializationRequestPreparer.Prepare(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "stored-session",
                false,
                null,
                null,
                null,
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                LipSyncTransportOptions.Disabled,
                StoredSessionFallbackPolicy.WebGLCompatibility);

            PreparedRoomInitializationRequest resumeEnabledPreparation = RoomInitializationRequestPreparer.Prepare(
                "char-123",
                "audio",
                "https://core.convai.com/connect",
                "stored-session",
                true,
                null,
                null,
                null,
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                LipSyncTransportOptions.Disabled,
                StoredSessionFallbackPolicy.WebGLCompatibility);

            Assert.That(resumeDisabledPreparation.Request.CharacterSessionId, Is.Null);
            Assert.That(resumeEnabledPreparation.Request.CharacterSessionId, Is.EqualTo("stored-session"));
        }

        [Test]
        public void PrepareRoomInitializationRequest_BuildsJoinMetadataAndLogMessage()
        {
            PreparedRoomInitializationRequest preparation = RoomInitializationRequestPreparer.Prepare(
                "char-123",
                "video",
                "https://core.convai.com/connect",
                "stored-session",
                true,
                new RoomJoinOptions("room-a", string.Empty, false, 4),
                "user-42",
                null,
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                LipSyncTransportOptions.Disabled,
                StoredSessionFallbackPolicy.NativeCompatibility);

            Assert.That(preparation.IsJoinRequest, Is.True);
            Assert.That(preparation.Request.Mode, Is.EqualTo("join"));
            Assert.That(preparation.Request.RoomName, Is.EqualTo("room-a"));
            Assert.That(preparation.Request.SpawnAgent, Is.False);
            Assert.That(preparation.Request.MaxNumParticipants, Is.EqualTo(4));
            Assert.That(preparation.Request.CharacterSessionId, Is.Null);
            Assert.That(
                preparation.FormatModeLogMessage("[WebGLRoomController]"),
                Is.EqualTo("[WebGLRoomController] Room join mode: room=room-a, spawnAgent=False"));
        }

        [Test]
        public void PrepareRoomInitializationRequest_CopiesResolvedDynamicVisionConfig()
        {
            RoomJoinOptions joinOptions = RoomJoinOptions.CreateNew(null);
            joinOptions.ResolvedVisionInputConfig = new RoomVisionInputConfig
            {
                Enabled = true,
                FramesPerTurn = 7
            };
            joinOptions.ResolvedRespondModes = new RoomRespondModesConfig
            {
                Vision = "silent",
                Trigger = "must_respond"
            };

            PreparedRoomInitializationRequest preparation = RoomInitializationRequestPreparer.Prepare(
                "char-123",
                "video",
                "https://core.convai.com/connect",
                null,
                false,
                joinOptions,
                "user-42",
                null,
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                LipSyncTransportOptions.Disabled,
                StoredSessionFallbackPolicy.NativeCompatibility);

            Assert.That(preparation.Request.VisionInputConfig, Is.Not.Null);
            Assert.That(preparation.Request.VisionInputConfig!.Enabled, Is.True);
            Assert.That(preparation.Request.VisionInputConfig!.FramesPerTurn, Is.EqualTo(7));
            Assert.That(preparation.Request.RespondModes, Is.Not.Null);
            Assert.That(preparation.Request.RespondModes!.Vision, Is.EqualTo("silent"));
            Assert.That(preparation.Request.RespondModes!.Trigger, Is.EqualTo("must_respond"));
        }

        [Test]
        public async Task SerializeForTransport_MatchesRoomServicePayload_ForSharedRoomRequest()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            RoomJoinOptions joinOptions = new("room-a", "session-xyz", false, 4);
            RoomConnectionRequest roomServiceRequest = RoomConnectionRequestFactory.Create(
                "char-123",
                "video",
                "https://core.convai.com/connect",
                "session-xyz",
                "user-42",
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                joinOptions,
                CreateLipSyncOptions(),
                endUserMetadata: new Dictionary<string, object>
                {
                    ["name"] = "Rishav"
                });

            await _client.Rooms.ConnectAsync(roomServiceRequest);

            ConvaiHttpRequest lastRequest = _transport.LastRequest
                                            ?? throw new AssertionException("Expected captured room connect request.");

            RoomConnectionRequest directTransportRequest = RoomConnectionRequestFactory.Create(
                "char-123",
                "video",
                "https://core.convai.com/connect",
                "session-xyz",
                "user-42",
                "camera-main",
                RoomEmotionConfig.Create("llm"),
                joinOptions,
                CreateLipSyncOptions(),
                endUserMetadata: new Dictionary<string, object>
                {
                    ["name"] = "Rishav"
                });

            string directJson = RoomConnectionRequestTransportSerializer.SerializeForTransport(
                directTransportRequest,
                new ConvaiRestClientOptions("test-api-key"));

            Assert.That(
                JToken.DeepEquals(JToken.Parse(lastRequest.Body!), JToken.Parse(directJson)),
                Is.True,
                "Direct transport serialization must match the canonical RoomService payload.");
        }

        [Test]
        public async Task ConnectAsync_Succeeds_WhenSpeakerIdIsMissingFromResponse()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(includeSpeakerId: false), request.Url);

            RoomDetails details = await _client.Rooms.ConnectAsync(new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit"
            });

            Assert.That(details, Is.Not.Null);
            Assert.That(details.Token, Is.EqualTo("test-token"));
            Assert.That(details.SpeakerId, Is.Null.Or.Empty);
        }

        [Test]
        public void ConnectAsync_PreservesErrorBodyForStoredSessionRecovery()
        {
            const string responseBody = "{\"detail\":\"Invalid character_session_id\"}";
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Failure(HttpStatusCode.BadRequest, responseBody, request.Url);

            ConvaiRestException exception = Assert.ThrowsAsync<ConvaiRestException>(() =>
                _client.Rooms.ConnectAsync(new RoomConnectionRequest
                {
                    CharacterId = "char-123",
                    CharacterSessionId = "stale-session",
                    CoreServiceUrl = "https://core.convai.com/connect",
                    Transport = "livekit"
                }))!;

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.ResponseBody, Is.EqualTo(responseBody));
        }

        [Test]
        public async Task ConnectAsync_PreservesBackendTraceAndEndUserEcho()
        {
            _transport.ResponseFactory = request =>
                ConvaiHttpResponse.Success(HttpStatusCode.OK, BuildRoomDetailsResponse(), request.Url);

            RoomDetails details = await _client.Rooms.ConnectAsync(new RoomConnectionRequest
            {
                CharacterId = "char-123",
                CoreServiceUrl = "https://core.convai.com/connect",
                Transport = "livekit"
            });

            Assert.That(details.RequestTraceId, Is.EqualTo("trace-123"));
            Assert.That(details.EndUserId, Is.EqualTo("player-42"));
            Assert.That(details.EndUserMetadata, Is.Not.Null);
            Assert.That(details.EndUserMetadata?["name"]?.ToString(), Is.EqualTo("Rishav"));
            Assert.That(Convert.ToInt32(details.EndUserMetadata?["rank"]), Is.EqualTo(7));
        }

        private static string BuildRoomDetailsResponse(bool includeSpeakerId = true)
        {
            return "{"
                   + "\"token\":\"test-token\","
                   + "\"room_name\":\"test-room\","
                   + "\"session_id\":\"session-123\","
                   + "\"request_trace_id\":\"trace-123\","
                   + "\"room_url\":\"wss://example.convai.com\","
                   + "\"character_session_id\":\"char-session-123\","
                   + "\"end_user_id\":\"player-42\","
                   + "\"end_user_metadata\":{\"name\":\"Rishav\",\"rank\":7},"
                   + (includeSpeakerId ? "\"speaker_id\":\"speaker-123\"," : string.Empty)
                   + "\"transport\":\"livekit\""
                   + "}";
        }

        private static CharacterDetails BuildCharacterDetails(Action<JObject> mutator)
        {
            JObject root = JObject.FromObject(CharacterDetails.Default());
            mutator(root);

            string json = root.ToString(Formatting.None);
            var details = JsonConvert.DeserializeObject<CharacterDetails>(json);
            return details ?? throw new AssertionException("Failed to deserialize CharacterDetails for test.");
        }

        private static LipSyncTransportOptions CreateLipSyncOptions()
        {
            return new LipSyncTransportOptions(
                true,
                "neurosync",
                LipSyncProfileId.ARKit,
                "arkit",
                new[] { "EyeBlinkLeft" },
                true,
                10,
                60,
                LipSyncTransportOptions.DefaultFramesBufferDuration);
        }

        private sealed class CapturingTransport : IConvaiHttpTransport
        {
            public ConvaiHttpRequest? LastRequest { get; private set; }

            public Func<ConvaiHttpRequest, ConvaiHttpResponse>? ResponseFactory { get; set; }

            public Task<ConvaiHttpResponse> SendAsync(
                ConvaiHttpRequest request,
                CancellationToken cancellationToken = default)
            {
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
