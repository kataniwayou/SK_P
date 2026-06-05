# Phase 37: Orchestrator Pause/Resume Coordination - Pattern Map

**Mapped:** 2026-06-06
**Files analyzed:** 12 (6 CREATE, 6 MODIFY) + 4 new test files
**Analogs found:** 12 / 12 (every target has an in-repo analog)

All line numbers below are from the analog files as read on 2026-06-06. The planner/executor
should mirror the cited excerpts directly. **No source was modified by this mapping pass.**

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/PauseWorkflow.cs` (CREATE) | contract (record) | event/control message | `src/Messaging.Contracts/StartOrchestration.cs` | exact (role + flow) |
| `src/Messaging.Contracts/ResumeWorkflow.cs` (CREATE) | contract (record) | event/control message | `src/Messaging.Contracts/StartOrchestration.cs` | exact |
| `src/Orchestrator/Consumers/PauseWorkflowConsumer.cs` (CREATE) | consumer | request-response (control, ACK-on-return) | `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` | exact |
| `src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs` (CREATE) | consumer | request-response (control, ACK-on-return) | `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` + `StopOrchestrationConsumer.cs` | role-match (new resume logic) |
| `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs` (CREATE) | config (consumer definition) | endpoint/retry config | `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs` | exact (+ `ConcurrentMessageLimit=1`) |
| `src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs` (CREATE) | config (consumer definition) | endpoint/retry config | `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs` | exact (+ `ConcurrentMessageLimit=1`) |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` (MODIFY) | service (Quartz wrapper) | event-driven (scheduler) | self (existing `ScheduleAsync`/`UnscheduleAsync`) | in-place extension |
| `src/Orchestrator/Program.cs` (MODIFY) | config (composition root) | DI / endpoint registration | self (lines 40-43) | in-place extension |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` (MODIFY) | consumer (publish site) | event-driven (publish) | self + `OrchestrationService.cs:263` publish idiom | in-place extension |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` (MODIFY) | consumer (publish site) | event-driven (publish) | self + `OrchestrationService.cs:377` publish idiom | in-place extension |
| `src/Keeper/Recovery/L2ProbeRecovery.cs` (REFERENCE — likely NO CODE CHANGE) | service (probe loop) | batch/poll | self | publish lives in the two consumers, not here |
| `tests/BaseApi.Tests/...` (4 new test files) | test | unit (RAM Quartz + NSubstitute) | `SchedulingTests.cs`, `StopConsumerLifecycleTests.cs`, `OrchestratorTestStubs.cs` | exact harness reuse |

> **L2ProbeRecovery note:** RESEARCH §6 + CONTEXT D-04 place the `ResumeWorkflow` publish in the
> two Keeper *consumers* (after the `Recovered` branch's `Send`), NOT inside `L2ProbeRecovery.RunAsync`.
> `RunAsync` already returns `ProbeOutcome` (`L2ProbeRecovery.cs:19`); the consumers branch on it. So
> `L2ProbeRecovery.cs` is a **read-only reference** for the planner (it defines `ProbeOutcome`), not a
> modify target — unless the planner decides to thread publish through it (not recommended; the
> `workflowId` is only in the consumer's scope via `inner.WorkflowId`).

---

## Pattern Assignments

### `src/Messaging.Contracts/PauseWorkflow.cs` + `ResumeWorkflow.cs` (contract, control message)

**Analog:** `src/Messaging.Contracts/StartOrchestration.cs` (entire file, lines 1-7)

```csharp
// src/Messaging.Contracts/StartOrchestration.cs:1-7
namespace Messaging.Contracts;

/// <summary>Control message: start orchestration for the given workflows. Body-carries the correlation id (D-01).</summary>
public sealed record StartOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
```

`ICorrelated` is a single-member interface (`src/Messaging.Contracts/ICorrelated.cs:4-7`):
```csharp
public interface ICorrelated
{
    Guid CorrelationId { get; }
}
```

**Mirror-this (per D-01 — single `WorkflowId` + `H`, NOT an array, since Keeper publishes per-workflow):**
```csharp
// src/Messaging.Contracts/PauseWorkflow.cs
namespace Messaging.Contracts;

public sealed record PauseWorkflow(Guid WorkflowId, string H) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
// src/Messaging.Contracts/ResumeWorkflow.cs — byte-identical shape, name swapped
```

**Why `string H` is a ctor param, not init-only:** the source `H` lives on the concrete fault records
(`EntryStepDispatch.H` at `EntryStepDispatch.cs:17` — `public string H { get; init; } = "";`). It is
**NOT** declared on `IExecutionCorrelated` (`IExecutionCorrelated.cs:10-17` exposes only
`ExecutionId/WorkflowId/StepId/ProcessorId/EntryId`). Keeper reads `inner.H` because `inner` is the
*concrete* type (see Keeper section). For the new contracts, carry `H` as a positional record param
(D-02: correlation/observability only — never counted).

---

### `src/Orchestrator/Consumers/PauseWorkflowConsumer.cs` (consumer, control)

**Analog:** `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` (lines 23-38)

```csharp
// StopOrchestrationConsumer.cs:23-38 — primary-ctor DI, foreach over body, ACK on normal return
public sealed class StopOrchestrationConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<StopOrchestrationConsumer> logger) : IConsumer<StopOrchestration>
{
    public async Task Consume(ConsumeContext<StopOrchestration> context)
    {
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            logger.LogInformation("Stop drain for WorkflowId={WorkflowId}", workflowId);
            await lifecycle.UnscheduleOnlyAsync(workflowId, context.CancellationToken);
        }
        // returns normally -> ACK
    }
}
```

**Mirror-this:** primary-ctor DI of `WorkflowScheduler` (Pause does NOT need `WorkflowLifecycle` — it
calls the scheduler's new `PauseAsync` directly) + `ILogger<PauseWorkflowConsumer>`. The body carries a
single `WorkflowId` (no `foreach` — `PauseWorkflow` is per-workflow). Resolve the `jobId` from L1 the
way `UnscheduleOnlyAsync` does (`WorkflowLifecycle.cs:149-158` — `store.TryGet(workflowId, out var wf)`
then act on `wf.JobId`). Re-pausing an already-paused job is idempotent (D-07). Absent-from-L1 is a
**business no-op** (log + return → ACK), exactly like `UnscheduleOnlyAsync`'s guard:

```csharp
// WorkflowLifecycle.cs:149-158 — the "resolve jobId from L1, act, keep L1" idiom to model on
public async Task UnscheduleOnlyAsync(Guid workflowId, CancellationToken ct)
{
    if (!store.TryGet(workflowId, out var wf))
    {
        return; // BUSINESS no-op — nothing to unschedule.
    }
    await scheduler.UnscheduleAsync(wf.JobId, ct); // jobId-addressed — NO store.Remove (keep L1)
}
```

> **Design choice for the planner:** Pause can either (a) call a new `WorkflowScheduler.PauseAsync(jobId)`
> after resolving `wf.JobId` from L1 in the consumer, or (b) add a `PauseOnlyAsync(workflowId)` seam to
> `WorkflowLifecycle` mirroring `UnscheduleOnlyAsync`. Option (b) keeps the L1-lookup idiom in one place
> (`WorkflowLifecycle`) and matches the Stop consumer's shape exactly (consumer → lifecycle → scheduler).
> **Recommendation: (b)** — it makes `PauseWorkflowConsumer` a near-verbatim clone of `StopOrchestrationConsumer`.

**Logging discipline (security V5 / T-35-04):** `WorkflowId`/`H` are STRUCTURED template holes, never
interpolated — mirror `FaultEntryStepDispatchConsumer.cs:42-44` and `StopOrchestrationConsumer.cs:33`.

---

### `src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs` (consumer, resume logic)

**Analogs:** `StartOrchestrationConsumer.cs:24-41` (Teardown→reschedule idiom) + `WorkflowLifecycle.cs:149-158` (L1 lookup).

The Start consumer's delete-then-reschedule is the proven idiom RESEARCH §3 points the resume sequence at:
```csharp
// StartOrchestrationConsumer.cs:36-37 — Teardown (DeleteJob) THEN re-schedule fresh
await lifecycle.TeardownAsync(workflowId, context.CancellationToken);       // DeleteJob + remove L1
await lifecycle.HydrateAndScheduleAsync(workflowId, context.CancellationToken);
```

**Mirror-this, but lighter (RESEARCH §7 "Seam note" — do NOT call `HydrateAndScheduleAsync`, it re-reads
L2 + resets liveness).** The resume sequence (RESEARCH §3, 3-step):
```csharp
// 1. resolve jobId+cron from in-memory L1 (mirror UnscheduleOnlyAsync's lookup)
if (!store.TryGet(workflowId, out var wf)) return;        // business no-op (D-16 idiom)
// 2. read Quartz state; act ONLY if Paused (D-06; guard on == Paused, NOT != None — RESEARCH §4)
var state = await scheduler.GetTriggerStateAsync(wf.JobId, ct);  // NEW wrapper
if (state != TriggerState.Paused) return;                 // None=Stopped / Normal=Running -> ignore
// 3. delete the stale paused job + schedule a FRESH from-now trigger (recompute off wf.Cron)
await scheduler.UnscheduleAsync(wf.JobId, ct);            // existing DeleteJob (WorkflowScheduler.cs:76-77)
await scheduler.ScheduleAsync(workflowId, wf.JobId, wf.Cron, ct); // existing — fresh future StartAt
```

`wf.JobId` and `wf.Cron` come straight off the L1 record (`WorkflowL1.cs:17-21` — both are ctor params):
```csharp
public sealed record WorkflowL1(List<Guid> EntryStepIds, string Cron, Guid JobId, ...)
```

> **Critical (RESEARCH §3 alternative-rejected):** do NOT keep the paused job and merely re-trigger it —
> a fresh trigger added to a still-paused job inherits `Paused`. The `DeleteJob` + `ScheduleAsync` route
> produces a non-paused job. Resume must NOT reuse the `IWorkflowL1Store` `Wait(0)` drop-if-held stripe
> (`IWorkflowL1Store.cs:29` `TryAcquire`) — it would silently drop a contended Resume (D-07).

---

### `src/Orchestrator/Scheduling/WorkflowScheduler.cs` (MODIFY — the load-bearing change)

**This is the most important MODIFY.** Triggers are currently built with **NO identity**, so
`GetTriggerState` has no deterministic key to query. The change is to **both** existing builder sites
PLUS two new methods.

**Existing `JobKey` helper (line 19) — the `TriggerKey` is its 1:1 sibling:**
```csharp
// WorkflowScheduler.cs:19
private static JobKey KeyFor(Guid jobId) => new(jobId.ToString("D"));
```
Add (RESEARCH §1 — matches the `23-REVIEW.md:104` convention `new TriggerKey(jobId.ToString("D"))`):
```csharp
private static TriggerKey TriggerKeyFor(Guid jobId) => new(jobId.ToString("D"));
```

**Change site 1 — `ScheduleAsync` (lines 41-45):** the trigger builder has NO `.WithIdentity`:
```csharp
// WorkflowScheduler.cs:41-45 (CURRENT — random auto-key)
var trigger = TriggerBuilder.Create()
    .ForJob(jobKey)
    .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))
    .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
    .Build();
```
→ insert `.WithIdentity(TriggerKeyFor(jobId))` before `.ForJob(jobKey)`.

**Change site 2 — `RescheduleAsync` (lines 64-68)** (the self-reschedule path hit on EVERY fire from
`WorkflowFireJob.cs:103`). **MUST also be stamped** or the key reverts to random after the first fire
(RESEARCH §1 planner note):
```csharp
// WorkflowScheduler.cs:64-68 (CURRENT — random auto-key; the self-reschedule path)
var trigger = TriggerBuilder.Create()
    .ForJob(KeyFor(jobId))
    .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))
    .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
    .Build();
```
→ insert `.WithIdentity(TriggerKeyFor(jobId))` here too. (`RescheduleAsync` already only takes `jobId`,
so the key derives from the same arg.)

**New method `PauseAsync` — sibling of the existing `UnscheduleAsync` (lines 76-77):**
```csharp
// WorkflowScheduler.cs:76-77 — the existing one-liner to mirror
public Task UnscheduleAsync(Guid jobId, CancellationToken ct) =>
    scheduler.DeleteJob(KeyFor(jobId), ct);
```
→ add:
```csharp
public Task PauseAsync(Guid jobId, CancellationToken ct) =>
    scheduler.PauseJob(KeyFor(jobId), ct);     // pauses the job's current triggers (RESEARCH §2)
```

**New method `GetTriggerStateAsync`:**
```csharp
public Task<TriggerState> GetTriggerStateAsync(Guid jobId, CancellationToken ct) =>
    scheduler.GetTriggerState(TriggerKeyFor(jobId), ct);   // None for unknown key (RESEARCH §4 / A2)
```

> The existing `ScheduleAsync(workflowId, jobId, cron, ct)` (line 26) and `UnscheduleAsync` (line 76)
> are REUSED verbatim by the resume path — no signature change. The cron-skip guard
> (`if (next is not { } nextUtc) return;` lines 30-33 / 59-62) and `CronInterval.NextOccurrence`
> (line 29) carry over unchanged.

---

### `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs` + `ResumeWorkflowConsumerDefinition.cs` (config)

**Analog:** `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs` (lines 15-39)

```csharp
// StopOrchestrationConsumerDefinition.cs:15-39 — ctor sets EndpointName; ConfigureConsumer sets retry
public sealed class StopOrchestrationConsumerDefinition : ConsumerDefinition<StopOrchestrationConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public StopOrchestrationConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator";   // SHARED base name (both defs)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StopOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(_retryOptions.Value.Limit);
            r.Ignore<WorkflowRootNotFoundException>();
        });
    }
}
```

**Mirror-this, with the two D-07 deltas:**
1. Set `consumerConfigurator.ConcurrentMessageLimit = 1;` inside `ConfigureConsumer` (RESEARCH §5).
2. **Endpoint topology (RESEARCH §5 + Assumption A3 — Claude's discretion):** `UseMessageRetry` is
   *per-endpoint*. Start/Stop already own `UseMessageRetry` on `EndpointName = "orchestrator"`. If
   Pause/Resume colocate there, only ONE def may register retry. **Recommended (RESEARCH §5b):** give
   Pause+Resume their own shared endpoint name (e.g. `EndpointName = "orchestrator-pauseresume"`) so
   they own retry + `ConcurrentMessageLimit=1` independently and don't throttle Start/Stop/Result.
   They still get the per-replica fan-out via `.Endpoint(e => { e.InstanceId = ...; e.Temporary = true; })`
   in Program.cs (next section).

`WorkflowRootNotFoundException` ignore is Start/Stop-specific; the Pause/Resume defs don't need it
(no L2 hydration). They may drop the `.Ignore<>` line.

---

### `src/Orchestrator/Program.cs` (MODIFY — DI registration)

**Analog:** lines 35-50 (the existing Start/Stop fan-out registration).

```csharp
// Program.cs:35-43 — stable per-replica instanceId, shared by the fan-out consumers
var instanceId = builder.Configuration["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N");

builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x =>
    {
        x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<ResultConsumer, ResultConsumerDefinition>();
    });
```

**Mirror-this:** add two `AddConsumer<PauseWorkflowConsumer, PauseWorkflowConsumerDefinition>()...` and
`AddConsumer<ResumeWorkflowConsumer, ResumeWorkflowConsumerDefinition>()...` registrations inside the same
`x => { ... }` closure, each with the **same** `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })`
block (per-replica fan-out, NOT competing-consumer). `WorkflowScheduler` is already a registered singleton
(line 56), so the consumers' DI resolves with no new service registration.

---

### `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` + `FaultExecutionResultConsumer.cs` (MODIFY — publish sites)

**Analog for the consumer body:** itself (already read). **Analog for the publish call:**
`OrchestrationService.cs:263` / `377` (`IPublishEndpoint.Publish` of `Start/StopOrchestration`).

**Current `FaultEntryStepDispatchConsumer.Consume` (lines 34-65)** — the two insert points:
```csharp
// FaultEntryStepDispatchConsumer.cs:36 — inner is the CONCRETE EntryStepDispatch (so inner.H + inner.WorkflowId resolve)
var inner = context.Message.Message;   // Fault<EntryStepDispatch>.Message => EntryStepDispatch
...
// FaultEntryStepDispatchConsumer.cs:49 — <<< D-03: publish PauseWorkflow IMMEDIATELY BEFORE this line
var outcome = await recovery.RunAsync(inner.EntryId, inner.H, context.CancellationToken);
if (outcome == ProbeOutcome.Recovered)
{
    var endpoint = await context.GetSendEndpoint(new Uri($"queue:{inner.ProcessorId:D}"));
    await endpoint.Send(inner, context.CancellationToken);
    // <<< D-04: publish ResumeWorkflow IMMEDIATELY AFTER this Send (still inside the Recovered branch)
}
else
{
    var dlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));
    await dlq.Send(context.Message, context.CancellationToken);   // <<< D-09: publish NOTHING here
}
```

**Publish idiom to mirror — `OrchestrationService.cs:263-264` uses `IPublishEndpoint.Publish`; inside a
consumer use `context.Publish` (RESEARCH §6 — Publish, NOT Send, so message-type binding routes it to
the orchestrator's per-replica fan-out):**
```csharp
// OrchestrationService.cs:263-264 — the body-carried CorrelationId publish shape
await _publishEndpoint.Publish(
    new StartOrchestration(started.ToArray()) { CorrelationId = startCorr }, ct);
```

**Insert at intake (before line 49) — D-03:**
```csharp
await context.Publish(
    new PauseWorkflow(inner.WorkflowId, inner.H) { CorrelationId = inner.CorrelationId },
    context.CancellationToken);
```
**Insert after the Recovered `Send` (after line 56) — D-04:**
```csharp
await context.Publish(
    new ResumeWorkflow(inner.WorkflowId, inner.H) { CorrelationId = inner.CorrelationId },
    context.CancellationToken);
```

`inner.WorkflowId` is on `IExecutionCorrelated` (`IExecutionCorrelated.cs:13`); `inner.H` resolves
because `inner` is the concrete `EntryStepDispatch` (`EntryStepDispatch.cs:17`), not the interface.
Set `CorrelationId = inner.CorrelationId` for continuity (the consumer already opens a manual
`CorrelationId` scope from `inner.CorrelationId` at line 39). The `else`/`GaveUp` branch (lines 58-64)
is **unchanged** (D-09 — parks to `keeper-dlq`, publishes nothing).

**`FaultExecutionResultConsumer.cs` is the verbatim sibling:** same insert points — publish
`PauseWorkflow` before `recovery.RunAsync(...)` at **line 50**, publish `ResumeWorkflow` after the
`Send` at **line 57** (inside the `Recovered` branch, lines 51-58). The only structural difference is
the re-inject endpoint (`queue:{OrchestratorQueues.Result}` vs `queue:{inner.ProcessorId:D}`), which
does not affect the pause/resume inserts.

---

## Shared Patterns

### Idempotent control consume + serial limit (D-07)
**Source:** `StopOrchestrationConsumer.cs` (shape) + `StopOrchestrationConsumerDefinition.cs` (config).
**Apply to:** both new orchestrator consumers + their definitions.
- Consumer: primary-ctor DI, return normally → ACK, business no-op on absent-L1, only infra throws.
- Definition: `consumerConfigurator.ConcurrentMessageLimit = 1;` (NEW) + per-endpoint `UseMessageRetry`.
- A re-applied Quartz transition (PauseJob on a paused job; the resume guard on `!= Paused`) is a no-op
  on redelivery → crash-before-ack is safe (D-07).

### Body-carried correlation publish (`ICorrelated`)
**Source:** `OrchestrationService.cs:263-264` (`IPublishEndpoint.Publish` with `{ CorrelationId = ... }`).
**Apply to:** the two Keeper publish inserts (`context.Publish` form). The outbound correlation filter
stamps the envelope from the body for any `ICorrelated` message — set `CorrelationId` on the body only.

### Structured-param logging (security V5 / T-35-04)
**Source:** `FaultEntryStepDispatchConsumer.cs:42-44`, `StopOrchestrationConsumer.cs:33`.
**Apply to:** every new consumer log line — `{WorkflowId}` / `{H}` as template holes, never interpolated.

### Resolve jobId/cron from L1 (no L2 read on the hot path)
**Source:** `WorkflowLifecycle.UnscheduleOnlyAsync` (`WorkflowLifecycle.cs:149-158`) — `store.TryGet` then
act on `wf.JobId`; `WorkflowFireJob.cs:103` for `wf.Cron`.
**Apply to:** Pause (jobId) and Resume (jobId + cron). Do NOT call `HydrateAndScheduleAsync` from Resume
(RESEARCH §7 — it re-reads L2 + resets liveness).

---

## Test Pattern Assignments (4 new files — Wave 0, per 37-RESEARCH §Validation)

### RAM-scheduler harness
**Source:** `SchedulingTests.cs:16-29` (`NewRamSchedulerAsync` — unique `quartz.scheduler.instanceName =
test-{Guid:N}` per test to dodge the shared process-wide repository) and
`StopConsumerLifecycleTests.cs:29-41` (same, with `Start` + `Shutdown` in `try/finally`).
**Apply to:** `PauseResumeSchedulingTests.cs` (PAUSE-02/03/05) — real `StdSchedulerFactory` RAMJobStore.

```csharp
// SchedulingTests.cs:21-28 — the per-test isolation idiom
var props = new System.Collections.Specialized.NameValueCollection
{ ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}" };
var scheduler = await new StdSchedulerFactory(props).GetScheduler();
await scheduler.Start();
```

### State-assertion idiom (the new key the tests must assert)
**Source:** `SchedulingTests.cs:61-66` — currently queries via `GetTriggersOfJob` (indirect). The NEW
tests must additionally assert the **deterministic** key:
```csharp
// NEW assertion the stamped TriggerKey enables (RESEARCH §1 planner note):
var state = await scheduler.GetTriggerState(new TriggerKey(jobId.ToString("D")), ct);
Assert.Equal(TriggerState.Normal, state);   // after fresh schedule
// ... Paused after PauseAsync; None after UnscheduleAsync (Stopped)
```
`TestContext.Current.CancellationToken` MUST be passed to every Quartz call (xUnit1051 analyzer —
RESEARCH Validation table).

### Consumer-under-test harness (mocks + ACK)
**Source:** `OrchestratorTestStubs.Context<T>(message, ct)` (`OrchestratorTestStubs.cs:129-136`) builds a
`Substitute.For<ConsumeContext<T>>()`; `StopConsumerLifecycleTests.cs:43-53` (`Build(...)`) wires
`WorkflowL1Store` + `WorkflowScheduler` + `WorkflowLifecycle` + the consumer with `NullLogger`.
**Apply to:** `PauseResumeConsumerTests.cs` (PAUSE-04 idempotency) — invoke consumer `.Consume(...)`
twice serially over one scheduler, assert one `Normal` trigger + no orphans (`GetJobKeys` count == 1).

### Keeper publish-site test (mock `context.Publish`)
**Source:** `OrchestratorTestStubs.Context<T>` (substitute `ConsumeContext`); mock
`context.Publish<PauseWorkflow>(...)` and assert `Received(1)`. The Keeper E2E sibling for the live
round-trip is `KeeperRecoveryE2ETests.cs` (RealStack — induce fault, observe pause/resume; rebuild
keeper + orchestrator containers first per MEMORY close-gate caveats).
**Apply to:** `KeeperPausePublishTests.cs` (PAUSE-01 publish) + `PauseResumeContractTests.cs`
(assert the two records implement `ICorrelated` and carry `WorkflowId`/`H`/`CorrelationId`).

---

## No Analog Found

None. Every file in scope has a direct in-repo analog. The only "new logic" (the resume 3-step
delete-then-fresh-schedule and the `GetTriggerState` guard) is composed entirely from existing
primitives (`UnscheduleAsync`, `ScheduleAsync`, and the new thin `PauseAsync`/`GetTriggerStateAsync`
wrappers over `IScheduler.PauseJob`/`GetTriggerState`).

## Metadata

**Analog search scope:** `src/Messaging.Contracts/`, `src/Orchestrator/{Consumers,Scheduling,Hydration,L1}/`,
`src/Orchestrator/Program.cs`, `src/Keeper/{Consumers,Recovery}/`, `src/BaseApi.Service/Features/Orchestration/`,
`tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Keeper/`.
**Files read (analogs):** 19.
**Pattern extraction date:** 2026-06-06.

### Load-bearing facts the planner MUST carry forward
1. **Triggers have NO identity today** — `TriggerBuilder.Create()` at `WorkflowScheduler.cs:41` AND `:64`
   has no `.WithIdentity`. Both must be stamped with `new TriggerKey(jobId.ToString("D"))` or
   `GetTriggerState` returns `None`. This is the prerequisite, not optional.
2. **`H` is NOT on `IExecutionCorrelated`** — it lives on the concrete records (`EntryStepDispatch.cs:17`).
   Keeper's `inner.H` works because `inner` is concrete (`Fault<EntryStepDispatch>.Message`). The new
   contracts carry `H` as their own positional param.
3. **Publish, not Send** — Keeper uses `context.Publish(new PauseWorkflow(...))` (message-type fan-out),
   distinct from the existing re-inject `context.GetSendEndpoint(uri).Send(inner)`.
4. **Resume must DeleteJob then fresh ScheduleAsync** — never re-trigger the paused job (it stays paused),
   never call `HydrateAndScheduleAsync` (re-reads L2). Guard on `state == TriggerState.Paused` exactly.
5. **Separate fan-out endpoint recommended** for Pause/Resume (`"orchestrator-pauseresume"`) to avoid the
   per-endpoint `UseMessageRetry` ownership conflict with Start/Stop and to isolate `ConcurrentMessageLimit=1`.

## PATTERN MAPPING COMPLETE

**Phase:** 37 - Orchestrator Pause/Resume Coordination
**Files classified:** 12 + 4 test files
**Analogs found:** 12 / 12

### Coverage
- Files with exact analog: 9
- Files with role-match analog (composed from primitives): 3
- Files with no analog: 0

### Key Patterns Identified
- All orchestrator control consumers mirror `StopOrchestrationConsumer` (primary-ctor DI, ACK-on-return, business-no-op on absent-L1, infra-throw); their definitions add `ConcurrentMessageLimit=1`.
- Contracts mirror `StartOrchestration` exactly (`sealed record : ICorrelated`, body-carried `CorrelationId`); the two new ones carry `WorkflowId` + `H` per-workflow.
- The scheduler change is load-bearing: stamp a deterministic `TriggerKey(jobId.ToString("D"))` on BOTH `ScheduleAsync` and `RescheduleAsync` builder sites (both currently identity-less), then add thin `PauseAsync`/`GetTriggerStateAsync` wrappers.
- Keeper publishes via `context.Publish` (not Send) at intake (Pause) and after the Recovered re-inject (Resume), reusing the `IPublishEndpoint.Publish` body-correlation idiom from `OrchestrationService`.

### File Created
`.planning/phases/37-orchestrator-pause-resume-coordination/37-PATTERNS.md`

### Ready for Planning
Pattern mapping complete. Planner can reference each analog's exact line ranges in the per-plan action sections.
