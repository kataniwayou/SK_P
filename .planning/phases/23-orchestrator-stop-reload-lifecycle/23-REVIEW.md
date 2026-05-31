---
phase: 23-orchestrator-stop-reload-lifecycle
reviewed: 2026-05-31T00:00:00Z
depth: standard
files_reviewed: 15
files_reviewed_list:
  - src/Messaging.Contracts/EntryStepDispatch.cs
  - src/Messaging.Contracts/IExecutionCorrelated.cs
  - src/Messaging.Contracts/Projections/StepProjection.cs
  - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
  - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
  - src/Orchestrator/Hydration/HydrationBackgroundService.cs
  - src/Orchestrator/Hydration/WorkflowLifecycle.cs
  - src/Orchestrator/L1/IWorkflowL1Store.cs
  - src/Orchestrator/L1/WorkflowL1.cs
  - src/Orchestrator/L1/WorkflowL1Store.cs
  - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
  - src/Orchestrator/Program.cs
  - src/Orchestrator/Scheduling/CronInterval.cs
  - src/Orchestrator/Scheduling/WorkflowFireJob.cs
  - src/Orchestrator/Scheduling/WorkflowScheduler.cs
findings:
  critical: 0
  warning: 4
  info: 5
  total: 9
status: issues_found
---

# Phase 23: Code Review Report

**Reviewed:** 2026-05-31
**Depth:** standard
**Files Reviewed:** 15
**Status:** issues_found

## Summary

Reviewed the Phase 23 orchestrator stop/reload lifecycle: the in-memory L1 store (ConcurrentDictionary + per-workflow SemaphoreSlim stripe), Quartz-based self-rescheduling one-shot fire jobs, MassTransit Send-based entry-step dispatch, and the gated Start/Stop consumers with their business-vs-infra ack split.

Overall the code is well-structured and the documented invariants (drop-if-held stripes, never-dispose semaphores, read-only L2, gate-closed clean-ack) are correctly implemented. The ack-split is sound and the tests genuinely exercise it (not tautological). No critical security or data-loss issues were found — the orchestrator is strictly read-only against L2 and carries no injection/secret surface.

The concerns are concentrated in **Quartz job-lifecycle race windows** between the self-rescheduling fire job and concurrent teardown, and in the **exception-classification split** when a non-Redis fault (Quartz `SchedulerException`) is raised inside the lifecycle — `IsInfra`/`IsBusiness` only knows about Redis exceptions, so scheduler faults are silently bucketed as "business" in one path and would propagate as "infra" in another. None are crashes; all are correctness-under-concurrency edge cases worth confirming against the design intent.

## Warnings

### WR-01: Self-reschedule races teardown — `ScheduleJob(trigger)` throws when the job was just deleted

**File:** `src/Orchestrator/Scheduling/WorkflowFireJob.cs:84-88`, `src/Orchestrator/Scheduling/WorkflowScheduler.cs:55-71`

**Issue:** `WorkflowFireJob.Execute` self-reschedules at the end via `scheduler.RescheduleAsync(...)`, which calls `scheduler.ScheduleJob(trigger, ct)` for the EXISTING job. `[DisallowConcurrentExecution]` prevents the same job double-firing, but it does NOT serialize against `TeardownAsync` -> `UnscheduleAsync` -> `DeleteJob`. A Stop (or Start-reload teardown) can run on a different thread/stripe while a fire is in flight: the stripe guard in the consumers protects the L1 store + scheduler ops, but the fire job itself does NOT acquire the stripe. Sequence:

1. Fire job for workflow W is executing (past the L1 `TryGet`, sending dispatches).
2. Stop consumer acquires W's stripe, runs `TeardownAsync` -> `DeleteJob(W)` (removes job + triggers).
3. Fire job reaches line 87 and calls `RescheduleAsync` -> `ScheduleJob(trigger)` for a job that no longer exists -> Quartz throws `SchedulerException` ("The job ... does not exist").

That exception propagates out of `Execute`. Per `IsInfra` it is NOT a Redis exception, so nothing in the orchestrator treats it specially; Quartz logs it and (with the one-shot trigger already consumed) the workflow silently stops rescheduling. Because the fire path and teardown path are not mutually excluded by the stripe, this is a live race, not just theoretical.

**Fix:** Either (a) have the fire job re-check job existence / catch `SchedulerException` from the reschedule and treat a "job gone" as a clean business no-op (the workflow was torn down — stop rescheduling intentionally):
```csharp
if (CronInterval.NextOccurrence(wf.Cron, nowUtc) is not null)
{
    try
    {
        await scheduler.RescheduleAsync(wf.JobId, wf.Cron, context.CancellationToken);
    }
    catch (SchedulerException) when (!await scheduler.CheckExists(jobKey, ct))
    {
        // Job was torn down (Stop/reload) while this fire was in flight — stop rescheduling (business).
        logger.LogInformation("Workflow {WorkflowId} torn down during fire — not rescheduling", workflowId);
    }
}
```
or (b) make `RescheduleAsync` no-op when the job no longer exists (`if (!await scheduler.CheckExists(KeyFor(jobId), ct)) return;`). Confirm which is the intended design — silently dropping the reschedule on a deleted job is almost certainly correct, but it must be explicit rather than an unhandled throw.

### WR-02: Quartz `SchedulerException` is misclassified by the `IsInfra`/`IsBusiness` split

**File:** `src/Orchestrator/Hydration/WorkflowLifecycle.cs:107,122,130-137`

**Issue:** `IsInfra` returns true only for `RedisConnectionException`/`RedisTimeoutException`/`RedisException`; everything else (per `IsBusiness`) is logged+skipped. But `HydrateAndScheduleAsync` (line 107) and `TeardownAsync` (line 122) both `await` Quartz scheduler calls that can throw `SchedulerException` / `ObjectAlreadyExistsException` for genuine infra-class reasons (scheduler shut down mid-op, RAMJobStore in a bad state, duplicate jobKey). The doc comment on `IsInfra` even claims it covers "a broker fault during scheduling" — but a Quartz fault is NOT a Redis exception, so it falls through to `IsBusiness`. Consequences:

- In `HydrationBackgroundService` (line 57) a scheduler fault during startup hydrate is swallowed as "corrupt workflow, skip" — the workflow silently never schedules, yet the gate still opens (line 64) reporting healthy.
- In the consumer path the same fault is NOT caught by any business guard (consumers don't wrap in `IsBusiness`), so it propagates as if infra -> bounded retry -> `_error`. Same exception, opposite handling depending on caller.

The split is "everything non-Redis = business," which conflates malformed-JSON (genuinely business, should skip) with scheduler/runtime faults (should propagate). 

**Fix:** Make the classification explicit about what is business rather than "not Redis." Whitelist the true business exceptions (`JsonException`, `FormatException`, `ArgumentException`) and treat `SchedulerException` plus unknown exceptions as infra:
```csharp
public static bool IsBusiness(Exception ex) =>
    ex is System.Text.Json.JsonException or FormatException or ArgumentException;

public static bool IsInfra(Exception ex) => !IsBusiness(ex);
```
At minimum, decide deliberately whether a Quartz scheduling fault during startup hydration should (a) be skipped silently while still opening the gate, or (b) keep the gate closed. The current code does (a) by accident.

### WR-03: `RescheduleAsync(trigger)` for an existing job can throw `ObjectAlreadyExistsException` if a stale trigger lingers

**File:** `src/Orchestrator/Scheduling/WorkflowScheduler.cs:64-70`, `src/Orchestrator/Scheduling/WorkflowFireJob.cs:87`

**Issue:** `RescheduleAsync` builds a `TriggerBuilder.Create()` with NO explicit identity, then `ScheduleJob(trigger)`. Quartz auto-generates a unique trigger key, so normally there is no collision. However, the one-shot trigger model relies on the previous trigger having been consumed/removed before the new one is added. If a misfire causes `WithMisfireHandlingInstructionFireNow()` to re-fire while the previous trigger has not yet been cleaned up, or if two fire executions overlap on the boundary of `[DisallowConcurrentExecution]` release, multiple live triggers can accumulate on the same job — each one re-arming the next. Over time this can drift from "one-shot self-rescheduling" to multiple concurrent trigger chains for one workflow (duplicate dispatches). Worth verifying that the previous one-shot trigger is always fully retired before the reschedule adds the next, e.g. by giving the trigger a deterministic identity and using `RescheduleJob(oldKey, newTrigger)` instead of `ScheduleJob(newTrigger)`.

**Fix:** Use a deterministic trigger key per job and replace rather than add:
```csharp
var triggerKey = new TriggerKey(jobId.ToString("D"));
var trigger = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(KeyFor(jobId))
    .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))
    .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow()).Build();
if (await scheduler.CheckExists(triggerKey, ct))
    await scheduler.RescheduleJob(triggerKey, trigger, ct);
else
    await scheduler.ScheduleJob(trigger, ct);
```
This makes "one live trigger per job" an invariant rather than relying on timing.

### WR-04: `CancellationToken` not propagated to the per-step Send in the loop, and a mid-loop infra fault leaves a partial fan-out

**File:** `src/Orchestrator/Scheduling/WorkflowFireJob.cs:55-74`

**Issue:** Two related concerns in the dispatch loop:
1. `sendProvider.GetSendEndpoint(...)` (line 72) is awaited WITHOUT the cancellation token (the overload takes none here, acceptable), but the partial-failure behavior is the real concern: if step N's `endpoint.Send` (line 73) throws an infra fault (broker unreachable), steps 1..N-1 have already been dispatched with the shared per-fire correlationId, and the job throws. Quartz will (per misfire) re-fire the WHOLE job, re-dispatching steps 1..N-1 a second time under a NEW correlationId. Entry-step dispatch is therefore at-least-once with possible partial duplicates on broker flap. This may be acceptable (the design notes "a 2nd replica N×-dispatches as accepted/deferred behavior"), but the partial-then-retry duplication within a single replica is a distinct, undocumented case.
2. The liveness refresh (lines 78-82) and reschedule (84-88) are skipped entirely when an early Send throws, so a transiently-unreachable broker also stalls the self-reschedule chain for that workflow until the next external Start (there is no fire to re-arm the trigger). Confirm this is intended — a broker blip could permanently park a workflow's schedule.

**Fix:** Document the at-least-once / partial-fan-out contract explicitly, and consider moving the reschedule into a `finally` (or before the Sends) so a broker blip does not permanently stop the workflow from re-arming. If exactly-once-per-fire matters, the dispatch loop would need an outbox/transactional-send, which is a larger design change — flag for the design owner.

## Info

### IN-01: `Step()` key omits the `:D` format specifier that `Root()` documents as load-bearing

**File:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:32,34`

**Issue:** `Root` uses `$"{Prefix}{workflowId:D}"` and the doc comment stresses the explicit `:D`. `Step` uses bare `$"{Prefix}{workflowId}:{stepId}"` with no `:D` on either GUID. These are byte-identical today (default `Guid.ToString()` is "D"), so not a bug — but the asymmetry undercuts the "make it explicit" rationale and is a latent footgun if anyone ever changes the default. Apply `:D` to both GUIDs in `Step` for consistency with the documented intent.

**Fix:** `public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId:D}:{stepId:D}";`

### IN-02: `WorkflowL1.Liveness` initialized to `default!` permits a null-deref window

**File:** `src/Orchestrator/L1/WorkflowL1.cs:28`, `src/Orchestrator/Scheduling/WorkflowFireJob.cs:79-82`

**Issue:** `Liveness { get; set; } = default!;` suppresses the nullable warning but leaves the property genuinely null if a `WorkflowL1` is ever constructed without the `with { Liveness = ... }` initializer. The fire path defensively handles `current is null` (line 80), which is good — but the `default!` makes the "always set at construction" contract a convention enforced only by the two known call sites (`HydrateAndScheduleAsync` line 101-104 and the test seeder), not the type. If a future call site forgets the initializer, the null flows silently. Consider making `Liveness` a required init property (`public required LivenessProjection Liveness { get; set; }`) so the compiler enforces it, and dropping the now-unreachable null branch in the fire job.

**Fix:** `public required LivenessProjection Liveness { get; set; }` (C# 11, available on net8.0) — removes `default!` and makes omission a compile error.

### IN-03: Hydration backoff loop's `delay` doubling can be skipped on the success path but never resets across transient cycles

**File:** `src/Orchestrator/Hydration/HydrationBackgroundService.cs:35,81`

**Issue:** Minor: `delay` only ever grows (capped at 30s). This is standard exponential backoff and fine for a one-shot startup hydration that returns on success. No action needed unless the loop is ever made to recover-and-continue rather than return — calling it out so a future edit doesn't reuse the field expecting a reset.

**Fix:** None required; documented for awareness.

### IN-04: `Program.cs` resolves the Quartz scheduler synchronously via `.GetAwaiter().GetResult()`

**File:** `src/Orchestrator/Program.cs:45-46`

**Issue:** The singleton factory blocks on `GetScheduler().GetAwaiter().GetResult()`. This runs once at DI resolution (first injection of `IScheduler`), not on a hot path, and Quartz's `GetScheduler` is effectively synchronous for the RAMJobStore, so deadlock risk is negligible in the Generic Host context. Flagged only because sync-over-async is a pattern worth avoiding; if it can be deferred to an async factory or resolved inside the hosted service start it would be cleaner.

**Fix:** Optional — consider resolving the scheduler inside `HydrationBackgroundService`/`WorkflowScheduler` lazily, or accept as-is given the one-time composition-root cost.

### IN-05: Tests are non-tautological and exercise real behavior — one observation

**File:** `tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs:276-279`, `tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs:52-59`

**Issue:** Verified the test suite genuinely drives production code (real `WorkflowL1Store`, real Quartz RAMJobStore, real `WorkflowLifecycle`, real MassTransit in-memory harness) — these are NOT assertion-free or tautological. One caveat: `FireDispatchTests.LivenessTimestampAdvancesOnFire_NoL2Write` asserts `DidNotReceive()` on a `db` substitute (lines 276-279) that is **never injected into the fire path** (the fire job takes no `IConnectionMultiplexer`). The assertion is therefore vacuously true regardless of fire-path behavior — it proves the structural fact "the fire job has no Redis dependency" only indirectly. The real zero-L2-write guarantee for the fire path comes from `WorkflowFireJob` having no Redis collaborator at all, which is better asserted by the type's constructor signature than by a `DidNotReceive` on an unrelated stub. The Stop test's `AssertZeroL2Writes` (lines 52-59) is meaningful because that `db` IS the one the lifecycle uses. No production bug; flagging the misleading assertion so it isn't mistaken for real coverage.

**Fix:** Either remove the vacuous `db` assertions from the fire test or replace with an assertion that the injected `ISendEndpointProvider` was used (which the test already does via the harness). Low priority.

---

_Reviewed: 2026-05-31_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
