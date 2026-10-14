# Convai Samples

This development tree contains optional sample scenes plus shared sample and default assets. Do not delete `SamplesShared/` wholesale: shipped prefabs and runtime defaults currently reference parts of it. The long-term package boundary should separate true runtime defaults from importable sample-only content.

If you’re integrating Convai, start with:

- `Documentation~/SETUP.md`
- `Documentation~/SOURCE-REFERENCE.md` (feature → script map for the whole package)

## Which sample should I open?

| What you’re trying to do | Start here |
| --- | --- |
| Get a native “hello world” scene working | `Samples/BasicSample/Scenes/Basic Sample.unity` |
| Validate browser permission/connect flow (WebGL build) | `Samples/BasicSample/Scenes/Basic Sample.unity` |
| Try the cinematic LipSync showcase | `Samples/LipSyncSample/Scenes/LipSync Sample.unity` |
| Hook transcripts into gameplay triggers/actions | `Behaviors/README.md`, `Documentation~/ACTIONS.md`, `Documentation~/ACTIONS-INTEGRATION-TUTORIAL.md` |
| Compare the engineer, newcomer, and designer event paths | `Documentation~/WORKING-WITH-EVENTS.md` and `Scripts/Events/` |

## Folder overview

- `SamplesShared/` — shared sample-owned code and future shared sample assets
- `SamplesShared/Profiles/` — every sample-owned profile asset: `Character/`, `Room/` and `Embodiment/`
- `SamplesShared/Profiles/Embodiment/` — the embodiment module profiles (Body Animation, Body Language, Conversation Flow, Emotion, Gaze), their dependencies (clip set, overlay mask, emotion taxonomy) and the shared character preset. Deliberately **not** a `Resources` folder: nothing loads them by resource path, and a `Resources` folder would force-include the whole motion library into every player build
- `SamplesShared/Resources/LipSync/` — the one genuine `Resources` folder here; the lipsync registries are loaded by resource path
- `Samples/BasicSample/` — minimal single-character demo and its Basic-only scene assets
- `Samples/LipSyncSample/` — LipSync/URP showcase scene and its sample-local assets
- `Behaviors/` — shared sample behavior and action-executor implementations; use `Documentation~/ACTIONS.md` for current action runtime semantics
- `Scripts/Events/` — event-surface examples (`TypedEventLoggerSample`, `TranscriptListenerSample`, relay-driven samples)
- `Scripts/Runtime/` — runtime examples (`RuntimeCharacterSpawner`, `LocalServerAuthTokenProviderExample`)
- `Scripts/UI/Settings/` — sample settings-panel bootstrap (`SettingsHandler`)
- `Scripts/UI/Notifications/` — sample notification service (`NotificationService`)
- `Scripts/UI/DynamicContext/` — Sample Debug Hub dynamic-context drawer (`DynamicContextDebugPanel`)
- `Prefabs/UI/Debug/` — drag-in runtime panels for emotion and dynamic-context visualization
- `Scripts/UI/Transcript/` — sample transcript metadata and subtitle UI (`SubtitleTranscriptUI`)
- `Scripts/UI/Utilities/` — sample interaction and transcript-filter helpers

For WebGL validation, build `Samples/BasicSample/Scenes/Basic Sample.unity` to WebGL and follow the browser gesture/HTTPS requirements in `Documentation~/PLATFORMS.md`.

> **URP note:** The LipSync sample depends on the Unity Universal Render Pipeline. The development host project declares URP, but the Convai package manifest does not currently own that dependency. Validate the transformed package artifact and its dependency installation before publishing the sample.
>
> **Package note:** Reusable UI prefabs (settings panel, transcript UI, notifications) ship with the package under `Prefabs/` (from the package root, same folder that contains `SDK/` and `Samples/`).

Event-system reference scripts live under `SamplesShared/Scripts/Events/`:

- engineer path: typed `ConvaiManager.Events`
- newcomer path: transcript UI/listener path
- designer path: relay-driven UnityEvent examples

Shared UI reference scripts live under `SamplesShared/Scripts/UI/`:

- `Settings/SettingsHandler.cs` wires the package `SettingsPanel` prefab and fallback runtime settings services
- `Notifications/NotificationService.cs` shows a sample notification-service implementation
- `DynamicContext/DynamicContextDebugPanel.cs` powers the Sample Debug Hub dynamic-context drawer
- `Prefabs/UI/Debug/Sample Debug Hub.prefab` is the recommended sample debug launcher (emotion, dynamic context, vision drawers)
- `Prefabs/UI/Debug/Emotion State Debug Panel.prefab` shows backend emotion events beside resolved face output
- `Prefabs/UI/Debug/Dynamic Context Debug Panel.prefab` sends and inspects dynamic-context state, events, attention, and backend ACKs
- `Transcript/Subtitle/SubtitleTranscriptUI.cs` demonstrates `ConvaiManager.Transcripts.SubscribeCaptions`

## For developers

Treat these as **reference implementations**. Copy what you need into your project, then customize.

Basic sample uses the shared `Convai.Sample.*` assemblies; LipSync sample is in `Convai.Samples.LipSyncSample`. The sample assemblies:

- references core SDK assemblies
- is intended to remain sample-owned, although a few shipped prefabs and defaults currently reference `SamplesShared`
- must not be removed until those package dependencies are migrated to runtime-owned paths

Sample ownership is intentionally explicit:

- `Samples/BasicSample/` should only own Basic-sample-specific scenes/assets
- `Samples/LipSyncSample/` should only own LipSync-sample-specific scenes/assets
- anything reused across both samples should live in `SamplesShared/` or remain a deliberate core package asset
- Basic and LipSync should not reference each other directly

## Local server authentication demo

`Scripts/Runtime/LocalServerAuthTokenProviderExample.cs` implements `IConvaiAuthTokenProvider`, registers itself before the first connection, and requests a fresh `apiAuthToken` from the localhost demo server without sending a client credential.

The matching zero-dependency Python server, configuration, tests, and step-by-step setup are under `Documentation~/Examples/AuthTokenServer/`. The Python process holds the Convai API key; the Unity component never does.
