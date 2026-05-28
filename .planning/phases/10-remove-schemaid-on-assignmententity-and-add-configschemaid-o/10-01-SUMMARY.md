---
phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
plan: 01
subsystem: docs

tags: [requirements, doc-first, in-place-amendment, entity-shape, validation-rules, phase-10]

# Dependency graph
requires:
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: "Locked v1 shape of ENTITY-04 (Processor.InputSchemaId? + OutputSchemaId?) and ENTITY-07 (Assignment.StepId + SchemaId + Payload); VALID-11 + VALID-15 rule patterns; constraint-name convention fk_<table>_<column> consumed by Phase 4 PostgresExceptionMapper Option A regex"
  - phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
    provides: "Last completed phase before Phase 10 (138 facts GREEN); ROADMAP coverage table baseline used to scope this commit"
provides:
  - "REQUIREMENTS.md ENTITY-04 amended in place: nullable Schema FK enumeration now lists 3 fields (InputSchemaId + OutputSchemaId + ConfigSchemaId) with explicit constraint names fk_processor_input_schema_id / fk_processor_output_schema_id / fk_processor_config_schema_id"
  - "REQUIREMENTS.md ENTITY-07 amended in place: AssignmentEntity property enumeration reduced to StepId + Payload (SchemaId dropped)"
  - "REQUIREMENTS.md VALID-11 amended in place: When().HasValue → NotEqual(Guid.Empty) rule list now covers InputSchemaId + OutputSchemaId + ConfigSchemaId (trinary semantics — source/sink/unconfigured)"
  - "REQUIREMENTS.md VALID-15 amended in place: AssignmentCreate/UpdateDto rule reduced to StepId only (SchemaId rule dropped)"
  - "REQUIREMENTS.md footer updated to 2026-05-28 — Phase 10 amendments for Assignment.SchemaId removal + Processor.ConfigSchemaId addition"
  - "Locked spec target for Plans 10-02 (Assignment SchemaId code removal), 10-03 (Processor ConfigSchemaId code addition), 10-04 (migration regeneration), 10-05 (test fixture updates + 2 new ConfigSchemaId facts)"
affects:
  - 10-02-remove-assignment-schemaid (code commit #2 — entity + DTOs + validator + EF config; reads amended ENTITY-07 + VALID-15)
  - 10-03-add-processor-configschemaid (code commit #3 — entity + DTOs + validator + EF config; reads amended ENTITY-04 + VALID-11)
  - 10-04-migration-regenerate-initialcreate (commit #4 — EF migration; constraint names locked by ENTITY-04 amendment)
  - 10-05-test-fixture-updates-and-configschemaid-facts (commit #5 — 8 ProcessorCreateDto sites + AssignmentsIntegrationTests + 2 new facts)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Doc-first amendment ordering — REQUIREMENTS.md commit #1 BEFORE code/EF/migration/test commits (D-01; mirrors Phase 3 D-03b PERSIST-16 introduction precedent verbatim)"
    - "ID preservation invariant — REQ-IDs ENTITY-04, ENTITY-07, VALID-11, VALID-15 edited in place; not renumbered, not superseded (D-01 forensic property — every commit between #1 and #5 independently revertable)"
    - "Byte-preserved REQ-ID headers — the `- [x] **{ID}` + newline + `**:` split-bold marker pattern preserved across edits so grep regexes that anchor on REQ-ID continue to match"

key-files:
  created: []
  modified:
    - ".planning/REQUIREMENTS.md"

key-decisions:
  - "D-01 (CONTEXT) — Doc-first ordering: REQUIREMENTS.md amendments land in commit #1 as standalone docs(req) commit BEFORE any code/EF/migration/test commit, so that if execution stops at any later commit (#2..#5), the spec still describes the target Phase-10 shape (not v1's old shape). Forensic property: every commit between #1 and #5 is independently revertable."
  - "D-01 (CONTEXT) — REQ-ID preservation: ENTITY-04/07 + VALID-11/15 IDs are NOT renumbered or superseded — same IDs, edited in place. No new REQ-IDs introduced in Phase 10."
  - "D-02 (CONTEXT) — Commit subject verbatim: `docs(req): amend ENTITY-04/07 + VALID-11/15 for Phase 10 shape`. Plan-locked phrasing to support cross-session grep-based audit."
  - "PATTERNS — Trinary wording locked: `source processors (no input), sink processors (no output), and unconfigured processors (no config)` in ENTITY-04 (extends the existing binary source/sink semantics with the new unconfigured semantics)."
  - "PATTERNS — Constraint names enumerated in ENTITY-04: all three FK constraint names (`fk_processor_input_schema_id`, `fk_processor_output_schema_id`, `fk_processor_config_schema_id`) listed inline so a reader can trace REQ-5 → Phase 4 PostgresExceptionMapper Option A regex without re-deriving the invariant."

patterns-established:
  - "In-place REQ-ID amendment: when a phase changes the shape of an existing requirement, edit the REQ-ID body in place rather than introducing a new REQ-ID. Preserves traceability tables and cross-phase references."
  - "Doc-first commit when shape changes span multiple commits: the spec commit lands FIRST so that intermediate commits can reference the new shape as authoritative."

requirements-completed: [ENTITY-04, ENTITY-07, VALID-11, VALID-15]

# Metrics
duration: ~5min
completed: 2026-05-28
---

# Phase 10 Plan 01: REQUIREMENTS.md doc-first amendments Summary

**Doc-first commit #1 of Phase 10 — REQUIREMENTS.md ENTITY-04/07 + VALID-11/15 amended in place to describe the post-Phase-10 shape (Processor adds ConfigSchemaId nullable FK; Assignment drops SchemaId) before any code/EF/migration/test commit lands.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-05-28T07:18Z (approximate — execution session start)
- **Completed:** 2026-05-28T07:24Z
- **Tasks:** 4 (Task 1 ENTITY-04, Task 2 ENTITY-07 + VALID-15, Task 3 VALID-11 + footer, Task 4 commit)
- **Files modified:** 1 (`.planning/REQUIREMENTS.md`)

## Accomplishments

- ENTITY-04 now describes ProcessorEntity with 3 nullable Schema FK fields (Input/Output/Config) with all 3 constraint names enumerated inline; trinary "source/sink/unconfigured" wording locked.
- ENTITY-07 now describes AssignmentEntity with only StepId + Payload (SchemaId removed); v1 v1.x boundary established for assignments shape.
- VALID-11 now enumerates the When().HasValue → NotEqual(Guid.Empty) rule for 3 nullable Schema FK fields (Input/Output/Config); preserves the ERROR-04 cross-reference (SQLSTATE 23503 → HTTP 422) and the `Guid.Empty` HTTP-400-not-DB rejection note.
- VALID-15 reduced to AssignmentCreate/UpdateDto.StepId only; SchemaId rule cleanly dropped.
- Footer carries the Phase 10 amendment note (2026-05-28).
- All 4 REQ-ID headers preserved byte-identical (no renumbering, no superseding).

## Task Commits

Single atomic commit per D-01 (doc-only commit #1 of Phase 10 sequence):

1. **Task 1 + Task 2 + Task 3 + Task 4 (combined doc commit):** `1de7e71` — `docs(req): amend ENTITY-04/07 + VALID-11/15 for Phase 10 shape`

Per CONTEXT D-01/D-02, all 4 plan tasks collapse to a single git commit (the doc-only commit #1 of the Phase 10 sequence). Tasks 1-3 are doc edits within the same file; Task 4 is the commit gate. There is no per-task atomicity because the spec must land as a single coherent unit (partial REQ-ID amendments would leave REQUIREMENTS.md in an internally inconsistent state).

**Plan metadata:** This SUMMARY.md + updated STATE.md + updated ROADMAP.md will be added as a separate final docs commit per the executor's final_commit step.

## Files Created/Modified

- `.planning/REQUIREMENTS.md` — 4 REQ-IDs amended in place (ENTITY-04, ENTITY-07, VALID-11, VALID-15) + footer date/note updated. 5 insertions / 5 deletions (in-place line replacements; no file structure changes).

## Decisions Made

None new — followed plan and CONTEXT D-01/D-02 exactly. The decisions captured here are pre-existing CONTEXT decisions whose application is documented above (D-01 doc-first ordering, D-01 ID preservation, D-02 verbatim commit subject).

## Deviations from Plan

None - plan executed exactly as written.

The plan's verbatim PATTERNS.md target text (lines 567-577 ENTITY-04 / 579-583 ENTITY-07 / 587-593 VALID-11 / 595-598 VALID-15 / 601-603 footer) was applied verbatim. All 4 plan-level verifications + the 5 success criteria passed on first attempt. No Rules 1-4 triggered.

---

**Total deviations:** 0
**Impact on plan:** Zero scope creep; zero unplanned work; zero auth gates.

## Issues Encountered

None.

## User Setup Required

None — doc-only commit; no external configuration touched.

## Next Phase Readiness

**Plan 10-02 (next):** Ready to execute. The amended ENTITY-07 + VALID-15 are now the locked spec target. Plan 10-02 will remove `AssignmentEntity.SchemaId` from `src/BaseApi.Service/Features/Assignment/` (entity + DTOs + validator + EF config) and the resulting code will be verifiable against the amended REQ-IDs.

**Plan 10-03:** Ready to execute (no dependency on 10-02 at the doc layer; can be parallelized at code level once the spec is locked). The amended ENTITY-04 + VALID-11 are now the locked spec target. 10-03 will add `ProcessorEntity.ConfigSchemaId` mirroring InputSchemaId behavior exactly.

**Plan 10-04:** Depends on 10-02 + 10-03 (needs both models in their final shape before `dotnet ef migrations add InitialCreate` regenerates the migration in place).

**Plan 10-05:** Depends on 10-02 + 10-03 + 10-04 (test fixture updates need the new DTO shapes; new ConfigSchemaId facts need the new column wired through).

**Forensic property preserved:** If execution stops at any commit #2..#5, REQUIREMENTS.md still describes the post-Phase-10 shape — the next session can resume from a self-consistent spec without re-deriving the target.

## Self-Check: PASSED

- FOUND: `.planning/REQUIREMENTS.md` (modified in place; verified by `git show --stat HEAD` showing exactly 1 file changed)
- FOUND: Commit `1de7e71` in git log (`git log --oneline -1` returns `1de7e71 docs(req): amend ENTITY-04/07 + VALID-11/15 for Phase 10 shape`)
- FOUND: `ConfigSchemaId` appears 3 times in REQUIREMENTS.md (ENTITY-04 once + VALID-11 twice)
- FOUND: `fk_processor_config_schema_id` appears exactly once (ENTITY-04 enumeration)
- FOUND: footer line `2026-05-28 — Phase 10 amendments for Assignment.SchemaId removal + Processor.ConfigSchemaId addition`
- VERIFIED ABSENT: `AssignmentEntity.*adds.*SchemaId` (ENTITY-07 cleaned)
- VERIFIED ABSENT: `AssignmentCreate/UpdateDto.StepId.*SchemaId` (VALID-15 cleaned)
- VERIFIED: All 4 REQ-ID headers present (`^\- \[x\] \*\*(ENTITY-04|ENTITY-07|VALID-11|VALID-15)` returns 4 matches)

---
*Phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o*
*Completed: 2026-05-28*
