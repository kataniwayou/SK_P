# Phase 36: L2 Health-Probe Recovery Loop & DLQs - Pattern Map

**Mapped:** 2026-06-05
**Files analyzed:** 14 (8 new / 6 modified)
**Analogs found:** 13 / 14 (DLQ-1 custom error transport = NO clean analog — novel, see below)

## File Classification

| New/Modified File | New/Mod | Role | Data Flow | Closest Analog | Match |
|-------------------|---------|------|-----------|----------------|-------|
| `src/Keeper/Recovery/L2ProbeRecovery.cs` (shared helper, discretion) | NEW | service | request-response + I/O | `src/Orchestrator/Consumers/ResultConsumer.cs` | role+flow (exact for ops) |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | MOD | consumer | event-driven | itself (Phase 35 skeleton) + `ResultConsumer.cs` | exact |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | MOD | consumer | event-driven | itself (Phase 35 skeleton) + `ResultConsumer.cs` | exact |
| `src/Keeper/Program.cs` | MOD | config/composition | — | itself + Orchestrator `Program.cs` | exact |
| `src/Keeper/appsettings.json` | MOD | config | — | itself | exact |
| `src/Keeper/ProbeOptions.cs` (Keeper-local, recommended) | NEW | config/options | — | `src/Messaging.Contracts/Configuration/RetryOptions.cs` | role-match |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | MOD | utility (key builder) | — | itself (`Flag`/`ExecutionData`) | exact |
| `src/Messaging.Contracts/KeeperQueues.cs` | MOD | contract (const) | — | itself (`FaultRecovery`) | exact |
| `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` | MOD | config/DI + error transport | request-response | `configureBus` seam (itself) | partial — transport filter NOVEL |
| `src/BaseConsole.Core/.../ConsolidatedErrorTransportFilter.cs` (Mechanism A) | NEW | middleware (IFilter) | transform | **NONE** (see "No Analog Found") | NONE |
| `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` | NEW | test (hermetic) | — | `KeeperFaultConsumerScopeTests.cs` | exact (harness rig) |
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | NEW | test (hermetic) | — | `KeeperFaultConsumerScopeTests.cs` | role-match |
| `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` | NEW | test (RealStack) | — | `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` | exact (rig) |
| `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` | MOD (likely NO-OP) | test | — | itself | exact |

---

## CRITICAL PRE-PLAN CORRECTIONS (verified in source — override the CONTEXT/RESEARCH wording)

1. **D-01 is already 90% done — do NOT add a second `AddBaseConsoleRedis(cfg)` line.**
   `src/Keeper/Program.cs:18` already calls `builder.Services.AddBaseConsole(builder.Configuration)`, and `AddBaseConsole` **chains `AddBaseConsoleRedis`** (`src/BaseConsole.Core/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs:30-32`). So `IConnectionMultiplexer` is ALREADY a registered singleton in Keeper. Adding an explicit `AddBaseConsoleRedis(cfg)` would double-register the singleton.
   → **Planner action:** the probe consumers/helper just ctor-inject `IConnectionMultiplexer` (as `ResultConsumer` does). No Program.cs change is needed for Redis itself; only the `Configure<ProbeOptions>` line is new.

2. **`ConnectionStrings:Redis` is already present** in `src/Keeper/appsettings.json:13-15` (`"redis:6379,abortConnect=false,connectTimeout=5000"`). The CONTEXT D-01 phrase "add `ConnectionStrings:Redis`" is already satisfied. → **Only** the `"Probe"` section is a genuinely new appsettings addition.

3. **`RetryOptions` is already bound in Keeper** (`src/Keeper/Program.cs:22`) and the `Immediate(N)` retry is already wired on the shared endpoint (`FaultEntryStepDispatchConsumerDefinition.cs:43`). DLQ-01 ("Keeper's own infra faults retry under Immediate(N)") needs NO Keeper-side change — it only changes the *destination* of the exhausted `_error` move, which is the `BaseConsole.Core` DLQ-1 work.

4. **`ExecutionData(string)` is the path to use** (`L2ProjectionKeys.cs:41`) — the `Guid` overload (`:48`) is transitional/legacy. `inner.EntryId` is already a `string` (`IExecutionCorrelated.EntryId`, `IExecutionCorrelated.cs:16`), so `ExecutionData(inner.EntryId)` binds the string overload directly.

5. **`RetryOptions.Limit` defaults to `3`** (`RetryOptions.cs:10`) — `Immediate(3)`. DLQ-1 wiring in `BaseConsole.Core` must keep this exact budget (bind from the same `"Retry"` section; do NOT hardcode).

---

## Pattern Assignments

### `src/Keeper/Recovery/L2ProbeRecovery.cs` (NEW — shared probe loop helper) + the two consumers

**Primary analog:** `src/Orchestrator/Consumers/ResultConsumer.cs` (the `IConnectionMultiplexer` ctor-inject + `GetDatabase()` + `StringGetAsync`/`StringSetAsync` shape; INFRA-fault-propagates convention).
**Secondary analog:** the Phase-35 consumer bodies (`FaultEntryStepDispatchConsumer.cs` / `FaultExecutionResultConsumer.cs`) — the recovery slot drops in between the unwrap (line 33-34) and the `return` (line 44-45).
**Re-inject analog:** `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:213-214, 241-243` (verbatim `GetSendEndpoint` + `Send`-inner-by-type).

**COPY — ctor-inject `IConnectionMultiplexer` + `GetDatabase` + read/write shape** (from `ResultConsumer.cs:40-46, 60`):
```csharp
public sealed class ResultConsumer(
    ...
    IConnectionMultiplexer redis,        // ← ctor-inject the SAME singleton (AddBaseConsole already registers it)
    ...) : IConsumer<ExecutionResult>
{
    public async Task Consume(...)
    {
        var db = redis.GetDatabase();    // INFRA fault on GetDatabase/StringGetAsync/StringSetAsync = NO catch → Immediate(N) → _error
        ...
        await db.StringGetAsync(L2ProjectionKeys.ExecutionData(m.EntryId));   // ← exact read shape to mirror (ResultConsumer.cs:92)
```

**WHAT TO CHANGE for the probe loop** (RESEARCH Pattern 1, shape; verify StackExchange.Redis 2.13.1 sigs):
- Wrap the read + write-then-delete in a `for (attempt = 0; attempt < opts.MaxAttempts; attempt++)` with a `try { ... return Recovered; } catch (RedisException) { if (attempt+1 < Max) await Task.Delay(DelaySeconds); }`.
- READ (value need NOT exist): `await db.StringGetAsync(L2ProjectionKeys.ExecutionData(inner.EntryId));` (discard result).
- WRITE-then-delete (D-03): `var scratch = L2ProjectionKeys.KeeperProbe(inner.H); await db.StringSetAsync(scratch, "1", expiry: TimeSpan.FromSeconds(30)); await db.KeyDeleteAsync(scratch);`
- Both ops, no exception → `ProbeOutcome.Recovered`; loop falls through → `ProbeOutcome.GaveUp`.
- **Catch `RedisException` (the superset of `RedisConnectionException` + `RedisTimeoutException`) — NOT `Exception`** (RESEARCH Anti-Patterns + Assumption A2; verify the hierarchy at plan time).
- **Do NOT** copy `ResultConsumer`'s `StringSetAsync(..., when: When.Exists, keepTtl: true)` flag-flip semantics — the probe write is a plain TTL'd scratch write, not the effect-first flag flip.

**COPY — re-inject verbatim by type on `Recovered`** (from `FaultRecoverySpikeE2ETests.cs:213-214, 242-243` + RESEARCH Pattern 2):
```csharp
var uri = inner switch
{
    EntryStepDispatch d => new Uri($"queue:{d.ProcessorId:D}"),            // spike :214 — origin processor endpoint
    ExecutionResult    => new Uri($"queue:{OrchestratorQueues.Result}"),  // spike :242 — "queue:orchestrator-result"
    _ => throw new InvalidOperationException("unknown inner type")
};
var endpoint = await context.GetSendEndpoint(uri);   // use the ConsumeContext overload (outbound correlation filter applies)
await endpoint.Send(inner, context.CancellationToken);   // Send (NOT Publish), verbatim inner, same H
```
> `inner` type pattern: `EntryStepDispatch` has `ProcessorId` (record positional param, `EntryStepDispatch.cs:12`); `ExecutionResult` re-injects to the fixed `OrchestratorQueues.Result` const (`"orchestrator-result"`, `OrchestratorQueues.cs:16`). PROBE-06: NO Keeper-side dedup — the receiver's surviving `flag[H]` gate collapses dups (proven in spike :217-231).

**COPY — park original `Fault<T>` envelope on `GaveUp`** (RESEARCH Pattern 3, D-09/D-10):
```csharp
var dlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));   // "queue:keeper-dlq"
await dlq.Send(context.Message, context.CancellationToken);   // context.Message == Fault<EntryStepDispatch>/Fault<ExecutionResult> (carries Exceptions[])
return;   // → ack. A fault in THIS Send is infra → Immediate(N) → DLQ-1 (consistent w/ every consumer)
```
> Park `context.Message` (the WHOLE Fault envelope) NOT the bare `inner` — D-10: the envelope carries `Exceptions[]` for triage. `context.Message.Exceptions` is read at `FaultEntryStepDispatchConsumer.cs:34`.

**WHAT TO CHANGE in the consumers themselves** (`FaultEntryStepDispatchConsumer.cs` / `FaultExecutionResultConsumer.cs`):
- Change the ctor to inject the helper (+ `IConnectionMultiplexer` if inlined) alongside the existing `ILogger<...>`.
- Change `Task Consume` → `async Task Consume` (the loop is awaited inside `Consume` — PROBE-05 ack-after-loop is automatic).
- KEEP the existing double-unwrap (`context.Message.Message`, line 33) + the manual `CorrelationKeys.LogScope` + `ExecutionLogScope.BuildState` scope (lines 36-37) + the Information log — these are load-bearing (T-35-06).
- REPLACE the `return Task.CompletedTask;` (line 44) with: invoke the helper → on `Recovered` re-inject, on `GaveUp` park → `return`.
- Keep structured logging only (exception text as params under fixed holes, no stack frames at Information — `FaultEntryStepDispatchConsumer.cs:39-41`).

---

### `src/Keeper/ProbeOptions.cs` (NEW — recommended Keeper-local)

**Analog:** `src/Messaging.Contracts/Configuration/RetryOptions.cs:8-12` (the `sealed class` + `int` props + `IOptions` bind pattern).
**Recommendation (RESEARCH Open Q #3):** Keeper-local (it is a Keeper-only knob, unlike the shared `RetryOptions`).

**COPY shape** (mirror `RetryOptions.cs`, RESEARCH Code Examples):
```csharp
/// <summary>
/// PROBE-01 constraint (LOAD-BEARING, D-04): DelaySeconds × MaxAttempts MUST stay well under RabbitMQ's
/// default 30-min consumer_timeout — the loop is awaited INSIDE Consume, holding the delivery un-acked.
/// Defaults 5s × 12 = 60s (30× margin).
/// </summary>
public sealed class ProbeOptions
{
    public int DelaySeconds { get; set; } = 5;
    public int MaxAttempts  { get; set; } = 12;
}
```

**Program.cs bind** (mirror `Program.cs:22` `Configure<RetryOptions>`):
```csharp
builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));
```
**appsettings.json** (add to `src/Keeper/appsettings.json`, next to the existing `"Retry"` block at line 25):
```jsonc
"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }
```

---

### `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (MOD — add `KeeperProbe`)

**Analog (same file):** `Flag(string h)` at line 51 (`$"{Prefix}flag:{h}"`) and `ExecutionData(string)` at line 41 — exact builder shape.

**ADD** (after line 51, RESEARCH D-03):
```csharp
/// <summary>D-03: probe scratch key — short-TTL write-then-delete; the TTL is the crash net-zero net.</summary>
public static string KeeperProbe(string h) => $"{Prefix}keeper:probe:{h}";   // "skp:keeper:probe:{h}"
```
> Uses the existing `Prefix = "skp:"` const (line 30). Result: `skp:keeper:probe:{h}`.

---

### `src/Messaging.Contracts/KeeperQueues.cs` (MOD — add `DeadLetter`)

**Analog (same file):** `FaultRecovery` const at line 15 — exact shape.

**ADD** (after line 15, D-08):
```csharp
/// <summary>
/// DLQ-2 (D-08): terminal probe give-up queue. Plain durable, NO x-message-ttl — its depth is the
/// PRIMARY operator alert (Phase 39), so it MUST persist until an operator drains it (contrast DLQ-1's TTL).
/// </summary>
public const string DeadLetter = "keeper-dlq";
```

---

### `src/Keeper/Program.cs` (MOD) + `appsettings.json` (MOD)

**Analog:** itself + Orchestrator `Program.cs` (the `AddBaseConsole` + `Configure<...>` ordering).
**Changes (minimal — see CRITICAL CORRECTIONS 1-3):**
- ADD `builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));` (after the `RetryOptions` bind at line 22).
- ADD `"Probe": { ... }` to appsettings.json.
- **Do NOT** add `AddBaseConsoleRedis` (already chained via `AddBaseConsole`, line 18).
- Consumer registration block (lines 30-34) is unchanged unless a `ConcurrentMessageLimit` is added (D-13 — keep defaults; optional).

---

### Test files

**Hermetic loop + DLQ tests → analog `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs`** (the standing hermetic Keeper rig).

**COPY the in-memory harness builder** (`KeeperFaultConsumerScopeTests.cs:62-70`):
```csharp
private static ServiceProvider BuildHarness(..., Action<IBusRegistrationConfigurator> addConsumers) =>
    new ServiceCollection()
        .AddLogging(...)
        .AddMassTransitTestHarness(x =>
        {
            addConsumers(x);
            x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
        })
        .BuildServiceProvider(true);
```
**COPY the Fault<T> publish-via-initializer pattern** (`:112` / `:161`) — a hand-rolled envelope does NOT satisfy framework `Fault<T>`:
```csharp
await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = inner }, ct);
Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));
```
**WHAT TO ADD for PROBE-01..05 / DLQ-01:**
- Register a **fake `IConnectionMultiplexer`/`IDatabase`** that throws `RedisConnectionException`/`RedisTimeoutException` on demand (down-then-up) — StackExchange.Redis interfaces are mockable (RESEARCH Wave-0 gap). Inject it into the harness's service collection so the consumer/helper resolves the fake.
- `Probe_RequiresReadAndWrite` (PROBE-02): assert a half-open Redis (read OK, write throws) counts as fault.
- `Probe_Success_Reinjects` (PROBE-03): fail-then-succeed → assert `harness.Sent` has the inner on `queue:{proc:D}` / `queue:orchestrator-result`.
- `Probe_GiveUp_ParksToDlq` (PROBE-04): fail-to-max → assert a `Fault<T>` was Sent to `queue:keeper-dlq`.
- `Probe_AcksOnlyAfterLoop` (PROBE-05): assert the message is consumed only after the loop exits (no premature ack).
- `ProbeOptions_Bound` (PROBE-01): assert `DelaySeconds × MaxAttempts` < 30 min at bind time.

**RealStack E2E → analog `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs`** (the standing RealStack rig).

**COPY verbatim** (these are the proven primitives):
- `[Trait("Category","E2E")]` + `[Trait("Category","RealStack")]` + `[Collection("Observability")]` (`:54-56`).
- The embedded-SourceHash reflection read (`:101-103`) — **rebuild the keeper container** before the run (SourceHash must match).
- `RealStackWebAppFactory` host-stack env overrides (`:743-771`): RMQ `localhost:5673`, Redis `localhost:6380`, `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`.
- `ArmWrongTypePoisonAsync` (`:381-387`) — LIST poison to trip a live `Fault<T>` (poison a **GET** key, per project memory `RealStack fault-trip mechanics`).
- `PollEsForLog` / `CountEsHitsAsync` (`:541-576`) — downstream-effect proof; ES body is `body.text`.
- Net-zero teardown: `L2KeysToCleanup` / `ScanKeys("data:*"|"flag:*")` BEFORE/AFTER + `/api/v1/orchestration/stop` (`:283-292, 802-819`).
**WHAT TO ADD:** scan `keeper:probe:*` in the net-zero snapshot (the new scratch-key family); assert recover-both-paths (dispatch + result) AND give-up → `keeper-dlq` park.

**`KeeperDependencyFirewallTests.cs` (MOD — likely NO-OP):** the allow-list (`:32-36`) bans `BaseApi.Core`, `Microsoft.EntityFrameworkCore`, `Npgsql`, `Quartz`, `Cronos`. Redis rides `BaseConsole.Core` (allowed). **No change expected** unless a new ProjectReference is added (none should be — D-01).

---

## Shared Patterns

### INFRA-fault-propagates-to-retry (no catch on the recovery Sends/Redis)
**Source:** `src/Orchestrator/Consumers/ResultConsumer.cs:57-60, 116`
**Apply to:** both Keeper fault consumers + the probe helper.
A Redis fault inside the *re-inject* `Send` or the *keeper-dlq* `Send` (NOT inside the probe loop's own try/catch) has NO catch → propagates to the endpoint's `Immediate(N)` → `_error` → DLQ-1. The probe loop's `catch (RedisException)` is the ONLY swallow, and only to keep looping.

### Single endpoint-retry owner on the shared Keeper queue (Pitfall 3)
**Source:** `FaultEntryStepDispatchConsumerDefinition.cs:31-44` (owns `UseMessageRetry`) + `FaultExecutionResultConsumerDefinition.cs:26-34` (intentional no-op).
**Apply to:** any DLQ-1 / retry wiring. Keep the sibling definition's `ConfigureConsumer` empty. DLQ-1 wiring lives in `BaseConsole.Core`'s `AddConfigureEndpointsCallback` (once per endpoint, framework-deduped) — NOT in per-consumer definitions.

### Shared `Immediate(N)` budget from `"Retry"` section
**Source:** `RetryOptions.cs:8-12` + `Program.cs:22` + `FaultEntryStepDispatchConsumerDefinition.cs:43`
**Apply to:** the DLQ-1 `AddConfigureEndpointsCallback` `UseMessageRetry(r => r.Immediate(RetryOptions.Limit))` — bind from the same single-source-of-truth section across all 3 consoles; do NOT hardcode `3`.

### Re-inject / DLQ verbatim-by-type
**Source:** `FaultRecoverySpikeE2ETests.cs:213-214, 242-243`
**Apply to:** both consumers — `context.GetSendEndpoint(uri)` + `Send(inner|context.Message)`, never `Publish`.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/BaseConsole.Core/.../ConsolidatedErrorTransportFilter.cs` (Mechanism A custom `IFilter<ExceptionReceiveContext>`) | middleware | transform | **Genuinely novel infra.** No existing custom MassTransit error-transport filter anywhere in `src/`. The default per-`{queue}_error` move is the framework default today (CONTEXT D-05 "NEW infra this phase"). RESEARCH confirms (Assumption A1, HIGH risk): MassTransit 8.5.5 provides NO built-in single-shared-error-queue formatter; the filter must faithfully reproduce the default `ErrorTransportFilter`'s move (headers, redelivery, content-type) with a fixed `skp-dlq-1` destination. **Use RESEARCH.md Mechanism A** (recommended) — `AddConfigureEndpointsCallback` → `e.ConfigureError(e => { e.UseFilter(new GenerateFaultFilter()); e.UseFilter(new ConsolidatedErrorTransportFilter(...)); })`; **`GenerateFaultFilter` MUST stay** (Keeper's whole model rides the `Fault<T>` pub/sub stream). Declare `skp-dlq-1` with `x-message-ttl=7d` (ms as int/long) once. **Mechanism B** (per-`_error` TTL + DLX chain) is the documented fallback if A proves too costly (flag the DLQ-03 immediacy gap to the user). |

**The `BaseConsole.Core/MessagingServiceCollectionExtensions.cs` MOD itself has a partial analog:** the existing `configureBus` bus-factory seam (`:55`) + `c.ConfigureEndpoints(ctx)` (`:59`) is where the `AddConfigureEndpointsCallback` (inside `AddMassTransit(x => ...)`, before `UsingRabbitMq`) and the `skp-dlq-1` declaration land. The *seam* exists; the *filter it installs* is novel.

**Planner open-verification (do during planning, not blocking):** read `MassTransit.RabbitMqTransport` 8.5.5 source for `MoveToErrorTransport*` to confirm the exact `IFilter<ExceptionReceiveContext>` API + how the default resolves its destination (RESEARCH Open Q #1, Assumptions A1/A5). Spike the filter in a hermetic harness before the base-library commit. Plan a one-time dev-broker reset (delete orphan `{queue}_error` + any pre-TTL `skp-dlq-1`) before the Phase-39 gate (Pitfalls 1/2).

---

## Metadata

**Analog search scope:** `src/Keeper`, `src/Orchestrator/Consumers`, `src/BaseConsole.Core/DependencyInjection`, `src/Messaging.Contracts` (+ `/Projections`, `/Configuration`), `tests/BaseApi.Tests/Keeper`, `tests/BaseApi.Tests/Orchestrator`.
**Files read for excerpts:** 16.
**Pattern extraction date:** 2026-06-05
