#nullable enable
using System;
using System.Net;
using Convai.Api.Transport;
using Convai.RestAPI.Transport;

namespace Convai.RestAPI
{
    /// <summary>
    /// Categories of errors that can occur when making REST API calls.
    /// </summary>
    public enum ConvaiRestErrorCategory
    {
        /// <summary>
        /// Unknown or uncategorized error.
        /// </summary>
        Unknown,

        /// <summary>
        /// Network or transport-level failure (connection error, timeout, etc.).
        /// </summary>
        Transport,

        /// <summary>
        /// Authentication or authorization failure (401, 403).
        /// </summary>
        Authentication,

        /// <summary>
        /// Resource not found (404).
        /// </summary>
        NotFound,

        /// <summary>
        /// Validation or bad request error (400).
        /// </summary>
        BadRequest,

        /// <summary>
        /// Rate limiting (429).
        /// </summary>
        RateLimited,

        /// <summary>
        /// Server-side error (5xx).
        /// </summary>
        ServerError,

        /// <summary>
        /// Failed to parse/deserialize the response.
        /// </summary>
        ParseError,

        /// <summary>
        /// Request was cancelled.
        /// </summary>
        Cancelled,

        /// <summary>Resource state conflicts with the request (409).</summary>
        Conflict,

        /// <summary>The request exceeded its timeout.</summary>
        Timeout
    }

    /// <summary>
    /// Exception thrown when a Convai REST API call fails.
    /// Contains detailed information about the failure for debugging and user feedback.
    /// </summary>
    public class ConvaiRestException : Exception
    {
        /// <summary>
        /// The category of error that occurred.
        /// </summary>
        public ConvaiRestErrorCategory Category { get; }

        /// <summary>
        /// The HTTP status code, if available (0 for transport errors).
        /// </summary>
        public HttpStatusCode StatusCode { get; }

        /// <summary>
        /// The numeric HTTP status code.
        /// </summary>
        public int StatusCodeInt => (int)StatusCode;

        /// <summary>
        /// The URL that was requested, if available.
        /// </summary>
        public Uri? Url { get; }

        /// <summary>
        /// The response body, if available (may be truncated).
        /// </summary>
        public string? ResponseBody { get; }

        public string? RequestTraceId => (InnerException as ConvaiApiException)?.RequestTraceId;
        public TimeSpan? RetryAfter => (InnerException as ConvaiApiException)?.RetryAfter;

        /// <summary>
        /// Creates a new ConvaiRestException.
        /// </summary>
        public ConvaiRestException(
            string message,
            ConvaiRestErrorCategory category,
            HttpStatusCode statusCode = 0,
            Uri? url = null,
            string? responseBody = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Category = category;
            StatusCode = statusCode;
            Url = url;
            ResponseBody = responseBody;
        }

        /// <summary>
        /// Creates an exception from an HTTP response.
        /// </summary>
        public static ConvaiRestException FromResponse(ConvaiHttpResponse response, string? contextMessage = null)
        {
            var category = response.IsTimeout ? ConvaiRestErrorCategory.Timeout :
                CategorizeStatusCode(response.StatusCode, response.HasTransportError);
            var message = BuildMessage(response, contextMessage);
            ConvaiApiException metadata = response.HasTransportError
                ? new ConvaiApiException(message,
                    response.IsTimeout ? ConvaiApiErrorCategory.Timeout : ConvaiApiErrorCategory.Transport,
                    responseBody: response.TransportError,
                    requestUri: response.Url)
                : ConvaiApiException.FromResponse(new ConvaiApiResponse(response.Url, response.StatusCodeInt,
                    ConvaiApiRequest.Utf8(response.Body), null, response.Headers));

            return new ConvaiRestException(
                message,
                category,
                response.StatusCode,
                response.Url,
                response.Body,
                metadata);
        }

        /// <summary>
        /// Creates an exception for a parse/deserialization error.
        /// </summary>
        public static ConvaiRestException ParseError(string message, Uri? url = null, string? responseBody = null, Exception? innerException = null)
        {
            return new ConvaiRestException(
                message,
                ConvaiRestErrorCategory.ParseError,
                0,
                url,
                responseBody,
                innerException);
        }

        /// <summary>
        /// Creates an exception for a cancellation.
        /// </summary>
        public static ConvaiRestException Cancelled(Uri? url = null)
        {
            return new ConvaiRestException(
                "Request was cancelled",
                ConvaiRestErrorCategory.Cancelled,
                0,
                url);
        }

        internal static ConvaiRestException FromApiException(ConvaiApiException exception, Uri? uri = null) =>
            new(
                exception.Message,
                exception.Category switch
                {
                    ConvaiApiErrorCategory.BadRequest or ConvaiApiErrorCategory.Contract =>
                        ConvaiRestErrorCategory.BadRequest,
                    ConvaiApiErrorCategory.Authentication or ConvaiApiErrorCategory.Authorization =>
                        ConvaiRestErrorCategory.Authentication,
                    ConvaiApiErrorCategory.Conflict => ConvaiRestErrorCategory.Conflict,
                    ConvaiApiErrorCategory.Timeout => ConvaiRestErrorCategory.Timeout,
                    ConvaiApiErrorCategory.NotFound => ConvaiRestErrorCategory.NotFound,
                    ConvaiApiErrorCategory.RateLimited => ConvaiRestErrorCategory.RateLimited,
                    ConvaiApiErrorCategory.Server => ConvaiRestErrorCategory.ServerError,
                    ConvaiApiErrorCategory.Serialization => ConvaiRestErrorCategory.ParseError,
                    _ => ConvaiRestErrorCategory.Transport
                },
                exception.StatusCode.HasValue ? (System.Net.HttpStatusCode)exception.StatusCode.Value : 0,
                exception.RequestUri ?? uri,
                responseBody: exception.ResponseBody,
                innerException: exception);

        private static ConvaiRestErrorCategory CategorizeStatusCode(HttpStatusCode statusCode, bool hasTransportError)
        {
            if (hasTransportError)
                return ConvaiRestErrorCategory.Transport;

            return (int)statusCode switch
            {
                400 or 422 => ConvaiRestErrorCategory.BadRequest,
                401 or 403 => ConvaiRestErrorCategory.Authentication,
                409 => ConvaiRestErrorCategory.Conflict,
                404 => ConvaiRestErrorCategory.NotFound,
                429 => ConvaiRestErrorCategory.RateLimited,
                >= 500 and < 600 => ConvaiRestErrorCategory.ServerError,
                _ => ConvaiRestErrorCategory.Unknown
            };
        }

        private static string BuildMessage(ConvaiHttpResponse response, string? contextMessage)
        {
            var prefix = contextMessage != null ? $"{contextMessage}: " : "";

            if (response.HasTransportError)
            {
                return $"{prefix}Transport error - {response.TransportError}";
            }

            var statusInfo = $"HTTP {response.StatusCodeInt} ({response.ReasonPhrase})";
            var truncatedBody = response.GetTruncatedBody(200);
            var bodyInfo = !string.IsNullOrEmpty(truncatedBody) ? $" - {truncatedBody}" : "";

            return $"{prefix}{statusInfo}{bodyInfo}";
        }

        /// <summary>
        /// Returns a user-friendly error message suitable for display.
        /// </summary>
        public string GetUserFriendlyMessage()
        {
            return Category switch
            {
                ConvaiRestErrorCategory.Transport => "Unable to connect to Convai servers. Please check your internet connection.",
                ConvaiRestErrorCategory.Authentication => "Authentication failed. Please verify your API key.",
                ConvaiRestErrorCategory.NotFound => "The requested resource was not found.",
                ConvaiRestErrorCategory.BadRequest => "Invalid request. Please check your input parameters.",
                ConvaiRestErrorCategory.RateLimited => "Too many requests. Please wait a moment and try again.",
                ConvaiRestErrorCategory.ServerError => "Convai servers are temporarily unavailable. Please try again later.",
                ConvaiRestErrorCategory.ParseError => "Failed to process server response.",
                ConvaiRestErrorCategory.Conflict => "The resource changed or conflicts with this request. Refresh before retrying.",
                ConvaiRestErrorCategory.Timeout => "The request timed out. Please try again.",
                ConvaiRestErrorCategory.Cancelled => "Request was cancelled.",
                _ => "An unexpected error occurred."
            };
        }
    }
}
