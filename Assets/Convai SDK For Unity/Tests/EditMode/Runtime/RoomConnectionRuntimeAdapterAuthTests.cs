using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Emotion;
using Convai.Domain.Errors;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Room;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class RoomConnectionRuntimeAdapterAuthTests
    {
        [Test]
        public async Task WaitForStartCompletionAsync_WhenCallerCancels_PropagatesCancellation()
        {
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                new TestCredentialProvider("api-key"),
                () => new TestCharacterAgent());
            using var cancellation = new CancellationTokenSource();

            Task<bool> wait = adapter.WaitForStartCompletionAsync(cancellation.Token);
            cancellation.Cancel();

            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(wait,
                "A cancelled caller must see the start wait cancelled.");
            Assert.That(wait.IsCanceled, Is.True);
        }

        [Test]
        public async Task WaitForConnectionResolutionAsync_WhenCallerCancels_PropagatesCancellation()
        {
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                new TestCredentialProvider("api-key"),
                () => new TestCharacterAgent());
            using var cancellation = new CancellationTokenSource();

            Task<bool> wait = adapter.WaitForConnectionResolutionAsync(cancellation.Token);
            cancellation.Cancel();

            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(wait,
                "A cancelled caller must see the connection wait cancelled.");
            Assert.That(wait.IsCanceled, Is.True);
        }

        /// <summary>
        ///     Resolving the connection wait must not resume its caller inside the lock that guards
        ///     the connection-state signal.
        /// </summary>
        /// <remarks>
        ///     Same rule as
        ///     <c>ConvaiCharacterLifecycleTests.CharacterReady_DoesNotResumeCallersInsideTheReadinessLock</c>:
        ///     a task source completed while a lock is held runs everything waiting on it inside that
        ///     lock, on the thread that signalled. Here the waiter is the connection coordinator and
        ///     the signal arrives from the session-state path, so the whole remainder of connecting
        ///     used to run inside <c>_connectionStateLock</c> — with every other caller of the wait
        ///     queued behind it.
        /// </remarks>
        [Test]
        public async Task ConnectionResolution_DoesNotResumeCallersInsideTheConnectionStateLock()
        {
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                new TestCredentialProvider("api-key"),
                () => new TestCharacterAgent());
            object connectionStateLock = typeof(RoomConnectionRuntimeAdapter)
                .GetField("_connectionStateLock", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(adapter);
            Assert.IsNotNull(connectionStateLock, "Expected the connection-state signal to be guarded by a lock.");

            Task<bool> wait = adapter.WaitForConnectionResolutionAsync(CancellationToken.None);
            bool lockHeldWhenCallerResumed = false;
            int awaitingThread = Thread.CurrentThread.ManagedThreadId;
            int resumedThread = 0;

            async Task ResumeLikeACallerAsync()
            {
                await wait;
                lockHeldWhenCallerResumed = Monitor.IsEntered(connectionStateLock);
                resumedThread = Thread.CurrentThread.ManagedThreadId;
            }

            Task caller = ResumeLikeACallerAsync();
            adapter.SignalConnectionStateWaiters(SessionState.Connected);

            await AsyncTestDeadline.WithinAsync(caller,
                "Reaching the Connected state must resolve the connection wait.");
            Assert.IsFalse(lockHeldWhenCallerResumed,
                "Code awaiting the connection resolution resumed while the adapter still held its " +
                "connection-state lock, so the SDK is running the rest of connecting inside its own " +
                "critical section.");
            Assert.AreEqual(awaitingThread, resumedThread,
                "Scheduling the resumption must not move the caller off the thread it awaited on.");
        }

        [Test]
        public async Task ConnectAsync_MissingActiveCharacter_DoesNotResolveAsyncCredential()
        {
            var sequence = new List<string>();
            var provider = new TestAsyncCredentialProvider((_, _) =>
            {
                sequence.Add("resolve");
                return Task.FromResult("fresh-token");
            });
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                provider,
                () =>
                {
                    sequence.Add("active-character");
                    return null;
                });

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Code, Is.EqualTo(SessionErrorCodes.ConnectionFailed));
            Assert.That(sequence, Is.EqualTo(new[] { "active-character" }));
            Assert.That(provider.ResolutionCount, Is.Zero,
                "A connection that cannot proceed must not mint a short-lived credential.");
            Assert.That(provider.GetApiKey(), Is.Null,
                "A credential from a failed connection attempt must not remain retained.");
        }

        [Test]
        public async Task ConnectAsync_WhenResolutionReturnsNoToken_FailsAfterActiveCharacterValidation()
        {
            bool activeCharacterRead = false;
            ConnectionFailure? recordedFailure = null;
            var provider = new TestAsyncCredentialProvider(
                (_, _) => Task.FromResult<string>(null),
                "The token endpoint response did not contain a token.");
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                provider,
                () =>
                {
                    activeCharacterRead = true;
                    return new TestCharacterAgent();
                },
                failure => recordedFailure = failure);

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Code, Is.EqualTo(SessionErrorCodes.ConnectionAuthTokenFetchFailed));
            Assert.That(result.Failure.Message,
                Is.EqualTo("The token endpoint response did not contain a token."));
            Assert.That(result.Failure.Stage, Is.EqualTo(SessionErrorStage.ConnectApi));
            Assert.That(result.Failure.IsRecoverable, Is.True);
            Assert.That(activeCharacterRead, Is.True);
            Assert.That(recordedFailure.HasValue, Is.True);
            Assert.That(recordedFailure.Value.Code, Is.EqualTo(result.Failure.Code));
        }

        [Test]
        public async Task ConnectAsync_WhenResolutionThrows_RecordsFetchFailureAfterActiveCharacterValidation()
        {
            bool activeCharacterRead = false;
            var exception = new InvalidOperationException("backend unavailable");
            var provider = new TestAsyncCredentialProvider((_, _) => Task.FromException<string>(exception));
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                provider,
                () =>
                {
                    activeCharacterRead = true;
                    return new TestCharacterAgent();
                });

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Code, Is.EqualTo(SessionErrorCodes.ConnectionAuthTokenFetchFailed));
            Assert.That(result.Failure.Message,
                Is.EqualTo("Auth token provider failed (InvalidOperationException)."));
            Assert.That(result.Failure.Message, Does.Not.Contain("backend unavailable"));
            Assert.That(result.Failure.Exception, Is.SameAs(exception));
            Assert.That(activeCharacterRead, Is.True);
        }

        [Test]
        public async Task ConnectAsync_Twice_ResolvesFreshCredentialForEachAttempt()
        {
            var provider = new TestAsyncCredentialProvider(
                (attempt, _) => Task.FromResult($"token-{attempt}"));
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(provider, () => new TestCharacterAgent());

            await adapter.ConnectAsync(CancellationToken.None);
            await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(provider.ResolutionCount, Is.EqualTo(2));
            Assert.That(provider.GetApiKey(), Is.Null);
        }

        [Test]
        public async Task DisconnectAsync_ClearsResolvedAsyncCredential()
        {
            var provider = new TestAsyncCredentialProvider((_, _) => Task.FromResult("session-token"));
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(provider, () => null);
            await provider.EnsureCredentialsAsync(CancellationToken.None);
            Assert.That(provider.GetApiKey(), Is.EqualTo("session-token"));

            await adapter.DisconnectAsync(CancellationToken.None);

            Assert.That(provider.GetApiKey(), Is.Null);
        }

        [Test]
        public void ConnectAsync_WhenCallerCancelsResolution_RestoresDisconnectedWithoutRecordingFailure()
        {
            bool activeCharacterRead = false;
            bool failureRecorded = false;
            SessionState? finalState = null;
            var provider = new TestAsyncCredentialProvider(
                (_, ct) => Task.FromCanceled<string>(ct));
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                provider,
                () =>
                {
                    activeCharacterRead = true;
                    return new TestCharacterAgent();
                },
                _ => failureRecorded = true,
                updateSessionState: (state, _) => finalState = state);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await adapter.ConnectAsync(cancellation.Token));

            Assert.That(activeCharacterRead, Is.True);
            Assert.That(failureRecorded, Is.False);
            Assert.That(finalState, Is.EqualTo(SessionState.Disconnected));
            Assert.That(provider.GetApiKey(), Is.Null);
        }

        [Test]
        public void ConnectAsync_WhenFaultRacesCallerCancellation_PrefersCancellationAndRestoresDisconnected()
        {
            using var cancellation = new CancellationTokenSource();
            bool failureRecorded = false;
            SessionState? finalState = null;
            var provider = new TestAsyncCredentialProvider((_, _) =>
            {
                cancellation.Cancel();
                return Task.FromException<string>(
                    new InvalidOperationException("provider fault after cancellation"));
            });
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                provider,
                () => new TestCharacterAgent(),
                _ => failureRecorded = true,
                updateSessionState: (state, _) => finalState = state);

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await adapter.ConnectAsync(cancellation.Token));

            Assert.That(failureRecorded, Is.False);
            Assert.That(finalState, Is.EqualTo(SessionState.Disconnected));
            Assert.That(provider.GetApiKey(), Is.Null);
        }

        [Test]
        public async Task ConnectAsync_LegacySynchronousCredentialProvider_StillUsesExistingPath()
        {
            bool activeCharacterRead = false;
            var provider = new TestCredentialProvider("api-key");
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                provider,
                () =>
                {
                    activeCharacterRead = true;
                    return new TestCharacterAgent();
                });

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Code, Is.EqualTo(SessionErrorCodes.ConnectionFailed));
            Assert.That(activeCharacterRead, Is.True,
                "A legacy ICredentialProvider must not be rejected for lacking IAsyncCredentialProvider.");
        }

        [Test]
        public async Task ConnectAsync_ExplicitToken_IsAppliedBeforeCredentialResolution()
        {
            var options = new RoomSessionConnectOptions();
            options.SetExplicitAuthToken("  explicit-token  ");
            var provider = new TestExplicitAuthTokenCredentialProvider();
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                provider,
                () => new TestCharacterAgent(),
                consumePendingConnectOptions: () => options);

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Code, Is.EqualTo(SessionErrorCodes.ConnectionFailed));
            Assert.That(provider.ReceivedAuthToken, Is.EqualTo("explicit-token"));
            Assert.That(provider.ResolutionCount, Is.EqualTo(1));
            Assert.That(options.ConsumeExplicitAuthToken(), Is.Null,
                "The per-call token must be removed from connect options as soon as it is handed off.");
        }

        [Test]
        public async Task ConnectAsync_ExplicitTokenInApiKeyMode_ReturnsConfigurationFailure()
        {
            bool activeCharacterRead = false;
            var options = new RoomSessionConnectOptions();
            options.SetExplicitAuthToken("explicit-token");
            RoomConnectionRuntimeAdapter adapter = CreateAdapter(
                new TestCredentialProvider("api-key"),
                () =>
                {
                    activeCharacterRead = true;
                    return new TestCharacterAgent();
                },
                consumePendingConnectOptions: () => options);

            RoomConnectionAttemptResult result = await adapter.ConnectAsync(CancellationToken.None);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Code, Is.EqualTo(SessionErrorCodes.ConfigAuthTokenModeRequired));
            Assert.That(result.Failure.Message, Does.Contain("require Auth Token mode"));
            Assert.That(result.Failure.Stage, Is.EqualTo(SessionErrorStage.Configuration));
            Assert.That(activeCharacterRead, Is.True);
        }

        private static RoomConnectionRuntimeAdapter CreateAdapter(
            ICredentialProvider credentialProvider,
            Func<IConvaiCharacterAgent> activeCharacterProvider,
            Action<ConnectionFailure> recordConnectionFailure = null,
            Func<RoomSessionConnectOptions> consumePendingConnectOptions = null,
            Action<SessionState, SessionError?> updateSessionState = null)
        {
            var disconnectAdapter = new RoomDisconnectRuntimeAdapter(
                () => null,
                () => null,
                updateSessionState ?? ((_, _) => { }),
                (_, _, _) => { },
                () => { });

            return new RoomConnectionRuntimeAdapter(
                () => SessionState.Disconnected,
                () => false,
                () => true,
                () => 1000,
                () => true,
                activeCharacterProvider,
                () => Array.Empty<IConvaiCharacterAgent>(),
                new AgentRegistry(),
                () => ConnectionContext.Empty,
                _ => { },
                () => ReconnectPolicy.Default,
                _ => { },
                _ => { },
                () => null,
                () => ConvaiConnectionType.Audio,
                () => "https://core.convai.com/connect",
                () => null,
                () => null,
                TurnTakingOptions.CreateHandsFreeDefault,
                UserVadSettings.CreateDefault,
                () => null,
                () => null,
                consumePendingConnectOptions ?? (() => null),
                (_, _) => { },
                _ => { },
                () => null,
                disconnectAdapter,
                updateSessionState ?? ((_, _) => { }),
                (_, _, _, _) => { },
                recordConnectionFailure ?? (_ => { }),
                credentialProvider: () => credentialProvider);
        }

        private sealed class TestCharacterAgent : IConvaiCharacterAgent
        {
            public string CharacterId => "character-id";
            public string CharacterName => "Test Character";
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext => null;
            public EmotionDetectionMode EmotionDetectionMode => EmotionDetectionMode.Off;
            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }
        }

        private sealed class TestCredentialProvider : ICredentialProvider
        {
            private readonly string _apiKey;

            public TestCredentialProvider(string apiKey)
            {
                _apiKey = apiKey;
            }

            public bool HasValidCredentials => !string.IsNullOrWhiteSpace(_apiKey);
            public string GetApiKey() => _apiKey;
            public string GetServerUrl() => "https://core.convai.com";
            public void Refresh() { }
        }

        private sealed class TestAsyncCredentialProvider :
            ICredentialProvider,
            IAsyncCredentialProvider,
            IAsyncCredentialResolutionStatus
        {
            private readonly string _resolutionErrorMessage;
            private readonly Func<int, CancellationToken, Task<string>> _resolver;
            private string _apiKey;

            public TestAsyncCredentialProvider(
                Func<int, CancellationToken, Task<string>> resolver,
                string resolutionErrorMessage = null)
            {
                _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
                _resolutionErrorMessage = resolutionErrorMessage;
            }

            public int ResolutionCount { get; private set; }
            public bool HasValidCredentials => true;
            public string CredentialResolutionErrorMessage => _resolutionErrorMessage;
            public string GetApiKey() => _apiKey;
            public string GetServerUrl() => "https://core.convai.com";

            public async Task EnsureCredentialsAsync(CancellationToken cancellationToken)
            {
                _apiKey = null;
                ResolutionCount++;
                _apiKey = await _resolver(ResolutionCount, cancellationToken);
            }

            public void Refresh()
            {
                _apiKey = null;
            }
        }

        private sealed class TestExplicitAuthTokenCredentialProvider :
            ICredentialProvider,
            IAsyncCredentialProvider,
            IExplicitAuthTokenCredentialProvider
        {
            private string _apiKey;
            private string _nextAuthToken;

            public string ReceivedAuthToken { get; private set; }
            public int ResolutionCount { get; private set; }
            public bool HasValidCredentials => false;
            public string GetApiKey() => _apiKey;
            public string GetServerUrl() => "https://core.convai.com";

            public void SetAuthTokenForNextConnection(string authToken)
            {
                ReceivedAuthToken = authToken?.Trim();
                _nextAuthToken = ReceivedAuthToken;
            }

            public Task EnsureCredentialsAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResolutionCount++;
                _apiKey = _nextAuthToken;
                _nextAuthToken = null;
                return Task.CompletedTask;
            }

            public void Refresh()
            {
                _apiKey = null;
                _nextAuthToken = null;
            }
        }
    }
}
