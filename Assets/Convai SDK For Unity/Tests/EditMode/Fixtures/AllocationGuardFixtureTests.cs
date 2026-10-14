using System;
using System.Collections.Generic;
using System.Threading;
using Convai.Domain.EventSystem;
using Convai.Tests.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Fixtures
{
    public class AllocationGuardFixtureTests
    {
        [Test]
        public void FakeDelivery_PreservesSnapshotsAcrossMutationAndNestedPublish()
        {
            var hub = new FakeEventHub();
            var deliveries = new List<string>();
            SubscriptionToken second = default;
            hub.Subscribe<int>(value =>
            {
                deliveries.Add($"first:{value}");
                if (value != 0) return;
                hub.Unsubscribe(second);
                hub.Subscribe<int>(nested => deliveries.Add($"new:{nested}"));
                hub.Publish(1);
            });
            second = hub.Subscribe<int>(value => deliveries.Add($"second:{value}"));

            hub.Publish(0);
            hub.Publish(2);

            CollectionAssert.AreEqual(new[]
            {
                "first:0", "first:1", "new:1", "second:0", "first:2", "new:2"
            }, deliveries);
            Assert.That(hub.PublishCount<int>(), Is.EqualTo(3));
            hub.Reset();
            Assert.That(hub.PublishCount<int>(), Is.Zero);
            hub.Publish(3);
            Assert.That(deliveries.Count, Is.EqualTo(6));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void AllocationCounter_DetectsAllocationInEitherBatch(int allocatingBatch)
        {
            int batch = 0;
            byte[] retained = null;
            long count = AllocationCounter.Measure(() =>
            {
                if (++batch == allocatingBatch) retained = new byte[1024];
            });
            Assert.That(retained, Has.Length.EqualTo(1024));
            Assert.That(count, Is.GreaterThan(0), "An allocation in either measured batch must fail a zero guard.");
        }

        [Test]
        public void AllocationCounter_ExcludesAWorkerAllocationFromBothBatches()
        {
            using var request = new AutoResetEvent(false);
            using var done = new AutoResetEvent(false);
            bool stop = false;
            byte[] retained = null;
            Exception workerFailure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    while (request.WaitOne())
                    {
                        if (stop) break;
                        retained = new byte[1024 * 1024];
                        done.Set();
                    }
                }
                catch (Exception error) { workerFailure = error; done.Set(); }
            }) { IsBackground = true };
            Action waitForAllocation = () =>
            {
                request.Set();
                if (!done.WaitOne(5000)) throw new TimeoutException("Worker did not complete the allocation.");
            };
            worker.Start();
            try
            {
                for (int i = 0; i < 5; i++) waitForAllocation();
                long count = AllocationCounter.Measure(waitForAllocation);
                Assert.That(workerFailure, Is.Null);
                Assert.That(retained.Length, Is.EqualTo(1024 * 1024));
                Assert.That(count, Is.Zero, "A worker allocation must not enter the current-thread count.");
            }
            finally
            {
                stop = true;
                request.Set();
                Assert.That(worker.Join(5000), Is.True, "The measurement worker must exit.");
            }
        }
    }
}
