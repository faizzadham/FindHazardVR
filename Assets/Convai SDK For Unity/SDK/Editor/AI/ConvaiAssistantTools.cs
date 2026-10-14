using System.Threading.Tasks;
using System.Collections.Generic;
using Convai.Runtime.Conversation;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Context;
using Convai.Shared.Types;
using Unity.AI.Assistant.FunctionCalling;

namespace Convai.Editor.AI
{
    /// <summary>
    ///     Direct Unity Assistant wrappers for the Convai foundation tools.
    ///     External MCP clients use the matching <c>Convai_*</c> tools.
    /// </summary>
    public static class ConvaiAssistantTools
    {
        [AgentTool(
            "Loads topic-specific Convai SDK workflow guidance. Call before configuring or debugging a Convai feature.",
            "Convai.GetGuidance")]
        public static object GetGuidance(
            [ToolParameter("Convai workflow topic to load.")]
            ConvaiGuidanceTopic topic = ConvaiGuidanceTopic.Overview) =>
            ConvaiMcpTools.GetGuidance(new ConvaiGuidanceRequest { Topic = topic });

        [AgentTool(
            "Reads Convai SDK, Unity Assistant, and non-secret project configuration status. Never returns the API key.",
            "Convai.GetProjectStatus")]
        public static object GetProjectStatus() => ConvaiMcpTools.GetProjectStatus();

        [AgentTool(
            "Inspects open scenes for Convai managers, rooms, players, and characters and returns exact instance IDs.",
            "Convai.InspectScene")]
        public static object InspectScene(
            [ToolParameter("Include disabled GameObjects and components.")]
            bool includeInactive = true) =>
            ConvaiMcpTools.InspectScene(new ConvaiSceneInspectionRequest { IncludeInactive = includeInactive });

        [AgentTool(
            "Validates Convai project and scene readiness without changing assets or scenes. Call before and after setup changes.",
            "Convai.ValidateSetup")]
        public static object ValidateSetup(
            [ToolParameter("Validation scope: All, Project, or Scene.")]
            ConvaiValidationScope scope = ConvaiValidationScope.All) =>
            ConvaiMcpTools.ValidateSetup(new ConvaiValidationRequest { Scope = scope });

        [AgentTool(
            "Previews or idempotently adds ConvaiManager and ConvaiRoomManager in Edit Mode. Preview first; never saves the scene or sets credentials.",
            "Convai.BootstrapScene")]
        public static object BootstrapScene(
            [ToolParameter("True previews changes; false applies them through Unity Undo.")]
            bool dryRun = true) =>
            ConvaiMcpTools.BootstrapScene(new ConvaiBootstrapRequest { DryRun = dryRun });

        [AgentTool(
            "Previews or configures ConvaiManager and ConvaiRoomManager on an explicit GameObject. Uses Undo and never saves or sets credentials.",
            "Convai.ConfigureRoom")]
        public static object ConfigureRoom(
            [ToolParameter("Target room GameObject instance ID.")] long targetInstanceId,
            [ToolParameter("Inline or ExistingProfile.")] ConvaiToolConfigurationMode configurationMode =
                ConvaiToolConfigurationMode.Inline,
            [ToolParameter("Existing ConvaiRoomManagerProfile path.")] string profileAssetPath = "",
            [ToolParameter("Audio or Video.")] ConvaiConnectionType connectionType = ConvaiConnectionType.Audio,
            [ToolParameter("HandsFree or PushToTalk.")] ConversationInputMode inputMode =
                ConversationInputMode.HandsFree,
            [ToolParameter("Connect automatically when the scene starts.")] bool connectOnStart = true,
            [ToolParameter("Connect, RoomSession, or DemoConnect.")] ConvaiServerEndpoint serverEndpoint =
                ConvaiServerEndpoint.Connect,
            [ToolParameter("Auto, Enabled, or Disabled.")] ConvaiVisionContextMode visionMode =
                ConvaiVisionContextMode.Auto,
            [ToolParameter("Unity KeyCode name for push-to-talk.")] string pushToTalkKey = "T",
            [ToolParameter("True previews; false applies through Unity Undo.")] bool dryRun = true) =>
            ConvaiMcpTools.ConfigureRoom(new ConvaiConfigureRoomRequest
            {
                TargetInstanceId = targetInstanceId,
                ConfigurationMode = configurationMode,
                ProfileAssetPath = profileAssetPath,
                ConnectionType = connectionType,
                InputMode = inputMode,
                ConnectOnStart = connectOnStart,
                ServerEndpoint = serverEndpoint,
                VisionMode = visionMode,
                PushToTalkKey = pushToTalkKey,
                DryRun = dryRun
            });

        [AgentTool(
            "Configure which characters join the next Convai room and how the SDK decides which one the player is talking to. Every tuning value is optional; -1 leaves a number unchanged.",
            "Convai.ConfigureConversationTargeting")]
        public static object ConfigureConversationTargeting(
            [ToolParameter("ConvaiManager GameObject instance ID. 0 uses the only manager in the loaded scenes.")]
            long managerInstanceId = 0,
            [ToolParameter("LookAt, Proximity, or Manual. Empty leaves it unchanged.")]
            string targetingMode = null,
            [ToolParameter("Range, in metres. -1 leaves it unchanged.")] float maxDistance = -1f,
            [ToolParameter("Look Angle, in degrees. -1 leaves it unchanged.")] float maxAngle = -1f,
            [ToolParameter("Switch Margin, in degrees. -1 leaves it unchanged.")] float switchMargin = -1f,
            [ToolParameter("Switch Delay, in seconds. -1 leaves it unchanged.")] float switchDelaySeconds = -1f,
            [ToolParameter("Player Camera: the Camera GameObject instance ID. 0 leaves it unchanged.")]
            long viewCameraInstanceId = 0,
            [ToolParameter("Clear Player Camera so the main camera is used again.")] bool clearViewCamera = false,
            [ToolParameter("Initial Character GameObject instance ID. 0 leaves it unchanged.")]
            long initialCharacterInstanceId = 0,
            [ToolParameter("Clear Initial Character.")] bool clearInitialCharacter = false,
            [ToolParameter("Characters Joining the Room: the exact set of character GameObject instance IDs.")]
            long[] includedCharacterInstanceIds = null,
            [ToolParameter("Send every active character to the next room.")] bool includeAllCharacters = false,
            [ToolParameter("True previews; false applies through Unity Undo.")] bool dryRun = true) =>
            ConvaiMcpTools.ConfigureConversationTargeting(new ConvaiConfigureConversationTargetingRequest
            {
                ManagerInstanceId = managerInstanceId,
                TargetingMode = ParseTargetingMode(targetingMode),
                MaxDistance = maxDistance >= 0f ? maxDistance : null,
                MaxAngle = maxAngle >= 0f ? maxAngle : null,
                SwitchMargin = switchMargin >= 0f ? switchMargin : null,
                SwitchDelaySeconds = switchDelaySeconds >= 0f ? switchDelaySeconds : null,
                ViewCameraInstanceId = viewCameraInstanceId != 0 ? viewCameraInstanceId : null,
                ClearViewCamera = clearViewCamera ? true : null,
                InitialCharacterInstanceId = initialCharacterInstanceId != 0 ? initialCharacterInstanceId : null,
                ClearInitialCharacter = clearInitialCharacter ? true : null,
                IncludedCharacterInstanceIds = includedCharacterInstanceIds,
                IncludeAllCharacters = includeAllCharacters ? true : null,
                DryRun = dryRun
            });

        [AgentTool(
            "Preview or change which character the player is addressing in an active multi-character room. Execution requires Play Mode and dryRun false; never changes Play Mode.",
            "Convai.SetConversationTarget")]
        public static Task<object> SetConversationTarget(
            [ToolParameter("Character GameObject instance ID to address.")]
            long characterInstanceId,
            [ToolParameter("ConvaiManager GameObject instance ID. 0 uses the only manager in the loaded scenes.")]
            long managerInstanceId = 0,
            [ToolParameter("True previews; false contacts the active room.")] bool dryRun = true,
            [ToolParameter("Maximum seconds to wait for the authoritative response.")]
            float timeoutSeconds = 10f) =>
            ConvaiMcpTools.SetConversationTarget(new ConvaiSetConversationTargetRequest
            {
                ManagerInstanceId = managerInstanceId,
                CharacterInstanceId = characterInstanceId,
                DryRun = dryRun,
                TimeoutSeconds = timeoutSeconds
            });

        [AgentTool(
            "Preview or add/remove one character in an active multi-character room. Execution requires Play Mode and dryRun false; never changes Play Mode.",
            "Convai.UpdateCharacterRoster")]
        public static Task<object> UpdateCharacterRoster(
            [ToolParameter("Add or Remove.")] ConvaiCharacterRosterOperation operation,
            [ToolParameter("Character GameObject instance ID to add or remove.")]
            long characterInstanceId,
            [ToolParameter("ConvaiRoomManager GameObject instance ID. 0 uses the only room manager in the loaded scenes.")]
            long roomManagerInstanceId = 0,
            [ToolParameter("Optional character GameObject instance ID to address after removing the current target.")]
            long replacementTargetCharacterInstanceId = 0,
            [ToolParameter("True previews; false contacts the active room.")] bool dryRun = true,
            [ToolParameter("Maximum seconds to wait for the authoritative response.")]
            float timeoutSeconds = 15f) =>
            ConvaiMcpTools.UpdateCharacterRoster(new ConvaiUpdateCharacterRosterRequest
            {
                Operation = operation,
                RoomManagerInstanceId = roomManagerInstanceId,
                CharacterInstanceId = characterInstanceId,
                ReplacementTargetCharacterInstanceId = replacementTargetCharacterInstanceId,
                DryRun = dryRun,
                TimeoutSeconds = timeoutSeconds
            });

        [AgentTool(
            "Wait for one current room member to become Ready. The wait never cancels character startup or changes Play Mode.",
            "Convai.WaitForCharacterReady")]
        public static Task<object> WaitForCharacterReady(
            [ToolParameter("Character GameObject instance ID whose membership must become Ready.")]
            long characterInstanceId,
            [ToolParameter("ConvaiRoomManager GameObject instance ID. 0 uses the only room manager in the loaded scenes.")]
            long roomManagerInstanceId = 0,
            [ToolParameter("Maximum seconds to wait.")]
            float timeoutSeconds = 30f) =>
            ConvaiMcpTools.WaitForCharacterReady(new ConvaiWaitForCharacterReadyRequest
            {
                CharacterInstanceId = characterInstanceId,
                RoomManagerInstanceId = roomManagerInstanceId,
                TimeoutSeconds = timeoutSeconds
            });

        [AgentTool(
            "Explain the geometric proposal and any live room-order recovery fallback from LookAt or Proximity targeting without claiming the temporal switch gates have committed it. Read-only and never contacts the backend.",
            "Convai.SimulateConversationTargeting")]
        public static object SimulateConversationTargeting(
            [ToolParameter("ConvaiManager GameObject instance ID. 0 uses the only manager in the loaded scenes.")]
            long managerInstanceId = 0,
            [ToolParameter("Camera GameObject instance ID. 0 uses the authored camera, Main Camera, then player.")]
            long viewCameraInstanceId = 0,
            [ToolParameter("Include inactive characters in the report; they remain ineligible.")]
            bool includeInactive = true) =>
            ConvaiMcpTools.SimulateConversationTargeting(new ConvaiSimulateConversationTargetingRequest
            {
                ManagerInstanceId = managerInstanceId,
                ViewCameraInstanceId = viewCameraInstanceId,
                IncludeInactive = includeInactive
            });

        [AgentTool(
            "Preview or configure the exact roster of at least two characters for the next room and optionally choose who speaks first.",
            "Convai.SetupMultiCharacterRoster")]
        public static object SetupMultiCharacterRoster(
            [ToolParameter("Exact set of at least two character GameObject instance IDs.")]
            long[] characterInstanceIds,
            [ToolParameter("ConvaiManager GameObject instance ID. 0 uses the only manager in the loaded scenes.")]
            long managerInstanceId = 0,
            [ToolParameter("Optional roster member that speaks first. 0 leaves it unchanged.")]
            long initialCharacterInstanceId = 0,
            [ToolParameter("True previews; false applies through Unity Undo.")]
            bool dryRun = true) =>
            ConvaiMcpTools.SetupMultiCharacterRoster(new ConvaiSetupMultiCharacterRosterRequest
            {
                CharacterInstanceIds = characterInstanceIds,
                ManagerInstanceId = managerInstanceId,
                InitialCharacterInstanceId = initialCharacterInstanceId,
                DryRun = dryRun
            });

        [AgentTool(
            "Wait for authoritative target, roster, epoch, readiness, or conversation-availability conditions in the active multi-character room; with no conditions, return a snapshot.",
            "Convai.WaitForMultiCharacterState")]
        public static Task<object> WaitForMultiCharacterState(
            [ToolParameter("ConvaiManager GameObject instance ID. 0 uses the only manager in the loaded scenes.")]
            long managerInstanceId = 0,
            [ToolParameter("Optional character GameObject instance ID that must become the active target.")]
            long expectedActiveCharacterInstanceId = 0,
            [ToolParameter("Expected roster size. -1 omits this condition.")]
            int expectedRosterSize = -1,
            [ToolParameter("Minimum roster epoch. -1 omits this condition.")]
            int minimumRosterEpoch = -1,
            [ToolParameter("Minimum route epoch. -1 omits this condition.")]
            int minimumRouteEpoch = -1,
            [ToolParameter("Wait until every current roster member is Ready.")]
            bool requireAllCharactersReady = false,
            [ToolParameter("Wait until the addressed character can accept player input.")]
            bool requireConversationAvailable = false,
            [ToolParameter("Maximum seconds to wait.")]
            float timeoutSeconds = 30f) =>
            ConvaiMcpTools.WaitForMultiCharacterState(new ConvaiWaitForMultiCharacterStateRequest
            {
                ManagerInstanceId = managerInstanceId,
                ExpectedActiveCharacterInstanceId = expectedActiveCharacterInstanceId,
                ExpectedRosterSize = expectedRosterSize,
                MinimumRosterEpoch = minimumRosterEpoch,
                MinimumRouteEpoch = minimumRouteEpoch,
                RequireAllCharactersReady = requireAllCharactersReady,
                RequireConversationAvailable = requireConversationAvailable,
                TimeoutSeconds = timeoutSeconds
            });

        /// <summary>
        ///     Reads a targeting mode written by an assistant, and treats an unknown one as "leave it
        ///     alone" rather than as LookAt.
        /// </summary>
        /// <remarks>
        ///     Silently defaulting a misspelled mode would change the scene to something nobody asked
        ///     for and report success, which is worse than the setting staying put.
        /// </remarks>
        private static ConversationTargetingMode? ParseTargetingMode(string value) =>
            System.Enum.TryParse(value, true, out ConversationTargetingMode parsed) ? parsed : null;

        [AgentTool(
            "Previews or adds and configures ConvaiPlayer on an explicit GameObject, then binds an unambiguous manager. Never modifies Main Camera.",
            "Convai.ConfigurePlayer")]
        public static object ConfigurePlayer(
            [ToolParameter("Target player GameObject instance ID.")] long targetInstanceId,
            [ToolParameter("Optional manager GameObject instance ID.")] long managerInstanceId = 0,
            [ToolParameter("Player display name.")] string playerName = "Player",
            [ToolParameter("Optional local transcript attribution ID.")] string playerId = "",
            [ToolParameter("True previews; false applies through Unity Undo.")] bool dryRun = true) =>
            ConvaiMcpTools.ConfigurePlayer(new ConvaiConfigurePlayerRequest
            {
                TargetInstanceId = targetInstanceId,
                ManagerInstanceId = managerInstanceId,
                PlayerName = playerName,
                PlayerId = playerId,
                DryRun = dryRun
            });

        [AgentTool(
            "Previews or adds and configures ConvaiCharacter plus recommended audio output on an explicit GameObject.",
            "Convai.ConfigureCharacter")]
        public static object ConfigureCharacter(
            [ToolParameter("Target character GameObject instance ID.")] long targetInstanceId,
            [ToolParameter("Optional manager GameObject instance ID.")] long managerInstanceId = 0,
            [ToolParameter("Inline or ExistingProfile.")] ConvaiToolConfigurationMode configurationMode =
                ConvaiToolConfigurationMode.Inline,
            [ToolParameter("Existing ConvaiCharacterProfile path.")] string profileAssetPath = "",
            [ToolParameter("Convai dashboard Character ID.")] string characterId = "",
            [ToolParameter("Character display name.")] string characterName = "",
            [ToolParameter("Ensure AudioSource and ConvaiAudioOutput.")] bool addAudioOutput = true,
            [ToolParameter("True previews; false applies through Unity Undo.")] bool dryRun = true) =>
            ConvaiMcpTools.ConfigureCharacter(new ConvaiConfigureCharacterRequest
            {
                TargetInstanceId = targetInstanceId,
                ManagerInstanceId = managerInstanceId,
                ConfigurationMode = configurationMode,
                ProfileAssetPath = profileAssetPath,
                CharacterId = characterId,
                CharacterName = characterName,
                AddAudioOutput = addAudioOutput,
                DryRun = dryRun
            });

        [AgentTool(
            "Previews or performs complete active-scene Convai Audio conversation setup with safe standalone placeholders and recommended defaults.",
            "Convai.SetupConversationScene")]
        public static object SetupConversationScene(
            [ToolParameter("Optional manager target instance ID.")] long managerInstanceId = 0,
            [ToolParameter("Optional player target instance ID.")] long playerInstanceId = 0,
            [ToolParameter("Optional character target instance ID.")] long characterInstanceId = 0,
            [ToolParameter("Optional existing room profile path.")] string roomProfileAssetPath = "",
            [ToolParameter("Optional existing character profile path.")] string characterProfileAssetPath = "",
            [ToolParameter("Convai dashboard Character ID.")] string characterId = "",
            [ToolParameter("Character display name.")] string characterName = "Convai Character",
            [ToolParameter("Player display name.")] string playerName = "Player",
            [ToolParameter("Optional local player ID.")] string playerId = "",
            [ToolParameter("HandsFree or PushToTalk.")] ConversationInputMode inputMode =
                ConversationInputMode.HandsFree,
            [ToolParameter("Connect automatically when the scene starts.")] bool connectOnStart = true,
            [ToolParameter("Create missing standalone placeholders.")] bool createPlaceholders = true,
            [ToolParameter("True previews; false applies through Unity Undo.")] bool dryRun = true) =>
            ConvaiMcpTools.SetupConversationScene(new ConvaiSetupConversationSceneRequest
            {
                ManagerInstanceId = managerInstanceId,
                PlayerInstanceId = playerInstanceId,
                CharacterInstanceId = characterInstanceId,
                RoomProfileAssetPath = roomProfileAssetPath,
                CharacterProfileAssetPath = characterProfileAssetPath,
                CharacterId = characterId,
                CharacterName = characterName,
                PlayerName = playerName,
                PlayerId = playerId,
                InputMode = inputMode,
                ConnectOnStart = connectOnStart,
                CreatePlaceholders = createPlaceholders,
                DryRun = dryRun
            });

        [AgentTool(
            "Diagnoses active-scene Convai conversation readiness and runtime state with ranked evidence and suggested fixes. Never returns API keys.",
            "Convai.DiagnoseConversation")]
        public static object DiagnoseConversation(
            [ToolParameter("Optional focused character GameObject instance ID.")] long characterInstanceId = 0,
            [ToolParameter("Include inactive scene objects.")] bool includeInactive = true,
            [ToolParameter("Include recent sanitized target, roster, and availability events from an active trace.")]
            bool includeRecentMultiCharacterEvents = false) =>
            ConvaiMcpTools.DiagnoseConversation(new ConvaiDiagnoseConversationRequest
            {
                CharacterInstanceId = characterInstanceId,
                IncludeInactive = includeInactive,
                IncludeRecentMultiCharacterEvents = includeRecentMultiCharacterEvents
            });

        [AgentTool("Safely upserts typed Convai actions and explicit scene targets on a character.", "Convai.ConfigureActions")]
        public static object ConfigureActions(long characterInstanceId,
            ConvaiActionDefinitionInput[] definitions = null,
            ConvaiActionTargetInput[] objects = null,
            ConvaiActionTargetInput[] characters = null,
            string initialAttentionObject = "", bool dryRun = true) =>
            ConvaiMcpTools.ConfigureActions(new ConvaiConfigureActionsRequest
            {
                CharacterInstanceId = characterInstanceId,
                Definitions = definitions ?? System.Array.Empty<ConvaiActionDefinitionInput>(),
                Objects = objects ?? System.Array.Empty<ConvaiActionTargetInput>(),
                Characters = characters ?? System.Array.Empty<ConvaiActionTargetInput>(),
                InitialAttentionObject = initialAttentionObject,
                DryRun = dryRun
            });

        [AgentTool("Diagnoses Convai action definitions, targets, executors, and dispatcher state.", "Convai.DiagnoseActions")]
        public static object DiagnoseActions(long characterInstanceId = 0, bool includeInactive = true) =>
            ConvaiMcpTools.DiagnoseActions(new ConvaiDiagnoseActionsRequest { CharacterInstanceId = characterInstanceId, IncludeInactive = includeInactive });

        [AgentTool("Validates an action in Edit Mode or runs it through the real dispatcher in Play Mode.", "Convai.SimulateAction")]
        public static Task<object> SimulateAction(long characterInstanceId, string actionName, string target = "", Dictionary<string, string> parameters = null, float timeoutSeconds = 10f) =>
            ConvaiMcpTools.SimulateAction(new ConvaiSimulateActionRequest { CharacterInstanceId = characterInstanceId, ActionName = actionName, Target = target, Parameters = parameters ?? new Dictionary<string, string>(), TimeoutSeconds = timeoutSeconds });

        [AgentTool("Configures the canonical transcript facade, relay, or shipped chat UI without changing project settings.", "Convai.ConfigureTranscripts")]
        public static object ConfigureTranscripts(long managerInstanceId = 0, long hostInstanceId = 0,
            ConvaiTranscriptToolMode mode = ConvaiTranscriptToolMode.EventRelay, bool finalOnly = false,
            bool ignoreInterim = true, string characterIdFilter = "", bool dryRun = true) =>
            ConvaiMcpTools.ConfigureTranscripts(new ConvaiConfigureTranscriptsRequest { ManagerInstanceId = managerInstanceId, HostInstanceId = hostInstanceId, Mode = mode, FinalOnly = finalOnly, IgnoreInterim = ignoreInterim, CharacterIdFilter = characterIdFilter, DryRun = dryRun });

        [AgentTool("Diagnoses transcript enablement, facade state, relays, UIs, and sanitized timeline metadata.", "Convai.DiagnoseTranscripts")]
        public static object DiagnoseTranscripts(long managerInstanceId = 0, bool includeText = false) =>
            ConvaiMcpTools.DiagnoseTranscripts(new ConvaiDiagnoseTranscriptsRequest { ManagerInstanceId = managerInstanceId, IncludeText = includeText });

        [AgentTool("Start, read, clear, or stop the bounded editor-only Convai runtime event trace.", "Convai.TraceRuntimeEvents")]
        public static object TraceRuntimeEvents(ConvaiRuntimeTraceOperation operation = ConvaiRuntimeTraceOperation.Read,
            long managerInstanceId = 0, long characterInstanceId = 0, string[] eventFilters = null,
            int limit = 100, bool captureTranscripts = false) =>
            ConvaiMcpTools.TraceRuntimeEvents(new ConvaiRuntimeTraceRequest
            {
                Operation = operation,
                ManagerInstanceId = managerInstanceId,
                CharacterInstanceId = characterInstanceId,
                EventFilters = eventFilters ?? System.Array.Empty<string>(),
                Limit = limit,
                CaptureTranscripts = captureTranscripts
            });
    }
}
