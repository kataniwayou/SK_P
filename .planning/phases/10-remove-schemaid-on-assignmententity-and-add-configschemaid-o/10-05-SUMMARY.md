---
phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
plan: 05
subsystem: tests

tags: [test-updates, processorcreate-dto-arity, configschemaid-roundtrip, assignmentintegrationtests-simplification, phase-10, 3-consecutive-green, psql-byte-identical, otel-collector-restart-fix]

# Dependency graph
requires:
  - phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
    provides: "Plan 10-01 doc commit + Plans 10-02/10-03 entity+DTO+validator+EF-config commits + Plan 10-04 regenerated InitialCreate migration. This plan reads the post-Phase-10 DTO arity contract (ProcessorCreate/UpdateDto = 7 positional, AssignmentCreate/UpdateDto = 5 positional) from the wave-2+3+4 commits and brings the test project from intentionally-RED (since commit #2) to GREEN."
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: "Phase8WebAppFactory (per-class throwaway DB via PostgresFixture) + Phase 4 PostgresExceptionMapper + 25 existing Wave B smoke facts + the AssignmentsIntegrationTests CreatePrereqAsync FK-chain helper pattern. This plan reuses Phase8WebAppFactory for the 2 new ConfigSchemaId round-trip facts (CONTEXT D-06 — no Phase10WebAppFactory)."
  - phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
    provides: "tests/BaseApi.Tests/Features/ folder layout (CONTEXT D-19 — sibling to Integration/); 3 Phase 9 test files that also construct ProcessorCreateDto (GetBySourceHashFacts + Start/Stop OrchestrationFacts). This plan updates these Phase-9 helper sites with the same ConfigSchemaId: null positional addition."
provides:
  - "All 10 ProcessorCreateDto + ProcessorUpdateDto construction sites in tests/ carry ConfigSchemaId: null (or schema!.Id, for the new non-null fact) as the 7th positional argument — Plan 03 arity contract satisfied"
  - "tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs is structurally aligned with Plan 02's post-removal AssignmentEntity shape: CreatePrereqAsync signature Task<Guid> (StepId only); no Schema POST; NewValidCreateDto signature (Guid stepId, string suffix = \"\"); AssignmentCreateDto body uses 5 positional params; 4 destructuring sites + 4 invocation sites + 1 round-trip assertion + 1 Update DTO body + 1 unused-import line removed"
  - "tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs has 2 new [Fact] methods: Create_ProcessorWithConfigSchemaId_RoundTripsCorrectly (POSTs Schema inline, POSTs Processor with ConfigSchemaId, GET asserts round-trip equality) AND Create_ProcessorWithNullConfigSchemaId_RoundTripsAsNull (POSTs Processor with ConfigSchemaId: null, GET asserts read.ConfigSchemaId is null)"
  - "Suite count after Plan 05: 140 -> 142 facts (previous suite was 140, not 138 as SPEC originally projected; the actual baseline carry-forward was 140 — independent of the 2-fact addition driven by SPEC REQ-7)"
  - "dotnet test SK_P.sln --no-restore -c Release reports 142/142 passing across 3 CONSECUTIVE runs (Run 1: 29.281s; Run 2: 28.979s; Run 3: 28.869s — well within the Phase 8 P08 + Phase 9 P09-03 cadence range)"
  - "Byte-identical psql -l SHA-256 snapshot BEFORE and AFTER the 3-run cycle (sha256 0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127 — 4 baseline DBs: postgres + template0 + template1 + stepsdb; zero leaked stepsdb_test_* databases)"
  - "dotnet build SK_P.sln (whole solution including test project) exits 0 with zero warnings in both Release and Debug — TreatWarningsAsErrors=true + EnforceCodeStyleInBuild=true gate held"
  - "Commit #5 of the 5-commit Phase 10 sequence per CONTEXT D-02 — verbatim subject (adjusted from SPEC's '8 sites' to live-audited '10 sites'): 'test: update 10 ProcessorCreateDto/UpdateDto sites + AssignmentsIntegrationTests + 2 new ConfigSchemaId facts'. Bisect-friendly atomic test-cleanup commit; closes Phase 10."
  - "Phase 10 COMPLETE — 5-commit sequence intact (1de7e71 + 79b07d1 + 12577ac + 146d482 + 6d043e4) and visible in git log --oneline -5; system is shippable with the post-Phase-10 model end-to-end (entity + DTOs + validators + EF config + REQUIREMENTS.md + migration + test suite all consistent)"
affects:
  - "ROADMAP.md Phase 10 row flips to COMPLETE (5/5 plans); Phase 10 success criteria all behaviorally verified end-to-end via 142 facts; phase REQ-IDs TEST-05/TEST-06/VALID-11 closed for Phase 10 amendment"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Mechanical positional-record arity update — when a feature plan adds a new field as the Nth positional to a C# positional record, EVERY construction site in the test tree compile-fails until the new positional value is supplied. The fix-pattern is a copy/paste-style edit (literally identical 'ConfigSchemaId: null' insertion immediately after the 6th positional) across N call sites. Use grep -rn 'new <DtoName>(' tests/ as the canonical enumeration; helper sites using target-typed '=> new()' require a separate grep on the helper return type signature. SPEC's count may be off (here: SPEC claimed 8; live audit found 10 = 7 explicit + 2 helper + 1 update site). Always re-audit at execution time."
    - "AssignmentsIntegrationTests CreatePrereqAsync simplification pattern — when an FK target is removed from a leaf entity (here: Assignment.SchemaId), the corresponding HTTP-API FK-chain helper collapses by exactly one prerequisite POST. Return type changes from Task<(Guid, Guid)> to Task<Guid>; all destructuring call sites update from 'var (a, b) = ...' to 'var a = ...'; helper invocations drop the dropped-FK argument; round-trip assertions on the dropped field are deleted; Update DTO bodies drop the dropped-FK named arg; the using import for the prerequisite namespace becomes unused and must be removed (TreatWarningsAsErrors=true)."
    - "OTel collector restart guard for the 3-consecutive-GREEN test cycle — when a prior plan tears down docker compose (docker compose down -v) and only restarts a SUBSET of services for its own gates (here: Plan 10-04 restarted only postgres for the migration regen), the Phase 5 Observability test family (LogLevelFilterTests, MetricsExportTests, TraceExportTests, LogExportTests) fails with 7 errors due to the otel-collector container being absent. The fingerprint is identical to the documented Phase 8 P08 'OTel cold-start' flake (4 categories x ~1-2 facts each = 7 total) — but is structurally NOT a flake when the collector is offline; it's a deterministic environment gap. Fix: docker compose up -d otel-collector before starting the 3-run cycle. Distinguishing flake-vs-environment by checking docker compose ps for the otel-collector service is a Rule 3 fix-forward pattern future migration-regen-followed-by-test plans should bake in."

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs (added: using BaseApi.Service.Features.Schema; updated helper NewValidCreateDto + Update DTO body to add ConfigSchemaId: null; appended 2 new [Fact] methods)"
    - "tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs (8 coordinated edits: signature change Task<Guid>; Schema POST removed; renumbered comments; ConfigSchemaId: null added to internal ProcessorCreateDto; NewValidCreateDto signature drops schemaId; 4 destructuring sites + 4 invocation sites + 1 round-trip assertion + 1 Update DTO body line removed; unused Schema using removed; XML doc-comment updated to describe Processor->Step chain only)"
    - "tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs (2 mechanical ConfigSchemaId: null additions — dup-source-hash fact + sc5-fk-restrict fact)"
    - "tests/BaseApi.Tests/Integration/WorkflowsIntegrationTests.cs (1 mechanical ConfigSchemaId: null addition — wf-proc helper)"
    - "tests/BaseApi.Tests/Integration/StepsIntegrationTests.cs (1 mechanical ConfigSchemaId: null addition — step-proc helper)"
    - "tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs (1 mechanical ConfigSchemaId: null addition — orch-proc helper)"
    - "tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs (1 mechanical ConfigSchemaId: null addition — orch-stop-proc helper)"
    - "tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs (1 mechanical ConfigSchemaId: null addition — NewValidCreateDto target-typed helper)"
  deleted: []

key-decisions:
  - "Followed CONTEXT D-02 verbatim commit subject pattern adjusted for the live audit count: 'test: update 10 ProcessorCreateDto/UpdateDto sites + AssignmentsIntegrationTests + 2 new ConfigSchemaId facts' — SPEC said 8 sites; planner counted 10; live audit confirmed 10. The discrepancy is documented in the commit body for forensic reviewability per the plan's Task 5 action."
  - "Followed CONTEXT D-04 verbatim — 2 new ConfigSchemaId facts authored as independent [Fact] methods (NOT [Theory]); each fact's body matches the verbatim skeleton from PATTERNS.md lines 378-438. Phase 9 D-19 features-folder pattern intentionally NOT applied (per CONTEXT D-04 — that pattern is for new endpoints; ConfigSchemaId is a field round-trip and lives at the same level as InputSchemaId/OutputSchemaId implicit round-trips in the existing file)."
  - "Followed CONTEXT D-05 verbatim — AssignmentsIntegrationTests.CreatePrereqAsync collapses Schema -> Processor -> Step into Processor -> Step (Schema POST removed; signature changes from Task<(Guid, Guid)> to Task<Guid>). All 4 call-site destructurings + 4 NewValidCreateDto invocations + 1 round-trip assertion + 1 Update DTO body line + 1 unused Schema using import + 1 XML doc-comment paragraph updated coordinated in a single mechanical pass."
  - "Followed CONTEXT D-06 verbatim — Phase8WebAppFactory reused for the 2 new facts; [Trait(\"Phase8Wave\", \"B\")] inherited from the existing class declaration; no Phase10WebAppFactory subclass introduced; no per-fact [Trait(\"Phase\", \"10\")] tag added (consistent with how Wave B smokes were tagged in Phase 8)."
  - "Rule 3 fix-forward — OTel collector restart after Plan 10-04 teardown: First two `dotnet test` attempts had identical 7 failures across the Phase 5 Observability test family (LogLevelFilterTests, MetricsExportTests x 2, TraceExportTests x 2, LogExportTests x 2). Investigation revealed the otel-collector container was absent (Plan 10-04's `docker compose down -v` step had torn it down; Plan 10-04 only restarted postgres for its migration-regen gate). The fingerprint matched the documented Phase 8 P08 'OTel cold-start' flake fingerprint (7 failures, same 4 test categories) but was structurally a deterministic environment gap (collector at localhost:4317 unreachable), NOT a flake. Fix-forward: `docker compose up -d otel-collector` brought the collector to Up and healthy; the SUBSEQUENT 3 `dotnet test` runs were each GREEN at 142/142 in well under 30s — the true 3-consecutive-GREEN cycle. Plan 10-04 SUMMARY did NOT document the collector teardown; this plan's deviation surfaces it for the next migration-regen plan to bake `docker compose up -d` for all services into the teardown sequence."
  - "Task 4 verify-gate cosmetic — the plan's `! grep \"SchemaId\" tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` (case-sensitive negation) is structurally impossible to satisfy because the ProcessorCreateDto positional names `InputSchemaId`, `OutputSchemaId`, `ConfigSchemaId` all contain the literal substring 'SchemaId'. The spirit of the gate is 'no AssignmentEntity.SchemaId references' — satisfied: zero `SchemaId: schemaId,` body lines, zero `read.SchemaId` assertions, zero `var (stepId, schemaId)` destructurings, zero `Guid schemaId` parameters. The XML doc-comment historical mention of 'no longer carries a SchemaId field' was preserved intentionally for forensic continuity (describes the Phase 10 transition). Plan-internal gate inconsistency noted but no remediation required."
  - "Task 4 step-5 telemetry.jsonl cleanup informational only — `tests/.otel-out/telemetry.jsonl` is present post-suite (76KB). This is the documented Phase 5 D-11 race window behavior (OtelEndOfSuiteCleanup assembly fixture is best-effort; the collector may regenerate the file from ambient host traffic on localhost:4317 after the cleanup runs). NOT a Phase 10 regression; matches Phase 5 SUMMARY documented behavior. No action taken."

patterns-established:
  - "Test-suite-GREEN-after-symmetric-DTO-arity-change pattern — when a phase changes a positional-record DTO's arity (here: 6 -> 7 positional on ProcessorCreate+Update; 6 -> 5 positional on AssignmentCreate+Update), the final commit MUST update every construction site in tests/ with the new positional argument value (typically null for nullable fields) plus 2-3 new round-trip facts validating the new field's null + non-null paths end-to-end. Total sites is found via grep -rn 'new <DtoName>(' tests/ + grep -rn '<DtoName> <HelperName>' tests/ for target-typed `=> new()` helpers."
  - "Pre-3-consecutive-GREEN environment-readiness check pattern — before starting the 3-run cycle, explicitly verify docker compose ps shows ALL required services healthy/running (here: postgres AND otel-collector). A subset of services running produces test failures with fingerprints indistinguishable from documented pre-existing flakes, which can mask the environment gap and waste 3+ retry cycles. The Phase 5 Observability test family is the canary — 7 failures across 4 test categories (Log*, Metric*, Trace*) typically indicates otel-collector is offline."

requirements-completed: [TEST-05, TEST-06, VALID-11]

# Metrics
duration: ~28min
completed: 2026-05-28
---

# Phase 10 Plan 05: Test Fixture Updates and ConfigSchemaId Facts Summary

**Test-update commit #5 of Phase 10 — 10 ProcessorCreateDto/UpdateDto sites across 8 test files gained `ConfigSchemaId: null` as the 7th positional; AssignmentsIntegrationTests collapsed by 1 FK prerequisite (no Schema POST in CreatePrereqAsync; signature Task<Guid>); 2 new ConfigSchemaId round-trip facts appended to ProcessorsIntegrationTests; 3 CONSECUTIVE GREEN `dotnet test SK_P.sln --no-restore -c Release` runs at 142/142 each (~29s per run); byte-identical psql `\l` SHA-256 snapshot preserved (`0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`); zero leaked `stepsdb_test_*` databases; closes Phase 10.**

## Performance

- **Duration:** ~28 min (PLAN_START 2026-05-28T07:56Z to commit landed ~08:24Z)
- **Started:** 2026-05-28T07:56Z
- **Completed:** 2026-05-28T08:24Z
- **Tasks:** 5 (Task 0 audit, Task 1 mechanical 10-site update, Task 2 AssignmentsIntegrationTests simplification, Task 3 2 new facts, Task 4 build + 3-run GREEN + psql snapshot, Task 5 atomic commit)
- **Files modified:** 8 (5 in Integration/ + 3 in Features/ subfolders)
- **Test runs:** 5 total (Run 0a + Run 0b + 3 GREEN consecutive)
  - Run 0a: 135/142 Failed:7 (7m 34s) — OTel collector offline (environment gap, not flake)
  - Run 0b: 135/142 Failed:7 (6m 32s) — same 7 OTel failures, confirmed deterministic environment gap
  - Run 1 (consecutive cycle): 142/142 Passed (29.281s) — after `docker compose up -d otel-collector`
  - Run 2: 142/142 Passed (28.979s)
  - Run 3: 142/142 Passed (28.869s)

## Task 0 Audit Result

Live execution-time count (re-grepped at 2026-05-28T07:56Z):

- 7 explicit `new ProcessorCreateDto(` sites:
  - tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs:39
  - tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs:110
  - tests/BaseApi.Tests/Integration/WorkflowsIntegrationTests.cs:56
  - tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs:58
  - tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs:44
  - tests/BaseApi.Tests/Integration/StepsIntegrationTests.cs:47
  - tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs:56
- 2 helper sites with target-typed `=> new(` (matched via `ProcessorCreateDto NewValidCreateDto`):
  - tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs:39
  - tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs:46
- 1 explicit `new ProcessorUpdateDto(` site:
  - tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs:114
- **GRAND TOTAL: 10 construction sites**

| Source | Claimed Count | Actual |
|--------|---------------|--------|
| SPEC.md          | 8  | 10 |
| Planner (PLAN.md)| 10 | 10 |
| Live audit       | 10 | 10 |

Planner-time prediction (10) matches live count exactly. SPEC.md is off by 2: the planner-time grep already accounted for the ProcessorUpdateDto site + the target-typed `=> new()` helpers; the live re-grep confirmed no drift.

## 3-Run GREEN Cycle Per-Run Detail

All 3 runs report `Passed! - Failed: 0, Passed: 142, Skipped: 0, Total: 142`.

| Run | Duration | Result |
|-----|----------|--------|
| Run 1 (consecutive cycle) | 29s 281ms | 142/142 |
| Run 2 | 28s 979ms | 142/142 |
| Run 3 | 28s 869ms | 142/142 |

Run 0 (pre-environment-fix) had 7 OTel Observability failures over 2 attempts (7m 34s + 6m 32s) — diagnosed as `docker compose up -d otel-collector` missing (Plan 10-04's `docker compose down -v` had torn it down; Plan 10-04 only restarted postgres for the migration-regen D-08 gate). After the otel-collector restart, all 3 subsequent runs were GREEN with no warm-up cold-start fingerprint (~29s steady-state runtime).

## psql `\l` SHA-256 Snapshot

| Snapshot | Value |
|----------|-------|
| BEFORE (pre-Run-1) | `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127` |
| AFTER (post-Run-3) | `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127` |

**BYTE-IDENTICAL** — Phase 3 D-15 cleanup discipline preserved end-to-end through Phase 10. 4 baseline DBs (postgres + template0 + template1 + stepsdb); zero leaked `stepsdb_test_*` databases at any point during the 3-run cycle. Matches the Phase 8 P08 + Phase 9 P09-03 baseline hash verbatim — this hash has now been the deterministic 4-baseline-DB fingerprint across 3 phases of work.

## Accomplishments

- All 10 ProcessorCreateDto + ProcessorUpdateDto construction sites in the tests/ tree now carry `ConfigSchemaId: null` as the 7th positional (or `ConfigSchemaId: schema!.Id` for the new non-null round-trip fact). Sites enumerated via Task 0's grep + each file's edit performed by direct string replacement.
- `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` is now structurally aligned with Plan 02's post-removal AssignmentEntity shape:
  - `CreatePrereqAsync` signature: `Task<Guid>` (StepId only); Schema POST removed; comments renumbered 1./2.
  - `NewValidCreateDto` signature: `(Guid stepId, string suffix = "")`; body uses 5 positional params (no SchemaId)
  - 4 destructuring sites updated: `var (stepId, schemaId) = ...` -> `var stepId = ...`
  - 4 NewValidCreateDto invocations updated: dropped `schemaId` argument
  - 1 round-trip assertion deleted: `Assert.Equal(schemaId, read.SchemaId);`
  - 1 Update DTO body line deleted: `SchemaId: schemaId,`
  - 1 unused `using BaseApi.Service.Features.Schema;` removed
  - 1 XML doc-comment paragraph updated to describe Processor → Step chain only (Phase 10 transition mention preserved for forensic continuity)
- `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` has 2 new `[Fact]` methods appended after the last existing fact (Delete_Returns204_WhenExisting):
  - `Create_ProcessorWithConfigSchemaId_RoundTripsCorrectly` — POSTs SchemaCreateDto inline, then POSTs ProcessorCreateDto with `ConfigSchemaId: schema!.Id`, then GETs the Processor by Id and asserts `read.ConfigSchemaId == schema.Id`
  - `Create_ProcessorWithNullConfigSchemaId_RoundTripsAsNull` — POSTs ProcessorCreateDto with `ConfigSchemaId: null`, then GETs the Processor by Id and asserts `Assert.Null(read.ConfigSchemaId)`
  - Added `using BaseApi.Service.Features.Schema;` import (alphabetical position) — required for SchemaCreateDto + SchemaReadDto
  - Both new facts inherit class-level `[Trait("Phase8Wave", "B")]` and `IClassFixture<Phase8WebAppFactory>` per CONTEXT D-06
- Full-solution build gate PASSED — both Release and Debug exit 0 with 0 warnings + 0 errors under TreatWarningsAsErrors=true + EnforceCodeStyleInBuild=true.
- 3 consecutive `dotnet test SK_P.sln --no-restore -c Release` runs GREEN at 142/142 each (29.281s / 28.979s / 28.869s — averaging 29.043s).
- Byte-identical psql `\l` SHA-256 snapshot preserved across the 3-run cycle.
- Single atomic commit `6d043e4` landed with the adjusted-count verbatim subject; 8 files changed; 99 insertions + 43 deletions.
- The 5-commit Phase 10 sequence is now structurally intact in `git log --oneline -5`:
  - `6d043e4` — commit #5 (this plan)
  - `146d482` — commit #4 (Plan 10-04 migration regen)
  - `12577ac` — commit #3 (Plan 10-03 Processor ConfigSchemaId addition)
  - `79b07d1` — commit #2 (Plan 10-02 Assignment SchemaId removal)
  - `1de7e71` — commit #1 (Plan 10-01 doc-first REQUIREMENTS.md amendment)

## Task Commits

Single atomic commit per CONTEXT D-02 (commit #5 of the Phase 10 sequence):

1. **Task 1 + Task 2 + Task 3 + Task 4 + Task 5 (combined atomic test-cleanup commit):** `6d043e4` — `test: update 10 ProcessorCreateDto/UpdateDto sites + AssignmentsIntegrationTests + 2 new ConfigSchemaId facts`

Per CONTEXT D-02, all 5 plan tasks collapse to a single git commit. Task 0 is an audit (no file mutations). Tasks 1-3 are coordinated file edits that produce coherent test-project state. Task 4 is verification (no commits). Task 5 is the atomic commit landing the entire test-cleanup as commit #5 of the 5-commit Phase 10 sequence.

**Plan metadata:** This SUMMARY.md + updated STATE.md + updated ROADMAP.md + updated REQUIREMENTS.md will be added as a separate final docs commit per the executor's `final_commit` step.

## Files Created/Modified

- **MODIFIED:** `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` — added `using BaseApi.Service.Features.Schema;`; updated `NewValidCreateDto` helper with ConfigSchemaId: null; updated the Update DTO instance at line 114 with ConfigSchemaId: null; appended 2 new `[Fact]` methods (~38 lines added).
- **MODIFIED:** `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` — Schema POST block deleted from CreatePrereqAsync; CreatePrereqAsync signature simplified to Task<Guid>; ConfigSchemaId: null added to internal ProcessorCreateDto; NewValidCreateDto signature changes; 4 destructuring sites + 4 invocations + 1 round-trip assertion + 1 Update DTO body line + 1 unused import + 1 XML doc paragraph updated coordinated in a single mechanical pass.
- **MODIFIED:** `tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs` — 2 mechanical `ConfigSchemaId: null` additions (lines 45 + 116).
- **MODIFIED:** `tests/BaseApi.Tests/Integration/WorkflowsIntegrationTests.cs` — 1 mechanical `ConfigSchemaId: null` addition (line 62).
- **MODIFIED:** `tests/BaseApi.Tests/Integration/StepsIntegrationTests.cs` — 1 mechanical `ConfigSchemaId: null` addition (line 53).
- **MODIFIED:** `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` — 1 mechanical `ConfigSchemaId: null` addition (line 62).
- **MODIFIED:** `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs` — 1 mechanical `ConfigSchemaId: null` addition (line 50).
- **MODIFIED:** `tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs` — 1 mechanical `ConfigSchemaId: null` addition (line 52, target-typed helper).

## Decisions Made

None new — followed plan + CONTEXT D-02 / D-04 / D-05 / D-06 exactly. The decisions captured here are pre-existing CONTEXT decisions whose application is documented above. The 1 Rule 3 fix-forward (otel-collector restart) was a deviation framework auto-fix that surfaced a Plan-10-04 teardown gap; it did NOT require new CONTEXT decisions — `docker compose up -d otel-collector` is a standard environment-readiness operation.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Environment Gap from Prior Plan] OTel collector absent after Plan 10-04 teardown**
- **Found during:** Task 4 Step 3 (first 2 dotnet test attempts).
- **Issue:** The first 2 `dotnet test SK_P.sln --no-restore -c Release` runs reported `Failed: 7, Passed: 135, Skipped: 0, Total: 142` (run 1: 7m 34s; run 2: 6m 32s). The 7 failures were exclusively in `BaseApi.Tests.Observability.*`:
  - LogLevelFilterTests.Test_Information_Log_Present_When_Default_Information
  - MetricsExportTests.Test_HttpServerRequestDuration_Present_For_App_Endpoint
  - MetricsExportTests.Test_RuntimeMetric_ProcessRuntimeDotnet_Exported
  - TraceExportTests.Test_NpgsqlChildSpan_DbStatement_Has_NoParameterValues
  - TraceExportTests.Test_NpgsqlChildSpan_Under_AspNetCore_Request_Span
  - LogExportTests.Test_LogRecord_CorrelationId_Survives_Sanitization
  - LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource

  Fingerprint matches the Phase 8 P08 documented "OTel cold-start" flake (7 failures across the same 4 test categories) — but the failures were DETERMINISTIC across 2 attempts (~14 min total). Investigation via `docker compose ps` showed only `sk_p-postgres-1` was running; the `sk-otel-collector` container was absent. Plan 10-04's D-07 teardown sequence ran `docker compose down -v` (tearing down ALL services + the pgdata volume) but Plan 10-04 then only ran `docker compose up -d postgres` (the migration regen only needs postgres for the EF database drop / migration apply; the otel-collector wasn't needed for Plan 10-04's gates).
- **Fix:** Ran `docker compose up -d otel-collector`. Container went from absent → Up + healthy in ~4 seconds. Re-captured the BEFORE psql `\l` SHA-256 snapshot (still `0d98b0de...` — same 4 baseline DBs; otel-collector restart does not affect Postgres state). Started the 3-run consecutive cycle from scratch. All 3 subsequent runs reported `Passed! - Failed: 0, Passed: 142, Skipped: 0, Total: 142` in ~29s each.
- **Files affected:** None (environment-only fix; no source edits).
- **Verification:** `docker compose ps` post-fix shows both containers Up; the SUBSEQUENT 3 `dotnet test` runs were GREEN with no Observability failures. Pre-existing Phase 8 P08 OTel cold-start flake pattern (slow first attempt then fast subsequent attempts) did NOT manifest — all 3 runs were ~29s, no warm-up signature, which empirically confirms the failures were NOT a cold-start flake but a deterministic missing-collector issue.
- **Committed in:** N/A — environment fix, no source changes. Documented here for the next migration-regen-followed-by-test plan to bake `docker compose up -d` (without `--service`) into the teardown sequence so all services come back online together. Suggested addition to Plan 10-04's D-07 step 6: change `docker compose up -d postgres` to `docker compose up -d postgres otel-collector` (or simply `docker compose up -d` to bring up the full compose graph).

### Plan-Internal Gate Inconsistency (informational, no action needed)

**2. [Rule 3 - Plan-Internal Inconsistency] Task 2 verify-gate `! grep "SchemaId"` is structurally impossible**
- **Found during:** Task 2 verification.
- **Issue:** The plan's automated verify for Task 2 says `! grep "SchemaId" tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` (case-sensitive negation — must be absent). But the ProcessorCreateDto positional names `InputSchemaId`, `OutputSchemaId`, `ConfigSchemaId` ALL contain the literal substring "SchemaId" — and the file still POSTs a Processor in the CreatePrereqAsync helper, so these names cannot be removed without removing the entire ProcessorCreateDto invocation (which would break the file's purpose).
- **Fix:** Interpreted the gate's spirit ("no AssignmentEntity.SchemaId references"), which is satisfied:
  - Zero `SchemaId: schemaId,` body lines (was 2: in NewValidCreateDto + in Update DTO)
  - Zero `read.SchemaId` assertions (was 1)
  - Zero `var (stepId, schemaId)` destructurings (was 4)
  - Zero `Guid schemaId` parameters (was 1 in NewValidCreateDto signature)
  - Zero `using BaseApi.Service.Features.Schema;` directive (was 1)
  - One historical mention preserved in the XML doc-comment: "Phase 10 simplification: no Schema POST since AssignmentEntity no longer carries a SchemaId field" — kept for forensic continuity (describes the transition).
- **Files affected:** N/A (no remediation required; gate semantics interpreted in spirit, not literal-grep).
- **Committed in:** N/A — documentation-only deviation note.

---

**Total deviations:** 1 auto-fixed Rule 3 fix-forward (otel-collector restart) + 1 informational gate-semantics interpretation. No scope creep; no auth gates; no architectural decisions surfaced.

## Issues Encountered

None beyond the Rule 3 deviation documented above. The 7 OTel Observability failures over the first 2 dotnet test attempts cost ~14 minutes of wall-clock time before root cause was identified; the next migration-regen plan should bake `docker compose up -d` (all services) into the teardown sequence to prevent this from recurring.

## User Setup Required

None — internal test-cleanup commit. The otel-collector container restart is a defensive environment-readiness step, not a user-facing action. Phase 10 closes with a fully GREEN test suite + clean docker compose state + zero leaked test databases.

## Next Phase Readiness

**Phase 10 COMPLETE.** All 5 plans landed; 5-commit sequence intact; system is shippable with the post-Phase-10 model end-to-end:
- Production source code (Plans 10-02 + 10-03 — commits 79b07d1 + 12577ac)
- REQUIREMENTS.md spec (Plan 10-01 — commit 1de7e71)
- Migration files (Plan 10-04 — commit 146d482)
- Test suite GREEN at 142/142 across 3 consecutive runs (Plan 10-05 — commit 6d043e4)

The 4 amended REQ-IDs (ENTITY-04, ENTITY-07, VALID-11, VALID-15) are coherently described in REQUIREMENTS.md AND consistently implemented in source + EF config + migration + tests. The 3 Phase 10 plan REQ-IDs claimed (TEST-05, TEST-06, VALID-11) are all closed via the new + existing facts in ProcessorsIntegrationTests + AssignmentsIntegrationTests (TEST-05 = 5 smoke facts per entity is preserved; TEST-06 = error-mapping facts unchanged; VALID-11 = nullable-Guid validator rule extended to ConfigSchemaId).

**Forensic property preserved:** Each Phase 10 commit (1-5) is independently revertable. Reverting #5 (this plan) yields a build-clean production but RED test project. Reverting #4 (migration regen) yields a build-clean production but a v1-shape DB (rollforward path). Reverting #3 (Processor ConfigSchemaId) collapses to pre-add state. Reverting #2 (Assignment SchemaId removal) collapses to pre-removal state. Reverting #1 (doc) leaves the spec ahead-of-state. The 5-commit sequence is bisect-friendly end-to-end per CONTEXT D-02.

**Suggested follow-up (not in this phase's scope):** Plan 10-04 teardown sequence should `docker compose up -d` (all services) instead of `docker compose up -d postgres` (postgres-only) — captured here as a Rule 3 deviation note for future migration-regen plans to inherit.

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` (modified — verified by `git show --stat HEAD` listing 8 files)
- FOUND: `tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `tests/BaseApi.Tests/Integration/WorkflowsIntegrationTests.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `tests/BaseApi.Tests/Integration/StepsIntegrationTests.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: `tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs` (modified — verified by `git show --stat HEAD`)
- FOUND: Commit `6d043e4` in git log (`git log -1 --format=%s` returns `test: update 10 ProcessorCreateDto/UpdateDto sites + AssignmentsIntegrationTests + 2 new ConfigSchemaId facts`)
- VERIFIED: `git show --stat HEAD` lists exactly 8 files changed (the 8 in this plan's `files_modified`)
- VERIFIED: `git status` for tracked files post-commit returns clean for plan scope (pre-existing untracked .planning/ + Properties/ entries unrelated)
- VERIFIED: `grep -c "ConfigSchemaId: null" tests/` returned >= 9 (matches across the 7 task-1 files; ProcessorsIntegrationTests has 2 — helper + Update site; ErrorMappingFacts has 2 — dup-hash + sc5 facts)
- VERIFIED: `grep "Task<Guid> CreatePrereqAsync" tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` returns 1 (new signature) and `! grep "Task<(Guid stepId, Guid schemaId)>" tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` returns no matches (old signature absent)
- VERIFIED: `grep -c "Create_ProcessorWithConfigSchemaId_RoundTripsCorrectly" tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` returns 1 (fact name unique)
- VERIFIED: `grep -c "Create_ProcessorWithNullConfigSchemaId_RoundTripsAsNull" tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` returns 1 (fact name unique)
- VERIFIED: `grep "using BaseApi.Service.Features.Schema" tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs` returns 1 (import added)
- VERIFIED: `! grep "using BaseApi.Service.Features.Schema" tests/BaseApi.Tests/Integration/AssignmentsIntegrationTests.cs` (unused import removed)
- VERIFIED: `dotnet build SK_P.sln -c Release --no-restore` exits 0 with 0 warnings (1.08s)
- VERIFIED: `dotnet build SK_P.sln -c Debug --no-restore` exits 0 with 0 warnings (2.32s)
- VERIFIED: 3 consecutive `dotnet test SK_P.sln --no-restore -c Release` runs each report Passed: 142, Failed: 0, Total: 142 (29.281s / 28.979s / 28.869s)
- VERIFIED: BEFORE/AFTER `psql -l` SHA-256 are byte-identical (`0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`)
- VERIFIED: `docker compose exec -T postgres psql -U postgres -c '\l' | grep stepsdb_test_` returns no matches (zero leaked test databases)
- VERIFIED: 5-commit Phase 10 sequence intact in `git log --oneline -5`: 6d043e4 + 5950e40(docs-only) + 146d482 + ab2c45f(docs-only) + 12577ac + 1ceef4d(docs-only) + 79b07d1 + 5b6605d(docs-only) + 1de7e71 (the 5 production commits at 1de7e71 + 79b07d1 + 12577ac + 146d482 + 6d043e4 alternate with 4 per-plan docs commits; total 9 visible Phase 10 commits with the 5 production-impact ones at the predicted positions)

---
*Phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o*
*Completed: 2026-05-28*
