# Roadmap: Steps API

## Milestones

- ✅ **v3.2.0 Steps API MVP** — Phases 1-11 (shipped 2026-05-28) — see [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md)
- ✅ **v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline** — Phases 12-16 (shipped 2026-05-29) — see [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md)
- ✅ **v3.4.0 BaseConsole + Orchestrator Messaging** — Phases 17-24 + 24.1 (shipped 2026-06-01) — see [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md)
- ✅ **v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip** — Phases 25-30 (shipped 2026-06-02)
- 🚧 **v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip** — Phase 31+ (planning)

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

## 🚧 v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip (Planning)

**Milestone Goal:** Make the orchestrator↔processor execution round-trip **exactly-once-effect** under at-least-once delivery — so `Immediate(3)` retries, broker redeliveries, publish-confirm ambiguity, the orchestrator's own re-dispatch, and fan-in/merge all stop producing duplicate downstream execution, with zero lost branches. Achieved by **deterministic content-addressed identity + effect-first receiver dedup**, not producer-side detection (which is provably impossible — confirmation is lossy in one direction).

- [x] **Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect)** — Deterministic per-fire identity `H = hash(correlationId, workflowId, stepId, processorId, EntryId)` (executionId excluded, lineage only); content-addressed two-level L2 data (`data[hash(result)]` blobs + `data[hash(manifest)]` manifest); symmetric effect-first `flag[H] = Pending|Ack` dedup via atomic CAS on every send/receive (processor inbound dispatch + orchestrator inbound result); merge correctness + no-override via input `EntryId` (identical-input collapse); manifest fan-out (successor stepId/processorId, regenerated executionId); configurable retry count (default `Immediate(N)`). Reworks `EntryStepDispatchConsumer` + orchestrator `ResultConsumer`/advancement; extends the wire contracts + `L2ProjectionKeys`. **SPEC locked (8 requirements)** — see [31-SPEC.md](phases/31-idempotent-execution-exactly-once-effect/31-SPEC.md) / [31-CONTEXT.md](phases/31-idempotent-execution-exactly-once-effect/31-CONTEXT.md). (spec'd 2026-06-04) (completed 2026-06-04)
- [x] **Phase 31.1: Close-Gate Redis Net-Zero (gap closure)** — Remediate NET-ZERO-31 (deferred from Phase 31, see [31-VERIFICATION.md](phases/31-idempotent-execution-exactly-once-effect/31-VERIFICATION.md)): RealStack E2E tests leave their cron `* * * * *` workflows firing in `sk-orchestrator`, churning per-fire `skp:flag:{H}` key names so the close-gate redis `--scan` triple-SHA never settles. Add stop-workflow-in-teardown across the 5 RealStack E2E tests (`POST /orchestration/stop` → `UnscheduleOnlyAsync`), a close-gate settle-drain before the AFTER snapshot, and a one-time purge of leaked in-memory Quartz jobs (RAMJobStore restart). The permanent-flag-key production bug was already fixed in Phase 31 (`keepTtl`). See [31.1-PLAN.md](phases/31.1-close-gate-net-zero/31.1-PLAN.md). (planned 2026-06-04)
- [ ] **Phase 32: Cancelled Circuit-Breaker** — On retry-budget exhaustion the processor emits `Cancelled` (sends the consumed message back) and performs a **two-level stop**: sets a `cancelled[workflowId]` marker in L2 (every receiver checks-and-drops in-flight messages → current fire drains to a halt) and signals the orchestrator, which resolves `jobId` from **L1** and unschedules the Quartz job (future fires). `Cancelled` is a terminal stop intercepted before `SelectNext` (advances no successor); **remove `StepEntryCondition.PreviousCancelled` (3)** (leave as gap; validator `IsInEnum` auto-rejects). Trip on first exhaustion (the configurable retry count is the transient-tolerance knob). Design record: the deferred Failure-policy section of [31-CONTEXT.md](phases/31-idempotent-execution-exactly-once-effect/31-CONTEXT.md). (planned 2026-06-04)

### Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect)
**Goal**: Every duplicate inbound message — from `Immediate(3)`, broker redelivery, publish-confirm ambiguity, the orchestrator's own re-dispatch, or a fan-in/merge re-dispatch — reproduces the same deterministic identity and is collapsed at the receiving node, so each step's downstream effect happens exactly once with no lost branch; the processor business transform (`ProcessAsync`) needs no idempotency logic.
**Depends on**: Phase 30 (the full v3.5.0 round-trip + both consoles + metrics in place; Phase 31 reworks the Phase 27 consumer + the Phase 24 advancement)
**Requirements**: IDEM-* (formalized at spec) — deterministic identity, content-addressed data, effect-first CAS dedup, merge/fan-out correctness, Cancelled circuit-breaker.
**Success Criteria** (what must be TRUE — to be sharpened at spec):
  1. The dedup identity `H = hash(correlationId, workflowId, stepId, processorId, EntryId)` is deterministic (correlationId = per-fire job id; EntryId = `hash(data)`; executionId excluded), so a retry/redelivery/re-dispatch of the same logical message yields the same `H`.
  2. L2 data is content-addressed at two levels (result blobs + manifest), all writes idempotent; an empty result is a terminal branch.
  3. Receiver dedup is **effect-first**: the downstream effect (send / dispatch) is produced before `flag[H]` is CAS-flipped `Pending → Ack`, so a crash in the window yields a *collapsed duplicate*, never a lost branch; the flip is atomic (no concurrency double-process).
  4. A merge step's per-edge executions are distinguished by their input `EntryId` (no false dedup, no output override); the merge is per-edge, not a join.
  5. A live real-stack proof: the dual-pipeline workflow under the **merge** topology, run across multiple cron fires plus an induced `Immediate(N)`/redelivery, shows in ES **exactly the expected per-fire effect set with no extra downstream execution** (the inverse of the `StepB4`-×2 duplicate).
**Plans**: 6 plans (4 waves) — planned 2026-06-04. **SPEC locked** — see 31-SPEC.md (8 requirements, ambiguity 0.13).
  - [x] 31-01-PLAN.md — shared hash helper + L2 key builders + RetryOptions + golden tests (req-1, req-7) [wave 1]
  - [x] 31-02-PLAN.md — EntryId Guid→string + H contract ripple, compile-green (req-1, req-2) [wave 2]
  - [x] 31-03-PLAN.md — processor receiver rework: content-addressed two-level write + manifest + effect-first dedup (req-3, req-4) [wave 3]
  - [x] 31-04-PLAN.md — orchestrator inbound dedup + manifest fan-out + entry-step EntryId + sender pre-write (req-2, req-4, req-5, req-6) [wave 3]
  - [x] 31-05-PLAN.md — configurable retry: 4 sites bound from RetryOptions per process (req-7) [wave 2]
  - [x] 31-06-PLAN.md — live exactly-once E2E (merge topology + induced retry) + phase-31-close.ps1 (req-8) [wave 4]

### Phase 32: Cancelled Circuit-Breaker
**Goal**: On infra-fault retry-budget exhaustion (`GetRetryAttempt() == RetryOptions.Limit`), a workflow is cleanly and completely stopped — the in-flight fire drains to a halt and future cron fires are unscheduled — via a two-level stop (an L2 `cancelled[workflowId]` marker plus a fanned-out `Fault<EntryStepDispatch>` that unschedules the Quartz job), instead of silently dead-lettering to `_error`. NOTE: criteria #1/#3 below are SUPERSEDED by the Fault-fanout design (CONTEXT D-03/D-04/D-06) — the breaker fans out via MassTransit `Fault<EntryStepDispatch>`, NOT a point-to-point `Cancelled` send; SPEC + CONTEXT are the source of truth.
**Depends on**: Phase 31 (deterministic `H` dedup + configurable retry must exist; `Cancelled` is deduped via `H`, and the final-attempt handler reads the configured retry limit)
**Requirements**: req-1..req-8 (formalized in 32-SPEC.md, ambiguity 0.11)
**Success Criteria** (what must be TRUE — to be sharpened at spec):
  1. On `GetRetryAttempt() == configured Limit`, the processor sends the consumed message back as `Cancelled` (not dead-lettered) and sets `cancelled[workflowId] = true` in L2 (effect-first: marker → send → ack).
  2. Every receiver (processor `EntryStepDispatchConsumer`, orchestrator `ResultConsumer`) checks `cancelled[workflowId]` before processing and drops in-flight messages → the current fire drains to a halt (stops advancing, no rollback).
  3. The orchestrator, on `Cancelled`, resolves `jobId` from **L1** (`store.TryGet → wf.JobId`, no L2 read) and unschedules the Quartz job; `Cancelled` is intercepted **before** `SelectNext` (advances no successor, regardless of entry condition).
  4. `StepEntryCondition.PreviousCancelled (3)` is removed (left as a numeric gap; `IsInEnum` validator auto-rejects 3); no live step uses `EntryCondition == 3`.
  5. Open decisions resolved at spec: trip-on-first-exhaustion (chosen) blast radius (whole-workflow); the resume path (clear marker + re-Start); `_error` retained as the backstop for bus-down exhaustion.
**Plans**: 7 plans (4 waves) — planned 2026-06-04. **SPEC locked** — see 32-SPEC.md (8 requirements, ambiguity 0.11).
  - [x] 32-01-PLAN.md — Wave-0 MassTransit-reality probes: RetryAttemptNumberingFacts (R1) + FaultConsumerBindingFacts (R2) (req-1, req-4) [wave 0]
  - [x] 32-02-PLAN.md — additive foundation: L2ProjectionKeys.Cancelled marker builder + 3 observability counters (req-2, req-7) [wave 1]
  - [~] 32-03-PLAN.md — enum cleanup: CANCELLED BY USER (D-12 reversed — PreviousCancelled (3) kept; StepOutcome.Cancelled (3) unchanged) (req-6) [wave 1]
  - [ ] 32-04-PLAN.md — processor breaker seam: final-attempt marker-set + trip counter/log + check-and-drop + dedup counter (req-1, req-2, req-3, req-7) [wave 2]
  - [ ] 32-05-PLAN.md — orchestrator check-and-drop gate + dedup counter (req-3, req-7) [wave 2]
  - [ ] 32-06-PLAN.md — Fault<EntryStepDispatch> fan-out consumer → Quartz unschedule, idempotent (req-4, req-8) [wave 2]
  - [ ] 32-07-PLAN.md — real-stack breaker/halt/resume E2E + phase-32-close.ps1 (no-TTL skp:cancelled:* scan-clean) (req-5, req-6, req-8) [wave 3]

## Progress

**Execution Order:**
Phases execute in numeric order: 25 → 26 → 27 → 28 → 29 → 30 → 31

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
| 32. Cancelled Circuit-Breaker | v3.6.0 | 0/TBD | Planning | — |

---
*v3.2.0 shipped 2026-05-28 (11 phases). v3.3.0 shipped 2026-05-29 (5 phases, Orchestration L3→L1→L2 build pipeline). v3.4.0 shipped 2026-06-01 (9 phases 17-24+24.1, BaseConsole + Orchestrator Messaging). v3.5.0 STARTED 2026-06-01 (4 phases 25-28, Processor Console — `BaseProcessor.Core` + `Processor.Sample`, assembly-embedded SourceHash, WebApi bus responders, L2 liveness self-registration, live execution round-trip; build order 25→26→27→28). 38/38 requirements mapped.*
