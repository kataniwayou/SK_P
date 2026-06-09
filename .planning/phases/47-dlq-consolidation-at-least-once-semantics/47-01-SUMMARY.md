---
phase: 47-dlq-consolidation-at-least-once-semantics
plan: 01
subsystem: testing
tags: [masstransit, dlq, reflection-guard, source-scan, at-least-once, structural-invariant, resilience]

# Dependency graph
requires:
  - phase: 36-keeper-l2-health-probe-recovery-loop-dlqs
    provides: ConsolidatedErrorTransportFilter (skp-dlq-1) + KeeperDlqConsolidationTests BuildHarness rig
  - phase: 43-message-contracts-l2-key-reshape
    provides: RETIRE-01 removal of H/flag[H]/CAS dedup (MessageIdentity deleted) on the execution path
  - phase: 44-processor-pre-in-post-process-pipeline
    provides: ProcessorPipeline throw sent.Error! send-exhaustion seam (D-10 propagation to _error)
provides:
  - "Standing fact framing the processor pipeline's send-exhaustion (throw sent.Error!) as routing to the consolidated skp-dlq-1 (RESIL-02 / R1)"
  - "Reflection regression guard: no MessageIdentity type/member survives on Orchestrator + BaseProcessor.Core (RESIL-03 / R4)"
  - "Source-scan regression guard: no v4 give-up path under Processing/ or Recovery/ references keeper-dlq, excluding KeeperRecoveryHandler.cs (RESIL-02 / R1 structural)"
affects: [48-reactive-path-keeper-dlq-retirement, 47-verification, 47-dlq-audit]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Reflection type/member-absence guard over execution-path assemblies (firewall-test idiom) for no-dedup invariant"
    - "Directory-scoped source-scan with CallerFilePath repo-root resolution + exist-before-scan false-pass guard"

key-files:
  created:
    - tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs
  modified:
    - tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs

key-decisions:
  - "Processor send-exhaustion fact uses a throwaway ProcessorSendExhausted record/consumer (hermetic equivalent of throw sent.Error!) in a local harness variant rather than booting a real ProcessorPipeline (no Redis/sendProvider in the rig) — 47-RESEARCH A2"
  - "No-dedup guard is reflection (type/member NAME absence), NOT a string-scan, to sidestep the legitimate BIT-gate string H member on PauseWorkflow/ResumeWorkflow (Pitfall 2)"
  - "Repo-root resolved via [CallerFilePath] walking up to SK_P.sln, with both scoped dirs asserted to exist before the scan (T-47-01 false-pass guard)"

patterns-established:
  - "Phase-trait-tagged structural invariants for at-least-once + single-DLQ, discoverable via --filter-trait Phase=47"

requirements-completed: [RESIL-02, RESIL-03]

# Metrics
duration: 13min
completed: 2026-06-09
---

# Phase 47 Plan 01: DLQ Consolidation & At-Least-Once Structural Invariants Summary

**Three hermetic Phase-47 facts proving the single-DLQ-consolidation (RESIL-02) and no-dedup (RESIL-03) structural invariants: a processor send-exhaustion -> skp-dlq-1 fact, a reflection no-MessageIdentity guard, and a directory-scoped no-keeper-dlq source-scan.**

## Performance

- **Duration:** ~13 min
- **Started:** 2026-06-09T06:35:55Z
- **Completed:** 2026-06-09T06:49:22Z
- **Tasks:** 2
- **Files modified:** 2 (1 created, 1 modified)

## Accomplishments

- `ProcessorSendExhaustion_RoutesToDlq1` [Fact] frames the ProcessorPipeline's `throw sent.Error!` (send-exhaustion) as routing the faulted message to the ONE consolidated `ConsolidatedErrorTransportFilter.Dlq1` (skp-dlq-1) as a `ConsolidatedFault`, with `GenerateFaultFilter` retained (Fault<T> still published) — R1 explicit framing, no `"skp-dlq-1"` literal.
- `No_dedup_machinery_on_execution_path` reflection guard: no type named `MessageIdentity` and no public property/field named `MessageIdentity` survives on the Orchestrator or BaseProcessor.Core assemblies (R4 / RESIL-03).
- `No_v4_give_up_path_references_keeper_dlq` source-scan: enumerates `*.cs` under `src/BaseProcessor.Core/Processing/` + `src/Keeper/Recovery/`, excludes `KeeperRecoveryHandler.cs`, and asserts no remaining file references `KeeperQueues.DeadLetter` or `"keeper-dlq"` (R1 structural / RESIL-02); both scoped dirs asserted present first.
- All three facts carry `[Trait("Phase","47")]` and are discoverable via `--filter-trait "Phase=47"`.

## Task Commits

1. **Task 1: Processor send-exhaustion -> skp-dlq-1 sibling fact** - `43e4855` (test)
2. **Task 2: AtLeastOnceStructuralFacts - reflection no-dedup + source-scan no-keeper-dlq** - `4c771f3` (test)

_Note: Task 1 is tdd-flagged but verify-only (the production wiring already exists from Phase 36), so the new fact was green on first run — no separate RED commit, as no production code change was warranted._

## Files Created/Modified

- `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` - Added `ProcessorSendExhausted` record + `ProcessorSendExhaustedConsumer`, a `BuildProcessorHarness` variant, and the `ProcessorSendExhaustion_RoutesToDlq1` [Fact] (Phase=47).
- `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` - New `BaseApi.Tests.Resilience` namespace; reflection no-dedup guard (FACT A) + directory-scoped no-keeper-dlq source-scan (FACT B), both Phase=47.

## Decisions Made

- Used a throwaway `ProcessorSendExhausted`/`ProcessorSendExhaustedConsumer` to simulate `throw sent.Error!` in a `BuildProcessorHarness` variant rather than booting a real `ProcessorPipeline` (the rig lacks Redis/IDatabase/sendProvider) — 47-RESEARCH A2.
- No-dedup guard implemented as reflection over loaded assemblies (type/member name absence), NOT a source string-scan, to avoid false-positives on the legitimate positional `string H` BIT-gate member on `PauseWorkflow`/`ResumeWorkflow` (Pitfall 2).
- Repo root resolved via `[CallerFilePath]` walking up to `SK_P.sln`; both scoped directories asserted to exist before enumeration to prevent a silently-empty scan false pass (T-47-01).

## Deviations from Plan

None - plan executed exactly as written. No production source change was needed; FACT B found no real offender (KeeperRecoveryHandler.cs is the sole legitimate keeper-dlq sender, confirmed pre-write).

## Landmines Honored

- **Reflection, not string-scan (no-dedup):** FACT A uses `GetTypes()` + `GetProperties`/`GetFields` over the Orchestrator + BaseProcessor.Core assemblies — never a text scan — so the legitimate BIT-gate `string H` member is correctly invisible.
- **KeeperRecoveryHandler.cs excluded (no-keeper-dlq):** FACT B's source-scan filters with the exact line `.Where(f => Path.GetFileName(f) != "KeeperRecoveryHandler.cs")` and scans for BOTH `KeeperQueues.DeadLetter` and `keeper-dlq`.

## Surfaced Finding

None — verify-only, no production change. Pre-write grep confirmed the only `keeper-dlq` / `KeeperQueues.DeadLetter` references under the two scoped dirs are in `KeeperRecoveryHandler.cs` (the mandated exclusion). The exclusion was NOT widened.

## Issues Encountered

None. `--filter-not-trait "Category=RealStack"` still emits MassTransit "Connection Failed: rabbitmq" retry log noise from broker-bound harness teardown, but the run reported 528/528 passed / 0 failed (the broker-dependent E2E noise is the documented pre-existing condition, not a test failure).

## Verification Results

- `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"` -> 3/3 passed.
- `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*ProcessorSendExhaustion*"` -> 1/1 passed.
- Full hermetic suite (`--filter-not-trait "Category=RealStack"`) -> 528/528 passed, 0 failed.
- `dotnet build SK_P.sln -c Release` -> 0 warnings / 0 errors.
- Encoding: both files BOM-less UTF-8, valid, no mojibake; new fact body references `ConsolidatedErrorTransportFilter.Dlq1` (no new `"skp-dlq-1"` literal).

## Next Phase Readiness

- Standing guards now block re-introduction of a second DLQ or a dedup key on the execution path — ready for Phase 48 (reactive-path + keeper-dlq retirement). When KeeperRecoveryHandler.cs is removed in Phase 48, the FACT B exclusion line becomes a no-op (the file no longer exists) and the scan still passes.
- Supports the 47-DLQ-AUDIT.md traceability deliverable (each RESIL-02/03 row resolves to a real green Phase-47 fact).

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs
- FOUND: tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs
- FOUND: .planning/phases/47-dlq-consolidation-at-least-once-semantics/47-01-SUMMARY.md
- FOUND commit: 43e4855 (Task 1)
- FOUND commit: 4c771f3 (Task 2)

---
*Phase: 47-dlq-consolidation-at-least-once-semantics*
*Completed: 2026-06-09*
