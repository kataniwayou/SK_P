---
phase: 63-seconds-granularity-cron
plan: 01
subsystem: api
tags: [cron, messaging-contracts, seconds-granularity, anti-desync, csharp]

# Dependency graph
requires:
  - phase: 21-v3.4.0-closeout-hygiene
    provides: "L2ProjectionKeys anti-desync hoist precedent (shared static class in the Messaging.Contracts leaf consumed by writer+reader)"
provides:
  - "CronFieldForm — pure token-count cron format detector (IsSecondsForm, IsValidFieldCount) in the Messaging.Contracts leaf"
  - "The single shared rule that Wave 2 call sites (CronInterval scheduler + the two validators) consume so validator-accepts <=> scheduler-parses-the-same-format cannot desync"
affects: [63-02 (CronInterval seconds scheduling), 63-03 (cron validator 6-field accept)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Anti-desync hoist: a single pure detection rule lives in the Messaging.Contracts leaf so both sides of a firewall consume ONE shape (mirrors L2ProjectionKeys, Phase 21)"
    - "Parser-free contracts leaf: detection (token count) is separated from parse (Cronos) — the contracts leaf stays Cronos-free"

key-files:
  created:
    - "src/Messaging.Contracts/CronFieldForm.cs"
    - "tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs"
  modified: []

key-decisions:
  - "CronFieldForm landed in namespace Messaging.Contracts.Projections (same as the L2ProjectionKeys precedent) — Wave 2 MUST import Messaging.Contracts.Projections"
  - "Detector is pure System string logic (Split(null, RemoveEmptyEntries) + IsNullOrWhiteSpace) — NO Cronos dependency added; Messaging.Contracts.csproj unchanged"
  - "6 tokens => seconds form; 5 tokens => standard form; any other count => invalid (rejected before any Cronos parse)"

patterns-established:
  - "Pattern: cron field-count -> format selection is one shared static rule, not duplicated at each call site"

requirements-completed: []  # CRON-01/CRON-02 are end-to-end (firing + validation) and only fully satisfied once Wave 2 (Plans 02/03) wire the call sites. This plan provides the shared hinge only — NOT marked complete here.

# Metrics
duration: 29min
completed: 2026-06-14
---

# Phase 63 Plan 01: Cron Field-Form Detector Summary

**Pure token-count cron format detector `CronFieldForm` (IsSecondsForm / IsValidFieldCount) hoisted into the Messaging.Contracts leaf — the single shared rule both the scheduler and the validators consume so 5-field-vs-6-field selection can never desynchronize across the firewall.**

> **NAMESPACE FOR WAVE 2:** `CronFieldForm` is in **`Messaging.Contracts.Projections`** (NOT the `Messaging.Contracts` root). Plans 02 and 03 must `using Messaging.Contracts.Projections;` to consume `CronFieldForm.IsSecondsForm` / `CronFieldForm.IsValidFieldCount`.

## Performance

- **Duration:** ~29 min
- **Started:** 2026-06-14T06:43:39Z
- **Completed:** 2026-06-14T07:12:37Z
- **Tasks:** 2
- **Files modified:** 2 (both new)

## Accomplishments
- Created `CronFieldForm` — a `public static class` in `Messaging.Contracts.Projections` mirroring the `L2ProjectionKeys` anti-desync precedent: pure `System` string logic, zero Cronos dependency.
- `IsSecondsForm(expr)` returns true for 6-token expressions (map to `CronFormat.IncludeSeconds`); `IsValidFieldCount(expr)` returns true for 5- or 6-token; whitespace-/null-/blank-robust via `Split((char[]?)null, RemoveEmptyEntries)` + `IsNullOrWhiteSpace`.
- Pinned the one shared rule (D-10) with a `[Theory]/[InlineData]` test suite (8 cases) covering 6->seconds, 5->standard, other->invalid, and whitespace robustness — all green.
- Confirmed the contracts leaf stays parser-free: no `using Cronos` in the new file, no `Cronos` PackageReference in `Messaging.Contracts.csproj` (unchanged).

## Task Commits

Each task was committed atomically:

1. **Task 1: Create the pure CronFieldForm detector** - `0e48293` (feat)
2. **Task 2: Add CronFieldForm detector unit test (D-10)** - `ee2b12d` (test)

_TDD note: the plan ordered the production file (Task 1) before the test (Task 2). Task 2's first run surfaced a defect in the plan's own test data (see Deviations), caught RED, fixed, then GREEN — the test commit pins the corrected rule._

## Files Created/Modified
- `src/Messaging.Contracts/CronFieldForm.cs` - Pure token-count cron format detector; `IsSecondsForm` (==6), `IsValidFieldCount` (5 or 6), private `FieldCount` (null/blank -> 0, else whitespace-collapsed token count). XML doc cites D-01..D-05.
- `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs` - `public sealed class CronFieldFormTests`, `[Trait("Phase","63")]`, `using Messaging.Contracts.Projections;`; 4 theories / 8 inline cases pinning D-10.

## Decisions Made
- **Namespace = `Messaging.Contracts.Projections`** (the plan's primary recommendation, matching the `L2ProjectionKeys` precedent that already lives there). Recorded prominently above so Wave 2 wires the correct import.
- Kept the detector to `IsSecondsForm` + `IsValidFieldCount` only — no Cronos parse, no `CronFormat` reference — preserving the parser-free contracts leaf (D-03/D-05).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Corrected a malformed whitespace InlineData in the plan's test table**
- **Found during:** Task 2 (CronFieldForm detector unit test)
- **Issue:** The plan's whitespace-robustness case `" */30   *  * * * "` was annotated "still 6 tokens" but actually tokenizes to only **5** tokens (`*/30 * * * *` — one `*` short). Running it RED-failed `IsSecondsForm_True_For_SixToken` (expected True, got False) because the production detector correctly counted 5. The production code was correct; the test literal was the bug.
- **Fix:** Changed the InlineData to `" */30   *  *   * * * "` (genuine 6 tokens with irregular/leading/trailing/multiple spacing), preserving the whitespace-robustness intent the plan described.
- **Files modified:** `tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs`
- **Verification:** `BaseApi.Tests.exe --filter-class BaseApi.Tests.Contracts.CronFieldFormTests` -> 8 total, 8 passed, 0 failed.
- **Committed in:** `ee2b12d` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug in plan-supplied test data)
**Impact on plan:** The fix tightened the test to genuinely exercise 6-token whitespace robustness as the plan intended. No production-code change, no scope creep — the detector shipped exactly as specified.

## Issues Encountered
- **Test-runner environment churn (resolved):** the test project uses xUnit v3 on Microsoft.Testing.Platform, where `dotnet test --filter` (VSTest syntax) is ignored (MTP0001) and overlapping `dotnet test` invocations collided on the locked `BaseApi.Tests.exe` / `TestResults` log (IOException, MSB3027). Resolved by killing the stale `dotnet`/`BaseApi.Tests` processes, rebuilding clean, and running the test executable directly with the native MTP filter `--filter-class "BaseApi.Tests.Contracts.CronFieldFormTests"` — fast, isolated, deterministic green. No product code was affected.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- **Wave 2 unblocked.** Plans 02 (CronInterval seconds scheduling) and 03 (cron validator 6-field accept) can now consume the single shared rule. **Import `Messaging.Contracts.Projections` and call `CronFieldForm.IsSecondsForm` / `CronFieldForm.IsValidFieldCount`** — do not re-implement token counting.
- CRON-01 / CRON-02 remain `Pending` until Wave 2 wires firing + validation; this plan delivered only the anti-desync hinge.

## Self-Check: PASSED
- FOUND: src/Messaging.Contracts/CronFieldForm.cs
- FOUND: tests/BaseApi.Tests/Contracts/CronFieldFormTests.cs
- FOUND commit: 0e48293 (feat 63-01 CronFieldForm)
- FOUND commit: ee2b12d (test 63-01 detector)

---
*Phase: 63-seconds-granularity-cron*
*Completed: 2026-06-14*
