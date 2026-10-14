# Convai REST modules for Unity

Convai REST support is split by backend ownership. `ConvaiRestClient` remains the SDK 4.x compatibility façade; new Character authoring code should use the typed Character API client.

## Module ownership

| Assembly | Responsibility | Player dependency |
|---|---|---|
| `Convai.Api.Transport` | Shared HTTP/UnityWebRequest wire contracts, errors, cancellation, JSON, form, binary, and SSE | When referenced |
| `Convai.CharacterApi.Contracts` | Checked-in generated Newtonsoft DTOs and operation metadata | No Unity dependency |
| `Convai.CharacterApi.Runtime` | Minimal read-only narrative operations needed at runtime | Default runtime surface |
| `Convai.CharacterApi.Authoring` | Full classified Character authoring API | Opt-in asmdef reference |
| `Convai.CoreApi` | Hand-maintained realtime `/connect` client | Runtime |
| `Convai.RestAPI` | SDK 4.x source-compatible adapters plus platform/account legacy routes | Compatibility only |

`/connect` is core-service-owned and is never generated from the Character contract. End-user, memory, usage, and account routes remain compatibility APIs until their owning services publish authoritative contracts.

## Generated Character coverage

The pinned Character OpenAPI currently contains 109 operations: 102 `public-stable`, 5 `compatibility`, and 2 `internal`. The generated operation catalog contains every classified operation. See [REST_API_PARITY.md](../../Documentation~/REST_API_PARITY.md) for verb/path parity against the 33 legacy fixed POST routes.

Regenerate and verify from the repository root:

```sh
python3 tools/rest-api-client/generate.py
python3 tools/rest-api-client/generate.py --check
```

Generation is deterministic and pinned by contract digest, generator CLI version, and checked-in operation policy. Generated DTOs intentionally do not ship OpenAPI Generator's stock HTTP client or runtime self-validation layer.

## Character authoring client

Reference `Convai.CharacterApi.Authoring` explicitly, then choose production, staging, or a reviewed custom endpoint.

```csharp
using Convai.CharacterApi;
using Convai.CharacterApi.Contracts.Model;

using var client = new ConvaiCharacterApiClient(
    ConvaiCharacterApiOptions.Production(characterApiKey));

CharacterResponse character =
    await client.Characters.GetAsync(characterId, cancellationToken);
```

For a personal access token, select the credential kind explicitly:

```csharp
var options = ConvaiCharacterApiOptions.Custom(
    new Uri(reviewedCharacterBaseUri),
    personalAccessToken,
    CharacterApiCredentialKind.PersonalAccessToken);
```

Mutations never retry or fall back between Character v2 and legacy hosts.

## Runtime Character reads

Runtime systems should reference `Convai.CharacterApi.Runtime`, not the full authoring assembly:

```csharp
using var narrative = new ConvaiCharacterNarrativeClient(
    new Uri(runtimeCharacterBaseUri),
    characterApiKey);
var sections = await narrative.ListSectionsAsync(characterId, cancellationToken);
```

The full authoring client is absent from the default player dependency graph.

## Core `/connect`

New core configuration owns the destination URI and uses a separate API-key or auth-token credential:

```csharp
using Convai.CoreApi;

ConvaiCoreApiOptions coreOptions = ConvaiCoreApiOptions.WithApiKey(
    new Uri(coreConnectUri),
    coreApiKey);
```

SDK 4.x callers can keep `ConvaiRestClient`, but should move the URL into client options and use the typed connection mode:

```csharp
using Convai.RestAPI;
using Convai.RestAPI.Services;

var options = new ConvaiRestClientOptions(coreApiKey)
{
    CoreBaseUrl = coreConnectUri
};

using var client = new ConvaiRestClient(options);
RoomDetails room = await client.Rooms.ConnectAsync(
    new RoomConnectionRequest
    {
        CharacterId = characterId,
        ConnectionMode = RoomConnectionType.Audio,
        EndUserId = stableAccountId
    },
    cancellationToken);
```

`RoomConnectionRequest.CoreServiceUrl` remains an obsolete 4.x adapter input and is not serialized as `core_service_url`. `connection_type` accepts only `audio` or `video`.

For multi-character joins, the request topology is resolved by `room_session_id` or `shared_session_key`; it does not resend a character roster. A shared session key requires an end-user ID so memory cannot be entered without identity scope.

## SDK 4.x compatibility

The SDK project factory uses Character api2 for the migrated reads by default. **Project Settings >
Convai SDK > Credentials > API Mode** provides a project-wide rollback to legacy routing. Direct
callers opt in by assigning `CharacterApiV2`:

```csharp
var options = new ConvaiRestClientOptions(legacyApiKey)
{
    CharacterApiV2 = ConvaiCharacterApiOptions.Production(characterApiKey)
};

using var client = new ConvaiRestClient(options);
CharacterDetails character =
    await client.Characters.GetDetailsAsync(characterId, cancellationToken);
```

Obsolete messages point to exact typed replacements. Public compatibility services, builders, models, and methods remain available throughout SDK 4.x. See [rest-api-migration-4x.md](../../Documentation~/rest-api-migration-4x.md) for mappings and the 5.0 removal list.

## Safety and release status

- End-user identity and metadata are frozen for one logical connection attempt and its retries.
- Resume keys are scoped by character, end user, and room/topology context without storing raw identifiers in the key.
- Credentials, room tokens, URLs, raw metadata, and direct identifiers must not be logged.
- End-user deactivation is not memory deletion; deletion is a separate irreversible operation.
- Staging qualification and production validation remain separate release checks.

See [REST_API_ARCHITECTURE.md](../../Documentation~/REST_API_ARCHITECTURE.md) for ownership boundaries and [rest-api-migration-4x.md](../../Documentation~/rest-api-migration-4x.md) for production-promotion gates.
