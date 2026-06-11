# Phase 54: Terminal Index Delete + Atomic Keeper GC — Specification

**Created:** 2026-06-12
**Ambiguity score:** 0.125 (gate: ≤ 0.20)
**Requirements:** 3 locked

## Goal

The processor actively reclaims the `L2[messageId]` allocation index at end-of-message: the no-`REINJECT` happy-path tail deletes BOTH the source `L2[entryId]` and the `L2[messageId]` index in a **single atomic Redis multi-key `DEL`**, and a delete exhaustion `PERSIST`s the index (cancels its random TTL) then escalates to a keeper `DELETE{messageId, entryId}` that deletes both keys atomically — replacing A18's passive TTL-only index reclaim (design-doc amendment A19).

## Background

Grounded in the v5.0.0 code shipped through Phase 53:

- **Index is never actively deleted.** `ProcessorPipeline.cs` writes the `L2[messageId]` HASH with a random whole-HASH TTL on every slot write/retire (`:165`, `:275`) but there is **no `KeyDelete(MessageIndex(...))` anywhere** — the index lingers at all-`guid.empty` until its TTL (300/600s) expires.
- **Tails delete only the source.** The forward happy tail (`DeleteSourceTail`, `:195-201`, reached at `:309`) and the recovery all-clear tail (`:182-185`) delete only `L2[entryId]` (`ExecutionData`); on exhaustion they `SendKeeper(BuildDelete(d))` (`:367-368`) — a source-only escalation.
- **Keeper DELETE is source-only.** `KeeperDelete.cs` carries `{corr, wf, step, proc, exec, entryId}` (no `MessageId`); `DeleteConsumer.cs:20` deletes only `ExecutionData(m.EntryId)`.
- **Net-zero consequence.** A18's design (doc lines 138/192/226) intends the index to be reclaimed by TTL, so after every happy-path message `skp:msg:*` survives 5–10 min — making close-gate net-zero a TTL race rather than a production property. A19 restores the v4 CLEANUP-grade deterministic reclaim via an active terminal delete.

This phase builds the A19 behavior. The live/real-stack proof + close-gate net-zero is **Phase 55** (TEST-01/02).

## Requirements

1. **GC-01 — Atomic two-key terminal delete**: The no-`REINJECT` happy-path tail deletes the source data key and the origin index key in one atomic Redis multi-key `DEL`.
   - Current: forward tail (`ProcessorPipeline.cs:195-201`/`:309`) and recovery all-clear tail (`:182-185`) delete only `ExecutionData(d.EntryId)`; the `MessageIndex(messageId)` HASH is left to its random TTL (`:165`/`:275`).
   - Target: both tails issue a single `RetryLoop`-wrapped `db.KeyDeleteAsync(new RedisKey[]{ ExecutionData(d.EntryId), MessageIndex(messageId) })` (one `DEL key1 key2` command), actively reclaiming the index. For a source step (`d.EntryId == Guid.Empty`) the `ExecutionData(Guid.Empty)` operand is a harmless absent no-op, so the DEL is effectively index-only.
   - Acceptance: a hermetic fact asserts the tail issues ONE multi-key `KeyDeleteAsync` (RedisKey array of exactly `ExecutionData` + `MessageIndex`), not two single-key calls, on a completed forward message AND on a recovery all-clear message; a source-step fact asserts the index is deleted and the Guid.Empty data operand does not throw.

2. **GC-02 — REINJECT mutual exclusion preserved**: The terminal two-key delete runs ONLY on the no-`infra_entryId`/no-`REINJECT` path; any REINJECT leaves both keys intact.
   - Current: the recovery tail already gates the source-delete behind `!anyInfra` (`:174-185`); the forward pass reaches its happy tail (`:309`) only on the all-clear path (infra handled inline as INJECT/drop/REINJECT).
   - Target: the new index delete inherits the same gate — on any REINJECT (recovery `anyInfra` → `:177`; forward/recovery existence-check or read exhaustion → `BuildReinject` at `:98`/`:129`/`:220`) NEITHER `ExecutionData` nor `MessageIndex` is deleted, so the index survives for the replay's `EXIST L2[messageId]` recovery pass.
   - Acceptance: a hermetic fact proves an `anyInfra` recovery path sends `REINJECT` and issues NO `KeyDelete` of source or index, and that the `MessageIndex` key still exists afterward.

3. **GC-03 — Persist-on-escalate + both-key keeper DELETE**: A terminal-delete exhaustion cancels the index TTL and hands a both-key delete to the keeper.
   - Current: `KeeperDelete` has no `MessageId` (`KeeperDelete.cs:11`); `BuildDelete(d)` (`:367-368`) stamps only the source ids; `DeleteConsumer.HandleAsync` deletes only `ExecutionData(m.EntryId)` (`DeleteConsumer.cs:20`).
   - Target: `KeeperDelete` gains a `MessageId` field; `BuildDelete` populates it from the inbound `messageId`. On a tail delete exhaustion the processor calls `RetryLoop`-wrapped `db.KeyPersistAsync(MessageIndex(messageId))` (cancel the random TTL) THEN `SendKeeper(BuildDelete(d, messageId))`. `DeleteConsumer.HandleAsync` deletes both keys in one `Db.KeyDeleteAsync(new RedisKey[]{ ExecutionData(m.EntryId), MessageIndex(m.MessageId) })` (drop-on-absent on either operand).
   - Acceptance: hermetic facts prove (a) tail delete exhaustion calls `KeyPersistAsync(MessageIndex)` then sends a `KeeperDelete` carrying the messageId; (b) `DeleteConsumer` issues ONE multi-key `KeyDeleteAsync` of `[ExecutionData(entryId), MessageIndex(messageId)]`; (c) absent operands no-op without throwing (drop-on-absent).

## Boundaries

**In scope:**
- Unify the forward + recovery happy-path tails into one atomic two-key `DEL` (`ExecutionData` + `MessageIndex`) via the existing `RetryLoop`.
- Persist-on-escalate: `KeyPersist(MessageIndex(messageId))` before the keeper handoff on terminal-delete exhaustion.
- `KeeperDelete` contract gains `MessageId`; `BuildDelete` stamps it from the inbound message id.
- `DeleteConsumer` deletes both keys atomically (single multi-key `DEL`), drop-on-absent.
- Hermetic facts for all of the above, including the source-step (`Guid.Empty`) index-only edge.
- Solution builds 0-warning Release + Debug; full hermetic suite green.

**Out of scope:**
- Real-stack / live E2E proof + close-gate triple-SHA net-zero — that is **Phase 55** (TEST-01/02); build-before-proof split.
- New metrics / counters for the GC path — deferred (no instrumentation this phase; matches the existing tail).
- Per-slot random-TTL writes (`ProcessorPipeline.cs:165`/`:275`) — **unchanged**; the TTL is retained as the crash-before-terminal-delete backstop (A19).
- `REINJECT` / `INJECT` consumer bodies, forward-Post allocation, recovery slot-classification — **unchanged**.
- Eliminating the source-step crash-before-ack duplicate window — **accepted** under A16 (at-least-once / no-dedup); must NOT be "fixed" with a dedup key.
- Redis-cluster multi-slot atomic delete — N/A (single-instance `sk-redis`).

## Constraints

- **Atomicity** = a single Redis `DEL key1 key2` command (single-threaded execution). Valid on the single-instance `sk-redis` only; a Redis *cluster* would require both keys in one hash slot — explicitly out of scope.
- All L2 ops + the `KeyPersist` are wrapped in the existing `RetryLoop` using `Retry:Limit`; a terminal-delete exhaustion routes per A19 (persist index → `KeeperDelete{messageId, entryId}`).
- No reintroduction of `UseMessageRetry` / `_error` routing — the Phase-53 `UseMessageRetry = none` end-state is preserved (send exhaustion still throws → broker redelivery).
- **Worst case = a duplicate message** on a source-step crash between the atomic `DEL` and the broker ack — accepted under A16; non-source steps self-heal (re-forward finds source gone → REINJECT → keeper drop).
- Solution builds 0-warning (Release + Debug); full hermetic suite green.

## Acceptance Criteria

- [ ] Forward happy-path tail issues ONE multi-key `KeyDeleteAsync([ExecutionData(entryId), MessageIndex(messageId)])` (not two single-key deletes).
- [ ] Recovery all-clear tail issues the same single multi-key delete.
- [ ] Source step (`entryId == Guid.Empty`): the terminal `DEL` removes the index; the `Guid.Empty` data operand no-ops without error.
- [ ] On any REINJECT path (recovery `anyInfra`, or existence/read exhaustion) NEITHER key is deleted and the `MessageIndex` key still exists.
- [ ] Tail delete exhaustion calls `KeyPersistAsync(MessageIndex)` then sends a `KeeperDelete` carrying `MessageId`.
- [ ] `KeeperDelete` contract exposes `MessageId`; `BuildDelete` populates it from the inbound message id.
- [ ] `DeleteConsumer` deletes both keys in one multi-key `KeyDeleteAsync`; absent operands no-op (no throw, drop-on-absent).
- [ ] Per-slot random-TTL writes (`ProcessorPipeline.cs:165`/`:275`) remain present (crash-backstop unchanged).
- [ ] No new metric series introduced.
- [ ] Solution builds 0-warning (Release + Debug); full hermetic suite green.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                              |
|--------------------|-------|------|--------|----------------------------------------------------|
| Goal Clarity       | 0.90  | 0.75 | ✓      | Unified atomic two-key delete + escalation, precise |
| Boundary Clarity   | 0.92  | 0.70 | ✓      | No-metrics, exact blast radius, hermetic-only locked |
| Constraint Clarity | 0.80  | 0.65 | ✓      | Single-instance atomic DEL, RetryLoop, A16 dup-tolerance |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 10 pass/fail checks incl. source-step + REINJECT edges |
| **Ambiguity**      | 0.125 | ≤0.20| ✓      |                                                    |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary              | Decision locked                                          |
|-------|-----------------|-------------------------------|---------------------------------------------------------|
| 0     | (pre-spec design) | A19 amendment co-authored     | Terminal two-key atomic DEL + persist-on-escalate + KeeperDelete{messageId,entryId} |
| 1     | Boundary Keeper | Observability in scope?       | No new metrics — pure behavior change                   |
| 1     | Boundary Keeper | Exact blast radius?           | Tail unify + persist + KeeperDelete.MessageId + DeleteConsumer; TTL/REINJECT/INJECT/forward-Post/recovery-classification UNCHANGED |
| 1     | Failure Analyst | Proof boundary?               | Hermetic facts only; live proof + net-zero → Phase 55   |

---

*Phase: 54-terminal-index-delete*
*Spec created: 2026-06-12*
*Next step: /gsd-discuss-phase 54 — implementation decisions (where exactly the unified tail sits, RedisKey-array helper shape, fact placement)*
