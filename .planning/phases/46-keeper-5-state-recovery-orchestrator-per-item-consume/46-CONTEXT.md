# Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume - Context

**Gathered:** 2026-06-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Build the two consumers that complete the v4.0.0 recovery path:

1. **Keeper recovery consumer** on `queue:keeper-recovery` (`KeeperQueues.Recovery`) — **gate-open-only** (awaits the Phase-45 `IL2HealthGate`), **partitioned by `corr:wf:ProcessorId:executionId`** (per-key ordering via MassTransit `UsePartitioner`), applying the five Keeper states:
   - `UPDATE` → write `validatedData` to `L2[CompositeBackup]` with the `BackupOptions` TTL (crash-backstop only).
   - `REINJECT` → read `L2[entryId]`; present → re-inject a reconstructed `EntryStepDispatch` to `queue:{ProcessorId}`; absent/empty (data gone) → read failure → retry loop → terminal give-up.
   - `INJECT` → read `L2[CompositeBackup]` → generate new `entryId` → write `L2[entryId]` (no TTL) → inject a reconstructed `StepCompleted` (carrying `entryId` + `executionId`) to the orchestrator result queue → **delete the composite copy**.
   - `DELETE` → delete `L2[entryId]` (GC only).
   - `CLEANUP` → delete the redundant `L2[CompositeBackup]` on the happy path.
2. **Orchestrator per-item result consumer** — the `TypedResultConsumer<TMessage>` family advancing workflow steps off per-item `StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing` (no manifest fan-out); a Keeper-`INJECT`'d `StepCompleted` is processed identically to a direct processor completion.

**Requirements (locked):** KEEP-04, KEEP-05, KEEP-06, KEEP-07, KEEP-08, KEEP-09, ORCH-01.

**NOT in this phase:**
- `_DLQ1` consolidation + at-least-once-no-dedup statement — **Phase 47** (RESIL-02/03). Phase 46 routes terminal give-ups to the **existing** bus error queue (D-04 below).
- Removal of the dark reactive `Fault<T>` recovery path + per-workflow `PauseWorkflow`/`ResumeWorkflow` + `keeper-dlq` — **Phase 48** (RETIRE-03). Phase 46 is additive; the new `keeper-recovery` consumer coexists with the dormant reactive `keeper-fault-recovery` consumer.
- The BIT gate / global pause-resume mechanism itself — **Phase 45** (built; Phase 46 only consumes the gate).

</domain>

<decisions>
## Implementation Decisions

### REINJECT payload reconstruction (KEEP-05) — scout-surfaced contract gap
- **D-01:** **Add `Payload` to `KeeperReinject`.** Scout finding: `KeeperReinject` carries `{corr, wf, step, proc, exec, entryId}` but **not** `Payload` — yet a faithfully reconstructed `EntryStepDispatch` requires `Payload` (the step config the author deserializes in In-Process). Re-injecting with an empty payload would silently diverge the recovered run from a direct dispatch (the author loses its config). Resolution: the processor (Phase-44 `BuildReinject` send site) stamps the inbound dispatch's `Payload` onto `KeeperReinject`, and the Keeper reconstructs a full `EntryStepDispatch(WorkflowId, StepId, ProcessorId, Payload) { CorrelationId, ExecutionId, EntryId }`.
  - **Cross-phase ripple (must be planned explicitly):** this is an **additive field on a Phase-43 shipped contract** (`src/Messaging.Contracts/KeeperReinject.cs`) plus an edit to the **Phase-44 processor** send site (`src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` `BuildReinject`). Both must be updated in this phase. Golden/contract test for `KeeperReinject` (Phase 43) must be updated to pin the new field. No other Keeper record changes.
  - **Design-doc reconciliation (user-owned):** the locked design doc §REINJECT says "re-inject a reconstructed `EntryStepDispatch`" without specifying the payload source — D-01 fills that gap. A follow-up amendment to `docs/design/2026-06-08-...md` recording the `Payload`-on-`KeeperReinject` carriage is warranted (doc update, user-owned).

### Recovery consumer structure (KEEP-04..09)
- **D-02:** **Five sealed per-state consumer classes + a shared gate/retry base**, each with its own `ConsumerDefinition` bound to `queue:keeper-recovery`. The base owns the cross-cutting concerns (await `IL2HealthGate.WaitForOpenAsync` once at entry per D-03; the `RetryLoop` wrapper for every L2 op + send); each subclass carries its distinct state body. Mirrors the orchestrator's `TypedResultConsumer<T>` symmetry, keeps each state independently testable, and the `UsePartitioner` ordering applies endpoint-wide regardless of class split. (Rejected: one class implementing all five `IConsumer<T>` — five unrelated bodies in one file, harder to test in isolation.)

### Gate-wait bound (KEEP-09 / Phase-45 D-11 deferral)
- **D-03:** **Await the gate ONCE at `Consume` entry** (before any L2 op), via a linked `CancellationTokenSource` bounded at **~5 minutes** — well under RabbitMQ's 30-min `consumer_timeout`. Matches the design's "gate-closed → waits bounded under the broker consumer timeout." (Rejected: re-awaiting before each individual L2 op — multiplies wait windows within one delivery and complicates the bound accounting.) The exact seconds value (default ~300s) is Claude's within the under-30-min constraint; bind from config alongside the other Keeper options if convenient.
  - **Resolution (2026-06-08, plan-phase OQ-1):** D-03's original "un-acked → redelivered (broker re-orders into key group)" wording presumed RabbitMQ `basic.nack(requeue:true)`, but MassTransit v8 moves a thrown delivery (including `OperationCanceledException` from the linked CTS) to the consolidated error queue `skp-dlq-1` — it does **not** broker-requeue. **Locked realization:** mirror the existing `L2ProbeRecovery` "await-inside-`Consume`, hold the delivery un-acked while looping" precedent — await `WaitForOpenAsync` with the ~5-min linked-CTS bound; if the gate is still closed at the bound, throw a **transient** marker that the endpoint's `UseMessageRetry` re-attempts (after retries, dead-letters to `skp-dlq-1`, acceptable under the healthy-soon assumption). This preserves D-03's intent (bounded wait, per-key ordering within each attempt) without depending on broker-level redelivery. Partition ordering is preserved across retries because the partitioner re-slots each redelivery into its key group.

### Terminal give-up routing (KEEP-05 data-gone; any Keeper L2/send exhaustion)
- **D-04:** **Defer `_DLQ1` to Phase 47 — throw to the existing bus error queue.** Mirror Phase-44 D-10: on retry-loop exhaustion of any Keeper L2 op or send, let the exception **propagate so MassTransit dead-letters to the existing `_error` / `keeper-dlq`** path; the `_DLQ1` rename/consolidation is **Phase 47** scope and is NOT built here. For the `REINJECT`-data-gone case (a **deliberate** terminal, not a natural Redis exception — the read simply found the key absent/empty), **throw a marker exception** so it forces the same dead-letter route rather than silently acking. Keeps every intermediate buildable, consistent with the locked build-before-teardown order.

### Retry-loop reuse (RESIL-01 / A3 — shared `Retry:Limit`)
- **D-05:** **Relocate `RetryLoop` from `BaseProcessor.Core` to `BaseConsole.Core`.** Keeper's project firewall references only `BaseConsole.Core` + `Messaging.Contracts` (NOT `BaseProcessor.Core`), so it cannot reuse the Phase-44 `RetryLoop` in place. Move `src/BaseProcessor.Core/Resilience/RetryLoop.cs` down into `BaseConsole.Core` (which both `BaseProcessor.Core` and `Keeper` reference) so there is ONE A3 N-immediate-attempt `Retry:Limit` implementation; update `BaseProcessor.Core`'s `using`. Keeper binds `Retry:Limit` from its existing `RetryOptions` section (already wired in `Keeper/Program.cs`). (Rejected: a duplicate Keeper-local helper — two implementations of the same A3 semantics drift.)

### Partition count (KEEP-09)
- **D-06:** **`UsePartitioner(N)` count is a config knob with a default of 8**, bound from appsettings (mirrors the existing `Probe`/`Backup`/`Retry` options precedent in `Keeper/Program.cs`). Tunable per deployment without a rebuild. The partition key is the `IKeeperRecoverable` 4-tuple (`corr:wf:ProcessorId:executionId`) per Phase-43 D-12.

### Orchestrator per-item consumer (ORCH-01) — shape pre-locked in Phase 43 D-06e
- **D-07:** Build the `TypedResultConsumer<TMessage> where TMessage : class, IStepResult` base + **four sealed one-line subclasses** (`StepCompletedConsumer`/`StepFailedConsumer`/`StepCancelledConsumer`/`StepProcessingConsumer`), all co-located on `OrchestratorQueues.Result` via four thin `ConsumerDefinition`s, exactly per 43-CONTEXT D-06e. One `protected abstract StepOutcome Outcome { get; }` knob per type — **no status if/switch anywhere**. Body = retained `ResultConsumed` metric → L1-miss business-ack → `StepAdvancement.SelectNext(Outcome, …)` → `DispatchAsync` per matched successor (preserving `correlationId`/`workflowId`/`executionId`, resolving new `ProcessorId`/`payload`/`stepId` from L1, seeding `entryId = m.EntryId`). The current straight-through `ResultConsumer.cs` stub (`IConsumer<StepCompleted>`) is **replaced** by `StepCompletedConsumer` + the base. Park-on-send-exhaustion routes to the existing `_error` queue (the `_DLQ1` consolidation is Phase 47, consistent with D-04).

### Claude's Discretion
- Exact namespace/placement of the recovery-consumer base + the marker give-up exception (D-02/D-04).
- The precise gate-wait timeout seconds under the 30-min bound, and whether it rides an existing or new options key (D-03).
- `RetryLoop` target namespace within `BaseConsole.Core` and how exhaustion is surfaced to the Keeper bodies (D-05) — keep parity with the Phase-44 surfacing contract.
- `KeeperReinject` `Payload` member name/position (follow the existing record convention; `init`-only) (D-01).
- The reconstructed-`EntryStepDispatch` send mechanism for REINJECT (reuse the `ISendEndpointProvider` → `queue:{ProcessorId:D}` idiom from `StepDispatcher`).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked design (source of truth)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — **LOCKED 2026-06-08.** §"Keeper → Recovery consumer" (lines 87–97) is the authoritative spec for the five states, their L2 ops, the `corr:wf:ProcessorId:executionId` partition, and the gate-open-only/bounded-wait rule. §"Result contract (Amendment A15)" (lines 23–38) governs the four typed result records and the `INJECT`-reconstructs-`StepCompleted` rule. §"Identities & L2" (lines 9–19) for the data key (no TTL) + composite backup key (2d crash-backstop TTL) + `Guid.Empty` sentinel. §"Locked decisions" rows A2 (absent/empty read → terminal), B/CLEANUP (Keeper owns the composite copy; per-key ordering guarantees `UPDATE` precedes `INJECT`/`CLEANUP`), A8/A12 (per-item results, no manifest), A4 (single `_DLQ1` — Phase 47). **Note D-01:** the design's "reconstructed `EntryStepDispatch`" for REINJECT is filled by carrying `Payload` on `KeeperReinject` (doc amendment warranted, user-owned).

### Requirements & roadmap
- `.planning/REQUIREMENTS.md` — **KEEP-04** (line 43), **KEEP-05** (44), **KEEP-06** (45), **KEEP-07** (46), **KEEP-08** (47), **KEEP-09** (48), **ORCH-01** (36). Every id must be accounted for in the plan. RESIL-02/03 (`_DLQ1` + at-least-once) are Phase 47 — referenced for boundary only.
- `.planning/ROADMAP.md` §"Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume" (lines 481–492) — the **6 success criteria** are the verification target.

### Existing code this phase builds on / rewrites (read before planning)
- `src/Keeper/Health/IL2HealthGate.cs` + `L2HealthGate.cs` — the gate primitive (Phase 45): `WaitForOpenAsync(ct)`, starts CLOSED (D-12). Phase 46 is its first reader (D-03).
- `src/Keeper/Recovery/L2ProbeRecovery.cs` — `ProbeOnceAsync` + the `RedisException`-only discipline + the `IConnectionMultiplexer` singleton pattern the recovery consumer's L2 ops reuse.
- `src/Keeper/Program.cs` — the composition root: `RetryOptions`/`ProbeOptions`/`BackupOptions` bindings, `AddBaseConsoleMessaging` consumer registration. Phase 46 registers the five recovery consumers + the partitioner here.
- `src/Keeper/Keeper.csproj` — the **firewall** (only `BaseConsole.Core` + `Messaging.Contracts`) that forces the `RetryLoop` relocation (D-05).
- `src/Messaging.Contracts/KeeperReinject.cs` — **edit** (add `Payload`, D-01). `KeeperUpdate`/`KeeperInject`/`KeeperDelete`/`KeeperCleanup.cs` — read (id-sets consumed verbatim). `IKeeperRecoverable.cs` — the partition 4-tuple (D-06). `KeeperQueues.cs` — `Recovery = "keeper-recovery"`.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData(Guid)` (data key, no TTL), `CompositeBackup(corr,wf,proc,exec)` (backup key). `src/Keeper/BackupOptions.cs` — the TTL applied at the `UPDATE` write (KEEP-04).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — `BuildReinject` send site (**edit** per D-01: stamp `Payload`). `src/BaseProcessor.Core/Resilience/RetryLoop.cs` — **relocate** to `BaseConsole.Core` (D-05).
- `src/Orchestrator/Consumers/ResultConsumer.cs` — the straight-through `StepCompleted` stub **replaced** by `StepCompletedConsumer` + the `TypedResultConsumer<T>` base (D-07). `src/Orchestrator/Dispatch/StepDispatcher.cs` — `DispatchAsync` reused by all four typed consumers; the `queue:{ProcessorId:D}` Send idiom reused for REINJECT. `src/Orchestrator/Dispatch/StepAdvancement.cs` (`SelectNext`) — the pure int-match advancement vocab.
- `src/Orchestrator/Program.cs` — register the four typed result consumers + definitions on `OrchestratorQueues.Result`.

### Prior-phase contracts (consumed)
- `.planning/phases/43-message-contracts-l2-key-reshape/43-CONTEXT.md` — **D-06e** (the `TypedResultConsumer<T>` shape, the authoritative blueprint for D-07 here), D-11/D-12 (Keeper records + partition tuple), D-08/D-09/D-10 (L2 keys + `BackupOptions`).
- `.planning/phases/44-processor-pre-in-post-process-pipeline/44-CONTEXT.md` — D-08/D-10 (`RetryLoop` + give-up-to-`_error` posture mirrored here), the processor send sites that emit the five Keeper messages this consumer receives.
- `.planning/phases/45-keeper-bit-health-gate-global-pause-resume/45-CONTEXT.md` — D-09..D-12 (the gate primitive + the explicit Phase-46 wait-bound deferral resolved by D-03 here).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`L2HealthGate.WaitForOpenAsync`** — the gate the recovery consumer base awaits once at entry (D-03). No new gate logic.
- **`L2ProbeRecovery` / `IConnectionMultiplexer` singleton + `RedisException`-only catch** — the L2-access pattern the five state bodies follow for read/write/delete.
- **`RetryLoop` (Phase 44)** — relocate to `BaseConsole.Core` and reuse for every Keeper L2 op + send (D-05); already surfaces exhaustion in the shape Phase 44 established.
- **`StepDispatcher.DispatchAsync` + `ISendEndpointProvider → queue:{ProcessorId:D}`** — the Send idiom REINJECT reuses to re-inject the reconstructed `EntryStepDispatch`, and that the four typed orchestrator consumers reuse for continuations.
- **`StepAdvancement.SelectNext` + L1 store** — the orchestrator advancement core, reused unchanged across the four typed consumers (L1-only, no Redis).
- **`L2ProjectionKeys.CompositeBackup` / `ExecutionData` + `BackupOptions`** — the keys + TTL for UPDATE/INJECT/CLEANUP/DELETE.

### Established Patterns
- **Per-key ordering via `UsePartitioner`** keyed on `IKeeperRecoverable` (corr:wf:proc:exec) — first use in the codebase; partition count is the D-06 config knob.
- **Gate-open-only + un-acked-on-timeout → redelivery** — the design's bounded-wait rule (D-03); the partitioner re-slots a redelivered message back into its key group.
- **Business-ack vs infra-throw** — orchestrator L1-miss = business-ack (existing `ResultConsumer` doc); Keeper L2/send exhaustion = infra-throw → existing error queue (D-04).
- **Options-bound knobs** (`Probe`/`Backup`/`Retry` in `Keeper/Program.cs`) — the partition-count + gate-wait knobs follow this precedent.
- **`TypedResultConsumer<T>` no-if/else routing by message type** — the orchestrator pattern locked in 43-CONTEXT D-06e.

### Integration Points (files this phase adds/edits)
- **Add (Keeper):** the recovery-consumer base + five sealed per-state consumers + five `ConsumerDefinition`s; register on `queue:keeper-recovery` with `UsePartitioner(N)` in `Keeper/Program.cs`; the give-up marker exception.
- **Add (Orchestrator):** `TypedResultConsumer<T>` base + four sealed subclasses + four `ConsumerDefinition`s; register in `Orchestrator/Program.cs`.
- **Edit (Messaging.Contracts):** `KeeperReinject` — add `Payload` (D-01).
- **Edit (BaseProcessor.Core):** `ProcessorPipeline.BuildReinject` — stamp `Payload` (D-01).
- **Move:** `RetryLoop.cs` → `BaseConsole.Core` (D-05); update `BaseProcessor.Core` using.
- **Replace:** `Orchestrator/Consumers/ResultConsumer.cs` → `StepCompletedConsumer` + base (D-07).
- **Update tests:** `KeeperReinject` golden/contract test (new `Payload` field).

</code_context>

<specifics>
## Specific Ideas

- **The REINJECT payload gap (D-01) is the load-bearing decision of this phase** — surfaced by scouting the shipped `KeeperReinject` record against the locked design's "reconstructed `EntryStepDispatch`." Without `Payload`, a recovered run would silently lose its author config. The user owns the spec; this resolution carries `Payload` on the wire and warrants a design-doc amendment.
- **Net-zero composite-backup invariant (design A2/B/CLEANUP):** after any non-crash path the composite copy is gone — deleted by `CLEANUP` (happy path) or `INJECT` (recovery), never left to its 2-day TTL. Per-key ordering guarantees `UPDATE` precedes that exec's `CLEANUP`/`INJECT`, so there is no data-gone→DLQ race. The Phase-49 close gate will scan composite keys for net-zero.
- **A Keeper-`INJECT`'d `StepCompleted` must be byte-indistinguishable from a direct processor completion** (ORCH-01) — same `entryId` + `executionId` carriage, lands on the same `OrchestratorQueues.Result`, processed by the same `StepCompletedConsumer`.

</specifics>

<deferred>
## Deferred Ideas

- **`_DLQ1` consolidation + at-least-once-no-dedup statement** — Phase 47 (RESIL-02/03). Phase 46 throws give-ups to the existing `_error`/`keeper-dlq` (D-04).
- **Removal of the dark reactive `Fault<T>` recovery path + `keeper-fault-recovery` + per-workflow `PauseWorkflow`/`ResumeWorkflow` + `keeper-dlq`** — Phase 48 (RETIRE-03). Phase 46 coexists additively.
- **Design-doc amendment recording `Payload`-on-`KeeperReinject`** (D-01) — user-owned doc update; surfaced here so it is not lost.

</deferred>

---

*Phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume*
*Context gathered: 2026-06-08*
