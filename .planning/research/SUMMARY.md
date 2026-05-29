# Project Research Summary

**Project:** Steps API -- v3.4.0 (BaseConsole + Orchestrator Messaging)
**Domain:** .NET 8 Generic-Host console platform + MassTransit/RabbitMQ fan-out messaging
**Researched:** 2026-05-30
**Confidence:** HIGH

## Executive Summary

v3.4.0 adds the first message-consumer side of the platform: a reusable BaseConsole.Core Generic-Host library (the console-side mirror of BaseApi.Core), a first Orchestrator concrete console, a Messaging.Contracts shared assembly, MassTransit 8.5.5/RabbitMQ transport, and end-to-end CorrelationId propagation proven in Elasticsearch logs. The milestone stops cleanly at the scheduler-job-start log seam -- no Quartz, no round-trip, no Redis writes from the Orchestrator. The critical licensing constraint is MassTransit 8.5.5 pinned in CPM: v9.0+ (first stable 9.1.1, released 2026-05-13) is a commercial product at $400/month minimum; v8.x remains Apache-2.0 through end-2026. All research confidence is HIGH across the four files.

The recommended build sequence is: Messaging.Contracts (leaf with no host dependency) -> BaseConsole.Core -> parallel Orchestrator + WebApi bus wiring -> RabbitMQ compose tier -> synthetic outbound-filter harness test. The entire consumer-side OTel story is metrics + logs only (MEL bridge, AddMeter(InstrumentationOptions.MeterName), no .WithTracing) -- MassTransit 8 emits natively via ActivitySource and Meter; no separate instrumentation package exists or is needed. Health probes in the console use an embedded minimal Kestrel WebApplication inside an IHostedService so the validated three-probe tag discipline and the MapHealthChecks endpoint surface carry over verbatim from BaseApi.Core.

The two highest risks are (1) the fan-out topology trap -- accidentally giving every Orchestrator replica the same receive-endpoint queue name so messages load-balance to one replica instead of broadcasting; this bug is entirely invisible at 1 replica and must be tested with TWO in-process bus instances this milestone -- and (2) ack semantics -- a catch(Exception) at the consumer boundary silently swallows crashes and loses messages forever. The correlation design deliberately keeps two worlds separate: HTTP X-Correlation-Id (string, lives in Redis L2 root) and the future bus-world Guid CorrelationId (minted by Quartz, future milestone); they are linked only via the shared CorrelationId MEL log-scope key whose PascalCase casing is load-bearing for OTel IncludeScopes.

---

## Key Findings

### Recommended Stack

MassTransit 8.5.5 + MassTransit.RabbitMQ 8.5.5 are the only new NuGet additions in Directory.Packages.props. Both must be **pinned with a blocking comment** in CPM (mirroring the Npgsql 8.0.9 cautionary comment) because NuGet now serves v9.x as the latest stable and v9 is commercial. No OpenTelemetry.Instrumentation.MassTransit package exists or is needed -- the standalone package is deprecated (contrib issue #778) because MassTransit v8.0.0 added built-in OTel. The MassTransit OTel meter name is the string InstrumentationOptions.MeterName (the string literal is 'MassTransit'); add it via .AddMeter(InstrumentationOptions.MeterName) in AddBaseConsoleObservability. The in-process test harness (ITestHarness, AddMassTransitTestHarness) ships inside the core MassTransit package -- there is no separate MassTransit.Testing NuGet to add. The RabbitMQ Docker image is pinned rabbitmq:4.1.8-management-alpine with rabbitmq-diagnostics -q ping as the compose healthcheck.

**Core technologies:**
- MassTransit 8.5.5: bus abstractions, consumer registration, consume/send/publish filter pipeline, in-process test harness -- last Apache-2.0 stable; v9+ is commercial
- MassTransit.RabbitMQ 8.5.5: RabbitMQ transport, UsingRabbitMq, instance-unique queue topology for fan-out; transitively pulls RabbitMQ.Client 7.x
- rabbitmq:4.1.8-management-alpine: broker in Docker Compose; management UI on :15672 for dev inspection; -alpine keeps image ~70 MB
- InstrumentationOptions.MeterName ('MassTransit'): native OTel meter -- metrics-only, no traces, no extra package
- Microsoft.AspNetCore.App FrameworkReference: gives Kestrel + MapHealthChecks to the console host at zero cost (shared framework already used by the sibling API)
- AspNetCore.HealthChecks.UI.Client 9.0.0: already pinned; reuse the JSON health body writer identically across API and console

### Expected Features

The milestone has a tight, well-defined scope with no ambiguity about what is P1 vs deferred.

**Must have (table stakes) -- all P1:**
- Bus skeleton in BaseConsole.Core (AddBaseConsoleMessaging) + WebApi joins the bus -- nothing moves without a configured bus on both ends
- Messaging.Contracts assembly: StartOrchestration{WorkflowIds[]}, StopOrchestration{WorkflowIds[]}, ICorrelated vocabulary, L2 root shape extracted from BaseApi.Service
- WebApi Publish<StartOrchestration> / Publish<StopOrchestration> -- the publisher half of the fan-out
- Per-replica fan-out receive endpoint: instance-unique + temporary/auto-delete queue per replica, bound via InstanceId; Publish from WebApi fans out to all bound queues
- Orchestrator consumers: read L2 root per WorkflowId -> extract stored X-CorrelationId string -> open 'CorrelationId' MEL log scope -> log to scheduler-job-start seam -> ack-on-success
- Inbound consume filter: correlationId -> AsyncLocal accessor + BeginScope(['CorrelationId'] = corrId) -- literal PascalCase key matching CorrelationIdMiddleware, surfaced by OTel IncludeScopes=true
- Outbound send/publish filter: ambient AsyncLocal -> stamp ICorrelated fields + MT envelope header; exercised this milestone via a synthetic harness downstream send (no real downstream yet)
- MassTransit OTel: AddMeter(InstrumentationOptions.MeterName) + MEL-bridge logs + AddRuntimeInstrumentation(); no .WithTracing, no AddAspNetCoreInstrumentation()
- Embedded health probes: /health/live|ready|startup on a dedicated port via EmbeddedHealthEndpointService; /ready flips when the MassTransit bus has started (MT auto-registers a ready-tagged check); /live never touches broker or Redis
- Ack semantics: business failure -> log + complete (ack); crash -> throw -> bounded UseMessageRetry -> _error queue; never catch(Exception) at the consumer boundary

**Should have (P2, bounded within milestone after happy path is green):**
- Bounded UseMessageRetry with Ignore<TBusinessException> -- prevents redelivery storms on transient infra faults; add once correlation is proven
- ConsumerDefinition<T> classes per consumer -- clean seam for retry/concurrency config; cheap, future-proofs Processor milestone
- ConcurrentMessageLimit / PrefetchCount tuning -- low-volume control queue; sensible defaults likely fine at 1 replica

**Defer (Processor milestone, v3.5.x+):**
- Quartz scheduler + bus Guid CorrelationId minting per trigger (the bus-world correlation source)
- Send to queue:processorId (load-balanced competing consumers) -- requires Processor console
- IRequestClient<GetProcessorBySourceHash> + WebApi responder
- Concrete JobTrigger / ExecutionResult records + live round-trip
- Transactional outbox -- only needed when DB-transaction-coupled publishing appears
- OTel traces across the bus hop -- Phase 11 D-03 locked decision; no traces backend deployed

### Architecture Approach

The architecture mirrors the existing BaseApi.Core / BaseApi.Service seam exactly. Messaging.Contracts is the leaf assembly both hosts reference; it depends only on MassTransit (for IFilter<ConsumeContext/SendContext/PublishContext>) and Microsoft.Extensions.Logging.Abstractions. BaseConsole.Core wraps the Generic Host bootstrap the same way BaseApi.Core wraps WebApplicationBuilder -- same 7-chain AddBaseConsole pattern, same IHostApplicationBuilder signature on AddBaseConsoleObservability so the OTel composition-root pattern lifts verbatim. Orchestrator is a ~7-line Program.cs thin shell passing consumer registrations as a lambda, identical to AddAppFeatures() in BaseApi.Service. AddBaseApiMessaging in BaseApi.Core joins the bus publish-only (no consumers, no receive endpoints), wired as composition call #8. WebApi never references BaseConsole.Core or Orchestrator -- shared code flows only through Messaging.Contracts.

**Major components:**
1. Messaging.Contracts -- wire contracts, ICorrelated vocabulary, both correlation filters, ICorrelationAccessor (AsyncLocal), WorkflowRootProjectionContract (moved from BaseApi.Service)
2. BaseConsole.Core -- Generic-Host bootstrap, console-flavored OTel (logs+metrics, no AspNetCore instrumentation), Redis client (lifted), EmbeddedHealthEndpointService (Kestrel-in-hosted-service), AddBaseConsoleMessaging (bus skeleton + outbound filters)
3. Orchestrator -- thin concrete console: StartOrchestrationConsumer, StopOrchestrationConsumer, instance-unique receive endpoint wiring
4. AddBaseApiMessaging in BaseApi.Core -- publish-only bus join for the web API; outbound correlation filters; hard dependency for Start/Stop path only
5. RabbitMQ compose tier -- rabbitmq:4.1.8-management-alpine with compose healthcheck; new healthy-service dependency for WebApi + Orchestrator Start/Stop paths

### Critical Pitfalls

1. **Fan-out topology trap (shared receive endpoint = load-balancing, not broadcast)** -- configure each Orchestrator replica with an instance-unique, **auto-delete** receive endpoint (via InstanceId + temporary queue); WebApi uses Publish, never Send to a named queue. **Must test with TWO bus instances in a single harness this milestone** -- the bug is completely invisible at 1 replica.

2. **ACK semantics -- catch-all swallows crashes / throw-on-business causes redelivery storm** -- business failure -> log + return (ack); infrastructure fault -> let it throw -> bounded UseMessageRetry -> _error queue. Never catch(Exception) at the consumer boundary.

3. **MassTransit ActivitySource resurrecting the removed traces pipeline** -- any .WithTracing(...) or .AddSource('MassTransit') in BaseConsole.Core re-enables a TracerProvider and floods a Collector with no traces pipeline. Console OTel = metrics-only: .WithMetrics(m => m.AddMeter(InstrumentationOptions.MeterName)). Assert no TracerProvider in the console container.

4. **RabbitMQ-down widening -- broker outage must not kill CRUD readiness or crash the WebApi host** -- bus start failure must be additive; Publish throws at call-time -> Start endpoint returns 503; CRUD is unaffected. For the WebApi /ready, set MinimalFailureStatus = Degraded on the bus health check or re-tag it off ready (open question -- see below).

5. **Correlation log-scope key casing is load-bearing** -- 'CorrelationId' (PascalCase) is the exact key CorrelationIdMiddleware uses and OTel IncludeScopes=true serializes into Elasticsearch. Any drift creates a different ES field and silently breaks the cross-service log join. Make it a shared constant in Messaging.Contracts.

6. **RabbitMQ queues added to the SHA-256 zero-leak gate** -- extend the phase-close gate to a **TRIPLE-SHA**: psql \l + redis-cli --scan + rabbitmqctl list_queues name BEFORE=AFTER. Tests must use auto-delete per-class unique queue prefixes (broker analog of RedisFixture.KeyPrefix).

---

## Implications for Roadmap

Phases continue numbering from v3.3.0 last phase 16; this milestone starts at phase 17.

### Phase 17: Messaging.Contracts + Shared L2 Root Extract

**Rationale:** Leaf dependency -- both WebApi and Orchestrator compile against it; nothing else can start until this exists. The WorkflowRootProjectionContract move out of BaseApi.Service must precede the WebApi writer swap.
**Delivers:** StartOrchestration/StopOrchestration records; ICorrelated vocabulary; ICorrelationAccessor + AsyncLocalCorrelationAccessor; InboundCorrelationConsumeFilter; OutboundCorrelationSendFilter + OutboundCorrelationPublishFilter; WorkflowRootProjectionContract (+ LivenessProjection) moved from BaseApi.Service.
**Addresses:** Messaging.Contracts table-stakes feature; shared L2 root shape as single source of truth.
**Avoids:** Anti-pattern of WebApi referencing console host code to publish; correlation key-casing drift (constant defined here).
**Research flag:** Standard pattern -- no phase research needed.

### Phase 18: BaseConsole.Core Library

**Rationale:** Console library must exist before the Orchestrator can inherit it; OTel + health probes must be validated before the bus is added on top.
**Delivers:** AddBaseConsole DI chain; AddBaseConsoleObservability (MEL bridge + runtime + MassTransit meter, no AspNetCore instrumentation, no .WithTracing); Redis client lifted; EmbeddedHealthEndpointService (Kestrel-in-hosted-service) + three-probe tag discipline; duplicate IStartupGate/StartupHealthCheck; AddBaseConsoleMessaging (bus skeleton + outbound filters wired bus-wide).
**Uses:** Microsoft.AspNetCore.App FrameworkReference; MassTransit + MassTransit.RabbitMQ 8.5.5; InstrumentationOptions.MeterName; AspNetCore.HealthChecks.UI.Client 9.0.0.
**Implements:** BaseConsole.Core component; EmbeddedHealthEndpointService pattern; MT auto ready check.
**Avoids:** Pitfall 3 (traces resurrection); Pitfall 5 (liveness touching broker); MapHealthChecks on Generic Host; hand-rolled bus-started latch.
**Research flag:** No phase research needed -- patterns verified HIGH in all four research files.

### Phase 19: Orchestrator Console + WebApi Bus Wiring (parallel streams)

**Rationale:** Both depend only on Messaging.Contracts + BaseConsole.Core (for the Orchestrator) and have no dependency on each other, so they run as parallel implementation streams.
**Delivers (stream A -- Orchestrator):** StartOrchestrationConsumer + StopOrchestrationConsumer (read L2 per WorkflowId -> extract stored X-CorrelationId -> 'CorrelationId' MEL log scope -> log to scheduler-job-start seam -> ack-on-success); AddAppConsumers + instance-unique auto-delete receive endpoint wiring; Program.cs (~7 lines).
**Delivers (stream B -- WebApi bus wiring):** AddBaseApiMessaging in BaseApi.Core (publish-only, call #8); OrchestrationController Start/Stop -> IPublishEndpoint.Publish; RedisProjectionWriter using-swap to WorkflowRootProjectionContract; RabbitMQ compose tier + appsettings for both hosts.
**Addresses:** All P1 table-stakes features; fan-out topology; ack semantics.
**Avoids:** Pitfall 1 (fan-out topology trap); Pitfall 2 (ack semantics); Pitfall 4 (RabbitMQ-down widening); competing-consumer queue for fan-out.
**Research flag:** InstanceId fan-out wiring variant (MEDIUM confidence) -- confirm exact API in phase plan before implementation.

### Phase 20: Correlation Propagation Proof + Synthetic Harness Test

**Rationale:** The headline milestone deliverable -- 'CorrelationId propagation proven end-to-end (HTTP -> Redis L2 -> fan-out message -> orchestrator correlated log in Elasticsearch)' -- is only provable with the outbound filter exercised and the ES query confirmed. This phase closes the milestone.
**Delivers:** Synthetic outbound downstream send via AddMassTransitTestHarness; two-bus-instance fan-out broadcast test (both consumers receive one Publish); E2E assertion that orchestrator ES log carries the same CorrelationId key + value as the HTTP request; no TracerProvider in console container assertion; rabbitmqctl list_queues SHA-256 BEFORE=AFTER extended gate; 3-consecutive-GREEN closeout.
**Uses:** AddMassTransitTestHarness (ships in core MassTransit package); real RabbitMQ via Docker Compose for the leak gate; per-class unique queue prefix discipline.
**Avoids:** Pitfall 6 (correlation lost / outbound filter omitted); Pitfall 7 (leaked queues breaking SHA gate).
**Research flag:** No phase research needed -- test harness API confirmed HIGH; ES correlation query mirrors existing SchemasLogsE2ETests.

### Phase Ordering Rationale

- Messaging.Contracts is the strict leaf dependency -- both hosts depend on it; it must compile before either can be written.
- BaseConsole.Core depends on Messaging.Contracts (filters are defined there) and must precede the Orchestrator.
- Orchestrator and WebApi bus wiring have no mutual dependency -- they parallelize naturally on the same phase boundary.
- The correlation proof phase is last because it is the integration validation across all three prior phases.
- The TRIPLE-SHA gate extension is in Phase 20 (not deferred) -- deferring it leaves the phase-close gate incomplete for the new resource class.

### Research Flags

Phases needing a doc check during planning:
- **Phase 19:** InstanceId fan-out wiring variant -- MEDIUM confidence on exact API surface. Three approaches exist (per-endpoint .Endpoint(e => e.InstanceId), a custom IEndpointNameFormatter, and ConnectReceiveEndpoint(new TemporaryEndpointDefinition())); phase plan should confirm which composes cleanest with ConfigureEndpoints before any code is written.

Phases with fully verified patterns (skip /gsd-research-phase):
- **Phase 17:** POCO contracts + filter interfaces -- no MassTransit-specific wiring.
- **Phase 18:** AddBaseConsole mirrors AddBaseApi verbatim; OTel console flavor is two edits from the existing method; embedded Kestrel pattern confirmed HIGH.
- **Phase 20:** AddMassTransitTestHarness API confirmed HIGH; ES correlation query mirrors existing SchemasLogsE2ETests.

### Open Questions -- Requirements Must Resolve

**(a) Does RabbitMQ-down flip WebApi /health/ready?**
MassTransit auto-registers a ready-tagged health check when AddMassTransit is called. If left defaulted, a broker outage flips WebApi /health/ready to 503 even though all CRUD operations continue to succeed -- violating the existing Redis soft-dependency contract. **Recommendation:** set MinimalFailureStatus = Degraded on the WebApi bus health check (or re-tag it off ready), keeping CRUD readiness green on broker outage and surfacing broker status as Degraded in the readiness body. The Orchestrator is different -- its entire job depends on the bus, so ready should go Unhealthy if the broker drops.

**(b) IStartupGate / StartupHealthCheck -- duplicate into BaseConsole.Core or lift to a shared Hosting.Abstractions assembly?**
A BaseConsole.Core -> BaseApi.Core dependency would drag EF Core and ASP.NET MVC transitively into a console host. **Recommendation:** duplicate the ~40 LOC this milestone; extract to Hosting.Abstractions if/when a third host type appears.

**(c) Extend the phase-close leak gate to a TRIPLE-SHA?**
The current gate is dual-SHA (psql \l + redis-cli --scan). **Recommendation:** extend to a TRIPLE-SHA -- add rabbitmqctl list_queues name (and optionally list_exchanges) to the BEFORE/AFTER snapshot. This must be in-scope for the milestone; deferring it leaves the gate incomplete and the leak discipline unproven for the new resource class.

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | MassTransit 8.5.5 + v9 commercial status verified on NuGet; native OTel confirmed via official docs + contrib issue #778; RabbitMQ image verified on Docker Hub; AddMassTransitTestHarness in core package confirmed |
| Features | HIGH | All four mechanisms (fan-out topology, ack/redelivery, correlation, request/response) verified against official MassTransit docs via Context7; scope discipline clear from PROJECT.md locked decisions |
| Architecture | HIGH | Existing codebase read directly for all mirror claims; MassTransit health-check tags/semantics, filter registration, and embedded Kestrel pattern confirmed HIGH |
| Pitfalls | HIGH | Fan-out trap + ack semantics confirmed across multiple MassTransit discussion threads + official docs; traces resurrection confirmed from PROJECT.md D-03 + contrib issue; tag discipline from existing StartupHealthCheck |

**Overall confidence:** HIGH

### Gaps to Address

- **InstanceId fan-out wiring variant (MEDIUM):** three valid approaches exist; Phase 19 plan should confirm the correct API before implementation. All three achieve the required fan-out; the uncertainty is only ergonomic.
- **WebApi bus health check tag behavior under custom ConfigureHealthCheckOptions:** custom tags replace defaults -- must re-add ready + masstransit manually if any custom configuration is applied. Recommendation: leave defaults alone and address only via MinimalFailureStatus = Degraded.
- **Compose depends_on scope:** WebApi and Orchestrator should depends_on rabbitmq condition service_healthy for the Start/Stop path; affects only compose-local dev boot ordering.

---

## Sources

### Primary (HIGH confidence)

- MassTransit 8.5.5 on NuGet (Apache-2.0, net8.0, 2025-10-22) -- version pinning, harness in core package
- MassTransit v9.1.1 on NuGet (commercial product statement) -- v9 license trap
- MassTransit official observability docs (masstransit.massient.com) -- ActivitySource = DiagnosticHeaders.DefaultListenerName, Meter = InstrumentationOptions.MeterName; fan-out InstanceId quote
- opentelemetry-dotnet-contrib issue #778 -- deprecation of standalone OpenTelemetry.Instrumentation.MassTransit; built-in v8 support confirmed
- Docker Hub rabbitmq official image -- 4.1.8-management-alpine tag, rabbitmq-diagnostics -q ping probe
- MassTransit concepts/transports, concepts/outbox, concepts/messages, concepts/exceptions, configuration/topology, configuration/middleware/retry (Context7, 2026-05-30) -- Publish/Send/ack/redelivery/correlation semantics
- Existing codebase (read directly): BaseApiServiceCollectionExtensions.cs, ObservabilityServiceCollectionExtensions.cs, CorrelationIdMiddleware.cs, StartupHealthCheck.cs, HealthServiceCollectionExtensions.cs, WorkflowRootProjection.cs, RedisProjectionWriter.cs
- .planning/PROJECT.md -- locked decisions (D-03 no traces, fan-out topology, correlation reconciliation, Redis soft-dep, 3-GREEN gate)

### Secondary (MEDIUM confidence)

- Milan Jovanovic -- MassTransit going commercial analysis; confirms v8-to-v9 Apache-2.0-to-commercial boundary
- OneUptime blog (2026-02) -- MassTransit OTel metrics-only wiring example
- MassTransit discussion threads (groups.google.com/g/masstransit-discuss, GitHub discussions) -- fan-out vs competing-consumer topology behavior at multiple replicas
- MassTransit Send vs Publish semantics (maldworth.com/2015/10/27) -- exchange vs queue routing

### Tertiary (LOW confidence)

- WorkerService + HealthCheck gist (CarlosLanderas) -- embedded Kestrel pattern reference; superseded by HIGH-confidence official Microsoft docs for the core approach

---
*Research completed: 2026-05-30*
*Ready for roadmap: yes*
