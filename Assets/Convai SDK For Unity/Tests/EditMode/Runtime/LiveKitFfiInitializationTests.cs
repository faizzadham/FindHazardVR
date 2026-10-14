using System;
using System.Linq;
using System.Reflection;
using LiveKit.Internal.FFI;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class LiveKitFfiInitializationTests
    {
        [Test]
        public void InitializeSdk_HandlesMissingNativeLibraryInsideEditorBoundary()
        {
            MethodInfo initializeSdk = typeof(FfiClient).GetMethod(
                "InitializeSdk",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(initializeSdk, Is.Not.Null);

            bool catchesMissingNativeLibrary = initializeSdk.GetMethodBody()
                ?.ExceptionHandlingClauses
                .Any(clause =>
                    clause.Flags == ExceptionHandlingClauseOptions.Clause &&
                    clause.CatchType == typeof(DllNotFoundException)) == true;

            Assert.That(
                catchesMissingNativeLibrary,
                Is.True,
                "A fresh checkout downloads livekit_ffi asynchronously. Editor initialization must " +
                "absorb DllNotFoundException and retry instead of breaking Play Mode.");

            MethodInfo retry = typeof(FfiClient).GetMethod(
                "RetryEditorInitializeSdk",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(retry, Is.Not.Null, "Missing-native recovery must schedule a bounded retry.");

            FieldInfo maxRetries = typeof(FfiClient).GetField(
                "EditorInitMaxRetries",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(maxRetries, Is.Not.Null, "Missing-native recovery must have an explicit retry limit.");
            Assert.That(maxRetries.GetRawConstantValue(), Is.TypeOf<int>().And.GreaterThan(0));

            FieldInfo warning = typeof(FfiClient).GetField(
                "EditorMissingFfiWarning",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(warning, Is.Not.Null);
            Assert.That(
                warning.GetRawConstantValue(),
                Does.Contain("Convai > Platform Support > Download FFI Libraries (Current Platform)"),
                "Giving up must direct users to the manual FFI recovery action.");
        }

        [Test]
        public void EditorRetryPolicy_SchedulesUntilLimitThenWarnsOnce()
        {
            FieldInfo maxRetries = typeof(FfiClient).GetField(
                "EditorInitMaxRetries",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(maxRetries, Is.Not.Null);
            int limit = (int)maxRetries.GetRawConstantValue();

            Assert.That(
                FfiClient.GetEditorFfiRetryAction(limit - 1, false, false),
                Is.EqualTo(FfiClient.EditorFfiRetryAction.ScheduleRetry));
            Assert.That(
                FfiClient.GetEditorFfiRetryAction(limit - 1, true, false),
                Is.EqualTo(FfiClient.EditorFfiRetryAction.None),
                "Only one delayed retry may be queued at a time.");
            Assert.That(
                FfiClient.GetEditorFfiRetryAction(limit, false, false),
                Is.EqualTo(FfiClient.EditorFfiRetryAction.LogWarning));
            Assert.That(
                FfiClient.GetEditorFfiRetryAction(limit, false, true),
                Is.EqualTo(FfiClient.EditorFfiRetryAction.None),
                "The recovery warning must only be emitted once.");
        }
    }
}
