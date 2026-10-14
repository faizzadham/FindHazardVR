using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.Emotion.Editor;
using Convai.Tests.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Tests for <see cref="EmotionTimelineRecorder" />, which backs the emotion timeline window:
    ///     the GUI-free ring-buffer store backing <c>ConvaiEmotionTimelineWindow</c>. No GUI is
    ///     exercised here — only the sampling throttle, ring wraparound, label locking/alignment,
    ///     marker bookkeeping, and the zero-steady-state-allocation contract.
    /// </summary>
    [TestFixture]
    public sealed class EmotionTimelineRecorderTests
    {
        private static EmotionReading MakeReading(
            string dominantLabel, float dominantScore, IReadOnlyDictionary<string, float> allScores,
            string moodLabel = "neutral", float moodScore = 0f)
        {
            return new EmotionReading(dominantLabel, dominantScore, allScores, 0.5f, 0f, moodLabel, moodScore);
        }

        [Test]
        public void Record_FirstCallAfterStart_AlwaysAccepted()
        {
            var recorder = new EmotionTimelineRecorder(sampleCapacity: 10, markerCapacity: 4, sampleInterval: 0.1f);
            EmotionReading reading = MakeReading("joy", 0.5f, new Dictionary<string, float> { { "joy", 0.5f } });
            recorder.StartRecording(reading);

            Assert.That(recorder.Record(0f, reading), Is.True,
                "the first Record after StartRecording must always be accepted regardless of throttle.");
            Assert.That(recorder.SampleCount, Is.EqualTo(1));
        }

        [Test]
        public void Record_BelowInterval_DroppedAtOrAboveInterval_Accepted()
        {
            var recorder = new EmotionTimelineRecorder(sampleCapacity: 10, markerCapacity: 4, sampleInterval: 0.1f);
            EmotionReading reading = MakeReading("joy", 0.5f, new Dictionary<string, float> { { "joy", 0.5f } });
            recorder.StartRecording(reading);

            recorder.Record(0f, reading);
            Assert.That(recorder.Record(0.05f, reading), Is.False, "a call below the sampling interval must be dropped.");
            Assert.That(recorder.Record(0.1f, reading), Is.True, "a call exactly at the sampling interval must be accepted.");
            Assert.That(recorder.Record(0.25f, reading), Is.True, "a call above the sampling interval must be accepted.");

            Assert.That(recorder.SampleCount, Is.EqualTo(3));
        }

        [Test]
        public void Record_Wraparound_OldestOverwritten_OrderPreserved()
        {
            const int capacity = 5;
            const int overflow = 3;
            var recorder = new EmotionTimelineRecorder(sampleCapacity: capacity, markerCapacity: 4, sampleInterval: 0f);
            EmotionReading reading = MakeReading("joy", 0.5f, new Dictionary<string, float> { { "joy", 0.5f } });
            recorder.StartRecording(reading);

            for (int i = 0; i < capacity + overflow; i++)
                Assert.That(recorder.Record(i, reading), Is.True);

            Assert.That(recorder.SampleCount, Is.EqualTo(capacity), "count must clamp to capacity once full.");

            // Records 0..overflow-1 were overwritten; the oldest surviving record is index
            // `overflow`, and order (oldest -> newest) must be preserved through the wrap.
            for (int viewIndex = 0; viewIndex < capacity; viewIndex++)
                Assert.That(recorder.GetSampleTime(viewIndex), Is.EqualTo(overflow + viewIndex),
                    $"view index {viewIndex} did not resolve to the expected post-wrap record.");
        }

        [Test]
        public void StartRecording_LocksLabels_SortedAndStable()
        {
            var recorder = new EmotionTimelineRecorder();
            EmotionReading seed = MakeReading("joy", 0.5f,
                new Dictionary<string, float> { { "neutral", 0f }, { "anger", 0.1f }, { "joy", 0.5f } });

            recorder.StartRecording(seed);

            CollectionAssert.AreEqual(new[] { "anger", "joy", "neutral" }, recorder.Labels,
                "labels must be locked at StartRecording, sorted for a stable column order.");
        }

        [Test]
        public void Record_LabelMissingFromLaterReading_RecordsZero()
        {
            var recorder = new EmotionTimelineRecorder(sampleInterval: 0f);
            EmotionReading seed = MakeReading("joy", 0.5f,
                new Dictionary<string, float> { { "anger", 0.1f }, { "joy", 0.5f }, { "neutral", 0f } });
            recorder.StartRecording(seed);

            // Locked order (sorted): anger=0, joy=1, neutral=2.
            const int angerIndex = 0;
            const int joyIndex = 1;

            recorder.Record(0f, seed);

            EmotionReading noAnger = MakeReading("joy", 0.7f,
                new Dictionary<string, float> { { "joy", 0.7f }, { "neutral", 0f } });
            recorder.Record(1f, noAnger);

            Assert.That(recorder.GetSampleScore(0, joyIndex), Is.EqualTo(0.5f));
            Assert.That(recorder.GetSampleScore(0, angerIndex), Is.EqualTo(0.1f));
            Assert.That(recorder.GetSampleScore(1, joyIndex), Is.EqualTo(0.7f));
            Assert.That(recorder.GetSampleScore(1, angerIndex), Is.EqualTo(0f),
                "a reading missing a locked label must record 0 for it, not throw or skip the sample.");
        }

        [Test]
        public void AddMarker_OrderWraparoundAndKindsPreserved()
        {
            const int capacity = 3;
            var recorder = new EmotionTimelineRecorder(markerCapacity: capacity);

            recorder.AddMarker(0f, EmotionTimelineRecorder.MarkerKind.Dominant, "joy");
            recorder.AddMarker(1f, EmotionTimelineRecorder.MarkerKind.Mood, "warm");
            recorder.AddMarker(2f, EmotionTimelineRecorder.MarkerKind.Dominant, "anger");
            recorder.AddMarker(3f, EmotionTimelineRecorder.MarkerKind.Mood, "cold"); // overwrites the t=0 marker

            Assert.That(recorder.MarkerCount, Is.EqualTo(capacity));

            Assert.That(recorder.GetMarker(0).Time, Is.EqualTo(1f));
            Assert.That(recorder.GetMarker(0).Kind, Is.EqualTo(EmotionTimelineRecorder.MarkerKind.Mood));
            Assert.That(recorder.GetMarker(0).Label, Is.EqualTo("warm"));

            Assert.That(recorder.GetMarker(1).Time, Is.EqualTo(2f));
            Assert.That(recorder.GetMarker(1).Kind, Is.EqualTo(EmotionTimelineRecorder.MarkerKind.Dominant));
            Assert.That(recorder.GetMarker(1).Label, Is.EqualTo("anger"));

            Assert.That(recorder.GetMarker(2).Time, Is.EqualTo(3f));
            Assert.That(recorder.GetMarker(2).Kind, Is.EqualTo(EmotionTimelineRecorder.MarkerKind.Mood));
            Assert.That(recorder.GetMarker(2).Label, Is.EqualTo("cold"));
        }

        [Test]
        public void Clear_EmptiesBothRings()
        {
            var recorder = new EmotionTimelineRecorder(sampleInterval: 0f);
            EmotionReading reading = MakeReading("joy", 0.5f, new Dictionary<string, float> { { "joy", 0.5f } });
            recorder.StartRecording(reading);
            recorder.Record(0f, reading);
            recorder.Record(1f, reading);
            recorder.AddMarker(0f, EmotionTimelineRecorder.MarkerKind.Dominant, "joy");

            recorder.Clear();

            Assert.That(recorder.SampleCount, Is.EqualTo(0));
            Assert.That(recorder.MarkerCount, Is.EqualTo(0));
        }

        [Test]
        public void Clear_ThenRecord_StillZeroAllocatingWithoutRestart()
        {
            var recorder = new EmotionTimelineRecorder(sampleInterval: 0f);
            EmotionReading reading = MakeReading("joy", 0.5f, new Dictionary<string, float> { { "joy", 0.5f } });
            recorder.StartRecording(reading);
            recorder.Record(0f, reading);

            recorder.Clear();

            Assert.That(recorder.Record(1f, reading), Is.True,
                "Record must keep working after Clear() without a fresh StartRecording call.");
            Assert.That(recorder.SampleCount, Is.EqualTo(1));
        }

        [Test]
        public void Record_SteadyState_AllocatesNothing()
        {
            var recorder = new EmotionTimelineRecorder(sampleInterval: 0f);
            EmotionReading reading = MakeReading("joy", 0.5f,
                new Dictionary<string, float> { { "joy", 0.5f }, { "anger", 0.1f }, { "neutral", 0f } });
            recorder.StartRecording(reading);

            float time = 0f;
            for (int i = 0; i < 500; i++)
            {
                time += 0.01f;
                recorder.Record(time, reading);
            }

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    time += 0.01f;
                    recorder.Record(time, reading);
                }
            });

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"EmotionTimelineRecorder.Record must produce zero managed allocation events in steady state; measured {allocationCount} allocation events.");
        }
    }
}
