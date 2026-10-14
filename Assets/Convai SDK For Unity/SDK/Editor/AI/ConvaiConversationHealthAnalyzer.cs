using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.DomainEvents.Session;
using Convai.Editor.Diagnostics;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Conversation;
using Convai.Runtime.Core.Composition;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Sources;
using Convai.Shared.Compatibility;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Editor.AI
{
    internal enum ConvaiDiagnosticSeverity
    {
        Error,
        Warning,
        Info
    }

    internal sealed class ConvaiDiagnosticIssue
    {
        public string Code { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
        public string Evidence { get; set; }
        public long AffectedInstanceId { get; set; }
        public bool AutoFixable { get; set; }
        public string SuggestedTool { get; set; }
        public object SuggestedArguments { get; set; }
    }

    internal sealed class ConvaiConversationDiagnosis
    {
        public bool Success { get; set; } = true;
        public string FailureCode { get; set; } = string.Empty;
        public string FailureMessage { get; set; } = string.Empty;
        public bool ReadyToRun { get; set; }
        public string Mode { get; set; }
        public List<ConvaiDiagnosticIssue> Issues { get; } = new();
        public object Configuration { get; set; }
        public object Runtime { get; set; }
    }

    internal static class ConvaiConversationHealthAnalyzer
    {
        public static ConvaiConversationDiagnosis Analyze(ConvaiDiagnoseConversationRequest request)
        {
            request ??= new ConvaiDiagnoseConversationRequest();
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return new ConvaiConversationDiagnosis
                {
                    Success = false,
                    FailureCode = "NO_ACTIVE_SCENE",
                    FailureMessage = "No loaded active scene is available."
                };
            }

            ConvaiManager[] managers = ConvaiConversationAuthoringService.GetSceneComponents<ConvaiManager>(
                scene, request.IncludeInactive);
            ConvaiRoomManager[] rooms = ConvaiConversationAuthoringService.GetSceneComponents<ConvaiRoomManager>(
                scene, request.IncludeInactive);
            ConvaiPlayer[] players = GetLoadedSceneComponents<ConvaiPlayer>(request.IncludeInactive);
            ConvaiCharacter[] allCharacters = GetLoadedSceneComponents<ConvaiCharacter>(request.IncludeInactive);
            ConvaiCharacter[] characters = allCharacters;
            if (request.CharacterInstanceId != 0)
            {
                if (!ConvaiMcpResolvers.TryCharacter(request.CharacterInstanceId, request.IncludeInactive,
                        out ConvaiCharacter focused, out string error))
                {
                    return new ConvaiConversationDiagnosis
                    {
                        Success = false,
                        FailureCode = ConvaiMcpResolvers.CharacterErrorCode,
                        FailureMessage = error
                    };
                }

                characters = new[] { focused };
            }

            var diagnosis = new ConvaiConversationDiagnosis
            {
                Mode = EditorApplication.isPlaying ? "PlayMode" : "EditMode"
            };
            ConvaiRoomManager room = rooms.Length == 1 ? rooms[0] : null;
            ConvaiManager manager = managers.Length == 1 ? managers[0] : null;

            if (EditorApplication.isPlaying && room != null && !string.IsNullOrWhiteSpace(room.LastSessionErrorCode))
            {
                Add(diagnosis, "SESSION_ERROR", ConvaiDiagnosticSeverity.Error,
                    "The latest Convai session failed.",
                    $"{room.LastSessionErrorCode}: {room.LastSessionErrorMessage}", room.gameObject,
                    false, "Unity.ReadConsole", new { types = new[] { "Error", "Warning" } });
            }

            ConvaiSettings settings = ConvaiSettings.Instance;
            bool credentialsConfigured = settings != null && settings.HasApiKey;
            if (!credentialsConfigured)
            {
                Add(diagnosis, "PROJECT_API_KEY_MISSING", ConvaiDiagnosticSeverity.Error,
                    "Convai credentials are not configured.",
                    "ConvaiSettings.HasApiKey is false. The key value was not read or returned.", null,
                    false, string.Empty, new { manual = "Edit > Project Settings > Convai SDK" });
            }

            if (managers.Length == 0)
                Add(diagnosis, "MANAGER_MISSING", ConvaiDiagnosticSeverity.Error,
                    "Active scene has no ConvaiManager.", "Manager count is 0.", null, true,
                    "Convai.SetupConversationScene", new { dryRun = true });
            else if (managers.Length > 1)
                Add(diagnosis, "MANAGER_DUPLICATE", ConvaiDiagnosticSeverity.Error,
                    "Active scene has multiple ConvaiManager components.", $"Manager count is {managers.Length}.",
                    null, false, "Convai.InspectScene", new { includeInactive = true });

            if (rooms.Length == 0)
                Add(diagnosis, "ROOM_MISSING", ConvaiDiagnosticSeverity.Error,
                    "Active scene has no ConvaiRoomManager.", "Room manager count is 0.", null, true,
                    "Convai.ConfigureRoom", new { targetInstanceId = manager != null ? Id(manager.gameObject) : 0, dryRun = true });
            else if (rooms.Length > 1)
                Add(diagnosis, "ROOM_DUPLICATE", ConvaiDiagnosticSeverity.Error,
                    "Active scene has multiple ConvaiRoomManager components.", $"Room manager count is {rooms.Length}.",
                    null, false, "Convai.InspectScene", new { includeInactive = true });

            if (players.Length == 0)
                Add(diagnosis, "PLAYER_MISSING", ConvaiDiagnosticSeverity.Error,
                    "Nothing in this scene represents the person playing, so the conversation cannot start. " +
                    "Add a Convai Player component to your camera rig or first-person controller.",
                    "No ConvaiPlayer component exists in the loaded scenes.", null, true,
                    "Convai.SetupConversationScene", new { dryRun = true });
            else if (players.Length > 1 && (manager == null || ReadReference(manager, "_explicitPlayer") == null))
                Add(diagnosis, "PLAYER_AMBIGUOUS", ConvaiDiagnosticSeverity.Error,
                    "This scene has more than one Convai Player, so the SDK cannot tell which one is the " +
                    "person playing. Assign the right one to Convai Manager > Player.",
                    $"Player count is {players.Length}.", manager != null ? manager.gameObject : null, false,
                    "Convai.ConfigurePlayer", new { managerInstanceId = manager != null ? Id(manager.gameObject) : 0, dryRun = true });

            Object explicitPlayer = manager != null ? ReadReference(manager, "_explicitPlayer") : null;

            if (allCharacters.Length == 0)
                Add(diagnosis, "CHARACTER_MISSING", ConvaiDiagnosticSeverity.Error,
                    "This scene has nobody to talk to. Add a Convai Character component to a character and " +
                    "give it a Character ID from your Convai account.",
                    "No ConvaiCharacter component exists in the loaded scenes.", null, true,
                    "Convai.SetupConversationScene", new { dryRun = true });

            Object[] explicitCharacters = manager != null
                ? ReadReferences(manager, "_explicitCharacters")
                : Array.Empty<Object>();
            Object explicitConversationTarget = manager != null
                ? ReadReference(manager, "_explicitConversationTarget")
                : null;
            bool usesCharacterConnectionSelection = manager != null &&
                                                    ReadBool(manager, "_useCharacterConnectionSelection");
            ConvaiCharacter[] includedCharacters = manager != null
                ? ReadReferences(manager, "_includedCharacters").OfType<ConvaiCharacter>().ToArray()
                : Array.Empty<ConvaiCharacter>();
            ConvaiSceneInstaller sceneInstaller = manager != null
                ? ReadReference(manager, "_sceneInstaller") as ConvaiSceneInstaller
                : null;
            IReadOnlyList<ConvaiCharacter> ownedCharacters = manager != null
                ? ConvaiRuntimeHost.ResolveOwnedCharacters(
                    sceneInstaller,
                    explicitCharacters.OfType<ConvaiCharacter>().ToArray(),
                    explicitConversationTarget as ConvaiCharacter,
                    allCharacters)
                : Array.Empty<ConvaiCharacter>();
            ConvaiCharacter[] connectableCharacters = ownedCharacters
                .Where(character => character != null && character.isActiveAndEnabled &&
                                    ConvaiRuntimeHost.IsCharacterSelectedForConnection(
                                        character,
                                        usesCharacterConnectionSelection,
                                        includedCharacters))
                .ToArray();
            ConvaiCharacter[] charactersToValidate = request.CharacterInstanceId != 0 || manager == null
                ? characters
                : connectableCharacters;

            // Same rule the room uses when Initial Character is left empty, so this report and the
            // running scene always name the same starting character.
            ConvaiCharacter defaultInitialCharacter = ConvaiRuntimeHost.ResolveDefaultInitialCharacter(
                ownedCharacters,
                usesCharacterConnectionSelection,
                includedCharacters);

            if (manager != null && usesCharacterConnectionSelection && connectableCharacters.Length == 0)
                Add(diagnosis, "CHARACTER_SELECTION_EMPTY", ConvaiDiagnosticSeverity.Error,
                    "No active characters are selected for the next room connection.",
                    $"The manager owns {ownedCharacters.Count} characters, but its explicit room selection resolves to none.",
                    manager.gameObject, false, "Convai.InspectScene", new
                    {
                        includeInactive = true,
                        manual = "Select at least one active character in the Convai Manager Inspector."
                    });
            else if (manager != null && connectableCharacters.Length > 1 && explicitConversationTarget == null &&
                     defaultInitialCharacter != null)
                // Runtime no longer refuses to start here: the first character in scene order takes the
                // first turn. Reporting this as an error would send the reader looking for a fault that
                // does not stop anything. It stays listed so the choice the scene left open is visible.
                Add(diagnosis, "CONVERSATION_TARGET_DEFAULTED", ConvaiDiagnosticSeverity.Info,
                    $"The conversation will start on '{defaultInitialCharacter.gameObject.name}'. " +
                    "Set Convai Manager > Initial Character to start on a different character.",
                    $"{connectableCharacters.Length} characters can join this room and no Initial Character is assigned.",
                    manager.gameObject, true,
                    "Convai.ConfigureCharacter", new
                    {
                        targetInstanceId = Id(defaultInitialCharacter.gameObject),
                        managerInstanceId = Id(manager.gameObject),
                        dryRun = true
                    });
            else if (explicitConversationTarget != null &&
                     (explicitConversationTarget is not ConvaiCharacter explicitTargetCharacter ||
                      !ownedCharacters.Contains(explicitTargetCharacter) ||
                      !explicitTargetCharacter.isActiveAndEnabled ||
                      !ConvaiRuntimeHost.IsCharacterSelectedForConnection(
                          explicitTargetCharacter,
                          usesCharacterConnectionSelection,
                          includedCharacters)))
                Add(diagnosis, "CONVERSATION_TARGET_INVALID", ConvaiDiagnosticSeverity.Error,
                    "ConvaiManager conversation target is not an active owned character.",
                    $"Explicit target instance is {Id(explicitConversationTarget)}.", manager.gameObject, false,
                    "Convai.InspectScene", new { includeInactive = true });

            foreach (ConvaiCharacter character in charactersToValidate)
            {
                if (string.IsNullOrWhiteSpace(character.CharacterId))
                    Add(diagnosis, "CHARACTER_ID_MISSING", ConvaiDiagnosticSeverity.Error,
                        $"Character '{character.gameObject.name}' has no Character ID.",
                        "Effective CharacterId is empty.", character.gameObject, true,
                        "Convai.ConfigureCharacter", new { targetInstanceId = Id(character.gameObject), dryRun = true });
                else if (!ConvaiConversationAuthoringService.IsValidCharacterId(character.CharacterId))
                    Add(diagnosis, "CHARACTER_ID_INVALID", ConvaiDiagnosticSeverity.Error,
                        $"Character '{character.gameObject.name}' has an invalid Character ID.",
                        character.CharacterId, character.gameObject, true,
                        "Convai.ConfigureCharacter", new { targetInstanceId = Id(character.gameObject), dryRun = true });

                if (character.GetComponent<ConvaiAudioOutput>() == null)
                    Add(diagnosis, "CHARACTER_AUDIO_OUTPUT_MISSING", ConvaiDiagnosticSeverity.Error,
                        $"Character '{character.gameObject.name}' cannot play remote audio through the recommended output component.",
                        "ConvaiAudioOutput is missing.", character.gameObject, true,
                        "Convai.ConfigureCharacter",
                        new { targetInstanceId = Id(character.gameObject), addAudioOutput = true, dryRun = true });
                if (character.GetComponent<AudioSource>() == null)
                    Add(diagnosis, "CHARACTER_AUDIO_SOURCE_MISSING", ConvaiDiagnosticSeverity.Error,
                        $"Character '{character.gameObject.name}' has no AudioSource.",
                        "AudioSource is missing.", character.gameObject, true,
                        "Convai.ConfigureCharacter",
                        new { targetInstanceId = Id(character.gameObject), addAudioOutput = true, dryRun = true });
            }

            // Asked of the same rule the Character inspector draws and Convai.ValidateSetup reports,
            // so the three surfaces cannot disagree about what counts as a duplicate.
            //
            // Every character in the loaded scenes, not only the ones that would join the next room.
            // This was scoped to the connectable set, which quietly excused the commonest shape of
            // the mistake: a duplicate that starts the scene disabled is not connectable, so nothing
            // here mentioned it — and it collides the instant somebody enables it. The message names
            // the GameObjects, so a reader can see for themselves which of them is switched off.
            var duplicateCandidates = new List<ConvaiCharacter>(allCharacters);
            var idConflicts = new List<ConvaiCharacterIdConflict>();
            ConvaiCharacterIdConflicts.Collect(duplicateCandidates, idConflicts);

            foreach (ConvaiCharacterIdConflict conflict in idConflicts)
            {
                // Pointed at the second character holding the ID rather than at nothing. One of
                // them keeps the ID and the others are the copies to fix, and by scene order the
                // first is the likelier original — so this names something the reader can click
                // instead of leaving them to work out which of two names to go and look at.
                ConvaiCharacter copy = conflict.Characters[1];
                Add(diagnosis, "CHARACTER_ID_DUPLICATE", ConvaiDiagnosticSeverity.Error,
                    "Multiple scene characters use the same Character ID.",
                    $"{conflict.CharacterId} is assigned to " +
                    $"{string.Join(", ", conflict.Characters.Select(item => item.gameObject.name))}. " +
                    "The SDK keys ownership, participants and audio by Character ID, so they collide " +
                    "rather than each being routed separately.",
                    copy != null ? copy.gameObject : null, false,
                    "Convai.InspectScene", new { includeInactive = true });
            }

            if (room != null && room.EffectiveConnectionType == ConvaiConnectionType.Video &&
                room.EffectiveVisionContextEnabled && !ConvaiSceneQueries.HasCompleteVisionPipeline(room.gameObject))
            {
                Add(diagnosis, "VIDEO_PIPELINE_INCOMPLETE", ConvaiDiagnosticSeverity.Error,
                    "Video room is missing its dynamic-vision publisher or frame source.",
                    "Expected IVisionPublisher and IVisionFrameSource under ConvaiRoomManager.", room.gameObject,
                    false, "Convai.GetGuidance", new { topic = "Vision" });
            }

            if (EditorApplication.isPlaying && room != null && room.CurrentState.ToString() == "Disconnected" &&
                string.IsNullOrWhiteSpace(room.LastSessionErrorCode))
            {
                Add(diagnosis, "SESSION_DISCONNECTED", ConvaiDiagnosticSeverity.Warning,
                    "Conversation room is currently disconnected.",
                    $"connectOnStart={room.EffectiveConnectOnStart}, attempts={room.ConnectAttemptCount}.",
                    room.gameObject, false, "Unity.ReadConsole", new { types = new[] { "Error", "Warning" } });
            }

            diagnosis.ReadyToRun = diagnosis.Issues.All(issue => issue.Severity != ConvaiDiagnosticSeverity.Error.ToString());
            diagnosis.Configuration = new
            {
                credentialsConfigured,
                activeScene = scene.name,
                managerCount = managers.Length,
                roomCount = rooms.Length,
                playerCount = players.Length,
                characterCount = allCharacters.Length,
                managerInstanceId = manager != null ? Id(manager.gameObject) : 0,
                roomInstanceId = room != null ? Id(room.gameObject) : 0,
                effectiveConnectionType = room?.EffectiveConnectionType.ToString() ?? string.Empty,
                effectiveInputMode = room?.EffectiveTurnTakingOptions.Mode.ToString() ?? string.Empty,
                connectOnStart = room?.EffectiveConnectOnStart ?? false,
                serverEndpoint = room?.EffectiveServerEndpoint.ToString() ?? string.Empty,
                visionMode = room?.EffectiveVisionContextMode.ToString() ?? string.Empty,
                visionContextEnabled = room?.EffectiveVisionContextEnabled ?? false,
                pushToTalkKey = room?.PushToTalkKey.ToString() ?? string.Empty,
                explicitPlayerInstanceId = SceneObjectId(explicitPlayer),
                explicitCharacterInstanceIds = explicitCharacters.Select(SceneObjectId).ToArray(),
                usesCharacterConnectionSelection,
                includedCharacterInstanceIds = includedCharacters.Select(character => Id(character.gameObject)).ToArray(),
                explicitConversationTargetInstanceId = SceneObjectId(explicitConversationTarget),
                conversationTargeting = DescribeConversationTargeting(manager),
                ownedCharacterInstanceIds = ownedCharacters.Select(character => Id(character.gameObject)).ToArray(),
                connectableCharacterInstanceIds = connectableCharacters.Select(character => Id(character.gameObject)).ToArray(),
                player = players.Length == 1
                    ? new { instanceId = Id(players[0].gameObject), players[0].PlayerName, players[0].PlayerId }
                    : null,
                characters = characters.Select(character => new
                {
                    instanceId = Id(character.gameObject),
                    character.CharacterId,
                    character.CharacterName,
                    includedInRoom = manager != null && ownedCharacters.Contains(character) &&
                                     ConvaiRuntimeHost.IsCharacterSelectedForConnection(
                                         character,
                                         usesCharacterConnectionSelection,
                                         includedCharacters),
                    hasAudioOutput = character.GetComponent<ConvaiAudioOutput>() != null,
                    hasAudioSource = character.GetComponent<AudioSource>() != null
                }).ToArray()
            };
            diagnosis.Runtime = new
            {
                managerInitialized = manager?.IsInitialized ?? false,
                managerConnected = manager?.IsConnected ?? false,
                sessionState = room?.CurrentState.ToString() ?? string.Empty,
                roomConnected = room?.IsConnected ?? false,
                roomName = room?.CurrentRoomName ?? string.Empty,
                sessionId = room?.CurrentSessionId ?? string.Empty,
                characterSessionId = room?.CurrentCharacterSessionId ?? string.Empty,
                micMuted = room?.IsMicMuted ?? false,
                requiresUserGestureForAudio = room?.RequiresUserGestureForAudio ?? false,
                connectAttemptCount = room?.ConnectAttemptCount ?? 0,
                reconnectCount = room?.ReconnectCount ?? 0,
                lastSessionErrorCode = room?.LastSessionErrorCode ?? string.Empty,
                lastSessionErrorMessage = room?.LastSessionErrorMessage ?? string.Empty,
                addressedCharacter = manager != null && manager.AddressedCharacter != null
                    ? manager.AddressedCharacter.CharacterName
                    : string.Empty,
                addressedCharacterInstanceId = manager != null && manager.AddressedCharacter != null
                    ? Id(manager.AddressedCharacter.gameObject)
                    : 0,
                conversationAvailability = manager != null
                    ? manager.ConversationAvailability.ToString()
                    : ConvaiConversationAvailability.NoCharacter.ToString(),
                playerCanTalk = manager != null && manager.ConversationAvailability.CanAcceptPlayerInput(),
                characters = characters.Select(character => new
                {
                    instanceId = Id(character.gameObject),
                    sessionState = character.SessionState.ToString(),
                    character.IsCharacterReady,
                    character.IsInConversation,
                    character.IsSpeaking,
                    conversationAvailability = character.ConversationAvailability.ToString()
                }).ToArray(),
                multiCharacter = DescribeMultiCharacterSession(manager, room)
            };
            return diagnosis;
        }

        /// <summary>
        ///     Collects components the runtime would own: everything in the loaded scenes, plus
        ///     everything living in <c>DontDestroyOnLoad</c> once Play Mode has started.
        /// </summary>
        /// <remarks>
        ///     A player rig that persists across scene loads leaves its scene during <c>Awake</c>, and
        ///     <c>SceneManager</c> never lists the scene it lands in. Without the second pass this
        ///     analyzer reported "no ConvaiPlayer" for a scene that visibly has one, which sends the
        ///     reader looking for a missing component instead of at the real problem. Ownership
        ///     resolution makes the same allowance, so the two agree on what exists.
        /// </remarks>
        /// <summary>
        ///     Reports the live roster, who is being addressed, and why the last decision went that way.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The diagnosis said nothing about multi-character rooms at all, which left the two
        ///         questions people actually ask about them unanswerable from here: which characters
        ///         made it into the room, and who is the player talking to.
        ///     </para>
        ///     <para>
        ///         The verdict matters as much as the target. A conversation that is holding correctly
        ///         and one that is stuck look identical from outside — both simply stay where they are —
        ///         and this is the only thing that separates them.
        ///     </para>
        /// </remarks>
        /// <summary>
        ///     The rule that decides who the player is talking to, values and all.
        /// </summary>
        /// <remarks>
        ///     The numbers matter as much as the mode. "The conversation flickers between two
        ///     characters" and "looking at a character does nothing" are both answered by them, and
        ///     without them a reader can only guess whether a scene is on the shipped defaults.
        ///     Reported from configuration rather than runtime, because they are authored values that
        ///     exist before the room does.
        /// </remarks>
        private static object DescribeConversationTargeting(ConvaiManager manager)
        {
            ConversationTargetingOptions targeting = manager?.ConversationTargeting;
            if (targeting == null) return null;

            ConversationTargetingOptions defaults = ConversationTargetingOptions.CreateDefault();
            return new
            {
                mode = targeting.Mode.ToString(),
                within = targeting.MaxDistance,
                lookAngle = targeting.MaxAngle,
                switchMargin = targeting.SwitchMargin,
                switchDelaySeconds = targeting.SwitchDelaySeconds,
                isShippedDefault =
                    targeting.Mode == defaults.Mode &&
                    Mathf.Approximately(targeting.MaxDistance, defaults.MaxDistance) &&
                    Mathf.Approximately(targeting.MaxAngle, defaults.MaxAngle) &&
                    Mathf.Approximately(targeting.SwitchMargin, defaults.SwitchMargin) &&
                    Mathf.Approximately(targeting.SwitchDelaySeconds, defaults.SwitchDelaySeconds),
                appliesOnlyToLookAt = new[] { "lookAngle", "switchMargin" }
            };
        }

        private static object DescribeMultiCharacterSession(ConvaiManager manager, ConvaiRoomManager room)
        {
            MultiCharacterRoomSession session = room?.CurrentMultiCharacterSession;
            if (session == null)
                return new { isMultiCharacterRoom = false };

            ConvaiCharacter target = manager != null ? manager.ConversationTarget : null;
            return new
            {
                isMultiCharacterRoom = true,
                session.RoomSessionId,
                session.IsReady,
                session.RouteEpoch,
                session.RosterEpoch,
                session.PartialDispatch,
                activeMembershipId = session.ActiveMembershipId,
                conversationTarget = target != null ? target.CharacterName : string.Empty,
                conversationTargetInstanceId = target != null ? Id(target.gameObject) : 0,
                targetingMode = manager?.ConversationTargeting?.Mode.ToString() ?? string.Empty,
                targetingVerdict = manager != null ? manager.ConversationTargetingStatus.ToString() : string.Empty,
                roster = session.Characters.Select(membership => new
                {
                    membership.MembershipId,
                    membership.CharacterId,
                    status = membership.Status.ToString(),
                    membership.FailureCode,
                    membership.IsInitial,
                    isActiveTarget = string.Equals(
                        membership.MembershipId,
                        session.ActiveMembershipId,
                        StringComparison.Ordinal),
                    participantBound = !string.IsNullOrEmpty(membership.ParticipantId),
                    name = membership.Character?.CharacterName ?? string.Empty
                }).ToArray()
            };
        }

        private static T[] GetLoadedSceneComponents<T>(bool includeInactive) where T : Component
        {
            var components = new List<T>();
            var enumeratedSceneHandles = new HashSet<long>();
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene loadedScene = SceneManager.GetSceneAt(sceneIndex);
                if (!loadedScene.IsValid() || !loadedScene.isLoaded)
                    continue;

                enumeratedSceneHandles.Add(ConvaiSceneId.Of(loadedScene));
                components.AddRange(
                    ConvaiConversationAuthoringService.GetSceneComponents<T>(loadedScene, includeInactive));
            }

            if (!EditorApplication.isPlaying)
                return components.ToArray();

            foreach (T candidate in ConvaiObjectFind.All<T>(includeInactive))
            {
                if (candidate == null)
                    continue;

                Scene candidateScene = candidate.gameObject.scene;
                if (!candidateScene.IsValid() || enumeratedSceneHandles.Contains(ConvaiSceneId.Of(candidateScene)))
                    continue;

                if (!components.Contains(candidate))
                    components.Add(candidate);
            }

            return components.ToArray();
        }

        private static void Add(ConvaiConversationDiagnosis diagnosis, string code,
            ConvaiDiagnosticSeverity severity, string message, string evidence, GameObject affected,
            bool autoFixable, string suggestedTool, object suggestedArguments)
        {
            diagnosis.Issues.Add(new ConvaiDiagnosticIssue
            {
                Code = code,
                Severity = severity.ToString(),
                Message = message,
                Evidence = evidence,
                AffectedInstanceId = Id(affected),
                AutoFixable = autoFixable,
                SuggestedTool = suggestedTool,
                SuggestedArguments = suggestedArguments
            });
        }

        private static long Id(Object value) => ConvaiConversationAuthoringService.EntityIdOf(value);

        private static long SceneObjectId(Object value) =>
            Id(value is Component component ? component.gameObject : value);

        private static Object ReadReference(ConvaiManager manager, string propertyName)
        {
            var serialized = new SerializedObject(manager);
            return serialized.FindProperty(propertyName)?.objectReferenceValue;
        }

        private static Object[] ReadReferences(ConvaiManager manager, string propertyName)
        {
            var serialized = new SerializedObject(manager);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray) return Array.Empty<Object>();
            var values = new Object[property.arraySize];
            for (int i = 0; i < property.arraySize; i++)
                values[i] = property.GetArrayElementAtIndex(i).objectReferenceValue;
            return values;
        }

        private static bool ReadBool(ConvaiManager manager, string propertyName)
        {
            var serialized = new SerializedObject(manager);
            return serialized.FindProperty(propertyName)?.boolValue == true;
        }

    }
}
