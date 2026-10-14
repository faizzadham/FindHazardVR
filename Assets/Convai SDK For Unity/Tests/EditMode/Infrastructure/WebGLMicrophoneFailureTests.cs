using Convai.Domain.Errors;
using Convai.Infrastructure.Networking.WebGL;
using Convai.Runtime.Core.Async;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class WebGLMicrophoneFailureTests
    {
        [TestCase("NotAllowedError")]
        [TestCase("SecurityError")]
        [TestCase("PermissionDeniedError")]
        public void Classify_PermissionDenial_IsPermissionDenied(string errorName)
        {
            ConvaiOperationException failure =
                WebGLMicrophoneFailure.Classify(errorName, "Permission denied", allowedByPermissionsPolicy: null);

            Assert.That(failure.Code, Is.EqualTo(SessionErrorCodes.AudioMicPermissionDenied));
            Assert.That(failure.Message, Does.Contain("site settings"));
            Assert.That(failure.Message, Does.Contain(errorName));
        }

        [Test]
        public void Classify_DenialUnderBlockingPermissionsPolicy_NamesTheIframeAllowAttribute()
        {
            ConvaiOperationException failure = WebGLMicrophoneFailure.Classify("NotAllowedError",
                "Permission denied", allowedByPermissionsPolicy: false);

            Assert.That(failure.Code, Is.EqualTo(SessionErrorCodes.AudioMicPermissionDenied));
            Assert.That(failure.Message, Does.Contain("allow=\"microphone\""));
        }

        [TestCase("NotFoundError")]
        [TestCase("OverconstrainedError")]
        [TestCase("DevicesNotFoundError")]
        public void Classify_MissingDevice_IsUnavailable(string errorName)
        {
            ConvaiOperationException failure =
                WebGLMicrophoneFailure.Classify(errorName, "Requested device not found", null);

            Assert.That(failure.Code, Is.EqualTo(SessionErrorCodes.AudioMicUnavailable));
        }

        [TestCase("NotReadableError", "Could not start audio source")]
        [TestCase("AbortError", null)]
        [TestCase(null, null)]
        public void Classify_OtherFailure_IsPublishFailedAndKeepsTheBrowserReason(string errorName, string message)
        {
            ConvaiOperationException failure = WebGLMicrophoneFailure.Classify(errorName, message, null);

            Assert.That(failure.Code, Is.EqualTo(SessionErrorCodes.AudioMicPublishFailed));
            Assert.That(failure.Message, Does.Contain(errorName ?? "the browser gave no reason"));
        }
    }
}
