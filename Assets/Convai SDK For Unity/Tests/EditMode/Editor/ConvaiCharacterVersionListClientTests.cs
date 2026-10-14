#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi;
using Convai.Editor.Inspectors;
using Convai.Tests.EditMode.Fixtures;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    public sealed class ConvaiCharacterVersionListClientTests
    {
        private const string CharacterId = "00000000-0000-4000-8000-000000000001";

        [TestCase(false, "api2.convai.com")]
        [TestCase(true, "api2-stg.convai.com")]
        public async Task Fetch_UsesConfiguredEndpoint_GetBareIdAndApiKey(bool preview, string expectedHost)
        {
            var transport = new RecordingTransport();
            ConvaiCharacterApiOptions options = preview
                ? ConvaiCharacterApiOptions.Preview("test-key")
                : ConvaiCharacterApiOptions.Production("test-key");

            await Fetch(options, transport);

            Assert.That(transport.Requests.Count, Is.EqualTo(1));
            ConvaiApiRequest request = transport.Requests[0];
            Assert.That(request.Method, Is.EqualTo(ConvaiApiHttpMethod.Get));
            Assert.That(request.Uri.Host, Is.EqualTo(expectedHost));
            Assert.That(request.Uri.AbsolutePath, Is.EqualTo("/character/versions/available"));
            Assert.That(request.Uri.Query, Is.EqualTo("?character_id=" + CharacterId));
            Assert.That(request.Headers["CONVAI-API-KEY"], Is.EqualTo("test-key"));
            Assert.That(request.Headers.ContainsKey("Authorization"), Is.False);
            Assert.That(request.Body, Is.Null);
            Assert.That(transport.DisposeCount, Is.Zero, "An injected transport belongs to its caller.");
        }

        [Test]
        public async Task Fetch_PreservesBasePathPatAndTimeout_WithoutWorkspaceOrDraftQuery()
        {
            var transport = new RecordingTransport();
            ConvaiCharacterApiOptions options = ConvaiCharacterApiOptions.Custom(
                    new Uri("https://example.invalid/gateway"), "cvpat-test", CharacterApiCredentialKind.PersonalAccessToken,
                    timeout: TimeSpan.FromSeconds(12))
                .WithReadContext("workspace with / symbols", useDraft: true);

            await Fetch(options, transport);

            Assert.That(transport.Requests.Count, Is.EqualTo(1));
            ConvaiApiRequest request = transport.Requests[0];
            Assert.That(request.Uri.AbsolutePath, Is.EqualTo("/gateway/character/versions/available"));
            Assert.That(request.Uri.Query, Is.EqualTo("?character_id=" + CharacterId));
            Assert.That(request.Headers["Authorization"], Is.EqualTo("Bearer cvpat-test"));
            Assert.That(request.Headers.ContainsKey("CONVAI-API-KEY"), Is.False);
            Assert.That(request.Timeout, Is.EqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(options.UseDraft, Is.True, "Fetching versions must not mutate the caller's read context.");
        }

        [Test]
        public async Task Fetch_PreservesExactReferencesLabelsLatestFlagsAndApiOrder()
        {
            var items = new JArray
            {
                Item("1.0", isLatest: true), Item("10.0"), Item("1.0.0"), Item("2.9"),
                Item("3.0-rc.1", reference: CharacterId + "-release-opaque")
            };
            var transport = new RecordingTransport { ResponseBody = Response(items).ToString() };

            CharacterVersionAvailability availability = await Fetch(Options(), transport);

            Assert.That(availability.CharacterId, Is.EqualTo(CharacterId));
            Assert.That(availability.CurrentReference, Is.EqualTo(CharacterId));
            CollectionAssert.AreEqual(new[] { "1.0", "10.0", "1.0.0", "2.9", "3.0-rc.1" },
                availability.Versions.Select(item => item.Version));
            CollectionAssert.AreEqual(items.Select(item => (string)item["reference"]!),
                availability.Versions.Select(item => item.Reference));
            CollectionAssert.AreEqual(new[] { true, false, false, false, false },
                availability.Versions.Select(item => item.IsLatest));
        }

        [Test]
        public async Task Fetch_EmptyVersionsRemainNormal_WithBareLiveReference()
        {
            var transport = new RecordingTransport();
            JObject response = Response(new JArray());
            transport.ResponseBody = response.ToString();

            CharacterVersionAvailability availability = await Fetch(Options(), transport);
            Assert.That(availability.Versions, Is.Empty);
            Assert.That(availability.CurrentReference, Is.EqualTo(CharacterId));
            Assert.That(transport.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void Availability_DefensivelyCopiesVersions_AndReturnsReadOnlyList()
        {
            var versions = new List<CharacterReleasedVersion>
            {
                new(CharacterId + "-1.0", "1.0", true)
            };
            var availability = new CharacterVersionAvailability(CharacterId, CharacterId, versions);
            versions.Clear();

            Assert.That(availability.Versions, Has.Count.EqualTo(1));
            var collection = (ICollection<CharacterReleasedVersion>)availability.Versions;
            Assert.That(collection.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => collection.Clear());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-uuid")]
        [TestCase("00000000-0000-4000-8000-000000000001-latest")]
        [TestCase("00000000-0000-4000-8000-000000000001-draft")]
        [TestCase("00000000-0000-4000-8000-000000000001-1.0")]
        [TestCase(" 00000000-0000-4000-8000-000000000001 ")]
        public async Task Fetch_InvalidOrQualifiedId_FailsBeforeNetwork(string characterId)
        {
            var transport = new RecordingTransport();

            await AsyncTestDeadline.ThrowsWithinAsync<ArgumentException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), characterId, transport: transport),
                "Only a bare UUID may be used to list versions.");

            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public async Task Fetch_NullOptions_FailsBeforeNetwork()
        {
            var transport = new RecordingTransport();

            await AsyncTestDeadline.ThrowsWithinAsync<ArgumentNullException>(
                ConvaiCharacterVersionListClient.FetchAsync(null!, CharacterId, transport: transport),
                "Options are required.");

            Assert.That(transport.Requests, Is.Empty);
        }

        [TestCase("character_id")]
        [TestCase("current")]
        [TestCase("current.reference")]
        [TestCase("versions")]
        [TestCase("versions[0].reference")]
        [TestCase("versions[0].version")]
        [TestCase("versions[0].is_latest")]
        public async Task Fetch_MissingRequiredField_IsAnError(string fieldPath)
        {
            JObject response = Response(new JArray(Item("1.0")));
            response.SelectToken(fieldPath)!.Parent!.Remove();
            var transport = new RecordingTransport { ResponseBody = response.ToString() };

            ConvaiApiException error = await AsyncTestDeadline.ThrowsWithinAsync<ConvaiApiException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), CharacterId, transport: transport),
                "Missing fields must not become a successful empty availability list.");

            Assert.That(error.Category, Is.EqualTo(ConvaiApiErrorCategory.Serialization));
        }

        [TestCase("character_id", "null")]
        [TestCase("current", "null")]
        [TestCase("current.reference", "null")]
        [TestCase("versions", "null")]
        [TestCase("versions", "{}")]
        [TestCase("versions[0]", "null")]
        [TestCase("versions[0].reference", "123")]
        [TestCase("versions[0].version", "123")]
        [TestCase("versions[0].is_latest", "null")]
        [TestCase("versions[0].is_latest", "\"false\"")]
        public async Task Fetch_WrongRequiredFieldType_IsAnError(string fieldPath, string replacement)
        {
            JObject response = Response(new JArray(Item("1.0")));
            response.SelectToken(fieldPath)!.Replace(JToken.Parse(replacement));
            var transport = new RecordingTransport { ResponseBody = response.ToString() };

            ConvaiApiException error = await AsyncTestDeadline.ThrowsWithinAsync<ConvaiApiException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), CharacterId, transport: transport),
                "Malformed field types must fail availability validation.");

            Assert.That(error.Category, Is.EqualTo(ConvaiApiErrorCategory.Serialization));
        }

        [TestCase("character_id", "00000000-0000-4000-8000-000000000002")]
        [TestCase("current.reference", "00000000-0000-4000-8000-000000000002")]
        [TestCase("current.reference", CharacterId + "-latest")]
        [TestCase("current.reference", CharacterId + "-draft")]
        [TestCase("versions[0].reference", "00000000-0000-4000-8000-000000000002-1.0")]
        [TestCase("versions[0].reference", CharacterId)]
        [TestCase("versions[0].reference", CharacterId + "-")]
        [TestCase("versions[0].reference", CharacterId + "-draft")]
        [TestCase("versions[0].reference", CharacterId + "-latest")]
        [TestCase("versions[0].reference", CharacterId + "-live")]
        [TestCase("versions[0].reference", CharacterId + "-1.0/other")]
        [TestCase("versions[0].reference", CharacterId + "-1.0\\other")]
        [TestCase("versions[0].reference", CharacterId + "-1.0?draft")]
        [TestCase("versions[0].reference", CharacterId + "-1.0#draft")]
        [TestCase("versions[0].reference", CharacterId + "-1.0 ")]
        [TestCase("versions[0].reference", CharacterId + "-1. 0")]
        [TestCase("versions[0].version", "")]
        [TestCase("versions[0].version", "draft")]
        [TestCase("versions[0].version", "latest")]
        public async Task Fetch_InvalidIdentityOrAlias_IsAnErrorWithoutDroppingEntries(
            string fieldPath, string replacement)
        {
            JObject response = Response(new JArray(Item("1.0")));
            response.SelectToken(fieldPath)!.Replace(new JValue(replacement));
            var transport = new RecordingTransport { ResponseBody = response.ToString() };

            ConvaiApiException error = await AsyncTestDeadline.ThrowsWithinAsync<ConvaiApiException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), CharacterId, transport: transport),
                "Cross-character, aliased or malformed references must not become usable choices.");

            Assert.That(error.Category, Is.EqualTo(ConvaiApiErrorCategory.Serialization));
        }

        [Test]
        public async Task Fetch_PreservesApiReferenceCasing_WhileMatchingUuidIdentity()
        {
            const string mixedId = "ABCDEFAB-ABCD-4ABC-8ABC-ABCDEFABCDEF";
            string returnedId = mixedId.ToLowerInvariant();
            var response = new JObject
            {
                ["character_id"] = returnedId,
                ["current"] = new JObject { ["reference"] = returnedId },
                ["versions"] = new JArray(new JObject
                {
                    ["reference"] = returnedId + "-1.0", ["version"] = "1.0", ["is_latest"] = false
                })
            };
            var transport = new RecordingTransport { ResponseBody = response.ToString() };

            var result = await ConvaiCharacterVersionListClient.FetchAsync(Options(), mixedId, transport: transport);

            Assert.That(result.CharacterId, Is.EqualTo(returnedId));
            Assert.That(result.CurrentReference, Is.EqualTo(returnedId));
            Assert.That(result.Versions[0].Reference, Is.EqualTo(returnedId + "-1.0"));
            Assert.That(transport.Requests[0].Uri.Query, Is.EqualTo("?character_id=" + mixedId));
        }

        [TestCase("not JSON")]
        [TestCase("null")]
        [TestCase("[]")]
        public async Task Fetch_MalformedResponse_PropagatesSerializationFailure(string body)
        {
            var transport = new RecordingTransport { ResponseBody = body };

            ConvaiApiException error = await AsyncTestDeadline.ThrowsWithinAsync<ConvaiApiException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), CharacterId, transport: transport),
                "Malformed JSON must remain visible to the inspector as an error.");

            Assert.That(error.Category, Is.EqualTo(ConvaiApiErrorCategory.Serialization));
        }

        [TestCase(400, ConvaiApiErrorCategory.BadRequest)]
        [TestCase(401, ConvaiApiErrorCategory.Authentication)]
        [TestCase(403, ConvaiApiErrorCategory.Authorization)]
        [TestCase(404, ConvaiApiErrorCategory.NotFound)]
        [TestCase(409, ConvaiApiErrorCategory.Conflict)]
        [TestCase(429, ConvaiApiErrorCategory.RateLimited)]
        [TestCase(500, ConvaiApiErrorCategory.Server)]
        public async Task Fetch_HttpFailure_PreservesStatusCategoryAndTrace(int status, ConvaiApiErrorCategory category)
        {
            var transport = new RecordingTransport { StatusCode = status };

            ConvaiApiException error = await AsyncTestDeadline.ThrowsWithinAsync<ConvaiApiException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), CharacterId, transport: transport),
                "An API error must propagate without fallback requests.");

            Assert.That(error.StatusCode, Is.EqualTo(status));
            Assert.That(error.Category, Is.EqualTo(category));
            Assert.That(error.RequestTraceId, Is.EqualTo("list-trace"));
            Assert.That(transport.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Fetch_TransportFailure_IsNotWrappedOrSwallowed()
        {
            var expected = new ConvaiApiException("test transport failure", ConvaiApiErrorCategory.Transport);
            var transport = new RecordingTransport
            {
                Handler = (_, _) => Task.FromException<ConvaiApiResponse>(expected)
            };

            ConvaiApiException actual = await AsyncTestDeadline.ThrowsWithinAsync<ConvaiApiException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), CharacterId, transport: transport),
                "Transport failure should reach the inspector unchanged.");

            Assert.That(actual, Is.SameAs(expected));
        }

        [Test]
        public async Task Fetch_PreCancelledToken_DoesNotSendRequest()
        {
            var transport = new RecordingTransport();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(
                ConvaiCharacterVersionListClient.FetchAsync(Options(), CharacterId, cancellation.Token, transport),
                "Closing an inspector before fetch must cancel without network activity.");

            Assert.That(transport.Requests, Is.Empty);
        }

        [Test]
        public async Task Fetch_InFlightCancellation_PropagatesToTransportAndCaller()
        {
            using var cancellation = new CancellationTokenSource();
            var transport = new RecordingTransport
            {
                Handler = async (request, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    throw new InvalidOperationException("A cancelled request must not complete normally.");
                }
            };
            Task<CharacterVersionAvailability> fetch = ConvaiCharacterVersionListClient.FetchAsync(
                Options(), CharacterId, cancellation.Token, transport);
            Assert.That(transport.Requests.Count, Is.EqualTo(1));
            cancellation.Cancel();

            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(fetch,
                "In-flight inspector requests must honor cancellation.");

            Assert.That(transport.LastCancellationToken, Is.EqualTo(cancellation.Token));
            Assert.That(transport.DisposeCount, Is.Zero);
        }

        private static ConvaiCharacterApiOptions Options() => ConvaiCharacterApiOptions.Production("test-key");

        private static async Task<CharacterVersionAvailability> Fetch(ConvaiCharacterApiOptions options,
            RecordingTransport transport)
        {
            Task<CharacterVersionAvailability> fetch = ConvaiCharacterVersionListClient.FetchAsync(
                options, CharacterId, transport: transport);
            await AsyncTestDeadline.WithinAsync(fetch, "The in-memory version request must complete.");
            return await fetch;
        }

        private static JObject Item(string version, bool isLatest = false, string? reference = null) => new()
        {
            ["reference"] = reference ?? CharacterId + "-" + version,
            ["version"] = version,
            ["is_latest"] = isLatest
        };

        private static JObject Response(JArray items) => new()
        {
            ["character_id"] = CharacterId,
            ["current"] = new JObject { ["reference"] = CharacterId },
            ["versions"] = items
        };

        private sealed class RecordingTransport : IConvaiApiTransport
        {
            public readonly List<ConvaiApiRequest> Requests = new();
            public string ResponseBody = Response(new JArray()).ToString();
            public int StatusCode = 200;
            public int DisposeCount;
            public CancellationToken LastCancellationToken;
            public Func<ConvaiApiRequest, CancellationToken, Task<ConvaiApiResponse>>? Handler;
            public ConvaiApiTransportCapabilities Capabilities => new(false);

            public Task<ConvaiApiResponse> SendAsync(ConvaiApiRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                LastCancellationToken = cancellationToken;
                return Handler?.Invoke(request, cancellationToken) ?? Task.FromResult(new ConvaiApiResponse(
                    request.Uri, StatusCode, ConvaiApiRequest.Utf8(ResponseBody), "application/json",
                    new Dictionary<string, string> { ["x-request-trace-id"] = "list-trace" }));
            }

            public Task SendSseAsync(ConvaiApiRequest request, Action<ConvaiApiSseEvent> onEvent,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public void Dispose() => DisposeCount++;
        }
    }
}
