# More than one character in a scene

A Convai room holds every active Convai Character in the loaded scenes, and the SDK works out which
one the player is addressing. There is no multi-character mode to switch on, no component to add and
no field to fill.

**Adding a second working character to a working scene is the whole setup.** Before configuring
anything, check whether the request actually needs a change. Most do not.

## Diagnose first

`Convai.DiagnoseConversation` answers every question this page is about:

- `configuration.conversationTargeting` — the rule and its four numbers, plus `isShippedDefault`.
- `runtime.addressedCharacter` — who the player is talking to, in either shape of room.
- `runtime.conversationAvailability` / `runtime.playerCanTalk` — whether a message sent right now
  would reach anybody.
- `runtime.multiCharacter.roster` — each membership's status and failure code.
- `runtime.multiCharacter.targetingVerdict` — the reason behind the last decision.

The verdict is the one to read carefully. A conversation that is holding correctly and one that is
stuck look identical from outside; `HeldForDelay`, `HeldForPlayerSpeech` and `AlreadyActive` are
working, and only the absence of any movement over many seconds with a better candidate in view is a
fault.

## Symptom to cause

| Symptom | Read | Usual cause |
|---|---|---|
| Only one character ever answers | `roster[].status` | The other is `Starting`, not `Ready`. A character that has not joined yet is not eligible |
| The player types and nothing answers | `conversationAvailability` | `Preparing` — connected, but this character has not been announced. The message was refused and the Console says so |
| Looking at a character does not switch | `conversationTargeting.within` / `lookAngle` | The character is outside the eligibility shape |
| The conversation flickers between two | `switchMargin`, then `switchDelaySeconds` | Two characters close together. Raise the margin first |
| Nothing switches at all | `conversationTargeting.mode` | `Manual`. The game owns the choice |
| A character spawned during play never joins | Console | A room opened with one character carries no roster and cannot grow. It joins on the next connection |
| The room stopped connecting when a second character arrived | `lastSessionErrorCode` | `connection.connect_multi_character_not_allowed` — the account lacks multi-character access. Nothing in the scene is wrong |

## Configuring

`Convai.ConfigureConversationTargeting` changes the rule and the room selection. Every field is optional
and omitting one leaves it as the project authored it.

- Flicker: raise `switchMargin` first, `switchDelaySeconds` second. Do **not** reach for
  `maxDistance` or `maxAngle` — those decide who is eligible, not who is preferred, and shrinking
  them makes the conversation stick to whoever was closest last.
- Scripted or menu-driven conversations: `targetingMode: "Manual"`, then move it from game code with
  `ConvaiManager.TalkTo(character)`.
- A rule the three modes cannot express — the character a quest is about, the one in a trigger
  volume — is `IConversationTargetProvider`, not a tuning value. Write the component; the SDK keeps
  applying the rules that stop it feeling wrong on top of whatever it returns.
- `includedCharacterInstanceIds` is the **exact** set. A character left out is excluded, and setting
  it at all switches the manager to an explicit selection, after which characters added to the scene
  later stay out until they are included too. Read the current set from the diagnosis first, or send
  `includeAllCharacters: true` to go back to the shipped default.

## Two things that are not settings

**One character speaks at a time.** Moving the conversation ends the previous character's answer.
That is the service's rule, not something the SDK can be configured out of. A line that must be
heard in full needs `Manual` targeting or a `ConvaiCharacter.IsSpeaking` check.

**The target never clears itself.** When nobody qualifies, the last character addressed keeps the
conversation, because a cleared target means the player speaks and nothing answers.

## Reacting from game code

`ConvaiManager.Events` carries the three that matter: `OnConversationTargetChanged` (Requested,
Confirmed and Failed — the first two a round trip apart), `OnConversationAvailabilityChanged`, and
`OnRoomRosterChanged`. `Convai.TraceRuntimeEvents` records all three, which is how to see what
happened in Play Mode rather than what the scene is configured to do.

## Changing a live room from MCP

Preview `Convai.SetConversationTarget`, then call it with `dryRun: false` in Play Mode to move the
conversation and wait for the service's authoritative target response. If the player is mid-utterance,
the tool instead reports a replaceable local queue with `executed: false`; use
`Convai.WaitForMultiCharacterState` after speech ends to observe the canonical target. Preview
`Convai.UpdateCharacterRoster`, then apply it to add or remove one local character instance and wait
for the roster acknowledgement. Both tools use exact instance IDs from `Convai.InspectScene`, never
change Play Mode, and return route/roster epochs so the result can be reconciled with
`Convai.DiagnoseConversation`.

After adding a character, call `Convai.WaitForCharacterReady` before targeting it. The roster
acknowledgement can arrive while the new member is still `Starting`; the readiness wait ends only
when that exact current-room membership is usable or has failed, and timing out does not cancel its
startup. Use `Convai.WaitForMultiCharacterState` when the desired outcome spans target, roster size,
route/roster epoch, all-member readiness, or conversation availability.

For scene authoring, `Convai.SetupMultiCharacterRoster` previews and applies the exact next-room set
plus an optional initial character in one call, after checking that at least two characters have
non-empty, unique Character IDs. `Convai.SimulateConversationTargeting` is the read-only way to tune
LookAt or Proximity: it explains every candidate's geometry, effective score, eligibility, and the
geometric proposal without entering Play Mode or contacting the backend. It also reports the live
proposal when an ineligible current target forces recovery to the first eligible character in the
authoritative room-membership order. The
live switch-delay, player-speech, and pending-command gates still decide whether that proposal can
switch now. Camera-less scenes use the same owned-player fallback as the live runtime. A wait ends
with `ROOM_SESSION_CHANGED` when its room disconnects or is replaced, rather than reporting a stale
timeout or startup failure.

These are live-room operations, not scene authoring. They do not reconnect a single-character room,
save a scene, change the next connection's selection, or bypass the account's multi-character gate.

Full reference: `Documentation~/MULTI-CHARACTER.md`.
