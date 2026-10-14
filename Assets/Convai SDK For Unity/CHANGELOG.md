# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/). Historical versions preserve their published numbers; consult each release's Breaking Changes and Migration Notes instead of assuming strict Semantic Versioning compatibility.

## [4.6.1] - 2026-09-28

This release adds the Scene Auto Tagger and push-to-talk finalization, and includes inspector updates,
audio and echo cancellation improvements, REST routing changes, a LiveKit upgrade, and breaking API cleanup.
Review Breaking Changes before upgrading from 4.6.0.

### Added

- **Word-level speaking progress on character transcripts.** `CharacterTranscriptReceived` now
  carries the `segment_id`, `spoken_status` and `spoken_progress` fields of `bot-output` as
  `OutputSegmentId` and `SpokenProgress` (`Status`, `SpokenText`, `RemainingText`), so captions can
  follow the voice word by word when its provider reports word timings. Both are `null` when the
  server omits them; text, lifecycle, identity, `IsSpoken` and the room transcript are unchanged.
  The existing constructor keeps its signature; a new overload takes the two values.

- Profiler markers for facial composition, lip-sync playback and bridge ticks, and transcript timeline construction.

- Feature-owned allocation regression gates cover transcript updates, EventHub delivery, packed lip-sync ingestion, and facial composition with measured ceilings and profiler-verified positive controls.

- **Project-wide API Mode.** Choose **Upgraded** in **Project Settings > Convai SDK > Credentials**
  to use production API2 for Character details, Narrative sections, and Narrative triggers through
  the existing `ConvaiRestClient` facade. **Legacy remains the package default** and is the explicit
  rollback option for these three reads; failures never silently retry against a different host.
- **Character API workspace and host configuration.** An optional Workspace ID scopes the migrated
  reads. Production, Beta, and Custom Character endpoints are independent of the legacy REST
  override and the Core and Platform service endpoints.
- **Explicit Narrative authoring reads.** Manager overloads and `NarrativeDesignDataService` accept
  `useDraft: true`; Narrative inspectors opt in even during Play Mode. Existing manager calls retain
  runtime read semantics. Explicit revision references are preserved, and bare-ID authoring reads
  resolve an accessible draft without changing the serialized Character ID.
- **Transport controls and disconnect reasons.** Native connections support an optional single-peer
  connection setting and explicit video degradation preferences. Native and WebGL adapters report
  duplicate identity, server shutdown, participant removal, room deletion, and join failure distinctly.
- **ChatGPT / Codex AI coding setup.** AI Coding can install a ChatGPT / Codex project skill mirror
  at `.agents/skills/convai-unity-sdk/` and reports ChatGPT / Codex app and Unity plugin (Codex)
  status read-only. Codex project instructions resolve the skill through that mirror when it is
  installed. The packaged skill manifest is now 1.1.0.

#### Scene Auto Tagger

- Add **Convai → Scene Auto Tagger** to discover scene objects, capture several views and generate names and descriptions with Convai Vision. Choose a character, draft scene guidance and explore the current scene or selected objects in one run.
- Review images and suggestions before Apply. Edit text, add correction notes, retry saved images, adjust camera views, recapture objects and manage verified duplicate groups. Progress, cancellation and recovery messages explain incomplete work.
- Apply approved metadata with Undo support and protection for manually authored content. Connect objects to actions, gaze, spatial context and optional NavMesh standing positions; logical groups retain their physical members.
- Actions on a logical group act on its nearest active physical member: Turn To Face, Lead Player, Measure Distance and Play Sound now match Walk To, Look At and Point At. Custom behaviors can call `ConvaiTargetedActionExecutor.ResolvePhysicalTarget`. Show Or Hide Object shows or hides every member while the group stays intact, and result messages use the group's World Object name instead of its generated root's name.
- Support isolated Built-in, URP 17.x and HDRP 17.x captures with lighting recovery. Bundle pinned Core v0.10.0 editor libraries for Windows x64 and Apple Silicon/Intel macOS; the current unsigned native release is for internal qualification.
- Increase metadata authoring limits from 50 to 64 UTF-16 units for names and from 200 to 320 for descriptions. Existing runtime strings remain unchanged.
- Include an authoring sample and [Scene Auto Tagger documentation](Documentation~/SCENE-AUTO-TAGGER.md) covering setup, review, retakes and platform qualification limits.

#### Push-to-talk finalization

- Add `ForceUserStoppedSpeakingAsync(releaseId)` and explicit committed, empty, and failed results. Pending releases survive controller disable and retry with the same ID.
- Native PTT flushes partial capture audio and awaits media-sink callbacks before sending STT mute and release. Bot completion is correlated by response ID; logical completion does not control audio playback or lip sync.
- Expose PTT state, commit events, an explicit reconnect retry, and a Legacy release option. WebGL retains legacy release because its current media sender has no drain acknowledgement.

### Changed

- AI Coding Setup and the AI Coding section show matching **Claude Code** and **ChatGPT / Codex**
  cards with **Project skill** and **Project instructions** rows; Claude MCP configuration and
  **Refresh** moved to Setup Health. The Claude card's **Configure** button was removed; use
  **Open Unity MCP Server Settings**.

- Transcript updates reuse the turn snapshots and index arrays they did not change: an interim
  update rebuilds only the turns it touched, so its cost no longer grows with retained history.
  `TranscriptTimelineSnapshot`/`TranscriptTimeline` now republish the settled `CommittedTurns` array
  and its turn instances by reference, and `TurnsById` and `LatestTurnByParticipant` are read-only
  views over the settled index and the open turns rather than fresh dictionary copies. Treat every
  published turn as immutable and shared; a new turn instance is no longer a signal that a turn
  changed, so compare `Revision` or the delivered change kind instead.

- Packaged agent instructions (`AIAssistantSkills/convai-unity-sdk`) and the installed agent-instruction
  snippet clarify that a setup or fix request authorizes its reversible changes,
  diagnosis-only requests do not authorize repairs, only task-relevant references are loaded, and transcript
  text stays hidden unless explicitly requested.
- Streamed character transcript, character TTS text chunk, and player transcription diagnostics
  (receipt, typed-echo suppression, publication, processed final) now log only metadata at Debug.
  Disabled diagnostics skip message formatting and enum stringification.

- Updated the vendored LiveKit Unity SDK to upstream **2.0.0**, using Convai fork tag
  `convai-v2.0.0.7-ptt.4` and native FFI **0.12.69**. Managed/native versions and archive hashes are pinned.
- Regenerated the Character contracts from the pinned production OpenAPI. Typed compatibility
  adapters normalize nullable Character/Narrative fields and honor backend schema defaults.
- Consolidated facial-mesh discovery, referential word matching, input-system lookup, and sample
  debug-panel UI construction without changing their feature-specific behavior.
- Keyboard, mouse and touch polling now resolves the Input System once per device instead of on every
  frame, so idle chat, settings-shortcut, push-to-talk and idle-reset polling no longer allocates.
  Keypad Enter, Num Lock, Print Screen, Menu and the Command/Windows keys are now recognized on the
  Input System backend, and a keyboard that is unplugged or replaced is picked up on the next frame.

#### Character and Room Manager inspectors

- **Copy the connect response.** The Convai Character's Session section and the Room Manager's Runtime section can
  copy the server's last room connect response as JSON: every field it sent (room, room URL, session, character
  session and trace IDs, the character list and any fields the SDK does not model yet), plus SDK version, Unity
  version, platform and the time it arrived. The room token is replaced with its length. The response stays
  available after Play mode stops, until the next connect.
- **Identity actions sit beside their fields.** Copy is next to Character ID and Fetch next to Character Name.
  Fetch says why it is disabled (profile-owned name, invalid ID, no API key), shows progress, reports a failure under
  the field instead of only in the Console, and confirms the result. The dashboard button reads *Open In Dashboard*,
  or *Find ID In Dashboard* while the ID is missing. Session ID Copy and Clear sit on the ID's row and are disabled
  while it is empty.
- **Advanced Room Control is now four cards:** Listening (end of turn, voice detection, speech recognition at start,
  microphone), Interruptions, Vision and Reconnect. Each starts collapsed and shows its state (*Defaults*,
  *Custom*, *On*, *Off*) in the header. The Room Manager Profile asset uses the same cards.
- Turn detection, voice detection and similar choices are shown as one segmented **Default / Custom** control.
  The default side explains in one sentence what it does, and value fields appear only for Custom instead of
  staying on screen disabled.
- How the player talks and the push-to-talk rules appear only in Conversation, where the profile inspector now
  shows them too. Dependent settings (fade duration, wait timeout, everything under Vision) appear only when the
  setting they depend on is on. The Vision card says whether frames are actually sent given the Connection Type.
  With on-device detection on and echo cancellation off, the Interruptions card warns and offers a one-click fix.
- Sampling windows are drawn as rows ("3 frames, 200 ms apart") with add and remove buttons, and a warning when
  they ask for more frames than Frames Per Reply.
- Labels and tooltips were rewritten in plain language with units and defaults, for example *Silence Before Reply
  (s)*, *Frames Per Reply* and *Rejoin Window (s)*. Serialized field names are unchanged, so no data migrates.

#### One look across every Convai inspector and window

- Action buttons use the Convai accent everywhere: a green outline over a light green wash, brighter on hover,
  grey when disabled. The accent-filled button is kept for each view's one main action. About 50 Unity grey
  buttons and mini buttons are replaced, mostly in Lip Sync, Embodiment, Body Animation, Gaze, Narrative Design,
  Push To Talk and the Actions inspectors.
- Buttons beside a field (Copy, Fetch, Refresh, Edit, Browse, Apply) share one width, so the fields next to them
  end on the same edge.
- Two to four option choices are segmented toggles, sized to their labels: setup source (Inline / Profile Asset),
  Connection Type, Emotion detection, Gaze eye contact and attention, NavMesh speed profile, Narrative activation
  mode, Action Target kind and visibility, dispatcher policies, and more.
- Unity help boxes and hand-drawn status panels are Convai info, warning and error boxes, with their fix button
  inside the box. First-run checklists in the Gaze, Emotion, Body Language and Body Animation inspectors use one
  check-row style instead of four.
- Inspectors that drew fixed, non-collapsing cards (event relays, Narrative Design, Actions inspectors, Push To
  Talk) use the standard collapsible sections, with Play mode sections in the live colour.
- The Gaze and Embodiment windows use the same tab bar as the other Convai windows.
- `[Header]` attributes that printed a second, unstyled title inside Convai sections are removed from the event
  relays, Narrative Design, Convai Manager, Action Dispatcher, Action Feedback Relay, Emotion Controller, Vision
  sources and publisher, and Body Animation entries. The Vision inspectors draw those fields with property fields
  again, so prefab overrides and Revert work on them.
- Project Settings › Convai SDK buttons and help boxes follow the same colours.
- Labels no longer run into their controls. Inside every Convai section the label column is sized to the view
  (45%, between 160 and 300 px) instead of Unity's 150 px, so labels such as *Mood Follows Conversation* read in
  full and checkboxes no longer sit on the text. Labels that still would not fit a normal Inspector are shortened,
  with the detail kept in the tooltip (mostly Gaze, Emotion, Body Language and Body Animation tuning fields).
- When a field's inline buttons would leave it too narrow, the buttons move to the row beneath it. Message-box
  titles wrap instead of clipping, their fix buttons are sized to their labels, wrapped captions reserve their
  measured height, and the Lip Sync mapping table shares its width between source and target columns.
- `ConvaiEmbodimentPresetLibrary` had two inspectors registered; the older one is removed.

#### Character voice volume

- 3D character speech now plays at full volume within 5 m (was 1 m) and attenuates beyond that. The Basic sample's
  camera sits about 4.8 m from the character, so its speech played about 14 dB quieter than the other samples. The
  `ConvaiAudioOutput` default, the Convai Character setup prefab and the Basic sample use 5 m; existing scenes keep
  their saved value.

#### Transcript snapshot consistency

- Synchronize transcript timeline/query mapping with engine change batches and facade disposal. Query methods reuse current timeline models; reads after disposal remain available without retaining facade history caches.
- Document immutable snapshot collection ownership and cover committed-turn correction identity and deterministic cache release.

#### Echo cancellation

- With acoustic echo cancellation enabled, client-triggered interruption is authorized only after the canceller has processed 1.5 s of audible rendered audio since capture last started; a processed frame of silence no longer counts. The warm-up restarts on every capture start, including resume from background and the Android silent-start recovery.
- The audio processing module on the AEC path now runs without automatic gain control. AGC raised the gain while the player was silent and amplified residual echo into the local speech detector. Transcription level on quiet speakers may change; device speech-to-text qualification is pending.
- With AEC enabled and the `LK_DEBUG` scripting define set, `MicrophoneSource` logs the lag between the microphone write head and the AudioSource read head once per second, with a wrap count.

#### Logging API migration

- Logging now uses published configuration snapshots, source-bound default categories, immutable context on every overload, and UTC event timestamps. Source prefixes are rendered by text sinks instead of stored in message text.
- Custom loggers must implement `Log(in LogEntry)`; custom sinks must implement `Write(in LogEntry)` and level/category filtering. Initialization is idempotent, sink removal owns retirement/disposal, and failing sinks are disabled after three consecutive failures.
- Debug/Trace build restrictions now apply consistently to injected, generic, and structured calls. Trace retains its severity; Off and invalid severities do not emit entries. Guard interface argument construction before logging.
- Console stack options are a severity mask (new default: Error); existing bool settings migrate to Warning|Error or none. Exceptions emit once with their original metadata. Frame timestamp hooks and the unrestricted builder accessor were removed.
- See the [logging migration guide](Documentation~/migration/logging.md) for API examples, ownership, category, context, exception, and formatting changes. Performance evidence and remaining platform gates are tracked separately in the repository logging redesign report.

### Fixed

- On WebGL, a denied microphone no longer halts the player. When the browser refused microphone access (the player
  clicked Block, or the build ran in an iframe that does not grant the microphone, such as play.unity.com), the SDK
  read the failure through a broken bridge accessor, and a JavaScript `TypeError` stopped the Unity runtime with
  "Halting program". The session now continues without voice input, and text input keeps working. The failure is
  reported through the existing session error codes: `audio.mic_permission_denied` for a refused permission,
  `audio.mic_unavailable` for a missing device, and `audio.mic_publish_failed` for any other capture failure, with the
  browser's reason in the message. When the page's permissions policy blocks the microphone, the message says that
  the host iframe needs `allow="microphone"`.
- On WebGL, a microphone publish now reports its outcome. A WebGL player never runs the .NET thread pool, and the
  SDK's audio path resumed its `await`s there with `ConfigureAwait(false)`, so the result of the browser's
  microphone request never reached the SDK, whether access was allowed or refused, and the SDK's microphone locks
  stayed held, which blocked any later microphone start or stop. Player code now resumes on Unity's synchronization
  context on WebGL and keeps `ConfigureAwait(false)` on every other platform.
- On WebGL, a rejected room connect now fails at once with LiveKit's reason. The rejection reason was read before it
  was captured, the read threw inside the promise callback, and the operation never reported completion, so the
  attempt waited out the 30-second connect timeout and was reported as a timeout.
- On WebGL, muting the microphone now stops the audio. `SetMicMuted(true)`, push-to-talk release and the
  multi-character routing gate set a mute flag that never reached the browser, so the server kept receiving and
  transcribing the player. The published browser track now follows the mute state.
- On WebGL, delays, timeouts and contended locks now complete. A WebGL player has no timer or thread-pool threads:
  measured in a Unity 6000.6 player, `Task.Delay`, a timeout `CancellationTokenSource`, `CancelAfter`,
  `System.Threading.Timer` and a contended `SemaphoreSlim.WaitAsync` never completed. Overlapping microphone
  start and stop calls left every call after the first waiting forever, with the microphone off for the rest of the
  session. Push-to-talk, conversation-input, action, vision and multi-character timeouts use the same timers. A
  `Task.WhenAny` race never completed either when one side came from a source created with
  `RunContinuationsAsynchronously`, so the character-ready wait, vision readiness, push-to-talk commits and
  cancellable waits could still hang or end as timeouts. These waits and races now run on the player loop in WebGL
  players and are unchanged on other platforms. While the browser tab is hidden the player loop stops, so a wait that elapsed meanwhile completes
  when the tab is shown again. `IUnityScheduler.ScheduleOnBackground` and `EventDeliveryPolicy.Background` deliver on
  the main thread in WebGL players, which have no background threads.
- On WebGL, REST and character API requests made at runtime, such as loading Narrative Design sections and
  triggers, now return to their caller. Their `await`s resumed on the thread pool, which a WebGL player never runs,
  after the request finished on the main thread.
- On WebGL, the character's audio is connected to the SDK on every connect. When the character's audio track was
  subscribed before the connect finished, the SDK never saw it ("Timed out waiting for wrapped remote audio track"),
  and raised no character audio playback events, which lip sync and body language use to follow the character's
  speech. The audio itself still played.
- On WebGL, a click in the scene now starts character audio when the browser blocked autoplay. The room manager
  could not reach the WebGL transport, so it reported audio playback as always active and `EnableAudioPlayback`,
  `EnableAudioAndStartListening` and the first-click flow did nothing. Measured in Chrome with its default autoplay
  policy: a player who refused the microphone never heard the character, even after clicking. The room manager now
  reads the browser's playback state, a click is checked against it each time rather than once at connect, and a
  refused `startAudio` is reported so the next click tries again. The microphone starts as it did before, whether
  or not playback is blocked.
- On WebGL, a failed room connection no longer throws while it cleans up. When WebRTC could not connect (for
  example "ICE failed" on a restricted network), disposing the LiveKit room read its handle after closing it: the
  console showed `ObjectDisposedException: Safe handle has been closed` from the finalizer. In earlier versions
  the room's event listeners could also stay registered, and LiveKit's later events then failed with errors such as
  "indirect call to null" and `can't access property "name"`. The room and its event emitter now release everything while the handle is open.
  The connection failure itself is still reported as before.
- On WebGL, lip sync starts when the character's voice starts. When a response's voice reached the browser more
  than 500 ms after its start message, lip sync gave up waiting and started from a point in the silence before the
  voice, so the mouth ran 0.7 to 1.0 s ahead of it for the whole response. When the voice arrived before the start
  message, lip sync started from the message, and the mouth ran behind the voice by the gap (608 ms measured). Lip sync
  now waits up to 2 s for the voice while the browser hears silence, and starts from the moment the voice began in
  both cases, the same onset rule the native audio clock uses. Measured in headless Chrome with the LipSync sample:
  before the fix, 2 of 15 responses started early this way; after it, all 18 responses started on the voice, and the
  lip sync timeline stayed within 30 ms of the time since the voice began.
- Leaving a session or switching characters no longer stalls for up to five seconds. The client waited for a room
  disconnect event that LiveKit does not raise when the client ends the session itself.
- On iOS, character audio stays on the loudspeaker while the microphone captures with acoustic echo cancellation.
  Unity's `Microphone.Start` sets the audio session category again once capture begins, which cleared the SDK's
  speaker override and moved playback to the receiver at phone-call volume. The voice-processing session now observes
  route changes and re-applies the override when playback lands on the receiver. Headphones, Bluetooth, USB and
  AirPlay outputs are left alone while they are in the route, and a speaker/receiver hop no longer counts as a route
  change that restarts capture.
- `OnSessionStateChanged` listeners and event hub `SessionStateChanged` subscribers that answer `Connected` by calling
  `SetConversationInputModeAsync` are no longer refused with `SessionInvalidState`. `ConvaiRoomManager` marked the
  session's turn-taking state as live only after raising `OnSessionStateChanged` from its own event hub subscription,
  so an input mode restored from those notifications was rejected and the session stayed in its configured mode. The
  state is now live before the transition is published. `ConversationInputModeChanged` for the session's mode is
  raised once the transition completes, after `Connected`. An error or disconnect afterwards still reverts to the
  configured defaults.
- Accepting room details no longer serializes them to JSON for the `Room details received` Debug line when Transport
  Debug output is filtered, as it is by default in non-development players. The dump was built on the main thread on
  every connect; on the first connect it also paid Newtonsoft's first-use cost for writing those types.
- A character agent that reports no version selection no longer resumes the previous character's session after its
  Character ID changes between sessions. It sent that character's session id after any disconnect, and after a
  network drop within the rejoin TTL it also rejoined that character's room, where the previous character's agent
  may still be answering. The connect now starts a new room instead. Rejoining the same character is unchanged. A
  room remembered from a `JoinExistingMultiCharacterRoom` connect made without an active character records no
  Character ID and is not checked.
- Client-side Silero detection now starts in IL2CPP player builds (Android, iOS, Quest). Nothing referenced its
  optional module directly, so managed code stripping removed the detector registration and only the Editor ran it.
- A character reply whose final text arrived after the character started speaking was split into two chat bubbles,
  because the reply's turn id switched from the client-generated id to the server's response id mid-reply.
- Starting an echo-cancelled microphone on Android no longer freezes the main thread. Entering communication
  mode made the next AudioManager call wait for the audio service (~650 ms on a Redmi Note 12, Android 15), and
  opening the microphone during the switch blocked as well. AudioManager calls now run in order on one
  JVM-attached worker thread, the microphone opens once the mode has settled, and the route baseline is read
  after the switch. A capture restarted within 1.5 s (a microphone recovery, the next push-to-talk press) keeps
  communication mode instead of leaving and re-entering it, and overlapping captures restore the previous mode
  only when the last one stops.

- LiveKit audio teardown no longer holds the audio callback lock while stopping Unity playback or releasing native handles. Late callbacks cannot recreate disposed state; FFI unsubscription and stop-listener failures cannot skip source, probe, or resource cleanup. The fork revision `convai-v2.0.0.7-ptt.4` retains upstream `v2.0.0` and native FFI `0.12.69`.

- Missing-blendshape warnings discovered while Character warnings are disabled are reported on the next lookup after logging is re-enabled; inspecting Gaze setup no longer emits or adds missing-shape reports.

- A client disconnect now ends the session: the next connect starts a fresh room instead of
  rejoining the old one within the rejoin TTL. Rejoining after a character switch bound the new
  character to both its own and the previous agent's participant, so every turn was handled twice.
  This covers every client path (`ConvaiManager.DisconnectAsync`, `ConvaiRoomManager.DisconnectAsync`
  and `DisconnectFromRoom`, `Room.DisconnectAsync`, `ConnectionCoordinator.DisconnectAsync`,
  `ConvaiCharacter.StopConversationAsync`, ambiguous-routing recovery, and `RetryPttAsync`, which now
  retries in a fresh room). Unexpected drops still keep the room for a rejoin; the per-character
  session id is kept either way.

- Missing blendshape diagnostics are aggregated across Emotion/Gaze binding and rebinds, with one report per changed missing set. Fully mapped bindings stay quiet, and numeric gaps in the semantic enum no longer produce false missing-shape reports.

- Protocol diagnostics check the current Debug filter before formatting and report metadata without transcript payloads, raw JSON, or content-bearing exception messages. Existing delegate-only gateway callers remain supported.
- Video track publication failures no longer also write to the global logger when an injected logger handles the null-track diagnostic. Vision publication readiness and background failures also support owned diagnostic routing, retaining the global logger by default. Gaze traces use the same owned routing for every severity; diagnostic fixtures no longer depend on the global console.

- Native operations await LiveKit completion instead of polling. Publication consumers resume only
  after the native callback has finished updating the track SID and publication state.
- Native dependency installation validates archive contents before replacing installed files and
  repairs missing Android libraries, with one owner for `libwebrtc.jar`.
- REST failures retain the failing request URI, response body, request ID, and retry metadata through
  the compatibility facade. Request deadlines remain distinct from caller cancellation, including
  UnityWebRequest's platform-specific timeout messages.
- Background Vision stop/restart and native disconnect failures are observed and reported without
  dumping backend details. Webcam startup cancellation avoids reading a destroyed capture texture.
- Gaze head/torso limits remain enforced after motion filtering.

#### Echo cancellation and client barge-in over speakers

- With **Echo Cancellation** and **On-Device Detection: Silero** both on over a device speaker, the character's own
  residual echo at each utterance onset was scored as speech within 64 ms and interrupted the character on its first
  words. Local detection now starts 0.4 s after the character becomes audible, confirmation needs about 192 ms of
  speech instead of 64 ms, and on a project with echo cancellation enabled a candidate no longer ducks playback until
  the canceller's warm-up has passed.
- The published microphone sends silence while the character is audible and the echo canceller has not finished
  warming up (the first ~1.5 s of audible playback after capture starts, plus 0.3 s of room tail), so echo of the
  first reply no longer reaches the server as player speech. Frames keep their timing, and local detection still
  receives the real processed audio. Vendors LiveKit `convai-v2.0.0.9` (`convai-v2.0.0.8` plus this change; upstream
  `v2.0.0`, FFI `0.12.69` unchanged).
- While client detection runs, every `[ClientLatency]` turn summary includes the local detector's candidate count
  (including `candidates=0`), AEC state at the last marker and at turn end, and the time from audible playback to the
  first candidate and first confirmation. Client barge-in markers log `aec=`.
- Rename the misleading latency label `audioHoldForLipSync` to `ttsNotificationToPlaybackNotification` in the log
  line and the latency file (`ttsNotificationToPlaybackNotification_ms`). This is a measured notification interval,
  not proof of a local lip-sync hold.
- Basic and Multi-Character sample scenes enable client detection alongside echo cancellation, as the LipSync sample
  already did.

#### Capture lifecycle fixes

- Retain ownership of asynchronous camera startup, cancel it on stop/source replacement, and preserve borrowed previews. A publish-wait timeout leaves a still-starting camera running, so its first frame publishes when it arrives.
- Re-publish replacement textures after retiring the previous attempt/track through its original room. Explicit stop and source failure suppress automatic publication.
- Vendor LiveKit `convai-v2.0.0.8`, which adds these capture fixes to `convai-v2.0.0.7-ptt.4` (upstream `v2.0.0`, FFI `0.12.69` unchanged): cancel stale microphone startup/resume, use unscaled waits, keep AEC recovery bounded, guard destroyed textures, and complete outstanding texture readback before releasing its buffers.
- Added source-level microphone and Unity vision/texture regressions. Device, GPU timing, and live conversation qualification remain separate gates.

### Breaking Changes

- `IConvaiRoomConnectionService.DisconnectAsync` (and `ConvaiRoomManager.DisconnectAsync`) no longer
  take a `DisconnectReason`; the argument was ignored. Every call is a client disconnect that ends
  the session. Migration: drop the argument, e.g. `DisconnectAsync(cancellationToken: ct)`.

- **Transcript console output changed.** Streamed character transcripts, character TTS text chunks,
  and player transcriptions no longer appear at Info, and Debug diagnostics contain participant,
  source, lifecycle or phase, and text length only. Migration: enable Debug for the
  Character/Events/Player categories when metadata is needed. Replace console-parsing integrations
  with `CharacterTranscriptReceived`, `CharacterTtsTextChunk`, or `FinalUserTranscriptionReceived`
  subscriptions or the transcript facade; transcript delivery and typed-echo suppression are
  unchanged. Raising the log level does not restore transcript text in these handler diagnostics.

- Removed `ConvaiManager.SetExplicitConversationTarget`; use `SetInitialCharacter` for startup
  selection, or the existing `TalkTo` API to change the conversation target in a live room.
- Removed `NarrativeDesignFetcher` and `FetchResult<T>`. Use `NarrativeDesignDataService` /
  `INarrativeDesignDataService` and `NarrativeFetchResult<T>` for section/trigger summaries. Consumers
  needing complete REST models should use `ConvaiRestClient.Narrative` directly.
- Removed the placeholder `ConvaiConversationProvider`. Normal room conversations use the room
  protocol; custom standalone providers still implement `IConversationProvider` and are supplied
  through `ConvaiRuntimeBuilder.UseConversation`.
- Removed unsupported participant-builder options (`WithLocalPlayer`, `WithOwnedCharacters`,
  `WithRoomMode`, `WithParticipantCoordinator`), their descriptors, and unused participant scaffolding.
- Removed unused runtime persistence-provider hooks, including `UsePersistence`, `Persistence`, the
  corresponding runtime constructor argument, and placeholder provider/result/options types.
  Application storage and the separately retained session-persistence APIs are unaffected.
- Removed nonfunctional `IPlayerSession` microphone methods/events and unused audio-provider/routing
  abstractions. Actual capture remains owned by `ConvaiRoomManager` and `ConvaiPushToTalkController`.

### Migration Notes

- The internal `TurnTakingOptionsDrawer` property drawer is removed.
- **Action Target** no longer offers *None* as a kind. A target already saved as None shows no selection until
  Object or Character is picked. A custom editor of your own that draws a
  `TurnTakingOptions` field with `PropertyField` now gets Unity's default nested drawer.
- `ConvaiCharacter` no longer carries `[Header]` attributes. They drew unstyled titles inside the custom
  inspector's sections.

### Compatibility and Qualification

- **Unity 6000.6 is supported.** The package did not build on 6000.6: Unity made the `int`-to-`EntityId`
  conversion an error there, and its implementation now throws even when reached by reflection. Ids the SDK
  hands out, such as those returned by the MCP tools, keep resolving on every supported editor. **One behavior
  change, on 6000.6 only:** a legacy 32-bit instance ID passed to an MCP tool no longer resolves, because Unity
  can no longer map one back to an object. Pass the id the tool returned instead. Verified by compiling on
  every Unity 6 stream from 6000.0 through 6000.6.
- The Multi-Character sample finds its manager and controller with `FindAnyObjectByType` instead of
  `FindFirstObjectByType`, which Unity deprecates from 6000.4 because it depends on instance-ID order. Outside
  the vendored LiveKit plugin, Convai code no longer calls any Unity API that is deprecated on 6000.6.
- Animation list, detail, download, and import remain on **Legacy in both API modes**, retaining
  server-side filtering/pagination and the existing media contract. API2 Animation media parity is
  not claimed. A completed-animation fixture is still needed to qualify the retained import path.
- Character/Narrative mutations, memory reads/writes, Core room connection, Platform/account APIs,
  and short-lived runtime auth-token flows keep their existing service ownership and routing.
- Changing API Mode back to Legacy does **not** restore removed C# APIs. Native-player/device,
  hosted WebGL/CORS, and clean-package import qualification remain outstanding; Editor and live-room
  test results do not replace those checks.
- See the [migration guide](Documentation~/rest-api-migration-4x.md) for replacement APIs and rollout
  behavior, and [REST API architecture](Documentation~/REST_API_ARCHITECTURE.md) for backend changes
  that would simplify the SDK.

## [4.6.0] - 2026-09-04

### Feature Additions

- **Conversation gaze.** In a room with more than one participant, only the character the player was
  addressing ever reacted. Everybody else sat in Idle, whose authored policy suppresses the player
  outright and commits to nothing — so a bystander never looked at the player who was speaking, and a
  character listening to *another* character had the speaker marked fully relevant and then no
  commitment with which to look at them. Only the occasional random idle glance ever landed, which
  reads as a room of people ignoring whoever is talking.

  **Attend To Speaker** on the Gaze component now follows the conversation happening around a
  character — the player, another character, or both (the default) — while that character is not in
  its own turn. Every participant carries an attention value that rises on a speech onset, on being
  addressed, or on being expected to answer, and fades continuously afterward rather than being held
  until a timeout and released all at once; whoever has the highest attention is who the listener
  looks at, so drifting off a conversation reads the way people actually do it. A listener's reaction
  to a new speaker is drawn per listener, per event — not a shared clock — and scaled by how near and
  central that speaker is to them, so a group never turns in the same frame even when the signal that
  moved them was identical; the room additionally keeps any two listeners' reactions to the same event
  from landing within a shared minimum gap of each other. The character being spoken to is untouched,
  so listeners read as listeners instead of as three copies of the addressee, and an eye-contact lock
  still overrides everything. A speaker occasionally checks a listener who is not the one it is
  addressing, and a challenger's eyes may flick toward it before the floor actually changes hands. A
  speaker across the level, or round behind the character's back with body turns off, is not attended
  at all.

  It follows the conversational **floor** rather than the two instantaneous speech flags, and the
  floor itself is now noticed from the player's own microphone rather than only the service's
  round-tripped confirmation. A speaker keeps the floor through their own pauses, and taking it from
  them needs sustained speech — so an interjection, a cough, or one false positive from voice
  detection no longer turns every head in the room at once and back again. That mattered because the
  player's voice flag reaches every character on the same frame from the same connection, which is
  what made a room of listeners move like one puppet. A player who *types* takes the floor too, which
  voice detection alone can never know.

  Four new settings in the profile's **Conversation** group tune the model: **Typical Pause Before
  Turning** (reaction latency's median), **How Much Reactions Vary** (its spread), **How Long
  Attention Fades Over** (the decay time constant), and **Shortest Gap Between Listeners Turning**
  (the room's minimum spacing between two listeners' reactions to the same event). Every shipped
  personality authors its own values, so Confident reacts slower and holds attention longer than
  Warm. The gaze inspector, the Gaze editor window and `Convai_DiagnoseGaze` each report one line
  naming who is being watched or which gate turned the speaker away.

  Every gaze target — a talking character's head bone, a walking camera — is now conditioned through
  a small motion filter before the movement detector ever sees it, so ordinary conversational bob and
  camera sway no longer trigger a head re-plan; a genuine teleport still snaps immediately.

- **Characters follow the conversation they are in.** In a room with more than one participant, only
  the character the player was addressing ever reacted. Everybody else sat in Idle, whose authored
  policy suppresses the player outright and commits to nothing — so a bystander never looked at the
  player who was speaking, and a character listening to *another* character had the speaker marked
  fully relevant and then no commitment with which to look at them. Only the occasional random idle
  glance ever landed, which reads as a room of people ignoring whoever is talking.

  **Attend To Speaker** on the Gaze component turns to whoever currently holds the floor — the
  player, another character, or both (the default) — while the character is not in its own turn. The
  character being spoken to is untouched, so listeners read as listeners instead of as three copies
  of the addressee, and an eye-contact lock still overrides everything. Each listener waits its own
  moment before turning so a group never snaps round in the same frame; the look is deliberately
  weaker than the addressee's, holds through a breath mid-sentence, and unwinds rather than snapping
  when the turn ends. While watching a character speak, a listener occasionally checks the person
  being spoken to, the way people in a group do. A speaker across the level, or round behind the
  character's back with body turns off, is not attended at all.

  It follows the conversational **floor** rather than the two instantaneous speech flags. A speaker
  keeps the floor through their own pauses, and taking it from them needs sustained speech — so an
  interjection, a cough, or one false positive from voice detection no longer turns every head in the
  room at once and back again. That mattered because the player's voice flag reaches every character
  on the same frame from the same connection, which is what made a room of listeners move like one
  puppet. A player who *types* takes the floor too, which voice detection alone can never know. And
  a listener that has settled on the speaker is no longer pulled off onto a silent bystander by the
  arbiter's interest budget: the character with the floor is published a priority tier above the
  room's idle characters.

  Manner and limits live in the profile's new **Conversation Attention** group so one shared profile
  governs a cast. The gaze inspector, the Gaze editor window and `Convai_DiagnoseGaze` each report
  one line naming who is being watched or which gate turned the speaker away.

- **The inspector says how far off target the eyes actually are.** `Off target` in a gaze
  controller's Live section is the angle between where the eyes end up pointing and where the target
  is, composed — face-scan landmarks, micro-saccades, fixation drift and per-eye rest reframing all
  included. The existing contact error compares the solver's internal angle against its own goal and
  is blind to every offset applied after it, which is how a character could be two degrees off the
  camera with nothing reporting it.

- **Characters notice you as you start talking, not a round trip later.** Whether the player is
  speaking has only ever been the service's call, and it is announced over the network — so a
  character did nothing at all until the verdict arrived, and then reacted. The delay is small and it
  reads as the character being slow rather than as the network being slow.

  The SDK now also watches locally and publishes what it sees as `LocalPlayerActivityChanged`, in
  both turn-taking modes. Hands-free gets a level gate on the microphone that is already open; it
  learns the room's own noise instead of using a fixed threshold, so it behaves the same on a quiet
  headset and a hot desk microphone, and it is held shut while the character is audible unless echo
  cancellation is genuinely running. Push-to-talk gets something better: the press is a decision
  rather than a sound, so it fires before the player has said a word.

  A character acts on this by moving to **Attending** and no further — it cannot reach *Listening*,
  take a turn, or send anything, so a false positive costs a glance rather than a mistake. The event
  is public for microphone meters and "we can hear you" indicators; the service's
  `PlayerSpeakingStateChanged` remains the only thing that commits a turn. Per character, **Notice
  You Locally** on the Conversation Flow profile turns it off.

- **A room opens on the character the player is looking at.** It opened on the first character in
  scene order, so in a two-character scene a player who spent the whole connection looking straight
  at one of them was answered by the other, and only then did targeting move the conversation
  across. Nothing was misconfigured and no setting was obviously wrong, which is the worst way for a
  scene to feel broken. Only the fallback changed: an assigned **Convai Manager → Initial
  Character** still wins outright, and a player looking at nobody still gets scene order. The
  Console names the character it opened on and why.

  The Inspector was hiding the controls for this in exactly the scenes that needed them. **Initial
  Character** and **Who The Player Talks To** appeared only when more than one character was active
  *right now*, so a scene whose second character starts disabled and is enabled during play — the
  setup the SDK documents — showed neither. They now appear whenever more than one character is
  selected for the room. Assigning a character that starts disabled is no longer an error either: it
  is a warning saying it will start the conversation once it is enabled, and naming what the room
  starts on until then.

- **The SDK now says whether the player can talk, and the chat field acts on it.** A room being
  connected has never meant the character being addressed can hear anything — it has to be announced
  by the service first — and everything in the product reported the first fact while the player
  experienced the second. The shipped chat field stayed usable and permanently labelled "Click here
  to type", so a greeting typed the instant a character appeared was accepted, sent and lost, with
  nothing logged anywhere: it had already passed every check the SDK had.

  `ConvaiManager.ConversationAvailability` answers it in one place — `Offline`, `Connecting`,
  `Preparing`, `Ready`, `Answering`, `Unavailable` — for `ConvaiManager.AddressedCharacter`, which
  names who the player is talking to whether or not the room keeps a roster.
  `ConversationAvailabilityChanged` reports each move, and `CanAcceptPlayerInput()` is the question
  a UI actually wants to ask. The same answer is available per character on
  `ConvaiCharacter.ConversationAvailability`.

  Everything that takes player input now consults it rather than deciding for itself. The chat field
  disables until the character can hear, and its prompt names the state and the character — "Sofia
  is getting ready…", then "Message Sofia" — with every string overridable on the component.
  `ConvaiPlayer.TrySendTextMessage` refuses with a reason a project can show, instead of dropping
  the message in one of four places that each logged and returned nothing. Push-to-talk refuses
  while the character cannot hear, and hands-free waits for the room to confirm its first character
  before opening the microphone, so the player is not talking into a room that is not listening.

  **Behaviour change:** in multi-character scenes the microphone now opens when the room confirms
  its first character rather than when the transport connects — typically a fraction of a second
  later. Single-character rooms are unaffected, because their session only reaches connected once
  the character has been announced.

- **A second character in the scene is now a conversation, not a configuration exercise.** A room
  with more than one character routes what the player says to one of them, and until now nothing in
  the package ever changed which one — the only implementation lived in the Multi-Character sample,
  so a scene that gained a second character gained a character who could not be spoken to. The
  manager now keeps the conversation pointed at whoever the player is addressing, with no component
  to add, no collider, no layer and no field to fill.

  The default reads the player's view rather than casting a ray, because a ray needs a collider and
  a layer mask and finds nothing — silently — when either is missing. It scores by angle with
  distance as a tiebreak, so it works on any character with a transform, and it works the same on a
  mouse, a gamepad, a touch screen and a head-mounted display because it reads the camera rather
  than the input.

  Three rules keep it from being annoying. **A glance is not an address**: a character has to be the
  clearly better choice for a moment before the conversation moves, so sweeping the view across a
  room does not address everyone it passes. **The player's own sentence pins the target**: the
  conversation cannot move part-way through what the player is saying, and a glance made during it is
  remembered rather than discarded. **The target never clears itself**: when nobody
  qualifies the last character addressed keeps the conversation, because a cleared target means the
  player speaks and nothing answers, which reads as a broken game rather than as addressing nobody.

  `ConvaiManager.ConversationTargeting` tunes it — `Mode` (`LookAt`, `Proximity`, `Manual`), the
  eligibility range and angle, and the margin and delay that stop the target flickering between two
  characters standing close together. `ConvaiManager.TalkTo(character)` points it explicitly for
  scripted conversations (the latest request waits for the player's current utterance to end),
  `ConversationTargetRequested` reports after input closes and just before the send attempt, and
  `ConversationTargetChanged` reports the authoritative response — a round trip apart, so anything
  the service does because of the move arrives between them — and
  `IConversationTargetProvider` replaces the rule outright for games whose own rule is none of the
  shipped ones.

  **One character speaks at a time.** This is the service's contract, not the SDK's choice: moving the
  conversation ends the previous character's turn, so a character part-way through an answer stops
  when the player addresses somebody else. Scenes where a line must always be heard in full should
  either use `Manual` mode and move the conversation themselves, or not move it while that character
  is speaking — `ConvaiCharacter.IsSpeaking` says when that is.

- **A character that appears while the room is connected now joins it.** Spawning a character from a
  prefab mid-session is an ordinary thing to do, and it used to do nothing visible: ownership
  resolution noticed the new character, decided the composition had changed, and queued a reconnect
  that would only happen if something else disconnected the room. The service has accepted live
  roster changes all along and nothing called them. A character appearing is added to the room and
  one destroyed or dropped from ownership is removed, both without a reconnect, and both say so in
  the Console.

  Nothing to call, and no reconnect to sit through: instantiating a character prefab — or enabling a
  character GameObject — is the whole integration. The character announces itself the way late
  modules already did, gets injected, and takes a seat. Disabling it keeps that seat but makes it
  ineligible to be addressed, so enabling it again is immediate and needs no roster round trip. A
  character that appears while the connection is still in flight is seated as soon as the session
  is ready, instead of being lost to the timing.

  **A room that connected with one character is the exception, and now says so.** Such a room is
  opened for that character alone and carries no roster, so there is nothing for a later character to
  join; it waits for the next connection. That was previously silent — the character was injected,
  owned, enabled and simply never spoke, which is indistinguishable from a broken scene. It now
  names itself and what to do instead: *"'James' cannot join this conversation: the room was opened
  for a single character… To let characters come and go during play, have every character you want
  in the scene when the room connects."*

  The check is deliberately narrow: only a roster difference is applied live. A changed player or a
  changed initial character still means a reconnect, because those decide how the room was created
  rather than who is in it, and emptying the room is left to the path that knows how to refuse it.
  The `RuntimeCharacterSpawner` sample script now leans on this: it no longer rewrites the
  manager's ownership to the spawned character or forces a disconnect when spawning into a
  connected room.

- The **Multi-Character sample** no longer implements the feature it demonstrates. Everything it
  used to — raycast targeting, an aim cone, a look-away grace period, retaining the target through a
  turn — is in the SDK now and needs no scene code at all, so what remains is sixty lines showing
  the two members a project would actually use to draw its own indicator, alongside the sample's own
  input plumbing. The F6 dynamic-roster acceptance path is gone: it was engineering verification,
  and it should not have shipped to customers. The player component is no longer called
  `MultiCharacterTestPlayerController` either — it is the sample's player, not a test rig, and is
  now `MultiCharacterSampleFirstPersonPlayer`, keeping its script GUID so the scene resolves it
  unchanged.

- **The conversation is on the typed event surface.** `ConvaiManager.Events` carries every other
  room fact, and the three that matter most in a room with several characters were reachable only as
  C# events on the manager — so a project that had standardised on the event surface could not see
  them, and neither could the runtime event trace the MCP tools use.

  `OnConversationAvailabilityChanged` reports the answer to "can the player talk right now", the
  verdict it replaced, and the character it is about. `OnConversationTargetChanged` reports each move
  of the conversation with its phase — `Requested`, `Confirmed`, and `Failed`, the last of which had
  no event at all and was reported only to the Console. `OnRoomRosterChanged` reports a character
  joining or leaving a connected room during play, and a refused edit with the reason, which
  previously existed only as a Console line a build cannot act on.

  The existing C# event names on `ConvaiManager` remain the shortest path for a single reaction.
  Their target payload now follows the service's authoritative route, including a late response or
  a newer route carried by a rejected request, instead of assuming the requested character won.

- **The Convai Manager inspector answers multi-character questions while the game runs.** The
  troubleshooting guide asks "is the other character `Ready`?" and "is the conversation holding or
  stuck?", and until now neither had a visible answer anywhere in the editor — only the Console at
  Debug verbosity, or the MCP diagnosis. The Live section now names who is being addressed, whether
  the player can talk to them and why not, whether the room carries a roster at all, the verdict
  behind the last targeting decision, and one row per character in the room with its status and, for
  a failed one, its failure code. The character inspector reports the same availability verdict per
  character, next to the readiness flag it is so easily confused with.

- **`Convai.ConfigureConversationTargeting`** joins the MCP and Unity Assistant tool set. An assistant could
  diagnose a multi-character room but not change one: the targeting rule, the four numbers behind it,
  the view camera, the initial character and the room selection had no tool at all. Every field is
  optional and omitting one leaves it exactly as the project authored it, and the whole request is
  planned before anything is written, so one bad value changes nothing rather than half-applying.

  `Convai.DiagnoseConversation` now reports the targeting rule with its values and whether they are
  still the shipped defaults, the addressed character, and the availability verdict — the first thing
  its own documentation tells a reader to check. `Convai.GetGuidance` gains a `MultiCharacter` topic,
  and the packaged assistant skill gains a multiple-characters reference. Tool contract version 5.

- **`ConvaiManager.CharactersToConnect` and `ConvaiManager.InitialCharacter`** are readable. Both had
  setters and no getters, and the room selection is an exact set rather than a list to append to — so
  a caller that means "and also this one" had no way to start from what was already there.

### Improvements

- **The Convai Manager inspector no longer says the starting character is the character being
  talked to.** With several characters in the scene and no **Initial Character** assigned, the
  manager reported *"Starting on Sofia — the player starts the conversation with this one"*. Under
  either automatic targeting mode that is true for about a fifth of a second: the room opens on the
  first character in scene order, and from the moment it is ready the targeting rule re-checks who
  is being addressed fifteen times a second and moves the conversation to whoever the player is
  looking at. Somebody reading the box concluded the field decided who the player talks to, and the
  same claim was in `MULTI-CHARACTER.md` ("an assigned one always wins") and on
  `ConvaiManager.InitialCharacter` itself.

  The box now leads with the rule — *the player talks to whichever character they are looking at* —
  in the words of the mode that is actually configured, and names the starting character second, as
  the fallback it is: who is addressed *until* the player looks at somebody. Heading the box with a
  character's name read as an instruction, as though that character had to be talked to or was about
  to say something, and neither is true. Assigning **Initial Character** puts the same rule under the
  field, because that is where the wrong conclusion was being drawn. **Initial Character**'s tooltip
  says it is optional — it always was, and the old tooltip claimed it was "required" once a second
  character could join.

- **Multi-character controls renamed for people meeting them for the first time.** *Include in Next
  Room* → **Characters Joining the Room** (there is only one room, and "next" read as a second one),
  *Within* → **Range (Metres)**, *Seen From* → **Player Camera**, *Scene Characters* → **Characters
  in Scene**, and *Room Shape*'s values *Roster room* / *Single character* → **Several characters** /
  **One character only**. **Look Angle**, **Switch Margin** and **Switch Delay** now carry their unit
  in the label, since a column reading `30 / 25 / 20 / 0.2` gave no way to tell metres from degrees
  from seconds. Console messages, the MCP tool descriptions and `MULTI-CHARACTER.md` follow the new
  names; a message naming a control that is not on screen is worse than the name it replaced.

- **Every Convai inspector section now lines its controls up with each other.** A section body
  opened its content with `EditorGUI.indentLevel++`, which indents anything drawn through
  `EditorGUI` — a property field, a toggle — and nothing the design system draws with `GUILayout`:
  the reading rows, the group captions, the paragraphs. Two families of control in one body
  therefore disagreed about the left margin by fifteen pixels, and because an indent applies to
  nothing on the right, a property field ran flush into the card edge while its own label sat inset.
  **Initial Character** looked like a sub-item of the readings above it; it was a sibling. Section
  bodies now carry symmetric padding instead, honoured by every control regardless of which API drew
  it. An `indentLevel` raised inside a body still nests sub-fields, which is what indenting is for.

- **One left edge for every label in a section.** Left to their own Unity base metrics, the design
  system's styles opened a line on four different pixels: a reading row's label on 0, a group
  caption on 3, a property field on 4, and a micro sub-caption on 6 — `miniBoldLabel` carries a
  margin of 4 and a padding of 2 where `label` carries 3 and 1, and nobody ever chose that. The
  visible result was captions sitting slightly inside the rows they name — **Session** and
  **Settings In Effect** indented from their own readings, **Frame Sampling** and **Respond Modes**
  indented from the fields beneath them — which reads as an accident, because hierarchy is not four
  pixels deep. Every one of them now lands on 4, the one edge that cannot be moved: it is where
  Unity draws a property field's label. Buttons drawn by rect take the same inset, so a button's
  frame starts where the words above it start. Table cells are explicitly held back from the shared
  edge — they are laid out against widths their caller measured, and a cell nudged four pixels is a
  column out of line with its own header.

  Matching the style tables was not enough on its own, and the reason is worth recording: a group
  caption drew itself with `GUILayout.Label` while a reading row placed its label inside a
  horizontal layout group, and those two IMGUI paths do not apply a style's left margin the same
  way. With every style reporting the same edge and the caption still sitting inside its own rows,
  the styles were ruled out and the layout ruled in. Both primitives now reserve a row and draw into
  it at the shared inset, so their left edge comes from one number rather than from two style tables
  that happen to agree.

- **The Convai Player says what it is for, and stops pointing at the wrong component.** Its
  description sent readers to "ConvaiManager" for the conversation mode — a class name rather than
  the component's name, and the wrong component besides: whether the player talks hands free or push
  to talk is a **Convai Room Manager** setting. A `[Header("Player Configuration")]` was drawing a
  second, bolder caption one line under the **Identity** section that already named it. Its
  Validation section gets the same treatment as the Room Manager's — a header coloured by what it
  found, a verdict in the collapsed summary, no "Player setup / Player setup looks healthy" box, and
  ghost buttons instead of Unity mini buttons.

  The checks themselves stop naming classes: *"Add one ConvaiManager"* is now *"Add a Convai
  Manager. It starts Convai for the scene, and without one this player never joins a conversation"*,
  and *"the SDK will reuse Player Name for local transcript attribution"* now names the actual name
  it will use. A player with everything filled in gets one line saying who transcripts will be
  attributed to, because a section holding nothing but two buttons reads as broken.

  Its Runtime section is the two-column readout the rest of the product uses rather than three
  disabled text boxes, with the name tag drawn as a colour swatch, and the inspector repaints while
  the game runs so the readings are current rather than whatever was true when it was last drawn.

- **The Convai Room Manager's Validation section stops crying wolf.** Its header was permanently
  amber, so a scene where every check passed still wore the one colour in the inspector that means
  *look here*. The header now takes the colour of the worst check it found, and carries the verdict
  as a summary — **All checks passed**, **Review**, **Needs fixing** — so a collapsed section still
  reports. A healthy scene no longer gets a message box saying "Scene setup / Scene setup looks
  healthy", which said the same thing twice and then a third time in the ticks below it; the box
  appears only when something is wrong, which is what makes a box worth noticing.

  Each check was drawn in the *section title* style, so four checks read as four headings competing
  with the real one above them, and the tick carrying the verdict was a character inside that text —
  the same weight and colour whether the check had passed or failed. A check is now a status dot,
  its title, and the word for anything that is not healthy (**Check this**, **Blocked**) so the
  verdict never rests on colour alone. "No room-specific issues detected" is gone: it sat under the
  last check with no separation and read as that check's own message.

- **The Room Manager's Runtime section reports a running scene the way the Convai Manager does.**
  Nine disabled text boxes — the shape of a form somebody has greyed out, which a reader tries to
  type into — are now the design system's two-column readings, grouped under **Session**, **Settings
  In Effect** and **Wiring**, with the connected state and the vision context tinted. The inspector
  also repaints while the game runs; without that it showed whatever was true when it was last
  drawn, and a stale live reading cannot be told apart from a current one. The redundant "Core
  Server URL Source: Project Settings" row is gone — the button underneath it goes there.

- **Three `[Header]` attributes were rendering a second caption inside sections that already had
  one.** Unity draws a header wherever its field is drawn, and every field on `ConvaiRoomManager` is
  drawn by the Convai inspector inside a group it has already named — so "Configuration" appeared
  under **Room Source**, "Dynamic Vision Context" under its own group caption, and "Inline Reconnect
  Defaults" in the middle of **Reconnect**. The headers are removed; grouping is the inspector's
  job. Section buttons are the design system's ghost buttons at their measured width instead of
  Unity mini buttons pinned to a hand-measured 160 pixels, which both clipped one label and left
  another swimming.

- **Turn detection explains itself in the reader's words.** *"SDK Default resolves to smart turn
  enabled with Stop Secs = 3, Pre Speech Ms = 0, Max Duration Secs = 8"* listed the same three
  numbers as the fields directly beneath it, in the vocabulary of the wire; it now says what the
  numbers mean — the turn ends after 3s of silence and never runs longer than 8s. The same pass
  reached *"resolves to no `turn_detection_config` being sent"* and the Initial Server STT
  sentences. **Enable Acoustic Echo Cancellation** is now **Echo Cancellation**: it was long enough
  to be cut off mid-word, and the group above it already says Local Audio Policy.

- **The Convai Manager's room selection is a list, not a run of loose tick boxes.** The characters
  sit in their own recessed panel with striped rows and room to breathe, so it is visible where the
  list starts and stops; a character's status is right-aligned in the row it belongs to. A rule
  separates what the scene *is* — Room Manager, Player, character counts — from what you *set*
  below it, because a field standing among readings reads as a reading that has somehow become
  editable. **Include Everyone** and **Include Nobody** were two full-width slabs directly under the
  last tick box, giving them the weight of the section's primary action; they are ghost buttons at
  their natural width now, parked on the right and clear of the tick column.

- **Less to read in the Convai Manager, in the places that were saying it twice.** The character
  list carried its explanation above the tick boxes and again underneath them; the per-character
  status column repeated what the tick box already said, and now speaks only for the two things it
  cannot — a character this manager does not control, and one that is ticked but switched off in the
  scene. *Who The Player Talks To* closes with two paragraphs instead of three. The **Scene
  Installer** row is drawn only once a Scene Installer exists, rather than reading "None" in every
  project that has never needed one. **Look Angle**'s tooltip no longer illustrates itself with `35`
  while the field shows a different number.

- **LiveKit Unity 2.0.0 with livekit-ffi 0.12.69.** The vendored LiveKit runtime moves from the
  patched 1.3.8 snapshot to 2.0.0, carried as a tagged build of the Convai-maintained
  `Conv-AI/livekit-client-sdk-unity` fork (`convai-v2.0.0.2`, see `Documentation~/LIVEKIT-VENDORING.md`).
  Native side: failed resume attempts escalate to a full reconnect, reconnect retries use jittered
  exponential backoff instead of a fixed five seconds, dynacast no longer pauses the only layer of a
  non-simulcast video track, and a native FFI panic now surfaces as a disconnect instead of
  silently stalling the room. Managed side: connect, publish and unpublish resume on the frame the
  native callback lands rather than polling every 50 ms.
- **Native libraries are verified.** The editor installer pins the FFI release and the sha256 of
  every platform zip, records what it installed in `livekit-ffi.lock.json` next to the binaries,
  and purges and reinstalls whenever the pinned release changes. A download that fails
  verification is discarded. On first open after this update the 0.12.63 binaries are replaced
  automatically. Linux ARM64 can be fetched from **Convai > Platform Support > Download FFI
  Libraries (All Platforms)**. Unpacking is all-or-nothing: if a file on disk cannot be replaced
  (on Windows, the library the editor already loaded) the folder is reported as not installed,
  nothing is recorded in the lock, and the next editor start retries instead of treating the old
  binary as verified. The shared Android `libwebrtc.jar` lands in exactly one architecture folder,
  including when an architecture is added to an existing install, so Android builds no longer hit
  duplicate classes.
- `TransportConnectOptions.SinglePeerConnection` (`bool?`) and
  `VideoPublishOptions.Degradation` let a project pin the native topology and the encoder's
  degradation policy. Both default to the transport's own choice.

- **The packaged `convai-unity-sdk` assistant skill was tested against realistic requests and tightened
  where an assistant went wrong.** Eleven planning runs (eight with the skill, three without) showed
  four repeatable failures: the Character ID was written with a second `Convai.ConfigureCharacter` call
  after `Convai.SetupConversationScene` had already accepted it, because the workflows reference was
  never linked; assistants invented argument names for `Convai.TraceRuntimeEvents` and the feature
  tools, because the tool contract gave those tools one line and no inputs; an explicit "fix it"
  still ended in "shall I apply it?"; and one assistant quoted a pasted API key while refusing to
  store it. The skill now links the workflows reference, states the setup and fix rule as a
  condition on the request rather than a blanket "apply when requested", forbids repeating any part
  of a key that appears in chat, and names the only two arguments that ever expose transcript text.
  The tool contract gains a real input line for every feature tool, including the trace operations
  and the exact `ConvaiManager.Events` names its filters accept, and the custom-actions reference
  gains one complete executor. All five failing scenarios pass on re-run.

- **Characters stop performing when their voice stops, not when the network says so.** Whether a
  character is speaking was decided by a message from the service — and that message is sent after
  the sound has already finished. Measured against a live backend it arrived 1.5–1.7 seconds late,
  with the turn-completed message a further 0.7 seconds behind that. Everything layered on top of
  speaking waited for it: the gesturing, the speaking stance, the breath, where the character looked,
  the beat where it hands the turn back to you. A character finished its sentence and then carried on
  performing into the silence, which reads as the character being slow rather than the network.

  The SDK now hears the voice stop and lets whichever source knows first end the turn, so a late
  confirmation arrives as a no-op instead of as a delay. Because a pause between sentences sounds
  exactly like the end of an answer, the ending is confirmed over **Voice End Hold** before anything
  acts on it — a voice returning inside that window cancels it with nothing on screen having changed.

  **Nothing was made snappier.** Every release — the talk layer's fade-out, the emotion prosody ease,
  the hand-micro slew, the settle beat and its breath — keeps the exact shape and duration it had.
  Only the moment they begin moved earlier, and the mouth is deliberately on a shorter leash than the
  body so an ending reads as a person finishing rather than as a switch being thrown.

  The service keeps everything that is genuinely its to decide: it still ends a turn immediately on
  an interruption or a cancelled response, it still says who holds the turn in a room of several
  characters, and `ConvaiCharacter.IsSpeaking`, `OnSpeechStopped`, `OnTurnCompleted`, the transcript,
  the microphone hand-back and the latency metrics all read exactly what they read before. Per
  character, **Stop When The Voice Stops** on the Conversation Flow profile restores the old timing.

- **The Multi-Character Sample keeps five focused EditMode input regressions.** Its small test
  assembly covers the sample's own cursor-lock predicate and joystick dead zone, while conversation
  targeting behaviour remains in the SDK's `ConversationTargetSolverTests` and
  `ConversationTargetSwitchPolicyTests`. Keeping those boundaries explicit prevents a sample input
  change from silently breaking desktop or mobile control while avoiding duplicate SDK behaviour
  tests in the sample.

- **One name per conversation state, everywhere.** The gaze personality table relabelled two of the
  eight states on its own — `Attending` was drawn as "Addressed" and `Settling` as "Winding down" —
  while every live panel in the SDK, and the API, used the state's real name. A user watching a
  character sit in "Attending" had no row called that to go and edit. All four modules that show the
  live state now read from one shared vocabulary, and the plain-English meaning sits beside the name
  instead of replacing it.

- **A gaze personality now applies the whole personality.** Each one wrote the per-state table plus
  nine feel fields and left the rest alone, so switching from Shy to Stoic produced neither: a Stoic
  with Shy's restless micro-saccades, Shy's face scan and Shy's nodding. Every field the three
  inspector dials read — eye liveliness, head entry angle, idle curiosity, listening nods — is now
  authored on all six, and a test applies every personality on top of every other to prove nothing
  of the first survives.

- **The six gaze personalities are now distinguishable.** Measured on the three dials the inspector
  shows, four of them sat inside 0.12 of each other and Warm was 0.009 from Default — picking a
  different pill changed almost nothing. Confident, Warm, Attentive, Shy and Stoic have been retuned
  around distinct ideas (unbroken and economical; animated and close; a still, nodding listener;
  reserved and restless; head-still and unreadable) and the toolbar order now runs Default,
  Confident, Warm, Attentive, Shy, Stoic. A test fails the build if any two drift back together.
  Existing profiles are untouched; re-click a personality to take the new tuning.

- **`Reacting` is reachable.** One of the eight conversation states had a row in every personality
  and in the profile table but nothing in the SDK ever entered it. `ConvaiConversationFlowController.PulseReaction(seconds)`
  is the verb for the moments the conversation cannot see — a door slams, a quest beat fires — and
  returns the character to whatever beat the conversation is actually on afterwards. A beat asked
  for while the character is not yet in the conversation is dropped rather than held, so it cannot
  be the one thing that overrides "not ready means idle".

- **The editor now says, before Play Mode, when a second Character ID has been duplicated.** The
  Character inspector validated the *shape* of a Character ID and never asked whether anything else in
  the scene was already using it, so the one mistake most likely to produce a second character —
  duplicating the first — was invisible until the room refused to connect. The inspector now names the
  other characters holding this ID, and `Convai.ValidateSetup` reports the same thing. Both ask the
  same rule `Convai.DiagnoseConversation` already used, so the three cannot drift apart.

- **A scene that is about to open a room no later character can join now says so.** A room is opened
  for the characters that are *active* when it connects, and one active character means a room with no
  roster — which cannot grow, so a character enabled during play waits for the next connection. That
  was documented and the Console said it at the moment it bit, which is halfway through Play Mode on a
  scene the author left the editor believing was a two-character scene. When more than one character
  is selected for the room and only one is active, **Convai Manager** now says the room will not be
  able to grow and offers to enable the others; `Convai.ValidateSetup` warns about the same shape.

- **`Convai.GetGuidance(MultiCharacter)` says how to add the second character.** It described how to
  tune a multi-character room and never how to reach one, so an assistant asked to add a character to
  a working scene had `Convai.SetupConversationScene` — which builds a whole first conversation — as
  the nearest-looking tool. It now names the two steps that are actually right, and the Character ID
  rule that decides whether the result will connect.

- **A glance made while the player is still speaking now counts towards the switch delay.** The
  conversation cannot move part-way through the player's own sentence, and it should not: the back
  half of a sentence reaching a different character is not something a later correction fixes. But
  the time spent looking at that character was thrown away at the full stop and the delay started
  again, so a player who held the look right through their sentence was asked to hold it a second
  time for a reason nothing in the game shows them. The documented rule — remembered, not discarded —
  is now the implemented one. A glance the player abandoned before finishing still does not move
  anything, because the next evaluation sees whoever they are actually looking at.

- **A roster that is obviously wrong is caught where it was built.** The 50-character client ceiling
  was checked when the room was created and not when a character was added to a live one, so a
  spawner in a loop reached the same limit one character at a time and failed on the wire instead.
  Both paths now refuse with the same message, which also says that the account's plan may allow
  fewer and that the service is the authority.

- **The Convai Manager inspector says that several characters need account access**, in Edit Mode,
  under "Who The Player Talks To". Multi-character rooms are a Convai account feature, and a project
  without it found out in Play Mode from a room that stopped connecting entirely — including the
  character that had worked a moment earlier. Nothing in the editor can check the account, so this
  is a stated fact in the same register as the one-speaker-at-a-time rule beside it, rather than a
  warning box claiming a verdict it has not measured.

- **The "Who The Player Talks To" section no longer disappears once it has been tuned.** It was kept
  visible for a scene that had customised targeting and then dropped to one character, but the check
  only looked at the mode and the view camera — so the four numbers that actually get tuned in play,
  the range, the angle, the margin and the delay, were exactly the ones it could not see.

- **The Multi-Character Sample no longer imports failing tests.** Four of the tests in its EditMode
  assembly had the sample's own address latch as their subject — code the SDK now owns — so
  importing the sample put four failures into the project's Test Runner, reflecting into a method
  that no longer exists. What they asserted is covered where the behaviour actually lives, in
  `ConversationTargetSolverTests` and `ConversationTargetSwitchPolicyTests`, except for one that
  asserted the conversation clears when a turn completes — the opposite of the shipped rule, which
  is that the last character addressed keeps it. The assembly keeps the tests that still have a
  subject: the cursor-lock decision and the joystick dead zone.

- **Room startup errors say what to do.** The three a project can actually hit named internal types
  and resolution steps — *"Failed to resolve an owned player"*, *"Room ownership did not resolve an
  active conversation target"* — which tells somebody new to Convai nothing about their scene. They
  now name the component to add and the Inspector field to set. The two that indicate an SDK fault
  rather than a scene problem now say so, and point at the earlier Console error that is the real
  cause. `Convai.DiagnoseConversation`'s `PLAYER_MISSING`, `PLAYER_AMBIGUOUS` and `CHARACTER_MISSING`
  messages were reworded the same way.

- **A character added to a live room can be awaited.** A roster update is acknowledged as soon as the
  service accepts it, which is before the character can say anything, so
  `CharacterRoomMembership.WaitUntilReadyAsync` completes when it is ready to talk and faults when it
  fails to start. Every caller was otherwise writing the same polling loop -- the shipped sample
  included -- and a retired session now releases anyone waiting rather than leaving them there.

- **`Convai.DiagnoseConversation` now reports multi-character rooms.** It said nothing about them at
  all, which left the two questions people actually ask unanswerable from the diagnosis: which
  characters made it into the room, and who is the player talking to. It now returns the roster with
  each membership's status, failure code and participant binding, the active membership, the
  addressed character, the targeting mode, and the verdict behind the last decision — the verdict
  because a conversation holding correctly and one that is stuck look identical from outside.

- `CONVERSATION_TARGET_MISSING` was replaced by `CONVERSATION_TARGET_DEFAULTED` (severity `Info`),
  since an unassigned Initial Character no longer stops anything. Tooling that keyed on the old code
  should read the new one; it no longer affects `readyToRun`.

- **Turning to a target the head cannot reach.** When the player moved beyond what the head and
  chest can cover (about 95° round), the body turn waited for the neck's comfort pressure to build —
  about a second and a half — because the residual left for the feet (18°) sat under the feet's entry
  angle (25°) while the eyes were parked 28° off the target at their limit. It measured as the head
  hitting its limit and staying there before the whole character turned. The body turn now fires as
  soon as the target is out of reach of the head and chest together (after the usual feet onset), in
  the willing states as it already did in the reluctant ones.

- **A camera cut no longer makes the character whip round.** Teleporting the player, or switching
  to a camera somewhere else in the scene, moved the character's target without the character
  having decided anything — and gaze answered that with the tempo reserved for a startle. A 90°
  cut was closed in about two thirds of a second with the head peaking near 130°/s, which reads as
  the character being alarmed by its own camera. A cut is now an ordinary look: the same movement
  law as deciding to look at anything else, about a third longer, and peaking near 100°/s. The eyes
  are untouched — they still re-acquire the target with a single ballistic saccade the instant it
  moves, and the head follows them as it always did. The one movement still executed at reflex
  speed is the startle re-acquisition, which is the only thing that happens *to* the character
  rather than being chosen by it.

- **The two head-and-body speed ceilings ship at speeds a person can actually reach.** *Head Speed
  Limit* was 240°/s and is now **150°/s**: it is a safety ceiling rather than a speed setting, and
  at 240 it was so far above anything the shipped turn times produce (about 100°/s on the largest
  look the neck takes alone) that it could not catch a mis-authored profile before the movement had
  already stopped reading as human. *Turn Speed (Rigs Without Turn Animation)* was 140°/s and is now
  **90°/s**: that is the peak of the procedural body turn, which is the only turn a character
  without analyzed turn-in-place clips has, and at 140 a standing character came round like a
  turntable. At 90 a 180° pivot takes about three and a half seconds. Both numbers are defaults —
  profiles you have already authored keep the values saved in them.

- **Look-away beats are the size of a glance again.** The cognitive "thinking" beat drew up to 22°
  of yaw and 14° of pitch, and the natural conversational break up to 13° of yaw. At that size the
  character was not averting its gaze from the person it was talking to, it was looking at
  something else behind them, and coming back read as a second decision rather than as the end of a
  thought. Cognitive beats are now 8–14° of yaw and 5–10° of pitch, natural ones 5–9° of yaw with
  pitch scaled to match. How often beats fire, how long they last and how much of one the head
  takes are all unchanged — only how far the character looks.

- **Following a moving target.** Measured on the head bone against a moving camera, the head
  parked in place for stretches — 71 of 599 frames on a slow 4°/s orbit — then jumped ahead in
  bursts at more than double the target's actual speed; a faster sweep sat still for a beat and
  then overshot the target's speed by a third; and a hand-held camera's shake came straight through
  as head shake, one-for-one. The cause was the same shape three times over: the head's following
  lane held its goal inside a small dead band (*Ignore Small Target Movement*) in front of a
  speed limiter that never questions in-budget motion, and a separate classifier decided, a tenth
  of a second late, when a target counted as "moving" and then closed the gap the dead band had
  left. Each of those is a discontinuity in the goal wearing a different name.

  The head and chest now follow a moving target continuously through a single tracker built for
  that job — no dead band, no delayed hand-off, no catch-up — trailing a constant-velocity target
  by a small, steady distance rather than by fits and starts, and a movement toward a moving goal
  now lands already moving with it instead of arriving at rest. The character's whole head motion,
  after every other pass (stabilization, aversion, gestures) has had its say, is now held inside
  its speed and acceleration limits as one signal, not lane by lane. *Ignore Small Target
  Movement* is gone; two new settings replace it, see Breaking Changes.

  Once the head followed its share faithfully, a second defect showed through: the comfort model's
  orbit-return pressure was hunting the head about three degrees back and forth every two seconds
  around a held look — pressure released in a third of a second and handed the head's extra share
  straight back, so the return itself became the oscillation. Relief now sheds over a second and
  only once the eyes are back inside half the comfort angle, so a settled look settles.

- **Looking at somebody at the edge of reach.** A fast camera move to 90° used to end with the head
  overshooting the limit and ringing against it for a third of a second: a movement was planned for
  the first few degrees of travel and kept that short clock while the target kept moving, so it
  landed at speed with nowhere left to go. A movement toward a goal that keeps moving now
  lengthens to the time its total distance deserves and lands on the goal matching its speed,
  instead of keeping the short clock, sprinting into the limit and correcting.

  A beat later, the same look could swing 20–25° back off the target the moment the idle animation
  turned the head the other way — the range limit was being applied to gaze's own contribution
  rather than to where the head actually ends up, so near the limit gaze had nothing left with which
  to cancel the animation's turn. The limit now applies to the total pose.

  Two related defaults changed. **Neck Relaxes During A Turn** (`bodyTurnHeadRelief`) is now **1**
  (was 0.4): the head no longer lets go of the target the moment a body turn is requested, and a
  turn now fires as soon as the target is genuinely out of the head's reach rather than after the
  head has already been released — the neck's own comfort pressure builds faster the further past
  comfort the head is held, in place of the fixed release. **Chest Speed Limit**
  (`maxTorsoAngularSpeed`) drops from 180°/s to **90°/s**, matching the same "ceiling, not a speed
  dial" rule as the head's limit above.

### Bug Fixes

- **The package compiles on Unity 6000.5 again.** Unity is migrating scene identity from a 32-bit
  handle to a 64-bit `EntityId`. `Scene.handle` is an `int` up to 6000.2, becomes a `SceneHandle`
  struct on 6000.3, gains `GetRawData()` and deprecates its implicit conversions on 6000.4, and on
  6000.5 those conversions are an error and the values stop fitting in an `int` at all.
  `Object.GetInstanceID()` became an error on the same editor. Five scene-handle reads and one
  unqualified `GetInstanceID()` call still used the raw APIs, so `Convai.Runtime` failed to build on
  6000.5 — and because every other Convai assembly depends on it, the whole package went down with
  it, which is the shape the failure took in the editor: a project that had imported the SDK
  reported a compile error in a file the user had never opened, and nothing Convai worked at all.

  Both APIs now go through the compatibility seams, `ConvaiSceneId` joining `ConvaiObjectId` and
  `ConvaiObjectFind` there. It reads `GetRawData()` from 6000.4 on — the first editor that offers
  it, not the first that requires it, since taking it later would mean compiling against a
  deprecated conversion and narrowing a 64-bit identity on the editor that first widened it — and
  the editor's own `int` below that, so ids are unchanged on 6000.0 through 6000.3 and agree with
  the old ones on 6000.4.

  `SceneHandle.GetHashCode()` looks like a simpler answer and is a lossy digest: the same 6000.3
  scene reports handle `-1412` and hash `64711643`. These ids are compared for equality and held as
  set members rather than used as buckets, so a digest would let two scenes read as one — and in the
  persistent-object sweep that hides a `ConvaiPlayer` or `ConvaiCharacter` from ownership
  resolution, making the room refuse to start over an agent the scene plainly contains.

  The guard test that is supposed to catch this class missed both APIs: its `GetInstanceID` rule
  required a leading dot, so a call on the component's own `this` was invisible to it, and it had no
  rule for scene handles at all. Both are fixed, and an EditMode test now rebuilds the expected id
  from Unity's own API by reflection, so a seam that starts folding or truncating the handle fails
  in the normal test run rather than in a customer's editor.

- **A character no longer watches anyone out of the corner of its eye.** The head's share of a
  look was a fraction of the angle — engagement times the state's head contribution — and the eyes
  were handed the rest, so the rest grew with the size of the look. A listener following somebody
  else's turn (engagement 0.6, head 0.7) standing 45° round was left with 26° in the eyes, the corner
  of the socket, with the head facing elsewhere; every state that looks with less than full
  commitment (Thinking, Settling, attending another speaker) did the same. The eyes now rest within
  *Eyes Get Tired Of Looking Sideways Past* and the head takes whatever lies beyond it, however
  reluctant its personality; a glance may rest its eyes further out and is drawn back to the band
  if held. A reluctant head made to turn still asks for the body, so shy characters turn their feet
  rather than crane. Two more paths to the same stare closed with it: a state that disallows the
  body turn (every listener's, by default) capped the head at the neck's comfort angle as if the
  character were walking, so a speaker 70° round met a 35° head and 20° of eye; and during the
  head's own movement the eyes flew to the end of their travel and waited there for the second
  the head took — they now lead only as far as their reach, and the gaze arrives with the head.
  Pinned by `GazeEyeRestBudgetTests`.

- The LiveKit installer restores a missing shared Android `libwebrtc.jar` even when the required
  native folders are already verified. Vendored tag `convai-v2.0.0.3` retains LiveKit Unity 2.0.0
  and livekit-ffi 0.12.69.
- WebGL disconnect reasons now preserve `JoinFailure` for protocol value 7 and `TransportError`
  for signal closure (value 9), matching the native transport.

- **A room of listeners no longer turns late, together, or to the wrong person.** Three related
  reports, one cause: the conversation layer read only the network-confirmed speech flag and reasoned
  in fixed timeouts rather than in attention that decays. Listeners turned to a new speaker a full
  round trip late and, because every listener read the same edge on the same frame, together — a
  room moving like one puppet rather than several people. Turning from character A to speak to
  character B moved a listener's expectation nowhere, so the next time A answered the listener was
  still looking at A when it should already have been on B; only the player's own speech ever
  retargeted it. And a listener that had settled on a conversation still drifted back to idle life
  and snapped back the moment somebody spoke, because attention was a linger timeout rather than a
  continuously fading value. All three are the same fix: attention is now a per-participant value
  that is refreshed by events (a speech onset, being addressed, being expected to answer) and decays
  continuously, the player's local microphone starts the reaction clock instead of the server's
  confirmation, and a change of addressee is itself an event a silent room reacts to. Pinned by
  `ConversationRoomModelTests` and `ConversationGazeDirectorTests`.

- **A listener no longer flicks away from the person it is listening to.** Four paths could move a
  character's look without anything having decided to. Somebody standing at the edge of *How Far
  Away It Still Notices* or *Widest Angle It Will Turn* crossed that line several times a second
  simply by breathing, and each crossing dropped and re-took the look; the person being attended is now
  judged on the way out with a margin (15% further, 10° wider, and a wall has to stand there for
  0.3 s rather than for one of the ten rays cast each second), while who becomes lookable in the
  first place is unchanged. While the conversation is attending somebody, no *other* character is
  offered to the target arbiter, which used to be free to hand a nearer bystander the frame on
  relevance alone — and if the attended character's target cannot be resolved for a moment, the look
  is held rather than given to whoever else is standing there. A look booked for whoever was expected
  to answer is withdrawn when the player turns to address somebody else before it lands, so the head
  no longer visits the character the conversation has already left. And the idle glances between
  people stand aside while a curiosity glance or a scripted look already owns the gaze, instead of
  interrupting it.

- **A listening character's eyes no longer roll up the instant you start talking.** The acknowledge
  nod that plays while listening used to fire on the edge that means "the player has started
  talking," not "I understood that," with a two-lobed motion strong enough that the eyes visibly
  counter-rotated to hold the face through it — which read as the eyes rolling up and back down at
  the exact moment a listener should simply be starting to pay attention. The nod is now a single
  softer dip (3°, 0.55 s), never fires on that entry edge, and the first one waits until the character
  has been listening for at least two seconds and preferably lands on a pause in what the player is
  saying.

- **A character's head no longer makes small sudden repositions while tracking a talking head or a
  moving camera.** Gaze targets were smoothed only at the actuator, so ordinary bob — a talking
  character's head bone, an FPS camera's walk sway — reached the movement detector as genuine motion
  and occasionally re-planned a head shift for a target that had not actually moved anywhere. Every
  gaze target point is now conditioned through a One-Euro filter first, so small oscillation is
  absorbed as noise while a real, sustained move still opens the filter up immediately; a teleport
  bypasses it and still snaps.

- **Characters no longer look at each other through walls.** *Won't Look Through Walls*
  (`playerLineOfSight`) gated the player and nothing else, so two characters in adjoining rooms
  followed each other's turns through the masonry: a listener turned its head, and with body turns
  allowed its whole body, toward a wall every time the character on the other side of it spoke. The
  switch now governs everyone the character might look at. Each character casts a throttled ray
  (~10/s, staggered so a room never raycasts on one frame) to every other character's head; anyone
  behind geometry on *What Counts As A Wall* is not attended when they speak, not glanced at while
  the room is quiet, not picked for an audience check or an interruption reflex, and never reaches
  the arbiter as a candidate. The diagnostics line names the gate — *"the speaker is behind a wall"*.
  Off by default, as before; the player anchor's own smoothed "lost you / found you again" behaviour
  is unchanged. This is what makes a one-character-per-room scene behave.

- **A Body Animation Config made from the Create menu now matches the one Convai ships.** Turn In
  Place and Planted Stops defaulted to on in a hand-made config while the shipped default has them
  off, so a new config walked differently from every sample with nothing in the Inspector saying
  why. Both are off by default now; tick them back on whenever the clip set carries the turn and
  stop content. The guard that compares the shipped config with a hand-made one covers every
  serialized field, so the next drift is caught before it ships.

- **The LipSync sample's Background Light stays on the background layer under URP 17.3 and later.**
  The light's rendering layer was stored only in the format older URP versions read; newer versions
  read a separate mask that the scene did not carry, so it defaulted to the character's layer and the
  high-intensity background light lit the character too. Both formats now say layer 2.

- **A character added at runtime is seated in a multi-character room like one loaded from a scene.**
  `ConvaiCharacter.CharacterId` returned null before the component had been through
  serialization, so a room membership carrying an empty id never matched it and the character was
  left without a seat. The id is now never null.

- **Idle looks no longer park the eyes in the corner of the socket.** A character with nothing to
  look at explores its surroundings, and the eye stage was given the whole exploration angle while
  the head was separately given its follow share of the same angle — so the two added up. The
  character looked past its own fixation and held its eyes near the end of their travel to do it,
  which reads as a shifty sideways stare rather than as somebody idly looking around. The eyes now
  take what the head has not, exactly as they already did for a real target, so the look lands where
  it was aimed and the eyes ease back toward centre as the head arrives. A wide idle look also
  recruits the head past its follow fraction rather than straining the eyes: people saccade to the
  edge of the socket, they do not rest there.

- **Muting your microphone left the character believing you were still talking.** Muting does not
  quieten the microphone, it stops it — no further audio frames are delivered at all — so the level
  gate that had just reported you as speaking never received the silence that would have closed it.
  Mute yourself mid-sentence and the character went on attending you until you unmuted, spoke, and
  stopped again. The gate now closes with the mute, whichever path muted it.

- **A character kept believing you were talking after it started answering.** The microphone level
  gate closes itself while the character's own voice is in the microphone, and it closed silently —
  on the reasoning that the closing word belonged to whoever suppressed it. Nothing ever said it, so
  a gate that was open when the character began its answer left everything downstream holding "the
  player is talking" until the player's *next* utterance ended, keeping the addressed character
  engaged for no reason. The gate says it itself now.

- **Push-to-talk let go of the button without saying so.** Routing the conversation to another
  character, and a disconnect or error ending the session, both cleared the press quietly — and
  since a release is refused once the press is already cleared, nothing afterwards could say it
  either. Holding the button while the conversation moved therefore left the character it moved to
  reading as though it were being addressed.

- **A character stopped hearing the player three times too slowly on some devices.** The microphone
  level gate's attack and release are authored as a number of analysis windows, but a window was a
  fixed 960 samples rather than a length of time — 20 ms on a desktop capturing at 48 kHz and 60 ms
  on a phone capturing at 16 kHz. The same authored half-second release hold therefore lasted a
  second and a half on mobile, and the character went on treating the player as talking long after
  they had stopped. The window is now measured from the rate the source already reports with every
  frame, so the timings hold on every platform this SDK ships to.

- **A character no longer glances away sharply the moment it stops speaking.** An aversion beat is
  composed downstream of the actuator, so the gaze ladder's duration law never saw it and the
  director's own ease was the only thing shaping it — at one fixed rate for every beat. A
  22-degree cognitive look-away therefore finished in about the same third of a second as a
  6-degree conversational one, travelling nearly four times faster than a deliberate look that size
  would, and landing right after the character finished a sentence it read as a flinch. The
  director is now given the head's movement law and a beat obeys it, so glancing away can never
  outrun turning to look. The eyes still take the beat as a step: they are ballistic, and easing
  them would smear one glance into a stutter of catch-up saccades.

- **A character no longer drops into its idle life for a moment after finishing a sentence.** Who
  the player is addressing is re-evaluated many times a second and moves through pending, looked-at
  and room answers, so it can flicker for a frame or two at a turn boundary. Engagement was wiped
  the instant that happened, which sent the character to *Idle* — where the player is not even a
  gaze candidate — so it looked away and snapped back a moment later. Letting go of the
  conversation is now damped by a short grace; becoming addressed stays instant, because taking a
  turn must not wait for a debounce, and a player who has genuinely walked away still cools within
  about a second rather than waiting out the idle delay.

- **A character's idle glance no longer reads as it whipping round.** Everything below the
  targeting stage had to guess what kind of look it was being asked to make, and each part guessed
  differently. The movement tempo asked "is there a target at all" — which is the same test as
  "is the character idle", written twice — so the relaxed idle tempo was unreachable for any real
  target and a glance ran at full conversational speed seconds after the idle drift it interrupted
  ran a third slower. The chest asked only whether the rig *had* a torso, so it joined every look on
  every rig in every state, and **Allow Body Turn**, which reads like the control for exactly this,
  only ever governed the feet. A look now carries what the character means by it, and a glance is
  unhurried and keeps the chest out of it.

- **The eyes were aimed a degree or two beside the player, and dead still on other characters.**
  Face scanning — moving between somebody's eyes and their mouth instead of freezing on one point —
  was switched on by whether the target was *the player* rather than by whether there was a *face*
  there. Aimed at a desktop camera it scanned an imaginary face and sat up to 2.2 degrees off the
  viewpoint the player is looking through; aimed at another character, who has an actual face, it
  did not run at all. Targets now say whether they are a face, and the player anchor has a **Face
  Presence** setting for VR, where the camera really is between the player's eyes.

- **The head returned from a look while the eyes stayed behind.** The eyes deliberately do not scale
  their aim by engagement — real eyes jump to a target rather than sweeping toward it — which is
  right while a look is being taken up. Applied to the release it produced the opposite of what eyes
  do: the head's share shrinks with engagement and unwound over most of a second while the eyes
  stayed pinned to the abandoned point, deviating *further* as the head left, because the eyes are
  the residual of the shift. The eyes now let go first and the head follows, which is the same order
  as acquiring a look.

- **Features that react to the conversation went inert unless Body Animation was also on the
  character.** Gaze, Body Language and Emotion all read the beat of the conversation — is the player
  speaking, is the character thinking, has it just been cut off — but only Body Animation ever asked
  for the component that works that beat out. A character carrying Gaze alone read `Idle` forever:
  it never committed to the player while they spoke, never broke contact to think, and never held
  the look while it talked. Body Language held a neutral posture through every conversation, with no
  lean-in, no breath before speaking and no flinch when interrupted. Nothing errored and nothing was
  logged; each feature simply never got past its first row.

  All three now ask for it through the same on-demand path Body Animation used, which announces in
  the Console what it added and why. Emotion asks only when its own dialogue-driven reactions are
  turned up from their shipped zero, so an emotional character gains nothing it did not ask for —
  but raising *Listening Reaction Strength* now does something instead of nothing.

  Body Animation's **Auto Create Conversation Flow** setting says what it does again: it governs
  whether *Body Animation* asks, and another feature that needs the conversation state still adds
  one. It also stopped describing the component as hidden, which it has not been for two releases.

- **In a room with more than one character, the player's turn could land on the wrong one.** The
  scope was decided by the character resolved at ownership time, which is the first one in scene
  order, rather than by `ConvaiManager.AddressedCharacter` — the SDK's own answer to who the player
  is talking to, and the one targeting keeps up to date. Talk to the character on the right and the
  character on the left is the one that listens, thinks and takes the turn, while the character
  being looked at sits at `Idle` for the whole conversation. Scope now follows `AddressedCharacter`.

  A character that *becomes* the addressee mid-utterance now picks the turn up from the signals
  already in flight, rather than staying silent until the next event: the player's speech and
  transcript signals are recorded whoever they are aimed at, and read through the scope at sample
  time instead of being discarded on arrival. Characters the player is not addressing are unchanged
  — they take none of the addressee's beats and go on living their ambient life.

- **Lip sync no longer works for the first answer only.** On a live room the service never sent the
  per-response statistics that close a lip-sync response, its speech-stop message arrived well after
  the voice had stopped, and the audio track kept rendering silent samples — which the fallback
  watchdog counted as progress. The finished response was therefore never retired, and every later
  response's frames were parked behind it until they were dropped: the character kept talking with a
  still mouth from the second answer on. A response whose voice has stopped and whose frames have all
  been shown is now finished on that evidence alone. The next response takes over the moment its
  frames arrive, a parked response is promoted as soon as the one in front of it has finished, a
  speech start that names no response still starts the parked one, and silence no longer counts as
  progress for the watchdog. A response whose voice is still going is never replaced, so a mid-word
  stall keeps its frames exactly as before, and a finished response is only replaced once its close
  has completed, so the mouth is never cut mid-close.

- **Interrupting a character no longer holds its mouth mid-word.** A barge-in cuts the audio with
  animation still ahead of the playhead; the service's stop follows within tens of milliseconds, but
  lip sync waited out its half-second stall timeout before fading, holding the caught shape on the
  face. A voice already gone plus the service's word is conclusive, so the close starts on the
  message.

- **A character's mouth now settles closed at the end of a sentence.** Two things made an ending
  look abrupt. The last shape was held until the audio track's silence detector confirmed the voice
  was gone — a quarter of a second measured live, an open vowel frozen on the face — even though the
  animation frames had already run out and say where a response ends. And the close itself was the
  interruption fade: every channel driven down one 0.2 s curve from a standing start, which reads as
  a mouth being shut rather than closing. The close now begins the moment the frames run out on
  settled input, and it is a settle rather than a fade — each channel a critically damped spring
  seeded with the motion the last frames had, over a new **Settle Duration** on the Lip Sync
  component (0.35 s). A jaw still rising comes back without a kink; one already closing keeps
  closing. **Fade Out Duration** still governs interruptions and cancels. For code driving
  `LipSyncPlaybackEngine` directly, `SettleToRest()` is that natural close and `IsSettling` reports
  whether the current fade-out is one; `StopSmooth()` is unchanged.

- **The speaking turn now ends on lip sync's word, not the silence detector's.** Ending on the
  voice alone has to wait out **Voice End Hold** to tell a pause between sentences from an ending,
  on top of the audio track's own half-second silence hold. Lip sync has no such ambiguity: speech
  animation arrives ahead of the sound it belongs to, so the last frame shown after the voice
  stopped is the end of the response, and frames still ahead of the playhead mean a pause however
  long the silence. On a character with Lip Sync the turn ends the frame that becomes true; Voice
  End Hold still governs characters without it. The Conversation Flow inspector's **Heard By** row
  says "Lip Sync" when that witness is on stage.

- **A talking hand is no longer left in the air when the voice stops.** The talk layer released a
  gesture by freezing the clip and dissolving its weight over 0.9 s, so a hand caught mid-stroke
  hung motionless while it faded. The clip now keeps moving and decelerates to rest exactly as the
  weight reaches zero, and the hand settles the way a person's does. The release also starts
  earlier: lip sync knows where a response ends before the sound gets there, so the hands begin to
  come down **Talk Release Lead Seconds** (0.6 s) before the end rather than after the service
  confirms it. Measured live, the last animation chunk lands 1–1.9 s before the frames run out; that
  lead was never used.

- **A character's mouth no longer opens the moment it finishes a sentence.** Lip sync hands the
  mouth back to the emotion expression when speech ends, and both used to change over the same
  0.4 s: lip sync fading out while the expression — held at a fifth of its strength during speech —
  snapped back to full. An expression with an open-jaw component (surprise ships with one) opened
  the jaw from roughly 8% to 38% right as the voice stopped, which reads as the mouth opening for no
  reason. The expression now returns over its own longer, eased **Emotion Return Duration** (1.2 s)
  on the Facial Composition Profile, so a face relaxes after talking instead of pulling a shape.

- **A character's mouth no longer hangs open after it stops talking.** When a response's audio ran
  out, lip sync held its last shape — mouth mid-vowel, frozen — until the service confirmed the turn
  was over. That confirmation is sent after the sound has already finished, so on a live session the
  mouth stayed open for roughly two and a half seconds every time the character finished a sentence.

  Holding the last shape is correct when a stream stalls mid-word: snapping the mouth shut would look
  far worse than a brief pause. It is wrong when the answer has simply ended, and nothing local told
  lip sync which of the two it was looking at — on the send-ahead path the end-of-stream notice is
  derived from the service's speech-stop message, so the visual could never get ahead of it. Lip sync
  now asks the audio instead: no voice and no animation data left is not ambiguous, and the mouth
  closes on its normal fade. A voice that returns inside that fade takes it straight back, so a brief
  gap mid-answer is not cut short.

  The fade's shape is fixed too. It eased *out*, leaving the held pose at full speed and decelerating
  into rest — a visible kink at the exact moment a viewer is looking at the mouth. It now eases both
  ends, so the mouth leaves rest gently and settles gently. Same duration, same endpoints.

- **One faulty event handler can no longer lock the conversation for the rest of the run.**
  `ConversationTargetRequested` was raised before the `try` that guards a change of target, so a
  subscriber throwing meant the `finally` never ran and the in-flight flag stayed set forever. From
  then on the switch policy answered `HeldForPendingCommand`, availability sat at `Preparing`, and
  the player could not say another word — with nothing in the Console tying it to the handler that
  threw. All three conversation events now go out through the same safe invoker every other event
  surface in the SDK uses: a throwing handler is logged with its own name and the conversation
  carries on. A change of target that is in flight when the room disconnects is dropped too, rather
  than waiting for an answer that is not coming.

- **A scripted `TalkTo` no longer reopens the window it just closed.** Two changes of target can be
  in flight at once — a `TalkTo` from a dialogue menu landing on top of an automatic switch — and
  the state that closes the send window was a single flag, so whichever round trip returned first
  cleared it. The newer change was still on its way to the service, but the field reopened and named
  a character the message would not reach: exactly the gap the pending target exists to close, and
  it opened only when a game drove the conversation itself. Each change now carries a generation and
  only the newest one may release the state.

- **A character that is not in the room no longer reports that the player can talk to it.** In a room
  that keeps a roster, `ConvaiCharacter.ConversationAvailability` asked the roster what it thought of
  that character — and a character with no seat in the roster has no answer there. The missing answer
  was read as "this room keeps no roster at all", which sent the question back to the character's own
  readiness flag; that flag is set by the readiness recovery and survives a previous connection, so a
  character left out of the room could report `Ready`. Everything downstream believed it: the chat
  field opened, the prompt named the character, and the message went nowhere. The absence of a roster
  and the absence of a seat in one are now separate facts, and a character without a seat reports
  `Unavailable`.

- **A room with two characters sharing one Character ID is refused instead of half-working.**
  Duplicating a working character is the quickest way to get a second one and the copy arrives holding
  the original's ID, which the SDK passed straight through to the service. It cannot work: ownership,
  participant bindings and audio sources are all keyed by Character ID, so the second character never
  gets routing of its own and the room answers both through the first — a voice from the wrong mouth,
  which reads as an audio fault rather than as the field two lines up in the Inspector. Both paths
  into a roster now refuse it and name the two GameObjects, the ID, and where to get another one.

- **Awaiting the SDK no longer resumes your code inside one of its locks.** Two signals — the
  character-ready signal behind `StartConversationAsync` and `WaitForCharacterReadyAsync`, and the
  connection-resolution signal behind the room connection sequence — were completed while the lock
  guarding them was held, and a task source completed that way runs everything waiting on it right
  there. So the line after your `await`, the SDK's own startup bookkeeping, and the remainder of
  connecting all ran inside those critical sections, on whichever thread delivered the event.
  Nothing was wrong with the code that ran; the problem was where it ran, because a lock held across
  a call into unknown code is a lock the SDK no longer controls: any other thread calling the same
  API waits behind that code, and a continuation that in turn waits on such a call deadlocks with
  nothing to see from the outside. Both signals now schedule their continuations instead of running
  them inline, and a test holds each one to it.

  Nothing changes for callers that await on the main thread: they still resume on that same thread,
  one dispatch later, and every state flag — `IsCharacterReady`, `IsInConversation`,
  `ConversationAvailability` — is already set before the signal completes, exactly as before.

- **A webcam vision source torn down outside Play Mode no longer logs an error and leaks its
  texture.** `WebcamVisionFrameSource` released its webcam and render textures with `Destroy()`,
  which Unity refuses outside Play Mode, so stopping a source in the editor printed "Destroy may not
  be called from edit mode" and left the texture alive. It now picks `DestroyImmediate` outside Play
  Mode, matching `VisionDebugPreview` and the embodiment profiles.

- **The character that opened the room can now leave it.** Destroying it, removing it from room
  ownership, or calling `RemoveCharacterAsync` gives up its seat — except, until now, for the one
  character the room was opened on, which could never leave. (Disabling a character only makes it
  ineligible to be addressed; it deliberately keeps its seat.) The symptom was worse than a stale
  name in a roster: the departed character kept the conversation, so
  `ConvaiManager.AddressedCharacter` named somebody who was no longer in the scene, availability
  reported `Ready`, and the shipped chat field invited the player to message it. The only report was
  one Console line at `Debug` verbosity.

  It went further than that: once the room's original starting character had left, **no** later
  roster edit could be applied either, so restoring that character to room ownership did nothing at
  all. The room was stuck with whatever roster it happened to have.

  The cause was a guard reading the wrong thing. The live roster path refuses an edit when the
  starting character changes, because choosing a different one decides how the room is *created*
  rather than who is in it. But a room with no assigned **Initial Character** derives its starting
  character from scene order, and that answer moves whenever the roster moves — so the guard fired
  on the very edit that moved it, and once the two disagreed they could never agree again. Only an
  assigned Initial Character is a decision; `LiveRosterPlanner` now guards on that alone, which is
  what this changelog and `MULTI-CHARACTER.md` always said it did. An assigned Initial Character
  that is disabled does not resolve, so it cannot trigger this by leaving either.

  The removal now also names a replacement, so the conversation is handed to a remaining ready
  character in the same edit rather than the room being left addressing nobody.

- **The prompt names the character the player is looking at while that character is still
  joining.** It already did this before the room came up — look at James during the first connect
  and the field says "Connecting to James…" — and then stopped doing it, so a character rejoining a
  live room got no acknowledgement at all: the player watched James arrive while the field named
  somebody else. It is the same question both times and now gets the same answer.

  Narrow on purpose. The look-at answer only outranks the room's while that character has a
  membership the service has not announced, because a character that cannot receive anything cannot
  be the wrong destination either — the prompt names it, the field closes, and `TrySendTextMessage`
  refuses, all three agreeing. A character that *can* hear is never named early, because until
  targeting moves the conversation the message would still reach somebody else, and that is the one
  thing this surface exists to prevent. Targeting moves within Switch Delay.

  **Behaviour change:** for the second or two a character takes to join, the player cannot message
  whoever they were talking to while they look at the joining one. Look back and the field reopens.

- **`ConvaiCharacter.RoomMembershipStatus`** reports what the room says about one character —
  `Starting`, `Ready`, `Failed`, or `null` when it is not in a room that keeps a roster. This is
  what a "joining…" indicator over a character should read, and the `null` is what stops that
  indicator appearing over a character that is not in the room at all.

- **A refused message says why, where the player is looking.** The refusal reason went to the
  Console only. That was survivable while the prompt could speak — but a refusal deliberately keeps
  the player's text in the field, and their text hides the prompt, so the field greyed out with
  their own sentence in it and nothing anywhere saying why. Assign **Refusal Notice Text** on
  `ChatTranscriptUI` and the reason appears there for **Refusal Notice Seconds** (default 4, 0 keeps
  it Console-only), then clears itself. Leave it empty and nothing changes.

  A second label mirroring the whole availability state was tried and removed before shipping: the
  prompt already carries it, so the two agreed word for word in the states that occur most, and "the
  character is speaking" is something the arriving message says better than a label can.

  `Gate Input On Availability` is unchanged, but its tooltip was describing the world before
  `ConvaiPlayer` refused early messages itself. Turning it off is presentation only — the send is
  still refused, the player just finds out by being refused rather than by the field being closed.

- **A message sent while the conversation is moving is no longer answered by nobody.** Changing
  which character is addressed is a round trip — measured at 1.1 seconds — and during it the room
  still reports the previous character as active while the service is already ending that
  character's turn. Anything typed in that window landed between the two. The window is now part of
  the availability verdict: `AddressedCharacter` names the character being moved *to*, the field
  closes, and `TrySendTextMessage` refuses with a reason instead of the message vanishing.

- **Disabling a character no longer takes it out of the room.** It keeps its seat and stops being
  addressable; enabling it again is immediate, because nothing had to happen in between. Every
  component toggle used to become a membership mutation — a new bot actor, a new participant
  identity, and a round trip that could time out — which is where a long tail of intermittent
  faults came from: the character rejoined with a new identity its audio could not be routed to, or
  the add was never acknowledged at all.

  `SetActive(false)` is how Unity says "behind a wall", "pooled" and "hidden for a beat" as much as
  "gone", and none of those are a reason to give up a seat that costs nothing to keep — a room
  already holds every member whether or not it is the one being addressed. A character now leaves
  when the project says so: destroyed, dropped from ownership, or taken out of **Include in Next
  Room**. `ConvaiRoomManager.RemoveCharacterAsync` still empties a seat deliberately.

  **Behaviour change:** a character disabled for a long time keeps its place in the room, and so
  keeps counting toward the plan's character limit. Use Include in Next Room or
  `RemoveCharacterAsync` when the seat itself needs freeing.

  The conversation no longer sits on a character that was disabled while holding it: with somebody
  else able to answer, it moves to them even if the player is not looking at anybody.

- **A character disabled and re-enabled quickly no longer waits for a reconnect.** Whether it
  rejoined at once or not at all depended on how fast you were, which is the worst kind of
  intermittent. A roster edit is a round trip; enabling the character again before the removal came
  back meant the change was judged against a roster the service had not updated yet, so it read as
  no change and was dropped — and nothing asked again afterwards, because ownership had already
  finished moving. The roster is now re-checked after each edit that lands, which converges: when
  it already matches, the planner answers `NoRosterChange` and nothing is sent. Only after an edit
  that succeeded, so a refusal is not retried in a loop.

- **A character leaving a live room now names a replacement every time.** The SDK only offered one
  when its own copy of the active membership matched the character being removed. The service
  decides that from *its* record, the two can differ, and when they did the removal came back
  refused with `replacement_target_required` — the character stayed in the roster, kept the
  conversation, and the Console said only "Roster update rejected". Offering a replacement that
  turns out to be unnecessary costs nothing, because the service reads the field only when it is
  removing the active membership. So the client stops guessing and lets the side that knows decide.

- **A refused roster edit says which refusal it was.** The service answers with a machine-readable
  code — `stale_roster_epoch`, `membership_not_found`, `roster_capacity_exceeded` and the rest — and
  a message that is the same sentence every time. The SDK carried the code all the way to
  `CharacterRosterUpdateException.Code` and then logged only the message, so every refusal read
  "Roster update rejected": the outcome named, the cause hidden. The code is now in the Console line
  and in `RoomRosterChanged.Reason`.

- **Audio routing no longer depends on which component wakes up first.** A character re-enabled
  during play re-subscribes its audio track from inside `ConvaiCharacter.OnEnable`, and Unity may run
  that before `ConvaiAudioOutput.OnEnable` has re-registered the `AudioSource` — so the track arrived
  with nowhere to play and the character answered in text and stayed silent. Registering on enable
  was necessary and not sufficient; the track manager now falls back to asking the character for its
  own `AudioSource`, which cannot be out of order because it is not a registration.

- **A character keeps its voice after being disabled and enabled again.** The registration that
  tells the audio track manager which `AudioSource` belongs to a character is given up in
  `OnDisable`. Putting it back happened only as a side effect of resolving dependencies for the
  first time, and that resolution returns early once they are cached — so a second enable never
  restored it. The character then rejoined its room, answered in text, and stayed silent, while lip
  sync waited for audio that was never going to arrive and discarded the animation it had buffered.
  The only report was one Console error naming a character ID.

  This was never reachable before, because a character that left a room could not rejoin it. It is
  not specific to multi-character rooms: any character disabled and re-enabled during play lost its
  voice the same way. Covered by a Play Mode test, since `OnEnable` and `OnDisable` are the whole
  defect and Edit Mode runs neither.

- **A character that is not in the scene can no longer be reported as available.** A roster edit is
  a round trip, so between a character being disabled and the service acknowledging it the
  membership still reads `Ready`. `ConvaiCharacter.ConversationAvailability` now answers for the
  scene first, which is the half the room can be wrong about.

- **The Convai Manager roster names a member whose character is gone.** A member that is disabled or
  destroyed read identically to a healthy one, which is what made the defect above invisible for as
  long as it was.

- **A refused message no longer clears the chat field for a custom player agent.** The shipped chat
  UI keeps the typed sentence when a send is refused, because retyping a sentence the game swallowed
  is the part players resent — but that only worked for `ConvaiPlayer`. Any other implementation of
  `IConvaiPlayerAgent` went through a void send that cannot report a refusal, and the field was
  cleared regardless. The UI now asks the manager's verdict directly on that path, which is the same
  answer `ConvaiPlayer` consults.

- **Aim points for characters that have left the room are no longer retained.** The conversation
  targeting cache is keyed by character and never read again once that character is destroyed, but
  the entry and its resolved renderer array stayed alive — one per character that had ever left, in
  exactly the rooms whose rosters change during play.

- **A character is no longer announced as ready before the service says so.** Evidence that a
  character was alive — a remote audio track, a speech chunk, lip-sync data — was treated as proof
  of readiness and applied immediately. That recovery exists for a readiness signal that never
  arrives, but it could not tell *late* from *missing*, and for a character joining a room that is
  already connected the audio track always precedes the signal. So the SDK announced characters the
  service was not routing to yet, measured once at 1.44 seconds early, and anything said in that
  window went nowhere.

  Evidence is now weighed rather than counted. A character that is producing speech is ready
  whatever the handshake has managed to say, so speech, lip-sync frames and a speech-start recover
  it on the spot — holding a character that the player can hear talking in "getting ready" would be
  the same lie pointed the other way. A subscribed audio track only means a participant joined,
  which happens before readiness on every normal connection, so on its own it waits. That split is
  what makes this correct rather than a timeout tuned against one measurement, and it leaves the
  wait governing only the narrow case where the service never announced a character *and* that
  character never speaks. The Console says so if it ever fires.

- **Enabling a character during play no longer fails its own connection.** Ownership injects
  characters that start the scene disabled, so enabling one ran its initialization — and its
  auto-connect — immediately, while it was still absent from the connectable roster, which is
  decided by activity rather than ownership. The connection was then composed from a roster the
  character was not in, resolved no conversation target, and was refused: *"ConnectAsync aborted
  because connection preparation failed"*, naming the very character that had just appeared.
  Auto-connect now waits for the manager to fold the character into ownership first.

- **A moving target is followed with the head, continuously — not in installments.** Watching
  someone strafe around the character, walk past it, or drift at talking distance, the head only
  ever came to rest at discrete orientations: it parked, waited for the error to build, and
  repositioned in a burst, so a walk-by read as the head clicking through invisible detents.
  Measured on the new pursuit traces: a walker being tracked at 35°/s produced a 96°/s whip to
  centre, a 0.38-second dead park while the eyes carried over 10° of moving eccentricity alone,
  and a second 94°/s whip out of it.

  Two causes, both structural. Head recruitment — "is this look big enough for the head to join" —
  was re-evaluated every frame from the live angle, so a target crossing the character's forward
  axis stood the head down at exactly the moment it should have been carrying through, and the
  recruitment ramp's own sweep added unauthored velocity on both sides of it. And the actuator
  knew only two verbs — a shaped movement that lands at rest, and a hold behind a stability
  dead-band — so sustained target motion was chopped into stick–slip between them. Recruitment is
  now decided per look and latched (it cannot be revoked by the target moving through centre), and
  sustained goal motion puts the head in a pursuit mode: the dead-band stands aside, the tracking
  filter's feedforward carries the head at the target's own pace, and quiescence re-arms the hold
  where the goal came to rest. Movement detection is also measured against the goal's own recent
  velocity rather than a fixed per-frame delta, so the classification no longer degrades with
  frame rate. After the fix the same traces show zero parked holds at conversational orbit speeds
  and a peak head speed equal to the target's own rate — the head never moves faster than the
  thing it is following. `Tests/EditMode/Gaze/GazePursuitTraceTests.cs` pins all four scenarios.

- **A character answering with the same words you typed no longer loses its text.** Typing a line
  arms a guard so the service mirroring that line back is not shown twice. The guard was released
  only in half — the speech-cycle half — when the typed line came back as the player's own
  transcript, and its other half stayed armed for the rest of a three-second window, throwing away
  any character text that matched. Measured over a session: the player's line was accounted for, and
  the character's reply was discarded 1.2 to 1.6 seconds later, which is the time it had spent
  generating one.

  It only ever bit when the whole reply matched, so `"Hey."` in answer to `"hey"` vanished while
  `"Hey — good to see somebody."` came through, and the audio played either way. Greetings collide
  like this constantly. The acknowledgement now retires the guard completely: once a typed line has
  come back as the player's own transcript it has been accounted for, and what the character says
  next is the character's.

- **`ConvaiCharacter.OnTranscriptReceived` never fired, in any room.** It read `bot-tts-text`, and
  the service does not send that message — measured over a session that produced 40 character
  transcript messages and not one of them. Everything downstream of it sat idle: gaze referential
  glances, and any `IConvaiCharacterBehavior` reacting to what a character said. Body Animation's
  referential gestures survived only because they also listen to the room transcript feed.

  The event now reads that same feed, which is the one that arrives. This also settles the other
  half: `bot-tts-text` carries no notion of a final chunk, so the event's `isFinal` argument was
  hard-coded to `false` and could not have been answered truthfully from it whatever the wire did.
  It now comes from the message's own lifecycle, so a settled line reports `true` and a streaming
  one reports `false`. Consumers gating on the final utterance work for the first time.

  Two consequences worth knowing. Text now arrives per sentence rather than per synthesis chunk,
  and a line that streams before it settles is delivered twice — once interim, once final — which
  is what the `isFinal` argument is for.

- **A room that refuses the second character now says that is what happened.** Multi-character rooms
  are an account feature, and an account without it answers the connect request with a token only the
  service can read. The SDK reported it as "realtime access is not allowed", which points at a
  different setting on a different page, and the symptom itself is already misleading: the scene
  connected a moment ago with one character and stops connecting with two, including the character
  that worked. The failure now names the account feature, and the plan's roster limit separately, and
  says how to get connecting again in the meantime. New session error codes
  `connection.connect_multi_character_not_allowed` and
  `connection.connect_multi_character_roster_limit` carry the distinction to code that reads them.

- **A player that survives scene loads is no longer invisible to the SDK.** Persisting the player rig
  with `DontDestroyOnLoad` is an ordinary Unity pattern — Unity's own URP sample player does it from
  `Awake` — and the object leaves its scene before the room starts. Ownership resolution enumerated
  the loaded scenes and nothing else, so the rig was simply gone, and the room refused to start with
  *"Failed to resolve an owned player before room startup."* The scene plainly contained a
  `ConvaiPlayer`, which made the message read as a lie and sent people looking for a missing
  component. `DontDestroyOnLoad` is now part of what ownership resolution looks at, for characters as
  well as players. Edit Mode discovery is untouched, so prefab-stage scenes still stay out of it.

- **Two characters in a scene now connect.** Adding a second `ConvaiCharacter` needs no hidden manager
  state — that was already true of character discovery — but the room then refused to start at all
  because no *Initial Character* said which one speaks first. Adding a character to a working scene
  broke it, and the only sign was one Console line. The first character in scene order now takes the
  first turn, and the SDK says which one it picked, once, so the choice is visible rather than silent.
  Assign **Convai Manager > Initial Character** to start on a different character; assigning it
  suppresses the message. The Inspector and `Convai.DiagnoseConversation` now name the same starting
  character the room will actually use, and report it as information rather than as an error.

- **The head turned further one way than the other.** Gaze took the head's and eyes' "straight
  ahead" from the pose the bones were in at bind time, and a rebind — Body Animation building its
  runtime rig — happens after the Animator has already posed the character, so whatever the idle
  clip was doing at that instant became a permanent bias. Measured on the sample character as a
  2–21° offset that changed from one Play session to the next: the head resting off centre with the
  camera dead ahead, one side reaching its limit early and the other late (a camera at ±55° drove the
  head to 32° one way and 63° the other), and the eyes falling short on one side. The rest
  orientation now comes from the rig's authored rest pose — the humanoid avatar's skeleton — rather
  than whatever pose animation happened to be in at bind time. Measured after: a ±60° sweep now
  reaches 57.7° / −57.9°, a ±90° sweep reaches 74.0° / −73.4°, and the camera dead ahead is matched
  within 1°. A rig without a humanoid avatar still takes its rest from the live pose at bind time, so
  on such a rig bind before animation has posed the character.

- **Off-target reading on rigs with per-eye axes.** On a rig whose left and right eye bones have
  different authored forward axes, the Gaze inspector's off-target figure composed each eye against
  the shared rest reference and counted the per-eye offset twice, so eyes that were genuinely on the
  target read as a few degrees off. Each eye is now measured against its own reference — the one it
  is actually written against.

- **A listener turning to whoever has just been given the floor no longer moves like a startle.**
  When somebody talks over the person holding the floor, a listener's eyes go there first and its
  head waits for the room's verdict — and when the verdict arrives and the floor moves, that listener
  is doing the most ordinary thing in a conversation: turning to whoever is talking. It was being
  issued as the same reflex the flick started as, so it ran at the tempo reserved for startles and
  camera cuts. Measured at 103–151°/s across sweeps of about 100° in a room of three, against
  conversational movement of well under half that. The catch-up is now attention, at ordinary tempo,
  keeping the eyes-ahead shape it inherited; only a genuine interrupter — somebody the room has *not*
  given the floor — still moves at a reflex's speed.

- **A head no longer goes fifty degrees out and comes straight back at the end of a turn.** In a
  three-character room, where any two people are the better part of a right angle apart, a character
  would put its head most of the way round to somebody and take it back inside three quarters of a
  second, at 110–150°/s. Three things produced it, and each is fixed where it was caused.

  The person the room expects to answer used to keep the attention they carry as the addressee only
  while somebody actually held the floor — so on the single tick between the question ending and the
  answer starting, the support was withdrawn, their value began to fade, and the speaker who had just
  stopped was still warm enough to hold the look through the switching margin; the head then went to
  the answerer anyway a moment later when their own raise landed. Two movements where the room only
  ever expressed one intention. That support now spans the hand-off, keyed to the raise it is
  bridging to rather than to a clock, so it ends when that raise lands or when the conversation stops
  expecting anybody — a room nobody answers in still runs out of people worth looking at.

  Every look that is short by design — a listener checking the person being spoken to, a speaker
  glancing round its audience, an idle glance between people, noticing somebody arrive — now asks the
  head for the same small share and is issued as a glance rather than as attention. Half a second of
  full head commitment is not a glance; it is a turn made and unmade inside a second.

  Finally, the look no longer follows a lead that opened by itself. A speaker pausing for breath
  fades past the person they are talking to, who is pinned at the addressee's share, so one overtakes
  the other with nothing whatever having happened — and the speaker's next word takes the look
  straight back. Such a lead must now stand for about as long as a look lasts before the look follows
  it. Anything that actually happens — a speech onset, the floor changing hands, somebody walking in
  — still moves the look on the frame it happens.

- **Two listeners no longer turn to the same face together when they got there different ways.** The
  room keeps any two listeners' reactions apart, but it was doing so per *cause*: noticing somebody
  start talking, following the floor as it moved to them, flicking to them as an interrupter and
  being handed them as the expected answerer were four separate schedules, and only listeners sharing
  one of them were spaced. Every pair of causes was therefore free to collide, which in a room of
  three was measured at seven to nine pairs of heads arriving within 0.0–0.4 s of each other against
  a minimum gap of 0.35 s. What a moment is now spaced against is *who it lands on*, so two faces
  arriving on a third are always the authored gap apart however differently they were moved — which
  matters because a viewer cannot see the cause, only the two heads.

- **An idle glance at somebody across the room no longer parks the eyes in the corner of the socket.**
  A character standing by would glance at another 60–66° round, hold 31° of eye deviation for one and
  a half to two and a half seconds, and barely move its head. Two rules were each right on their own
  and wrong together. A glance is allowed to rest its eyes further out than a committed look, because
  the eyes are running ahead of a movement that finishes — but a glance never brings the body round,
  so for one that could only be reached by turning there is no movement to run ahead of, and whatever
  the eyes are handed at the start they hold for the whole beat. And a glance was aimed by the same
  reach test that decides what a character is *allowed* to look at, which is a different question
  from what it can look at with its eyes and a comfortable neck.

  A glance at somebody further round than the neck's comfortable travel plus the band the eyes may
  rest in is now refused outright — at that angle there is no glance to make. One inside that reach
  is held to the same eye comfort band as a look, so the head takes the rest and the eyes come back
  to centre once the glance has settled.

- **A body turn that is re-aimed mid-flight no longer snaps the last of the way round.** When the
  character was turning to face someone and the target moved while the turn was in the air — the
  player walking behind them, a camera cut to the other side — the body could cover the whole
  remaining angle in a single frame and stop dead. Measured at 28.5° between two frames, about
  1880°/s, against a turn authored to peak at 140°/s. The turn was planned as one movement over a
  duration chosen from how far there was to go, and a re-aim can leave far more angle to cover than
  that clock has time for — most of all when the new target is on the other side and the body has to
  reverse. The movement then ended the only way it could: by putting the character on the goal. Which
  is why nothing about the final pose ever looked wrong, and only the frame before it did.

  A residual still worth moving is now re-planned as its own movement, launched from the rate the
  body is already turning at and given the time that size of turn deserves, so the turn finishes with
  the same shaped motion it started with. A residual small enough to stop inside still settles the way
  it always did. Underneath both, a hard envelope now bounds how far any single frame may rotate the
  body — the peak rate a turn at the authored speed actually reaches — so no path through the turn
  can put a rate no body could produce on screen. Pinned by `ProceduralReorientationRetargetTests`,
  which measures the per-frame step rather than the pose: a test that only checked where the
  character ended up could not see this at all.

- **A conversation state that cannot make up its mind no longer moves the character.** At the end of
  every utterance the dialogue state handed to gaze alternates between Speaking and Settling on
  consecutive frames — measured at 15 ms in play mode, for 0.2–0.3 s at a normal boundary and for
  seven seconds during one long answer. Gaze read that state twice a frame and acted on every edge,
  and the two state rows disagree about nearly everything: engagement, how much of the look the head
  takes, whether the body may turn, and which aversion cadence is running. So the character re-planned
  its head twice a frame, switched its aversion mode on and off, retired and restarted its speaking
  turn sixty times a second, and — before the reluctant-turn fix below — abandoned any body turn that
  happened to be in flight. All of it at the exact moment the character is supposed to be coming to
  rest. Gaze now reads the state once per frame, through a single seam, and adopts a new one only
  after it has held for 0.12 s; cognition and expression can no longer act on different states within
  the same frame. `Interrupted` and `Reacting` are exempt and land on the frame they arrive, because a
  startle that is an eighth of a second late is not a startle. A genuine transition is at most 0.12 s
  later than before, which is under the module's own movement onsets. Pinned by
  `DialogueStateDebounceTests` and `GazeDialogueStateSeamTests`, the latter carrying a negative
  control that requires the same square wave read raw to still break the invariants.

- **A character that would rather not turn its body no longer stares out of the corner of its eye.**
  *May turn its body* on a dialogue state row was read as a ban, and the state rows that ship with it
  off — Thinking, Settling, Idle — are exactly the ones a conversation spends time in. With the
  player 95° round, nothing downstream could clamp the head: the rung whose whole job is to absorb
  what the head and chest cannot take was switched off, so the head saturated at its limit and the
  entire unreachable remainder landed in the eyes, 31° at the corner of the socket, held for as long
  as the state lasted. The flag is now reluctance rather than refusal. It still declines both turns
  that are a matter of taste — the comfort turn a held neck asks for, and the ordinary turn for an
  angle the eyes could have carried — and it no longer survives a target the head and eyes cannot
  reach between them, which is measured against the ceiling: what would still be left over if the
  head and chest both committed as far as they physically go, against the band the eyes may rest in.
  Such a target turns the body after about twice the usual delay, because reluctance shows as
  hesitation, not as a different turn. A turn already in flight is no longer cancelled by the state
  changing underneath it, which used to leave the character facing halfway between where it was and
  where it was going. Only *Turns Its Body* on the profile, and a character that is actually walking,
  stop the turn outright — walking still keeps the head inside the neck's comfort angle, because its
  legs are about to do the turning. Pinned by `GazeReluctantBodyTurnTests`.

- **The first look after a character binds is a movement, not a jump.** The bind frame is the one
  frame with no previous head share to compare against, so it reported "nothing moved" and
  initialised the channel instead — and an un-seeded tracking filter puts its whole goal on in a
  single frame. A character whose idle life had already chosen a fixation therefore snapped its head
  onto its share of it at 613 deg/s the instant gaze started, which is the first thing a player sees.
  The bind frame now asks the question it can actually answer — is this share where the head already
  is? — so a rig that binds holding its look is still initialised and never re-runs the turn it is at
  the end of, while a share the head is not at is a decision like any other and is shaped by the
  ballistic lane from the pose the animation left. Pinned by `GazeFirstFixationTests`.

- **An idle animation that turns the head no longer drags the eyes the other way.** Idle life aims in
  the character's own frame and hands the eyes whatever part of the fixation the head has not taken —
  measured as where the head bone actually points, which is gaze's own share *plus* everything the
  animation is doing. So an idle clip that turned the head 30° to look around was read as the head
  having taken 30° of a fixation that was dead ahead, and the eyes swung 30° the other way and stayed
  there: the character looked around with its head while its eyes stayed locked on what it had turned
  away from, at the corner of the socket, for the length of the fixation. Nothing was engaged, so
  there was no world point to hold and nothing to correct it — the reflex that holds a look while the
  head turns is deliberately off without a target. The head stage now reports what the animation
  alone is doing, sampled at the one point in the frame where that is unambiguous, and idle life
  subtracts only gaze's own share: an authored head turn carries the eyes with it, while the head's
  share of an idle look is still given up as the head takes it. Looking at something is unchanged —
  a head turning under an engaged look must not take the gaze off it, and does not. Pinned by
  `GazeIdleAnimatedHeadTests`.

- **A look that forbids a body turn is not overruled by one that is beyond reach.** A dialogue state
  that would rather not turn is a preference the actuator ladder may overrule for a target the head
  and eyes cannot reach between them (the reluctant turn above). A scripted look whose options say
  `AllowBodyTurn = false`, and a glance, were being read through the same flag, so a target behind
  the character turned the body after the reluctant delay despite the request forbidding it. The
  directive now carries the prohibition separately, and the ladder never puts the feet on for it.

- **The fallback body turn's per-frame envelope is the authored speed, not 1.875x it.** The turn's
  clock is already stretched so that the minimum-jerk profile peaks at *Turn Speed (Rigs Without
  Turn Animation)*, so the safety envelope that bounds a single frame's rotation was allowing
  1.875 times that peak — 168°/s against the default 90°/s — on exactly the landing frames it exists
  to catch. It now caps at the authored speed; `ProceduralReorientationRetargetTests` measures
  against that bound.

- **A fast, steady target at a low trigger and a modest frame rate no longer restarts the head's
  movement every frame.** With *New Look Starts Above* at its 0.5° minimum and 30 frames per second, a
  55°/s sweep clears even the in-flight re-trigger bar on every frame, and the goal-velocity estimate
  — which deliberately ignores a trigger frame, because a step is a decision rather than motion —
  could then never learn the sweep: the ballistic lane was re-begun each frame and the head pinned
  in its opening phase until the target slowed. A trigger that fires on two consecutive frames is
  now folded into the estimate, which converges within a few frames and lets the pursuit lane take
  over.

- **A withdrawn reaction no longer delays the one that replaces it.** The room spaces any two
  listeners' reactions to the same person apart by the authored gap, and it keeps that book per
  person now rather than per event. A booking the director withdrew — the expected responder when
  the turn moved on, a social glance cleared, a participant who left — stayed in the room's book, so
  the next reaction aimed at the same person was spaced against a ghost: measured as the hand-off
  look landing up to one full gap (0.35 s) late. Every path that drops a booking now gives it back to
  the room. Pinned by `ConversationRoomModelTests` and `ConversationGazeDirectorTests`, with a
  negative control that shows the un-released ghost still delays.

### Breaking Changes

- `DisconnectReason.RemoteHangUp` is removed. Native and WebGL transports now report
  `DuplicateIdentity`, `ServerShutdown`, `ParticipantRemoved`, `RoomDeleted`, `JoinFailure`,
  `TransportError` or `Unknown`, so a handler can tell a server shutdown from a network drop.
- The vendored LiveKit editor assembly is named `LiveKit.Editor` (was `Editor`). LiveKit's
  optional UniTask bridge is not vendored.

- The Gaze profile's Conversation Attention fields **Shortest/Longest Delay Before Turning**, **Stays
  With The Conversation For**, and **Looks To Whoever Answers Next** are removed — attention now
  decays continuously rather than running on a linger timeout and a hand-off probability, so there is
  nothing left for them to tune. Their replacements are **Typical Pause Before Turning**, **How Much
  Reactions Vary**, **How Long Attention Fades Over**, and **Shortest Gap Between Listeners Turning**.
  `ConvaiGazeProfile.SpeakerAttentionReactionDelayMin/Max`, `SpeakerAttentionLingerSeconds`, and
  `SpeakerAttentionHandOffGlanceChance` are removed from the public API with them.

- `ConvaiManager.SetExplicitConversationTarget` is renamed to
  **`ConvaiManager.SetInitialCharacter`**. The old name is still callable and marked `[Obsolete]`.

  The rename exists because the old name described the wrong thing and cost people real time: it
  reads like the verb for *change who the player is talking to*, and that is what it was reached
  for, but it chooses which character speaks **first** and calling it on a connected room queues an
  ownership reconnect rather than moving the conversation. Two different things were called the
  conversation target and the discoverable one was the wrong one.

- The Gaze profile's **Ignore Small Target Movement** (`headStabilityDegrees`) is removed — the head
  and chest following lane no longer has a dead band to size. In its place: **How Quickly The Head
  Follows Movement** (`HeadFollowSeconds`, default 0.2 s) and **How Quickly The Chest Follows
  Movement** (`TorsoFollowSeconds`, default 0.35 s), both on **Head & Body**.

- The Gaze profile's **Neck Relaxes During A Turn** (`bodyTurnHeadRelief`) default changed from
  **0.4 to 1**, and **Chest Speed Limit** (`maxTorsoAngularSpeed`) default changed from **180°/s to
  90°/s**. Existing profiles keep their saved values; only new profiles pick up the new defaults.

### Migration Notes

- A profile carrying the removed Conversation Attention fields loses them silently on next save —
  re-apply a personality, or leave the defaults, to pick up the new attention model's tuning.

- Replace comparisons against `DisconnectReason.RemoteHangUp` with the specific reason you care
  about, or with `reason != DisconnectReason.ClientInitiated` for "anything unexpected", which is
  what the SDK's own cleanup uses.
- Any asmdef that referenced the LiveKit editor assembly as `Editor` must reference
  `LiveKit.Editor`.

- Replace `SetExplicitConversationTarget(character)` with `SetInitialCharacter(character)` — same
  behaviour, same signature. If the call was meant to switch the conversation in a running room,
  it was not doing that; use `TalkTo(character)` instead.

- A profile carrying the removed **Ignore Small Target Movement** ignores it from the moment it loads
  and drops it on its next save — it had no value worth carrying over, since the dead band it sized
  was the defect. If a fast target
  trails the head more than you want, lower **How Quickly The Head Follows Movement**.

- To pick up the new head/turn defaults on an existing profile, set **Neck Relaxes During A
  Turn** to 1 and **Chest Speed Limit** to 90°/s by hand — saved profiles are not migrated.

## [4.5.0] - 2026-08-14

### Feature Additions

- Added four production-ready built-in Action executors: **Lead Player To Target**, **Scan Environment**,
  **Count Target Group**, and **Measure Distance**. Actions added from the Actions Editor now create
  and bind their executor, preserve an executor type fallback, and provision deterministic
  character-side component requirements in one undoable operation.
- Removed the unpublished experimental **Guided Tour**, **Address The Group**, and **Perform Gesture
  At Target** executors from the public Action catalog.
- Reworked the Actions Editor's first-run experience around four representative starters: **Walk To
  Target**, **Follow The Player**, **Look At Target**, and **Play Gesture**. The Add Action catalog now
  shows curated recommendations first, keeps remaining built-ins alphabetical, and moves sample and
  project/package archetypes into dedicated submenus.

- Added project-level API Key and Auth Token authentication modes. Auth Token mode resolves a fresh short-lived credential from a configured backend endpoint or `IConvaiAuthTokenProvider` before each room connection, supports Native and WebGL transports, and strips the saved account API key from player builds while retaining it for Editor tooling.
- Added `ConvaiManager.ConnectWithAuthTokenAsync` for developers whose login or networking layer already has a Convai auth token. The one-shot call also supplies `end_user_id` and `end_user_metadata.name` without registering a token provider or configuring an endpoint.
- **A point can now be as short as it looks like it should be.** *Point At Target* offered one
  setting, *Hold Seconds*, and it does not control how long the gesture takes — it is the pause in
  the middle of it. The arm's rise and fall belong to the animation, and the shipped pointing clips
  put the apex halfway through five seconds, so a one-second hold still produced a six-second point
  and there was no way to shorten it. Two settings now reach the pointing layer, which has supported
  both all along: **Gesture Speed** multiplies the rise and the fall, and **Release** set to `Blend`
  drops the pose out when the hold ends instead of playing the arm back down. Both default to the
  previous behaviour, so no existing scene changes; a point of about a second is Speed 1.5 with
  Release `Blend`. *Hold Seconds* keeps its meaning and says so in its tooltip now, because the name
  reads like a duration for the whole gesture and it never was one.

- **`ConvaiPlayerBody` — where the player actually is — is now public.** The rule was internal, and
  it is not one anybody guesses: first-person controllers, Unity's own Starter Assets among them,
  put the `CharacterController` on a capsule *inside* the rig and move that, leaving the object
  carrying `ConvaiPlayer` parked at the spawn point for the whole session. The SDK's own movement
  behaviors have always resolved this correctly, and every Action Behavior written outside the
  package had to rediscover it — one written against this SDK did not, and a character who led a
  visitor across a room then stood waiting for somebody already beside her. Use
  `ConvaiActionExecutorBase.ResolvePlayer()` inside a behavior, now `virtual` so a project with
  split screen or several rigs can answer it once for every behavior in its hierarchy, and
  `ConvaiPlayerBody.Resolve()` from scene code that is not a behavior.

- **`ConvaiActionExecutorBase.DeclaredButNotSent(invocation, parameterName)`** reports whether the
  action declares a parameter that the Convai Character sent no value for. A default on the behavior
  answers a different question — *which gesture is this behavior for* — and is only meaningful when
  the action declares no such parameter; see the Bug Fixes entry on behavior defaults.

- **An action can now answer a question, and the Convai Character can say the answer.**
  `ConvaiActionExecutionResult.Answered("The valve reads 40 percent.")` sits alongside
  `Succeeded(...)`, and the sentence it carries is the only part of a result the character is ever
  told. `Message` stays what it always was — diagnostic text for the Console, the Actions Editor and
  your own game code — because the two have different audiences: the 165 messages already written
  across this SDK say things like `"arrived"` and `"Already open"`, and promoting those into a
  character's model of the world would have been a strange way to fix anything.

  Whether the answer is spoken is authored per action, under the action's description in the Actions
  Editor: **When It Finishes** → *Use the character's setting* (the default, and today's
  behaviour exactly), *Remember only*, *Mention if relevant*, or *Tell the player*. It is the more
  specific setting and wins over the character's `Convai Action Feedback Relay`, the same way
  `FailurePolicyOverride` wins over the dispatcher's policy. A line under the dropdown resolves the
  two against each other and says what will actually happen, so nobody has to hold the precedence in
  their head.

  This closes a gap with no workaround inside the SDK: an action that read a gauge or counted a group
  had nowhere to put what it found, so the character ran the action correctly and then said nothing.
  `ConvaiActionAnswerDelivery` is authoring-only — it is not part of the `action_config` sent to
  Convai and cannot change which action a character chooses. An action that answers nothing is
  unaffected whatever it is set to.

  *If a batch mixes both kinds:* the most talkative answering step decides for the batch, so a visitor
  who asked a question is answered even when the character also walked somewhere on the way.

- **An action parameter now says whether the Convai Character actually filled it in.**
  `ConvaiActionParameterValue.Presence` is `Provided` or `Missing`. An action declaring three
  parameters has always come back with three, because unfilled slots are padded to keep values lined
  up with the authored order — and that padding was indistinguishable from an answer. An Action
  Behavior could not tell *"no destination was given"* from *"the destination is blank"*, so it did
  the same thing for both, slightly wrong, for a reason nothing recorded. The line is whether
  anything was assigned to the slot, not whether the text is empty: a character that names a
  parameter and leaves it blank is `Provided` with empty text. Missing parameters are also now
  reported through `LogCategory.Actions`. `Provided` is the zero value, so a parameter value you
  build in your own code means what it always meant.

  *If you serialize action parameters yourself:* `Presence` is a public property, so
  `JsonConvert.SerializeObject` on a `ConvaiActionParameterValue` now emits one extra field. Reading
  an older document still works — the absent field is `Provided`, which is what those values were.
  Unity's own serialization is unaffected, since it serializes fields rather than properties. Nothing
  in the SDK serializes an action command or parameter value; this only concerns your own saves,
  replays or tooling.

- **`ConvaiActionDispatcher` now says whether it can start work right now:** `IsBusy`,
  `PendingBatchCount` and `CurrentActionName`, alongside the existing `BatchPolicy`. Without them,
  "the character received the command and has not started it" was unanswerable from outside — and
  that one sentence covers two completely different situations: work legitimately queued behind
  something still running, and work that is never going to start. A debug overlay, an editor panel,
  or game code that wants to gate input on the character being free all need to tell those apart.
  Read-only and cheap to poll.

- **Gaze Profile → While Walking → Coming To Rest.** As a character comes to a stop at the end of
  a walk, its eyes drop a few degrees for a beat and lift back — the difference between a person
  stopping and a camera being repositioned. `Eyes Drop As It Comes To Rest` (4° by default, 0
  switches it off) and `How Long The Settle Takes` (0.7 s). Eyes only: it rides the micro-motion
  channel, which the head never reads, so it can never become a head bow.

- **Gaze Profile → Head & Body → `Keep Head Level While Looking`:** how much of the animation's own
  head movement the head cancels while the character is engaged with something. At the default of 1
  the head holds level on its target and the eyes stay centred in their orbits; lower it to let a
  clip's head bob show through, at the cost of the eyes having to look up or sideways to keep
  contact. Scaled by engagement, so an idle character keeps its animated head personality either
  way.

- **`ConvaiActionDispatcher` now says whether it can start work right now:** `IsBusy`,
  `PendingBatchCount` and `CurrentActionName`, alongside the existing `BatchPolicy`. Without them,
  "the character received the command and has not started it" was unanswerable from outside — and
  that one sentence covers two completely different situations: work legitimately queued behind
  something still running, and work that is never going to start. A debug overlay, an editor panel,
  or game code that wants to gate input on the character being free all need to tell those apart.
  Read-only and cheap to poll.

- **Actions grew an authoring system.** Actions used to be a dispatcher, a config source and two behaviors, all of it authored field by field on a component. This release gives the module the surface it needed: an **Actions Editor** window (`Convai → Actions Editor`), reusable **Action Sets**, scene knowledge, categories, spoken outcome feedback, and a built-in behavior library.
  - **One window authors a character's actions.** The Actions Editor is a two-pane window over the character you pick: an action list on the left, and on the right the action's name and description, the behavior that runs it, a **Try It** box, and an **Advanced** foldout for parameters, valid targets, timeout and failure policy. A mode switcher covers **Scene Knowledge** (what the character knows about the scene, and what is actually sent to Convai), **Character Settings** (the dispatcher and feedback relay, edited without hunting for the components), and — while playing — **Live**.
  - **You can run an action without talking to the character.** In Play mode **Try It** becomes a **Test Run**: pick a target, fill in parameters, press **Run Now**, and the action goes through the real dispatcher — not a simulation. In Edit mode it is a **Preview** that runs a typed phrase through the same target-resolution ladder the runtime uses and tells you which target it would match, and at which step.
  - **Live mode shows what the character is doing.** The batch in progress, a timeline of recent batches with each step's duration and outcome, the merged target registry as it changes, the feedback lines the character composed, and per-action insights (run count, failure split, slowest duration) with a **Copy Report** button. The recording outlives Play mode as **Session Review**, so a run you just watched is still there to read.
  - **Actions can be shared between characters.** A `ConvaiActionSet` asset (*Create → Convai → Action Set*) holds actions that several characters offer. A set binds its behavior by type name rather than by scene reference, because an asset cannot hold one, and each character resolves that name against its own hierarchy — the window says which characters are missing the component and offers to add it.
  - **A built-in behavior library, no sample import.** Twenty-one Action Behaviors ship in the package and are picked from the editor's **+ Add Action ▾** catalog: Raise Unity Event, Wait, Run In Order, Show Or Hide Object, Play Animator State and Play Sound need no module at all; Look At Target, Watch The Player and Scan Environment come with Gaze; Set Mood and React with Emotion; Nod Or Shake Head with Body Language; Play Gesture and Point At Target, Walk To Target, Turn To Face Target, Follow The Player, Lead Player To Target and Return To Start with Body Animation; Count Target Group and Measure Distance observe the scene. Each declares its own catalog entry through `ConvaiActionArchetypeAttribute`, so a behavior you write appears in the same catalog with the same one-click add-and-bind.
  - **A character can be asked to look where it acts.** When a step starts with a resolved target, the dispatcher tells the character's other modules what it is about to act on, and each reacts in its own way — gaze turns toward it, Body Language may acknowledge, Emotion may colour the moment. On by default (**Lifelike Reactions** on `ConvaiActionDispatcher`) and applied to every targeted action, including behaviors you write.
  - **A character can say what happened.** `ConvaiActionFeedbackRelay` turns an action outcome into something the character says, silently feeds it back as context, or reads a scripted line — with a cooldown and mid-utterance deferral so it neither spams nor talks over itself.
  - **Actions are organized by a label you choose.** `ConvaiActionDefinition.Category` is an optional short name — `Counter`, `Tour`, `Small Talk` — filed on the action itself. It is never part of the action config sent to Convai and cannot change what the character chooses to do; it exists so a character with twenty actions is a list you can read.
  - **A Troubleshooter finds the setup mistakes.** `Convai → Action Troubleshooter` reports what is wrong with a character's action setup, most findings carrying a one-click **Fix**. Every surface that counts problems — the window, the config source inspector, the Troubleshooter — reads the same check engine, so the three can never disagree about how many there are.
  - **Action behaviors can live on a child object.** A character using much of the library ends up with twenty or more components on it. Assign a child to **Action Behaviors Object** on `ConvaiActionConfigSource` and every authoring path creates new behaviors there instead. Behaviors are found either way, so both layouts run identically and there is no migration you have to finish.
- **Convai Gaze: characters now make eye contact.** Add **Convai → Embodiment → Gaze** to a character and it finds the player, holds their gaze while they talk, looks away to think, comes back, blinks, and turns to face someone standing behind it. There is nothing else to set up: the head and eye bones come from the character's rig, the camera tagged `MainCamera` counts as the player, and the shipped defaults are tuned to look right — a Gaze Profile asset tunes personality rather than being required to get output.
  - **How much eye contact a character makes is a setting, not a rewrite.** *Natural* follows the conversation — engaged while listening, looser while idle. *Speaking Focus* holds the player only while the character talks. *Conversation Lock* and *Always Lock* commit fully, for kiosk and presenter characters that must never look away.
  - **What it looks at can come from the scene.** Add a **Gaze Target** to a prop worth noticing and give it a priority; the player counts as 10, so anything above that outranks the player mid-conversation. Character-to-character gaze, joint attention (following where the player is looking), and referential glances at what is being discussed are optional components under **Convai → Gaze → Advanced**.
  - **It knows when it is being looked at.** `PlayerAttentionSensor` answers "is the player looking at me, or at that?" — the same rule the eyes use, exposed so game code can ask it rather than guess and quietly disagree with what the character does.
  - **Walking is handled.** A character travelling somewhere watches where it is going and glances at its destination periodically, instead of staring at it for the whole journey. Turning to face an off-axis target either plays the character's own turn animation or rotates it directly, whichever the project has content for.
  - **Action behaviors:** *Look At*, *Scan Environment* and *Watch Player*, plus a look-where-you-act glance that follows whatever a targeted action step is acting on and hands the look over when that step walks somewhere.
  - **A Gaze editor window, and an inspector that says what is wrong and what to do about it.** For AI coding assistants, `Convai.ConfigureGaze`, `Convai.DiagnoseGaze` and `Convai.MarkGazeTarget` read the same checks the inspector shows, so the two cannot disagree about a character.
- **XR eye tracking is now something you can actually plug in.** `IPlayerGazeRaySource` and `PlayerAttentionSensor.SetGazeRaySource` / `GazeJointAttention.SetGazeRaySource` are public, so an adapter over your headset's eye-tracking API can tell a Convai character where the player is really looking, instead of assuming they are looking wherever the camera points. Implement the interface, then either drop your component into **Gaze Ray Source Component** on the Player Attention Sensor or call `SetGazeRaySource` from code. The SDK still takes no XR package dependency, and the camera remains the default.
  - This is deliberately not a reversal of the same release's decision to keep cross-module contracts internal. That decision is about the seams Convai's own modules use to talk to each other, which nothing outside the package should implement. This one is an extension point the documentation asks *you* to implement, and it was only ever internal by oversight — the interface's own description already told you to implement it.
- **Convai Body Animation: a character that idles, talks, gestures, points and walks, with no Animator Controller to build.** Add `ConvaiBodyAnimationController` and give it an animation set; the module builds its own layered playable graph and drives the character from the conversation — idle and talk variants, co-speech and beat gestures, gestures aimed at what is being referred to, and NavMesh-synced walking with directional starts, planted stops and turns in place. Movement is optional: a character with no movement component still idles, talks, gestures and points.
  - **It is content-gated by design, and says so.** Behaviours stay inert until this character's animation set carries clips tagged for them. The inspector, the console and the diagnostic each tell three states apart — not set up, set up but blocked by the rig, and set up but without clips for a behaviour — and each names what is missing and where it comes from. A setting that is on with nothing to play, and content that is tagged with the setting that would play it turned off, are both reported once when the character is built rather than left silent.
  - **Motion is calibrated to the character's own rig.** Walk and jog speeds, stop distances and the NavMesh agent capsule are derived from the character's measured height against the clip's authored motion, so a character taller or shorter than the rig the clips were authored on still plants its feet where it should instead of skating.
  - **Seven action behaviours ship with it** — walk to, lead the player, follow the player, turn to face, point at, play a gesture and return to start — so a backend-triggered action can move and pose the character with no custom code.
  - **A character can be given its own settings in one click.** Setup deliberately assigns no settings asset, so no character owns one it never changed — but that left the Personality controls with nothing to act on and no way to get it. The section now offers **Give This Character Its Own Settings**, which creates a Body Animation Config on the SDK's defaults and points the character at it.
  - **Four MCP tools** (`Convai.ConfigureBodyAnimation`, `Convai.DiagnoseBodyAnimation`, `Convai.InspectBodyAnimationContent`, `Convai.TuneBodyAnimationPersonality`) let an AI assistant set the module up, tell those three states apart, list every action name and alias `PlayAction` accepts, and tune a character's personality — copying a shared settings asset for that character first, rather than editing one that several characters read.
  - **Animation content ships with it, so a character moves out of the box.** The release includes `ConvaiBodyAnimationSet_Female` and its clips alongside the module, its default tuning and the upper-body mask, and setup assigns them — there is no separate content step and nothing to source first. Remove that content, or point a character at an empty set, and the module reports the gap rather than failing: the setup checklist marks the animation row as waiting on content instead of as an error, keeps the setup button available for everything else it can still do, and offers **Create Animation Set…** for building a set from your own clips.
- **Convai Body Language: a character that is alive between its animations.** A new module gives a Convai character conversational nonverbal behavior — it breathes, shifts its weight from foot to foot, sways gently on the spot, pulses its posture and beats its head in time with its own speech, leans in while listening, fidgets when idle, and reacts to a sudden emotion with a startle or an amused bounce. All of it layers on top of whatever the Animator is already playing: the module never owns a clip, and it needs none.

  Adding **Convai → Embodiment → Body Language** to a Humanoid character is the whole setup. There is no profile to assign and no content to import — a character with nothing assigned runs on built-in defaults tuned to read at a normal conversational distance. Assign a `ConvaiBodyLanguageProfile` when you want to shape a character: one expressiveness dial from `Subtle` to `Theatrical` scales the whole system coherently, and a per-dialogue-state table tunes how the body behaves while speaking, listening, thinking or settling.

  It shares the spine, shoulders and head with Convai Body Animation and Convai Gaze, and every one of those relationships degrades gracefully — which is exactly why they are hard to see. So the component states them: a **Sharing This Body** card names every Convai module moving this character and what each one changes, and while playing the runtime readout says what is reducing the motion right now and whether Gaze or Body Language itself is moving the head. The same report answers an assistant asking `Convai.DiagnoseBodyLanguage` — one source, so the two cannot describe a character differently.

  Also included: the **Nod Or Shake Head** action behavior, so a character can answer with its head; a scripted API (`Nod`, `PulseGesture`, `TriggerReaction`, `Expressiveness`) whose handles are cancellation-aware and never hang; and three assistant tools — Configure, Diagnose and Inspect Personalities — that read and assign but never create or edit a settings asset.

- **Emotion readings now carry the optional protocol metadata a newer backend sends.** `CharacterEmotionChanged` gains `Sequence`, `UtteranceId`, `Confidence` and `DurationMilliseconds`, each with a defined meaning when the backend omits it, so the existing constructor and every existing payload stay valid. Sequence lets a delayed packet be ignored rather than rewinding a character's expression, and confidence lets an uncertain reading be expressed faintly rather than discarded.
- **The scene-wide tools can now see a character's behaviour modules.** The survey seam shipped with nothing registered against it, so `Convai.InspectScene` and `Convai.ValidateSetup` listed no module readiness at all. Emotion registers a surveyor, joining Gaze, so both tools report whether a character's face is set up, blocked by its rig, or set up and still never going to move — in the same words, from the same checks, as the component inspector.
- **Convai Emotion is now a complete character-expression module.** A character gets a face that reacts to what it feels: expression driven by semantic recipes that resolve onto whichever blendshapes the rig actually has, so one personality works on ARKit, Reallusion CC3/CC4 and MetaHuman faces without per-character authoring. On top of that sit a resting mood the face settles to between emotions, mood that drifts with the conversation, mood picked up from a nearby character, blending so related emotions show together with anti-flicker hysteresis, a small-movement layer that keeps a resting face from reading as a mask, reactions to the listening, thinking, reacting and interrupted conversation beats, expression that follows the voice's energy while speaking, per-emotion attack and decay timing, and shader-property output for effects like blush, tears and sweat. Four character types — Composed, Warm, Energetic and Reserved — set all of it at once, and ship as four personality assets.
- **Runtime mood control**: `ConvaiEmotionController.SetMood(label, intensity, transitionSeconds)` and `ClearMood(transitionSeconds)` crossfade a character's resting mood from gameplay code. `SetMood` outranks the character's own **This character rests at** override, which outranks the personality's resting mood; `ClearMood` returns to whichever of those two applies. Neither ever appears as the character's active emotion.
- **Two resolved gameplay events**, `DominantEmotionChanged` and `MoodChanged`, both `Action<string, float>`. They fire on label transitions only, after the reading they describe is already current, and a throwing subscriber is logged rather than allowed to break the frame. Unlike `ConvaiManager.Events.OnCharacterEmotionChanged`, which relays the raw backend packet, these report what the character actually ends up expressing.
- **An Emotion editor window** (`Convai → Emotion Editor`) with setup, every setting grouped in plain language, the resolved expression mapping for this character's face, and a live view in Play Mode — plus a left pane listing every character in the open scenes so a whole scene can be swept.
- **An Emotion Timeline window** (`Convai → Developer → Emotion Timeline`) that plots a character's emotion and mood over time with markers where they changed, for answering "the mood felt wrong there" by eye.
- **Four MCP tools** — `Convai.ConfigureEmotion`, `Convai.DiagnoseEmotion`, `Convai.InspectEmotionPersonalities` and `Convai.TuneEmotionPersonality` — so an assistant can set a character's face up and answer questions about it. Diagnosis reports what a user would actually see rather than what is stored: a beat reaction is reported as off when the layer that plays it is off, and the resting-mood answer names which link of the precedence chain won.
- **The Lip Sync Sample ships a new character, Sofia.** She replaces Camila as the sample's face and is set up the same way — real-backend conversation, lip sync, gaze and body animation — on a Reallusion CC4 rig with the same blendshape convention, so nothing you learned from the sample changes. Her body animation runs on the shipped Female animation set, and the sample scene is wired to the SDK's built-in defaults rather than to per-character profile assets, so it behaves identically in your project as it does in ours.
- **Convai's first-person sample controller is now part of the package.** `ConvaiSampleFirstPersonController` and `ConvaiSampleFirstPersonInputs` (under `SamplesShared/ThirdParty/StarterAssets/`) give the samples a player you can walk around with, without asking you to import anything from the Asset Store first. They are a renamed, trimmed copy of Unity's Starter Assets first-person controller — renamed precisely so that a project which already imports Starter Assets does not get an ambiguous-reference compile error. Provenance, the exact upstream version, and every change made are documented in `Documentation~/STARTERASSETS-VENDOR-PROVENANCE.md`, and the upstream licence travels with the code.
- Added Unity runtime session lifecycle APIs: idle-warning and locally derived idle-deadline events, `ResetIdleTimer`/`ExtendIdleTimeout`, explicit pause/resume/reconnect controls, and `ContinueAudibly`, `PauseTimeline`, and `MuteButCatchUp` background policies with typed and Inspector relay events.
- **Writing your own action behavior no longer starts from a bare interface.** Three base classes carry the parts every behavior was otherwise reimplementing. `ConvaiActionExecutorBase` resolves the character's transform correctly whether the behavior sits on the Convai Character itself or on a child object holding its behaviors — reading the component's own transform is wrong on the second layout and silent about it. `ConvaiTargetedActionExecutor` adds the target precondition, peer lookup with caching, a missing-peer report logged once per component instead of on every call, and helpers that let an invocation parameter override an Inspector-authored default. `ConvaiCharacterActionExecutor<TPeer>` handles that whole flow for a behavior that works through exactly one component on the character, so a subclass starts at what to do once the component is there.
- **A Troubleshooter that says what is stopping a character from working, and fixes what it can.** **Convai → Troubleshooter** reports every Convai capability on a character in one place — what is set up, what is blocked, and what is set up but will not visibly do anything yet — with the repair beside each finding. Every fix is one undo step, and **Fix All** names what it is about to do before it does it. A status chip in an inspector now opens the window on that character, expanded at the capability the chip was reporting on. Opened from the menu with nothing selected, it lists every Convai Character in the scene with its worst finding, so "which one is broken" is answerable without clicking through them one at a time. A **Checked And Fine** section lists what passed, so a green chip can be verified rather than taken on trust.
  - **You can report your own findings.** Implement `IConvaiSetupHealthProvider` and register it with `ConvaiSetupHealthRegistry.Register`, and your own character checks appear in the Troubleshooter beside Convai's, with your fix buttons and your "Show Me" targets. The same registration makes them visible to `Convai.InspectScene` and `Convai.ValidateSetup`, so an AI coding assistant reads the same report a person does. New public API: `IConvaiSetupHealthProvider`, `ConvaiSetupHealthRegistry`, `ConvaiSetupFinding`, `ConvaiSetupHealthResult` and `ConvaiSetupHealthSnapshot`. An `Error` finding must carry either a fix or somewhere to look — an error a user cannot act on is a defect in the finding, and a guard test enforces that rather than a reviewer.
- **The Quest passthrough camera has a real inspector.** A Quest Vision Frame Source drew Unity's default inspector: no live capture readout, no resolution or frame counters, and no note that a Passthrough Camera Access component is discovered automatically when capture starts. It now has the same three sections its camera and webcam siblings already had.
- **An action failure now says why in a form code can react to.** `ConvaiActionFailureReason` accompanies the existing message on `ConvaiActionExecutionResult` and `ConvaiActionStepReport`, distinguishing a missing target, an unreachable one, a blocked path, a missing peer component, an invalid state, a timeout, an interruption, a target that cannot be acted on, and a character that is simply busy right now. A behavior you write returns the reason alongside its message, and whatever handles the step report reads it without parsing text. Existing `Failed(message)` calls keep working and report `Custom`; `Canceled()` and `TimedOut()` report `Interrupted` and `Timeout`, and `TimedOut` now takes an optional message so a step that ran out of time can name itself.

### Improvements

- **Sofia's FBX no longer carries a second copy of every texture.** The Character Creator export
  embedded all 45 texture files inside `Sofia.Fbx` — 49.8 MB of the file's 65.8 MB — and Unity
  extracted every one of them into `Sofia.fbm/` on first import. The materials bind the extracted
  assets, so the copy inside the model was downloaded by every customer and read by nobody. The
  `Content` node under each `Video` node is now removed and the file rewritten: 65.8 MB → 16.0 MB.
  Mesh, rig, blendshape and material data are untouched — node counts are identical except for the
  45 removed blobs, and the writer was proven byte-exact by round-tripping the original file to an
  identical MD5 before it was allowed to modify anything.

- **The LipSync Sample character is 147 MB smaller and renders identically.** Sofia's source
  textures shipped at their Character Creator export resolution — up to 4096 — while her import
  settings clamp them to between 64 and 1024, so every customer downloaded pixels Unity discarded
  before they ever reached the GPU. A single 2048×2048 lossless map of the inside of her mouth
  accounted for 26 MB and was imported at 256. Each source is now stored at exactly the size its
  own importer asks for, downsampled with the same Mitchell filter (`resizeAlgorithm: 0`) Unity was
  applying at import, on straight non-premultiplied alpha. Her texture payload drops from 162.8 MB
  to 8.8 MB. Together with the FBX rewrite above the character falls from 241 MB to 48 MB and the
  package from 450 MB to 258 MB. Nothing about the rendered result changes and no
  importer setting, GUID, material binding or prefab was touched: 46 of the 82 resized files are
  bit-identical to what Unity produced before, and the 36 JPEG-container maps — which cannot become
  PNG without changing their asset path — are re-encoded once from the pristine source at maximum
  quality, measuring no worse than 50.6 dB PSNR against the ideal downsample.

- **Tuning a personality no longer leaves the type row blank.** Emotion, Gaze and Body Animation
  infer their Character Type / Archetype from the values on the asset, so the first slider that
  diverged used to turn every pill off and say nothing. The row now shows a **Custom** status, the
  collapsed section header keeps the asset name with `(Custom)` beside it, and a line underneath
  tells you that is fine and that clicking a named type applies it again. Copy-on-write is unchanged:
  the first edit of an SDK-owned asset still makes a project copy for that character.

- **The three core Actions components now say what they do.** Add Component and the Convai
  inspectors show **Convai Actions**, **Convai Action Runner**, and **Convai Action Monitor** instead
  of the implementation-oriented Config Source, Dispatcher, and Debug Probe names. Their public C#
  types (`ConvaiActionConfigSource`, `ConvaiActionDispatcher`, and `ConvaiActionDebugProbe`) and all
  serialized data are unchanged, so existing scripts, scenes, prefabs, and Inspector values need no
  migration.

- **An action's Description is written in a field that grows to fit it.** Description is the field
  the Convai Character actually reasons from, and it was a one-line text field: past about a dozen
  words the text scrolled sideways and the author could see only the fragment under the caret, with
  no way to read back what they had written. Both Description fields in the Actions window — the
  action's and each parameter's — now use the same growing prose field the Scene Knowledge
  descriptions already use: one line tall while the text fits on one, taller as it is written, never
  clipped, and showing an example inside the field while it is empty. Nothing about what is sent to
  the character changed.

- **Turn-in-place and planted stops are off in the shipped default body animation config.** Both
  are content-hungry features that read well only when the clip set has the matching turn and stop
  content authored and analyzed, and read worse than plain blending when it does not — a turn the
  clip cannot cover, or a stop plant landing on the wrong foot. Neither is removed and neither has
  changed: tick **Turn In Place** and **Planted Stops** back on in the config whenever the content
  is there. Braking is unaffected — a character that stops on purpose still runs its stride out
  (see the graceful-stop entry below); with planted stops off it decelerates on agent braking and
  blends to idle rather than landing a stop clip.

### Bug Fixes

- **Push-to-talk no longer strands a turn when speech resumes during release finalization.** After the first
  release-tail window, the SDK now mutes the local microphone before sending the authoritative stop while keeping
  backend STT open for one final bounded window. Buffered ASR can still finalize, but no new microphone audio can
  restart server speech after the stop and leave the character without a turn to answer. Rejected stop delivery is
  retried once, terminal control failures are reported, and runtime input-mode switches now preserve the same
  fail-closed ordering without publishing a mode that was not accepted.
- **The LipSync sample background light once again targets only its intended URP rendering layer.** The current
  rendering-layer mask now matches the existing legacy mask and the sample release contract.
- **The neck no longer snaps as a body turn begins.** When the feet start turning, the head's
  offsets are relieved so the neck rides the turn instead of staying pinned — and the relief factor
  was eased, so the head's goal never jumped. Its *rate* did: an exponential ease leaves at its peak
  slew, so the factor acquired full speed in a single frame, and it multiplies the head's entire
  allocated share. On a 45° look that put about 160 °/s into the goal from a standing start. The
  tracking lane reproduces whatever the goal does, so the head chased it with everything it had —
  measured at a clean ±1500 °/s² (the acceleration envelope, exactly), peaking at 212 °/s before
  braking. Bang-bang: the harshest motion the limits allow, and the one shape the two-lane actuator
  exists to never produce. The blend is now critically damped, so it leaves and arrives at rest, and
  it settles over a time comparable to the turn-time law's own base instead of a third of it —
  relief moves the head 27° on that 45° look, and doing that in a fifth of a second was the relief
  blend quietly bypassing the duration law for one of the largest movements the module makes.
- **The procedural body turn now finishes.** On a character with no Body Animation module — the
  fallback swivel — a turn toward a target that was not moving never landed. The driver is re-aimed
  every frame of a turn whether or not the target went anywhere, and the re-aim decided whether to
  extend the turn's clock by asking whether the angle still to cover warranted more time than the
  clock had left. That is true for most of a healthy movement rather than only for a re-aimed one:
  a minimum-jerk turn leaves at rest, so early on the angle shrinks more slowly than the clock runs
  down. The clock was therefore reset to a full fresh duration on every frame, the turn was pinned
  in its opening phase, and a 180° pivot planned for 2.4 s had covered 65° after 3.3 s and was still
  going — smoothly, and correctly shaped, which is why it read as a character that turns to face you
  with enormous patience rather than as a bug. Worse with a target in motion, where it diverged
  outright. Only a goal that has actually moved — measured from where it was when the clock was
  planned, so a steadily walking target still counts — may now re-plan the clock.
- **A character in conversation no longer glances away and back for no reason.** Once in a while,
  usually as an answer finished, the head would set toward somewhere else entirely and return
  almost immediately. The cause was the idle-life hand-over added alongside the curiosity-glance
  fix below: while the head has not yet joined a new look it keeps holding the fixation idle life
  gave it, rather than being dropped to a share the actuator ladder has not allocated yet. That is
  right, but it was gated on the head's *recruitment* being zero — and recruitment is the entry
  ease times the onset gate, whose two zeros mean opposite things. Onset zero means the head is
  about to join. Entry zero means the shift is smaller than the head's entry angle so the head is
  never joining, the eyes own it — which is the steady state of talking to someone you are already
  facing. The gate therefore stood open for the whole conversation, and any dip in the target
  commitment ramp handed the head's goal to a fixation chosen before the conversation started, or,
  once idle life's resume window had cleared it, to straight ahead. Because it was a jump in the
  *goal*, the actuator did exactly what it should with one: planned a properly shaped movement to
  the wrong place, then another one back. The hand-over now keys on the onset alone, and the
  controller additionally requires that idle life still holds a fixation worth resuming.
- **The end of an answer no longer re-aims the head.** A conversation-state change re-weights how
  strongly an unchanged look is held; it is not a decision to look somewhere else. Both values the
  ladder divides a shift by are handed to it unsmoothed so that a genuine re-target arrives as a
  step — but they also stepped on state edges, and on the shipped table Speaking to Settling drops
  head participation from 0.85 to 0.36 in a single frame. The actuator read that as a decision and
  performed a deliberate turn away from a target the eyes never left, then repeated it when the
  floor-yield engagement pin expired. Those inputs now step on a new look and settle within one, so
  a state change reads as the character relaxing its hold rather than looking elsewhere.
- **A character talking to someone off to one side no longer hunts.** The comfort model's strain
  test was a bare threshold feeding an integrator that moves the very pose the test measures, with
  no hysteresis and a release nearly five times quicker than the build — a loop that does not
  settle. Sustained eye eccentricity would recruit the head, the head's share would bring the eyes
  back inside the comfort angle, the pressure would collapse, the share would go back, and the eyes
  would go straight back out. Strain now releases only once the pose is well inside the comfort
  angle, and the orbit pressure is eased rather than ramped linearly.
- **The head no longer twitches when a character finishes speaking mid-aversion.** The turn-taking
  director's head-participation scale is a gain on the aversion offset, which is composed onto the
  head pose after everything that shapes motion — so a step in it is a step in the pose. It stepped
  twice per utterance: the floor-yield cancelled any running break by driving the scale to zero
  while the beat's offset was still most of the way out, and the consumer, which switched the term
  off outside Speaking, restored it to one on the Speaking-exit edge. The scale now falls instantly
  (a beat starts from rest, so there is nothing to step) and rises over a ramp, a cancelled break
  returns the head to full participation instead of none, and the consumer no longer switches on
  the dialogue state.
- **An idle character's curiosity glance no longer snaps.** The glance at the player that an idle
  character makes every few seconds was the harshest movement the gaze module produced, and it was
  three defects stacked on one beat. Idle exploration was switched off by a boolean the frame a
  target became engaged — a whole head-onset gap before the actuator ladder had any share to replace
  it with — so the head's goal collapsed toward centre, the actuator faithfully executed a movement
  in that direction, and the real turn to the player then reversed it: one look performed as two
  movements in opposite directions. That first movement was also executed at *reflex* speed, because
  an ordinary re-target and a camera cut were reported on the same flag and the head stage read it as
  "something happened to the character" — so a voluntary glance ran 1.8× faster than the idle drift
  it interrupted, out of a state whose whole job is to look unhurried. And when the glance released,
  idle life had been cleared underneath it, so the character came back to a fresh random fixation
  instead of to whatever it had been looking at.

  Underneath all three sat a fourth: the actuator ladder was handed engagement *after* both the
  acquisition ramp and the policy blend had been folded into it, so the head's goal did not step —
  it ramped, over `Commitment Acquire Seconds` and `Policy Blend Speed`. A ramped goal is **tracked**, not shaped: the tracking lane is
  transparent to in-budget motion by design, so the head covered the whole turn in the ramp's own
  time at constant velocity, with the turn-time settings (*Head Turn Time*, *Added Time Per Degree*)
  never consulted. That is why the movement read as far quicker than any profile value could
  explain. The ladder is now handed the settled strength; the acquisition ramp says the character is
  taking the look up, not how fast its neck moves.

  So: the head holds its idle fixation until it joins the look and takes it back when it stops
  taking part — one step, one planned movement, never travelling away from what it is about to look
  at; a cut and a re-target are reported separately, so only genuine reflexes (camera cuts,
  teleports, the startle beat) move at reflex speed; and a glance-length interruption leaves the
  idle fixation standing, so a character that glances at you returns to what it was doing. Affects
  every look taken out of idle, not only the curiosity glance. No settings changed, and nothing to
  migrate — but a character will now take the turn time its profile asks for, which is slower than
  what shipped.

- **An idle glance looks at you, not past you.** A glance's strength was multiplied by the dialogue
  state's head contribution, and in `Idle` that number describes a character drifting around a room
  (0.4 in the shipped sample profile). The product — 0.5 × 0.4 = 0.2 — is a value nobody authored:
  the head took a fifth of the shift and left the eyes holding the other four fifths, far outside
  the profile's own *Eye Comfort Degrees*, so the character read as looking at you out of the corner
  of its eyes. Past a 44° shift the eyes ran out of travel (*Eye Max Yaw* 35°) and the aim landed
  short of the player outright. A glance now states its own head participation, and carries the
  committed engagement every other glance in the module already used (*"the brevity is the
  modifier"* — see `GlanceOptions`): the head does about three quarters of the turn and the eyes
  finish it, which is how people look at each other.

- **A glance at the player aims at the player, not at the floor under them.** A scripted request
  that carries a transform resolves its aim from that transform's position — correct for
  `GazeAt(prop)`, wrong for a re-pushed provider candidate, because the player anchor aims at an eye
  line above the rig's root and that offset was being dropped. Characters followed the player
  perfectly and looked at their feet. Only affects setups whose player anchor is a rig object; with
  the main camera as the anchor (the default, and what the samples use) the aim point already was
  the transform's position, so nothing there changes.

- **An idle glance is no longer attempted at someone standing behind the character's shoulder.** A
  glance never turns the body, so a curiosity glance at a player more than 100° round pinned the head
  and eyes at their limits for the full hold and then unwound — a lurch, not a glance. The curiosity
  glance now applies the same reachability rule the character-to-character glance already used, and
  waits for its next turn instead.

- **Scene Knowledge lists the Convai Action Targets in your scene without being asked.** *Find
  Targets In Your Scene* only ever looked when the user found and pressed **Scan Scene**, and
  everything downstream inherited that: a scene with targets in it read "none found", and *Sent To
  Convai* showed the connect payload and called it the whole story — while the Live tab showed the
  backend holding targets the preview had never mentioned. The scan now runs on its own whenever the
  open scenes change, so both cards describe the scene as it is. The button remains, as **Scan
  Again**, for the one case the hierarchy does not report: editing a Target Name on a component.

- **Message boxes no longer run their last words past their own right edge.** An info, warning or
  error box measured its body against the window width, which is wider than the box whenever one is
  drawn inside a card — so the copy was wrapped for a column wider than the surface it was drawn on
  and the overflow was clipped. Boxes now measure against the width they are actually drawn at.

- **A hint beside a button sits on the button's centre line.** The drag hints under Known Objects and
  Known Characters took their own height in the row and rode above the *+ Add* button next to them.

- Fixed Meta Quest push-to-talk releases by reading the A, B, X, and Y buttons through Unity's XR input API. Active XR controller read failures and disconnects now fail closed so microphone capture cannot remain open, while keyboard and non-XR joystick fallback behavior is unchanged.

- **Actions Editor Live tables line their column titles up with the cells.** Insights, Timeline
  and Feedback Log drew headers as sequential GUILayout columns and the rows as inset rects, so
  Duration and Timing sat well left of the numbers, and Insights **Order by** sat on the top edge
  of the sort pills. Headers now share the row geometry; Order by reserves the same height as the
  pills.

- **Shipped docs and inspector copy name the Action catalog that actually ships.** Gaze, Body
  Animation, the Actions tutorial, the target-group inspector, and the assistant custom-actions
  note described Guided Tour, Address The Group, Perform Gesture At Target, and sample files that
  are not in this package.

- **The head no longer jumps when the conversation changes beat.** Moving between dialogue states —
  most visibly settling back to attending — could snap the head to a new pose in a single frame. The
  cause was a gain rather than a movement: the reflex that cancels the animation's own head motion
  is deliberately immediate, because a canceller that lags leaves exactly the head-bow it exists to
  remove — but *how much* of it applies is a policy value, and policy values step. Engagement is
  pinned outright by the floor-yield beat and floored by the target-loss search, both on on/off
  edges, so the head moved by the animated deviation times the change, instantly, downstream of
  everything that shapes motion. The reflex is still frame-exact; its gain now ramps, so a policy
  changing its mind can no longer arrive as a pose step.

  The same term had a second, larger step in it, on **every** release of a look: the deviation is
  only measurable while there is something to look at, so it was dropped to zero the frame gaze
  disengaged, and a head that had been held level against a talking clip's bow fell onto that bow
  in one frame — a dozen degrees on a typical rig. The last measured deviation is now faded out
  instead of dropped.

- **A walking character no longer glances at its destination every few seconds.** It still watches
  the path ahead — that is the behaviour worth having, and it is untouched. The glance at the
  destination is the one that read badly, and the reason is its timing: it comes from a countdown
  rather than from anything the character noticed, so it lands as an unexplained look away from the
  road, and it is at its most conspicuous near arrival where the destination is close enough that
  the look is a large movement. Now off by default, behind *Gaze Profile → Travel → Glances At Its
  Destination*, separate from *Watches Where It Is Going* so the two can be chosen independently.

- **A character no longer glances at whatever its current action step is about.** Described in a
  sentence — "it looks at the thing it is acting on" — this sounds obviously right, and on screen it
  is not: the glance is decided by a step boundary rather than by anything the character noticed, so
  it lands at moments a person would not have looked, and it reads as the character being distracted
  by its own hands. It is now off by default, behind *Gaze Profile → Targeting → Look At Action
  Targets* for anyone who wants it. **A walking character is unaffected**: watching the road and
  glancing at where it is going are the travel settings' business and are still on.

- **A character no longer ducks its head at the floor as it arrives somewhere.** Walking to
  something looked right the whole way and then went wrong in the last stride: the head tipped down
  toward the spot the character was about to stop on. When an action step announced its target so
  the character could look at what it was doing, it announced the target's *interaction point* — the
  place to stand. For anything you walk to that is on the floor by definition, and when no explicit
  one is authored it falls back to the object's own pivot, which sits on the floor for most props.
  So the look-where-you-act glance aimed at the character's own feet. The angle hid it: it grows
  with closeness, a few degrees and invisible at the far end of a walk, nearly sixty degrees down at
  arrival — and each new step of a multi-step action re-aimed it at the worst possible moment.
  A step now announces where the target visibly *is* — the centre of its drawn volume — while
  continuing to walk to the interaction point. Acting on something genuinely low still looks down at
  it, because a low object's drawn volume is low. Reported while walking a character around the
  Terminal demo.

- **Selecting a Convai Action Target no longer floods the console.** Its scene-view handle read the
  editor's `targets` array and `serializedObject`, both of which Unity forbids inside `OnSceneGUI`
  and warns about — once per repaint, so moving the mouse over the Scene view filled the console
  with two warnings a frame. The handle reads the selected target directly now; it behaves the same.

- **Punctuation written in a normal editor no longer changes what an action means.** The wire format
  is defined over printable ASCII, and everything outside it was replaced by a space. An em dash is
  outside it, so a description reading *"it does not build anything — say Run The Assembly for that"*
  reached the character as *"it does not build anything say Run The Assembly for that"*: the aside had
  become part of the clause, and the sentence the character reasoned from was not the one anybody
  wrote. Curly quotes and ellipses — what any word processor produces, and what a description pasted
  from a design document is full of — did the same. Nothing reported it; the inspector showed the
  author their own text. Typographic dashes, quotes, apostrophes and ellipses are now replaced by the
  ASCII that means the same thing rather than by a gap. Characters with no exact ASCII spelling — an
  arrow, a currency symbol, an emoji — are still dropped, because guessing at those is how a fold
  starts changing meaning instead of preserving it.

- **An action is no longer dropped because the Convai Character wrote its name without an article.**
  Shown an action called *Light The Room*, a character will sometimes answer `Light Room` — the same
  action, in the English a person would actually speak — and the command was refused as an action
  this character does not have. Nothing ran, and the only trace was a drop reported in the Actions
  window; from the visitor's side the lights simply did not come on, at random, because the same
  request would work the next time the character happened to copy the name exactly. Name matching
  now falls back to comparing the words that carry the meaning, ignoring `a`, `an`, `the` and the
  punctuation between words, after exact and prefix matching have both failed. On the text the
  character wrote, that reading stops where the name stops: a slot brace ends it, and a word written
  against a separator — the `mode` in `mode: on` — names a slot rather than the action. Without that
  boundary the longest match wins by swallowing a slot label, so a character offered both
  `Light The Room` and `Light The Room Mode` would run the second for `Light Room {mode: "on"}`.
  Punctuation the reading stepped over is taken as part of the name too, so `Ring Bell? loud` against
  `Ring The Bell!` leaves `loud` rather than `? loud`. It stays strict about
  everything else: the words must match in order and in full, so *Walk Toward The Bench* is still not
  the action *Walk To*. Where two actions reduce to the same words — a character authored with both
  *Light Room* and *Light The Room* — neither is chosen and the command is still reported, because
  moving a character on a coin toss is worse than a drop the developer can read. The matched name is
  also taken off by the length the character actually wrote rather than the authored length, so the
  action's own name is never carved up as a parameter value.

- **The eyes no longer lose what they are looking at while the head turns toward it.** Turning back
  to the player after looking away — the end of a *Point At*, a *Look At*, or any glance — took
  three saccades where it should take one, and in between them the eyes were up to 24° off the
  player and pointing back toward whatever had just been looked at. The first saccade reached the
  player on time; what followed read as sidelong and evasive, because the head arrived while the
  gaze was still elsewhere. The eye stage stores an eye-in-head angle, and two of its branches
  deliberately hold that angle still — the saccadic reaction time, and the ballistic flight of the
  saccade itself. Holding an eye-in-head angle only holds a fixation while the head is also still;
  during a gaze shift the head is turning, so a held angle swept the eyes across the room at head
  speed, undoing each saccade and forcing another. The eye state is now stabilized against the
  head's own rotation before the state machine reads it, which is what the vestibulo-ocular reflex
  does and what the stage always claimed to do; the compensation is bounded by the current aim, so
  it can carry the eyes toward what they are looking at but never past it or away from it. Measured
  on the same shot: three saccades and 24° of stray became one saccade and 3°. Pursuit loses its
  standing tracking lag as a side effect. Nothing is configurable here and nothing needs to be — the
  eyes are the remainder of a shift the head has already taken its share of, so the reflex runs at
  full strength. Eyes still saturate, and the gaze still gets dragged, when the head turns past what
  the oculomotor range can reach; that part is real and unchanged.

- **A character that stops mid-walk no longer slides to a halt with its feet standing still.**
  Ending a walk by cancelling its path — which is what every "stop and wait" behaviour did — told
  the animation the move was over on that frame while the NavMesh agent carried on coasting down
  under its own acceleration. From a jog that is roughly 0.8 m of travel with nothing playing over
  it: the body glides, the legs are already idle, and the two never agree. Two changes close it.
  `ConvaiNavMeshLocomotion.Stop()` now zeroes the agent's velocity as well as clearing its path, so
  an interruption really is instantaneous rather than instantaneous-in-animation-only. And a new
  `ConvaiNavMeshLocomotion.StopGracefully()` ends a walk the way a person does — the character keeps
  moving to a braking point a stride ahead on its current path, so the agent decelerates under its
  own auto-braking and the animation gets the arrival it needs to play a planted stop. The braking
  distance comes from the character's own stop clip when a `Convai Body Animation` controller is
  present, and from the agent's physics otherwise, so a character with no movement content still
  brakes smoothly — it just has no footfall to land on. `MoveEnded` still reports `false` for a
  graceful stop: the character stopped, it did not get where it was sent.

  *Follow The Player* now uses it in both places it stops on purpose — closing on the follow
  distance, and being told to stop following. Cancelling outright remains correct for a genuine
  interruption (the component being disabled, another action taking over) and is unchanged there.

- **Editor readings no longer inherit the colour of the last thing that went wrong.** The Actions
  Editor overview showed every group's count in the error colour while all sixteen actions were
  ready, and it was not the Actions Editor's mistake: the shared editor styles applied a tint by
  writing the colour onto the one style instance everything draws through, and nothing ever put it
  back. The first tinted reading anywhere in the editor — a warning on a rig binding, a broken
  action in another character — set the colour for every untinted reading, in every Convai window and
  inspector, for the rest of the session. It survived closing the window, because the styles outlive
  it.

  Tinted styles are now pooled per colour, so a tint reaches only the draw that asked for it. Stat-tile
  numbers, table cells, live telemetry values, right-aligned counts, status pills and message icons all
  went through the same mechanism and are all on the pooled one now, and a guard test fails the build if
  a call site re-colours a shared style again. Colour is how this SDK's editor tells you something needs
  attention, so a ready count reading as red is not a cosmetic defect — it is the tooling reporting a
  problem that does not exist.

- **A request the Convai Character wrote as several entries in the actions list is now read as one
  command.** Asked to turn the gallery lights on, a character sent
  `["Light The Room", "on", "Gallery Lights"]` — the action, the value for its Choice, and the
  target, as three entries. All three then failed on their own terms: the action for naming nothing
  to act on, and the other two for not being actions. Measured against a live backend, where this
  accounted for every failure in a full run of a sixteen-action library.

  An entry is folded into the one before it only when three things hold: it names no action of its
  own, the entry before it arrived carrying nothing at all, and its text is admissible for one of
  that action's still-empty slots — an authored Choice value, or the name of something this
  character has. Anything else is dropped and explained exactly as before. The join is written in
  the wire grammar (`Light The Room {mode: on} {target: Gallery Lights}`), so there is no second way
  of parsing a command to keep in step with the first, and it is reported once per batch because a
  command that was repaired is not the command that was sent.

- **A Convai Character that wrote its parameters in brackets is no longer told the action does not
  exist.** `Follow Me(mode='follow')` was dropped as an unknown action, and the visitor's "follow
  me" did nothing. The name test accepted only whitespace or a brace after the action name; it now
  asks that the next character not continue a word, which covers every punctuation a model might
  reach for while still refusing `Walk Toward` as a match for `Walk To`.

- **A whole parameter set written as one object now reads correctly.** `{step: 4, target: "Bench"}`
  left the outer brace and the comma inside the values — `step` arrived as `4,`, which is not a
  number, so a visitor who asked for step four got step two and then step three, with nothing
  anywhere saying why. Brackets wrapping the whole answer are removed first (`{}`, `()` and `[]`,
  all three measured on one run), field separators are trimmed, and a quoted key — `"low": 20` — is
  recognised as the label it is. A value that already names something this character has is left
  alone, as everywhere else.

- **A slot the Convai Character deliberately left out no longer fills up with another slot's
  label.** Asked for the next step of an assembly, it answers `{target: Assembly Bench}` and omits
  `step`, which is what that action's description asks for. Labels covering only some slots used to
  be refused outright, sending the text to a positional guess that put `{target:` into `step`. The
  labels are believed now and the unlabelled slots marked Missing — under two conditions that keep
  it from losing text: the last slot must be labelled, and nothing may precede the first label.

- **An action with a single parameter no longer has its value carved up and partly thrown away.**
  The value splitter ran for every action, however many slots it had — but carving only exists
  because two or more values had to share one string, and where that is not the case there is
  nothing to decide. A line holding two quoted phrases matched the quoted-group stage, which found
  two groups where the template had one slot; the extra was padded away and the parameter arrived
  holding only the first phrase. A truncated line reads as a line, so nothing looked wrong.

  A one-slot action now takes what the Convai Character sent, whole, and the cleaner takes any
  decoration off it. The stage order for actions that genuinely need carving is written down with
  its reason: a value delimited at both ends beats one that is only marked at the start. Sent
  `{low: 20} {high: 80}`, the label anchors read `low` as `20} {`, because the punctuation between
  two values belongs to neither of them.

- **A Convai Character that answers a slot with a JSON object no longer has the answer spliced into
  nonsense.** Shown `Play Gesture {gesture: choice [wave|bye|…]}`, a model may fill the slot with a
  whole object — `{"gesture": "wave"}`. The braces came off correctly, and then the quote repair
  took the leading and trailing quotes of *two different strings* for one wrapping pair and removed
  both, handing the behavior the literal `gesture": "wave`. Measured live: the character announced a
  wave and stood still, and the console reported only that it had no gesture by that name.

  Two faults met on that input and both are fixed. Quotes are now only stripped when the opening
  quote closes at the end of the value — or when what it wraps is itself a wrapped value, which is
  what keeps `''wave''` working; an opener that closes partway through, with a second string after
  it, is not a wrapper. And dropping a slot's own label off the front of its value now recognises
  the label whether or not the Convai Character quoted it. Neither is a new repair: both are
  existing repairs that were reading a narrower shape than the one models actually send.

- **A behavior's default no longer stands in for a value the Convai Character was asked to send.**
  Measured twice on one run, and both times the default was the opposite of what was asked: "stop
  following me" arrived as Follow Me with no `mode` and the character started following; "thanks,
  goodbye" arrived as Play Gesture with no `gesture` and she waved hello. Neither reported anything
  wrong, because from the behavior's side a default is a valid value. Where the action declares the
  parameter, the behavior now declines and says why; where it does not — a "Wave" action wired to
  its own behavior — the default works exactly as before.

- **An action that asks for a target its own behavior does not need is now reported at authoring
  time.** The admission stage reads the *definition*, so a command that names nothing is turned away
  before the behavior is asked — and a behavior that overrides `RequiresTarget` to `false`, because
  it finds what to act on itself or acts on the player, never gets the chance. Nothing about that
  reads as a mismatch: the drop says the Convai Character "named nothing", which sends whoever is
  diagnosing to the action's wording. Found twice in one session on two different actions, and
  twelve shipped behaviors override `RequiresTarget`, so the trap is available to every project.
  Reported as a warning, because the pairing is occasionally deliberate — it is surfaced so the
  choice is made rather than discovered.

- **A Choice value that misses now says what it nearly hit.** `waving` against an action offering
  `wave` reported only that the value was not among the authored choices and listed them. It now
  adds *"Did you mean 'wave'?"* when the value is one small slip away. Naming the near miss is
  diagnosis; acting on it would not be, so the parameter still falls back rather than being silently
  corrected to a value nobody sent.

- **The console stops explaining a dropped action command after the first one, for the rest of the
  connection — fixed.** Each distinct fault was said once and then suppressed permanently, by a set
  that was never cleared. That is a mute, not a throttle, and it fails in the two moments that
  matter most. You change an alias and ask again: the answer is silence, which reads exactly like
  success. And the first occurrence is usually spent before anyone opens the console to look, so the
  Actions editor lists a dropped command the console never mentioned.

  A repeated fault is now quiet for thirty seconds rather than forever, and the line that follows
  says how many like it were held back in between. The lip-sync ingress already worked this way;
  both now share `RepeatedMessageThrottle` instead of two hand-written copies of the policy, only
  one of which was correct.

- **The Actions Editor's Scene Knowledge view no longer comes back from a script compile describing
  a state it has thrown away — and a scan now survives the compile at all.** Reloading assemblies
  restored the pane's plain values but not the collections behind them: the headers came back still
  saying *39 found · all known* and *41 entries* while the lists they counted came back empty, both
  "rebuild me" flags came back **false** so nothing was ever recomputed, and the character reference
  the pane watches for a change came back intact, so the reset that switching characters performs
  never fired either. The result was a header contradicting its own body on every compile, permanent
  until the user happened to switch character. Scan results are now carried through the reload with
  their scene objects, their verdicts are re-derived once on the other side, and everything else the
  pane derives is rebuilt rather than remembered. Scanning after every recompile is no longer part of
  using the view.

- **Find Targets In Your Scene no longer reports a count it does not list.** A scanned target whose
  object had since been deleted was skipped as each row was drawn, so it stayed in the result count
  while contributing nothing to the list — the section could claim *39 found* above a body that said
  nothing was found. A deleted target is now dropped from the results outright, once, so the count
  and the list can only ever be the same list. It also drew fewer controls than the pane had reserved,
  which is a layout mismatch Unity aborts the pass over.

- **A spoken answer is no longer rate-limited away.** `Convai Action Feedback Relay`'s cooldown
  (10 seconds by default) downgraded any narration inside its window to a silent context update. That
  is right for chatter and wrong for an answer: two questions asked six seconds apart produced one
  spoken answer and one silence, which is indistinguishable from a character that did not hear the
  second question. The cooldown still throttles everything else.

- **An answer that arrives while the character is talking is now delivered afterwards instead of
  being discarded.** The relay downgraded any narration to silent whenever `IsSpeaking` was true —
  and a character saying *"let me check…"* is speaking at exactly the moment a fast lookup finishes,
  so the answer was lost at the one moment it was most likely to be wanted. It is now held until the
  utterance ends and spoken then. An answer held longer than 20 seconds is delivered silently rather
  than volunteered into a conversation that has moved on, and nothing is held across a disabled relay
  or a scene change.

- **A later failure no longer erases an answer that already succeeded.** Batch feedback returned at
  the first hard failure and never looked at the steps around it, so *"tell me the generator's
  reading, then meet me at the door"* with a blocked door reported only the door. Answers produced
  before the failure are now reported alongside it, in the order they happened.

- **A character that speaks scripted lines no longer swallows an answer.** An authored success line
  has only an `{action}` token, so an action set to tell the player what it found would have said
  "There, all done." instead. Answers are spoken in the character's own words, and the Actions Editor
  says so on the action rather than leaving it to be discovered in play.

- **`Wait For Bot Speech` no longer promises something it does not do.** Its tooltip said the action
  was held until the Convai Character *finishes* speaking its reply; the gate has always also released
  when speech *starts*, so the action runs alongside the reply. The wording now matches the behaviour.
  Nothing about when actions run has changed.

- **A Convai Action Target placed in the scene is now something the character knows exists.** The
  component is documented as making any GameObject a target "drag-drop, no code required", behaving
  "exactly like an authored object", and the quick-start teaches it as step 4 — but the model was
  never told about it. It resolved perfectly inside Unity and the character answered *"that is not
  in our environment"*, because the mid-session sync that grounds the backend's prompt was staged
  only when the **runtime registry** was non-empty, and a `ConvaiActionTarget` deliberately does not
  join that registry: by design it keeps a polled list instead. Two correct designs with nothing
  connecting them.

  The sync is now staged when the character can act on something the connect payload did not carry,
  which is the question that was always meant — and it covers the registry, scene components and
  target groups together, because all three reach the merged config the same way. A scene whose
  targets are components rather than Scene Knowledge entries stops being invisible.

- **Destroying a character while an action batch is in flight no longer takes unrelated work with
  it.** A batch handed in from a background thread is marshalled to the main thread and runs a frame
  or more later, and the character can be gone by then — a scene change, a despawn, an object
  destroyed mid-conversation. A destroyed component keeps a live C# reference, so the queued work
  still ran and threw inside the scheduler, which then abandoned the rest of that frame's queue:
  callbacks belonging to objects that were perfectly alive simply did not happen. The dispatcher now
  checks that it still exists before touching anything.

- **`ConvaiActionDispatcher.EnqueueActions` can now actually be called from a background thread.**
  It is public API and a network callback, job or async continuation is an ordinary place to call it
  from, so the batch is snapshotted and marshalled to the main thread before anything reads scene
  state. Deciding *whether* to marshal, however, was itself a main-thread-only call — the check
  consulted `Application.isPlaying` — so a worker-thread caller threw before reaching the marshal
  that exists to prevent exactly that. The thread is now the only thing that decision looks at.

- **A reference whose kind is already known no longer resolves to the other kind.** When an action's
  parameter has been read as naming a character, re-resolving it at dispatch asks for a character —
  and if only an object of that name is left in the scene, the answer is now nothing rather than the
  object. The two ways of asking this resolver for a target have never meant the same thing: an
  action's Target Requirement is a *preference*, and the caller checks what kind came back, whereas a
  kind the SDK has already determined is a *constraint*. Folding both onto one ladder made the
  constraint behave like a preference, so an action accepting Either kind would have acted on a prop
  when it had been told the target was a person — the same class of silently-wrong target the ladder
  rework exists to close. Reachable whenever the scene changes between a command being admitted and
  being performed, which queuing and the speech gate both make ordinary.

- **A target described on a Convai Action Target now reaches the character when the Scene Knowledge
  entry of the same name has no description.** The component's object and interaction point already
  completed a blank authored entry; its description did not, so an author who wrote the sentence on
  the component and named the thing in Scene Knowledge sent the character nothing. A blank is not
  wording, so a later source may fill it — the same rule the binding beside it already followed.

- **The action simulator now reads values the way a conversation does.** `Convai_SimulateAction`
  cleaned the values you hand it without consulting the character's own objects and characters, so a
  target legitimately called `- Special` or `{Annex}` simulated as unreachable while working
  perfectly at runtime — a simulator sending you to look for a fault that was not there.

- **A command whose values only partly match a template shape no longer loses the rest.** When the
  Convai Character sent some slots in one shape and the remainder plainly — `{20} {80} Power
  Generator` for an action with two parameters and a target — the SDK took the two it recognised,
  filled the third with a blank and dropped the command for naming nothing, while the name sat in the
  text it had just stopped reading. Each way of splitting a command's values is now used only when it
  accounts for every slot the action offers, and hands over to the next when it does not. This is the
  mirror of the truncation fix below: there the reader had one value too many and discarded the last,
  here one too few and invented a blank.

- **A target named like punctuation survives being one of several values.** A bay called `{Annex}` or
  a room called `'Q'` kept its name when it was an action's only value and lost it when it was one of
  two — the check that protects real names ran on the whole text, which can only be one target's name
  when the action has one slot. Values are now checked individually, in the form they arrived in.
  Splitting on plain spaces remains the one repair this cannot protect, because there is no candidate
  to check: with two or more slots, a multi-word name has no marker saying where it ends.

- **An action command you build yourself keeps its own speech-gate settings.**
  `ConvaiActionDispatcher.EnqueueActions` now reads commands the same way the backend path does, and
  that reading was overwriting `WaitForBotSpeech` and `DelayAfterBotSpeechSeconds` with the action
  definition's values — so a command you had told to wait for the character to finish speaking
  started immediately. The definition supplies those only for commands arriving from Convai, which
  send no opinion about them; yours are yours.

- **A multi-parameter action no longer strips a `word:` prefix off a value that is a real name.** The
  rule that a repair to model output is only applied after the raw text has been checked against what
  the character can actually act on was held by the value cleaner but not by the stage that splits a
  blob on parameter-name anchors, which removes text just as surely. A single-slot action was
  covered; two or more were not.

- **An action that needs a target but declares no parameter for it no longer throws away the target
  it was sent.** Such an action is offered to Convai with an extra `{target: reference}` slot, so its
  template presents one more slot than the action declares parameters. The reader sized its split by
  the declared parameters, so when the Convai Character filled every slot in — which is exactly what
  it is asked to do — the last value was truncated away. The action was then dropped for having no
  target, and the explanation said the character "named nothing", while the name it had sent sat in
  the value that had just been discarded.

  Found by asking a character *"is the power generator reading between 20 and 80?"* against the live
  backend: it answered `Compare Reading {low: 20} {high: 80} {target: Power Generator}` — correct in
  every respect — and the command was dropped. Renderer and reader now take the slot list from the
  same place, so they cannot present different templates.

  Actions with two or more declared parameters and a target requirement were affected on every
  command. An action with one declared parameter, or none, was usually unaffected because the
  whitespace fallback happened to keep the trailing value.

- **A character's head no longer snaps between fixations.** Idle look-around, and the moment a
  curiosity glance released back to idle, moved by accelerating hard to a capped speed and braking
  to a stop — the harshest motion the speed and acceleration limits allowed, and worst on the
  smallest movements, which are also the ones a character makes most often. Deciding to look at
  something new is now its own kind of movement, with a gentle start and landing timed by how far
  it has to travel; everything that is not a decision to look elsewhere — following a moving
  target, holding steady under an animated pose — keeps the lag-free tracking that was already
  right and is untouched. The character's full-body turn toward a target, and the eased look-away
  used for aversion beats, get the same treatment. Reported building the Terminal demo, most
  visible in ambient exploration and on releasing a curiosity glance.

### Improvements

- Added a server-authentication integration guide covering existing player login systems, dynamic Unity auth-token providers, a game-server exchange endpoint, WebGL requirements, and deployment security checks. A runnable localhost-only standard-library Python server and Unity provider component demonstrate the complete local flow without putting the Convai API key or a shared demo credential in Unity.
- Auth Token room connections now use Convai's dedicated `API-AUTH-TOKEN` request header on Native and WebGL instead of presenting the short-lived token as an API key.

- **Scene Knowledge now says how an object gets known, instead of leaving it to be inferred.** The
  view offered two ways to tell a Convai Character about a scene — a written entry, or a Convai
  Action Target component on the object — and explained neither's relationship to the other, so
  *Find Targets In Your Scene* reading "39 found" next to a list of 18 entries looked like 21 things
  had gone missing. The scan now states its result in full (*"All 39 targets in your scene already
  reach this Convai Character — 17 through an entry above, 22 automatically"*), Known Objects says
  what an entry is actually for now that an object can introduce itself without one, and the rule
  that decides ties is written down where it applies: **an entry beats a component of the same name**,
  its description is what Convai receives, and an entry in that position is marked
  **Overrides target**. Until now the only way to discover that was to edit a component's description
  and wonder why nothing changed. `Documentation~/ACTIONS.md` gains a section covering the same
  ground for people not in front of the editor.

- **Find Targets In Your Scene can add every target the character cannot reach, in one step**, and
  the row names are clickable — a scan of forty rows no longer means hunting through the Hierarchy to
  find out which `Spot Light_01` a row meant. The bulk action deliberately touches only the targets
  that reach the character through neither channel: copying the ones that already work into entries
  would freeze their descriptions and quietly stop the live components from mattering. Names are
  de-duplicated against the existing lists and within the batch itself, because scenes really do hold
  several targets sharing one name and the backend rejects duplicates outright. It is one undo step,
  and it is not shown at all when there is nothing to add.

- **Sent To Convai now shows everything the Convai Character will know, not just the connect
  message.** The card's promise was that it "always matches what is actually sent", and it kept that
  promise about the wrong thing. Objects carrying a Convai Action Target component are deliberately
  not part of the connect payload — they arrive a beat later, in the sync that goes out once the
  conversation starts — so a scene with eighteen written entries and thirty-nine components previewed
  as **19** while the character ended up knowing **41**. Read literally, the card said the other
  twenty-two did not exist. The preview is now split into the two groups that actually exist,
  **Sent when this Convai Character connects** and **Added as soon as the conversation starts**, and
  the header reports both (`41 entries · 19 at connect`). The second group can only account for what
  the last scan found, so until you scan, the card says so rather than implying there is nothing
  there.

- **The Sent To Convai preview is readable at any size.** Every line used to be drawn in one style
  with indentation faked by spaces inside the string, so a heading, a name and its description were
  typographically identical and eighteen entries read as one paragraph. Names now carry their own
  weight with the description beneath them, each delivery group sits in its own panel, and both
  groups scroll inside a fixed height — the card stays the same size whether the character knows five
  things or five hundred, and nothing is elided to keep it that way.

- **Every section of the Actions Editor's Scene Knowledge view folds away, and says what it holds
  while folded.** A character that knows a dozen objects turned the view into a single scroll with no
  landmarks: reaching *Sent To Convai* meant scrolling past every entry, and there was no way to put
  a finished list out of sight. All six sections — Known Objects, Known Characters, Initial
  Attention, Find Targets In Your Scene, Add From Your Scene and Sent To Convai — are now collapsible,
  and each header carries a summary of its contents (`8 objects`, `12 found · 3 not known`,
  `9 entries`) so folding one away never hides the answer to *is there anything in there*. Expansion
  is remembered per section across editor restarts, like every other Convai section. Anything that
  adds to a folded list — a drag-and-drop onto it, or **Add** on a scan result — opens it, so nothing
  can be added behind a closed header. Folding still works while Play Mode has editing disabled;
  only the editing inside a section is switched off.

- **Explanatory copy in Convai's editor UI no longer clips its own last line.** A word-wrapped label
  in IMGUI is measured during the layout pass, before the pane's width is settled, so a paragraph
  that fits on one line at the width it *asked* for and needs two at the width it *got* — the
  difference is exactly a vertical scrollbar — reserved a single line and drew the second one through
  whatever sat above and below it. In the Scene Knowledge view this put the *Find Targets In Your
  Scene* explainer on top of its own section rule with its last line cut off. Paragraphs now reserve
  the height they are actually drawn at, through a new measured primitive in the editor design
  system that every Convai surface can use. It measures against the width the paragraph was last
  drawn at, keeps a little slack so a line ending flush against the right edge cannot measure as
  fitting and then wrap, and reports back when it settles a width so the window draws once more —
  which is what makes the first pass after a domain reload correct rather than correct-on-the-next-
  mouse-move.

- **The Convai menu is nine rows instead of seventeen, and every one of them goes somewhere new.**
  The menu had no owner: each feature added its own entry in its own file, every addition looked
  reasonable on its own, and what accumulated was a menu longer than most users' patience in which
  half the rows led back to places they had already been. Nothing was removed from the SDK — every
  tool below is still there, reached from the surface that owns its subject.
  - **The seven configuration rows are two.** *Welcome*, *Account*, *Settings*, *AI Coding Setup*,
    *Config Assets*, *Long Term Memory* and *Contact Us* all opened the same window at a different
    section of its own navigation rail — a menu that was a copy of a sidebar, and as long as one.
    **Convai → Convai Editor** opens the window; **Convai → Settings** stays, because an API key is
    the thing people come back for. The other five sections are one click further in, where the
    window already lists them. Their `ConvaiConfigurationWindowEditor` methods remain public, so
    tooling that opened a section directly is unaffected.
  - **There is one troubleshooter again.** *Action Troubleshooter* opened the same window as
    *Troubleshooter* — the Actions-only window had already been merged away, and the row survived as
    an alias. A second path to one destination is not muscle memory, it is a choice a user should
    never have been asked to make. **Convai → Troubleshooter** is it; the Actions Editor and the
    character inspector still open it expanded at Actions.
  - **Three measurement instruments moved next to what they measure.** *LipSync Drift Monitor* is
    now **Open Drift Monitor** in the Lip Sync component's *Streaming & Latency* section, beside the
    settings that cause the drift. *Emotion Timeline* is **Open Emotion Timeline** in the Emotion
    editor window's *Live* mode, which has already picked the character the timeline needs.
    *Body Animation → Analyze Selected Animation Set* is gone: the Animation Set's own inspector and
    the editor window's Content mode already ran it from a button that is visible at the moment you
    are looking at the set, and the menu row was greyed out until you had selected one anyway.
  - **`Convai → Embodiment` is now `Convai → Embodiment Editor`,** matching the four sibling rows it
    sits with, all of which name the window they open.
  - **Documentation moved off the GameObject menu.** *GameObject → Convai → Open Documentation* is
    **Convai → Documentation**; the GameObject menu creates and configures scene objects, and a
    documentation link is neither. *GameObject → Convai → Open SDK Settings* is gone, being a second
    path to **Convai → Settings**. The two entries that do act on the scene — *Setup Required
    Components* and *Validate Scene Setup* — stayed where they were.
  - **The Animation Set inspector's analyze button is called *Measure Clips*,** the same words the
    editor window uses for the same run. With the menu entry retired these two buttons are the only
    ways in, and two names made them look like two tools.
  - A new architecture guard fails the build if two Convai menu entries share a priority, if one
    command is offered under two paths, or if the menu grows past the length it was cut to.

- **The wire format actions travel in has one owner.** Rendering an action to the string sent to
  Convai is a grammar, and reading a command back is that grammar run backwards — but the two were
  written independently, and a third place recovered action names from rendered strings with its own
  copy of the delimiters. Every new rendering decision could therefore create a new way to be
  misread, somewhere else, silently. `ConvaiActionWireGrammar` now declares the tokens once, as
  data, and the renderer, the name reader, the response parser's name-boundary test and the
  wire-text repairs all take their delimiters from it. **Nothing sent to Convai changed** — the
  rendered string is byte-for-byte what it was.

- **Action names, parameter names and choices that the format cannot carry are now reported while
  you author them.** Nothing used to check that authored text avoided the grammar's own delimiters,
  so an action named `Sit - Chair` rendered as `Sit - Chair - <description>` and everything that
  recovered a name from that string read it as `Sit`: the availability filter and the mid-session
  config sync addressed an action nobody had defined, while the character was offered one it could
  never be asked to perform, with no diagnostic anywhere. The rule is per surface, not a blanket ban
  — a plain dash stays legal everywhere, so choices like `path-blocked` are unaffected. Two actions
  that render to the identical line are also reported, because nothing can tell those apart, the
  Convai Character included.

- **A name a request already matches is no longer rewritten on its way in.** The SDK repairs the
  decoration a model puts around values — a leading separator, wrapping quotes, a copied `{slot:}` —
  and each of those repairs could destroy a name that was real. A room called `'Q'`, a prop called
  `- Special`, a bay called `{Annex}` or a target called `Bay 2: North` simply stopped matching, and
  the action did nothing for a reason that looked correct in isolation. Every stage that can rewrite
  a value now checks the character's own vocabulary first, so a repair can only ever stop a wrong
  rewrite. Costs nothing when there was nothing to repair.

- **A name means the best match, not the first list it appears in.** Target resolution ran the whole
  object ladder and only then the whole character ladder, so the *fuzziest* match of one kind beat
  the *exact* match of the other: asked for `Sofia`, a scene holding a character named Sofia and an
  object named "Sofia's Statue" resolved to the statue — and logged it as a successful match, so it
  read as working. Resolution now walks rung by rung across both. Everything else about it is
  deliberately unchanged: objects are still considered before characters when an action accepts
  either, proximity still only separates two entries of the same kind, and the fuzzy rung still
  refuses to guess between several fits.

- **Commands you inject yourself are read the same way as commands from Convai.**
  `ConvaiActionDispatcher.EnqueueActions` now applies the same wire-text cleaning, parameter parsing
  and target resolution, and produces the same explanations when something will not work. It
  previously skipped all of that whenever the command's name happened to match a definition — which
  meant the SDK's own Test Run and Live tools exercised a different pipeline from a real
  conversation. Injected commands are explained, not refused: the step preconditions remain the gate
  they always were.

- **A target that changes while a command is waiting now says so.** A command is judged when it
  arrives and performed later — behind another batch, or behind the speech gate — and the scene does
  not stand still. Acting on the freshest answer is correct; doing it invisibly was not.

- **Runtime target registration no longer fails for actions whose first parameter carries a
  connector.** An action named `Walk` with a connector of `to` renders as
  `Walk to {destination: reference}`, whose recovered name is `Walk to` — which matched no
  definition. The availability filter treated that miss as "no opinion" and sent actions the author
  had disabled; the mid-session config reconciler treated it as "no local definition" and rejected
  the whole sync. Both now match the complete rendered string, which is exact.

- **A runtime target and a scene component of the same name no longer produce two entries.** The
  merge checked each source against the base configuration only, so a source could not see what the
  source before it had already contributed. Which of the two duplicates a command meant was then
  decided by where things happened to stand in the scene.

- **The Convai menu is nine rows instead of seventeen, and every one of them goes somewhere new.**
  The menu had no owner: each feature added its own entry in its own file, every addition looked
  reasonable on its own, and what accumulated was a menu longer than most users' patience in which
  half the rows led back to places they had already been. Nothing was removed from the SDK — every
  tool below is still there, reached from the surface that owns its subject.
  - **The seven configuration rows are two.** *Welcome*, *Account*, *Settings*, *AI Coding Setup*,
    *Config Assets*, *Long Term Memory* and *Contact Us* all opened the same window at a different
    section of its own navigation rail — a menu that was a copy of a sidebar, and as long as one.
    **Convai → Convai Editor** opens the window; **Convai → Settings** stays, because an API key is
    the thing people come back for. The other five sections are one click further in, where the
    window already lists them. Their `ConvaiConfigurationWindowEditor` methods remain public, so
    tooling that opened a section directly is unaffected.
  - **There is one troubleshooter again.** *Action Troubleshooter* opened the same window as
    *Troubleshooter* — the Actions-only window had already been merged away, and the row survived as
    an alias. A second path to one destination is not muscle memory, it is a choice a user should
    never have been asked to make. **Convai → Troubleshooter** is it; the Actions Editor and the
    character inspector still open it expanded at Actions.
  - **Three measurement instruments moved next to what they measure.** *LipSync Drift Monitor* is
    now **Open Drift Monitor** in the Lip Sync component's *Streaming & Latency* section, beside the
    settings that cause the drift. *Emotion Timeline* is **Open Emotion Timeline** in the Emotion
    editor window's *Live* mode, which has already picked the character the timeline needs.
    *Body Animation → Analyze Selected Animation Set* is gone: the Animation Set's own inspector and
    the editor window's Content mode already ran it from a button that is visible at the moment you
    are looking at the set, and the menu row was greyed out until you had selected one anyway.
  - **`Convai → Embodiment` is now `Convai → Embodiment Editor`,** matching the four sibling rows it
    sits with, all of which name the window they open.
  - **Documentation moved off the GameObject menu.** *GameObject → Convai → Open Documentation* is
    **Convai → Documentation**; the GameObject menu creates and configures scene objects, and a
    documentation link is neither. *GameObject → Convai → Open SDK Settings* is gone, being a second
    path to **Convai → Settings**. The two entries that do act on the scene — *Setup Required
    Components* and *Validate Scene Setup* — stayed where they were.
  - **The Animation Set inspector's analyze button is called *Measure Clips*,** the same words the
    editor window uses for the same run. With the menu entry retired these two buttons are the only
    ways in, and two names made them look like two tools.
  - A new architecture guard fails the build if two Convai menu entries share a priority, if one
    command is offered under two paths, or if the menu grows past the length it was cut to.

- **A look is now shared out across the body as one movement.** Gaze used to have no object that
  owned a gaze shift: the head solver, the eye solver and the body-turn director each decided how
  much of it to take, from their own angle threshold and their own timer, and nothing checked the
  three answers added up. When one backed off — the neck relaxing during a body turn — the slack
  landed on whichever stage still had room, in practice the eyes, against their limit. The
  character ended up looking at you out of the corner of its eyes with its head somewhere else.
  - **One ladder, in order: eyes → head → chest → feet.** Each part is handed only what the parts
    above it could not take, so the contributions always reconstruct the whole look. A part that
    cannot contribute passes its share on instead of swallowing it.
  - **One cascade, on one clock.** The head starts shortly after the eyes, the chest after the
    head, the feet last. These used to be three unrelated waits that could stack: arriving
    somewhere and turning to face you cost a 0.25 s head latency, then a 0.35 s turn hysteresis,
    before the turn's own response even began. They now read from a single shift clock.
  - **Arriving is one movement.** Handing over from the path a walking character watches to
    whatever is at the end of it no longer restarts the cascade, so there is no second freeze at
    exactly the moment the character reaches you.
  - **Eyes come back to centre.** Eyes held off-centre now recruit more head until they can
    return, instead of sitting at their limit for as long as the look lasts.
  - **The body turns because the neck is tired, not because a number was crossed.** A held head
    turn builds pressure that asks for the feet even when the character can already see what it is
    looking at — which is why people turn to face someone perfectly well in view. The old fixed
    68° tripwire could not tell a character whose head covered the look comfortably from one
    already pinned at its limit.

- **Deciding to look somewhere new and continuing to watch or hold on something are now handled as
  two different kinds of movement.** Settling on a fresh target — a new topic catching the
  character's attention, a curiosity glance releasing back to idle — travels there with a natural
  start, a travel and a landing, timed by how far it has to go. Tracking a moving target, or holding
  steady while an animation plays underneath, stays exactly as immediate and lag-free as before.
  Head, chest and the procedural full-body turn (used when no animated turn-in-place clip is
  available) all work this way now. See `Documentation~/GAZE.md`'s *How a look moves* section.

- **Gaze Profile → Head & Body gains a full set of movement-timing controls.** *Head Turn Time* and
  *Added Time Per Degree* set how long a look takes to reach a new target; the chest has its own,
  slower pair (*Chest Turn Time*, *Chest Added Time Per Degree*). *Movement Front-Loading* controls
  how much a look gets going quickly and eases into its landing rather than moving at an even pace.
  *New Look Starts Above* sets how far the aim has to jump before it counts as a decision to look
  elsewhere rather than the current look being nudged. *Idle Drift Slowdown* keeps ambient
  looking-around noticeably lazier than a purposeful look. *Head Speed Limit* and *Chest Speed
  Limit* are now a safety ceiling rather than the setting that decides how fast a look happens — a
  well-tuned character should never reach either one.

- **Gaze Profile → Head & Body → Follow Through.** The head chain no longer rotates as one rigid
  piece: the neck leads a turn and the head arrives a moment later and settles, which is the single
  most noticeable difference between a procedural head turn and a real one. (The chest leads both of
  them for a separate reason — it has its own, slower turn timing.) Set Follow Through to 0 to turn
  the neck and head back into one rigid piece.

- **Head Starts After The Eyes By defaults to 0.12 seconds, up from 0.06.** The shorter gap left too
  little room for the eyes-then-head cascade to read before the head arrived; the new default
  restores it.

- **Gaze and Body Language no longer keep separate write guards over the same bones.** Gaze's
  head and neck writes now go through the character's shared pose compositor, as its chest writes
  already did, so one guard owns the restore protocol for the whole chain. The previous split left
  two restore-once-per-frame protocols over an overlapping bone set on any character running both
  modules. Characters without Body Language are unaffected — with no compositor to share, gaze
  keeps its own guard.

- **A character running an errand for you stays engaged.** Conversation state was measured only in
  speech, so a character told to walk somewhere decayed to Idle partway through — and every
  behaviour keyed off the state followed, most visibly gaze, which stopped treating the player as
  present. The character arrived where you sent it and then looked at the wall. An action in
  flight now counts as engagement, and the normal idle decay resumes once it finishes. Ambient
  looking-around while genuinely idle is unchanged and remains the intended behaviour.

### Breaking Changes

- **`Convai.Infrastructure.Protocol.Messages.ActionResponsePayload` is removed.** It described the
  action-response envelope as a typed class, but nothing ever used it — the reader walks the payload
  directly — so it was a public type documenting a path that does not exist. It was also the second
  surface through which a payload could have set pipeline state, because it declared its items as
  the pipeline's own command type. Action responses are read through an internal two-field type
  matching what the backend actually sends.

  *Migration:* nothing to change unless you referenced the class by name. Action commands still
  arrive through `ConvaiCharacter.OnActionsReceived` and `ConvaiManager.Events.OnCharacterActionReceived`
  exactly as before; if you were deserializing the envelope yourself, read `name` and `target` from
  each entry of the `actions` array.

- **Gaze Profile: five settings replaced by the Head & Body ladder.** `headRecruitmentThreshold`,
  `headLatencySeconds`, `torsoActivationDegrees`, `bodyTurnYawThreshold` and `bodyTurnHoldSeconds`
  are removed. Three of them were waits that could stack and two were thresholds that could
  disagree; ladder depth and the onset cascade now come from one place.

  | Removed | Now |
  |---|---|
  | Head Joins In Above (`headRecruitmentThresholdDegrees`) | Head Joins In Above (`headEntryDegrees`) |
  | Head Lags The Eyes By (`headLatencySeconds`) | Head Starts After The Eyes By (`headOnsetSeconds`) |
  | Chest Joins In Above (`torsoActivationDegrees`) | Chest Joins In Above (`torsoEntryDegrees`) |
  | Turns The Body Beyond (`bodyTurnYawThresholdDegrees`) | Turns The Feet When This Much Is Left Over (`feetEntryDegrees`) — measured on what the head and chest could not cover, not on the raw angle |
  | Wait Before Committing To A Turn (`bodyTurnHoldSeconds`) | Feet Start After The Eyes By (`feetOnsetSeconds`) |

  New alongside them: `Eyes Get Tired Of Looking Sideways Past` and `Neck Gets Tired Of Staying
  Turned Past`.

- **Gaze Profile: `Head Snappiness` and `Chest Snappiness` are removed.** They configured the
  spring that no longer exists. Head speed is still authored (`Fastest Head Turn`); the rest is
  fixed biomechanics.

### Bug Fixes

- **A character finishing a walk no longer cranes its neck at you while it is still stopping.**
  The look point hands over from the path to whatever is at the end of it before the character
  has actually stopped, and while it is walking the movement system owns its facing — so the head
  stretched to its full range at a target the body was not yet free to turn toward, and held it
  there until the stop finished. The actuator ladder now applies a general rule: **a part of the
  body must not take a share that only exists because the part below it is blocked.** While
  something else owns the character's facing, the head stops at what the neck can hold
  comfortably and the eyes carry the rest, then the body turns properly once it is free.

- **A character arriving somewhere no longer ducks its head at its own feet.** While walking, the
  character periodically glances at what the journey is about. That subject is reported as a
  transform position — the player's root for a companion, which is their feet rather than their
  eyes, and a point on the floor for a destination — so the glance aimed at the ground. It got
  worse the closer the character came: the angle steepens to roughly 57° down at a metre, while
  the glance cadence simultaneously tightens on approach. The last thing a character did before
  stopping was repeatedly bow its neck toward its own feet and lift it again.
  - **A subject below the character's eye line is now looked at level**, the same correction the
    player anchor already applies to a non-camera anchor. One-sided on purpose: a subject above
    the eye line is left alone, so a character still looks up at a high shelf.
  - **A journey that has arrived stops checking in.** Tightening the cadence on approach is right
    for a door you are walking toward and wrong once you are standing in it. Following someone is
    a journey with no known end, so a companion is still checked on however close they are.
  - **The path a walking character watches is now placed at its eye line rather than at its head
    bone**, removing a standing downward bias of about a degree on everything it looked at while
    travelling.

- **A character turning to face you no longer arrives with its chin down and its eyes rolled up.**
  Four defects in the gaze head chain stacked into one very visible pose, most obviously at the end
  of a Walk To or Follow Me.
  - **The head's aim rotation was built with the wrong operand order.** The composition meaning
    "yaw, then pitch about the yawed right axis" is `Y·X`, written as
    `AngleAxis(-pitch, Y·right) · Y`; the code had the yawed-right pitch on the right instead,
    making it `Y²·X·Y⁻¹`. The head therefore aimed somewhere other than where the solver asked —
    3.7° off at 40° yaw / 15° pitch, 14.8° off at the chain's clamp corner.
  - **Splitting that rotation across the neck and head bones left a parasitic roll.** Yaw and pitch
    do not commute, so building each bone's share from a scaled yaw/pitch pair does not recompose
    to the whole; the residual landed on the head as a sideways tilt of up to 16.6°, appearing only
    when yaw and pitch were both non-zero — i.e. exactly while turning toward an off-axis target.
    The share is now a slerp along the aim's own axis, which recomposes exactly at any share. The
    same fix applies to the chest / upper-chest split of a torso aim.
  - **The head's stabilization reflex was being switched off precisely when it was needed.** The
    solver cancels the animation's own head deviation so the eyes do not have to counter-stare
    against a talk or walk clip's head bow. That reflex was being scaled by the body-turn relief and
    by head contribution along with the voluntary aim, compounding to 0.245 during a body turn and
    leaving 76% of the clip's bow on screen. It is now composed additively after both, gated only by
    engagement and the new `Keep Head Level While Looking` setting.
  - **The same reflex was disabled entirely during the head-latency window** (0.25 s after every
    re-target, including the hand-off from the path ahead to the player on arrival), because the
    solver returned before reaching it. The window now holds the voluntary aim only.
  - **Head aim is measured from the eye line rather than the head bone.** The head bone sits about
    7 cm behind and below the eyes, so aiming it at the target overshot the eyes past it and left
    them holding a permanent counter-offset — roughly 2.7° at 1.5 m, more at conversational range.
  - **A head-tilt gesture now rolls about where the head is looking**, not about the character's
    neutral forward, so a tilt applied to an already-turned head is a tilt rather than a tilt mixed
    with yaw.

### Improvements

- **The Character Rig component now answers the question it is opened to answer.** This component is
  added for you and, on a standard character, never edited — so the thing a user wants from it is
  "did Convai recognise this character?", not a form to fill in. The header now reports that
  directly, in a sentence, with a Ready or Needs Attention pill beside it; the two resolution tables
  come next with their counts on the section headers; and the fields only a custom rig needs sit
  below them, collapsed but one click away. Previously the first thing on screen was eighteen empty
  bone fields, and the detection result was three sections further down.
  - **"Convention Override: Unknown" is now "Rig Type: Detect automatically (recommended)".** The
    stored value is untouched — only its wording. `Unknown` is the state of the *override*, not of
    the rig, but as the first line of the component it read as a fault on every character Convai had
    recognised perfectly well. When it is left automatic, the type Convai settled on is shown beside
    the readings above it.
  - **Detection confidence is banded rather than raw.** `0.79` asks the reader to know what a good
    score is; it now reads `Strong — 79% of this type's marker shapes were found`, and a rig type you
    set yourself reads `Set by you` rather than a fabricated 100% match. A weak match says so in a
    warning box instead of leaving the number to be interpreted.
  - **Unresolved rows are visible as unresolved.** Every entry in both tables used to be the same
    grey, so a missing bone read like a present one. Missing rows are now amber, resolved bones say
    when they came from something other than the avatar — an assignment you made, or a name match
    that a rename can silently break — and the header escalates only for the bones whose absence
    actually stops something, so the pill is not permanently amber over an optional Upper Chest.
  - Opening the component on a freshly loaded scene no longer reports an unrecognised rig at zero
    confidence, and no longer writes a console warning per gap every time you touch a field.
  - `Capture Current Resolved Bones As Overrides` is now `Lock In What Convai Found`, and
    `Rebuild Resolution Tables` is now `Re-scan This Character`.

- **The rig component is called Character Rig everywhere it is named.** Its Add Component entry has
  always read **Convai → Embodiment → Character Rig**, but its own inspector title and thirteen
  messages across Gaze, Body Language, the setup troubleshooter and the troubleshooting guide called
  it Standard Rig Binding — the class name. A user told to "add a Standard Rig Binding" had nothing
  by that name to add. Only the wording changes; no type, menu path or API is renamed.

- **Facial composition now recognises the naming the shipped rigs actually use.** Blendshapes are
  sorted into face regions by name pattern, and each region decides how strongly emotion and lip
  sync drive it. Separators are ignored when matching, but order is not — and Character Creator
  writes the side into the *middle* of a name, so `Eye_L_Look_Up` never matched the `Eye_Look`
  pattern that exists to catch it. Measured against Convai's own lip-sync maps, every eye-look
  target on a CC rig fell through to the leftover region, as did `Jaw_L`, `Jaw_R`, `Jaw_Up`,
  `Jaw_Down` and `Jaw_Backward`, which the jaw list simply never named. That is not cosmetic: the
  leftover region composes lip sync at `0.3` where Jaw composes it at `1`, so directional jaw
  movement drove at under a third of its intended strength while speaking. Eyelashes now count as
  part of the eye rather than as leftovers. ARKit rigs are unaffected — their spelling already
  matched, and the abbreviated jaw patterns cover both conventions.
  - A guard test now compares every serialized field of the shipped facial composition profile
    against the built-in default the compositor uses when none is assigned, so the two cannot drift
    into composing a face differently with nothing saying why.

- **A Body Animation config you make yourself is no longer a different character from the ones
  Convai ships.** Four talk values had been tuned by hand on the shipped settings and never brought
  back to the defaults, so **Create → Convai → Embodiment → Body Animation Config** produced a
  character that resembled nothing in the SDK. The talk overlay could reach full weight against the
  shipped `0.45` — the control whose own tooltip says lower values keep more of the idle pose under
  speech gestures — and the talk layer held `0.65` through silence against the shipped `0.2`. A
  hand-made config gestured roughly twice as hard as every Convai sample, and nothing in the
  Inspector said why. The fade-out and release-hold timings had drifted with them. All four now
  default to what Convai ships, and a guard test compares *every* serialized field of the shipped
  config against a freshly created one, so the next divergence fails the build with the field named
  rather than reaching a customer.
  - **The Body Animation archetype picker no longer highlights an archetype the config has left.**
    Applying one wrote three values but recognising one compared only two, so a config whose idle
    activity timing had been retimed still reported itself as untouched. Setting an archetype and
    being one are now the same list of fields, and a guard test holds the shipped config to reading
    as exactly one archetype — the SDK default — so the picker can never come up blank or ambiguous
    on Convai's own character.
  - **The same correction in Gaze, across nine values.** Applying a gaze personality writes the
    state table *and* the blink rate, ambient look-around, face-scan radius and planning-break
    chance beneath it; recognising one compared the state table alone. Retune the blink rate and the
    profile still lit that personality's pill and still showed its description — and because the
    picker ignores a click on the pill already lit, there was no way to put those nine values back.

- **Two gaze personalities stopped staring.** A gaze state carries both a way of breaking eye
  contact and how much of it, and the runtime discards the amount when the way is set to *None* —
  so the two settings silently void each other. Six rows across four personalities shipped with a
  graded amount (`0.03`, `0.05`, `0.08`, `0.1`) behind *None*, and none of it ever ran. **Confident**,
  whose description promises "minimal aversion", held unbroken eye contact through six of its eight
  conversational states; **Attentive** did the same while attending and listening. Each of those
  personalities also writes *None* with exactly `0` elsewhere — Stoic six times, Confident five — so
  these read as a missed setting rather than a leftover number, and the amounts now run. A guard
  test rejects the contradiction in both directions, on the personality table and on the profile
  assets Convai ships.

- **Characters now actually look friendly when they are supposed to.** Every character type's
  resting face was tuned so low that it could not be seen. Resting intensity drives the smile
  shapes directly — `MouthSmileLeft`/`MouthSmileRight` at full weights 68/71 through a linear
  curve — so **Warm**'s shipped `joy` at `0.22` put roughly fifteen units of smile on a
  hundred-unit blendshape: measurable, invisible. Picking the type that Convai documents as
  "approachable and easy to read" produced a character that read as blank, and the only way to fix
  it was to find the resting-strength slider and guess a number.
  - **Warm** now rests at `joy 0.55` and **Energetic** at `joy 0.6`. Energetic — "host, tour guide,
    streamer" — had shipped at `0.12`, making the loudest character type the flattest one in the
    SDK at rest: its `1.6` joy amplification only ever reached incoming server emotions, never the
    resting face, so it looked lively while speaking and blank the moment it stopped.
  - **Composed** ("receptionist, clerk, guide") now rests on a faint `trust` at `0.15` instead of on
    absolute nothing. That recipe is a closed-lip pleasantness — civility rather than cheer — and it
    is also what a character falls back to when no personality is assigned at all.
  - **Reserved** still rests at nothing, deliberately. The four types now form a legible ladder:
    nothing, civility, warmth, open cheer.
  - Choosing a resting mood anywhere — the personality Inspector, a character's own **This character
    rests at** override, the Setup Doctor's fixes, and the Convai MCP tools — now seeds a strength
    that visibly lands instead of `0.22`/`0.25`. All five surfaces read one shared value, so they
    can no longer disagree about what a usable resting mood is.
  - The LipSync sample character follows the Warm type again rather than carrying a private copy of
    the built-in expression library: 1,224 lines of duplicated recipe data that had frozen her out
    of every future improvement to it, for behaviour identical to leaving it empty.

- **An emotion is now always picked from a list, never typed.** Settings that name an emotion — the
  personality's **Resting Mood** and per-emotion overrides, a character's own resting-mood override
  and held expression, the mood after an action succeeds or fails, expression recipes, material
  binding slots, the **Set Mood** and **React** behaviors, and the per-emotion modifiers in Body
  Animation, Body Language and Gaze — were text boxes. A name only does something when the
  character's vocabulary defines it, so every one of them was a setting that looked configured and
  silently did nothing the moment it was misspelled. They are all dropdowns of that character's own
  emotions now, written the way a person writes them (`Joy`, not `joy`), and the same list is used
  everywhere so one emotion is never shown two ways.
  - A stored name the vocabulary no longer defines stays selected and is marked
    **(not in this character's vocabulary)** — opening an Inspector never rewrites authored data,
    and the setting says plainly why it is inert.
  - **Fixed a dead default the dropdown exposed:** a character's mood after an action succeeded or
    failed shipped as `satisfied` / `frustrated` — two words no emotion vocabulary in the SDK
    defines, so the feature resolved nothing and silently did nothing for everyone who turned it on.
    They are now `joy` and `sadness`, which exist.
  - **Create → Convai → Embodiment → Emotion Taxonomy** now starts as a copy of the built-in
    vocabulary instead of an empty asset, so authoring your own emotions for your art direction is
    renaming and adding rather than starting from nothing — and every dropdown in the SDK follows it.

- **You can now see and change a character's profile from the Inspector, on every module that has
  one.** Gaze and Body Animation showed no profile field at all once one was assigned: the only
  entry point was a button that *creates* a new asset, so picking one of the profiles that ship with
  the SDK meant switching to the Debug inspector or hand-editing the scene file. Emotion had a field
  but filed it under **Advanced**, a section that starts collapsed, while printing the asset's name
  into a header there was nothing to click. Body Language's was a plain card at the very bottom of
  the inspector, and Conversation Flow's said nothing about its state. All five now agree:
  - The profile is the **first row** of the section that tunes it — Emotion and Gaze **Personality**,
    Body Language and Conversation Flow **Profile**, Body Animation **Content** — and it is drawn
    whether or not one is assigned, so seeing it, swapping it and clearing it are the same gesture.
  - Every one of those section headers reports which asset the character is on, or **SDK defaults**
    when it has none, so a collapsed section still answers the question.
  - Body Language's profile moved up under the setup checklist instead of sitting below two other
    cards, and Body Animation's routing fields moved above the clip counts they explain.

- **Gaze now warns before you re-tune every character sharing a personality.** The Gaze dials write
  into the profile asset, and Gaze was the one module with live controls and no ownership notice — so
  moving **Eye contact** on a shared profile silently changed every character using it, with nothing
  on screen saying so. It now draws the same notice Emotion and Body Animation already did, naming
  how many characters are affected and offering **Make Unique For This Character**. A profile that
  ships inside the Convai package is still copied into your project on the first change rather than
  written to in place, and says nothing beforehand, because there is nothing for you to do.

- **The Gaze personality row now looks like the other modules'.** Gaze drew its personality presets as tinted mini-buttons while Body Animation and Emotion used the design system's segmented picker, so the same control read differently across three inspectors on the same character. Gaze now uses the shared picker, and its caption reads **Character Type** instead of repeating the section's own "Personality" heading. Applying a preset is also confirmed after the draw pass, as the other two already did.

### Bug Fixes

- **Fixed an action that needs a target being offered to the Convai Character with nowhere to put
  one.** An action's target requirement was known to the SDK and never said on the wire, so an
  action that declares no parameters of its own — the ordinary shape of "walk to somewhere" —
  reached the character as a bare name. The prose description asked for a place; the template
  offered no slot to put one in.
  - What that produces looks like a backend fault and is intermittent by nature: the character
    picks the action, names the destination in what it says out loud — *"I'll head to the
    Gallery."* — and sends the command with an empty target, because nothing in the shape it was
    given suggested the destination belonged in the command. Ask for a second place in the same
    session and it fails every time.
  - The evidence was already on screen: an action with a declared parameter, `Follow Me {mode:
    choice [follow|stop]}`, was filled in reliably every turn. Same model, same session — the slot
    is what it responds to.
  - Such an action is now offered as `Walk To {target: reference} - …`. The slot is named `target`
    deliberately: that is the key enrichment already parks an inline target under, so a filled slot
    and a name arriving inside the action text land in exactly the same place. An action whose own
    parameter can already carry the target keeps that one slot — two slots asking for the same
    thing invite the character to split it across both.

- **Fixed a Convai Action Target's alternate names being thrown away by the setup the tooling
  recommends.** Writing a name in Scene Knowledge and putting a Convai Action Target of that name
  on the object is the flow `Convai_ConfigureActions` asks for — and whenever the Scene Knowledge
  entry already carried its own object, every alias on the component was silently discarded and the
  pair was reported as a name collision. Alternate names are not two answers to one question, they
  are more ways to ask it, so they now always merge. A collision is now only reported when the two
  entries genuinely point at **different** objects, which is the only case where one of them can
  never be reached — and it is a warning rather than a debug line, because the symptom is a
  character acting confidently on the wrong thing.

- **Convai_SimulateAction no longer rejects values the live character accepts.** The editor
  simulator trimmed a parameter where production also strips the quotes a model wraps values in, so
  a `Choice` sent as `'follow'` failed in the tool and worked in the game. A simulator stricter than
  the thing it simulates is worse than no simulator: it sends whoever trusted it looking for a fault
  that is not there.

- **Fixed an authored target name being rewritten by the cleanup meant for the Convai Character's
  replies.** Stripping the quotes a model wraps values in, and the separator it echoes back off the
  template it was shown, are repairs to how a model writes — but they lived in the shared string
  helper every name passes through, so they also ran over names typed into an inspector, and over
  every `Clone` and `ToString` on the way past. A target legitimately called `- Special` or `"Q"`
  could not be called by its own name. The cleanup now happens once, where the reply is read, and
  nowhere else.
  - **An alias typed with a stray space now matches.** It never did, and nothing showed it: the
    padding is invisible in the inspector, so the alias simply did no work.
  - Both are also cheaper. The shared helper is a single scan that returns the original string when
    there is nothing to trim, which matters because it runs on every candidate at every rung of the
    resolution ladder — its cost was multiplied by the number of targets in the scene.

- **Fixed a command being admitted on the strength of one target and then performed on another.**
  Deciding whether a command could run and deciding what it should act on were two separate copies
  of the same resolution ladder, and they disagreed in three ways: the admission check passed no
  origin, so with two same-named targets it judged the first while the dispatcher walked to the
  nearest; it demanded a scene binding where the dispatcher did not; and a target-less action was
  short-circuited by one and resolved opportunistically by the other. There is now one ladder and
  both callers climb it, so the two cannot disagree by construction rather than by discipline.
  - **An entry with nothing in the scene behind it no longer shadows the real object.** Writing a
    name in Scene Knowledge *and* putting a Convai Action Target of that name on the object it
    describes is the obvious way to set one up — and whichever the ladder met first used to win, so
    half the time a request resolved to the entry with nothing behind it while the object stood
    right there. A bound entry now always beats an unbound one; among equals the nearest still wins.
  - **Three ways of failing to find a target now read differently**, because they have opposite
    fixes: a name that matches nothing (add an alias), a name that matches the wrong sort of thing
    ("this character does have something by that name — but it is an object and the action needs a
    person"), and a name that matches an entry with nothing built behind it (link a GameObject, or
    tick Text Only if that is deliberate).

- **A Convai Character that appears to ignore what it was asked now says why, to you.** An action
  command could be discarded at seven different points before it reached an Action Behavior, and six
  of them produced nothing at all: no error, no spoken failure, no row in the Actions editor. The
  character talked normally and simply did not act, which is the hardest possible symptom to trace
  because it is indistinguishable from the Convai Character never having chosen an action. Every one
  of those points now explains itself in a sentence that names the action, what it asked for, why
  that failed and what to do about it — an unknown action name lists what *can* run, an unresolvable
  target lists what is on offer, and a batch discarded while the character was busy names the batch
  policy that discarded it.
  - **The Actions editor's Live view shows them.** Its session collector only ever listened to the
    dispatcher, and a dropped command never reaches the dispatcher — so for this failure mode the
    window was reliably empty. A new **Commands That Never Ran** card lists them as they happen.
  - **"The Convai Character chose no action" is now distinguishable from "it chose one and the SDK
    dropped it."** The two look identical from outside and need opposite investigations; the first
    is reported once per character, in its own words.
  - **Ambiguous targets are a warning rather than a debug line.** The resolution ladder refuses to
    guess between two candidates and resolves to nothing, which reads exactly like every other
    silent drop unless it says so. It now names the candidates and suggests the alias that would
    separate them.
  - **A new `Actions` log category** carries all of it, so action diagnostics can be turned up while
    diagnosing and down for a build without silencing everything else a character reports. Nothing
    is formatted unless something is listening.

- **Optional: a Convai Character can now admit that it could not do something.** `Convai Action
  Feedback Relay` gained **Dropped Command Feedback**, using the same modes, cooldown and authored
  lines as its existing failure feedback. It is **Off** by default and that is deliberate — a
  shipped character that announces every dropped command subjects players to a stream of apologies
  for a fault only the developer can fix. Turn it on when silence is the worse failure: a companion
  that appears to ignore a direct request reads as broken, while one that says it cannot do that
  reads as honest. `Silent Context` is often the right middle setting — the character's own model of
  the world learns the thing did not happen without anyone being told out loud.

- **Fixed a target the model wrote as a filled-in template slot being dropped.** An action is
  presented as `Name {target: reference} - description`, and a model asked to use it fills the slot
  *in place*, answering `{target: "The Gallery"}`. The braces and the slot's own name are template
  syntax rather than part of the name being asked for, so the command was dropped as targetless
  while carrying a perfectly good target. A single slot wrapping the whole value is now unwrapped,
  and its name dropped when it looks like one — a lone unspaced word followed by a colon. A value
  that merely contains a colon (`Bay 2: North`) keeps it, and a multi-slot parameter blob
  (`{Cube} {2.5} {yes}`) is left untouched, since unwrapping its outer braces would splice the first
  field onto the last.

- **Fixed every action that takes a target being silently dropped.** Three faults stacked into one
  symptom: a character that chatted normally, ran its targetless actions perfectly, and did nothing
  at all when asked to act on a place, prop or person — with no error, no spoken failure, and no
  step in any of the action tooling, because the command was discarded before it reached a handler.
  - **A registered target could never resolve.** `Available` defaulted through a property
    initializer, and Unity rebuilds `[Serializable]` instances field by field without running a
    constructor — so every target loaded from a scene came back unavailable, and the resolution
    ladder skips unavailable entries at every rung. It is now stored inverted, so the zero value
    means available, which is the only encoding that survives that deserialization.
  - **A target sent inside the action text was extracted and then ignored.** The backend often
    sends `Walk To The Gallery` rather than a separate target field; enrichment strips the action
    name and parks the rest under the implicit target key, but the required-target check and the
    dispatcher's own resolution both only looked at parameters the action itself declared. An
    action with no declared parameters — the ordinary shape of "walk to somewhere" — therefore had
    its target read, stored, and never consulted. Both now consult the implicit target.
  - **The template's own separator arrived glued to the target.** Actions are presented as
    `Name - description`, and models complete the pattern they are shown, answering
    `Walk To - The Gallery`. With the action name stripped, `- The Gallery` matched nothing. A
    leading separator followed by whitespace is now removed (a dash with no space, as in `X-Ray`,
    is left alone).
  - **Dropped commands now say what they wanted.** The only trace used to be an aggregate count
    (`rejected=1, reasons=required_target_unresolved:1`), which names the category but never the
    target — so the fault could not be diagnosed from a log at all. A dropped command now reports
    the name it asked for and what was on offer, and distinguishes "asked for something unknown"
    from "asked for nothing", because those need opposite fixes.

- **Fixed quoted parameter values being treated as wrong values.** Models quote what they emit and
  the wire quotes it again, so a `Choice` parameter authored as `follow|stop` arrived as `'follow'`
  and matched nothing. The parameter then fell back to its authored default, a quoted `Number` read
  as `0`, and a quoted `Reference` resolved to no target — each of them silently, because the only
  symptom was a console line saying the value `''follow''` "is not in authored choices", which reads
  as a formatting artefact rather than as the fault. Action names, targets and parameter values now
  have matching enclosing quotes stripped, including repeated and typographic ones. A quote that is
  part of the text — an apostrophe inside a name, a one-sided quote — is left exactly as it came.

- **Fixed the character following where the player *started* instead of where they are.** Actions
  that address the player without a backend target — Follow The Player, and the Gaze module's Watch
  Player — read the transform carrying `ConvaiPlayer`. First-person rigs, including Unity's own
  Starter Assets, put the `CharacterController` on a capsule *inside* the rig and move that, leaving
  the prefab root parked at the spawn point for the whole session. So a character asked to follow
  agreed, said so, walked to wherever the player stood when the scene loaded, and then ignored them
  — with nothing failing and nothing logged. The player's position now resolves to the body that
  actually moves (`CharacterController`, then `Rigidbody`, then the component's own transform). A rig
  that already carries `ConvaiPlayer` on the moving object resolves to exactly the same transform as
  before, so nothing needs rewiring.

- **Fixed a following character refusing to walk anywhere else.** Follow The Player is the one
  behavior whose effect deliberately outlives the action that started it, so it was still re-aiming
  four times a second while every later action arrived — and each of those re-aims overwrote the
  destination. Ask a following character to walk to the far side of the room and she took two steps
  and came straight back; Walk To, Lead Me To, Return To Start and every other move were all
  unusable for as long as she was following, and none of them reported a failure, because from the
  action's point of view the move had been accepted. Following now stands down for the duration of
  any move it did not order and falls back in beside the player when that move ends, arrived or
  interrupted. Nothing needs reconfiguring, and the follow is not cancelled by the errand — being
  asked to fetch something is an interruption, not a withdrawal of "come with me".

- **Fixed the Convai editor windows losing their layout — "EndLayoutGroup: BeginLayoutGroup must be
  called first", "Invalid GUILayout state", and section cards drawn sideways instead of stacked.**
  Cards and nested panels are opened with a `using` scope whose struct took every argument as
  optional. C# does not treat a struct constructor with only optional parameters as the parameterless
  one, so `new CardScope()` compiled, ran no constructor at all and produced the zero-initialised
  value: the card was never opened, while disposing it still closed one. All thirty-one call sites
  across the SDK therefore drew no card or panel frame and closed a layout group they had never
  opened, draining the layout stack one group per card until it hit zero and the window collapsed.
  The Emotions window showed it first because it stacks nine section cards. Scopes now open through
  `ConvaiEditorFrame.Card()` / `.Panel()` / `.TableHeader()` and `ConvaiEditorSections.Body()`, the
  silent form no longer compiles, and a guard test fails the build if a disposable scope struct
  becomes constructible with no arguments again. Section cards and panels draw their frames again as
  a result.

- **Removed the black line running across the character list in the Convai windows.** The tab row
  that switches the Gaze, Body Animation and Emotions windows between their views sits inside the
  right-hand pane, but drew its underline from the window's left edge — so the line carried on
  behind the character list beside it and read as a hard seam cutting through the character card
  rather than as the underline of those tabs. The underline now spans only the tab row it belongs
  to. The Actions window, whose tab row does own the full width, is unchanged.

### Improvements

- **One window for everything a character's behaviour modules share.** `Convai → Embodiment` opens a view of the character's modules together — which are present, which are blocked, and what each one is contributing — beside the preset that installs them. The embodiment preset and preset library get real inspectors rather than Unity's default field list, the components a module adds for you explain why they are there instead of appearing unannounced, and a character in motion gets a Travel Intent inspector that names what it believes it is doing and where that belief came from — the one thing a project driving movement with its own code had no way to see.
- **The built-in lip sync content now ships with the module that owns it.** The default viseme maps moved from the shared sample folder into the LipSync module, and the three built-in profiles are constructed in code rather than loaded from assets whose entire content was three strings. A project that put its own registry under `Resources/LipSync/ProfileRegistries/` is unaffected — that path is still scanned, and the maps kept their identifiers, so scenes and prefabs pointing at them resolve exactly as before.
- **Conversation Flow is named like the rest of the embodiment modules.** Its **Add Component** entry reads `Convai/Embodiment/Conversation Flow` rather than `Convai/Embodiment/Conversation Flow Controller`, and the module now declares itself to the embodiment catalog — which it never had, so its module id resolved to nothing and an embodiment preset naming it did nothing at all, silently. The type name is unchanged, so no code or existing scene is affected.
- **The Actions documentation is five pages instead of two.** `ACTIONS.md` is now a hub — the concept, the model, the runtime flow, and a ten-minute path to one working action — with `ACTIONS-CATALOG.md` (every built-in behavior, what it needs, how it fails), `ACTIONS-AUTHORING.md` (the Actions Editor, Action Sets, scene knowledge, targets, the Troubleshooter) and `ACTIONS-EXTENDING.md` (writing your own behavior, the backend wire contract, every public type) beside it.
- **Settings that ship with the Convai SDK are no longer a dead end.** A settings asset from inside the package cannot be written to in a normally installed project, so an edit to one was refused outright — or, in a project where the package happens to be embedded, accepted and then lost to the next SDK update. The SDK now decides who owns a settings asset in exactly one place, and gives the same answer everywhere:
  - A settings asset opened on its own in the Project window is honestly read-only, and offers **Create A Project Copy** instead of leaving controls live and futile. The copy lands under `Assets/Convai/`, is selected for you, and is yours to assign wherever you like. The LipSync map editor is the first surface covered: selecting a shipped map and pressing Auto-detect previously rewrote it.
  - The same question is now answered in one place for every Convai module, so the answer cannot drift: two modules previously carried their own version of it and had already diverged, and three did not ask it at all.
- **A personality created from the Create menu now behaves like one the setup button makes.** Emotion mixing and the small-movement layer defaulted off on the serialized fields while every character type turns both on, so a personality authored by hand was quietly the flattest one in the project and nothing said why. Both now default on, and a guard test fails if the two creation paths ever disagree again. Existing personality assets are unaffected — a serialized value already on disk is what it says it is.
- The Emotion component's **Add Component** entry reads `Convai/Embodiment/Emotion` rather than `Convai/Embodiment/Emotion Controller`, naming the behaviour being added rather than the class behind it. The type name is unchanged, so no code or existing scene is affected.
- **Every Convai inspector, asset editor and editor window now draws from one design system.** Colours, styles, section marks and collapsible-section state had been defined in several places at once, so surfaces meant to look like one product drifted apart in padding, tint, type size and hover behaviour — and the oldest of them hardcoded dark greys that rendered as dark blocks in Unity's Light editor skin. Every Convai editor surface now follows the editor skin, and guard tests fail the build on a second palette, a second style cache, a second section-state store, a colour literal, a locally allocated `GUIStyle`, or an icon spelled at a call site. Collapsed sections keep their state across the change, except on the Vision components, whose foldouts were stored under a separate key and reset once on upgrade.
- Convai components that previously showed Unity's default grey inspector — among them the Convai Manager, Convai Settings, the transcript and session event relays, push-to-talk and audio output — now have designed panels with headers, grouped sections and explanations, so a scene no longer mixes designed and undesigned Convai components.
- Editor messages that stated a problem without a remedy now say what to do next: a Character ID with stray spaces says to delete them, one of the wrong length says where to copy the whole ID from, and the Room Manager's unavailable turn-taking and connection sections say how to rebuild them. The Narrative Design template-key list and the transcript relay's character filter now explain their format and give an example.
- Editor tooling resolves object ids through an editor-side compatibility seam, so an id handed back after a domain reload or scene change still resolves when Unity has unloaded the asset it names. This affects the MCP tools and the scene setup API, which previously reported such an asset as missing.
- Defined and implemented the character-audio, canonical transcript, shipped transcript presentation, and LipSync consequences of every background policy, including an observable WebGL fallback from `PauseTimeline` to `MuteButCatchUp`.
- Version-sensitive Unity APIs now go through two compatibility seams, `ConvaiObjectFind` and `ConvaiObjectId`, so no call site has to know which object-search or object-identity overloads the running editor offers. Guard tests fail the build if a version-sensitive API is reintroduced directly, or if a shipped page states an editor version that contradicts the manifest.
- Documentation now states the minimum editor precisely. `SETUP.md` and `README.md` previously said "Unity 6.0 or newer", which reads as any 6000.0 patch. Package Manager has always enforced the exact build, so a user on an earlier 6000.0 patch was refused an install the documentation had promised them.
- **The package is 84 MB smaller.** A dialogue-animation clip library that nothing had referenced since the module was retired, and a facial-animation profile carrying 993 243 lines of empty slots, are gone. The Lip Sync Sample itself drops by about 101 MB with the new character; what always installs with the package grows by about 17 MB, because the Body Animation clips now ship rather than being sourced separately.
- **Shipped sample profiles no longer force themselves into your build.** The Embodiment and Lip Sync profile assets moved out of `SamplesShared/Resources/` into `SamplesShared/Profiles/`. Anything under a `Resources/` folder is included in every build whether or not the scene uses it; these assets are looked up by reference, so the folder only cost build size. Nothing needs re-assigning — asset GUIDs are unchanged, so existing references still resolve.
- `SETUP.md` now lists TextMesh Pro Essential Resources as a prerequisite and says how to import them. Unity unpacks TMP's shaders and default font per project, and without that step a scene containing Convai UI raises a `NullReferenceException` inside TextMeshPro instead of merely looking unstyled.

### Bug Fixes

- **A mid-session config reconcile silently dropped what you authored on a target.** It rebuilt every target from three fields, so alternate names, the interaction point, the text-only flag and availability were all thrown away — and this was not a backend-only path: the character's own current config went through it too. Alias matching stopped working after any reconcile, and a target you had marked unavailable became available again. The reconcile now clones, so authored data survives.
- **An action behavior's peer field filled itself in, against its own tooltip.** The field says to leave it empty and let the behavior find its peer; instead the automatic result was written back into the scene, with no undo, quietly making an authored-looking assignment out of a guess. The automatic result is now remembered for the run only, and the field stays exactly as you left it.
- **A behavior parented under its character after first use kept the wrong transform.** The character transform an action behavior works from cached its own fallback permanently, so a behavior that resolved before it was under a character never picked the character up — the exact failure its own documentation warns about. Only a real character is cached now.
- **The Action Activity Log recorded a failed step's count but not its detail.** The failure reason showed up under **Step Completed** rather than **Step Failed**, which read as though the step had finished normally. A failed step now records its own detail.
- **The Actions Editor ran a full scene scan per shared row, per repaint.** On a window that repaints on every mouse move, each row from an Action Set triggered an inactive-inclusive scene search with two allocations — the same cost this window had already removed from its character list. It is now one scan per draw pass.
- **A per-category logging level stopped working for any category added after Lip Sync.** `LoggingConfig` sized its lookup table from one named member of the category list rather than from the list, so a category declared after that member fell outside it: the per-category override in Convai Settings was skipped, the global level never reached it either, and it sat at a fixed default no setting could move — while `ConvaiSettings.GetLogLevel` reported it as configured. The table is now sized from the category list, so it cannot fall behind again, and a test asserts every category honours both its override and the global level rather than naming one that happens to be inside the table.
- **The emotion detection dropdown offered the two providers swapped.** It used the enum's declaration index as an index into its own label array, and the two orders differ, so choosing the responsive word-matching provider connected with the whole-reply one and the one-click "turn emotions on" fix selected the opposite of what it promised. The presented order is now declared separately from the enum and every read and write goes through it; a guard test compares each option's wording against the provider string that actually reaches the backend.
- **`UnlockEmotion` left the face holding the locked expression.** Locking writes the value into the accumulator's target scores as well as its current ones, so clearing the flag alone left the tick still smoothing toward the locked emotion — indefinitely on a quiet connection. It now restores the target, deferring to an active `SetEmotionOverride` when one is in play.
- **The post-action mood reaction discarded any runtime mood.** It was implemented as `SetMood` followed by `ClearMood`, and clearing means "return to the authored baseline", so a two-second reaction silently threw away a gameplay `SetMood` and any accumulated mood drift — breaking the module's own documented precedence from inside the module. It now rides on its own short-lived channel and leaves whatever is underneath it intact.
- `AnimatorConductor` reported an ownership conflict on every conflicting write, on a path called every frame. It now reports once per parameter and once per layer, and the record clears when the parameter or layer is re-registered, so a genuine new conflict is still reported.
- `StandardRigBinding` failed silently when a bone or blendshape could not resolve. It now says once, in plain language, what is missing and what to do about it (assign it under Semantic Bone Overrides, or map it under Custom Convention Map), and caches the miss so it does not repeat or re-walk the hierarchy on every later lookup.
- The LipSync speech-energy adapter never sampled. `ConvaiLipSyncSpeechEnergyAdapter` published itself as the character's speech-energy source, but nothing drove its sampling, so the value it exposed never changed and every feature reading speech energy read a flat signal for the character's whole lifetime. It now joins the character's tick and samples each frame.

### Breaking Changes

- **Removed `ConvaiActionTestSetup`** and its three menu items — `Convai/Developer/Prepare Action Test Setup`, `Convai/Developer/Prepare Action Test Setup (No Dialog)` and `Convai/Developer/Run Local Action Execution Test (No Dialog)`. It built a scratch character and fired a hard-coded action at it, which is how actions were exercised before there was anywhere else to do it. The Actions Editor's **Try It** box does the same thing against your own character and your own action, in both Edit and Play mode.
- **Removed `LookAtTargetActionExecutor`** (`Convai/Actions/Look At Target Action Executor`). It turned the character's root transform toward a target and nothing else — no eyes, no head, no settling. The Gaze module's `ConvaiLookAtActionExecutor` (`Convai/Actions/Look At Target`) replaces it and is what "look at" should always have meant.
- **Renamed `UnityEventActionExecutor` to `ConvaiUnityEventActionExecutor`**, bringing it in line with every other shipped behavior. Its **Add Component** entry is now `Convai/Actions/Raise Unity Event`. **The new type has a different GUID, so Unity does not carry the component across**: see Migration Notes — this one needs hands.
- **Removed `ConvaiActionDebugWindow`** and its menu item `Convai/Developer/Action Debug Window`. It was a public `EditorWindow` that predated any real authoring surface and had become the only place several things could be done. The Actions Editor (`Convai → Actions Editor`) covers all of it — raw command injection, target-resolution testing and the runtime patch composer live under **Live → Advanced** — and the Action Troubleshooter (`Convai → Action Troubleshooter`) covers the setup checks.
- **Removed the slot-list facial output path from the Emotion module.** Gone: `EmotionSlotBinding`, `BlendshapeEmotionBinding`, `AnimatorParameterEmotionBinding`, `RealisticEmotionSlots`, `NeutralAlternator`, and on `ConvaiEmotionProfile` the `SemanticExpressionsEnabled` and `NeutralAlternationEnabled` switches and the `CreateBlendshapeRuntimeBinding`/`CreateAnimatorRuntimeBinding` factories.

  This was a second facial path whose data the runtime discarded whenever semantic expressions were on — which was every shipped profile — so its authored slots, the tooling that built them for a rig, and the neutral alternator they fed were dead weight presented as live configuration. Expression recipes replace it and need no per-rig authoring at all. A profile that carried authored slots loses only data that was never read; nothing needs porting. Shader-property output is unaffected and is now the one remaining output binding.
- **Fourteen editor types are now `internal`.** They were `public` by default rather than by decision — Unity requires no particular accessibility on a custom editor, and these were never documented, never referenced by the samples, and never intended as an extension point. The SDK's shape is one user-facing component and one profile per module, with everything else internal; this brings the editor layer in line with it.

  The types: `ConvaiCharacterEditor`, `ConvaiPlayerEditor`, `ConvaiRoomManagerEditor`, `ConvaiRoomManagerProfileEditor`, `ConvaiCharacterEventRelayEditor`, `ConvaiSessionEventRelayEditor`, `ConvaiTranscriptEventRelayEditor`, `ConvaiNarrativeDesignManagerEditor`, `ConvaiNarrativeDesignTriggerEditor`, `ConvaiVisionBaseEditor`, `ConvaiVisionPublisherEditor`, `CameraVisionFrameSourceEditor`, `WebcamVisionFrameSourceEditor`, and `TurnTakingOptionsDrawer`.

  **`[CustomEditor]` registration is unaffected.** Unity discovers custom editors and property drawers by attribute, not by accessibility, so every Convai inspector and drawer continues to draw exactly as before. Nothing changes for a project that simply uses the SDK.
- Removed the public `UnityObjectCompatibility` class. It was public but was never customer API: an SDK-internal utility bridging Unity's object-identity and object-search differences across editor versions. Its replacements, `ConvaiObjectFind` and `ConvaiObjectId`, are deliberately `internal`, so the package no longer exposes this surface.
- **Replaced `EmbodimentContext`'s per-seam `Register*`/`Unregister*`/`*Changed` members with one `CharacterServiceRegistry`.** Adding a cross-module contract used to mean a hand-written slot, property, event, and register/unregister pair on `EmbodimentContext` per seam; it is now a Domain interface published through `Provide`/`Contribute` and read through the registry, and the composition root itself no longer changes. The cross-module contract interfaces (for example `IGazeSource`, `IBrowCueSink`, `ICharacterReorientationHandler`, `IEmotionStateSource`), `EmbodimentTickScheduler`, `FacialBlendshapeCompositorHost`, `EmbodimentExecutionOrders`, and `DeterministicEmbodimentRandom` are now `internal` — none of this was ever meant to be consumed from outside the package, so the type now says so.
- **`EmbodimentContext.TryResolve` no longer creates a context on a GameObject that is not a Convai character.** It previously fell back to the calling component's own GameObject, so dropping an embodiment component anywhere in a scene silently grew a composition root there and produced a setup that looked wired but drove nothing. It now resolves only against a real character root and returns `false` otherwise. A component that needs a context should call `TryResolveFor`, which reports the reason so the user learns what to do; `TryResolve` stays quiet because it doubles as the "is there one?" lookup for callers where a missing context is a normal answer.
- **Renamed embodiment types**: `EmbodimentProfileReceiver<T>` is now `ConvaiCharacterModule<T>`; `CharacterEmbodimentPreset` is now `ConvaiEmbodimentPreset`; `EmbodimentPresetLibrary` is now `ConvaiEmbodimentPresetLibrary`; `ConvaiCharacterEmbodimentBinding` is now `ConvaiEmbodimentPresetBinding`.
- **Removed the previous Gaze implementation** ahead of its replacement: the `Convai.Modules.Gaze` module (`ConvaiGazeCoordinator`, `ConvaiHeadLookActuator`, `ConvaiEyeGazeActuator`, and its three profile assets), and the domain contracts `GazeIntent`, `IGazeIntentProvider`, `AttentionReading`, `AttentionCandidate`, `IAttentionSource`, and `IFocusTargetProvider`.
- **Removed the Attention module** and its assembly `Convai.Modules.Attention`: `ConvaiAttentionController`, `ConvaiAttentionProfile`, `DefaultFocusTargetProvider`, `ConvaiAttentionDynamicContextBridge`, `AttentionTimings`, and the `convai.attention` preset slot. Convai Gaze replaces it.
- **Removed the Dialogue Animation module** and its two assemblies, `Convai.Modules.DialogueAnimation` and `Convai.Modules.DialogueAnimation.Rigging`. Gone with them: `ConvaiDialogueAnimationController`, `ConvaiDialogueAnimationProfile`, `DialogueAnimationLibrary`, `DialogueAnimationRuntimeConfig`, `DialogueAnimatorContract`, `DialogueClipEntry`, `CharacterGender`, `DialogueEmotionAffinity`, `DialogueEmotionAffinityMapping`, `DialogueTalkBodyCoverage`, `IAnimationClipLibrary`, `IDialogueVariantSelector`, `VariantSelectionContext`, `EmotionWeightedRandomSelector`, `DialogueAnimationClipPicker`, `AnimatorLayerBlender`, `AnimatorSlotOverrider`, `AnimatorStatePingPong`, `DialogueAnimationLibraryEditor`, the optional `AnimationRiggingGazeBridge`, and the `convai.dialogue-animation` preset slot. Convai Body Animation replaces it.
- **Removed the facial clip system** and its assembly `Convai.Modules.FacialAnimation`: `ConvaiFacialClipPlayer`, `ConvaiFacialClipRuntimePlayer`, `ConvaiFacialAnimationProfile`, `ConvaiRuntimeFacialClipProfile`, the *Facial Clip Bake Tool* editor window (`FacialAnimationClipBakeWindow`), and the `convai.baked-facial-clip` and `convai.runtime-facial-clip` preset slots. The Emotion module's micro-expression layer replaces the idle facial life these clips were used for.
- **Removed the shared copies of the built-in lip sync content** under `SamplesShared/Resources/LipSync/`. The default viseme maps now live in the LipSync module and kept their asset identifiers, so anything referencing them still resolves; the built-in profiles and the registry asset that listed them are replaced by code. Nothing to do unless your own code loaded `LipSync/ProfileRegistries/LipSyncBuiltInProfileRegistry` by that path — see Migration Notes.
- The shipped sample preset `ConvaiSamplesShared_CharacterEmbodimentPreset` no longer carries `convai.attention` or `convai.dialogue-animation` slots, and the Basic and Lip Sync sample scenes no longer carry the retired components.
- **Removed the Camila sample character.** `Samples/LipSyncSample/Characters/Camila/**` — the prefab, meshes, materials and textures — is gone, replaced by Sofia. Camila's assets had their own GUIDs, so a scene or prefab of yours that referenced them will report a missing reference rather than silently substituting Sofia.
- **Removed six sample action behaviors** from `SamplesShared/Behaviors/`: `AnimatorTriggerActionExecutor`, `HeldObjectActionState`, `NavMeshMoveToActionExecutor`, `PickUpActionExecutor`, `PutOnActionExecutor` and `TransformMoveToActionExecutor`. These were demonstrations of how to write a behavior, not supported API, but they lived in the auto-referenced `Convai.Sample` assembly and so were reachable from project code.
- **Replaced the shared Emotion taxonomy asset and removed the shared Emotion profile.** `ConvaiSamplesShared_EmotionTaxonomy.asset` is rebuilt and carries a new asset identifier, and `ConvaiSamplesShared_EmotionProfile.asset` is gone, replaced by four named personalities — Warm, Composed, Energetic and Reserved. A character or profile of yours that pointed at either reports a missing reference rather than falling back, and a missing taxonomy empties the emotion dropdown on every module that reads one. See Migration Notes.
- **Removed `ConvaiWorldObjectFocusProvider`.** It marked a scene object as something a character could look at, for the gaze implementation this release replaces. `ConvaiGazeTarget` does the same job for Convai Gaze and gives you more control over it — a priority tier, a base relevance, the distances inside which the target is a candidate at all, and an aim offset so the eyes meet the top of a painting rather than its pivot. Because the old one was a `MonoBehaviour` living in your scenes, Unity drops it silently the next time the scene is saved; see Migration Notes before you open your scenes.
- **Removed the dialogue-animation clip library** (`SamplesShared/Art/Animations/Dialogue/`) and `ConvaiSamplesShared_IdleFacialAnimationProfile`. Both belonged to modules retired in this release and nothing had referenced them since.

### Migration Notes

- **If you loaded the built-in lip sync profile registry by path.** `Resources.Load` on `LipSync/ProfileRegistries/LipSyncBuiltInProfileRegistry` now returns nothing, because the three profiles it listed are built in code instead. Ask `LipSyncProfileCatalog` for a profile by id rather than loading the registry; a registry of your own under `Resources/LipSync/ProfileRegistries/` is still discovered and still wins, so a project that added its own needs no change.
- **If you used `Convai → Developer → Prepare Action Test Setup`.** Open `Convai → Actions Editor`, pick the character, select the action and use **Try It** — **Preview** in Edit mode, which runs a typed phrase through the real target-resolution ladder, or **Test Run** in Play mode, which dispatches the action for real. Unlike the removed menu it exercises the action you actually shipped rather than a hard-coded stand-in.
- **If you used `LookAtTargetActionExecutor`.** Remove the component and add `ConvaiLookAtActionExecutor` (*Add Component → Convai → Actions → Look At Target*), then re-point any action bound to the old one. The replacement lives in the Gaze module and works through `ConvaiGazeController`, so the character needs Gaze on it (*Add Component → Convai → Embodiment → Gaze*); without that peer the behavior declines with one clear message instead of half-working. It also gains what the old one never had: `mode` (`glance` or `sustained`), `holdSeconds` and `engagement`, each authored on the component and overridable per call.
- **If you used `UnityEventActionExecutor`, read this before opening your scenes.** The type was renamed to `ConvaiUnityEventActionExecutor`, and because the new type carries a different GUID, **Unity does not migrate the component — it is dropped from every scene and prefab that had one, along with the events you wired into it.** There is no upgrade step that recovers it. On each affected object, add `ConvaiUnityEventActionExecutor` (*Add Component → Convai → Actions → Raise Unity Event*) and **re-wire its event by hand**, then re-point any action that was bound to the old component. Note down what each event called before you upgrade, because after the upgrade there is nothing left to read it from. The serialized field is still `_onExecute` and behaves identically, so a `[SerializeField] private UnityEvent` you reference from your own code needs no change beyond the type name.
- **If you opened `Convai → Developer → Action Debug Window`.** Use `Convai → Actions Editor`. Injecting a raw command, testing target resolution and composing a runtime action-config patch are all under its **Live** mode's **Advanced** group, in Play mode with the character connected; a `IConvaiActionDebugPresetProvider` you registered still supplies its templates there. Setup problems — a missing dispatcher, an unbound action, a target nothing can resolve — are now reported by `Convai → Action Troubleshooter`, with a one-click fix on most of them. If you referenced `ConvaiActionDebugWindow` from your own editor code, there is no replacement type; the window is internal to the SDK now.
- **Targeting an editor older than 6000.0.80f1:** upgrade the editor. There is no supported configuration below this floor.
- **If you called `UnityObjectCompatibility.FindObjectsByType<T>(mode)`:** call Unity's `Object.FindObjectsByType<T>` directly. On Unity 6000.0-6000.3 pass both arguments — `FindObjectsByType<T>(mode, FindObjectsSortMode.None)` — because the single-argument overload does not exist there; on 6000.4+ either form compiles.
- **If you called `UnityObjectCompatibility.GetId(value)`:** use `value.GetInstanceID()` up to Unity 6000.4, or `value.GetEntityId()` on 6000.2 and newer. `GetInstanceID()` is an error on 6000.5. As before, these ids are valid only within the session that produced them and must not be serialized.

- **If you subclassed a Convai editor or property drawer.** That is no longer supported, and there is no replacement extension point — the honest answer is that this was never an intended one. Delete the subclass; the Convai inspector it derived from continues to draw the component on its own. If you were overriding an inspector to add project-specific controls to a Convai component, the supported shape is a separate `MonoBehaviour` of your own with its own inspector, rather than a subclass of ours. If you were overriding one to *remove* or reorganise what the Convai inspector shows, please open an issue describing what you needed — that is a gap in the component's own configuration surface, and patching it from outside was always going to break on an upgrade.

- **If you drove your own component from the embodiment tick scheduler, or registered/unregistered a cross-module contract directly on `EmbodimentContext`.** The scheduler component itself is now `internal`, and the per-seam `Register*`/`Unregister*`/`*Changed` members are gone from `EmbodimentContext`. To join the character's per-frame tick, implement `IEmbodimentTickable` on your component and call `EmbodimentContext.RegisterTickable(this)` from `OnEnable` (pair it with `UnregisterTickable` in `OnDisable`) instead of reaching for the scheduler directly. First-party cross-module contracts are internal by design — see `Documentation~/EMBODIMENT.md`.

- **If you referenced `EmbodimentProfileReceiver<T>`, `CharacterEmbodimentPreset`, `EmbodimentPresetLibrary`, or `ConvaiCharacterEmbodimentBinding`.** These are renamed, not redesigned: switch your source references to `ConvaiCharacterModule<T>`, `ConvaiEmbodimentPreset`, `ConvaiEmbodimentPresetLibrary`, and `ConvaiEmbodimentPresetBinding` respectively. Asset GUIDs are preserved, so preset assets you already authored keep every existing binding and reference — only your source code needs updating.

- **If your characters used the previous Gaze components.** `ConvaiGazeCoordinator`, `ConvaiHeadLookActuator` and `ConvaiEyeGazeActuator` are replaced by a single `ConvaiGazeController` (*Add Component → Convai → Embodiment → Gaze*). Remove all three and add the one; it resolves the same head and eye bones from the character's rig itself, so there is nothing to re-point.

  **Delete your `ConvaiGazeCoordinationProfile`, `ConvaiGazeEyeProfile` and `ConvaiGazeHeadProfile` assets** — those three types are gone and the assets will not deserialize. Their settings do not map field-for-field onto the new `ConvaiGazeProfile`, and you should not try to transcribe them: the new defaults are tuned to look right with no profile assigned at all, so start there and only create a profile (*Create → Convai → Embodiment → Gaze Profile*) if you want to change the character's personality. What used to be spread across three assets — how much the head follows versus the eyes, how the two are weighted per dialogue state, how far each may travel — is now one asset with named sections, and the setup card on the component will offer to create one for the character that owns it.

  If you drove the old components from code, the entry points are `ConvaiGazeController.GazeAt` and `GlanceAt` rather than weights on an actuator. Each returns a `GazeHandle`: call `Release()` on it to end the request early, or await `Settled` / `Completion` to know when the character actually got there. `ReleaseAllScriptedGaze()` drops every scripted request at once. If you wrote a focus provider, implement `IGazeTargetProvider` and register it with `RegisterTargetProvider`.

- **If your characters used the Attention module.** Attention decided what a character was looking at: focus providers offered candidate targets each frame, and a weighted director committed to one, held it, and eventually broke away so the character did not stare. That job now belongs to **Convai Gaze**, which does the deciding *and* the looking in one component instead of publishing an attention reading for separate eye and head actuators to consume.

  Replace `ConvaiAttentionController` with `ConvaiGazeController` (*Add Component → Convai → Embodiment → Gaze*) and assign a `ConvaiGazeProfile` (*Create → Convai → Embodiment → Gaze Profile*) where the attention profile went. If you relied on the auto-created default focus provider to make the character watch the player, add `PlayerAnchorTargetProvider` (*Convai → Gaze → Advanced → Player Anchor*), which resolves the main camera or an explicit transform the same way. If you wrote your own `IFocusTargetProvider`, implement `IGazeTargetProvider` instead — the shape is the same: you are asked for a candidate and you answer with a point and a relevance. And if you used `ConvaiAttentionDynamicContextBridge` to publish the current target into `current_attention_object` for backend grounding, use `GazeDynamicContextBridge` (*Convai → Gaze → Advanced → Dynamic Context Bridge*), which publishes the same field from the gaze target.

  **Your tuning values do not carry over.** Gaze does not expose the interest-budget model as separate acquire, release, hold, decay, recovery and break-threshold numbers; it reaches the same "committed but not staring" result through its own controls. Re-tune on the Gaze profile rather than trying to map the old fields one to one, and delete your `ConvaiAttentionProfile` assets once you have — they will not deserialize.

- **If your characters used Dialogue Animation.** Dialogue Animation played idle and talk clips by driving a four-layer Animator Controller you had to author yourself: base idle, idle overlay, body talk and head talk, each with a pair of ping-pong states whose placeholder clips the SDK swapped at runtime. The replacement, **Convai Body Animation**, builds its own layered `PlayableGraph` in code — **there is no Animator Controller asset to author any more**. This is the important part of the migration: you are not porting the animator contract, you are deleting it.

  Replace `ConvaiDialogueAnimationController` with `ConvaiBodyAnimationController` (*Add Component → Convai → Embodiment → Body Animation*). Your clips move from a `DialogueAnimationLibrary` into a `ConvaiBodyAnimationSet` (*Create → Convai → Embodiment → Body Animation Set*), and your timing and weight tuning moves from a `DialogueAnimationRuntimeConfig` into a `ConvaiBodyAnimationConfig` (*Create → Convai → Embodiment → Body Animation Config*); a `ConvaiBodyAnimationProfile` points the controller at both. Then delete the `DialogueAnimatorContract` asset, the four layers, the ping-pong states and every `ConvaiDialogueSlot_*` placeholder clip — nothing reads them, and an Animator Controller left in place will fight the graph for the same bones.

  Two authoring concepts have no replacement and their data is not migrated. Per-clip **gender** filtering (`CharacterGender` on the character and on each entry) is gone: author one set per character type instead of one mixed set filtered at runtime. Per-clip **emotion affinity** tags (`DialogueEmotionAffinity`, and the `EmotionBiasStrength` that weighted them) are gone as an authoring surface; Body Animation and Emotion coordinate through the embodiment context rather than through tags on clip entries. If you implemented `IDialogueVariantSelector` to control which variant played, that seam does not exist on Body Animation — open an issue describing what your selector decided, so the replacement seam covers it.

  Finally, `AnimationRiggingGazeBridge` is gone. It was an optional component that drove Unity Animation Rigging `MultiAimConstraint` weights from gaze, compiled only when `com.unity.animation.rigging` was installed. Convai Gaze writes bone rotations procedurally and needs no rigging package; remove the bridge component and its constraints, and delete the package reference if nothing else in your project uses it.

- **If your characters used facial clips.** `ConvaiFacialClipPlayer` played a baked `ConvaiFacialAnimationProfile`, and `ConvaiFacialClipRuntimePlayer` sampled an `AnimationClip` directly; both fed blendshape curves into the shared facial compositor, and the *Facial Clip Bake Tool* window produced the baked profiles. In practice these were used to give an idle face some life — blinks, small brow and mouth movement — under lip sync and emotion.

  For that use, delete the component and profile and let the **Emotion** module's micro-expression layer do it: it produces idle drift and speech-coupled accents procedurally, needs no authored clips, and composes additively so it can never suppress the expression underneath. Nothing needs to be ported; the behaviour is on by default in the Emotion profile.

  If instead you were playing a *deliberate, authored* facial performance — a scripted reaction, a cutscene beat — **4.5.0 has no supported replacement for it.** The compositor this release drives the face through is internal, so there is no API to submit your own blendshape weights through, and we would rather say that than point you at something that will not compile. Drive the mesh directly instead: read the blendshape weights you need with `SkinnedMeshRenderer.SetBlendShapeWeight` on your own schedule, on a mesh no Convai module is composing. Bake your clips to your own asset before you upgrade — `ConvaiFacialAnimationProfile` assets will not deserialize once the module is gone, and there is no way to read them back afterwards. If authored facial performance matters to your project, open an issue describing what you are driving; a supported extension point is the right answer and it is not in this release.

- **If a character or profile of yours referenced the shared Emotion taxonomy or the shared Emotion profile.** Both asset identifiers change in this release: the taxonomy is rebuilt, and the single shared profile is replaced by four named personalities. Unity cannot carry either reference across, so open each affected character and re-point it — the taxonomy at `SamplesShared/Profiles/Embodiment/Modules/Emotion/ConvaiSamplesShared_EmotionTaxonomy.asset`, and the personality at whichever of Warm, Composed, Energetic or Reserved fits. A character left with no taxonomy still runs, but every emotion dropdown that reads one comes up empty, which reads as a broken inspector rather than as a missing reference. If you had edited either asset inside the package, copy your version into your own `Assets/` folder **before** upgrading.
- **If you used `ConvaiWorldObjectFocusProvider` to mark what a character can look at.** Add `ConvaiGazeTarget` to the same object instead (*Add Component → Convai → Gaze → Target*) and delete the old component. Do this **before** you save the scene: the old type no longer exists, so Unity strips the component silently on the next save and the object stops being a gaze target with nothing to show you why. `ConvaiGazeTarget` covers what the old one did and adds the controls that decide how much attention the target actually earns: a priority tier that can be set to outrank even the player, a base relevance, a maximum distance beyond which it stops being a candidate, and a local-space aim offset.
- **If you built on the Camila sample character.** Camila is replaced by Sofia and her assets are removed, so a scene of yours that used the Camila prefab, or a material or texture from her folder, will show a missing reference. Sofia is a Reallusion CC4 rig with the same blendshape convention, so a Convai setup transfers: add your components to the Sofia prefab, or drag Sofia into the scene in Camila's place and re-point whatever referenced her. If you had customised Camila's assets, copy them out of the package into your own `Assets/` folder **before** upgrading — anything left inside the package is replaced by the update.

- **If you used one of the removed sample action behaviors.** The SDK now ships supported behaviors for the same jobs, and they are the ones to move to: `ConvaiWalkToActionExecutor` replaces `NavMeshMoveToActionExecutor` and `TransformMoveToActionExecutor`, and `ConvaiTurnToFaceActionExecutor`, `ConvaiPointAtActionExecutor`, `ConvaiFollowPlayerActionExecutor` and `ConvaiReturnToStartActionExecutor` cover the rest of the movement demonstrations. Unlike the samples they are configured in the Inspector, report a typed failure reason, and are covered by tests. `PickUpActionExecutor`, `PutOnActionExecutor` and `HeldObjectActionState` demonstrated carrying an object and have no direct replacement; if you shipped them, copy the files out of the package into your own project before upgrading and they will keep working — they only ever depended on `IConvaiActionExecutor`.

## [4.4.1] - 2026-07-30

### Fixes

- Added Unity 6.0 through 6.5+ compatibility paths for object IDs and object searches. Unity 6.4 and newer use 64-bit `EntityId` and no-sort search APIs, while Unity 6.0 through 6.3 retain safe legacy fallbacks.
- Fixed Unity 6.0 project resolution by removing unavailable pseudo-module dependencies and pinning Collections 2.6.8 to avoid the known Collections 2.6.7 + AI Assistant `xxHash3`/`Unsafe` compiler regression.
- Kept the LipSync sample background light isolated on Unity 6.0 and 6.4+ by aligning both URP rendering-layer serialization formats, preventing the background light from overexposing Camila.
- Push-to-talk release now keeps the microphone and backend STT open while waiting for ASR-final. If the first configurable `PushToTalkPolicy.ReleaseTailMs` window expires, the SDK signals the authoritative stop and allows one more bounded window for provider finalization before closing capture.
- Fixed WebGL builds crashing from a stale `NativeLib` reference in `livekit-bridge.jslib`, and restored first-turn LipSync by correcting audio-timing registration order, warming the WebGL analyser, and recovering missed `PlaybackStarted` callbacks.

## [4.4.0] - 2026-07-21

### Feature Additions

- Added active-session Actions updates through `ConvaiActionConfigPatch`, with exact omitted-versus-empty list semantics, object/character/attention replacement, generated update IDs, and typed backend action-update acknowledgement metadata.
- Added a native **AI Coding** section to the Convai Editor window for Assistant/package/tool health, Unity MCP settings routing, and explicit managed instructions for supported coding clients. **Convai > AI Coding Setup** now opens this integrated section.
- Rebuilt the **Convai SDK** Project Settings page (Edit > Project Settings > Convai SDK) as UI Toolkit section views that are also mounted by a new **Settings** section in the Convai Editor window (`Convai > Settings`) — one implementation, two hosts, always in sync. Sections: Setup Health, Credentials, Runtime Defaults, Diagnostics, Advanced, and About.
- Credentials now include a **Validate & Save** button with a cached validation badge (valid/invalid/not validated, surviving Editor restarts for up to 24 hours via a hashed, SDK-versioned `EditorPrefs` entry) and an **Environment** preset (Production / Beta / Custom). The preset drives the REST base URL through the new `ConvaiRestOptionsFactory`; Custom unlocks raw core-server and REST base URL fields.
- Runtime Defaults gained a **microphone picker** listing actual device names (with System Default and refresh), plus new project-wide defaults: `DefaultPlayerDisplayName`, `CharacterAudioVolume`, and `AudioFeedbackEnabled`, all seeding the runtime settings service and runtime preferences.
- Diagnostics gained one-click logging presets (Verbose / Default / Errors Only) above the per-category override list; sections expose Reset-to-defaults buttons.
- Advanced gained a **feature-flag manager** that toggles the `CONVAI_DEBUG_LOGGING`, `CONVAI_ENABLE_SERVER_ANIMATION`, `CONVAI_ENABLE_UPDATES_SECTION`, and `CONVAI_ANIMATION_RIGGING` scripting defines for the active build target (with recompile confirmation and cross-target drift detection).
- Added a **Setup Health** section with project checks (settings asset present, API key set/validated, iOS microphone usage description, Android permission guidance, define drift, platform caveats) and one-click fix buttons, and an **About** section with SDK version, canonical links, and a Copy Support Info button (never includes the API key).
- Expanded the optional Unity MCP integration to 20 tools (contract version 4) with a privacy-preserving 256-entry runtime event trace and Unity-side Narrative Design configuration/diagnosis. Added **Convai > AI Coding Setup** for compatibility/tool checks, official Unity MCP settings routing, and explicit atomic managed-instruction installation for Codex, Claude Code, Cursor, Gemini, and VS Code Copilot. Expanded the packaged skill with progressive quickstart, action, runtime-debugging, dynamic-context, and narrative references. Added dev-only MCP recording-scene builder and four-prompt runbook under `Assets/ConvaiDev/MCPDemo/`.
- Added seven optional Unity MCP and Assistant tools for action authoring/simulation, lip-sync configuration/diagnosis, and transcript scene setup/diagnosis.
- Added a canonical room-scoped transcript timeline backed by `RoomTranscriptEngine` and exposed through `ConvaiManager.Transcripts`. The new immutable `TranscriptTimeline`, `TranscriptTurn`, `TranscriptSegment`, `TranscriptSpeaker`, and `TranscriptChange` models provide stable turn IDs, revisions, speaker/source/state metadata, active and committed history, and explicit added/updated/committed/interrupted/corrected/removed changes. Consumers can read `CurrentTimeline`, query with `GetTurns`/`GetTurn`/`GetLatestTurn`, react through `Changed` and the turn-specific events, subscribe with replay and speaker filters through `Subscribe`/`SubscribeCommitted`, clear history, and export committed turns as plain text, Markdown, or JSON.
- Added a separate speech-aligned caption projection on `ConvaiManager.Transcripts`: `CurrentCaptions`, `CaptionsChanged`, and `SubscribeCaptions` expose streaming/final captions without treating ephemeral TTS text as durable chat history. `IsPresentationEnabled` and `PresentationEnabledChanged` let shipped presentation components hide and replay without stopping canonical room recording; `ChatTranscriptUI` consumes history turns, while the sample `SubtitleTranscriptUI` consumes captions.
- Added an optional Unity AI Assistant 2.13 integration foundation with ten official Unity MCP and matching Assistant tools: guidance, project status, scene inspection, setup validation, manager bootstrap, room/player/character configuration, end-to-end conversation-scene orchestration, and ranked Edit/Play Mode diagnosis. Mutations are previewable, idempotent, Undo-enabled, active-scene scoped, never save scenes, and never read or write API-key values. The package-discovered `convai-unity-sdk` skill uses these tools proactively with recommended hands-free defaults before asking irreducible questions.
- Added an actions speech gate: `ConvaiActionDefinition.WaitForBotSpeech` (mirrored on `ConvaiActionCommand`) makes the first action of a fresh batch wait for character speech before executing, with an optional `DelayAfterBotSpeechSeconds` pause after the gate releases and a dispatcher-level `Speech Gate Timeout` (default 2 s) so a silent turn never stalls the batch.
- Added dynamic context vision support: rooms can opt into backend frame sampling with a new **Dynamic Vision Context** section on `ConvaiRoomManager` and `ConvaiRoomManagerProfile` (`ConvaiVisionContextMode`, `ConvaiVisionInputSettings`, `ConvaiVisionRespondModeSettings`). `respond_modes` is always sent on connect (its `context_update`/`trigger`/`scene_metadata` lanes govern non-vision features too); `vision_input_config` is sent only while dynamic vision resolves enabled. See `Documentation~/DYNAMIC-VISION-CONTEXT.md`.
- Added runtime vision controls on `IConvaiRoomConnectionService`/`ConvaiRoomManager`: `RequestVisionStatus()` queries the backend frame buffer, `TriggerVision(ConvaiVisionTriggerRequest)` attaches buffered frames to a turn with an optional prompt, respond mode, relative frame window, or absolute PTS pinning, and `UpdateRespondMode(lane, mode)` changes an input lane's respond mode mid-session.
- Added `VisionContextStatusReceived`, `VisionContextTriggerReceived`, and `RespondModeUpdateResultReceived` domain events carrying the backend acknowledgements (outcomes, downgrades, attached frame PTS, token estimates).
- Added a vision target rig and a Sample Debug Hub with a vision panel to the LipSync sample for exercising status/trigger flows end to end.

### Improvements

- Removed bundled LiveKit `ffi-*` architecture payloads from the released package. The retained editor downloader now installs only the running Unity Editor architecture plus architectures enabled for the active build target into the writable project `Assets/Convai/...` path; managed LiveKit source, Protobuf, and generated plugin import settings remain packaged.
- Hardened native NeuroSync playback alignment by anchoring each response to the exact PCM source
  frame where speech starts, interpolating the rendered audio position within Unity's DSP callback,
  compensating bounded LipSync smoothing delay, blending visual discontinuities after audio skips,
  recovering missing animation against the audible deadline, and retaining five minutes of indexed
  response frames.
- Expanded **Convai > Developer > Action Debug Window** with backend-confirmed runtime state, pending update IDs and age, exact action-update ACK metadata, a previewable runtime patch composer with omitted-versus-empty controls, and privacy-safe action filter counts/reason codes.
- Runtime action state is now backend-confirmed: sends remain pending, successful ACKs commit in send order, and errors, malformed/mismatched metadata, disconnects, or 30-second timeouts discard local mutations without retry. Realtime `requires_reconnect` status is surfaced without automatic reconnect.
- Unified direct and nested `action-response` handling behind one fail-closed filter. Unknown/unexecutable actions, unresolved required targets, and unresolved reference parameters are removed before public events and dispatch; logs expose counts and stable reason codes without raw action payloads.
- The API key is now stored obfuscated (XOR+Base64) in `ConvaiSettings.asset` instead of plaintext, with automatic one-time migration of existing plaintext keys on editor load. This is a deterrent against casual asset/VCS grepping, not encryption — any key shipped in a client build remains extractable; use a runtime `ICredentialProvider` with server-issued tokens where that matters.
- All REST client construction now flows through `ConvaiRestOptionsFactory` so the project-wide environment preset applies to narrative fetching, room auth, editor tools, and account/usage requests uniformly.
- Runtime settings now apply `PlayerDisplayName` to `ConvaiPlayer`; the optional settings-panel input remains null-safe until assigned in the shipped prefab.
- AI coding setup now validates the exact 20-tool MCP contract instead of accepting any matching count, rejects stale or unexpected tool names, and reconnects a running Unity MCP bridge after registry repair so external clients refresh their catalog.
- **Convai > AI Coding Setup** now shows status-specific **Fix** buttons. Missing or unsupported Unity AI Assistant installs the supported `2.13.0-pre.2` package through Unity Package Manager, then survives domain reload, refreshes assets, recompiles, refreshes the Unity MCP registry, and verifies all 20 Convai tools. Tool/skill refresh repairs are also available without reinstalling a compatible package; repair never changes Play Mode automatically and reports failures in-window.
- Restructured the parameterized actions runtime for maintainability: split the multi-type `ConvaiActionResolution.cs` into `ConvaiActionDefinition.cs`, `ConvaiActionInvocation.cs`, `ConvaiActionExecutionResult.cs`, and `IConvaiActionExecutor.cs` (all types keep their names and namespace — no code changes needed), added XML documentation across the public actions surface, and deduplicated target-kind/reference resolution between the dispatcher and `ConvaiActionInvocation.GetReference` into one shared resolver.
- Actions runtime logging now routes through `ConvaiLogger` (`LogCategory.Character`) instead of raw `Debug.*` in `ConvaiActionResponseParser`, `ConvaiActionDispatcher`, and `ConvaiActionConfigSource`.
- Added `ConvaiActionCommand.Enriched`: the response parser marks commands it has enriched and the dispatcher re-enriches only unmarked commands, replacing the previous zero-parameters heuristic that re-parsed legitimate parameter-less commands on every dispatch.
- Reduced steady-state actions overhead: `ConvaiActionExecutor<TParameters>` caches its reflection member map per parameter type, the response parser's regexes are compiled statics, and `ConvaiActionDefinition.ToActionConfigString()` renders are memoized per definition (hash-validated, so live edits still re-render).
- Step failure messages are now composed structurally: executor results carry the raw cause and the dispatcher appends the batch-abort/continue consequence exactly once when building `ConvaiActionStepReport.FailureMessage` (previously the dispatcher string-sniffed its own suffix to avoid double-appending). Final report text is unchanged.
- Removed the unused `ConvaiTargetActionParameters`, `ConvaiTextActionParameters`, and `ConvaiReferenceNumberActionParameters` DTOs (never released, zero references).
- The **Action Debug Window** is now sample-agnostic: project-specific templates and injection shortcuts live behind an editor extension seam (`IConvaiActionDebugPresetProvider` + `ConvaiActionDebugPresetRegistry`); the window includes manual injection fields and one-click injection for every authored action, and writes templates through an internal setter instead of private-field reflection.
- Vision input settings clamp every field into the backend's validated ranges and trim sampling windows into the frames-per-turn budget, so an inspector-authored config can never be rejected at connect.
- Published Convai vision frames are no longer vertically flipped a second time during LiveKit texture readback; orientation is owned by the Convai frame sources, so frames arrive upright at the backend vision model. Note for advanced users publishing a raw `RenderTexture` directly through the LiveKit `TextureVideoSource` (bypassing Convai frame sources): the plugin no longer flips for you — apply your own Y-flip or route through a Convai `IVisionFrameSource`.
- Half-configured sampling windows (interval left at 0) are now dropped instead of being clamped up to a 1 ms horizon that would request maximal backend capture load.
- The dynamic-context batch window is exposed as `ConvaiCharacter.DynamicContextBatchDelaySeconds` so tooling can display the real value instead of hardcoding it.
- RTVI `server-response` event types are now matched case-insensitively.
- Canonical transcript history now keeps recording while runtime transcript presentation is disabled; reenabling the shipped chat/subtitle UIs replays current state instead of losing turns captured while hidden.
- Cached `TranscriptTimeline.Turns` ordering and the facade's snapshot mapping, so repeated `ConvaiManager.Transcripts.CurrentTimeline` reads return the same immutable instance until the engine publishes a changed snapshot instead of rebuilding the complete timeline on every access.
- Tuned the CC4 Extended LipSync map mouth-shape multipliers and fade timing for clearer articulation and smoother settling.
- Refactored LipSync around a pure response-owned indexed session with strict owner precedence,
  bounded future-response buffering, deterministic gap recovery, centralized reset, and direct
  native source-sample sampling. WebGL and legacy transports retain the compatibility clock path.
- Reduced LipSync runtime log noise to one terminal response summary at `Info`, actionable recovery
  warnings, and opt-in owner/sample/gate timing at `Debug`; disabled detailed diagnostics no longer
  format per-packet strings.
- Simplified LipSync runtime/editor composition, moved map authoring and catalog invalidation fully
  into the Editor assembly, made map/profile collections immutable to consumers, and restricted map
  import to the canonical versioned JSON format.

### Changed

- Centralized internal component log tags through the existing `ILogger` seam. Injected consumers now apply one ownership tag, while static `ConvaiLogger` calls derive tags from simple source filenames without exposing directories (partial files normalize to their base filename). Semantic and dynamic bracket tokens remain; legacy static aliases normalize to filename tags.
- `ConvaiDynamicContextRelay` missing-character warnings now route through `ConvaiLogger` under the `Character` category instead of raw Unity `Debug` logging.
- Consolidated internal dynamic-context batch staging into `ConvaiDynamicContextTracker`; public behavior and wire payloads are unchanged.
- Consolidated whitespace-aware transcript text joining and prefix-extension handling into the internal `TranscriptTextMerge` helper; public transcript behavior is unchanged.

### Bug Fixes

- Fixed runtime action patches conflating omitted and empty lists, optimistically mutating `ConvaiCharacter.ActionConfig`, losing local `GameObjectReference` bindings, and retaining stale attention after object replacement.
- Fixed MCP action diagnosis failing to report null UnityEvent placeholders as `ACTION_EVENT_UNWIRED`.
- Locally configured player names from the `ConvaiPlayer` inspector or runtime settings are now authoritative for local transcript and caption display; settings-panel renames apply immediately through the manager's runtime settings service, retroactively correct existing turns, and pre-populate from the effective player name. The shipped sample-panel field now matches its existing `playerNameInputField` prefab binding. Backend speaker names remain available through `SpeakerInfo` and speaker IDs still drive actor identity.
- Fixed LiveKit remote-audio playback throwing `OverflowException` when peak detection encountered the valid PCM16 minimum sample (`-32768`).
- Fixed Dynamic Context debug-panel scrolling throwing inside `TMP_InputField.OnScroll` by giving generated multiline inputs a proper masked text viewport; refreshed the shared debug hub with clearer panel chrome, close controls, hover states, and consistent input/button styling.
- Fixed the chat transcript UI remaining invisible on startup when its Canvas Group begins at zero alpha; active chat prefabs now run their configured fade-in automatically.
- Fixed Unity 6000 EntityId resolution across MCP feature authoring targets, and made default LipSync diagnosis auto-resolve one unambiguous active-scene character.
- Fixed the `convai-unity-sdk` package skill being rejected by Unity Assistant because its required Editor version used an invalid two-component constraint.
- Fixed canonical player turns receiving duplicate processed-final reducer input. Late processed finals still reach player callbacks, correct the existing turn when identifiable, and do not restart a completed speaking session.
- Fixed `ConvaiTranscriptEventRelay` reporting every unmapped or unknown `TranscriptTextSource` as `PlayerAsr`; interim/final ASR sources now map explicitly to `PlayerAsr`, while unknown and future unmapped sources report `TranscriptSegmentSourceKind.Unknown`.
- Fixed MCP transcript diagnosis depending on hardcoded type-name strings and scanning every `MonoBehaviour`; it now detects `ConvaiTranscriptDisplay` and `ChatTranscriptUI` through typed active-scene queries and explicitly notes that sample UIs are not counted.
- Fixed indexed LipSync starting after audible audio had already ended when delayed data/lifecycle
  packets rebound response zero to still-advancing silent PCM. A new response now requires a current
  audible-audio start before its gate can open; sample timing only keeps an already-open response
  locked through silence or underrun. Added response/audio/sample ordering diagnostics.
- Fixed indexed LipSync continuing after native audio ended when the sample clock stalled before
  the buffered animation boundary. Closed responses now fade the current mouth pose after a bounded
  no-progress window.
- Fixed indexed LipSync packets failing on the first response because an uninitialized internal
  owner dereferenced a null response id. Metadata-free/scalar bot speech lifecycle payloads now
  degrade to an empty owner instead of throwing in protocol dispatch.
- Fixed cumulative native LipSync drift and long-response freezes by sampling animation from an absolute LiveKit source-frame clock, treating silent PCM separately from underruns, recovering missing indexed frames after a bounded grace period, and enforcing strict response ownership.
- Hardened long native audio responses with a two-second bounded ring buffer, all-or-nothing PCM writes, oldest-frame overflow recovery, and overflow/skip/underrun diagnostics. LipSync completion remains read-only and cannot stop or mutate audio playback.

### Breaking Changes

- `ConvaiSettings.DefaultMicrophoneIndex` was replaced by `DefaultMicrophoneDeviceId` (string; empty = system default). The old integer index is not value-migrated — re-pick your device in Settings > Runtime Defaults if you used a non-zero index.
- Removed the dead `ConvaiSettings.NativeRuntimeMode` property and the `NativeRuntimeMode` enum (it was forced to `Transport` since the transport unification).
- `ConvaiSettings.ServerUrl` is now derived from the environment preset: the serialized URL is honored only when the environment is `Custom`; Production and Beta always use `https://live.convai.com`.
- The `Convai > Logger Settings` menu and window section were removed; logging configuration lives in `Convai > Settings` (Diagnostics) and Project Settings. The Account section no longer contains API key entry — it moved to Settings > Credentials (the Account section links there).
- Removed the editor types `APIKeySetupLogic`, `ConvaiAccountSectionLogic`, `ConvaiLoggerSettingSection`, and `LoggerSettingsLogic`, plus the `ConvaiAccountSection.APIInputField`/`ShowHideAPIKeyButton`/`UpdateSaveButton` properties. Use `Convai.Editor.Settings.Services.ApiKeyValidationService` and `LogOverrideEditing` instead.
- Removed `ConvaiTranscriptToolMode.FacadeOnly`; it was advertised but had no handler, so requests silently did nothing while reporting success. Migration: the transcript facade needs no configuration; use `EventRelay`, `ChatUI`, or `WorldSpaceChatUI` to set up presentation.
- Removed `TranscriptTurnState.Committing` and `TranscriptSegmentSourceKind.BotTtsCaption`; neither was ever assigned or produced. Migration: use `TranscriptTurnState.Committed` or turn completion, and `TranscriptSegmentSourceKind.Unknown`, respectively.
- Replaced the snapshot-based `ConvaiManager.Transcripts` contract with the public timeline model: `CurrentTimeline` now returns `TranscriptTimeline` instead of `TranscriptTimelineSnapshot`; `Changed` now supplies `TranscriptChangeBatch` instead of `TranscriptUpdateBatch`; and `GetTurns`/`GetTurn`/`GetLatestTurn` now return `TranscriptTurn` values instead of `TranscriptTurnSnapshot`. `Subscribe` and `SubscribeCommitted` callbacks now receive `TranscriptChange` instead of `TranscriptTurn`, and `TranscriptSubscriptionOptions.IncludeInterim`/`IncludeCommitted` were renamed to `IncludeActive`/`IncludeTerminal` to describe listening, streaming, stable, committed, interrupted, and corrected states accurately.
- Removed the legacy conversation-history types `ConversationHistoryService`, `TranscriptEntry`, and `ConversationExportFormat`; canonical history, clearing, and export now live on `ConvaiManager.Transcripts`.
- Removed the legacy transcript presentation layer: `TranscriptUIController`, `ITranscriptUI`, `ITranscriptListener`, `TranscriptViewModel`, `Convai.Runtime.Presentation.Presenters.TranscriptSpeaker`, `ChatPresentationStrategy`, and `ITranscriptPresentationStrategy`. Custom UIs now consume `TranscriptChange` values directly from `ConvaiManager.Transcripts`; the domain model still provides the distinct `Convai.Domain.Models.TranscriptSpeaker` type.
- Removed the legacy transcript filtering/formatting types `ITranscriptFilter`, `DefaultTranscriptFilter`, `ITranscriptFormatter`, `DefaultTranscriptFormatter`, and `TranscriptFilterBase`, plus the sample-only `ProximityCharacterFilter`, `SingleCharacterFilter`, and `IVisionConeProvider` utilities. Use `TranscriptSubscriptionOptions`/`TranscriptCaptionSubscriptionOptions` for speaker, participant, active/terminal, and streaming/final selection, then apply UI-specific formatting or spatial filtering in the subscriber.
- Renamed the `Convai_DiagnoseTranscripts` runtime-turn JSON key from `lifecycle` to `state`; MCP clients that parse `runtime.turns[]` must read `state`.
- Removed `ConvaiActionConfigSource.TryResolveObject(string, out ConvaiActionObjectDefinition)`, an unused lookup that no SDK system called. Migration: enumerate the component's authored objects via the `Objects` inspector list, or — inside an action executor — read the already-resolved target from `ConvaiActionInvocation.ResolvedTarget` / `GetReference(name)`.
- Internalized the actions wire-format helpers `ConvaiActionResponseParser` and `ConvaiActionTemplateRenderer`: enrichment always runs automatically before `OnActionsReceived`/dispatch, and `ConvaiActionDefinition.ToActionConfigString()` remains the public rendering entry point, so there is no supported scenario that requires calling either type directly.
- Reshaped `ConvaiActionParameterReference` (the enriched reference handle on `ConvaiActionParameterValue.ResolvedReference`): `Kind` is now the `ConvaiActionTargetKind` enum instead of a free-form string, the enum moved from `Convai.Runtime.Actions` to `Convai.Shared.Types` (add a using if you referenced it by namespace), and the write-only `GameObjectReference` payload was removed — it was populated but never read by any SDK system. Migration: compare `Kind` against `ConvaiActionTargetKind` values instead of strings, and resolve scene objects through `ConvaiActionInvocation.GetReference(name).GameObjectReference`.
- `IConvaiRoomConnectionService` gained three members: `RequestVisionStatus(string)`, `TriggerVision(ConvaiVisionTriggerRequest)`, and `UpdateRespondMode(ConvaiRespondModeLane, ConvaiRespondMode, string)`. Code that consumes the interface (e.g. via `ConvaiRoomManager`) is unaffected; custom implementations of the interface must add the three methods (return `false` when vision is unsupported).
- Removed `ConvaiContextStateEntry`, an inert inspector DTO that was never read by any SDK system.
- Removed the legacy `SampleDynamicContextUI` component and `Prefabs/SampleDynamicContextUI.prefab`. Use the Dynamic Context panel in `SamplesShared/Prefabs/UI/Debug/Sample Debug Hub.prefab` instead; scenes that instantiated the removed prefab should replace missing-prefab entries with `SamplesShared/Prefabs/UI/Debug/Dynamic Context Debug Panel.prefab`.
- Unified the respond-mode vocabulary: `ConvaiContextReactionMode` is removed and every dynamic-context API now uses `ConvaiRespondMode` (moved from `Convai.Runtime.Vision.Context` to `Convai.Runtime`). Migration: `SyncOnly` → `Silent`, `ReactImmediately` → `MustRespond`, `Auto` unchanged. The wire format is untouched (`run_llm` still sends `auto`/`true`/`false`). If a scene or prefab saved during the beta serialized a reaction override (`ConvaiDynamicContextRelay`, `ConvaiTrackedContextProperty`), re-check the value in the Inspector — the enum was renumbered, so every saved index changes meaning: old `Auto` (0) deserializes as `Silent`, old `ReactImmediately` (1) as `Auto`, and old `SyncOnly` (2) as `MustRespond`. Shipped SDK assets carry no such serialized values; this only affects scenes saved against unreleased beta builds.
- Removed the public `IBlendshapeSink` extension seam and internalized
  `SkinnedMeshBlendshapeSink`, `LipSyncDriftMonitor`, `LipSyncDriftSample`, and `LipSyncDriftEvent`.
  The supported diagnostics surface is the **Convai → LipSync Drift Monitor** editor window and CSV
  export.
- Removed runtime map-authoring methods `ConvaiLipSyncMapAsset.ClearMappings`,
  `InitializeWithDefaults`, and `AutoDetectFromMeshes`. Equivalent undo-aware actions remain in the
  LipSync inspectors. Runtime import now accepts canonical version-1 JSON only.

### Migration Notes

- API keys migrate automatically: on first editor load after upgrading, a plaintext `_apiKey` in `Assets/Resources/ConvaiSettings.asset` is re-written as `_apiKeyObfuscated` and the plaintext field is cleared. Keep this generated settings asset out of version control. Code reading `ConvaiSettings.ApiKey` is unaffected. If you previously edited `_serverUrl` for staging, set Environment to `Custom` in Settings > Credentials to keep using it.
- Replace direct `new ConvaiRestClientOptions(apiKey)` construction with `ConvaiRestOptionsFactory.Create(apiKey)` so the environment preset applies.
- Replace `ConversationHistoryService.Entries`/`GetEntriesByPlayerOrCharacterId(...)` with `ConvaiManager.Transcripts.CurrentTimeline.Turns` or `GetTurns(...)`; replace `EntryAdded` with `SubscribeCommitted(...)`; keep using `ConvaiManager.Transcripts.Clear()` for canonical history removal; and replace `Export(ConversationExportFormat)` with `Export(TranscriptExportFormat)`, whose supported values are `PlainText`, `Markdown`, and `Json`.
- Replace `TranscriptUIController`, `ITranscriptUI`, `ITranscriptListener`, and presentation-strategy/view-model integrations with `ConvaiManager.Transcripts.Subscribe(...)`. Update rows from `change.Turn` keyed by `change.Turn.Id`, inspect `change.Kind` for lifecycle transitions, and remove rows by `change.TurnId` when the kind is `Removed`; `ChatTranscriptUI` and `ConvaiTranscriptEventRelay` are shipped references for code-driven and Inspector-driven history reactions.
- Split old single-stream transcript consumers by purpose: use `Subscribe(...)`/`CurrentTimeline.Turns` for durable chat, history, replay, and corrections; use `SubscribeCaptions(...)`/`CurrentCaptions` for low-latency speech-aligned subtitles. `ConvaiTranscriptDisplay` remains a character-local TTS convenience component, not the canonical room transcript surface.
- Update new-pipeline beta integrations from `Action<TranscriptTurn>` subscription callbacks to `Action<TranscriptChange>` and read the turn from `change.Turn`; rename option assignments from `IncludeInterim` to `IncludeActive` and `IncludeCommitted` to `IncludeTerminal`. Treat `CurrentTimeline` as immutable: repeated reads return the same instance while transcript state is unchanged and a new instance after an engine timeline change.
- Code that implemented `IBlendshapeSink` must drive a supported map/profile through
  `ConvaiLipSyncComponent`; custom runtime sink injection is no longer supported. Replace direct
  drift-type calls with the editor drift window/CSV workflow. Move map generation into editor
  tooling and re-export old tuple, pair, array-root, or JSON-like files as canonical version-1 JSON.
- Dynamic vision is off unless you opt in. `Auto` (the default mode) enables it only when the room's Connection Type is already `Video`; it never upgrades an Audio room, so existing Audio scenes keep their behavior and cost after upgrading. Set the mode to `Enabled` to force a video-capable room with vision.
- Rooms already configured with Connection Type `Video` before this release resolve `Auto` to vision **on** after upgrading and start sending `vision_input_config` on connect. If a Video room should keep its legacy native-video behavior (e.g. Gemini Live) without the new config, set Dynamic Vision Context to `Disabled`.
- `Disabled` suppresses the vision connect config without touching the configured Connection Type, so legacy native-video flows (e.g. Gemini Live) keep publishing video.
- The removed `SampleDynamicContextUI` also carried a microphone mute toggle and an initial-context readout; the Debug Hub panel does not replicate them. Use `ConvaiManager.Audio.SetMicMuted` (or the push-to-talk flow) for mic control.
- Vision frames attach image tokens to every model turn while enabled. The defaults mirror the backend (5 frames per turn, 1 s sampling, single horizon); richer configs such as dual-horizon sampling windows are an explicit opt-in — see the token-cost note in `Documentation~/DYNAMIC-VISION-CONTEXT.md`.

## [4.3.0] - 2026-06-23

### Feature Additions

- Added support with bundled native plugins, editor tooling, package version embedding, data stream updates, token-source helpers, and platform audio support across Windows, macOS, Linux, Android, and iOS.
- Added configurable connect-time user VAD settings for room connections, including room/profile inspector controls, transport mapping to `vad_params`, server-default handling, and resolved VAD logging.
- Added the consolidated dynamic context v2 flow with tracked state/events, batching, acknowledgement/result events, token feedback, attention object updates, and the `ConvaiDynamicContextRelay` authoring surface.
- Added synced world-object context support so tracked scene metadata and the current focus object can be sent through dynamic context.
- Added manual session resume id support, player-name metadata, request-trace metadata, end-user metadata, and the `InteractionCreated` runtime event.
- Added separate narrative trigger modes for saved triggers, inline events, and scripted speech.
- Added runtime debug panels and prefabs for dynamic context and emotion state, plus a world-space chat transcript prefab.
- Added action configuration validation, duplicate-binding preservation, step diagnostics, and action debug probing.
- Added Enter-to-focus behavior for chat input.

### Improvements

- Hardened native audio subscription reconciliation, FFI/audio lifecycle ownership, sample-rate handling, and native subscribed-track hydration during connect.
- Improved WebGL lipsync playback timing and typed-text RTVI stop handling.
- Improved emotion label resolution and aligned emotion scoring/docs with the current reading API.
- Improved scene metadata flushing so pending world-object metadata is sent reliably.
- Improved editor reuse for shared VAD inspector drawing and clarified inspector copy for server VAD behavior.
- Updated package documentation for dynamic context v2, actions, emotions, VAD setup, and source references.

### Bug Fixes

- Fixed unconditional Android AEC republishing.
- Fixed null or destroyed `AudioSource` handling.
- Fixed valid duplicate action bindings being treated as invalid.
- Fixed emotion debug panel scoping in shared sample assets.
- Fixed pending scene metadata not being flushed.

### Migration Notes

- Dynamic context now uses the v2 tracked update flow. Prefer `ConvaiCharacter.DynamicContext` or `ConvaiDynamicContextRelay` over the removed command-style dynamic context UI.
- Narrative trigger requests now carry an explicit mode. Use saved triggers, inline events, or scripted speech according to the desired backend behavior.
- Custom VAD values are sent only during room connect. Use the server-default option when the backend should own VAD defaults.

## [4.2.0] - 2026-05-08

### Feature Additions

- Added complete structured Actions support, including backend `action-response` handling, action configuration, target/character authoring, queued dispatch, target resolution, result-aware step reports, config validation, built-in executors, sample executors, and debug/editor validation tooling.
- Added Meta Quest passthrough vision support via `QuestVisionFrameSource`.
- Added runtime switching between push-to-talk and hands-free conversation modes without reconnecting.
- Expanded dynamic context runtime support with tracker APIs, inspector tooling, and sample UI commands.
- Added Convai scene setup API with setup wizard validation and bootstrap flow.
- Hands-free and push to talk support given in Settings Window.

### Improvements

- Updated Basic Sample with action targets and action sample assets.
- Added and refreshed docs for Actions, the actions integration tutorial, turn-taking, API entrypoints, setup, troubleshooting, and source references.

### Bug Fixes

- Fixed Convai scene loading when a non-Convai scene is already open.
- Improved scene file opening with lazy, non-blocking retry behavior.
- Fixed FFI shutdown handling in audio callbacks.
- Improved setup health checks, logging, diagnostics, and test coverage.

## [4.1.0] - 2026-04-09

### Feature Additions

- Added dynamic context support.
- Made the LipSync sample's showcase camera work without a hard dependency on the Unity Input System by switching to reflection-based optional support.
- Refined the packaged sample scenes and transcript chat prefab defaults for a smoother out-of-box setup experience.

### Bug Fixes and Improvements

- Improved Vision module behavior and reliability.
- Improved editor startup reliability to reduce first-load native plugin errors during package import.
- Fixed compile-time issues around optional support libraries and improved native plugin compatibility for different Windows Unity editor architectures.
- Fixed an iOS crash related to support library handling.
- Fixed LipSync showcase eye-contact blend shape discovery so characters whose face meshes live outside the eye-bone subtree resolve correctly.
- Tuned showcase camera behavior and related sample assets for more stable presentation in the LipSync sample.

## [4.0.0] - 2026-03-12

### Feature Additions

- Introduced initial LipSync support in the Unity Core SDK, including core runtime integration points for LipSync-driven character workflows.
- Added bundled default ARKit blendshape maps to streamline early LipSync setup and reduce manual configuration.
- Expanded initial WebGL + LiveKit support, including support for vision canvas publishing in WebGL environments.
- Added an initial configurable native runtime mode to support evolving runtime selection behavior across supported platforms.
- Introduced early session resume UI and remote audio control support as part of the ongoing session and media control workflow improvements.
- Added support for passing emotion_config in room connection payloads.
- Continued evolving the SDK API surface with ConvaiRoomSession and broader session-oriented facade changes.
- Introduced a platform-aware networking bootstrap flow to support cleaner runtime registration for native and WebGL networking implementations.

### Bug Fixes and Improvements

- Improved stability of LipSync room-connect transport integration and related runtime connection flows.
- Continued simplifying the networking stack by removing older orchestration, reconnection, and legacy connection service layers in favor of more direct runtime ownership.
- Improved reliability of manager-driven startup and bootstrap behavior under the ConvaiManager flow.
- Updated native library download handling so native libraries are imported into a writable Unity project location, improving package install compatibility.
- Improved UPM sample packaging and sample structure to better align with Unity package distribution expectations.
- Resolved a number of compiler warnings, runtime integration issues, and editor UI sizing and stability issues in configuration and setup surfaces.

## [0.1.0] - 2026-02-20

### Added

- Real-time conversational AI characters via `ConvaiCharacter`, `ConvaiPlayer`, and `ConvaiRoomManager` components.
- Full conversation pipeline: Speech Recognition, Language Understanding and Generation, Text-to-Speech, Lipsync.
- Event-driven architecture with `IEventHub` for decoupled communication between SDK components.
- Modular behavior system (`ConvaiCharacterBehaviorBase`, `ConvaiPlayerBehaviorBase`) for extending character and player logic.
- Vision module for camera and webcam frame capture with configurable resolution and frame rate.
- Narrative Design module for trigger-based story progression and section synchronization.
- Scene metadata system (`ConvaiObjectMetadata`, `ConvaiSceneMetadataCollector`) for environment-aware AI.
- Configurable logging framework with pluggable sinks (Console, File, HTTP).
- UI components: transcript display (chat and subtitle modes), connection status indicator, notification system.
- Native transport layer powered by LiveKit for low-latency audio/video streaming.
- REST API client for character management, animation, long-term memory, and narrative services.
- Platform support for Windows, macOS, Linux, Android, and iOS (WebGL support planned).
- Editor tooling: Project Settings panel, custom inspectors, and menu items for quick setup.
- Sample scene with demo characters, animations, and interaction controller.
