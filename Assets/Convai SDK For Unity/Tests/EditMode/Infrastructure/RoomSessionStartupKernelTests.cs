using Convai.Domain.Errors;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.WebGL;
using Convai.Infrastructure.Networking.Models;
using Convai.RestAPI.Internal;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RoomSessionStartupKernelTests
    {
        [Test]
        public void Prepare_ExposesPreparedRequestAndModeLogMessage()
        {
            RoomSessionStartupPlan startupPlan = CreatePlan(joinOptions: new RoomJoinOptions("room-a", spawnAgent: true));

            Assert.That(startupPlan.Request.CharacterSessionId, Is.EqualTo("stored-session"));
            Assert.That(startupPlan.FormatModeLogMessage(),
                Is.EqualTo("Room join mode: room=room-a, spawnAgent=True"));
        }

        [Test]
        public void AcceptRoomDetails_WithResolvedSession_ProducesAcceptedDecisionAndPersistenceOutcome()
        {
            RoomSessionStartupPlan startupPlan = CreatePlan();
            var roomDetails = new RoomDetails(
                "token-a",
                "room-a",
                "session-a",
                "wss://room-a",
                "fresh-session",
                "speaker-a");

            RoomSessionStartupDecision decision = RoomSessionStartupKernel.AcceptRoomDetails(startupPlan, roomDetails);

            Assert.That(decision.Kind, Is.EqualTo(RoomSessionStartupDecisionKind.Accepted));
            Assert.That(decision.ShouldApplyRoomDetails, Is.True);
            Assert.That(decision.ShouldProceedToTransportConnect, Is.True);
            Assert.That(decision.AppliedRoomDetailsState.CharacterSessionID, Is.EqualTo("fresh-session"));
            Assert.That(decision.InitializationOutcome.Status,
                Is.EqualTo(RoomInitializationOutcomeStatus.RoomDetailsAccepted));
            Assert.That(decision.InitializationOutcome.AttemptedWithStoredSession, Is.True);
            Assert.That(decision.InitializationOutcome.ShouldPersistStoredSession, Is.True);
            Assert.That(decision.DiagnosticsMetadata, Does.Contain("kind=Accepted"));
            Assert.That(decision.DiagnosticsMetadata, Does.Contain("roomName=room-a"));
        }

        [Test]
        public void AcceptRoomDetails_AfterStoredSessionCleared_UsesMutatedRequestStateForRetryOutcome()
        {
            RoomSessionStartupPlan startupPlan = CreatePlan();
            startupPlan.Request.CharacterSessionId = null;

            RoomSessionStartupDecision decision = RoomSessionStartupKernel.AcceptRoomDetails(startupPlan,
                new RoomDetails("token-a", "room-a", "session-a", "wss://room-a", "fresh-session", "speaker-a"));

            Assert.That(decision.InitializationOutcome.AttemptedWithStoredSession, Is.False);
            Assert.That(decision.InitializationOutcome.ShouldPersistStoredSession, Is.True);
        }

        [Test]
        public void FromRequestFailure_WithInvalidStoredSessionAndRetryAllowed_ProducesRetryDecision()
        {
            RoomSessionStartupPlan startupPlan = CreatePlan();

            RoomSessionStartupDecision decision = RoomSessionStartupKernel.FromRequestFailure(
                startupPlan,
                "HTTP request failed: Bad Request (Code: 400). Response: Invalid character_session_id");

            Assert.That(decision.Kind, Is.EqualTo(RoomSessionStartupDecisionKind.Failed));
            Assert.That(decision.ShouldApplyRoomDetails, Is.False);
            Assert.That(decision.ShouldProceedToTransportConnect, Is.False);
            Assert.That(decision.InitializationOutcome.Status,
                Is.EqualTo(RoomInitializationOutcomeStatus.InvalidStoredSession));
            Assert.That(decision.InitializationOutcome.ShouldRetryWithoutStoredSession, Is.True);
            Assert.That(decision.FailureOutcome.Category,
                Is.EqualTo(RoomInitializationFailureCategory.RoomDetailsRequestFailed));
        }

        [Test]
        public void FromRequestException_WithWebGLPolicy_PreservesNoRetryNoClearAndSessionErrorMapping()
        {
            RoomSessionStartupPlan startupPlan = CreatePlan(
                recoveryPolicy: InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed,
                storedSessionFallbackPolicy: StoredSessionFallbackPolicy.WebGLCompatibility);
            var exception = new RoomInitializationFetchException(
                "HTTP request failed: Bad Request (Code: 400). Response: Invalid character_session_id",
                "HTTP request failed: Bad Request (Code: 400)",
                responseCode: 400);

            RoomSessionStartupDecision decision = RoomSessionStartupKernel.FromRequestException(startupPlan, exception);

            Assert.That(decision.InitializationOutcome.Status,
                Is.EqualTo(RoomInitializationOutcomeStatus.InvalidStoredSession));
            Assert.That(decision.InitializationOutcome.ShouldRetryWithoutStoredSession, Is.False);
            Assert.That(decision.InitializationOutcome.ShouldClearStoredSession, Is.False);
            Assert.That(decision.FailureOutcome.ShouldPublishSessionError, Is.True);
            Assert.That(decision.FailureOutcome.SessionErrorCode, Is.EqualTo(SessionErrorCodes.ConnectionBadRequest));
            Assert.That(decision.FailureOutcome.SessionErrorMessage,
                Is.EqualTo("HTTP request failed: Bad Request (Code: 400)"));
        }

        [Test]
        public void WebGLRequestFailure_PreservesBodyForStoredSessionDetection()
        {
            RoomSessionStartupPlan startupPlan = CreatePlan();
            RoomInitializationFetchException exception =
                WebGLRoomController.CreateRoomDetailsRequestException(
                    "Bad Request",
                    400,
                    "{\"detail\":\"Invalid character_session_id\"}");

            RoomSessionStartupDecision decision = RoomSessionStartupKernel.FromRequestException(
                startupPlan,
                exception);

            Assert.That(exception.ResponseBody, Does.Contain("Invalid character_session_id"));
            Assert.That(decision.InitializationOutcome.Status,
                Is.EqualTo(RoomInitializationOutcomeStatus.InvalidStoredSession));
            Assert.That(decision.InitializationOutcome.ShouldRetryWithoutStoredSession, Is.True);
            Assert.That(decision.FailureOutcome.SessionErrorCode,
                Is.EqualTo(SessionErrorCodes.ConnectionConnectInvalidSessionId));
        }

        [Test]
        public void FromInvalidRoomDetails_ProducesNonPublishableFailureDecision()
        {
            RoomSessionStartupPlan startupPlan = CreatePlan(
                recoveryPolicy: InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionDisallowed,
                storedSessionFallbackPolicy: StoredSessionFallbackPolicy.WebGLCompatibility);

            RoomSessionStartupDecision decision = RoomSessionStartupKernel.FromInvalidRoomDetails(
                startupPlan,
                "Failed to get room details");

            Assert.That(decision.InitializationOutcome.Status, Is.EqualTo(RoomInitializationOutcomeStatus.Failed));
            Assert.That(decision.FailureOutcome.ShouldPublishSessionError, Is.True);
            Assert.That(decision.FailureOutcome.SessionErrorCode, Is.EqualTo(SessionErrorCodes.ConnectionFailed));
            Assert.That(decision.FailureOutcome.SessionErrorMessage, Is.EqualTo("Failed to get room details"));
        }

        private static RoomSessionStartupPlan CreatePlan(
            RoomJoinOptions joinOptions = null,
            InvalidStoredSessionRecoveryPolicy recoveryPolicy =
                InvalidStoredSessionRecoveryPolicy.RetryWithoutStoredSessionAllowed,
            StoredSessionFallbackPolicy storedSessionFallbackPolicy = StoredSessionFallbackPolicy.NativeCompatibility)
            => RoomSessionStartupKernel.Prepare(
                "char-123",
                "audio",
                "https://core.convai.com",
                "stored-session",
                enableSessionResume: true,
                joinOptions,
                "user-1",
                null,
                "camera-main",
                emotionConfig: null,
                LipSyncTransportOptions.Disabled,
                storedSessionFallbackPolicy,
                recoveryPolicy);
    }
}
