# Runtime debugging

1. Read the current Editor state and diagnose in that mode. If Play Mode is already active, inspect the running session without stopping or restarting it. In Edit Mode, complete configuration diagnosis first.
2. Enter or exit Play Mode only on explicit request. A debugging request alone does not authorize a mode change; continue available read-only diagnosis and report any runtime evidence still needed.
3. In Play Mode, start `TraceRuntimeEvents` with narrow manager/character/event filters. Transcript capture stays off unless requested.
4. Observe a reproduction within the authorized scope. Read the trace and feature diagnosis; use Unity console tools for stack traces. Running a real action or changing a live conversation requires the corresponding action request.
5. Stop a trace you started when done. Trace storage is editor-only, bounded, and cleared on Play Mode exit/domain reload.

Rank evidence: session/pipeline errors, character readiness, turn/action lifecycle, speech/mic state, dynamic-context acknowledgements, then configuration warnings. Never infer success from absence of console errors.
