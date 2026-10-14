using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Errors;
using Convai.Infrastructure.Networking;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Core.Configuration;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class AuthTokenCredentialProviderTests
    {
        private ConvaiSettings _settings;

        [SetUp]
        public void SetUp()
        {
            ConvaiAuthTokenProviderRegistry.Clear();
            _settings = ScriptableObject.CreateInstance<ConvaiSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiAuthTokenProviderRegistry.Clear();
            if (_settings != null)
                UnityEngine.Object.DestroyImmediate(_settings);
        }

        [Test]
        public void AuthTokenResult_EmptyToken_IsFailureWithSafeDefaultMessage()
        {
            var result = new AuthTokenResult("   ");

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Token, Is.Empty);
            Assert.That(result.ErrorMessage, Is.EqualTo("Auth token provider returned an empty token."));
        }

        [Test]
        public void AuthTokenResult_ErrorMessage_OverridesOtherwiseValidToken()
        {
            var result = new AuthTokenResult("token", errorMessage: "provider rejected the request");

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Token, Is.Empty);
            Assert.That(result.ErrorMessage, Is.EqualTo("provider rejected the request"));
        }

        [Test]
        public void CredentialProvider_BeforeResolution_IsEmptyButRegisteredProviderIsValidConfiguration()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("token")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            Assert.That(credentialProvider.HasValidCredentials, Is.True);
            Assert.That(credentialProvider.GetApiKey(), Is.Empty);
            Assert.That(credentialProvider.ConfigurationErrorCode, Is.Empty);
            Assert.That(credentialProvider.ConfigurationErrorMessage, Is.Empty);
        }

        [Test]
        public void TransportConfiguration_AuthTokenCredentialProvider_UsesAuthTokenAuthentication()
        {
            var credentialProvider = new AuthTokenCredentialProvider(_settings);
            ITransportConfiguration transportConfiguration = new TransportConfigurationBuilder()
                .WithCredentialProvider(credentialProvider)
                .Build();

            Assert.That(TransportAuthenticationSupport.UsesAuthToken(transportConfiguration), Is.True);
            Assert.That(
                TransportAuthenticationSupport.GetHeaderName(transportConfiguration),
                Is.EqualTo(TransportAuthenticationSupport.AuthTokenHeaderName));
        }

        [Test]
        public async Task CredentialProvider_EnsureCredentialsAsync_StoresTrimmedFreshToken()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded(
                    "  fresh-token  ",
                    DateTimeOffset.UtcNow.AddMinutes(30))));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("fresh-token"));
            Assert.That(credentialProvider.CredentialResolutionErrorMessage, Is.Empty);
            Assert.That(tokenProvider.CallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task CredentialProvider_EnsureCredentialsAsyncTwice_RefetchesAndReplacesToken()
        {
            var tokenProvider = new SequenceTokenProvider((attempt, _) =>
                Task.FromResult(AuthTokenResult.Succeeded($"token-{attempt}")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);
            string firstToken = credentialProvider.GetApiKey();
            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(firstToken, Is.EqualTo("token-1"));
            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("token-2"));
            Assert.That(tokenProvider.CallCount, Is.EqualTo(2));
        }

        [Test]
        public async Task CredentialProvider_ExplicitToken_BypassesProviderForOneConnectionOnly()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("provider-token")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            credentialProvider.SetAuthTokenForNextConnection("  explicit-token  ");
            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("explicit-token"));
            Assert.That(tokenProvider.CallCount, Is.Zero);

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("provider-token"));
            Assert.That(tokenProvider.CallCount, Is.EqualTo(1),
                "The caller-supplied token must not be reused by a later connection.");
        }

        [Test]
        public async Task CredentialProvider_ExplicitToken_WorksWithoutProviderOrEndpoint()
        {
            SetSetting("_authTokenEndpointUrl", string.Empty);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            credentialProvider.SetAuthTokenForNextConnection("explicit-token");
            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("explicit-token"));
            Assert.That(credentialProvider.CredentialResolutionErrorMessage, Is.Empty);
        }

        [Test]
        public async Task CredentialProvider_Refresh_ClearsPendingExplicitToken()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("provider-token")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            credentialProvider.SetAuthTokenForNextConnection("explicit-token");
            credentialProvider.Refresh();
            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("provider-token"));
            Assert.That(tokenProvider.CallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task CredentialProvider_FailedSecondResolution_ClearsStaleToken()
        {
            var tokenProvider = new SequenceTokenProvider((attempt, _) =>
                Task.FromResult(attempt == 1
                    ? AuthTokenResult.Succeeded("token-1")
                    : AuthTokenResult.Failed("second resolution failed")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);
            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("token-1"));

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.Empty,
                "A failed reconnect must never reuse the previous session token.");
            Assert.That(credentialProvider.CredentialResolutionErrorMessage,
                Is.EqualTo("second resolution failed"));
            Assert.That(tokenProvider.CallCount, Is.EqualTo(2));
        }

        [Test]
        public async Task CredentialProvider_ProviderException_IsRedactedAndClearsCredential()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromException<AuthTokenResult>(
                    new InvalidOperationException("sensitive provider detail")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.Empty);
            Assert.That(credentialProvider.CredentialResolutionErrorMessage,
                Is.EqualTo("Auth token provider failed (InvalidOperationException)."));
            Assert.That(credentialProvider.CredentialResolutionErrorMessage,
                Does.Not.Contain("sensitive provider detail"));
        }

        [Test]
        public void CredentialProvider_WhenFaultRacesCallerCancellation_PropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            var tokenProvider = new SequenceTokenProvider((_, _) =>
            {
                cancellation.Cancel();
                return Task.FromException<AuthTokenResult>(
                    new InvalidOperationException("provider fault after cancellation"));
            });
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await credentialProvider.EnsureCredentialsAsync(cancellation.Token));
            Assert.That(credentialProvider.GetApiKey(), Is.Empty);
        }

        [Test]
        public async Task CredentialProvider_PastExpirationMetadata_DoesNotRejectFreshlyResolvedToken()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded(
                    "expired-token",
                    DateTimeOffset.UtcNow.AddMinutes(-1))));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("expired-token"));
            Assert.That(credentialProvider.CredentialResolutionErrorMessage, Is.Empty);
        }

        [Test]
        public async Task CredentialProvider_CancelledReconnect_ClearsStaleTokenAndPropagatesCancellation()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("initial-token")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);
            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await credentialProvider.EnsureCredentialsAsync(cancellation.Token));
            Assert.That(credentialProvider.GetApiKey(), Is.Empty);
            Assert.That(tokenProvider.CallCount, Is.EqualTo(1),
                "An already-cancelled reconnect should clear the old token before invoking a provider.");
        }

        [Test]
        public async Task CredentialProvider_Refresh_ClearsResolvedToken()
        {
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("token")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);
            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            credentialProvider.Refresh();

            Assert.That(credentialProvider.GetApiKey(), Is.Empty);
            Assert.That(credentialProvider.CredentialResolutionErrorMessage, Is.Empty);
        }

        [Test]
        public void CredentialProvider_NoProviderOrEndpoint_ReportsProviderMissingConfiguration()
        {
            SetSetting("_authTokenEndpointUrl", string.Empty);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            Assert.That(credentialProvider.HasValidCredentials, Is.False);
            Assert.That(credentialProvider.ConfigurationErrorCode,
                Is.EqualTo(SessionErrorCodes.ConfigAuthTokenProviderMissing));
            Assert.That(credentialProvider.ConfigurationErrorMessage,
                Does.Contain("requires a registered IConvaiAuthTokenProvider"));
        }

        [Test]
        public void CredentialProvider_InvalidEndpoint_ReportsEndpointInvalidConfiguration()
        {
            SetSetting("_authTokenEndpointUrl", "relative/token");
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            Assert.That(credentialProvider.HasValidCredentials, Is.False);
            Assert.That(credentialProvider.ConfigurationErrorCode,
                Is.EqualTo(SessionErrorCodes.ConfigAuthTokenEndpointInvalid));
            Assert.That(credentialProvider.ConfigurationErrorMessage,
                Is.EqualTo(EndpointAuthTokenProvider.InvalidEndpointMessage));
        }

        [Test]
        public async Task CredentialProvider_RegisteredProvider_TakesPrecedenceOverInvalidEndpoint()
        {
            SetSetting("_authTokenEndpointUrl", "relative/token");
            var tokenProvider = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("registered-token")));
            ConvaiAuthTokenProviderRegistry.Register(tokenProvider);
            var credentialProvider = new AuthTokenCredentialProvider(_settings);

            await credentialProvider.EnsureCredentialsAsync(CancellationToken.None);

            Assert.That(credentialProvider.HasValidCredentials, Is.True);
            Assert.That(credentialProvider.GetApiKey(), Is.EqualTo("registered-token"));
            Assert.That(tokenProvider.CallCount, Is.EqualTo(1));
        }

        [Test]
        public async Task DelegateProvider_ValidToken_TrimsValueAndForwardsCancellationToken()
        {
            CancellationToken observedToken = default;
            var provider = new DelegateAuthTokenProvider(ct =>
            {
                observedToken = ct;
                return Task.FromResult("  delegated-token  ");
            });
            using var cancellation = new CancellationTokenSource();

            AuthTokenResult result = await provider.GetTokenAsync(cancellation.Token);

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Token, Is.EqualTo("delegated-token"));
            Assert.That(observedToken, Is.EqualTo(cancellation.Token));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public async Task DelegateProvider_EmptyToken_ReturnsFailure(string token)
        {
            var provider = new DelegateAuthTokenProvider(_ => Task.FromResult(token));

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Token, Is.Empty);
            Assert.That(result.ErrorMessage, Is.EqualTo("Auth token delegate returned an empty token."));
        }

        [Test]
        public async Task DelegateProvider_NullTask_ReturnsFailure()
        {
            var provider = new DelegateAuthTokenProvider(_ => null);

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo("Auth token delegate returned no task."));
        }

        [Test]
        public async Task DelegateProvider_Exception_ReturnsRedactedFailureType()
        {
            var provider = new DelegateAuthTokenProvider(_ =>
                Task.FromException<string>(new InvalidOperationException("sensitive response")));

            AuthTokenResult result = await provider.GetTokenAsync(CancellationToken.None);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage,
                Is.EqualTo("Auth token delegate failed (InvalidOperationException)."));
            Assert.That(result.ErrorMessage, Does.Not.Contain("sensitive response"));
        }

        [Test]
        public void DelegateProvider_WhenFaultRacesCallerCancellation_PropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            var provider = new DelegateAuthTokenProvider(_ =>
            {
                cancellation.Cancel();
                return Task.FromException<string>(
                    new InvalidOperationException("delegate fault after cancellation"));
            });

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await provider.GetTokenAsync(cancellation.Token));
        }

        [Test]
        public void DelegateProvider_CallerCancellation_PropagatesOperationCanceledException()
        {
            var provider = new DelegateAuthTokenProvider(ct => Task.FromCanceled<string>(ct));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await provider.GetTokenAsync(cancellation.Token));
        }

        [Test]
        public void DelegateProvider_NullDelegate_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new DelegateAuthTokenProvider(null));
        }

        [Test]
        public void Registry_RegisterReplacesProvider_AndStaleUnregisterDoesNotClearReplacement()
        {
            var first = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("first")));
            var second = new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("second")));

            ConvaiAuthTokenProviderRegistry.Register(first);
            ConvaiAuthTokenProviderRegistry.Register(second);

            Assert.That(ConvaiAuthTokenProviderRegistry.IsRegistered, Is.True);
            Assert.That(ConvaiAuthTokenProviderRegistry.Unregister(first), Is.False);
            Assert.That(ConvaiAuthTokenProviderRegistry.TryGetProvider(out IConvaiAuthTokenProvider active),
                Is.True);
            Assert.That(active, Is.SameAs(second));
            Assert.That(ConvaiAuthTokenProviderRegistry.Unregister(second), Is.True);
            Assert.That(ConvaiAuthTokenProviderRegistry.IsRegistered, Is.False);
        }

        [Test]
        public void Registry_RegisterNull_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => ConvaiAuthTokenProviderRegistry.Register(null));
        }

        [Test]
        public void CredentialProviderFactory_ApiKeyMode_UsesProjectSettingsProviderEvenWhenTokenProviderExists()
        {
            _settings.SetApiKey("api-key");
            ConvaiAuthTokenProviderRegistry.Register(new SequenceTokenProvider((_, _) =>
                Task.FromResult(AuthTokenResult.Succeeded("token"))));

            ICredentialProvider provider = CredentialProviderFactory.Create(_settings);

            Assert.That(provider, Is.TypeOf<ProjectSettingsCredentialProvider>());
            Assert.That(provider.GetApiKey(), Is.EqualTo("api-key"));
        }

        [Test]
        public void CredentialProviderFactory_AuthTokenMode_UsesAsyncTokenProvider()
        {
            SetSetting("_authMode", ConvaiAuthMode.AuthToken);
            SetSetting("_authTokenEndpointUrl", "https://auth.example.com/convai-token");

            ICredentialProvider provider = CredentialProviderFactory.Create(_settings);

            Assert.That(provider, Is.TypeOf<AuthTokenCredentialProvider>());
            Assert.That(provider, Is.InstanceOf<IAsyncCredentialProvider>());
            Assert.That(provider.HasValidCredentials, Is.True);
            Assert.That(provider.GetApiKey(), Is.Empty);
        }

        private void SetSetting(string fieldName, object value)
        {
            FieldInfo field = typeof(ConvaiSettings).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(typeof(ConvaiSettings).FullName, fieldName);

            field.SetValue(_settings, value);
        }

        private sealed class SequenceTokenProvider : IConvaiAuthTokenProvider
        {
            private readonly Func<int, CancellationToken, Task<AuthTokenResult>> _resolve;

            public SequenceTokenProvider(Func<int, CancellationToken, Task<AuthTokenResult>> resolve)
            {
                _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            }

            public int CallCount { get; private set; }

            public Task<AuthTokenResult> GetTokenAsync(CancellationToken cancellationToken)
            {
                CallCount++;
                return _resolve(CallCount, cancellationToken);
            }
        }
    }
}
