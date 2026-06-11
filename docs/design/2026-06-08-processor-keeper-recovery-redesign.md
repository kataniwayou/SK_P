# Processor Pre/In/Post-Process + Keeper Recovery — Redesign Spec

**Status:** LOCKED 2026-06-08 — source of truth. No additions or discrepancies beyond what is stated here.
**Amended 2026-06-08 (A15):** the processor→orchestrator result is split into **four typed records** (`StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing`), replacing the single `ExecutionResult(Outcome)` — see the **Result contract** section and locked-decision **A15**. Every "send orchestrator result" / `ExecutionResult(<status>, …)` phrasing below is read through that section.
**Amended 2026-06-09 (A16):** the **at-least-once / no-dedup delivery model is now a named guarantee** — the v4 execution path is at-least-once, carries **no dedup / idempotency key** (the v3.x `H` + dedup gate were removed in Phase 43), and **tolerates duplicate effects downstream by construction** (a redelivered message reproduces its effect; nothing collapses it). This elevates the **Delivery model** line (line 5) and locked decisions **No dedup / idempotency key** (line 105) + **A4** single `_DLQ1` (line 112) to a single test-cited guarantee. It is proven **hermetically** by the Phase-47 traceability ledger `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-DLQ-AUDIT.md` (every RESIL-02 / RESIL-03 + SC-1/2/3 row → a green test: single-DLQ consolidation + duplicate-delivery no-collapse + data-gone terminal); the **live / real-stack** proof (broker `skp-dlq-1` + `x-message-ttl`) is **Phase 49** (TEST-01..03). **Bundled (Phase-46 deferred note):** `KeeperReinject` carries an additive **`Payload : string`** field — verified live by `KeeperContractTests` and stamped/set by `ProcessorPipeline.BuildReinject` + `RecoveryDeadLetterFacts`; without it a recovered run silently loses its author config. *(Additive amendment; no source change.)*
**Amended 2026-06-09 (A17):** the v3.x **reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path + the `keeper-dlq` / `keeper-fault-recovery` queues are now retired** (RETIRE-03) — the reactive consumers, `KeeperRecoveryHandler`, the orphaned `KeeperMetrics` meter, and the `KeeperQueues.DeadLetter`/`FaultRecovery` consts were deleted in Phase 48 (the v4 5-state `keeper-recovery` consumer is the sole recovery mechanism; `KeeperQueues.Recovery` is the sole surviving Keeper queue; the single consolidated `skp-dlq-1` is the sole terminal dead-letter). RETIRE-01 (`H`/`flag[H]`/CAS dedup) + RETIRE-02 (content-addressing + result manifest + N×M fan-out) are confirmed gone by a remnant sweep. Proven **hermetically** by `.planning/phases/48-v3-x-teardown/48-TEARDOWN-AUDIT.md` (every RETIRE-01/02/03 + SC-1/2/3 row → a green reflection guard / source-scan: no reactive `Fault<T>` consumer survives, no `keeper-dlq` literal under `src/Keeper/`, `KeeperQueues` exposes only `Recovery`, `ExecutionData` is `Guid`-only + no `*Manifest*`), and closed by the SC-4 hermetic gate (suite GREEN ×3 + Release/Debug 0-warning build); the live / real-stack + triple-SHA net-zero proof is **Phase 49** (TEST-01..03). *(Additive amendment; the source teardown is Phase 48, this doc only records it.)*
**Amended 2026-06-11 (A18 — SUPERSEDE → v5.0.0, LOCKED):** a **second recovery re-architecture supersedes the keeper-owned composite-backup recovery (Model B)** described below. It replaces the composite key `L2[corr:wf:ProcessorId:executionId]` + the 5 states (`UPDATE` and `CLEANUP` drop out) with a **processor-owned `messageId` slot-array** (`L2[messageId][x]=entryId`, retired with `guid.empty` only **after** a confirmed orchestrator send), a **3-state** keeper (`REINJECT`/`INJECT`/`DELETE`, `INJECT` forward-only), a **split infra taxonomy** (`infra_messageId` → drop · `infra_entryId` → `INJECT`), a configurable **DLQ1-vs-sustained-outage** keeper exhaustion policy, and **gate-closed non-destructive consume**. **The Identities & L2 key scheme, the Result contract (A15), the BIT health gate + global pause/resume (A14), and the at-least-once guarantee (A16) below still hold; only §3 Post-Process backup/cleanup mechanics and the §Keeper recovery states are superseded.** Full superseding spec: **"Recovery Re-architecture (A18)"** at the end of this doc. The Model-B sections below are **retained verbatim as the as-shipped v4.0.0 record (phases 43–49) — do not delete them.** This is a **breaking change to the recovery core** — it is the **v5.0.0** milestone (phases 50+).

**Delivery model:** at-least-once; **no dedup / idempotency key** (the v3.x `H` + dedup gate are removed); duplicate effects are tolerated downstream.

---

## Identities & L2

Every message carries: `correlationId, workFlowId, stepId, ProcessorId, executionId, entryId`.

- `entryId` is a **GUID**. `Guid.Empty` = source step → **skip L2 read and skip end-delete**.
- L2 (Redis projection), two key schemes:
  - **Data key** `L2[entryId]` — per-item payload. **No TTL.**
  - **Composite backup key** `L2[correlationId:workFlowId:ProcessorId:executionId]` — written by `UPDATE`, **deleted the moment it is redundant** (processor `CLEANUP` after a successful output write; keeper after a successful `INJECT`). **TTL = 2 days (configurable in days) is a crash-backstop only** — not the cleanup.
- **infra failure** = an L2 op that fails after its retry loop is exhausted. For a **read**, failure = a Redis exception **or an absent/empty key** (the data isn't there). For **delete/write**, failure = a Redis exception. Anything else that fails = **business failure**.
- **retry loop** = N immediate attempts, no backoff, shared config `Retry:Limit` (default 3).
- **`_DLQ1`** = single consolidated dead-letter queue for every terminal send/L2 give-up (processor *and* keeper). No separate `keeper-dlq`.

---

## Result contract (four typed records) — Amendment A15 (2026-06-08)

> **Supersedes** every "send orchestrator result" / `ExecutionResult(<status>, …)` phrasing in this doc. Where the round-trip and Keeper sections write `ExecutionResult(Completed, …)` etc., read it as **the matching one of the four typed records below**.

The processor→orchestrator result is **four typed records**, not a single `ExecutionResult` carrying a `StepOutcome`:

- **`StepCompleted`** `{ correlationId, workFlowId, stepId, ProcessorId, executionId, entryId }` — carries the **real** per-item data-key `entryId`.
- **`StepFailed`** `{ …six ids, errorMessage }` — `entryId = Guid.Empty`.
- **`StepCancelled`** `{ …six ids, cancellationMessage }` — `entryId = Guid.Empty`.
- **`StepProcessing`** `{ …six ids }` — `entryId = Guid.Empty`.

All four implement `IStepResult : IExecutionCorrelated`, carry the six ids, and drop `H`. **`entryId` seeding is done by the contracts** (`StepCompleted` = the real key; the other three default `Guid.Empty`), so a consumer reads `entryId` uniformly with no branching.

`StepOutcome` (`Processing/Completed/Failed/Cancelled`, int-aligned to `StepEntryCondition.Previous*`) is **no longer a wire field** — it survives only as the orchestrator-internal advancement vocabulary and the per-type consumer knob.

**Why typed (routing rationale):** the orchestrator consumes each result with a typed `TypedResultConsumer<TMessage> where TMessage : IStepResult` whose only per-type knob is its `StepOutcome` — **no status `if`/`switch` anywhere**; routing is by message type, funneling into the same `StepEntryCondition` match. The processor send-side emits the matching record per item (In-Process `completed`→`StepCompleted`, business `failed`→`StepFailed`, author-thrown `processing`/`cancelled`→`StepProcessing`/`StepCancelled`). Keeper `INJECT` reconstructs a `StepCompleted`. All four land on the one orchestrator-result queue.

---

## Processor round trip

### 1 · Pre-Process
1. Consume `EntryStepDispatch` from orchestrator.
2. If `entryId == Guid.Empty` → skip read; `validatedData` = empty (no input validation).
3. Else read `L2[entryId]`; on Redis exception **or absent/empty key** → retry loop; exhausted → **infra(READ)**.
4. Validate read data vs input schema; on failure → **business `Failed`**.

**Terminal:**
- **infra(READ)** → send Keeper **`REINJECT`** `{corr, wf, step, proc, exec, entryId}`. End round trip. *(Input left intact for the keeper.)*
- **business `Failed`** → send orchestrator result `Failed`, then run **End-delete (§4)**.
- **success** (read + validate OK) → In-Process.
- Any processor send that throws → retry loop; exhausted → `_DLQ1`.

### 2 · In-Process (author override)
- Signature: `(validatedData, payload) → List<Item>`, where `Item = { result: "completed" | "failed", data, executionId }` and **`executionId` is author-minted, new per item**.
- Author deserializes `payload`; may throw a status-carrying exception: `"processing" | "failed" | "cancelled"`.
- Wrapped in try/catch. **Any exception** → send ONE orchestrator result with Outcome = thrown status (an unexpected / non-status exception ⇒ `"failed"`); whole batch aborted (no Post-Process); then run **End-delete (§4)**.
- Normal return → per-item list flows to Post-Process.

### 3 · Post-Process (per item, in order)
1. If `completed` → validate `data` vs output schema → set `completed` | `failed`.
2. If `completed` → send Keeper **`UPDATE`** `{corr, wf, step, proc, exec, validatedData}` (keeper writes the composite backup, TTL 2d).
3. If `completed` → generate `entryId` (GUID), write `L2[entryId]` (no TTL); on Redis exception → retry loop; exhausted → item = `failed (infra)`, else `completed` **and send Keeper `CLEANUP {corr, wf, step, proc, exec}`** to delete the now-redundant composite backup.
4. If item is **not-infra** (`completed` ∪ business `failed`) → send orchestrator result. A `completed` result carries `entryId` + `executionId`.
5. If item is **infra** → send Keeper **`INJECT`** `{corr, wf, step, proc, exec}`.

- N completed items → **N separate per-item orchestrator results** (no manifest).
- Any processor send that throws → retry loop; exhausted → `_DLQ1`.

### 4 · End-delete (`finally` — runs on every path where the read succeeded)
- **Applies to:** happy path, pre-process business-fail, and In-Process exception.
- **Skipped only on:** infra(READ)/`REINJECT` (input left intact) and `Guid.Empty` source steps.
- Delete `L2[entryId]`; on Redis exception → retry loop; exhausted → **infra(DELETE)** → send Keeper **`DELETE`** `{corr, wf, step, proc, exec, entryId}`.
- End of round trip. *(A delete failure never alters results already sent to the orchestrator; `DELETE` is L2 garbage-collection only.)*

---

## Keeper

### BIT health gate (background)
- `while` loop with a configurable delay in seconds (`Probe:DelaySeconds`). Each tick runs **BIT** against L2 (read + write-then-delete probe).
- BIT results are **suppressed** (exceptions never crash the loop). The result fans out a **global** broadcast to **all** orchestrators: **unhealthy → pause all jobs; healthy → resume all jobs.**
- Orchestrator pause/resume-all is **idempotent per job** (pause only if Running, resume only if Paused); job state is known via **Quartz `TriggerState`**.

### Recovery consumer (messages from processors)
- Consumes `UPDATE / REINJECT / INJECT / DELETE / CLEANUP`.
- **Partitioned by `corr:wf:ProcessorId:executionId`** (per-key ordering — e.g. MassTransit `UsePartitioner`): messages for the **same exec** are processed in arrival order, so `UPDATE` always precedes that exec's `CLEANUP`/`INJECT`; different execs still run in parallel.
- Performs the L2 op **only when L2 is healthy (BIT gate open)**; gate-closed → the consumer **waits for the gate** (bounded so as not to exceed the broker consumer timeout).
- **`UPDATE`** → write `validatedData` to `L2[corr:wf:ProcessorId:executionId]`, TTL 2 days (configurable in days) — the TTL is a crash-backstop; the copy is normally deleted by `CLEANUP`/`INJECT`.
- **`REINJECT`** → read `L2[entryId]`; if **present** (transient outage — data survived) → re-inject a reconstructed `EntryStepDispatch` to `queue:{ProcessorId}`; if **absent/empty** (data truly gone — redelivery after end-delete, or missing input) → read failure → retry loop → `_DLQ1`.
- **`INJECT`** → read `L2[corr:wf:ProcessorId:executionId]` → **generate `entryId`** → write `L2[entryId]` (no TTL) → inject a reconstructed `StepCompleted` (carrying `entryId` + `executionId`; per A15 — was `ExecutionResult(Completed, …)`) to the orchestrator result queue → **delete the composite copy** (now redundant).
- **`DELETE`** → delete `L2[entryId]`.
- **`CLEANUP`** → delete the composite copy `L2[corr:wf:ProcessorId:executionId]` (happy-path redundancy cleanup; ordered after this exec's `UPDATE` by the partitioner).
- Any keeper send that throws → retry loop; exhausted → `_DLQ1`.
- Any keeper L2 op that throws → retry loop; exhausted → `_DLQ1`.

---

## Locked decisions (traceability)

| Tag | Decision |
|-----|----------|
| — | No dedup / idempotency key (v3.x `H` + dedup gate removed); at-least-once; duplicates tolerated. |
| A2 | A read finding an absent/empty `L2[entryId]` = read failure → **infra(READ)** → `REINJECT`. In the redelivery-after-end-delete (or genuinely-missing-input) case the data is truly gone, so Keeper's `REINJECT` read also misses → `_DLQ1`. |
| D1 | End-delete uses `finally` semantics over **all** read-succeeded paths (Option A). |
| — | `entryId` is a GUID; `L2[entryId]` has **no TTL**; composite backup key TTL = **2 days, configurable in days** — a crash-backstop only. |
| B / CLEANUP | Keeper owns the composite copy (Model B). The recovery consumer is **partitioned by `corr:wf:ProcessorId:executionId`** for per-key ordering. A 5th state **`CLEANUP`** deletes the redundant copy on the happy path (processor sends it after a successful output write); `INJECT` deletes it on the recovery path. Net-zero on every non-crash path; the 2-day TTL only covers a crash mid-recovery. Per-key ordering also guarantees `UPDATE` precedes `INJECT` (no data-gone-→`_DLQ1` race). |
| A8/A12 | Author mints `executionId` per item; N completed items → N per-item orchestrator results (no manifest). |
| A14 | Global pause-all / resume-all to all orchestrators (replaces per-workflow pause). |
| A4 | Single `_DLQ1` for all terminal give-ups; `keeper-dlq` removed. |
| A3 | Retry loops: N immediate attempts, no backoff, shared `Retry:Limit`. |
| A15 | Processor→orchestrator result is **four typed records** (`StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing : IStepResult : IExecutionCorrelated`), replacing the single `ExecutionResult(Outcome)`. `entryId` seeding is contract-level (`StepCompleted` = real key, the other three `Guid.Empty`); `StepOutcome` is demoted off the wire to internal advancement/consumer vocabulary. Enables no-`if`/`else` typed-consumer routing. See the **Result contract** section. *(Amendment 2026-06-08.)* |
| A17 | The v3.x **reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path + the `keeper-dlq` / `keeper-fault-recovery` queues are retired** in Phase 48 (RETIRE-03): the reactive consumers + `KeeperRecoveryHandler` + the orphaned `KeeperMetrics` meter + the `KeeperQueues.DeadLetter`/`FaultRecovery` consts are deleted — the v4 5-state `keeper-recovery` consumer is the sole recovery mechanism, `KeeperQueues.Recovery` the sole surviving Keeper queue, and `skp-dlq-1` (A4) the sole terminal dead-letter. RETIRE-01/02 confirmed gone by remnant sweep. Proven by `48-TEARDOWN-AUDIT.md` (reflection + `src/Keeper/` source-scan guards) + the SC-4 hermetic close gate; live / triple-SHA proof is Phase 49. *(Additive amendment 2026-06-09; source teardown is Phase 48.)* |
| A18 | **SUPERSEDE Model B** (proposed Phase 50 / post-v4.0.0): processor-owned `messageId` slot-array recovery (`L2[messageId][x]=entryId`, `guid.empty`-retire only after a confirmed send) replaces the composite backup key + `UPDATE`/`CLEANUP`; keeper shrinks to **3 states** (`REINJECT`/`INJECT`/`DELETE`, `INJECT` forward-only); split infra (`infra_messageId` → drop / `infra_entryId` → `INJECT`); `REINJECT` and source-delete mutually exclusive; configurable **DLQ1-vs-sustained-outage** exhaustion; **gate-closed non-destructive consume**. Identities / A15 / A14 / A16 / A4 unchanged. See **"Recovery Re-architecture (A18)"**. *(LOCKED amendment 2026-06-11; v5.0.0 source of truth.)* |

---

## Scope note

This **replaces** large parts of the shipped v3.x execution model — effect-first idempotency (`H`/content-addressing/CAS), the single-`ProcessAsync` seam, the result manifest, and the reactive `Fault<T>` recovery path. It is a **breaking change** and should be planned as the next **major** milestone (v4.0.0).

---

## Recovery Re-architecture (A18 — supersede Model B) — v5.0.0 (Phase 50+)

**Status:** LOCKED 2026-06-11 — **v5.0.0 source of truth**. **Supersedes** §3 Post-Process backup/cleanup mechanics and the §Keeper recovery states above. Everything else above (Identities & L2 data key, Result contract A15, BIT health gate + global pause/resume A14, at-least-once A16, single `_DLQ1` A4) **still holds**.

**What changes vs Model B:** drop the keeper-owned composite backup key `L2[corr:wf:ProcessorId:executionId]` and the `UPDATE`/`CLEANUP` states. Recovery becomes **processor-owned** via a per-message slot-array index, and the keeper shrinks to **3 states**.

### New L2 vocabulary
- `L2[entryId]` — per-item data (GUID key). *(unchanged)*
- `L2[messageId]` — a per-message **slot array** of allocated output `entryId`s; doubles as the **idempotency marker** and the **allocation table**. Slots are retired to `guid.empty` only **after** their result is safely delivered. `TTL(random)`.
- **infra failure** = an L2 op exhausted after its retry loop. Split by site: `infra_messageId` (slot/allocation write) vs `infra_entryId` (data write/read).

### Global rules *(unchanged from above)*
- `_error` routing disabled; `UseMessageRetry = none`.
- **send op:** try/catch retry loop; exhausted → **throw** (broker redelivery).
- **L2 op:** try/catch retry loop; exhausted → routed per-site (below).

### Processor — FORWARD pass (`NOT exist L2[messageId]`)
```
1) processor consumes EntryStepDispatch from orchestrator.   // trigger, outside the branch

if NOT exist L2[messageId]:
    (existence-check L2 fail → exhausted → keeper REINJECT; end)

  PRE
    read L2[entryId]                 (L2 fail → exhausted → keeper REINJECT; end)
    validate vs INPUT schema           invalid → send orchestrator(Failed); end

  IN  (author override)
    list = author.Process(validatedData, payload)   // List<(result, data, executionId)>
      general exception → send orchestrator(ONE synthetic batch result); end

  POST (forward)
    for each completed item: validate vs OUTPUT def → completed | failed
    for each completed item:
        1) generate entryId
        2) write L2[messageId][slot]=entryId  TTL(rand)      // ALLOCATION FIRST
             L2 fail → exhausted → item=failed +error_message="infra_messageId"
        3) write L2[entryId]=data                            // DATA SECOND
             L2 fail → exhausted → item=failed +error_message="infra_entryId"
    dispatch (per item):
        error_message != infra_*          → send orchestrator(result)
        error_message == "infra_entryId"  → send keeper INJECT (ids, entryId, data, deleteEntryId=source)
        error_message == "infra_messageId" → DROP (no send)
    delete L2[source entryId]                                // happy-path tail
        delete fail → exhausted → keeper DELETE
    end
```
*Allocation-before-data is deliberate:* a crash after the data write but before the index write would orphan unreferenced data; index-first means the worst case is a **skippable dangling pointer**, never a leak.

### Processor — RECOVERY pass (`EXIST L2[messageId]`)
```
else:  // exist
    (existence-check L2 fail → exhausted → keeper REINJECT; end)
    read L2[messageId] → entryIds[]   (L2 fail → exhausted → keeper REINJECT; end)

    create temp list; for each entryId in entryIds[]:
        if exist L2[entryId]:
            L2 op fail → exhausted → temp item = "failed" +error_message="infra_entryId"
            exists     → temp item = "completed"
            not exists → temp item = "failed"
    for each temp item:
        "completed"          → send orchestrator("completed")            // send → retry/throw
                             → write L2[messageId][slot]=guid.empty TTL(rand)   (fail → do nothing)
        "failed" (not-exist) → drop                                      // no send, no retire
        "failed" (infra)     → leave slot intact                        // preserved for REINJECT

    if any temp item "failed" +error_message="infra_entryId":
        send keeper REINJECT             // do NOT delete source — the replay owns the lifecycle
        end round trip
    else:
        delete L2[source entryId]        (delete fail → exhausted → keeper DELETE)
        end round trip
```
*Key invariants:* `guid.empty` retires a slot **only after** its `completed` result is sent (so replays don't re-send completed entries, while infra entries stay re-checkable); **`REINJECT` and the source-delete are mutually exclusive** (a REINJECT replays the whole message, which owns the eventual source delete).

### Keeper — 3 states (forward-only INJECT)
```
REINJECT(ids, entryId, payload):                 // simulate orchestrator send; replay whole message
    read L2[entryId]; if NOT exist → drop
    re-inject reconstructed EntryStepDispatch → PROCESSOR input (carries original payload)

INJECT(ids, entryId, data, deleteEntryId):        // forward-only; data is in-hand
    write L2[entryId] = data
    send → ORCHESTRATOR (StepCompleted)            // per A15
    delete L2[deleteEntryId]

DELETE(entryId):
    delete L2[entryId]; if NOT exist → drop
```
- BIT health gate + global pause/resume fan-out: **unchanged** (A14 / §BIT health gate).
- Keeper processes recovery messages **only when the BIT gate is open**; **gate-closed → do not dequeue-and-drop** (pause consumption / requeue without ack — messages accumulate and drain when the gate opens).
- **Keeper exhaustion policy is configurable:** *DLQ1 mode* → exhausted L2-op/send dead-letters to `skp-dlq-1`; *sustained-outage mode* → hold/requeue and wait for L2 recovery (no dead-letter). Each carries its own residual (DLQ1 may dead-letter recoverable work during an outage; outage mode may spin on a true poison message).

### Invariants
- **`INJECT` is forward-only** — the one place the data is in-hand; recovery never needs data it doesn't hold.
- **`REINJECT`** = replay the whole message; safe because the source `entryId` + payload survive (source delete is the happy-path tail, deferred whenever a REINJECT fires).
- **`guid.empty`** retires a slot only after safe delivery.
- **Accepted silent losses** (by design, dup-tolerant, narrow windows): `infra_messageId` items (allocation never landed → never recovered); crash-window slots (allocated, data never written → recovery finds not-exist → dropped).
