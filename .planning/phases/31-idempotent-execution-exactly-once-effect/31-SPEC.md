# Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect) — Specification

**Created:** 2026-06-04
**Ambiguity score:** 0.13 (gate: ≤ 0.20)
**Requirements:** 8 locked

## Goal

The orchestrator↔processor execution round-trip becomes **exactly-once-effect**: every duplicate inbound message — from configurable `Immediate(N)` retry, broker redelivery, publish-confirm ambiguity, the orchestrator's own re-dispatch, or a fan-in/merge re-dispatch — reproduces the same deterministic identity and is collapsed at the receiving node, so each step's downstream effect happens exactly once with **no lost branch**, and the processor transform (`ProcessAsync`) needs no idempotency logic.

## Background

Today (Phase 27) `EntryStepDispatchConsumer` mints `entryId`/`executionId` via `NewId.NextGuid()` **per result, per attempt**, writes each output to `L2[data(newEntryId)]`, sends `ExecutionResult`s one-by-one, and relies on a hard-coded `Immediate(3)` with **no receiver dedup**. `StepAdvancement.SelectNext` advances a step per incoming edge. Consequences, proven live: a fan-in/merge re-dispatches a step → `StepB4` executed twice in one fire (ES-confirmed); a retry/redelivery re-runs the whole `Consume` → re-minted ids → duplicate downstream execution. The non-idempotency is **framework-level** (re-minted ids + re-send + no dedup), not the processor business logic. Two hard facts force the fix to live at the **receiver** (dedup), not the producer (detection): at-least-once delivery (a processed-but-unacked message is redelivered), and one-directional confirmation loss (a thrown publish / lost ACK means "no confirmation," not "didn't happen"). Full design rationale in `31-CONTEXT.md`.

## Requirements

1. **Deterministic identity (H)**: every dispatch and result carries a deterministic identity `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` with `executionId` EXCLUDED.
   - Current: no message-identity hash; ids minted via `NewId.NextGuid()` per attempt; orchestrator has no dedup key.
   - Target: `H` is a deterministic 64-hex over the 5 fields; a retry/redelivery/re-dispatch of the same logical message yields the same `H`; `executionId` is lineage only and does not affect `H`.
   - Acceptance: a hermetic test computes `H`, recomputes it after a simulated retry (same 5 fields) → byte-identical; changing any of the 5 fields changes `H`; changing `executionId` does NOT change `H`.

2. **Per-fire identity + entry-step EntryId**: `correlationId` is the per-fire id; entry steps get a deterministic non-empty `EntryId`.
   - Current: `WorkflowFireJob` mints `correlationId` per fire; entry steps dispatch with `EntryId == Guid.Empty`, which is also the "source step / skip input read" signal.
   - Target: `correlationId` remains the per-fire id; entry steps carry `EntryId = hash(correlationId, stepId)` (non-empty, deterministic, stamped at fire); the "skip input read" signal moves to `InputDefinition == null` (the `EntryId == Guid.Empty` branch is removed).
   - Acceptance: an entry-step dispatch carries a non-empty `EntryId == hash(correlationId, stepId)`; a source step (`InputDefinition` null) with no L2 input still processes with empty input; two fires of the same entry step yield different `EntryId` (different `correlationId`).

3. **Content-addressed two-level L2 data**: result blobs and the result manifest are content-addressed.
   - Current: each output written to `L2[data(newEntryId)]` with a random per-attempt `newEntryId` → duplicate/divergent keys on retry; results sent one-by-one.
   - Target: each result blob at `data[hash(result)]` (idempotent overwrite); the processor returns a **manifest** `[hash(r1)…]` written at `data[EntryId]` where `EntryId = hash(manifest)`; empty result → `manifest = []` → `EntryId = hash([])` → terminal branch (zero successors); JSON-schema validation runs on the result DATA, not the manifest.
   - Acceptance: writing the same result twice targets the same key (one entry, no orphan); the orchestrator round-trips a manifest from `data[EntryId]`; an empty-result dispatch advances zero successors and is acked; output-schema validation runs on the blob, not the list-of-hashes.

4. **Effect-first CAS dedup (symmetric, both hops)**: every receiver dedups its inbound message on `H`.
   - Current: no dedup flag; `Immediate(3)`/redelivery re-runs `Consume` → re-sends → orchestrator advances duplicates.
   - Target: a `flag[H] = Pending|Ack` dedup at the processor (inbound `EntryStepDispatch`) AND the orchestrator (inbound `ExecutionResult`); **effect-first** — the downstream effect (write+send / dispatch) is produced BEFORE `flag[H]` is flipped `Pending → Ack` via an **atomic CAS**; an inbound `H` already `Ack` is dropped.
   - Acceptance: a duplicate message (same `H`) whose effect already completed is dropped (`flag == Ack`); a crash simulated between effect and CAS leaves `flag == Pending` so the redelivery re-produces the effect (collapsed downstream, not lost); the `Pending→Ack` flip is an atomic CAS (a concurrent-duplicate test shows the flag transitions once, with the second effect collapsed by `H`).

5. **Merge correctness (per-edge via input EntryId, content-collapse)**: a merge step's per-edge executions are distinguished by their input data.
   - Current: `SelectNext` re-dispatches a merge step per incoming edge → duplicate execution (`StepB4` ×2); a `{correlationId}:{stepId}` key would collide and override.
   - Target: per-edge executions carry different input `EntryId` (`hash(out_P1) ≠ hash(out_P2)`) → different `H` (no false dedup) and different output key (no override); **identical-input branches collapse** (same `EntryId` → same `H` → one execution); per-edge execution, NOT a join; `predecessorStepId` is NOT in `H`.
   - Acceptance: under the merge topology, two predecessors with DIFFERENT outputs yield two distinct `H` for the merge step (both execute, distinct output keys, no override); two predecessors with IDENTICAL output yield the same `H` (collapse to one execution).

6. **Manifest fan-out**: the orchestrator unbundles the manifest and dispatches per item.
   - Current: processor sends one `ExecutionResult` per result one-by-one; orchestrator `SelectNext` fans to `NextStepIds`.
   - Target: the processor sends one result message carrying the manifest `EntryId`; the orchestrator reads the manifest and, per item, dispatches a successor with `correlationId`+`workflowId` pass-as-is, `stepId`/`processorId` = the **successor's** (from `NextStepIds`/`SelectNext`), `EntryId` = the item hash, `executionId` regenerated; result-loop (N items) × next-step-loop (M successors) = N×M dispatches, each with its own deterministic `H`.
   - Acceptance: a multi-result process yields N manifest items; the orchestrator dispatches one successor per (item, `NextStep`) carrying the successor's `stepId`/`processorId` and the item's `EntryId`; an orchestrator redelivery reuses the same `H` per (item, successor) → deduped (no extra dispatch).

7. **Configurable retry + L2 key format**: the retry budget is config; L2 keys are `prefix + 64-hex`.
   - Current: `Immediate(3)` is hard-coded in the consumer definitions; L2 keys are `skp:{type}:{guid:D}`.
   - Target: retry count (and strategy) is bound from appsettings (default `Immediate(N)`); content-addressed `data` and `flag` keys are `prefix + 64-hex` (e.g. `skp:data:{64hex}`, `skp:flag:{64hex}`), within Redis limits, consistent with the existing `^[a-f0-9]{64}$` convention.
   - Acceptance: changing the configured retry count changes the effective retry budget (verified by a test that counts attempts); the `data` + `flag` key builders produce `prefix + 64-hex` (golden test pinning the exact strings).

8. **Live exactly-once-effect proof (real-stack)**: the merge topology no longer duplicates downstream.
   - Current: the merge topology demonstrably duplicates — `StepB4` ×2 observed in ES across fires.
   - Target: the same dual-pipeline workflow under the merge topology, run across multiple cron fires plus an **induced** `Immediate(N)`/redelivery, shows in ES exactly the expected per-fire downstream effect set with NO extra downstream execution (the inverse of `StepB4` ×2).
   - Acceptance: a real-stack E2E asserts that, per cron trigger (`CorrelationId`), the set of advanced/executed downstream effects equals the expected per-fire set with zero duplicates — even with an induced retry/redelivery; the phase-close gate (3-consecutive-GREEN + triple-SHA BEFORE=AFTER) holds with the new `data`/`flag` keys covered by scan-clean teardown.

## Boundaries

**In scope:**
- Deterministic identity `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` (executionId excluded)
- Per-fire `correlationId` reuse + entry-step `EntryId = hash(correlationId, stepId)`; source-step signal → `InputDefinition == null`
- Content-addressed two-level L2 data (result blobs + manifest); empty result = terminal branch
- Effect-first `flag[H] = Pending|Ack` dedup via atomic CAS, at BOTH hops (processor inbound dispatch + orchestrator inbound result)
- Merge correctness via input `EntryId` (collapse identical-input; per-edge, not join)
- Manifest fan-out (successor `stepId`/`processorId`, regenerated `executionId`)
- Configurable retry count/strategy (default `Immediate(N)`); `prefix + 64-hex` L2 key builders for `data`/`flag`
- Wire-contract extension (`EntryStepDispatch`/`ExecutionResult` gain the deterministic-id + manifest fields) + `L2ProjectionKeys` additions
- Real-stack proof: merge topology across fires + induced retry/redelivery shows no extra downstream execution + close gate

**Out of scope:**
- **Cancelled circuit-breaker** (exhaustion → `Cancelled`, two-level stop = `cancelled[workflowId]` marker + unschedule via L1 `jobId`, removal of `StepEntryCondition.PreviousCancelled`, resume path) — moved to **Phase 32**; separable concern with its own decisions (trip on first exhaustion already chosen; blast radius; resume procedure). Phase 31's receiver flow does NOT check a cancel marker.
- **Transactional outbox** (atomic flag+send to close even the collapsible-duplicate window) — deliberately deferred; effect-first + downstream dedup chosen instead.
- **`ProcessAsync` external-side-effect idempotency** — out of framework scope; this phase delivers exactly-once-EFFECT, not exactly-once-EXECUTION (`ProcessAsync` may re-run, so it must be pure or independently idempotent).
- **`predecessorStepId`-in-`H` strict-per-edge merge** — not chosen (content-collapse selected).
- **Back-off retry as the default strategy** — available as config, not the default (`Immediate(N)` is the default).

## Constraints

- `ProcessAsync` must be **deterministic** — `EntryId = hash(output)` assumes same input → same output; non-determinism produces orphan keys and possible divergence under duplicate delivery.
- Guarantee is **exactly-once-EFFECT only** — the irreducible residual (a crash between the send and the CAS flip) is a downstream-collapsed duplicate, never a loss.
- **Per-fire content-addressed keys accumulate** — L2 TTL must outlive the slowest fire + redelivery; close-gate teardown must scan-clean the new `data`/`flag` keys.
- **Hashing must be deterministic across processes** — a single canonical serialization of the hash inputs (SHA-256, lowercase 64-hex, matching the existing `SourceHash` convention).
- **Reuse existing machinery** — Orchestrator, processor, Quartz scheduling, L1 store, MassTransit/RabbitMQ transport; the wire contracts gain fields but the transport and lifecycle are unchanged.

## Acceptance Criteria

- [ ] `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` is deterministic, identical across a simulated retry, and unaffected by `executionId`.
- [ ] Entry-step dispatch carries a non-empty `EntryId == hash(correlationId, stepId)`; source-step processing keys on `InputDefinition == null`, not `EntryId == Guid.Empty`.
- [ ] Result blobs and manifest are content-addressed; identical content → same key (no orphan); empty result advances zero successors.
- [ ] `flag[H]` dedup is effect-first with an atomic CAS; a duplicate whose effect completed is dropped; a crash-window re-run re-produces a collapsed duplicate (no loss).
- [ ] Merge: different-output branches → distinct `H`, no override; identical-output branches → collapse to one.
- [ ] Manifest fan-out dispatches one successor per (item, `NextStep`) with the successor's `stepId`/`processorId` + the item `EntryId`; an orchestrator re-dispatch is deduped.
- [ ] Retry count is configurable (verified by attempt count); `data` + `flag` key builders produce `prefix + 64-hex` (golden test).
- [ ] Real-stack: merge topology across fires + induced retry/redelivery shows the expected per-fire downstream effect set with NO extra execution (the `StepB4`-×2 inverse); close gate (3-GREEN + triple-SHA) holds.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.92  | 0.75 | ✓      | Exactly-once-effect, dedup core scoped (Cancelled → Phase 32) |
| Boundary Clarity   | 0.90  | 0.70 | ✓      | Split locked; explicit out-of-scope incl. outbox + Phase 32  |
| Constraint Clarity | 0.80  | 0.65 | ✓      | ProcessAsync determinism, exactly-once-EFFECT, TTL, key fmt  |
| Acceptance Criteria| 0.80  | 0.70 | ✓      | 8 pass/fail criteria; StepB4-×2 inverse is the live proof    |
| **Ambiguity**      | 0.13  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective            | Question summary                              | Decision locked                                                      |
|-------|------------------------|-----------------------------------------------|---------------------------------------------------------------------|
| 0     | Researcher (pre-interview) | Current round-trip + duplicate causes      | Framework-level non-idempotency; receiver dedup is the only fix (CONTEXT) |
| 1     | Boundary Keeper        | Full design vs split                          | SPLIT — Phase 31 = dedup core; Phase 32 = Cancelled circuit-breaker  |
| 1     | Seed Closer            | Cancelled trip threshold                      | First exhaustion (deferred to Phase 32; retry count = transient knob) |
| 1     | Seed Closer            | Identical-input merge: collapse vs per-edge   | COLLAPSE (content-converge); no `predecessorStepId` in `H`           |
| 1     | Seed Closer            | Retry strategy default                        | `Immediate(N)` configurable (default); back-off available as config |

---

*Phase: 31-idempotent-execution-exactly-once-effect*
*Spec created: 2026-06-04*
*Next step: /gsd-discuss-phase 31 — implementation decisions (hash input canonicalization, CAS Lua vs SET, contract field shape, L1/L2 wiring, test harness for induced retry)*
