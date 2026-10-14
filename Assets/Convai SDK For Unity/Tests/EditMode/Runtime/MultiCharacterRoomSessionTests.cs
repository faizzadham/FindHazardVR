using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Infrastructure.Protocol.Messages;
using Convai.RestAPI.Internal;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.Room;
using Convai.Tests.EditMode.Fixtures;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    public sealed class MultiCharacterRoomSessionTests
    {
        [Test]
        public void ObservingANewerRosterEpochPublishesExactlyOneAdvance()
        {
            MultiCharacterRoomSession session = CreateSession();
            var observed = new List<int>();
            session.RosterEpochChanged += observed.Add;

            session.ObserveRosterEpoch(3);
            session.ObserveRosterEpoch(2);
            session.ObserveRosterEpoch(3);

            Assert.That(session.RosterEpoch, Is.EqualTo(3));
            Assert.That(observed, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void RetiringASessionPublishesExactlyOnce()
        {
            MultiCharacterRoomSession session = CreateSession();
            int retired = 0;
            session.Retired += () => retired++;

            session.Retire();
            session.Retire();

            Assert.That(retired, Is.EqualTo(1));
        }

        [Test]
        public async Task InitialMembershipAloneControlsEffectiveReadiness()
        {
            MultiCharacterRoomSession session = CreateSession();

            CharacterRoomMembership secondary = session.FindByMembershipId("membership-2");
            session.MarkReady(secondary, "participant-2");
            Assert.That(session.IsReady, Is.False);

            CharacterRoomMembership initial = session.FindByMembershipId("membership-1");
            session.MarkReady(initial, "participant-1");
            await session.WaitUntilReadyAsync(CancellationToken.None);

            Assert.That(session.IsReady, Is.True);
            Assert.That(initial.ParticipantId, Is.EqualTo("participant-1"));
        }

        [Test]
        public void ReadyEventWithoutParticipantIdPreservesExistingParticipantBinding()
        {
            MultiCharacterRoomSession session = CreateSession();
            CharacterRoomMembership initial = session.FindByMembershipId("membership-1");
            session.BindParticipant(initial, "participant-1");

            session.MarkReady(initial, string.Empty);

            Assert.That(initial.ParticipantId, Is.EqualTo("participant-1"));
            Assert.That(session.Resolve(null, null, "participant-1"), Is.SameAs(initial));
        }

        [Test]
        public async Task InteractionTargetAckAdvancesCanonicalEpoch()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<InteractionTargetResult> pending = session.RegisterTargetCommand(
                "command-1",
                CancellationToken.None);

            session.CompleteTargetCommand(
                "command-1",
                "success",
                null,
                "membership-2",
                "membership-1",
                1,
                true);

            InteractionTargetResult result = await pending;
            Assert.That(result.Changed, Is.True);
            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-2"));
            Assert.That(session.RouteEpoch, Is.EqualTo(1));
        }

        [Test]
        public async Task CancelledTargetWaitStillReconcilesALateAcknowledgement()
        {
            MultiCharacterRoomSession session = CreateSession();
            using var cancellation = new CancellationTokenSource();
            InteractionTargetCommandRegistration registration = session.RegisterTrackedTargetCommand(
                "command-late-target",
                cancellation.Token);
            Task<InteractionTargetResult> callerWait = registration.CallerCompletion;

            cancellation.Cancel();
            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(
                callerWait,
                "Cancelling a caller must release only that caller's wait.");

            session.CompleteTargetCommand(
                "command-late-target",
                "success",
                null,
                "membership-2",
                "membership-1",
                1,
                true);

            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-2"),
                "The late service acknowledgement is still authoritative after the caller leaves.");
            Assert.That(session.RouteEpoch, Is.EqualTo(1));
            InteractionTargetResult canonical = await registration.CanonicalCompletion;
            Assert.That(canonical.ActiveMembershipId, Is.EqualTo("membership-2"),
                "The canonical completion remains observable after the caller abandons its wait.");
        }

        [Test]
        public async Task SupersededNoAckTargetIsRemovedWithoutApplyingLateState()
        {
            MultiCharacterRoomSession session = CreateSession();
            InteractionTargetCommandRegistration registration = session.RegisterTrackedTargetCommand(
                "command-superseded-target",
                CancellationToken.None);
            session.ApplyInteractionTarget("membership-2", 1);

            CanonicalRoutingTimeoutDisposition disposition = session.ClaimTargetCommandTimeout(
                "command-superseded-target",
                commandRouteEpoch: 0,
                reason: new TimeoutException("A newer authoritative route superseded this command."));

            Assert.That(disposition, Is.EqualTo(CanonicalRoutingTimeoutDisposition.Superseded));
            await AsyncTestDeadline.ThrowsWithinAsync<TimeoutException>(
                registration.CanonicalCompletion,
                "A superseded raw command must complete instead of leaking until disconnect.");
            Assert.That(session.PendingTargetCommandCount, Is.Zero);

            session.CompleteTargetCommand(
                "command-superseded-target",
                "success",
                null,
                "membership-1",
                "membership-2",
                2,
                true);

            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-2"),
                "A response retired after a newer canonical route must not apply stale state.");
        }

        [Test]
        public async Task AckAtCanonicalDeadlineWinsAtomicClaimAndKeepsRoomUsable()
        {
            MultiCharacterRoomSession session = CreateSession();
            InteractionTargetCommandRegistration registration = session.RegisterTrackedTargetCommand(
                "command-deadline-ack",
                CancellationToken.None);

            session.CompleteTargetCommand(
                "command-deadline-ack",
                "success",
                null,
                "membership-2",
                "membership-1",
                1,
                true);
            CanonicalRoutingTimeoutDisposition disposition = session.ClaimTargetCommandTimeout(
                "command-deadline-ack",
                commandRouteEpoch: 0,
                reason: new TimeoutException("deadline"));

            InteractionTargetResult result = await registration.CanonicalCompletion;
            Assert.That(disposition, Is.EqualTo(CanonicalRoutingTimeoutDisposition.AlreadyResolved),
                "Once the acknowledgement owns the command, timeout recovery must be a no-op.");
            Assert.That(result.ActiveMembershipId, Is.EqualTo("membership-2"));

            Task<InteractionTargetResult> next = session.RegisterTargetCommand(
                "command-after-deadline",
                CancellationToken.None);
            session.CompleteTargetCommand(
                "command-after-deadline",
                "success",
                null,
                "membership-1",
                "membership-2",
                2,
                true);
            Assert.That((await next).ActiveMembershipId, Is.EqualTo("membership-1"),
                "A reconciled deadline race must not retire or poison the room.");
        }

        [Test]
        public async Task TimeoutClaimBeforeAckPreventsLateStateFromApplying()
        {
            MultiCharacterRoomSession session = CreateSession();
            InteractionTargetCommandRegistration registration = session.RegisterTrackedTargetCommand(
                "command-timeout-wins",
                CancellationToken.None);
            var timeout = new TimeoutException("deadline");

            CanonicalRoutingTimeoutDisposition disposition = session.ClaimTargetCommandTimeout(
                "command-timeout-wins",
                commandRouteEpoch: 0,
                timeout);
            session.CompleteTargetCommand(
                "command-timeout-wins",
                "success",
                null,
                "membership-2",
                "membership-1",
                1,
                true);

            Assert.That(disposition, Is.EqualTo(CanonicalRoutingTimeoutDisposition.RequiresRecovery));
            await AsyncTestDeadline.ThrowsWithinAsync<TimeoutException>(
                registration.CanonicalCompletion,
                "The claimed timeout must fault the canonical command before recovery.");
            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-1"),
                "An acknowledgement that loses the atomic claim cannot mutate an ambiguous room.");
        }

        [Test]
        public async Task OlderInteractionTargetAckDoesNotRegressCanonicalRoute()
        {
            MultiCharacterRoomSession session = CreateSession();
            session.ApplyInteractionTarget("membership-2", 2);
            Task<InteractionTargetResult> pending = session.RegisterTargetCommand(
                "command-stale-target",
                CancellationToken.None);

            session.CompleteTargetCommand(
                "command-stale-target",
                "success",
                null,
                "membership-1",
                "membership-2",
                1,
                true);

            InteractionTargetResult result = await pending;
            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-2"));
            Assert.That(session.RouteEpoch, Is.EqualTo(2));
            Assert.That(result.ActiveMembershipId, Is.EqualTo("membership-2"));
            Assert.That(result.RouteEpoch, Is.EqualTo(2));
            Assert.That(result.Changed, Is.False);
        }

        [Test]
        public async Task RejectedInteractionTargetCommandAppliesNewerCanonicalClear()
        {
            MultiCharacterRoomSession session = CreateSession();
            session.ApplyInteractionTarget("membership-2", 2);
            Task<InteractionTargetResult> pending = session.RegisterTargetCommand(
                "command-rejected-after-clear",
                CancellationToken.None);

            session.CompleteTargetCommand(
                "command-rejected-after-clear",
                "error",
                "stale route epoch",
                null,
                "membership-2",
                3,
                true);

            InteractionTargetCommandException error =
                await AsyncTestDeadline.ThrowsWithinAsync<InteractionTargetCommandException>(pending,
                    "A command rejected after a canonical clear must surface the rejection.");
            Assert.That(error?.Message, Is.EqualTo("stale route epoch"));
            Assert.That(error?.CanonicalChanged, Is.True);
            Assert.That(error?.ActiveMembershipId, Is.Empty);
            Assert.That(error?.RouteEpoch, Is.EqualTo(3));
            Assert.That(session.ActiveMembershipId, Is.Empty);
            Assert.That(session.RouteEpoch, Is.EqualTo(3));
        }

        [Test]
        public void ConflictingDuplicateRouteEpochDoesNotReplaceCanonicalTarget()
        {
            MultiCharacterRoomSession session = CreateSession();
            session.ApplyInteractionTarget("membership-2", 2);

            bool applied = session.ApplyInteractionTarget("membership-1", 2);

            Assert.That(applied, Is.False);
            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-2"));
            Assert.That(session.RouteEpoch, Is.EqualTo(2));
        }

        [Test]
        public void InteractionTargetWireMessageUsesEnvelopeIdAndBackendDataContract()
        {
            var message = new RTVIInteractionTarget("command-1", "room-1", "membership-2", 4);
            JObject json = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.That(json["type"]?.Value<string>(), Is.EqualTo("interaction-target"));
            Assert.That(json["id"]?.Value<string>(), Is.EqualTo("command-1"));
            Assert.That(json["data"]?["room_session_id"]?.Value<string>(), Is.EqualTo("room-1"));
            Assert.That(json["data"]?["target_membership_id"]?.Value<string>(), Is.EqualTo("membership-2"));
            Assert.That(json["data"]?["expected_route_epoch"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(json["data"]?["command_id"], Is.Null);
        }

        [Test]
        public async Task ClearInteractionTargetAckRemovesCanonicalTargetAndAdvancesEpoch()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<InteractionTargetResult> pending = session.RegisterTargetCommand(
                "command-clear",
                CancellationToken.None);

            session.CompleteTargetCommand(
                "command-clear",
                "success",
                null,
                null,
                "membership-1",
                1,
                true);

            InteractionTargetResult result = await pending;
            Assert.That(result.ActiveMembershipId, Is.Empty);
            Assert.That(session.ActiveMembershipId, Is.Empty);
            Assert.That(session.RouteEpoch, Is.EqualTo(1));
        }

        [Test]
        public void ClearInteractionTargetWireMessageSerializesExplicitNullMembership()
        {
            var message = new RTVIInteractionTarget("command-clear", "room-1", null, 4);
            JObject json = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.That(json["data"]?["target_membership_id"]?.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["data"]?["expected_route_epoch"]?.Value<int>(), Is.EqualTo(4));
        }

        [Test]
        public void CharacterRosterUpdateWireMessageMatchesReliableBackendContract()
        {
            var message = new RTVICharacterRosterUpdate(
                "command-roster",
                "room-1",
                7,
                new[]
                {
                    new RTVICharacterRosterUpdate.CharacterRosterAddition(
                        "b0000000-0000-4000-8000-000000000003")
                },
                System.Array.Empty<string>(),
                null);
            JObject json = JObject.Parse(JsonConvert.SerializeObject(message));

            Assert.That(json["type"]?.Value<string>(), Is.EqualTo("character-roster-update"));
            Assert.That(json["id"]?.Value<string>(), Is.EqualTo("command-roster"));
            Assert.That(json["data"]?["command_id"]?.Value<string>(), Is.EqualTo("command-roster"));
            Assert.That(json["data"]?["room_session_id"]?.Value<string>(), Is.EqualTo("room-1"));
            Assert.That(json["data"]?["expected_roster_epoch"]?.Value<int>(), Is.EqualTo(7));
            Assert.That(json["data"]?["add"]?[0]?["character_id"]?.Value<string>(),
                Is.EqualTo("b0000000-0000-4000-8000-000000000003"));
            Assert.That(json["data"]?["add"]?[0]?["character_session_id"]?.Type, Is.EqualTo(JTokenType.Null));
            Assert.That(json["data"]?["remove_membership_ids"], Is.Empty);
            Assert.That(json["data"]?["replacement_target_membership_id"]?.Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public async Task StartingEventBeforeAddAckCreatesMembershipExactlyOnce()
        {
            MultiCharacterRoomSession session = CreateSession();
            int addedEvents = 0;
            session.CharacterAdded += _ => addedEvents++;
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-add",
                null,
                null,
                CancellationToken.None);
            RoomCharacterDetails details = CreateMembershipDetails(
                "membership-3",
                "character-3",
                "character-session-3",
                "character:membership-3",
                "starting");

            CharacterRoomMembership lifecycleMembership = session.UpsertMembership(details);
            session.ObserveRosterEpoch(1);
            session.CompleteRosterCommand(
                "command-add",
                "success",
                null,
                null,
                new[] { details },
                null,
                null,
                "membership-1",
                0,
                1);

            CharacterRosterUpdateResult result = await pending;
            Assert.That(session.Characters, Has.Count.EqualTo(3));
            Assert.That(addedEvents, Is.EqualTo(1));
            Assert.That(result.Added, Has.Count.EqualTo(1));
            Assert.That(result.Added[0], Is.SameAs(lifecycleMembership));
            Assert.That(session.RosterEpoch, Is.EqualTo(1));
        }

        [Test]
        public async Task CancelledRosterWaitStillReconcilesALateAcknowledgement()
        {
            MultiCharacterRoomSession session = CreateSession();
            using var cancellation = new CancellationTokenSource();
            Task<CharacterRosterUpdateResult> callerWait = session.RegisterRosterCommand(
                "command-late-roster",
                null,
                null,
                cancellation.Token);
            RoomCharacterDetails added = CreateMembershipDetails(
                "membership-3",
                "character-3",
                "character-session-3",
                "character:membership-3",
                "starting");

            cancellation.Cancel();
            await AsyncTestDeadline.ThrowsWithinAsync<OperationCanceledException>(
                callerWait,
                "Cancelling a caller must not discard canonical roster tracking.");

            session.CompleteRosterCommand(
                "command-late-roster",
                "success",
                null,
                null,
                new[] { added },
                null,
                null,
                "membership-1",
                0,
                1);

            Assert.That(session.FindByMembershipId("membership-3"), Is.Not.Null,
                "The service accepted the character even though the original caller timed out.");
            Assert.That(session.RosterEpoch, Is.EqualTo(1));
        }

        [Test]
        public async Task RemovedEventBeforeAckRemovesMembershipExactlyOnce()
        {
            MultiCharacterRoomSession session = CreateSession();
            int removedEvents = 0;
            session.CharacterRemoved += _ => removedEvents++;
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-remove",
                null,
                "membership-2",
                CancellationToken.None);

            CharacterRoomMembership lifecycleRemoved = session.RemoveMembership("membership-2", 1);
            session.CompleteRosterCommand(
                "command-remove",
                "success",
                null,
                null,
                null,
                new[]
                {
                    CreateMembershipDetails(
                        "membership-2",
                        "character-2",
                        "character-session-2",
                        "character:membership-2",
                        "removed")
                },
                new[] { "membership-2" },
                "membership-1",
                0,
                1);

            CharacterRosterUpdateResult result = await pending;
            Assert.That(session.Characters, Has.Count.EqualTo(1));
            Assert.That(removedEvents, Is.EqualTo(1));
            Assert.That(result.Removed, Has.Count.EqualTo(1));
            Assert.That(result.Removed[0], Is.SameAs(lifecycleRemoved));
            Assert.That(session.RosterEpoch, Is.EqualTo(1));
        }

        [Test]
        public async Task RosterHandoverPublishesOneOldToReplacementTargetEvent()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-handover",
                null,
                "membership-1",
                CancellationToken.None);
            var targetEvents = new List<(string Previous, string Current)>();
            session.InteractionTargetChanged += (previous, current) => targetEvents.Add((
                previous?.MembershipId,
                current?.MembershipId));

            session.CompleteRosterCommand(
                "command-handover",
                "success",
                null,
                null,
                null,
                null,
                new[] { "membership-1" },
                "membership-2",
                1,
                1);
            await pending;

            Assert.That(targetEvents, Is.EqualTo(new[]
            {
                ("membership-1", "membership-2")
            }), "One atomic backend handover must not expose a transient no-target route.");
            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-2"));
        }

        [Test]
        public async Task RosterEpochEventObservesTheFinalRosterAndReplacementTarget()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-handover-epoch",
                null,
                "membership-1",
                CancellationToken.None);
            int rosterSizeAtEpoch = -1;
            string activeMembershipAtEpoch = null;
            session.RosterEpochChanged += _ =>
            {
                rosterSizeAtEpoch = session.Characters.Count;
                activeMembershipAtEpoch = session.ActiveMembershipId;
            };

            session.CompleteRosterCommand(
                "command-handover-epoch",
                "success",
                null,
                null,
                null,
                null,
                new[] { "membership-1" },
                "membership-2",
                1,
                1);
            await pending;

            Assert.That(rosterSizeAtEpoch, Is.EqualTo(1));
            Assert.That(activeMembershipAtEpoch, Is.EqualTo("membership-2"),
                "Epoch observers must not see the transient cleared target from an atomic handover.");
        }

        [Test]
        public async Task LifecycleRemovalBeforeRosterAckStillPublishesOneAtomicHandover()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-handover-lifecycle-first",
                null,
                "membership-1",
                CancellationToken.None);
            var targetEvents = new List<(string Previous, string Current)>();
            string activeSeenByRemovalHandler = null;
            session.InteractionTargetChanged += (previous, current) => targetEvents.Add((
                previous?.MembershipId,
                current?.MembershipId));
            session.CharacterRemoved += _ => activeSeenByRemovalHandler = session.ActiveMembershipId;

            session.RemoveMembership("membership-1", 1);
            Assert.That(targetEvents, Is.Empty,
                "A lifecycle echo owned by the pending handover must not expose A-to-null.");
            Assert.That(activeSeenByRemovalHandler, Is.Null,
                "Removal publication waits until the correlated ACK establishes the replacement.");

            session.CompleteRosterCommand(
                "command-handover-lifecycle-first",
                "success",
                null,
                null,
                null,
                null,
                new[] { "membership-1" },
                "membership-2",
                1,
                1);
            await pending;

            Assert.That(targetEvents, Is.EqualTo(new[]
            {
                ("membership-1", "membership-2")
            }));
            Assert.That(activeSeenByRemovalHandler, Is.EqualTo("membership-2"),
                "CharacterRemoved observers must see the final authoritative target.");
        }

        [Test]
        public async Task SuppressedLifecycleRemovalPublishesOnceAfterNewerTargetWins()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-remove-after-newer-target",
                null,
                "membership-1",
                CancellationToken.None);
            int removedEvents = 0;
            session.CharacterRemoved += _ => removedEvents++;

            session.RemoveMembership("membership-1", 1);
            session.ApplyInteractionTarget("membership-2", 2);
            session.CompleteRosterCommand(
                "command-remove-after-newer-target",
                "success",
                null,
                null,
                null,
                null,
                new[] { "membership-1" },
                "membership-2",
                1,
                1);
            await pending;

            Assert.That(session.ActiveMembershipId, Is.EqualTo("membership-2"));
            Assert.That(removedEvents, Is.EqualTo(1),
                "A newer route can supersede the handover, not the suppressed removal event.");
        }

        [Test]
        public async Task OlderRosterAckDoesNotRegressObservedRosterEpoch()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-stale-roster",
                null,
                null,
                CancellationToken.None);
            session.ObserveRosterEpoch(3);

            session.CompleteRosterCommand(
                "command-stale-roster",
                "success",
                null,
                null,
                null,
                null,
                null,
                "membership-1",
                0,
                2);

            CharacterRosterUpdateResult result = await pending;
            Assert.That(session.RosterEpoch, Is.EqualTo(3));
            Assert.That(result.RosterEpoch, Is.EqualTo(3));
        }

        [Test]
        public async Task OlderRosterAckStillAppliesItsCommandCorrelatedDelta()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-stale-membership",
                null,
                "membership-2",
                CancellationToken.None);
            session.ObserveRosterEpoch(3);

            session.CompleteRosterCommand(
                "command-stale-membership",
                "success",
                null,
                null,
                null,
                null,
                new[] { "membership-2" },
                "membership-1",
                0,
                2);

            CharacterRosterUpdateResult result = await pending;
            Assert.That(session.FindByMembershipId("membership-2"), Is.Null,
                "A later lifecycle epoch is not a full snapshot and cannot erase an accepted " +
                "command-correlated removal.");
            Assert.That(result.Removed, Has.Count.EqualTo(1));
            Assert.That(result.RosterEpoch, Is.EqualTo(3));
        }

        [Test]
        public void RemovingActiveMembershipPublishesTargetClearBeforeRemoval()
        {
            MultiCharacterRoomSession session = CreateSession();
            CharacterRoomMembership initial = session.FindByMembershipId("membership-1");
            var eventOrder = new List<string>();
            CharacterRoomMembership previousTarget = null;
            CharacterRoomMembership currentTarget = initial;
            session.InteractionTargetChanged += (previous, current) =>
            {
                eventOrder.Add("target");
                previousTarget = previous;
                currentTarget = current;
            };
            session.CharacterRemoved += _ => eventOrder.Add("removed");

            session.RemoveMembership("membership-1", 1);

            Assert.That(session.ActiveMembershipId, Is.Empty);
            Assert.That(previousTarget, Is.SameAs(initial));
            Assert.That(currentTarget, Is.Null);
            Assert.That(eventOrder, Is.EqualTo(new[] { "target", "removed" }));
        }

        [Test]
        public async Task RemovingAStartingMembershipFaultsItsReadinessWait()
        {
            MultiCharacterRoomSession session = CreateSession();
            CharacterRoomMembership starting = session.FindByMembershipId("membership-2");
            Task readiness = starting.WaitUntilReadyAsync();

            session.RemoveMembership(starting.MembershipId, 1);

            InvalidOperationException error =
                await AsyncTestDeadline.ThrowsWithinAsync<InvalidOperationException>(
                    readiness,
                    "A character removed while Starting can never become ready.");
            Assert.That(error.Message, Does.Contain("left the room before it became ready"));
        }

        [Test]
        public async Task RemovingTheStartingInitialMembershipFaultsBothReadinessWaits()
        {
            MultiCharacterRoomSession session = CreateSession();
            CharacterRoomMembership initial = session.InitialCharacter;
            Task membershipReadiness = initial.WaitUntilReadyAsync();
            Task roomReadiness = session.WaitUntilReadyAsync();

            session.RemoveMembership(initial.MembershipId, 1);

            await AsyncTestDeadline.ThrowsWithinAsync<InvalidOperationException>(
                membershipReadiness,
                "The removed initial membership cannot become ready.");
            InvalidOperationException roomError =
                await AsyncTestDeadline.ThrowsWithinAsync<InvalidOperationException>(
                    roomReadiness,
                    "The room cannot become ready after its Starting initial membership leaves.");
            Assert.That(roomError.Message, Does.StartWith("Initial character"));
        }

        [Test]
        public void MembershipIdentityUpdateRemovesStaleLookupKey()
        {
            MultiCharacterRoomSession session = CreateSession();
            CharacterRoomMembership initial = session.FindByMembershipId("membership-1");
            RoomCharacterDetails updated = CreateMembershipDetails(
                "membership-1",
                "character-1",
                "character-session-1",
                "character:membership-1-updated",
                "ready");

            session.UpsertMembership(updated);

            Assert.That(session.Resolve(null, "character:membership-1", null), Is.Null);
            Assert.That(session.Resolve(null, "character:membership-1-updated", null), Is.SameAs(initial));
        }

        [Test]
        public async Task RosterErrorSurfacesSafeBackendCode()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-error",
                null,
                null,
                CancellationToken.None);

            session.CompleteRosterCommand(
                "command-error",
                "error",
                "Roster update rejected",
                "roster_epoch_mismatch",
                null,
                null,
                null,
                "membership-1",
                0,
                0);

            CharacterRosterUpdateException error =
                await AsyncTestDeadline.ThrowsWithinAsync<CharacterRosterUpdateException>(pending,
                    "A rejected roster update must surface the backend code.");
            Assert.That(error?.Code, Is.EqualTo("roster_epoch_mismatch"));
        }

        [Test]
        public async Task ErrorWithoutEchoedCommandIdCompletesTheOnlySerializedMutation()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task<CharacterRosterUpdateResult> pending = session.RegisterRosterCommand(
                "command-only",
                null,
                null,
                CancellationToken.None);

            string resolvedCommandId = session.ResolvePendingRosterCommandId(null);
            session.CompleteRosterCommand(
                resolvedCommandId,
                "error",
                "Sender is not authorized for this room",
                "unauthorized_sender",
                null,
                null,
                null,
                "membership-1",
                0,
                0);

            CharacterRosterUpdateException error =
                await AsyncTestDeadline.ThrowsWithinAsync<CharacterRosterUpdateException>(pending,
                    "The only serialized mutation must be completed by an unechoed error.");
            Assert.That(resolvedCommandId, Is.EqualTo("command-only"));
            Assert.That(error?.Code, Is.EqualTo("unauthorized_sender"));
        }

        [Test]
        public async Task RetiredNoAckRosterCommandDoesNotPoisonCommandIdFallback()
        {
            MultiCharacterRoomSession session = CreateSession();
            CharacterRosterCommandRegistration orphan = session.RegisterTrackedRosterCommand(
                "command-orphan",
                null,
                null,
                CancellationToken.None);
            CanonicalRoutingTimeoutDisposition disposition = session.ClaimRosterCommandTimeout(
                "command-orphan",
                new TimeoutException("The ambiguous room was retired."));

            Assert.That(disposition, Is.EqualTo(CanonicalRoutingTimeoutDisposition.RequiresRecovery));
            await AsyncTestDeadline.ThrowsWithinAsync<TimeoutException>(
                orphan.CanonicalCompletion,
                "A no-ack roster command must be removed from canonical tracking.");
            Assert.That(session.PendingRosterCommandCount, Is.Zero);

            Assert.That(session.ResolvePendingRosterCommandId(null), Is.Empty,
                "The no-ack entry must no longer poison command-id fallback before retirement.");
        }

        [Test]
        public async Task RetiringSessionCompletesReadinessAndCommandWaitersImmediately()
        {
            MultiCharacterRoomSession session = CreateSession();
            Task readiness = session.WaitUntilReadyAsync();
            Task<InteractionTargetResult> target = session.RegisterTargetCommand(
                "command-target",
                CancellationToken.None);
            Task<CharacterRosterUpdateResult> roster = session.RegisterRosterCommand(
                "command-roster",
                null,
                null,
                CancellationToken.None);

            session.Retire();

            await AssertRetiredAsync(readiness);
            await AssertRetiredAsync(target);
            await AssertRetiredAsync(roster);
            Assert.That(session.IsReady, Is.False);
        }

        [Test]
        public async Task RetiredSessionRejectsNewCommandsAndReadiness()
        {
            MultiCharacterRoomSession session = CreateSession();
            session.Retire();

            Task readiness = session.WaitUntilReadyAsync();
            Task<InteractionTargetResult> target = session.RegisterTargetCommand(
                "command-late-target",
                CancellationToken.None);
            Task<CharacterRosterUpdateResult> roster = session.RegisterRosterCommand(
                "command-late-roster",
                null,
                null,
                CancellationToken.None);

            await AssertRetiredAsync(readiness);
            await AssertRetiredAsync(target);
            await AssertRetiredAsync(roster);
        }

        [Test]
        public async Task RegistryClearRetiresCurrentSession()
        {
            var registry = new AgentRegistry();
            MultiCharacterRoomSession session = registry.Configure(CreateRoomDetails(), null);
            Task<InteractionTargetResult> pending = session.RegisterTargetCommand(
                "command-clear-registry",
                CancellationToken.None);

            registry.ClearMultiCharacterSession();

            Assert.That(registry.CurrentMultiCharacterSession, Is.Null);
            await AssertRetiredAsync(pending);
        }

        [Test]
        public async Task RegistryReplacementRetiresPreviousSession()
        {
            var registry = new AgentRegistry();
            MultiCharacterRoomSession previous = registry.Configure(CreateRoomDetails(), null);
            Task readiness = previous.WaitUntilReadyAsync();

            MultiCharacterRoomSession current = registry.Configure(CreateRoomDetails("room-2"), null);

            Assert.That(registry.CurrentMultiCharacterSession, Is.SameAs(current));
            await AssertRetiredAsync(readiness);
        }

        [Test]
        public void OldRecoveryCannotClaimOrClearAReplacementSession()
        {
            var registry = new AgentRegistry();
            MultiCharacterRoomSession previous = registry.Configure(CreateRoomDetails("room-old"), null);
            MultiCharacterRoomSession current = registry.Configure(CreateRoomDetails("room-current"), null);

            bool claimed = registry.TryClaimMultiCharacterSessionForRecovery(previous);

            Assert.That(claimed, Is.False);
            Assert.That(registry.CurrentMultiCharacterSession, Is.SameAs(current),
                "An old no-ack observer must never detach the room that replaced its session.");
        }

        private static async Task AssertRetiredAsync(Task task)
        {
            try
            {
                await task;
                Assert.Fail("Expected the retired session task to fail.");
            }
            catch (InvalidOperationException error)
            {
                Assert.That(error.Message, Is.EqualTo("The multi-character room session is no longer active."));
            }
        }

        private static MultiCharacterRoomSession CreateSession() =>
            new(CreateRoomDetails(), null);

        private static RoomDetails CreateRoomDetails(string roomSessionId = "room-1")
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
              },
              {
                'membership_id':'membership-2',
                'character_id':'character-2',
                'session_id':'session-2',
                'character_session_id':'character-session-2',
                'participant_identity':'character:membership-2',
                'is_initial':false,
                'provisioning_status':'dispatch_queued'
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

        private static RoomCharacterDetails CreateMembershipDetails(
            string membershipId,
            string characterId,
            string characterSessionId,
            string participantIdentity,
            string provisioningStatus)
        {
            var json = new JObject
            {
                ["membership_id"] = membershipId,
                ["character_id"] = characterId,
                ["character_session_id"] = characterSessionId,
                ["participant_identity"] = participantIdentity,
                ["is_initial"] = false,
                ["provisioning_status"] = provisioningStatus
            };
            return json.ToObject<RoomCharacterDetails>();
        }
    }
}
