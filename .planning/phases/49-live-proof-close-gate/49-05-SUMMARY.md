---
phase: 49-live-proof-close-gate
plan: 05
subsystem: infra
tags: [quartz, scheduler, pause-resume, gap-closure, orchestrator, masstransit]

# Dependency graph
requires:
  - phase: 45-keeper-bit-health-gate-global-pause-resume
    provides: PauseAllConsumer/ResumeAllConsumer + WorkflowScheduler.PauseAllAsync (the pause/resume baseline this gap-closure repairs)
  - phase: 49-live-proof-close-gate
    provides: SC3 live outage proof that surfaced GAP-49-2 (orchestrator stuck PAUSED for new workflows after a pause-all/resume-all cycle)
provides:
  - WorkflowScheduler.ResumeAllGroupsAsync — scheduler-wide group-flag clear (clears Quartz pausedTriggerGroups) mirroring PauseAllAsync
  - ResumeAllConsumer clears pausedTriggerGroups via one group-level resume AFTER the per-job ResumeAsync loop (load-bearing ordering -> no misfire herd)
  - GAP-49-2 regression (Normal_After_PauseAll_Resume_Cycle) locking that a post-cycle workflow is born Normal again
  - Replaced no-herd contract (Group_Resume_Runs_After_Per_Job_Reschedules ordering + StartAt>=now across all reschedules)
affects: [phase-49-close-gate, operator-live-run, v4.0.0-milestone-audit]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Group-flag clear AFTER per-job fresh-from-now reschedule: the ordering, not avoidance of the global call, is the no-herd guarantee (D-08 Option A)"
    - "Regression-over-real-RAM-scheduler: drive the TRUE PauseAllConsumer -> PauseAll() -> ResumeAllConsumer path end-to-end to lock a recovery invariant"

key-files:
  created: []
  modified:
    - src/Orchestrator/Scheduling/WorkflowScheduler.cs
    - src/Orchestrator/Consumers/ResumeAllConsumer.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs

key-decisions:
  - "GAP-49-2 fixed via D-08 Option A: clear pausedTriggerGroups on resume; PauseAllConsumer/PauseAll() left UNCHANGED"
  - "ResumeAllGroupsAsync uses scheduler.ResumeAll(), NOT ResumeTriggers(GroupMatcher.AnyGroup()) — in Quartz 3.18 RAMJobStore the AnyGroup matcher unpauses existing triggers but does NOT clear pausedTriggerGroups, so post-cycle workflows stay Paused (the locked spec's API would not have closed the gap)"
  - "No-herd guarantee shifts from 'no native ResumeAll call ever' to 'no immediate-refire herd', preserved by the load-bearing ordering (group resume strictly after every fresh-from-now per-job reschedule)"

patterns-established:
  - "Pattern: scheduler owns the Quartz group-level call (ResumeAllGroupsAsync) keeping the consumer thin, mirroring PauseAllAsync"
  - "Pattern: assert load-bearing ordering over an NSubstitute IScheduler spy via ReceivedCalls() index comparison (ResumeAll index > last ScheduleJob index)"

requirements-completed: [TEST-01, TEST-02, TEST-03]

# Metrics
duration: ~35min
completed: 2026-06-09
---

# Phase 49 Plan 05: GAP-49-2 Close (ResumeAll clears pausedTriggerGroups) Summary

**Closed GAP-49-2: orchestrator Quartz scheduler no longer stays PAUSED for newly-scheduled workflows after a global pause-all/resume-all cycle — ResumeAllConsumer now clears Quartz's pausedTriggerGroups via a single scheduler-wide ResumeAll() placed AFTER the per-job fresh-from-now reschedule loop (no misfire herd).**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-06-09T12:43:00Z (approx)
- **Completed:** 2026-06-09T13:18:00Z
- **Tasks:** 2
- **Files modified:** 4 (2 production, 2 test)

## Accomplishments
- Added `WorkflowScheduler.ResumeAllGroupsAsync(ct)` — scheduler-wide group-flag clear, owning the Quartz call so the consumer stays thin (mirrors `PauseAllAsync`).
- `ResumeAllConsumer.Consume` now injects `WorkflowScheduler` and calls `ResumeAllGroupsAsync` EXACTLY ONCE, AFTER the per-job `ResumeAsync` foreach — the load-bearing ordering that neutralizes the catch-up herd.
- Added the GAP-49-2 regression `Normal_After_PauseAll_Resume_Cycle`: over a real RAM scheduler it drives the TRUE `PauseAllConsumer -> scheduler.PauseAll() -> ResumeAllConsumer.Consume` path and asserts a brand-new workflow scheduled AFTER the cycle is born `TriggerState.Normal` with a future fire time (FAILS pre-fix, PASSES post-fix).
- Retired the locked negative `Native_ResumeAll_Is_Never_Called`, replacing it with `Group_Resume_Runs_After_Per_Job_Reschedules` (ordering: `ResumeAll` index > last `ScheduleJob` index, `Received(1).ResumeAll`) and strengthened the no-burst fact to assert `StartAt >= now` across ALL reschedules.
- `PauseAllConsumer.cs` and `PauseAllConsumerTests` are byte-for-byte unchanged (D-08 Option A preserved); all four build configs (Orchestrator + BaseApi.Tests, Release + Debug) are 0-warning / 0-error.

## Task Commits

Each task was committed atomically:

1. **Task 1: Add group-level resume to scheduler + wire it after the per-job loop in the consumer** — `081895f` (fix)
2. **Task 2: Replace the Native_ResumeAll negative with the ordering + no-herd contract; add the GAP-49-2 regression (incl. the Rule-1 ResumeAll() API correction)** — `03e0129` (test)

_Note: Task 2's commit also carries the Rule-1 production correction (ResumeTriggers(AnyGroup()) -> ResumeAll()) because that correction is what makes the Task-2 regression pass; see Deviations._

## Files Created/Modified
- `src/Orchestrator/Scheduling/WorkflowScheduler.cs` — added `ResumeAllGroupsAsync(ct) => scheduler.ResumeAll(ct)` with a doc-comment explaining why `ResumeAll()` (not `ResumeTriggers(AnyGroup())`) is required to clear `pausedTriggerGroups`.
- `src/Orchestrator/Consumers/ResumeAllConsumer.cs` — injected `WorkflowScheduler scheduler`; appended one `await scheduler.ResumeAllGroupsAsync(ct)` after the per-job loop; rewrote the load-bearing XML doc-comment to the new "no immediate-refire herd" contract (GAP-49-2 / D-08 Option A).
- `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs` — retired `Native_ResumeAll_Is_Never_Called`; added `Group_Resume_Runs_After_Per_Job_Reschedules`; strengthened `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now` to `Assert.All(... StartTimeUtc >= before)`; passed `WorkflowScheduler` to the consumer in the `Build` helper; updated the class doc-comment.
- `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs` — added `Normal_After_PauseAll_Resume_Cycle` (`[Trait("Phase","49")]`); passed `WorkflowScheduler` to both existing consumer ctors; retired the lines-68-71 group-semantics workaround comment.

## Decisions Made
- **D-08 Option A** applied exactly: keep scheduler-wide `PauseAll()` unchanged; clear the group flag on resume after the per-job loop.
- **Use `scheduler.ResumeAll()` for the group-flag clear** rather than the spec's suggested `ResumeTriggers(GroupMatcher<TriggerKey>.AnyGroup())`. The CONTEXT/PLAN spec explicitly permitted (did not mandate) avoiding the native call — the binding guarantee was the no-herd ordering, not the API name. Empirically, the AnyGroup matcher does not clear `pausedTriggerGroups` in Quartz 3.18 RAMJobStore, so it leaves GAP-49-2 open; `ResumeAll()` is the only API that clears the set. The no-herd ordering still holds because every trigger is already fresh-from-now before the call.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Group-flag clear API: `ResumeTriggers(GroupMatcher.AnyGroup())` -> `scheduler.ResumeAll()`**
- **Found during:** Task 2 (running the new `Normal_After_PauseAll_Resume_Cycle` regression)
- **Issue:** The plan/CONTEXT locked the group clear to `scheduler.ResumeTriggers(GroupMatcher<TriggerKey>.AnyGroup(), ct)`. With that API the regression FAILED: the existing workflow (W1) recovered to `Normal`, but a brand-new workflow (W2) scheduled AFTER the pause/resume cycle was still born `Paused` — i.e. GAP-49-2 was NOT closed. In Quartz 3.18 RAMJobStore, `ResumeTriggers(matcher)` with an Anything/AnyGroup matcher unpauses the EXISTING matched triggers but does not remove the "pause future groups" flag from `pausedTriggerGroups`; only `ResumeAll()` clears that set wholesale.
- **Fix:** `ResumeAllGroupsAsync(ct) => scheduler.ResumeAll(ct)`; removed the now-unused `using Quartz.Impl.Matchers;` from `WorkflowScheduler.cs`; updated the `Group_Resume_Runs_After_Per_Job_Reschedules` assertion from `Received(1).ResumeTriggers(...)` to `Received(1).ResumeAll(...)` (ordering still asserted: `ResumeAll` index > last `ScheduleJob` index); aligned both production doc-comments and the test class doc-comment to `ResumeAll()`.
- **Spec-compatibility:** CONTEXT D-08 explicitly states the literal native `ResumeAll()` "may stay avoided in favor of `ResumeTriggers(...)`" — i.e. permitted, not mandatory — and that "the binding guarantee is now 'no immediate-refire herd,' not 'no group-level resume call ever.'" The fix honors the binding guarantee (no-herd via the load-bearing ordering: `ResumeAll` runs strictly after every fresh-from-now reschedule, all `StartAt >= now`) while actually closing the gap. The `must_haves`/`key_links` text that names `ResumeTriggers`/`GroupMatcher` is the surface that changed; the behavioral truths (post-cycle workflow born Normal; no herd; group resume after the per-job loop; PauseAll unchanged) all hold.
- **Verification:** `Normal_After_PauseAll_Resume_Cycle` exits 0 WITH the fix (FAILS with `ResumeTriggers(AnyGroup())` — confirmed empirically). `Group_Resume_Runs_After_Per_Job_Reschedules` and `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now` exit 0. All four build configs 0-warning.
- **Committed in:** `03e0129` (Task 2 commit)

**2. [Rule 3 - Blocking] Unresolvable XML cref in `WorkflowScheduler.cs` doc-comment**
- **Found during:** Task 1 (first Orchestrator Release build)
- **Issue:** The plan-supplied doc-comment used `<see cref="WorkflowLifecycle.ResumeAsync"/>`; `WorkflowScheduler` does not reference `WorkflowLifecycle`'s namespace and the cref could not be resolved -> `CS1574` (treated as error under this repo's 0-warning gate), blocking the build.
- **Fix:** Demoted that single cross-type reference to a plain `<c>WorkflowLifecycle.ResumeAsync</c>` code span (other crefs that DO resolve were kept).
- **Verification:** Orchestrator Release + Debug build 0-warning / 0-error.
- **Committed in:** `081895f` (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (1 bug, 1 blocking)
**Impact on plan:** Deviation 1 is essential — the locked API would not have closed GAP-49-2; the corrected API closes it while preserving every behavioral must-have and the no-herd guarantee. Deviation 2 is a doc-comment build fix. No scope creep: still 2 production-file edits + 2 test-file edits; `PauseAllConsumer.cs`/`PauseAllConsumerTests` untouched; no SC1/SC2/SC3 E2E or close-script changes.

## Issues Encountered
- The MTP runner ignores `dotnet test --filter` AND requires fully-qualified or wildcarded method names for `--filter-method` (a bare method name yields "Zero tests ran"). Resolved by scoping with a leading wildcard, e.g. `--filter-method "*Normal_After_PauseAll_Resume_Cycle"`.

## User Setup Required
None - no external service configuration required. The actual live N-consecutive-GREEN close run remains operator-gated (D-03); TEST-01/02/03 tick on the operator's GREEN run against the rebuilt v4 stack (tracked in 49-HUMAN-UAT.md). This plan removed the defect (GAP-49-2) that was sinking that run.

## Next Phase Readiness
- GAP-49-2 closed and locked by the regression; the orchestrator no longer freezes new-workflow scheduling after a transient L2 outage recovery.
- The operator can now re-run `pwsh -File scripts/phase-49-close.ps1` against the rebuilt v4 stack for the live close gate.
- No open defects remain in scope for Phase 49.

## Self-Check: PASSED

- All 4 modified files exist on disk.
- SUMMARY.md exists.
- Task commits `081895f` and `03e0129` exist in git history.

---
*Phase: 49-live-proof-close-gate*
*Completed: 2026-06-09*
