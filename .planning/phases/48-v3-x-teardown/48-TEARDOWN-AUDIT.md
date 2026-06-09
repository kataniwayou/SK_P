# Phase 48 — v3.x Reactive/Keeper-DLQ Teardown Audit Ledger

**Phase:** 48-v3-x-teardown
**Date:** 2026-06-09
**Purpose:** Maps RETIRE-01 / RETIRE-02 / RETIRE-03 and roadmap SC-1 / SC-2 / SC-3 / SC-4 to their proving tests. Every RETIRE/SC row resolves to a real GREEN hermetic guard test or source-scan (authored across Phases 47/48-01/48-02) — each retirement invariant has at least one named proving test. This is the phase's consolidated traceability deliverable (D-04): proof that the v3.x reactive `Fault<T>` Keeper recovery path + the `keeper-dlq` / `keeper-fault-recovery` queues are gone and cannot silently regress, gathered here one row per criterion (mirrors `47-DLQ-AUDIT.md`).

The roadmap success criteria are:
- **SC-1** — no `keeper-dlq` on the v4 give-up path; the consolidated `skp-dlq-1` is the sole terminal dead-letter (the reactive-recovery `keeper-dlq` const is removed).
- **SC-2** — RETIRE-02 remnant-verify: `L2ProjectionKeys.ExecutionData` is the single-`Guid` overload and no `*Manifest*` type survives on the execution-path assemblies (content-addressing + result manifest gone).
- **SC-3** — RETIRE-03 retirement: no reactive `Fault<T>` consumer / `KeeperRecoveryHandler` survives, no `keeper-dlq` / `keeper-fault-recovery` literal under `src/Keeper/`, and `KeeperQueues` exposes only the surviving `Recovery` const.
- **SC-4** — close gate: the full hermetic suite is GREEN ×3 consecutive and both Release AND Debug builds are 0-warning on the v4-only path (all retired machinery deleted).

The retirement *statement* itself is recorded additively in the design doc — see `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` amendment **A17** (Task 1 of this plan), which cross-references this ledger.

---

## Traceability Ledger

| Requirement | Roadmap SC | Invariant / behavior | Proving test (file:method) | Status | Verify command |
|-------------|-----------|----------------------|----------------------------|--------|----------------|
| RETIRE-01 | SC-1 | No dedup machinery (`H` / `MessageIdentity` / `flag[H]` CAS) survives on the execution-path assemblies (reflection guard) | `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs:No_dedup_machinery_on_execution_path` | cite-existing (Phase 47, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*No_dedup_machinery_on_execution_path*"` |
| RETIRE-01 | SC-1 | Structural: no v4 give-up path references the retired `keeper-dlq` / `KeeperQueues.DeadLetter` — now scanned UNCONDITIONALLY (the `KeeperRecoveryHandler.cs` exclusion was removed in Plan 02 since the file is deleted) | `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs:No_v4_give_up_path_references_keeper_dlq` | widened (Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*No_v4_give_up_path_references_keeper_dlq*"` |
| RETIRE-02 | SC-2 | `L2ProjectionKeys.ExecutionData` has exactly one overload whose single parameter is `typeof(Guid)` (content-addressing gone), AND no `*Manifest*` type survives on the Orchestrator + BaseProcessor.Core execution-path assemblies (result manifest + N×M fan-out gone) | `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs:ExecutionData_is_guid_only_and_no_manifest_type_survives` | new (Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*ExecutionData_is_guid_only_and_no_manifest_type_survives*"` |
| RETIRE-03 | SC-3 | FACT 1 (reflection): no Keeper-assembly type is named `FaultEntryStepDispatchConsumer` / `FaultExecutionResultConsumer` / `KeeperRecoveryHandler`, AND no type implements `IConsumer<Fault<T>>` for any `T` (a reactive consumer re-introduced under ANY name is caught) | `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs:No_reactive_fault_consumer_survives_on_keeper_assembly` | new (Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*No_reactive_fault_consumer_survives_on_keeper_assembly*"` |
| RETIRE-03 | SC-3 | FACT 2 (source-scan): recursive `src/Keeper/` scan (fail-loud `Directory.Exists` guard) finds no `keeper-fault-recovery` / `keeper-dlq` / `KeeperQueues.FaultRecovery` / `KeeperQueues.DeadLetter` literal | `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs:No_retired_reactive_literal_under_src_keeper` | new (Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*No_retired_reactive_literal_under_src_keeper*"` |
| RETIRE-03 | SC-3 | FACT 3 (const absence): `KeeperQueues` public-static fields — `FaultRecovery` / `DeadLetter` absent, `Recovery` present (the sole surviving Keeper queue) | `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs:KeeperQueues_has_only_recovery_const` | new (Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*KeeperQueues_has_only_recovery_const*"` |
| RETIRE-01/02/03 | SC-3 | Batch: all four `[Trait("Phase","48")]` retirement guards GREEN and non-empty (the fail-loud `Directory.Exists` guards ensure this is not a silently-empty false pass) | `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` (4 facts) | new (Plan 02, green: 4/4) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"` |
| RETIRE-01/02/03 | SC-4 | Close gate: full hermetic suite GREEN ×3 consecutive + Release AND Debug builds at 0 warnings on the v4-only path | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-not-trait Category=RealStack` (×3) + `dotnet build SK_P.sln -c Release`/`-c Debug` | **MET** (Plan 03, Task 2): 507/507 ×3, 0 failed; Release 0 warn; Debug 0 warn — see result below | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-not-trait Category=RealStack` (×3) ; `dotnet build SK_P.sln -c Release` ; `dotnet build SK_P.sln -c Debug` |

### SC-4 close-gate result (captured in Task 2, 2026-06-09)

- **Hermetic suite GREEN ×3 consecutive** (RealStack-excluded, MTP `--filter-not-trait Category=RealStack`): **507 / 507** passed, **0 failed**, 0 skipped — three consecutive runs, each `EXIT=0`. (`dotnet test SK_P.sln`'s VSTest `--filter` is ignored under Microsoft.Testing.Platform — `warning MTP0001` — so the MTP-native `dotnet run ... -- --filter-not-trait` form is the canonical hermetic command; the 2 RealStack E2E tests it excludes are the pre-existing docker-dependent ones — `rabbitmq://rabbitmq/` host-unreachable — documented in 48-01-SUMMARY + PROJECT.md, not v4-path regressions.)
- **`dotnet build SK_P.sln -c Release`**: Build succeeded — **0 Warning(s), 0 Error(s)**, `EXIT=0`.
- **`dotnet build SK_P.sln -c Debug`**: Build succeeded — **0 Warning(s), 0 Error(s)**, `EXIT=0`.

The SC-4 hermetic close gate is **met**. The live / real-stack + triple-SHA net-zero close gate is deferred to **Phase 49** (TEST-01..03) per D-03.

---

## Coverage confirmation

- **RETIRE-01** (`H` / dedup gate / CAS removed) — proven by 2 rows: the reflection no-dedup guard (`No_dedup_machinery_on_execution_path`, cited from Phase 47) + the now-unconditional `keeper-dlq` source-scan (`No_v4_give_up_path_references_keeper_dlq`, widened in Plan 02). Maps SC-1.
- **RETIRE-02** (content-addressing + result manifest + N×M fan-out removed) — proven by 1 row: the SC-2 remnant-verify (`ExecutionData_is_guid_only_and_no_manifest_type_survives`). Maps SC-2.
- **RETIRE-03** (reactive `Fault<T>` recovery path + `keeper-dlq` queue removed) — proven by 3 rows: FACT 1 reflection no-Fault-consumer, FACT 2 `src/Keeper/` source-scan, FACT 3 `KeeperQueues` const absence. Maps SC-3.
- **SC-4** (close gate) — 1 row: the ×3-GREEN + Release/Debug 0-warning build result captured in Task 2.

All three RETIRE ids and all four roadmap SCs carry at least one named, green proving test/scan.

Quick batch verification of the Phase-48 retirement guards:

```
dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=48"
```

(4 facts: `No_reactive_fault_consumer_survives_on_keeper_assembly`, `No_retired_reactive_literal_under_src_keeper`, `KeeperQueues_has_only_recovery_const`, `ExecutionData_is_guid_only_and_no_manifest_type_survives`.) The two RETIRE-01/SC-1 facts (`No_dedup_machinery_on_execution_path`, `No_v4_give_up_path_references_keeper_dlq`) carry the `[Trait("Phase","47")]` tag (cited / widened from Phase 47) and run under their `--filter-method` commands above.

---

## Scope note

This ledger proves the reactive-path + `keeper-dlq` retirement **hermetically** (reflection + recursive source-scan over the torn-down `src/Keeper/` tree). The **live / real-stack** proof + the triple-SHA (psql / redis / rabbitmq) net-zero close gate is deferred to **Phase 49 (TEST-01..03)** per D-03; the SC-4 gate here is the hermetic-only close gate (suite GREEN ×3 + Release/Debug 0-warning build), the in-memory MassTransit harness + FakeRedis double covering everything available in this environment.

The reactive-path + `keeper-dlq` retirement **statement** is recorded additively in the design doc (`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`, amendment **A17**), which cites this ledger as the traceability proof.
