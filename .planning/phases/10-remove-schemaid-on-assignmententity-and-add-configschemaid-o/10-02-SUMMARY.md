---
phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
plan: 02
subsystem: api

tags: [assignment-entity, schemaid-removal, refactor, ef-core, fluentvalidation, mapperly-symmetric, phase-10]

# Dependency graph
requires:
  - phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
    provides: "Plan 10-01 amended REQUIREMENTS.md ENTITY-07 + VALID-15 to describe post-Phase-10 Assignment shape (StepId + Payload only); this plan codifies the entity/DTO/validator/EF-config layer to match the amended spec."
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: "Phase 8 established AssignmentEntity v1 shape (StepId + SchemaId + Payload), AssignmentDtos 6-positional records, AssignmentDtoValidator VALID-15 + VALID-16 rules, AssignmentEntityConfiguration dual-FK + Restrict cascade — this plan reverses the SchemaId half of those Phase 8 additions."
provides:
  - "AssignmentEntity reduced to (StepId, Payload) — single non-nullable FK to StepEntity; XML doc reflects single-FK shape; VALID-21 deferral rephrased to acknowledge Phase 10 makes the validation structurally impossible at this layer."
  - "AssignmentCreateDto / AssignmentUpdateDto — 5 positional params each (Name, Version, Description, StepId, Payload); arity doc-comments updated 6 → 5."
  - "AssignmentReadDto — 10 positional params (Id, Name, Version, Description, StepId, Payload, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy); arity doc-comment updated 11 → 10."
  - "AssignmentCreateDtoValidator + AssignmentUpdateDtoValidator — each contain only StepId NotEqual(Guid.Empty) + Payload NotEmpty/MaxLength/JSON-parse rules (plus inherited BaseDtoValidator). The VALID-15 SchemaId rule block is gone from both."
  - "AssignmentEntityConfiguration — single HasOne<StepEntity>() FK block (was dual-FK with SchemaEntity); unused 'using BaseApi.Service.Features.Schema;' import removed (TreatWarningsAsErrors=true / EnforceCodeStyleInBuild=true guard); XML doc updated to single-FK shape."
  - "Commit #2 of the 5-commit Phase 10 sequence per CONTEXT D-02 — bisect-friendly atomic refactor; production projects (BaseApi.Core + BaseApi.Service) build zero-warning Release + Debug."
affects:
  - 10-03-add-processor-configschemaid (independent — different feature folder; can ship in either order within Wave 2)
  - 10-04-migration-regenerate-initialcreate (Plan 04 reads the post-Phase-10 model — including this plan's AssignmentEntity shape — to regenerate the migration; the new InitialCreate will have NO 'assignments.schema_id' column / fk_assignment_schema_id / ix_assignments_schema_id)
  - 10-05-test-fixture-updates-and-configschemaid-facts (Plan 05 updates AssignmentsIntegrationTests.cs to drop SchemaId from NewValidCreateDto + Update DTO + assertions + CreatePrereqAsync; the test project is intentionally RED between commit #2 and commit #5 per D-02 bisect-friendliness contract)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Symmetric field-shape removal — entity AND all 3 DTOs lose SchemaId together → Mapperly RMG012/RMG020 do not fire on AssignmentEntityMapper.cs (zero-touch confirmation per CONTEXT D-10 invariant)."
    - "EF-config unused-import cleanup under TreatWarningsAsErrors=true — when removing an FK block, audit the file's using directives and drop any that are now orphan (would otherwise trip CS8019 / IDE0005 as build-fatal warnings per Directory.Build.props EnforceCodeStyleInBuild=true)."
    - "Doc-comment rephrase to avoid literal removed-symbol token while preserving deferral semantics — when plan-suggested wording trips a plan-level grep-empty assertion, rephrase to convey the same intent without naming the removed token literally (Plan 06-01 MP-code rephrase + Plan 08-01 'EnsureCreatedAsync' rephrase precedents apply)."
    - "Bisect-friendly per-commit build gate — production-source builds must stay green at every commit even when test code is intentionally RED until a later commit lands the test edits (D-02 contract — production projects exclusively, not solution-level)."

key-files:
  created: []
  modified:
    - "src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs"
    - "src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs"
    - "src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs"
    - "src/BaseApi.Service/Persistence/Configurations/AssignmentEntityConfiguration.cs"

key-decisions:
  - "Followed CONTEXT D-02 verbatim commit subject: 'refactor(asm): remove SchemaId from AssignmentEntity (entity + DTOs + validator + EF config)' — plan-locked phrasing supports cross-session grep-based audit."
  - "Per CONTEXT D-02 bisect-friendliness, ran the build gate on the 2 PRODUCTION projects only (BaseApi.Core + BaseApi.Service, Release + Debug) rather than the full SK_P.sln — test project intentionally fails to compile between commit #2 and commit #5 since AssignmentsIntegrationTests.cs still references the removed AssignmentEntity.SchemaId / 6-positional DTOs. Plan 05 closes the test edits."
  - "Rule 1 fix-forward on VALID-21 deferral note: plan's verbatim suggested wording contained literal 'Assignment.SchemaId' and 'Processor.ConfigSchemaId' tokens, but the plan-level verify at SPEC line 299 requires `grep -rn 'SchemaId' src/BaseApi.Service/Features/Assignment/` to return zero matches. Rephrased deferral note in both AssignmentEntity.cs and AssignmentDtoValidator.cs to convey the same intent ('Phase 10 removed the direct schema reference from Assignment; future work would need a processor-side schema reference') without the literal token. Plan 06-01 / 08-01 educational-rephrase precedent applies."
  - "Mapperly drift probe zero-touch confirmed (CONTEXT D-10) — AssignmentEntityMapper.cs was NOT modified; symmetric SchemaId removal across entity + 3 DTOs means RMG012 (target unmapped) + RMG020 (source unmapped) do not fire. Production projects Release + Debug build with 0 warnings + 0 errors verifies."

patterns-established:
  - "Symmetric field-shape removal across entity + DTOs + validator + EF config in a single atomic commit — preserves Mapperly diagnostics quiet (RMG012/RMG020 do not fire on the symmetric removal axis) and keeps each commit independently revertable (D-02 bisect-friendliness)."
  - "Production-build gate vs solution-build gate: when a multi-commit refactor knowingly breaks the test project between intermediate commits, the build gate runs on production projects (BaseApi.Core + BaseApi.Service) explicitly rather than `dotnet build SK_P.sln` — preserves the per-commit green-build invariant for the production source set without forcing all test edits into a single mega-commit (D-02)."
  - "Unused-import cleanup follows FK removal — when an EF entity configuration loses an FK block to a feature folder, audit the file's using directives and drop the now-orphan import. Required under TreatWarningsAsErrors=true + EnforceCodeStyleInBuild=true global gates (Phase 1 D-02)."

requirements-completed: [ENTITY-07, VALID-15]

# Metrics
duration: ~4min
completed: 2026-05-28
---

# Phase 10 Plan 02: Remove SchemaId on AssignmentEntity Summary

**Atomic 4-file refactor commit removing SchemaId from AssignmentEntity (entity + 3 DTOs + 2 validators + EF config) — Assignment surface now (StepId, Payload)-only; production projects build zero-warning Release + Debug; test project intentionally RED until commit #5 per D-02 bisect-friendliness.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-05-28T07:28:01Z
- **Completed:** 2026-05-28T07:31:22Z
- **Tasks:** 5 (Task 1 AssignmentEntity, Task 2 AssignmentDtos, Task 3 AssignmentDtoValidator, Task 4 AssignmentEntityConfiguration + build verify, Task 5 atomic commit)
- **Files modified:** 4 (per files_modified frontmatter — exactly matches plan scope)

## Accomplishments

- AssignmentEntity reduced from (StepId + SchemaId + Payload) to (StepId + Payload); XML doc updated to single-FK shape; VALID-21 deferral note preserved in rephrased form to satisfy plan-level grep-empty assertion.
- AssignmentDtos: all 3 records lose SchemaId (Create/Update 6 → 5 positional; Read 11 → 10 positional); arity doc-comments updated.
- AssignmentDtoValidator: both Create and Update validators lose the VALID-15 SchemaId rule block; XML doc bullet simplified; VALID-21 deferral note rephrased.
- AssignmentEntityConfiguration: HasOne<SchemaEntity>() FK block removed; orphan `using BaseApi.Service.Features.Schema;` import removed; XML doc updated to single-FK shape.
- Single atomic commit `79b07d1` landed with verbatim D-02 subject; production projects Release + Debug build clean with 0 warnings.

## Task Commits

Single atomic commit per D-02 (commit #2 of the Phase 10 sequence):

1. **Task 1 + Task 2 + Task 3 + Task 4 + Task 5 (combined atomic refactor):** `79b07d1` — `refactor(asm): remove SchemaId from AssignmentEntity (entity + DTOs + validator + EF config)`

Per CONTEXT D-02, all 5 plan tasks collapse to a single git commit (the production refactor commit #2 of the Phase 10 sequence). Tasks 1-4 are code edits across 4 files; Task 5 is the commit gate. There is no per-task atomicity because the 4 files must land together — splitting Tasks 1-4 across separate commits would leave intermediate commits in a build-fail state (e.g., DTOs still carry SchemaId while entity has dropped it → Mapperly RMG020 fires on ToRead → CS-class compile error).

**Plan metadata:** This SUMMARY.md + updated STATE.md + updated ROADMAP.md will be added as a separate final docs commit per the executor's final_commit step.

## Files Created/Modified

- `src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs` — SchemaId property removed; XML doc reflects single-FK shape (StepId only); VALID-21 deferral note rephrased.
- `src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs` — Create/Update records 6 → 5 positional params; Read record 11 → 10 positional params; arity doc-comments updated.
- `src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs` — VALID-15 SchemaId rule blocks removed from both Create and Update validators; XML doc bullet simplified; VALID-21 deferral note rephrased.
- `src/BaseApi.Service/Persistence/Configurations/AssignmentEntityConfiguration.cs` — HasOne<SchemaEntity>() FK block + 'using BaseApi.Service.Features.Schema;' import removed; XML doc updated to single-FK shape.

## Decisions Made

None new — followed plan and CONTEXT D-02 / D-10 exactly. The decisions captured here are pre-existing CONTEXT decisions whose application is documented above (D-02 atomic-refactor commit + verbatim subject + production-build-gate scope; D-10 symmetric-removal → Mapperly zero-touch).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Plan Internal Inconsistency] Rephrased VALID-21 deferral note to avoid literal `SchemaId` token**
- **Found during:** Task 1 (AssignmentEntity.cs) verification, surfaced again in Task 3 (AssignmentDtoValidator.cs).
- **Issue:** The plan's Task 1 + Task 3 "suggested wording" for the VALID-21 deferral note contained literal tokens `Assignment.SchemaId` and `Processor.ConfigSchemaId`. However, the plan-level verification at SPEC.md line 299 requires `grep -rn "SchemaId" src/BaseApi.Service/Features/Assignment/` to return zero matches across the entire Assignment feature folder. The two assertions conflict — the verbatim suggested wording would fail the negation grep.
- **Fix:** Rephrased the deferral note in both files to convey the same semantic intent without the literal token. New wording: "Schema-conformance validation (VALID-21) is now structurally impossible at this layer — Phase 10 removed the direct schema reference from Assignment. Any future Payload-vs-schema validation would need a new design (e.g., a processor-side schema reference). Deferred to v2." Preserves the VALID-21 marker, the deferral semantics, the cross-reference to a future processor-side schema design, AND the grep-empty invariant.
- **Files modified:** src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs, src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs
- **Verification:** `grep -rn "SchemaId" src/BaseApi.Service/Features/Assignment/` returns zero matches; `grep "VALID-21" src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs` still returns 1 match (deferral marker preserved); production projects Release + Debug build with 0 warnings.
- **Committed in:** `79b07d1` (part of the atomic Plan 10-02 commit — no separate fix(10-02) commit because the deviation surfaced before the commit was created)
- **Precedent:** Plan 06-01 MP-code rephrase (Directory.Build.props comment) + Plan 08-01 'EnsureCreatedAsync' → 'pre-create schema / model-shortcut create-from-model API' rephrase. Educational rephrase pattern is established for plan-internal-inconsistency between suggested wording and plan-level grep assertions.

---

**Total deviations:** 1 auto-fixed (1 plan internal inconsistency — Rule 1)
**Impact on plan:** Zero scope creep; zero unplanned work; zero auth gates. The rephrase preserves all plan-level invariants (VALID-21 marker + deferral semantics + grep-empty assertion) simultaneously. No file count change (still 4 modified). No commit-message change (still verbatim D-02 subject).

## Issues Encountered

None beyond the deviation documented above.

## User Setup Required

None — internal refactor commit; no external configuration touched.

## Next Phase Readiness

**Plan 10-03 (next):** Ready to execute. Independent of this plan at the file-edit layer (different feature folder — Processor vs Assignment). Plan 10-03 will add `ProcessorEntity.ConfigSchemaId` mirroring InputSchemaId behavior exactly. The amended ENTITY-04 + VALID-11 (Plan 10-01) are the locked spec target. After Plan 10-03 lands, both halves of the Phase 10 field-shape revision will be in place at the source-code layer.

**Plan 10-04:** Depends on 10-02 + 10-03 (needs both models in their final shape before `dotnet ef migrations add InitialCreate` regenerates the migration in place). Migration files in `src/BaseApi.Service/Persistence/Migrations/` (currently 3 files for the v1 `20260527203118_InitialCreate`) WILL still reference `schema_id` / `fk_assignment_schema_id` / `ix_assignments_schema_id` until Plan 10-04 regenerates — expected per the plan's `assignments` block invariant.

**Plan 10-05:** Depends on 10-02 + 10-03 + 10-04. Currently `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` references `AssignmentEntity.SchemaId` / `AssignmentCreateDto(...).SchemaId` / etc. — the test project WILL fail to build between this commit and commit #5. That is the documented D-02 bisect-friendliness contract: production source builds clean at every commit; test source bundles its edits into commit #5 to keep the cleanup churn out of the surface-change commits.

**Forensic property preserved:** If execution stops at this commit, the production Assignment surface is internally consistent — entity + DTOs + validator + EF config all describe the same `(StepId, Payload)` shape. REQUIREMENTS.md (amended in Plan 10-01) already describes this shape. The next session can resume from a self-consistent state. The migration + test files lag by design until Plans 10-04 + 10-05 land.

## Self-Check: PASSED

- FOUND: `src/BaseApi.Service/Features/Assignment/AssignmentEntity.cs` (modified — verified by `git show --stat HEAD` showing the file in the 4-file commit)
- FOUND: `src/BaseApi.Service/Features/Assignment/AssignmentDtos.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `src/BaseApi.Service/Persistence/Configurations/AssignmentEntityConfiguration.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: Commit `79b07d1` in git log (`git log -1 --format=%s` returns `refactor(asm): remove SchemaId from AssignmentEntity (entity + DTOs + validator + EF config)`)
- VERIFIED ABSENT: `grep -rn "SchemaId" src/BaseApi.Service/Features/Assignment/` returns zero matches (entity + DTOs + validator + configuration all clean)
- VERIFIED ABSENT: `grep "fk_assignment_schema_id"` returns zero matches in the Assignment feature folder + EF configuration directory (migration files still carry it — expected, Plan 10-04 regenerates)
- VERIFIED ABSENT: `grep "HasOne<SchemaEntity>"` returns zero matches in `AssignmentEntityConfiguration.cs`
- VERIFIED ABSENT: `grep "using BaseApi.Service.Features.Schema"` returns zero matches in `AssignmentEntityConfiguration.cs`
- VERIFIED PRESENT: `grep -c "HasConstraintName" AssignmentEntityConfiguration.cs` returns 1 (only `fk_assignment_step_id` remains)
- VERIFIED PRESENT: `grep "VALID-21" AssignmentEntity.cs` returns 1 (deferral marker preserved)
- VERIFIED: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Release --no-restore` exits 0 with 0 warnings (5.58s)
- VERIFIED: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Debug --no-restore` exits 0 with 0 warnings (1.97s)
- VERIFIED: `dotnet build src/BaseApi.Core/BaseApi.Core.csproj -c Release --no-restore` exits 0 with 0 warnings (0.65s)
- VERIFIED: `git status --porcelain` returns no `M` entries for the 4 Assignment files (working tree clean for tracked plan scope; pre-existing untracked .planning artifacts unrelated to this plan)
- VERIFIED: `git diff --diff-filter=D --name-only HEAD~1 HEAD` returns empty (no accidental file deletions)

---
*Phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o*
*Completed: 2026-05-28*
