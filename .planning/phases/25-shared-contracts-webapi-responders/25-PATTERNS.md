# Phase 25: Shared Contracts + WebApi Responders - Pattern Map

**Mapped:** 2026-06-01
**Files analyzed:** 13 (8 new, 5 modified)
**Analogs found:** 13 / 13 (all have a verified in-repo analog)

> Every file in this phase has an exact or near-exact in-repo analog. This is composition of five proven patterns, not invention. All excerpts below are verbatim from the verified source — copy structure, adapt names/fields per the "Adaptation" note.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/Projections/ProcessorProjection.cs` (NEW — relocated) | model (contract record) | transform (STJ projection) | `src/Messaging.Contracts/Projections/LivenessProjection.cs` | exact (it's a verbatim lift) |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (MODIFY — add `ExecutionData`) | utility (SoT key builder) | transform (string format) | same file — existing `Root`/`Processor` builders | exact |
| `src/Messaging.Contracts/Projections/LivenessStatus.cs` (NEW) | config (SoT const class) | n/a | `src/Messaging.Contracts/OrchestratorQueues.cs` | exact (static const SoT) |
| `src/Messaging.Contracts/ProcessorQueries.cs` (NEW — request/response record pairs) | model (bus contract records) | request-response | `src/Messaging.Contracts/ExecutionResult.cs` | role-match (plain wire record) |
| `src/Messaging.Contracts/ProcessorQueues.cs` (NEW — queue-name constants) | config (SoT const class) | n/a | `src/Messaging.Contracts/OrchestratorQueues.cs` | exact |
| `src/BaseApi.Service/Features/.../GetProcessorBySourceHashConsumer.cs` (NEW) | service (MassTransit consumer) | request-response (dual) | `src/Orchestrator/Consumers/ResultConsumer.cs` | role-match |
| `src/BaseApi.Service/Features/.../GetSchemaDefinitionConsumer.cs` (NEW) | service (MassTransit consumer) | request-response (dual) | `src/Orchestrator/Consumers/ResultConsumer.cs` | role-match |
| `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (MODIFY — add hooks) | config (DI bus join) | event-driven (bus) | `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` | exact (hook-shape donor) |
| `src/BaseApi.Service/Program.cs` (MODIFY — supply hooks) | config (composition root) | n/a | `src/Orchestrator/Program.cs` | role-match (hook call site) |
| `tests/.../L2ProjectionKeysTests.cs` (MODIFY — add `ExecutionData` golden) | test | transform | same file (existing golden tests) | exact |
| `tests/.../LivenessStatusTests.cs` (NEW) | test | n/a | `L2ProjectionKeysTests.cs` (golden-pin discipline) | role-match |
| `tests/.../ProcessorResponderTests.cs` (NEW) | test | request-response (harness) | `tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs` | role-match |
| `tests/.../SchemaResponderTests.cs` (NEW) | test | request-response (harness) | `tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs` | role-match |

## Pattern Assignments

### `src/Messaging.Contracts/Projections/ProcessorProjection.cs` (model, transform) — D-01

**Analog:** the source file being moved — `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` (verbatim lift). Sibling reference: `LivenessProjection.cs` (already public, same leaf namespace).

**Source record to lift (lines 1-15) — change ONLY namespace + `internal`→`public`, preserve `[property:]` targets verbatim:**
```csharp
using System.Text.Json.Serialization;
using Messaging.Contracts.Projections;   // becomes redundant once moved into this namespace — drop the using

namespace BaseApi.Service.Features.Orchestration.Projection;   // → namespace Messaging.Contracts.Projections;

internal sealed record ProcessorProjection(                    // → public sealed record
    [property: JsonPropertyName("inputDefinition")]  string? InputDefinition,
    [property: JsonPropertyName("outputDefinition")] string? OutputDefinition,
    [property: JsonPropertyName("liveness")]         LivenessProjection Liveness);
```

**Adaptation:**
1. New file at `src/Messaging.Contracts/Projections/ProcessorProjection.cs`; namespace `Messaging.Contracts.Projections`; `public sealed record`.
2. Keep `[property: JsonPropertyName("inputDefinition"/"outputDefinition"/"liveness")]` byte-identical (RESEARCH Pitfall 1/3 — bare attribute on a positional record binds to the ctor param and STJ ignores it).
3. `LivenessProjection` is already public in this namespace → no extra using; the move is dependency-free.
4. Delete the old file at `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs`.
5. Update the ONE reference site (see Shared Pattern: Reference-site update).

---

### `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (utility, transform) — D-02

**Analog:** same file. Existing sibling builders (lines 30-36):
```csharp
public const string Prefix = "skp:";
public static string ParentIndex() => Prefix;
public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";
public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId}:{stepId}";
public static string Processor(Guid processorId) => $"{Prefix}{processorId}";
```

**Adaptation — add ONE builder (first key with a `data:` discriminator segment):**
```csharp
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";   // skp:data:{entryId:D}
```
Update the class XML-doc `<list>` to add the `ExecutionData: {Prefix}data:{entryId}` bullet, noting the `data:` discriminator makes it the FIRST non-flat key (distinct from `Root`/`Processor` which are byte-identical bare-prefix keys).

---

### `src/Messaging.Contracts/Projections/LivenessStatus.cs` (config, SoT const) — D-03

**Analog:** `src/Messaging.Contracts/OrchestratorQueues.cs` (static const SoT class, lines 8-17).

**Analog structure:**
```csharp
namespace Messaging.Contracts;

public static class OrchestratorQueues
{
    public const string Result = "orchestrator-result";
}
```

**Adaptation — new tiny static class in `Messaging.Contracts.Projections`:**
```csharp
namespace Messaging.Contracts.Projections;

public static class LivenessStatus
{
    public const string Healthy = "Healthy";   // single SoT — writer (Phase 26) + readers cannot desync
}
```
Note: the consumer field is `LivenessProjection.Status` (the third positional param, `[property: JsonPropertyName("status")]`). Const chosen over hanging it off the record (CONTEXT D-03).

---

### `src/Messaging.Contracts/ProcessorQueues.cs` (config, SoT const) — D-06

**Analog:** `src/Messaging.Contracts/OrchestratorQueues.cs` (exact mirror — same static-class SoT, bare short-names with NO `queue:`/`exchange:` scheme prefix; the sender prepends it).

**Analog (full, lines 8-17):**
```csharp
public static class OrchestratorQueues
{
    /// <summary>Bind it as ReceiveEndpoint(OrchestratorQueues.Result); a sender prepends the queue: scheme.</summary>
    public const string Result = "orchestrator-result";
}
```

**Adaptation — new class (exact name is discretion; RESEARCH suggests `ProcessorQueues`) in `Messaging.Contracts`:**
```csharp
namespace Messaging.Contracts;

public static class ProcessorQueues
{
    public const string IdentityQuery = "processor-identity-query";   // bare short-name, no scheme
    public const string SchemaQuery   = "schema-definition-query";
}
```
The endpoint binds `ReceiveEndpoint(ProcessorQueues.IdentityQuery, ...)`; Phase 26's request client targets `exchange:{IdentityQuery}`.

---

### `src/Messaging.Contracts/ProcessorQueries.cs` (model, request-response records) — D-04

**Analog:** `src/Messaging.Contracts/ExecutionResult.cs` — plain wire record, NO `[JsonPropertyName]` (default STJ, mirrors `EntryStepDispatch`), no MassTransit attributes/usings (leaf needs NO MassTransit package ref — RESEARCH A2).

**Analog structure (lines 3-11):**
```csharp
// NOTE: bus envelope — NO [JsonPropertyName], default STJ serialization (mirrors EntryStepDispatch).
public sealed record ExecutionResult(
    Guid WorkflowId,
    Guid StepId,
    Guid ProcessorId,
    StepOutcome Outcome) : IExecutionCorrelated { ... }
```

**Adaptation — TWO request records + four response records (found/not-found per query). Field sources are direct projections of existing DTOs (no reshaping):**
```csharp
namespace Messaging.Contracts;

// RPC-01: identity-by-source-hash — found fields are a direct copy of ProcessorReadDto's
// { Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? } (see ProcessorDtos.cs:42-54).
public sealed record GetProcessorBySourceHash(string SourceHash);
public sealed record ProcessorIdentityFound(
    Guid Id, Guid? InputSchemaId, Guid? OutputSchemaId, Guid? ConfigSchemaId);
public sealed record ProcessorIdentityNotFound(string SourceHash);

// RPC-02: schema-definition-by-id — found carries SchemaReadDto.Definition (string) (see SchemaDtos.cs:33-42).
// Read is by Id (Guid) via BaseService.GetByIdAsync (RESEARCH A1).
public sealed record GetSchemaDefinition(Guid SchemaId);
public sealed record SchemaDefinitionFound(string Definition);
public sealed record SchemaDefinitionNotFound(Guid SchemaId);
```
Discretion (CONTEXT D-04): one file vs split-per-query; exact not-found record names. Keep records plain (no MassTransit attrs) so the leaf stays MassTransit-free.

---

### `src/BaseApi.Service/Features/.../GetProcessorBySourceHashConsumer.cs` (service, dual-response) — RPC-01

**Analog:** `src/Orchestrator/Consumers/ResultConsumer.cs` (sealed `IConsumer<T>`, primary-ctor DI of a Service, single `Consume(ConsumeContext<T>)` method, `context.CancellationToken` threaded to the read). The dual-response shape (`RespondAsync<TFound>`/`RespondAsync<TNotFound>`) is from RESEARCH Pattern 1.

**Analog imports + ctor + method shape (lines 1-7, 37-44):**
```csharp
using MassTransit;
using Microsoft.Extensions.Logging;
// ...
public sealed class ResultConsumer(
    IWorkflowL1Store store, /* ...deps... */ ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
    {
        var m = context.Message;
        // ... reads store, threads context.CancellationToken ...
    }
}
```

**Backing read (verified) — `ProcessorService.GetBySourceHashAsync(string, ct)` THROWS `NotFoundException` on miss (ProcessorService.cs:66-82):**
```csharp
public async Task<ProcessorReadDto> GetBySourceHashAsync(string sourceHash, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(sourceHash)) throw new NotFoundException(nameof(ProcessorEntity), sourceHash ?? "(null)");
    var entity = await DbContext.Set<ProcessorEntity>().AsNoTracking()
        .FirstOrDefaultAsync(p => p.SourceHash == sourceHash, ct);
    if (entity is null) throw new NotFoundException(nameof(ProcessorEntity), sourceHash);
    return _mapper.ToRead(entity);
}
```

**Adaptation — thin adapter; catch `NotFoundException` (`BaseApi.Core.Exceptions`) to drive the not-found branch:**
```csharp
using BaseApi.Core.Exceptions;
using BaseApi.Service.Features.Processor;
using MassTransit;
using Messaging.Contracts;

namespace BaseApi.Service.Features.Processor.Responders;   // folder/namespace is discretion

public sealed class GetProcessorBySourceHashConsumer(ProcessorService processors)
    : IConsumer<GetProcessorBySourceHash>
{
    public async Task Consume(ConsumeContext<GetProcessorBySourceHash> context)
    {
        try
        {
            var p = await processors.GetBySourceHashAsync(context.Message.SourceHash, context.CancellationToken);
            await context.RespondAsync<ProcessorIdentityFound>(
                new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId));
        }
        catch (NotFoundException)
        {
            await context.RespondAsync<ProcessorIdentityNotFound>(
                new ProcessorIdentityNotFound(context.Message.SourceHash));
        }
    }
}
```
Stateless query responder — NO correlation filters (CONTEXT discretion confirmed; those live in `BaseConsole.Core` which Core/Service must not reference). NO `ConsumerDefinition` needed here unless retry is wanted (see optional `ResultConsumerDefinition` analog below).

---

### `src/BaseApi.Service/Features/.../GetSchemaDefinitionConsumer.cs` (service, dual-response) — RPC-02

**Analog:** same as above (`ResultConsumer.cs` shape + RESEARCH Pattern 1).

**Backing read (verified) — `SchemaService` inherits `BaseService.GetByIdAsync(Guid, ct)` which THROWS `NotFoundException` (SchemaService.cs is empty body; BaseService.cs:82-87):**
```csharp
public async Task<TRead> GetByIdAsync(Guid id, CancellationToken ct)
{
    var entity = await _repo.GetAsync(id, ct);
    if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
    return _mapper.ToRead(entity);
}
```

**Adaptation:**
```csharp
public sealed class GetSchemaDefinitionConsumer(SchemaService schemas)
    : IConsumer<GetSchemaDefinition>
{
    public async Task Consume(ConsumeContext<GetSchemaDefinition> context)
    {
        try
        {
            var s = await schemas.GetByIdAsync(context.Message.SchemaId, context.CancellationToken);
            await context.RespondAsync<SchemaDefinitionFound>(new SchemaDefinitionFound(s.Definition));
        }
        catch (NotFoundException)
        {
            await context.RespondAsync<SchemaDefinitionNotFound>(new SchemaDefinitionNotFound(context.Message.SchemaId));
        }
    }
}
```
`SchemaReadDto.Definition` is the `string` field (SchemaDtos.cs:38).

---

### (Optional) `ConsumerDefinition` for the responders — analog `ResultConsumerDefinition.cs`

Only if a retry/endpoint-name binding via definition is preferred over inline `ReceiveEndpoint`. RESEARCH/D-06 favor explicit `ReceiveEndpoint(ProcessorQueues.X, ...)` inline in the Service hook, NOT a definition. Analog for the definition route (ResultConsumerDefinition.cs:20-33):
```csharp
public sealed class ResultConsumerDefinition : ConsumerDefinition<ResultConsumer>
{
    public ResultConsumerDefinition() => EndpointName = OrchestratorQueues.Result;
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint, ...)
        => endpoint.UseMessageRetry(r => r.Immediate(3));
}
```
Default: NO definition — bind explicit endpoints in the Service hook (D-06). Responder retry posture is discretion (client-side retry is Phase 26).

---

### `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (MODIFY — add hooks) — D-05/D-06

**Analog (hook-shape donor):** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — already exposes the EXACT two seams needed: `Action<IBusRegistrationConfigurator> configureConsumers` + `Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureBus`.

**Donor signature + invocation (BaseConsole.Core, lines 34-60):**
```csharp
public static IServiceCollection AddBaseConsoleMessaging(
    this IServiceCollection services, IConfiguration cfg,
    Action<IBusRegistrationConfigurator> configureConsumers,
    Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureBus = null)
{
    services.AddMassTransit(x =>
    {
        configureConsumers(x);                       // AddConsumer<T>() seam
        x.UsingRabbitMq((ctx, c) =>
        {
            c.Host(rabbitHost, h => { h.Username(rabbitUser); h.Password(rabbitPass); });
            configureBus?.Invoke(ctx, c);            // ReceiveEndpoint(...).ConfigureConsumer<T>(ctx) seam
            c.ConfigureEndpoints(ctx);               // <- BaseApi MUST NOT copy this (D-06 anti-pattern)
        });
    });
}
```

**Current `AddBaseApiMessaging` to extend (lines 44-70) — note the load-bearing health block + publish-only comment:**
```csharp
public static IServiceCollection AddBaseApiMessaging(this IServiceCollection services, IConfiguration cfg)
{
    var host = cfg.Require("RabbitMq:Host");
    var port = cfg.GetValue<ushort>("RabbitMq:Port", 5672);
    var user = cfg.Require("RabbitMq:Username");
    var pass = cfg.Require("RabbitMq:Password");
    services.AddMassTransit(bus =>
    {
        bus.ConfigureHealthCheckOptions(o => { o.MinimalFailureStatus = HealthStatus.Degraded; });  // MUST NOT CHANGE (MSG-WEBAPI-04)
        bus.UsingRabbitMq((context, busCfg) =>
        {
            busCfg.Host(host, port, "/", h => { h.Username(user); h.Password(pass); });
            // Publish-only — NO ConfigureEndpoints, NO consumers, NO correlation filters (D-02).
        });
    });
    return services;
}
```

**Adaptation — add TWO optional null-default hooks (Pitfall 1: `ReceiveEndpoint` needs `context` only available in the `UsingRabbitMq` closure, so a single `configureConsumers` hook is insufficient — use the BaseConsole two-seam shape):**
```csharp
public static IServiceCollection AddBaseApiMessaging(
    this IServiceCollection services, IConfiguration cfg,
    Action<IBusRegistrationConfigurator>? configureConsumers = null,                              // D-05: AddConsumer<T> seam
    Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureEndpoints = null)  // D-06: ReceiveEndpoint seam
{
    // ...host/port/user/pass unchanged...
    services.AddMassTransit(bus =>
    {
        bus.ConfigureHealthCheckOptions(o => { o.MinimalFailureStatus = HealthStatus.Degraded; });  // BYTE-IDENTICAL — do not touch
        configureConsumers?.Invoke(bus);                                                            // no-op default = publish-only
        bus.UsingRabbitMq((context, busCfg) =>
        {
            busCfg.Host(host, port, "/", h => { h.Username(user); h.Password(pass); });
            configureEndpoints?.Invoke(context, busCfg);   // explicit ReceiveEndpoints supplied by Service; NO ConfigureEndpoints
        });
    });
    return services;
}
```
**Firewall (CONTRACT-01/D-05):** Core keeps referencing `Messaging.Contracts` + MassTransit ONLY — hooks are typed in MassTransit interfaces, never the concrete consumer types, so Core never names `BaseApi.Service`. Do NOT add `ConfigureEndpoints(context)` (D-06 anti-pattern — the BaseConsole donor uses it; BaseApi must not). Do NOT touch `o.MinimalFailureStatus`/`o.Tags` (Pitfall 2).

---

### `src/BaseApi.Service/Program.cs` (MODIFY — supply hooks) — D-05/D-06

**Analog:** `src/Orchestrator/Program.cs` (the hook call site — passes a `configureConsumers` lambda with `AddConsumer<T>(...)` into `AddBaseConsoleMessaging`, lines 29-42).

**Analog call site:**
```csharp
builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x =>
    {
        x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()...;
        x.AddConsumer<ResultConsumer, ResultConsumerDefinition>();
    });
```

**Current BaseApi call site (Program.cs:8):**
```csharp
builder.Services.AddBaseApiMessaging(builder.Configuration);   // Phase 19 publish-only
```

**Adaptation — supply both hooks (consumers + explicit endpoints keyed off `ProcessorQueues`). Endpoint binding mirrors RESEARCH Pattern 3 / ResultConsumeTests harness `ReceiveEndpoint(name, e => e.ConfigureConsumer<T>(ctx))`:**
```csharp
builder.Services.AddBaseApiMessaging(builder.Configuration,
    configureConsumers: x =>
    {
        x.AddConsumer<GetProcessorBySourceHashConsumer>();
        x.AddConsumer<GetSchemaDefinitionConsumer>();
    },
    configureEndpoints: (context, busCfg) =>
    {
        busCfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
            e => e.ConfigureConsumer<GetProcessorBySourceHashConsumer>(context));
        busCfg.ReceiveEndpoint(ProcessorQueues.SchemaQuery,
            e => e.ConfigureConsumer<GetSchemaDefinitionConsumer>(context));
    });
```
Discretion: the lambda may instead live in a Service-owned extension (mirroring the `AddAppFeatures` aggregator in `Composition/AppFeatures.cs`) to keep `Program.cs` thin. `ProcessorService`/`SchemaService` are already DI-registered via `AddAppFeatures()` (Program.cs:9 → `AddProcessorFeature`/`AddSchemaFeature`), so the consumers resolve them by ctor injection.

---

### `tests/.../L2ProjectionKeysTests.cs` (MODIFY — add golden) — CONTRACT-02

**Analog:** same file (existing golden-pin discipline, lines 21-52). Each test asserts a literal byte string against a known GUID.

**Existing pattern:**
```csharp
[Fact]
public void Root_Produces_Prefix_Plus_HyphenatedGuid()
    => Assert.Equal("skp:11111111-1111-1111-1111-111111111111", L2ProjectionKeys.Root(Workflow));
```

**Adaptation — add golden + anti-collision (RESEARCH Pitfall 5):**
```csharp
[Fact]
public void ExecutionData_Produces_Prefix_Data_Discriminator_Plus_HyphenatedGuid()
{
    var entryId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    Assert.Equal("skp:data:55555555-5555-5555-5555-555555555555", L2ProjectionKeys.ExecutionData(entryId));
}

[Fact]
public void ExecutionData_Is_Distinct_From_Root_And_Processor()
{
    var g = Guid.Parse("66666666-6666-6666-6666-666666666666");
    Assert.NotEqual(L2ProjectionKeys.Root(g),      L2ProjectionKeys.ExecutionData(g));
    Assert.NotEqual(L2ProjectionKeys.Processor(g), L2ProjectionKeys.ExecutionData(g));
}
```

---

### `tests/.../LivenessStatusTests.cs` (NEW) — CONTRACT-03

**Analog:** `L2ProjectionKeysTests.cs` (trivial golden-pin; `using Messaging.Contracts.Projections; using Xunit;`).

**Adaptation:**
```csharp
[Fact]
public void Healthy_Equals_Literal_Healthy()
    => Assert.Equal("Healthy", LivenessStatus.Healthy);
```

---

### `tests/.../ProcessorResponderTests.cs` + `SchemaResponderTests.cs` (NEW) — RPC-01/02

**Analog:** `tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs` (in-memory `AddMassTransitTestHarness` + `UsingInMemory` + `ReceiveEndpoint(name, e => e.ConfigureConsumer<T>(ctx))` + `ITestHarness`).

**Analog harness scaffold (lines 39-54):**
```csharp
new ServiceCollection()
    .AddLogging()
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<CapturingDispatchConsumer>();
        x.UsingInMemory((ctx, cfg) =>
        {
            cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
            cfg.ConfigureEndpoints(ctx);
        });
    })
    .BuildServiceProvider(true);
// ... var harness = provider.GetRequiredService<ITestHarness>(); await harness.Start();
```

**Adaptation — add a stub/real `ProcessorService` (or `SchemaService`) to DI, register the responder consumer, bind it on `ProcessorQueues.IdentityQuery`/`SchemaQuery`, and assert found AND not-found via the request client (RESEARCH Code Examples; confirm `harness.GetRequestClient<T>()` accessor for MT 8.5.5 at plan time, A4):**
```csharp
.AddSingleton(processorServiceStub)
.AddMassTransitTestHarness(x =>
{
    x.AddConsumer<GetProcessorBySourceHashConsumer>();
    x.UsingInMemory((ctx, cfg) =>
        cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
            e => e.ConfigureConsumer<GetProcessorBySourceHashConsumer>(ctx)));
})
// ...
var client = harness.GetRequestClient<GetProcessorBySourceHash>();
var found = await client.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(new GetProcessorBySourceHash("<known>"));
Assert.True(found.Is(out Response<ProcessorIdentityFound> _));
var miss  = await client.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(new GetProcessorBySourceHash("<unknown>"));
Assert.True(miss.Is(out Response<ProcessorIdentityNotFound> _));
```
Stub note: the responder catches `NotFoundException` from the real service. A stub `ProcessorService` is hard to fake (concrete `BaseService` subclass); prefer a real service over an in-memory `BaseDbContext`/repo, OR refactor is out of scope — plan time should decide (a thin seam over the read may be simpler than constructing the full service). The found-path needs a seeded row; the not-found path needs an unseeded hash.

## Shared Patterns

### Reference-site update for the CONTRACT-01 move (the only blast radius)
**Source of edit:** `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs`
**Apply to:** the single consumer of `ProcessorProjection` after the move.
The file already imports BOTH namespaces (lines 2-3):
```csharp
using BaseApi.Service.Features.Orchestration.Projection;   // ← DELETE this line (the projection moved out)
using Messaging.Contracts.Projections;                     // ← already present; now also supplies ProcessorProjection
```
The `JsonSerializer.Deserialize<ProcessorProjection>(raw!)` call (line 44) is otherwise unchanged. RESEARCH: only TWO `src/` reference sites — the record (deleted) + this validator. Re-grep `tests/` at plan time (A5) before deleting the old type, in case a fact constructs it.

### Static-class single-source-of-truth (D-03/D-06)
**Source:** `src/Messaging.Contracts/OrchestratorQueues.cs`, `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`
**Apply to:** `LivenessStatus.cs`, `ProcessorQueues.cs` — `public static class` of `public const string`; bare short-names with NO scheme prefix; writer + reader consume one symbol.

### Plain leaf wire records (D-04)
**Source:** `src/Messaging.Contracts/ExecutionResult.cs` (+ `EntryStepDispatch.cs`)
**Apply to:** all six `ProcessorQueries.cs` records — `public sealed record`, NO `[JsonPropertyName]`, NO MassTransit attrs/usings. The leaf needs NO MassTransit package ref (RESEARCH A2). Contrast: `ProcessorProjection`/`LivenessProjection` (Redis projections) DO carry `[property: JsonPropertyName]` — wire records do not.

### Dual-response consumer (RPC-01/02)
**Source:** RESEARCH Pattern 1 + `src/Orchestrator/Consumers/ResultConsumer.cs` (IConsumer shape) + verified `NotFoundException`-on-miss reads (`ProcessorService.cs:66-82`, `BaseService.cs:82-87`)
**Apply to:** both responder consumers — `try { read; RespondAsync<TFound> } catch (NotFoundException) { RespondAsync<TNotFound> }`; thread `context.CancellationToken`; NO correlation filters.

### Bus-join hook seam (D-05/D-06)
**Source:** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:34-60`
**Apply to:** `AddBaseApiMessaging` — two null-default hooks (`configureConsumers` + `configureEndpoints`) typed in MassTransit interfaces only; preserve the `Degraded` health block byte-identical; do NOT copy the donor's `ConfigureEndpoints(ctx)` line (D-06). Firewall: Core never names a concrete consumer type.

### In-memory harness round-trip (test)
**Source:** `tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs:39-54`
**Apply to:** both responder test files — `AddMassTransitTestHarness` + `UsingInMemory` + `ReceiveEndpoint(ProcessorQueues.X, e => e.ConfigureConsumer<T>(ctx))` + `ITestHarness.Start()`.

### Firewall regression guard (RPC-03)
**Source:** `tests/.../Observability/HealthEndpointsTests.cs` — `Health_Ready_Returns_200_When_Broker_Dead`, `Health_Live_Returns_200_When_Broker_Dead` (already exist; per RESEARCH lines 220-252)
**Apply to:** re-run after the bus-join change; they must stay green (Pitfall 2). Also a new/existing csproj-reference firewall assertion: `BaseApi.Core.csproj` has no `ProjectReference` to `BaseApi.Service`/`BaseConsole.Core`.

## No Analog Found

None. Every file has a verified in-repo analog.

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| — | — | — | All 13 files map to an existing pattern. |

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/BaseApi.Core/`, `src/BaseApi.Service/Features/{Processor,Schema,Orchestration}/`, `src/BaseConsole.Core/DependencyInjection/`, `src/Orchestrator/{Program.cs,Consumers/}`, `tests/BaseApi.Tests/{Orchestrator,Features/Orchestration/Projection}/`
**Files scanned (Read):** 16
**Pattern extraction date:** 2026-06-01
