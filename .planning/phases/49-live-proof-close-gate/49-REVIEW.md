---
phase: 49-live-proof-close-gate
reviewed: 2026-06-09T13:23:33Z
depth: standard
files_reviewed: 4
files_reviewed_list:
  - src/Orchestrator/Scheduling/WorkflowScheduler.cs
  - src/Orchestrator/Consumers/ResumeAllConsumer.cs
  - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs
  - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
findings:
  critical: 0
  warning: 1
  info: 2
  total: 3
status: issues_found
---

# Phase 49 (plan-49-05 GAP-49-2 fix): Code Review Report

**Reviewed:** 2026-06-09T13:23:33Z
**Depth:** standard
**Files Reviewed:** 4
**Status:** issues_found

## Summary

Reviewed the four files changed by plan-49-05 to close GAP-49-2: the new
`WorkflowScheduler.ResumeAllGroupsAsync` wrapper, the updated `ResumeAllConsumer.Consume`,
and the two test classes (`ResumeNoBurstTests`, `ResumeAllConsumerTests`) that validate the
ordering and correctness properties.

**The fix is correct.** The three key correctness properties from the scope note all hold:

1. **Ordering** — `ResumeAllGroupsAsync` is called at line 47 of `ResumeAllConsumer.Consume`,
   which is outside and after the `foreach` loop (lines 43-44). No interleaving is possible.

2. **No-herd guarantee** — `WorkflowLifecycle.ResumeAsync` acts on a `Paused` trigger by
   calling `UnscheduleAsync` (DeleteJob, removes the stale paused trigger entirely) and then
   `ScheduleAsync` (creates a fresh trigger with `StartAt = next Cronos >= now`). By the time
   `ResumeAll()` runs, every formerly-Paused trigger has been deleted and replaced with a
   fresh Normal trigger that fires in the future. There is no Paused trigger remaining for
   `ResumeAll()` to catch-up-fire.

3. **CancellationToken propagation, idempotency, ack** — `context.CancellationToken` flows to
   both `ResumeAsync` (line 44) and `ResumeAllGroupsAsync` (line 47). On serial redelivery,
   per-job `ResumeAsync` skips `Normal` triggers (no-op guard) and Quartz `ResumeAll()` is a
   no-op when no group is paused — fully idempotent. No try/catch in `Consume`; exceptions
   propagate to MassTransit for retry, consistent with `PauseAllConsumer`.

The `Normal_After_PauseAll_Resume_Cycle` test (the GAP regression probe) correctly drives the
full production path over a real Quartz RAM scheduler and its decisive assertion — that a
brand-new workflow scheduled AFTER the pause/resume cycle is born `Normal` — is the right
falsification target. It would have been Red before the fix and is Green after.

One warning and two info items are noted below, all in the test layer. No production code issues.

No Critical issues found.

## Warnings

### WR-01: `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now` captures the hydration-time ScheduleJob — the assertion can pass even if the resume path skips rescheduling

**File:** `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs:100-109`
**Issue:** `Build()` calls `lifecycle.HydrateAndScheduleAsync` (line 56), which internally
calls `scheduler.ScheduleJob` on the spy. The test then collects ALL `ScheduleJob` calls on
the spy after `consumer.Consume` (line 100-105), so the hydration-time trigger is also included
in `scheduled`. The assertion `Assert.All(scheduled, t => Assert.True(t.StartTimeUtc >= before, ...))`
passes for the hydration trigger too (its `StartAt` is also a future Cronos occurrence). As a
result, if `ResumeAsync` were somehow skipping the reschedule step (e.g. a bug in the Paused
guard path that made it exit early), the test would still pass because the hydration call
alone keeps `scheduled` non-empty and future-dated. The test does not exclusively verify that
the RESUME path contributed a `ScheduleJob`.

In the current codebase this is not a latent bug — the spy is configured to return
`TriggerState.Paused` (line 46-47), which drives `ResumeAsync` through the
`UnscheduleAsync + ScheduleAsync` path deterministically. But the test is weaker than it
looks: it conflates hydration and resume reschedule calls.

**Fix:** Filter the captured `ScheduleJob` calls to only those made AFTER `Consume` starts,
or record a call-count baseline before `Consume` and assert the post-`Consume` delta is >= 1:
```csharp
// Record hydration calls before the resume
var callsBefore = spy.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob));

var before = DateTimeOffset.UtcNow;
await consumer.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));

var scheduled = spy.ReceivedCalls()
    .Where(c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob))
    .Select(c => c.GetArguments())
    .Where(args => args.Length >= 2 && args[0] is IJobDetail && args[1] is ITrigger)
    .Select(args => (ITrigger)args[1]!)
    .Skip(callsBefore)  // exclude hydration-time calls
    .ToList();

Assert.NotEmpty(scheduled); // proves the resume path actually rescheduled
Assert.All(scheduled, t => Assert.True(t.StartTimeUtc >= before, "..."));
```

## Info

### IN-01: `Group_Resume_Runs_After_Per_Job_Reschedules` ordering assertion holds vacuously if the resume loop produces no ScheduleJob

**File:** `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs:77-83`
**Issue:** `lastScheduleJobIndex` is computed with `FindLastIndex` across ALL spy calls,
including the hydration-time `ScheduleJob` from `Build()`. If `ResumeAsync` were to exit
before calling `ScheduleAsync` (due to a hypothetical guard regression), `lastScheduleJobIndex`
would still be >= 0 (from hydration), and the `resumeAllIndex > lastScheduleJobIndex` assertion
might still pass depending on relative positions. The test does not prove that the LAST
`ScheduleJob` it finds belongs to the resume path rather than hydration.

This is mitigated in practice: the spy returns `TriggerState.Paused`, so `ResumeAsync`
definitely calls `ScheduleAsync`, and that call is necessarily later in the call timeline than
the hydration call (hydration runs first in `Build`, resume runs inside `Consume`). The ordering
assertion is sound for the current setup, but the test would be tighter if it asserted the
count of `ScheduleJob` calls increased by exactly 1 during `Consume` (proving the resume path
contributed its own reschedule and no spurious extras occurred).

**Fix:** Optional — add a baseline count before `Consume` and assert the increment equals the
number of workflows enumerated (1 in the single-workflow setup):
```csharp
var scheduleJobsBefore = spy.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob));
await consumer.Consume(...);
var scheduleJobsAfter = spy.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob));
Assert.Equal(1, scheduleJobsAfter - scheduleJobsBefore); // exactly one per-job reschedule
```

### IN-02: `Normal_After_PauseAll_Resume_Cycle` XML doc comment cites `ResumeTriggers(AnyGroup())` but the implementation uses `ResumeAll()`

**File:** `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs:133`
**Issue:** The XML doc summary on line 133 reads "per-job reschedule THEN the group-level
`ResumeTriggers(AnyGroup())` clear" — this is the planned approach that was empirically found
NOT to clear `pausedTriggerGroups` in Quartz 3.18 RAMJobStore. The actual implementation uses
`scheduler.ResumeAll()` via `WorkflowScheduler.ResumeAllGroupsAsync`. The comment is a stale
artifact from before the D-08 Option A decision was locked. It does not affect test correctness
but would mislead a reader investigating how the group-flag clear works.

**Fix:** Update the XML doc on line 133 to reference `ResumeAll()` / `ResumeAllGroupsAsync`:
```
/// <see cref="ResumeAllConsumer"/> -> per-job reschedule THEN the single
/// group-level <c>scheduler.ResumeAll()</c> clear (via <see cref="WorkflowScheduler.ResumeAllGroupsAsync"/>).
```

---

_Reviewed: 2026-06-09T13:23:33Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
