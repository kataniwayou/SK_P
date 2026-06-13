# Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record - Pattern Map

**Mapped:** 2026-06-13
**Files analyzed:** 14 (4 new src, 4 modified src, 3 new test, 5 modified test) — counting the 3 orchestrator-ctor facts as one modify-group
**Analogs found:** 14 / 14 (every symbol mirrors an existing in-repo idiom; the only genuinely new construct — the lock-free L1 holder — mirrors `ProcessorContext.IsHealthy`)

> Wiring phase. Phase 59 already shipped every value/key/identity primitive (`ProcessorLivenessEntry.Create`, `L2ProjectionKeys.PerInstance/InstanceIndex`, `InstanceId.Resolve`, `LivenessStatus.Unhealthy`, `SchemaOutcome`). 59-PATTERNS.md mapped those NEW symbols; this map does NOT re-derive them — it maps the WRITE-side wiring that consumes them. The dominant analog for almost every write discipline is the existing `ProcessorLivenessHeartbeat` (read in full this session).

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` (NEW) | L1 holder interface | n/a (in-mem contract) | `IProcessorContext.cs` (interface shape) | exact |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessState.cs` (NEW) | L1 holder impl (lock-free) | event-driven (ref swap) | `ProcessorContext.cs` (`volatile`/`Interlocked` publication of `IsHealthy`) | exact (idiom) |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs` (NEW, recommended) | shared internal writer (service) | request-response (L2 SET + SADD + L1 update) | `ProcessorLivenessHeartbeat` write block + `RedisProjectionWriter.SetAddAsync` | composed (two analogs) |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` (MODIFY) | BackgroundService (writer) | streaming (per-beat) | self | exact (in-place swap) |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (MODIFY) | BackgroundService (writer) | event-driven (per-iteration) | self + heartbeat write block | exact |
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` (MODIFY) | config/options | transform (config→int) | self (5 existing `[ConfigurationKeyName]` knobs) | exact |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (MODIFY) | composition root | n/a (DI) | self (line 136 `AddSingleton<IProcessorContext>`; line 178/182 hosted) | exact |
| `tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs` (NEW) | test (unit, hermetic) | n/a | `ProcessorOptionsBindingFacts` (pure-hermetic) | role-match |
| `tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs` (NEW) | test (integration, RedisFixture) | n/a | `LivenessHeartbeatFacts` (Redis + TTL banding) | exact |
| `tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs` (NEW) | test (integration, harness+Redis) | n/a | `IdentityResolutionFacts`/`SchemaResolutionFacts` (harness) + `LivenessHeartbeatFacts` (Redis) | composed |
| `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` (MODIFY) | test (integration, RedisFixture) | n/a | self | exact |
| `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` (MODIFY) | test (unit, hermetic) | n/a | self | exact |
| `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` (MODIFY) | test (DI descriptor) | n/a | self (the `IProcessorContext`/`IHostedService` descriptor asserts) | exact |
| `tests/BaseApi.Tests/Processor/{IdentityResolution,SchemaResolution,DispatchBindSequence}Facts.cs` (MODIFY ctor sites) | test (integration, harness) | n/a | self (the `Stub*()` seam + positional ctor) | exact |

## Pattern Assignments

### `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` (L1 holder interface) — NEW

**Analog:** `src/BaseProcessor.Core/Identity/IProcessorContext.cs` — the existing mutable-singleton interface shape with a memory-visibility XML-doc invariant.

**Namespace + interface shape to copy** (mirror `IProcessorContext`'s `namespace BaseProcessor.Core.Identity;` and the XML-doc-states-the-publication-invariant style — `IProcessorContext.cs:5-33`). The new file lives in `BaseProcessor.Core.Liveness`:
```csharp
using Messaging.Contracts.Projections;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// D-08/L1-01: the dedicated singleton L1 liveness record, updated by BOTH the startup orchestrator
/// (unhealthy writer) and the heartbeat (healthy writer), read by the Phase-61 self-watchdog probe.
/// Stores the SAME immutable <see cref="ProcessorLivenessEntry"/> written to L2 that iteration (D-09)
/// so L1 and L2 cannot desync. Publication is a volatile reference swap (D-10) — readable DURING
/// startup (unhealthy), a different access discipline than IProcessorContext's read-after-Healthy (WR-03),
/// which is precisely why it is NOT bolted onto IProcessorContext.
/// </summary>
public interface IProcessorLivenessState
{
    /// <summary>Swap the current immutable record (called by both loops every iteration).</summary>
    void Update(ProcessorLivenessEntry entry);

    /// <summary>Snapshot read for the Phase-61 probe; null until the first write.</summary>
    ProcessorLivenessEntry? Current { get; }
}
```
> Member names (`Update`/`Current`) are Claude's discretion (CONTEXT) — these match RESEARCH Pattern 2. The shape + publication semantics (D-08/09/10) are LOCKED.

---

### `src/BaseProcessor.Core/Liveness/ProcessorLivenessState.cs` (L1 holder impl) — NEW

**Analog:** `src/BaseProcessor.Core/Identity/ProcessorContext.cs` — specifically the `IsHealthy` `volatile`/`Interlocked` lock-free publication idiom (`ProcessorContext.cs:27,60`).

**The `IsHealthy` publication idiom this mirrors** (`ProcessorContext.cs:27,60,94`):
```csharp
private int _isHealthy; // 0/1 — Interlocked.Exchange has no bool overload in .NET 8
public bool IsHealthy => Volatile.Read(ref _isHealthy) == 1;
// MarkHealthy(): Interlocked.Exchange(ref _isHealthy, 1)  — full barrier publishes prior writes
```

**`public sealed` rationale to copy verbatim** (`ProcessorContext.cs:12-15`): `public sealed` so `services.AddSingleton<IProcessorLivenessState, ProcessorLivenessState>()` resolves across the assembly boundary without `InternalsVisibleTo`.

**Pattern to build** (D-10 LOCKS plain `volatile` reference assignment — NOT a `lock{}`, NOT `Interlocked.Exchange<T>`; reference-type assignment is atomic in the CLR, RESEARCH Anti-Pattern + Alternatives table):
```csharp
using Messaging.Contracts.Projections;

namespace BaseProcessor.Core.Liveness;

public sealed class ProcessorLivenessState : IProcessorLivenessState
{
    // volatile reference: atomic assignment + safe publication across the startup-thread / heartbeat-thread
    // writers and the Phase-61 probe-thread reader (D-10). Mirrors ProcessorContext.IsHealthy's discipline.
    private volatile ProcessorLivenessEntry? _current;

    public void Update(ProcessorLivenessEntry entry) => _current = entry;
    public ProcessorLivenessEntry? Current => _current;
}
```

---

### `src/BaseProcessor.Core/Liveness/ProcessorLivenessWriter.cs` (shared internal writer) — NEW (recommended, discretion)

**Analogs:** `ProcessorLivenessHeartbeat.cs:70-110` (the entire write+catch block) and `RedisProjectionWriter.cs:84` (the `SetAddAsync` precedent).

**Ctor + null-guard pattern** (copy `ProcessorLivenessHeartbeat.cs:41-59` / `RedisProjectionWriter.cs:44-54` — `IConnectionMultiplexer` + `IOptions<ProcessorLivenessOptions>().Value` + `ILogger`, each `?? throw new ArgumentNullException(nameof(...))`):
```csharp
public ProcessorLivenessWriter(
    IConnectionMultiplexer redis,
    IProcessorLivenessState l1,
    IOptions<ProcessorLivenessOptions> options,
    ILogger<ProcessorLivenessWriter> logger)
{
    _redis   = redis   ?? throw new ArgumentNullException(nameof(redis));
    _l1      = l1      ?? throw new ArgumentNullException(nameof(l1));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
}
```

**Core write pattern** (lift the heartbeat's write+catch — `ProcessorLivenessHeartbeat.cs:84-110` — and add the SADD from `RedisProjectionWriter.cs:84` + the L1 `Update`; key via `L2ProjectionKeys`, NEVER a literal; status const from `Create`, never a literal):
```csharp
public async Task WriteAsync(Guid processorId, string instanceId, ProcessorLivenessEntry entry)
{
    // D-09 / Open Q3 (RESEARCH A3): update L1 UNCONDITIONALLY (the watchdog wants latest in-process
    // truth, not Redis reachability). The L1 record IS the same immutable entry written to L2 this
    // iteration — provably identical.
    _l1.Update(entry);
    try
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(entry);                       // DEFAULT options (the [JsonPropertyName] pins carry the shape — Pitfall 2)
        var ttl  = Math.Max(entry.Interval * 2, _options.TtlSeconds);     // D-13: max(activeInterval*2, Ttl-floor); entry.Interval is the active interval (Pattern 3)

        // Blind whole-value last-write-wins SET..EX (LIVE-02/06) — NO RMW, NO stoppingToken (the
        // command timeout is the bound — heartbeat lines 91-96). Key via L2ProjectionKeys (never a literal).
        await db.StringSetAsync(
            L2ProjectionKeys.PerInstance(processorId, instanceId),
            json,
            expiry: TimeSpan.FromSeconds(ttl));

        // Idempotent SADD (D-15) — set semantics make a double-add by both loops harmless; no "have I
        // added?" flag. instanceId is already a string (InstanceId.Resolve()) — no ToString (RESEARCH SADD note).
        await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(processorId), instanceId);
    }
    catch (Exception ex)
    {
        // Resilience (D-11 / T-26-10): log-and-CONTINUE; never throw, never crash the host / abort resolution.
        _logger.LogWarning(ex,
            "Liveness write failed for processor {ProcessorId}; will retry next iteration", processorId);
    }
}
```
> `internal sealed` is fine (same assembly as both loops); but if `AddBaseProcessorFacts` asserts its registration descriptor (RESEARCH Wave 0), it is reachable from the test assembly — confirm visibility against how the writer is registered (a `services.AddSingleton<ProcessorLivenessWriter>()` concrete registration is descriptor-assertable even if the type is `internal`, provided `InternalsVisibleTo` exists for the test assembly — check; the simplest is `public sealed` like `ProcessorContext`).

---

### `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` (BackgroundService writer) — MODIFY

**Analog:** self. In-place swap of the write block (`ProcessorLivenessHeartbeat.cs:70-111`).

**What to KEEP unchanged:** the `IsHealthy && Id is { } id` gate (line 70 — "I am the *healthy* writer" signal, D-14), the `_clock.GetUtcNow().UtcDateTime` read (line 76 — same clock the reader uses), the deliberate no-`stoppingToken`-into-the-write comment (lines 91-96), the `catch → LogWarning → continue` resilience (lines 102-110), the `Task.Delay(period, _clock, stoppingToken)` loop tail (lines 113-120).

**What to SWAP** (the inside of the gate, replacing `ProcessorLivenessHeartbeat.cs:78-100`). BEFORE = `ProcessorProjection` + `L2ProjectionKeys.Processor(id)` + flat `TtlSeconds`. AFTER (route through the shared writer — Pitfall 6 — so L1 update + SADD + TTL come for free):
```csharp
if (_context.IsHealthy && _context.Id is { } id)
{
    try
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        // Frozen healthy (D-14): all outcomes SUCCESS => Create derives Healthy (no mid-life re-validation,
        // does NOT re-read context definition props — WR-03). Active interval = heartbeat IntervalSeconds (D-12).
        var entry = ProcessorLivenessEntry.Create(
            inputOutcome:  SchemaOutcome.Success,
            outputOutcome: SchemaOutcome.Success,
            configOutcome: SchemaOutcome.Success,
            timestamp:     now,
            interval:      opts.IntervalSeconds);
        await _writer.WriteAsync(id, _instanceId, entry); // SET(perInstance, ttl=max(20,30)=30) + SADD + L1 Update
    }
    catch (Exception ex) { /* keep existing log-and-continue (lines 102-110) — belt-and-braces; writer also catches */ }
}
```
**Ctor growth:** add the shared writer + the instanceId (`InstanceId.Resolve()` is callable at ctor — or inject the resolved string). The heartbeat is DI-resolved (`AddHostedService`), so ONLY `LivenessHeartbeatFacts` (which `new`s it) needs updating, not a DI signature (Pitfall 6). The `_redis`/`_options` fields it no longer uses directly (the writer owns Redis) can be dropped to avoid CS0169 unused-field warnings (SC-5 0-warning build) — keep only what the post-swap body reads.

> **Pitfall 5 (RESEARCH):** REMOVE the old `ProcessorProjection`/`L2ProjectionKeys.Processor(id)` write ENTIRELY — do NOT dual-write "for safety" (breaks the D-05 hard-swap + net-zero accounting). The TYPES survive (reader compiles against them, deleted Phase 61); the WRITE is gone.

---

### `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (BackgroundService writer) — MODIFY

**Analog:** self (the existing 3 backoff/iteration points) + the heartbeat write block for the write discipline.

**Iteration points to instrument** (each is where the orchestrator currently logs "retrying in {Delay}" / evaluates coverage):
1. Loop A — but ONLY *after* `context.SetIdentity(...)` populates `context.Id` (D-02: no `processorId` before identity resolves). The Loop-A retry iterations before identity have no key — accepted (Pitfall 3).
2. Loop B — each per-definition backoff iteration (`ProcessorStartupOrchestrator.cs:147-173`), `context.Id` is now non-null.
3. After Gate A — both the clash-return path (line 185-192) and the pass/skip path are post-identity; write the final `unhealthy` snapshot reflecting Gate A's outcome before `MarkHealthy` flips.

**The inline-write helper to add** (build outcomes from the orchestrator's OWN single-threaded resolution state — safe ONLY here, WR-03; feed straight into `ProcessorLivenessEntry.Create` — RESEARCH Pattern 1):
```csharp
private async Task WriteUnhealthyAsync()
{
    if (context.Id is not { } procId) return; // D-02: no processorId before Loop A resolves identity

    // null-is-skip => Success; non-null-but-unresolved (definition still null) => Fail; resolved => Success
    // (mirrors Create's null-is-skip — RESEARCH Pattern 1 per-schema rule + Open Q1 for config).
    static string Outcome(Guid? id, string? def) =>
        id is null ? SchemaOutcome.Success
                   : def is null ? SchemaOutcome.Fail : SchemaOutcome.Success;

    var now = clock.GetUtcNow().UtcDateTime; // SAME clock the reader uses (heartbeat line 76)
    var entry = ProcessorLivenessEntry.Create(
        inputOutcome:  Outcome(context.InputSchemaId,  context.InputDefinition),
        outputOutcome: Outcome(context.OutputSchemaId, context.OutputDefinition),
        configOutcome: Outcome(context.ConfigSchemaId, context.ConfigDefinition), // Open Q1 — confirm pre-Gate-A convention
        timestamp:     now,
        interval:      opts.StartupIntervalSeconds); // D-12: startup anchor = BackoffCap (30s)
    // status stays Unhealthy until ALL non-null schemas resolve (Create's invariant). Writer logs-and-continues.
    await writer.WriteAsync(procId, instanceId, entry);
}
```
**Ctor growth — Pitfall 1 (the biggest blast radius):** add the shared writer + the instanceId to the primary-constructor parameter list (`ProcessorStartupOrchestrator.cs:63-74`). `IConnectionMultiplexer` is already DI-registered by `AddBaseConsole`, so the writer resolves. This breaks all 3 direct `new ProcessorStartupOrchestrator(...)` call sites (below) + the DI registration — update ALL of them.

> Redis-fault resilience: the write goes through the shared writer's log-and-continue — a dead Redis must NOT crash startup or abort identity/definition resolution (CONTEXT discretion; mirrors heartbeat D-11).

---

### `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` (config/options) — MODIFY

**Analog:** self — the 5 existing `[ConfigurationKeyName]` seconds-int auto-properties (`ProcessorLivenessOptions.cs:20-41`).

**Existing knob shape to mirror** (lines 18-21 — bare config key, `Seconds`-suffixed property, baked default, XML doc):
```csharp
[ConfigurationKeyName("Interval")]
public int IntervalSeconds { get; set; } = 10;   // STAYS = heartbeat cadence (D-11; no appsettings churn)
```

**Pattern to add** (one new property beside the others; default 30 anchored to `BackoffCapSeconds`'s 30 — D-12; the config-key string is Claude's discretion, `"StartupInterval"` recommended):
```csharp
/// <summary>Startup-loop staleness anchor in seconds (D-12; default 30 = BackoffCap). Recorded as the
/// <c>interval</c> on the orchestrator's <c>unhealthy</c> entries so the Phase-61 staleness math + the
/// derived TTL cover the slowest backoff write.</summary>
[ConfigurationKeyName("StartupInterval")]
public int StartupIntervalSeconds { get; set; } = 30;
```
> `Ttl` / `IntervalSeconds` stay UNCHANGED (D-11/13: `Interval`=heartbeat, `Ttl`=floor). Update the class XML doc (lines 5-14) from "Five INDEPENDENT" to "Six INDEPENDENT" and add `StartupInterval` to the listed keys.

---

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (composition root) — MODIFY

**Analog:** self — line 136 (`AddSingleton<IProcessorContext, ProcessorContext>()`) for the holder; lines 178/182 (`AddHostedService`) for the loops; line 126 (`AddSingleton<ISourceHashProvider, ...>`) for a plain service.

**Singleton-holder registration to mirror** (line 134-136):
```csharp
// 6. The mutable identity/Healthy holder shared by the orchestrator (writer) and the heartbeat (reader).
services.AddSingleton<IProcessorContext, ProcessorContext>();
```

**Pattern to add** (alongside line 136 — the L1 holder; and the shared writer near it; the orchestrator at line 178 already resolves `IConnectionMultiplexer` from `AddBaseConsole`):
```csharp
// 6e. L1-01 (D-08): the dedicated in-memory liveness record, updated by BOTH loops, read by the
//     Phase-61 self-watchdog probe. Singleton — mirrors the IProcessorContext holder above.
services.AddSingleton<IProcessorLivenessState, ProcessorLivenessState>();

// 6f. The shared internal liveness writer (L2 SET + index SADD + L1 update) both loops call so the
//     SADD/TTL/L1 disciplines cannot drift. IConnectionMultiplexer is already registered by AddBaseConsole.
services.AddSingleton<ProcessorLivenessWriter>();
```
> No appsettings change for `Interval`/`Ttl` (D-11). The orchestrator (`AddHostedService` line 178) and heartbeat (line 182) keep their hosted-service registrations; their ctors grow, but DI resolves the added deps automatically.

---

### `tests/BaseApi.Tests/Processor/ProcessorLivenessStateFacts.cs` (unit, hermetic) — NEW

**Analog:** `ProcessorOptionsBindingFacts.cs` — pure-hermetic, no fixture, no stack.

**Pattern to build** (no Redis, no harness; `[Trait("Phase","60")]` on the class):
```csharp
[Fact]
public void Current_Is_Null_Before_First_Update()
{
    var state = new ProcessorLivenessState();
    Assert.Null(state.Current);
}

[Fact]
public void Update_Publishes_Last_Entry()  // L1-01 / D-10
{
    var state = new ProcessorLivenessState();
    var entry = ProcessorLivenessEntry.Create(
        SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success,
        DateTime.UtcNow, interval: 10);
    state.Update(entry);
    Assert.Same(entry, state.Current);          // SAME immutable reference (D-09)
}

[Fact]
public void Update_Overwrites_With_Latest()    // both-loops-update — last write wins
{
    var state = new ProcessorLivenessState();
    var first  = ProcessorLivenessEntry.Create(SchemaOutcome.Fail, null, null, DateTime.UtcNow, 30);
    var second = ProcessorLivenessEntry.Create(SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success, DateTime.UtcNow, 10);
    state.Update(first);
    state.Update(second);
    Assert.Same(second, state.Current);
    Assert.Equal(LivenessStatus.Healthy, state.Current!.Status); // assert via const, never a literal
}
```

---

### `tests/BaseApi.Tests/Processor/ProcessorLivenessWriterFacts.cs` (integration, RedisFixture) — NEW

**Analog:** `LivenessHeartbeatFacts.cs` — `IClassFixture<RedisFixture>`, `FakeTimeProvider`, `_redis.Track(...)`, `KeyTimeToLiveAsync` + `Assert.InRange` TTL banding, `StringGetAsync` + `JsonSerializer.Deserialize` value assertion.

**TTL-banding pattern to copy** (`LivenessHeartbeatFacts.cs:99-108`):
```csharp
var remaining = await db.KeyTimeToLiveAsync(key);
Assert.NotNull(remaining);
Assert.InRange(remaining!.Value.TotalSeconds, ttl - 5, ttl);
var raw = await db.StringGetAsync(key);
var entry = System.Text.Json.JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
Assert.Equal(LivenessStatus.Healthy, entry!.Status);
```

**Net-zero tracking — RESEARCH keyspace note (MUST):** `_redis.Track(L2ProjectionKeys.PerInstance(id, instId))` for the per-instance key AND `SREM`/track the `InstanceIndex(id)` SET *member* (the index SET is the shared contention point — mirror the Phase-22 parent-index `SREM`-your-own-id discipline; run in a non-parallel collection if it touches a shared SET).

**Pattern to build** (`[Trait("Phase","60")]`): construct the writer with `_redis.Multiplexer` + a `ProcessorLivenessState` + `Options(...)` + `NullLogger`; call `WriteAsync(id, "pod-x", entry)` with a startup entry (interval 30 ⇒ TTL band `(55, 60]`) and a heartbeat entry (interval 10 ⇒ TTL band `(25, 30]`); assert per-instance key present + correct `Status`/`Interval`, `SMEMBERS InstanceIndex(id)` contains `"pod-x"` and a second `WriteAsync` keeps the member count at 1 (SADD idempotency, D-15), and the `IProcessorLivenessState.Current` equals the written entry (L1 == L2, D-09).

---

### `tests/BaseApi.Tests/Processor/StartupUnhealthyWriteFacts.cs` (integration, harness + Redis) — NEW

**Analogs:** `IdentityResolutionFacts.cs` / `SchemaResolutionFacts.cs` (the `ProcessorTestHarness.BuildProvider` + `FakeTimeProvider` + `AdvanceUntilAsync` driving pattern) and `LivenessHeartbeatFacts` (the real-Redis GET/TTL assertions).

**Orchestrator-drive pattern to copy** (`IdentityResolutionFacts.cs:72-82` + the `Stub*()` seam + `AdvanceUntilAsync` at lines 105-113). Construct the orchestrator with its grown ctor (the new writer pointing at the test Redis + a resolved instanceId), drive past identity, and assert (RESEARCH Observable behaviors 1/2/3/9):
- per-instance key present with `Status == LivenessStatus.Unhealthy` during the post-identity resolution window;
- one schema unresolved ⇒ that `summary` field = `SchemaOutcome.Fail`; resolved ⇒ `Success`;
- `SMEMBERS InstanceIndex(id)` contains the instanceId;
- the OLD flat `L2ProjectionKeys.Processor(id)` key is NOT created by the orchestrator (D-05).

**Resilience case (RESEARCH LOOP-01):** point the orchestrator's writer at a dead Redis port; assert resolution still reaches Healthy and the host does not crash (the writer's log-and-continue). Stub Redis via NSubstitute (RESEARCH Environment Availability fallback) for the pure-shape variant if compose is unavailable. `[Trait("Phase","60")]` + net-zero `Track` both keys.

---

### `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` (integration, RedisFixture) — MODIFY

**Analog:** self.

**What to SWAP** (Pitfall 5): the two existing facts assert the OLD contract — `L2ProjectionKeys.Processor(testProcessorId)` (lines 46-47, 73-74) + `JsonSerializer.Deserialize<ProcessorProjection>` + `projection.Liveness.Status` (lines 106-108). Re-point to:
- `L2ProjectionKeys.PerInstance(testProcessorId, instanceId)` for the key + `_redis.Track`;
- `JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!)` + `Assert.Equal(LivenessStatus.Healthy, entry!.Status)`;
- TTL band stays `(ttl-5, ttl]` with `ttl=30` (heartbeat `max(20,30)=30`, D-13);
- add: index `SADD` member present + `IProcessorLivenessState.Current` == written entry (D-15/L1-01).

**Ctor update:** the heartbeat's `new ProcessorLivenessHeartbeat(...)` calls (lines 54-56, 87-89) gain the shared writer + instanceId; construct the writer over `_redis.Multiplexer` + a real `ProcessorLivenessState` + the same `Options(...)`. The `Options(...)` helper (lines 32-39) needs `StartupIntervalSeconds = 30` added if the writer reads it (it does not — TTL derives from `entry.Interval` — but add for completeness).

> Case A (not-yet-Healthy writes nothing) stays valid — the gate is unchanged; just swap the key it checks-absent to `PerInstance`.

---

### `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` (unit, hermetic) — MODIFY

**Analog:** self.

**Pattern to extend** (the existing 5-knob fact `Binds_Five_Independent...` lines 16-40 becomes SIX; `Empty_Config_Yields_Baked_Defaults` lines 42-56 gains the new default). Add the `"Processor:StartupInterval" = "30"` key + `Assert.Equal(30, opts!.StartupIntervalSeconds)`, and a default assertion `Assert.Equal(30, opts.StartupIntervalSeconds)` (RESEARCH Config-binding-test example). Update the class XML doc's key list.

---

### `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` (DI descriptor) — MODIFY

**Analog:** self — the `IProcessorContext` singleton-descriptor assert (lines 60-63) and the `IHostedService`/`ProcessorStartupOrchestrator` assert (lines 75-77).

**Descriptor-assert pattern to copy** (lines 60-63):
```csharp
Assert.Contains(services, d =>
    d.ServiceType == typeof(IProcessorContext) &&
    d.ImplementationType == typeof(ProcessorContext) &&
    d.Lifetime == ServiceLifetime.Singleton);
```

**Pattern to add** (assert the new L1 holder + writer registrations):
```csharp
Assert.Contains(services, d =>
    d.ServiceType == typeof(IProcessorLivenessState) &&
    d.ImplementationType == typeof(ProcessorLivenessState) &&
    d.Lifetime == ServiceLifetime.Singleton);

Assert.Contains(services, d =>
    d.ServiceType == typeof(ProcessorLivenessWriter) &&
    d.Lifetime == ServiceLifetime.Singleton);
```
> Both hosted-service descriptor asserts (orchestrator + heartbeat) stay valid — the ctors grow but the `AddHostedService` descriptors don't change. If the writer is `internal`, this fact's assembly needs `InternalsVisibleTo` (it already references `BaseProcessor.Core` internals via `FakeProcessorContext`-style usage — verify; `public sealed` sidesteps it).

---

### `tests/BaseApi.Tests/Processor/{IdentityResolution,SchemaResolution,DispatchBindSequence}Facts.cs` (ctor sites) — MODIFY

**Analog:** self — the `Stub*()` seam in `IdentityResolutionFacts.cs:121-172` (returns NSubstitute fakes) + the positional `new ProcessorStartupOrchestrator(...)` (lines 72-75).

**Current ctor call to update** (`IdentityResolutionFacts.cs:72-75`):
```csharp
var orchestrator = new ProcessorStartupOrchestrator(
    identityClient, schemaClient, sourceHash, context, gate, StubConnector(),
    StubMeterProviderHolder(), StubConfigTypeProvider(), options, fakeClock,
    NullLogger<ProcessorStartupOrchestrator>.Instance);
```

**Pattern to apply (Pitfall 1):** add the two new positional args (the shared writer + an instanceId string) to ALL THREE call sites. Follow the established `Stub*()` seam — add a `StubLivenessWriter()` helper (or construct a real `ProcessorLivenessWriter` over a stubbed `IConnectionMultiplexer`):
```csharp
internal static ProcessorLivenessWriter StubLivenessWriter() =>
    new(Substitute.For<IConnectionMultiplexer>(), new ProcessorLivenessState(),
        Options.Create(new ProcessorLivenessOptions()), NullLogger<ProcessorLivenessWriter>.Instance);
```
> A stubbed `IConnectionMultiplexer.GetDatabase()` returns a `null`/substitute `IDatabase`, and the writer's log-and-continue swallows any resulting fault — so these harness facts (which assert resolution/bind, NOT the L2 write) still pass without a real Redis. The `new` is in `IdentityResolutionFacts`; `SchemaResolutionFacts` and `DispatchBindSequenceFacts` `new` it too (grep-confirmed 3 sites) — update each.

## Shared Patterns

### Blind whole-value last-write-wins L2 write + log-and-continue
**Source:** `ProcessorLivenessHeartbeat.cs:84-110`
**Apply to:** the shared writer, both loops
`_clock.GetUtcNow().UtcDateTime` (same clock the reader uses) → `JsonSerializer.Serialize(entry)` under DEFAULT options → blind `StringSetAsync(key, json, expiry)` (no RMW, no `stoppingToken` threaded in — command timeout is the bound) → `catch (Exception) → LogWarning → continue` (never throw, never crash the host).

### Key/status/outcome via the SoT const — never a literal
**Source:** `L2ProjectionKeys.cs`, `LivenessStatus.cs`, `SchemaOutcome.cs`, `ProcessorLivenessEntry.Create`
**Apply to:** every write + every test assertion
Keys via `L2ProjectionKeys.PerInstance`/`.InstanceIndex`; status NEVER hand-derived — always built through `ProcessorLivenessEntry.Create(...)` (the single STATE-01/02 invariant point, `ProcessorLivenessEntry.cs:27-47`); status/outcome compared against `LivenessStatus.*`/`SchemaOutcome.*` consts in assertions (literals only inside `[InlineData]`).

### Lock-free cross-thread publication
**Source:** `ProcessorContext.cs:27,60,94` (`volatile`/`Interlocked` `IsHealthy`)
**Apply to:** `ProcessorLivenessState`
`volatile` reference field + plain assignment for `Update` (atomic for reference types) + plain read for `Current`. D-10 LOCKS this — NO `lock{}`, NO `Interlocked.Exchange<T>`.

### Idempotent index SADD
**Source:** `RedisProjectionWriter.cs:84` (`batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D"))`)
**Apply to:** the shared writer
`db.SetAddAsync(L2ProjectionKeys.InstanceIndex(processorId), instanceId)` — set semantics make a double-add by both loops harmless; no "have I added?" flag (D-15). `instanceId` is already a string (`InstanceId.Resolve()`) — no `ToString`.

### Options binding test discipline
**Source:** `ProcessorOptionsBindingFacts.cs:16-56`
**Apply to:** the `StartupInterval` knob
`AddInMemoryCollection({"Processor:Key" = "val"})` → `GetSection("Processor").Get<ProcessorLivenessOptions>()` → `Assert.Equal`; plus an empty-config baked-default fact.

### Hermetic + RedisFixture net-zero test discipline
**Source:** `LivenessHeartbeatFacts.cs` (Redis), `ProcessorOptionsBindingFacts.cs` (pure), `IdentityResolutionFacts.cs` (harness)
**Apply to:** all Phase-60 tests
`[Trait("Phase","60")]` on new classes; `IClassFixture<RedisFixture>` + `FakeTimeProvider` + `_redis.Track(...)` for Redis facts (track the per-instance key AND `SREM`/track the index SET member — the shared contention point); `ProcessorTestHarness.BuildProvider` + `AdvanceUntilAsync` for orchestrator-driven facts; NSubstitute `Stub*()` seam for collaborators.

### 0-warning additive build (SC-5)
**Source:** `Directory.Build.props` (Nullable enable, `TreatWarningsAsErrors=true`)
**Apply to:** all new/modified `.cs`
No unused `private` fields (drop the heartbeat's now-unused `_redis`/`_options` if the writer owns them — CS0169/IDE0051); record reference types non-null; build BOTH Release and Debug with `-warnaserror`.

## No Analog Found

None. Every symbol mirrors an existing in-repo idiom:
- L1 holder ← `ProcessorContext`'s `volatile`/`Interlocked` publication;
- shared writer ← heartbeat write block + `RedisProjectionWriter.SetAddAsync`;
- options knob ← the 5 existing `[ConfigurationKeyName]` knobs;
- every test ← an existing fact class in the same `tests/BaseApi.Tests/Processor/` slice.

The only non-mechanical judgment calls are RESEARCH Open Q1 (pre-Gate-A `configSchema` outcome convention) and Open Q3 (L1-update-on-Redis-fault ordering) — both flagged in the Pattern Assignments above, neither a missing-analog gap.

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/{Liveness,Startup,Configuration,Identity,DependencyInjection}/`, `src/Messaging.Contracts/{Projections,Identity}/`, `src/BaseApi.Service/Features/Orchestration/Projection/`, `tests/BaseApi.Tests/Processor/`
**Files read in full this session:** `ProcessorLivenessHeartbeat.cs`, `ProcessorLivenessOptions.cs`, `ProcessorContext.cs`, `IProcessorContext.cs`, `ProcessorStartupOrchestrator.cs`, `BaseProcessorServiceCollectionExtensions.cs`, `ProcessorLivenessEntry.cs`, `L2ProjectionKeys.cs`, `LivenessStatus.cs`, `SchemaOutcome.cs`, `InstanceId.cs`, `LivenessHeartbeatFacts.cs`, `ProcessorOptionsBindingFacts.cs`, `FakeProcessorContext.cs`, `IdentityResolutionFacts.cs` (+ targeted reads of `RedisProjectionWriter.cs`, `AddBaseProcessorFacts.cs`, `ProcessorTestHarness.cs`); grep-confirmed the 3 `new ProcessorStartupOrchestrator(...)` ctor sites
**Pattern extraction date:** 2026-06-13
