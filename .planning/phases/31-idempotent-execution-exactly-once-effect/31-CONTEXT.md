# Phase 31 — Idempotent Execution Round-Trip (Exactly-Once-Effect) — CONTEXT

**Status:** PLANNED (design agreed in discussion 2026-06-04; awaiting spec/plan).
**Supersedes the relevant parts of:** Phase 27 (Execution Round-Trip) — which mints `NewId` entry/execution ids, sends results one-by-one, and relies on `Immediate(3)` with no receiver dedup.

## Problem (why this phase exists)

The current round-trip is **not idempotent**. `Immediate(3)` re-runs the whole `Consume` (re-mints ids, re-writes L2, re-sends results), and a fan-in/merge re-dispatches a step — both produce duplicate downstream execution (proven live: `StepB4` ×2 under one fire). The framework-level non-idempotency (re-minted `entryId`/`executionId` + re-send + no orchestrator dedup), **not** the processor business logic, is the cause.

Two unavoidable facts force the design:
1. **At-least-once delivery.** A processed-but-unacked inbound message is redelivered by the broker (crash / lost ACK), and `Immediate(3)` re-runs on a thrown send. Neither can be turned off at the producer.
2. **Confirmation is lossy in one direction.** A thrown publish (lost publisher confirm) and a lost inbound ACK both mean "no confirmation," not "didn't happen" — so the producer can never *know* whether its output landed. Resolution must therefore be **dedup at the receiver**, not detection at the producer.

## Agreed design (the protocol)

### Identities — all deterministic
```
correlationId = per-fire id (the cron/scheduled-job id; minted once per WorkflowFireJob fire,
                stamped on every message of that fire, stable across that fire's retries/redeliveries)
workflowId, stepId, processorId = stable, pass-as-is
EntryId       = hash(data)                              // content-addressed data key
executionId   = lineage only — NOT part of H (may be regenerated freely)
H             = hash(correlationId, workflowId, stepId, processorId, EntryId)   // the dedup identity
```
`H` is content-and-position based and fully deterministic (because `EntryId` is content-addressed and `executionId` is excluded). Any duplicate from any source — `Immediate(3)`, redelivery, publish-confirm ambiguity, the orchestrator's own re-dispatch, fan-in — reproduces the **same `H`** and is collapsed at the receiving node.

**Entry steps (no predecessor) — per-fire `EntryId` (refinement 2026-06-04):** entry steps have no input, so their `EntryId` would be `Guid.Empty` (degenerate in `H`). Instead, `WorkflowFireJob` generates a per-fire `EntryId` alongside `correlationId` and stamps it on the entry-step dispatch (stable across that fire's retries → same `H`). Preferred form: derive it deterministically, `EntryId_entry = hash(correlationId, stepId)`, keeping the "deterministic everywhere" principle (a carried random guid also works; one shared per fire is fine — `stepId` disambiguates). **The "source step / skip input read" signal moves off `EntryId == Guid.Empty` onto `InputDefinition == null`** — the existing read-then-check path already handles it (read `L2[EntryId]` → empty → `InputDefinition` null → proceed with empty input), so the `Guid.Empty` branch can be removed.

### Two L2 namespaces
- `data[EntryId] = payload` — content-addressed, idempotent writes. **Two levels:** each result blob at `data[hash(result)]`; the processor's returned value is the **manifest** = `[hash(r1), hash(r2), …]`, stored at `data[EntryId]` where `EntryId = hash(manifest)`. (Empty result → `manifest = []` → `EntryId = hash([])` → terminal branch, fans out to zero successors.)
- `flag[H] = Pending | Ack` — dedup state, flipped by an **atomic CAS** (Lua / conditional SET).

**Redis key sizing (checked 2026-06-04):** all keys are `prefix + 64-hex` (SHA-256 = 64 bytes; e.g. `skp:data:{64hex}`, `skp:flag:{64hex}` ≈ 73 bytes). Redis keys are binary-safe up to 512 MB — 64 bytes is trivially within limit, no perf/collision concern, and 64-hex matches the existing `^[a-f0-9]{64}$` SourceHash convention. Spec pins the exact prefix + 64-hex format per namespace.

### Effect-first dedup (symmetric — every send, both directions)
Receiver, on an inbound message with identity `H` (workflow `W`):
0. `if cancelled[W] == true → drop` (the Cancelled in-flight stop — see Failure-policy).
1. `if flag[H] == Ack → drop` (safe: under effect-first, `Ack ⟹ effect completed).
2. else: **produce the downstream effect first** (process → write content-addressed data → send child message(s), each carrying its own `Pending`), **then** atomic-CAS `flag[H]: Pending → Ack`, then broker-ack.

**Atomic CAS scope:** the CAS is the end-of-processing `Pending → Ack` flip — it gives an atomic transition and dedups the **sequential** redelivery (the common case). It does **not** prevent a *concurrent* double-effect (two duplicates both read not-`Ack` and both process); those are collapsed downstream by the deterministic `H`. Claiming `Ack` *before* the effect would prevent the concurrent double-run but reintroduces the loss horn — so we keep effect-first (CAS-at-end) and let `H` dedup absorb concurrency.

**Why effect-first:** a crash between the effect and the marker leaves `flag[H] = Pending` → the redelivery **re-sends** (a *collapsible duplicate*, never a loss), because the re-sent child carries the same deterministic `H_child` and is collapsed at the next node. Ack-first would set the marker before the effect → a crash drops the redelivery → **lost branch**. Effect-first converts the only unavoidable failure from *loss* (unrecoverable) into *duplicate* (collapsed downstream).

### Fan-out (multi-result)
Processor returns one `EntryId` (the manifest). Orchestrator reads the manifest and, per item, dispatches a successor with: `correlationId`+`workflowId` pass-as-is; **`stepId`/`processorId` = the successor's** (from `NextStepIds`/`SelectNext`); `EntryId` = the item hash; `executionId` regenerated (lineage). The result-loop (N items) and the next-step-loop (M successors via `SelectNext`) are orthogonal → N×M dispatches, each with its own deterministic `H`.

### Merge correctness (the EntryId is the load-bearing field)
A merge step is one `stepId` but two executions (one per incoming branch). Each branch delivers a **different input `EntryId`** (`hash(out_P1) ≠ hash(out_P2)`) → different `H` → **not falsely deduped** (per-edge execution preserved), and different output `EntryId` → **no override**. This is exactly why `EntryId` must be in `H`. Edge cases to decide at spec:
- **Identical-input branches collapse** (same data → same `EntryId` → one execution). Likely whenever outputs are low-entropy (empty results all share `hash([])`; default/status tokens). Acceptable for state-convergence; if strict per-edge count is required even for equal data, fold `predecessorStepId` into `H`.
- The protocol does **per-edge execution, not a join/barrier** (matches locked `241ba32` semantics; a true combine-both-inputs join would need a barrier).

### Configurable retry (refinement 2026-06-04)
The retry **limit and strategy** are config (bound from appsettings, e.g. `RetryOptions{ Limit, Strategy }`) — not a hard-coded `Immediate(3)`. The strategy knob is where "switch to `Exponential`/`Interval` back-off to separate transient from persistent and avoid timeout-storm amplification" lands. **Single source of truth:** the same `Limit` feeds both `UseMessageRetry` and the `Cancelled` final-attempt check (`GetRetryAttempt() == Limit`), or they desync.

### Failure-policy hook (Cancelled = unconditional, two-level stop) — DEFERRED TO PHASE 32
> Per the Phase 31 SPEC (2026-06-04), the Cancelled circuit-breaker is split out to **Phase 32**. Phase 31's receiver flow does NOT check the cancel marker. This section is the design record for Phase 32. Trip-on-first-exhaustion is chosen; blast radius / resume remain Phase 32 spec decisions.

On retry exhaustion the processor (final-attempt handler, `GetRetryAttempt() == configured Limit`) **stops the workflow at two levels** and sends the consumed message back as `Cancelled` instead of dead-lettering:
1. **Current running workflow (in-flight)** — the processor sets a `cancelled[workflowId] = true` marker directly in L2 (it has Redis access). **Every receiver** (processor `EntryStepDispatchConsumer`, orchestrator `ResultConsumer`) **checks this marker before processing and drops** any in-flight message for a cancelled workflow → the fire drains to a halt. (Stops *advancing*, not a rollback — already-completed steps are not undone. Keyed on `workflowId` so any concurrent in-flight fire of that workflow also stops.)
2. **Scheduled job (future fires)** — the processor sends the consumed message as `Cancelled`; the orchestrator resolves `jobId` from **L1** (`store.TryGet(workflowId) → wf.JobId` — in-memory, **no L2 read/write for the jobId**; absent-from-L1 ⇒ no-op) and **unschedules the Quartz job** (the processor cannot do this — the scheduler lives in the orchestrator; reuses the existing Stop/Teardown machinery; idempotent unschedule). The only L2 *write* in the Cancelled path is the `cancelled[workflowId]` marker in step 1.

Processor order is effect-first: set `cancelled[workflowId]` → send `Cancelled` → ack (a crash between leaves the marker set; redelivery re-exhausts → re-sets marker (idempotent) + re-sends `Cancelled`). **`Cancelled` is a terminal STOP, intercepted *before* `SelectNext`** — it advances **no** successor (not even `Always`/4 ones); the `Cancelled` message is deduped via deterministic `H`. **Resume** = clear `cancelled[workflowId]` + re-Start.

**Enum change (refinement 2026-06-04):** **remove `StepEntryCondition.PreviousCancelled` (value 3)** — it's meaningless now (no step advances *on* a cancelled predecessor; `Cancelled` halts the whole job). Leave `3` as a removed/gap value (do NOT renumber 0/1/2/4/5, or existing step data reinterprets); verify no live step uses `EntryCondition == 3` (the dual-pipeline steps use `Always`/4 — likely clean) and update `StepDtoValidator` to reject 3. `StepOutcome.Cancelled` (3) stays (the processor still reports it) but no longer mirrors an entry condition — update the "ints mirror `Previous*`" note: `Cancelled` is special-cased, not matched by `SelectNext`.

**Open decisions (spec):** trip on *sustained* failure not first exhaustion (threshold / back-off retry) so transients don't halt the workflow; blast radius (whole-workflow halt vs branch-level fail); a defined **resume** path; keep `_error` as the backstop for **bus-down** exhaustion (the Cancelled can't be sent then).

## Properties achieved
- **Exactly-once *effect*** (not exactly-once *execution*): `ProcessAsync` may still re-run, so it must be pure or its external side effects independently idempotent. The downstream effect is deduped.
- **No loss** (effect-first); all duplicates collapsed (deterministic `H`); merges distinguished by input; concurrency serialized by CAS.
- **`Immediate(3)` kept and now safe.** Consider switching to `Exponential`/`Interval` back-off to better separate transient from persistent and avoid timeout-storm amplification.

## Conditions / caveats (state at spec)
1. `ProcessAsync` determinism (`EntryId = hash(output)` assumes same input → same output); non-determinism → orphan keys + possible divergence under duplicate delivery.
2. Per-fire keys accumulate → L2 TTL sized to outlive the slowest fire + redelivery.
3. A **transactional outbox** would close even the collapsible-duplicate window (atomic flag+send); deliberately deferred in favour of effect-first + downstream dedup.
4. Rejected along the way (record so they are not re-litigated): random `SendId` (doesn't catch the sender's own re-send); `{correlationId}:{stepId}` key (collides merge per-edge executions); gate-the-send-on-key-existence (lost-send bug); catch-all + remove retry (can't catch crash/redelivery; publish-confirm ambiguity); `GetRetryAttempt()==0`-conditioned Ack check (resets on redelivery, doesn't encode effect-completion); integer progress counter (subdivides the dual-write, corrupts under concurrency).

## Scope boundary
This phase reworks the **processor `EntryStepDispatchConsumer`** (deterministic ids, content-addressed two-level data, manifest result, effect-first CAS dedup, Cancelled-on-exhaustion) and the **orchestrator `ResultConsumer`/advancement** (inbound-result dedup on `H`, manifest unbundling + fan-out, Cancelled→halt). `WorkflowFireJob` already mints the per-fire `correlationId` (reused as `fireId`). The wire contracts (`EntryStepDispatch`/`ExecutionResult`) gain the deterministic-id fields; `L2ProjectionKeys` gains the content-addressed data + `flag[H]` key builders.
