# Convai foundation tool reference

Unity Assistant calls the dot-named tools below. External MCP clients receive the equivalent underscore-normalized names shown in parentheses. Search for the selected tool's heading and read that section; use the exposed schema for its complete arguments and accepted values.

All tools return `{ success, message, data }`. Treat `success=false` as a failed operation even when the tool call itself completed.

The tools below match `SDK/Editor/AI/ConvaiMcpToolCatalog.cs` relative to the package root. Contract version: 7.

## `Convai.GetGuidance` (`Convai_GetGuidance`)

- Input: `topic` — `Overview`, `Setup`, `Actions`, `DynamicContext`, `Vision`, `Narrative`, `Embodiment`, `Events`, `Runtime`, `Gaze`, `BodyAnimation`, `BodyLanguage`, `Emotion`, or `MultiCharacter`.
- Use before feature-specific work.
- Output includes summary, prerequisites, workflow, relevant Convai and Unity tools, and documentation paths.

## `Convai.GetProjectStatus` (`Convai_GetProjectStatus`)

- No input.
- Returns SDK, Unity, Assistant and tool-contract versions; credential presence; non-secret server and feature settings; Editor state; package root.
- It never returns the API key. Never ask the user to paste one into chat.

## `Convai.InspectScene` (`Convai_InspectScene`)

- Input: `includeInactive` (default `true`).
- Returns open scenes plus managers, rooms, players, characters, counts, component IDs, GameObject IDs, names, active state, Character IDs, and player names.
- Each character also carries a `modules` list: which Convai capabilities it has, a `readiness` word (`NotInstalled`, `Blocked`, `Inert`, `Working`), a one-line summary and, when something is wrong, a `blocker` sentence. Use it to see a capability exists before choosing a feature tool; use `Convai.DiagnoseEmbodiment` when you need the detail.
- Pass returned instance IDs to later operations. Names are display context, not stable identifiers.

## `Convai.ValidateSetup` (`Convai_ValidateSetup`)

- Input: `scope` — `All` (default), `Project`, or `Scene`.
- Returns `errors`, `warnings`, and `nextSteps`.
- Validation is read-only. Run before and after mutations.

## `Convai.BootstrapScene` (`Convai_BootstrapScene`)

- Input: `dryRun` (Unity Assistant default `true`).
- Edit Mode only. With `dryRun=true`, returns `wouldAddManager` and `wouldAddRoomManager` without mutation.
- With `dryRun=false`, idempotently creates or reuses the manager object and adds required manager/room components through Unity Undo.
- Does not add players or characters, set credentials, save the scene, or enter Play Mode.
- Prefer `Convai.SetupConversationScene` for end-to-end setup. Use bootstrap only for manager/room-only work.

## `Convai.ConfigureRoom` (`Convai_ConfigureRoom`)

- Requires an explicit target GameObject instance ID; previews by default.
- Ensures `ConvaiManager` and `ConvaiRoomManager`, then writes inline Audio/Video, input, startup, endpoint, vision-policy, and PTT settings or assigns an existing room profile.
- Rejects incomplete dynamic-vision Video configuration instead of opening a modal prompt.

## `Convai.ConfigurePlayer` (`Convai_ConfigurePlayer`)

- Requires an explicit target GameObject instance ID; previews by default.
- Adds/configures `ConvaiPlayer` and binds an explicit or unambiguous same-scene manager.
- Never repurposes or modifies `Main Camera`.

## `Convai.ConfigureCharacter` (`Convai_ConfigureCharacter`)

- Requires an explicit target GameObject instance ID; previews by default.
- Adds/configures `ConvaiCharacter`, `AudioSource`, and `ConvaiAudioOutput`, then binds manager ownership and active target.
- Empty Character ID permits independent authoring but returns `complete=false` and `requiredInputs=["characterId"]`.

## `Convai.SetupConversationScene` (`Convai_SetupConversationScene`)

- Input: optional `managerInstanceId`, `playerInstanceId`, `characterInstanceId`, `roomProfileAssetPath`, `characterProfileAssetPath`; `characterId` (the dashboard Character ID, pass it here when known instead of a second `Convai.ConfigureCharacter` call); `characterName`, `playerName`, `playerId`; `inputMode` (`HandsFree` or `PushToTalk`); `connectOnStart`; `createPlaceholders`; `dryRun`.
- Active-scene orchestrator; previews by default.
- Selection order is explicit instance ID, one unambiguous existing component, then a safe placeholder when none exists.
- Creates `[Convai Manager]`, standalone `Convai Player`, and visible Capsule `Convai Character` as needed; configures Audio, HandsFree, automatic connection, audio output, and explicit ownership.
- Applies all unblocked work, reports ambiguity instead of guessing, and never saves or enters Play Mode.

## `Convai.DiagnoseConversation` (`Convai_DiagnoseConversation`)

- Read-only in Edit and Play Mode.
- Returns `readyToRun`, configuration/runtime snapshots, and ranked issues with stable codes, evidence, auto-fixability, and suggested tool arguments.
- `configuration.conversationTargeting` carries the rule that picks between characters and its four numbers, plus `isShippedDefault` so you can tell a tuned scene from an untouched one.
- `runtime.conversationAvailability` and `runtime.playerCanTalk` answer "would a message sent right now reach anybody". `Preparing` means the room is connected but the addressed character has not been announced yet, which is the state that silently swallowed messages before it was reported. `runtime.addressedCharacter` names who the player is talking to in either shape of room.
- `runtime.multiCharacter` carries the roster: each membership's status, failure code, whether it is the active target, and the verdict behind the last targeting decision.
- Optional `includeRecentMultiCharacterEvents` adds up to 20 sanitized target, roster, and availability events when a runtime trace is active. It never adds transcript text.
- Use Unity Console tools when it suggests console evidence; the diagnostic never changes runtime state.

## `Convai.ConfigureConversationTargeting` (`Convai_ConfigureConversationTargeting`)

- Edit Mode only. Uses Undo, never saves the scene, never touches credentials.
- Input: `managerInstanceId` (0 uses the only manager in the loaded scenes), `targetingMode` (`LookAt`, `Proximity`, `Manual`), `maxDistance`, `maxAngle`, `switchMargin`, `switchDelaySeconds`, `viewCameraInstanceId`, `clearViewCamera`, `initialCharacterInstanceId`, `clearInitialCharacter`, `includedCharacterInstanceIds`, `includeAllCharacters`, `dryRun`.
- Every field is optional and omitting one leaves that setting exactly as the project authored it. The request is planned in full before anything is written, so one bad value changes nothing.
- `includedCharacterInstanceIds` is the **exact** set, not an addition: a character left out is excluded from the next connection, and setting it switches the manager to an explicit selection, after which a character added later stays out until it is included too. Read the current set from `Convai.DiagnoseConversation` first, or send `includeAllCharacters` to go back to the default.
- Nothing here is needed to make a multi-character room work. A room already holds every active Convai Character in the loaded scenes and already works out who is being addressed; this tool exists for projects that want to change that.

## `Convai.SetConversationTarget` (`Convai_SetConversationTarget`)

- Previews by default. Execution requires Play Mode and an explicit `dryRun=false`; the tool never enters or exits Play Mode.
- Input: `managerInstanceId`, exact `characterInstanceId`, `dryRun`, and `timeoutSeconds`.
- Goes through `ConvaiManager.TalkTo`, so a request made while the player is speaking is held until that utterance ends instead of splitting one sentence between two characters. This local deferral returns immediately with `queued=true`, `executed=false`, and `canonicalCommandRegistered=false`; another `TalkTo` before speech ends can replace it.
- Once the room registers the command, waits for the manager's Requested then Confirmed/Failed event sequence and returns route-epoch, previous-target, and authoritative-target evidence. A timeout distinguishes a command still waiting to register from one that is canonically pending; a disconnected or replaced room ends immediately with `ROOM_SESSION_CHANGED`.

## `Convai.UpdateCharacterRoster` (`Convai_UpdateCharacterRoster`)

- Previews by default. Execution requires Play Mode and an explicit `dryRun=false`; the tool never enters or exits Play Mode.
- Input: `operation` (`Add` or `Remove`), exact `characterInstanceId`, optional `roomManagerInstanceId`, optional `replacementTargetCharacterInstanceId` for removal, `dryRun`, and `timeoutSeconds`.
- Waits for the authoritative roster acknowledgement and returns added/removed memberships plus the resulting roster and route epochs.
- A room that connected with one character has no multi-character roster and cannot grow. Diagnose first; the tool reports this as `MULTI_CHARACTER_ROOM_REQUIRED` rather than reconnecting or changing scene setup.
- Its maximum timeout is 15 seconds, matching the runtime command. A timeout after canonical registration is reported as pending because that command can still reconcile later. A timeout while waiting to register behind another roster mutation reports `executed=false` and `pending=false` because no request was sent.

## `Convai.WaitForCharacterReady` (`Convai_WaitForCharacterReady`)

- Play Mode only and read-only. Requires an exact character instance ID; an optional room-manager ID disambiguates multiple rooms.
- Waits on the current membership's readiness rather than polling. Cancellation or timeout abandons only the wait and never cancels character startup.
- Returns membership status and failure code, conversation availability, and current route/roster epochs.

## `Convai.SimulateConversationTargeting` (`Convai_SimulateConversationTargeting`)

- Read-only in Edit and Play Mode and never contacts the backend.
- Uses an explicit camera, the manager's authored camera, Main Camera, or the owned player transform in that order, matching the live runtime. It never invents a view transform.
- Resolves an explicit manager from any loaded scene. Reports every character's distance, angle, raw/effective score, eligibility reason, the pure geometric proposal, and the proposal the live runtime would evaluate. When the current target is no longer eligible and nobody meets the geometry limits, the live proposal identifies the first eligible character in authoritative room-membership order and marks `recoveryFallbackApplied=true`.
- It explicitly does not apply switch delay, player-speech, or pending-command gates, so it never labels a proposal as a definite switch.
- Manual mode reports that geometry does not choose a target. In Play Mode only Ready room members are eligible; in Edit Mode the next-room selection is used.

## `Convai.SetupMultiCharacterRoster` (`Convai_SetupMultiCharacterRoster`)

- Edit Mode, preview-first orchestrator for the exact next-room roster and optional initial character.
- Requires at least two exact character instance IDs and validates non-empty, unique Character IDs before delegating one atomic Undo-backed write to `Convai.ConfigureConversationTargeting`.
- Never creates characters, changes credentials, saves the scene, connects a room, or changes Play Mode.

## `Convai.WaitForMultiCharacterState` (`Convai_WaitForMultiCharacterState`)

- Play Mode only and read-only. Waits on room events rather than requiring repeated diagnosis calls.
- Conditions may include authoritative target, exact roster size, minimum route/roster epoch, all-members-ready, and conversation availability. Every supplied condition must match.
- With no conditions, returns an immediate current-state snapshot instead of starting a wait.
- If the room disconnects or is replaced, ends immediately with `ROOM_SESSION_CHANGED` and current-room evidence rather than timing out on a retired session.
- Timeout changes no room state and returns a current sanitized snapshot for reconciliation.

## Feature tools

### `Convai.ConfigureActions` (`Convai_ConfigureActions`)

- Input: `characterInstanceId`; `definitions[]` each with `name`, `description`, `parameters[]` (`name`, `description`, `type` of `Auto`, `Reference`, `String`, `Number` or as listed in the schema, `connector`, `choices[]`), `targetRequirement`, `executorInstanceId`, `timeoutSeconds`, `waitForBotSpeech`, `delayAfterBotSpeechSeconds`; `objects[]` and `characters[]` each with `name`, `description`, `gameObjectInstanceId`; `initialAttentionObject`; `dryRun`.
- Upserts by name. Definitions and targets not mentioned are left alone. A definition with no `executorInstanceId` receives a UnityEvent placeholder and stays incomplete until wired.

### `Convai.DiagnoseActions` (`Convai_DiagnoseActions`)

- Input: `characterInstanceId` (0 uses the only character), `includeInactive`.
- Returns validator evidence, every effective action with its enabled state, assigned action sets, targets, executors, and dispatcher state. Read-only.

### `Convai.SimulateAction` (`Convai_SimulateAction`)

- Input: `characterInstanceId`, `actionName`, optional `target` (a registered target name), `parameters` (name to value map), `timeoutSeconds`.
- Edit Mode validates the definition, target, and executor binding without running anything. Play Mode dispatches through the real dispatcher and waits for the result; it has no `dryRun` flag, so a diagnosis-only request does not authorize that execution.

### `Convai.ConfigureLipSync` (`Convai_ConfigureLipSync`)

- Input: `characterInstanceId`; `meshInstanceIds[]` (skinned meshes with blendshapes; omitted means detect); `profile` (`Auto`, `arkit`, `cc4_extended`, `metahuman`; `Auto` fails when detection is not unique); `mappingAssetPath`; `latencyMode` (`Balanced`, `UltraLowLatency`, `NetworkSafe`, `Custom`); `dryRun`.
- Assigns existing meshes, maps, and shipped profiles only. It never creates an asset.

### `Convai.DiagnoseLipSync` (`Convai_DiagnoseLipSync`)

- Input: `characterInstanceId`, `includeRuntimeMetrics`.
- Returns compatibility and configuration evidence, and in Play Mode the runtime buffer state. Read-only.

### `Convai.ConfigureTranscripts` (`Convai_ConfigureTranscripts`)

- Input: `managerInstanceId`, `hostInstanceId`, `mode` (`EventRelay`, `ChatUI`, `WorldSpaceChatUI`), `finalOnly`, `ignoreInterim`, `characterIdFilter`, `dryRun`.
- Configures the facade, relay, or shipped chat UI. Never changes project settings.

### `Convai.DiagnoseTranscripts` (`Convai_DiagnoseTranscripts`)

- Input: `managerInstanceId`, `includeText` (default `false`).
- Returns enablement, facade state, relays, UIs, and sanitized timeline metadata. With `includeText=true` it also returns the retained transcript text; set it only when the user asked to see what was said.

### `Convai.TraceRuntimeEvents` (`Convai_TraceRuntimeEvents`)

- Input: `operation` (`Start`, `Read`, `Clear`, `Stop`; default `Read`), `managerInstanceId`, `characterInstanceId`, `eventFilters[]`, `limit` (default 100), `captureTranscripts` (default `false`).
- `eventFilters` takes exact `ConvaiManager.Events` member names. Useful ones: `OnConnected`, `OnDisconnected`, `OnSessionError`, `OnPipelineError`, `OnCharacterReady`, `OnCharacterSpeechStateChanged`, `OnCharacterTurnCompleted`, `OnPlayerSpeakingStateChanged`, `OnMicMuteChanged`, `OnVadSttStateChanged`, `OnCharacterActionReceived`, `OnDynamicContextUpdateResultReceived`, `OnConversationTargetChanged`, `OnConversationAvailabilityChanged`, `OnRoomRosterChanged`. An empty list records every event.
- Recording begins at `Start` and holds at most 256 entries in editor memory; nothing spoken before `Start` can be recovered. The trace clears on Play Mode exit and domain reload.
- `captureTranscripts=true` adds `OnCharacterTranscriptReceived`, `OnPlayerTranscriptReceived`, and `OnFinalUserTranscriptionReceived` with their text. Leave it `false` unless the user asked for the words.

### `Convai.ConfigureNarrative` (`Convai_ConfigureNarrative`)

- Input: `characterInstanceId`, `managerHostInstanceId`, `sections[]`, `templateKeys[]`, `triggers[]` (shapes in the tool schema), `dryRun`.
- Upserts Unity-side configuration by section ID, key, and trigger identity. Never contacts the backend.

### `Convai.DiagnoseNarrative` (`Convai_DiagnoseNarrative`)

- Input: `characterInstanceId`, `includeInactive`, `includeContent` (default `false`; narrative text stays hidden unless the user asked for it).
- Returns local configuration and sanitized runtime state. Never contacts the backend.

## Gaze tools

- Inputs: `Convai.DiagnoseGaze(characterInstanceId, includeRuntimeState)`; `Convai.ConfigureGaze(characterInstanceId, eyeContactMode, focusFidelity, playerAnchorInstanceId, clearPlayerAnchorOverride, bodyTurnStyle, profileAssetPath, capabilities[], dryRun)`; `Convai.MarkGazeTarget(gameObjectInstanceIds[], remove, dryRun)`. Accepted values for the string settings are listed in each tool's schema.
- `Convai.DiagnoseGaze` answers "why isn't this character looking at me?". Read-only in Edit and Play Mode. Returns the preflight checks, the resolved head/neck/eye bone names, the eye backend, the facing-direction angle, the personality in use, the optional extras, the scene's gaze targets, and — in Play Mode — what the character is looking at right now.
- Its `watches` block names which link decided the player: `PlayerAnchorOverride`, `PlayerAnchorProvider`, `MainCamera`, `GuessedCamera`, or `Unresolved`. This is the most common cause of wrong-looking gaze; read it before changing anything.
- `Convai.ConfigureGaze` adds the Gaze component and tunes eye-contact mode, focus fidelity, the player anchor, aim point, body-turn style, and the optional extras. Edit Mode only; previews by default.
- Omitted settings are left unchanged. Omitting `capabilities` leaves the extras alone on a character that already has gaze, and gives a new one the recommended pair; passing an array sets the exact set, so `[]` removes them all.
- Neither tool creates or edits an asset. A character with no Gaze Profile is working, not broken — the response names `Assets → Create → Convai → Embodiment → Gaze Profile` rather than authoring one. Everything on the profile is out of scope for these tools.
- `Convai.MarkGazeTarget` adds or removes `ConvaiGazeTarget` on named GameObjects. The player publishes at priority 10, so a target above that outranks the player during conversation and the tool warns when you ask for it.

## Body animation tools

- Inputs: `Convai.DiagnoseBodyAnimation(characterInstanceId, includeRuntimeState)`; `Convai.ConfigureBodyAnimation(characterInstanceId, includeMovement, profileAssetPath, animationSetAssetPath, configAssetPath, speedProfile, dryRun)`; `Convai.InspectBodyAnimationContent(characterInstanceId, animationSetAssetPath)`; `Convai.TuneBodyAnimationPersonality(characterInstanceId, archetype, makeConfigUnique, dryRun)`.
- `Convai.DiagnoseBodyAnimation` answers "why isn't this character doing anything?". Read-only in Edit and Play Mode. Returns the setup checklist, the troubleshooter findings, the rig's motion-scale calibration, the resolved animation content, the personality config and how many characters share it, movement settings, a per-behaviour breakdown, and — in Play Mode — layer weights, foot slide and the transition log.
- This module is **content-gated**, so read two fields before concluding anything is broken. `readiness.state` is `NotInstalled`, `Blocked` (the rig), `NeedsContent` (no animation set) or `Working`. Each behaviour in `features` then carries its own `state`: `Working`; `NeedsContent` — set up, but this character has no clips for it; `ContentIdle` — the clips exist and the setting that plays them is off; `OffByChoice` — a shipped default, not a fault; `FallbackTier` — no switch gates it and a documented fallback is running, which is correct behaviour.
- `Convai.ConfigureBodyAnimation` adds the component, assigns the shipped animation content, optionally adds movement, and tunes Speed Profile, Auto Jog Distance, Min Jog Distance, Acceleration and turn rate. Edit Mode only; previews by default. Omitted settings are left unchanged. A character whose rig cannot host the module is told why and gets no component.
- Walk Speed and Jog Speed are deliberately not settable: the animation content's measured clip speeds override them whenever the controller runs, which is what keeps the feet from sliding. Diagnose reports the effective speeds and their source.
- Movement is genuinely optional. A character with no `ConvaiNavMeshLocomotion` idles, talks, gestures and points perfectly well; never report its absence as a fault.
- `Convai.InspectBodyAnimationContent` lists the Idle/Talk/Listen/Think pools, every action with the names and aliases `PlayAction` accepts, locomotion coverage across all 26 slots, and pointing directions. Call it before writing code that plays a gesture. Works from a character or straight from an Animation Set asset path.
- `Convai.TuneBodyAnimationPersonality` sets **How expressive**, **How calm**, **Keeps busy when alone** and the four archetypes. One config can be shared by many characters, so it copies the config for the named character first and returns `CONFIG_SHARED_CONSENT_REQUIRED` until you pass `makeConfigUnique`. A config only one character uses is written in place.
- No tool creates an Animation Set or Config, assigns or tags a clip, runs the Clip Motion Analyzer, generates an overlay mask, or touches an Animator Controller. Responses name the Body Animation Editor mode or menu path that does. The personality tool's copy duplicates an existing asset and never authors a new one.

## Body language tools

- Inputs: `Convai.DiagnoseBodyLanguage(characterInstanceId, includeRuntimeState)`; `Convai.ConfigureBodyLanguage(characterInstanceId, personalityAssetPath, assignDefaultPersonality, dryRun)`; `Convai.InspectBodyLanguagePersonalities(folderPaths[])`.
- `Convai.DiagnoseBodyLanguage` answers "why isn't this character moving?". Read-only in Edit and Play Mode. Returns the setup checklist, what the rig offers, which personality tunes the character and which behaviours it switches off, and — because this module shares the spine, shoulders and head with Body Animation and Gaze — a `coordination` block naming which other Convai modules are on the character and what each one changes.
- Read `coordination` before concluding anything is broken. Every one of those relationships degrades silently, so "the module is broken" and "another module is holding the pose" look identical from outside.
- `Convai.ConfigureBodyLanguage` adds the component and assigns a Body Language Profile the project already has. Edit Mode only; previews by default. A character whose rig cannot drive the module is told why and gets no component.
- `Convai.InspectBodyLanguagePersonalities` lists the profiles the project has, each one's expressiveness, the behaviours it switches off, and which characters already use it.
- No tool creates or edits a Body Language Profile. A character with no profile is working, not broken — it runs on the SDK defaults, and responses name the menu path that creates one.

## Emotion tools

- Inputs: `Convai.DiagnoseEmotion(characterInstanceId, includeRuntimeState)`; `Convai.ConfigureEmotion(characterInstanceId, personalityAssetPath, emotionDetection, restingMood, restingMoodStrength, dryRun)` where `emotionDetection` is `Responsive`, `Accurate`, or `Off`; `Convai.InspectEmotionPersonalities(folderPaths[])`; `Convai.TuneEmotionPersonality(characterInstanceId, characterType, restingMood, restingMoodStrength, makePersonalityUnique, dryRun)`.
- `Convai.DiagnoseEmotion` answers "why isn't this character's face doing anything?". Read-only in Edit and Play Mode. Returns the setup checklist, the troubleshooter findings, how the character detects feelings, which personality tunes it, what it rests at, a per-behaviour breakdown, and — in Play Mode — the live reading.
- `readiness.state` is `NotInstalled`, `Blocked` (no skinned mesh with blendshapes), `Inert` — set up, unblocked, and still never going to change expression because emotion detection is Off — or `Working`. `Inert` is the state that looks fine and is not.
- **Never report a setting from its stored value; read the `behaviour` block.** Three settings look on and do nothing: a personality that did not come from a character type stores `false` for switches the documentation calls on by default; the four conversation-beat reactions only play when **Never sits perfectly still** is on; and **Picks up other characters' moods** does nothing in a scene holding one character.
- Emotion detection has three settings and the user-facing words matter: **Responsive** updates while the reply is spoken and is the default, **Accurate** reads the whole reply once and works in any language, **Off** means the character never receives anything to feel. Use these UI labels for setup explanations; use internal names such as NRCLex or LLM when relevant to a technical question.
- `restingMood.decidedBy` names which link of the chain won: `ProfileBaseline`, `InitialMoodOverride`, `ForcedNeutralOverride` — an override deliberately forcing a neutral rest, which suppresses the personality rather than falling through to it — or `None`. In Play Mode the live value is reported beside the authored one, because a runtime `SetMood` call and mood drift are indistinguishable from outside the runtime.
- `Convai.ConfigureEmotion` adds the component, assigns an existing personality, and sets emotion detection and this character's own resting mood. Edit Mode only; previews by default. It writes only fields on the character, so it can never restyle another character.
- `Convai.InspectEmotionPersonalities` lists the personalities the project has, each one's character type, what it rests at, whether it ships with the SDK, and which characters use it.
- `Convai.TuneEmotionPersonality` sets character type, resting mood, expression strength and speed, and the feel switches. These live on a personality that may be shared, so it returns `PERSONALITY_SHARED_CONSENT_REQUIRED` until you pass `makePersonalityUnique`, then gives the character its own copy and writes only that. A personality only one character uses and that does not ship with the SDK is written in place.
- No tool creates a personality from nothing, edits a shared or SDK-shipped personality in place, or authors an emotion vocabulary or expression recipe. Responses name the Emotion Editor or the setup route that does.

## Embodiment tools

Embodiment is the layer the five expressive features share: the rig they all depend on, and the preset that hands each one its settings. **Reach for these first** — before you know which feature a question is about.

- Inputs: `Convai.DiagnoseEmbodiment(characterInstanceId, includeCapabilities, includeRuntimeState)`; `Convai.ConfigureEmbodiment(characterInstanceId, setUpRig, capabilities[], presetAssetPath, dryRun)`; `Convai.InspectEmbodimentPresets(folderPaths[])`.
- `Convai.DiagnoseEmbodiment` surveys one character end to end: the rig, every feature it has, which of them will actually do something, its preset, and — in Play Mode — what it is doing right now. Read-only. Each entry in `capabilities` names that feature's own `configureTool` and `diagnoseTool`, so one call tells you which tool to reach for next.
- `readiness` uses one vocabulary for the character and for every feature: `NotInstalled`, `Blocked`, `Inert`, `Working`. `NotInstalled` is usually a deliberate choice, not a fault — a character that should not gesture is correctly configured without Body Animation. `Blocked` means the rig cannot support it. `Inert` means set up, unblocked, and still nothing visible will happen; `blocker` says why in one sentence.
- Read `rig` before concluding a feature is broken. Every feature depends on it, and a face rig Convai could not recognize confidently is the documented usual cause of "expression does nothing" — the fix is the **Character Rig** component's convention or a **Custom Rig Convention Map**, not the feature.
- `Convai.ConfigureEmbodiment` works out the rig now instead of at runtime, adds the features you name, and assigns an existing preset — one collapsed undo step. Edit Mode only; previews by default. It adds components only; every knob belongs to the feature's own Configure tool.
- `Convai.InspectEmbodimentPresets` lists the presets the project has, each one's validity verdict and entries, plus every feature a preset can carry and the menu path that creates its settings asset.
- No tool creates or edits a preset or a settings asset. A feature with no settings asset is working, not unfinished — it runs on Convai's built-in defaults, and responses name the menu path that creates one.
- The same survey reaches `Convai.InspectScene` and `Convai.ValidateSetup`: every character's `modules` list carries the readiness word and blocker for each feature, and for the Embodiment layer itself.

## Error handling

- `PLAY_MODE_ACTIVE`: authoring tools require Edit Mode. Do not stop Play Mode unless requested; diagnosis remains available.
- Invalid or missing IDs: inspect again; never fall back to a name guess.
- Missing credentials: complete independent offline authoring and direct the user to Project Settings. Recheck credential presence after the user reports configuring it; do not poll unchanged status.
- Compilation or domain reload: wait for Editor readiness, then repeat the last read-only inspection before continuing.
