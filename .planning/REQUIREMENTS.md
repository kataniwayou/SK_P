# Steps API — v8.0.0 Requirements

> **Milestone:** v8.0.0 — E2E Resilience Proof
> **Source of truth:** This milestone's planning conversation (2026-06-14), scope confirmed point-by-point.
> **Posture:** Whole-system **live recovery proof under faults**. Supersedes v7.0.0's deferred Phase-62 live proof. Sources of truth are **Prometheus + Elasticsearch ONLY** (distinct from the prior triple-SHA infra net-zero close gates). Phases continue at **63**.

## Goal

Prove perfect (**zero-missing**, **effect-once**) recovery of a fan-out orchestrated workflow under 7 sustained 5-minute fault scenarios, verified solely from Prometheus metrics and Elasticsearch logs, fully automated — no human verification.

**Workflow under test:** a single seeded definition `A → B → C → { D1 → E1 → F1, D2 → E2 → F2 }` (9 steps, entry A, fan-out at C, two sinks F1/F2), all steps backed by **one** shared `processor-sample`, cron `*/30 * * * * *`, each step's assignment payload `{ number:int, label:"Step_*" }`.

**Pass bar:** every triggered correlationId reaches both sinks (zero-missing) AND each step's COMPLETED effect lands exactly once per correlationId (effect-once); message-level redelivery during a crash is **reported, not failed** (matches the documented exactly-once-effect guarantee).

## Requirements

### Seconds-Granularity Cron (CRON)
- [x] **CRON-01
**: The orchestrator fires a workflow on a 6-field seconds-granularity cron expression (`*/30 * * * * *` → every 30 seconds); `CronInterval` next-occurrence + interval math computes sub-minute intervals correctly (UTC).
- [x] **CRON-02
**: The workflow create/update cron validator accepts the 6-field seconds form (previously rejected as non-5-field-standard), with the 5-field form still accepted.

### Processor Work & Structured Logging (PROC)
- [x] **PROC-01
**: A processor step's payload carries an integer and a string; the framework deserializes the assignment payload into the typed config exposing both fields.
- [x] **PROC-02
**: `ProcessAsync` generates a random number, adds it to the payload integer, and produces the sum as the step's completed result.
- [x] **PROC-03
**: `ProcessAsync` emits a structured log entry tagged with the payload string `Step_<label>` and the computed sum, carrying `correlationId` + `stepId` (+ `workflowId`/`processorId`) so Elasticsearch can aggregate a run by correlationId and identify each step.

### Fan-Out Workflow Seeder (WF)
- [x] **WF-01
**: A seeder creates the fan-out workflow `A→B→C→{D1→E1→F1, D2→E2→F2}` (9 steps, entry A, fan-out at C, sinks F1+F2) with every step referencing one shared processor and the `*/30 * * * * *` cron.
- [x] **WF-02
**: Each of the 9 steps has an assignment carrying the `{ number, label:"Step_*" }` payload; the seeder is idempotent (re-runnable without duplicating workflow/step/assignment rows).

### Clean-State Test Stack (ENV)
- [x] **ENV-01
**: The proof runs a minimal stack with a single `processor-sample` — the redundant `processor-badconfig` is excluded — alongside the full infra + observability tiers (postgres, redis, rabbitmq, otel-collector, elasticsearch, prometheus, orchestrator, keeper, baseapi-service).
- [x] **ENV-02
**: The harness starts each test from clean state — Redis flushed, Postgres workflow/step/assignment rows reset, and leftover/redundant processor containers removed — so each test's metrics and logs are attributable to that run only.

### Fault-Injection Harness (FAULT)
- [ ] **FAULT-01**: The harness activates the workflow via `POST /api/v1/orchestration/start` and lets the cron drive it for a 5-minute observation window per test (~10 triggers, fresh correlationId per fire).
- [ ] **FAULT-02**: The harness injects each scenario's fault mid-run (container kill/restart of the targeted tier) and allows the system to recover within the same window.
- [ ] **FAULT-03**: Each scenario runs fully automated end-to-end (clean → seed → activate → inject fault → observe → analyze → tear down) with no human verification step.

### Prometheus + ES Analysis (OBS)
- [x] **OBS-01
**: The analyzer aggregates all Elasticsearch logs sharing a correlationId into a per-run trace and determines, per run, whether all 9 steps and both sinks (F1, F2) completed.
- [x] **OBS-02
**: The analyzer detects, against the total number of cron triggers, MISSING runs/steps (a triggered correlationId that did not complete all steps/both sinks) and DUPLICATE step effects (a step's COMPLETED effect recorded more than once per correlationId).
- [x] **OBS-03
**: The analyzer queries Prometheus counters (`orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `processor_dispatch_consumed`, `processor_result_sent{outcome}`, dedupe + keeper counters) and cross-checks dispatched vs completed vs deduped against the total trigger count.
- [ ] **OBS-04**: Each test emits a complete per-test smoke report (correlationId-aggregated log trace + metric summary) and an automated PASS/FAIL verdict derived **solely** from Prometheus + Elasticsearch.

### Resilience Proof — 7 Scenarios (TEST)
> Each scenario PASSES iff, over its 5-minute/30s-cron window, **zero-missing** (every triggered correlationId reaches both sinks F1+F2) AND **effect-once** (each step's COMPLETED effect once per correlationId) hold; message-level redelivery during the fault is reported, not failed.
- [ ] **TEST-01**: Happy path — no fault injected; baseline zero-missing + effect-once.
- [ ] **TEST-02**: Processor crash during orchestration — recovery proven.
- [ ] **TEST-03**: Orchestrator crash during orchestration — recovery proven.
- [ ] **TEST-04**: Keeper crash during orchestration — recovery proven.
- [ ] **TEST-05**: Redis crash during orchestration — recovery proven.
- [ ] **TEST-06**: RabbitMQ crash during orchestration — recovery proven.
- [ ] **TEST-07**: Redis + RabbitMQ crash during orchestration — recovery proven.

## Future Requirements (deferred)

- **K8s liveness-probe restart wiring** (carried from v7.0.0) — pointing the actual Kubernetes liveness probe at the watchdog with a restart policy.
- **Continuous/long-soak resilience runs** — beyond the 5-minute per-scenario window (e.g. multi-hour soak, randomized fault timing).
- **Grafana dashboards / alerting** over the resilience metrics (analysis is script-driven this milestone).

## Out of Scope

- **Triple-SHA infra net-zero close gate** — this milestone's truth is Prometheus + ES, not `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` SHA invariants (the prior close-gate protocol). Net-zero is not a v8.0.0 pass criterion.
- **Multi-replica orchestrator/keeper scaling** — orchestrator stays single-replica; faults are injected against the existing topology.
- **Distinct processors per step** — all 9 steps share one `processor-sample` (step identity comes from the payload label, not the processor).
- **New recovery mechanisms** — v8.0.0 PROVES the existing recovery machinery (keeper, idempotency/effect-once, slot-array, per-replica liveness); it does not add new recovery logic. Any product change is limited to seconds-cron + the processor payload/logging.

## Traceability

REQ-IDs are filled into phases by the roadmapper (Step 10). Every requirement maps to exactly one phase; the roadmapper validates 100% coverage. Phases continue at **63**.

| Requirement | Phase | Status |
|-------------|-------|--------|
| CRON-01, CRON-02 | Phase 63 | Pending |
| PROC-01, PROC-02, PROC-03 | Phase 64 | Pending |
| WF-01, WF-02 | Phase 65 | Pending |
| ENV-01, ENV-02 | Phase 65 | Pending |
| OBS-01, OBS-02, OBS-03, OBS-04 | Phase 66 | Pending |
| FAULT-01, FAULT-02, FAULT-03 | Phase 67 | Pending |
| TEST-01, TEST-02, TEST-03, TEST-04, TEST-05, TEST-06, TEST-07 | Phase 68 | Pending |

**Coverage:** 23 requirements across 7 categories (CRON, PROC, WF, ENV, FAULT, OBS, TEST). Filled into phases by the roadmapper.
