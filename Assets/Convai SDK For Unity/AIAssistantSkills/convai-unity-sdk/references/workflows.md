# Convai agent workflows

## Foundation setup

Use [quickstart](quickstart.md) for the scene setup sequence. An end-to-end setup request authorizes reversible authoring within that scope. Apply unblocked work before asking for missing inputs; resolve ambiguous targets before changes that depend on them.

- Load `Convai.GetGuidance` with topic `Setup` when tool-provided setup guidance is needed. Reuse current project, scene, and validation results instead of restarting inspection because another reference was opened.
- Use the known Character ID in `Convai.SetupConversationScene`; let it reuse unambiguous authored targets or create missing player/character placeholders.
- Use Audio, HandsFree, automatic connection, and inline configuration unless the user chose otherwise. If the orchestrator cannot express that choice, use the relevant Configure tools. Do not add optional features or ask product-choice questions just to finish the foundation.
- Check credential presence only. Missing credentials do not block offline authoring; direct the user to Project Settings after completing independent work. Never accept a key in chat.
- Follow remaining `requiredInputs`, then inspect, validate, and diagnose the resulting scene. Keep the scene unsaved and leave Play Mode unchanged unless the user explicitly requested those operations.

## Diagnosis and repair

Choose the feature diagnosis that matches the symptom, using current project status, scene inspection, and relevant validation. Read Unity Console errors and warnings when compilation or runtime behavior is involved. Use [runtime debugging](runtime-debugging.md) for live-session evidence.

Rank likely causes with their evidence, affected object, and next useful check. A diagnosis-only request does not authorize repairs or real action dispatch. A fix request authorizes previewing and applying the supported repair on the diagnosed instance, then checking the result. Reuse current evidence and stop once the requested result is verified; repeat or broaden checks when new evidence warrants it.

Common `Convai.GetGuidance` topics and package-relative references:

- Cannot connect or speak: `Runtime`, then project/scene validation.
- Missing character behavior: `Setup`, then exact character inspection.
- Actions: `Actions`, then `Documentation~/ACTIONS.md`.
- Dynamic context: `DynamicContext`, then `Documentation~/DYNAMIC-CONTEXT.md`.
- Vision: `Vision`, then `Documentation~/DYNAMIC-VISION-CONTEXT.md`.
- Narrative: `Narrative`, then `SDK/Modules/Narrative/README.md`.
- Unclear face, gaze, gesture, posture, or lip-sync symptom: `Convai.DiagnoseEmbodiment`, then the feature tool named in its capabilities.

## Completion report

Lead with the result. Include changed instance IDs, validation/readiness, whether the scene remains unsaved, and any required input or manual work. Distinguish configuration readiness from what was actually observed in Play Mode. Omit empty categories and optional-feature checklists.
