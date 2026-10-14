using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Conversation;
using Convai.Runtime.Core.Composition;
using Convai.Runtime.Room;
using Convai.Shared.Compatibility;
using Newtonsoft.Json.Linq;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEngine;

namespace Convai.Editor.AI
{
    /// <summary>
    ///     High-signal multi-character workflows built on the lower-level configuration and live-room tools.
    /// </summary>
    public static partial class ConvaiMcpTools
    {
        private const string WaitForCharacterReadyTool = "Convai.WaitForCharacterReady";
        private const string SimulateConversationTargetingTool = "Convai.SimulateConversationTargeting";
        private const string SetupMultiCharacterRosterTool = "Convai.SetupMultiCharacterRoster";
        private const string WaitForMultiCharacterStateTool = "Convai.WaitForMultiCharacterState";

        [McpTool(
            WaitForCharacterReadyTool,
            "Waits for one character already in the active multi-character room to become Ready. " +
            "The wait never cancels character startup and never changes Play Mode.",
            "Wait for Convai Character Ready",
            Groups = new[] { "convai", "runtime", "multi-character" },
            EnabledByDefault = true)]
        public static async Task<object> WaitForCharacterReady(JObject parameters) =>
            await WaitForCharacterReady(Parse<ConvaiWaitForCharacterReadyRequest>(parameters));

        public static async Task<object> WaitForCharacterReady(ConvaiWaitForCharacterReadyRequest request)
        {
            request ??= new ConvaiWaitForCharacterReadyRequest();
            if (!ValidTimeout(request.TimeoutSeconds))
                return FeatureFailure("INVALID_TIMEOUT", "timeoutSeconds must be greater than 0 and no more than 60.");
            if (request.CharacterInstanceId == 0)
                return FeatureFailure("CHARACTER_REQUIRED", "characterInstanceId is required.");
            if (!ConvaiMcpResolvers.TryRoomManager(
                    request.RoomManagerInstanceId,
                    true,
                    out ConvaiRoomManager room,
                    out string roomFailure))
                return FeatureFailure(ConvaiMcpResolvers.RoomManagerErrorCode, roomFailure);
            if (!ConvaiMcpResolvers.TryCharacterInLoadedScenes(
                    request.CharacterInstanceId,
                    out ConvaiCharacter character,
                    out string characterFailure))
                return FeatureFailure(ConvaiMcpResolvers.CharacterErrorCode, characterFailure);
            if (!EditorApplication.isPlaying)
                return FeatureFailure(
                    "PLAY_MODE_REQUIRED",
                    "Character readiness exists only in an active room; Play Mode was not changed.");

            MultiCharacterRoomSession session = room.CurrentMultiCharacterSession;
            if (session == null)
                return FeatureFailure("MULTI_CHARACTER_ROOM_REQUIRED", "No active multi-character room session exists.");
            CharacterRoomMembership membership = session.FindByCharacter(character);
            if (membership == null)
                return FeatureFailure("CHARACTER_NOT_IN_ROOM", "The character is not a member of the active room.");
            if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                return RoomSessionChangedFailure(room, session);
            if (membership.Status == CharacterRoomStatus.Failed)
                return FeatureFailure(
                    string.IsNullOrWhiteSpace(membership.FailureCode)
                        ? "CHARACTER_START_FAILED"
                        : membership.FailureCode,
                    "The character failed to become ready in this room.");
            if (membership.Status == CharacterRoomStatus.Ready)
                return CharacterReadyResponse(room, session, membership, false);

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
                await membership.WaitUntilReadyAsync(timeout.Token);
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return FeatureFailure(
                        "ROOM_SESSION_CHANGED",
                        "The multi-character room changed while readiness was being awaited. Diagnose the current room.");
                return CharacterReadyResponse(room, session, membership, true);
            }
            catch (OperationCanceledException)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return Failure(
                    "CHARACTER_READY_TIMEOUT",
                    "The character is still starting; the wait ended without cancelling startup.",
                    CharacterStateSnapshot(room, session, membership));
            }
            catch (InvalidOperationException exception)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                string code = membership.Status == CharacterRoomStatus.Failed &&
                              !string.IsNullOrWhiteSpace(membership.FailureCode)
                    ? membership.FailureCode
                    : "CHARACTER_READY_FAILED";
                return Failure(code, exception.Message, CharacterStateSnapshot(room, session, membership));
            }
        }

        [McpSchema(WaitForCharacterReadyTool)]
        public static object WaitForCharacterReadyInputSchema() => ObjectSchema(
            new Dictionary<string, object>
            {
                ["roomManagerInstanceId"] = IntegerProperty(
                    "ConvaiRoomManager GameObject instance ID. Zero uses the only room manager in the loaded scenes.",
                    0),
                ["characterInstanceId"] = IntegerProperty(
                    "Character GameObject instance ID whose current membership must become Ready."),
                ["timeoutSeconds"] = TimeoutProperty(30f, 60f)
            },
            "characterInstanceId");

        [McpOutputSchema(WaitForCharacterReadyTool)]
        public static object WaitForCharacterReadyOutputSchema() => StandardResponseSchema();

        [McpTool(
            SimulateConversationTargetingTool,
            "Explains the geometric proposal and any live recovery fallback produced by configured LookAt or " +
            "Proximity targeting. Read-only in Edit and Play Mode; never contacts the backend or claims to " +
            "apply temporal switch gates.",
            "Simulate Convai Conversation Targeting",
            Groups = new[] { "convai", "scene", "multi-character" },
            EnabledByDefault = true)]
        public static object SimulateConversationTargeting(JObject parameters) =>
            SimulateConversationTargeting(Parse<ConvaiSimulateConversationTargetingRequest>(parameters));

        public static object SimulateConversationTargeting(ConvaiSimulateConversationTargetingRequest request)
        {
            request ??= new ConvaiSimulateConversationTargetingRequest();
            if (!ConvaiMcpResolvers.TryManagerInLoadedScenes(
                    request.ManagerInstanceId,
                    request.IncludeInactive,
                    out ConvaiManager manager,
                    out string managerFailure))
                return FeatureFailure(ConvaiMcpResolvers.ManagerErrorCode, managerFailure);
            if (!TryResolveSimulationView(
                    manager,
                    request.ViewCameraInstanceId,
                    out Transform view,
                    out string viewSource,
                    out string viewFailure))
                return FeatureFailure("VIEW_TRANSFORM_NOT_FOUND", viewFailure);

            ConversationTargetingOptions options = manager.ConversationTargeting;
            MultiCharacterRoomSession session = manager.GetComponent<ConvaiRoomManager>()?.CurrentMultiCharacterSession;
            ConvaiCharacter current = session != null ? manager.ConversationTarget : manager.InitialCharacter;
            IReadOnlyList<ConvaiCharacter> roomCharacters = session?.Characters
                .Select(membership => membership.Character as ConvaiCharacter)
                .ToArray();
            IReadOnlyList<ConvaiCharacter> ownedCharacters = session == null
                ? ResolveCurrentOwnedCharacters(manager)
                : Array.Empty<ConvaiCharacter>();
            IReadOnlyList<ConvaiCharacter> simulationCharacters = ResolveSimulationCharacterSource(
                session != null,
                roomCharacters,
                ownedCharacters);
            ConvaiCharacter[] sceneCharacters = simulationCharacters
                .Where(character => character != null &&
                                    character.gameObject.scene.IsValid() &&
                                    (request.IncludeInactive || character.gameObject.activeInHierarchy))
                .Distinct()
                .ToArray();

            var candidates = new List<ConversationTargetCandidate>();
            var evidence = new List<object>();
            bool byLook = options.Mode == ConversationTargetingMode.LookAt;
            foreach (ConvaiCharacter character in sceneCharacters)
            {
                CharacterRoomMembership membership = session?.FindByCharacter(character);
                bool selectedForNextRoom = manager.IsCharacterSelectedForConnection(character);
                bool stateEligible = session != null
                    ? membership?.Status == CharacterRoomStatus.Ready && character.isActiveAndEnabled
                    : selectedForNextRoom && character.isActiveAndEnabled;
                Vector3 point = ConversationAimPoint.For(character.transform).Resolve();
                Vector3 offset = point - view.position;
                float distance = offset.magnitude;
                float angle = distance <= Mathf.Epsilon ? 0f : Vector3.Angle(view.forward, offset / distance);
                bool withinDistance = distance <= options.MaxDistance;
                bool withinAngle = !byLook || angle <= options.MaxAngle;
                bool geometryEligible = withinDistance && withinAngle;
                bool eligible = stateEligible && geometryEligible && options.Mode != ConversationTargetingMode.Manual;
                float rawScore = byLook ? angle : distance;
                float effectiveScore = rawScore -
                                       (byLook && ReferenceEquals(character, current)
                                           ? options.SwitchMargin
                                           : 0f);
                long id = ConvaiMcpEntityRef.ToToolId(character.gameObject);

                if (stateEligible)
                    candidates.Add(new ConversationTargetCandidate(
                        id,
                        point,
                        ReferenceEquals(character, current)));

                evidence.Add(new
                {
                    characterInstanceId = id,
                    characterName = character.CharacterName,
                    status = session != null
                        ? membership?.Status.ToString() ?? "NotInRoom"
                        : selectedForNextRoom ? "SelectedForNextRoom" : "ExcludedFromNextRoom",
                    active = character.isActiveAndEnabled,
                    isCurrentTarget = ReferenceEquals(character, current),
                    distance,
                    angle,
                    rawScore,
                    effectiveScore,
                    withinDistance,
                    withinAngle,
                    eligible,
                    reason = EligibilityReason(
                        options.Mode,
                        character,
                        membership,
                        session != null,
                        selectedForNextRoom,
                        withinDistance,
                        withinAngle)
                });
            }

            long geometricProposalId = options.Mode == ConversationTargetingMode.Manual
                ? ConversationTargetSolver.NoTarget
                : ConversationTargetSolver.Solve(
                    new ConversationTargetQuery(view.position, view.forward, options),
                    candidates);
            bool currentCanStillHear = candidates.Any(candidate => candidate.IsCurrentTarget);
            bool recoveryFallbackApplied = options.Mode != ConversationTargetingMode.Manual &&
                                           geometricProposalId == ConversationTargetSolver.NoTarget &&
                                           !currentCanStillHear &&
                                           candidates.Count > 0;
            long proposedId = recoveryFallbackApplied
                ? ResolveRecoveryFallbackId(session, candidates)
                : geometricProposalId;
            ConvaiCharacter geometricProposal = sceneCharacters.FirstOrDefault(
                character => ConvaiMcpEntityRef.ToToolId(character.gameObject) == geometricProposalId);
            ConvaiCharacter proposed = sceneCharacters.FirstOrDefault(
                character => ConvaiMcpEntityRef.ToToolId(character.gameObject) == proposedId);

            return Success(
                "Simulated Convai conversation targeting from the current scene geometry.",
                new
                {
                    mode = EditorApplication.isPlaying ? "PlayMode" : "EditMode",
                    targetingMode = options.Mode.ToString(),
                    managerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject),
                    viewTransformInstanceId = ConvaiMcpEntityRef.ToToolId(view.gameObject),
                    viewCameraInstanceId = view.GetComponent<Camera>() != null
                        ? ConvaiMcpEntityRef.ToToolId(view.gameObject)
                        : 0,
                    viewSource,
                    currentTargetInstanceId = ConvaiMcpEntityRef.ToToolId(current?.gameObject),
                    geometricProposalTargetInstanceId = ConvaiMcpEntityRef.ToToolId(geometricProposal?.gameObject),
                    geometricProposalTargetName = geometricProposal?.CharacterName ?? string.Empty,
                    geometricProposalDiffersFromCurrent = geometricProposal != null &&
                                                            current != null &&
                                                            !ReferenceEquals(geometricProposal, current),
                    proposedTargetInstanceId = ConvaiMcpEntityRef.ToToolId(proposed?.gameObject),
                    proposedTargetName = proposed?.CharacterName ?? string.Empty,
                    proposedTargetDiffersFromCurrent = proposed != null &&
                                                       current != null &&
                                                       !ReferenceEquals(proposed, current),
                    recoveryFallbackApplied,
                    temporalSwitchPolicyApplied = false,
                    liveTargetingVerdict = EditorApplication.isPlaying
                        ? manager.ConversationTargetingStatus.ToString()
                        : string.Empty,
                    decision = options.Mode == ConversationTargetingMode.Manual
                        ? "Manual targeting does not choose from scene geometry."
                        : recoveryFallbackApplied
                            ? "No ready or selected character met the geometry limits and the current target is no " +
                              "longer eligible. The live runtime therefore proposes the first eligible room " +
                              "character before applying temporal switch gates."
                        : proposed != null
                            ? "This is the geometric proposal after the current-target margin. Live targeting " +
                              "still applies switch delay, player-speech, and pending-command gates before switching."
                            : "No character qualifies; a live room keeps its current target rather than clearing it.",
                    settings = new
                    {
                        options.MaxDistance,
                        options.MaxAngle,
                        options.SwitchMargin,
                        options.SwitchDelaySeconds
                    },
                    candidates = evidence
                });
        }

        [McpSchema(SimulateConversationTargetingTool)]
        public static object SimulateConversationTargetingInputSchema() => ObjectSchema(
            new Dictionary<string, object>
            {
                ["managerInstanceId"] = IntegerProperty(
                    "ConvaiManager GameObject instance ID. Zero uses the only manager in the loaded scenes.",
                    0),
                ["viewCameraInstanceId"] = IntegerProperty(
                    "Camera GameObject instance ID. Zero uses the authored camera, Main Camera, then owned player.",
                    0),
                ["includeInactive"] = BooleanProperty(
                    "Include inactive characters in the report; they remain ineligible.",
                    true)
            });

        [McpOutputSchema(SimulateConversationTargetingTool)]
        public static object SimulateConversationTargetingOutputSchema() => StandardResponseSchema();

        [McpTool(
            SetupMultiCharacterRosterTool,
            "Previews or configures the exact roster of at least two characters for the next room and optionally " +
            "chooses who speaks first. Validates local identity before applying through Unity Undo.",
            "Set Up Convai Multi-Character Roster",
            Groups = new[] { "convai", "scene", "multi-character" },
            EnabledByDefault = true)]
        public static object SetupMultiCharacterRoster(JObject parameters) =>
            SetupMultiCharacterRoster(Parse<ConvaiSetupMultiCharacterRosterRequest>(parameters));

        public static object SetupMultiCharacterRoster(ConvaiSetupMultiCharacterRosterRequest request)
        {
            request ??= new ConvaiSetupMultiCharacterRosterRequest();
            long[] ids = request.CharacterInstanceIds ?? Array.Empty<long>();
            if (ids.Length < 2)
                return FeatureFailure(
                    "MULTI_CHARACTER_ROSTER_REQUIRED",
                    "characterInstanceIds must contain at least two characters.");
            if (ids.Any(id => id == 0))
                return FeatureFailure("CHARACTER_REQUIRED", "Every characterInstanceId must be non-zero.");
            if (ids.Distinct().Count() != ids.Length)
                return FeatureFailure(
                    "DUPLICATE_CHARACTER_INSTANCE_ID",
                    "characterInstanceIds must name each local character once.");

            var characters = new List<ConvaiCharacter>(ids.Length);
            foreach (long id in ids)
            {
                if (!ConvaiMcpResolvers.TryCharacterInLoadedScenes(
                        id,
                        out ConvaiCharacter character,
                        out string failure))
                    return FeatureFailure(ConvaiMcpResolvers.CharacterErrorCode, failure);
                characters.Add(character);
            }

            ConvaiCharacter missingId = characters.FirstOrDefault(
                character => string.IsNullOrWhiteSpace(character.CharacterId));
            if (missingId != null)
                return FeatureFailure(
                    "CHARACTER_ID_MISSING",
                    $"'{missingId.gameObject.name}' has no Character ID. Configure it before building the roster.");
            IGrouping<string, ConvaiCharacter> duplicateId = characters
                .GroupBy(character => character.CharacterId, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateId != null)
                return FeatureFailure(
                    "DUPLICATE_CHARACTER_ID",
                    "Every character in a multi-character room needs a unique Character ID.");
            if (request.InitialCharacterInstanceId != 0 && !ids.Contains(request.InitialCharacterInstanceId))
                return FeatureFailure(
                    "INITIAL_CHARACTER_NOT_IN_ROSTER",
                    "initialCharacterInstanceId must also appear in characterInstanceIds.");

            object configured = ConfigureConversationTargeting(
                new ConvaiConfigureConversationTargetingRequest
                {
                    ManagerInstanceId = request.ManagerInstanceId,
                    IncludedCharacterInstanceIds = ids,
                    InitialCharacterInstanceId = request.InitialCharacterInstanceId != 0
                        ? request.InitialCharacterInstanceId
                        : null,
                    DryRun = request.DryRun
                });
            JObject response = JObject.FromObject(configured);
            if (response.Value<bool>("success"))
            {
                response["message"] = request.DryRun
                    ? "Previewed the exact multi-character roster for the next room."
                    : "Configured the exact multi-character roster for the next room.";
                if (response["data"] is JObject data)
                {
                    data["characterInstanceIds"] = JArray.FromObject(ids);
                    data["initialCharacterInstanceId"] = request.InitialCharacterInstanceId;
                }
            }

            return response;
        }

        [McpSchema(SetupMultiCharacterRosterTool)]
        public static object SetupMultiCharacterRosterInputSchema() => ObjectSchema(
            new Dictionary<string, object>
            {
                ["managerInstanceId"] = IntegerProperty(
                    "ConvaiManager GameObject instance ID. Zero uses the only manager in the loaded scenes.",
                    0),
                ["characterInstanceIds"] = new
                {
                    type = "array",
                    description = "Exact set of at least two character GameObject instance IDs for the next room.",
                    minItems = 2,
                    uniqueItems = true,
                    items = IntegerProperty("Character GameObject instance ID.")
                },
                ["initialCharacterInstanceId"] = IntegerProperty(
                    "Optional roster member that speaks first. Zero leaves the authored value unchanged.",
                    0),
                ["dryRun"] = BooleanProperty("Preview without mutation.", true)
            },
            "characterInstanceIds");

        [McpOutputSchema(SetupMultiCharacterRosterTool)]
        public static object SetupMultiCharacterRosterOutputSchema() => StandardResponseSchema();

        [McpTool(
            WaitForMultiCharacterStateTool,
            "Waits for authoritative target, roster epoch, route epoch, roster size, readiness, or conversation " +
            "availability conditions in the active multi-character room. With no conditions, returns a current " +
            "snapshot. Read-only and never changes Play Mode.",
            "Wait for Convai Multi-Character State",
            Groups = new[] { "convai", "runtime", "multi-character" },
            EnabledByDefault = true)]
        public static async Task<object> WaitForMultiCharacterState(JObject parameters) =>
            await WaitForMultiCharacterState(Parse<ConvaiWaitForMultiCharacterStateRequest>(parameters));

        public static async Task<object> WaitForMultiCharacterState(ConvaiWaitForMultiCharacterStateRequest request)
        {
            request ??= new ConvaiWaitForMultiCharacterStateRequest();
            if (!ValidTimeout(request.TimeoutSeconds))
                return FeatureFailure("INVALID_TIMEOUT", "timeoutSeconds must be greater than 0 and no more than 60.");
            if (request.ExpectedRosterSize < -1 || request.MinimumRosterEpoch < -1 || request.MinimumRouteEpoch < -1)
                return FeatureFailure("INVALID_STATE_CONDITION", "Roster size and epoch conditions must be -1 or greater.");
            if (!ConvaiMcpResolvers.TryManagerInLoadedScenes(
                    request.ManagerInstanceId,
                    true,
                    out ConvaiManager manager,
                    out string managerFailure))
                return FeatureFailure(ConvaiMcpResolvers.ManagerErrorCode, managerFailure);
            ConvaiRoomManager room = manager.GetComponent<ConvaiRoomManager>();
            if (room == null &&
                !ConvaiMcpResolvers.TryRoomManager(0, true, out room, out string roomFailure))
                return FeatureFailure(ConvaiMcpResolvers.RoomManagerErrorCode, roomFailure);
            ConvaiCharacter expected = null;
            if (request.ExpectedActiveCharacterInstanceId != 0 &&
                !ConvaiMcpResolvers.TryCharacterInLoadedScenes(
                    request.ExpectedActiveCharacterInstanceId,
                    out expected,
                    out string characterFailure))
                return FeatureFailure(ConvaiMcpResolvers.CharacterErrorCode, characterFailure);
            if (!EditorApplication.isPlaying)
                return FeatureFailure(
                    "PLAY_MODE_REQUIRED",
                    "Live multi-character state exists only in Play Mode; Play Mode was not changed.");

            MultiCharacterRoomSession session = room.CurrentMultiCharacterSession;
            if (session == null)
                return FeatureFailure("MULTI_CHARACTER_ROOM_REQUIRED", "No active multi-character room session exists.");
            if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                return RoomSessionChangedFailure(room, session);
            if (!HasMultiCharacterStateCondition(request))
                return Success(
                    "Returned the current multi-character state without waiting.",
                    new
                    {
                        snapshotOnly = true,
                        waited = false,
                        state = MultiCharacterStateSnapshot(manager, room, session)
                    });
            CharacterRoomMembership expectedMembership = expected == null ? null : session.FindByCharacter(expected);
            if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                return RoomSessionChangedFailure(room, session);
            if (expected != null && expectedMembership == null)
                return FeatureFailure("CHARACTER_NOT_IN_ROOM", "The expected target is not a member of the active room.");

            bool Matches() => MultiCharacterStateMatches(manager, room, session, expectedMembership, request);
            if (Matches())
                return MultiCharacterStateResponse(manager, room, session, true, false);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Reevaluate()
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                {
                    completion.TrySetResult(false);
                    return;
                }

                if (Matches()) completion.TrySetResult(true);
            }
            void MembershipChanged(CharacterRoomMembership _) => Reevaluate();
            void TargetChanged(CharacterRoomMembership _, CharacterRoomMembership __) => Reevaluate();
            void AvailabilityChanged(ConvaiConversationAvailability _) => Reevaluate();
            void RosterEpochChanged(int _) => Reevaluate();
            void SessionRetired() => completion.TrySetResult(false);

            session.CharacterStatusChanged += MembershipChanged;
            session.CharacterAdded += MembershipChanged;
            session.CharacterRemoved += MembershipChanged;
            session.RosterEpochChanged += RosterEpochChanged;
            session.Retired += SessionRetired;
            session.InteractionTargetChanged += TargetChanged;
            manager.ConversationAvailabilityChanged += AvailabilityChanged;
            try
            {
                Reevaluate();
                Task finished = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(TimeSpan.FromSeconds(request.TimeoutSeconds)));
                if (!ReferenceEquals(finished, completion.Task))
                {
                    if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                        return RoomSessionChangedFailure(room, session);
                    return Failure(
                        "MULTI_CHARACTER_STATE_TIMEOUT",
                        "The requested multi-character state was not reached before timeout.",
                        MultiCharacterStateSnapshot(manager, room, session));
                }
                bool matched = await completion.Task;
                if (!matched || !ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return MultiCharacterStateResponse(manager, room, session, false, true);
            }
            finally
            {
                session.CharacterStatusChanged -= MembershipChanged;
                session.CharacterAdded -= MembershipChanged;
                session.CharacterRemoved -= MembershipChanged;
                session.RosterEpochChanged -= RosterEpochChanged;
                session.Retired -= SessionRetired;
                session.InteractionTargetChanged -= TargetChanged;
                manager.ConversationAvailabilityChanged -= AvailabilityChanged;
            }
        }

        [McpSchema(WaitForMultiCharacterStateTool)]
        public static object WaitForMultiCharacterStateInputSchema() => ObjectSchema(
            new Dictionary<string, object>
            {
                ["managerInstanceId"] = IntegerProperty(
                    "ConvaiManager GameObject instance ID. Zero uses the only manager in the loaded scenes.",
                    0),
                ["expectedActiveCharacterInstanceId"] = IntegerProperty(
                    "Optional character GameObject instance ID that must become the authoritative target.",
                    0),
                ["expectedRosterSize"] = new { type = "integer", minimum = -1, @default = -1 },
                ["minimumRosterEpoch"] = new { type = "integer", minimum = -1, @default = -1 },
                ["minimumRouteEpoch"] = new { type = "integer", minimum = -1, @default = -1 },
                ["requireAllCharactersReady"] = BooleanProperty(
                    "Wait until every current roster member is Ready.",
                    false),
                ["requireConversationAvailable"] = BooleanProperty(
                    "Wait until the addressed character can accept player input.",
                    false),
                ["timeoutSeconds"] = TimeoutProperty(30f, 60f)
            });

        [McpOutputSchema(WaitForMultiCharacterStateTool)]
        public static object WaitForMultiCharacterStateOutputSchema() => StandardResponseSchema();

        private static object CharacterReadyResponse(
            ConvaiRoomManager room,
            MultiCharacterRoomSession session,
            CharacterRoomMembership membership,
            bool waited) =>
            Success(
                waited ? "The character became ready." : "The character is already ready.",
                new
                {
                    waited,
                    roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                    routeEpoch = session.RouteEpoch,
                    rosterEpoch = session.RosterEpoch,
                    membership = DescribeMembership(session, membership),
                    conversationAvailability = (membership.Character as ConvaiCharacter)?.ConversationAvailability.ToString() ??
                                               string.Empty
                });

        private static object CharacterStateSnapshot(
            ConvaiRoomManager room,
            MultiCharacterRoomSession session,
            CharacterRoomMembership membership) => new
        {
            roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
            roomStillCurrent = ReferenceEquals(room.CurrentMultiCharacterSession, session),
            routeEpoch = session.RouteEpoch,
            rosterEpoch = session.RosterEpoch,
            membership = DescribeMembership(session, membership)
        };

        private static bool TryResolveSimulationView(
            ConvaiManager manager,
            long instanceId,
            out Transform view,
            out string source,
            out string failure)
        {
            view = null;
            source = string.Empty;
            failure = string.Empty;
            if (instanceId != 0)
            {
                if (!ConvaiMcpEntityRef.TryResolve(instanceId, out GameObject target) || target == null)
                {
                    failure = $"Instance ID {instanceId} does not name a current scene GameObject.";
                    return false;
                }

                Camera camera = target.GetComponent<Camera>();
                if (camera == null)
                {
                    failure = $"'{target.name}' has no Camera component.";
                    return false;
                }

                view = camera.transform;
                source = "ExplicitCamera";
                return true;
            }

            var serialized = new SerializedObject(manager);
            Camera authored = serialized.FindProperty("_conversationViewCamera")?.objectReferenceValue as Camera;
            Camera automaticMain = Camera.main;
            view = manager.ResolveViewTransform(automaticMain);
            if (view != null)
            {
                source = authored != null
                    ? "AuthoredCamera"
                    : automaticMain != null
                        ? "MainCamera"
                        : "Player";
                return true;
            }

            failure =
                "No authored conversation camera, Main Camera, or owned Convai Player is available. " +
                "Send viewCameraInstanceId from Unity scene inspection or configure the manager's player.";
            return false;
        }

        private static object RoomSessionChangedFailure(
            ConvaiRoomManager room,
            MultiCharacterRoomSession previous)
        {
            MultiCharacterRoomSession current = room.CurrentMultiCharacterSession;
            return Failure(
                "ROOM_SESSION_CHANGED",
                "The multi-character room was replaced or cleared while the wait was active. Diagnose the current room.",
                new
                {
                    roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                    previousRoomSessionId = previous?.RoomSessionId ?? string.Empty,
                    currentRoomSessionId = current?.RoomSessionId ?? string.Empty,
                    currentRouteEpoch = current?.RouteEpoch ?? 0,
                    currentRosterEpoch = current?.RosterEpoch ?? 0,
                    currentRoster = current?.Characters.Select(item => DescribeMembership(current, item)).ToArray() ??
                                    Array.Empty<object>()
                });
        }

        private static IReadOnlyList<ConvaiCharacter> ResolveCurrentOwnedCharacters(ConvaiManager manager)
        {
            var serialized = new SerializedObject(manager);
            ConvaiSceneInstaller installer = serialized.FindProperty("_sceneInstaller")?.objectReferenceValue as
                ConvaiSceneInstaller;
            ConvaiCharacter explicitTarget = serialized.FindProperty("_explicitConversationTarget")?
                .objectReferenceValue as ConvaiCharacter;
            SerializedProperty explicitCharacters = serialized.FindProperty("_explicitCharacters");
            var authored = new List<ConvaiCharacter>();
            if (explicitCharacters != null && explicitCharacters.isArray)
                for (int index = 0; index < explicitCharacters.arraySize; index++)
                    if (explicitCharacters.GetArrayElementAtIndex(index).objectReferenceValue is ConvaiCharacter character)
                        authored.Add(character);

            ConvaiCharacter[] loaded = ConvaiObjectFind.All<ConvaiCharacter>(FindObjectsInactive.Include)
                .Where(character => character != null && character.gameObject.scene.IsValid())
                .ToArray();
            return ConvaiRuntimeHost.ResolveOwnedCharacters(installer, authored, explicitTarget, loaded);
        }

        internal static IReadOnlyList<ConvaiCharacter> ResolveSimulationCharacterSource(
            bool hasLiveSession,
            IReadOnlyList<ConvaiCharacter> roomCharacters,
            IReadOnlyList<ConvaiCharacter> ownedCharacters) =>
            hasLiveSession
                ? roomCharacters ?? Array.Empty<ConvaiCharacter>()
                : ownedCharacters ?? Array.Empty<ConvaiCharacter>();

        private static string EligibilityReason(
            ConversationTargetingMode mode,
            ConvaiCharacter character,
            CharacterRoomMembership membership,
            bool hasSession,
            bool selectedForNextRoom,
            bool withinDistance,
            bool withinAngle)
        {
            if (mode == ConversationTargetingMode.Manual) return "ManualMode";
            if (!character.isActiveAndEnabled) return "Inactive";
            if (hasSession && membership == null) return "NotInCurrentRoom";
            if (hasSession && membership.Status != CharacterRoomStatus.Ready)
                return membership.Status == CharacterRoomStatus.Failed ? "Failed" : "Starting";
            if (!hasSession && !selectedForNextRoom) return "ExcludedFromNextRoom";
            if (!withinDistance) return "OutsideMaxDistance";
            if (!withinAngle) return "OutsideMaxAngle";
            return "Eligible";
        }

        private static long ResolveRecoveryFallbackId(
            MultiCharacterRoomSession session,
            IReadOnlyList<ConversationTargetCandidate> candidates)
        {
            if (session == null) return candidates[0].Id;

            long[] membershipOrder = session.Characters
                .Select(membership => membership.Character is ConvaiCharacter character
                    ? ConvaiMcpEntityRef.ToToolId(character.gameObject)
                    : ConversationTargetSolver.NoTarget)
                .ToArray();
            long[] candidateIds = candidates.Select(candidate => candidate.Id).ToArray();
            return ResolveRecoveryFallbackId(membershipOrder, candidateIds);
        }

        internal static long ResolveRecoveryFallbackId(
            IReadOnlyList<long> membershipOrder,
            IReadOnlyList<long> candidateIds)
        {
            for (int membershipIndex = 0; membershipIndex < membershipOrder.Count; membershipIndex++)
            {
                long characterId = membershipOrder[membershipIndex];
                for (int candidateIndex = 0; candidateIndex < candidateIds.Count; candidateIndex++)
                    if (candidateIds[candidateIndex] == characterId)
                        return membershipOrder[membershipIndex];
            }

            return ConversationTargetSolver.NoTarget;
        }

        private static bool MultiCharacterStateMatches(
            ConvaiManager manager,
            ConvaiRoomManager room,
            MultiCharacterRoomSession session,
            CharacterRoomMembership expectedMembership,
            ConvaiWaitForMultiCharacterStateRequest request)
        {
            if (!ReferenceEquals(room.CurrentMultiCharacterSession, session)) return false;
            if (expectedMembership != null &&
                !string.Equals(
                    session.ActiveMembershipId,
                    expectedMembership.MembershipId,
                    StringComparison.Ordinal))
                return false;
            if (request.ExpectedRosterSize >= 0 && session.Characters.Count != request.ExpectedRosterSize)
                return false;
            if (request.MinimumRosterEpoch >= 0 && session.RosterEpoch < request.MinimumRosterEpoch)
                return false;
            if (request.MinimumRouteEpoch >= 0 && session.RouteEpoch < request.MinimumRouteEpoch)
                return false;
            if (request.RequireAllCharactersReady &&
                (session.Characters.Count == 0 ||
                 session.Characters.Any(membership => membership.Status != CharacterRoomStatus.Ready)))
                return false;
            return !request.RequireConversationAvailable || manager.ConversationAvailability.CanAcceptPlayerInput();
        }

        private static bool HasMultiCharacterStateCondition(ConvaiWaitForMultiCharacterStateRequest request) =>
            request.ExpectedActiveCharacterInstanceId != 0 ||
            request.ExpectedRosterSize >= 0 ||
            request.MinimumRosterEpoch >= 0 ||
            request.MinimumRouteEpoch >= 0 ||
            request.RequireAllCharactersReady ||
            request.RequireConversationAvailable;

        private static object MultiCharacterStateResponse(
            ConvaiManager manager,
            ConvaiRoomManager room,
            MultiCharacterRoomSession session,
            bool alreadyMatched,
            bool waited) =>
            Success(
                alreadyMatched
                    ? "The requested multi-character state already matches."
                    : "The requested multi-character state was reached.",
                new
                {
                    alreadyMatched,
                    waited,
                    state = MultiCharacterStateSnapshot(manager, room, session)
                });

        private static object MultiCharacterStateSnapshot(
            ConvaiManager manager,
            ConvaiRoomManager room,
            MultiCharacterRoomSession session) => new
        {
            managerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject),
            roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
            roomStillCurrent = ReferenceEquals(room.CurrentMultiCharacterSession, session),
            session.IsReady,
            session.RouteEpoch,
            session.RosterEpoch,
            rosterSize = session.Characters.Count,
            activeMembershipId = session.ActiveMembershipId,
            activeTarget = DescribeMembership(session, session.FindByMembershipId(session.ActiveMembershipId)),
            conversationAvailability = manager.ConversationAvailability.ToString(),
            playerCanTalk = manager.ConversationAvailability.CanAcceptPlayerInput(),
            roster = session.Characters.Select(membership => DescribeMembership(session, membership)).ToArray()
        };
    }
}
