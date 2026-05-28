# Phase 10: Remove SchemaId on AssignmentEntity and add ConfigSchemaId on ProcessorEntity - Context

**Gathered:** 2026-05-28
**Status:** Ready for planning

<domain>
## Phase Boundary

Coordinated field-shape revision of the v1 surface — drop `SchemaId` from `AssignmentEntity` (entity + DTOs + validator + EF config + migration + REQUIREMENTS.md + tests) and add nullable `Guid? ConfigSchemaId` to `ProcessorEntity` mirroring `InputSchemaId` behavior exactly (SetNull cascade, lambda-less HasOne, When().HasValue → NotEqual(Guid.Empty) validator, no nav properties). DB migration is regenerated in place with a new timestamp (no additive migration). REQUIREMENTS.md ENTITY-04/07 + VALID-11/15 are amended in place. 138 → 140 facts; 3 consecutive GREEN `dotnet test` runs required.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**7 requirements are locked.** See `10-SPEC.md` for full requirements, boundaries, and acceptance criteria.

Downstream agents MUST read `10-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- `AssignmentEntity.SchemaId` removal across entity + DTOs + validator + EF config + migration + tests + REQUIREMENTS.md
- `ProcessorEntity.ConfigSchemaId` nullable Guid? addition mirroring `InputSchemaId` exactly (SetNull cascade, lambda-less `HasOne<SchemaEntity>().WithMany()`, `fk_processor_config_schema_id`, `When().HasValue → NotEqual(Guid.Empty)` validator pattern, no nav properties)
- DB migration regenerated in place with a new timestamp (old `20260527203118_InitialCreate` files deleted; `AppDbContextModelSnapshot.cs` regenerated)
- REQUIREMENTS.md amends ENTITY-04, ENTITY-07, VALID-11, VALID-15 in place; footer updated; ID numbering preserved
- 8 `ProcessorCreateDto(...)` call sites mechanically updated with `ConfigSchemaId: null`
- `AssignmentsIntegrationTests.CreatePrereqAsync` simplified (drop Schema POST + tuple return)
- 2 new ConfigSchemaId round-trip facts in `ProcessorsIntegrationTests.cs` (happy-path with value, happy-path with null)
- Full regression suite stays GREEN: 140 facts across 3 consecutive runs; byte-identical psql `\l` snapshot (Phase 3 D-15)

**Out of scope (from SPEC.md):**
- Renaming Schema/Step/Workflow FK columns elsewhere
- Backfilling ConfigSchemaId on existing rows
- Adding a `GetByConfigSchemaId` lookup or any new endpoint
- Refactoring the 8 `ProcessorCreateDto` call sites into a shared helper
- Additive migration alongside InitialCreate
- Preserving Assignment.SchemaId data
- Negative-path facts for ConfigSchemaId (Guid.Empty → 400, non-existent FK → 422, Schema-DELETE-while-referenced SetNull verification)
- New error shapes, new exception types, new ProblemDetails fields

</spec_lock>

<decisions>
## Implementation Decisions

### Edit ordering / commit strategy (Area 1)

- **D-01: Doc-first commit ordering (Phase 3 D-03b precedent).** REQUIREMENTS.md amends to ENTITY-04/07 + VALID-11/15 land in commit #1 as a standalone `docs(req): amend ENTITY-04/07 + VALID-11/15 for Phase 10 shape` commit BEFORE any code, EF config, migration, or test commits. Code commits then point back to the amended REQ-IDs as the source of truth. Forensic property: if execution stops at any later commit (#2..#5), REQUIREMENTS.md still reflects the target shape — not v1's old shape — so the next session can resume from a self-consistent spec. Mirrors Plan 03-01's PERSIST-16 introduction pattern verbatim.
- **D-02: Per logical area commits — 4 code commits after the doc-first commit.** The work splits naturally as:
  ```
  #1: docs(req): amend ENTITY-04/07 + VALID-11/15 for Phase 10 shape
  #2: refactor(asm): remove SchemaId from AssignmentEntity (entity + DTOs + validator + EF config)
  #3: feat(proc): add ConfigSchemaId to ProcessorEntity (entity + DTOs + validator + EF config; mirrors InputSchemaId)
  #4: migration: regenerate InitialCreate (drop assignments.schema_id; add processors.config_schema_id)
  #5: test: update 8 ProcessorCreateDto sites + AssignmentsIntegrationTests + 2 new ConfigSchemaId facts
  ```
  Gates: `dotnet build` zero-warning per commit; `dotnet test` 3 consecutive GREEN at commit #5. Each commit is independently revertable. Bisect-friendly (per-file-type splits would leave commits #2/#3/#4 RED on build). Single-bundled commit rejected for the same reason as per-file splits — too coarse for review/revert.
- **D-03: Migration regeneration sits AFTER all code changes, BEFORE test commit.** Order is load-bearing: EF needs the post-Phase-10 model (entity + EF config) to generate the correct migration via `dotnet ef migrations add InitialCreate`. Regenerating before #2/#3 would re-emit the v1 migration (no model delta to capture). Regenerating after #5 would mean tests run against a stale schema. Only the locked ordering produces a correct migration file in one pass.

### Test fixture & ConfigSchemaId round-trip design (Area 2)

- **D-04: 2 new ConfigSchemaId facts live in `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs`; each fact seeds its own Schema inline.** Two new `[Fact]`s appended to the existing file. The non-null-ConfigSchemaId fact POSTs a Schema inline (one HTTP call) before the Processor POST. The null-ConfigSchemaId fact does no Schema POST. Mirrors the Plan 09-03 D-19 precedent: independent `[Fact]`s, no `[Theory]`, clean failure attribution. Phase 9 D-19 \"feature folder\" pattern (`tests/Features/Processor/GetBySourceHashFacts.cs`) deliberately NOT applied — that pattern is for NEW ENDPOINTS; ConfigSchemaId is a FIELD ROUND-TRIP and lives at the same level as the implicit InputSchemaId/OutputSchemaId round-trips in the existing file.
- **D-05: `AssignmentsIntegrationTests.CreatePrereqAsync` simplifies — drop the Schema POST entirely; return `Guid stepId` only.** After Phase 10, `AssignmentEntity` has no `SchemaId`; the helper's existing Schema → Processor → Step chain collapses to Processor → Step (Step.ProcessorId is the only FK target left, and `ProcessorCreateDto` accepts all-null schema FKs per ENTITY-04). Helper signature changes from `Task<(Guid stepId, Guid schemaId)>` to `Task<Guid>`; 3 call sites at lines ~95, ~140, ~180 update from `var (stepId, schemaId) = await CreatePrereqAsync(...)` to `var stepId = await CreatePrereqAsync(...)`. The 2 `NewValidCreateDto(stepId, schemaId)` signatures and the line 131 round-trip assertion lose their `schemaId` argument/assertion.
- **D-06: Reuse `Phase8WebAppFactory` for the 2 new facts — no `Phase10WebAppFactory`.** Phase 8 D-01 fixture is the canonical integration-test factory; already serves all Wave B smokes (Schemas/Processors/Steps/Assignments/Workflows), Phase 9 `GetBySourceHashFacts`, and Phase 9 `Start/StopOrchestrationFacts`. Behavioral delta of a Phase10WebAppFactory subclass would be zero (the per-class throwaway DB fixture is shape-agnostic to model changes). Keep `[Trait("Phase8Wave", "B")]` on ProcessorsIntegrationTests; the 2 new facts inherit the class trait without their own `[Trait("Phase", "10")]` tag (consistent with how Wave B smokes were tagged in Phase 8).

### Migration regeneration mechanics & verification (Area 3)

- **D-07: Migration regeneration teardown ordering — captured as BOTH a CONTEXT.md decision AND a Plan Task 0 step.**

  **Teardown sequence (must run BEFORE `dotnet ef migrations add InitialCreate`):**
  ```
  1. docker compose down -v                                                    # tears down pgdata volume
  2. dotnet ef database drop -f -p src/BaseApi.Service -s src/BaseApi.Service  # drops stepsdb on local Postgres
  3. rm src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.cs
  4. rm src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.Designer.cs
  5. dotnet ef migrations add InitialCreate -p src/BaseApi.Service -s src/BaseApi.Service
  6. docker compose up -d postgres                                             # restart pristine
  7. dotnet test (StartupCompletionService.MigrateAsync runs against fresh DB)
  ```

  Both layers required: CONTEXT.md captures the WHY for cross-session reviewability; Plan Task 0 turns the WHAT into executable steps the executor MUST run. Without the doc, the teardown rationale is lost. Without the plan task, the executor may invoke `dotnet ef migrations add` before deleting old files (which produces a SECOND migration file alongside the first — silently violates the SPEC).

- **D-08: \"Exactly one InitialCreate migration file\" invariant enforced via plan-level grep + file-count gate.** The migration plan's verification step must include:
  ```bash
  # Old InitialCreate files must NOT exist
  test ! -f src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.cs
  test ! -f src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.Designer.cs

  # Exactly 3 .cs files in Migrations/ (new InitialCreate + Designer + ModelSnapshot)
  cnt=$(ls src/BaseApi.Service/Persistence/Migrations/*.cs | wc -l)
  [[ $cnt -eq 3 ]] || { echo "FAIL: expected 3 .cs files, got $cnt"; exit 1; }

  # ModelSnapshot must exist
  test -f src/BaseApi.Service/Persistence/Migrations/AppDbContextModelSnapshot.cs

  # `schema_id` references in the new InitialCreate.cs must only appear under the processors block
  # (input_schema_id, output_schema_id, config_schema_id — 3 columns + their FKs + their indexes)
  grep -c "schema_id" src/BaseApi.Service/Persistence/Migrations/*InitialCreate.cs   # expected count to be locked in plan after a dry-run

  # `assignments` table block must NOT mention schema_id, fk_assignment_schema_id, or ix_assignments_schema_id
  ! grep -E "fk_assignment_schema_id|ix_assignments_schema_id" src/BaseApi.Service/Persistence/Migrations/*InitialCreate.cs
  ```
  Plan verification FAILS the migration commit (commit #4) atomically if any line fails. Forensic-friendly — no \"silently lands a second migration file\" failure mode.

- **D-09: No sidecar SQL script (no `dotnet ef migrations script` artifact in Phase 10 directory).** The regenerated migration file IS the source of truth and lives in git. A separate SQL script would duplicate content, rot independently from the migration, and not be consumed by any downstream tool. The diff of commit #4 (migration regenerated) IS the snapshot review surface: it shows DELETED old `20260527203118_InitialCreate.*` files + CREATED `<new-timestamp>_InitialCreate.*` files + MODIFIED `AppDbContextModelSnapshot.cs`. The code-review pass on commit #4 substitutes for any sidecar artifact.

### Mapperly drift probe (Area 4)

- **D-10: No Mapperly drift probe in Phase 10 — trust Phase 6 enforcement.** RMG012 + RMG020 + RMG089 are promoted globally via `Directory.Build.props` (Plan 06-01) and have caught asymmetric drift across Phases 6/7/8/9 without false-negatives. Phase 10 introduces only SYMMETRIC field changes — `Assignment.SchemaId` removed from BOTH entity and DTOs together; `Processor.ConfigSchemaId` added to BOTH entity and DTOs together. The drift-probe pattern (Plan 06-02 Check 4) proves the safety net catches asymmetric changes; it has no diagnostic value for a phase that introduces no asymmetry. If drift confidence ever erodes, the probe lives in `/gsd-validate-phase` as a retroactive audit pass — not in the forward execution plan. Code-review phase will visually verify Mapperly attributes on `ProcessorEntityMapper` (zero changes expected) and `AssignmentEntityMapper` (zero changes expected; the 5 `[MapperIgnoreTarget]`s for Id + 4 audit fields stay byte-identical).

### Claude's Discretion

- Exact wording of REQUIREMENTS.md amendments (the SHAPE is locked in SPEC.md REQ-5 + this CONTEXT D-01..D-03; the prose phrasing is at the planner/executor's discretion as long as it preserves the locked semantics).
- Exact `[Trait]` tag values on the 2 new ConfigSchemaId facts (D-06 says \"inherit Phase8Wave=B from class\"; if the planner finds a stronger convention by inspecting Phase 8's actual `[Trait]` usage, they may diverge — provided they document it).
- Exact wording of new XML `<summary>` doc-comments on `ProcessorEntity.ConfigSchemaId` and the updated `AssignmentEntity` (must preserve the source/sink/config wording pattern from existing InputSchemaId/OutputSchemaId comments).

### Folded Todos

None — `.planning/todos/pending/` does not exist; no todos were captured during the session.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase 10 spec (locked requirements)

- `.planning/phases/10-remove-schemaid-on-assignmententity-and-add-configschemaid-o/10-SPEC.md` — **Locked requirements** — MUST read before planning. 7 requirements + 8-item out-of-scope list + 15 pass/fail acceptance criteria.

### Project-level spec

- `.planning/REQUIREMENTS.md` — Specifically:
  - **ENTITY-04** (line ~60) — current Processor shape including `InputSchemaId Guid?`/`OutputSchemaId Guid?`; Phase 10 amends to add `ConfigSchemaId Guid?`.
  - **ENTITY-07** (line ~66) — current Assignment shape `StepId Guid (FK→Step), SchemaId Guid (FK→Schema), Payload string (jsonb)`; Phase 10 amends to drop SchemaId.
  - **VALID-11** (line ~131) — nullable Guid? `When().HasValue → NotEqual(Guid.Empty)` pattern; Phase 10 amends to add ConfigSchemaId to the rule list.
  - **VALID-15** (line ~140) — `AssignmentCreate/UpdateDto.StepId/SchemaId: NotEmpty Guid`; Phase 10 amends to drop SchemaId rule.
  - **PERSIST-12, PERSIST-13, ERROR-04, ERROR-05, ERROR-11** — FK constraint and SQLSTATE mapping rules that the regenerated migration must still satisfy (the migration must still produce `fk_processor_config_schema_id`-style constraint names so Phase 4 `PostgresExceptionMapper` Option A regex continues to map 23503 → 422).

### Code anchors (current state on disk, mid-Phase-9)

- `src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs:24-29` — current entity shape (StepId + SchemaId + Payload)
- `src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs` — Create/Update/ReadDto record positional shapes
- `src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs:42-44, 83-85` — VALID-15 SchemaId rules
- `src/BaseApi.Service/Persistence/Configurations/AssignmentEntityConfiguration.cs:44-49` — `fk_assignment_schema_id` FK block (to be removed)
- `src/BaseApi.Service/Features/Processor/ProcessorEntity.cs:23-28` — current entity shape (SourceHash + InputSchemaId? + OutputSchemaId?)
- `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` — Create/Update/ReadDto record positional shapes (Phase 10 adds ConfigSchemaId as 7th positional / 12th on Read)
- `src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs:35-47` — `When().HasValue → NotEqual(Guid.Empty)` pattern for Input/Output (Phase 10 adds a third When-block for ConfigSchemaId, mirroring verbatim)
- `src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs:31-41` — existing `fk_processor_input_schema_id` + `fk_processor_output_schema_id` blocks (Phase 10 adds a third `fk_processor_config_schema_id` block with `OnDelete(SetNull)`)
- `src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.cs` — current migration (will be DELETED and regenerated with new timestamp)
- `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs:54-67` — FK regex `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` with INVARIANT \"table segment contains no underscore\" — `fk_processor_config_schema_id` parses as table=\"processor\", column=\"config_schema_id\" (satisfies invariant)
- `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs:45-82, 84-90, 131, 174` — call sites updated by D-05
- `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` — 2 new `[Fact]`s appended per D-04
- 8 `ProcessorCreateDto(...)` mechanical-update sites: `ErrorMappingFacts.cs:44-45, 115-116`; `WorkflowsIntegrationTests.cs:61-62`; `AssignmentsIntegrationTests.cs:63-64`; `StepsIntegrationTests.cs:52-53`; `ProcessorsIntegrationTests.cs:44-45, 119-120`; `StartOrchestrationFacts.cs:61-62`; `StopOrchestrationFacts.cs:49-50`; `GetBySourceHashFacts.cs:51-52`

### Prior phase CONTEXT.md files relevant to Phase 10

- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` — D-15 byte-identical psql `\l` cleanup discipline; D-18 3-consecutive-GREEN convention; D-03b doc-first precedent
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-CONTEXT.md` — PostgresExceptionMapper Option A regex; ERROR-11 constraint naming convention
- `.planning/phases/06-validation-mapping-base/06-CONTEXT.md` — RMG007/RMG012/RMG020/RMG089 promotion; AddValidatorsFromAssembly auto-discovery
- `.planning/phases/08-entity-build-out-migrations-docker-runtime-tests/08-CONTEXT.md` — Phase8WebAppFactory pattern; per-class throwaway DB via PostgresFixture; explicit constraint-name convention; lambda-less HasOne pattern; `OnDelete(SetNull)` for nullable Schema FKs

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets

- **`Phase8WebAppFactory`** (`tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`) — public, non-sealed; encapsulates `PostgresFixture` for per-class throwaway DB. Already used by all Wave B smokes + Phase 9 facts; Phase 10 facts inherit the pattern without modification.
- **`PostgresFixture`** (`tests/BaseApi.Tests/Composition/PostgresFixture.cs`) — per-class throwaway Postgres DB on the docker-compose Postgres at `localhost:5433`. Phase 3 D-15 byte-identical psql `\l` SHA-256 snapshot proven across 138 facts spanning Phases 3-9. Phase 10's 2 new facts inherit this proof.
- **`ProcessorEntityMapper`** (`src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs`) — audit-symmetric Mapperly partial; zero changes needed for Phase 10 because the new `ConfigSchemaId` field is added to BOTH entity and DTOs simultaneously (no asymmetry → no RMG012/RMG020 fires).
- **`AssignmentEntityMapper`** (`src/BaseApi.Service/Features/Assignment/AssignmentEntityMapper.cs`) — same audit-symmetric pattern; zero changes needed for Phase 10 because `SchemaId` is removed from BOTH entity and DTOs simultaneously.
- **`BaseDtoValidator<T>`** + **`Include(new BaseDtoValidator<...>())`** pattern (Phase 6 VALID-20) — Phase 10 validators (ProcessorCreate/UpdateDtoValidator) continue to include the base; the new ConfigSchemaId rule sits alongside the existing SourceHash + InputSchemaId + OutputSchemaId rules with the same `When().HasValue` shape.
- **`PostgresExceptionMapper.FkRegex`** (`src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs:61-63`) — `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` with INVARIANT: table segment contains no underscore. `fk_processor_config_schema_id` parses cleanly as table=\"processor\", column=\"config_schema_id\" — invariant satisfied. No changes needed.

### Established Patterns

- **Lambda-less `HasOne<SchemaEntity>().WithMany()`** (Plan 08-03 + ENTITY-09) — Phase 10's new ConfigSchemaId FK block reuses verbatim; satisfies \"no navigation properties between entities\" while creating the Postgres FK constraint.
- **Explicit `HasConstraintName(\"fk_<table>_<column>\")`** (Plan 08-03 + ERROR-11) — Phase 10's new FK is named `fk_processor_config_schema_id` (matches Phase 4 PostgresExceptionMapper Option A regex).
- **`OnDelete(DeleteBehavior.SetNull)` for nullable Schema FKs** (RESEARCH §Cascade behaviors line 582) — Phase 10's new `ConfigSchemaId` inherits this; deleting a referenced Schema sets ConfigSchemaId to null, NOT cascade-delete the Processor.
- **`When(x => x.Field.HasValue, () => RuleFor(x => x.Field!.Value).NotEqual(Guid.Empty))`** (VALID-11 + Plan 08-03) — Phase 10's new ConfigSchemaId validator rules use this pattern verbatim; null is valid, non-null Guid.Empty fails 400, non-existent Guid surfaces as 23503 → 422 via Phase 4 mapper.
- **Audit-symmetric ReadDto** (Plan 08-02 + HTTP-07) — Phase 10's `ProcessorReadDto.ConfigSchemaId` joins the existing 11-positional → 12-positional record; no `[MapperIgnoreSource]` needed (Read DTO carries audit fields).
- **3 consecutive GREEN `dotnet test` runs** (Phase 3 D-18 + Phase 8 P08 + Phase 9 P09-03) — Phase 10's test verification step inherits; pre-existing flake on Run 0 acceptable per Plan 09-03 precedent.
- **Byte-identical psql `\l` BEFORE/AFTER SHA-256 snapshot** (Phase 3 D-15) — Phase 10's migration regeneration must preserve this proof across the 3-run cycle (zero leaked `stepsdb_test_*` databases).
- **`StartupCompletionService` MigrateAsync swap** (Plan 08-07) — applies the regenerated InitialCreate on first boot; no change to the service code needed.
- **`xmin` shadow concurrency token via `BaseDbContext.OnModelCreating`** (Phase 3 PERSIST-16) — applied to all BaseEntity subclasses via model-builder iteration; ProcessorEntity + AssignmentEntity remain BaseEntity subclasses, so xmin annotation regenerates automatically in the new InitialCreate migration. No explicit wiring needed in Phase 10.

### Integration Points

- `AssignmentEntity` continues to inherit `BaseEntity` (Id + Name + Version + Description + audit fields + xmin); only the `SchemaId` property is removed.
- `ProcessorEntity` continues to inherit `BaseEntity`; the new `ConfigSchemaId` property is added as a 4th non-base field alongside `SourceHash + InputSchemaId? + OutputSchemaId?`.
- `AppDbContext.OnModelCreating` (Plan 08-07) uses `ApplyConfigurationsFromAssembly` — both updated `AssignmentEntityConfiguration` and `ProcessorEntityConfiguration` are picked up automatically; no AppDbContext changes needed.
- DI registration: `AddProcessorFeature()` + `AddAssignmentFeature()` in `Program.cs` and their respective `ServiceCollectionExtensions` files do NOT change shape (the validators are still auto-discovered by `AddValidatorsFromAssembly`).
- `dotnet ef migrations add InitialCreate -p src/BaseApi.Service -s src/BaseApi.Service` is the canonical command (Plan 08-07 precedent); the regenerated `AppDbContextModelSnapshot.cs` is the EF Core canonical artifact and MUST be committed alongside the migration.

</code_context>

<specifics>
## Specific Ideas

- The new ConfigSchemaId field's XML doc-comment on `ProcessorEntity` should preserve the source/sink/config wording of the existing `InputSchemaId`/`OutputSchemaId` comments — e.g., \"supports source/sink/config-only processors (no input / no output / no config)\". The planner may rephrase slightly to capture the trinary symmetry but must preserve the \"null is permitted\" semantics.
- The PostgresExceptionMapper.cs INVARIANT comment (lines 54-60) does NOT need editing — \"processor\" satisfies \"table segment contains no underscore\" for `fk_processor_config_schema_id`. Add this as an explicit verification line in the Phase 10 plan so the reviewer doesn't have to re-derive the invariant.
- The 2 new ConfigSchemaId facts should follow the existing ProcessorsIntegrationTests fact naming pattern (verb-shape-suffix, e.g., `Create_ProcessorWithConfigSchemaId_RoundTripsCorrectly`) rather than the Phase 9 features-folder naming style (e.g., `GetBySourceHash_NonExistentHash_Returns404`) — the former matches the file's existing convention.

</specifics>

<deferred>
## Deferred Ideas

- **Negative-path facts for ConfigSchemaId** (Guid.Empty → 400, non-existent FK → 422 via `fk_processor_config_schema_id`, Schema-DELETE-while-referenced SetNull verification). Explicitly excluded by SPEC.md out-of-scope list. Defer to a future hardening phase or `/gsd-validate-phase 10` retroactive audit if confidence ever erodes.
- **`MaximumCount` cap on the WorkflowIds validator** (Phase 9 D-08 deferred this). Not related to Phase 10 but flagged in Phase 9 CONTEXT — surface here for traceability.
- **Pagination/filtering/sorting on list endpoints (HTTP-17/18/19)** — already in REQUIREMENTS.md v2 backlog; not Phase 10's concern.
- **VALID-21 dynamic schema conformance** (Assignment.Payload validated against the SchemaEntity referenced by Assignment.SchemaId) — was already deferred to v2 in Phase 8. Now that AssignmentEntity loses SchemaId entirely, VALID-21 becomes structurally impossible to implement at the existing layer; any future revival would need a new design (e.g., a Processor.ConfigSchemaId-referenced schema for payload conformance). Out of Phase 10 scope; flagged for v2 reconsideration.
- **Helper extraction for the 8 `ProcessorCreateDto(...)` call sites** — explicitly excluded by SPEC.md (\"don't bundle cleanup with surface change\"); defer to a future test-cleanup phase if churn accumulates.
- **Sidecar SQL script snapshot of the regenerated migration** — rejected as D-09 to avoid duplicate-source-of-truth rot; defer to a separate documentation phase if external SQL-review consumers ever emerge.

### Reviewed Todos (not folded)

None — `.planning/todos/pending/` does not exist.

</deferred>

---

*Phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o*
*Context gathered: 2026-05-28*
