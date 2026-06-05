# Phase 34: Keeper Console Foundation - Pattern Map

**Mapped:** 2026-06-05
**Files analyzed:** 12 (8 CREATE, 4 MODIFY)
**Analogs found:** 12 / 12 (all exact — this is a console-MIRROR phase)

This is a **clone-and-strip** phase. Every new Keeper artifact has a byte-near analog in the shipped `src/Orchestrator/` console. The dominant action is "copy the analog verbatim, then apply the listed deltas." Excerpts below are the exact source text to replicate.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Keeper/Keeper.csproj` | config (csproj) | n/a | `src/Orchestrator/Orchestrator.csproj` | exact (leaner: drop Quartz+Cronos) |
| `src/Keeper/Program.cs` | composition-root | event-driven (bus) | `src/Orchestrator/Program.cs` | exact (minus scheduler/L1/metrics + minus SCS-removal) |
| `src/Keeper/Dockerfile` | config (build) | n/a | `src/Orchestrator/Dockerfile` | exact (swap COPY/publish target, port 8083) |
| `src/Keeper/appsettings.json` | config | n/a | `src/Orchestrator/appsettings.json` | exact (drop InstanceId concept; port 8083) |
| `src/Keeper/Consumers/PlaceholderConsumer.cs` | consumer | event-driven (competing-consumer) | `src/Orchestrator/Consumers/ResultConsumer.cs` | role-match (no-op body, NOT the DAG logic) |
| `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` | consumer-definition | event-driven (endpoint binding) | `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` | exact (the D-02 template) |
| `src/Keeper/Consumers/KeeperPlaceholder.cs` (message, LOCAL) | contract (message) | event-driven | `src/Messaging.Contracts/StartOrchestration.cs` | exact-shape (record : ICorrelated) |
| `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` | test | event-driven | `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs` | exact-inverse (count==1, not 2) |
| `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs` (+ Keeper fixture) | test | n/a | `tests/BaseApi.Tests/Console/ConsoleHostBootTests.cs` + `ConsoleTestHostFixture.cs` | exact |
| `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` | test | n/a | `tests/BaseApi.Tests/Console/ConsoleDependencyFirewallTests.cs` | exact (anchor on Keeper type; add Quartz/Cronos prefixes) |
| `SK_P.sln` (MODIFY) | config | n/a | the `Orchestrator` / `Processor.Sample` project entries | exact |
| `compose.yaml` (MODIFY) | config | n/a | the `orchestrator:` tier (lines 185-214) | exact (minus container_name, plus deploy.replicas:2) |
| `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` (MODIFY) | test | n/a | the `processor-sample` facts (lines 133-159) | exact |
| `src/Messaging.Contracts/KeeperQueues.cs` (MODIFY/CREATE, optional) | contract (const) | n/a | `src/Messaging.Contracts/OrchestratorQueues.cs` | exact |

---

## Pattern Assignments

### `src/Keeper/Keeper.csproj` (config, csproj)

**Analog:** `src/Orchestrator/Orchestrator.csproj`

**Copy verbatim, change these properties** (analog lines 24-32):
```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <RootNamespace>Keeper</RootNamespace>       <!-- was Orchestrator -->
  <AssemblyName>Keeper</AssemblyName>         <!-- was Orchestrator -->
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <NoWarn>$(NoWarn);CS1591</NoWarn>           <!-- Pitfall 5: silence CS1591 under TreatWarningsAsErrors -->
</PropertyGroup>
```

**PackageReference ItemGroup — DROP Quartz + Cronos** (analog lines 34-43 has 4 refs; Keeper keeps only 2):
```xml
<ItemGroup>
  <PackageReference Include="MassTransit" />
  <PackageReference Include="MassTransit.RabbitMQ" />
  <!-- DELETE: Quartz.Extensions.Hosting (analog line 39) — Keeper does not schedule (D-07) -->
  <!-- DELETE: Cronos (analog line 42) — Keeper has no cron math (D-07) -->
</ItemGroup>
```

**Content + ProjectReference ItemGroups — copy verbatim** (analog lines 45-55):
```xml
<ItemGroup>
  <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
<ItemGroup>
  <ProjectReference Include="..\BaseConsole.Core\BaseConsole.Core.csproj" />
  <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
</ItemGroup>
```

**Constraints (RESEARCH Pitfalls 3,4,5; D-07):** NO `Version=` on PackageReferences (CPM). NO redeclaration of `TargetFramework`/`Nullable`/`TreatWarningsAsErrors` (inherited from `Directory.Build.props`). NO `BaseApi.*` / `BaseProcessor.Core` reference. `Sdk="Microsoft.NET.Sdk"` (NOT `.Web`).

---

### `src/Keeper/Program.cs` (composition-root, event-driven)

**Analog:** `src/Orchestrator/Program.cs`

Keeper is the Orchestrator's `Program.cs` with **two deletions**: (1) the entire runtime-wiring block (analog lines 52-74: Quartz/L1/scheduler/lifecycle/dispatch/metrics/hydration), and (2) the `StartupCompletionService`-removal `foreach` (analog lines 76-83). **Keep** the three-call seam + the `Configure<RetryOptions>` line.

**Keep this — the kept seam** (analog lines 1-29, stripped of Orchestrator-only usings):
```csharp
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
```

**Change this — the AddBaseConsoleMessaging lambda** (analog lines 37-50 register 3 consumers with `.Endpoint(InstanceId/Temporary)` fan-out; Keeper registers ONE consumer with PLAIN AddConsumer — D-02):
```csharp
builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x => x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>());  // plain AddConsumer — NO .Endpoint(InstanceId/Temporary)

var host = builder.Build();
await host.RunAsync();
```

**DELETE — do NOT copy these from the analog:**
- Usings (analog lines 6, 9-15): `Microsoft.Extensions.DependencyInjection.Extensions`, `Orchestrator.Dispatch/Hydration/L1/Observability/Scheduling`, `OpenTelemetry.Metrics`, `Quartz`.
- The `instanceId` var + `.Endpoint(e => { e.InstanceId; e.Temporary = true; })` fan-out (analog lines 35, 41-43) — this is the **anti-pattern** for Keeper (RESEARCH Anti-Patterns; Pitfall 1: churns the close-gate rabbitmq SHA).
- The whole Quartz/L1/metrics/hydration block (analog lines 52-74).
- **CRITICAL (D-06/D-08):** the `foreach(... StartupCompletionService) builder.Services.Remove(d)` block (analog lines 76-83). Keeper KEEPS the default `StartupCompletionService` — readiness flips on bus-start, not hydration.

---

### `src/Keeper/Dockerfile` (config, build)

**Analog:** `src/Orchestrator/Dockerfile`

Copy verbatim; swap the COPY restore-cache list + publish target to Keeper's closure, and bump every `8081` → `8083`. The COPY closure is identical to Orchestrator's (Messaging.Contracts + BaseConsole.Core + the console csproj) — NO BaseProcessor.Core, NO BaseApi.* (D-05/D-07).

```dockerfile
# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/Keeper/Keeper.csproj", "src/Keeper/"]            # was Orchestrator
RUN dotnet restore "src/Keeper/Keeper.csproj"               # was Orchestrator
COPY src/ src/
RUN dotnet publish "src/Keeper/Keeper.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime   # aspnet, NOT runtime (Pitfall 3)
WORKDIR /app
COPY --from=build /publish .
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \           # Pitfall 2: slim image ships no wget
    && rm -rf /var/lib/apt/lists/*
USER app
ENV ASPNETCORE_URLS=http://+:8083                                  # was 8081
EXPOSE 8083                                                        # was 8081
ENTRYPOINT ["dotnet", "Keeper.dll"]                                # was Orchestrator.dll
```

---

### `src/Keeper/appsettings.json` (config)

**Analog:** `src/Orchestrator/appsettings.json`

Copy verbatim; change `Service:Name`, `ConsoleHealth:Port` (8081→8083). Keep `Retry` (D-09 binds it), `RabbitMq`, `ConnectionStrings:Redis`, `Logging`. `OpenTelemetry` key is dead config (Pitfall 6 — the live knob is the `OTEL_EXPORTER_OTLP_ENDPOINT` env var in compose) but Orchestrator keeps it, so mirror for consistency.

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.Hosting.Lifetime": "Information", "MassTransit": "Warning" }
  },
  "Service": { "Name": "keeper", "Version": "3.4.0" },          // was "orchestrator"
  "ConnectionStrings": { "Redis": "redis:6379,abortConnect=false,connectTimeout=5000" },
  "OpenTelemetry": { "Endpoint": "http://otel-collector:4317", "Protocol": "grpc" },
  "RabbitMq": { "Host": "rabbitmq", "Username": "guest", "Password": "guest" },
  "Retry": { "Limit": 3, "Strategy": "Immediate" },             // D-09 — bound by Program.cs
  "ConsoleHealth": { "Port": 8083 },                            // was 8081
  "AllowedHosts": "*"
}
```
Note: Orchestrator's appsettings has NO `Orchestrator:InstanceId` key (that comes from compose env, line 201). Keeper has no instance-id concept at all — omit entirely.

---

### `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` (consumer-definition, event-driven)

**Analog:** `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` — **this is the D-02 source.**

The competing-consumer binding shape, verbatim except the generic type, the `EndpointName` const, and the class name. The `IOptions<RetryOptions>` ctor + `UseMessageRetry(Immediate(Limit))` are the load-bearing DLQ-04/D-09 pattern.

**Analog (lines 22-43) — replicate this exact shape:**
```csharp
using MassTransit;
using Messaging.Contracts;                       // KeeperQueues const lives here (if D-08 puts it in Contracts)
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Keeper.Consumers;

public sealed class PlaceholderConsumerDefinition : ConsumerDefinition<PlaceholderConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public PlaceholderConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = KeeperQueues.FaultRecovery;   // stable, shared, DURABLE — e.g. "keeper-fault-recovery"
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PlaceholderConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));   // DLQ-04 / D-09
    }
}
```

**Load-bearing (RESEARCH Pitfall 1 / D-02):** `EndpointName` set to a stable const + plain `AddConsumer` in Program.cs = ONE durable shared queue = round-robin + net-zero close-gate SHA. NEVER add `.Endpoint(e => { e.InstanceId; e.Temporary = true; })` (that is the Orchestrator Start/Stop fan-out anti-pattern, Program.cs:41-43).

---

### `src/Keeper/Consumers/PlaceholderConsumer.cs` (consumer, event-driven)

**Analog:** `src/Orchestrator/Consumers/ResultConsumer.cs` (role only — the analog's 70-line DAG-advance body is NOT replicated; D-03 is a no-op log).

The analog's value is its `IConsumer<T>` primary-constructor + `ILogger` injection shape (analog lines 40-48). Body is throwaway:
```csharp
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Keeper.Consumers;

public sealed class PlaceholderConsumer(ILogger<PlaceholderConsumer> logger)
    : IConsumer<KeeperPlaceholder>
{
    public Task Consume(ConsumeContext<KeeperPlaceholder> context)
    {
        logger.LogInformation("Keeper placeholder consumed (topology proof only)");   // D-03 no-op
        return Task.CompletedTask;
    }
}
```

---

### `src/Keeper/Consumers/KeeperPlaceholder.cs` (message contract, LOCAL to Keeper)

**Analog:** `src/Messaging.Contracts/StartOrchestration.cs` (the `record : ICorrelated` body-correlation shape).

Per RESEARCH Open Question 1 recommendation: keep the throwaway **message type** LOCAL to Keeper (deleted in Phase 35); only the `KeeperQueues` const goes into Contracts. The type must implement `ICorrelated` so the bus-wide `InboundCorrelationConsumeFilter` reads the body cleanly (RESEARCH Pattern 2; verified against `src/Messaging.Contracts/ICorrelated.cs`).

**Analog shape (StartOrchestration.cs lines 4-7):**
```csharp
using Messaging.Contracts;   // ICorrelated

namespace Keeper.Consumers;

public sealed record KeeperPlaceholder : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
```

---

### `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` (test, KEEP-02)

**Analog:** `tests/BaseApi.Tests/Orchestrator/FanOutBroadcastTests.cs` — **INVERT it.**

FanOut proves broadcast (two distinct-InstanceId endpoints → count==2). Round-robin proves load-balance (ONE shared endpoint, ONE consumer type → count==1). Reuse the analog's `AddMassTransitTestHarness` + `UsingInMemory` + `Select<T>(ct).Count()` idiom (analog lines 48-91) exactly; flip the registration and the assertion.

**Analog registration (lines 50-58) — REPLACE the two-distinct-InstanceId fan-out with the single shared endpoint:**
```csharp
await using var provider = new ServiceCollection()
    .AddLogging()
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>();   // plain — shared EndpointName, NO InstanceId/Temporary
        x.UsingInMemory((c, cfg) => cfg.ConfigureEndpoints(c));
    })
    .BuildServiceProvider(true);
```

**Analog assertion (lines 87-91 asserts `countA + countB == 2`) — INVERT to count==1:**
```csharp
var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
try
{
    await harness.Bus.Publish(new KeeperPlaceholder { CorrelationId = NewId.NextGuid() }, ct);
    var consumer = harness.GetConsumerHarness<PlaceholderConsumer>();
    Assert.True(await consumer.Consumed.Any<KeeperPlaceholder>(ct));
    Assert.Equal(1, consumer.Consumed.Select<KeeperPlaceholder>(ct).Count());   // LOAD-BALANCE: exactly one
}
finally { await harness.Stop(ct); }
```
**Honest scope (RESEARCH A2):** the in-memory harness has a single endpoint instance, so this proves the *binding shape* (shared, not fan-out), not true cross-replica distribution. The real KEEP-02 proof is the live-stack manual smoke (`docker compose up`, publish N, observe split across `keeper-1`/`keeper-2` logs).

---

### `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs` (+ Keeper fixture) (test, KEEP-01)

**Analogs:** `tests/BaseApi.Tests/Console/ConsoleHostBootTests.cs` + `tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs`

Reuse `ConsoleTestHostFixture`'s exact structure (free-port pick, in-memory config with DEAD Redis + unreachable RabbitMQ, three-call seam, `IAsyncLifetime` start/stop). The ONE delta: the Keeper variant's `ConfigureBuilder` override registers the placeholder consumer + binds `RetryOptions` (so `PlaceholderConsumerDefinition`'s `IOptions<RetryOptions>` ctor resolves).

**Analog `ConfigureBuilder` (ConsoleTestHostFixture.cs lines 88-93) — Keeper override adds the consumer + Retry binding:**
```csharp
protected override void ConfigureBuilder(IHostApplicationBuilder builder)
{
    builder.AddBaseConsoleObservability(builder.Configuration);
    builder.Services.AddBaseConsole(builder.Configuration);
    builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));   // resolves the definition's IOptions ctor
    builder.Services.AddBaseConsoleMessaging(builder.Configuration,
        x => x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>());
}
```

**Boot assertion — copy ConsoleHostBootTests.cs lines 20-31 verbatim** (host boots against dead deps; `IBusControl` resolvable). KEEP-01 readiness-on-bus-start is implicitly covered: Keeper keeps the default `StartupCompletionService`, and the fixture's `InitializeAsync` (lines 129-135) runs `Host.StartAsync()` which flips the gate (`StartupCompletionService.StartAsync → gate.MarkReady()`).

---

### `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` (test, KEEP-01 reference closure)

**Analog:** `tests/BaseApi.Tests/Console/ConsoleDependencyFirewallTests.cs`

Same `GetReferencedAssemblies()` reflection pattern (analog lines 30-87). Two deltas: (1) anchor `BaseConsoleAssembly` on a **Keeper** type (e.g. `typeof(Keeper.Consumers.PlaceholderConsumer).Assembly`) instead of `RequiredConfig`; (2) extend `ForbiddenPrefixes` to also forbid `Quartz` and `Cronos` (D-07: Keeper does not schedule) on top of the existing `BaseApi.Core` / `Microsoft.EntityFrameworkCore` / `Npgsql`.

**Analog forbidden-prefix list (lines 33-38) — extend for Keeper:**
```csharp
private static readonly Assembly KeeperAssembly =
    typeof(Keeper.Consumers.PlaceholderConsumer).Assembly;   // was typeof(RequiredConfig)

private static readonly string[] ForbiddenPrefixes =
[
    "BaseApi.Core",
    "Microsoft.EntityFrameworkCore",
    "Npgsql",
    "Quartz",        // ADD — D-07: Keeper does not schedule
    "Cronos",        // ADD — D-07
];
```
Reuse the consolidated `GetReferencedAssemblies().Where(StartsWith)` assertion (analog lines 62-76) verbatim.

---

### `SK_P.sln` (MODIFY)

**Analog:** the existing `Orchestrator` / `Processor.Sample` project entries.

Three blocks to add (the `.sln` format requires all three for the project to build under the solution):

1. **Project declaration** (mirror line 17, NEW GUID):
```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Keeper", "src\Keeper\Keeper.csproj", "{NEW-GUID}"
EndProject
```
2. **ProjectConfigurationPlatforms** (mirror lines 49-52 — 4 Debug/Release lines for the new GUID), inside `GlobalSection(ProjectConfigurationPlatforms)`.
3. **NestedProjects** (mirror line 68 — nest under the `src` solution folder `{EAC83310-2BA8-4E7E-9A90-5C6BD471C231}`), inside `GlobalSection(NestedProjects)`.

Project-type GUID for a console csproj here is `{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}` (same as Orchestrator/Messaging.Contracts/BaseConsole.Core). Generate a fresh unique project GUID for the new entry.

---

### `compose.yaml` (MODIFY)

**Analog:** the `orchestrator:` tier (lines 185-214).

Copy the orchestrator tier; apply the **two D-04 divergences** (drop `container_name`, add `deploy.replicas: 2`), drop the `Orchestrator__InstanceId` env, and bump the healthcheck port to 8083. NO `baseapi-service` dependency (D-05).

```yaml
  keeper:
    build:
      context: .
      dockerfile: src/Keeper/Dockerfile
    # NO container_name (D-04 — a named container cannot be scaled)
    deploy:
      replicas: 2                      # D-04 — `docker compose up` brings up 2 replicas
    restart: unless-stopped
    depends_on:
      rabbitmq:
        condition: service_healthy
      redis:
        condition: service_healthy
      # NO baseapi-service (D-05 — Keeper resolves no identity over the WebApi)
    environment:
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      # NO Orchestrator__InstanceId (Keeper has no instance-id concept)
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8083/health/ready"]   # was 8081
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
```
**No `ports:` block** — orchestrator/processor publish no host port; Keeper follows (8083 is container-internal; two replicas on the same internal port is fine since nothing is published — RESEARCH Code Examples).

---

### `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` (MODIFY)

**Analog:** the Phase-28 processor-sample facts (lines 133-159) — the regex-against-`compose.yaml`-content pattern.

Add a Keeper fact group mirroring `ComposeYaml_Has_ProcessorSample_Service_Block` (lines 133-139). Reuse the existing `ComposeYamlContent()` helper. Assertions to add (KEEP-03):
```csharp
// keeper tier present + correct Dockerfile
Assert.Matches(new Regex(@"dockerfile:\s*src/Keeper/Dockerfile"), content);
// D-04: replicas, scoped to the keeper block
Assert.Matches(new Regex(@"(?ms)keeper:[\s\S]*?deploy:\s*\n\s*replicas:\s*2"), content);
// D-04: NO container_name inside the keeper block (named containers can't scale)
Assert.DoesNotMatch(new Regex(@"(?ms)keeper:[\s\S]*?container_name:"), content);
// 8083 health
Assert.Matches(new Regex(@"http://localhost:8083/health/ready"), content);
```
Note the existing facts use block-scoped `(?ms)<tier>:[\s\S]*?` lookaheads (lines 142-158) to avoid matching neighbouring tiers — replicate that scoping. The `DoesNotMatch container_name` regex must be keeper-block-scoped (the file's OTHER tiers DO have `container_name`), so bound it with the next-tier header or a non-greedy window.

---

### `src/Messaging.Contracts/KeeperQueues.cs` (CREATE, optional — D-08 discretion)

**Analog:** `src/Messaging.Contracts/OrchestratorQueues.cs`

Per RESEARCH Open Question 1 recommendation: put the queue-name const in Contracts (Phase 35's real consumers reuse the SAME endpoint name, so the const survives). Mirror the `OrchestratorQueues` static-class shape (analog lines 8-17):
```csharp
namespace Messaging.Contracts;

public static class KeeperQueues
{
    /// <summary>Stable shared competing-consumer queue for Keeper fault-recovery work.
    /// Durable (NOT InstanceId/Temporary) so it survives in both close-gate rabbitmq snapshots.</summary>
    public const string FaultRecovery = "keeper-fault-recovery";   // enduring Phase-35 role name (RESEARCH OQ-2)
}
```

---

## Shared Patterns

### Competing-consumer endpoint binding (the load-bearing pattern)
**Source:** `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` lines 22-43
**Apply to:** `PlaceholderConsumerDefinition` + the Program.cs `AddConsumer` call + the round-robin test.
Plain `AddConsumer<C,D>()` + `ConsumerDefinition.EndpointName = <stable const>` + `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`. This single shape satisfies D-02 (round-robin not fan-out), D-09 (shared `Immediate(N)`), and the close-gate net-zero SHA (durable queue in both snapshots). **The inverse — `.Endpoint(e => { e.InstanceId; e.Temporary = true; })` — is forbidden everywhere in Keeper.**

### Retry-budget binding (DLQ-04 / D-09)
**Source:** `src/Orchestrator/Program.cs` line 29 + `src/Messaging.Contracts/Configuration/RetryOptions.cs`
**Apply to:** `Program.cs` and the Keeper host-boot test fixture.
`builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));` — single source of truth read by `UseMessageRetry`. Default `Immediate(3)` from `RetryOptions` if the section is absent.

### Thin-shell composition root
**Source:** `src/Orchestrator/Program.cs` lines 17-50 (kept portion) + `tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs` lines 88-93
**Apply to:** `Program.cs` and the Keeper test fixture.
`Host.CreateApplicationBuilder` → `AddBaseConsoleObservability` → `AddBaseConsole` → `AddBaseConsoleMessaging(cfg, x => ...)`. ASP.NET surface flows transitively via BaseConsole.Core's `FrameworkReference` (no `FrameworkReference` in the concrete csproj; aspnet base image mandatory — Pitfall 3).

### Dockerfile multi-stage console build
**Source:** `src/Orchestrator/Dockerfile` lines 9-34
**Apply to:** `src/Keeper/Dockerfile`.
`sdk:8.0-bookworm-slim` build → csproj-only restore-cache layer → full-source publish → `aspnet:8.0-bookworm-slim` runtime → `apt-get install wget` (as root, before `USER app`) → `ASPNETCORE_URLS`/`EXPOSE` on the health port. Swap the 3-csproj COPY list + publish target + `.dll` entrypoint to Keeper; bump port to 8083.

### Reflection ref-firewall test
**Source:** `tests/BaseApi.Tests/Console/ConsoleDependencyFirewallTests.cs` lines 62-87
**Apply to:** `KeeperDependencyFirewallTests`.
`assembly.GetReferencedAssemblies().Where(name => ForbiddenPrefixes.Any(p => name.StartsWith(p)))` → `Assert.Empty`. Keeper extends the forbidden list with `Quartz` + `Cronos`.

### Compose-shape regex fact
**Source:** `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` lines 16-21 (`ComposeYamlContent` helper) + 133-159 (block-scoped tier facts)
**Apply to:** the new Keeper facts.
`File.ReadAllText(compose.yaml)` + block-scoped `(?ms)keeper:[\s\S]*?<assertion>` regexes. Reuse `FindRepoRoot()` (walks up to `SK_P.sln`).

---

## No Analog Found

None. Every Phase-34 artifact has an exact or role-match in-repo analog (this is a console-mirror phase). The planner should rely on PATTERNS.md excerpts directly, not RESEARCH.md fallback patterns.

---

## Metadata

**Analog search scope:** `src/Orchestrator/`, `src/Messaging.Contracts/`, `src/Processor.Sample/Dockerfile`, `tests/BaseApi.Tests/{Console,Orchestrator,Composition}/`, `compose.yaml`, `SK_P.sln`.
**Files scanned:** 15 (all read in full or targeted ranges; no re-reads).
**Pattern extraction date:** 2026-06-05
