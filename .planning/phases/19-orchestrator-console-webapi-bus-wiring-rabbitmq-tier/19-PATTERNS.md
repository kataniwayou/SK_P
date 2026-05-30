# Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier - Pattern Map

**Mapped:** 2026-05-30
**Files analyzed:** 19 (new + modified)
**Analogs found:** 17 / 19 (2 are NET-NEW structural idioms — closest structural analog mapped)

> **Stack note:** .NET 8 / C# microservices (CPM via `Directory.Packages.props`; common props inherited from
> `Directory.Build.props` — NEVER redeclare `TargetFramework`/`Nullable`/`LangVersion`/`TreatWarningsAsErrors`).
> MassTransit + MassTransit.RabbitMQ pinned 8.5.5. Generic Host confirmed (`IHostApplicationBuilder`, see
> `BaseConsoleObservabilityExtensions.cs:33` — resolves RESEARCH A8: use `Host.CreateApplicationBuilder`, NOT
> `WebApplication.CreateBuilder`).

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Orchestrator/Program.cs` (NEW) | host/composition | event-driven (consume) | `src/BaseApi.Service/Program.cs` + `BaseConsoleObservabilityExtensions.cs` | role-match (Generic Host vs Web) |
| `src/Orchestrator/Orchestrator.csproj` (NEW) | config | — | `src/BaseConsole.Core/BaseConsole.Core.csproj` | exact (Microsoft.NET.Sdk worker) |
| `src/Orchestrator/Dockerfile` (NEW) | config | — | root `Dockerfile` | exact (multi-stage net8.0) |
| `src/Orchestrator/appsettings.json` (NEW) | config | — | `src/BaseApi.Service/appsettings.json` | role-match |
| `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` (NEW) | consumer | event-driven + CRUD-read (L2) | NET-NEW (no `IConsumer<T>` precedent); closest = `OrchestrationService` (read-Redis + ack/throw split) | partial (idiom net-new) |
| `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` (NEW) | consumer | event-driven + CRUD-read (L2) | same as Start consumer | partial |
| `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs` (NEW) | config/definition | — | NET-NEW (`ConsumerDefinition<T>` no precedent); RESEARCH Pattern 1 verified shape | partial |
| `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs` (NEW) | config/definition | — | same as Start definition | partial |
| `src/Orchestrator/...` business exception `WorkflowRootNotFoundException` (NEW) | exception type | — | `src/BaseApi.Core/Exceptions/*` (NotFoundException pattern) | role-match |
| `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (NEW) | DI extension | request-response (publish-only) | `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` | exact |
| `src/Messaging.Contracts/ICorrelated.cs` (MODIFY) | contract/interface | — | self (slim from 6→1) | exact |
| `src/Messaging.Contracts/StartOrchestration.cs` (MODIFY) | contract/record | — | self + Pitfall 5 init-member shape | exact |
| `src/Messaging.Contracts/StopOrchestration.cs` (MODIFY) | contract/record | — | `StartOrchestration.cs` (mirror) | exact |
| `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` (MODIFY) | middleware/filter | event-driven | self (re-point envelope→body, line 34) | exact |
| `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` OR the new messaging ext (bus health soft-dep, D-05) | DI/health config | — | `HealthServiceCollectionExtensions.cs` tag discipline + `BusReadyHealthCheck.cs` (inverse posture) | role-match |
| `src/BaseApi.Service/Program.cs` (MODIFY) | host/composition | — | self (chain `AddBaseApiMessaging`) | exact |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` (MODIFY) | service | CRUD + publish | self (inject + publish after L2 write) | exact |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` (MODIFY) | DI extension | — | self (add `IPublishEndpoint` to explicit factory) | exact |
| `compose.yaml` (MODIFY) | infra config | — | `redis`/`postgres`/`elasticsearch` service blocks + `baseapi-service` depends_on | exact |
| `src/BaseApi.Service/appsettings.json` + `src/Orchestrator/appsettings.json` (MODIFY/NEW) | config | — | `appsettings.json` ConnectionStrings/Redis sections | exact |
| `tests/BaseApi.Tests/Orchestrator/*` (NEW) + `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` (REWRITE) | test | event-driven (harness) | `ConsoleCorrelationFilterTests.cs` (AddMassTransitTestHarness, xUnit v3) | exact |
| `SK_P.sln` (MODIFY) | config | — | existing `BaseConsole.Core` GUID-typed project entry | exact |

---

## Pattern Assignments

### `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (NEW — D-03, publish-only)

**Analog:** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (the console-side sibling — copy its structure, DROP the consumer lambda, filters, accessor, and `ConfigureEndpoints`).

**Config-read + bus-registration skeleton to copy** (BaseConsole `MessagingServiceCollectionExtensions.cs:25-49`):
```csharp
var rabbitHost = cfg.Require("RabbitMq:Host");      // RequiredConfig.Require — exists in BaseApi.Core too
var rabbitUser = cfg.Require("RabbitMq:Username");
var rabbitPass = cfg.Require("RabbitMq:Password");

services.AddMassTransit(x =>
{
    // ... (Phase 19: NO configureConsumers, NO accessor, NO Use*Filter, NO ConfigureEndpoints)
    x.UsingRabbitMq((ctx, c) =>
    {
        c.Host(rabbitHost, h => { h.Username(rabbitUser); h.Password(rabbitPass); });
    });
});
return services;
```

**Differences from analog (publish-only, per D-02/D-03):**
- `AddBaseApiMessaging(this IServiceCollection, IConfiguration)` — NO `Action<IBusRegistrationConfigurator>` parameter (nothing to register).
- NO `AddSingleton<ICorrelationAccessor, ...>` (D-02 — no correlation filter/accessor on WebApi).
- NO `UseConsumeFilter`/`UseSendFilter`/`UsePublishFilter` (those live in `BaseConsole.Core`, which `BaseApi.*` MUST NOT reference — D-03/MSG-WEBAPI-01).
- NO `ConfigureEndpoints(ctx)` (publish-only — no receive endpoints).

**Visibility note:** the BaseConsole analog is `public static`. The existing `HealthServiceCollectionExtensions` in BaseApi.Core is `internal static` (line 15). `AddBaseApiMessaging` is called from `BaseApi.Service.Program.cs` (a referencing assembly) — make it **`public static`** (mirror the `AddBaseApi`/`AddBaseApiObservability` public surface, NOT the internal health ext).

**Bus health soft-dep (D-05) — add INSIDE the `AddMassTransit(x => ...)` body** (verified API, RESEARCH Pattern 3):
```csharp
x.ConfigureHealthCheckOptions(o =>
{
    o.MinimalFailureStatus = HealthStatus.Degraded;   // never escalate to Unhealthy
    // DO NOT touch o.Tags — overriding REPLACES defaults ["ready","masstransit"] (Pitfall 7)
});
```
> Tag discipline source: `HealthServiceCollectionExtensions.cs:21-26` uses `tags: new[] { "ready" }` / `{ "live" }` / `{ "startup", "ready" }`. The MT bus check self-registers with `["ready","masstransit"]`; capping `MinimalFailureStatus = Degraded` keeps CRUD `/health/ready` 200 on broker-down. This is the **inverse** of `BusReadyHealthCheck.cs:60-64` (Orchestrator: Degraded → Unhealthy → 503).

---

### `src/BaseApi.Service/Program.cs` (MODIFY — D-03)

**Analog:** self (lines 5-11). Insert one line after `AddBaseApi<AppDbContext>`, before `Build()`:
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddBaseApiObservability(builder.Configuration);
builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
builder.Services.AddBaseApiMessaging(builder.Configuration);   // <-- NEW (D-03)
builder.Services.AddAppFeatures();
builder.Services.AddBaseApiFallbackHandler();
var app = builder.Build();
```

---

### `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` (MODIFY — D-04, publish)

**Analog:** self. The class is `public sealed`; ctor is **`internal`** (line 66) with `?? throw new ArgumentNullException` guards on every field (lines 79-89).

**Inject `IPublishEndpoint`** — add a readonly field + ctor parameter + guard, mirroring the existing field block (lines 49-90):
```csharp
private readonly IPublishEndpoint _publishEndpoint;
// ... in ctor signature (line 66 block), add parameter:
//   IPublishEndpoint publishEndpoint,
// ... in ctor body (line 79 block), add guard:
_publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
```

**Publish call site — Start** (insert AFTER the per-workflow loop closes, line 149, before method end). The existing L2-write loop ends at line 149; the existing param is `IReadOnlyList<Guid> workflowIds` (line 101):
```csharp
await _publishEndpoint.Publish(
    new StartOrchestration(workflowIds.ToArray()) { CorrelationId = NewId.NextGuid() },
    ct);
```

**Publish call site — Stop** (insert AFTER the post-gate cleanup loop, line 218, where all roots exist — D-04). Mirror Start but publish `StopOrchestration`. The `workflowIds` param is `IReadOnlyList<Guid>` (line 163).

> Do NOT set the MT envelope `CorrelationId` — body-only (D-02). `NewId.NextGuid()` preferred over `Guid.NewGuid()` (sequential — discretion, D-02 verbatim shape). The existing HTTP-stage `correlationId` string (line 108, from `HttpContext.Items["CorrelationId"]`) is a SEPARATE stage — leave it untouched; do NOT carry it onto the bus message.

---

### `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` (MODIFY)

**Analog:** self (lines 56-67). The service is registered via an **explicit factory** because the ctor is internal. Add one line to the factory call (the parameter ORDER must match the ctor signature you edit in OrchestrationService):
```csharp
services.AddScoped<OrchestrationService>(sp => new OrchestrationService(
    sp.GetRequiredService<BaseDbContext>(),
    // ... existing 9 resolves ...
    sp.GetRequiredService<IOptions<RedisProjectionOptions>>(),
    sp.GetRequiredService<IPublishEndpoint>()));   // <-- NEW (registered by AddMassTransit)
```
> `IPublishEndpoint` is registered by `AddBaseApiMessaging`'s `AddMassTransit` (MassTransit DI). Pattern mirrors the existing "NEW (...)" annotated resolves at lines 64-67.

---

### `src/Messaging.Contracts/ICorrelated.cs` (MODIFY — D-01, slim 6→1)

**Analog:** self (current 6-Guid get-only at lines 4-12). Replace the body — interface stays get-only (init lives on the implementer, Pitfall 5):
```csharp
namespace Messaging.Contracts;

/// <summary>Universal correlation contract — body-carried correlation id (v3.4.0 model, D-01).</summary>
public interface ICorrelated
{
    Guid CorrelationId { get; }   // interface get-only; implementers use { get; init; }
}
```
> Blast radius (VERIFIED in RESEARCH §Runtime State Inventory): only `ConsoleCorrelationFilterTests.ProbeMessage` breaks at compile (declares all 6 Guids). The two outbound filters gate on `is ICorrelated` (type), not the removed members → no break. Do NOT define `IExecutionCorrelated` (DEFERRED — Processor milestone).

---

### `src/Messaging.Contracts/StartOrchestration.cs` + `StopOrchestration.cs` (MODIFY — D-01)

**Analog:** current bare records (`StartOrchestration.cs:4`, `StopOrchestration.cs:4`). Make each implement `ICorrelated` with a NON-positional init member (Pitfall 5 — a positional record can't add `init` to a positional param; add it as a separate property):
```csharp
public sealed record StartOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }   // non-positional init member
}
// StopOrchestration mirrors this exactly.
```

---

### `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` (MODIFY — D-01, envelope→body)

**Analog:** self. Re-point line 34 ONLY; keep `where T : class` (line 30 — registered bus-wide via open-generic `UseConsumeFilter`, must tolerate non-correlated messages):
```csharp
// BEFORE (line 34):
var corrId = context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString();
// AFTER (D-01 — read body, keep the string accessor contract; envelope fallback optional, planner may drop):
var corrId = (context.Message as ICorrelated)?.CorrelationId.ToString()
             ?? context.CorrelationId?.ToString()
             ?? Guid.NewGuid().ToString();
```
> `Messaging.Contracts` is already imported (line 2). The accessor stores `string?` (Pitfall 6) — `.ToString()` keeps that contract; do NOT change the accessor to `Guid`. Lines 35-37 (`accessor.Set` + `BeginScope` under `CorrelationKeys.LogScope`) are unchanged.

---

### `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` + `StopOrchestrationConsumer.cs` (NEW — ORCH-CON-03/04, D-07/D-08)

**Analog (structural — `IConsumer<T>` is NET-NEW):** closest is `OrchestrationService.cs` for (a) the Redis read via `IConnectionMultiplexer` + `RedisProjectionKeys.Root` (lines 179-184), and (b) the catch-and-classify discipline (lines 118-126). Primary-ctor DI mirrors `BusReadyHealthCheck` / filter ctors and `ConsoleCorrelationFilterTests.ProbeConsumer` (test file line 37).

**Redis L2 read shape to copy** (from `OrchestrationService.StopAsync` lines 179-184):
```csharp
var db = _multiplexer.GetDatabase();
// ... db.KeyExistsAsync(RedisProjectionKeys.Root(_keyPrefix, id))
```
Orchestrator read uses `StringGetAsync` (read payload) instead of `KeyExistsAsync`:
```csharp
public sealed class StartOrchestrationConsumer(
    IConnectionMultiplexer redis, ILogger<StartOrchestrationConsumer> logger)
    : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        // Inbound filter already opened the "CorrelationId" scope from the body (no re-read here).
        var db = redis.GetDatabase();   // infra fault THROWS → retry → _error (D-08)
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            var raw = await db.StringGetAsync(/* Root(prefix, workflowId) */);
            if (raw.IsNullOrEmpty)        // business failure → log + ack, NEVER throw (D-07 / MSG-ACK-01)
            {
                logger.LogWarning("Workflow {WorkflowId} absent from L2 — business failure, acking", workflowId);
                continue;                 // (or throw WorkflowRootNotFoundException ONLY if the definition Ignore<>s it)
            }
            var root = JsonSerializer.Deserialize<WorkflowRootProjection>(raw!);  // camelCase frozen, Phase 17 D-08
            logger.LogInformation("Scheduler job start (seam) for {WorkflowId}", workflowId);  // ORCH-CON-04 seam — NO Redis write, NO Quartz
        }
    }
}
```

**Key-prefix source (discretion — RESEARCH Open-Q3):** `RedisProjectionKeys` is `internal` in `BaseApi.Service` (`RedisProjectionKeys.cs:18`), so the Orchestrator can't reference it. Two options: (a) hoist `RedisProjectionKeys.Root(prefix, id) => $"{prefix}{workflowId}"` ("D" Guid format) to `Messaging.Contracts`, OR (b) duplicate the literal with a comment. The Orchestrator reads its own prefix from config (`Redis:KeyPrefix` = `"skp:"`, see `appsettings.json:22`).

**Catch discipline (Pitfall 2):** catch ONLY the business outcome (`raw.IsNullOrEmpty` continue, or `catch (WorkflowRootNotFoundException)`), NEVER `catch (Exception)`. Infra faults propagate → retry → `_error`.

---

### `src/Orchestrator/Consumers/*ConsumerDefinition.cs` (NEW — MSG-ACK-04, D-06/D-08)

**Analog:** NET-NEW (`ConsumerDefinition<T>` has no repo precedent). Use the VERIFIED RESEARCH Pattern 1 shape; the structural role mirrors how `OrchestrationServiceCollectionExtensions` is the config seam for `OrchestrationService` (separating wiring/retry config from behavior):
```csharp
public sealed class StartOrchestrationConsumerDefinition
    : ConsumerDefinition<StartOrchestrationConsumer>
{
    public StartOrchestrationConsumerDefinition() => EndpointName = "orchestrator";  // SHARED base name (both defs)

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StartOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>          // D-07/D-08 ack split
        {
            r.Immediate(3);
            r.Ignore<WorkflowRootNotFoundException>();      // business failure never retries
        });
    }
}
// StopOrchestrationConsumerDefinition mirrors EXACTLY (same EndpointName = "orchestrator").
```
> Registration goes in the Orchestrator's `AddBaseConsoleMessaging(cfg, x => {...})` lambda — both consumers use the SAME `instanceId` so `ConfigureEndpoints` groups them onto one per-replica queue (VERIFIED A2):
> ```csharp
> x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
>     .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
> x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
>     .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
> ```
> `instanceId = cfg["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N")` (D-06), captured by closure in Program.cs. `e.Temporary = true` gives the non-durable/auto-delete fan-out queue (Open-Q2). NO BaseConsole.Core change — `AddBaseConsoleMessaging` already calls `ConfigureEndpoints(ctx)` (line 44).

---

### `src/Orchestrator/Program.cs` (NEW — ORCH-CON-01)

**Analog:** `src/BaseApi.Service/Program.cs` (top-level statements, composition chain) + `BaseConsoleObservabilityExtensions.cs:33` (confirms `IHostApplicationBuilder` — use **`Host.CreateApplicationBuilder(args)`**, NOT `WebApplication.CreateBuilder`). The three-call seam is documented at `BaseConsoleServiceCollectionExtensions.cs:20`.

**Composition chain to mirror** (the BaseApi `Program.cs:5-14` structure, swapped to console seams):
```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddBaseConsoleObservability(builder.Configuration);      // IHostApplicationBuilder (Logging+Services)
builder.Services.AddBaseConsole(builder.Configuration);          // Redis soft-dep + embedded health
var instanceId = builder.Configuration["Orchestrator:InstanceId"]
                 ?? Guid.NewGuid().ToString("N");                // D-06 fallback
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
    x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
});
var host = builder.Build();
await host.RunAsync();
```
> Do NOT add `.WithTracing`/`.AddSource("MassTransit")` — console OTel is metrics-only (Pitfall 4, `BaseConsoleObservabilityExtensions.cs` has no tracer provider).

---

### `src/Orchestrator/Orchestrator.csproj` (NEW)

**Analog:** `src/BaseConsole.Core/BaseConsole.Core.csproj` (Microsoft.NET.Sdk worker — NOT `.Web`). Copy the `<PropertyGroup>` (RootNamespace/AssemblyName/`NoWarn CS1591`) and these ItemGroups:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Orchestrator</RootNamespace>
    <AssemblyName>Orchestrator</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MassTransit" />            <!-- CPM, no Version= -->
    <PackageReference Include="MassTransit.RabbitMQ" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\BaseConsole.Core\BaseConsole.Core.csproj" />
    <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
  </ItemGroup>
</Project>
```
> Do NOT redeclare `TargetFramework` etc. (inherited from `Directory.Build.props`). NO reference to `BaseApi.*`. `FrameworkReference Microsoft.AspNetCore.App` flows transitively via `BaseConsole.Core` (the embedded Kestrel health listener needs it at runtime → use `aspnet:8.0` base image). Direct `MassTransit` ref is cleaner than transitive (A9 — cosmetic).

---

### `src/BaseApi.Core/BaseApi.Core.csproj` (MODIFY — D-03)

**Analog:** self (existing CPM `<PackageReference Include="..." />` pattern, no Version=, lines 43-97) + `BaseConsole.Core.csproj:40-42`. Add one ItemGroup + the contracts ref:
```xml
<ItemGroup>
  <PackageReference Include="MassTransit" />
  <PackageReference Include="MassTransit.RabbitMQ" />
</ItemGroup>
<ItemGroup>
  <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
</ItemGroup>
```

---

### `src/Orchestrator/Dockerfile` (NEW — D-09, INFRA-RMQ-03)

**Analog:** root `Dockerfile` (multi-stage net8.0; lines 1-19). Mirror exactly but swap the COPY list / publish target to `Orchestrator` and use port 8081 (ConsoleHealth default, Phase 18 D-04). Use `aspnet:8.0-bookworm-slim` runtime (NOT `runtime:8.0` — BaseConsole.Core needs the ASP.NET shared framework). COPY list must include `Messaging.Contracts.csproj` + `BaseConsole.Core.csproj` + `Orchestrator.csproj` (the restore-cache layer). See RESEARCH §Dockerfile lines 648-666 for the verified file.

---

### `compose.yaml` (MODIFY — D-09, INFRA-RMQ-02)

**Analog:** the `redis` block (lines 135-151 — `sk-*` container_name, CMD-form healthcheck, host:container port offset, `restart: unless-stopped`) and `baseapi-service` (lines 153-184 — `depends_on: condition: service_healthy`, `environment:` `__`-delimited overrides). NEW `rabbitmq` service mirrors the redis healthcheck idiom (CMD form):
```yaml
rabbitmq:
  image: rabbitmq:4.1.8-management-alpine
  container_name: sk-rabbitmq          # sk-* cross-stack uniqueness (matches sk-redis line 137)
  restart: unless-stopped
  ports:
    - "5673:5672"      # AMQP (host 5673 — mirrors redis 6380:6379 collision-avoidance, line 140)
    - "15673:15672"    # management UI
  environment:
    RABBITMQ_DEFAULT_USER: guest
    RABBITMQ_DEFAULT_PASS: guest
  healthcheck:
    test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]   # CMD form — matches redis line 147
    interval: 10s
    timeout: 5s
    retries: 5
    start_period: 40s    # cold-start; cf. ES start_period:60s (line 49)
```
- Add `rabbitmq: { condition: service_healthy }` to `baseapi-service.depends_on` (mirror the existing `redis:`/`postgres:` entries, lines 173-183) + `RabbitMq__Host/Username/Password` env (mirror `ConnectionStrings__Redis` line 163).
- NEW `orchestrator` service: `build.dockerfile: src/Orchestrator/Dockerfile`; `depends_on: rabbitmq: service_healthy` + `redis: service_healthy`; env `RabbitMq__*`, `ConnectionStrings__Redis` (copy line 163 verbatim), `Orchestrator__InstanceId`; healthcheck `wget --spider -q http://localhost:8081/health/ready` (mirror baseapi line 168, port 8081). See RESEARCH lines 607-644 for the verified block.

---

### `src/BaseApi.Service/appsettings.json` + `src/Orchestrator/appsettings.json` (MODIFY/NEW — INFRA-RMQ-03)

**Analog:** `src/BaseApi.Service/appsettings.json` (the `ConnectionStrings`/`Redis` sections, lines 13-27). Add a `RabbitMq` section to BOTH hosts (read by `cfg.Require("RabbitMq:Host"|"Username"|"Password")`):
```json
"RabbitMq": { "Host": "rabbitmq", "Username": "guest", "Password": "guest" }
```
The Orchestrator's appsettings ALSO needs (mirror the existing keys): `Service:{Name,Version}` (for `AddBaseConsoleObservability` `cfg.Require`, see `BaseConsoleObservabilityExtensions.cs:38-39`), `ConnectionStrings:Redis` OR `Redis:` block (L2 read), `Redis:KeyPrefix` (`"skp:"`, copy `appsettings.json:22`), `Orchestrator:InstanceId` (optional — D-06 fallback), `ConsoleHealth:Port` (8081, Phase 18 D-04).

---

### `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` (REWRITE) + `tests/BaseApi.Tests/Orchestrator/*` (NEW)

**Analog:** `ConsoleCorrelationFilterTests.cs` (the canonical xUnit v3 + `AddMassTransitTestHarness` harness — copy its lifecycle, REWRITE its assertions).

**Harness lifecycle to copy** (lines 51-106):
```csharp
var ct = TestContext.Current.CancellationToken;                  // xUnit v3 idiom (line 51)
await using var provider = new ServiceCollection()
    .AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>()
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<...>();
        x.UsingInMemory((ctx, cfg) =>
        {
            cfg.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);   // bus-wide (lines 65-68)
            cfg.ConfigureEndpoints(ctx);
        });
    })
    .BuildServiceProvider(true);
var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
try { /* ... */ } finally { await harness.Stop(ct); }
// Assert: harness.Published.Any<T>(...), harness.Consumed.Any<T>(ct), harness.Faulted...
```

**REWRITE deltas (RESEARCH §Wave-0 Gaps, VERIFIED both counts break):**
- `ProbeMessage` (lines 25-31): drop the 6 Guids → `record ProbeMessage(Guid CorrelationId) : ICorrelated;` (won't compile after the slim).
- Assertion (lines 92-94, 101): the inbound filter now reads the **body** `ICorrelated.CorrelationId`, NOT the envelope `p.Context.CorrelationId`. Set the body field at publish (`new ProbeMessage(someGuid)`) and assert the consumer's captured accessor value equals `someGuid.ToString()`.

**NEW Orchestrator tests** (`tests/BaseApi.Tests/Orchestrator/` — discretion vs separate `tests/Orchestrator.Tests`; placement should anticipate Phase 20's heavy two-bus/ES tests):
- `StartStopConsumerAckTests.cs` — MSG-ACK-01/02/03: business failure → `harness.Consumed.Any<T>` (acked, NOT `harness.Consumed.Any<T>(filter where exception)`); infra fault (stub `IConnectionMultiplexer` that throws) → `harness.Consumed` faulted/retried; `Ignore<WorkflowRootNotFoundException>` skips business.
- `OrchestrationServicePublishTests.cs` — MSG-WEBAPI-02: register `OrchestrationService` against the harness's `IPublishEndpoint`, assert `harness.Published.Any<StartOrchestration>(...)` / `StopOrchestration`.
> Fan-out broadcast + ES correlation surface = DEFERRED Phase 20 (TEST-RMQ-01/02) — in-memory harness can't prove them.

---

### `SK_P.sln` (MODIFY)

**Analog:** the existing `BaseConsole.Core` entry (line 15) — a `Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}")` (C# SDK GUID) line + matching `GlobalSection(ProjectConfigurationPlatforms)` Debug/Release Any CPU entries (section starts line 22). Add an `Orchestrator` project with a fresh GUID, same C# project-type GUID, path `src\Orchestrator\Orchestrator.csproj`, and its 4 config-platform lines.

---

## Shared Patterns

### Config fail-fast read (`cfg.Require`)
**Source:** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:29-31` (`cfg.Require("RabbitMq:Host")`); `RequiredConfig.Require` exists in BOTH `BaseApi.Core` and `BaseConsole.Core` (verified RESEARCH). Also `BaseConsoleObservabilityExtensions.cs:38-39` for `Service:Name`/`Service:Version`.
**Apply to:** `AddBaseApiMessaging` (3 RabbitMq keys), Orchestrator `Program.cs` / observability (Service + RabbitMq + Redis keys).

### Redis L2 read via `IConnectionMultiplexer` + `RedisProjectionKeys.Root`
**Source:** `OrchestrationService.cs:179-184` (`_multiplexer.GetDatabase()` → `RedisProjectionKeys.Root(_keyPrefix, id)`). Key shape: `RedisProjectionKeys.cs:20` = `$"{prefix}{workflowId}"` ("D" Guid format). Prefix from `Redis:KeyPrefix` (`appsettings.json:22` = `"skp:"`).
**Apply to:** both Orchestrator consumers (read-only — `StringGetAsync`, no writes; ORCH-CON-04).

### Catch-and-classify (business vs infra fault)
**Source:** `OrchestrationService.cs:118-126` (catch `RedisException`, tag, rethrow) — the discipline of catching ONLY specific exception types, never `catch (Exception)` (Pitfall 2).
**Apply to:** both consumers (business = `raw.IsNullOrEmpty`/`WorkflowRootNotFoundException` → log + ack; infra → throw → retry → `_error`).

### Explicit-factory DI for internal-ctor services
**Source:** `OrchestrationServiceCollectionExtensions.cs:56-67` (`AddScoped<T>(sp => new T(sp.GetRequiredService<...>(), ...))`).
**Apply to:** the `IPublishEndpoint` injection into `OrchestrationService` (extend the existing factory).

### CPM PackageReference (no Version=)
**Source:** `BaseConsole.Core.csproj:40-42`, `BaseApi.Core.csproj` (all ItemGroups). Versions in `Directory.Packages.props`.
**Apply to:** `Orchestrator.csproj`, `BaseApi.Core.csproj` (MassTransit + MassTransit.RabbitMQ).

### compose `sk-*` container_name + CMD healthcheck + `depends_on: service_healthy`
**Source:** `compose.yaml` redis block (135-151) + baseapi-service depends_on (173-183).
**Apply to:** new `rabbitmq` + `orchestrator` services; `baseapi-service` + `orchestrator` depends_on.

### Health tag discipline (`live`/`ready`/`startup`) + soft-vs-hard inversion
**Source:** `HealthServiceCollectionExtensions.cs:21-26` (tags), `BusReadyHealthCheck.cs:60-64` (Orchestrator HARD: Degraded→Unhealthy→503).
**Apply to:** WebApi bus check (D-05 SOFT: `MinimalFailureStatus = Degraded`, never override `o.Tags` — Pitfall 7). The two postures are a DELIBERATE per-host inversion — do not unify.

### xUnit v3 + AddMassTransitTestHarness
**Source:** `ConsoleCorrelationFilterTests.cs` (whole file — `TestContext.Current.CancellationToken`, `await using ... BuildServiceProvider(true)`, `ITestHarness`, `harness.Start()/Stop(ct)`, `harness.Published/Consumed.Any<T>`).
**Apply to:** all new Orchestrator consumer/publish tests + the rewrite.

---

## No Analog Found (NET-NEW structural idioms)

| File | Role | Data Flow | Reason / Mitigation |
|------|------|-----------|---------------------|
| `Consumers/*Consumer.cs` (`IConsumer<T>`) | consumer | event-driven | No `IConsumer<T>` exists in the repo yet. Closest structural analog: `OrchestrationService` (Redis read + catch-classify) for behavior; `ProbeConsumer` (test file line 37) for the primary-ctor `IConsumer<T>` shape. Use RESEARCH §"Consumer with business-ack/infra-throw split" (lines 553-578). |
| `Consumers/*ConsumerDefinition.cs` (`ConsumerDefinition<T>`) | config/definition | — | No `ConsumerDefinition<T>` precedent. Establishes the "base + per-message-type definition" idiom (MSG-ACK-04 rationale, reused by Processor milestone). Use VERIFIED RESEARCH Pattern 1 (lines 296-329). The structural role (config seam decoupled from behavior) mirrors `OrchestrationServiceCollectionExtensions` ↔ `OrchestrationService`. |

> Both have HIGH-confidence Context7-verified API shapes in RESEARCH — the planner uses those excerpts directly (no codebase analog needed).

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/BaseConsole.Core/{DependencyInjection,Messaging,Health}/`, `src/BaseApi.Core/DependencyInjection/`, `src/BaseApi.Service/{Program.cs,appsettings.json,Features/Orchestration/}`, root `Dockerfile` + `compose.yaml` + `SK_P.sln`, `tests/BaseApi.Tests/Console/`.
**Files scanned/read:** 17 source/infra/test files + CONTEXT.md + RESEARCH.md (847 lines).
**Pattern extraction date:** 2026-05-30
**Host type resolved (RESEARCH A8):** Generic Host — `IHostApplicationBuilder` / `Host.CreateApplicationBuilder` (confirmed `BaseConsoleObservabilityExtensions.cs:33`).
```

