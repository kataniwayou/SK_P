---
phase: 08-entity-build-out-migrations-docker-runtime-tests
plan: 02
subsystem: feature
tags: [schema, mapperly, fluentvalidation, jsonschema, jsonb, integration-test, valid-08, valid-09]

# Dependency graph
requires:
  - phase: 03-ef-core-persistence-base
    provides: BaseEntity (8 fields), BaseDbContext (snake_case + xmin shadow token), IRepository<T> (5-method surface)
  - phase: 04-cross-cutting-middleware-error-handling
    provides: NotFoundExceptionHandler (404), ValidationExceptionHandler (400 ProblemDetails), correlationId on every error
  - phase: 06-validation-mapping-base
    provides: IBaseDto, BaseDtoValidator<T>, IEntityMapper<TEntity,TCreate,TUpdate,TRead>, Mapperly RMG012/RMG020 promoted-to-error
  - phase: 07-generic-http-base-composition-root
    provides: BaseController<TEntity,TCreate,TUpdate,TRead> (5 verbs + URL-segment versioning), BaseService<...> (locked 6-step CreateAsync), AddBaseApi<TDbContext>, IHasId marker
  - plan: 08-01
    provides: Phase8WebAppFactory (encapsulated PostgresFixture, public-class-not-sealed), 4 PackageReferences (Riok.Mapperly, EFCore.Design, JsonSchema.Net, Cronos) on BaseApi.Service.csproj
provides:
  - SchemaEntity : BaseEntity with Definition jsonb-bound string (PERSIST-08)
  - 3 SchemaDtos (SchemaCreateDto + SchemaUpdateDto + SchemaReadDto) — Read carries Id + 4 audit fields per HTTP-07
  - SchemaEntityMapper [Mapper] partial — 10 [MapperIgnoreTarget] (5×ToEntity, 5×Update); ZERO [MapperIgnoreSource] on ToRead (audit-symmetric)
  - SchemaService — passthrough ctor, no junctions (Schema has no M2M)
  - SchemasController : BaseController — empty body; URL /api/v1/schemas via [controller] token
  - SchemaEntityConfiguration — Definition .IsRequired().HasColumnType("jsonb")
  - SchemaServiceCollectionExtensions.AddSchemaFeature — concrete service + abstract-base alias DI registration
  - SchemaCreateDtoValidator + SchemaUpdateDtoValidator — VALID-20 inheritance + VALID-08 meta-validation + VALID-09 SSRF guard
  - SchemasIntegrationTests — 5 smoke facts (List/Create/GetById/Update/Delete)
affects:
  - Wave C 08-07 — owns the DI composition (AddAppFeatures aggregator) that wires AddSchemaFeature() so SchemasController DI activates
  - Wave C 08-07 — owns AppDbContext.DbSet<SchemaEntity> + the OnModelCreating.ApplyConfigurationsFromAssembly so SchemaEntityConfiguration takes effect
  - Wave C 08-08 — 3-consecutive-run regression proves the 5 integration tests GREEN

# Tech tracking
tech-stack:
  added: []     # all packages already added in Wave A 08-01
  patterns:
    - "ReadDto carries Id + 4 audit fields per HTTP-07 → no [MapperIgnoreSource] on ToRead (audit-symmetric)"
    - "10 [MapperIgnoreTarget] attributes on ToEntity+Update enforces strict Mapperly drift detection on the 5 server-controlled fields"
    - "JsonSchema.Net static-ctor pattern: Dialect.Default = Draft202012 + SchemaRegistry.Global.Fetch = (_,_) => null — VALID-08 dialect pin + VALID-09 SSRF defense-in-depth in ONE place"
    - "FluentValidation Custom rule parses JsonDocument then evaluates against MetaSchemas.Draft202012 (try/finally Dispose for JsonDocument lifecycle)"
    - "Per-entity DI extension method (AddSchemaFeature) registers concrete SchemaService + abstract-base BaseService<...> alias — load-bearing because SchemasController injects the abstract"
    - "Marker controller pattern: empty body inherits 5 verbs; URL via [controller] token strips Controller suffix → /api/v1/schemas"

key-files:
  created:
    - src/BaseApi.Service/Features/Schema/SchemaEntity.cs
    - src/BaseApi.Service/Features/Schema/SchemaDtos.cs
    - src/BaseApi.Service/Features/Schema/SchemaEntityMapper.cs
    - src/BaseApi.Service/Features/Schema/SchemaDtoValidator.cs
    - src/BaseApi.Service/Features/Schema/SchemaController.cs
    - src/BaseApi.Service/Features/Schema/SchemaService.cs
    - src/BaseApi.Service/Features/Schema/SchemaServiceCollectionExtensions.cs
    - src/BaseApi.Service/Persistence/Configurations/SchemaEntityConfiguration.cs
    - tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs
  modified: []

key-decisions:
  - "ReadDto carries Id + 4 audit fields per HTTP-07 → ToRead method has ZERO [MapperIgnoreSource] attributes (audit-symmetric; CONTEXT D-08 amended Claude's Discretion, PATTERNS §C lines 188-189)"
  - "SchemaServiceCollectionExtensions per-entity DI module (NOT direct registration in AddBaseApi) — preserves BaseApi.Core unawareness of concrete entity types; Wave C 08-07 composes the 5 per-entity extensions into AddAppFeatures()"
  - "Static-ctor configuration of Dialect.Default + SchemaRegistry.Global.Fetch lives on SchemaCreateDtoValidator only — per-type static state is initialized on first touch of EITHER validator type since both participate in the AddBaseApiValidation assembly scan"
  - "Test class accepts Wave B isolation: integration facts fail at HTTP 500 (DI resolve InvalidOperationException) because Wave C 08-07 has not yet wired AddSchemaFeature() into Program.cs — documented design per plan Task 2 design note; GREEN-state verified by 08-08 regression after Wave C ships"
  - "[Trait(\"Phase8Wave\", \"B\")] on the test class enables developers to filter or skip-with-awareness during Wave B development; xUnit Traits do NOT need to match conventional names — they are arbitrary key/value pairs"

patterns-established:
  - "Pattern: Schema feature folder layout — 6 production files (Entity, Dtos, Mapper, Service, Controller, DI extensions) + 1 EF config file in sibling Persistence/Configurations/. Plans 08-03..08-06 will mirror this layout exactly."
  - "Pattern: Static-ctor side-effect for library configuration (Dialect.Default + SchemaRegistry.Global.Fetch) — runs once per AppDomain on first validator construction; safe even when validators are Scoped because the static-ctor fires before any instance ctor body. Phase 8 reuses the same pattern for VALID-08/09."
  - "Pattern: Per-entity ServiceCollectionExtensions module (internal static class with single AddXxxFeature method) — uniform across 5 entities; Wave C 08-07 AddAppFeatures() aggregator composes them. Plans 08-03..08-06 use the identical shape."

requirements-completed:
  - ENTITY-03
  - ENTITY-09
  - ENTITY-10
  - PERSIST-08
  - PERSIST-13
  - HTTP-04
  - HTTP-05
  - HTTP-06
  - HTTP-07
  - HTTP-11
  - HTTP-12
  - VALID-08
  - VALID-09
  - VALID-20
  - TEST-05

# Metrics
duration: 5min
completed: 2026-05-27
---

# Phase 8 Plan 02: Schema Feature Folder + Smoke Integration Tests Summary

**Schema is the FK-root entity of the 5-entity graph; this plan ships the complete feature folder (6 production files + 1 EF config + 1 DI extension) + the validator (VALID-08 meta-validation + VALID-09 SSRF guard) + 5 smoke integration tests against the production AppDbContext (GREEN-state pending Wave C 08-07 DbSet wiring + AddSchemaFeature DI registration).**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-05-27T19:19:58Z
- **Completed:** 2026-05-27T19:24:56Z
- **Tasks:** 2
- **Files created:** 9 (7 production + 1 test class + 0 modifications)
- **Files modified:** 0

## Accomplishments

- **Schema feature folder complete (7 production files):** All 6 paths in `src/BaseApi.Service/Features/Schema/` plus 1 EF config file in `src/BaseApi.Service/Persistence/Configurations/`. `SchemaEntity : BaseEntity` adds `string Definition` (jsonb-bound via `SchemaEntityConfiguration`). `SchemaDtos.cs` ships 3 sealed records: `SchemaCreateDto` + `SchemaUpdateDto` (4 positional params each, IBaseDto only) and `SchemaReadDto` (9 positional params: Id + 8 from the entity, IBaseDto + IHasId). `SchemaEntityMapper` is a `[Mapper] sealed partial class` with EXACTLY 10 `[MapperIgnoreTarget]` (5 on ToEntity + 5 on Update) and ZERO `[MapperIgnoreSource]` (ReadDto carries audit fields per HTTP-07 → ToRead is symmetric — Plan 06-CONTEXT D-08 amended). `SchemaService` is a marker passthrough with no body. `SchemasController : BaseController<...>` is an empty-body subclass injecting the ABSTRACT `BaseService<SchemaEntity,...>` per Phase 7 Warning 7 option b. `SchemaEntityConfiguration` calls `entity.Property(e => e.Definition).IsRequired().HasColumnType("jsonb")` for PERSIST-08.

- **DI composition wired (1 extension file):** `SchemaServiceCollectionExtensions.AddSchemaFeature()` registers concrete `SchemaService` + the abstract-base `BaseService<SchemaEntity,SchemaCreateDto,SchemaUpdateDto,SchemaReadDto>` alias. The alias is LOAD-BEARING because `SchemasController.ctor` injects the abstract type. Wave C 08-07 composes this with `AddProcessorFeature/AddStepFeature/AddAssignmentFeature/AddWorkflowFeature` into a single `AddAppFeatures()` aggregator invoked from `Program.cs`.

- **VALID-08 + VALID-09 implementation:** `SchemaCreateDtoValidator.static ctor` performs the two security-critical assignments: `Dialect.Default = Dialect.Draft202012` (VALID-08 — JsonSchema.Net library default is V1, not 2020-12) and `SchemaRegistry.Global.Fetch = (_, _) => null` (VALID-09 SSRF defense-in-depth — library default is already no-op, explicit assignment encodes intent so future package upgrades that change the default surface here, not in production). Both validators use FluentValidation `.Custom((definition, ctx) => {...})` rule that (1) parses the input as `JsonDocument` with try/catch → field-level "Definition is not valid JSON" failure on `JsonException`, then (2) evaluates the parsed document against `MetaSchemas.Draft202012.Evaluate` with `OutputFormat.List` → field-level "Definition is not a valid JSON Schema (draft 2020-12)" failure on `!results.IsValid`. JsonDocument disposal in `finally` block. Both validators `Include(new BaseDtoValidator<T>())` for VALID-20 inheritance.

- **5 smoke integration tests authored:** `SchemasIntegrationTests : IClassFixture<Phase8WebAppFactory>` ships exactly 5 `[Fact]` methods named per CONTEXT D-07 `Verb_Behavior_Condition`: `List_ReturnsEmptyArray_OnEmptyDb`, `Create_Returns201_AndLocationHeader_WhenValid`, `GetById_Returns200_WhenExisting`, `Update_Returns200_AndChangedFields_WhenExisting`, `Delete_Returns204_WhenExisting`. EVERY async call passes `TestContext.Current.CancellationToken` (xUnit1051 + TreatWarningsAsErrors=true). Helper method `NewValidCreateDto(suffix)` generates per-fact unique names with a valid `$schema`+`type:object` JSON Schema definition.

- **HTTP-07 audit population verified by test assertion:** `Create_Returns201_AndLocationHeader_WhenValid` asserts `read.CreatedAt != default` AND `read.UpdatedAt != default` after the POST roundtrip — proves the Mapperly ToRead method maps audit fields from entity to DTO. (The GREEN-state assertion will activate after Wave C 08-07 wires the DI composition.)

- **Mapperly RMG012/RMG020 drift detection still LIVE:** Build succeeds with 0 warnings on both Release configurations confirming Phase 6 D-08 amended drift detection holds: adding an unmapped property to `SchemaEntity` would fire RMG012 on ToEntity+Update AND RMG020 on ToRead (because no `[MapperIgnoreSource]` attributes exist on ToRead). 10 `[MapperIgnoreTarget]` + 0 `[MapperIgnoreSource]` is the canonical pattern for Phase 8 entities whose ReadDto carries audit fields.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Schema feature folder (7 files)** — `4912a83` (feat)
2. **Task 2: SchemaDtoValidator + 5 smoke integration tests** — `0903729` (feat)

**Plan metadata commit:** to follow this SUMMARY.md write (docs(08-02): complete Schema feature).

## Files Created/Modified

### Created (9)

**Production code (8 files):**
- `src/BaseApi.Service/Features/Schema/SchemaEntity.cs` — `public sealed class SchemaEntity : BaseEntity { public string Definition { get; set; } = string.Empty; }`
- `src/BaseApi.Service/Features/Schema/SchemaDtos.cs` — 3 sealed records (Create + Update are IBaseDto; Read is IBaseDto + IHasId carrying Id + 8 fields total)
- `src/BaseApi.Service/Features/Schema/SchemaEntityMapper.cs` — `[Mapper] public sealed partial class` with 10 `[MapperIgnoreTarget]` and 0 `[MapperIgnoreSource]`
- `src/BaseApi.Service/Features/Schema/SchemaDtoValidator.cs` — 2 validators (Create + Update); static ctor sets Dialect.Default + SchemaRegistry.Global.Fetch; Custom Definition rule uses MetaSchemas.Draft202012.Evaluate
- `src/BaseApi.Service/Features/Schema/SchemaService.cs` — `public sealed class SchemaService : BaseService<...>` passthrough ctor
- `src/BaseApi.Service/Features/Schema/SchemaController.cs` — `public sealed class SchemasController : BaseController<...>` empty-body subclass
- `src/BaseApi.Service/Features/Schema/SchemaServiceCollectionExtensions.cs` — internal static class with `AddSchemaFeature()` registering concrete + abstract-base alias
- `src/BaseApi.Service/Persistence/Configurations/SchemaEntityConfiguration.cs` — internal sealed `IEntityTypeConfiguration<SchemaEntity>` with `Definition .IsRequired().HasColumnType("jsonb")`

**Test code (1 file):**
- `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` — `[Trait("Phase8Wave", "B")] public sealed class SchemasIntegrationTests : IClassFixture<Phase8WebAppFactory>` with 5 `[Fact]` methods

### Modified

*(none — Wave B isolation contract: this plan adds files only; DI composition + AppDbContext mutations are Wave C 08-07's responsibility)*

## Decisions Made

- **ReadDto-with-audit drives ToRead audit-symmetric.** Plan body and Phase 6 D-08 amended (Claude's Discretion, PATTERNS §C lines 188-189) both flag that when the ReadDto carries audit fields (HTTP-07 mandates Id + audit), the Mapperly ToRead method needs ZERO `[MapperIgnoreSource]` because every entity-side property maps to a DTO-side member. 10 `[MapperIgnoreTarget]` (5 on ToEntity + 5 on Update) + 0 `[MapperIgnoreSource]` is the canonical pattern.

- **Per-entity DI extension module (NOT direct registration in BaseApi.Core).** BaseApi.Core cannot reference concrete entity types (would invert the dependency graph). The Plans 08-02..08-06 pattern is one `internal static class XxxServiceCollectionExtensions { public static IServiceCollection AddXxxFeature(...) }` per entity. Wave C 08-07's `Composition/AppFeatures.cs` aggregator composes the 5 per-entity extensions into a single `AddAppFeatures()` invoked from `Program.cs` after `AddBaseApi<AppDbContext>(...)`.

- **Static-ctor configuration on SchemaCreateDtoValidator only.** Per-type static state initializes once on first touch of EITHER validator type since both `SchemaCreateDtoValidator` and `SchemaUpdateDtoValidator` participate in the same `AddBaseApiValidation` assembly scan (Phase 6 D-16). The library-wide assignments (`Dialect.Default`, `SchemaRegistry.Global.Fetch`) need only one assignment site, not two. Documented in the SchemaUpdateDtoValidator XML doc-comment.

- **[Trait("Phase8Wave", "B")] on the test class.** xUnit Traits are arbitrary key/value pairs — using a Phase8Wave trait makes Wave B isolation visible to developers running `--filter-trait Phase8Wave=B` and to the Wave C 08-08 plan that explicitly runs the SchemasIntegrationTests after the DI composition lands.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Build error] SchemaEntityConfiguration.cs XML cref to BaseDbContext unresolvable**
- **Found during:** Task 1 first `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Release`
- **Issue:** CS1574 "XML comment has cref attribute 'BaseDbContext' that could not be resolved" — the file does not declare a `using BaseApi.Core.Persistence;` directive (it doesn't need one — `IEntityTypeConfiguration` and `EntityTypeBuilder` come from `Microsoft.EntityFrameworkCore.*`), so the `<see cref="BaseDbContext"/>` XML reference couldn't bind.
- **Fix:** Changed `<see cref="BaseDbContext"/>` to `<c>BaseDbContext</c>` (non-binding code-literal markup that documents the design intent without requiring the type to be in scope). Adding a `using BaseApi.Core.Persistence;` directive would be an alternative but creates an unused import warning under TreatWarningsAsErrors=true. The `<c>` markup preserves the doc-comment educational content.
- **Files modified:** `src/BaseApi.Service/Persistence/Configurations/SchemaEntityConfiguration.cs`
- **Verification:** `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Release` succeeds with 0 warnings 0 errors after the fix
- **Committed in:** `4912a83` (part of Task 1 commit — fixed before Task 1 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — XML cref binding error)
**Impact on plan:** Fix preserves doc-comment intent without adding an unused namespace import. No scope creep; no architectural changes.

## Issues Encountered

**Wave B isolation expected runtime failure (NOT a regression).** The 5 SchemasIntegrationTests fail at HTTP 500 with `InvalidOperationException: Unable to resolve service for type 'BaseService<SchemaEntity,...>' while attempting to activate 'SchemasController'`. This is the **documented and accepted Wave B isolation contract** per the plan's Task 2 design note: *"the SchemasIntegrationTests in Task 2 will FAIL at boot until Wave C 08-07 adds the service registrations. Task 2 acceptance criteria reflect that the build must succeed but the 5 facts will go GREEN only after Wave C completes."* The tests load, the controllers route, the action method binds — only the DI activation throws because `AddSchemaFeature()` is not yet called from `Program.cs`. Wave C 08-07 wires `AddAppFeatures()`; Wave C 08-08 verifies all 5 facts go GREEN in a 3-consecutive-run regression.

The `[Trait("Phase8Wave", "B")]` marker on the test class enables `dotnet test --filter "Phase8Wave!=B"` (or equivalent MTP filter syntax) to skip these facts during Wave B development without breaking the Phase 1-7 regression count.

## Threat Model Compliance

All Plan 08-02 mitigate-disposition threats addressed:

| Threat ID | Disposition | Status | Verification |
|-----------|-------------|--------|--------------|
| T-08-02-SSRF-DOLLAR-REF | mitigate | DONE | `SchemaRegistry.Global.Fetch = (_, _) => null` explicit assignment in static ctor (VALID-09 defense-in-depth); library default is already no-op. SSRF fact test sending `{"$ref":"https://attacker.example/..."}` lives in Plan 08-08 and will pass because IsValid=false (no fetch happens). |
| T-08-02-INVALID-SCHEMA-ACCEPTED | mitigate | DONE | `MetaSchemas.Draft202012.Evaluate` confirms user-supplied definition IS itself a valid JSON Schema (not just valid JSON). `Dialect.Default = Dialect.Draft202012` explicitly set per VALID-08 / RESEARCH Pitfall 2. |
| T-08-02-DOS-LARGE-DEFINITION | accept | DEFERRED v2 | v1 trusts validator; jsonb has no row-size limit; .NET JsonDocument has implicit OOM bounds. v2 may add explicit MaxLength. |
| T-08-02-CONCURRENT-WRITE-LOST-UPDATE | mitigate (transitive) | INHERITED | Phase 3 PERSIST-16 xmin shadow concurrency token + Phase 4 DbUpdateConcurrencyException → 409 mapping; no Plan 08-02 test required. |
| T-08-02-MASS-ASSIGNMENT-AUDIT | mitigate | DONE | DTOs omit Id + 4 audit fields per HTTP-05 + HTTP-06 (source-side guard); Mapperly RMG012/RMG020 promoted to error catches drift at build time (10 `[MapperIgnoreTarget]` verified). |

## User Setup Required

None — Phase8WebAppFactory uses the same `localhost:5433` Postgres container already running for Phases 3-7; no additional external service configuration required.

## Next Phase Readiness

**Plans 08-03 (Processor), 08-04 (Step), 08-05 (Assignment), 08-06 (Workflow) unblocked.** All 4 plans can run in parallel per Phase 8 Wave B isolation contract — no Phase 8 plan depends on any other Phase 8 entity's migration (InitialCreate lands in Wave C 08-07 after ALL Wave B entities ship). Each plan mirrors the Schema feature folder layout (6 production files + 1 EF config + 1 ServiceCollectionExtensions + 1 integration test class with 5 facts).

**Wave C 08-07 (AppDbContext + InitialCreate migration + AddAppFeatures composition) unblocked at the file-availability level.** SchemaEntity, SchemaEntityConfiguration, and SchemaServiceCollectionExtensions.AddSchemaFeature are now committed and discoverable by:
- `AppDbContext.OnModelCreating.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly)` (Wave C 08-07 Task 1) to pick up `SchemaEntityConfiguration`
- `Composition/AppFeatures.AddAppFeatures()` (Wave C 08-07 Task 2) to compose `services.AddSchemaFeature()` into the production DI graph

**Wave C 08-08 (cross-entity regression) unblocked.** `SchemasIntegrationTests` is committed with the canonical 5-fact shape; 08-08 will run all 25 facts (5 per entity × 5 entities) plus 4 migration-failure facts in a single 3-consecutive-run regression to prove GREEN-state stability.

**Phase 1-7 regression preserved.** 98/98 facts GREEN; the 5 new SchemasIntegrationTests fail at runtime per Wave B isolation contract (documented and accepted; not a regression).

## Self-Check

Verification of claims before final commit:

**Created files (verified via `[ -f ... ]`):**
- `src/BaseApi.Service/Features/Schema/SchemaEntity.cs` — FOUND
- `src/BaseApi.Service/Features/Schema/SchemaDtos.cs` — FOUND
- `src/BaseApi.Service/Features/Schema/SchemaEntityMapper.cs` — FOUND
- `src/BaseApi.Service/Features/Schema/SchemaDtoValidator.cs` — FOUND
- `src/BaseApi.Service/Features/Schema/SchemaService.cs` — FOUND
- `src/BaseApi.Service/Features/Schema/SchemaController.cs` — FOUND
- `src/BaseApi.Service/Features/Schema/SchemaServiceCollectionExtensions.cs` — FOUND
- `src/BaseApi.Service/Persistence/Configurations/SchemaEntityConfiguration.cs` — FOUND
- `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` — FOUND

**Task commits (verified via `git log --oneline -3`):**
- `4912a83` Task 1 — FOUND
- `0903729` Task 2 — FOUND

**Quality gates (full plan-level verify):**
- 10 `[MapperIgnoreTarget]` attribute usages, 0 `[MapperIgnoreSource]` usages — PASSED
- 5 `[Fact]` attributes with the 5 prescribed names — PASSED
- 5 usages of `TestContext.Current.CancellationToken` (one per fact) — PASSED
- `Dialect.Default = Dialect.Draft202012` present in SchemaDtoValidator.cs — PASSED
- `SchemaRegistry.Global.Fetch = (_, _) => null` present in SchemaDtoValidator.cs — PASSED
- `MetaSchemas.Draft202012.Evaluate` present (both validators) — PASSED
- `Include(new BaseDtoValidator<SchemaCreateDto>())` + `Include(new BaseDtoValidator<SchemaUpdateDto>())` — PASSED
- `HasColumnType("jsonb")` in SchemaEntityConfiguration — PASSED
- `dotnet build SK_P.sln -c Release` succeeds with 0 warnings, 0 errors — PASSED
- Phase 1-7 regression: 98/98 GREEN (5 SchemasIntegrationTests fail per Wave B isolation contract — documented and accepted) — PASSED

## Self-Check: PASSED

---
*Phase: 08-entity-build-out-migrations-docker-runtime-tests*
*Plan: 02*
*Completed: 2026-05-27*
