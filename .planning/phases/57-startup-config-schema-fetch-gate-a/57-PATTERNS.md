# Phase 57: Startup Config-Schema Fetch + Gate A - Pattern Map

**Mapped:** 2026-06-12
**Files analyzed:** 11 (4 NEW, 4 MODIFIED, 3 TEST)
**Analogs found:** 11 / 11

All file paths below are absolute. Every excerpt is verbatim from read source with file path + line numbers so the planner/executor can mirror exactly.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` **(NEW)** | utility (stateless checker) | transform (schema-text + Type → verdict) | `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` (Gate B) | role-match (reverse-direction check; shares `JsonSchema.FromText` parse) |
| `src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenException.cs` **(NEW)** | model (domain exception) | event-driven (throw → handler) | `src/BaseApi.Core/Exceptions/NotFoundException.cs` | exact (domain Exception carrying a safe Guid) |
| `src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenExceptionHandler.cs` **(NEW)** | middleware (IExceptionHandler) | request-response (HTTP 409 + RFC-7807) | `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationExceptionHandler.cs` | exact (same `IExceptionHandler` shape; status 422→409) |
| `tests/BaseApi.Tests/Processor/ConfigSchemaCoverageFacts.cs` **(NEW)** | test (pure unit, table-driven) | transform | `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` (xUnit v3 idiom) | role-match (pure unit, no harness) |
| `tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs` **(NEW)** | test (integration, WebApplicationFactory) | request-response | (no in-repo WAF analog read this pass — see No Analog Found) | partial |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` **(MODIFIED)** | service (BackgroundService) | event-driven (startup loop) | itself (in-place edit at :125, :164-185) | exact (self) |
| `src/BaseProcessor.Core/Identity/ProcessorContext.cs` **(MODIFIED)** | model (mutable singleton) | CRUD (in-memory state) | itself (`SetDefinition` :74-80) | exact (self) |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` **(MODIFIED)** | model (interface) | — | itself (add `ConfigDefinition` getter, :54-58) | exact (self) |
| `src/BaseApi.Service/Features/Schema/SchemaService.cs` **(MODIFIED)** | service | CRUD (override UpdateAsync) | `src/BaseApi.Service/Features/Processor/ProcessorService.cs` (cross-entity `DbContext.Set<T>()` query) | role-match |
| `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` **(MODIFIED — invert)** | test (harness) | event-driven | itself (`:145` assertion flip) | exact (self) |
| `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` **(MODIFIED — extend)** | test (harness) | event-driven | itself (add clash + null-skip cases) | exact (self) |

---

## Pattern Assignments

### `src/BaseProcessor.Core/Configuration/ConfigSchemaCoverageCheck.cs` (NEW — utility, transform)

**Analog:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` — the *reverse-direction* sibling. Gate B is `payload ⊨ schema` (instance vs schema, `Evaluate`); Gate A is `schema ⊨ TConfig` (schema vs CLR type, structural walk). They **share the `JsonSchema.FromText` parse** and the `using Json.Schema;` import; Gate A does **not** call `Evaluate`.

**Parse pattern to mirror** (`PayloadConfigSchemaValidator.cs:1-3, 53-62`):
```csharp
using System.Text.Json;
using Json.Schema;
// ...
try
{
    schema = JsonSchema.FromText(schemaDto.Definition);   // <-- the exact parse Gate A reuses
}
catch (Exception ex) when (ex is JsonException or JsonSchemaException)
{
    // Gate B translates a bad-parse to 422; Gate A treats an unparseable config-schema as its own
    // terminal clash (CFG-06) — return Covered=false with a ClashDetail rather than throwing.
}
```

**Null-is-skip convention to mirror** (`PayloadConfigSchemaValidator.cs:41-42`):
```csharp
var cfgId = proc.ConfigSchemaId;
if (cfgId is null) continue;   // null ConfigSchemaId passes — no schema to validate against.
```
→ Gate A: `if (configDefinition is null) return Covered;` (CFG-07; matches the orchestrator's existing null-skip at `ProcessorStartupOrchestrator.cs:127-128`).

**SSRF-cctor dependency note** (`JsonSchemaConfig.cs:16-18` doc + `PayloadConfigSchemaValidator.cs:82-84`): a pure structural walk does **not** call `Evaluate`, so it does not strictly need `JsonSchemaConfig.DefaultOptions`. If the walk ever resolves `$ref` subschemas, touch a `JsonSchemaConfig` member first to fire the SSRF cctor (RESEARCH Pitfall 3). The `JsonSchema.Net` keyword accessors (`GetProperties()`/`GetType()`/`GetEnum()`/`GetItems()`) are the structural-walk API (RESEARCH Pattern 3).

**Binding contract to model — the cached options instance** (`ProcessorConfig.cs:18-22`):
```csharp
public static readonly JsonSerializerOptions SerializerOptions = new()
{
    PropertyNameCaseInsensitive = true,   // D-05
    // default unknown-member handling = ignore (do NOT set JsonUnmappedMemberHandling.Disallow) — D-05
};
```
→ Name mapping uses `[JsonPropertyName] ?? p.Name` with `StringComparer.OrdinalIgnoreCase`; no camelCase policy (RESEARCH Pattern 4 + Pitfall 2). Gate A returns a structured `(bool Covered, string? ClashDetail)`. The clash rule table is RESEARCH §"STJ Type-Clash Rule Table" (rows #13/#5/#22 require the Wave-0 `Deserialize` spike before locking).

**Recommended placement:** `internal` stateless static/class in `BaseProcessor.Core` (Configuration namespace alongside `ProcessorConfig`). It is process-internal logic, not a DI seam.

---

### `src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenException.cs` (NEW — model, event-driven)

**Analog:** `src/BaseApi.Core/Exceptions/NotFoundException.cs` — a sealed domain `Exception` carrying only a safe client-visible Guid, mapped to an HTTP status by a dedicated handler. Mirror this shape exactly (swap 404 framing for 409).

**Full analog to mirror** (`NotFoundException.cs:19-38`):
```csharp
public sealed class NotFoundException : Exception
{
    public string ResourceType { get; }
    public object Id { get; }

    public NotFoundException(string resourceType, object id)
        : base($"{resourceType} with id '{id}' was not found.")
    {
        ResourceType = resourceType;
        Id = id;
    }
}
```
→ New: `public sealed class SchemaDefinitionFrozenException(Guid schemaId) : Exception(...)` exposing `Guid SchemaId`. Carry only the Guid (information-disclosure guard, same as `NotFoundException` doc IN-02). Namespace `BaseApi.Service.Features.Schema`.

---

### `src/BaseApi.Service/Features/Schema/SchemaDefinitionFrozenExceptionHandler.cs` (NEW — middleware, request-response)

**Analog:** `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationExceptionHandler.cs` — the canonical domain `IExceptionHandler`. Copy structure verbatim, swap the exception type + status (422 → 409). The `DbUpdateExceptionHandler` is the model for the 409 status specifically.

**Full analog to mirror** (`OrchestrationValidationExceptionHandler.cs:25-56`):
```csharp
public sealed class OrchestrationValidationExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;
    public OrchestrationValidationExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not OrchestrationValidationException ex) return false;  // Pitfall 6: bail FAST

        httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = ex.Title,
            Detail = ex.Message,
            // ... Extensions ...
        };
        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
```

**409-status framing** (`DbUpdateExceptionHandler.cs:50-57`):
```csharp
httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
var concurrencyProblem = new ProblemDetails
{
    Status = StatusCodes.Status409Conflict,
    Title = "Conflict",
    Detail = "The resource was modified by another request; reload and retry.",
};
```

**CRITICAL — do NOT set `correlationId` / `Instance`:** the `CustomizeProblemDetails` customizer injects both on every emission (`ErrorHandlingServiceCollectionExtensions.cs:26-34`). The handler sets only Status/Title/Detail (matches `OrchestrationValidationExceptionHandler` D-02 doc :19-23).

**Registration — load-bearing order** (`OrchestrationServiceCollectionExtensions.cs:84-88`):
```csharp
// D-04 ordering: registered here so it lands after the Core NotFound/Validation/DbUpdate
// handlers and BEFORE the split-out FallbackExceptionHandler (AddBaseApiFallbackHandler is
// called last in Program.cs, after AddAppFeatures runs this method). Reachable → emits 422.
services.AddExceptionHandler<OrchestrationValidationExceptionHandler>();
```
→ Add `services.AddExceptionHandler<SchemaDefinitionFrozenExceptionHandler>();` in the **Schema feature's** `AddXxxFeature` extension (or alongside the orchestration registration) so it lands ahead of `FallbackExceptionHandler`. Walk order == registration order; first to return `true` claims (`ErrorHandlingServiceCollectionExtensions.cs:37-43, 52-56`).

---

### `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (MODIFIED — service, event-driven)

**Analog:** itself. Three in-place edits.

**Edit 1 — Loop B third iteration (D-12, CFG-03/04).** Current code at `:124-128`:
```csharp
// --- Loop B: per-non-null definition (SCHEMA-01/02). Never read the config schema id (D-05). ---
foreach (var schemaId in new[] { context.InputSchemaId, context.OutputSchemaId })
{
    if (schemaId is not { } id)
        continue; // null schema id skipped by design — no request sent (SCHEMA-02).
```
→ Add `context.ConfigSchemaId` to the array; the existing `if (schemaId is not { } id) continue;` null-skips a null config id (CFG-07 fetch-side). Update the `// Never read the config schema id (D-05)` comment + the class XML doc at `:27-31` (the D-05 carve-out is now lifted). The unchanged retry body (`SchemaDefinitionNotFound`/`RequestTimeoutException` loop, `:136-157`) handles CFG-04 verbatim.

**Edit 2 — insert Gate A after Loop B, BEFORE the bind (D-13, CFG-05/06).** Current completion block at `:164-184`:
```csharp
// --- Completion (D-02/D-03): identity + all required non-null definitions resolved. ---
var queueName = $"{context.Id!.Value:D}";
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);
});
await handle.Ready;
context.MarkHealthy(); // NOW IsHealthy opens
gate.MarkReady();      // flip the startup gate HERE, not at host-start.
```
→ Insert the Gate A call before `var queueName`. On clash: single `logger.LogError` (D-10), `gate.MarkReady()`, `return;` (terminal, no retry — D-11). On pass/skip: fall through to the existing bind + `MarkHealthy()` + `MarkReady()`.

**Edit 3 — decouple `MarkReady` from `MarkHealthy` (D-09, Pitfall 1).** Today they fire together at `:181-182`. After the edit: `gate.MarkReady()` fires on ALL THREE paths (pass / clash / skip); `context.MarkHealthy()` + the bind fire ONLY on pass/skip. **Warning sign of the bug:** a clash path that skips both → readiness never green → K8s crash-loop.

**TConfig resolution at startup (D-01, Pitfall 4):** the orchestrator holds no `BaseProcessor` reference today. Per RESEARCH Pattern 2 note, resolve only the **Type** via `processor.GetType().BaseType!.GenericTypeArguments[0]` (do NOT capture the scoped instance — captive-dependency bug). Planning chooses the wiring (inject `IServiceProvider`/`BaseProcessor` and read `.GetType()`, or add an `IConfigTypeProvider` singleton mirroring `ISourceHashProvider`). Note the existing ctor signature at `:51-61` for the injection site.

---

### `src/BaseProcessor.Core/Identity/ProcessorContext.cs` + `IProcessorContext.cs` (MODIFIED — model)

**Analog:** itself. The `InputDefinition`/`OutputDefinition` pair is the exact template for a new `ConfigDefinition`.

**`ProcessorContext.cs` — `SetDefinition` gains a 3rd branch** (current `:74-80`):
```csharp
public void SetDefinition(Guid schemaId, string definition)
{
    if (schemaId == InputSchemaId)
        InputDefinition = definition;
    if (schemaId == OutputSchemaId)
        OutputDefinition = definition;
    // NEW: if (schemaId == ConfigSchemaId) ConfigDefinition = definition;
}
```
Note these are independent `if`s (not `else if`) — if two roles share an Id, one fetch populates both slots (RESEARCH Pattern 1 edge case; correct, idempotent). Add a `public string? ConfigDefinition { get; private set; }` auto-property mirroring `OutputDefinition` at `:51-54`.

**`IProcessorContext.cs` — add the getter** mirroring `:54-58`:
```csharp
/// <summary>The resolved output schema definition (null until Loop B resolves it).</summary>
string? OutputDefinition { get; }
// NEW: string? ConfigDefinition { get; }  (null until Loop B resolves it — D-14)
```
Also update the `ConfigSchemaId` doc at `:45` (was `never resolved to a definition — D-05`; now resolved). **D-14: do NOT add `ConfigDefinition` to the heartbeat L2 `ProcessorProjection`** — store on context only. Note the WR-03 memory-visibility doc at `:21-32` applies to the new property too (safe to read only after `IsHealthy`).

---

### `src/BaseApi.Service/Features/Schema/SchemaService.cs` (MODIFIED — service, CRUD)

**Analog (override target):** `src/BaseApi.Core/Services/BaseService.cs` `UpdateAsync` (`:106-115`). **Analog (cross-entity query):** `src/BaseApi.Service/Features/Processor/ProcessorService.cs` `:77-79`.

Current `SchemaService` is a bare 5-param passthrough marker (`SchemaService.cs:15-25`) — no overrides today. Add a `UpdateAsync` override (D-06/D-08).

**`BaseService.UpdateAsync` is NOT virtual today** (`BaseService.cs:106`):
```csharp
public async Task<TRead> UpdateAsync(Guid id, TUpdate dto, CancellationToken ct)
{
    await _updateValidator.ValidateAndThrowAsync(dto, ct);
    var entity = await _repo.GetAsync(id, ct);
    if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
    _mapper.Update(dto, entity);          // <-- mutates entity; freeze check needs PRE-mutation Definition
    await SyncJunctionsAsync(entity, default, dto, ct);
    await DbContext.SaveChangesAsync(ct);
    return _mapper.ToRead(entity);
}
```
→ Planning must add `virtual` (additive, no behavior change — only override is `SchemaService`; RESEARCH A4). Do NOT route through `SyncJunctionsAsync` (it runs after `_mapper.Update` mutates the entity, losing the pre-mutation Definition; RESEARCH note).

**Cross-entity referenced-query pattern to mirror** (`ProcessorService.cs:77-79`):
```csharp
var entity = await DbContext.Set<ProcessorEntity>()
    .AsNoTracking()
    .FirstOrDefaultAsync(p => p.SourceHash == sourceHash, ct);
```
→ Gate query: `await DbContext.Set<ProcessorEntity>().AsNoTracking().AnyAsync(p => p.InputSchemaId == id || p.OutputSchemaId == id || p.ConfigSchemaId == id, ct)`. This keeps `IRepository<T>` at its locked 5 methods (RESEARCH "Don't Hand-Roll"). The FK shape is confirmed: `ProcessorEntity` has exactly `InputSchemaId`/`OutputSchemaId`/`ConfigSchemaId` nullable Guids (`ProcessorEntity.cs:27-29`); `AssignmentEntity` has no direct schema FK (RESEARCH A5/Pitfall 6).

**Freeze logic (D-06/D-07, Pitfall 5):** throw `SchemaDefinitionFrozenException(id)` ONLY when the incoming `dto.Definition` differs from the persisted `Definition` (`StringComparison.Ordinal`) AND the schema is referenced. `Name`/`Description`-only edits and unchanged-`Definition` edits flow through to `base.UpdateAsync` (D-07). DTO shape: `SchemaUpdateDto(Name, Version, Description, Definition)` (`SchemaDtos.cs:21-25`).

**Why NOT the DTO validator:** "is referenced" is a cross-entity DB precondition; FluentValidation validators are constructed without DbContext (RESEARCH anti-pattern + Pitfall — use the service override).

---

### `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` (MODIFIED — invert, CFG-03/04)

**Analog:** itself. The `LoopB_Resolves_Input_And_Output_Never_Config` fact currently asserts config is NEVER queried (`:145`):
```csharp
Assert.Contains(inputId, queried);
Assert.Contains(outputId, queried);
Assert.DoesNotContain(configId, queried);   // <-- INVERT to Assert.Contains (D-12); also assert ConfigDefinition set
```
→ Flip `:145` to `Assert.Contains(configId, queried)` and add `Assert.Equal($"def-for-{configId:N}", context.ConfigDefinition)` (rename the fact to drop "Never_Config"). The `CapturingSchemaResponder` + `SchemaCapture.NextIsNotFound` (`:38-58`) already prove CFG-04 transient retry per-Id — reuse for the config Id. Build harness (`:60-79`) and driver (`:81-115`) are reused verbatim.

---

### `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` (MODIFIED — extend, CFG-06/07)

**Analog:** itself. The ordered-log seam is the exact recording mechanism for Gate A's pass/clash/skip observability.

**Pass-path ordered log to extend** (`:103`):
```csharp
Assert.Equal(new[] { "connect", "ready", "markhealthy" }, log);
Assert.NotNull(connector.BoundQueueName);
```
**Clash case to add:** assert `log == ["ready"]` only (NO "connect"/"markhealthy"), `connector.BoundQueueName is null`, `!context.IsHealthy`, and one Error log (add a capturing `ILogger` in place of `NullLogger` at `:165`). **Null-skip case to add:** config id null → reaches `["connect","ready","markhealthy"]` + bound (CFG-07).

The `RecordingConnector` (`:35-48`, exposes `BoundQueueName`), `RecordingHandle` (`:54-65`), and `RecordingContext` (`:72-95`) are the recording fakes to reuse. The `RecordingContext` already proxies `ConfigSchemaId`/`ConfigDefinition` via `_inner` — extend `SetDefinition` proxy if the new context member needs surfacing. Driver `DriveOrchestratorToHealthy` (`:122-179`) sets `SchemaNotFoundCount = 0` and null schema Ids — parametrize to inject a config def that clashes vs covers.

---

### `tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs` (NEW — integration, CFG-10)

**Closest analog:** structurally the WebAPI 409 round-trip; no WebApplicationFactory fixture was read this pass (see No Analog Found). For the *assertion contract*, mirror the handler output: HTTP 409 + RFC-7807 body + `X-Correlation-Id` echo (the customizer at `ErrorHandlingServiceCollectionExtensions.cs:31-33`). Cases (RESEARCH validation map): frozen `Definition` mutation → 409; `Name`/`Description` edit on a referenced schema → 200; unreferenced-draft `Definition` edit → 200. This fact RECORDS the chosen TOCTOU mechanism (ROADMAP SC-5).

---

## Shared Patterns

### Domain exception → IExceptionHandler → RFC-7807
**Source:** `src/BaseApi.Service/Features/Orchestration/OrchestrationValidationExceptionHandler.cs:25-56` (handler shape), `src/BaseApi.Core/Exceptions/Handlers/DbUpdateExceptionHandler.cs:50-57` (409 framing), `src/BaseApi.Core/Exceptions/NotFoundException.cs:19-38` (exception shape).
**Apply to:** the new `SchemaDefinitionFrozenException` + handler.
**Invariant:** fast-bail `if (ex is not T) return false;`; set only Status/Title/Detail; never set `correlationId`/`Instance` (the customizer owns them — `ErrorHandlingServiceCollectionExtensions.cs:26-34`).

### Handler registration order (walk == registration)
**Source:** `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs:37-56`, `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs:84-88`.
**Apply to:** registering `SchemaDefinitionFrozenExceptionHandler` — must register inside an `AddXxxFeature` (runs via `AddAppFeatures`) so it lands AFTER core handlers and BEFORE the LAST-registered `FallbackExceptionHandler`.

### Cross-entity read without growing IRepository
**Source:** `src/BaseApi.Service/Features/Processor/ProcessorService.cs:77-79` (`DbContext.Set<T>().AsNoTracking()`).
**Apply to:** the `SchemaService.UpdateAsync` referenced-query. `IRepository<T>` stays at 5 methods.

### JsonSchema.Net parse + SSRF-locked options
**Source:** `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs:55,84`, `src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs:22-34`.
**Apply to:** Gate A's `JsonSchema.FromText` (shared parse). Gate A is structural-walk only (no `Evaluate`); touch a `JsonSchemaConfig` member only if it later resolves `$ref` (Pitfall 3).

### Config-deserialization contract (the binding model Gate A gates against)
**Source:** `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs:18-22` — the single cached `SerializerOptions` (case-insensitive, ignore-unknown, no naming policy, no `NumberHandling`).
**Apply to:** Gate A name-mapping + the STJ clash rule table. Model THIS instance, not generic STJ (Pitfall 2).

### Null-is-skip gate convention (CFG-07)
**Source:** `PayloadConfigSchemaValidator.cs:42` (Gate B), `ProcessorStartupOrchestrator.cs:127-128` (Loop B).
**Apply to:** Gate A (`ConfigDefinition is null` → Covered) and the Loop B fetch (null `ConfigSchemaId` → no request).

### xUnit v3 + harness/recording test idioms
**Source:** `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` (capturing responder + `FakeTimeProvider` + `TestContext.Current.CancellationToken`), `tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs` (ordered-log recording fakes + `IdentityResolutionFacts.AdvanceUntilAsync`).
**Apply to:** the new `ConfigSchemaCoverageFacts` (pure-unit, table-driven — no harness needed) and the harness extensions. The A1/A2/A3 rule-table rows MUST drive the REAL `ProcessorConfig.SerializerOptions` through a `Deserialize` spike (RESEARCH Wave-0).

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `tests/BaseApi.Tests/Features/Schema/SchemaDefinitionFreezeFacts.cs` | test (integration) | request-response | No WebApplicationFactory-based integration fact was read this pass. The handler-output contract (409 + RFC-7807 + correlation echo) is fully specified by the shared-pattern sources above; planning should locate the existing integration-test fixture (the suite is hermetic, `--filter-not-trait "Category=RealStack"`) and mirror it. If none exists, this is the first WAF integration fact for the Schema feature. |

The Gate A covers-checker (`ConfigSchemaCoverageCheck`) has NO exact analog (it is the only `schema ⊨ TConfig` direction in the repo) but has a strong **partial** analog in `PayloadConfigSchemaValidator` for the parse + null-skip; the novel logic is the STJ clash rule table (RESEARCH §"STJ Type-Clash Rule Table"), which planning must lock via the Wave-0 spike.

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/{Startup,Identity,Configuration}`, `src/BaseApi.Service/Features/{Schema,Orchestration,Processor}`, `src/BaseApi.Core/{Services,Exceptions,DependencyInjection}`, `tests/BaseApi.Tests/Processor`.
**Files read (analogs):** `ProcessorStartupOrchestrator.cs`, `ProcessorContext.cs`, `IProcessorContext.cs`, `PayloadConfigSchemaValidator.cs`, `JsonSchemaConfig.cs`, `ProcessorConfig.cs`, `OrchestrationValidationExceptionHandler.cs`, `OrchestrationValidationException.cs`, `DbUpdateExceptionHandler.cs`, `NotFoundException.cs`, `BaseService.cs`, `SchemaService.cs`, `ProcessorService.cs`, `ProcessorEntity.cs`, `SchemaDtos.cs`, `ErrorHandlingServiceCollectionExtensions.cs`, `OrchestrationServiceCollectionExtensions.cs`, `SchemaResolutionFacts.cs`, `DispatchBindSequenceFacts.cs`.
**Pattern extraction date:** 2026-06-12
