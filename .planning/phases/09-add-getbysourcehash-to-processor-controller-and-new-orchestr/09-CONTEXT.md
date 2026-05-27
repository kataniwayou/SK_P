# Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop - Context

**Gathered:** 2026-05-28
**Status:** Ready for planning

<domain>
## Phase Boundary

Add one new endpoint on `ProcessorsController` (`GetBySourceHash`) and create a new `Features/Orchestration/` feature folder containing `OrchestrationController` with two endpoints (`StartOrchestration`, `StopOrchestration`). v1 orchestration endpoints **validate only** (duplicates + non-empty + non-`Guid.Empty` + existence of every Workflow id) and return **`204 No Content`** on success — no entity projection, no response payload, no orchestration side-effects. The Orchestration feature folder is built design-mirror to the existing 5 feature folders so future phases can add real orchestration logic + cross-entity endpoints without churning the v1 surface.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**6 requirements are locked.** See `09-SPEC.md` for full requirements, boundaries, and acceptance criteria.

Downstream agents MUST read `09-SPEC.md` before planning or implementing. Requirements are not duplicated here.

> **SPEC.md was amended 2026-05-28 (during discuss-phase Area 4):** Requirements 3 + 4 originally locked `200 OK + List<WorkflowReadDto>`; revised to `204 No Content`. v1 endpoint behavior is validation-only. Out-of-scope item "A new OrchestrationReadDto" expanded to "A response body of any kind on Orchestration endpoints". One Constraints bullet rewritten.

**In scope (from SPEC.md):**
- One new method on `ProcessorsController` (`GetBySourceHash`)
- One new `ProcessorService.GetBySourceHashAsync` method (delegates to the repository / DbContext for a `WHERE source_hash = …` lookup)
- New `Features/Orchestration/` folder with `OrchestrationController + OrchestrationService + OrchestrationServiceCollectionExtensions`
- Two new endpoints on `OrchestrationController` (`StartOrchestration`, `StopOrchestration`), behaviorally identical in v1
- `Program.cs` updated to call `AddOrchestrationFeature()`
- Duplicate-id and existence validation via existing Phase 4/6 exception-handler pipeline (no new error shapes)
- Integration tests in `tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs` and `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` + `StopOrchestrationFacts.cs` covering hit, miss, duplicate, and missing-id paths

**Out of scope (from SPEC.md):**
- Actual orchestration side-effects (queueing/runners)
- New domain entities, DB tables, or EF migrations
- Auth / authorization
- Persisting Start/Stop audit history
- Idempotency keys / retry semantics
- Route-layer validation of sourceHash format
- A request DTO around the Orchestration `List<Guid>` body
- Diverging Start vs Stop behavior in v1
- **A response body of any kind on Orchestration endpoints** (amended — was previously "a new OrchestrationReadDto")

</spec_lock>

<decisions>
## Implementation Decisions

### Processor.GetBySourceHash data access (Area 1)

- **D-01:** `GetBySourceHash` lookup lives on `ProcessorService` as a new `GetBySourceHashAsync(string sourceHash, CancellationToken ct)` method that queries `DbContext.Set<ProcessorEntity>().FirstOrDefaultAsync(p => p.SourceHash == sourceHash, ct)`. Mirrors the Phase 3 D-04 doc-comment guidance ("junction entities accessed via raw `DbContext.Set<T>()` from the Service") — `IRepository<T>`'s 5-method surface stays untouched. Service throws `NotFoundException(nameof(ProcessorEntity), sourceHash)` on miss; Phase 4 `NotFoundExceptionHandler` returns 404.
- **D-02:** `GetBySourceHashAsync` uses `.AsNoTracking()` on the EF query. Read-only intent makes this the correct shape; small, deliberate divergence from `BaseService.GetByIdAsync` (which uses default tracking via `_repo.GetAsync`). Performance impact at this scale is negligible — the choice is semantic.
- **D-03:** `ProcessorsController` gains a single new method `GetBySourceHash` with `[HttpGet("by-source-hash/{sourceHash}")]`. Lives on the concrete controller, not on `BaseController<>` (SourceHash is processor-specific; promoting to the base would require an `IHasSourceHash` marker interface — overkill for one endpoint). No route-level regex constraint on `{sourceHash}` — off-format strings 404 via row-miss (Phase 3 D-04 / SPEC.md Constraint).

### OrchestrationService composition (Area 2)

- **D-04:** `OrchestrationService` injects `BaseDbContext` **directly** (Option 3) — NOT `BaseService<WorkflowEntity, …>` and NOT `IRepository<WorkflowEntity>`. Rationale: the v1 path is a **batch read** (`List<Guid>` → existence check); a single SQL `WHERE id IN (…)` is the minimum-surface shape and avoids the N-query pattern that would result from looping `GetByIdAsync`. Future v2 endpoints with real side-effects keep the business logic in `OrchestrationService`.
- **D-05:** **All 5 entity mappers are injected in `OrchestrationService`'s ctor up front**, even though v1 uses zero of them (204 No Content means no entity projection). This is a deliberate "build for the second use" choice: the user has stated future phases will need all 5 entities, and injecting them now means future ctor surface is stable — future phases add methods, not ctor params. Mapper types (Mapperly-generated):
  - `IEntityMapper<SchemaEntity,    SchemaCreateDto,    SchemaUpdateDto,    SchemaReadDto>`
  - `IEntityMapper<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>`
  - `IEntityMapper<StepEntity,      StepCreateDto,      StepUpdateDto,      StepReadDto>`
  - `IEntityMapper<AssignmentEntity,AssignmentCreateDto,AssignmentUpdateDto,AssignmentReadDto>`
  - `IEntityMapper<WorkflowEntity,  WorkflowCreateDto,  WorkflowUpdateDto,  WorkflowReadDto>`

  **Known smell to accept:** v1 has 5 unused mapper dependencies. SPEC.md design-mirror principle and user's stated v2 intent justify it.
- **D-06:** `OrchestrationController` injects the concrete `OrchestrationService` directly — **no `IOrchestrationService` interface**. There is no generic base controller for orchestration to inherit from (it's not CRUD over an entity), so the Phase 7 Warning 7 abstract-base injection pattern doesn't apply. Concrete-on-concrete is the right shape here.

### Validation (Area 3)

- **D-07:** Duplicate-id validation goes through the **FluentValidation auto-discovery pipeline** (most consistent path — mirrors Phase 6 VALID-03 / `BaseService.CreateAsync` step 1). New file `src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs` contains `public sealed class WorkflowIdsValidator : AbstractValidator<IReadOnlyList<Guid>>`. Auto-discovered by `AddValidatorsFromAssembly` scan in `AddBaseApiValidation` (Phase 6). `OrchestrationService.ValidateWorkflowIdsAsync` calls `await _idsValidator.ValidateAndThrowAsync(ids, ct)` as **step 1** (mirroring `BaseService.CreateAsync` step 1 verbatim). Phase 4 `ValidationExceptionHandler` maps `ValidationException` → `400 ValidationProblemDetails`.
- **D-08:** Validator rules (in order):
  1. `RuleFor(ids => ids).NotNull().NotEmpty()` — reject `null` or `[]`
  2. `RuleFor(ids => ids).Must(NotHaveDuplicates).WithMessage("WorkflowIds must be unique.")`
  3. `RuleForEach(ids => ids).NotEqual(Guid.Empty).WithMessage("WorkflowIds must not contain Guid.Empty.")` (Phase 8 VALID-11 pattern)

  **No** `MaximumCount(N)` cap in v1 — outside SPEC.md scope; deferred.

- **D-09:** **Shared validator caveat (intentional)** — `AbstractValidator<IReadOnlyList<Guid>>` is one-of-a-kind in the codebase (all other validators target specific project DTO records). This validator is shared by Start AND Stop (both call the same service method). Acceptable because Start and Stop are behaviorally identical in v1 (SPEC.md D-08). If they diverge later, refactor to typed request records (e.g., `StartOrchestrationRequest` / `StopOrchestrationRequest`).
- **D-10:** **Existence check lives in the service**, after the validator: `var existingIds = await _db.Set<WorkflowEntity>().AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct);` then `var missing = ids.Except(existingIds).ToList();` and `if (missing.Count > 0) throw new NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing));`. **Lightweight projection** — only the `id` column is hydrated, no full `WorkflowEntity` materialization. `AsNoTracking()` consistent with Area 1 D-02.

### Response & endpoint behavior (Area 4)

- **D-11:** **Both endpoints return `204 No Content` on success** — no body. v1 behavior is validation-only. The endpoint runs the validator + existence check and returns `NoContent()` once both pass. SPEC.md Requirements 3 + 4 + Constraints + Acceptance Criteria amended 2026-05-28 to reflect this (was previously `200 OK + List<WorkflowReadDto>`).
- **D-12:** **Start and Stop delegate to the same service method** — `OrchestrationService.ValidateWorkflowIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)`. Since v1 behavior is identical, a single method backs both endpoints. The only divergence is the URL segment (`/start` vs `/stop`). Future phases that introduce real Start vs Stop divergence will split this into separate methods on `OrchestrationService`.

### Controller HTTP shape

- **D-13:** Controller is `[ApiController]` + `[ApiVersion("1.0")]` + `[Route("api/v{version:apiVersion}/[controller]")]` — same attribute stack as `BaseController<>`. The `[controller]` token resolves to `Orchestration` (class-name minus `Controller` suffix). Full routes: `POST /api/v1/orchestration/start`, `POST /api/v1/orchestration/stop`.
- **D-14:** `ProducesResponseType` attributes per endpoint:
  - `[ProducesResponseType(StatusCodes.Status204NoContent)]`
  - `[ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]`
  - `[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]`

  Mirrors the convention used by `BaseController.Delete` (also returns 204 + 404).
- **D-15:** Controller body is thin — single line per endpoint: `await _service.ValidateWorkflowIdsAsync(workflowIds, ct); return NoContent();`. No business logic in the controller.

### Feature folder layout

- **D-16:** New folder `src/BaseApi.Service/Features/Orchestration/` contains exactly these files:
  - `OrchestrationController.cs` — `public sealed class OrchestrationController : ControllerBase` with `Start` + `Stop` endpoints
  - `OrchestrationService.cs` — `public sealed class OrchestrationService` (concrete, no interface) with `ValidateWorkflowIdsAsync` method
  - `OrchestrationDtoValidator.cs` — `public sealed class WorkflowIdsValidator : AbstractValidator<IReadOnlyList<Guid>>`
  - `OrchestrationServiceCollectionExtensions.cs` — `internal static class OrchestrationServiceCollectionExtensions { public static IServiceCollection AddOrchestrationFeature(this IServiceCollection services) { ... } }`

  **Diff from the 5 existing feature folders:** no `OrchestrationEntity.cs`, no `OrchestrationDtos.cs` (only the validator file), no `OrchestrationEntityMapper.cs`. Orchestration is not CRUD-over-an-entity — it's a cross-entity composition over Workflow (and, in v2, the other 4 entities).
- **D-17:** `AddOrchestrationFeature()` body:
  ```csharp
  services.AddScoped<OrchestrationService>();
  return services;
  ```
  The validator (`WorkflowIdsValidator`) is picked up by `AddBaseApiValidation`'s `AddValidatorsFromAssembly` scan automatically — no manual registration needed (Phase 6 VALID-02 pattern). The 5 entity mappers are already registered by `AddBaseApiMapping` (Phase 6) — also no manual registration needed.
- **D-18:** `src/BaseApi.Service/Composition/AppFeatures.cs` `AddAppFeatures()` aggregator gains a 6th line: `services.AddOrchestrationFeature();` — appended after the 5 existing entity feature registrations. `Program.cs` is **unchanged** (it already calls `AddAppFeatures()`).

### Test layout

- **D-19:** Test files:
  - `tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs` — 2 facts (200 + miss/404 + optional malformed-hash 404)
  - `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` — happy path (204), duplicate (400), missing-id (404), empty-list (400), Guid.Empty (400)
  - `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs` — mirror of Start (smaller, since behavior is identical) OR `[Theory]`-parameterized over endpoint URL inside Start facts. Planner decides.

  **Planner discretion:** test file structure (one fact-per-method vs. `[Theory]` parameterization for the Start/Stop URL pairing).

### Test harness reuse

- **D-20:** **Reuse `Phase8WebAppFactory` + `PostgresFixture`** (Phase 8 D-08). No new `Phase9WebAppFactory`. Phase 9 adds zero schema changes (no migration), so the Phase 8 harness applies unchanged. Planner may add a per-class subclass only if a Phase 9-specific service override is required (none currently anticipated).

### Claude's Discretion

The planner may decide:
- Whether to use `[Theory]`-parameterization for the Start/Stop endpoint pair vs. duplicating facts across two files
- Whether to add a `MaximumCount(N)` rule later (not in v1 per D-08)
- The naming of facts (existing Phase 4-7 convention: `Verb_Behavior_Condition`)
- Whether to land a small `OrchestrationFeatureFolder` `internal sealed` access modifier vs. `public sealed` for the controller — match the closest existing precedent

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase 9 spec (locked requirements — must read first)
- `.planning/phases/09-add-getbysourcehash-to-processor-controller-and-new-orchestr/09-SPEC.md` — 6 locked requirements, boundaries, constraints, acceptance criteria. **Note:** amended 2026-05-28 — Requirements 3 + 4 + one Constraints bullet + one Out-of-scope bullet + 2 Acceptance Criteria checkboxes were rewritten when discuss-phase Area 4 changed the response shape from `200 OK + List<WorkflowReadDto>` to `204 No Content`.

### Existing patterns to mirror (Phase 7 + Phase 8)
- `src/BaseApi.Core/Controllers/BaseController.cs` — `GetById` design (HttpGet + `:guid` route constraint + `ProducesResponseType` attribute stack). Phase 9's `GetBySourceHash` mirrors the response shape (200 + entity / 404).
- `src/BaseApi.Core/Services/BaseService.cs` — `CreateAsync` step 1 (`ValidateAndThrowAsync`) is the canonical "validate first" pattern. `OrchestrationService.ValidateWorkflowIdsAsync` mirrors this verbatim.
- `src/BaseApi.Service/Features/Processor/ProcessorService.cs` — current `ProcessorService` shape (5-param ctor calling `: base(...)`). New `GetBySourceHashAsync` method added to this class.
- `src/BaseApi.Service/Features/Processor/ProcessorController.cs` — current empty-body concrete controller. New `GetBySourceHash` method added to this class.
- `src/BaseApi.Service/Features/Workflow/` — folder structure to mirror loosely. **Phase 9's Orchestration folder omits Entity + Dtos + Mapper** (not CRUD over an entity).
- `src/BaseApi.Service/Composition/AppFeatures.cs` — `AddAppFeatures()` aggregator. Add 6th line: `services.AddOrchestrationFeature();`

### Phase 4 exception-handler chain (load-bearing for error semantics)
- Phase 4 `NotFoundExceptionHandler` → `404 ProblemDetails` (used by Areas 1 + 3 + 4 error paths)
- Phase 4 `ValidationExceptionHandler` → `400 ValidationProblemDetails` (used by Area 3 validation failures)

### Phase 6 validation pipeline
- `AddBaseApiValidation` (Phase 6) — `AddValidatorsFromAssembly` scan auto-discovers `WorkflowIdsValidator`. No manual DI registration needed in `AddOrchestrationFeature()`.

### Phase 3 repository contract
- `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` — **EXACTLY 5 methods, no helpers** (Phase 3 D-04). New specialized lookups (like `GetBySourceHashAsync`) go to the Service via `DbContext.Set<T>()`, never extend `IRepository<T>`.

### Phase 8 test harness (reused)
- `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` — reuse as-is (Phase 8 D-08). No new `Phase9WebAppFactory` needed.
- `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` — per-class throwaway-DB pattern. Phase 9 reuses unchanged.

### Prior phase context (decisions to honor)
- `.planning/phases/08-entity-build-out-migrations-docker-runtime-tests/08-CONTEXT.md` — D-09 (per-entity feature folder layout), D-12 (concrete service marker class only for entities that override `SyncJunctionsAsync`), D-18 (3-consecutive-GREEN regression cadence)
- `.planning/phases/07-generic-http-base-composition-root/07-CONTEXT.md` — Warning 7 (controllers inject the abstract `BaseService<…>`, not the concrete). Phase 9 deviates intentionally: `OrchestrationController` injects the **concrete** `OrchestrationService` because there's no abstract base for orchestration to mirror.
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` — D-04 (`IRepository<T>` has exactly 5 methods; specialized lookups via `DbContext.Set<T>()`), D-15 (byte-identical `psql \l` BEFORE/AFTER cleanup discipline)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets

- **`BaseDbContext`** (Scoped via Phase 7 D-14 alias) — already DI-registered; `OrchestrationService` injects it directly.
- **`IEntityMapper<TEntity, TCreate, TUpdate, TRead>`** — Mapperly-generated implementations for all 5 entities already DI-registered via `AddBaseApiMapping` (Phase 6). All 5 mappers injectable into `OrchestrationService` ctor with zero new DI wiring.
- **`AddBaseApiValidation`** (Phase 6) — `AddValidatorsFromAssembly` scan automatically discovers any new `AbstractValidator<T>` in `BaseApi.Service`. `WorkflowIdsValidator` lands in the existing scan; zero new DI.
- **`NotFoundException`** (Phase 4) — `throw new NotFoundException(nameof(WorkflowEntity), string.Join(", ", missingIds))` reaches the Phase 4 `NotFoundExceptionHandler` → 404. Constructor signature: `(string entityName, object id)`.
- **`FluentValidation.ValidationException`** — thrown via `ValidateAndThrowAsync`; reaches Phase 4 `ValidationExceptionHandler` → 400.
- **`Phase8WebAppFactory` + `PostgresFixture`** — Phase 9 tests reuse both unchanged.
- **`AppFeatures.AddAppFeatures()`** — aggregator at `src/BaseApi.Service/Composition/AppFeatures.cs`. Append `services.AddOrchestrationFeature();` as the 6th line.

### Established Patterns

- **Per-entity feature folder layout (Phase 8 D-09):** Phase 9's Orchestration folder follows the spirit (one folder per feature, `XxxServiceCollectionExtensions` for DI wiring) but **does not contain Entity / Dtos / Mapper files** because Orchestration is not CRUD over an entity.
- **Controller class-name pluralization:** Phase 8 controllers are `SchemasController`, `ProcessorsController`, etc. — class name plus `Controller` suffix, with `[controller]` token resolving to the lowercase singular/plural matching the URL. **For Phase 9, `OrchestrationController` (singular, no `s`)** resolves to `/api/v1/orchestration/...` which matches the user's stated singular noun.
- **Validator as step 1 (Phase 6 VALID-03):** `BaseService.CreateAsync`/`UpdateAsync` call `validator.ValidateAndThrowAsync(dto, ct)` as the first action. `OrchestrationService.ValidateWorkflowIdsAsync` mirrors this exactly.
- **NotFoundException for missing entity (Phase 4 / Phase 3):** Single exception type for "entity not found" — `NotFoundException("WorkflowEntity", id)`. Phase 9 uses the same exception for both single-id misses and (via concatenated id strings) multi-id misses.
- **Concrete controller injecting `BaseService<…>` (Phase 7 Warning 7):** Phase 8 controllers inject the abstract `BaseService<…>`. **Phase 9 deviates intentionally** — `OrchestrationController` injects the **concrete** `OrchestrationService` because there's no abstract base for orchestration.

### Integration Points

- **`ProcessorsController.cs`** — gains a new method `GetBySourceHash`. Existing empty-body class becomes one-method class. CRUD verbs still inherited from `BaseController<>`.
- **`ProcessorService.cs`** — gains a new method `GetBySourceHashAsync`. Existing class structure unchanged.
- **`src/BaseApi.Service/Features/Orchestration/`** — new folder, 4 files.
- **`src/BaseApi.Service/Composition/AppFeatures.cs`** — single-line edit adding `services.AddOrchestrationFeature();`.
- **No edits to `BaseApi.Core`** — all Phase 9 work lives in `BaseApi.Service` (and `tests/BaseApi.Tests`).
- **No edits to `Program.cs`** — `AddAppFeatures()` is already called there.

</code_context>

<specifics>
## Specific Ideas

- **User's stated v2 intent (load-bearing for D-05):** "eventually the orchestration controller will need all entities (not in this phase)." This is the explicit justification for injecting all 5 entity mappers up front in v1 even though zero are used in v1.
- **User's clarified v1 scope (load-bearing for D-11/D-12 + SPEC.md amendment):** "the orchestrator start/stop get workflowids list then it validate the list. that all. it do the same to stop." → no entity projection, no response body, both endpoints identical.
- **User's stated design principle throughout:** "follow the same design as get by id" — interpreted as **HTTP-shape mirror** (single-method endpoint, 404 via NotFoundException, ProducesResponseType convention), not as a code-path mirror (the internal data-access paths may diverge where the batch-read shape calls for it).

</specifics>

<deferred>
## Deferred Ideas

- **Real orchestration side-effects** — actual Start/Stop semantics (queueing jobs, dispatching to external schedulers, persisting action state). Future phase. SPEC.md out-of-scope item.
- **Response body for Start/Stop** — when real orchestration logic lands, the response will likely change to per-id status (e.g., `[{ workflowId, status, jobId }]`). For now, 204 No Content is the honest shape.
- **Diverging Start vs Stop behavior** — currently they share `OrchestrationService.ValidateWorkflowIdsAsync`. When real semantics diverge, split into two methods.
- **`MaximumCount(N)` cap on the validator** — DoS guard. Outside SPEC.md v1 scope; defer until production traffic patterns inform a sensible limit.
- **Typed request records `StartOrchestrationRequest` / `StopOrchestrationRequest`** — currently the bare `List<Guid>` body shares one validator. If Start and Stop diverge later, refactor to per-endpoint typed records each with its own validator.
- **`OrchestrationReadDto`** — explicitly out of scope per SPEC.md amendment. Future endpoints that need a response body should design their own DTOs at that time.
- **Promoting `GetBySourceHash` to `BaseController<>` via an `IHasSourceHash` marker interface** — overkill for one endpoint on one entity. If future entities (none currently) gain a unique business-key lookup, revisit then.

</deferred>

---

*Phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr*
*Context gathered: 2026-05-28*
