#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi;
using Convai.CharacterApi.Contracts;
using Convai.Runtime;
using Newtonsoft.Json.Linq;

namespace Convai.Editor.Inspectors
{
    /// <summary>One exact runtime reference returned by the availability API.</summary>
    internal sealed class CharacterReleasedVersion
    {
        internal CharacterReleasedVersion(string reference, string version, bool isLatest)
        {
            Reference = reference;
            Version = version;
            IsLatest = isLatest;
        }

        internal string Reference { get; }
        internal string Version { get; }
        internal bool IsLatest { get; }
    }

    /// <summary>Runtime-safe choices in API order, without mutable authoring metadata.</summary>
    internal sealed class CharacterVersionAvailability
    {
        internal CharacterVersionAvailability(string characterId, string currentReference,
            IReadOnlyList<CharacterReleasedVersion> versions)
        {
            CharacterId = characterId;
            CurrentReference = currentReference;
            Versions = new List<CharacterReleasedVersion>(versions).AsReadOnly();
        }

        internal string CharacterId { get; }
        internal string CurrentReference { get; }
        internal IReadOnlyList<CharacterReleasedVersion> Versions { get; }
    }

    /// <summary>Loads runtime-selectable references without changing the saved character selection.</summary>
    internal static class ConvaiCharacterVersionListClient
    {
        // This runtime-discovery endpoint was added after the bundled authoring contract snapshot.
        // Keep its editor-only descriptor here rather than editing generated source by hand.
        private static readonly CharacterApiOperation AvailableOperation = new(
            "character_version_available", "GET", "/character/versions/available",
            CharacterApiOperationClass.PublicStable, "character-versions", string.Empty,
            new[] { "application/json" }, new[] { "application/json" }, false);

        internal static async Task<CharacterVersionAvailability> FetchAsync(
            ConvaiCharacterApiOptions options,
            string characterId,
            CancellationToken cancellationToken = default,
            IConvaiApiTransport? transport = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!TryReadBareId(characterId, out Guid requestedId))
                throw new ArgumentException("Character ID must be a bare UUID without spaces or a version suffix.",
                    nameof(characterId));
            cancellationToken.ThrowIfCancellationRequested();

            // The server derives visibility from the target. Neither a workspace query nor an
            // authoring -draft/-latest selector belongs on this request.
            var query = new Dictionary<string, object?> { ["character_id"] = characterId };
            using var client = new ConvaiCharacterApiClient(options, transport);
            JObject response = await client.ExecuteAsync<JObject>(AvailableOperation,
                query: query, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string responseId = RequiredString(response, "character_id");
            if (!TryReadBareId(responseId, out Guid returnedId) || returnedId != requestedId ||
                response["current"] is not JObject current || response["versions"] is not JArray items)
                throw InvalidResponse();

            string currentReference = RequiredString(current, "reference");
            if (!TryReadBareId(currentReference, out Guid currentId) || currentId != requestedId)
                throw InvalidResponse();

            var versions = new List<CharacterReleasedVersion>();
            foreach (JToken token in items)
            {
                if (token is not JObject item || item["is_latest"]?.Type != JTokenType.Boolean)
                    throw InvalidResponse();
                string reference = RequiredString(item, "reference");
                string version = RequiredString(item, "version");
                if (IsAlias(version) || !ConvaiCharacterVersionReference.TryCreate(characterId,
                        ConvaiCharacterVersionMode.Specific, version, out _, out _, reference))
                    throw InvalidResponse();

                // References and version labels are server-owned strings. Never reconstruct,
                // normalize, numerically parse, reorder, or silently drop returned choices.
                versions.Add(new CharacterReleasedVersion(reference, version, item["is_latest"]!.Value<bool>()));
            }

            return new CharacterVersionAvailability(responseId, currentReference, versions);
        }

        private static bool TryReadBareId(string? value, out Guid id)
        {
            id = default;
            return value != null && value.Length == 36 && Guid.TryParseExact(value, "D", out id);
        }

        private static string RequiredString(JObject value, string name)
        {
            JToken? token = value[name];
            if (token?.Type != JTokenType.String) throw InvalidResponse();
            string text = token.Value<string>()!;
            if (string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal))
                throw InvalidResponse();
            return text;
        }

        private static bool IsAlias(string value) =>
            string.Equals(value, "draft", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "live", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "default", StringComparison.OrdinalIgnoreCase);

        private static ConvaiApiException InvalidResponse() => new(
            "Character version availability response did not contain valid runtime references for the requested character.",
            ConvaiApiErrorCategory.Serialization);
    }
}
