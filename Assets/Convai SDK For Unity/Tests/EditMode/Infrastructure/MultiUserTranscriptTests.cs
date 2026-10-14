using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Models;
using Convai.Runtime.Services.Transcript;
using NUnit.Framework;
using TransportPhase = Convai.Domain.Models.TranscriptionPhase;
using DomainPhase = Convai.Domain.Models.TranscriptionPhase;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    ///     Unit tests for multi-user transcript functionality.
    ///     Tests SpeakerInfo propagation through the transcript pipeline.
    /// </summary>
    public class MultiUserTranscriptTests
    {
        [Test]
        public void Adapter_Passes_SpeakerInfo_Through_ProcessedFinal()
        {
            var hub = new CapturingEventHub();
            using var adapter = new PlayerTranscriptAdapter(hub, "default-player", "You");

            var speakerInfo = new SpeakerInfo(
                "speaker-123",
                "John",
                "PA_xyz"
            );

            adapter.OnPlayerTranscriptionReceived("Hello", TransportPhase.ProcessedFinal, speakerInfo);

            Assert.AreEqual(1, hub.CapturedEvents.Count);
            Assert.AreEqual(DomainPhase.ProcessedFinal, hub.CapturedEvents[0].Phase);
            Assert.IsTrue(hub.CapturedEvents[0].HasSpeakerInfo);
            Assert.AreEqual("speaker-123", hub.CapturedEvents[0].SpeakerInfo.SpeakerId);
            Assert.AreEqual("John", hub.CapturedEvents[0].SpeakerInfo.SpeakerName);
            Assert.AreEqual("PA_xyz", hub.CapturedEvents[0].SpeakerInfo.ParticipantId);
        }

        /// <summary>
        ///     With no player name configured, a named speaker is shown by their own name rather
        ///     than by the local placeholder.
        /// </summary>
        /// <remarks>
        ///     "You" is the placeholder the adapter falls back to, not a name anyone chose, so it
        ///     yields to the backend's speaker directory. Without this, every human in a multi-user
        ///     room is labelled "You" — including the ones who are not you.
        ///     <see cref="Adapter_Prefers_ConfiguredPlayerName_Over_SpeakerName" /> pins the other
        ///     half of the rule.
        /// </remarks>
        [Test]
        public void Adapter_Uses_SpeakerInfo_For_Message_Fields()
        {
            var hub = new CapturingEventHub();
            using var adapter = new PlayerTranscriptAdapter(hub, "default-player", "You");

            var speakerInfo = new SpeakerInfo(
                "speaker-123",
                "John",
                "PA_xyz"
            );

            adapter.OnPlayerTranscriptionReceived("Hello", TransportPhase.ProcessedFinal, speakerInfo);

            Assert.AreEqual("speaker-123", hub.CapturedEvents[0].Message.PlayerOrCharacterId);
            Assert.AreEqual("John", hub.CapturedEvents[0].Message.DisplayName);
        }

        /// <summary>
        ///     A configured player name is authoritative: the backend's speaker name never
        ///     overwrites what the developer set on the player.
        /// </summary>
        [Test]
        public void Adapter_Prefers_ConfiguredPlayerName_Over_SpeakerName()
        {
            var hub = new CapturingEventHub();
            using var adapter = new PlayerTranscriptAdapter(hub, "default-player", "Kaan");

            var speakerInfo = new SpeakerInfo("speaker-123", "John", "PA_xyz");

            adapter.OnPlayerTranscriptionReceived("Hello", TransportPhase.ProcessedFinal, speakerInfo);

            Assert.AreEqual("Kaan", hub.CapturedEvents[0].Message.DisplayName);
            Assert.AreEqual("speaker-123", hub.CapturedEvents[0].Message.PlayerOrCharacterId,
                "Identity still follows the speaker directory; only the shown name is the developer's.");
        }

        [Test]
        public void Adapter_Falls_Back_To_Default_Without_SpeakerInfo()
        {
            var hub = new CapturingEventHub();
            using var adapter = new PlayerTranscriptAdapter(hub, "default-player", "DefaultName");

            adapter.OnPlayerTranscriptionReceived("Hello", TransportPhase.ProcessedFinal, SpeakerInfo.Empty);

            Assert.AreEqual(1, hub.CapturedEvents.Count);
            Assert.AreEqual("default-player", hub.CapturedEvents[0].Message.PlayerOrCharacterId);
            Assert.AreEqual("DefaultName", hub.CapturedEvents[0].Message.DisplayName);
            Assert.IsFalse(hub.CapturedEvents[0].HasSpeakerInfo);
        }

        [Test]
        public void Adapter_Caches_SpeakerInfo_For_Completed_Phase()
        {
            var hub = new CapturingEventHub();
            using var adapter = new PlayerTranscriptAdapter(hub, "default-player", "You");

            var speakerInfo = new SpeakerInfo("speaker-123", "John", "PA_xyz");

            adapter.OnPlayerTranscriptionReceived("Hello", TransportPhase.ProcessedFinal, speakerInfo);

            adapter.OnPlayerTranscriptionReceived("", TransportPhase.Completed, SpeakerInfo.Empty);

            Assert.AreEqual(2, hub.CapturedEvents.Count);

            Assert.AreEqual("speaker-123", hub.CapturedEvents[0].Message.PlayerOrCharacterId);
            Assert.AreEqual("speaker-123", hub.CapturedEvents[1].Message.PlayerOrCharacterId);
        }

        [Test]
        public void Adapter_Resets_SpeakerInfo_After_Completed()
        {
            var hub = new CapturingEventHub();
            using var adapter = new PlayerTranscriptAdapter(hub, "default-player", "You");

            var speakerInfo = new SpeakerInfo("speaker-123", "John", "PA_xyz");

            adapter.OnPlayerTranscriptionReceived("Hello", TransportPhase.ProcessedFinal, speakerInfo);
            adapter.OnPlayerTranscriptionReceived("", TransportPhase.Completed, SpeakerInfo.Empty);

            adapter.OnPlayerTranscriptionReceived("Hi", TransportPhase.ProcessedFinal, SpeakerInfo.Empty);

            Assert.AreEqual(3, hub.CapturedEvents.Count);

            Assert.AreEqual("speaker-123", hub.CapturedEvents[0].Message.PlayerOrCharacterId);
            Assert.AreEqual("speaker-123", hub.CapturedEvents[1].Message.PlayerOrCharacterId);

            Assert.AreEqual("default-player", hub.CapturedEvents[2].Message.PlayerOrCharacterId);
        }

        [Test]
        public void PlayerTranscriptReceived_HasSpeakerInfo_Property()
        {
            var speakerInfo = new SpeakerInfo("speaker-123", "John", "PA_xyz");

            var evt = PlayerTranscriptReceived.Create(
                "speaker-123",
                "John",
                "Hello",
                true,
                DomainPhase.ProcessedFinal,
                speakerInfo: speakerInfo
            );

            Assert.IsTrue(evt.HasSpeakerInfo);
            Assert.AreEqual("speaker-123", evt.SpeakerInfo.SpeakerId);
            Assert.AreEqual("John", evt.SpeakerInfo.SpeakerName);
        }

        [Test]
        public void PlayerTranscriptReceived_CreateWithSpeaker_Factory()
        {
            var speakerInfo = new SpeakerInfo("speaker-123", "John", "PA_xyz");

            var evt = PlayerTranscriptReceived.CreateWithSpeaker(
                "Hello",
                DomainPhase.ProcessedFinal,
                speakerInfo
            );

            Assert.AreEqual("speaker-123", evt.Message.PlayerOrCharacterId);
            Assert.AreEqual("John", evt.Message.DisplayName);
            Assert.AreEqual("PA_xyz", evt.Message.ParticipantId);
            Assert.AreEqual(SpeakerType.Player, evt.Message.SpeakerType);
            Assert.IsTrue(evt.HasSpeakerInfo);
        }

        [Test]
        public void TranscriptMessage_ForPlayer_Factory_Sets_SpeakerType()
        {
            TranscriptMessage message = TranscriptMessage.ForPlayer(
                "Hello",
                true,
                "speaker-123",
                "John",
                "PA_xyz"
            );

            Assert.AreEqual(SpeakerType.Player, message.SpeakerType);
            Assert.AreEqual("speaker-123", message.PlayerOrCharacterId);
            Assert.AreEqual("John", message.DisplayName);
            Assert.AreEqual("PA_xyz", message.ParticipantId);
        }

        [Test]
        public void TranscriptMessage_ForCharacter_Factory_Sets_SpeakerType()
        {
            TranscriptMessage message = TranscriptMessage.ForCharacter(
                "char-1",
                "Alice",
                "Hello",
                true,
                "PA_abc"
            );

            Assert.AreEqual(SpeakerType.Character, message.SpeakerType);
            Assert.AreEqual("char-1", message.PlayerOrCharacterId);
            Assert.AreEqual("Alice", message.DisplayName);
            Assert.AreEqual("PA_abc", message.ParticipantId);
        }

        [Test]
        public void TranscriptMessage_WithActorInfo_Creates_Copy()
        {
            TranscriptMessage original = TranscriptMessage.ForPlayer(
                "Hello",
                true,
                "old-id",
                "OldName"
            );

            TranscriptMessage updated = original.WithPlayerOrCharacterInfo("new-id", "NewName", "PA_new");

            Assert.AreEqual("new-id", updated.PlayerOrCharacterId);
            Assert.AreEqual("NewName", updated.DisplayName);
            Assert.AreEqual("PA_new", updated.ParticipantId);

            Assert.AreEqual("old-id", original.PlayerOrCharacterId);
            Assert.AreEqual("OldName", original.DisplayName);
        }

        private sealed class CapturingEventHub : IEventHub
        {
            public List<PlayerTranscriptReceived> CapturedEvents { get; } = new();

            public void Publish<TEvent>(TEvent @event) where TEvent : notnull
            {
                if (@event is PlayerTranscriptReceived ptr)
                    CapturedEvents.Add(ptr);
            }

            public SubscriptionToken Subscribe<TEvent>(IEventSubscriber<TEvent> subscriber,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull
                => SubscriptionToken.Create();

            public SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull
                => SubscriptionToken.Create();

            public SubscriptionToken Subscribe<TEvent>(IEventSubscriber<TEvent> subscriber, IEventFilter<TEvent> filter,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull
                => SubscriptionToken.Create();

            public SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler, IEventFilter<TEvent> filter,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull
                => SubscriptionToken.Create();

            public void Unsubscribe(SubscriptionToken token) { }

            public bool TryPeriodicCleanup(float currentTimeSeconds, float cleanupIntervalSeconds = 60f) => false;
        }
    }
}
