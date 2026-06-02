---
phase: 29-structured-execution-scope-logging
plan: 01
subsystem: infra
tags: [logging, otel, scopes, messaging-contracts, elasticsearch]

# Dependency graph
requires:
  - phase: 17-messaging-contracts
    provides: "Messaging.Contracts pure-POCO leaf library + CorrelationKeys sibling pattern + IExecutionCorrelated execution-id member names"
provides:
  - "ExecutionLogScope constants class — single source of truth for the five execution-id scope keys (WorkflowId, StepId, ProcessorId, ExecutionId, EntryId)"
  - "Hermetic key-pin test guaranteeing each key string == its structured-param name (LOG-03 ES single-field invariant)"
affects: [consume-filter, nested-beginscope, processorid-enricher, workflowfirejob, wave-2]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Scope-key constants class mirrors CorrelationKeys shape (pure POCO, file-scoped namespace, zero usings, no MassTransit ref)"
    - "Key string == structured-param name so scope-derived and template-param-derived attributes coincide on the same ES attributes.<Key> field"

key-files:
  created:
    - src/Messaging.Contracts/ExecutionLogScope.cs
    - tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs
  modified: []

key-decisions:
  - "CorrelationId deliberately excluded from ExecutionLogScope — it stays owned by CorrelationKeys.LogScope (D-01)"
  - "New Contracts/ test folder created (none existed) to mirror the Messaging.Contracts namespace under test"

patterns-established:
  - "Execution-id scope keys live in one POCO leaf; consumers read by name (never string literals at call sites)"

requirements-completed: [LOG-03]

# Metrics
duration: 2min
completed: 2026-06-02
---

# Phase 29 Plan 01: ExecutionLogScope Constants Class Summary

**Foundational `ExecutionLogScope` POCO leaf in Messaging.Contracts — the single source of truth pinning the five execution-id scope keys (WorkflowId/StepId/ProcessorId/ExecutionId/EntryId) to their structured-param names so scope- and template-param-derived attributes land on the same Elasticsearch field (LOG-03).**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-06-02T16:12:09Z
- **Completed:** 2026-06-02T16:13:47Z
- **Tasks:** 1 (TDD: RED + GREEN)
- **Files modified:** 2 created

## Accomplishments

- `ExecutionLogScope` static class added to `Messaging.Contracts` — five `public const string` members, each equal to its structured-param name, mirroring the `CorrelationKeys` shape byte-for-byte (pure POCO, file-scoped namespace, zero usings).
- `CorrelationId` deliberately excluded — it remains owned by `CorrelationKeys.LogScope` (D-01); confirmed via grep that the only `CorrelationId` occurrence is in the doc-comment prose, not a member.
- Hermetic `ExecutionLogScopeKeyTests` pins each key string to its param name — the LOG-03 single-ES-field invariant now has a regression guard. GREEN (1/1).
- Messaging.Contracts stays a MassTransit-free POCO leaf (no `using` directive in the new file); `dotnet build` clean at 0 Warning / 0 Error.

## Task Commits

TDD task (RED → GREEN, no refactor needed — file matches the locked spec exactly):

1. **Task 1 (RED): failing key-pin test** - `e9da7f2` (test)
2. **Task 1 (GREEN): ExecutionLogScope constants class** - `07df976` (feat)

**Plan metadata:** committed separately with this SUMMARY + state docs.

## Files Created/Modified

- `src/Messaging.Contracts/ExecutionLogScope.cs` - The five execution-id scope-key constants (single source of truth for the new scope keys).
- `tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs` - Hermetic test asserting each key string == its structured-param name.

## Decisions Made

- **CorrelationId excluded from ExecutionLogScope (D-01):** correlation stays owned by `CorrelationKeys.LogScope`; the new class carries only the five execution-id keys.
- **New `Contracts/` test folder:** none existed under `tests/BaseApi.Tests`; created it (namespace `BaseApi.Tests.Contracts`) to mirror the `Messaging.Contracts` namespace, per the plan's "create it if absent" guidance.

## Deviations from Plan

None - plan executed exactly as written. The provided source and test were used verbatim; no Rule 1/2/3 auto-fixes were required.

## Issues Encountered

None.

## TDD Gate Compliance

RED gate (`test(29-01)`, `e9da7f2`) → GREEN gate (`feat(29-01)`, `07df976`) recorded in git log in order. RED failed for the correct reason (CS0103 — `ExecutionLogScope` did not exist), then GREEN made it pass. No unexpected early pass. REFACTOR gate intentionally omitted (the spec-locked file needed no cleanup).

## Verification Evidence

- `dotnet build src/Messaging.Contracts -c Debug` → Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet test ... -- --filter-class *ExecutionLogScopeKeyTests` → Passed 1 / Failed 0.
- `grep CorrelationId|^using` on `ExecutionLogScope.cs` → only the doc-comment prose match; no `CorrelationId` member, no `using` directive.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Wave 1 leaf complete and unblocked: all of Wave 2 (consume filter, nested `BeginScope` in `EntryStepDispatchConsumer`, the `ProcessorId` enricher, and `WorkflowFireJob`) can now read scope keys from `ExecutionLogScope` by name instead of string literals.
- No blockers.

## Self-Check: PASSED

- FOUND: `src/Messaging.Contracts/ExecutionLogScope.cs`
- FOUND: `tests/BaseApi.Tests/Contracts/ExecutionLogScopeKeyTests.cs`
- FOUND: `.planning/phases/29-structured-execution-scope-logging/29-01-SUMMARY.md`
- FOUND commit: `e9da7f2` (test RED)
- FOUND commit: `07df976` (feat GREEN)

---
*Phase: 29-structured-execution-scope-logging*
*Completed: 2026-06-02*
