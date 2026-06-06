---
phase: 41-orchestrator-pause-resume-diagnostics
plan: 02
subsystem: infra
tags: [quartz, scheduling, orchestrator, job-persistence, hermetic-test]

# Dependency graph
requires:
  - phase: 23-orchestrator-stop-reload-lifecycle
    provides: WorkflowScheduler.RescheduleAsync + WorkflowFireJob self-reschedule (the latent WR-02 fallback)
  - phase: 37-orchestrator-pause-resume-coordination
    provides: 37-REVIEW WR-02 finding + PauseResumeSchedulingTests hermetic RAM-scheduler harness
provides:
  - "RescheduleAsync re-creates the full job+trigger in the replaced-is-null fallback — cannot throw JobPersistenceException on a purged non-durable job (WR-02/D-04)"
  - "RescheduleAsync signature now carries workflowId, threaded from its sole caller WorkflowFireJob.Execute"
  - "Hermetic test (RescheduleSchedulingTests) drives the null-fallback branch and asserts re-establishment, not a throw (D-06)"
affects: [orchestrator-scheduling, quartz-job-lifecycle]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Self-sufficient reschedule fallback: mirror ScheduleAsync's JobBuilder.Create<WorkflowFireJob>().UsingJobData(workflowId) + two-arg ScheduleJob(job, trigger) instead of bare single-arg ScheduleJob(trigger)"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/Scheduling/RescheduleSchedulingTests.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowScheduler.cs
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs

key-decisions:
  - "D-04 (Fork A): re-create the full job+trigger in the fallback rather than fail-loud (D-05) — honors the phase Goal 'cannot throw on a purged non-durable job'"
  - "Inline duplication of the JobBuilder block (not a shared private helper) to keep the diff minimal — explicitly allowed by D-04 discretion"
  - "Thread workflowId as the FIRST parameter of RescheduleAsync to mirror ScheduleAsync(Guid workflowId, Guid jobId, ...); recovered from caller, not from a purged job"
  - "D-06: test drives the null branch via a never-scheduled jobId (cleanest 'no prior trigger') and asserts Single/Normal/future-next-fire + workflowId job-data, NOT Assert.ThrowsAsync<JobPersistenceException>"

patterns-established:
  - "Reschedule fallback re-establishes the schedule self-sufficiently — same scheduling footprint as ScheduleAsync, idempotent on the deterministic KeyFor(jobId)"

requirements-completed: [WR-02]

# Metrics
duration: ~20min
completed: 2026-06-07
---

# Phase 41 Plan 02: RescheduleAsync Fallback Hardening (WR-02) Summary

**RescheduleAsync's replaced-is-null fallback now re-creates the full WorkflowFireJob+trigger (re-stamping the workflowId job-data) instead of a bare ScheduleJob(trigger), so it can no longer throw JobPersistenceException on a purged non-durable job — proven by a hermetic RAM-scheduler test that asserts re-establishment.**

## Performance

- **Duration:** ~20 min
- **Completed:** 2026-06-07
- **Tasks:** 2
- **Files modified:** 2 source + 1 new test

## Accomplishments
- Hardened `WorkflowScheduler.RescheduleAsync`'s `replaced is null` fallback (WR-02/D-04): mirrors `ScheduleAsync`'s `JobBuilder.Create<WorkflowFireJob>().WithIdentity(KeyFor(jobId)).UsingJobData("workflowId", workflowId.ToString("D")).Build()` and calls the two-arg `ScheduleJob(job, trigger, ct)` — the trigger already `.ForJob(KeyFor(jobId))` so it binds to the re-created job.
- Threaded `workflowId` into the signature as `RescheduleAsync(Guid workflowId, Guid jobId, string cron, CancellationToken ct)`; updated the sole production caller `WorkflowFireJob.Execute` (line 103) to pass the `workflowId` it already parses from its `MergedJobDataMap`.
- Added hermetic test `RescheduleSchedulingTests.RescheduleReestablishesScheduleWhenNoPriorTrigger` (D-06): never-scheduled jobId drives the null branch; asserts exactly one trigger, `TriggerState.Normal`, future next-fire, and that the re-created job carries the `workflowId` job-data.
- Updated the `RescheduleAsync` XML doc-comment to document the re-create-on-purge fallback semantics.

## Task Commits

Each task was committed atomically:

1. **Task 1: Thread workflowId into RescheduleAsync + re-create full job+trigger in fallback** - `de0cec0` (fix)
2. **Task 2: Hermetic test driving the replaced-is-null fallback** - `0edeb53` (test)

_Note: Task 1 is the production fix (committed as `fix`); Task 2's deliverable is purely the new test, which goes GREEN against the already-landed Task 1 (signature + fallback), so it is the single `test` commit. The RED→GREEN intent is preserved: the test as written cannot compile/pass against the old 3-arg signature, and passes only because Task 1's 4-arg signature + re-create fallback are in._

## Files Created/Modified
- `src/Orchestrator/Scheduling/WorkflowScheduler.cs` - `RescheduleAsync` now takes `workflowId`; fallback re-creates the full job+trigger (re-stamping workflowId job-data) instead of bare `ScheduleJob(trigger)`.
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` - `Execute` passes `workflowId` into `RescheduleAsync` (line 103).
- `tests/BaseApi.Tests/Orchestrator/Scheduling/RescheduleSchedulingTests.cs` - hermetic fallback test (cloned PauseResumeSchedulingTests harness).

## Decisions Made
- Inline duplication of the `JobBuilder` block in the fallback (not a shared private helper) — minimal diff, explicitly allowed by D-04 discretion.
- Drove the null branch via a never-scheduled jobId (PATTERNS.md option 2 — cleanest expression of "no prior trigger") rather than schedule-then-DeleteJob.
- `workflowId` is the first parameter to mirror `ScheduleAsync(Guid workflowId, Guid jobId, ...)`.

## Deviations from Plan

None - plan executed exactly as written. The sole caller grep confirmed only `WorkflowFireJob.Execute` calls `RescheduleAsync` (no test/other 3-arg callers to migrate beyond the new test).

## Issues Encountered
- The MTP test runner ignores the VSTest-style `--filter "FullyQualifiedName~..."` argument (warning MTP0001) and runs the whole suite. Verified the focused test separately with the MTP-native `-- --filter-class "*RescheduleSchedulingTests*"` syntax: 1 passed. This is a known runner-syntax quirk, not a test defect.

## Verification
- `dotnet build src/Orchestrator/Orchestrator.csproj -c Release -warnaserror` → **0 warnings, 0 errors, exit 0**.
- Focused (`-- --filter-class "*RescheduleSchedulingTests*"`) → **Passed! 1/1**.
- Full hermetic suite (BaseApi.Tests.dll) → **Passed! 505/505, 0 failed** — the 4-arg signature change broke no existing test.
- grep confirms: fallback contains `JobBuilder.Create<WorkflowFireJob>()` + `.UsingJobData("workflowId"`, no `ScheduleJob(trigger, ct)`; caller passes `RescheduleAsync(workflowId, wf.JobId, ...)`; test contains `TriggerState.Normal` + `JobDataMap.GetString("workflowId")` and no `Assert.ThrowsAsync<JobPersistenceException>`.

## Threat Surface
No new surface (per plan threat_model, threats_open: 0). The re-created `UsingJobData("workflowId", ...)` value is `workflowId.ToString("D")` — an orchestrator-controlled Guid identical to what `ScheduleAsync` already stamps. The fallback re-creates exactly one job+trigger on the deterministic key — idempotent, no amplification, same footprint as `ScheduleAsync`. No new external input, no new parsing, no new trust boundary.

## Next Phase Readiness
- WR-02 (SC2) closed. Combined with WR-01 (plan 41-01, the ResumeAsync diagnostics log), the phase's two gap-closure goals are addressed.
- No blockers. Orchestrator Release build is 0-warning and the hermetic suite is GREEN.

## Self-Check: PASSED

- FOUND: src/Orchestrator/Scheduling/WorkflowScheduler.cs
- FOUND: src/Orchestrator/Scheduling/WorkflowFireJob.cs
- FOUND: tests/BaseApi.Tests/Orchestrator/Scheduling/RescheduleSchedulingTests.cs
- FOUND: .planning/phases/41-orchestrator-pause-resume-diagnostics/41-02-SUMMARY.md
- FOUND commit: de0cec0 (Task 1 — fix)
- FOUND commit: 0edeb53 (Task 2 — test)

---
*Phase: 41-orchestrator-pause-resume-diagnostics*
*Completed: 2026-06-07*
