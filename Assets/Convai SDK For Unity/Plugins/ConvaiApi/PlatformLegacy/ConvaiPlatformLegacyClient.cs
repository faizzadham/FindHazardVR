#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Newtonsoft.Json;

namespace Convai.PlatformApi.Legacy
{
    /// <summary>Transport client for uncontracted platform/account compatibility routes.</summary>
    public sealed class ConvaiPlatformLegacyClient : IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly ConvaiPlatformLegacyOptions _options;
        private readonly IConvaiApiTransport _transport;
        private readonly bool _ownsTransport;
        private bool _disposed;

        public ConvaiPlatformLegacyClient(
            ConvaiPlatformLegacyOptions options,
            IConvaiApiTransport? transport = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? ConvaiApiTransportFactory.Create(options.Timeout);
            _ownsTransport = transport == null;
        }

        public async Task<TResponse> PostAsync<TResponse>(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default)
        {
            ConvaiApiResponse response = await PostCoreAsync(relativePath, body, cancellationToken)
                .ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            try
            {
                TResponse? result = JsonConvert.DeserializeObject<TResponse>(response.BodyText, JsonSettings);
                return result ?? throw new JsonSerializationException("Platform response was null.");
            }
            catch (JsonException ex)
            {
                throw new ConvaiApiException(
                    "The platform compatibility response did not match the expected SDK 4.x model.",
                    ConvaiApiErrorCategory.Serialization,
                    response.StatusCode,
                    response.Header("x-request-trace-id"),
                    innerException: ex,
                    requestUri: response.Uri);
            }
        }

        public async Task PostAsync(
            string relativePath,
            object? body,
            CancellationToken cancellationToken = default) =>
            await PostCoreAsync(relativePath, body, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);

        private async Task<ConvaiApiResponse> PostCoreAsync(
            string relativePath,
            object? body,
            CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConvaiPlatformLegacyClient));
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path is required.", nameof(relativePath));

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_options.CredentialKind == PlatformLegacyCredentialKind.ApiKey)
                headers["CONVAI-API-KEY"] = _options.Credential;
            else
                headers["API-AUTH-TOKEN"] = _options.Credential;

            byte[]? payload = body == null
                ? null
                : ConvaiApiRequest.Utf8(JsonConvert.SerializeObject(body, JsonSettings));
            var request = new ConvaiApiRequest(
                new Uri(_options.BaseUri, relativePath.TrimStart('/')),
                ConvaiApiHttpMethod.Post,
                headers,
                payload,
                payload == null ? null : "application/json",
                "application/json",
                _options.Timeout);

            ConvaiApiResponse response = await _transport.SendAsync(request, cancellationToken)
                .ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            if (!response.IsSuccess)
                throw ConvaiApiException.FromResponse(response);
            return response;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsTransport) _transport.Dispose();
        }
    }
}
