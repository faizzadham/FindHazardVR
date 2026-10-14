using Convai.Runtime.Conversation;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Context;
using Convai.Shared.Types;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace Convai.Editor.AI
{
    /// <summary>Configuration source used by Convai authoring tools.</summary>
    public enum ConvaiToolConfigurationMode
    {
        Inline,
        ExistingProfile
    }

    /// <summary>Input for <c>Convai.ConfigureRoom</c>.</summary>
    public sealed class ConvaiConfigureRoomRequest
    {
        [McpDescription("GameObject instance ID that owns or will own ConvaiManager and ConvaiRoomManager.")]
        public long TargetInstanceId { get; set; }

        [McpDescription("Use inline scene settings or assign an existing profile.", Default = ConvaiToolConfigurationMode.Inline)]
        public ConvaiToolConfigurationMode ConfigurationMode { get; set; } = ConvaiToolConfigurationMode.Inline;

        [McpDescription("Existing ConvaiRoomManagerProfile asset path. Required in ExistingProfile mode.")]
        public string ProfileAssetPath { get; set; } = string.Empty;

        [McpDescription("Inline connection type.", Default = ConvaiConnectionType.Audio)]
        public ConvaiConnectionType ConnectionType { get; set; } = ConvaiConnectionType.Audio;

        [McpDescription("Inline conversation input mode.", Default = ConversationInputMode.HandsFree)]
        public ConversationInputMode InputMode { get; set; } = ConversationInputMode.HandsFree;

        [McpDescription("Connect automatically when the scene starts.", Default = true)]
        public bool ConnectOnStart { get; set; } = true;

        [McpDescription("Core-service endpoint.", Default = ConvaiServerEndpoint.Connect)]
        public ConvaiServerEndpoint ServerEndpoint { get; set; } = ConvaiServerEndpoint.Connect;

        [McpDescription("Dynamic vision policy.", Default = ConvaiVisionContextMode.Auto)]
        public ConvaiVisionContextMode VisionMode { get; set; } = ConvaiVisionContextMode.Auto;

        [McpDescription("Unity KeyCode name used by push-to-talk.", Default = "T")]
        public string PushToTalkKey { get; set; } = "T";

        [McpDescription("Preview changes without modifying the scene.", Default = true)]
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Input for <c>Convai.ConfigurePlayer</c>.</summary>
    public sealed class ConvaiConfigurePlayerRequest
    {
        [McpDescription("Target player GameObject instance ID.")]
        public long TargetInstanceId { get; set; }

        [McpDescription("Optional manager GameObject instance ID. Zero auto-resolves one manager in the target scene.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("Player display name.", Default = "Player")]
        public string PlayerName { get; set; } = "Player";

        [McpDescription("Optional local transcript attribution ID. Defaults to player name.")]
        public string PlayerId { get; set; } = string.Empty;

        [McpDescription("Preview changes without modifying the scene.", Default = true)]
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Input for <c>Convai.ConfigureCharacter</c>.</summary>
    public sealed class ConvaiConfigureCharacterRequest
    {
        [McpDescription("Target character GameObject instance ID.")]
        public long TargetInstanceId { get; set; }

        [McpDescription("Optional manager GameObject instance ID. Zero auto-resolves one manager in the target scene.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("Use inline scene settings or assign an existing profile.", Default = ConvaiToolConfigurationMode.Inline)]
        public ConvaiToolConfigurationMode ConfigurationMode { get; set; } = ConvaiToolConfigurationMode.Inline;

        [McpDescription("Existing ConvaiCharacterProfile asset path. Required in ExistingProfile mode.")]
        public string ProfileAssetPath { get; set; } = string.Empty;

        [McpDescription("Convai dashboard Character ID. May be omitted while authoring an incomplete placeholder.")]
        public string CharacterId { get; set; } = string.Empty;

        [McpDescription("Character display name. Defaults to target GameObject name.")]
        public string CharacterName { get; set; } = string.Empty;

        [McpDescription("Ensure AudioSource and ConvaiAudioOutput companions.", Default = true)]
        public bool AddAudioOutput { get; set; } = true;

        [McpDescription("Preview changes without modifying the scene.", Default = true)]
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Input for <c>Convai.SetupConversationScene</c>.</summary>
    public sealed class ConvaiSetupConversationSceneRequest
    {
        [McpDescription("Optional manager target GameObject instance ID.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("Optional player target GameObject instance ID.")]
        public long PlayerInstanceId { get; set; }

        [McpDescription("Optional character target GameObject instance ID.")]
        public long CharacterInstanceId { get; set; }

        [McpDescription("Optional existing ConvaiRoomManagerProfile path.")]
        public string RoomProfileAssetPath { get; set; } = string.Empty;

        [McpDescription("Optional existing ConvaiCharacterProfile path.")]
        public string CharacterProfileAssetPath { get; set; } = string.Empty;

        [McpDescription("Convai dashboard Character ID. May be omitted until all independent setup is complete.")]
        public string CharacterId { get; set; } = string.Empty;

        [McpDescription("Character display name.", Default = "Convai Character")]
        public string CharacterName { get; set; } = "Convai Character";

        [McpDescription("Player display name.", Default = "Player")]
        public string PlayerName { get; set; } = "Player";

        [McpDescription("Optional local transcript attribution ID.")]
        public string PlayerId { get; set; } = string.Empty;

        [McpDescription("Recommended room input mode.", Default = ConversationInputMode.HandsFree)]
        public ConversationInputMode InputMode { get; set; } = ConversationInputMode.HandsFree;

        [McpDescription("Connect automatically when the scene starts.", Default = true)]
        public bool ConnectOnStart { get; set; } = true;

        [McpDescription("Create standalone player and capsule character placeholders when none exist.", Default = true)]
        public bool CreatePlaceholders { get; set; } = true;

        [McpDescription("Preview changes without modifying the scene.", Default = true)]
        public bool DryRun { get; set; } = true;
    }

    /// <summary>
    ///     Input for <c>Convai.ConfigureConversationTargeting</c>. Every field is optional, and omitting one
    ///     leaves that setting exactly as the project authored it.
    /// </summary>
    public sealed class ConvaiConfigureConversationTargetingRequest
    {
        [McpDescription("ConvaiManager GameObject instance ID. Zero uses the only manager in the loaded scenes.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("How the character being addressed is chosen. Omit to leave unchanged.")]
        public ConversationTargetingMode? TargetingMode { get; set; }

        [McpDescription("Range: how far away a character can be and still be addressed, in metres.")]
        public float? MaxDistance { get; set; }

        [McpDescription("Look Angle: how far from the centre of view a character can be, in degrees. LookAt only.")]
        public float? MaxAngle { get; set; }

        [McpDescription("Switch Margin: how much better a challenger must look before the conversation moves, in degrees. LookAt only.")]
        public float? SwitchMargin { get; set; }

        [McpDescription("Switch Delay: how long a challenger must stay the best choice, in seconds.")]
        public float? SwitchDelaySeconds { get; set; }

        [McpDescription("Player Camera: Camera GameObject instance ID treated as the player view.")]
        public long? ViewCameraInstanceId { get; set; }

        [McpDescription("Clear Player Camera so the main camera is used again.")]
        public bool? ClearViewCamera { get; set; }

        [McpDescription("Initial Character: the character the room opens on, whatever the player is looking at.")]
        public long? InitialCharacterInstanceId { get; set; }

        [McpDescription("Clear Initial Character so the room opens on whoever the player is looking at.")]
        public bool? ClearInitialCharacter { get; set; }

        [McpDescription("Characters Joining the Room: the exact set of characters to send, by GameObject instance ID.")]
        public long[] IncludedCharacterInstanceIds { get; set; }

        [McpDescription("Send every active character to the next room, which is the default behaviour.")]
        public bool? IncludeAllCharacters { get; set; }

        [McpDescription("Preview changes without modifying the scene.", Default = true)]
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Input for <c>Convai.DiagnoseConversation</c>.</summary>
    public sealed class ConvaiDiagnoseConversationRequest
    {
        [McpDescription("Optional character GameObject instance ID to focus diagnostics on.")]
        public long CharacterInstanceId { get; set; }

        [McpDescription("Include inactive GameObjects and components.", Default = true)]
        public bool IncludeInactive { get; set; } = true;

        [McpDescription("Include up to 20 recent sanitized target, roster, and availability events from an active trace.", Default = false)]
        public bool IncludeRecentMultiCharacterEvents { get; set; }
    }
}
