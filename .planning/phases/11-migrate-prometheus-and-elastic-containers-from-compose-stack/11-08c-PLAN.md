---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 08c
type: execute
wave: 6
depends_on:
  - "11-07"
  - "11-08b"
files_modified:
  - tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs
autonomous: false
requirements:
  - OBSERV-13
  - OBSERV-14
  - TEST-07
must_haves:
  truths:
    - "`OtelCollectorFixture.cs` is DELETED — `git rm`; zero remaining references anywhere in `tests/` or `src/` (verified by grep at Task 1)"
    - "3 consecutive GREEN `dotnet test SK_P.sln --no-restore -c Release` runs (Phase 3 D-18 cadence) at a stable fact count across all 3 runs"
    - "Byte-identical psql `\\l` SHA-256 BEFORE/AFTER the 3-run cycle (Phase 3 D-15 cleanup discipline preserved across Phase 11)"
    - "No telemetry.jsonl regression — `tests/.otel-out/` directory does NOT exist on the local filesystem after the 3-run cycle (Plan 11-05 removed it; this plan verifies it stays gone)"
    - "No orphan references in any .cs file: zero matches for `OtelCollectorFixture`, `OtelEndOfSuiteCleanup`, `TraceExportTests`, `.WithTracing`, `tests/.otel-out` across `src/**/*.cs` + `tests/**/*.cs`"
    - "Phase 11 SUMMARY narrative written (multi-paragraph; matches Phase 10 SUMMARY precedent) — captures the 8-commit decomposition + the 3 Wave-6 plan-split rationale per checker WARNING #3"
    - "ROADMAP.md Phase 11 marked Complete; STATE.md milestone log appended"
  artifacts:
    - path: "tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs"
      provides: "DELETED — final removal after all consumers migrated"
    - path: ".planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md"
      provides: "Phase 11 closing narrative — 10-commit sequence (1 doc + 3 parallel-safe config + 1 cleanup + 1 helpers + 1 E2E + 3 Wave-6-split) + revision-iteration-1 fixes summary"
      contains: "Phase 11 COMPLETE"
  key_links:
    - from: "tests/ + src/ codebase (post-deletion)"
      to: "tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs (NO LONGER EXISTS)"
      via: "git rm — zero remaining references prove the deletion is safe"
      pattern: "OtelCollectorFixture"
    - from: ".planning/STATE.md (post-Phase-11 milestone entry)"
      to: ".planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md"
      via: "STATE.md milestone log references the SUMMARY's commit sequence + key decisions"
      pattern: "Phase 11"
---

<objective>
Final Phase 11 closing plan — split from the original Plan 11-08 per checker WARNING #3 (scope_sanity). Three responsibilities:

1. **Delete `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs`** — after Plans 11-08a (HealthEndpointsTests rebase) and 11-08b (LogExport/LogLevelFilter/MetricsExport migration) have landed, the file has ZERO remaining consumers. `git rm` it.

2. **Drive the Phase 3 D-18 cadence** — 3 consecutive GREEN `dotnet test SK_P.sln --no-restore -c Release` runs with a stable fact count across all 3 runs. Records wall-clock per run and the actual fact count in the SUMMARY.

3. **Drive the Phase 3 D-15 byte-identical psql `\l` snapshot** — capture SHA-256 BEFORE the 3-run cycle, capture SHA-256 AFTER, prove they match (no leaked `stepsdb_test_*` databases — PostgresFixture throwaway-DB discipline preserved across Phase 11's WebApplicationFactory subclasses).

4. **Write Phase 11 SUMMARY narrative + update ROADMAP.md + STATE.md** — close the phase formally with a multi-paragraph narrative matching Phase 10 SUMMARY precedent.

Purpose: brings the Phase 11 sequence to a clean close with bisect-friendly git history (each commit independently revertable; no half-state where some consumers reference the deleted fixture). Validates the entire suite end-to-end against the live stack with the cleanup-discipline invariants Phase 3 established.

**NOT in this plan's scope** (already covered by Plans 11-08a/b):
- HealthEndpointsTests rebase (Plan 11-08a).
- LogExport/LogLevelFilter/MetricsExport migration (Plan 11-08b).
- Any code under `src/` or `tests/` other than the `OtelCollectorFixture.cs` deletion.

Output: TWO atomic commits:
- Commit A: `chore(observability): remove OtelCollectorFixture.cs (no remaining consumers after Plans 11-08a + 11-08b)` — single-file deletion.
- Commit B (after Task 2 checkpoint approval): `docs(phase-11): close phase — 10-commit sequence + revision-iteration-1 fixes summary` — Phase 11 SUMMARY + ROADMAP.md + STATE.md updates.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-CONTEXT.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-RESEARCH.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-PATTERNS.md
@.planning/phases/10-remove-schemaid-on-assignmententity-and-add-configschemaid-o/10-05-SUMMARY.md

<interfaces>
<!-- After Plans 11-08a + 11-08b have landed, the codebase state should be: -->
<!-- HealthEndpointsTests.cs       — references Phase8WebAppFactory + Phase11WebAppFactory; no OtelCollectorFixture -->
<!-- LogExportTests.cs              — uses Phase11WebAppFactory + ElasticsearchTestClient; no OtelCollectorFixture -->
<!-- LogLevelFilterTests.cs         — uses Phase11WebAppFactory + ElasticsearchTestClient; no OtelCollectorFixture -->
<!-- MetricsExportTests.cs          — uses Phase11WebAppFactory + PrometheusTestClient; no OtelCollectorFixture -->
<!-- OtelCollectorFixture.cs        — STILL EXISTS but ORPHANED (zero consumers) — about to be deleted -->

<!-- Expected fact counts after Phase 11 completes: -->
<!-- Phase 10 baseline:    142 facts -->
<!-- Plan 11-05 deletes:   TraceExportTests 2 facts removed = 140 -->
<!-- Plan 11-07 adds:      SchemasLogsE2ETests 1 + SchemasMetricsE2ETests 1 = +2 → 142 -->
<!-- Plan 11-08a affects:  0 net (HealthEndpointsTests has 7 facts, all preserved through rebase) -->
<!-- Plan 11-08b affects:  0 net (LogExportTests 2 + LogLevelFilterTests 2 + MetricsExportTests 3 = 7 facts, all preserved) -->
<!-- Plan 11-08c affects:  0 net (OtelCollectorFixture deletion has no facts) -->
<!-- EXPECTED FINAL:       142 facts (subject to drift from any test additions outside Phase 11 scope) -->

<!-- Phase 3 D-15 psql \l SHA-256 baseline -->
Expected baseline (Phase 8 P08 + Phase 9 P09-03 + Phase 10 P10-05 carry-forward):
  0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127

<!-- Phase 10 SUMMARY structure (model for Phase 11 SUMMARY) -->
.planning/phases/10-*/10-05-SUMMARY.md — closing-plan format includes:
  - Summary block (1-paragraph close)
  - Commit log table (commit hash + subject + scope)
  - Wave-by-wave breakdown
  - Key decisions table (CONTEXT D-XX references)
  - Threat model status
  - Test count + wall-clock evidence
  - Phase 11-specific addendum: revision iteration 1 closure narrative
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Verify orphaned OtelCollectorFixture.cs has zero remaining consumers; delete it</name>
  <files>tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs</files>
  <read_first>
    - tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs (the file about to be deleted — verify its current shape)
    - Sanity grep: `grep -rn "OtelCollectorFixture" tests/ src/ --include="*.cs"` should now show ONLY the file's own contents (zero external references)
  </read_first>
  <action>
    Two-step deletion with defensive grep.

    Step 1 — confirm zero remaining external consumers:
    ```bash
    grep -rn "OtelCollectorFixture" tests/ src/ --include="*.cs" | grep -v "^tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs:"
    ```

    Expected output: EMPTY (zero lines). The grep filters out the file's own contents; any OTHER .cs file appearing here means a consumer was missed by Plans 11-08a + 11-08b. If output is non-empty:
    - STOP. Investigate which file still references OtelCollectorFixture.
    - Most likely cause: Plan 11-08a or 11-08b didn't fully execute (HealthEndpointsTests / LogExportTests / LogLevelFilterTests / MetricsExportTests).
    - Do NOT proceed to the `git rm` until the grep returns empty.

    Step 2 — delete the file:
    ```bash
    git rm tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs
    ```

    Step 3 — re-verify zero references after deletion:
    ```bash
    grep -rn "OtelCollectorFixture" tests/ src/ --include="*.cs"
    ```

    Expected output: EMPTY (the file's own contents are gone now too).

    Step 4 — build verification:
    ```bash
    dotnet build SK_P.sln -c Release --no-restore
    ```

    Must exit 0 zero-warning. If any compilation error references OtelCollectorFixture, the deletion broke a hidden consumer — restore the file via `git checkout HEAD~1 -- tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` and re-investigate.

    Step 5 — commit:
    Stage ONLY the deletion (`git rm` already staged it; no additional `git add` needed). Create commit A with the exact message:
    ```
    chore(observability): remove OtelCollectorFixture.cs (no remaining consumers after Plans 11-08a + 11-08b)
    ```

    Use a HEREDOC. Verify `git status --porcelain` returns empty post-commit.
  </action>
  <verify>
    <automated>! test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs (file deleted); ! grep -rn "OtelCollectorFixture" tests/ src/ --include="*.cs" (negation — no references anywhere in any .cs file); dotnet build SK_P.sln -c Release --no-restore exits 0 zero-warning; git log -1 --format=%s returns "chore(observability): remove OtelCollectorFixture.cs (no remaining consumers after Plans 11-08a + 11-08b)"; git show --stat HEAD lists exactly 1 file deleted; git status --porcelain returns empty</automated>
  </verify>
  <done>OtelCollectorFixture.cs deleted via `git rm`; zero remaining references anywhere in `src/` or `tests/`; solution builds zero-warning; single-deletion commit landed.</done>
</task>

<task type="checkpoint:human-verify" gate="blocking">
  <name>Task 2: Phase 3 D-18 cadence — 3 consecutive GREEN dotnet test runs + Phase 3 D-15 byte-identical psql snapshot</name>
  <what-built>All Phase 11 code/config/test landings are in. Now the closing verification: 3 consecutive full-suite GREEN runs (D-18) + a byte-identical psql `\l` BEFORE/AFTER snapshot to prove cleanup discipline (D-15) preserved across Phase 11.</what-built>
  <how-to-verify>
    1. **Snapshot psql BEFORE the 3-run cycle**:
       ```powershell
       docker exec sk_p-postgres-1 psql -U postgres -c "\l" > psql-before-phase11-final.txt
       Get-FileHash psql-before-phase11-final.txt -Algorithm SHA256
       ```
       (If your compose container name differs, use `docker compose -f compose.yaml ps postgres --format "{{.Name}}"` to find it.) Record the SHA-256.

       Expected baseline (from Phase 8 P08 + Phase 9 P09-03 + Phase 10 P10-05): `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`. If your BEFORE hash differs from this, investigate before proceeding (some prior phase may have left a leak).

    2. **Confirm stack is up + healthy**:
       ```powershell
       docker compose -f compose.yaml up -d --wait --timeout 120
       docker compose -f compose.yaml ps --format "{{.Service}}:{{.Status}}"
       ```
       Expected: postgres, elasticsearch, otel-collector, prometheus all Up + Healthy/Started within 120s.

    3. **3 consecutive full-suite runs**. Use `Measure-Command` (PowerShell) for wall-clock:
       ```powershell
       # Run 1
       Measure-Command { dotnet test SK_P.sln --no-restore -c Release } | Select-Object TotalSeconds
       # Run 2
       Measure-Command { dotnet test SK_P.sln --no-restore -c Release } | Select-Object TotalSeconds
       # Run 3
       Measure-Command { dotnet test SK_P.sln --no-restore -c Release } | Select-Object TotalSeconds
       ```
       Each run MUST exit 0. The test count should be stable across all 3 runs (drift indicates flakes or non-determinism). The expected count is documented in `<interfaces>` (≈142 facts; record the actual count).

       Note on Pitfall 7 OTel cold-start: Phase 8/10's documented "first run pre-existing flake" pattern (OTel collector cold-start) may bite Run 1 — that's not a Phase 11 regression but it WOULD cost a 4th run to hit 3 consecutive GREEN. Per Plans 08-08 + 09-03 + 10-05 precedent ("consecutive is the gate, not first-attempt-3"), if Run 1 fails with a transient flake fingerprint, document it inline and start the consecutive cycle from Run 2.

    4. **Snapshot psql AFTER the 3-run cycle**:
       ```powershell
       docker exec sk_p-postgres-1 psql -U postgres -c "\l" > psql-after-phase11-final.txt
       Get-FileHash psql-after-phase11-final.txt -Algorithm SHA256
       ```
       Compare to BEFORE hash — MUST match byte-identically (Phase 3 D-15 invariant: zero leaked `stepsdb_test_*` databases).

    5. **Verify no telemetry.jsonl regression** (Phase 5 D-11 → Phase 11 D-05 obsolesced):
       ```powershell
       Get-ChildItem tests/.otel-out -ErrorAction SilentlyContinue
       ```
       Expected: empty (the directory does not exist — Plan 11-05 removed it).

    6. **Verify no orphan references** anywhere:
       ```powershell
       Select-String -Path "src/**/*.cs","tests/**/*.cs" -Pattern "OtelCollectorFixture|OtelEndOfSuiteCleanup|TraceExportTests|.WithTracing|tests/.otel-out" -List
       ```
       Expected: 0 matches.

    Record in the resume-signal:
    - 3 wall-clock times (Run 1 / Run 2 / Run 3 in seconds)
    - Total fact count (e.g., 142)
    - psql `\l` SHA-256 BEFORE
    - psql `\l` SHA-256 AFTER
    - Whether they match
    - Any flake observed + which run

    Approve only when all 3 runs are GREEN consecutively + psql hashes match. (CHECKER NOTE: per WARNING #4 INFO, the resume-signal could be tightened to a strict schema in a future hardening pass; for now the freeform shape is acceptable.)
  </how-to-verify>
  <resume-signal>Type `approved — Run 1: <N>s, Run 2: <N>s, Run 3: <N>s, count: <N>, psql SHA256 match: yes` with the actual values, OR describe any flake/failure observed.</resume-signal>
</task>

<task type="auto">
  <name>Task 3: Write Phase 11 SUMMARY + update ROADMAP.md + STATE.md; commit closing narrative</name>
  <files>.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md, .planning/ROADMAP.md, .planning/STATE.md</files>
  <read_first>
    - .planning/phases/10-remove-schemaid-on-assignmententity-and-add-configschemaid-o/10-05-SUMMARY.md (the closest precedent for a closing-plan SUMMARY format)
    - .planning/ROADMAP.md Phase 11 entry (currently marked planned; needs Complete marker)
    - .planning/STATE.md (append Phase 11 milestone narrative)
    - All 10 Phase 11 SUMMARY files (11-01, 11-02, 11-03, 11-04, 11-05, 11-06, 11-07, 11-08a, 11-08b, plus the implicit OtelCollectorFixture-deletion commit from this plan's Task 1)
  </read_first>
  <action>
    Three file edits.

    **Edit 1 — create `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md`** with the closing narrative. Structure:

    ```markdown
    # Phase 11 Plan 08c — Phase Close

    **Status:** Complete
    **Date:** <today>
    **Predecessors:** Plan 11-08a (HealthEndpointsTests rebase), Plan 11-08b (3-test migration), Plan 11-07 (E2E round-trip)

    ## Summary

    Phase 11 closed with a 10-commit bisect-friendly sequence delivering:
    1. ES + Prom backends in the compose stack (Plans 11-02 + 11-04)
    2. OTel collector rewired for logs→ES + metrics→Prom (Plan 11-03)
    3. SDK strip of .WithTracing() (Plan 11-05)
    4. Test infrastructure: Phase11WebAppFactory + helpers + Wave 0 ES index name resolution (Plan 11-06)
    5. E2E round-trip facts: SchemasLogsE2ETests + SchemasMetricsE2ETests (Plan 11-07)
    6. Legacy fact migration: HealthEndpointsTests rebase + Log/LogLevel/Metrics migration (Plans 11-08a + 11-08b)
    7. Final cleanup: OtelCollectorFixture deletion + Phase close (this plan)

    The original Plan 11-08 was split into 11-08a/b/c per revision-iteration-1 checker WARNING #3 (scope_sanity — 7 tasks spanning rebase + 3 rewrites + delete + cadence + commit in a single closing plan would carry the highest quality-degradation cost in the entire phase).

    ## Commit Log (10 commits)

    | # | Plan | Commit Subject |
    |---|------|----------------|
    | 1 | 11-01 | `docs(req): amend OBSERV-12 + INFRA-06 + add OBSERV-13/14 + INFRA-08 + TEST-07 for Phase 11 shape` |
    | 2 | 11-02 | `feat(compose): add elasticsearch + prometheus services; bump otel-collector to 0.152.0; extend baseapi-service depends_on chain` |
    | 3 | 11-03 | `feat(otel-collector): rewire pipelines — logs to elasticsearch, metrics to prometheus, drop traces + file exporter` |
    | 4 | 11-04 | `feat(prometheus): add scrape config for otel-collector:8889 (verbatim from sk2_1)` |
    | 5 | 11-05 | `refactor(observability): strip .WithTracing() + delete TraceExportTests + OtelEndOfSuiteCleanup + tests/.otel-out/` |
    | 6 | 11-06 | `test(observability): add Phase11WebAppFactory + ElasticsearchTestClient + PrometheusTestClient + EsIndexNames (Wave 0)` |
    | 7 | 11-07 | `test(observability): add SchemasLogsE2ETests + SchemasMetricsE2ETests (Phase 11 D-17 round-trip)` |
    | 8 | 11-08a | `refactor(observability): rebase HealthEndpointsTests onto Phase11WebAppFactory + migrate health-OTLP-absence fact to ES polling` |
    | 9 | 11-08b | `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling` |
    | 10 | 11-08c | `chore(observability): remove OtelCollectorFixture.cs (no remaining consumers after Plans 11-08a + 11-08b)` |

    ## Wave Structure (revised at iteration 1)

    - Wave 1: 11-01 (doc-first)
    - Wave 2: 11-02 (compose.yaml), 11-03 (collector config) — 2 parallel
    - Wave 3: 11-04 (prometheus.yml + smoke restart), 11-05 (strip traces + cleanup) — 2 parallel
    - Wave 4: 11-06 (helpers + Wave 0 ES probe)
    - Wave 5: 11-07 (E2E round-trip)
    - Wave 6: 11-08a (HealthEndpointsTests rebase) → 11-08b (3-test migration) → 11-08c (deletion + close) — sequential

    Revision iteration 1 (checker feedback) moved Plan 11-04 from Wave 2 to Wave 3 (`depends_on: [11-01, 11-02]`) because Task 2's `docker compose restart prometheus` requires the prometheus service block + bind-mount declaration to exist in compose.yaml (which Plan 11-02 lands). Wave-level parallel execution would have fired 11-02 + 11-04 simultaneously with 11-04 Task 2 failing.

    ## Revision Iteration 1 Fixes Summary

    Applied per checker feedback (2 BLOCKERS + 5 WARNINGS targeted):
    - **BLOCKER #1 (dependency_correctness, Plan 11-04):** wave bumped to 3; depends_on extended to [11-01, 11-02]. ROADMAP wave breakdown updated.
    - **BLOCKER #2 (task_completeness, Plan 11-08 Task 0):** HealthEndpointsTests rebase case A pre-determined by reading the file at revision time. Plan 11-08 split into 11-08a (rebase + OTLP-absence fact migration) + 11-08b (3-test migration) + 11-08c (delete + close) — each a self-contained 2-4-task plan.
    - **WARNING #3 (scope_sanity, Plan 11-08):** addressed by the 3-way split. Each split plan targets ~30-40% context (well under the 50% ceiling).
    - **WARNING #4 (task_completeness, Plan 11-08c Task 2 resume-signal):** kept as human-verify per fix-hint (closing-phase forensic data is safest as human-verified).
    - **WARNING #5 (task_completeness, Plan 11-06 Task 1 placeholder substitution):** added explicit non-empty + non-placeholder + non-sentinel acceptance criteria to the verify gate.
    - **WARNING #6 (task_completeness, Plan 11-05 Task 3 .gitignore line-range deletion):** switched from line-range to content-match deletion; verify gate includes literal header-line negation via `grep -F`.
    - **WARNING #7 (task_completeness, Plan 11-07 Task 1 hardcoded "3.2.0"):** version-specific assertion dropped from SchemasLogsE2ETests + LogExportTests; service.name="sk-api" retained as the load-bearing assertion per D-07.

    ## Threat Model Status

    All STRIDE threats from Plans 11-01 through 11-08b are either MITIGATED with verifiable evidence in commit history OR ACCEPTED with documented rationale (dev posture per CONTEXT Out of Scope). See per-plan SUMMARY files for the cumulative threat register.

    ## Test Suite Evidence

    - Fact count: <fill from Task 2 checkpoint>
    - 3 consecutive GREEN wall-clock: Run 1: <Ns>, Run 2: <Ns>, Run 3: <Ns>
    - psql `\l` SHA-256 BEFORE: <hash>
    - psql `\l` SHA-256 AFTER:  <hash> (matches BEFORE — Phase 3 D-15 preserved)
    - `tests/.otel-out/` directory: absent (Plan 11-05 removed it; no regression)
    - Orphan reference grep (`OtelCollectorFixture|OtelEndOfSuiteCleanup|TraceExportTests|.WithTracing|tests/.otel-out`): 0 matches across `src/**/*.cs` + `tests/**/*.cs`

    ## REQ-ID Closure

    - **OBSERV-12** (traces) — superseded; row moved to Out of Scope (Plan 11-01)
    - **INFRA-06** (compose declares postgres + depends_on) — extended in place to cover ES + Prom + collector image bump (Plan 11-01)
    - **OBSERV-13** (NEW — logs in ES with Attributes.CorrelationId) — closed by Plans 11-07 + 11-08b
    - **OBSERV-14** (NEW — metrics in Prom with service_name label) — closed by Plans 11-07 + 11-08b
    - **INFRA-08** (NEW — compose ES + Prom + collector bump) — closed by Plans 11-02 + 11-04
    - **TEST-07** (NEW — E2E round-trip test class) — closed by Plan 11-07

    ## Phase 11 COMPLETE

    All success criteria from ROADMAP.md Phase 11 met. Solution builds zero-warning Release+Debug. Full test suite GREEN 3 times consecutively. psql cleanup discipline preserved byte-identically. Ready for Phase 12 (next phase in ROADMAP).
    ```

    Substitute `<today>`, `<fill from Task 2 checkpoint>`, the 3 wall-clock numbers, and the psql SHA-256 hashes with the verified Task 2 values.

    **Edit 2 — `.planning/ROADMAP.md`:**
    Update the Phase 11 entry header. Find the line:
    - BEFORE: `**Plans:** 8 plans (1 doc-first → 3 parallel-safe config edits → 1 cleanup → 1 helpers + Wave 0 → 1 E2E + 1 migration-close)`
    - AFTER:  `**Plans:** 10 plans (1 doc-first → 2 parallel-safe Wave-2 config edits → 2 parallel-safe Wave-3 [compose-config + SDK strip] → 1 helpers + Wave 0 → 1 E2E → 3-way Wave-6 close [rebase + migrate + delete]) — **COMPLETE**`

    Update the Plans bullet list — replace the 8 entries with 10 entries reflecting the revised wave structure. Mark all 10 with `- [x]` (complete). Replace the original `11-08-PLAN.md` line with three lines for `11-08a` / `11-08b` / `11-08c`.

    Update the footer marker:
    - BEFORE: `*Phase 11 planned: 2026-05-28 — 8 plans (doc-first → 3× parallel-safe config edits → SDK strip → helpers + Wave 0 → E2E round-trip + migration close)*`
    - AFTER:  `*Phase 11 planned: 2026-05-28 — 8 plans; revised iteration 1 to 10 plans (Plan 11-04 wave bump + Plan 11-08 3-way split per checker feedback); completed <today>*`

    Update the wave breakdown reference at the top of the Phase 11 entry to reflect the new structure (Wave 1 → 2 → 3 → 4 → 5 → 6 instead of Wave 1 → 2 → 3 → 4 → 5).

    **Edit 3 — `.planning/STATE.md`:**
    Append a Phase 11 milestone log entry matching the Phase 10 precedent (multi-paragraph; covers what shipped, key decisions, and forward-looking notes). Keep the entry under ~30 lines.

    Stage all 3 files + commit:
    - `git add .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md`
    - `git add .planning/ROADMAP.md`
    - `git add .planning/STATE.md`

    Create commit B with the exact message:
    ```
    docs(phase-11): close phase — 10-commit sequence + revision-iteration-1 fixes summary
    ```

    Use a HEREDOC. Verify `git status --porcelain` returns empty post-commit. Do NOT push.
  </action>
  <verify>
    <automated>test -f .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md exits 0; grep "Phase 11 COMPLETE" .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md returns 1; grep "10 plans" .planning/ROADMAP.md returns at least 1; grep "11-08a" .planning/ROADMAP.md returns at least 1; grep "11-08b" .planning/ROADMAP.md returns at least 1; grep "11-08c" .planning/ROADMAP.md returns at least 1; grep -i "phase 11" .planning/STATE.md returns at least 1 (new milestone entry); git log -1 --format=%s returns "docs(phase-11): close phase — 10-commit sequence + revision-iteration-1 fixes summary"; git show --stat HEAD lists exactly 3 files (SUMMARY + ROADMAP + STATE); git status --porcelain returns empty</automated>
  </verify>
  <done>Phase 11 SUMMARY narrative committed; ROADMAP.md reflects 10-plan revised structure with Phase 11 marked COMPLETE; STATE.md milestone log appended; Phase 11 formally closed.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| (no new) | This plan deletes files + writes docs. No new code paths; no new attack surface. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-11-08c-T1 (premature OtelCollectorFixture deletion breaks build if a consumer was missed) | A (Availability — build failure) | tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs | mitigate | Task 1 includes a defensive grep BEFORE the `git rm` step. If any file outside OtelCollectorFixture.cs itself references the type, the grep returns non-empty and the planner STOPS. Plus, post-deletion `dotnet build` exits 0 zero-warning gates the commit. **Verify:** Task 1 verify gate confirms zero references + zero build warnings. |
| T-11-08c-T2 (3-run cadence masks a flake by retrying past it) | T (Test correctness — silent regression) | Task 2 checkpoint | mitigate | Per Plans 08-08 + 09-03 + 10-05 precedent, the rule is "3 CONSECUTIVE green runs, not 3 of 4". If any of the 3 runs is RED, the cadence resets. Resume-signal records each run's wall-clock + the actual fact count — drift between runs (count changes) is a flag. **Verify:** Task 2 checkpoint's `approved` resume signal requires explicit 3-of-3 GREEN. |
| T-11-08c-T3 (psql `\l` cleanup-discipline regression — leaked stepsdb_test_* databases) | A (Availability — local-dev cleanliness) | PostgresFixture lifecycle | mitigate | Task 2 SHA-256 BEFORE/AFTER snapshot proves zero leak. Phase11WebAppFactory inherits Phase8WebAppFactory which inherits Phase 3 PostgresFixture's DROP WITH FORCE on dispose. **Verify:** Task 2 checkpoint approval gates Task 3 commit. |
| T-11-08c-T4 (SUMMARY narrative loses fidelity to actual revision history) | I (Documentation drift) | Task 3 SUMMARY content | accept | Task 3 substitutes verified Task 2 values (wall-clock, fact count, SHA-256 hashes) directly. Per-plan SUMMARY files retain the canonical commit-level evidence. **Verify:** Task 3 verify gate confirms the SUMMARY contains the closing-plan markers. |
</threat_model>

<verification>
- `! test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (deleted).
- `! grep -rn "OtelCollectorFixture" tests/ src/ --include="*.cs"` (zero references).
- `dotnet build SK_P.sln -c Release --no-restore` exits 0 zero-warning.
- Task 2 checkpoint approved: 3 consecutive GREEN `dotnet test` runs + psql `\l` SHA-256 BEFORE = AFTER.
- `test -f .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md` exits 0.
- `grep "Phase 11 COMPLETE" .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md` returns 1.
- `grep "10 plans" .planning/ROADMAP.md` returns at least 1.
- `grep -E "11-08[abc]" .planning/ROADMAP.md` returns at least 3.
- `git log --oneline -10` shows the full 10-commit Phase 11 sequence intact.
- `git status --porcelain` returns empty post-commit.
</verification>

<success_criteria>
1. `OtelCollectorFixture.cs` deleted via `git rm`; zero remaining references anywhere in `src/` or `tests/`.
2. Solution builds zero-warning Release+Debug post-deletion.
3. Task 2 checkpoint approved: 3 consecutive GREEN `dotnet test SK_P.sln --no-restore -c Release` runs at stable fact count; psql `\l` SHA-256 BEFORE/AFTER matches byte-identically.
4. `11-08c-SUMMARY.md` written with the 10-commit closing narrative + revision-iteration-1 fixes summary.
5. ROADMAP.md updated: Phase 11 marked COMPLETE; Plan list reflects 10 plans (including 11-08a/b/c split); wave breakdown reflects 6 waves.
6. STATE.md appended with Phase 11 milestone narrative.
7. Two atomic commits at HEAD: `chore(observability): remove OtelCollectorFixture.cs ...` (HEAD-1) + `docs(phase-11): close phase ...` (HEAD).
8. Full Phase 11 10-commit sequence intact in `git log --oneline -10`.
9. Phase 11 COMPLETE.
</success_criteria>

<output>
After completion, `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08c-SUMMARY.md` IS the SUMMARY for this plan (created in Task 3). No additional file generation needed.
</output>
</content>
