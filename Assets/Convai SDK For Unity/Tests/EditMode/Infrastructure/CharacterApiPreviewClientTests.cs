using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi;
using Convai.CharacterApi.Contracts;
using Convai.CharacterApi.Contracts.Client;
using Convai.CharacterApi.Contracts.Model;
using Convai.CharacterApi.Runtime;
using Convai.RestAPI;
using Convai.RestAPI.Services;
using Convai.PlatformApi.Legacy;
using Convai.Tests.EditMode.Fixtures;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public sealed class CharacterApiPreviewClientTests
    {
        [Test]
        public void GeneratedStreamingOperation_UsesSuccessfulSseMediaType()
        {
            Assert.That(CharacterApiOperations.CharacterGenerateBackstory.ResponseMediaType,
                Is.EqualTo("text/event-stream"));
            Assert.That(CharacterApiOperations.CharacterGenerateBackstory.ErrorResponseMediaTypes,
                Does.Contain("application/json"));
        }

        [Test]
        public void NullableDateConverter_AcceptsExplicitNull()
        {
            NullableDateContainer? result = JsonConvert.DeserializeObject<NullableDateContainer>(
                "{\"value\":null}");

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value, Is.Null);
        }

        [Test]
        public void SseParser_RemovesOnlyOneOptionalSpace()
        {
            var parser = new SseEventParser();

            Assert.That(parser.Push("data:  indented"), Is.Null);
            ConvaiApiSseEvent? result = parser.Push(string.Empty);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Data, Is.EqualTo(" indented"));
        }

        [Test]
        public void UnityTransport_ClassifiesDataProcessingErrorsAsTransportFailures()
        {
            Assert.That(UnityWebRequestApiTransport.IsTransportFailure(
                UnityEngine.Networking.UnityWebRequest.Result.DataProcessingError), Is.True);
        }

        [TestCase("Request timeout", true)]
        [TestCase("Request Timeout", true)]
        [TestCase("Connection timed out", true)]
        [TestCase("CONNECTION TIMED OUT", true)]
        [TestCase("Cannot resolve destination host", false)]
        [TestCase("Request aborted", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void UnityTransports_PreserveTimeoutClassificationAndRequestUri(string? message, bool isTimeout)
        {
            var uri = new Uri("https://example.invalid/character/get?character_id=character%40draft&workspace_id=workspace");
            ConvaiApiException apiError = UnityWebRequestApiTransport.TransportFailure(uri, message);
            var response = Convai.RestAPI.Transport.UnityWebRequestTransport.TransportFailure(uri, message);
            ConvaiRestException restError = ConvaiRestException.FromResponse(response);

            Assert.That(apiError.Category, Is.EqualTo(isTimeout
                ? ConvaiApiErrorCategory.Timeout : ConvaiApiErrorCategory.Transport));
            Assert.That(apiError.RequestUri, Is.EqualTo(uri));
            Assert.That(response.IsTimeout, Is.EqualTo(isTimeout));
            Assert.That(restError.Category, Is.EqualTo(isTimeout
                ? ConvaiRestErrorCategory.Timeout : ConvaiRestErrorCategory.Transport));
            Assert.That(restError.Url, Is.EqualTo(uri));
        }

        [Test]
        public async Task CustomBaseUri_PreservesFinalPathSegmentWithoutTrailingSlash()
        {
            var transport = new RecordingTransport("{}");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Custom(
                    new Uri("https://proxy.example/convai"), "secret"),
                transport);

            await client.Characters.ExecuteAsync<JObject>(CharacterApiOperations.CharacterGet);

            Assert.That(transport.LastRequest!.Uri.AbsolutePath,
                Is.EqualTo("/convai/character/get"));
        }

        [Test]
        public async Task PlatformCustomBaseUri_PreservesFinalPathSegmentWithoutTrailingSlash()
        {
            var transport = new RecordingTransport("{}");
            using var client = new ConvaiPlatformLegacyClient(
                ConvaiPlatformLegacyOptions.WithApiKey(
                    new Uri("https://proxy.example/convai"), "secret"),
                transport);

            await client.PostAsync<JObject>("user/get", null);

            Assert.That(transport.LastRequest!.Uri.AbsolutePath,
                Is.EqualTo("/convai/user/get"));
        }

        [Test]
        public async Task SendSseAsync_CallerCancellationInterruptsStalledLineRead()
        {
            using var source = new CancellationTokenSource();
            using var transport = new HttpClientApiTransport(
                TimeSpan.FromSeconds(30), new StalledSseHandler());
            var request = new ConvaiApiRequest(
                new Uri("https://example.invalid/stream"),
                ConvaiApiHttpMethod.Get,
                accept: "text/event-stream");

            Task pending = transport.SendSseAsync(request, _ => { }, source.Token);
            await Task.Yield();
            source.Cancel();

            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(pending,
                "A stalled SSE line read ignored cancellation.", 2000);
        }

        [Test]
        public void CompatibilityFacade_DoesNotReferenceFullAuthoringAssembly()
        {
            bool referencesAuthoring = typeof(ConvaiRestClient).Assembly.GetReferencedAssemblies()
                .Any(assembly => assembly.Name == "Convai.CharacterApi.Authoring");

            Assert.That(referencesAuthoring, Is.False,
                "The 4.x facade is part of the default player graph and must not pull in authoring APIs.");
        }

        [Test]
        public void CompatibilityProjection_ConvertsStructuredBoostedWords()
        {
            var source = new CharacterResponse(
                userId: "user-1",
                characterId: "character-42",
                characterName: "Test",
                voiceType: "test-voice",
                languageCode: "en",
                description: string.Empty,
                modelType: string.Empty,
                memorySettings: new Dictionary<string, object>(),
                characterTraits: new Dictionary<string, object>(),
                metadataFilter: new Dictionary<string, object>(),
                boostedWords: new List<Dictionary<string, string>>
                {
                    new() { ["spelledAs"] = "Convai", ["pronouncedAs"] = "con-vai" }
                },
                moderationEnabled: true,
                editCharacterAccess: true);

            CharacterDetails result = CharacterService.ToLegacyCharacterDetails(source);

            Assert.That(result.CharacterID, Is.EqualTo("character-42"));
            Assert.That(result.BoostedWords, Is.EqualTo(new[] { "Convai" }));
            Assert.That(result.ModerationEnabled, Is.True);
            Assert.That(result.EditCharacterAccess, Is.True);
        }

        [Test]
        public void RuntimeCharacterAssembly_IsAvailableToDefaultUnityAssemblies()
        {
            string asmdef = File.ReadAllText(
                "Packages/com.convai.convai-sdk-for-unity/Plugins/ConvaiApi/CharacterRuntime/" +
                "Convai.CharacterApi.Runtime.asmdef");
            JObject definition = JObject.Parse(asmdef);

            Assert.That(definition.Value<bool>("autoReferenced"), Is.True);
        }

        [Test]
        public void ProductionOptions_UseProductionCharacterApi()
        {
            ConvaiCharacterApiOptions options = ConvaiCharacterApiOptions.Production("secret");

            Assert.That(options.BaseUri, Is.EqualTo(new Uri("https://api2.convai.com/")));
            Assert.That(options.CredentialKind, Is.EqualTo(CharacterApiCredentialKind.ApiKey));
        }

        [Test]
        public void ResourceGroupsExposeEveryClassifiedStableOperation()
        {
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Preview("secret"), new RecordingTransport("{}"));
            CharacterApiResourceClient[] resources =
            {
                client.Access, client.ActionEvents, client.Animations, client.Assets, client.Avatars,
                client.Characters, client.CharacterVersions, client.ChatHistory, client.Domains,
                client.Experiences, client.Functions, client.InteractionLogs, client.LlmModels,
                client.Mcp, client.Mindview, client.Narrative, client.Project, client.Snapshots,
                client.Tts, client.XpStreams, client.ExperienceSessions, client.KnowledgeBank,
                client.StreamAccess, client.UserAccount, client.Workspaces, client.WorkspacePreview
            };

            CharacterApiOperation[] exposed = resources.SelectMany(resource => resource.Operations)
                .Distinct()
                .ToArray();
            CharacterApiOperation[] expected = CharacterApiOperations.All
                .Where(operation => operation.Classification == CharacterApiOperationClass.PublicStable)
                .ToArray();

            Assert.That(CharacterApiOperations.All.Count, Is.EqualTo(150));
            Assert.That(exposed, Is.EquivalentTo(expected));
        }

        [Test]
        public async Task ExecuteAsync_BuildsCanonicalGetQueryAndApiKeyHeader()
        {
            var transport = new RecordingTransport("{\"charID\":\"00000000-0000-4000-8000-000000000001\"}");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Preview("secret"), transport);

            await client.Characters.ExecuteAsync<JObject>(
                CharacterApiOperations.CharacterGet,
                query: new Dictionary<string, object?>
                {
                    ["character_id"] = "00000000-0000-4000-8000-000000000001"
                });

            Assert.That(transport.LastRequest, Is.Not.Null);
            Assert.That(transport.LastRequest!.Method, Is.EqualTo(ConvaiApiHttpMethod.Get));
            Assert.That(transport.LastRequest.Uri.Host, Is.EqualTo("api2-stg.convai.com"));
            Assert.That(transport.LastRequest.Uri.Query, Does.Contain("character_id="));
            Assert.That(transport.LastRequest.Headers["CONVAI-API-KEY"], Is.EqualTo("secret"));
            Assert.That(transport.LastRequest.Headers.ContainsKey("Authorization"), Is.False);
        }

        [Test]
        public async Task ExecuteAsync_UsesBearerHeaderForPat()
        {
            var transport = new RecordingTransport("{\"items\":[],\"total\":0,\"page\":1,\"size\":20,\"pages\":0}");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Preview("pat", CharacterApiCredentialKind.PersonalAccessToken),
                transport);

            await client.Characters.ExecuteAsync<JObject>(
                CharacterApiOperations.CharacterList);

            Assert.That(transport.LastRequest!.Headers["Authorization"], Is.EqualTo("Bearer pat"));
            Assert.That(transport.LastRequest.Headers.ContainsKey("CONVAI-API-KEY"), Is.False);
        }

        [Test]
        public async Task TypedCharacterGet_UsesCanonicalGetRoute()
        {
            var transport = new RecordingTransport(
                "{\"user_id\":\"user-1\",\"character_id\":\"character-42\"," +
                "\"character_name\":\"Test\",\"voice_type\":\"test\",\"language_code\":\"en\"," +
                "\"description\":\"\",\"model_type\":\"\",\"is_narrative_driven\":false," +
                "\"timestamp\":\"2026-01-01T00:00:00Z\",\"memory_settings\":{}," +
                "\"character_traits\":{},\"metadata_filter\":{}}");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Preview("secret"), transport);

            await client.Characters.GetAsync("character-42");

            Assert.That(transport.LastRequest!.Method, Is.EqualTo(ConvaiApiHttpMethod.Get));
            Assert.That(transport.LastRequest.Uri.AbsolutePath, Is.EqualTo("/character/get"));
            Assert.That(transport.LastRequest.Uri.Query, Is.EqualTo("?character_id=character-42"));
        }

        [Test]
        public async Task TypedCharacterDelete_UsesPathParameterAndDeleteVerb()
        {
            var transport = new RecordingTransport("{\"message\":\"deleted\"}");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Preview("secret"), transport);

            await client.Characters.DeleteAsync("character/42");

            Assert.That(transport.LastRequest!.Method, Is.EqualTo(ConvaiApiHttpMethod.Delete));
            Assert.That(transport.LastRequest.Uri.AbsolutePath, Is.EqualTo("/character/character%2F42"));
        }

        [Test]
        public async Task RuntimeNarrativeClient_UsesReadOnlyCanonicalRoute()
        {
            var transport = new RecordingTransport("[]");
            using var client = new ConvaiCharacterNarrativeClient(
                new Uri("https://api2-stg.convai.com/"), "secret", transport: transport);

            await client.ListSectionsAsync("character-42");

            Assert.That(transport.LastRequest!.Method, Is.EqualTo(ConvaiApiHttpMethod.Get));
            Assert.That(transport.LastRequest.Uri.AbsolutePath,
                Is.EqualTo("/character/narrative/sections/list"));
            Assert.That(transport.LastRequest.Uri.Query, Is.EqualTo("?character_id=character-42"));
        }

        [Test]
        public void ExecuteAsync_RejectsCompatibilityWithoutExplicitOptIn()
        {
            var transport = new RecordingTransport("{}");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Preview("secret"), transport);

            ConvaiApiException exception = Assert.ThrowsAsync<ConvaiApiException>(() =>
                client.ExecuteAsync<object>(CharacterApiOperations.CloneCharacterUserCloneCharacterPost))!;

            Assert.That(exception.Category, Is.EqualTo(ConvaiApiErrorCategory.Contract));
            Assert.That(transport.LastRequest, Is.Null);
        }

        [Test]
        public void ResourceClient_RejectsOperationFromAnotherTag()
        {
            var transport = new RecordingTransport("{}");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Preview("secret"), transport);

            ConvaiApiException exception = Assert.ThrowsAsync<ConvaiApiException>(() =>
                client.Narrative.ExecuteAsync<object>(CharacterApiOperations.CharacterGet))!;

            Assert.That(exception.Category, Is.EqualTo(ConvaiApiErrorCategory.Contract));
        }

        [Test]
        public void CallerCancellation_RemainsOperationCanceledException()
        {
            using var source = new CancellationTokenSource();
            source.Cancel();
            using var transport = new HttpClientApiTransport(
                TimeSpan.FromSeconds(5), new CancellationHandler());
            var request = new ConvaiApiRequest(
                new Uri("https://example.invalid"), ConvaiApiHttpMethod.Get);

            Exception exception = Assert.CatchAsync<OperationCanceledException>(() =>
                transport.SendAsync(request, source.Token))!;
            Assert.That(exception, Is.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public void GeneratedConstructors_AcceptRequiredNullableValuesButRejectMissingIdentity()
        {
            var section = new SectionResponse(characterId: "character", sectionId: "section");
            Assert.That(section.BtConstants, Is.Null);
            Assert.That(section.Decisions, Is.Null);
            Assert.Throws<ArgumentNullException>(() => new SectionResponse(sectionId: "section"));
        }

        [TestCase("multipart/form-data")]
        [TestCase("text/event-stream")]
        public void JsonExecutor_RejectsUnsupportedMediaBeforeSending(string media)
        {
            var transport = new RecordingTransport("{}");
            using var client = new ConvaiCharacterApiClient(ConvaiCharacterApiOptions.Production("key"), transport);
            var operation = CharacterApiOperations.All.First(item =>
                item.Classification == CharacterApiOperationClass.PublicStable &&
                (item.RequestMediaType == media || item.ResponseMediaType == media));
            var error = Assert.ThrowsAsync<ConvaiApiException>(() => client.ExecuteAsync<object>(operation));
            Assert.That(error!.Category, Is.EqualTo(ConvaiApiErrorCategory.Contract));
            Assert.That(transport.LastRequest, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LegacyNativeTransport_CallerCancellationInterruptsRequestsAndDownloads(bool download)
        {
            using var source = new CancellationTokenSource();
            var handler = new PendingHandler();
            using var transport = new Convai.RestAPI.Transport.HttpClientTransport(TimeSpan.FromSeconds(10), handler);
            var uri = new Uri("https://example.invalid/");
            Task pending = download ? transport.DownloadBytesAsync(uri, source.Token) :
                transport.SendAsync(Convai.RestAPI.Transport.ConvaiHttpRequest.CreateBuilder(
                    uri, Convai.RestAPI.Transport.HttpMethod.Get).Build(), source.Token);
            source.Cancel();
            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(pending,
                "Legacy transport ignored caller cancellation.", 2000);
            Assert.That(handler.SawCancellation, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LegacyNativeTransport_TimeoutIsNotCallerCancellation(bool download)
        {
            using var transport = new Convai.RestAPI.Transport.HttpClientTransport(
                TimeSpan.FromMilliseconds(20), new PendingHandler());
            var uri = new Uri("https://example.invalid/");
            if (download)
            {
                var error = Assert.ThrowsAsync<ConvaiRestException>(() => transport.DownloadBytesAsync(uri));
                Assert.That(error!.Category, Is.EqualTo(ConvaiRestErrorCategory.Timeout));
            }
            else
            {
                var response = await transport.SendAsync(Convai.RestAPI.Transport.ConvaiHttpRequest.CreateBuilder(
                    uri, Convai.RestAPI.Transport.HttpMethod.Get).Build());
                Assert.That(response.IsTimeout, Is.True);
                Assert.That(ConvaiRestException.FromResponse(response).Category, Is.EqualTo(ConvaiRestErrorCategory.Timeout));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeApiTransport_TimeoutPreservesRequestUri(bool stream)
        {
            using var transport = new HttpClientApiTransport(TimeSpan.FromMilliseconds(20), new PendingHandler());
            var uri = new Uri("https://example.invalid/character/get?character_id=character-draft&workspace_id=workspace");
            var request = new ConvaiApiRequest(uri, ConvaiApiHttpMethod.Get);

            var error = Assert.ThrowsAsync<ConvaiApiException>(() => stream
                ? transport.SendSseAsync(request, _ => { })
                : transport.SendAsync(request));

            Assert.That(error!.Category, Is.EqualTo(ConvaiApiErrorCategory.Timeout));
            Assert.That(error.RequestUri, Is.EqualTo(uri));
        }

        [Test]
        public void AuthoringRead_MalformedResponsePreservesRequestUri()
        {
            var transport = new RecordingTransport("not-json");
            using var client = new ConvaiCharacterApiClient(
                ConvaiCharacterApiOptions.Production("key").WithReadContext("workspace", useDraft: false),
                transport);

            var error = Assert.ThrowsAsync<ConvaiApiException>(() => client.Characters.GetAsync("character"));

            Assert.That(error!.Category, Is.EqualTo(ConvaiApiErrorCategory.Serialization));
            Assert.That(error.RequestUri, Is.EqualTo(transport.LastRequest!.Uri));
            Assert.That(error.RequestUri!.Query, Does.Contain("character_id=character"));
            Assert.That(error.RequestUri.Query, Does.Contain("workspace_id=workspace"));
        }

        private sealed class PendingHandler : HttpMessageHandler
        {
            public bool SawCancellation { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { SawCancellation = true; throw; }
                throw new AssertionException("Pending request unexpectedly completed.");
            }
        }

        private sealed class RecordingTransport : IConvaiApiTransport
        {
            private readonly string _response;

            public RecordingTransport(string response) => _response = response;

            public ConvaiApiRequest? LastRequest { get; private set; }
            public ConvaiApiTransportCapabilities Capabilities { get; } = new(true);

            public Task<ConvaiApiResponse> SendAsync(
                ConvaiApiRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastRequest = request;
                return Task.FromResult(new ConvaiApiResponse(
                    request.Uri, 200, ConvaiApiRequest.Utf8(_response), "application/json",
                    new Dictionary<string, string>()));
            }

            public Task SendSseAsync(
                ConvaiApiRequest request,
                Action<ConvaiApiSseEvent> onEvent,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                onEvent(new ConvaiApiSseEvent(_response));
                return Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }

        private sealed class CancellationHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }

        private sealed class StalledSseHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new NeverCompletingReadStream())
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(response);
            }
        }

        private sealed class NeverCompletingReadStream : Stream
        {
            private readonly TaskCompletionSource<int> _pending = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) => _pending.Task;
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }

        private sealed class NullableDateContainer
        {
            [JsonProperty("value")]
            [JsonConverter(typeof(OpenAPIDateConverter))]
            public DateTime? Value { get; set; }
        }
    }
}
