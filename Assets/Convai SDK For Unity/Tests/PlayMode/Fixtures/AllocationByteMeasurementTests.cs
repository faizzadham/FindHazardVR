#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using Unity.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Fixtures
{
    public sealed class AllocationByteMeasurementTests
    {
        private byte[] _retained;

        [UnityTest]
        public IEnumerator SynchronousBatches_ReportBytesIndependently()
        {
            using var measurement = new AllocationByteMeasurement();
            Action first = () => _retained = new byte[2048];
            Action second = () => _retained = new byte[4096];
            first();
            second();
            yield return null;
            yield return null;

            yield return measurement.Measure(first);
            Assert.That(measurement.Bytes, Is.InRange(2048L, 2304L));
            yield return measurement.Measure(second);
            Assert.That(measurement.Bytes, Is.InRange(4096L, 4352L));
            GC.KeepAlive(_retained);
        }

        [UnityTest]
        public IEnumerator FrameBatch_IncludesLateUpdateAndExcludesUnrelatedAllocations()
        {
            using var measurement = new AllocationByteMeasurement();
            var root = new GameObject("AllocationByteProbe");
            try
            {
                var probe = root.AddComponent<AllocationByteProbe>();
                probe.Scope = "Convai.Tests.LateAllocation." + Guid.NewGuid().ToString("N");
                probe.Marker = new ProfilerMarker(probe.Scope);
                for (int i = 0; i < 5; i++) yield return null;

                yield return measurement.MeasureFrames(4, probe.Scope);
                Assert.That(measurement.Bytes, Is.InRange(4 * 128L, 4 * 256L),
                    "Count bytes in all four LateUpdates; exclude the separate 4 KiB allocations.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [UnityTest]
        public IEnumerator MissingWorkloadScope_FailsInsteadOfReportingZero()
        {
            bool enabled = ProfilerDriver.enabled;
            using var measurement = new AllocationByteMeasurement();
            yield return null;
            yield return null;
            yield return ExpectFailure(measurement.MeasureFrames(2, "Convai.Tests.Absent." + Guid.NewGuid()),
                "Missing workload scope");
            Assert.That(measurement.Bytes, Is.EqualTo(-1));
            Assert.That(ProfilerDriver.enabled, Is.EqualTo(enabled),
                "A failed child measurement must restore recording before its caller is disposed.");
        }

        [UnityTest]
        public IEnumerator NonAllocatingPositiveControl_FailsInsteadOfAcceptingZero()
        {
            using var measurement = new AllocationByteMeasurement(() => { });
            yield return null;
            yield return null;
            yield return ExpectFailure(measurement.Measure(() => { }), "retained 1 KiB positive control");
            Assert.That(measurement.Bytes, Is.EqualTo(-1));
        }

        [Test]
        public void FailingControl_RestoresProfilerSettings()
        {
            bool enabled = ProfilerDriver.enabled;
            bool editor = ProfilerDriver.profileEditor;
            bool cpu = ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU);
            bool memory = ProfilerDriver.IsAreaEnabled(ProfilerArea.Memory);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var measurement = new AllocationByteMeasurement(() => throw new InvalidOperationException());
            });
            Assert.That(ProfilerDriver.enabled, Is.EqualTo(enabled));
            Assert.That(ProfilerDriver.profileEditor, Is.EqualTo(editor));
            Assert.That(ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU), Is.EqualTo(cpu));
            Assert.That(ProfilerDriver.IsAreaEnabled(ProfilerArea.Memory), Is.EqualTo(memory));
        }

        private static IEnumerator ExpectFailure(IEnumerator measurement, string message)
        {
            try
            {
                while (true)
                {
                    bool next;
                    try
                    {
                        next = measurement.MoveNext();
                    }
                    catch (AssertionException error)
                    {
                        StringAssert.Contains(message, error.Message);
                        yield break;
                    }
                    if (!next) break;
                    yield return measurement.Current;
                }
                Assert.Fail("Measurement unexpectedly accepted invalid profiler evidence.");
            }
            finally
            {
                (measurement as IDisposable)?.Dispose();
            }
        }
    }
}
#endif
