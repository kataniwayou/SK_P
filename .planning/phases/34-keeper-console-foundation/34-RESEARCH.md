# Phase 34: Keeper Console Foundation - Research

**Researched:** 2026-06-05
**Domain:** .NET 8 Generic-Host console (MassTransit/RabbitMQ competing-consumer) + Docker Compose multi-replica
**Confidence:** HIGH (every finding grounded in in-repo source; no external lookups needed — this is a console-mirror of an existing, shipped console)

## Summary

Phase 34 is a **console-mirror** phase: stand up `src/Keeper/` as a structural twin of the shipped `src/Orchestrator/` console, leaner (no Quartz/Cronos, no L1/hydration, no metrics block, and KEEPING the `BaseConsole.Core` default `StartupCompletionService` that Orchestrator removes). CONTEXT.md decisions D-01..D-09 are fully consistent with the actual code — this research validates each against the real `Orchestrator`, `BaseConsole.Core`, `Messaging.Contracts`, and `compose.yaml` sources, and surfaces the concrete shapes the planner needs.

The five new artifacts are: `src/Keeper/Keeper.csproj`, `src/Keeper/Program.cs`, `src/Keeper/Dockerfile`, a placeholder `IConsumer<T>` + `ConsumerDefinition<T>` pair, and a new `keeper` tier in `compose.yaml`. Plus three test/wiring touch-points: SK_P.sln registration, a hermetic round-robin proof test in `tests/BaseApi.Tests/`, and (optionally) a `ComposeYamlFacts` assertion + a `KeeperQueues` const in `Messaging.Contracts`.

The single load-bearing correctness fact for the close gate: the placeholder MUST bind a **stable, durable shared queue** (`ConsumerDefinition.EndpointName`, plain `AddConsumer<,>()`, NO `InstanceId`/`Temporary`). This is what makes (a) RabbitMQ round-robin across replicas the default reality [KEEP-02], and (b) the queue appear in BOTH the BEFORE and AFTER `rabbitmqctl list_queues` snapshots → **net-zero triple-SHA** (an auto-delete/temporary queue would churn the rabbitmq SHA and break the close gate in Phase 38).

**Primary recommendation:** Clone `Orchestrator` minus the scheduler/L1/hydration/metrics block and minus the `StartupCompletionService` removal; bind a stable shared queue via a `KeeperQueues` const placeholder; express multi-replica in compose via `deploy.replicas: 2` + drop `container_name`; prove KEEP-02 hermetically with an `AddMassTransitTestHarness` round-robin test mirroring `FanOutBroadcastTests.cs` (inverted: ONE consumer type on ONE shared endpoint, assert total consumed == 1, not 2).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Host shell / composition root | Keeper console (Generic-Host) | BaseConsole.Core | `Host.CreateApplicationBuilder` worker, NOT WebApplication; infra flows from base library |
| Observability (metrics-only OTel) | BaseConsole.Core | — | `AddBaseConsoleObservability` — no tracer (Pitfall: T-18-07) |
| Redis soft-dep + health probes | BaseConsole.Core | — | `AddBaseConsole` wires soft-dep multiplexer + embedded Kestrel `/health/{live,ready,startup}` |
| Bus + correlation pipeline | BaseConsole.Core | Keeper supplies consumers | `AddBaseConsoleMessaging(cfg, x => ...)` — base = infra, concrete = consumer lambda |
| Competing-consumer queue (round-robin) | Keeper placeholder consumer | RabbitMQ broker | Stable shared `EndpointName`; broker round-robins one message to one replica |
| Multi-replica scaling | Docker Compose `deploy.replicas` | — | `docker compose up` brings up 2 replicas; no `container_name` (named containers can't scale) |
| Readiness gate | BaseConsole.Core `StartupCompletionService` | `IStartupGate` | Keeper KEEPS the default — readiness flips on host-start (D-06); no hydration to gate on |
| Retry/DLQ policy | `RetryOptions` (`Messaging.Contracts`) | placeholder `ConsumerDefinition` | `Immediate(Limit)` from `"Retry"` section (DLQ-04 pattern) |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus abstraction + consumer/endpoint binding | [VERIFIED: Directory.Packages.props:137] Last Apache-2.0 line; CPM-pinned, do NOT add `Version=` |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport | [VERIFIED: Directory.Packages.props:138] CPM-pinned |
| BaseConsole.Core | project ref | Infra spine (observability/Redis/health/bus) | [VERIFIED: src/BaseConsole.Core/] Carries `FrameworkReference Microsoft.AspNetCore.App` transitively |
| Messaging.Contracts | project ref | `RetryOptions`, queue-name consts, `ICorrelated` | [VERIFIED: src/Messaging.Contracts/] Leaf assembly, no infra deps |
| .NET SDK | 8.0.421 | Target framework `net8.0` | [VERIFIED: global.json] `rollForward: latestFeature` |

### Supporting (NONE beyond the above)
Keeper's reference closure is **leaner than Orchestrator's** — it does NOT take Quartz.Extensions.Hosting or Cronos (no scheduling). [VERIFIED: 34-CONTEXT.md D-07; Orchestrator.csproj:37-42 shows those two are scheduler-only].

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Stable `EndpointName` competing-consumer | `InstanceId`+`Temporary` fan-out | WRONG for Keeper — fan-out broadcasts to all replicas; Keeper needs ONE replica per message (D-02). Also churns the rabbitmq close-gate SHA. |
| `KeeperQueues` const in Messaging.Contracts | local const in Keeper | Both valid (Claude's discretion D-08). Const-in-Contracts is the house precedent (`OrchestratorQueues`/`ProcessorQueues`). |

**Installation:** No `dotnet add package` calls — both NuGet refs are CPM-pinned; the csproj lists `<PackageReference Include="..." />` with NO `Version=`.

**Version verification:** [VERIFIED: Directory.Packages.props] MassTransit + MassTransit.RabbitMQ both pinned at `8.5.5`. Do not bump — 8.5.5 is the last Apache-2.0 release (comment at line 133).

## Architecture Patterns

### System Architecture Diagram (Phase 34 scope)

```
                      docker compose up
                            │
              ┌─────────────┴─────────────┐
              │  keeper tier (replicas:2)  │   ← NO container_name (scalable)
              │                            │
        ┌─────▼─────┐              ┌───────▼───────┐
        │ keeper #1 │              │   keeper #2   │
        │ Generic-  │              │   Generic-    │
        │ Host      │              │   Host        │
        └─────┬─────┘              └───────┬───────┘
              │  both AddConsumer<Placeholder, PlaceholderDefinition>()
              │  bound to SAME stable EndpointName (KeeperQueues.X)
              ▼                            ▼
        ┌───────────────────────────────────────────┐
        │  RabbitMQ queue "keeper-..." (durable)     │  ← ONE shared queue
        │  competing-consumer → round-robins 1 msg   │     (NOT per-replica fan-out)
        │  to exactly ONE replica                    │
        └───────────────────────────────────────────┘

   Per replica, independent of the bus:
        embedded Kestrel :8083  →  /health/live  (self-only, always Healthy)
                                   /health/ready  (BusReadyHealthCheck — bus state)
                                   /health/startup(StartupHealthCheck — gate)
        compose healthcheck: wget --spider http://localhost:8083/health/ready
```

Data flow this phase is topology-only: no real `Fault<T>` arrives (the placeholder message has no producer in-stack). The placeholder exists to *declare* the shared queue so round-robin is live-verifiable [KEEP-02], not to carry recovery behavior.

### Recommended Project Structure
```
src/Keeper/
├── Keeper.csproj            # OutputType=Exe, refs BaseConsole.Core + Messaging.Contracts only
├── Program.cs               # thin composition root (mirror Orchestrator minus scheduler/L1/metrics/SCS-removal)
├── Dockerfile               # multi-stage sdk:8.0 → aspnet:8.0-bookworm-slim (+wget), port 8083
├── appsettings.json         # Service/RabbitMq/Redis/ConsoleHealth/Retry (mirror Orchestrator)
└── Consumers/
    ├── PlaceholderConsumer.cs            # throwaway no-op IConsumer<T> (log-only)
    └── PlaceholderConsumerDefinition.cs  # stable EndpointName + UseMessageRetry(Immediate(Limit))
```
(Optional: `Messaging.Contracts/KeeperQueues.cs` const + placeholder message type — planner's call per D-08.)

### Pattern 1: Thin-shell composition root (Program.cs)
**What:** Generic-Host worker; base library supplies all infra; concrete supplies only the consumer lambda.
**When to use:** Every console in this repo (Orchestrator, Processor.Sample, now Keeper).
**Example (the exact target shape — Orchestrator MINUS the deferred blocks):**
```csharp
// Source: src/Orchestrator/Program.cs (lines 21-50) — mirror minus scheduler/L1/metrics + KEEP StartupCompletionService
using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Keeper.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel (no tracer)
builder.Services.AddBaseConsole(builder.Configuration);       // Redis soft-dep + embedded health + StartupCompletionService (KEPT — D-06)

builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));   // DLQ-04 shared policy (D-09)

builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x => x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>());  // plain AddConsumer — NO .Endpoint(InstanceId/Temporary)

var host = builder.Build();
await host.RunAsync();
```
**Critical difference from Orchestrator:** Keeper does NOT include the `foreach(...) builder.Services.Remove(StartupCompletionService)` block (Orchestrator/Program.cs:78-83). That removal exists ONLY to re-gate readiness on L1 hydration (D-06). Keeper has no hydration → keeps the default `StartupCompletionService`, so `/health/ready` (the `BusReadyHealthCheck`) flips when the bus starts. [VERIFIED: src/BaseConsole.Core/Health/StartupCompletionService.cs — `StartAsync` calls `gate.MarkReady()`; src/Orchestrator/Program.cs:76-83 — the removal block].

### Pattern 2: Stable shared competing-consumer (the D-02 source)
**What:** `ConsumerDefinition<T>` sets `EndpointName` to a stable const; `ConfigureConsumer` applies `UseMessageRetry(Immediate(Limit))`. Registered via plain `AddConsumer<C, D>()` with NO `.Endpoint(e => { e.InstanceId; e.Temporary; })`.
**When to use:** Work that must be load-balanced (consumed exactly once across the replica set), NOT broadcast.
**Example:**
```csharp
// Source: src/Orchestrator/Consumers/ResultConsumerDefinition.cs (the D-02 template)
public sealed class PlaceholderConsumerDefinition : ConsumerDefinition<PlaceholderConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;
    public PlaceholderConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = KeeperQueues.FaultRecovery;   // e.g. "keeper-fault-recovery" — stable, shared, durable
    }
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PlaceholderConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));   // DLQ-04 / D-09
}
```
**Placeholder consumer body (throwaway — D-03):**
```csharp
public sealed class PlaceholderConsumer(ILogger<PlaceholderConsumer> logger)
    : IConsumer<KeeperPlaceholder>   // message type: a trivial record implementing ICorrelated
{
    public Task Consume(ConsumeContext<KeeperPlaceholder> context)
    {
        logger.LogInformation("Keeper placeholder consumed (topology proof only)");
        return Task.CompletedTask;
    }
}
```
**Message type:** make `KeeperPlaceholder` a `record(...) : ICorrelated` carrying `Guid CorrelationId` so the bus-wide `InboundCorrelationConsumeFilter` reads it from the body cleanly (matches `ExecutionResult`/`StartOrchestration` shape). [VERIFIED: src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:35 reads `context.Message as ICorrelated`; src/Messaging.Contracts/ICorrelated.cs].

### Anti-Patterns to Avoid
- **`InstanceId`+`Temporary` endpoint on the Keeper consumer:** That is the Orchestrator Start/Stop *fan-out* shape (Program.cs:41-43) — it broadcasts to every replica (WRONG for round-robin) AND creates auto-delete queues that churn the rabbitmq close-gate SHA. Keeper wants the **inverse** (plain `AddConsumer`).
- **`container_name: sk-keeper` on a replicated tier:** A named container cannot be scaled — `deploy.replicas: 2` + `container_name` is a compose error. Drop `container_name` (D-04).
- **Redeclaring common MSBuild props** (`TargetFramework`, `Nullable`, `TreatWarningsAsErrors`, etc.) in Keeper.csproj — they inherit from `Directory.Build.props`. [VERIFIED: Directory.Build.props:28-42].
- **Adding `Version=` to PackageReference** — CPM forbids it; versions live in `Directory.Packages.props`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Health probes (`/live`/`/ready`/`/startup`) | Custom Kestrel listener | `AddBaseConsole` → `EmbeddedHealthEndpointService` | [VERIFIED] Already built, dual-container, bus-aware, port-configurable via `ConsoleHealth:Port` |
| Readiness latch | Custom flag | `IStartupGate` + `StartupCompletionService` | [VERIFIED] Thread-safe one-shot latch already in base |
| Bus + correlation/log-scope filters | Manual filter wiring | `AddBaseConsoleMessaging` | [VERIFIED] Registers all 4 correlation filters bus-wide |
| Metrics-only OTel | Manual OTel SDK setup | `AddBaseConsoleObservability` | [VERIFIED] No-tracer metrics pipeline (T-18-07) |
| Round-robin queue topology | Manual exchange/queue declaration | MassTransit `ConsumerDefinition.EndpointName` + plain `AddConsumer` | [VERIFIED: ResultConsumerDefinition] MassTransit declares + binds the durable queue |
| Retry budget | Hard-coded `Immediate(3)` | `Configure<RetryOptions>(GetSection("Retry"))` | [VERIFIED] Single source of truth (DLQ-04) |

**Key insight:** Keeper's `Program.cs` is ~6 meaningful lines + one placeholder consumer pair. The entire infra spine is inherited. Any custom infra in the concrete console is a smell.

## Common Pitfalls

### Pitfall 1: Temporary/auto-delete queue churns the close-gate rabbitmq SHA
**What goes wrong:** Using the `InstanceId`/`Temporary` endpoint shape creates `keeper-{instanceId}` queues that are auto-deleted on disconnect. They appear/disappear in `rabbitmqctl list_queues name` → the Phase-38 triple-SHA (BEFORE vs AFTER) flips RED.
**Why it happens:** Copying the Orchestrator Start/Stop fan-out shape instead of the ResultConsumer competing-consumer shape.
**How to avoid:** Plain `AddConsumer<C, D>()` + stable `EndpointName` → ONE durable queue present at steady state, in BOTH snapshots → net-zero. [VERIFIED: scripts/phase-33-close.ps1:21-22,189-282 — `rabbitmqctl -q list_queues name | sort | SHA-256`; the steady-state dispatch queue is part of both snapshots].
**Warning signs:** Queue name contains a GUID/instance suffix; `e.Temporary = true` anywhere in Keeper.

### Pitfall 2: aspnet runtime image ships no wget/curl
**What goes wrong:** The compose `["CMD","wget","--spider",...]` healthcheck cannot exec ("wget: not found") and the container is marked unhealthy despite a healthy app.
**Why it happens:** `aspnet:8.0-bookworm-slim` has neither wget nor curl.
**How to avoid:** `RUN apt-get install -y --no-install-recommends wget` as root BEFORE `USER app`. [VERIFIED: src/Orchestrator/Dockerfile:28-31; src/Processor.Sample/Dockerfile:31-33].

### Pitfall 3: aspnet (not runtime) base image is mandatory
**What goes wrong:** Using `runtime:8.0` fails at startup — the embedded Kestrel health listener needs the ASP.NET Core shared framework.
**Why it happens:** `BaseConsole.Core` carries `FrameworkReference Microsoft.AspNetCore.App`.
**How to avoid:** `FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime`. [VERIFIED: src/Orchestrator/Dockerfile:21].

### Pitfall 4: appsettings.json not copied by the worker SDK
**What goes wrong:** Console boots but `cfg.Require("RabbitMq:Host")` throws — appsettings.json isn't next to the assembly.
**Why it happens:** `Microsoft.NET.Sdk` (worker), unlike `.Web`, does NOT copy appsettings.json by default.
**How to avoid:** `<Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />`. [VERIFIED: src/Orchestrator/Orchestrator.csproj:45-50].

### Pitfall 5: CS1591 doc-comment warnings under TreatWarningsAsErrors
**What goes wrong:** With `GenerateDocumentationFile=true` and `TreatWarningsAsErrors=true`, missing XML doc comments (CS1591) fail the build.
**Why it happens:** Repo-wide warnings-as-errors (Directory.Build.props:35).
**How to avoid:** Either omit `GenerateDocumentationFile` OR mirror Orchestrator exactly: `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<NoWarn>$(NoWarn);CS1591</NoWarn>`. [VERIFIED: src/Orchestrator/Orchestrator.csproj:30-31]. Recommend mirroring Orchestrator for consistency.

### Pitfall 6: OTLP endpoint env var, not appsettings key
**What goes wrong:** Keeper logs/metrics never reach the collector if you rely on `appsettings OpenTelemetry:Endpoint`.
**Why it happens:** That appsettings key is **dead config** — the exporter reads the standard `OTEL_EXPORTER_OTLP_ENDPOINT` env var, set in compose.
**How to avoid:** Set `OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"` in the keeper compose env block. [VERIFIED: compose.yaml:202-208; matches user's MEMORY.md note].

## Code Examples

### Compose tier (the D-04/D-05 divergence — mirror orchestrator, drop container_name, add deploy.replicas)
```yaml
# Source: compose.yaml orchestrator: (185-214) — MIRROR with the two D-04 changes
  keeper:
    build:
      context: .
      dockerfile: src/Keeper/Dockerfile
    # NO container_name (D-04 — named containers cannot scale)
    deploy:
      replicas: 2                      # D-04 — `docker compose up` brings up 2 replicas
    restart: unless-stopped
    depends_on:
      rabbitmq:
        condition: service_healthy
      redis:
        condition: service_healthy
      # NO baseapi-service dependency (Keeper resolves no identity over the WebApi — D-05)
    environment:
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8083/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
```
**No host port publishing:** orchestrator/processor publish NO host port (only baseapi maps 8080). Keeper follows — 8083 is container-internal; the healthcheck hits `localhost:8083` *inside* the container. [VERIFIED: compose.yaml orchestrator/processor blocks have no `ports:`]. Two replicas on the same internal port is fine — no host-port collision because nothing is published.

### Dockerfile (mirror Orchestrator, swap target + port)
```dockerfile
# Source: src/Orchestrator/Dockerfile — swap COPY list to Keeper's closure, port 8083
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/Keeper/Keeper.csproj", "src/Keeper/"]
RUN dotnet restore "src/Keeper/Keeper.csproj"
COPY src/ src/
RUN dotnet publish "src/Keeper/Keeper.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /publish .
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*
USER app
ENV ASPNETCORE_URLS=http://+:8083
EXPOSE 8083
ENTRYPOINT ["dotnet", "Keeper.dll"]
```
**COPY restore-cache list (D-05):** exactly Messaging.Contracts + BaseConsole.Core + Keeper csproj — NO BaseProcessor.Core (that's a Processor-only dependency), NO BaseApi.*. [VERIFIED: Keeper.csproj refs per D-07; Processor.Sample/Dockerfile:17-20 includes BaseProcessor.Core which Keeper does not].

### Hermetic round-robin proof for KEEP-02 (inverse of FanOutBroadcastTests)
```csharp
// Source: INVERT tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs
// FanOut proves broadcast (count==2 across two distinct-InstanceId endpoints).
// Round-robin proves load-balance: ONE shared endpoint, ONE consumer type, total consumed == 1.
[Fact]
public async Task One_Publish_Is_Delivered_To_Exactly_One_Replica_On_Shared_Endpoint()
{
    var ct = TestContext.Current.CancellationToken;
    await using var provider = new ServiceCollection()
        .AddLogging()
        .AddMassTransitTestHarness(x =>
        {
            // Plain AddConsumer + a fixed EndpointName (no InstanceId/Temporary) => one shared queue.
            x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>();
            x.UsingInMemory((c, cfg) => cfg.ConfigureEndpoints(c));
        })
        .BuildServiceProvider(true);
    var harness = provider.GetRequiredService<ITestHarness>();
    await harness.Start();
    try
    {
        await harness.Bus.Publish(new KeeperPlaceholder { CorrelationId = NewId.NextGuid() }, ct);
        var consumer = harness.GetConsumerHarness<PlaceholderConsumer>();
        Assert.True(await consumer.Consumed.Any<KeeperPlaceholder>(ct));
        // LOAD-BALANCE: exactly ONE consume (a single shared endpoint cannot double-deliver).
        Assert.Equal(1, consumer.Consumed.Select<KeeperPlaceholder>(ct).Count());
    }
    finally { await harness.Stop(ct); }
}
```
**Honest scope note:** the in-memory harness has a single endpoint instance, so this test proves the *binding shape* (shared endpoint, not fan-out — count==1 not N) and that the placeholder consumes. True multi-replica RabbitMQ round-robin distribution is only observable against the **live compose stack** (`docker compose up --no-recreate` with `deploy.replicas: 2`, publish N messages, observe split across replica logs). The hermetic test is the CI guard; the live stack is the KEEP-02 acceptance proof (Phase 38 E2E territory, but a manual smoke is appropriate here). [VERIFIED: FanOutBroadcastTests.cs:43-98 uses the same single-process harness pattern].

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `version: "3.x"` compose key | version-less Compose Spec | this repo | [VERIFIED: compose.yaml has no `version:` key] — `deploy:` is honored by `docker compose up` (Compose Spec), NOT swarm-only |
| `docker compose --scale` manual | `deploy.replicas: N` in file | D-04 | Round-robin is the default `up` reality, no manual flag |

**`deploy:` + `docker compose up` (the D-04 verification):** In legacy Compose v1, `deploy:` was swarm-only and ignored by `up`. In the modern Compose Spec (Docker Compose v2, what this repo uses — version-less file, `# Docker Compose v2 declaration` header at compose.yaml:1), `deploy.replicas` IS honored by `docker compose up`. [VERIFIED: compose.yaml:1 header declares Compose v2; CITED: Compose Spec — `deploy.replicas` supported by `docker compose up` since Compose v2]. Combined with NO `container_name`, `docker compose up` starts 2 keeper replicas. **Confidence: HIGH** on the no-container_name requirement (a named service cannot scale — hard compose rule); **MEDIUM-HIGH** on `deploy.replicas` being honored by plain `up` in the operator's exact Docker version — recommend the planner add a manual verification step (`docker compose up -d keeper && docker compose ps` shows `keeper-1`/`keeper-2`).

## Project Constraints (from CLAUDE.md / STATE.md / Directory.Build.props)

- **No `./CLAUDE.md`** found in the working directory. Constraints are sourced from STATE.md/PROJECT.md (per the objective) and the build props.
- **.NET 8.0.421 pinned** (global.json, `rollForward: latestFeature`). [VERIFIED]
- **CPM** — NO `Version=` on any `PackageReference`; versions in `Directory.Packages.props`. [VERIFIED]
- **Directory.Build.props inherited** — do NOT redeclare `TargetFramework`/`Nullable`/`ImplicitUsings`/`LangVersion`/`AnalysisMode`/`EnforceCodeStyleInBuild`/`TreatWarningsAsErrors`. [VERIFIED: Directory.Build.props:28-42]
- **Release + Debug 0-warning** required (TreatWarningsAsErrors=true). Mirror Orchestrator's `GenerateDocumentationFile`+`NoWarn;CS1591`. [VERIFIED]
- **MassTransit 8.5.5 CPM-pinned** — do not bump (last Apache-2.0). [VERIFIED]
- **Close gate triple-SHA net-zero** (psql/redis/rabbitmq) — Keeper's placeholder queue MUST be a stable/durable shared queue (NOT auto-delete/temporary) so it's in BOTH rabbitmq snapshots → net-zero. [VERIFIED: scripts/phase-33-close.ps1]

## Validation Architecture

> Nyquist validation enabled (no `workflow.nyquist_validation: false` found). The sole test project is `tests/BaseApi.Tests` (xUnit v3); console/messaging tests live under `tests/BaseApi.Tests/Console/` and `tests/BaseApi.Tests/Orchestrator/`.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (`TestContext.Current.CancellationToken` idiom) + MassTransit.Testing harness |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (the only test project) |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"` |
| Full suite command | `dotnet test SK_P.sln` |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| KEEP-01 | Keeper console exists on BaseConsole.Core, mirrors Orchestrator; metrics-only OTel, no BaseApi/EF refs | build + reflection fact | `dotnet build src/Keeper -c Release` (+ `-c Debug`); ref-firewall reflection test | ❌ Wave 0 |
| KEEP-01 | `Program.cs` boots with three-call seam; bus resolvable; readiness flips on bus-start (SCS kept) | hermetic host-boot | mirror `ConsoleHostBootTests` for a Keeper test host | ❌ Wave 0 |
| KEEP-02 | Shared competing-consumer endpoint (NOT fan-out) — one publish → one consume | hermetic harness | round-robin test (inverse of `FanOutBroadcastTests`) | ❌ Wave 0 |
| KEEP-02 | Live multi-replica round-robin across 2 replicas | manual live-stack smoke | `docker compose up -d keeper; docker compose ps` + publish + log split | ❌ manual |
| KEEP-03 | Builds + containerizes (multi-stage Dockerfile) | build + docker build | `docker compose build keeper` | ❌ Wave 0 |
| KEEP-03 | Joins compose stack as healthy tier | compose-shape fact + live health | `ComposeYamlFacts` assertion + `docker compose up`/`ps` health | ❌ Wave 0 |
| DLQ-04 | Placeholder binds `Immediate(Limit)` from shared `RetryOptions` | covered by reading definition / harness | (pattern assertion; full DLQ routing is Phase 36) | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet build src/Keeper -c Debug` + `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"`
- **Per wave merge:** `dotnet test SK_P.sln`
- **Phase gate:** Full suite green + `dotnet build -c Release` 0-warning + `dotnet build -c Debug` 0-warning + `docker compose build keeper` + live `docker compose up` health-ready, before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` — covers KEEP-02 (hermetic round-robin)
- [ ] `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs` (+ a Keeper-specific `ConsoleTestHostFixture` variant registering the placeholder consumer) — covers KEEP-01 boot/readiness
- [ ] `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` — mirror `ConsoleDependencyFirewallTests` anchored on a Keeper type; asserts no BaseApi.*/EF/Npgsql/Quartz/Cronos refs (KEEP-01 reference closure)
- [ ] `ComposeYamlFacts` new assertions: keeper tier present, `deploy.replicas: 2`, NO `container_name` for keeper, `dockerfile: src/Keeper/Dockerfile`, 8083 health (KEEP-03)
- [ ] No framework install needed — xUnit v3 + MassTransit.Testing already present in `BaseApi.Tests`.

*(Note: there is no separate `Orchestrator.Tests`/`Keeper.Tests` project — all console tests live in `BaseApi.Tests`. Keeper tests go there, in a new `Keeper/` folder, mirroring the existing `Orchestrator/` and `Console/` folders.)*

## Security Domain

> `security_enforcement` config not located in this session; including a lightweight pass since Keeper joins a message bus. This phase introduces NO new external attack surface (no published host port, no HTTP API, no new secrets).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Keeper authenticates to RabbitMQ via guest/guest dev creds from compose env (never baked into image) [VERIFIED: compose env blocks] |
| V3 Session Management | no | No sessions |
| V4 Access Control | no | No external endpoints (health probes are internal-only, status-only bodies) |
| V5 Input Validation | minimal | Inbound correlation id treated as opaque untrusted text (T-18-04) — inherited from `InboundCorrelationConsumeFilter` |
| V6 Cryptography | no | None hand-rolled |
| V7 Error/Logging | yes | Health bodies carry status only — no connection strings / stack traces (T-18-08, inherited) |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Secrets in image | Information Disclosure | guest/guest from compose env, never the image (inherited convention) |
| Log injection via correlation id | Tampering | Id placed only as scope VALUE under fixed key, never interpolated into a template (T-18-04, inherited filter) |
| Dependency blip flips liveness → pod restart | Denial of Service | `/health/live` is self-only (never Redis/RMQ) — inherited from `EmbeddedHealthEndpointService` (CONSOLE-HEALTH-02) |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `deploy.replicas` is honored by `docker compose up` (not swarm-only) in the operator's exact Docker version | State of the Art / Compose | LOW-MED — if the installed Docker is unusually old/v1, `up` ignores `deploy` and starts 1 replica. Mitigation: planner adds `docker compose ps` verification step; fallback is documented `--scale keeper=2`. The no-`container_name` requirement is independently correct regardless. |
| A2 | The hermetic in-memory harness proves *binding shape* but NOT true cross-replica distribution | Validation / Code Examples | LOW — flagged explicitly; live-stack manual smoke is the real KEEP-02 proof. |
| A3 | DLQ-04 retry-binding belongs in Phase 34 (per CONTEXT D-09) despite ROADMAP mapping DLQ-04 to Phase 36 | User Constraints / Stack | LOW — D-09 is explicit that the *pattern* (binding `RetryOptions` + `UseMessageRetry(Immediate(Limit))` on the placeholder definition) lands now; the full two-DLQ routing is Phase 36. Both can be true. Planner should treat D-09 as the authority for Phase 34 scope and NOT check the DLQ-04 requirement box (that's Phase 36). |

## Open Questions

1. **`KeeperQueues` const location + placeholder message location**
   - What we know: house precedent is const-in-`Messaging.Contracts` (`OrchestratorQueues`/`ProcessorQueues`); the message type for `ExecutionResult`/`StartOrchestration` also live in Contracts.
   - What's unclear: whether a throwaway placeholder (replaced wholesale in Phase 35) warrants a Contracts entry or should stay local to Keeper to avoid polluting the shared contract assembly.
   - Recommendation: Put the `KeeperQueues.FaultRecovery` const in `Messaging.Contracts` (Phase 35's real consumers reuse the SAME endpoint name, so the const survives). Keep the throwaway *message type* (`KeeperPlaceholder`) LOCAL to Keeper — it's deleted in Phase 35 when the real `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` arrive. This minimizes Contracts churn.

2. **Exact stable queue name**
   - What we know: Phase 35 swaps the placeholder for the two real Fault consumers "on the same competing-consumer endpoint pattern" (D-01). Phase 36 introduces `keeper-dlq` (DLQ-2).
   - What's unclear: whether the single shared worklist queue should be named to anticipate Phase 35 (e.g. `keeper-fault-recovery`) vs a neutral placeholder name.
   - Recommendation: name it for its enduring role (`keeper-fault-recovery` or similar) since the endpoint persists into Phase 35; avoid "placeholder" in the queue name. Planner's discretion (D-08).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | ✓ (assumed — repo builds today) | 8.0.421 | — |
| Docker + Compose v2 | KEEP-03 containerize/compose | ✓ (assumed — close gates run docker exec) | v2 (version-less compose) | — |
| RabbitMQ (compose) | KEEP-02 round-robin | ✓ (compose service) | per compose | — |
| Redis (compose) | soft-dep health | ✓ (compose service) | 7.4.x-alpine | soft-dep (boots without) |

**No missing dependencies identified** — Keeper reuses the exact toolchain + compose services the Orchestrator/Processor tiers already use. (Availability marked "assumed" because this research session did not execute build/docker commands; the repo's shipped close gates confirm the toolchain is present.)

## Sources

### Primary (HIGH confidence — in-repo source, this session)
- `src/Orchestrator/Program.cs`, `Orchestrator.csproj`, `Dockerfile`, `appsettings.json` — mirror template
- `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` + `ResultConsumer.cs` — D-02 competing-consumer template
- `src/Processor.Sample/Dockerfile` — second Dockerfile analog (BaseProcessor.Core inclusion contrast)
- `src/BaseConsole.Core/DependencyInjection/{BaseConsole,ConsoleHealth,Messaging,BaseConsoleObservability}ServiceCollectionExtensions.cs` — infra surface
- `src/BaseConsole.Core/Health/{StartupCompletionService,StartupHealthCheck,IStartupGate,BusReadyHealthCheck,EmbeddedHealthEndpointService}.cs` — readiness gate (D-06)
- `src/Messaging.Contracts/{OrchestratorQueues,ProcessorQueues,ICorrelated}.cs` + `Configuration/RetryOptions.cs` — queue-const precedent + retry binding (D-09)
- `compose.yaml` (orchestrator/processor tiers, lines 185-252; version-less Compose v2 header) — compose mirror (D-04/D-05)
- `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — compose-shape assertion pattern
- `tests/BaseApi.Tests/Console/{ConsoleTestHostFixture,ConsoleHostBootTests,ConsoleDependencyFirewallTests,ConsoleObservabilityTests}.cs` — hermetic console test conventions
- `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs` — the harness pattern to INVERT for KEEP-02 round-robin
- `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `SK_P.sln` — build constraints + project registration
- `scripts/phase-33-close.ps1` — triple-SHA mechanics (durable-vs-temporary queue concern)
- `.planning/REQUIREMENTS.md` (KEEP-01/02/03, DLQ-04), `.planning/ROADMAP.md` (Phase 34) — locked spec

### Secondary (MEDIUM confidence)
- Compose Spec `deploy.replicas` honored by `docker compose up` v2 — cross-referenced with the version-less compose header (A1).

### Tertiary (LOW confidence)
- None — this is a closed-world console-mirror; all findings are repo-grounded.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every ref/version verified against Directory.Packages.props + existing Orchestrator csproj
- Architecture: HIGH — Keeper is a structural clone of a shipped console; every pattern has a working in-repo source
- Pitfalls: HIGH — each pitfall is drawn from a fix-comment in the actual Orchestrator/Processor Dockerfiles, close-gate script, or test
- Compose `deploy.replicas` behavior: MEDIUM-HIGH — verified against Compose Spec + version-less header; flagged A1 for a `docker compose ps` confirmation step

**Research date:** 2026-06-05
**Valid until:** stable (~30 days) — repo-internal mirror; only drifts if Orchestrator/BaseConsole.Core is refactored
