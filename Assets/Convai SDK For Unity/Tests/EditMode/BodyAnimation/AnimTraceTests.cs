using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public class AnimTraceTests
    {
        [Test]
        public void Verbosity_GatesRecording()
        {
            var trace = new AnimTrace("Test", () => 0f) { Verbosity = AnimTraceVerbosity.State };
            var entries = new List<AnimTraceEntry>();

            trace.State("state message");
            trace.Detail("detail message");   // below gate — dropped

            Assert.AreEqual(1, trace.CopyRecentEntries(entries));
            Assert.AreEqual("state message", entries[0].Message);

            trace.Verbosity = AnimTraceVerbosity.Detail;
            trace.Detail("detail message 2");

            Assert.AreEqual(2, trace.CopyRecentEntries(entries));
            Assert.AreEqual("detail message 2", entries[1].Message);
        }

        [Test]
        public void Off_StillRecordsWarningsAndErrors()
        {
            var trace = new AnimTrace("Test", () => 0f) { Verbosity = AnimTraceVerbosity.Off };
            var entries = new List<AnimTraceEntry>();

            trace.State("dropped");
            trace.Warning("kept warning");

            Assert.AreEqual(1, trace.CopyRecentEntries(entries));
            Assert.AreEqual("kept warning", entries[0].Message);
        }

        [Test]
        public void RingBuffer_KeepsMostRecent_OldestFirst()
        {
            float now = 0f;
            var trace = new AnimTrace("Test", () => now) { Verbosity = AnimTraceVerbosity.State };

            for (int i = 0; i < AnimTrace.Capacity + 10; i++)
            {
                now = i;
                trace.State($"msg {i}");
            }

            var entries = new List<AnimTraceEntry>();
            int count = trace.CopyRecentEntries(entries);

            Assert.AreEqual(AnimTrace.Capacity, count);
            Assert.AreEqual("msg 10", entries[0].Message, "oldest surviving entry");
            Assert.AreEqual($"msg {AnimTrace.Capacity + 9}", entries[^1].Message, "newest entry");
            Assert.AreEqual(AnimTrace.Capacity + 10, trace.TotalRecorded);
        }

        [Test]
        public void Firehose_IsNeverRecorded()
        {
            var trace = new AnimTrace("Test", () => 0f) { Verbosity = AnimTraceVerbosity.Firehose };
            var entries = new List<AnimTraceEntry>();

            trace.Firehose("tick dump");
            trace.State("transition");

            Assert.AreEqual(1, trace.CopyRecentEntries(entries));
            Assert.AreEqual("transition", entries[0].Message);
        }

        // R5b: IsState/IsDetail/IsFirehose are the gates call sites must check before building
        // an eager (interpolated/concatenated) message. They must agree exactly with whether
        // State/Detail/Firehose would actually record+log at every verbosity, including Off,
        // so a future verbosity level can never silently desync from its gate property.
        [Test]
        public void GateProperties_AgreeWithCorrespondingLevel_AtEveryVerbosity(
            [Values] AnimTraceVerbosity verbosity)
        {
            var trace = new AnimTrace("Test", () => 0f) { Verbosity = verbosity };
            var entries = new List<AnimTraceEntry>();

            bool expectedState = verbosity >= AnimTraceVerbosity.State;
            bool expectedDetail = verbosity >= AnimTraceVerbosity.Detail;
            bool expectedFirehose = verbosity >= AnimTraceVerbosity.Firehose;

            Assert.AreEqual(expectedState, trace.IsState, $"IsState mismatch at {verbosity}");
            Assert.AreEqual(expectedDetail, trace.IsDetail, $"IsDetail mismatch at {verbosity}");
            Assert.AreEqual(expectedFirehose, trace.IsFirehose, $"IsFirehose mismatch at {verbosity}");

            // Cross-check against actual recording behaviour: IsState/IsDetail must predict
            // whether the ring buffer gains an entry for a State/Detail call.
            trace.State("state probe");
            Assert.AreEqual(expectedState ? 1 : 0, trace.CopyRecentEntries(entries),
                $"State recording mismatch at {verbosity}");

            trace.Detail("detail probe");
            int expectedTotal = (expectedState ? 1 : 0) + (expectedDetail ? 1 : 0);
            Assert.AreEqual(expectedTotal, trace.CopyRecentEntries(entries),
                $"Detail recording mismatch at {verbosity}");
        }
    }
}
