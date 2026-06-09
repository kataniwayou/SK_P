---
phase: 48-v3-x-teardown
plan: 02
subsystem: infra
tags: [keeper, masstransit, teardown, negative-guard, regression-proof, verification]

# Dependency graph
requires:
  - phase: 48-v3-x-teardown
    plan: 01
    provides: the deleted reactive Fault<T> surface + KeeperRecoveryHandler + the removed keeper-dlq/keeper-fault-recovery consts that FACT 1/2/3 assert ABSENT, and the deleted KeeperRecoveryHandler.cs that lets the Phase-47 scan widen
provides:
  - ReactivePathRetiredFacts.cs — a 4-fact [Trait("Phase","48")] negative-guard class that makes the RETIRE-03 teardown self-verifying (FACT 1 reflection no-Fault<T>-consumer, FACT 2 recursive src/Keeper source-scan, FACT 3 KeeperQueues const absence, FACT 4 SC-2 ExecutionData-Guid-only + no-Manifest)
  - The widened Phase-47 keeper-dlq scan — the KeeperRecoveryHandler.cs exclusion removed, src/Keeper/Recovery/ now scanned unconditionally (RETIRE-01/SC-1 widen)
affects: [48-03, keeper, recovery, validation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Negative-guard fact class: reflection (assembly anchored on a SURVIVING type) + recursive fail-loud source-scan over a scoped src/ dir — mirrors AtLeastOnceStructuralFacts verbatim, anchored on the teardown's DELETED targets"
    - "Interface-shape reflection guard: IConsumer<Fault<>> closed-generic check catches a reactive consumer re-introduced under ANY type name (stronger than a name-only DoesNotContain)"

key-files:
  created:
    - tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs
  modified:
    - tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs

key-decisions:
  - "Assembly anchor = typeof(global::Keeper.Health.BitHealthLoop).Assembly — the SAME surviving type the firewall test was re-anchored to in Plan 01, NOT a deleted Consumers.Fault* type."
  - "SC-2 no-Manifest assertion anchored on the Orchestrator (StepDispatcher) + BaseProcessor.Core (ProcessorPipeline) execution-path assemblies — the same pair AtLeastOnceStructuralFacts uses."
  - "Acceptance criterion read strictly: ZERO 'KeeperRecoveryHandler.cs' mentions in AtLeastOnceStructuralFacts (exclusion line AND every doc-comment filename mention removed), not just the .Where(...) line."

requirements-completed: [RETIRE-03, RETIRE-01, RETIRE-02]

# Metrics
duration: 9min
completed: 2026-06-09
---

# Phase 48 Plan 02: Reactive-Path Retirement Negative Guards Summary

**Authored the four-fact `ReactivePathRetiredFacts` class (anchored on the surviving `BitHealthLoop` assembly) that makes the RETIRE-03 teardown self-verifying and regression-proof, added the SC-2 RETIRE-02 remnant-verify (ExecutionData-Guid-only + no-Manifest), and widened the Phase-47 `keeper-dlq` scan to be unconditional now that `KeeperRecoveryHandler.cs` is deleted — Phase=48 trait run is 4/4 GREEN (non-empty) and the widened Phase-47 scan stays GREEN.**

## Performance

- **Duration:** ~9 min
- **Completed:** 2026-06-09
- **Tasks:** 2
- **Files:** 1 created + 1 modified

## The new fact class + its four facts

`tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` (public sealed class, namespace `BaseApi.Tests.Resilience`), every fact `[Fact] [Trait("Phase", "48")]`:

1. **FACT 1 — `No_reactive_fault_consumer_survives_on_keeper_assembly` (SC-3, reflection):** asserts no Keeper-assembly type is named `FaultEntryStepDispatchConsumer`/`FaultExecutionResultConsumer`/`KeeperRecoveryHandler`, AND (the stronger interface-shape check) no type implements `IConsumer<Fault<T>>` for any `T` — a reactive consumer re-introduced under ANY name is caught.
2. **FACT 2 — `No_retired_reactive_literal_under_src_keeper` (SC-3, source-scan):** recursive (`SearchOption.AllDirectories`) scan over `src/Keeper/` with a fail-loud `Directory.Exists` guard (T-47-01) — asserts no `keeper-fault-recovery` / `keeper-dlq` / `KeeperQueues.FaultRecovery` / `KeeperQueues.DeadLetter` literal survives. No `KeeperRecoveryHandler.cs` exclusion (the file is deleted).
3. **FACT 3 — `KeeperQueues_has_only_recovery_const` (SC-3, const absence):** reflection over `KeeperQueues` public-static fields — `FaultRecovery`/`DeadLetter` absent, `Recovery` present.
4. **FACT 4 — `ExecutionData_is_guid_only_and_no_manifest_type_survives` (SC-2, RETIRE-02 remnant-verify):** `L2ProjectionKeys.ExecutionData` has exactly one overload (`Assert.Single`) whose single parameter is `typeof(Guid)`, AND no `*Manifest*` type survives on the Orchestrator + BaseProcessor.Core execution-path assemblies.

## Surviving-type anchor used

`private static readonly Assembly Keeper = typeof(global::Keeper.Health.BitHealthLoop).Assembly;` — the SAME surviving type the Plan-01 firewall test was re-anchored to (the deleted `Consumers.Fault*` types could not be used as an anchor). SC-2's no-Manifest check is anchored on `Orchestrator.Dispatch.StepDispatcher` + `BaseProcessor.Core.Processing.ProcessorPipeline` (the AtLeastOnceStructuralFacts pair). `RepoRoot()` copied verbatim (the `[CallerFilePath]` walk-up-to-`SK_P.sln` + `Assert.NotNull` guard).

## Task Commits

Each task was committed atomically:

1. **Task 1: Author `ReactivePathRetiredFacts.cs` (FACT 1+2+3+SC-2)** — `7bf0f18` (test)
2. **Task 2: Widen the Phase-47 keeper-dlq scan (remove the deleted-file exclusion)** — `3ef4248` (test)

## Files Created/Modified

**Created (Task 1):**
- `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` — 162 lines, 4 facts, anchored on `BitHealthLoop`.

**Modified (Task 2):**
- `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` — removed the `.Where(f => Path.GetFileName(f) != "KeeperRecoveryHandler.cs")` exclusion (the `src/Keeper/Recovery/` scan is now unconditional) and reworded FACT B + the class summary doc-comments to drop every `KeeperRecoveryHandler.cs` filename mention (acceptance: 0 matches). The `[Trait("Phase","47")]` tag, scoped dirs, offender literals, and T-47-01 fail-loud guard are unchanged.

## Verification

- **Phase=48 trait run GREEN and non-empty:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` → **total: 4, failed: 0, succeeded: 4** (the fail-loud `Directory.Exists` guards ensure this is not a silently-empty false pass).
- **Widened Phase-47 scan stays GREEN:** `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*No_v4_give_up_path_references_keeper_dlq*"` → **total: 1, failed: 0** (the now-unconditional `src/Keeper/Recovery/` scan finds zero offenders — `KeeperRecoveryHandler.cs` is deleted).
- **`dotnet build SK_P.sln -c Debug` = 0 Warning / 0 Error.**
- Task-1 acceptance greps: trait count = 4 (≥4), `SearchOption.AllDirectories` present, `Directory.Exists` present, `BitHealthLoop).Assembly` anchor present. Task-2 acceptance grep: `KeeperRecoveryHandler.cs` = 0 matches.

## Deviations from Plan

None — plan executed exactly as written.

(Note on a strict-reading judgement, not a deviation: Task 2's acceptance criterion — "`grep -n "KeeperRecoveryHandler.cs"` returns NO matches (the exclusion line AND the comment mention are gone)" — was honored to the letter. The first reword pass kept the `.cs` filename in two doc-comments; a second pass dropped it to "dormant reactive recovery handler" so the grep returns exactly 0, matching the acceptance criterion's explicit wording.)

## Known Stubs

None — this is a verification-only (test-authoring) plan; no production code, no placeholder/stub data.

## Threat Flags

None — net security posture is unchanged / surface-reducing. The two artifacts are test-only and ENFORCE the deletion (T-48-01 + T-48-03 mitigations realized); no new runtime input/auth/crypto/data flow.

## Self-Check: PASSED

- `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` present on disk (created, 162 lines).
- `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` present on disk (modified, 0 `KeeperRecoveryHandler.cs` mentions).
- Both task commits present in git log: `7bf0f18`, `3ef4248`.
- No unexpected file deletions in either commit (`git diff --diff-filter=D HEAD~1 HEAD` empty for both).

---
*Phase: 48-v3-x-teardown*
*Completed: 2026-06-09*
