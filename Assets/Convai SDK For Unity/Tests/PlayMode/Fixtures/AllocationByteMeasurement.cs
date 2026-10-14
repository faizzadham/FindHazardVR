using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Unity.Profiling;
#if UNITY_EDITOR
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine.Profiling;
#endif

namespace Convai.Tests.PlayMode.Fixtures
{
    // Editor hierarchy GC memory is bytes, unlike AllocationCounter's allocation-event count.
    // Keep Editor dependencies here, outside the platform-independent shared fixtures assembly.
    internal sealed class AllocationByteMeasurement : IDisposable
    {
        private readonly Action _positiveControl;
        private byte[] _retainedControl;
        internal long Bytes { get; private set; }
#if UNITY_EDITOR
        private readonly bool _enabled, _editor, _cpu, _memory;
#endif

        internal AllocationByteMeasurement(Action positiveControl = null)
        {
#if UNITY_EDITOR
            Assert.That(ProfilerDriver.deepProfiling, Is.False, "Allocation guards require Deep Profile disabled.");
            _enabled = ProfilerDriver.enabled;
            _editor = ProfilerDriver.profileEditor;
            _cpu = ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU);
            _memory = ProfilerDriver.IsAreaEnabled(ProfilerArea.Memory);
            _positiveControl = positiveControl ?? AllocateControl;
            try
            {
                ProfilerDriver.profileEditor = true;
                ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU, true);
                ProfilerDriver.SetAreaEnabled(ProfilerArea.Memory, true);
                ProfilerDriver.enabled = true;
                _positiveControl(); // Warm the control before the caller's normal workload warm-up.
            }
            catch
            {
                Dispose();
                throw;
            }
#else
            Assert.Fail("These byte guards require the Unity Editor hierarchy profiler; unsupported counters must not pass as zero.");
#endif
        }

        internal IEnumerator Measure(Action operation) => Capture(operation, 1, null);

        // The tag identifies complete recorded frames. Never hold a profiler scope across yields:
        // the scheduler and LateUpdate run outside the test coroutine's call stack.
        internal IEnumerator MeasureFrames(int frames, params string[] scopes) => Capture(null, frames, scopes);

        private IEnumerator Capture(Action operation, int frames, string[] scopes)
        {
#if UNITY_EDITOR
            bool completed = false;
            try
            {
                Assert.That(frames, Is.GreaterThan(0));
                Assert.That(operation != null || scopes?.Length > 0, Is.True, "A measured workload scope is required.");
                string prefix = "Convai.Tests.AllocationBytes." + Guid.NewGuid().ToString("N");
                string tagName = prefix + ".Work";
                string controlName = prefix + ".Control";
                var tag = new ProfilerMarker(tagName);
                var control = new ProfilerMarker(controlName);
                string[] targets = operation != null ? new[] { tagName } : scopes;
                Bytes = -1; // A failed or incomplete batch never leaves a usable zero result.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                int frameBefore = ProfilerDriver.lastFrameIndex;
                using (control.Auto()) _positiveControl();
                for (int i = 0; i < frames; i++)
                {
                    using (tag.Auto()) operation?.Invoke();
                    if (operation == null) yield return null;
                }

                long[] result = null;
                for (int attempt = 0; attempt < 20 && result == null; attempt++)
                {
                    yield return null;
                    result = Read(frameBefore, frames, tagName, controlName, targets);
                }
                Assert.That(result, Is.Not.Null,
                    $"Missing allocation frames or control sample: expected {frames} tagged frames. Missing data is not zero.");
                Assert.That(result[1], Is.GreaterThanOrEqualTo(1024L),
                    "The byte reader must detect the retained 1 KiB positive control in every batch.");
                Bytes = result[0];
                TestContext.WriteLine($"Allocation bytes: frames={frames}; workload={Bytes}; control={result[1]}");
                completed = true;
            }
            finally
            {
                // A failure in a yielded child iterator may stop Unity's parent coroutine
                // before its using statement is disposed. Restore settings at the failure site.
                if (!completed) Dispose();
            }
#else
            Assert.Fail("Allocation byte measurement requires the Unity Editor hierarchy profiler.");
            yield break;
#endif
        }

#if UNITY_EDITOR
        private static long[] Read(int frameBefore, int expectedFrames, string tagName, string controlName, string[] scopes)
        {
            int taggedFrames = 0;
            int previousFrame = -1;
            long workloadBytes = 0, controlBytes = 0;
            bool controlFound = false;
            var pending = new Stack<int>();
            var children = new List<int>();
            for (int frame = Math.Max(frameBefore, ProfilerDriver.firstFrameIndex); frame <= ProfilerDriver.lastFrameIndex; frame++)
            {
                using var view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnGcMemory, false);
                if (view == null || !view.valid) continue;
                int tags = 0;
                long frameBytes = 0;
                var found = new bool[scopes.Length];
                pending.Push(view.GetRootItemID());
                while (pending.Count > 0)
                {
                    int item = pending.Pop();
                    string name = view.GetItemName(item);
                    if (name == tagName)
                        tags += (int)view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnCalls);
                    if (name == controlName)
                    {
                        controlFound = true;
                        controlBytes += (long)view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnGcMemory);
                    }
                    int index = Array.IndexOf(scopes, name);
                    if (index >= 0)
                    {
                        found[index] = true;
                        frameBytes += (long)view.GetItemColumnDataAsDouble(item, HierarchyFrameDataView.columnGcMemory);
                        // The GC column is inclusive. Descendants must not be counted twice.
                        continue;
                    }
                    children.Clear();
                    view.GetItemChildren(item, children);
                    foreach (int child in children) pending.Push(child);
                }
                if (tags == 0) continue;
                Assert.That(tags, Is.EqualTo(1), "Each measured frame must have exactly one workload tag.");
                if (previousFrame >= 0)
                    Assert.That(frame, Is.EqualTo(previousFrame + 1), "A measured frame was dropped; a partial window cannot pass.");
                previousFrame = frame;
                for (int i = 0; i < scopes.Length; i++)
                    Assert.That(found[i], Is.True, $"Missing workload scope '{scopes[i]}' in measured frame {frame}.");
                taggedFrames++;
                workloadBytes += frameBytes;
            }
            Assert.That(taggedFrames, Is.LessThanOrEqualTo(expectedFrames), "Unexpected extra workload frames.");
            return taggedFrames == expectedFrames && controlFound ? new[] { workloadBytes, controlBytes } : null;
        }
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AllocateControl() => _retainedControl = new byte[1024];

        public void Dispose()
        {
#if UNITY_EDITOR
            ProfilerDriver.profileEditor = _editor;
            ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU, _cpu);
            ProfilerDriver.SetAreaEnabled(ProfilerArea.Memory, _memory);
            ProfilerDriver.enabled = _enabled;
#endif
            GC.KeepAlive(_retainedControl);
            _retainedControl = null;
        }
    }
}
