---
name: convai-unity-sdk
description: Set up, configure, or diagnose Convai conversation scenes in Unity using SDK-aware tools. Use for connection, audio, character targeting, actions, embodiment, transcripts, context, or narrative issues. Repository-only code or documentation work does not require scene inspection.
required_packages:
  com.unity.ai.assistant: ">=2.13.0-pre.2, <3.0.0"
required_editor_version: ">=6000.0.0"
tools:
  - Convai.GetGuidance
  - Convai.GetProjectStatus
  - Convai.InspectScene
  - Convai.ValidateSetup
  - Convai.BootstrapScene
  - Convai.ConfigureRoom
  - Convai.ConfigurePlayer
  - Convai.ConfigureCharacter
  - Convai.SetupConversationScene
  - Convai.DiagnoseConversation
  - Convai.ConfigureActions
  - Convai.DiagnoseActions
  - Convai.SimulateAction
  - Convai.ConfigureConversationTargeting
  - Convai.SetupMultiCharacterRoster
  - Convai.SimulateConversationTargeting
  - Convai.SetConversationTarget
  - Convai.UpdateCharacterRoster
  - Convai.WaitForCharacterReady
  - Convai.WaitForMultiCharacterState
  - Convai.ConfigureLipSync
  - Convai.DiagnoseLipSync
  - Convai.ConfigureGaze
  - Convai.DiagnoseGaze
  - Convai.MarkGazeTarget
  - Convai.ConfigureTranscripts
  - Convai.DiagnoseTranscripts
  - Convai.TraceRuntimeEvents
  - Convai.ConfigureNarrative
  - Convai.DiagnoseNarrative
  - Convai.ConfigureBodyAnimation
  - Convai.DiagnoseBodyAnimation
  - Convai.InspectBodyAnimationContent
  - Convai.TuneBodyAnimationPersonality
  - Convai.ConfigureBodyLanguage
  - Convai.DiagnoseBodyLanguage
  - Convai.InspectBodyLanguagePersonalities
  - Convai.ConfigureEmotion
  - Convai.DiagnoseEmotion
  - Convai.InspectEmotionPersonalities
  - Convai.TuneEmotionPersonality
  - Convai.DiagnoseEmbodiment
  - Convai.ConfigureEmbodiment
  - Convai.InspectEmbodimentPresets
---

# Convai Unity SDK

## Where these tools come from

Convai tools are hosted by the Unity MCP Server from `com.unity.ai.assistant`. Configure your client in Project Settings > AI > Unity MCP Server (select Codex for ChatGPT desktop / Codex, or Claude Code for Claude). Unity CLI/Pipeline uses a separate bridge.

Loading this skill does not create that connection, and a Pipeline connection does not supply the Assistant-registered `Convai_*` methods. Assistant tool IDs, MCP tool names, and the namespace the client exposes can differ; use the tool schemas the client actually discovered rather than guessed arguments.

For ChatGPT desktop / Codex setup and skill discovery, read [ChatGPT / Codex setup](references/chatgpt-codex.md). The same SDK workflows below apply across clients.

## Operating rules

These workflows apply to scene operations. For repository code, tests, or documentation, use file and shell tools and load only relevant API references; an unavailable Editor does not block independent repository work. User instructions take precedence over skill guidelines within host permissions. If a skill rule blocks requested work, link the file, quote the rule, and explain the remaining dependency while completing independent authorized work.

1. Establish scene state with `Convai.GetProjectStatus`, `Convai.InspectScene`, and `Convai.ValidateSetup`, then use the diagnosis matching the symptom. Reuse current results; inspect again after mutations, domain reload, or evidence that targets changed.
2. Use returned instance IDs. If multiple plausible targets remain, inspect and resolve the ambiguity before any mutation that depends on the choice. Continue work on unambiguous targets.
3. Make routine reversible choices within the request. Ask for missing values only when they cannot be detected or safely defaulted; a missing Character ID does not block independent scene authoring. Do not turn optional features into required setup questions.
4. Use Unity tools for generic GameObjects, scripts, scenes, assets, console reads, screenshots, and Play Mode. Use Convai tools for SDK scene configuration and diagnosis. If a needed tool is unavailable, report the specific blocked operation and continue work that does not need it.
5. For tools that expose `dryRun`, preview with `dryRun=true` and inspect the result. What happens next depends on the request:
   - The request asks to set up, add, enable, or fix something: apply the valid preview with `dryRun=false` on the diagnosed instance, then diagnose again. Do not ask "apply it?"; the request already answered that. Scene authoring uses Undo; live-room operations require the corresponding requested outcome and must be checked for acknowledgement.
   - The request asks what is wrong, why, or whether: do not repair or change gameplay state. Report ranked causes and the proposed fix. Metadata tracing is diagnostic; `Convai.SimulateAction` executes real behavior in Play Mode and requires an execution request, even though it has no `dryRun` option.
6. Never request, print, set, or transmit API keys. No Convai tool accepts one. If a key appears in the conversation, do not repeat any part of it, not even in a refusal; call it "the key you pasted", point to **Edit > Project Settings > Convai SDK**, and advise rotating it. Never expose transcript text unless the user asked to see what was said; then use only `Convai.TraceRuntimeEvents` with `captureTranscripts=true` or `Convai.DiagnoseTranscripts` with `includeText=true`, and say so.
7. Never enter or exit Play Mode, save a scene, or bake a NavMesh unless the user explicitly asked for that action. Never claim visual or runtime quality without evidence from a diagnosis, trace, or Console read.
8. Custom action executors implement `IConvaiActionExecutor`. Create scripts through Unity script tools, attach them, then pass returned component IDs to `Convai.ConfigureActions`.
9. Every action executor must complete on success, failure, timeout, and cancellation. Never leave dispatcher work pending.

## Route

- First setup, or "make this scene talk": [quickstart](references/quickstart.md), then [workflows](references/workflows.md) for the decision order and the completion report.
- Something does not connect, hear, answer, or move in Play Mode: [runtime debugging](references/runtime-debugging.md).
- Custom actions, "make the character do X when asked": [custom actions](references/custom-actions.md).
- Dynamic context, attention, scene objects the character should know about: [dynamic context](references/dynamic-context-and-scene-objects.md).
- Narrative sections and triggers: [narrative](references/narrative.md).
- More than one character, who the player is talking to, "only one character ever answers": [multiple characters](references/multiple-characters.md).
- Any face, gaze, gesture, posture, or lip-sync question, before you know which feature it is: call `Convai.DiagnoseEmbodiment`; its `capabilities` name the next tool.
- "Why isn't it looking at me?": call `Convai.GetGuidance` with topic `Gaze`.
- Exact inputs, outputs, and boundaries of every tool: [tool contract](resources/foundation-tools.md). Read it before calling a tool whose arguments you have not seen; do not infer argument names.

Read only the route needed for this task. In the [tool contract](resources/foundation-tools.md), locate the selected tool's section; use the exposed tool schema for exact arguments rather than loading unrelated tool details. A tool error is evidence to diagnose, not a reason to repeat the same mutation indefinitely.

Lead with outcome. For scene work, report changed instance IDs, readiness, unsaved state, blockers, and unverified checks. Stop when the requested change and relevant verification are complete; distinguish setup readiness from observed runtime behavior.
