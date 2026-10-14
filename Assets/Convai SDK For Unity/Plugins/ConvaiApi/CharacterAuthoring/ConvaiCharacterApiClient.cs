#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi.Contracts;
using Convai.CharacterApi.Runtime;
using Newtonsoft.Json;

namespace Convai.CharacterApi
{
    public sealed class ConvaiCharacterApiClient : IDisposable
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly ConvaiCharacterApiOptions _options;
        private readonly IConvaiApiTransport _transport;
        private readonly bool _ownsTransport;
        private bool _disposed;

        public ConvaiCharacterApiClient(
            ConvaiCharacterApiOptions options,
            IConvaiApiTransport? transport = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _transport = transport ?? ConvaiApiTransportFactory.Create(options.Timeout);
            _ownsTransport = transport == null;
            ReadClient = new ConvaiCharacterNarrativeClient(options, _transport);
            Access = new CharacterApiResourceClient(this, "access");
            ActionEvents = new CharacterApiResourceClient(this, "action-events");
            Animations = new AnimationResourceClient(this);
            Assets = new CharacterApiResourceClient(this, "assets");
            Avatars = new CharacterApiResourceClient(this, "avatars");
            Characters = new CharacterResourceClient(this);
            CharacterVersions = new CharacterApiResourceClient(this, "character-versions");
            ChatHistory = new CharacterApiResourceClient(this, "chat-history");
            Domains = new CharacterApiResourceClient(this, "domains");
            Experiences = new CharacterApiResourceClient(this, "experience");
            Functions = new CharacterApiResourceClient(this, "functions");
            InteractionLogs = new CharacterApiResourceClient(this, "interaction-logs");
            LlmModels = new CharacterApiResourceClient(this, "llm-models");
            Mcp = new CharacterApiResourceClient(this, "mcp");
            Mindview = new CharacterApiResourceClient(this, "mindview");
            Narrative = new NarrativeResourceClient(this);
            Project = new CharacterApiResourceClient(this, "project");
            Snapshots = new CharacterApiResourceClient(this, "snapshots");
            Tts = new CharacterApiResourceClient(this, "tts");
            ExperienceSessions = new CharacterApiResourceClient(this, "experience-sessions");
            KnowledgeBank = new CharacterApiResourceClient(this, "knowledge-bank");
            StreamAccess = new CharacterApiResourceClient(this, "stream-access");
            UserAccount = new CharacterApiResourceClient(this, "user-account");
            Workspaces = new CharacterApiResourceClient(this, "workspaces");
            WorkspacePreview = new CharacterApiResourceClient(this, "workspace-preview");
            XpStreams = new CharacterApiResourceClient(this, "xp-streams");
        }

        public ConvaiApiTransportCapabilities TransportCapabilities => _transport.Capabilities;
        internal ConvaiCharacterNarrativeClient ReadClient { get; }
        public CharacterApiResourceClient Access { get; }
        public CharacterApiResourceClient ActionEvents { get; }
        public AnimationResourceClient Animations { get; }
        public CharacterApiResourceClient Assets { get; }
        public CharacterApiResourceClient Avatars { get; }
        public CharacterResourceClient Characters { get; }
        public CharacterApiResourceClient CharacterVersions { get; }
        public CharacterApiResourceClient ChatHistory { get; }
        public CharacterApiResourceClient Domains { get; }
        public CharacterApiResourceClient Experiences { get; }
        public CharacterApiResourceClient Functions { get; }
        public CharacterApiResourceClient InteractionLogs { get; }
        public CharacterApiResourceClient LlmModels { get; }
        public CharacterApiResourceClient Mcp { get; }
        public CharacterApiResourceClient Mindview { get; }
        public NarrativeResourceClient Narrative { get; }
        public CharacterApiResourceClient Project { get; }
        public CharacterApiResourceClient Snapshots { get; }
        public CharacterApiResourceClient Tts { get; }
        public CharacterApiResourceClient ExperienceSessions { get; }
        public CharacterApiResourceClient KnowledgeBank { get; }
        public CharacterApiResourceClient StreamAccess { get; }
        public CharacterApiResourceClient UserAccount { get; }
        public CharacterApiResourceClient Workspaces { get; }
        public CharacterApiResourceClient WorkspacePreview { get; }
        public CharacterApiResourceClient XpStreams { get; }

        public async Task<TResponse> ExecuteAsync<TResponse>(
            CharacterApiOperation operation,
            IReadOnlyDictionary<string, object?>? routeValues = null,
            IReadOnlyDictionary<string, object?>? query = null,
            object? body = null,
            IReadOnlyDictionary<string, string>? form = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidateOperation(operation);
            if (operation.RequestMediaType == "multipart/form-data" ||
                operation.ResponseMediaType == "text/event-stream")
                throw new ConvaiApiException("Use ExecuteSseAsync for streaming. Multipart uploads are not supported by the JSON executor.",
                    ConvaiApiErrorCategory.Contract);
            ConvaiApiRequest request = BuildRequest(operation, routeValues, query, body, form);
            ConvaiApiResponse response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
            if (!response.IsSuccess) throw ConvaiApiException.FromResponse(response);
            if (typeof(TResponse) == typeof(CharacterApiEmptyResponse))
                return (TResponse)(object)new CharacterApiEmptyResponse();
            try
            {
                TResponse? value = JsonConvert.DeserializeObject<TResponse>(response.BodyText, JsonSettings);
                if (value == null)
                    throw new JsonSerializationException("Response was null.");
                return value;
            }
            catch (JsonException ex)
            {
                throw new ConvaiApiException(
                    $"Response for {operation.OperationId} did not match the generated contract.",
                    ConvaiApiErrorCategory.Serialization,
                    response.StatusCode,
                    response.Header("x-request-trace-id"),
                    innerException: ex,
                    requestUri: response.Uri);
            }
        }

        public Task ExecuteSseAsync(
            CharacterApiOperation operation,
            Action<ConvaiApiSseEvent> onEvent,
            IReadOnlyDictionary<string, object?>? routeValues = null,
            IReadOnlyDictionary<string, object?>? query = null,
            object? body = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidateOperation(operation);
            if (!string.Equals(operation.ResponseMediaType, "text/event-stream", StringComparison.Ordinal))
                throw new ConvaiApiException(
                    $"{operation.OperationId} is not an SSE operation.",
                    ConvaiApiErrorCategory.Contract);
            ConvaiApiRequest request = BuildRequest(operation, routeValues, query, body, null);
            return _transport.SendSseAsync(request, onEvent, cancellationToken);
        }

        private void ValidateOperation(CharacterApiOperation operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (operation.Classification == CharacterApiOperationClass.Internal)
                throw new ConvaiApiException("Internal Character API operations are not callable by the SDK client.",
                    ConvaiApiErrorCategory.Contract);
            if (operation.Classification == CharacterApiOperationClass.Compatibility &&
                !_options.AllowCompatibilityOperations)
                throw new ConvaiApiException(
                    "Compatibility operation requires explicit opt-in.",
                    ConvaiApiErrorCategory.Contract);
        }

        private ConvaiApiRequest BuildRequest(
            CharacterApiOperation operation,
            IReadOnlyDictionary<string, object?>? routeValues,
            IReadOnlyDictionary<string, object?>? query,
            object? body,
            IReadOnlyDictionary<string, string>? form)
        {
            string path = operation.Path;
            if (routeValues != null)
            {
                foreach (KeyValuePair<string, object?> pair in routeValues)
                    path = path.Replace("{" + pair.Key + "}", Escape(pair.Value));
            }
            if (path.IndexOf('{') >= 0)
                throw new ConvaiApiException($"Missing route value for {operation.Path}.", ConvaiApiErrorCategory.BadRequest);

            // Read context is deliberately limited to these documented, scoped reads.
            // It never rewrites mutation targets or an explicitly qualified character reference.
            if (operation.Method == "GET" && (path == "/character/get" || path == "/character/list" ||
                path == "/character/narrative/sections/list" || path == "/character/narrative/triggers/list"))
            {
                var scoped = new Dictionary<string, object?>();
                if (query != null)
                    foreach (var pair in query) scoped[pair.Key] = pair.Value;
                if (!scoped.ContainsKey("workspace_id") && _options.WorkspaceId != null)
                    scoped["workspace_id"] = _options.WorkspaceId;
                query = scoped;
            }
            var uriBuilder = new UriBuilder(new Uri(_options.BaseUri, path.TrimStart('/')));
            if (query != null && query.Count > 0)
            {
                uriBuilder.Query = string.Join("&", query
                    .Where(pair => pair.Value != null)
                    .SelectMany(ExpandQuery)
                    .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            }

            byte[]? payload = null;
            string? contentType = null;
            if (form != null)
            {
                payload = ConvaiApiRequest.Utf8(string.Join("&", form.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
                contentType = "application/x-www-form-urlencoded";
            }
            else if (body != null)
            {
                payload = ConvaiApiRequest.Utf8(JsonConvert.SerializeObject(body, JsonSettings));
                contentType = string.IsNullOrEmpty(operation.RequestMediaType)
                    ? "application/json"
                    : operation.RequestMediaType;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_options.CredentialKind == CharacterApiCredentialKind.ApiKey)
                headers["CONVAI-API-KEY"] = _options.Credential;
            else
                headers["Authorization"] = "Bearer " + _options.Credential;

            return new ConvaiApiRequest(
                uriBuilder.Uri,
                (ConvaiApiHttpMethod)Enum.Parse(typeof(ConvaiApiHttpMethod), operation.Method, true),
                headers,
                payload,
                contentType,
                string.IsNullOrEmpty(operation.ResponseMediaType) ? "application/json" : operation.ResponseMediaType,
                _options.Timeout);
        }

        private static IEnumerable<KeyValuePair<string, string>> ExpandQuery(KeyValuePair<string, object?> pair)
        {
            if (pair.Value is System.Collections.IEnumerable values && pair.Value is not string)
            {
                foreach (object? value in values)
                    yield return new KeyValuePair<string, string>(pair.Key, Invariant(value));
                yield break;
            }
            yield return new KeyValuePair<string, string>(pair.Key, Invariant(pair.Value));
        }

        private static string Escape(object? value) => Uri.EscapeDataString(Invariant(value));
        private static string Invariant(object? value) =>
            value is IFormattable formatted
                ? formatted.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty
                : value?.ToString() ?? string.Empty;

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConvaiCharacterApiClient));
        }

        public void Dispose()
        {
            ReadClient.Dispose();
            if (_disposed) return;
            _disposed = true;
            if (_ownsTransport) _transport.Dispose();
        }
    }

    public sealed class CharacterApiEmptyResponse
    {
    }

    public class CharacterApiResourceClient
    {
        private readonly ConvaiCharacterApiClient _client;

        internal CharacterApiResourceClient(ConvaiCharacterApiClient client, string tag)
        {
            _client = client;
            Tag = tag;
            Operations = CharacterApiOperations.All
                .Where(operation => operation.Classification == CharacterApiOperationClass.PublicStable &&
                                    string.Equals(operation.Tag, tag, StringComparison.Ordinal))
                .ToArray();
        }

        public string Tag { get; }
        public IReadOnlyList<CharacterApiOperation> Operations { get; }

        public Task<TResponse> ExecuteAsync<TResponse>(
            CharacterApiOperation operation,
            IReadOnlyDictionary<string, object?>? routeValues = null,
            IReadOnlyDictionary<string, object?>? query = null,
            object? body = null,
            IReadOnlyDictionary<string, string>? form = null,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(operation.Tag, Tag, StringComparison.Ordinal))
                throw new ConvaiApiException(
                    $"Operation {operation.OperationId} does not belong to the {Tag} resource.",
                    ConvaiApiErrorCategory.Contract);
            return _client.ExecuteAsync<TResponse>(
                operation, routeValues, query, body, form, cancellationToken);
        }

        protected ConvaiCharacterApiClient Client => _client;
    }
}
