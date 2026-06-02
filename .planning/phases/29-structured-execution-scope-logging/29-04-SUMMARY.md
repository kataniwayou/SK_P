---
phase: 29-structured-execution-scope-logging
plan: 04
subsystem: orchestrator
tags: [logging, otel, scopes, orchestrator, quartz]

# Dependency graph
requires:
  - phase: 29-structured-execution-scope-logging
    plan: 01
    provides: "ExecutionLogScope.WorkflowId scope-key constant (read by name)"
  - phase: 17-messaging-contracts
    provides: "CorrelationKeys.LogScope (the CorrelationId scope key); InboundCorrelationConsumeFilter BeginScope Dictionary shape to mirror"
provides:
  - "Explicit BeginScope(CorrelationId + WorkflowId) wrapping the post-mint body of WorkflowFireJob.Execute — the Quartz fire path now surfaces both ids at ES attributes.CorrelationId / attributes.WorkflowId (LOG-05 / LOG-01)"
  - "Hermetic WorkflowFireJobScopeTests proving a post-mint fire log carries CorrelationId (non-empty Guid) + WorkflowId == the parsed input"
affects: [es-attributes, observability, wave-2-merge]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "A non-consume execution path (Quartz job) opens its OWN explicit scope (D-06) because no consume filter wraps it — the ONE place the job owns CorrelationId, so it uses CorrelationKeys.LogScope directly"
    - "Scope opens AFTER the per-fire correlationId mint (the point both ids are known); the pre-mint early returns stay outside the scope (Pattern 6)"
    - "Ids go ONLY into the scope dictionary, never interpolated into a message template (T-18-04 / T-29-08); existing templates byte-unchanged (additive-only)"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs

key-decisions:
  - "WorkflowFireJob owns CorrelationId via CorrelationKeys.LogScope (D-06) — it runs outside the consume pipeline so no correlation/execution filter ever scopes it"
  - "Scope wraps the dispatch loop + liveness refresh + self-reschedule; the two early returns (unparseable workflowId / workflow absent) are NOT wrapped — they fire before the correlationId mint (Pattern 6)"
  - "Test captures scope via the same scope-capturing ILogger<T> double used in EntryStepDispatchScopeTests (no new package); CorrelationId asserted by presence + Guid-shape (minted per fire), WorkflowId asserted == the deterministic input"

patterns-established:
  - "Out-of-pipeline execution paths self-scope their owned ids explicitly; in-pipeline paths inherit the filter-opened scope"

requirements-completed: [LOG-05, LOG-01]

# Metrics
duration: 2min
completed: 2026-06-02
---

# Phase 29 Plan 04: WorkflowFireJob Explicit CorrelationId+WorkflowId Scope Summary

**`WorkflowFireJob.Execute` now wraps its post-mint body (dispatch loop + liveness refresh + self-reschedule) in an explicit `BeginScope` carrying the per-fire `CorrelationId` (via `CorrelationKeys.LogScope`) and the parsed `WorkflowId` (via `ExecutionLogScope.WorkflowId`) — the Quartz fire path runs OUTSIDE the consume pipeline so no filter scopes it, and this explicit scope (D-06) makes its fire logs surface `attributes.CorrelationId` + `attributes.WorkflowId` and correlate with the round-trip they trigger (LOG-05 / LOG-01); the pre-mint early returns stay unscoped (Pattern 6), additive-only with zero template interpolation.**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-06-02T16:36:37Z
- **Completed:** 2026-06-02T16:38:51Z
- **Tasks:** 1 (TDD: RED + GREEN, no refactor)
- **Files modified:** 1 created, 1 modified

## Accomplishments

- `WorkflowFireJob.Execute` opens an explicit `using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = correlationId.ToString(), [ExecutionLogScope.WorkflowId] = workflowId.ToString() }))` IMMEDIATELY after `var correlationId = NewId.NextGuid();` — wrapping the `foreach` dispatch loop, the L1 liveness refresh, and the self-reschedule. Both ids surface on every post-mint fire log via the unchanged OTel `IncludeScopes` bridge.
- The two early returns (unparseable `workflowId` `LogWarning`; workflow-absent `LogInformation`) precede the mint and are deliberately NOT inside the scope (Pattern 6 / RESEARCH) — they emit before both ids are known.
- `using Messaging.Contracts;` was already present (the file already references `EntryStepDispatch`/`L2ProjectionKeys`); no new using needed. No id interpolated into any log-message template (the only `{WorkflowId}` placeholders are the two PRE-EXISTING early/skip templates — unchanged); the loop/refresh/reschedule logic is byte-identical apart from the indentation from the wrapping block.
- Hermetic `WorkflowFireJobScopeTests` drives the real `WorkflowFireJob` (real `WorkflowL1Store`, in-memory MassTransit harness behind `StepDispatcher`, `FakeTimeProvider`, isolated RAM Quartz scheduler) with a scope-capturing `ILogger<WorkflowFireJob>` double (mirrors `EntryStepDispatchScopeTests`) and asserts a post-mint fire log's captured scope carries `CorrelationId` (non-empty Guid string — minted per fire) AND `WorkflowId` == the known input. GREEN (1/1).

## Task Commits

TDD task (RED → GREEN, no refactor — the wrap matches the locked spec exactly):

1. **Task 1 (RED): failing scope test** - `c08d882` (test)
2. **Task 1 (GREEN): explicit CorrelationId+WorkflowId scope** - `d06dd12` (feat)

**Plan metadata:** committed separately with this SUMMARY + state docs.

## Files Created/Modified

- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` (modified) — post-mint body wrapped in the explicit `BeginScope(CorrelationId + WorkflowId)`; early returns untouched.
- `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs` (created) — hermetic proof the post-mint fire log carries both scope keys with the expected values.

## Decisions Made

- **Job owns CorrelationId (D-06):** `WorkflowFireJob` is the ONE place using `CorrelationKeys.LogScope` directly — it runs outside the consume pipeline, so neither the correlation filter nor the execution-scope filter ever sees it.
- **Scope boundary at the mint:** the scope opens right after `correlationId = NewId.NextGuid()` (both ids known) and wraps the dispatch loop + liveness + reschedule; the pre-mint early returns are excluded (Pattern 6).
- **Test scope-capture double (reused pattern):** the same ~20-line scope-capturing `ILogger<T>` double from `EntryStepDispatchScopeTests` is used (no `ICorrelationAccessor`, no new package); CorrelationId is asserted by presence + Guid-shape (minted per fire, not a fixed value), WorkflowId by exact equality to the deterministic input.

## Deviations from Plan

None - plan executed exactly as written. The provided scope-edit shape and test guidance were followed verbatim. The only adjustment was adding `using MassTransit;` / `using MassTransit.Testing;` to the test file (harness extension methods) — these are standard test-harness usings already present in the sibling `FireDispatchTests`, not a logic deviation. No Rule 1/2/3 auto-fixes were required.

## Issues Encountered

None functional. The first test build failed on a missing `using MassTransit;` (the `AddMassTransitTestHarness` extension); added it and the RED test then compiled and failed for the correct reason (scope keys absent).

## TDD Gate Compliance

RED gate (`test(29-04)`, `c08d882`) → GREEN gate (`feat(29-04)`, `d06dd12`) recorded in git log in order. RED failed for the correct reason (the SUT opened no scope, so `FireScope` returned an empty dict and the `ContainsKey` assertions failed — Failed 1/Passed 0), then GREEN made it pass (Passed 1/0). No unexpected early pass. REFACTOR gate intentionally omitted (the spec-locked wrap needed no cleanup).

## Verification Evidence

- `dotnet build src/Orchestrator -c Debug` → Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet test ... -- --filter-class *WorkflowFireJobScopeTests` → Passed 1 / Failed 0 (RED was Failed 1 / Passed 0 before the SUT edit).
- `dotnet test ... -- --filter-class *FireDispatchTests` → Passed 3 / Failed 0 (no regression — the scope wrap is additive, fire logic byte-identical).
- `git diff --diff-filter=D HEAD~1 HEAD` → no deletions in the feat commit.
- Template-interpolation check on the committed file → only the two PRE-EXISTING `{WorkflowId}` placeholders (early-return + missing-step skip); no new `{CorrelationId}`/`{WorkflowId}` placeholder added.

## Threat Surface

No new surface. T-29-08 (log injection via job-data workflowId) and T-29-09 (info disclosure) both `accept` in the plan's threat register: `workflowId` is `Guid.TryParse`-validated BEFORE the scope (the early return rejects unparseable input), and the per-fire `correlationId` is server-minted (`NewId.NextGuid()`). Both reach the scope only as VALUES under fixed keys (never interpolated), and both are GUIDs — no PII/secret. No new endpoint, auth, network, or storage surface; the existing OTel `IncludeScopes` bridge is reused unchanged.

## Next Phase Readiness

- LOG-05 (the explicit WorkflowFireJob scope) shipped; the fire path's CorrelationId + WorkflowId now flow to ES attributes, the last Wave-2 scope writer. Phase 29's logging slice is functionally complete pending the wave merge + close-gate.
- No blockers. Per the plan's verification, after the wave merges, run the hermetic full suite (exclude Category=RealStack) — the scope wrap is additive, so existing WorkflowFireJob/scheduling tests are expected to stay GREEN (confirmed already against FireDispatchTests 3/3).

## Self-Check: PASSED

- FOUND: `src/Orchestrator/Scheduling/WorkflowFireJob.cs` (modified)
- FOUND: `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs`
- FOUND: `.planning/phases/29-structured-execution-scope-logging/29-04-SUMMARY.md`
- FOUND commit: `c08d882` (test RED)
- FOUND commit: `d06dd12` (feat GREEN)

---
*Phase: 29-structured-execution-scope-logging*
*Completed: 2026-06-02*
