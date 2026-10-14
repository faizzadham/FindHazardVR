using System;
using Convai.CharacterApi;
using Convai.RestAPI;

namespace Convai.Runtime
{
    /// <summary>
    ///     Creates <see cref="ConvaiRestClientOptions" /> honoring the project-wide
    ///     <see cref="ConvaiApiEnvironment" /> preset from <see cref="ConvaiSettings" />.
    ///     Use this instead of constructing options directly so the Beta/Custom presets apply.
    /// </summary>
    public static class ConvaiRestOptionsFactory
    {
        /// <summary>
        ///     Creates options for the given key (or the configured key when null),
        ///     using the environment configured in <see cref="ConvaiSettings" />.
        /// </summary>
        public static ConvaiRestClientOptions Create(string apiKey = null)
        {
            ConvaiSettings settings = ConvaiSettings.Instance;
            return Create(
                apiKey ?? settings?.ApiKey,
                settings?.ApiEnvironment ?? ConvaiApiEnvironment.Production,
                settings?.RestBaseUrlOverride,
                settings?.ApiMode ?? ConvaiApiMode.Legacy,
                settings?.CharacterApiBaseUrlOverride,
                settings?.CharacterWorkspaceId);
        }

        /// <summary>
        ///     Creates options for an explicit key/environment combination, e.g. to validate
        ///     credentials that are not saved to <see cref="ConvaiSettings" /> yet.
        /// </summary>
        public static ConvaiRestClientOptions Create(string apiKey, ConvaiApiEnvironment environment,
            string customRestBaseUrl,
            ConvaiApiMode apiMode = ConvaiApiMode.Legacy,
            string customCharacterApiBaseUrl = null,
            string workspaceId = null)
        {
            var options = new ConvaiRestClientOptions(apiKey);

            switch (environment)
            {
                case ConvaiApiEnvironment.Beta:
                    options.Environment = ConvaiEnvironment.Beta;
                    break;
                case ConvaiApiEnvironment.Custom:
                    options.Environment = ConvaiEnvironment.Production;
                    if (!string.IsNullOrWhiteSpace(customRestBaseUrl))
                        options.ProductionBaseUrl = customRestBaseUrl.Trim();
                    break;
                default:
                    options.Environment = ConvaiEnvironment.Production;
                    break;
            }

            if (apiMode == ConvaiApiMode.Upgraded)
                options.CharacterApiV2 = CreateCharacterApiOptions(apiKey, environment, customCharacterApiBaseUrl)
                    .WithReadContext(workspaceId);

            return options;
        }

        /// <summary>Editor authoring reads use draft without changing the saved character identity.</summary>
        public static ConvaiRestClientOptions CreateForAuthoring(string apiKey = null)
        {
            ConvaiRestClientOptions options = Create(apiKey);
            if (options.CharacterApiV2 != null)
                options.CharacterApiV2 = options.CharacterApiV2.WithReadContext(
                    options.CharacterApiV2.WorkspaceId, useDraft: true);
            return options;
        }

        /// <summary>
        ///     Creates options for a character read. Qualified references require Upgraded mode.
        ///     Selected Live and pinned references can be preserved even during editor authoring.
        /// </summary>
        public static ConvaiRestClientOptions CreateForCharacterRead(string characterReference,
            string apiKey = null, bool useDraft = false, bool preserveSelectedReference = false)
        {
            bool hasSelector = characterReference != null && characterReference.Length > 36 &&
                characterReference[36] == '-' &&
                Guid.TryParseExact(characterReference.Substring(0, 36), "D", out _);
            ConvaiRestClientOptions options = Create(apiKey);
            if (hasSelector && options.CharacterApiV2 == null)
                throw new InvalidOperationException(
                    "Character version reads require API Mode Upgraded in Project Settings > Convai SDK > Credentials. " +
                    "Legacy REST version reads are not supported.");

            if (options.CharacterApiV2 != null)
                options.CharacterApiV2 = options.CharacterApiV2.WithReadContext(
                    options.CharacterApiV2.WorkspaceId,
                    useDraft: useDraft && !hasSelector && !preserveSelectedReference);
            return options;
        }

        private static ConvaiCharacterApiOptions CreateCharacterApiOptions(
            string apiKey,
            ConvaiApiEnvironment environment,
            string customRestBaseUrl)
        {
            switch (environment)
            {
                case ConvaiApiEnvironment.Beta:
                    return ConvaiCharacterApiOptions.Preview(apiKey);
                case ConvaiApiEnvironment.Custom when !string.IsNullOrWhiteSpace(customRestBaseUrl):
                    return ConvaiCharacterApiOptions.Custom(new Uri(customRestBaseUrl.Trim()), apiKey);
                default:
                    return ConvaiCharacterApiOptions.Production(apiKey);
            }
        }

        /// <summary>
        ///     Creates options for a resolved runtime credential and selects the corresponding wire-level
        ///     authentication header.
        /// </summary>
        internal static ConvaiRestClientOptions CreateForRuntimeCredential(string credential, bool usesAuthToken)
            => CreateForRuntimeCredential(credential, usesAuthToken, ConvaiSettings.Instance);

        internal static ConvaiRestClientOptions CreateForRuntimeCredential(string credential, bool usesAuthToken,
            ConvaiSettings settings)
        {
            ConvaiRestClientOptions options = Create(
                credential,
                settings?.ApiEnvironment ?? ConvaiApiEnvironment.Production,
                settings?.RestBaseUrlOverride,
                settings?.ApiMode ?? ConvaiApiMode.Legacy,
                settings?.CharacterApiBaseUrlOverride,
                settings?.CharacterWorkspaceId);
            options.AuthenticationMode = usesAuthToken
                ? ConvaiAuthenticationMode.AuthToken
                : ConvaiAuthenticationMode.ApiKey;
            // api2 accepts API keys or personal access tokens. The SDK's short-lived API-AUTH-TOKEN
            // is a different credential, so these calls stay on the compatible legacy routes.
            if (usesAuthToken)
                options.CharacterApiV2 = null;
            options.DefaultTimeout = TimeSpan.FromSeconds(
                settings == null || settings.ConnectionTimeout <= 0f
                    ? 30f
                    : settings.ConnectionTimeout);
            if (options.CharacterApiV2 != null)
                options.CharacterApiV2 = options.CharacterApiV2.WithReadContext(
                    options.CharacterApiV2.WorkspaceId, timeout: options.DefaultTimeout);
            return options;
        }
    }
}
