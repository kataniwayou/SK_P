# Phase 47 — DLQ Consolidation & At-Least-Once Audit Ledger

**Phase:** 47-dlq-consolidation-at-least-once-semantics
**Date:** 2026-06-09
**Purpose:** Maps RESIL-02 / RESIL-03 and roadmap SC-1 / SC-2 / SC-3 to their proving tests. Every row resolves to a real GREEN hermetic test (file:method) — each invariant has at least one named proving test. This is the phase's consolidated traceability deliverable (R5): proof that was previously scattered across Phases 36/43/44/46 and the new Phase-47 facts is gathered here, one row per invariant.

The roadmap success criteria are:
- **SC-1** — single-DLQ consolidation: every terminal give-up routes to the one consolidated `skp-dlq-1`; no `keeper-dlq` on the v4 path.
- **SC-2** — at-least-once / no-dedup: duplicate deliveries reproduce their effect (no collapse); no dedup/idempotency machinery survives on the execution path.
- **SC-3** — data-gone terminal: a `REINJECT` whose `L2[entryId]` is truly gone faults to the consolidated dead-letter rather than looping.

The at-least-once guarantee *statement* itself lives in the design doc — see `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` amendment **A16** (Task 2 of this plan), which cross-references this ledger.

---

## Traceability Ledger

| Requirement | Roadmap SC | Invariant / behavior | Proving test (file:method) | Status | Verify command |
|-------------|-----------|----------------------|----------------------------|--------|----------------|
| RESIL-02 (R1) | SC-1 | Generic transport-exhaustion routes to the ONE consolidated `skp-dlq-1` as a `ConsolidatedFault` (and `Fault<T>` is still published) | `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs:Dlq1_Consolidated` | cite-existing (Phase 36, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*Dlq1_Consolidated*"` |
| RESIL-02 (R1) | SC-1 | A throwing/infra-faulting consumer exhausts `Immediate(N)` and routes to the consolidated `skp-dlq-1` (the identical endpoint path a Keeper Send/Redis fault takes) | `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs:Keeper_SendFault_RetriesToDlq1` | cite-existing (Phase 36, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*Keeper_SendFault_RetriesToDlq1*"` |
| RESIL-02 (R1) | SC-1 | Processor send-exhaustion (`throw sent.Error!`) routes the faulted message to the consolidated `skp-dlq-1` as a `ConsolidatedFault` | `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs:ProcessorSendExhaustion_RoutesToDlq1` | new gap-fill (Plan 01, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*ProcessorSendExhaustion*"` |
| RESIL-02 (R1) | SC-1 | Structural: no v4 give-up path under `Processing/` or `Recovery/` references `keeper-dlq` / `KeeperQueues.DeadLetter` (excl. the dormant `KeeperRecoveryHandler.cs`, retired Phase 48) | `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs:No_v4_give_up_path_references_keeper_dlq` | new gap-fill (Plan 01, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"` |
| RESIL-02 (R2) | SC-3 | Data-gone `REINJECT` (`L2[entryId]` absent/empty) faults with `RecoveryDataGoneException` and routes to the consolidated dead-letter — terminates, does not loop | `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:DataGone_reinject_faults_and_routes_to_dead_letter` | cite-existing (Phase 46; Phase-47 re-tagged by Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*DataGone_reinject*"` |
| RESIL-03 (R3) | SC-2 | Duplicate `StepCompleted` delivery reproduces its effect with no collapse — ONE dispatcher, same instance Consumed twice, `Calls.Count == 2`, no lost branch | `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs:Duplicate_StepCompleted_reproduces_effect_no_collapse` | new gap-fill (Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*Duplicate_StepCompleted*"` |
| RESIL-03 (R3) | SC-2 | Duplicate `KeeperReinject` delivery on the EntryStepDispatch-family seam reproduces its effect — ONE consumer, same instance Consumed twice, `Sent.Count == 2` | `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:Duplicate_Reinject_reproduces_effect_no_collapse` | new gap-fill (Plan 02, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-method "*Duplicate_Reinject*"` |
| RESIL-03 (R4) | SC-2 | Structural: no `MessageIdentity` type/dedup member survives on the Orchestrator + BaseProcessor.Core execution-path assemblies (reflection guard, not string-scan) | `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs:No_dedup_machinery_on_execution_path` | new gap-fill (Plan 01, green) | `dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"` |

---

## Coverage confirmation

- **RESIL-02** — proven by 5 rows: generic consolidation (`Dlq1_Consolidated`, `Keeper_SendFault_RetriesToDlq1`), processor send-exhaustion (`ProcessorSendExhaustion_RoutesToDlq1`), the no-`keeper-dlq` structural fact (`No_v4_give_up_path_references_keeper_dlq`), and the data-gone terminal (`DataGone_reinject_faults_and_routes_to_dead_letter`). Maps R1 (SC-1) and R2 (SC-3).
- **RESIL-03** — proven by 3 rows: the two no-collapse duplicate-delivery facts (`Duplicate_StepCompleted_*`, `Duplicate_Reinject_*`) and the reflection no-dedup fact (`No_dedup_machinery_on_execution_path`). Maps R3/R4 (SC-2).
- **SC-1** (consolidation) — 4 rows. **SC-2** (at-least-once / no-dedup) — 3 rows. **SC-3** (data-gone terminal) — 1 row. All three roadmap SCs and both RESIL ids carry at least one named, green proving test.

Quick batch verification of the new/re-tagged Phase-47 facts:

```
dotnet run --project tests/BaseApi.Tests -c Debug -- --filter-trait "Phase=47"
```

(6 facts: `ProcessorSendExhaustion_RoutesToDlq1`, `No_v4_give_up_path_references_keeper_dlq`, `No_dedup_machinery_on_execution_path`, `DataGone_reinject_faults_and_routes_to_dead_letter`, `Duplicate_StepCompleted_reproduces_effect_no_collapse`, `Duplicate_Reinject_reproduces_effect_no_collapse`.) The two cited-existing generic-consolidation facts (`Dlq1_Consolidated`, `Keeper_SendFault_RetriesToDlq1`) are not Phase-47-tagged — they are cited from Phase 36 and run under their `--filter-method` commands above.

---

## Scope note

This ledger proves the consolidation + no-collapse + data-gone invariants **hermetically** (in-memory MassTransit harness + reflection/source-scan). The **live / real-stack** proof — a message actually landing in the broker `skp-dlq-1` with its `x-message-ttl` applied — is deferred to **Phase 49 (TEST-01..03)** as a RealStack close gate; the in-memory harness cannot exercise RabbitMQ transport TTL behavior.

The at-least-once / no-dedup **guarantee statement** is recorded in the design doc (`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`, amendment **A16**), which cites this ledger as the traceability proof.
