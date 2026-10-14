using System;
using System.Collections.Generic;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using Convai.RestAPI.Internal;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class RoomDetailsStateApplierTests
    {
        [Test]
        public void Apply_MapsSharedRoomDetailsFields_WithoutMarkingHasRoomDetailsByDefault()
        {
            var target = new TestTarget();
            var logger = new TestLogger();
            var roomDetails = new RoomDetails(
                "token-a",
                "room-a",
                "session-a",
                "wss://room-a",
                "character-session-a",
                "speaker-a");

            AppliedRoomDetailsState state = RoomDetailsStateApplier.Apply(target, roomDetails, logger);

            Assert.That(state.Token, Is.EqualTo("token-a"));
            Assert.That(target.RoomName, Is.EqualTo("room-a"));
            Assert.That(target.RoomURL, Is.EqualTo("wss://room-a"));
            Assert.That(target.SessionID, Is.EqualTo("session-a"));
            Assert.That(target.CharacterSessionID, Is.EqualTo("character-session-a"));
            Assert.That(target.ResolvedSpeakerId, Is.EqualTo("speaker-a"));
            Assert.That(target.RequestTraceId, Is.Null.Or.Empty);
            Assert.That(target.ResolvedEndUserId, Is.Null.Or.Empty);
            Assert.That(target.ResolvedEndUserMetadata, Is.Null.Or.Empty);
            Assert.That(target.HasRoomDetails, Is.False);
            Assert.That(logger.DebugMessages, Has.Count.EqualTo(2));
            Assert.That(logger.DebugMessages[0], Does.Contain("Room details received:"));
            Assert.That(logger.DebugMessages[1], Does.Contain("Resolved Speaker ID (diagnostics): speaker-a"));
        }

        [Test]
        public void Apply_CanMarkHasRoomDetailsAndPrefixLogs_ForWebGLTiming()
        {
            var target = new TestTarget();
            var logger = new TestLogger();
            var roomDetails = new RoomDetails(
                "token-b",
                "room-b",
                "session-b",
                "wss://room-b",
                "character-session-b",
                "speaker-b");

            RoomDetailsStateApplier.Apply(
                target,
                roomDetails,
                logger,
                markHasRoomDetails: true,
                logPrefix: "[WebGLRoomController]");

            Assert.That(target.Token, Is.EqualTo("token-b"));
            Assert.That(target.HasRoomDetails, Is.True);
            Assert.That(logger.DebugMessages[0], Does.StartWith("[WebGLRoomController] Room details received:"));
            Assert.That(logger.DebugMessages[1], Does.StartWith("[WebGLRoomController] Token: token-b;"));
        }

        [Test]
        public void Apply_OmitsResolvedSpeakerIdFromSummary_WhenNotReturnedByBackend()
        {
            var target = new TestTarget();
            var logger = new TestLogger();
            var roomDetails = new RoomDetails(
                "token-c",
                "room-c",
                "session-c",
                "wss://room-c",
                "character-session-c");

            RoomDetailsStateApplier.Apply(target, roomDetails, logger);

            Assert.That(target.ResolvedSpeakerId, Is.Null.Or.Empty);
            Assert.That(logger.DebugMessages[1], Does.Not.Contain("Resolved Speaker ID"));
        }

        [Test]
        public void Apply_MapsTraceAndEndUserEcho_ForDiagnostics()
        {
            var target = new TestTarget();
            var logger = new TestLogger();
            var roomDetails = new RoomDetails(
                "token-d",
                "room-d",
                "session-d",
                "wss://room-d",
                "character-session-d",
                "speaker-d",
                requestTraceId: "trace-d",
                endUserId: "player-d",
                endUserMetadata: new Dictionary<string, object>
                {
                    ["name"] = "Player D"
                });

            AppliedRoomDetailsState state = RoomDetailsStateApplier.Apply(target, roomDetails, logger);

            Assert.That(state.RequestTraceId, Is.EqualTo("trace-d"));
            Assert.That(state.ResolvedEndUserId, Is.EqualTo("player-d"));
            Assert.That(state.ResolvedEndUserMetadata?["name"], Is.EqualTo("Player D"));
            Assert.That(target.RequestTraceId, Is.EqualTo("trace-d"));
            Assert.That(target.ResolvedEndUserId, Is.EqualTo("player-d"));
            Assert.That(target.ResolvedEndUserMetadata?["name"], Is.EqualTo("Player D"));
            Assert.That(logger.DebugMessages[1], Does.Contain("Request Trace ID: trace-d"));
            Assert.That(logger.DebugMessages[1], Does.Contain("Resolved End User ID: player-d"));
        }

        [Test]
        public void CreateState_DoesNotSerializeRoomDetails()
        {
            var probe = new SerializationProbe();

            AppliedRoomDetailsState state = RoomDetailsStateApplier.CreateState(CreateRoomDetails(probe));

            Assert.That(probe.Reads, Is.Zero);
            Assert.That(state.Token, Is.EqualTo("token-e"));
        }

        [Test]
        public void Apply_BuildsNoDiagnostics_WhenTransportDebugIsFiltered()
        {
            var target = new TestTarget();
            var logger = new TestLogger { DebugEnabled = false };
            var probe = new SerializationProbe();

            AppliedRoomDetailsState state = RoomDetailsStateApplier.Apply(
                target,
                CreateRoomDetails(probe),
                logger,
                markHasRoomDetails: true);

            Assert.That(probe.Reads, Is.Zero);
            Assert.That(logger.DebugMessages, Is.Empty);
            Assert.That(state.Token, Is.EqualTo("token-e"));
            Assert.That(target.Token, Is.EqualTo("token-e"));
            Assert.That(target.RoomName, Is.EqualTo("room-e"));
            Assert.That(target.ResolvedEndUserMetadata?["probe"], Is.SameAs(probe));
            Assert.That(target.HasRoomDetails, Is.True);
        }

        [Test]
        public void Apply_SerializesRoomDetailsOnce_WhenTransportDebugIsEnabled()
        {
            var target = new TestTarget();
            var logger = new TestLogger();
            var probe = new SerializationProbe();

            RoomDetailsStateApplier.Apply(target, CreateRoomDetails(probe), logger);

            Assert.That(probe.Reads, Is.EqualTo(1));
            Assert.That(logger.DebugMessages, Has.Count.EqualTo(2));
            Assert.That(logger.DebugMessages[0], Does.StartWith("Room details received: {"));
            Assert.That(logger.DebugMessages[0], Does.Contain("\"room_name\":\"room-e\""));
            Assert.That(logger.DebugMessages[1], Does.StartWith("Token: token-e;"));
        }

        private static RoomDetails CreateRoomDetails(SerializationProbe probe) => new(
            "token-e",
            "room-e",
            "session-e",
            "wss://room-e",
            "character-session-e",
            endUserMetadata: new Dictionary<string, object>
            {
                ["probe"] = probe
            });

        /// <summary>
        ///     Counts how often the room details were serialized: its only property is read by the JSON
        ///     serializer and by nothing else.
        /// </summary>
        private sealed class SerializationProbe
        {
            public int Reads { get; private set; }

            public string Value
            {
                get
                {
                    Reads++;
                    return "probe";
                }
            }
        }

        private sealed class TestTarget : IRoomDetailsStateTarget
        {
            public string Token { get; set; }
            public string RoomName { get; set; }
            public string RoomURL { get; set; }
            public string SessionID { get; set; }
            public string CharacterSessionID { get; set; }
            public string ResolvedSpeakerId { get; set; }
            public string RequestTraceId { get; set; }
            public string ResolvedEndUserId { get; set; }
            public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata { get; set; }
            public bool HasRoomDetails { get; set; }
        }

        private sealed class TestLogger : ILogger
        {
            public void Log(in Convai.Domain.Logging.LogEntry entry)
            {
                if (entry.HasException) { Error(entry.Exception, entry.Message, entry.Context, entry.Category); return; }
                switch (entry.Level)
                {
                    case Convai.Domain.Logging.LogLevel.Debug:
                    case Convai.Domain.Logging.LogLevel.Trace: Debug(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Info: Info(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Warning: Warning(entry.Message, entry.Context, entry.Category); break;
                    case Convai.Domain.Logging.LogLevel.Error: Error(entry.Message, entry.Context, entry.Category); break;
                }
            }
            public List<string> DebugMessages { get; } = new();
            public bool DebugEnabled { get; set; } = true;

            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }

            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Debug(string message, LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);

            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => Debug(message, category);

            public void Info(string message, LogCategory category = LogCategory.SDK) { }

            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Warning(string message, LogCategory category = LogCategory.SDK) { }

            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(string message, LogCategory category = LogCategory.SDK) { }

            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) { }

            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK)
            {
            }

            public bool IsEnabled(LogLevel level, LogCategory category) =>
                DebugEnabled || level != LogLevel.Debug || category != LogCategory.Transport;
        }
    }
}
