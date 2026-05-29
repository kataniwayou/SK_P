# Phase 13: OrchestrationService split + L3 fetch + L1 build - Pattern Map

**Mapped:** 2026-05-29
**Files analyzed:** 12 (8 created, 2 modified, 2 test-file targets)
**Analogs found:** 12 / 12 (all in-repo; zero "no analog")

> Every file path and symbol below was confirmed against the live codebase in this session. The one item that does NOT yet exist (and the planner MUST create) is the `[assembly: InternalsVisibleTo("BaseApi.Tests")]` attribute — see **Shared Patterns → InternalsVisibleTo gap**.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `Features/Orchestration/Loading/IWorkflowGraphLoader.cs` (NEW) | service interface | request-response | `src/BaseApi.Core/Mapping/IEntityMapper.cs` (interface shape) | role-match |
| `Features/Orchestration/Loading/WorkflowGraphLoader.cs` (NEW) | service (loader) | batch read + transform | `OrchestrationService.ValidateWorkflowIdsAsync` (AsNoTracking batch) + `StepService.SyncJunctionsAsync` (junction read) | exact (composite) |
| `Features/Orchestration/WorkflowGraphSnapshot.cs` (NEW) | model (transient read-model) | in-memory state / IDisposable | `StepDtos.cs` / `WorkflowDtos.cs` positional records (DTO shape); no IDisposable precedent in repo | role-match |
| `Features/Orchestration/Validation/CycleDetector.cs` (NEW) | validator (no-op stub) | transform (sync, in-memory) | Phase 9 D-05 pre-injected mapper fields (pre-wire-before-use) | role-match |
| `Features/Orchestration/Validation/SchemaEdgeValidator.cs` (NEW) | validator (no-op stub) | transform (sync, in-memory) | same as above | role-match |
| `Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` (NEW) | validator (no-op stub) | transform (sync, in-memory) | same as above | role-match |
| `Features/Orchestration/Projection/IRedisProjectionWriter.cs` (NEW) | projection interface (no-op) | event-driven / write (deferred) | Phase 12 Redis pre-wiring (`12-CONTEXT` D-14/15/16) | role-match |
| `Features/Orchestration/Projection/RedisProjectionWriter.cs` (NEW) | projection impl (no-op) | event-driven / write (deferred) | same as above | role-match |
| `Features/Orchestration/OrchestrationService.cs` (MODIFIED) | orchestrator service | request-response | current `OrchestrationService` (self) | exact |
| `Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` (MODIFIED) | config (DI extension) | request-response | current `AddOrchestrationFeature` (self) | exact |
| `tests/.../Orchestration/WorkflowGraphLoaderFacts.cs` (NEW) | test (integration) | request-response | `StartOrchestrationFacts.cs` + `Phase8WebAppFactory` | exact |
| `tests/.../Orchestration/StartCleanupFacts.cs` (NEW, or folded) | test (integration) | request-response | `StartOrchestrationFacts.cs` + `RecordingTestService.cs` (recording-double precedent) | role-match |

---

## Pattern Assignments

### `Features/Orchestration/Loading/WorkflowGraphLoader.cs` (service, batch read + transform) — THE only real seam

**Analogs (composite):**
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` lines 115-119 (AsNoTracking batch read)
- `src/BaseApi.Service/Features/Step/StepService.cs` lines 50-74 (junction read via `DbContext.Set<StepNextSteps>()`)
- `src/BaseApi.Service/Features/{Step,Workflow}/*EntityMapper.cs` (`ToRead` usage)

**Imports / ctor pattern — relocate the 5 mappers here (D-05).** Copy the field + null-guard shape verbatim from the CURRENT `OrchestrationService` lines 48-73, but DROP the 5 `[SuppressMessage("Style","IDE0052")]` attributes (the mappers are now USED, so the smell is gone). The loader ctor additionally needs `BaseDbContext` and (for the D-04 log line) an `ILogger<WorkflowGraphLoader>`:

```csharp
// Relocated from OrchestrationService — now ACTUALLY USED, so no [SuppressMessage].
private readonly BaseDbContext _db;
private readonly IEntityMapper<SchemaEntity,     SchemaCreateDto,     SchemaUpdateDto,     SchemaReadDto>     _schemaMapper;
private readonly IEntityMapper<ProcessorEntity,  ProcessorCreateDto,  ProcessorUpdateDto,  ProcessorReadDto>  _processorMapper;
private readonly IEntityMapper<StepEntity,       StepCreateDto,       StepUpdateDto,       StepReadDto>       _stepMapper;
private readonly IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto> _assignmentMapper;
private readonly IEntityMapper<WorkflowEntity,   WorkflowCreateDto,   WorkflowUpdateDto,   WorkflowReadDto>   _workflowMapper;
// null-guard each in ctor exactly as OrchestrationService lines 67-73 do.
```

**Batch-read pattern (L1-BUILD-02) — copy from `OrchestrationService.ValidateWorkflowIdsAsync` lines 115-119.** Verified live; Npgsql translates `ids.Contains` to `= ANY(@ids)`:

```csharp
var workflows = await _db.Set<WorkflowEntity>()
    .AsNoTracking()
    .Where(w => ids.Contains(w.Id))
    .ToListAsync(ct);
```

Repeat this exact shape for `Set<StepEntity>()`, `Set<ProcessorEntity>()`, `Set<SchemaEntity>()`, `Set<AssignmentEntity>()`. ALL reads use `Set<>()` directly — NOT `Repository<TEntity>` (L1-BUILD-02 locked). `BaseDbContext.Set<T>()` is inherited from `DbContext` and `AppDbContext` exposes all 5 entity + 3 junction DbSets (verified `AppDbContext.cs` lines 27-36).

**Junction read pattern (D-06, L1-BUILD-04) — derived from `StepService.SyncJunctionsAsync` lines 50-74.** The junction set is reached via `_db.Set<StepNextSteps>()`. Children of step `S` are rows where `StepId == S` (PK is `(StepId, NextStepId)`, verified `StepNextSteps.cs` lines 17-18):

```csharp
var nextRows = await _db.Set<StepNextSteps>()
    .AsNoTracking()
    .Where(j => stepIds.Contains(j.StepId))
    .ToListAsync(ct);
var nextStepLookup = nextRows
    .GroupBy(j => j.StepId)
    .ToDictionary(g => g.Key, g => g.Select(j => j.NextStepId).ToList());
```

Same shape for `WorkflowEntrySteps` (`WorkflowId, StepId` — entry steps) and `WorkflowAssignments` (`WorkflowId, AssignmentId` — assignments). Junction field names verified: `StepNextSteps {StepId, NextStepId}`, `WorkflowEntrySteps {WorkflowId, StepId}`, `WorkflowAssignments {WorkflowId, AssignmentId}`.

**CRITICAL data-model fact (Pitfall 1, VERIFIED):** `AssignmentEntity` has NO `WorkflowId`. `AssignmentReadDto` is `(Id, Name, Version, Description, StepId, Payload, +4 audit)` — only `StepId` + `Payload` (verified `AssignmentDtos.cs` lines 38-48). Assignments are reachable ONLY via `WorkflowAssignments.Where(j => workflowIds.Contains(j.WorkflowId)).Select(j => j.AssignmentId)`, then batch-load `AssignmentEntity` by those ids. Do NOT look for `Step.AssignmentId` or `Assignment.WorkflowId` — neither exists.

**Mapperly ToRead → enrich-via-`with {}` (D-06).** `ToRead` returns null junction collections (proven below). Enrich ONLY Step (`NextStepIds`) and Workflow (`EntryStepIds`, `AssignmentIds`). Schema / Processor / Assignment need NO enrichment:

```csharp
var dto = _stepMapper.ToRead(stepEntity);          // NextStepIds == null here (by [MapValue])
var children = nextStepLookup.GetValueOrDefault(stepEntity.Id) ?? new List<Guid>();
steps[stepEntity.Id] = dto with { NextStepIds = children };   // immutable positional-record rebuild
```

Positional-record rebuild targets (verified):
- `StepReadDto` (StepDtos.cs:45-56): `with { NextStepIds = children }`
- `WorkflowReadDto` (WorkflowDtos.cs:57-68): `with { EntryStepIds = entrySteps, AssignmentIds = assignmentIds }`

**Traversal (L1-BUILD-04, D-03) — private iterative BFS inside `LoadL1Async`.** Lives in the loader, NOT on the snapshot (snapshot is pure data). `visited` is `List<Guid>` keyed on StepId (NOT `HashSet` — REQ explicit). Depth-wave: wave0 = all `EntryStepIds`; waveN = `NextStepIds` of waveN-1 filtered by `visited`. The `visited` guard is what makes loading TERMINATE on a cyclic graph in P13 (no cycle-rejecting validator yet). Before enqueueing a child: `if (visited.Contains(childId)) continue; else { visited.Add(childId); enqueue; }`.

**Anti-patterns (locked OUT):** No `.Include()` (no nav properties — ENTITY-09); no recursion (L1-VALIDATE-03 bans it downstream — use iterative now); no inline `.Select(e => new ReadDto(...))` (D-05 single mapping seam); no `HashSet` for visited.

---

### `Features/Orchestration/Loading/IWorkflowGraphLoader.cs` (service interface)

**Analog:** `src/BaseApi.Core/Mapping/IEntityMapper.cs` (minimal interface shape, lines 27-32).

`internal` interface (ORCH-SPLIT-02). Signature locked by L1-BUILD-02:

```csharp
internal interface IWorkflowGraphLoader
{
    Task<WorkflowGraphSnapshot> LoadL1Async(IReadOnlyList<Guid> workflowIds, CancellationToken ct);
}
```

The `IReadOnlyList<Guid>` parameter type mirrors the existing `ValidateWorkflowIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)` signature (OrchestrationService.cs:94).

---

### `Features/Orchestration/WorkflowGraphSnapshot.cs` (model, transient IDisposable record)

**Analog:** positional/`record` DTO shapes in `StepDtos.cs` / `WorkflowDtos.cs` (all `sealed record`). No existing `IDisposable` in the repo — this is the first; follow RESEARCH §Code Examples (lines 273-299) verbatim.

`internal sealed record : IDisposable` with 5 `Dictionary<Guid, EntityDto> { get; init; } = new();` members (keyed by `Id`, values = the verified ReadDto types: `WorkflowReadDto`, `AssignmentReadDto`, `StepReadDto`, `ProcessorReadDto`, `SchemaReadDto`). `Dispose()` calls `.Clear()` on each dict (mutates contents — legal on init-only refs), sets `public bool IsDisposed { get; private set; } = true`, and is idempotent (`if (IsDisposed) return;`).

**Pitfall 2 (CS8852):** do NOT null-out the dictionary references; `.Clear()` mutates contents which is legal. `IsDisposed` is a separate mutable auto-property, NOT a positional member.

**D-04 log line:** `ILogger.LogDebug("L1 snapshot disposed")` — planner's discretion whether the snapshot takes an injected `ILogger` or the loader logs around disposal. The TEST asserts `IsDisposed == true` + all dicts `Count == 0`, NOT the log line. Keep the log message data-free (no entity ids/PII — Security V7).

---

### `Features/Orchestration/Validation/{CycleDetector,SchemaEdgeValidator,PayloadConfigSchemaValidator}.cs` (no-op validator stubs)

**Analog (pattern, not file):** Phase 9 D-05 pre-injected-but-unused mapper fields (OrchestrationService.cs:48-56) — the codebase's "pre-wire the shape before the behavior" precedent. D-01 makes these the "v2" of that pattern.

Three `internal sealed class`, NO `I` prefix (D-02), synchronous `void Validate(WorkflowGraphSnapshot snapshot)` with empty body in P13:

```csharp
internal sealed class CycleDetector
{
    // Phase 14 fills this body and throws a 422-mapped exception on a detected cycle.
    public void Validate(WorkflowGraphSnapshot snapshot) { /* no-op in P13 */ }
}
```

Identical shape for `SchemaEdgeValidator` and `PayloadConfigSchemaValidator`. Do NOT introduce the 422 exception type — that is a Phase 14 concern (RESEARCH §Don't Hand-Roll).

---

### `Features/Orchestration/Projection/{IRedisProjectionWriter,RedisProjectionWriter}.cs` (no-op projection)

**Analog (pattern):** Phase 12 Redis pre-wiring (`12-CONTEXT` D-14/D-15/D-16 — Redis DI wired before any writer existed). The no-op writer takes NO Redis types in P13 (RESEARCH §Standard Stack note: keep the P13 signature dependency-free).

`internal` interface + `internal sealed` impl. Forward-compatible async signature (Phase 15 fills the body):

```csharp
internal interface IRedisProjectionWriter
{
    Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct);
}
internal sealed class RedisProjectionWriter : IRedisProjectionWriter
{
    public Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
}
```

---

### `Features/Orchestration/OrchestrationService.cs` (MODIFIED → ~80 LOC orchestrator)

**Current shape (verified):** 92 LOC, ctor injects `BaseDbContext` + `IValidator<IReadOnlyList<Guid>>` + 5 mappers (all with `[SuppressMessage(IDE0052)]`, lines 47-56). Single public method `ValidateWorkflowIdsAsync` (lines 94-128).

**What changes:**
1. **DROP** the 5 mapper fields (lines 48-56) AND their 5 `[SuppressMessage]` attributes — they relocate to the loader (D-05). Resolves the Phase 9 D-05 "pre-injected, unused" smell.
2. **New ctor deps:** `IWorkflowGraphLoader`, `CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator`, `IRedisProjectionWriter` (plus keep `BaseDbContext` + `IValidator<IReadOnlyList<Guid>>` for the existence check). Use the same `?? throw new ArgumentNullException(nameof(...))` guard style as lines 67-73.
3. **`StartAsync` (NEW orchestrator, ORCH-SPLIT-03)** — structurally FINAL per D-01. Order LOCKED. Copy the `using`-declaration disposal pattern from RESEARCH §Pattern 4 (lines 200-211):

```csharp
public async Task StartAsync(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
{
    await ExistenceCheckAsync(workflowIds, ct);                       // 1. D-08 404 fast-fail
    using var snapshot = await _loader.LoadL1Async(workflowIds, ct);  // 2. disposed on success AND throw
    _cycleDetector.Validate(snapshot);                               // 3. no-op P13
    _schemaEdgeValidator.Validate(snapshot);                         // 4. no-op P13
    _payloadConfigSchemaValidator.Validate(snapshot);                // 5. no-op P13
    await _redisProjectionWriter.UpsertAsync(snapshot, ct);          // 6. no-op P13
    // 7. snapshot.Dispose() runs implicitly here AND on any throw above (using declaration).
}
```

Do NOT hand-roll `try/finally` (RESEARCH anti-pattern; the `using` declaration IS what the forced-throw test verifies).

4. **`StopAsync` (NEW separate method, D-07/ORCH-SPLIT-04)** — the OLD `ValidateWorkflowIdsAsync` body verbatim (the null-guard at lines 103-109, `_idsValidator.ValidateAndThrowAsync` at 112, existence check at 115-119, `NotFoundException` at 124-126). Renamed; no longer shared with Start. Phase 15 swaps the body to Redis EXISTS.

5. **Existence check (D-08)** — extract a private `ExistenceCheckAsync(IReadOnlyList<Guid>, CancellationToken)` helper holding lines 103-127 logic, called by BOTH `StartAsync` (step 1) and `StopAsync`. A private helper is NOT "sharing a method" in the D-07 sense (distinct public Start/Stop surfaces). The error shape `NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing))` is LOCKED — `StartOrchestrationFacts` lines 178 + 219 assert it exactly. Do NOT change it.

**Controller is UNCHANGED in routing** but its two calls (`OrchestrationController.cs:50` and `:66`) currently both call `ValidateWorkflowIdsAsync`. After the split, `Start` calls `_service.StartAsync(...)` and `Stop` calls `_service.StopAsync(...)`. The controller still injects the concrete `OrchestrationService` (Phase 9 D-06, verified `OrchestrationController.cs:33-36`).

---

### `Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` (MODIFIED)

**Current body (verified, lines 32-36):**

```csharp
public static IServiceCollection AddOrchestrationFeature(this IServiceCollection services)
{
    services.AddScoped<OrchestrationService>();
    return services;
}
```

**What changes — add 5 Scoped registrations** mirroring the existing Scoped lifetime (Claude's discretion → Scoped; rationale: `OrchestrationService` and the loader both depend on Scoped `BaseDbContext`, so Singleton would be a captive-dependency bug):

```csharp
services.AddScoped<OrchestrationService>();
services.AddScoped<IWorkflowGraphLoader, WorkflowGraphLoader>();
services.AddScoped<CycleDetector>();
services.AddScoped<SchemaEdgeValidator>();
services.AddScoped<PayloadConfigSchemaValidator>();
services.AddScoped<IRedisProjectionWriter, RedisProjectionWriter>();
```

This extension stays `internal static`; it is the 6th feature call in `AppFeatures.AddAppFeatures`. The 5 mappers + `WorkflowIdsValidator` remain auto-discovered (do NOT register them here — see the extension's own XML doc lines 18-28).

---

### `tests/BaseApi.Tests/Features/Orchestration/WorkflowGraphLoaderFacts.cs` (NEW — SC3 + SC5)

**Analog:** `StartOrchestrationFacts.cs` (verified) — copy `[Trait("Phase","13")]`, `IClassFixture<Phase8WebAppFactory>`, `TestContext.Current.CancellationToken` (xUnit v3), and the `SeedWorkflowAsync` HTTP-seeding helper (lines 47-86) which chains Processor → Step → Workflow via the public API. Extend the seed with `NextStepIds`, `AssignmentIds`, and non-null processor schema ids to exercise enrichment.

**SC3 (white-box, preferred):** resolve the loader from a DI scope — `using var scope = factory.Services.CreateScope(); var loader = scope.ServiceProvider.GetRequiredService<IWorkflowGraphLoader>();` — call `LoadL1Async`, assert `snapshot.Steps.ContainsKey(...)` etc. Requires `InternalsVisibleTo` (see Shared Patterns).
**SC5:** seed a fan-out graph (1 entry → 2 children) + a cyclic graph (A→B→A); assert all steps present + the cyclic load does NOT hang (the `visited` guard).

### `tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs` (NEW — SC4, THE acceptance gate)

**Analogs:** `StartOrchestrationFacts.cs` (HTTP harness) + `RecordingTestService.cs` (the repo's recording-test-double precedent — a subclass that stashes observed state in a public `List<>`).

**D-04 mechanism (verified-feasible):** register a **recording `IWorkflowGraphLoader` double** that wraps the real loader and captures the returned `WorkflowGraphSnapshot` instance in a field; register a **throwing `IRedisProjectionWriter` double** (interface → trivially substitutable, fires LAST after all validators) whose `UpsertAsync` throws. Swap both via `factory.WithWebHostBuilder(b => b.ConfigureTestServices(services => { ... }))`. POST `/start`, expect the throw to surface as 500, then assert `captured.IsDisposed == true` and every dictionary `Count == 0`. Prefer the throwing writer over a throwing validator (writer is already an interface; validators are concrete per D-02).

---

## Shared Patterns

### AsNoTracking batch read
**Source:** `OrchestrationService.cs` lines 115-119 (verified).
**Apply to:** every entity + junction read in `WorkflowGraphLoader`.
```csharp
await _db.Set<TEntity>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
```

### Mapperly ToRead returns null junctions (the D-06 enrichment driver)
**Source:** `StepEntityMapper.cs:54` `[MapValue(nameof(StepReadDto.NextStepIds), null)]`; `WorkflowEntityMapper.cs:65-66` `[MapValue(EntryStepIds, null)]` + `[MapValue(AssignmentIds, null)]` (both verified).
**Apply to:** loader's Step + Workflow mapping — `ToRead` then `with { ... = junctionList }`.

### `?? throw new ArgumentNullException(nameof(x))` ctor guard
**Source:** `OrchestrationService.cs` lines 67-73 (verified).
**Apply to:** loader ctor + orchestrator ctor for every injected dependency.

### `using` declaration for deterministic cleanup (L1-BUILD-05)
**Source:** RESEARCH §Pattern 4 (lines 200-211); no in-repo precedent for snapshot disposal (this is new).
**Apply to:** `StartAsync` — `using var snapshot = await _loader.LoadL1Async(...)`.

### NotFoundException error shape (LOCKED contract)
**Source:** `OrchestrationService.cs` lines 124-126; asserted by `StartOrchestrationFacts.cs:178` (single id) + `:219` (multi-id comma-join).
**Apply to:** the shared `ExistenceCheckAsync` helper. `NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing))` — byte-exact, do not alter the `", "` separator.

### InternalsVisibleTo gap (A1 — MUST be added by the planner)
**Status:** VERIFIED ABSENT. `[assembly: InternalsVisibleTo("BaseApi.Tests")]` does NOT exist anywhere in `src/BaseApi.Service` (the only `InternalsVisibleTo` hits are unrelated XML-doc-comment mentions in `BaseApi.Core` and binary DLL matches). The `BaseApi.Service.csproj` has no `InternalsVisibleTo` MSBuild item either.
**Impact:** The `internal` seams (ORCH-SPLIT-02) are invisible to `BaseApi.Tests`, so the SC3 white-box loader resolution + the SC4 internal seam doubles will NOT compile (CS0122) without it.
**Fix (one line, planner MUST include):** add `[assembly: InternalsVisibleTo("BaseApi.Tests")]` to `BaseApi.Service` (e.g., an `AssemblyInfo.cs` or any source file), OR add an `InternalsVisibleTo` MSBuild item to the csproj. Alternatively fall back to black-box `POST /start` 204 assertions for SC3 (but SC4's recording double still needs internal access).

---

## No Analog Found

None. Every P13 file maps to an in-repo analog (some patterns — `IDisposable`, the recording-loader double — have only a *partial* precedent and follow RESEARCH §Code Examples, but no file is left to RESEARCH-only patterns).

---

## Metadata

**Analog search scope:** `src/BaseApi.Service/Features/{Orchestration,Step,Workflow,Processor,Schema,Assignment}/`, `src/BaseApi.Core/Mapping/`, `src/BaseApi.Service/AppDbContext.cs`, `tests/BaseApi.Tests/{Features/Orchestration,Composition}/`.
**Files scanned (read in full or targeted):** OrchestrationService.cs, OrchestrationController.cs, OrchestrationServiceCollectionExtensions.cs, StepEntityMapper.cs, WorkflowEntityMapper.cs, StepNextSteps.cs, WorkflowEntrySteps.cs, WorkflowAssignments.cs, StepDtos.cs, WorkflowDtos.cs, AssignmentDtos.cs, ProcessorDtos.cs, SchemaDtos.cs, StepService.cs (SyncJunctionsAsync), AppDbContext.cs (DbSets), IEntityMapper.cs, StartOrchestrationFacts.cs, Phase8WebAppFactory.cs, RecordingTestService.cs, BaseApi.Service.csproj.
**Pattern extraction date:** 2026-05-29
```
