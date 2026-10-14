#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Newtonsoft.Json;

namespace Convai.CoreApi
{
    /// <summary>Hand-maintained client for the core-service room connection contract.</summary>
    public sealed class ConvaiCoreApiClient : IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly ConvaiCoreApiOptions _options;
        private readonly IConvaiApiTransport _transport;
        private readonly bool _ownsTransport;
        private bool _disposed;

        public ConvaiCoreApiClient(
            ConvaiCoreApiOptions options,
            IConvaiApiTransport? transport = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? ConvaiApiTransportFactory.Create(options.Timeout);
            _ownsTransport = transport == null;
        }

        public async Task<TResponse> ConnectAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConvaiCoreApiClient));
            if (request == null) throw new ArgumentNullException(nameof(request));

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_options.CredentialKind == CoreApiCredentialKind.ApiKey)
                headers["x-api-key"] = _options.Credential;
            else
                headers["API-AUTH-TOKEN"] = _options.Credential;

            string json = JsonConvert.SerializeObject(request, JsonSettings);
            var transportRequest = new ConvaiApiRequest(
                _options.ConnectUri,
                ConvaiApiHttpMethod.Post,
                headers,
                ConvaiApiRequest.Utf8(json),
                "application/json",
                "application/json",
                _options.Timeout);

            ConvaiApiResponse response = await _transport.SendAsync(transportRequest, cancellationToken)
                .ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            if (!response.IsSuccess)
                throw ConvaiApiException.FromResponse(response);

            try
            {
                TResponse? result = JsonConvert.DeserializeObject<TResponse>(response.BodyText, JsonSettings);
                return result ?? throw new JsonSerializationException("Core response was null.");
            }
            catch (JsonException ex)
            {
                throw new ConvaiApiException(
                    "The core-service response did not match the pinned contract.",
                    ConvaiApiErrorCategory.Serialization,
                    response.StatusCode,
                    response.Header("x-request-trace-id"),
                    innerException: ex,
                    requestUri: response.Uri);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsTransport) _transport.Dispose();
        }
    }
}
