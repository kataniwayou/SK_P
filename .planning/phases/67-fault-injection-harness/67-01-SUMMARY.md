---
phase: 67-fault-injection-harness
plan: 01
subsystem: testing
tags: [analyzer, env-var-seam, fault-injection, observability, xunit, dotnet]

# Dependency graph
requires:
  - phase: 66-analyzer
    provides: "AnalyzerE2ETests.cs RealStack fixture + PassFailEngine + BuildStepSearchBody (the analyzer the harness invokes)"
provides:
  - "D-16 env-var seam in AnalyzerE2ETests.cs: SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC reads with const/UtcNow fallback"
  - "ParseUtcOr helper (round-trip 'o'-format → UTC, UtcNow fallback on null/empty/malformed)"
  - "Scenario-id whitelist guard (^[A-Za-z0-9_-]+$) now validating the env-supplied SCENARIO_ID (T-67-03 mitigated)"
affects: [67-02-harness, 67-03-live-proof, 68-live-resilience-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Read-with-fallback env seam (?? const / ParseUtcOr UtcNow) — test fixture honors harness-supplied values, byte-identical defaults when unset"
    - "Whitelist-before-path-compose guard reused for env-supplied identifiers (path-traversal mitigation across a new trust boundary)"

key-files:
  created:
    - .planning/phases/67-fault-injection-harness/67-01-SUMMARY.md
  modified:
    - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs

key-decisions:
  - "Task-2 checkpoint resolved BY CONSTRUCTION (user decision): accept the seam on the proven fallback + verified diff scope; defer the full live analyzer-GREEN verdict to plan 67-03, because reproducing it needs the 67-02 harness + a clean orchestrator that does not yet exist."
  - "AssumeUniversal|AdjustToUniversal on ParseUtcOr so PowerShell 'o'-format timestamps normalize to UTC; TryParse→false degrades to UtcNow (T-67-04 accepted, no crash)."

patterns-established:
  - "Harness/fixture env seam: three RHS substitutions only; BuildStepSearchBody + PassFailEngine left untouched so Phase 66 stays the single scoring source of truth."

requirements-completed: [FAULT-01, FAULT-03]

# Metrics
duration: ~1 day (context + spec + plan + execute, multi-session)
completed: 2026-06-14
---

# Phase 67 Plan 01: D-16 Analyzer Env-Var Seam Summary

**Test-only env-var seam (SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC) added to the Phase 66 analyzer fixture with const/UtcNow fallback, so the Phase 67 harness can drive per-scenario report names and the harness-recorded 5-minute window — fallback proven live; full standalone analyzer-GREEN verdict deferred to plan 67-03.**

## Performance

- **Duration:** ~1 day across multiple sessions (research → patterns → plan → execute)
- **Tasks:** 1 of 2 executed to completion (Task 1); Task 2 was a checkpoint resolved by construction
- **Files modified:** 1 product/test file (+1 summary, +2 tracking docs)

## Accomplishments

- Added the D-16 env-var seam to `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` with exactly three RHS substitutions:
  - `scenarioId` reads `SCENARIO_ID` with `?? DefaultScenarioId` fallback (whitelist guard kept at the same site).
  - `windowStartUtc` reads `WINDOW_START_UTC` via `ParseUtcOr(..., UtcNow)`.
  - `snapshotUtc` reads `WINDOW_END_UTC` via `ParseUtcOr(..., UtcNow)`.
- Introduced the `ParseUtcOr` private static helper (round-trip parse → UTC, falls back to the caller's default on null/empty/malformed input).
- Preserved the `ScenarioIdPattern` whitelist guard (`^[A-Za-z0-9_-]+$`) — it now validates the env-supplied SCENARIO_ID before any path is composed (T-67-03 mitigated).
- Left `BuildStepSearchBody` and `PassFailEngine` untouched — Phase 66 remains the single source of truth for cohort range interpolation and scoring.
- Verified the test project builds clean: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -warnaserror` exits 0 (0 warnings).

## Task Commits

1. **Task 1: Add SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC env-var seam with const/UtcNow fallback (D-16)** — `55b2fef` (test)
2. **Task 2: Verify Phase 66 standalone analyzer stays green (defaults fallback)** — checkpoint (`checkpoint:human-verify`), resolved BY CONSTRUCTION; no live run performed (see Deviations).

**Plan metadata:** _(this commit)_ (docs: complete plan)

## Files Created/Modified

- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — Three env reads + `ParseUtcOr` helper; whitelist guard + `BuildStepSearchBody` + `PassFailEngine` unchanged. (commit `55b2fef`, +17/-3)

## Decisions Made

- **Task-2 checkpoint resolved by construction (user decision).** The user accepted the seam on the strength of the proven fallback path + the verified diff scope, and explicitly deferred the full live analyzer-GREEN verdict to plan 67-03. Rationale: reproducing a green verdict requires the 67-02 harness and a clean orchestrator that do not yet exist; attempting it now in an uncontrolled environment would prove nothing about the seam.
- **ParseUtcOr normalization.** `DateTimeStyles.AssumeUniversal | AdjustToUniversal` chosen so the harness's PowerShell `Get-Date -Format o` / `.ToString("o")` round-trip timestamps normalize to UTC; `TryParse → false` degrades to `UtcNow` so the no-env standalone path is byte-for-byte unchanged (T-67-04 accepted).

## Deviations from Plan

The plan's Task 2 was a blocking `checkpoint:human-verify` ("standalone Phase 66 analyzer stays green with fallback defaults"). It was **not executed as a live run** — it was resolved by construction per the user decision above. This is a process deviation (checkpoint disposition), not an auto-fix. The following findings were surfaced while attempting live verification and are recorded here so plans 67-02 and 67-03 inherit them.

### Checkpoint disposition

**Task 2 — live verification DEFERRED, not achieved.**
- **What was attempted:** A live analyzer run against the running stack with no harness env vars set.
- **What WAS proven:** The seam's **fallback path**. With no env vars set, the live analyzer run wrote `analyzer-reports/TEST-01.json` — the const `DefaultScenarioId` default — at the correct fixed reports directory. This confirms the fallback chooses the const (not an env value) and the report path/name behavior is intact.
- **What was NOT proven:** The live analyzer **VERDICT did not go green** in this environment. Root cause is environmental, NOT the seam (see findings below).
- **Disposition:** The full standalone analyzer-GREEN proof is **deferred to plan 67-03** (live proof under a controlled window), which depends on the 67-02 harness + a clean orchestrator.

### Environmental findings handed to 67-02 / 67-03 (NOT seam defects)

1. **Microsoft.Testing.Platform (MTP) silently ignores `dotnet test --filter`.** `--filter` is VSTest syntax; MTP requires `-- --filter-method "*AnalyzerE2ETests*"` for test isolation. The plan's `<how-to-verify>` step 3 (`--filter "Category=RealStack&FullyQualifiedName~Analyzer"`) does not isolate the fixture under MTP. **→ 67-02 harness must use `-- --filter-method`.**
2. **`phase-65-reset` does not stop ghost orchestrator crons.** The long-running orchestrator keeps firing dozens of stale/ghost workflow crons whose DB assignment rows were deleted by the reset; they dispatch NULL payloads and emit no `Step_*` labels → Elasticsearch has zero `Step_*` docs in any window → the analyzer scored 66 triggered / 0 complete / all-missing. **→ 67-02 must guarantee a clean orchestrator (no stale crons) before the observation window.**
3. **`POST /api/v1/orchestration/start` returned 422 for the freshly-seeded workflow**, so the good workflow never registered/activated. **→ 67-02/67-03 must investigate the 422 (likely seeding/gate interaction) so the good workflow actually starts.**

**Total deviations:** 1 checkpoint disposition (deferred live verification) + 3 inherited environmental findings. No code auto-fixes. No scope creep — the seam edit is exactly the planned ~17-line test-only change.

## Issues Encountered

- See the three environmental findings above. None are defects in the Task 1 seam; all are environment/orchestration issues that block a live green verdict and are explicitly deferred to the harness (67-02) and live-proof (67-03) plans.

## User Setup Required

None — no external service configuration required for this plan.

## Next Phase Readiness

- **Ready:** The D-16 seam is in place and Release-clean, so 67-02 (harness) can now set `SCENARIO_ID` / `WINDOW_START_UTC` / `WINDOW_END_UTC` and get a correctly-named, correctly-windowed report.
- **Carried forward to 67-02:** Use `-- --filter-method` (not `--filter`); guarantee a clean orchestrator with no ghost crons; resolve the `/orchestration/start` 422.
- **Carried forward to 67-03:** The standalone analyzer-GREEN verdict (Task 2's original intent) is owed here, under the controlled harness window.

## Self-Check

- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — FOUND (seam present: `GetEnvironmentVariable("SCENARIO_ID")` :88, `WINDOW_START_UTC` :107, `WINDOW_END_UTC` :122, `ParseUtcOr` :75, `ScenarioIdPattern.IsMatch` :94).
- Commit `55b2fef` — FOUND in git log (`test(67-01): add D-16 env-var seam to analyzer fixture`).
- Task 1 build verification (Release -warnaserror exit 0) — PASSED (per orchestrator self-verification).

### Self-Check: PASSED (with deferred verification noted)

Task 1 claims (seam present, build clean, commit exists) are verified. **The plan-level live analyzer-GREEN claim was intentionally NOT made** — Task 2's live verification is deferred to plan 67-03, as documented under Deviations. This self-check does NOT assert a live green run occurred.

---
*Phase: 67-fault-injection-harness*
*Completed: 2026-06-14*
