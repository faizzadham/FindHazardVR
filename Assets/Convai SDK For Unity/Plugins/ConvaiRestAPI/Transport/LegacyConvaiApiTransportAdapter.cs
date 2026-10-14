#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;

namespace Convai.RestAPI.Transport
{
    /// <summary>Allows the 4.x transport injection seam to drive owner-specific clients.</summary>
    internal sealed class LegacyConvaiApiTransportAdapter : IConvaiApiTransport
    {
        private readonly IConvaiHttpTransport _transport;

        internal LegacyConvaiApiTransportAdapter(IConvaiHttpTransport transport) =>
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

        public ConvaiApiTransportCapabilities Capabilities => new(false);

        public async Task<ConvaiApiResponse> SendAsync(
            ConvaiApiRequest request,
            CancellationToken cancellationToken = default)
        {
            ConvaiHttpRequest.Builder builder = ConvaiHttpRequest.CreateBuilder(
                request.Uri,
                ConvertMethod(request.Method));
            builder.WithHeaders(request.Headers);
            if (request.Body != null)
                builder.WithBody(Encoding.UTF8.GetString(request.Body));
            if (request.Timeout.HasValue)
                builder.WithTimeout(request.Timeout.Value);

            ConvaiHttpResponse response = await _transport.SendAsync(builder.Build(), cancellationToken)
                .ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            if (response.HasTransportError)
                throw new ConvaiApiException(
                    "The legacy transport could not reach the configured Convai service.",
                    response.IsTimeout ? ConvaiApiErrorCategory.Timeout : ConvaiApiErrorCategory.Transport,
                    responseBody: response.TransportError,
                    requestUri: response.Url);
            return new ConvaiApiResponse(
                response.Url,
                response.StatusCodeInt,
                ConvaiApiRequest.Utf8(response.Body),
                response.Headers.TryGetValue("Content-Type", out string contentType) ? contentType : null,
                response.Headers);
        }

        public Task SendSseAsync(
            ConvaiApiRequest request,
            Action<ConvaiApiSseEvent> onEvent,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The legacy transport adapter does not support SSE.");

        public void Dispose()
        {
            // The owning ConvaiRestClient disposes the wrapped transport.
        }

        private static HttpMethod ConvertMethod(ConvaiApiHttpMethod method) => method switch
        {
            ConvaiApiHttpMethod.Get => HttpMethod.Get,
            ConvaiApiHttpMethod.Post => HttpMethod.Post,
            ConvaiApiHttpMethod.Put => HttpMethod.Put,
            ConvaiApiHttpMethod.Delete => HttpMethod.Delete,
            _ => throw new NotSupportedException($"The 4.x transport cannot send {method} requests.")
        };
    }
}
