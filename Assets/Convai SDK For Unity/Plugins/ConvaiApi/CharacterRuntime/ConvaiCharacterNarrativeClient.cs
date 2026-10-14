#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi.Contracts.Model;
using Newtonsoft.Json;

namespace Convai.CharacterApi.Runtime
{
    /// <summary>Minimal read-only Character API shipped with the default runtime.</summary>
    public sealed class ConvaiCharacterNarrativeClient : IDisposable
    {
        private readonly ConvaiCharacterApiOptions _options;
        private readonly IConvaiApiTransport _transport;
        private readonly bool _ownsTransport;
        private bool _disposed;

        public ConvaiCharacterNarrativeClient(
            Uri baseUri, string apiKey, TimeSpan? timeout = null, IConvaiApiTransport? transport = null)
            : this(ConvaiCharacterApiOptions.Custom(baseUri, apiKey, timeout: timeout), transport)
        {
        }

        public ConvaiCharacterNarrativeClient(ConvaiCharacterApiOptions options, IConvaiApiTransport? transport = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? ConvaiApiTransportFactory.Create(options.Timeout);
            _ownsTransport = transport == null;
        }

        public Task<CharacterResponse> GetCharacterAsync(string characterId, CancellationToken cancellationToken = default) =>
            GetAsync<CharacterResponse>("character/get", characterId, cancellationToken);

        public Task<IReadOnlyList<SectionResponse>> ListSectionsAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            GetListAsync<SectionResponse>("character/narrative/sections/list", characterId, cancellationToken);

        public Task<IReadOnlyList<TriggerResponse>> ListTriggersAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            GetListAsync<TriggerResponse>("character/narrative/triggers/list", characterId, cancellationToken);

        private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, string characterId,
            CancellationToken cancellationToken) =>
            await GetAsync<List<T>>(path, characterId, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);

        private async Task<T> GetAsync<T>(string path, string characterId, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConvaiCharacterNarrativeClient));
            string reference = _options.GetReadReference(characterId);
            if (reference != characterId)
            {
                // Existing unversioned and public characters cannot be read through explicit draft.
                // Probe the bare reference before choosing an authoring snapshot; never swallow a 404/409.
                CharacterResponse current = await SendAsync<CharacterResponse>(
                    "character/get", characterId, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
                if (!current.EditCharacterAccess || string.IsNullOrEmpty(current.ResolvedRevisionId))
                {
                    reference = characterId;
                    if (typeof(T) == typeof(CharacterResponse))
                        return (T)(object)current;
                }
            }
            return await SendAsync<T>(path, reference, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        private async Task<T> SendAsync<T>(string path, string reference, CancellationToken cancellationToken)
        {
            var builder = new UriBuilder(new Uri(_options.BaseUri, path))
            {
                Query = "character_id=" + Uri.EscapeDataString(reference)
            };
            if (_options.WorkspaceId != null)
                builder.Query = builder.Query.TrimStart('?') + "&workspace_id=" + Uri.EscapeDataString(_options.WorkspaceId);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_options.CredentialKind == CharacterApiCredentialKind.ApiKey)
                headers["CONVAI-API-KEY"] = _options.Credential;
            else
                headers["Authorization"] = "Bearer " + _options.Credential;
            var request = new ConvaiApiRequest(builder.Uri, ConvaiApiHttpMethod.Get, headers, timeout: _options.Timeout);
            ConvaiApiResponse response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            if (!response.IsSuccess) throw ConvaiApiException.FromResponse(response);
            try
            {
                T? result = JsonConvert.DeserializeObject<T>(response.BodyText);
                return result ?? throw new JsonSerializationException("API response was null.");
            }
            catch (JsonException ex)
            {
                throw new ConvaiApiException("Character API response did not match the generated contract.",
                    ConvaiApiErrorCategory.Serialization, response.StatusCode,
                    response.Header("x-request-trace-id") ?? response.Header("x-request-id"),
                    innerException: ex, responseBody: response.BodyText, requestUri: response.Uri);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsTransport) _transport.Dispose();
        }
    }
}
