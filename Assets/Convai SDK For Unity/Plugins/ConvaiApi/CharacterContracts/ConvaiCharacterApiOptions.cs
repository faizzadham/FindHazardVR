#nullable enable
using System;

namespace Convai.CharacterApi
{
    public enum CharacterApiCredentialKind
    {
        ApiKey,
        PersonalAccessToken
    }

    public sealed class ConvaiCharacterApiOptions
    {
        private static readonly Uri ProductionUri = new("https://api2.convai.com/");
        private static readonly Uri PreviewUri = new("https://api2-stg.convai.com/");

        private ConvaiCharacterApiOptions(
            Uri baseUri,
            string credential,
            CharacterApiCredentialKind credentialKind,
            bool allowCompatibility,
            TimeSpan timeout,
            string? workspaceId = null,
            bool useDraft = false)
        {
            if (baseUri == null || !baseUri.IsAbsoluteUri) throw new ArgumentException("Base URI must be absolute.", nameof(baseUri));
            if (string.IsNullOrWhiteSpace(credential)) throw new ArgumentException("Credential is required.", nameof(credential));
            BaseUri = NormalizeBaseUri(baseUri);
            Credential = credential;
            CredentialKind = credentialKind;
            AllowCompatibilityOperations = allowCompatibility;
            Timeout = timeout;
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();
            UseDraft = useDraft;
        }

        public Uri BaseUri { get; }
        internal string Credential { get; }
        public CharacterApiCredentialKind CredentialKind { get; }
        public bool AllowCompatibilityOperations { get; }
        public TimeSpan Timeout { get; }
        public string? WorkspaceId { get; }
        public bool UseDraft { get; }

        /// <summary>Copies these options with an explicit authoring scope. Already-qualified IDs are preserved.</summary>
        public ConvaiCharacterApiOptions WithReadContext(string? workspaceId, bool useDraft = false,
            TimeSpan? timeout = null) =>
            new(BaseUri, Credential, CredentialKind, AllowCompatibilityOperations, timeout ?? Timeout,
                workspaceId, useDraft);

        public string GetReadReference(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID is required.", nameof(characterId));
            // Only canonical bare IDs are composed here; never double-suffix an explicit reference.
            return UseDraft && Guid.TryParseExact(characterId, "D", out _)
                ? characterId + "-draft"
                : characterId;
        }

        /// <summary>
        /// Creates options for the production Character API.
        /// </summary>
        public static ConvaiCharacterApiOptions Production(
            string credential,
            CharacterApiCredentialKind credentialKind = CharacterApiCredentialKind.ApiKey,
            bool allowCompatibilityOperations = false,
            TimeSpan? timeout = null) =>
            new(ProductionUri, credential, credentialKind, allowCompatibilityOperations,
                timeout ?? TimeSpan.FromSeconds(30));

        /// <summary>
        /// Creates options for the staging Character API.
        /// </summary>
        public static ConvaiCharacterApiOptions Preview(
            string credential,
            CharacterApiCredentialKind credentialKind = CharacterApiCredentialKind.ApiKey,
            bool allowCompatibilityOperations = false,
            TimeSpan? timeout = null) =>
            new(PreviewUri, credential, credentialKind, allowCompatibilityOperations,
                timeout ?? TimeSpan.FromSeconds(30));

        public static ConvaiCharacterApiOptions Custom(
            Uri baseUri,
            string credential,
            CharacterApiCredentialKind credentialKind = CharacterApiCredentialKind.ApiKey,
            bool allowCompatibilityOperations = false,
            TimeSpan? timeout = null) =>
            new(baseUri, credential, credentialKind, allowCompatibilityOperations,
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
