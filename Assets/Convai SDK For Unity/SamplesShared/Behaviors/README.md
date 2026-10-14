# Behaviors (Sample Pack)

This folder contains small, copy‑pasteable scripts that show how to react to Convai events (speech, transcripts, “ready”) in a modular way, plus two minimal action-executor teaching examples.

If you’re integrating the SDK, start with: `Documentation~/SETUP.md`.

## Who should read this

- **Designers / producers** who want to understand what can be driven by AI dialogue (animations, triggers, UI)
- **Engineers** who want a clean way to add “game rules” without forking the SDK, or who want to write their own action executor

If you want the broader event-system map first, read `Documentation~/WORKING-WITH-EVENTS.md`.

## Why behaviors exist

Convai scenes usually need small bits of glue code:

- “When the NPC starts speaking, play an animation”
- “When the NPC says a keyword, trigger a quest/shop UI”
- “When the backend says the character is ready, kick off a scripted moment”

The SDK supports this using an **interceptor chain**:

- Behaviors are ordered by **Priority** (higher runs first)
- For callbacks that return `bool`, return:
  - `true` to **consume/intercept** the event (stop the chain)
  - `false` to **observe** the event (let others run)

These behavior scripts are one integration path, not the only one:

- use `ConvaiManager.Events` for typed room/session reactions
- use `ConvaiManager.Transcripts.Subscribe` for transcript state/history
- use `ConvaiManager.Transcripts.SubscribeCaptions` for live subtitles
- use relay components for no-code UnityEvent wiring
- use behaviors when you want ordered gameplay logic on top of character or player callbacks
- use `ConvaiActionDispatcher` + `IConvaiActionExecutor` when gameplay should follow backend `action-response` batches instead of transcript heuristics

## How to use (Character behaviors)

1. On your NPC GameObject, add:
   - `Convai/Convai Character`
   - `Convai/Character Behavior Dispatcher`
2. Add one or more behavior components (implement `IConvaiCharacterBehavior`).
   - Recommended base class: `ConvaiCharacterBehaviorBase`
3. Set each behavior’s **Priority** field (higher runs earlier).

If you’re unsure this is wired correctly, open the sample scene:

- `Samples/BasicSample/Scenes/Basic Sample.unity` (after importing samples via Package Manager)

## What’s included

### Character behaviors (wired via `CharacterBehaviorDispatcher`)

- `ShopkeeperBehavior`
  - Looks for commerce keywords in the **final** transcript and calls `agent.SendTrigger(...)`.
  - Because it returns `true` when it fires, it **consumes** that transcript event for lower-priority behaviors.
- `QuestGiverBehavior`
  - On `OnCharacterReady`, sends a `quest.step` trigger (example of a scripted “start” moment).

### Action executors — use the SDK suite, not samples

Production-quality action executors now ship in the SDK itself and need no sample import:

- **Core** (`SDK/Runtime/Actions/Executors/`) — the verbs that need no module:
  `ConvaiSetActiveActionExecutor`, `ConvaiWaitActionExecutor`, `ConvaiSequenceActionExecutor`,
  `ConvaiAnimatorStateActionExecutor`, `ConvaiPlaySoundActionExecutor`,
  `ConvaiUnityEventActionExecutor`, `ConvaiCountTargetGroupActionExecutor`,
  `ConvaiMeasureDistanceActionExecutor`, plus the two bases every targeted verb builds on
  (`ConvaiTargetedActionExecutor`, `ConvaiCharacterActionExecutor`).
- **Body Animation** (`SDK/Modules/BodyAnimation/Executors/`): `ConvaiWalkToActionExecutor`,
  `ConvaiFollowPlayerActionExecutor`, `ConvaiReturnToStartActionExecutor`,
  `ConvaiTurnToFaceActionExecutor`, `ConvaiPointAtActionExecutor`,
  `ConvaiPlayGestureActionExecutor`, `ConvaiLeadPlayerActionExecutor`.
- **Gaze** (`SDK/Modules/Gaze/Executors/`): `ConvaiLookAtActionExecutor`,
  `ConvaiWatchPlayerActionExecutor`, `ConvaiScanEnvironmentActionExecutor`.
- **Emotion** (`SDK/Modules/Emotion/Executors/`): `ConvaiSetMoodActionExecutor`,
  `ConvaiReactActionExecutor`.
- **Body Language** (`SDK/Modules/BodyLanguage/Executors/`): `ConvaiHeadResponseActionExecutor`.

Pick any of these from the `ConvaiActionConfigSourceEditor` inspector's **Add Action ▾** catalog —
no code, no sample import required. This folder used to carry sample-tier copies of a few verbs
(`Task.Delay` timing, busy-poll waits, implicit "last child" held-object guessing); they were
replaced by the SDK suite above.

### Writing your own executor

One worked example remains in this folder, heavily commented, to show the authoring shape without
any gameplay complexity in the way:

- `SampleOpenContainerActionExecutor` — a targeted executor built on `ConvaiActionExecutorBase`,
  the SDK's base class for actions that resolve a scene target before acting.

For the shared skeleton behind every shipped targeted executor (peer resolution, missing-target/
missing-peer handling, invocation parameter overrides), read `ConvaiTargetedActionExecutor` in
`SDK/Runtime/Actions/Executors/ConvaiTargetedActionExecutor.cs` and its usage in
`SDK/Runtime/Actions/Interaction/` — those are production-quality references, not samples.

Recommended setup for action-driven scenes:

1. Add `ConvaiActionConfigSource` to same GameObject as `ConvaiCharacter`.
2. Add one or more `ConvaiActionDefinition` entries (or use the inspector's **Add Action ▾**
   catalog): set the backend `ActionName`, `TargetRequirement`, and bind the `Executor` field to the
   component that should run.
3. Fill `objects`, `characters`, and optional initial attention.
4. Click `Validate Action Config` in the Inspector to catch missing executors, duplicate names,
   target-reference issues, and weak metadata.
5. Add `ConvaiActionDispatcher`. The dispatcher resolves action definitions and calls bound
   executors automatically.
6. Add `ConvaiActionDebugProbe` while testing. It records received batches, step reports, failure
   reasons, and batch aborts.
7. Carry verbs — pick up, place, give, drop — are not shipped executors. Write them against
   `ConvaiActionExecutorBase` the way `SampleOpenContainerActionExecutor` in this folder does.

For the complete current action reference, read `Documentation~/ACTIONS.md`. For the step-by-step tutorial, read `Documentation~/ACTIONS-INTEGRATION-TUTORIAL.md`.

### Player-side runtime setup

Scene-level conversation setup now lives on `ConvaiRoomManager`.

- Use `How The Player Talks = Hands Free` for the default smart-turn path.
- Use `How The Player Talks = Push To Talk` to enable the built-in scene keybind flow.
- Use `Room Setup Source = Room Manager Profile Asset` when you want reusable advanced room defaults to drive the scene instead of inline scene defaults.

The supported runtime implementation for push-to-talk is `ConvaiPushToTalkController`, which the manager provisions and drives automatically from the room-manager configuration.

If you are building a custom runtime flow, the advanced low-level control surfaces are still the room connection and audio services:

- `SetSttMuted(bool)`
- `ForceUserStoppedSpeaking()`
- local mic mute controls via `ConvaiManager.Audio.ToggleMicMuted()`

Player-side sample hook in this folder:

- `PlayerSessionStateHandler`
  - Example `ConvaiPlayerInputHandlerBase` implementation that plays an audio cue when the room reaches `Connected`

## Common pitfalls / gotchas

- Behaviors must live on the **same GameObject** as the dispatcher and `ConvaiCharacter`.
- Start by returning `false` (observe) until you know you need to intercept.
- `agent.SendTrigger(...)` only helps if your Convai backend/project is set up to respond to that trigger name.
- Many rules should only run on **final** transcripts (interim results can change).

## Go deeper

- Setup + where to add components: `Documentation~/SETUP.md`
- Full feature → script index: `Documentation~/SOURCE-REFERENCE.md`
- Behavior system types:
  - `SDK/Runtime/Components/CharacterBehaviorDispatcher.cs`
  - `SDK/Runtime/Behaviors/Character/IConvaiCharacterBehavior.cs`
  - `SDK/Runtime/Behaviors/Character/ConvaiCharacterBehaviorBase.cs`
  - `SDK/Runtime/Behaviors/Player/ConvaiPlayerInputHandlerBase.cs`
- Executor teaching example in this folder: `SampleOpenContainerActionExecutor.cs`
- Production action executors: `SDK/Runtime/Actions/Executors/` and each module's own `Executors/` folder (docs: `Documentation~/ACTIONS.md`)
- Sample character behaviors: `ShopkeeperBehavior.cs`, `QuestGiverBehavior.cs`, `ResponseLoggerBehavior.cs`, `ConvaiApproachOnConversation.cs`
- Sample player hook: `PlayerSessionStateHandler.cs`
