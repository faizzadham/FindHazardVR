#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Convai.Api.Transport
{
    public enum ConvaiApiHttpMethod
    {
        Get,
        Post,
        Put,
        Patch,
        Delete
    }

    public sealed class ConvaiApiRequest
    {
        public ConvaiApiRequest(
            Uri uri,
            ConvaiApiHttpMethod method,
            IReadOnlyDictionary<string, string>? headers = null,
            byte[]? body = null,
            string? contentType = null,
            string accept = "application/json",
            TimeSpan? timeout = null)
        {
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            Method = method;
            Headers = headers ?? EmptyHeaders;
            Body = body;
            ContentType = contentType;
            Accept = string.IsNullOrWhiteSpace(accept) ? "application/json" : accept;
            Timeout = timeout;
        }

        private static IReadOnlyDictionary<string, string> EmptyHeaders { get; } =
            new Dictionary<string, string>();

        public Uri Uri { get; }
        public ConvaiApiHttpMethod Method { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public byte[]? Body { get; }
        public string? ContentType { get; }
        public string Accept { get; }
        public TimeSpan? Timeout { get; }

        public static byte[] Utf8(string content) => Encoding.UTF8.GetBytes(content ?? string.Empty);
    }
}
