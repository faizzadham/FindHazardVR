using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Abstractions;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Emotion;
using Convai.Domain.Errors;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.Models;
using Convai.RestAPI;
using Convai.RestAPI.Internal;
using Convai.RestAPI.Services;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Core.Configuration;
using Convai.Runtime.Core.Registry;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Room;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Fixtures;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class CharacterVersionConnectionTests
    {
        private const string BaseId = "00000000-0000-4000-8000-000000000001";
        private const string OtherId = "00000000-0000-4000-8000-000000000002";
        private const string CaseId = "abcdef01-abcd-4000-8000-abcdef012345";
        private const string UpperCaseId = "ABCDEF01-ABCD-4000-8000-ABCDEF012345";
        private readonly List<GameObject> _objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject value in _objects)
                if (value != null) Object.DestroyImmediate(value);
            _objects.Clear();
        }

        [TestCase("live")]
        [TestCase("1.2")]
        [TestCase("1.2.3")]
        public async Task SingleCharacter_UsesSelectedWireReference_AndRetainsBaseIdentity(string selector)
        {
            var agent = new VersionedAgent { CharacterReference = selector == "live" ? BaseId : BaseId + "-" + selector };
            var harness = new Harness(agent);

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.BaseCharacterId, Is.EqualTo(BaseId));
            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(agent.CharacterReference));
            Assert.That(harness.Context.CharacterId, Is.EqualTo(BaseId));
            Assert.That(harness.Context.CharacterReference, Is.EqualTo(agent.CharacterReference));
            Assert.That(harness.DiagnosticCharacterId, Is.EqualTo(BaseId));
        }

        [Test]
        public async Task CustomAgentWithoutVersionSource_KeepsBareWireAndSessionScope()
        {
            var harness = new Harness(new BareAgent());

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(BaseId));
            Assert.That(harness.Controller.Last.Options.ResolvedCharacterReference, Is.Null);
            Assert.That(harness.Persistence.LastSavedKey,
                Is.EqualTo(SessionResumeScope.CreatePersistenceKey(BaseId, null, RoomJoinOptions.CreateNew())));
        }

        [Test]
        public void PersistenceScope_IsolatesLiveSpecificAndUnspecifiedSelections()
        {
            var keys = new HashSet<string>();
            foreach (string reference in new[] { null, BaseId, BaseId + "-1.0", BaseId + "-2.0" })
            {
                var options = RoomJoinOptions.CreateNew();
                options.ResolvedCharacterReference = reference;
                Assert.That(keys.Add(SessionResumeScope.CreatePersistenceKey(BaseId, "user", options)), Is.True);
            }
        }

        [TestCase(UpperCaseId, CaseId, true)]
        [TestCase(CaseId, CaseId, true)]
        [TestCase(null, null, true)]
        [TestCase(null, CaseId, false)]
        [TestCase(UpperCaseId + "-1.0", CaseId + "-1.0", false)]
        [TestCase(CaseId + "-Release", CaseId + "-release", false)]
        [TestCase(CaseId + "-1.0", CaseId + "-1.0", true)]
        [TestCase(" " + UpperCaseId, " " + CaseId, false)]
        [TestCase("{" + UpperCaseId + "}", "{" + CaseId + "}", false)]
        public void ReferenceComparison_OnlyBareUuidsIgnoreCase(string first, string second, bool expected)
        {
            Assert.That(SessionResumeScope.CharacterReferencesMatch(first, second), Is.EqualTo(expected));
        }

        [TestCase(UpperCaseId, CaseId, true)]
        [TestCase(null, CaseId, false)]
        [TestCase(UpperCaseId + "-1.0", CaseId + "-1.0", false)]
        [TestCase(CaseId + "-Release", CaseId + "-release", false)]
        public void PersistenceScope_NormalizesOnlyBareReferenceCasing(string first, string second, bool sameKey)
        {
            var firstOptions = RoomJoinOptions.CreateNew();
            firstOptions.ResolvedCharacterReference = first;
            var secondOptions = RoomJoinOptions.CreateNew();
            secondOptions.ResolvedCharacterReference = second;

            string firstKey = SessionResumeScope.CreatePersistenceKey(UpperCaseId, "user", firstOptions);
            string secondKey = SessionResumeScope.CreatePersistenceKey(UpperCaseId, "user", secondOptions);

            Assert.That(string.Equals(firstKey, secondKey, StringComparison.Ordinal), Is.EqualTo(sameKey));
            Assert.That(firstOptions.ResolvedCharacterReference, Is.EqualTo(first), "Hashing must not alter wire references.");
            Assert.That(secondOptions.ResolvedCharacterReference, Is.EqualTo(second));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task LiveReferenceCasingChange_PreservesKnownComponentSessionAndRoom(
            bool roster, bool explicitSession)
        {
            ConvaiCharacter character = CreateObject("LiveCasingResume").AddComponent<ConvaiCharacter>();
            SetField(character, "_characterId", UpperCaseId);
            SetField(character, "_enableSessionResume", true);
            const string endUserId = "live-casing-resume-user";
            var harness = new Harness(character)
            {
                Invocation = new RoomSessionConnectOptions { EndUserId = endUserId }
            };
            if (roster)
                harness.Characters = new IConvaiCharacterAgent[] { character, new BareAgent() };
            RoomConnectionAttemptResult initialResult = await harness.Connect();
            Assert.That(initialResult.Succeeded, Is.True, initialResult.Failure.Message);
            string previousSession = character.CharacterSessionId;
            harness.Context = harness.Context.WithDisconnection(DateTime.UtcNow);

            // The inspector saves the API's lowercase current.reference after the first Live connection.
            SetField(character, "_characterVersionReference", CaseId);
            if (explicitSession)
                harness.Invocation = new RoomSessionConnectOptions
                {
                    EndUserId = endUserId, CharacterSessionId = previousSession
                };
            harness.Controller.OnAttempt = _ =>
            {
                Assert.That(character.CharacterSessionId, Is.EqualTo(previousSession));
                return RoomConnectionAttemptResult.Success();
            };

            RoomConnectionAttemptResult result = await harness.Connect();

            Assert.That(result.Succeeded, Is.True, result.Failure.Message);
            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.True);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo(previousSession));
            Assert.That(harness.Context.CharacterReference, Is.EqualTo(CaseId));
            if (roster)
            {
                Assert.That(harness.Controller.Last.Options.ResolvedCharacterRoster[0].CharacterSessionId,
                    Is.EqualTo(previousSession));
                Assert.That(harness.Controller.Last.Options.ResolvedCharacterRoster[0].CharacterId,
                    Is.EqualTo(UpperCaseId));
            }
            else
            {
                Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(CaseId));
                Assert.That(harness.Controller.Last.BaseCharacterId, Is.EqualTo(UpperCaseId));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RecreatedAdapter_LiveReferenceCasingChange_PreservesContextSession(bool explicitSession)
        {
            var agent = new VersionedAgent { CharacterId = UpperCaseId, CharacterReference = CaseId };
            var harness = new Harness(agent)
            {
                Context = new ConnectionContext("previous-room", "previous-session", "room-session", UpperCaseId,
                    DateTime.UtcNow, DateTime.UtcNow, UpperCaseId)
            };
            if (explicitSession)
                harness.Invocation = new RoomSessionConnectOptions { CharacterSessionId = "previous-session" };

            RoomConnectionAttemptResult result = await harness.Connect();

            Assert.That(result.Succeeded, Is.True, result.Failure.Message);
            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.True);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo("previous-session"));
            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(CaseId));
        }

        [Test]
        public async Task LiveReferenceCasingChange_ResumesPersistedSessionWithoutRoomContext()
        {
            var agent = new VersionedAgent { CharacterId = UpperCaseId, CharacterReference = UpperCaseId };
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            string persistenceKey = harness.Persistence.LastSavedKey;
            harness.Context = ConnectionContext.Empty;
            agent.CharacterReference = CaseId;

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo("character-session-1"));
            Assert.That(harness.Persistence.LastSavedKey, Is.EqualTo(persistenceKey));
            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(CaseId));
        }

        [TestCase(UpperCaseId + "-release", CaseId + "-release")]
        [TestCase(CaseId + "-Release", CaseId + "-release")]
        public async Task PinnedReferenceCasingChange_StillRejectsKnownExplicitSession(string first, string second)
        {
            var agent = new VersionedAgent { CharacterId = CaseId, CharacterReference = first };
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(first));
            agent.CharacterReference = second;
            harness.Invocation = new RoomSessionConnectOptions { CharacterSessionId = "character-session-1" };

            RoomConnectionAttemptResult result = await harness.Connect();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Message, Does.Contain("different character version"));
            Assert.That(harness.Controller.Attempts.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task SameSelection_RejoinsRoom_AndResumesSession()
        {
            var agent = new VersionedAgent();
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            harness.Context = harness.Context.WithDisconnection(DateTime.UtcNow);

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.True);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo("character-session-1"));
            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(agent.CharacterReference));
        }

        [Test]
        public async Task ChangedSelection_CreatesNewRoomAndScopedSession_ThenCanResumeOriginalSelection()
        {
            var agent = new VersionedAgent();
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            string liveKey = harness.Persistence.LastSavedKey;
            harness.Context = harness.Context.WithDisconnection(DateTime.UtcNow);
            agent.CharacterReference = BaseId + "-2.0";

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.Null);
            Assert.That(harness.Persistence.LastSavedKey, Is.Not.EqualTo(liveKey));
            harness.Context = harness.Context.WithDisconnection(DateTime.UtcNow);
            agent.CharacterReference = BaseId;

            Assert.That((await harness.Connect()).Succeeded, Is.True);
            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo("character-session-1"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ChangedCharacterId_CreatesNewRoom_WithoutThePreviousCharactersSession(bool reportsSelection)
        {
            BareAgent agent = reportsSelection ? new VersionedAgent() : new BareAgent();
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            harness.Context = harness.Context.WithDisconnection(DateTime.UtcNow);
            agent.CharacterId = OtherId;
            if (agent is VersionedAgent versioned)
                versioned.CharacterReference = OtherId;

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False,
                "the remembered room belongs to the previous character");
            Assert.That(harness.Controller.Last.StoredSessionId, Is.Null,
                "the previous character's conversation must not be resumed as this one's");
            Assert.That(harness.Controller.Last.BaseCharacterId, Is.EqualTo(OtherId));
            Assert.That(harness.Context.CharacterId, Is.EqualTo(OtherId));
        }

        [Test]
        public async Task ClientEndedSession_ChangedCharacterId_WithoutSelection_DoesNotResumePreviousSession()
        {
            var agent = new BareAgent();
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            // What a client-initiated disconnect leaves: no room, but the character session id is kept.
            harness.Context = harness.Context.WithoutRoom();
            agent.CharacterId = OtherId;

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.Null,
                "the previous character's conversation must not be resumed as this one's");
            Assert.That(harness.Controller.Last.BaseCharacterId, Is.EqualTo(OtherId));
        }

        [Test]
        public async Task SameCharacterIdInAnotherCase_WithoutSelection_RejoinsRoom_AndResumesSession()
        {
            var agent = new BareAgent { CharacterId = CaseId };
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            harness.Context = harness.Context.WithDisconnection(DateTime.UtcNow);
            agent.CharacterId = UpperCaseId;

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.True);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo("character-session-1"));
        }

        [Test]
        public async Task RecreatedAdapter_RespectsContextVersionProvenance()
        {
            var harness = new Harness(new VersionedAgent { CharacterReference = BaseId + "-2.0" })
            {
                Context = PreviousContext(BaseId + "-1.0")
            };

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.Null);
        }

        [Test]
        public async Task ChangedSelection_RejectsKnownExplicitSession_ButAllowsUnknownExplicitSession()
        {
            var agent = new VersionedAgent();
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            agent.CharacterReference = BaseId + "-2.0";
            harness.Invocation = new RoomSessionConnectOptions { CharacterSessionId = "character-session-1" };

            RoomConnectionAttemptResult rejected = await harness.Connect();

            Assert.That(rejected.Succeeded, Is.False);
            Assert.That(rejected.Failure.Message, Does.Contain("different character version"));
            Assert.That(harness.Controller.Attempts.Count, Is.EqualTo(1));
            harness.Invocation = new RoomSessionConnectOptions { CharacterSessionId = "external-session" };
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo("external-session"));
            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False);
        }

        [Test]
        public async Task ComponentSelectionChange_ClearsOldSession_ButKeepsFirstUseManualSession()
        {
            ConvaiCharacter character = CreateObject("VersionedCharacter").AddComponent<ConvaiCharacter>();
            SetField(character, "_characterId", BaseId);
            SetField(character, "_enableSessionResume", true);
            character.SetCharacterSessionId("manual-first-use-session");
            var harness = new Harness(character);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            Assert.That(harness.Controller.Last.StoredSessionId, Is.EqualTo("manual-first-use-session"));
            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(character, "_characterVersion", "2.0");
            SetField(character, "_characterVersionReference", BaseId + "-api-release-reference");
            harness.Controller.OnAttempt = _ =>
            {
                Assert.That(character.CharacterSessionId, Is.Empty);
                return RoomConnectionAttemptResult.Success();
            };

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.StoredSessionId, Is.Null);
            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(BaseId + "-api-release-reference"));
            Assert.That(character.CharacterSessionId, Is.EqualTo("character-session-2"));
        }

        [Test]
        public async Task KnownOlderExplicitSession_IsRejectedEvenAfterCurrentSelectionAlreadyConnected()
        {
            var agent = new VersionedAgent();
            var harness = new Harness(agent);
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            agent.CharacterReference = BaseId + "-2.0";
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            harness.Invocation = new RoomSessionConnectOptions { CharacterSessionId = "character-session-1" };

            RoomConnectionAttemptResult result = await harness.Connect();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Message, Does.Contain("different character version"));
            Assert.That(harness.Controller.Attempts.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task RetryAndJoinFallback_FreezeReferenceDespiteSelectionMutation_AndPersistAttemptedSelection()
        {
            var agent = new VersionedAgent();
            var harness = new Harness(agent)
            {
                Context = PreviousContext(agent.CharacterReference),
                Invocation = new RoomSessionConnectOptions { MaxNumParticipants = 4 }
            };
            harness.Controller.OnAttempt = attempt =>
            {
                if (attempt != 1) return RoomConnectionAttemptResult.Success();
                agent.CharacterReference = BaseId + "-2.0";
                return RoomConnectionAttemptResult.Fail(ConnectionFailure.FromException(
                    new TimeoutException("test timeout"), SessionErrorCodes.ConnectionFailed,
                    "test timeout", SessionErrorStage.Runtime, true));
            };

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Attempts.Count, Is.EqualTo(2));
            Assert.That(harness.Controller.Attempts[0].Options.IsJoinRequest, Is.True);
            Assert.That(harness.Controller.Attempts[1].Options.IsJoinRequest, Is.False);
            foreach (Attempt attempt in harness.Controller.Attempts)
                Assert.That(attempt.WireCharacterId, Is.EqualTo(BaseId));
            Assert.That(harness.Context.CharacterReference, Is.EqualTo(BaseId));
            var liveOptions = RoomJoinOptions.CreateNew();
            liveOptions.ResolvedCharacterReference = BaseId;
            Assert.That(harness.Persistence.LastSavedKey,
                Is.EqualTo(SessionResumeScope.CreatePersistenceKey(BaseId, null, liveOptions)));
            harness.Controller.OnAttempt = null;
            Assert.That((await harness.Connect()).Succeeded, Is.True);
            Assert.That(harness.Controller.Last.WireCharacterId, Is.EqualTo(BaseId + "-2.0"));
            Assert.That(harness.Controller.Last.StoredSessionId, Is.Null);
            Assert.That(harness.Controller.Last.Options.IsJoinRequest, Is.False);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task LiveRosterReconnect_PreservesKnownSession_ForImplicitAndExplicitResume(
            bool startWithRoster, bool explicitSession)
        {
            ConvaiCharacter character = CreateObject("LiveResumeCharacter").AddComponent<ConvaiCharacter>();
            SetField(character, "_characterId", BaseId);
            SetField(character, "_enableSessionResume", true);
            var roster = new IConvaiCharacterAgent[]
            {
                character, new BareAgent { CharacterId = "00000000-0000-4000-8000-000000000002" }
            };
            const string endUserId = "live-roster-resume-user";
            var harness = new Harness(character)
            {
                Invocation = new RoomSessionConnectOptions { EndUserId = endUserId }
            };
            if (startWithRoster) harness.Characters = roster;
            RoomConnectionAttemptResult initialResult = await harness.Connect();
            Assert.That(initialResult.Succeeded, Is.True, initialResult.Failure.Message);
            string previousSession = character.CharacterSessionId;
            Assert.That(previousSession, Is.EqualTo("character-session-1"));

            harness.Context = harness.Context.WithDisconnection(DateTime.UtcNow);
            harness.Characters = roster;
            if (explicitSession)
                harness.Invocation = new RoomSessionConnectOptions
                {
                    EndUserId = endUserId, CharacterSessionId = previousSession
                };
            harness.Controller.OnAttempt = _ =>
            {
                Assert.That(character.CharacterSessionId, Is.EqualTo(previousSession),
                    "Building a Live roster must not clear a session for the same reference.");
                return RoomConnectionAttemptResult.Success();
            };

            RoomConnectionAttemptResult result = await harness.Connect();

            Assert.That(result.Succeeded, Is.True, result.Failure.Message);
            Assert.That(harness.Controller.Attempts.Count, Is.EqualTo(2));
            Assert.That(harness.Controller.Last.Options.ResolvedCharacterRoster[0].CharacterSessionId,
                Is.EqualTo(previousSession));
        }

        [TestCase("1.0")]
        [TestCase("released-reference")]
        public async Task InitialRoster_RejectsPinnedSelectionsBeforeController(string selector)
        {
            var agent = new VersionedAgent { CharacterReference = BaseId + "-" + selector };
            var harness = new Harness(agent)
            {
                Characters = new IConvaiCharacterAgent[] { agent, new BareAgent { CharacterId = "second-character" } }
            };

            RoomConnectionAttemptResult result = await harness.Connect();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure.Message, Does.Contain("single-character connection"));
            Assert.That(harness.Controller.Attempts, Is.Empty);
        }

        [TestCase("1.0")]
        [TestCase("released-reference")]
        public async Task DynamicRoster_RejectsPinnedSelectionsBeforeSessionOrNetwork(string selector)
        {
            ConvaiRoomManager manager = CreateObject("VersionedRosterManager").AddComponent<ConvaiRoomManager>();
            var agent = new VersionedAgent { CharacterReference = BaseId + "-" + selector };

            ConvaiOperationException error = await AsyncTestDeadline.ThrowsWithinAsync<ConvaiOperationException>(
                manager.AddCharacterAsync(agent).AsTask(), "Versioned roster additions must fail before sending.");

            Assert.That(error.Message, Does.Contain("single-character connection"));
        }

        [Test]
        public async Task InitialRoster_AllowsLiveCharacters_AndKeepsBareWireIds()
        {
            ConvaiCharacter first = CreateObject("LiveFirst").AddComponent<ConvaiCharacter>();
            ConvaiCharacter second = CreateObject("LiveSecond").AddComponent<ConvaiCharacter>();
            SetField(first, "_characterId", BaseId);
            const string secondId = "00000000-0000-4000-8000-000000000002";
            SetField(second, "_characterId", secondId);
            var harness = new Harness(first)
            {
                Characters = new IConvaiCharacterAgent[] { first, second },
                Invocation = new RoomSessionConnectOptions { EndUserId = "roster-user" }
            };

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.WireCharacterId, Is.Null);
            Assert.That(harness.Controller.Last.Options.ResolvedCharacterRoster[0].CharacterId, Is.EqualTo(BaseId));
            Assert.That(harness.Controller.Last.Options.ResolvedCharacterRoster[1].CharacterId, Is.EqualTo(secondId));
        }

        [Test]
        public void RosterGuard_AllowsCanonicalLiveReferenceWithDifferentUuidCasing()
        {
            const string canonicalId = "abcdef01-abcd-4000-8000-abcdef012345";
            ConvaiCharacter character = CreateObject("CanonicalLive").AddComponent<ConvaiCharacter>();
            SetField(character, "_characterId", canonicalId.ToUpperInvariant());
            SetField(character, "_characterVersionReference", canonicalId);

            Assert.That(character.CharacterReference, Is.EqualTo(canonicalId));
            Assert.DoesNotThrow(() => RoomConnectionRuntimeAdapter.EnsureUnversionedRosterCharacter(character));
        }

        [Test]
        public async Task DynamicRoster_LivePassesVersionGuardAndReachesSessionValidation()
        {
            ConvaiRoomManager manager = CreateObject("LiveRosterManager").AddComponent<ConvaiRoomManager>();
            ConvaiCharacter character = CreateObject("LiveCharacter").AddComponent<ConvaiCharacter>();
            SetField(character, "_characterId", BaseId);

            InvalidOperationException error = await AsyncTestDeadline.ThrowsWithinAsync<InvalidOperationException>(
                manager.AddCharacterAsync(character).AsTask(), "Live is supported and should reach session validation.");

            Assert.That(error.Message, Does.Contain("No multi-character room session"));
        }

        [Test]
        public async Task TopologyFreeJoin_DoesNotSendOrResolveCharacterSelection()
        {
            var agent = new VersionedAgent { ThrowOnReferenceRead = true };
            var harness = new Harness(agent)
            {
                Invocation = new RoomSessionConnectOptions
                {
                    JoinExistingMultiCharacterRoom = true,
                    RoomSessionId = "existing-room-session",
                    EndUserId = "participant"
                }
            };

            Assert.That((await harness.Connect()).Succeeded, Is.True);

            Assert.That(harness.Controller.Last.WireCharacterId, Is.Null);
            Assert.That(harness.Controller.Last.Options.ResolvedCharacterReference, Is.Null);
        }

        private static ConnectionContext PreviousContext(string reference) =>
            new("previous-room", "previous-session", "room-session", BaseId, DateTime.UtcNow,
                DateTime.UtcNow, reference);

        private GameObject CreateObject(string name)
        {
            var value = new GameObject(name);
            value.SetActive(false);
            _objects.Add(value);
            return value;
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        private sealed class Harness
        {
            public readonly CapturingController Controller = new();
            public readonly MemoryPersistence Persistence = new();
            public ConnectionContext Context = ConnectionContext.Empty;
            public IReadOnlyList<IConvaiCharacterAgent> Characters;
            public RoomSessionConnectOptions Invocation;
            public string DiagnosticCharacterId;
            private readonly RoomConnectionRuntimeAdapter _adapter;

            public Harness(IConvaiCharacterAgent character)
            {
                Characters = new[] { character };
                var disconnect = new RoomDisconnectRuntimeAdapter(() => null, () => Controller,
                    (_, _) => { }, (_, _, _) => { }, () => { });
                _adapter = new RoomConnectionRuntimeAdapter(
                    () => SessionState.Disconnected, () => false, () => true, () => 1000, () => true,
                    () => character, () => Characters, new AgentRegistry(), () => Context, value => Context = value,
                    () => ReconnectPolicy.Default, _ => { }, _ => { }, () => Controller,
                    () => ConvaiConnectionType.Audio, () => "https://core.convai.com/connect", () => null, () => null,
                    TurnTakingOptions.CreateHandsFreeDefault, UserVadSettings.CreateDefault, () => null, () => null,
                    () => Invocation, (_, _) => { }, _ => { }, () => Persistence, disconnect, (_, _) => { },
                    (room, session, roomSession, id) =>
                    {
                        DiagnosticCharacterId = id;
                        Context = new ConnectionContext(room, session, roomSession, id, DateTime.UtcNow);
                    }, _ => { });
            }

            public async Task<RoomConnectionAttemptResult> Connect()
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Task<RoomConnectionAttemptResult> task = _adapter.ConnectAsync(deadline.Token);
                await AsyncTestDeadline.WithinAsync(task, "The in-memory connection must complete.");
                return await task;
            }
        }

        private class BareAgent : IConvaiCharacterAgent
        {
            public string CharacterId { get; set; } = BaseId;
            public string CharacterName => "Test Character";
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => true;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext => null;
            public EmotionDetectionMode EmotionDetectionMode => EmotionDetectionMode.Off;
            public void SendTrigger(string name) { }
            public void SendNarrativeEvent(string message) { }
            public void SendNarrativeSpeech(string text) { }
            public void UpdateTemplateKeys(Dictionary<string, string> keys) { }
        }

        private sealed class VersionedAgent : BareAgent, ICharacterVersionSource
        {
            private string _reference = BaseId;
            public bool ThrowOnReferenceRead;
            public string CharacterReference
            {
                get => ThrowOnReferenceRead ? throw new InvalidOperationException("Unexpected selection read.") : _reference;
                set => _reference = value;
            }
        }

        private sealed class MemoryPersistence : ISessionPersistence
        {
            private readonly Dictionary<string, string> _values = new();
            public string LastSavedKey;
            public string LoadSession(string key) => _values.TryGetValue(key, out string value) ? value : null;
            public void SaveSession(string key, string value) { _values[key] = value; LastSavedKey = key; }
            public void ClearSession(string key) => _values.Remove(key);
            public void ClearAllSessions() => _values.Clear();
            public bool HasSession(string key) => _values.ContainsKey(key);
        }

        private sealed class Attempt
        {
            public string BaseCharacterId;
            public string WireCharacterId;
            public string StoredSessionId;
            public RoomJoinOptions Options;
        }

        private sealed class CapturingController : IConvaiRoomController
        {
            public readonly List<Attempt> Attempts = new();
            public Func<int, RoomConnectionAttemptResult> OnAttempt;
            public Attempt Last => Attempts[Attempts.Count - 1];
            public bool HasRoomDetails => true;
            public bool IsConnectedToRoom => true;
            public bool IsMicMuted => false;
            public string SessionID => "room-session";
            public string CharacterSessionID => "character-session-" + Attempts.Count;
            public string RoomName => "room-name";
            public string RoomURL => "wss://room-url";
            public string Token => "token";
            public string ResolvedSpeakerId => string.Empty;
            public string RequestTraceId => string.Empty;
            public string ResolvedEndUserId => string.Empty;
            public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata => null;
            public RTVIHandler RTVIHandler => null;
            public IRoomFacade CurrentRoom => null;
            public event Action OnRoomConnectionSuccessful { add { } remove { } }
            public event Action OnRoomConnectionFailed { add { } remove { } }
            public event Action<bool> OnMicMuteChanged { add { } remove { } }
            public event Action OnRoomReconnecting { add { } remove { } }
            public event Action OnRoomReconnected { add { } remove { } }
            public event Action OnUnexpectedRoomDisconnected { add { } remove { } }
            public event Action<IRemoteAudioTrack, string, string> OnRemoteAudioTrackSubscribed { add { } remove { } }
            public event Action<string, string> OnRemoteAudioTrackUnsubscribed { add { } remove { } }

            public Task<RoomConnectionAttemptResult> InitializeAsync(string connectionType, string coreServerUrl,
                string characterId, string storedSessionId, bool enableSessionResume, string dynamicInfoText,
                bool keepDynamicInfoInContext) => InitializeAsync(connectionType, coreServerUrl, characterId,
                storedSessionId, enableSessionResume, dynamicInfoText, keepDynamicInfoInContext, null);

            public Task<RoomConnectionAttemptResult> InitializeAsync(string connectionType, string coreServerUrl,
                string characterId, string storedSessionId, bool enableSessionResume, string dynamicInfoText,
                bool keepDynamicInfoInContext, RoomJoinOptions joinOptions, CancellationToken cancellationToken = default)
            {
                var request = RoomConnectionRequestFactory.Create(characterId, connectionType, coreServerUrl,
                    storedSessionId, joinOptions?.ResolvedEndUserId, null, null, joinOptions,
                    LipSyncTransportOptions.Disabled);
                JObject wire = JObject.Parse(RoomConnectionRequestTransportSerializer.SerializeForTransport(
                    request, new ConvaiRestClientOptions("test-api-key")));
                Attempts.Add(new Attempt
                {
                    BaseCharacterId = characterId, WireCharacterId = wire["character_id"]?.Value<string>(),
                    StoredSessionId = storedSessionId, Options = joinOptions
                });
                return Task.FromResult(OnAttempt?.Invoke(Attempts.Count) ?? RoomConnectionAttemptResult.Success());
            }

            public void DisconnectFromRoom() { }
            public Task DisconnectFromRoomAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void SetMicMuted(bool mute) { }
            public void ToggleMicMute() { }
            public bool SetCharacterAudioMuted(string characterId, bool mute) => true;
            public bool MuteCharacter(string characterId) => true;
            public bool UnmuteCharacter(string characterId) => true;
            public bool IsCharacterAudioMuted(string characterId) => false;
            public void SetAudioSubscriptionPolicy(Func<string, bool> policy) { }
            public void ApplyRemoteAudioPreference(string characterId, bool enabled) { }
            public void Dispose() { }
        }
    }
}
