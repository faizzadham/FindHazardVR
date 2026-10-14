#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Convai.Api.Transport
{
    public sealed class ConvaiApiResponse
    {
        public ConvaiApiResponse(
            Uri uri,
            int statusCode,
            byte[]? body,
            string? contentType,
            IReadOnlyDictionary<string, string>? headers)
        {
            Uri = uri;
            StatusCode = statusCode;
            Body = body ?? Array.Empty<byte>();
            ContentType = contentType ?? string.Empty;
            Headers = headers ?? new Dictionary<string, string>();
        }

        public Uri Uri { get; }
        public int StatusCode { get; }
        public byte[] Body { get; }
        public string ContentType { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public bool IsSuccess => StatusCode >= 200 && StatusCode <= 299;
        public string BodyText => Encoding.UTF8.GetString(Body);

        public string? Header(string name) =>
            Headers.TryGetValue(name, out string? value) ? value : null;
    }

    public sealed class ConvaiApiSseEvent
    {
        public ConvaiApiSseEvent(string data, string? eventName = null, string? id = null)
        {
            Data = data;
            EventName = eventName;
            Id = id;
        }

        public string Data { get; }
        public string? EventName { get; }
        public string? Id { get; }
    }

    public readonly struct ConvaiApiTransportCapabilities
    {
        public ConvaiApiTransportCapabilities(bool incrementalSse)
        {
            IncrementalSse = incrementalSse;
        }

        public bool IncrementalSse { get; }
    }
}
