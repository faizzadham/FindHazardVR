using System;
using System.Text;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.Logging;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Convai.Infrastructure.Networking
{
    internal enum RoomInitializationFailureCategory
    {
        RoomDetailsRequestFailed,
        InvalidRoomDetails
    }

    public readonly struct ConnectionFailure
    {
        private ConnectionFailure(
            string code,
            string message,
            SessionErrorStage stage,
            bool isRecoverable = false,
            int? httpStatusCode = null,
            string rawResponseBody = null,
            Exception exception = null)
        {
            Code = string.IsNullOrWhiteSpace(code) ? SessionErrorCodes.ConnectionFailed : code;
            Message = string.IsNullOrWhiteSpace(message) ? "Connection failed." : message;
            Stage = stage;
            IsRecoverable = isRecoverable;
            HttpStatusCode = httpStatusCode;
            RawResponseBody = rawResponseBody;
            Exception = exception;
        }

        public string Code { get; }
        public string Message { get; }
        public SessionErrorStage Stage { get; }
        public bool IsRecoverable { get; }
        public int? HttpStatusCode { get; }
        public string RawResponseBody { get; }
        public Exception Exception { get; }

        public SessionError ToSessionError(string sessionId = null) =>
            SessionError.Create(Code, Message, sessionId, IsRecoverable, Exception, Stage, HttpStatusCode);

        public ConvaiOperationException ToException() => new(Code, Message, Exception);

        public static ConnectionFailure Create(
            string code,
            string message,
            SessionErrorStage stage,
            bool isRecoverable = false,
            int? httpStatusCode = null,
            string rawResponseBody = null,
            Exception exception = null) =>
            new(code, message, stage, isRecoverable, httpStatusCode, rawResponseBody, exception);

        public static ConnectionFailure FromException(
            Exception exception,
            string code,
            string message,
            SessionErrorStage stage,
            bool isRecoverable = false,
            int? httpStatusCode = null,
            string rawResponseBody = null) =>
            new(code, message, stage, isRecoverable, httpStatusCode, rawResponseBody, exception);
    }

    public readonly struct RoomConnectionAttemptResult
    {
        private RoomConnectionAttemptResult(bool succeeded, ConnectionFailure failure)
        {
            Succeeded = succeeded;
            Failure = failure;
        }

        public bool Succeeded { get; }
        public ConnectionFailure Failure { get; }

        public static RoomConnectionAttemptResult Success() => new(true, default);
        public static RoomConnectionAttemptResult Fail(ConnectionFailure failure) => new(false, failure);
    }

    internal sealed class RoomInitializationFetchException : Exception
    {
        internal RoomInitializationFetchException(
            string failureMessage,
            string sessionErrorMessage = null,
            long? responseCode = null,
            string responseBody = null,
            Exception innerException = null)
            : base(failureMessage, innerException)
        {
            SessionErrorMessage = string.IsNullOrWhiteSpace(sessionErrorMessage)
                ? failureMessage
                : sessionErrorMessage;
            ResponseCode = responseCode;
            ResponseBody = responseBody;
        }

        internal string SessionErrorMessage { get; }
        internal long? ResponseCode { get; }
        internal string ResponseBody { get; }
    }

    /// <summary>
    ///     Turns a failed room-details attempt into one Console line a reader can act on.
    /// </summary>
    /// <remarks>
    ///     The failure carries the HTTP status and the service's own explanation, and both used to
    ///     stay in a <c>Debug</c> diagnostics line that default verbosity hides. What reached the
    ///     Console was the exception's type name, which cannot distinguish a rejected API key from a
    ///     character that belongs to another account, from a service that was never reached at all —
    ///     three problems with three different fixes. The status and the service's reason are part of
    ///     the error now. The body is capped because a service error page can be arbitrarily long,
    ///     and a Console line that scrolls the rest of the failure away helps nobody.
    /// </remarks>
    internal static class RoomInitializationFailureLog
    {
        private const int MaxResponseBodyCharacters = 400;

        internal static string Describe(Exception error)
        {
            if (error == null)
                return "Room-details request failed for an unknown reason.";

            if (error is not RoomInitializationFetchException fetchFailure)
                return $"Room-details request failed: {error.Message} ({error.GetType().Name}).";

            var description = new StringBuilder("Room-details request failed");
            bool serviceAnswered = fetchFailure.ResponseCode.HasValue && fetchFailure.ResponseCode.Value > 0;
            if (serviceAnswered)
                description.Append(" with HTTP ").Append(fetchFailure.ResponseCode.Value);
            description.Append(": ").Append(fetchFailure.Message);

            string detail = Summarize(fetchFailure.ResponseBody);
            if (!string.IsNullOrEmpty(detail))
                // Without a status code nothing answered, so the text cannot be a service response —
                // it is the transport saying why it never got one.
                description.Append(serviceAnswered ? " Service response: " : " Details: ").Append(detail);

            return description.ToString();
        }

        private static string Summarize(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return string.Empty;

            string trimmed = responseBody.Trim();
            return trimmed.Length <= MaxResponseBodyCharacters
                ? trimmed
                : trimmed.Substring(0, MaxResponseBodyCharacters) + "…";
        }
    }

    internal readonly struct RoomInitializationFailureOutcome
    {
        internal RoomInitializationFailureOutcome(
            RoomInitializationFailureCategory category,
            in RoomInitializationOutcome recoveryOutcome,
            ConnectionFailure failure)
        {
            Category = category;
            RecoveryOutcome = recoveryOutcome;
            FailureMessage = recoveryOutcome.FailureMessage;
            Failure = failure;
            DiagnosticsMetadata = CreateDiagnosticsMetadata(
                category,
                recoveryOutcome,
                failure);
        }

        public RoomInitializationFailureCategory Category { get; }
        public RoomInitializationOutcome RecoveryOutcome { get; }
        public string FailureMessage { get; }
        public ConnectionFailure Failure { get; }
        public string SessionErrorCode => Failure.Code;
        public string SessionErrorMessage => Failure.Message;
        public bool ShouldPublishSessionError => true;
        public bool SessionErrorIsRecoverable => Failure.IsRecoverable;
        public long? ResponseCode => Failure.HttpStatusCode;
        public string DiagnosticsMetadata { get; }

        public string FormatDiagnosticsLogMessage(string prefix = null) => Prefix(
            prefix,
            $"Room init failure outcome: {DiagnosticsMetadata}");

        private static string CreateDiagnosticsMetadata(
            RoomInitializationFailureCategory category,
            in RoomInitializationOutcome recoveryOutcome,
            in ConnectionFailure failure)
        {
            string normalizedSessionErrorCode =
                string.IsNullOrWhiteSpace(failure.Code) ? "<none>" : failure.Code;
            string normalizedSessionErrorMessage = string.IsNullOrWhiteSpace(failure.Message)
                ? "<none>"
                : failure.Message;
            string normalizedResponseCode = failure.HttpStatusCode?.ToString() ?? "<none>";
            return
                $"category={category}; responseCode={normalizedResponseCode}; shouldPublishSessionError=True; sessionErrorCode={normalizedSessionErrorCode}; sessionErrorRecoverable={failure.IsRecoverable}; sessionErrorMessage={normalizedSessionErrorMessage}; recovery={{{recoveryOutcome.DiagnosticsMetadata}}}";
        }

        private static string Prefix(string prefix, string message) => string.IsNullOrWhiteSpace(prefix)
            ? message
            : $"{prefix} {message}";
    }

    internal static class RoomInitializationFailureSupport
    {
        internal static ConnectionFailure FromAuthTokenFetchFailure(
            string message,
            Exception exception = null)
        {
            const string fallbackMessage = "Failed to fetch an auth token before connecting.";
            string effectiveMessage = string.IsNullOrWhiteSpace(message) ? fallbackMessage : message;
            return ConnectionFailure.FromException(
                exception,
                SessionErrorCodes.ConnectionAuthTokenFetchFailed,
                effectiveMessage,
                SessionErrorStage.ConnectApi,
                SessionErrorCodes.IsRecoverable(SessionErrorCodes.ConnectionAuthTokenFetchFailed));
        }

        internal static ConnectionFailure ClassifyConnectApiFailure(
            long? httpStatusCode,
            string message,
            string rawResponseBody = null,
            Exception exception = null)
        {
            string extractedMessage = ExtractConnectApiMessage(rawResponseBody);
            string effectiveMessage = FirstNonEmpty(extractedMessage, message, "Connection failed.");
            string normalized = effectiveMessage.ToLowerInvariant();
            int? statusCode = httpStatusCode.HasValue ? (int)httpStatusCode.Value : null;

            if (statusCode == 422)
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectValidationError,
                    effectiveMessage,
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 400 && (normalized.Contains("invalid uuid") ||
                                      normalized.Contains("invalid session id") ||
                                      normalized.Contains("invalid session_id") ||
                                      normalized.Contains("invalid character_session_id") ||
                                      normalized.Contains("invalid uuid format")))
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectInvalidSessionId,
                    effectiveMessage,
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 401)
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectInvalidApiKey,
                    effectiveMessage,
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            // A scene that gains its second character silently changes the room the service is
            // asked for, so an account without multi-character access stops connecting at all —
            // including the character that worked a moment ago. The service names the reason in a
            // token nobody outside it can read, and the plain 403 branch below would blame realtime
            // access instead, which is the wrong thing to go and check.
            if (statusCode == 403 && normalized.Contains("multi_character_v0_disabled"))
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectMultiCharacterNotAllowed,
                    "This room holds more than one character, and multi-character access is not " +
                    "enabled for the Convai account this API key belongs to. Ask Convai to enable " +
                    "it, or send one character at a time using Convai Manager > Characters Joining the Room.",
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 403 && normalized.Contains("multi_character_roster_limit_exceeded"))
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectMultiCharacterRosterLimit,
                    "This room holds more characters than the Convai plan for this API key allows. " +
                    "Send fewer of them using Convai Manager > Characters Joining the Room, or ask Convai " +
                    "to raise the limit.",
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 403)
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectRealtimeNotAllowed,
                    effectiveMessage,
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 404)
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectCharacterNotFound,
                    effectiveMessage,
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 429 && normalized.Contains("speaker"))
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectSpeakerLimitReached,
                    effectiveMessage,
                    false,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 429)
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectConcurrencyLimitReached,
                    effectiveMessage,
                    true,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 500 && normalized.Contains("failed to start bot"))
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectBotStartFailed,
                    effectiveMessage,
                    true,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            if (statusCode == 500)
            {
                return CreateConnectApiFailure(
                    SessionErrorCodes.ConnectionConnectUnhandledServerException,
                    effectiveMessage,
                    true,
                    statusCode,
                    rawResponseBody,
                    exception);
            }

            string fallbackCode = MapFallbackCode(statusCode);

            if (!string.IsNullOrWhiteSpace(rawResponseBody))
            {
                ConvaiLogger.Warning(
                    $"[ConnectAPI] Unclassified response ({statusCode}). Falling back to '{fallbackCode}'. " +
                    $"Raw response: {rawResponseBody}",
                    LogCategory.Bootstrap);
            }

            return CreateConnectApiFailure(
                fallbackCode,
                effectiveMessage,
                SessionErrorCodes.IsRecoverable(fallbackCode),
                statusCode,
                rawResponseBody,
                exception);
        }

        internal static RoomInitializationFailureOutcome FromRequestFailure(
            string attemptedCharacterSessionId,
            bool enableSessionResume,
            string failureMessage,
            InvalidStoredSessionRecoveryPolicy recoveryPolicy,
            long? responseCode = null,
            string sessionErrorMessage = null,
            string responseBody = null)
        {
            string recoveryFailureMessage = FirstNonEmpty(
                ExtractConnectApiMessage(responseBody),
                failureMessage);
            RoomInitializationOutcome recoveryOutcome = RoomInitializationRecoverySupport.FromFailure(
                attemptedCharacterSessionId,
                enableSessionResume,
                recoveryFailureMessage,
                recoveryPolicy);
            ConnectionFailure failure = ClassifyConnectApiFailure(
                responseCode,
                string.IsNullOrWhiteSpace(sessionErrorMessage) ? failureMessage : sessionErrorMessage,
                responseBody);
            return new RoomInitializationFailureOutcome(
                RoomInitializationFailureCategory.RoomDetailsRequestFailed,
                recoveryOutcome,
                failure);
        }

        internal static RoomInitializationFailureOutcome FromRequestException(
            string attemptedCharacterSessionId,
            bool enableSessionResume,
            Exception exception,
            InvalidStoredSessionRecoveryPolicy recoveryPolicy)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));

            if (exception is RoomInitializationFetchException fetchException)
            {
                return FromRequestFailure(
                    attemptedCharacterSessionId,
                    enableSessionResume,
                    fetchException.Message,
                    recoveryPolicy,
                    fetchException.ResponseCode,
                    fetchException.SessionErrorMessage,
                    fetchException.ResponseBody);
            }

            return FromRequestFailure(
                attemptedCharacterSessionId,
                enableSessionResume,
                exception.Message,
                recoveryPolicy);
        }

        internal static RoomInitializationFailureOutcome FromInvalidRoomDetails(
            string attemptedCharacterSessionId,
            bool enableSessionResume,
            string failureMessage,
            InvalidStoredSessionRecoveryPolicy recoveryPolicy)
        {
            RoomInitializationOutcome recoveryOutcome = RoomInitializationRecoverySupport.FromFailure(
                attemptedCharacterSessionId,
                enableSessionResume,
                failureMessage,
                recoveryPolicy);
            var failure = ConnectionFailure.Create(
                SessionErrorCodes.ConnectionFailed,
                failureMessage,
                SessionErrorStage.ConnectApi);
            return new RoomInitializationFailureOutcome(
                RoomInitializationFailureCategory.InvalidRoomDetails,
                recoveryOutcome,
                failure);
        }

        private static ConnectionFailure CreateConnectApiFailure(
            string code,
            string message,
            bool isRecoverable,
            int? httpStatusCode,
            string rawResponseBody,
            Exception exception) =>
            ConnectionFailure.Create(
                code,
                message,
                SessionErrorStage.ConnectApi,
                isRecoverable,
                httpStatusCode,
                rawResponseBody,
                exception);

        private static string MapFallbackCode(int? responseCode)
        {
            if (!responseCode.HasValue) return SessionErrorCodes.ConnectionFailed;

            return responseCode.Value switch
            {
                0 => SessionErrorCodes.ConnectionNetworkError,
                401 => SessionErrorCodes.ConnectionInvalidToken,
                403 => SessionErrorCodes.ConnectionAuthFailed,
                404 => SessionErrorCodes.ConnectionNotFound,
                429 => SessionErrorCodes.ConnectionRateLimited,
                503 => SessionErrorCodes.ConnectionServiceUnavailable,
                >= 500 and < 600 => SessionErrorCodes.ConnectionServerError,
                >= 400 and < 500 => SessionErrorCodes.ConnectionBadRequest,
                _ => SessionErrorCodes.ConnectionFailed
            };
        }

        private static string ExtractConnectApiMessage(string rawResponseBody)
        {
            if (string.IsNullOrWhiteSpace(rawResponseBody))
                return null;

            try
            {
                JToken root = JToken.Parse(rawResponseBody);
                return FirstNonEmpty(
                    root["detail"]?.Type == JTokenType.Array
                        ? root["detail"]?.ToString(Formatting.None)
                        : root["detail"]?.ToString(),
                    root["message"]?.ToString(),
                    root["error"]?.ToString(),
                    rawResponseBody);
            }
            catch
            {
                return rawResponseBody;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }
    }
}
