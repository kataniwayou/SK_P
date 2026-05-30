# Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier - Research

**Researched:** 2026-05-30
**Domain:** MassTransit 8.5.5 + RabbitMQ fan-out topology, ack semantics, publish-only bus join, body-carried correlation, **.NET 8** console containerization
**Confidence:** HIGH — repo facts + project conventions verified by reading all source/infra files; MassTransit API surface (fan-out `.Endpoint(e => e.InstanceId)` / `e.Temporary`, `ConsumerDefinition` + `UseMessageRetry`/`Ignore<>`, `ConfigureHealthCheckOptions(MinimalFailureStatus)`, `TemporaryEndpointDefinition`, `NewId.NextGuid()`, multi-consumer same-name endpoint grouping) verified via Context7 official MassTransit docs (`/websites/masstransit_massient`, fetched 2026-05-30).

> **CORRECTION — the repo is .NET 8, not .NET 9.** The spawning prompt said ".NET 9"; the actual repo
> targets `net8.0` (`Directory.Build.props:29`, SDK pinned `8.0.421` via `global.json`/README). The
> existing `Dockerfile` uses `mcr.microsoft.com/dotnet/sdk:8.0` + `aspnet:8.0`. **All Phase 19 new
> projects MUST target `net8.0`** (inherited from `Directory.Build.props`; do NOT redeclare). MassTransit
> 8.5.5 targets `net8.0` — no .NET 9 runtime is pulled. `[VERIFIED: repo]`

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (verbatim from 19-CONTEXT.md ## Implementation Decisions)

- **D-01 (LOAD-BEARING — amends shipped Phase 17 + Phase 18):** Correlation rides the **message body**
  via a **slimmed `ICorrelated` contract** `{ Guid CorrelationId }` (init-set), NOT the MassTransit
  envelope, and is **per-stage with a clean handoff at each boundary**.
  - **(i) Contract shape:** `ICorrelated` slimmed to single field `{ Guid CorrelationId }`, init-set.
    Every bus message implements it. The five execution ids (`ExecutionId, WorkflowId, StepId,
    ProcessorId, EntryId`) move to a **derived `IExecutionCorrelated : ICorrelated`** — **DEFERRED to
    the Processor milestone** (NOT defined this milestone). `StartOrchestration`/`StopOrchestration`
    implement just the slim `ICorrelated`, keeping `Guid[] WorkflowIds` payload.
  - **(ii) Per-stage values:** (1) HTTP stage — `X-Correlation-Id` scopes request→L1→validation→L2
    write→publish; **NOT persisted to L2 root**, orchestrator never reads it (HTTP id is `string`, bus
    id is `Guid`). (2) Bus stage — a **fresh `Guid CorrelationId` minted at publish, set on the message
    body**; the inbound consume filter reads `message is ICorrelated → CorrelationId` and opens the
    `"CorrelationId"` MEL scope. (3) Scheduler stage — FUTURE, milestone stops at the seam.
  - The `"CorrelationId"` log-scope KEY (CorrelationKeys.LogScope, Phase 17 D-11) is the shared join;
    only the VALUE changes per stage.
- **D-01a (amendment record):** Amends shipped Phase 17 code (`ICorrelated` slimmed to `{ Guid CorrelationId }`
  init-set; Start/Stop now implement it) and Phase 18 code (inbound consume filter reads **body**
  `ICorrelated.CorrelationId`, not envelope `ctx.CorrelationId`; outbound filter stays for the Phase 20
  harness case). Do NOT re-introduce envelope-carried or L2-carried-X-CorrelationId chains.
- **D-02:** WebApi joins the bus **publish-only**, NO correlation filter / NO AsyncLocal accessor. The
  publisher sets correlation on the **message body at construction**, minting a fresh sequential Guid:
  `Publish(new StartOrchestration(ids.ToArray()) { CorrelationId = NewId.NextGuid() })`. Do NOT also
  set the MT envelope `CorrelationId`.
- **D-03:** Bus registration in a new **`AddBaseApiMessaging(cfg)` extension in `BaseApi.Core`**
  (publish-only: `AddMassTransit` → `UsingRabbitMq`, no receive endpoints, no consumers). Called from
  `BaseApi.Service.Program.cs` after `AddBaseApi<AppDbContext>`, before `Build()`. `BaseApi.Core` adds
  `MassTransit` + `MassTransit.RabbitMQ` PackageReferences + a `ProjectReference` to
  `Messaging.Contracts`. **NO reference to `BaseConsole.Core`.**
- **D-04:** `Publish` calls go in `OrchestrationService.StartAsync`/`StopAsync` (inject
  `IPublishEndpoint`), **after** the successful L2 write (Start) / existence check (Stop).
  `WorkflowIds[]` = existing `workflowIds` param `.ToArray()`.
- **D-05:** Set the MT auto-registered bus health check's **`MinimalFailureStatus = Degraded`** so
  broker-down reports Degraded but never flips CRUD `/health/ready` to 503. **Inverse** of the
  Orchestrator (hard-on-broker).
- **D-06:** `InstanceId` from config key **`Orchestrator:InstanceId`**, **fallback to a generated GUID**
  when unset. **One** instance-unique **temporary/auto-delete** receive endpoint named
  `orchestrator-{InstanceId}` hosting **both** Start and Stop consumers (shared endpoint; two
  `ConsumerDefinition`s point `EndpointName` at the same instance queue). Non-durable + auto-delete.
- **D-07:** Business failure = a `WorkflowId` absent from the L2 root → **catch + log at the correlated
  scope + complete (ack)**, never throw. Introduce a dedicated business exception (e.g.
  `WorkflowRootNotFoundException`) so retry config can `Ignore<>` it.
- **D-08:** Infra faults (Redis/broker) **throw → bounded `UseMessageRetry`
  (with `Ignore<WorkflowRootNotFoundException>`) → `_error` dead-letter**; a mid-consume crash leaves
  the message unacked for broker redelivery. Each consumer has a **`ConsumerDefinition`** class as the
  retry/InstanceId/endpoint config seam (MSG-ACK-04 P2). Both P2 items folded in now.
- **D-09:** Add the **`Orchestrator` Dockerfile + `orchestrator` service to `compose.yaml` this phase**.
  Its `/health/ready` is hard-on-broker; `depends_on: rabbitmq: service_healthy`. RabbitMQ service:
  `rabbitmq:4.1.8-management-alpine`, healthcheck `rabbitmq-diagnostics -q ping`, host ports
  `5673:5672` + `15673:15672`, dev creds `guest/guest`. WebApi Start/Stop path
  `depends_on: rabbitmq: service_healthy`. RabbitMQ config added to both hosts' appsettings under
  `RabbitMq:{Host,Username,Password}`.

### Claude's Discretion (verbatim)
- `WorkflowRootProjection.correlationId` becomes **vestigial** under D-01 (still written, no longer
  read). Leave it written this milestone. Planner confirms.
- Exact business-exception type name/location (`WorkflowRootNotFoundException`).
- Hoisting the `skp:` L2 key-prefix + `RedisProjectionKeys.Root(...)` helper so the orchestrator's read
  path and the existing writer share one key source (the keys type is currently `internal` in
  `BaseApi.Service`; orchestrator can't reference it — planner: duplicate the key shape, or move the key
  helper to `Messaging.Contracts`).
- Orchestrator read-L2 helper shape (deserialize `WorkflowRootProjection` via STJ; camelCase wire shape
  fixed by Phase 17 D-08).
- Orchestrator test location: `tests/BaseApi.Tests/Orchestrator/` vs a separate `tests/Orchestrator.Tests`.
- `Orchestrator.csproj` / `Program.cs` thin-shell shape.
- `NewId.NextGuid()` vs `Guid.NewGuid()` for the publish-mint (NewId preferred — sequential).

### Deferred Ideas (OUT OF SCOPE — verbatim)
- **Derived `IExecutionCorrelated : ICorrelated`** (5 execution ids) → Processor milestone (FUT-CONTRACTS-01).
  Do NOT define this milestone (no unused contract in `Messaging.Contracts`).
- **Stage-3 per-job-trigger `Guid CorrelationId`** → FUTURE / Processor (FUT-QUARTZ-01, FUT-SEND-01/02).
- **Processor → WebApi startup self-id message** → OUT of v3.4.0 (FUT-REQRESP-01).
- **`CorrelatedBy<Guid>` (MT body→envelope auto-bridge)** → rejected (would drag MassTransit into
  `Messaging.Contracts`). Hand-rolled slim `ICorrelated` + body-reading inbound filter is the chosen
  path; the envelope `CorrelationId` slot is left unused.
- **Stop writing `WorkflowRootProjection.correlationId`** → optional later cleanup; left written now.
- **Hoisting `RedisProjectionKeys` to `Messaging.Contracts`** → planner's call; if deferred, duplicate
  the key shape with a comment.
- **Prefetch / concurrency tuning** (`PrefetchCount`, `ConcurrentMessageLimit`) → Out-of-Scope.
- **`AddBaseApiMessaging` request/response or consumer support** → publish-only this phase.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ORCH-CON-01 | Runnable `Orchestrator` console inherits `BaseConsole.Core`, thin shell (consumers + fan-out endpoint only, no infra code) | §"Orchestrator console + Dockerfile", §"Standard Stack" |
| ORCH-CON-02 | Binds instance-unique temporary/auto-delete receive endpoint via `InstanceId`; every replica gets own copy (fan-out, NOT load-balanced); 1→N no code change | §"Fan-out receive endpoints", §"The #1 trap" |
| ORCH-CON-03 | On consume, opens correlated log scope from **message-body `ICorrelated.CorrelationId`** + reads Redis L2 root per `WorkflowId` (absent = MSG-ACK-01 business failure) | §"Body-vs-envelope correlation read", §"L2 read path" |
| ORCH-CON-04 | Logs to "scheduler job start" seam under correlated scope; NO Redis writes, NO Quartz | §"Orchestrator consumer shape" |
| MSG-WEBAPI-01 | WebApi joins bus as publisher, references `Messaging.Contracts`, NOT `BaseConsole.Core` | §"Publisher-only bus join" |
| MSG-WEBAPI-02 | Successful `POST .../start` publishes `StartOrchestration{WorkflowIds[]}`; `stop` publishes `StopOrchestration{WorkflowIds[]}` | §"Publisher-only bus join", §"Publishing with body correlation" |
| MSG-WEBAPI-03 | RabbitMQ hard dep for Start/Stop path only — 5xx + RFC 7807 on broker-down; CRUD unaffected | §"Publish-on-broker-down behavior" |
| MSG-WEBAPI-04 | WebApi bus health check does NOT flip CRUD `/health/ready` on broker-down (`MinimalFailureStatus=Degraded`) | §"Bus health check soft-dependency" |
| MSG-ACK-01 | Business failures caught, logged at correlated scope, consume completes (acked) — NOT thrown | §"Ack semantics" |
| MSG-ACK-02 | Infra faults throw → bounded retry → `_error` dead-letter; crash mid-consume = unacked redelivery | §"Ack semantics" |
| MSG-ACK-03 (P2) | Bounded `UseMessageRetry` with `Ignore<>` for business-failure type | §"Ack semantics" |
| MSG-ACK-04 (P2) | Each consumer has a `ConsumerDefinition` class as config seam | §"ConsumerDefinition + two-consumers-on-one-endpoint" |
| INFRA-RMQ-02 | `rabbitmq:4.1.8-management-alpine` in `compose.yaml`, `rabbitmq-diagnostics -q ping` healthcheck, Start/Stop path depends on `service_healthy` | §"RabbitMQ in docker compose" |
| INFRA-RMQ-03 | appsettings carry RabbitMQ connection config for both hosts | §"RabbitMQ in docker compose", §"Config reads" |
</phase_requirements>

## Summary

Phase 19 stands up three streams over MassTransit 8.5.5 + RabbitMQ on **.NET 8**: (1) a runnable
`Orchestrator` console (instance-unique **fan-out** receive endpoint hosting two consumers, read-L2 →
correlated log → business-ack/infra-throw split); (2) a **publish-only** WebApi bus join
(`AddBaseApiMessaging` in `BaseApi.Core`, `IPublishEndpoint` from `OrchestrationService`); (3) the
RabbitMQ compose tier + appsettings for both hosts + an `orchestrator` container.

The load-bearing wrinkle is the **body-carried correlation model** (D-01), revised after discuss-phase:
correlation is a `Guid CorrelationId` on the **message body** via a slimmed `ICorrelated` interface, not
the MassTransit envelope. This is a **reconciliation of already-shipped Phase 17 + Phase 18 code**, done
as Phase 19 implementation work. The slim is a **narrowing** (6 get-only Guids → 1 init-set Guid), Start/Stop
implement it, and the Phase 18 inbound filter must be re-pointed from `context.CorrelationId` (envelope)
to `context.Message is ICorrelated → CorrelationId` (body). **This breaks the shipped
`ConsoleCorrelationFilterTests` outright** (its `ProbeMessage` has 6 Guids and asserts the published
*envelope* `CorrelationId`) — that test must be rewritten, not merely extended. `[VERIFIED: read both files]`

The single most important topology fact: **same queue name = competing-consumer load-balancing; unique
queue name per replica = fan-out broadcast.** Each replica must bind a non-durable + auto-delete queue
named `orchestrator-{InstanceId}` so RabbitMQ's exchange fans the message to every per-replica queue.

**Primary recommendation (now VERIFIED end-to-end):** register both consumers via
`x.AddConsumer<C, D>().Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` with a
`ConsumerDefinition` per consumer carrying `UseMessageRetry(r => { r.Immediate(N); r.Ignore<WorkflowRootNotFoundException>(); })`.
MassTransit's `ConfigureEndpoints` **groups consumers that share the same endpoint name onto one receive
endpoint** (VERIFIED — Context7), so both Start and Stop land on the single per-replica
`orchestrator-{InstanceId}` queue with **no BaseConsole.Core change** (the existing
`AddBaseConsoleMessaging` already calls `ConfigureEndpoints(ctx)`). Publish-side: minimal
`AddMassTransit(x => x.UsingRabbitMq(...))` with no `AddConsumer`, inject `IPublishEndpoint`, set the body
field at construction with `NewId.NextGuid()`. Soften the bus health check via
`bus.ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded)` on the WebApi only.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Mint correlation Guid + publish Start/Stop | API / Backend (`OrchestrationService`) | — | Bus-stage id minted at the publish boundary (D-02/D-04) |
| Publish-only bus join + host config | API infra (`BaseApi.Core.AddBaseApiMessaging`) | — | Mirrors `AddBaseApi` infra vs `AddAppFeatures` concrete (D-03) |
| Bus health soft-dependency (Degraded) | API infra (`AddBaseApiMessaging` health opts) | — | CRUD readiness must not couple to broker (D-05, MSG-WEBAPI-04) |
| Fan-out receive endpoint binding | Orchestrator console (`.Endpoint(e => e.InstanceId)` + `ConsumerDefinition`) | BaseConsole.Core (bus skeleton) | InstanceId-unique queue is consumer-specific config (D-06) |
| Consume → correlated log scope from body | BaseConsole.Core inbound filter (re-pointed) | Orchestrator consumer | Filter is bus-wide infra; body read is the Phase 19 re-point (D-01) |
| Business-vs-infra ack split | Orchestrator consumer + ConsumerDefinition retry | — | Catch-and-ack is consumer logic; `Ignore<>`/retry is definition config (D-07/D-08) |
| Read Redis L2 root | Orchestrator consumer (read-only) | Redis (StackExchange) | No writes this milestone (ORCH-CON-04) |
| RabbitMQ broker + healthcheck | Infra (compose) | — | Compose service + `service_healthy` gate (D-09, INFRA-RMQ-02) |
| Orchestrator container | Infra (Dockerfile + compose) | — | First runnable Orchestrator (D-09) |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, consumers, definitions, retry, test harness | Pinned `Directory.Packages.props:126`; v9+ commercial — **must stay 8.5.5** `[VERIFIED]` |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport (`UsingRabbitMq`, endpoint durability/auto-delete) | Pinned `:127` `[VERIFIED]` |
| StackExchange.Redis | 2.13.1 | L2 root read (`IConnectionMultiplexer`) | Pinned `:120`; soft-dep in BaseConsole.Core `[VERIFIED]` |
| OpenTelemetry | 1.15.3 (core/hosting/OTLP), 1.15.0 (runtime) | MEL logs + metrics + MassTransit meter | Pinned `:72-79` `[VERIFIED]` |
| AspNetCore.HealthChecks.UI.Client | 9.0.0 | Health JSON body writer | Pinned `:91` `[VERIFIED]` |
| xUnit v3 | 3.2.2 | Tests + `AddMassTransitTestHarness` (in-memory, ships in core MT pkg) | Pinned `:110-112` `[VERIFIED]` |

**Verified CPM state:** MassTransit + MassTransit.RabbitMQ `PackageVersion` entries exist at 8.5.5
(`Directory.Packages.props:126-127`). `BaseConsole.Core.csproj` already has the `PackageReference`s
(lines 41-42). **Phase 19 must ADD `<PackageReference Include="MassTransit" />` + `MassTransit.RabbitMQ`
to `BaseApi.Core.csproj` (D-03) and to the new `Orchestrator.csproj`** (no `Version=` — CPM).
`[VERIFIED: read props + BaseConsole.Core.csproj]`

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| MassTransit in-memory harness | 8.5.5 | `AddMassTransitTestHarness` (in core pkg) for consumer ack tests | This phase's consumer tests; real-broker = Phase 20 `[VERIFIED: ConsoleCorrelationFilterTests.cs:59]` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Hand-rolled slim `ICorrelated` body field | `CorrelatedBy<Guid>` (MT envelope auto-bridge) | **Rejected (D-01 deferred):** drags MassTransit into pure-POCO `Messaging.Contracts` |
| `NewId.NextGuid()` | `Guid.NewGuid()` | NewId is sequential (COMB) → better broker/ES-index locality; either correct (discretion). MT v8 default ProcessId provider ensures cross-process uniqueness `[VERIFIED]` |
| Shared endpoint via same-name definitions | Two separate endpoints | **Rejected (D-06):** shared endpoint keeps both control messages on one per-replica queue |

**Installation:**
- `BaseApi.Core.csproj`: add `<PackageReference Include="MassTransit" />` + `<PackageReference Include="MassTransit.RabbitMQ" />` (CPM, no `Version=`) + `<ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />`. `[VERIFIED: D-03]`
- New `src/Orchestrator/Orchestrator.csproj`: `Sdk="Microsoft.NET.Sdk"` (mirror `BaseConsole.Core.csproj`, NOT `.Web`), inherits `net8.0`. `<ProjectReference>` to `BaseConsole.Core` + `Messaging.Contracts`. Add a direct `<PackageReference Include="MassTransit" />` for the consumer/definition symbols (flows transitively via BaseConsole.Core; direct ref is cleaner — planner confirms). `[ASSUMED — transitive may suffice]`

> **`BaseConsole.Core` is `Microsoft.NET.Sdk` + `FrameworkReference Microsoft.AspNetCore.App`** (NOT `.Web`) —
> confirmed by reading `BaseConsole.Core.csproj`. The embedded health listener is
> `EmbeddedHealthEndpointService` (an `IHostedService`). **Planner: read Phase 18
> `BaseConsoleObservabilityExtensions.cs` + `BaseConsoleServiceCollectionExtensions.cs` to confirm whether
> `Program.cs` uses `WebApplication.CreateBuilder` or `Host.CreateApplicationBuilder` before writing it.** `[ASSUMED: Generic Host]`

## Architecture Patterns

### System Architecture Diagram

```
                         HTTP stage (X-Correlation-Id, string — HttpContext.Items["CorrelationId"])
  POST /api/v1/orchestration/start ──► OrchestrationController.Start([FromBody] List<Guid> workflowIds)
                                              │
                                              ▼
                                       OrchestrationService.StartAsync(workflowIds, ct)
                                         1. ExistenceCheckAsync (Postgres 404 gate)
                                         2. per workflow: pre-clean → LoadL1 → 3 validators → L2 write (Redis)
                                         3. ── PUBLISH BOUNDARY (stage handoff, D-04) ──
                                            await _publishEndpoint.Publish(
                                              new StartOrchestration(workflowIds.ToArray())
                                                  { CorrelationId = NewId.NextGuid() }, ct)
                                              │
                                              ▼
                ┌──────────── RabbitMQ exchange  Messaging.Contracts:StartOrchestration ───────────────┐
                │  MassTransit publishes to the message-type exchange; each bound queue gets a copy     │
                └───────────┬───────────────────────────────────┬──────────────────────────────────────┘
                            │                                    │
              orchestrator-{InstanceId-A}            orchestrator-{InstanceId-B}   (Durable=false, AutoDelete=true / Temporary)
                            │                                    │
                            ▼                                    ▼
              InboundCorrelationConsumeFilter<T> (BaseConsole.Core) ── Phase 19 RE-POINT:
                BEFORE: context.CorrelationId (envelope)
                AFTER : (context.Message as ICorrelated)?.CorrelationId.ToString() ?? <fallback>
                opens MEL scope CorrelationKeys.LogScope ("CorrelationId") = body corrId
                            │
                            ▼
              Start/StopOrchestrationConsumer.Consume(ConsumeContext<StartOrchestration>)
                foreach workflowId:
                   db.StringGetAsync(RedisProjectionKeys.Root(prefix, workflowId))   // "{prefix}{workflowId}"
                   ├─ present  → JsonSerializer.Deserialize<WorkflowRootProjection> → LOG "scheduler job start" ─► ACK
                   └─ absent   → business failure → LOG at correlated scope → ACK (D-07; never throw)
                infra fault (Redis/broker) → throw → UseMessageRetry(Ignore<biz>) → _error DLQ (D-08)
```

### Recommended Project Structure (new + touched — all paths VERIFIED to exist)
```
src/
├── Messaging.Contracts/                 # MODIFY
│   ├── ICorrelated.cs                    # SLIM: { Guid CorrelationId } get-only  (currently 6 get-only Guids)
│   ├── StartOrchestration.cs             # ADD : ICorrelated { Guid CorrelationId { get; init; } } (currently bare record)
│   ├── StopOrchestration.cs             # ADD : ICorrelated (currently bare record)
│   ├── CorrelationKeys.cs                # UNCHANGED (LogScope = "CorrelationId")
│   └── Projections/WorkflowRootProjection.cs  # UNCHANGED read-shape (correlationId now vestigial)
├── BaseConsole.Core/
│   └── Messaging/InboundCorrelationConsumeFilter.cs   # RE-POINT envelope→body (Phase 19)
├── BaseApi.Core/
│   └── DependencyInjection/
│       └── MessagingServiceCollectionExtensions.cs    # NEW: AddBaseApiMessaging(this IServiceCollection, IConfiguration)
├── BaseApi.Service/
│   ├── Program.cs                        # chain AddBaseApiMessaging after AddBaseApi<AppDbContext>
│   ├── appsettings.json                  # + RabbitMq: section
│   └── Features/Orchestration/
│       ├── OrchestrationService.cs        # inject IPublishEndpoint; Publish after L2 write / Stop gate
│       └── OrchestrationServiceCollectionExtensions.cs  # add IPublishEndpoint to the explicit factory ctor call
└── Orchestrator/                          # NEW runnable project (mirrors src/BaseApi.Service)
    ├── Orchestrator.csproj                # Microsoft.NET.Sdk; refs BaseConsole.Core + Messaging.Contracts
    ├── Program.cs                         # AddBaseConsoleObservability + AddBaseConsole + AddBaseConsoleMessaging(cfg, x => {...}) + RunAsync
    ├── appsettings.json                   # RabbitMq:, Orchestrator:InstanceId, ConnectionStrings:Redis (or Redis:), Service:, ConsoleHealth:
    ├── Consumers/
    │   ├── StartOrchestrationConsumer.cs + StartOrchestrationConsumerDefinition.cs
    │   └── StopOrchestrationConsumer.cs  + StopOrchestrationConsumerDefinition.cs
    └── Dockerfile                         # mirror root Dockerfile multi-stage net8.0

tests/BaseApi.Tests/
├── Console/ConsoleCorrelationFilterTests.cs   # REWRITE for body read (currently asserts envelope + 6-Guid ProbeMessage)
└── Orchestrator/                              # NEW consumer ack-split tests (in-memory harness)  [discretion: vs separate project]
```

### Pattern 1: Fan-out via `.Endpoint(e => e.InstanceId)` + shared-name `ConsumerDefinition` (RECOMMENDED) `[VERIFIED: Context7 MassTransit docs]`
**What:** MT has a first-class `e.InstanceId` and `e.Temporary` on the per-consumer-registration
`.Endpoint(...)` configurator (VERIFIED: *"Endpoint configuration settings include the queue name, an
optional unique instance ID for fan-out scenarios, and a temporary flag to remove the endpoint after the
bus stops"*). MT appends the InstanceId to the endpoint name → a distinct per-replica queue.

**Two consumers on ONE queue — RESOLVED (VERIFIED):** `ConfigureEndpoints` docs state: *"If multiple
consumer types share the same endpoint name, they will be grouped together on the same receive endpoint.
Definitions associated with each consumer type are applied during this configuration process."* So both
Start and Stop definitions naming the same endpoint (same base name + same `InstanceId`) co-host on one
per-replica queue — **no BaseConsole.Core change** (CONTEXT: "the console base requires NO change"). The
existing `AddBaseConsoleMessaging` already calls `c.ConfigureEndpoints(ctx)`
(`MessagingServiceCollectionExtensions.cs:44`, VERIFIED).

**ConsumerDefinition (VERIFIED shape):**
```csharp
// Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs
public sealed class StartOrchestrationConsumerDefinition
    : ConsumerDefinition<StartOrchestrationConsumer>
{
    public StartOrchestrationConsumerDefinition()
    {
        EndpointName = "orchestrator";   // shared base name; .Endpoint(e => e.InstanceId) appends the per-replica suffix
    }

    protected override void ConfigureConsumer(            // VERIFIED signature
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StartOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Ack split (D-07/D-08): bounded retry for infra; business failure never retries.
        endpointConfigurator.UseMessageRetry(r =>         // VERIFIED API
        {
            r.Immediate(3);
            r.Ignore<WorkflowRootNotFoundException>();      // VERIFIED Ignore<T>() on retry config
        });
        // RabbitMQ temporary fan-out queue, via the VERIFIED callback pattern:
        endpointConfigurator.AddConfigureEndpointCallback(cfg =>   // VERIFIED (job-consumer SetQuorumQueue example)
        {
            if (cfg is IRabbitMqReceiveEndpointConfigurator rmq)
            {
                rmq.Durable = false;
                rmq.AutoDelete = true;
            }
        });
    }
}
// StopOrchestrationConsumerDefinition mirrors this EXACTLY (same EndpointName = "orchestrator").
```
**Registration (in the Orchestrator's `configureConsumers` lambda — VERIFIED `.Endpoint(e => e.InstanceId)`):**
```csharp
// passed to AddBaseConsoleMessaging(cfg, x => { ... })
x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });   // VERIFIED InstanceId + Temporary
x.AddConsumer<StopOrchestrationConsumer,  StopOrchestrationConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });   // SAME instanceId → SAME queue (grouped)
```
> **`e.Temporary = true`** is the documented "remove the endpoint after the bus stops" flag — the cleanest
> way to get a non-durable/auto-delete fan-out queue. Setting it on `.Endpoint(...)` AND/OR the
> `Durable=false/AutoDelete=true` callback in the definition both work; `e.Temporary` is the simplest.
> Planner may use `e.Temporary = true` alone and drop the `AddConfigureEndpointCallback`. `[VERIFIED: Context7]`

### Pattern 2: Two consumers on one explicit ReceiveEndpoint (alternative, needs a base seam) `[VERIFIED: Context7]`
The explicit form (also VERIFIED) co-hosts two consumers but lives in the bus factory (`UsingRabbitMq`),
which the Phase 18 base owns — using it would require adding an optional bus-factory callback to
`AddBaseConsoleMessaging`. **Prefer Pattern 1** (no base change). Keep this only if Pattern 1's grouping
behaves unexpectedly in the spike:
```csharp
cfg.ReceiveEndpoint($"orchestrator-{instanceId}", e =>
{
    e.Durable = false; e.AutoDelete = true;
    e.UseMessageRetry(r => { r.Immediate(3); r.Ignore<WorkflowRootNotFoundException>(); });
    e.ConfigureConsumer<StartOrchestrationConsumer>(context);
    e.ConfigureConsumer<StopOrchestrationConsumer>(context);
});
```

### Pattern 3: Publish-only bus join (`AddBaseApiMessaging`) + Degraded health `[VERIFIED API: Context7; D-02/D-03 intent VERIFIED]`
```csharp
// src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs  (NEW)
public static IServiceCollection AddBaseApiMessaging(this IServiceCollection services,
                                                     IConfiguration cfg)
{
    var host = cfg.Require("RabbitMq:Host");        // RequiredConfig.Require — existing fail-fast helper
    var user = cfg.Require("RabbitMq:Username");
    var pass = cfg.Require("RabbitMq:Password");

    services.AddMassTransit(bus =>
    {
        // D-05 — soften the auto-registered bus health check (WebApi ONLY). VERIFIED API:
        bus.ConfigureHealthCheckOptions(o =>
        {
            o.Name = "masstransit";                          // keep default name
            o.MinimalFailureStatus = HealthStatus.Degraded;  // never escalate to Unhealthy
            // do NOT touch o.Tags — keeps default ["ready","masstransit"] (D-05 rejects re-tagging)
        });
        // NO bus.AddConsumer(...) — publish-only
        bus.UsingRabbitMq((context, busCfg) =>
        {
            busCfg.Host(host, h => { h.Username(user); h.Password(pass); });
            // NO busCfg.ConfigureEndpoints(context) — nothing to bind (publish-only)
        });
    });
    return services;
}
```
> **Health-check (VERIFIED: Context7 + `MessagingServiceCollectionExtensions.cs:20-23`):** MT's default
> behavior already reports **Degraded** when broker connectivity is lost *at runtime* and **Unhealthy** on
> *startup* issues. Setting `MinimalFailureStatus = Degraded` caps it so it **never** reaches Unhealthy →
> CRUD `/health/ready` (keyed off the `ready` tag) stays 200. Docs: *"If set to Degraded, the check will
> report as degraded upon issues but will never report as unhealthy."* Do NOT touch `o.Tags` (custom tags
> REPLACE the defaults — Phase 18 confirms). The Orchestrator does NOT call this → keeps the default hard
> posture. `RequiredConfig.Require` exists in BOTH `BaseApi.Core` and `BaseConsole.Core` (VERIFIED).

### Pattern 4: Publish with body correlation (D-02 call shape) `[VERIFIED call shape: D-02 verbatim; NewId VERIFIED Context7]`
```csharp
// OrchestrationService.StartAsync — AFTER the per-workflow L2-write loop (D-04)
await _publishEndpoint.Publish(
    new StartOrchestration(workflowIds.ToArray()) { CorrelationId = NewId.NextGuid() },
    ct);
// StopAsync — AFTER the EXISTS gate passes, publish StopOrchestration the same way.
```
> Injecting `IPublishEndpoint`: `OrchestrationService`'s ctor is **`internal`** and DI-registered via an
> **explicit factory** in `OrchestrationServiceCollectionExtensions.cs:56-67` (because the ctor takes
> internal seam types). Adding `IPublishEndpoint` = (a) add the ctor parameter, (b) add
> `sp.GetRequiredService<IPublishEndpoint>()` to that factory call. `IPublishEndpoint` is registered by
> `AddMassTransit`. **`StartAsync(IReadOnlyList<Guid> workflowIds, ...)` → `.ToArray()` confirmed.** `[VERIFIED]`

### Anti-Patterns to Avoid
- **Same queue name across replicas** (without distinct InstanceId) → load-balancing not fan-out (the #1 trap). `[VERIFIED: ROADMAP:18, PITFALLS:1]`
- **Durable per-replica queues** → orphan/accumulate on rescale. Use `e.Temporary`/auto-delete. `[VERIFIED: REQUIREMENTS Out-of-Scope]`
- **`catch (Exception)` in the consumer** → swallows real crashes (PITFALLS:2). Catch only the business outcome. `[VERIFIED: PITFALLS]`
- **Throwing on business failure without `Ignore<>`** → dead-letters to `_error`. `[VERIFIED: D-07]`
- **Setting the MT envelope `CorrelationId`** → second confusing source; correlation is body-only (D-02).
- **`ConfigureEndpoints` on the publish-only WebApi bus** → no endpoints to bind.
- **Referencing `BaseConsole.Core` from `BaseApi.*`** → forbidden (MSG-WEBAPI-01, D-03).
- **Adding `.WithTracing`/`.AddSource("MassTransit")` to the Orchestrator** → resurrects traces (PITFALLS:3).
- **Adding RabbitMQ to WebApi `/health/ready` as hard** → breaks soft/hard boundary (PITFALLS:4).
- **Overriding bus health-check `o.Tags`** → REPLACES defaults `["ready","masstransit"]`, breaking the `ready` probe. `[VERIFIED]`

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Broadcast to N replicas | Manual exchange/queue declaration | `.Endpoint(e => { e.InstanceId = id; e.Temporary = true; })` | MT auto-declares the type exchange + per-instance queue |
| Bounded retry + skip-on-business-error | Custom try/catch retry loop | `UseMessageRetry(r => { r.Immediate(N); r.Ignore<T>(); })` | MT handles redelivery + the `Ignore<>` filter |
| Dead-letter on exhaustion | Manual `_error` publish | MT default `{queue}_error` | MT auto-creates + routes exhausted faults |
| Sequential Guid | `Guid.NewGuid()` | `NewId.NextGuid()` | COMB/sequential — better broker + ES locality |
| Bus health check | Hand-rolled `IHealthCheck` | MT auto-registered `ready`+`masstransit` check + `ConfigureHealthCheckOptions` | Phase 18 relies on it; D-05 just tunes `MinimalFailureStatus` |
| In-memory consumer tests | Mock `ConsumeContext` | `AddMassTransitTestHarness` | Real MT pipeline in-memory; asserts consumed/faulted/published |
| L2 key string | Re-derive ad hoc | `RedisProjectionKeys.Root(prefix, id)` = `"{prefix}{workflowId}"` ("D" Guid format) | Single source of truth (currently `internal` — hoist or duplicate per discretion) |

**Key insight:** MassTransit owns the receive pipeline (topology, retry, dead-letter, correlation filters —
all shipped Phase 18). Phase 19 is **configuration + two consumer classes + the correlation re-point**, not
bus plumbing.

## Runtime State Inventory

> Phase 19 includes a shipped-code reconciliation (slim `ICorrelated`, re-point inbound filter) — a
> refactor-flavored change — so this section applies to that slice.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | `WorkflowRootProjection.correlationId` is written to the Redis L2 root by `RedisProjectionWriter` (`OrchestrationService.StartAsync` passes `correlationId` from `HttpContext.Items["CorrelationId"]`, line 108/141). Under D-01 it becomes **vestigial** (still written, never read). No stored KEY changes. | None — left written (discretion). No data migration. The `UpsertAsync(snapshot, correlationId, ct)` call site is unchanged. `[VERIFIED]` |
| Live service config | RabbitMQ queues are temporary/auto-delete — no durable runtime state. RabbitMQ is net-new (no existing broker). | None. |
| OS-registered state | None — no Task Scheduler / systemd touched. | None — verified by absence in CONTEXT/REQUIREMENTS. |
| Secrets/env vars | New `RabbitMq:{Host,Username,Password}` + `Orchestrator:InstanceId`. Dev creds `guest/guest` (D-09). Compose env overrides: `RabbitMq__*`, `Orchestrator__InstanceId`. Existing convention: `ConnectionStrings__Redis` injected via compose env (`compose.yaml:163`). | Add config keys to both hosts' appsettings + compose env. |
| Build artifacts / installed packages | New `MassTransit`/`MassTransit.RabbitMQ` `PackageReference` in `BaseApi.Core` + new `Orchestrator` project. Slimming `ICorrelated` recompiles `Messaging.Contracts` consumers. | `dotnet restore`/`build`. The narrowing recompiles `BaseConsole.Core` (the two outbound filters reference `ICorrelated` by type) + the filter test. |

**Reconciliation impact of slimming `ICorrelated` (6 get-only Guids → 1 get-only Guid) — VERIFIED references:**
- `src/Messaging.Contracts/ICorrelated.cs` — currently 6 get-only Guids (lines 6-11). **Slim to one
  `Guid CorrelationId { get; }`** (init is on the implementer, not the interface — Pitfall 7). `[VERIFIED]`
- `src/BaseConsole.Core/Messaging/OutboundCorrelationPublishFilter.cs` + `OutboundCorrelationSendFilter.cs`
  — gated on `message is ICorrelated` (the TYPE, not the removed members). **Will NOT break** from the slim. `[VERIFIED: PublishFilter uses `is ICorrelated` then `accessor.Get()`, never the 5 ids]`
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` — currently reads
  `context.CorrelationId` (envelope, line 34); does **NOT** reference `ICorrelated`. **Phase 19 ADDS the
  body read here.** `[VERIFIED]`
- `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` — **WILL BREAK on BOTH counts:** (a) its
  `ProbeMessage` declares all 6 Guids (lines 25-31) → won't compile after the slim; (b) it asserts the
  published **envelope** `CorrelationId` (lines 93, 101) → semantically wrong after the body re-point.
  **REWRITE, not extend.** `[VERIFIED]`
- **`OrchestrationService` does NOT reference `ICorrelated`** — uses a separate HTTP-stage `string`
  correlationId from `HttpContext.Items`. No break. `[VERIFIED]`
- Blast radius is small (the 6-id interface had zero implementers per Phase 17 D-09). Only compile break:
  the test's `ProbeMessage`. **Planner: grep `.ExecutionId`/`.StepId`/`.ProcessorId`/`.EntryId` to confirm
  no production member access on `ICorrelated`-typed values (expected: none).**

## Common Pitfalls

(Cross-referenced with the milestone `.planning/research/PITFALLS.md` — read it in full; its 7 pitfalls
map directly. Phase-19 emphasis below.)

### Pitfall 1: Unique-vs-shared queue name (fan-out vs load-balance)
All replicas on the same queue → RabbitMQ round-robins; only ONE replica sees each message. Invisible at
1 replica. **Avoid:** distinct `e.InstanceId` per replica (D-06). Two-bus broadcast proof = Phase 20
(TEST-RMQ-01). `[VERIFIED: ROADMAP:18, PITFALLS:1]`

### Pitfall 2: Business failure dead-lettered
**Do BOTH (D-07/D-08):** catch the business outcome inside `Consume` and complete (primary, MSG-ACK-01
"never throw") AND configure `Ignore<WorkflowRootNotFoundException>` in the definition (MSG-ACK-03 P2 +
defends against escape). Catch ONLY `WorkflowRootNotFoundException` (or check `raw.IsNullOrEmpty` and
`continue`), never `catch (Exception)`. `[VERIFIED: D-07/D-08, PITFALLS:2]`

### Pitfall 3: Broker-down flips CRUD readiness (WebApi)
`MinimalFailureStatus = Degraded` (D-05). Orchestrator keeps it hard (`BusReadyHealthCheck`). **Inverse
postures — do not unify.** `[VERIFIED: D-05, 18-CONTEXT, PITFALLS:4]`

### Pitfall 4: MassTransit resurrecting a traces pipeline
Orchestrator OTel is metrics-only (Phase 18 `BaseConsoleObservabilityExtensions`). Do NOT add
`.WithTracing`/`.AddSource("MassTransit")`. `[VERIFIED: ROADMAP:20, PITFALLS:3]`

### Pitfall 5: Positional-record `init` + interface
An interface can't declare `init`; a positional record binds params positionally. Use
`interface ICorrelated { Guid CorrelationId { get; } }`; the implementer
`record StartOrchestration(Guid[] WorkflowIds) : ICorrelated { public Guid CorrelationId { get; init; } }`
adds `CorrelationId` as a **non-positional init member**. Publisher uses
`new StartOrchestration(ids) { CorrelationId = ... }` (D-02's shape). `[VERIFIED: C# semantics + current bare-record shape]`

### Pitfall 6: AsyncLocal accessor is `string?`, not `Guid`
The shipped `AsyncLocalCorrelationAccessor` stores `string?` (VERIFIED line 12); the inbound filter calls
`accessor.Set(corrId)` with a `string`. The body `ICorrelated.CorrelationId` is a `Guid`. The re-point
reads `(context.Message as ICorrelated)?.CorrelationId.ToString()` to keep the accessor's `string`
contract. **Do NOT change the accessor to `Guid`** — the outbound filter does `Guid.TryParse` on the
string (VERIFIED PublishFilter:18). `[VERIFIED]`

### Pitfall 7: Overriding the bus health-check tags
Setting `o.Tags` REPLACES the defaults `["ready","masstransit"]` → the `ready` probe stops seeing the bus
check. Only set `o.MinimalFailureStatus` (and optionally `o.Name`); leave `o.Tags` alone. `[VERIFIED: Context7 + Phase 18 comment]`

## Code Examples

### ICorrelated slimmed (VERIFIED target shape)
```csharp
// src/Messaging.Contracts/ICorrelated.cs  — Phase 19 replaces the 6-Guid body with:
namespace Messaging.Contracts;

/// <summary>Universal correlation contract — body-carried correlation id (v3.4.0 model, D-01).</summary>
public interface ICorrelated
{
    Guid CorrelationId { get; }   // interface get-only; implementers use { get; init; }
}
```

### StartOrchestration / StopOrchestration implement it (current files are bare records — VERIFIED)
```csharp
// src/Messaging.Contracts/StartOrchestration.cs  (currently: `public sealed record StartOrchestration(Guid[] WorkflowIds);`)
namespace Messaging.Contracts;

public sealed record StartOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }   // non-positional init member
}
// StopOrchestration mirrors this exactly.
```

### Inbound filter re-point: envelope → body (VERIFIED current code at line 34)
```csharp
// src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs : Send(...)
// BEFORE (shipped):
//   var corrId = context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString();
// AFTER (Phase 19, D-01 — read the body, keep the string accessor contract):
var corrId = (context.Message as ICorrelated)?.CorrelationId.ToString()
             ?? context.CorrelationId?.ToString()       // optional envelope fallback (planner may drop)
             ?? Guid.NewGuid().ToString();
accessor.Set(corrId);
using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId }))
    await next.Send(context);
```
> Keep `where T : class` (NOT `where T : ICorrelated`) — registered bus-wide via
> `UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx)` and must tolerate non-correlated
> messages; the `as ICorrelated` handles them. `[VERIFIED: registration line 41; filter signature line 30]`

### Consumer with business-ack/infra-throw split
```csharp
public sealed class StartOrchestrationConsumer(
    IConnectionMultiplexer redis, ILogger<StartOrchestrationConsumer> logger)
    : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        // Inbound filter already opened the "CorrelationId" scope from the body.
        var db = redis.GetDatabase();   // infra fault here THROWS → retry → _error (D-08)
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            var raw = await db.StringGetAsync(RedisProjectionKeys.Root(prefix, workflowId)); // "{prefix}{workflowId}"
            if (raw.IsNullOrEmpty)
            {
                // BUSINESS failure → log + complete (ack), never throw (D-07 / MSG-ACK-01)
                logger.LogWarning("Workflow {WorkflowId} absent from L2 — business failure, acking", workflowId);
                continue;
            }
            var root = JsonSerializer.Deserialize<WorkflowRootProjection>(raw!);
            logger.LogInformation("Scheduler job start (seam) for {WorkflowId}", workflowId); // ORCH-CON-04 seam
        }
        // returns normally → ACK
    }
}
```
> `prefix`: `RedisProjectionKeys` is `internal` in `BaseApi.Service` with `Root(string prefix, Guid)`;
> prefix from `RedisProjectionOptions.KeyPrefix`. **Discretion:** hoist `RedisProjectionKeys.Root` + the
> prefix to `Messaging.Contracts` (single source), OR duplicate the `"{prefix}{workflowId}"` shape with a
> comment. The Orchestrator needs its own prefix config read. `[VERIFIED: keys internal, "D" Guid format]`

### Compose RabbitMQ service (mirrors the verified redis/postgres idiom)
```yaml
# compose.yaml — NEW service (mirror the sk-* container_name + CMD-form healthcheck convention)
rabbitmq:
  image: rabbitmq:4.1.8-management-alpine
  container_name: sk-rabbitmq          # sk-* cross-stack uniqueness convention (Phase 11/12)
  restart: unless-stopped
  ports:
    - "5673:5672"      # AMQP (host 5673 avoids local 5672 collision, D-09)
    - "15673:15672"    # management UI (host 15673)
  environment:
    RABBITMQ_DEFAULT_USER: guest
    RABBITMQ_DEFAULT_PASS: guest
  healthcheck:
    test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]   # CMD form (no shell) — matches redis idiom
    interval: 10s
    timeout: 5s
    retries: 5
    start_period: 40s   # broker cold-start; tune like ES start_period:60s pattern
```
> `start_period` matters: RabbitMQ cold-start can exceed the default (existing convention sets generous
> `start_period`, e.g. ES 60s). Use ~40s. `[VERIFIED healthcheck cmd: STACK.md; start_period value ASSUMED]`

### Orchestrator service + WebApi depends_on (compose)
```yaml
baseapi-service:
  depends_on:
    rabbitmq:
      condition: service_healthy   # INFRA-RMQ-02 — Start/Stop path hard-dep
    # ... existing postgres/redis/etc.
  environment:
    RabbitMq__Host: rabbitmq        # compose DNS (NOT 5673 — that's the host-side port)
    RabbitMq__Username: guest
    RabbitMq__Password: guest

orchestrator:
  build:
    context: .
    dockerfile: src/Orchestrator/Dockerfile
  restart: unless-stopped
  depends_on:
    rabbitmq:
      condition: service_healthy
    redis:
      condition: service_healthy
  environment:
    RabbitMq__Host: rabbitmq
    RabbitMq__Username: guest
    RabbitMq__Password: guest
    ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"  # mirror baseapi-service:163
    Orchestrator__InstanceId: orchestrator-1   # or leave unset → generated GUID (D-06)
  healthcheck:
    test: ["CMD", "wget", "--spider", "-q", "http://localhost:8081/health/ready"]  # ConsoleHealth:Port default 8081 (18 D-04)
    interval: 10s
    timeout: 3s
    retries: 5
    start_period: 30s
```
> Orchestrator `/health/ready` is hard-on-broker (Phase 18 `BusReadyHealthCheck`); `depends_on rabbitmq:
> service_healthy` + the healthcheck enforce it. ConsoleHealth port default `8081` (Phase 18 D-04).
> `[VERIFIED: 18-CONTEXT D-04; healthcheck pattern from compose.yaml:168]`

### Dockerfile (mirror root Dockerfile — net8.0 multi-stage)
```dockerfile
# src/Orchestrator/Dockerfile  (mirror root Dockerfile; .NET 8 — NOT 9)
# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/Orchestrator/Orchestrator.csproj", "src/Orchestrator/"]
RUN dotnet restore "src/Orchestrator/Orchestrator.csproj"
COPY src/ src/
RUN dotnet publish "src/Orchestrator/Orchestrator.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime   # aspnet (not runtime) — FrameworkReference Microsoft.AspNetCore.App
WORKDIR /app
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8081     # embedded health listener port (ConsoleHealth:Port default 8081)
EXPOSE 8081
ENTRYPOINT ["dotnet", "Orchestrator.dll"]
```
> Use **`aspnet:8.0`** (NOT `runtime:8.0`): `BaseConsole.Core` has `FrameworkReference
> Microsoft.AspNetCore.App` for the embedded Kestrel health listener, which needs the ASP.NET Core shared
> framework at runtime. The root Dockerfile is hard-wired to `BaseApi.Service`; the Orchestrator needs its
> OWN Dockerfile (different COPY list + publish target). `[VERIFIED: BaseConsole.Core.csproj:35; root Dockerfile]`

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Fat 6-id `ICorrelated` get-only (Phase 17 D-09) | Slim `{ Guid CorrelationId }` init-set + deferred `IExecutionCorrelated` | Phase 19 D-01 | Interface segregation; no `Guid.Empty` slots |
| Envelope `ctx.CorrelationId` read (Phase 18 inbound filter) | Body `message is ICorrelated → CorrelationId` | Phase 19 D-01 | Decouples correlation from MT envelope; per-stage handoff |
| Start/Stop bare POCO records (Phase 17 D-10) | Start/Stop implement slim `ICorrelated` | Phase 19 D-01 | Publisher sets body id at construction |

**Deprecated/outdated for THIS milestone:**
- `CorrelatedBy<Guid>` — rejected (couples `Messaging.Contracts` to MassTransit).
- Carrying one HTTP `X-Correlation-Id` across the bus hop — replaced by per-stage mint-at-publish.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| RabbitMQ | Orchestrator consume, WebApi publish, integration tests | ✗ (net-new) | `4.1.8-management-alpine` (compose, D-09) | In-memory `AddMassTransitTestHarness` for consumer tests this phase |
| Redis | L2 root read (orchestrator) + L2 write (WebApi) | ✓ (compose) | `redis:7.4.9-alpine`, host 6380:6379 | — `[VERIFIED: compose.yaml:135-151]` |
| .NET 8 SDK | Build all projects | ✓ | `8.0.421` (global.json) | — `[VERIFIED: README]` |
| Docker / compose | RabbitMQ + orchestrator services | ✓ (existing compose) | Compose v2 | — `[VERIFIED]` |

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit **v3** (`xunit.v3` 3.2.2, `xunit.v3.assert`, `xunit.runner.visualstudio` 3.1.5) `[VERIFIED: props:110-112]` |
| Config file | per `tests/BaseApi.Tests` project (uses `TestContext.Current.CancellationToken` — VERIFIED in ConsoleCorrelationFilterTests) |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Orchestrator"` |
| Full suite command | `dotnet test SK_P.sln` |

> **xUnit v3 idiom (VERIFIED from the shipped test):** `TestContext.Current.CancellationToken`;
> `await using var provider = ...BuildServiceProvider(true)`; harness via
> `provider.GetRequiredService<ITestHarness>()`, `harness.Start()/Stop(ct)`, `harness.Published.Any<T>(...)`,
> `harness.Consumed.Any<T>(ct)`. Mirror `ConsoleCorrelationFilterTests` for new consumer tests.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| MSG-ACK-01 | business failure → ack (consumed, not faulted) | unit (harness) | `dotnet test --filter "FullyQualifiedName~OrchestratorConsumer"` | ❌ Wave 0 |
| MSG-ACK-02/03 | infra fault → faulted/retried; `Ignore<>` skips business | unit (harness) | same | ❌ Wave 0 |
| ORCH-CON-03 | body `CorrelationId` opens `"CorrelationId"` scope | unit (harness + log capture) | same | ❌ Wave 0 (+ REWRITE `ConsoleCorrelationFilterTests`) |
| MSG-WEBAPI-02 | start/stop publishes correct message | unit (harness `IPublishEndpoint` + `harness.Published`) | `--filter "FullyQualifiedName~OrchestrationServicePublish"` | ❌ Wave 0 |
| MSG-WEBAPI-04 | bus check Degraded not Unhealthy | unit (assert option set) | (broker-down fixture is Phase 20 TEST-RMQ-03) | ❌ Wave 0 |
| ICorrelated slim | Phase 17/18 + v3.3.0 suites stay GREEN | regression | `dotnet test SK_P.sln` | ⚠️ `ConsoleCorrelationFilterTests` WILL break → rewrite |
| ORCH-CON-02 fan-out | two-bus broadcast | integration | **DEFERRED to Phase 20 (TEST-RMQ-01)** | n/a |

> **Testable now (in-memory) vs. needs real broker:** `AddMassTransitTestHarness` proves
> business-ack-vs-infra-throw, publish-happened, and the inbound-filter scope on a single in-memory bus.
> It **cannot** prove fan-out broadcast (two bus instances / real exchange topology) or the ES correlation
> surface — both Phase 20. `[VERIFIED: 18-CONTEXT D-02 point 6; 19-CONTEXT OUT-list]`

### Sampling Rate
- **Per task commit:** `dotnet test --filter "FullyQualifiedName~Orchestrator"` (fast harness subset).
- **Per wave merge:** `dotnet test SK_P.sln` (full suite — must stay GREEN through the `ICorrelated` slim +
  filter re-point + the `ConsoleCorrelationFilterTests` rewrite).
- **Phase gate:** full suite GREEN. Triple-SHA `rabbitmqctl list_queues` gate = **Phase 20** (in-memory
  tests leak no broker resources). `[VERIFIED: 18-CONTEXT D-03; 19-CONTEXT OUT-list]`

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` — MSG-ACK-01/02/03 via harness.
- [ ] `tests/BaseApi.Tests/Orchestrator/OrchestrationServicePublishTests.cs` — MSG-WEBAPI-02 publish assertion.
- [ ] **REWRITE** `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` — body read; `ProbeMessage`
      down to `{ Guid CorrelationId }`; assert the inbound filter reads the body, not the envelope.
- [ ] No new framework install — xUnit v3 + MT harness already present.

## Security Domain

> No `.planning/config.json` found this session (planner confirm `security_enforcement`). Including the
> relevant categories; PITFALLS.md "Security Mistakes" covers the domain.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No new auth surface; bus internal (broker creds only) |
| V5 Input Validation | partial | `WorkflowIds[]` validated upstream (v3.3.0 gates); orchestrator treats absent-from-L2 as business failure (ack+log, not crash) |
| V6 Cryptography | no | No new crypto |
| V7 Error Handling | yes | RFC 7807 on Start/Stop broker-down (MSG-WEBAPI-03) — reuse existing ProblemDetails handlers (`FallbackExceptionHandler`) |
| Secrets management | yes | RabbitMQ creds dev `guest/guest` only; env-override in compose; never prod creds in appsettings.json |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Broker creds in source | Information Disclosure | Dev `guest/guest`; compose/k8s env override; never prod creds committed |
| Unbounded retry storm on poison message | DoS | **Bounded** `UseMessageRetry(r.Immediate(N))` + `_error` DLQ (D-08) |
| Durable queue accumulation on rescale | Resource exhaustion | Temporary/auto-delete per-replica queues (`e.Temporary`, D-06) |
| Logging full message bodies | Information Disclosure | Log `CorrelationId` + ids, not full payloads (PITFALLS Security row) |

## Assumptions Log

| # | Claim | Status | Section | Risk if Wrong |
|---|-------|--------|---------|---------------|
| A1 | `.Endpoint(e => { e.InstanceId = ...; e.Temporary = true; })` makes a unique per-instance temporary fan-out queue, honored by `ConfigureEndpoints`; `ConsumerDefinition` + `UseMessageRetry`/`Ignore<T>()` is the retry seam | ✅ VERIFIED (Context7) | Pattern 1 | — (resolved) |
| A2 | **Two consumers sharing the same `EndpointName` are grouped onto ONE receive endpoint by `ConfigureEndpoints`** | ✅ VERIFIED (Context7: *"If multiple consumer types share the same endpoint name, they will be grouped together on the same receive endpoint"*) | Pattern 1 | — (resolved; Pattern 2 is the fallback if grouping behaves oddly in the spike) |
| A3 | `bus.ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded)` is the D-05 API; default tags `["ready","masstransit"]`; tags must NOT be overridden | ✅ VERIFIED (Context7) | Pattern 3, §Bus health | — (resolved) |
| A4 | MT default RabbitMQ topology (publish → message-type exchange → bound queues) yields broadcast when queue names differ | ✅ VERIFIED (Context7 — `TemporaryEndpointDefinition` "typically for fan-out event consumers"; per-instance queue is the documented broadcast mechanism) | Diagram | — (resolved) |
| A5 | `NewId.NextGuid()` sequential GUID; v8 default ProcessId provider for cross-process uniqueness | ✅ VERIFIED (Context7) | §Publish | — (resolved) |
| A6 | `AddConfigureEndpointCallback(cfg => { if (cfg is IRabbitMqReceiveEndpointConfigurator rmq) rmq.Durable=false; rmq.AutoDelete=true; })` reaches RabbitMQ settings from a definition | ✅ VERIFIED (Context7 — job-consumer `SetQuorumQueue` example uses this exact callback shape) | Pattern 1 | — (resolved; `e.Temporary` is the simpler alternative) |
| A7 | `_error` queue auto-created `{queue}_error`; retry-exhausted faults route there; mid-consume crash leaves message unacked | ✅ (long-stable MT/RabbitMQ default; not re-fetched) | §Ack | LOW |
| A8 | Orchestrator `Program.cs` host type (`WebApplication` vs `Host.CreateApplicationBuilder`) | ⚠️ ASSUMED | §Standard Stack note, Open Q4 | MEDIUM — confirm by reading Phase 18 `AddBaseConsole*`/`EmbeddedHealthEndpointService` |
| A9 | Direct `MassTransit` PackageReference in `Orchestrator.csproj` (vs transitive) | ⚠️ ASSUMED | §Installation | LOW — cosmetic |
| A10 | Compose RabbitMQ `start_period: 40s` adequate for cold-start | ⚠️ ASSUMED | §Compose | LOW — tune empirically; healthcheck cmd verified |
| A11 | Optional envelope fallback in re-pointed inbound filter is acceptable (planner may drop for body-only) | ⚠️ design choice | §Inbound filter | LOW |

## Open Questions

1. **Two consumers, one per-instance temporary endpoint — VERIFIED resolved; spike to confirm in this repo's exact wiring.**
   - VERIFIED: same-`EndpointName` consumers are grouped onto one endpoint; `.Endpoint(e => { e.InstanceId;
     e.Temporary; })` makes the per-replica temporary queue; `AddBaseConsoleMessaging` already calls
     `ConfigureEndpoints(ctx)`. **No BaseConsole.Core change expected.**
   - Recommendation: **First planning task = a 20-line in-memory harness spike** registering both
     consumers' definitions at the same name + InstanceId; assert both consume one published message on one
     endpoint. If grouping behaves oddly → fall to Pattern 2 (explicit `ReceiveEndpoint` + a small optional
     bus-factory seam on `AddBaseConsoleMessaging`). Low risk; the verified docs say it should just work.

2. **InstanceId flow into the consumer endpoint (DI vs closure).**
   - Known: `InstanceId` from `Orchestrator:InstanceId` ?? generated GUID (D-06).
   - Recommendation: read `cfg["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N")` in `Program.cs`
     and capture it in the `configureConsumers` lambda's `.Endpoint(e => e.InstanceId = instanceId)` (a
     closure is simplest; the `ConsumerDefinition` only needs `EndpointName = "orchestrator"`). No
     `InstanceIdProvider` DI type needed if the closure is used.

3. **RedisProjectionKeys reuse (hoist vs duplicate) + prefix config.**
   - Known: `RedisProjectionKeys` is `internal` in `BaseApi.Service`, `Root(prefix, id) = "{prefix}{workflowId}"`
     ("D" Guid format); prefix from `RedisProjectionOptions.KeyPrefix` (VERIFIED). Orchestrator can't ref it.
   - Recommendation: hoist `RedisProjectionKeys.Root` + the prefix shape to `Messaging.Contracts`, OR
     duplicate the literal with a comment. Orchestrator needs its own prefix config read.

4. **`Orchestrator.csproj` SDK + `Program.cs` host shape (A8).**
   - Recommendation: read `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs`,
     `BaseConsoleServiceCollectionExtensions.cs`, `EmbeddedHealthEndpointService.cs` to confirm whether the
     chain expects a `WebApplicationBuilder` or `HostApplicationBuilder` before writing `Program.cs`.

## Sources

### Primary (HIGH — VERIFIED by reading this session)
- Planning: `19-CONTEXT.md` (D-01..D-09), `17-CONTEXT.md` (D-09/D-10 AMENDED), `18-CONTEXT.md` (D-01 AMENDED, D-06),
  `REQUIREMENTS.md`, `ROADMAP.md` (Phase 19 SC + cross-phase constraints), `.planning/research/{STACK,PITFALLS}.md`.
- Source: `src/Messaging.Contracts/{ICorrelated,StartOrchestration,StopOrchestration,CorrelationKeys}.cs`,
  `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs`,
  `src/BaseConsole.Core/Messaging/{InboundCorrelationConsumeFilter,OutboundCorrelationPublishFilter,ICorrelationAccessor,AsyncLocalCorrelationAccessor}.cs`,
  `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs`, `BaseConsole.Core.csproj`,
  `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs`,
  `src/BaseApi.Service/Program.cs`, `src/BaseApi.Service/Features/Orchestration/{OrchestrationService,OrchestrationController,OrchestrationServiceCollectionExtensions}.cs`,
  `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs`,
  `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs`.
- Infra: `Directory.Packages.props`, `Directory.Build.props`, `compose.yaml`, `Dockerfile`, `SK_P.sln`, `README.md`.

### Secondary (HIGH — Context7 official MassTransit docs, `/websites/masstransit_massient`, fetched 2026-05-30)
- `ConsumerDefinition` (EndpointName + `ConfigureConsumer(IReceiveEndpointConfigurator, IConsumerConfigurator<T>, IRegistrationContext)` + `UseMessageRetry`): masstransit.massient.com/configuration/consumers
- `.Endpoint(e => { e.InstanceId; e.Temporary; })` per-instance fan-out queue + "Endpoint Configuration" (InstanceId for fan-out, temporary flag): masstransit.massient.com/configuration
- `ConfigureEndpoints` groups same-name consumers onto one endpoint: masstransit.massient.com/configuration
- `AddConfigureEndpointCallback` + `if (cfg is IRabbitMqReceiveEndpointConfigurator rmq)`: masstransit.massient.com/configuration/job-consumer
- `TemporaryEndpointDefinition` "typically for fan-out event consumers"; `?temporary=true` / `?durable=false` address params: masstransit.massient.com/configuration/transports/rabbitmq
- `ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded)`; default tags `["ready","masstransit"]`; Degraded-on-runtime-broker-loss: masstransit.massient.com/configuration
- `NewId.NextGuid()` sequential GUID; v8 default ProcessId provider: masstransit.massient.com/concepts/messages + /reference/newid
- Multi-consumer on one explicit `ReceiveEndpoint`: masstransit.massient.com/configuration

### Tertiary (confirm in planning)
- `_error` queue auto-creation (long-stable, not re-fetched). Orchestrator `Program.cs` host type (A8).

## Metadata

**Confidence breakdown:**
- User constraints / requirements mapping: HIGH — verbatim from CONTEXT/REQUIREMENTS/ROADMAP.
- Repo facts (file shapes, CPM pins, .NET 8, the `ICorrelated`/filter/test breakage, the publish seam,
  RedisProjectionKeys, compose idiom, Dockerfile): HIGH — all read this session.
- MassTransit 8.5.5 fan-out config, retry/Ignore, health-options, NewId, multi-consumer grouping: HIGH —
  verified via Context7 official docs. The lone residual is a confirmatory spike of same-name grouping in
  this repo's exact `AddBaseConsoleMessaging` wiring (low risk; docs say it works).
- Pitfalls: HIGH (conceptual) — corroborated by the milestone PITFALLS.md.

**Research date:** 2026-05-30
**Valid until:** 2026-06-29 (stack stable; MassTransit pinned 8.5.5).
