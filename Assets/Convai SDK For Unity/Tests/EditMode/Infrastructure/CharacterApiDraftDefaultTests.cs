#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Api.Transport;
using Convai.CharacterApi;
using Convai.CharacterApi.Contracts.Model;
using Convai.CharacterApi.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public sealed class CharacterApiDraftDefaultTests
    {
        private const string CharacterId = "00000000-0000-4000-8000-000000000001";

        [TestCase(null, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void CharacterResponse_UsesSchemaDefaultOnlyWhenEditAccessIsOmitted(
            bool? editable, bool expectedAccess)
        {
            CharacterResponse? result = JsonConvert.DeserializeObject<CharacterResponse>(
                CharacterJson(editable, "Published"));

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.EditCharacterAccess, Is.EqualTo(expectedAccess));
        }

        [Test]
        public async Task DraftReads_HonorMissingAndExplicitEditAccess(
            [Values(null, true, false)] bool? editable,
            [Values("character/get", "character/narrative/sections/list", "character/narrative/triggers/list")]
            string path)
        {
            var transport = new DraftReadTransport(editable);
            using var client = new ConvaiCharacterNarrativeClient(
                ConvaiCharacterApiOptions.Production("key").WithReadContext("workspace", useDraft: true),
                transport);
            bool expectedDraft = editable != false;

            switch (path)
            {
                case "character/get":
                    CharacterResponse character = await client.GetCharacterAsync(CharacterId);
                    Assert.That(character.CharacterName, Is.EqualTo(expectedDraft ? "Draft" : "Published"));
                    break;
                case "character/narrative/sections/list":
                    await client.ListSectionsAsync(CharacterId);
                    break;
                case "character/narrative/triggers/list":
                    await client.ListTriggersAsync(CharacterId);
                    break;
                default:
                    throw new AssertionException("Unexpected read path: " + path);
            }

            Assert.That(transport.Requests.Count, Is.EqualTo(path == "character/get" && !expectedDraft ? 1 : 2));
            Assert.That(transport.Requests[0].Uri.AbsolutePath, Is.EqualTo("/character/get"));
            Assert.That(transport.Requests[0].Uri.Query,
                Is.EqualTo("?character_id=" + CharacterId + "&workspace_id=workspace"));
            ConvaiApiRequest selected = transport.Requests[transport.Requests.Count - 1];
            Assert.That(selected.Uri.AbsolutePath, Is.EqualTo("/" + path));
            Assert.That(selected.Uri.Query,
                Is.EqualTo("?character_id=" + CharacterId + (expectedDraft ? "-draft" : "") +
                           "&workspace_id=workspace"));
        }

        private static string CharacterJson(bool? editable, string name)
        {
            var json = JObject.Parse(@"{""user_id"":""user"",""character_id"":""00000000-0000-4000-8000-000000000001"",""character_name"":""Test"",""voice_type"":""test"",""language_code"":""en"",""description"":"""",""model_type"":"""",""is_narrative_driven"":false,""timestamp"":""2026-01-01T00:00:00Z"",""memory_settings"":{},""character_traits"":{},""metadata_filter"":{},""resolved_revision_id"":""revision""}");
            json["character_name"] = name;
            if (editable.HasValue)
                json["edit_character_access"] = editable.Value;
            return json.ToString();
        }

        private sealed class DraftReadTransport : IConvaiApiTransport
        {
            private readonly bool? _editable;

            public DraftReadTransport(bool? editable) => _editable = editable;

            public List<ConvaiApiRequest> Requests { get; } = new();
            public ConvaiApiTransportCapabilities Capabilities { get; } = new(false);

            public Task<ConvaiApiResponse> SendAsync(
                ConvaiApiRequest request, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request);
                string response = request.Uri.AbsolutePath == "/character/get"
                    ? CharacterJson(_editable, request.Uri.Query.Contains("-draft") ? "Draft" : "Published")
                    : "[]";
                return Task.FromResult(new ConvaiApiResponse(
                    request.Uri, 200, ConvaiApiRequest.Utf8(response), "application/json", null));
            }

            public Task SendSseAsync(ConvaiApiRequest request, Action<ConvaiApiSseEvent> onEvent,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public void Dispose() { }
        }
    }
}
