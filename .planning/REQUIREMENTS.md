# Requirements: Steps API — Milestone v3.5.0

**Defined:** 2026-06-01
**Milestone:** v3.5.0 — Processor Console (Self-Registration, Liveness & Execution Round-Trip)
**Core Value:** A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework.

Phase numbering continues from 24 (this milestone starts at **Phase 25**). REQ-IDs are scoped to this milestone.

## v3.5.0 Requirements

### Processor Base Library (`BaseProcessor.Core`)

- [x] **BPC-01
**: `BaseProcessor.Core` is a reusable Generic-Host library built on `BaseConsole.Core` (inheriting soft-dep Redis, embedded health probes, metrics-only OTel, MassTransit/RabbitMQ, and the inbound/outbound correlation filters).
- [x] **BPC-02
**: A new processor is created by subclassing the base and implementing exactly one `abstract` method — no infrastructure, id, L2, or bus code in the concrete.
- [x] **BPC-03
**: An `AddBaseProcessor` composition root wires the startup orchestration (identity loop, liveness worker, dispatch consumer) so a concrete `Program.cs` stays minimal (mirrors `AddBaseConsole`/`Orchestrator`).

### Processor Identity (assembly-embedded SourceHash)

- [x] **IDENT-01**: An MSBuild target (`BeforeTargets=CoreCompile`) computes the implementation SourceHash — SHA-256, lowercase 64-hex, LF-normalized, per-file hashes folded deterministically (ordinal path sort) over `BaseProcessor.Core` + the concrete's `.cs` (excluding generated files, `BaseConsole.Core`, `Messaging.Contracts`).
- [x] **IDENT-02**: The hash is embedded as `[assembly: AssemblyMetadata("SourceHash", …)]` and the target re-runs when implementation source changes (no stale hash on incremental builds).
- [x] **IDENT-03
**: At runtime the processor reads its SourceHash from assembly metadata via reflection.
- [x] **IDENT-04
**: The processor resolves its identity (`Id`, `InputSchemaId?`, `OutputSchemaId?`, `ConfigSchemaId?`) by querying the WebApi over the bus by SourceHash, retrying on failure (timeout / not-found) until it succeeds — booting before the DB row is registered is tolerated.

### Bus Request/Response (WebApi responders)

- [x] **RPC-01
**: WebApi answers a `GetProcessorBySourceHash` bus request, returning the processor identity (`Id` + the three nullable schema Ids) or a not-found response.
- [x] **RPC-02
**: WebApi answers a `GetSchemaDefinition` bus request, returning the schema `Definition` for a given schema Id or a not-found response.
- [x] **RPC-03
**: The two request/response contracts live in `Messaging.Contracts`; WebApi's bus join is extended from publish-only to host these two responders, leaving the CRUD surface unaffected.
- [x] **RPC-04
**: The processor issues both queries via MassTransit `IRequestClient`s (first request/response usage on the console side).

### Schema Resolution

- [x] **SCHEMA-01
**: For each non-null schema Id (input, output) the processor queries the WebApi over the bus for the definition, retrying on failure until resolved.
- [x] **SCHEMA-02
**: Null (optional) schema Ids are skipped — an absent definition is by design, never a failure. (Config schema is not resolved by the processor.)

### Liveness Self-Registration (Redis L2)

- [x] **LIVE-01
**: A background heartbeat worker writes/refreshes `skp:{processorId:D}` every `Interval` seconds with `{ inputDefinition, outputDefinition, liveness{ timestamp, interval, status: "Healthy" } }` — written **only while the replica is Healthy**.
- [x] **LIVE-02
**: Each heartbeat refreshes the liveness `timestamp` and re-applies the configured `Ttl` key expiry (sliding expiration); with N replicas the key is kept fresh by whichever are healthy.
- [x] **LIVE-03
**: The written liveness `interval` equals the configured heartbeat delay (seconds), so the orchestrator's `timestamp + interval×2` staleness check holds.
- [x] **LIVE-04
**: The processor writes the liveness key **only once Healthy** (identity + all required non-null definitions resolved); `status` is always `"Healthy"` when written. A starting / restarting / unhealthy replica does **not** write — to the orchestrator it is `absent`, and the shared key goes `stale` only when *no* replica is healthy. (Optional schema Ids being null is by design, not unhealthy.)
- [x] **LIVE-05
**: The written L2 value shape exactly matches what `ProcessorLivenessValidator` reads; the v3.4.0 validator is reused **unchanged** (absent/stale/malformed → 422). Presence + freshness of `skp:{processorId:D}` therefore means "≥1 replica is currently healthy" — exactly the orchestration-Start admission signal.
- [x] **LIVE-06
**: Multi-replica L2 writes are **lock-free and safe**: execution-data keys are unique per result (no contention); the shared liveness key is a blind whole-value `SET` written only-when-Healthy, so concurrent writes from N replicas are equivalent (same definitions, `status: "Healthy"`, fresh timestamp) and last-write-wins requires no synchronization.

### Shared-Contract Extracts (`Messaging.Contracts`)

- [x] **CONTRACT-01
**: `ProcessorProjection` is made public and relocated to `Messaging.Contracts.Projections` so WebApi and the processor share one source of truth (mirrors the Phase 17/21 extracts).
- [x] **CONTRACT-02
**: `L2ProjectionKeys` gains an `ExecutionData(Guid entryId)` builder producing `skp:data:{entryId:D}`, discriminated from `root`/`step`/`processor` keys.
- [x] **CONTRACT-03
**: The liveness `status` value `"Healthy"` (the only value ever written under Path 1) is defined as a shared constant in `Messaging.Contracts`, so the processor (writer) and any reader cannot desync — same single-source-of-truth discipline as the L2 keys.

### Execution Round-Trip

- [x] **EXEC-01**: The processor consumes `EntryStepDispatch` on a **durable** `queue:{processorId:D}` competing-consumer endpoint. The consumer is bound only once the processor is **Healthy** (definitions resolved), and `Healthy` is written to L2 only after the bind completes — so the orchestrator (which admits only Healthy processors) never sends to a non-existent queue, and the processor never consumes a dispatch it cannot yet validate. The durable queue holds dispatches across processor restarts; a restarting/unhealthy processor leaves them queued (not lost, not processed) until it recovers.
- [x] **EXEC-02
**: Input data is read from `L2[data(entryId)]` (existence-checked first); the dispatch `Payload` is treated as config, never as input data.
- [x] **EXEC-03
**: Input is validated against `inputDefinition` when present; an empty definition skips validation; a non-empty definition with a missing/empty `entryId` (no L2 data) yields a `Failed` result with an error message.
- [x] **EXEC-04
**: The transform is the sole `abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` seam, where `config` = the dispatch `Payload`.
- [x] **EXEC-05
**: For each result the framework validates output against `outputDefinition` when present (empty = valid); on success it mints a new `entryId` and writes the output to `L2[data(newEntryId)]` with the configured TTL; on output-validation failure the result becomes `Failed` and nothing is written.
- [x] **EXEC-06
**: The framework mints a per-result `executionId`, stamps the shared Ids, and builds one `ExecutionResult` per result; concretes write no id/L2/bus code.
- [x] **EXEC-07
**: Results are sent to `queue:orchestrator-result` one-by-one (individual messages in a loop, never a batched list).
- [x] **EXEC-08
**: An empty result list with no exception/cancellation produces no result message (ack only); `Failed` (including a caught exception, with an error message) and `Cancelled` are always sent.
- [x] **EXEC-09
**: The dispatch is acked only after all result sends complete; infra faults (e.g. send failure) throw and retry (`Immediate(3)`), mirroring the orchestrator's business-ack / infra-throw discipline.
- [x] **EXEC-10
**: Correlation is inherited from `BaseConsole.Core` — the body `CorrelationId` flows from the dispatch into the log scope and onto every published `ExecutionResult`.

### Sample Concrete

- [x] **SAMPLE-01**: `Processor.Sample` is the first concrete console (family convention `Processor.<Purpose>`), implementing `ProcessAsync` with a minimal POC dummy result list.
- [x] **SAMPLE-02**: `Processor.Sample` ships a multistage Dockerfile and joins the compose stack (mirroring the Orchestrator tier).

### Configuration

- [x] **CONFIG-01
**: Liveness `Interval` (seconds) and `Ttl` (seconds) are two independent appsettings values.
- [x] **CONFIG-02
**: Execution-data L2 keys have their own configurable TTL (seconds).

### Testing & Closeout

- [x] **TEST-01**: A real-stack E2E proves the live orchestrator→`Processor.Sample`→orchestrator round-trip (dispatch consumed, output written to L2, `ExecutionResult` advanced by the orchestrator) and the liveness-gated Start path (a live processor's heartbeat lets orchestration Start pass).
- [x] **TEST-02**: The phase-close gate retains the 3-consecutive-GREEN cadence + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE=AFTER discipline, with scan-clean teardown covering the new processor-liveness and execution-data keys.

### Structured Execution-Scope Logging (Phase 29 — v3.5.0 follow-up)

- [x] **LOG-01
**: All six execution ids surface in Elasticsearch as `attributes.CorrelationId` / `WorkflowId` / `StepId` / `ProcessorId` / `ExecutionId` / `EntryId`, sourced from log **scopes** (fixed-key values, never interpolated into message text — T-18-04), via the existing `IncludeScopes`+`ParseStateValues` OTel bridge reused unchanged.
- [x] **LOG-02
**: A new open-generic `InboundExecutionScopeConsumeFilter<T>` scopes the execution id-set for `IExecutionCorrelated` messages and passes through all others; registered once bus-wide in `AddBaseConsoleMessaging` so both the orchestrator (`ResultConsumer`) and processor (`EntryStepDispatchConsumer`) are covered with no per-console wiring. `InboundCorrelationConsumeFilter` is left byte-unchanged.
- [x] **LOG-03
**: A single `ExecutionLogScope` constants class in `Messaging.Contracts` (pure POCO leaf, no MassTransit ref) is the source of truth for scope keys, with key strings equal to the structured-param names so scope-derived and param-derived attributes coincide on the same ES field; `Guid.Empty` values are skipped (no zero-guid noise attributes).
- [x] **LOG-04
**: The per-result minted `ExecutionId` + output `EntryId` are captured via a nested `BeginScope` in `EntryStepDispatchConsumer` (overriding inbound values for the write/send log lines), and `ProcessorId` enriches ALL processor logs via an OTel `LogRecord` enricher reading `IProcessorContext.Id` (null-safe — emits nothing before identity resolves).
- [x] **LOG-05
**: `WorkflowFireJob` (a Quartz job, outside the consume pipeline) opens an explicit `BeginScope(CorrelationId + WorkflowId)` in `Execute` so its fire logs correlate with the round-trip it triggers.
- [x] **LOG-06
**: No log-shape regression (existing templates untouched, additive scopes only); the full hermetic + real-stack suite stays GREEN and the close-gate triple-SHA still holds; proof bar = hermetic scope-capture tests PLUS one extension of the existing real-stack E2E asserting ≥1 **scope-sourced, processor-side** execution id round-trips to ES.

### Runtime & Business Metrics (Phase 30 — v3.5.0 follow-up)

- [x] **METRIC-01
**: A `service.instance.id` resource attribute is set **in application code** in BOTH base libs (`BaseApi.Core.ObservabilityServiceCollectionExtensions` + `BaseConsole.Core.BaseConsoleObservabilityExtensions`), resolved from the pod identity (`POD_NAME`/`HOSTNAME` env, GUID fallback off-cluster), so every emitted metric (runtime, HTTP, business) carries a uniform `service_instance_id` Prometheus label AND every log carries `resource.attributes.service.instance.id` in Elasticsearch (all logs, no exceptions) per replica — with NO `otel-collector` metrics-config change (the existing generic `resource_to_telemetry_conversion` forwards it). In k8s the value is the pod **name**.
- [x] **METRIC-02
**: All three process types (WebApi, Orchestrator, every `Processor.*`) emit .NET runtime metrics via the base libs' existing `AddRuntimeInstrumentation`, now carrying the `service_instance_id` label.
- [x] **METRIC-03
**: The WebApi's ASP.NET Core HTTP server metrics (existing `AddAspNetCoreInstrumentation`; health routes still collector-filtered) carry the `service_instance_id` label.
- [x] **METRIC-04**: The Orchestrator defines a code-owned `Meter` with two monotonic counters — `orchestrator_dispatch_sent_total` (incremented at the `EntryStepDispatch` send site) and `orchestrator_result_consumed_total` (incremented in `ResultConsumer`) — each labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter`. No `workflowId` label (high-cardinality).
- [ ] **METRIC-05**: `BaseProcessor.Core` defines a code-owned `Meter` with `processor_dispatch_consumed_total` (incremented on consuming an `EntryStepDispatch`) and `processor_result_sent_total` (incremented per `ExecutionResult` sent, labelled by `outcome` ∈ {completed, failed, cancelled}) — both labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter` so every `Processor.*` inherits them. No `workflowId` label; the in-flight "processing" outcome is deferred.
- [ ] **METRIC-06**: The send/consume counters align by `ProcessorId` so PromQL `sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total[…])) − sum by (ProcessorId)(rate(processor_dispatch_consumed_total[…]))` quantifies per-processor dispatch backlog/bottleneck across replicas, and `rate(processor_result_sent_total{outcome=…})` gives per-processor outcome rates — proven by a real-stack assertion that the new series appear in Prometheus with the expected `ProcessorId` / `outcome` / `service_instance_id` labels after a live round-trip.
- [x] **METRIC-07
**: Metric definitions, names, and labels are owned entirely in application code (`System.Diagnostics.Metrics` + code-set resource attributes); the `otel-collector` metrics pipeline is NOT modified to inject labels or define instruments (it stays a generic OTLP→Prometheus bridge).

## Future Requirements

Deferred to later milestones. Tracked, not in this roadmap.

### Processor Family

- **FUT-PROC-01**: Additional real concretes (`Processor.<Purpose>`) with genuine (non-dummy) transform logic.
- **FUT-PROC-02**: Execution-data key eviction / cleanup-on-read strategy (beyond TTL).
- **FUT-PROC-03**: Step-to-step output-data forwarding on the wire (would extend the result contract beyond outcome-only).
- **FUT-PROC-04**: Execution dispatch TTL / dead-letter / alerting for permanently-stuck steps (a workflow pauses indefinitely if its processor never recovers; no timeout exists today).

## Out of Scope

Explicitly excluded this milestone. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Real (non-dummy) transform logic | POC milestone — proves the pattern; `Processor.Sample` returns a dummy list. |
| Step-to-step output-data on the wire | `ExecutionResult` stays outcome-only by design; data flows via L2 keyed by `entryId`. |
| Config re-validation in the processor | `Payload`/config was already config-schema-validated at orchestration Start (Phase 14). |
| Execution-data eviction / cleanup-on-read | Rely on the configurable TTL; eviction is FUT-PROC-02. |
| Orchestrator-side entry-step L2 seeding | Entry steps with a required input definition + empty `entryId` fail by design; no orchestrator change. |
| Orchestrator / WebApi / wire-contract changes | The `Orchestrator` console, the `EntryStepDispatch`/`ExecutionResult` wire contracts, AND the `ProcessorLivenessValidator` are all reused **unchanged** — Path 1 (only-healthy liveness writes) makes presence+freshness mean "≥1 replica healthy", so the existing absent/stale gate suffices with no status check. |
| Per-replica liveness keys + aggregating gate | The shared liveness key suffices for competing-consumer dispatch; per-replica keys (`skp:{processorId}:{replicaId}`) + "≥1 healthy" aggregation in the gate are not needed (Path 2, rejected). |
| Per-dispatch liveness re-gating | Liveness is checked at orchestration **Start** only; dispatches are not re-gated per message. Post-Start health drift is not detected (FUT-PROC-04). |
| Multiple concrete processors | Only `Processor.Sample` ships; the `Processor.<Purpose>` family convention is established but not exercised by a second concrete. |

## Traceability

Which phases cover which requirements. Populated during roadmap creation (Phase 25-28).

| Requirement | Phase | Status |
|-------------|-------|--------|
| CONTRACT-01 | Phase 25 | Complete |
| CONTRACT-02 | Phase 25 | Complete |
| CONTRACT-03 | Phase 25 | Complete |
| RPC-01 | Phase 25 | Complete |
| RPC-02 | Phase 25 | Complete |
| RPC-03 | Phase 25 | Complete |
| BPC-01 | Phase 26 | Complete |
| BPC-02 | Phase 26 | Complete |
| BPC-03 | Phase 26 | Complete |
| IDENT-03 | Phase 26 | Complete |
| IDENT-04 | Phase 26 | Complete |
| RPC-04 | Phase 26 | Complete |
| SCHEMA-01 | Phase 26 | Complete |
| SCHEMA-02 | Phase 26 | Complete |
| LIVE-01 | Phase 26 | Complete |
| LIVE-02 | Phase 26 | Complete |
| LIVE-03 | Phase 26 | Complete |
| LIVE-04 | Phase 26 | Complete |
| LIVE-05 | Phase 26 | Complete |
| LIVE-06 | Phase 26 | Complete |
| CONFIG-01 | Phase 26 | Complete |
| EXEC-01 | Phase 27 | Complete |
| EXEC-02 | Phase 27 | Complete |
| EXEC-03 | Phase 27 | Complete |
| EXEC-04 | Phase 27 | Complete |
| EXEC-05 | Phase 27 | Complete |
| EXEC-06 | Phase 27 | Complete |
| EXEC-07 | Phase 27 | Complete |
| EXEC-08 | Phase 27 | Complete |
| EXEC-09 | Phase 27 | Complete |
| EXEC-10 | Phase 27 | Complete |
| CONFIG-02 | Phase 27 | Complete |
| IDENT-01 | Phase 28 | Complete |
| IDENT-02 | Phase 28 | Complete |
| SAMPLE-01 | Phase 28 | Complete |
| SAMPLE-02 | Phase 28 | Complete |
| TEST-01 | Phase 28 | Complete |
| TEST-02 | Phase 28 | Complete |
| LOG-01 | Phase 29 | Complete |
| LOG-02 | Phase 29 | Complete |
| LOG-03 | Phase 29 | Complete |
| LOG-04 | Phase 29 | Complete |
| LOG-05 | Phase 29 | Complete |
| LOG-06 | Phase 29 | Complete |
| METRIC-01 | Phase 30 | Planned |
| METRIC-02 | Phase 30 | Planned |
| METRIC-03 | Phase 30 | Planned |
| METRIC-04 | Phase 30 | Complete |
| METRIC-05 | Phase 30 | Planned |
| METRIC-06 | Phase 30 | Planned |
| METRIC-07 | Phase 30 | Planned |

**Coverage:**
- v3.5.0 core requirements: 38 total (BPC ×3, IDENT ×4, RPC ×4, SCHEMA ×2, LIVE ×6, CONTRACT ×3, EXEC ×10, SAMPLE ×2, CONFIG ×2, TEST ×2)
- v3.5.0 follow-up (Phase 29): 6 (LOG ×6) — structured execution-scope logging
- v3.5.0 follow-up (Phase 30): 7 (METRIC ×7) — runtime + business metrics (planned)
- Mapped to phases: 51/51 ✓ (Phase 25 ×6, Phase 26 ×15, Phase 27 ×11, Phase 28 ×6, Phase 29 ×6, Phase 30 ×7)
- Unmapped: 0 ✓

---
*Requirements defined: 2026-06-01*
*Last updated: 2026-06-02 — Phase 30 (Runtime & Business Metrics) ADDED as a v3.5.0 follow-up: METRIC-01..07 (Planned), 51 requirements mapped across Phases 25-30. Prior: Phase 29 COMPLETE (5/5 plans; close gate 3×405 GREEN + triple-SHA HELD); LOG-01..06 all Complete.*
