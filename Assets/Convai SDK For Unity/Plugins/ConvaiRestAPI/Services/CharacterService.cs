#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.RestAPI.Internal;
using Convai.RestAPI.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Convai.CharacterApi;
using Convai.CharacterApi.Runtime;
using Convai.CharacterApi.Contracts.Model;

namespace Convai.RestAPI.Services
{
    /// <summary>
    /// Service for character-related API operations.
    /// </summary>
    public sealed class CharacterService : ConvaiServiceBase
    {
        private readonly CharacterApiCompatibilityReader? _characterApiV2;

        [Obsolete("Use ConvaiCharacterApiClient.Characters.GetAsync with explicit production, preview, or custom options.")]
        public const string ProductionCharacterGetUrl = "https://api.convai.com/character/get";
        private const string CharacterGetEndpoint = "character/get";
        private const string CharacterUpdateEndpoint = "character/update";

        internal CharacterService(
            ConvaiRestClientOptions options,
            IConvaiHttpTransport transport)
            : base(options, transport)
        {
            if (options.CharacterApiV2 != null)
                _characterApiV2 = new CharacterApiCompatibilityReader(
                    options.CharacterApiV2,
                    transport);
        }

        /// <summary>
        /// Gets the details of a character by ID.
        /// </summary>
        /// <param name="characterId">The character ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The character details.</returns>
        public async Task<CharacterDetails> GetDetailsAsync(
            string characterId,
            CancellationToken cancellationToken = default)
        {
            if (_characterApiV2 != null)
            {
                CharacterResponse typed = await _characterApiV2.GetAsync(characterId, cancellationToken)
                    .ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
                return ToLegacyCharacterDetails(typed);
            }
            return await GetLegacyDetailsAsync(characterId, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        private async Task<CharacterDetails> GetLegacyDetailsAsync(string characterId, CancellationToken cancellationToken)
        {
            var requestBody = new Dictionary<string, string>
            {
                { "charID", characterId }
            };
            Uri requestUri = BuildUrl(CharacterGetEndpoint);

            JObject response = await PostAsync<JObject>(
                CharacterGetEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);

            try
            {
                JToken detailsToken = response["response"] ?? response;
                CharacterDetails? details = detailsToken.ToObject<CharacterDetails>();
                if (details == null)
                {
                    throw ConvaiRestException.ParseError(
                        $"Failed to deserialize response to {nameof(CharacterDetails)}: result was null",
                        requestUri,
                        response.ToString(Formatting.None));
                }

                return details;
            }
            catch (JsonException ex)
            {
                throw ConvaiRestException.ParseError(
                    $"Failed to deserialize response to {nameof(CharacterDetails)}: {ex.Message}",
                    requestUri,
                    response.ToString(Formatting.None),
                    ex);
            }
        }

        /// <summary>
        /// Updates character settings.
        /// </summary>
        /// <param name="characterId">The character ID.</param>
        /// <param name="updateData">The update data object (will be serialized to JSON).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        [Obsolete("Use the typed ConvaiCharacterApiClient.Characters.UpdateAsync method.")]
        public async Task UpdateAsync(
            string characterId,
            object updateData,
            CancellationToken cancellationToken = default)
        {
            JObject requestBody = updateData == null
                ? new JObject()
                : JObject.FromObject(updateData);
            requestBody["charID"] = characterId;

            await PostVoidAsync(
                CharacterUpdateEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        /// <summary>
        /// Gets whether long-term memory is enabled for the character.
        /// </summary>
        public async Task<bool> GetMemoryEnabledAsync(
            string characterId,
            CancellationToken cancellationToken = default)
        {
            // This read must observe SetMemoryEnabledAsync, which still writes the mutable legacy record.
            CharacterDetails details = await GetLegacyDetailsAsync(characterId, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            return details.MemorySettings?.IsEnabled ?? false;
        }

        /// <summary>
        /// Enables or disables long-term memory for the character.
        /// </summary>
        public async Task SetMemoryEnabledAsync(
            string characterId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new CharacterUpdateRequest(characterId, enabled);
            await PostVoidAsync(
                CharacterUpdateEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        internal static CharacterDetails ToLegacyCharacterDetails(CharacterResponse source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var memorySettings = new MemorySettings(
                ReadValue(source.MemorySettings, "enabled", false));
            var modelDetails = new CharacterDetails.ModelDetailsData(
                ReadValue(source.ModelDetails, "modelType", source.ModelType ?? string.Empty),
                ReadValue(source.ModelDetails, "modelLink", string.Empty),
                ReadValue(source.ModelDetails, "modelPlaceholder", string.Empty));
            var guardrailMeta = new CharacterDetails.GuardrailMetaData(
                ReadValue(source.GuardrailMeta, "limitResponseLevel", 0),
                ReadValue(source.GuardrailMeta, "blockedWords", new List<string>()));
            Dictionary<string, object>? personality = ReadValue<Dictionary<string, object>?>(
                source.CharacterTraits, "personality_traits", null);
            var characterTraits = new CharacterDetails.CharacterTraitsData(
                ReadValue(source.CharacterTraits, "catch_phrases", new List<string>()),
                ReadValue(source.CharacterTraits, "speaking_style",
                    ReadValue(source.SpeakingStyle, "name", string.Empty)),
                new CharacterDetails.PersonalityTraits(
                    ReadValue(personality, "openness", 0),
                    ReadValue(personality, "sensitivity", 0),
                    ReadValue(personality, "extraversion", 0),
                    ReadValue(personality, "agreeableness", 0),
                    ReadValue(personality, "meticulousness", 0)));

            var result = new CharacterDetails(
                memorySettings,
                source.CharacterName ?? string.Empty,
                source.UserId ?? string.Empty,
                source.CharacterId ?? string.Empty,
                source.Listing ?? string.Empty,
                source.LanguageCodes ?? new List<string>(),
                source.VoiceType ?? string.Empty,
                source.CharacterActions ?? new List<string>(),
                new List<string>(),
                modelDetails,
                source.LanguageCode ?? string.Empty,
                guardrailMeta,
                characterTraits,
                source.Timestamp == default
                    ? string.Empty
                    : source.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                string.Empty,
                string.Empty,
                source.Pronunciations ?? new List<Dictionary<string, object>>(),
                ConvertBoostedWords(source.BoostedWords),
                new List<string>(),
                string.Empty,
                string.Empty,
                source.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                source.Backstory ?? string.Empty);
            result.ApplyCharacterApiCompatibilityFields(
                source.IsNarrativeDriven,
                source.ModerationEnabled ?? false,
                source.EditCharacterAccess);
            return result;
        }

        private static List<string> ConvertBoostedWords(
            IEnumerable<Dictionary<string, string>>? boostedWords) =>
            boostedWords?
                .Where(entry => entry != null)
                .Select(entry =>
                    entry.TryGetValue("spelledAs", out string spelledAs)
                        ? spelledAs
                        : entry.TryGetValue("word", out string word)
                            ? word
                            : entry.Values.FirstOrDefault() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList()
            ?? new List<string>();

        private static T ReadValue<T>(
            IReadOnlyDictionary<string, object>? values,
            string key,
            T fallback)
        {
            if (values == null || !values.TryGetValue(key, out object value) || value == null)
                return fallback;
            if (value is T typed)
                return typed;

            try
            {
                return JToken.FromObject(value).ToObject<T>() ?? fallback;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }
    }

    /// <summary>
    /// Keeps the SDK 4.x Character read adapter runtime-safe without referencing the full
    /// authoring assembly. New code should use ConvaiCharacterApiClient directly.
    /// </summary>
    internal sealed class CharacterApiCompatibilityReader
    {
        private readonly ConvaiCharacterNarrativeClient _client;

        internal CharacterApiCompatibilityReader(ConvaiCharacterApiOptions options, IConvaiHttpTransport transport)
        {
            _client = new ConvaiCharacterNarrativeClient(options, new LegacyConvaiApiTransportAdapter(transport));
        }

        internal Task<CharacterResponse> GetAsync(string characterId, CancellationToken cancellationToken) =>
            ReadAsync(() => _client.GetCharacterAsync(characterId, cancellationToken));

        internal Task<IReadOnlyList<SectionResponse>> GetSectionsAsync(string characterId, CancellationToken cancellationToken) =>
            ReadAsync(() => _client.ListSectionsAsync(characterId, cancellationToken));

        internal Task<IReadOnlyList<TriggerResponse>> GetTriggersAsync(string characterId, CancellationToken cancellationToken) =>
            ReadAsync(() => _client.ListTriggersAsync(characterId, cancellationToken));

        private async Task<T> ReadAsync<T>(Func<Task<T>> read)
        {
            try { return await read().ConfigureAwait(ApiAwait.ResumeOnCapturedContext); }
            catch (ConvaiApiException ex)
            {
                throw ConvaiRestException.FromApiException(ex);
            }
        }
    }
}
