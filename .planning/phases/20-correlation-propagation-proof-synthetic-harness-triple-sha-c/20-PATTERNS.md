# Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout - Pattern Map

**Mapped:** 2026-05-30
**Files analyzed:** 11 (5 new tests/fixtures, 1 new factory, 2 new scripts, 4 source/test/Docker edits)
**Analogs found:** 11 / 11 (every file has a verified in-repo analog — this is a mirror-and-extend phase, zero invented patterns)

> All excerpts below were read this session and the line numbers verified against the live repo. RESEARCH.md pre-pinned most of these; every pin was confirmed (one correction: the IN-02 mislog assertion is at `StartStopConsumerAckTests.cs:217`, in the **Stop_Present** test — confirmed exact).

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs` | new test factory | request-response (in-memory bus swap) | `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (`ConfigureTestServices` seam) + `Phase8WebAppFactory` | role-match (seam exact; the `RemoveMassTransit()` swap is new wiring) |
| `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs` (TEST-RMQ-01) | new test | event-driven (pub→2 consumers) | `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` (in-memory harness shape) + `src/Orchestrator/Program.cs:28-34` (fan-out wiring) | exact (harness) + exact (production fan-out to mirror) |
| `tests/BaseApi.Tests/Orchestrator/OutboundFilterSyntheticTests.cs` (CORR-03) | new test | event-driven (synthetic outbound send) | `src/BaseConsole.Core/Messaging/OutboundCorrelationPublishFilter.cs` + `AsyncLocalCorrelationAccessor.cs` + `OrchestrationServicePublishTests.cs` (`harness.Published.Select<T>().Single()`) | exact |
| `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` (TEST-RMQ-02 / CORR-04) | new test | request-response → event-driven → ES read (cross-process) | `tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs` + `Helpers/ElasticsearchTestClient.cs` + `HealthEndpointsTests.cs:261-306` (env-var-in-ctor) | exact (ES poll) + role-match (real-stack factory new) |
| `HealthDeadRabbitFixture` (in `HealthEndpointsTests.cs`, TEST-RMQ-03) | new test fixture | request-response (health probe, dead dep) | `HealthDeadRedisFixture` / `HealthDeadPostgresFixture` (`HealthEndpointsTests.cs:251-378`) | exact |
| `scripts/phase-20-close.ps1` | new script | batch (snapshot diff + test gate) | `scripts/phase-17-close.ps1` | exact (copy + extend) |
| `scripts/phase-20-close.sh` | new script | batch (snapshot diff + test gate) | `scripts/phase-16-close.sh` | exact (copy + extend) |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` (D-07) | source edit | event-driven (publish-side log) | self (`OrchestrationService.cs:163-165`) | edit-in-place |
| `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` (D-13) | source edit | event-driven (seam log string fix) | self (`StopOrchestrationConsumer.cs:42`) + `StartOrchestrationConsumer.cs` | edit-in-place |
| `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` (D-13) | test edit | event-driven (assertion string fix) | self (line 217) | edit-in-place |
| `Dockerfile` (root, D-12) | config edit | build (apt layer before USER app) | self (`Dockerfile:13-19`) + Phase 19 `e4fcf67` orchestrator fix | edit-in-place |

---

## Pattern Assignments

### `tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs` (new factory, D-01)

**Analog:** `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (the `ConfigureTestServices` seam) — extend a `Phase8WebAppFactory` subclass.

**The seam is `ConfigureTestServices`, which runs AFTER `Program.cs`'s `AddBaseApiMessaging` → `AddMassTransit`** (`WebAppFactory.cs:37-52`):
```csharp
public class WebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(typeof(WebAppFactory).Assembly);
            // ...
        });
    }
}
```

**Why removal is mandatory:** `Program.cs` already calls `AddBaseApiMessaging` → `AddMassTransit` (`MessagingServiceCollectionExtensions.cs:47`). Calling `AddMassTransitTestHarness()` again throws `ConfigurationException("AddMassTransit() was already called")`. The new factory must `services.RemoveMassTransit()` first, then `services.AddMassTransitTestHarness()`, inside `ConfigureTestServices`. **Wave-0 verify (A1):** confirm `RemoveMassTransit()` is in MassTransit 8.5.5's public surface; fallback is manual `RemoveAll(typeof(IBusControl))` + `IBus` + the MT `IHostedService`.

**What this unblocks:** the 16 Phase-19 orchestration HTTP facts go 204 because the harness's `IPublishEndpoint` satisfies `OrchestrationService`'s ctor injection (`OrchestrationService.cs:61,80`).

---

### `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs` (new test, TEST-RMQ-01)

**Analog (harness shape):** `StartStopConsumerAckTests.cs:91-115` — the in-memory `AddMassTransitTestHarness(x => { AddConsumer; UsingInMemory })` builder:
```csharp
private static ServiceProvider BuildStartHarness(
    IConnectionMultiplexer mux, CapturingLoggerProvider? logs = null)
    => new ServiceCollection()
        .AddSingleton(mux)
        .AddSingleton(new OrchestratorRedisOptions(Prefix))
        .AddLogging(b => { if (logs is not null) b.AddProvider(logs); })
        .AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>();
            x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
        })
        .BuildServiceProvider(true);
```

**Analog (the production fan-out wiring to mirror):** `src/Orchestrator/Program.cs:28-34` — two consumers, SAME `InstanceId`, `Temporary=true`. For the broadcast TEST the test instead uses TWO **distinct** InstanceIds so two queues bind the same message-type exchange:
```csharp
var instanceId = builder.Configuration["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N");
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
    x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
});
```

**Publish + per-endpoint assertion pattern** (from `StartStopConsumerAckTests.cs:119-135`, extended for broadcast):
```csharp
await harness.Bus.Publish(new StartOrchestration([Guid.NewGuid()]), ct);
Assert.True(await harness.Consumed.Any<StartOrchestration>(ct));               // existing single-endpoint shape
Assert.False(await harness.Consumed.Any<StartOrchestration>(m => m.Exception != null, ct));
```
**Anti-pattern guard (Pitfall 1 — THE #1 trap):** assert BOTH `GetConsumerHarness<A>().Consumed.Any<...>()` AND `<B>...`, AND `harness.Consumed.Count<StartOrchestration>(ct) == 2`. A single `.Any()` aliases broadcast≡load-balance and silently greens a competing-consumer regression. **Wave-0 spike (A2):** two distinct consumer TYPES on two endpoints vs two separate `IServiceProvider` harnesses — both prove broadcast; pick the one whose per-consumer assertion is unambiguous.

---

### `tests/BaseApi.Tests/Orchestrator/OutboundFilterSyntheticTests.cs` (new test, CORR-03)

**Analog (the filter under test):** `src/BaseConsole.Core/Messaging/OutboundCorrelationPublishFilter.cs:13-24` — stamps the **ENVELOPE** `context.CorrelationId` (not the body) from the ambient accessor:
```csharp
public sealed class OutboundCorrelationPublishFilter<T>(ICorrelationAccessor accessor)
    : IFilter<PublishContext<T>> where T : class
{
    public Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        if (context.Message is ICorrelated && Guid.TryParse(accessor.Get(), out var id))
            context.CorrelationId = id;   // envelope, not body (D-01)
        return next.Send(context);
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-out-publish");
}
```

**Analog (the ambient accessor to seed):** `AsyncLocalCorrelationAccessor.cs:10-17` — `Set(string?)` / `Get()`.

**Analog (published-message inspection):** `OrchestrationServicePublishTests.cs:144` — `harness.Published.Select<T>(ct).Single()` then assert on `.Context.CorrelationId`:
```csharp
var published = harness.Published.Select<StartOrchestration>(ct).Single();
Assert.Equal(published.Context.Message.CorrelationId, published.Context.CorrelationId);
```

**CORR-03 wiring (per RESEARCH Code Example 4):** build a harness whose `UsingInMemory((ctx,cfg) => { cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx); cfg.ConfigureEndpoints(ctx); })`, register `ICorrelationAccessor` as the ambient singleton, `Set(stampId.ToString())`, publish an `ICorrelated` message with body CorrelationId left default, then `Assert.Equal(stampId, harness.Published.Select<StartOrchestration>(ct).Single().Context.CorrelationId)`. **Key distinction vs the WebApi path:** WebApi sets the **BODY** (no outbound filter — `MessagingServiceCollectionExtensions.cs:16-17` documents publish-only, NO filters); the CONSOLE outbound filter stamps the **ENVELOPE** — that is the synthetic-send subject under test.

---

### `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` (new test, TEST-RMQ-02 / CORR-04)

**Analog (ES poll-and-assert):** `OrchestrationLogsE2ETests.cs:120-142` — query by per-test-unique id, 30s budget, assert `NotNull` + value present:
```csharp
using var esClient = new ElasticsearchTestClient();
var queryBody = $$"""
  {
    "size": 10,
    "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{corrId}}" } }
  }
  """;
var hit = await esClient.PollEsForLog(queryBody, timeoutMs: 30_000, ct: ct);
Assert.NotNull(hit);
var rawJson = hit!.Value.GetRawText();
Assert.Contains(corrId, rawJson);
```

**Analog (the poll helper — reuse verbatim, D-05):** `ElasticsearchTestClient.cs:62-109` — `PollEsForLog(queryBody, timeoutMs, indexPath?, ct)`; exponential backoff 200ms→3200ms; 404/empty-hits tolerance; `BaseAddress = http://localhost:9200/`; returns a `Clone()`d detached `JsonElement?`. **Do not re-build this.**

**Analog (field-path constants — reuse verbatim):** `EsIndexNames.cs` — `LogsDataStream = "logs-generic.otel-default"` (line 40); `CorrelationIdFieldPath = "attributes.CorrelationId"` (line 71). **Anti-pattern (Pitfall 4):** querying `attributes.CorrelationId.keyword` returns zero hits forever — the x-pack ECS data stream maps strings directly to `keyword`, no sub-field (documented at `EsIndexNames.cs:49-69`, the reverted IN-05 commit 9370e89).

**Analog (env-var-in-ctor host override for the real-stack factory):** `HealthEndpointsTests.cs:261-293` (`HealthDeadPostgresFixture`) — set env vars in the FIXTURE CTOR (before the base `WebApplicationFactory<Program>` ctor runs); `ConfigureAppConfiguration`/`AddInMemoryCollection` is TOO LATE (the connection string is captured by value at registration — see the `DEVIATION FROM PLAN` doc-comment at lines 228-249). Capture+restore in `DisposeAsync`:
```csharp
_priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
try { Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", DeadConnectionString); }
catch { Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue); throw; }
// ...
public override async ValueTask DisposeAsync()
{
    Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
    await base.DisposeAsync();
}
```

**Two-doc full-chain assertion (D-07):** read the orchestrator SEAM doc (match `"Scheduler job start"`), extract its `attributes.CorrelationId` (= body Guid `G1`); read the WebApi PUBLISHED doc (`"Published StartOrchestration CorrelationId=G1"`); assert `webapi.published.CorrelationId == orchestrator.seam.CorrelationId` AND both `!=` the HTTP `X-Correlation-Id`. Use a per-run `httpCorr = $"{Guid.NewGuid():N}"` (the `OrchestrationLogsE2ETests.cs:101` isolation idiom).

> **OPEN QUESTION #1 / A3 (HIGH — resolve before planning this test):** the in-process WebApi configured `RabbitMq__Host=localhost` connects to **5672** (MT default), but the host broker is on **5673** (`compose.yaml:166`). `AddBaseApiMessaging` reads only `RabbitMq:Host` — NO port key (`MessagingServiceCollectionExtensions.cs:43-45`). Recommended in-scope mechanic: add a `RabbitMq:Port` read defaulting to 5672 (so compose-internal `rabbitmq:5672` is unaffected) and set `RabbitMq__Port=5673` in the fixture. Flag with user as test-enablement, not new behavior.

---

### `HealthDeadRabbitFixture` (in `HealthEndpointsTests.cs`, TEST-RMQ-03)

**Analog:** `HealthDeadRedisFixture` (`HealthEndpointsTests.cs:327-378`) + `HealthDeadPostgresFixture` (`:251-306`) — private sealed `Phase8WebAppFactory` variants with env-var-in-ctor dead-dep config.

**Mirror shape (env-var override = broker only; keep Postgres + Redis live):**
```csharp
private sealed class HealthDeadRabbitFixture : Phase8WebAppFactory
{
    private readonly string? _prior;
    public HealthDeadRabbitFixture()
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
```

**Why `ready` stays 200:** the auto-registered MassTransit bus check is capped at `Degraded` via `MinimalFailureStatus` (`MessagingServiceCollectionExtensions.cs:49-54`) — a broker-down condition never flips `/health/ready` to 503 (Phase 19 D-05).

**Facts (TEST-RMQ-03):** `GET /health/live → 200` AND `GET /health/ready → 200` with the broker dead. **Security note (inherited):** do NOT assert on connection strings in the broker-down `/health/ready` body (`HealthEndpointsTests.cs:113-134` secret-free contract). **Wave-0 verify:** the `Phase8WebAppFactory` 4-arg ctor (`Phase8WebAppFactory.cs:86`) lets you keep BOTH Postgres + Redis live (`skipRedisFixture:false`); the env-var override is broker-only.

---

### `scripts/phase-20-close.ps1` (new script, TEST-RMQ-05 / D-09 / D-11)

**Analog:** `scripts/phase-17-close.ps1` (copy + extend). The redis arm to mirror for rabbitmq (`phase-17-close.ps1:54-58`):
```powershell
$beforeRedis = (docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive | Out-String).Trim()
$beforeRedisHash = (Get-FileHash -Algorithm SHA256 -InputStream (
    [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRedis))
)).Hash.ToLower()
```
**New rabbitmq arm (D-09 — use `-q` to strip the header, Pitfall 3):**
```powershell
$beforeRmq = (docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Sort-Object -CaseSensitive | Out-String).Trim()
$beforeRmqHash = (Get-FileHash -Algorithm SHA256 -InputStream (
    [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($beforeRmq)))).Hash.ToLower()
```
Add to BOTH the BEFORE block (~line 54-58) and AFTER block (~line 132-136), plus a third invariant-assertion block mirroring `phase-17-close.ps1:150-157`.

### `scripts/phase-20-close.sh` (new script)

**Analog:** `scripts/phase-16-close.sh`. Redis arm (`:47-49`) → rabbitmq arm:
```bash
BEFORE_RMQ=$(docker exec sk-rabbitmq rabbitmqctl -q list_queues name | LC_ALL=C sort)
BEFORE_RMQ_HASH=$(printf '%s' "$BEFORE_RMQ" | sha256sum | awk '{print $1}')
```

**Net-zero-leak reasoning (D-09):** test endpoints are temporary/auto-delete + per-class-prefixed → zero leaked queues; the Orchestrator's own `orchestrator-{InstanceId}` queue (`Program.cs:31,33`, `e.Temporary=true`) is present in BOTH BEFORE and AFTER when the stack is up (stable, not a leak). BEFORE==AFTER holds. (A5: if a test queue lingers on slow auto-delete, add a short settle delay before AFTER.)

**Three smell fixes (D-11) — common to both scripts:**
- **Smell A (`-1` fact-count fallback):** `phase-16-close.sh:64` coerces an unparseable count to `-1`; three failed parses (`-1==-1==-1`) false-green the 3-GREEN gate. `phase-17-close.ps1:97,120` already uses `-1` + a WARNING. **Fix:** unparseable count is a HARD failure (exit 1) in BOTH.
- **Smell B (`compose ps --format json` shape divergence):** PS1 (`:36`) reads `.Health` off an OBJECT; SH (`:31`) reads `.[].Health` off an ARRAY. The shape varies by compose version. **Fix:** tolerate object-OR-array in both (e.g. `jq '.[0].Health // .Health'`).
- **Smell C (service-list divergence):** PS1 (`:34`) lists `postgres, redis, otel-collector, elasticsearch, prometheus`; SH (`:30`) MISSES `otel-collector`. **Fix:** ONE canonical Phase-20 list in both — `postgres redis rabbitmq otel-collector elasticsearch prometheus orchestrator baseapi-service` (otel-collector still allowed non-healthy; it has no healthcheck — PS1:37 already special-cases it). The D-12 wget fix makes `baseapi-service` report healthy.

---

### `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` (source edit, D-07)

**Edit site:** the Start publish call at `OrchestrationService.cs:163-165`:
```csharp
await _publishEndpoint.Publish(
    new StartOrchestration(workflowIds.ToArray()) { CorrelationId = NewId.NextGuid() },
    ct);
```
**Add:** capture the minted Guid into a local, then `_logger.LogInformation("Published StartOrchestration CorrelationId={CorrelationId}", startCorr);` at `Information` level (symmetry with the orchestrator seam log so both docs land in ES at the same level). **Ctor wiring:** the class has NO `ILogger` today — inject `ILogger<OrchestrationService>` into the `internal` ctor (`OrchestrationService.cs:69-81`) and add a `private readonly ILogger<OrchestrationService> _logger;` field beside the others (`:51-62`). **Caller impact:** `OrchestrationServicePublishTests.cs:104-119` `BuildService(...)` constructs this ctor directly via `InternalsVisibleTo` — that helper must add a logger arg (e.g. `NullLogger<OrchestrationService>.Instance`). The Stop publish (`:240-242`) is symmetric if a Stop publish-log is also wanted (D-07 names only Start). **A4:** confirm the `CorrelationId={CorrelationId}` structured property serializes to ES; fallback is to query the published doc by message TEXT.

---

### `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` (source edit, D-13 / IN-02)

**Edit site:** `StopOrchestrationConsumer.cs:42` — the Stop consumer logs the **Start** seam string (copy-paste bug):
```csharp
// BEFORE (WRONG — Start string on a Stop):
logger.LogInformation("Scheduler job start (seam) for {WorkflowId}", workflowId);
// AFTER (distinct Stop vocabulary so it doesn't muddy the TEST-RMQ-02 ES proof):
logger.LogInformation("Scheduler job stop (seam) for {WorkflowId}", workflowId);
```
**OQ#3:** RESEARCH proposes `"Scheduler job stop (seam) for {WorkflowId}"`; confirm the exact wording with the planner — it MUST stay distinct from the Start seam string.

### `tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs` (test edit, D-13)

**Edit site (CONFIRMED exact):** line 217, inside `Stop_Present_In_L2_Logs_Seam_And_Writes_Nothing`:
```csharp
// BEFORE:
Assert.Contains(logs.Messages, m => m.Contains("Scheduler job start"));
// AFTER (track the corrected Stop string):
Assert.Contains(logs.Messages, m => m.Contains("Scheduler job stop"));
```
> Note: line 156 (`Start_Present_...`) ALSO asserts `"Scheduler job start"` — that one is CORRECT (it tests the Start consumer) and must NOT be changed. Only line 217 (the Stop test) carries the wrong string.

---

### `Dockerfile` (root, config edit, D-12)

**Edit site:** the runtime stage `Dockerfile:13-19` (no wget/curl in `aspnet:8.0-bookworm-slim`):
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BaseApi.Service.dll"]
```
**Add the apt layer BEFORE `USER app`** (mirrors the Phase 19 orchestrator fix `e4fcf67`):
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish .
USER app
...
```
**Why:** `compose.yaml:230` healthcheck `["CMD", "wget", "--spider", "-q", "http://localhost:8080/health/ready"]` cannot execute without wget → `sk-baseapi-service` never reports healthy → the close gate (which now lists `baseapi-service`) fails pre-flight. No compose change needed; rebuild the image (`docker compose build baseapi-service`) before the gate.

---

## Shared Patterns

### In-memory MassTransit harness construction (D-01 / D-04)
**Source:** `StartStopConsumerAckTests.cs:91-115` + `OrchestrationServicePublishTests.cs:129-133`
**Apply to:** FanOutBroadcastTests, OutboundFilterSyntheticTests, HarnessWebAppFactory
```csharp
await using var provider = new ServiceCollection()
    /* singletons */ 
    .AddMassTransitTestHarness(x => { /* AddConsumer / filters */ x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx)); })
    .BuildServiceProvider(true);
var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
try { /* publish + assert via harness.Published / harness.Consumed */ }
finally { await harness.Stop(ct); }
```
Standard idioms: `var ct = TestContext.Current.CancellationToken;` (xunit.v3); `harness.Published.Select<T>(ct).Single()`; `harness.Consumed.Any<T>(m => m.Exception != null, ct)`.

### Env-var-in-ctor dead/real-dependency host override (TEST-RMQ-02 / TEST-RMQ-03)
**Source:** `HealthEndpointsTests.cs:261-306` (the `DEVIATION FROM PLAN` doc-comment at `:228-249` explains WHY `ConfigureAppConfiguration` is too late)
**Apply to:** HealthDeadRabbitFixture, the TEST-RMQ-02 RealStackWebAppFactory
**Discipline:** set in ctor (before base `WebApplicationFactory<Program>` ctor); capture+restore in `DisposeAsync`; rely on `[Collection("Observability")]` serialization to prevent env-var nesting races; wrap `SetEnvironmentVariable` in try/catch that restores on throw.

### ES poll-and-assert by unique correlation id (D-05 / D-07)
**Source:** `ElasticsearchTestClient.PollEsForLog` (`:62`) + `OrchestrationLogsE2ETests.cs:120-142` + `EsIndexNames.cs:40,71`
**Apply to:** CorrelationPropagationE2ETests
**Discipline:** per-test unique id, never cumulative counts; 30s timeout; query `attributes.CorrelationId` directly (NOT `.keyword`); reuse the helper verbatim.

### Body-only correlation contract (per-stage handoff — the proof's whole point)
**Source:** `OrchestrationService.cs:156-165` (BODY only, envelope unset, HTTP id NOT carried) + `OutboundCorrelationPublishFilter.cs:18-19` (the CONSOLE path stamps the ENVELOPE from ambient)
**Apply to:** the assertions in OutboundFilterSyntheticTests (envelope) and CorrelationPropagationE2ETests (body Guid ≠ HTTP id). These are DIFFERENT mechanics — keep them distinct: WebApi=body, Console-outbound-filter=envelope.

### Triple-SHA BEFORE=AFTER + 3-GREEN close gate (D-09/D-10/D-11)
**Source:** `phase-17-close.ps1` + `phase-16-close.sh` (full files)
**Apply to:** phase-20-close.{ps1,sh}
**Pattern:** pre-flight health check → BEFORE snapshots (psql `\l` + redis `--scan` + NEW rabbitmq `-q list_queues name`, each sorted → SHA-256) → zero-warning build (both configs) → 3× `dotnet test` (identical Passed count) → AFTER snapshots → assert all three SHA invariants BEFORE==AFTER. Exit 0/1/2.

---

## No Analog Found

None. Every file has a verified in-repo analog — this is a mirror-and-extend proof+closeout phase.

---

## Metadata

**Analog search scope:** `tests/BaseApi.Tests/{Composition,Orchestrator,Orchestration,Observability,Middleware}/`, `src/{BaseApi.Service,BaseApi.Core,Orchestrator,BaseConsole.Core}/`, `scripts/`, root `Dockerfile`, `compose.yaml`.
**Files scanned (read this session):** OrchestrationServicePublishTests.cs, StartStopConsumerAckTests.cs, HealthEndpointsTests.cs (220-388), ElasticsearchTestClient.cs, EsIndexNames.cs, OrchestrationLogsE2ETests.cs (90-144), Program.cs (Orchestrator), OutboundCorrelationPublishFilter.cs, StopOrchestrationConsumer.cs, OrchestrationService.cs (1-90, 150-243), WebAppFactory.cs, MessagingServiceCollectionExtensions.cs, AsyncLocalCorrelationAccessor.cs, phase-17-close.ps1, phase-16-close.sh, Dockerfile, compose.yaml (rabbitmq tier).
**Pattern extraction date:** 2026-05-30

**Wave-0 verification carry-forward (from RESEARCH Assumptions Log):**
- A1: `RemoveMassTransit()` public in 8.5.5 — fallback manual `RemoveAll`.
- A2: two-bus fan-out idiom (distinct consumer types vs two providers).
- A3 / OQ#1: `RabbitMq:Port` read for the in-process WebApi to reach `localhost:5673` (HIGH — resolve before TEST-RMQ-02 planning).
- A4: D-07 structured-property serialization to ES — fallback query by message text.
- A5: rabbitmqctl SHA stability — fallback short settle delay before AFTER.
