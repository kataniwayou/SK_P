# Phase 51: Processor Forward + Recovery Pipeline - Context

**Gathered:** 2026-06-11
**Status:** Ready for planning

<domain>
## Phase Boundary

Rewrite `ProcessorPipeline` (in `BaseProcessor.Core`) to implement the A18 **processor-owned slot-array** recovery model, replacing the Phase-50-stubbed Model-B Post mechanics. The pipeline branches on `exist L2[messageId]`:

1. **FORWARD pass (`NOT exist L2[messageId]`)** — Pre → In → Post with **allocation-before-data** slot writes (`L2[messageId][slot]=entryId` first, then `L2[entryId]=data`), the **split infra taxonomy** (`infra_messageId` → drop / `infra_entryId` → keeper `INJECT`), per-item dispatch, and a happy-path **source-delete tail** (exhaustion → keeper `DELETE`).
2. **RECOVERY pass (`exist L2[messageId]`)** — read the slot array → build a temp list per slot (`exists`→completed / `not-exist`→failed / L2-fail→failed+`infra_entryId`), **send-before-retire** (`completed` → re-send orchestrator then retire slot to `guid.empty`), and the **`REINJECT`⊻source-delete** mutual-exclusion rule (any `infra_entryId` → `REINJECT` and do NOT delete source).
3. Author the slot-array `TTL(random)` options (deferred from Phase 50 D-07).
4. Keep the solution buildable 0-warning (Release + Debug); hermetic facts prove both passes.

**A18 is the LOCKED source of truth** — the WHAT (the two passes, the id-sets, the invariants) is fixed. These decisions cover only the HOW A18 left open. The 3-state **Keeper recovery consumer** is Phase 52; full **Model-B teardown + remnant-verification** is Phase 53; **live proof + close gate** is Phase 54.

</domain>

<decisions>
## Implementation Decisions

### executionId provenance (across all dispatch paths)
The slot HASH stores only `entryId` (Phase-50 D-05) — `executionId` is NOT persisted — so the recovery pass cannot recover an origin per-item exec. The uniform rule:

- **D-01:** **`REINJECT` and `DELETE` carry the ORIGIN executionId** — the inbound dispatch's `d.ExecutionId` (whole-message / source-scoped). Matches the existing `BuildReinject`/`BuildDelete` (A1 convention) — no change.
- **D-02:** **`INJECT` (item `infra_entryId`/failed) and `completed` (→ orchestrator) carry a CREATED per-item executionId.**
- **D-03:** **"Created" reconciles per pass:**
  - **Forward pass** — the author already mints the per-item exec in the In phase (`item.ExecutionId` from `ProcessAsync`'s returned list); `completed`/`INJECT` carry **that** id. Matches the current `BuildCompleted`/`BuildInject` — no change.
  - **Recovery pass** — no author/item is in hand (slot holds only `entryId`), so the re-sent `completed` mints a **fresh** `executionId` (`NewId.NextGuid()`) at re-send time. **This is the new behavior.** Safe under A16 (orchestrator does not dedup; advances per-item off the step graph + entryId; exec is observability identity only).

### Slot-array `TTL(random)` options (SLOT-01 / Phase-50 D-07 deferral)
- **D-04:** **New options record** `SlotArrayOptions` in `BaseProcessor.Core.Configuration`, bound from the `"Processor"` config section (mirrors `ProcessorLivenessOptions`' `ConfigurationKeyName` idiom). **NOT** the deleted Keeper `BackupOptions` location (D-07 explicit). Clean separation of concerns from the liveness/execution-data knobs.
- **D-05:** **Random TTL range `[300, 600]s`** (min/max as two int-seconds config keys). Floor = `ExecutionDataTtl` default (300s) so the `L2[messageId]` marker outlives the data keys it references; ceiling = 2× for jitter, spreading expiry to avoid a synchronized-expiry herd.
- **D-06:** One `TTL(random)` is applied to the **whole `L2[messageId]` HASH** on each slot write (allocation write + the `guid.empty` retire write both refresh it), per Phase-50 D-05 ("one `EXPIRE(random)` on the whole key").

### Pipeline restructure (FWD-01..03 / RECOV-01..03 / the REINJECT⊻source-delete invariant)
- **D-07:** **Split into `RunForwardAsync` / `RunRecoveryAsync`**, dispatched by a top-level `exist L2[messageId]` existence-check in `RunAsync` (existence-check exhaustion → `REINJECT`; end — FWD-01).
- **D-08:** **Explicit inline source-delete tails — drop the try/finally end-delete.** Each pass owns its own source-delete at the tail, gated by the `REINJECT`⊻source-delete mutual exclusion (a `REINJECT` ends the round trip WITHOUT deleting the source; the source delete only runs on the no-REINJECT path). This directly resolves the Phase-44 **WR-01** landmine (the `finally` end-delete firing during a send-exhaustion unwind could delete the input before bus replay) — the input is now only deleted on an explicit happy-path tail, never in a `finally`.

### messageId plumbing (the branch key)
- **D-09:** Change the seam to `RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)`; `EntryStepDispatchConsumer.Consume` passes `ctx.MessageId!.Value`. The hermetic facts construct the pipeline directly and pass a `messageId` Guid (no MassTransit harness — preserves the Phase-44 plain-object testability).
- **D-10:** **Fail-fast throw on a null `MessageId`** (`InvalidOperationException`). MassTransit always sets `MessageId` on `Send`/`Publish`, so null is a never-in-practice contract violation — surface it loudly rather than synthesize a key or degrade to a no-recovery path.

### Claude's Discretion
- **Slot-index counter semantics** — slot = ordinal of **completed** items only; `infra_messageId`-dropped items and business-failed (output-invalid / author `Failed`) items consume **no** slot (the forward POST allocates a slot only inside the per-completed-item write block, per the A18 pseudocode).
- **Recovery temp-list internal representation** (a local list of `(slot, entryId, outcome, errorMessage)` or equivalent).
- **Infra-marker representation** — how `infra_messageId` / `infra_entryId` are carried internally (enum vs string literal on the temp item); the literal strings `"infra_messageId"`/`"infra_entryId"` are locked by INFRA-01/02 only as the taxonomy split, not necessarily a wire field.
- **Random source** — `Random.Shared` (thread-safe) for the `[min,max]` TTL pick.
- Exact hermetic-fact decomposition proving forward + recovery flows (SC-5).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Source of Truth (LOCKED)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` → **"Recovery Re-architecture (A18)"** (lines 130-227, LOCKED 2026-06-11) — the v5.0.0 source of truth. Specifically: §New L2 vocabulary (`L2[messageId]` slot array, `infra_messageId`/`infra_entryId` split), §Processor FORWARD pass (lines 146-177, allocation-before-data + per-item dispatch + source-delete tail), §Processor RECOVERY pass (lines 179-203, temp-list + send-before-retire + REINJECT⊻source-delete), §Invariants (lines 223-227, including accepted silent losses). Everything above A18 in that doc (Identities & L2 data key, Result contract A15, BIT gate + pause/resume A14, at-least-once A16, single `skp-dlq-1` A4) still holds unchanged.

### Requirements & Roadmap
- `.planning/REQUIREMENTS.md` — **SLOT-01/02/03, INFRA-01/02, FWD-01/02/03, RECOV-01/02/03** are the Phase-51 scope (the KEEP family is Phase 52; RETIRE-03 is Phase 53).
- `.planning/ROADMAP.md` → **Phase 51** section — the 5 success criteria.

### Predecessor context (Phase 50 — contracts already landed)
- `.planning/phases/50-contracts-slot-array-l2-key-reshape/50-CONTEXT.md` — the locked contract decisions this phase builds against: `MessageIndex` key shape (D-04), Redis-HASH int-slot→entryId structure (D-05), `KeeperInject(EntryId,Data,DeleteEntryId)` / `KeeperReinject(EntryId,Payload)` / `KeeperDelete(EntryId)` id-sets (D-08/D-09), and the D-07 deferral of the slot-array `TTL(random)` to THIS phase.

### Code to rewrite / touch (this phase)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — the rewrite target (split forward/recovery; the Phase-50 stub comments at the old Post block mark where the slot-array allocation lands).
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — pass `ctx.MessageId!.Value` into `RunAsync` (D-09/D-10).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — the `MessageIndex(Guid messageId)` builder (already shipped Phase 50) is the slot-array key; recovery uses `HGETALL`, retire uses `HSET slot guid.empty`.
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` — the precedent (idiom + `ConfigurationKeyName`) for the new `SlotArrayOptions` (D-04); `ExecutionDataTtlSeconds` default (300) is the TTL floor reference (D-05).
- `src/BaseProcessor.Core/Processing/ProcessItem.cs`, `ProcessOutcome.cs` — the per-item shape the forward POST iterates (author-minted `ExecutionId`, `Result`, `Data`).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`RetryLoop` / `RetryOutcome`** (`BaseConsole.Core.Resilience`, relocated D-05) — wraps every L2 op + every send; `Succeeded` gates the per-site infra routing. The forward/recovery passes reuse it verbatim for the existence-check, slot writes, data writes, retire writes, and the source delete.
- **`L2ProjectionKeys.MessageIndex` + `ExecutionData`** — the two L2 keys the passes operate on; `ExecutionData(entryId)` is the data key, `MessageIndex(messageId)` is the slot-array HASH.
- **`ProcessorJsonSchemaValidator.TryValidate`** — input (Pre) + output (Post) schema gates; unchanged.
- **`KeyAbsentException`** (`BaseProcessor.Core.Resilience`) — the Pre-read absent/empty-unified-with-fault sentinel; reused for the recovery `exist L2[entryId]` checks (absent → not-exist temp item, fault-exhaust → `infra_entryId`).
- **Keeper-state builders** (`BuildReinject`/`BuildInject`/`BuildDelete` in `ProcessorPipeline`) — already carry the A1 id-sets; the forward/recovery dispatch reuse them (recovery `REINJECT` carries origin exec per D-01).
- **`ProcessorLivenessOptions`** — the options-record + `ConfigurationKeyName` idiom to mirror for `SlotArrayOptions` (D-04).

### Established Patterns
- **Plain-object testable pipeline** (Phase-44 RESEARCH Pattern 1) — `ProcessorPipeline` is constructed directly by hermetic facts; the consumer is a thin metric+delegate shell. Preserve this: pass `messageId` as a method arg (D-09), not via `ConsumeContext`.
- **Per-op in-code retry + bus retry as outer latch** (RESIL-01 / D-09/D-10) — L2-op exhaustion routes per-site (infra taxonomy); send-op exhaustion PROPAGATES (→ bus `UseMessageRetry` dead-letter latch). Note A18 global rule: `_error` routing disabled / `UseMessageRetry = none` for the v5 path — the planner must reconcile this against the current Phase-44 `UseMessageRetry` outer-latch wiring.
- **Bounded TTL for net-zero close gate** — every L2 write carries a bounded TTL so the close-gate `redis --scan` stays leak-free; the slot-array `TTL(random)` (D-04/05/06) extends this invariant to `L2[messageId]`.

### Integration Points
- `EntryStepDispatchConsumer` → `ProcessorPipeline.RunAsync` is the one seam changing signature (`+ Guid messageId`).
- The forward/recovery dispatch sends land on `queue:{OrchestratorQueues.Result}` (orchestrator per-item consumer, Phase 46) and `queue:{KeeperQueues.Recovery}` (the 3-state keeper consumer, **Phase 52** — Phase 51 sends the messages; Phase 52 consumes them).
- `Processor.Sample` is the worked example processor; its `ProcessAsync` override is unchanged (the pipeline rewrite is framework-internal).

</code_context>

<specifics>
## Specific Ideas

- The executionId rule was the user's reframing of a narrower recovery-only question into a uniform cross-path rule: **REINJECT/DELETE = origin exec; INJECT/completed = created exec** (author-minted in forward, framework-minted fresh in recovery). Capture it verbatim in D-01..D-03 — it is the load-bearing identity decision.
- `SlotArrayOptions` TTL range `[300, 600]s` — the 300 floor is intentionally pinned to `ExecutionDataTtl`'s default so the marker never expires before the data it indexes.
- The "split + explicit inline tails" structure (D-07/D-08) is chosen specifically to retire the Phase-44 WR-01 `finally`-unwind robustness edge case — call this out in the plan as a resolved landmine, not just a refactor.

</specifics>

<deferred>
## Deferred Ideas

- The 3-state **Keeper recovery consumer** (gate-open-only apply of `REINJECT`/`INJECT`/`DELETE`, configurable DLQ1-vs-sustained-outage exhaustion, gate-closed non-destructive consume) → **Phase 52** (KEEP family). Phase 51 only *sends* these keeper messages.
- Full **Model-B teardown + reflection/source remnant sweep** (RETIRE-03) → **Phase 53**.
- **Live proof + N×GREEN triple-SHA close gate** (TEST-01/02) → **Phase 54**.
- Reconciling A18's `UseMessageRetry = none` / `_error`-disabled global rule against the current Phase-44 outer-latch wiring — flagged for the planner; may be a Phase-51 touch or a Phase-53 teardown item depending on the planner's read.

None of these are scope creep — they are the locked downstream phases of the same milestone; captured so Phase 51 stays processor-pipeline-only.

</deferred>

---

*Phase: 51-processor-forward-recovery-pipeline*
*Context gathered: 2026-06-11*
