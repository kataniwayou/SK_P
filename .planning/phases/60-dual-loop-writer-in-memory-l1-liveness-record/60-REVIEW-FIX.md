---
phase: 60-dual-loop-writer-in-memory-l1-liveness-record
fixed_at: 2026-06-13T00:00:00Z
review_path: .planning/phases/60-dual-loop-writer-in-memory-l1-liveness-record/60-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 60: Code Review Fix Report

**Fixed at:** 2026-06-13
**Source review:** .planning/phases/60-dual-loop-writer-in-memory-l1-liveness-record/60-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5 (WR-01, WR-02, IN-01, IN-02, IN-03 — fix_scope = all)
- Fixed: 5
- Skipped: 0

All fixes were applied to the main working tree and committed atomically (one commit
per finding), with normal commit hooks. Overlapping-file findings were staged hunk-by-hunk
via `git apply --cached` so each commit carries only its own finding's changes.

## Verification

Run against the committed end-state (all 5 fixes in place):

- **Release build** (`dotnet build src/BaseProcessor.Core -c Release -warnaserror`): succeeded, 0 Warning(s), 0 Error(s).
- **Debug build** (`-c Debug -warnaserror`): succeeded, 0 Warning(s), 0 Error(s).
- **Phase=60 hermetic suite** (`dotnet test tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=60"`, MTP-native filter, Redis at localhost:6380): Passed — Failed: 0, Passed: 17, Skipped: 0, Total: 17.

Working tree is clean for `src/` after the run (the untracked `src/BaseApi.Service/Properties/launchSettings.json` is pre-existing and unrelated).

## Fixed Issues

### WR-01: Loop A / Loop B catch only `RequestTimeoutException`

**Files modified:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`
**Commit:** 87673bf
**Applied fix:** Added a `catch (Exception ex) when (ex is not OperationCanceledException)` clause to BOTH the Loop A (identity) and Loop B (schema) request loops, after the existing `RequestTimeoutException` catch. Transient bus/transport faults (broker reconnect, `MassTransitException`, connection drop in the boot-before-register window) are now logged and re-looped on backoff instead of faulting the `BackgroundService` and taking the host down. The `OperationCanceledException` exclusion preserves the existing shutdown-cancellation return path, and the `RequestTimeoutException`-specific log is unchanged. Applied symmetrically to both loops per the directive.

### WR-02: Heartbeat catch re-reads `_context.Id` instead of the captured local `id`

**Files modified:** `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs`
**Commit:** c6d2e99
**Applied fix:** Changed the heartbeat write-failure warning to log the gate-captured local `id` (the exact value the failed `_writer.WriteAsync(id, ...)` used) instead of re-reading the `_context.Id` property, so the diagnostic and the write reference the identical value. No behavior change to the write path.

### IN-01: `configOutcomeOverride` is `string?` with no guard it is a SchemaOutcome const

**Files modified:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`
**Commit:** 8646e2a
**Applied fix:** Chose the lowest-risk option — added a `Debug.Assert` at the top of `WriteUnhealthyAsync` asserting `configOutcomeOverride is null or SchemaOutcome.Success or SchemaOutcome.Fail` (plus a `using System.Diagnostics;`). This guards the single internal caller's invariant in Debug builds without altering the (correct) runtime path or the method's `string?` signature, so the one internal call site is unaffected.

### IN-02: Stale doc comments reference the removed flat-key scheme and pre-Phase-60 counts

**Files modified:** `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs`, `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`, `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
**Commit:** d81f107
**Applied fix:** Documentation-only (no behavior change), as directed:
- `ProcessorLivenessOptions.TtlSeconds` doc now describes the TTL floor folded into the per-instance key `skp:proc:{id}:{instanceId}` via the writer's `max(interval×2, TtlSeconds)` formula, noting the flat `skp:{id}` write was removed (D-05).
- The orchestrator Gate-A class doc now states the clash path WRITES an `unhealthy` per-instance key with `configOutcome=Fail` and the gate fails on `status` (not "absent"), referencing the per-instance key and D-05.
- The DI extensions comment corrected "four independent seconds-ints" → "six".
The old flat-key L2 write was NOT reintroduced; only comments changed.

### IN-03: `InstanceId.Resolve()` precedence duplicated in the holder copy

**Files modified:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
**Commit:** 1540251
**Applied fix:** Confirmed the cycle-free precondition before applying — this file already imports `Messaging.Contracts.Identity` (line 12) and already calls `InstanceId.Resolve()` at the DI site (line 199), and the holder copy was byte-identical to the SoT. Changed `ResolveInstanceIdForHolder()` to delegate to `InstanceId.Resolve()` so the precedence lives in one implementation, and updated the drift-guard XML doc accordingly. The OTel observability copies in `BaseConsole.Core` / `BaseApi.Core` (and their hermetic mirror) were left untouched per scope. Verified 0-warning under `-warnaserror` in both Release and Debug.

## Skipped Issues

None — all 5 in-scope findings were fixed.

---

_Fixed: 2026-06-13_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
