# Phase 52: Three-State Keeper - Context

**Gathered:** 2026-06-11
**Status:** Ready for planning

<domain>
## Phase Boundary

Implement the A18 keeper's three real recovery-state bodies — `REINJECT` / `INJECT` (forward-only) / `DELETE` — plus **gate-closed non-destructive consume** (KEEP-04) and a **configurable exhaustion policy** (KEEP-05), replacing the Model-B 5-state recovery consumer. Requirements **KEEP-01..05** are locked in REQUIREMENTS.md; the keeper section of **A18** (`docs/design/2026-06-08-processor-keeper-recovery-redesign.md` §"Recovery Re-architecture (A18)" → Keeper, lines 205-227) is the literal, LOCKED v5.0.0 spec.

This is a **body-only** phase: the three keeper contracts (`KeeperInject{EntryId,Data,DeleteEntryId}`, `KeeperReinject{EntryId,Payload}`, `KeeperDelete{EntryId}`) and the partition marker (`corr:wf:proc:exec` 4-tuple) are already final from Phase 50. We clarified HOW to implement, not WHAT.

</domain>

<decisions>
## Implementation Decisions

### Exhaustion policy (KEEP-05)
- **D-01:** **Single global enum**, keeper-wide, read at startup — e.g. `Recovery:ExhaustionPolicy ∈ {Dlq1, SustainedOutage}` on `RecoveryOptions`. A18 phrases it as one policy and all three states share the one `keeper-recovery` endpoint, so a single switch is the natural granularity (not per-state).
- **D-02:** **Default = `Dlq1`** when unset. Matches the current v4 behavior, the A4 single-DLQ posture, and the at-least-once/dup-tolerant model; sustained-outage is opt-in. Lowest-surprise default.
- **D-03:** **Sustained-outage = pure hold/requeue, no dead-letter, accepted spin.** Exactly A18 ("hold/requeue and wait for L2 recovery, no dead-letter"). The accepted residual is that a genuine poison op spins while L2 is healthy. **No** bounded-then-DLQ1 backstop (that would reintroduce a DLQ path into the mode meant to avoid one).

### Gate-closed consume (KEEP-04)
- **D-04:** **Pause/resume the `keeper-recovery` receive endpoint**, driven by the BIT loop / `IL2HealthGate`. When the gate closes the endpoint stops consuming (messages accumulate unconsumed on the broker queue); when it opens, consumption resumes and drains. This **retires** the bounded await-in-`Consume` approach and **eliminates the WR-02 landmine** (parked `Consume` vs RabbitMQ `consumer_timeout`) — no parked channels, no hot-spin.
- **D-05:** **KEEP-04 is unconditional.** Gate-closed stays non-destructive in **both** exhaustion modes; the DLQ1-vs-sustained-outage choice (D-01) governs only op/send failures that occur **while the gate is open**. KEEP-04 and KEEP-05 are separate invariants — a closed gate never dead-letters, even in DLQ1 mode.

### REINJECT data-gone (KEEP-01)
- **D-06:** **Absent/empty `L2[entryId]` (no Redis exception) → silent drop / ack.** Adopt A18 literally ("if NOT exist → drop"); **retire `RecoveryDataGoneException` for REINJECT.** Safe under A16 (the data is genuinely gone — end-delete already ran or input never landed — so a replay can't proceed and nothing downstream is lost; A18 lists this under "accepted silent losses"). A Redis **exception** on the read is still infra → `Guard`/`RetryLoop` → exhaustion policy (D-01). DELETE already drops-on-absent (`KeyDeleteAsync` no-ops on a missing key); INJECT has data in-hand and performs no presence read.
- **D-07:** **Emit a log (Information/Warning) + a counter** (e.g. `keeper_reinject_dropped`) on each by-design drop. Observability only — no behavior change — so a drop spike is distinguishable from healthy expected drops.

### Phase-53 boundary
- **D-08:** **Keeper-recovery-endpoint-local scope only.** Phase 52 touches only the `keeper-recovery` endpoint: the policy-conditional retry/dead-letter wiring (DLQ1 keeps `UseMessageRetry`→`skp-dlq-1`; sustained-outage configures no dead-letter + requeue) and the endpoint pause/resume. The **processor-side latch** (Phase-51 keep-latch), the **global A18 `UseMessageRetry=none` rule**, and the **Model-B remnant sweep (RETIRE-03)** all stay in **Phase 53**. Mirrors Phase 51's no-scope-creep discipline.
- **D-09:** **Remove `RecoveryGateTimeoutException` + `RecoveryOptions.GateWaitSeconds` in-phase.** They are obsoleted by D-04's pause/resume mechanism — this phase's own KEEP-04 change, not a Model-B remnant — so removing them keeps the keeper coherent (no dark code). Parallels Phase 51 retiring the WR-01 `finally` in-phase.

### Claude's Discretion
- The exact MassTransit mechanism for endpoint pause/resume (e.g. `HostReceiveEndpointHandle` Stop/Start, control bus, receive-endpoint connector, or `ConcurrentMessageLimit` gating) — researcher/planner to determine against the installed 8.5.5 assembly.
- The exact startup-time conditional endpoint config for the two exhaustion modes (immediate-requeue filter vs no-dead-letter binding vs requeue middleware), given the policy is a startup enum.
- Whether `INJECT`/`DELETE` reuse `RecoveryConsumerBase.Guard`/`RetryLoop` unchanged (likely yes) and how much of the base survives once the bounded gate-wait is removed.
- Metric instrument type/name for the reinject-drop counter (D-07).
- The hermetic-fact decomposition proving each state + gate-closed accumulate/drain + both exhaustion modes (SC-4 / KEEP coverage).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### A18 spec (LOCKED — v5.0.0 source of truth)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` §"Recovery Re-architecture (A18)" → **Keeper, lines 205-227** — the literal 3-state pseudocode (`REINJECT`/`INJECT`/`DELETE`), gate-closed rule (line 220), configurable exhaustion policy (line 221), and invariants (forward-only INJECT, REINJECT=replay, accepted silent losses).
- Same doc, **Global rules lines 141-144** — `_error` disabled / `UseMessageRetry=none` (end-state, deferred to Phase 53 per D-08); send-op throw-on-exhaust; L2-op routed per-site.
- Same doc, **A18 amendment row (line 120)** and **§New L2 vocabulary (136-139)** — `infra_messageId`/`infra_entryId` split, `L2[messageId]` slot array context.

### Requirements
- `.planning/REQUIREMENTS.md` — **KEEP-01..05** (lines 40-44) are the locked acceptance criteria for this phase.

### Prior-phase decisions carried forward
- `.planning/phases/50-contracts-slot-array-l2-key-reshape/50-CONTEXT.md` — contract finality (D-08 `KeeperInject` id-set; D-09 `KeeperReinject`/`KeeperDelete` already match A18), partition marker unchanged (D-03), stub-survivors-await-Phase-52 (D-01/D-02).
- `.planning/phases/51-processor-forward-recovery-pipeline/51-CONTEXT.md` — keep-latch + `UseMessageRetry=none` deferred to Phase 53, recovery re-send mints a fresh `executionId` (D-03), `REINJECT`⊻source-delete invariant.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/Keeper/Recovery/RecoveryConsumerBase.cs` — `Guard`/`Guard<T>` RetryLoop wrapper around L2 ops + Sends, with give-up → `skp-dlq-1` via the inherited `ConsolidatedErrorTransportFilter`. **Keep** the Guard/RetryLoop; the base's bounded gate-wait (its D-03) is **replaced** by endpoint pause/resume (D-04) and `RecoveryGateTimeoutException` is removed (D-09).
- `src/Keeper/Recovery/ReinjectConsumer.cs` — has a v4 body (STRLEN presence check, reconstructs `EntryStepDispatch` to `queue:{ProcessorId:D}` carrying `Payload`). **Revise** the absent-key path from throw-terminal to silent drop + log/metric (D-06/D-07); the reconstruction + send are reusable.
- `src/Keeper/Recovery/InjectConsumer.cs` — **no-op stub**; implement the A18 forward-only body: write `L2[m.EntryId]=m.Data` → send reconstructed `StepCompleted` (A15) → delete `L2[m.DeleteEntryId]`.
- `src/Keeper/Recovery/DeleteConsumer.cs` — already deletes `L2[m.EntryId]` via Guard; `KeyDeleteAsync` naturally drops-on-absent, so KEEP-03 is largely satisfied — verify against A18.
- `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` — **single-owner** endpoint config for `keeper-recovery`: the `UseMessageRetry` latch + the shared `Partitioner` on the 4-tuple. This is where the **policy-conditional retry/dead-letter wiring (D-01..D-03/D-08)** and the **pause/resume wiring (D-04)** land. The other two definitions stay no-op.
- `src/Keeper/Health/{IL2HealthGate,L2HealthGate,BitHealthLoop}.cs` — the BIT gate; `BitHealthLoop` already publishes `PauseAll`/`ResumeAll` to the orchestrator (A14) on health transitions and is the natural driver to also pause/resume the keeper's OWN recovery endpoint (D-04).
- `src/Keeper/RecoveryOptions.cs` — **add** the `ExhaustionPolicy` enum key (D-01); **remove** `GateWaitSeconds` (D-09).
- `Messaging.Contracts.Projections.L2ProjectionKeys.ExecutionData(entryId)` — the `L2[entryId]` key INJECT writes / REINJECT reads / DELETE deletes.

### Established Patterns
- Guard/RetryLoop give-up dead-letters to `skp-dlq-1` (DLQ1 mode = the existing mechanism, kept per D-08).
- STRLEN (not StringGet) for the presence check — absent OR empty both → length 0 (existing ReinjectConsumer IN-04).
- Inner broker `Send`/`ep.Send` uses `CancellationToken.None` ("don't abort a started broker send"); the outer Guard keeps `ct` (existing IN-01 convention).

### Integration Points
- `KeeperQueues.Recovery` ("keeper-recovery") — the single shared endpoint for all three states; pause/resume + policy wiring attach here.
- `BitHealthLoop` ↔ `keeper-recovery` endpoint — **new** coupling: the BIT transition drives endpoint Stop/Start (D-04).
- Orchestrator result queue — INJECT sends a reconstructed `StepCompleted` (A15).
- `queue:{ProcessorId:D}` — REINJECT re-injects the reconstructed `EntryStepDispatch`.

</code_context>

<specifics>
## Specific Ideas

- The A18 §Keeper pseudocode (lines 205-227) is the literal spec for the three bodies — implement to match it verbatim, including the invariants (INJECT forward-only/data-in-hand, REINJECT=replay-whole-message, `guid.empty` retire happens processor-side not keeper-side).

</specifics>

<deferred>
## Deferred Ideas

- **Global `UseMessageRetry=none` rule + processor-side dead-letter latch removal** → Phase 53 (continues the Phase-51 keep-latch deferral).
- **Model-B remnant sweep (RETIRE-03)** → Phase 53.

None — discussion otherwise stayed within phase scope. No pending todos matched this phase.

</deferred>

---

*Phase: 52-three-state-keeper*
*Context gathered: 2026-06-11*
