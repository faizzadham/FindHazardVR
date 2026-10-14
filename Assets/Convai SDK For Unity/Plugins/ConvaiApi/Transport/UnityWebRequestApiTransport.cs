#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Convai.Api.Transport
{
    public sealed class UnityWebRequestApiTransport : IConvaiApiTransport
    {
        private readonly TimeSpan _defaultTimeout;
        private bool _disposed;

        public UnityWebRequestApiTransport(TimeSpan defaultTimeout)
        {
            _defaultTimeout = defaultTimeout;
        }

        public ConvaiApiTransportCapabilities Capabilities { get; } = new(true);

        public async Task<ConvaiApiResponse> SendAsync(
            ConvaiApiRequest request,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            using UnityWebRequest webRequest = Create(request, new DownloadHandlerBuffer());
            await Send(webRequest, request, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            return Response(webRequest, request.Uri);
        }

        public async Task SendSseAsync(
            ConvaiApiRequest request,
            Action<ConvaiApiSseEvent> onEvent,
            CancellationToken cancellationToken = default)
        {
            if (onEvent == null) throw new ArgumentNullException(nameof(onEvent));
            ThrowIfDisposed();
            using var handler = new SseDownloadHandler(onEvent);
            using UnityWebRequest webRequest = Create(request, handler);
            await Send(webRequest, request, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        private async Task Send(
            UnityWebRequest webRequest,
            ConvaiApiRequest request,
            CancellationToken cancellationToken)
        {
            UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    webRequest.Abort();
                    throw new OperationCanceledException(cancellationToken);
                }
                await Task.Yield();
            }

            if (IsTransportFailure(webRequest.result))
            {
                throw TransportFailure(request.Uri, webRequest.error);
            }

            if (webRequest.responseCode < 200 || webRequest.responseCode > 299)
                throw ConvaiApiException.FromResponse(Response(webRequest, request.Uri));
        }

        internal static bool IsTransportFailure(UnityWebRequest.Result result) =>
            result == UnityWebRequest.Result.ConnectionError ||
            result == UnityWebRequest.Result.DataProcessingError;

        internal static ConvaiApiException TransportFailure(Uri uri, string? error)
        {
            bool timedOut = UnityWebRequestErrors.IsTimeout(error);
            return new ConvaiApiException(
                timedOut ? "Convai API request timed out." : "Convai API transport failed.",
                timedOut ? ConvaiApiErrorCategory.Timeout : ConvaiApiErrorCategory.Transport,
                requestUri: uri);
        }

        private UnityWebRequest Create(ConvaiApiRequest request, DownloadHandler downloadHandler)
        {
            string method = request.Method switch
            {
                ConvaiApiHttpMethod.Get => "GET",
                ConvaiApiHttpMethod.Post => "POST",
                ConvaiApiHttpMethod.Put => "PUT",
                ConvaiApiHttpMethod.Patch => "PATCH",
                ConvaiApiHttpMethod.Delete => "DELETE",
                _ => throw new ArgumentOutOfRangeException()
            };
            var webRequest = new UnityWebRequest(request.Uri, method)
            {
                downloadHandler = downloadHandler,
                timeout = (int)Math.Ceiling((request.Timeout ?? _defaultTimeout).TotalSeconds)
            };
            if (request.Body != null)
            {
                webRequest.uploadHandler = new UploadHandlerRaw(request.Body);
                webRequest.SetRequestHeader("Content-Type", request.ContentType ?? "application/json");
            }
            webRequest.SetRequestHeader("Accept", request.Accept);
            foreach (KeyValuePair<string, string> header in request.Headers)
                webRequest.SetRequestHeader(header.Key, header.Value);
            return webRequest;
        }

        private static ConvaiApiResponse Response(UnityWebRequest request, Uri uri)
        {
            IReadOnlyDictionary<string, string> headers = request.GetResponseHeaders() ??
                new Dictionary<string, string>();
            return new ConvaiApiResponse(
                uri,
                (int)request.responseCode,
                request.downloadHandler?.data,
                headers.FirstOrDefault(pair => string.Equals(
                    pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value,
                headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnityWebRequestApiTransport));
        }

        public void Dispose() => _disposed = true;

        private sealed class SseDownloadHandler : DownloadHandlerScript
        {
            private readonly Action<ConvaiApiSseEvent> _onEvent;
            private readonly SseChunkDecoder _decoder = new();

            public SseDownloadHandler(Action<ConvaiApiSseEvent> onEvent) : base(new byte[8 * 1024])
            {
                _onEvent = onEvent;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return true;
                _decoder.Push(data, dataLength, _onEvent);
                return true;
            }
        }
    }
}
