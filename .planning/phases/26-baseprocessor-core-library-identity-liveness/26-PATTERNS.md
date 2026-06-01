# Phase 26: BaseProcessor.Core — Library, Identity & Liveness - Pattern Map

**Mapped:** 2026-06-01
**Files analyzed:** 12 created + 2 modified
**Analogs found:** 12 / 12 (every new file has a verified in-repo analog)

> This map builds directly on `26-RESEARCH.md`'s file plan and Code Examples §1–§5. Where RESEARCH already pins a template, this doc gives the planner the exact analog file + verified line ranges to drop into each plan's `read_first`, plus the convention to replicate. No re-derivation.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/BaseProcessor.Core.csproj` | project/config | n/a | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (CPM no-Version + ProjectReference block) | role-match |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | composition root | request-response | `src/Orchestrator/Program.cs` + `BaseConsoleServiceCollectionExtensions.cs` + `MessagingServiceCollectionExtensions.cs` | exact |
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` | options | config | `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs` | exact |
| `src/BaseProcessor.Core/Identity/ISourceHashProvider.cs` | abstract seam | transform | `src/BaseConsole.Core/Health/IStartupGate.cs` (interface+sealed impl shape) | role-match |
| `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs` | utility | transform | (BCL reflection; no in-repo analog — see No Analog Found) | none |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` | context holder | event-driven | `src/BaseConsole.Core/Health/IStartupGate.cs` (`StartupGate` latch idiom) | role-match |
| `src/BaseProcessor.Core/Identity/ProcessorContext.cs` | context holder | event-driven | `src/BaseConsole.Core/Health/IStartupGate.cs` (`StartupGate` Volatile/Interlocked) | role-match |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | hosted orchestrator | request-response | `src/Orchestrator/Hydration/HydrationBackgroundService.cs` | exact |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` | heartbeat worker | event-driven (periodic write) | `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` + `HydrationBackgroundService.cs` (loop/clock) | role-match |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` | abstract processor seam | n/a (declared) | (signature locked by PROJECT.md:32 — see No Analog Found) | none |
| `src/BaseProcessor.Core/Processing/ProcessResult.cs` | model | n/a (declared) | `src/Messaging.Contracts/Projections/LivenessProjection.cs` (sealed record shape) | role-match |
| `tests/BaseApi.Tests/Processor/*Facts.cs` (new) | test | n/a | `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` (harness) + `RedisFixture.cs` | exact |
| **MODIFIED** `tests/BaseApi.Tests/BaseApi.Tests.csproj` | project/config | n/a | self (`ProjectReference` block lines 114-128) | exact |
| (consumed unchanged) `Messaging.Contracts.Projections.*`, `ProcessorQueries`, `ProcessorQueues` | contracts | n/a | reused verbatim — never redefined | exact |

---

## Pattern Assignments

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (composition root, request-response)

**Analogs:** `src/Orchestrator/Program.cs` (gate-removal + TimeProvider), `src/BaseConsole.Core/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs` (chained-extension shape), `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (the `configureConsumers` lambda where request clients fit).

**read_first:** `src/Orchestrator/Program.cs:18-71`, `MessagingServiceCollectionExtensions.cs:34-63`, `BaseConsoleServiceCollectionExtensions.cs:29-33`

**Three-call composition + bus lambda** (`Program.cs:18-42`) — `AddBaseProcessor` folds `AddBaseConsole` + `AddBaseConsoleMessaging` internally (RESEARCH Pattern 1; Observability stays a separate `IHostApplicationBuilder` call in the concrete `Program.cs`):
```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel
builder.Services.AddBaseConsole(builder.Configuration);       // Redis soft-dep + embedded health
builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x =>
    {
        x.AddConsumer<...>()...;   // <-- the processor puts AddRequestClient<T> here instead
    });
```

**Request clients inside the lambda** — the WebApi binds named `ReceiveEndpoint`s (`ProcessorQueues.IdentityQuery`/`SchemaQuery`); senders target `exchange:{name}` (RESEARCH Pattern 2 / Pitfall 4):
```csharp
x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));
// NO AddConsumer this phase (dispatch consumer is Phase 27). Do NOT auto-bind endpoints.
```

**Gate-removal — copy verbatim** (`Program.cs:61-68`) so `MarkReady` fires on Healthy, not host-start (D-02). The removal operates on `IServiceCollection` so it belongs in the composition root, not the concrete `Program.cs`:
```csharp
foreach (var d in builder.Services
             .Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService))
             .ToList())
{
    builder.Services.Remove(d);
}
```

**TimeProvider idempotent registration** (`Program.cs:59`):
```csharp
builder.Services.TryAddSingleton(TimeProvider.System);
```

**Singleton/hosted registrations to add** (RESEARCH §1): `services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"))`, `AddSingleton<ISourceHashProvider, AssemblyMetadataSourceHashProvider>()`, `AddSingleton<IProcessorContext, ProcessorContext>()`, `AddHostedService<ProcessorStartupOrchestrator>()`, `AddHostedService<ProcessorLivenessHeartbeat>()`.

**Chained-return convention** (`BaseConsoleServiceCollectionExtensions.cs:29-33`) — `public static IServiceCollection AddBaseProcessor(this IServiceCollection services, IConfiguration cfg)` returning `services`.

---

### `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (hosted orchestrator, request-response)

**Analog:** `src/Orchestrator/Hydration/HydrationBackgroundService.cs` (the near-exact template per RESEARCH §3).

**read_first:** `src/Orchestrator/Hydration/HydrationBackgroundService.cs:24-85`

**`BackgroundService` + primary-ctor injection + bounded-backoff constants** (lines 24-35):
```csharp
public sealed class HydrationBackgroundService(
    IConnectionMultiplexer redis,
    WorkflowLifecycle lifecycle,
    IStartupGate gate,
    ILogger<HydrationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);  // <-- processor reads this from ProcessorLivenessOptions.BackoffCapSeconds (D-04)
```
> Processor ctor instead injects: `IRequestClient<GetProcessorBySourceHash>`, `IRequestClient<GetSchemaDefinition>`, `ISourceHashProvider`, `IProcessorContext`, `IStartupGate`, `IOptions<ProcessorLivenessOptions>`, `TimeProvider`, `ILogger<...>` (RESEARCH §2 ctor).

**Retry loop + backoff doubling + cancellation-safe delay** (lines 37, 64-82) — this is the load-bearing shape for both Loop A and Loop B:
```csharp
while (!stoppingToken.IsCancellationRequested)
{
    try
    {
        // ... do work; on success: gate.MarkReady(); return;
        gate.MarkReady(); // D-12 — gate flips HERE, only on completion
        return;
    }
    catch (Exception ex) when (WorkflowLifecycle.IsInfra(ex))   // processor: catch RequestTimeoutException + treat NotFound as retry
    {
        logger.LogWarning(ex, "... retrying in {Delay}", delay);
        try { await Task.Delay(delay, stoppingToken); }
        catch (OperationCanceledException) { return; }
        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
    }
}
```
> **Deviation from analog:** the processor's retry is **unbounded** (D-04) — there is no `return` on a not-found/timeout, only on a `Found` response. The hydration service returns after one success; the processor keeps looping past not-found. Use the 3-arg `Task.Delay(delay, clock, ct)` (FakeTimeProvider-drivable, RESEARCH §3 note) instead of the 2-arg form here.

**Dual-response pattern-match** (RESEARCH §3, MassTransit docs — no in-repo IRequestClient precedent exists):
```csharp
var resp = await identityClient.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
    new GetProcessorBySourceHash(hash), ct, RequestTimeout.After(seconds: opts.RequestTimeoutSeconds));
if (resp.Is(out Response<ProcessorIdentityFound> found)) { context.SetIdentity(found.Message); break; }
// ProcessorIdentityNotFound | RequestTimeoutException → backoff + retry (boot-before-register)
```

**Loop B skip-null discipline** (RESEARCH §3b, D-05) — iterate only `InputSchemaId`/`OutputSchemaId`; never read `ConfigSchemaId`; `null` schema id → `continue`. After all required resolved: `context.MarkHealthy(); gate.MarkReady();`.

---

### `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` (heartbeat worker, periodic write)

**Analogs:** `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` (clock + `LivenessProjection` build + serialize + `StringSetAsync`), `HydrationBackgroundService.cs` (BackgroundService loop + cancellation-safe delay).

**read_first:** `RedisProjectionWriter.cs:56-73`, `ProcessorLivenessValidator.cs:30,54-55`, `HydrationBackgroundService.cs:33-82`

**Clock + liveness sub-document build** (`RedisProjectionWriter.cs:60-61`) — MUST use the same `TimeProvider` clock the reader uses (`ProcessorLivenessValidator.cs:30`) so the `interval*2` math aligns:
```csharp
var now = _clock.GetUtcNow().UtcDateTime;   // mirrors RedisProjectionWriter.cs:60 AND validator:30
var liveness = new LivenessProjection(now, 0, "Pending");   // <-- processor writes: (now, opts.IntervalSeconds, LivenessStatus.Healthy)
```

**Serialize + StringSet** (`RedisProjectionWriter.cs:73,80`) — default `System.Text.Json`; processor adds the sliding `expiry`:
```csharp
var rootJson = JsonSerializer.Serialize(root);
var db = _multiplexer.GetDatabase();
tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Root(wf.Id), rootJson));   // root key — NO expiry
// processor: await db.StringSetAsync(L2ProjectionKeys.Processor(id), json, expiry: TimeSpan.FromSeconds(opts.TtlSeconds));
```

**Reader staleness contract the writer must satisfy** (`ProcessorLivenessValidator.cs:54-55`, D-08/LIVE-03):
```csharp
var deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2);   // interval in SECONDS
if (deadline <= now) throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "stale");
```
> So the written `LivenessProjection.Interval` MUST equal `opts.IntervalSeconds` (seconds, NOT `(int)delay.TotalMilliseconds` — RESEARCH Pitfall 2).

**Per-beat resilience (log-and-continue, D-11)** — wrap each `StringSetAsync` in try/catch; log a warning; never `throw`/`return`. Mirror RESEARCH §5 and `RedisProjectionWriter.cs:110-123`'s structured-warning posture (but continue the loop instead of rethrowing). The Healthy-gate (`_context.IsHealthy && _context.Id is { } id`) and the cancellation-safe `Task.Delay(period, _clock, ct)` loop come from `HydrationBackgroundService.cs:33-82` + RESEARCH §5.

**Key/status single-source-of-truth** — `L2ProjectionKeys.Processor(id)` (`L2ProjectionKeys.cs:37`) and `LivenessStatus.Healthy` (`LivenessStatus.cs:11`). Verified: the reader's `RedisProjectionKeys.Processor(id)` forwards to `L2ProjectionKeys.Processor(id)` (`RedisProjectionKeys.cs:19`), so the writer using `L2ProjectionKeys.Processor` is byte-identical.

---

### `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs` (context holder, event-driven)

**Analog:** `src/BaseConsole.Core/Health/IStartupGate.cs` (interface + `public sealed` latch impl with `Volatile.Read`/`Interlocked.Exchange`).

**read_first:** `src/BaseConsole.Core/Health/IStartupGate.cs:13-45`

**Thread-safe latch idiom** (lines 36-45) — reuse for the `IsHealthy` flag; RESEARCH Open Q1 recommends ALSO exposing a `Task WhenHealthy` (a `TaskCompletionSource` completed at `MarkHealthy()`) for Phase 27 to await:
```csharp
public sealed class StartupGate : IStartupGate
{
    private int _isReady; // 0 = false, 1 = true (Interlocked has no bool overload in .NET 8)
    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
```
> `public sealed` so `AddSingleton<IProcessorContext, ProcessorContext>()` resolves across the assembly boundary without `InternalsVisibleTo` (the IStartupGate doc-comment at lines 31-34 explains exactly this). Context members per RESEARCH: `Id` (Guid?), three schema Ids (Guid?), `InputDefinition`/`OutputDefinition` (string?), `IsHealthy`, `SetIdentity(...)`, `SetDefinition(...)`, `MarkHealthy()`.

---

### `src/BaseProcessor.Core/Identity/ISourceHashProvider.cs` (abstract seam) + `AssemblyMetadataSourceHashProvider.cs` (utility)

**Analog (interface shape):** `IStartupGate.cs` (small interface + sealed default impl). **Impl body:** BCL reflection — no in-repo analog (see No Analog Found); follow RESEARCH §4 verbatim.

**read_first:** `26-RESEARCH.md` §4 (Code Examples), `IStartupGate.cs:13-20`

Default impl reads `Assembly.GetEntryAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()` filtered to `Key == "SourceHash"`; **throw** (fail-fast) when absent (RESEARCH §4 + Open Q2). Tests stub `ISourceHashProvider` with a known 64-hex hash via NSubstitute (D-13) — no real assembly attribute needed (the MSBuild embed is Phase 28).

---

### `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (abstract seam) + `ProcessResult.cs` (model)

**Analog (record shape):** `src/Messaging.Contracts/Projections/LivenessProjection.cs` (sealed positional record). **Signature:** locked by PROJECT.md:32 — no in-repo analog (see No Analog Found).

**read_first:** `PROJECT.md:32`, `LivenessProjection.cs:11-14`

Declare `public abstract class BaseProcessor` with the single `protected abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` seam (D-12 / BPC-02; RESEARCH Pattern 5). **Declared now, invoked Phase 27.** A test double overrides it to prove the class compiles + DI-resolves. `ProcessResult` is a sealed record declared now; its concrete fields are firmed up in Phase 27 — keep minimal here.

---

### `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` (options, config)

**Analog:** `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs` (sealed POCO, seconds/days int with defaults, doc-comment per field).

**read_first:** `src/BaseApi.Core/Configuration/RedisProjectionOptions.cs:14-31`

```csharp
public sealed class RedisProjectionOptions
{
    /// <summary>Processor-key TTL in days (D-08, default 100). ...</summary>
    public int ProcessorKeyTtlDays { get; set; } = 100;
    ...
}
```
> Processor binds four independent seconds-int values: `IntervalSeconds`, `TtlSeconds` (CONFIG-01 — two independent values), `RequestTimeoutSeconds` (~5-10s), `BackoffCapSeconds` (~30s). Bake sensible defaults; bind via `cfg.GetSection("Processor")`. See Shared Patterns → Fail-fast config for any keys treated as required.

---

### `src/BaseProcessor.Core/BaseProcessor.Core.csproj` (project/config)

**Analog:** `tests/BaseApi.Tests/BaseApi.Tests.csproj:55-128` (CPM no-`Version=` `PackageReference` + `ProjectReference` conventions).

**read_first:** `26-RESEARCH.md` "Installation" block + `BaseApi.Tests.csproj:114-128`

CPM — no `Version=` attributes. Direct `<PackageReference Include="MassTransit" />` + `<PackageReference Include="StackExchange.Redis" />`; `<ProjectReference>` to `..\BaseConsole.Core\BaseConsole.Core.csproj` + `..\Messaging.Contracts\Messaging.Contracts.csproj`. **NOT** `BaseApi.Service` (firewall — RESEARCH Integration Points). Stay on MassTransit 8.5.5 (RESEARCH Pitfall 6).

---

### `tests/BaseApi.Tests/Processor/*Facts.cs` (test) + MODIFIED `BaseApi.Tests.csproj`

**Analogs:** `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` (in-memory `AddMassTransitTestHarness` — drives Loop A/B), `tests/BaseApi.Tests/Composition/RedisFixture.cs` (real-Redis round-trip + `Track` cleanup), `tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs` (descriptor-inspection + dead-Redis soft-dep boot).

**read_first:** `ConsoleCorrelationFilterTests.cs:51-101`, `RedisFixture.cs:34-83`, `ConsoleTestHostFixture.cs:60-93`, `BaseApi.Tests.csproj:114-128`

**Harness wiring** (`ConsoleCorrelationFilterTests.cs:60-95`) — register a stub responder (sequence NotFound→NotFound→Found per RESEARCH Wave 0), start/stop, assert `Consumed.Any<T>`:
```csharp
await using var provider = new ServiceCollection()
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<ProbeConsumer>();
        x.UsingInMemory((ctx, cfg) => { ...; cfg.ConfigureEndpoints(ctx); });
    })
    .BuildServiceProvider(true);
var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
try { await harness.Bus.Publish(...); Assert.True(await harness.Consumed.Any<ProbeMessage>(ct)); }
finally { await harness.Stop(ct); }
```

**Redis round-trip + track-for-cleanup** (`RedisFixture.cs:46-55,64-82`) — `Track(L2ProjectionKeys.Processor(testProcessorId))` so the triple-SHA close gate sees BEFORE==AFTER (RESEARCH Wave 0 + MEMORY phase-20 gate discipline). The LIVE-05 round-trip test asserts the written JSON deserializes via `JsonSerializer.Deserialize<ProcessorProjection>` AND passes the **real** `ProcessorLivenessValidator.ValidateAsync` as live; advance `FakeTimeProvider` past `interval*2` → reader sees `stale`.

**Descriptor-inspection for `AddBaseProcessor`** (`ConsoleTestHostFixture.cs:65,79`) — capture `builder.Services.ToList()` before `Build()` to assert the graph (request clients, orchestrator, heartbeat registered; `StartupCompletionService` removed).

**csproj modification:** add `<ProjectReference Include="..\..\src\BaseProcessor.Core\BaseProcessor.Core.csproj" />` to the block at lines 114-128 (mirrors how Phase 18 added BaseConsole.Core line 123, Phase 19 added Orchestrator line 127). `FakeTimeProvider` (line 74) + `StackExchange.Redis` (line 105) + `NSubstitute` (line 103) are already referenced — no new packages.

---

## Shared Patterns

### Fail-fast config (`cfg.Require`)
**Source:** `src/BaseConsole.Core/Configuration/RequiredConfig.cs:20-23`
**Apply to:** Any `ProcessorLivenessOptions` key treated as required; the bus already reads RabbitMq via `cfg.Require` in `AddBaseConsoleMessaging`.
```csharp
public static string Require(this IConfiguration cfg, string key)
    => cfg[key] ?? throw new InvalidOperationException(
        $"Required configuration key '{key}' is missing. ...");
```
> Names the missing KEY, never the value (V7 / Information-Disclosure mitigation, RESEARCH Security Domain).

### Same clock as the reader (`TimeProvider`)
**Source:** `RedisProjectionWriter.cs:60` + `ProcessorLivenessValidator.cs:30` (both `_clock.GetUtcNow().UtcDateTime`)
**Apply to:** `ProcessorLivenessHeartbeat` (timestamp) and any retry-delay (`Task.Delay(delay, clock, ct)`). Inject `TimeProvider`; `FakeTimeProvider`-testable.

### Frozen L2 shape — reuse, never redefine (D-09)
**Source:** `ProcessorProjection.cs:14-17`, `LivenessProjection.cs:11-14`, `L2ProjectionKeys.cs:37`, `LivenessStatus.cs:11`
**Apply to:** `ProcessorLivenessHeartbeat` only.
```csharp
public sealed record ProcessorProjection(
    [property: JsonPropertyName("inputDefinition")]  string? InputDefinition,
    [property: JsonPropertyName("outputDefinition")] string? OutputDefinition,
    [property: JsonPropertyName("liveness")]         LivenessProjection Liveness);
```
> The `[property: JsonPropertyName]` targets are load-bearing (RESEARCH Pitfall 1). Building a parallel writer DTO is FORBIDDEN (D-09).

### Dual-response contracts + endpoint constants (consume, do not modify)
**Source:** `ProcessorQueries.cs:6-14` (`GetProcessorBySourceHash`/`ProcessorIdentityFound`/`ProcessorIdentityNotFound`, `GetSchemaDefinition`/`SchemaDefinitionFound`/`SchemaDefinitionNotFound`), `ProcessorQueues.cs:10-11` (`IdentityQuery`/`SchemaQuery`)
**Apply to:** `BaseProcessorServiceCollectionExtensions` (request-client registration) + `ProcessorStartupOrchestrator` (the queries).

### Startup-gate re-pointing (D-02)
**Source:** `Orchestrator/Program.cs:61-68` (removal) + `HydrationBackgroundService.cs:64` (`gate.MarkReady()` on completion)
**Apply to:** `BaseProcessorServiceCollectionExtensions` (removal loop) + `ProcessorStartupOrchestrator` (`gate.MarkReady()` after Healthy).

---

## No Analog Found

Files with no close in-repo match — planner uses RESEARCH.md patterns (verified against MassTransit official docs / BCL / PROJECT.md):

| File / surface | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `AddRequestClient<T>` / `GetResponse<TFound,TNotFound>()` usage | request-response | request-response | First `IRequestClient` request/response on the console side (RPC-04). Grep confirms NO `IRequestClient` exists anywhere in `src/`. Use RESEARCH §2/§3 + official MassTransit 8.5.5 docs; confirm `exchange:` scheme + overload arg-order with a Wave-0 harness round-trip (RESEARCH A2/A5). |
| `AssemblyMetadataSourceHashProvider.Get()` body | utility | transform | Reflection over `AssemblyMetadataAttribute` — standard BCL, no in-repo precedent. Follow RESEARCH §4 (throw-when-absent). |
| `BaseProcessor.ProcessAsync` signature | abstract seam | n/a | Signature `Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` is locked by PROJECT.md:32; no existing abstract-processor base to copy. Declared here, invoked Phase 27. |

> Interface/record/options *shapes* for the above still copy in-repo conventions (`IStartupGate` for the interface+sealed-impl idiom; `LivenessProjection` for the record shape) — only the method bodies/signatures are net-new.

## Metadata

**Analog search scope:** `src/Orchestrator/`, `src/BaseConsole.Core/`, `src/BaseApi.Service/Features/Orchestration/`, `src/Messaging.Contracts/`, `src/BaseApi.Core/Configuration/`, `tests/BaseApi.Tests/{Console,Composition}/`
**Files read for excerpts:** 16 (Program.cs, HydrationBackgroundService.cs, both BaseConsole DI extensions, IStartupGate.cs, RequiredConfig.cs, ProcessorProjection/LivenessProjection/L2ProjectionKeys/LivenessStatus.cs, ProcessorQueries/ProcessorQueues.cs, ProcessorLivenessValidator.cs, RedisProjectionWriter.cs + RedisProjectionKeys.cs grep, RedisProjectionOptions.cs, ConsoleCorrelationFilterTests.cs, RedisFixture.cs, ConsoleTestHostFixture.cs, BaseApi.Tests.csproj)
**Pattern extraction date:** 2026-06-01
