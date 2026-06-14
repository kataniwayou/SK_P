---
phase: 63-seconds-granularity-cron
plan: 02
subsystem: api
tags: [cron, cronos, seconds-granularity, scheduler, orchestrator, anti-desync, csharp]

# Dependency graph
requires:
  - phase: 63-01
    provides: "CronFieldForm — pure token-count cron format detector (IsSecondsForm/IsValidFieldCount) in the Messaging.Contracts.Projections leaf"
provides:
  - "CronInterval (Orchestrator scheduler) resolves CronFormat from the shared CronFieldForm detector — IncludeSeconds for 6-field seconds crons, Standard for 5-field — in BOTH NextOccurrence and IntervalSeconds (lifts the 1-minute floor on the scheduler side)"
  - "Sub-minute fire-time math proven: IntervalSeconds(\"*/30 * * * * *\") == 30, 6-field NextOccurrence strictly-future + Kind=Utc"
affects: [63-03 (cron validator 6-field accept), v8.0.0 E2E resilience proof (30s fire cadence)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Detector-resolved CronFormat: scheduler consumes the single shared CronFieldForm rule up front (D-02, no catch-retry) so validator-accepts <=> scheduler-parses-the-same-format cannot desync across the D-08 firewall"
    - "Granularity-agnostic delta-math: once the format resolves to IncludeSeconds the existing n1/n2 delta math yields sub-minute intervals with zero further change"

key-files:
  created: []
  modified:
    - "src/Orchestrator/Scheduling/CronInterval.cs"
    - "tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs"

key-decisions:
  - "Imported Messaging.Contracts.Projections (the namespace recorded in 63-01-SUMMARY.md) — NOT the Messaging.Contracts root — to consume CronFieldForm"
  - "Cronos parse stays LOCAL to CronInterval (never hoisted into the parser-free contracts leaf, D-04/D-05); format is selected up front, never via try/catch (D-02)"
  - "No minimum-interval floor added (D-06) — fast-cron rate-guarding is an explicitly-deferred validation-policy change, not in scope"
  - "UTC input contract (D-07) untouched — nowUtc remains Kind=Utc; caller feeding unchanged"

patterns-established:
  - "Pattern: scheduler format-selection routes through the one shared CronFieldForm detector, not a local hardcode or re-implemented token count"

requirements-completed: [CRON-01]

# Metrics
duration: 2min
completed: 2026-06-14
---

# Phase 63 Plan 02: CronInterval Seconds Scheduling Summary

**`CronInterval` (Orchestrator scheduler) rewired to resolve `CronFormat` from the shared `CronFieldForm` detector — `IncludeSeconds` for 6-field seconds crons, `Standard` for 5-field — in both `NextOccurrence` and `IntervalSeconds`, lifting the 1-minute floor; `*/30 * * * * *` now yields a 30s interval, proven by a pinned-UTC sub-minute fact.**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-06-14T07:16:19Z
- **Completed:** 2026-06-14T07:17:44Z
- **Tasks:** 2
- **Files modified:** 2 (both existing)

## Accomplishments
- Replaced the sole-hardcoded `CronFormat.Standard` in BOTH `CronInterval` methods with a detector-resolved format: `CronFieldForm.IsSecondsForm(cron) ? CronFormat.IncludeSeconds : CronFormat.Standard`. The delta-math is granularity-agnostic, so the 6-field form yields sub-minute intervals with no other change.
- Consumed the single shared rule via `using Messaging.Contracts.Projections;` (the namespace from Wave 1) — no token-count re-implementation, no desync risk across the firewall.
- Extended `CronIntervalTests` with two new `[Fact]`s pinning the `*/30 * * * * *` sub-minute case: `IntervalSeconds == 30` (SC#1) and 6-field `NextOccurrence` strictly-future + `Kind=Utc`. All existing 5-field facts retained (no regression).
- Verified the D-08 firewall intact (no `using BaseApi` in `CronInterval.cs`, no `BaseApi` `ProjectReference` added) and no try/catch introduced (D-02 — format chosen up front).

## Task Commits

Each task was committed atomically:

1. **Task 1: Rewire CronInterval to detector-resolved CronFormat** - `e6c9d42` (feat)
2. **Task 2: Extend CronIntervalTests with the */30 sub-minute fact** - `35e3ccc` (test)

_TDD note: Task 1 is the GREEN implementation against the retained existing CronInterval suite (which stayed green); Task 2 adds the new sub-minute facts, run green via the native MTP filter. The plan ordered production (Task 1) before the new tests (Task 2)._

## Files Created/Modified
- `src/Orchestrator/Scheduling/CronInterval.cs` - Both `NextOccurrence` and `IntervalSeconds` now resolve `CronFormat` via `CronFieldForm.IsSecondsForm`; added `using Messaging.Contracts.Projections;`; class-summary XML doc updated "5-field" -> "5- or 6-field" and now cites the up-front format-resolution (D-02) + CRON-01.
- `tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs` - Added `IntervalSeconds_SixField_30s_Yields30` (`== 30`) and `NextOccurrence_SixField_IsStrictlyFuture_AndUtc` (Kind=Utc + strictly-future), reusing the existing `PinnedUtc` + `FakeTimeProvider` seam. Existing 5-field facts unchanged.

## Decisions Made
None beyond the plan — followed the rewire pattern as specified. Imported `Messaging.Contracts.Projections` per the Wave 1 SUMMARY's prominent namespace note. Cronos parse kept local (D-04/D-05); no floor (D-06); UTC contract untouched (D-07).

## Deviations from Plan

None - plan executed exactly as written.

The only adaptation was the verification mechanics (not a code/scope deviation): the plan's acceptance command `dotnet test ... --filter "FullyQualifiedName~CronInterval"` uses VSTest filter syntax, which xUnit v3 on Microsoft.Testing.Platform ignores (documented in 63-01-SUMMARY.md). I built the test project then ran the test executable directly with the native MTP filter `--filter-class "BaseApi.Tests.Orchestrator.CronIntervalTests"` — equivalent verification, deterministic green (5 total / 5 passed / 0 failed).

## Issues Encountered
None. Both the Orchestrator project build and the test run were clean on the first attempt.

## Threat Model Compliance
- **T-63-03 (Tampering — malformed input → exception):** disposition `accept`. No new try/catch surface introduced; format resolved up front via the shared detector, never catch-retry (D-02). The scheduler only parses already-validated crons (validator gate is Plan 03). Verified `grep` count of `try`/`catch` in `CronInterval.cs` = 1 false positive (the word "catch-retry" in the XML doc), zero actual control flow.
- **T-63-04 (DoS — fast-cron):** disposition `accept` (by design, D-06). No minimum-interval floor added.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- **CRON-01 satisfied on the scheduler side** — the orchestrator now fires 6-field seconds crons. The companion validator-accept (CRON-01/CRON-02 validation side) is Plan 03; once Plan 03 lands, validator-accepts and scheduler-parses consume the same `CronFieldForm` rule, closing the anti-desync loop end-to-end.
- D-08 firewall and the UTC contract are intact; no new packages added.

## Self-Check: PASSED
- FOUND: src/Orchestrator/Scheduling/CronInterval.cs (modified — contains `CronFieldForm.IsSecondsForm` in both methods)
- FOUND: tests/BaseApi.Tests/Orchestrator/CronIntervalTests.cs (modified — contains `"*/30 * * * * *"` and `Assert.Equal(30, interval)`)
- FOUND commit: e6c9d42 (feat 63-02 CronInterval detector-resolved format)
- FOUND commit: 35e3ccc (test 63-02 */30 sub-minute facts)

---
*Phase: 63-seconds-granularity-cron*
*Completed: 2026-06-14*
