using System;
using Convai.Domain.Errors;
using Convai.Runtime.Core.Async;
using LiveKit;

namespace Convai.Infrastructure.Networking.WebGL
{
    /// <summary>
    ///     Turns a failed browser microphone capture into a <see cref="ConvaiOperationException" /> whose
    ///     <see cref="ConvaiOperationException.Code" /> is a <see cref="SessionErrorCodes" /> audio code, so a
    ///     denial reaches the app as a session error instead of an unhandled browser fault.
    /// </summary>
    internal static class WebGLMicrophoneFailure
    {
        /// <summary>
        ///     Describes why <paramref name="capture" /> was rejected, reading the rejection reason the
        ///     browser supplied and the page's Permissions Policy.
        /// </summary>
        internal static ConvaiOperationException FromRejectedCapture(JSPromise capture)
        {
            string errorName = null;
            string errorMessage = null;
            try
            {
                JSError error = capture?.RejectError;
                errorName = error?.Name;
                errorMessage = error?.Message;
            }
            catch (Exception)
            {
                // The reason is diagnostic only; an unreadable one still classifies as a publish failure.
            }

            bool? allowedByPolicy = null;
            if (IsPermissionDenial(errorName))
            {
                try
                {
                    allowedByPolicy = PermissionsPolicy.AllowsFeature("microphone");
                }
                catch (Exception)
                {
                    // Unknown policy only changes the wording of the message.
                }
            }

            return Classify(errorName, errorMessage, allowedByPolicy);
        }

        /// <summary>
        ///     Maps a <c>getUserMedia</c> rejection (its <c>DOMException</c> name and message) to a session
        ///     error code and a message that says what to do next.
        /// </summary>
        /// <param name="errorName">The rejection's <c>name</c>, for example <c>NotAllowedError</c>; may be null.</param>
        /// <param name="errorMessage">The rejection's <c>message</c>; may be null.</param>
        /// <param name="allowedByPermissionsPolicy">
        ///     Whether the page's Permissions Policy allows the microphone, or null when unknown.
        /// </param>
        internal static ConvaiOperationException Classify(string errorName, string errorMessage,
            bool? allowedByPermissionsPolicy)
        {
            string detail = Describe(errorName, errorMessage);

            if (IsPermissionDenial(errorName))
            {
                string message = allowedByPermissionsPolicy == false
                    ? "Microphone access is blocked by the page's permissions policy, so the browser " +
                      "refused it without asking. When the build is embedded in an iframe, the host page " +
                      "must grant it with allow=\"microphone\"; hosts that do not grant it cannot provide " +
                      $"voice input, and text input still works. ({detail})"
                    : "Microphone access was denied. Allow microphone access for this site in the " +
                      $"browser's site settings, then start listening again. ({detail})";
                return new ConvaiOperationException(SessionErrorCodes.AudioMicPermissionDenied, message);
            }

            if (IsMissingDevice(errorName))
            {
                return new ConvaiOperationException(SessionErrorCodes.AudioMicUnavailable,
                    $"No usable microphone was found. Connect a microphone, then start listening again. ({detail})");
            }

            return new ConvaiOperationException(SessionErrorCodes.AudioMicPublishFailed,
                $"The browser could not start the microphone. ({detail})");
        }

        private static bool IsPermissionDenial(string errorName) =>
            errorName is "NotAllowedError" or "SecurityError" or "PermissionDeniedError";

        private static bool IsMissingDevice(string errorName) =>
            errorName is "NotFoundError" or "OverconstrainedError" or "DevicesNotFoundError";

        private static string Describe(string errorName, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorName) && string.IsNullOrWhiteSpace(errorMessage))
                return "the browser gave no reason";
            if (string.IsNullOrWhiteSpace(errorMessage))
                return errorName;
            if (string.IsNullOrWhiteSpace(errorName))
                return errorMessage;
            return $"{errorName}: {errorMessage}";
        }
    }
}
