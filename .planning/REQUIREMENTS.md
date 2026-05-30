# Requirements: Steps API — Milestone v3.4.0 (BaseConsole + Orchestrator Messaging)

**Defined:** 2026-05-30
**Core Value:** A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework — now extended with a reusable console-worker base and a message bus.

## Milestone v3.4.0 Requirements

Requirements for this milestone. Each maps to exactly one roadmap phase (continues phase numbering from v3.3.0's Phase 16). `P2` = bounded refinement, in-scope but lower priority.

### Messaging Contracts (shared assembly)

- [x] **MSG-CONTRACTS-01
**: A `Messaging.Contracts` class library exists, referenced by both `BaseApi.Service` (publisher) and `Orchestrator` (consumer), with NO dependency on MassTransit or any host library (POCO records only — message types discovered structurally).
- [x] **MSG-CONTRACTS-02
**: `StartOrchestration` and `StopOrchestration` records each carry `Guid[] WorkflowIds` as their operational payload and implement `ICorrelated` (carrying a body `Guid CorrelationId` minted at publish). [AMENDED 2026-05-30, Phase 19 D-01: superseded the original "no correlation field / do NOT implement ICorrelated" — correlation now rides the message body via the slim `ICorrelated` contract; Phase 19 reconciles the shipped Phase 17 code.]
- [x] **MSG-CONTRACTS-03
**: An `ICorrelated` contract declares the single universal correlation field `{ Guid CorrelationId }` (init-set), implemented by every bus message. The five execution ids (`ExecutionId, WorkflowId, StepId, ProcessorId, EntryId`) belong to a derived `IExecutionCorrelated : ICorrelated` contract for orchestrator↔processor execution messages, defined in the future Processor milestone where those ids are real (deferred — NOT defined this milestone). [AMENDED 2026-05-30, Phase 19 D-01: superseded the original fat-6-id frozen vocabulary — interface segregation (slim base + derived) avoids `Guid.Empty` slots on operational messages; Phase 19 reconciles the shipped Phase 17 `ICorrelated`.]
- [x] **MSG-CONTRACTS-04
**: The L2 root read-shape (the `WorkflowRootProjection` correlationId + fields) lives in `Messaging.Contracts`; `BaseApi.Service` writes it and `Orchestrator` reads it from this single source of truth (no duplicated shape).

### BaseConsole.Core (reusable Generic-Host library)

- [x] **CONSOLE-01
**: A reusable `BaseConsole.Core` class library provides a Generic-Host composition root (`AddBaseConsole`/`RunAsync` equivalent) that a concrete console wires in a handful of lines (mirrors `AddBaseApi`/`UseBaseApi`).
- [x] **CONSOLE-02
**: `BaseConsole.Core` configures console-flavored OpenTelemetry — MEL-bridge logs + runtime metrics + MassTransit meter (`InstrumentationOptions.MeterName`) via OTLP — with NO AspNetCore/HttpClient instrumentation and NO `.WithTracing` (no traces backend, preserving Phase 11 D-03).
- [x] **CONSOLE-03
**: `BaseConsole.Core` registers a singleton `IConnectionMultiplexer` Redis client as a soft dependency (`abortConnect=false`), lifted from the `BaseApi.Core` pattern.
- [x] **CONSOLE-04
**: `BaseConsole.Core` provides a MassTransit bus skeleton (`AddBaseConsoleMessaging(cfg, configureConsumers)`) that wires the RabbitMQ host + global outbound correlation filter and accepts a concrete callback to register consumers/receive endpoints (base = infra, concrete = consumers — mirrors `AddBaseApi` vs `AddAppFeatures`).
- [x] **CONSOLE-05
**: `BaseConsole.Core` references `Microsoft.AspNetCore.App` via `FrameworkReference` (stays a library, not the Web SDK); only the runnable `Orchestrator` is the executable host.

### Console Health Probes (embedded HTTP)

- [x] **CONSOLE-HEALTH-01
**: A console built on `BaseConsole.Core` exposes `/health/live`, `/health/ready`, `/health/startup` over an embedded minimal HTTP listener hosted inside the Generic Host (`MapHealthChecks` is `WebApplication`-only → inner minimal Kestrel in a hosted service).
- [x] **CONSOLE-HEALTH-02
**: `/health/live` returns 200 without touching RabbitMQ or Redis (tag discipline: live → self only), even when both are down.
- [x] **CONSOLE-HEALTH-03
**: `/health/ready` reports Healthy only once the MassTransit bus has started (reuses MassTransit's auto-registered `ready`-tagged bus health check — no hand-rolled latch) and Unhealthy while the broker is unreachable.
- [x] **CONSOLE-HEALTH-04
**: `IStartupGate` + `StartupHealthCheck` are duplicated into `BaseConsole.Core` (no `BaseConsole.Core → BaseApi.Core` dependency that would drag EF Core/MVC into a worker). [F11]

### WebApi Bus Integration (publisher / fan-out)

- [ ] **MSG-WEBAPI-01**: `BaseApi.Service` joins the MassTransit bus as a publisher and references `Messaging.Contracts`, without referencing `BaseConsole.Core`.
- [ ] **MSG-WEBAPI-02**: A successful `POST /api/v1/orchestration/start` publishes a `StartOrchestration{WorkflowIds[]}` message; `stop` publishes `StopOrchestration{WorkflowIds[]}`.
- [ ] **MSG-WEBAPI-03**: RabbitMQ is a hard dependency for the Start/Stop path only — Start/Stop fail (5xx + RFC 7807) when the broker is unreachable, while the CRUD surface is unaffected.
- [ ] **MSG-WEBAPI-04**: The WebApi bus health check does NOT flip CRUD `/health/ready` when RabbitMQ is down (`MinimalFailureStatus=Degraded` or re-tagged off `ready`), mirroring the Redis soft-dependency posture. [F10]

### Orchestrator (concrete console)

- [ ] **ORCH-CON-01**: A runnable `Orchestrator` console inherits `BaseConsole.Core` and is a thin shell (registers only its consumers + fan-out endpoint; no infrastructure code).
- [ ] **ORCH-CON-02**: The Orchestrator binds an instance-unique, temporary/auto-delete receive endpoint via `InstanceId`, so every replica receives its own copy of each published Start/Stop (fan-out, NOT load-balanced); scaling 1→N requires no code change.
- [ ] **ORCH-CON-03**: On consuming `StartOrchestration`/`StopOrchestration`, the Orchestrator opens the correlated log scope from the **message-body `ICorrelated.CorrelationId`** (minted fresh at publish) and reads the Redis L2 root per `WorkflowId` for existence/payload (a `WorkflowId` absent from L2 is the MSG-ACK-01 business-failure path). [AMENDED 2026-05-30, Phase 19 D-01: per-stage correlation model — correlation rides the message body via the slim `ICorrelated` contract (not the MassTransit envelope); the HTTP `X-Correlation-Id` is not stored in or read from the L2 root; correlation is handed off at the publish boundary, not carried across the hop.]
- [ ] **ORCH-CON-04**: The Orchestrator logs its processing up to the "scheduler job start" seam under the correlated log scope, and performs no Redis writes and no Quartz scheduling this milestone (logs are the deliverable).

### Correlation Propagation

- [x] **CORR-01
**: `BaseConsole.Core` includes an inbound consume filter that resolves the correlation value **from the message body (`message is ICorrelated → CorrelationId`)**, pushes it into an AsyncLocal accessor, and opens a MEL log scope under the literal key `"CorrelationId"` (same key as `CorrelationIdMiddleware`, so OTel `IncludeScopes` serializes it to the identical Elasticsearch attribute). [AMENDED 2026-05-30, Phase 19 D-01: the shipped Phase 18 filter reads the MassTransit *envelope* `CorrelationId`; Phase 19 re-points it to the message-body `ICorrelated.CorrelationId` per the body-carried correlation model.]
- [x] **CORR-02
**: `BaseConsole.Core` includes an outbound send/publish filter that stamps the ambient AsyncLocal correlationId onto every outgoing `ICorrelated` message.
- [ ] **CORR-03**: The outbound filter is exercised this milestone by a synthetic test-harness downstream send that asserts the correlationId is stamped (no real downstream consumer required).
- [ ] **CORR-04**: Per-stage correlation is proven across stage boundaries — an HTTP `X-Correlation-Id` scopes the HTTP stage (request → L2 write → publish); a fresh `Guid CorrelationId` minted at publish and carried on the **message body** via `ICorrelated.CorrelationId` rides the fan-out message and is the value the Orchestrator log line surfaces in Elasticsearch under the `"CorrelationId"` attribute. Clean per-stage handoff at the publish boundary, not a single value carried across all hops. [AMENDED 2026-05-30, Phase 19 D-01.]

### Acknowledgement Semantics

- [ ] **MSG-ACK-01**: Business failures in a consumer (e.g., a `WorkflowId` absent from L2) are caught, logged at the correlated scope, and the consume completes (message acked) — they are NOT thrown.
- [ ] **MSG-ACK-02**: Genuine infrastructure faults are allowed to throw → bounded retry → dead-letter to the `_error` queue; a process crash mid-consume leaves the message unacked for broker redelivery (the crash-safety guarantee).
- [ ] **MSG-ACK-03** (P2): Consumers configure bounded `UseMessageRetry` with `Ignore<>` for the business-failure exception type, so business failures never retry or dead-letter. [F13]
- [ ] **MSG-ACK-04** (P2): Each consumer has a `ConsumerDefinition` class as the config seam for retry / InstanceId / endpoint settings (matches the project's "base + per-entity definition" idiom; future-proofs the Processor milestone). [F13]

### Infrastructure (RabbitMQ + pins)

- [x] **INFRA-RMQ-01
**: `MassTransit` and `MassTransit.RabbitMQ` are pinned at `8.5.5` (Apache-2.0) in Central Package Management with a blocking comment that v9+ is commercial ($400/mo) — mirroring the existing Npgsql cautionary pin.
- [ ] **INFRA-RMQ-02**: A `rabbitmq:4.1.8-management-alpine` service is added to `compose.yaml` with a `rabbitmq-diagnostics -q ping` healthcheck; the Start/Stop path depends on `service_healthy`.
- [ ] **INFRA-RMQ-03**: appsettings carry RabbitMQ connection configuration for both `BaseApi.Service` and `Orchestrator`; the bus connects with the locked host/credentials.

### Test Discipline

- [ ] **TEST-RMQ-01**: A fan-out test runs two in-process bus instances (two `InstanceId`s) and asserts BOTH receive a copy of a single published Start message (broadcast proven, not load-balanced) — the #1 topology trap, tested now rather than deferred.
- [ ] **TEST-RMQ-02**: An end-to-end test drives a real HTTP Start (carrying an `X-Correlation-Id` scoping the HTTP stage) and asserts the Orchestrator's correlated log line surfaces in Elasticsearch under the `"CorrelationId"` attribute carrying the **body `ICorrelated.CorrelationId` minted at publish** (the per-stage handoff value), proving the chain HTTP stage → publish-mint → fan-out message body → orchestrator log. [AMENDED 2026-05-30, Phase 19 D-01.]
- [ ] **TEST-RMQ-03**: A "broker down" test asserts WebApi CRUD `/health/ready` and `/health/live` both stay 200 with RabbitMQ unreachable (a `HealthDeadRabbitFixture` mirroring `HealthDeadRedisFixture`).
- [ ] **TEST-RMQ-04**: Test receive endpoints use temporary/auto-delete, per-test-class-prefixed queues; NO global queue purge in teardown (the `FLUSHDB`-ban analog).
- [ ] **TEST-RMQ-05**: The phase-close gate is extended to a triple-SHA snapshot — `psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues name` — asserting BEFORE=AFTER, alongside the 3-consecutive-GREEN cadence. [F12]

## Future Requirements (Processor milestone, v3.5.x+)

Deferred. Tracked, not in this roadmap.

### Scheduling & Processor round-trip

- **FUT-QUARTZ-01**: Quartz scheduler in the Orchestrator; mints a fresh `Guid CorrelationId` per job trigger (the bus-world correlation source).
- **FUT-SEND-01**: `Send` to `queue:processorId` (load-balanced competing consumers) — orchestrator→processor execution-activity dispatch.
- **FUT-SEND-02**: Processor→orchestrator result `Send` (load-balanced shared results queue).
- **FUT-REQRESP-01**: `IRequestClient<GetProcessorBySourceHash>` + a WebApi bus responder (processor self-id-by-SourceHash on startup).
- **FUT-CONTRACTS-01**: Concrete `JobTrigger` / `ExecutionResult` records (carrying the full `ICorrelated` fields) + the live orchestrator→processor→orchestrator round-trip.

### Deferred from prior milestones (unchanged)

- **FUT-OUTBOX-01**: Transactional outbox — once DB-transaction-coupled publishing appears.
- **FUT-OTEL-TRACES-01**: OTel traces across the bus hop — when request-flow debugging becomes painful (re-opens Phase 11 D-03).
- Prior v3.3.0 deferrals: FUTURE-STOP-EVICTION, FUTURE-GENERATION-ID, FUTURE-VALID-21-HTTP-WRITE, v2 hardening (TEST-03/04, INFRA-09/10, HTTP-17/18/19, auth boundary).

## Out of Scope

Explicitly excluded for v3.4.0. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Quartz scheduler + minting the bus `Guid CorrelationId` | Locked FUTURE (Processor milestone); Orchestrator stops at the "scheduler job start" log seam this milestone |
| `Send` to `queue:processorId` (load-balanced) | No Processor console exists yet; no downstream send this milestone — fan-out only |
| Request/Response `GetProcessorBySourceHash` + WebApi responder | No caller yet; `GetBySourceHash` stays the existing HTTP endpoint |
| Concrete `JobTrigger` / `ExecutionResult` records + live round-trip | Deliverable is logs to the scheduler-seam; outbound filter exercised via synthetic harness send only |
| Orchestrator Redis writes / new keyspaces | Locked: no Redis writes this milestone — read-only L2 access |
| Throwing on business failure | Would dead-letter business failures to `_error` (opposite of MSG-ACK-01); catch + log + complete instead |
| Transactional outbox / sagas / state machines | Overkill — WebApi only publishes control messages; Orchestrator is a stateless log-to-seam consumer |
| OTel traces / spans across the bus hop | No traces backend (Phase 11 D-03); correlation rides the `"CorrelationId"` log-scope key in Elasticsearch |
| Durable per-replica fan-out queues | Durable instance queues orphan and accumulate on rescale → unbounded broker growth; fan-out endpoints are temporary/auto-delete |
| Prefetch / concurrency (`PrefetchCount`, `ConcurrentMessageLimit`) tuning | Orthogonal to replica count; defaults suffice at control-message volume — tune only when single-replica pressure is observed |

## Traceability

Which phases cover which requirements. Filled by the roadmapper.

| Requirement | Phase | Status |
|-------------|-------|--------|
| MSG-CONTRACTS-01 | Phase 17 | Complete |
| MSG-CONTRACTS-02 | Phase 17 | Complete |
| MSG-CONTRACTS-03 | Phase 17 | Complete |
| MSG-CONTRACTS-04 | Phase 17 | Complete |
| INFRA-RMQ-01 | Phase 17 | Complete |
| CONSOLE-01 | Phase 18 | Complete |
| CONSOLE-02 | Phase 18 | Complete |
| CONSOLE-03 | Phase 18 | Complete |
| CONSOLE-04 | Phase 18 | Complete |
| CONSOLE-05 | Phase 18 | Complete |
| CONSOLE-HEALTH-01 | Phase 18 | Complete |
| CONSOLE-HEALTH-02 | Phase 18 | Complete |
| CONSOLE-HEALTH-03 | Phase 18 | Complete |
| CONSOLE-HEALTH-04 | Phase 18 | Complete |
| CORR-01 | Phase 18 | Complete |
| CORR-02 | Phase 18 | Complete |
| ORCH-CON-01 | Phase 19 | Pending |
| ORCH-CON-02 | Phase 19 | Pending |
| ORCH-CON-03 | Phase 19 | Pending |
| ORCH-CON-04 | Phase 19 | Pending |
| MSG-WEBAPI-01 | Phase 19 | Pending |
| MSG-WEBAPI-02 | Phase 19 | Pending |
| MSG-WEBAPI-03 | Phase 19 | Pending |
| MSG-WEBAPI-04 | Phase 19 | Pending |
| MSG-ACK-01 | Phase 19 | Pending |
| MSG-ACK-02 | Phase 19 | Pending |
| MSG-ACK-03 (P2) | Phase 19 | Pending |
| MSG-ACK-04 (P2) | Phase 19 | Pending |
| INFRA-RMQ-02 | Phase 19 | Pending |
| INFRA-RMQ-03 | Phase 19 | Pending |
| CORR-03 | Phase 20 | Pending |
| CORR-04 | Phase 20 | Pending |
| TEST-RMQ-01 | Phase 20 | Pending |
| TEST-RMQ-02 | Phase 20 | Pending |
| TEST-RMQ-03 | Phase 20 | Pending |
| TEST-RMQ-04 | Phase 20 | Pending |
| TEST-RMQ-05 | Phase 20 | Pending |

**Coverage:**
- Milestone requirements: 37 total — 35 P1 + 2 P2 (`MSG-ACK-03`, `MSG-ACK-04`)
- Mapped to phases: 37 (100% — Phase 17: 5 · Phase 18: 11 · Phase 19: 14 · Phase 20: 7)
- Unmapped: 0 ✓ (every requirement maps to exactly one phase; no orphans, no duplicates)

> By category: MSG-CONTRACTS 4 · CONSOLE 5 · CONSOLE-HEALTH 4 · MSG-WEBAPI 4 · ORCH-CON 4 · CORR 4 · MSG-ACK 4 · INFRA-RMQ 3 · TEST-RMQ 5 = 37. Future and Out-of-Scope items are not counted.

---
*Requirements defined: 2026-05-30*
*Last updated: 2026-05-30 — Traceability filled by roadmapper: 37/37 requirements mapped to Phases 17-20.*
