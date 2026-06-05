# Requirements: Steps API — Milestone v3.7.0

**Defined:** 2026-06-05
**Milestone:** v3.7.0 — Keeper: L2-Outage Dead-Letter Recovery & Workflow Pause/Resume
**Core Value:** A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework.

**Milestone mission — autonomous workflowing:** the cron-driven execution loop (fire → dispatch → process → result → fan-out) **self-heals through transient L2 (Redis) outages without operator intervention**. A new multi-replica `Keeper` console reacts to the `Fault<T>` events that the execution-path consumers publish on retry-budget exhaustion, probes L2 health on a bounded loop, pauses the affected workflow's cron so the outage stops spreading, re-injects the recovered work to its origin (riding the existing `flag[H]` idempotency), resumes the cron when no recoveries remain pending, and parks the genuinely unrecoverable in a single `keeper-dlq` for operator triage. Keeper is the automated operator for v3.6.0's accepted "an infra-faulting workflow keeps dead-lettering until an operator intervenes" gap. Operator-initiated commands (`StartOrchestration` / `StopOrchestration`) are **out of scope** — a failed Start/Stop is simply re-issued by the operator.

Phase numbering continues from 32.1 (this milestone starts at **Phase 33**). REQ-IDs are scoped to this milestone.

## v3.7.0 Requirements

### Keeper Console Foundation (KEEP)

- [ ] **KEEP-01**: A `Keeper` console exists on `BaseConsole.Core` (Generic-Host, metrics-only OTel, soft-dep Redis, embedded health probes, MassTransit/RabbitMQ, inherited correlation filters) with a minimal `Program.cs` mirroring `Orchestrator` — no infrastructure, identity, or bus boilerplate in the concrete beyond its recovery logic.
- [ ] **KEEP-02**: Keeper runs **multi-replica** with work load-balanced across replicas — a shared competing-consumer endpoint (not instance-unique fan-out), so RabbitMQ round-robins fault events across Keeper replicas.
- [ ] **KEEP-03**: Keeper builds, containerizes (multi-stage Dockerfile), and joins the compose stack as a new healthy tier alongside `orchestrator` / `processor-sample`.

### Fault Intake (INTAKE)

- [ ] **INTAKE-01**: Keeper recovers off the **published `Fault<T>` events** of the autonomous execution-path consumers — one `IConsumer<Fault<EntryStepDispatch>>` (processor) and one `IConsumer<Fault<ExecutionResult>>` (orchestrator) — each binding covers all producing replicas via pub/sub (no per-`{procId}_error`-queue binding). `Fault<StartOrchestration>` / `Fault<StopOrchestration>` are NOT consumed (operator-retried commands).
- [ ] **INTAKE-02**: Keeper extracts the original message and its full 6-id `IExecutionCorrelated` tuple (correlationId, workflowId, stepId, processorId, entryId, executionId) + `H` from `Fault<T>.Message`, and opens the execution log-scope from that inner message (since `Fault<T>` is not itself `IExecutionCorrelated`), so OTel logs carry the propagated correlation + execution ids.
- [ ] **INTAKE-03**: The MassTransit `_error` queues (`{procId}_error`, `orchestrator-result_error`) are retained only as a **self-expiring (TTL'd) forensic copy** — never Keeper's worklist; recovered work is never double-processed from `_error`.
- [ ] **INTAKE-04**: On recovery, Keeper **re-injects the original message directly to its origin endpoint by type** — `queue:{processorId:D}` for `EntryStepDispatch`, `queue:orchestrator-result` for `ExecutionResult` — riding the receiver's existing dedup gate (processor `flag[H]`; orchestrator `flag[m.H]`); no orchestrator round-trip for re-injection.

### L2 Health-Probe Recovery Loop (PROBE)

- [ ] **PROBE-01**: On intake, Keeper runs a recovery loop with a configurable inter-attempt delay (seconds) and configurable max-attempts; `delay × attempts` is bounded under RabbitMQ's consumer delivery-ack timeout (documented constraint).
- [ ] **PROBE-02**: Each iteration performs an L2 health probe — a read at the message's `skp:data:{entryId}` key **and** a write-then-delete of a scratch key — exercising both Redis read and write availability (not mere connectivity).
- [ ] **PROBE-03**: On the first successful probe, Keeper exits the loop and triggers recovery (re-inject + resume).
- [ ] **PROBE-04**: When max-attempts exhaust without a successful probe, Keeper parks the original message in `keeper-dlq` and exits the loop.
- [ ] **PROBE-05**: The fault message is acked **only after the loop exits** (success or give-up); a Keeper crash mid-loop leaves it un-acked → redelivered → the loop restarts (no lost message; at-least-once recovery).
- [ ] **PROBE-06**: Keeper performs **no `flag[H]` dedup of its own** — re-injection idempotency rides the receiver's surviving Phase-31 dedup gate; a duplicate re-inject (crash/redelivery, multiple replicas) collapses at the processor or orchestrator.

### Give-Up / Dead-Letter (DLQ)

- [ ] **DLQ-01**: Keeper's own infra faults (e.g., a failed re-inject send or pause/resume publish) retry under the same configurable `Immediate(N)` policy bound from config.
- [ ] **DLQ-02**: Both give-up paths land in a **single shared `keeper-dlq`**: (a) L2-probe attempts exhausted (explicit Send-to-`keeper-dlq` then ack); (b) Keeper's own `Immediate(N)` exhausted (error transport redirected to `keeper-dlq`).
- [ ] **DLQ-03**: `keeper-dlq` depth is exposed as a metric for Prometheus alerting; on give-up the affected workflow **stays paused** and resume is an operator action (no auto-resume into a still-broken L2).

### Workflow Pause/Resume Coordination (PAUSE)

- [ ] **PAUSE-01**: New `PauseWorkflow` and `ResumeWorkflow` message contracts exist in `Messaging.Contracts`, fanned out from Keeper to the orchestrator over the bus (cron scheduling only — re-injection lives in Keeper per INTAKE-04).
- [ ] **PAUSE-02**: On the first in-flight recovery for a workflow, the orchestrator halts that workflow's future cron fires (`UnscheduleOnlyAsync`, L1 preserved); the pause-state — a per-workflow pending-recovery set keyed by `H` — lives in the **single-replica orchestrator's in-memory L1** (available during the L2 outage), never in L2.
- [ ] **PAUSE-03**: When no recoveries remain pending for a workflow, the orchestrator reschedules it from L1 (`ScheduleAsync` with the L1 root's `jobId` + `cron`).
- [ ] **PAUSE-04**: Pause/resume signals are **idempotent and keyed by `H`**; duplicate or concurrent signals (Keeper crash/redelivery, multiple Keeper replicas) are serialized by the orchestrator's per-`workflowId` semaphore and do not double-count.
- [ ] **PAUSE-05**: A workflow with ≥1 unrecovered (given-up) message **remains paused** until an operator intervenes; the orchestrator does not auto-resume it.

### Keeper Observability (KMET)

- [ ] **KMET-01**: A code-defined `Keeper` meter is registered following the house pattern (snake_case instruments, no `_total` suffix, inherited `service_instance_id` resource label).
- [ ] **KMET-02**: Throughput/outcome counters exist — `keeper_fault_consumed`, `keeper_recovered`, `keeper_dlq_pushed{reason}`, `keeper_workflow_paused`, `keeper_workflow_resumed`, `keeper_l2_probe_failed` — labeled by `processorId` where meaningful, with no high-cardinality `workflowId` label.
- [ ] **KMET-03**: Bottleneck signals exist — `keeper_in_flight` (UpDownCounter of messages currently held in probe loops) and a `keeper_recovery_duration` histogram (intake → terminal) — enabling saturation/latency PromQL.
- [ ] **KMET-04**: Keeper emits OTel logs consistent with the other consoles, carrying the correlationId + execution-scope ids propagated from the faulted message.

### Live Proof & Close Gate (TEST)

- [ ] **TEST-01**: A real-stack E2E induces an L2 (Redis) outage that dead-letters **both** an `EntryStepDispatch` (processor) and an `ExecutionResult` (orchestrator), then proves Keeper pauses the workflow, recovers when L2 returns, resumes, and re-injects each to its origin with exactly-once downstream effect (no duplicate) — validating the uniform loop + per-type re-inject.
- [ ] **TEST-02**: A real-stack E2E proves the give-up path: L2 stays down past max-attempts → the message lands in `keeper-dlq`, the workflow stays paused, and `keeper_dlq_pushed` increments.
- [ ] **TEST-03**: A phase-close gate runs 3× consecutive GREEN with the triple-SHA (psql `\l` / redis `--scan` / rabbitmqctl `list_queues`) BEFORE==AFTER, including `keeper-dlq` + probe scratch-key scan-clean (net-zero), Release+Debug 0-warning.

## Future Requirements (deferred)

- **FUTURE-KEEPER-SWEEP** — a background L2-liveness sweep in Keeper that auto-resumes paused workflows on L2 recovery (replacing the operator step for given-up messages).
- **FUTURE-KEEPER-ERROR-SUPPRESS** — suppress the `_error` move entirely (vs TTL'd forensic copy) once `Fault<T>` subscriber-queue durability is confirmed.
- **FUTURE-KEEPER-START-RECOVERY** — extend Keeper to recover operator-command faults (`Fault<StartOrchestration>`) if autonomous Start recovery is later desired.

## Out of Scope

- **Operator-command recovery (`StartOrchestration` / `StopOrchestration`)** — a failed Start/Stop is re-issued by the operator; Keeper targets only the autonomous execution loop. *Why: Start/Stop are operator-initiated, not autonomous; Stop is L1-only and never L2-faults.*
- **Recovery of genuine poison/business faults** — Keeper assumes recoverable infra (L2) faults; a message that fails for non-L2 reasons exhausts the probe loop and lands in `keeper-dlq`. *Why: only L2-outage recovery is in scope; business faults need different handling.*
- **Multi-replica orchestrator scheduling** — the orchestrator stays single-replica; the pause-state design (in-memory L1) depends on this. *Why: distributed scheduling is a separate concern; single-replica is the current reality.*
- **Transactional outbox** — still deferred from v3.6.0. *Why: effect-first + downstream dedup remains the chosen idempotency model.*
- **Auto-resume into a recovered L2 after give-up** — an operator action in v1 (tracked as FUTURE-KEEPER-SWEEP). *Why: avoids resuming into a still-broken L2; bounds v1 scope.*

## Traceability

_(filled by the roadmapper — every REQ-ID maps to exactly one phase)_

| REQ-ID | Phase | Status |
|--------|-------|--------|
| _pending roadmap_ | | |
