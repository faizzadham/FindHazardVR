using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.Models;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Room;
using Convai.Tests.EditMode.Fixtures;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Push-to-talk keeps capture open briefly after physical release so the STT provider can finalize.
    ///     If the first window expires, local capture closes before the stop while backend STT remains open for
    ///     one final bounded window.
    /// </summary>
    public class ConvaiPushToTalkControllerTests
    {
        private readonly List<Object> _createdObjects = new();
        private MockRoomAudioService _audio;
        private MockRoomConnectionService _connection;
        private ConvaiPushToTalkController _controller;
        private FakeEventHub _eventHub;
        private TurnTakingOptions _options;
        private readonly List<string> _turnBoundaryOperations = new();

        [SetUp]
        public void SetUp()
        {
            _options = TurnTakingOptions.CreatePushToTalkDefault();
            _connection = new MockRoomConnectionService();
            _audio = new MockRoomAudioService();
            _eventHub = new FakeEventHub();
            _turnBoundaryOperations.Clear();
            _connection.TurnBoundaryOperationRecorded = _turnBoundaryOperations.Add;
            _audio.TurnBoundaryOperationRecorded = _turnBoundaryOperations.Add;

            var managerObject = new GameObject("ConvaiManager");
            _createdObjects.Add(managerObject);
            ConvaiManager manager = managerObject.AddComponent<ConvaiManager>();

            var controllerObject = new GameObject("ConvaiPushToTalkController");
            _createdObjects.Add(controllerObject);
            _controller = controllerObject.AddComponent<ConvaiPushToTalkController>();
            _controller.InjectForTests(
                manager,
                _eventHub,
                _connection,
                _audio,
                () => TurnTakingOptionsResolver.ResolveFromSource(_options));
            _controller.SetTargetCharacterIdForTests("character-1");

            _connection.ConnectAsync();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdObjects.Count; i++)
                if (_createdObjects[i] != null)
                    Object.DestroyImmediate(_createdObjects[i]);
            _createdObjects.Clear();
        }

        [Test]
        public void Release_KeepsCaptureOpenUntilAsrFinalThenCommitsTheTurn()
        {
            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            Assert.That(_audio.IsMicMuted, Is.False);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false }));
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.Zero);

            PublishPlayerTranscript(TranscriptionPhase.AsrFinal);

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false, true }));
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "stt:False", "mic:True", "force-user-stopped-speaking", "stt:True" }));
            Assert.That(
                _connection.TurnControlCalls,
                Is.EqualTo(new[] { "stt:False", "force-user-stopped-speaking", "stt:True" }));
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
        }

        [Test]
        public void AsrFinalBeforeRelease_DoesNotSkipTailForALaterSegment()
        {
            Assert.IsTrue(_controller.Press());
            PublishPlayerTranscript(TranscriptionPhase.AsrFinal);

            Assert.IsTrue(_controller.Release());

            Assert.That(_audio.IsMicMuted, Is.False);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.Zero);

            PublishPlayerTranscript(TranscriptionPhase.AsrFinal);

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Release_WhenInitialTailExpires_ClosesLocalCaptureBeforeStopButKeepsServerSttOpen()
        {
            _options.PushToTalkPolicy.ReleaseTailMs = 500;

            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            _controller.ExpireReleaseTailForTests();

            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false }));
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "stt:False", "mic:True", "force-user-stopped-speaking" }));
            Assert.That(
                _connection.TurnControlCalls,
                Is.EqualTo(new[] { "stt:False", "force-user-stopped-speaking" }));

            PublishPlayerTranscript(TranscriptionPhase.AsrFinal);

            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false, true }));
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "stt:False", "mic:True", "force-user-stopped-speaking", "stt:True" }));
            Assert.That(
                _connection.TurnControlCalls,
                Is.EqualTo(new[] { "stt:False", "force-user-stopped-speaking", "stt:True" }));
        }

        [Test]
        public void Release_WhenPostStopTailExpires_ClosesCaptureWithoutSendingStopAgain()
        {
            _options.PushToTalkPolicy.ReleaseTailMs = 100;

            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            _controller.ExpireReleaseTailForTests();

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false }));

            _controller.ExpireReleaseTailForTests();

            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false, true }));
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "stt:False", "mic:True", "force-user-stopped-speaking", "stt:True" }));
        }

        [Test]
        public void Release_WithTailDisabled_CommitsImmediately()
        {
            _options.PushToTalkPolicy.ReleaseTailMs = 0;

            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "stt:False", "mic:True", "force-user-stopped-speaking", "stt:True" }));
        }

        [Test]
        public void Release_WithServerSttToggleDisabled_LeavesBackendTranscriptionAlone()
        {
            _options.PushToTalkPolicy.EnableServerSttToggle = false;
            _options.PushToTalkPolicy.ReleaseTailMs = 0;

            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.SttMutedStates, Is.Empty);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "mic:True", "force-user-stopped-speaking" }));
        }

        [Test]
        public void Press_WhenServerSttCannotBeEnabled_RejectsWithoutOpeningMicrophone()
        {
            _audio.SetMicMuted(true);
            _turnBoundaryOperations.Clear();
            _connection.SetSttMutedResult = false;

            Assert.IsFalse(_controller.Press());

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_controller.IsPressed, Is.False);
            Assert.That(_controller.BlockedReason, Does.Contain("speech recognition"));
            Assert.That(_turnBoundaryOperations, Is.EqualTo(new[] { "stt:False" }));
        }

        [Test]
        public void Release_WhenInitialStopAttemptFails_RetriesAtTerminalBoundaryAndSendsOnce()
        {
            _options.PushToTalkPolicy.ReleaseTailMs = 100;
            _connection.ForceUserStoppedSpeakingResult = false;

            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            _controller.ExpireReleaseTailForTests();

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(_connection.SuccessfulForceUserStoppedSpeakingCallCount, Is.Zero);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false }));
            Assert.That(_controller.HasPendingReleaseForTests, Is.True);

            PublishPlayerTranscript(TranscriptionPhase.AsrFinal);

            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1),
                "A failed initial stop should retry only at the existing terminal boundary.");
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false }));
            Assert.That(_controller.HasPendingReleaseForTests, Is.True);

            _connection.ForceUserStoppedSpeakingResult = true;
            _controller.ExpireReleaseTailForTests();

            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(2));
            Assert.That(_connection.SuccessfulForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false, true }));
            Assert.That(_controller.HasPendingReleaseForTests, Is.False);
        }

        [Test]
        public void Release_WhenEveryStopAttemptFails_CleansUpAndAllowsAnotherTurn()
        {
            _options.PushToTalkPolicy.ReleaseTailMs = 100;
            _connection.ForceUserStoppedSpeakingResult = false;

            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            _controller.ExpireReleaseTailForTests();
            _controller.ExpireReleaseTailForTests();

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(2));
            Assert.That(_connection.SuccessfulForceUserStoppedSpeakingCallCount, Is.Zero);
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false, true }));
            Assert.That(_controller.HasPendingReleaseForTests, Is.False);
            Assert.That(_controller.HasReleaseTailCoroutineForTests, Is.False);
            Assert.That(_controller.IsAwaitingTurnCompletion, Is.False);
            Assert.That(_controller.BlockedReason, Does.Contain("could not signal"));

            _connection.ForceUserStoppedSpeakingResult = true;
            _connection.SetSttMutedResult = true;
            Assert.IsTrue(_controller.Press());
        }

        [Test]
        public void Release_WithoutPress_DoesNotEndTheTurn()
        {
            Assert.IsFalse(_controller.Release());

            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.Zero);
        }

        [Test]
        public void ConversationInputModeTransition_WhilePressed_CommitsAndClosesCapture()
        {
            Assert.IsTrue(_controller.Press());

            bool prepared = _controller.PrepareForConversationInputModeTransition(
                TurnTakingOptionsResolver.ResolveFromSource(TurnTakingOptions.CreateHandsFreeDefault()),
                "test:handsfree-switch");

            Assert.That(prepared, Is.True);
            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "stt:False", "mic:True", "force-user-stopped-speaking", "stt:True" }));
            Assert.That(
                _connection.TurnControlCalls,
                Is.EqualTo(new[] { "stt:False", "force-user-stopped-speaking", "stt:True" }));
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(_controller.IsPressed, Is.False);
        }

        [Test]
        public void ConversationTargetRoutingPreservesOpenOnFirstPressPublicationState()
        {
            _options.LocalAudioPolicy.PushToTalkStartupMode =
                PushToTalkMicStartupMode.OpenOnFirstPress;
            Assert.IsTrue(_controller.Press());
            Assert.That(_audio.StartListeningCallCount, Is.EqualTo(1));

            bool prepared = _controller.PrepareForConversationTargetRouting(
                "test:conversation-target-routing");
            Assert.That(prepared, Is.True);
            Assert.That(_controller.IsPressed, Is.False);

            Assert.IsTrue(_controller.Press());
            Assert.That(_audio.StartListeningCallCount, Is.EqualTo(1),
                "A route change must not republish a microphone already opened in this session.");
        }

        /// <summary>
        ///     Routing to the character that is already the target must leave its speaking guard
        ///     standing.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The guard is <c>_targetSpeaking</c>, and this used to be asserted through
        ///         <see cref="ConvaiPushToTalkController.Press" /> returning false. That is a proxy
        ///         for the guard, and under the shipped policy it is the wrong one: with
        ///         <c>InterruptBotOnPress</c> on — which is the default — a press against a busy
        ///         target interrupts it and succeeds, so <c>Press()</c> returns true with the guard
        ///         perfectly intact. The assertion was measuring the interrupt policy, not the
        ///         guard, and it went red without anything being wrong.
        ///     </para>
        ///     <para>
        ///         So the guard is read directly, and then made to bite: with the interrupt turned
        ///         off, refusing the press is the observable thing the guard is for.
        ///     </para>
        /// </remarks>
        [Test]
        public void PreparedRoutingWithoutCanonicalMovePreservesCharacterTurnGuard()
        {
            SetPrivateField(_controller, "_targetSpeaking", true);

            bool prepared = _controller.PrepareForConversationTargetRouting(
                "test:no-op-target",
                preservesCurrentTurnBoundary: true);

            Assert.That(prepared, Is.True);
            Assert.That(GetPrivateField<bool>(_controller, "_targetSpeaking"), Is.True,
                "A successful request for the already-active target must not erase its speaking guard.");

            _options.PushToTalkPolicy.InterruptBotOnPress = false;
            _options.PushToTalkPolicy.RequireTurnCompletionBeforeNextPress = true;
            Assert.That(_controller.Press(), Is.False,
                "And the guard still decides: with the interrupt off, it is what refuses the press.");
            Assert.That(_controller.BlockedReason, Does.Contain("waiting"));
        }

        [Test]
        public void CanonicalTargetMoveClearsPreviousCharacterTurnGuard()
        {
            SetPrivateField(_controller, "_targetSpeaking", true);
            _controller.PrepareForConversationTargetRouting(
                "test:target-send",
                preservesCurrentTurnBoundary: true);

            _controller.CommitConversationTargetChange("test:target-confirmed");

            Assert.That(_controller.Press(), Is.True,
                "Only an authoritative move may release the previous character's turn guard.");
        }

        [Test]
        public void ReenableAfterMissedDisconnectRepublishesOpenOnFirstPressMicrophone()
        {
            _options.LocalAudioPolicy.PushToTalkStartupMode =
                PushToTalkMicStartupMode.OpenOnFirstPress;
            Assert.That(_controller.Press(), Is.True);
            Assert.That(_audio.StartListeningCallCount, Is.EqualTo(1));

            InvokePrivateMethod(_controller, "OnDisable");
            _connection.DisconnectAsync();
            _connection.ConnectAsync();
            InvokePrivateMethod(_controller, "OnEnable");

            Assert.That(_controller.Press(), Is.True);
            Assert.That(_audio.StartListeningCallCount, Is.EqualTo(2),
                "A disabled controller can miss the room boundary, so re-enable must re-ensure publication.");
        }

        [Test]
        public void AbortActiveCapture_WhileReleaseIsPending_ClosesCaptureAndCommitsOnce()
        {
            Assert.IsTrue(_controller.Press());
            Assert.IsTrue(_controller.Release());

            _controller.AbortActiveCaptureForTests();

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
            Assert.That(_connection.SttMutedStates, Is.EqualTo(new[] { false, true }));
            Assert.That(
                _turnBoundaryOperations,
                Is.EqualTo(new[] { "stt:False", "mic:True", "force-user-stopped-speaking", "stt:True" }));
        }

        [Test]
        public void ConversationInputModeTransition_WhenStopIsRejected_ReportsFailureAndLeavesMicClosed()
        {
            Assert.IsTrue(_controller.Press());
            _connection.ForceUserStoppedSpeakingResult = false;

            bool prepared = _controller.PrepareForConversationInputModeTransition(
                TurnTakingOptionsResolver.ResolveFromSource(TurnTakingOptions.CreateHandsFreeDefault()),
                "test:handsfree-switch");

            Assert.That(prepared, Is.False);
            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_controller.IsPressed, Is.False);
            Assert.That(_controller.BlockedReason, Does.Contain("could not signal"));
        }

        [Test]
        public void Disable_WithoutRuntimeDependencies_StillClosesCachedLocalMicrophone()
        {
            Assert.IsTrue(_controller.Press());
            SetPrivateField(_controller, "_manager", null);
            SetPrivateField(_controller, "_eventHub", null);

            InvokePrivateMethod(_controller, "OnDisable");

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_controller.IsPressed, Is.False);
            Assert.That(_controller.HasPendingReleaseForTests, Is.False);
            Assert.That(_controller.HasReleaseTailCoroutineForTests, Is.False);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.EqualTo(1));
        }

        [TestCase(SessionState.Disconnected)]
        [TestCase(SessionState.Error)]
        public void SessionEnds_WhilePressed_ClosesLocalCaptureAndClearsController(SessionState terminalState)
        {
            Assert.IsTrue(_controller.Press());

            _eventHub.Publish(SessionStateChanged.Create(
                SessionState.Connected,
                terminalState,
                "mock-session"));

            Assert.That(_audio.IsMicMuted, Is.True);
            Assert.That(_controller.IsPressed, Is.False);
            Assert.That(_controller.IsAwaitingTurnCompletion, Is.False);
            Assert.That(_connection.ForceUserStoppedSpeakingCallCount, Is.Zero);
            Assert.That(_turnBoundaryOperations, Is.EqualTo(new[] { "stt:False", "mic:True" }));
        }

        /// <summary>
        ///     Holding the talk control publishes a local rising edge, and every path that drops
        ///     the press has to publish the falling one.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <see cref="ConvaiPushToTalkController.Release" /> refuses to run once
        ///         <c>IsPressed</c> is false, so a press cleared quietly by one of these paths is
        ///         never followed by a falling edge from anywhere: the player's physical release is
        ///         rejected, and every listener goes on believing the control is held. Routing is
        ///         the visible case — the button is still down while the conversation moves, and
        ///         the character it moves to reads as being addressed.
        ///     </para>
        /// </remarks>
        [Test]
        public void RoutingAwayWhileHeld_LetsGoOfThePress()
        {
            var activity = new List<bool>();
            _eventHub.Subscribe<LocalPlayerActivityChanged>(e => activity.Add(e.IsActive));

            Assert.IsTrue(_controller.Press());
            Assert.That(activity, Is.EqualTo(new[] { true }));

            _controller.PrepareForConversationTargetRouting("test:route-away-while-held");

            Assert.That(activity, Is.EqualTo(new[] { true, false }),
                "The conversation moved while the button was down; the hold has to end with it.");
            Assert.That(_controller.Release(), Is.False,
                "And nothing else will say it: the physical release is refused from here.");
        }

        [TestCase(SessionState.Disconnected)]
        [TestCase(SessionState.Error)]
        public void SessionEndingWhileHeld_LetsGoOfThePress(SessionState terminalState)
        {
            var activity = new List<bool>();
            _eventHub.Subscribe<LocalPlayerActivityChanged>(e => activity.Add(e.IsActive));

            Assert.IsTrue(_controller.Press());
            Assert.That(activity, Is.EqualTo(new[] { true }));

            _eventHub.Publish(SessionStateChanged.Create(
                SessionState.Connected,
                terminalState,
                "mock-session"));

            Assert.That(activity, Is.EqualTo(new[] { true, false }),
                "A session that ends under a held button still ends the hold.");
        }

        private void PublishPlayerTranscript(TranscriptionPhase phase)
        {
            _eventHub.Publish(PlayerTranscriptReceived.Create(
                "player",
                "Player",
                "test",
                phase == TranscriptionPhase.AsrFinal,
                phase));
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing private field {fieldName}.");
            return (T)field.GetValue(target);
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing private field {fieldName}.");
            field.SetValue(target, value);
        }

        private static void InvokePrivateMethod(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Missing private method {methodName}.");
            method.Invoke(target, null);
        }
    }
}
