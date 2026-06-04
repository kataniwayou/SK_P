# Phase 32 — Cancelled Circuit-Breaker — CONTEXT (stub)

**Status:** PLANNED (split out of Phase 31 on 2026-06-04). Depends on Phase 31 (deterministic `H`, configurable retry).

**Design record:** the full design lives in the **deferred Failure-policy section of [`../31-idempotent-execution-exactly-once-effect/31-CONTEXT.md`](../31-idempotent-execution-exactly-once-effect/31-CONTEXT.md)**. Summary below; `/gsd-spec-phase 32` will formalize `CANCEL-*` and resolve the open decisions.

## What it delivers

On retry-budget exhaustion (`GetRetryAttempt() == configured Limit`, the limit from Phase 31's config), the processor:
1. Sends the **consumed message back as `Cancelled`** (not dead-lettered). `Cancelled` is deduped via the Phase-31 deterministic `H`.
2. **Two-level stop:**
   - **In-flight (current fire):** processor sets `cancelled[workflowId] = true` in L2; **every receiver checks it before processing and drops** → the fire drains to a halt (stops advancing, no rollback; `workflowId`-keyed so concurrent fires stop too).
   - **Future fires:** the orchestrator, on `Cancelled`, resolves `jobId` from **L1** (`store.TryGet → wf.JobId` — no L2 read) and unschedules the Quartz job (reuses Stop/Teardown machinery; idempotent).
3. Effect-first order: set marker → send `Cancelled` → ack (idempotent on re-exhaustion).

`Cancelled` is a **terminal stop intercepted before `SelectNext`** — it advances no successor regardless of entry condition.

## Enum change
**Remove `StepEntryCondition.PreviousCancelled (3)`** — meaningless now (`Cancelled` halts the whole job, never advances). Leave `3` as a numeric gap (do NOT renumber 0/1/2/4/5). `StepDtoValidator` uses `.IsInEnum()` → 3 auto-rejected. Verify no live step has `EntryCondition == 3` (dual-pipeline steps use `Always`/4). `StepOutcome.Cancelled (3)` stays (processor reports it); update the "ints mirror `Previous*`" note (Cancelled is special-cased, not matched).

## Decided / open at spec
- **Decided:** trip on **first exhaustion** (Phase 31's configurable retry count is the transient-tolerance knob); blast radius = **whole workflow**.
- **Open (spec):** the **resume** procedure (clear `cancelled[workflowId]` + re-Start — manual vs auto-cooldown); keep `_error` as the backstop for **bus-down** exhaustion (the `Cancelled` send itself can't be delivered then).
