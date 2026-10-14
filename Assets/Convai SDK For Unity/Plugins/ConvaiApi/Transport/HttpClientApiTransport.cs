#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Convai.Api.Transport
{
    public sealed class HttpClientApiTransport : IConvaiApiTransport
    {
        private readonly HttpClient _client;
        private readonly TimeSpan _defaultTimeout;
        private bool _disposed;

        public HttpClientApiTransport(TimeSpan defaultTimeout, HttpMessageHandler? handler = null)
        {
            _defaultTimeout = defaultTimeout;
            _client = handler == null ? new HttpClient() : new HttpClient(handler, true);
            _client.Timeout = Timeout.InfiniteTimeSpan;
        }

        public ConvaiApiTransportCapabilities Capabilities { get; } = new(true);

        public async Task<ConvaiApiResponse> SendAsync(
            ConvaiApiRequest request,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            using CancellationTokenSource timeout = CreateTimeout(request, cancellationToken);
            try
            {
                using HttpRequestMessage message = CreateMessage(request);
                using HttpResponseMessage response = await _client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseContentRead,
                    timeout.Token).ConfigureAwait(false);
                byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return new ConvaiApiResponse(
                    request.Uri,
                    (int)response.StatusCode,
                    body,
                    response.Content.Headers.ContentType?.MediaType,
                    ReadHeaders(response));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                throw new ConvaiApiException(
                    $"Convai API request timed out after {(request.Timeout ?? _defaultTimeout).TotalSeconds:F1} seconds.",
                    ConvaiApiErrorCategory.Timeout,
                    innerException: ex,
                    requestUri: request.Uri);
            }
            catch (HttpRequestException ex)
            {
                throw new ConvaiApiException(
                    "Convai API transport failed.",
                    ConvaiApiErrorCategory.Transport,
                    innerException: ex,
                    requestUri: request.Uri);
            }
        }

        public async Task SendSseAsync(
            ConvaiApiRequest request,
            Action<ConvaiApiSseEvent> onEvent,
            CancellationToken cancellationToken = default)
        {
            if (onEvent == null) throw new ArgumentNullException(nameof(onEvent));
            ThrowIfDisposed();
            using CancellationTokenSource timeout = CreateTimeout(request, cancellationToken);
            try
            {
                using HttpRequestMessage message = CreateMessage(request);
                using HttpResponseMessage response = await _client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    throw ConvaiApiException.FromResponse(new ConvaiApiResponse(
                        request.Uri, (int)response.StatusCode, body,
                        response.Content.Headers.ContentType?.MediaType, ReadHeaders(response)));
                }

                using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var parser = new SseEventParser();
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    string? line = await ReadLineAsync(reader, timeout.Token).ConfigureAwait(false);
                    if (line == null) break;
                    ConvaiApiSseEvent? parsed = parser.Push(line);
                    if (parsed != null) onEvent(parsed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                throw new ConvaiApiException(
                    "Convai API SSE request timed out.",
                    ConvaiApiErrorCategory.Timeout,
                    innerException: ex,
                    requestUri: request.Uri);
            }
        }

        private static async Task<string?> ReadLineAsync(
            StreamReader reader,
            CancellationToken cancellationToken)
        {
            Task<string?> read = reader.ReadLineAsync();
            if (read.IsCompleted)
                return await read.ConfigureAwait(false);

            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
                    throw new OperationCanceledException(cancellationToken);
                return await read.ConfigureAwait(false);
            }
        }

        private CancellationTokenSource CreateTimeout(ConvaiApiRequest request, CancellationToken token)
        {
            CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(token);
            source.CancelAfter(request.Timeout ?? _defaultTimeout);
            return source;
        }

        private static HttpRequestMessage CreateMessage(ConvaiApiRequest request)
        {
            System.Net.Http.HttpMethod method = request.Method switch
            {
                ConvaiApiHttpMethod.Get => System.Net.Http.HttpMethod.Get,
                ConvaiApiHttpMethod.Post => System.Net.Http.HttpMethod.Post,
                ConvaiApiHttpMethod.Put => System.Net.Http.HttpMethod.Put,
                ConvaiApiHttpMethod.Patch => new System.Net.Http.HttpMethod("PATCH"),
                ConvaiApiHttpMethod.Delete => System.Net.Http.HttpMethod.Delete,
                _ => throw new ArgumentOutOfRangeException()
            };
            var message = new HttpRequestMessage(method, request.Uri);
            message.Headers.TryAddWithoutValidation("Accept", request.Accept);
            foreach (KeyValuePair<string, string> header in request.Headers)
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Body != null)
            {
                message.Content = new ByteArrayContent(request.Body);
                message.Content.Headers.TryAddWithoutValidation(
                    "Content-Type",
                    request.ContentType ?? "application/json");
            }
            return message;
        }

        private static IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response)
        {
            return response.Headers.Concat(response.Content.Headers)
                .ToDictionary(item => item.Key, item => string.Join(",", item.Value), StringComparer.OrdinalIgnoreCase);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HttpClientApiTransport));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _client.Dispose();
        }
    }
}
