---
phase: 53-model-b-teardown
plan: 01
subsystem: testing
tags: [xunit, reflection, source-scan, masstransit, negative-guard, tdd, RETIRE-03]

# Dependency graph
requires:
  - phase: 50-recovery-rearchitecture-slot-array-3-state-keeper
    provides: "ModelBContractsRetiredFacts.cs (Phase-50 negative-guard class) + L2ProjectionKeys/Keeper anchors"
  - phase: 52-three-state-keeper
    provides: "5->3 state collapse (RecoveryConsumerBase<T> : IConsumer<T> for the 3 surviving states)"
provides:
  - "FACT 5 (GREEN): 5->3 reflection guard — Keeper assembly consumes EXACTLY KeeperReinject/KeeperInject/KeeperDelete"
  - "FACT 6 (RED-now): D-01 source-scan — no bus-retry/error-transport CALL on exec+orchestrator path"
  - "FACT 7 (RED-now): D-03 source-scan — ConfigureError is keeper-local only (binder yes, global callback no)"
  - "FACT 8 (RED-now): D-07 source-scan — dead Ignore<WorkflowRootNotFoundException> removed from Start/Stop defs"
  - "RepoRoot([CallerFilePath]) anchor + Directory.Exists/File.Exists false-pass guard for the source-scan facts"
affects: [53-02, 53-03, model-b-teardown verification, close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Bisect-friendly Wave-0 negative guards: land RED standing facts FIRST so each Wave-1 removal commit is verified by an already-present fact"
    - "CALL-pattern source-scan (endpointConfigurator/cfg.UseMessageRetry(, .ConfigureError() — NOT the bare word — to dodge ~9 doc-comment false-positives"
    - "RepoRoot [CallerFilePath] anchor + Directory.Exists/File.Exists false-pass guard (T-53-01)"

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs"

key-decisions:
  - "Omitted the Orchestrator/BaseProcessorCore assembly anchors from this file — the 3 new facts are pure source-scans (RepoRoot file paths), so adding them would leave unused fields and break the 0-warning Release build"
  - "FACTS 6/7/8 are intentionally RED on the pre-teardown tree (expected Wave-0 state) — RED here is success, not failure; Wave-1 plans 02/03 turn them GREEN"

patterns-established:
  - "Pattern 1: Wave-0 RED negative-guard landing — standing facts encode the end-state before the removal commits exist"
  - "Pattern 2: CALL-pattern (not bare-word) source-scan to exclude legitimate doc-comment survivors"

requirements-completed: [RETIRE-03]

# Metrics
duration: 13min
completed: 2026-06-11
---

# Phase 53 Plan 01: Model-B Teardown — Standing Negative Guards Summary

**Five standing negative-guard [Fact]s landed in `ModelBContractsRetiredFacts.cs` (1 GREEN 5->3 reflection + 3 RED-now D-01/D-03/D-07 source-scans + a RepoRoot [CallerFilePath] anchor) that encode the RETIRE-03 end-state and verify every Wave-1 Model-B removal commit.**

## Performance

- **Duration:** 13 min
- **Started:** 2026-06-11T19:42:29Z
- **Completed:** 2026-06-11T19:56:19Z
- **Tasks:** 2
- **Files modified:** 1

## Accomplishments
- **FACT 5 (GREEN, verified):** `Keeper_registers_exactly_three_recovery_consumers` — reflects over the Keeper assembly's `IConsumer<T>` closed-generic args and asserts EXACTLY `[KeeperDelete, KeeperInject, KeeperReinject]` (no KeeperUpdate/KeeperCleanup). This is the RETIRE-03 / SC-2 verification half; passes today because Phase 50/52 already collapsed the states.
- **FACT 6 (RED-now, has teeth):** `No_bus_retry_or_error_transport_on_execution_path_endpoints` — CALL-pattern scan over `src/Orchestrator/Consumers` + `src/BaseProcessor.Core/Startup`. RED naming 5 real offenders: `PauseAllConsumerDefinition.cs`, `PauseWorkflowConsumerDefinition.cs`, `StartOrchestrationConsumerDefinition.cs`, `StepCompletedConsumerDefinition.cs`, `StopOrchestrationConsumerDefinition.cs`.
- **FACT 7 (RED-now, has teeth):** `ConfigureError_is_keeper_local_only` — RED because `e.ConfigureError(ep =>` still lives in the global `MessagingServiceCollectionExtensions.cs` callback (asserts it is gone from global AND present in `RecoveryEndpointBinder.cs`).
- **FACT 8 (RED-now, has teeth):** `Dead_WorkflowRootNotFound_ignore_removed_from_start_stop_definitions` — RED because `r.Ignore<WorkflowRootNotFoundException>` still sits in a Start/Stop definition (pure-teardown, no DLQ seam).
- **RepoRoot anchor + false-pass guard:** Copied the `RepoRoot([CallerFilePath])` walk-to-`SK_P.sln` idiom verbatim from `ReactivePathRetiredFacts.cs`; every source-scan fact asserts `Directory.Exists`/`File.Exists` before scanning (T-53-01 mitigation).

## Task Commits

Each task was committed atomically (TDD `test(...)` commits — these ARE the test deliverable):

1. **Task 1: 5->3 keeper-state reflection fact (FACT 5)** — `2bbf1f4` (test)
2. **Task 2: RepoRoot anchor + D-01/D-03/D-07 source-scan facts (FACTS 6/7/8)** — `46aa5e3` (test)

**Plan metadata:** _(final docs commit — SUMMARY/STATE/ROADMAP/REQUIREMENTS)_

## Files Created/Modified
- `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs` — extended the existing Phase-50 guard class with 5 new facts (FACT 5–8 + RepoRoot anchor); added `using MassTransit;` (FACT 5 `IConsumer<>`) and `using System.Runtime.CompilerServices;` (`[CallerFilePath]`).

## Expected-RED State (CRITICAL — do NOT misread as failure)

This plan is "guards RED-first" / bisect-friendly Wave 0. The final test state is INTENTIONAL:

| Fact | Test | State now | Why |
|------|------|-----------|-----|
| FACT 5 | `Keeper_registers_exactly_three_recovery_consumers` | **GREEN** | Verification half — states already collapsed (Phase 50/52) |
| FACT 6 | `No_bus_retry_or_error_transport_on_execution_path_endpoints` | **RED (expected)** | Bus retry / `ConfigureError` still on exec path — stripped in Wave-1 plan 02/03 |
| FACT 7 | `ConfigureError_is_keeper_local_only` | **RED (expected)** | Filter still global, not yet keeper-local — moved in Wave-1 |
| FACT 8 | `Dead_WorkflowRootNotFound_ignore_removed_from_start_stop_definitions` | **RED (expected)** | Dead `Ignore<>` still present — removed in Wave-1 |

**Verified result:** `dotnet test ... --filter-trait "Phase=53"` → `Total: 4, Passed: 1, Failed: 3`. The 3 failures are the expected-RED guards, each failing with a genuine assertion that NAMES real offending src files (proving they have teeth — not anchor false-passes). Wave-1 plans 02 + 03 turn FACTS 6/7/8 GREEN. **RED here is success, not a regression.** The orchestrator spot-check should NOT treat these 3 RED facts as a failed plan.

## Decisions Made
- **Omitted the exec-path assembly anchors** (`Orchestrator`/`BaseProcessorCore`) the plan flagged as optional — the 3 source-scan facts use `RepoRoot()` file paths, not assemblies, so adding the anchors would leave unused fields and break the mandated 0-warning Release build. (Plan explicitly preferred omitting unused anchors.)
- **CALL-pattern, not bare-word scan** for FACT 6 (PATTERNS Finding 2 / RESEARCH Pitfall 3) — matched `endpointConfigurator.UseMessageRetry(` / `cfg.UseMessageRetry(` / `.ConfigureError(`. Confirmed: the offender list contains only real CALLs, not the ~9 surviving doc-comment lines.

## Deviations from Plan

None — plan executed exactly as written. No code outside the test file was touched (the expected-RED guards were NOT "fixed" by altering production code — that is Wave-1 scope).

## Issues Encountered
- **MTP `--filter` quirk (tooling, not a code issue):** the test project runs on Microsoft.Testing.Platform; the VSTest-style `dotnet test --filter "FullyQualifiedName~..."` is ignored (warning MTP0001) and runs the whole suite. Resolved by using the MTP-native `-- --filter-trait "Phase=53"`, which correctly isolated the 4 Phase-53 facts. The full-suite run also surfaced 5 pre-existing RealStack failures (bus "Not Started" — the known docker-dependent E2E tests, unrelated to this plan).

## Verification
- `dotnet build tests/BaseApi.Tests -c Release` — **0 Warning / 0 Error** (both `using`s consumed).
- `dotnet test ... -- --filter-trait "Phase=53"` — **Passed: 1, Failed: 3, Total: 4** (FACT 5 GREEN; FACTS 6/7/8 expected-RED, each naming real offenders).

## Known Stubs
None — this plan adds only hermetic test code (reflection + `src/`-scoped `File.ReadAllText`). The 3 RED facts are intentional Wave-0 placeholders awaiting Wave-1 removal, not stubs.

## Next Phase Readiness
- Wave-1 plans 53-02 (guards/orchestrator+processor retry/_error strip) and 53-03 (filter keeper-scoping) now have standing facts that fail loudly on a missed removal — including the dual-owner Stop retry (RESEARCH Pitfall 3) and all 5 FACT-6 offenders. Each removal commit is now self-verified.
- No blockers.

## Self-Check: PASSED
- FOUND: `.planning/phases/53-model-b-teardown/53-01-SUMMARY.md`
- FOUND: `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs`
- FOUND commit: `2bbf1f4` (Task 1 — FACT 5)
- FOUND commit: `46aa5e3` (Task 2 — FACTS 6/7/8 + RepoRoot)

---
*Phase: 53-model-b-teardown*
*Completed: 2026-06-11*
