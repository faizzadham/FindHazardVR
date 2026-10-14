using System.Collections.Generic;
using System.Linq;
using Convai.RestAPI;
using Convai.RestAPI.Internal;
using Convai.RestAPI.Services;
using Convai.Infrastructure.Networking.Models;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Room;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public sealed class MultiCharacterConnectContractTests
    {
        [Test]
        public void RosterCreateSerializesOrderedCharactersWithoutLegacyTopology()
        {
            var request = BaseRequest();
            request.CharacterId = null;
            request.Mode = "create";
            request.SpawnAgent = true;
            request.EndUserId = "player-1";
            request.Characters = new List<RoomConnectionCharacterRequest>
            {
                new() { CharacterId = "character-1", CharacterSessionId = "session-1" },
                new() { CharacterId = "character-2", CharacterSessionId = "session-2" }
            };

            JObject json = JObject.Parse(RoomConnectionRequestTransportSerializer.SerializeForTransport(
                request,
                new ConvaiRestClientOptions("test-api-key")));

            Assert.That(json["character_id"], Is.Null);
            Assert.That(json["character_session_id"], Is.Null);
            Assert.That(json["characters"]?.Count(), Is.EqualTo(2));
            Assert.That(json["characters"]?[0]?["character_id"]?.Value<string>(), Is.EqualTo("character-1"));
            Assert.That(json["characters"]?[1]?["character_id"]?.Value<string>(), Is.EqualTo("character-2"));
            Assert.That(json["spawn_agent"]?.Value<bool>(), Is.True);
            Assert.That(json["initial_character_id"], Is.Null);
            Assert.That(json["active_character_id"], Is.Null);
            Assert.That(json["core_service_url"], Is.Null);
        }

        [Test]
        public void ExistingRoomJoinSerializesLocatorWithoutRoster()
        {
            var request = BaseRequest();
            request.CharacterId = null;
            request.CharacterSessionId = null;
            request.Mode = "join";
            request.SpawnAgent = null;
            request.EndUserId = "player-2";
            request.RoomSessionId = "room-session-1";

            JObject json = JObject.Parse(RoomConnectionRequestTransportSerializer.SerializeForTransport(
                request,
                new ConvaiRestClientOptions("test-api-key")));

            Assert.That(json["room_session_id"]?.Value<string>(), Is.EqualTo("room-session-1"));
            Assert.That(json["mode"]?.Value<string>(), Is.EqualTo("join"));
            Assert.That(json["character_id"], Is.Null);
            Assert.That(json["characters"], Is.Null);
            Assert.That(json["spawn_agent"], Is.Null);
        }

        [Test]
        public void ExistingRoomJoinRejectsRosterTopology()
        {
            var request = BaseRequest();
            request.CharacterId = null;
            request.Characters = new List<RoomConnectionCharacterRequest>
            {
                new() { CharacterId = "character-1" }
            };
            request.Mode = "join";
            request.EndUserId = "player-2";
            request.RoomSessionId = "room-session-1";

            Assert.Throws<ConvaiRestException>(() =>
                RoomConnectionRequestTransportSerializer.SerializeForTransport(
                    request,
                    new ConvaiRestClientOptions("test-api-key")));
        }

        [Test]
        public void LegacyConnectResponseAcceptsNullRoomEpochs()
        {
            const string response = @"{
                'session_id':'session-1',
                'character_session_id':'character-session-1',
                'room_url':'wss://room',
                'room_name':'room-1',
                'token':'token-1',
                'route_epoch':null,
                'roster_epoch':null
            }";

            RoomDetails details = JsonConvert.DeserializeObject<RoomDetails>(response);
            var session = new MultiCharacterRoomSession(details, null);

            Assert.That(details, Is.Not.Null);
            Assert.That(details!.RouteEpoch, Is.Null);
            Assert.That(details.RosterEpoch, Is.Null);
            Assert.That(session.RouteEpoch, Is.Zero);
            Assert.That(session.RosterEpoch, Is.Zero);
        }

        [Test]
        public void TypedConnectionModeSerializesOnlySupportedWireValue()
        {
            var request = BaseRequest();
            request.ConnectionType = string.Empty;
            request.ConnectionMode = RoomConnectionType.Video;

            JObject json = JObject.Parse(RoomConnectionRequestTransportSerializer.SerializeForTransport(
                request,
                new ConvaiRestClientOptions("test-api-key")));

            Assert.That(json["connection_type"]?.Value<string>(), Is.EqualTo("video"));
        }

        [Test]
        public void InvalidConnectionModeIsRejected()
        {
            var request = BaseRequest();
            request.ConnectionType = "text";

            Assert.Throws<ConvaiRestException>(() =>
                RoomConnectionRequestTransportSerializer.SerializeForTransport(
                    request,
                    new ConvaiRestClientOptions("test-api-key")));
        }

        [Test]
        public void SharedRoomRequiresEndUserForMemoryIsolation()
        {
            var request = BaseRequest();
            request.SharedSessionKey = "shared-room";

            Assert.Throws<ConvaiRestException>(() =>
                RoomConnectionRequestTransportSerializer.SerializeForTransport(
                    request,
                    new ConvaiRestClientOptions("test-api-key")));
        }

        [Test]
        public void ConnectResponsePreservesUnknownProperties()
        {
            RoomDetails details = JsonConvert.DeserializeObject<RoomDetails>(
                "{\"token\":\"t\",\"room_name\":\"r\",\"session_id\":\"s\"," +
                "\"room_url\":\"wss://room\",\"future_status\":\"ready\"}");

            Assert.That(details.AdditionalData["future_status"]?.Value<string>(), Is.EqualTo("ready"));
        }

        [Test]
        public void SessionResumeKeysAreIsolatedAndDoNotExposeIdentifiers()
        {
            RoomJoinOptions firstRoom = RoomJoinOptions.CreateMultiCharacterJoin("room-1", null);
            RoomJoinOptions secondRoom = RoomJoinOptions.CreateMultiCharacterJoin("room-2", null);

            string first = SessionResumeScope.CreatePersistenceKey("character-1", "user-1", firstRoom);
            string otherUser = SessionResumeScope.CreatePersistenceKey("character-1", "user-2", firstRoom);
            string otherRoom = SessionResumeScope.CreatePersistenceKey("character-1", "user-1", secondRoom);

            Assert.That(first, Is.Not.EqualTo(otherUser));
            Assert.That(first, Is.Not.EqualTo(otherRoom));
            Assert.That(first, Does.Not.Contain("character-1").And.Not.Contain("user-1").And.Not.Contain("room-1"));
        }

        private static RoomConnectionRequest BaseRequest() => new()
        {
            CharacterId = "legacy-character",
            CoreServiceUrl = "https://core.convai.com/connect",
            ConnectionType = "audio",
            Transport = "livekit"
        };
    }
}
