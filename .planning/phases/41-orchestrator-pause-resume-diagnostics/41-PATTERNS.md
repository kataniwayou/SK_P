# Phase 41: Orchestrator Pause/Resume Diagnostics - Pattern Map

**Mapped:** 2026-06-07
**Files analyzed:** 3 (2 modified + 1 created)
**Analogs found:** 3 / 3 (all exact)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Orchestrator/Hydration/WorkflowLifecycle.cs` (MODIFY — `ResumeAsync` ignore branch) | service (lifecycle coordinator) | event-driven (Resume command → guard) | Same file — existing `logger.LogWarning(...)` calls within `WorkflowLifecycle` | exact (in-file convention) |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` (MODIFY — `RescheduleAsync` fallback) | service (Quartz scheduler wrapper) | event-driven (self-rescheduling job) | Same file — `ScheduleAsync` build block (lines 28-51) | exact (in-file analog) |
| `tests/BaseApi.Tests/Orchestrator/Scheduling/RescheduleSchedulingTests.cs` (CREATE — fallback test) | test | request-response (hermetic Quartz assert) | `PauseResumeSchedulingTests.cs` + `SchedulingTests.cs` | exact |

## Pattern Assignments

### `src/Orchestrator/Hydration/WorkflowLifecycle.cs` (service, event-driven) — WR-01 / D-01

**Analog:** same file — existing structured `ILogger<WorkflowLifecycle>` usage.

**Fix location:** the `state != TriggerState.Paused` ignore branch in `ResumeAsync` (lines 186-190). The
return stays; add an informational log *before* the `return` carrying `WorkflowId` + the observed `state`.

Current branch (lines 185-190):
```csharp
var state = await scheduler.GetTriggerStateAsync(wf.JobId, ct);
if (state != TriggerState.Paused)
{
    // None(Stopped)/Normal(Running)/Blocked/Error -> ignore (D-09).
    return;
}
```

**Logger is already injected** — no new dependency (constructor line 30: `ILogger<WorkflowLifecycle> logger`).

**Structured-logging convention to follow** (in-file, e.g. lines 45, 64, 99-100):
```csharp
logger.LogWarning("Workflow {WorkflowId} absent from L2 root — skipping hydration (business)", workflowId);
logger.LogWarning("Workflow {WorkflowId} has no cron — skipping hydration (business)", workflowId);
logger.LogWarning(
    "Step {StepId} of workflow {WorkflowId} absent from L2 — skipping step (business)", stepId, workflowId);
```

Convention rules extracted from these calls:
- Named placeholders in PascalCase: `{WorkflowId}`, `{StepId}` — never positional `{0}`.
- Message ends with a parenthetical classifier (`(business)`); a `(dropped)` / `(ignored)` style suffix fits the Resume-ignore semantics.
- Positional args follow the template in placeholder order.

**D-01 / D-03 constraints (locked by SC1, not discretionary):**
- Level MUST be `logger.LogInformation(...)` — NOT `LogWarning`/`LogDebug`.
- Structured fields MUST be `WorkflowId` (the `workflowId` param) AND the observed `state` (`TriggerState`).
- Prose wording is Claude's discretion; the two fields and the level are not.

**Target shape** (illustrative — exact prose is discretionary):
```csharp
var state = await scheduler.GetTriggerStateAsync(wf.JobId, ct);
if (state != TriggerState.Paused)
{
    // None(Stopped)/Normal(Running)/Blocked/Error -> ignore (D-09).
    logger.LogInformation(
        "Resume ignored for workflow {WorkflowId} — trigger state is {TriggerState}, not Paused (D-09)",
        workflowId, state);
    return;
}
```

---

### `src/Orchestrator/Scheduling/WorkflowScheduler.cs` (service, event-driven) — WR-02 / D-04

**Analog:** same file — `ScheduleAsync` (lines 28-51) is the exact build pattern the fallback must mirror.

**Build block to mirror** (`ScheduleAsync`, lines 37-50):
```csharp
var jobKey = KeyFor(jobId);
var job = JobBuilder.Create<WorkflowFireJob>()
    .WithIdentity(jobKey)
    .UsingJobData("workflowId", workflowId.ToString("D"))
    .Build();

var trigger = TriggerBuilder.Create()
    .WithIdentity(TriggerKeyFor(jobId))
    .ForJob(jobKey)
    .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))
    .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
    .Build();

await scheduler.ScheduleJob(job, trigger, ct);
```

**Fix location:** the `replaced is null` fallback in `RescheduleAsync` (lines 82-86):
```csharp
var replaced = await scheduler.RescheduleJob(triggerKey, trigger, ct);
if (replaced is null)
{
    await scheduler.ScheduleJob(trigger, ct);   // <-- WR-02: throws JobPersistenceException if the non-durable job was purged
}
```

**The defect:** the bare single-arg `ScheduleJob(trigger, ct)` assumes the non-durable `WorkflowFireJob` still
exists in the store. When the job has been purged (non-durable, no live trigger), Quartz throws
`JobPersistenceException`. D-04 requires re-creating the **full job+trigger** so the fallback re-establishes the
schedule and **cannot throw**.

**Signature note for the planner:** `RescheduleAsync(Guid jobId, ...)` does NOT currently receive `workflowId`,
but `ScheduleAsync`'s `JobBuilder` stamps `.UsingJobData("workflowId", workflowId.ToString("D"))` (line 40). The
re-created job MUST carry the same `workflowId` job-data so the resurrected `WorkflowFireJob` can still resolve its
workflow on fire. The current `RescheduleAsync` signature lacks `workflowId` — the planner must either thread
`workflowId` into the signature (and update the sole caller `WorkflowFireJob.Execute`) OR recover it from the
existing job detail / the firing trigger's data map. This is the one non-trivial decision in WR-02; do not drop
the `workflowId` job-data or the resurrected job fires without its workflow context.

**Helper-vs-duplicate (D-04 discretion):** the `JobBuilder` block may be extracted into a small private helper
(e.g. `BuildJob(jobId, workflowId)` / `BuildTrigger(jobId, nextUtc)`) shared with `ScheduleAsync`, or duplicated
inline. Planner's call on readability. Keep deterministic keys via the existing `KeyFor` / `TriggerKeyFor` helpers
(lines 19-21).

**Target shape** (illustrative):
```csharp
var replaced = await scheduler.RescheduleJob(triggerKey, trigger, ct);
if (replaced is null)
{
    // Non-durable job purged (no prior trigger) — re-create the full job, not just the trigger,
    // so the fallback re-establishes the schedule instead of throwing JobPersistenceException (WR-02).
    var job = JobBuilder.Create<WorkflowFireJob>()
        .WithIdentity(KeyFor(jobId))
        .UsingJobData("workflowId", workflowId.ToString("D"))
        .Build();
    await scheduler.ScheduleJob(job, trigger, ct);
}
```

---

### `tests/BaseApi.Tests/Orchestrator/Scheduling/RescheduleSchedulingTests.cs` (test) — WR-02 / D-06

**Analog:** `tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs` (closest — same directory,
same harness, exercises `WorkflowScheduler` against a real RAM scheduler). `SchedulingTests.cs` is the secondary
analog for the `GetJobKeys` / `GetTriggersOfJob` assertions.

File placement & naming are discretionary (D-06); `Scheduling/RescheduleSchedulingTests.cs` mirrors the existing
directory and the `*SchedulingTests` suffix.

**Hermetic RAM-scheduler harness to clone** (`PauseResumeSchedulingTests` lines 38-50):
```csharp
private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
{
    // Unique instance name — StdSchedulerFactory binds schedulers in a SHARED process-wide repository
    // keyed by instance name; the default name collides across parallel test classes. A fresh GUID
    // name isolates each test's RAMJobStore.
    var props = new System.Collections.Specialized.NameValueCollection
    {
        ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
    };
    var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
    await scheduler.Start(ct);
    return scheduler;
}
```

**SUT construction + ct convention** (lines 55-64): real scheduler, `TimeProvider.System`, every Quartz call
takes `TestContext.Current.CancellationToken` (xUnit1051):
```csharp
var ct = TestContext.Current.CancellationToken;
var scheduler = await NewRamSchedulerAsync(ct);
try
{
    var sut = new WorkflowScheduler(scheduler, TimeProvider.System);
    var workflowId = Guid.NewGuid();
    var jobId = Guid.NewGuid();
    var triggerKey = new TriggerKey(jobId.ToString("D"));
    // ... arrange / act / assert ...
}
finally
{
    await scheduler.Shutdown(waitForJobsToComplete: false, ct);
}
```

**Assert-schedule-re-established pattern** (adapted from `PauseResumeSchedulingTests` lines 102-108 — D-06 says
assert re-establishment, NOT a throw):
```csharp
var jobKey = new JobKey(jobId.ToString("D"));
var triggerKey = new TriggerKey(jobId.ToString("D"));
var triggers = await scheduler.GetTriggersOfJob(jobKey, ct);
var trigger = Assert.Single(triggers);
Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, ct));
Assert.NotNull(trigger.GetNextFireTimeUtc());
```

**Driving the `replaced is null` branch (the test's whole point — D-06):** `RescheduleJob` returns `null` only when
no prior trigger exists for the deterministic `triggerKey`. Two ways to set up the purged-job state:
1. Schedule, then `DeleteJob` (mirrors `ResumeIgnoresStoppedAndRunning` lines 128-130 / `UnscheduleAsync_RemovesJobAndTriggers`
   lines 87-92) so the non-durable job AND its trigger are gone — then call `RescheduleAsync(jobId, cron, ct)` directly.
2. Call `RescheduleAsync` on a never-scheduled `jobId` (no job, no trigger) — the cleanest expression of "purged / no prior trigger".

After the call, assert a live trigger exists for the deterministic key (Single, `TriggerState.Normal`, future
next-fire) — i.e. the fallback **re-established** the schedule rather than threw.

**Constant convention** (line 36): `private const string EveryFiveMinutes = "*/5 * * * *";` — reuse the same cron literal.

**Imports block to copy** (`PauseResumeSchedulingTests` lines 1-10):
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace BaseApi.Tests.Orchestrator.Scheduling;
```

---

## Shared Patterns

### Structured Logging
**Source:** `src/Orchestrator/Hydration/WorkflowLifecycle.cs` lines 45, 64, 99-100
**Apply to:** the new WR-01 log in `WorkflowLifecycle.ResumeAsync`
- PascalCase named placeholders (`{WorkflowId}`, `{TriggerState}`), never positional.
- Trailing parenthetical classifier on the message.
- WR-01 deviates on level only: `LogInformation` (D-03), not `LogWarning`.

### Deterministic Quartz Keying
**Source:** `src/Orchestrator/Scheduling/WorkflowScheduler.cs` lines 19-21 (`KeyFor` / `TriggerKeyFor`)
**Apply to:** the WR-02 fallback (re-created job uses `KeyFor(jobId)`) and the new test
(`new JobKey(jobId.ToString("D"))` / `new TriggerKey(jobId.ToString("D"))`).
```csharp
private static JobKey KeyFor(Guid jobId) => new(jobId.ToString("D"));
private static TriggerKey TriggerKeyFor(Guid jobId) => new(jobId.ToString("D"));
```
Both fixes and the test address by `jobId.ToString("D")` — the single load-bearing key contract.

### Hermetic RAM-Scheduler Harness
**Source:** `tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs` lines 38-50
**Apply to:** the new fallback test
- Unique `quartz.scheduler.instanceName = test-{Guid:N}` per scheduler (parallel-class isolation).
- `new WorkflowScheduler(scheduler, TimeProvider.System)`.
- `TestContext.Current.CancellationToken` on every Quartz call (xUnit1051).
- `try { ... } finally { await scheduler.Shutdown(false, ct); }`.

## No Analog Found

None. All three files map to exact in-codebase analogs (two in-file, one in the sibling test class).

## Metadata

**Analog search scope:** `src/Orchestrator/Hydration/`, `src/Orchestrator/Scheduling/`,
`tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Orchestrator/Scheduling/`
**Files scanned:** 4 (WorkflowLifecycle.cs, WorkflowScheduler.cs, PauseResumeSchedulingTests.cs, SchedulingTests.cs)
**Pattern extraction date:** 2026-06-07
