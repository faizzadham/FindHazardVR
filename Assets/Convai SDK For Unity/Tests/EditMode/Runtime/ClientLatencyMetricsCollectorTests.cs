using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Networking.Media;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ClientLatencyMetricsCollectorTests
    {
        private EventHub _eventHub;
        private RecordingFileWriter _writer;

        [SetUp]
        public void SetUp()
        {
            _eventHub = new EventHub(new ImmediateScheduler());
            _writer = new RecordingFileWriter();
        }

        [Test]
        public void TurnSummary_DetectionRunningWithoutCandidates_ReportsZeroAndAecAtTurnEnd()
        {
            using ClientLatencyMetricsCollector collector = CreateCollector(() => true);

            PlayCharacterTurn(collector);

            Assert.That(_writer.Summaries, Is.EqualTo(new[]
            {
                "ClientBargeIn: candidates=0 aecWarm=True aecAtTurnEnd=True"
            }));
        }

        [Test]
        public void TurnSummary_DetectionNotRunning_OmitsClientBargeIn()
        {
            using ClientLatencyMetricsCollector collector = CreateCollector(() => null);

            PlayCharacterTurn(collector);

            Assert.That(_writer.Summaries, Is.Empty);
        }

        [Test]
        public void TurnSummary_WithCandidates_KeepsExistingFields()
        {
            using ClientLatencyMetricsCollector collector = CreateCollector(() => true);

            PlayCharacterTurn(collector,
                BargeInMarker.Create(BargeInMarkerStage.ClientSpeechCandidate, BargeInTrigger.ClientVoiceActivity,
                    aecActive: false),
                BargeInMarker.Create(BargeInMarkerStage.ClientSpeechConfirmed, BargeInTrigger.ClientVoiceActivity,
                    aecActive: false));

            Assert.That(_writer.Summaries, Has.Count.EqualTo(1));
            string summary = _writer.Summaries[0];
            StringAssert.StartsWith("ClientBargeIn: candidates=1 aecWarm=False aecAtTurnEnd=True ", summary);
            StringAssert.Contains("audioPlaying→firstCandidate=", summary);
            StringAssert.Contains("audioPlaying→firstConfirmed=", summary);
        }

        private ClientLatencyMetricsCollector CreateCollector(Func<bool?> clientBargeInAecActive)
        {
            var collector = new ClientLatencyMetricsCollector(
                _eventHub, () => true, fileWriter: _writer, clientBargeInAecActive: clientBargeInAecActive);
            collector.Bind();
            return collector;
        }

        private void PlayCharacterTurn(ClientLatencyMetricsCollector collector, params BargeInMarker[] markers)
        {
            _eventHub.Publish(CharacterSpeechStateChanged.Create("character", true));
            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("character"));
            foreach (BargeInMarker marker in markers)
                collector.RecordBargeInMarker(marker);
            _eventHub.Publish(CharacterSpeechStateChanged.Create("character", false));
        }

        private sealed class RecordingFileWriter : IDebugMetricsFileWriter
        {
            public readonly List<string> Summaries = new();

            public void WriteLine(string eventType, string payload)
            {
                if (eventType == "client_latency_summary") Summaries.Add(payload);
            }

            public void Dispose()
            {
            }
        }

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
