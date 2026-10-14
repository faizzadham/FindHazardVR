using Unity.AI.MCP.Editor.ToolRegistry;

namespace Convai.Editor.AI
{
    /// <summary>Guidance topics available to AI clients.</summary>
    public enum ConvaiGuidanceTopic
    {
        Overview,
        Setup,
        Actions,
        DynamicContext,
        Vision,
        Narrative,
        Embodiment,
        Events,
        Runtime,
        Gaze,
        BodyAnimation,
        BodyLanguage,
        Emotion,
        MultiCharacter
    }

    /// <summary>Validation scopes supported by the foundation MCP tools.</summary>
    public enum ConvaiValidationScope
    {
        All,
        Project,
        Scene
    }

    /// <summary>Live roster mutation supported by <c>Convai.UpdateCharacterRoster</c>.</summary>
    public enum ConvaiCharacterRosterOperation
    {
        Add,
        Remove
    }

    /// <summary>Input for <c>Convai.SetConversationTarget</c>.</summary>
    public sealed class ConvaiSetConversationTargetRequest
    {
        [McpDescription("ConvaiManager GameObject instance ID. Zero uses the only manager in the active scene.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("Character GameObject instance ID to address.")]
        public long CharacterInstanceId { get; set; }

        [McpDescription("Preview without contacting the active room.", Default = true)]
        public bool DryRun { get; set; } = true;

        [McpDescription("Maximum seconds to wait for the authoritative routing response.", Default = 10f)]
        public float TimeoutSeconds { get; set; } = 10f;
    }

    /// <summary>Input for <c>Convai.UpdateCharacterRoster</c>.</summary>
    public sealed class ConvaiUpdateCharacterRosterRequest
    {
        [McpDescription("Add or Remove one character from the active room roster.", Default = ConvaiCharacterRosterOperation.Add)]
        public ConvaiCharacterRosterOperation Operation { get; set; } = ConvaiCharacterRosterOperation.Add;

        [McpDescription("ConvaiRoomManager GameObject instance ID. Zero uses the only room manager in the active scene.")]
        public long RoomManagerInstanceId { get; set; }

        [McpDescription("Character GameObject instance ID to add or remove.")]
        public long CharacterInstanceId { get; set; }

        [McpDescription("Optional character GameObject instance ID to address after removing the current target.")]
        public long ReplacementTargetCharacterInstanceId { get; set; }

        [McpDescription("Preview without contacting the active room.", Default = true)]
        public bool DryRun { get; set; } = true;

        [McpDescription("Maximum seconds to wait for the authoritative roster response.", Default = 15f)]
        public float TimeoutSeconds { get; set; } = 15f;
    }

    /// <summary>Input for <c>Convai.WaitForCharacterReady</c>.</summary>
    public sealed class ConvaiWaitForCharacterReadyRequest
    {
        [McpDescription("ConvaiRoomManager GameObject instance ID. Zero uses the only room manager in the active scene.")]
        public long RoomManagerInstanceId { get; set; }

        [McpDescription("Character GameObject instance ID whose current membership must become Ready.")]
        public long CharacterInstanceId { get; set; }

        [McpDescription("Maximum seconds to wait. Cancelling the wait never cancels character startup.", Default = 30f)]
        public float TimeoutSeconds { get; set; } = 30f;
    }

    /// <summary>Input for <c>Convai.SimulateConversationTargeting</c>.</summary>
    public sealed class ConvaiSimulateConversationTargetingRequest
    {
        [McpDescription("ConvaiManager GameObject instance ID. Zero uses the only manager in the loaded scenes.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("Camera GameObject instance ID. Zero uses the manager's authored camera, Main Camera, then owned player transform.")]
        public long ViewCameraInstanceId { get; set; }

        [McpDescription("Include inactive characters in the candidate report. They remain ineligible.", Default = true)]
        public bool IncludeInactive { get; set; } = true;
    }

    /// <summary>Input for <c>Convai.SetupMultiCharacterRoster</c>.</summary>
    public sealed class ConvaiSetupMultiCharacterRosterRequest
    {
        [McpDescription("ConvaiManager GameObject instance ID. Zero uses the only manager in the loaded scenes.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("Exact set of at least two character GameObject instance IDs for the next room.")]
        public long[] CharacterInstanceIds { get; set; }

        [McpDescription("Optional character GameObject instance ID that speaks first. Zero leaves it unchanged.")]
        public long InitialCharacterInstanceId { get; set; }

        [McpDescription("Preview changes without modifying the scene.", Default = true)]
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Input for <c>Convai.WaitForMultiCharacterState</c>.</summary>
    public sealed class ConvaiWaitForMultiCharacterStateRequest
    {
        [McpDescription("ConvaiManager GameObject instance ID. Zero uses the only manager in the active scene.")]
        public long ManagerInstanceId { get; set; }

        [McpDescription("Optional character GameObject instance ID that must become the authoritative target.")]
        public long ExpectedActiveCharacterInstanceId { get; set; }

        [McpDescription("Expected roster size. Negative values omit this condition.", Default = -1)]
        public int ExpectedRosterSize { get; set; } = -1;

        [McpDescription("Minimum authoritative roster epoch. Negative values omit this condition.", Default = -1)]
        public int MinimumRosterEpoch { get; set; } = -1;

        [McpDescription("Minimum authoritative route epoch. Negative values omit this condition.", Default = -1)]
        public int MinimumRouteEpoch { get; set; } = -1;

        [McpDescription("Wait until every current roster member is Ready.", Default = false)]
        public bool RequireAllCharactersReady { get; set; }

        [McpDescription("Wait until the addressed character can accept player input.", Default = false)]
        public bool RequireConversationAvailable { get; set; }

        [McpDescription("Maximum seconds to wait. This never changes or cancels room state.", Default = 30f)]
        public float TimeoutSeconds { get; set; } = 30f;
    }

    /// <summary>Input for <c>Convai.GetGuidance</c>.</summary>
    public sealed class ConvaiGuidanceRequest
    {
        [McpDescription("Convai workflow topic to load.", Default = ConvaiGuidanceTopic.Overview)]
        public ConvaiGuidanceTopic Topic { get; set; } = ConvaiGuidanceTopic.Overview;
    }

    /// <summary>Input for <c>Convai.InspectScene</c>.</summary>
    public sealed class ConvaiSceneInspectionRequest
    {
        [McpDescription("Include disabled GameObjects and components in the inspection.", Default = true)]
        public bool IncludeInactive { get; set; } = true;
    }

    /// <summary>Input for <c>Convai.ValidateSetup</c>.</summary>
    public sealed class ConvaiValidationRequest
    {
        [McpDescription("Validation scope.", Default = ConvaiValidationScope.All)]
        public ConvaiValidationScope Scope { get; set; } = ConvaiValidationScope.All;
    }

    /// <summary>Input for <c>Convai.BootstrapScene</c>.</summary>
    public sealed class ConvaiBootstrapRequest
    {
        [McpDescription("Preview required changes without modifying the scene.", Default = false)]
        public bool DryRun { get; set; }
    }
}
