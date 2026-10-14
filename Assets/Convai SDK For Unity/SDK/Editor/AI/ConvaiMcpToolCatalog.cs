using System.Collections.Generic;

namespace Convai.Editor.AI
{
    internal static class ConvaiMcpToolCatalog
    {
        internal static readonly IReadOnlyList<string> All = new[]
        {
            "Convai.BootstrapScene",
            "Convai.ConfigureCharacter",
            "Convai.ConfigureActions",
            "Convai.ConfigureBodyAnimation",
            "Convai.ConfigureBodyLanguage",
            "Convai.ConfigureEmbodiment",
            "Convai.ConfigureEmotion",
            "Convai.ConfigureGaze",
            "Convai.ConfigureLipSync",
            "Convai.ConfigureConversationTargeting",
            "Convai.ConfigureNarrative",
            "Convai.ConfigurePlayer",
            "Convai.ConfigureRoom",
            "Convai.ConfigureTranscripts",
            "Convai.DiagnoseActions",
            "Convai.DiagnoseBodyAnimation",
            "Convai.DiagnoseBodyLanguage",
            "Convai.DiagnoseConversation",
            "Convai.DiagnoseEmbodiment",
            "Convai.DiagnoseEmotion",
            "Convai.DiagnoseGaze",
            "Convai.DiagnoseLipSync",
            "Convai.DiagnoseNarrative",
            "Convai.DiagnoseTranscripts",
            "Convai.GetGuidance",
            "Convai.GetProjectStatus",
            "Convai.InspectBodyAnimationContent",
            "Convai.InspectBodyLanguagePersonalities",
            "Convai.InspectEmbodimentPresets",
            "Convai.InspectEmotionPersonalities",
            "Convai.InspectScene",
            "Convai.MarkGazeTarget",
            "Convai.SetupConversationScene",
            "Convai.SetupMultiCharacterRoster",
            "Convai.SetConversationTarget",
            "Convai.SimulateAction",
            "Convai.SimulateConversationTargeting",
            "Convai.TraceRuntimeEvents",
            "Convai.TuneBodyAnimationPersonality",
            "Convai.TuneEmotionPersonality",
            "Convai.UpdateCharacterRoster",
            "Convai.ValidateSetup",
            "Convai.WaitForCharacterReady",
            "Convai.WaitForMultiCharacterState"
        };
    }
}
