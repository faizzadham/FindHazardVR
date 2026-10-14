using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Unity.Profiling;

namespace Convai.Tests.Fixtures
{
    /// <summary>Checks synchronous zero-allocation workloads with a validated event count for the current thread.</summary>
    public static class AllocationCounter
    {
        private static byte[] _control;

        public static long Measure(Action batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Read(AllocateControl);
                long control = Read(AllocateControl);
                Assert.That(control, Is.GreaterThan(0), "GC.Alloc must detect a retained allocation before zero counts are trusted.");
                long first = Read(batch);
                long second = Read(batch);
                TestContext.WriteLine($"Allocation counts: first={first}; second={second}; control={control}");
                // Both batches are measured work; neither may absorb a first-use allocation.
                return Math.Max(first, second);
            }
            finally { _control = null; }
        }

        private static long Read(Action operation)
        {
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
                ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            if (!recorder.Valid) throw new InvalidOperationException("GC.Alloc recorder unavailable.");
            operation();
            recorder.Stop();
            return recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AllocateControl() => _control = new byte[1024];
    }
}
