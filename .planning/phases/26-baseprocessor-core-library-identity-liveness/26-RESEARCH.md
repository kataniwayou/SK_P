# Phase 26: BaseProcessor.Core — Library, Identity & Liveness - Research

**Researched:** 2026-06-01
**Domain:** .NET 8 Generic-Host library; MassTransit 8.5.5 request/response (`IRequestClient`); StackExchange.Redis 2.13.1 liveness self-registration; assembly-metadata reflection; in-memory test harness validation
**Confidence:** HIGH (every recommendation grounded in concrete codebase files; MassTransit request-client API cross-verified against official docs)

## Summary

Phase 26 builds `src/BaseProcessor.Core/` as a thin composition over the existing `BaseConsole.Core` infra base. There is **nothing exotic to discover** — the entire design is already pinned by CONTEXT.md's 13 locked decisions and demonstrated by existing in-repo precedent. The two highest-value findings are: (1) `Orchestrator/Program.cs` + `HydrationBackgroundService.cs` are a near-exact, copyable template for the D-01/D-02 startup orchestrator (remove the base `StartupCompletionService`, drive `MarkReady` from a completion event, bounded-backoff retry, host never crashes), and (2) the L2 writer half is a trivial whole-value `StringSet` of the **already-public, frozen** `Messaging.Contracts.Projections.ProcessorProjection` — the reader (`ProcessorLivenessValidator`) is unchanged and the closed writer↔reader loop is directly assertable in-test against a real Redis (`RedisFixture` at `localhost:6380`).

The only genuinely new code surface is the **first `IRequestClient` usage on the console side** (RPC-04). MassTransit 8.5.5's dual-response `GetResponse<TFound, TNotFound>()` + `response.Is(out Response<T>)` pattern (verified against official docs) maps cleanly onto the Phase 25 contract pairs. Request clients are registered with an explicit destination `Uri("exchange:{name}")` targeting the `ProcessorQueues.IdentityQuery`/`SchemaQuery` endpoints, and driven in-test by the MassTransit in-memory `ITestHarness` (already wired into `tests/BaseApi.Tests`) — exactly the discipline `ConsoleCorrelationFilterTests` already uses.

**Primary recommendation:** Build `BaseProcessor.Core` as `AddBaseProcessor(cfg)` = `AddBaseConsole` + `AddBaseConsoleMessaging` (with two `AddRequestClient` registrations + the `StartupCompletionService` removal baked in) + one `BackgroundService` startup orchestrator (Loop A → launch heartbeat → Loop B → `MarkReady`) + one separate heartbeat `BackgroundService` + a singleton `IProcessorContext` holder + a stubbable `ISourceHashProvider` (default = reflection over `AssemblyMetadata`). Mirror `Orchestrator/Program.cs` verbatim for the gate re-pointing. Reuse the shared `ProcessorProjection`/`LivenessProjection`/`L2ProjectionKeys`/`LivenessStatus` types for the L2 write — never define a parallel DTO.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01 (BPC-03 / IDENT-04 / SCHEMA-01):** Model the two-loop startup as a single `BackgroundService` (or equivalent hosted startup orchestrator) wired by `AddBaseProcessor`. **Loop A** resolves identity by SourceHash; **Loop B** resolves the input/output definitions for each non-null schema Id. Mirrors the `Orchestrator/Program.cs` shape — base library supplies all infra, concrete `Program.cs` stays minimal.
- **D-02 (BPC-03):** Tie `BaseConsole.Core`'s startup gate (`IStartupGate.MarkReady`) to **"Healthy reached"** — flip `/startup` (and therefore `/ready`) green only once identity + all required definitions resolve, exactly mirroring how `Orchestrator/Program.cs` **removes** the base `StartupCompletionService` (so `MarkReady` no longer fires at bare host-start) and drives `MarkReady` from completion instead. `/live` stays dependency-independent (untouched). Until Healthy, the processor is not ready and writes no liveness key.
- **D-03 (RPC-04 / IDENT-04 / SCHEMA-01):** Both queries go through MassTransit `IRequestClient` using the Phase 25 dual-response contracts — `IRequestClient<GetProcessorBySourceHash>.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>()` and `IRequestClient<GetSchemaDefinition>.GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>()`. Pattern-match on the typed not-found response rather than null-check. Request clients target the `ProcessorQueues.IdentityQuery` / `ProcessorQueues.SchemaQuery` endpoints (sender adds the `exchange:`/`queue:` scheme).
- **D-04 (IDENT-04 — retry posture):** **Unbounded** retry loop with a **bounded exponential backoff** (cap ~30s). Retry on BOTH the `IRequestClient` request timeout AND the typed not-found response (boot-before-register). Per-request `IRequestClient` timeout kept short (~5–10s). Backoff cap and per-request timeout are **appsettings-configurable**.
- **D-05 (SCHEMA-02):** Null/optional schema Ids are **skipped by design** — absent definition is never a failure. The **config** schema Id is resolved by neither loop (only input + output definitions are fetched and written). All-null input/output Ids → still reaches Healthy.
- **D-06 (BPC-02 / forward-compat with Phase 27):** A single mutable singleton context holder (`IProcessorContext` / `ProcessorIdentity`) is the source of truth for the resolved `Id`, the three schema Ids, the resolved input/output **definitions**, and a **Healthy** flag. The startup orchestrator populates it; the heartbeat reads it; Phase 27's consumer will read it for its queue name + validation.
- **D-07 (LIVE-01 / LIVE-04):** A background heartbeat worker writes/refreshes `L2ProjectionKeys.Processor(processorId)` (= `skp:{processorId:D}`) every `Interval` seconds with a `ProcessorProjection { inputDefinition, outputDefinition, liveness{ timestamp, interval, status } }`. Writes **only when Healthy**. `status` always `LivenessStatus.Healthy`.
- **D-08 (LIVE-02 / LIVE-03):** Each beat refreshes `timestamp` (from `TimeProvider`) and re-applies the configured `Ttl` (sliding expiration via `SET ... EX`). Written `interval` **equals the configured heartbeat delay in seconds**. `Interval` and `Ttl` are two independent appsettings seconds-values.
- **D-09 (LIVE-05 — frozen shape):** Reuse the shared `ProcessorProjection` + `LivenessProjection` records directly (NO parallel writer DTO). Use `L2ProjectionKeys.Processor(id)` for the key, `LivenessStatus.Healthy` for the status.
- **D-10 (LIVE-06 — lock-free):** Blind whole-value `SET`, only-when-Healthy; concurrent N-replica writes are equivalent → last-write-wins, no synchronization, no lock, no read-modify-write.
- **D-11 (heartbeat resilience):** A Redis write fault on a beat is **log-and-continue** — never crash. Missed beats let the key slide to `stale`. The worker keeps beating.
- **D-12 (BPC-02):** Declare the `abstract` base processor class + the single `abstract` execution method signature (`ProcessAsync` seam) + its result type **now**. Invoked only in Phase 27. Concrete overrides exactly one method.
- **D-13:** Prove `BaseProcessor.Core` standalone via in-memory tests (Phase 18 discipline): `ISourceHashProvider` stub; MassTransit in-memory harness asserts retry-on-not-found then resolve-on-found; assert the exact L2 JSON byte-matches what `ProcessorLivenessValidator` reads.

### Claude's Discretion

- Exact namespaces, class names, file layout under `src/BaseProcessor.Core/` (mirror `BaseConsole.Core` conventions: `DependencyInjection`, `Configuration`, + startup/identity + heartbeat areas).
- Exact `IProcessorContext` member shape; whether Healthy is a flag, enum, or `TaskCompletionSource` (confirm against Phase 27's consumer needs).
- Whether the two resolution loops live in one hosted service or two; whether the heartbeat is separate or folded in (D-01 prefers one orchestrator + separate heartbeat).
- Exact backoff curve + appsettings key names/defaults for retry timeout + backoff cap (D-04) and heartbeat `Interval`/`Ttl` (CONFIG-01).
- Whether `AddBaseProcessor` composes `AddBaseConsole` + `AddBaseConsoleMessaging` internally or expects the concrete `Program.cs` to call them (mirror whichever keeps the concrete `Program.cs` smallest — BPC-03).

### Deferred Ideas (OUT OF SCOPE)

- **Execution round-trip** — `queue:{processorId:D}` consumer, L2 input resolution + validation, `ProcessAsync` **invocation**, output validation + L2 data write + `ExecutionResult` sends, ack-after-send/business-ack/infra-throw — **Phase 27** (seam *declared* here, *wired* there).
- **SourceHash embed mechanism** — MSBuild `BeforeTargets=CoreCompile` SHA-256 target + `[assembly: AssemblyMetadata("SourceHash", …)]` emit — **Phase 28**. This phase only *reads* assembly metadata (behind a seam).
- **Concrete `Processor.Sample`** — dummy `ProcessAsync`, Dockerfile, compose tier, real-stack E2E + 3-GREEN/triple-SHA close gate — **Phase 28**.
- Config re-validation in the processor; cleanup-on-read of execution-data keys; step-to-step output-data forwarding on the wire; real (non-dummy) transform logic.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| BPC-01 | `BaseProcessor.Core` is a reusable Generic-Host library built on `BaseConsole.Core` (inherits soft-dep Redis, embedded health, metrics-only OTel, MassTransit/RabbitMQ, correlation filters) | `AddBaseProcessor` composes `AddBaseConsole` + `AddBaseConsoleMessaging` (Standard Stack; Architecture Pattern 1). New `src/BaseProcessor.Core/` references `BaseConsole.Core` + `Messaging.Contracts` only (Integration Points). |
| BPC-02 | A processor is created by subclassing the base + implementing exactly one `abstract` method — no infra/id/L2/bus code in the concrete | `abstract` base class + `ProcessAsync` seam declared now (Pattern 5). `IProcessorContext` (D-06) holds all resolved state so concrete needs none. |
| BPC-03 | An `AddBaseProcessor` composition root wires the startup orchestration so concrete `Program.cs` stays minimal (mirrors `AddBaseConsole`/`Orchestrator`) | `Orchestrator/Program.cs` is the verbatim template (Code Examples §1). `AddBaseProcessor` registers orchestrator + heartbeat + request clients + gate removal. |
| IDENT-03 | At runtime reads SourceHash from assembly metadata via reflection | `ISourceHashProvider` seam, default reflection over `AssemblyMetadataAttribute` (Pattern 4; Code Examples §4). |
| IDENT-04 | Resolves identity (`Id` + 3 nullable schema Ids) by querying WebApi over the bus by SourceHash, retrying on failure until success (boot-before-register tolerated) | Loop A: `IRequestClient<GetProcessorBySourceHash>` dual-response + unbounded retry/bounded-backoff (Pattern 2/3; Code Examples §2/§3). |
| RPC-04 | Processor issues both queries via MassTransit `IRequestClient`s (first request/response on console side) | `AddRequestClient<T>(new Uri("exchange:{name}"))` (Code Examples §2). Verified MassTransit 8.5.5 API. |
| SCHEMA-01 | For each non-null (input, output) schema Id, queries definition over bus, retrying until resolved | Loop B: `IRequestClient<GetSchemaDefinition>` per non-null Id, same retry shape (Code Examples §3). |
| SCHEMA-02 | Null (optional) schema Ids skipped — never a failure; config schema not resolved | Loop B iterates only over non-null `InputSchemaId`/`OutputSchemaId`; `ConfigSchemaId` never read (Pattern 3). |
| LIVE-01 | Background heartbeat writes/refreshes `skp:{processorId:D}` every `Interval`s with the projection — only while Healthy | Heartbeat `BackgroundService` reads `IProcessorContext.IsHealthy` gate (Pattern 6; Code Examples §5). |
| LIVE-02 | Each beat refreshes `timestamp` + re-applies `Ttl` (sliding); N replicas keep it fresh | `StringSetAsync(key, json, expiry: TimeSpan.FromSeconds(Ttl))` per beat (Code Examples §5). |
| LIVE-03 | Written `interval` equals heartbeat delay (seconds) so `timestamp + interval×2` holds | Write `interval = IntervalSeconds`; reader math at `ProcessorLivenessValidator.cs:55` (Pattern 6; Pitfall 2). |
| LIVE-04 | Writes only once Healthy (identity + required non-null definitions resolved); `status` always `"Healthy"`; non-Healthy replica is `absent` | Healthy = identity resolved AND all non-null input/output defs resolved; gate checked each beat (Pattern 6; Pitfall 3). |
| LIVE-05 | Written L2 shape exactly matches `ProcessorLivenessValidator` read; v3.4.0 validator unchanged | Reuse `ProcessorProjection`/`LivenessProjection` verbatim; round-trip assertion (Don't Hand-Roll; Validation Architecture). |
| LIVE-06 | Multi-replica writes lock-free + safe (blind whole-value `SET`, equivalent content, last-write-wins) | No lock, no RMW — single `StringSetAsync` (D-10; Pattern 6). |
| CONFIG-01 | Liveness `Interval` (s) + `Ttl` (s) are two independent appsettings values | `ProcessorLivenessOptions` bound from config with `cfg.Require` posture (Config section). |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| SourceHash read | Processor process (reflection on its own assembly) | — | The hash describes *this binary*; read locally via `Assembly.GetEntryAssembly()`/the base type's assembly, never over the wire. |
| Identity resolution (by hash) | Processor (request client) → WebApi (responder, Phase 25) | RabbitMQ bus | Processor is the *client*; WebApi owns the Postgres `Processor` row and answers `GetProcessorBySourceHash`. Firewall: processor talks to WebApi only over the bus. |
| Schema-definition resolution | Processor (request client) → WebApi (responder, Phase 25) | RabbitMQ bus | Same client/responder split; WebApi owns schema rows. |
| Startup gate (`/startup`,`/ready`) | Processor process (in-proc `IStartupGate` from `BaseConsole.Core`) | — | Health is local. Gate flips on Healthy-reached, driven by the startup orchestrator. |
| Liveness self-registration | Processor (writer) → Redis L2 | — | Processor is the **external self-registrar**; the orchestrator-side `ProcessorLivenessValidator` is the *reader* (untouched). |
| `ProcessAsync` transform seam | Concrete processor subclass | — | Declared here; invoked Phase 27. The only code a concrete writes. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | `IRequestClient` dual-response identity/schema queries (RPC-04) | `[VERIFIED: Directory.Packages.props:137]` Last Apache-2.0 line; already the bus library across the solution. Do NOT bump to 9.x (commercial). |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport (inherited via `AddBaseConsoleMessaging`) | `[VERIFIED: Directory.Packages.props:138]` |
| StackExchange.Redis | 2.13.1 | Liveness `StringSet ... EX` writes via soft-dep `IConnectionMultiplexer` | `[VERIFIED: Directory.Packages.props:131]` Already the L2 client; multiplexer comes free from `AddBaseConsoleRedis`. |
| BaseConsole.Core | (project ref) | The entire infra base `AddBaseProcessor` composes over | `[VERIFIED: src/BaseConsole.Core/]` Soft-dep Redis + embedded health + metrics OTel + bus + 3 correlation filters + `IStartupGate`. |
| Messaging.Contracts | (project ref) | Frozen L2 projection types + dual-response contracts + queue/key/status constants | `[VERIFIED: src/Messaging.Contracts/]` All Phase 25 types already public in the leaf. |

### Supporting (test project)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| MassTransit.Testing (`ITestHarness`, `AddMassTransitTestHarness`) | 8.5.5 (transitive) | In-memory broker harness to drive Loop A/B retry→resolve (D-13) | `[VERIFIED: tests/.../ConsoleCorrelationFilterTests.cs:2]` `using MassTransit.Testing;` already in use. |
| Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`) | 8.10.0 | Deterministic clock for liveness timestamp + heartbeat tick assertions | `[VERIFIED: Directory.Packages.props:128]` Already referenced by `tests/BaseApi.Tests`. |
| StackExchange.Redis | 2.13.1 | Real-Redis round-trip JSON byte-match assertion (writer↔reader) | `[VERIFIED: RedisFixture.cs]` `localhost:6380` host-side compose Redis. |
| xunit.v3 + xunit.v3.assert | 3.2.2 | Test framework (MTP runner) | `[VERIFIED: Directory.Packages.props:121-122]` |
| NSubstitute | 5.3.0 | Stub `ISourceHashProvider`, fakes if needed | `[VERIFIED: Directory.Packages.props:120]` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `IRequestClient<T>` | Raw publish + a response consumer | `[CITED: masstransit.io/documentation/concepts/requests]` Request client owns correlation + timeout + reply-queue plumbing; hand-rolling re-implements all of it. D-03 locks `IRequestClient`. |
| `BackgroundService` orchestrator | `IHostedService` `StartAsync` blocking loop | A blocking `StartAsync` stalls host startup and would deadlock the gate-removal pattern; `BackgroundService.ExecuteAsync` runs after the host starts, exactly like `HydrationBackgroundService`. |
| `PeriodicTimer` (TimeProvider ctor) for heartbeat | `Task.Delay` loop | Both viable. `Task.Delay(TimeSpan, TimeProvider, ct)` is the established repo pattern (`HydrationBackgroundService.cs:74`) and is `FakeTimeProvider`-drivable; recommend matching it for consistency. |
| Separate parallel writer DTO | — | FORBIDDEN by D-09. Reusing `ProcessorProjection` is the only desync-proof option. |

**Installation (new `src/BaseProcessor.Core/BaseProcessor.Core.csproj`):**
```xml
<ItemGroup>
  <PackageReference Include="MassTransit" />
  <PackageReference Include="StackExchange.Redis" />
</ItemGroup>
<ItemGroup>
  <ProjectReference Include="..\BaseConsole.Core\BaseConsole.Core.csproj" />
  <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
  <!-- NOT BaseApi.Service — firewall: processor talks to WebApi only over the bus. -->
</ItemGroup>
```
(CPM — no `Version=` attributes. `MassTransit.RabbitMQ` + OTel + health come transitively via `BaseConsole.Core`; add `MassTransit`/`StackExchange.Redis` only if directly referenced in `BaseProcessor.Core` code, which they are.) `[VERIFIED: Directory.Packages.props CPM convention]`

**Version verification:** All four version pins were read directly from the live `Directory.Packages.props` in this session — no registry lookup needed; the solution is on a frozen CPM pin set (MassTransit deliberately held at 8.5.5 for licensing). `[VERIFIED: Directory.Packages.props]`

## Architecture Patterns

### System Architecture Diagram

```
                         appsettings.json
                  (Interval s, Ttl s, RequestTimeout s,
                   BackoffCap s, RabbitMq:*, Redis conn)
                                 │
                                 ▼
   ┌─────────────────────────── Generic Host (concrete Program.cs, thin) ─────────────────────────┐
   │  AddBaseConsoleObservability(cfg)   AddBaseProcessor(cfg)  ──► AddBaseConsole(cfg)            │
   │                                              │                 AddBaseConsoleMessaging(cfg,   │
   │                                              │                   x => { AddRequestClient×2 }) │
   │                                              │                 + remove StartupCompletionSvc  │
   │                                              │                 + TryAddSingleton TimeProvider │
   │                                              ▼                                                │
   │   ┌────────────── ProcessorStartupOrchestrator : BackgroundService ──────────────┐           │
   │   │  ISourceHashProvider.Get()  ── reflection ─► [assembly:AssemblyMetadata        │          │
   │   │        │                                       ("SourceHash", "<64hex>")]      │          │
   │   │        ▼                                                                        │         │
   │   │  LOOP A (unbounded retry, bounded backoff ≤ BackoffCap):                        │         │
   │   │     IRequestClient<GetProcessorBySourceHash>                                    │         │
   │   │       .GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>() ───────────┐      │
   │   │       NotFound | RequestTimeoutException ─► back off, retry                      │  │      │
   │   │       Found ─► IProcessorContext { Id, In?, Out?, Config? } populated            │  │      │
   │   │        │                                                                         │  │      │
   │   │        ▼  (launch heartbeat after identity per PROJECT.md L23)                   │  │      │
   │   │  LOOP B (for each NON-NULL InputSchemaId, OutputSchemaId; skip Config):          │  │      │
   │   │     IRequestClient<GetSchemaDefinition>                                          │  │      │
   │   │       .GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>() ──────────────┤      │
   │   │       NotFound | timeout ─► back off, retry   Found ─► store Definition          │  │      │
   │   │        │                                                                         │  │  bus │
   │   │        ▼  all required resolved ─► IProcessorContext.MarkHealthy()               │  │  ▼   │
   │   │           ─► IStartupGate.MarkReady()  (/startup,/ready flip green; /live indep.) │ │ RabbitMQ
   │   └─────────────────────────────────────────────────────────────────────────────────┘ │  │   │
   │                                              │ reads IsHealthy + projection            │  ▼   │
   │   ┌────────────── ProcessorLivenessHeartbeat : BackgroundService ───────┐              │ WebApi
   │   │  every Interval s, ONLY while IProcessorContext.IsHealthy:           │              │ responders
   │   │     projection = ProcessorProjection(In?, Out?,                      │              │ (Phase 25)
   │   │        LivenessProjection(TimeProvider.now, Interval, "Healthy"))    │              │      │
   │   │     db.StringSetAsync(L2ProjectionKeys.Processor(Id), json,          │──► Redis L2 ◄─────────┐
   │   │        expiry: TimeSpan.FromSeconds(Ttl))   // sliding SET..EX       │   skp:{id:D}          │
   │   │     Redis fault ─► log-and-continue (never crash; key slides stale)  │                       │
   │   └──────────────────────────────────────────────────────────────────────┘                     │
   └────────────────────────────────────────────────────────────────────────────────────────────────┘
                                                                                                       │
   Orchestrator side (UNCHANGED, v3.4.0): ProcessorLivenessValidator.ValidateAsync reads skp:{id:D},  │
   JsonSerializer.Deserialize<ProcessorProjection>, stale if timestamp + interval*2 <= now ──► 422 ◄───┘
```

### Recommended Project Structure
```
src/BaseProcessor.Core/
├── DependencyInjection/
│   └── BaseProcessorServiceCollectionExtensions.cs   # AddBaseProcessor(cfg) composition root (BPC-03)
├── Configuration/
│   └── ProcessorLivenessOptions.cs                   # Interval(s), Ttl(s), RequestTimeout(s), BackoffCap(s) (CONFIG-01)
├── Identity/
│   ├── ISourceHashProvider.cs                        # stubbable seam (IDENT-03 / D-13)
│   ├── AssemblyMetadataSourceHashProvider.cs         # default reflection impl
│   ├── IProcessorContext.cs                           # mutable singleton holder (D-06)
│   └── ProcessorContext.cs                            # backing impl (Id, schema Ids, In/Out defs, IsHealthy)
├── Startup/
│   └── ProcessorStartupOrchestrator.cs               # BackgroundService: Loop A + Loop B + MarkReady (D-01/D-02)
├── Liveness/
│   └── ProcessorLivenessHeartbeat.cs                 # BackgroundService: SET..EX only-when-Healthy (LIVE-*)
└── Processing/
    ├── BaseProcessor.cs                               # abstract base; declares ProcessAsync seam (D-12, BPC-02)
    └── ProcessResult.cs                               # result type declared now, invoked Phase 27
```
(Mirrors `BaseConsole.Core` folder conventions per Claude's Discretion.)

### Pattern 1: `AddBaseProcessor` composes the full stack (BPC-03)
**What:** A single composition root chaining `AddBaseConsole` + `AddBaseConsoleMessaging` (with request clients) + the orchestrator/heartbeat hosted services + context + gate-removal.
**When to use:** Always — it is the seam that keeps the concrete `Program.cs` minimal.
**Recommendation:** `AddBaseProcessor(this IServiceCollection, IConfiguration)` SHOULD compose `AddBaseConsole` + `AddBaseConsoleMessaging` **internally** (passing the request-client registration as the `configureConsumers` lambda) because that minimizes the concrete `Program.cs` (Claude's Discretion resolved toward BPC-03's "stays minimal"). Observability stays a separate `builder.AddBaseConsoleObservability(cfg)` call in `Program.cs` because it needs `IHostApplicationBuilder`, not `IServiceCollection` — same three-call asymmetry `Orchestrator/Program.cs` already has. `[VERIFIED: Orchestrator/Program.cs:20-21]`

### Pattern 2: Register `IRequestClient` against a named endpoint (RPC-04)
**What:** Two `AddRequestClient<T>(new Uri("exchange:{queueName}"))` calls inside the `AddBaseConsoleMessaging` `configureConsumers` lambda.
**When to use:** Once, at composition. The clients are resolved by the startup orchestrator.
**Example:** see Code Examples §2. `[CITED: masstransit.io/documentation/concepts/requests]` `exchange:` is the correct scheme for a fanout-bound responder endpoint; the WebApi binds these as named `ReceiveEndpoint(ProcessorQueues.IdentityQuery/...)` `[VERIFIED: ResponderMessaging.cs:38-41]`.

### Pattern 3: Dual-response + unbounded-retry/bounded-backoff loop (D-03/D-04)
**What:** `GetResponse<TFound, TNotFound>()`, pattern-match, retry on `TNotFound` OR `RequestTimeoutException`, backoff doubling capped at `BackoffCap`.
**When to use:** Both Loop A (identity) and Loop B (each schema definition).
**Example:** Code Examples §3. The bounded-backoff curve copies `HydrationBackgroundService.cs:30-31,74-81` verbatim (`InitialDelay=1s`, `Math.Min(delay*2, cap)`). `[VERIFIED: HydrationBackgroundService.cs]`

### Pattern 4: Startup-gate re-pointing (D-02) — copy `Orchestrator/Program.cs` verbatim
**What:** Remove the base `StartupCompletionService` by `ImplementationType`, so `MarkReady` no longer fires at host-start; the orchestrator calls `gate.MarkReady()` when Healthy.
**Where it should live:** Inside `AddBaseProcessor` (the removal loop operates on `IServiceCollection`, so it belongs in the composition root, not `Program.cs`, to keep the concrete minimal). `[VERIFIED: Orchestrator/Program.cs:61-68]` — exact lines in Code Examples §1.

### Pattern 5: `abstract` processor base + seam (D-12 / BPC-02)
**What:** `public abstract class BaseProcessor { protected abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct); }`
**When to use:** Declared now; **not invoked** this phase. A test double overrides it to prove the class compiles + DI-resolves.
**Signature is locked by PROJECT.md:32** — `Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)`. `[VERIFIED: PROJECT.md:32]`

### Pattern 6: Liveness heartbeat (LIVE-01..06) — blind whole-value sliding SET
**What:** A `BackgroundService` that, every `Interval` seconds and only when `IProcessorContext.IsHealthy`, serializes a fresh `ProcessorProjection` and `StringSetAsync(key, json, expiry)`.
**When to use:** Started by `AddBaseProcessor`; gates itself on Healthy each tick (so it can start before Healthy and simply no-op until then).
**Example:** Code Examples §5. Mirrors `RedisProjectionWriter`/`ProcessorLivenessValidator` clock usage (`_clock.GetUtcNow().UtcDateTime`). `[VERIFIED: RedisProjectionWriter.cs:60, ProcessorLivenessValidator.cs:30]`

### Anti-Patterns to Avoid
- **Parallel writer DTO for the L2 value** — FORBIDDEN (D-09). A separate record's `JsonPropertyName` could desync from the reader. Reuse `ProcessorProjection`/`LivenessProjection`.
- **`ConfigureEndpoints(context)` on the processor** — the processor has no receive endpoints this phase (Phase 27 adds the dispatch queue). The request clients are senders, not consumers. Do not auto-bind endpoints.
- **Blocking `StartAsync`** — use `BackgroundService.ExecuteAsync`; a blocking `IHostedService.StartAsync` stalls host start and breaks the gate-removal pattern.
- **Crashing the host on Redis fault** — D-11: log-and-continue. A dead Redis must not crash; the soft-dep multiplexer materializes even when Redis is down (`abortConnect=false`). `[VERIFIED: ConsoleRedisServiceCollectionExtensions.cs:20-21]`
- **Writing liveness before Healthy** — D-07/LIVE-04: a non-Healthy replica must be `absent` to the reader. Gate every write on `IsHealthy`.
- **Reading `ConfigSchemaId`** — D-05: only input + output definitions are fetched. Config schema is never resolved.
- **Hardcoding the `"Healthy"` string / the key format** — use `LivenessStatus.Healthy` and `L2ProjectionKeys.Processor(id)` (single source of truth). `[VERIFIED: LivenessStatus.cs:11, L2ProjectionKeys.cs:37]`

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Request/reply correlation + reply-queue + timeout | Custom publish + response consumer + correlation map | `IRequestClient<T>.GetResponse<TFound,TNotFound>()` | MassTransit owns the temporary reply endpoint, correlation, and timeout. `[CITED: masstransit.io/.../requests]` |
| L2 projection value shape | A new `record` mirroring the JSON | `Messaging.Contracts.Projections.ProcessorProjection` + `LivenessProjection` | D-09: any field-name drift breaks the reader silently. The shared type's `JsonPropertyName` attrs are load-bearing. `[VERIFIED: ProcessorProjection.cs:14-17]` |
| L2 key string | `$"skp:{id}"` interpolation | `L2ProjectionKeys.Processor(id)` | Reader uses `RedisProjectionKeys.Processor` which composes the same `L2ProjectionKeys.Prefix`. `[VERIFIED: L2ProjectionKeys.cs:37, ProcessorLivenessValidator.cs:33]` |
| Liveness status value | `"Healthy"` literal | `LivenessStatus.Healthy` | Shared const so writer/reader can't desync (CONTRACT-03). `[VERIFIED: LivenessStatus.cs:11]` |
| Soft-dep Redis multiplexer | `ConnectionMultiplexer.Connect` in the worker | inject `IConnectionMultiplexer` from `AddBaseConsole` | Singleton, thread-safe, `abortConnect=false` boot-safe. `[VERIFIED: ConsoleRedisServiceCollectionExtensions.cs:32]` |
| Clock | `DateTime.UtcNow` | inject `TimeProvider`, `.GetUtcNow().UtcDateTime` | Must match the reader's clock for `interval×2` math; `FakeTimeProvider`-testable. `[VERIFIED: ProcessorLivenessValidator.cs:30]` |
| Bounded-backoff retry loop | Bespoke retry policy | Copy `HydrationBackgroundService` pattern (`Task.Delay(delay, ct)`, `Math.Min(delay*2, cap)`) | Established, cancellation-safe, in-repo precedent. `[VERIFIED: HydrationBackgroundService.cs:33-83]` |
| Startup-gate-on-completion | New gate type | `IStartupGate.MarkReady()` + remove `StartupCompletionService` | The base gate + its removal pattern already exist. `[VERIFIED: Orchestrator/Program.cs:61-68]` |
| Assembly-metadata read | Manual attribute parsing | `assembly.GetCustomAttributes<AssemblyMetadataAttribute>()` LINQ | BCL attribute; one-liner. `[ASSUMED — standard BCL; see Pattern 4 / Assumptions A1]` |

**Key insight:** This phase is ~90% composition of existing, proven primitives. The only net-new vocabulary is `IRequestClient` (well-documented MassTransit) and the `ISourceHashProvider` seam (a 5-line reflection read). Hand-rolling any of the above would *recreate* something already shipped and tested in this repo.

## Runtime State Inventory

> Greenfield-leaning phase (a new library + its hosted services). Not a rename/refactor. But because it self-registers into Redis L2 and consumes frozen contracts, the relevant runtime-state questions are answered for completeness.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Writes a NEW key family `skp:{processorId:D}` into Redis L2. The reader (`ProcessorLivenessValidator`) already expects it; `RedisProjectionWriter` deliberately writes ZERO processor keys (PROC-NOCREATE-01) — this phase is the sole writer. | Code: write the key only when Healthy. No migration (no pre-existing processor keys to convert). `[VERIFIED: RedisProjectionWriter.cs:104-106]` |
| Live service config | RabbitMQ: the WebApi already binds `ReceiveEndpoint(ProcessorQueues.IdentityQuery/SchemaQuery)` (Phase 25). The processor's request clients SEND to `exchange:{those names}` — no new broker topology the processor must declare beyond what MassTransit auto-creates for the request reply queue. | None — responder endpoints exist; reply queues are temporary/auto. `[VERIFIED: ResponderMessaging.cs:38-41]` |
| OS-registered state | None — no Task Scheduler / pm2 / systemd registration in scope (Dockerfile/compose is Phase 28). | None — verified by scope (CONTEXT deferred). |
| Secrets/env vars | New appsettings keys (`Interval`,`Ttl`,`RequestTimeout`,`BackoffCap`) under a processor section. No secrets. RabbitMq/Redis conn strings inherited from `BaseConsole`. | Add keys to the (later, Phase 28) concrete appsettings; defaults baked into `ProcessorLivenessOptions`. |
| Build artifacts | None this phase — the SourceHash MSBuild embed is Phase 28. This phase READS whatever `AssemblyMetadata("SourceHash")` exists (possibly absent in tests → stub supplies it). | None — `ISourceHashProvider` default returns the metadata value (or a documented fallback when the attribute is absent; see Open Questions Q2). |

## Common Pitfalls

### Pitfall 1: `JsonPropertyName` on positional records must use `[property: ...]`
**What goes wrong:** A bare `[JsonPropertyName]` on a positional record parameter binds to the *parameter*, and System.Text.Json ignores it — producing PascalCase JSON the reader can't match.
**Why it happens:** Record positional-parameter attribute targeting.
**How to avoid:** This is already solved in the shared types (`[property: JsonPropertyName(...)]` on `ProcessorProjection`/`LivenessProjection`). **Reusing the shared types (D-09) sidesteps this entirely** — do not redefine. `[VERIFIED: ProcessorProjection.cs:9-10, LivenessProjection.cs:7-9]`
**Warning signs:** A round-trip test where `JsonSerializer.Deserialize<ProcessorProjection>(written)` yields null `Liveness`.

### Pitfall 2: `interval` units — seconds, not the `Interval` TimeSpan
**What goes wrong:** Writing `interval` as milliseconds or a TimeSpan tick count makes the reader's `timestamp + interval×2` deadline absurdly far in the future or past.
**Why it happens:** The config `Interval` is a seconds-int; the projection `interval` field is also a seconds-int — but it's easy to write `(int)heartbeatDelay.TotalMilliseconds`.
**How to avoid:** Write `liveness.interval = IntervalSeconds` (the same integer used to compute the heartbeat delay). LIVE-03 + reader at `ProcessorLivenessValidator.cs:55` (`liveness.Timestamp.AddSeconds(liveness.Interval * 2)`). `[VERIFIED: ProcessorLivenessValidator.cs:54-55]`
**Warning signs:** Reader reports `stale` immediately, or never goes stale even when the processor is dead.

### Pitfall 3: Heartbeat starting before Healthy
**What goes wrong:** If the heartbeat writes before identity/definitions resolve, it either NREs on a null `Id`/projection or registers a non-Healthy replica as live.
**Why it happens:** Both `BackgroundService`s start at host-start; the orchestrator hasn't populated `IProcessorContext` yet.
**How to avoid:** The heartbeat checks `IProcessorContext.IsHealthy` (and a populated `Id`) at the top of each tick and no-ops until set (LIVE-04). It does NOT need to *wait*; a no-op tick is correct. `[VERIFIED: D-07, LIVE-04]`
**Warning signs:** `NullReferenceException` on `Id` in the heartbeat at boot; an `absent`-expected replica reading as live.

### Pitfall 4: Request-client target scheme / unconfigured endpoint
**What goes wrong:** Registering `AddRequestClient<T>()` with no address publishes the request; if no responder is bound to the published type's exchange the request times out forever — masking the boot-before-register case.
**Why it happens:** The WebApi binds responders on *named* endpoints (`processor-identity-query`), not on the message-type-default exchange.
**How to avoid:** Register with an explicit `new Uri("exchange:" + ProcessorQueues.IdentityQuery)` / `... + ProcessorQueues.SchemaQuery`. `[CITED: masstransit.io/.../requests]` `[VERIFIED: ResponderMessaging.cs:38-41]`
**Warning signs:** Every identity request times out even when the WebApi is up and the DB row exists.

### Pitfall 5: Redis fault crashing the host (D-11)
**What goes wrong:** An unguarded `StringSetAsync` on a beat throws `RedisConnectionException` and faults the `BackgroundService`, taking the host down.
**Why it happens:** Soft-dep Redis can be unreachable at any beat.
**How to avoid:** Wrap each write in try/catch, log a warning, continue the loop (never `throw`, never `return`). `[VERIFIED: D-11; mirror RedisProjectionWriter's structured-warning posture]`
**Warning signs:** Processor host exits when Redis bounces; orchestrator sees the processor flap between live and absent on every Redis blip rather than sliding gracefully to stale.

### Pitfall 6: Bumping MassTransit to 9.x
**What goes wrong:** MassTransit 9.x is commercial-licensed; an accidental bump introduces a licensing obligation.
**How to avoid:** Stay on the CPM-pinned 8.5.5. Add only `<PackageReference Include="MassTransit" />` (no `Version=`). `[VERIFIED: Directory.Packages.props:133-138]`

## Code Examples

### §1 — Composition root + gate removal (mirror `Orchestrator/Program.cs`)
```csharp
// Source: src/Orchestrator/Program.cs:52-68 (VERIFIED) — adapted into AddBaseProcessor.
// Concrete Program.cs target shape (BPC-03 — minimal):
//   var builder = Host.CreateApplicationBuilder(args);
//   builder.AddBaseConsoleObservability(builder.Configuration);
//   builder.Services.AddBaseProcessor(builder.Configuration);   // <-- everything below lives here
//   var host = builder.Build(); await host.RunAsync();

public static IServiceCollection AddBaseProcessor(this IServiceCollection services, IConfiguration cfg)
{
    services.AddBaseConsole(cfg);                                 // Redis soft-dep + embedded health
    services.AddBaseConsoleMessaging(cfg, x =>                    // bus + 3 correlation filters
    {
        // Pattern 2 — request clients targeting the Phase 25 named responder endpoints:
        x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
        x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));
        // NO AddConsumer this phase (dispatch consumer is Phase 27).
    });

    services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"));   // CONFIG-01
    services.TryAddSingleton(TimeProvider.System);               // VERIFIED Orchestrator/Program.cs:59
    services.AddSingleton<ISourceHashProvider, AssemblyMetadataSourceHashProvider>();
    services.AddSingleton<IProcessorContext, ProcessorContext>();
    services.AddHostedService<ProcessorStartupOrchestrator>();   // Loop A + Loop B + MarkReady
    services.AddHostedService<ProcessorLivenessHeartbeat>();     // only-when-Healthy SET..EX

    // D-02 — remove the base StartupCompletionService so MarkReady fires on Healthy, not host-start.
    // VERIFIED verbatim from Orchestrator/Program.cs:63-68:
    foreach (var d in services
                 .Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService))
                 .ToList())
    {
        services.Remove(d);
    }
    return services;
}
```

### §2 — Register + resolve `IRequestClient` (RPC-04)
```csharp
// Source: masstransit.io/documentation/concepts/requests (CITED) + ResponderMessaging.cs (VERIFIED endpoint names).
// Registration (inside AddBaseConsoleMessaging's configureConsumers lambda — see §1):
x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));

// Resolution (constructor-inject into the startup orchestrator):
public ProcessorStartupOrchestrator(
    IRequestClient<GetProcessorBySourceHash> identityClient,
    IRequestClient<GetSchemaDefinition> schemaClient,
    ISourceHashProvider sourceHash,
    IProcessorContext context,
    IStartupGate gate,
    IOptions<ProcessorLivenessOptions> options,
    TimeProvider clock,
    ILogger<ProcessorStartupOrchestrator> logger) { /* ... */ }
```

### §3 — Loop A: dual-response + unbounded retry / bounded backoff (D-03/D-04)
```csharp
// Source: masstransit.io/.../requests (dual GetResponse + response.Is) — CITED;
//         backoff curve from HydrationBackgroundService.cs:30-31,74-81 — VERIFIED.
var requestTimeout = RequestTimeout.After(seconds: opts.RequestTimeoutSeconds);  // ~5–10s (D-04)
var delay = TimeSpan.FromSeconds(1);
var cap   = TimeSpan.FromSeconds(opts.BackoffCapSeconds);                          // ~30s (D-04)

while (!ct.IsCancellationRequested)
{
    try
    {
        var hash = sourceHash.Get();   // IDENT-03 reflection read
        Response response = await identityClient.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
            new GetProcessorBySourceHash(hash), ct, requestTimeout);

        if (response.Is(out Response<ProcessorIdentityFound> found))
        {
            context.SetIdentity(found.Message);   // Id + InputSchemaId? + OutputSchemaId? + ConfigSchemaId?
            break;                                 // Loop A done → proceed to Loop B
        }
        // ProcessorIdentityNotFound → boot-before-register; fall through to backoff.
        logger.LogInformation("Processor row not yet registered for hash {Hash}; retrying in {Delay}", hash, delay);
    }
    catch (RequestTimeoutException)               // responder slow/absent → fail fast, re-loop (D-04)
    {
        logger.LogWarning("Identity request timed out; retrying in {Delay}", delay);
    }
    try { await Task.Delay(delay, clock, ct); } catch (OperationCanceledException) { return; }
    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, cap.TotalSeconds));
}
```
> Note: `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` is the `FakeTimeProvider`-drivable overload (.NET 8). The repo's `HydrationBackgroundService` uses the 2-arg `Task.Delay(delay, ct)`; for `FakeTimeProvider`-controlled tests prefer the 3-arg `TimeProvider` overload. `[ASSUMED — overload exists in .NET 8 BCL; Assumptions A3]`

### §3b — Loop B: per non-null schema Id (SCHEMA-01/02)
```csharp
// Only input + output; config schema is NEVER resolved (D-05 / SCHEMA-02).
foreach (var schemaId in new[] { context.InputSchemaId, context.OutputSchemaId })
{
    if (schemaId is not { } id) continue;          // null → skipped by design (SCHEMA-02)
    // ... same retry/backoff loop as §3, calling:
    var resp = await schemaClient.GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>(
        new GetSchemaDefinition(id), ct, requestTimeout);
    if (resp.Is(out Response<SchemaDefinitionFound> def))
        context.SetDefinition(id, def.Message.Definition);   // store input/output Definition
    // SchemaDefinitionNotFound | timeout → backoff + retry
}
context.MarkHealthy();   // identity + ALL required non-null defs resolved (LIVE-04 meaning of "Healthy")
gate.MarkReady();        // D-02 — /startup + /ready flip green HERE, not at host-start
```

### §4 — `ISourceHashProvider` default reflection impl (IDENT-03)
```csharp
// Source: BCL System.Reflection (ASSUMED — standard pattern; Assumptions A1).
public interface ISourceHashProvider { string Get(); }

public sealed class AssemblyMetadataSourceHashProvider : ISourceHashProvider
{
    public string Get()
    {
        // The hash describes the IMPLEMENTATION assembly (the concrete + BaseProcessor.Core).
        // The embed target (Phase 28) emits it onto the entry assembly.
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var value = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                       .FirstOrDefault(a => a.Key == "SourceHash")?.Value;
        return value ?? throw new InvalidOperationException(
            "Assembly metadata 'SourceHash' is missing. The MSBuild embed target (IDENT-01/02, Phase 28) " +
            "must emit [assembly: AssemblyMetadata(\"SourceHash\", \"<64-hex>\")].");
    }
}
// Tests register a stub returning a known 64-hex hash (D-13) — no assembly attribute needed in test.
```
> Open Question Q2: whether `Get()` should throw or return a sentinel when the attribute is absent. Recommend **throw** (fail-fast) — a processor with no SourceHash can never resolve identity, so failing loudly at first read beats an infinite not-found retry on an empty hash.

### §5 — Liveness heartbeat (LIVE-01..06) — sliding whole-value SET
```csharp
// Source: RedisProjectionWriter.cs (clock + StringSet) + ProcessorLivenessValidator.cs (read shape) — VERIFIED.
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var opts = _options.Value;
    var period = TimeSpan.FromSeconds(opts.IntervalSeconds);
    while (!stoppingToken.IsCancellationRequested)
    {
        if (_context.IsHealthy && _context.Id is { } id)        // LIVE-04 — only-when-Healthy gate
        {
            try
            {
                var now = _clock.GetUtcNow().UtcDateTime;         // SAME clock as the reader (Pitfall 2)
                var projection = new ProcessorProjection(         // D-09 — REUSE the shared record
                    _context.InputDefinition,                     // string? (null when no input schema)
                    _context.OutputDefinition,                    // string?
                    new LivenessProjection(now, opts.IntervalSeconds, LivenessStatus.Healthy)); // LIVE-03 seconds
                var json = JsonSerializer.Serialize(projection);
                var db = _redis.GetDatabase();
                await db.StringSetAsync(
                    L2ProjectionKeys.Processor(id), json,
                    expiry: TimeSpan.FromSeconds(opts.TtlSeconds)); // LIVE-02 sliding SET..EX, LIVE-06 blind whole-value
            }
            catch (Exception ex)                                  // D-11 — log-and-continue, never crash
            {
                _logger.LogWarning(ex, "Liveness write failed for processor {ProcessorId}; will retry next beat", _context.Id);
            }
        }
        try { await Task.Delay(period, _clock, stoppingToken); } catch (OperationCanceledException) { return; }
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Processor liveness written by `RedisProjectionWriter` (per-proc TTL'd loop) | External self-registration only; writer creates ZERO processor keys | Phase 22 PROC-NOCREATE-01 | This phase is the **sole** writer of `skp:{processorId}`. `[VERIFIED: RedisProjectionWriter.cs:104-106]` |
| WebApi bus = publish-only firewall | WebApi hosts two dual-response responders | Phase 25 (RPC-01/02/03) | The processor's `IRequestClient`s have live responders to query. `[VERIFIED: ResponderMessaging.cs]` |
| `MassTransit` CPM-pinned but unreferenced on console side | First `IRequestClient` request/response usage | This phase (RPC-04) | New request/response surface; no existing in-repo `IRequestClient` precedent to copy — rely on official docs + Phase 25 contracts. `[VERIFIED: grep — no IRequestClient in src/]` |

**Deprecated/outdated:** None relevant. Note MassTransit 9.x is commercial — stay on 8.5.5.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `assembly.GetCustomAttributes<AssemblyMetadataAttribute>()` + `.Key=="SourceHash"` is the correct BCL read for `[assembly: AssemblyMetadata("SourceHash", …)]` | Code Examples §4 / Don't Hand-Roll | Low — standard BCL; if the embed target uses a different attribute (Phase 28), the seam's default impl is swapped, tests stub regardless. Verify against Phase 28's embed target when written. |
| A2 | `RequestTimeout.After(...)` and the per-call `GetResponse(msg, ct, timeout)` overload accept the timeout as shown | Code Examples §3 | Low — `RequestTimeout.After`/`.None`/`.Default` confirmed by official docs; exact overload arg order should be confirmed against MassTransit 8.5.5 XML docs / IntelliSense at implementation time. `[CITED: masstransit.io/.../requests]` |
| A3 | `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` 3-arg overload exists in .NET 8 for `FakeTimeProvider` control | Code Examples §3 note | Low — added in .NET 8; the repo currently uses the 2-arg form. If absent, use `PeriodicTimer(period, timeProvider)` (also .NET 8) which `FakeTimeProvider` advances. |
| A4 | `response.Is(out Response<T>)` is available on the `Response` returned by the 2-type `GetResponse` in 8.5.5 | Code Examples §3 | Low — documented; the deconstruction `switch ((_, T) => ...)` form is the documented fallback if `.Is` is unavailable. `[CITED: masstransit.io/.../requests]` |
| A5 | `exchange:` (not `queue:`) is the correct scheme for the request-client `Uri` targeting the WebApi's named `ReceiveEndpoint` | Pattern 2 / Pitfall 4 | Medium — both schemes exist; `exchange:{name}` routes to the endpoint's exchange (the normal case for a `ReceiveEndpoint`). Confirm with a harness round-trip test (Validation Architecture covers this). CONTEXT D-03 explicitly says "sender adds the `exchange:`/`queue:` scheme", endorsing `exchange:`. |

## Open Questions (RESOLVED)

1. **`IProcessorContext.IsHealthy` representation (flag vs `TaskCompletionSource`)?**
   - What we know: D-06 leaves this to discretion; Phase 27's consumer must *wait* until Healthy to bind `queue:{processorId:D}`.
   - What's unclear: whether Phase 27 needs to *await* Healthy (favoring a `TaskCompletionSource<ProcessorIdentity>` the consumer-binding code can `await`) or merely *poll* a bool.
   - Recommendation: Expose **both** — a `volatile bool IsHealthy` (cheap per-beat read for the heartbeat) AND a `Task WhenHealthy` (a `TaskCompletionSource` completed at `MarkHealthy()`) for Phase 27 to await. This costs nothing now and forward-fits Phase 27's queue-bind-after-Healthy requirement (EXEC-01). Use `Volatile`/`Interlocked` like `StartupGate`. `[VERIFIED: StartupGate.cs:38-44 for the latch idiom]`

2. **`ISourceHashProvider.Get()` when the attribute is absent — throw or sentinel?**
   - Recommendation: **Throw** (fail-fast). See Code Examples §4. A null/empty hash would otherwise drive an infinite, silent not-found retry.

3. **One orchestrator service or two? Heartbeat folded or separate?**
   - Recommendation: **One** startup orchestrator (Loop A + Loop B) + **one separate** heartbeat `BackgroundService`, per D-01's stated preference. The heartbeat self-gates on `IsHealthy`, so it can start at host-start and no-op until Healthy — no ordering dependency between the two services. PROJECT.md:23 notes heartbeat launches "after identity" conceptually; the self-gate satisfies that without explicit sequencing.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | Build/run the library | ✓ (assumed — solution builds on 8.0.421) | 8.0.x | — |
| RabbitMQ (compose) | Live IRequestClient round-trip (E2E is Phase 28) | n/a this phase (tests use in-memory harness) | — | MassTransit in-memory `ITestHarness` |
| Redis (compose, `localhost:6380`) | Real-Redis JSON round-trip assertion (D-13) | ✓ (used by existing `RedisFixture` tests) | 2.13.1 client | A fake/in-memory Redis if compose isn't up (but real Redis is the existing repo pattern) |

**Missing dependencies with no fallback:** None — all unit/in-memory validation needs are met by packages already in `tests/BaseApi.Tests`.
**Missing dependencies with fallback:** Real broker not needed (in-memory harness covers Loop A/B). Real-stack E2E is explicitly Phase 28.

## Validation Architecture

> nyquist_validation is enabled (config.json `workflow.nyquist_validation: true`). This section seeds the VALIDATION.md (Nyquist) strategy.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 (Microsoft.Testing.Platform runner) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`, `TestingPlatformDotnetTestSupport=true`) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter-class "*Processor*"` (MTP filter via `-- --filter-class` per MEMORY) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

> Recommendation: add `BaseProcessor.Core` as a `ProjectReference` to the existing `tests/BaseApi.Tests` project (mirrors how Phase 18 added `BaseConsole.Core` and Phase 19 added `Orchestrator` — see `BaseApi.Tests.csproj:119-127`). Place tests under `tests/BaseApi.Tests/Processor/`. No new test project needed.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| BPC-01/03 | `AddBaseProcessor` registers the full graph (Redis, health, bus, request clients, orchestrator, heartbeat) + removes `StartupCompletionService` | unit (descriptor inspection, like `ConsoleTestHostFixture.RegisteredDescriptors`) | `dotnet test --filter-class "*AddBaseProcessorFacts*"` | ❌ Wave 0 |
| BPC-02/D-12 | `BaseProcessor` abstract class compiles; a test double overrides `ProcessAsync`; DI-resolves | unit | `*BaseProcessorSeamFacts*` | ❌ Wave 0 |
| IDENT-03 | `AssemblyMetadataSourceHashProvider` reads the attribute; throws when absent; stub injects known hash | unit | `*SourceHashProviderFacts*` | ❌ Wave 0 |
| IDENT-04 / RPC-04 | Loop A: in-memory harness with a responder that returns NotFound twice then Found → context populated; assert retry happened | integration (in-memory harness) | `*IdentityResolutionFacts*` | ❌ Wave 0 |
| SCHEMA-01/02 | Loop B: non-null Ids resolved (retry→found); null Ids skipped (no request sent); config Id never queried | integration (harness) | `*SchemaResolutionFacts*` | ❌ Wave 0 |
| LIVE-01..06 | Heartbeat writes `skp:{id:D}` only when Healthy; not before; whole-value SET with TTL | integration (FakeTimeProvider + RedisFixture) | `*LivenessHeartbeatFacts*` | ❌ Wave 0 |
| LIVE-05 (closed loop) | Written JSON deserializes via `JsonSerializer.Deserialize<ProcessorProjection>` AND passes `ProcessorLivenessValidator.ValidateAsync` as "live"; advance `FakeTimeProvider` past `interval×2` → reader sees `stale` | integration (RedisFixture + the real validator) | `*LivenessReaderRoundTripFacts*` | ❌ Wave 0 |
| LIVE-03 (units) | `interval` written == configured `IntervalSeconds` | unit/assertion within the round-trip test | (same) | ❌ Wave 0 |
| D-11 | Redis fault on a beat → warning logged, loop continues, host stays up | integration (dead-Redis multiplexer like `ConsoleTestHostFixture`) | `*LivenessResilienceFacts*` | ❌ Wave 0 |
| CONFIG-01 | `ProcessorLivenessOptions` binds `Interval`/`Ttl` independently from `Processor` section | unit | `*ProcessorOptionsBindingFacts*` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter-class "*Processor*"` (the new processor test slice).
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (guards no regression in the existing console/orchestrator/CRUD suites).
- **Phase gate:** full suite green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] Add `<ProjectReference Include="..\..\src\BaseProcessor.Core\BaseProcessor.Core.csproj" />` to `tests/BaseApi.Tests/BaseApi.Tests.csproj`.
- [ ] `tests/BaseApi.Tests/Processor/` test folder + a harness fixture mirroring `ConsoleCorrelationFilterTests`'s `AddMassTransitTestHarness` + a stub responder for `GetProcessorBySourceHash`/`GetSchemaDefinition` that can be sequenced (NotFound→NotFound→Found).
- [ ] Reuse the existing `RedisFixture` (`localhost:6380`, known-key cleanup via `Track`) for the L2 round-trip — **track `skp:{testProcessorId:D}`** so teardown deletes it (triple-SHA close-gate discipline).
- [ ] `FakeTimeProvider` registration in the heartbeat tests (already pinned: `Microsoft.Extensions.TimeProvider.Testing 8.10.0`).
- Framework install: none — xunit.v3 + harness + FakeTimeProvider + StackExchange.Redis + NSubstitute all already referenced.

## Security Domain

> `security_enforcement` is not set in config.json → treated as enabled. This is an internal backend library (no external request surface beyond the inherited minimal health probes; no auth/session/PII).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No user auth surface; bus credentials inherited from `BaseConsole` config. |
| V3 Session Management | no | Stateless processor. |
| V4 Access Control | no | No HTTP surface beyond health probes (live/ready/startup), which expose no data. |
| V5 Input Validation | yes (defensive deserialize) | The L2 value the processor *writes* is its own; but the reader (`ProcessorLivenessValidator`) already defensively `try/catch`es malformed JSON → 422. The processor reads the WebApi's dual-response messages — typed contracts (no free-form parsing). Schema-definition *content* validation is Phase 27 (input/output validation), not this phase. |
| V6 Cryptography | no (this phase) | SourceHash is SHA-256 but the *compute* is Phase 28; this phase only reads a string. No secrets crypto here. |
| V7 Error Handling / Logging | yes | Fail-fast config (`cfg.Require` names the key, never the value); structured warnings on Redis fault (no secret leakage). Mirror `RequiredConfig` posture. `[VERIFIED: RequiredConfig.cs:21-23]` |

### Known Threat Patterns for .NET Generic-Host + bus + Redis
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malformed/poisoned L2 value read by orchestrator | Tampering | Reader already maps malformed → 422 (unchanged, untouched). Writer reuses the typed record so it cannot emit a wrong shape. `[VERIFIED: ProcessorLivenessValidator.cs:42-52]` |
| Liveness spoofing (a non-Healthy replica appearing live) | Spoofing | Only-when-Healthy write gate (LIVE-04) — a non-Healthy replica writes nothing, so it's `absent`. |
| Config-key value leaked in exception | Information Disclosure | `cfg.Require` names the *key* only; never logs values. `[VERIFIED: RequiredConfig.cs:21-23]` |
| Host crash on dependency fault (DoS via Redis bounce) | Denial of Service | D-11 log-and-continue; soft-dep Redis (`abortConnect=false`) — host never crashes on Redis/RabbitMQ outage. `[VERIFIED: ConsoleRedisServiceCollectionExtensions.cs]` |
| Unbounded retry resource exhaustion | DoS | Bounded backoff cap (~30s) between attempts; one in-flight request at a time per loop. `[VERIFIED: HydrationBackgroundService.cs:81 pattern]` |

## Sources

### Primary (HIGH confidence)
- `src/BaseConsole.Core/DependencyInjection/{BaseConsoleServiceCollectionExtensions,MessagingServiceCollectionExtensions,ConsoleRedisServiceCollectionExtensions,BaseConsoleObservabilityExtensions}.cs` — composition seam `AddBaseProcessor` builds on.
- `src/BaseConsole.Core/Health/{IStartupGate,StartupCompletionService,StartupHealthCheck}.cs` — the gate to re-point (D-02).
- `src/Orchestrator/Program.cs` (lines 18-71) — the verbatim composition + gate-removal template.
- `src/Orchestrator/Hydration/HydrationBackgroundService.cs` — the bounded-backoff / MarkReady-on-completion `BackgroundService` template.
- `src/Messaging.Contracts/Projections/{ProcessorProjection,LivenessProjection,L2ProjectionKeys,LivenessStatus}.cs` — frozen L2 shape (reuse verbatim).
- `src/Messaging.Contracts/{ProcessorQueries,ProcessorQueues}.cs` — dual-response contracts + endpoint names.
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — the reader to satisfy (interval×2 seconds math, malformed→422).
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` — TimeProvider clock + StringSet precedent; PROC-NOCREATE confirmation.
- `src/BaseApi.Service/Composition/ResponderMessaging.cs` + `Features/{Processor,Schema}/Responders/*.cs` — the named responder endpoints the request clients target.
- `tests/BaseApi.Tests/{Console/ConsoleCorrelationFilterTests.cs,Console/ConsoleTestHostFixture.cs,Composition/RedisFixture.cs,BaseApi.Tests.csproj}` — the in-memory harness + Redis fixture validation discipline (D-13).
- `Directory.Packages.props` — all version pins (MassTransit 8.5.5, StackExchange.Redis 2.13.1, FakeTimeProvider 8.10.0, xunit.v3 3.2.2).
- `.planning/{REQUIREMENTS.md,ROADMAP.md,PROJECT.md}` — requirement wording + milestone narrative (PROJECT.md:23 loop ordering; :32 ProcessAsync signature).

### Secondary (MEDIUM confidence)
- `https://masstransit.massient.com/documentation/concepts/requests` (official MassTransit docs, redirected from masstransit.io) — `AddRequestClient(Uri)`, dual `GetResponse<T1,T2>`, `response.Is(out Response<T>)`, `RequestTimeout.After/None/Default`, `RequestTimeoutException`.

### Tertiary (LOW confidence)
- Context7 `/masstransit/masstransit` — returned only MessageData/MongoDB snippets for the request-client query; NOT used for the request-client API (official docs used instead). Flagged so the planner knows Context7 was non-productive here.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all versions read from live CPM file; all base types read from source.
- Architecture: HIGH — `Orchestrator/Program.cs` + `HydrationBackgroundService` are near-exact templates; gate-removal pattern verified line-by-line.
- IRequestClient API: MEDIUM-HIGH — official docs verified; no in-repo precedent, so exact 8.5.5 overload arg order (A2) and `exchange:` scheme (A5) should be confirmed with a harness round-trip test during Wave 0.
- Liveness write: HIGH — reader contract + clock + key/status constants all verified in source.
- Pitfalls: HIGH — each traces to a specific verified file/line.

**Research date:** 2026-06-01
**Valid until:** 2026-07-01 (stable — frozen CPM pins; MassTransit held at 8.5.5; codebase is the source of truth)
