---
phase: 63-seconds-granularity-cron
plan: 03
subsystem: api
tags: [cron, validation, fluentvalidation, seconds-granularity, anti-desync, csharp]

# Dependency graph
requires:
  - phase: 63-seconds-granularity-cron
    plan: 01
    provides: "CronFieldForm (IsSecondsForm / IsValidFieldCount) in Messaging.Contracts.Projections — the single shared cron field-count → format rule"
provides:
  - "WorkflowCreateDtoValidator + WorkflowUpdateDtoValidator accept the 6-field seconds form (*/30 * * * * *) while still accepting 5-field and rejecting malformed/wrong-count — CRON-02 (validation side)"
  - "WorkflowCronValidatorTests pinning 5/6-field accept + malformed/wrong-count reject against BOTH validators"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Anti-desync consume: both duplicated validators route through the ONE shared CronFieldForm rule (D-04) so validator-accepts ⟺ scheduler-parses-the-same-format cannot desync"
    - "Field-count gate before parse: reject non-5/6 up front via IsValidFieldCount WITHOUT throwing (D-02), then ONE guarded CronExpression.Parse(expr, format)"

key-files:
  created:
    - "tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs"
  modified:
    - "src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs"

key-decisions:
  - "Kept the two BeValidStandardCron methods duplicated (one per validator) rather than collapsing into a shared helper — the plan offered both; in-place edits keep the change minimal, both messages and both bodies are explicitly updated (Pitfall 3)"
  - "Resolved format with CronFieldForm.IsSecondsForm ? CronFormat.IncludeSeconds : CronFormat.Standard; non-5/6 rejected before any parse (no exception-as-control-flow, D-02); genuinely-malformed 5/6-token still rejected via CronFormatException"
  - "No minimum-interval / sub-second rate floor added (D-06) — accepting a 1s cron is a deliberate product decision this milestone"

patterns-established:
  - "Pattern: both byte-identical Create/Update cron validators consume the single shared field-form rule and carry the same accurate user-facing message"

requirements-completed: [CRON-02]

# Metrics
duration: 2min
completed: 2026-06-14
---

# Phase 63 Plan 03: Cron Validator 6-Field Accept Summary

**Both `WorkflowCreateDtoValidator` and `WorkflowUpdateDtoValidator` now lift the 1-minute floor on the validation side: `BeValidStandardCron` routes through the shared `CronFieldForm` detector — rejecting non-5/6 field counts up front without an exception (D-02), then doing one guarded `CronExpression.Parse(expr, format)` with the resolved `CronFormat` — so a 6-field `*/30 * * * * *` is accepted, 5-field still validates, and malformed/wrong-count is still rejected. Both user-facing messages updated to "5- or 6-field" (D-11). CRON-02 (validation side) satisfied.**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-06-14T07:20:26Z
- **Completed:** 2026-06-14T07:22:31Z
- **Tasks:** 2
- **Files modified:** 2 (1 product modified, 1 test created)

## Accomplishments
- Added `using Messaging.Contracts.Projections;` and rewired `BeValidStandardCron` in BOTH validators (byte-identical bodies, Pitfall 3): null/blank stays valid (ENTITY-08) → `IsValidFieldCount` rejects non-5/6 up front (D-02, no exception-as-control-flow) → `IsSecondsForm` resolves `CronFormat.IncludeSeconds` vs `CronFormat.Standard` → one guarded `CronExpression.Parse(expr, format)` (genuinely-malformed 5/6-token still rejects via `CronFormatException`).
- Updated BOTH `.WithMessage(...)` strings (D-11) from "valid 5-field cron expression (e.g., '0 0 * * *')." to "valid 5- or 6-field cron expression (e.g., '0 0 * * *' or '*/30 * * * * *')." — so a rejected user sees the accurate accepted set (mitigates T-63-07).
- Added `WorkflowCronValidatorTests` — 4 `[Theory]`/8 inline cases pinning, against BOTH Create and Update validators: 5-field accept (SC#3 regression), 6-field accept (SC#2), malformed reject, wrong-field-count reject (D-09). All 8 GREEN.
- Confirmed NO minimum-interval / rate floor added (D-06) and NO new package (Cronos already referenced by BaseApi.Service).

## Task Commits

Each task was committed atomically:

1. **Task 1: Rewire both validators' BeValidStandardCron + update both messages** - `4fec1a6` (feat)
2. **Task 2: Add WorkflowCronValidatorTests for Create + Update** - `34a9059` (test)

_TDD note: the plan ordered the production rewire (Task 1) before the tests (Task 2). With the rewire already in place, Task 2's tests went straight to GREEN (8/8) — confirming the wiring rather than driving it. No RED iteration was needed because the corrected shared rule (pinned RED-first in Plan 01) was already proven._

## Files Created/Modified
- `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs` — both `BeValidStandardCron` bodies route through `CronFieldForm` (`IsValidFieldCount` up-front reject, `IsSecondsForm` → `CronFormat`, one guarded `Parse(expr, format)`); both `.WithMessage` strings say "5- or 6-field"; XML docs updated to cite D-02/D-11/CRON-02.
- `tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs` — `public sealed class`, `[Trait("Phase","63")]`, factory helpers producing a VALID baseline DTO (strict-SemVer Version + one non-empty EntryStepIds) so CronExpression is the only failing variable; accept/reject theories against BOTH validators.

## Decisions Made
- **Kept the duplication** (one `BeValidStandardCron` per validator) instead of collapsing into a shared private helper. The plan explicitly allowed either; in-place edits keep the diff minimal and make both bodies + both messages visibly updated (defends Pitfall 3 directly).
- **Field-count gate precedes parse** — `IsValidFieldCount(expr)` returns the reject branch before any `CronExpression.Parse`, so a wrong-count string (e.g. `* * *`) is rejected without an exception (D-02); only a 5/6-token-but-malformed string reaches the guarded parse and rejects via `CronFormatException`.
- **No interval floor** (D-06) — a 1s cron remains accepted by design (T-63-06 accepted/out-of-scope).

## Deviations from Plan

None — plan executed as written.

_Note on the acceptance-criterion grep: the plan stated `grep for "5- or 6-field" returns 2 matches`. The actual file has 4 matches — 2 in the user-facing `.WithMessage(...)` strings (the load-bearing ones) and 2 in the new XML doc comments. The intent (both user-facing messages updated, zero stale "valid 5-field cron") is fully satisfied: `WithMessage.*5- or 6-field` = 2, `valid 5-field cron` = 0. Not a behavioral deviation._

## Threat Surface
- T-63-05 (Tampering / malformed input) — **mitigated**: `IsValidFieldCount` rejects non-5/6 token strings up front without throwing; the single guarded `CronExpression.Parse(expr, format)` rejects genuinely-malformed expressions → 422/400. Cron is parsed by Cronos, never eval'd/shelled (no injection surface). Both validators wired AND tested.
- T-63-07 (stale-message UX) — **mitigated**: both messages updated (D-11).
- T-63-06 (fast-cron DoS) — **accepted** (D-06, by design this milestone).
- No new security surface introduced beyond the plan's threat model.

## Verification
- `dotnet build src/BaseApi.Service/BaseApi.Service.csproj -c Debug` → Build succeeded, 0 Warning(s), 0 Error(s).
- `BaseApi.Tests.exe --filter-class "BaseApi.Tests.Validation.WorkflowCronValidatorTests"` (MTP native filter — xUnit v3 on Microsoft.Testing.Platform; VSTest `--filter` is ignored, MTP0001) → total 8, passed 8, failed 0.
- grep: `WithMessage.*5- or 6-field` = 2; `valid 5-field cron` = 0; `CronExpression.Parse(expr, format)` = 2; no `Must`/`When` interval-threshold rule.

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- **CRON-02 satisfied** on both the scheduler side (Plan 02) and the validation side (Plan 03). The anti-desync hinge (Plan 01's `CronFieldForm`) is now consumed by both call sites; validator-accepts ⟺ scheduler-parses-the-same-format holds.
- Wave 2 (Plans 02 + 03) complete — phase 63 seconds-granularity cron is wired end-to-end (6-field fires + 6-field validates).

## Self-Check: PASSED
- FOUND: src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs
- FOUND: tests/BaseApi.Tests/Validation/WorkflowCronValidatorTests.cs
- FOUND commit: 4fec1a6 (feat 63-03 validator rewire)
- FOUND commit: 34a9059 (test 63-03 validator tests)

---
*Phase: 63-seconds-granularity-cron*
*Completed: 2026-06-14*
