# Phase 13: OrchestrationService split + L3 fetch + L1 build - Research

**Researched:** 2026-05-29
**Domain:** .NET 8 / EF Core 8 (Npgsql) backend refactor — service decomposition + transient read-model build
**Confidence:** HIGH (all findings verified against live code in this session; no external library version risk)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01:** Scaffold ALL 4 seams now as no-op (validators + writer). `StartAsync`'s body is structurally FINAL in P13 — `existence-check → loader → CycleDetector → SchemaEdgeValidator → PayloadConfigSchemaValidator → IRedisProjectionWriter → snapshot.Dispose()`. Phases 14/15 fill seam bodies only; zero re-wiring. (Mirrors Phase 9 D-05 pre-injected mappers + Phase 12 pre-wired Redis DI.)
- **D-02:** Validators invoked by **explicit ordered calls by concrete name** (not a shared-interface chain), contract `void Validate(WorkflowGraphSnapshot snapshot)` — **synchronous**, **throws** a 422-mapped exception on failure. In P13 bodies are empty (no-op). Concrete (no `I` prefix) per REQ naming; loader/writer remain interfaces.
- **D-03:** Traversal lives **inside `IWorkflowGraphLoader.LoadL1Async` as private logic** (iterative BFS from entry steps; visited-list keyed on StepId). `WorkflowGraphSnapshot` stays **pure data** (5 dictionaries only — no walk methods). The visited list is what makes loading TERMINATE on a cyclic workflow (validators are no-op in P13).
- **D-04:** `WorkflowGraphSnapshot` exposes a public `IsDisposed` bool set true in `Dispose()` (which also clears the dictionaries) + an `ILogger.LogDebug("L1 snapshot disposed")` line. Forced-throw integration test captures the snapshot via a **recording `IWorkflowGraphLoader` test double** + a **throwing seam double** (validator or writer) firing *after* the loader returns; asserts `IsDisposed == true` and dictionaries empty.
- **D-05:** The 5 `IEntityMapper` instances **move from `OrchestrationService` ctor to the `IWorkflowGraphLoader` impl ctor**. Orchestrator drops all 5 mapper fields AND their `[SuppressMessage(IDE0052)]` attributes. Loader produces ReadDtos via existing **Mapperly** mappers (AsNoTracking load → `ToRead`). Inline `.Select` projection rejected.
- **D-06:** Mapperly `ToRead` returns **null** junction collections. Loader **enriches**: keep Mapperly for all 5 (scalars), then for **Step** (`NextStepIds`) and **Workflow** (`EntryStepIds`, `AssignmentIds`) batch-load the junction tables (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`) and rebuild immutable records via `readDto with { NextStepIds = [...] }`. Schema / Processor / Assignment need no enrichment.
- **D-07 (StopAsync, ORCH-SPLIT-04):** `StopAsync` extracted as its own method retaining the current workflow-id existence-check behavior (the old `ValidateWorkflowIdsAsync` logic) in P13. Phase 15 swaps it to Redis `EXISTS`. Start and Stop no longer share a method.
- **D-08 (existence-check placement):** The existing `SELECT id WHERE id IN (...)` workflow-existence check stays as `StartAsync`'s FIRST step. Small overlap with the loader's later workflow fetch is intentional — fails fast (404) before building any L1 state.

### Claude's Discretion

- **Loader batch-query staging:** depth-wave step loads (round-trips ≈ graph depth) → one batched processor query → one batched schema query → assignments via one batched query from the workflows' `AssignmentIds` junction. All `AsNoTracking` + `Where(ids.Contains)`. Exact staging is topology-forced — planner decides.
- **DI lifetimes for new seams:** register loader + 3 validators + writer mirroring `OrchestrationService`'s existing **Scoped** lifetime, unless research surfaces a reason otherwise.
- **No-op writer signature:** `IRedisProjectionWriter` method shape (async, takes the snapshot, returns `Task`) — planner defines a forward-compatible signature Phase 15 can fill without re-wiring.

### Deferred Ideas (OUT OF SCOPE)

- **Validator rejection logic** (cycle / schema-edge / payload → 422, mandatory order) — Phase 14. P13 seams are no-op.
- **Redis L2 projection write + Stop-as-EXISTS** — Phase 15. P13 writer is no-op; StopAsync keeps workflow-id existence check (D-07).
- **Shared graph-walk abstraction** (loader walk + CycleDetector walk) — not built now; the two walks have opposite cycle semantics (D-03).
- **HTTP GET/List junction enrichment** — D-06 enriches the L1 path only; v1 GET/List null behavior on `StepReadDto.NextStepIds` etc. is intentionally untouched.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ORCH-SPLIT-01 | Split `OrchestrationService` into orchestrator + 4 internal seams under `Features/Orchestration/{Loading,Validation,Projection}/` | §Architecture Patterns (seam layout + DI); §Don't Hand-Roll. Current 92-LOC service + DI extension verified. |
| ORCH-SPLIT-02 | Seams stay `internal` to `BaseApi.Service.Features.Orchestration` | §Pitfall: internal visibility vs. test access — recording double must live in the same assembly OR `InternalsVisibleTo` used. |
| ORCH-SPLIT-03 | `StartAsync` becomes orchestrator only (~80 LOC): existence → loader → validators → writer → `Dispose` | §Code Examples (orchestrator skeleton). Order locked by D-01. |
| ORCH-SPLIT-04 | `StopAsync` extracted as NEW method (split from `ValidateWorkflowIdsAsync`) | §Code Examples (StopAsync). Current `ValidateWorkflowIdsAsync` body is the StopAsync template. |
| L1-BUILD-01 | `WorkflowGraphSnapshot` transient `IDisposable` record, lives only in `StartAsync` scope | §Code Examples (snapshot record); §Pitfall (record + IDisposable + clearing dictionaries). |
| L1-BUILD-02 | `IWorkflowGraphLoader.LoadL1Async(IReadOnlyList<Guid>)` uses `BaseDbContext.Set<>().AsNoTracking()...Where(x => ids.Contains(x.Id))` batch queries; NOT `Repository<TEntity>` | Verified `ValidateWorkflowIdsAsync` already uses this exact pattern; `BaseDbContext.Set<T>()` available on all entity + junction types. |
| L1-BUILD-03 | Loader populates flat `Dictionary<Guid, EntityDto>` per type: Workflows, Assignments, Steps, Processors, Schemas; keyed by `Id`; DTOs are v3.2.0 ReadDto types | §Architectural Responsibility Map; §Code Examples. All 5 ReadDto shapes verified. |
| L1-BUILD-04 | Step traversal: every `Workflow.EntryStepIds[*]` → every `StepEntity.NextStepIds[*]` via `StepNextSteps`; multi-child fan-out; per-traversal `visited` list keyed on StepId (NOT HashSet on object identity) | §Code Examples (BFS); §Pitfall (termination on cycle). Junction PK is `(StepId, NextStepId)` — children of S = `Where(j => j.StepId == S).Select(j => j.NextStepId)`. |
| L1-BUILD-05 | `WorkflowGraphSnapshot.Dispose()` clears dictionaries + traversal lists; called via `using` declaration so cleanup runs on success AND failure | §Code Examples (`using` declaration); §Validation Architecture (forced-throw test). |
</phase_requirements>

## Summary

This is a pure refactor + read-model-build phase against an already-understood, fully-local stack (.NET 8, EF Core 8 with Npgsql, Mapperly 4.x). There is **zero external-library version risk** — no new packages are introduced (Phase 12 already landed Redis DI). Every fact in this document was verified by reading the live source in this session. The entire phase is mechanical: decompose a 92-LOC service into a thin orchestrator + 4 seams, and implement one real seam (`IWorkflowGraphLoader`) that issues batched `AsNoTracking` reads, maps via the existing Mapperly mappers, enriches two DTO families with junction data, and walks the step graph with a termination-guaranteeing `visited` list.

The two highest-risk areas are (1) **the loader's batch-query staging** — depth-wave step loads because `StepEntity` carries no next-step nav property, so children are only discoverable by querying the `StepNextSteps` junction one BFS wave at a time; and (2) **deterministic disposal** — a C# `using` declaration on the snapshot inside `StartAsync` guarantees `Dispose()` runs on the throw path, which is the entire point of L1-BUILD-05 and Success Criterion 4. A subtle but load-bearing data-model fact: **`AssignmentEntity` has `StepId` + `Payload` but NO `WorkflowId`** (Phase 10 removed schema coupling). Assignments are reachable from a workflow ONLY through the `WorkflowAssignments` junction (`WorkflowId → AssignmentId`), not by walking steps. The loader must collect `AssignmentId`s from `WorkflowAssignments` for the requested workflows, then batch-load those assignments.

**Primary recommendation:** Land the split as its own commit(s) first (orchestrator shape final, all 4 seams scaffolded no-op, mappers relocated to the loader), then implement `LoadL1Async`. Use a private iterative BFS inside the loader keyed on a `List<Guid> visited`. Wrap the snapshot in a `using` declaration. Register all 5 seams `Scoped` mirroring `OrchestrationService`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| HTTP request acceptance / 204 response | API / Controller (`OrchestrationController`, unchanged) | — | Routing + `[ProducesResponseType]` already correct for P13; controller still injects concrete `OrchestrationService` (Phase 9 D-06). |
| Orchestration sequencing (existence → loader → validators → writer → dispose) | API / Backend service (`OrchestrationService` orchestrator) | — | The orchestrator is pure control-flow; ORCH-SPLIT-03 caps it at ~80 LOC. |
| L3 read (batched entity + junction fetch) | Database / Persistence (`IWorkflowGraphLoader` via `BaseDbContext`) | — | The ONLY seam with real behavior in P13. Direct `Set<>().AsNoTracking()` — explicitly NOT through `Repository<TEntity>` (L1-BUILD-02). |
| Entity→DTO mapping | Mapping seam (Mapperly mappers, relocated into loader) | — | Single RMG-guarded mapping seam (D-05); no inline `.Select` allowed. |
| Junction enrichment (Step.NextStepIds, Workflow.EntryStepIds/AssignmentIds) | Persistence + in-memory rebuild (loader) | — | Mapperly returns null junctions (D-06); loader batch-queries junctions and rebuilds positional records via `with {}`. |
| Graph traversal / termination | In-memory (loader private BFS) | — | `visited` `List<Guid>` keyed on StepId guarantees termination on cyclic input (D-03, L1-BUILD-04). |
| L1 lifecycle / cleanup | In-memory transient (`WorkflowGraphSnapshot : IDisposable`) | — | `using` declaration in `StartAsync` runs `Dispose()` on success + throw (L1-BUILD-05). |
| Validation gates | Validation seams (no-op stubs in P13) | Phase 14 | Empty `void Validate(snapshot)` bodies; throw-semantics reserved. |
| L2 write | Projection seam (no-op stub in P13) | Phase 15 | Forward-compatible `Task UpsertAsync(snapshot, ...)` signature only. |

## Standard Stack

No new packages. All capabilities use already-referenced libraries verified present in the solution.

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.EntityFrameworkCore | 8.x (in use) | `Set<T>().AsNoTracking().Where(...).ToListAsync()` batch reads | [VERIFIED: live code] `ValidateWorkflowIdsAsync` already uses this exact API surface. |
| Npgsql.EntityFrameworkCore.PostgreSQL | 8.x (in use) | Postgres provider; translates `ids.Contains(x.Id)` to SQL `= ANY(@ids)` | [VERIFIED: live code] Existing existence check already translates `Contains`. |
| Riok.Mapperly | 4.x (in use) | Source-gen entity→DTO mapping (`ToRead`), RMG-guarded | [VERIFIED: live code] `StepEntityMapper`, `WorkflowEntityMapper` etc. confirmed using `[MapValue(..., null)]` and `[Mapper]`. |
| Microsoft.Extensions.Logging.Abstractions | 8.x (in use) | `ILogger<>` for the `LogDebug("L1 snapshot disposed")` line (D-04) | [VERIFIED] MEL is the project logging standard (Phase 5). |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.DependencyInjection | 8.x (in use) | `AddScoped<>` for the 5 new seams in `AddOrchestrationFeature` | Registering loader + 3 validators + writer. |
| StackExchange.Redis | 2.13.x (in use, Phase 12) | Type only available for the no-op writer's *forward* signature; NOT consumed in P13 | The no-op writer needs NO Redis types — keep its P13 signature dependency-free (`Task UpsertAsync(WorkflowGraphSnapshot, ...)`). |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Mapperly `ToRead` + junction enrichment (D-06) | Inline `.Select(e => new ReadDto(...))` projection | REJECTED by D-05 — duplicates mapping outside the single RMG-guarded seam; risks Mapperly drift-guard divergence. |
| Direct `Set<>().AsNoTracking()` (L1-BUILD-02) | `Repository<TEntity>` 5-method surface | REJECTED — Phase 3 `IRepository<>` surface is locked (no `IQueryable<>` leakage); loader is explicitly NOT a repository. |
| Depth-wave BFS over junction queries | EF `.Include()` nav-property traversal | IMPOSSIBLE — entities have no nav properties (ENTITY-09); there is nothing to `.Include()`. |
| `List<Guid> visited` (L1-BUILD-04) | `HashSet<Guid>` | REQ L1-BUILD-04 is explicit: List keyed on StepId, NOT HashSet on object identity. (Perf is irrelevant at workflow-graph scale.) |

**Installation:** None. `git diff Directory.Packages.props` must show NO change in this phase.

## Architecture Patterns

### System Architecture Diagram

```
POST /api/v1/orchestration/start  (List<Guid> workflowIds)
        │
        ▼
OrchestrationController.Start ──► OrchestrationService.StartAsync   (orchestrator, ~80 LOC)
        │
        │  1. existence-check (D-08): SELECT id WHERE id IN (...)  ──► 404 fast-fail if any missing
        │
        │  2. using snapshot = await loader.LoadL1Async(workflowIds)   ◄── IWorkflowGraphLoader (REAL)
        │            │
        │            ├─ batch: Workflows  (Set<WorkflowEntity>.AsNoTracking().Where(ids.Contains))
        │            ├─ junction: WorkflowEntrySteps  → entry StepIds per workflow
        │            ├─ junction: WorkflowAssignments → AssignmentIds per workflow
        │            ├─ BFS depth-waves over StepNextSteps:
        │            │      wave0 = entry steps → load StepEntities
        │            │      waveN = NextStepIds of waveN-1 (visited-guarded) → load StepEntities
        │            ├─ batch: Processors (collected StepEntity.ProcessorId)
        │            ├─ batch: Schemas    (collected Input/Output/Config schema ids)
        │            ├─ batch: Assignments (collected AssignmentIds)
        │            ├─ Mapperly ToRead → 5 Dictionary<Guid, ReadDto>
        │            └─ enrich: Step.NextStepIds, Workflow.EntryStepIds/AssignmentIds via `with {}`
        │
        │  3. CycleDetector.Validate(snapshot)           ◄── no-op stub (Phase 14)
        │  4. SchemaEdgeValidator.Validate(snapshot)     ◄── no-op stub (Phase 14)
        │  5. PayloadConfigSchemaValidator.Validate(snapshot) ◄── no-op stub (Phase 14)
        │  6. await writer.UpsertAsync(snapshot, ...)    ◄── IRedisProjectionWriter no-op stub (Phase 15)
        │
        │  7. snapshot.Dispose()  ── runs via `using` on BOTH success AND throw paths
        ▼
204 No Content

POST /api/v1/orchestration/stop ──► OrchestrationService.StopAsync (SEPARATE method, D-07)
        └─ retains v3.2.0 existence-check (Postgres SELECT id WHERE id IN); Phase 15 swaps to Redis EXISTS
```

### Recommended Project Structure
```
src/BaseApi.Service/Features/Orchestration/
├── OrchestrationController.cs                 # unchanged routing; ctor injects OrchestrationService
├── OrchestrationService.cs                    # SLIMMED: orchestrator (StartAsync + StopAsync), no mapper fields
├── OrchestrationServiceCollectionExtensions.cs# + 5 new AddScoped<> registrations
├── OrchestrationDtoValidator.cs               # WorkflowIdsValidator — unchanged
├── WorkflowGraphSnapshot.cs                    # NEW: transient IDisposable record (5 dictionaries + IsDisposed)
├── Loading/
│   ├── IWorkflowGraphLoader.cs                 # NEW: Task<WorkflowGraphSnapshot> LoadL1Async(IReadOnlyList<Guid>, CancellationToken)
│   └── WorkflowGraphLoader.cs                  # NEW: REAL impl — 5 mappers relocated here + BFS + enrichment
├── Validation/
│   ├── CycleDetector.cs                        # NEW: void Validate(WorkflowGraphSnapshot) — no-op body
│   ├── SchemaEdgeValidator.cs                  # NEW: void Validate(WorkflowGraphSnapshot) — no-op body
│   └── PayloadConfigSchemaValidator.cs         # NEW: void Validate(WorkflowGraphSnapshot) — no-op body
└── Projection/
    ├── IRedisProjectionWriter.cs               # NEW: Task UpsertAsync(WorkflowGraphSnapshot, ...) — forward-compatible
    └── RedisProjectionWriter.cs                # NEW: no-op body returning Task.CompletedTask
```
All new types are `internal` (ORCH-SPLIT-02). Concrete validators have NO `I` prefix (D-02); loader + writer ARE interfaces.

### Pattern 1: Direct AsNoTracking batch read (the loader's only DB mechanism)
**What:** Read entities by id-set in one round-trip, no change tracking, no `IQueryable` leakage.
**When to use:** Every entity + junction read in the loader.
```csharp
// Source: VERIFIED live code — OrchestrationService.ValidateWorkflowIdsAsync (lines 115-119)
var workflows = await _db.Set<WorkflowEntity>()
    .AsNoTracking()
    .Where(w => ids.Contains(w.Id))
    .ToListAsync(ct);
// Npgsql translates ids.Contains(...) to `WHERE id = ANY(@ids)` — one query, not N.
```

### Pattern 2: Mapperly ToRead + junction enrichment via `with {}` (D-06)
**What:** Map scalars via the drift-guarded Mapperly mapper, then rebuild the immutable positional record with the junction collection filled in.
**When to use:** Step (`NextStepIds`) and Workflow (`EntryStepIds`, `AssignmentIds`) ONLY. Schema/Processor/Assignment need no enrichment.
```csharp
// Source: VERIFIED — StepReadDto is a positional record; StepEntityMapper.ToRead returns NextStepIds=null
var dto = _stepMapper.ToRead(stepEntity);                  // NextStepIds == null here (by [MapValue])
var children = nextStepLookup[stepEntity.Id];              // List<Guid> from StepNextSteps junction
var enriched = dto with { NextStepIds = children };        // immutable rebuild
stepsDict[enriched.Id] = enriched;
```

### Pattern 3: Junction lookup grouping (one query → in-memory grouping)
**What:** Batch-read junction rows for the relevant parent ids, then `GroupBy` into a `Dictionary<Guid, List<Guid>>`.
```csharp
// Source: pattern derived from StepService.SyncJunctionsAsync (junction query) + AppDbContext.StepNextSteps
// Junction PK is (StepId, NextStepId) [VERIFIED: StepNextStepsConfiguration]. Children of S => rows where StepId == S.
var nextRows = await _db.Set<StepNextSteps>()
    .AsNoTracking()
    .Where(j => stepIds.Contains(j.StepId))
    .ToListAsync(ct);
var nextStepLookup = nextRows
    .GroupBy(j => j.StepId)
    .ToDictionary(g => g.Key, g => g.Select(j => j.NextStepId).ToList());
```

### Pattern 4: `using` declaration for deterministic disposal (L1-BUILD-05)
**What:** A C# 8 `using` declaration disposes at end of the enclosing scope on EVERY exit path (return, throw).
```csharp
public async Task StartAsync(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
{
    await ExistenceCheckAsync(workflowIds, ct);                 // 404 fast-fail (D-08)
    using var snapshot = await _loader.LoadL1Async(workflowIds, ct);  // <-- disposed on success AND throw
    _cycleDetector.Validate(snapshot);                          // no-op P13
    _schemaEdgeValidator.Validate(snapshot);                    // no-op P13
    _payloadConfigSchemaValidator.Validate(snapshot);           // no-op P13
    await _redisProjectionWriter.UpsertAsync(snapshot, ct);     // no-op P13
    // snapshot.Dispose() invoked implicitly here AND if any line above throws.
}
```
**Note:** A `using` declaration is functionally a `try/finally` the compiler generates. It satisfies "L1 cleanup still runs in the `finally` path" (forward REQ L1-VALIDATE-01). Do NOT hand-roll `try/finally` — the declaration is cleaner and is what the forced-throw test verifies.

### Anti-Patterns to Avoid
- **`try { } finally { snapshot.Dispose(); }` by hand:** Redundant with a `using` declaration; the declaration is the idiomatic and test-verified form.
- **`.Include()` traversal:** Impossible — no nav properties exist (ENTITY-09). The junction queries ARE the traversal.
- **Recursive graph walk:** Forbidden downstream (L1-VALIDATE-03 bans recursion for `StackOverflowException` safety). Use iterative BFS now so the loader's walk shape never needs re-litigation.
- **`HashSet<Guid>` for visited:** REQ L1-BUILD-04 explicitly says `List<Guid>` keyed on StepId.
- **Inline `.Select` ReadDto projection:** Violates D-05 single-mapping-seam discipline; will diverge from Mapperly drift guards.
- **Collecting assignments by walking `Step.AssignmentId`:** There is no such field. Assignments attach to workflows via the `WorkflowAssignments` junction; `AssignmentEntity` only has `StepId` + `Payload`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Entity→DTO mapping | Manual constructor calls / inline `.Select` | Existing Mapperly `ToRead` (relocated into loader) | RMG007/012/020/089 build-error drift guards; D-05 single-seam rule. |
| `WHERE id IN (...)` batching | N+1 per-id queries | `Where(ids.Contains(x.Id))` (Npgsql → `= ANY`) | One round-trip; already the established pattern. |
| Deterministic cleanup | hand-written `try/finally` | `using` declaration on the snapshot | Compiler-generated, exception-safe, test-verified. |
| 404 existence error shape | new error payload | existing `NotFoundException("WorkflowEntity", joinedIds)` | Locked error contract (StartOrchestrationFacts asserts `string.Join(", ", missing)` exactly). |
| Validation 422 wiring | new exception handler in P13 | NOTHING — validators are no-op in P13 | The 422-mapped exception type is a Phase 14 concern; do not introduce it now. |

**Key insight:** The only genuinely new logic in this phase is the loader's BFS staging and the snapshot's `Dispose`. Everything else is relocation of existing, already-guarded machinery (mappers, the existence check, DI registration). The risk is in *over-building* — adding the 422 exception, the writer's Redis body, or a shared walk abstraction before their phases.

## Common Pitfalls

### Pitfall 1: Assignments are not reachable by walking steps
**What goes wrong:** Loader tries to find a workflow's assignments via `Step.AssignmentId` or `Assignment.WorkflowId` — neither exists.
**Why it happens:** Phase 10 removed schema coupling; `AssignmentEntity` now carries only `StepId` + `Payload`. The workflow↔assignment edge lives ONLY in the `WorkflowAssignments` junction (`WorkflowId, AssignmentId`).
**How to avoid:** Collect `AssignmentId`s from `WorkflowAssignments.Where(j => workflowIds.Contains(j.WorkflowId))`, then batch-load `AssignmentEntity` by those ids.
**Warning signs:** Empty Assignments dictionary for a workflow that has assignments; compile error looking for a non-existent `WorkflowId` property.

### Pitfall 2: Record + IDisposable + dictionary clearing
**What goes wrong:** `WorkflowGraphSnapshot` is a `record` (immutable positional members) but `Dispose()` must mutate `IsDisposed` and CLEAR the dictionaries.
**Why it happens:** Positional record init-only members can't be reassigned; but `Dictionary.Clear()` mutates the *contents*, not the reference, and `IsDisposed` can be a mutable auto-property (not a positional member).
**How to avoid:** Declare the 5 dictionaries as init-only/get-only members holding mutable `Dictionary<>` instances; `Dispose()` calls `.Clear()` on each (mutates contents, legal) and sets a separate mutable `public bool IsDisposed { get; private set; }`. Do NOT try to null-out the dictionary references.
**Warning signs:** CS8852 "init-only property can only be assigned in an object initializer"; attempting `Workflows = null` in `Dispose()`.

### Pitfall 3: `internal` seams vs. test-double access (ORCH-SPLIT-02)
**What goes wrong:** The forced-throw test (D-04) needs to substitute a recording `IWorkflowGraphLoader` + a throwing seam — but the seams are `internal`, invisible to `BaseApi.Tests`.
**Why it happens:** ORCH-SPLIT-02 keeps seams `internal` to the Orchestration namespace.
**How to avoid:** Add `[assembly: InternalsVisibleTo("BaseApi.Tests")]` to `BaseApi.Service` (check whether it already exists — Phase 8/12 tests reach internal types, so it likely does). Verify before planning the test wiring. The test then swaps the DI registration of the loader/writer for its double via `WebApplicationFactory.ConfigureTestServices`.
**Warning signs:** CS0122 "inaccessible due to its protection level" in test project.

### Pitfall 4: Depth-wave round-trips, not a single step query
**What goes wrong:** Loader tries to load all steps in one query but doesn't know the full step-id set up front, because children are only discoverable by querying `StepNextSteps` after loading the current wave.
**Why it happens:** No next-step field on `StepEntity`; the graph is only walkable through the junction.
**How to avoid:** BFS in waves: load entry steps → query their `NextStepIds` from `StepNextSteps` → (visited-guarded) load the next wave → repeat until no new ids. Round-trips ≈ graph depth (acceptable; depth is small for real workflows). THEN do single batched queries for processors / schemas / assignments once all step ids are known.
**Warning signs:** Missing deep steps in the snapshot; or an attempt to recursively self-join the junction in SQL.

### Pitfall 5: Cyclic graph never terminates (the whole reason `visited` exists in P13)
**What goes wrong:** A workflow with a step cycle (A→B→A) causes the BFS to loop forever, because P13 has NO cycle-rejecting validator (CycleDetector is no-op until Phase 14).
**Why it happens:** D-03 — the loader must TOLERATE cycles to load; only Phase 14 rejects them.
**How to avoid:** Before enqueuing a child StepId, check `visited.Contains(childId)`; skip if present; else add to `visited` and enqueue. This is L1-BUILD-04's explicit contract and is what guarantees termination this phase.
**Warning signs:** Test hang / timeout when a cyclic workflow is loaded.

### Pitfall 6: Existence-check / loader double-fetch is intentional
**What goes wrong:** A reviewer "optimizes away" the D-08 existence check because the loader fetches workflows anyway.
**Why it happens:** Apparent redundancy.
**How to avoid:** Keep both. D-08 is locked: the existence check fails fast with 404 BEFORE any L1 state is built, and its error shape (`NotFoundException` → `string.Join(", ", missing)`) is asserted by existing facts. The loader's workflow fetch is for snapshot-building, not existence.

## Code Examples

### WorkflowGraphSnapshot (transient IDisposable record) — L1-BUILD-01/03/05, D-04
```csharp
// internal — Features/Orchestration/WorkflowGraphSnapshot.cs
internal sealed record WorkflowGraphSnapshot : IDisposable
{
    public Dictionary<Guid, WorkflowReadDto>   Workflows   { get; init; } = new();
    public Dictionary<Guid, AssignmentReadDto> Assignments { get; init; } = new();
    public Dictionary<Guid, StepReadDto>       Steps       { get; init; } = new();
    public Dictionary<Guid, ProcessorReadDto>  Processors  { get; init; } = new();
    public Dictionary<Guid, SchemaReadDto>     Schemas     { get; init; } = new();

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed) return;
        Workflows.Clear();
        Assignments.Clear();
        Steps.Clear();
        Processors.Clear();
        Schemas.Clear();
        IsDisposed = true;
        // ILogger.LogDebug("L1 snapshot disposed") — inject logger or log from orchestrator (D-04).
    }
}
```
*Logger placement is planner's discretion: D-04 says the log line lives with Dispose observability. Cleanest is to pass an `ILogger` into the snapshot or have the loader log; planner decides. The test asserts `IsDisposed == true` + all dictionaries `Count == 0`.*

### No-op seam contracts (D-01/D-02) — forward-compatible
```csharp
// Validation/CycleDetector.cs (+ SchemaEdgeValidator, PayloadConfigSchemaValidator — identical shape)
internal sealed class CycleDetector
{
    // Phase 14 fills this body and throws a 422-mapped exception on a detected cycle.
    public void Validate(WorkflowGraphSnapshot snapshot) { /* no-op in P13 */ }
}

// Projection/IRedisProjectionWriter.cs
internal interface IRedisProjectionWriter
{
    // Phase 15 fills the body (3 keyspaces via IDatabase.CreateBatch). Signature is forward-compatible:
    // takes the snapshot; async returning Task. Add a correlationId param now if cheap, else Phase 15 adds it.
    Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct);
}
internal sealed class RedisProjectionWriter : IRedisProjectionWriter
{
    public Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
}
```

### StopAsync extraction (D-07 / ORCH-SPLIT-04)
```csharp
// StopAsync is the OLD ValidateWorkflowIdsAsync body verbatim (Postgres existence check),
// renamed and no longer shared with Start. Phase 15 swaps the body for Redis EXISTS.
public async Task StopAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
{
    if (ids is null) throw new ValidationException(new[]
        { new ValidationFailure(nameof(ids), "Request body must not be null.") });
    await _idsValidator.ValidateAndThrowAsync(ids, ct);
    var existingIds = await _db.Set<WorkflowEntity>().AsNoTracking()
        .Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct);
    var missing = ids.Except(existingIds).ToList();
    if (missing.Count > 0)
        throw new NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing));
}
```
*The Start path's existence-check step (D-08) is the SAME logic; planner decides whether to share a private helper between StartAsync's step-1 and StopAsync or duplicate. CONTEXT D-07 says Start/Stop no longer share a method, but a private existence-check helper called by both is not "sharing a method" in the D-07 sense — planner's discretion, lean toward a private helper to avoid duplication while keeping StartAsync/StopAsync distinct public surfaces.*

### DI registration (Claude's discretion → Scoped, mirroring OrchestrationService)
```csharp
public static IServiceCollection AddOrchestrationFeature(this IServiceCollection services)
{
    services.AddScoped<OrchestrationService>();
    services.AddScoped<IWorkflowGraphLoader, WorkflowGraphLoader>();
    services.AddScoped<CycleDetector>();
    services.AddScoped<SchemaEdgeValidator>();
    services.AddScoped<PayloadConfigSchemaValidator>();
    services.AddScoped<IRedisProjectionWriter, RedisProjectionWriter>();
    return services;
}
```
**Lifetime rationale [VERIFIED]:** `OrchestrationService` is `AddScoped` and it depends on `BaseDbContext` (Scoped per Phase 7 D-14). The loader also depends on `BaseDbContext`, so it MUST be Scoped or Transient (never Singleton — would capture a Scoped DbContext = captive-dependency bug). The 5 Mapperly mappers are stateless; Scoped is safe and consistent. No reason to deviate from Scoped.

## Runtime State Inventory

This is a code-refactor + new-read-path phase with no rename and no schema change. The relevant question — "what runtime state does this touch?" — is answered per category:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — Phase 13 issues read-only `AsNoTracking` queries against the existing v3.2.0 Postgres schema. No INSERT/UPDATE/DELETE, no migration, no new column/table. The `InitialCreate` schema (5 entities + 3 junctions) is consumed as-is. | None. `psql \l` SHA-256 `0d98b0de…0aac127` MUST remain byte-identical (verified by phase-close gate). |
| Live service config | None — no compose change, no new env var, no Redis key written (writer is no-op). | None. |
| OS-registered state | None. | None. |
| Secrets/env vars | None — `ConnectionStrings:Redis` already added in Phase 12; P13 reads no new config. | None. |
| Build artifacts | None — no package added/removed; `Directory.Packages.props` unchanged. Mapperly source-gen re-runs but produces no new generated mappers (existing `ToRead` reused). | Standard rebuild only. |

## Validation Architecture

**Test framework** (verified from existing orchestration facts + Phase8WebAppFactory):

| Property | Value |
|----------|-------|
| Framework | xUnit (v3 — `TestContext.Current.CancellationToken`, `[Fact]`, `IClassFixture<>`) |
| Config file | none — convention-based; `BaseApi.Tests` project |
| Integration fixture | `Phase8WebAppFactory : WebAppFactory, IAsyncLifetime` (real Postgres via `PostgresFixture` + Redis via `RedisFixture`) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "Phase=13"` |
| Full suite command | `dotnet test` (solution root) |

### Phase Requirements → Test Map

| SC / Req | Behavior | Test Type | Automated Command | File Exists? |
|----------|----------|-----------|-------------------|-------------|
| SC1 / ORCH-SPLIT-01,03 | `StartAsync` ~80 LOC delegating to 4 seams under `{Loading,Validation,Projection}/` | build + grep / unit | build green + `rg` confirms 4 seam files + namespaces | ❌ Wave 0 (grep fact optional) |
| SC2 / ORCH-SPLIT-04 | `StopAsync` is a separate method; Start/Stop no longer share `ValidateWorkflowIdsAsync` | integration (existing facts adapted) | `dotnet test --filter "FullyQualifiedName~StopOrchestrationFacts"` | ✅ adapt `StopOrchestrationFacts` (rename of method must keep `/stop` 204 + 404 green) |
| SC3 / L1-BUILD-02,03 | For a known multi-workflow Start, snapshot contains all expected entities in the 5 dictionaries | integration | new `WorkflowGraphLoaderFacts` against `Phase8WebAppFactory` — seed a 2-workflow graph, resolve the loader from DI scope, assert dictionary keys | ❌ Wave 0 |
| SC4 / L1-BUILD-05 | `Dispose()` runs on success AND failure (forced throw mid-Start) | integration | new fact: recording loader double captures snapshot + throwing seam double → assert `IsDisposed`==true + dictionaries empty | ❌ Wave 0 (THE acceptance gate) |
| SC5 / L1-BUILD-04 | Traversal walks all `EntryStepIds[*]` + all `NextStepIds[*]`; multi-child fan-out; terminates on cycle | integration | new fact: seed a fan-out graph (1 entry → 2 children) + a cyclic graph (A→B→A); assert all steps present + no hang | ❌ Wave 0 |

### Test double mechanism (SC3 + SC4 — the load-bearing detail)

- **SC3 (snapshot contents):** Seed a 2-workflow graph through the public HTTP API (Processor→Step→Workflow chain, exactly like `StartOrchestrationFacts.SeedWorkflowAsync`, extended with `NextStepIds`, `AssignmentIds`, and non-null schema ids on the processor to exercise enrichment). Then either (a) call `POST /start` and assert 204 (black-box: proves the load path runs without throwing), or (b) resolve `IWorkflowGraphLoader` from a DI scope (`factory.Services.CreateScope()`) and call `LoadL1Async` directly, asserting `snapshot.Steps.ContainsKey(...)` etc. (white-box: directly asserts SC3's "all expected entities"). Prefer (b) for SC3 — it directly verifies dictionary contents. Requires `InternalsVisibleTo("BaseApi.Tests")` (Pitfall 3).
- **SC4 (forced-throw cleanup):** Per D-04 — register a **recording `IWorkflowGraphLoader` double** via `ConfigureTestServices` that wraps the real loader and stashes the returned `WorkflowGraphSnapshot` instance in a captured field; register a **throwing seam double** (e.g., a `CycleDetector` subclass/replacement, or the `IRedisProjectionWriter` double whose `UpsertAsync` throws) that fires AFTER the loader returns. POST `/start`, expect the throw to surface as 500. Then assert `capturedSnapshot.IsDisposed == true` and every dictionary `Count == 0`. Because validators are concrete (D-02), substituting a throwing `CycleDetector` requires it be registered by its concrete type and overridable, OR use the `IRedisProjectionWriter` interface double (cleaner — it's already an interface and fires last in the chain). **Recommendation:** use a throwing `IRedisProjectionWriter` double for the forced throw — it's an interface (trivially substitutable) and sits after all validators, proving disposal survives a late-pipeline throw.

### Sampling Rate
- **Per task commit:** `dotnet build` (RMG drift guards + compile = the build-half check) + `dotnet test --filter "Phase=13"`.
- **Per wave merge:** `dotnet test --filter "FullyQualifiedName~Orchestration"` (Phase 9 + Phase 13 facts together — proves no regression on Start/Stop 204/400/404).
- **Phase gate:** full `dotnet test` green ×3 consecutive runs (Phase 3 D-18 cadence) + `psql \l` SHA-256 BEFORE=AFTER byte-identical + `redis-cli --scan` SHA-256 BEFORE=AFTER (no keys written this phase, so trivially stable).

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Features/Orchestration/WorkflowGraphLoaderFacts.cs` — SC3 (snapshot contents) + SC5 (fan-out + cycle termination). New file.
- [ ] `tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs` (or fold into above) — SC4 forced-throw `IsDisposed` assertion + throwing-writer double.
- [ ] Adapt existing `StartOrchestrationFacts` / `StopOrchestrationFacts` — they currently assert the shared `ValidateWorkflowIdsAsync` 404 contract; after the split, Start's existence check (D-08) must keep the SAME `string.Join(", ", missing)` 404 shape (the facts at lines 178 / 219 lock it). Verify these stay green; the `[Trait("Phase","9")]` facts are a regression guard, not new work.
- [ ] Confirm `[assembly: InternalsVisibleTo("BaseApi.Tests")]` exists on `BaseApi.Service` (needed for white-box loader resolution + internal seam doubles). If absent, add it.
- [ ] No framework install needed — xUnit + `Phase8WebAppFactory` already present.

## Security Domain

`security_enforcement` is not set in `.planning/config.json` → treated as **enabled** (Phase 12 research established this convention). However, Phase 13 introduces **no new external input surface, no new persistence write, no new network call, and no new auth boundary** — it is read-only against an existing schema with an existing validated input contract (`WorkflowIdsValidator`). The applicable ASVS surface is therefore minimal and already satisfied by inherited controls.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Auth on `/orchestration/*` is project-wide-deferred (out of scope per REQUIREMENTS Out-of-Scope); unchanged here. |
| V3 Session Management | no | Stateless API. |
| V4 Access Control | no | No new resource exposure; same endpoints. |
| V5 Input Validation | yes (inherited) | `WorkflowIdsValidator` (FluentValidation) already gates the `List<Guid>` body for non-empty / no-duplicate / no-`Guid.Empty`. P13 adds no new request fields. |
| V6 Cryptography | no | None. |
| V7 Error Handling / Logging | yes (inherited) | Existing IExceptionHandler chain → RFC 7807; `NotFoundException` id values are GUIDs only (safe to echo per NotFoundException XML doc IN-02). The new `LogDebug("L1 snapshot disposed")` line logs no entity data — keep it data-free. |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| SQL injection via id-set | Tampering | Parameterized EF Core LINQ (`ids.Contains` → parameterized `= ANY`); no string concatenation. Inherited, unchanged. |
| Information disclosure via error body | Information Disclosure | RFC 7807 bodies echo only GUIDs (`string.Join(", ", missing)`); no DB internals. Keep the disposal log line free of payloads/PII. |
| DoS via cyclic graph (infinite loop) | Denial of Service | `visited` `List<Guid>` guard guarantees BFS termination on cyclic input (L1-BUILD-04) — directly relevant: without it a malicious/broken cyclic workflow would hang the request thread. |
| DoS via huge graph | Denial of Service | Out of P13 scope (no payload-size gate beyond existing 1MB Assignment payload cap). Note for Phase 14+ if needed; do not build now. |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Assignment.SchemaId` (direct schema coupling) | `Processor.ConfigSchemaId` (schema moved to processor); `AssignmentEntity` = `(StepId, Payload)` only | Phase 10 (v3.2.0) | Loader collects assignments via `WorkflowAssignments` junction, not via any schema field; payload↔schema resolution is Step→Processor→ConfigSchema (Phase 14 concern). |
| Start/Stop share `ValidateWorkflowIdsAsync` | StopAsync extracted (D-07); StartAsync becomes orchestrator | Phase 13 (this) | Honors Phase 9 D-09 "refactor when they diverge". |
| 5 mappers pre-injected unused on `OrchestrationService` | Mappers relocated into `IWorkflowGraphLoader` and actually used | Phase 13 (this) | Resolves the Phase 9 D-05 "pre-injected, unused" smell; drops 5 `[SuppressMessage(IDE0052)]` attrs. |

**Deprecated/outdated:** None relevant. (Project is on EF Core 8 / .NET 8 — no version migration in scope.)

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `[assembly: InternalsVisibleTo("BaseApi.Tests")]` exists on `BaseApi.Service` (Phase 8/12 tests reach internal types so it is highly likely present) — planner MUST verify before relying on white-box test access. | Validation Architecture / Pitfall 3 | If absent, the SC3 white-box loader resolution + internal seam doubles won't compile; planner must add the attribute (one line) or use black-box `POST /start` assertions for SC3. LOW risk, trivially fixable. |
| A2 | xUnit v3 (inferred from `TestContext.Current.CancellationToken` in existing facts) — assumed for new test file authoring. | Validation Architecture | If v2, the `TestContext.Current` pattern differs; new facts must match whatever the existing facts use (copy the existing pattern verbatim). Negligible risk — copy existing fact structure. |
| A3 | The forward-compatible `IRedisProjectionWriter.UpsertAsync` signature `Task UpsertAsync(WorkflowGraphSnapshot, CancellationToken)` is sufficient for Phase 15 without re-wiring; Phase 15 adds `correlationId` either as a param or via the snapshot. | Code Examples / no-op writer | If Phase 15 needs correlationId at the writer boundary and it's not threadable, a one-param signature change is needed in Phase 15 (minor, isolated). Acceptable per D-01 "fill bodies, zero re-wiring" intent — the *method existence* is what matters; param tweak is low-cost. |

**Note:** A1 is the only assumption a planner must actively confirm (one `rg "InternalsVisibleTo" src/BaseApi.Service`). A2/A3 are self-resolving by copying existing patterns / are Phase 15's concern.

## Open Questions

1. **Logger placement for the `Dispose()` debug line (D-04).**
   - What we know: D-04 requires `ILogger.LogDebug("L1 snapshot disposed")`. The snapshot is a pure-data record (D-03).
   - What's unclear: whether the log line lives inside `Dispose()` (snapshot needs an injected `ILogger`, slightly muddying "pure data") or is emitted by the orchestrator/loader around disposal.
   - Recommendation: Planner's call. Cleanest: have the loader pass an `ILogger` into the snapshot, OR log from `StartAsync` immediately after the `using` scope is set up isn't possible (disposal is implicit) — so either inject the logger into the snapshot, or have the orchestrator catch the dispose via a small wrapper. Lean toward injecting `ILogger` into the snapshot record (it stays "data + one diagnostic line"); the `IsDisposed` flag is the test's actual assertion target, not the log line.

2. **Whether StartAsync's existence-check (D-08) and StopAsync share a private helper.**
   - What we know: D-07 says Start/Stop no longer share a *method*; D-08 keeps the existence check as Start's first step; the logic is identical to StopAsync's body.
   - What's unclear: duplicate the ~6 lines or extract a private `ExistenceCheckAsync` helper called by both.
   - Recommendation: Extract a private helper (not a public shared method) — satisfies D-07 (distinct public Start/Stop surfaces) while avoiding duplication. Planner decides; either is compliant.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8 | build/test | ✓ (project builds) | 8.x | — |
| Postgres (testcontainer / compose) | integration facts (`PostgresFixture`) | ✓ | per `PostgresFixture` | — |
| Redis (testcontainer / compose) | `Phase8WebAppFactory` boot (Phase 12 `RedisFixture`) | ✓ | 7.4.x-alpine | — |
| Mapperly source generator | build | ✓ | 4.x | — |

**Missing dependencies with no fallback:** None.
**Missing dependencies with fallback:** None. (No new tooling introduced this phase.)

## Sources

### Primary (HIGH confidence — live code read this session)
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — 92-LOC service; `ValidateWorkflowIdsAsync` AsNoTracking pattern; 5 pre-injected mappers + IDE0052 suppressions.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` — `AddScoped<OrchestrationService>()`; where 5 new registrations go.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` — concrete-on-concrete injection; Start/Stop endpoints; `[ProducesResponseType]`.
- `src/BaseApi.Service/Features/Step/StepEntityMapper.cs`, `WorkflowEntityMapper.cs` — `[MapValue(..., null)]` proving null junction collections (D-06 driver).
- `src/BaseApi.Service/Features/Step/StepService.cs` — `SyncJunctionsAsync` junction read/write template.
- `src/BaseApi.Service/Features/Step/StepNextSteps.cs`, `Workflow/WorkflowEntrySteps.cs`, `Workflow/WorkflowAssignments.cs` — 3 join entities (scalar FK pairs, no nav props).
- `src/BaseApi.Service/Features/{Step,Workflow,Processor,Schema,Assignment}/*Dtos.cs` — all 5 positional ReadDto shapes; **Assignment = `(StepId, Payload)`, Processor = Input/Output/ConfigSchemaId**.
- `src/BaseApi.Service/AppDbContext.cs` — 5 entity + 3 junction DbSets; `Set<T>()` access confirmed.
- `src/BaseApi.Core/Persistence/BaseDbContext.cs` — `Set<T>()` inherited from `DbContext`; snake_case + xmin.
- `src/BaseApi.Service/Persistence/Configurations/StepNextStepsConfiguration.cs` — junction PK `(StepId, NextStepId)`; children-of-S = `Where(StepId == S).Select(NextStepId)`.
- `src/BaseApi.Core/Mapping/IEntityMapper.cs` — 3-method `ToEntity/Update/ToRead` contract.
- `src/BaseApi.Core/Exceptions/{NotFoundException.cs, Handlers/ValidationExceptionHandler.cs, Handlers/DbUpdateExceptionHandler.cs}` — 404/400/422 mapping mechanisms (422 already exists for FK violations; new validation 422 is a Phase 14 concern).
- `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — real Postgres + Redis integration fixture; `ConfigureWebHost`; `RedisMultiplexer` access.
- `tests/BaseApi.Tests/Features/Orchestration/{Start,Stop}OrchestrationFacts.cs` — existing 204/400/404 facts + the locked `string.Join(", ", missing)` error-shape assertions (regression guard after the split).
- `.planning/REQUIREMENTS.md`, `.planning/ROADMAP.md` (§Phase 13), `.planning/phases/13.../13-CONTEXT.md` — locked requirements, success criteria, v3.2.0 invariants.

### Secondary (MEDIUM confidence)
- `.planning/phases/12-.../12-RESEARCH.md` — `security_enforcement` "absent = enabled" convention; Redis Singleton multiplexer / per-call IDatabase decisions (informs the no-op writer's restraint).

### Tertiary (LOW confidence)
- None. No external/web sources were needed — the phase is fully determined by the local codebase and locked decisions.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; every API verified in live code.
- Architecture: HIGH — seam layout dictated by locked decisions D-01..D-08; orchestrator skeleton derived from the existing service.
- Loader staging / traversal: HIGH — junction PK direction, no-nav-property constraint, and AsNoTracking pattern all verified; BFS shape is forced by the data model.
- Pitfalls: HIGH — each pitfall traced to a specific verified code fact (Assignment has no WorkflowId; record+IDisposable interaction; internal visibility).
- Validation architecture: HIGH for the mechanism (Phase8WebAppFactory + ConfigureTestServices); A1 (`InternalsVisibleTo`) is the one item to confirm.

**Research date:** 2026-05-29
**Valid until:** 2026-06-28 (stable — internal-codebase-driven; no fast-moving external dependency).
