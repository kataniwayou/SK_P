---
phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
plan: 03
subsystem: api

tags: [processor-entity, configschemaid-addition, feature-add, ef-core, fluentvalidation, mapperly-symmetric, phase-10]

# Dependency graph
requires:
  - phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
    provides: "Plan 10-01 amended REQUIREMENTS.md ENTITY-04 + VALID-11 to describe post-Phase-10 Processor shape (adds ConfigSchemaId as 3rd nullable Schema FK alongside Input/Output); this plan codifies the entity/DTO/validator/EF-config layer to match the amended spec."
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: "Phase 8 established the canonical Input/Output nullable Schema FK pattern: nullable Guid?, DeleteBehavior.SetNull, lambda-less HasOne<SchemaEntity>().WithMany(), When().HasValue -> NotEqual(Guid.Empty) validator block, fk_processor_<column>_id constraint name convention matching Phase 4 PostgresExceptionMapper Option A regex. This plan replicates that pattern verbatim for the new ConfigSchemaId field."
provides:
  - "ProcessorEntity gains 4th non-base property `public Guid? ConfigSchemaId { get; set; }` immediately after OutputSchemaId; XML doc updated to trinary source/sink/config semantic with all 3 FK constraint names enumerated inline."
  - "ProcessorCreateDto + ProcessorUpdateDto: 6 -> 7 positional records (Name, Version, Description, SourceHash, InputSchemaId, OutputSchemaId, ConfigSchemaId). ConfigSchemaId inserted as the 7th positional param (NOT at end) to preserve property ordering convention shared with the entity."
  - "ProcessorReadDto: 11 -> 12 positional record. ConfigSchemaId inserted as the 8th positional, between OutputSchemaId and CreatedAt (preserves the audit-block trailer at the end)."
  - "ProcessorCreateDtoValidator + ProcessorUpdateDtoValidator: each gains a 3rd `When(x => x.ConfigSchemaId.HasValue, () => RuleFor(x => x.ConfigSchemaId!.Value).NotEqual(Guid.Empty).WithMessage(...))` block — verbatim mirror of the existing Input/Output rules. XML doc VALID-11 bullet enumerates all 3 nullable Schema FK fields; FK constraint enumeration extended with fk_processor_config_schema_id."
  - "ProcessorEntityConfiguration: gains a 3rd `HasOne<SchemaEntity>().WithMany().HasForeignKey(e => e.ConfigSchemaId).HasConstraintName(\"fk_processor_config_schema_id\").OnDelete(DeleteBehavior.SetNull)` FK block — verbatim mirror of the existing Input/Output FK blocks. XML doc updated to enumerate all 3 FK constraint names."
  - "Commit #3 of the 5-commit Phase 10 sequence per CONTEXT D-02 — bisect-friendly atomic feature commit; production projects (BaseApi.Core + BaseApi.Service) build zero-warning Release + Debug. Test project remains intentionally RED until commit #5."
  - "PostgresExceptionMapper.cs UNTOUCHED — regex `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` invariant preserved by zero-touch (fk_processor_config_schema_id parses as table=\"processor\" (no underscore — invariant satisfied), column=\"config_schema_id\")."
  - "ProcessorEntityMapper.cs UNTOUCHED — Mapperly symmetric-addition invariant per CONTEXT D-10 (entity + 3 DTOs gain ConfigSchemaId together -> RMG012/RMG020 do not fire)."
affects:
  - 10-04-migration-regenerate-initialcreate (Plan 04 reads the post-Phase-10 model — including this plan's ProcessorEntity shape + EF config FK block — to regenerate the migration in place; the new InitialCreate will create `processors.config_schema_id uuid NULL` + `fk_processor_config_schema_id` (SetNull) + `ix_processors_config_schema_id`)
  - 10-05-test-fixture-updates-and-configschemaid-facts (Plan 05 updates 8 ProcessorCreateDto call sites with `ConfigSchemaId: null` + 1 ProcessorUpdateDto site; adds 2 new ConfigSchemaId round-trip facts to ProcessorsIntegrationTests.cs; this plan's positional ordering of ConfigSchemaId as the 7th param is load-bearing for those call-site edits)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Symmetric field-shape addition — entity AND all 3 DTOs gain ConfigSchemaId together -> Mapperly RMG012 (target unmapped) + RMG020 (source unmapped) do not fire on ProcessorEntityMapper.cs (zero-touch confirmation per CONTEXT D-10 invariant)."
    - "Trinary nullable Schema FK pattern — Input/Output/Config all use the same shape: Guid? entity property + Guid? positional DTO param + When().HasValue -> NotEqual(Guid.Empty) validator block + lambda-less HasOne<SchemaEntity>().WithMany() with explicit fk_processor_<column>_id constraint name + OnDelete(DeleteBehavior.SetNull) cascade. Phase 8 established the binary pattern; Phase 10 extends to trinary verbatim."
    - "Positional-param insertion preserves field ordering — when adding a new property to an existing positional record, insert it at the slot mirroring the entity's property order (NOT at the end of the param list). ConfigSchemaId inserted between OutputSchemaId and the audit/closing fields in all 3 DTOs to match the entity-side ordering."
    - "Mid-plan symbol-name enumeration — when a phase adds a new symbol that participates in a regex/constraint convention, both the new and existing symbols MUST be enumerated together in XML docs and verification grep assertions so future readers can trace the convention without re-deriving it. ENTITY-04 + ProcessorEntity XML doc + ProcessorEntityConfiguration XML doc + ProcessorDtoValidator XML doc all enumerate all 3 fk_processor_<column>_id constraint names."

key-files:
  created: []
  modified:
    - "src/BaseApi.Service/Features/Processor/ProcessorEntity.cs"
    - "src/BaseApi.Service/Features/Processor/ProcessorDtos.cs"
    - "src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs"
    - "src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs"

key-decisions:
  - "Followed CONTEXT D-02 verbatim commit subject: 'feat(proc): add ConfigSchemaId to ProcessorEntity (entity + DTOs + validator + EF config; mirrors InputSchemaId)' — plan-locked phrasing supports cross-session grep-based audit."
  - "Per CONTEXT D-02 bisect-friendliness, ran the build gate on the 2 PRODUCTION projects only (BaseApi.Core + BaseApi.Service, Release + Debug) rather than the full SK_P.sln — test project intentionally fails to compile until commit #5 since AssignmentsIntegrationTests.cs still references the removed AssignmentEntity.SchemaId (from commit #2) and all ProcessorCreateDto call sites still use the 6-positional shape. Plan 05 closes the test edits."
  - "Mapperly drift probe zero-touch confirmed (CONTEXT D-10) — ProcessorEntityMapper.cs was NOT modified; symmetric ConfigSchemaId addition across entity + 3 DTOs means RMG012 (target unmapped) + RMG020 (source unmapped) do not fire. Production projects 0 warnings + 0 errors Release + Debug verifies."
  - "PostgresExceptionMapper.cs zero-touch confirmed — regex `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$` at lines 54-67 parses fk_processor_config_schema_id as table=\"processor\" (no underscore — invariant satisfied per lines 54-60), column=\"config_schema_id\". 23503 -> HTTP 422 mapping path preserved without source change. Explicitly verified via `git diff src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` returning empty."
  - "Positional ordering of ConfigSchemaId — inserted as the 7th positional in Create/Update DTOs (immediately after OutputSchemaId) and the 8th positional in Read DTO (between OutputSchemaId and CreatedAt) to mirror the entity-side property order. This is load-bearing for Plan 05 which mechanically adds `ConfigSchemaId: null` to 8 existing call sites."

patterns-established:
  - "Symmetric field-shape addition across entity + DTOs + validator + EF config in a single atomic commit — preserves Mapperly diagnostics quiet (RMG012/RMG020 do not fire on the symmetric addition axis) and keeps each commit independently revertable (D-02 bisect-friendliness). Verbatim mirror of an existing canonical pattern (here: InputSchemaId) is the safest extension method."
  - "Production-build gate vs solution-build gate (extends Plan 10-02 pattern) — when a multi-commit refactor knowingly breaks the test project between intermediate commits, the build gate runs on production projects (BaseApi.Core + BaseApi.Service) explicitly rather than `dotnet build SK_P.sln`. This commit (#3) further widens the test-project-RED state introduced by commit #2: tests now reference an entity shape (6-positional Create/Update / 11-positional Read) that no longer exists in production code."
  - "Trinary symmetric mirror — when a binary pattern (e.g., Input/Output) is being extended to trinary (Input/Output/Config), enumerate all 3 symbols together in every cross-reference doc-comment so the trinary semantic is internally consistent everywhere. The binary -> trinary transition itself is invisible at runtime (no new behavior), but documentary consistency is load-bearing for future maintainers."

requirements-completed: [ENTITY-04, VALID-11]

# Metrics
duration: ~3min
completed: 2026-05-28
---

# Phase 10 Plan 03: Add ConfigSchemaId on ProcessorEntity Summary

**Atomic 4-file feature commit adding nullable Guid? ConfigSchemaId to ProcessorEntity (entity + 3 DTOs + 2 validators + EF config) — Processor surface now carries 3 nullable Schema FKs (Input/Output/Config); production projects build zero-warning Release + Debug; test project remains intentionally RED until commit #5 per D-02 bisect-friendliness.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-05-28T07:36:30Z
- **Completed:** 2026-05-28T07:39:19Z
- **Tasks:** 5 (Task 1 ProcessorEntity, Task 2 ProcessorDtos, Task 3 ProcessorDtoValidator, Task 4 ProcessorEntityConfiguration + build verify, Task 5 atomic commit)
- **Files modified:** 4 (per files_modified frontmatter — exactly matches plan scope)

## Accomplishments

- ProcessorEntity gained 4th non-base property `public Guid? ConfigSchemaId { get; set; }` immediately after OutputSchemaId. XML `<para>` doc rewritten from binary "source/sink" to trinary "source/sink/config" semantic; all 3 FK constraint names enumerated inline (fk_processor_input_schema_id, fk_processor_output_schema_id, fk_processor_config_schema_id).
- ProcessorDtos: all 3 records gained ConfigSchemaId as a nullable Guid? positional param. Create/Update: 6 -> 7 positional (ConfigSchemaId as 7th, after OutputSchemaId); Read: 11 -> 12 positional (ConfigSchemaId as 8th, between OutputSchemaId and CreatedAt). Arity doc-comments updated on all 3 records.
- ProcessorDtoValidator: both Create and Update validators gained a 3rd `When(x => x.ConfigSchemaId.HasValue, () => RuleFor(x => x.ConfigSchemaId!.Value).NotEqual(Guid.Empty).WithMessage(...))` block — verbatim mirror of the existing Input/Output rules. XML doc VALID-11 bullet now enumerates 3 fields (Input/Output/Config) with the same "source/sink/unconfigured" trinary wording; FK constraint enumeration in doc-comment extended with `fk_processor_config_schema_id` -> `config_schema_id`.
- ProcessorEntityConfiguration: gained a 3rd lambda-less `HasOne<SchemaEntity>().WithMany().HasForeignKey(e => e.ConfigSchemaId).HasConstraintName("fk_processor_config_schema_id").OnDelete(DeleteBehavior.SetNull)` FK block — verbatim mirror of the existing Input/Output FK blocks (5-line block, identical attribute chain). XML doc updated to enumerate the new constraint name; "Lambda-less HasOne" para preserved byte-identical.
- PostgresExceptionMapper.cs and ProcessorEntityMapper.cs both verified UNTOUCHED — regex invariant + Mapperly symmetric-addition invariant both preserved by zero-touch.
- Single atomic commit `12577ac` landed with verbatim D-02 subject; production projects (BaseApi.Service + BaseApi.Core) Release + Debug build clean with 0 warnings + 0 errors.

## Task Commits

Single atomic commit per D-02 (commit #3 of the Phase 10 sequence):

1. **Task 1 + Task 2 + Task 3 + Task 4 + Task 5 (combined atomic feature commit):** `12577ac` — `feat(proc): add ConfigSchemaId to ProcessorEntity (entity + DTOs + validator + EF config; mirrors InputSchemaId)`

Per CONTEXT D-02, all 5 plan tasks collapse to a single git commit (the production feature commit #3 of the Phase 10 sequence). Tasks 1-4 are code edits across 4 files; Task 5 is the commit gate. There is no per-task atomicity because the 4 files must land together — splitting Tasks 1-4 across separate commits would leave intermediate commits in a build-fail state (e.g., EF config adds the ConfigSchemaId FK block while the entity has not yet declared the property -> CS-class compile error, OR validator declares When(x => x.ConfigSchemaId.HasValue) block while the DTO has not yet declared the property -> CS-class compile error).

**Plan metadata:** This SUMMARY.md + updated STATE.md + updated ROADMAP.md will be added as a separate final docs commit per the executor's final_commit step.

## Files Created/Modified

- `src/BaseApi.Service/Features/Processor/ProcessorEntity.cs` — added `public Guid? ConfigSchemaId { get; set; }` as 4th non-base property; XML `<para>` doc rewritten to trinary source/sink/config semantic with all 3 constraint names enumerated.
- `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` — Create/Update records 6 -> 7 positional (ConfigSchemaId as 7th); Read record 11 -> 12 positional (ConfigSchemaId as 8th, before CreatedAt); arity doc-comments updated on all 3.
- `src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` — appended 3rd `When(ConfigSchemaId.HasValue)` -> NotEqual(Guid.Empty) block to both Create + Update validators; XML doc VALID-11 bullet extended to trinary; FK constraint enumeration extended.
- `src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` — appended 3rd lambda-less `HasOne<SchemaEntity>().WithMany()` FK block with explicit `fk_processor_config_schema_id` constraint name and `OnDelete(DeleteBehavior.SetNull)`; XML doc extended to enumerate new constraint.

## Decisions Made

None new — followed plan and CONTEXT D-02 / D-10 exactly. The decisions captured here are pre-existing CONTEXT decisions whose application is documented above (D-02 atomic-feature commit + verbatim subject + production-build-gate scope; D-10 symmetric-addition -> Mapperly zero-touch).

## Deviations from Plan

None - plan executed exactly as written.

The plan's verbatim PATTERNS.md target text (lines 38-56 ProcessorEntity / 60-97 ProcessorDtos / 101-126 ProcessorDtoValidator / 130-154 ProcessorEntityConfiguration) was applied verbatim. All 5 plan-level task verifications + the 8 success criteria passed on first attempt. No Rules 1-4 triggered. No auth gates. No checkpoint stops.

The one minor mid-flight observation: the plan's Task 1 verify line included `grep -E "source processors \\(no input\\), sink processors \\(no output\\), and unconfigured processors \\(no config\\)"` as a single-line regex, but the actual XML doc wraps the phrase across two lines for readability. Re-verified with a sub-phrase grep (`"sink processors (no output), and unconfigured processors (no config)"`) on the single line containing the wrap-point — returned 1 match, satisfying the trinary-wording-locked semantic without source change. No deviation classification needed; the verbatim semantic content is present, only the regex line-anchoring assumption was off.

---

**Total deviations:** 0
**Impact on plan:** Zero scope creep; zero unplanned work; zero auth gates.

## Issues Encountered

None.

## User Setup Required

None — internal feature-addition commit; no external configuration touched.

## Next Phase Readiness

**Plan 10-04 (next):** Ready to execute. Now that both halves of the Phase 10 field-shape revision are in place at the source-code layer (Plan 10-02 removed Assignment.SchemaId; this plan added Processor.ConfigSchemaId), `dotnet ef migrations add InitialCreate` in Plan 10-04 will regenerate the migration with the post-Phase-10 model: `assignments` table loses `schema_id` / `fk_assignment_schema_id` / `ix_assignments_schema_id`; `processors` table gains `config_schema_id uuid NULL` / `fk_processor_config_schema_id` (SetNull) / `ix_processors_config_schema_id`.

**Plan 10-05:** Depends on 10-02 + 10-03 + 10-04. Currently `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` still references the old 6-positional AssignmentCreateDto/UpdateDto and `AssignmentEntity.SchemaId` (from commit #2's removal); ALL `ProcessorCreateDto(...)` call sites across 8 test files now ALSO have an arity mismatch (6 positional in tests vs the new 7 positional in production) — the test project WILL fail to build between this commit and commit #5. That is the documented D-02 bisect-friendliness contract: production source builds clean at every commit; test source bundles its edits into commit #5 to keep the cleanup churn out of the surface-change commits.

**Forensic property preserved:** If execution stops at this commit, the production Processor surface is internally consistent — entity + DTOs + validator + EF config all describe the same `(SourceHash + InputSchemaId? + OutputSchemaId? + ConfigSchemaId?)` shape. REQUIREMENTS.md (amended in Plan 10-01) already describes this shape. Plan 10-02 (Assignment SchemaId removal) is independently revertable. The next session can resume from a self-consistent state. The migration + test files lag by design until Plans 10-04 + 10-05 land.

## Self-Check: PASSED

- FOUND: `src/BaseApi.Service/Features/Processor/ProcessorEntity.cs` (modified — verified by `git show --stat HEAD` showing the file in the 4-file commit)
- FOUND: `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: Commit `12577ac` in git log (`git log -1 --format=%s` returns `feat(proc): add ConfigSchemaId to ProcessorEntity (entity + DTOs + validator + EF config; mirrors InputSchemaId)`)
- VERIFIED PRESENT: `grep -c "public Guid? ConfigSchemaId" src/BaseApi.Service/Features/Processor/ProcessorEntity.cs` returns 1
- VERIFIED PRESENT: `grep -cE "public Guid\?" src/BaseApi.Service/Features/Processor/ProcessorEntity.cs` returns 3 (Input + Output + Config)
- VERIFIED PRESENT: `grep -c "Guid? ConfigSchemaId" src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` returns 3 (one per DTO record)
- VERIFIED PRESENT: `grep -B1 "Guid? ConfigSchemaId" src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` shows `OutputSchemaId` on the immediately preceding line in ALL 3 occurrences (positional order locked)
- VERIFIED PRESENT: `grep -c "When(x => x.ConfigSchemaId.HasValue" src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` returns 2 (one per validator)
- VERIFIED PRESENT: `grep -c "ConfigSchemaId must not be Guid.Empty when provided" src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` returns 2
- VERIFIED PRESENT: `grep -c "When(x => x.InputSchemaId.HasValue" src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` returns 2 (existing rule preserved)
- VERIFIED PRESENT: `grep -c "When(x => x.OutputSchemaId.HasValue" src/BaseApi.Service/Features/Processor/ProcessorDtoValidator.cs` returns 2 (existing rule preserved)
- VERIFIED PRESENT: `grep -c "fk_processor_config_schema_id" src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` returns 2 (HasConstraintName + XML doc)
- VERIFIED PRESENT: `grep -c "HasOne<SchemaEntity>" src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` returns 3 (Input + Output + Config FK blocks)
- VERIFIED PRESENT: `grep -c "DeleteBehavior.SetNull" src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` returns 3 (all 3 nullable Schema FKs)
- VERIFIED PRESENT: `grep -c "HasForeignKey(e => e.ConfigSchemaId)" src/BaseApi.Service/Persistence/Configurations/ProcessorEntityConfiguration.cs` returns 1
- VERIFIED ABSENT: `git diff src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` returns empty (zero-touch invariant confirmed — fk_processor_config_schema_id parses under the Option A regex without source change)
- VERIFIED ABSENT: `git diff src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs` returns empty (Mapperly symmetric-addition invariant confirmed)
- VERIFIED: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Release --no-restore` exits 0 with 0 warnings (1.55s)
- VERIFIED: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Debug --no-restore` exits 0 with 0 warnings (1.41s)
- VERIFIED: `dotnet build src/BaseApi.Core/BaseApi.Core.csproj -c Release --no-restore` exits 0 with 0 warnings (0.60s)
- VERIFIED: `git status --porcelain` returns no `M` entries for the 4 Processor files (working tree clean for tracked plan scope; pre-existing untracked .planning artifacts unrelated to this plan)
- VERIFIED: `git diff --diff-filter=D --name-only HEAD~1 HEAD` returns empty (no accidental file deletions)
- VERIFIED: `git show --stat HEAD | tail` shows exactly 4 files changed (the 4 in files_modified)

---
*Phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o*
*Completed: 2026-05-28*
