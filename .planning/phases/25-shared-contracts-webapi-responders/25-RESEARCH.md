# Phase 25: Shared Contracts + WebApi Responders - Research

**Researched:** 2026-06-01
**Domain:** C#/.NET 8 backend — MassTransit 8.5.5 request/response responders + shared-contract leaf extracts
**Confidence:** HIGH

## Summary

Phase 25 has two near-independent workstreams: (1) **leaf contract extracts** in `Messaging.Contracts` — relocate `ProcessorProjection` to public, add an `ExecutionData` L2 key builder, add a `"Healthy"` constant, and define two request/response record pairs + their queue-name constants; and (2) **WebApi responder host** — extend the publish-only `AddBaseApiMessaging` join with an optional consumer/endpoint hook so `BaseApi.Service` can register two MassTransit consumers (`GetProcessorBySourceHash`, `GetSchemaDefinition`) on explicit `ReceiveEndpoint`s, each answering with a typed found-OR-not-found response.

The good news: every pattern this phase needs **already exists in the repo** and is verified. The MassTransit dual-response responder pattern (`context.RespondAsync<TFound>` / `context.RespondAsync<TNotFound>`), the explicit `ReceiveEndpoint(name, e => e.ConfigureConsumer<T>(context))` shape, the optional `Action<IBusRegistrationConfigurator>` hook (mirrored from `BaseConsole.Core`'s `AddBaseConsoleMessaging`), the shared queue-name static-class (`OrchestratorQueues`), the L2 key golden-test discipline, and the in-memory `AddMassTransitTestHarness` round-trip pattern are all present and proven. There is no novel infrastructure — this is composition of established patterns.

The single highest-risk area is **preserving the firewall + Degraded-cap + publish-only-default invariants** while adding the responder hook to `BaseApi.Core`. The hook must be typed only in MassTransit/Contracts types, default to no-op (publish-only unchanged), and not touch the `ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded)` block.

**Primary recommendation:** Mirror `BaseConsole.Core.AddBaseConsoleMessaging`'s `Action<IBusRegistrationConfigurator> configureConsumers` hook shape into `AddBaseApiMessaging` (optional/nullable, default null), have `BaseApi.Service` own the two consumer classes + their `ReceiveEndpoint` bindings via that hook, and use `context.RespondAsync<TFound>(...)` / `context.RespondAsync<TNotFound>(...)` inside each consumer keyed off the existing `ProcessorService.GetBySourceHashAsync` / `SchemaService.GetByIdAsync` NotFound contracts.

## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01 (CONTRACT-01):** Relocate `ProcessorProjection` from `BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` (`internal sealed record`) into `Messaging.Contracts.Projections` as `public`. Single shared type — no duplicate. Its only dependency `LivenessProjection` is already public in the leaf (clean lift). The `[property: JsonPropertyName("inputDefinition"/"outputDefinition"/"liveness")]` targets are load-bearing and preserved verbatim. Update the `BaseApi.Service` reference site(s).
- **D-02 (CONTRACT-02):** Add `L2ProjectionKeys.ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}"` producing `skp:data:{entryId:D}`. First key with a `data:` discriminator segment. Pin with a golden test.
- **D-03 (CONTRACT-03):** Define `"Healthy"` as `public const string Healthy = "Healthy"` in a new tiny `LivenessStatus` static class in `Messaging.Contracts.Projections`. Mirrors `L2ProjectionKeys` SoT shape. (Chosen over hanging the const off `LivenessProjection`.)
- **D-04 (RPC-01/02 — not-found shape):** Idiomatic MassTransit dual-response. Each query defines a distinct found record AND a distinct not-found record. `GetProcessorBySourceHash` found → `{ Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` (direct from `ProcessorReadDto`); `GetSchemaDefinition` found → `{ Definition }` (string). Each plus a separate not-found record. Distinct types let Phase 26 pattern-match cleanly.
- **D-05 (RPC-03 — Core hosts responders):** Keep `AddBaseApiMessaging` in `BaseApi.Core`; extend with an optional consumer/endpoint hook (`Action<IBusRegistrationConfigurator>? configureConsumers = null`) applied inside the existing `AddMassTransit`. `BaseApi.Service` owns the two consumer classes (they reference `ProcessorService`/`SchemaService`) and passes them via the hook. Core references `Messaging.Contracts` + MassTransit only — never `BaseApi.Service`/`BaseConsole.Core` (Phase-19 firewall holds). Default (no hook) stays publish-only; CRUD surface byte-unaffected. Bus health stays capped at `Degraded` (MSG-WEBAPI-04).
- **D-06 (RPC-03 — endpoint/queue naming):** Add shared queue-name constants to `Messaging.Contracts` mirroring `OrchestratorQueues` (bare short-names, no `queue:` scheme prefix). Bind explicit `ReceiveEndpoint`s for the two responders. (Chosen over convention-based `ConfigureEndpoints`.)

### Claude's Discretion

- Correlation filters on the responder side: the publish-only join deliberately omits correlation filters (they live in `BaseConsole.Core`, which Core must not reference). These query responders are stateless and outside the orchestration correlation chain — defaulting to **no** correlation filters keeps the firewall intact. Research **confirms** this is correct (see Pitfall 4).
- Exact consumer class structure, names, namespaces, and the DI mapping from `ProcessorReadDto`/`SchemaReadDto` into the response records.
- Whether the two response-record pairs share a file or are split per query; exact not-found record naming.
- Retry/timeout posture on the responder side (client-side retry is Phase 26; the responder just answers or returns not-found).

### Deferred Ideas (OUT OF SCOPE)

- Processor-side consumption (`IRequestClient` usage, identity/schema resolution, retry loops) — Phase 26 (RPC-04, IDENT-03/04, SCHEMA-01/02).
- Actual writes under `skp:data:{entryId}` (execution round-trip read/write) — Phase 27 (EXEC-02/05).
- Writing the liveness key with `status: "Healthy"` (heartbeat worker) — Phase 26 (LIVE-01/04).
- Config re-validation, eviction/cleanup of execution-data keys — FUT-PROC-02.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CONTRACT-01 | `ProcessorProjection` made public + relocated to `Messaging.Contracts.Projections` | Source record at `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs:12`; only 2 reference sites found (the record itself + `ProcessorLivenessValidator.cs:41,44`). `LivenessProjection` already public. Clean lift. |
| CONTRACT-02 | `L2ProjectionKeys.ExecutionData(Guid) => skp:data:{entryId:D}` | `L2ProjectionKeys.cs:36` shows existing builder shape; golden-test template at `tests/.../Projection/L2ProjectionKeysTests.cs`. |
| CONTRACT-03 | `"Healthy"` shared constant in `Messaging.Contracts` | `OrchestratorQueues.cs` is the static-class SoT template; `LivenessProjection.Status` field is the consumer. |
| RPC-01 | WebApi answers `GetProcessorBySourceHash` → identity or not-found | `ProcessorService.GetBySourceHashAsync` (throws `NotFoundException` on miss) + `ProcessorReadDto` field set. Dual-response pattern verified via MassTransit docs. |
| RPC-02 | WebApi answers `GetSchemaDefinition` → `Definition` or not-found | `BaseService.GetByIdAsync(Guid, ct)` (throws `NotFoundException`) + `SchemaReadDto.Definition`. |
| RPC-03 | Contracts in `Messaging.Contracts`; publish-only join extended to host responders; CRUD unaffected | `AddBaseApiMessaging` hook pattern mirrored from `AddBaseConsoleMessaging`; firewall + Degraded-cap preserved; `HealthEndpointsTests.Health_Ready_Returns_200_When_Broker_Dead` is the regression guard. |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Shared contract records (projection, key builder, status const, request/response pairs, queue names) | `Messaging.Contracts` (leaf) | — | Both WebApi (now) and processor (Phase 26) depend on one source of truth; leaf has no project deps except STJ. |
| Bus responder consumer classes (the two query handlers) | `BaseApi.Service` | — | They depend on `ProcessorService`/`SchemaService` which live in Service; Core must not reference Service (firewall). |
| Bus join + endpoint binding plumbing (the hook) | `BaseApi.Core` | — | Core owns `AddMassTransit`/`UsingRabbitMq`; the hook is typed in MassTransit/Contracts only so Service supplies consumers without Core knowing the concrete types. |
| DB reads backing the responders | `BaseApi.Service` (`ProcessorService`/`SchemaService`) → EF Core → Postgres | — | Reuse existing read paths unchanged; responders are thin adapters. |
| Bus health check (capped at Degraded) | `BaseApi.Core` | — | Existing `ConfigureHealthCheckOptions` block — must not be altered by the responder addition. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, consumers, request/response | `[VERIFIED: Directory.Packages.props:137]` — already pinned. Last Apache-2.0 line; do NOT bump to 9.x (commercial). |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport | `[VERIFIED: Directory.Packages.props:138]` |
| MassTransit (test harness) | 8.5.5 | `AddMassTransitTestHarness` / `ITestHarness` in-memory | `[VERIFIED: tests/.../Orchestrator/ResultConsumeTests.cs:41]` — `MassTransit.Testing` namespace already used. |

No new package references are required for Phase 25. MassTransit is pinned (Phase 17) and `BaseApi.Core` already references it. `Messaging.Contracts` references only `System.Text.Json` (via SDK). Verify whether `Messaging.Contracts.csproj` needs a MassTransit reference: the request/response **records** are plain records with no MassTransit attributes (mirroring `EntryStepDispatch`/`ExecutionResult`), so the leaf needs **no** MassTransit dependency. `[VERIFIED: src/Messaging.Contracts/ExecutionResult.cs, EntryStepDispatch.cs — plain records, no MassTransit usings]`

**Version verification:** MassTransit 8.5.5 pin confirmed in `Directory.Packages.props`. No registry call needed — the version is fixed by an explicit license decision documented at `Directory.Packages.props:133-138` `[VERIFIED: Directory.Packages.props]`.

## Architecture Patterns

### System Architecture Diagram

```
                       Messaging.Contracts (leaf — STJ only, no MassTransit)
                       ┌──────────────────────────────────────────────────┐
                       │ Projections/                                       │
                       │   ProcessorProjection  (D-01, moved here, public)  │
                       │   LivenessProjection   (already public)            │
                       │   LivenessStatus.Healthy = "Healthy"  (D-03)       │
                       │   L2ProjectionKeys.ExecutionData(...)  (D-02)      │
                       │ Request/response pairs (D-04):                      │
                       │   GetProcessorBySourceHash / *Found / *NotFound     │
                       │   GetSchemaDefinition       / *Found / *NotFound     │
                       │ ProcessorQueues (or similar) constants  (D-06)     │
                       └───────────────▲──────────────────────▲────────────┘
                                       │ consumed by          │ consumed (Phase 26)
                                       │                      │
   ┌───────────────────────────────────┴────────┐    ┌────────┴─────────────────────┐
   │ BaseApi.Core                                │    │ Processor (Phase 26 — N/A now) │
   │  AddBaseApiMessaging(cfg,                    │    │  IRequestClient<GetProcessor…> │
   │     configureConsumers?  ← D-05 hook)       │    │  sends to exchange:{queue}     │
   │   AddMassTransit(bus =>                       │   └────────────────────────────────┘
   │     ConfigureHealthCheckOptions(Degraded) ←─ MUST NOT CHANGE (MSG-WEBAPI-04)
   │     configureConsumers?.Invoke(bus)  ← AddConsumer<T> registered here
   │     UsingRabbitMq((ctx, busCfg) =>           │
   │        Host(...);                             │
   │        ❰ Service-supplied ReceiveEndpoints ❱ ◄── via the hook closure (D-06)
   │   ))                                          │
   └───────────────▲──────────────────────────────┘
                   │ hook supplied at call site
   ┌───────────────┴──────────────────────────────────────────┐
   │ BaseApi.Service                                            │
   │  Program.cs: AddBaseApiMessaging(cfg, busCfg => { … })     │
   │  Consumers/                                                │
   │    GetProcessorBySourceHashConsumer : IConsumer<GetProcessorBySourceHash>
   │       → ProcessorService.GetBySourceHashAsync             │
   │         hit  → RespondAsync<…Found>(…)                     │
   │         miss → RespondAsync<…NotFound>(…)  (catch NotFoundException)
   │    GetSchemaDefinitionConsumer : IConsumer<GetSchemaDefinition>
   │       → SchemaService.GetByIdAsync                         │
   │         hit  → RespondAsync<…Found>(Definition)            │
   │         miss → RespondAsync<…NotFound>(…)                  │
   └───────────────────────────────────────────────────────────┘
                   │ reads (unchanged)
                   ▼
            EF Core → Postgres (ProcessorEntity, SchemaEntity)
```

The request enters via the bus (a `GetProcessorBySourceHash`/`GetSchemaDefinition` message lands on the explicitly-named receive endpoint), the consumer adapts it onto the existing read service, and responds with exactly one of two typed responses. No HTTP path is touched.

### Recommended Project Structure
```
src/Messaging.Contracts/
├── Projections/
│   ├── ProcessorProjection.cs     # MOVED here (D-01), public
│   ├── LivenessProjection.cs      # unchanged (already public)
│   ├── LivenessStatus.cs          # NEW (D-03) static class, const Healthy
│   └── L2ProjectionKeys.cs        # + ExecutionData(Guid) (D-02)
├── ProcessorQueries.cs            # NEW (D-04) request/response record pairs (or split per query)
└── ProcessorQueues.cs             # NEW (D-06) queue-name constants, mirrors OrchestratorQueues

src/BaseApi.Service/
├── Features/.../Responders/       # NEW consumers (discretion on exact folder)
│   ├── GetProcessorBySourceHashConsumer.cs
│   └── GetSchemaDefinitionConsumer.cs
└── Program.cs                     # AddBaseApiMessaging(cfg, hook) call site (line 8)

src/BaseApi.Core/
└── DependencyInjection/MessagingServiceCollectionExtensions.cs  # + optional hook param (D-05)
```

### Pattern 1: Dual-Response Responder Consumer (verified MassTransit 8.5.5)
**What:** A consumer that calls `RespondAsync<TFound>` on hit and `RespondAsync<TNotFound>` on miss. Distinct response types let the Phase 26 client `response.Is(out Response<TFound> r)` pattern-match.
**When to use:** Both query responders.
**Example:**
```csharp
// Source: https://masstransit.massient.com/concepts/requests  [CITED]
public class CheckOrderStatusConsumer : IConsumer<CheckOrderStatus>
{
    public async Task Consume(ConsumeContext<CheckOrderStatus> context)
    {
        var order = await _orderRepository.Get(context.Message.OrderId);
        if (order == null)
            await context.RespondAsync<OrderNotFound>(context.Message);
        else
            await context.RespondAsync<OrderStatusResult>(new { OrderId = order.Id, /* ... */ });
    }
}
```
Adapted for this repo (the existing read services throw `NotFoundException` on miss — catch it to drive the not-found branch rather than null-check):
```csharp
public sealed class GetProcessorBySourceHashConsumer(ProcessorService processors)
    : IConsumer<GetProcessorBySourceHash>
{
    public async Task Consume(ConsumeContext<GetProcessorBySourceHash> context)
    {
        try
        {
            var p = await processors.GetBySourceHashAsync(context.Message.SourceHash, context.CancellationToken);
            await context.RespondAsync<ProcessorIdentityFound>(new
            {
                p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId
            });
        }
        catch (NotFoundException)
        {
            await context.RespondAsync<ProcessorIdentityNotFound>(new { context.Message.SourceHash });
        }
    }
}
```
Note: `GetByIdAsync`/`GetBySourceHashAsync` both throw `NotFoundException` on miss `[VERIFIED: BaseService.cs:82-87; ProcessorService.cs:66-82]`. Schema reads by Id use the inherited `BaseService.GetByIdAsync(Guid, ct)` (SchemaService has an empty body) `[VERIFIED: SchemaService.cs:15-25, BaseService.cs:82]`.

### Pattern 2: Optional Consumer Hook on the Bus Join (D-05) — mirror BaseConsole.Core
**What:** Add an optional `Action<IBusRegistrationConfigurator>? configureConsumers = null` parameter to `AddBaseApiMessaging`, invoked inside `AddMassTransit(bus => { ... })`. The receive-endpoint bindings (D-06) live in the `UsingRabbitMq` closure; the consumer registrations live on the `IBusRegistrationConfigurator`.
**Why this exact type:** `AddBaseConsoleMessaging` already uses `Action<IBusRegistrationConfigurator> configureConsumers` invoked as `configureConsumers(x)` inside `AddMassTransit(x => ...)` `[VERIFIED: BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:34-47]`. Use the same type — it is exactly the configurator surface `AddConsumer<T>` lives on.
**Example (target shape for `AddBaseApiMessaging`):**
```csharp
public static IServiceCollection AddBaseApiMessaging(
    this IServiceCollection services, IConfiguration cfg,
    Action<IBusRegistrationConfigurator>? configureConsumers = null)   // D-05: optional, default null = publish-only
{
    // ... host/port/user/pass unchanged ...
    services.AddMassTransit(bus =>
    {
        bus.ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = HealthStatus.Degraded); // UNCHANGED
        configureConsumers?.Invoke(bus);   // D-05: registers consumers when supplied; no-op default
        bus.UsingRabbitMq((context, busCfg) =>
        {
            busCfg.Host(host, port, "/", h => { h.Username(user); h.Password(pass); });
            // D-06 endpoints — but see "Pitfall 2": ReceiveEndpoint needs `context`, which is
            // only available inside this closure. Either pass a second hook typed
            // Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>, OR bind the
            // endpoints here keyed on whether consumers were supplied.
        });
    });
    return services;
}
```

### Pattern 3: Explicit ReceiveEndpoint Binding (D-06) — verified
**What:** Bind a named queue per responder with `cfg.ReceiveEndpoint(name, e => e.ConfigureConsumer<T>(context))`.
**Example:**
```csharp
// Source: https://masstransit.massient.com/configuration  [CITED]
services.AddMassTransit(x =>
{
    x.AddConsumer<SubmitOrderConsumer>();
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.ReceiveEndpoint("order-service", e => e.ConfigureConsumer<SubmitOrderConsumer>(context));
    });
});
```
In-repo precedent (the orchestrator binds the stable shared queue via `EndpointName` on a `ConsumerDefinition`, and the test harness binds explicit `ReceiveEndpoint`s):
```csharp
// Source: tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs:44-52  [VERIFIED]
cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
```
For Phase 25 the two endpoints use the new shared constants (D-06), e.g. `cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery, e => e.ConfigureConsumer<GetProcessorBySourceHashConsumer>(context))`.

### Pattern 4: Shared Queue-Name Static Class (D-06) — mirror OrchestratorQueues
```csharp
// Mirror of src/Messaging.Contracts/OrchestratorQueues.cs  [VERIFIED]
public static class ProcessorQueues   // exact name is discretion
{
    public const string IdentityQuery = "processor-identity-query";   // bare short-name, no queue: scheme
    public const string SchemaQuery   = "schema-definition-query";
}
```
The bare short-name is stored WITHOUT the `queue:`/`exchange:` scheme — the Phase 26 sender prepends it (RabbitMQ request clients target `exchange:{name}`) `[CITED: masstransit.massient.com/concepts/requests — AddRequestClient(new Uri("exchange:order-status"))]`.

### Pattern 5: Static-Class SoT Constant (D-03) — mirror OrchestratorQueues / L2ProjectionKeys
```csharp
namespace Messaging.Contracts.Projections;
public static class LivenessStatus
{
    public const string Healthy = "Healthy";   // single source — writer (Phase 26) + readers cannot desync
}
```

### Anti-Patterns to Avoid
- **Referencing `BaseApi.Service` from `BaseApi.Core`:** breaks the Phase-19 firewall. The hook must be typed only in `IBusRegistrationConfigurator` (+ optionally `IBusRegistrationContext`/`IRabbitMqBusFactoryConfigurator`) so Core never names the concrete consumer types.
- **Adding `ConfigureEndpoints(context)` to the WebApi bus:** D-06 explicitly chooses explicit `ReceiveEndpoint`s over convention-based auto-naming. Convention-naming would also auto-bind via message-type and defeat the shared-constant SoT. The publish-only path has NO `ConfigureEndpoints` today `[VERIFIED: MessagingServiceCollectionExtensions.cs:65]` — keep it that way; only add explicit endpoints when the hook is supplied.
- **Adding correlation filters to the WebApi responders:** they live in `BaseConsole.Core` (forbidden ref). These stateless query responders are outside the orchestration correlation chain. Default = no filters (confirmed correct).
- **Touching `o.MinimalFailureStatus = HealthStatus.Degraded` or `o.Tags`:** overriding `Tags` REPLACES the defaults `["ready","masstransit"]` (existing Pitfall 7 note in the file). Leave the health block byte-identical.
- **Bare `[JsonPropertyName]` on a positional record:** on a positional record a bare attribute binds to the ctor parameter and STJ ignores it — the `[property: ...]` target prefix is mandatory and load-bearing (existing Pitfall 1). Preserve verbatim when moving `ProcessorProjection`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Not-found signaling over the bus | Nullable payload + null-check | Dual-response `RespondAsync<TFound>`/`RespondAsync<TNotFound>` | Idiomatic MassTransit; Phase 26 retries cleanly via `response.Is(out Response<TNotFound>)` (D-04). |
| Mapping a DB miss to a not-found response | New "exists?" query method | Catch the existing `NotFoundException` thrown by `GetBySourceHashAsync`/`GetByIdAsync` | The read services already throw on miss `[VERIFIED]`; no new query logic. |
| Queue-name strings | Inline literals at endpoint + client | Shared static-class constants (D-06) | `OrchestratorQueues` precedent; writer (endpoint) + reader (Phase 26 client) cannot desync. |
| Responder bus round-trip test | Real RabbitMQ in unit tests | `AddMassTransitTestHarness` + `UsingInMemory` + `ITestHarness` | Already the repo pattern `[VERIFIED: ResultConsumeTests.cs]`; supports request/response. |
| L2 key formatting | New string-format helper per key | `L2ProjectionKeys` static class | Existing SoT; `ExecutionData` is one more builder (D-02). |

**Key insight:** This phase adds zero new infrastructure abstractions — it composes five already-proven repo patterns. The work is careful placement (firewall) and verbatim preservation (JSON targets), not invention.

## Runtime State Inventory

> CONTRACT-01 is a type relocation (rename/move). This inventory covers the move's blast radius.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | `ProcessorProjection` is the deserialization target for `skp:{processorId}` L2 values `[VERIFIED: ProcessorLivenessValidator.cs:44]`. The JSON shape (`inputDefinition`/`outputDefinition`/`liveness`) is UNCHANGED by the move — only the CLR type's namespace/visibility change. No data migration. | Code edit only (update `using`). Existing Redis values remain valid — the wire JSON is byte-identical. |
| Live service config | None — no external service stores the CLR type name. The L2 JSON is field-name-keyed, not type-name-keyed. | None — verified by inspection of `ProcessorLivenessValidator` (deserializes by field name). |
| OS-registered state | None — verified (no task/service references a CLR type). | None. |
| Secrets/env vars | None — verified. | None. |
| Build artifacts | Moving a `.cs` file between projects + flipping `internal`→`public` triggers a rebuild of `Messaging.Contracts`, `BaseApi.Service`, and downstream. No stale artifact survives a clean build. | Clean build (already part of the close gate). |

**Reference sites for the move (CONTRACT-01):** Only TWO in `src/` `[VERIFIED: Grep ProcessorProjection in src/]`:
1. `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs:12` — the definition itself (delete after move).
2. `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs:41,44` — `using BaseApi.Service.Features.Orchestration.Projection;` (line 2) must change to `using Messaging.Contracts.Projections;` (which is ALSO already imported at line 3 — so the projection `using` line can simply be removed). The `JsonSerializer.Deserialize<ProcessorProjection>` call site is otherwise unchanged.

No test file references `ProcessorProjection` directly `[VERIFIED: Grep returned only src/ hits]` — but verify again at plan time in case tests are added; `RedisProjectionWriterFacts`/`ProcessorLivenessFacts` may construct it.

## Common Pitfalls

### Pitfall 1: `ReceiveEndpoint` needs `context`, which is only in the `UsingRabbitMq` closure
**What goes wrong:** D-05's hook is typed `Action<IBusRegistrationConfigurator>` (where `AddConsumer<T>` lives), but `ReceiveEndpoint(...).ConfigureConsumer<T>(context)` needs the `IBusRegistrationContext`/`IRabbitMqBusFactoryConfigurator`, which only exist inside `bus.UsingRabbitMq((context, busCfg) => ...)`. A single `Action<IBusRegistrationConfigurator>` hook can register the consumers but cannot bind their explicit endpoints.
**Why it happens:** MassTransit splits registration (`IBusRegistrationConfigurator`) from transport/endpoint config (`IRabbitMqBusFactoryConfigurator` + `IBusRegistrationContext`). `BaseConsole.Core` sidesteps this by using `ConfigureEndpoints(ctx)` (convention auto-binding) — but D-06 explicitly rejects `ConfigureEndpoints`.
**How to avoid:** Expose **two** seams, mirroring `AddBaseConsoleMessaging`'s `configureConsumers` + `configureBus` pair `[VERIFIED: BaseConsole.Core MessagingServiceCollectionExtensions.cs:34-37]`:
  - `Action<IBusRegistrationConfigurator>? configureConsumers` — for `AddConsumer<T>()`.
  - `Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureEndpoints` — for the explicit `ReceiveEndpoint(...)` bindings, invoked inside the `UsingRabbitMq` closure (after `Host(...)`, where `context` is available).
  Both default to null → publish-only unchanged. `BaseApi.Service` supplies both at the `Program.cs` call site. (Planner should pick the exact signature; the two-hook shape is the lowest-risk, firewall-clean option and matches the established `BaseConsole` precedent.)
**Warning signs:** Compile error "the name 'context' does not exist" if you try to bind endpoints from the single consumer hook; or silently falling back to `ConfigureEndpoints` (violates D-06).

### Pitfall 2: Accidentally flipping `/health/ready` to 503 (MSG-WEBAPI-04 regression)
**What goes wrong:** Adding consumers/endpoints could (a) change the bus health check escalation, or (b) make the bus a hard dependency so broker-down 503s CRUD readiness.
**Why it happens:** Touching the `ConfigureHealthCheckOptions` block, or the receive endpoints failing to connect escalating the check past Degraded.
**How to avoid:** Do not modify the `o.MinimalFailureStatus = HealthStatus.Degraded` line or `o.Tags`. The cap is on the *bus* check, which covers receive endpoints too — adding endpoints does not change which tags the check carries. Regression guard already exists: `HealthEndpointsTests.Health_Ready_Returns_200_When_Broker_Dead` and `Health_Live_Returns_200_When_Broker_Dead` `[VERIFIED: HealthEndpointsTests.cs:220-252]`. Re-run them after the change; they must stay green.
**Warning signs:** `Health_Ready_Returns_200_When_Broker_Dead` turns red.

### Pitfall 3: `internal`→`public` without preserving JSON targets
**What goes wrong:** Re-typing the record during the move drops a `[property: ...]` prefix, breaking STJ field mapping silently (existing Pitfall 1 in both `ProcessorProjection.cs` and `LivenessProjection.cs`).
**How to avoid:** Move the file verbatim; change only the namespace (`BaseApi.Service.Features.Orchestration.Projection` → `Messaging.Contracts.Projections`) and `internal`→`public`. Keep `[property: JsonPropertyName("inputDefinition")]`, `("outputDefinition")`, `("liveness")` exactly. A round-trip serialization test (or the existing `ProcessorLivenessFacts`) catches a regression.
**Warning signs:** `ProcessorLivenessValidator` starts mapping `absent`/`malformed` for previously-valid L2 values.

### Pitfall 4: Adding correlation filters or ambient accessor to the WebApi bus
**What goes wrong:** Importing `BaseConsole.Core` correlation filters into the responders breaks the firewall (Core would need to reference `BaseConsole.Core`).
**How to avoid:** Don't. These responders are stateless and outside the correlation chain. The publish-only join deliberately has no filters `[VERIFIED: MessagingServiceCollectionExtensions.cs:16-17, 65]`. Discretion item — confirmed: default to no filters.
**Warning signs:** A `using BaseConsole.Core...` appears in `BaseApi.Core` or `BaseApi.Service` responder code; the firewall architecture test fails.

### Pitfall 5: `ExecutionData` `{guid:D}` vs bare interpolation divergence
**What goes wrong:** The existing builders mix explicit `:D` (`Root`) and bare interpolation (`Step`/`Processor`); both render identically (Guid default format is "D"), but the golden test must pin the exact bytes.
**How to avoid:** Write `$"{Prefix}data:{entryId:D}"` and assert the literal `"skp:data:<hyphenated-guid>"` in a golden test using a known GUID, mirroring `L2ProjectionKeysTests.cs:30` `[VERIFIED]`. Also add an anti-collision assertion: `ExecutionData(g) != Root(g)` and `!= Processor(g)` (the `data:` discriminator must keep the namespace distinct — CONTEXT D-02 intent).
**Warning signs:** Key collides with `Root`/`Processor` for the same GUID (it must not, due to the `data:` segment).

## Code Examples

### CONTRACT-02 golden test (mirror existing discipline)
```csharp
// Source: tests/.../Projection/L2ProjectionKeysTests.cs (existing pattern)  [VERIFIED]
[Fact]
public void ExecutionData_Produces_Prefix_Data_Discriminator_Plus_HyphenatedGuid()
{
    var entryId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    Assert.Equal("skp:data:55555555-5555-5555-5555-555555555555",
                 L2ProjectionKeys.ExecutionData(entryId));
}

[Fact]
public void ExecutionData_Is_Distinct_From_Root_And_Processor()
{
    var g = Guid.Parse("66666666-6666-6666-6666-666666666666");
    Assert.NotEqual(L2ProjectionKeys.Root(g), L2ProjectionKeys.ExecutionData(g));
    Assert.NotEqual(L2ProjectionKeys.Processor(g), L2ProjectionKeys.ExecutionData(g));
}
```

### Responder round-trip test (in-memory harness, found + not-found)
```csharp
// Source: tests/.../Orchestrator/ResultConsumeTests.cs harness pattern  [VERIFIED]
// + masstransit.massient.com/guides/unit-testing/request-response  [CITED]
await using var provider = new ServiceCollection()
    .AddLogging()
    .AddSingleton(processorServiceStub)   // stub/real ProcessorService
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<GetProcessorBySourceHashConsumer>();
        x.UsingInMemory((ctx, cfg) =>
            cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
                e => e.ConfigureConsumer<GetProcessorBySourceHashConsumer>(ctx)));
    })
    .BuildServiceProvider(true);

var harness = provider.GetRequiredService<ITestHarness>();
await harness.Start();
var client = harness.GetRequestClient<GetProcessorBySourceHash>();

// found:
var found = await client.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
    new GetProcessorBySourceHash("<known-hash>"));
Assert.True(found.Is(out Response<ProcessorIdentityFound> _));

// not-found:
var miss = await client.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
    new GetProcessorBySourceHash("<unknown-hash>"));
Assert.True(miss.Is(out Response<ProcessorIdentityNotFound> _));
```
Note: `harness.GetRequestClient<T>()` is the harness convenience for request/response (parallels `harness.Bus` used in `ResultConsumeTests`). Confirm the exact accessor at plan time against MassTransit 8.5.5; `IRequestClient<T>` can also be resolved from DI when registered via `AddRequestClient<T>`.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Exception-on-miss request/response | Typed dual-response (`TFound`/`TNotFound`) | MassTransit 5+ | A miss is not a fault; client retries via tuple match (D-04). |
| Convention `ConfigureEndpoints` auto-naming | Explicit `ReceiveEndpoint` + shared constants | Project choice (D-06) | Endpoint names are a single source of truth shared with the Phase 26 client. |

**Deprecated/outdated:** Nothing in scope. Do NOT bump MassTransit to 9.x (commercial license; v8.x is the Apache-2.0 line, security-patched through end-2026) `[VERIFIED: Directory.Packages.props:133-136]`.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 + xunit.v3.assert `[VERIFIED: Directory.Packages.props:121-123]` |
| Bus test harness | `MassTransit.Testing` `AddMassTransitTestHarness` + `UsingInMemory` `[VERIFIED: ResultConsumeTests.cs]` |
| Config file | none — `[Collection]`/`[Trait]` attributes; xunit auto-discovery |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~L2ProjectionKeysTests"` |
| Full suite command | `dotnet test` (solution root) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| CONTRACT-01 | `ProcessorProjection` public in leaf + STJ round-trips with same JSON | unit | `dotnet test --filter "FullyQualifiedName~ProcessorProjection"` | ❌ Wave 0 (round-trip pin) |
| CONTRACT-01 | `BaseApi.Core` has no reference to `BaseApi.Service` (firewall) | architecture | `dotnet test --filter "FullyQualifiedName~Firewall"` or csproj-ref assertion | ❌ Wave 0 |
| CONTRACT-02 | `ExecutionData` golden string + distinctness | unit | `dotnet test --filter "FullyQualifiedName~L2ProjectionKeysTests"` | ✅ extend existing file |
| CONTRACT-03 | `LivenessStatus.Healthy == "Healthy"` | unit | `dotnet test --filter "FullyQualifiedName~LivenessStatus"` | ❌ Wave 0 (trivial pin) |
| RPC-01 | `GetProcessorBySourceHash` found AND not-found round-trip | integration (in-memory harness) | `dotnet test --filter "FullyQualifiedName~ProcessorResponder"` | ❌ Wave 0 |
| RPC-02 | `GetSchemaDefinition` found AND not-found round-trip | integration (in-memory harness) | `dotnet test --filter "FullyQualifiedName~SchemaResponder"` | ❌ Wave 0 |
| RPC-03 | Bus health stays Degraded; `/health/ready` 200 broker-down | integration (regression) | `dotnet test --filter "FullyQualifiedName~Health_Ready_Returns_200_When_Broker_Dead"` | ✅ exists, re-run as guard |
| RPC-03 | CRUD surface unchanged | regression | `dotnet test` (full suite green) | ✅ existing suite |

### Sampling Rate
- **Per task commit:** the matching `--filter` quick run (e.g. `L2ProjectionKeysTests`, responder facts).
- **Per wave merge:** `dotnet test tests/BaseApi.Tests` (full project).
- **Phase gate:** Full solution suite green (335/335 baseline from v3.4.0) + the responder facts, before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `ProcessorProjectionRoundTripTests.cs` (or extend `ProcessorLivenessFacts`) — STJ serialize/deserialize pins `inputDefinition`/`outputDefinition`/`liveness` after the move (CONTRACT-01).
- [ ] Firewall assertion — `BaseApi.Core.csproj` has no `ProjectReference` to `BaseApi.Service`/`BaseConsole.Core` (CONTRACT-01/D-05). Mirror any existing csproj-reference architecture test if present; otherwise a new `FirewallFacts` test parsing the csproj or asserting no type leakage.
- [ ] `L2ProjectionKeysTests` — add `ExecutionData` golden + distinctness (CONTRACT-02). Extend the existing file.
- [ ] `LivenessStatusTests.cs` — `Healthy == "Healthy"` pin (CONTRACT-03).
- [ ] `ProcessorResponderTests.cs` + `SchemaResponderTests.cs` — in-memory harness, found AND not-found for each query (RPC-01/02). New files; mirror `ResultConsumeTests` harness scaffold.
- [ ] No framework install needed — xunit.v3 + `MassTransit.Testing` already referenced.

## Environment Availability

> Phase 25 is code + unit/in-memory-test only. The in-memory MassTransit harness needs no real broker; the broker-down regression test deliberately points at a dead host.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8 | build/test | ✓ (assumed — repo builds on it) | 8.0.x | — |
| MassTransit 8.5.5 | bus + harness | ✓ (NuGet, pinned) | 8.5.5 | — |
| RabbitMQ (real) | NOT needed this phase | n/a | — | In-memory harness; broker-down test uses a dead host by design |

**Missing dependencies with no fallback:** None.
**Missing dependencies with fallback:** Real RabbitMQ is intentionally not exercised in Phase 25 unit/integration tests (full real-stack E2E is Phase 28, TEST-01).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Schema reads use the inherited `BaseService.GetByIdAsync(Guid, ct)` (SchemaService has empty body) and `GetSchemaDefinition` queries by schema **Id** (Guid), not by name | RPC-02 / Pattern 1 | Low — verified empty body + `SchemaReadDto.Definition`; if the contract should key by something other than `Guid`, the request record's field type changes. CONTEXT D-04 says "read by Id". |
| A2 | `Messaging.Contracts` needs NO MassTransit package reference (records are plain, like `ExecutionResult`) | Standard Stack | Low — verified existing contracts are plain records. If a response record needs a MassTransit attribute (none anticipated), the leaf would need the ref, slightly widening the firewall surface (still allowed: Core/leaf may reference MassTransit). |
| A3 | The two-hook signature (`configureConsumers` + `configureEndpoints`) is the cleanest firewall-safe shape for D-05/D-06 | Pitfall 1 | Medium — this is the recommended shape, not a locked decision. The planner may choose a single combined hook; the constraint is only that `ReceiveEndpoint` binding needs the `context`, so SOME seam must expose it. Verify the exact `Action<...>` arity at plan time. |
| A4 | `harness.GetRequestClient<T>()` is the MassTransit 8.5.5 harness accessor for request/response in tests | Code Examples | Low — request/response harness support is long-standing; exact accessor name to be confirmed at plan time (alternative: resolve `IRequestClient<T>` from DI after `AddRequestClient<T>`). |
| A5 | No test file currently constructs/serializes `ProcessorProjection` (only `src/` references exist) | Runtime State Inventory | Low — grep covered `src/`; re-grep `tests/` at plan time before deleting the old type, in case a fact builds it directly. |

## Open Questions

1. **Single combined hook vs. two hooks for D-05/D-06?**
   - What we know: `AddConsumer<T>` needs `IBusRegistrationConfigurator`; `ReceiveEndpoint(...).ConfigureConsumer<T>(context)` needs the `UsingRabbitMq` closure's `context` + bus-factory configurator. `BaseConsole.Core` already exposes both (`configureConsumers` + `configureBus`).
   - What's unclear: whether the planner prefers two parameters or one `Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>` that does both consumer-endpoint binding (consumers can be registered via `AddConsumer` inside `UsingRabbitMq`? No — `AddConsumer` is on the registration configurator). The two-seam shape is safest.
   - Recommendation: two optional hooks (mirror `AddBaseConsoleMessaging`), both default null. Confirm exact signature at plan time.

2. **Exact request-record field for `GetSchemaDefinition`.**
   - What we know: D-04 says success carries `{ Definition }`; the read is by Id (`BaseService.GetByIdAsync(Guid, ct)`).
   - What's unclear: whether the request carries `Guid SchemaId` (most likely) — confirm against how Phase 26 will resolve input/output schema **Ids** (it has the Guids from the identity response). Recommendation: `GetSchemaDefinition(Guid SchemaId)`.

## Sources

### Primary (HIGH confidence)
- Repo source (VERIFIED by Read/Grep): `Directory.Packages.props`, `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs`, `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs`, `src/Messaging.Contracts/{L2ProjectionKeys,OrchestratorQueues,LivenessProjection,ExecutionResult,EntryStepDispatch}.cs`, `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs`, `.../Validation/ProcessorLivenessValidator.cs`, `.../Processor/{ProcessorDtos,ProcessorService}.cs`, `.../Schema/{SchemaDtos,SchemaService}.cs`, `src/BaseApi.Core/Services/BaseService.cs`, `src/BaseApi.Service/Program.cs`, `src/BaseApi.Service/Composition/AppFeatures.cs`, `src/Orchestrator/Program.cs`, `src/Orchestrator/Consumers/{ResultConsumer,ResultConsumerDefinition}.cs`, `tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs`, `tests/.../Observability/HealthEndpointsTests.cs`, `tests/.../Projection/L2ProjectionKeysTests.cs`.
- MassTransit official docs (Context7 `/websites/masstransit_massient`) — `concepts/requests` (dual-response consumer + tuple client), `configuration` / `configuration/consumers` (explicit `ReceiveEndpoint` + `ConfigureConsumer`), `guides/unit-testing/request-response`.

### Secondary (MEDIUM confidence)
- None required — all critical claims verified against repo source or official docs.

### Tertiary (LOW confidence)
- None.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — MassTransit pinned in CPM; no new packages; leaf needs no new ref (verified against existing plain-record contracts).
- Architecture: HIGH — every pattern (hook, explicit endpoint, dual-response, shared constants, golden test, harness) has a verified in-repo precedent + official-doc confirmation.
- Pitfalls: HIGH — the `context`-scoping pitfall (Pitfall 1) and the Degraded-cap/firewall invariants are derived directly from verified source + an existing regression test.

**Research date:** 2026-06-01
**Valid until:** 2026-07-01 (stable — MassTransit 8.x pinned by license decision; repo patterns settled across v3.4.0).
