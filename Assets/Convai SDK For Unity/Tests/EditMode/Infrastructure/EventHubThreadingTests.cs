using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.EventSystem;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    ///     Multi-threaded integration tests for EventHub.
    ///     Tests concurrent subscriptions, thread safety, and delivery policy correctness.
    /// </summary>
    public class EventHubThreadingTests
    {
        private EventHub _eventHub;
        private ThreadingTestScheduler _scheduler;

        [SetUp]
        public void SetUp()
        {
            _scheduler = new ThreadingTestScheduler();
            _eventHub = new EventHub(_scheduler);
        }

        [TearDown]
        public void TearDown()
        {
            _scheduler?.Reset();
            _scheduler = null;
            _eventHub = null;
        }

        #region Copy-on-Write Thread Safety Tests

        [Test]
        public void CopyOnWrite_IterationNotAffectedByModification()
        {
            int deliveryCount = 0;
            ManualResetEventSlim firstDeliveryStarted = new(false);
            ManualResetEventSlim subscribeComplete = new(false);

            _eventHub.Subscribe<ThreadingEvent>(_ =>
            {
                firstDeliveryStarted.Set();
                subscribeComplete.Wait(TimeSpan.FromSeconds(5));
                Interlocked.Increment(ref deliveryCount);
            }, EventDeliveryPolicy.Immediate);

            Task publishTask = Task.Run(() => _eventHub.Publish(new ThreadingEvent(1)));

            firstDeliveryStarted.Wait(TimeSpan.FromSeconds(5));

            for (int i = 0; i < 10; i++)
            {
                _eventHub.Subscribe<ThreadingEvent>(_ => Interlocked.Increment(ref deliveryCount),
                    EventDeliveryPolicy.Immediate);
            }

            subscribeComplete.Set();
            publishTask.Wait(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, deliveryCount,
                "Original subscriber should receive event despite concurrent modifications");
        }

        #endregion

        #region Helper Types

        private readonly struct ThreadingEvent
        {
            public int Value { get; }

            public ThreadingEvent(int value)
            {
                Value = value;
            }
        }

        #endregion

        #region Concurrent Subscription Tests

        [Test]
        public void ConcurrentSubscriptions_DoNotCorruptState()
        {
            const int threadCount = 10;
            const int subscriptionsPerThread = 100;
            ConcurrentBag<SubscriptionToken> tokens = new();
            CountdownEvent countdown = new(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        for (int i = 0; i < subscriptionsPerThread; i++)
                        {
                            SubscriptionToken token = _eventHub.Subscribe<ThreadingEvent>(
                                _ => { },
                                EventDeliveryPolicy.Immediate);
                            tokens.Add(token);
                        }
                    }
                    finally
                    {
                        countdown.Signal();
                    }
                });
            }

            bool completed = countdown.Wait(TimeSpan.FromSeconds(10));
            Assert.IsTrue(completed, "All subscription threads should complete");

            int expectedCount = threadCount * subscriptionsPerThread;
            Assert.AreEqual(expectedCount, tokens.Count, "All subscriptions should be created");
            Assert.AreEqual(expectedCount, _eventHub.GetSubscriptionCount<ThreadingEvent>(),
                "EventHub should track all subscriptions");
        }

        [Test]
        public void ConcurrentUnsubscriptions_DoNotCorruptState()
        {
            const int subscriptionCount = 1000;
            var tokens = new SubscriptionToken[subscriptionCount];

            for (int i = 0; i < subscriptionCount; i++)
                tokens[i] = _eventHub.Subscribe<ThreadingEvent>(_ => { }, EventDeliveryPolicy.Immediate);

            const int threadCount = 10;
            int tokensPerThread = subscriptionCount / threadCount;
            CountdownEvent countdown = new(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                int threadIndex = t;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        int start = threadIndex * tokensPerThread;
                        for (int i = 0; i < tokensPerThread; i++) _eventHub.Unsubscribe(tokens[start + i]);
                    }
                    finally
                    {
                        countdown.Signal();
                    }
                });
            }

            bool completed = countdown.Wait(TimeSpan.FromSeconds(10));
            Assert.IsTrue(completed, "All unsubscription threads should complete");
            Assert.AreEqual(0, _eventHub.GetSubscriptionCount<ThreadingEvent>(),
                "All subscriptions should be removed");
        }

        #endregion

        #region Delivery Policy Tests

        [Test]
        public void MainThreadDelivery_QueuesForMainThread()
        {
            int deliveryCount = 0;

            _eventHub.Subscribe<ThreadingEvent>(_ =>
            {
                Interlocked.Increment(ref deliveryCount);
            });

            Task.Run(() => _eventHub.Publish(new ThreadingEvent(1))).Wait();

            Assert.AreEqual(0, deliveryCount, "Event should be queued, not delivered immediately");
            Assert.AreEqual(1, _scheduler.MainThreadQueueCount, "One action should be queued");

            _scheduler.ProcessMainThreadQueue();

            Assert.AreEqual(1, deliveryCount, "Event should be delivered after processing queue");
        }

        [Test]
        public void ImmediateDelivery_DeliversOnCallingThread()
        {
            int deliveryThreadId = 0;
            int callingThreadId = 0;

            _eventHub.Subscribe<ThreadingEvent>(_ =>
            {
                deliveryThreadId = Thread.CurrentThread.ManagedThreadId;
            }, EventDeliveryPolicy.Immediate);

            Task.Run(() =>
            {
                callingThreadId = Thread.CurrentThread.ManagedThreadId;
                _eventHub.Publish(new ThreadingEvent(1));
            }).Wait();

            Assert.AreEqual(callingThreadId, deliveryThreadId,
                "Immediate delivery should happen on calling thread");
        }

        [Test]
        public void BackgroundDelivery_DeliversOnBackgroundThread()
        {
            int deliveryThreadId = 0;
            ManualResetEventSlim delivered = new(false);

            _eventHub.Subscribe<ThreadingEvent>(_ =>
            {
                deliveryThreadId = Thread.CurrentThread.ManagedThreadId;
                delivered.Set();
            }, EventDeliveryPolicy.Background);

            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _eventHub.Publish(new ThreadingEvent(1));

            bool wasDelivered = delivered.Wait(TimeSpan.FromSeconds(5));

            Assert.IsTrue(wasDelivered, "Event should be delivered");
            Assert.AreNotEqual(mainThreadId, deliveryThreadId,
                "Background delivery should happen on different thread");
        }

        #endregion
    }
}
