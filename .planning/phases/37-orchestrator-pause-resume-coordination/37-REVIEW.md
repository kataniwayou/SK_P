---
phase: 37-orchestrator-pause-resume-coordination
reviewed: 2026-06-06T00:00:00Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
  - src/Keeper/Consumers/FaultExecutionResultConsumer.cs
  - src/Messaging.Contracts/PauseWorkflow.cs
  - src/Messaging.Contracts/ResumeWorkflow.cs
  - src/Orchestrator/Consumers/PauseWorkflowConsumer.cs
  - src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
  - src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs
  - src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs
  - src/Orchestrator/Hydration/WorkflowLifecycle.cs
  - src/Orchestrator/Program.cs
  - src/Orchestrator/Scheduling/WorkflowScheduler.cs
  - tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs
  - tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs
  - tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs
  - tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs
findings:
  critical: 0
  warning: 2
  info: 3
  total: 5
status: issues_found
---

# Phase 37: Code Review Report

**Reviewed:** 2026-06-06T00:00:00Z
**Depth:** standard
**Files Reviewed:** 16 (15 in scope; one listed path resolved to two consumer files)
**Status:** issues_found

## Summary

This phase wires orchestrator pause/resume coordination: the two Keeper fault consumers
publish `PauseWorkflow` at fault intake and `ResumeWorkflow` on probe recovery; two
orchestrator consumers share a dedicated `orchestrator-pauseresume` fan-out endpoint
(`ConcurrentMessageLimit=1`) and drive a Quartz three-state model (Normal/Paused/None) via the
deterministic `TriggerKey(jobId.ToString("D"))`.

Overall the implementation is solid and well-documented, with the security/structured-logging
discipline (no interpolation of `WorkflowId`/`H`/correlation ids) consistently applied. The
MassTransit Publish-vs-Send semantics are correct (control messages Publish for type-routed
fan-out; re-inject and DLQ use Send to a specific queue). Async/cancellation-token threading is
correct throughout — every awaited call receives `context.CancellationToken`.

**The commit 571498f `RescheduleAsync` fix is correct and complete.** `RescheduleJob(triggerKey,
trigger)` atomically removes the completed-but-not-yet-purged one-shot trigger that still occupies
the deterministic key and stores the replacement, without deleting the non-durable job (the job is
preserved because only the trigger is swapped). The `replaced is null` fallback to
`ScheduleJob(trigger, ct)` correctly handles the first-trigger / no-prior-trigger case. This
resolves the `ObjectAlreadyExistsException` regression from 37-02. No further change needed there.

Two warnings below concern a Quartz trigger-state race window and a fallback edge on a non-durable
job; three info items cover duplication and minor robustness. None are blockers.

## Warnings

### WR-01: Resume guard can transiently observe a non-Paused state during the fire window

**File:** `src/Orchestrator/Hydration/WorkflowLifecycle.cs:185-190`
**Issue:** `ResumeAsync` reads `GetTriggerStateAsync` and acts ONLY on `TriggerState.Paused`. The
one-shot trigger is `Normal` between fires and transitions to a completing/`None` state while
`WorkflowFireJob.Execute` runs (the completed no-repeat trigger is purged only after Execute
returns; `RescheduleAsync` then re-arms it). If a `ResumeWorkflow` is delivered in the narrow
window where the trigger is NOT exactly `Paused` (e.g. the job is mid-fire, or a `PauseJob` landed
on a job whose only trigger had just completed and left the key transiently empty → `None`), the
resume is silently dropped. The workflow would then stay halted until the next operator action.
The serial `ConcurrentMessageLimit=1` endpoint plus the Keeper ordering (Pause before probe, Resume
only after recovery) makes this unlikely in the designed happy path, but it is a real
correctness edge for the Quartz state model and is not covered by a test.
**Fix:** Document the assumption explicitly (Resume relies on the deterministic key being in
exactly `Paused` at delivery time because Pause is published strictly before the probe and Resume
strictly after, on a serial endpoint), OR harden the guard to also re-arm when the key is `None`
but L1 still holds the workflow (treat "paused-then-trigger-vanished" as resumable). At minimum add
a debug/info log on the ignore branch so a dropped resume is observable:
```csharp
var state = await scheduler.GetTriggerStateAsync(wf.JobId, ct);
if (state != TriggerState.Paused)
{
    logger.LogInformation(
        "Resume ignored for WorkflowId={WorkflowId} — trigger state {TriggerState} is not Paused",
        workflowId, state);
    return;
}
```
This costs one log line and turns a silent no-op into a diagnosable event.

### WR-02: RescheduleAsync fallback ScheduleJob(trigger) assumes the non-durable job still exists

**File:** `src/Orchestrator/Scheduling/WorkflowScheduler.cs:82-86`
**Issue:** When `RescheduleJob` returns `null` (no prior trigger on the deterministic key), the
fallback calls `scheduler.ScheduleJob(trigger, ct)` — the single-arg overload that adds a trigger to
an ALREADY-REGISTERED job. The job built in `ScheduleAsync` is non-durable (no `.StoreDurably()`),
and Quartz auto-removes a non-durable job once it has no remaining triggers. In the normal
self-reschedule path this is safe (the firing trigger is still present until Execute returns, so
`RescheduleJob` finds it and never hits the fallback). But the fallback branch is reachable only in
states where the trigger is already gone — precisely the state where a non-durable job may also have
been purged. If the job is gone, `ScheduleJob(trigger)` throws `JobPersistenceException`
("The job ... referenced by the trigger does not exist") rather than re-establishing the schedule.
The current callers (`WorkflowFireJob.Execute`) only invoke `RescheduleAsync` from inside Execute
where the trigger is still present, so this is latent rather than live — but the fallback as written
gives a false sense of "handles the no-prior-trigger case."
**Fix:** Make the fallback self-sufficient by rebuilding the full job+trigger pair (mirror
`ScheduleAsync`'s `JobBuilder` + two-arg `ScheduleJob(job, trigger, ct)`), or assert/document that
`RescheduleAsync` is only ever called while the deterministic trigger is live so the fallback is
defensive-only. If the latter, a comment stating "fallback unreachable from current callers; present
only for the cold-start-with-durable-job case" would prevent a future caller from relying on broken
behavior.

## Info

### IN-01: Near-total duplication between the two Keeper fault consumers

**File:** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs:34-77` and
`src/Keeper/Consumers/FaultExecutionResultConsumer.cs:35-78`
**Issue:** The two consumers' `Consume` bodies are identical except for (a) the `nameof(...)` fault
label and (b) the re-inject endpoint (`queue:{inner.ProcessorId:D}` vs `queue:{OrchestratorQueues.Result}`).
Pause/Resume publish, probe-loop, DLQ, and structured-log logic are copied verbatim. Any future fix
to the pause/resume publish discipline (e.g. the WR-01 ordering note, or carrying additional
correlation metadata) must be applied in two places, risking drift.
**Fix:** Extract a shared helper (e.g. a base method taking the verbatim inner, the fault label, and
a `Func` that resolves the re-inject endpoint URI) so the pause/probe/resume/DLQ flow lives once.
Keep the two `IConsumer<Fault<T>>` shells thin.

### IN-02: ProcessorId interpolated into the re-inject queue URI

**File:** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs:62`
**Issue:** `new Uri($"queue:{inner.ProcessorId:D}")` interpolates a Guid into a queue address. This
is functionally correct and not a security finding (a Guid `:D` format cannot inject a
broker-control character), but it is the one place in these files where a message field is string-
interpolated rather than passed structurally — worth a deliberate note so the structured-logging
convention is not assumed to extend to address construction.
**Fix:** No change required; optionally add a brief comment that Guid `:D` is a closed
character-set and thus safe to embed in the queue address. Confirms intent for future readers.

### IN-03: PauseWorkflowConsumerDefinition holds IOptions but reads it only at configure time

**File:** `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs:22-38`
**Issue:** `_retryOptions` is stored as a field and dereferenced once inside `ConfigureConsumer`
(`_retryOptions.Value.Limit`). This is correct and matches the existing Start/Stop definition
pattern, but the field outlives its single use. Minor — flagged only for consistency review, not a
defect. The shared-endpoint retry-ownership design (only the Pause definition registers
`UseMessageRetry`; Resume inherits it) is correctly implemented and well-documented.
**Fix:** None required. The pattern is intentional and matches the codebase convention.

---

_Reviewed: 2026-06-06T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
