using System;
using System.Collections.Generic;
using Convai.Domain.EventSystem;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Fixtures
{
    /// <summary>
    ///     Synchronous, in-memory <see cref="IEventHub" /> for unit tests.
    ///     Publish delivers immediately on the calling thread regardless of delivery policy.
    ///     Call <see cref="Reset" /> in [SetUp] to ensure test isolation.
    /// </summary>
    public sealed class FakeEventHub : IEventHub
    {
        private readonly Dictionary<Type, List<object>> _handlers = new();
        private readonly Dictionary<Type, object[]> _snapshots = new();
        private readonly Dictionary<SubscriptionToken, (Type eventType, object handler)> _tokens = new();
        private readonly Dictionary<Type, int> _publishCounts = new();

        public SubscriptionToken Subscribe<TEvent>(
            IEventSubscriber<TEvent> subscriber,
            EventDeliveryPolicy _ = EventDeliveryPolicy.MainThread)
            where TEvent : notnull
        {
            if (subscriber == null) throw new ArgumentNullException(nameof(subscriber));
            Action<TEvent> action = e => subscriber.OnEvent(e);
            return RegisterAction<TEvent>(action);
        }

        public SubscriptionToken Subscribe<TEvent>(
            Action<TEvent> handler,
            EventDeliveryPolicy _ = EventDeliveryPolicy.MainThread)
            where TEvent : notnull
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return RegisterAction<TEvent>(handler);
        }

        public SubscriptionToken Subscribe<TEvent>(
            IEventSubscriber<TEvent> subscriber,
            IEventFilter<TEvent> filter,
            EventDeliveryPolicy _ = EventDeliveryPolicy.MainThread)
            where TEvent : notnull
        {
            if (subscriber == null) throw new ArgumentNullException(nameof(subscriber));
            Action<TEvent> action = e =>
            {
                if (filter == null || filter.ShouldDeliver(e))
                    subscriber.OnEvent(e);
            };
            return RegisterAction<TEvent>(action);
        }

        public SubscriptionToken Subscribe<TEvent>(
            Action<TEvent> handler,
            IEventFilter<TEvent> filter,
            EventDeliveryPolicy _ = EventDeliveryPolicy.MainThread)
            where TEvent : notnull
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Action<TEvent> action = e =>
            {
                if (filter == null || filter.ShouldDeliver(e))
                    handler(e);
            };
            return RegisterAction<TEvent>(action);
        }

        public void Publish<TEvent>(TEvent @event) where TEvent : notnull
        {
            Type key = typeof(TEvent);
            _publishCounts[key] = (_publishCounts.TryGetValue(key, out int c) ? c : 0) + 1;

            if (!_handlers.TryGetValue(key, out List<object> list)) return;
            if (!_snapshots.TryGetValue(key, out object[] snapshot))
                _snapshots[key] = snapshot = list.ToArray();
            foreach (object h in snapshot)
                ((Action<TEvent>)h)(@event);
        }

        public void Unsubscribe(SubscriptionToken token)
        {
            if (!_tokens.TryGetValue(token, out var entry)) return;
            if (_handlers.TryGetValue(entry.eventType, out List<object> list))
                list.Remove(entry.handler);
            _snapshots.Remove(entry.eventType);
            _tokens.Remove(token);
        }

        public bool TryPeriodicCleanup(float currentTimeSeconds, float cleanupIntervalSeconds = 60f) => false;

        // ── Test helpers ──────────────────────────────────────────────────────────

        /// <summary>Returns how many times <typeparamref name="TEvent" /> has been published.</summary>
        public int PublishCount<TEvent>() =>
            _publishCounts.TryGetValue(typeof(TEvent), out int c) ? c : 0;

        /// <summary>Asserts that <typeparamref name="TEvent" /> was published at least once and the last event matches <paramref name="predicate" />.</summary>
        public void AssertPublished<TEvent>(Func<TEvent, bool> predicate = null) where TEvent : notnull
        {
            Assert.Greater(PublishCount<TEvent>(), 0,
                $"Expected {typeof(TEvent).Name} to be published at least once.");
        }

        /// <summary>Clears all subscriptions and publish counts. Call in [SetUp] for isolation.</summary>
        public void Reset()
        {
            _handlers.Clear();
            _snapshots.Clear();
            _tokens.Clear();
            _publishCounts.Clear();
        }

        private SubscriptionToken RegisterAction<TEvent>(Action<TEvent> action) where TEvent : notnull
        {
            Type key = typeof(TEvent);
            if (!_handlers.TryGetValue(key, out List<object> list))
                _handlers[key] = list = new List<object>();
            list.Add(action);
            _snapshots.Remove(key);

            SubscriptionToken token = new(Guid.NewGuid());
            _tokens[token] = (key, action);
            return token;
        }
    }
}
