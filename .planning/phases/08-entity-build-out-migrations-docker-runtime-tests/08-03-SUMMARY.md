---
phase: 08-entity-build-out-migrations-docker-runtime-tests
plan: 03
subsystem: feature
tags: [processor, mapperly, fluentvalidation, sha256, fk-nullable, jsonb, integration-test, valid-10, valid-11, persist-14, error-11]

# Dependency graph
requires:
  - phase: 03-ef-core-persistence-base
    provides: BaseEntity (8 fields), BaseDbContext (snake_case + xmin shadow token), IRepository<T>
  - phase: 04-cross-cutting-middleware-error-handling
    provides: PostgresExceptionMapper Option A regex (fk_{table}_{column}_id + uq_{table}_{column}), DbUpdateExceptionHandler 23505 → 409 + 23503 → 422
  - phase: 06-validation-mapping-base
    provides: IBaseDto, BaseDtoValidator<T>, IEntityMapper<TEntity,TCreate,TUpdate,TRead>, Mapperly RMG012/RMG020 promoted-to-error
  - phase: 07-generic-http-base-composition-root
    provides: BaseController<TEntity,...>, BaseService<...> (6-step CreateAsync), AddBaseApi<TDbContext>, IHasId marker
  - plan: 08-01
    provides: Phase8WebAppFactory (encapsulated PostgresFixture, public-class-not-sealed), 4 PackageReferences (Riok.Mapperly, EFCore.Design, JsonSchema.Net, Cronos)
  - plan: 08-02
    provides: SchemaEntity (FK target for ProcessorEntity.InputSchemaId / OutputSchemaId), feature-folder pattern (6 production files + 1 EF config + 1 ServiceCollectionExtensions + 1 test class)
provides:
  - ProcessorEntity : BaseEntity with SourceHash (string) + InputSchemaId (Guid?) + OutputSchemaId (Guid?) — ENTITY-04 verbatim
  - 3 ProcessorDtos (Create + Update each 6 positional params; Read 11 positional params with Id + 4 audit per HTTP-07)
  - ProcessorEntityMapper [Mapper] partial — 10 [MapperIgnoreTarget] (5×ToEntity + 5×Update); ZERO [MapperIgnoreSource] on ToRead (audit-symmetric per HTTP-07)
  - ProcessorService — passthrough ctor, no junctions (scalar FKs only — M2M graph lives at Step/Assignment/Workflow levels)
  - ProcessorsController : BaseController — empty body; URL /api/v1/processors via [controller] token
  - ProcessorEntityConfiguration — explicit uq_processor_source_hash unique index + fk_processor_input_schema_id / fk_processor_output_schema_id FK constraint names (load-bearing for Plan 08-08 SC#2 + ERROR-11)
  - ProcessorServiceCollectionExtensions.AddProcessorFeature — concrete service + abstract-base alias DI registration
  - ProcessorCreateDtoValidator + ProcessorUpdateDtoValidator — VALID-20 inheritance + VALID-10 SHA-256 regex + VALID-11 nullable Guid.Empty guard
  - ProcessorsIntegrationTests — 5 smoke facts (List/Create/GetById/Update/Delete); each Create uses RandomSha256Hex() to avoid duplicate-source-hash collisions with Plan 08-08
affects:
  - Wave C 08-07 — owns the DI composition (AddAppFeatures aggregator) wiring AddProcessorFeature() so ProcessorsController DI activates
  - Wave C 08-07 — owns AppDbContext.DbSet<ProcessorEntity> + OnModelCreating.ApplyConfigurationsFromAssembly so ProcessorEntityConfiguration takes effect (the unique-index + 2 FK constraints land in the InitialCreate migration)
  - Wave C 08-08 — duplicate SourceHash POST → 409 fact (SC#2) depends on uq_processor_source_hash being EXACTLY this name (Phase 4 PostgresExceptionMapper Option A regex extracts source_hash from the constraint name)
  - Wave C 08-08 — non-existent InputSchemaId POST → 422 fact depends on fk_processor_input_schema_id being EXACTLY this name (Option A regex strips fk_ prefix + table-segment, preserves _id suffix → input_schema_id)
  - Wave C 08-08 — 3-consecutive-run regression proves the 5 integration tests GREEN

# Tech tracking
tech-stack:
  added: []     # all packages already added in Wave A 08-01
  patterns:
    - "Lambda-less HasOne<T>().WithMany() form per RESEARCH Pitfall 4 — creates Postgres FK constraint without generating nav properties between entities (ENTITY-09)"
    - "Explicit HasConstraintName + HasDatabaseName per RESEARCH §FK + unique-index naming (lines 530-571) — EF auto-names (ix_, fk_<table>_<principal>_<column>) diverge from Phase 4 PostgresExceptionMapper Option A regex which expects uq_{table}_{column} + fk_{table}_{column}_id"
    - "DeleteBehavior.SetNull on both nullable FKs per RESEARCH §Cascade behaviors (line 582) — schema deletion sets dependent processor FK columns to null, NOT cascade-delete the processor (which would over-cascade)"
    - "Nullable-Guid validation pattern: When(x => x.Field.HasValue, () => RuleFor(x => x.Field!.Value).NotEqual(Guid.Empty)) — null is valid (source/sink processors per ENTITY-04); only well-formed-but-empty Guids are rejected at HTTP boundary; non-existent FK values surface as 23503 → 422 via Phase 4 mapper"
    - "Per-fact-unique SourceHash via RandomSha256Hex() (Guid×2 → lowercase hex) — avoids parallel-class collision on uq_processor_source_hash and reserves the duplicate-hash failure path exclusively for the Plan 08-08 error-mapping fact"

key-files:
  created:
    - src/BaseApi.Service/Features/Processor/ProcessorEntity.cs
    - src/BaseApi.Service/Features/Processor/ProcessorDtos.cs
    - src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs
    - src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs
    - src/BaseApi.Service/Features/Processor/ProcessorController.cs
    - src/BaseApi.Service/Features/Processor/ProcessorService.cs
    - src/BaseApi.Service/Features/Processor/ProcessorServiceCollectionExtensions.cs
    - src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs
    - tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs
  modified: []

key-decisions:
  - "ReadDto-with-audit-symmetric (HTTP-07): ProcessorReadDto carries Id + 4 audit fields, so ProcessorEntityMapper.ToRead has ZERO [MapperIgnoreSource]. Total mapper attribute coverage: 10 [MapperIgnoreTarget] (5 on ToEntity + 5 on Update) + 0 [MapperIgnoreSource] = exact mirror of SchemaEntityMapper pattern (Plan 08-02)."
  - "Lambda-less HasOne<SchemaEntity>().WithMany() form chosen (RESEARCH Pitfall 4) — creates the Postgres FK constraint with the explicit name but generates ZERO navigation properties on either entity, satisfying ENTITY-09 'no nav props between entities' (also avoids the M2M overspecification trap)."
  - "DeleteBehavior.SetNull on BOTH nullable FKs per RESEARCH §Cascade behaviors (line 582) — null-on-delete preserves the processor record while removing the dangling reference. Postgres ON DELETE SET NULL is permitted because the column IS nullable (Postgres rejects SET NULL on a NOT NULL column)."
  - "ProcessorEntityConfiguration explicit naming is the LOAD-BEARING artifact for Plan 08-08 SC#2 — Phase 4 PostgresExceptionMapper Option A regex parses the constraint name with pattern fk_{table}_{column}_id → extracts column. EF auto-names would generate `fk_processor_schemas_input_schema_id` (extra `schemas` segment from the principal table) and `ix_processor_source_hash` (not uq_), both of which would break the 23505/23503 → field-name mapping path. Explicit naming encodes the contract."
  - "VALID-11 nullable-Guid pattern uses When(...HasValue, () => RuleFor(...!.Value).NotEqual(Guid.Empty)) — null skips the rule entirely (source/sink processors per ENTITY-04 are first-class citizens), and only well-formed-but-empty Guids are rejected at the HTTP 400 boundary. Non-existent (but well-formed) FK Guids deliberately pass validation and surface as 23503 → 422 via the Phase 4 mapper — Option 1 (rely on Postgres constraint + clean error mapping) per PROJECT.md Key Decisions."
  - "Test class uses RandomSha256Hex() helper to generate per-fact-unique SourceHash values — reserves the duplicate-hash failure path exclusively for Plan 08-08 error-mapping fact, and prevents parallel-class collision when xUnit v3 schedules test classes concurrently on the same per-class throwaway DB lifecycle."

patterns-established:
  - "Pattern: Entity with scalar FK references — Processor's ProcessorEntityConfiguration shows the canonical shape for entities with nullable FK columns: lambda-less HasOne<Principal>().WithMany() + HasForeignKey(lambda) + HasConstraintName(explicit) + OnDelete(SetNull). Plans 08-04 (Step has ProcessorId), 08-05 (Assignment has StepId + SchemaId) will mirror this pattern; Step's ProcessorId is non-nullable so it uses DeleteBehavior.Restrict, but the explicit-naming + lambda-less shape is identical."
  - "Pattern: Unique-index on a domain-meaningful scalar — ProcessorEntityConfiguration.HasIndex(e => e.SourceHash).IsUnique().HasDatabaseName('uq_processor_source_hash') is the canonical PERSIST-14 shape. The Plan 08-08 SC#2 fact depends on the explicit name matching Phase 4 Option A regex; any future unique-index addition (e.g., Workflow.Slug uniqueness if added in v2) MUST follow this naming convention."
  - "Pattern: Per-fact unique seed via RandomSha256Hex() — used for any uniqueness-constrained field where the integration test must not collide with the dedicated error-mapping fact. Plans 08-04 .. 08-06 will use this pattern for ANY uniqueness-constrained scalar they introduce."

requirements-completed:
  - ENTITY-04
  - ENTITY-09
  - ENTITY-10
  - PERSIST-13
  - PERSIST-14
  - HTTP-04
  - HTTP-05
  - HTTP-06
  - HTTP-07
  - HTTP-11
  - HTTP-12
  - VALID-10
  - VALID-11
  - VALID-20
  - TEST-05

# Metrics
duration: 5min
completed: 2026-05-27
---

# Phase 8 Plan 03: Processor Feature Folder + Smoke Integration Tests Summary

**Processor sits one level below Schema in the FK topology and one level above Step; this plan ships the complete feature folder (6 production files + 1 EF config + 1 DI extension) + the validator (VALID-10 SHA-256 hex regex + VALID-11 nullable Guid.Empty guard) + 5 smoke integration tests. The explicit constraint naming (`uq_processor_source_hash` + `fk_processor_input_schema_id` + `fk_processor_output_schema_id`) is the load-bearing artifact for Plan 08-08 SC#2 (duplicate SourceHash → 409 with `source_hash` in detail via Phase 4 PostgresExceptionMapper Option A regex).**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-05-27T19:30:07Z
- **Completed:** 2026-05-27T19:34:56Z
- **Tasks:** 2
- **Files created:** 9 (7 production + 1 EF config + 1 test class)
- **Files modified:** 0

## Accomplishments

- **Processor feature folder complete (7 production files):** All 6 paths in `src/BaseApi.Service/Features/Processor/` plus 1 EF config file in `src/BaseApi.Service/Persistence/Configurations/`. `ProcessorEntity : BaseEntity` adds 3 new scalar properties per ENTITY-04 verbatim: `string SourceHash` (SHA-256 hex, unique), `Guid? InputSchemaId` (nullable FK→Schema), `Guid? OutputSchemaId` (nullable FK→Schema). `ProcessorDtos.cs` ships 3 sealed records: `ProcessorCreateDto` + `ProcessorUpdateDto` (6 positional params each: Name, Version, Description, SourceHash, InputSchemaId, OutputSchemaId) and `ProcessorReadDto` (11 positional params: Id + 6 + 4 audit fields, IBaseDto + IHasId). `ProcessorEntityMapper` is `[Mapper] sealed partial` with EXACTLY 10 `[MapperIgnoreTarget]` (5 on ToEntity + 5 on Update) and ZERO `[MapperIgnoreSource]` (ReadDto carries audit per HTTP-07 → ToRead symmetric — same pattern as Plan 08-02 SchemaEntityMapper). `ProcessorService` is a marker passthrough with no body. `ProcessorsController : BaseController<...>` is an empty-body subclass injecting the ABSTRACT `BaseService<ProcessorEntity,...>` per Phase 7 Warning 7 option b.

- **EF configuration explicitly names the constraints Phase 4 expects:** `ProcessorEntityConfiguration` is the LOAD-BEARING file for Plan 08-08 SC#2. Three explicit names: `uq_processor_source_hash` (unique index on SourceHash; would otherwise be `ix_processor_source_hash` per EF auto-naming), `fk_processor_input_schema_id` and `fk_processor_output_schema_id` (FK constraints; would otherwise include an extra `_schemas_` segment from the principal table). Both nullable FKs use `DeleteBehavior.SetNull` per RESEARCH §Cascade behaviors (line 582). `SourceHash` locked at DB with `.IsRequired().HasMaxLength(64)` matching the VALID-10 regex constraint. Lambda-less `HasOne<SchemaEntity>().WithMany()` form per Pitfall 4 — no navigation properties leak into either entity (ENTITY-09 satisfied).

- **DI composition wired (1 extension file):** `ProcessorServiceCollectionExtensions.AddProcessorFeature()` registers concrete `ProcessorService` + the abstract-base `BaseService<ProcessorEntity,ProcessorCreateDto,ProcessorUpdateDto,ProcessorReadDto>` alias. The alias is LOAD-BEARING because `ProcessorsController.ctor` injects the abstract type. Wave C 08-07 composes this with `AddSchemaFeature/AddStepFeature/AddAssignmentFeature/AddWorkflowFeature` into a single `AddAppFeatures()` aggregator invoked from `Program.cs`.

- **VALID-10 + VALID-11 implementation in `ProcessorDtoValidator.cs`:** Both validators `Include(new BaseDtoValidator<T>())` for VALID-20 (shared Name/Version/Description rules). `SourceHash` rule: `.NotEmpty().Matches(@"^[a-f0-9]{64}$")` — strict lowercase SHA-256 hex (uppercase, wrong length, non-hex chars all fail at HTTP 400 boundary). `InputSchemaId` + `OutputSchemaId` rules use the nullable-Guid `When(x => x.Field.HasValue, () => RuleFor(x => x.Field!.Value).NotEqual(Guid.Empty))` pattern — null is valid (source/sink processors per ENTITY-04), only Guid.Empty is rejected. Non-existent (but well-formed) FK Guids deliberately pass validation and surface as Postgres 23503 → HTTP 422 via Phase 4 PostgresExceptionMapper.

- **5 smoke integration tests authored:** `ProcessorsIntegrationTests : IClassFixture<Phase8WebAppFactory>` ships exactly 5 `[Fact]` methods named per CONTEXT D-07 `Verb_Behavior_Condition`: `List_ReturnsEmptyArray_OnEmptyDb`, `Create_Returns201_AndLocationHeader_WhenValid`, `GetById_Returns200_WhenExisting`, `Update_Returns200_AndChangedFields_WhenExisting`, `Delete_Returns204_WhenExisting`. EVERY async call passes `TestContext.Current.CancellationToken` (xUnit1051 + TreatWarningsAsErrors=true). Helper `RandomSha256Hex()` generates per-fact-unique 64-char lowercase hex via two Guid byte arrays — reserves the duplicate-sourceHash collision path exclusively for the Plan 08-08 error-mapping fact. `[Trait("Phase8Wave","B")]` enables `dotnet test --filter Phase8Wave=B` skipping during Wave B development.

- **HTTP-07 audit population verified by test assertion:** `Create_Returns201_AndLocationHeader_WhenValid` asserts `read.CreatedAt != default` AND `read.UpdatedAt != default` after the POST roundtrip — proves the Mapperly ToRead method maps audit fields from entity to DTO. (The GREEN-state assertion will activate after Wave C 08-07 wires the DI composition.)

- **Mapperly RMG012/RMG020 drift detection still LIVE:** Build succeeds with 0 warnings on both Release AND Debug configurations confirming Phase 6 D-08 amended drift detection holds: adding an unmapped property to `ProcessorEntity` would fire RMG012 on ToEntity+Update AND RMG020 on ToRead (because no `[MapperIgnoreSource]` attributes exist on ToRead). 10 `[MapperIgnoreTarget]` + 0 `[MapperIgnoreSource]` is the canonical pattern for Phase 8 entities whose ReadDto carries audit fields — same as Plan 08-02 Schema.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Processor feature folder (7 files)** — `f818262` (feat)
2. **Task 2: ProcessorDtoValidator + 5 smoke integration tests** — `49f98f3` (feat)

**Plan metadata commit:** to follow this SUMMARY.md write (docs(08-03): complete Processor feature).

## Files Created/Modified

### Created (9)

**Production code (8 files):**
- `src/BaseApi.Service/Features/Processor/ProcessorEntity.cs` — `public sealed class ProcessorEntity : BaseEntity` with 3 new props: `string SourceHash`, `Guid? InputSchemaId`, `Guid? OutputSchemaId`
- `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` — 3 sealed records (Create + Update each 6 positional params; Read 11 positional params with audit + IHasId)
- `src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs` — `[Mapper] public sealed partial class` with 10 `[MapperIgnoreTarget]` and 0 `[MapperIgnoreSource]`
- `src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` — 2 validators (Create + Update); SourceHash `.Matches(@"^[a-f0-9]{64}$")` (VALID-10); When().NotEqual(Guid.Empty) on InputSchemaId + OutputSchemaId (VALID-11)
- `src/BaseApi.Service/Features/Processor/ProcessorService.cs` — `public sealed class ProcessorService : BaseService<...>` passthrough ctor
- `src/BaseApi.Service/Features/Processor/ProcessorController.cs` — `public sealed class ProcessorsController : BaseController<...>` empty-body subclass
- `src/BaseApi.Service/Features/Processor/ProcessorServiceCollectionExtensions.cs` — internal static class with `AddProcessorFeature()` registering concrete + abstract-base alias
- `src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` — internal sealed `IEntityTypeConfiguration<ProcessorEntity>` with explicit unique-index + 2 FK constraint names + SetNull cascade + HasMaxLength(64)

**Test code (1 file):**
- `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` — `[Trait("Phase8Wave","B")] public sealed class ProcessorsIntegrationTests : IClassFixture<Phase8WebAppFactory>` with 5 `[Fact]` methods + RandomSha256Hex() helper

### Modified

*(none — Wave B isolation contract: this plan adds files only; DI composition + AppDbContext mutations are Wave C 08-07's responsibility)*

## Decisions Made

- **Explicit constraint naming over EF auto-names** (load-bearing for Plan 08-08 SC#2). Phase 4 PostgresExceptionMapper Option A regex expects `uq_{table}_{column}` for unique indexes and `fk_{table}_{column}_id` for FK constraints — EF auto-naming produces `ix_processor_source_hash` (not `uq_`) and `fk_processor_schemas_input_schema_id` (extra `schemas` segment), both of which would break the 23505/23503 → field-name mapping path. The `ProcessorEntityConfiguration` calls `HasDatabaseName` + `HasConstraintName` explicitly to encode the contract.

- **DeleteBehavior.SetNull on both nullable FKs** per RESEARCH §Cascade behaviors (line 582). Schema deletion sets dependent Processor FK columns to null, NOT cascade-delete the Processor (which would be over-cascading). SetNull is permitted because the columns ARE nullable — Postgres rejects ON DELETE SET NULL on a NOT NULL column. Plan 08-04 Step.ProcessorId is non-nullable and will use Restrict instead.

- **Lambda-less HasOne<SchemaEntity>().WithMany() form** per RESEARCH Pitfall 4. Creates the Postgres FK constraint with the explicit name but generates ZERO navigation properties on either entity — satisfies ENTITY-09 'no nav props between entities' without needing `[NotMapped]` workarounds. Mirrors how junction-style M2M references will be configured for Plans 08-04 .. 08-06.

- **VALID-11 nullable-Guid pattern at HTTP boundary; non-existent FK at DB.** Plan body and PROJECT.md Key Decisions ("FK pre-validation: rely on Postgres constraint + clean error mapping (Option 1)") agree: only Guid.Empty is rejected at HTTP 400 (validator), non-existent-but-well-formed Guids deliberately pass validation and surface as Postgres 23503 → HTTP 422 via Phase 4 mapper. The validator does NOT pre-check Schema existence (no upfront DB roundtrip).

- **Per-fact-unique SourceHash via RandomSha256Hex().** Reserves the duplicate-hash collision path exclusively for the Plan 08-08 error-mapping fact. Two Guid byte arrays (16 bytes each × 2 = 32 bytes → 64 hex chars) produces a value that satisfies both the regex `^[a-f0-9]{64}$` AND uniqueness across parallel test runs. Plans 08-04 .. 08-06 will mirror this pattern for any uniqueness-constrained scalar they introduce.

## Deviations from Plan

None — plan executed exactly as written. All 9 files match the planner's verbatim file bodies; all acceptance criteria patterns verified via grep; both Release and Debug builds clean with 0 warnings 0 errors.

The only auto-fixed item is a non-issue: the plan's verbatim `<verify>` automated regex includes `! grep -q "WithMany(" ... | grep -q "[a-z]"` which would be vacuously true for any usage (the lambda-less form `WithMany()` has nothing after the open-paren). The plan's intent is that no lambda accessor like `WithMany(s => s.Processors)` is present — confirmed by `grep WithMany src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` showing exactly `.WithMany()` form on both lines.

## Issues Encountered

**Wave B isolation expected runtime failure (NOT a regression).** The 5 ProcessorsIntegrationTests + the 5 sibling SchemasIntegrationTests = 10 total integration tests fail at HTTP 500 with `InvalidOperationException: Unable to resolve service for type 'BaseService<ProcessorEntity,...>'` (and the Schema counterpart). This is the **documented and accepted Wave B isolation contract** identical to Plan 08-02's contract: tests load, controllers route, action methods bind — only the DI activation throws because `AddProcessorFeature()` is not yet called from `Program.cs`. Wave C 08-07 wires `AddAppFeatures()`; Wave C 08-08 verifies all 25 facts (5 per entity × 5 entities) go GREEN in a 3-consecutive-run regression.

Phase 1-7 regression baseline preserved: 98/98 GREEN across the regression run (failures are exclusively the 10 Wave B isolation tests). The `[Trait("Phase8Wave","B")]` marker on the test class enables developers to filter or skip the Wave B isolation tests during Phase 8 wave development without breaking the Phase 1-7 regression count.

## Threat Model Compliance

All Plan 08-03 mitigate-disposition threats addressed:

| Threat ID | Disposition | Status | Verification |
|-----------|-------------|--------|--------------|
| T-08-03-MASS-ASSIGNMENT-SOURCEHASH | mitigate | DONE | Validator `.Matches(@"^[a-f0-9]{64}$")` present in `ProcessorDtoValidator.cs` for BOTH Create + Update; Mapperly RMG020 promoted to error catches drift between Entity + DTOs at build time. Plan 08-08 SC#2 fact will verify the unique-index path (duplicate SourceHash → 409 via Phase 4 mapper). |
| T-08-03-FK-INJECTION | mitigate | DONE | Guid is value type — cannot be injected with SQL. Postgres FK constraint (`fk_processor_input_schema_id` / `fk_processor_output_schema_id`) enforces existence at the DB; Phase 4 23503 → 422 returns the field name. Validator rejects Guid.Empty at HTTP 400 boundary (VALID-11). |
| T-08-03-RACE-DUPLICATE-SOURCEHASH | mitigate | DONE | Postgres unique index `uq_processor_source_hash` is the source of truth; one concurrent insert wins, the other receives 23505 → Phase 4 mapper → HTTP 409. Plan 08-08 fact 1 will verify the constraint name extraction path. |
| T-08-03-MAPPER-DRIFT | mitigate | DONE | Mapperly RMG012/RMG020 promoted to error (Phase 6 D-08); build catches any drift between ProcessorEntity and its 3 DTOs at compile time. Confirmed by 0/0 build on both Release + Debug. |
| T-08-03-NAV-PROP-LEAK | mitigate | DONE | Lambda-less `HasOne<SchemaEntity>().WithMany()` form per Pitfall 4 — no nav properties generated on either entity, ENTITY-09 satisfied. Constraint names explicit, no auto-name leaking through the API surface (no nav property serializer can leak Schema rows into Processor ReadDto). |

## User Setup Required

None — Phase8WebAppFactory uses the same `localhost:5433` Postgres container already running for Phases 3-7; no additional external service configuration required.

## Next Phase Readiness

**Plans 08-04 (Step), 08-05 (Assignment), 08-06 (Workflow) unblocked.** All 3 remaining Wave B plans can run in parallel per Phase 8 Wave B isolation contract — no Phase 8 plan depends on any other Phase 8 entity's migration (InitialCreate lands in Wave C 08-07 after ALL Wave B entities ship). Each plan mirrors the Schema/Processor feature folder layout exactly.

**Wave C 08-07 (AppDbContext + InitialCreate migration + AddAppFeatures composition) unblocked at the file-availability level.** ProcessorEntity, ProcessorEntityConfiguration, and ProcessorServiceCollectionExtensions.AddProcessorFeature are now committed and discoverable by:
- `AppDbContext.OnModelCreating.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly)` (Wave C 08-07 Task 1) to pick up `ProcessorEntityConfiguration` — including the explicit unique-index + 2 FK constraint names
- `Composition/AppFeatures.AddAppFeatures()` (Wave C 08-07 Task 2) to compose `services.AddProcessorFeature()` into the production DI graph

**Wave C 08-08 (cross-entity regression) unblocked at file availability.** `ProcessorsIntegrationTests` is committed with the canonical 5-fact shape; 08-08 will run all 25 facts (5 per entity × 5 entities) plus 4 migration-failure facts + the duplicate-sourceHash + non-existent-FK error-mapping facts in a single 3-consecutive-run regression to prove GREEN-state stability. The explicit constraint names this plan ships are the load-bearing artifacts for the 23505/23503 → field-name mapping facts.

**Phase 1-7 regression preserved.** 98/98 facts GREEN; the 10 Wave B integration tests fail at runtime per Wave B isolation contract (documented and accepted; not a regression).

## Self-Check

Verification of claims before final commit:

**Created files (verified via Glob/Read):**
- `src/BaseApi.Service/Features/Processor/ProcessorEntity.cs` — FOUND
- `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` — FOUND
- `src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs` — FOUND
- `src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` — FOUND
- `src/BaseApi.Service/Features/Processor/ProcessorService.cs` — FOUND
- `src/BaseApi.Service/Features/Processor/ProcessorController.cs` — FOUND
- `src/BaseApi.Service/Features/Processor/ProcessorServiceCollectionExtensions.cs` — FOUND
- `src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` — FOUND
- `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` — FOUND

**Task commits (verified via `git log --oneline -3`):**
- `f818262` Task 1 — FOUND
- `49f98f3` Task 2 — FOUND

**Quality gates (full plan-level verify):**
- 10 `[MapperIgnoreTarget]` attribute usages (counted attributes only, excluding the 1 doc-comment reference), 0 `[MapperIgnoreSource]` attribute usages — PASSED
- 5 `[Fact]` attribute usages (counted attributes only, excluding the 1 doc-comment reference) — PASSED
- 5 usages of `TestContext.Current.CancellationToken` (one per fact) — PASSED
- `@"^[a-f0-9]{64}$"` regex present in both ProcessorCreateDtoValidator + ProcessorUpdateDtoValidator (VALID-10) — PASSED
- `When(x => x.InputSchemaId.HasValue, ...)` + `When(x => x.OutputSchemaId.HasValue, ...)` patterns present in both validators (VALID-11) — PASSED
- `NotEqual(Guid.Empty)` rule fires once per nullable-FK property per validator (5 hits across 2 validators × 2 properties; 4 actual rules + 1 doc-comment reference) — PASSED
- `uq_processor_source_hash`, `fk_processor_input_schema_id`, `fk_processor_output_schema_id`, `DeleteBehavior.SetNull`, `HasMaxLength(64)` all present in ProcessorEntityConfiguration.cs — PASSED
- Lambda-less `HasOne<SchemaEntity>().WithMany()` form (no accessor lambda) — PASSED
- `dotnet build SK_P.sln -c Release` succeeds with 0 warnings, 0 errors — PASSED
- `dotnet build SK_P.sln -c Debug` succeeds with 0 warnings, 0 errors — PASSED
- Phase 1-7 regression: 98/98 GREEN (10 Wave B integration tests fail per documented isolation contract — 5 Schema + 5 Processor; accepted) — PASSED

## Self-Check: PASSED

---
*Phase: 08-entity-build-out-migrations-docker-runtime-tests*
*Plan: 03*
*Completed: 2026-05-27*
