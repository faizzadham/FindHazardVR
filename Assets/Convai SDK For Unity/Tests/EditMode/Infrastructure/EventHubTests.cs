using System;
using Convai.Domain.EventSystem;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    ///     Tests for EventHub functionality, including periodic cleanup and event versioning.
    /// </summary>
    public class EventHubTests
    {
        private EventHub _eventHub;

        [SetUp]
        public void SetUp() => _eventHub = new EventHub(new ImmediateScheduler());

        [TearDown]
        public void TearDown() => _eventHub = null;

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        #region Helper Types

        private readonly struct SimpleNonVersionedEvent
        {
            public string Value { get; }

            public SimpleNonVersionedEvent(string value)
            {
                Value = value;
            }
        }

        #endregion

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator Delivery_WarmedSubscriber_StaysWithinAllocationBudget()
        {
            var cases = new[] { (EventDeliveryPolicy.Immediate, true), (EventDeliveryPolicy.MainThread, true),
                (EventDeliveryPolicy.MainThread, false), (EventDeliveryPolicy.Background, true) };
            foreach (var (policy, onMainThread) in cases)
            {
                var scheduler = new AllocationScheduler { OnMainThread = onMainThread };
                var hub = new EventHub(scheduler);
                var value = new SimpleNonVersionedEvent("fixture");
                int delivered = 0;
                bool allValuesMatch = true;
                Action<SimpleNonVersionedEvent> observer = received =>
                {
                    delivered++;
                    allValuesMatch &= received.Value == value.Value;
                };
                hub.Subscribe(observer, policy);
                long bytes = 0;
                yield return AllocationMeasurement.Measure(_ =>
                {
                    hub.Publish(value);
                    scheduler.Drain();
                }, result => bytes = result, $"{policy}/{onMainThread}");

                Assert.That(delivered, Is.EqualTo(AllocationMeasurement.WarmupCalls + 2 * AllocationMeasurement.MeasuredCalls));
                Assert.That(allValuesMatch, Is.True);
                Assert.That(scheduler.PendingCount, Is.Zero);
                GC.KeepAlive(observer);
                long ceiling = policy == EventDeliveryPolicy.Immediate ? 80L : 208L;
                Assert.That(bytes, Is.LessThanOrEqualTo(ceiling * AllocationMeasurement.MeasuredCalls),
                    $"Warmed {policy}/{onMainThread} delivery must stay within its measured allocation baseline.");
            }
        }

        private sealed class AllocationScheduler : IUnityScheduler
        {
            private readonly System.Collections.Generic.Queue<Action> _pending = new(1);
            internal bool OnMainThread;
            internal int PendingCount => _pending.Count;
            public bool IsMainThread() => OnMainThread;
            public void ScheduleOnMainThread(Action action)
            {
                if (OnMainThread) action();
                else _pending.Enqueue(action);
            }
            public void ScheduleOnBackground(Action action) => _pending.Enqueue(action);
            internal void Drain()
            {
                while (_pending.Count > 0) _pending.Dequeue()();
            }
        }

        #region TryPeriodicCleanup Tests

        [Test]
        public void TryPeriodicCleanup_ReturnsTrue_WhenIntervalElapsed()
        {
            bool result = _eventHub.TryPeriodicCleanup(0f);

            Assert.IsTrue(result, "First cleanup should trigger when interval has elapsed");
        }

        [Test]
        public void TryPeriodicCleanup_ReturnsFalse_WhenIntervalNotElapsed()
        {
            _eventHub.TryPeriodicCleanup(0f);

            bool result = _eventHub.TryPeriodicCleanup(30f);

            Assert.IsFalse(result, "Cleanup should not trigger before interval elapsed");
        }

        [Test]
        public void TryPeriodicCleanup_ReturnsTrue_AfterIntervalElapsed()
        {
            _eventHub.TryPeriodicCleanup(0f);

            bool result = _eventHub.TryPeriodicCleanup(61f);

            Assert.IsTrue(result, "Cleanup should trigger after interval elapsed");
        }

        [Test]
        public void TryPeriodicCleanup_UpdatesLastCleanupTime()
        {
            _eventHub.TryPeriodicCleanup(100f);

            Assert.AreEqual(100f, _eventHub.LastPeriodicCleanupTime,
                "LastPeriodicCleanupTime should be updated after cleanup");
        }

        [Test]
        public void ResetPeriodicCleanupTimer_ResetsToZero()
        {
            _eventHub.TryPeriodicCleanup(100f);
            _eventHub.ResetPeriodicCleanupTimer();

            Assert.AreEqual(0f, _eventHub.LastPeriodicCleanupTime,
                "ResetPeriodicCleanupTimer should reset timer to 0");
        }

        [Test]
        public void TryPeriodicCleanup_UsesDefaultInterval_WhenNotSpecified()
        {
            _eventHub.TryPeriodicCleanup(0f);

            bool result30 = _eventHub.TryPeriodicCleanup(30f);
            Assert.IsFalse(result30, "Should not trigger before default 60s interval");

            bool result61 = _eventHub.TryPeriodicCleanup(61f);
            Assert.IsTrue(result61, "Should trigger after default 60s interval");
        }

        #endregion

        #region Event Filtering Tests

        [Test]
        public void Subscribe_WithPredicateFilter_FiltersBasedOnPredicate()
        {
            var hub = new EventHub(new ImmediateScheduler());
            int receivedCount = 0;
            IEventFilter<SimpleNonVersionedEvent> filter =
                EventFilter.Where<SimpleNonVersionedEvent>(e => e.Value.StartsWith("A"));

            hub.Subscribe(_ => receivedCount++, filter, EventDeliveryPolicy.Immediate);

            hub.Publish(new SimpleNonVersionedEvent("Apple"));
            hub.Publish(new SimpleNonVersionedEvent("Banana"));
            hub.Publish(new SimpleNonVersionedEvent("Avocado"));

            Assert.AreEqual(2, receivedCount, "Only events starting with 'A' should be delivered");
        }

        [Test]
        public void Subscribe_WithAcceptAllFilter_DeliversAllEvents()
        {
            var hub = new EventHub(new ImmediateScheduler());
            int receivedCount = 0;
            IEventFilter<SimpleNonVersionedEvent> filter = EventFilter.AcceptAll<SimpleNonVersionedEvent>();

            hub.Subscribe(_ => receivedCount++, filter, EventDeliveryPolicy.Immediate);

            hub.Publish(new SimpleNonVersionedEvent("one"));
            hub.Publish(new SimpleNonVersionedEvent("two"));

            Assert.AreEqual(2, receivedCount, "AcceptAll filter should deliver all events");
        }

        [Test]
        public void Subscribe_WithCombinedAndFilter_RequiresBothConditions()
        {
            var hub = new EventHub(new ImmediateScheduler());
            int receivedCount = 0;
            IEventFilter<SimpleNonVersionedEvent> filter = EventFilter
                .Where<SimpleNonVersionedEvent>(e => e.Value.Length > 3)
                .And(e => e.Value.StartsWith("L"));

            hub.Subscribe(_ => receivedCount++, filter, EventDeliveryPolicy.Immediate);

            hub.Publish(new SimpleNonVersionedEvent("Long")); // passes both: length > 3 AND starts with L
            hub.Publish(new SimpleNonVersionedEvent("Hi")); // fails: length <= 3
            hub.Publish(new SimpleNonVersionedEvent("Apple")); // fails: doesn't start with L

            Assert.AreEqual(1, receivedCount, "Only events passing both conditions should be delivered");
        }

        [Test]
        public void Subscribe_WithCombinedOrFilter_RequiresEitherCondition()
        {
            var hub = new EventHub(new ImmediateScheduler());
            int receivedCount = 0;
            IEventFilter<SimpleNonVersionedEvent> filter = EventFilter
                .Where<SimpleNonVersionedEvent>(e => e.Value.Length > 5)
                .Or(EventFilter.Where<SimpleNonVersionedEvent>(e => e.Value == "Special"));

            hub.Subscribe(_ => receivedCount++, filter, EventDeliveryPolicy.Immediate);

            hub.Publish(new SimpleNonVersionedEvent("Hi")); // fails both
            hub.Publish(new SimpleNonVersionedEvent("Special")); // passes predicate (== "Special")
            hub.Publish(new SimpleNonVersionedEvent("LongWord")); // passes length (> 5)

            Assert.AreEqual(2, receivedCount, "Events passing either condition should be delivered");
        }

        [Test]
        public void Subscribe_WithFilter_CanUnsubscribe()
        {
            var hub = new EventHub(new ImmediateScheduler());
            int receivedCount = 0;
            IEventFilter<SimpleNonVersionedEvent> filter = EventFilter.AcceptAll<SimpleNonVersionedEvent>();

            SubscriptionToken token = hub.Subscribe(_ => receivedCount++, filter, EventDeliveryPolicy.Immediate);

            hub.Publish(new SimpleNonVersionedEvent("one"));
            hub.Unsubscribe(token);
            hub.Publish(new SimpleNonVersionedEvent("two"));

            Assert.AreEqual(1, receivedCount, "Should not receive events after unsubscribe");
        }

        #endregion
    }
}
