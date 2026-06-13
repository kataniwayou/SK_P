# Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe - Pattern Map

**Mapped:** 2026-06-13
**Files analyzed:** 13 (4 modify, 2 create, 1 delete, 6 test add/re-point)
**Analogs found:** 13 / 13 (every file is a generalization/teardown of shipped in-repo code)

> All excerpts below are real, verified file:line reads from this session. The planner should reference these analogs directly in each plan's action section. Discretion items (per CONTEXT D-04/D-08 + RESEARCH Open-Q) are flagged inline.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseApi.Service/.../Validation/ProcessorLivenessValidator.cs` | validator/service | request-response (Redis read) | **its own shipped body** (single-key → SMEMBERS→GET-each) | exact (self-generalize) |
| `src/BaseApi.Service/.../Projection/RedisProjectionKeys.cs` | config/key-forwarder | n/a (string builders) | its own `Root`/`Step`/`Processor` forwarders | exact (delete one member) |
| `src/BaseApi.Service/.../OrchestrationValidationException.cs` | model (exception factory) | request-response | its own `ProcessorNotLive` factory (`:76-81`) + `ProcessorLivenessOffending` (`:97`) | exact (extend reason) |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | config/key SoT | n/a | its own `Processor(Guid)` builder (`:40`) | exact (delete builder) |
| `src/Messaging.Contracts/Projections/ProcessorProjection.cs` | model (record) | n/a | DELETE — superseded by `ProcessorLivenessEntry` | exact (delete) |
| `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` **(NEW)** | health-check | event-driven (probe pull) | `BaseConsole.Core/Health/BusReadyHealthCheck.cs` | exact (outer-bridged IHealthCheck) |
| `src/BaseConsole.Core/Health/HealthCheckDescriptor.cs` **(NEW)** | model (record seam) | n/a | the `new BusReadyHealthCheck(_outer)` bridge + `.AddCheck(...)` chain | role-match (new generic seam) |
| `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` | hosted-service (listener) | request-response (Kestrel) | its own `StartAsync` AddHealthChecks chain (`:73-86`) | exact (extend chain) |
| `src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` | DI registration | n/a | its own `AddBaseConsoleHealth` (`:30-43`) | exact (add seam) |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | DI registration | n/a | the Phase-60 `IProcessorLivenessState` singleton site (`:142`) | exact (adjacent registration) |
| `tests/BaseApi.Tests/.../ProcessorLivenessFacts.cs` | test (real-Redis integration) | request-response | its own shipped facts (`SeedLivenessAsync` `:101`) | exact (re-point seeding) |
| `tests/BaseApi.Tests/.../Projection/RedisProjectionKeysTests.cs` + `L2ProjectionKeysTests.cs` | test (golden pins) | n/a | their own `Processor_*` facts | exact (delete pins) |
| `tests/.../ProjectionRecordRoundTripTests.cs` | test (round-trip) | n/a | its own `ProcessorProjection_*` facts | exact (delete facts) |
| **NEW** `ProcessorLivenessGateUnitTests.cs` + `LivenessWatchdogHealthCheckTests.cs` + processor `/health/live` fixture | test (unit + Generic-Host) | — | `FakeRedis.cs`, `ConsoleTestHostFixture.cs` / `ConsoleHealthLiveTests.cs` | role-match |

---

## Pattern Assignments

### `ProcessorLivenessValidator.cs` (validator, Redis request-response) — MODIFY (D-06/07/08/09/10)

**Analog:** its own shipped body — `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs`. The swap is a mechanical generalization of the single-key loop to a two-level (SMEMBERS → GET-each) loop **inside the same `try` discipline**. Ctor (`:18-25`, `IConnectionMultiplexer` + `TimeProvider`), `_clock.GetUtcNow().UtcDateTime` clock discipline (`:30`), and the `foreach (proc in snapshot.Processors.Values)` shell (`:31`) are UNCHANGED.

**Shipped single-key core to replace** (`:33-58`):
```csharp
var raw = await db.StringGetAsync(RedisProjectionKeys.Processor(proc.Id));
if (raw.IsNullOrEmpty)
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "absent");
ProcessorProjection? projection;
try { projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!); }
catch (JsonException) { throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed"); }
if (projection?.Liveness is not { } liveness)
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed");
var deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2);   // ← preserve this exact math
if (deadline <= now)
    throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "stale");
```

**New shape to copy** (verified in RESEARCH §Code Examples lines 281-315). Key changes:
- `db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(proc.Id))` → `RedisValue[]` (per-replica discovery; in-repo precedent `Orchestrator/Hydration/HydrationBackgroundService.cs:42`).
- per-member `db.StringGetAsync(L2ProjectionKeys.PerInstance(proc.Id, member.ToString()))`.
- deserialize to **`ProcessorLivenessEntry`** (NOT `ProcessorProjection`) — `entry.Status != LivenessStatus.Healthy` ⇒ unhealthy; `entry.Summary is null` ⇒ malformed.
- staleness: `entry.Timestamp.AddSeconds(entry.Interval * 2) <= now` (identical math to shipped `:55`).
- absent-only lazy SREM: `_ = db.SetRemoveAsync(L2ProjectionKeys.InstanceIndex(proc.Id), member, CommandFlags.FireAndForget)` (D-09).
- `qualified = true; break;` short-circuits on first healthy+fresh replica (D-07).
- no qualifier ⇒ `throw OrchestrationValidationException.ProcessorNotLive(proc.Id, aggregateReason)`.

**Load-bearing constraints (carried, do not regress):**
- The WR-01 comment block (`:37-40`) explains why malformed/null-liveness map to 422, not 500 — re-author it for the per-replica world. **Every data state must throw `OrchestrationValidationException`**, never let `JsonException`/NRE escape (the 500 catch is in the CALLER, see Shared Pattern A — RESEARCH Pitfall 1).
- Compare against `LivenessStatus.Healthy` const, never the literal `"Healthy"` (D-07; const at `LivenessStatus.cs:12`).
- Deserialize with **default** STJ options — `[property: JsonPropertyName]` on `ProcessorLivenessEntry` carries the wire shape (`ProcessorLivenessEntry.cs:15-18`); do not add a camelCase policy (RESEARCH Pitfall 5).

---

### `RedisProjectionKeys.cs` (key-forwarder) — MODIFY (D-11)

**Analog:** its own forwarder body — `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs`.

**Delete line 19:**
```csharp
public static string Processor(Guid processorId) => L2ProjectionKeys.Processor(processorId);
```

**Decision for the planner — do NOT add `PerInstance`/`InstanceIndex` forwarders.** The writer-side convention does NOT forward these: the Phase-60 writer (`ProcessorLivenessWriter`) and the Phase-61 gate call `L2ProjectionKeys.PerInstance`/`InstanceIndex` **directly** (the gate already imports `Messaging.Contracts.Projections`, see `ProcessorLivenessValidator.cs:3`). The forwarder exists only for the *writer's* legacy flat keys (`ParentIndex`/`Root`/`Step`). CONTEXT D-11 says "or via new forwarders if the writer-side convention warrants" — it does not (no writer call site forwards the per-instance builders). Keep the validator calling `L2ProjectionKeys.*` directly.

---

### `OrchestrationValidationException.cs` (exception factory) — MODIFY (D-08)

**Analog:** its own `ProcessorNotLive` factory + `ProcessorLivenessOffending` record.

**Shipped factory** (`:76-81`) — **signature is UNCHANGED** `(Guid procId, string reason)`:
```csharp
public static OrchestrationValidationException ProcessorNotLive(Guid procId, string reason)
    => new("processorLiveness", "Participating processor is not live",
           $"Processor '{procId}' is not live: {reason}.",
           new ProcessorLivenessOffending(procId, reason));
```

**Record** (`:97`):
```csharp
public sealed record ProcessorLivenessOffending(Guid procId, string reason);
```

**Change (D-08, discretion on format):** keep the `(procId, reason)` shape (CONTEXT discretion — structured breakdown only if trivially additive; RESEARCH Open-Q 2 recommends a count string). The only edit is the `reason` STRING the *validator* passes — an aggregate like `"no healthy replica (3 checked: 1 absent, 1 unhealthy, 1 stale, 0 malformed)"`. **Info-disclosure guard (SECURITY V7 / T-14-02):** the reason carries COUNTS only, never instanceIds. Update the `///` doc on `:96` (`"absent"|"stale"|"malformed"`) to describe the aggregate. The 422 RFC-7807 envelope (`ErrorsExtension` `{ gate, offending }`, `:33`) and the handler are UNCHANGED.

---

### `L2ProjectionKeys.cs` (key SoT) — MODIFY (D-11)

**Analog:** its own builders. **Delete `Processor(Guid)` at line 40:**
```csharp
public static string Processor(Guid processorId) => $"{Prefix}{processorId}";
```
Also update the XML doc bullet (`:23`) describing `Processor`, and the class-level note (`:11-13`) about `Root`/`Processor` byte-identity. The retained `PerInstance` (`:46`) and `InstanceIndex` (`:52`) are the gate's new targets — their doc already references the Phase-60 writer + Phase-61 gate (`:49-53`).

---

### `ProcessorProjection.cs` (record) — DELETE (D-11)

**File:** `src/Messaging.Contracts/Projections/ProcessorProjection.cs` (full body, `:14-17`):
```csharp
public sealed record ProcessorProjection(
    [property: JsonPropertyName("inputDefinition")]  string? InputDefinition,
    [property: JsonPropertyName("outputDefinition")] string? OutputDefinition,
    [property: JsonPropertyName("liveness")]         LivenessProjection Liveness);
```
**CRITICAL:** This record carries a `LivenessProjection` member — deleting `ProcessorProjection` does NOT delete the SHARED `LivenessProjection` (still used by `WorkflowRootProjection` — RESEARCH §Compile-break, Pitfall in CONTEXT D-11). **Never touch `LivenessProjection`.** Verified callers of `ProcessorProjection` (all re-pointed/removed this phase): the validator (swapped) + `ProcessorLivenessFacts.SeedLivenessAsync` + `ProjectionRecordRoundTripTests.ProcessorProjection_*`.

---

### `LivenessWatchdogHealthCheck.cs` (health-check) — CREATE (D-01/02/03/04)

**Analog:** `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs` — the **exact** outer-`IServiceProvider`-bridged `IHealthCheck` to mirror.

**Pattern to copy** (`BusReadyHealthCheck.cs:36-68`):
```csharp
public sealed class BusReadyHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;
    public BusReadyHealthCheck(IServiceProvider outer) => _outer = outer;   // ← copy this ctor shape

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var bus = _outer.GetService<IBusControl>();              // resolve OUTER singleton at check time
        if (bus is null) return Task.FromResult(HealthCheckResult.Unhealthy("Bus not started"));
        // ... map to Healthy/Unhealthy ...
    }
}
```

**Watchdog body** (verified shape, RESEARCH §Code Examples `:320-345`):
- `_outer.GetRequiredService<IProcessorLivenessState>()` — Singleton (`BaseProcessorServiceCollectionExtensions.cs:142`); resolved at check time, NOT captured at registration (RESEARCH Pitfall 4).
- `_outer.GetRequiredService<TimeProvider>()` — same clock the writer + gate use (`TimeProvider.System` registered `BaseProcessorServiceCollectionExtensions.cs:124`); `now = clock.GetUtcNow().UtcDateTime`.
- `state.Current is null` ⇒ `HealthCheckResult.Unhealthy("liveness loop not started")` (D-02; `Current` is null until first write per `IProcessorLivenessState.cs:19`).
- `now > current.Timestamp.AddSeconds(current.Interval * 2)` ⇒ `Unhealthy("...stale...", data: data)` (D-03 — identical math to the gate).
- else ⇒ `Healthy("live", data: data)`.
- **`data` dictionary (D-04, key names = discretion):** carry `current.Summary.InputSchema/OutputSchema/ConfigSchema` (record `LivenessSummary` at `ProcessorLivenessEntry.cs:55-58`). All three fields MUST be present. `HealthCheckResult.Healthy/Unhealthy(description, …, IReadOnlyDictionary<string,object>? data)` — the UI writer serializes per-check `data` (Shared Pattern C).

**Namespace:** `BaseProcessor.Core.Liveness` (sibling to `IProcessorLivenessState`).

---

### `HealthCheckDescriptor.cs` + `EmbeddedHealthEndpointService.cs` (generic seam) — CREATE + MODIFY (D-05)

**Analog:** the existing OUTER-bridge wiring inside `EmbeddedHealthEndpointService.StartAsync`.

**Shipped bridge + chain** (`EmbeddedHealthEndpointService.cs:73-86`):
```csharp
// Bridge to the OUTER bus health so /health/ready reflects real bus state (Open-Q 1).
builder.Services.AddSingleton(new BusReadyHealthCheck(_outer));

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })       // live = self-only
    .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup" })
    .AddCheck<BusReadyHealthCheck>("bus-ready", tags: new[] { "ready" });
// ...
_app.MapHealthChecks("/health/live", new HealthCheckOptions {
    Predicate = c => c.Tags.Contains("live"),                      // ← live-tagged check auto-mapped, NO change
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});
```

**New seam (RESEARCH Pattern 2, `:194-210`):** add a `HealthCheckDescriptor` record (suggested `record HealthCheckDescriptor(string Name, string[] Tags, Func<IServiceProvider, IHealthCheck> Factory)`) in `BaseConsole.Core/Health/`. In `StartAsync`, after the existing `AddHealthChecks()` chain, enumerate the OUTER-registered descriptors and fold each in:
```csharp
var hc = builder.Services.AddHealthChecks()
    .AddCheck("self", ...).AddCheck<StartupHealthCheck>(...).AddCheck<BusReadyHealthCheck>(...);

foreach (var d in _outer.GetServices<HealthCheckDescriptor>())     // NEW: enumerate outer descriptors
{
    var captured = d;
    hc.AddCheck(captured.Name, captured.Factory(_outer), tags: captured.Tags);   // factory bridges _outer in
}
```
`AddCheck(string, IHealthCheck instance, tags)` accepts a pre-built instance (factory builds it with `_outer`). A `"live"`-tagged descriptor is picked up by the unchanged `/health/live` predicate (`:84`). `_outer` is already the injected ctor field (`:46, :53`). **D-01 scope:** Orchestrator/Keeper register NO descriptor ⇒ their `/health/live` stays self-only-unchanged; only `BaseProcessor.Core` populates the collection.

---

### `ConsoleHealthServiceCollectionExtensions.cs` (DI seam) — MODIFY/optional (D-05)

**Analog:** `AddBaseConsoleHealth` (`:30-43`). The descriptor collection is resolved from the OUTER container via `_outer.GetServices<HealthCheckDescriptor>()`, so it needs only `services.AddSingleton(new HealthCheckDescriptor(...))` registrations (no explicit collection type). **No change is strictly required here** — `BaseProcessor.Core` does the registration (next file). The only candidate edit is a doc note that the inner listener now folds outer descriptors. The shipped outer chain (`:34-36`) stays.

---

### `BaseProcessorServiceCollectionExtensions.cs` (DI registration) — MODIFY (D-05)

**Analog:** the Phase-60 `IProcessorLivenessState` singleton registration site (`:139-142`):
```csharp
// 6a. Phase 60 ... the in-memory L1 liveness holder ... read by the Phase-61 self-watchdog probe.
services.AddSingleton<IProcessorLivenessState, ProcessorLivenessState>();
```
**Add adjacent (alongside `:142`, before/after `ProcessorLivenessWriter` `:149`):** register the watchdog as a `HealthCheckDescriptor` whose factory closes over the outer provider:
```csharp
services.AddSingleton(new HealthCheckDescriptor(
    Name: "liveness-watchdog",
    Tags: new[] { "live" },                                          // D-05: live-tagged
    Factory: outer => new LivenessWatchdogHealthCheck(outer)));      // bridges outer IProcessorLivenessState
```
`TimeProvider.System` is already registered (`:124`); `IProcessorLivenessState` at `:142`. Add `using BaseConsole.Core.Health;` for the descriptor type. The watchdog arrives transitively via `AddBaseProcessor` — `Processor.Sample/Program.cs:16` calls `AddBaseProcessor` and does NO per-app health wiring, so nothing changes there.

---

### Tests

**Re-point `ProcessorLivenessFacts.cs` (real-Redis integration — RESEARCH Pitfall 2).** These are HTTP-level facts against a REAL Redis (`_factory.RedisMultiplexer.GetDatabase()` `:119`), NOT a mock. Re-point `SeedLivenessAsync` (`:101-106`) off `ProcessorProjection`/`L2ProjectionKeys.Processor` onto the per-instance keyspace:
```csharp
// SHIPPED (delete):
var projection = new ProcessorProjection(null, null, liveness);
await db.StringSetAsync(L2ProjectionKeys.Processor(procId), JsonSerializer.Serialize(projection));
// NEW: SADD the index + SET each per-instance ProcessorLivenessEntry.Create(...)
await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), instanceId);
await db.StringSetAsync(L2ProjectionKeys.PerInstance(procId, instanceId),
    JsonSerializer.Serialize(ProcessorLivenessEntry.Create(null, null, null, DateTime.UtcNow, 300)));
_factory.TrackRedisKey(L2ProjectionKeys.InstanceIndex(procId));
_factory.TrackRedisKey(L2ProjectionKeys.PerInstance(procId, instanceId));
```
Keep the 422 body assertions (`gate=="processorLiveness"`, `offending.procId`, `offending.reason`) — the `reason` assertion now matches the aggregate string (D-08). The `MalformedProcessorRegistration_Returns422` theory (`:204-243`) still seeds raw bytes — re-point the SADD+key to the per-instance shape. The "stale" fact seeds a stale `ProcessorLivenessEntry`. Class uses `[Collection("ParentIndex")]` (`:42`) + `TrackRedisKey` cleanup discipline — preserve.

**Delete golden pins (D-11).** In `RedisProjectionKeysTests.cs`: delete `Processor_Produces_*` (`:47-52`) + `Root_And_Processor_Are_ByteIdentical_*` (`:54-63`). In `L2ProjectionKeysTests.cs`: delete `Processor_Produces_*` (`:42-45`) + `Root_And_Processor_Are_ByteIdentical_*` (`:66-71`).
**A1 CORRECTION (RESEARCH assumption A1 is WRONG):** the `PerInstance`/`InstanceIndex` golden pins ALREADY EXIST in `L2ProjectionKeysTests.cs:48-64` (incl. a Phase-61 `PerInstance_Is_Prefixed_By_Its_InstanceIndex` forward-fit fact). The planner does NOT need to add them — only delete the `Processor`/byte-identity pins.

**Delete round-trip facts (D-11).** In `ProjectionRecordRoundTripTests.cs`: delete the two `ProcessorProjection_*` facts; KEEP the `WorkflowRoot_*` facts (LivenessProjection untouched).

**NEW `ProcessorLivenessGateUnitTests.cs`** (pure unit — GATE-01/02/03). Analog: `FakeRedis.cs` (`Substitute.For<IDatabase>()`/`IConnectionMultiplexer` shapes). RESEARCH Open-Q 1 recommends a fresh minimal `Substitute.For<IDatabase>()` in BaseApi.Tests (not extending Keeper's `FakeRedis`). **Pitfall 3 (mandatory stubs):** stub `SetMembersAsync(InstanceIndex(id))` → seeded `RedisValue[]`; stub each `StringGetAsync(PerInstance(id, member))` → serialized entry or `RedisValue.Null`; stub `SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), CommandFlags.FireAndForget)` even if unasserted (else the fire-and-forget NREs on an unconfigured Task). Mirror `FakeRedis.BuildDatabase` (`:137-206`) stub idiom (`Arg.Any<...>().Returns(_ => ...)`). Cases: ≥1 healthy+fresh ⇒ no throw; all unhealthy/stale/absent ⇒ `OrchestrationValidationException` with `gate=="processorLiveness"` + aggregate reason; assert lazy-SREM fired for the absent member only.

**NEW `LivenessWatchdogHealthCheckTests.cs`** (pure unit — PROBE-01/02). Fabricate `IProcessorLivenessState` (NSubstitute or a hand-rolled fake) with `Current` = null / stale / fresh `ProcessorLivenessEntry`; control `now` via a `FakeTimeProvider`/`TimeProvider` stub bridged through a `Substitute.For<IServiceProvider>()` (the watchdog resolves both from `_outer`). Call `CheckHealthAsync` directly; assert `HealthCheckResult.Status` (Unhealthy/Unhealthy/Healthy) AND the three summary keys are present in `result.Data`.

**NEW processor `/health/live` Generic-Host fixture** (integration — PROBE-02). Analog: `ConsoleTestHostFixture.cs` (`protected virtual ConfigureBuilder` overridable `:88`) + `ConsoleHealthLiveTests.cs`. Subclass the fixture, compose `AddBaseProcessor` (per `Processor.Sample/Program.cs:16`), seed a stale/fresh `IProcessorLivenessState`; GET `/health/live`; assert body carries `inputSchema`/`outputSchema`/`configSchema` (PROBE-02) and flips Unhealthy on stale/null. Mirror `ConsoleHealthLiveTests.Live_Body_Has_No_Secrets` (`:30-49`) to assert no instanceId/secret/stack-trace leak (SECURITY V7).

---

## Shared Patterns

### A. 422-vs-500 split — preserved through TWO layers (Pitfall 1)
**Source:** `OrchestrationService.cs:193-201` (the CALLER, NOT the validator).
**Apply to:** the gate swap.
```csharp
try { await _processorLivenessValidator.ValidateAsync(snapshot, ct); }
catch (RedisException ex) { ex.Data["redisOp"] = "UpsertAsync"; throw; }   // 500 path ONLY
// OrchestrationValidationException is NOT a RedisException → flows PAST → 422 handler.
```
The validator must NEVER add its own `RedisException` catch and must convert ALL data states (absent/unhealthy/stale/malformed) to `OrchestrationValidationException`. A genuine `RedisException` on `SMEMBERS`/`GET` propagates untouched ⇒ 500 (D-10).

### B. Outer-IServiceProvider bridge into the inner Kestrel container
**Source:** `BusReadyHealthCheck.cs:38-44` (`_outer` ctor) + `EmbeddedHealthEndpointService.cs:73` (`new BusReadyHealthCheck(_outer)`).
**Apply to:** `LivenessWatchdogHealthCheck` (reads OUTER `IProcessorLivenessState`/`TimeProvider`) + the generic `HealthCheckDescriptor.Factory(_outer)` seam.

### C. UI per-check `data` serialization (free `summary` carriage — D-04)
**Source:** `EmbeddedHealthEndpointService.cs:85` — `ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse`.
**Apply to:** the probe — attaching `summary` fields to `HealthCheckResult.data` rides into the `/health/live` body with no writer change.

### D. String-const SoT comparison (never literals)
**Source:** `LivenessStatus.cs:12` (`Healthy = "Healthy"`), `SchemaOutcome.cs:11-12` (`Success/Fail`).
**Apply to:** gate (`entry.Status != LivenessStatus.Healthy`) + the entry's `Create` invariant. Never compare against `"Healthy"` literal (Anti-pattern, RESEARCH `:225`).

### E. `[property: JsonPropertyName]` default-options round-trip (Pitfall 5)
**Source:** `ProcessorLivenessEntry.cs:14-18` + `LivenessSummary:55-58`.
**Apply to:** gate deserialize (`JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!)` with DEFAULT options — matches the writer's default-options serialize). The positional ctor is public ONLY for STJ; `Create(...)` (`:27`) is the only sanctioned construction path in production/tests.

### F. Fire-and-forget SREM (D-09)
**Source:** RESEARCH §Verified SREM signature + StackExchange.Redis upstream.
**Apply to:** absent-only index hygiene — `db.SetRemoveAsync(key, member, CommandFlags.FireAndForget)`; never awaited, never throws into the verdict, absent-only (NOT stale/unhealthy).

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `HealthCheckDescriptor.cs` | model (generic seam) | n/a | A genuinely new generic record. CLOSEST precedent is the one-off `new BusReadyHealthCheck(_outer)` bridge it generalizes (`EmbeddedHealthEndpointService.cs:73`) — there is no existing descriptor-collection seam in the repo. Shape is locked by D-05 (Name + Tags + `Func<IServiceProvider,IHealthCheck>`); planner builds it from RESEARCH Pattern 2. |

All other files have an exact or strong role-match analog. No file requires falling back to RESEARCH-only generic patterns.

---

## Metadata

**Analog search scope:** `src/BaseApi.Service/Features/Orchestration/{Validation,Projection}`, `src/BaseConsole.Core/{Health,DependencyInjection}`, `src/BaseProcessor.Core/{Liveness,DependencyInjection}`, `src/Messaging.Contracts/Projections`, `src/Processor.Sample`, `tests/BaseApi.Tests/{Features/Orchestration,Console,Keeper}`.
**Files read this session:** 18 (all targeted, no re-reads).
**Pattern extraction date:** 2026-06-13
**Notable correction:** RESEARCH assumption A1 is FALSE — the `PerInstance`/`InstanceIndex` golden pins already exist (`L2ProjectionKeysTests.cs:48-64`). Planner deletes the legacy `Processor`/byte-identity pins only; no new pins to add.
