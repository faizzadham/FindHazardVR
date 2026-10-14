using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Unity.Profiling;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine.Profiling;

namespace Convai.Tests.EditMode.Fixtures
{
    internal static class AllocationMeasurement
    {
        internal const int WarmupCalls = 1000;
        internal const int MeasuredCalls = 100;
        private static byte[] _control;

        // Unity's Boehm GC reports zero from GetAllocatedBytesForCurrentThread. Read the
        // scoped hierarchy GC byte column instead; the frame counter includes other threads.
        internal static IEnumerator Measure(Action<int> operation, Action<long> measured, string label = null)
        {
            bool enabled = ProfilerDriver.enabled;
            bool editor = ProfilerDriver.profileEditor;
            bool cpu = ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU);
            bool memory = ProfilerDriver.IsAreaEnabled(ProfilerArea.Memory);
            Assert.That(ProfilerDriver.deepProfiling, Is.False, "Allocation gates require Deep Profile disabled.");
            string prefix = "Convai.Tests.Allocation." + Guid.NewGuid().ToString("N");
            string[] names = { prefix + ".Control", prefix + ".First", prefix + ".Second" };
            var controlMarker = new ProfilerMarker(names[0]);
            var firstMarker = new ProfilerMarker(names[1]);
            var secondMarker = new ProfilerMarker(names[2]);
            try
            {
                ProfilerDriver.profileEditor = true;
                ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU, true);
                ProfilerDriver.SetAreaEnabled(ProfilerArea.Memory, true);
                ProfilerDriver.enabled = true;
                yield return null;
                yield return null;
                AllocateControl();
                for (int i = 0; i < WarmupCalls; i++) operation(i);
                int frameBefore = ProfilerDriver.lastFrameIndex;
                using (controlMarker.Auto()) AllocateControl();
                Batch(operation, firstMarker);
                Batch(operation, secondMarker);

                long[] bytes = null;
                for (int attempt = 0; attempt < 20 && bytes == null; attempt++)
                {
                    yield return null;
                    bytes = ReadBytes(frameBefore, names);
                }
                Assert.That(bytes, Is.Not.Null, "All three scoped allocation samples must be present; missing samples are not zero.");
                Assert.That(bytes[0], Is.GreaterThanOrEqualTo(1024L), "The scoped byte reader must detect the retained 1 KiB positive control.");
                TestContext.WriteLine($"Allocation batches: label={label}; calls={MeasuredCalls}; first={bytes[1]}; second={bytes[2]}; control={bytes[0]}");
                measured(bytes[2]);
            }
            finally
            {
                ProfilerDriver.profileEditor = editor;
                ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU, cpu);
                ProfilerDriver.SetAreaEnabled(ProfilerArea.Memory, memory);
                ProfilerDriver.enabled = enabled;
                _control = null;
            }
        }

        private static void Batch(Action<int> operation, ProfilerMarker marker)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using (marker.Auto())
                for (int i = 0; i < MeasuredCalls; i++) operation(i);
        }

        private static long[] ReadBytes(int frameBefore, string[] names)
        {
            var bytes = new long[names.Length];
            var found = new bool[names.Length];
            var pending = new Stack<int>();
            var children = new List<int>();
            for (int frame = Math.Max(frameBefore, ProfilerDriver.firstFrameIndex); frame <= ProfilerDriver.lastFrameIndex; frame++)
            {
                using var view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnGcMemory, false);
                if (view == null || !view.valid) continue;
                pending.Push(view.GetRootItemID());
                while (pending.Count > 0)
                {
                    int item = pending.Pop();
                    int index = Array.IndexOf(names, view.GetItemName(item));
                    if (index >= 0)
                    {
                        bytes[index] += (long)view.GetItemColumnDataAsFloat(item, HierarchyFrameDataView.columnGcMemory);
                        found[index] = true;
                    }
                    children.Clear();
                    view.GetItemChildren(item, children);
                    foreach (int child in children) pending.Push(child);
                }
            }
            return Array.TrueForAll(found, value => value) ? bytes : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AllocateControl() => _control = new byte[1024];
    }
}
