using System;
using Convai.Editor.Diagnostics;
using Convai.Infrastructure.Networking;
using Convai.RestAPI.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    public sealed class ConvaiConnectResponseReportTests
    {
        private const string ResponseJson = @"{
            ""token"": ""eyJhbGciOiJIUzI1NiJ9.secret"",
            ""room_name"": ""convai-room-1"",
            ""session_id"": ""sess-1"",
            ""room_url"": ""wss://example.livekit.cloud"",
            ""character_session_id"": ""char-sess-1"",
            ""request_trace_id"": ""trace-1"",
            ""room_session_id"": ""room-sess-1"",
            ""future_field"": { ""nested"": 42 }
        }";

        [TearDown]
        public void TearDown() => ConvaiConnectResponseLog.Clear();

        [Test]
        public void Build_RedactsTokenButKeepsItsLength()
        {
            JObject report = BuildReport();

            Assert.That(
                (string)report["response"]["token"],
                Is.EqualTo("<redacted, 27 chars>"));
            StringAssert.DoesNotContain("secret", report.ToString());
        }

        [Test]
        public void Build_KeepsEveryResponseFieldIncludingUnknownOnes()
        {
            JToken response = BuildReport()["response"];

            Assert.That((string)response["room_name"], Is.EqualTo("convai-room-1"));
            Assert.That((string)response["room_url"], Is.EqualTo("wss://example.livekit.cloud"));
            Assert.That((string)response["character_session_id"], Is.EqualTo("char-sess-1"));
            Assert.That((string)response["request_trace_id"], Is.EqualTo("trace-1"));
            Assert.That((string)response["room_session_id"], Is.EqualTo("room-sess-1"));
            Assert.That((int)response["future_field"]["nested"], Is.EqualTo(42));
        }

        [Test]
        public void Build_DescribesTheClientAndCharacter()
        {
            JToken client = BuildReport()["client"];

            Assert.That((string)client["sdk_version"], Is.Not.Empty);
            Assert.That((string)client["unity_version"], Is.Not.Empty);
            Assert.That((string)client["received_at_utc"], Does.StartWith("2026-09-25T10:15:00"));
            Assert.That((string)client["character"]["id"], Is.EqualTo("char-id"));
            Assert.That((string)client["character"]["name"], Is.EqualTo("Nova"));
            Assert.That((string)client["character"]["reference"], Is.EqualTo("char-id"));
        }

        [Test]
        public void Build_OmitsTheCharacterWhenCopiedFromTheRoom()
        {
            var snapshot = new ConvaiConnectResponseSnapshot(Deserialize(), DateTime.UtcNow);
            JObject report = Parse(ConvaiConnectResponseReport.Build(snapshot));

            Assert.That(report["client"]["character"], Is.Null);
            Assert.That((string)report["response"]["room_name"], Is.EqualTo("convai-room-1"));
        }

        [Test]
        public void Build_ReturnsNullWithoutAResponse() =>
            Assert.That(ConvaiConnectResponseReport.Build(null, "id", "name", "Live"), Is.Null);

        [Test]
        public void Log_RecordsClearsAndIgnoresAMissingResponse()
        {
            ConvaiConnectResponseLog.Record(Deserialize());
            Assert.That(ConvaiConnectResponseLog.Latest, Is.Not.Null);

            ConvaiConnectResponseLog.Clear();
            Assert.That(ConvaiConnectResponseLog.Latest, Is.Null);

            ConvaiConnectResponseLog.Record(null);
            Assert.That(ConvaiConnectResponseLog.Latest, Is.Null);
        }

        private static JObject BuildReport()
        {
            var snapshot = new ConvaiConnectResponseSnapshot(
                Deserialize(), new DateTime(2026, 9, 25, 10, 15, 0, DateTimeKind.Utc));
            return Parse(ConvaiConnectResponseReport.Build(snapshot, "char-id", "Nova", "char-id"));
        }

        /// <summary>Parses without turning ISO timestamps into DateTime, so they read back exactly as written.</summary>
        private static JObject Parse(string json) =>
            JsonConvert.DeserializeObject<JObject>(
                json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });

        private static RoomDetails Deserialize() => JsonConvert.DeserializeObject<RoomDetails>(ResponseJson);
    }
}
