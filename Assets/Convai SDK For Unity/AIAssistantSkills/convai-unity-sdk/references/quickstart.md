# Quickstart

1. Call `Convai.GetProjectStatus`, `Convai.InspectScene`, and `Convai.ValidateSetup` with scope `All`.
2. Preview `Convai.SetupConversationScene` with `dryRun=true`, passing detected instance IDs and the known Character ID as `characterId`. Use Audio, HandsFree, connect-on-start, and placeholders unless the user requested otherwise. If the orchestrator cannot express the requested setup, use the relevant Configure tools without overwriting that choice.
3. For a setup request, apply the safe, unblocked work with `dryRun=false`. One call sets up the manager, room, player, character, audio output, ownership, and the Character ID; do not follow it with `Convai.ConfigureCharacter` to write the ID again. A missing Character ID does not block independent authoring. Resolve ambiguous targets before dependent changes; never guess an instance ID to force completion.
4. Follow remaining `requiredInputs` after independent setup. Ask for a Character ID only when neither the request nor the existing character provides one.
5. Reinspect, validate, and call `Convai.DiagnoseConversation`. State any remaining input or readiness gap.
6. Enter Play Mode only when requested. Start `Convai.TraceRuntimeEvents` with `operation=Start` and narrow `eventFilters`, then diagnose the connection.

Never modify Main Camera to create a player. Never ask whether to save, whether to create placeholders, or whether to use the recommended defaults.
