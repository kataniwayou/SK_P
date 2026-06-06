# Phase 38: Uniform `service_name` + Instance Labels Across All Metrics — Pattern Map

**Mapped:** 2026-06-06
**Files analyzed:** 13 (3 new, 10 modified)
**Analogs found:** 13 / 13 (every new/modified file has a same-repo template)

> The hard mechanism is the processor `MeterProviderHolder` swap (MLBL-03 / D-02/D-03). Its closest
> in-repo analog is the **mutable-singleton-with-setter** pattern of `ProcessorContext`/`IProcessorContext`
> for the *registration + threading discipline*, and `ProcessorMetrics` for the *OTel-owned, meter-by-name,
> sealed-singleton, `IDisposable`-resource* shape. There is no existing standalone `Sdk.CreateMeterProviderBuilder()`
> owner — that is genuinely new (research §"The holder type"), so the holder copies *registration + lifecycle
> idioms* from these analogs, not a literal swap body.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| **NEW** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs` | provider/service (OTel resource owner) | event-driven (swap on identity-resolve) | `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` (OTel-owned sealed singleton) + `ProcessorContext.cs` (mutable singleton w/ setter) | role-match (composite; no literal swap analog) |
| **NEW** `tests/BaseApi.Tests/Observability/MeterProviderHolderFacts.cs` | test (hermetic) | request-response (build→inspect resource) | `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` (OTel-provider hermetic, build+inspect) | role-match |
| **NEW** contract/responder test for `ProcessorIdentityFound` Name/Version | test (hermetic, MT harness) | request-response | `tests/BaseApi.Tests/Messaging/ProcessorResponderTests.cs` | **exact** (same consumer, extend in place) |
| `src/Messaging.Contracts/ProcessorQueries.cs` | model (contract record) | request-response | sibling records in same file (`ProcessorIdentityFound` itself) | **exact** |
| `src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs` | responder (MT consumer) | request-response | itself (line 25-26 projection) | **exact** |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` | model (interface) | event-driven (mutable singleton) | itself (schema-Id props mirror) | **exact** |
| `src/BaseProcessor.Core/Identity/ProcessorContext.cs` | model (impl) | event-driven | itself (`SetIdentity` storage block) | **exact** |
| `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` | config (DI/OTel wiring) | transform (resource build) | itself (metrics `ConfigureResource` line 65-67) | **exact** |
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` | config (DI/OTel wiring) | transform | itself (metrics `ConfigureResource` line 70-72) | **exact** |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | service (BackgroundService) | event-driven | itself (Loop A `SetIdentity` site line 79-85) | **exact** |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | config (composition root) | — | itself (singleton + meter-provider reg, line 99/121-122) | **exact** |
| `src/Processor.Sample/Program.cs` | config (composition root) | — | itself (thin-shell call order) | **exact** |
| PromQL consumers (`MetricsExportTests.cs`, `SchemasMetricsE2ETests.cs`, `PrometheusTestClient.cs` doc) | test (literal update) | request-response | each other | **exact** (literal swap only) |
| `IProcessorContext` fakes (`StubContext`, `FakeProcessorContext`, `RecordingContext`) | test (interface impl) | — | `FakeProcessorContext.cs` (settable-props shape) | **exact** |

---

## Pattern Assignments

### NEW `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs` (provider, event-driven swap) — HIGHEST VALUE

**Analogs (composite):**
- **Registration + threading discipline** → `ProcessorContext` / `IProcessorContext` (mutable singleton, set-once, WR-03 memory-visibility).
- **OTel-owned, meter-by-name, sealed, disposable resource** → `ProcessorMetrics`.
- **Swap body itself** → no analog; copy from RESEARCH §"The holder type" (lines 117-166) verbatim.

**Why these are the analogs:** `MeterProviderHolder` is a `sealed` singleton that (a) is `services.AddSingleton<>()`-registered exactly like `IProcessorContext`/`ProcessorMetrics`, (b) is mutated **once** after async identity-resolve (the `SetIdentity` → here `SwapTo` trigger), and (c) owns an `IDisposable` OTel pipeline whose meter set is referenced **by name** — the exact `ProcessorMetrics.MeterName` const idiom. It is NOT a literal copy of any one file because no existing type owns a `Sdk.CreateMeterProviderBuilder()` provider.

**Singleton-set-once threading pattern to mirror** — `ProcessorContext.cs:25-31, 57-63`:
```csharp
public sealed class ProcessorContext : IProcessorContext        // sealed → AddSingleton across asm boundary, no InternalsVisibleTo
{
    public Guid? Id { get; private set; }                       // plain auto-prop, written once post-resolve
    public void SetIdentity(ProcessorIdentityFound identity)    // the single mutation entry point (analog of SwapTo)
    {
        Id = identity.Id;
        Version = identity.Version;                             // (after this phase's edit)
    }
}
```
**WR-03 carry-over for the holder:** the swap reads `found.Message.Name`/`.Version` **straight off the received message** at the call-site (RESEARCH line 237) — NOT via `context.Name`/`.Version` — so the memory-visibility invariant is moot for the swap itself. The holder's own `_current` field is single-threaded (only Loop A writes it, before `MarkHealthy`).

**OTel-owned sealed-singleton + meter-by-name pattern to mirror** — `ProcessorMetrics.cs:25-28, 42-48`:
```csharp
public sealed class ProcessorMetrics                            // sealed; DI singleton; OTel-owned
{
    public const string MeterName = "BaseProcessor";           // the SAME const the holder's .AddMeter(...) must reference
    public ProcessorMetrics(IMeterFactory meterFactory) { var meter = meterFactory.Create(MeterName); ... }
}
```
The holder's `Build()` must call `.AddMeter(ProcessorMetrics.MeterName)` (NOT a string literal) + `.AddMeter(MassTransit.Monitoring.InstrumentationOptions.MeterName)` + `.AddRuntimeInstrumentation()` + bare `.AddOtlpExporter()`. **Bare** `AddOtlpExporter()` is load-bearing — it inherits `OTEL_EXPORTER_OTLP_ENDPOINT` (MEMORY: appsettings endpoint key is dead). Mirror the shared path's bare call at `BaseConsoleObservabilityExtensions.cs:73`.

**The swap body (no analog — copy verbatim from RESEARCH lines 155-165):**
```csharp
public void SwapTo(string resolvedServiceName)
{
    var next = Build(resolvedServiceName);   // (1) build #2 first — provider exists before #1 dies
    var prior = _current;
    _current = next;                          // (2) repoint BEFORE disposing prior
    prior.ForceFlush(milliseconds: 5000);     // (3) push the placeholder window's in-flight batch
    prior.Dispose();                          // (4) flush+shutdown reader, release OTLP gRPC channel
}
public void Dispose() => _current?.Dispose();
```

**`service.instance.id` preservation (Pitfall 4 / Phase 30 D-10):** the holder MUST capture the **already-resolved** instance id (ctor param) and reuse it across both providers — NEVER re-run the `POD_NAME ?? HOSTNAME ?? MachineName ?? GUID` precedence (that adds a 4th IN-03 drift site, RESEARCH lines 194-195). The resource build mirrors the shared `instanceAttrs` shape from `BaseConsoleObservabilityExtensions.cs:47`:
```csharp
.AddAttributes(new[] { new KeyValuePair<string, object>("service.instance.id", _instanceId) })
```
**OPEN FORK for planner (RESEARCH Open Q1 / A1-vs-A2):** A1 = holder owns only provider #2, disposes the host's #1 at swap (zero shared-path diff, relies on idempotent `MeterProvider.Dispose`); A2 = holder owns BOTH, one removal line in `AddBaseProcessor` mirroring the `StartupCompletionService` removal idiom (`BaseProcessorServiceCollectionExtensions.cs:136-141`). Both correct; planner resolves.

---

### NEW `tests/BaseApi.Tests/Observability/MeterProviderHolderFacts.cs` (hermetic) — highest-value new test

**Analog:** `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs`

**Why:** it is the established pattern for a **hermetic** test that *builds a real OTel provider, drives it, and inspects the result in-memory* — no compose stack. `ProcessorIdEnricherTests` builds an OTel logger provider with a capturing `BaseProcessor<LogRecord>` and asserts on attributes; the holder test builds a `MeterProvider` and asserts on the **resource** `service.name` + `service.instance.id`.

**Scaffolding pattern to mirror** — `ProcessorIdEnricherTests.cs:61-74` (build-and-inspect):
```csharp
private static CapturingProcessor EmitOneLog(IProcessorContext context)
{
    var capture = new CapturingProcessor();
    using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
    {
        o.AddProcessor(new ProcessorIdLogEnricher(context));   // SUT
        o.AddProcessor(capture);                               // inspect downstream
    }));
    ...
}
```
**Holder test shape (RESEARCH lines 366-367):** construct holder with known instanceId + placeholder name `processor-sample_3.5.0`; assert provider #1 resource `service.name == "processor-sample_3.5.0"` + the captured instance id; call `SwapTo("db-name_9.9.9")`; assert new provider resource `service.name == "db-name_9.9.9"` and the **SAME** instance id; assert #1 was disposed (no leak). Note: resource-inspection on a built `MeterProvider` has **no existing analog** in the test tree (grep for `CreateMeterProviderBuilder|GetResource` → none) — the assertion will need either a test exporter or reflection over the provider's resource; the planner must pick the inspection seam.

---

### NEW contract/responder test for `ProcessorIdentityFound` Name/Version

**Analog:** `tests/BaseApi.Tests/Messaging/ProcessorResponderTests.cs` — **EXACT** (same consumer; extend in place rather than new file is viable).

**Why:** this file already drives `GetProcessorBySourceHashConsumer` through the in-memory MassTransit harness over a real `ProcessorService` + EF-InMemory seeded row, and already asserts the Found message's Id + 3 schema Ids. Adding Name/Version assertions is a 2-line extension of the existing `SeededHash_Responds_ProcessorIdentityFound_With_Seeded_Fields` fact.

**Seed already carries Name/Version** — `ProcessorResponderTests.cs:52-61`:
```csharp
db.Processors.Add(new ProcessorEntity {
    Id = SeededId, Name = "seed", Version = "1.0.0", SourceHash = SeededHash, ... });
```
**Assertion to add** — `ProcessorResponderTests.cs:107-111`:
```csharp
Assert.True(response.Is(out Response<ProcessorIdentityFound>? found));
Assert.Equal(SeededId, found!.Message.Id);
// ADD:
Assert.Equal("seed",  found.Message.Name);
Assert.Equal("1.0.0", found.Message.Version);
```

---

### `src/Messaging.Contracts/ProcessorQueries.cs` (model) — positional record extension

**Analog:** the record itself (line 7-8). Positional `sealed record`; sibling records show the shape.
```csharp
// CURRENT (line 7-8):
public sealed record ProcessorIdentityFound(
    Guid Id, Guid? InputSchemaId, Guid? OutputSchemaId, Guid? ConfigSchemaId);
// TARGET — append positional Name + Version (RESEARCH line 231):
public sealed record ProcessorIdentityFound(
    Guid Id, Guid? InputSchemaId, Guid? OutputSchemaId, Guid? ConfigSchemaId,
    string Name, string Version);
```
Appending positional params is source-compatible only at the **two** call-sites (responder `RespondAsync` + `SetIdentity` reads) — both are in this phase's edit set.

---

### `src/BaseApi.Service/.../GetProcessorBySourceHashConsumer.cs` (responder)

**Analog:** itself (line 25-26). `ProcessorReadDto` already exposes `Name`/`Version` (RESEARCH A1 — VERIFIED, DTO lines 44-45).
```csharp
// CURRENT (line 25-26):
await context.RespondAsync<ProcessorIdentityFound>(
    new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId));
// TARGET:
await context.RespondAsync<ProcessorIdentityFound>(
    new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId, p.Name, p.Version));
```

---

### `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs` (model)

**Analog:** itself — mirror the existing schema-Id property + `SetIdentity` storage idiom exactly.

**Interface** — add two members alongside the schema-Id props (`IProcessorContext.cs:36-45`):
```csharp
/// <summary>The resolved processor Name (DB single source of truth; null until Loop A completes). WR-03: read after IsHealthy.</summary>
string? Name { get; }
/// <summary>The resolved processor Version (DB single source of truth; null until Loop A completes). WR-03: read after IsHealthy.</summary>
string? Version { get; }
```
**Impl** — plain auto-props + `SetIdentity` storage (`ProcessorContext.cs:33-42, 57-63`):
```csharp
public string? Name { get; private set; }          // mirrors Id/InputSchemaId — plain auto-prop, NO volatile (WR-03)
public string? Version { get; private set; }
// inside SetIdentity (line 57-63), append:
Name = identity.Name;
Version = identity.Version;
```
**WR-03 invariant carries over verbatim** (`IProcessorContext.cs:22-31`): Name/Version are plain auto-props with no barrier; safe to read cross-thread only after observing `IsHealthy == true`. Update the XML-doc invariant list to include `Name`/`Version`.

---

### `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (BackgroundService) — swap trigger

**Analog:** itself — the Loop A `SetIdentity` block (line 79-85). Inject `MeterProviderHolder` as a new primary-ctor param (line 50-60 pattern) and fire `SwapTo` immediately after `SetIdentity`, before `break` (RESEARCH lines 168-182):
```csharp
if (resp.Is(out Response<ProcessorIdentityFound>? found))
{
    context.SetIdentity(found!.Message);
    meterProviderHolder.SwapTo($"{found.Message.Name}_{found.Message.Version}");  // ← swap HERE (pre-bind, pre-MarkHealthy)
    logger.LogInformation("Identity resolved for hash {Hash}: processor {ProcessorId}", hash, found.Message.Id);
    break;
}
```
**Race-safety (RESEARCH lines 184-189):** the swap is synchronous inside Loop A, BEFORE Loop B / the `{Id:D}` queue-bind / `MarkHealthy` (line 145-160). The only post-Healthy metric writers are the dispatch counters, which can't fire until the queue is bound (post-swap). `ProcessorLivenessHeartbeat` writes Redis-only (no metric). Correct ordering preserved.

---

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (composition root)

**Analog:** itself — the singleton + meter-provider registrations (line 99, 121-122) and the `StartupCompletionService` removal idiom (line 136-141, needed only for A2).

**Singleton registration to mirror** (line 99 / 121):
```csharp
services.AddSingleton<IProcessorContext, ProcessorContext>();   // line 99
services.AddSingleton<ProcessorMetrics>();                       // line 121 — concrete singleton, same shape as the holder
// ADD this phase:
services.AddSingleton<MeterProviderHolder>();                    // + register as IHostedService if A2 owns provider #1 at StartAsync
```
**A2-only host-provider-removal idiom to mirror** (line 136-141 — already used for `StartupCompletionService`):
```csharp
foreach (var d in services.Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService)).ToList())
    services.Remove(d);
```
The holder needs the resolved `service.instance.id` + appsettings name/version + the meter-name set. RESEARCH prefers surfacing the once-resolved id (capture, don't re-resolve). Note `cfg.Require("Service:Name")` / `cfg.Require("Service:Version")` (the placeholder seed) is already read in `AddBaseConsoleObservability` — pass these into the holder, do NOT add a second `ResolveInstanceId` copy.

---

### `src/Processor.Sample/Program.cs` (thin-shell composition root)

**Analog:** itself — the 3-call thin-shell order (line 13-20). The holder registration lives inside `AddBaseProcessor` (above), so this file is touched only if A2 needs the holder wired as an `IHostedService` that must build provider #1 before the host metrics provider. Preserve the existing call order:
```csharp
builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel (placeholder resource = provider #1 source)
builder.Services.AddBaseProcessor(builder.Configuration);     // identity + liveness + dispatch + heartbeat (+ holder)
builder.Services.AddSingleton<BaseProcessorBase, SampleProcessor>();
```

---

## Shared Patterns

### The `{name}_{version}` combine (MLBL-01) — metrics resource ONLY
**Source:** `BaseConsoleObservabilityExtensions.cs:64-67` and `ObservabilityServiceCollectionExtensions.cs:69-72`
**Apply to:** both base-lib metrics `ConfigureResource` blocks (the singletons). NEVER the logs `SetResourceBuilder` block (MLBL-04 / Pitfall 5).
```csharp
// CURRENT (metrics block — BaseConsole line 65-67 / BaseApi line 70-72):
.ConfigureResource(r => r
    .AddService(serviceName: serviceName, serviceVersion: serviceVersion)   // bare name
    .AddAttributes(instanceAttrs))
// TARGET (D-01 + D-07):
.ConfigureResource(r => r
    .AddService(serviceName: $"{serviceName}_{serviceVersion}", serviceVersion: serviceVersion)  // combined name; service.version kept
    .AddAttributes(instanceAttrs))
```
**LOGS block stays bare — DO NOT TOUCH** (`BaseConsole.cs:56-58` / `BaseApi.cs:61-63`): the logs `SetResourceBuilder(...AddService(serviceName, serviceVersion)...)` is textually separate; editing only the metrics line keeps logs `service.name` bare (MLBL-04, protects Phase-35 ES `service.name="keeper"`).

### `service.instance.id` single-resolve invariant (Phase 30 D-10, IN-03 drift guard)
**Source:** `BaseConsoleObservabilityExtensions.cs:41-47, 91-95` (+ BaseApi twin + `ResolveInstanceIdFacts.cs`)
**Apply to:** the holder MUST reuse the captured id, NOT re-implement the precedence (would be a 4th drift copy). The 3 existing copies move in lock-step:
```csharp
private static string ResolveInstanceId() =>
    Environment.GetEnvironmentVariable("POD_NAME")
    ?? Environment.GetEnvironmentVariable("HOSTNAME")
    ?? Environment.MachineName
    ?? Guid.NewGuid().ToString("N");
```

### `IProcessorContext` fakes must add Name/Version (Pitfall 6 / CS0535)
**Source:** `FakeProcessorContext.cs:18-29` (settable-props shape)
**Apply to:** ALL three implementers (grep `: IProcessorContext` → exactly these, no production callers beyond `ProcessorContext`):
| File | Member style to add |
|------|---------------------|
| `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs:41` (`StubContext`) | `public string? Name { get; set; }` + `Version` (settable, like its `Id`) |
| `tests/BaseApi.Tests/Processor/FakeProcessorContext.cs:18` (`FakeProcessorContext`) | `public string? Name { get; set; }` + `Version` (settable) |
| `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs:72` (`RecordingContext`) | `public string? Name => _inner.Name;` + `Version` (delegate to `_inner`) |
```csharp
// FakeProcessorContext settable-props analog (line 18-24):
public Guid? Id { get; set; } = Guid.NewGuid();
public Guid? InputSchemaId { get; set; }
// add: public string? Name { get; set; }   public string? Version { get; set; }

// RecordingContext delegate-to-inner analog (line 76-83):
public Guid? Id => _inner.Id;
// add: public string? Name => _inner.Name;   public string? Version => _inner.Version;
```

### PromQL literal update (MLBL-05 / D-08) — exact literal, NOT regex
**Source:** `MetricsExportTests.cs:49, 97, 99` + `SchemasMetricsE2ETests.cs:102` (+ doc comments 16, 85, 111)
**Apply to:** swap bare `service_name="sk-api"` → `service_name="sk-api_3.2.0"` (4 query literals total). Per-console literals:

| Console | Combined `service_name` literal |
|---------|----------------------------------|
| sk-api | `sk-api_3.2.0` |
| orchestrator | `orchestrator_3.4.0` |
| keeper | `keeper_3.7.0` |
| processor-sample (boot placeholder) | `processor-sample_3.5.0` |
| processor-sample (steady state) | `{db.Name}_{db.Version}` (dynamic — read the seeded row, RESEARCH line 239) |

```csharp
// MetricsExportTests.cs:49 — example site:
const string query = """http_server_request_duration_seconds_count{service_name="sk-api",http_route="test-obs/ok"}""";
// TARGET:
const string query = """http_server_request_duration_seconds_count{service_name="sk-api_3.2.0",http_route="test-obs/ok"}""";
```
`PrometheusTestClient.cs` lines 26-29 are a **doc-comment only** (no query) — reconcile the narrative to mention `{name}_{version}`; cosmetic.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| (none for the file as a whole) | — | — | — |
| **Partial gap:** `MeterProviderHolder.SwapTo` body | provider | event-driven | No existing type owns a `Sdk.CreateMeterProviderBuilder()` provider — the swap sequence is copied from RESEARCH §"The holder type" (lines 117-166), NOT from a repo file. Registration/threading/disposal idioms ARE analoged (above). |
| **Partial gap:** built-`MeterProvider` **resource inspection** in `MeterProviderHolderFacts` | test | request-response | `grep CreateMeterProviderBuilder\|GetResource` over `tests/` → zero hits. No existing test reads a metrics provider's resource; planner must choose the inspection seam (in-memory test exporter vs reflection). The build-and-inspect *scaffold* is analoged to `ProcessorIdEnricherTests`. |

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/{Observability,Identity,Startup,DependencyInjection}`, `src/{BaseConsole.Core,BaseApi.Core}/DependencyInjection`, `src/Messaging.Contracts`, `src/BaseApi.Service/Features/Processor/Responders`, `src/Processor.Sample`, `tests/BaseApi.Tests/{Observability,Messaging,Processor}`, all `src/*/appsettings.json`.
**Files scanned (read):** 16 source + test files; 1 multi-console appsettings grep; 2 implementer/literal greps.
**Per-console versions confirmed (live appsettings):** sk-api 3.2.0, orchestrator 3.4.0, keeper 3.7.0, processor-sample 3.5.0.
**Pattern extraction date:** 2026-06-06

## PATTERN MAPPING COMPLETE
