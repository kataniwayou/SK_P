---
phase: 35-fault-intake-correlation
plan: 01
subsystem: messaging-base-library
tags: [refactor, log-scope, execution-correlation, D-07, regression-guarded]
requires:
  - "IExecutionCorrelated + ExecutionLogScope 5 key constants (pre-existing in Messaging.Contracts)"
  - "InboundExecutionScopeConsumeFilter inline scope build (pre-existing, LOG-02/LOG-03)"
provides:
  - "ExecutionLogScope.BuildState(IExecutionCorrelated) — single source of truth for the 5-key execution-scope dict (D-07)"
  - "Filter delegating to the shared helper; the Wave-2 Keeper fault consumers can now open the same scope manually"
affects:
  - "Every console consuming via the bus-wide InboundExecutionScopeConsumeFilter (behavior byte-identical)"
tech-stack:
  added: []
  patterns:
    - "Pure-POCO static helper as single source of truth for a scope-key set, shared by a consume-filter and (next wave) manual consumers"
key-files:
  created: []
  modified:
    - src/Messaging.Contracts/ExecutionLogScope.cs
    - src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs
decisions:
  - "D-07: centralize the 5-key execution-scope build in ExecutionLogScope.BuildState so the bus-wide filter and the Wave-2 Keeper fault consumers (the filter does NOT fire on Fault<T>) share one skip-rule set — prevents per-console scope drift."
  - "BuildState adds NO CorrelationId key — that stays owned by CorrelationKeys.LogScope (D-01), byte-identical to the filter it replaced."
metrics:
  duration: ~6 min
  completed: 2026-06-05
  tasks: 2
  files: 2
---

# Phase 35 Plan 01: ExecutionLogScope.BuildState single-source-of-truth (D-07) Summary

Refactored the 5-key execution-scope dict builder out of `InboundExecutionScopeConsumeFilter` into a shared, regression-guarded `ExecutionLogScope.BuildState(IExecutionCorrelated)` helper in `Messaging.Contracts` — the byte-identical single source of truth the Wave-2 Keeper fault consumers will call to open the execution log-scope manually (the bus-wide filter never fires on `Fault<T>`).

## What Was Built

**Task 0 — `ExecutionLogScope.BuildState` helper (commit `3949107`)**
Added a `public static Dictionary<string, object> BuildState(IExecutionCorrelated ec)` to the existing pure-POCO `ExecutionLogScope` leaf, extracting the filter's inline block VERBATIM: the only key set is `{WorkflowId, StepId, ProcessorId, ExecutionId, EntryId}`, four `!= Guid.Empty` skips, one `!string.IsNullOrEmpty(ec.EntryId)` skip, EntryId stored verbatim (no `.ToString()`), and NO CorrelationId key. Inside the class the bare key constants resolve directly (no `ExecutionLogScope.` prefix). No new project reference — `IExecutionCorrelated` lives in the same leaf assembly.

**Task 1 — Filter delegation (commit `3f1edea`)**
Replaced the inline `var state = new Dictionary...` block (and its `BeginScope`) with a single `using (logger.BeginScope(ExecutionLogScope.BuildState(ec)))`. The `is not IExecutionCorrelated ec` pass-through and `Probe` stay UNCHANGED; class signature, open-generic `<T>`, and namespace untouched.

## Regression Baseline (Wave-0 guard)

Captured GREEN **BEFORE** any edit (per 35-VALIDATION.md):
- `*ConsoleExecutionScopeFilterTests` — **4/4 PASS** (Case A 5 keys/no CorrelationId, B Guid.Empty skip, C non-IExecutionCorrelated pass-through, D empty-string EntryId skip)
- `*ExecutionLogScopeKeyTests` — **1/1 PASS** (key-string == param-name)

Re-run GREEN **AFTER** the refactor (the D-07 byte-identical proof), all 6 enumerated guards:
- `*ConsoleExecutionScopeFilterTests` — 4/4
- `*ExecutionLogScopeKeyTests` — 1/1
- `*EntryStepDispatchScopeTests` — 2/2
- `*EntryStepDispatchRuntimeScopeTests` — 1/1
- `*WorkflowFireJobScopeTests` — 1/1
- `*ProcessorIdEnricherTests` — 2/2

No console lost an `attributes.*` field; Case A still asserts exactly 5 keys / no CorrelationId.

## Verification

- `dotnet build src/Messaging.Contracts -c Debug` — **0 Warning / 0 Error**
- `dotnet build SK_P.sln -c Release` — **0 Warning / 0 Error** (base-library change consumed by every console)
- Acceptance greps (Task 0): `BuildState` sig == 1, helper-body Guid.Empty guards == 4, `!string.IsNullOrEmpty(ec.EntryId)` == 1, `.ToString()` == 4 (the four Guids; EntryId verbatim).
- Acceptance greps (Task 1): `ExecutionLogScope.BuildState(ec)` == 1, `var state = new Dictionary` == 0 (inline build removed), `is not IExecutionCorrelated ec` == 1 (pass-through kept).

### Note on the `grep -c "CorrelationId" == 0` acceptance criterion
The plan's Task-0 acceptance asserted the file's `CorrelationId` grep count == 0. The actual count is **2 — both doc-comment-only**: line 7 is the PRE-EXISTING class doc (`Sibling to CorrelationKeys; CorrelationId is deliberately NOT here`), and line 22 is my added `BuildState` `<summary>` ("no CorrelationId key"). The grep was authored against the helper *body*; the substantive intent — **the BuildState code adds NO CorrelationId key** — holds exactly. Verified: no `state[...CorrelationId...]` assignment exists anywhere in the file. Not a deviation requiring a fix; recorded here for the verifier.

## Deviations from Plan

None — plan executed exactly as written. No auto-fixes (Rules 1-3) and no architectural decisions (Rule 4) were needed. No authentication gates. The only annotation is the doc-comment `CorrelationId`-grep clarification above, which is a criterion-phrasing artifact, not a behavior change.

## Working-Tree Hygiene

Both commits staged EXPLICIT paths only (`src/Messaging.Contracts/ExecutionLogScope.cs`, then `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs`). The ~242 pre-existing unstaged `.planning/phases/*` archive deletions were NOT staged and remain uncommitted. `git diff --diff-filter=D HEAD~1 HEAD` shows zero tracked-file deletions in either commit.

## Self-Check: PASSED

- `src/Messaging.Contracts/ExecutionLogScope.cs` — FOUND, contains `public static Dictionary<string, object> BuildState(`
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` — FOUND, contains `ExecutionLogScope.BuildState(ec)`
- Commit `3949107` — FOUND in git log
- Commit `3f1edea` — FOUND in git log
