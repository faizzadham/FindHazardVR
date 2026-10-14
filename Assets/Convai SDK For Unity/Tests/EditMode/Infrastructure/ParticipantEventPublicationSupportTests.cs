using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.DomainEvents.Participant;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Infrastructure.Networking;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class ParticipantEventPublicationSupportTests
    {
        [Test]
        public void PublishConnected_PublishesParticipantConnectedEvent()
        {
            RecordingEventHub eventHub = new();
            TestLogger logger = new();
            ParticipantInfo participant = ParticipantInfo.ForCharacter("participant-1", "char-1", "Guide");

            ParticipantEventPublicationSupport.PublishConnected(eventHub, logger, participant, "TestPublisher");

            Assert.That(eventHub.LastPublishedEvent, Is.TypeOf<ParticipantConnected>());
            Assert.That(((ParticipantConnected)eventHub.LastPublishedEvent).Participant, Is.EqualTo(participant));
            Assert.That(logger.DebugMessages,
                Has.Some.Contains("Published ParticipantConnected via EventHub: Guide (participant-1)"));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Transport));
        }

        [Test]
        public void PublishDisconnected_PublishesParticipantDisconnectedEvent()
        {
            RecordingEventHub eventHub = new();
            TestLogger logger = new();
            ParticipantInfo participant = ParticipantInfo.ForCharacter("participant-2", "char-2", "Helper");

            ParticipantEventPublicationSupport.PublishDisconnected(eventHub, logger, participant, "TestPublisher");

            Assert.That(eventHub.LastPublishedEvent, Is.TypeOf<ParticipantDisconnected>());
            Assert.That(((ParticipantDisconnected)eventHub.LastPublishedEvent).Participant, Is.EqualTo(participant));
            Assert.That(logger.DebugMessages,
                Has.Some.Contains("Published ParticipantDisconnected via EventHub: Helper (participant-2)"));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Transport));
        }

        [Test]
        public void PublishConnected_WhenEventHubUnavailable_LogsSkip()
        {
            TestLogger logger = new();
            ParticipantInfo participant = ParticipantInfo.ForCharacter("participant-3", "char-3", "MissingHub");

            Assert.DoesNotThrow(() =>
                ParticipantEventPublicationSupport.PublishConnected(null, logger, participant, "TestPublisher"));

            Assert.That(logger.DebugMessages,
                Has.Some.Contains("EventHub unavailable; skipping ParticipantConnected event."));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Transport));
        }

        [Test]
        public void PublishDisconnected_WhenPublishThrows_LogsFailure()
        {
            ThrowingEventHub eventHub = new(new InvalidOperationException("boom"));
            TestLogger logger = new();
            ParticipantInfo participant = ParticipantInfo.ForCharacter("participant-4", "char-4", "Thrower");

            Assert.DoesNotThrow(() =>
                ParticipantEventPublicationSupport.PublishDisconnected(eventHub, logger, participant, "TestPublisher"));

            Assert.That(logger.DebugMessages,
                Has.Some.Contains("Failed to publish ParticipantDisconnected event: boom"));
            Assert.That(logger.Entries.Select(entry => entry.Source), Is.All.EqualTo("TestPublisher"));
            Assert.That(logger.Entries.Select(entry => entry.Category), Is.All.EqualTo(LogCategory.Transport));
        }

        private sealed class RecordingEventHub : IEventHub
        {
            public object LastPublishedEvent { get; private set; }

            public SubscriptionToken Subscribe<TEvent>(IEventSubscriber<TEvent> subscriber,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public SubscriptionToken Subscribe<TEvent>(IEventSubscriber<TEvent> subscriber, IEventFilter<TEvent> filter,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler, IEventFilter<TEvent> filter,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public void Publish<TEvent>(TEvent @event) where TEvent : notnull => LastPublishedEvent = @event;
            public void Unsubscribe(SubscriptionToken token) { }
            public bool TryPeriodicCleanup(float currentTimeSeconds, float cleanupIntervalSeconds = 60f) => false;
        }

        private sealed class ThrowingEventHub : IEventHub
        {
            private readonly Exception _exception;

            public ThrowingEventHub(Exception exception) => _exception = exception;

            public SubscriptionToken Subscribe<TEvent>(IEventSubscriber<TEvent> subscriber,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public SubscriptionToken Subscribe<TEvent>(IEventSubscriber<TEvent> subscriber, IEventFilter<TEvent> filter,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public SubscriptionToken Subscribe<TEvent>(Action<TEvent> handler, IEventFilter<TEvent> filter,
                EventDeliveryPolicy deliveryPolicy = EventDeliveryPolicy.MainThread) where TEvent : notnull =>
                throw new NotSupportedException();

            public void Publish<TEvent>(TEvent @event) where TEvent : notnull => throw _exception;
            public void Unsubscribe(SubscriptionToken token) { }
            public bool TryPeriodicCleanup(float currentTimeSeconds, float cleanupIntervalSeconds = 60f) => false;
        }

        private sealed class TestLogger : ILogger
        {
            public List<LogEntry> Entries { get; } = new();
            public void Log(in Convai.Domain.Logging.LogEntry entry)
            {
                Entries.Add(entry);
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

            public void Log(LogLevel level, string message, LogCategory category = LogCategory.SDK) { }
            public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }
            public void Debug(string message, LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);
            public void Debug(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) => DebugMessages.Add(message);
            public void Info(string message, LogCategory category = LogCategory.SDK) { }
            public void Info(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }
            public void Warning(string message, LogCategory category = LogCategory.SDK) { }
            public void Warning(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }
            public void Error(string message, LogCategory category = LogCategory.SDK) { }
            public void Error(string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }
            public void Error(Exception exception, string message = null, LogCategory category = LogCategory.SDK) { }
            public void Error(Exception exception, string message, IReadOnlyDictionary<string, object> context,
                LogCategory category = LogCategory.SDK) { }
            public bool IsEnabled(LogLevel level, LogCategory category) => true;
        }
    }
}