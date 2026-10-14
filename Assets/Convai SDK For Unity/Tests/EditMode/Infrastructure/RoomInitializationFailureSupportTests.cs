using System;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Infrastructure.Networking;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RoomInitializationFailureSupportTests
    {
        [Test]
        public void FromRequestFailure_WithHttp400InvalidStoredSession_PublishesBadRequestWithoutRetry()
        {
            RoomInitializationFailureOutcome outcome = RoomInitializationFailureSupport.FromRequestFailure(
                "stored-session",
                enableSessionResume: true,
                failureMessage: "HTTP request failed: Bad Request (Code: 400). Response: Invalid character_session_id",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed,
                responseCode: 400,
                sessionErrorMessage: "HTTP request failed: Bad Request (Code: 400)");

            Assert.That(outcome.Category, Is.EqualTo(RoomInitializationFailureCategory.RoomDetailsRequestFailed));
            Assert.That(outcome.RecoveryOutcome.Status,
                Is.EqualTo(RoomInitializationOutcomeStatus.InvalidStoredSession));
            Assert.That(outcome.RecoveryOutcome.ShouldRetryWithoutStoredSession, Is.False);
            Assert.That(outcome.RecoveryOutcome.ShouldClearStoredSession, Is.False);
            Assert.That(outcome.ShouldPublishSessionError, Is.True);
            Assert.That(outcome.SessionErrorCode, Is.EqualTo(SessionErrorCodes.ConnectionBadRequest));
            Assert.That(outcome.SessionErrorMessage, Is.EqualTo("HTTP request failed: Bad Request (Code: 400)"));
            Assert.That(outcome.SessionErrorIsRecoverable, Is.False);
            Assert.That(outcome.DiagnosticsMetadata, Does.Contain("responseCode=400"));
        }

        [Test]
        public void FromRequestException_WithFetchException_PreservesDetailedFailureAndRecoverableHttpCode()
        {
            var exception = new RoomInitializationFetchException(
                "HTTP request failed: Service Unavailable (Code: 503). Response: temporary outage",
                "HTTP request failed: Service Unavailable (Code: 503)",
                responseCode: 503);

            RoomInitializationFailureOutcome outcome = RoomInitializationFailureSupport.FromRequestException(
                attemptedCharacterSessionId: null,
                enableSessionResume: false,
                exception,
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionAllowed);

            Assert.That(outcome.FailureMessage,
                Is.EqualTo("HTTP request failed: Service Unavailable (Code: 503). Response: temporary outage"));
            Assert.That(outcome.SessionErrorMessage,
                Is.EqualTo("HTTP request failed: Service Unavailable (Code: 503)"));
            Assert.That(outcome.SessionErrorCode, Is.EqualTo(SessionErrorCodes.ConnectionServiceUnavailable));
            Assert.That(outcome.SessionErrorIsRecoverable, Is.True);
        }

        [Test]
        public void FromRequestException_WithGenericException_MapsConnectionFailed()
        {
            RoomInitializationFailureOutcome outcome = RoomInitializationFailureSupport.FromRequestException(
                attemptedCharacterSessionId: null,
                enableSessionResume: false,
                new Exception("boom"),
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionAllowed);

            Assert.That(outcome.Category, Is.EqualTo(RoomInitializationFailureCategory.RoomDetailsRequestFailed));
            Assert.That(outcome.SessionErrorCode, Is.EqualTo(SessionErrorCodes.ConnectionFailed));
            Assert.That(outcome.SessionErrorMessage, Is.EqualTo("boom"));
            Assert.That(outcome.ShouldPublishSessionError, Is.True);
            Assert.That(outcome.RecoveryOutcome.Status, Is.EqualTo(RoomInitializationOutcomeStatus.Failed));
        }

        [Test]
        public void FromAuthTokenFetchFailure_UsesExpectedCodeStageAndRecoverability()
        {
            var exception = new InvalidOperationException("token endpoint unavailable");

            ConnectionFailure failure = RoomInitializationFailureSupport.FromAuthTokenFetchFailure(
                "Unable to resolve a fresh credential.",
                exception);

            Assert.That(failure.Code, Is.EqualTo(SessionErrorCodes.ConnectionAuthTokenFetchFailed));
            Assert.That(failure.Message, Is.EqualTo("Unable to resolve a fresh credential."));
            Assert.That(failure.Stage, Is.EqualTo(SessionErrorStage.ConnectApi));
            Assert.That(failure.IsRecoverable, Is.True);
            Assert.That(failure.Exception, Is.SameAs(exception));
        }

        [Test]
        public void FromAuthTokenFetchFailure_WithEmptyMessage_UsesSafeFallback()
        {
            ConnectionFailure failure = RoomInitializationFailureSupport.FromAuthTokenFetchFailure("   ");

            Assert.That(failure.Code, Is.EqualTo(SessionErrorCodes.ConnectionAuthTokenFetchFailed));
            Assert.That(failure.Message, Is.EqualTo("Failed to fetch an auth token before connecting."));
            Assert.That(failure.Stage, Is.EqualTo(SessionErrorStage.ConnectApi));
            Assert.That(failure.IsRecoverable, Is.True);
            Assert.That(failure.Exception, Is.Null);
        }

        [Test]
        public void FromInvalidRoomDetails_DoesNotProducePublishableSessionError()
        {
            RoomInitializationFailureOutcome outcome = RoomInitializationFailureSupport.FromInvalidRoomDetails(
                "stored-session",
                enableSessionResume: true,
                failureMessage: "Failed to get room details",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed);

            Assert.That(outcome.Category, Is.EqualTo(RoomInitializationFailureCategory.InvalidRoomDetails));
            Assert.That(outcome.RecoveryOutcome.Status, Is.EqualTo(RoomInitializationOutcomeStatus.Failed));
            Assert.That(outcome.ShouldPublishSessionError, Is.True);
            Assert.That(outcome.SessionErrorCode, Is.EqualTo(SessionErrorCodes.ConnectionFailed));
            Assert.That(outcome.SessionErrorMessage, Is.EqualTo("Failed to get room details"));
        }
        [Test]
        public void FromRequestFailure_WithMultiCharacterDisabled_NamesTheAccountFeatureRatherThanRealtime()
        {
            RoomInitializationFailureOutcome outcome = RoomInitializationFailureSupport.FromRequestFailure(
                attemptedCharacterSessionId: null,
                enableSessionResume: false,
                failureMessage: "HTTP request failed: Forbidden (Code: 403).",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed,
                responseCode: 403,
                sessionErrorMessage: null,
                responseBody: "{\"detail\":\"MULTI_CHARACTER_V0_DISABLED\"}");

            Assert.That(outcome.SessionErrorCode,
                Is.EqualTo(SessionErrorCodes.ConnectionConnectMultiCharacterNotAllowed));
            Assert.That(outcome.SessionErrorMessage, Does.Contain("more than one character"));
            Assert.That(outcome.SessionErrorMessage, Does.Contain("Characters Joining the Room"));
            Assert.That(outcome.SessionErrorMessage, Does.Not.Contain("MULTI_CHARACTER_V0_DISABLED"));
            Assert.That(outcome.SessionErrorIsRecoverable, Is.False);
        }

        [Test]
        public void FromRequestFailure_WithRosterLimitExceeded_NamesThePlanLimit()
        {
            RoomInitializationFailureOutcome outcome = RoomInitializationFailureSupport.FromRequestFailure(
                attemptedCharacterSessionId: null,
                enableSessionResume: false,
                failureMessage: "HTTP request failed: Forbidden (Code: 403).",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed,
                responseCode: 403,
                sessionErrorMessage: null,
                responseBody: "{\"detail\":\"MULTI_CHARACTER_ROSTER_LIMIT_EXCEEDED\"}");

            Assert.That(outcome.SessionErrorCode,
                Is.EqualTo(SessionErrorCodes.ConnectionConnectMultiCharacterRosterLimit));
            Assert.That(outcome.SessionErrorMessage, Does.Contain("more characters"));
            Assert.That(outcome.SessionErrorMessage, Does.Not.Contain("MULTI_CHARACTER_ROSTER_LIMIT_EXCEEDED"));
        }

        /// <summary>
        ///     Proves the two branches above read the service's reason rather than the status alone.
        ///     Without this, a 403 that means something else would be reported as a multi-character
        ///     problem and the new messages would be confidently wrong.
        /// </summary>
        [Test]
        public void FromRequestFailure_WithUnrelatedForbidden_StillReportsRealtimeNotAllowed()
        {
            RoomInitializationFailureOutcome outcome = RoomInitializationFailureSupport.FromRequestFailure(
                attemptedCharacterSessionId: null,
                enableSessionResume: false,
                failureMessage: "HTTP request failed: Forbidden (Code: 403).",
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed,
                responseCode: 403,
                sessionErrorMessage: null,
                responseBody: "{\"detail\":\"REALTIME_DISABLED\"}");

            Assert.That(outcome.SessionErrorCode,
                Is.EqualTo(SessionErrorCodes.ConnectionConnectRealtimeNotAllowed));
        }

    }
}
