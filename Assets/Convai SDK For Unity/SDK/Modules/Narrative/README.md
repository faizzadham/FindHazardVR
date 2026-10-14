# Narrative Module (Narrative Design)

This module adds Unity components for Convai Narrative Design so a character can react to backend sections, triggers, and template-key updates without hard-coded dialogue trees.

If you're integrating the SDK, start with:

- `Documentation~/SETUP.md`

Prerequisites:

- API key set in `Edit > Project Settings > Convai SDK`
- a valid Character ID on your `ConvaiCharacter`

## Main pieces

- `ConvaiNarrativeDesignManager` — syncs backend sections, preserves configured UnityEvents, and publishes section changes
- `ConvaiNarrativeDesignTrigger` — sends named triggers using collision, proximity, manual, or time-based activation
- `ConvaiCharacter.NarrativeDesign` — programmatic section and trigger reads through the shared narrative data service

## Source (paths from package root)

- `SDK/Modules/Narrative/ConvaiNarrativeDesignManager.cs`
- `SDK/Modules/Narrative/ConvaiNarrativeDesignTrigger.cs`
- `SDK/Runtime/Infrastructure/Services/NarrativeDesignDataService.cs`

## Recommended setup

1. Add `ConvaiNarrativeDesignManager` to the character GameObject.
2. Assign the target `ConvaiCharacter`.
3. Sync sections from the backend.
4. Configure section-start and section-end UnityEvents.
5. Add `ConvaiNarrativeDesignTrigger` components where gameplay should fire backend triggers.

## Runtime behavior

- section sync updates names while preserving configured UnityEvents
- deleted backend sections are kept locally as orphaned entries instead of silently destroying authored callbacks
- template keys can be updated at runtime and sent to the backend
- trigger activation supports collision, proximity, manual, and time-based modes

## Use in code

- `ConvaiNarrativeDesignManager` exposes template-key update helpers and section-change events
- `ConvaiNarrativeDesignTrigger` exposes `InvokeTrigger()` and reset helpers
- Use `character.NarrativeDesign.FetchSectionsAsync()` / `FetchTriggersAsync()` for a character, or `NarrativeDesignDataService` with an explicit credential provider for tooling. 4.6.1 removes `NarrativeDesignFetcher` and `FetchResult<T>`; the shared service returns `NarrativeFetchResult<T>` with domain section/trigger records.
- For authoring tools, construct `new NarrativeDesignDataService(apiKeyProvider, useDraft: true)` to read the draft narrative. Character runtime reads retain the default published context. The manager section sync and trigger inspector explicitly use authoring reads.

## See also

- `Documentation~/SOURCE-REFERENCE.md` — feature map for the whole package
