#nullable enable
using System;

namespace Convai.PlatformApi.Legacy
{
    public enum PlatformLegacyCredentialKind
    {
        ApiKey,
        AuthToken
    }

    /// <summary>
    /// Immutable configuration for platform/account compatibility routes.
    /// This module remains legacy until each route has an authoritative owner contract.
    /// </summary>
    public sealed class ConvaiPlatformLegacyOptions
    {
        private ConvaiPlatformLegacyOptions(
            Uri baseUri,
            string credential,
            PlatformLegacyCredentialKind credentialKind,
            TimeSpan timeout)
        {
            if (baseUri == null || !baseUri.IsAbsoluteUri)
                throw new ArgumentException("Base URI must be absolute.", nameof(baseUri));
            if (string.IsNullOrWhiteSpace(credential))
                throw new ArgumentException("Credential is required.", nameof(credential));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            BaseUri = NormalizeBaseUri(baseUri);
            Credential = credential;
            CredentialKind = credentialKind;
            Timeout = timeout;
        }

        public Uri BaseUri { get; }
        internal string Credential { get; }
        public PlatformLegacyCredentialKind CredentialKind { get; }
        public TimeSpan Timeout { get; }

        public static ConvaiPlatformLegacyOptions WithApiKey(
            Uri baseUri,
            string apiKey,
            TimeSpan? timeout = null) =>
            new(baseUri, apiKey, PlatformLegacyCredentialKind.ApiKey,
                timeout ?? TimeSpan.FromSeconds(30));

        public static ConvaiPlatformLegacyOptions WithAuthToken(
            Uri baseUri,
            string authToken,
            TimeSpan? timeout = null) =>
            new(baseUri, authToken, PlatformLegacyCredentialKind.AuthToken,
                timeout ?? TimeSpan.FromSeconds(30));

        private static Uri NormalizeBaseUri(Uri baseUri)
        {
            var builder = new UriBuilder(baseUri);
            if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
                builder.Path += "/";
            return builder.Uri;
        }
    }
}
