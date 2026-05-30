# Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout - Research

**Researched:** 2026-05-30
**Domain:** MassTransit 8.5.5 test harnesses (in-memory + real RabbitMQ), Elasticsearch log-readback E2E, multi-SHA close-gate scripting (.NET 8 / RabbitMQ / EF / Redis / ES stack)
**Confidence:** HIGH (every mechanic grounded in actual repo code read this session; one CLI-fallback Context7 confirmation of the WebApplicationFactory harness pattern)

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01 .. D-13 — DO NOT relitigate; fill mechanics beneath)
- **D-01:** In-memory `AddMassTransitTestHarness` is the DEFAULT test transport. Swap the `UsingRabbitMq` registration on the integration host so `/start|/stop` publish succeeds in-process → the 16 Phase-19 orchestration HTTP facts go GREEN (204), published contract assertable via `ITestHarness.Published`. Kills the ~1m40s publish-timeout hangs.
- **D-02:** ONLY TEST-RMQ-02 (ES correlation proof) uses the real Docker stack — the sole genuinely cross-process assertion.
- **D-03:** TEST-RMQ-03 (broker-down) uses the REAL RabbitMQ transport pointed at a dead/unreachable endpoint (`HealthDeadRabbitFixture` mirroring `HealthDeadRedisFixture`), NOT in-memory.
- **D-04:** TEST-RMQ-01 (fan-out) + CORR-03 (outbound-filter synthetic send) stay in-memory two-bus harness.
- **D-05:** Reuse the existing ES-query E2E pattern (`OrchestrationLogsE2ETests` + `ElasticsearchTestClient`) — poll ES by `CorrelationId`, tolerate indexing latency. Invent no new ES plumbing.
- **D-06:** Hybrid topology — real Orchestrator container consumes from real RabbitMQ → otel → ES; the HTTP Start is driven by an in-process WebApi (`WebApplicationFactory`) that points at REAL host endpoints (`localhost:5673` broker, host Redis, host ES) as the single exception to D-01's in-memory default.
- **D-07:** Full-chain assertion. Add a structured publish-side log on `OrchestrationService` (e.g. `"Published StartOrchestration CorrelationId={guid}"`). TEST-RMQ-02 reads BOTH the webapi-published doc and the orchestrator seam doc from ES and asserts `webapi.published.CorrelationId == orchestrator.seam.CorrelationId` AND both `!=` the HTTP `X-Correlation-Id`.
- **D-08:** Seed the workflow's L2 root in real Redis before the proof request (normal start path or direct projection write) so the Orchestrator reaches the scheduler-job-start success seam.
- **D-09:** RabbitMQ snapshot = `docker exec sk-rabbitmq rabbitmqctl list_queues name`, sorted → SHA-256, BEFORE=AFTER alongside `psql \l` + `redis-cli --scan`. Net-zero leak: temporary/auto-delete per-class queues + the Orchestrator's own `orchestrator-{InstanceId}` temporary queue (present in both BEFORE and AFTER when stack is up).
- **D-10:** Same 3-consecutive-GREEN cadence; TEST-RMQ-02 requires the full Docker stack up during the gate (existing stack-up gate provides it).
- **D-11:** Copy `scripts/phase-17-close.ps1` + bash counterpart `scripts/phase-16-close.sh` → `phase-20-close.{ps1,sh}`, add the rabbitmq SHA, AND fix the three deferred smells (`-1` fact-count fallback, `compose ps --format json` assumption, PS1/SH service-list divergence).
- **D-12:** Fold in the `baseapi-service` wget healthcheck fix — install `wget` in the root `Dockerfile` runtime stage BEFORE `USER app` (mirrors Phase 19 orchestrator fix `e4fcf67`). `aspnet:8.0-bookworm-slim` ships neither wget nor curl.
- **D-13:** Fold in the IN-02 fix — `StopOrchestrationConsumer.cs` logs the Start seam string `"Scheduler job start"` for a Stop; correct the Stop log message AND update the carried-over assertion in `StartStopConsumerAckTests.cs` (~line 217).

### Claude's Discretion
- Exact in-memory-harness wiring inside the factory (how `UsingRabbitMq` is replaced/removed), fixture class names, factory-variant vs sibling.
- `HealthDeadRabbitFixture` placement/shape (mirror `HealthDeadRedisFixture`).
- Exact ES query shape / poll timeout for the two-doc correlation assertion.
- Exact structured-log message text + log level for the WebApi publish-side log (D-07).
- Two-bus in-memory harness construction (TEST-RMQ-01) + CORR-03 synthetic outbound send shape.
- Close-gate script internals beyond triple-SHA + smell fixes; placement of `phase-20-close.*`.
- Test placement (`tests/BaseApi.Tests/Orchestrator/` per Phase 19 discretion vs a dedicated project).

### Deferred Ideas (OUT OF SCOPE — ignore)
- WR-02 Start dual-write outbox; WR-01 published-set-vs-completed-set; stop writing vestigial `WorkflowRootProjection.correlationId`; IN-01/IN-03/IN-04/IN-05 (informational).
- All Processor-milestone items: Quartz, `IExecutionCorrelated`, `Send` to `queue:processorId`, request/response, `JobTrigger`/`ExecutionResult`.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CORR-03 | Outbound filter exercised by a synthetic test-harness downstream send asserting the correlationId is stamped (no real downstream consumer). | The outbound filters stamp the **envelope** `context.CorrelationId` from the **ambient `ICorrelationAccessor`** (`OutboundCorrelationPublishFilter.cs:18-19`, `OutboundCorrelationSendFilter.cs`). Synthetic test = wire `ICorrelationAccessor` + the outbound filter into an in-memory harness, set the ambient id, publish an `ICorrelated` message, assert `harness.Published.Select<T>().Single().Context.CorrelationId == set id`. (Code Example 4) |
| CORR-04 | Per-stage correlation proven across stage boundaries (HTTP id ≠ body-minted Guid; body Guid surfaces in ES). | Same chain TEST-RMQ-02 proves. The publish mints `NewId.NextGuid()` on the body (`OrchestrationService.cs:163-165`); inbound filter reads body (`InboundCorrelationConsumeFilter.cs:35-37`); seam log emits under `"CorrelationId"` scope → ES `attributes.CorrelationId`. |
| TEST-RMQ-01 | Two in-process bus instances (two `InstanceId`s) both receive ONE published Start (broadcast not load-balanced). | Fan-out mechanism proven in `Orchestrator/Program.cs:26-34`: `e.InstanceId = instanceId; e.Temporary = true;` per consumer → one `orchestrator-{InstanceId}` queue each. Two distinct InstanceIds = two queues bound to the same message-type exchange = broadcast. (Pattern 2, Pitfall 1) |
| TEST-RMQ-02 | Real HTTP Start → real RabbitMQ → Orchestrator container → ES; assert orchestrator log carries the body-minted Guid. | Hybrid: `WebApplicationFactory` pointed at host endpoints (`localhost:5673` / host Redis / host ES via env-var-in-ctor override, `HealthEndpointsTests.cs:251-306` pattern) + ES poll (`ElasticsearchTestClient.PollEsForLog`, 30s). (Pattern 3, Code Example 3) |
| TEST-RMQ-03 | Broker-down: WebApi CRUD `/health/ready` + `/health/live` BOTH 200 with RabbitMQ unreachable. | `HealthDeadRabbitFixture` mirrors `HealthDeadRedisFixture` (`HealthEndpointsTests.cs:327-378`): env-var `RabbitMq__Host` → dead host. Bus check capped Degraded (`MessagingServiceCollectionExtensions.cs:49-54`, MinimalFailureStatus=Degraded) keeps `ready` at 200. (Code Example 2) |
| TEST-RMQ-04 | Test receive endpoints temporary/auto-delete, per-test-class-prefixed; NO global queue purge in teardown. | In-memory harness auto-configures temporary endpoints; the real-broker fan-out endpoints set `e.Temporary = true`. Per-class prefix the InstanceId (e.g. `t01-{guid:N}`). The FLUSHDB-ban analog. (Pattern 2, Pitfall 4) |
| TEST-RMQ-05 | Close gate extended to triple-SHA (`psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues name`) BEFORE=AFTER + 3× GREEN. | Extend `phase-17-close.ps1` / `phase-16-close.sh` (read in full). Add rabbitmq arm; fix 3 smells. (Pattern 4, Code Example 5) |
</phase_requirements>

## Summary

This is a **proof + closeout** phase — zero new runtime capability. Every requirement is a validation: a test that observes an already-built behavior, or the milestone close gate that asserts no-leak + GREEN. The CONTEXT.md locked all 13 decisions; the mechanics gaps are entirely in **how** to wire four MassTransit harness shapes (in-memory swap, two-bus fan-out, synthetic outbound, real-broker dead-host), **how** the single cross-process ES proof points an in-process WebApi at host endpoints, **where** to add one publish-side log line, and **how** to extend the dual-SHA close gate to triple-SHA while de-smelling it.

The repo already contains every pattern needed as precedent: `OrchestrationServicePublishTests.cs` (in-memory harness driving the real `OrchestrationService`), `StartStopConsumerAckTests.cs` (in-memory `AddMassTransitTestHarness(x => { x.AddConsumer; x.UsingInMemory(...) })`), `HealthEndpointsTests.cs` (private-sealed `Phase8WebAppFactory` dead-port fixtures via env-var-in-ctor), `OrchestrationLogsE2ETests.cs` + `ElasticsearchTestClient.cs` (ES poll-and-assert by correlationId), and `phase-17-close.ps1` / `phase-16-close.sh` (dual-SHA BEFORE=AFTER + 3× GREEN). Nothing is invented; everything is mirrored.

**Primary recommendation:** Build all five tests in `tests/BaseApi.Tests/Orchestrator/` (Phase 19 discretion already placed the consumer tests there, anticipating Phase 20's heavy tests). Use a NEW sibling `HarnessWebAppFactory : WebAppFactory` for the D-01 in-memory swap (it must `RemoveMassTransit()` then `AddMassTransitTestHarness()` in `ConfigureTestServices` because `AddMassTransit` is already called by `Program.cs:8`). Mark TEST-RMQ-02 + the close gate as the only stack-dependent items; everything else is hermetic.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| In-memory publish/consume assertion (D-01, TEST-RMQ-01, CORR-03) | Test harness (in-process MassTransit in-memory transport) | — | MassTransit's `ITestHarness` owns published/consumed inspection; no broker, no I/O. |
| Real cross-process correlation proof (TEST-RMQ-02) | Real RabbitMQ + Orchestrator container + ES backend | In-process WebApi (`WebApplicationFactory`) as publisher | The broker→consumer→otel→ES path is the genuine subject; the WebApi is just the request driver, kept in-process (lighter than containerizing). |
| Broker-down health posture (TEST-RMQ-03) | WebApi health-check tier (MassTransit auto-registered bus check, MinimalFailureStatus=Degraded) | Real RabbitMQ transport pointed at dead host | The subject is the WebApi's soft-dependency posture; needs the real transport to attempt+fail a connection. |
| Publish-side correlation log (D-07) | API/Backend (`OrchestrationService`, WebApi process) | otel-collector → ES | The log emits at the publish seam; it surfaces in ES via the existing OTLP pipeline. |
| Close-gate leak/GREEN proof (TEST-RMQ-05) | CI/script tier (`scripts/phase-20-close.{ps1,sh}`) | Docker stack (postgres + redis + rabbitmq via `docker exec`/`compose exec`) | Snapshot diffing is a host-side scripting concern that reaches into running containers. |

## Standard Stack

### Core (all already pinned — no new packages)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, `AddMassTransitTestHarness`, `ITestHarness`, in-memory transport, `RemoveMassTransit` | `Directory.Packages.props:131` (last Apache-2.0 line; do NOT bump to 9.x per the pin comment). |
| MassTransit.RabbitMQ | 8.5.5 | `UsingRabbitMq` for the real-broker TEST-RMQ-02/03 paths | `Directory.Packages.props:132`. |
| Microsoft.AspNetCore.Mvc.Testing | 8.0.27 | `WebApplicationFactory<Program>` host | `Directory.Packages.props:111`. |
| xunit.v3 / xunit.v3.assert | 3.2.2 | Test framework — note `TestContext.Current.CancellationToken` idiom | `Directory.Packages.props:115-116`. |
| NSubstitute | 5.3.0 | Stub `IConnectionMultiplexer` / `IDatabase` for in-memory harness Redis seams | `Directory.Packages.props:114`. |
| StackExchange.Redis | 2.13.1 | Real Redis L2 seed (D-08) + dead-port fixture | `Directory.Packages.props:125`. |

`MassTransit.Testing` namespace (`ITestHarness`, `IConsumerTestHarness`, `harness.Published`, `harness.Consumed`) ships inside the `MassTransit` package — no separate PackageReference. Confirmed in use at `OrchestrationServicePublishTests.cs:13` and `StartStopConsumerAckTests.cs:4`.

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `RemoveMassTransit()` + re-add harness in test factory | Manual `services.RemoveAll<...>()` of MT descriptors | `RemoveMassTransit()` is the MassTransit-supplied bulk removal for exactly this swap; manual removal is brittle (must enumerate `IBusControl`, `IBus`, `IHostedService`, all hosted bus services). Use `RemoveMassTransit()`; document the manual fallback only if 8.5.5 lacks it (it ships in 8.x). |
| Two in-memory buses (two `IServiceProvider`s) for TEST-RMQ-01 | One harness with two endpoints / one bus | A single in-memory bus with two `InstanceId`-suffixed endpoints binding the same exchange is the *faithful* analog of two replicas (Pattern 2). Two separate providers is also valid but heavier. Prefer the single-harness/two-endpoint shape — it exercises the exact `ConfigureEndpoints` fan-out path. |

**Installation:** None. All packages pinned in Phase 17.

**Version verification:** MassTransit 8.5.5 confirmed pinned (`Directory.Packages.props:131-132`); the WebApplicationFactory test-harness API (`AddMassTransitTestHarness` in `ConfigureServices`, `application.Services.GetTestHarness()`, `harness.Published.Any<T>(...)`) confirmed against official docs `masstransit.massient.com/guides/unit-testing/web-application-factory` [CITED]. No `npm`/registry check applies (.NET CPM).

## Architecture Patterns

### System Architecture Diagram

```
TEST-RMQ-02 (real cross-process — the ONE true proof):

  [xUnit test process]
        │  POST /api/v1/orchestration/start  (header X-Correlation-Id = "http-stage-id")
        ▼
  ┌─────────────────────────────┐
  │ in-process WebApi            │   env-var override → host endpoints:
  │ (WebApplicationFactory)      │     ConnectionStrings__Redis = localhost:<hostRedis>
  │  OrchestrationService        │     RabbitMq__Host           = localhost:5673
  │   • L2 write (real Redis)    │     OTEL exporter            = host otel-collector
  │   • mint NewId on BODY ──────┼──► publish StartOrchestration { CorrelationId = G1 }
  │   • LOG "Published … =G1"(D7)│         │
  └─────────────────────────────┘         │ AMQP (host 5673 → container 5672)
                                           ▼
                                  ┌──────────────────┐
                                  │ real RabbitMQ     │  message-type exchange
                                  │ (sk-rabbitmq)     │  → orchestrator-{InstanceId} queue
                                  └──────────────────┘
                                           │ consume
                                           ▼
                                  ┌──────────────────────────────────┐
                                  │ Orchestrator container            │
                                  │  inbound filter opens "CorrelationId"=G1 scope (body)
                                  │  read L2 root (must EXIST — D-08 seed)
                                  │  LOG "Scheduler job start (seam)" under scope G1
                                  └──────────────────────────────────┘
                                           │ OTLP
                                           ▼
                          otel-collector ──► Elasticsearch (logs-generic.otel-default)

  [test polls ES]  term attributes.CorrelationId = G1
     ├─ webapi.published doc  "Published … CorrelationId=G1"
     └─ orchestrator.seam doc "Scheduler job start (seam) …"
     ASSERT: both docs' CorrelationId == G1  AND  G1 != "http-stage-id"


TEST-RMQ-01 / CORR-03 / D-01 (hermetic — in-memory, no broker, no ES):

  [xUnit]──► AddMassTransitTestHarness (in-memory transport) ──► ITestHarness.Published / .Consumed
```

### Recommended Project Structure
```
tests/BaseApi.Tests/
├── Orchestrator/
│   ├── StartStopConsumerAckTests.cs        # EXISTS — edit line ~217 assertion (D-13)
│   ├── FanOutBroadcastTests.cs             # NEW — TEST-RMQ-01 (two-endpoint in-memory)
│   ├── OutboundFilterSyntheticTests.cs     # NEW — CORR-03 (ambient accessor + outbound filter)
│   └── CorrelationPropagationE2ETests.cs   # NEW — TEST-RMQ-02 / CORR-04 (real stack + ES)
├── Composition/
│   ├── Phase8WebAppFactory.cs              # EXISTS — base for fixtures
│   └── HarnessWebAppFactory.cs             # NEW — D-01 in-memory MT swap (sibling of WebAppFactory)
├── Observability/
│   └── HealthEndpointsTests.cs             # EXISTS — add HealthDeadRabbitFixture + 2 facts (TEST-RMQ-03)
└── Observability/Helpers/
    └── ElasticsearchTestClient.cs          # EXISTS — reuse verbatim (D-05)
src/
├── BaseApi.Service/Features/Orchestration/OrchestrationService.cs  # add publish-side log (D-07)
├── Orchestrator/Consumers/StopOrchestrationConsumer.cs             # fix Stop seam log (D-13)
└── ../Dockerfile (root)                                            # install wget (D-12)
scripts/
├── phase-20-close.ps1   # NEW — copy phase-17-close.ps1 + triple-SHA + smell fixes
└── phase-20-close.sh    # NEW — copy phase-16-close.sh + triple-SHA + smell fixes
```

### Pattern 1: In-memory harness swap on the integration host (D-01)
**What:** Replace the real `UsingRabbitMq` registration with the in-memory test harness inside a `WebApplicationFactory` subclass so `/start` and `/stop` publish in-process and return 204.
**When to use:** The default for the 16 Phase-19 orchestration HTTP facts and any future HTTP test that publishes.
**Critical mechanic:** `Program.cs:8` already calls `AddBaseApiMessaging` → `AddMassTransit`. Calling `AddMassTransitTestHarness` again throws `ConfigurationException("AddMassTransit() was already called")`. You MUST remove the prior registration first. `ConfigureTestServices` runs AFTER the app's own `ConfigureServices` (verified: `WebAppFactory.cs:37-52` uses `ConfigureTestServices`), so it is the correct seam.
```csharp
// Source: tests/BaseApi.Tests/Middleware/WebAppFactory.cs:37 (ConfigureTestServices seam)
//       + masstransit.massient.com/guides/unit-testing/web-application-factory [CITED]
public sealed class HarnessWebAppFactory : Phase8WebAppFactory   // inherits Postgres+Redis fixtures
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);                 // keeps the Postgres/Redis AddInMemoryCollection wiring
        builder.ConfigureTestServices(services =>
        {
            // ConfigureTestServices runs AFTER Program.cs's AddBaseApiMessaging → AddMassTransit.
            // Remove the real bus registration, then add the in-memory harness in its place.
            services.RemoveMassTransit();               // MassTransit 8.x bulk removal of all MT descriptors
            services.AddMassTransitTestHarness();       // in-memory transport; registers IPublishEndpoint + ITestHarness
        });
    }
}
```
**Note (ASSUMED, verify in Wave 0):** If `RemoveMassTransit()` is not exposed in 8.5.5's public surface, fall back to `services.RemoveAll(typeof(IBusControl)); RemoveAll(typeof(IBus)); ...` plus removing the MT `IHostedService`. The harness's `IPublishEndpoint` then satisfies `OrchestrationService`'s constructor injection.

### Pattern 2: Two-endpoint in-memory fan-out (TEST-RMQ-01) — THE #1 topology trap
**What:** Prove a single published `StartOrchestration` is delivered to BOTH of two distinct `InstanceId`-suffixed endpoints (broadcast), not split between them (load-balance).
**When to use:** TEST-RMQ-01 only.
**Mechanic:** The fan-out behavior is created by binding two DIFFERENT queues to the same message-type exchange. In `Orchestrator/Program.cs:26-34` this is `e.InstanceId = instanceId; e.Temporary = true;`. To reproduce it in one in-memory harness, register the consumer twice on two endpoints with two InstanceIds — OR run two separate in-memory providers. The faithful single-harness shape:
```csharp
// Source: src/Orchestrator/Program.cs:26-34 (the production fan-out wiring this mirrors)
//       + tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs:97-101 (harness shape)
var counterA = new ConsumeCounter();   // distinct singleton per replica's consumer instance
var counterB = new ConsumeCounter();

await using var provider = new ServiceCollection()
    .AddSingleton(AbsentL2(out _))                 // or PresentL2 — see StartStopConsumerAckTests stubs
    .AddSingleton(new OrchestratorRedisOptions("skp:"))
    .AddLogging()
    .AddMassTransitTestHarness(x =>
    {
        // Two endpoints, two InstanceIds → two queues bound to the StartOrchestration exchange.
        x.AddConsumer<CountingStartConsumerA>()
            .Endpoint(e => { e.InstanceId = "rep-a"; e.Temporary = true; });
        x.AddConsumer<CountingStartConsumerB>()
            .Endpoint(e => { e.InstanceId = "rep-b"; e.Temporary = true; });
        x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
    })
    .BuildServiceProvider(true);
var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
try
{
    await harness.Bus.Publish(new StartOrchestration([Guid.NewGuid()]) { CorrelationId = NewId.NextGuid() }, ct);
    // BROADCAST: BOTH consumer harnesses observed the same single message.
    Assert.True(await harness.GetConsumerHarness<CountingStartConsumerA>().Consumed.Any<StartOrchestration>(ct));
    Assert.True(await harness.GetConsumerHarness<CountingStartConsumerB>().Consumed.Any<StartOrchestration>(ct));
    // Anti-load-balance: harness.Consumed total count == 2 (one per endpoint), not 1.
    Assert.Equal(2, await harness.Consumed.Count<StartOrchestration>(ct));   // see Pitfall 1 for the count API
}
finally { await harness.Stop(ct); }
```
**Why two consumer TYPES (A/B):** MassTransit keys endpoints/consumer harnesses by consumer type. Two endpoints hosting the *same* consumer type collapses in the harness's consumer-harness lookup. Two thin subclasses (or a generic counting consumer registered twice) gives two independently-assertable `GetConsumerHarness<T>()` slots. (ASSUMED — confirm the cleanest 8.5.5 idiom in Wave 0; an alternative is two separate `IServiceProvider` harnesses each with one consumer + a shared exchange, which is unambiguous.)

### Pattern 3: Hybrid real-stack ES proof (TEST-RMQ-02) — point in-process WebApi at host endpoints
**What:** Drive `POST /start` against an in-process `WebApplicationFactory` configured for the REAL host broker/Redis/ES, let the real Orchestrator container consume, poll ES for both log docs.
**Config-override mechanic:** The proven way to make `Program.cs`'s config reads land on host endpoints is the env-var-in-ctor pattern documented exhaustively at `HealthEndpointsTests.cs:226-293` (the `ConfigureAppConfiguration`/`AddInMemoryCollection` path is TOO LATE for values captured at registration time). Set env vars in the fixture ctor BEFORE the base `WebApplicationFactory<Program>` ctor runs:
```csharp
// Source: tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:261-293 (env-var-in-ctor pattern)
//       + compose.yaml:161-167 (host ports 5673 AMQP), :219 (host Redis), :39 ES on 9200
private sealed class RealStackWebAppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string,string?> _prior = new();
    public RealStackWebAppFactory()
    {
        Set("RabbitMq__Host", "localhost");                // host AMQP port 5673 — see note
        Set("ConnectionStrings__Redis", "localhost:6380,abortConnect=false");  // host Redis (compose maps 6380:6379)
        Set("ConnectionStrings__Postgres", "Host=localhost;Port=5433;...");    // host Postgres
        // OTEL exporter endpoint: ensure the in-process WebApi exports to the SAME otel-collector → ES.
    }
    private void Set(string k, string v) { _prior[k] = Environment.GetEnvironmentVariable(k); Environment.SetEnvironmentVariable(k, v); }
    // restore in DisposeAsync (mirror HealthDeadPostgresFixture)
}
```
**Port note (verify Wave 0):** RabbitMq host config is `Host` + `Username` + `Password` only (`MessagingServiceCollectionExtensions.cs:43-45`, no explicit port key). The AMQP port for the in-process WebApi to reach the host broker is 5673 (`compose.yaml:166`). MassTransit's `Host(host, ...)` defaults to 5672 — so a bare `RabbitMq__Host=localhost` will hit 5672, NOT 5673. **This is a real gap:** either the in-process WebApi must target the broker on 5673 (requires a host-with-port form, e.g. `rabbitmq://localhost:5673` or a `Port` config key the current `AddBaseApiMessaging` does NOT read), OR the test runs the WebApi against the compose-internal broker. RECOMMENDATION for the planner: confirm whether `AddBaseApiMessaging` needs a `RabbitMq:Port` key added (small, in-scope mechanic) so the in-process publisher can reach `localhost:5673`. Flag as Open Question #1.
**ES poll + two-doc assertion:**
```csharp
// Source: tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs:122-142
//       + Helpers/ElasticsearchTestClient.cs:62 + Helpers/EsIndexNames.cs
var httpCorr = $"{Guid.NewGuid():N}";       // the HTTP-stage id — must NOT equal the body Guid
client.DefaultRequestHeaders.Add("X-Correlation-Id", httpCorr);
// ... seed graph + L2 root (D-08), POST /start (expect 204) ...
using var es = new ElasticsearchTestClient();

// The body-minted Guid is NOT known to the test a priori. Strategy: query ES for the orchestrator
// seam doc by its message text, read back its attributes.CorrelationId (= body Guid G1), then
// query the webapi "Published … CorrelationId=G1" doc, assert equality + G1 != httpCorr.
var seamQuery = $$"""
  { "size": 5, "query": { "bool": { "must": [
      { "match_phrase": { "body": "Scheduler job start" } }
  ] } } }
  """;
var seam = await es.PollEsForLog(seamQuery, timeoutMs: 30_000, ct: ct);
Assert.NotNull(seam);
var bodyGuid = /* extract seam.attributes.CorrelationId */ ;
Assert.NotEqual(httpCorr, bodyGuid);                       // per-stage handoff (not the HTTP id)

var publishedQuery = $$"""
  { "size": 5, "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{bodyGuid}}" } } }
  """;
var published = await es.PollEsForLog(publishedQuery, timeoutMs: 30_000, ct: ct);
Assert.NotNull(published);                                 // both docs carry the SAME body Guid
Assert.Contains("Published", published!.Value.GetRawText());
```
**Isolation:** Per `ElasticsearchTestClient.cs:14-23` and `OrchestrationLogsE2ETests.cs:100`, query on a per-test unique value (here the body Guid discovered from the seam doc, or the per-run httpCorr) — never cumulative counts. The ES data stream is shared suite-wide.

### Pattern 4: Triple-SHA close gate (TEST-RMQ-05)
**What:** Add a third snapshot arm to the existing dual-SHA BEFORE=AFTER gate; assert net-zero queue leak.
**Mechanic:** `phase-17-close.ps1:54` already does `docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive`. The rabbitmq arm mirrors it: `docker exec sk-rabbitmq rabbitmqctl list_queues name`, sort, SHA-256. Both BEFORE (line ~58) and AFTER (line ~136) blocks gain the arm, and a third invariant-assertion block (line ~150 pattern) is added.
```powershell
# Source: scripts/phase-17-close.ps1:54-58 (redis arm shape, mirror for rabbitmq)
# D-09: rabbitmqctl list_queues prints a header line "Listing queues ..." — strip it OR use -q.
$beforeRmq = (docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Sort-Object -CaseSensitive | Out-String).Trim()
$beforeRmqHash = (Get-FileHash -Algorithm SHA256 -InputStream (
    [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRmq)))).Hash.ToLower()
```
```bash
# Source: scripts/phase-16-close.sh:47-49 (redis arm shape, mirror for rabbitmq)
BEFORE_RMQ=$(docker exec sk-rabbitmq rabbitmqctl -q list_queues name | LC_ALL=C sort)
BEFORE_RMQ_HASH=$(printf '%s' "$BEFORE_RMQ" | sha256sum | awk '{print $1}')
```
**Net-zero-leak reasoning (D-09):** Test endpoints are temporary/auto-delete + per-class-prefixed → a clean run leaks zero queues. The Orchestrator's own `orchestrator-{InstanceId}` queue is temporary/auto-delete (`Orchestrator/Program.cs:31,33`) and present in BOTH BEFORE and AFTER while the stack is up (stable, not a leak). So BEFORE==AFTER holds. **Use `rabbitmqctl -q`** (quiet) to suppress the "Listing queues for vhost..." header that varies and would break the SHA.

### Anti-Patterns to Avoid
- **Calling `AddMassTransitTestHarness` without first removing the real bus** → `ConfigurationException` at host build. Always `RemoveMassTransit()` first in `ConfigureTestServices` (Pattern 1).
- **Asserting `harness.Consumed.Count == 1` for fan-out** → that is the load-balance expectation; broadcast to two endpoints yields 2. Asserting 1 silently passes a broken (competing-consumer) topology — the exact trap TEST-RMQ-01 exists to catch (Pitfall 1).
- **`rabbitmqctl list_queues` without `-q`** → header line pollutes the SHA, BEFORE!=AFTER false-fails the gate.
- **Querying `attributes.CorrelationId.keyword`** → zero hits; the x-pack ECS data stream maps strings directly to `keyword` (`EsIndexNames.cs` historical IN-05 note). Query `attributes.CorrelationId` directly.
- **Setting host endpoints via `ConfigureAppConfiguration` for the real-stack proof** → too late for registration-time-captured values (`HealthEndpointsTests.cs:229-249`). Use env-var-in-ctor.
- **Global queue purge / `rabbitmqctl purge_queue` in teardown** → the FLUSHDB-ban analog; banned by TEST-RMQ-04. Rely on temporary/auto-delete.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Published-message inspection | Custom in-memory publish-capture list | `ITestHarness.Published.Any<T>(...)` / `.Select<T>().Single()` | `OrchestrationServicePublishTests.cs:142-148` already proves it; MassTransit owns the timing/await semantics. |
| Removing the real bus from the test host | Manual `RemoveAll` of every MT descriptor | `services.RemoveMassTransit()` | MassTransit-supplied bulk removal; manual enumeration misses hosted services. |
| ES polling with indexing-latency tolerance | New HttpClient + retry loop | `ElasticsearchTestClient.PollEsForLog` | `ElasticsearchTestClient.cs:62` — exponential backoff (200ms→3.2s), 404/empty-hits tolerance, clone-detach already handled (D-05 mandates reuse). |
| Dead-endpoint health fixture | New `WebApplicationFactory` subclass from scratch | `Phase8WebAppFactory` + env-var-in-ctor (mirror `HealthDeadRedisFixture`) | `HealthEndpointsTests.cs:327-378` is the exact template; includes the port-collision pre-flight guard idea. |
| Two-endpoint fan-out wiring | Hand-built exchange/queue bindings | `.Endpoint(e => { e.InstanceId = ...; e.Temporary = true; })` + `ConfigureEndpoints` | `Orchestrator/Program.cs:31,33` — the production wiring; the test must exercise THIS path, not a synthetic one. |
| Snapshot SHA gate | New diffing script | Copy `phase-17-close.ps1` / `phase-16-close.sh` | D-11 mandates copy-and-extend; the BEFORE=AFTER + 3×GREEN scaffold is proven. |

**Key insight:** Phase 20 is almost entirely composition of existing, tested helpers. The only genuinely new code is: one publish-side log line (D-07), one consumer log-string fix (D-13), one Dockerfile RUN line (D-12), the rabbitmq SHA arm (D-09), the three smell fixes (D-11), and the five test bodies that wire the above helpers together.

## Runtime State Inventory

> Phase 20 adds no rename/refactor, but it DOES create real broker state during TEST-RMQ-02 and the close gate. This inventory covers the runtime-state surface the close gate must reason about.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Redis L2 roots seeded for D-08 proof (`skp:` prefix, per `OrchestratorRedisOptions`/`RedisProjectionKeys.Root`). Postgres workflow rows seeded via HTTP endpoints. | Both are cleaned by existing per-class throwaway-DB + Redis SCAN-zero discipline (`Phase8WebAppFactory.DisposeAsync:163-177`). The close-gate redis-SHA expects BEFORE==AFTER — seed/cleanup must be balanced (no leaked `skp:` keys). |
| Live service config | RabbitMQ queues: the Orchestrator's `orchestrator-{InstanceId}` temporary/auto-delete queue (present while container up); per-test temporary/auto-delete queues (TEST-RMQ-04). | NONE for the stable orchestrator queue (in both BEFORE and AFTER). Test queues auto-delete on harness/connection close — verified by the temporary endpoint config; the close-gate rabbitmq-SHA asserts net-zero. |
| OS-registered state | None — no Task Scheduler / systemd / pm2 involvement. | None (verified: no scheduler/daemon registration in this phase; Quartz is deferred). |
| Secrets/env vars | `RabbitMq__*`, `ConnectionStrings__*` env vars set in test fixture ctors (TEST-RMQ-02/03). | Capture+restore prior value in `DisposeAsync` (mirror `HealthDeadPostgresFixture:283-305`) — process-wide env vars persist across fixtures; the `[Collection("Observability")]` serialization prevents nesting races. |
| Build artifacts | Root `Dockerfile` produces `sk-baseapi-service` image; D-12 adds a wget apt layer. | Rebuild the baseapi-service image (`docker compose build baseapi-service`) before the close gate so the healthcheck (`compose.yaml:230` `wget --spider`) can execute. |

## Common Pitfalls

### Pitfall 1: Broadcast asserted as load-balance (TEST-RMQ-01)
**What goes wrong:** A fan-out test that asserts the message was consumed "by a consumer" passes whether ONE or BOTH endpoints received it — silently green on a broken competing-consumer topology.
**Why it happens:** `harness.Consumed.Any<StartOrchestration>()` is true if *any* endpoint consumed; it does not distinguish broadcast from load-balance.
**How to avoid:** Assert BOTH per-consumer harnesses saw the message (`harness.GetConsumerHarness<A>().Consumed.Any<...>()` AND `<B>...`), and assert the TOTAL consumed count == 2. Two distinct `InstanceId` endpoints binding the same message-type exchange is the broadcast precondition (`Orchestrator/Program.cs:31,33`).
**Warning signs:** A passing fan-out test where only one consumer-harness slot ever observes the message.

### Pitfall 2: `AddMassTransit` called twice (D-01 swap)
**What goes wrong:** `ConfigurationException: AddMassTransit() was already called` at host build because `Program.cs:8` already registered the real bus.
**Why it happens:** `ConfigureTestServices` runs after the app's `ConfigureServices`; adding the harness without removal is a second `AddMassTransit`.
**How to avoid:** `services.RemoveMassTransit()` immediately before `AddMassTransitTestHarness()` (Pattern 1).
**Warning signs:** Host fails to build; the 16 HTTP facts error in fixture init rather than in the assertion.

### Pitfall 3: rabbitmqctl header line breaks the SHA (TEST-RMQ-05)
**What goes wrong:** `rabbitmqctl list_queues name` (without `-q`) prefixes a "Listing queues for vhost / ..." line; if its wording or timing varies, BEFORE!=AFTER false-fails the gate.
**Why it happens:** Non-quiet `rabbitmqctl` emits a human-facing header.
**How to avoid:** Use `rabbitmqctl -q list_queues name` (quiet) — emits only queue names, one per line, sortable + hashable like the redis arm.
**Warning signs:** Intermittent close-gate failure with a one-line diff at the top of the rabbitmq snapshot.

### Pitfall 4: ES indexing latency / wrong field path (TEST-RMQ-02)
**What goes wrong:** The seam/published doc isn't in ES yet (poll returns null too early), OR the term query targets a non-existent `.keyword` sub-field (zero hits forever).
**Why it happens:** otel→ES has multi-second pipeline latency; the x-pack ECS data stream maps strings directly to `keyword` (no sub-field).
**How to avoid:** Use `ElasticsearchTestClient.PollEsForLog(..., timeoutMs: 30_000)` (positive-assertion budget per `OrchestrationLogsE2ETests.cs:131`) and query `EsIndexNames.CorrelationIdFieldPath` = `attributes.CorrelationId` directly (`EsIndexNames.cs`).
**Warning signs:** Test fails only on cold-start or CI; passes on warm local re-run.

### Pitfall 5: Host-port RabbitMQ unreachable from in-process WebApi (TEST-RMQ-02)
**What goes wrong:** The in-process WebApi configured with `RabbitMq__Host=localhost` connects to 5672 (MT default), but the host broker is on 5673 (`compose.yaml:166`) — publish fails or hangs.
**Why it happens:** `AddBaseApiMessaging` reads only `RabbitMq:Host` (no port key, `MessagingServiceCollectionExtensions.cs:43-45`); `Host(host, ...)` defaults to 5672.
**How to avoid:** See Open Question #1 — likely needs a small `RabbitMq:Port` config-read addition so the in-process publisher reaches `localhost:5673`, OR drive the proof differently. Resolve before planning TEST-RMQ-02.
**Warning signs:** TEST-RMQ-02 times out at the publish step (NOT at the ES poll).

## Code Examples

### Code Example 2: HealthDeadRabbitFixture (TEST-RMQ-03)
```csharp
// Source: tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:327-378 (HealthDeadRedisFixture mirror)
//       + src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:49-54 (Degraded cap)
private sealed class HealthDeadRabbitFixture : Phase8WebAppFactory
{
    // A host:port guaranteed unreachable. RabbitMq host config takes Host/Username/Password only;
    // a dead host name (or an unbound port via host:port form if AddBaseApiMessaging supports it)
    // makes the bus connection fail → bus check Degraded (NOT Unhealthy) → /health/ready stays 200.
    private readonly string? _prior;
    public HealthDeadRabbitFixture()  // live Postgres + live Redis kept; only the broker is dead
        : base(skipPostgresFixture: false, connectionStringOverride: null!,
               skipRedisFixture: false, redisConnectionStringOverride: null!)
    {
        _prior = Environment.GetEnvironmentVariable("RabbitMq__Host");
        try { Environment.SetEnvironmentVariable("RabbitMq__Host", "localhost-rabbit-dead.invalid"); }
        catch { Environment.SetEnvironmentVariable("RabbitMq__Host", _prior); throw; }
    }
    public override async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("RabbitMq__Host", _prior);
        await base.DisposeAsync();
    }
}
// Facts (TEST-RMQ-03): GET /health/live → 200; GET /health/ready → 200 (Degraded-not-Unhealthy, Phase 19 D-05).
// Note: the base ctor variants do NOT currently expose a "skip Redis but keep Postgres + dead broker"
// path that ALSO leaves both live — confirm the 4-arg ctor (Phase8WebAppFactory.cs:86) lets you keep
// both fixtures live (pass skipRedisFixture:false). The env-var override is broker-only.
```

### Code Example 3: WebApi publish-side log (D-07)
```csharp
// Source: src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:163-165
// Add an ILogger<OrchestrationService> (inject via ctor) and log the minted Guid at the publish seam.
// LEVEL: Information (the seam log on the orchestrator side is LogInformation — keep symmetry so both
// docs land in ES at the same level; /health filtering does not touch this path).
var startCorr = NewId.NextGuid();
await _publishEndpoint.Publish(
    new StartOrchestration(workflowIds.ToArray()) { CorrelationId = startCorr }, ct);
_logger.LogInformation("Published StartOrchestration CorrelationId={CorrelationId}", startCorr);
// The "CorrelationId={CorrelationId}" structured property is independent of the MEL "CorrelationId"
// log SCOPE (HTTP-stage id). TEST-RMQ-02 reads the body Guid from the message-property/text, not the
// scope. Confirm the otel pipeline serializes the structured property so ES can query it (the seam
// doc is found by message text + read-back attribute; the published doc by the body Guid value).
```

### Code Example 4: CORR-03 synthetic outbound-filter send
```csharp
// Source: src/BaseConsole.Core/Messaging/OutboundCorrelationPublishFilter.cs:13-21 (stamps ENVELOPE from ambient)
//       + AsyncLocalCorrelationAccessor.cs + MessagingServiceCollectionExtensions.cs:33,42-43 (wiring)
// The outbound filters stamp context.CorrelationId (ENVELOPE) from the ambient ICorrelationAccessor
// for any ICorrelated message whose ambient id parses as a Guid. Synthetic proof:
var ambient = new AsyncLocalCorrelationAccessor();
var stampId = NewId.NextGuid();
ambient.Set(stampId.ToString());

await using var provider = new ServiceCollection()
    .AddSingleton<ICorrelationAccessor>(ambient)
    .AddMassTransitTestHarness(x =>
        x.UsingInMemory((ctx, cfg) =>
        {
            cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);   // the filter under test
            cfg.ConfigureEndpoints(ctx);
        }))
    .BuildServiceProvider(true);
var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
try
{
    await harness.Bus.Publish(new StartOrchestration([Guid.NewGuid()]), ct);  // body CorrelationId left default
    var pub = harness.Published.Select<StartOrchestration>(ct).Single();
    // CORR-03: the OUTBOUND filter stamped the ambient id onto the ENVELOPE (no real downstream consumer).
    Assert.Equal(stampId, pub.Context.CorrelationId);
}
finally { await harness.Stop(ct); }
// NOTE the distinction from the WebApi path: WebApi sets the BODY (no outbound filter, D-02). The
// CONSOLE outbound filter stamps the ENVELOPE from the ambient accessor — that is what CORR-03 proves.
```

### Code Example 5: Three close-gate smell fixes (D-11)
```
Smell A — the `-1` fact-count fallback (phase-16-close.sh:64):
  PASSED=$(echo "$OUTPUT" | grep -oE 'Passed:\s+[0-9]+' | ... || echo "-1")
  FIX: a `-1` silently makes the 3-GREEN equality pass if ALL three runs fail to parse (-1==-1==-1).
       Replace the fallback with a hard error: if the count can't be parsed, exit 1 (do not coerce to -1).
       The PS1 already uses -1 as a sentinel + a WARNING (phase-17-close.ps1:97,120) — unify on
       "unparseable count is a HARD failure" in BOTH scripts.

Smell B — the `compose ps --format json` assumption divergence:
  PS1 (phase-17-close.ps1:36): (docker compose ps $svc --format json | ConvertFrom-Json).Health   # OBJECT
  SH  (phase-16-close.sh:31):  docker compose ps "$svc" --format json | jq -r '.[].Health'         # ARRAY
  FIX: docker compose ps --format json emits per-service JSON whose shape (object vs array) varies by
       compose version. Pin one robust form in BOTH: query a single service and tolerate object-OR-array
       (e.g. jq '.[0].Health // .Health'); the PS1 should likewise handle an array result.

Smell C — PS1/SH service-list divergence:
  PS1 services (phase-17-close.ps1:34): postgres, redis, otel-collector, elasticsearch, prometheus
  SH  services (phase-16-close.sh:30):  postgres redis elasticsearch prometheus   # MISSING otel-collector
  FIX: unify ONE canonical service list in both. For Phase 20 the full v3.4.0 stack adds rabbitmq,
       orchestrator, AND baseapi-service (the D-12 wget fix makes baseapi-service report healthy).
       Canonical Phase-20 list: postgres redis rabbitmq otel-collector elasticsearch prometheus
       orchestrator baseapi-service — with otel-collector still allowed non-healthy (it has no
       healthcheck; PS1:37 already special-cases it).
```

### Code Example 6: D-12 Dockerfile wget install + D-13 Stop seam fix
```dockerfile
# Source: Dockerfile:13-19 (runtime stage) — install wget BEFORE USER app (mirrors Phase 19 e4fcf67)
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BaseApi.Service.dll"]
```
```csharp
// Source: src/Orchestrator/Consumers/StopOrchestrationConsumer.cs:42 (the IN-02 mislog, D-13)
// BEFORE: logger.LogInformation("Scheduler job start (seam) for {WorkflowId}", workflowId);  // WRONG — Start string on a Stop
// AFTER (correct the Stop vocabulary; keep it distinct from the Start seam so it doesn't muddy TEST-RMQ-02):
   logger.LogInformation("Scheduler job stop (seam) for {WorkflowId}", workflowId);
// AND update the carried-over assertion locking the wrong string:
// Source: tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs:217
// BEFORE: Assert.Contains(logs.Messages, m => m.Contains("Scheduler job start"));
// AFTER:  Assert.Contains(logs.Messages, m => m.Contains("Scheduler job stop"));
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Phase 19 human-UAT item: manually prove live cross-process correlation in ES | TEST-RMQ-02 automates it (`19-VERIFICATION.md` UAT absorbed) | This phase | The single manual UAT becomes an automated fact. |
| Dual-SHA close gate (psql + redis) | Triple-SHA (+ rabbitmqctl) | This phase (D-09/D-11) | Broker queue leaks now gate the milestone. |
| `Phase8WebAppFactory` real-broker publish (1m40s hangs) | In-memory harness swap (D-01) | This phase | 16 HTTP facts go 204; suite fast + hermetic. |

**Deprecated/outdated:**
- MassTransit 9.x: COMMERCIAL — do NOT bump (`Directory.Packages.props:127-131`). 8.5.5 is the last Apache-2.0 line.
- `mapping.mode: none` in the otel ES exporter is ignored by elasticsearchexporter v0.152.0 (falls back to `logs-generic.otel-default` otel shape) — already accounted for by `EsIndexNames.cs`; do not "fix" the field path to `.keyword`.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `services.RemoveMassTransit()` is exposed in MassTransit 8.5.5's public API for the D-01 swap. | Pattern 1 | If absent, fall back to manual `RemoveAll` of MT descriptors + hosted services. Low risk — documented fallback. |
| A2 | Two endpoints hosting two DISTINCT consumer types (vs one type twice) is the cleanest 8.5.5 two-bus fan-out idiom for independent per-consumer-harness assertion. | Pattern 2 | If the same-type-twice approach works in the harness, the test simplifies; if neither, use two separate `IServiceProvider` harnesses. Verify Wave 0. |
| A3 | The in-process WebApi can reach the host broker on 5673 only with a port-aware host config; `AddBaseApiMessaging` currently reads no `RabbitMq:Port`. | Pattern 3, Pitfall 5, OQ#1 | If wrong (e.g. a default-port assumption holds), TEST-RMQ-02 publish step fails. HIGH — resolve before planning. |
| A4 | The D-07 structured log property `CorrelationId={guid}` surfaces in ES as a queryable field/text via the existing otel pipeline (same as the seam log under the scope). | Code Example 3 | If the structured property is NOT serialized, query the published doc by its message TEXT instead. Medium — has a fallback. |
| A5 | `rabbitmqctl -q list_queues name` output is stable BEFORE==AFTER given temporary/auto-delete test queues + the stable orchestrator queue. | Pattern 4, Pitfall 3 | If a test queue lingers (slow auto-delete on connection close), the gate false-fails. Mitigate with a short settle delay before the AFTER snapshot. Medium. |
| A6 | The seam-doc → read-back-body-Guid → published-doc two-query strategy works because the test does not know the minted Guid a priori. | Pattern 3 | If reading `attributes.CorrelationId` off the seam doc is awkward, the WebApi published-doc text `CorrelationId=G1` can be parsed first instead. Low — symmetric fallback. |

## Open Questions

1. **RabbitMQ host port for the in-process WebApi in TEST-RMQ-02 (A3).**
   - What we know: host AMQP port is 5673 (`compose.yaml:166`); `AddBaseApiMessaging` reads only `RabbitMq:Host` (no port, `MessagingServiceCollectionExtensions.cs:43-45`); MT `Host(host, ...)` defaults to 5672.
   - What's unclear: whether the in-process WebApi needs a small `RabbitMq:Port` config-read (or a `rabbitmq://localhost:5673` host form) to reach the host broker — or whether the proof should be driven another way.
   - Recommendation: planner adds a `RabbitMq:Port` read to `AddBaseApiMessaging` (defaulting to 5672 so compose-internal `rabbitmq:5672` is unaffected) and the TEST-RMQ-02 fixture sets `RabbitMq__Port=5673`. Small, in-scope, unblocks the proof. CONFIRM with user if it counts as "new runtime capability" (it is test-enablement, not behavior).

2. **Two-bus harness idiom (A2).** Single harness + two distinct consumer types/endpoints vs two `IServiceProvider`s. Resolve in Wave 0 with a spike; both prove broadcast — pick the one whose per-consumer assertion is unambiguous.

3. **Stop seam string wording (D-13).** CONTEXT says "correct the Stop log message"; this research proposes `"Scheduler job stop (seam) for {WorkflowId}"`. Confirm the exact desired wording with the planner (must stay distinct from the Start seam string so the ES proof and ack tests don't conflate them).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker + compose | TEST-RMQ-02, close gate | (assumed — prior phases ran the stack) | — | None — TEST-RMQ-02 + gate are stack-mandatory (D-02/D-10). |
| RabbitMQ container `sk-rabbitmq` | TEST-RMQ-02, close-gate rabbitmq SHA | via compose (`compose.yaml:161-167`) | rabbitmq:4.1.8-management-alpine | None. |
| Orchestrator container | TEST-RMQ-02 | via compose (`compose.yaml:185`) | — | None — it is the consumer under proof. |
| Elasticsearch (host :9200) | TEST-RMQ-02 | via compose | ES 8.15.5 (`EsIndexNames.cs`) | None — ES is the assertion backend. |
| `rabbitmqctl` (inside container) | close-gate SHA | ships in the rabbitmq image | — | None. |
| wget (inside baseapi-service image) | baseapi-service healthcheck during close gate | ✗ until D-12 | — | D-12 installs it; the image MUST be rebuilt before the gate runs. |

**Missing dependencies with no fallback:** None blocking once D-12 lands (rebuild `baseapi-service`).
**Missing dependencies with fallback:** wget — installed by D-12 (the close-gate prerequisite this phase folds in).

## Validation Architecture

> Nyquist framing: for each requirement, the minimum observable signal that proves it true, and the sampling granularity that avoids aliasing (false-green from under-sampling a topology/timing condition).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 (`Directory.Packages.props:115`) |
| Config file | none (CPM + standard xUnit); `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release --no-build --filter-class *FanOutBroadcastTests` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` |

### Phase Requirements → Test Map (the heart of this proof phase)
| Req | Behavior | Observable signal (the assertion) | Sampling granularity / failure mode guarded |
|-----|----------|-----------------------------------|----------------------------------------------|
| CORR-03 | Outbound filter stamps ambient id on the ENVELOPE | `harness.Published.Select<StartOrchestration>().Single().Context.CorrelationId == stampId` | One published message inspected; guards against the filter NOT stamping (silent ambient loss). Sample = exactly the message that traversed the publish pipeline. |
| CORR-04 | Per-stage handoff (HTTP id ≠ body Guid; body Guid in ES) | (== TEST-RMQ-02) two ES docs share body Guid `G1` AND `G1 != httpCorr` | Two-doc cross-check; guards against the HTTP id leaking onto the bus (the anti-pattern Phase 19 D-01 forbids). Under-sampling (asserting only equality, not distinctness) would false-green a single-id-across-hops regression — both halves required. |
| TEST-RMQ-01 | Fan-out broadcast at 2 replicas | BOTH `GetConsumerHarness<A/B>().Consumed.Any<Start>()` true AND total `Consumed.Count<Start>() == 2` | Per-endpoint sampling (count==2). Guards against the load-balance trap; a single `.Any()` under-samples and aliases broadcast≡load-balance. THE #1 trap. |
| TEST-RMQ-02 | End-to-end correlation in ES | seam doc + published doc both at `attributes.CorrelationId == G1`, found via `PollEsForLog(30_000ms)` | Real cross-process; 30s poll over-samples the otel→ES latency (Nyquist: poll interval << indexing latency via exponential backoff). Guards against silent drop anywhere in HTTP→publish→broker→consume→otel→ES. |
| TEST-RMQ-03 | WebApi soft-on-broker | `GET /health/live` → 200 AND `GET /health/ready` → 200 with broker dead | Both probes sampled (live + ready); guards against a broker-down condition flipping `ready` to 503 (the MinimalFailureStatus=Degraded contract). |
| TEST-RMQ-04 | Temporary/auto-delete, no global purge | Close-gate rabbitmq SHA BEFORE==AFTER (no leaked test queues); endpoints declared `e.Temporary=true` | Suite-level snapshot diff; guards against durable test-queue leak. Sample = full queue list pre/post entire suite (catches any per-test leak in aggregate). |
| TEST-RMQ-05 | Triple-SHA leak gate + 3× GREEN | `SHA256(psql \l)` BEFORE==AFTER AND `SHA256(redis --scan)` BEFORE==AFTER AND `SHA256(rabbitmqctl -q list_queues name)` BEFORE==AFTER, all 3 runs identical Passed count | Three independent snapshots × 3 runs; guards against DB/key/queue leak AND flaky fact-count drift. The `-1`-fallback smell (Smell A) is itself an aliasing bug — three failed parses (-1==-1==-1) false-green; fix makes unparseable a hard fail. |

### Sampling Rate
- **Per task commit:** `dotnet test ... --filter-class <the test for that task>` (quick, < 30s for in-memory tests).
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` (includes the real-stack TEST-RMQ-02 — requires the Docker stack up).
- **Phase gate:** `scripts/phase-20-close.{ps1,sh}` — 3× consecutive GREEN + triple-SHA BEFORE==AFTER, full stack up.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs` — the D-01 in-memory swap (verify `RemoveMassTransit()` API surface in 8.5.5 — A1).
- [ ] Spike the two-bus fan-out idiom (A2) — distinct-consumer-types vs two providers — before writing `FanOutBroadcastTests.cs`.
- [ ] Resolve OQ#1 (RabbitMq port for the in-process WebApi) before `CorrelationPropagationE2ETests.cs`.
- [ ] Confirm D-07 structured-property serialization to ES (A4) — spike one POST + ES poll.

*(Existing test infrastructure — `Phase8WebAppFactory`, `ElasticsearchTestClient`, `HealthEndpointsTests` fixtures, the in-memory harness precedents — covers everything else.)*

## Security Domain

> `security_enforcement` is not set to false in config (absent = enabled). This phase adds NO new attack surface (proof + closeout, no new endpoints, no new data paths). The relevant controls are inherited and already shipped.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth surface added; dev-only `guest/guest` broker creds are an accepted dev posture (T-19-broker-creds, `compose.yaml:157`), k8s/prod override. |
| V3 Session Management | no | N/A. |
| V4 Access Control | no | N/A. |
| V5 Input Validation | yes (inherited) | The inbound id is treated as opaque untrusted text — scope VALUE only, never interpolated into a log template (`InboundCorrelationConsumeFilter.cs` T-18-04). No new validation in this phase. |
| V6 Cryptography | no | SHA-256 in the close gate is integrity-snapshotting, not security crypto (`Get-FileHash`/`sha256sum`). |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log injection via correlation id | Tampering / Information disclosure | Opaque-text-as-scope-value discipline (already shipped, `InboundCorrelationConsumeFilter.cs`); no change this phase. |
| Health endpoint leaking secrets | Information disclosure | `/health/ready` body asserted secret-free (`HealthEndpointsTests.cs:113-134`); the new `HealthDeadRabbitFixture` facts inherit this — DO NOT assert on connection strings in the broker-down body. |
| Test env-var leak across fixtures | Tampering | Capture+restore env vars in `DisposeAsync` (`HealthDeadPostgresFixture:283-305` pattern); `[Collection("Observability")]` serialization prevents nesting races. |

## Sources

### Primary (HIGH confidence — repo code read this session)
- `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — fixture base, ctor variants, `ConfigureAppConfiguration`/`AddInMemoryCollection` seam, DisposeAsync cleanup discipline.
- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — `ConfigureTestServices` seam (the D-01 swap point).
- `tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs` — in-memory `AddMassTransitTestHarness` + `harness.Published` precedent.
- `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` — `AddMassTransitTestHarness(x => { AddConsumer; UsingInMemory })` + the IN-02 assertion at line 217 (D-13).
- `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — `HealthDeadRedisFixture`/`HealthDeadPostgresFixture` env-var-in-ctor dead-port pattern.
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` + `EsIndexNames.cs` — ES poll-and-assert + verified `attributes.CorrelationId` field path.
- `tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs` — ES E2E query shape + 30s poll budget.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:163-165,240-242` — publish call sites (D-07 target).
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs:42` + `StartOrchestrationConsumer.cs:46` — the seam-log strings (D-13).
- `src/Orchestrator/Program.cs:26-34` — the production fan-out wiring (`e.InstanceId`/`e.Temporary`) TEST-RMQ-01 mirrors.
- `src/BaseConsole.Core/Messaging/OutboundCorrelationPublishFilter.cs` + `OutboundCorrelationSendFilter.cs` + `AsyncLocalCorrelationAccessor.cs` + `InboundCorrelationConsumeFilter.cs` — CORR-03 envelope-stamping mechanic + inbound body-read.
- `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — publish-only `AddBaseApiMessaging`, Degraded cap, no `RabbitMq:Port` read.
- `scripts/phase-17-close.ps1` + `scripts/phase-16-close.sh` — dual-SHA gate to extend; the three smells (service-list divergence, `--format json` assumption, `-1` fallback).
- `Dockerfile` — runtime stage (no wget; D-12 target).
- `compose.yaml:161-249` — rabbitmq tier (host 5673/15673, sk-rabbitmq), baseapi-service wget healthcheck (line 230), orchestrator service.
- `Directory.Packages.props:111-132` — MassTransit 8.5.5 pin + test packages.
- `.planning/ROADMAP.md:82-91` (Phase 20 SC) + `.planning/REQUIREMENTS.md:73-100` (CORR-03/04, TEST-RMQ-01..05).

### Secondary (MEDIUM confidence — official docs via Context7 CLI fallback)
- `masstransit.massient.com/guides/unit-testing/web-application-factory` [CITED] — `AddMassTransitTestHarness` in `ConfigureServices`, `application.Services.GetTestHarness()`, `harness.Published.Any<T>(...)`, `harness.GetConsumerHarness<T>()`.
- `masstransit.massient.com/guides/unit-testing` [CITED] — in-memory transport default, `UsingInMemory((ctx,cfg)=>cfg.ConfigureEndpoints(ctx))`.

### Tertiary (LOW confidence — flagged in Assumptions Log)
- `RemoveMassTransit()` exact availability in 8.5.5 (A1) — verify Wave 0.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — versions pinned + read directly from `Directory.Packages.props`.
- Architecture / harness mechanics: HIGH — every shape grounded in an existing repo precedent; the WebApplicationFactory harness pattern cross-confirmed with official docs.
- Real-stack TEST-RMQ-02 port plumbing: MEDIUM — the 5673 vs 5672 gap is a real Open Question (A3/OQ#1) the planner must resolve.
- Close-gate smell fixes: HIGH — the three smells located at exact lines in both scripts.
- Pitfalls: HIGH — derived from the read code + the documented prior-phase fixes (IN-05 keyword path, e4fcf67 wget).

**Research date:** 2026-05-30
**Valid until:** 2026-06-29 (stable .NET/MassTransit stack; 30 days)
