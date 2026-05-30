# Roadmap: Steps API

## Milestones

- ✅ **v3.2.0 Steps API MVP** — Phases 1-11 (shipped 2026-05-28) — see [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md)
- ✅ **v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline** — Phases 12-16 (shipped 2026-05-29) — see [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md)
- 🚧 **v3.4.0 BaseConsole + Orchestrator Messaging** — Phases 17-20 (active, started 2026-05-30)

## Active Milestone: v3.4.0 — BaseConsole + Orchestrator Messaging

**Granularity:** standard · **Phases:** 4 (17-20) · **Coverage:** 37/37 requirements (35 P1 + 2 P2)

**Goal:** Stand up a reusable `BaseConsole.Core` Generic-Host library (the console-side mirror of `BaseApi.Core`) and a first `Orchestrator` console that inherits it, connected to the web API over MassTransit/RabbitMQ, with **per-stage CorrelationId observability** proven across stage boundaries: the HTTP `X-Correlation-Id` scopes the request through the L2 write and publish; a **fresh `Guid CorrelationId` minted at publish and carried on the message body via `ICorrelated.CorrelationId`** rides the fan-out message to the Orchestrator, which opens the identical `"CorrelationId"` log scope from that body id and surfaces it in Elasticsearch. Each stage owns its correlationId with a clean handoff at the publish boundary; the HTTP `X-Correlation-Id` is **not** persisted in the L2 root. The milestone stops cleanly at the "scheduler job start" log seam — the next stage (deferred) would mint its own job-trigger correlationId — no Quartz, no round-trip, no Orchestrator Redis writes.

**Locked build order:** `Messaging.Contracts` (leaf — compile prerequisite for both publisher and consumer; includes extracting the `WorkflowRootProjection` read-shape out of `BaseApi.Service`) → `BaseConsole.Core` (Generic Host, console OTel, Redis client, embedded health probes, MassTransit bus skeleton + correlation filters) → `Orchestrator` console **∥** WebApi bus wiring + RabbitMQ compose tier (no mutual dependency, one phase with parallel waves) → Correlation propagation proof + synthetic outbound-filter harness test + triple-SHA close gate + 3-consecutive-GREEN closeout.

**Cross-phase hard constraints** (threaded into the relevant phases):
- **Fan-out, not load-balancing:** each replica binds an `InstanceId`-suffixed **temporary/auto-delete** receive queue to the shared exchange; the trap is invisible at 1 replica and MUST be proven with **two in-process bus instances** (Phase 20 / TEST-RMQ-01).
- **Correlation key casing is load-bearing:** the inbound consume filter MUST open the MEL scope under the literal `"CorrelationId"` key (the exact key `CorrelationIdMiddleware` uses and OTel `IncludeScopes=true` serializes), defined as a shared constant in `Messaging.Contracts`. **Per-stage correlation (v3.4.0 model):** the *value* under that key differs by stage — the HTTP `X-Correlation-Id` scopes the request/L2-write/publish stage; a fresh `Guid CorrelationId` is minted at publish and carried on the **message body** via `ICorrelated.CorrelationId` (NOT the MassTransit envelope), and is the value the Orchestrator logs under the same key. The key is the shared join; the value is handed off (not carried) at each stage boundary. The HTTP `X-Correlation-Id` is not persisted to the L2 root. **Correlation contract (v3.4.0 model):** `ICorrelated` is slimmed to the single universal field `{ Guid CorrelationId }` (init-set); operational messages (Start/Stop) implement just `ICorrelated`; the five execution ids (`ExecutionId, WorkflowId, StepId, ProcessorId, EntryId`) move to a derived `IExecutionCorrelated : ICorrelated` defined in the future Processor milestone where they are real (deferred — not defined this milestone).
- **OTel metrics-only, NO `.WithTracing`:** console OTel = MEL-bridge logs + runtime + `AddMeter(InstrumentationOptions.MeterName)`; no AspNetCore instrumentation, no `TracerProvider` (preserves Phase 11 D-03); MassTransit's `ActivitySource` must NOT resurrect a traces pipeline.
- **Ack semantics:** business failures are caught + logged at the correlated scope + the consume completes (acked); only genuine infrastructure faults throw → bounded retry → `_error` dead-letter; a mid-consume crash leaves the message unacked for broker redelivery.
- **RabbitMQ soft on CRUD readiness:** hard dependency for the Start/Stop path only; the WebApi bus health check must NOT flip CRUD `/health/ready` when the broker is down (`MinimalFailureStatus=Degraded` or re-tagged off `ready`), mirroring the Redis soft-dep posture. The Orchestrator is the opposite — its `/health/ready` goes Unhealthy if the broker drops.
- **No global purge:** the phase-close gate extends to a **triple-SHA** snapshot — `psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues name` — asserting BEFORE=AFTER; test queues are temporary/auto-delete with per-test-class prefixes (the `FLUSHDB`-ban analog).

### Phases (v3.4.0)

- [ ] **Phase 17: Messaging.Contracts + Shared L2 Root Extract** — The leaf assembly both hosts compile against: control records, `ICorrelated` vocabulary, the two correlation filters + AsyncLocal accessor, and the L2 root read-shape moved out of `BaseApi.Service`; MassTransit pinned at 8.5.5.
- [ ] **Phase 18: BaseConsole.Core Library** — Reusable Generic-Host base mirroring `BaseApi.Core`: console-flavored OTel (logs+metrics, no traces), lifted Redis client, embedded minimal-Kestrel health probes (`/ready` flips on bus-started), duplicated startup gate, and the MassTransit bus skeleton with the correlation filters wired bus-wide.
- [ ] **Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier** — The runnable `Orchestrator` (instance-unique fan-out endpoint, read-L2 → correlated log → ack-on-success, business-vs-infra ack split) in parallel with WebApi publish-only bus join, the L2-writer using-swap, and the RabbitMQ compose tier + appsettings for both hosts.
- [ ] **Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout** — End-to-end correlation proven in Elasticsearch, the two-bus-instance fan-out broadcast test, the synthetic outbound-filter harness send, and the triple-SHA close gate at 3-consecutive-GREEN.

## Phase Details (v3.4.0)

### Phase 17: Messaging.Contracts + Shared L2 Root Extract
**Goal**: A leaf `Messaging.Contracts` assembly exists that both `BaseApi.Service` and `Orchestrator` can compile against, carrying the frozen message vocabulary, the correlation machinery, and the single-source-of-truth L2 root read-shape — with MassTransit pinned safely.
**Depends on**: Nothing (first phase of the milestone; v3.3.0 codebase is the baseline)
**Requirements**: MSG-CONTRACTS-01, MSG-CONTRACTS-02, MSG-CONTRACTS-03, MSG-CONTRACTS-04, INFRA-RMQ-01
**Success Criteria** (what must be TRUE):
  1. A `Messaging.Contracts` class library exists and is referenced by both `BaseApi.Service` and `Orchestrator`, with no dependency on any host library (POCO records + MassTransit/Logging.Abstractions only).
  2. `StartOrchestration` and `StopOrchestration` each carry exactly `Guid[] WorkflowIds` and no correlation field; an `ICorrelated` contract declares the frozen `{ CorrelationId, ExecutionId, WorkflowId, StepId, ProcessorId, EntryId }` Guid vocabulary.
  3. The L2 root read-shape (`WorkflowRootProjection` correlationId + fields) now lives in `Messaging.Contracts`; the camelCase `[JsonPropertyName]` wire shape is byte-identical to the shape `BaseApi.Service` previously wrote (no duplicated shape, no wire drift).
  4. `MassTransit` and `MassTransit.RabbitMQ` are pinned at `8.5.5` in Central Package Management with a blocking comment that v9+ is commercial, mirroring the existing Npgsql cautionary pin.
  5. The solution still builds zero-warning Release + Debug and the existing v3.3.0 test suite stays GREEN (the L2 root move is a behavior-preserving using-swap).
**Plans**: 2 plans
  - [x] 17-01-PLAN.md — Create Messaging.Contracts leaf (frozen vocabulary + moved L2 records), MassTransit 8.5.5 CPM pins, sln + ProjectReference wiring
  - [x] 17-02-PLAN.md — Using-swap all 8 consumers to the new namespace + terminal gate (zero-warning Release/Debug, 3-consecutive-GREEN, dual-snapshot BEFORE=AFTER)

### Phase 18: BaseConsole.Core Library
**Goal**: A reusable `BaseConsole.Core` Generic-Host library exists — the console-side mirror of `BaseApi.Core` — providing observability, Redis, embedded health probes, and a MassTransit bus skeleton with correlation filters, validated standalone before any concrete console inherits it.
**Depends on**: Phase 17 (the correlation filters and contracts are defined there)
**Requirements**: CONSOLE-01, CONSOLE-02, CONSOLE-03, CONSOLE-04, CONSOLE-05, CONSOLE-HEALTH-01, CONSOLE-HEALTH-02, CONSOLE-HEALTH-03, CONSOLE-HEALTH-04, CORR-01, CORR-02
**Success Criteria** (what must be TRUE):
  1. A console built on `BaseConsole.Core` boots via an `AddBaseConsole`/`RunAsync`-style chain in a handful of lines, registers a singleton soft-dependency Redis client (`abortConnect=false`), and references `Microsoft.AspNetCore.App` via `FrameworkReference` (stays a library, not the Web SDK) with no `BaseConsole.Core → BaseApi.Core` dependency.
  2. Console OTel emits MEL-bridge logs + runtime metrics + the MassTransit meter via OTLP with NO AspNetCore/HttpClient instrumentation and NO `TracerProvider` (asserted absent — preserves Phase 11 D-03).
  3. `/health/live` returns 200 over the embedded minimal HTTP listener without touching RabbitMQ or Redis, even when both are down; `/health/ready` reports Healthy only once the MassTransit bus has started (via MassTransit's auto-registered `ready`-tagged bus check, no hand-rolled latch) and Unhealthy while the broker is unreachable; `/health/startup` is served by the duplicated `IStartupGate` + `StartupHealthCheck`.
  4. `AddBaseConsoleMessaging(cfg, configureConsumers)` wires the RabbitMQ host + the bus-wide outbound correlation send/publish filters and accepts a concrete callback for consumers/receive endpoints (base = infra, concrete = consumers).
  5. The inbound consume filter resolves the correlation value, pushes it into an AsyncLocal accessor, and opens a MEL log scope under the literal `"CorrelationId"` key; the outbound filter stamps the ambient correlationId onto every outgoing `ICorrelated` message.
**Plans**: 4 plans
  - [x] 18-01-PLAN.md — Create BaseConsole.Core project + sln entry + foundational primitives (csproj/FrameworkReference, RequiredConfig, startup gate trio, Phase-5 StartupCompletionService, soft-dep Redis, AsyncLocal correlation accessor)
  - [x] 18-02-PLAN.md — Console OTel (metrics-only, no traces) + the three correlation filters + AddBaseConsoleMessaging bus skeleton wiring filters bus-wide
  - [x] 18-03-PLAN.md — Embedded minimal-Kestrel health listener (/live|ready|startup) + BusReadyHealthCheck bridge + ConsoleHealth registration + non-generic AddBaseConsole root
  - [x] 18-04-PLAN.md — ConsoleTestHostFixture + five Console validation test classes (D-02 six proof points) + dual-SHA 3-consecutive close gate
**UI hint**: no

### Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier
**Goal**: The first runnable `Orchestrator` console consumes Start/Stop on an instance-unique fan-out queue and logs to the scheduler-job-start seam under a correlated scope, while the WebApi joins the bus as a publisher; RabbitMQ is live in the compose stack. Both streams run in parallel (no mutual dependency).
**Depends on**: Phase 18 (Orchestrator inherits `BaseConsole.Core`); Phase 17 (both reference `Messaging.Contracts`)
**Requirements**: ORCH-CON-01, ORCH-CON-02, ORCH-CON-03, ORCH-CON-04, MSG-WEBAPI-01, MSG-WEBAPI-02, MSG-WEBAPI-03, MSG-WEBAPI-04, MSG-ACK-01, MSG-ACK-02, MSG-ACK-03, MSG-ACK-04, INFRA-RMQ-02, INFRA-RMQ-03
**Success Criteria** (what must be TRUE):
  1. A runnable `Orchestrator` console (thin shell — registers only its consumers + fan-out endpoint, no infrastructure code) binds an `InstanceId`-based temporary/auto-delete receive endpoint, so every replica receives its own copy of each published Start/Stop and scaling 1→N requires no code change.
  2. On consuming Start/Stop, the Orchestrator opens the correlated log scope from the **message-body `ICorrelated.CorrelationId`** (minted fresh at publish) and reads the Redis L2 root per `WorkflowId` for existence/payload (the absent-from-L2 case is the MSG-ACK-01 business-failure path); it logs up to the "scheduler job start" seam — performing no Redis writes and no Quartz scheduling. The HTTP `X-Correlation-Id` is not stored in or read from the L2 root; correlation rides the message body (not the MassTransit envelope) and is per-stage with a handoff at the publish boundary.
  3. A successful `POST /api/v1/orchestration/start` publishes `StartOrchestration{WorkflowIds[]}` and `stop` publishes `StopOrchestration{WorkflowIds[]}` (WebApi joins the bus as publisher referencing only `Messaging.Contracts`, never `BaseConsole.Core`); Start/Stop fail with 5xx + RFC 7807 when the broker is unreachable while the CRUD surface and CRUD `/health/ready` are unaffected.
  4. A `WorkflowId` absent from L2 is caught, logged at the correlated scope, and the consume completes (acked — not thrown, not dead-lettered); genuine infrastructure faults throw → bounded `UseMessageRetry` (with `Ignore<>` for the business-failure type) → `_error` queue, and a mid-consume crash leaves the message unacked for broker redelivery.
  5. A `rabbitmq:4.1.8-management-alpine` service is healthy in `compose.yaml` (`rabbitmq-diagnostics -q ping`), the Start/Stop path `depends_on service_healthy`, both hosts carry RabbitMQ connection configuration in appsettings, and each consumer has a `ConsumerDefinition` class as the retry/InstanceId/endpoint config seam.
**Plans**: 4 plans
  - [x] 19-01-PLAN.md — Reconcile shipped Phase 17/18 to body-carried correlation (slim ICorrelated, Start/Stop implement it, re-point inbound filter, rewrite ConsoleCorrelationFilterTests) — the compile prerequisite for both streams
  - [x] 19-02-PLAN.md — Runnable Orchestrator console (thin shell, two consumers + definitions, instance-unique fan-out endpoint, read-L2 → seam, business-ack/infra-throw split, harness ack tests)
  - [x] 19-03-PLAN.md — WebApi publish-only bus join (AddBaseApiMessaging, Degraded bus health, publish Start/Stop with body CorrelationId, publish harness tests)
  - [x] 19-04-PLAN.md — RabbitMQ compose tier + runnable orchestrator container + WebApi broker hard-dep (Dockerfile, compose services)
**UI hint**: no

### Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout
**Goal**: The headline milestone deliverable — CorrelationId proven end-to-end in Elasticsearch — is demonstrated, the fan-out broadcast and outbound filter are proven via test, and the milestone closes under the extended triple-SHA leak gate at 3-consecutive-GREEN.
**Depends on**: Phase 19 (requires both the Orchestrator consumer and the WebApi publisher live end-to-end)
**Requirements**: CORR-03, CORR-04, TEST-RMQ-01, TEST-RMQ-02, TEST-RMQ-03, TEST-RMQ-04, TEST-RMQ-05
**Success Criteria** (what must be TRUE):
  1. An end-to-end test drives a real HTTP Start (carrying an `X-Correlation-Id` that scopes the HTTP stage) and asserts the Orchestrator's correlated log line surfaces in Elasticsearch under the identical `"CorrelationId"` attribute carrying the **body `ICorrelated.CorrelationId` minted at publish** — proving the per-stage correlation chain (HTTP stage → publish-boundary mint → fan-out message body → orchestrator log) with a clean handoff, not a single value carried across all hops.
  2. A fan-out test runs two in-process bus instances (two `InstanceId`s) and asserts BOTH receive a copy of a single published Start (broadcast proven, not load-balanced) — the #1 topology trap, tested now.
  3. The outbound correlation filter is exercised by a synthetic test-harness downstream send that asserts the ambient correlationId is stamped (no real downstream consumer required).
  4. A "broker down" test asserts WebApi CRUD `/health/ready` and `/health/live` both stay 200 with RabbitMQ unreachable (a `HealthDeadRabbitFixture` mirroring `HealthDeadRedisFixture`); test receive endpoints use temporary/auto-delete per-class-prefixed queues with no global queue purge in teardown.
  5. The phase-close gate runs 3× consecutive GREEN with a triple-SHA snapshot (`psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues name`) asserting BEFORE=AFTER.
**Plans**: 4 plans
Plans:
- [ ] 20-01-PLAN.md — Source/test prerequisites: D-13 Stop seam + assertion fix, D-07 publish-side correlation log, OQ#1 RabbitMq:Port read, D-01 HarnessWebAppFactory in-memory swap, D-12 Dockerfile wget
- [ ] 20-02-PLAN.md — Hermetic in-memory tests: TEST-RMQ-01 fan-out broadcast, CORR-03 synthetic outbound filter, TEST-RMQ-03 HealthDeadRabbitFixture broker-down (TEST-RMQ-04 temporary/auto-delete discipline)
- [ ] 20-03-PLAN.md — Real-stack ES E2E: TEST-RMQ-02/CORR-04 two-doc correlation proof (body Guid == seam == published, != HTTP X-Correlation-Id)
- [ ] 20-04-PLAN.md — Triple-SHA close gate (psql+redis+rabbitmq) + 3 smell fixes + 3x-GREEN closeout (TEST-RMQ-04/05)
**UI hint**: no

### Coverage (v3.4.0)

✓ All 37 milestone requirements mapped to exactly one phase (35 P1 + 2 P2)
✓ No orphaned requirements · ✓ Phase numbering continues from v3.3.0 (16 → 17-20; no reset to 1)

| Category | Count | Phase(s) |
|----------|-------|----------|
| MSG-CONTRACTS (01-04) | 4 | 17 |
| INFRA-RMQ (01) | 1 | 17 |
| CONSOLE (01-05) | 5 | 18 |
| CONSOLE-HEALTH (01-04) | 4 | 18 |
| CORR (01, 02) | 2 | 18 |
| ORCH-CON (01-04) | 4 | 19 |
| MSG-WEBAPI (01-04) | 4 | 19 |
| MSG-ACK (01-04, incl. P2 03/04) | 4 | 19 |
| INFRA-RMQ (02, 03) | 2 | 19 |
| CORR (03, 04) | 2 | 20 |
| TEST-RMQ (01-05) | 5 | 20 |
| **Total** | **37** | **17-20** |

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

## Progress

| Phase | Milestone | Plans Complete | Status      | Completed  |
| ----- | --------- | -------------- | ----------- | ---------- |
| 1-11  | v3.2.0    | 41/41          | Complete    | 2026-05-28 |
| 12    | v3.3.0    | 8/8            | Complete    | 2026-05-29 |
| 13    | v3.3.0    | 3/3            | Complete    | 2026-05-29 |
| 14    | v3.3.0    | 5/5            | Complete    | 2026-05-29 |
| 15    | v3.3.0    | 5/5            | Complete    | 2026-05-29 |
| 16    | v3.3.0    | 5/5            | Complete    | 2026-05-29 |
| 17    | v3.4.0    | 2/2 | Complete    | 2026-05-30 |
| 18    | v3.4.0    | 4/4 | Complete    | 2026-05-30 |
| 19    | v3.4.0    | 4/4 | Complete    | 2026-05-30 |
| 20    | v3.4.0    | 0/?            | Not started | -          |

---
*v3.2.0 shipped 2026-05-28 (11 phases). v3.3.0 shipped 2026-05-29 (5 phases, Orchestration L3→L1→L2 build pipeline). v3.4.0 (BaseConsole + Orchestrator Messaging) roadmap created 2026-05-30 — 4 phases (17-20), 37 requirements, dependency-ordered per HIGH-confidence research (`.planning/research/SUMMARY.md`). Next: `/gsd-plan-phase 17`.*
