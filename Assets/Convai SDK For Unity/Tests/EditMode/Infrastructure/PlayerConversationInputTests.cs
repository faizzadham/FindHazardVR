using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Protocol.Messages;
using Convai.Tests.EditMode.Core;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public class PlayerConversationInputTests
    {
        [Test]
        public void Interim_Stop_Without_Final_Does_Not_Emit_Completed()
        {
            var playerEvents = new CapturingPlayerEvents();
            var coordinator = new PlayerConversationInput(playerEvents, new ImmediateDispatcher());

            coordinator.HandleStart();
            coordinator.HandleInterim("Hello");
            coordinator.HandleStop();

            CollectionAssert.DoesNotContain(playerEvents.Phases, TranscriptionPhase.Completed);
            Assert.AreEqual(1, playerEvents.StoppedSessions.Count);
            Assert.IsFalse(playerEvents.StoppedSessions[0].DidProduceFinalTranscript);
        }

        [Test]
        public void AsrFinal_Stop_Completes_After_Grace_Window()
        {
            var playerEvents = new CapturingPlayerEvents();
            var coordinator = new PlayerConversationInput(playerEvents, new ImmediateDispatcher());

            coordinator.HandleStart();
            coordinator.HandleInterim("Tell me");
            coordinator.HandleAsrFinal("Tell me if");
            coordinator.HandleStop();

            Assert.IsFalse(playerEvents.Phases.Contains(TranscriptionPhase.Completed));

            Assert.That(playerEvents.Completed.Wait(TimeSpan.FromSeconds(1)), Is.True,
                "The ASR-final grace window did not complete the session.");

            CollectionAssert.AreEqual(
                new[]
                {
                    TranscriptionPhase.Listening,
                    TranscriptionPhase.Interim,
                    TranscriptionPhase.AsrFinal,
                    TranscriptionPhase.Completed
                },
                playerEvents.Phases);
            Assert.AreEqual(1, playerEvents.StoppedSessions.Count);
            Assert.IsTrue(playerEvents.StoppedSessions[0].DidProduceFinalTranscript);
        }

        [Test]
        public void AsrFinal_Stop_InAWebGLPlayer_CompletesOnThePlayerLoop()
        {
            using var loop = new PlayerLoopScope();
            var playerEvents = new CapturingPlayerEvents();
            var coordinator = new PlayerConversationInput(playerEvents, new ImmediateDispatcher());

            coordinator.HandleStart();
            coordinator.HandleAsrFinal("Tell me if");
            coordinator.HandleStop();

            Assert.That(playerEvents.Completed.Wait(TimeSpan.FromMilliseconds(500)), Is.False,
                "The grace window elapsed on a framework timer, which never fires in a WebGL player.");

            loop.Advance(0.2);

            Assert.That(playerEvents.Completed.Wait(TimeSpan.FromSeconds(1)), Is.True,
                "The grace window did not complete the session on the player loop.");
            Assert.AreEqual(1, playerEvents.StoppedSessions.Count);
        }

        [Test]
        public void ProcessedFinal_Within_Grace_Window_Wins_And_Completes_Once()
        {
            var playerEvents = new CapturingPlayerEvents();
            var coordinator = new PlayerConversationInput(playerEvents, new ImmediateDispatcher());
            var speakerInfo = new SpeakerInfo("speaker-1", "Rishav", "PA_1");

            coordinator.HandleStart();
            coordinator.HandleAsrFinal("tell me if");
            coordinator.HandleStop();

            coordinator.HandleProcessedFinal("tell me if you can hear me", speakerInfo);

            Assert.AreEqual(1, playerEvents.Phases.Count(phase => phase == TranscriptionPhase.Completed));
            Assert.AreEqual(1, playerEvents.StoppedSessions.Count);
            Assert.AreEqual("tell me if you can hear me",
                playerEvents.Transcripts.Last(entry => entry.Phase == TranscriptionPhase.ProcessedFinal).Text);
        }

        [Test]
        public void LateProcessedFinal_PublishesPhaseWithoutStartingDuplicateSession()
        {
            var playerEvents = new CapturingPlayerEvents();
            var coordinator = new PlayerConversationInput(playerEvents, new ImmediateDispatcher());
            var speakerInfo = new SpeakerInfo("speaker-1", "Player", "PA_1");

            coordinator.HandleStart();
            coordinator.HandleProcessedFinal("Hello", speakerInfo);
            coordinator.HandleStop();
            int eventCountAfterCompletion = playerEvents.Transcripts.Count;

            coordinator.HandleProcessedFinal("Hello", speakerInfo);

            Assert.AreEqual(eventCountAfterCompletion + 1, playerEvents.Transcripts.Count);
            Assert.AreEqual(2,
                playerEvents.Phases.Count(phase => phase == TranscriptionPhase.ProcessedFinal));
            Assert.AreEqual(1,
                playerEvents.Phases.Count(phase => phase == TranscriptionPhase.Completed));
            Assert.AreEqual(1, playerEvents.StoppedSessions.Count);
        }

        /// <summary>
        ///     The typed-echo guard stops a typed line being shown twice. These hold it to that job
        ///     and no more: once the typed line has come back as the player's own transcript, the
        ///     character's words are the character's, even when they are the same words.
        /// </summary>
        [Test]
        public void TypedEchoIsSuppressedBeforeThePlayerTranscriptComesBack()
        {
            var coordinator = NewCoordinator();
            coordinator.RegisterTypedText("msg-1", "hey");

            Assert.That(coordinator.ShouldSuppressMirroredCharacterText("Hey"), Is.True,
                "A mirrored typed line still has to be caught, or it is shown twice.");
        }

        [Test]
        public void TheCharacterKeepsItsReplyOnceTheTypedLineHasBeenAccountedFor()
        {
            var coordinator = NewCoordinator();
            coordinator.RegisterTypedText("msg-1", "hey");

            coordinator.HandleProcessedFinal(new FinalUserTranscriptionPayload
            {
                Text = "hey",
                MessageId = "msg-1"
            });

            Assert.That(coordinator.ShouldSuppressMirroredCharacterText("Hey."), Is.False,
                "Measured: the character's own reply arrived over a second after the typed line was " +
                "accounted for, and was thrown away for being the same word.");
        }

        [Test]
        public void AnUnrelatedAcknowledgementDoesNotRetireTheGuard()
        {
            var coordinator = NewCoordinator();
            coordinator.RegisterTypedText("msg-1", "hey");

            coordinator.HandleProcessedFinal(new FinalUserTranscriptionPayload
            {
                Text = "something else",
                MessageId = "msg-2"
            });

            Assert.That(coordinator.ShouldSuppressMirroredCharacterText("Hey"), Is.True,
                "Only the typed line's own acknowledgement retires its guard.");
        }

        /// <summary>
        ///     Built the way the SDK builds it. The logger is optional on the constructor but the
        ///     paths under test log, and a coordinator without one throws rather than staying quiet.
        /// </summary>
        private static PlayerConversationInput NewCoordinator() =>
            new(new CapturingPlayerEvents(), new ImmediateDispatcher(), logger: new SilentLogger());

        private sealed class CapturingPlayerEvents : IConvaiPlayerEvents
        {
            public readonly List<TranscriptEntry> Transcripts = new();
            public readonly List<StoppedSession> StoppedSessions = new();

            /// <summary>
            ///     Signalled once the whole grace-window sequence is over. It is set from
            ///     <see cref="OnPlayerStoppedSpeaking" /> — the last callback of the sequence — not from the
            ///     Completed transcript phase, because a test that waits on the phase and then asserts the
            ///     stopped-session list is racing two different callbacks.
            /// </summary>
            public readonly ManualResetEventSlim Completed = new(false);

            public IEnumerable<TranscriptionPhase> Phases => Transcripts.Select(entry => entry.Phase);

            public void OnPlayerTranscriptionReceived(string transcript, TranscriptionPhase transcriptionPhase) =>
                OnPlayerTranscriptionReceived(transcript, transcriptionPhase, SpeakerInfo.Empty);

            public void OnPlayerTranscriptionReceived(string transcript, TranscriptionPhase transcriptionPhase,
                SpeakerInfo speakerInfo)
            {
                Transcripts.Add(new TranscriptEntry(transcript, transcriptionPhase, speakerInfo));
            }

            public void OnPlayerStartedSpeaking(string sessionId)
            {
            }

            public void OnPlayerStoppedSpeaking(string sessionId, bool didProduceFinalTranscript)
            {
                StoppedSessions.Add(new StoppedSession(sessionId, didProduceFinalTranscript));
                Completed.Set();
            }
        }

        private sealed class ImmediateDispatcher : IMainThreadDispatcher
        {
            public bool TryDispatch(System.Action action)
            {
                action?.Invoke();
                return true;
            }
        }

        private readonly struct TranscriptEntry
        {
            public TranscriptEntry(string text, TranscriptionPhase phase, SpeakerInfo speakerInfo)
            {
                Text = text;
                Phase = phase;
                SpeakerInfo = speakerInfo;
            }

            public string Text { get; }
            public TranscriptionPhase Phase { get; }
            public SpeakerInfo SpeakerInfo { get; }
        }

        private readonly struct StoppedSession
        {
            public StoppedSession(string sessionId, bool didProduceFinalTranscript)
            {
                SessionId = sessionId;
                DidProduceFinalTranscript = didProduceFinalTranscript;
            }

            public string SessionId { get; }
            public bool DidProduceFinalTranscript { get; }
        }
    }
}
