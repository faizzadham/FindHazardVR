using System;
using Convai.Infrastructure.Networking.Models;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class ReconnectionSystemTests
    {
        [Test]
        public void ConnectionContext_TracksImmutableDisconnectionSnapshot()
        {
            DateTime connected = DateTime.UtcNow.AddMinutes(-5);
            DateTime disconnected = DateTime.UtcNow;
            var original = new ConnectionContext("room", "character-session", "session", "character", connected);

            ConnectionContext snapshot = original.WithDisconnection(disconnected);

            Assert.That(snapshot.RoomName, Is.EqualTo("room"));
            Assert.That(snapshot.CharacterSessionId, Is.EqualTo("character-session"));
            Assert.That(snapshot.SessionId, Is.EqualTo("session"));
            Assert.That(snapshot.CharacterId, Is.EqualTo("character"));
            Assert.That(snapshot.ConnectedAtUtc, Is.EqualTo(connected));
            Assert.That(snapshot.DisconnectedAtUtc, Is.EqualTo(disconnected));
            Assert.That(original.DisconnectedAtUtc, Is.Null);
        }

        [TestCase("room", true, 30, 60, true)]
        [TestCase("room", true, 120, 60, false)]
        [TestCase("room", false, 0, 60, false)]
        [TestCase(null, true, 30, 60, false)]
        public void ConnectionContext_RejoinValidityUsesRoomTimestampAndTtl(
            string room,
            bool hasDisconnectedAt,
            int ageSeconds,
            int ttlSeconds,
            bool expected)
        {
            DateTime? disconnected = hasDisconnectedAt ? DateTime.UtcNow.AddSeconds(-ageSeconds) : null;
            var context = new ConnectionContext(room, "character-session", "session", "character",
                DateTime.UtcNow.AddMinutes(-5), disconnected);

            Assert.That(context.IsRoomValidForRejoin(ttlSeconds), Is.EqualTo(expected));
        }

        [Test]
        public void ReconnectPolicy_PresetsCarryOperationalDefaults()
        {
            ReconnectPolicy defaults = ReconnectPolicy.Default;
            Assert.That(defaults.RoomRejoinTtlSeconds, Is.EqualTo(60));
            Assert.That(defaults.ResumePolicy, Is.EqualTo(ResumePolicy.ResumeIfPossible));
            Assert.That(defaults.MaxReconnectAttempts, Is.EqualTo(3));
            Assert.That(defaults.SpawnAgentOnRejoin, Is.True);
            Assert.That(defaults.StartWaitTimeoutMs, Is.EqualTo(5000));
            Assert.That(defaults.AutoMicStartDelaySeconds, Is.EqualTo(0.5f));

            ReconnectPolicy fresh = ReconnectPolicy.AlwaysCreateNew;
            Assert.That(fresh.RoomRejoinTtlSeconds, Is.Zero);
            Assert.That(fresh.ResumePolicy, Is.EqualTo(ResumePolicy.AlwaysFresh));
        }

        [TestCase(30, ResumePolicy.ResumeIfPossible, true, true, "room", "character-session")]
        [TestCase(120, ResumePolicy.ResumeIfPossible, true, false, null, "character-session")]
        [TestCase(30, ResumePolicy.AlwaysFresh, true, true, "room", null)]
        [TestCase(30, ResumePolicy.AlwaysResume, false, true, "room", "character-session")]
        public void RoomJoinOptions_FromContextAppliesTtlResumeAndSpawnPolicy(
            int disconnectedAgeSeconds,
            ResumePolicy resumePolicy,
            bool spawnAgent,
            bool expectedJoin,
            string expectedRoom,
            string expectedSession)
        {
            var context = new ConnectionContext(
                "room",
                "character-session",
                "session",
                "character",
                DateTime.UtcNow.AddMinutes(-5),
                DateTime.UtcNow.AddSeconds(-disconnectedAgeSeconds));
            var policy = new ReconnectPolicy(resumePolicy: resumePolicy, spawnAgentOnRejoin: spawnAgent);

            RoomJoinOptions options = RoomJoinOptions.FromContext(context, policy);

            Assert.That(options.IsJoinRequest, Is.EqualTo(expectedJoin));
            Assert.That(options.RoomName, Is.EqualTo(expectedRoom));
            Assert.That(options.CharacterSessionId, Is.EqualTo(expectedSession));
            Assert.That(options.SpawnAgent, Is.EqualTo(expectedJoin ? spawnAgent : true));
        }

        [Test]
        public void RoomJoinOptions_NullContextCreatesFreshRoom()
        {
            RoomJoinOptions options = RoomJoinOptions.FromContext(null, ReconnectPolicy.Default);
            Assert.That(options.IsJoinRequest, Is.False);
            Assert.That(options.CharacterSessionId, Is.Null);
        }
    }
}
