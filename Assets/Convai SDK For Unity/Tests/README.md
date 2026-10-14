# Test suite policy

Tests protect customer-visible behavior, wire contracts, architecture boundaries, and costly regressions. They do not archive migrations or restate implementation.

## Ownership

- Put tests under the feature they protect: `LipSync`, `Vision`, `Transcripts`, `Emotion`, `Gaze`, and similar folders.
- Use `Runtime` for public component orchestration and `Infrastructure` for transport/protocol behavior.
- Use `Samples` for shipped sample code and `Release` for platform/package smoke gates.
- Keep `PlayMode` for frame ordering and Unity lifecycle behavior that cannot be proved in Edit Mode. Prefer `EditMode` otherwise.
- Keep reusable doubles in `EditMode/Fixtures` or `EditMode/Mocks` when only Edit Mode needs them. Use `Tests/Fixtures` for the test-only assembly shared by Edit Mode and Play Mode; it must not depend on UnityEditor. Extend an existing double before creating another local implementation.

## Keep or delete

Keep a test when its failure identifies a broken behavior or contract. Delete or merge tests that only prove:

- constructors assign properties;
- events can be subscribed or unsubscribed;
- a method does not throw without checking resulting state;
- a deleted type, file, wording, migration phase, or temporary compatibility bridge remains absent;
- private implementation details, unless reflection is required by a deliberate public-surface or architecture guard.

Prefer one scenario with strong state and event assertions over many one-assertion tests. Parameterize genuine input matrices. Use immediate schedulers, injected clocks, and mock transport packet injection instead of sleeps.

## Verification

Run focused Edit Mode tests while iterating. Before merge, run all package Edit Mode tests and the small package Play Mode lifecycle suite. Use the pinned Unity editor in batch mode when no editor owns the project, or run `Convai → Developer → Automation Testing → Run PR Validation` in-editor.

Repository-root tests under `Assets/Convai/Tests` are developer automation and live validation, not package test ownership. Keep backend-dependent validation explicitly opt-in and do not interpret generated `TestReport` output as a current run.

## Allocation guards

Use `Convai.Tests.Fixtures.AllocationCounter.Measure` for synchronous workloads whose contract is exactly zero managed allocations. Construct inputs and the measurement delegate, then warm the feature before calling it. The helper validates `GC.Alloc` with a retained positive control, measures two identical batches on the calling thread, reports both counts, and returns their maximum, so either batch allocating fails an exact-zero assertion. Collection/finalization setup belongs inside the helper. First-batch allocations must be diagnosed; a passing second batch does not excuse them. Put assertions and diagnostic output after measurement. A missing recorder or undetected positive control fails the guard.

The result is an allocation **event count**, not bytes or duration. Zero events protect an exact-zero byte contract; event counts cannot replace nonzero byte ceilings or measurements spanning yielded frames. The feature-owned Edit Mode `AllocationMeasurement` fixture provides scoped hierarchy byte measurements where byte ceilings are required. Player and device results need their own supported profiling configuration and validation.

The Play Mode `Fixtures/AllocationByteMeasurement` fixture supports byte ceilings for synchronous batches and real frame workloads in the Editor. Create it before the feature's warm-up and dispose it to restore profiler settings. Measure both batches and assert their maximum against the unchanged per-batch ceiling. Each batch requires a retained 1 KiB positive control. Frame measurements tag consecutive frames and sum inclusive GC bytes under every named workload scope, including scheduler and `LateUpdate` work outside the test coroutine. Missing scopes, dropped frames, insufficient retained history, and undetected controls fail instead of reporting zero. Deep Profile must be off; retain enough profiler history for the complete batch and readback. The fixture deliberately fails on players until a supported player byte reader is validated. Its contract tests exercise byte sizes, cross-frame attribution, missing scopes, failed controls, and profiler-state restoration.
