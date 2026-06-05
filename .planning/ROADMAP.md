# Roadmap: Steps API

## Milestones

- ✅ **v3.2.0 Steps API MVP** — Phases 1-11 (shipped 2026-05-28) — see [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md)
- ✅ **v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline** — Phases 12-16 (shipped 2026-05-29) — see [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md)
- ✅ **v3.4.0 BaseConsole + Orchestrator Messaging** — Phases 17-24 + 24.1 (shipped 2026-06-01) — see [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md)
- ✅ **v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip** — Phases 25-30 (shipped 2026-06-02)
- ✅ **v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip** — Phases 31-32.1 (shipped 2026-06-05) — see [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md)
- 🚧 **v3.7.0 Keeper — L2-Outage Dead-Letter Recovery & Workflow Pause/Resume** — Phases 33-38 (in progress)

## 🚧 v3.7.0 Keeper — L2-Outage Dead-Letter Recovery & Workflow Pause/Resume (In Progress)

**Milestone Goal:** Make the autonomous execution loop (cron fire → dispatch → process → result → fan-out) **self-heal through transient L2 (Redis) outages without operator intervention.** A new multi-replica `Keeper` console reacts to the `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` events that the execution-path consumers publish on retry-budget exhaustion, probes L2 health on a bounded loop, pauses the affected workflow's cron (via the single-replica orchestrator's in-memory L1) so the outage stops spreading, re-injects recovered work to its origin (riding the receiver's existing `flag[H]` idempotency), resumes when no recoveries remain pending, and parks the genuinely unrecoverable in `keeper-dlq` for operator triage. Keeper is the automated operator for v3.6.0's accepted "an infra-faulting workflow keeps dead-lettering until an operator intervenes" gap. Operator commands (Start/Stop) are out of scope — a failed Start/Stop is simply re-issued.

**Build order (locked):** 33 (spike — de-risk the `Fault<T>`→reinject→`flag[H]`-collapse round-trip) → 34 (Keeper console foundation) → 35 (fault intake + correlation) → 36 (L2 probe loop + two DLQs) → 37 (orchestrator pause/resume) → 38 (metrics + real-stack E2E + close gate).

- [x] **Phase 33: Fault-Recovery Spike (de-risk)** — Prove `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` consumption via pub/sub, inner-message + 6-id correlation extraction, re-inject to origin, and receiver `flag[H]` collapse — before building anything. (INTAKE-01, INTAKE-02, INTAKE-04, PROBE-06) (completed 2026-06-05 — LIVE-PROVEN: spike GREEN + close gate GATE_EXIT=0, 453 facts ×3, triple-SHA net-zero held; 2 trip recipes corrected, c2d6ea6)
- [x] **Phase 34: Keeper Console Foundation** — Runnable multi-replica `Keeper` on `BaseConsole.Core`; builds, containerizes, joins compose healthy; competing-consumer load-balancing. (KEEP-01, KEEP-02, KEEP-03) (completed 2026-06-05 — hermetic 4/4: round-robin test consumed==1, ComposeYamlFacts shape guards, docker build green, 0-warning Release+Debug, 454-pass suite; live multi-replica + compose-health smokes operator-pending, 34-HUMAN-UAT.md, Phase-38 live gate)
- [x] **Phase 35: Fault Intake & Correlation** — Production intake of the two `Fault<T>` events; extract 6-id tuple + `H`; open execution log-scope; `_error` → TTL'd forensic DLQ-1 only. (INTAKE-03, KMET-04) (completed 2026-06-05 — hermetic: BuildState byte-identical refactor (6 scope-guard classes GREEN), two real `Fault<T>` consumers on `keeper-fault-recovery` with manual CorrelationId scope + KeeperFaultConsumerScopeTests 3/3 (SC2), 0-warning Release; INTAKE-03 separation slice + KMET-04 hermetic-proven; SC3 live ES-correlation operator-pending, 35-HUMAN-UAT.md, Phase-38 live gate)
- [ ] **Phase 36: L2 Health-Probe Recovery Loop & DLQs** — Bounded crash-survivable L2 read+write probe loop; re-inject on success, give-up to `keeper-dlq` (DLQ-2); ack-after-loop; two DLQs split by exhaustion mechanism (Immediate(N) → DLQ-1, probe → DLQ-2); shared `Immediate(N)` from appsettings across all consumers. (PROBE-01..05, DLQ-01..04)
- [ ] **Phase 37: Orchestrator Pause/Resume Coordination** — New `PauseWorkflow`/`ResumeWorkflow` contracts + orchestrator consumers; per-workflow pending-recovery set keyed by `H` in single-replica L1; idempotent; stays paused on give-up. *(Only phase touching the Orchestrator project.)* (PAUSE-01..05)
- [ ] **Phase 38: Keeper Observability + Real-Stack E2E + Close Gate** — `Keeper` meter + counters/histograms; E2E proving recover-both-paths + give-up; 3×GREEN triple-SHA net-zero close gate (both DLQs + scratch-key scan-clean). (KMET-01/02/03, TEST-01/02/03)

## Phases (shipped milestones)

<details>
<summary>✅ v3.2.0 Steps API MVP (Phases 1-11) — SHIPPED 2026-05-28</summary>

11 phases / 41 plans / 142 integration facts GREEN × 3 consecutive runs. Full phase details, decisions, and execution narrative archived to [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md).

- [x] Phase 1: Repository Scaffold (3/3 plans) — 2026-05-26
- [x] Phase 2: Postgres + Docker Compose (2/2 plans) — 2026-05-26
- [x] Phase 3: EF Core Persistence Base (2/2 plans) — 2026-05-27
- [x] Phase 4: Cross-Cutting Middleware + Error Handling (2/2 plans) — 2026-05-27
- [x] Phase 5: Observability + Health Probes (2/2 plans) — 2026-05-27
- [x] Phase 6: Validation + Mapping Base (2/2 plans) — 2026-05-27
- [x] Phase 7: Generic HTTP Base + Composition Root (2/2 plans) — 2026-05-27
- [x] Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests (8/8 plans) — 2026-05-28
- [x] Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop (3/3 plans) — 2026-05-28
- [x] Phase 10: Remove SchemaId on AssignmentEntity, add ConfigSchemaId on ProcessorEntity (5/5 plans) — 2026-05-28
- [x] Phase 11: Migrate Prometheus + Elasticsearch from compose stack sk2_1 to sk_p (10/10 plans) — 2026-05-28

</details>

<details>
<summary>✅ v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline (Phases 12-16) — SHIPPED 2026-05-29</summary>

5 phases / 26 plans / 235 integration facts GREEN × 3 consecutive runs, dual-SHA (`psql \l` + `redis-cli --scan`) BEFORE=AFTER held. 64/64 requirements satisfied (audit PASSED). Full phase details, success criteria, and decisions archived to [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md).

- [x] Phase 12: Redis infra + composition + healthcheck + DI registration (8/8 plans) — 2026-05-29
- [x] Phase 13: OrchestrationService split + L3 fetch + L1 build (3/3 plans) — 2026-05-29
- [x] Phase 14: Validation gates (DFS + schema-edge + payload-config-schema) (5/5 plans) — 2026-05-29
- [x] Phase 15: L2 Redis projection write + Stop existence check (5/5 plans) — 2026-05-29
- [x] Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout (5/5 plans) — 2026-05-29

</details>

<details>
<summary>✅ v3.4.0 BaseConsole + Orchestrator Messaging (Phases 17-24 + 24.1) — SHIPPED 2026-06-01</summary>

9 phases / 31 plans. A reusable `BaseConsole.Core` Generic-Host library + a runnable `Orchestrator` console connected to the WebApi over MassTransit/RabbitMQ, with body-carried CorrelationId proven end-to-end (HTTP → Redis L2 → fan-out → orchestrator log in Elasticsearch), the full orchestrator lifecycle (L1 hydration, Quartz scheduling, entry-step dispatch, stop teardown), the processor→orchestrator result round-trip + L1-only step advancement, and a gating redesign (L2-existence dedup, boot-gate/plugin removal, atomic Stop). Final clean-build suite 335/335 GREEN (real-stack E2E live), Release 0 warnings. 70/70 requirements (ORCH-GATE-01 superseded by 24.1). Milestone audit PASSED. Full phase details archived to [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md).

- [x] Phase 17: Messaging.Contracts + Shared L2 Root Extract (2/2 plans) — 2026-05-30
- [x] Phase 18: BaseConsole.Core Library (4/4 plans) — 2026-05-30
- [x] Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier (4/4 plans) — 2026-05-30
- [x] Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout (4/4 plans) — 2026-05-31
- [x] Phase 21: v3.4.0 Closeout Hygiene — shared L2ProjectionKeys (1/1 plan) — 2026-05-31
- [x] Phase 22: L2 Root-Parent Restructure + Processor Self-Registration (5/5 plans) — 2026-05-31
- [x] Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown (5/5 plans) — 2026-05-31
- [x] Phase 24: Orchestrator Result-Consume & Step Advancement (5/5 plans) — 2026-06-01
- [x] Phase 24.1: Gating Redesign — L2-dedup + Gate Removal (gap-closure) (1/1 plan) — 2026-06-01

</details>

<details>
<summary>✅ v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip (Phases 31-32.1) — SHIPPED 2026-06-05</summary>

4 phases / 9 active plans. The orchestrator↔processor round-trip became exactly-once-effect: deterministic identity `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` (executionId excluded) + effect-first `flag[H]` CAS dedup at both hops → each step's downstream effect happens exactly once, no lost branch, `ProcessAsync` needs no idempotency logic. Content-addressed two-level L2 (blobs + manifest), per-edge merge (content-collapse), N×M manifest fan-out, configurable retry budget. A late course-correction reverted the planned cancelled circuit-breaker (Phase 32 → 32.1) to plain dead-lettering on exhaustion, preserving the Phase-31 idempotency layer. 14/14 active requirements satisfied + live-verified (Phase-32's 8 retired); audit tech_debt with 0 functional blockers; 32.1 close gate `GATE_EXIT=0` (3×GREEN=452, triple-SHA BEFORE==AFTER held). Full phase details, decisions, and the breaker-revert rationale archived to [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md).

- [x] Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect) (6/6 plans) — 2026-06-04
- [x] Phase 31.1: Close-Gate Redis Net-Zero (gap closure) (1/1 plan) — 2026-06-04
- [x] Phase 32: Cancelled Circuit-Breaker (5/6 plans) — **superseded by 32.1** (breaker reverted)
- [x] Phase 32.1: Dead-Letter on Exhaustion (Breaker Reverted) (2/2 plans) — 2026-06-05

</details>

## 🚧 v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip (In Progress)

**Milestone Goal:** Stand up a reusable `BaseProcessor.Core` library + a first concrete `Processor.Sample` console (the processor-side mirror of `BaseConsole.Core`/`Orchestrator`) that self-identifies via an assembly-embedded SourceHash, self-registers its liveness into Redis L2 (only-when-Healthy, lock-free), and runs the full live orchestrator→processor→orchestrator execution round-trip — with the actual transform isolated to one minimal `abstract ProcessAsync` seam. The v3.4.0 `Orchestrator`, the `EntryStepDispatch`/`ExecutionResult` wire contracts, and `ProcessorLivenessValidator` are all reused **unchanged**.

**Build order (locked, mirrors the v3.4.0 leaf→base→wiring→concrete→proof cadence):**
25 (leaf shared contracts + WebApi responders) → 26 (`BaseProcessor.Core`: library + identity + two-loop startup + liveness worker) → 27 (execution round-trip) → 28 (SourceHash MSBuild identity + `Processor.Sample` + real-stack E2E closeout).

- [x] **Phase 25: Shared Contracts + WebApi Responders** — Leaf contract extracts (`ProcessorProjection` public, `ExecutionData` key, `"Healthy"` constant, 2 request/response pairs) + relax the WebApi publish-only firewall to host `GetProcessorBySourceHash` + `GetSchemaDefinition` responders. (completed 2026-06-01)
- [x] **Phase 26: BaseProcessor.Core — Library, Identity & Liveness** — Reusable Generic-Host scaffold on `BaseConsole.Core`; two-loop startup (identity-by-SourceHash + schema-definition resolution via `IRequestClient` with retry); only-when-Healthy liveness heartbeat worker into Redis L2. (completed 2026-06-01)
- [x] **Phase 27: Execution Round-Trip** — Durable `queue:{processorId:D}` consumer bound at Healthy; L2 input resolution + input validation; the `abstract ProcessAsync` seam; per-result output validation + L2 data write + result minting + one-by-one `ExecutionResult` sends; ack-after-send / business-ack / infra-throw; inherited correlation.
- [x] **Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout** — MSBuild SourceHash target (SHA-256, lowercase 64-hex, LF-normalized, folded over base+concrete `.cs`) + assembly-metadata embed; first concrete `Processor.Sample` (dummy `ProcessAsync` + multistage Dockerfile + compose tier); real-stack E2E round-trip proof + 3-GREEN/triple-SHA close gate.
- [x] **Phase 29: Structured Execution-Scope Logging** — Ambient structured-attribute logs (CorrelationId, WorkflowId, StepId, ProcessorId, ExecutionId, EntryId) via MEL log scopes serialized by OTel `IncludeScopes` to Elasticsearch: unchanged `InboundCorrelationConsumeFilter` + new bus-wide `InboundExecutionScopeConsumeFilter` (execution id-set for `IExecutionCorrelated`, both consoles), shared `ExecutionLogScope` keys, skip `Guid.Empty`, per-result inner scope for minted ExecutionId/output EntryId, process-wide ProcessorId enricher from `IProcessorContext`, explicit scope in the Quartz `WorkflowFireJob`. (completed 2026-06-02)
- [x] **Phase 30: Runtime & Business Metrics** — Code-defined runtime + business metrics carrying a per-replica `service_instance_id` label (the pod name in k8s), set as a resource attribute in the base libs; new orchestrator + processor send/consume counters (labelled by `ProcessorId`, processor adds `outcome`) registered via `AddMeter` — enabling PromQL rate/diff analysis of orchestrator→processor dispatch throughput and per-processor outcome bottlenecks across replicas, with no high-cardinality `workflowId` label and no collector-side metric config. (planned 2026-06-02) (completed 2026-06-02)

## Phase Details

### Phase 25: Shared Contracts + WebApi Responders
**Goal**: The leaf shared-contract vocabulary both sides depend on exists in `Messaging.Contracts`, and the WebApi can answer identity + schema-definition bus requests — so the processor (built later) has something to query.
**Depends on**: Phase 24.1 (v3.4.0 close — `Messaging.Contracts`, `L2ProjectionKeys`, `ProcessorService.GetBySourceHashAsync`, and the publish-only `AddBaseApiMessaging` firewall all in place)
**Requirements**: CONTRACT-01, CONTRACT-02, CONTRACT-03, RPC-01, RPC-02, RPC-03
**Success Criteria** (what must be TRUE):
  1. `ProcessorProjection` is public in `Messaging.Contracts.Projections` and is the single shared type used by both WebApi and (later) the processor — no duplicate definition.
  2. `L2ProjectionKeys.ExecutionData(Guid entryId)` returns `skp:data:{entryId:D}`, distinct from the existing `root`/`step`/`processor` key builders, with a golden test pinning the exact string.
  3. The liveness `status` value `"Healthy"` is a shared constant in `Messaging.Contracts` (single source of truth — writer and reader cannot desync), and the two `GetProcessorBySourceHash` / `GetSchemaDefinition` request/response record pairs are defined there.
  4. The WebApi bus join, extended from publish-only, answers a `GetProcessorBySourceHash` request with `{ Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` (or a not-found response) backed by `ProcessorService.GetBySourceHashAsync`, and a `GetSchemaDefinition(schemaId)` request with `{ Definition }` (or not-found) backed by the existing schema read.
  5. The CRUD surface is unaffected — existing WebApi HTTP behavior and the v3.4.0 publish path are unchanged (no regression in the existing suite).
**Plans**: 2 plans
- [x] 25-01-PLAN.md — Shared contract extracts in Messaging.Contracts (ProcessorProjection move, ExecutionData key, Healthy const, request/response record pairs + queue constants)
- [x] 25-02-PLAN.md — WebApi responder host (two-hook bus join extension + GetProcessorBySourceHash / GetSchemaDefinition dual-response consumers, firewall + Degraded-cap preserved)

### Phase 26: BaseProcessor.Core — Library, Identity & Liveness
**Goal**: A reusable `BaseProcessor.Core` library exists on which a concrete processor self-identifies via its embedded SourceHash, resolves its identity + schema definitions over the bus (retrying through boot-before-register), and self-registers liveness into Redis L2 — only while Healthy, lock-free, in the exact shape the v3.4.0 `ProcessorLivenessValidator` reads.
**Depends on**: Phase 25 (the request/response contracts + WebApi responders must exist before the processor can resolve identity/definitions)
**Requirements**: BPC-01, BPC-02, BPC-03, IDENT-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02, LIVE-01, LIVE-02, LIVE-03, LIVE-04, LIVE-05, LIVE-06, CONFIG-01
**Success Criteria** (what must be TRUE):
  1. `BaseProcessor.Core` is a reusable Generic-Host library built on `BaseConsole.Core` (inheriting soft-dep Redis, embedded health probes, metrics-only OTel, MassTransit/RabbitMQ, inbound/outbound correlation filters), and `AddBaseProcessor` wires the startup orchestration so a concrete `Program.cs` stays minimal.
  2. At runtime the processor reads its SourceHash from assembly metadata via reflection, then resolves its identity (`Id` + three nullable schema Ids) by issuing a `GetProcessorBySourceHash` `IRequestClient` query, retrying on timeout/not-found until it succeeds (booting before the DB row exists is tolerated).
  3. For each non-null (input, output) schema Id the processor resolves the definition via a `GetSchemaDefinition` `IRequestClient` query (retry until resolved); null/optional schema Ids are skipped by design and never cause failure (the config schema is not resolved).
  4. A background heartbeat worker writes/refreshes `skp:{processorId:D}` every `Interval` seconds with `{ inputDefinition, outputDefinition, liveness{ timestamp, interval, status: "Healthy" } }`, re-applying the configured `Ttl` expiry each beat (sliding) — written **only once Healthy** (identity + all required definitions resolved); a starting/restarting/unhealthy replica does not write (orchestrator sees `absent`), and the written `interval` equals the configured delay so `timestamp + interval×2` staleness holds.
  5. The written L2 value shape exactly matches what the v3.4.0 `ProcessorLivenessValidator` reads (reused unchanged — presence+freshness ⟺ "≥1 replica healthy"), and multi-replica writes are lock-free: the shared liveness key is a blind whole-value `SET` of equivalent only-when-Healthy content (last-write-wins, no synchronization).
**Plans**: 3 plans
  - [x] 26-01-PLAN.md — Project skeleton + identity/seam/options contracts + Wave 0 test scaffold + exchange: request-client confirmation (BPC-01, BPC-02, IDENT-03, CONFIG-01) — completed 2026-06-01
  - [x] 26-02-PLAN.md — AddBaseProcessor composition root + two-loop startup orchestrator (BPC-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02) — completed 2026-06-01
  - [x] 26-03-PLAN.md — Only-when-Healthy liveness heartbeat worker + closed reader round-trip (LIVE-01..06) — completed 2026-06-01

### Phase 27: Execution Round-Trip
**Goal**: A Healthy processor consumes a real `EntryStepDispatch`, resolves + validates its input from L2, runs the `abstract ProcessAsync` transform, validates + writes each output to L2, mints results, and sends `ExecutionResult`s back to the orchestrator one-by-one — with the framework owning all id-minting, validation, L2 I/O, and sending so a concrete overrides only `ProcessAsync`.
**Depends on**: Phase 26 (the processor must resolve identity + definitions and be Healthy before it can bind the dispatch queue and validate input/output)
**Requirements**: EXEC-01, EXEC-02, EXEC-03, EXEC-04, EXEC-05, EXEC-06, EXEC-07, EXEC-08, EXEC-09, EXEC-10, CONFIG-02
**Success Criteria** (what must be TRUE):
  1. The processor consumes `EntryStepDispatch` on a **durable** `queue:{processorId:D}` competing-consumer endpoint that is bound only once Healthy (and `Healthy` is written to L2 only after the bind), so the orchestrator never sends to a non-existent queue; a restarting/unhealthy processor leaves dispatches queued (not lost, not processed) until it recovers.
  2. Input data is read only from `L2[data(entryId)]` (existence-checked; `Payload` is config, never input) and validated against `inputDefinition` when present — an empty definition skips validation; a non-empty definition with missing/empty `entryId` yields a `Failed` result with an error message.
  3. The sole transform seam is `abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` (`config` = dispatch `Payload`); for each result the framework validates output vs `outputDefinition` (empty = valid), mints a new `entryId` + writes the output to `L2[data(newEntryId)]` with the configured execution-data TTL on success (nothing written on output-validation failure → `Failed`), and mints a per-result `executionId` + stamps the shared Ids.
  4. Results are sent to `queue:orchestrator-result` one-by-one (never a batched list); an empty result list with no exception/cancellation acks only (no message), while `Failed` (incl. caught exceptions, with an error message) and `Cancelled` are always sent.
  5. The dispatch is acked only after all sends complete; infra faults throw and retry (`Immediate(3)`) (business-ack / infra-throw, mirroring the orchestrator), and the body `CorrelationId` is inherited from `BaseConsole.Core` — flowing from the dispatch into the log scope and onto every published `ExecutionResult`.
**Plans**: 3 plans
- [x] 27-01-PLAN.md — Foundation: firm ProcessResult + BaseProcessor internal invoker, port SSRF-locked Json.Schema validator, add CONFIG-02 ExecutionDataTtlSeconds (EXEC-03/04/05, CONFIG-02) — completed 2026-06-01
- [x] 27-02-PLAN.md — EntryStepDispatchConsumer: L2 input read/validate, ProcessAsync invoke, per-result output-validate/mint/write, one-by-one ExecutionResult send, business-ack/infra-throw (EXEC-02/04/05/06/07/08/09/10) — completed 2026-06-01
- [x] 27-03-PLAN.md — Wiring: register consumer (ExcludeFromConfigureEndpoints) + runtime ConnectReceiveEndpoint bind-then-MarkHealthy (EXEC-01) — completed 2026-06-02

### Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout
**Goal**: The deterministic build-time SourceHash identity is embedded into the assembly, the first concrete `Processor.Sample` exists and joins the compose stack, and a real-stack E2E proves the live orchestrator→Processor.Sample→orchestrator round-trip and the liveness-gated Start — all behind the 3-GREEN / triple-SHA close gate.
**Depends on**: Phase 27 (the full framework round-trip must exist before a concrete + E2E can exercise it end-to-end)
**Requirements**: IDENT-01, IDENT-02, SAMPLE-01, SAMPLE-02, TEST-01, TEST-02
**Success Criteria** (what must be TRUE):
  1. An MSBuild target (`BeforeTargets=CoreCompile`) computes the SourceHash — SHA-256, lowercase 64-hex, LF-normalized, per-file hashes folded deterministically (ordinal path sort) over `BaseProcessor.Core` + the concrete's `.cs` (excluding generated files, `BaseConsole.Core`, `Messaging.Contracts`) — emits it as `[assembly: AssemblyMetadata("SourceHash", …)]`, and re-runs on implementation source change (no stale hash on incremental builds).
  2. `Processor.Sample` is the first concrete console (family convention `Processor.<Purpose>`), implementing `ProcessAsync` with a minimal POC dummy result list and carrying no infrastructure/id/L2/bus code.
  3. `Processor.Sample` ships a multistage Dockerfile and joins the compose stack (mirroring the Orchestrator tier), and its built binary's embedded SourceHash (lowercase 64-hex, satisfying the DB `^[a-f0-9]{64}$` validator) is the value registered as the Processor DB row via CRUD.
  4. A real-stack E2E proves the live round-trip — a dispatch is consumed, output is written to L2, and the orchestrator advances on the returned `ExecutionResult` — and proves the liveness-gated Start path (a live `Processor.Sample` heartbeat lets orchestration Start pass).
  5. The phase-close gate holds: 3-consecutive-GREEN cadence + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE=AFTER, with scan-clean teardown covering the new processor-liveness and execution-data keys.
**Plans**: 4 plans
- [x] 28-01-PLAN.md — SourceHash.targets (inline RoslynCodeTaskFactory + two-target emit) + Processor.Sample project skeleton + hermetic reflection/unit facts (IDENT-01/02, SAMPLE-01) — complete 2026-06-02
- [x] 28-02-PLAN.md — Multistage Dockerfile + processor-sample compose tier + ComposeYamlFacts + cross-OS dual-build hash-reproducibility gate (SAMPLE-02; IDENT-02 reproducibility proven cross-OS) — complete 2026-06-02
- [x] 28-03-PLAN.md — Real-stack SampleRoundTripE2ETests (genuine embedded hash, no synthetic liveness seed, truthful liveness-gated Start) (TEST-01) — complete 2026-06-02
- [x] 28-04-PLAN.md — phase-28-close.ps1 (3-GREEN + triple-SHA, steady-state processor-id pre-flight seed) (TEST-02) — complete 2026-06-02 (gate exit 0: 395 facts GREEN x3 + triple-SHA BEFORE==AFTER held)

### Phase 29: Structured Execution-Scope Logging
**Goal**: Every project emits logs as structured attributes only (CorrelationId, WorkflowId, StepId, ProcessorId, ExecutionId, EntryId), carried ambiently via MEL log scopes and serialized by OTel `IncludeScopes` into Elasticsearch — so the full orchestrator→processor→orchestrator round-trip is queryable by any id without interpolating ids into message templates or threading them through method signatures.
**Depends on**: Phase 28 (the full round-trip + both consoles + the `IExecutionCorrelated` contracts must exist to scope)
**Requirements**: LOG-01, LOG-02, LOG-03, LOG-04, LOG-05, LOG-06 (proposed — formalized at spec)
**Success Criteria** (what must be TRUE):
  1. All ids appear as Elasticsearch attributes (`attributes.CorrelationId` / `WorkflowId` / `StepId` / `ProcessorId` / `ExecutionId` / `EntryId`) sourced from log SCOPES (values under fixed keys, never interpolated into message text — T-18-04), via the existing `IncludeScopes` + `ParseStateValues` OTel bridge.
  2. `InboundCorrelationConsumeFilter` is unchanged (still scopes `CorrelationId` for all messages); a new bus-wide open-generic `InboundExecutionScopeConsumeFilter` scopes the execution id-set for `IExecutionCorrelated` messages and passes through all others, registered in `AddBaseConsoleMessaging` so BOTH the orchestrator (`ResultConsumer` ← `ExecutionResult`) and the processor (`EntryStepDispatchConsumer` ← `EntryStepDispatch`) are covered with no per-console wiring.
  3. A shared `ExecutionLogScope` keys class in `Messaging.Contracts` is the single source of truth, its key strings equal to the structured-param names (`{WorkflowId}` …) so scope-derived and param-derived attributes coincide on the same field; `Guid.Empty` values are skipped (no zero-guid noise attributes).
  4. The processor's per-result minted `ExecutionId` + output `EntryId` are captured via a nested `BeginScope` in `EntryStepDispatchConsumer` (overriding the inbound values for the write/send lines), and `ProcessorId` enriches ALL processor logs (startup, heartbeat, consume) via an OTel `LogRecord` enricher reading `IProcessorContext.Id` (null-safe before identity resolves).
  5. `WorkflowFireJob` (a Quartz job, outside the consume pipeline) opens an explicit `BeginScope(CorrelationId + WorkflowId)` in `Execute` so its fire logs correlate with the round-trip it triggers; the full hermetic + real-stack suite stays GREEN with no log-shape regression and the close-gate triple-SHA still holds.
**Plans**: 5 plans
- [x] 29-01-PLAN.md - ExecutionLogScope keys class in Messaging.Contracts + key-pin test (LOG-03) — 2026-06-02
- [x] 29-02-PLAN.md - InboundExecutionScopeConsumeFilter (5 ids, Guid.Empty-skip, non-IExecutionCorrelated no-op) + bus-wide registration + hermetic probe test (LOG-01/02/03) — 2026-06-02
- [x] 29-03-PLAN.md - ProcessorId LogRecord enricher (null-safe, processor-side only) + nested BeginScope for minted ExecutionId+EntryId in EntryStepDispatchConsumer + tests (LOG-01/04) — 2026-06-02
- [x] 29-04-PLAN.md - WorkflowFireJob explicit BeginScope(CorrelationId + WorkflowId) + hermetic test (LOG-01/05)
- [x] 29-05-PLAN.md - Real-stack E2E scope-sourced processor-side proof (L1 trap closed) + scripts/phase-29-close.ps1 close gate (LOG-01/06)

### Phase 30: Runtime & Business Metrics
**Goal**: Every service emits code-defined metrics carrying a per-replica `service_instance_id` label so that, across multiple orchestrator/processor replicas, PromQL can measure the rate of orchestrator→processor dispatch *sending* vs processor *consuming* (the per-processor bottleneck) and per-processor outcome rates — without high-cardinality workflow labels and without collector-side metric config.
**Depends on**: Phase 29 (the full round-trip + both consoles in place; the new counters instrument the same send/consume sites the logging phase scoped)
**Requirements**: METRIC-01, METRIC-02, METRIC-03, METRIC-04, METRIC-05, METRIC-06, METRIC-07 (proposed — formalized at spec)
**Success Criteria** (what must be TRUE):
  1. A `service.instance.id` resource attribute is set **in code** in BOTH base libs (`BaseApi.Core` `ObservabilityServiceCollectionExtensions` + `BaseConsole.Core` `BaseConsoleObservabilityExtensions`) from the pod identity (`POD_NAME`/`HOSTNAME` env, GUID fallback off-cluster); every emitted metric (runtime, HTTP, business) carries a uniform `service_instance_id` Prometheus label per replica — and the `otel-collector` metrics pipeline is NOT modified to add it (the existing generic `resource_to_telemetry_conversion` forwards it).
  2. All three process types (WebApi, Orchestrator, every `Processor.*`) emit .NET runtime metrics (existing `AddRuntimeInstrumentation`), and the WebApi emits ASP.NET Core HTTP server metrics (existing `AddAspNetCoreInstrumentation`) — all now carrying `service_instance_id`.
  3. The Orchestrator defines a code-owned `Meter` with two monotonic counters — `orchestrator_dispatch_sent_total` (at the `EntryStepDispatch` send) and `orchestrator_result_consumed_total` (in `ResultConsumer`) — each labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter`; **no** `workflowId` label.
  4. `BaseProcessor.Core` defines a code-owned `Meter` with `processor_dispatch_consumed_total` (on consuming `EntryStepDispatch`) and `processor_result_sent_total` (per `ExecutionResult` sent, labelled by `outcome` ∈ {completed, failed, cancelled}) — both labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter` so every `Processor.*` inherits them; **no** `workflowId` label; the in-flight "processing" outcome is deferred.
  5. The counters align by `ProcessorId` so PromQL `sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total[…])) − sum by (ProcessorId)(rate(processor_dispatch_consumed_total[…]))` quantifies per-processor dispatch backlog across replicas, and per-outcome rates are queryable — proven by a real-stack assertion that the new series appear in Prometheus with the expected `ProcessorId` / `outcome` / `service_instance_id` labels after a live round-trip.
**Plans**: 4 plans (2 waves)
- [x] 30-01-PLAN.md — service.instance.id resource attr in both base libs + PrometheusTestClient/ResolveInstanceId scaffolding (METRIC-01/02/03/07) — 2026-06-02
- [x] 30-02-PLAN.md — Orchestrator counters: dispatch_sent + result_consumed keyed by ProcessorId (METRIC-04) — 2026-06-02
- [x] 30-03-PLAN.md — BaseProcessor.Core counters: dispatch_consumed + result_sent{outcome} via firewall-correct meter seam (METRIC-05) — 2026-06-02
- [x] 30-04-PLAN.md — RealStack MetricsRoundTripE2ETests: Prometheus series + by-ProcessorId bottleneck PromQL (METRIC-06; live proof of 01..05; 07 gate) — 2026-06-02

### Phases 31-32.1 (v3.6.0 — SHIPPED 2026-06-05)

Full phase details (31, 31.1, 32→32.1), success criteria, plans, decisions, and the cancelled-circuit-breaker revert rationale are archived to [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md). Requirements: [milestones/v3.6.0-REQUIREMENTS.md](milestones/v3.6.0-REQUIREMENTS.md). Audit: [milestones/v3.6.0-MILESTONE-AUDIT.md](milestones/v3.6.0-MILESTONE-AUDIT.md).

### Phase 33: Fault-Recovery Spike (De-Risk)
**Goal**: Prove the load-bearing assumption of the whole milestone — that a published `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` event can be consumed by an external subscriber, the original message + correlation extracted from `Fault<T>.Message`, re-injected to its origin endpoint by type, and silently collapsed by the receiver's surviving Phase-31 `flag[H]` dedup — before committing to the full Keeper build.
**Depends on**: — (first phase of the milestone)
**Requirements**: INTAKE-01, INTAKE-02, INTAKE-04, PROBE-06
**Success Criteria** (what must be TRUE):
  1. A spike consumer binding `IConsumer<Fault<EntryStepDispatch>>` and `IConsumer<Fault<ExecutionResult>>` receives the fault events the processor/orchestrator publish on retry-budget exhaustion — via pub/sub, no per-`{procId}_error`-queue binding — while `Fault<StartOrchestration>`/`Fault<StopOrchestration>` are demonstrably NOT delivered.
  2. From `Fault<T>.Message` the spike extracts the original message + full 6-id `IExecutionCorrelated` tuple (correlationId, workflowId, stepId, processorId, entryId, executionId) + `H`, proving the inner-message shape is reachable even though `Fault<T>` is not itself `IExecutionCorrelated`.
  3. The extracted message is re-injected directly to its origin endpoint by type (`queue:{processorId:D}` for dispatch, `queue:orchestrator-result` for result) — no orchestrator round-trip — and the downstream effect happens exactly once.
  4. A deliberately duplicated re-inject collapses at the receiver via its existing `flag[H]` gate with no second downstream effect (Keeper needs no dedup of its own). The `_error`-retention decision (TTL'd-forensic vs suppress) is recorded.
**Plans**: 2 plans
- [x] 33-01-PLAN.md — Author FaultRecoverySpikeE2ETests (clone rig + 6 grafts: dual Fault<T> capture, double-.Message unwrap, WRONGTYPE dispatch+result trips, a-priori H, verbatim re-inject x2, negative command-fault proof); hermetic compile + zero-regression gate [autonomous] ✓ Release 0/0, hermetic 447/0 RealStack-excluded
- [~] 33-02-PLAN.md — phase-33-close.ps1 (clone phase-32.1-close) + record D-10 _error decision + operator runbook for the live trip/recover/re-inject/collapse + close gate [autonomous:false] — AUTHORED + committed (`26e174a`); LIVE close gate pending operator (GATE_EXIT=0)

### Phase 34: Keeper Console Foundation
**Goal**: Stand up a runnable, multi-replica `Keeper` console on `BaseConsole.Core` (mirroring `Orchestrator`) that builds, containerizes, and joins the compose stack as a healthy tier with work load-balanced across replicas.
**Depends on**: Phase 33
**Requirements**: KEEP-01, KEEP-02, KEEP-03
**Success Criteria** (what must be TRUE):
  1. A `Keeper` console exists on `BaseConsole.Core` (Generic-Host, metrics-only OTel, soft-dep Redis, embedded health probes, MassTransit/RabbitMQ, inherited correlation filters) with a minimal `Program.cs` mirroring `Orchestrator`.
  2. Keeper runs multi-replica with fault work bound to a shared competing-consumer endpoint (not instance-unique fan-out); RabbitMQ round-robins fault events across replicas.
  3. Keeper builds clean (Release+Debug, 0 warnings) and containerizes via a multi-stage Dockerfile.
  4. Keeper joins the compose stack as a new healthy tier alongside `orchestrator` / `processor-sample` (health probes report ready live).
**Plans**: 3 plans
  - [x] 34-01-PLAN.md — KeeperQueues const + Keeper.csproj + SK_P.sln registration (KEEP-01 foundation)
  - [x] 34-02-PLAN.md — Keeper console body: placeholder message/consumer/definition + Program.cs + appsettings + Dockerfile (KEEP-01/02)
  - [x] 34-03-PLAN.md — compose keeper tier + ComposeYamlFacts + 4 hermetic Keeper tests + live operator smoke (KEEP-01/02/03)

### Phase 35: Fault Intake & Correlation
**Goal**: Wire the production fault-intake path — consuming the two execution-path `Fault<T>` events, extracting the inner message + 6-id correlation + `H`, opening the propagated execution log-scope, and confirming the `_error` record consolidates into the TTL'd forensic DLQ-1 (never Keeper's worklist).
**Depends on**: Phase 34
**Requirements**: INTAKE-03, KMET-04
**Success Criteria** (what must be TRUE):
  1. The transport-exhaustion record consolidates into DLQ-1 (TTL'd forensic) — Keeper recovers off the `Fault<T>` pub/sub events, never reads the error/DLQ-1 queue, and recovered work is never double-processed from it.
  2. On every consumed fault, Keeper opens the execution log-scope from the extracted inner message so its OTel logs carry the propagated correlationId + execution-scope ids (consistent with the other consoles).
  3. A faulted message processed by Keeper produces an Elasticsearch log correlated to the original execution by correlationId + ids (observable end-to-end).
**Plans**: 3 plans
- [x] 35-01-PLAN.md — Shared ExecutionLogScope.BuildState refactor (D-07), byte-identical + regression-guarded
- [x] 35-02-PLAN.md — Two real Fault<T> consumers + definitions + Program.cs swap + placeholder deletion + hermetic scope proof (completed 2026-06-05 — KeeperFaultConsumerScopeTests 3/3 GREEN proving CorrelationId + 5 exec ids; both defs on keeper-fault-recovery, single retry owner; 3 placeholders deleted; SK_P.sln 0/0 Release; hermetic suite 457/0; b71233c, 418bc3f)
- [x] 35-03-PLAN.md — RealStack SC3: running-Keeper-container correlated-ES-log proof (authored — KeeperFaultIntakeE2ETests sibling clone of the Phase-33 spike: WRONGTYPE live-trip → running-Keeper-container correlated ES log on service.name=keeper + attributes.CorrelationId == tripped correlationId + attributes.StepId + body.text ~ "keeper fault intake"; net-zero teardown; no DLQ-1/TTL scope creep; SK_P.sln 0/0 Release; hermetic suite unchanged; 1b64143. **LIVE SC3 run OPERATOR-PENDING** — not observed this session; runbook in 35-03-SUMMARY; INTAKE-03/KMET-04 stay unticked until the operator's GREEN live run)

### Phase 36: L2 Health-Probe Recovery Loop & DLQs
**Goal**: Implement the core recovery engine — a bounded, crash-survivable L2 read+write probe loop that re-injects to origin on first success or parks the unrecoverable in `keeper-dlq` (DLQ-2) on give-up — plus the two-DLQ topology (Immediate(N) exhaustion → DLQ-1; probe exhaustion → DLQ-2) and the shared `Immediate(N)` policy across all consumers.
**Depends on**: Phase 35
**Requirements**: PROBE-01, PROBE-02, PROBE-03, PROBE-04, PROBE-05, DLQ-01, DLQ-02, DLQ-03, DLQ-04
**Success Criteria** (what must be TRUE):
  1. On intake Keeper runs a recovery loop with config-driven inter-attempt delay (s) and max-attempts, each iteration probing L2 by reading `skp:data:{entryId}` AND write-then-deleting a scratch key (read + write, not mere connectivity); `delay × attempts` is documented as bounded under RabbitMQ's delivery-ack timeout.
  2. On the first successful probe Keeper exits and triggers recovery (re-inject + resume); when max-attempts exhaust it parks the original message in `keeper-dlq` (DLQ-2) and exits.
  3. The fault message is acked only after the loop exits (success or give-up); killing Keeper mid-loop leaves it un-acked → redelivered → loop restarts, with no lost message (at-least-once recovery observable).
  4. Two DLQs exist split by mechanism: DLQ-1 (all `Immediate(N)` transport exhaustions, consolidated across processor/orchestrator/Keeper, TTL'd forensic) and DLQ-2 `keeper-dlq` (probe give-ups); `keeper-dlq` depth is the primary Prometheus alert; on give-up the workflow stays paused (no auto-resume).
  5. All consumers (processor dispatch, orchestrator result/start/stop, Keeper) use the same `Immediate(N)` bound from the shared `RetryOptions` appsettings, routed uniformly to DLQ-1 (same pattern across consoles).
**Plans**: TBD

### Phase 37: Orchestrator Pause/Resume Coordination
**Goal**: Add the `PauseWorkflow`/`ResumeWorkflow` contracts and the orchestrator-side consumers that halt and reschedule a workflow's cron off an in-memory L1 pending-recovery set keyed by `H` — keeping the workflow paused while any recovery is in flight (or any message is given up), idempotent under duplicate/concurrent signals.
**Depends on**: Phase 36 (re-inject lives in Keeper per INTAKE-04; this is the cron-scheduling half)
**Requirements**: PAUSE-01, PAUSE-02, PAUSE-03, PAUSE-04, PAUSE-05
**Success Criteria** (what must be TRUE):
  1. New `PauseWorkflow` and `ResumeWorkflow` contracts exist in `Messaging.Contracts`, fanned from Keeper to the orchestrator (cron scheduling only; re-injection stays in Keeper).
  2. On the first in-flight recovery for a workflow the orchestrator halts its future cron fires (`UnscheduleOnlyAsync`, L1 preserved), tracking pause-state as a per-workflow pending-recovery set keyed by `H` in its single-replica in-memory L1 — never in L2.
  3. When no recoveries remain pending for a workflow, the orchestrator reschedules it from L1 (`ScheduleAsync` with the L1 root's `jobId` + `cron`) and future cron fires resume.
  4. Duplicate/concurrent pause/resume signals (Keeper crash/redelivery, multiple replicas) are serialized by the orchestrator's per-`workflowId` semaphore, keyed idempotently by `H`, and do not double-count.
  5. A workflow with ≥1 given-up (unrecovered) message remains paused until an operator intervenes; the orchestrator does not auto-resume it.
**Plans**: TBD

### Phase 38: Keeper Observability + Real-Stack E2E + Close Gate
**Goal**: Register the Keeper meter + throughput/saturation instruments, then prove the full recover-and-give-up behavior live against the real stack and lock a 3×GREEN triple-SHA net-zero close gate.
**Depends on**: Phase 37
**Requirements**: KMET-01, KMET-02, KMET-03, TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):
  1. A code-defined `Keeper` meter is registered per the house pattern (snake_case, no `_total` suffix, inherited `service_instance_id`).
  2. Throughput/outcome counters (`keeper_fault_consumed`, `keeper_recovered`, `keeper_dlq_pushed{reason}`, `keeper_workflow_paused`, `keeper_workflow_resumed`, `keeper_l2_probe_failed`) and saturation/latency signals (`keeper_in_flight` UpDownCounter, `keeper_recovery_duration` histogram) are emitted and Prometheus-scrapable, labeled by `processorId` where meaningful with no high-cardinality `workflowId`.
  3. A real-stack E2E induces an L2 outage that dead-letters both an `EntryStepDispatch` and an `ExecutionResult`, then proves Keeper pauses the workflow, recovers on L2 return, resumes, and re-injects each to origin with exactly-once downstream effect (no duplicate).
  4. A real-stack E2E proves the give-up path: L2 stays down past max-attempts → message in `keeper-dlq`, workflow stays paused, `keeper_dlq_pushed` increments.
  5. The close gate runs 3× consecutive GREEN with triple-SHA (psql `\l` / redis `--scan` / rabbitmqctl `list_queues`) BEFORE==AFTER — including both DLQs + probe scratch-key scan-clean (net-zero) — at Release+Debug 0-warning.
**Plans**: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 25 → 26 → 27 → 28 → 29 → 30 → 31 → 31.1 → 32 → 32.1 → 33 → 34 → 35 → 36 → 37 → 38

| Phase | Milestone | Plans Complete | Status   | Completed  |
| ----- | --------- | -------------- | -------- | ---------- |
| 1-11  | v3.2.0    | 41/41          | Complete | 2026-05-28 |
| 12-16 | v3.3.0    | 26/26          | Complete | 2026-05-29 |
| 17    | v3.4.0    | 2/2            | Complete | 2026-05-30 |
| 18    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 19    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 20    | v3.4.0    | 4/4            | Complete | 2026-05-31 |
| 21    | v3.4.0    | 1/1            | Complete | 2026-05-31 |
| 22    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 23    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 24    | v3.4.0    | 5/5            | Complete | 2026-06-01 |
| 24.1  | v3.4.0    | 1/1            | Complete | 2026-06-01 |
| 25. Shared Contracts + WebApi Responders | v3.5.0 | 2/2 | Complete    | 2026-06-01 |
| 26. BaseProcessor.Core — Library, Identity & Liveness | v3.5.0 | 3/3 | Complete    | 2026-06-01 |
| 27. Execution Round-Trip | v3.5.0 | 3/3 | Complete | 2026-06-02 |
| 28. SourceHash Identity + Processor.Sample + E2E Closeout | v3.5.0 | 4/4 | Complete    | 2026-06-02 |
| 29. Structured Execution-Scope Logging | v3.5.0 | 5/5 | Complete    | 2026-06-02 |
| 30. Runtime & Business Metrics | v3.5.0 | 4/4 | Complete    | 2026-06-02 |
| 31. Idempotent Execution Round-Trip (Exactly-Once-Effect) | v3.6.0 | 6/6 | Complete    | 2026-06-04 |
| 31.1 Close-Gate Redis Net-Zero (gap closure) | v3.6.0 | 1/1 | Complete | 2026-06-04 |
| 32. Cancelled Circuit-Breaker | v3.6.0 | 5/6 | Superseded by 32.1 | — |
| 32.1 Dead-Letter on Exhaustion (Breaker Reverted) | v3.6.0 | 2/2 | Complete    | 2026-06-05 |
| 33. Fault-Recovery Spike (De-Risk) | v3.7.0 | 2/2 | Complete    | 2026-06-05 |
| 34. Keeper Console Foundation | v3.7.0 | 3/3 | Complete    | 2026-06-05 |
| 35. Fault Intake & Correlation | v3.7.0 | 3/3 | Complete    | 2026-06-05 |
| 36. L2 Health-Probe Recovery Loop & DLQs | v3.7.0 | 0/? | Not started | — |
| 37. Orchestrator Pause/Resume Coordination | v3.7.0 | 0/? | Not started | — |
| 38. Keeper Observability + Real-Stack E2E + Close Gate | v3.7.0 | 0/? | Not started | — |

---
*v3.2.0 shipped 2026-05-28 (11 phases). v3.3.0 shipped 2026-05-29 (5 phases, Orchestration L3→L1→L2 build pipeline). v3.4.0 shipped 2026-06-01 (9 phases 17-24+24.1, BaseConsole + Orchestrator Messaging). v3.5.0 shipped 2026-06-02 (6 phases 25-30, Processor Console — `BaseProcessor.Core` + `Processor.Sample`, assembly-embedded SourceHash, WebApi bus responders, L2 liveness self-registration, live execution round-trip + runtime/business metrics) — note: formal archival (ROADMAP/MILESTONES/tag) deferred. v3.6.0 shipped 2026-06-05 (4 phases 31-32.1, Idempotent Execution — exactly-once-effect round-trip via deterministic `H` + effect-first `flag[H]` dedup at both hops; cancelled circuit-breaker built then reverted to plain dead-lettering). Next milestone planning begins with `/gsd-new-milestone`.*
