---
phase: 32-cancelled-circuit-breaker
plan: 03
subsystem: enum cleanup (CANCELLED)
status: cancelled
cancelled_by: user
tags: [enum, step-entry-condition, d-12-reversed, cancelled-by-user, wave-1, no-op]

# Dependency graph
requires: []
provides: []
---

# Plan 32-03 — CANCELLED BY USER

## Decision

Plan 32-03 was **cancelled by explicit user direction** during Wave 1 execution. The plan's
sole purpose was to remove the dead `StepEntryCondition.PreviousCancelled (3)` member (per the
locked decision **D-12**) so `StepDtoValidator.IsInEnum()` would auto-reject `EntryCondition == 3`.

The user directed that `PreviousCancelled = 3` be **kept**, on the grounds that the member is
unused and removing it is unnecessary churn that does not affect any production behavior.

This is a deliberate **reversal of D-12**. It is recorded here for traceability so the phase
verifier does not flag 32-03 as missing work.

## What was reverted (the pre-staged 32-03 edits were uncommitted; reverted, not committed)

- `src/BaseApi.Service/Features/Step/StepEntryCondition.cs` — restored to committed state
  (`PreviousCancelled = 3` retained; all 6 members present, explicit numeric assignments unchanged).
- `src/Messaging.Contracts/StepOutcome.cs` — restored to committed state
  (`Cancelled = 3, // == StepEntryCondition.PreviousCancelled` comment retained; accurate again
  now that the mirrored member exists).
- `tests/BaseApi.Tests/Orchestrator/StepEntryConditionEnumFacts.cs` — the new removal-assertion
  test (untracked) was deleted; it would fail against the retained member.

No commit was produced by this plan — the reverts returned three files to their already-committed
state and removed one untracked file. `git status` for all three is clean.

## Requirement impact (req-6)

- The **"keep `StepOutcome.Cancelled (3)` live"** half of req-6 is satisfied trivially —
  `StepOutcome.Cancelled = 3` is unchanged and remains live for the token-cancellation EXEC-08
  outcome (special-cased by the consumer, not matched by `SelectNext`).
- The **"remove `PreviousCancelled (3)`"** half is intentionally NOT performed, per user override
  of D-12. `IsInEnum()` continues to accept `EntryCondition == 3` (the member is valid). No
  production code references `PreviousCancelled` (grep-confirmed: only the enum declaration itself),
  so behavior is unchanged.

## Downstream

- No other plan depends on 32-03 (it was "fully standalone, disjoint from every other Plan-32
  file"). Waves 2 and 3 (32-04/05/06/07) are unaffected.

## Self-Check: PASSED (cancelled cleanly)

- [x] PreviousCancelled = 3 present in enum
- [x] StepOutcome.cs comment restored
- [x] removal-test deleted
- [x] working tree clean for all three files
- [x] no spurious commit produced
