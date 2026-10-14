using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Convai.RestAPI.Internal;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.Facades;
using Convai.Runtime.Room;
using Convai.Tests.EditMode.Mocks;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using Convai.Tests.EditMode.Fixtures;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    /// <summary>
    ///     Covers the state that closes the send window while the conversation is moving.
    /// </summary>
    /// <remarks>
    ///     Both failures this guards against are silent. A window left closed makes the player
    ///     permanently unable to talk with nothing in the Console to explain it; a window reopened
    ///     early loses whatever is sent into it. Neither looks like a fault in the code that caused
    ///     it.
    /// </remarks>
    [TestFixture]
    public sealed class ConversationTargetCommandTrackerTests
    {
        private ConversationTargetCommandTracker _tracker;
        private ConvaiCharacter _sofia;
        private ConvaiCharacter _james;

        [SetUp]
        public void SetUp()
        {
            _tracker = new ConversationTargetCommandTracker();
            _sofia = NewCharacter("Sofia");
            _james = NewCharacter("James");
        }

        [TearDown]
        public void TearDown()
        {
            if (_sofia != null) Object.DestroyImmediate(_sofia.gameObject);
            if (_james != null) Object.DestroyImmediate(_james.gameObject);
        }

        [Test]
        public void NothingIsInFlightBeforeACommandStarts()
        {
            Assert.That(_tracker.IsInFlight, Is.False);
            Assert.That(_tracker.Pending, Is.Null);
        }

        [Test]
        public void ACommandClosesTheWindowAndNamesWhereTheConversationIsGoing()
        {
            _tracker.Begin(_sofia);

            Assert.That(_tracker.IsInFlight, Is.True);
            Assert.That(_tracker.Pending, Is.SameAs(_sofia),
                "The pending target is what the chat prompt reads. Naming the character being "
                + "moved away from would point the player at somebody the message cannot reach.");
        }

        [Test]
        public void CompletingTheCommandThatOwnsTheStateReleasesIt()
        {
            int generation = _tracker.Begin(_sofia);
            _tracker.Complete(generation);

            Assert.That(_tracker.IsInFlight, Is.False);
            Assert.That(_tracker.Pending, Is.Null);
        }

        [Test]
        public void ASupersededCommandCompletingDoesNotReopenTheNewerOnesWindow()
        {
            // The discriminating case, and the one a boolean cannot express. A scripted TalkTo
            // lands on top of an automatic switch; the first round trip returns second. With one
            // flag its completion cleared the newer command's pending target, and the window that
            // stops a message vanishing mid-handover silently reopened.
            int first = _tracker.Begin(_sofia);
            int second = _tracker.Begin(_james);

            _tracker.Complete(first);

            Assert.That(_tracker.IsInFlight, Is.True,
                "The second command is still on its way to the service.");
            Assert.That(_tracker.Pending, Is.SameAs(_james),
                "The conversation is going to James; the first command finishing says nothing "
                + "about that.");

            _tracker.Complete(second);

            Assert.That(_tracker.IsInFlight, Is.False);
            Assert.That(_tracker.Pending, Is.Null);
        }

        [Test]
        public void CompletingTwiceIsHarmless()
        {
            // The release happens in a finally, and a caller is allowed to be careless about how
            // many times it runs.
            int generation = _tracker.Begin(_sofia);
            _tracker.Complete(generation);
            _tracker.Complete(generation);

            Assert.That(_tracker.IsInFlight, Is.False);

            int next = _tracker.Begin(_james);
            _tracker.Complete(generation);

            Assert.That(_tracker.IsInFlight, Is.True,
                "A stale token must not release a command that came after it.");
            _tracker.Complete(next);
            Assert.That(_tracker.IsInFlight, Is.False);
        }

        [Test]
        public void AResetDropsTheWindowAndDisownsWhateverIsStillInFlight()
        {
            // The room going away while a command is out. Nothing is going to answer it, so the
            // window has to open — and the command must not be able to close it again later by
            // completing against the room that replaced it.
            int orphaned = _tracker.Begin(_sofia);
            _tracker.Reset();

            Assert.That(_tracker.IsInFlight, Is.False);
            Assert.That(_tracker.Pending, Is.Null);

            int fresh = _tracker.Begin(_james);
            _tracker.Complete(orphaned);

            Assert.That(_tracker.IsInFlight, Is.True,
                "The orphaned command belongs to a room that no longer exists; it cannot release "
                + "the state the next one claimed.");
            Assert.That(_tracker.Pending, Is.SameAs(_james));

            _tracker.Complete(fresh);
            Assert.That(_tracker.IsInFlight, Is.False);
        }

        [Test]
        public void ScriptedTargetDuringSpeechReplaysOnlyTheMostRecentValidRequest()
        {
            MultiCharacterRoomSession session = CreateSession(_sofia, _james, "room-1");
            var deferral = new ConversationTargetRequestDeferral();

            deferral.Hold(session, "membership-1", _sofia);
            deferral.Hold(session, "membership-2", _james);

            bool replay = deferral.TryTake(
                session,
                out string membershipId,
                out ConvaiCharacter character);

            Assert.That(replay, Is.True);
            Assert.That(membershipId, Is.EqualTo("membership-2"),
                "The latest explicit TalkTo call wins while one utterance pins the route.");
            Assert.That(character, Is.SameAs(_james));
            Assert.That(deferral.IsPending, Is.False);
            Assert.That(deferral.TryTake(session, out _, out _), Is.False,
                "The speaking boundary flushes a held request exactly once.");
        }

        [Test]
        public void ClearingDeferredTargetDisownsTheHeldRequest()
        {
            MultiCharacterRoomSession session = CreateSession(_sofia, _james, "room-1");
            var deferral = new ConversationTargetRequestDeferral();
            deferral.Hold(session, "membership-2", _james);

            Assert.That(deferral.IsPending, Is.True);
            deferral.Clear();
            Assert.That(deferral.IsPending, Is.False,
                "An immediate TalkTo after speech ends must be able to disown the stale held request.");
        }

        [Test]
        public async Task TargetRequestObservationsDoNotShareSameMembershipFailures()
        {
            var older = new ConversationTargetRequestObservation();
            var newer = new ConversationTargetRequestObservation();
            var olderCanonical = new TaskCompletionSource<InteractionTargetResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var newerCanonical = new TaskCompletionSource<InteractionTargetResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            older.Register(olderCanonical.Task);
            newer.Register(newerCanonical.Task);

            olderCanonical.SetException(new System.InvalidOperationException("older command failed"));
            await AsyncTestDeadline.ThrowsWithinAsync<System.InvalidOperationException>(older.Completion,
                "the older observation faults with the older command's failure");
            Assert.That(newer.Completion.IsCompleted, Is.False,
                "A failure from an older same-membership command cannot complete the newer observation.");

            var expected = new InteractionTargetResult(
                "new-command",
                "membership-2",
                "membership-1",
                2,
                true);
            newerCanonical.SetResult(expected);
            InteractionTargetResult actual = await newer.Completion;

            Assert.That(actual.CommandId, Is.EqualTo("new-command"));
            Assert.That(actual.ActiveMembershipId, Is.EqualTo("membership-2"));
        }

        [Test]
        public void DeferredObservationIsLocalAndNotCanonicallyRegistered()
        {
            var observation = new ConversationTargetRequestObservation();
            observation.MarkDeferred();

            Assert.That(observation.IsDeferred, Is.True);
            Assert.That(observation.IsRegistered, Is.False);
            Assert.That(observation.Completion.IsCompleted, Is.False);
        }

        [Test]
        public void ScriptedTargetHeldByAnOldRoomIsNotReplayedAfterReconnect()
        {
            MultiCharacterRoomSession oldSession = CreateSession(_sofia, _james, "room-old");
            MultiCharacterRoomSession currentSession = CreateSession(_sofia, _james, "room-current");
            var deferral = new ConversationTargetRequestDeferral();
            deferral.Hold(oldSession, "membership-2", _james);

            Assert.That(deferral.TryTake(currentSession, out _, out _), Is.False);
            Assert.That(deferral.IsPending, Is.False,
                "A stale scripted request must be discarded rather than carried into a new room.");
        }

        private static ConvaiCharacter NewCharacter(string name) =>
            new GameObject($"ConversationTargetCommandTrackerTests_{name}")
                .AddComponent<ConvaiCharacter>();

        private static MultiCharacterRoomSession CreateSession(
            ConvaiCharacter first,
            ConvaiCharacter second,
            string roomSessionId)
        {
            const string charactersJson = @"[
              {
                'membership_id':'membership-1',
                'character_id':'',
                'session_id':'session-1',
                'participant_identity':'character:membership-1',
                'is_initial':true,
                'provisioning_status':'dispatch_accepted'
              },
              {
                'membership_id':'membership-2',
                'character_id':'',
                'session_id':'session-2',
                'participant_identity':'character:membership-2',
                'is_initial':false,
                'provisioning_status':'dispatch_accepted'
              }
            ]";
            var characters = JsonConvert.DeserializeObject<List<RoomCharacterDetails>>(charactersJson);
            var details = new RoomDetails(
                "token",
                "room-name",
                "session-1",
                "wss://room",
                roomSessionId: roomSessionId,
                activeMembershipId: "membership-1",
                routeEpoch: 0,
                characters: characters);
            return new MultiCharacterRoomSession(
                details,
                new IConvaiCharacterAgent[] { first, second });
        }
    }

    [TestFixture]
    public sealed class ConversationRoutingMicrophoneGateTests
    {
        private ConversationRoutingMicrophoneGate _gate;
        private bool _effectiveMuted;

        [SetUp]
        public void SetUp()
        {
            _gate = new ConversationRoutingMicrophoneGate();
            _effectiveMuted = false;
        }

        [Test]
        public void HandsFreeRoutingMutesImmediatelyAndRestoresAnOpenMicrophone()
        {
            int generation = _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: false,
                muted => _effectiveMuted = muted);

            Assert.That(_effectiveMuted, Is.True,
                "The microphone must close before the target request can leave this frame.");

            _gate.Complete(generation, muted => _effectiveMuted = muted);

            Assert.That(_effectiveMuted, Is.False);
        }

        [Test]
        public void ThrowingMuteApplicationRollsBackSuppressionBeforeLeaseExists()
        {
            int calls = 0;

            Assert.Throws<System.InvalidOperationException>(() => _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: false,
                _ =>
                {
                    calls++;
                    throw new System.InvalidOperationException("subscriber failed");
                }));

            Assert.That(_gate.IsSuppressing, Is.False,
                "No finally can release a generation when Begin itself failed.");
            Assert.That(calls, Is.EqualTo(2),
                "The gate attempts to restore the pre-route effective mute before rethrowing.");
        }

        [Test]
        public void UserMuteDuringRoutingIsPreservedAfterCompletion()
        {
            int generation = _gate.Begin(true, false, muted => _effectiveMuted = muted);

            _gate.SetUserMuted(true, muted => _effectiveMuted = muted);
            _gate.Complete(generation, muted => _effectiveMuted = muted);

            Assert.That(_effectiveMuted, Is.True,
                "Temporary routing suppression must never undo a user mute.");
        }

        [Test]
        public void UserUnmuteDuringRoutingWaitsUntilRoutingCompletes()
        {
            int generation = _gate.Begin(true, true, muted => _effectiveMuted = muted);

            _gate.SetUserMuted(false, muted => _effectiveMuted = muted);
            Assert.That(_effectiveMuted, Is.True,
                "An unmute request cannot transmit into the in-flight routing window.");

            _gate.Complete(generation, muted => _effectiveMuted = muted);
            Assert.That(_effectiveMuted, Is.False);
        }

        [Test]
        public void ToggleDuringRoutingInvertsSavedIntentInsteadOfTemporaryEffectiveMute()
        {
            int generation = _gate.Begin(true, false, muted => _effectiveMuted = muted);
            Assert.That(_effectiveMuted, Is.True);

            bool toggled = !_gate.ResolveUserMuted(_effectiveMuted);
            _gate.SetUserMuted(toggled, muted => _effectiveMuted = muted);
            _gate.Complete(generation, muted => _effectiveMuted = muted);

            Assert.That(toggled, Is.True,
                "A temporarily force-muted open mic must toggle to a saved user mute.");
            Assert.That(_gate.IsUserMuted, Is.True);
            Assert.That(_effectiveMuted, Is.True,
                "The saved mute must remain effective after routing completes.");
        }

        [Test]
        public void SupersededCompletionCannotUnmuteANewerRoutingCommand()
        {
            int first = _gate.Begin(true, false, muted => _effectiveMuted = muted);
            int second = _gate.Begin(true, true, muted => _effectiveMuted = muted);

            _gate.Complete(first, muted => _effectiveMuted = muted);

            Assert.That(_effectiveMuted, Is.True);
            Assert.That(_gate.IsSuppressing, Is.True);

            _gate.Complete(second, muted => _effectiveMuted = muted);
            Assert.That(_effectiveMuted, Is.False);
        }

        [Test]
        public void CallerTimeoutKeepsTextAndHandsFreeAudioClosedUntilLateCanonicalAck()
        {
            var targetTracker = new ConversationTargetCommandTracker();
            int targetGeneration = targetTracker.Begin(null);
            int microphoneGeneration = _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: false,
                muted => _effectiveMuted = muted);
            var routingLease = new ConversationTargetRoutingLease(() =>
            {
                targetTracker.Complete(targetGeneration);
                _gate.Complete(microphoneGeneration, muted => _effectiveMuted = muted);
            });

            routingLease.MarkCanonicalCommandRegistered();
            routingLease.ReleaseAfterCallerCompletion();

            Assert.That(targetTracker.IsInFlight, Is.True,
                "A caller timeout is not an authoritative routing answer.");
            Assert.That(
                ConversationAvailabilityResolver.ApplyRoutingTransition(
                    Convai.Domain.DomainEvents.Session.ConvaiConversationAvailability.Ready,
                    targetTracker.IsInFlight),
                Is.EqualTo(Convai.Domain.DomainEvents.Session.ConvaiConversationAvailability.Preparing),
                "Same-frame text and push-to-talk must remain closed during the late-ack window.");
            Assert.That(_effectiveMuted, Is.True,
                "Hands-free audio must remain muted during the late-ack window.");

            routingLease.ReleaseAfterCanonicalCompletion();

            Assert.That(targetTracker.IsInFlight, Is.False);
            Assert.That(_effectiveMuted, Is.False);
        }

        [Test]
        public void NeverAcknowledgedCommandRetiresAmbiguousRoomBeforeInputReopens()
        {
            var order = new List<string>();
            var targetTracker = new ConversationTargetCommandTracker();
            int targetGeneration = targetTracker.Begin(null);
            int microphoneGeneration = _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: false,
                muted => _effectiveMuted = muted);
            var routingLease = new ConversationTargetRoutingLease(() =>
            {
                order.Add("release");
                targetTracker.Complete(targetGeneration);
                _gate.Complete(microphoneGeneration, muted => _effectiveMuted = muted);
            }, () =>
            {
                order.Add("release");
                targetTracker.Reset();
                _gate.AbortForConnectionRecovery(muted => _effectiveMuted = muted);
            });
            routingLease.MarkCanonicalCommandRegistered();
            routingLease.ReleaseAfterCallerCompletion();

            bool recovered = ConversationTargetAmbiguityRecovery.RecoverIfCurrent(
                isCurrentSession: true,
                commandRouteEpoch: 4,
                currentRouteEpoch: 4,
                markRoomUnavailable: () => order.Add("unavailable"),
                retireSession: () => order.Add("retire"),
                beginDisconnect: () => order.Add("disconnect"));
            routingLease.ReleaseAfterAmbiguousRecovery();

            Assert.That(recovered, Is.True);
            Assert.That(order, Is.EqualTo(new[] { "unavailable", "retire", "disconnect", "release" }),
                "The room must become unavailable before an ambiguous routing gate can reopen.");
            Assert.That(targetTracker.IsInFlight, Is.False);
            Assert.That(_effectiveMuted, Is.True,
                "Lease release must not unmute while transport teardown is still in flight.");
            Assert.That(_gate.IsRecoveryMuted, Is.True);
            Assert.That(_gate.IsUserMuted, Is.False,
                "Fail-closed recovery must not overwrite the player's saved mute preference.");

            _gate.ResetForConnectionBoundary();

            Assert.That(_effectiveMuted, Is.True,
                "Clearing connection-only state must not mutate the transport during teardown.");
            Assert.That(_gate.IsRecoveryMuted, Is.False);
            Assert.That(_gate.IsUserMuted, Is.False);

            _gate.RestoreUserMuteAfterConnectionBoundary(muted => _effectiveMuted = muted);

            Assert.That(_effectiveMuted, Is.False,
                "Hands-free startup must restore the saved preference even without another route.");
            Assert.That(_gate.IsUserMuted, Is.False);
        }

        [Test]
        public void OrdinaryConnectionBoundaryRestoresSavedUnmutedIntentAtNextStartup()
        {
            _gate.SetUserMuted(false, muted => _effectiveMuted = muted);
            _effectiveMuted = true; // Transport teardown force-mutes independently of user intent.

            _gate.ResetForConnectionBoundary();
            Assert.That(_effectiveMuted, Is.True,
                "Reset must not unmute while the old transport is tearing down.");

            _gate.RestoreUserMuteAfterConnectionBoundary(muted => _effectiveMuted = muted);
            Assert.That(_effectiveMuted, Is.False,
                "Hands-free startup must restore user intent after an ordinary reconnect too.");
        }

        [Test]
        public void PrewarmPolicyMuteMakesFirstToggleUnmute()
        {
            _gate.ResetForConnectionBoundary();
            _gate.AcceptPolicyMuteAfterConnectionBoundary();
            _effectiveMuted = true;

            bool toggled = !_gate.ResolveUserMuted(_effectiveMuted);
            _gate.SetUserMuted(toggled, muted => _effectiveMuted = muted);

            Assert.That(toggled, Is.False,
                "The first toggle after a policy mute should ask to open the microphone.");
            Assert.That(_effectiveMuted, Is.False);
        }

        [Test]
        public void NewerAuthoritativeTargetCanReleaseOlderTargetsWithoutReleasingRosterGate()
        {
            var tracker = new ConversationTargetCommandTracker();
            int olderTarget = tracker.Begin(null);
            int roster = tracker.Begin(null);
            int newerTarget = tracker.Begin(null);
            int olderMic = _gate.Begin(true, false, muted => _effectiveMuted = muted);
            int rosterMic = _gate.Begin(true, true, muted => _effectiveMuted = muted);
            int newerMic = _gate.Begin(true, true, muted => _effectiveMuted = muted);
            var newerLease = new ConversationTargetRoutingLease(
                () => { },
                releaseAfterAuthoritativeTargetResponse: () =>
                {
                    tracker.Complete(olderTarget);
                    tracker.Complete(newerTarget);
                    _gate.Complete(olderMic, muted => _effectiveMuted = muted);
                    _gate.Complete(newerMic, muted => _effectiveMuted = muted);
                });

            newerLease.MarkCanonicalCommandRegistered();
            newerLease.ReleaseAfterAuthoritativeTargetResponse();

            Assert.That(tracker.IsInFlight, Is.True,
                "An authoritative target supersedes older target commands, not roster deltas.");
            Assert.That(tracker.Pending, Is.Null);
            Assert.That(_gate.IsSuppressing, Is.True);
            Assert.That(_effectiveMuted, Is.True);

            tracker.Complete(roster);
            _gate.Complete(rosterMic, muted => _effectiveMuted = muted);
            Assert.That(tracker.IsInFlight, Is.False);
            Assert.That(_effectiveMuted, Is.False);
        }

        [Test]
        public void OlderNeverAckRecoveryAlsoClosesANewerUnresolvedRoutingGeneration()
        {
            var targetTracker = new ConversationTargetCommandTracker();
            int firstTargetGeneration = targetTracker.Begin(null);
            int firstMicrophoneGeneration = _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: false,
                muted => _effectiveMuted = muted);
            var firstLease = new ConversationTargetRoutingLease(() =>
            {
                targetTracker.Complete(firstTargetGeneration);
                _gate.Complete(firstMicrophoneGeneration, muted => _effectiveMuted = muted);
            }, () =>
            {
                targetTracker.Reset();
                _gate.AbortForConnectionRecovery(muted => _effectiveMuted = muted);
            });
            firstLease.MarkCanonicalCommandRegistered();
            firstLease.ReleaseAfterCallerCompletion();

            int newerTargetGeneration = targetTracker.Begin(null);
            int newerMicrophoneGeneration = _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: true,
                muted => _effectiveMuted = muted);

            firstLease.ReleaseAfterAmbiguousRecovery();

            Assert.That(targetTracker.IsInFlight, Is.False,
                "Retiring the room must close every command generation in that room.");
            Assert.That(_effectiveMuted, Is.True,
                "An older command's recovery must keep a newer overlapping route muted too.");
            Assert.That(_gate.IsRecoveryMuted, Is.True);

            targetTracker.Complete(newerTargetGeneration);
            _gate.Complete(newerMicrophoneGeneration, muted => _effectiveMuted = muted);

            Assert.That(targetTracker.IsInFlight, Is.False);
            Assert.That(_effectiveMuted, Is.True,
                "Stale completions cannot reopen audio after connection recovery begins.");
        }

        [Test]
        public void OlderNeverAckDoesNotRetireRoomAfterNewerCanonicalAck()
        {
            var order = new List<string>();
            var targetTracker = new ConversationTargetCommandTracker();
            int firstTargetGeneration = targetTracker.Begin(null);
            int firstMicrophoneGeneration = _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: false,
                muted => _effectiveMuted = muted);
            var firstLease = new ConversationTargetRoutingLease(() =>
            {
                targetTracker.Complete(firstTargetGeneration);
                _gate.Complete(firstMicrophoneGeneration, muted => _effectiveMuted = muted);
            });
            firstLease.MarkCanonicalCommandRegistered();
            firstLease.ReleaseAfterCallerCompletion();

            // A later command advances the canonical route epoch and releases the newest gates.
            int newerTargetGeneration = targetTracker.Begin(null);
            int newerMicrophoneGeneration = _gate.Begin(
                shouldSuppress: true,
                isCurrentlyMuted: true,
                muted => _effectiveMuted = muted);
            targetTracker.Complete(newerTargetGeneration);
            _gate.Complete(newerMicrophoneGeneration, muted => _effectiveMuted = muted);

            bool recovered = ConversationTargetAmbiguityRecovery.RecoverIfCurrent(
                isCurrentSession: true,
                commandRouteEpoch: 4,
                currentRouteEpoch: 5,
                markRoomUnavailable: () => order.Add("unavailable"),
                retireSession: () => order.Add("retire"),
                beginDisconnect: () => order.Add("disconnect"));
            if (recovered)
                firstLease.ReleaseAfterAmbiguousRecovery();
            else
                firstLease.ReleaseAfterCanonicalCompletion();

            Assert.That(recovered, Is.False,
                "A newer authoritative route epoch makes the older missing response non-ambiguous.");
            Assert.That(order, Is.Empty,
                "An older observer must not disconnect a room reconciled by a newer response.");
            Assert.That(targetTracker.IsInFlight, Is.False);
            Assert.That(_effectiveMuted, Is.False);
            Assert.That(_gate.IsRecoveryMuted, Is.False);
        }
    }

    [TestFixture]
    public sealed class ConversationRoutingAudioFacadeTests
    {
        [Test]
        public void AudioFacadeDelegatesToggleToSavedIntentAwareService()
        {
            var service = new MockRoomAudioService();
            service.SetMicMuted(true); // Simulates a temporary effective routing mute.
            service.ToggleMicMuteOverride = () => true; // Saved unmuted intent toggles to muted.
            var audio = new ConvaiAudio(service);

            bool muted = audio.ToggleMicMuted();

            Assert.That(muted, Is.True);
            Assert.That(service.ToggleMicMuteCallCount, Is.EqualTo(1),
                "ConvaiManager.Audio must not derive user intent from a force-muted transport.");
        }
    }

    [TestFixture]
    public sealed class CanonicalTargetCommandPublicationTests
    {
        [Test]
        public async Task ReconciledRoutePublishesBeforeRejectedCommandFailureObserverResumes()
        {
            var registry = new AgentRegistry();
            MultiCharacterRoomSession session = registry.Configure(CreateRoomDetails(), null);
            var roomManager = (ConvaiRoomManager)FormatterServices.GetUninitializedObject(
                typeof(ConvaiRoomManager));
            PropertyInfo agentRegistry = typeof(ConvaiRoomManager).GetProperty(
                nameof(ConvaiRoomManager.AgentRegistry),
                BindingFlags.Instance | BindingFlags.Public);
            agentRegistry?.GetSetMethod(nonPublic: true)?.Invoke(roomManager, new object[] { registry });

            var phases = new List<string>();
            roomManager.CanonicalConversationTargetReconciled += (_, _, _) => phases.Add("confirmed");
            var rawOutcome = new TaskCompletionSource<InteractionTargetResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<InteractionTargetResult> publishedOutcome =
                roomManager.ObserveCanonicalTargetCommandAsync(session, rawOutcome.Task);
            Task failureObserver = RecordFailureAsync(publishedOutcome, phases);

            rawOutcome.SetException(new InteractionTargetCommandException(
                "The requested route was rejected.",
                session.ActiveMembershipId,
                session.RouteEpoch,
                canonicalChanged: true));
            await failureObserver;

            CollectionAssert.AreEqual(
                new[] { "confirmed", "failed" },
                phases,
                "The authoritative route carried by a rejection must be visible before its " +
                "request reports Failed.");
        }

        private static async Task RecordFailureAsync(
            Task<InteractionTargetResult> outcome,
            ICollection<string> phases)
        {
            try
            {
                await outcome;
            }
            catch (InteractionTargetCommandException)
            {
                phases.Add("failed");
            }
        }

        private static RoomDetails CreateRoomDetails()
        {
            const string charactersJson = @"[
              {
                'membership_id':'membership-1',
                'character_id':'character-1',
                'session_id':'session-1',
                'character_session_id':'character-session-1',
                'participant_identity':'character:membership-1',
                'is_initial':true,
                'provisioning_status':'dispatch_accepted'
              }
            ]";
            var characters = JsonConvert.DeserializeObject<List<RoomCharacterDetails>>(charactersJson);
            return new RoomDetails(
                "token",
                "room-name",
                "session-1",
                "wss://room",
                roomSessionId: "room-1",
                activeMembershipId: "membership-1",
                routeEpoch: 0,
                characters: characters);
        }
    }

    [TestFixture]
    public sealed class ConversationViewResolutionTests
    {
        private GameObject _managerObject;
        private GameObject _firstCameraObject;
        private GameObject _replacementCameraObject;

        [TearDown]
        public void TearDown()
        {
            if (_managerObject != null) Object.DestroyImmediate(_managerObject);
            if (_firstCameraObject != null) Object.DestroyImmediate(_firstCameraObject);
            if (_replacementCameraObject != null) Object.DestroyImmediate(_replacementCameraObject);
        }

        [Test]
        public void AutomaticMainCameraFallbackFollowsAReplacementCamera()
        {
            _managerObject = new GameObject("ConversationViewResolutionTests_Manager");
            ConvaiManager manager = _managerObject.AddComponent<ConvaiManager>();
            _firstCameraObject = new GameObject("ConversationViewResolutionTests_FirstCamera");
            Camera first = _firstCameraObject.AddComponent<Camera>();
            _replacementCameraObject = new GameObject("ConversationViewResolutionTests_ReplacementCamera");
            Camera replacement = _replacementCameraObject.AddComponent<Camera>();

            Assert.That(manager.ResolveViewTransform(first), Is.SameAs(first.transform));
            Assert.That(manager.ResolveViewTransform(replacement), Is.SameAs(replacement.transform),
                "An automatic fallback is evaluated each time; only an explicitly configured " +
                "camera may remain pinned.");
        }
    }

    [TestFixture]
    public sealed class ConversationTargetCanonicalObserverTests
    {
        private GameObject _managerObject;
        private GameObject _firstCharacterObject;
        private GameObject _secondCharacterObject;

        [TearDown]
        public void TearDown()
        {
            if (_managerObject != null) Object.DestroyImmediate(_managerObject);
            if (_firstCharacterObject != null) Object.DestroyImmediate(_firstCharacterObject);
            if (_secondCharacterObject != null) Object.DestroyImmediate(_secondCharacterObject);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SuccessfulCanonicalAckRaisesPublicChangedForAuthoritativeTarget(
            bool serviceReportedChange)
        {
            _managerObject = new GameObject("ConversationTargetCanonicalObserverTests_Manager");
            ConvaiManager manager = _managerObject.AddComponent<ConvaiManager>();
            _firstCharacterObject = new GameObject("ConversationTargetCanonicalObserverTests_First");
            ConvaiCharacter first = _firstCharacterObject.AddComponent<ConvaiCharacter>();
            _secondCharacterObject = new GameObject("ConversationTargetCanonicalObserverTests_Second");
            ConvaiCharacter second = _secondCharacterObject.AddComponent<ConvaiCharacter>();
            var registry = new AgentRegistry();
            MultiCharacterRoomSession session = registry.Configure(
                CreateRoomDetails("room-current"),
                new IConvaiCharacterAgent[] { first, second });
            BindCurrentSession(manager, registry);
            ConvaiCharacter observed = null;
            manager.ConversationTargetChanged += character => observed = character;

            // This handler runs from the canonical task even if the caller's ten-second wait has
            // already timed out. The service's actual target, not the stale requested value, wins.
            manager.HandleCanonicalConversationTargetResult(
                session,
                new InteractionTargetResult(
                    "command-late",
                    "membership-2",
                    "membership-1",
                    1,
                    serviceReportedChange));

            Assert.That(observed, Is.SameAs(second));
        }

        [Test]
        public void CanonicalAckFromReplacedRoomDoesNotPublishAgainstCurrentRoom()
        {
            _managerObject = new GameObject("ConversationTargetCanonicalObserverTests_Manager");
            ConvaiManager manager = _managerObject.AddComponent<ConvaiManager>();
            _firstCharacterObject = new GameObject("ConversationTargetCanonicalObserverTests_First");
            ConvaiCharacter first = _firstCharacterObject.AddComponent<ConvaiCharacter>();
            _secondCharacterObject = new GameObject("ConversationTargetCanonicalObserverTests_Second");
            ConvaiCharacter second = _secondCharacterObject.AddComponent<ConvaiCharacter>();
            var registry = new AgentRegistry();
            MultiCharacterRoomSession current = registry.Configure(
                CreateRoomDetails("room-current"),
                new IConvaiCharacterAgent[] { first, second });
            BindCurrentSession(manager, registry);
            var stale = new MultiCharacterRoomSession(
                CreateRoomDetails("room-stale"),
                new IConvaiCharacterAgent[] { first, second });
            ConvaiCharacter observed = null;
            manager.ConversationTargetChanged += character => observed = character;

            manager.HandleCanonicalConversationTargetResult(
                stale,
                new InteractionTargetResult(
                    "command-stale",
                    "membership-2",
                    "membership-1",
                    1,
                    true));

            Assert.That(observed, Is.Null,
                "An old session continuation must not announce its target in the replacement room.");

            manager.HandleCanonicalConversationTargetResult(
                current,
                new InteractionTargetResult(
                    "command-current",
                    "membership-2",
                    "membership-1",
                    1,
                    true));
            Assert.That(observed, Is.SameAs(second));
        }

        private static void BindCurrentSession(ConvaiManager manager, AgentRegistry registry)
        {
            ConvaiRoomManager roomManager = manager.GetComponent<ConvaiRoomManager>() ??
                                           manager.gameObject.AddComponent<ConvaiRoomManager>();
            PropertyInfo agentRegistry = typeof(ConvaiRoomManager).GetProperty(
                nameof(ConvaiRoomManager.AgentRegistry),
                BindingFlags.Instance | BindingFlags.Public);
            agentRegistry?.GetSetMethod(nonPublic: true)?.Invoke(roomManager, new object[] { registry });
            FieldInfo managerRoom = typeof(ConvaiManager).GetField(
                "_roomManager",
                BindingFlags.Instance | BindingFlags.NonPublic);
            managerRoom?.SetValue(manager, roomManager);
        }

        private static RoomDetails CreateRoomDetails(string roomSessionId)
        {
            const string charactersJson = @"[
              {
                'membership_id':'membership-1',
                'character_id':'',
                'session_id':'session-1',
                'participant_identity':'character:membership-1',
                'is_initial':true,
                'provisioning_status':'dispatch_accepted'
              },
              {
                'membership_id':'membership-2',
                'character_id':'',
                'session_id':'session-2',
                'participant_identity':'character:membership-2',
                'is_initial':false,
                'provisioning_status':'dispatch_accepted'
              }
            ]";
            var characters = JsonConvert.DeserializeObject<List<RoomCharacterDetails>>(charactersJson);
            var details = new RoomDetails(
                "token",
                "room-name",
                "session-1",
                "wss://room",
                roomSessionId: roomSessionId,
                activeMembershipId: "membership-1",
                routeEpoch: 0,
                characters: characters);
            return details;
        }
    }
}
