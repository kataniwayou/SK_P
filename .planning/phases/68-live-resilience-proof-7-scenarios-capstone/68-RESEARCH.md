# Phase 68: Live Resilience Proof — 7 Scenarios (Capstone) - Research

**Researched:** 2026-06-15
**Domain:** Live fault-injection resilience proof (PowerShell sweep over an existing harness) + per-fault-class recovery-semantics risk analysis
**Confidence:** HIGH (all claims verified against in-repo source; the open risks are explicitly flagged as empirical-unknown-until-run, which is the point of the phase)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Uniform fault params across all 5 new rows — `dwellSeconds = 45`, `injectAfterNFires = 4`, `faultType = 'stop-start'` — mirroring the proven TEST-02 recipe exactly. No per-fault-class tuning. 45s spans ≥1 full 30s cron fire while the tier is dead; the remaining ~135s window + the analyzer's 120s drain give recovery room.
  - **D-01a — `targetContainers` per row (compose SERVICE names, whole-tier per Phase 67 D-06):**
    - TEST-03 → `@('orchestrator')` (single `sk-orchestrator`; re-hydrates Quartz crons from the L2 parent index on restart)
    - TEST-04 → `@('keeper')` (BOTH replicas — total liveness blackout during dwell; the hardest keeper proof, see Note 1)
    - TEST-05 → `@('redis')` (single `sk-redis`)
    - TEST-06 → `@('rabbitmq')` (single `sk-rabbitmq`)
    - TEST-07 → `@('redis','rabbitmq')` (combined; both stopped, dwell, both started)
  - **D-01b — Planner-verify:** 45s/N=4 is proven ONLY for the stateless processor tier. A non-PASS on any STATEFUL tier (orchestrator re-hydration, keeper takeover, redis/rabbitmq reconnect + redelivery) is a **real finding to investigate first** — NEVER an auto-bump of the dwell. Tuning a single row's dwell upward is a permitted deviation-with-rationale ONLY if a verdict FAIL is traced to insufficient recovery time, never a blind retry.

- **D-02:** Thin wrapper script `scripts/phase-68-*.ps1` (close-script-family sibling) loops `pwsh -File scripts/phase-67-harness.ps1 -ScenarioId <id>` over all 7 ids in numeric order (TEST-01 → TEST-07), runs **every** scenario even if an earlier one fails (no fail-fast). Wrapper final exit is non-zero if ANY scenario is non-PASS; zero only when all 7 PASS. No harness changes beyond the 5 data rows. Wrapper only loops + records.

- **D-03:** Roll-up summary + the 7 per-scenario JSONs. Keep the existing `analyzer-reports/{scenarioId}.json` (7 of them) AND emit one capstone summary (7-row table: scenarioId · verdict · zero-missing · effect-once · trigger/complete counts · harness exit code). Summary is derived from the 7 JSON reports + each harness exit code — it adds no new scoring. Exact format + path = Claude's discretion (default: a `phase-68` summary `.json` + a human-readable `.md`/console table in close-script style).

- **D-04:** Re-run allowed on INFRA-ABORT only (distinct exit codes 10/20/25/30/40/50/60/70 / bad-arg 64); NEVER on a verdict FAIL (exit 1). A verdict FAIL is a real finding to investigate, never retried away. No auto-retry in the runner (operator re-invokes the single failed scenario). Wrapper job: run + record + distinguish infra-abort vs verdict-FAIL in the roll-up, not mask flake.

### Claude's Discretion
- Exact roll-up summary file format + path + console rendering (D-03) — default `phase-68` close-script style (JSON + human table).
- Wrapper internals: how it captures each harness exit code, how it tags infra-abort vs verdict-FAIL rows, where its console log lands (D-02/D-04).
- Whether the wrapper accepts an optional id-subset arg vs always all-7 — default all-7; single-id re-run is just the bare harness.

### Deferred Ideas (OUT OF SCOPE)
None. Per-fault-class dwell tuning was considered and **rejected** in favor of the uniform proven recipe (D-01); returns only as a permitted deviation-with-rationale if a verdict FAIL is traced to insufficient recovery time (D-01b). Bounded auto-retry in the runner was considered and **rejected** in favor of operator-initiated re-runs on infra-abort only (D-04).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| TEST-01 | Happy path — no fault; baseline zero-missing + effect-once | Already PASS in Phase 67 (reference run, 67-03). Row already in the harness table (`faultType='none'`, N=0, dwell=0). The sweep re-proves it as the baseline-first run (Phase 67 D-10). |
| TEST-02 | Processor crash — recovery proven | Already PASS in Phase 67 (10/10 started complete, zero missing, zero dup). Row already in the table. Recovery path = the slot-array RECOVERY pass + broker nack-requeue redelivery (see §Recovery Semantics). The richest classifier-exercising proof and the only timing proven live. |
| TEST-03 | Orchestrator crash — recovery proven | NEW row. Recovery = `HydrationBackgroundService.SMEMBERS(parent-index)` re-schedules crons on boot; in-flight `orchestrator-result` queue messages redeliver. Risk: hydration-gated readiness + RAMJobStore loss. See §Recovery Semantics (a). |
| TEST-04 | Keeper crash (BOTH replicas) — recovery proven | NEW row. Total liveness blackout for 45s. Recovery = on restart the BIT loop's first healthy tick re-opens the gate + ResumeAll; keeper-recovery queue messages accumulate non-destructively while down. Risk: whole-tier blackout vs single-replica-failover envelope (Note 1). See §Recovery Semantics (b). |
| TEST-05 | Redis crash — recovery proven | NEW row. Redis backs L1 projections, L2 slot-array + execution-data, processor liveness, AND the keeper BIT probe. The most cross-cutting fault. See §Recovery Semantics (c). |
| TEST-06 | RabbitMQ crash — recovery proven | NEW row. Broker down → all sends exhaust → propagate → no `_error` (A18 end-state) → nack-requeue redelivery on return. See §Recovery Semantics (d). |
| TEST-07 | Redis + RabbitMQ combined crash — recovery proven | NEW row. Superset of (c)+(d); both state + transport gone simultaneously. See §Recovery Semantics (e). |
</phase_requirements>

## Summary

Phase 68 is genuinely "just data + a thin sweep wrapper." The harness, seeder, reset, analyzer, fault-injection mechanism, and PASS/FAIL engine are all complete and proven live (Phases 65/66/67). The mechanical work — 5 hashtable rows + a loop script + a roll-up summary — is low-risk and follows established patterns verbatim.

**The real value of this research is the per-fault-class recovery-semantics risk map.** The uniform 45s-dwell / N=4 recipe is proven only for the *stateless processor* tier (TEST-02, 10/10 PASS). For the five new rows, every fault class has a *different* recovery path through the codebase, and three of them (orchestrator, keeper-whole-tier, redis) touch state that the proven recipe never exercised. This document traces each recovery path to its source and predicts where 45s/N=4 is likely sufficient versus where a verdict FAIL would be a *real finding* (not a flake) per D-01b.

**Validation here is inherently empirical:** there is no new product code to unit-test. The proof IS the 7 live harness runs, each scored PASS/FAIL by the Phase 66 analyzer from Prometheus + Elasticsearch, with ES-primary completeness as the binding arbiter (counter-reset-immune — essential because every fault row resets a tier's Prom counters mid-window).

**Primary recommendation:** Add the 5 rows verbatim per D-01. Write the wrapper as a `phase-68-sweep.ps1` close-script sibling that captures `$LASTEXITCODE` per run, classifies it via the Phase 67 exit-code table, reads the 7 `analyzer-reports/{id}.json`, and emits a JSON+console roll-up. **Resolve the IN-04 stale comment** (rename or alias `Analyze_HappyPath_Window_Yields_Pass` to a verdict-neutral name and keep the harness `--filter-method` in sync) — but verify FIRST (below) that no rename is functionally required, only cosmetic. Treat any stateful-tier verdict FAIL as a finding to surface, never an auto-retry.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Add 5 scenario rows | Harness data seam (`$Scenarios` hashtable, phase-67-harness.ps1 ~L87) | — | Phase 67 D-12 made the table the "just data" seam; rows are pure config |
| Drive 7 runs + collect verdicts | New `scripts/phase-68-*.ps1` (PowerShell ops tier) | phase-67-harness.ps1 (process invocation) | The sweep is orchestration over the harness; matches the close-script family |
| Score each run PASS/FAIL | Phase 66 analyzer fixture (test tier) | PassFailEngine (pure) | Single source of truth; the sweep never re-scores (D-03) |
| Roll-up aggregation | New phase-68 wrapper (reads the 7 JSONs) | — | Pure read + tabulate; adds no scoring (D-03) |
| Fault injection / recovery | **Product runtime (keeper / orchestrator / processor / redis / rabbitmq)** | — | NOT touched by this phase — it is *proven*, not built (v8.0.0 scope discipline) |
| Verdict-name reconciliation | Test fixture method name (`AnalyzerE2ETests.cs`) | harness `--filter-method` literal | Cosmetic test-code rename only; keep the two in sync (IN-04) |

## Recovery Semantics — Per-Fault-Class Risk Analysis (the core of this research)

> **How "effect-once" actually works in this codebase (load-bearing, verified).** There is **no dedup-on-write idempotency key** for step effects. The orchestrator's `TypedResultConsumer` (`src/Orchestrator/Consumers/TypedResultConsumer.cs`) reads L1 only, has **no dedup, no manifest, no Redis read** on the result path, and advances the DAG once per consumed result. Effect-once is delivered by the *processor-side slot-array recovery pass* plus *broker redelivery semantics*:
> - On a redelivery, the processor's `ProcessorPipeline.RunAsync` (`src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:85`) branches on `EXISTS L2[messageId]`. A redelivered dispatch finds the slot-array present → runs `RunRecoveryAsync` (`:121`), which HGETALLs the slots, re-sends any `completed` result *before* retiring the slot (SLOT-03 send-before-retire), then deletes the source. A retired (`Guid.Empty`) slot is inert.
> - The "effect-once at the ES-log level" guarantee therefore rests on: (1) the slot-retire making a second recovery pass a no-op, and (2) `SampleProcessor` emitting its `Step_<label>` log once per actual execution. A redelivery that *re-executes* `ProcessAsync` (forward pass, because the slot array was lost/expired) would emit a **second `Step_*` log for the same correlationId+label → a DUPLICATE → analyzer FAIL** (fail-closed, binding; `PassFailEngine.cs:107-110`). This is the precise mechanism every fault row stresses, and it is where redis state-loss (TEST-05/07) is most dangerous.
> - MassTransit redelivery is **broker nack-requeue** (A18 end-state, Phase 53 D-01): `UseMessageRetry=none`, no `_error` dead-letter on the dispatch/result/keeper endpoints. A send that exhausts its in-code `RetryLoop` PROPAGATES (throws) → RabbitMQ nack-requeue. So "message-level redelivery during the fault is *reported, not failed*" maps directly to "redelivered messages re-run through the EXISTS-L2 idempotent recovery pass." Confirmed: `ProcessorPipeline.SendResult/SendKeeper` `:330/:356`.

### (a) TEST-03 — Orchestrator crash (single `sk-orchestrator`)

**Recovery path (verified):** On `docker compose start`, the orchestrator boots and `HydrationBackgroundService.ExecuteAsync` (`src/Orchestrator/Hydration/HydrationBackgroundService.cs:33`) does `SMEMBERS` of the L2 parent index, calls `lifecycle.HydrateAndScheduleAsync` for each workflow (re-registering the Quartz cron into the in-process RAMJobStore), then `gate.MarkReady()` flips `/health/ready` healthy. The harness STEP F.4 health-wait gates on exactly that healthy signal (bounded 90s).

**What's in-flight during the 45s blackout:**
- The Quartz RAMJobStore is lost on stop (in-process, non-persistent) → re-built from the L2 parent index on restart. The seeded `v8-fanout-proof` workflow IS in the parent index (it was seeded into L2 by Phase 65's seeder), so it re-schedules. **Cron fires that would have happened during the 45s are simply lost (no catch-up herd)** — Quartz reschedules fresh-from-now (the ResumeAll path documents "skip-to-next, no immediate refire"; a cold boot schedules from now). These lost fires are **never-started runs**, invisible to ES (no Step_A) → they surface only as a *non-fatal Prom corroboration WARNING* (impliedRuns > startedRuns), NOT a verdict FAIL (`PassFailEngine.cs:136-143`). **This is the key reason a 45s orchestrator outage should still PASS:** a run that never dispatched is not "missing," it is "never started."
- Results in-flight on the `orchestrator-result` queue when the orchestrator dies: these accumulate on the broker (durable competing-consumer queue) and redeliver when the orchestrator's result consumer re-binds. On redelivery `TypedResultConsumer` does an L1 lookup — **and here is a subtle risk:** a result that arrives for a step whose L1 entry was re-hydrated cleanly will advance normally; but if a run was mid-traversal when the orchestrator died, its continuation chain pauses until restart, then resumes. The analyzer's 120s drain (`DrainMs=60s + PollToStableBudgetMs=60s`) plus ~135s residual window is the recovery budget.

**Risk verdict:** **MEDIUM-LOW.** Likely PASS. The orchestrator is explicitly designed for crash-restart re-hydration (the harness's own STEP B1 clean-restart relies on it). The dominant effect is *lost fires* (corroboration warning, non-fatal), not missing/duplicate completions. **Watch for:** a started-but-incomplete run (a correlationId that logged Step_A..Step_C but whose continuation result was on the queue and got mis-advanced on redelivery) — that would be a real MISSING finding. Also watch the bounded 90s health-wait: orchestrator `start_period:30s` + hydration could push first-healthy close to the limit on a cold machine (infra-abort exit 60, re-runnable per D-04).

### (b) TEST-04 — Keeper crash, BOTH replicas (whole-tier liveness blackout)

**Recovery path (verified):** The keeper tier (`deploy.replicas:2`, no `container_name`) runs the `BitHealthLoop` (`src/Keeper/Health/BitHealthLoop.cs`) on each replica — an edge-triggered L2 probe that on healthy→unhealthy publishes `PauseAll` + Stops the keeper-recovery receive endpoint, and on unhealthy→healthy publishes `ResumeAll` + Starts it. The keeper-recovery queue (`keeper-fault-recovery`) is a durable shared queue; while both replicas are down, `KeeperReinject`/`Inject`/`Delete` messages **accumulate non-destructively on the broker** (`BitHealthLoop` comment: "gate-closed never dequeue-and-drops"). On restart, the first healthy BIT tick re-opens the gate, Starts the endpoint, and broadcasts ResumeAll — and the accumulated recovery messages drain.

**What's in-flight during the 45s blackout:** The keeper only acts on *recovery* messages (REINJECT/INJECT/DELETE), which are only produced when a processor's L2 op or send *exhausts its RetryLoop* (an infra fault). On the happy fan-out path with healthy redis+rabbitmq, **the keeper sees little or no traffic** — the processor completes forward passes directly to the orchestrator without involving the keeper. So a 45s keeper blackout, *with redis and rabbitmq both healthy*, likely perturbs nothing observable: no recovery messages are being generated, so none accumulate, so the blackout is a no-op for the proof cohort.

**Risk verdict:** **LOW for correctness, but this is the Note-1 design question.** The most likely outcome is a clean PASS *precisely because the keeper is idle on the happy path*. **The genuine finding to watch (Note 1):** the CONTEXT flags whether the system is designed only to survive *single-replica* keeper loss (failover) vs a *total blackout*. The BIT loop is per-replica and edge-triggered; a total blackout means NO replica is probing L2 for 45s. On restart, the first tick is treated as a transition (`prevHealthy=null`) and re-asserts a healthy/open posture (idempotent ResumeAll). **There is no cross-replica leader election or shared-blackout detection** — each replica independently re-asserts. This *should* recover, but if planning/execution finds the blackout leaves the recovery endpoint Stopped or the gate Closed after restart (e.g. a replica boots, probes L2-down because redis is fine but the probe races, and never re-opens), that is a **real finding to surface to the spec owner, NOT silently weakened to single-replica** (Note 1). **Watch:** keeper `/health/ready` after restart (the harness health-wait gates on it); and whether `keeper_reinject_dropped_total` moved (a Prom corroboration signal).

### (c) TEST-05 — Redis crash (single `sk-redis`) — the most cross-cutting fault

**Redis backs (verified across the codebase):**
- **L2 slot array + execution data** (`L2ProjectionKeys.MessageIndex` / `.ExecutionData`) — the heart of effect-once. The processor reads/writes these on every step.
- **L1 projections** (orchestrator hydration source; `HydrationBackgroundService` reads the parent index from redis).
- **Processor liveness** (`ProcessorLivenessHeartbeat` writes `skp:proc:{id}:{instance}` every interval; TTL=30s).
- **Keeper BIT probe** (`L2ProbeRecovery.ProbeOnceAsync` — a redis-down probe returns `false` → keeper goes unhealthy → PauseAll).

**What a 45s redis outage does:**
- **Processor in-flight:** every L2 op is wrapped in `RetryLoop` (`Retry:Limit` attempts). A redis-down read/write **exhausts the RetryLoop → routes to KeeperReinject/Inject/Delete** (the design's whole point: redis fault → keeper recovery). The send to the keeper queue is itself a broker op (rabbitmq is healthy in TEST-05), so it succeeds. So in-flight processor work during a redis outage is *handed to the keeper for replay*, not lost.
- **The dangerous interaction (effect-once):** if a forward-pass dispatch's `EXISTS L2[messageId]` check exhausts during the redis outage, it REINJECTs (input intact, no source delete). When redis returns, the keeper's `ReinjectConsumer` (`src/Keeper/Recovery/ReinjectConsumer.cs`) reads `STRLEN L2[entryId]` — if present, re-dispatches a reconstructed `EntryStepDispatch` to `queue:{ProcessorId}`. That re-dispatch hits the processor's EXISTS-L2 branch again. **If the slot array's random TTL (`SlotArrayTtlMin/MaxSeconds`) expired during the outage**, the redelivered/reinjected dispatch sees `EXISTS==false` → runs the **FORWARD pass again → re-executes `ProcessAsync` → emits a SECOND `Step_*` log → DUPLICATE → FAIL.** This is the single highest-probability source of a *real* verdict FAIL in the whole sweep.
- **`Processor__ExecutionDataTtl: "5"`** (compose `:285`) — execution data keys self-expire in **5 seconds** (a deliberately short TTL for the close-gate net-zero, carried over). A 45s redis outage **vastly exceeds** this 5s TTL. When redis returns, any execution-data key written before the outage is gone (expired during downtime OR the write never landed). The keeper REINJECT's `STRLEN L2[entryId]==0` → **by-design silent DROP** (`ReinjectConsumer.cs:37-41`, increments `keeper_reinject_dropped_total`). A dropped reinject = a run that does NOT complete = potentially a **MISSING run → FAIL** if that correlationId had already started (logged Step_A).
- **Liveness expiry:** processor liveness TTL=30s. A 45s redis outage means the liveness key is absent when redis returns until the next heartbeat re-writes it. This does NOT block the cohort (the workflow is already activated; `orchestration/start` is validation-only and not re-called mid-window). It only matters if a NEW `POST /start` happened mid-window — it doesn't.

**Risk verdict:** **HIGH — this is the row most likely to produce a verdict FAIL, and that FAIL may be a real finding OR an artifact of the 5s ExecutionDataTtl interacting with the 45s dwell.** Per D-01b, do NOT auto-bump the dwell. **If TEST-05 FAILs with duplicates or missing runs, the investigation must distinguish:** (i) a genuine effect-once defect (real finding, surface it), vs (ii) the 5s `ExecutionDataTtl` being unrealistically short relative to a 45s outage such that legitimately-replayable data expired (a test-environment artifact — and a permitted deviation-with-rationale candidate, e.g. a longer TTL for this row, OR documenting that a 45s redis outage exceeds the data-retention envelope by design). **This is the planner's #1 watch item.**

### (d) TEST-06 — RabbitMQ crash (single `sk-rabbitmq`)

**Recovery path (verified):** Broker down → every `ep.Send(...)` in the processor and orchestrator exhausts its in-code `RetryLoop` and **propagates (throws)**. With `UseMessageRetry=none` and no `_error` pipeline (A18 end-state, Phase 53 D-01), the broker default is **nack-requeue** — the message returns to its queue and **redelivers when the broker is back**. Orchestrator/keeper/processor are HARD-on-broker (`/health/ready` gated on `BusReadyHealthCheck`), so they go *unhealthy* during the outage and recover when MassTransit reconnects (auto-reconnect is MassTransit default).

**What's in-flight during the 45s blackout:**
- The cron itself fires inside the orchestrator (no broker needed to fire), but the resulting dispatch *send* fails → nack-requeue / redelivery on return. The 6-field cron `*/30` keeps firing; fires during the outage either fail-to-send (redelivered) or never reach a consumer.
- On reconnect, redelivered dispatches hit the processor's EXISTS-L2 idempotent recovery pass — **and here redis is HEALTHY** (TEST-06 only kills rabbitmq), so the slot array survives the 45s (subject to the 5s ExecutionDataTtl / slot-array TTL — the same TTL-vs-dwell hazard as (c), but WITHOUT redis being down, so writes that landed before the outage are still subject to their own TTL expiry).

**Risk verdict:** **MEDIUM.** Redelivery is the designed-for path and TEST-02 (processor crash) already proved redelivery + recovery PASSes. The new wrinkle vs TEST-02 is that *every* tier loses the broker simultaneously (not just the processor), so the orchestrator's result-advancement also stalls. **Watch:** started-but-incomplete runs whose continuation results sat un-redelivered past the drain window (MISSING → FAIL); and the same 5s-ExecutionDataTtl-vs-45s-dwell expiry hazard for any execution-data key minted just before the outage.

### (e) TEST-07 — Redis + RabbitMQ combined crash

**This is the superset of (c) and (d) with the worst interaction:** state (redis) AND transport (rabbitmq) are both gone for 45s. The processor cannot read/write L2 (RetryLoop exhausts) AND cannot send to the keeper queue (broker down) — so the `SendKeeper` path *also* exhausts and propagates → nack-requeue. On recovery, messages redeliver into a redis whose 5s-TTL execution-data is long expired. **The REINJECT-with-missing-data silent-drop path (`ReinjectConsumer.cs:37`) is most likely to fire here**, producing dropped runs.

**Risk verdict:** **HIGH — the hardest scenario, most likely to FAIL.** If TEST-05 and TEST-06 both PASS individually, TEST-07 likely PASSes too (the system is resilient to each independently). If TEST-05 reveals the TTL-vs-dwell artifact, TEST-07 will reveal it more severely. **Same D-01b discipline:** a FAIL here is a finding to investigate (real defect vs TTL-artifact), never an auto-retry. **Watch:** `keeper_reinject_dropped_total` delta (the silent-drop signal) and missing-run count.

### Cross-cutting risk: the 5-second `ExecutionDataTtl` vs the 45-second dwell

**Flagged as ASSUMED-impactful (A1).** `Processor__ExecutionDataTtl: "5"` (compose `:285`) was set short for the *retired* triple-SHA close-gate net-zero sweep (so `skp:data:*` keys self-expire before the AFTER snapshot). That close-gate is **explicitly dropped for v8.0.0** (CONTEXT Out-of-scope; REQUIREMENTS Out-of-scope). A 5s data-key TTL means **any inter-step execution data minted >5s before a fault recovery is gone** — fine on the happy path (steps traverse in <5s) but adversarial against a 45s outage. This is the most likely root cause of a stateful-tier FAIL that is an *artifact*, not a defect. **The planner should pre-stage this as a known investigation branch** (not a pre-emptive change — D-01 locks the uniform recipe and forbids touching product/compose config speculatively; but it should be the first hypothesis if TEST-05/06/07 FAIL with drops/missing). Whether raising this TTL is in-scope is a **spec-owner question** (it is compose config, arguably "just data," but it touches runtime behavior, not the scenario table).

## Standard Stack

No new libraries. Everything is in-repo and proven.

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| PowerShell (pwsh) | 7.x (repo convention) | The sweep wrapper + the harness | Every infra op in this repo is PowerShell (18 close-scripts + phase-65/67 family) |
| `scripts/phase-67-harness.ps1` | current | Single-scenario end-to-end driver | The artifact being extended (5 rows) + driven (7×) |
| `scripts/phase-65-up.ps1` / `phase-65-reset.ps1` | current | Per-run bring-up + reset (shelled by the harness) | Already proven; the sweep does not call them directly |
| `AnalyzerE2ETests.cs` (Phase 66 fixture) | current | The verdict source (`dotnet test` MTP filter) | Single source of truth for PASS/FAIL; env-seam already wired (D-16) |
| `docker compose` v2 | current | Fault ops (`stop`/`start`), bring-up, teardown | Compose service names are the fault targets |

**No installation needed.** The only authored artifacts are: 5 hashtable rows, one new `scripts/phase-68-*.ps1`, and the roll-up summary file(s).

## Architecture Patterns

### System / Sweep Flow Diagram

```
phase-68-sweep.ps1  (NEW — the only new control flow)
   │
   │  for each id in [TEST-01, TEST-02, TEST-03, TEST-04, TEST-05, TEST-06, TEST-07]   (numeric order, D-02)
   │     │
   │     ├─► pwsh -File scripts/phase-67-harness.ps1 -ScenarioId <id>      (verbatim, unchanged)
   │     │       │  A0 build → A up → B reset → B1 clean-orch → C seed → D wf-id
   │     │       │  → E 204 gate → F observe(N=4)+crash(45s dwell)+recover → H analyze → Z down
   │     │       └─► exit code  (0=PASS | 1=verdict FAIL | 10..70=infra-abort | 64=bad-arg)
   │     │
   │     ├─► capture $LASTEXITCODE  →  classify (PASS / verdict-FAIL / infra-abort)   (D-04)
   │     └─► read analyzer-reports/<id>.json  (startedRuns, completeRuns, missing, duplicates, verdict)
   │
   ├─► aggregate 7 rows  →  roll-up.json  +  console/.md table   (D-03; no new scoring)
   └─► exit  0 iff all 7 PASS, else non-zero   (D-02)
```

Note: each harness run does its OWN `docker compose down` at STEP Z, and the NEXT run does its own A0 build + A up + B reset. So the 7 runs are **fully self-isolating** — the sweep wrapper holds NO stack state between runs (see §Per-run Isolation).

### Pattern 1: Capture + classify the harness exit code (PowerShell)

```powershell
# Run one scenario; capture its exit code without aborting the sweep (no fail-fast, D-02).
& pwsh -File (Join-Path $PSScriptRoot 'phase-67-harness.ps1') -ScenarioId $id
$code = $LASTEXITCODE

$class = switch ($code) {
    0       { 'PASS' }
    1       { 'VERDICT_FAIL' }        # real finding — NEVER retried (D-04)
    64      { 'BAD_ARG' }             # config-usage error
    default { 'INFRA_ABORT' }         # 10/20/25/30/40/50/60/70 — operator re-runnable (D-04)
}
```
Because `$ErrorActionPreference='Stop'` is set inside the harness (not the wrapper), and the harness is invoked as a separate `pwsh -File` process, a harness `exit N` surfaces as `$LASTEXITCODE` in the wrapper without terminating the wrapper's loop. Verified against the harness's `exit $analyzerExit` (`phase-67-harness.ps1:368`).

### Pattern 2: Read the per-scenario analyzer report for the roll-up

```powershell
# The harness writes analyzer-reports/{id}.json under tests/BaseApi.Tests/bin/** (Release).
# Mirror the harness's own discovery (phase-67-harness.ps1:353-356).
$report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$id.json" `
            -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
$json = if ($report) { Get-Content $report.FullName -Raw | ConvertFrom-Json } else { $null }
# Fields available (AnalyzerReport): Verdict, StartedRuns, CompleteRuns, Missing, Duplicates,
#   TriggerCount, PromImpliedRuns, Reconciliation, CorroborationDetail.
```
The roll-up's zero-missing column = `($json.Missing -eq 0)`; effect-once column = `($json.Duplicates.Count -eq 0)`. The verdict is `$json.Verdict` (and must equal PASS iff the harness exit was 0 — cross-check them; a mismatch is itself a finding).

### Pattern 3: Roll-up summary (close-script style, D-03)

A `phase-68-summary.json` (machine) + a console/`.md` 7-row table (human), in the `[phase-68-sweep]`-prefixed `Write-Host` style of the close-script family. Columns per D-03: `scenarioId · verdict · zeroMissing · effectOnce · startedRuns · completeRuns · harnessExit · class`. Path: `analyzer-reports/phase-68-summary.json` (sibling to the per-scenario reports) or a repo-root `phase-68-*.txt` mirroring the existing `psql-*.txt` artifacts — Claude's discretion (D-03).

### Anti-Patterns to Avoid
- **Re-scoring in the wrapper.** The wrapper reads + tabulates the Phase 66 verdict; it NEVER recomputes missing/duplicate (D-03). The analyzer is the single source of truth.
- **Auto-retry on FAIL.** Forbidden (D-04). A verdict FAIL (exit 1) is a finding. Only infra-abort codes (10-70) are operator-re-runnable, and only by re-invoking the bare harness for that one id — never inside the wrapper loop.
- **Touching the harness machinery.** Only the `$Scenarios` table grows (5 rows). No new STEP, no flow change. (Phase 67 D-12 / 68 scope discipline.)
- **Treating a Prom counter discontinuity as a fault.** Every fault row resets a tier's Prom counters mid-window. ES-primary completeness is the binding arbiter; the Prom delta is corroboration only (`PassFailEngine.cs:112-143`). The wrapper must NOT read Prom deltas at all.
- **Bumping the dwell to make a stateful row pass.** Forbidden as a blind retry (D-01b). A FAIL is investigated first.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Single-scenario end-to-end run | A new runner | `phase-67-harness.ps1 -ScenarioId <id>` | Complete + live-proven (67-03); the sweep only loops it |
| PASS/FAIL scoring | Any new scoring in the wrapper | The Phase 66 analyzer verdict (exit code + JSON) | D-03: single source of truth, no new scoring |
| Per-run clean state | A custom reset | The harness's own STEP B (`phase-65-reset.ps1`) per run | Already wired; the sweep gets it for free |
| Fault injection | A custom docker stop/start sequencer | The harness STEP F.3 crash sequencer | Whole-tier stop/dwell/start already proven for processor |
| Exit-code classification | A custom verdict taxonomy | The Phase 67 exit-code table (0/1/10-70/64) | Already designed to separate infra-abort from verdict-FAIL (D-04) |
| NDJSON health parsing | A new parser | The harness STEP F.4 per-replica health-wait | The sweep never health-waits; the harness owns it |

**Key insight:** the wrapper's entire job is `loop + capture exit code + read 7 JSONs + tabulate`. Any logic beyond that is re-implementing something Phases 65/66/67 already proved — a scope violation.

## The IN-04 / "Yields_Pass" Reconciliation (Planner-Verify — RESOLVED here)

**The question (from CONTEXT canonical_refs + IN-04 comment):** the harness hardcodes `--filter-method "*Analyze_HappyPath_Window_Yields_Pass*"` for EVERY scenario, and the IN-04 comment says "for a fault scenario a FAIL verdict is the EXPECTED outcome." That wording is **stale for this capstone** — a *recovered* fault run MUST assert PASS.

**Resolution (verified by reading `AnalyzerE2ETests.cs:84-198` + `PassFailEngine.cs`):**

1. **The fixture's assertion is verdict-correct for all 7 scenarios as-is.** The single assertion is `Assert.True(report.Verdict == Verdict.Pass, report.HumanSummary)` (`:197`). The verdict is computed purely from the live telemetry: `pass = missing == 0 && !dupFail` (`PassFailEngine.cs:161`). So:
   - A **recovered** fault run (zero-missing + effect-once) → `Verdict.Pass` → **assertion passes → harness exit 0** = exactly what the capstone wants.
   - A **non-recovered** fault run (missing or duplicate) → `Verdict.Fail` → assertion fails → harness exit 1 = the legitimate verdict-FAIL (a real finding).
   - **No rename is functionally required.** The method name `Analyze_HappyPath_Window_Yields_Pass` is *misleading* (it is no longer only a happy-path assertion — it now asserts PASS for recovered fault runs too), but the BEHAVIOR is correct. The `--filter-method` wiring works identically.

2. **The fixture does NOT mis-score a fault run.** Specifically it does NOT treat message-level redelivery as a duplicate-effect FAIL *at the metric level*: the dedupe counters (`orchestrator_result_deduped_total`, `processor_dispatch_deduped_total`) are DORMANT (no increment site) and map to `null`/Absent, feeding **no** arithmetic (`AnalyzerE2ETests.cs:364-366`). The duplicate FAIL is **ES-log-based only**: a duplicate `(correlationId, StepLabel)` in the Step_* hits (`PassFailEngine.cs:107-110`). So "redelivery reported, not failed" holds **as long as redelivery does not cause the processor to emit a second `Step_*` log for the same correlationId+label** — which is the effect-once guarantee analyzed in §Recovery Semantics. The classifier is correct; the *system under test* is what's being proven.

3. **Recommendation for the planner:** rename `Analyze_HappyPath_Window_Yields_Pass` → a verdict-neutral name (e.g. `Analyze_Window_Yields_Pass` or `Analyze_Scenario_Window_Yields_Pass`) **AND** update the harness `--filter-method` literal in the SAME change (`phase-67-harness.ps1:346`) + the IN-04 comment (`:340-345`) to drop the stale "FAIL is expected" wording. This is a **cosmetic test-code rename + a literal sync + a comment fix** — the ONLY code touch this phase permits beyond PowerShell+data (CONTEXT code_context). It is OPTIONAL for correctness (the wiring already works) but RECOMMENDED to remove the misleading "HappyPath"/"FAIL expected" signals now that all 7 must assert PASS. **If the planner chooses to skip the rename, that is acceptable** — but the IN-04 comment MUST at minimum be corrected, because its current text ("for a fault scenario a FAIL verdict is the EXPECTED outcome") is now actively wrong for the capstone and would mislead a future reader into thinking a TEST-05 FAIL is normal.

## Per-Run Isolation Across 7 Sequential Runs (Planner-Verify — CONFIRMED clean)

**The concern:** does running 7 in a row (vs the 2 reference runs in Phase 67) break isolation?

**Verified clean on all axes:**
1. **Prom counter accumulation across runs is a NON-issue.** The analyzer is ES-primary; the binding verdict is computed from ES Step_* hits bounded to the recorded `[WINDOW_START_UTC, WINDOW_END_UTC]` window (`AnalyzerE2ETests.cs:146,208-222`). Prom counters are monotonic-cumulative across the long-lived stack, but the analyzer reads them as **windowed deltas pinned to the recorded window bounds** (`:156-158`, instant-query at windowStart and windowEnd) — so accumulation across the prior 6 runs does NOT inflate this run's delta. And even if it did, the delta is **corroboration-only, non-binding** (`PassFailEngine.cs:112-143`). The harness's own `Get-FireCount` baseline is also a per-run subtraction (`fireBaseline`, `:236`). The `[long]` cast (IN-02) already defends against Int32 overflow on a long-lived counter. **Confirmed: 7 sequential runs do not perturb scoring via Prom accumulation.**
2. **ES window isolation.** Each run records a fresh `windowStart`/`windowEnd` and the analyzer filters Step_* hits to that exact range. Prior runs' Step_* docs are outside the window and excluded. The per-run `phase-65-reset` (FLUSHALL + row-scoped DB reset) + STEP B1 clean-orchestrator restart guarantees only the freshly-seeded workflow fires in *this* run's window (the harness header's whole rationale).
3. **Per-run stack lifecycle.** Each harness invocation does A0 build → A `up --force-recreate` → ... → Z `down`. So between sweep iterations the stack is fully torn down and rebuilt — there is NO cross-run container state leak. (This is *heavier* than Phase 67's "stack stays up between the 2 reference runs via reset" model: in Phase 67 the *harness* was invoked twice but the between-run reset kept the stack up; in the Phase 68 sweep each `pwsh -File phase-67-harness.ps1` invocation is a full down/up cycle because STEP Z down + next STEP A up bracket every run. This is **slower but maximally isolating** — see Wall-Clock below.)
4. **Redis/DB state.** FLUSHALL + FK-safe row-scoped reset + processor-set assertion run per-run inside the harness STEP B. The 5s `ExecutionDataTtl` further self-expires any straggler data keys. No accumulation.

**Net:** 7-in-a-row is isolation-safe. The ES-primary design is specifically what makes it safe (CONTEXT code_context "ES-primary arbiter, Prom corroborating").

## Validation Architecture

> Nyquist validation is enabled. For Phase 68 the validation is **inherently empirical** — there is no new product code to unit-test (CONTEXT scope discipline: no new product code, no new recovery logic). The proof IS the 7 live harness runs. Hermetic/unit coverage is **N/A** for this phase's deliverables (the wrapper is thin ops glue; the scoring it consumes is already covered by Phase 66's `PassFailEngineFacts.cs` hermetic suite). The "test framework" for the *capstone proof* is the live sweep itself.

### Test Framework (for the live proof)
| Property | Value |
|----------|-------|
| Framework | The 7-scenario live sweep (`phase-68-*.ps1`) driving the Phase 66 analyzer (`AnalyzerE2ETests.cs`, xunit.v3 / MTP) per run |
| Config file | none — the harness shells `dotnet test ... -- --filter-method` (MTP-native filter; VSTest `--filter` is silently ignored — MEMORY: MTP filter syntax) |
| Quick run command | `pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-03` (one scenario, ~10-12 min) |
| Full suite command | `pwsh -File scripts/phase-68-*.ps1` (all 7, ~70-85 min — see Wall-Clock) |

### Phase Requirements → Proof Map (what each scenario SAMPLES)
| Req | Fault class sampled | Recovery path proven | Binding measure | Pre-staged risk |
|-----|--------------------|--------------------|-----------------|-----------------|
| TEST-01 | none (baseline) | clean pipeline | zero-missing + effect-once | none (already PASS) |
| TEST-02 | stateless worker crash | broker redelivery → slot-array recovery pass | zero-missing + effect-once | none (already PASS, proven timing) |
| TEST-03 | orchestrator crash | RAMJobStore re-hydration from L2 parent index + result-queue redelivery | zero-missing (lost fires are non-fatal Prom warnings, not missing) | MEDIUM-LOW |
| TEST-04 | keeper whole-tier blackout | per-replica BIT re-assert + accumulated recovery-queue drain | zero-missing + effect-once (keeper idle on happy path) | LOW correctness / Note-1 design Q |
| TEST-05 | redis crash | RetryLoop-exhaust → keeper REINJECT; recovery on reconnect | zero-missing + effect-once | **HIGH** (TTL-vs-dwell duplicate/drop hazard) |
| TEST-06 | rabbitmq crash | nack-requeue redelivery on reconnect | zero-missing + effect-once | MEDIUM |
| TEST-07 | redis+rabbitmq crash | superset of TEST-05+06 | zero-missing + effect-once | **HIGH** (hardest; reinject-drop) |

### What the analyzer measures (binding)
- **Zero-missing (completeness):** every STARTED run (distinct correlationId with ≥1 Step_* log) reaches COMPLETE = the full 9-label set incl. both sinks Step_F1+Step_F2. `Missing = StartedRuns − CompleteRuns; >0 ⇒ FAIL` (`PassFailEngine.cs:94,161`). **ES-primary, counter-reset-immune** — this is the binding arbiter precisely because every fault row resets a tier's Prom counters mid-window.
- **Effect-once (dedupe):** ANY duplicate `(correlationId, StepLabel)` in the ES Step_* hits ⇒ FAIL, fail-closed (`PassFailEngine.cs:107-110`). Redelivery is reported-not-failed *only* because the recovery machinery prevents a redelivery from emitting a second Step_* log (§Recovery Semantics).
- **Minimum evidence the recovery machinery is proven across the fault space:** all 7 scenarios produce `Verdict.Pass` (harness exit 0) on a clean end-to-end run, with the roll-up showing 7/7 PASS. A FAIL on any stateful row is a real finding, investigated before the milestone is declared proven (D-01b/D-04).

### Sampling Rate
- **Per scenario (the unit of proof):** one 5-min window + ~120s drain + bring-up/teardown ≈ 10-12 min, scored once by the analyzer.
- **Per sweep (the milestone gate):** all 7, exit 0 iff 7/7 PASS.
- **Re-run granularity:** single-scenario re-invocation of the bare harness, on an INFRA-ABORT exit only (D-04).

### Wave 0 Gaps
- [ ] `scripts/phase-68-*.ps1` — the sweep wrapper (NEW; the only authored control flow)
- [ ] 5 rows in the `$Scenarios` table in `phase-67-harness.ps1` (TEST-03..07)
- [ ] (recommended) rename `Analyze_HappyPath_Window_Yields_Pass` + sync the harness `--filter-method` literal + fix the stale IN-04 comment
- [ ] roll-up summary artifact (JSON + console/.md table)
- No new C# test files, no framework install — Phase 66's `PassFailEngineFacts.cs` already covers the scoring hermetically; the live proof is the validation.

## Wall-Clock Estimate

Per scenario (verified against the harness flow):
- A0 build (cache-warm no-op fast, but `--force-recreate` + 10-svc health gate): ~1-3 min
- B reset + B1 clean-orchestrator restart + health-wait: ~1 min
- C seed + D wf-id + E 204 gate: ~1 min
- F observe-to-N (N=4 fires at 30s cron ≈ 2 min) + 45s dwell + recovery health-wait: ~3-4 min
- F.5 hold remainder of 300s window: balance to 5 min total window
- H analyze: DrainMs 60s + PollToStable up to 60s + dotnet test spin-up: ~2-3 min
- Z down: ~0.5 min

**≈ 10-12 min per scenario → 7 × ≈ 70-85 min total** for a clean all-PASS sweep. A FAIL adds no time (the harness still completes its window + analyze). An infra-abort re-run adds one scenario's ~10-12 min. This aligns with MEMORY's "~50min/run" note for the heavier close-gate protocol — the Phase 68 sweep is lighter per-run (no triple-SHA net-zero) but runs 7×. **Plan for a ~1.5-hour single uninterrupted sweep.**

## Project Constraints (from CLAUDE.md / MEMORY)

- **MTP filter syntax (MEMORY):** `BaseApi.Tests` is xunit.v3 under Microsoft.Testing.Platform; `dotnet test --filter` (VSTest) is SILENTLY IGNORED (runs all 638). The harness already uses the correct MTP-native `-- --filter-method "*...*"` form. Any rename must preserve this form.
- **Design iteration style (MEMORY):** the user owns the spec; this phase analyzes + drives, never adds scope. The 5 rows + wrapper + roll-up are the entire deliverable. Do NOT add new recovery logic, new metrics, or speculative config changes (e.g. do NOT pre-emptively raise `ExecutionDataTtl` — surface it as an investigation branch instead).
- **Planning docs bloat / scoping drift (MEMORY):** edit `.planning/` docs surgically; do NOT full-rewrite STATE.md. Verify `STATE.md` frontmatter `milestone:` before milestone ops.
- **Close-gate net-zero protocol (MEMORY):** explicitly DROPPED for v8.0.0 (REQUIREMENTS Out-of-scope). Do NOT re-introduce any `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` BEFORE==AFTER gate. Truth = Prometheus + ES only.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| pwsh 7.x | the wrapper + harness | ✓ (repo convention; 18 close-scripts) | 7.x | none needed |
| docker + docker compose v2 | fault ops, bring-up, teardown | ✓ (assumed — proven in 67-03 live runs) | v2 | none |
| .NET 8 SDK (`dotnet test`) | seeder + analyzer fixtures | ✓ (proven in 67-03) | net8.0 | none |
| The full compose stack | every run | brought up per-run by the harness | per compose.yaml | none |

**No missing dependencies.** The phase ran live end-to-end in Phase 67 (67-03), so the environment is proven. The only run-time risk is machine load extending cold-start past the bounded 90s health-waits (an infra-abort, re-runnable per D-04).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The 5s `Processor__ExecutionDataTtl` (compose:285) is the most likely root cause of an *artifact* FAIL on TEST-05/06/07 (data expires during the 45s dwell). | Recovery Semantics (c)(e), cross-cutting risk | If wrong, a stateful-tier FAIL is a genuine effect-once defect — which is exactly the finding the phase is built to surface. Either way the D-01b "investigate first" discipline is correct; this only affects which hypothesis to test first. |
| A2 | The keeper is largely IDLE on the happy fan-out path (no recovery messages generated when redis+rabbitmq are healthy), so a 45s keeper-only blackout (TEST-04) perturbs little. | Recovery Semantics (b) | If keeper traffic is actually present on the happy path, TEST-04 stresses the recovery-queue accumulation/drain harder than predicted — still a valid proof, just a different risk profile. |
| A3 | Quartz cron fires lost during an orchestrator/redis/broker outage surface as NEVER-STARTED runs (non-fatal Prom corroboration warnings), not MISSING runs (verdict FAIL). | Recovery Semantics (a)(d) | If a lost-then-redelivered fire instead produces a started-but-incomplete run, that IS a MISSING → FAIL — a real finding to investigate. The analyzer scores this correctly either way; the assumption only affects the predicted verdict. |
| A4 | MassTransit auto-reconnect restores the broker-hard-dependent consumers (orchestrator/keeper/processor) within the residual window + drain after a rabbitmq restart. | Recovery Semantics (d)(e) | If reconnect is slow, redelivered results may miss the drain window → MISSING → FAIL. Watch the recovery health-wait (the harness gates on `/health/ready`, which is broker-hard for these tiers). |
| A5 | No rename of the analyzer fixture method is *functionally* required (the PASS assertion is already verdict-correct for recovered fault runs). | IN-04 Reconciliation | Verified directly from source (`AnalyzerE2ETests.cs:197` + `PassFailEngine.cs:161`); confidence HIGH. The rename is cosmetic-only. |

## Open Questions

1. **Will TEST-05/07 PASS with the uniform 45s dwell despite the 5s ExecutionDataTtl?**
   - What we know: the recovery path (RetryLoop-exhaust → keeper REINJECT → STRLEN-present check) is sound; the slot-array recovery pass is idempotent.
   - What's unclear: whether the 5s data-key TTL expiring during a 45s redis outage causes (a) reinject-drops (missing runs) or (b) forward-pass re-execution (duplicate Step_* logs). This is genuinely empirical — only the live run answers it.
   - Recommendation: run it as locked (D-01). If FAIL, investigate A1 first (TTL artifact vs defect). Do NOT pre-change the TTL or dwell.

2. **Is a TOTAL keeper blackout (both replicas, TEST-04) inside the recovery envelope, or only single-replica failover?** (Note 1)
   - What we know: the BIT loop is per-replica, edge-triggered, idempotent on re-assert; recovery messages accumulate non-destructively on the durable queue.
   - What's unclear: whether a total-blackout restart reliably re-opens the gate + Starts the recovery endpoint on every replica with no race.
   - Recommendation: run whole-tier per D-06. A FAIL traced to total-blackout-not-recoverable is a **real finding for the spec owner** — surface it, do NOT silently weaken to single-replica.

3. **Where should the roll-up summary land + what format?** (D-03, Claude's discretion)
   - Recommendation: `analyzer-reports/phase-68-summary.json` (machine, sibling to the 7 per-scenario reports) + a console table in the `[phase-68-sweep]` close-script style, optionally mirrored to a repo-root `phase-68-summary.txt` (matching the existing `psql-*.txt` artifact convention). Defer the final choice to the planner.

## Sources

### Primary (HIGH confidence — in-repo source, read this session)
- `scripts/phase-67-harness.ps1` (full) — the harness flow, exit-code table, scenario seam (~L87), IN-04 stale comment (L340-346), STEP Z down (L364).
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (full) — the PASS assertion (L197), env-seam (L116-117), windowed-delta Prom reads (L156-158), dedupe-counter dormancy (L364-366), ES window filter (L208-222).
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (full) — verdict logic (L161), missing (L94), duplicate fail-closed (L107-110), Prom corroboration non-binding (L112-143).
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` — no-dedup L1-only result advancement.
- `src/Orchestrator/Hydration/HydrationBackgroundService.cs` — RAMJobStore re-hydration from L2 parent index, hydration-gated readiness.
- `src/Orchestrator/Consumers/PauseAllConsumer.cs` / `ResumeAllConsumer.cs` — Quartz pause/resume, no-immediate-refire ordering.
- `src/Keeper/Health/BitHealthLoop.cs` — per-replica edge-triggered L2 probe, gate + recovery-endpoint Stop/Start, ResumeAll/PauseAll.
- `src/Keeper/Recovery/ReinjectConsumer.cs` — STRLEN-present check, by-design silent DROP on absent data (keeper_reinject_dropped).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (full) — EXISTS-L2 forward/recovery branch, slot-array send-before-retire, nack-requeue (no _error) send semantics.
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — 30s-TTL liveness, redis-fault log-and-continue.
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — bus wiring, redelivery seam.
- `compose.yaml` — service names, replica counts (keeper=2, processor-sample=2), `restart:unless-stopped`, `ExecutionDataTtl:5` (L285), broker-hard health deps.
- `.planning/REQUIREMENTS.md` — TEST-01..07 + pass bar + out-of-scope (net-zero dropped).
- `.planning/phases/68-.../68-CONTEXT.md` + `.planning/phases/67-.../67-CONTEXT.md` — locked decisions.

### Secondary (MEDIUM)
- MEMORY entries (MTP filter syntax, close-gate net-zero protocol, design-iteration style, planning-docs bloat) — project conventions.

### Tertiary (LOW)
- None. No web research needed — this phase is entirely in-repo.

## Metadata

**Confidence breakdown:**
- Sweep wrapper shape + exit-code handling: HIGH — verified against the harness's own exit + report-discovery code.
- IN-04 reconciliation: HIGH — verdict logic read directly from source; rename is cosmetic-only.
- Per-run isolation: HIGH — ES-primary windowing + per-run down/up verified.
- Per-fault-class recovery semantics: HIGH on the *mechanism* (each path traced to source), but the *verdict prediction* per stateful row is inherently MEDIUM/empirical — which is the entire point of the phase (the live run is the proof).
- The 5s-TTL-vs-45s-dwell hazard: MEDIUM (ASSUMED-impactful, A1) — the single most important thing for the planner to pre-stage as an investigation branch.

**Research date:** 2026-06-15
**Valid until:** ~2026-07-15 (stable; in-repo source, no external deps). Re-verify only if the harness, analyzer, or compose `ExecutionDataTtl` changes before execution.
```

