using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Convai.RestAPI.Transport;
using Convai.Runtime.Core.Configuration;
using NUnit.Framework;
using TransportHttpMethod = Convai.RestAPI.Transport.HttpMethod;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class EndpointAuthTokenProviderTests
    {
        [TestCase(ConvaiAuthTokenHttpMethod.Get, TransportHttpMethod.Get, null)]
        [TestCase(ConvaiAuthTokenHttpMethod.Post, TransportHttpMethod.Post, "{}")]
        public async Task GetTokenAsync_ValidResponse_UsesConfiguredRequestAndParsesResult(
            ConvaiAuthTokenHttpMethod configuredMethod,
            TransportHttpMethod expectedMethod,
            string expectedBody)
        {
            var transport = new CapturingTransport
            {
                ResponseFactory = request => ConvaiHttpResponse.Success(
                    HttpStatusCode.OK,
                    "{\"payload\":{\"credential\":\"  short-lived-token  \"}," +
                    "\"expirationTime\":\"2026-06-11T13:00:00Z\"}",
                    request.Url)
            };
            var provider = new EndpointAuthTokenProvider(
                "  https://auth.example.com/convai/token?player=42  ",
                configuredMethod,
                "payload.credential",
                new[]
                {
                    new ConvaiAuthTokenHeader("Authorization", "Bearer player-session"),
                    new ConvaiAuthTokenHeader("", "ignored")
                },
                transport,
                TimeSpan.FromSeconds(12));

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Token, Is.EqualTo("short-lived-token"));
            Assert.That(result.ExpiresAtUtc,
                Is.EqualTo(new DateTimeOffset(2026, 6, 11, 13, 0, 0, TimeSpan.Zero)));
            Assert.That(result.ErrorMessage, Is.Empty);

            ConvaiHttpRequest request = transport.LastRequest;
            Assert.That(request, Is.Not.Null);
            Assert.That(request.Url.AbsoluteUri,
                Is.EqualTo("https://auth.example.com/convai/token?player=42"));
            Assert.That(request.Method, Is.EqualTo(expectedMethod));
            Assert.That(request.Body, Is.EqualTo(expectedBody));
            Assert.That(request.Timeout, Is.EqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(request.Headers["Authorization"], Is.EqualTo("Bearer player-session"));
            Assert.That(request.Headers.ContainsKey("CONVAI-API-KEY"), Is.False);
            Assert.That(request.Headers.ContainsKey("x-api-key"), Is.False);
            Assert.That(transport.LastCancellationToken, Is.EqualTo(CancellationToken.None));
        }

        [Test]
        public async Task GetTokenAsync_NonSuccessResponse_ReturnsStatusFailure()
        {
            var transport = new CapturingTransport
            {
                ResponseFactory = request => ConvaiHttpResponse.Failure(
                    HttpStatusCode.BadGateway,
                    "upstream unavailable",
                    request.Url)
            };
            var provider = CreateProvider(transport);

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Token, Is.Empty);
            Assert.That(result.ErrorMessage, Is.EqualTo("Auth token endpoint returned HTTP 502."));
        }

        [Test]
        public async Task GetTokenAsync_TransportFailure_ReturnsRedactedFailure()
        {
            var transport = new CapturingTransport
            {
                ResponseFactory = request => ConvaiHttpResponse.TransportFailure(
                    request.Url,
                    "request timed out")
            };
            var provider = CreateProvider(transport);

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo("Auth token endpoint transport failed."));
            Assert.That(result.ErrorMessage, Does.Not.Contain("request timed out"));
        }

        [Test]
        public async Task GetTokenAsync_TransportThrows_ReturnsRedactedFailureType()
        {
            var transport = new CapturingTransport
            {
                Exception = new InvalidOperationException("sensitive backend response")
            };
            var provider = CreateProvider(transport);

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage,
                Is.EqualTo("Auth token endpoint request failed (InvalidOperationException)."));
            Assert.That(result.ErrorMessage, Does.Not.Contain("sensitive backend response"));
        }

        [TestCase("not-json", "Auth token endpoint returned malformed JSON.")]
        [TestCase("{}", "Auth token response field 'apiAuthToken' was not found.")]
        [TestCase("{\"apiAuthToken\":\"   \"}", "Auth token response field 'apiAuthToken' was empty.")]
        [TestCase("{\"apiAuthToken\":123}", "Auth token response field 'apiAuthToken' was empty.")]
        [TestCase(
            "{\"apiAuthToken\":\"token\",\"expirationTime\":\"not-a-date\"}",
            "Auth token response contained an invalid expirationTime.")]
        public async Task GetTokenAsync_InvalidJsonContract_ReturnsExpectedFailure(
            string responseBody,
            string expectedError)
        {
            var transport = new CapturingTransport
            {
                ResponseFactory = request => ConvaiHttpResponse.Success(
                    HttpStatusCode.OK,
                    responseBody,
                    request.Url)
            };
            var provider = CreateProvider(transport);

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Token, Is.Empty);
            Assert.That(result.ErrorMessage, Is.EqualTo(expectedError));
        }

        [TestCase("")]
        [TestCase("relative/token")]
        [TestCase("ftp://auth.example.com/token")]
        [TestCase("http://auth.example.com/token")]
        [TestCase("http://192.168.1.20/token")]
        [TestCase("https://")]
        public async Task GetTokenAsync_InvalidEndpoint_ReturnsConfigurationFailureWithoutTransportCall(
            string endpointUrl)
        {
            var transport = new CapturingTransport();
            var provider = new EndpointAuthTokenProvider(endpointUrl, transport: transport);

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage,
                Is.EqualTo(EndpointAuthTokenProvider.InvalidEndpointMessage));
            Assert.That(transport.SendCount, Is.Zero);
        }

        [TestCase("http://127.0.0.1:8787/v1/convai/token")]
        [TestCase("http://localhost:8787/v1/convai/token")]
        [TestCase("http://[::1]:8787/v1/convai/token")]
        public async Task GetTokenAsync_HttpLoopbackEndpoint_IsAllowedForLocalDevelopment(
            string endpointUrl)
        {
            var transport = new CapturingTransport();
            var provider = new EndpointAuthTokenProvider(endpointUrl, transport: transport);

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(transport.SendCount, Is.EqualTo(1));
        }

        [Test]
        public void GetTokenAsync_CallerCancellation_PropagatesOperationCanceledException()
        {
            var transport = new CapturingTransport();
            var provider = CreateProvider(transport);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await provider.GetTokenAsync(cancellation.Token));
            Assert.That(transport.SendCount, Is.Zero);
        }

        [Test]
        public void GetTokenAsync_WhenTransportFaultRacesCallerCancellation_PropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            var transport = new CapturingTransport
            {
                BeforeResponse = cancellation.Cancel,
                Exception = new InvalidOperationException("transport fault after cancellation")
            };
            var provider = CreateProvider(transport);

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await provider.GetTokenAsync(cancellation.Token));
        }

        private static EndpointAuthTokenProvider CreateProvider(IConvaiHttpTransport transport) =>
            new("https://auth.example.com/convai-token", transport: transport);

        private sealed class CapturingTransport : IConvaiHttpTransport
        {
            public Action BeforeResponse { get; set; }
            public Exception Exception { get; set; }
            public CancellationToken LastCancellationToken { get; private set; }
            public ConvaiHttpRequest LastRequest { get; private set; }
            public Func<ConvaiHttpRequest, ConvaiHttpResponse> ResponseFactory { get; set; }
            public int SendCount { get; private set; }

            public Task<ConvaiHttpResponse> SendAsync(
                ConvaiHttpRequest request,
                CancellationToken cancellationToken = default)
            {
                SendCount++;
                LastRequest = request;
                LastCancellationToken = cancellationToken;
                BeforeResponse?.Invoke();

                if (Exception != null)
                    return Task.FromException<ConvaiHttpResponse>(Exception);

                ConvaiHttpResponse response = ResponseFactory?.Invoke(request)
                                              ?? ConvaiHttpResponse.Success(
                                                  HttpStatusCode.OK,
                                                  "{\"apiAuthToken\":\"token\"}",
                                                  request.Url);
                return Task.FromResult(response);
            }

            public Task<byte[]> DownloadBytesAsync(
                Uri url,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(Array.Empty<byte>());

            public void Dispose()
            {
            }
        }
    }
}
