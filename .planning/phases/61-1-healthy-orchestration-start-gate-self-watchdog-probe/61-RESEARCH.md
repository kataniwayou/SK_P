# Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe - Research

**Researched:** 2026-06-13
**Domain:** .NET 8 / C# — StackExchange.Redis SET reads, ASP.NET Core HealthChecks, cross-library DI bridging, RFC 7807 ProblemDetails
**Confidence:** HIGH (every claim verified against in-repo source or StackExchange.Redis upstream; no LLM/JS instincts apply)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01 — probe augments `/health/live`, processor-scoped only.** Watchdog joins the existing `/health/live` probe **for processors only**. `Orchestrator`/`Keeper` `/health/live` stay self-only, unchanged. The watchdog never consults Redis/RMQ — it reads only the in-process L1 loop-liveness signal.
- **D-02 — `IProcessorLivenessState.Current == null ⇒ Unhealthy` ("liveness loop not started").** A loop that crashed before its first write is caught. Boot coverage is the future K8s `startupProbe`'s job (out of scope).
- **D-03 — verdict math: recorded-interval staleness.** Fresh ⇒ `Healthy`; `now > Current.Timestamp + Current.Interval × 2` ⇒ `Unhealthy`. Interval is read from the entry (heartbeat=10s, startup-unhealthy=30s anchor). Use the same `TimeProvider`/`_clock.GetUtcNow().UtcDateTime` discipline as the writer + gate. Identical math to the gate.
- **D-04 — `summary` in body (PROBE-02).** Probe returns `{inputSchema, outputSchema, configSchema}` via the `HealthCheckResult` `data` dictionary; `UIResponseWriter.WriteHealthCheckUIResponse` serializes per-check `data`. Exact `data` key names = Claude's discretion; the three summary fields must be present.
- **D-05 — generic pluggable-check hook in `BaseConsole.Core`.** `BaseConsole.Core` is a leaf `BaseProcessor.Core` depends on (not the reverse) — so `EmbeddedHealthEndpointService` cannot reference a `BaseProcessor` check type. Add a generic seam: a collection of additional health-check descriptors (name + tags + factory) registered in the OUTER container; `EmbeddedHealthEndpointService` enumerates them and folds them into its inner Kestrel container, bridging the OUTER `IServiceProvider` in — exactly the `BusReadyHealthCheck(_outer)` pattern. `BaseProcessor.Core` then registers `LivenessWatchdogHealthCheck` (reading `IProcessorLivenessState`) tagged `"live"`.
- **D-06 — replica discovery: `SMEMBERS` then `GET`-each.** Validator does `SMEMBERS skp:proc:{processorId}` (`L2ProjectionKeys.InstanceIndex(proc.Id)`) with no prior knowledge of instanceIds, then `GET`s each `L2ProjectionKeys.PerInstance(proc.Id, instanceId)`. Empty/missing index SET → zero replicas → fails the gate.
- **D-07 — verdict: first-qualifier-wins, ≥1.** Processor PASSES iff ≥1 discovered replica is present AND `status == LivenessStatus.Healthy` AND `liveness.Timestamp.AddSeconds(entry.Interval * 2) > now`. Compare against `LivenessStatus`/`SchemaOutcome` consts, never literals. Deserialize each value to `ProcessorLivenessEntry` (NOT `ProcessorProjection`).
- **D-08 — no-qualifier → 422 with aggregate reason; malformed = fail-that-replica.** Throw `OrchestrationValidationException.ProcessorNotLive(proc.Id, reason)` → 422 + RFC 7807. `reason` is an aggregate with a replica-count breakdown (e.g. `"no healthy replica (3 checked: 1 absent, 1 unhealthy, 1 stale)"`). Present-but-malformed per-instance JSON (bad shape / null liveness / `JsonException`) fails that replica (counted like `unhealthy`) — never a 500. Preserves the WR-01 reasoning.
- **D-09 — lazy-`SREM`: absent-only, fire-and-forget.** Only members whose per-instance `GET` returns null (TTL-expired) are `SREM`'d from the index. Present-but-stale and present-but-unhealthy keys are NOT pruned. The `SREM` is fire-and-forget — a failure never changes the verdict, never surfaces as a 500.
- **D-10 — genuine Redis fault → 500, unchanged.** A real `RedisException` on `SMEMBERS`/`GET` still propagates as a 500. Only deterministic absent/unhealthy/stale/malformed *data* states map to 422.
- **D-11 — delete the old contract.** With the validator swapped, delete `L2ProjectionKeys.Processor(Guid)`, the `RedisProjectionKeys.Processor` forwarder, and the `ProcessorProjection` record. Re-point any hermetic tests that pinned the old key string or `ProcessorProjection` round-trip onto the new `PerInstance`/`InstanceIndex` + `ProcessorLivenessEntry` shapes. NEVER touch the SHARED `LivenessProjection`.

### Claude's Discretion

- Exact type/file name for the watchdog check (`LivenessWatchdogHealthCheck` suggested) and the `BaseConsole.Core` hook seam (e.g. a `HealthCheckDescriptor` record + an `IEnumerable<…>`/options-registered collection). The *shape* (outer-provider-bridged, `live`-tagged, additive) is locked.
- Exact `data`-dictionary key names carrying the `summary` (input/output/config outcomes all present).
- Precise wording/format of the aggregate 422 `reason` string; whether per-state counts are a formatted string vs a structured `ProcessorLivenessOffending` extension — keep the existing `ProcessorLivenessOffending(procId, reason)` shape unless a structured breakdown is trivially additive.
- `SREM` batching/pipelining mechanics for absent members — as long as fire-and-forget + absent-only.
- Whether per-instance values are read sequentially or pipelined/`Task.WhenAll` — behavior-equivalent; pick for clarity (small replica count).

### Deferred Ideas (OUT OF SCOPE)

- RealStack/live proof (two replicas, restart-as-unhealthy, ≥1-healthy admit / none-qualify 422, stale-L1 probe fail) + triple-SHA net-zero close gate → **Phase 62** (TEST-01/02/03).
- K8s `livenessProbe` wiring to `/health/live` + `startupProbe` boot coverage + pod-restart policy → future.
- Mid-life health re-validation (`healthy → unhealthy` within a process) → out of scope; frozen-healthy this milestone.
- Repointing the two observability `instanceId` copies to `InstanceId.Resolve()` → optional sweep, not required.
- Workflow-root liveness (`LivenessProjection` with status `active`) → out of scope for the whole milestone. Do NOT touch the SHARED `LivenessProjection`.
- Per-field Redis TTL (`HEXPIRE` / Redis 7.4 hash-field expiry) → explicitly not used.
- Gate A / Gate B compatibility logic → unchanged; this milestone only surfaces their result into `summary`.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| GATE-01 | Validator discovers replicas via `SMEMBERS skp:proc:{processorId}` → `GET` each per-instance key (no prior instanceId knowledge). | `L2ProjectionKeys.InstanceIndex(Guid)` / `PerInstance(Guid,string)` already exist (Phase 59); `IDatabase.SetMembersAsync` returns `Task<RedisValue[]>` (verified). Precedent: `HydrationBackgroundService.cs:42` already calls `db.SetMembersAsync(...)`. |
| GATE-02 | ≥1 replica present AND `status=Healthy` AND non-stale (`timestamp + interval×2 > now`). | `ProcessorLivenessEntry` (Timestamp, Interval, Status, Summary) + `LivenessStatus.Healthy` const. Staleness math identical to the shipped validator line 55. |
| GATE-03 | No-qualifier → 422 + RFC 7807; absent/TTL-expired member skipped + lazily `SREM`'d. | `OrchestrationValidationException.ProcessorNotLive` → `OrchestrationValidationExceptionHandler` 422. `SetRemoveAsync(key, value, CommandFlags.FireAndForget)` verified. |
| PROBE-01 | Processor probe reads L1, reports `unhealthy` when L1 timestamp stale beyond active-interval ×2. | `IProcessorLivenessState.Current` (`ProcessorLivenessEntry?`, null until first write). `IHealthCheck` returns `HealthCheckResult.Unhealthy(...)`. |
| PROBE-02 | Probe returns per-schema `summary` in its body. | `LivenessSummary {InputSchema, OutputSchema, ConfigSchema}` carried via `HealthCheckResult.data`; `UIResponseWriter.WriteHealthCheckUIResponse` serializes per-check `data`. |
</phase_requirements>

## Summary

This phase has two independent reader-side deliverables plus a teardown, all in a .NET 8 / C# codebase. **(a)** Swap `ProcessorLivenessValidator` from a single-key `GET skp:{procId}` + `ProcessorProjection` deserialize to `SMEMBERS skp:proc:{procId}` (instance index) → `GET`-each per-instance key → deserialize `ProcessorLivenessEntry` → admit iff ≥1 replica is `Healthy` + non-stale. **(b)** Add a processor-scoped `IHealthCheck` (`LivenessWatchdogHealthCheck`) reading the singleton `IProcessorLivenessState.Current`, surfaced on `/health/live` via a new generic descriptor-collection hook in `BaseConsole.Core` (because the dependency direction forbids a direct type reference). **(c)** Delete `L2ProjectionKeys.Processor`, the `RedisProjectionKeys.Processor` forwarder, and `ProcessorProjection`.

The load-bearing contract to preserve is the **422-vs-500 split**: deterministic *data* states (absent / unhealthy / stale / malformed-value) → 422 via `OrchestrationValidationException`; transport `RedisException` → 500. This split is enforced at TWO layers and the planner must respect both: the validator throws `OrchestrationValidationException` (not a `RedisException`) for data states, and the `OrchestrationService.StartAsync` wrapper at line 193–201 catches only `RedisException` to tag `redisOp` for the 500 path — the domain exception flows past it untouched to the 422 handler.

**Primary recommendation:** Build the gate by mechanically generalizing the existing single-key loop to a two-level loop (SMEMBERS → per-member GET) inside the SAME `try` discipline, keeping every throw an `OrchestrationValidationException`. Build the probe by copying the `BusReadyHealthCheck(_outer)` outer-provider-bridge pattern verbatim, generalized into a `HealthCheckDescriptor` collection the listener enumerates. Re-point the existing gate facts (they run against a REAL Redis harness, not a mock — see Pitfall 2) and delete the `ProcessorProjection` round-trip pins.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Orchestration-start gate (≥1-healthy) | API / Backend (`BaseApi.Service`) | Redis (L2 read) | The gate is a WebAPI validator in the orchestration-start chain; Redis is the read source the Phase-60 writer populated. |
| Self-watchdog probe verdict | Processor host (`BaseProcessor.Core`) | — | Reads only the in-process singleton L1 holder; never touches Redis/RMQ (D-01). |
| Generic health-check hook seam | Console infra leaf (`BaseConsole.Core`) | Processor host (registers descriptor) | `BaseConsole.Core` owns the embedded Kestrel listener; the seam is generic so the leaf carries no `BaseProcessor` reference (D-05). |
| Per-instance key/index contract | Contract leaf (`Messaging.Contracts`) | Both reader + writer | `L2ProjectionKeys` + `ProcessorLivenessEntry` are the shared SoT consumed by gate (reader) and Phase-60 writer. |
| RFC 7807 422 mapping | API / Backend (`BaseApi.Service`) | — | `OrchestrationValidationExceptionHandler` is the existing claimed `IExceptionHandler`. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| StackExchange.Redis | 2.13.1 (CPM-pinned, `Directory.Packages.props`) | `SetMembersAsync` (SMEMBERS), `StringGetAsync` (GET), `SetRemoveAsync` (SREM) | The repo's only Redis client; key SoT already in `L2ProjectionKeys`. [VERIFIED: Directory.Packages.props grep] |
| Microsoft.Extensions.Diagnostics.HealthChecks | .NET 8 framework | `IHealthCheck`, `HealthCheckResult.Healthy/Unhealthy(description, data:)`, tags + `Predicate` filtering | Already the basis of all three probes (`EmbeddedHealthEndpointService`). [VERIFIED: in-repo usage] |
| AspNetCore.HealthChecks.UI.Client | (CPM-pinned) | `UIResponseWriter.WriteHealthCheckUIResponse` — serializes per-check `data` into the JSON body | Already wired as the `ResponseWriter` for `/health/{live,ready,startup}`. [VERIFIED: EmbeddedHealthEndpointService.cs:1,85] |
| System.Text.Json | .NET 8 framework | Deserialize per-instance value to `ProcessorLivenessEntry` under DEFAULT options | `[property: JsonPropertyName]` pins carry the wire shape; default options (matches the writer's `JsonSerializer.Serialize(entry)` with default options — `ProcessorLivenessWriter.cs:69`). [VERIFIED] |

### Supporting (test)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| xUnit.v3 | 3.2.2 (MTP) | Test framework; `TestContext.Current.CancellationToken` threading | All tests. [VERIFIED: csproj grep] |
| NSubstitute | (CPM-pinned) | Mock `IConnectionMultiplexer`/`IDatabase` and fabricate `IProcessorLivenessState` | Unit-level gate tests + the probe test. The `FakeRedis` double (`tests/BaseApi.Tests/Keeper/FakeRedis.cs`) is a reusable NSubstitute-backed precedent. [VERIFIED] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Task.WhenAll` pipelined per-instance GETs | Sequential `await` loop | Behavior-equivalent; replica count per processor is small (CONTEXT D-07 discretion). Sequential is clearer and avoids interleaving the per-replica count bookkeeping. |
| Aggregate reason as a formatted string | Structured `ProcessorLivenessOffending` extension | CONTEXT says keep the existing `(procId, reason)` shape unless structured is trivially additive. String is the minimal change. |

**Installation:** No new packages. All dependencies are CPM-pinned and already referenced.

**Version verification:** StackExchange.Redis 2.13.1 confirmed in `Directory.Packages.props` (CPM). No `npm`/registry step applies (.NET CPM project).

## Architecture Patterns

### System Architecture Diagram

```
(a) GATE — POST /api/v1/orchestration/start
   HTTP request
      │
      ▼
   OrchestrationService.StartAsync
      │  (sync trio: cycle, schemaEdge, payloadConfig — unchanged)
      ▼
   try { await _processorLivenessValidator.ValidateAsync(snapshot, ct) }   ◄── line 193-201
   catch (RedisException ex) { ex.Data["redisOp"]="UpsertAsync"; throw }   ─────────────► 500 (FallbackHandler)
      │  (OrchestrationValidationException flows PAST this catch — not a RedisException)
      ▼
   ProcessorLivenessValidator.ValidateAsync  (per participating processor)
      │
      ├─► SMEMBERS skp:proc:{procId}        (InstanceIndex)  ── empty/missing ─► 0 replicas ─► 422
      │        │
      │        ▼  for each instanceId
      ├─► GET skp:proc:{procId}:{instanceId} (PerInstance)
      │        │
      │        ├─ null  ─────────► count "absent"  + lazy SREM (fire-and-forget, D-09)
      │        ├─ JsonException / null-liveness ─► count "malformed" (NEVER 500, WR-01)
      │        ├─ status != Healthy ─► count "unhealthy"
      │        ├─ timestamp+interval*2 <= now ─► count "stale"
      │        └─ Healthy + fresh ─► QUALIFIES ─► processor PASSES (short-circuit OK)
      │
      └─ no qualifier ─► throw OrchestrationValidationException.ProcessorNotLive(procId, aggregateReason)
                              │
                              ▼
                         OrchestrationValidationExceptionHandler ─► 422 + RFC 7807 ProblemDetails
                              (errors = { gate:"processorLiveness", offending:{ procId, reason } })

(b) PROBE — GET /health/live  (processor host only)
   HTTP request (inner Kestrel, EmbeddedHealthEndpointService)
      │
      ▼
   HealthCheckService runs all "live"-tagged checks:
      ├─ "self"  (always Healthy — unchanged)
      └─ LivenessWatchdogHealthCheck  ◄── registered via generic descriptor hook (D-05)
              │  resolves IProcessorLivenessState from OUTER provider (singleton)
              ▼
           Current == null ─► Unhealthy("liveness loop not started")        (D-02)
           now > Current.Timestamp + Current.Interval*2 ─► Unhealthy(stale) (D-03)
           else ─► Healthy
              │  (both attach summary{inputSchema,outputSchema,configSchema} to data)
              ▼
   UIResponseWriter.WriteHealthCheckUIResponse ─► JSON body with per-check status + data
```

### Recommended Project Structure (additive/changed files)
```
src/BaseConsole.Core/Health/
├── EmbeddedHealthEndpointService.cs   # CHANGE: enumerate the new descriptor collection into inner container
├── HealthCheckDescriptor.cs           # NEW (suggested): record { Name, Tags, Func<IServiceProvider,IHealthCheck> Factory }
└── BusReadyHealthCheck.cs             # precedent (unchanged)
src/BaseConsole.Core/DependencyInjection/
└── ConsoleHealthServiceCollectionExtensions.cs  # CHANGE/ADD: registration seam for descriptor collection
src/BaseProcessor.Core/Liveness/
└── LivenessWatchdogHealthCheck.cs     # NEW: reads IProcessorLivenessState (outer-bridged)
src/BaseProcessor.Core/DependencyInjection/
└── BaseProcessorServiceCollectionExtensions.cs  # CHANGE: register the descriptor (tag "live")
src/BaseApi.Service/Features/Orchestration/Validation/
└── ProcessorLivenessValidator.cs      # CHANGE: single-key → SMEMBERS→GET-each ≥1-healthy
src/BaseApi.Service/Features/Orchestration/Projection/
└── RedisProjectionKeys.cs             # CHANGE: delete Processor forwarder (D-11)
src/Messaging.Contracts/Projections/
├── L2ProjectionKeys.cs                # CHANGE: delete Processor(Guid) (D-11)
└── ProcessorProjection.cs             # DELETE (D-11)
```

### Pattern 1: Outer-provider-bridged IHealthCheck (the watchdog mirrors this exactly)
**What:** An `IHealthCheck` registered in the INNER listener container, constructed with the OUTER `IServiceProvider`, resolving outer singletons at check time.
**When to use:** The probe must read the outer `IProcessorLivenessState` singleton; the inner Kestrel container cannot see it directly.
**Example:**
```csharp
// Source: src/BaseConsole.Core/Health/BusReadyHealthCheck.cs (in-repo precedent)
public sealed class BusReadyHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;
    public BusReadyHealthCheck(IServiceProvider outer) => _outer = outer;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var bus = _outer.GetService<IBusControl>();      // resolve OUTER singleton at check time
        if (bus is null) return Task.FromResult(HealthCheckResult.Unhealthy("Bus not started"));
        // ...
    }
}
```
The watchdog resolves `_outer.GetService<IProcessorLivenessState>()` the same way. `IProcessorLivenessState` is a Singleton (`BaseProcessorServiceCollectionExtensions.cs:142`), so a single outer instance is shared — no lifetime mismatch (see Open Question / Pitfall 4).

### Pattern 2: Generic descriptor-collection hook (D-05 seam)
**What:** A `HealthCheckDescriptor` record (`Name`, `string[] Tags`, `Func<IServiceProvider, IHealthCheck> Factory`) registered in the OUTER container (e.g. `services.AddSingleton(new HealthCheckDescriptor(...))` or via an options list). `EmbeddedHealthEndpointService.StartAsync` resolves `IEnumerable<HealthCheckDescriptor>` from `_outer`, and for each folds it into the inner `AddHealthChecks()` chain.
**When to use:** Any console wanting to surface a `live`-tagged check that needs outer state, without `BaseConsole.Core` referencing the check's type.
**Example:**
```csharp
// Inside EmbeddedHealthEndpointService.StartAsync, extending the existing AddHealthChecks() chain:
var hc = builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup" })
    .AddCheck<BusReadyHealthCheck>("bus-ready", tags: new[] { "ready" });

foreach (var d in _outer.GetServices<HealthCheckDescriptor>())   // NEW: enumerate outer-registered descriptors
{
    var captured = d;                                            // capture for the closure
    hc.AddCheck(captured.Name, captured.Factory(_outer), tags: captured.Tags);
}
```
Note `AddCheck(string, IHealthCheck instance, …)` accepts a pre-built instance — the factory builds it with `_outer` bridged in. The watchdog descriptor is tagged `"live"` so the existing `/health/live` `Predicate = c => c.Tags.Contains("live")` picks it up with NO mapping change. [VERIFIED: EmbeddedHealthEndpointService.cs:75-86]

### Pattern 3: 422-vs-500 split preserved through TWO layers
**What:** Data states throw `OrchestrationValidationException` (→ 422 handler). Only `RedisException` is caught-and-tagged in the `OrchestrationService` wrapper (→ 500 fallback).
**Example:**
```csharp
// Source: src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:193-201
try { await _processorLivenessValidator.ValidateAsync(snapshot, ct); }
catch (RedisException ex) { ex.Data["redisOp"] = "UpsertAsync"; throw; }  // 500 path ONLY
// OrchestrationValidationException is NOT a RedisException → flows past → 422 handler claims it.
```
The validator must therefore NEVER let a `JsonException` or NRE escape as itself — it must catch and re-throw `OrchestrationValidationException` (the WR-01 discipline, `ProcessorLivenessValidator.cs:42-52`).

### Anti-Patterns to Avoid
- **Throwing a raw `JsonException`/NRE from the validator on malformed data:** escapes the `RedisException` catch as a 500. Always map malformed → `ProcessorNotLive` 422 (WR-01).
- **Comparing `status` against the literal `"Healthy"`:** use `LivenessStatus.Healthy` const (carried discipline; the writer uses the same SoT).
- **Reshaping JSON / configuring custom STJ options on deserialize:** the writer serializes with DEFAULT options (`ProcessorLivenessWriter.cs:69`); the gate must deserialize with default options or the `[JsonPropertyName]` pins carry the shape. Do not add a camelCase policy.
- **Letting the lazy `SREM` block or throw into the verdict:** must be `CommandFlags.FireAndForget` and absent-only (D-09).
- **Pruning present-but-stale / present-but-unhealthy keys:** D-09 prunes absent-only; those keys' TTL is still alive.
- **Touching the SHARED `LivenessProjection`:** out of scope; deleting it breaks the workflow-root path (D-11).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Discover replicas without instanceIds | A `KEYS skp:proc:{id}:*` scan | `SMEMBERS skp:proc:{id}` (`InstanceIndex`) | The Phase-60 writer SADDs each instanceId into the index SET (`ProcessorLivenessWriter.cs:80`); the index is the SoT. Keyspace scans are forbidden (L2-PROJECT-07 convention; `RedisL2Cleanup` never scans). |
| Fire-and-forget SREM | A detached `Task.Run` / discarded task | `db.SetRemoveAsync(key, member, CommandFlags.FireAndForget)` | The flag makes the client not await the result and never surface a fault — exactly D-09's contract. [VERIFIED: CommandFlags.FireAndForget = "caller not interested in result", StackExchange.Redis source] |
| Serialize the probe `data` into JSON body | A custom `ResponseWriter` | `UIResponseWriter.WriteHealthCheckUIResponse` (already wired) | It already serializes per-check `data` dictionaries; the `summary` rides for free (D-04). |
| 422 RFC 7807 mapping | A new `IExceptionHandler` | `OrchestrationValidationException.ProcessorNotLive` + the existing `OrchestrationValidationExceptionHandler` | The handler already maps the domain exception → 422 + `errors={gate,offending}`; Phase-4 customizer adds correlationId/instance. |
| Cross-library check registration | A second Kestrel listener in `BaseProcessor.Core` | The generic `HealthCheckDescriptor` hook in `BaseConsole.Core` (D-05) | One listener, additive seam, reusable by any console; respects the leaf dependency direction. |

**Key insight:** Nearly every mechanic here already exists in-repo (index SADD, outer-provider bridge, 422 handler, UI response writer). The phase is a careful *generalization* of shipped patterns, not new infrastructure.

## Common Pitfalls

### Pitfall 1: The validator's `RedisException` catch is in the CALLER, not the validator
**What goes wrong:** A planner edits the validator's internal `try/catch` and assumes that's where the 500 split lives.
**Why it happens:** The validator itself has no `RedisException` catch — it lets `RedisException` propagate. The catch is in `OrchestrationService.StartAsync:197`.
**How to avoid:** Keep the validator throwing `OrchestrationValidationException` for ALL data states and letting `RedisException` propagate untouched. Do not add a `RedisException` catch inside the validator.
**Warning signs:** A test seeding a dead Redis returns 422 instead of 500.

### Pitfall 2: The gate facts run against a REAL Redis, not a mock — the additional_context "hermetic mock-Redis validator" is inaccurate for these facts
**What goes wrong:** Planning a NSubstitute mock for the existing `ProcessorLivenessFacts` rewrite, when those facts seed `_factory.RedisMultiplexer.GetDatabase()` (a real Redis fixture via `HarnessWebAppFactory` → `Phase8WebAppFactory._redisFixture.Multiplexer`).
**Why it happens:** The CONTEXT/additional_context describes a "hermetic mock-Redis validator"; the NSubstitute `FakeRedis` double exists but is used by the **Keeper** tests, NOT the gate facts.
**How to avoid:** The existing `ProcessorLivenessFacts` are HTTP-level integration tests against real Redis — re-point their seeding to write per-instance keys via `db.StringSetAsync(L2ProjectionKeys.PerInstance(...))` + `db.SetAddAsync(L2ProjectionKeys.InstanceIndex(...))` and serialize a `ProcessorLivenessEntry`. For a *new pure-unit* validator test, the `FakeRedis` NSubstitute double (or a fresh `Substitute.For<IConnectionMultiplexer>()`) is the vehicle. Both approaches are valid; pick per test level.
**Warning signs:** A rewritten fact still references `ProcessorProjection` (deleted by D-11) or `LivenessProjection(now, 300, "Live")` instead of `ProcessorLivenessEntry.Create(...)`.

### Pitfall 3: NSubstitute default-return false-green on `SetMembersAsync`
**What goes wrong:** An unstubbed `IDatabase.SetMembersAsync` returns NSubstitute's default — `Task<RedisValue[]>` resolves to an **empty array** (a 0-length array, not null), making the gate read "zero replicas" and fail with 422 in a test that meant to seed replicas — a false-green or false-red depending on intent.
**Why it happens:** NSubstitute returns `default` for unconfigured members; for `Task<RedisValue[]>` that is `Task.FromResult((RedisValue[])null)` unless the test explicitly stubs it. A null array then NREs in the gate's `foreach`.
**How to avoid:** In any unit test, ALWAYS stub `SetMembersAsync` to return the seeded instanceId array AND stub each per-key `StringGetAsync(PerInstance(...))` to return the serialized entry (or `RedisValue.Null` for absent). Mirror `FakeRedis.BuildDatabase` — it stubs `StringGetAsync`/`StringSetAsync` explicitly via `Arg.Any<...>()`. Stub `SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.FireAndForget)` even if you don't assert on it, so the fire-and-forget call doesn't NRE on an unconfigured Task.
**Warning signs:** A test passes regardless of whether the gate logic is correct; or NRE in the gate loop with "Object reference not set" on the SMEMBERS array.

### Pitfall 4: DI lifetime — Scoped validator vs Singleton watchdog (no mismatch, but verify)
**What goes wrong:** A planner worries the Scoped `ProcessorLivenessValidator` reading Redis conflicts with the Singleton watchdog reading a Singleton L1 holder.
**Why it happens:** They are independent: the validator (Scoped, `OrchestrationServiceCollectionExtensions.cs:76`) injects the Singleton `IConnectionMultiplexer` (no captive bug — singleton-into-scoped is fine). The watchdog is constructed as an instance bridged with the OUTER provider and resolves the Singleton `IProcessorLivenessState` at check time — also fine. No new lifetime relationship is introduced.
**How to avoid:** Register the watchdog descriptor so its factory closes over `_outer` and resolves `IProcessorLivenessState` per check (like `BusReadyHealthCheck`), NOT by capturing the instance at registration time (it may be null before first write, but the holder itself is non-null from construction; `Current` is the volatile that's null until first write — that's the D-02 signal, handled in the check body).
**Warning signs:** `ValidateOnBuild`/`ValidateScopes` failure at host build (the .NET Host enables both in Development); a captive-dependency warning.

### Pitfall 5: `[property: JsonPropertyName]` is load-bearing on `ProcessorLivenessEntry`
**What goes wrong:** Deserializing the per-instance value with non-default STJ options or assuming PascalCase keys.
**Why it happens:** The writer serializes with DEFAULT options (`ProcessorLivenessWriter.cs:69`); the wire keys are `timestamp`/`interval`/`status`/`summary` (and nested `inputSchema`/`outputSchema`/`configSchema`) purely because of the `[property: JsonPropertyName]` attributes.
**How to avoid:** `JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!)` with default options — exactly as the shipped validator deserializes `ProcessorProjection` (`ProcessorLivenessValidator.cs:44`). The positional ctor on `ProcessorLivenessEntry` is `public` specifically for STJ deserialization (documented in the record).
**Warning signs:** A round-trip test where deserialized fields are null/default.

## Code Examples

### Verified gate shape (generalizing the shipped single-key loop)
```csharp
// Derived from src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs
// (shipped single-key version) generalized to SMEMBERS→GET-each. Keeps the WR-01 422-vs-500 split.
var db  = _multiplexer.GetDatabase();
var now = _clock.GetUtcNow().UtcDateTime;
foreach (var proc in snapshot.Processors.Values)
{
    var members = await db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(proc.Id)); // RedisValue[] (verified)
    int absent = 0, unhealthy = 0, stale = 0, malformed = 0;
    bool qualified = false;

    foreach (var member in members)
    {
        var instanceId = member.ToString();
        var raw = await db.StringGetAsync(L2ProjectionKeys.PerInstance(proc.Id, instanceId));
        if (raw.IsNullOrEmpty)
        {
            absent++;
            // D-09: absent-only lazy SREM, fire-and-forget — never throws into the verdict.
            _ = db.SetRemoveAsync(L2ProjectionKeys.InstanceIndex(proc.Id), member, CommandFlags.FireAndForget);
            continue;
        }
        ProcessorLivenessEntry? entry;
        try { entry = JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!); }
        catch (JsonException) { malformed++; continue; }            // WR-01: never a 500
        if (entry?.Summary is null) { malformed++; continue; }      // null-shape → fail that replica
        if (entry.Status != LivenessStatus.Healthy) { unhealthy++; continue; }
        if (entry.Timestamp.AddSeconds(entry.Interval * 2) <= now) { stale++; continue; }
        qualified = true; break;                                    // ≥1 healthy+fresh ⇒ PASS (short-circuit)
    }

    if (!qualified)
    {
        var reason = $"no healthy replica ({members.Length} checked: " +
                     $"{absent} absent, {unhealthy} unhealthy, {stale} stale, {malformed} malformed)";
        throw OrchestrationValidationException.ProcessorNotLive(proc.Id, reason);  // → 422
    }
}
```

### Verified watchdog shape (PROBE-01/02)
```csharp
// NEW: src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs — mirrors BusReadyHealthCheck(_outer).
public sealed class LivenessWatchdogHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;
    public LivenessWatchdogHealthCheck(IServiceProvider outer) => _outer = outer;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default)
    {
        var state = _outer.GetRequiredService<IProcessorLivenessState>();   // singleton, resolved at check time
        var clock = _outer.GetRequiredService<TimeProvider>();
        var current = state.Current;                                        // volatile snapshot
        if (current is null)
            return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop not started")); // D-02

        var data = new Dictionary<string, object>                          // D-04: summary in body
        {
            ["inputSchema"]  = current.Summary.InputSchema,
            ["outputSchema"] = current.Summary.OutputSchema,
            ["configSchema"] = current.Summary.ConfigSchema,
        };
        var now = clock.GetUtcNow().UtcDateTime;                            // D-03: same clock discipline
        if (now > current.Timestamp.AddSeconds(current.Interval * 2))
            return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop stale", data: data));
        return Task.FromResult(HealthCheckResult.Healthy("live", data: data));
    }
}
```
`HealthCheckResult.Unhealthy(string? description, Exception? exception = null, IReadOnlyDictionary<string, object>? data = null)` and `Healthy(string? description, IReadOnlyDictionary<string, object>? data = null)` both accept the `data` dictionary the UI writer serializes. [VERIFIED: framework signature + EmbeddedHealthEndpointService usage of UIResponseWriter]

### Verified SREM signature
```csharp
// StackExchange.Redis IDatabaseAsync (2.13.1):
Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags = CommandFlags.None);
// FireAndForget → returns default(bool) immediately, never surfaces a fault. [VERIFIED upstream]
```

## Runtime State Inventory

> This phase is partly a teardown (D-11 deletes a contract + key builder) AND reads a live keyspace. Inventory below.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | The **legacy flat key `skp:{processorId}`** (written by the pre-Phase-60 flat-write path) is the key the deleted `Processor(Guid)` builder produced. Phase 60 D-05 removed the dual-write, so NO new code writes it; the gate stops reading it this phase. Any residual `skp:{procId}` values in a live Redis are now orphaned (TTL will expire them). The NEW read targets `skp:proc:{procId}` (index SET) + `skp:proc:{procId}:{instanceId}` (per-instance), populated by the Phase-60 writer. | Code edit only (swap reader). No data migration in THIS phase — Phase 62 proves the live keyspace against the real stack. Orphaned `skp:{procId}` values expire by TTL; no explicit purge required. |
| Live service config | None — this is library code, not a deployed-service config. K8s probe wiring is explicitly future/out-of-scope. | None. |
| OS-registered state | None — no Task Scheduler / pm2 / systemd registrations touched. | None. |
| Secrets/env vars | None — no secret/env-var names reference the deleted types. `InstanceId.Resolve()` env precedence is unchanged (Phase 59 SoT). | None. |
| Build artifacts | None — pure source change; no egg-info/compiled-binary analog in .NET that carries the old type name. The `ProcessorProjection` type deletion is a compile-time break caught by the build (its last callers are the validator + the round-trip test, both re-pointed). | None beyond `dotnet build` re-compile. |

**Canonical question answer:** After the source swap, the only runtime residue is orphaned legacy `skp:{procId}` string values in a live Redis (if any) — they are no longer read by anyone and expire by their own TTL. No active system caches or re-registers the deleted type/key.

## Common Pitfalls — Compile-break callers of the deleted symbols (D-11)

Verified callers of the three symbols being deleted (so the planner can sequence the teardown without a broken build):

| Deleted symbol | Verified callers (must be re-pointed/removed in the same phase) |
|----------------|-----------------------------------------------------------------|
| `L2ProjectionKeys.Processor(Guid)` | `RedisProjectionKeys.Processor` forwarder (deleted with it); the shipped `ProcessorLivenessValidator` (`StringGetAsync(RedisProjectionKeys.Processor(proc.Id))`) — swapped this phase. Tests: `ProcessorLivenessFacts` (`SeedLivenessAsync` + malformed seed), `RedisProjectionKeysTests` (`Processor_Produces_*`, `Root_And_Processor_Are_ByteIdentical_*`). |
| `RedisProjectionKeys.Processor(Guid)` | Only the validator + `RedisProjectionKeysTests`. No writer call site (the writer uses `PerInstance`/`InstanceIndex` since Phase 60). |
| `ProcessorProjection` record | Only the validator (`JsonSerializer.Deserialize<ProcessorProjection>`); tests `ProcessorLivenessFacts.SeedLivenessAsync` (constructs it) + `ProjectionRecordRoundTripTests` (`ProcessorProjection_Serializes_*`, `ProcessorProjection_RoundTrips_*`). |

**Note:** `ProcessorProjection` carries a `LivenessProjection` member (the SHARED record). Deleting `ProcessorProjection` does NOT delete `LivenessProjection` — `LivenessProjection` is still used by `WorkflowRootProjection` (the workflow-root path, verified in `ProjectionRecordRoundTripTests.WorkflowRoot_*`). Do NOT touch `LivenessProjection` (D-11).

## Validation Architecture

> nyquist_validation is enabled (config: `workflow.nyquist_validation: true`). This section maps the 5 success criteria to concrete hermetic test strategies.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit.v3 3.2.2 (Microsoft.Testing.Platform / MTP) + NSubstitute |
| Config file | `tests/BaseApi.Tests/xunit.runner.json` (parallelism cap, Phase-39) |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~ProcessorLiveness | FullyQualifiedName~LivenessWatchdog"` |
| Full suite command | `dotnet test` (from repo root) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated approach | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| GATE-01 | SMEMBERS→GET-each discovery, no instanceId knowledge | unit (NSubstitute) | Stub `SetMembersAsync(InstanceIndex(id))` → `["inst-a","inst-b"]`; stub `StringGetAsync(PerInstance(id,"inst-a"))` → serialized healthy entry. Assert validator GETs each member. | ❌ Wave 0 (new unit class) — or re-point integration `ProcessorLivenessFacts` |
| GATE-02 | ≥1 healthy+fresh admits; unhealthy/stale fail that replica | unit + integration | Unit: one stale + one healthy member → PASS (no throw). One healthy + one absent → PASS. All unhealthy → 422. Integration: re-point `AllProcessorsLive_Returns204` to seed per-instance keys via real Redis. | ⚠️ `ProcessorLivenessFacts` exists — re-point (uses REAL Redis, Pitfall 2) |
| GATE-03 | no-qualifier → 422 + RFC 7807; absent → lazy SREM | unit + integration | Unit: assert `OrchestrationValidationException` with `gate=="processorLiveness"` + aggregate reason; assert `SetRemoveAsync(InstanceIndex, member, FireAndForget)` was called for the absent member only. Integration: HTTP 422, `errors.gate`, `errors.offending.reason`, `offending.procId`; genuine RedisException → 500 (dead-Redis fact). | ⚠️ Re-point existing 422 facts; ADD lazy-SREM unit assertion |
| PROBE-01 | L1 null → Unhealthy; L1 stale → Unhealthy; fresh → Healthy | unit (NSubstitute) | Fabricate `IProcessorLivenessState` with `Current` = null / stale `ProcessorLivenessEntry` / fresh entry (use a `FakeTimeProvider` or `TimeProvider` stub to control `now`). Call `CheckHealthAsync` directly; assert `HealthCheckResult.Status`. | ❌ Wave 0 (new unit class) |
| PROBE-02 | `summary` present in `/health/live` body | integration (Generic-Host) | Extend a `ConsoleTestHostFixture`-style processor fixture that registers the watchdog descriptor + a seeded `IProcessorLivenessState`; GET `/health/live`; assert body contains `inputSchema`/`outputSchema`/`configSchema`. Mirror `ConsoleHealthLiveTests`. | ❌ Wave 0 (new fixture/test) |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~ProcessorLiveness | FullyQualifiedName~LivenessWatchdog"`
- **Per wave merge:** `dotnet test tests/BaseApi.Tests` (full BaseApi.Tests assembly — gate facts share the non-parallel `ParentIndex` collection)
- **Phase gate:** Full suite green before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/.../ProcessorLivenessGateUnitTests.cs` (suggested) — pure-unit validator test with NSubstitute `IConnectionMultiplexer`/`IDatabase` (covers GATE-01/02/03 deterministically, incl. lazy-SREM assertion). Reuse the `FakeRedis` double or a minimal `Substitute.For<IDatabase>()` with `SetMembersAsync` + per-key `StringGetAsync` + `SetRemoveAsync(...,FireAndForget)` stubbed (Pitfall 3).
- [ ] `tests/BaseApi.Tests/.../LivenessWatchdogHealthCheckTests.cs` — pure-unit probe test (null/stale/fresh L1 via fabricated `IProcessorLivenessState` + controllable `TimeProvider`).
- [ ] A processor-scoped Generic-Host fixture (mirror `ConsoleTestHostFixture` + `ConsoleHealthLiveTests`) that composes `AddBaseProcessor` and asserts `/health/live` flips Unhealthy on stale/null L1 AND carries `summary` (PROBE-02). The existing `ConsoleTestHostFixture` is `protected virtual ConfigureBuilder`-overridable — subclass it.
- [ ] Re-point `ProcessorLivenessFacts` (real-Redis integration) onto `PerInstance`/`InstanceIndex` + `ProcessorLivenessEntry.Create(...)`; delete the `ProcessorProjection`/`LivenessProjection("Live")` seeding.
- [ ] Update `RedisProjectionKeysTests` — delete the `Processor_*` and `Root_And_Processor_Are_ByteIdentical_*` facts (D-11); confirm `PerInstance`/`InstanceIndex` golden pins exist (added Phase 59 — verify, add if missing).
- [ ] Update `ProjectionRecordRoundTripTests` — delete the two `ProcessorProjection_*` facts (D-11); keep the `WorkflowRoot_*` facts (LivenessProjection untouched).
- *Framework install:* none — xUnit.v3 + NSubstitute already referenced.

## Security Domain

> `security_enforcement` is not set in config (absent ⇒ enabled by the agent contract). This phase is internal infra (orchestration gate + liveness probe) with no new external input surface, auth, or crypto. Applicable categories below.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth surface added; orchestration-start auth is unchanged (out of scope). |
| V3 Session Management | no | No sessions. |
| V4 Access Control | no | No new access-control decision. |
| V5 Input Validation | yes | The per-instance value is EXTERNAL self-registered data (WR-01) — malformed JSON / null-liveness MUST be tolerated (→ 422, never a 500/NRE). This IS the input-validation control. Index members (instanceIds) are treated as opaque strings. |
| V6 Cryptography | no | No crypto. |
| V7 Error Handling (info disclosure) | yes | The 422 `offending` payload carries ONLY procId Guids + a reason string with replica COUNTS (never instanceIds, connection strings, or stack traces — T-14-02 / OBSERV-REDIS-03). The probe body is status + summary outcomes only (T-18-08 — no secrets, mirrored by `ConsoleHealthLiveTests.Live_Body_Has_No_Secrets`). |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malformed self-registered L2 value crashes the gate (DoS via 500) | Denial of Service | WR-01: catch `JsonException`/null-shape → 422 fail-that-replica (never a 500). Verified test exists (`MalformedProcessorRegistration_Returns422`) — re-point it. |
| Info disclosure in 422 body or probe body | Information Disclosure | Counts + Guids only in the 422 reason; status + summary outcomes only in the probe body. Assert no instanceId/connection-string/stack-trace leaks (extend the existing `Live_Body_Has_No_Secrets` pattern). |
| Aggregate reason string leaking instanceIds | Information Disclosure | The aggregate reason (D-08) must report COUNTS per state, not the offending instanceIds. Keep the discretion-chosen format count-only. |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Single flat key `skp:{procId}` + `ProcessorProjection` (`present ⟺ live`) | Per-instance keys `skp:proc:{procId}:{instanceId}` + index SET `skp:proc:{procId}` + `ProcessorLivenessEntry` (≥1-of-N healthy) | Phases 59 (contract) → 60 (writer) → 61 (reader, this phase) | Presence no longer implies live; multi-replica liveness; legacy contract deleted. |
| `HealthChecks.UI.Client` `UIResponseWriter` | unchanged — still the body writer | — | The `data` dictionary serialization the probe relies on is stable. |

**Deprecated/outdated:** `L2ProjectionKeys.Processor` / `RedisProjectionKeys.Processor` / `ProcessorProjection` — deleted this phase (D-11); their last callers are the reader + two test classes.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `PerInstance`/`InstanceIndex` golden-string pins already exist in a Phase-59 test (the CONTEXT implies it) — I verified `RedisProjectionKeysTests` still pins only the OLD `Processor` key, not the new builders. | Wave 0 Gaps | LOW — if missing, the planner just adds the pins (trivial); flagged so the planner doesn't assume they exist. |
| A2 | `HealthCheckResult.Healthy/Unhealthy` accept an `IReadOnlyDictionary<string,object>? data` parameter in the .NET 8 framework version in use. | Code Examples / D-04 | LOW — this is the documented framework signature and the repo already uses `UIResponseWriter` which reads per-check `data`; verify at implementation by compiling. |

**Note:** Every other claim is `[VERIFIED: in-repo source]` or `[VERIFIED: StackExchange.Redis upstream]`. A1 is actually a *correction* of an additional_context implication (the new pins are NOT yet present) — the planner should add them.

## Open Questions

1. **Where does the new pure-unit gate test live, and does it reuse `FakeRedis`?**
   - What we know: `FakeRedis` (NSubstitute, Keeper namespace) is a reusable stateful double; the gate facts use real Redis.
   - What's unclear: whether to extend `FakeRedis` with `SetMembersAsync`/`SetRemoveAsync` support or write a fresh minimal `Substitute.For<IDatabase>()` in a new BaseApi-side unit class.
   - Recommendation: a fresh minimal `Substitute.For<IConnectionMultiplexer>()`/`IDatabase` in a new BaseApi.Tests unit class (gate logic is BaseApi-side; `FakeRedis` lives in the Keeper namespace and models a different failure model). Stub exactly SMEMBERS + per-key GET + SREM(FireAndForget). The real-Redis facts cover the integration path.

2. **Does the aggregate reason need a structured breakdown, or is a count string sufficient?**
   - What we know: CONTEXT D-08 leaves format to discretion; keep `ProcessorLivenessOffending(procId, reason)` unless structured is trivially additive.
   - Recommendation: count string (`"no healthy replica (N checked: A absent, U unhealthy, S stale, M malformed)"`). Trivial, info-disclosure-safe, satisfies "aggregate replica-count reason."

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build + test | ✓ (assumed — repo is .NET 8) | 8.x | — |
| Real Redis (test fixture) | `ProcessorLivenessFacts` integration facts | ✓ via `Phase8WebAppFactory._redisFixture` | (fixture-managed) | Pure-unit NSubstitute path (no Redis) covers GATE logic deterministically |
| StackExchange.Redis 2.13.1 | gate Redis ops | ✓ (CPM-pinned) | 2.13.1 | — |
| NSubstitute / xUnit.v3 | tests | ✓ (CPM-pinned) | xUnit.v3 3.2.2 | — |

**Missing dependencies with no fallback:** None.
**Missing dependencies with fallback:** Real-Redis integration facts depend on the test Redis fixture; the unit-level NSubstitute path is the fallback for deterministic GATE coverage and runs with no Redis.

## Sources

### Primary (HIGH confidence — in-repo source, this session)
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — shipped single-key gate + WR-01 422-vs-500 discipline (the swap target).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:193-201` — the `RedisException`-catch wrapper that owns the 500 split (NOT the validator).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationException.cs:76-97` + `OrchestrationValidationExceptionHandler.cs` — `ProcessorNotLive` factory + 422 RFC 7807 mapping.
- `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs:73-96` + `BusReadyHealthCheck.cs` — the outer-provider-bridge precedent + the `live`-tag predicate + `UIResponseWriter`.
- `src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` — outer health registration seam.
- `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` + `ProcessorLivenessState.cs` + `ProcessorLivenessWriter.cs:61-87` — the L1 holder (null-until-first-write) + writer SET/SADD using `PerInstance`/`InstanceIndex` + default-options serialize.
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:142` — `IProcessorLivenessState` Singleton registration; `TimeProvider.System` at line 124.
- `src/Messaging.Contracts/Projections/{ProcessorLivenessEntry,L2ProjectionKeys,LivenessStatus,SchemaOutcome,ProcessorProjection}.cs` — the contract being read + the symbols being deleted.
- `tests/BaseApi.Tests/.../ProcessorLivenessFacts.cs`, `Projection/{RedisProjectionKeysTests,ProjectionRecordRoundTripTests}.cs` — the facts to re-point/delete; confirms real-Redis seeding via `_factory.RedisMultiplexer`.
- `tests/BaseApi.Tests/Composition/HarnessWebAppFactory.cs` + `Phase8WebAppFactory.cs:169` — real-Redis fixture vehicle (Pitfall 2).
- `tests/BaseApi.Tests/Console/{ConsoleTestHostFixture,ConsoleHealthLiveTests}.cs` — the Generic-Host `/health/live` integration vehicle the probe test mirrors.
- `tests/BaseApi.Tests/Keeper/FakeRedis.cs` — NSubstitute `IConnectionMultiplexer`/`IDatabase` double precedent (stub shapes for SMEMBERS/GET/SREM).
- `src/Orchestrator/Hydration/HydrationBackgroundService.cs:42` — in-repo `db.SetMembersAsync(...)` precedent.
- `.planning/REQUIREMENTS.md:46-71` — GATE-01/02/03, PROBE-01/02 + binding Out-of-Scope.

### Secondary (HIGH confidence — upstream)
- StackExchange.Redis `IDatabaseAsync.cs` (main) — `SetMembersAsync` → `Task<RedisValue[]>`; `SetRemoveAsync(RedisKey, RedisValue, CommandFlags)` → `Task<bool>`; `StringGetAsync` → `Task<RedisValue>`. [VERIFIED via WebFetch]
- StackExchange.Redis `CommandFlags.cs` (main) — `FireAndForget` (value 2): "caller not interested in the result; immediately receives a default-value." [VERIFIED via WebFetch]

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all in-repo, CPM-pinned, no new packages.
- Architecture (gate + probe + hook): HIGH — every pattern is a generalization of shipped in-repo code; signatures verified upstream.
- Pitfalls: HIGH — derived directly from reading the actual test fixtures (real-Redis correction is a concrete finding, not a guess).
- DI lifetimes: HIGH — verified Singleton L1 + Scoped validator + Singleton multiplexer registrations.

**Research date:** 2026-06-13
**Valid until:** 2026-07-13 (stable — internal codebase + pinned deps; re-verify only if `EmbeddedHealthEndpointService` or the validator's caller wrapper changes).
