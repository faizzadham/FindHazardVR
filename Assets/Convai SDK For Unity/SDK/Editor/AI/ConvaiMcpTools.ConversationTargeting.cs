using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Conversation;
using Convai.Runtime.Core.Async;
using Convai.Runtime.Room;
using Newtonsoft.Json.Linq;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEditor;

namespace Convai.Editor.AI
{
    /// <summary>
    ///     The MCP surface for a room that holds more than one character.
    /// </summary>
    /// <remarks>
    ///     A separate tool rather than more fields on <c>Convai.ConfigureRoom</c>. Everything here is
    ///     inert in a scene with one character, and a first-time setup should not have to read past
    ///     eleven settings that cannot do anything yet to find the two that can.
    /// </remarks>
    public static partial class ConvaiMcpTools
    {
        private const string ConfigureConversationTargetingTool = "Convai.ConfigureConversationTargeting";
        private const string SetConversationTargetTool = "Convai.SetConversationTarget";
        private const string UpdateCharacterRosterTool = "Convai.UpdateCharacterRoster";

        [McpTool(
            ConfigureConversationTargetingTool,
            "Previews or configures which characters join the next Convai room and how the SDK decides " +
            "which of them the player is talking to. Every field is optional and omitting one leaves it " +
            "unchanged. Uses Undo, never saves, and never changes credentials.",
            "Configure Convai Conversation Targeting",
            Groups = new[] { "convai", "scene" },
            EnabledByDefault = true)]
        public static object ConfigureConversationTargeting(JObject parameters) =>
            ConfigureConversationTargeting(Parse<ConvaiConfigureConversationTargetingRequest>(parameters));

        public static object ConfigureConversationTargeting(ConvaiConfigureConversationTargetingRequest request) =>
            AuthoringResponse(
                ConvaiMultiCharacterAuthoringService.Configure(request),
                "Configured Convai conversation targeting.",
                "Previewed Convai conversation targeting configuration.");

        [McpSchema(ConfigureConversationTargetingTool)]
        public static object ConfigureConversationTargetingInputSchema() => ObjectSchema(
            new Dictionary<string, object>
            {
                ["managerInstanceId"] = ConvaiMcpResponses.OptionalIntegerProperty(
                    "The Convai Manager to configure. Omit when the loaded scenes hold only one."),
                ["targetingMode"] = ConvaiMcpResponses.OptionalStringEnumProperty(
                    "How the character being addressed is chosen. LookAt is the default and picks the " +
                    "character nearest the centre of view; Proximity picks the nearest character " +
                    "wherever the player is looking; Manual moves the conversation only when the game " +
                    "calls ConvaiManager.TalkTo. Omit to leave unchanged.",
                    Enum.GetNames(typeof(ConversationTargetingMode))),
                ["maxDistance"] = ConvaiMcpResponses.OptionalNumberProperty(
                    "Range: how far away a character can be and still be addressed, in metres. A guard " +
                    "against addressing somebody in the next room, not the rule that picks between " +
                    "characters, so it is deliberately generous."),
                ["maxAngle"] = ConvaiMcpResponses.OptionalNumberProperty(
                    "Look Angle: how far from the centre of view a character can be, in degrees. 35 " +
                    "means a 70-degree cone. LookAt only."),
                ["switchMargin"] = ConvaiMcpResponses.OptionalNumberProperty(
                    "Switch Margin: how much better a different character must look before the " +
                    "conversation moves, in degrees. Raise it when two characters standing close " +
                    "together trade the conversation on small camera movements. LookAt only."),
                ["switchDelaySeconds"] = ConvaiMcpResponses.OptionalNumberProperty(
                    "Switch Delay: how long a character must stay the best choice before the " +
                    "conversation moves. Stops a sweep of the view addressing everyone it passes."),
                ["viewCameraInstanceId"] = ConvaiMcpResponses.OptionalIntegerProperty(
                    "Player Camera: the Camera treated as the player's view. Omit to leave unchanged."),
                ["clearViewCamera"] = ConvaiMcpResponses.OptionalBooleanProperty(
                    "Clear Player Camera so the main camera is used again."),
                ["initialCharacterInstanceId"] = ConvaiMcpResponses.OptionalIntegerProperty(
                    "Initial Character: the character the room opens on regardless of where the player " +
                    "is looking. Omit to leave unchanged."),
                ["clearInitialCharacter"] = ConvaiMcpResponses.OptionalBooleanProperty(
                    "Clear Initial Character so the room opens on whoever the player is looking at."),
                ["includedCharacterInstanceIds"] = ConvaiMcpResponses.ArrayProperty(
                    "Characters Joining the Room: the exact set of characters to send, so one left out is " +
                    "excluded. Read the current set from Convai.DiagnoseConversation before changing it. " +
                    "Setting this switches the manager to an explicit selection, which means a character " +
                    "added to the scene later stays out until it is included too.",
                    ConvaiMcpResponses.IntegerProperty("Character GameObject instance ID.")),
                ["includeAllCharacters"] = ConvaiMcpResponses.OptionalBooleanProperty(
                    "Send every active character to the next room, which is the shipped default."),
                ["dryRun"] = BooleanProperty("Preview without mutation.", true)
            });

        [McpOutputSchema(ConfigureConversationTargetingTool)]
        public static object ConfigureConversationTargetingOutputSchema() => StandardResponseSchema();

        [McpTool(
            SetConversationTargetTool,
            "Previews or changes which character the player is addressing in an active multi-character " +
            "room. Execution requires Play Mode and an explicit dryRun=false; it never changes Play Mode.",
            "Set Convai Conversation Target",
            Groups = new[] { "convai", "runtime", "multi-character" },
            EnabledByDefault = true)]
        public static async Task<object> SetConversationTarget(JObject parameters) =>
            await SetConversationTarget(Parse<ConvaiSetConversationTargetRequest>(parameters));

        public static async Task<object> SetConversationTarget(ConvaiSetConversationTargetRequest request)
        {
            request ??= new ConvaiSetConversationTargetRequest();
            if (!ValidTimeout(request.TimeoutSeconds))
                return FeatureFailure("INVALID_TIMEOUT", "timeoutSeconds must be greater than 0 and no more than 60.");
            if (request.CharacterInstanceId == 0)
                return FeatureFailure("CHARACTER_REQUIRED", "characterInstanceId is required.");
            if (!ConvaiMcpResolvers.TryManagerInLoadedScenes(
                    request.ManagerInstanceId,
                    true,
                    out ConvaiManager manager,
                    out string managerFailure))
                return FeatureFailure(ConvaiMcpResolvers.ManagerErrorCode, managerFailure);

            if (!ConvaiMcpResolvers.TryCharacterInLoadedScenes(
                    request.CharacterInstanceId,
                    out ConvaiCharacter character,
                    out string characterFailure))
                return FeatureFailure(ConvaiMcpResolvers.CharacterErrorCode, characterFailure);

            ConvaiRoomManager room = manager.GetComponent<ConvaiRoomManager>();
            if (room == null &&
                !ConvaiMcpResolvers.TryRoomManager(0, true, out room, out string roomFailure))
                return FeatureFailure(ConvaiMcpResolvers.RoomManagerErrorCode, roomFailure);

            MultiCharacterRoomSession session = room.CurrentMultiCharacterSession;
            CharacterRoomMembership membership = session?.FindByCharacter(character);
            if (request.DryRun)
            {
                return Success(
                    "Previewed live conversation target change.",
                    new
                    {
                        dryRun = true,
                        executed = false,
                        requiresPlayMode = !EditorApplication.isPlaying,
                        activeMultiCharacterRoom = session != null,
                        managerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject),
                        roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                        characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                        characterIsInCurrentRoom = membership != null
                    });
            }

            if (!EditorApplication.isPlaying)
                return FeatureFailure("PLAY_MODE_REQUIRED", "Live conversation targeting requires Play Mode; Play Mode was not changed.");
            if (session == null)
                return FeatureFailure("MULTI_CHARACTER_ROOM_REQUIRED", "No active multi-character room session exists.");
            if (membership == null)
                return FeatureFailure("CHARACTER_NOT_IN_ROOM", "The character is not a member of the active room.");
            if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                return RoomSessionChangedFailure(room, session);

            if (string.Equals(session.ActiveMembershipId, membership.MembershipId, StringComparison.Ordinal))
            {
                return Success(
                    "The requested conversation target was already active.",
                    new
                    {
                        dryRun = false,
                        executed = true,
                        managerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject),
                        roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                        characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                        activeMembershipId = membership.MembershipId,
                        previousMembershipId = membership.MembershipId,
                        routeEpoch = session.RouteEpoch,
                        changed = false,
                        activeTarget = DescribeMembership(session, membership)
                    });
            }

            try
            {
                string previousMembershipId = session.ActiveMembershipId;
                var sessionRetired = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void HandleSessionRetired() => sessionRetired.TrySetResult(true);

                session.Retired += HandleSessionRetired;
                InteractionTargetResult result;
                try
                {
                    if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                        return RoomSessionChangedFailure(room, session);
                    ConversationTargetRequestObservation observation = manager.TalkToObserved(character);
                    if (observation.IsDeferred)
                        return Success(
                            "Queued the conversation-target change until the player's current utterance ends.",
                            new
                            {
                                dryRun = false,
                                executed = false,
                                queued = true,
                                pending = true,
                                canonicalCommandRegistered = false,
                                deferredUntilPlayerUtteranceEnds = true,
                                replaceableByAnotherTalkTo = true,
                                managerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject),
                                roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                                characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                                previousMembershipId,
                                requestedMembershipId = membership.MembershipId,
                                routeEpoch = session.RouteEpoch,
                                guidance = "The target is queued locally and has not been sent to the room. " +
                                           "A later TalkTo call before the utterance ends replaces it. " +
                                           "Use Convai.WaitForMultiCharacterState after speech ends to observe the canonical target."
                            });
                    Task finished = await Task.WhenAny(
                        observation.Completion,
                        sessionRetired.Task,
                        Task.Delay(TimeSpan.FromSeconds(request.TimeoutSeconds)));
                    if (!ReferenceEquals(room.CurrentMultiCharacterSession, session) ||
                        ReferenceEquals(finished, sessionRetired.Task))
                        return RoomSessionChangedFailure(room, session);
                    if (!ReferenceEquals(finished, observation.Completion))
                        return Success(
                            observation.IsRegistered
                                ? "The canonical conversation-target request is still pending."
                                : "The conversation-target request is queued but has not registered with the room yet.",
                            new
                            {
                                dryRun = false,
                                executed = observation.IsRegistered,
                                queued = !observation.IsRegistered,
                                pending = true,
                                canonicalCommandRegistered = observation.IsRegistered,
                                deferredUntilPlayerUtteranceEnds = false,
                                managerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject),
                                roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                                characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                                previousMembershipId,
                                requestedMembershipId = membership.MembershipId,
                                routeEpoch = room.CurrentMultiCharacterSession?.RouteEpoch ?? session.RouteEpoch,
                                guidance = observation.IsRegistered
                                    ? "The canonical request remains active and may reconcile later. " +
                                      "Use Convai.WaitForMultiCharacterState or Convai.DiagnoseConversation before retrying."
                                    : "No canonical registration was observed before timeout, but the local asynchronous request " +
                                      "may still register later. Diagnose the room before retrying."
                            });
                    try
                    {
                        result = await observation.Completion;
                    }
                    catch (Exception exception)
                    {
                        if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                            return RoomSessionChangedFailure(room, session);
                        return FeatureFailure(
                            "TARGET_CHANGE_REJECTED",
                            string.IsNullOrWhiteSpace(exception.Message)
                                ? "The active room rejected the conversation-target request."
                                : exception.Message);
                    }
                }
                finally
                {
                    session.Retired -= HandleSessionRetired;
                }

                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);

                MultiCharacterRoomSession current = room.CurrentMultiCharacterSession;
                bool changed = result.Changed;
                return Success(
                    changed ? "Changed the active conversation target." : "The requested conversation target was already active.",
                    new
                    {
                        dryRun = false,
                        executed = true,
                        managerInstanceId = ConvaiMcpEntityRef.ToToolId(manager.gameObject),
                        roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                        characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                        activeMembershipId = result.ActiveMembershipId,
                        previousMembershipId = string.IsNullOrWhiteSpace(result.PreviousMembershipId)
                            ? previousMembershipId
                            : result.PreviousMembershipId,
                        routeEpoch = result.RouteEpoch,
                        changed,
                        activeTarget = DescribeMembership(
                            current,
                            current?.FindByMembershipId(result.ActiveMembershipId))
                    });
            }
            catch (ArgumentException exception)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return FeatureFailure("INVALID_TARGET", exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return FeatureFailure("TARGET_CHANGE_REJECTED", exception.Message);
            }
        }

        [McpSchema(SetConversationTargetTool)]
        public static object SetConversationTargetInputSchema() => ObjectSchema(
            new Dictionary<string, object>
            {
                ["managerInstanceId"] = IntegerProperty(
                    "ConvaiManager GameObject instance ID. Zero uses the only manager in the loaded scenes.",
                    0),
                ["characterInstanceId"] = IntegerProperty(
                    "Character GameObject instance ID to address."),
                ["dryRun"] = BooleanProperty("Preview without contacting the active room.", true),
                ["timeoutSeconds"] = TimeoutProperty(10f, 60f)
            },
            "characterInstanceId");

        [McpOutputSchema(SetConversationTargetTool)]
        public static object SetConversationTargetOutputSchema() => StandardResponseSchema();

        [McpTool(
            UpdateCharacterRosterTool,
            "Previews or adds/removes one character in an active multi-character room and returns the " +
            "authoritative roster and routing epochs. Execution requires Play Mode and dryRun=false; it never " +
            "changes Play Mode.",
            "Update Convai Character Roster",
            Groups = new[] { "convai", "runtime", "multi-character" },
            EnabledByDefault = true)]
        public static async Task<object> UpdateCharacterRoster(JObject parameters) =>
            await UpdateCharacterRoster(Parse<ConvaiUpdateCharacterRosterRequest>(parameters));

        public static async Task<object> UpdateCharacterRoster(ConvaiUpdateCharacterRosterRequest request)
        {
            request ??= new ConvaiUpdateCharacterRosterRequest();
            if (!ValidTimeout(request.TimeoutSeconds, 15f))
                return FeatureFailure("INVALID_TIMEOUT", "timeoutSeconds must be greater than 0 and no more than 15.");
            if (request.CharacterInstanceId == 0)
                return FeatureFailure("CHARACTER_REQUIRED", "characterInstanceId is required.");
            if (!Enum.IsDefined(typeof(ConvaiCharacterRosterOperation), request.Operation))
                return FeatureFailure("INVALID_ROSTER_OPERATION", "operation must be Add or Remove.");
            if (request.Operation == ConvaiCharacterRosterOperation.Add &&
                request.ReplacementTargetCharacterInstanceId != 0)
                return FeatureFailure(
                    "REPLACEMENT_ONLY_FOR_REMOVE",
                    "replacementTargetCharacterInstanceId is valid only for Remove.");
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

            ConvaiCharacter replacement = null;
            if (request.ReplacementTargetCharacterInstanceId != 0 &&
                !ConvaiMcpResolvers.TryCharacterInLoadedScenes(
                    request.ReplacementTargetCharacterInstanceId,
                    out replacement,
                    out string replacementFailure))
                return FeatureFailure("INVALID_REPLACEMENT_TARGET", replacementFailure);

            MultiCharacterRoomSession session = room.CurrentMultiCharacterSession;
            CharacterRoomMembership membership = session?.FindByCharacter(character);
            CharacterRoomMembership replacementMembership = replacement == null
                ? null
                : session?.FindByCharacter(replacement);
            if (request.DryRun)
            {
                return Success(
                    "Previewed live character roster update.",
                    new
                    {
                        dryRun = true,
                        executed = false,
                        operation = request.Operation.ToString(),
                        requiresPlayMode = !EditorApplication.isPlaying,
                        activeMultiCharacterRoom = session != null,
                        roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                        characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                        replacementTargetCharacterInstanceId = ConvaiMcpEntityRef.ToToolId(replacement?.gameObject),
                        characterIsInCurrentRoom = membership != null,
                        replacementIsInCurrentRoom = replacement == null || replacementMembership != null
                    });
            }

            if (!EditorApplication.isPlaying)
                return FeatureFailure("PLAY_MODE_REQUIRED", "Live roster updates require Play Mode; Play Mode was not changed.");
            if (session == null)
                return FeatureFailure("MULTI_CHARACTER_ROOM_REQUIRED", "No active multi-character room session exists.");
            if (request.Operation == ConvaiCharacterRosterOperation.Add && membership != null)
                return FeatureFailure("CHARACTER_ALREADY_IN_ROOM", "The character is already a member of the active room.");
            if (request.Operation == ConvaiCharacterRosterOperation.Remove && membership == null)
                return FeatureFailure("CHARACTER_NOT_IN_ROOM", "The character is not a member of the active room.");
            if (replacement != null && replacementMembership == null)
                return FeatureFailure("REPLACEMENT_NOT_IN_ROOM", "The replacement target is not a member of the active room.");
            if (ReferenceEquals(character, replacement))
                return FeatureFailure("REPLACEMENT_MATCHES_REMOVAL", "The replacement target cannot be the character being removed.");
            if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                return RoomSessionChangedFailure(room, session);

            bool canonicalCommandRegistered = false;
            MultiCharacterRoomSession commandSession = null;
            void CommandRegistered(
                MultiCharacterRoomSession registeredSession,
                Task<CharacterRosterUpdateResult> _)
            {
                commandSession = registeredSession;
                canonicalCommandRegistered = true;
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
                CharacterRosterUpdateResult result = request.Operation == ConvaiCharacterRosterOperation.Add
                    ? await room.AddCharacterTrackedAsync(
                        character,
                        CommandRegistered,
                        cancellationToken: timeout.Token)
                    : await room.RemoveCharacterTrackedAsync(
                        membership.MembershipId,
                        replacementMembership?.MembershipId,
                        CommandRegistered,
                        timeout.Token);
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session) ||
                    (commandSession != null && !ReferenceEquals(commandSession, session)))
                    return RoomSessionChangedFailure(room, session);
                MultiCharacterRoomSession current = room.CurrentMultiCharacterSession;
                return Success(
                    request.Operation == ConvaiCharacterRosterOperation.Add
                        ? "Added the character to the active room roster."
                        : "Removed the character from the active room roster.",
                    new
                    {
                        dryRun = false,
                        executed = true,
                        operation = request.Operation.ToString(),
                        roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                        characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                        commandId = result.CommandId,
                        activeMembershipId = result.ActiveMembershipId,
                        routeEpoch = result.RouteEpoch,
                        rosterEpoch = result.RosterEpoch,
                        added = result.Added.Select(item => DescribeMembership(current, item)).ToArray(),
                        removed = result.Removed.Select(item => DescribeMembership(current, item)).ToArray(),
                        activeTarget = DescribeMembership(current, current?.FindByMembershipId(result.ActiveMembershipId))
                    });
            }
            catch (OperationCanceledException)
            {
                return RosterUpdateTimedOut(
                    room,
                    session,
                    character,
                    request.Operation,
                    canonicalCommandRegistered);
            }
            catch (TimeoutException)
            {
                return RosterUpdateTimedOut(
                    room,
                    session,
                    character,
                    request.Operation,
                    canonicalCommandRegistered);
            }
            catch (CharacterRosterUpdateException exception)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return FeatureFailure(
                    string.IsNullOrWhiteSpace(exception.Code) ? "ROSTER_UPDATE_REJECTED" : exception.Code,
                    exception.Message);
            }
            catch (ConvaiOperationException exception)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return FeatureFailure(exception.Code, exception.Message);
            }
            catch (ArgumentException exception)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return FeatureFailure("INVALID_ROSTER_UPDATE", exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                if (!ReferenceEquals(room.CurrentMultiCharacterSession, session))
                    return RoomSessionChangedFailure(room, session);
                return FeatureFailure("ROSTER_UPDATE_REJECTED", exception.Message);
            }
        }

        [McpSchema(UpdateCharacterRosterTool)]
        public static object UpdateCharacterRosterInputSchema() => ObjectSchema(
            new Dictionary<string, object>
            {
                ["operation"] = EnumProperty(
                    "Add or Remove one character from the active room roster.",
                    ConvaiCharacterRosterOperation.Add),
                ["roomManagerInstanceId"] = IntegerProperty(
                    "ConvaiRoomManager GameObject instance ID. Zero uses the only room manager in the loaded scenes.",
                    0),
                ["characterInstanceId"] = IntegerProperty("Character GameObject instance ID to add or remove."),
                ["replacementTargetCharacterInstanceId"] = IntegerProperty(
                    "Optional character GameObject instance ID to address after removing the current target.",
                    0),
                ["dryRun"] = BooleanProperty("Preview without contacting the active room.", true),
                ["timeoutSeconds"] = TimeoutProperty(15f, 15f)
            },
            "characterInstanceId");

        [McpOutputSchema(UpdateCharacterRosterTool)]
        public static object UpdateCharacterRosterOutputSchema() => StandardResponseSchema();

        private static object PendingRosterUpdate(
            ConvaiRoomManager room,
            MultiCharacterRoomSession originalSession,
            ConvaiCharacter character,
            ConvaiCharacterRosterOperation operation)
        {
            MultiCharacterRoomSession current = room.CurrentMultiCharacterSession;
            MultiCharacterRoomSession snapshot = current ?? originalSession;
            return Success(
                "The roster update is still pending.",
                new
                {
                    dryRun = false,
                    executed = true,
                    pending = true,
                    operation = operation.ToString(),
                    roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                    characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                    routeEpoch = snapshot?.RouteEpoch ?? 0,
                    rosterEpoch = snapshot?.RosterEpoch ?? 0,
                    roster = snapshot?.Characters.Select(item => DescribeMembership(snapshot, item)).ToArray() ??
                             Array.Empty<object>(),
                    guidance = "The canonical request remains active and may reconcile later. " +
                               "Use Convai.WaitForMultiCharacterState or Convai.DiagnoseConversation before retrying."
                });
        }

        private static object RosterUpdateTimedOut(
            ConvaiRoomManager room,
            MultiCharacterRoomSession originalSession,
            ConvaiCharacter character,
            ConvaiCharacterRosterOperation operation,
            bool canonicalCommandRegistered)
        {
            if (!ReferenceEquals(room.CurrentMultiCharacterSession, originalSession))
                return RoomSessionChangedFailure(room, originalSession);
            if (canonicalCommandRegistered)
                return PendingRosterUpdate(room, originalSession, character, operation);

            MultiCharacterRoomSession current = room.CurrentMultiCharacterSession;
            MultiCharacterRoomSession snapshot = current ?? originalSession;
            return Failure(
                "ROSTER_UPDATE_TIMEOUT",
                "The roster update timed out before a canonical command was registered; no request was sent.",
                new
                {
                    dryRun = false,
                    executed = false,
                    pending = false,
                    operation = operation.ToString(),
                    roomManagerInstanceId = ConvaiMcpEntityRef.ToToolId(room.gameObject),
                    characterInstanceId = ConvaiMcpEntityRef.ToToolId(character.gameObject),
                    routeEpoch = snapshot?.RouteEpoch ?? 0,
                    rosterEpoch = snapshot?.RosterEpoch ?? 0,
                    roster = snapshot?.Characters.Select(item => DescribeMembership(snapshot, item)).ToArray() ??
                             Array.Empty<object>()
                });
        }

        private static bool ValidTimeout(float seconds, float maximum = 60f) =>
            !float.IsNaN(seconds) && !float.IsInfinity(seconds) && seconds > 0f && seconds <= maximum;

        private static object TimeoutProperty(float defaultValue, float maximum) => new
        {
            type = "number",
            description = "Maximum seconds to wait for the authoritative response.",
            minimum = 0.1f,
            maximum,
            @default = defaultValue
        };

        private static object DescribeMembership(
            MultiCharacterRoomSession session,
            CharacterRoomMembership membership)
        {
            if (membership == null) return null;
            var character = membership.Character as ConvaiCharacter;
            return new
            {
                membershipId = membership.MembershipId,
                characterInstanceId = ConvaiMcpEntityRef.ToToolId(character?.gameObject),
                characterName = character?.CharacterName ?? string.Empty,
                status = membership.Status.ToString(),
                failureCode = membership.FailureCode ?? string.Empty,
                isInitial = membership.IsInitial,
                isActiveTarget = string.Equals(
                    session?.ActiveMembershipId,
                    membership.MembershipId,
                    StringComparison.Ordinal)
            };
        }
    }
}
