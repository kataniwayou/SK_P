# Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier - Context

**Gathered:** 2026-05-30
**Status:** Ready for planning

<domain>
## Phase Boundary

Three streams, runnable end of phase (two have no mutual dependency → parallel waves):

1. **Orchestrator console** — a runnable, thin `Orchestrator` inheriting `BaseConsole.Core`
   (registers only its consumers + an instance-unique fan-out receive endpoint, no infra code).
   Consumes `StartOrchestration` / `StopOrchestration`, opens the correlated log scope from the
   **message envelope `CorrelationId`**, reads the Redis L2 root per `WorkflowId` for
   existence/payload, and logs **up to the "scheduler job start" seam** — no Redis writes, no Quartz.
2. **WebApi publish-only bus join** — `BaseApi.Service` joins the MassTransit bus as a publisher
   (references `Messaging.Contracts` only, never `BaseConsole.Core`) and publishes Start/Stop from
   `OrchestrationService` after the L2 write, minting a fresh envelope `CorrelationId` at publish.
3. **RabbitMQ infra tier** — `rabbitmq:4.1.8-management-alpine` in `compose.yaml` + RabbitMQ
   connection config in appsettings for both hosts + a runnable `Orchestrator` container/service.

In scope (14 requirements): ORCH-CON-01..04, MSG-WEBAPI-01..04, MSG-ACK-01..04 (03/04 are P2),
INFRA-RMQ-02, INFRA-RMQ-03.

OUT of this phase (downstream / Phase 20): two-bus fan-out broadcast test (TEST-RMQ-01),
ES correlation E2E proof (CORR-04 / TEST-RMQ-02), synthetic outbound-filter harness send
(CORR-03), broker-down CRUD-health test (TEST-RMQ-03), temporary/auto-delete per-class test
queues + no-global-purge (TEST-RMQ-04), triple-SHA close gate (TEST-RMQ-05).

OUT of milestone (FUTURE / Processor v3.5.x+): Quartz scheduler, the **third-stage** fresh
per-job-trigger `Guid CorrelationId` that becomes the bus-world id traveling orchestrator↔processor,
`Send` to `queue:processorId`, request/response, concrete `JobTrigger`/`ExecutionResult` records.

</domain>

<decisions>
## Implementation Decisions

### Correlation model — per-stage handoff (Area: Fork 1) ⭐ LOAD-BEARING — amends locked specs
- **D-01:** Correlation is **per-stage with a clean handoff at each boundary**, NOT a single value
  carried across the HTTP→bus hop. Three stages, fresh id at each boundary:
  1. **HTTP stage** — `CorrelationIdMiddleware`'s `X-Correlation-Id` scopes the request through L1
     build, validation, L2 write, and publish. It is **NOT persisted to the L2 root** and the
     orchestrator never reads it.
  2. **Bus stage** — a **fresh envelope `CorrelationId` is minted at publish** and rides the message.
     The inbound consume filter (CORR-01, already built in Phase 18) opens the `"CorrelationId"` MEL
     scope from that envelope id; the orchestrator logs under it up to the scheduler-job-start seam.
     This envelope value is what CORR-04 / TEST-RMQ-02 (Phase 20) assert surfaces in Elasticsearch.
  3. **Scheduler stage (FUTURE)** — the job trigger would mint yet another fresh `Guid CorrelationId`
     (the bus-world source for orchestrator↔processor). The milestone STOPS at the seam, which is
     exactly where stage-2 hands off to stage-3 — so no minting, no Quartz here.
  The `"CorrelationId"` log-scope KEY is the shared cross-stage join (CorrelationKeys.LogScope,
  Phase 17 D-11); only the VALUE under it changes per stage. This is the "consistency behavior":
  every stage owns its own correlationId.
- **D-01a (amendment record):** This model **contradicted and now amends** three locked specs +
  the milestone goal line, all edited 2026-05-30:
  - ROADMAP milestone goal (line 13) + Phase 19 SC#2 + cross-phase correlation constraint (line 19).
  - ORCH-CON-03 (was "extracts the stored X-CorrelationId" → now "scope from envelope id; reads L2
    for existence/payload").
  - CORR-04 + TEST-RMQ-02 (was "same value HTTP→L2→message→log" → now "envelope-mint value,
    per-stage handoff"). Downstream agents read the AMENDED text; do not re-introduce the
    L2-carries-X-CorrelationId chain.

### Publish-side wiring (Area: Fork 2)
- **D-02:** WebApi joins the bus **publish-only** with **no correlation filter / no AsyncLocal
  accessor**. Because the orchestrator's correlation source is the freshly-minted envelope id (D-01),
  and `StartOrchestration`/`StopOrchestration` do NOT implement `ICorrelated` (Phase 17 D-10), the
  publish explicitly mints the envelope id at the call site:
  `Publish(new StartOrchestration(ids.ToArray()), ctx => ctx.CorrelationId = NewId.NextGuid())`
  (NewId.NextGuid() — MassTransit's sequential-Guid generator). No outbound filter is duplicated
  into BaseApi (the BaseConsole filter is inapplicable to non-`ICorrelated` messages anyway, and
  lives in `BaseConsole.Core` which WebApi must not reference per MSG-WEBAPI-01).
- **D-03:** Bus registration lives in a new **`AddBaseApiMessaging(cfg)` extension in `BaseApi.Core`**
  (publish-only: `AddMassTransit` → `UsingRabbitMq` host config, no receive endpoints, no consumers),
  mirroring `AddBaseConsoleMessaging` symmetry. APIs that don't publish simply don't call it.
  `BaseApi.Service.Program.cs` calls it after `AddBaseApi<AppDbContext>` and before `Build()`.
  `BaseApi.Core` adds `MassTransit` + `MassTransit.RabbitMQ` PackageReferences (pins exist from
  Phase 17) + a `ProjectReference` to `Messaging.Contracts`. NO reference to `BaseConsole.Core`.
- **D-04:** The actual `Publish` calls go in `OrchestrationService.StartAsync` / `StopAsync`
  (inject `IPublishEndpoint`), **after** the successful L2 write (Start) / existence check (Stop).
  `WorkflowIds[]` = the existing `workflowIds` method parameter, `.ToArray()`. Start publishes
  `StartOrchestration`, Stop publishes `StopOrchestration` (MSG-WEBAPI-02).

### WebApi bus-health soft-dependency (Area: Fork 3 — MSG-WEBAPI-04)
- **D-05:** Set the MassTransit auto-registered bus health check's **`MinimalFailureStatus = Degraded`**
  (via `AddMassTransit(...).ConfigureHealthCheckOptions` / the bus health-check options) so a
  broker-down condition reports Degraded in the `/health` payload but never flips CRUD
  `/health/ready` to 503 — mirroring the Redis soft-dependency posture. (Rejected: stripping the
  `ready` tag entirely — quieter but loses the Degraded signal.) NOTE this is the **inverse** of the
  Orchestrator, whose `/health/ready` IS hard-on-broker (BaseConsole `BusReadyHealthCheck`, Phase 18).

### InstanceId + receive-endpoint shape (Area: Fork 4 — ORCH-CON-02)
- **D-06:** `InstanceId` from config key **`Orchestrator:InstanceId`**, **fallback to a generated
  GUID** when unset (1 replica works with zero config; compose/k8s override per replica via
  `Orchestrator__InstanceId`). **One** instance-unique **temporary/auto-delete** receive endpoint
  named `orchestrator-{InstanceId}` hosting **both** the Start and Stop consumers (shared endpoint;
  two `ConsumerDefinition`s point their `EndpointName` at the same instance queue). Both control
  messages thus fan out to the same per-replica queue. (Rejected: two separate endpoints.) The
  endpoint is configured non-durable + auto-delete so replicas don't orphan durable queues on
  rescale (REQUIREMENTS Out-of-Scope: "Durable per-replica fan-out queues").

### Ack semantics (Area: ack split — MSG-ACK-01..04)
- **D-07:** Business failure = a `WorkflowId` absent from the L2 root → **catch + log at the
  correlated scope + complete (ack)**, never throw (MSG-ACK-01). Introduce a dedicated business
  exception type (e.g. `WorkflowRootNotFoundException`) so retry config can `Ignore<>` it.
- **D-08:** Infrastructure faults (Redis/broker) **throw → bounded `UseMessageRetry`
  (with `Ignore<WorkflowRootNotFoundException>`) → `_error` dead-letter** (MSG-ACK-02, MSG-ACK-03 P2);
  a mid-consume crash leaves the message unacked for broker redelivery. Each consumer has a
  **`ConsumerDefinition`** class as the retry/InstanceId/endpoint config seam (MSG-ACK-04 P2) — both
  P2 items are folded in now since the ConsumerDefinition + Ignore<> wiring is the same code path.

### Orchestrator containerization (Area: Fork 5 — INFRA-RMQ-02/03)
- **D-09:** Add the **`Orchestrator` Dockerfile + `orchestrator` service to `compose.yaml` this phase**
  — Phase 19's headline is "the first *runnable* Orchestrator," so a runnable stack belongs here.
  Its `/health/ready` is hard-on-broker; `depends_on: rabbitmq: service_healthy`. (Rejected:
  defer containerization to Phase 20.) RabbitMQ service: `rabbitmq:4.1.8-management-alpine`,
  healthcheck `rabbitmq-diagnostics -q ping`, host ports `5673:5672` + `15673:15672` (avoid local
  collisions), dev creds `guest/guest`. The WebApi Start/Stop path `depends_on: rabbitmq:
  service_healthy` (INFRA-RMQ-02); RabbitMQ connection config added to both hosts' appsettings
  under a `RabbitMq:{Host,Username,Password}` section (INFRA-RMQ-03).

### Claude's Discretion
- `WorkflowRootProjection.correlationId` field (shipped Phase 17, written by `RedisProjectionWriter`)
  becomes **vestigial** under D-01 (still written, no longer read). Leave it written this milestone
  (zero churn, keeps v3.3.0 tests GREEN); a later phase may stop writing it. Planner confirms.
- Exact business-exception type name/location (`WorkflowRootNotFoundException`).
- Hoisting the `skp:` L2 key-prefix + `RedisProjectionKeys.Root(...)` helper so the orchestrator's
  read path and the existing writer share one key source (the keys type is currently `internal` in
  `BaseApi.Service`; the orchestrator can't reference it — planner decides: duplicate the key shape,
  or move the key helper to `Messaging.Contracts`).
- Orchestrator read-L2 helper shape (deserialize `WorkflowRootProjection` via STJ; the camelCase
  wire shape is fixed by Phase 17 D-08).
- Orchestrator test location: `tests/BaseApi.Tests/Orchestrator/` vs a separate
  `tests/Orchestrator.Tests` project (Phase 20 adds the heavy two-bus + ES tests — placement should
  anticipate that).
- `Orchestrator.csproj` / `Program.cs` thin-shell shape (Generic Host: `AddBaseConsoleObservability`
  + `AddBaseConsole` + `AddBaseConsoleMessaging` with the consumer-registration lambda + `RunAsync`).
- `NewId.NextGuid()` vs `Guid.NewGuid()` for the publish-mint (NewId preferred — sequential, better
  broker/index locality).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase scope & requirements (authoritative — read AMENDED text)
- `.planning/ROADMAP.md` §"Phase 19" (lines ~65-76) — goal, depends-on (17, 18), 5 success criteria
  (SC#1 AMENDED, SC#2 AMENDED 2026-05-30 per D-01).
- `.planning/REQUIREMENTS.md` — ORCH-CON-01..04 (03 AMENDED), MSG-WEBAPI-01..04, MSG-ACK-01..04,
  INFRA-RMQ-02/03 (the 14 mapped requirements); Out-of-Scope table (fan-out-not-LB, no Redis writes,
  no Quartz, no durable per-replica queues, no prefetch tuning, throwing-on-business-failure banned).
- `.planning/ROADMAP.md` lines 17-23 — cross-phase hard constraints (correlation constraint line 19
  AMENDED for per-stage model; fan-out-not-LB proven at 2 replicas in Phase 20; ack semantics;
  RabbitMQ soft-on-CRUD = WebApi, hard = Orchestrator; no global purge / triple-SHA = Phase 20).

### Prior-phase context (locked decisions this phase builds on)
- `.planning/phases/17-messaging-contracts-shared-l2-root-extract/17-CONTEXT.md` — D-10 (control records
  carry `Guid[] WorkflowIds`, do NOT implement `ICorrelated` → forces explicit publish-mint, D-02),
  D-08 (camelCase wire shape frozen), D-11 (`CorrelationKeys.LogScope = "CorrelationId"` shared const).
- `.planning/phases/18-baseconsole-core-library/18-CONTEXT.md` — D-01 (outbound filter stamps envelope,
  `ICorrelated`-gated → inapplicable to control records), D-06 (the `AddBaseConsole*` seam the
  Orchestrator wires), the `BusReadyHealthCheck` hard-on-broker posture (Orchestrator = inverse of D-05).

### BaseConsole.Core public API (built Phase 18 — the Orchestrator consumes this)
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` —
  `AddBaseConsoleMessaging(cfg, Action<IBusRegistrationConfigurator> configureConsumers)`: the
  consumer-registration seam. Orchestrator passes `x => { x.AddConsumer<StartConsumer, StartDef>();
  x.AddConsumer<StopConsumer, StopDef>(); }`. Filters + `ConfigureEndpoints` already wired bus-wide.
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs` —
  `AddBaseConsole(cfg)` (Redis soft-dep + embedded health).
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` — opens the `"CorrelationId"`
  scope from the envelope id (CORR-01); this is the orchestrator's correlation source under D-01.
- `src/BaseConsole.Core/Messaging/{ICorrelationAccessor,AsyncLocalCorrelationAccessor}.cs`,
  `OutboundCorrelation{Send,Publish}Filter.cs` — console-side outbound stamping (Phase 20 harness).
- `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs` — Orchestrator `/health/ready` hard-on-broker.

### Messaging.Contracts (Phase 17 — reference, do not modify)
- `src/Messaging.Contracts/{StartOrchestration,StopOrchestration}.cs` — `(Guid[] WorkflowIds)` records.
- `src/Messaging.Contracts/CorrelationKeys.cs` — `LogScope = "CorrelationId"`.
- `src/Messaging.Contracts/Projections/{WorkflowRootProjection,LivenessProjection}.cs` — the L2 root
  read-shape the orchestrator deserializes (camelCase frozen; `correlationId` field now vestigial).

### WebApi publish integration points
- `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` — `POST .../start|stop`
  (`List<Guid> workflowIds`).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — `StartAsync`/`StopAsync`;
  inject `IPublishEndpoint`, publish after L2 write / existence check (D-04).
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` +
  `RedisProjectionKeys.cs` (internal) — the writer stays; key helper is the orchestrator read-path
  key source (discretion: duplicate vs hoist).
- `src/BaseApi.Service/Program.cs` — chain `AddBaseApiMessaging(cfg)` after `AddBaseApi<>`.
- `src/BaseApi.Service/appsettings.json` — add `RabbitMq:` section (mirror `ConnectionStrings:`/`Redis:`).

### Health soft-dep mirror (D-05)
- `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` — tag discipline
  (`live`/`ready`/`startup`); the bus check must be Degraded-not-Unhealthy on broker-down for `ready`.

### Infra
- `compose.yaml` (root) — service + healthcheck + `depends_on: service_healthy` pattern to mirror for
  the `rabbitmq` service and the new `orchestrator` service.
- `Directory.Packages.props` — MassTransit + MassTransit.RabbitMQ 8.5.5 pinned (Phase 17); add
  PackageReferences in `BaseApi.Core` (and the Orchestrator) this phase.
- `SK_P.sln` + `src/` — add the new `src/Orchestrator` runnable project (mirrors `src/BaseApi.Service`).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `AddBaseConsoleMessaging(cfg, configureConsumers)` already exposes the exact consumer/endpoint seam
  the Orchestrator needs — the console base requires NO change this phase (the consumer lambda + two
  `ConsumerDefinition`s are the only new bus code).
- `RedisProjectionWriter` / `WorkflowRootProjection` define the L2 root the orchestrator reads;
  read path = `db.StringGetAsync(Root(prefix, workflowId))` → `JsonSerializer.Deserialize<…>`.
- `compose.yaml` has a uniform `image + healthcheck + depends_on: service_healthy` idiom (postgres,
  redis, elasticsearch, prometheus) to copy for `rabbitmq` and `orchestrator`.
- `RequiredConfig` (`cfg.Require(...)`) fail-fast reads — reuse for the `RabbitMq:*` keys on both hosts.
- MassTransit auto-registers a `ready`-tagged bus health check — D-05 just sets its failure status to
  Degraded on the WebApi side (no hand-rolled check), and Phase-18's `BusReadyHealthCheck` keeps the
  Orchestrator hard-on-broker.

### Established Patterns
- Publisher vs consumer asymmetry mirrors `AddBaseApi` (infra) vs `AddAppFeatures` (concrete):
  `AddBaseApiMessaging` (D-03) = infra publish-join; the `Publish` call sites (D-04) = concrete.
- `ConsumerDefinition` + `IConsumer<T>` is **net-new** to the repo (no precedent) — establishes the
  "base + per-message-type definition" idiom the Processor milestone will reuse (MSG-ACK-04 rationale).
- Soft-vs-hard dependency posture is a deliberate per-host inversion: Redis + RabbitMQ are soft on the
  CRUD API (`/health/ready` stays 200), RabbitMQ is hard on the Orchestrator (`/health/ready` → 503).

### Integration Points
- NEW `src/Orchestrator` runnable project → `SK_P.sln`; references `BaseConsole.Core` +
  `Messaging.Contracts` (NOT `BaseApi.*`).
- `BaseApi.Core` gains MassTransit/MassTransit.RabbitMQ PackageReferences + a `Messaging.Contracts`
  ProjectReference + `AddBaseApiMessaging`; `BaseApi.Service` calls it + publishes from
  `OrchestrationService`.
- `compose.yaml` gains `rabbitmq` + `orchestrator` services; both hosts' appsettings gain `RabbitMq:`.
- `tests/BaseApi.Tests` (or a new `tests/Orchestrator.Tests`) gains the consumer ack-split tests
  (business-ack vs infra-throw) using `AddMassTransitTestHarness` (in-memory; real-broker tests = Phase 20).

</code_context>

<specifics>
## Specific Ideas

- The correlation model (D-01) is the user's deliberate "consistency behavior": each stage owns its
  own correlationId with a clean handoff at the boundary, rather than threading one HTTP value across
  the whole pipeline. The `"CorrelationId"` log-scope KEY is the constant join across stages; the VALUE
  is per-stage. The scheduler-job-start seam is the precise edge where stage-2 (envelope) would hand off
  to stage-3 (future per-job Guid) — which is exactly where the milestone stops.
- Phase 19 deliverable is logs-to-the-seam + a runnable stack; the *proof* that correlation surfaces in
  Elasticsearch and that fan-out broadcasts (not load-balances) is Phase 20. Design the consumer logging
  + the publish-mint so the Phase 20 ES assertion reads the envelope `CorrelationId` under `"CorrelationId"`.
- Two parallel waves with no mutual dependency: (A) Orchestrator console + consumers + container;
  (B) WebApi publish-join + RabbitMQ compose tier + appsettings. They meet only at the shared contract
  + the live broker — plan them as independent waves.

</specifics>

<deferred>
## Deferred Ideas

- **Stage-3 per-job-trigger `Guid CorrelationId`** (the bus-world correlation source that travels
  orchestrator→processor→orchestrator) → FUTURE / Processor milestone (FUT-QUARTZ-01, FUT-SEND-01/02).
  Minted at the job trigger, i.e. one step past this milestone's seam. Tracked, not lost.
- **Stop writing `WorkflowRootProjection.correlationId`** to the L2 root (vestigial under D-01) →
  optional later cleanup; left written this milestone for zero churn / GREEN-preservation.
- **Hoisting `RedisProjectionKeys` to `Messaging.Contracts`** (so writer + orchestrator-reader share one
  key source) → planner's call this phase; if deferred, duplicate the key shape with a comment.
- **Prefetch / concurrency tuning** (`PrefetchCount`, `ConcurrentMessageLimit`) → REQUIREMENTS
  Out-of-Scope; defaults suffice at control-message volume. The `ConsumerDefinition` seam (D-08) is
  where it would later land.
- **`AddBaseApiMessaging` request/response or consumer support** → this phase is publish-only; the
  WebApi bus responder (`GetProcessorBySourceHash`) is FUT-REQRESP-01.

None of these are losses — all tracked above and/or in REQUIREMENTS.md Future/Out-of-Scope.

</deferred>

---

*Phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier*
*Context gathered: 2026-05-30*
