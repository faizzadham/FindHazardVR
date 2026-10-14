#nullable enable
using System;

namespace Convai.CoreApi
{
    public enum CoreApiCredentialKind
    {
        ApiKey,
        AuthToken
    }

    /// <summary>Immutable endpoint and credential configuration for core-service.</summary>
    public sealed class ConvaiCoreApiOptions
    {
        private ConvaiCoreApiOptions(
            Uri connectUri,
            string credential,
            CoreApiCredentialKind credentialKind,
            TimeSpan timeout)
        {
            if (connectUri == null || !connectUri.IsAbsoluteUri)
                throw new ArgumentException("Connect URI must be absolute.", nameof(connectUri));
            if (string.IsNullOrWhiteSpace(credential))
                throw new ArgumentException("Credential is required.", nameof(credential));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            ConnectUri = connectUri;
            Credential = credential;
            CredentialKind = credentialKind;
            Timeout = timeout;
        }

        public Uri ConnectUri { get; }
        internal string Credential { get; }
        public CoreApiCredentialKind CredentialKind { get; }
        public TimeSpan Timeout { get; }

        public static ConvaiCoreApiOptions WithApiKey(
            Uri connectUri,
            string apiKey,
            TimeSpan? timeout = null) =>
            new(connectUri, apiKey, CoreApiCredentialKind.ApiKey,
                timeout ?? TimeSpan.FromSeconds(30));

        public static ConvaiCoreApiOptions WithAuthToken(
            Uri connectUri,
            string authToken,
            TimeSpan? timeout = null) =>
            new(connectUri, authToken, CoreApiCredentialKind.AuthToken,
                timeout ?? TimeSpan.FromSeconds(30));
    }
}
