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

- [x] **MSG-WEBAPI-01
**: `BaseApi.Service` joins the MassTransit bus as a publisher and references `Messaging.Contracts`, without referencing `BaseConsole.Core`.
- [x] **MSG-WEBAPI-02
**: A successful `POST /api/v1/orchestration/start` publishes a `StartOrchestration{WorkflowIds[]}` message; `stop` publishes `StopOrchestration{WorkflowIds[]}`.
- [x] **MSG-WEBAPI-03
**: RabbitMQ is a hard dependency for the Start/Stop path only — Start/Stop fail (5xx + RFC 7807) when the broker is unreachable, while the CRUD surface is unaffected.
- [x] **MSG-WEBAPI-04
**: The WebApi bus health check does NOT flip CRUD `/health/ready` when RabbitMQ is down (`MinimalFailureStatus=Degraded` or re-tagged off `ready`), mirroring the Redis soft-dependency posture. [F10]

### Orchestrator (concrete console)

- [x] **ORCH-CON-01
**: A runnable `Orchestrator` console inherits `BaseConsole.Core` and is a thin shell (registers only its consumers + fan-out endpoint; no infrastructure code).
- [x] **ORCH-CON-02
**: The Orchestrator binds an instance-unique, temporary/auto-delete receive endpoint via `InstanceId`, so every replica receives its own copy of each published Start/Stop (fan-out, NOT load-balanced); scaling 1→N requires no code change.
- [x] **ORCH-CON-03
**: On consuming `StartOrchestration`/`StopOrchestration`, the Orchestrator opens the correlated log scope from the **message-body `ICorrelated.CorrelationId`** (minted fresh at publish) and reads the Redis L2 root per `WorkflowId` for existence/payload (a `WorkflowId` absent from L2 is the MSG-ACK-01 business-failure path). [AMENDED 2026-05-30, Phase 19 D-01: per-stage correlation model — correlation rides the message body via the slim `ICorrelated` contract (not the MassTransit envelope); the HTTP `X-Correlation-Id` is not stored in or read from the L2 root; correlation is handed off at the publish boundary, not carried across the hop.]
- [x] **ORCH-CON-04
**: The Orchestrator logs its processing up to the "scheduler job start" seam under the correlated log scope, and performs no Redis writes and no Quartz scheduling this milestone (logs are the deliverable).

### Correlation Propagation

- [x] **CORR-01
**: `BaseConsole.Core` includes an inbound consume filter that resolves the correlation value **from the message body (`message is ICorrelated → CorrelationId`)**, pushes it into an AsyncLocal accessor, and opens a MEL log scope under the literal key `"CorrelationId"` (same key as `CorrelationIdMiddleware`, so OTel `IncludeScopes` serializes it to the identical Elasticsearch attribute). [AMENDED 2026-05-30, Phase 19 D-01: the shipped Phase 18 filter reads the MassTransit *envelope* `CorrelationId`; Phase 19 re-points it to the message-body `ICorrelated.CorrelationId` per the body-carried correlation model.]
- [x] **CORR-02
**: `BaseConsole.Core` includes an outbound send/publish filter that stamps the ambient AsyncLocal correlationId onto every outgoing `ICorrelated` message.
- [x] **CORR-03**: The outbound filter is exercised this milestone by a synthetic test-harness downstream send that asserts the correlationId is stamped (no real downstream consumer required).
- [x] **CORR-04**: Per-stage correlation is proven across stage boundaries — an HTTP `X-Correlation-Id` scopes the HTTP stage (request → L2 write → publish); a fresh `Guid CorrelationId` minted at publish and carried on the **message body** via `ICorrelated.CorrelationId` rides the fan-out message and is the value the Orchestrator log line surfaces in Elasticsearch under the `"CorrelationId"` attribute. Clean per-stage handoff at the publish boundary, not a single value carried across all hops. [AMENDED 2026-05-30, Phase 19 D-01.]

### Acknowledgement Semantics

- [x] **MSG-ACK-01
**: Business failures in a consumer (e.g., a `WorkflowId` absent from L2) are caught, logged at the correlated scope, and the consume completes (message acked) — they are NOT thrown.
- [x] **MSG-ACK-02
**: Genuine infrastructure faults are allowed to throw → bounded retry → dead-letter to the `_error` queue; a process crash mid-consume leaves the message unacked for broker redelivery (the crash-safety guarantee).
- [x] **MSG-ACK-03
** (P2): Consumers configure bounded `UseMessageRetry` with `Ignore<>` for the business-failure exception type, so business failures never retry or dead-letter. [F13]
- [x] **MSG-ACK-04
** (P2): Each consumer has a `ConsumerDefinition` class as the config seam for retry / InstanceId / endpoint settings (matches the project's "base + per-entity definition" idiom; future-proofs the Processor milestone). [F13]

### Infrastructure (RabbitMQ + pins)

- [x] **INFRA-RMQ-01
**: `MassTransit` and `MassTransit.RabbitMQ` are pinned at `8.5.5` (Apache-2.0) in Central Package Management with a blocking comment that v9+ is commercial ($400/mo) — mirroring the existing Npgsql cautionary pin.
- [x] **INFRA-RMQ-02**: A `rabbitmq:4.1.8-management-alpine` service is added to `compose.yaml` with a `rabbitmq-diagnostics -q ping` healthcheck; the Start/Stop path depends on `service_healthy`.
- [x] **INFRA-RMQ-03**: appsettings carry RabbitMQ connection configuration for both `BaseApi.Service` and `Orchestrator`; the bus connects with the locked host/credentials.

### Test Discipline

- [x] **TEST-RMQ-01**: A fan-out test runs two in-process bus instances (two `InstanceId`s) and asserts BOTH receive a copy of a single published Start message (broadcast proven, not load-balanced) — the #1 topology trap, tested now rather than deferred.
- [x] **TEST-RMQ-02**: An end-to-end test drives a real HTTP Start (carrying an `X-Correlation-Id` scoping the HTTP stage) and asserts the Orchestrator's correlated log line surfaces in Elasticsearch under the `"CorrelationId"` attribute carrying the **body `ICorrelated.CorrelationId` minted at publish** (the per-stage handoff value), proving the chain HTTP stage → publish-mint → fan-out message body → orchestrator log. [AMENDED 2026-05-30, Phase 19 D-01.]
- [x] **TEST-RMQ-03**: A "broker down" test asserts WebApi CRUD `/health/ready` and `/health/live` both stay 200 with RabbitMQ unreachable (a `HealthDeadRabbitFixture` mirroring `HealthDeadRedisFixture`).
- [x] **TEST-RMQ-04**: Test receive endpoints use temporary/auto-delete, per-test-class-prefixed queues; NO global queue purge in teardown (the `FLUSHDB`-ban analog).
- [ ] **TEST-RMQ-05**: The phase-close gate is extended to a triple-SHA snapshot — `psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues name` — asserting BEFORE=AFTER, alongside the 3-consecutive-GREEN cadence. [F12]

### Closeout Hardening (Phase 21 — post-audit gap-closure)

Added 2026-05-31 from the v3.4.0 milestone audit (`milestones/v3.4.0-MILESTONE-AUDIT.md`). These close non-blocking tech-debt items, not blocking gaps — the milestone's 37 functional requirements were all satisfied. Tracked here so the hardening is accounted for rather than lost.

- [x] **HARDEN-01** (WR-01): `EmbeddedHealthEndpointService.StopAsync` disposes the inner `WebApplication` (`DisposeAsync` after `StopAsync`) so the inner DI container / Kestrel / TCP socket are released deterministically on shutdown — not left to the finalizer. **Already satisfied in Phase 18 (commit `d4c0af5`)** — the v3.4.0 audit mis-flagged this as open by carrying it forward from Phase 18's VERIFICATION anti-pattern table, which predated the same-phase fix.
- [x] **HARDEN-02** (WR-02): The embedded console health Kestrel listener isolates bind failure — a port collision on `ConsoleHealth:Port` surfaces as a logged, contained failure instead of an unhandled exception escaping `Host.StartAsync`. **Already satisfied in Phase 18 (commit `4e9e21a`)** — same audit carry-forward error as HARDEN-01.
- [x] **HARDEN-03** (WARNING-1): The L2 root key shape (`Root(prefix, workflowId)`) is a single shared source of truth consumed by both the writer (`RedisProjectionKeys`) and the reader (`OrchestratorL2Keys`), so a future GUID-format/suffix change cannot silently desynchronize them. (The only genuinely-open hardening item → Phase 21.)

## Future Requirements (Processor milestone, v3.5.x+)

Deferred. Tracked, not in this roadmap.

### Scheduling & Processor round-trip

- **FUT-QUARTZ-01**: Quartz scheduler in the Orchestrator; mints a fresh `Guid CorrelationId` per job trigger (the bus-world correlation source).
- **FUT-SEND-01**: `Send` to `queue:processorId` (load-balanced competing consumers) — orchestrator→processor execution-activity dispatch.
- **FUT-SEND-02**: Processor→orchestrator result `Send` (load-balanced shared results queue).
- **FUT-REQRESP-01**: `IRequestClient<GetProcessorBySourceHash>` + a WebApi bus responder (processor self-id-by-SourceHash on startup).
- **FUT-CONTRACTS-01**: Concrete `JobTrigger` / `ExecutionResult` records (carrying the full `ICorrelated` fields) + the live orchestrator→processor→orchestrator round-trip.

### Phase 23 — Orchestrator Lifecycle (locked via 23-SPEC.md, 2026-05-31)

Concrete requirements for Phase 23, which pulls forward the dispatch half of the scheduling work above. **Realizes** `FUT-QUARTZ-01` (ORCH-SCHED-01 + ORCH-FIRE-01), `FUT-SEND-01` (ORCH-FIRE-01), and the orchestrator→processor message half of `FUT-CONTRACTS-01` (ORCH-CONTRACT-02). The processor→orchestrator round-trip (`FUT-SEND-02`, `FUT-REQRESP-01`, round-trip half of `FUT-CONTRACTS-01`) remains deferred. See `.planning/phases/23-orchestrator-stop-reload-lifecycle/23-SPEC.md` for current/target/acceptance per requirement.

- [x] **ORCH-CONTRACT-01
**: A reader-consumable step-projection record exists in `Messaging.Contracts.Projections` (`{ entryCondition:int, processorId, payload, nextStepIds[] }`, camelCase `[property: JsonPropertyName]`), hoisting the shape from the writer-internal `BaseApi.Service.StepProjection` so the orchestrator can deserialize per-step L2 values (single source of truth, per `HARDEN-03`).
- [x] **ORCH-CONTRACT-02
**: An orchestrator→processor entry-step dispatch message record carries `correlationId` (`ICorrelated`), `workflowId`, `stepId`, `processorId`, `executionId`, `entryId`, `payload`.
- [ ] **ORCH-STARTUP-01**: On host startup the orchestrator reads ALL workflow ids from the L2 parent index (`skp:`) and hydrates each into an in-memory L1 dictionary (workflow entry + per-step entries); L1 holds NO processor keys and NOT the parent-index key.
- [ ] **ORCH-SCHED-01**: Each hydrated workflow gets one in-memory (RAMJobStore) Quartz job whose `JobKey` embeds its `jobId`; the cron→interval is the delta (seconds) between the next two fire times, stored in L1 liveness `interval`.
- [x] **ORCH-FIRE-01**: On each fire — generate a fresh `correlationId`, `Send` the ORCH-CONTRACT-02
 message to `queue:{processorId}` for EVERY entry step (`executionId`/`entryId` = `Guid.Empty`), and refresh the workflow's L1 liveness `timestamp` to UTC-now (no L2 write).
- [ ] **ORCH-CONSUME-01**: The start-orchestration consumer hydrates ONLY the consumed workflowId(s) into L1, then runs ORCH-SCHED-01 + ORCH-FIRE-01 for them.
- [ ] **ORCH-STOP-01**: The stop-orchestration consumer consumes `workflowId`s, resolves each `jobId` from L1, `DeleteJob(JobKey(jobId))`, and clears that workflow's L1 entries — performing NO L2 mutation.
- [ ] **ORCH-SCALE-01**: All new state (L1, Quartz RAMJobStore) is per-instance with NO single-instance/global-uniqueness assumption; a single active replica is assumed at runtime, cross-replica duplicate-dispatch coordination deferred.
- [ ] **ORCH-ACK-01**: The new consumers + startup hydration follow the existing MSG-ACK split — business failures log + ack/skip; infra faults propagate to bounded retry → `_error`; startup skips a missing/corrupt entry without crashing the host.

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
| INFRA-RMQ-02 | Phase 19 | Complete |
| INFRA-RMQ-03 | Phase 19 | Complete |
| CORR-03 | Phase 20 | Done |
| CORR-04 | Phase 20 | Done |
| TEST-RMQ-01 | Phase 20 | Done |
| TEST-RMQ-02 | Phase 20 | Done |
| TEST-RMQ-03 | Phase 20 | Done |
| TEST-RMQ-04 | Phase 20 | Done |
| TEST-RMQ-05 | Phase 20 | Pending |
| HARDEN-01 | Phase 18 | Complete (commit d4c0af5; audit carry-forward error) |
| HARDEN-02 | Phase 18 | Complete (commit 4e9e21a; audit carry-forward error) |
| HARDEN-03 | Phase 21 | Complete (all 3 L2 builders hoisted to shared L2ProjectionKeys; writer+reader forwarders; 270×3 GREEN + triple-SHA gate exit 0) |

**Coverage:**
- Milestone requirements: 40 total — 35 P1 + 2 P2 (`MSG-ACK-03`, `MSG-ACK-04`) + 3 hardening (`HARDEN-01..03`, Phase 21 gap-closure)
- Mapped to phases: 40 (100% — Phase 17: 5 · Phase 18: 11 · Phase 19: 14 · Phase 20: 7 · Phase 21: 3)
- Unmapped: 0 ✓ (every requirement maps to exactly one phase; no orphans, no duplicates)

> By category: MSG-CONTRACTS 4 · CONSOLE 5 · CONSOLE-HEALTH 4 · MSG-WEBAPI 4 · ORCH-CON 4 · CORR 4 · MSG-ACK 4 · INFRA-RMQ 3 · TEST-RMQ 5 · HARDEN 3 = 40. Future and Out-of-Scope items are not counted.

---
*Requirements defined: 2026-05-30*
*Last updated: 2026-05-30 — Traceability filled by roadmapper: 37/37 requirements mapped to Phases 17-20.*
