# Phase 51: Processor Forward + Recovery Pipeline - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-11
**Phase:** 51-processor-forward-recovery-pipeline
**Areas discussed:** Recovery re-send identity, Slot-array random TTL, Pipeline restructure shape, messageId plumbing/null-guard

---

## Recovery re-send identity (executionId provenance)

| Option | Description | Selected |
|--------|-------------|----------|
| Fresh-mint per re-send | NewId.NextGuid() on each recovery 'completed' re-send; safe under A16 no-dedup | ✓ (recovery pass) |
| Reuse inbound d.ExecutionId | Carry the inbound dispatch ExecutionId (A1 convention) | ✓ (REINJECT/DELETE only) |

**User's choice:** The user reframed the narrow recovery-only question into a uniform cross-path rule:
1. `REINJECT` uses the executionId from the origin message.
2. For each forward-POST item with `error_message="infra_entryId"`/failed → send keeper `INJECT`, create executionId.
3. For each item `completed` → send orchestrator `completed`, create executionId.

**Notes:** Reconciled as — **REINJECT/DELETE → origin `d.ExecutionId`**; **INJECT/completed → a created per-item exec** (author-minted `item.ExecutionId` in the forward pass; freshly minted `NewId.NextGuid()` in the recovery pass, where no author/item is in hand because the slot HASH stores only `entryId` per Phase-50 D-05). Storing exec in the slot was precluded — it would break the Phase-50-locked HASH-value shape. Captured as D-01/D-02/D-03.

---

## Slot-array random TTL

| Option | Description | Selected |
|--------|-------------|----------|
| New options record, 300–600s | New SlotArrayOptions in BaseProcessor.Core.Configuration bound from 'Processor'; random [300,600]s | ✓ |
| Fold into ProcessorLivenessOptions | Add MessageIndexTtlMin/Max to the existing options record | |
| Hardcoded constants | Bake min/max as consts, no config knob | |

**User's choice:** New options record, 300–600s.
**Notes:** 300 floor pinned to `ExecutionDataTtl`'s default so the `L2[messageId]` marker outlives the data keys it references; 600 ceiling gives jitter to avoid synchronized-expiry herd. NOT the deleted Keeper BackupOptions location (Phase-50 D-07 explicit). Captured as D-04/D-05/D-06.

---

## Pipeline restructure shape

| Option | Description | Selected |
|--------|-------------|----------|
| Split + explicit inline tails | RunForwardAsync/RunRecoveryAsync off a top-level existence-check; inline source-delete tails; drop try/finally | ✓ |
| One method, shared finally + flag | Single RunAsync if/else with finally end-delete gated by reinjectFired flag | |
| Hybrid branch + minimal finally | Branch with inline tails, finally only for skip-guards | |

**User's choice:** Split + explicit inline tails.
**Notes:** Chosen specifically to retire the Phase-44 WR-01 landmine (the `finally` end-delete firing during a send-exhaustion unwind could delete the input before bus replay). Honors the REINJECT⊻source-delete mutual exclusion explicitly; each pass independently testable. Captured as D-07/D-08.

---

## messageId plumbing / null-guard

| Option | Description | Selected |
|--------|-------------|----------|
| Fail-fast throw if null | RunAsync(..., Guid messageId, ct); consumer passes ctx.MessageId!.Value; throw InvalidOperationException if null | ✓ |
| Null → REINJECT/redeliver | Treat absent MessageId as infra; throw so broker redelivers | |
| Synthesize from ids | Derive a deterministic messageId from correlationId/stepId when null | |

**User's choice:** Fail-fast throw if null.
**Notes:** MassTransit always sets MessageId on Send/Publish, so null is a never-in-practice contract violation — surface it loudly rather than degrade. Pipeline stays a plain testable object (messageId is a method arg, not read from ConsumeContext). Captured as D-09/D-10.

---

## Claude's Discretion

- Slot-index counter semantics (slot = ordinal of completed items only; dropped/failed items consume no slot).
- Recovery temp-list internal representation.
- Infra-marker internal representation (enum vs string literal).
- Random source (`Random.Shared`).
- Exact hermetic-fact decomposition (SC-5).

## Deferred Ideas

- 3-state Keeper recovery consumer → Phase 52.
- Full Model-B teardown + remnant sweep (RETIRE-03) → Phase 53.
- Live proof + close gate (TEST-01/02) → Phase 54.
- Reconciling A18's `UseMessageRetry = none` / `_error`-disabled rule vs current Phase-44 outer-latch wiring → flagged for planner.
