# Phase 43: Message Contracts & L2 Key Reshape - Context

**Gathered:** 2026-06-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Establish the reshaped **shared wire vocabulary** for v4.0.0 — no behavior change beyond what's structurally forced:

- `EntryStepDispatch` / `ExecutionResult` carry exactly the six ids (`correlationId, workFlowId, stepId, ProcessorId, executionId, entryId`) and **drop `H`**.
- `entryId` becomes a `Guid`; `Guid.Empty` is the explicit source-step sentinel (skip read + skip end-delete).
- Five Keeper-state contracts exist — `UPDATE` / `REINJECT` / `INJECT` / `DELETE` / `CLEANUP`.
- `L2ProjectionKeys` gains both new key schemes — the GUID **data key** (no TTL) and the **composite backup key** (configurable 2-day TTL).

The v4 Pre/In/Post pipeline (Phase 44), the BIT gate + global pause/resume (Phase 45), and the 5-state recovery consumer + orchestrator per-item consume (Phase 46) are OUT of this phase. Source of truth is LOCKED — see canonical refs.

</domain>

<decisions>
## Implementation Decisions

### Build-coexistence strategy (the load-bearing call)
- **D-01:** Reshape `EntryStepDispatch` / `ExecutionResult` **in place** in Phase 43 (drop wire `H`, `entryId` string→`Guid`). Because `H` is the key for the `flag[H]` CAS dedup and `entryId`-as-64-hex *is* content-addressing, those teardowns (RETIRE-01, RETIRE-02) are **coupled to the field removal and therefore move into Phase 43**. They cannot survive the type/field change, so they are not deferrable to Phase 48.
- **D-02:** **Phase 48 shrinks** as a consequence — it becomes RETIRE-03 (reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` recovery path + `keeper-dlq`) + a final remnant sweep only. (See Deferred Ideas for the ROADMAP reconciliation this implies.)
- **D-03:** **Bleed boundary = "dead-machinery removal + straight-through."** Phase 43 removes `flag[H]`/CAS, content-addressing, and the N×M manifest fan-out, then adapts the processor (`EntryStepDispatchConsumer`) and orchestrator (`ResultConsumer`, `StepDispatcher`) to the **simplest compile-and-pass** behavior on the new contracts: single result, no dedup, no manifest. Phase 43 does **NOT** rewrite consumers to v4 behavior — the full Pre/In/Post pipeline stays Phase 44; the 5-state recovery consumer + per-item orchestrator consume stay Phase 46.

### Execution contracts (MSG-01, MSG-02)
- **D-04:** `EntryStepDispatch` + `ExecutionResult` carry exactly the six ids; `H` removed from **both records and from `IExecutionCorrelated`**. A golden/contract test pins the shape and asserts `H` is absent (SC-1).
- **D-05:** `entryId` is a `Guid` on both contracts and on `IExecutionCorrelated` (was `string` 64-hex). `Guid.Empty` = source-step sentinel.
- **D-06:** `ExecutionResult` **keeps** `ErrorMessage` + `CancellationMessage` (business `Failed`/`Cancelled` still need a diagnostic message); only `H` is dropped. The `StepOutcome` enum (`Processing`/`Completed`/`Failed`/`Cancelled`, plain `int`) is reused **unchanged** — the author-throwable `processing` status already has a value.

### Guid.Empty source sentinel (MSG-02, SC-2)
- **D-07:** A **single shared predicate** recognizes the source-step sentinel so every consumer branches "skip read / skip end-delete" off ONE helper, not ad-hoc `== Guid.Empty` checks scattered through the pipeline. Lives in `Messaging.Contracts`.

### L2 keys (MSG-03, SC-4)
- **D-08:** **Data key** — one builder `L2ProjectionKeys.ExecutionData(Guid entryId) => skp:data:{entryId:D}`, **no TTL**. Replaces the content-addressed `ExecutionData(string)` + the transitional `ExecutionData(Guid)` overloads (both removed). Keeps the `data:` segment (disambiguates payload keys from root/step/processor GUID keys). Golden test pins it.
- **D-09:** **Composite backup key** — builder `L2ProjectionKeys.CompositeBackup(corr, wf, proc, exec) => skp:{correlationId:D}:{workFlowId:D}:{ProcessorId:D}:{executionId:D}`. `skp:`-prefixed for convention consistency with every other L2 key, **even though the design doc writes it bare**. Golden test pins it.
- **D-10:** **Composite backup TTL** — configurable in **days** via a new options class (e.g. `BackupOptions { TtlDays = 2 }`) bound from appsettings, mirroring the `ProbeOptions` precedent. Default 2 days. The TTL is a **crash-backstop only** — the copy is normally deleted by `CLEANUP`/`INJECT`.

### Five Keeper-state contracts (MSG-03, SC-3)
- **D-11:** Five records in `Messaging.Contracts` — `UPDATE` (+`validatedData`), `REINJECT`, `INJECT`, `DELETE`, `CLEANUP`. Per the design doc id-sets, **all five carry `{correlationId, workFlowId, stepId, ProcessorId, executionId}`**; `REINJECT`/`DELETE` additionally carry `entryId`; `UPDATE` additionally carries `validatedData`.
- **D-12:** All five implement a shared **marker interface `IKeeperRecoverable`** exposing the **partition 4-tuple** `correlationId / workFlowId / ProcessorId / executionId` — the key the MassTransit `UsePartitioner` uses for per-key ordering (KEEP-09, Phase 46). `stepId` rides as a plain property on each record, NOT part of the partition key (partition key == composite-backup key == 4-tuple, no step).
- **D-13:** New queue const `KeeperQueues.Recovery = "keeper-recovery"` for the (later, Phase 46) gate-open-only recovery consumer. The v3.7 `KeeperQueues.FaultRecovery` (`keeper-fault-recovery`) and `KeeperQueues.DeadLetter` (`keeper-dlq`) consts are retired in Phases 47/48, **not** here.

### Claude's Discretion
- Exact naming/shape of the D-07 sentinel helper (e.g. static `SourceStep.IsSource(Guid)` vs an extension method) — only requirement: it is the single referenced predicate.
- Exact member names on `IKeeperRecoverable`.
- Whether the five Keeper contracts also implement `ICorrelated` (their id-set includes `correlationId`; leaning **yes** for log-scope propagation consistency with the other consoles). They cannot implement `IExecutionCorrelated` (it now requires `entryId`, which `UPDATE`/`INJECT`/`CLEANUP` lack).
- The appsettings section name + binding wiring for `BackupOptions`.
- Golden-test file organization.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked design (source of truth)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — **LOCKED 2026-06-08.** §"Identities & L2" (six ids; `Guid.Empty` source sentinel; data key no-TTL + composite backup key + 2d configurable crash-backstop TTL); §"Keeper › Recovery consumer" (the five states, their exact id-sets, and the `corr:wf:ProcessorId:executionId` partition key); §"Locked decisions" rows A2 (absent/empty read = `infra(READ)` → `REINJECT`) and the `entryId`-GUID / no-TTL / 2d-backstop row.

### Requirements & roadmap
- `.planning/REQUIREMENTS.md` — **MSG-01, MSG-02, MSG-03** (this phase); **RESIL-02/03** + **RETIRE-01/02/03** (teardown partly pulled into 43 per D-01/D-02); "Out of Scope" (exactly-once-effect & any dedup/idempotency key deliberately removed).
- `.planning/ROADMAP.md` — §"Phase 43" (4 success criteria, depends-on none); §"Build order (locked)" (43→44→…→49); §"Phase 48" (teardown — reconcile per D-02).

### Existing code to reshape (in `Messaging.Contracts` unless noted)
- `src/Messaging.Contracts/EntryStepDispatch.cs` — drop `H`, `EntryId` string→`Guid`.
- `src/Messaging.Contracts/ExecutionResult.cs` — drop `H`, `EntryId` string→`Guid`, keep `ErrorMessage`/`CancellationMessage`.
- `src/Messaging.Contracts/IExecutionCorrelated.cs` — drop `H`, `EntryId` string→`Guid`.
- `src/Messaging.Contracts/StepOutcome.cs` — reused unchanged.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — new `ExecutionData(Guid)` single builder; new `CompositeBackup(...)`; remove content-addressed string + transitional Guid overloads + `Flag` (H-keyed).
- `src/Messaging.Contracts/KeeperQueues.cs` — add `Recovery` const.
- `src/Messaging.Contracts/Hashing/MessageIdentity.cs` — `H` computation; removed as part of the coupled RETIRE-01 bleed.

### Consumers adapted to "straight-through" in 43 (blast radius — 37 refs / 13 files)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — remove `flag[H]`/CAS + content-addressing; compile-and-pass on new contracts.
- `src/Orchestrator/Consumers/ResultConsumer.cs`, `src/Orchestrator/Dispatch/StepDispatcher.cs` — remove dedup + manifest fan-out; single-result behavior.
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs` — reads `H` (recovery key); affected by the interface change.
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` + `src/Messaging.Contracts/ExecutionLogScope.cs` — read `EntryId` for the log scope; `entryId` `Guid` change ripples here.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`L2ProjectionKeys`** single-source-of-truth pattern (Phase 21/22): add both new builders here; both forwarders consume them. Established golden-test pinning convention applies.
- **`ProbeOptions` precedent** (Phase 36): the template for the new `BackupOptions { TtlDays }` appsettings-bound options class.
- **`OrchestratorQueues` / `KeeperQueues` const precedent**: where the new `Recovery` queue const lives.
- **`IExecutionCorrelated : ICorrelated` interface layering** + **bus-envelope record convention** (no `[JsonPropertyName]`, default STJ) — the new Keeper contracts + `IKeeperRecoverable` follow this.
- **`StepOutcome`** already has `Processing/Completed/Failed/Cancelled` — no enum change needed.

### Established Patterns
- Wire contracts are `sealed record`s; ids are `init`-only; correlation flows via the interface hierarchy and the inbound log-scope filters.
- Per-key ordering for the recovery consumer is MassTransit `UsePartitioner` (design doc) — `IKeeperRecoverable` exists to feed it one predictable composite key.

### Integration Points
- 37 occurrences of `.H` / `MessageIdentity` / `.EntryId` / `IExecutionCorrelated` across 13 files define the reshape blast radius — every one must compile against the six-id, Guid-`entryId`, no-`H` shape before Phase 43 is green.
- The execution-scope **log path** (`InboundExecutionScopeConsumeFilter`, `ExecutionLogScope`) consumes `EntryId`; the string→Guid change touches scope serialization there.

</code_context>

<specifics>
## Specific Ideas

- The user owns the LOCKED spec; this phase implements its §"Identities & L2" + §"Keeper recovery" vocabulary verbatim. Claude analyzes/refactors within that boundary and does not add scope.
- The composite backup key is `skp:`-prefixed (D-09) as a deliberate, documented divergence from the design doc's bare `L2[correlationId:workFlowId:ProcessorId:executionId]` notation — chosen for consistency with every other L2 key in `L2ProjectionKeys`.

</specifics>

<deferred>
## Deferred Ideas

- **ROADMAP reconciliation (doc, not code).** D-01/D-02 move RETIRE-01 (`H`/`flag[H]`/CAS) and RETIRE-02 (content-addressing/manifest/N×M fan-out) into Phase 43, leaving Phase 48 = RETIRE-03 (reactive `Fault<T>` path + `keeper-dlq`) + final sweep. The `ROADMAP.md` Phase 43 ("just vocabulary") and Phase 48 descriptions, and the REQUIREMENTS traceability rows for RETIRE-01/02, should be reconciled to reflect the actual landing phase. Surface during planning; apply as a docs update.
- **`keeper-fault-recovery` / `keeper-dlq` queue-const removal** → Phases 47/48 (the reactive path retirement), not Phase 43.
- **Durable (non-L2) recovery backup** — milestone-deferred (REQUIREMENTS "Future Requirements"); the v4 backup deliberately lives in L2 (transient-outage model only).

</deferred>

---

*Phase: 43-message-contracts-l2-key-reshape*
*Context gathered: 2026-06-08*
