# Phase 10: Remove SchemaId on AssignmentEntity and add ConfigSchemaId on ProcessorEntity — Specification

**Created:** 2026-05-28
**Ambiguity score:** 0.11 (gate: ≤ 0.20)
**Requirements:** 7 locked

## Goal

Field-shape revision of the v1 surface — drop `SchemaId` from `AssignmentEntity` and add nullable `Guid? ConfigSchemaId` to `ProcessorEntity` mirroring `InputSchemaId` behavior exactly. No new endpoints, no new entities, no new error shapes — a clean entity-and-DTO delta with synchronized REQUIREMENTS.md amendments, DB migration regeneration (no shipped data), and mechanical test-fixture updates plus 2 new ConfigSchemaId round-trip facts.

## Background

**What exists today (verified in codebase):**
- `AssignmentEntity` (`src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs:24-29`) has three scalar properties on top of `BaseEntity`: `StepId` (non-nullable FK→Step), `SchemaId` (non-nullable FK→Schema), `Payload` (jsonb).
- `AssignmentEntityConfiguration` (`src/BaseApi.Service/Persistence/Configurations/AssignmentEntityConfiguration.cs:38-49`) wires `fk_assignment_step_id` + `fk_assignment_schema_id` both with `OnDelete(Restrict)`.
- `AssignmentCreateDto` / `AssignmentUpdateDto` / `AssignmentReadDto` (`src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs`) all carry `SchemaId`.
- `AssignmentCreateDtoValidator` + `AssignmentUpdateDtoValidator` (`src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs`) both enforce VALID-15 `SchemaId NotEqual(Guid.Empty)`.
- `ProcessorEntity` (`src/BaseApi.Service/Features/Processor/ProcessorEntity.cs:23-28`) has `SourceHash`, `InputSchemaId` (nullable FK→Schema), `OutputSchemaId` (nullable FK→Schema) — both schema FKs use the `When(x => x.Field.HasValue, () => RuleFor(...).NotEqual(Guid.Empty))` VALID-11 pattern.
- `ProcessorEntityConfiguration` (`src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs:31-41`) wires `fk_processor_input_schema_id` + `fk_processor_output_schema_id` both with `OnDelete(SetNull)`.
- Single shipped migration: `20260527203118_InitialCreate` (`src/BaseApi.Service/Persistence/Migrations/`). No additional migration files. No shipped production data; local docker-compose `pgdata` volume is tear-downable; tests use per-class throwaway DBs (Phase 3 D-15 pattern).
- 8 test files construct `ProcessorCreateDto(...)` with `InputSchemaId: null, OutputSchemaId: null`; 1 file (`tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs`) constructs `AssignmentCreateDto/UpdateDto` with `SchemaId` and seeds a Schema in `CreatePrereqAsync`.
- REQUIREMENTS.md ENTITY-04, ENTITY-07, VALID-11, VALID-15 are all locked and reference the affected fields directly.
- Existing suite count after Phase 9: 138 facts.

**What this phase delivers:** a coordinated entity + DTO + validator + config + migration + REQUIREMENTS.md + test-fixture delta producing the new v1.x surface — Assignment with `(StepId, Payload)` and Processor with `(SourceHash, InputSchemaId?, OutputSchemaId?, ConfigSchemaId?)`.

## Requirements

1. **AssignmentEntity SchemaId removal**: `AssignmentEntity` no longer carries `SchemaId`.
   - Current: `AssignmentEntity` has `StepId`, `SchemaId`, `Payload` (`src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs:24-29`).
   - Target: `AssignmentEntity` has `StepId`, `Payload` only. `XML <summary>` doc updated to drop the `SchemaId` reference and the `fk_assignment_schema_id` cascade note.
   - Acceptance: `grep -n "SchemaId" src/BaseApi.Service/Features/Assignment/` returns zero matches; `dotnet build` is zero-warning Debug + Release.

2. **Assignment DTOs SchemaId removal**: `AssignmentCreateDto` / `AssignmentUpdateDto` / `AssignmentReadDto` no longer carry `SchemaId`.
   - Current: All three records list `SchemaId` as a positional parameter (`src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs`).
   - Target: Create + Update drop from 6 params → 5 (`Name, Version, Description, StepId, Payload`); Read drops from 11 params → 10 (`Id, Name, Version, Description, StepId, Payload, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy`).
   - Acceptance: `grep -n "SchemaId" src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs` returns zero matches; record positional arity matches the target counts.

3. **VALID-15 amendment + AssignmentEntityConfiguration FK drop**: Validators no longer enforce `SchemaId NotEqual(Guid.Empty)`; `fk_assignment_schema_id` FK + `ix_assignments_schema_id` index removed from the EF entity configuration.
   - Current: Both validators have a `RuleFor(x => x.SchemaId).NotEqual(Guid.Empty)` block (`AssignmentDtoValidator.cs:42-44, 83-85`); `AssignmentEntityConfiguration` declares `HasOne<SchemaEntity>().WithMany().HasForeignKey(e => e.SchemaId).HasConstraintName("fk_assignment_schema_id").OnDelete(DeleteBehavior.Restrict)`.
   - Target: SchemaId rules dropped from both validators (StepId rule + Payload rule remain); `HasOne<SchemaEntity>()` block removed from `AssignmentEntityConfiguration` (only `HasOne<StepEntity>()` remains).
   - Acceptance: `grep -n "SchemaId\|fk_assignment_schema_id\|ix_assignments_schema_id" src/BaseApi.Service/` returns zero matches in non-binary files; `dotnet build` is zero-warning.

4. **ProcessorEntity ConfigSchemaId addition (mirrors InputSchemaId)**: `ProcessorEntity` carries a new `Guid? ConfigSchemaId` property; all DTOs include it; EF wires the FK + index identical to `InputSchemaId`; validator enforces the nullable-Guid VALID-11 pattern.
   - Current: `ProcessorEntity` has `SourceHash`, `InputSchemaId?`, `OutputSchemaId?` only. Validators cover Input + Output via the `When().HasValue → NotEqual(Guid.Empty)` pattern. `ProcessorEntityConfiguration` wires Input + Output FKs with `OnDelete(SetNull)`.
   - Target: `ProcessorEntity` adds `public Guid? ConfigSchemaId { get; set; }`. `ProcessorCreateDto/UpdateDto/ReadDto` add `ConfigSchemaId` as a nullable Guid? positional param (Create + Update: 6→7; Read: 11→12). `ProcessorCreateDtoValidator` and `ProcessorUpdateDtoValidator` each add a third `When(x => x.ConfigSchemaId.HasValue, () => RuleFor(x => x.ConfigSchemaId!.Value).NotEqual(Guid.Empty).WithMessage("ConfigSchemaId must not be Guid.Empty when provided."))` block. `ProcessorEntityConfiguration` adds a third `HasOne<SchemaEntity>().WithMany().HasForeignKey(e => e.ConfigSchemaId).HasConstraintName("fk_processor_config_schema_id").OnDelete(DeleteBehavior.SetNull)` block (lambda-less, no nav properties, matches the Input/Output pattern verbatim).
   - Acceptance: `grep -n "ConfigSchemaId" src/BaseApi.Service/Features/Processor/` shows the field on Entity + all 3 DTOs + both validators; `dotnet build` is zero-warning; Mapperly `ProcessorEntityMapper` requires zero changes (symmetric on both Entity and DTO sides — same audit-symmetric pattern as InputSchemaId).

5. **REQUIREMENTS.md amendments to 4 locked REQ-IDs**: ENTITY-04, ENTITY-07, VALID-11, VALID-15 are edited in place to reflect the new v1.x surface; IDs retained; footer updated.
   - Current: REQUIREMENTS.md describes the v1 shape (ENTITY-07: `StepId Guid (FK→Step), SchemaId Guid (FK→Schema), Payload`; VALID-15: `AssignmentCreate/UpdateDto.StepId/SchemaId: NotEmpty Guid`; ENTITY-04: `InputSchemaId Guid? + OutputSchemaId Guid?`; VALID-11: covers Input + Output).
   - Target:
     - **ENTITY-07** → `AssignmentEntity : BaseEntity` adds `StepId Guid (FK→Step), Payload string (jsonb)`.
     - **VALID-15** → `AssignmentCreate/UpdateDto.StepId: NotEmpty Guid`.
     - **ENTITY-04** → adds `ConfigSchemaId Guid?` to the existing nullable-FK list alongside `InputSchemaId` + `OutputSchemaId`; same source/sink/config wording; `fk_processor_config_schema_id` constraint named.
     - **VALID-11** → adds `ConfigSchemaId` to the list of nullable-Guid fields with the same `When(...).HasValue → NotEqual(Guid.Empty)` pattern. Footer: `*Last updated: 2026-05-28 — Phase 10 amendments for Assignment.SchemaId removal + Processor.ConfigSchemaId addition*`.
   - Acceptance: All 4 REQ-IDs reflect the post-Phase-10 shape; no orphan references to `AssignmentEntity.SchemaId` remain in REQUIREMENTS.md; footer date matches.

6. **DB migration regenerated in place (new timestamp)**: The existing `20260527203118_InitialCreate` migration is DELETED and a new single migration is generated reflecting the post-Phase-10 model. No additive "drop column / add column" migration is produced.
   - Current: 3 files in `src/BaseApi.Service/Persistence/Migrations/`: `20260527203118_InitialCreate.cs`, `20260527203118_InitialCreate.Designer.cs`, `AppDbContextModelSnapshot.cs` — all reflect v1 shape with `assignments.schema_id + fk_assignment_schema_id + ix_assignments_schema_id` and NO `processors.config_schema_id`.
   - Target: Old InitialCreate files deleted; new `<new-timestamp>_InitialCreate.cs` + `.Designer.cs` generated via `dotnet ef migrations add InitialCreate -p src/BaseApi.Service -s src/BaseApi.Service`; `AppDbContextModelSnapshot.cs` regenerated to match. Resulting `assignments` table has NO `schema_id` column / FK / index; `processors` table has `config_schema_id uuid NULL` + `fk_processor_config_schema_id` (SetNull) + `ix_processors_config_schema_id`. Documented teardown step (`docker compose down -v` + `dotnet ef database drop -f`) recorded in CONTEXT.md so any local dev DB is reset before the new migration applies.
   - Acceptance: Exactly one migration file in `src/BaseApi.Service/Persistence/Migrations/` (besides `AppDbContextModelSnapshot.cs`); `grep -n "schema_id" src/BaseApi.Service/Persistence/Migrations/*InitialCreate.cs` shows entries ONLY under the processors table (input_schema_id + output_schema_id + config_schema_id); `assignments` table block contains no `schema_id` column or FK.

7. **Test suite updates: mechanical edits + 2 new ConfigSchemaId facts; full regression GREEN**: All affected call sites compile under the new DTO shapes; 2 new round-trip facts cover ConfigSchemaId behavior; full suite stays GREEN across 3 consecutive `dotnet test` runs.
   - Current: 138 facts. 8 `ProcessorCreateDto(...)` call sites supply only `InputSchemaId: null, OutputSchemaId: null` (`ErrorMappingFacts.cs:44-45, 115-116`; `WorkflowsIntegrationTests.cs:61-62`; `AssignmentsIntegrationTests.cs:63-64`; `StepsIntegrationTests.cs:52-53`; `ProcessorsIntegrationTests.cs:44-45, 119-120`; `StartOrchestrationFacts.cs:61-62`; `StopOrchestrationFacts.cs:49-50`; `GetBySourceHashFacts.cs:51-52`). 1 file constructs `AssignmentCreateDto/UpdateDto` with `SchemaId` (`AssignmentsIntegrationTests.cs:84-90, 89, 131, 174`) and `CreatePrereqAsync` (lines 47-55) creates a Schema POST for the FK target.
   - Target:
     - **Mechanical updates:** All 8 `ProcessorCreateDto(...)` call sites add `ConfigSchemaId: null` as a third nullable param. `AssignmentsIntegrationTests.cs` drops `SchemaId` from `NewValidCreateDto` (line 84-90), from the round-trip assertion (line 131), and from the Update call (line 174). `CreatePrereqAsync` is simplified — drops the Schema POST (lines 47-55) and the `schemaId` tuple return; signature changes to return only `Guid stepId`.
     - **2 new facts in `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs`:**
       1. `Create_ProcessorWithConfigSchemaId_RoundTripsCorrectly` — POST Processor with `ConfigSchemaId = <previously-POSTed Schema.Id>`; GET by Id returns the same Guid in `ConfigSchemaId`.
       2. `Create_ProcessorWithNullConfigSchemaId_RoundTripsAsNull` — POST Processor with `ConfigSchemaId: null`; GET by Id returns `ConfigSchemaId == null`.
     - **Suite target:** 138 → 140 facts; `dotnet test SK_P.sln --no-restore -c Release` passes 140/140 across 3 consecutive runs per the Phase 8/9 convention (pre-existing flake on Run 0 acceptable per Plan 09-03 precedent); byte-identical psql `\l` BEFORE/AFTER snapshot proves no leaked test DBs (Phase 3 D-15 discipline preserved).
   - Acceptance: `dotnet test` reports 140 facts passing 3 times in a row; `grep -n "SchemaId" tests/` outside the ProcessorCreateDto `ConfigSchemaId: …` literal returns no orphan references to the removed `Assignment.SchemaId`; the SHA-256 of `psql -l` BEFORE and AFTER the 3-run cycle match (Phase 3 D-15).

## Boundaries

**In scope:**
- `AssignmentEntity.SchemaId` property removal (entity + DTOs + validator + EF config + migration + tests + REQUIREMENTS.md)
- `ProcessorEntity.ConfigSchemaId` nullable Guid? property addition (entity + DTOs + validator + EF config + migration + tests + REQUIREMENTS.md), mirroring `InputSchemaId` behavior exactly (SetNull cascade, lambda-less HasOne, When().HasValue → NotEqual(Guid.Empty), no nav properties)
- DB migration regenerated in place with a new timestamp; old `20260527203118_InitialCreate` files deleted; `AppDbContextModelSnapshot.cs` regenerated
- REQUIREMENTS.md amends ENTITY-04, ENTITY-07, VALID-11, VALID-15 in place; footer updated; ID numbering preserved
- Mechanical updates to 8 `ProcessorCreateDto(...)` call sites adding `ConfigSchemaId: null`
- Mechanical updates to `AssignmentsIntegrationTests.cs`: drop `SchemaId` from DTOs and from `CreatePrereqAsync` (Schema POST + tuple return)
- 2 new ConfigSchemaId round-trip facts in `ProcessorsIntegrationTests.cs` (happy-path with value, happy-path with null)
- Documented teardown step (`docker compose down -v` + `dotnet ef database drop -f`) in Phase 10 CONTEXT.md before the new migration applies
- Full regression suite stays GREEN: 140 facts across 3 consecutive `dotnet test` runs; byte-identical psql `\l` snapshot

**Out of scope:**
- **Renaming Schema/Step/Workflow FK columns elsewhere** — Phase 10 ONLY touches `AssignmentEntity.SchemaId` (remove) and `ProcessorEntity.ConfigSchemaId` (add). Step.ProcessorId, Workflow.EntryStepIds/AssignmentIds, Assignment.StepId, Processor.Input/OutputSchemaId all remain untouched.
- **Backfilling ConfigSchemaId on existing rows** — no data-seeding logic to populate the new field for any pre-existing Processor row. Dev DBs are rebuilt from the regenerated migration; rows start clean.
- **Adding a `GetByConfigSchemaId` lookup or any new endpoint** — Phase 10 is field-shape only. `ProcessorService` remains at the 5 CRUD verbs + the Phase 9 `GetBySourceHashAsync`. `AssignmentController/Service` remain at the 5 CRUD verbs.
- **Refactoring the 8 `ProcessorCreateDto` call sites into a helper** — explicit per-site edits preserve per-test blame trail; helper-extraction churn is bundled cleanup work that doesn't belong in a surface-change phase.
- **Additive migration alongside InitialCreate** — explicitly rejected per locked Migration Strategy decision; regenerate in place with a new timestamp is the chosen path.
- **Preserving Assignment.SchemaId data** — no archive column, no value-preservation logic; dev/test-only data means rebuild from clean.
- **Negative-path facts for ConfigSchemaId** (Guid.Empty → 400, non-existent FK → 422 via fk_processor_config_schema_id, Schema-DELETE-while-referenced SetNull verification) — explicitly deferred; the existing InputSchemaId facts (Phase 8 P08) cover the byte-identical code shape, so the new ConfigSchemaId facts limit themselves to happy-path round-trip coverage.
- **New error shapes, new exception types, new ProblemDetails fields** — all error mapping continues through the Phase 4 pipeline unchanged.

## Constraints

- `ConfigSchemaId` MUST mirror `InputSchemaId` behavior exactly: nullable `Guid?`, `DeleteBehavior.SetNull`, lambda-less `HasOne<SchemaEntity>().WithMany()` (no nav properties, satisfies ENTITY-09), `fk_processor_config_schema_id` constraint name matching the Phase 4 PostgresExceptionMapper Option A regex (`fk_<table>_<column>` with no underscore in table segment per PostgresExceptionMapper.cs:54-60 invariant), `When(x => x.ConfigSchemaId.HasValue, () => RuleFor(x => x.ConfigSchemaId!.Value).NotEqual(Guid.Empty))` validator pattern.
- Migration regeneration MUST use a new timestamp (not reuse `20260527203118`) so EF Core detects it as a fresh migration on any dev DB that survived an incomplete teardown. The documented teardown step (`docker compose down -v` + `dotnet ef database drop -f`) MUST be recorded in Phase 10 CONTEXT.md.
- Exactly one migration file (plus its `.Designer.cs`) MUST exist in `src/BaseApi.Service/Persistence/Migrations/` after regeneration alongside `AppDbContextModelSnapshot.cs` — no leftover `20260527203118_*` files.
- All REQUIREMENTS.md amendments MUST preserve the existing REQ-ID numbering (ENTITY-04/07, VALID-11/15) — no superseding, no new REQ-IDs introduced this phase.
- `dotnet build` MUST be zero-warning in both Debug and Release configurations (project-wide `TreatWarningsAsErrors=true` + Mapperly `RMG007/RMG012/RMG020/RMG089` promotion).
- `dotnet test SK_P.sln --no-restore -c Release` MUST report 140/140 passing across 3 consecutive runs (Phase 8/9 convention; pre-existing flake on Run 0 acceptable per Plan 09-03 precedent).
- Phase 3 D-15 byte-identical `psql -l` BEFORE/AFTER snapshot discipline MUST be preserved across the 3-run cycle — zero leaked `stepsdb_test_*` databases.
- No new NuGet dependencies; no new error shapes; no new exception types; no new endpoints; no new entities.

## Acceptance Criteria

- [ ] `grep -n "SchemaId" src/BaseApi.Service/Features/Assignment/` returns zero matches (entity, DTOs, validator, configuration all clean)
- [ ] `grep -n "fk_assignment_schema_id\|ix_assignments_schema_id" src/BaseApi.Service/` returns zero matches in non-binary files
- [ ] `ProcessorEntity` has `public Guid? ConfigSchemaId { get; set; }` and `ProcessorCreate/Update/ReadDto` each list `ConfigSchemaId` as a positional `Guid?` param
- [ ] `ProcessorCreateDtoValidator` + `ProcessorUpdateDtoValidator` each contain a `When(x => x.ConfigSchemaId.HasValue, () => RuleFor(x => x.ConfigSchemaId!.Value).NotEqual(Guid.Empty))` block
- [ ] `ProcessorEntityConfiguration` declares `HasOne<SchemaEntity>().WithMany().HasForeignKey(e => e.ConfigSchemaId).HasConstraintName("fk_processor_config_schema_id").OnDelete(DeleteBehavior.SetNull)`
- [ ] REQUIREMENTS.md ENTITY-04, ENTITY-07, VALID-11, VALID-15 reflect the post-Phase-10 shape; IDs unchanged; footer updated with the Phase 10 amendment note
- [ ] Exactly one migration file (plus its `.Designer.cs`) exists in `src/BaseApi.Service/Persistence/Migrations/` alongside `AppDbContextModelSnapshot.cs`; the old `20260527203118_InitialCreate.*` files are deleted
- [ ] The regenerated migration's `assignments` `CreateTable` block contains no `schema_id` column / FK / index; the `processors` `CreateTable` block contains `config_schema_id uuid NULL` + `fk_processor_config_schema_id` (SetNull) + `ix_processors_config_schema_id`
- [ ] Phase 10 CONTEXT.md (next phase artifact) records the teardown step: `docker compose down -v` + `dotnet ef database drop -f` before the new migration applies
- [ ] All 8 existing `ProcessorCreateDto(...)` call sites supply `ConfigSchemaId: null` (or a value, for the 2 new facts)
- [ ] `AssignmentsIntegrationTests.cs` no longer references `SchemaId` in DTOs or in `CreatePrereqAsync`; `CreatePrereqAsync` returns `Guid stepId` only
- [ ] Two new facts exist in `ProcessorsIntegrationTests.cs`: `Create_ProcessorWithConfigSchemaId_RoundTripsCorrectly` (non-null guid round-trips) and `Create_ProcessorWithNullConfigSchemaId_RoundTripsAsNull` (null round-trips as null)
- [ ] `dotnet build` is zero-warning in Debug + Release
- [ ] `dotnet test SK_P.sln --no-restore -c Release` reports 140/140 passing across 3 consecutive runs
- [ ] SHA-256 of `psql -l` BEFORE and AFTER the 3-run cycle is identical (Phase 3 D-15 cleanup discipline preserved)

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                    |
|--------------------|-------|------|--------|----------------------------------------------------------|
| Goal Clarity       | 0.92  | 0.75 | ✓      | Two named field changes; "mirror InputSchemaId exactly"  |
| Boundary Clarity   | 0.92  | 0.70 | ✓      | 8-item explicit out-of-scope list                        |
| Constraint Clarity | 0.85  | 0.65 | ✓      | Cascade + validation + migration + 3-GREEN locked        |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 15 pass/fail checkboxes                                  |
| **Ambiguity**      | 0.11  | ≤0.20| ✓      | Gate passed in 2 rounds                                  |

## Interview Log

| Round | Perspective      | Question summary                                                      | Decision locked                                                                                                                  |
|-------|------------------|-----------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| 1     | Researcher       | Migration strategy: new additive migration vs regenerate in place     | Regenerate `InitialCreate` in place with a new timestamp; documented teardown (`docker compose down -v` + `dotnet ef database drop -f`); dev/test-only data, no preservation logic |
| 1     | Researcher       | Scope of REQUIREMENTS.md amendments                                   | Amend the 4 locked REQ-IDs (ENTITY-04/07, VALID-11/15) in place; keep IDs intact; bump footer with Phase 10 amendment note        |
| 1     | Researcher       | ConfigSchemaId FK cascade behavior                                    | Mirror InputSchemaId exactly: SetNull, `fk_processor_config_schema_id`, lambda-less HasOne, When().HasValue → NotEqual(Guid.Empty) validator pattern |
| 2     | Simplifier       | Integration-test bar for Phase 10                                     | Mechanical updates to 8 ProcessorCreateDto call sites + AssignmentsIntegrationTests; add 2 new ConfigSchemaId round-trip facts (happy-path + null); 138 → 140 facts; 3 consecutive GREEN |
| 2     | Boundary Keeper  | Explicit out-of-scope items                                           | Renaming other FK columns, ConfigSchemaId backfill, new endpoints, helper-extraction cleanup, additive migration, data preservation, negative-path facts, new error shapes — all explicitly excluded |

---

*Phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o*
*Spec created: 2026-05-28*
*Next step: /gsd-discuss-phase 10 — implementation decisions (e.g., exact order of code edits vs migration regeneration vs test updates; whether RandomSha256Hex stays inline or migrates to a shared helper; whether the new Phase 10 CONTEXT.md captures the teardown sequence as a D-XX decision or as a Pitfall note)*
