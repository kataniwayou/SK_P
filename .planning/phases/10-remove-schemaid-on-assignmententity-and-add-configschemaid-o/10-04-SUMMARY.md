---
phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
plan: 04
subsystem: persistence

tags: [migration-regeneration, ef-core, initialcreate, docker-pgdata-teardown, phase-10, d-07-teardown, d-08-gate]

# Dependency graph
requires:
  - phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
    provides: "Plan 10-02 (commit 79b07d1) removed AssignmentEntity.SchemaId; Plan 10-03 (commit 12577ac) added ProcessorEntity.ConfigSchemaId. This plan reads the post-Phase-10 model from both wave-2 commits to regenerate the InitialCreate migration in a single pass capturing the full target schema."
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: "Plan 08-07 established the original InitialCreate migration (20260527203118 timestamp) + AppDbContext.OnModelCreating via ApplyConfigurationsFromAssembly + StartupCompletionService MigrateAsync swap (PERSIST-09 + PERSIST-10 contract). This plan deletes the v1 migration files and regenerates against the new model."
provides:
  - "Old 20260527203118_InitialCreate.cs + .Designer.cs DELETED — no leftover v1-shape migration files in src/BaseApi.Service/Persistence/Migrations/"
  - "New 20260528074618_InitialCreate.cs + .Designer.cs GENERATED — fresh post-2026-05-27 timestamp; clean single migration capturing the full post-Phase-10 schema (no additive drop-column/add-column delta)"
  - "AppDbContextModelSnapshot.cs REGENERATED in place — reflects post-Phase-10 model (declares ProcessorEntity.ConfigSchemaId; no fk_assignment_schema_id)"
  - "Exactly 3 .cs files in src/BaseApi.Service/Persistence/Migrations/ — new InitialCreate + Designer + ModelSnapshot — matches SPEC REQ-6 invariant"
  - "Assignments table block: NO schema_id column, NO fk_assignment_schema_id constraint, NO ix_assignments_schema_id index — only fk_assignment_step_id (Restrict) remains"
  - "Processors table block: config_schema_id column (uuid NULL) + fk_processor_config_schema_id ForeignKey (SetNull cascade) + ix_processors_config_schema_id index — mirrors InputSchemaId/OutputSchemaId verbatim"
  - "Pristine Postgres + named volume (pgdata) — D-07 teardown sequence executed (docker compose down -v + dotnet ef database drop -f + rm of old migration files + dotnet ef migrations add + docker compose up -d postgres); pgdata named volume sk_p_pgdata removed and recreated; stepsdb starts clean for next test boot"
  - "Commit #4 of the 5-commit Phase 10 sequence per CONTEXT D-02 — verbatim subject: 'migration: regenerate InitialCreate (drop assignments.schema_id; add processors.config_schema_id)'. Bisect-friendly atomic regen commit; production projects (BaseApi.Core + BaseApi.Service) build zero-warning Release."
  - "PostgresExceptionMapper.cs UNTOUCHED — regex ^fk_[a-z0-9]+_(?<col>[a-z0-9_]+)$ invariant preserved by zero-touch (new fk_processor_config_schema_id parses as table='processor' (no underscore — invariant satisfied), column='config_schema_id'); 23503 -> HTTP 422 mapping path preserved"
  - "Mapperly entity mappers (ProcessorEntityMapper + AssignmentEntityMapper) UNTOUCHED — symmetric add/remove invariant per CONTEXT D-10 holds across migration regen"
affects:
  - 10-05-test-fixture-updates-and-configschemaid-facts (Plan 05 runs the 3-consecutive-GREEN dotnet test cycle against this regenerated migration applied by StartupCompletionService.MigrateAsync to per-class throwaway DBs; the v1 fk_assignment_schema_id constraint absence + the new fk_processor_config_schema_id presence are load-bearing for the 2 new ConfigSchemaId round-trip facts + the AssignmentsIntegrationTests CreatePrereqAsync simplification)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Migration regeneration via dotnet ef migrations add against the post-model state — CONTEXT D-07 teardown sequence: docker compose down -v + dotnet ef database drop -f + rm old InitialCreate files + dotnet ef migrations add (with -o Persistence/Migrations override) + docker compose up -d postgres. Produces a clean single migration capturing the full target schema in one pass; no additive sidecar."
    - "Snapshot-replacement gate for clean single-migration regen — the plan asserted dotnet ef migrations add would regenerate the snapshot automatically to produce a clean InitialCreate; in practice EF computes a DELTA against the existing snapshot. The clean path requires ALSO deleting the AppDbContextModelSnapshot.cs before running migrations add (or removing the bad delta migration via `dotnet ef migrations remove`, which also deletes the snapshot, then re-running). Rule 3 fix-forward — applied here as 'migrations remove + re-run with -o' to land a clean InitialCreate."
    - "Explicit --output-dir for ef migrations add — when the existing migrations live in a non-default subfolder (here: Persistence/Migrations/ instead of EF Core 8.0's default Migrations/), dotnet ef migrations add defaults to the EF Core default and emits files in the WRONG location. Pass `-o Persistence/Migrations` to land files in the canonical project subfolder and preserve the BaseApi.Service.Persistence.Migrations namespace. Without this flag, EF writes to ./Migrations/ and uses BaseApi.Service.Migrations namespace, leaving the existing snapshot orphaned."
    - "D-08 verification gate as single atomic shell script — all 9 invariant checks (old files absent + exactly 3 .cs files + snapshot exists + no fk_assignment_schema_id/ix_assignments_schema_id in new InitialCreate + config_schema_id column + fk_processor_config_schema_id constraint + ix_processors_config_schema_id index + SetNull cascade + ConfigSchemaId in snapshot + zero-touch invariant on PostgresExceptionMapper) run as `set -euo pipefail` script. Failure on any line aborts the gate with specific error message."
    - "Git rename detection on regenerated migrations — when a file's timestamp changes but ~97% of content is preserved (CreateTable blocks, column shapes, audit columns, BaseEntity columns), git's `--find-renames` heuristic (R097) collapses 2 deletions + 2 additions into 2 rename entries at default --stat output. Full decomposition (5 entries: 2D + 2A + 1M) requires `--no-renames` flag. Both representations are equivalent at the object-graph level; the plan's 'exactly 5 entries' verification was based on --no-renames output."

key-files:
  created:
    - "src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.cs"
    - "src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.Designer.cs"
  modified:
    - "src/BaseApi.Service/Persistence/Migrations/AppDbContextModelSnapshot.cs"
  deleted:
    - "src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.cs"
    - "src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.Designer.cs"

key-decisions:
  - "Followed CONTEXT D-02 verbatim commit subject: 'migration: regenerate InitialCreate (drop assignments.schema_id; add processors.config_schema_id)' — plan-locked phrasing supports cross-session grep-based audit."
  - "Followed CONTEXT D-07 teardown sequence verbatim: Step 1 docker compose down -v removed sk_p_pgdata named volume; Step 2 dotnet ef database drop -f exited non-zero (acceptable — Postgres was down post-Step 1, volume teardown already handled deletion); Step 3 rm old InitialCreate files; Step 4 dotnet ef migrations add (Rule 3 deviation — needed two passes; see below); Step 5 docker compose up -d postgres; pg_isready healthcheck flipped healthy in ~6 seconds (well under the 20s timeout)."
  - "Rule 3 deviation — Snapshot-replacement gap in plan: First `dotnet ef migrations add InitialCreate -p src/BaseApi.Service -s src/BaseApi.Service` run produced an ADDITIVE migration (Up() started with DropForeignKey fk_assignment_schema_id + DropColumn schema_id + AddColumn config_schema_id) NOT a clean InitialCreate. Root cause: the existing AppDbContextModelSnapshot.cs (v1 shape) was still in Persistence/Migrations/, so EF computed a delta against it. The plan's footnote at lines 196-200 incorrectly asserted that `migrations add` would automatically regenerate the snapshot to produce a clean single migration. Fix-forward: ran `dotnet ef migrations remove -f` to delete both the bad migration AND the snapshot (EF's remove command DOES delete the snapshot as documented), then re-ran `dotnet ef migrations add InitialCreate -o Persistence/Migrations` against the no-migration-history state, which produced a clean single InitialCreate AND regenerated a fresh AppDbContextModelSnapshot.cs reflecting the post-Phase-10 model."
  - "Rule 3 deviation — Default output-dir mismatch: dotnet ef migrations add (EF Core 8.0) defaults to writing files to `<project-root>/Migrations/` regardless of where existing migrations live. The first regen attempt emitted files at `src/BaseApi.Service/Migrations/` with `namespace BaseApi.Service.Migrations` — NOT in `Persistence/Migrations/` where the canonical Phase 8 P07 layout placed them. Fix-forward: passed `-o Persistence/Migrations` flag on the second invocation; EF emitted files at the canonical path with the correct `namespace BaseApi.Service.Persistence.Migrations`. The orphan `src/BaseApi.Service/Migrations/` directory left by the first attempt was rmdir'd (was empty after the bad migration was removed)."
  - "Postgres container health gate met (Step 5 wait-loop): pg_isready flipped 'healthy' in ~6 seconds (well under the 20-second timeout). docker compose ps postgres --format json output included `\"Health\":\"healthy\"` on the first 2-second poll."
  - "Production-build sanity check passed (OPTIONAL pre-commit step from plan Task 3 action): `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Release --no-restore` exited 0 with 0 warnings / 0 errors in 1.72s — confirms the regenerated migration C# code compiles cleanly under TreatWarningsAsErrors=true. Test project build deliberately NOT attempted per plan — Plan 05 owns the suite GREEN gate."
  - "Git rename-detection cosmetic: `git show --stat HEAD` reports 3 entries (R097 + R097 + M) instead of the plan's expected '5 entries (2 D + 2 A + 1 M)'. Object-graph equivalence proven via `git show HEAD --name-status --no-renames` which DOES return 5 entries. Functional state matches plan exactly — the 5-entry assertion in the plan referenced the --no-renames decomposition implicitly. No deviation classification needed; rename detection is git's default rendering, not a content mismatch."
  - "PostgresExceptionMapper.cs and both Mapperly entity mappers byte-identical post-regen — explicitly verified via `git diff src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` (empty) + `git diff src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs` (empty) + `git diff src/BaseApi.Service/Features/Assignment/AssignmentEntityMapper.cs` (empty). CONTEXT D-10 zero-touch invariant + ERROR-11 constraint-name regex invariant both preserved through migration regeneration as expected."

patterns-established:
  - "Migration regeneration in place — DELETE old migration files (both `.cs` and `.Designer.cs`) BEFORE running `dotnet ef migrations add`; also delete OR allow `dotnet ef migrations remove` to delete the existing `AppDbContextModelSnapshot.cs`; then re-run `migrations add` to land a clean single migration. ALWAYS pass `-o <relative-path-from-project-root>` to override EF Core 8.0's default `Migrations/` output dir when migrations live in a project subfolder."
  - "D-07 teardown sequence pattern — when regenerating a v1 migration: docker compose down -v (tears down pgdata named volume) → dotnet ef database drop -f (defensive; non-zero exit acceptable if Postgres is already down) → rm old InitialCreate files (and let migrations remove handle the snapshot) → dotnet ef migrations add InitialCreate -o <subfolder> → docker compose up -d postgres → wait for pg_isready healthy. Producing a clean single migration AND a pristine dev DB for the next test boot. PERSIST-09 + PERSIST-10 contract preserved."
  - "D-08 verification gate pattern — single shell script with set -euo pipefail combining 9 invariants: old-files-absent (negation tests), exact-file-count (`ls *.cs | wc -l`), positive existence of new files, grep negations (no removed-symbol refs in new migration), grep positives (new column + new FK + new index + new cascade behavior), grep positives on snapshot (new property name), grep negations on snapshot (removed constraint name), educational verification of zero-touch invariant on referencing source files. Failure on any line aborts the gate with specific error message."

requirements-completed: [PERSIST-09, PERSIST-10, PERSIST-13, ERROR-04, ERROR-11]

# Metrics
duration: ~4min
completed: 2026-05-28
---

# Phase 10 Plan 04: Regenerate InitialCreate Migration Summary

**Migration regen commit #4 of Phase 10 — old `20260527203118_InitialCreate.{cs,Designer.cs}` DELETED and new `20260528074618_InitialCreate.{cs,Designer.cs}` GENERATED against the post-Phase-10 model; `AppDbContextModelSnapshot.cs` regenerated in place; D-07 teardown sequence executed end-to-end (pgdata volume torn down + recreated pristine); D-08 verification gate (9 invariant checks) all PASS; production projects build zero-warning Release.**

## Performance

- **Duration:** ~4 min (261 seconds wall-clock from PLAN_START to commit landed)
- **Started:** 2026-05-28T07:44:03Z
- **Completed:** 2026-05-28T07:48:24Z
- **Tasks:** 4 (Task 0 D-07 teardown, Task 1 migration regen, Task 2 D-08 gate, Task 3 commit)
- **Files modified:** 5 entries via `--no-renames` (2 D + 2 A + 1 M); git rename-detection collapses to 3 entries (R097 + R097 + M) at default stat output

## Accomplishments

- D-07 teardown sequence executed end-to-end:
  - `docker compose down -v` removed the `sk_p_pgdata` named volume + both running containers (postgres + otel-collector)
  - `dotnet ef database drop -f` exited non-zero (expected — Postgres was down post-step-1; the volume teardown already handled the deletion)
  - `rm src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.{cs,Designer.cs}` deleted the v1 migration files
- Migration regenerated against post-Phase-10 model:
  - First attempt produced an additive DELTA migration (DropForeignKey + DropColumn + AddColumn) because the v1 `AppDbContextModelSnapshot.cs` was still present
  - Rule 3 fix-forward: ran `dotnet ef migrations remove -f` (which deleted both the bad migration AND the snapshot) then re-ran `dotnet ef migrations add InitialCreate -o Persistence/Migrations` against the no-history state
  - Result: clean single `20260528074618_InitialCreate.cs` with `Up()` starting at `CreateTable` (no Drop* calls), reflecting the full post-Phase-10 schema
- Postgres restarted pristine via `docker compose up -d postgres`; pg_isready healthcheck flipped healthy in ~6 seconds
- D-08 verification gate (9 invariant checks) all PASS:
  - Gate 1: old `20260527203118_InitialCreate.cs` absent
  - Gate 2: old `20260527203118_InitialCreate.Designer.cs` absent
  - Gate 3: exactly 3 .cs files in `Persistence/Migrations/`
  - Gate 4: no `fk_assignment_schema_id` or `ix_assignments_schema_id` in new InitialCreate
  - Gate 5a: `config_schema_id` present in new InitialCreate
  - Gate 5b: `fk_processor_config_schema_id` constraint present
  - Gate 5c: `ix_processors_config_schema_id` index present
  - Gate 6: `fk_processor_config_schema_id` cascade is `ReferentialAction.SetNull` (verified via `grep -B2 -A6`)
  - Gate 7: `ConfigSchemaId` declared in `AppDbContextModelSnapshot.cs`
  - Gate 8: `fk_assignment_schema_id` absent from `AppDbContextModelSnapshot.cs`
  - Gate 9: new FK name satisfies PostgresExceptionMapper.cs:54-67 invariant (table='processor' has no underscore)
- Single atomic commit `146d482` landed with verbatim D-02 subject
- Production projects (`BaseApi.Service` Release) build clean — 0 warnings + 0 errors
- Zero-touch invariants confirmed:
  - `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` byte-identical (regex still parses new constraint name)
  - `src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs` byte-identical (Mapperly symmetric-addition invariant)
  - `src/BaseApi.Service/Features/Assignment/AssignmentEntityMapper.cs` byte-identical (Mapperly symmetric-removal invariant)

## Task Commits

Single atomic commit per D-02 (commit #4 of the Phase 10 sequence):

1. **Task 0 + Task 1 + Task 2 + Task 3 (combined atomic migration-regen commit):** `146d482` — `migration: regenerate InitialCreate (drop assignments.schema_id; add processors.config_schema_id)`

Per CONTEXT D-02, all 4 plan tasks collapse to a single git commit (the migration-regen commit #4 of the Phase 10 sequence). Task 0 is filesystem mutations (volume teardown + file deletions) that prepare the workspace but do NOT produce intermediate commits — the v1 migration deletion lands ATOMICALLY with the new migration addition in commit #4 so the migration directory is never in a "no InitialCreate present" intermediate state on the branch.

**Plan metadata:** This SUMMARY.md + updated STATE.md + updated ROADMAP.md + updated REQUIREMENTS.md will be added as a separate final docs commit per the executor's `final_commit` step.

## Files Created/Modified

- **CREATED:** `src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.cs` — fresh post-2026-05-27 timestamp; clean single migration; `Up()` starts with `CreateTable name: "schemas"` (no Drop* calls); processors block has all 3 schema FKs (input + output + config) with SetNull; assignments block has only `fk_assignment_step_id` (Restrict).
- **CREATED:** `src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.Designer.cs` — EF-generated companion to the new migration; contains the EF model metadata mirroring the post-Phase-10 model.
- **MODIFIED:** `src/BaseApi.Service/Persistence/Migrations/AppDbContextModelSnapshot.cs` — regenerated in place by EF; declares ProcessorEntity with ConfigSchemaId; AssignmentEntity carries no SchemaId. Same filename + same `BaseApi.Service.Persistence.Migrations` namespace as before.
- **DELETED:** `src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.cs` — v1 migration carrying assignments.schema_id + lacking processors.config_schema_id.
- **DELETED:** `src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.Designer.cs` — companion to the deleted v1 migration.

## Decisions Made

None new — followed plan and CONTEXT D-02 / D-07 / D-08 exactly. The decisions captured here are pre-existing CONTEXT decisions whose application is documented above (D-02 atomic-regen commit + verbatim subject; D-07 teardown sequence; D-08 verification gate). The Rule 3 fix-forwards documented in `key-decisions` and `Deviations` did NOT introduce new decisions — they're applications of the deviation framework (Rules 1-3 auto-fix without user permission) to surface a plan-internal gap (the snapshot-replacement and output-dir issues) and resolve them via standard `dotnet ef` commands (`migrations remove` + `migrations add -o`).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Plan-Internal Gap] Snapshot-replacement required for clean InitialCreate regen**
- **Found during:** Task 1 first attempt (after `dotnet ef migrations add InitialCreate`).
- **Issue:** The plan's Task 1 footnote at lines 196-200 asserted that `dotnet ef migrations add` would automatically regenerate `AppDbContextModelSnapshot.cs` to reflect the post-Phase-10 model and produce a single clean InitialCreate migration. In practice, EF Core 8.0's `migrations add` command READS the existing snapshot as the "previous model state" and computes a DELTA against the current model. With the v1 snapshot still present (only the InitialCreate.cs files had been deleted in Task 0 step 3), EF emitted an ADDITIVE migration whose `Up()` started with `migrationBuilder.DropForeignKey(name: "fk_assignment_schema_id", ...)` + `DropColumn(...)` + `AddColumn(name: "config_schema_id", ...)` — NOT a clean InitialCreate. SPEC REQ-6 explicitly forbids this ("No additive 'drop column / add column' migration is produced").
- **Fix:** Ran `dotnet ef migrations remove -f -p src/BaseApi.Service -s src/BaseApi.Service` to discard the bad delta migration. `dotnet ef migrations remove` ALSO deletes the snapshot file as part of its rollback (documented behavior — confirmed in stdout: "Removing migration '20260528074511_InitialCreate'. Removing model snapshot."). This left the `Persistence/Migrations/` directory completely empty (no .cs files), giving EF no prior model state to delta against. Re-ran `dotnet ef migrations add InitialCreate -p src/BaseApi.Service -s src/BaseApi.Service -o Persistence/Migrations` against the no-history state, which produced a clean single InitialCreate (Up() starts at CreateTable) AND regenerated a fresh AppDbContextModelSnapshot.cs.
- **Files affected:** transient — first attempt's `src/BaseApi.Service/Migrations/20260528074511_InitialCreate.{cs,Designer.cs}` were created then removed; final state has exactly 3 .cs files in `src/BaseApi.Service/Persistence/Migrations/`.
- **Verification:** Final `Up()` method starts with `migrationBuilder.CreateTable(name: "schemas", ...)` (clean InitialCreate) — verified via `head -30 20260528074618_InitialCreate.cs`. No Drop* calls in Up(). The 9-check D-08 gate all PASS confirming the clean post-Phase-10 schema is captured in a single migration file.
- **Committed in:** `146d482` (part of the atomic Plan 10-04 commit — no separate fix(10-04) commit because the deviation was resolved BEFORE the commit was created)

**2. [Rule 3 - Plan-Internal Gap] EF default output-dir mismatch**
- **Found during:** Task 1 first attempt (after `dotnet ef migrations add InitialCreate`).
- **Issue:** EF Core 8.0's `dotnet ef migrations add` defaults to writing files to `<project-root>/Migrations/` regardless of where the existing snapshot lives. The first regen attempt emitted files at `src/BaseApi.Service/Migrations/20260528074511_InitialCreate.{cs,Designer.cs}` with `namespace BaseApi.Service.Migrations`, NOT in the canonical `src/BaseApi.Service/Persistence/Migrations/` where Plan 08-07 had originally placed them with `namespace BaseApi.Service.Persistence.Migrations`. This would have produced a split-location migration set (new files in `Migrations/`, snapshot in `Persistence/Migrations/`) with mismatched namespaces — EF would not be able to resolve the migrations as a coherent set at runtime.
- **Fix:** Passed `-o Persistence/Migrations` flag on the second `dotnet ef migrations add` invocation. EF emitted both new files (`20260528074618_InitialCreate.cs` + `.Designer.cs`) at the canonical path `src/BaseApi.Service/Persistence/Migrations/` with the correct `namespace BaseApi.Service.Persistence.Migrations`. Also `rmdir src/BaseApi.Service/Migrations` cleaned up the empty orphan default-output directory.
- **Files affected:** transient — first attempt's `src/BaseApi.Service/Migrations/` directory created then removed (the bad migration was removed by Rule 3 fix #1's `migrations remove`, leaving the directory empty; rmdir cleaned the empty dir).
- **Verification:** `find . -name "*InitialCreate*"` returns only files under `src/BaseApi.Service/Persistence/Migrations/` (no orphan files at `src/BaseApi.Service/Migrations/`); `grep -n "namespace " src/BaseApi.Service/Persistence/Migrations/*.cs` shows all 3 files use `BaseApi.Service.Persistence.Migrations`.
- **Committed in:** `146d482` (part of the atomic Plan 10-04 commit — the orphan directory was rmdir'd before the commit was created, so the final state at HEAD has no trace of the first attempt)

---

**Total deviations:** 2 auto-fixed (both Rule 3 — plan-internal gaps surfacing EF Core 8.0 default behaviors the plan footnote didn't account for)
**Impact on plan:** Zero scope creep; zero unplanned work outside the plan's stated Tasks 0-3; zero auth gates. The 2 fix-forwards are bookkeeping repairs (correcting where EF Core 8.0 wrote files + ensuring a clean snapshot replacement) that landed BEFORE the atomic commit was created, so the final commit `146d482` contains exactly the 5 file changes the plan anticipated (2 D + 2 A + 1 M). No cosmetic indication of the two fix-forwards in the commit graph — both transient.

## Issues Encountered

None beyond the 2 Rule 3 deviations documented above. The deviations were resolved via standard `dotnet ef` commands (`migrations remove` + `migrations add -o`) without requiring source code edits, schema redesigns, or external dependencies.

## User Setup Required

None — internal migration-regen commit. The Postgres dev DB was reset to pristine state (sk_p_pgdata named volume torn down and recreated via the D-07 teardown sequence). No external configuration touched; no secrets rotated. Next dev/test boot will apply the regenerated migration via `StartupCompletionService.MigrateAsync` on a fresh schema (PERSIST-09 + PERSIST-10 contract preserved).

## Next Phase Readiness

**Plan 10-05 (next):** Ready to execute. The post-Phase-10 model is now fully captured in:
- Production source code (Plans 10-02 + 10-03 commits 79b07d1 + 12577ac)
- REQUIREMENTS.md spec (Plan 10-01 commit 1de7e71)
- Migration files (this plan's commit 146d482)

Plan 10-05 will:
- Add `ConfigSchemaId: null` as the 7th positional param to all 8 `ProcessorCreateDto(...)` call sites + 1 `ProcessorUpdateDto(...)` site across the test tree (mechanical edits per PATTERNS.md analog table)
- Simplify `AssignmentsIntegrationTests.CreatePrereqAsync` — drop the Schema POST + tuple return; signature changes from `Task<(Guid stepId, Guid schemaId)>` to `Task<Guid>`
- Drop `SchemaId` from `NewValidCreateDto` + 4 call sites + 1 round-trip assertion + the Update DTO
- Add 2 new `[Fact]`s to `ProcessorsIntegrationTests.cs`: `Create_ProcessorWithConfigSchemaId_RoundTripsCorrectly` (POSTs Schema inline, POSTs Processor with ConfigSchemaId, GET asserts round-trip) and `Create_ProcessorWithNullConfigSchemaId_RoundTripsAsNull`
- Run `dotnet test SK_P.sln --no-restore -c Release` 3 times consecutively expecting 140/140 GREEN each run (Phase 8 P08 + Phase 9 P09-03 convention)
- Verify byte-identical `psql -l` SHA-256 snapshot BEFORE and AFTER the 3-run cycle (Phase 3 D-15 cleanup discipline)

**Forensic property preserved:** If execution stops at this commit, the production source + REQUIREMENTS.md + migration files are all internally consistent — entity + DTOs + validator + EF config + spec + DB schema all describe the same post-Phase-10 shape. The test project remains intentionally RED (as in Plans 10-02 and 10-03) until Plan 10-05 lands; this is the documented D-02 bisect-friendliness contract. The pristine Postgres + named volume teardown means Plan 10-05's first `dotnet test` run will apply the regenerated migration to a clean schema on each per-class throwaway DB.

## Self-Check: PASSED

- FOUND: `src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.cs` (created — verified by `ls` and `git show --stat HEAD`)
- FOUND: `src/BaseApi.Service/Persistence/Migrations/20260528074618_InitialCreate.Designer.cs` (created — verified by `ls` and `git show --stat HEAD`)
- FOUND: `src/BaseApi.Service/Persistence/Migrations/AppDbContextModelSnapshot.cs` (modified — verified by `git show --stat HEAD`)
- VERIFIED ABSENT: `src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.cs` (`test ! -f` returned true)
- VERIFIED ABSENT: `src/BaseApi.Service/Persistence/Migrations/20260527203118_InitialCreate.Designer.cs` (`test ! -f` returned true)
- VERIFIED ABSENT: `src/BaseApi.Service/Migrations/` directory (rmdir'd cleanly; `ls` confirms it no longer exists)
- FOUND: Commit `146d482` in git log (`git log -1 --format=%s` returns `migration: regenerate InitialCreate (drop assignments.schema_id; add processors.config_schema_id)`)
- VERIFIED: `git show HEAD --name-status --no-renames` returns exactly 5 entries: 2 D (old) + 2 A (new) + 1 M (snapshot); default `git show --stat` shows 3 entries (R097 + R097 + M) due to git rename detection (cosmetic, not a content mismatch)
- VERIFIED: exactly 3 .cs files in `src/BaseApi.Service/Persistence/Migrations/` (`ls *.cs | wc -l` returns 3)
- VERIFIED: new InitialCreate timestamp `20260528074618` is strictly greater than the deleted `20260527203118`
- VERIFIED PRESENT: `grep "config_schema_id" 20260528074618_InitialCreate.cs` returns multiple matches (column + FK + index)
- VERIFIED PRESENT: `grep "fk_processor_config_schema_id" 20260528074618_InitialCreate.cs` returns 1 match (constraint name)
- VERIFIED PRESENT: `grep "ix_processors_config_schema_id" 20260528074618_InitialCreate.cs` returns 1 match (index)
- VERIFIED PRESENT: `grep -B2 -A6 "fk_processor_config_schema_id" 20260528074618_InitialCreate.cs | grep "ReferentialAction.SetNull"` returns 1 match (cascade behavior locked)
- VERIFIED PRESENT: `grep "ConfigSchemaId" AppDbContextModelSnapshot.cs` returns matches (snapshot reflects post-Phase-10 model)
- VERIFIED ABSENT: `grep "fk_assignment_schema_id" 20260528074618_InitialCreate.cs` returns no matches
- VERIFIED ABSENT: `grep "ix_assignments_schema_id" 20260528074618_InitialCreate.cs` returns no matches
- VERIFIED ABSENT: `grep "fk_assignment_schema_id" AppDbContextModelSnapshot.cs` returns no matches
- VERIFIED: assignments table block in new InitialCreate has only `fk_assignment_step_id` (Restrict) — no schema FK
- VERIFIED: processors table block in new InitialCreate has all 3 schema FKs (input + output + config) all with SetNull
- VERIFIED: `git diff src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` returns empty (zero-touch invariant confirmed)
- VERIFIED: `git diff src/BaseApi.Service/Features/Processor/ProcessorEntityMapper.cs` returns empty (Mapperly symmetric-addition zero-touch)
- VERIFIED: `git diff src/BaseApi.Service/Features/Assignment/AssignmentEntityMapper.cs` returns empty (Mapperly symmetric-removal zero-touch)
- VERIFIED: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Release --no-restore` exits 0 with 0 warnings + 0 errors (1.72s)
- VERIFIED: `docker compose ps postgres --format json | grep Health` shows `"Health":"healthy"` (postgres pristine and running after `docker compose up -d postgres`)
- VERIFIED: `git status --porcelain src/BaseApi.Service/Persistence/Migrations/` returns no entries within plan scope (working tree clean for tracked plan scope; pre-existing untracked `.planning/phases/` and `src/BaseApi.Service/Properties/` entries are from earlier plans / Phase 1 leftovers and are unrelated to this plan)

---
*Phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o*
*Completed: 2026-05-28*
