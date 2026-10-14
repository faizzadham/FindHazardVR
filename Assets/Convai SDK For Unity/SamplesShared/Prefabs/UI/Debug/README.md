# Runtime debug panels

Sample-only helpers for inspecting live SDK state. These live under `SamplesShared/` and are **not** part of the core SDK runtime.

## Recommended setup

Drag **`Sample Debug Hub.prefab`** into a sample scene. It provides a left-side launcher with accordion drawers for:

| Button | Panel | Shows |
| --- | --- | --- |
| Emotion | `EmotionStateDebugPanel` | backend emotion label, scale, resolved face output |
| Context | `DynamicContextDebugPanel` | dynamic-context state, events, attention, ACKs |
| Vision | `VisionContextDebugPanel` | vision status/trigger controls for dynamic vision context |

Only one drawer is open at a time. Click the active button again to collapse it.

## Standalone prefabs

You can still drag individual panels into a scene if you do not want the hub:

| Prefab | Shows |
| --- | --- |
| `Emotion State Debug Panel.prefab` | emotion pipeline debug overlay |
| `Dynamic Context Debug Panel.prefab` | dynamic-context controls overlay |

In single-character sample scenes, keep **Auto Resolve** enabled on each panel. In multi-character scenes, assign the target `ConvaiCharacter` and related controller or manager explicitly.

## Notes

- `DynamicVisionContextSceneTool` (also under `SamplesShared/Scripts/UI/Vision/`) is a minimal IMGUI overlay for quick scene-only checks. Prefer the sample **Vision** drawer; keep `_showOverlay` off when the hub is present.
- All debug UI scripts are in `SamplesShared/Scripts/UI/` and are safe to remove with the samples package.
