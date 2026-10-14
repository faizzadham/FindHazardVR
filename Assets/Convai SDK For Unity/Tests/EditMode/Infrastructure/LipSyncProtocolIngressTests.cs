using System;
using System.Collections.Generic;
using System.Text;
using Convai.Domain.DomainEvents.LipSync;
using Convai.Domain.EventSystem;
using Convai.Domain.Models.LipSync;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Protocol;
using Convai.RestAPI.Internal;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.Logging;
using Convai.Shared.Types;
using Newtonsoft.Json.Linq;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class LipSyncProtocolIngressTests
    {
        private readonly List<GameObject> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
                if (_createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);

            _createdObjects.Clear();
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator PackedData_WarmedIngestion_StaysWithinAllocationBudget()
        {
            var registry = new AgentRegistry();
            registry.RegisterCharacter(CreateCharacter("character-1"));
            registry.RegisterCharacter(CreateCharacter("character-2"));
            registry.Configure(CreateRoomDetails(), registry.Characters);
            var eventHub = new EventHub(new ImmediateScheduler());
            int delivered = 0;
            LipSyncPackedDataReceived last = default;
            System.Action<LipSyncPackedDataReceived> observer = value => { delivered++; last = value; };
            eventHub.Subscribe(observer);
            var logger = new SilentLogger { Enabled = false };
            var ingress = new LipSyncProtocolIngress(registry, eventHub, logger, CreateTransportOptions());
            var packet = new ProtocolPacket(CreatePackedDataPayload(), "character:membership-2", string.Empty, true);
            int accepted = 0;
            long bytes = 0;
            yield return AllocationMeasurement.Measure(_ => { if (ingress.TryHandlePacket(packet)) accepted++; }, value => bytes = value);

            int expected = AllocationMeasurement.WarmupCalls + 2 * AllocationMeasurement.MeasuredCalls;
            Assert.That(accepted, Is.EqualTo(expected));
            Assert.That(delivered, Is.EqualTo(expected));
            Assert.That(last.CharacterId, Is.EqualTo("character-2"));
            Assert.That(last.ParticipantId, Is.EqualTo("character:membership-2"));
            Assert.That(last.Chunk.ChannelNames[0], Is.EqualTo("jawOpen"));
            Assert.That(last.Chunk.Frames[0][0], Is.EqualTo(0.75f));
            System.GC.KeepAlive(observer);
            Assert.That(bytes, Is.LessThanOrEqualTo(524L * AllocationMeasurement.MeasuredCalls),
                "Warmed per-operation allocation must not exceed the measured baseline; tighten after optimizing this path.");
        }

        [Test]
        public void PackedData_MultiCharacterParticipantIdentity_RoutesToOwningCharacter()
        {
            var registry = new AgentRegistry();
            ConvaiCharacter first = CreateCharacter("character-1");
            ConvaiCharacter second = CreateCharacter("character-2");
            registry.RegisterCharacter(first);
            registry.RegisterCharacter(second);
            registry.Configure(CreateRoomDetails(), registry.Characters);

            var eventHub = new EventHub(new ImmediateScheduler());
            var received = new List<LipSyncPackedDataReceived>();
            eventHub.Subscribe<LipSyncPackedDataReceived>(received.Add);
            var ingress = new LipSyncProtocolIngress(
                registry,
                eventHub,
                new ConvaiLogger(),
                CreateTransportOptions());

            var packet = new ProtocolPacket(
                CreatePackedDataPayload(),
                "character:membership-2",
                string.Empty,
                true);

            Assert.IsTrue(ingress.TryHandlePacket(packet));
            Assert.That(received, Has.Count.EqualTo(1),
                "The participant identity from the room roster must not drop the second character's LipSync frames.");
            Assert.That(received[0].CharacterId, Is.EqualTo("character-2"));
            Assert.That(received[0].ParticipantId, Is.EqualTo("character:membership-2"));
        }

        private ConvaiCharacter CreateCharacter(string characterId)
        {
            var gameObject = new GameObject($"LipSyncProtocolIngressTests_{characterId}");
            _createdObjects.Add(gameObject);
            ConvaiCharacter character = gameObject.AddComponent<ConvaiCharacter>();
            character.Configure(characterId, characterId);
            return character;
        }

        private static RoomDetails CreateRoomDetails()
        {
            var characters = new List<RoomCharacterDetails>
            {
                CreateMembership("membership-1", "character-1", "character:membership-1", true),
                CreateMembership("membership-2", "character-2", "character:membership-2", false)
            };

            return new RoomDetails(
                "token",
                "room-name",
                "session-1",
                "wss://room",
                roomSessionId: "room-1",
                activeMembershipId: "membership-1",
                characters: characters);
        }

        private static RoomCharacterDetails CreateMembership(
            string membershipId,
            string characterId,
            string participantIdentity,
            bool isInitial)
        {
            return new JObject
            {
                ["membership_id"] = membershipId,
                ["character_id"] = characterId,
                ["character_session_id"] = $"session:{membershipId}",
                ["participant_identity"] = participantIdentity,
                ["is_initial"] = isInitial,
                ["provisioning_status"] = "dispatch_accepted"
            }.ToObject<RoomCharacterDetails>();
        }

        private static ReadOnlyMemory<byte> CreatePackedDataPayload()
        {
            const string json =
                "{\"type\":\"server-message\",\"payload\":{" +
                "\"type\":\"chunked-neurosync-blendshapes\"," +
                "\"format\":\"arkit\",\"blendshapes\":[[0.75]]}}";
            return Encoding.UTF8.GetBytes(json);
        }

        private static LipSyncTransportOptions CreateTransportOptions() =>
            new(
                true,
                "neurosync",
                LipSyncProfileId.ARKit,
                "arkit",
                new[] { "jawOpen" },
                true,
                10,
                60,
                LipSyncTransportOptions.DefaultFramesBufferDuration);

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
